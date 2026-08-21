using System;
using System.Threading.Tasks;
using Khela.Common.Piggy;
using PlayCard.Piggy.Net;
using UnityEngine;

namespace PlayCard.Piggy
{
    /// <summary>
    /// The one place the piggy-bank snapshot lives, so every widget showing it — the Home HUD, a shop entry, a
    /// full-bank badge — reads the same number without each fetching for itself. A plain singleton, so it survives
    /// scene loads.
    ///
    /// Nothing here decides anything. <see cref="CanBreak"/> is true because the SERVER said so, and the countdown is
    /// the server's, merely ticked down locally between refreshes.
    /// </summary>
    public sealed class PiggyState
    {
        private static PiggyState _instance;
        public static PiggyState Instance => _instance ??= new PiggyState();

        /// <summary>Raised whenever <see cref="Current"/> changes — bind widgets to this.</summary>
        public event Action<PiggyStateDto> Changed;

        /// <summary>Raised the first time a snapshot arrives showing a bank that is ready to buy. The cue for a badge,
        /// a sound, or a nudge — fired once per fill rather than on every refresh that happens to still be full.</summary>
        public event Action<PiggyStateDto> BecameReady;

        /// <summary>The last snapshot, or null before the first fetch.</summary>
        public PiggyStateDto Current { get; private set; }

        public bool Enabled => Current != null && Current.Enabled;
        public bool CanBreak => Enabled && Current.CanBreak;

        /// <summary>Is a countdown running? Show the timer label off THIS, never off a seconds value of zero — a bank
        /// the player hasn't been shown yet has no clock at all, which is not the same as an expired one.</summary>
        public bool TimerRunning => Enabled && Current.TimerRunning;

        /// <summary>How stale a cached snapshot may be before <see cref="RefreshAsync"/> re-fetches it.</summary>
        private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Is there a token to call with yet?
        ///
        /// Widgets refresh on enable, and on a cold start that happens while the device is still registering — so the
        /// first fetch would fire before there is a token, take a 401, and then fail to parse its empty body. It reads
        /// as a server fault in the logs and is nothing of the sort. Not calling is the fix.
        /// </summary>
        private static bool IsSignedIn
            => PlayCard.Account.AccountManager.Instance == null   // no auth in this scene: let the call through
            || !string.IsNullOrEmpty(PlayCard.Account.AccountManager.Instance.JwtToken);

        private bool _refreshing;
        private bool _wasReady;
        private DateTime _fetchedAtUtc;

        /// <summary>Cheap to call on every screen open: it no-ops while a request is in flight and reuses a snapshot
        /// that is still young.</summary>
        public async Task<PiggyStateDto> RefreshAsync(bool force = false)
        {
            if (_refreshing) return Current;
            if (!IsSignedIn) return Current;
            if (!force && Current != null && DateTime.UtcNow - _fetchedAtUtc < Freshness) return Current;

            _refreshing = true;
            try
            {
                var result = await PiggyRestClient.Instance.GetStateAsync();
                if (!result.Ok || result.Value == null)
                {
                    Debug.LogWarning($"[PiggyState] fetch failed: {result.Error}");
                    return Current;
                }

                Apply(result.Value);
                return Current;
            }
            finally { _refreshing = false; }
        }

        /// <summary>
        /// Tell the server the player is looking at a full bank — this is what starts their countdown.
        ///
        /// Call it from the widget the moment the ready state is actually shown, and nowhere else. Doing it on a plain
        /// refresh would start deadlines for banks nobody ever saw, which is the one failure this design exists to
        /// avoid. Harmless to call twice: only the first sighting counts, server-side.
        /// </summary>
        public async Task<PiggyStateDto> MarkSeenAsync()
        {
            if (!IsSignedIn) return Current;

            var result = await PiggyRestClient.Instance.MarkSeenAsync();
            if (!result.Ok || result.Value == null)
            {
                Debug.LogWarning($"[PiggyState] mark-seen failed: {result.Error}");
                return Current;
            }

            Apply(result.Value);
            return Current;
        }

        /// <summary>
        /// The chips have finished flying — tell the server, so the next celebration measures from here.
        ///
        /// Sent AFTER the animation. Acknowledging first would throw the whole delta away if the app died mid-burst,
        /// and a player who never actually saw their chips arrive would never be shown them again.
        /// </summary>
        /// <summary>
        /// Buy the bank. Returns the SERVER'S result - what it actually paid and the bank it left behind.
        ///
        /// The caller animates the payout from <c>result.Amount</c>, never from a figure it worked out itself: on an
        /// early break the payout is deliberately not what the bank held, and a client that assumed otherwise would
        /// show the player the wrong number for the money they just spent.
        ///
        /// A refusal comes back as <c>Ok = false</c> with a reason, not as a thrown error.
        /// </summary>
        public async Task<PiggyBreakResultDto> BreakAsync(PiggyBreakOption option, string purchaseId)
        {
            if (!IsSignedIn) return new PiggyBreakResultDto { Ok = false, Error = "Not signed in." };

            var result = await PiggyRestClient.Instance.BreakAsync(option, purchaseId);
            if (!result.Ok || result.Value == null)
            {
                Debug.LogWarning($"[PiggyState] break failed: {result.Error}");
                return new PiggyBreakResultDto { Ok = false, Error = result.Error ?? "The break failed." };
            }

            // The response carries the fresh bank, so adopt it rather than refetching - one round trip, and no
            // window where the HUD still shows a full bank that has already been sold.
            if (result.Value.Piggy != null) Apply(result.Value.Piggy);

            return result.Value;
        }

        public async Task<PiggyStateDto> MarkCelebratedAsync()
        {
            if (!IsSignedIn) return Current;

            var result = await PiggyRestClient.Instance.MarkCelebratedAsync();
            if (!result.Ok || result.Value == null)
            {
                Debug.LogWarning($"[PiggyState] mark-celebrated failed: {result.Error}");
                return Current;
            }

            Apply(result.Value);
            return Current;
        }

        private void Apply(PiggyStateDto state)
        {
            Current = state;
            _fetchedAtUtc = DateTime.UtcNow;

            // Edge-triggered, not level-triggered: "it just filled" is a moment worth a badge or a sound, while "it is
            // still full" is the state of every refresh until it's bought and would fire the nudge forever.
            bool ready = state != null && state.Enabled && state.CanBreak;
            bool justBecameReady = ready && !_wasReady;
            _wasReady = ready;

            Changed?.Invoke(Current);
            if (justBecameReady) BecameReady?.Invoke(Current);
        }
    }
}
