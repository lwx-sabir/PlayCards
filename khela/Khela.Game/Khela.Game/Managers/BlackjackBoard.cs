using System.Linq;
using CardGames.Blackjack;
using CardGames.Platforms;

namespace Khela.Game.Managers
{
    /// <summary>
    /// Single source of truth for the client-facing board snapshot. Masks the dealer hole card,
    /// reports only the dealer's visible value, and publishes the provably-fair commitment (never
    /// the secret server seed). Used by the hub (join/request), live broadcasts, and REST reads.
    /// </summary>
    public static class BlackjackBoard
    {
        public static object Build(BlackjackTable table) => new
        {
            table.TableId,
            // Monotonic server-stamp (set on every SaveTableAsync). The client drops any snapshot older than the one it
            // already holds, so a late REST/poll response can't clobber a newer hub push (stale-board / resurrected-round).
            table.UpdatedAt,
            table.MaxPlayers,
            table.MaxSeatsPerUser,
            table.RoundInProgress,
            table.MinBet,
            table.MaxBet,
            table.CurrentSeatNumber,
            table.CurrentHandIndex,
            table.TurnExpiresAt,
            // The configured decision length. TurnExpiresAt is stamped as a GENEROUS ceiling until the player's client
            // calls /presented, so the client clamps its countdown to this — it never shows more than a real turn, even
            // in the moment before the collapse lands (or if that call never happens).
            table.TurnDurationSeconds,
            table.InsuranceExpiresAt, // when set, the round is in its insurance phase (its own countdown)
            // Between rounds: when the betting window closes and the server deals whatever is down. Null = no window
            // (disabled, or the table is idle waiting for a first bet). Same ceiling-then-collapse deal as the turn
            // clock, so the client clamps its countdown to BettingDurationSeconds.
            table.BettingExpiresAt,
            table.BettingDurationSeconds,
            table.LastHandId, // id of the most recently settled hand — feeds GET /verify/{handId}
            table.LastResults, // per-seat outcome of the last settled round (drives the client result banner)
            // Commitment only — the server seed stays secret until reveal/verify. ShoeNonce, not RoundNonce, is what
            // derives the shuffle: one shoe spans many rounds, so it advances per SHOE while RoundNonce counts rounds.
            Fairness = new { table.ServerSeedHash, table.ClientSeed, table.RoundNonce, table.ShoeNonce },
            // SHOE state, so the table can show the cut card. CutCardAt is how many cards are left when the cut is
            // reached; CutCardReached true means THIS round finishes on the current shoe and the next one is dealt
            // from a freshly shuffled shoe. Single-deck tables report CutCardAt 0 and never flag it.
            Shoe = BuildShoe(table),
            Dealer = new
            {
                Cards = table.Game.Dealer.Hand.Cards.Select(MaskCard),
                HandValue = table.Game.Dealer.Hand.GetVisibleSum()
            },
            Players = table.Game.Players.Select(ToPlayerDto),
            Seats = table.Seats.Select(s => ToSeatDto(s, table))
        };

        // SHOE state, so the table can show the cut card. CutCardAt is how many cards are LEFT when the cut is
        // reached; CutCardReached true means THIS round finishes on the current shoe and the next one is dealt from
        // a freshly shuffled shoe. A reshuffle-every-round table reports CutCardAt 0 and never flags it.
        //
        // Until a table has dealt its first round under the shoe there is no managed shoe yet, and reporting a live
        // CardsRemaining against a ShoeSize of 0 would describe an impossible deck. Report nothing instead.
        private static object BuildShoe(BlackjackTable table)
        {
            var cards = table.Game?.Deck?.Cards;
            bool managed = table.ShoeSize > 0 && !string.IsNullOrEmpty(table.ShoeHash);
            if (!managed)
                return new { ShoeSize = 0, CardsRemaining = 0, CutCardAt = 0, CutCardReached = false, ShoeId = (string)null };

            // Clamped because a shoe that had to extend itself mid-round can hold more than it was dealt with, and a
            // client drawing a "cards left" gauge should never see a value outside the shoe.
            int remaining = cards == null ? 0 : Math.Min(cards.Count, table.ShoeSize);
            return new
            {
                table.ShoeSize,
                CardsRemaining = remaining,
                table.CutCardAt,
                CutCardReached = table.CutCardAt > 0 && remaining <= table.CutCardAt,
                ShoeId = table.ShoeHash
            };
        }

        // Face-down cards (the dealer hole card) are masked so a snapshot never leaks the down card.
        private static object MaskCard(Card c) => c.IsCardUp
            ? new { FaceVal = (int)c.FaceVal, Suit = (int)c.Suit, Value = CardValue(c), c.IsCardUp }
            : new { FaceVal = 0, Suit = 0, Value = 0, c.IsCardUp };

        // Blackjack point value of a card (J/Q/K = 10, Ace = 11; the hand total resolves soft/hard aces).
        private static int CardValue(Card c) => c.FaceVal switch
        {
            FaceValue.Ace => 11,
            FaceValue.Jack or FaceValue.Queen or FaceValue.King => 10,
            _ => (int)c.FaceVal
        };

        private static object ToPlayerDto(Player p) => new
        {
            p.Id,
            p.Name,
            p.Balance,
            p.SeatNumber,
            p.InRound,   // participating in the CURRENT round (false = spectating/waiting for the next deal)
            p.BetThisWindow,   // actively bet THIS betting window (vs a persisted auto-repeat) — client shows the bet during the window
            Hands = p.Hands.Select((h, idx) => new
            {
                HandIndex = idx,
                h.Bet,
                Insurance = h.InsuranceBet,
                h.Done, // hand has finished acting (stood/bust/double/split-aces) — lets the client close the insurance window
                Cards = h.Hand.Cards.Select(c => new { FaceVal = (int)c.FaceVal, Suit = (int)c.Suit, Value = CardValue(c), c.IsCardUp }),
                HandValue = h.Hand.GetSumOfHand()
            }),
            p.Wins,
            p.Losses,
            p.Push
        };

        private static object ToSeatDto(Seat s, BlackjackTable table) => new
        {
            s.SeatNumber,
            Occupied = s.Player != null,
            Player = s.Player == null ? null : ToPlayerDto(s.Player),
            s.IsConnected,   // false ⇒ client shows "disconnected…" for this seat
            s.IsStalled,     // no heartbeat past StalledTimeout — removal imminent
            s.MissedBetWindows,             // consecutive betting windows sat out (for UI / debugging)
            IdleKickWarning = IsIdleKickWarning(s, table)   // this seat's FINAL window before an idle eviction
        };

        // True when the seat is in its last betting window before being evicted for not betting: the betting window
        // is open, idle eviction is enabled, the seat has no funded bet, and it has already missed all but one of the
        // allowed windows. The client shows the "bet or be removed" warning to the LOCAL player when this is set.
        private static bool IsIdleKickWarning(Seat s, BlackjackTable table)
        {
            if (s.Player == null) return false;
            if (table.RoundInProgress || !table.BettingExpiresAt.HasValue) return false;   // only during an open window
            var cap = table.MaxIdleBettingWindows;
            if (cap <= 0) return false;                                                     // idle eviction disabled
            bool hasBet = s.Player.Hands.Count > 0 && s.Player.Hands[0].Bet > 0;
            if (hasBet) return false;                                                       // already safe this round
            return s.MissedBetWindows >= cap - 1;                                           // one miss from eviction
        }
    }
}
