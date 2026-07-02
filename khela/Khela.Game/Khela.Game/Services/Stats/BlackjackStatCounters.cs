using System.Collections.Generic;
using CardGames.Blackjack;
using Khela.Common.Stats;

namespace Khela.Game.Services.Stats
{
    /// <summary>
    /// Pure: roll ONE seat's settled hands (a round) into per-game lifetime stat-counter DELTAS — the increments
    /// merged into the JSON counter bag on UserGameStats. Keys are <see cref="GameStatKeys"/>; only non-zero
    /// counters are emitted (except HandsPlayed). A split shows as extra hands beyond the first, so
    /// <c>splits = hands - 1</c>. No DB, no clock — unit-testable.
    /// </summary>
    public static class BlackjackStatCounters
    {
        public static Dictionary<string, long> ForSeat(IReadOnlyList<HandSettlement> seatHands)
        {
            var c = new Dictionary<string, long>();
            if (seatHands == null || seatHands.Count == 0) return c;

            long won = 0, lost = 0, push = 0, bj = 0, bust = 0, dbl = 0, pairs = 0, insTaken = 0, insWon = 0;
            foreach (var h in seatHands)
            {
                switch (h.Outcome)
                {
                    case HandOutcome.Blackjack: bj++; won++; break;     // a natural is also a win
                    case HandOutcome.Win: won++; break;
                    case HandOutcome.Push: push++; break;
                    case HandOutcome.Bust: bust++; lost++; break;       // a bust is also a loss
                    case HandOutcome.Lose: lost++; break;
                }
                if (h.Doubled) dbl++;
                if (h.WasDealtPair) pairs++;        // only hand 0 carries it → 0 or 1 per seat
                if (h.InsuranceStake > 0m) insTaken++;
                if (h.Insurance == InsuranceResult.Win) insWon++;
            }

            c[GameStatKeys.HandsPlayed] = seatHands.Count;
            if (won > 0)      c[GameStatKeys.HandsWon] = won;
            if (lost > 0)     c[GameStatKeys.HandsLost] = lost;
            if (push > 0)     c[GameStatKeys.Pushes] = push;
            if (bj > 0)       c[GameStatKeys.Blackjacks] = bj;
            if (bust > 0)     c[GameStatKeys.Busts] = bust;
            if (dbl > 0)      c[GameStatKeys.Doubles] = dbl;
            if (pairs > 0)    c[GameStatKeys.Pairs] = pairs;
            if (seatHands.Count > 1) c[GameStatKeys.Splits] = seatHands.Count - 1;   // each split adds one hand
            if (insTaken > 0) c[GameStatKeys.InsuranceTaken] = insTaken;
            if (insWon > 0)   c[GameStatKeys.InsuranceWon] = insWon;
            return c;
        }
    }
}
