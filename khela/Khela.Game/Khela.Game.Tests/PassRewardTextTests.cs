using System.Collections.Generic;
using System.Linq;
using Khela.Common.Rewards;
using Khela.Game.Services.Pass;
using Xunit;

namespace Khela.Game.Tests
{
    /// <summary>
    /// Locks the admin ladder editor's one-line reward format (31 days × 2 tracks is unusable as dropdowns).
    /// Above all it must keep the legal guardrail: no text an admin can type may produce a Tokens reward
    /// (CLAUDE.md NON-NEGOTIABLE #2/#4). Pure.
    /// </summary>
    public class PassRewardTextTests
    {
        private static List<RewardGrant> Ok(string text)
        {
            var lines = PassRewardText.Parse(text, out var error);
            Assert.Null(error);
            Assert.NotNull(lines);
            return lines;
        }

        private static string Bad(string text)
        {
            var lines = PassRewardText.Parse(text, out var error);
            Assert.Null(lines);
            Assert.False(string.IsNullOrWhiteSpace(error));
            return error;
        }

        [Fact]
        public void ParsesTheEverydayCase()
        {
            var lines = Ok("Chips 2000, Kash 15, XP 100");
            Assert.Equal(3, lines.Count);
            Assert.Equal(RewardKind.Currency, lines[0].Kind);
            Assert.Equal("Chips", lines[0].Id);
            Assert.Equal(2000m, lines[0].Amount);
            Assert.Equal(RewardKind.Xp, lines[2].Kind);
            Assert.Equal(100m, lines[2].Amount);
        }

        [Fact]
        public void ParsesChestsItemsAndCounts()
        {
            var lines = Ok("Chest CK_Chest:Rare, Item lottery_ticket x3, Cosmetic sku_hat");
            Assert.Equal(RewardKind.Chest, lines[0].Kind);
            Assert.Equal("CK_Chest:Rare", lines[0].Id);
            Assert.Equal(1m, lines[0].Amount);                    // a bare chest means one
            Assert.Equal(RewardKind.Item, lines[1].Kind);
            Assert.Equal(3m, lines[1].Amount);
            Assert.Equal(RewardKind.Cosmetic, lines[2].Kind);
        }

        [Fact]
        public void IsForgivingAboutSeparatorsCaseAndThousandsCommas()
        {
            Assert.Equal(2, Ok("chips 1000; xp 50").Count);
            Assert.Equal(3, Ok("Chips 1000\nKash 5\nXP 10").Count);
            Assert.Equal(25000m, Ok("Chips 25_000")[0].Amount);
            Assert.Empty(Ok("   "));
            Assert.Empty(Ok(null));
        }

        [Fact]
        public void RefusesTheTradeableToken_HoweverItIsWritten()
        {
            Assert.Contains("tradeable token", Bad("Tokens 100"));
            Assert.Contains("tradeable token", Bad("tokens 1"));
            Assert.Contains("Chips 100, TOKENS 5", "Chips 100, TOKENS 5");
            Assert.Contains("tradeable token", Bad("Chips 100, TOKENS 5"));
        }

        [Fact]
        public void RefusesANumericCurrencyId_SoAnIntCanNeverBecomeTokens()
        {
            Assert.NotNull(Bad("3 100"));
            Assert.NotNull(Bad("0 100"));
        }

        [Fact]
        public void ErrorsNameTheOffendingToken()
        {
            Assert.Contains("'Chips'", Bad("Chips"));                       // no amount
            Assert.Contains("Chips abc", Bad("Chips abc"));                 // non-numeric
            Assert.Contains("Chips 0", Bad("Chips 0"));                     // non-positive
            Assert.Contains("Chest CK_Chest", Bad("Chest CK_Chest"));       // missing tier
            Assert.Contains("Bitcoin", Bad("Bitcoin 5"));                   // not a currency
        }

        // ---- artwork ----

