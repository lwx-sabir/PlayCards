using System.Diagnostics;
using Debug = UnityEngine.Debug;

namespace PlayCard.App
{
    /// <summary>
    /// Stopwatch for the Lobby → Table transition, so a slow join can be attributed to a SEGMENT rather than
    /// guessed at.
    ///
    /// Everything it writes goes through <c>Debug.Log</c>, which <c>DevLogRecorder</c> already captures into the
    /// session file — so a device build can simply Send Logs and the whole timeline comes back with it. That
    /// matters because the profiler is not available on a device that isn't attached to an editor, and reading the
    /// code has repeatedly failed to identify which part is actually slow.
    ///
    /// Each mark prints the time since the transition began AND the gap since the previous mark. The gap is the
    /// number that matters: it says which step cost the time, rather than how far in it appeared.
    /// </summary>
    public static class JoinTrace
    {
        private static readonly Stopwatch Clock = new Stopwatch();
        private static double _lastMs;
        private static string _lastLabel;

        /// <summary>Start (or restart) the trace. Called the moment the player commits to opening a table.</summary>
        public static void Begin(string what)
        {
            Clock.Restart();
            _lastMs = 0d;
            _lastLabel = "start";
            Debug.Log($"[JoinTrace] ---- BEGIN {what} ----");
        }

        /// <summary>Stamp a milestone. No-op before <see cref="Begin"/>, so stray marks can't print nonsense.</summary>
        public static void Mark(string label)
        {
            if (!Clock.IsRunning) return;

            double now = Clock.Elapsed.TotalMilliseconds;
            double gap = now - _lastMs;
            Debug.Log($"[JoinTrace] {now,8:0} ms total | +{gap,7:0} ms since '{_lastLabel}' | {label}");
            _lastMs = now;
            _lastLabel = label;
        }

        /// <summary>Final mark — the table is usable. Stops the clock so later frames don't keep logging.</summary>
        public static void End(string label = "table interactive")
        {
            if (!Clock.IsRunning) return;
            Mark(label);
            Debug.Log($"[JoinTrace] ---- END after {Clock.Elapsed.TotalMilliseconds:0} ms ----");
            Clock.Stop();
        }
    }
}
