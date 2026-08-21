using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Khela.Game.Database.Models
{
    /// <summary>
    /// One player's piggy bank: chips banked by playing, released only by paying.
    ///
    /// The amount here is NOT money — nothing is minted until a break is purchased, which is why this is a single
    /// running row rather than a ledger. A duplicate accrual can unlock the offer slightly early; it cannot create
    /// chips. <see cref="PiggyBreak"/> is where the audit lives, because that one moves real balance.
    /// </summary>
    [Table("PlayerPiggyBanks")]
    [Index(nameof(UserId), IsUnique = true)]
    public class PlayerPiggyBank
    {
        [Key] public Guid Id { get; set; } = Guid.NewGuid();

        [Required] public Guid UserId { get; set; }

        /// <summary>Which tier's bank this is, 1-based. Rises with player level; set when a bank is created or reset.</summary>
        [Required] public int Tier { get; set; } = 1;

        [Required, Column(TypeName = "decimal(18,4)")] public decimal Amount { get; set; }

        /// <summary>
        /// The tier's capacity, SNAPSHOTTED when this bank was opened.
        ///
        /// Deliberately not read from config at display time: an admin lowering a tier's max would otherwise strand
        /// every player mid-fill above the new ceiling, and one raising it would un-fill banks that were full a moment
        /// ago. A player's current bank keeps the deal it started with; the next one gets the new number.
        /// </summary>
        [Required, Column(TypeName = "decimal(18,4)")] public decimal MaxAmount { get; set; }

        [Required, Column(TypeName = "decimal(18,4)")] public decimal LifetimeAccrued { get; set; }

        /// <summary>How many banks this player has bought. Drives the tier ladder and reads well on a profile.</summary>
        [Required] public int BreaksCount { get; set; }

        /// <summary>
        /// The daily cap, held here rather than in its own table: the UTC date the counter belongs to, and the amount
        /// banked on it. A row whose date is not today is treated as zero and rewritten in place, so there is nothing
        /// to reset on a schedule and no second table to join on the settle path.
        /// </summary>
        public DateTime? AccrualDateUtc { get; set; }
        [Required, Column(TypeName = "decimal(18,4)")] public decimal AccruedToday { get; set; }

        /// <summary>
        /// The three moments of a bank's life, and the reason the countdown is fair.
        ///
        /// <see cref="ReadyAtUtc"/> — it reached the buy threshold. Nothing is running yet; a bank can sit full
        /// indefinitely while the player is away.
        ///
        /// <see cref="SeenAtUtc"/> — the player's client actually SHOWED them the full bank. Only now does the clock
        /// start. This is the whole point: a window that ran while someone was offline would take away an offer they
        /// were never given, and a player who opens the game to find a piggy that expired last Tuesday learns that
        /// filling it is pointless.
        ///
        /// <see cref="ExpiresAtUtc"/> — seen + the configured hours. Past it, the bank resets and starts filling again.
        ///
        /// All three are null on a bank that is still filling. Expiry is evaluated LAZILY wherever the bank is read or
        /// written — no sweeper job to lag or fall over, and no window in which the database disagrees with what the
        /// player is being shown.
        /// </summary>
        public DateTime? ReadyAtUtc { get; set; }
        public DateTime? SeenAtUtc { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }

        /// <summary>
        /// The bank level the player was last SHOWN a celebration for — chips flying into the pig.
        ///
        /// Everything above it is "new since they last saw it", which is what the fly threshold is measured against.
        /// It lives here rather than on the device because it is a fact about the player, not about an install: a
        /// client-side baseline is reset by every app restart, and with a daily cap smaller than the threshold that
        /// makes the celebration literally unreachable for anyone who closes the game between sessions.
        ///
        /// Only ever advances when a celebration actually plays, so several small sessions accumulate into one worth
        /// watching instead of three shrugs.
        /// </summary>
        [Required, Column(TypeName = "decimal(18,4)")] public decimal CelebratedAmount { get; set; }

        /// <summary>How many windows have run out on this player, and what the last one was holding. Not money —
        /// nothing was ever minted — but the single best signal for whether the countdown is tuned too tight.</summary>
        [Required] public int ExpiredCount { get; set; }
        [Required, Column(TypeName = "decimal(18,4)")] public decimal LastExpiredAmount { get; set; }
        public DateTime? LastExpiredAtUtc { get; set; }

        [Required] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        [Required] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        [Timestamp, Column(TypeName = "timestamp(6)")] public DateTime? RowVersion { get; set; }
    }

    /// <summary>
    /// One purchased bank — the money event, and therefore the one part of this feature with a full audit row.
    ///
    /// <see cref="PurchaseId"/> is the store's own order/receipt id and is uniquely indexed, which is what makes a
    /// replayed purchase (a retried request, a re-delivered receipt) pay exactly once. Same reserve → grant → complete
    /// shape as a pass or daily claim: a row with <c>CompletedAt == null</c> still owes a payout and is re-driven.
    /// </summary>
    [Table("PiggyBreaks")]
    [Index(nameof(PurchaseId), IsUnique = true)]
    [Index(nameof(UserId), nameof(CreatedAt))]
    public class PiggyBreak
    {
        [Key] public Guid Id { get; set; } = Guid.NewGuid();

        [Required] public Guid UserId { get; set; }

        /// <summary>The tier of the bank that was bought.</summary>
        [Required] public int Tier { get; set; }

        /// <summary>
        /// What was actually PAID OUT into the wallet. This is the money row, so this is the money figure.
        /// </summary>
        [Required, Column(TypeName = "decimal(18,4)")] public decimal Amount { get; set; }

        /// <summary>
        /// What the bank actually HELD at the moment it was bought.
        ///
        /// Recorded separately because on an early break the two differ on purpose: the player pays a premium for
        /// the bank's full capacity before it has filled. A row carrying only the payout would describe a bank that
        /// was mysteriously fuller than it ever was, and there would be no way to tell an early break from a full
        /// one after the fact.
        /// </summary>
        [Required, Column(TypeName = "decimal(18,4)")] public decimal BankedAmount { get; set; }

        /// <summary>Which offer was sold: "Full", "FullDouble" or "Early". Never inferred from the amounts.</summary>
        [Required, MaxLength(16)] public string Option { get; set; } = "Full";

        /// <summary>The payout multiplier applied — 1 for a plain break, 2 for the double.</summary>
        [Required, Column(TypeName = "decimal(18,4)")] public decimal Multiplier { get; set; } = 1m;

        /// <summary>The store product this was sold as.</summary>
        [MaxLength(128)] public string PriceSku { get; set; }

        /// <summary>The store's order id — the thing that makes this idempotent.</summary>
        [Required, MaxLength(256)] public string PurchaseId { get; set; }

        /// <summary>"Pending" | "Completed" | "Failed".</summary>
        [Required, MaxLength(16)] public string Status { get; set; } = "Pending";

        /// <summary>The payload has been paid into the wallet.</summary>
        [Required] public bool Granted { get; set; }

        [Required] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }

        [Timestamp, Column(TypeName = "timestamp(6)")] public DateTime? RowVersion { get; set; }
    }
}
