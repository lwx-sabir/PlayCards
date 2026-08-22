using System;
using System.Collections.Generic;
using Khela.Common.Pass;
using Khela.Common.Piggy;
using Khela.Common.Rewards;

namespace PlayCard.Game.Net
{
    /// <summary>
    /// Client mirror of the server's <c>RedeemPurchaseResultDto</c> (POST /api/store/redeem) — the one store DTO the client
    /// needs its own copy of, because it must implement <see cref="IChipBalanceResult"/> so the wallet's single source
    /// (<c>WalletManager</c>) repaints every balance HUD from <see cref="NewChipBalance"/> the instant the grant lands.
    /// The shape is identical to the shared DTO; System.Text.Json maps it case-insensitively.
    /// </summary>
    public sealed class StoreRedeemResultData : IChipBalanceResult
    {
        public bool Ok { get; set; }
        /// <summary>RedeemStatus as int: 0 Granted · 1 AlreadyGranted · 2 Invalid · 3 Pending · 4 ProductUnavailable ·
        /// 5 PlatformDisabled · 6 StoreDisabled · 7 Error. The client confirms the store order on 0/1/2 only.</summary>
        public int Status { get; set; }
        public string Error { get; set; }
        /// <summary>True = try again later (store API unreachable, server busy): keep the order pending, do NOT confirm.</summary>
        public bool Transient { get; set; }
        public Guid? PurchaseId { get; set; }
        public string ProductId { get; set; }
        /// <summary>What was actually applied (the ledger's numbers) — for the collect animation.</summary>
        public List<GrantedLineDto> Grants { get; set; } = new List<GrantedLineDto>();
        public decimal NewChipBalance { get; set; }
        public decimal NewKashBalance { get; set; }
        public bool IsTest { get; set; }
        /// <summary>Set when the product was a piggy break — payout + fresh bank for the celebration.</summary>
        public PiggyBreakResultDto Piggy { get; set; }
        /// <summary>Set when the product was the golden pass.</summary>
        public PassPurchaseResultDto Pass { get; set; }

        public const int StatusGranted = 0;
        public const int StatusAlreadyGranted = 1;
        public const int StatusInvalid = 2;
        public const int StatusPending = 3;
        public const int StatusProductUnavailable = 4;
        public const int StatusPlatformDisabled = 5;
        public const int StatusStoreDisabled = 6;
        public const int StatusError = 7;

        public bool IsGranted => Ok && (Status == StatusGranted || Status == StatusAlreadyGranted);
        /// <summary>The store order can be confirmed (finished/consumed): the server paid, had already paid, or definitively rejected it.</summary>
        public bool ShouldConfirmOrder => Status == StatusGranted || Status == StatusAlreadyGranted || Status == StatusInvalid;
    }
}
