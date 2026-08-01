using System.Collections.Generic;
using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Pops a per-hand banner on a NATURAL blackjack (unsplit 2-card 21, pays 3:2), a BUST (> 21), or — at settle — a
    /// WIN / PUSH / LOSE, showing the matching child (<c>Label_BJ</c> / <c>Label_Bust</c> / <c>Label_Win</c> /
    /// <c>Label_Push</c> / <c>Label_Lose</c>). Win/push/lose are decided PER HAND (from the server's per-hand outcome),
    /// so a split whose hands split the result labels each hand correctly instead of the seat's misleading net.
    ///
    /// The DEALER shows BJ / BUST only — never WIN / LOSE / PUSH. Those are a PER-PLAYER result (she can beat one seat
    /// and lose to another in the same round), so a single banner on her hand could not be true. The settlement banners
    /// (win/push/lose, players only) are HELD
    /// by the <see cref="RoundEndDirector"/> until its PAY beat (<see cref="RevealNow"/>) — after reveal → draws →
    /// collect → pay — so they never announce the outcome before the dealer's cards are shown and the chips have moved
    /// (BJ / BUST are hand-intrinsic and still pop live). With no director wired they show at the settle push.
    /// Pins to the hand's LAST card via
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
        [Tooltip("Child shown when this hand WINS at settle (beats the dealer; not a blackjack — BJ takes precedence).")]
        [SerializeField] private string winChildName = "Label_Win";
        [Tooltip("Child shown on a PUSH at settle (tie with the dealer).")]
        [SerializeField] private string pushChildName = "Label_Push";
        [Tooltip("Child shown when this hand LOSES at settle (a non-bust loss; a bust shows Label_Bust instead). Leave " +
                 "EMPTY to show no banner on a plain loss (the pre-Label_Lose behaviour).")]
        [SerializeField] private string loseChildName = "Label_Lose";

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

        private enum Variant { None, BJ, Bust, Win, Push, Lose }

        private struct Badge { public GameObject Go; public float ShownAt; public GameObject BjChild; public GameObject BustChild; public GameObject WinChild; public GameObject PushChild; public GameObject LoseChild; }

        private readonly Dictionary<int, Badge> _active = new Dictionary<int, Badge>();
        private readonly Stack<GameObject> _free = new Stack<GameObject>();
        private readonly HashSet<int> _desired = new HashSet<int>();
        private readonly List<int> _stale = new List<int>();

        private Vector3 _baseScale = Vector3.one;       // prefab root scale (kept full; the root is NOT tweened)
        private Vector3 _bjBaseScale = Vector3.one;     // Label_BJ child's full scale — the unroll target
        private Vector3 _bustBaseScale = Vector3.one;   // Label_Bust child's full scale
        private Vector3 _winBaseScale = Vector3.one;    // Label_Win child's full scale
        private Vector3 _pushBaseScale = Vector3.one;   // Label_Push child's full scale
        private Vector3 _loseBaseScale = Vector3.one;   // Label_Lose child's full scale
        private bool _hasLoseChild;                      // the prefab actually CONTAINS a Label_Lose child (else: no lose banner)
        private bool _capturedScale;
        private BoardSnapshot _board;

        // Round-end HOLD (same mechanism as SeatPlates / WinChipFly): the SETTLEMENT banners
        // (Win / Push / Lose, on player hands only) announce the outcome, so with a RoundEndDirector
        // presenting they must wait for its PAY beat — AFTER reveal → draws → collect → pay — not flash on the raw
        // settle push. BJ / Bust are hand-intrinsic and still pop live. Held as a MonoBehaviour so there's no hard type
        // dependency on the director. Null (no director wired) = shows at settle, as before.
        private MonoBehaviour _settleDirector;
        private bool _settleRevealed;

        /// <summary>The director arms deferral (called at its OnEnable): the settlement banners wait for the PAY beat.</summary>
        public void RegisterSettleDirector(MonoBehaviour director) => _settleDirector = director;
        public void UnregisterSettleDirector(MonoBehaviour director) { if (_settleDirector == director) _settleDirector = null; }

        /// <summary>Director's PAY beat: release the settlement banners (win/push/lose) now.</summary>
        public void RevealNow(BoardSnapshot board)
        {
            _settleRevealed = true;
            if (board != null) _board = board;
            if (_board != null) Relayout(_board);
        }

        // IAnchorLabel — lets the editor CardAnchorGizmo preview this banner before Play.
        public GameObject LabelPrefab => labelPrefab;
        public Vector3 CornerOffset => cornerOffset;
        public Vector3 LabelFlatEuler => labelFlatEuler;
        public bool ScaleOffsetWithCard => true;    // offset scales with the (split-shrunk) card — same as the value badge
        public bool AnchorAtHandCenter => false;    // pinned to the hand's LAST card, like the value badge

        private void OnEnable()
        {
            CaptureScale();
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
            // Re-arm the settle hold while a round is live, so the NEXT settle waits for the director's PAY beat again
            // (mirrors SeatPlates / WinChipFly). Without this a second round would reveal its outcome at the raw settle push.
            if (board != null && board.RoundInProgress) _settleRevealed = false;
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

            // Follow the CARDS, not RoundInProgress: each banner is gated in Place on its hand's LAST card being
            // rendered + landed (view.CardSettled), so it pops WITH the card and persists through the round-end window
            // (the server leaves settled hands on the board; the felt still shows them during the deferred sweep), then
            // clears when the cards are collected. Gating on RoundInProgress hid the BUST banner entirely — the server
            // flips RoundInProgress false the instant the player busts, but the bust card only lands ~1s later.
            // Hold the DEALER's banner until her hole card is actually FLIPPED. The settle board carries her full total
            // the instant the round resolves, so popping "BLACKJACK" here would announce the face-down card before it's
            // turned. Not adding it to _desired keeps it hidden; the reveal beat releases it.
            if (includeDealer && board.Dealer != null && !(view.RoundEndHeld && !view.DealerHoleRevealed))
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

            _stale.Clear();
            foreach (var kv in _active) if (!_desired.Contains(kv.Key)) _stale.Add(kv.Key);
            for (int i = 0; i < _stale.Count; i++) ReleaseKey(_stale[i]);
        }

        // Pops on a natural blackjack (unsplit 2-card 21), a bust (> 21), or — at settle — a WIN / PUSH, showing the
        // matching child.
        private void Place(int seat, int handIndex, int handCount, List<CardView> cards, int value)
        {
            if (cards == null) return;

            // Which variant, if any? BJ / Bust read straight from the hand as the cards land (visible from the cards
            // alone, so they pop the instant the card lands — before settle). WIN / PUSH / LOSE are SETTLEMENT outcomes
            // (they need the dealer comparison), so they only appear once the round has settled and LastResults is
            // populated — and they're read PER HAND, so a split labels each hand on its own. BJ and Bust take
            // precedence — a natural stays "BJ", a bust stays "Bust", never win/push/lose.
            Variant variant;
            if (cards.Count == 2 && value == 21 && handCount == 1) variant = Variant.BJ;
            else if (value > 21) variant = Variant.Bust;
            // The DEALER never gets a settlement label. WIN / LOSE / PUSH are a PER-PLAYER result — at a multi-seat
            // table she can beat one seat and lose to another, so a single banner on her hand can't be true. She only
            // ever shows what is readable from her own cards: BJ / BUST (both handled above).
            else if (seat == 0) variant = Variant.None;
            else
            {
                string outcome = OutcomeFor(seat, handIndex);   // per hand at settle; null mid-round (dealer handled above)
                switch (outcome)
                {
                    case "win":
                    case "blackjack": variant = Variant.Win; break;   // "blackjack" defensive — a natural is caught above
                    case "push":      variant = Variant.Push; break;
                    // A non-bust loss shows Label_Lose only when the prefab actually HAS that child (so the default
                    // name can't render a blank banner before the artist adds it); "bust" is defensive (caught above).
                    case "lose":
                    case "bust":      variant = _hasLoseChild ? Variant.Lose : Variant.None; break;
                    default:          variant = Variant.None; break;
                }
            }
            if (variant == Variant.None) return;                             // plain mid-round hand → no banner

            // Settlement outcomes (Win/Push/Lose — player hands only) announce the result, so with a
            // RoundEndDirector presenting, hold them until it releases them (RevealNow) — after reveal → draws →
            // collect → pay — so nothing is announced before the dealer's cards are shown and the chips have moved. Same
            // hold SeatPlates / WinChipFly use. No director wired ⇒ _settleDirector null ⇒ show at settle (fallback).
            // BJ / Bust are hand-intrinsic (readable from the cards alone) and still pop live.
            bool isSettlement = variant == Variant.Win || variant == Variant.Push || variant == Variant.Lose;
            if (isSettlement && _settleDirector != null && !_settleRevealed) return;

            var anchor = view.SeatAnchor(seat);
            if (anchor == null) return;

            // Gate on the ANIMATION: hold the banner until the hand's LAST card has LANDED, so it pops WITH the card,
            // not the instant the server pushes the resolved hand. LateUpdate re-checks every frame.
            if (!view.CardSettled(seat, handIndex, cards.Count - 1)) return;

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

            // Show the variant for this situation, hide the rest.
            SetChild(b.BjChild,   variant == Variant.BJ);
            SetChild(b.BustChild, variant == Variant.Bust);
            SetChild(b.WinChild,  variant == Variant.Win);
            SetChild(b.PushChild, variant == Variant.Push);
            SetChild(b.LoseChild, variant == Variant.Lose);

            // Unroll the ACTIVE variant on its X axis (around its own pivot) — opens in place, no slide.
            GameObject child; Vector3 childBase;
            switch (variant)
            {
                case Variant.BJ:   child = b.BjChild;   childBase = _bjBaseScale;   break;
                case Variant.Bust: child = b.BustChild; childBase = _bustBaseScale; break;
                case Variant.Win:  child = b.WinChild;  childBase = _winBaseScale;  break;
                case Variant.Push: child = b.PushChild; childBase = _pushBaseScale; break;
                default:           child = b.LoseChild; childBase = _loseBaseScale; break; // Lose
            }
            float t = tweenDuration > 0f ? Mathf.Clamp01((Time.unscaledTime - b.ShownAt) / tweenDuration) : 1f;
            float e = EaseOutBack(t);
            if (child != null) child.transform.localScale = new Vector3(childBase.x * e, childBase.y, childBase.z);
        }

        private static void SetChild(GameObject c, bool on)
        {
            if (c != null && c.activeSelf != on) c.SetActive(on);
        }

        // This HAND's settled outcome ("blackjack"|"win"|"push"|"bust"|"lose") from the seat's per-hand list in
        // LastResults, or null (mid-round, or the dealer — seat 0 has no result entry). On a split each hand reads its
        // own outcome, so a mixed win/loss labels each hand correctly (the seat-level net would call it a "push").
        // Falls back to the seat-level net Outcome when an older server sends no per-hand list.
        private string OutcomeFor(int seat, int handIndex)
        {
            var results = _board?.LastResults;
            if (results == null) return null;
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                if (r == null || r.SeatNumber != seat) continue;
                var per = r.Hands;
                if (per != null)
                    for (int h = 0; h < per.Count; h++)
                        if (per[h] != null && per[h].HandIndex == handIndex) return per[h].Outcome;
                return r.Outcome;   // fallback: seat-level net (older server, or missing per-hand data)
            }
            return null;
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
                WinChild = FindChild(go, winChildName),
                PushChild = FindChild(go, pushChildName),
                LoseChild = FindChild(go, loseChildName),
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
            var win = FindChild(labelPrefab, winChildName);
            if (win != null) _winBaseScale = win.transform.localScale;
            var push = FindChild(labelPrefab, pushChildName);
            if (push != null) _pushBaseScale = push.transform.localScale;
            var lose = FindChild(labelPrefab, loseChildName);
            if (lose != null) { _loseBaseScale = lose.transform.localScale; _hasLoseChild = true; }
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