        [Fact]
        public void ParsesUpToThreeImagesPerReward()
        {
            var lines = Ok("Chips 2000 @icons/card.png|icons/chip.png|icons/glow.png, XP 50");
            Assert.Equal(3, lines[0].Images.Count);
            Assert.Equal("icons/card.png", lines[0].Images[0]);      // back layer first
            Assert.Equal("icons/glow.png", lines[0].Images[2]);
            Assert.Null(lines[1].Images);                            // art is optional
        }

        [Fact]
        public void TheArtworkIsPeeledBeforeTheRewardIsParsed()
        {
            // A url carries digits, colons and slashes — none of it may be mistaken for an amount or a chest tier.
            var chest = Ok("Chest CK_Chest:Rare @https://cdn.khela.app/i/chest_512.png")[0];
            Assert.Equal(RewardKind.Chest, chest.Kind);
            Assert.Equal("CK_Chest:Rare", chest.Id);
            Assert.Equal(1m, chest.Amount);
            Assert.Equal("https://cdn.khela.app/i/chest_512.png", chest.Images[0]);

            var item = Ok("Item lottery_ticket x3 @icons/ticket_2.png")[0];
            Assert.Equal(3m, item.Amount);
            Assert.Single(item.Images);
        }

        [Fact]
        public void ImagesRoundTripExactly()
        {
            const string text = "Chips 2000 @icons/card.png|icons/chip.png, Chest CK_Chest:Rare @icons/chest.png";
            Assert.Equal(text, PassRewardText.Format(Ok(text)));
        }

        [Fact]
        public void RefusesTooManyImagesOrArtworkWithNoReward()
        {
            Assert.Contains("at most 3 images", Bad("Chips 100 @a.png|b.png|c.png|d.png"));
            Assert.Contains("need a reward in front", Bad("@a.png"));
        }

        [Fact]
        public void FormatRoundTripsSoAnUneditedSaveChangesNothing()
        {
            foreach (var node in PassCatalog.DefaultLadder())
            {
                foreach (var track in new[] { node.Free, node.Golden })
                {
                    var text = PassRewardText.Format(track);
                    var reparsed = Ok(text);
                    Assert.Equal(track.Count, reparsed.Count);
                    for (int i = 0; i < track.Count; i++)
                    {
                        Assert.Equal(track[i].Kind, reparsed[i].Kind);
                        Assert.Equal(track[i].Id, reparsed[i].Id);
                        Assert.Equal(track[i].Amount, reparsed[i].Amount);
                    }
                    Assert.Equal(text, PassRewardText.Format(reparsed));    // stable, not just equivalent
                }
            }
        }

        [Fact]
        public void FormatsXpWithoutAnId_AndCountsOnlyWhenAboveOne()
        {
            Assert.Equal("XP 100", PassRewardText.Format(new[] { RewardGrant.Xp(100) }));
            Assert.Equal("Chest CK_Chest:Rare", PassRewardText.Format(new[] { RewardGrant.Chest("CK_Chest:Rare") }));
            Assert.Equal("Chest CK_Chest:Rare x2",
                PassRewardText.Format(new[] { new RewardGrant { Kind = RewardKind.Chest, Id = "CK_Chest:Rare", Amount = 2m } }));
        }

        [Fact]
        public void ParsedLaddersStillPassCatalogValidation()
        {
            // The editor's output has to survive the same gate the game applies — including the token ban.
            var program = PassCatalog.MonthlyProgram();
            program.Nodes = program.Nodes.Select((n, i) => new PassNode
            {
                Index = i + 1,
                IsMilestone = n.IsMilestone,
                Free = Ok(PassRewardText.Format(n.Free)),
                Golden = Ok(PassRewardText.Format(n.Golden)),
            }).ToList();

            Assert.Null(PassCatalog.Validate(new PassConfig { Programs = new List<PassProgram> { program } },
                Khela.Game.Services.Chests.ChestCatalog.Defaults()));
        }
    }
}
