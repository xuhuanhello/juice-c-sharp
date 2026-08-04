using System.Threading;
using UnityEngine;

namespace DataChannelUnity.Internal
{
    /// <summary>
    /// 主线程契约的执行点（SPEC §6 / #29 决议 5）。
    /// </summary>
    /// <remarks>
    /// **公开面全部限主线程，<c>Dispose</c> 也不例外。** 几乎整个 Unity API 都是这个
    /// 契约，所以它的学习成本是零；而备选方案「<c>Dispose</c> 只打标记、推迟到 pump
    /// 执行」恰好在最需要它的三个时刻失效 —— 编辑模式、应用退出、域重载之后，
    /// 泵可能再也不转了，「推迟销毁」就变成「永不销毁」。
    ///
    /// 断言用 <see cref="System.Diagnostics.ConditionalAttribute"/> 而不是 <c>#if</c>
    /// 包住方法体：前者让**调用点**在 Release 里一并消失，后者留下一串空调用。
    /// </remarks>
    internal static class MainThread
    {
        private const int Uncaptured = 0;

        // ManagedThreadId 从 1 起，0 因此可以安全地当「还没抓到」用。
        private static int _mainThreadId = Uncaptured;
        private static bool _warnedUncaptured;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void CaptureInPlayer() => Capture();

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void CaptureInEditor() => Capture();
#endif

        /// <summary>
        /// 记下当前线程为主线程。两个入口都是 Unity 保证在主线程上跑的：
        /// 播放器走 <c>SubsystemRegistration</c>，编辑器走 <c>InitializeOnLoadMethod</c>
        /// —— 后者是 EditMode 测试唯一会经过的那个（<c>RuntimeInitializeOnLoadMethod</c>
        /// 在编辑模式下根本不触发）。
        /// </summary>
        private static void Capture()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>
        /// 断言调用发生在主线程。<paramref name="api"/> 是出现在错误里的成员名。
        /// </summary>
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        internal static void Assert(string api)
        {
            var main = _mainThreadId;
            if (main == Uncaptured)
            {
                // 两个 Capture 入口都没跑过 —— 这本身是缺陷，不是「没法判断所以放行」。
                // 让它出声一次，而不是永远沉默地放行（CONTRIBUTING：缺席必须是失败）。
                if (!_warnedUncaptured)
                {
                    _warnedUncaptured = true;
                    DataChannelLog.Emit(LogLevel.Error,
                        "主线程标识从未被捕获，" + api + " 的线程断言无法执行。"
                        + "这是本包的缺陷，请附 Unity 版本提 issue。");
                }
                return;
            }

            if (Thread.CurrentThread.ManagedThreadId == main) return;

            throw new System.InvalidOperationException(
                api + " 只能在 Unity 主线程调用（当前线程 "
                + Thread.CurrentThread.ManagedThreadId + "，主线程 " + main + "）。"
                + "本包的公开面**全部**限主线程，Dispose 也不例外 —— 见 docs/SPEC.md §6。"
                + "若要从后台线程释放，请把调用调度回主线程。");
        }
    }
}
