using System.Collections.Generic;

namespace PlayCard.Game.Dtos
{
    /// <summary>
    /// Client-side mirror of the board snapshot pushed by the server's BlackjackHub
    /// ("TableUpdated" message). Property names match the server's projection; the SignalR
    /// JSON protocol is case-insensitive, so PascalCase here maps to the server's camelCase.
    ///
    /// NOTE: <see cref="CardView.FaceVal"/> and <see cref="CardView.Suit"/> arrive as integers
    /// (System.Text.Json serialises the server's FaceValue/Suit enums as numbers). Confirm the
    /// server enum ordering when mapping to card art — better still, promote these DTOs + the
    /// FaceValue/Suit enums into Khela.Common so client and server share one definition and can
    /// never drift.
    /// </summary>
    public sealed class BoardSnapshot
    {
        public string TableId { get; set; }
        public int MaxPlayers { get; set; }
        public int MaxSeatsPerUser { get; set; }
        public bool RoundInProgress { get; set; }
        public DealerView Dealer { get; set; }
        public List<PlayerView> Players { get; set; } = new List<PlayerView>();
        public List<SeatView> Seats { get; set; } = new List<SeatView>();
    }

    public sealed class DealerView
    {
        public List<CardView> Cards { get; set; } = new List<CardView>();
        public int HandValue { get; set; }
    }

    public sealed class PlayerView
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public decimal Balance { get; set; }
        public int SeatNumber { get; set; }
        public List<HandView> Hands { get; set; } = new List<HandView>();
        public int Wins { get; set; }
        public int Losses { get; set; }
        public int Push { get; set; }
    }

    public sealed class HandView
    {
        public int HandIndex { get; set; }
        public decimal Bet { get; set; }
        public decimal Insurance { get; set; }
        public List<CardView> Cards { get; set; } = new List<CardView>();
        public int HandValue { get; set; }
    }

    public sealed class SeatView
    {
        public int SeatNumber { get; set; }
        public bool Occupied { get; set; }
        public PlayerView Player { get; set; }
    }

    public sealed class CardView
    {
        /// <summary>Server FaceValue enum as an integer. Confirm ordering before mapping to art.</summary>
        public int FaceVal { get; set; }

        /// <summary>Server Suit enum as an integer. Confirm ordering before mapping to art.</summary>
        public int Suit { get; set; }

        /// <summary>False for the dealer's hole card (rendered face-down).</summary>
        public bool IsCardUp { get; set; }
    }
}
