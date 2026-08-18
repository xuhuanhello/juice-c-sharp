// dcu 稳定 C ABI 的实现，架在 libdatachannel 的 **C++ API** 之上（决策 #42 / SPEC §2）。
//
// 本文件不再包含 <rtc/rtc.h>：句柄由 dcu 自己的 DcuHandleTable 分配与验活，
// 而不是 src/capi.cpp 里那张够不着的私有 map。
//
// ABI 的版本号与导出清单**不写在本文件**：版本看 dcu.h 的 DCU_ABI_VERSION，
// 成员看 native/exports/expected-symbols.txt。本文件头曾写死一对「版本号 + 导出数」
// 并烂到与实物脱节两个大版本 —— 散文里的数字没人检查就会撒谎（CONTEXT.md 词条 1
// 的同一课）。事件 ABI、独立错误码、拉模式消息、日志桥均已落地，见下文各段。
//
// 三条**违反了不会报错**的不变量（SPEC §2），改动本文件前请先读：
//
//   1. 入向 DataChannel 的 onOpen 必须在 onDataChannel 回调体内**同步** wire。
//      上游用调用顺序实现「注册后重放」（triggerDataChannel 先 resetOpenCallback，
//      再 dataChannelCallback，最后 triggerOpen）。若把 wire 挪到 lambda 之外或
//      延后到登记之后，入向通道的 DC_OPEN 会全部丢失，且编译、门禁、单向测试全绿。
//   2. 收消息若改拉模式，必须沿用 peek() / receive() 这一对（拷贝成功才丢弃）。
//      直接 receive() 再拷贝会在调用方缓冲不足时丢消息 —— reliable 通道上即协议违约。
//   3. 出向补发 DC_OPEN（#32 决议 2，见 resend_open_if_already_open）只能存在于
//      出向创建路径；入向路径此刻 mIsOpen 已为 true 而 mOpenTriggered 刚被重置，
//      补查会倒转事件顺序。

#include "dcu.h"
#include "dcu_handles.hpp"
#include "dcu_log_queue.hpp"
#include "dcu_queue.hpp"

#include <atomic>
#include <chrono>
#include <cstring>
#include <future>
#include <thread>
#include <memory>
#include <stdexcept>
#include <string>
#include <variant>
#include <vector>

#include <rtc/rtc.hpp>

namespace {

std::atomic<bool> g_inited{false};
// #150：上一次 dcu_shutdown 的 Cleanup future。超时抛出后它仍在后台收尾，
// 下一次 dcu_init 必须先看它 —— init 与在途 Cleanup 的竞态由此确定性消灭。
// 只在 dcu_init / dcu_shutdown 中触碰，而这两个入口由托管侧的主线程契约串行化
//（SPEC §4：不得在事件处理内调用），故不加锁。
std::shared_future<void> g_pending_cleanup;
DcuEventQueue g_queue;
DcuLogQueue g_log_queue;
DcuHandleTable g_table;
std::atomic<int> g_open_race_delay_ms{0}; // 仅契约测试用，见 dcu.h

// ---------------------------------------------------------------------------
// 异常边界
//
// 上游 capi 的 wrap() 少一个 catch(...)，且把 what() 压进 plog 就丢了。这里补上
// catch(...)，异常文本经 log_error 走日志桥（#33）。错误码**独立编号**（#31），
// 刻意不与 RTC_ERR_* 逐值相同，数值只写在 dcu.h：透传上游错误码的代码会因此
// 产出一个未定义的码而当场暴露，而不是一个「长得完全合法」的错误。
//   std::invalid_argument -> DCU_ERR_INVALID
//   其余 std::exception   -> DCU_ERR_FAILURE
//   非 std 异常           -> DCU_ERR_UPSTREAM_UNKNOWN
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
        int dh;
        try {
            dh = g_table.add_dc(dc);
        } catch (const std::exception &e) {
            // #151：句柄空间耗尽（add_dc 到顶即抛）。本 lambda 跑在上游的回调
            // 线程上，异常穿出去就是 std::terminate —— 回调边界与 C ABI 边界
            // 同样不许异常穿越。丢弃这条入向通道并记日志：2^31 句柄耗尽时
            // 一切都已不可用，一条被拒的通道 + 一行 Error 是诚实的失败形态。
            log_error(e.what());
            return;
        }
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

    // #150：上一次 shutdown 的 Cleanup 若还在后台收尾，此刻 init 会与「正在拆
    // 全局态的上游」竞态（编辑器域重载恰是这个时序）。fail-fast + 回滚：
    // 失败如实报出，等收尾完成后重试即可成功。
    if (g_pending_cleanup.valid() &&
        g_pending_cleanup.wait_for(std::chrono::seconds(0)) == std::future_status::timeout) {
        g_inited.store(false);
        log_error("dcu_init refused: the previous dcu_shutdown's Cleanup is still finishing in the "
                  "background (it had timed out; possible upstream deadlock). Retry after it completes.");
        return DCU_ERR_FAILURE;
    }

    const int rc = dcu_wrap([] {
        rtc::InitLogger(rtc::LogLevel::Warning, log_trampoline);
        rtc::Preload();
        return DCU_OK;
    });
    // #150：失败回滚。exchange(true) 在前，不回滚的话下一次 dcu_init 会直接
    // 返回 DCU_OK —— 失败被闩成永久成功，桥未接、Preload 未跑，而调用方看到
    // 的一切都像初始化过了。回滚后 init 可重试（dcu.h 写明）。
    if (rc != DCU_OK)
        g_inited.store(false);
    return rc;
}

