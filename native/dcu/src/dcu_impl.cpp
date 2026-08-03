#include "dcu.h"
#include "dcu_queue.hpp"

#include <atomic>
#include <cstring>
#include <memory>
#include <sstream>
#include <string>
#include <vector>

#include <rtc/rtc.h>

namespace {

std::atomic<bool> g_inited{false};
DcuEventQueue g_queue;
std::mutex g_log_mu;

std::string percent_encode_userinfo(const std::string &s) {
    static const char *hex = "0123456789ABCDEF";
    std::string out;
    out.reserve(s.size() * 3);
    for (unsigned char c : s) {
        if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
            c == '-' || c == '_' || c == '.' || c == '~') {
            out.push_back(static_cast<char>(c));
        } else {
            out.push_back('%');
            out.push_back(hex[c >> 4]);
            out.push_back(hex[c & 0xF]);
        }
    }
    return out;
}

std::string build_ice_uri(const std::string &url, const char *user, const char *pass) {
    if (!user || !user[0])
        return url;

    // Inject user:pass@ after scheme://
    auto pos = url.find("://");
    std::string scheme;
    std::string rest;
    if (pos == std::string::npos) {
        scheme = "turn:";
        rest = url;
        // If url already has scheme without //
        auto colon = url.find(':');
        if (colon != std::string::npos && url.find('@') == std::string::npos) {
            // e.g. turn:host:3478
            scheme = url.substr(0, colon + 1);
            rest = url.substr(colon + 1);
        }
    } else {
        scheme = url.substr(0, pos + 3);
        rest = url.substr(pos + 3);
    }

    // Skip if already has userinfo
    if (rest.find('@') != std::string::npos)
        return url;

    std::string u = percent_encode_userinfo(user);
    std::string p = pass ? percent_encode_userinfo(pass) : "";
    return scheme + u + ":" + p + "@" + rest;
}

void push_event(DcuEvent ev) { g_queue.push(std::move(ev)); }

void on_local_description(int pc, const char *sdp, const char *type, void *) {
    DcuEvent ev;
    ev.type = DCU_EVENT_LOCAL_DESCRIPTION;
    ev.pc = pc;
    ev.payload = bytes_from_cstr(sdp);
    ev.payload2 = bytes_from_cstr(type);
    push_event(std::move(ev));
}

void on_local_candidate(int pc, const char *cand, const char *mid, void *) {
    DcuEvent ev;
    ev.type = DCU_EVENT_LOCAL_CANDIDATE;
    ev.pc = pc;
    ev.payload = bytes_from_cstr(cand);
    ev.payload2 = bytes_from_cstr(mid);
    push_event(std::move(ev));
}

void on_state_change(int pc, rtcState state, void *) {
    DcuEvent ev;
    ev.type = DCU_EVENT_CONNECTION_STATE;
    ev.pc = pc;
    ev.state = static_cast<int>(state);
    push_event(std::move(ev));
}

void on_gathering_state(int pc, rtcGatheringState state, void *) {
    DcuEvent ev;
    ev.type = DCU_EVENT_GATHERING_STATE;
    ev.pc = pc;
    ev.state = static_cast<int>(state);
    push_event(std::move(ev));
}

void on_dc_open(int id, void *) {
    DcuEvent ev;
    ev.type = DCU_EVENT_DC_OPEN;
    ev.dc = id;
    push_event(std::move(ev));
}

void on_dc_closed(int id, void *) {
    DcuEvent ev;
    ev.type = DCU_EVENT_DC_CLOSED;
    ev.dc = id;
    push_event(std::move(ev));
}

void on_dc_error(int id, const char *error, void *) {
    DcuEvent ev;
    ev.type = DCU_EVENT_DC_ERROR;
    ev.dc = id;
    ev.payload = bytes_from_cstr(error);
    push_event(std::move(ev));
}

