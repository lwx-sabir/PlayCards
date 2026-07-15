using System;
using System.Threading;
using System.Threading.Tasks;
using PlayCard.Game.Net;            // ApiResult<T> (shared transport result)
using PlayCard.ThreeCardPoker.Dtos;
using UnityEngine;

namespace PlayCard.ThreeCardPoker.Net
{
    /// <summary>
    /// REST-polling implementation of <see cref="ITcpHubClient"/>: instead of a live socket it polls
    /// <c>GET /api/threecard/{id}/board</c> on an interval and raises <see cref="OnTableUpdated"/>. Keeps the whole
    /// 3CP client buildable + the Home → Lobby → Table flow testable with NO SignalR DLLs. 3CP is a single
    /// Play/Fold decision per round, so a 1–2s poll is perfectly playable. Mirror of <c>PollingBlackjackHubClient</c>.
    ///
    /// The poll loop starts on the Unity main thread, so its <c>await</c> continuations resume there — safe to touch
    /// Unity objects in handlers.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TcpPollingHubClient : MonoBehaviour, ITcpHubClient
    {
        [Header("Polling")]
        [Tooltip("Seconds between board polls. 3CP is a single decision per round, so 1–2s is plenty.")]
        [SerializeField] private float pollIntervalSeconds = 1.5f;

        [Tooltip("Consecutive failed polls before reporting a disconnect to the UI.")]
        [SerializeField] private int failuresBeforeDisconnect = 3;

        public event Action<TcpBoard> OnTableUpdated;
        public event Action OnConnected;
        public event Action<string> OnDisconnected;

        public bool IsConnected { get; private set; }

        private string _tableId;
        private CancellationTokenSource _cts;
        private int _consecutiveFailures;
        private bool _reportedDown;

        public Task ConnectAsync()
        {
            IsConnected = true;
            OnConnected?.Invoke();
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            StopPolling();
            _tableId = null;
            if (IsConnected)
            {
                IsConnected = false;
                OnDisconnected?.Invoke("disconnected");
            }
            return Task.CompletedTask;
        }

        public Task JoinTableAsync(string tableId)
        {
            _tableId = tableId;
            _consecutiveFailures = 0;
            _reportedDown = false;
            StartPolling(tableId);
            return Task.CompletedTask;
        }

        public Task LeaveTableAsync(string tableId)
        {
            if (_tableId == tableId) { StopPolling(); _tableId = null; }
            return Task.CompletedTask;
        }

        /// <summary>One-shot board fetch — equivalent to the hub's "re-send current snapshot".</summary>
        public async Task RequestBoardAsync(string tableId)
        {
            var res = await TcpRestClient.Instance.GetBoardAsync(tableId);
            if (res.Ok && res.Value != null)
                OnTableUpdated?.Invoke(res.Value);
        }

        /// <summary>Seated keep-alive over REST (the polling transport has no socket to ping) so the server's
        /// stalled-seat reaper doesn't drop us. Fire-and-forget — a missed ping is retried next heartbeat tick.</summary>
        public async Task HeartbeatAsync(string tableId)
        {
            try { await TcpRestClient.Instance.HeartbeatAsync(tableId); }
            catch (Exception ex) { Debug.LogWarning($"[TcpPollingHub] heartbeat failed: {ex.Message}"); }
        }

        private void StartPolling(string tableId)
        {
            StopPolling();
            _cts = new CancellationTokenSource();
            _ = PollLoop(tableId, _cts.Token);
        }

        private void StopPolling()
        {
            if (_cts != null) { _cts.Cancel(); _cts.Dispose(); _cts = null; }
        }

        private async Task PollLoop(string tableId, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                ApiResult<TcpBoard> res = default;
                try { res = await TcpRestClient.Instance.GetBoardAsync(tableId); }
                catch (Exception ex) { Debug.LogWarning($"[TcpPollingHub] poll failed: {ex.Message}"); }

                if (ct.IsCancellationRequested) return;

                if (res.Ok && res.Value != null)
                {
                    if (_reportedDown) { _reportedDown = false; OnConnected?.Invoke(); }
                    _consecutiveFailures = 0;
                    OnTableUpdated?.Invoke(res.Value);
                }
                else
                {
                    _consecutiveFailures++;
                    if (!_reportedDown && _consecutiveFailures >= failuresBeforeDisconnect)
                    {
                        _reportedDown = true;
                        OnDisconnected?.Invoke(res.Error ?? "server unreachable");
                    }
                }

                try { await Task.Delay(Mathf.Max(250, (int)(pollIntervalSeconds * 1000)), ct); }
                catch (TaskCanceledException) { return; }
            }
        }

        private void OnDestroy() => StopPolling();
    }
}
