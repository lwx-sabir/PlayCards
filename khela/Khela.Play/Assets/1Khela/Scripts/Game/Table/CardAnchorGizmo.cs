using System.Collections.Generic;
using PlayCard.Game.Cards;
using PlayCard.UI;
using UnityEngine;

namespace PlayCard.Game.Table
{
    /// <summary>
    /// Editor preview for a card anchor (seat or dealer): spawns the REAL card prefab in the fan — true size, real
    /// art — exactly where it'll be dealt, AND a sample of EVERY <see cref="IAnchorLabel"/> in the scene (the value
    /// badge, the blackjack banner, …) at its own corner offset, so you can position each label before entering
    /// Play. Previews are flagged DontSave (never saved) and follow the live settings; the preview auto-rebuilds
    /// every <see cref="autoRefreshSeconds"/> so any change shows up. Inert + self-clearing in Play mode. Set
    /// <see cref="seatNumber"/> (0 = dealer / default); Preview Hands = 2 shows a split. Delete once anchors are placed.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class CardAnchorGizmo : MonoBehaviour
    {
        [Tooltip("Which seat this anchor is (1/2/3…) so the preview uses that seat's fan. 0 = dealer / default.")]
        [Min(0)] [SerializeField] private int seatNumber = 0;

        [Tooltip("How many cards to preview — a blackjack hand can reach ~7.")]
        [Range(1, 20)] [SerializeField] private int previewCount = 2;

        [Tooltip("How many HANDS to preview (1 = normal, 2 = a split) so you can tune Split Hand Step / the gap.")]
        [Range(1, 2)] [SerializeField] private int previewHands = 1;

        [Tooltip("Auto-rebuild the preview every N seconds in the editor, so ANY change (card prefab art, skin, " +
                 "labels) shows up without hitting Refresh. 0 = off (manual Refresh / live tweaks only).")]
        [SerializeField] private float autoRefreshSeconds = 2f;

        private const string CardName = "__cardpreview";
        private const string BadgeName = "__badgepreview";

        private struct BadgeRef { public Transform T; public Component SrcComp; public IAnchorLabel Src; public int Hand; }

        private readonly List<Transform> _cards = new List<Transform>();
        private readonly List<BadgeRef> _badges = new List<BadgeRef>();
        private readonly List<IAnchorLabel> _sources = new List<IAnchorLabel>();
        private BlackjackTableView _view;
        private CardVisual _builtPrefab;
        private CardSkin _builtSkin;
        private bool _dirty;

        private void OnEnable()
        {
            ClearPreview();                              // kill any leftovers — incl. when ENTERING Play (fixes "extra cards")
            if (!Application.isPlaying) Rebuild();
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEditor.EditorApplication.update -= EditorTick;   // avoid double-subscribe
                UnityEditor.EditorApplication.update += EditorTick;
            }
#endif
        }

        private void OnDisable()
        {
            ClearPreview();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorTick;
#endif
        }

        private void OnValidate() => _dirty = true;       // inspector changed — rebuild next tick (instantiate is unsafe in OnValidate)

        [ContextMenu("Refresh Preview")]
        private void ForceRefresh() => _dirty = true;

        private void Update()
        {
            if (Application.isPlaying) { ClearPreview(); return; }   // the live table owns the cards at runtime
            EditorRefresh();
        }

        // Rebuild when our card count changed or the view swapped its prefab/skin; else re-Layout. The periodic
        // EditorTick forces a full rebuild on a timer so label add/remove + prefab edits are picked up regardless.
        private void EditorRefresh()
        {
            if (Application.isPlaying) return;
            var view = View;
            bool sourceChanged = view != null && (view.PreviewPrefab != _builtPrefab || view.PreviewSkin != _builtSkin);
            if (_dirty || sourceChanged || _cards.Count != previewCount * previewHands) Rebuild();
            else Layout();
        }

#if UNITY_EDITOR
        private double _lastAuto;

        // Driven by EditorApplication.update — fires reliably in edit mode even when idle (ExecuteAlways Update can't).
        private void EditorTick()
        {
            if (Application.isPlaying) return;
            if (this == null) { UnityEditor.EditorApplication.update -= EditorTick; return; }   // destroyed — detach
            if (autoRefreshSeconds > 0f && UnityEditor.EditorApplication.timeSinceStartup - _lastAuto >= autoRefreshSeconds)
            {
                _lastAuto = UnityEditor.EditorApplication.timeSinceStartup;
                _dirty = true;
            }
            EditorRefresh();
        }
#endif

