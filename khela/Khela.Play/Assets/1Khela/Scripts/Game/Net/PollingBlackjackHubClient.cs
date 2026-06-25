using System;
using System.Threading;
using System.Threading.Tasks;
using PlayCard.Game.Dtos;
using UnityEngine;

namespace PlayCard.Game.Net
{
    /// <summary>
    /// REST-polling implementation of <see cref="IBlackjackHubClient"/>: instead of a live socket it
    /// polls <c>GET /board</c> on an interval and raises <see cref="OnTableUpdated"/>. This keeps the
    /// whole client buildable and the Home → Lobby → Table flow testable with NO SignalR DLLs.
    ///
    /// Blackjack is turn-based, so a 1–2s poll is perfectly playable. When the real-time transport
    /// (Best SignalR) is added, drop in that implementation instead — game code that depends only on
    /// <see cref="IBlackjackHubClient"/> (e.g. BlackjackTableView) doesn't change.
    ///
    /// The poll loop is started from the Unity main thread, so its <c>await</c> continuations resume
    /// on the main thread and every event is raised there — safe to touch Unity objects in handlers.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PollingBlackjackHubClient : MonoBehaviour, IBlackjackHubClient
    {
        [Header("Polling")]
        [Tooltip("Seconds between board polls. Blackjack is turn-based, so 1–2s is plenty.")]
        [SerializeField] private float pollIntervalSeconds = 1.5f;

        [Tooltip("Consecutive failed polls before reporting a disconnect to the UI.")]
        [SerializeField] private int failuresBeforeDisconnect = 3;

        public event Action<BoardSnapshot> OnTableUpdated;
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
            if (_tableId == tableId)
            {
                StopPolling();
                _tableId = null;
            }
            return Task.CompletedTask;
        }

        /// <summary>One-shot board fetch — equivalent to the hub's "re-send current snapshot".</summary>
        public async Task RequestBoardAsync(string tableId)
        {
            var res = await BlackjackRestClient.Instance.GetBoardAsync(tableId);
            if (res.Ok && res.Value != null)
                OnTableUpdated?.Invoke(res.Value);
        }

        /// <summary>Seated keep-alive over REST (the polling transport has no socket to ping).</summary>
        public Task HeartbeatAsync(string tableId) => BlackjackRestClient.Instance.HeartbeatAsync(tableId);

        private void StartPolling(string tableId)
        {
            StopPolling();
            _cts = new CancellationTokenSource();
            _ = PollLoop(tableId, _cts.Token);
        }

        private void StopPolling()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
        }

        private async Task PollLoop(string tableId, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                ApiResult<BoardSnapshot> res = default;
                try
                {
                    res = await BlackjackRestClient.Instance.GetBoardAsync(tableId);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[PollingHub] poll failed: {ex.Message}");
                }

                if (ct.IsCancellationRequested) return;

                if (res.Ok && res.Value != null)
                {
                    if (_reportedDown)
                    {
                        _reportedDown = false;
                        OnConnected?.Invoke(); // recovered
                    }
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
