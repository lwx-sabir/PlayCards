using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Khela.Common.Daily;
using Khela.Common.Rewards;
using PlayCard.Daily.Net;
using UnityEngine;

namespace PlayCard.Daily
{
    /// <summary>
    /// The one place the daily login snapshot lives, so any scene can show a "collect me" badge without fetching for
    /// itself. Survives scene loads (a plain singleton, not a scene object), caches the last snapshot, and refreshes
    /// when it goes stale — the player's day flipping, or a claim landing.
    ///
    /// Everything here mirrors a SERVER decision. <see cref="HasClaimable"/> is true because the server said a day is
    /// claimable, never because the device clock rolled over.
    /// </summary>
    public sealed class DailyState
    {
        private static DailyState _instance;
        public static DailyState Instance => _instance ??= new DailyState();

        /// <summary>Raised whenever <see cref="Current"/> changes — bind badges and panels to this.</summary>
        public event Action<DailyStateDto> Changed;

        /// <summary>Raised after a successful claim with what was actually paid out, so a HUD anywhere can animate it.
        /// The screen doesn't own this — the balance widget lives in whatever scene is loaded.</summary>
        public event Action<IReadOnlyList<GrantedLineDto>, decimal> RewardsGranted;

        /// <summary>The last snapshot, or null before the first fetch.</summary>
        public DailyStateDto Current { get; private set; }

        /// <summary>True while a fetch or a claim is in flight — for spinners.</summary>
        public bool Busy => _refreshing || _claiming;

        // Tracked SEPARATELY, and this matters: a refresh runs on open and after every claim, and against a distant
        // database that is seconds long. Sharing one flag meant a tap landing in that window was dropped before it was
        // ever sent — the tile flipped optimistically, the claim returned "not attempted", and the UI rolled it back
        // while the server sat there having done nothing wrong. A background read must never swallow a deliberate act.
        private bool _refreshing;
        private bool _claiming;

        /// <summary>Is a daily reward running at all? False also covers "not fetched yet".</summary>
        public bool Active => Current != null && Current.Active;

        /// <summary>Something is claimable for free right now — the badge condition.</summary>
        public bool HasClaimable => Active && Current.Nodes != null && Current.Nodes.Any(n => n != null && n.ClaimableNow);

        /// <summary>Missed days rewarded ads could still buy back.</summary>
        public int AdUnlockableCount => Active && Current.Nodes != null
            ? Current.Nodes.Count(n => n != null && n.AdUnlockable) : 0;

        /// <summary>When the player's own day next flips. <see cref="DateTime.MinValue"/> when nothing is loaded.</summary>
        public DateTime NextDayUtc => Active ? Current.NextDayUtc : DateTime.MinValue;

        /// <summary>
        /// Is there a token to call with yet?
        ///
        /// Every screen refreshes on enable, and on a cold start those enables happen while the device is still
        /// registering and logging in — so the very first fetch fires before there is a token, takes a 401, and the
        /// EMPTY body of that 401 then fails to parse ("The input does not contain any JSON tokens"). It looks like a
        /// server fault in the logs and is nothing of the sort. Not calling is the fix; the button re-refreshes on
        /// AccountManager.OnReady.
        /// </summary>
        private static bool IsSignedIn
            => PlayCard.Account.AccountManager.Instance == null   // no auth in this scene: let the call through
            || !string.IsNullOrEmpty(PlayCard.Account.AccountManager.Instance.JwtToken);

        private DateTime _fetchedAtUtc;

        /// <summary>
        /// Fetch a fresh snapshot. Cheap to call on every screen open: it no-ops while a request is in flight, and
        /// without <paramref name="force"/> it reuses a snapshot that is still young AND still on the same day.
        /// </summary>
        public async Task<DailyStateDto> RefreshAsync(bool force = false)
        {
            if (_refreshing) return Current;
            if (!IsSignedIn) return Current;   // see IsSignedIn — a pre-auth fetch is a guaranteed 401
            if (!force && IsFresh()) return Current;

            _refreshing = true;
            try
            {
                var result = await DailyRestClient.Instance.GetStateAsync();
                if (!result.Ok)
                {
                    Debug.LogWarning($"[DailyState] refresh failed: {result.Error}");
                    return Current;   // keep the last good snapshot rather than blanking the UI
                }

                Current = result.Value;
                _fetchedAtUtc = DateTime.UtcNow;
                Changed?.Invoke(Current);
                return Current;
            }
            finally { _refreshing = false; }
        }

