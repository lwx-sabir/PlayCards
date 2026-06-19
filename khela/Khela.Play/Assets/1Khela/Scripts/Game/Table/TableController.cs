using System;
using System.Linq;
using System.Threading.Tasks;
using PlayCard.Account;
using PlayCard.App;
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

        /// <summary>Latest board, after caching. UI gates buttons off this.</summary>
        public event Action<BoardSnapshot> OnBoardChanged;
        /// <summary>A server action was rejected; arg is the server's message.</summary>
        public event Action<string> OnActionError;
        /// <summary>Live-channel connection state changed.</summary>
        public event Action<bool> OnConnectionChanged;

        public BoardSnapshot Board { get; private set; }
        public string TableId { get; private set; }

        private IBlackjackHubClient _hub;
        private static BlackjackRestClient Rest => BlackjackRestClient.Instance;

        /// <summary>This player's seat in the latest board (-1 if not seated), matched by user id.</summary>
        public int MySeat
        {
            get
            {
                var uid = AccountManager.Instance != null ? AccountManager.Instance.UserId : null;
                if (Board?.Seats == null || string.IsNullOrEmpty(uid)) return -1;
                return Board.Seats.FirstOrDefault(s => s.Player != null && s.Player.Id == uid)?.SeatNumber ?? -1;
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
                    await Rest.JoinAsync(TableId, "Player");

                await _hub.ConnectAsync();
                await _hub.JoinTableAsync(TableId);
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TableController] connect/join failed: {ex.Message}");
                OnConnectionChanged?.Invoke(false);
            }
        }

        private void OnDestroy()
        {
            if (_hub == null) return;
            _hub.OnTableUpdated -= HandleBoard;
            _hub.OnConnected -= HandleConnected;
            _hub.OnDisconnected -= HandleDisconnected;
        }

        // Single board path: hub pushes AND inline action responses both flow through here, so the view,
        // camera and action bar always see the same snapshot in the same frame.
        private void HandleBoard(BoardSnapshot board)
        {
            if (board == null) return;

            // Round-end transition (in-progress → ended): the server's auto-settle arrives as a push with no
            // client REST call, so refresh the chips HUD here to catch the credited winnings.
            bool roundEnded = Board != null && Board.RoundInProgress && !board.RoundInProgress;

            Board = board;
            if (tableView != null) tableView.Render(board);
            OnBoardChanged?.Invoke(board);

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

        public Task PlaceBet(decimal amount)   => Do(Rest.BetAsync(TableId, amount, MySeat));
        public Task Deal()                      => Do(Rest.DealAsync(TableId));
        public Task Hit()                       => Do(Rest.HitAsync(TableId, MySeat, CurrentHand));
        public Task Stand()                     => Do(Rest.StandAsync(TableId, MySeat, CurrentHand));
        public Task DoubleDown()                => Do(Rest.DoubleAsync(TableId, MySeat, CurrentHand));
        public Task Split()                     => Do(Rest.SplitAsync(TableId, MySeat, CurrentHand));
        public Task Insurance(decimal amount)   => Do(Rest.InsuranceAsync(TableId, MySeat, amount, CurrentHand));
        public Task DealerPlay()                => Do(Rest.DealerPlayAsync(TableId));

        public async Task Leave()
        {
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

        private int CurrentHand => Board?.CurrentHandIndex ?? 0;

        // Every action returns the authoritative board, so render it immediately — this covers a down /
        // mid-reconnect hub (RequestBoard would no-op). The server also pushes TableUpdated; the view diffs,
        // so the duplicate render is a no-op. Then refresh the chips HUD for any money that moved.
        private async Task Do<T>(Task<ApiResult<T>> call)
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

            if (res.Value is BoardSnapshot board && board != null)
                HandleBoard(board);
            else
                await RefreshAsync();   // non-board response — fall back to a push/fetch

            if (WalletManager.Instance != null) _ = WalletManager.Instance.RefreshAsync();
        }
    }
}
