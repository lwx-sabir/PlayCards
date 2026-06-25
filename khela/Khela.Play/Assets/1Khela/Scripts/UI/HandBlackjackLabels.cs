using System.Collections.Generic;
using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Pops a per-hand banner on a NATURAL blackjack (unsplit 2-card 21, pays 3:2) OR a BUST (> 21), showing the
    /// matching child (<c>Label_BJ</c> / <c>Label_Bust</c>). Pins to the hand's LAST card via
    /// <see cref="BlackjackTableView.CardLocalTRS"/> with its OWN offset, positioned IDENTICALLY to the value badge
    /// (offset scaled by the card). World-space, pooled, diffed; the active variant UNROLLS open on its X axis. Shows
    /// only while the round is live; clears otherwise. The banner ART is your prefab. Put this on an always-active
    /// object. NOTE: it sits on the LAST card, so the spot shifts with card count — preview it with the gizmo's
    /// Preview Count set to the real card count (2 for a blackjack).
    /// </summary>
    public sealed class HandBlackjackLabels : MonoBehaviour, IAnchorLabel
    {
        [SerializeField] private TableController table;
        [SerializeField] private BlackjackTableView view;
        [Tooltip("The BLACKJACK banner prefab — a self-contained WORLD-SPACE object. Pooled per blackjack hand.")]
        [SerializeField] private GameObject labelPrefab;
        [Tooltip("Also pop the banner for a DEALER blackjack / bust.")]
        [SerializeField] private bool includeDealer = true;

        [Header("Variant children (one is shown per situation; the rest of the badge is shared)")]
        [Tooltip("Child object shown on a natural blackjack.")]
        [SerializeField] private string bjChildName = "Label_BJ";
        [Tooltip("Child object shown on a bust (value > 21).")]
        [SerializeField] private string bustChildName = "Label_Bust";

        [Header("Placement (independent of the value label — place it where you want, e.g. on top)")]
        [Tooltip("Offset from the LAST card's CENTRE in the CARD's local frame: X = card's right, Y = lift off the " +
                 "felt, Z = toward the dealer (−Z = toward the player). Scales with the card, exactly like the value " +
                 "badge. Tune in the gizmo with Preview Count = the real card count (2 for a BJ).")]
        [SerializeField] private Vector3 cornerOffset = new Vector3(0f, 0.02f, 0.06f);
        [Tooltip("Extra rotation to lay the banner flat like the cards (a world-space UI banner stands vertical → try (90,0,0)).")]
        [SerializeField] private Vector3 labelFlatEuler = new Vector3(90f, 0f, 0f);

        [Header("Unroll tween — the active variant scales open on its X axis (set its pivot for the roll origin)")]
        [Tooltip("Seconds for the unroll.")]
        [SerializeField] private float tweenDuration = 0.35f;
        [Tooltip("Overshoot of the ease-out-back unroll — 0 = clean, ~1.7 = stretches past full then settles.")]
        [SerializeField] private float overshoot = 1.7f;

        private struct Badge { public GameObject Go; public float ShownAt; public GameObject BjChild; public GameObject BustChild; }

        private readonly Dictionary<int, Badge> _active = new Dictionary<int, Badge>();
        private readonly Stack<GameObject> _free = new Stack<GameObject>();
        private readonly HashSet<int> _desired = new HashSet<int>();
        private readonly List<int> _stale = new List<int>();

        private Vector3 _baseScale = Vector3.one;       // prefab root scale (kept full; the root is NOT tweened)
        private Vector3 _bjBaseScale = Vector3.one;     // Label_BJ child's full scale — the unroll target
        private Vector3 _bustBaseScale = Vector3.one;   // Label_Bust child's full scale
        private bool _capturedScale;
        private BoardSnapshot _board;

        // IAnchorLabel — lets the editor CardAnchorGizmo preview this banner before Play.
        public GameObject LabelPrefab => labelPrefab;
        public Vector3 CornerOffset => cornerOffset;
        public Vector3 LabelFlatEuler => labelFlatEuler;
        public bool ScaleOffsetWithCard => true;    // offset scales with the (split-shrunk) card — same as the value badge
        public bool AnchorAtHandCenter => false;    // pinned to the hand's LAST card, like the value badge

        private void OnEnable()
        {
            CaptureScale();
            if (table == null) return;
            table.OnBoardChanged += OnBoard;
            if (table.Board != null) OnBoard(table.Board);
        }

        private void OnDisable()
        {
            if (table != null) table.OnBoardChanged -= OnBoard;
        }

        private void OnBoard(BoardSnapshot board)
        {
            _board = board;
            Relayout(_board);
        }

        // Re-place + drive the pop tween every frame.
        private void LateUpdate()
        {
            if (_board != null) Relayout(_board);
        }

        private void Relayout(BoardSnapshot board)
        {
            if (board == null || view == null || labelPrefab == null) return;
            _desired.Clear();

            if (board.RoundInProgress)
            {
                if (includeDealer && board.Dealer != null)
                    Place(0, 0, 1, board.Dealer.Cards, board.Dealer.HandValue);

                if (board.Seats != null)
                {
                    foreach (var seat in board.Seats)
                    {
                        var hands = seat?.Player?.Hands;
                        if (hands == null) continue;
                        for (int h = 0; h < hands.Count; h++)
                            Place(seat.SeatNumber, h, hands.Count, hands[h].Cards, hands[h].HandValue);
                    }
                }
            }

            _stale.Clear();
            foreach (var kv in _active) if (!_desired.Contains(kv.Key)) _stale.Add(kv.Key);
            for (int i = 0; i < _stale.Count; i++) ReleaseKey(_stale[i]);
        }

        // Pops on a natural blackjack (unsplit 2-card 21) OR a bust (> 21), showing the matching child.
        private void Place(int seat, int handIndex, int handCount, List<CardView> cards, int value)
        {
            if (cards == null) return;
            bool isBJ = cards.Count == 2 && value == 21 && handCount == 1;
            bool isBust = value > 21;
            if (!isBJ && !isBust) return;                                    // banner only on BJ or bust
            var anchor = view.SeatAnchor(seat);
            if (anchor == null) return;

            int key = SlotKey(seat, handIndex);
            _desired.Add(key);

            // Pin to the hand's LAST card, tilted with it, offset scaled by the card — IDENTICAL to the value badge
            // (single-hand correct). On a split the *scale shrinks the offset 10% with the card.
            view.CardLocalTRS(seat, handIndex, handCount, cards.Count - 1, cards.Count, out var pos, out var rot, out var scale);
            Vector3 worldPos = anchor.TransformPoint(pos);
            Quaternion worldRot = anchor.rotation * rot;

            var b = Rent(key);
            if (b.Go == null) return;

            // Root stays FULL scale + positioned, so the badge sits exactly where you set it (matches the gizmo).
            b.Go.transform.SetPositionAndRotation(
                worldPos + worldRot * (cornerOffset * scale),
                worldRot * Quaternion.Euler(labelFlatEuler));
            b.Go.transform.localScale = _baseScale;

            // Show the variant for this situation, hide the other.
            if (b.BjChild != null && b.BjChild.activeSelf != isBJ) b.BjChild.SetActive(isBJ);
            if (b.BustChild != null && b.BustChild.activeSelf != isBust) b.BustChild.SetActive(isBust);

            // Unroll the ACTIVE variant on its X axis (around its own pivot) — opens in place, no slide.
            float t = tweenDuration > 0f ? Mathf.Clamp01((Time.unscaledTime - b.ShownAt) / tweenDuration) : 1f;
            float e = EaseOutBack(t);
            var child = isBJ ? b.BjChild : b.BustChild;
            var childBase = isBJ ? _bjBaseScale : _bustBaseScale;
            if (child != null) child.transform.localScale = new Vector3(childBase.x * e, childBase.y, childBase.z);
        }

        // Ease-out-back: rises, overshoots past 1, settles to 1 → a lively pop. overshoot 0 → plain ease-out.
        private float EaseOutBack(float t)
        {
            float c1 = overshoot;
            float c3 = c1 + 1f;
            float p = t - 1f;
            return 1f + c3 * (p * p * p) + c1 * (p * p);
        }

        private Badge Rent(int key)
        {
            if (_active.TryGetValue(key, out var b) && b.Go != null) return b;   // same hand → keep ShownAt, tween continues
            var go = _free.Count > 0 ? _free.Pop() : Instantiate(labelPrefab);
            if (go != null) go.SetActive(true);   // root stays full scale; the active CHILD unrolls (set below)
            b = new Badge
            {
                Go = go,
                ShownAt = Time.unscaledTime,
                BjChild = FindChild(go, bjChildName),
                BustChild = FindChild(go, bustChildName),
            };
            _active[key] = b;
            return b;
        }

        private void ReleaseKey(int key)
        {
            if (!_active.TryGetValue(key, out var b)) return;
            _active.Remove(key);
            if (b.Go != null) { b.Go.SetActive(false); _free.Push(b.Go); }
        }

        private void CaptureScale()
        {
            if (_capturedScale || labelPrefab == null) return;
            _baseScale = labelPrefab.transform.localScale;
            var bj = FindChild(labelPrefab, bjChildName);
            if (bj != null) _bjBaseScale = bj.transform.localScale;
            var bust = FindChild(labelPrefab, bustChildName);
            if (bust != null) _bustBaseScale = bust.transform.localScale;
            _capturedScale = true;
        }

        // Find a descendant by name (incl. inactive) — the per-situation variant inside the badge prefab.
        private static GameObject FindChild(GameObject root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name)) return null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t.gameObject;
            return null;
        }

        private static int SlotKey(int seat, int handIndex) => seat * 100 + handIndex;   // dealer = 0
    }
}
