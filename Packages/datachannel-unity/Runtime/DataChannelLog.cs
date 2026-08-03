using System;
using DataChannelUnity.Internal;
using UnityEngine;

namespace DataChannelUnity
{
    public static class DataChannelLog
    {
        /// <summary>库产生的日志行。脱敏后投递。</summary>
        public static event Action<LogLevel, string> MessageLogged;

        private static LogLevel _level = LogLevel.Warning;
        private static bool _initialized;

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
                try
                {
                    if (DataChannelRuntime.IsNativeAvailable)
                        NativeMethods.dcu_set_log_level((int)value);
                }
                catch (DllNotFoundException)
                {
                    // 原生插件尚未就位。
                }
                catch (EntryPointNotFoundException)
                {
                }
            }
        }

        internal static void EnsureDefaults()
        {
            if (_initialized) return;
            _initialized = true;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Level = LogLevel.Info;
#else
            Level = LogLevel.Warning;
#endif
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
        /// #34 决议 2 定了它收 internal。**S0 暂不收**：它唯一的测试目前直接调它，
        /// 而替代方案（经公开日志入口打一条含凭证的日志再断言输出）要等日志桥
        /// 落地才成立 —— 在那之前没有任何原生失败会流到 DataChannelLog。
        /// 现在收 internal 只能二选一：开 InternalsVisibleTo（#39 已否），
        /// 或删掉测试静默失去覆盖。两者都比晚收一步差。随 S5 一并收。
        /// </remarks>
        public static string RedactIceCredentials(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;
            // turn:user:pass@host → turn:***@host
            // Match both "turn:user:pass@host" and "turns://user:pass@host"
            return System.Text.RegularExpressions.Regex.Replace(
                message,
                @"((?:stun|stuns|turn|turns):(?://)?)([^@/\s]+@)",
                m => m.Groups[1].Value + "credentials=redacted@",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
    }
}
