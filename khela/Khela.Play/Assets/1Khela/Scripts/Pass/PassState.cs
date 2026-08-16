using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Khela.Common.Pass;
using Khela.Common.Rewards;
using PlayCard.Pass.Net;
using UnityEngine;

namespace PlayCard.Pass
{
    /// <summary>
    /// The one place the pass snapshot lives, so any scene can show a "claimable today" badge without fetching for
    /// itself. Survives scene loads (it's a plain singleton, not a scene object), caches the last snapshot, and
    /// refreshes when it goes stale — the player's day flipping, or a claim landing.
    ///
    /// Everything here is a mirror of a SERVER decision. <see cref="HasClaimable"/> is true because the server said a
    /// day is claimable, never because the device clock rolled over.
    /// </summary>
    public sealed class PassState
    {
        private static PassState _instance;
        public static PassState Instance => _instance ??= new PassState();

        /// <summary>Raised whenever <see cref="Current"/> changes — bind badges and buttons to this.</summary>
        public event Action<PassStateDto> Changed;

        /// <summary>Raised after a successful claim with what was actually paid out, so a HUD anywhere can animate
        /// it. The screen doesn't own this — the balance widget lives in whatever scene is loaded.</summary>
        public event Action<IReadOnlyList<GrantedLineDto>, decimal> RewardsGranted;

        /// <summary>The last snapshot, or null before the first fetch.</summary>
        public PassStateDto Current { get; private set; }

        /// <summary>True while a fetch or a claim is in flight — for spinners, and to stop double taps.</summary>
        public bool Busy { get; private set; }

        /// <summary>Is a pass running at all? False also covers "not fetched yet".</summary>
        public bool Active => Current != null && Current.Active;

        /// <summary>Something is claimable for free right now — the badge condition.</summary>
        public bool HasClaimable => Active && Current.Nodes != null && Current.Nodes.Any(n => n != null && n.ClaimableNow);

        /// <summary>Missed days a subscription would hand back — the conversion number.</summary>
        public int MissedDays => Active ? Current.GoldenLockedCount : 0;

        public bool IsGolden => Active && Current.IsGolden;

        /// <summary>When the player's own day next flips. <see cref="DateTime.MinValue"/> when nothing is loaded.</summary>
        public DateTime NextDayUtc => Active ? Current.NextDayUtc : DateTime.MinValue;

        /// <summary>Time left in the player's cycle.</summary>
        public TimeSpan CycleRemaining => Active ? Max(Current.CycleEndUtc - DateTime.UtcNow, TimeSpan.Zero) : TimeSpan.Zero;

        private DateTime _fetchedAtUtc;

        /// <summary>
        /// Fetch a fresh snapshot. Cheap to call on every screen open: it no-ops while a request is in flight, and
        /// without <paramref name="force"/> it reuses a snapshot that is still young AND still on the same day.
        /// </summary>
        public async Task<PassStateDto> RefreshAsync(bool force = false, string passKey = null)
        {
            if (Busy) return Current;
            if (!force && IsFresh()) return Current;

            Busy = true;
            try
            {
                var result = await PassRestClient.Instance.GetStateAsync(passKey);
                if (!result.Ok)
                {
                    Debug.LogWarning($"[PassState] refresh failed: {result.Error}");
                    return Current;   // keep the last good snapshot rather than blanking the UI
                }

                Current = result.Value;
                _fetchedAtUtc = DateTime.UtcNow;
                Changed?.Invoke(Current);
                return Current;
            }
            finally { Busy = false; }
        }

        /// <summary>
        /// Claim one day. <paramref name="useAds"/> spends rewarded-ad credits on a missed day — only ever after the
        /// ad actually played and the network's callback credited it; the server refuses otherwise.
        /// Returns the server's result so the caller can show its error verbatim.
        /// </summary>
        public async Task<PassClaimResultDto> ClaimAsync(int? day = null, bool useAds = false, string passKey = null)
        {
            if (Busy) return null;

            Busy = true;
            try
            {
                var result = await PassRestClient.Instance.ClaimAsync(day, useAds, passKey);
                if (!result.Ok || result.Value == null || !result.Value.Ok)
                {
                    var message = result.Value?.Error ?? result.Error;
                    Debug.LogWarning($"[PassState] claim failed: {message}");
                    return result.Value ?? new PassClaimResultDto { Ok = false, Error = message };
                }

                RaiseGranted(result.Value);
                return result.Value;
            }
            finally
            {
                Busy = false;
                await RefreshAsync(force: true, passKey);   // the ladder moved; re-read rather than guess
            }
        }

        /// <summary>Claim everything currently free, oldest first.</summary>
        public async Task<PassClaimResultDto> ClaimAllAsync(string passKey = null)
        {
            if (Busy) return null;

            Busy = true;
            try
            {
                var result = await PassRestClient.Instance.ClaimAllAsync(passKey);
                if (result.Ok && result.Value != null && result.Value.Ok) RaiseGranted(result.Value);
                return result.Value;
            }
            finally
            {
                Busy = false;
                await RefreshAsync(force: true, passKey);
            }
        }

        /// <summary>Ask the server for an ad-intent token for a missed day. Hand <see cref="PassAdIntentDto.Token"/>
        /// to the ad SDK as custom data; the credit arrives via the network's server callback, not from here.</summary>
        public async Task<PassAdIntentDto> RequestAdIntentAsync(int day, string passKey = null)
        {
            var result = await PassRestClient.Instance.AdIntentAsync(day, passKey);
            if (result.Ok && result.Value != null) return result.Value;
            return new PassAdIntentDto { Ok = false, Error = result.Value?.Error ?? result.Error };
        }

        /// <summary>Forget everything — on sign-out, so the next account doesn't inherit this one's ladder.</summary>
        public void Clear()
        {
            Current = null;
            _fetchedAtUtc = default;
            Changed?.Invoke(null);
        }

        private void RaiseGranted(PassClaimResultDto result)
        {
            if (result.Granted != null && result.Granted.Count > 0)
                RewardsGranted?.Invoke(result.Granted, result.NewChipBalance);

            // Re-pull the wallet so every balance HUD in the loaded scene updates itself. Deliberately a refetch and
            // not "trust the number in the response": a claim can also move Kash, Gems or XP, and the server is the
            // only thing that knows all of them.
            var wallet = PlayCard.Game.Wallet.WalletManager.Instance;
            if (wallet != null) _ = wallet.RefreshAsync();
        }

        /// <summary>A snapshot stays usable for a couple of minutes, and never past the moment the player's day
        /// flips — after that the ladder genuinely changed and a stale badge would lie.</summary>
        private bool IsFresh()
        {
            if (Current == null) return false;
            var now = DateTime.UtcNow;
            if (Current.Active && now >= Current.NextDayUtc) return false;
            return now - _fetchedAtUtc < TimeSpan.FromMinutes(2);
        }

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    }
}
