#ifndef DCU_H
#define DCU_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32) && !defined(DCU_STATIC)
#  if defined(DCU_BUILD)
#    define DCU_API __declspec(dllexport)
#  else
#    define DCU_API __declspec(dllimport)
#  endif
#else
#  define DCU_API __attribute__((visibility("default")))
#endif

#define DCU_ABI_VERSION 2

/*
 * 调用约定（SPEC §4）
 * ------------------
 * **每个函数都返回状态码；它产出的每个值都经 out 参数带出。**
 * 没有任何函数的返回值兼作数据 —— 判断成功只有一种写法：rc == DCU_OK。
 *
 * 这条取代了此前两种在 0 上冲突的形状：句柄型（> 0 有效，0 视为失败）与
 * 计数型（0 是合法值）。此前没出事只是因为上游 lastId 恰好从 1 开始，
 * 那是实现细节不是契约。
 *
 * 错误码**独立编号**，刻意不与 RTC_ERR_* 逐值相同：一旦有人写出
 * `return rtcSomething(...)` 这样的直接透传，数值相同的话会返回一个
 * **长得完全合法的 dcu 码**并被静默当真；独立编号下它是一个未定义的码，
 * 一眼看出是上游漏出来的。
 */
#define DCU_OK 0
#define DCU_ERR_INVALID -101          /* 调用方传错了，可自助修复 */
#define DCU_ERR_FAILURE -102          /* 运行期失败 */
#define DCU_ERR_NOT_AVAIL -103        /* 此刻没有可返回的东西 */
#define DCU_ERR_TOO_SMALL -104        /* 调用方缓冲不足，见下方幂等重试契约 */
#define DCU_ERR_UPSTREAM_UNKNOWN -105 /* 上游失败但无法归类，绝不压平成 FAILURE */

/*
 * 缓冲不足的幂等重试契约
 * ----------------------
 * 返回 DCU_ERR_TOO_SMALL 时：填入**所需长度**，且**不消费**该对象
 * （事件不弹出）。扩容后重试是幂等的。
 *
 * 因为填入的长度是精确值，且单消费者契约保证两次调用之间队首不变，
 * **一次重试必然成功**；第二次仍失败意味着单消费者契约被破坏 ——
 * 那是要人来看的 bug，不是该循环重试的瞬时故障。
 *
 * 返回值域门禁
 * ------------
 * dcu_event_next 只允许返回
 * DCU_OK / DCU_ERR_NOT_AVAIL / DCU_ERR_TOO_SMALL / DCU_ERR_INVALID。
 * 这一条读一遍函数就能核实。
 *
 * **将来若给它新增错误码，必须同时声明它是否消费队首。**
 */

/* DataChannel 三态。**活查询，不是缓存** —— 回调是通知、状态是查询，与浏览器
 * readyState、libwebrtc state()、libdatachannel isOpen() 同构。 */
#define DCU_DC_STATE_CONNECTING 0
#define DCU_DC_STATE_OPEN 1
#define DCU_DC_STATE_CLOSED 2

/* DataChannel label 的上界，单位是 UTF-8 字节。**实测值，不是理论值**：
 * 65535 端到端可用，65536 越界。上游 OutgoingDataChannel::open() 的
 * to_uint16(mLabel.size()) 会抛，而若 DC 在**连接前**创建，该异常被
 * iterateDataChannels 的 catch 吞成一行 PLOG_WARNING —— 调用方拿到正句柄、
 * 通道不 open 不 closed 无 error，而 rtcSendMessage 仍返回成功且消息真发上线，
 * 对端判协议违规关流。故两层都要前置校验。 */
#define DCU_LABEL_MAX_BYTES 65535

/* 状态枚举越界时的取值。上游新增成员时映射到它，绝不抛、绝不丢事件、
 * 也绝不冒充某个既有成员。 */
#define DCU_STATE_UNKNOWN -1

typedef enum dcu_event_type {
    DCU_EVENT_NONE = 0,
    DCU_EVENT_LOCAL_DESCRIPTION = 1,
    DCU_EVENT_LOCAL_CANDIDATE = 2,
    DCU_EVENT_CONNECTION_STATE = 3,
    DCU_EVENT_GATHERING_STATE = 4,
    DCU_EVENT_INCOMING_DATA_CHANNEL = 5,
    DCU_EVENT_DC_OPEN = 6,
    DCU_EVENT_DC_CLOSED = 7,
    DCU_EVENT_DC_ERROR = 8
    /* 没有 DC_MESSAGE：消息不进事件队列，改由 dcu_dc_receive 逐通道拉取。
     * 见下方「控制推、数据拉」。 */
} dcu_event_type;

typedef struct dcu_event_header {
    int type; /* dcu_event_type */
    int pc;
    int dc;
    int state;
    int payload_len;
    int payload2_len;
} dcu_event_header;

typedef struct dcu_ice_server {
    const char **urls;
    int url_count;
    const char *username;
    const char *credential;
} dcu_ice_server;

