using System.Collections;
using System.Collections.Generic;
using Khela.Common.Rewards;
using PlayCard.Core;
using PlayCard.UI.RewardFly;
using UnityEngine;

namespace PlayCard.Pass
{
    /// <summary>
    /// Turns a claimed pass day into the collect juice: the rewards the SERVER actually paid burst out of the card
    /// that was tapped and fly to their counters — chips to the chip counter, Kash to the Kash counter.
    ///
    /// It animates <see cref="PassState.RewardsGranted"/>, i.e. the applied amounts, never the ladder's advertised
    /// ones: a chest rolls its contents server-side, so what flies is what was really credited.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PassRewardFlyBinder : MonoBehaviour
    {
        [SerializeField] private PassScreen screen;
        [Tooltip("The juice service. Empty = look for one on this object or its children.")]
        [SerializeField] private RewardFly fly;
        [Tooltip("Fallback source when the tapped card is already gone (a rebuild swapped it) — usually the panel centre.")]
        [SerializeField] private RectTransform fallbackSource;

        private void Awake()
        {
            if (screen == null) screen = GetComponentInChildren<PassScreen>(true);
            if (fly == null) fly = GetComponentInChildren<RewardFly>(true);
        }

        private void OnEnable() => PassState.Instance.RewardsGranted += OnRewardsGranted;

        private void OnDisable() => PassState.Instance.RewardsGranted -= OnRewardsGranted;

        private void OnRewardsGranted(IReadOnlyList<GrantedLineDto> granted, decimal newChipBalance)
        {
            if (fly == null || granted == null || granted.Count == 0) return;

            var source = screen != null && screen.LastClaimSource != null ? screen.LastClaimSource : fallbackSource;
            if (source == null) return;

            var items = new List<RewardFlyItem>(granted.Count);
            foreach (var line in granted)
            {
                if (line == null || line.Amount <= 0m) continue;

                // XP has no counter to fly to in this layout; skipping it here means a missing HUD costs nothing.
                var id = string.IsNullOrEmpty(line.Id) ? KindName(line.Kind) : line.Id;

                // ARM the balance hold NOW, synchronously, before returning to PassState — because the very next thing
                // it does is refetch the wallet, and that push is what would otherwise snap the counter to its new
                // value a second and a half before the first chip arrives. Arming inside RewardFly is too late: the
                // burst deliberately waits for the ladder to settle first. No counter for this reward means nothing
                // will fly, ArmBurst refuses, and the balance updates normally — which is the correct behaviour.
                RewardFlyTarget.ArmBurst(id);

                items.Add(new RewardFlyItem
                {
                    RewardId = id,
                    Amount = line.Amount,
                    Icon = IconFrom(line),
                });
            }

            if (items.Count > 0 && isActiveAndEnabled)
            {
                StartCoroutine(BurstFrom(source.position, items));
                return;
            }

            // Armed but not flying (nothing to show, or this binder is disabled): hand the holds straight back rather
            // than making a HUD wait out its timeout on a burst that is never coming.
            foreach (var item in items) RewardFlyTarget.EndBurst(item.RewardId);
        }

        /// <summary>
        /// Pin the burst to WHERE the card was, then let the claim's UI work finish before spawning anything.
        ///
        /// Two things happen to the tapped card the moment a claim lands: it is respawned as its collected variant, and
        /// the panel re-lays out around it. Bursting into that frame means the pieces' first steps are eaten by it — the
        /// burst looks frozen, then jumps. Waiting for the end of the frame costs one frame of latency nobody can see
        /// and buys a burst that starts clean. The position is captured as a VALUE first: the card is gone by then.
        /// </summary>
        private IEnumerator BurstFrom(Vector3 world, List<RewardFlyItem> items)
        {
            yield return new WaitForEndOfFrame();
            yield return null;

            if (fly == null) yield break;

            var anchor = Anchor();
            anchor.position = world;
            fly.Play(items, anchor);
        }

        /// <summary>A parked, reusable transform standing in for the card that paid — it outlives the card.</summary>
        private RectTransform Anchor()
        {
            if (_anchor != null) return _anchor;

            var go = new GameObject("RewardFly_Source", typeof(RectTransform));
            _anchor = (RectTransform)go.transform;
            _anchor.SetParent(fly.transform, false);
            _anchor.sizeDelta = Vector2.zero;
            return _anchor;
        }

        private RectTransform _anchor;

        /// <summary>Use the artwork the server sent for this reward, if it's already downloaded — the flying pieces
        /// then match the card the player just tapped. Not yet cached: the RewardFly's own icon set is used.</summary>
        private static Sprite IconFrom(GrantedLineDto line)
        {
            if (line.Images == null || line.Images.Count == 0) return null;
            RemoteImage.TryGetCached(line.Images[0], out var sprite);
            return sprite;
        }

        private static string KindName(int kind) => kind switch
        {
            1 => "XP",
            2 => "Chest",
            3 => "Cosmetic",
            4 => "Item",
            _ => "Chips",
        };
    }
}
