using System;
using UnityEngine;

namespace PlayCard.App
{
    /// <summary>How the client currently stands with the server.</summary>
    public enum NetState
    {
        /// <summary>Live push connection is up. Nothing to show.</summary>
        Online = 0,
        /// <summary>The connection dropped and we are actively trying to get it back. Show the overlay.</summary>
        Reconnecting = 1,
        /// <summary>Not connected and not currently attempting — e.g. we haven't joined a table yet.</summary>
        Offline = 2,
    }

    /// <summary>
    /// GLOBAL connection-state broker. One place for "are we talking to the server", so UI can react without
    /// knowing who owns the socket.
    ///
    /// This exists because connection state lives on <c>TableController</c>, which is scene-scoped and only present
    /// in the Table scene — a global overlay prefab that survives scene loads can't reach it, and shouldn't have to.
    /// Producers (the hub client today; the REST client later if we want it) call <see cref="Report"/>; consumers
    /// subscribe to <see cref="OnChanged"/>. Neither side references the other.
    ///
    /// Self-instantiates before the first scene so a reporter can never fire before there is somewhere to report to,
    /// and survives scene loads so a drop during a Home → Table transition isn't lost.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NetworkStatus : MonoBehaviour
    {
        public static NetworkStatus Instance { get; private set; }

        /// <summary>Current state. Defaults to <see cref="NetState.Online"/> so nothing shows until a real drop.</summary>
        public NetState State { get; private set; } = NetState.Online;

        /// <summary>Why we're in this state (a close reason, an exception message). May be null.</summary>
        public string Reason { get; private set; }

        /// <summary>Raised on the MAIN thread whenever <see cref="State"/> changes. Never fires for a no-op report.</summary>
        public event Action<NetState, string> OnChanged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;
            var go = new GameObject("[NetworkStatus]");
            DontDestroyOnLoad(go);
            go.AddComponent<NetworkStatus>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Report the current state. Safe to call every attempt — identical reports are dropped, so a retry loop can
        /// report on each pass without spamming subscribers. MAIN THREAD ONLY: producers that live on a background
        /// thread (Best HTTP raises its callbacks off-thread) must marshal first.
        /// </summary>
        public static void Report(NetState state, string reason = null)
        {
            var inst = Instance;
            if (inst == null) return;                       // torn down during shutdown — nothing to tell
            if (inst.State == state) { inst.Reason = reason; return; }

            inst.State = state;
            inst.Reason = reason;
            inst.OnChanged?.Invoke(state, reason);
        }
    }
}
