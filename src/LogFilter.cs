using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Zandatsu
{
    /// <summary>
    /// Drops one warning the game logs from EffectAudio.Play: "Tried to use haptic audio, but no amplitude curve was
    /// available". The Outer Rim's lightsaber effects ask for haptic audio without curves, and a lightsaber passing through
    /// a body cut into many pieces logged it up to 2000 times a second, each with a stack trace: the game froze. Filtered
    /// before Unity's own handler, so no stack trace is taken. Everything else passes through untouched; how many were
    /// dropped is logged once a minute.
    /// </summary>
    public sealed class LogFilter : ILogHandler
    {
        private const string Haptic = "Warning: Tried to use haptic audio, but no amplitude curve was available";
        private const float ReportSeconds = 60f;

        private readonly ILogHandler inner;
        private static LogFilter installed;
        private static int dropped;
        private static float nextReport;

        private LogFilter(ILogHandler inner) { this.inner = inner; }

        public static void Install()
        {
            if (installed != null || Debug.unityLogger.logHandler is LogFilter) return;
            installed = new LogFilter(Debug.unityLogger.logHandler);
            Debug.unityLogger.logHandler = installed;
        }

        public static void Uninstall()
        {
            if (installed == null) return;
            if (ReferenceEquals(Debug.unityLogger.logHandler, installed)) Debug.unityLogger.logHandler = installed.inner;
            installed = null;
        }

        public void LogFormat(LogType logType, Object context, string format, params object[] args)
        {
            if (logType == LogType.Warning && IsHaptic(format, args))
            {
                dropped++;
                return;
            }
            inner.LogFormat(logType, context, format, args);
        }

        public void LogException(Exception exception, Object context) => inner.LogException(exception, context);

        private static bool IsHaptic(string format, object[] args)
        {
            if (format != null && format.StartsWith(Haptic, StringComparison.Ordinal)) return true;
            return args != null && args.Length > 0 && args[0] is string s && s.StartsWith(Haptic, StringComparison.Ordinal);
        }

        /// <summary>Every frame: once a minute, how many were dropped (if any).</summary>
        public static void Update()
        {
            if (Time.unscaledTime < nextReport) return;
            nextReport = Time.unscaledTime + ReportSeconds;
            if (dropped == 0) return;
            int n = dropped;
            dropped = 0;
            Debug.Log($"[Zandatsu] dropped {n} game warnings \"Tried to use haptic audio...\" in the last minute (a lightsaber or other effect asking for haptics it has no curve for)");
        }
    }
}
