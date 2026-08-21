using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Best.SignalR;
using Best.SignalR.Authentication;
using Best.SignalR.Encoders;
using PlayCard.Account;
using PlayCard.App;        // NetworkStatus — global connection state for the reconnect overlay
using PlayCard.Core;
using PlayCard.Game.Dtos;
using UnityEngine;

namespace PlayCard.Game.Net
{
    /// <summary>
    /// SignalR implementation of <see cref="IBlackjackHubClient"/>, on the <b>Best SignalR</b> (v3) transport. Works on
    /// IL2CPP (Android/iOS) AND WebGL — the reason we moved off Microsoft SignalR (which was dead on IL2CPP). Every
    /// <c>Best.SignalR</c> type is confined to THIS file; game code depends only on the interface.
    ///
    /// Threading: Best SignalR raises <c>On&lt;T&gt;</c> + lifecycle callbacks on the Best HTTP background thread, so board
    /// snapshots and connection changes are queued here and re-raised on Unity's main thread in <see cref="Update"/>.
    ///
    /// ⚠ Encoder: uses the built-in <see cref="LitJsonEncoder"/>. If board fields arrive null/default at runtime it's a
    /// JSON casing mismatch — switch that one line to <c>JsonDotNetEncoder</c> (Newtonsoft is already vendored; needs the
    /// <c>BEST_SIGNALR_ENABLE_NEWTONSOFT_JSON_DOTNET_ENCODER</c> define).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SignalRBlackjackHubClient : MonoBehaviour, IBlackjackHubClient
    {
        [Header("Hub")]
        [Tooltip("Server hub URL, e.g. https://host/blackjackhub. Leave the localhost:5000 placeholder to use AppConfig.")]
        [SerializeField] private string hubUrl = "http://localhost:5000/blackjackhub";

        // --- IBlackjackHubClient events (always raised on the Unity main thread, from Update) ---
        public event Action<BoardSnapshot> OnTableUpdated;
        public event Action OnConnected;
        public event Action<string> OnDisconnected;

        public bool IsConnected => _connected;

        /// <summary>Supplies the JWT for the [Authorize] hub. Defaults to AccountManager's cached token.</summary>
        public Func<string> TokenProvider { get; set; }

        private HubConnection _hub;
        private volatile bool _connected;

        // --- reconnect watchdog ---------------------------------------------------------------------------------
        // Best SignalR's ReconnectPolicy retries a BOUNDED number of times and then fires OnClosed for good. Nothing
        // reopened the connection after that, so one long tunnel / backgrounded app / server restart left the table
        // permanently frozen — still rendering, never updating again, with no way back short of leaving the table.
        // These drive a retry loop that outlives the policy, for as long as the caller still wants a connection.
        private bool _wantConnected;      // set by ConnectAsync, cleared by DisconnectAsync — "should we be up?"
        private bool _reconnecting;       // an attempt is in flight; don't stack another
        // ⚠️ THE WATCHDOG MUST ONLY RUN ONCE BEST HAS GIVEN UP (OnClosed). Best raises OnReconnecting while it is
        // still retrying, which also reads as "not connected" — acting on that tore down the connection Best was
        // about to restore. The hub then churned, hub Heartbeats stopped landing, the server's stalled-seat reaper
        // evicted the player, and because §5 defers pulling a seat that has a live stake, the eviction surfaced the
        // instant the round settled: "kicked to the lobby right after the dealer reveals". Do not widen this.
        private bool _policyGaveUp;
        private int _retry;               // consecutive failures, for the backoff curve
        private float _nextAttemptAt;     // unscaled time of the next attempt
        private readonly ConcurrentQueue<BoardSnapshot> _boards = new ConcurrentQueue<BoardSnapshot>();
        private readonly ConcurrentQueue<ConnChange> _conn = new ConcurrentQueue<ConnChange>();

        private readonly struct ConnChange
        {
            public readonly bool Connected;
            public readonly string Reason;
            /// <summary>Best's own reconnect policy is FINISHED with this connection (OnClosed) — as opposed to
            /// OnReconnecting/OnError, where it is still working and must be left alone.</summary>
            public readonly bool Terminal;

            public ConnChange(bool connected, string reason, bool terminal = false)
            {
                Connected = connected;
                Reason = reason;
                Terminal = terminal;
            }
        }

        private void Awake()
        {
            TokenProvider ??= () => AccountManager.Instance != null ? AccountManager.Instance.JwtToken : null;
        }

        public async Task ConnectAsync()
        {
            _wantConnected = true;   // from here on, any close that isn't ours is something to recover from
            if (_hub == null) Build();
            try
            {
                await EnsureTokenAsync();
                await _hub.ConnectAsync();
                _connected = true;   // immediate; the OnConnected event (queued) also re-fires our event in Update
                _retry = 0;
            }
            catch (Exception ex)
            {
                // Arm the watchdog. A connect that never OPENED won't raise OnClosed, so without this the retry loop
                // stays disarmed and a failed FIRST connect is permanent — the table then runs REST-only for the whole
                // session with no live pushes and no way back.
                _policyGaveUp = true;
                _conn.Enqueue(new ConnChange(false, ex.Message));
                throw;
            }
        }

        public Task DisconnectAsync()
        {
            _wantConnected = false;   // deliberate — the watchdog must NOT drag it back up
            _hub?.StartClose();       // graceful; OnClosed fires → queued → Update raises OnDisconnected
            return Task.CompletedTask;
        }

        /// <summary>
        /// Make sure the token the authenticator is about to read is actually valid.
        ///
        /// <see cref="TokenProvider"/> is synchronous and hands back whatever <see cref="AccountManager"/> has cached,
        /// so a connect attempt that lands after expiry but before the background refresh has run will 401 the
        /// handshake — and each failed handshake burns one of the reconnect policy's finite attempts, which is how a
        /// recoverable blip turns into a dead connection. Refreshing here costs nothing when the token is healthy
        /// (a clock compare), and is bounded so a stalled refresh can't wedge the connect the way it wedged REST.
        /// </summary>
        private static async Task EnsureTokenAsync()
        {
            if (AccountManager.Instance == null) return;
            try
            {
                var gate = AccountManager.Instance.EnsureValidTokenAsync();
                var budget = Task.Delay(TimeSpan.FromSeconds(Mathf.Max(1, AppConfig.Instance.RequestTimeoutSeconds)));
                if (await Task.WhenAny(gate, budget) == budget)
                    _ = gate.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex)
            {
                // Never let auth trouble stop us ATTEMPTING — a stale token that 401s is still better than not trying.
                Debug.LogWarning($"[Hub] token refresh before connect failed: {ex.Message}");
            }
        }

        public Task JoinTableAsync(string tableId)    => Send("JoinTable", tableId);
        public Task LeaveTableAsync(string tableId)   => Send("LeaveTable", tableId);

        /// <summary>
        /// Ask for the current board. Falls back to REST while the socket is down.
        ///
        /// Send() is a silent no-op when not connected, which is right for fire-and-forget hub chatter and WRONG for
        /// this: the client asks for a board precisely when it believes it has missed something. During a reconnect it
        /// would ask, be told nothing, and sit on a stale board — which is a dealer who has already settled server-side
        /// standing there not revealing until the socket comes back.
        /// </summary>
        public async Task RequestBoardAsync(string tableId)
        {
            if (await TrySend("RequestBoard", tableId)) return;

            var res = await BlackjackRestClient.Instance.GetBoardAsync(tableId);
            if (res.Ok && res.Value != null) OnTableUpdated?.Invoke(res.Value);
        }

        /// <summary>
        /// Seated keep-alive. Falls back to REST while the socket is down — and that fallback is the whole point.
        ///
        /// A hub Send() while disconnected returns having sent NOTHING and reported nothing, so the caller's 5s loop
        /// keeps ticking in the belief that the seat is alive while the server hears silence. Its reaper drops the seat
        /// at Table:StalledTimeoutSeconds (30s), so roughly six quiet ticks into a reconnect the player is removed from
        /// a table they are still sitting at and still playing. REST is a separate path that keeps working through a
        /// socket outage, which is exactly the condition this has to survive.
        /// </summary>
        public async Task HeartbeatAsync(string tableId)
        {
            if (await TrySend("Heartbeat", tableId)) return;
            await BlackjackRestClient.Instance.HeartbeatAsync(tableId);
        }

        private Task Send(string method, string tableId) => TrySend(method, tableId);

        /// <summary>
        /// Send over the hub and SAY whether it actually went. The flag alone is not enough to decide that: a drop is
        /// discovered by a send THROWING, and _connected only turns false once Best has raised the change and Update
        /// has processed the queue. In that window a send fails, the exception is swallowed as an expected close, and
        /// a caller checking only the flag believes it succeeded — which is how the heartbeat went missing while the
        /// fallback that exists to cover it never ran.
        /// </summary>
        private async Task<bool> TrySend(string method, string tableId)
        {
            if (!_connected || _hub == null) return false;
            try { await _hub.SendAsync(method, tableId); return true; }
            catch (Exception ex)
            {
                // A send that races a closing connection ("Connection closed unexpectedly") is EXPECTED on any drop —
                // the reconnect policy recovers, so it's not worth a warning. Only surface unexpected send failures.
                if (!IsExpectedClose(ex)) Debug.LogWarning($"[Hub] {method} failed: {ex.Message}");
                return false;
            }
        }

        private static bool IsExpectedClose(Exception ex)
            => ex?.Message != null &&
               (ex.Message.IndexOf("closed", StringComparison.OrdinalIgnoreCase) >= 0
                || ex.Message.IndexOf("not connected", StringComparison.OrdinalIgnoreCase) >= 0);

        // Prefer an explicit, non-placeholder inspector URL; otherwise use the shared AppConfig.
        private string ResolveHubUrl()
            => !string.IsNullOrWhiteSpace(hubUrl) && !hubUrl.Contains("localhost:5000")
                ? hubUrl
                : AppConfig.Instance.HubUrl;

        private void Build()
        {
            IProtocol protocol = new JsonProtocol(new LitJsonEncoder());
            var options = new HubOptions { PreferedTransport = TransportTypes.WebSocket, SkipNegotiation = false };
            _hub = new HubConnection(new Uri(ResolveHubUrl()), protocol, options);

            // Inject our EXISTING JWT (from AccountManager) — Authorization header on native, ?access_token= on WebGL.
            _hub.AuthenticationProvider = new BearerTokenAuthenticator(() => TokenProvider?.Invoke());
            _hub.ReconnectPolicy = new DefaultRetryPolicy();

            // Server → client board snapshot. Queued here (background thread), raised in Update() (main thread).
            _hub.On<BoardSnapshot>("TableUpdated", snapshot => _boards.Enqueue(snapshot));

            _hub.OnConnected    += _         => _conn.Enqueue(new ConnChange(true, null));
            _hub.OnReconnected  += _         => _conn.Enqueue(new ConnChange(true, null));
            _hub.OnReconnecting += (_, r)    => _conn.Enqueue(new ConnChange(false, r ?? "reconnecting"));
            // ONLY OnClosed is terminal. OnReconnecting/OnError mean Best is still working the problem — the watchdog
            // must not touch the connection then, or it tears down the one Best is about to restore.
            _hub.OnClosed       += _         => _conn.Enqueue(new ConnChange(false, "closed", terminal: true));
            _hub.OnError        += (_, err)  => _conn.Enqueue(new ConnChange(false, err ?? "error"));
        }

        private void Update()
        {
            while (_boards.TryDequeue(out var board))
                OnTableUpdated?.Invoke(board);

            while (_conn.TryDequeue(out var change))
            {
                _connected = change.Connected;
                if (change.Connected)
                {
                    _retry = 0;
                    _policyGaveUp = false;   // Best is healthy again — the watchdog stands down
                    NetworkStatus.Report(NetState.Online);
                    OnConnected?.Invoke();
                }
                else
                {
                    // Arm the watchdog ONLY on a terminal close. While Best is mid-reconnect, leave its connection
                    // alone — see _policyGaveUp.
                    if (change.Terminal) _policyGaveUp = true;

                    // Only call it "reconnecting" while we actually intend to be up; a deliberate leave is just offline.
                    NetworkStatus.Report(_wantConnected ? NetState.Reconnecting : NetState.Offline, change.Reason);
                    OnDisconnected?.Invoke(change.Reason);
                }
            }

            PumpReconnect();
        }

        /// <summary>
        /// Keep trying to get back up after Best SignalR's own policy has given up, with exponential backoff capped so
        /// a long outage settles into a slow poll rather than hammering. Runs from Update (main thread) and only while
        /// <see cref="_wantConnected"/> — leaving the table stops it immediately.
        /// </summary>
        private void PumpReconnect()
        {
            // While we're up, keep the flag clear. Rebuilding closes the OLD connection, and that late "closed" can
            // land after the NEW one is already connected — leaving the watchdog armed for the next ordinary blip,
            // which is precisely the state that caused it to fight Best's reconnect.
            if (_connected) { _policyGaveUp = false; return; }

            if (!_wantConnected || _reconnecting) return;
            if (!_policyGaveUp) return;   // Best is still retrying — hands off (see the field comment)
            if (Time.unscaledTime < _nextAttemptAt) return;

            _reconnecting = true;
            NetworkStatus.Report(NetState.Reconnecting, "retrying");
            _ = AttemptReconnect();
        }

        private async Task AttemptReconnect()
        {
            try
            {
                // Rebuild rather than reuse: a HubConnection that has run out of retries is terminal, and calling
                // ConnectAsync on it again just throws.
                try { _hub?.StartClose(); } catch { /* already dead */ }
                _hub = null;

                await ConnectAsync();   // refreshes the token first, and resets _retry on success
            }
            catch (Exception ex)
            {
                _retry++;
                // 2s, 4s, 8s, 16s, capped at 30s. Unbounded ATTEMPTS (the player may sit on a dead network for
                // minutes and should still recover), just never faster than this.
                float wait = Mathf.Min(30f, 2f * Mathf.Pow(2f, Mathf.Min(_retry - 1, 4)));
                _nextAttemptAt = Time.unscaledTime + wait;
                Debug.LogWarning($"[Hub] reconnect attempt {_retry} failed ({ex.Message}); retrying in {wait:0}s.");
            }
            finally { _reconnecting = false; }
        }

        private void OnDestroy()
        {
            _wantConnected = false;   // stop the watchdog before we drop the connection
            try { _hub?.StartClose(); } catch { /* shutting down */ }
            _hub = null;
        }
    }

