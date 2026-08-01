using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using PlayCard.Game.Dtos;
using PlayCard.Game.Net;
using PlayCard.Game.Table;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.UI
{
    /// <summary>
    /// The session HAND LOG / report panel: every hand THIS player has finished at THIS table during this sitting,
    /// newest first, with session totals. Opened by a log/report button, closed by its own close button.
    ///
    /// Server-authoritative: the list is fetched from <c>GET /api/Blackjack/{tableId}/history</c>, which reads the
    /// per-hand AUDIT rows the wallet ledger was written from. It is therefore correct after a reconnect or scene
    /// reload — a client-side tally would silently lose everything played before the blip — and it can never claim a
    /// payout the server didn't make. A SPLIT round contributes one row per hand, tagged "Hand 1" / "Hand 2".
    ///
    /// This is display-only and refetches each time it opens, so it always shows the round that just settled.
    ///
    /// IMPORTANT (the recurring disabled-watcher trap): put this component on an ALWAYS-ACTIVE object (e.g. TableHUD)
    /// and assign <see cref="panel"/> = the popup visual, which this activates/deactivates. A component sitting on the
    /// object it hides gets no Update/callbacks once hidden, so it could never re-open itself.
    /// </summary>
    public sealed class HandLogView : MonoBehaviour
    {
        [Header("Source")]
        [Tooltip("Auto-found if unassigned — supplies the table id and the sitting's start time.")]
        [SerializeField] private TableController table;

        [Header("Panel")]
        [Tooltip("The log panel VISUAL, on a SEPARATE object from this controller. May be disabled by default.")]
        [SerializeField] private GameObject panel;
        [Tooltip("Opens the log (your log / report button).")]
        [SerializeField] private Button openButton;
        [Tooltip("Closes the log.")]
        [SerializeField] private Button closeButton;
        [Tooltip("Optional: also close when this full-screen background is clicked.")]
        [SerializeField] private Button backdropButton;

        [Header("List")]
        [Tooltip("The row PREFAB — must have a HandLogRow component. One instance per settled hand.")]
        [SerializeField] private HandLogRow rowPrefab;
        [Tooltip("Parent the rows are spawned under — the Content object of your Scroll View.")]
        [SerializeField] private Transform rowParent;
        [Tooltip("Optional: shown while the request is in flight.")]
        [SerializeField] private GameObject loadingIndicator;
        [Tooltip("Optional: shown when the sitting has no settled hands yet (e.g. \"No hands played yet\").")]
        [SerializeField] private GameObject emptyState;
        [Tooltip("Optional: shown when the request fails, so a network error isn't a silently empty list.")]
        [SerializeField] private GameObject errorState;
        [Tooltip("Optional scroll rect — snapped back to the top on each open.")]
        [SerializeField] private ScrollRect scrollRect;

        [Header("Summary (all optional)")]
        [SerializeField] private TMP_Text handsPlayedLabel;
        [SerializeField] private TMP_Text wageredLabel;
        [SerializeField] private TMP_Text returnedLabel;
        [Tooltip("Signed session net, tinted win/lose/push.")]
        [SerializeField] private TMP_Text netLabel;
        [Tooltip("Optional \"12W / 8L / 2P\" style record.")]
        [SerializeField] private TMP_Text recordLabel;
        [Tooltip("Optional: shown only when the server capped the list (older hands exist beyond it).")]
        [SerializeField] private GameObject truncatedNotice;

        [Header("Drawer slide")]
        [Tooltip("Slide in from the RIGHT edge (default). Uncheck for a left-hand drawer.")]
        [SerializeField] private bool slideFromRight = true;
        [Tooltip("How far off-screen it parks, in anchored units. Leave 0 to use the panel's own WIDTH, which puts it " +
                 "exactly off its own edge however you resize it.")]
        [SerializeField] private float slideDistance = 0f;
        [SerializeField] private float openSeconds = 0.34f;
        [SerializeField] private float closeSeconds = 0.26f;
        [Tooltip("Juice on the way in — a small settle as the drawer arrives. 0 = a clean slide.")]
        [SerializeField] private float overshoot = 0.5f;

        [Header("Options")]
        [Tooltip("Max hands to request. The server clamps to 500.")]
        [SerializeField] private int take = 100;
        [Tooltip("ON (default): only hands from THIS sitting. OFF: every hand this table has for you, up to Take.")]
        [SerializeField] private bool limitToThisSitting = true;

        [Header("Formatting")]
        [SerializeField] private string amountFormat = "#,0";
        [SerializeField] private Color winColor = new Color(0.30f, 0.85f, 0.40f);
        [SerializeField] private Color loseColor = new Color(0.90f, 0.35f, 0.35f);
        [SerializeField] private Color pushColor = new Color(0.85f, 0.80f, 0.45f);

        private readonly List<HandLogRow> _rows = new List<HandLogRow>();
        private bool _loading;
        private RectTransform _panelRt;
        private Vector2 _shownPos;      // AUTHORED position = where the drawer sits when open
        private bool _open;
        private Coroutine _slide;

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (panel == gameObject)
                Debug.LogError($"[{nameof(HandLogView)}] is on the SAME GameObject as its 'panel'. Once the panel is " +
                    "hidden this component stops receiving callbacks, so the log could never re-open. Move it to an " +
                    "ALWAYS-ACTIVE object (e.g. TableHUD) and set 'panel' to the popup visual.", this);
        }
#endif

        private void Awake()
        {
            if (table == null) table = FindAnyObjectByType<TableController>();
            if (openButton != null) openButton.onClick.AddListener(Open);
            if (closeButton != null) closeButton.onClick.AddListener(Close);
            if (backdropButton != null) backdropButton.onClick.AddListener(Close);

            if (panel != null)
            {
                _panelRt = panel.GetComponent<RectTransform>();
                if (_panelRt != null) _shownPos = _panelRt.anchoredPosition;   // AUTHORED position = the open position
                if (panel != gameObject) panel.SetActive(false);               // hidden until asked for
            }
        }

        // Parked position, just off its own edge. Computed on demand rather than at Awake: the panel's width isn't
        // final until layout has run, and using the width means the drawer clears itself however you resize it.
        private Vector2 ClosedPos()
        {
            float dist = slideDistance > 0f
                ? slideDistance
                : Mathf.Max(1f, _panelRt != null ? _panelRt.rect.width : 0f);
            return _shownPos + (slideFromRight ? Vector2.right : Vector2.left) * dist;
        }

        private void OnDestroy()
        {
            if (openButton != null) openButton.onClick.RemoveListener(Open);
            if (closeButton != null) closeButton.onClick.RemoveListener(Close);
            if (backdropButton != null) backdropButton.onClick.RemoveListener(Close);
        }

        /// <summary>Open the drawer and (re)load it. Safe to wire straight to a Button.</summary>
        public void Open()
        {
            if (panel == null) return;
            if (!panel.activeSelf && panel != gameObject) panel.SetActive(true);

            // Re-tapping while it's already open must NOT snap it back off-screen to slide in again — just refresh.
            if (!_open && _panelRt != null)
            {
                _panelRt.anchoredPosition = ClosedPos();
                StartSlide(_shownPos, deactivateAtEnd: false, opening: true);
            }
            _open = true;
            _ = LoadAsync();
        }

        /// <summary>Close the drawer (slides back out and deactivates).</summary>
        public void Close()
        {
            if (panel == null) return;
            _open = false;
            if (_panelRt == null) { if (panel != gameObject) panel.SetActive(false); return; }
            StartSlide(ClosedPos(), deactivateAtEnd: panel != gameObject, opening: false);
        }

        /// <summary>Toggle — handy if you'd rather the same button open and close it.</summary>
        public void Toggle()
        {
            if (_open) Close();
            else Open();
        }

        private void StartSlide(Vector2 target, bool deactivateAtEnd, bool opening)
        {
            if (_slide != null) StopCoroutine(_slide);
            _slide = StartCoroutine(SlideRoutine(target, deactivateAtEnd, opening));
        }

        private IEnumerator SlideRoutine(Vector2 target, bool deactivateAtEnd, bool opening)
        {
            Vector2 start = _panelRt.anchoredPosition;
            float duration = opening ? openSeconds : closeSeconds;
            float t = 0f;
            while (t < duration && duration > 0f)
            {
                t += Time.unscaledDeltaTime;
                float raw = Mathf.Clamp01(t / duration);
                // Settle in on the way OPEN, wind up and shoot out on the way CLOSED — the drawer feel. LerpUnclamped
                // so it can briefly pass the endpoint for the overshoot.
                float k = opening ? UITween.EaseOutBack(raw, overshoot) : UITween.EaseInBack(raw, overshoot);
                _panelRt.anchoredPosition = Vector2.LerpUnclamped(start, target, k);
                yield return null;
            }
            _panelRt.anchoredPosition = target;
            _slide = null;
            if (deactivateAtEnd && panel != null) panel.SetActive(false);
        }

        private async Task LoadAsync()
        {
            if (_loading) return;                     // a double-tap must not spawn two lists
            if (table == null || string.IsNullOrEmpty(table.TableId)) { ShowState(error: true); return; }

            _loading = true;
            ShowState(loading: true);
            ClearRows();

            var since = limitToThisSitting ? TableController.SessionStartedUtc : null;
            var res = await BlackjackRestClient.Instance.GetHandLogAsync(table.TableId, since, take);

            _loading = false;

            // The player may have closed the panel (or left the table) while the request was in flight.
            if (this == null || panel == null) return;

            if (!res.Ok || res.Value == null)
            {
                Debug.LogWarning($"[HandLogView] hand log failed ({res.Status}): {res.Error}");
                ShowState(error: true);
                return;
            }

            Populate(res.Value);
        }

        private void Populate(HandLogData data)
        {
            var hands = data.Hands ?? new List<HandLogEntry>();

            // A round that SPLIT shows two entries with the same round number. One entry can't tell on its own, so
            // mark them here — the row uses this to show its "Hand 1 / Hand 2" tag.
            MarkSplitParts(hands);

            // Renumber for THIS sitting (1, 2, 3 …) — the server's HandNumber is the table's all-time counter, so a
            // player who sat down at round 249 would read "#249, #250, #251" instead of "#1, #2, #3".
            NumberSession(hands);

            if (rowPrefab != null && rowParent != null)
            {
                for (int i = 0; i < hands.Count; i++)
                {
                    var row = Instantiate(rowPrefab, rowParent);
                    row.gameObject.SetActive(true);
                    row.Bind(hands[i]);
                    _rows.Add(row);
                }
            }

            if (handsPlayedLabel != null) handsPlayedLabel.text = hands.Count.ToString();
            if (wageredLabel != null) wageredLabel.text = data.Wagered.ToString(amountFormat);
            if (returnedLabel != null) returnedLabel.text = data.Returned.ToString(amountFormat);
            if (netLabel != null)
            {
                netLabel.text = data.Net > 0m ? "+" + data.Net.ToString(amountFormat) : data.Net.ToString(amountFormat);
                netLabel.color = data.Net > 0m ? winColor : data.Net < 0m ? loseColor : pushColor;
            }
            if (recordLabel != null) recordLabel.text = $"{data.Wins}W / {data.Losses}L / {data.Pushes}P";
            if (truncatedNotice != null) truncatedNotice.SetActive(data.Truncated);

            ShowState(empty: hands.Count == 0);
            if (scrollRect != null) scrollRect.verticalNormalizedPosition = 1f;   // newest first → start at the top
        }

        /// <summary>
        /// Number the rounds for this sitting: oldest = 1. Keyed on the server's HandNumber, NOT on row position, so a
        /// SPLIT's two entries share one number (two rows, one round) and the count still matches the "hands played"
        /// the player actually experienced.
        ///
        /// If the list was truncated by the server cap, the oldest row returned isn't truly the session's first round,
        /// so the numbering is relative to what's shown — which is why the view surfaces the Truncated notice.
        /// </summary>
        private static void NumberSession(List<HandLogEntry> hands)
        {
            var rounds = new SortedSet<int>();                       // ascending = oldest first
            for (int i = 0; i < hands.Count; i++)
                if (hands[i] != null) rounds.Add(hands[i].HandNumber);

            var ordinal = new Dictionary<int, int>();
            int n = 1;
            foreach (int handNumber in rounds) ordinal[handNumber] = n++;

            for (int i = 0; i < hands.Count; i++)
                if (hands[i] != null && ordinal.TryGetValue(hands[i].HandNumber, out int r))
                    hands[i].SessionRound = r;
        }

        // Flag every entry that shares its round number with another — i.e. the two halves of a split round.
        private static void MarkSplitParts(List<HandLogEntry> hands)
        {
            for (int i = 0; i < hands.Count; i++)
            {
                if (hands[i] == null) continue;
                if (hands[i].HandIndex > 0) { hands[i].IsSplitPart = true; continue; }   // hand 2+ is always a split part
                for (int j = 0; j < hands.Count; j++)
                {
                    if (i == j || hands[j] == null) continue;
                    if (hands[j].HandNumber == hands[i].HandNumber && hands[j].HandIndex > 0)
                    {
                        hands[i].IsSplitPart = true;   // hand 1 of a round that also has a hand 2
                        break;
                    }
                }
            }
        }

        private void ClearRows()
        {
            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i] != null) Destroy(_rows[i].gameObject);
            _rows.Clear();
        }

        // One place decides which of loading / empty / error is visible, so they can never be shown together.
        private void ShowState(bool loading = false, bool empty = false, bool error = false)
        {
            if (loadingIndicator != null) loadingIndicator.SetActive(loading);
            if (emptyState != null) emptyState.SetActive(empty);
            if (errorState != null) errorState.SetActive(error);
        }
    }
}
