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
            MessageLogged?.Invoke(level, message);

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
            MessageLogged?.Invoke(level, text);
            Debug.LogException(exception);
        }
    }
}