    /// <summary>
    /// Injects an ALREADY-HELD JWT into Best SignalR's negotiate/reconnect requests — no token fetch (the token comes
    /// from <see cref="AccountManager"/>). Header on native platforms; <c>?access_token=</c> on WebGL (browsers can't set
    /// a WS handshake header). The token is read fresh from the provider on every request, so a refresh is picked up.
    /// </summary>
    internal sealed class BearerTokenAuthenticator : IAuthenticationProvider
    {
        private readonly Func<string> _token;
        public BearerTokenAuthenticator(Func<string> token) { _token = token; }

        public bool IsPreAuthRequired => false;   // nothing to fetch — we already have the JWT

        public event OnAuthenticationSuccededDelegate OnAuthenticationSucceded;
#pragma warning disable 67 // required by IAuthenticationProvider; we already hold the JWT so auth never fails → never raised
        public event OnAuthenticationFailedDelegate OnAuthenticationFailed;
#pragma warning restore 67

        public void StartAuthentication() => OnAuthenticationSucceded?.Invoke(this);

        public void PrepareRequest(Best.HTTP.HTTPRequest request)
        {
            var t = _token?.Invoke();
            if (!string.IsNullOrEmpty(t)) request.SetHeader("Authorization", "Bearer " + t);
        }

        public Uri PrepareUri(Uri uri)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            var t = _token?.Invoke();
            if (string.IsNullOrEmpty(t)) return uri;
            string q = string.IsNullOrEmpty(uri.Query) ? "?" : uri.Query + "&";
            return new UriBuilder(uri.Scheme, uri.Host, uri.Port, uri.AbsolutePath, q + "access_token=" + t).Uri;
#else
            return uri;
#endif
        }

        public void Cancel() { }
    }
}
