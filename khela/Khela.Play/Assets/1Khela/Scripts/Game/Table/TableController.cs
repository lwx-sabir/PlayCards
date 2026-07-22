using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PlayCard.Account;
using PlayCard.App;
using PlayCard.Core;
using PlayCard.Game.Dtos;
using PlayCard.Game.Net;
using PlayCard.Game.Wallet;
using UnityEngine;

namespace PlayCard.Game.Table
{
    /// <summary>
    /// Orchestrates one blackjack table: connects the live channel (<see cref="IBlackjackHubClient"/>),
    /// joins the table chosen in the lobby (<see cref="GameSession.TableId"/>), keeps
    /// <see cref="BlackjackTableView"/> fed with board snapshots, and turns UI intents
    /// (bet/hit/stand/…) into server-authoritative REST calls. The client never decides outcomes —
    /// it sends actions and renders whatever the server returns/pushes.
    ///
    /// One board path: every snapshot (hub push OR an action's inline response) flows through
    /// <see cref="HandleBoard"/>, which feeds the view, camera and action bar together.
    /// </summary>
    public sealed class TableController : MonoBehaviour
    {
        [Header("Refs")]
        [SerializeField] private BlackjackTableView tableView;
        [Tooltip("A component implementing IBlackjackHubClient — SignalRBlackjackHubClient or PollingBlackjackHubClient.")]
        [SerializeField] private MonoBehaviour hubComponent;

        [Header("Dev (standalone table testing)")]
        [Tooltip("Used only when the lobby didn't set GameSession.TableId. Paste an id from GET /api/lobby/blackjack.")]
        [SerializeField] private string debugTableId;
        [Tooltip("When testing standalone (no lobby), auto-take a seat so the table is playable.")]
        [SerializeField] private bool debugAutoJoin = true;
        [Tooltip("Standalone testing: which seat to auto-take (1-based) to test per-seat cameras. 0 = first open seat.")]
        [SerializeField] private int debugSeat = 0;

        [Header("Heartbeat")]
        [Tooltip("Seconds between seated keep-alive pings. Keep WELL below the server's Table:StalledTimeoutSeconds (30s) " +
                 "so a missed ping or two doesn't get us reaped.")]
        [SerializeField] private float heartbeatSeconds = 5f;

        /// <summary>Latest board, after caching. UI gates buttons off this.</summary>
        public event Action<BoardSnapshot> OnBoardChanged;
        /// <summary>A server action was rejected; arg is the server's message.</summary>
        public event Action<string> OnActionError;
        /// <summary>Live-channel connection state changed.</summary>
        public event Action<bool> OnConnectionChanged;

        public BoardSnapshot Board { get; private set; }
        public string TableId { get; private set; }

        private IBlackjackHubClient _hub;
        private CancellationTokenSource _heartbeatCts;
        private static BlackjackRestClient Rest => BlackjackRestClient.Instance;

        /// <summary>
        /// This player's seat (-1 if not seated). Board-authoritative once a snapshot arrives and matches us by
        /// user id; until then (or if the live channel is down, e.g. SignalR on IL2CPP) it falls back to the seat
        /// picked in the lobby (<see cref="GameSession.SeatNumber"/>) — so the camera + seat-aware UI resolve
        /// instantly and <see cref="Leave"/> actually releases the seat instead of leaking it in Redis.
        /// </summary>
        public int MySeat
        {
            get
            {
                var uid = AccountManager.Instance != null ? AccountManager.Instance.UserId : null;
                if (Board?.Seats != null && !string.IsNullOrEmpty(uid))
                {
                    var seat = Board.Seats.FirstOrDefault(s => s.Player != null && s.Player.Id == uid)?.SeatNumber ?? -1;
                    if (seat > 0) return seat;   // board confirmed our seat
                }
                return GameSession.SeatNumber > 0 ? GameSession.SeatNumber : -1;   // lobby-picked fallback
            }
        }

        public bool IsMyTurn => Board != null && Board.RoundInProgress && MySeat > 0 && Board.CurrentSeatNumber == MySeat;

