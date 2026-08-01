using System;
using UnityEngine;

namespace PlayCard.Core
{
    /// <summary>
    /// Swallows ONE known, benign log-spam pattern from the Best.SignalR package: a NullReferenceException raised
    /// inside <c>HubConnection.SendMessage</c> while the library tears down a WebSocket that closed abnormally
    /// (server restart, network blip, app backgrounding, idle timeout). The connection self-recovers via the
    /// reconnect policy, but the library emits the NRE via <c>Debug.LogError</c> — which crash reporters
    /// (Crashlytics / Unity Cloud Diagnostics, both hooked on the log pipeline) record as a NON-FATAL. Pure noise.
    ///
    /// Installed once at boot as a wrapper around Unity's log handler. It forwards EVERYTHING except this exact
    /// signature, so genuine errors — including real Best.SignalR failures — are never hidden. Because the message
    /// is dropped at the handler (never reaching the native log), <c>Application.logMessageReceived</c> doesn't fire
    /// for it, so a crash reporter listening there won't capture it either.
    /// </summary>
    public sealed class BestNetLogFilter : ILogHandler
    {
        private readonly ILogHandler _inner;
        private static bool _installed;

        private BestNetLogFilter(ILogHandler inner) => _inner = inner;

        /// <summary>Install the filter once (idempotent). Call at boot, before any networking.</summary>
        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            Debug.unityLogger.logHandler = new BestNetLogFilter(Debug.unityLogger.logHandler);
        }

        public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
        {
            // The library logs via Debug.LogError(string) → LogFormat(Error, ctx, "{0}", <json blob>). Only inspect
            // error-level messages, and only build the string for those, so normal logging pays nothing.
            if (logType == LogType.Error && IsBenignSignalRTeardown(SafeFormat(format, args))) return;
            _inner.LogFormat(logType, context, format, args);
        }

        public void LogException(Exception exception, UnityEngine.Object context)
        {
            if (exception is NullReferenceException && IsBenignSignalRTeardown(exception.StackTrace)) return;
            _inner.LogException(exception, context);
        }

        // Narrow match: the NRE text AND the exact library method, so only THIS teardown race is dropped.
        private static bool IsBenignSignalRTeardown(string text)
            => !string.IsNullOrEmpty(text)
               && text.IndexOf("Object reference not set to an instance of an object", StringComparison.Ordinal) >= 0
               && text.IndexOf("HubConnection.SendMessage", StringComparison.Ordinal) >= 0;

        private static string SafeFormat(string format, object[] args)
        {
            if (args == null || args.Length == 0) return format;
            try { return string.Format(format, args); } catch { return format; }
        }
    }
}
