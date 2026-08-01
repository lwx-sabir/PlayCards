using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Best.SignalR;
using Best.SignalR.Authentication;
using Best.SignalR.Encoders;
using PlayCard.Account;
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
        private readonly ConcurrentQueue<BoardSnapshot> _boards = new ConcurrentQueue<BoardSnapshot>();
        private readonly ConcurrentQueue<ConnChange> _conn = new ConcurrentQueue<ConnChange>();

        private readonly struct ConnChange
        {
            public readonly bool Connected;
            public readonly string Reason;
            public ConnChange(bool connected, string reason) { Connected = connected; Reason = reason; }
        }

        private void Awake()
        {
            TokenProvider ??= () => AccountManager.Instance != null ? AccountManager.Instance.JwtToken : null;
        }

        public async Task ConnectAsync()
        {
            if (_hub == null) Build();
            try
            {
                await _hub.ConnectAsync();
                _connected = true;   // immediate; the OnConnected event (queued) also re-fires our event in Update
            }
            catch (Exception ex)
            {
                _conn.Enqueue(new ConnChange(false, ex.Message));
                throw;
            }
        }

        public Task DisconnectAsync()
        {
            _hub?.StartClose();     // graceful; OnClosed fires → queued → Update raises OnDisconnected
            return Task.CompletedTask;
        }

        public Task JoinTableAsync(string tableId)    => Send("JoinTable", tableId);
        public Task LeaveTableAsync(string tableId)   => Send("LeaveTable", tableId);
        public Task RequestBoardAsync(string tableId) => Send("RequestBoard", tableId);
        public Task HeartbeatAsync(string tableId)    => Send("Heartbeat", tableId);

        private async Task Send(string method, string tableId)
        {
            if (!_connected || _hub == null) return;
            try { await _hub.SendAsync(method, tableId); }
            catch (Exception ex)
            {
                // A send that races a closing connection ("Connection closed unexpectedly") is EXPECTED on any drop —
                // the reconnect policy recovers, so it's not worth a warning. Only surface unexpected send failures.
                if (!IsExpectedClose(ex)) Debug.LogWarning($"[Hub] {method} failed: {ex.Message}");
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
            _hub.OnClosed       += _         => _conn.Enqueue(new ConnChange(false, "closed"));
            _hub.OnError        += (_, err)  => _conn.Enqueue(new ConnChange(false, err ?? "error"));
        }

        private void Update()
        {
            while (_boards.TryDequeue(out var board))
                OnTableUpdated?.Invoke(board);

            while (_conn.TryDequeue(out var change))
            {
                _connected = change.Connected;
                if (change.Connected) OnConnected?.Invoke();
                else OnDisconnected?.Invoke(change.Reason);
            }
        }

        private void OnDestroy()
        {
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
