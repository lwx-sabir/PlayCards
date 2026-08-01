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
        /// <summary>The server removed us from our seat (idle-kick or stalled-reaper). Fired once, just before we
        /// navigate back to the lobby — a subscriber can flash a message, but navigation happens regardless.</summary>
        public event Action OnRemovedFromSeat;

        public BoardSnapshot Board { get; private set; }
        public string TableId { get; private set; }

        /// <summary>
        /// When this SITTING began — stamped the first time we sit at a given table in this app run, and used to scope
        /// the session hand log (<see cref="PlayCard.Game.Net.BlackjackRestClient.GetHandLogAsync"/>) to "this sitting".
        ///
        /// Static + keyed by table id ON PURPOSE: a reconnect, a scene reload, or a mid-round rejoin all build a new
        /// TableController, and an instance field would reset the window and silently drop every hand played before the
        /// blip. Leaving the table clears it, so the next sitting starts a fresh log.
        /// </summary>
        public static DateTimeOffset? SessionStartedUtc { get; private set; }
        private static string _sessionTableId;

        /// <summary>Begin (or keep) the sitting window for <paramref name="tableId"/>. Idempotent per table.</summary>
        private static void MarkSessionStart(string tableId)
        {
            if (string.IsNullOrEmpty(tableId)) return;
            if (_sessionTableId == tableId && SessionStartedUtc.HasValue) return;   // same sitting — keep the original stamp
            _sessionTableId = tableId;
            SessionStartedUtc = DateTimeOffset.UtcNow;
        }

        /// <summary>End the sitting window (called when we actually leave the table).</summary>
        private static void ClearSessionStart()
        {
            _sessionTableId = null;
            SessionStartedUtc = null;
        }

        private IBlackjackHubClient _hub;
        private CancellationTokenSource _heartbeatCts;
        private bool _boardConfirmedSeat;   // the board has, at least once, shown us seated by id
        private int _missingSeatBoards;     // consecutive boards missing our seat — tolerate one stale/transient board
        private bool _lastIdleWarned;       // our last seated board had the idle-kick warning (⇒ removal was a bet-timeout)
        private bool _leaving;              // guards against double navigation (manual Leave + a removal push racing)
        private decimal? _lastBoardChips;   // last seat balance we took off a board — only a CHANGE drives the chips HUD
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

        /// <summary>The board's seat for THIS user, matched by user id (not seat number, so a stale lobby-seat
        /// fallback can't point at whoever now sits there). Null until the board confirms our seat, or after we're
        /// removed.</summary>
        private SeatView MySeatView
        {
            get
            {
                var uid = AccountManager.Instance != null ? AccountManager.Instance.UserId : null;
                if (Board?.Seats == null || string.IsNullOrEmpty(uid)) return null;
                return Board.Seats.FirstOrDefault(s => s.Player != null && s.Player.Id == uid);
            }
        }

        /// <summary>We are seated AND our hand is live in the current round (we bet and were dealt in). False while
        /// spectating or waiting for the next deal. Used to lock the leave button during our own live round.</summary>
        public bool AmIInRound => Board != null && Board.RoundInProgress && (MySeatView?.Player?.InRound ?? false);

        /// <summary>Seated but NOT in the current round while a round is running — i.e. watching, waiting for the next
        /// deal (joined mid-round, or sat this one out). Drives the "waiting for next round" panel.</summary>
        public bool AmISpectatingRound => Board != null && Board.RoundInProgress
                                          && MySeatView != null && !(MySeatView.Player?.InRound ?? false);

        /// <summary>Our seat is in its final betting window before an idle eviction — show the "bet or be removed"
        /// warning. Server-computed per seat, matched to us by id.</summary>
        public bool AmIIdleKickWarned => MySeatView?.IdleKickWarning ?? false;

        private async void Start()
        {
            bool fromLobby = !string.IsNullOrEmpty(GameSession.TableId);
            TableId = fromLobby ? GameSession.TableId : debugTableId;
            _hub = hubComponent as IBlackjackHubClient;

            if (_hub == null) { Debug.LogError("[TableController] hubComponent must implement IBlackjackHubClient."); return; }
            if (string.IsNullOrEmpty(TableId)) { Debug.LogError("[TableController] No table id — open from the lobby or set a debugTableId."); return; }

            // Open the sitting window for the session hand log. Idempotent per table, so a reconnect / scene reload
            // keeps the ORIGINAL start time and the log still shows the hands played before the blip.
            MarkSessionStart(TableId);

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

        /// <summary>
        /// Re-fan the CURRENT board to every <see cref="OnBoardChanged"/> consumer without a new server push. Used by
        /// the round-end director when it releases the felt: components that hard-return on board pushes while the
        /// director holds (the view's Render, BetStacks, DealerAnimator) DISCARD those snapshots, so on release they
        /// would otherwise sit on stale state until the next push — which, on a push-only transport, may be a whole
        /// turn away. Safe to call any time; a null board is a no-op.
        /// </summary>
        public void RepublishBoard()
        {
            if (Board != null) OnBoardChanged?.Invoke(Board);
        }

        // ---- Presentation handshake (turn clock) ----

        private bool _presentedSent;
        private bool _betPresentedSent;   // same handshake, but for the between-rounds betting window

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
            if (Board == null) { _presentedSent = false; _betPresentedSent = false; return; }

            // BETWEEN ROUNDS the same handshake collapses the BETTING window. The server arms it at settle with an
            // allowance for the whole round-end ceremony (reveal → dealer draws → collect → pay → sweep → finale);
            // this says "the ceremony is done, we can see the felt", which trims it to the real betting length. Same
            // predicate the betting HUD gates on, so the clock and the chips appear together.
            if (!Board.RoundInProgress)
            {
                _presentedSent = false;
                if (_betPresentedSent || MySeat <= 0 || Board.BettingExpiresAt == null) return;
                if (tableView != null && tableView.RoundEndSettling) return;
                _betPresentedSent = true;
                _ = SendPresentedAsync(MySeat);
                return;
            }
            _betPresentedSent = false;   // round running → re-arm for the next round end

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

            // Ordering guard: DROP any snapshot older than the one we already hold. Hub pushes, inline REST action
            // responses, and poll results all funnel here with no transport-level ordering, so on mobile latency a late
            // response (e.g. a delayed Stand, or a poll that read Redis before a concurrent action) could otherwise
            // clobber a newer push — resurrecting a finished round or flipping back to a turn already ended. UpdatedAt is
            // a monotonic server stamp; equal is kept (idempotent same-state re-apply); a missing stamp is always
            // accepted so a pre-upgrade server can never freeze us.
            if (board.UpdatedAt.HasValue && Board?.UpdatedAt is System.DateTimeOffset cur && board.UpdatedAt.Value < cur)
                return;

            // Round-end transition (in-progress → ended): the server's auto-settle arrives as a push with no
            // client REST call, so refresh the chips HUD here to catch the credited winnings.
            var previous = Board;
            bool roundStarted = (previous == null || !previous.RoundInProgress) && board.RoundInProgress;
            bool roundEnded = previous != null && previous.RoundInProgress && !board.RoundInProgress;

            Board = board;

            // Self-removal watch: once the board has confirmed us seated (by id), a later board where we're gone
            // means the server evicted us (idle-kick or stalled-reaper). MySeat can't detect this — it falls back to
            // the lobby-picked seat — so track board-confirmed seating explicitly and bail to the lobby on the drop.
            var uid = AccountManager.Instance != null ? AccountManager.Instance.UserId : null;
            bool seatedNow = board.Seats != null && !string.IsNullOrEmpty(uid)
                             && board.Seats.Any(s => s.Player != null && s.Player.Id == uid);
            if (seatedNow)
            {
                _boardConfirmedSeat = true;
                _missingSeatBoards = 0;
                _lastIdleWarned = MySeatView?.IdleKickWarning ?? false;
            }
            else if (_boardConfirmedSeat)
            {
                // Don't bounce on a SINGLE board that lacks us — a post-connect resync or a stale push can briefly omit
                // our seat. Re-request the authoritative board; only bail once a confirming board also shows us gone.
                if (++_missingSeatBoards >= 2)
                {
                    _boardConfirmedSeat = false;
                    _missingSeatBoards = 0;
                    HandleRemovedFromSeat();
                    return;   // leaving this scene — don't fan a stale board out to UI that's about to unload
                }
                _ = RefreshAsync();   // fetch a fresh board; if we're truly gone the next one confirms it
            }

            // Paint the chips HUD from the board's OWN authoritative mirror. The server syncs that value from the wallet
            // on every money operation (stake debit, settle credit), so it is real server data — just carried on the
            // push we already received instead of needing a separate /wallet round-trip. That extra hop is the main
            // reason a win or a stake showed up a beat late on device. Being server-sourced, it also clears any
            // optimistic prediction. The RefreshAsync below still runs as the exact reconcile (it also covers the other
            // currencies, and a mirror that's stale because the same player is staking at another table).
            SyncChipsFromBoard(board);

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
        // NOTE: no optimistic chip prediction here. Every action also kicks a balance refresh, so a refresh already in
        // flight would return carrying the debit and the prediction landed on top of it — the balance dipped twice and
        // then corrected back up, flashing "gain" green for a bet. SyncChipsFromBoard already paints the server's own
        // post-action balance off the response board, which is fast enough without guessing.
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
            if (_leaving) return;
            _leaving = true;
            StopHeartbeat();   // we're giving up the seat — stop pinging
            try
            {
                if (MySeat > 0) await Rest.LeaveAsync(TableId, MySeat);
                if (_hub != null) await _hub.LeaveTableAsync(TableId);
            }
            catch (Exception ex) { Debug.LogWarning($"[TableController] leave failed: {ex.Message}"); }
            GameSession.SeatNumber = 0;
            ClearSessionStart();   // the sitting is over — the next one starts a fresh hand log
            SceneNavigator.GoToLobby();
        }

        // The SERVER removed us (idle-kick after the bet-timeout warning, or the stalled reaper). The seat is already
        // gone server-side, so — unlike Leave() — we do NOT call the leave endpoint; we just stop pinging, drop the
        // stale seat, surface a one-shot notice, and return to the lobby. Idempotent via _leaving.
        private void HandleRemovedFromSeat()
        {
            if (_leaving) return;
            _leaving = true;
            StopHeartbeat();
            GameSession.SeatNumber = 0;
            ClearSessionStart();   // the sitting is over — the next one starts a fresh hand log
            GameSession.PendingNotice = _lastIdleWarned
                ? "You were removed from the table for not betting."
                : "You were removed from the table.";
            OnRemovedFromSeat?.Invoke();
            if (_hub != null) { try { _ = _hub.LeaveTableAsync(TableId); } catch { /* best-effort group cleanup */ } }
            SceneNavigator.GoToLobby();
        }

        /// <summary>Force an immediate board refresh (re-push for SignalR / fetch for polling).</summary>
        public Task RefreshAsync() => _hub != null ? _hub.RequestBoardAsync(TableId) : Task.CompletedTask;

        // ---- chips HUD off the board (instant, still server data) ----

        // Push the board's own wallet mirror for our seat into the chips HUD (see the call site for why).
        //
        // Only when the mirror actually CHANGED. The mirror is refreshed from the wallet whenever the server moves money
        // for this seat, so a change means real table money moved and the value is fresh. An UNCHANGED value tells us
        // nothing new — and re-applying it on every push would stomp a credit that arrived from somewhere else while we
        // sat here (claiming a reward, opening a chest, an IAP), snapping the HUD back down until the next reconcile.
        private void SyncChipsFromBoard(BoardSnapshot board)
        {
            if (WalletManager.Instance == null || board?.Seats == null) return;
            int seat = MySeat;
            if (seat <= 0) return;
            var me = board.Seats.FirstOrDefault(s => s.SeatNumber == seat)?.Player;
            if (me == null) return;                       // not in this snapshot (mid-join / removed) — leave the HUD alone
            if (_lastBoardChips == me.Balance) return;    // nothing moved at the table — don't touch the HUD
            _lastBoardChips = me.Balance;
            WalletManager.Instance.SetChips(me.Balance);
        }

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
