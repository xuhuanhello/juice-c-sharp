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
 * dcu_event_copy_payload / dcu_event_copy_payload2 只允许返回
 * DCU_OK / DCU_ERR_NOT_AVAIL / DCU_ERR_TOO_SMALL / DCU_ERR_INVALID。
 * 这一条读一遍函数就能核实。
 *
 * **将来若给这两个函数新增错误码，必须同时声明它是否消费队首。**
 */

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
    DCU_EVENT_DC_ERROR = 8,
    DCU_EVENT_DC_MESSAGE = 9
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

/* --- 事件泵 -------------------------------------------------------------- */

DCU_API int dcu_event_peek(dcu_event_header *out_header);
DCU_API int dcu_event_copy_payload(void *buffer, int capacity, int *out_len);
DCU_API int dcu_event_copy_payload2(void *buffer, int capacity, int *out_len);
DCU_API int dcu_event_pop(void);

#ifdef __cplusplus
}
#endif

#endif /* DCU_H */
