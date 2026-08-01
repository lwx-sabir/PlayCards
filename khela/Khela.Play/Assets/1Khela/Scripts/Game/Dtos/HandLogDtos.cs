using System;
using System.Collections.Generic;

namespace PlayCard.Game.Dtos
{
    /// <summary>
    /// The session HAND LOG for this player at this table — the response of
    /// <c>GET /api/Blackjack/{tableId}/history</c>.
    ///
    /// Server-authoritative: it is read from the per-hand AUDIT rows (GameHandParticipants), the same records the
    /// wallet ledger was written from — NOT from anything the client accumulated. So it is correct after a
    /// reconnect, a scene reload, or a mid-round rejoin, and it can never drift from what the player was actually
    /// paid. A split contributes one entry per hand (HandIndex 0, 1, …), exactly as it settled.
    ///
    /// The totals are computed over the RETURNED entries, so a truncated list never reports a total it doesn't
    /// itemise (check <see cref="Truncated"/>).
    /// </summary>
    public sealed class HandLogData
    {
        public string TableId { get; set; }

        /// <summary>Start of the sitting this log was scoped to (what the client asked for), or null for "all".</summary>
        public DateTimeOffset? SinceUtc { get; set; }

        /// <summary>Number of entries returned.</summary>
        public int Count { get; set; }

        /// <summary>True when the server hit the row cap — there are older hands beyond this list.</summary>
        public bool Truncated { get; set; }

        /// <summary>Total staked across the returned hands (main bets + insurance).</summary>
        public decimal Wagered { get; set; }

        /// <summary>Total gross returned across the returned hands.</summary>
        public decimal Returned { get; set; }

        /// <summary>Net for the session = Returned − Wagered (signed).</summary>
        public decimal Net { get; set; }

        public int Wins { get; set; }
        public int Losses { get; set; }
        public int Pushes { get; set; }

        /// <summary>The hands themselves, NEWEST FIRST.</summary>
        public List<HandLogEntry> Hands { get; set; } = new List<HandLogEntry>();
    }

    /// <summary>One settled hand in the session log (a split hand is its own entry).</summary>
    public sealed class HandLogEntry
    {
        /// <summary>The settled hand's id — feeds the one-click provably-fair check,
        /// <c>GET /api/Blackjack/verify/{handId}</c>.</summary>
        public string HandId { get; set; }

        /// <summary>The TABLE's all-time round counter (shared by a split's two entries). Server-authoritative, and the
        /// key that ties a split's entries together — but not what a player wants to read, since it starts at whatever
        /// the table happened to be on when they sat down. Display <see cref="SessionRound"/> instead.</summary>
        public int HandNumber { get; set; }

        /// <summary>Round number within THIS sitting, 1-based and oldest-first — derived on the client by
        /// <see cref="HandLogView"/> from the entries it received. A split's two entries share one number. Purely a
        /// display convenience; every value in this log is still the server's.</summary>
        public int SessionRound { get; set; }

        public DateTimeOffset? SettledAt { get; set; }

        public int SeatNumber { get; set; }

        /// <summary>0 = the main hand; 1+ = a split hand. Two entries with the same
        /// <see cref="HandNumber"/> are the two halves of one split round.</summary>
        public int HandIndex { get; set; }

        public decimal Bet { get; set; }
        public decimal InsuranceBet { get; set; }

        /// <summary>Gross returned for this hand (0 on a loss, stake back on a push, incl. insurance).</summary>
        public decimal Payout { get; set; }

        /// <summary>Net for this hand = Payout − (Bet + InsuranceBet). Signed.</summary>
        public decimal Delta { get; set; }

        public int FinalHandValue { get; set; }
        public bool Bust { get; set; }
        public bool Blackjack { get; set; }

        /// <summary>"win" | "lose" | "push" | "blackjack" | "bust".</summary>
        public string Outcome { get; set; }

        /// <summary>True when this entry is one hand of a SPLIT round — i.e. worth labelling
        /// "Hand 1 / Hand 2" in the UI. Derived by the view, which can see sibling entries.</summary>
        public bool IsSplitPart { get; set; }
    }
}
