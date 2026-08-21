using System;
using System.Collections.Generic;
using DG.Tweening;
using PlayCard.Game.Net;
using PlayCard.Game.Wallet;
using PlayCard.UI.RewardFly;
using TMPro;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Drop-in balance HUD for ANY screen. Assign a TMP field for each currency you want to show (all optional) and it
    /// keeps them in sync with the player's wallet: it paints the cached balances on enable, then re-pulls, and listens
    /// to <see cref="WalletManager.OnBalancesChanged"/> so it updates after settles / claims / purchases. The server is
    /// authoritative — this only displays. Adding a new currency = one field + one line in <see cref="Show"/>.
    ///
    /// A change is ANIMATED rather than snapped: the number rolls to its new value and the label pops, with an optional
    /// colour flash (green up, red down). Money appearing out of nowhere is the single cheapest thing to get wrong in a
    /// casino HUD — the player has to SEE it arrive, or a reward they just collected reads as not having paid.
    ///
    /// The roll length scales to how big the change is RELATIVE to the balance, not to a fixed figure, because the
    /// currencies here differ by orders of magnitude (millions of chips beside a handful of Kash) and one absolute
    /// yardstick can't serve both. Labels governed by a <see cref="ChipCountJuice"/> are left entirely alone — that
    /// component has its own, table-aware timing and the two must never write the same label.
    /// </summary>
    public sealed class BalanceBinder : MonoBehaviour
    {
        [Header("Assign the text for any currency you show (all optional)")]
        [SerializeField] private TMP_Text chipsText;
        [SerializeField] private TMP_Text coinsText;
        [SerializeField] private TMP_Text gemsText;
        [SerializeField] private TMP_Text kashText;
        [SerializeField] private TMP_Text tokensText;

        [Tooltip("Number format for every balance (e.g. \"#,0\" → 1,234,567; \"0\" → 1234567).")]
        [SerializeField] private string moneyFormat = "#,0";

        [Header("Count roll")]
        [Tooltip("Off = the old behaviour, values snap. Everything below is ignored.")]
        [SerializeField] private bool animate = true;
        [Tooltip("Seconds for a FULL roll — one big enough to be worth watching. Smaller changes finish sooner.")]
        [SerializeField] private float rollSeconds = 0.55f;
        [Tooltip("Floor on the roll time, so even +10 on a millionaire's balance still reads as a count and not a snap.")]
        [SerializeField] private float minRollSeconds = 0.22f;
        [Tooltip("What counts as a BIG change, as a share of the current balance: 0.25 = a quarter of the balance earns " +
                 "the full roll. Relative, so it works for millions of chips and for single-digit Kash alike.")]
        [Range(0.01f, 2f)][SerializeField] private float fullRollFraction = 0.25f;
        [Tooltip("CHIPS ONLY: don't count — the chip number snaps to its new value. The pop and the flash still play. " +
                 "On by default: a chip balance in the millions moves by a fraction of a percent on most payouts, so " +
                 "the roll is a wobble on digits nobody is reading. Turn it off to roll chips like every other currency.")]
        [SerializeField] private bool chipsSkipCount = true;

        [Header("Fly-in credit")]
        [Tooltip("When a RewardFly burst is heading for this currency's counter, HOLD the number and tick it up as " +
                 "each piece lands, instead of snapping to the server's value a second before the first one arrives. " +
                 "Only applies to currencies that actually have a RewardFlyTarget — nothing flies without one.")]
        [SerializeField] private bool creditWithFlyingPieces = true;

        [Tooltip("OFF (default) = the counter only ever walks toward a balance the SERVER has confirmed. Turn ON and " +
                 "it starts walking the moment the pieces launch, using the amount the burst announces, and " +
                 "reconciles when the real balance arrives.\n\n" +
                 "Only needed where claims QUEUE and each takes seconds: the second payout's chips land long before " +
                 "its balance does, so a counter that waits shows nothing and then jumps. Where taps are one at a " +
                 "time the wallet is already there when the pieces are, and this only adds a way to be briefly wrong.")]
        [SerializeField] private bool creditBeforeWalletConfirms;

        [Tooltip("Give up on a burst after this long and show the true balance. The safety net for a panel closed " +
                 "mid-flight or a burst that never reports: a held number is a stale number until it is released.")]
        [SerializeField] private float holdTimeout = 5f;
        [Tooltip("Pop strength per landing piece, as a fraction of the full pop — twenty full-strength kicks is a lot.")]
        [Range(0f, 1f)][SerializeField] private float perPiecePunch = 0.5f;

        [Header("Pop")]
        [Tooltip("Punch strength as a fraction of the target's size. 0 = no pop.")]
        [SerializeField] private float punchScale = 0.22f;
        [SerializeField] private float punchSeconds = 0.28f;
        [Tooltip("Pop the label's PARENT (the whole pill — icon and number together) rather than just the number. " +
                 "Leave off if the parent is driven by a layout group, which would fight the scale.")]
        [SerializeField] private bool punchParent;

        [Header("Flash")]
        [Tooltip("Tint the number on a change and fade it back. Skipped on a label using a TMP vertex gradient, which " +
                 "would override the tint and show nothing.")]
        [SerializeField] private bool flashColour = true;
        [SerializeField] private Color gainColour = new Color(0.30f, 0.85f, 0.40f);
        [SerializeField] private Color lossColour = new Color(0.90f, 0.35f, 0.35f);
        [SerializeField] private float flashSeconds = 0.35f;

        /// <summary>One currency label and everything being animated on it.</summary>
        private sealed class Track
        {
            public string Id;        // currency name, matched against the reward id a burst flies under
            public TMP_Text Label;
            public RectTransform Punch;
            public decimal Shown;    // what the label currently reads (mid-roll this is a partial value)
            public decimal Target;   // where the roll is heading — what the wallet actually says
            public bool HasValue;
            public Tween Roll, Pop, Flash;
            public Vector3 BaseScale = Vector3.one;
            public bool BaseScaleCaptured;
            public Color BaseColour = Color.white;
            public bool BaseColourCaptured;
            public bool SawChange;   // has this label already had one change since the screen opened?
            public bool SkipRoll;    // snap instead of counting (chips, by default)

            // A credit being walked in by flying pieces: the number sits at Held From until the first one lands, then
            // steps toward Target as they arrive.
            public bool Held;
            public decimal HeldFrom;
            public decimal HeldPending;  // announced by bursts, not yet confirmed by the wallet — claims queue, so several stack
            public float HeldUntil;      // unscaled deadline — the number reconciles itself if the burst never finishes
            public bool HeldFlashed;

            // How long a balance BELOW what the pieces already showed is treated as a late confirmation rather than a
            // correction, plus the one such value we're sitting on. Without this the number bounces: with claims
            // queued, tap 2's chips land before tap 1's wallet push arrives.
            public float OptimisticUntil;
            public decimal PendingWallet;
            public bool HasPendingWallet;
        }

        private readonly List<Track> _tracks = new List<Track>();

        private void Awake()
        {
            // The ids are the wallet's own currency names, which is exactly what a currency reward flies under
            // (CurrencyGranter reports `currency.ToString()`), so a counter and a burst find each other with no
            // configuration.
            Add("Chips", chipsText, chipsSkipCount);
            Add("Coins", coinsText);
            Add("Gems", gemsText);
            Add("Kash", kashText);
            Add("Tokens", tokensText);
        }

        private void Add(string id, TMP_Text label, bool skipRoll = false)
        {
            if (label == null) return;
            var punch = punchParent ? label.rectTransform.parent as RectTransform : label.rectTransform;
            _tracks.Add(new Track
            {
                Id = id,
                Label = label,
                Punch = punch != null ? punch : label.rectTransform,
                SkipRoll = skipRoll,
            });
        }

        private void OnEnable()
        {
            foreach (var t in _tracks) t.SawChange = false;

            RewardFlyTarget.BurstValue += OnBurstStarted;
            RewardFlyTarget.BurstProgress += OnPieceLanded;
            RewardFlyTarget.BurstEnded += OnBurstEnded;

            var wm = WalletManager.Instance;
            if (wm == null) return;
            wm.OnBalancesChanged += Show;
            if (wm.Balances != null) Show(wm.Balances);   // paint what we already have, no flicker
            _ = wm.RefreshAsync();                         // then re-pull from the server
        }

        private void OnDisable()
        {
            RewardFlyTarget.BurstValue -= OnBurstStarted;
            RewardFlyTarget.BurstProgress -= OnPieceLanded;
            RewardFlyTarget.BurstEnded -= OnBurstEnded;
            if (WalletManager.Instance != null) WalletManager.Instance.OnBalancesChanged -= Show;

            // Leave every label at rest. A tween killed by the object going away mid-pop would otherwise strand the
            // label at 1.2× scale or half-green, and the next scene shows it like that.
            foreach (var t in _tracks) Settle(t);
        }

        private void Show(WalletBalances b)
        {
            if (b == null) return;
            int i = 0;
            Set(chipsText, b.Chips, ref i);
            Set(coinsText, b.Coins, ref i);
            Set(gemsText, b.Gems, ref i);
            Set(kashText, b.Kash, ref i);
            Set(tokensText, b.Tokens, ref i);
        }

        // Walks the tracks in the SAME order they were added, so each label keeps its own last-shown value. Skips any
        // label a ChipCountJuice is animating — otherwise this snaps the value mid-roll and the count looks like it
        // jumps instead of counting.
        private void Set(TMP_Text label, decimal amount, ref int index)
        {
            if (label == null) return;
            var track = index < _tracks.Count ? _tracks[index] : null;
            index++;

            if (ChipCountJuice.Owns(label)) return;
            if (track == null || track.Label != label) { label.text = amount.ToString(moneyFormat); return; }

            Apply(track, amount);
        }

        private void Apply(Track t, decimal value)
        {
            // First paint: SNAP. Rolling here would count the player's entire balance up from zero every time they
            // open a screen, which reads as a giant win.
            if (!animate || !t.HasValue) { Snap(t, value); return; }
            // Compared against the TARGET, not what's on screen: mid-roll the label reads a partial figure, and a
            // repeated push of the same balance (they arrive in pairs — the instant chip hint, then the refresh)
            // would otherwise restart the roll from wherever it had got to and re-fire the pop.
            if (value == t.Target) { t.HasPendingWallet = false; return; }   // the wallet caught up with the optimism

            bool gain = value > t.Target;

            // A balance BELOW what the pieces already walked the label to, moments after a burst, is almost always a
            // LATE CONFIRMATION of an earlier claim rather than a correction to this one: claims are queued, so tap 2's
            // chips land well before tap 1's wallet push arrives. Rolling down to it would bounce the number backwards
            // and then straight up again. Hold it aside — if it really was the truth (a refused claim), nothing higher
            // follows and the grace period expires, and Update applies it then.
            if (!gain && Time.unscaledTime < t.OptimisticUntil)
            {
                t.PendingWallet = value;
                t.HasPendingWallet = true;
                return;
            }
            t.HasPendingWallet = false;

            // A CREDIT with pieces on the way: freeze the number where it is and let the landings walk it up. Only a
            // credit — money leaving is the player's own action and must show at once — and only when the number has
            // not already moved, because rewinding a balance downward to make room for juice is worse than a burst
            // that doesn't line up.
            //
            // Checked BEFORE the placeholder rule below: an armed burst is direct evidence that this is a real payout
            // the player just triggered, which is a far stronger signal than "the previous value happened to be zero".
            if (gain && creditWithFlyingPieces && RewardFlyTarget.IsBurstArmed(t.Id))
            {
                if (!t.Held) { t.HeldFrom = t.Shown; t.HeldPending = 0m; t.HeldFlashed = false; }
                t.Held = true;
                t.SawChange = true;
                t.Target = value;
                t.HeldUntil = Time.unscaledTime + Mathf.Max(0.5f, holdTimeout);
                // Only an OPTIMISTIC counter distrusts a lower balance (see the guard in this method). Without the
                // opt-in the server is always right the moment it speaks, exactly as before.
                if (creditBeforeWalletConfirms) t.OptimisticUntil = t.HeldUntil;
                t.Roll?.Kill();       // a roll started before the burst armed must not race the pieces
                return;
            }

            // The FIRST change after opening a screen, from a zero, is the server RECONCILING a placeholder, not a
            // payout: WalletManager.SetChips creates a balances object with every other currency at 0, so the refresh
            // that follows legitimately fills Kash/Gems in from nothing. Snap that one, then animate everything after
            // it — so genuinely collecting Kash from a zero balance still gets its roll and pop.
            if (t.Shown <= 0m && !t.SawChange) { t.SawChange = true; Snap(t, value); return; }
            t.SawChange = true;

            // Snap the figure but keep the reaction: the player still sees the label kick and flash, so the money
            // clearly LANDED — it just doesn't spin digits that barely move.
            if (t.SkipRoll) Snap(t, value);
            else Roll(t, value);

            Pop(t);
            Flash(t, gain);
        }

        // ---------------- flying-piece credit ----------------

        /// <summary>
        /// One piece hit this currency's counter. Step the number to <paramref name="progress01"/> of the way through
        /// the held credit and pop — so the balance climbs WITH the chips, arriving exactly as the last one lands.
        ///
        /// Measured from the value held when the burst armed, not from the shrinking remainder, so the steps are even.
        /// </summary>
        /// <summary>
        /// Pieces are on their way, and this is what they're worth. Start the walk NOW, from the amount rather than
        /// from the wallet.
        ///
        /// Waiting for the balance to move is what made this fail: claims queue, so the second payout's wallet update
        /// lands seconds after its chips do. The counter had nothing to walk toward while they arrived, released, and
        /// then jumped when the truth turned up. Walking the announced amount means the number climbs with the pieces
        /// every time — and the real value, when it arrives, simply becomes the target mid-walk.
        ///
        /// Display only. The wallet is still the authority: <see cref="Apply"/> overwrites the target the moment the
        /// server says otherwise, and a refused claim rolls the number back down with it.
        /// </summary>
        private void OnBurstStarted(string rewardId, int pieces, decimal amount)
        {
            // Opt-in, and OFF by default: this is the only place the counter moves on something the server has not
            // confirmed yet. A screen that claims one tap at a time never needs it, and must not silently get it.
            if (!creditBeforeWalletConfirms) return;
            if (!creditWithFlyingPieces || amount <= 0m) return;

            var t = TrackFor(rewardId);
            if (t == null || !t.HasValue) return;

            if (!t.Held)
            {
                t.HeldFrom = t.Shown;
                t.HeldPending = 0m;
                t.HeldFlashed = false;
                t.Held = true;
            }

            // This IS the change — so the "first value after opening is a placeholder, snap it" rule can't claim it
            // and skip the walk on a counter that happened to be sitting at zero.
            t.SawChange = true;

            // ACCUMULATE. A second burst for the same currency adds to the first rather than replacing it — five taps
            // in a row are five payouts, and the counter has to be walking toward their sum. Only ever raises the
            // target, so a wallet value that already includes this payout is left alone.
            t.HeldPending += amount;
            var expected = t.HeldFrom + t.HeldPending;
            if (expected > t.Target) t.Target = expected;

            float grace = Mathf.Max(0.5f, holdTimeout);
            t.HeldUntil = Time.unscaledTime + grace;
            t.OptimisticUntil = t.HeldUntil;
            t.Roll?.Kill();
        }

        private void OnPieceLanded(string rewardId, float progress01)
        {
            var t = TrackFor(rewardId);
            if (t == null || !t.Held) return;

            decimal span = t.Target - t.HeldFrom;
            if (span <= 0m) { ReleaseHold(t); return; }

            t.Shown = t.HeldFrom + span * (decimal)Mathf.Clamp01(progress01);
            Paint(t);

            if (!t.HeldFlashed) { t.HeldFlashed = true; Flash(t, gain: true); }   // one flash for the payout, not one per piece
            Pop(t, perPiecePunch);

            if (progress01 >= 1f) ReleaseHold(t);
        }

        private void OnBurstEnded(string rewardId)
        {
            var t = TrackFor(rewardId);
            if (t != null && t.Held) ReleaseHold(t);
        }

        /// <summary>Stop holding and land on the truth — rolling the remainder if the pieces didn't finish the job.</summary>
        private void ReleaseHold(Track t)
        {
            t.Held = false;
            t.HeldPending = 0m;
            if (t.Shown == t.Target) return;
            if (t.SkipRoll) Snap(t, t.Target);
            else Roll(t, t.Target);
        }

        private void Update()
        {
            // The deadline. A hold exists only because pieces are coming; if they never arrive — the panel closed, the
            // fly component was destroyed, a coroutine was cut — the label must stop lying about the balance. Cheap:
            // it does nothing at all unless something is actually held.
            for (int i = 0; i < _tracks.Count; i++)
            {
                var t = _tracks[i];

                // The deferred truth. A balance lower than the optimistic figure was set aside rather than rolled to,
                // in case a queued claim's confirmation was simply late. Once the grace is up and nothing higher has
                // arrived, it WAS the truth — a refused claim — so the number rolls back down to it.
                if (t.HasPendingWallet && Time.unscaledTime >= t.OptimisticUntil)
                {
                    t.HasPendingWallet = false;
                    decimal v = t.PendingWallet;
                    if (v != t.Target)
                    {
                        bool up = v > t.Target;
                        if (t.SkipRoll) Snap(t, v); else Roll(t, v);
                        Pop(t);
                        Flash(t, up);
                    }
                }

                if (!t.Held || Time.unscaledTime < t.HeldUntil) continue;
                ReleaseHold(t);
                // Clear the arm too, or the very next credit is held all over again. Done AFTER the release so the
                // BurstEnded it raises finds nothing left to do.
                RewardFlyTarget.EndBurst(t.Id);
            }
        }

        private Track TrackFor(string rewardId)
        {
            if (string.IsNullOrWhiteSpace(rewardId)) return null;
            for (int i = 0; i < _tracks.Count; i++)
                if (string.Equals(_tracks[i].Id, rewardId, StringComparison.OrdinalIgnoreCase))
                    return _tracks[i];
            return null;
        }

        private void Snap(Track t, decimal value)
        {
            t.Roll?.Kill();
            t.Shown = t.Target = value;
            t.HasValue = true;
            Paint(t);
        }

        private void Roll(Track t, decimal to)
        {
            decimal from = t.Shown;
            float duration = RollDuration(from, to);

            t.Roll?.Kill();
            t.Target = to;
            // Interpolate a 0→1 FACTOR and rebuild the value in decimal from it, rather than tweening the balance as a
            // float: a float carries about seven digits, so a multi-million chip balance would visibly quantise as it
            // counted. This keeps the arithmetic exact and still gives a real, killable, eased tween.
            t.Roll = DOVirtual.Float(0f, 1f, duration, u =>
                {
                    t.Shown = from + (to - from) * (decimal)u;
                    Paint(t);
                })
                .SetEase(Ease.OutQuad)          // fast off the mark, settling into the figure — a counter spinning down
                .SetUpdate(true)                // unscaled: the HUD still counts over a paused game
                .OnComplete(() => { t.Shown = to; Paint(t); });
        }

        /// <summary>
        /// How long to roll, from how big the change is RELATIVE to the balance. +1,000 on 2.7M is noise and gets the
        /// floor; doubling a small Kash balance gets the full length. The square root keeps middling changes from
        /// collapsing straight to the minimum.
        /// </summary>
        private float RollDuration(decimal from, decimal to)
        {
            decimal jump = to > from ? to - from : from - to;
            decimal reference = Math.Max(1m, Math.Abs(from)) * (decimal)Mathf.Max(0.01f, fullRollFraction);
            float f = Mathf.Clamp01((float)decimal.Divide(jump, reference));
            return Mathf.Max(minRollSeconds, rollSeconds * Mathf.Sqrt(f));
        }

        private void Pop(Track t, float strength = 1f)
        {
            if (punchScale <= 0f || punchSeconds <= 0f || t.Punch == null || strength <= 0f) return;

            // Capture the rest scale LAZILY. HUD panels commonly animate in from scale 0, and a base captured at Awake
            // would be that 0 — every pop then scales 0 → 0, i.e. nothing, permanently.
            if (!t.BaseScaleCaptured)
            {
                var s = t.Punch.localScale;
                t.BaseScale = (s.x <= 0.0001f || s.y <= 0.0001f) ? Vector3.one : s;
                t.BaseScaleCaptured = true;
            }

            t.Pop?.Kill();
            t.Punch.localScale = t.BaseScale;   // a pop interrupting a pop starts from rest, never from a stretched state
            var punch = t.Punch;
            var rest = t.BaseScale;
            t.Pop = punch.DOPunchScale(rest * (punchScale * strength), punchSeconds, 1, 0.6f)
                .SetUpdate(true)
                .OnComplete(() => punch.localScale = rest);
        }

        private void Flash(Track t, bool gain)
        {
            if (!flashColour || flashSeconds <= 0f || t.Label == null) return;
            // A TMP vertex gradient wins over .color, so the tint would silently never appear. Leave those labels be
            // rather than switching off artwork someone deliberately authored.
            if (t.Label.enableVertexGradient) return;

            if (!t.BaseColourCaptured) { t.BaseColour = t.Label.color; t.BaseColourCaptured = true; }

            t.Flash?.Kill();
            t.Label.color = gain ? gainColour : lossColour;
            t.Flash = t.Label.DOColor(t.BaseColour, flashSeconds).SetUpdate(true);
        }

        private void Paint(Track t)
        {
            if (t.Label != null) t.Label.text = t.Shown.ToString(moneyFormat);
        }

        /// <summary>Kill anything in flight and put the label back at rest, showing its true value.</summary>
        private void Settle(Track t)
        {
            t.Roll?.Kill(); t.Pop?.Kill(); t.Flash?.Kill();
            t.Roll = t.Pop = t.Flash = null;
            t.Held = false;

            if (t.Punch != null && t.BaseScaleCaptured) t.Punch.localScale = t.BaseScale;
            if (t.Label == null) return;
            if (t.BaseColourCaptured) t.Label.color = t.BaseColour;
            // Land on the TARGET, not on wherever the roll had got to — a panel closed mid-count must not leave the
            // label (and the cached value it rolls from next time) showing a partial figure.
            if (t.HasValue) { t.Shown = t.Target; Paint(t); }
        }
    }
}
