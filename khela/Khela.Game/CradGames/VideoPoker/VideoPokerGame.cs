using System;
using System.Collections.Generic;
using CardGames.Platforms;

namespace CardGames.VideoPoker
{
    /// <summary>
    /// Pure single-hand video-poker engine: DEAL 5 → apply a HOLD mask → DRAW replacements from the SAME committed
    /// deck → EVALUATE. One 52-card deck per hand, one draw only. Provably-fair by construction: the deck is shuffled
    /// from a seed and can be hashed (<see cref="DeckHash"/>) BEFORE the hold arrives, so the draw cards (deck
    /// positions 5..) are PRE-COMMITTED — they can never be re-picked to dodge the player's hold, the one fairness
    /// fear unique to video poker. This is a PURE engine: no wallet, no server, no paytable, no Redis — that all lives
    /// in a separate layer.
    /// </summary>
    public sealed class VideoPokerGame
    {
        public Deck Deck { get; private set; }
        /// <summary>The initial 5 cards (deck positions 0..4).</summary>
        public List<Card> Dealt { get; private set; } = new(5);
        /// <summary>The final 5 after the draw (null until <see cref="Draw"/>).</summary>
        public List<Card> Final { get; private set; }
        public bool Drawn { get; private set; }

        /// <summary>Shuffle a fresh 52-card deck (deterministic + replayable WITH a seed; crypto shuffle without) and
        /// deal the first 5. Commit <see cref="DeckHash"/> with these 5 before accepting the hold.</summary>
        public void Deal(byte[] seed = null)
        {
            Deck = new Deck();                       // single 52-card deck, reshuffled every hand
            if (seed != null) Deck.Shuffle(seed); else Deck.Shuffle();

            Dealt = new List<Card>(5);
            for (int i = 0; i < 5; i++) Dealt.Add(Deck.Draw());   // positions 0..4
            Final = null;
            Drawn = false;
        }

        /// <summary>SHA-256 of the committed shuffled deck — publish with the dealt 5 BEFORE the hold (provably-fair).</summary>
        public string DeckHash() => Deck?.ComputeHash();

        /// <summary>
        /// Apply the hold (a length-5 mask: <c>true</c> = keep that dealt card) and draw replacements for the rest from
        /// the committed remainder (deck positions 5..). Returns the final 5. One draw only — a second call throws.
        /// </summary>
        public IReadOnlyList<Card> Draw(bool[] hold)
        {
            if (Deck == null) throw new InvalidOperationException("Deal a hand first.");
            if (Drawn) throw new InvalidOperationException("Already drawn — a video-poker hand allows exactly one draw.");
            if (hold == null || hold.Length != 5) throw new ArgumentException("Hold mask must be length 5.", nameof(hold));

            Final = new List<Card>(5);
            for (int i = 0; i < 5; i++)
                Final.Add(hold[i] ? Dealt[i] : Deck.Draw());   // replacements come from the pre-committed remainder
            Drawn = true;
            return Final;
        }

        /// <summary>Evaluate the final 5-card hand naturally — no wilds (must have drawn).</summary>
        public VideoPokerHandRank Evaluate()
        {
            if (!Drawn || Final == null) throw new InvalidOperationException("Draw before evaluating.");
            return VideoPokerEvaluator.Evaluate(Final);
        }

        /// <summary>Evaluate the final 5-card hand for a wild variant (must have drawn). <paramref name="isWild"/> flags
        /// the wild cards — e.g. Deuces Wild = <c>c =&gt; c.FaceVal == FaceValue.Two</c>. Null = natural.</summary>
        public VideoPokerHandRank Evaluate(Func<Card, bool> isWild)
        {
            if (!Drawn || Final == null) throw new InvalidOperationException("Draw before evaluating.");
            return VideoPokerEvaluator.EvaluateWild(Final, isWild);
        }
    }
}
