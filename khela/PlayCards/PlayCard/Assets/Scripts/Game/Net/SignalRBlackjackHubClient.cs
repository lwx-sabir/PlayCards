using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using UnityEngine;
using PlayCard.Account;
using PlayCard.Game.Dtos;

namespace PlayCard.Game.Net
{
    /// <summary>
    /// Microsoft.AspNetCore.SignalR.Client implementation of <see cref="IBlackjackHubClient"/>.
    ///
    /// Threading follows the proven NetworkClient pattern: SignalR callbacks arrive on a background
    /// thread and are marshalled onto Unity's main thread via ConcurrentQueues drained in Update().
    ///
    /// Every Microsoft-SignalR type is confined to THIS file. Swapping to the Asset-Store Best SignalR
    /// transport (~3 weeks out) is a single new IBlackjackHubClient implementation — game code that
    /// depends only on the interface (e.g. BlackjackTableView) changes nothing.
    ///
    /// NOTE: this transport does NOT work in WebGL (no background sockets/threads in the browser) —
    /// WebGL rides on the Best SignalR migration. Android/iOS (IL2CPP) are fine.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SignalRBlackjackHubClient : MonoBehaviour, IBlackjackHubClient
    {
        [Header("Hub")]
        [Tooltip("Server hub URL, e.g. https://host/blackjackhub")]
        [SerializeField] private string hubUrl = "http://localhost:5000/blackjackhub";

        // --- IBlackjackHubClient events (always raised on the Unity main thread) ---
        public event Action<BoardSnapshot> OnTableUpdated;
        public event Action OnConnected;
        public event Action<string> OnDisconnected;

        public bool IsConnected => _hub != null && _hub.State == HubConnectionState.Connected;

        /// <summary>
        /// Supplies the JWT for the [Authorize] hub. Defaults to AccountManager's cached token;
        /// override before <see cref="ConnectAsync"/> if you manage tokens elsewhere.
        /// </summary>
        public Func<string> TokenProvider { get; set; }

        private HubConnection _hub;
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
            if (_hub.State != HubConnectionState.Disconnected) return; // already connecting/connected

            try
            {
                await _hub.StartAsync();
                _conn.Enqueue(new ConnChange(true, null));
            }
            catch (Exception ex)
            {
                _conn.Enqueue(new ConnChange(false, ex.Message));
                throw;
            }
        }

        public async Task DisconnectAsync()
        {
            if (_hub == null) return;
            await _hub.StopAsync();
        }

        public Task JoinTableAsync(string tableId)   => Invoke("JoinTable", tableId);
        public Task LeaveTableAsync(string tableId)  => Invoke("LeaveTable", tableId);
        public Task RequestBoardAsync(string tableId) => Invoke("RequestBoard", tableId);

        private async Task Invoke(string method, string tableId)
        {
            if (!IsConnected) return;
            try { await _hub.InvokeAsync(method, tableId); }
            catch (Exception ex) { Debug.LogWarning($"[Hub] {method} failed: {ex.Message}"); }
        }

        private void Build()
        {
            _hub = new HubConnectionBuilder()
                .WithUrl(hubUrl, options =>
                {
                    // JWT for the [Authorize] BlackjackHub.
                    options.AccessTokenProvider = () => Task.FromResult(TokenProvider?.Invoke());
                })
                .WithAutomaticReconnect()
                .Build();

            // Server → client board snapshot. Queued here (background thread), raised in Update() (main).
            _hub.On<BoardSnapshot>("TableUpdated", snapshot => _boards.Enqueue(snapshot));

            _hub.Reconnecting += _     => { _conn.Enqueue(new ConnChange(false, "reconnecting")); return Task.CompletedTask; };
            _hub.Reconnected  += _     => { _conn.Enqueue(new ConnChange(true, null));            return Task.CompletedTask; };
            _hub.Closed       += error => { _conn.Enqueue(new ConnChange(false, error?.Message ?? "closed")); return Task.CompletedTask; };
        }

        private void Update()
        {
            // Drain background-thread messages onto the main thread.
            while (_boards.TryDequeue(out var board))
                OnTableUpdated?.Invoke(board);

            while (_conn.TryDequeue(out var change))
            {
                if (change.Connected) OnConnected?.Invoke();
                else OnDisconnected?.Invoke(change.Reason);
            }
        }

        private async void OnDestroy()
        {
            if (_hub != null)
            {
                try { await _hub.DisposeAsync(); } catch { /* shutting down */ }
                _hub = null;
            }
        }
    }
}