int dcu_shutdown(int *out_undestroyed) {
    if (!out_undestroyed)
        return DCU_ERR_INVALID;
    *out_undestroyed = 0;
    if (!g_inited.exchange(false))
        return DCU_OK;
    // 顺序对应上游 rtcCleanup：先丢对象（eraseAll），再 Cleanup。队列在对象销毁
    // **之后**清，这样销毁期间回调推进来的事件也一并清掉。
    return dcu_wrap([out_undestroyed] {
        // clear() 返回它丢掉的对象数 —— 那正是「到调用 shutdown 为止都没人销毁的」。
        // 托管侧把它当泄漏账单用：正常收尾应当是 0，因为域将死之前会先
        // DisposeAllLive() 精确释放一遍（#37 决议 2、5）。
        int dropped = static_cast<int>(g_table.clear());

        // #150：二次清扫孤儿窗口。第一次 clear() 在锁外析构 PC 时，在途的
        // onDataChannel 回调仍可能完成 add_dc，把入向 DC 塞进刚清空的表 ——
        // 此刻全部 PC 已析构完，回调物理上不可能再发生，扫到的就是全部孤儿。
        // 不清的话：孤儿的 shared_ptr 会把下面的 Cleanup 拖到超时（上游要等
        // 所有对象释放），且不进账单 —— 泄漏、假账、超时三个症状同一根。
        dropped += static_cast<int>(g_table.clear());
        *out_undestroyed = dropped;

        g_queue.clear();
        g_log_queue.clear();
        // #150：future 存入全局再等。超时抛出 → FAILURE，但 Cleanup 仍在后台跑；
        // 下一次 dcu_init 先查它，fail-fast 而不是与收尾竞态。
        g_pending_cleanup = rtc::Cleanup();
        if (g_pending_cleanup.wait_for(std::chrono::seconds(10)) == std::future_status::timeout)
            throw std::runtime_error("Cleanup timeout (possible deadlock or undestructible object)");
        return DCU_OK;
    });
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
    // #151：倒置端口区间是明确无意义的输入，在两个消费边界都失败（C# 侧同判据）。
    // 单侧设置（任一端为 0）语义归上游，不校验。
    if (config->port_range_end != 0 && config->port_range_begin > config->port_range_end)
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

int dcu_pc_connection_path(int pc, int *out_verdict, void *buf, int cap, int *out_len) {
    if (!out_verdict || !out_len)
        return DCU_ERR_INVALID;
    *out_verdict = DCU_PATH_DIRECT;
    *out_len = 0;
    if (pc <= 0)
        return DCU_ERR_INVALID;

    return dcu_wrap([pc, out_verdict, buf, cap, out_len] {
        auto conn = g_table.get_pc(pc);

        // 状态门禁**在这里**，不在托管侧。上游的选中候选对从不被清空，所以失败或
        // 断开之后 getSelectedCandidatePair 仍会返回 true 并带回上一次的对；不拦
        // 就是把陈旧值当现状交出去。
        //
        // 放这一层而不是 C# 那一层，是实测出来的：`state` 是 std::atomic<State>
        // 的一次原子读（peerconnection.cpp:52），**活的**；而 C# 的 ConnectionState
        // 是事件缓存，落后到下一次 pump 派发为止。用缓存做门禁会拒掉一个真正已连接
        // 的连接 —— 通道的 State 是活查询，会先变 Open，于是调用方在事件派发前问
        // 就被自己的门禁挡住。（那个 conn_lock 的成本顾虑在 libjuice 那一层，与本
        // 判断无关。）
        if (conn->state() != rtc::PeerConnection::State::Connected)
            return DCU_ERR_NOT_AVAIL;

        rtc::Candidate local, remote;
        if (!conn->getSelectedCandidatePair(&local, &remote))
            return DCU_ERR_NOT_AVAIL;

        // 判据两端都要看：走中继时，靠 TURN 那一侧的 local 是 relay 而 remote
        // 是对面的 host —— 只判 remote 会把这一侧报成直连。见 dcu.h 的注释。
        *out_verdict = (local.type() == rtc::Candidate::Type::Relayed ||
                        remote.type() == rtc::Candidate::Type::Relayed)
                           ? DCU_PATH_RELAYED
                           : DCU_PATH_DIRECT;

        // 只带远端。本地那条在非中继路径上是 local.candidates[0] 的替身而非真实
        // 路径（dcu.h 有完整理由）—— 判定内部采信它、且只在 == relay 这个方向上
        // 采信，与把它交给调用方是两件事。
        //
        // std::string(remote) 走 operator string()，带 "a=" 前缀，与
        // DCU_EVENT_LOCAL_CANDIDATE 同形；Candidate::candidate() 是不带前缀的
        // 那个，别混 —— 一个 API 面上两种形态会让调用方无从判断要不要自己拼。
        const std::string sdp = std::string(remote);
        const size_t size = sdp.size();
        *out_len = static_cast<int>(size);

        // 与 dcu_dc_receive / dcu_log_next 同款：长度先填精确值，再判容量。
        // 判定已经写进 out_verdict —— 缓冲不足只影响 SDP 那一段，不影响判定，
        // 但仍返回 TOO_SMALL，因为调用方要的两个出参没有都拿到。
        if (!buf || cap < static_cast<int>(size))
            return DCU_ERR_TOO_SMALL;

        if (size > 0)
            std::memcpy(buf, sdp.data(), size);

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
