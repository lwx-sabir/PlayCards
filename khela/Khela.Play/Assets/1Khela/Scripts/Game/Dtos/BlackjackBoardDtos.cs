using System;
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

        /// <summary>Seat whose turn it is (server seat number), or -1 when nobody is to act.</summary>
        public int CurrentSeatNumber { get; set; } = -1;

        /// <summary>Hand index (for splits) the current seat must act on.</summary>
        public int CurrentHandIndex { get; set; }

        /// <summary>When the current turn auto-expires (UTC), or null when no turn is active.</summary>
        public DateTimeOffset? TurnExpiresAt { get; set; }

        /// <summary>Id of the most recently settled hand — feeds GET /api/Blackjack/verify/{handId}.</summary>
        public string LastHandId { get; set; }

        /// <summary>Provably-fair commitment for the current round (never the secret server seed).</summary>
        public FairnessView Fairness { get; set; }

        public DealerView Dealer { get; set; }
        public List<PlayerView> Players { get; set; } = new List<PlayerView>();
        public List<SeatView> Seats { get; set; } = new List<SeatView>();

        /// <summary>Per-seat outcome of the most recently settled round (drives the result banner); empty mid-round.</summary>
        public List<SeatResultView> LastResults { get; set; } = new List<SeatResultView>();
    }

    /// <summary>One seat's outcome for the last settled round — mirrors the server's SeatRoundResult.</summary>
    public sealed class SeatResultView
    {
        public int SeatNumber { get; set; }
        public string Outcome { get; set; }     // "win" | "lose" | "push"
        public decimal Delta { get; set; }        // net chips change this round (signed: + win, - loss, 0 push)
        public decimal Payout { get; set; }       // gross returned to the wallet
        public int FinalHandValue { get; set; }
        public bool Bust { get; set; }
        public bool Blackjack { get; set; }
    }

    public sealed class FairnessView
    {
        /// <summary>SHA-256 commitment of the secret server seed, published before the deal.</summary>
        public string ServerSeedHash { get; set; }
        public string ClientSeed { get; set; }
        public long RoundNonce { get; set; }
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

        /// <summary>Blackjack point value (J/Q/K = 10, Ace = 11); 0 for a masked hole card.</summary>
        public int Value { get; set; }

        /// <summary>False for the dealer's hole card (rendered face-down).</summary>
        public bool IsCardUp { get; set; }
    }
}
