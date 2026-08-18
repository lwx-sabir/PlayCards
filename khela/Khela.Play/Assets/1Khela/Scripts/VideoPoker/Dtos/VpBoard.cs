using System;
using System.Collections.Generic;
using PlayCard.Game.Cards;

namespace PlayCard.VideoPoker.Dtos
{
    /// <summary>
    /// Client mirror of the server's <c>VideoPokerBoard</c> — the single projection returned by both
    /// <c>/api/videopoker/deal</c> and <c>/draw</c>. Single-player: no seats, no dealer, no hub. The client only
    /// RENDERS what the authoritative server dealt and SENDS the hold mask — it never invents a card or decides an
    /// outcome. The secret server seed is absent until the hand is <c>complete</c> (only its committed hash is present
    /// before that). Property names are PascalCase; the wire is camelCase (the REST client reads case-insensitively).
    /// </summary>
    public sealed class VpBoard
    {
        public string HandId { get; set; }
        public string VariantId { get; set; }
        public string VariantName { get; set; }
        /// <summary>"dealt" (awaiting the hold) → "complete".</summary>
        public string Phase { get; set; }
        public int Coins { get; set; }
        public decimal Denomination { get; set; }
        public decimal Bet { get; set; }

        /// <summary>The initial 5 cards.</summary>
        public List<VpCard> Dealt { get; set; } = new List<VpCard>();
        /// <summary>The final 5 after the draw — empty until <see cref="Phase"/> is "complete".</summary>
        public List<VpCard> Final { get; set; } = new List<VpCard>();
        /// <summary>Which of the 5 were held (null until drawn).</summary>
        public bool[] Hold { get; set; }

        public string Category { get; set; }     // null until complete
        public int PayoutCoins { get; set; }
        public decimal Payout { get; set; }
        public decimal Balance { get; set; }      // Chips balance after the op
        public VpFairness Fairness { get; set; }

        public bool IsComplete => Phase == "complete";
        public bool IsDealt => Phase == "dealt";
        public bool IsWin => PayoutCoins > 0;

        /// <summary>The cards to show right now: the final hand once complete, otherwise the dealt hand.</summary>
        public List<VpCard> Cards => IsComplete && Final != null && Final.Count == 5 ? Final : Dealt;
    }

    /// <summary>A single card as the server dealt it: Rank 2..14 (Ace = 14) + Suit as the enum NAME
    /// (Diamonds/Spades/Clubs/Hearts). Mapped to the shared renderer's <see cref="CardId"/> by NAME, never raw int.</summary>
    public sealed class VpCard
    {
        public int Rank { get; set; }
        public string Suit { get; set; }

        public CardId ToCardId(bool faceUp = true)
        {
            var suit = Enum.TryParse<CardSuit>(Suit, ignoreCase: true, out var s) ? s : CardSuit.Spades;
            return new CardId((CardRank)Rank, suit, faceUp);
        }
    }

    /// <summary>Provably-fair fields surfaced to the client. The secret <see cref="ServerSeed"/> is null until the hand
    /// is complete; <see cref="ServerSeedHash"/> + <see cref="DeckHash"/> are the up-front commitment.</summary>
    public sealed class VpFairness
    {
        public string ServerSeedHash { get; set; }
        public string ClientSeed { get; set; }
        public long Nonce { get; set; }
        public string DeckHash { get; set; }
        public string ServerSeed { get; set; }   // revealed only once the hand is complete
    }

    /// <summary>Client mirror of a menu row from <c>GET /api/videopoker/variants</c> — a variant + its paytable.</summary>
    public sealed class VpVariantSummary
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int MinCoins { get; set; }
        public int MaxCoins { get; set; }
        public List<VpPaytableRow> Rows { get; set; } = new List<VpPaytableRow>();
    }

    public sealed class VpPaytableRow
    {
        public string Hand { get; set; }
        public int PerCoin { get; set; }        // per-coin multiplier (0 = non-linear / not offered)
        public int AtMaxCoins { get; set; }     // gross coins at max bet (captures the royal jackpot)
    }
}
