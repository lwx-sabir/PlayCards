using System;
using System.Collections.Generic;
using System.Linq;
using CardGames.Platforms;

namespace Khela.Game.Games.ThreeCardPoker
{
    /// <summary>
    /// Client-safe projection of a <see cref="ThreeCardPokerTable"/> — the single board contract returned by every
    /// REST action and pushed over SignalR. The dealer's cards are withheld until reveal, and the secret server
    /// seed is NEVER included (only its committed hash), so the snapshot can't be used to predict the deal.
    /// </summary>
    public sealed class ThreeCardPokerBoard
    {
        public string TableId { get; set; }
        public string Phase { get; set; }
        public bool RoundInProgress { get; set; }
        public int MaxPlayers { get; set; }
        public TcpBetLimits Limits { get; set; }
        public bool DealerRevealed { get; set; }
        public long? DecideEpochMs { get; set; }        // acting-phase countdown target (unix ms)
        public List<SeatView> Seats { get; set; } = new();
        public List<CardView> Dealer { get; set; } = new();   // empty until DealerRevealed
        public FairnessView Fairness { get; set; }

        public static ThreeCardPokerBoard Build(ThreeCardPokerTable t)
        {
            return new ThreeCardPokerBoard
            {
                TableId = t.TableId,
                Phase = t.Phase,
                RoundInProgress = t.RoundInProgress,
                MaxPlayers = t.MaxPlayers,
                Limits = t.Limits,
                DealerRevealed = t.DealerRevealed,
                DecideEpochMs = t.DecideExpiresAt.HasValue
                    ? new DateTimeOffset(DateTime.SpecifyKind(t.DecideExpiresAt.Value, DateTimeKind.Utc)).ToUnixTimeMilliseconds()
                    : (long?)null,
                Seats = t.Seats.Select(SeatView.From).ToList(),
                Dealer = t.DealerRevealed ? t.DealerCards.Select(CardView.From).ToList() : new List<CardView>(),
                Fairness = new FairnessView
                {
                    ServerSeedHash = t.ServerSeedHash,
                    ClientSeed = t.ClientSeed,
                    RoundNonce = t.RoundNonce,
                    DeckHash = t.DealerRevealed ? t.CurrentDeckHash : null,   // deck hash only meaningful once revealed
                }
            };
        }

        public sealed class SeatView
        {
            public int SeatNumber { get; set; }
            public string PlayerName { get; set; }
            public string PlayerImage { get; set; }
            public bool Occupied { get; set; }
            public bool IsConnected { get; set; }
            public bool IsStalled { get; set; }
            public decimal Ante { get; set; }
            public decimal PairPlus { get; set; }
            public decimal Prime { get; set; }
            public decimal SixCard { get; set; }
            public bool InRound { get; set; }
            public bool Decided { get; set; }
            public bool Played { get; set; }
            public List<CardView> Cards { get; set; } = new();
            public string Outcome { get; set; }
            public decimal LastReturn { get; set; }

            public static SeatView From(TcpSeat s) => new SeatView
            {
                SeatNumber = s.SeatNumber,
                PlayerName = s.Player?.Name,
                PlayerImage = s.Player?.Image,
                Occupied = s.Player != null,
                IsConnected = s.IsConnected,
                IsStalled = s.IsStalled,
                Ante = s.Ante,
                PairPlus = s.PairPlus,
                Prime = s.Prime,
                SixCard = s.SixCard,
                InRound = s.InRound,
                Decided = s.Decided,
                Played = s.Played,
                Cards = (s.Cards ?? new List<Card>()).Select(CardView.From).ToList(),
                Outcome = s.Outcome,
                LastReturn = s.LastReturn,
            };
        }

        public sealed class CardView
        {
            public int Rank { get; set; }     // 2..14 (Ace = 14)
            public string Suit { get; set; }  // Diamonds/Spades/Clubs/Hearts
            public static CardView From(Card c) => new CardView { Rank = (int)c.FaceVal, Suit = c.Suit.ToString() };
        }

        public sealed class FairnessView
        {
            public string ServerSeedHash { get; set; }
            public string ClientSeed { get; set; }
            public long RoundNonce { get; set; }
            public string DeckHash { get; set; }
        }
    }
}
