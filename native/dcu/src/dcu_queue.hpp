#pragma once

#include "dcu.h"

#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <vector>

struct DcuEvent {
    dcu_event_type type = DCU_EVENT_NONE;
    int pc = 0;
    int dc = 0;
    int state = 0;
    std::vector<uint8_t> payload;
    std::vector<uint8_t> payload2;
};

class DcuEventQueue {
public:
    void push(DcuEvent ev) {
        std::lock_guard<std::mutex> lock(mu_);
        q_.push_back(std::move(ev));
    }

    bool peek(dcu_event_header *out) {
        std::lock_guard<std::mutex> lock(mu_);
        if (q_.empty()) {
            if (out) {
                out->type = DCU_EVENT_NONE;
                out->pc = out->dc = out->state = 0;
                out->payload_len = out->payload2_len = 0;
            }
            return false;
        }
        const auto &e = q_.front();
        if (out) {
            out->type = static_cast<int>(e.type);
            out->pc = e.pc;
            out->dc = e.dc;
            out->state = e.state;
            out->payload_len = static_cast<int>(e.payload.size());
            out->payload2_len = static_cast<int>(e.payload2.size());
        }
        return true;
    }

    int copy_payload(char *buffer, int capacity, bool second) {
        std::lock_guard<std::mutex> lock(mu_);
        if (q_.empty())
            return DCU_ERR_NOT_AVAIL;
        const auto &buf = second ? q_.front().payload2 : q_.front().payload;
        if (!buffer || capacity < static_cast<int>(buf.size()))
            return DCU_ERR_TOO_SMALL;
        if (!buf.empty())
            std::memcpy(buffer, buf.data(), buf.size());
        return static_cast<int>(buf.size());
    }

    int pop() {
        std::lock_guard<std::mutex> lock(mu_);
        if (q_.empty())
            return DCU_ERR_NOT_AVAIL;
        q_.pop_front();
        return DCU_OK;
    }

    void clear() {
        std::lock_guard<std::mutex> lock(mu_);
        q_.clear();
    }

private:
    std::mutex mu_;
    std::deque<DcuEvent> q_;
};

inline std::vector<uint8_t> bytes_from_cstr(const char *s) {
    if (!s)
        return {};
    size_t n = std::strlen(s);
    return std::vector<uint8_t>(s, s + n);
}

inline std::vector<uint8_t> bytes_from_buf(const char *p, int len) {
    if (!p || len <= 0)
        return {};
    return std::vector<uint8_t>(p, p + len);
}

inline std::string string_from_buf(const char *p, int len) {
    if (!p || len <= 0)
        return {};
    return std::string(p, p + len);
}
