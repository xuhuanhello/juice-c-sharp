#pragma once

// dcu 自有句柄表（决策 #42 / SPEC §2）。
//
// 迁移到 libdatachannel 的 C++ API 之后，`src/capi.cpp` 里那张 int -> shared_ptr
// 的私有 map 我们够不着，句柄分配、验活、类型区分、跨线程表锁全部由本表接手。
//
// 两条不变量：
//
//   1. 句柄单调递增、**永不复用**，PC 与 DC 共用同一个计数器。
//      与上游 capi 的 `lastId` 同形（#28 研究 §3.4）：陈旧事件必然查不到句柄，
//      因而只会被静默丢弃，不可能错投递到后来的对象上。
//
//   2. **销毁绝不在持锁状态下发生。** `~PeerConnection` / `~DataChannel` 会阻塞
//      等待回调收敛，而回调自己要进本表查句柄 —— 持锁销毁 = 自死锁。
//      所有摘除路径都先把 shared_ptr move 出来、解锁、再让它在锁外析构。

#include <limits>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <unordered_map>
#include <utility>
#include <vector>

#include <rtc/rtc.hpp>

class DcuHandleTable {
public:
    int add_pc(std::shared_ptr<rtc::PeerConnection> pc) {
        std::lock_guard<std::mutex> lk(mu_);
        guard_exhaustion();
        int h = next_++;
        pcs_.emplace(h, std::move(pc));
        return h;
    }

    int add_dc(std::shared_ptr<rtc::DataChannel> dc) {
        std::lock_guard<std::mutex> lk(mu_);
        guard_exhaustion();
        int h = next_++;
        dcs_.emplace(h, std::move(dc));
        return h;
    }

    // 查不到即抛 std::invalid_argument —— 与上游 `getPeerConnection` / `getChannel`
    // 同形，好让它经 dcu_wrap 落到与迁移前一致的错误码上。
    std::shared_ptr<rtc::PeerConnection> get_pc(int h) {
        std::lock_guard<std::mutex> lk(mu_);
        auto it = pcs_.find(h);
        if (it == pcs_.end())
            throw std::invalid_argument("PeerConnection handle does not exist");
        return it->second;
    }

    std::shared_ptr<rtc::DataChannel> get_dc(int h) {
        std::lock_guard<std::mutex> lk(mu_);
        auto it = dcs_.find(h);
        if (it == dcs_.end())
            throw std::invalid_argument("DataChannel handle does not exist");
        return it->second;
    }

    bool erase_pc(int h) { return erase_from(pcs_, h); }
    bool erase_dc(int h) { return erase_from(dcs_, h); }

    // 返回被丢弃的对象数。与上游 `eraseAll()` 同位置调用（shutdown 时）。
    size_t clear() {
        std::vector<std::shared_ptr<rtc::PeerConnection>> pcs;
        std::vector<std::shared_ptr<rtc::DataChannel>> dcs;
        {
            std::lock_guard<std::mutex> lk(mu_);
            pcs.reserve(pcs_.size());
            dcs.reserve(dcs_.size());
            for (auto &kv : pcs_)
                pcs.push_back(std::move(kv.second));
            for (auto &kv : dcs_)
                dcs.push_back(std::move(kv.second));
            pcs_.clear();
            dcs_.clear();
        }
        // 锁已释放：此处的析构可以安全地阻塞等回调收敛。
        // DataChannel 先于 PeerConnection 释放，避免 DC 析构时其 PC 已经走远。
        size_t n = pcs.size() + dcs.size();
        dcs.clear();
        pcs.clear();
        return n;
    }

private:
    // #151：句柄空间 2^31，按进程生命周期设计。`next_` 到顶时再 ++ 是有符号溢出
    // UB，回绕后「永不复用」在数学上失效 —— 与其未定义，不如响亮：到顶即抛，
    // 经 dcu_wrap 落 DCU_ERR_FAILURE（回调路径由调用方自己接住，见 dcu_impl）。
    // 量级注记：每秒 1000 次创建也要 ~25 天才耗尽，游戏客户端不可达。
    void guard_exhaustion() const {
        if (next_ == std::numeric_limits<int>::max())
            throw std::runtime_error(
                "dcu handle space exhausted: 2^31 handles were allocated in this process. "
                "Handles are monotonic and never reused by design (stale-event safety).");
    }

    template <typename Map> bool erase_from(Map &m, int h) {
        typename Map::mapped_type keep;
        {
            std::lock_guard<std::mutex> lk(mu_);
            auto it = m.find(h);
            if (it == m.end())
                return false;
            keep = std::move(it->second);
            m.erase(it);
        }
        return true; // keep 在锁外析构
    }

    std::mutex mu_;
    std::unordered_map<int, std::shared_ptr<rtc::PeerConnection>> pcs_;
    std::unordered_map<int, std::shared_ptr<rtc::DataChannel>> dcs_;
    int next_ = 1; // 与上游 lastId 一样从 1 起；0 不是合法句柄
};
