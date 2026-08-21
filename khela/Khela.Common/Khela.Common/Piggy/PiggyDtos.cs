namespace Khela.Common.Piggy
{
    /// <summary>
    /// Which offer the player took. Three, not two — the price and the payout differ per option, and the audit has to
    /// record which was sold rather than inferring it from the amount.
    /// </summary>
    public enum PiggyBreakOption
    {
        /// <summary>The bank is full: pay and take it.</summary>
        Full = 0,

        /// <summary>The bank is full: pay a little more and take double.</summary>
        FullDouble = 1,

        /// <summary>
        /// The bank is NOT full: pay a premium and take the full capacity anyway — buying the wait, not the chips.
        ///
        /// The reason this needs its own option rather than a flag: it is the one path where the payout is not what
        /// the bank holds, so a ledger row that only recorded an amount would describe a bank that was mysteriously
        /// fuller than it ever was.
        /// </summary>
        Early = 2,
    }

    /// <summary>
    /// The piggy bank as one player sees it: how full it is, whether it can be bought yet, and what it costs.
    ///
    /// Everything here is decided by the server. The client renders the bar and the price; it never computes either,
    /// and it certainly never decides that a bank is full.
    /// </summary>
    public sealed class PiggyStateDto
    {
        /// <summary>False when the feature is off or unconfigured — the client hides the whole widget.</summary>
        public bool Enabled { get; set; }

        /// <summary>What is banked right now.</summary>
        public decimal Amount { get; set; }

        /// <summary>This tier's capacity. The bar is Amount / Max.</summary>
        public decimal Max { get; set; }

        /// <summary>0..1, pre-computed so every client draws the same bar.</summary>
        public float Percent { get; set; }

        /// <summary>Which tier's bank this is (1-based). Rises with player level.</summary>
        public int Tier { get; set; }

        /// <summary>Buyable right now: full enough, and the feature allows it. The server re-checks on the break.</summary>
        public bool CanBreak { get; set; }

        /// <summary>The store product to buy it with. Empty until IAP products exist.</summary>
        public string PriceSku { get; set; }

        /// <summary>How much has been banked today, and whether today's cap has been hit — so the client can say
        /// "come back tomorrow" instead of showing a bar that has quietly stopped moving.</summary>
        public decimal AccruedToday { get; set; }
        public bool DailyCapReached { get; set; }

        /// <summary>
        /// Is a countdown actually running? SHOW THE TIMER LABEL OFF THIS, not off <see cref="SecondsLeft"/>.
        ///
        /// The clock only starts once the player has been shown a full bank (the client posts <c>/api/piggy/seen</c>
        /// at that moment). Until then there is no deadline at all — and a zero <see cref="SecondsLeft"/> would read
        /// as "expired" when it means "not started".
        /// </summary>
        public bool TimerRunning { get; set; }

        /// <summary>
        /// When the window runs out, and how long that is from now.
        ///
        /// Both are sent on purpose: a device with a wrong clock would show a wrong countdown from the timestamp
        /// alone. Tick <see cref="SecondsLeft"/> down locally and re-read it on refresh.
        /// </summary>
        public System.DateTime? ExpiresAtUtc { get; set; }
        public long SecondsLeft { get; set; }

        /// <summary>
        /// How long the window is in total, in seconds — so a draining bar can be drawn as <c>SecondsLeft /
        /// WindowSeconds</c> rather than inferred.
        ///
        /// Sent because the client cannot work it out: it only ever sees what is LEFT, and a bar that guesses its own
        /// full length from the largest value it happens to have seen starts wrong for anyone who opens the game
        /// halfway through. 0 when no window is configured.
        /// </summary>
        public long WindowSeconds { get; set; }

        /// <summary>
        /// How much must have gone into the bank since the last celebration before the client flies chips into it on
        /// the player's return. Below it the bar simply fills.
        ///
        /// Sent rather than hard-coded client-side so the threshold can be tuned from the admin without a build — and
        /// so every platform agrees on when a session was "worth" a celebration. 0 = always fly.
        /// </summary>
        public decimal MinFlyAmount { get; set; }

        /// <summary>
        /// How much has gone into the bank since the player was last shown a celebration. Fly the chips when this
        /// reaches <see cref="MinFlyAmount"/>, then POST <c>/api/piggy/celebrated</c> to bank the acknowledgement.
        ///
        /// Server-owned on purpose. A client-side baseline is wiped by every app restart, and when the daily accrual
        /// cap is smaller than the threshold that makes the celebration unreachable for anyone who closes the game
        /// between sessions — which is everyone.
        /// </summary>
        public decimal UnseenAccrued { get; set; }

        /// <summary>Lifetime totals, for the profile screen.</summary>
        public decimal LifetimeAccrued { get; set; }
        public int BreaksCount { get; set; }
    }

    /// <summary>The result of buying a full bank. <c>Ok = false</c> carries a reason rather than an HTTP error, the
    /// same convention the pass and daily reward endpoints use.</summary>
    public sealed class PiggyBreakResultDto
    {
        public bool Ok { get; set; }
        public string Error { get; set; }

        /// <summary>Chips paid out.</summary>
        public decimal Amount { get; set; }

        /// <summary>Chip balance after the payout, so the HUD settles without another round trip.</summary>
        public decimal NewChipBalance { get; set; }

        /// <summary>The freshly reset bank.</summary>
        public PiggyStateDto Piggy { get; set; }
    }
}
