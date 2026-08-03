// dcu 稳定 C ABI 的实现，架在 libdatachannel 的 **C++ API** 之上（决策 #42 / SPEC §2）。
//
// 本文件不再包含 <rtc/rtc.h>：句柄由 dcu 自己的 DcuHandleTable 分配与验活，
// 而不是 src/capi.cpp 里那张够不着的私有 map。
//
// 本次迁移是**实现变更，不是 ABI 变更** —— dcu.h 与整个 C# 侧一字未动，
// DCU_ABI_VERSION 仍为 1，导出仍为 18 个。事件 ABI 的重写、错误码独立编号、
// 拉模式消息、日志桥等属于 SPEC §14 的第 2–4 步，不在本次范围内。
//
// 三条**违反了不会报错**的不变量（SPEC §2），改动本文件前请先读：
//
//   1. 入向 DataChannel 的 onOpen 必须在 onDataChannel 回调体内**同步** wire。
//      上游用调用顺序实现「注册后重放」（triggerDataChannel 先 resetOpenCallback，
//      再 dataChannelCallback，最后 triggerOpen）。若把 wire 挪到 lambda 之外或
//      延后到登记之后，入向通道的 DC_OPEN 会全部丢失，且编译、门禁、单向测试全绿。
//   2. 收消息若改拉模式，必须沿用 peek() / receive() 这一对（拷贝成功才丢弃）。
//      直接 receive() 再拷贝会在调用方缓冲不足时丢消息 —— reliable 通道上即协议违约。
//   3. 出向补发 DC_OPEN（#32 决议 2，尚未实现）只能加在出向创建路径；
//      入向路径此刻 mIsOpen 已为 true 而 mOpenTriggered 刚被重置，补查会倒转事件顺序。

#include "dcu.h"
#include "dcu_handles.hpp"
#include "dcu_queue.hpp"

#include <atomic>
#include <chrono>
#include <cstring>
#include <memory>
#include <stdexcept>
#include <string>
#include <variant>
#include <vector>

#include <rtc/rtc.hpp>

