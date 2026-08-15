using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using CardGames.Platforms;

namespace Khela.Game.Games.VideoPoker
{
    /// <summary>Start a hand: pick a variant, a coin denomination (Chips per coin), and 1–5 coins. The stake
    /// (<c>coins × denomination</c>) is debited from the AUTHORITATIVE wallet — never a client-supplied balance.
    /// An optional client seed folds into the provably-fair commitment.</summary>
    public sealed class DealVideoPokerRequest
    {
        public string VariantId { get; set; } = VideoPokerVariants.DefaultId;
        [Range(1, 5)] public int Coins { get; set; } = 5;
        [Range(typeof(decimal), "0.0001", "79228162514264337593543950335")]
        public decimal Denomination { get; set; } = 100m;   // Chips per coin
        public string ClientSeed { get; set; }
        /// <summary>Optional client-generated idempotency token. If set, a retried /deal with the same token returns the
        /// SAME hand instead of debiting a second stake — protects the bet-and-deal step against network retries.</summary>
        public string ClientRequestId { get; set; }
    }

    /// <summary>Complete a hand: which of the dealt 5 to keep. The draw fills the rest from the SAME committed deck.</summary>
    public sealed class DrawVideoPokerRequest
    {
        [Required] public string HandId { get; set; }
        [Required] public bool[] Hold { get; set; }
    }

    /// <summary>
    /// The single client-facing projection of a video-poker hand — returned by both <c>/deal</c> and <c>/draw</c> so
    /// the client has one board contract. The secret server seed is withheld until the hand is COMPLETE (then revealed
    /// so the player can replay the shuffle); the deck hash + server-seed hash are the up-front commitment.
    /// </summary>
    public sealed class VideoPokerBoard
    {
        public string HandId { get; set; }
        public string VariantId { get; set; }
        public string VariantName { get; set; }
        public string Phase { get; set; }         // "dealt" (awaiting the hold) | "complete"
        public int Coins { get; set; }
        public decimal Denomination { get; set; }  // Chips per coin
        public decimal Bet { get; set; }           // coins × denomination
        public List<CardView> Dealt { get; set; } = new();   // the initial 5
        public List<CardView> Final { get; set; } = new();    // the 5 after the draw (empty until complete)
        public bool[] Hold { get; set; }                       // null until drawn
        public string Category { get; set; }                   // null until complete
        public int PayoutCoins { get; set; }                   // gross coins returned by the paytable (0 until complete)
        public decimal Payout { get; set; }                    // PayoutCoins × denomination (0 until complete)
        public decimal Balance { get; set; }                   // player's Chips balance after the op
        public VpFairness Fairness { get; set; }

        public sealed class CardView
        {
            public int Rank { get; set; }     // 2..14 (Ace = 14)
            public string Suit { get; set; }  // Diamonds/Spades/Clubs/Hearts
            public static CardView From(Card c) => new CardView { Rank = (int)c.FaceVal, Suit = c.Suit.ToString() };
        }

        public sealed class VpFairness
        {
            public string ServerSeedHash { get; set; }   // committed at deal
            public string ClientSeed { get; set; }
            public long Nonce { get; set; }
            public string DeckHash { get; set; }          // committed at deal (whole shuffled deck)
            public string ServerSeed { get; set; }        // revealed only once the hand is COMPLETE
        }
    }

    /// <summary>Lightweight menu row for the paytable/variants screen (no live hand state).</summary>
    public sealed class VideoPokerVariantSummary
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int MinCoins { get; set; }
        public int MaxCoins { get; set; }
        public List<PaytableRow> Rows { get; set; } = new();

        public sealed class PaytableRow
        {
            public string Hand { get; set; }
            public int PerCoin { get; set; }        // per-coin multiplier (0 = not offered / non-linear)
            public int AtMaxCoins { get; set; }     // gross coins at max bet (captures the royal jackpot)
        }
    }

    /// <summary>
    /// Provably-fair verification of a settled hand — the commitment published BEFORE the deal, the seed revealed at
    /// settle, and what independently RE-RUNNING the algorithm from that seed produces, with a per-field match report.
    /// A skeptic can either trust <see cref="Verified"/> or replay <see cref="Algorithm"/> offline with the same inputs.
    /// </summary>
    public sealed class VideoPokerVerification
    {
        public string HandId { get; set; }
        public string VariantId { get; set; }
        public bool Verified { get; set; }              // every check below passed
        public string Reason { get; set; }              // set only when verification could not run (e.g. seed missing)
        public string Algorithm { get; set; }           // one-line spec for offline reimplementation

        public Commit Committed { get; set; }           // published BEFORE the deal (cannot change after)
        public Reveal Revealed { get; set; }            // published at settle
        public Redo Recomputed { get; set; }            // output of re-running the shuffle + draw from the revealed seed
        public MatchReport Matches { get; set; }
        public ChainLink Chain { get; set; }

        public sealed class Commit { public string ServerSeedHash { get; set; } public string DeckHash { get; set; } }
        public sealed class Reveal { public string ServerSeed { get; set; } public string ClientSeed { get; set; } public long Nonce { get; set; } public int Jokers { get; set; } }
        public sealed class Redo { public string DeckHash { get; set; } public string[] Dealt { get; set; } public bool[] Hold { get; set; } public string[] Final { get; set; } public string Category { get; set; } public int PayoutCoins { get; set; } public decimal Payout { get; set; } }
        public sealed class MatchReport { public bool SeedBindsToCommitment { get; set; } public bool DeckHashMatches { get; set; } public bool DealtMatches { get; set; } public bool FinalMatches { get; set; } public bool CategoryMatches { get; set; } public bool PayoutMatches { get; set; } }
        public sealed class ChainLink { public string PrevHandHash { get; set; } public string ResultChecksum { get; set; } public string HandHash { get; set; } }
    }
}
