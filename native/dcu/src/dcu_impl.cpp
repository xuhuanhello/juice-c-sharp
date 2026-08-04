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
#include "dcu_log_queue.hpp"
#include "dcu_queue.hpp"

#include <atomic>
#include <chrono>
#include <cstring>
#include <thread>
#include <memory>
#include <stdexcept>
#include <string>
#include <variant>
#include <vector>

#include <rtc/rtc.hpp>

namespace {

std::atomic<bool> g_inited{false};
DcuEventQueue g_queue;
DcuLogQueue g_log_queue;
DcuHandleTable g_table;
std::atomic<int> g_open_race_delay_ms{0}; // 仅契约测试用，见 dcu.h

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

// 唯一的日志出口。上游在**持锁**状态下调它，进程内所有线程的每条日志都串行经过
// 这里 —— 只入队，绝不阻塞、绝不进托管。
void log_trampoline(rtc::LogLevel level, std::string message) {
    g_log_queue.push(static_cast<int>(level), std::move(message));
}

void log_error(const char *what) {
    g_log_queue.push(static_cast<int>(rtc::LogLevel::Error), std::string(what ? what : "?"));
}

template <typename F> int dcu_wrap(F &&f) {
    try {
        return f();
    } catch (const std::invalid_argument &e) {
        // 迁移时这里把 e.what() 丢掉了（上游 wrap 会打进 plog，我们当时没有日志出口）。
        // 桥就位，补回来 —— 错误码告诉你是哪一类，文本告诉你是哪一个。
        log_error(e.what());
        return DCU_ERR_INVALID;
    } catch (const std::exception &e) {
        log_error(e.what());
        return DCU_ERR_FAILURE;
    } catch (...) {
        log_error("unclassifiable non-std exception");
        // 无法归类的失败。**绝不压平成 FAILURE** —— 压平丢掉的恰是最有诊断价值的
        // 那一位：INVALID（你传的参数不对，可自助修复）被伪装成 FAILURE（运行时
        // 问题，只能提 issue）。见 #31 决议 5。
        return DCU_ERR_UPSTREAM_UNKNOWN;
    }
}

// ---------------------------------------------------------------------------
// 状态枚举映射
//
// 显式 switch 而非强转（#31 决议 3）。此处**故意不写 default 标签**：枚举成员
// 全覆盖时，上游若新增成员，编译器的 -Wswitch 会报出来 —— 这替回了一部分
// static_assert 在 C++ 路线上失去的编译期信号（#42 已知退步）。
// 越界值映射到 DCU_STATE_UNKNOWN：不抛（default 绝不抛）、不丢事件（应用会停在
// 旧状态）、也不冒充某个既有成员（那是撒谎）。
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
    return DCU_STATE_UNKNOWN;
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
    return DCU_STATE_UNKNOWN;
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
void wire_dc_callbacks(int h, const std::shared_ptr<rtc::DataChannel> &dc,
                       const std::shared_ptr<std::atomic<bool>> &openReported) {
    dc->onOpen([h, openReported] {
        // 与出向补发共用一个去重标志：先到者投递，另一条路径静默跳过。
        if (openReported->exchange(true))
            return;
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

    // **刻意不设 onMessage。** 上游 Channel::flushPendingMessages 是
    // `while (messageCallback)` —— 不设回调，消息就留在它自己的 mRecvQueue 里，
    // 由 dcu_dc_receive 拉取。这是真背压的前提（见 dcu.h「控制推、数据拉」）。
    // 加回这个回调会**静默**摧毁背压闭环：消息会立刻被推进无界的控制队列。
}

// 出向专用：wire 完之后查一次 isOpen() 补发。**入向路径绝不能加这个** ——
// 此刻 mIsOpen 已为 true（processOpenMessage 末尾设的）而 mOpenTriggered 刚被
// resetOpenCallback 清掉，补查会立刻返回 true，在 INCOMING_DATA_CHANNEL 之前
// 先推出 DC_OPEN，把事件顺序倒过来。
void resend_open_if_already_open(int h, const std::shared_ptr<rtc::DataChannel> &dc,
                                 const std::shared_ptr<std::atomic<bool>> &openReported) {
    // isOpen() 是 !mIsClosed && mIsOpen。窗口内 open 完又 close 的通道这里为 false，
    // 于是只投 DC_CLOSED 不投 DC_OPEN —— 这不是取舍，是被事实逼的：C++ 公开面
    // 不暴露 mIsOpen，无法区分「open 过又关了」与「从没 open 过」，补 OPEN 等于伪造。
    if (!dc->isOpen())
        return;
    if (openReported->exchange(true))
        return;
    DcuEvent ev;
    ev.type = DCU_EVENT_DC_OPEN;
    ev.dc = h;
    push_event(std::move(ev));
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
        wire_dc_callbacks(dh, dc, std::make_shared<std::atomic<bool>>(false));
        // 此处**不做** resend_open_if_already_open，理由见该函数注释。

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

int dcu_abi_version(int *out_version) {
    if (!out_version)
        return DCU_ERR_INVALID;
    *out_version = DCU_ABI_VERSION;
    return DCU_OK;
}

int dcu_init(void) {
    if (g_inited.exchange(true))
        return DCU_OK;
    return dcu_wrap([] {
        rtc::InitLogger(rtc::LogLevel::Warning, log_trampoline);
        rtc::Preload();
        return DCU_OK;
    });
}

int dcu_shutdown(void) {
    if (!g_inited.exchange(false))
        return DCU_OK;
    // 顺序对应上游 rtcCleanup：先丢对象（eraseAll），再 Cleanup。队列在对象销毁
    // **之后**清，这样销毁期间回调推进来的事件也一并清掉。
    return dcu_wrap([] {
        g_table.clear();
        g_queue.clear();
        g_log_queue.clear();
        if (rtc::Cleanup().wait_for(std::chrono::seconds(10)) == std::future_status::timeout)
            throw std::runtime_error("Cleanup timeout (possible deadlock or undestructible object)");
        return DCU_OK;
    });
    // 返回未销毁对象数（经 out 参数）归 #37 决议 7，随 S8 落地。
}

int dcu_set_log_level(int level) {
    return dcu_wrap([level] {
        // **始终**把同一个 trampoline 传下去。传 nullptr 会静默拆桥并回落 stdout，
        // 那个参数因此不暴露给调用方（见 dcu.h）。
        rtc::InitLogger(map_log_level(level), log_trampoline);
        return DCU_OK;
    });
}

int dcu_pc_create(const dcu_pc_config *config, int *out_pc) {
    if (!out_pc)
        return DCU_ERR_INVALID;
    *out_pc = 0;
    if (!g_inited.load())
        return DCU_ERR_FAILURE;
    if (!config)
        return DCU_ERR_INVALID;

    return dcu_wrap([config, out_pc] {
        rtc::Configuration cfg;

        // 凭证走结构化字段，不再拼 URI（#33 决议 3 / SPEC §5）。
        // rtc::IceServer 的字段全 public，URL 构造函数自己 url_decode userinfo，
        // 我们不需要解析任何东西。
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

        // SPEC §5 定的是「<= 0 即自动」。
        if (config->mtu > 0)
            cfg.mtu = static_cast<size_t>(config->mtu);
        if (config->max_message_size > 0)
            cfg.maxMessageSize = static_cast<size_t>(config->max_message_size);

        auto pc = std::make_shared<rtc::PeerConnection>(std::move(cfg));
        int h = g_table.add_pc(pc);
        wire_pc_callbacks(h, pc);
        *out_pc = h;
        return DCU_OK;
    });
}

int dcu_pc_close(int pc) {
    if (pc <= 0)
        return DCU_ERR_INVALID;
    return dcu_wrap([pc] {
        g_table.get_pc(pc)->close();
        return DCU_OK;
    });
}

int dcu_pc_destroy(int pc) {
    if (pc <= 0)
        return DCU_ERR_INVALID;
    return dcu_wrap([pc] {
        // 与上游 rtcDeletePeerConnection 同形：先 close 再摘表。
        // 摘表**只摘 PC**，其子 DataChannel 仍留在表里 —— 级联释放由托管侧负责
        // （#29 / SPEC §6），随 S6 落地。
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
    return dcu_wrap([pc, &s, &t] {
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
    return dcu_wrap([pc, &c, &m] {
        g_table.get_pc(pc)->addRemoteCandidate({c, m});
        return DCU_OK;
    });
}

int dcu_pc_create_data_channel(int pc, const char *label, int label_len, const dcu_dc_init *init,
                               int *out_dc) {
    if (!out_dc)
        return DCU_ERR_INVALID;
    *out_dc = 0;
    if (pc <= 0 || !label || label_len < 0)
        return DCU_ERR_INVALID;
    // 超界 label 在「连接前创建」这条路径上是**静默失败**（见 dcu.h 的
    // DCU_LABEL_MAX_BYTES 注释）。两层都校验，这里是第二层。
    if (label_len > DCU_LABEL_MAX_BYTES)
        return DCU_ERR_INVALID;
    std::string lab = string_from_buf(label, label_len);

    return dcu_wrap([pc, &lab, init, out_dc] {
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

        // 契约测试用的人为延迟，把竞态窗口撑开到可确定性观测。默认 0，完全惰性。
        const int delay = g_open_race_delay_ms.load();
        if (delay > 0)
            std::this_thread::sleep_for(std::chrono::milliseconds(delay));

        auto openReported = std::make_shared<std::atomic<bool>>(false);
        wire_dc_callbacks(h, dc, openReported);
        // 出向专用补发：wire 之前若已经 open，回调就永远不会来了。
        resend_open_if_already_open(h, dc, openReported);

        *out_dc = h;
        return DCU_OK;
    });
}

int dcu_dc_send(int dc, const void *data, int len) {
    if (dc <= 0 || !data || len < 0)
        return DCU_ERR_INVALID;
    return dcu_wrap([dc, data, len] {
        const auto *b = static_cast<const rtc::byte *>(data);
        // send() 返回 false 仅表示「已缓冲」，上游 rtcSendMessage 同样忽略它。
        g_table.get_dc(dc)->send(rtc::binary(b, b + len));
        return DCU_OK;
    });
}

int dcu_dc_close(int dc) {
    if (dc <= 0)
        return DCU_ERR_INVALID;
    return dcu_wrap([dc] {
        g_table.get_dc(dc)->close();
        return DCU_OK;
    });
}

int dcu_dc_destroy(int dc) {
    if (dc <= 0)
        return DCU_ERR_INVALID;
    return dcu_wrap([dc] {
        auto d = g_table.get_dc(dc);
        d->close();
        d.reset();
        g_table.erase_dc(dc);
        return DCU_OK;
    });
}

int dcu_dc_state(int dc, int *out_state) {
    if (!out_state)
        return DCU_ERR_INVALID;
    *out_state = DCU_DC_STATE_CONNECTING;
    if (dc <= 0)
        return DCU_ERR_INVALID;
    return dcu_wrap([dc, out_state] {
        auto ch = g_table.get_dc(dc);
        // 有序读：先问 closed。closed 是终态，读到就不会再变；反过来先问 open
        // 则可能报出「已关闭却说 Open」。
        if (ch->isClosed())
            *out_state = DCU_DC_STATE_CLOSED;
        else if (ch->isOpen())
            *out_state = DCU_DC_STATE_OPEN;
        else
            *out_state = DCU_DC_STATE_CONNECTING;
        return DCU_OK;
    });
}

int dcu_test_set_open_race_delay_ms(int ms) {
    g_open_race_delay_ms.store(ms < 0 ? 0 : ms);
    return DCU_OK;
}

int dcu_dc_buffered_amount(int dc, int *out_amount) {
    if (!out_amount)
        return DCU_ERR_INVALID;
    *out_amount = 0;
    if (dc <= 0)
        return DCU_ERR_INVALID;
    return dcu_wrap([dc, out_amount] {
        *out_amount = static_cast<int>(g_table.get_dc(dc)->bufferedAmount());
        return DCU_OK;
    });
}

int dcu_event_next(dcu_event_header *out_header, void *buf, int cap, void *buf2, int cap2) {
    return g_queue.next(out_header, buf, cap, buf2, cap2);
}

int dcu_log_next(int *out_level, void *buf, int cap, int *out_len, int *out_dropped) {
    return g_log_queue.next(out_level, buf, cap, out_len, out_dropped);
}

int dcu_event_queue_depth(int *out_depth) {
    if (!out_depth)
        return DCU_ERR_INVALID;
    *out_depth = g_queue.size();
    return DCU_OK;
}

int dcu_dc_receive(int dc, void *buf, int cap, int *out_len) {
    if (!out_len)
        return DCU_ERR_INVALID;
    *out_len = 0;
    if (dc <= 0)
        return DCU_ERR_INVALID;

    return dcu_wrap([dc, buf, cap, out_len] {
        auto ch = g_table.get_dc(dc);

        // 不变量 2（见文件头）：peek -> 拷贝 -> **成功才 receive() 丢弃**。
        // 直接 receive() 再拷贝是 C++ 面上最直觉的写法，但调用方缓冲不足时
        // 消息就丢了 —— 在 reliable 通道上那是协议违约。
        auto msg = ch->peek();
        if (!msg)
            return DCU_ERR_NOT_AVAIL;

        const bool isBinary = std::holds_alternative<rtc::binary>(*msg);
        const size_t size = isBinary ? std::get<rtc::binary>(*msg).size()
                                     : std::get<rtc::string>(*msg).size();
        *out_len = static_cast<int>(size);

        if (!buf || cap < static_cast<int>(size))
            return DCU_ERR_TOO_SMALL; // 不消费

        if (size > 0) {
            const void *src = isBinary
                                  ? static_cast<const void *>(std::get<rtc::binary>(*msg).data())
                                  : static_cast<const void *>(std::get<rtc::string>(*msg).data());
            std::memcpy(buf, src, size);
        }

        ch->receive(); // 拷贝成功了才丢弃
        return DCU_OK;
    });
}

} // extern "C"
