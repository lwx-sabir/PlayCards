using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PlayCard.Game.Net;            // ApiResult<T>
using PlayCard.VideoPoker.Dtos;
using PlayCard.VideoPoker.Net;
using UnityEngine;

namespace PlayCard.VideoPoker.Game
{
    /// <summary>
    /// Orchestrates one video-poker machine: turns UI intents (deal / toggle-hold / draw) into server-authoritative
    /// REST calls and keeps <see cref="VideoPokerView"/> fed with board snapshots. Single-player + REST-only, so there
    /// is no hub, no seat, no polling — <c>/deal</c> and <c>/draw</c> each return the whole board. The client NEVER
    /// decides an outcome or invents a card: it sends the bet + the hold mask and renders whatever the server returns.
    ///
    /// One board path: every snapshot (deal or draw response) flows through <see cref="HandleBoard"/>.
    /// </summary>
    public sealed class VideoPokerController : MonoBehaviour
    {
        [Header("Refs")]
        [SerializeField] private VideoPokerView view;

        [Header("Bet (set by the lobby / bet UI; these are the defaults)")]
        [SerializeField] private string variantId = "jacks-or-better";
        [SerializeField, Range(1, 5)] private int coins = 5;
        [Tooltip("Chips per coin. Total bet = coins × denomination.")]
        [SerializeField] private int denomination = 100;
        [Tooltip("Load the paytable for the selected variant on Start.")]
        [SerializeField] private bool loadVariantsOnStart = true;

        /// <summary>Latest server board. UI gates off this.</summary>
        public event Action<VpBoard> OnBoardChanged;
        /// <summary>A server action was rejected (insufficient funds, bad input, …); arg is the server's message.</summary>
        public event Action<string> OnActionError;
        /// <summary>A request is in flight — the view disables the action button while true.</summary>
        public event Action<bool> OnBusyChanged;

        public VpBoard Board { get; private set; }
        public IReadOnlyList<bool> Hold => _hold;

        private readonly bool[] _hold = new bool[5];
        private bool _busy;
        private static VideoPokerRestClient Rest => VideoPokerRestClient.Instance;

        public int Coins { get => coins; set => coins = Mathf.Clamp(value, 1, 5); }
        public int Denomination { get => denomination; set => denomination = Mathf.Max(1, value); }
        public string VariantId { get => variantId; set => variantId = string.IsNullOrEmpty(value) ? variantId : value; }
        public decimal TotalBet => (decimal)coins * denomination;

        public bool CanDeal => !_busy && (Board == null || Board.IsComplete);
        public bool CanDraw => !_busy && Board != null && Board.IsDealt;

        private void Start()
        {
            if (view != null) view.Bind(this);
            if (loadVariantsOnStart) LoadVariants();
        }

        /// <summary>The single big button: deal a new hand, or draw the current one.</summary>
        public void DealOrDraw()
        {
            if (CanDeal) Deal();
            else if (CanDraw) Draw();
        }

        public async void Deal()
        {
            if (!CanDeal) return;
            SetBusy(true);
            Array.Clear(_hold, 0, _hold.Length);
            var req = new DealVpRequest
            {
                VariantId = variantId,
                Coins = coins,
                Denomination = denomination,
                ClientRequestId = Guid.NewGuid().ToString("N"),   // idempotent bet-and-deal on retry
            };
            Apply(await Rest.DealAsync(req));
            SetBusy(false);
        }

        public async void Draw()
        {
            if (!CanDraw) return;
            SetBusy(true);
            var req = new DrawVpRequest { HandId = Board.HandId, Hold = (bool[])_hold.Clone() };
            Apply(await Rest.DrawAsync(req));
            SetBusy(false);
        }

        /// <summary>Record a hold toggle from a card slot. Only meaningful in the "dealt" phase.</summary>
        public void SetHold(int index, bool held)
        {
            if (Board == null || !Board.IsDealt || index < 0 || index >= _hold.Length) return;
            _hold[index] = held;
        }

        public async void LoadVariants()
        {
            var res = await Rest.GetVariantsAsync();
            if (res.Ok && view != null) view.SetVariants(res.Value, variantId);
            else if (!res.Ok) OnActionError?.Invoke(res.Error);
        }

        private void Apply(ApiResult<VpBoard> res)
        {
            if (res.Ok) HandleBoard(res.Value);
            else OnActionError?.Invoke(res.Error);
        }

        private void HandleBoard(VpBoard board)
        {
            Board = board;
            if (board?.Hold != null && board.Hold.Length == _hold.Length)
                Array.Copy(board.Hold, _hold, _hold.Length);   // sync to the server's record on settle
            OnBoardChanged?.Invoke(board);
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            OnBusyChanged?.Invoke(busy);
        }
    }
}
