using System;
using System.Threading;
using System.Threading.Tasks;
using PlayCard.App;
using PlayCard.Game.Net;            // ApiResult<T>
using PlayCard.Game.Wallet;
using PlayCard.ThreeCardPoker.Dtos;
using PlayCard.ThreeCardPoker.Net;
using UnityEngine;

namespace PlayCard.ThreeCardPoker.Table
{
    /// <summary>
    /// Orchestrates one Three Card Poker table — the exact analogue of the blackjack <c>TableController</c>.
    /// Connects the live channel (<see cref="ITcpHubClient"/>), joins the table chosen in the lobby
    /// (<see cref="GameSession.TableId"/>), keeps <see cref="TcpTableView"/> fed with board snapshots, and turns UI
    /// intents (place-bets/deal/play/fold) into server-authoritative REST calls. The client never decides an
    /// outcome — it sends actions and renders whatever the server returns/pushes.
    ///
    /// One board path: every snapshot (hub push OR an action's inline response) flows through
    /// <see cref="HandleBoard"/>, which feeds the view, the bet panel, the action bar and the result banner together.
    ///
    /// Seat identity: the masked 3CP board carries no user-id, so <see cref="MySeat"/> is the seat this client
    /// joined (<see cref="GameSession.SeatNumber"/>) — always join a SPECIFIC seat (the lobby seat-pick does).
    /// </summary>
    public sealed class TcpTableController : MonoBehaviour
    {
        [Header("Refs")]
        [SerializeField] private TcpTableView tableView;
        [Tooltip("A component implementing ITcpHubClient — TcpSignalRHubClient or TcpPollingHubClient.")]
        [SerializeField] private MonoBehaviour hubComponent;

        [Header("Dev (standalone table testing)")]
        [Tooltip("Used only when the lobby didn't set GameSession.TableId. Paste an id from GET /api/lobby/threecard.")]
        [SerializeField] private string debugTableId;
        [Tooltip("When testing standalone (no lobby), auto-take a seat so the table is playable.")]
        [SerializeField] private bool debugAutoJoin = true;
        [Tooltip("Standalone testing: which seat to auto-take (1-based). Must be a specific seat so MySeat is known.")]
        [SerializeField] private int debugSeat = 1;

        [Header("Heartbeat")]
        [Tooltip("Seconds between seated keep-alive pings (live transport only). Keep well below the server's stalled timeout.")]
        [SerializeField] private float heartbeatSeconds = 5f;

        /// <summary>Latest board, after caching. UI gates off this.</summary>
        public event Action<TcpBoard> OnBoardChanged;
        /// <summary>A server action was rejected; arg is the server's message.</summary>
        public event Action<string> OnActionError;
        /// <summary>Live-channel connection state changed.</summary>
        public event Action<bool> OnConnectionChanged;

        public TcpBoard Board { get; private set; }
        public string TableId { get; private set; }

        private ITcpHubClient _hub;
        private CancellationTokenSource _heartbeatCts;
        private static TcpRestClient Rest => TcpRestClient.Instance;

        /// <summary>This player's seat (-1 if not seated). The seat we joined (lobby-picked), since the masked board
        /// carries no user-id.</summary>
        public int MySeat => GameSession.SeatNumber > 0 ? GameSession.SeatNumber : -1;

        /// <summary>Our seat's current view on the latest board (null if not present).</summary>
        public TcpSeatView MySeatView => Board != null && MySeat > 0 ? Board.SeatAt(MySeat) : null;

        /// <summary>True while betting is open for us (before a round, or after one settled).</summary>
        public bool CanBet => Board != null && (Board.Phase == "betting" || Board.Phase == "complete");

        /// <summary>True while we have a pending Play/Fold decision this round.</summary>
        public bool CanDecide
        {
            get
            {
                var s = MySeatView;
                return Board != null && Board.Phase == "acting" && s != null && s.InRound && !s.Decided;
            }
        }

