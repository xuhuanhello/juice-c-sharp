#pragma once

// 日志队列（SPEC §7 / #33 决议 1）。
//
// 与 DcuEventQueue **语义相反**，两者不能合并：
//   控制事件：无界，永不丢 —— 丢一条 DcClosed 就是永久失同步，没有下一条来补。
//   日志行：  有界 1024，丢最旧 + 计数 —— 日志是可丢的，而写日志的那条路径
//             **持着上游的锁**，进程内所有线程的每一条日志都串行经过它。
//             这个上界与其说是内存保护，不如说是在保护那条临界路径。

#include <cstring>
#include <deque>
#include <mutex>
#include <string>

#include "dcu.h"

class DcuLogQueue {
public:
    static constexpr size_t kLimit = 1024;

    // 由上游的日志回调调用 —— 持锁、每条日志都经过。必须极快、绝不阻塞。
    void push(int level, std::string message) {
        std::lock_guard<std::mutex> lock(mu_);
        if (q_.size() >= kLimit) {
            q_.pop_front();
            ++dropped_;
        }
        q_.emplace_back(level, std::move(message));
    }

    // 契约与 dcu_event_next 一致：TOO_SMALL 填长度且**不消费**。
    // out_dropped 带出「自上次读取以来丢弃的条数」并清零 —— 即使队列为空也填，
    // 这样 pump 排空到 NOT_AVAIL 时仍能拿到丢弃计数。
    int next(int *out_level, void *buf, int cap, int *out_len, int *out_dropped) {
        if (!out_level || !out_len || !out_dropped)
            return DCU_ERR_INVALID;

        std::lock_guard<std::mutex> lock(mu_);
        *out_level = 0;
        *out_len = 0;
        *out_dropped = static_cast<int>(dropped_);

        if (q_.empty()) {
            dropped_ = 0;
            return DCU_ERR_NOT_AVAIL;
        }

        const auto &e = q_.front();
        *out_level = e.first;
        *out_len = static_cast<int>(e.second.size());

        if (!buf || cap < static_cast<int>(e.second.size()))
            return DCU_ERR_TOO_SMALL; // 不消费，dropped_ 也不清

        if (!e.second.empty())
            std::memcpy(buf, e.second.data(), e.second.size());
        q_.pop_front();
        dropped_ = 0;
        return DCU_OK;
    }

    void clear() {
        std::lock_guard<std::mutex> lock(mu_);
        q_.clear();
        dropped_ = 0;
    }

private:
    std::mutex mu_;
    std::deque<std::pair<int, std::string>> q_;
    size_t dropped_ = 0;
};
