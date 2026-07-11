using System;
using System.Collections.Generic;
using PlayCard.Game.Cards;

namespace PlayCard.ThreeCardPoker.Dtos
{
    /// <summary>
    /// Client mirror of the server's <c>ThreeCardPokerBoard</c> — the single masked board contract returned by every
    /// <c>/api/threecard/*</c> action and pushed over the <c>/threecardhub</c> SignalR channel. The dealer's cards
    /// are absent until reveal and the secret server seed is NEVER present (only its committed hash), so the client
    /// can only RENDER what the authoritative server dealt. Field names are PascalCase; the wire is camelCase (the
    /// REST client + SignalR encoder read case-insensitively).
    /// </summary>
    public sealed class TcpBoard
    {
        public string TableId { get; set; }
        /// <summary>betting → acting → complete.</summary>
        public string Phase { get; set; }
        public bool RoundInProgress { get; set; }
        public int MaxPlayers { get; set; }
        public TcpLimits Limits { get; set; }
        public bool DealerRevealed { get; set; }
        /// <summary>Acting-phase Play/Fold countdown target (unix ms), or null outside the acting phase.</summary>
        public long? DecideEpochMs { get; set; }
        public List<TcpSeatView> Seats { get; set; } = new List<TcpSeatView>();
        /// <summary>Empty until <see cref="DealerRevealed"/>.</summary>
        public List<TcpCard> Dealer { get; set; } = new List<TcpCard>();
        public TcpFairness Fairness { get; set; }

        public TcpSeatView SeatAt(int seatNumber)
            => Seats?.Find(s => s.SeatNumber == seatNumber);
    }

    /// <summary>Independent per-circle Chips bet limits. Play always == Ante (inherits the Ante bound); the three
    /// side bets (Pair Plus / Prime / 6-Card) share the Side min/max.</summary>
    public sealed class TcpLimits
    {
        public decimal AnteMin { get; set; }
        public decimal AnteMax { get; set; }
        public decimal SideMin { get; set; }
        public decimal SideMax { get; set; }
        /// <summary>Optional per-bet max WIN cap (0 = disabled).</summary>
        public decimal MaxWinPerBet { get; set; }
    }

    /// <summary>One seat: occupant + connection state + this round's bets, decision, cards and last result. The
    /// masked board carries NO player user-id (privacy) — the client knows its own seat from the one it joined
    /// (<see cref="PlayCard.App.GameSession.SeatNumber"/>).</summary>
    public sealed class TcpSeatView
    {
        public int SeatNumber { get; set; }
        public string PlayerName { get; set; }
        public string PlayerImage { get; set; }
        public bool Occupied { get; set; }
        public bool IsConnected { get; set; }
        public bool IsStalled { get; set; }

        // this round's bets (Chips)
        public decimal Ante { get; set; }
        public decimal PairPlus { get; set; }
        public decimal Prime { get; set; }
        public decimal SixCard { get; set; }

        public bool InRound { get; set; }   // posted an Ante → dealt this round
        public bool Decided { get; set; }   // has acted (Play or Fold)?
        public bool Played { get; set; }    // true = posted Play, false = folded

        public List<TcpCard> Cards { get; set; } = new List<TcpCard>();

        // last settled result (drives the result banner)
        public string Outcome { get; set; }
        public decimal LastReturn { get; set; }

        /// <summary>Total chips this seat has committed this round (Ante + Play-if-played + the three side bets).
        /// The basis for the result banner's net = <see cref="LastReturn"/> − this.</summary>
        public decimal TotalStaked => Ante + PairPlus + Prime + SixCard + (Played ? Ante : 0m);
    }

    /// <summary>A single card as the server dealt it: Rank 2..14 (Ace = 14) + Suit as the enum NAME
    /// (Diamonds/Spades/Clubs/Hearts). Mapped to the shared renderer's <see cref="CardId"/>.</summary>
    public sealed class TcpCard
    {
        public int Rank { get; set; }
        public string Suit { get; set; }

        /// <summary>Map straight to the shared card renderer. Suit is matched by NAME to <see cref="CardSuit"/>
        /// (never by raw int) so it stays correct regardless of atlas layout.</summary>
        public CardId ToCardId(bool faceUp = true)
        {
            var suit = Enum.TryParse<CardSuit>(Suit, ignoreCase: true, out var s) ? s : CardSuit.Spades;
            return new CardId((CardRank)Rank, suit, faceUp);
        }
    }

    /// <summary>Provably-fair commitment surfaced to the client (never the secret seed).</summary>
    public sealed class TcpFairness
    {
        public string ServerSeedHash { get; set; }
        public string ClientSeed { get; set; }
        public long RoundNonce { get; set; }
        public string DeckHash { get; set; }
    }
}
