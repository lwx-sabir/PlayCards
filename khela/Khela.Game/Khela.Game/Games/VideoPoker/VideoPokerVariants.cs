using System;
using System.Collections.Generic;
using CardGames.Platforms;
using CardGames.VideoPoker;

namespace Khela.Game.Games.VideoPoker
{
    /// <summary>
    /// The set of video-poker GAMES the server offers, as data. A variant binds a <see cref="VideoPokerPaytable"/>
    /// (the win-rate dial) to an evaluation function (natural, or wild with the right wild predicate) and its bet
    /// bounds. This is the ONLY place the pure engine (<c>CardGames.VideoPoker</c>) is chosen from — the server
    /// module references the engine and the shared card platform, and no other game. Adding a variant is a data edit;
    /// the money path never changes.
    /// </summary>
    public sealed class VideoPokerVariant
    {
        public string Id { get; init; }
        public string Name { get; init; }
        public VideoPokerPaytable Paytable { get; init; }
        /// <summary>How this variant scores a final 5-card hand — natural, or wild (Deuces/Joker).</summary>
        public Func<IReadOnlyList<Card>, VideoPokerHandRank> Evaluate { get; init; }
        /// <summary>Wild jokers added to the deck (Joker Poker = 1 → 53 cards); 0 = the standard 52.</summary>
        public int Jokers { get; init; } = 0;
        public int MinCoins { get; init; } = 1;
        public int MaxCoins { get; init; } = 5;

        public VideoPokerHandRank Score(IReadOnlyList<Card> finalHand) => Evaluate(finalHand);
    }

    /// <summary>Registry of the offered variants, looked up by id (the client sends the id; the server owns the rules).</summary>
    public static class VideoPokerVariants
    {
        /// <summary>Full-pay Jacks or Better 9/6 — natural (no wilds).</summary>
        public static readonly VideoPokerVariant JacksOrBetter = new()
        {
            Id = "jacks-or-better",
            Name = "Jacks or Better 9/6",
            Paytable = VideoPokerPaytable.JacksOrBetter96,
            Evaluate = hand => VideoPokerEvaluator.Evaluate(hand),
        };

        /// <summary>Full-pay Deuces Wild — the four 2s are always wild.</summary>
        public static readonly VideoPokerVariant DeucesWild = new()
        {
            Id = "deuces-wild",
            Name = "Deuces Wild (full-pay)",
            Paytable = VideoPokerPaytable.DeucesWildFullPay,
            Evaluate = hand => VideoPokerEvaluator.EvaluateWild(hand, c => c.FaceVal == FaceValue.Two),
        };

        /// <summary>Bonus Poker 8/5 — Jacks or Better with rank-tiered four-of-a-kind. Natural (no wilds).</summary>
        public static readonly VideoPokerVariant BonusPoker = new()
        {
            Id = "bonus-poker",
            Name = "Bonus Poker 8/5",
            Paytable = VideoPokerPaytable.BonusPoker85,
            Evaluate = hand => VideoPokerEvaluator.Evaluate(hand),
        };

        /// <summary>Double Bonus 9/7/5 — bigger quad bonuses. Natural (no wilds).</summary>
        public static readonly VideoPokerVariant DoubleBonus = new()
        {
            Id = "double-bonus",
            Name = "Double Bonus 9/7/5",
            Paytable = VideoPokerPaytable.DoubleBonus975,
            Evaluate = hand => VideoPokerEvaluator.Evaluate(hand),
        };

        /// <summary>Double Double Bonus 9/6 — quad bonuses with kicker premiums. Natural (no wilds).</summary>
        public static readonly VideoPokerVariant DoubleDoubleBonus = new()
        {
            Id = "double-double-bonus",
            Name = "Double Double Bonus 9/6",
            Paytable = VideoPokerPaytable.DoubleDoubleBonus96,
            Evaluate = hand => VideoPokerEvaluator.Evaluate(hand),
        };

        /// <summary>Joker Poker (Kings or Better) — one wild joker, a 53-card deck.</summary>
        public static readonly VideoPokerVariant JokerPoker = new()
        {
            Id = "joker-poker",
            Name = "Joker Poker (Kings or Better)",
            Paytable = VideoPokerPaytable.JokerPokerKings,
            Evaluate = hand => VideoPokerEvaluator.EvaluateWild(hand, c => c.IsJoker),
            Jokers = 1,
        };

        private static readonly Dictionary<string, VideoPokerVariant> All =
            new(StringComparer.OrdinalIgnoreCase)
            {
                [JacksOrBetter.Id] = JacksOrBetter,
                [DeucesWild.Id] = DeucesWild,
                [BonusPoker.Id] = BonusPoker,
                [DoubleBonus.Id] = DoubleBonus,
                [DoubleDoubleBonus.Id] = DoubleDoubleBonus,
                [JokerPoker.Id] = JokerPoker,
            };

        public const string DefaultId = "jacks-or-better";

        /// <summary>Resolve a variant by id; unknown/blank falls back to the default (server never trusts a client id blindly).</summary>
        public static VideoPokerVariant Resolve(string id)
            => !string.IsNullOrWhiteSpace(id) && All.TryGetValue(id, out var v) ? v : JacksOrBetter;

        public static IReadOnlyCollection<VideoPokerVariant> List() => All.Values;
    }
}