typedef struct dcu_pc_config {
    const dcu_ice_server *ice_servers;
    int ice_server_count;
    int transport_policy; /* 0 All, 1 RelayOnly */
    uint16_t port_range_begin;
    uint16_t port_range_end;
    const char *bind_address;
    int enable_ice_tcp;
    int enable_ice_udp_mux;
    int mtu;
    int max_message_size;
} dcu_pc_config;

typedef struct dcu_dc_init {
    int ordered;
    int reliable;
    uint32_t max_retransmits;
    uint32_t max_packet_lifetime;
} dcu_dc_init;

/* --- 全局 ---------------------------------------------------------------- */

DCU_API int dcu_abi_version(int *out_version);
DCU_API int dcu_init(void);
DCU_API int dcu_shutdown(void);
DCU_API int dcu_set_log_level(int level);

/* --- PeerConnection ------------------------------------------------------ */

DCU_API int dcu_pc_create(const dcu_pc_config *config, int *out_pc);
DCU_API int dcu_pc_close(int pc);
DCU_API int dcu_pc_destroy(int pc);
DCU_API int dcu_pc_set_remote_description(int pc, const char *sdp, int sdp_len,
                                          const char *type, int type_len);
DCU_API int dcu_pc_add_remote_candidate(int pc, const char *cand, int cand_len,
                                        const char *mid, int mid_len);
DCU_API int dcu_pc_create_data_channel(int pc, const char *label, int label_len,
                                       const dcu_dc_init *init, int *out_dc);

/* --- DataChannel --------------------------------------------------------- */

/* data 是长度定界的**不透明字节**，可含内嵌 NUL，故用 const void* 而非 char*
 * —— 后者对 C 读者暗示 NUL 终止。 */
DCU_API int dcu_dc_send(int dc, const void *data, int len);
DCU_API int dcu_dc_close(int dc);
DCU_API int dcu_dc_destroy(int dc);
DCU_API int dcu_dc_buffered_amount(int dc, int *out_amount);

/* 三态活查询，一次 ABI 穿越内合成，调用方不必分别问 open 与 closed。
 * （上游只暴露 isOpen()/isClosed() 两个独立读，无法做到真正原子；此处保证的是
 * 「一次穿越 + 有序读」，最坏情况是刚关闭的通道被报成 Connecting，下次查询即纠正。） */
DCU_API int dcu_dc_state(int dc, int *out_state);

/* --- 事件泵 -------------------------------------------------------------- */

/* 单次原子取事件：填充 header + 两段载荷并弹出；缓冲不足则填好 header（含两个
 * 精确长度）但**不弹出**，返回 DCU_ERR_TOO_SMALL；队列空则 header.type =
 * DCU_EVENT_NONE 并返回 DCU_ERR_NOT_AVAIL。 */
DCU_API int dcu_event_next(dcu_event_header *out_header, void *buf, int cap, void *buf2,
                           int cap2);

/* 控制队列的只读深度。永不丢控制事件，故队列无界；深度暴露出来是为了让积压可见。 */
DCU_API int dcu_event_queue_depth(int *out_depth);

/*
 * 控制推、数据拉（SPEC §4 / #30 决议 2、3）
 * -----------------------------------------
 * 消息**不进**上面的事件队列。dcu 刻意不设 rtcSetMessageCallback —— 上游
 * Channel::flushPendingMessages 是 `while (messageCallback)`，不设回调消息就留在
 * 它自己的 mRecvQueue（1024 条/通道）里。队列满时 push **阻塞**，背压顶回 SCTP
 * 接收窗口，对端被迫减速。**这是真背压，不是丢包** —— 在 reliable 通道上丢消息
 * 等于让 Reliable = true 变成假承诺。
 *
 * 语义与上游 rtcReceiveMessage 逐字相同：peek -> 拷贝 -> **成功才丢弃**。
 * 缓冲不足时填入所需长度、不消费，扩容重试幂等。
 *
 * 注意：WebGL 上背压保证**不成立**（datachannel-wasm 没有接收队列，浏览器
 * onmessage 不可阻塞），见 SPEC §8。
 */
DCU_API int dcu_dc_receive(int dc, void *buf, int cap, int *out_len);

/* --- 仅供契约测试 -------------------------------------------------------- */

/*
 * 在出向 createDataChannel 之后、wire 回调之前插入人为延迟（毫秒）。
 *
 * **它存在的唯一理由**是让「出向 open 补发」这条分支可被确定性地验证。那条分支
 * 的竞态窗口约 1µs 对一个 SCTP RTT，正常跑几乎总是走回调路径 —— 不注入延迟，
 * 测试只能证明「连接后建通道能开」，证明不了补发本身是对的。
 *
 * 为什么是导出而不是编译期开关：编译期开关会让**被测二进制与出货二进制不是同一个**。
 * 这个函数随产品一起出货、默认 0、不设就完全惰性，代价是公开面多一个显然叫 test 的
 * 符号 —— 用「测你出货的那个」换它，值。
 */
DCU_API int dcu_test_set_open_race_delay_ms(int ms);

#ifdef __cplusplus
}
#endif

#endif /* DCU_H */
