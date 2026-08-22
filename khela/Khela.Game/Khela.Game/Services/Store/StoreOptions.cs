namespace Khela.Game.Services.Store
{
    /// <summary>
    /// The <c>Store</c> appsettings section (docs/IAP_SPEC.md §7.1). Bound once via <c>IOptionsMonitor&lt;StoreOptions&gt;</c>.
    /// The SWITCHES (<c>Enabled</c>, per-platform <c>Enabled</c>) are additionally overridable live from the
    /// <c>khela:settings</c> Redis hash (see <see cref="StoreSwitches"/>); credentials and paths never are.
    /// </summary>
    public sealed class StoreOptions
    {
        public const string Section = "Store";

        public bool Enabled { get; set; } = true;

        /// <summary>Test purchases (licence testers, sandbox, Fake) grant normally but feed NO spend hooks / revenue unless this is on.</summary>
        public bool TestPurchasesFeedSpend { get; set; } = false;

        /// <summary>Whether chest (random) lines may be sold for real money. Off until the loot-box/odds question is decided.</summary>
        public bool AllowRandomPayloads { get; set; } = false;

        /// <summary>XP granted per USD of reference price on a verified purchase. 0 = off (purchased chips earn XP when wagered).</summary>
        public decimal XpPerUsd { get; set; } = 0m;

        /// <summary>Cap on the stored raw receipt / JWS (the request body itself is capped at the controller).</summary>
        public int MaxReceiptBytes { get; set; } = 65536;

        public int ReconcileIntervalSeconds { get; set; } = 120;

        /// <summary>A Pending/Verified row older than this is re-driven by the reconciler.</summary>
        public int RedriveAfterSeconds { get; set; } = 120;

        /// <summary>Give up re-driving after this many attempts (the row stays for admin re-drive).</summary>
        public int MaxRedriveAttempts { get; set; } = 30;

        public RefundOptions Refunds { get; set; } = new RefundOptions();
        public GooglePlayOptions GooglePlay { get; set; } = new GooglePlayOptions();
        public AppStoreOptions AppStore { get; set; } = new AppStoreOptions();
        public FakeStoreOptions Fake { get; set; } = new FakeStoreOptions();
        public WebStoreOptions Web { get; set; } = new WebStoreOptions();

        public sealed class RefundOptions
        {
            /// <summary>"Rollback" (reverse the credited lines via the wallet's RollbackAsync; never negative; falls back to Flag
            /// when already spent) or "Flag" (record only).</summary>
            public string Policy { get; set; } = "Rollback";
            public bool IsRollback => string.Equals(Policy?.Trim(), "Rollback", System.StringComparison.OrdinalIgnoreCase);
        }

        public sealed class GooglePlayOptions
        {
            public bool Enabled { get; set; } = true;
            public string PackageName { get; set; } = "com.casuallabinteractive.khela";
            /// <summary>Path to the Play-only service-account JSON (server-only file, never in git). Empty = verifier not configured → platform answers PlatformDisabled.</summary>
            public string ServiceAccountJsonPath { get; set; } = "";
            /// <summary>Optional Play Console licence RSA public key (base64) for a cheap pre-check of the receipt signature. Empty = skip.</summary>
            public string LicensePublicKey { get; set; } = "";
            /// <summary>Grant licence-tester purchases (purchaseType 0). They are flagged IsTest and excluded from revenue/spend.</summary>
            public bool AcceptTestPurchases { get; set; } = true;
            /// <summary>Acknowledge/consume server-side right after the grant (the 3-day auto-refund backstop).</summary>
            public bool AcknowledgeOnGrant { get; set; } = true;
            public int SweepUnacknowledgedAfterHours { get; set; } = 24;
            public int VoidedPollMinutes { get; set; } = 360;
            /// <summary>Shared secret the RTDN push subscription appends as <c>?token=</c> (with the subscription phase).</summary>
            public string PubSubToken { get; set; } = "";
        }

        public sealed class AppStoreOptions
        {
            public bool Enabled { get; set; } = false;
            public string BundleId { get; set; } = "com.casuallabinteractive.khela";
            /// <summary>Accept Sandbox-environment transactions (flagged IsTest). App Review buys with sandbox accounts on the production build.</summary>
            public bool AcceptSandbox { get; set; } = true;
            /// <summary>Path to Apple Root CA G3 (.cer, DER) for local JWS chain verification. Empty = verifier not configured.</summary>
            public string RootCertPath { get; set; } = "";
            /// <summary>Also run Apple's online (OCSP) checks during verification.</summary>
            public bool EnableOnlineChecks { get; set; } = false;
            // App Store Server API (refresh / refunds / subscriptions) — optional for verification, needed for RefreshAsync.
            public string IssuerId { get; set; } = "";
            public string KeyId { get; set; } = "";
            public string PrivateKeyPath { get; set; } = "";
        }

        public sealed class FakeStoreOptions
        {
            /// <summary>Unity's Editor fake store. Honoured ONLY in the Development environment regardless of this value.</summary>
            public bool Enabled { get; set; } = true;
        }

        public sealed class WebStoreOptions
        {
            public bool Enabled { get; set; } = false;
        }
    }
}
