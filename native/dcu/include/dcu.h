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

#define DCU_ABI_VERSION 1

#define DCU_OK 0
#define DCU_ERR_INVALID -1
#define DCU_ERR_FAILURE -2
#define DCU_ERR_NOT_AVAIL -3
#define DCU_ERR_TOO_SMALL -4

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
    int type;       /* dcu_event_type */
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

DCU_API int dcu_abi_version(void);
DCU_API int dcu_init(void);
DCU_API int dcu_shutdown(void);
DCU_API int dcu_set_log_level(int level);

DCU_API int dcu_pc_create(const dcu_pc_config *config);
DCU_API int dcu_pc_close(int pc);
DCU_API int dcu_pc_destroy(int pc);
DCU_API int dcu_pc_set_remote_description(int pc, const char *sdp, int sdp_len,
                                          const char *type, int type_len);
DCU_API int dcu_pc_add_remote_candidate(int pc, const char *cand, int cand_len,
                                        const char *mid, int mid_len);
DCU_API int dcu_pc_create_data_channel(int pc, const char *label, int label_len,
                                       const dcu_dc_init *init);

DCU_API int dcu_dc_send(int dc, const char *data, int len);
DCU_API int dcu_dc_close(int dc);
DCU_API int dcu_dc_destroy(int dc);
DCU_API int dcu_dc_buffered_amount(int dc);

DCU_API int dcu_event_peek(dcu_event_header *out_header);
DCU_API int dcu_event_copy_payload(char *buffer, int capacity);
DCU_API int dcu_event_copy_payload2(char *buffer, int capacity);
DCU_API int dcu_event_pop(void);

#ifdef __cplusplus
}
#endif

#endif /* DCU_H */
