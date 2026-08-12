using System.Collections;
using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Covers the Table scene while it builds, then hands the camera over.
    ///
    /// Entering a table is not instant even when the network is fast: the dealer avatar is assembled at runtime and
    /// takes 1-3s, the first board has to arrive, and the felt paints from it. Without a cover the player watches that
    /// happen — and the camera used to start its move the moment the board landed, which reads as a lurch before
    /// there is anything to look at. This holds the camera parked at the entry pose, waits for the scene to be
    /// genuinely ready, then dismisses and releases it so the first move is something the player is watching.
    ///
    /// Owns the COVER and the camera hold, nothing else — whatever the panel contains (text, spinner, art) is its own
    /// business and its own components.
    ///
    /// PUT THIS ON AN ALWAYS-ACTIVE OBJECT (the table HUD root), never on the panel it hides — a component on a
    /// disabled object gets no Update and could never dismiss itself. That trap has bitten several popups here.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TableLoadingScreen : MonoBehaviour
    {
        [Header("Refs (auto-found if empty)")]
        [SerializeField] private TableController table;
        [SerializeField] private TableCameraController cameraController;

        [Header("Panel")]
        [Tooltip("The cover itself — a SEPARATE object from this component. Shown from Awake, hidden when ready.")]
        [SerializeField] private GameObject panel;
        [Tooltip("Optional CanvasGroup on the panel — enables the fade-out. Without one it just switches off.")]
        [SerializeField] private CanvasGroup group;
        [SerializeField] private float fadeSeconds = 0.35f;

        [Header("Timing")]
        [Tooltip("Shortest the cover stays up, even if the board arrives instantly. This is what hides the dealer " +
                 "avatar's 1-3s build — set it to comfortably cover that on your SLOWEST target device.")]
        [SerializeField] private float minSeconds = 2.5f;
        [Tooltip("Safety cap. Dismiss even if the board never arrives, so a bad connection strands the player on a " +
                 "table they can see rather than on a cover they can't get past.")]
        [SerializeField] private float maxSeconds = 10f;
        [Tooltip("Extra settle time AFTER the first board paints, so the felt isn't still popping in as it lifts.")]
        [SerializeField] private float afterBoardSeconds = 0.35f;

        private bool _boardArrived;
        private float _boardAt;   // unscaled time the first board landed, so the settle is measured FROM it

        private void Awake()
        {
            if (table == null) table = FindAnyObjectByType<TableController>(FindObjectsInactive.Include);
            if (cameraController == null) cameraController = FindAnyObjectByType<TableCameraController>(FindObjectsInactive.Include);
            if (group == null && panel != null) group = panel.GetComponent<CanvasGroup>();

            // Cover FIRST and hold the camera in Awake — before any board can arrive or the camera can take a frame.
            // Doing this in Start or OnEnable is late enough to show one unheld frame on a fast join.
            Show();
            if (cameraController != null) cameraController.HoldAtEntry(true);
        }

        private void OnEnable()
        {
            if (table != null) table.OnBoardChanged += OnBoard;
            StartCoroutine(RunRoutine());
        }

        private void OnDisable()
        {
            if (table != null) table.OnBoardChanged -= OnBoard;
        }

        private void OnBoard(BoardSnapshot board)
        {
            if (_boardArrived) return;   // first one only — later pushes must not restart the settle
            _boardArrived = true;
            _boardAt = Time.unscaledTime;
        }

        private IEnumerator RunRoutine()
        {
            float started = Time.unscaledTime;

            // Ready = BOTH clocks satisfied: the cover has been up for minSeconds (which is what hides the dealer
            // build), and the board has been painted for afterBoardSeconds (measured FROM its arrival, so a board
            // that lands late still gets its full settle). Whichever finishes last decides. maxSeconds is the escape
            // hatch — never trap the player behind this.
            while (Time.unscaledTime - started < maxSeconds)
            {
                bool longEnough = Time.unscaledTime - started >= minSeconds;
                bool settled = _boardArrived && Time.unscaledTime - _boardAt >= afterBoardSeconds;
                if (longEnough && settled) break;

                yield return null;
            }

            yield return Dismiss();
        }

        private IEnumerator Dismiss()
        {
            // Release the camera BEFORE the fade so its first move happens under the lifting cover rather than after
            // it — the move is then something the player sees begin, not a jump that has already happened.
            if (cameraController != null) cameraController.HoldAtEntry(false);

            if (group != null && fadeSeconds > 0f)
            {
                float from = group.alpha;
                float t = 0f;
                while (t < fadeSeconds)
                {
                    t += Time.unscaledDeltaTime;
                    group.alpha = Mathf.Lerp(from, 0f, Mathf.Clamp01(t / fadeSeconds));
                    yield return null;
                }
                group.alpha = 0f;
            }

            Show(false);
        }

        private void Show(bool visible = true)
        {
            if (panel != null && panel.activeSelf != visible) panel.SetActive(visible);
            if (!visible) return;

            if (group != null)
            {
                group.alpha = 1f;
                group.blocksRaycasts = true;   // swallow taps on a table that isn't ready to be tapped
            }
        }
    }
}
