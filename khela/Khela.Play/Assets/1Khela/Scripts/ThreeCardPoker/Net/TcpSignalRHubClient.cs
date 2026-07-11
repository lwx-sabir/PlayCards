using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Best.SignalR;
using Best.SignalR.Authentication;
using Best.SignalR.Encoders;
using PlayCard.Account;
using PlayCard.Core;
using PlayCard.ThreeCardPoker.Dtos;
using UnityEngine;

namespace PlayCard.ThreeCardPoker.Net
{
    /// <summary>
    /// SignalR implementation of <see cref="ITcpHubClient"/> on the <b>Best SignalR</b> (v3) transport — the exact
    /// analogue of <c>SignalRBlackjackHubClient</c>, targeting the server's <c>/threecardhub</c>. Every Best.SignalR
    /// type is confined to this file; game code depends only on the interface. Board snapshots + connection changes
    /// are queued off the Best HTTP background thread and re-raised on Unity's main thread in <see cref="Update"/>.
    ///
    /// ⚠ Encoder: uses <see cref="LitJsonEncoder"/>. If board fields arrive null/default it's a JSON casing mismatch —
    /// switch to <c>JsonDotNetEncoder</c> (same fix noted on the blackjack client).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TcpSignalRHubClient : MonoBehaviour, ITcpHubClient
    {
        [Header("Hub")]
        [Tooltip("Server hub URL, e.g. https://host/threecardhub. Leave the localhost:5000 placeholder to derive it " +
                 "from AppConfig (BaseApiUrl + /threecardhub).")]
        [SerializeField] private string hubUrl = "http://localhost:5000/threecardhub";

        public event Action<TcpBoard> OnTableUpdated;
        public event Action OnConnected;
        public event Action<string> OnDisconnected;

        public bool IsConnected => _connected;

        /// <summary>Supplies the JWT for the [Authorize] hub. Defaults to AccountManager's cached token.</summary>
        public Func<string> TokenProvider { get; set; }

        private HubConnection _hub;
        private volatile bool _connected;
        private readonly ConcurrentQueue<TcpBoard> _boards = new ConcurrentQueue<TcpBoard>();
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
                _connected = true;
            }
            catch (Exception ex)
            {
                _conn.Enqueue(new ConnChange(false, ex.Message));
                throw;
            }
        }

        public Task DisconnectAsync()
        {
            _hub?.StartClose();
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
            catch (Exception ex) { Debug.LogWarning($"[TcpHub] {method} failed: {ex.Message}"); }
        }

        // Prefer an explicit, non-placeholder inspector URL; otherwise derive from the shared AppConfig base
        // (AppConfig.HubUrl is hard-wired to the blackjack hub, so 3CP builds its own path off BaseApiUrl).
        private string ResolveHubUrl()
            => !string.IsNullOrWhiteSpace(hubUrl) && !hubUrl.Contains("localhost:5000")
                ? hubUrl
                : AppConfig.Instance.BaseApiUrl + "/threecardhub";

        private void Build()
        {
            IProtocol protocol = new JsonProtocol(new LitJsonEncoder());
            var options = new HubOptions { PreferedTransport = TransportTypes.WebSocket, SkipNegotiation = false };
            _hub = new HubConnection(new Uri(ResolveHubUrl()), protocol, options);

            _hub.AuthenticationProvider = new TcpBearerAuthenticator(() => TokenProvider?.Invoke());
            _hub.ReconnectPolicy = new DefaultRetryPolicy();

            _hub.On<TcpBoard>("TableUpdated", snapshot => _boards.Enqueue(snapshot));

            _hub.OnConnected    += _        => _conn.Enqueue(new ConnChange(true, null));
            _hub.OnReconnected  += _        => _conn.Enqueue(new ConnChange(true, null));
            _hub.OnReconnecting += (_, r)   => _conn.Enqueue(new ConnChange(false, r ?? "reconnecting"));
            _hub.OnClosed       += _        => _conn.Enqueue(new ConnChange(false, "closed"));
            _hub.OnError        += (_, err) => _conn.Enqueue(new ConnChange(false, err ?? "error"));
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

    /// <summary>Injects an already-held JWT into Best SignalR's negotiate/reconnect requests (header on native,
    /// <c>?access_token=</c> on WebGL). Read fresh each request so a token refresh is picked up. Mirror of the
    /// blackjack client's authenticator, kept local so the 3CP module is self-contained.</summary>
    internal sealed class TcpBearerAuthenticator : IAuthenticationProvider
    {
        private readonly Func<string> _token;
        public TcpBearerAuthenticator(Func<string> token) { _token = token; }

        public bool IsPreAuthRequired => false;

        public event OnAuthenticationSuccededDelegate OnAuthenticationSucceded;
#pragma warning disable 67 // required by the interface; we already hold the JWT so auth never fails → never raised
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
