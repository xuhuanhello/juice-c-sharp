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

    // 单次原子取事件（SPEC §4 / #30 决议 1）。
    //
    // 取代了原先 peek -> copy_payload -> copy_payload2 -> pop 的四段式：那套协议
    // 靠一个隐式的「当前队首」把四次独立加锁串起来，而 copy_payload(buf, cap) 的
    // 签名里**没有任何东西**把它和刚才那次 peek 绑定 —— 它读的是「此刻的队首」。
    // 契约靠文档维持是脆的；单次调用让误用在物理上不可能：要么拿到一整个事件，
    // 要么什么都没拿到。
    //
    // 返回值域穷尽为 OK / NOT_AVAIL / TOO_SMALL / INVALID —— 读一遍本函数即可核实。
    // **将来若新增错误码，必须同时声明它是否消费队首。**
    int next(dcu_event_header *out, void *buf, int cap, void *buf2, int cap2) {
        if (!out)
            return DCU_ERR_INVALID;

        std::lock_guard<std::mutex> lock(mu_);
        out->type = DCU_EVENT_NONE;
        out->pc = out->dc = out->state = 0;
        out->payload_len = out->payload2_len = 0;

        if (q_.empty())
            return DCU_ERR_NOT_AVAIL;

        const auto &e = q_.front();
        out->type = static_cast<int>(e.type);
        out->pc = e.pc;
        out->dc = e.dc;
        out->state = e.state;
        out->payload_len = static_cast<int>(e.payload.size());
        out->payload2_len = static_cast<int>(e.payload2.size());

        // 缓冲不足：header（含两个精确长度）已填好，**不弹出**，扩容重试幂等。
        const bool fits1 = e.payload.empty() || (buf && cap >= static_cast<int>(e.payload.size()));
        const bool fits2 =
            e.payload2.empty() || (buf2 && cap2 >= static_cast<int>(e.payload2.size()));
        if (!fits1 || !fits2)
            return DCU_ERR_TOO_SMALL;

        if (!e.payload.empty())
            std::memcpy(buf, e.payload.data(), e.payload.size());
        if (!e.payload2.empty())
            std::memcpy(buf2, e.payload2.data(), e.payload2.size());

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