void on_dc_message(int id, const char *message, int size, void *) {
    DcuEvent ev;
    ev.type = DCU_EVENT_DC_MESSAGE;
    ev.dc = id;
    if (size >= 0) {
        ev.payload.assign(reinterpret_cast<const uint8_t *>(message),
                          reinterpret_cast<const uint8_t *>(message) + size);
    } else if (message) {
        // libdatachannel string form; treat as UTF-8 bytes without NUL
        size_t n = std::strlen(message);
        ev.payload.assign(reinterpret_cast<const uint8_t *>(message),
                          reinterpret_cast<const uint8_t *>(message) + n);
    }
    push_event(std::move(ev));
}

void wire_dc_callbacks(int dc) {
    rtcSetOpenCallback(dc, on_dc_open);
    rtcSetClosedCallback(dc, on_dc_closed);
    rtcSetErrorCallback(dc, on_dc_error);
    rtcSetMessageCallback(dc, on_dc_message);
}

void on_data_channel(int pc, int dc, void *) {
    char label[256];
    int n = rtcGetDataChannelLabel(dc, label, static_cast<int>(sizeof(label)));
    DcuEvent ev;
    ev.type = DCU_EVENT_INCOMING_DATA_CHANNEL;
    ev.pc = pc;
    ev.dc = dc;
    if (n > 0)
        ev.payload.assign(reinterpret_cast<uint8_t *>(label),
                          reinterpret_cast<uint8_t *>(label) + (n > 0 ? std::strlen(label) : 0));
    wire_dc_callbacks(dc);
    push_event(std::move(ev));
}

void wire_pc_callbacks(int pc) {
    rtcSetLocalDescriptionCallback(pc, on_local_description);
    rtcSetLocalCandidateCallback(pc, on_local_candidate);
    rtcSetStateChangeCallback(pc, on_state_change);
    rtcSetGatheringStateChangeCallback(pc, on_gathering_state);
    rtcSetDataChannelCallback(pc, on_data_channel);
}

} // namespace