namespace {

std::atomic<bool> g_inited{false};
DcuEventQueue g_queue;
DcuHandleTable g_table;

// ---------------------------------------------------------------------------
// 异常边界
//
// 上游 capi 的 wrap() 少一个 catch(...)，且把 what() 压进 plog 就丢了。这里补上
// catch(...)；错误码沿用迁移前的值域，使托管侧观察不到差异：
//   std::invalid_argument -> DCU_ERR_INVALID (-1)   （= RTC_ERR_INVALID）
//   其余异常              -> DCU_ERR_FAILURE (-2)   （= RTC_ERR_FAILURE）
//
// 已知的暂时性缺口：capi 的 wrap() 会把 e.what() 打进 plog（走 stdout），本层
// 目前没有可用的日志出口，故异常文本被丢弃。由 SPEC §14 第 4 步的日志桥（#33）补回。
// ---------------------------------------------------------------------------

template <typename F> int dcu_wrap(F &&f) {
    try {
        return f();
    } catch (const std::invalid_argument &) {
        return DCU_ERR_INVALID;
    } catch (const std::exception &) {
        return DCU_ERR_FAILURE;
    } catch (...) {
        return DCU_ERR_FAILURE;
    }
}

// 迁移前这些函数一律是 `rc == 0 ? DCU_OK : DCU_ERR_FAILURE`，把 INVALID 压平成
// FAILURE。保真化归 #31（SPEC §4），本次不改，以免行为漂移。
template <typename F> int dcu_wrap_flat(F &&f) {
    return dcu_wrap(std::forward<F>(f)) == DCU_OK ? DCU_OK : DCU_ERR_FAILURE;
}

// ---------------------------------------------------------------------------
// 状态枚举映射
//
// 显式 switch 而非强转（#31 决议 3）。此处**故意不写 default 标签**：枚举成员
// 全覆盖时，上游若新增成员，编译器的 -Wswitch 会报出来 —— 这替回了一部分
// static_assert 在 C++ 路线上失去的编译期信号（#42 已知退步）。
// 越界值经落尾的强转原样带出，与迁移前一致；映射到 Unknown 归 #31/#34。
// ---------------------------------------------------------------------------

int map_pc_state(rtc::PeerConnection::State s) {
    switch (s) {
    case rtc::PeerConnection::State::New:
        return 0;
    case rtc::PeerConnection::State::Connecting:
        return 1;
    case rtc::PeerConnection::State::Connected:
        return 2;
    case rtc::PeerConnection::State::Disconnected:
        return 3;
    case rtc::PeerConnection::State::Failed:
        return 4;
    case rtc::PeerConnection::State::Closed:
        return 5;
    }
    return static_cast<int>(s);
}

int map_gathering_state(rtc::PeerConnection::GatheringState s) {
    switch (s) {
    case rtc::PeerConnection::GatheringState::New:
        return 0;
    case rtc::PeerConnection::GatheringState::InProgress:
        return 1;
    case rtc::PeerConnection::GatheringState::Complete:
        return 2;
    }
    return static_cast<int>(s);
}

rtc::LogLevel map_log_level(int level) {
    if (level <= 0)
        return rtc::LogLevel::None;
    switch (level) {
    case 1:
        return rtc::LogLevel::Fatal;
    case 2:
        return rtc::LogLevel::Error;
    case 3:
        return rtc::LogLevel::Warning;
    case 4:
        return rtc::LogLevel::Info;
    case 5:
        return rtc::LogLevel::Debug;
    default:
        return rtc::LogLevel::Verbose;
    }
}

void push_event(DcuEvent ev) { g_queue.push(std::move(ev)); }

std::vector<uint8_t> bytes_from_string(const std::string &s) {
    return std::vector<uint8_t>(s.begin(), s.end());
}

// 回调只捕获 int 句柄，绝不捕获 shared_ptr —— 后者会让对象经由自己的回调持有
// 自己，形成永不释放的环。
void wire_dc_callbacks(int h, const std::shared_ptr<rtc::DataChannel> &dc) {
    dc->onOpen([h] {
        DcuEvent ev;
        ev.type = DCU_EVENT_DC_OPEN;
        ev.dc = h;
        push_event(std::move(ev));
    });

    dc->onClosed([h] {
        DcuEvent ev;
        ev.type = DCU_EVENT_DC_CLOSED;
        ev.dc = h;
        push_event(std::move(ev));
    });

    dc->onError([h](std::string error) {
        DcuEvent ev;
        ev.type = DCU_EVENT_DC_ERROR;
        ev.dc = h;
        ev.payload = bytes_from_string(error);
        push_event(std::move(ev));
    });

    // 推模式，与迁移前一致。改为拉模式（#30）是 SPEC §14 第 2 步，届时见不变量 2。
    dc->onMessage([h](rtc::message_variant data) {
        DcuEvent ev;
        ev.type = DCU_EVENT_DC_MESSAGE;
        ev.dc = h;
        if (std::holds_alternative<rtc::binary>(data)) {
            const auto &b = std::get<rtc::binary>(data);
            const auto *p = reinterpret_cast<const uint8_t *>(b.data());
            ev.payload.assign(p, p + b.size());
        } else {
            // 文本帧：透明转 UTF-8 字节。variant 自带长度，内嵌 NUL 不再被截断
            // —— 迁移前走 strlen，这是 #32 D7 记录的真 bug，被本次迁移结构性消掉。
            ev.payload = bytes_from_string(std::get<rtc::string>(data));
        }
        push_event(std::move(ev));
    });
}

void wire_pc_callbacks(int h, const std::shared_ptr<rtc::PeerConnection> &pc) {
    pc->onLocalDescription([h](rtc::Description desc) {
        DcuEvent ev;
        ev.type = DCU_EVENT_LOCAL_DESCRIPTION;
        ev.pc = h;
        ev.payload = bytes_from_string(std::string(desc));
        ev.payload2 = bytes_from_string(desc.typeString());
        push_event(std::move(ev));
    });

    pc->onLocalCandidate([h](rtc::Candidate cand) {
        DcuEvent ev;
        ev.type = DCU_EVENT_LOCAL_CANDIDATE;
        ev.pc = h;
        ev.payload = bytes_from_string(std::string(cand));
        ev.payload2 = bytes_from_string(cand.mid());
        push_event(std::move(ev));
    });

    pc->onStateChange([h](rtc::PeerConnection::State s) {
        DcuEvent ev;
        ev.type = DCU_EVENT_CONNECTION_STATE;
        ev.pc = h;
        ev.state = map_pc_state(s);
        push_event(std::move(ev));
    });

    pc->onGatheringStateChange([h](rtc::PeerConnection::GatheringState s) {
        DcuEvent ev;
        ev.type = DCU_EVENT_GATHERING_STATE;
        ev.pc = h;
        ev.state = map_gathering_state(s);
        push_event(std::move(ev));
    });

    // 不变量 1：登记 + wire + push 全部在本回调体内同步完成。
    pc->onDataChannel([h](std::shared_ptr<rtc::DataChannel> dc) {
        int dh = g_table.add_dc(dc);
        wire_dc_callbacks(dh, dc);

        DcuEvent ev;
        ev.type = DCU_EVENT_INCOMING_DATA_CHANNEL;
        ev.pc = h;
        ev.dc = dh;
        // label() 返回 std::string —— 迁移前的 char[256] + 静默截断整条路径消失（#32 D8）。
        ev.payload = bytes_from_string(dc->label());
        push_event(std::move(ev));
    });
}

} // namespace