        /// <summary>
        /// Claim a day. <paramref name="useAds"/> spends rewarded-ad credits on a missed day — only ever after the ad
        /// actually played and the network's callback credited it; the server refuses otherwise.
        /// Returns the server's result so the caller can show its error verbatim.
        /// </summary>
        public async Task<DailyClaimResultDto> ClaimAsync(int? day = null, bool useAds = false)
        {
            // QUEUE, never drop. A claim is a deliberate act; refusing it because another one is still in flight means
            // a player tapping five days gets one reward and four tiles that flip back — which is indistinguishable
            // from the server rejecting them. Against a distant database each claim is seconds long, so this is the
            // normal case, not a stress test. They serialise: the server's per-node unique index is what actually
            // guarantees correctness, this only keeps the requests orderly.
            System.Threading.Interlocked.Increment(ref _queuedClaims);
            await _claimLock.WaitAsync();

            _claiming = true;
            try
            {
                var result = await DailyRestClient.Instance.ClaimAsync(day, useAds);

                // "Already collected" is a SUCCESS from the player's side: the day is theirs and the reward is paid.
                // Only the payout is missing, because it happened on the earlier request — so the tile must stay
                // collected and nothing must fly. Treating it as a failure is what made a re-tapped day visibly
                // un-collect itself.
                if (result.Value != null && result.Value.AlreadyClaimed)
                {
                    Debug.Log($"[DailyState] day {day} was already collected — keeping it collected.");
                    result.Value.Ok = true;
                    return result.Value;
                }

                if (!result.Ok || result.Value == null || !result.Value.Ok)
                {
                    var message = result.Value?.Error ?? result.Error;
                    Debug.LogWarning($"[DailyState] claim day {day} failed: {message}");
                    return result.Value ?? new DailyClaimResultDto { Ok = false, Error = message };
                }

                RaiseGranted(result.Value);
                return result.Value;
            }
            finally
            {
                _claiming = false;
                _claimLock.Release();

                // Re-read ONCE, after the last queued claim. Refreshing per claim would fire a round trip between
                // every tap and re-render the ladder underneath the ones still waiting.
                if (System.Threading.Interlocked.Decrement(ref _queuedClaims) == 0)
                    await RefreshAsync(force: true);
            }
        }

        private readonly System.Threading.SemaphoreSlim _claimLock = new System.Threading.SemaphoreSlim(1, 1);
        private int _queuedClaims;

        /// <summary>Ask the server for an ad-intent token for a missed day. Hand <see cref="DailyAdIntentDto.Token"/>
        /// to the ad SDK as custom data; the credit arrives via the network's server callback, not from here.</summary>
        public async Task<DailyAdIntentDto> RequestAdIntentAsync(int day)
        {
            var result = await DailyRestClient.Instance.AdIntentAsync(day);
            if (result.Ok && result.Value != null) return result.Value;
            return new DailyAdIntentDto { Ok = false, Error = result.Value?.Error ?? result.Error };
        }

        /// <summary>Forget everything — on sign-out, so the next account doesn't inherit this one's ladder.</summary>
        public void Clear()
        {
            Current = null;
            _fetchedAtUtc = default;
            Changed?.Invoke(null);
        }

        private void RaiseGranted(DailyClaimResultDto result)
        {
            if (result.Granted != null && result.Granted.Count > 0)
                RewardsGranted?.Invoke(result.Granted, result.NewChipBalance);

            // Re-pull the wallet so every balance HUD in the loaded scene updates itself. Deliberately a refetch and
            // not "trust the number in the response": a claim can also move Kash, Gems or XP, and the server is the
            // only thing that knows all of them.
            //
            // Raised BEFORE the refetch on purpose — a HUD holding its number for the collect animation has to arm
            // itself before the wallet push lands, or the balance finishes before the chips have flown.
            var wallet = PlayCard.Game.Wallet.WalletManager.Instance;
            if (wallet != null) _ = wallet.RefreshAsync();
        }

        /// <summary>A snapshot stays usable for a couple of minutes, and never past the moment the player's day flips
        /// — after that the ladder genuinely changed and a stale badge would lie.</summary>
        private bool IsFresh()
        {
            if (Current == null) return false;
            var now = DateTime.UtcNow;
            if (Current.Active && now >= Current.NextDayUtc) return false;
            return now - _fetchedAtUtc < TimeSpan.FromMinutes(2);
        }
    }
}