extern "C" {

int dcu_abi_version(void) { return DCU_ABI_VERSION; }

int dcu_init(void) {
    if (g_inited.exchange(true))
        return DCU_OK;
    rtcInitLogger(RTC_LOG_WARNING, nullptr);
    rtcPreload();
    return DCU_OK;
}

int dcu_shutdown(void) {
    if (!g_inited.exchange(false))
        return DCU_OK;
    g_queue.clear();
    rtcCleanup();
    return DCU_OK;
}

int dcu_set_log_level(int level) {
    // Map roughly to rtcLogLevel
    rtcLogLevel l = RTC_LOG_WARNING;
    if (level <= 0)
        l = RTC_LOG_NONE;
    else if (level == 1)
        l = RTC_LOG_FATAL;
    else if (level == 2)
        l = RTC_LOG_ERROR;
    else if (level == 3)
        l = RTC_LOG_WARNING;
    else if (level == 4)
        l = RTC_LOG_INFO;
    else if (level == 5)
        l = RTC_LOG_DEBUG;
    else
        l = RTC_LOG_VERBOSE;
    rtcInitLogger(l, nullptr);
    return DCU_OK;
}

int dcu_pc_create(const dcu_pc_config *config) {
    if (!g_inited.load())
        return DCU_ERR_FAILURE;
    if (!config)
        return DCU_ERR_INVALID;

    std::vector<std::string> uriStorage;
    std::vector<const char *> uriPtrs;

    if (config->ice_servers && config->ice_server_count > 0) {
        for (int i = 0; i < config->ice_server_count; ++i) {
            const dcu_ice_server &s = config->ice_servers[i];
            if (!s.urls || s.url_count <= 0)
                continue;
            for (int u = 0; u < s.url_count; ++u) {
                if (!s.urls[u])
                    continue;
                uriStorage.push_back(build_ice_uri(s.urls[u], s.username, s.credential));
            }
        }
    }
    uriPtrs.reserve(uriStorage.size());
    for (auto &s : uriStorage)
        uriPtrs.push_back(s.c_str());

    rtcConfiguration cfg{};
    std::memset(&cfg, 0, sizeof(cfg));
    if (!uriPtrs.empty()) {
        cfg.iceServers = uriPtrs.data();
        cfg.iceServersCount = static_cast<int>(uriPtrs.size());
    }
    cfg.iceTransportPolicy =
        config->transport_policy == 1 ? RTC_TRANSPORT_POLICY_RELAY : RTC_TRANSPORT_POLICY_ALL;
    cfg.portRangeBegin = config->port_range_begin;
    cfg.portRangeEnd = config->port_range_end;
    cfg.bindAddress = config->bind_address;
    cfg.enableIceTcp = config->enable_ice_tcp != 0;
    cfg.enableIceUdpMux = config->enable_ice_udp_mux != 0;
    cfg.mtu = config->mtu;
    cfg.maxMessageSize = config->max_message_size;
    cfg.disableAutoNegotiation = false;

    int pc = rtcCreatePeerConnection(&cfg);
    if (pc <= 0)
        return pc < 0 ? pc : DCU_ERR_FAILURE;

    wire_pc_callbacks(pc);
    return pc;
}

int dcu_pc_close(int pc) {
    if (pc <= 0)
        return DCU_ERR_INVALID;
    return rtcClosePeerConnection(pc) == 0 ? DCU_OK : DCU_ERR_FAILURE;
}

int dcu_pc_destroy(int pc) {
    if (pc <= 0)
        return DCU_ERR_INVALID;
    return rtcDeletePeerConnection(pc) == 0 ? DCU_OK : DCU_ERR_FAILURE;
}

int dcu_pc_set_remote_description(int pc, const char *sdp, int sdp_len, const char *type,
                                  int type_len) {
    if (pc <= 0 || !sdp || sdp_len < 0)
        return DCU_ERR_INVALID;
    std::string s = string_from_buf(sdp, sdp_len);
    std::string t = string_from_buf(type, type_len);
    int rc = rtcSetRemoteDescription(pc, s.c_str(), t.empty() ? nullptr : t.c_str());
    return rc == 0 ? DCU_OK : DCU_ERR_FAILURE;
}

int dcu_pc_add_remote_candidate(int pc, const char *cand, int cand_len, const char *mid,
                                int mid_len) {
    if (pc <= 0 || !cand || cand_len < 0)
        return DCU_ERR_INVALID;
    std::string c = string_from_buf(cand, cand_len);
    std::string m = string_from_buf(mid, mid_len);
    int rc = rtcAddRemoteCandidate(pc, c.c_str(), m.empty() ? nullptr : m.c_str());
    return rc == 0 ? DCU_OK : DCU_ERR_FAILURE;
}

int dcu_pc_create_data_channel(int pc, const char *label, int label_len, const dcu_dc_init *init) {
    if (pc <= 0 || !label || label_len < 0)
        return DCU_ERR_INVALID;
    std::string lab = string_from_buf(label, label_len);

    int dc;
    if (!init) {
        dc = rtcCreateDataChannel(pc, lab.c_str());
    } else {
        rtcDataChannelInit di{};
        std::memset(&di, 0, sizeof(di));
        di.reliability.unordered = init->ordered ? false : true;
        di.reliability.unreliable = init->reliable ? false : true;
        di.reliability.maxRetransmits = init->max_retransmits;
        di.reliability.maxPacketLifeTime = init->max_packet_lifetime;
        dc = rtcCreateDataChannelEx(pc, lab.c_str(), &di);
    }
    if (dc <= 0)
        return dc < 0 ? dc : DCU_ERR_FAILURE;
    wire_dc_callbacks(dc);
    return dc;
}

int dcu_dc_send(int dc, const char *data, int len) {
    if (dc <= 0 || !data || len < 0)
        return DCU_ERR_INVALID;
    int rc = rtcSendMessage(dc, data, len);
    return rc == 0 ? DCU_OK : DCU_ERR_FAILURE;
}

int dcu_dc_close(int dc) {
    if (dc <= 0)
        return DCU_ERR_INVALID;
    return rtcClose(dc) == 0 ? DCU_OK : DCU_ERR_FAILURE;
}

int dcu_dc_destroy(int dc) {
    if (dc <= 0)
        return DCU_ERR_INVALID;
    return rtcDeleteDataChannel(dc) == 0 ? DCU_OK : DCU_ERR_FAILURE;
}

int dcu_dc_buffered_amount(int dc) {
    if (dc <= 0)
        return DCU_ERR_INVALID;
    int n = rtcGetBufferedAmount(dc);
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