        private async void Start()
        {
            bool fromLobby = !string.IsNullOrEmpty(GameSession.TableId);
            TableId = fromLobby ? GameSession.TableId : debugTableId;
            _hub = hubComponent as IBlackjackHubClient;

            if (_hub == null) { Debug.LogError("[TableController] hubComponent must implement IBlackjackHubClient."); return; }
            if (string.IsNullOrEmpty(TableId)) { Debug.LogError("[TableController] No table id — open from the lobby or set a debugTableId."); return; }

            _hub.OnTableUpdated += HandleBoard;
            _hub.OnConnected += HandleConnected;
            _hub.OnDisconnected += HandleDisconnected;

            try
            {
                // Standalone dev path (no lobby): take a seat ourselves so the table is playable.
                if (!fromLobby && debugAutoJoin)
                {
                    await Rest.JoinAsync(TableId, "Player", "", debugSeat > 0 ? debugSeat : (int?)null);
                    GameSession.SeatNumber = debugSeat;   // so MySeat resolves locally in standalone too (0 = let the board decide)
                }

                await _hub.ConnectAsync();
                await _hub.JoinTableAsync(TableId);
                await RefreshAsync();

                StartHeartbeat();   // keep our seat alive so the server's stalled-player reaper doesn't remove us
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TableController] connect/join failed: {ex.Message}");
                OnConnectionChanged?.Invoke(false);
            }
        }

        private void OnDestroy()
        {
            StopHeartbeat();
            if (_hub == null) return;
            _hub.OnTableUpdated -= HandleBoard;
            _hub.OnConnected -= HandleConnected;
            _hub.OnDisconnected -= HandleDisconnected;
        }

        // ---- Presentation handshake (turn clock) ----

        private bool _presentedSent;

        /// <summary>
        /// Tell the server the moment we can ACTUALLY act. The server stamps turn deadlines generously
        /// (max-presentation + turn) so a slow deal-in can never cut us off mid-animation; this call collapses that to
        /// the real turn length from now — so the decision clock is the full configured turn on any device, table size
        /// or clip length, with nothing to hand-tune.
        ///
        /// EDGE-TRIGGERED, and re-armed whenever it stops being our ready turn. That matters for hit/split: they deal a
        /// card, which drops <see cref="BlackjackTableView.DecisionReady"/> while it flies, so once it lands we signal
        /// again — matching the server re-stamping the turn on those actions. Cheap early-outs keep the per-frame cost
        /// to nothing outside a live turn.
        /// </summary>
        private void Update()
        {
            if (Board == null || !Board.RoundInProgress) { _presentedSent = false; return; }

            int seat = MySeat;
            if (seat <= 0 || Board.CurrentSeatNumber != seat
                || (tableView != null && !tableView.DecisionReady(seat)))
            {
                _presentedSent = false;   // not our ready turn → re-arm for the next one
                return;
            }

            if (_presentedSent) return;
            _presentedSent = true;
            _ = SendPresentedAsync(seat);
        }

        // Fire-and-forget: the server is authoritative and the call is idempotent per turn, so a failure just leaves the
        // generous ceiling standing (we lose the collapse, never the turn).
        private async Task SendPresentedAsync(int seat)
        {
            if (string.IsNullOrEmpty(TableId) || seat <= 0) return;
            var res = await Rest.PresentedAsync(TableId, seat);
            if (res.Ok && res.Value != null) HandleBoard(res.Value);   // carries the collapsed deadline
        }

        // Single board path: hub pushes AND inline action responses both flow through here, so the view,
        // camera and action bar always see the same snapshot in the same frame.
        private void HandleBoard(BoardSnapshot board)
        {
            if (board == null) return;

            // Round-end transition (in-progress → ended): the server's auto-settle arrives as a push with no
            // client REST call, so refresh the chips HUD here to catch the credited winnings.
            var previous = Board;
            bool roundStarted = (previous == null || !previous.RoundInProgress) && board.RoundInProgress;
            bool roundEnded = previous != null && previous.RoundInProgress && !board.RoundInProgress;

            Board = board;
            if (tableView != null) tableView.Render(board);
            OnBoardChanged?.Invoke(board);

            if (roundStarted)
                KhelaAnalytics.LogRoundStarted(GameSession.SelectedGame ?? "blackjack", TableId);

            if (roundEnded)
            {
                var myResult = board.LastResults?.FirstOrDefault(r => r.SeatNumber == MySeat);
                if (myResult != null)
                    KhelaAnalytics.LogRoundEnded(GameSession.SelectedGame ?? "blackjack", TableId,
                        myResult.Outcome, myResult.Delta, myResult.Payout);
            }

            if (roundEnded && WalletManager.Instance != null) _ = WalletManager.Instance.RefreshAsync();
        }

        private void HandleConnected()
        {
            OnConnectionChanged?.Invoke(true);
            // Re-join the table group + resync on (re)connect: a reconnect gets a NEW connection id and is
            // dropped from the server's group, so without this the board freezes after a network blip.
            if (!string.IsNullOrEmpty(TableId)) _ = RejoinAsync();
        }

