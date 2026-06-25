namespace Khela.Common.Blackjack
{
    public class CreateBlackjackTableRequest
    {
        public int MaxPlayers { get; set; } = 5;
        public int MaxSeatsPerUser { get; set; } = 1;
        public BlackjackMode Mode { get; set; } = BlackjackMode.Classic;
        public decimal MinBet { get; set; } = 1000;
        public decimal MaxBet { get; set; } = 10000;
    }

    public class JoinTableRequest
    {
        public string PlayerId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public decimal Balance { get; set; }

        public string Image { get; set; } = string.Empty;

        /// <summary>Seat the player wants (1-based). Null = let the server assign the first open seat.</summary>
        public int? SeatNumber { get; set; }
    }

    public class PlaceBetRequest
    {
        public string PlayerId { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public int SeatNumber { get; set; }
        public int HandIndex { get; set; } = 0;
    }

    public class DoubleDownRequest
    {
        public int SeatNumber { get; set; }
        public int HandIndex { get; set; } = 0;
    }

    public class InsuranceRequest
    {
        public int SeatNumber { get; set; }
        public decimal Amount { get; set; }
        public int HandIndex { get; set; } = 0;
    }

    public class EmoteRequest
    {
        /// <summary>Catalog id of the emote to play (the client maps id → visual). Validated server-side.</summary>
        public string EmoteId { get; set; } = string.Empty;
    }
}
