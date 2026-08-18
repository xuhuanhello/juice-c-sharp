using System;
using DataChannelUnity.Internal;
using UnityEngine;

namespace DataChannelUnity
{
    public static class DataChannelLog
    {
        /// <summary>库产生的日志行。脱敏后投递。</summary>
        public static event Action<LogLevel, string> MessageLogged;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private static LogLevel _level = LogLevel.Info;
#else
        private static LogLevel _level = LogLevel.Warning;
#endif

        /// <summary>
        /// 泄漏诊断档位。Editor / Development 构建默认 <see cref="LeakDetectionMode.Enabled"/>，
        /// Release 默认 <see cref="LeakDetectionMode.Disabled"/>。
        /// </summary>
        /// <remarks>
        /// 它与终结器的条件编译是**叠加的两层**而非二选一：Release 里终结器整个不存在，
        /// 因此在 Release 下把它设成 <see cref="LeakDetectionMode.Enabled"/> 不会有任何效果。
        /// 详见 <see cref="LeakDetectionMode"/> 与 docs/SPEC.md §6。
        /// </remarks>
        public static LeakDetectionMode LeakDetection
        {
            get => _leakDetection;
            set
            {
                MainThread.Assert("DataChannelLog.LeakDetection");
                _leakDetection = value;
            }
        }

        private static LeakDetectionMode _leakDetection =
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            LeakDetectionMode.Enabled;
#else
            LeakDetectionMode.Disabled;
#endif

        // 每条日志新建一个未 Compiled 的 Regex 是原来的写法；改为静态 Compiled。
        private static readonly System.Text.RegularExpressions.Regex CredentialPattern =
            new System.Text.RegularExpressions.Regex(
                @"((?:stun|stuns|turn|turns):(?://)?)([^@/\s]+@)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// 日志级别。这是**唯一**的公开入口 —— 原先并存的 <c>SetLogLevel()</c> 方法已删除
        /// （.NET 准则反对属性与 Get/Set 方法对并存）。
        /// </summary>
        public static LogLevel Level
        {
            get => _level;
            set
            {
                MainThread.Assert("DataChannelLog.Level");
                _level = value;
                // **单向依赖**：本类只管托管状态，不知道 native 存在。
                // 把级别同步下去是 DataChannelRuntime 的事 —— 环就是这么剪断的，
                // 而不是靠两边各自的标志位「先置位才终止」那种隐含不变量。
                DataChannelRuntime.OnLogLevelChanged(value);
            }
        }

        internal static void Emit(LogLevel level, string message)
        {
            if (level > _level || level == LogLevel.None) return;
            message = RedactIceCredentials(message);
            RaiseLogged(level, message);

            switch (level)
            {
                case LogLevel.Fatal:
                case LogLevel.Error:
                    Debug.LogError("[DataChannelUnity] " + message);
                    break;
                case LogLevel.Warning:
                    Debug.LogWarning("[DataChannelUnity] " + message);
                    break;
                default:
                    Debug.Log("[DataChannelUnity] " + message);
                    break;
            }
        }

        /// <summary>
        /// 进程身份事实的出口：**绕过级别门禁**，其余处理照常（SPEC §7 的
        /// identity-banner 例外，#140 / #142）。
        /// </summary>
        /// <remarks>
        /// 它凭什么绕过门禁：分级通道回答「发生了什么」，本入口回答「跑的是什么」。
        /// 成员资格由两条边界**同时成立**来定 —— ① 每进程至多发一次；
        /// ② 回答「跑的是什么」而不是「发生了什么」。缺一不算。当前成员只有
        /// ABI 横幅一行；「至多一次」的 latch 归发行的调用方持有（本类只管托管
        /// 日志状态，不知道调用方的生命周期，见 SPEC §7 单向依赖）。
        ///
        /// 唯一让它闭嘴的档位是 <see cref="LogLevel.None"/>：None 是绝对静默，
        /// 是唯一一档用户明确表达了意图的值，其余档位都是替他猜的默认（#140 Q3）。
        /// 绕过的只有级别门禁，不是全部处理 —— 照常脱敏；照常经
        /// <see cref="MessageLogged"/> 派发，级别参数填 <see cref="LogLevel.Info"/>
        /// （那是它诚实的严重度，订阅者自己的过滤策略归订阅者）；走
        /// <see cref="Debug.Log"/> 而非 LogWarning / LogError —— 它不是告警。
        /// </remarks>
        internal static void EmitProcessIdentity(string message)
        {
            if (_level == LogLevel.None) return;
            message = RedactIceCredentials(message);
            RaiseLogged(LogLevel.Info, message);
            Debug.Log("[DataChannelUnity] " + message);
        }

        /// <summary>
        /// 派发 <see cref="MessageLogged"/>，**每订阅者隔离，且异常只能吞掉**。
        /// </summary>
        /// <remarks>
        /// 隔离的理由与其它事件相同（一个抛出的订阅者不该让后面的收不到）。
        /// 但这里多一条：捕获到的异常**不能再去记日志** —— 那会立刻回到本方法，
        /// 无限递归。所以这是全包唯一一处「吞掉异常」是正确做法的地方，
        /// 不是 <c>catch {}</c> 偷懒。
        /// </remarks>
        private static void RaiseLogged(LogLevel level, string message)
        {
            var handlers = MessageLogged;
            if (handlers == null) return;
            var list = handlers.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                try { ((Action<LogLevel, string>)list[i])(level, message); }
                catch { /* 只能吞：记它就会递归回到这里 */ }
            }
        }

        /// <summary>
        /// 对 URI 里的 userinfo（<c>user:pass@host</c>）脱敏。
        /// </summary>
        /// <remarks>
        /// internal：脱敏是库自己的职责，留在公开面会让人误以为那是调用方的活。
        /// 它的测试走**公开日志路径**，不开 <c>InternalsVisibleTo</c> —— 那样顺带
        /// 覆盖了「脱敏有没有被真正接进日志路径」，而直接调本方法测不到这一点。
        /// </remarks>
        internal static string RedactIceCredentials(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;
            // turn:user:pass@host → turn:***@host
            // Match both "turn:user:pass@host" and "turns://user:pass@host"
            return CredentialPattern.Replace(
                message, m => m.Groups[1].Value + "credentials=redacted@");
        }

        /// <summary>
        /// 记录一个异常，**保留完整栈**。
        /// </summary>
        /// <remarks>
        /// 订阅者异常是最需要完整栈的一类日志 —— 错误发生在应用代码里，
        /// 只给一行 <c>e.Message</c> 等于让人自己猜。走 <c>Debug.LogException</c>
        /// 以便 Console 里可点击跳转。
        /// </remarks>
        internal static void Emit(LogLevel level, string context, Exception exception)
        {
            if (level > _level || level == LogLevel.None) return;
            var text = RedactIceCredentials(context + ": " + exception);
            RaiseLogged(level, text);
            Debug.LogException(exception);
        }
    }
}