        // Anchors don't have to be children of the view — find it in the scene, cached.
        private BlackjackTableView View
        {
            get
            {
                if (_view == null) _view = GetComponentInParent<BlackjackTableView>() ?? Object.FindFirstObjectByType<BlackjackTableView>();
                return _view;
            }
        }

        private void Rebuild()
        {
            _dirty = false;
            ClearPreview();
            var view = View;
            if (view == null) return;
            _builtPrefab = view.PreviewPrefab;
            _builtSkin = view.PreviewSkin;

            int total = previewCount * previewHands;
            for (int i = 0; i < total; i++)
            {
                var card = view.InstantiatePreviewCard(transform, i);
                if (card == null) { ClearPreview(); return; }           // no card prefab assigned on the view
                card.gameObject.name = CardName;
                card.gameObject.hideFlags = HideFlags.HideAndDontSave;
                _cards.Add(card.transform);
            }

            // One sample of every anchor label (value badge, blackjack banner, …) per preview hand.
            CollectSources();
            foreach (var src in _sources)
            {
                if (src == null || src.LabelPrefab == null) continue;
                for (int h = 0; h < previewHands; h++)
                {
                    var go = Instantiate(src.LabelPrefab, transform);
                    go.name = BadgeName;
                    go.hideFlags = HideFlags.HideAndDontSave;
                    _badges.Add(new BadgeRef { T = go.transform, SrcComp = src as Component, Src = src, Hand = h });
                }
            }

            Layout();
        }

        private void CollectSources()
        {
            _sources.Clear();
            var v = Object.FindFirstObjectByType<HandValueLabels>();
            if (v != null) _sources.Add(v);
            var bj = Object.FindFirstObjectByType<HandBlackjackLabels>();
            if (bj != null) _sources.Add(bj);
        }

        private void Layout()
        {
            var view = View;
            if (view == null) return;
            int perHand = Mathf.Max(1, previewCount);
            Vector3 baseScale = view.PreviewPrefab != null ? view.PreviewPrefab.transform.localScale : Vector3.one;

            for (int i = 0; i < _cards.Count; i++)
            {
                if (_cards[i] == null) { _dirty = true; return; }       // cleaned up under us — rebuild next tick
                int handIndex = i / perHand;
                int cardIndex = i % perHand;
                view.CardLocalTRS(seatNumber, handIndex, previewHands, cardIndex, perHand, out var pos, out var rot, out var scale);
                _cards[i].localPosition = pos;
                _cards[i].localRotation = rot;
                _cards[i].localScale = baseScale * scale;
            }

            // Each label badge → its hand's LAST card corner, using that label's own (live-read) offset.
            int last = perHand - 1;
            for (int b = 0; b < _badges.Count; b++)
            {
                var br = _badges[b];
                if (br.T == null || br.SrcComp == null) { _dirty = true; return; }   // badge or its source went away

                Vector3 wp;
                Quaternion wr;
                if (br.Src.AnchorAtHandCenter)
                {
                    // Per-hand banner: hand centre, hand-aligned (independent of card count) — matches runtime.
                    wp = transform.TransformPoint(view.HandCenterLocal(br.Hand, previewHands)) + (transform.rotation * br.Src.CornerOffset);
                    wr = transform.rotation;
                }
                else
                {
                    // Per-card label: the hand's last card, tilted with it.
                    view.CardLocalTRS(seatNumber, br.Hand, previewHands, last, perHand, out var pos, out var rot, out var scale);
                    Quaternion cardWorldRot = transform.rotation * rot;
                    wp = transform.TransformPoint(pos) + cardWorldRot * (br.Src.CornerOffset * (br.Src.ScaleOffsetWithCard ? scale : 1f));
                    wr = cardWorldRot;
                }
                br.T.SetPositionAndRotation(wp, wr * Quaternion.Euler(br.Src.LabelFlatEuler));
            }
        }

        // Destroy preview cards + badges by name-marker only — safe in edit mode AND at runtime (real cards aren't named this).
        private void ClearPreview()
        {
            _cards.Clear();
            _badges.Clear();
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var ch = transform.GetChild(i);
                if (ch == null) continue;
                if (!ch.name.StartsWith(CardName) && !ch.name.StartsWith(BadgeName)) continue;
                if (Application.isPlaying) Destroy(ch.gameObject);
                else DestroyImmediate(ch.gameObject);
            }
        }
    }
}
