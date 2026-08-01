using System.Collections.Generic;
using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// Pins a small world-space VALUE badge to each hand's LAST card (bottom-right corner) showing the hand total —
    /// dealer + every seat, split hands included — and tints the badge's background Image by SITUATION (Normal /
    /// Stand / Busted / Blackjack). Driven by the live board, diffed (one pooled badge per hand). Placement comes
    /// from <see cref="BlackjackTableView.CardLocalTRS"/> so it tracks the last card + respects the split-shrink.
    /// Badges show only while a round is live. The badge ART is your prefab; "Normal" reuses the prefab's own bg
    /// colour (captured automatically), the rest are the colours below. Put on an always-active object; assign a
    /// self-contained world-space prefab with a TMP_Text and a background Image.
    /// </summary>
    public sealed class HandValueLabels : MonoBehaviour, IAnchorLabel
    {
        [SerializeField] private TableController table;
        [SerializeField] private BlackjackTableView view;
        [Tooltip("The value badge prefab — a self-contained WORLD-SPACE UI object with a TMP_Text + a background Image. Pooled per hand.")]
        [SerializeField] private GameObject labelPrefab;
        [Tooltip("Also show a badge on the dealer's hand (uses the dealer's visible total).")]
        [SerializeField] private bool includeDealer = true;

        [Header("Placement")]
        [Tooltip("Offset from the LAST card's CENTRE to its bottom-right CORNER, in the CARD's local frame: X = the " +
                 "card's right, Y = lift off the felt, Z = toward the dealer (−Z = bottom/player edge). Scales with split-shrunk cards.")]
        [SerializeField] private Vector3 cornerOffset = new Vector3(0.025f, 0.006f, -0.035f);
        [Tooltip("Extra rotation to lay the badge FLAT like the cards (a world-space UI badge stands vertical → try (90,0,0)).")]
        [SerializeField] private Vector3 labelFlatEuler = new Vector3(90f, 0f, 0f);

        [Header("Background colour by situation (Normal = the prefab's own colour)")]
        [Tooltip("Hand finished by standing / double / split-ace and is ≤ 21.")]
        [SerializeField] private Color standColor = new Color(0.35f, 0.55f, 0.90f);     // blue
        [Tooltip("Hand value is over 21.")]
        [SerializeField] private Color bustedColor = new Color(0.85f, 0.22f, 0.22f);    // red
        [Tooltip("A NATURAL blackjack — a 2-card 21 on an UNSPLIT hand (pays 3:2).")]
        [SerializeField] private Color blackjackColor = new Color(0.97f, 0.80f, 0.22f); // gold
        [Tooltip("A 21 that is NOT a natural — made with 3+ cards, or a split 21 (pays 1:1).")]
        [SerializeField] private Color twentyOneColor = new Color(0.30f, 0.80f, 0.35f); // green
        [Tooltip("Value TEXT colour on a natural blackjack (every other situation keeps the prefab's own text colour).")]
        [SerializeField] private Color blackjackTextColor = new Color(0.20f, 0.13f, 0.0f); // dark, for contrast on gold

        private struct Badge { public GameObject Go; public TMP_Text Text; public Image Bg; }

        private readonly Dictionary<int, Badge> _active = new Dictionary<int, Badge>();
        private readonly Stack<Badge> _free = new Stack<Badge>();
        private readonly HashSet<int> _desired = new HashSet<int>();
        private readonly List<int> _stale = new List<int>();

        private Color _normalColor = Color.white;        // bg colour for Normal — captured from the prefab default
        private Color _normalTextColor = Color.white;    // value-text colour for non-BJ — captured from the prefab default
        private bool _capturedNormal;
        private BoardSnapshot _board;

        // Editor-tooling accessors so the CardAnchorGizmo can show + position a sample badge before Play.
        public GameObject LabelPrefab => labelPrefab;
        public Vector3 CornerOffset => cornerOffset;
        public Vector3 LabelFlatEuler => labelFlatEuler;
        public bool ScaleOffsetWithCard => true;   // glued to the card corner — tracks the split-shrunk card
        public bool AnchorAtHandCenter => false;   // per-card: pinned to the hand's last card

        private void OnEnable()
        {
            CaptureNormalColor();
            if (view == null) view = FindAnyObjectByType<BlackjackTableView>();   // so the deal-landed gate can't be silently bypassed
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

        // Re-place every frame so inspector tuning (offsets / colours) updates LIVE in Play and badges stay pinned.
        private void LateUpdate()
        {
            if (_board != null) Relayout(_board);
        }

        private void Relayout(BoardSnapshot board)
        {
            if (board == null || view == null || labelPrefab == null) return;
            _desired.Clear();

            // Follow the CARDS, not RoundInProgress (each badge is gated in Place on its hand's last card being
            // rendered + landed via view.CardSettled): so a value/bust badge stays with the cards through the round-end
            // window and clears when they're collected. Gating on RoundInProgress dropped the badge the instant the
            // server resolved (e.g. a bust) while the card was still animating in. The view only renders cards while a
            // round is live (or during its deferred sweep), so nothing shows between rounds.
            // The dealer's badge stays up for the whole round and re-totals as each of her draws lands. The hole card is
            // masked until the reveal beat turns it on screen — the settle board marks it face up the moment the round
            // resolves, which would otherwise print the hidden value early.
            if (includeDealer && board.Dealer != null)
                Place(0, 0, 1, board.Dealer.Cards, false, maskHole: view.RoundEndHeld && !view.DealerHoleRevealed);

            if (board.Seats != null)
            {
                foreach (var seat in board.Seats)
                {
                    var hands = seat?.Player?.Hands;
                    if (hands == null) continue;
                    for (int h = 0; h < hands.Count; h++)
                        Place(seat.SeatNumber, h, hands.Count, hands[h].Cards, hands[h].Done, maskHole: false);
                }
            }

            _stale.Clear();
            foreach (var kv in _active) if (!_desired.Contains(kv.Key)) _stale.Add(kv.Key);
            for (int i = 0; i < _stale.Count; i++) ReleaseKey(_stale[i]);
        }

        private void Place(int seat, int handIndex, int handCount, List<CardView> cards, bool done, bool maskHole)
        {
            if (cards == null || cards.Count == 0) return;
            var anchor = view.SeatAnchor(seat);
            if (anchor == null) return;   // seat beyond our authored anchors — skip

            // Follow the last card that has actually LANDED — NOT the last card in the hand. Gating on the final card
            // meant that the moment a new one was dealt (every dealer draw, every hit) the badge failed the test, got
            // swept as stale, and vanished until that card touched down. The badge now stays up throughout and simply
            // re-totals as each card arrives, which is also what "reveals in step with the deal" was always meant to be.
            int last = LastSettledIndex(seat, handIndex, cards.Count);
            if (last < 0) return;   // nothing dealt yet — nothing to show

            int landed = last + 1;
            int value = VisibleTotal(cards, last, maskHole);

            int key = SlotKey(seat, handIndex);
            _desired.Add(key);
            view.CardLocalTRS(seat, handIndex, handCount, last, landed, out var pos, out var rot, out var scale);
            Vector3 cardWorldPos = anchor.TransformPoint(pos);
            Quaternion cardWorldRot = anchor.rotation * rot;

            var b = Rent(key);
            if (b.Go == null) return;
            b.Go.transform.SetPositionAndRotation(
                cardWorldPos + cardWorldRot * (cornerOffset * scale),   // *scale → tracks a shrunk split card's corner
                cardWorldRot * Quaternion.Euler(labelFlatEuler));
            bool isBlackjack = landed == 2 && value == 21 && handCount == 1;
            if (b.Text != null)
            {
                b.Text.text = value.ToString();
                b.Text.color = isBlackjack ? blackjackTextColor : _normalTextColor;
            }
            if (b.Bg != null) b.Bg.color = ColorFor(landed, value, handCount, done);
        }

        // Highest card index in this hand that has LANDED, or -1 if none has.
        private int LastSettledIndex(int seat, int handIndex, int cardCount)
        {
            for (int i = cardCount - 1; i >= 0; i--)
                if (view.CardSettled(seat, handIndex, i)) return i;
            return -1;
        }

        /// <summary>
        /// Blackjack total of the cards that have landed AND are face up (aces 11, demoted to 1 while the hand would
        /// bust) — the same rule the server's visible-sum uses, computed here so the badge always agrees with what is
        /// actually on the felt rather than with a total that includes a card still in the air.
        /// <paramref name="maskHole"/> additionally excludes the dealer's hole card: the settle board marks it face up
        /// the instant the round resolves, but it isn't turned on screen until the reveal beat, and printing its value
        /// early would leak the hidden card.
        /// </summary>
        private static int VisibleTotal(List<CardView> cards, int upTo, bool maskHole)
        {
            int total = 0, aces = 0;
            for (int i = 0; i <= upTo && i < cards.Count; i++)
            {
                var c = cards[i];
                if (c == null || !c.IsCardUp) continue;      // face-down contributes nothing (masked server-side too)
                if (maskHole && i == 1) continue;            // dealer hole: revealed in data, not yet on screen
                if (c.Value == 11) aces++;
                total += c.Value;
            }
            while (total > 21 && aces > 0) { total -= 10; aces--; }   // soft ace → hard
            return total;
        }

        private Color ColorFor(int cardCount, int value, int handCount, bool done)
        {
            if (value > 21) return bustedColor;                                   // busted
            if (value == 21)
                return (cardCount == 2 && handCount == 1) ? blackjackColor        // true natural (unsplit 2-card 21) → 3:2
                                                          : twentyOneColor;       // any other 21 (3+ cards or split) → 1:1
            if (done) return standColor;                                          // stood / doubled / split-ace, < 21
            return _normalColor;                                                  // still in play
        }

        private Badge Rent(int key)
        {
            if (_active.TryGetValue(key, out var b) && b.Go != null) return b;
            if (_free.Count > 0) b = _free.Pop();
            else
            {
                var go = Instantiate(labelPrefab);
                b = new Badge { Go = go, Text = go.GetComponentInChildren<TMP_Text>(true), Bg = go.GetComponentInChildren<Image>(true) };
            }
            if (b.Go != null) b.Go.SetActive(true);
            _active[key] = b;
            return b;
        }

        private void ReleaseKey(int key)
        {
            if (!_active.TryGetValue(key, out var b)) return;
            _active.Remove(key);
            if (b.Go != null) { b.Go.SetActive(false); _free.Push(b); }
        }

        // "Normal" colour = the prefab's authored bg colour (the default you set). Read once from the prefab asset.
        private void CaptureNormalColor()
        {
            if (_capturedNormal || labelPrefab == null) return;
            var bg = labelPrefab.GetComponentInChildren<Image>(true);
            if (bg != null) _normalColor = bg.color;
            var tmp = labelPrefab.GetComponentInChildren<TMP_Text>(true);
            if (tmp != null) _normalTextColor = tmp.color;
            _capturedNormal = true;
        }

        private static int SlotKey(int seat, int handIndex) => seat * 100 + handIndex;   // dealer = 0
    }
}