        private async Task RejoinAsync()
        {
            try { await _hub.JoinTableAsync(TableId); await RefreshAsync(); }
            catch (Exception ex) { Debug.LogWarning($"[TableController] rejoin failed: {ex.Message}"); }
        }

        private void HandleDisconnected(string reason) => OnConnectionChanged?.Invoke(false);

        // ---- intents (UI → server-authoritative REST) ----

        public Task PlaceBet(decimal amount)   => Do(Rest.BetAsync(TableId, amount, MySeat),
            _ => KhelaAnalytics.LogBetPlaced(GameSession.SelectedGame ?? "blackjack", amount, MySeat));
        public Task Deal()                      => Do(Rest.DealAsync(TableId));
        public Task Hit()                       => Do(Rest.HitAsync(TableId, MySeat, CurrentHand));
        public Task Stand()                     => Do(Rest.StandAsync(TableId, MySeat, CurrentHand));
        public Task DoubleDown()                => Do(Rest.DoubleAsync(TableId, MySeat, CurrentHand));
        public Task Split()                     => Do(Rest.SplitAsync(TableId, MySeat, CurrentHand));
        public Task Insurance(decimal amount)   => Do(Rest.InsuranceAsync(TableId, MySeat, amount, 0)); // insurance is on the main hand (pre-split), and may be placed off-turn
        public Task DeclineInsurance()          => Do(Rest.DeclineInsuranceAsync(TableId, MySeat));
        public Task DealerPlay()                => Do(Rest.DealerPlayAsync(TableId));

        public async Task Leave()
        {
            StopHeartbeat();   // we're giving up the seat — stop pinging
            try
            {
                if (MySeat > 0) await Rest.LeaveAsync(TableId, MySeat);
                if (_hub != null) await _hub.LeaveTableAsync(TableId);
            }
            catch (Exception ex) { Debug.LogWarning($"[TableController] leave failed: {ex.Message}"); }
            SceneNavigator.GoToLobby();
        }

        /// <summary>Force an immediate board refresh (re-push for SignalR / fetch for polling).</summary>
        public Task RefreshAsync() => _hub != null ? _hub.RequestBoardAsync(TableId) : Task.CompletedTask;

        // ---- seated keep-alive (feeds the server's stalled-player reaper) ----

        private void StartHeartbeat()
        {
            StopHeartbeat();
            _heartbeatCts = new CancellationTokenSource();
            _ = HeartbeatLoopAsync(_heartbeatCts.Token);
        }

        private void StopHeartbeat()
        {
            if (_heartbeatCts == null) return;
            _heartbeatCts.Cancel();
            _heartbeatCts.Dispose();
            _heartbeatCts = null;
        }

        // Ping the server every ~5s while we hold a seat so the reaper doesn't drop us during a long think or a
        // brief blip. Routes through the hub interface: a hub call on the live transport, a REST call on polling.
        // Fire-and-forget — a failed ping is logged and simply retried next tick. Started on the main thread, so
        // the awaited hub/REST continuations resume there too.
        private async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            var delayMs = Mathf.Max(1000, (int)(heartbeatSeconds * 1000));
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(delayMs, ct); }
                catch (TaskCanceledException) { return; }
                if (ct.IsCancellationRequested) return;

                if (MySeat > 0 && _hub != null)
                {
                    try { await _hub.HeartbeatAsync(TableId); }
                    catch (Exception ex) { Debug.LogWarning($"[TableController] heartbeat failed: {ex.Message}"); }
                }
            }
        }

        private int CurrentHand => Board?.CurrentHandIndex ?? 0;

        // Every action returns the authoritative board, so render it immediately — this covers a down /
        // mid-reconnect hub (RequestBoard would no-op). The server also pushes TableUpdated; the view diffs,
        // so the duplicate render is a no-op. Then refresh the chips HUD for any money that moved.
        private async Task Do<T>(Task<ApiResult<T>> call, Action<T> onSuccess = null)
        {
            ApiResult<T> res;
            try { res = await call; }
            catch (Exception ex) { OnActionError?.Invoke(ex.Message); return; }

            if (!res.Ok)
            {
                Debug.LogWarning($"[TableController] action failed: {res.Error}");
                OnActionError?.Invoke(res.Error);
                return;
            }

            onSuccess?.Invoke(res.Value);

            if (res.Value is BoardSnapshot board && board != null)
                HandleBoard(board);
            else
                await RefreshAsync();   // non-board response — fall back to a push/fetch

            if (WalletManager.Instance != null) _ = WalletManager.Instance.RefreshAsync();
        }
    }
}
