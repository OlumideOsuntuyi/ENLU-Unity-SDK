#nullable enable
using System;

namespace Enlu
{
    /// <summary>Log severity used by the SDK.</summary>
    public enum EnluLogLevel
    {
        /// <summary>Request traces (only when request logging is enabled).</summary>
        Debug = 0,
        /// <summary>Normal lifecycle messages.</summary>
        Info = 1,
        /// <summary>Recoverable problems.</summary>
        Warning = 2,
        /// <summary>Failures.</summary>
        Error = 3,
    }

    /// <summary>
    /// The SDK's logger. In Unity it writes to the Console. Session tickets, passwords and API keys
    /// are never passed to it.
    /// </summary>
    public static class EnluLog
    {
        /// <summary>Messages below this level are dropped.</summary>
        public static EnluLogLevel MinLevel { get; set; } = EnluLogLevel.Info;

        /// <summary>Where messages go. Replace to route SDK logs into your own logger; set to null to silence.</summary>
        public static Action<EnluLogLevel, string>? Sink { get; set; } = DefaultSink;

        internal static void Write(EnluLogLevel level, string message)
        {
            if (level < MinLevel) return;
            var sink = Sink;
            if (sink == null) return;
            try
            {
                sink(level, "[ENLU] " + message);
            }
            catch
            {
                // A broken log sink must never break a request.
            }
        }

        internal static void Debug(string m) => Write(EnluLogLevel.Debug, m);
        internal static void Info(string m) => Write(EnluLogLevel.Info, m);
        internal static void Warn(string m) => Write(EnluLogLevel.Warning, m);
        internal static void Error(string m) => Write(EnluLogLevel.Error, m);

        private static void DefaultSink(EnluLogLevel level, string message)
        {
#if UNITY_5_3_OR_NEWER
            switch (level)
            {
                case EnluLogLevel.Error: UnityEngine.Debug.LogError(message); break;
                case EnluLogLevel.Warning: UnityEngine.Debug.LogWarning(message); break;
                default: UnityEngine.Debug.Log(message); break;
            }
#else
            Console.WriteLine(level + " " + message);
#endif
        }
    }
}
