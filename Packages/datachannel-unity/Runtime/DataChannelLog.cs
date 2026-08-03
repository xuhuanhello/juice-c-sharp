using System;
using DataChannelUnity.Internal;
using UnityEngine;

namespace DataChannelUnity
{
    public static class DataChannelLog
    {
        public static event Action<LogLevel, string> Message;

        private static LogLevel _level = LogLevel.Warning;
        private static bool _initialized;

        public static LogLevel Level
        {
            get => _level;
            set => SetLogLevel(value);
        }

        internal static void EnsureDefaults()
        {
            if (_initialized) return;
            _initialized = true;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            SetLogLevel(LogLevel.Info);
#else
            SetLogLevel(LogLevel.Warning);
#endif
        }

        public static void SetLogLevel(LogLevel level)
        {
            _level = level;
            try
            {
                if (DataChannelRuntime.IsNativeAvailable)
                    NativeMethods.dcu_set_log_level((int)level);
            }
            catch (DllNotFoundException)
            {
                // Native not present yet.
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        internal static void Emit(LogLevel level, string message)
        {
            if (level > _level || level == LogLevel.None) return;
            message = RedactIceCredentials(message);
            Message?.Invoke(level, message);

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
        /// Redacts userinfo in URIs (user:pass@host).
        /// </summary>
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
