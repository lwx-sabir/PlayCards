using System;
using System.Threading.Tasks;
using Khela.Common.Pass;
using UnityEngine;

namespace PlayCard.Pass
{
    /// <summary>
    /// The glue on the pass prefab's root: fetches through <see cref="PassState"/>, feeds <see cref="PassScreen"/>,
    /// and turns the screen's intents back into server calls.
    ///
    /// Nothing below decides anything about the pass. A claim is sent and the server's answer is re-read; a refusal
    /// is shown, not worked around. That's what keeps the client honest about which days are claimable.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PassScreen))]
    public sealed class PassPanel : MonoBehaviour
    {
        [Tooltip("Leave empty to use the PassScreen on this object.")]
        [SerializeField] private PassScreen screen;

        [Tooltip("Which pass program to show. Empty = the monthly pass.")]
        [SerializeField] private string passKey;

        [Tooltip("Close the panel automatically after claiming the last available day.")]
        [SerializeField] private bool closeWhenNothingLeft;

        /// <summary>
        /// The player wants Golden. Wire this to the IAP sheet when purchasing ships — until then the panel just
        /// logs, so the button is honest rather than silently dead.
        /// </summary>
        public event Action SubscribeRequested;

        /// <summary>
        /// A rewarded ad needs to play for <c>day</c>, using the server-issued <c>token</c> as the ad's custom data.
        /// The ad SDK integration subscribes here; the CREDIT arrives from the network's server-to-server callback,
        /// never from whatever the SDK tells the client, so this handler's only job is to show the ad.
        /// </summary>
        public event Action<int, string> AdRequested;

        private void Awake()
        {
            if (screen == null) screen = GetComponent<PassScreen>();
            screen.ClaimRequested += OnClaimRequested;
            screen.SubscribeRequested += OnSubscribeRequested;
        }

        private void OnEnable() => PassState.Instance.Changed += OnStateChanged;

        private void OnDisable() => PassState.Instance.Changed -= OnStateChanged;

        private void OnDestroy()
        {
            if (screen == null) return;
            screen.ClaimRequested -= OnClaimRequested;
            screen.SubscribeRequested -= OnSubscribeRequested;
        }

        /// <summary>Open the pass: render whatever is cached immediately (no empty frame), then refresh.</summary>
        public async void Open()
        {
            gameObject.SetActive(true);

            var cached = PassState.Instance.Current;
            if (cached != null) screen.Render(cached);

            var fresh = await PassState.Instance.RefreshAsync(passKey: NullIfEmpty(passKey));
            if (this != null && fresh != null) screen.Render(fresh);
        }

        /// <summary>Close it. Also on the screen's own back button.</summary>
        public void Close() => gameObject.SetActive(false);

        private void OnStateChanged(PassStateDto state)
        {
            if (this == null || !gameObject.activeInHierarchy) return;
            screen.Render(state);

            if (closeWhenNothingLeft && state != null && state.Active && !PassState.Instance.HasClaimable)
                { /* left open on purpose: the ladder is still worth looking at */ }
        }

        private async void OnClaimRequested(int day, bool useAds)
        {
            if (PassState.Instance.Busy) return;   // one tap at a time; the server would refuse the second anyway

            if (useAds)
            {
                await HandleAdUnlockAsync(day);
                return;
            }

            var result = await PassState.Instance.ClaimAsync(day, useAds: false, passKey: NullIfEmpty(passKey));
            Report(result, day);
        }

        /// <summary>
        /// A missed day bought with rewarded ads. If the player already holds enough VERIFIED credits (the network
        /// called us back earlier), the claim goes straight through; otherwise we ask the server for an intent token
        /// and hand it to whoever shows ads. We never claim on the SDK's say-so.
        /// </summary>
        private async Task HandleAdUnlockAsync(int day)
        {
            var state = PassState.Instance.Current;
            bool enoughCredits = state != null && state.AdCreditsHeld >= Mathf.Max(1, state.AdsPerUnlock);

            if (enoughCredits)
            {
                var claimed = await PassState.Instance.ClaimAsync(day, useAds: true, passKey: NullIfEmpty(passKey));
                Report(claimed, day);
                return;
            }

            var intent = await PassState.Instance.RequestAdIntentAsync(day, NullIfEmpty(passKey));
            if (intent == null || !intent.Ok)
            {
                Debug.LogWarning($"[Pass] day {day} can't be unlocked with ads: {intent?.Error}");
                return;
            }

            if (AdRequested == null)
            {
                Debug.LogWarning($"[Pass] no ad handler wired — day {day} needs {intent.AdsRequired} ad view(s). " +
                                 "Subscribe to PassPanel.AdRequested when the ad SDK lands.");
                return;
            }

            AdRequested.Invoke(day, intent.Token);
            // Nothing else happens here: the credit lands via the network's callback, and the next refresh sees it.
        }

        private void OnSubscribeRequested()
        {
            if (SubscribeRequested != null) { SubscribeRequested.Invoke(); return; }
            Debug.Log("[Pass] Golden purchase requested — IAP isn't wired yet.");
        }

        private static void Report(PassClaimResultDto result, int day)
        {
            if (result == null) return;
            if (!result.Ok) { Debug.LogWarning($"[Pass] claim day {day} refused: {result.Error}"); return; }
            // The reward animation is driven by PassState.RewardsGranted, so whatever HUD is in the current scene
            // gets it — the panel deliberately doesn't own that.
        }

        private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
