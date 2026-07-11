using System;
using System.Collections.Generic;
using CardGames.Platforms;
using CardGames.ThreeCardPoker;

namespace Khela.Game.Games.ThreeCardPoker
{
    /// <summary>Independent per-circle Chips bet limits (per the spec). Play is always == Ante, so it inherits
    /// the Ante bound. Side bets share a single min/max for now (Pair Plus / 6-Card / Prime).</summary>
    public sealed class TcpBetLimits
    {
        public decimal AnteMin { get; set; } = 1000m;
        public decimal AnteMax { get; set; } = 10000m;
        public decimal SideMin { get; set; } = 1000m;
        public decimal SideMax { get; set; } = 10000m;
        /// <summary>Optional per-bet max WIN cap (0 = disabled) — bounds liability on the high-multiplier side bets.</summary>
        public decimal MaxWinPerBet { get; set; } = 0m;
    }

    public sealed class TcpPlayer
    {
        public string Id { get; set; }        // userId (GUID string)
        public string Name { get; set; }
        public string Image { get; set; }
    }

    /// <summary>One seat: occupant + connection state + this round's bets, decision, cards and last result.</summary>
    public sealed class TcpSeat
    {
        public int SeatNumber { get; set; }
        public TcpPlayer Player { get; set; }
        public bool IsConnected { get; set; } = true;
        public bool IsStalled { get; set; }
        public DateTime LastHeartbeatAt { get; set; } = DateTime.UtcNow;

        // ── per-round bets (Chips) ──
        public decimal Ante { get; set; }
        public decimal PairPlus { get; set; }
        public decimal Prime { get; set; }
        public decimal SixCard { get; set; }
        public bool InRound { get; set; }          // posted an Ante this round → will be dealt
        public bool Decided { get; set; }          // has acted (Play or Fold) this round?
        public bool Played { get; set; }           // true = posted Play, false = folded (safe default)

        public List<Card> Cards { get; set; } = new();

        // ── last settled result (for the board banner + audit) ──
        public string Outcome { get; set; }
        public decimal LastReturn { get; set; }
        public string PayoutTxId { get; set; }
    }

    /// <summary>
    /// Redis-persisted Three Card Poker table (JSON blob keyed <c>threecard:table:{id}</c>). Holds only what the
    /// server needs between requests: seats + their bets/decisions/cards, the dealer's cards, phase + timer, and
    /// the provably-fair commitment. The engine (<c>ThreeCardPokerGame</c>/<c>ThreeCardPokerSettlement</c>) is
    /// transient — used to deal and to settle, not stored.
    /// </summary>
    public sealed class ThreeCardPokerTable
    {
        public string TableId { get; set; }
        public int MaxPlayers { get; set; } = 5;
        public int MaxSeatsPerUser { get; set; } = 1;
        public TcpBetLimits Limits { get; set; } = new();
        public ThreeCardPokerPaytables Paytables { get; set; } = ThreeCardPokerPaytables.Default;
        public List<TcpSeat> Seats { get; set; } = new();

        // ── round state ──
        public bool RoundInProgress { get; set; }
        public string CurrentRoundId { get; set; }
        public DateTime? RoundStartedAt { get; set; }
        /// <summary>betting → acting → complete.</summary>
        public string Phase { get; set; } = "betting";

        public List<Card> DealerCards { get; set; } = new();
        public bool DealerRevealed { get; set; }

        // ── decision (Play/Fold) timer for the acting phase ──
        public DateTime? DecideExpiresAt { get; set; }
        public int DecideDurationSeconds { get; set; } = 30;

        // ── provably-fair commit–reveal ──
        public string ServerSeed { get; set; }        // secret; never leaves the server until rotation
        public string ServerSeedHash { get; set; }    // committed up front
        public string ClientSeed { get; set; }
        public long RoundNonce { get; set; }
        public string CurrentDeckHash { get; set; }
        public string LastHandId { get; set; }
        public string LastHandHash { get; set; }
    }
}