        private async void Start()
        {
            bool fromLobby = !string.IsNullOrEmpty(GameSession.TableId);
            TableId = fromLobby ? GameSession.TableId : debugTableId;
            _hub = hubComponent as ITcpHubClient;

            if (_hub == null) { Debug.LogError("[TcpTableController] hubComponent must implement ITcpHubClient."); return; }
            if (string.IsNullOrEmpty(TableId)) { Debug.LogError("[TcpTableController] No table id — open from the lobby or set a debugTableId."); return; }

            _hub.OnTableUpdated += HandleBoard;
            _hub.OnConnected += HandleConnected;
            _hub.OnDisconnected += HandleDisconnected;

            try
            {
                // Standalone dev path (no lobby): take a specific seat so the table is playable AND MySeat is known.
                if (!fromLobby && debugAutoJoin)
                {
                    int seat = debugSeat > 0 ? debugSeat : 1;
                    GameSession.SeatNumber = seat;
                    var join = await Rest.JoinAsync(TableId, "Player", "", seat);
                    if (join.Ok && join.Value != null) HandleBoard(join.Value);
                }

                await _hub.ConnectAsync();
                await _hub.JoinTableAsync(TableId);
                await RefreshAsync();

                StartHeartbeat();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TcpTableController] connect/join failed: {ex.Message}");
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

        // Single board path: hub pushes AND inline action responses both flow through here, so the view, bet panel,
        // action bar and result banner always see the same snapshot in the same frame.
        private void HandleBoard(TcpBoard board)
        {
            if (board == null) return;

            var previous = Board;
            // Round settled (…→ complete): the server's auto-settle can arrive as a push with no client REST call,
            // so refresh the chips HUD here to catch the credited winnings.
            bool roundSettled = (previous == null || previous.Phase != "complete") && board.Phase == "complete";

            Board = board;
            if (tableView != null) tableView.Render(board);
            OnBoardChanged?.Invoke(board);

            if (roundSettled && WalletManager.Instance != null) _ = WalletManager.Instance.RefreshAsync();
        }

        private void HandleConnected()
        {
            OnConnectionChanged?.Invoke(true);
            // Re-join the table group + resync on (re)connect: a reconnect gets a NEW connection id and is dropped
            // from the server's group, so without this the board freezes after a blip.
            if (!string.IsNullOrEmpty(TableId)) _ = RejoinAsync();
        }

        private async Task RejoinAsync()
        {
            try { await _hub.JoinTableAsync(TableId); await RefreshAsync(); }
            catch (Exception ex) { Debug.LogWarning($"[TcpTableController] rejoin failed: {ex.Message}"); }
        }

        private void HandleDisconnected(string reason) => OnConnectionChanged?.Invoke(false);

        // ---- intents (UI → server-authoritative REST) ----

        /// <summary>Post this round's bets (Ante mandatory; the three side bets optional). Server validates limits.</summary>
        public Task PlaceBets(decimal ante, decimal pairPlus = 0m, decimal prime = 0m, decimal sixCard = 0m)
            => Do(Rest.PlaceBetsAsync(TableId, new PlaceTcpBetsRequest
            {
                SeatNumber = MySeat, Ante = ante, PairPlus = pairPlus, Prime = prime, SixCard = sixCard
            }));

        /// <summary>Deal the round (debit-on-bet).</summary>
        public Task Deal() => Do(Rest.DealAsync(TableId));

        /// <summary>Convenience: place bets then deal in one tap (the common "confirm bet" button). Only deals if the
        /// bet actually registered (server accepted our Ante) — so a rejected bet doesn't trigger a bad deal.</summary>
        public async Task PlaceBetsAndDeal(decimal ante, decimal pairPlus = 0m, decimal prime = 0m, decimal sixCard = 0m)
        {
            await PlaceBets(ante, pairPlus, prime, sixCard);
            var seat = MySeatView;
            if (Board != null && Board.Phase == "betting" && seat != null && seat.InRound && seat.Ante > 0m)
                await Deal();
        }

        /// <summary>Post the Play bet (== Ante). The round reveals + settles once every seat has decided.</summary>
        public Task Play() => Do(Rest.PlayAsync(TableId, MySeat));

        /// <summary>Fold — forfeit the Ante (side bets still settle).</summary>
        public Task Fold() => Do(Rest.FoldAsync(TableId, MySeat));

        public async Task Leave()
        {
            StopHeartbeat();
            try
            {
                if (MySeat > 0) await Rest.LeaveAsync(TableId, MySeat);
                if (_hub != null) await _hub.LeaveTableAsync(TableId);
            }
            catch (Exception ex) { Debug.LogWarning($"[TcpTableController] leave failed: {ex.Message}"); }
            SceneNavigator.GoToLobby();
        }

        /// <summary>Force an immediate board refresh (re-push for SignalR / fetch for polling).</summary>
        public Task RefreshAsync() => _hub != null ? _hub.RequestBoardAsync(TableId) : Task.CompletedTask;

        // ---- seated keep-alive ----

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
                    catch (Exception ex) { Debug.LogWarning($"[TcpTableController] heartbeat failed: {ex.Message}"); }
                }
            }
        }

        // Every action returns the authoritative board, so render it immediately (covers a down / mid-reconnect hub).
        // The server also pushes TableUpdated; the view diffs, so the duplicate render is a no-op. Then refresh the
        // chips HUD for any money that moved.
        private async Task Do(Task<ApiResult<TcpBoard>> call)
        {
            ApiResult<TcpBoard> res;
            try { res = await call; }
            catch (Exception ex) { OnActionError?.Invoke(ex.Message); return; }

            if (!res.Ok)
            {
                Debug.LogWarning($"[TcpTableController] action failed: {res.Error}");
                OnActionError?.Invoke(res.Error);
                return;
            }

            if (res.Value != null) HandleBoard(res.Value);
            else await RefreshAsync();

            if (WalletManager.Instance != null) _ = WalletManager.Instance.RefreshAsync();
        }
    }
}
