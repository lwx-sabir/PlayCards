using System.ComponentModel.DataAnnotations;

namespace Khela.Game.Games.ThreeCardPoker
{
    /// <summary>Create a 3CP table. Per-circle Chips limits (Play = Ante, side bets share Side min/max).</summary>
    public sealed class CreateThreeCardTableRequest
    {
        [Range(1, 7)] public int MaxPlayers { get; set; } = 5;
        [Range(1, 7)] public int MaxSeatsPerUser { get; set; } = 1;
        public decimal AnteMin { get; set; } = 1000m;
        public decimal AnteMax { get; set; } = 10000m;
        public decimal SideMin { get; set; } = 1000m;
        public decimal SideMax { get; set; } = 10000m;
    }

    /// <summary>Take a seat. Balance/Name/Image are display only — the seat is funded from the AUTHORITATIVE wallet,
    /// never from a client-supplied balance. SeatNumber null = auto-assign the first open seat.</summary>
    public sealed class JoinThreeCardTableRequest
    {
        public string Name { get; set; }
        public string Image { get; set; }
        public int? SeatNumber { get; set; }
    }

    /// <summary>Place this round's bets before the deal. Ante is mandatory (0 = not playing this round); the side
    /// bets are optional (0 = not placed). All in Chips.</summary>
    public sealed class PlaceThreeCardBetsRequest
    {
        [Required] public int SeatNumber { get; set; }
        public decimal Ante { get; set; }
        public decimal PairPlus { get; set; }
        public decimal Prime { get; set; }
        public decimal SixCard { get; set; }
    }

    /// <summary>Lightweight lobby row — one browsable 3CP table (screen 3). No cards/seeds; just enough to pick a
    /// table by stakes + occupancy.</summary>
    public sealed class ThreeCardPokerTableSummary
    {
        public string TableId { get; set; }
        public int MaxPlayers { get; set; }
        public int SeatsOccupied { get; set; }
        public decimal AnteMin { get; set; }
        public decimal AnteMax { get; set; }
        public decimal SideMin { get; set; }
        public decimal SideMax { get; set; }
        public bool RoundInProgress { get; set; }
        public string Phase { get; set; }
    }
}
