namespace PlayCard.ThreeCardPoker.Dtos
{
    /// <summary>Create a 3CP table. Per-circle Chips limits (Play = Ante; side bets share Side min/max). Field
    /// names match the server's <c>CreateThreeCardTableRequest</c> (serialized camelCase).</summary>
    public sealed class CreateTcpTableRequest
    {
        public int MaxPlayers { get; set; } = 5;
        public int MaxSeatsPerUser { get; set; } = 1;
        public decimal AnteMin { get; set; } = 1000m;
        public decimal AnteMax { get; set; } = 10000m;
        public decimal SideMin { get; set; } = 1000m;
        public decimal SideMax { get; set; } = 10000m;
    }

    /// <summary>Take a seat. Name/Image are display only — the seat is funded from the AUTHORITATIVE wallet, never a
    /// client-supplied balance. SeatNumber null = server auto-assigns; the 3CP board carries no user-id, so prefer
    /// a SPECIFIC seat so the client knows which one it holds.</summary>
    public sealed class JoinTcpTableRequest
    {
        public string Name { get; set; }
        public string Image { get; set; }
        public int? SeatNumber { get; set; }
    }

    /// <summary>Place this round's bets before the deal. Ante is mandatory (0 = not playing); the three side bets are
    /// optional (0 = not placed). All in Chips.</summary>
    public sealed class PlaceTcpBetsRequest
    {
        public int SeatNumber { get; set; }
        public decimal Ante { get; set; }
        public decimal PairPlus { get; set; }
        public decimal Prime { get; set; }
        public decimal SixCard { get; set; }
    }

    /// <summary>One browsable 3CP lobby row — client mirror of the server's <c>ThreeCardPokerTableSummary</c>
    /// (GET /api/lobby/threecard).</summary>
    public sealed class TcpTableSummary
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