extern "C" {

int dcu_abi_version(void) { return DCU_ABI_VERSION; }

int dcu_init(void) {
    if (g_inited.exchange(true))
        return DCU_OK;
    dcu_wrap([] {
        rtc::InitLogger(rtc::LogLevel::Warning);
        rtc::Preload();
        return DCU_OK;
    });
    return DCU_OK;
}

int dcu_shutdown(void) {
    if (!g_inited.exchange(false))
        return DCU_OK;
    // 顺序对应上游 rtcCleanup：先丢对象（eraseAll），再 Cleanup。队列在对象销毁
    // **之后**清，这样销毁期间回调推进来的事件也一并清掉。
    dcu_wrap([] {
        g_table.clear();
        g_queue.clear();
        if (rtc::Cleanup().wait_for(std::chrono::seconds(10)) == std::future_status::timeout)
            throw std::runtime_error("Cleanup timeout (possible deadlock or undestructible object)");
        return DCU_OK;
    });
    // 迁移前 rtcCleanup() 返回 void 并自行吞掉异常，dcu_shutdown 恒返回 DCU_OK。
    // 改为返回未销毁对象数归 #37 决议 7（SPEC §4），本次不改。
    return DCU_OK;
}

int dcu_set_log_level(int level) {
    dcu_wrap([level] {
        // 注意：上游 InitLogger 不幂等，回调传空即静默拆桥回落 stdout。目前我们本来
        // 就没装桥，故与迁移前一致；静态 trampoline 归 #33（SPEC §7）。
        rtc::InitLogger(map_log_level(level));
        return DCU_OK;
    });
    return DCU_OK;
}

int dcu_pc_create(const dcu_pc_config *config) {
    if (!g_inited.load())
        return DCU_ERR_FAILURE;
    if (!config)
        return DCU_ERR_INVALID;

    return dcu_wrap([config] {
        rtc::Configuration cfg;

        // 凭证走结构化字段，不再拼 URI（#33 决议 3 / SPEC §5）。
        // rtc::IceServer 的字段全 public，URL 构造函数自己 url_decode userinfo，
        // 我们不需要解析任何东西 —— percent_encode_userinfo / build_ice_uri 连同
        // 它们的三个真 bug（无 :// 时猜 scheme、rest 遇 @ 整串放弃、stun: 也被塞凭证）一并删除。
        if (config->ice_servers && config->ice_server_count > 0) {
            for (int i = 0; i < config->ice_server_count; ++i) {
                const dcu_ice_server &s = config->ice_servers[i];
                if (!s.urls || s.url_count <= 0)
                    continue;
                for (int u = 0; u < s.url_count; ++u) {
                    if (!s.urls[u])
                        continue;
                    rtc::IceServer ice{std::string(s.urls[u])};
                    if (s.username && s.username[0])
                        ice.username = s.username;
                    if (s.credential && s.credential[0])
                        ice.password = s.credential;
                    cfg.iceServers.push_back(std::move(ice));
                }
            }
        }

        // 以下逐条对应 capi.cpp:394-425 的翻译，保持行为一致。
        if (config->bind_address)
            cfg.bindAddress = std::string(config->bind_address);

        if (config->port_range_begin > 0 || config->port_range_end > 0) {
            cfg.portRangeBegin = config->port_range_begin;
            cfg.portRangeEnd = config->port_range_end;
        }

        cfg.iceTransportPolicy = config->transport_policy == 1 ? rtc::TransportPolicy::Relay
                                                              : rtc::TransportPolicy::All;
        cfg.enableIceTcp = config->enable_ice_tcp != 0;
        cfg.enableIceUdpMux = config->enable_ice_udp_mux != 0;
        cfg.disableAutoNegotiation = false;

        // SPEC §5 定的是「<= 0 即自动」。capi 对 maxMessageSize 用的是 != 0，
        // 对 C# 能产出的取值（0 或正数）两者等价。
        if (config->mtu > 0)
            cfg.mtu = static_cast<size_t>(config->mtu);
        if (config->max_message_size > 0)
            cfg.maxMessageSize = static_cast<size_t>(config->max_message_size);

        auto pc = std::make_shared<rtc::PeerConnection>(std::move(cfg));
        int h = g_table.add_pc(pc);
        wire_pc_callbacks(h, pc);
        return h;
    });
}

int dcu_pc_close(int pc) {
    if (pc <= 0)
        return DCU_ERR_INVALID;
    return dcu_wrap_flat([pc] {
        g_table.get_pc(pc)->close();
        return DCU_OK;
    });
}

int dcu_pc_destroy(int pc) {
    if (pc <= 0)
        return DCU_ERR_INVALID;
    return dcu_wrap_flat([pc] {
        // 与上游 rtcDeletePeerConnection 同形：先 close 再摘表。
        // 摘表**只摘 PC**，其子 DataChannel 仍留在表里 —— 与迁移前一致（级联释放
        // 由托管侧负责，见 #29 / SPEC §6）。
        auto p = g_table.get_pc(pc);
        p->close();
        p.reset();
        g_table.erase_pc(pc);
        return DCU_OK;
    });
}

int dcu_pc_set_remote_description(int pc, const char *sdp, int sdp_len, const char *type,
                                  int type_len) {
    if (pc <= 0 || !sdp || sdp_len < 0)
        return DCU_ERR_INVALID;
    std::string s = string_from_buf(sdp, sdp_len);
    std::string t = string_from_buf(type, type_len);
    return dcu_wrap_flat([pc, &s, &t] {
        g_table.get_pc(pc)->setRemoteDescription({s, t});
        return DCU_OK;
    });
}

int dcu_pc_add_remote_candidate(int pc, const char *cand, int cand_len, const char *mid,
                                int mid_len) {
    if (pc <= 0 || !cand || cand_len < 0)
        return DCU_ERR_INVALID;
    std::string c = string_from_buf(cand, cand_len);
    std::string m = string_from_buf(mid, mid_len);
    return dcu_wrap_flat([pc, &c, &m] {
        g_table.get_pc(pc)->addRemoteCandidate({c, m});
        return DCU_OK;
    });
}

int dcu_pc_create_data_channel(int pc, const char *label, int label_len, const dcu_dc_init *init) {
    if (pc <= 0 || !label || label_len < 0)
        return DCU_ERR_INVALID;
    std::string lab = string_from_buf(label, label_len);

    return dcu_wrap([pc, &lab, init] {
        rtc::DataChannelInit dci;
        if (init) {
            // 逐条对应 capi.cpp 的 rtcCreateDataChannelEx：unordered 直传；
            // 仅当 unreliable 时，lifetime > 0 走 lifetime，否则走 retransmits。
            dci.reliability.unordered = init->ordered == 0;
            if (init->reliable == 0) {
                if (init->max_packet_lifetime > 0)
                    dci.reliability.maxPacketLifeTime.emplace(
                        std::chrono::milliseconds(init->max_packet_lifetime));
                else
                    dci.reliability.maxRetransmits.emplace(init->max_retransmits);
            }
        }

        auto dc = g_table.get_pc(pc)->createDataChannel(lab, std::move(dci));
        int h = g_table.add_dc(dc);
        // 出向路径：wire 与创建之间存在 open 竞态窗口（#32 T1），与迁移前同样大。
        // 补发是 SPEC §14 第 2 步，见文件头不变量 3。
        wire_dc_callbacks(h, dc);
        return h;
    });
}

int dcu_dc_send(int dc, const char *data, int len) {
    if (dc <= 0 || !data || len < 0)
        return DCU_ERR_INVALID;
    return dcu_wrap_flat([dc, data, len] {
        const auto *b = reinterpret_cast<const rtc::byte *>(data);
        // send() 返回 false 仅表示「已缓冲」，迁移前的 rtcSendMessage 同样忽略它。
        g_table.get_dc(dc)->send(rtc::binary(b, b + len));
        return DCU_OK;
    });
}

int dcu_dc_close(int dc) {
    if (dc <= 0)
        return DCU_ERR_INVALID;
    return dcu_wrap_flat([dc] {
        g_table.get_dc(dc)->close();
        return DCU_OK;
    });
}

int dcu_dc_destroy(int dc) {
    if (dc <= 0)
        return DCU_ERR_INVALID;
    return dcu_wrap_flat([dc] {
        auto d = g_table.get_dc(dc);
        d->close();
        d.reset();
        g_table.erase_dc(dc);
        return DCU_OK;
    });
}

int dcu_dc_buffered_amount(int dc) {
    if (dc <= 0)
        return DCU_ERR_INVALID;
    int n = dcu_wrap([dc] { return static_cast<int>(g_table.get_dc(dc)->bufferedAmount()); });
    return n < 0 ? DCU_ERR_FAILURE : n;
}

int dcu_event_peek(dcu_event_header *out_header) {
    if (!out_header)
        return DCU_ERR_INVALID;
    if (!g_queue.peek(out_header))
        return DCU_ERR_NOT_AVAIL;
    return DCU_OK;
}

int dcu_event_copy_payload(char *buffer, int capacity) {
    return g_queue.copy_payload(buffer, capacity, false);
}

int dcu_event_copy_payload2(char *buffer, int capacity) {
    return g_queue.copy_payload(buffer, capacity, true);
}

int dcu_event_pop(void) { return g_queue.pop(); }

} // extern "C"
