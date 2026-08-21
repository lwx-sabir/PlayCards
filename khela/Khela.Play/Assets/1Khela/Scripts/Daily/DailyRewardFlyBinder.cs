using System.Collections;
using System.Collections.Generic;
using Khela.Common.Rewards;
using PlayCard.Core;
using PlayCard.UI.RewardFly;
using UnityEngine;

namespace PlayCard.Daily
{
    /// <summary>
    /// Turns a collected daily reward into the collect juice: the rewards burst out of the tile that was tapped and
    /// fly to their counters — chips to the chip counter, Kash to the Kash counter.
    ///
    /// It animates <see cref="DailyScreen.CollectStarted"/>, which fires on the TAP rather than on the server's
    /// response. That is the whole point: a claim against a remote database can take seconds, and a burst that arrives
    /// that late has stopped being feedback. The amounts are the ladder's advertised ones — correct for every fixed
    /// reward, and the wallet refresh reconciles the counter regardless. If the server refuses, the tile rolls back
    /// (DailyScreen.RevertCollect) and the balance follows the wallet down.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DailyRewardFlyBinder : MonoBehaviour
    {
        [SerializeField] private DailyScreen screen;
        [Tooltip("The juice service. Empty = look for one on this object or its children.")]
        [SerializeField] private RewardFly fly;
        [Tooltip("Fallback source when the tapped tile is already gone (a rebuild swapped it) — usually the panel centre.")]
        [SerializeField] private RectTransform fallbackSource;

        private void Awake()
        {
            if (screen == null) screen = GetComponentInChildren<DailyScreen>(true);
            if (fly == null) fly = GetComponentInChildren<RewardFly>(true);

            // Both are auto-found, so an unassigned field is only a problem when the component genuinely isn't there —
            // and in that case the collect juice simply never happens, with nothing in the log to say why.
            if (screen == null)
                Debug.LogError($"{name}: DailyRewardFlyBinder found no DailyScreen — no collect will ever be seen. " +
                               "Put this on the daily panel root, or assign Screen.", this);
            if (fly == null)
                Debug.LogError($"{name}: DailyRewardFlyBinder found no RewardFly — nothing will fly. Add a RewardFly " +
                               "to this panel (piece prefab + fly layer), or assign Fly.", this);
        }

        private void OnEnable()
        {
            if (screen != null) screen.CollectStarted += OnCollectStarted;
        }

        private void OnDisable()
        {
            if (screen != null) screen.CollectStarted -= OnCollectStarted;
        }

        private void OnCollectStarted(int day, IReadOnlyList<RewardGrant> rewards, RectTransform source)
        {
            if (fly == null || rewards == null || rewards.Count == 0) return;

            var from = source != null ? source : fallbackSource;
            if (from == null) return;

            var items = new List<RewardFlyItem>(rewards.Count);
            foreach (var line in rewards)
            {
                // XP has no counter to fly to in this layout; skipping it here means a missing HUD costs nothing.
                if (line == null || line.Amount <= 0m || line.Kind == RewardKind.Xp) continue;

                var id = string.IsNullOrEmpty(line.Id) ? KindName(line.Kind) : line.Id;

                // ARM the balance hold now, synchronously. The wallet refresh that follows the claim is what would
                // otherwise snap the counter to its new value before a single chip has landed.
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
                StartCoroutine(BurstFrom(from.position, items));
                return;
            }

            // Armed but not flying: hand the holds straight back rather than making a HUD wait out its timeout.
            foreach (var item in items) RewardFlyTarget.EndBurst(item.RewardId);
        }

        /// <summary>
        /// Pin the burst to WHERE the tile was, then let the frame's UI work finish before spawning anything.
        ///
        /// The tile flips to its Collected art on the same tap, and the panel may re-lay out around it. Bursting into
        /// that frame means the pieces' first steps are eaten by it — the burst looks frozen, then jumps. The position
        /// is captured as a VALUE first, because the tile may be gone by the time this resumes.
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

        /// <summary>A parked, reusable transform standing in for the tile that paid — it outlives the tile.</summary>
        private RectTransform Anchor()
        {
            if (_anchor != null) return _anchor;

            var go = new GameObject("DailyFly_Source", typeof(RectTransform));
            _anchor = (RectTransform)go.transform;
            _anchor.SetParent(fly.transform, false);
            _anchor.sizeDelta = Vector2.zero;
            return _anchor;
        }

        /// <summary>Use the artwork the server sent for this reward, if it's already downloaded — the flying pieces
        /// then match the tile the player just tapped. Not yet cached: the RewardFly's own icon set is used.</summary>
        private static Sprite IconFrom(RewardGrant line)
        {
            if (line.Images == null || line.Images.Count == 0) return null;
            RemoteImage.TryGetCached(line.Images[0], out var sprite);
            return sprite;
        }

        private static string KindName(RewardKind kind) => kind switch
        {
            RewardKind.Xp => "XP",
            RewardKind.Chest => "Chest",
            RewardKind.Cosmetic => "Cosmetic",
            RewardKind.Item => "Item",
            _ => "Chips",
        };

        private RectTransform _anchor;
    }
}
