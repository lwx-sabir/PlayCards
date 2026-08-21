using System;
using System.Collections;
using System.Threading.Tasks;
using DG.Tweening;
using Khela.Common.Piggy;
using UnityEngine;

namespace PlayCard.Piggy
{
    /// <summary>
    /// The glue on the piggy popup's root: opens and closes it, and turns the screen's chosen offer into a purchase.
    ///
    /// The purchase itself is NOT here. This raises <see cref="BreakRequested"/> with the option the player picked and
    /// waits; the IAP layer answers it. That seam matters because the store flow is the one part of this that cannot
    /// be tested from the editor — keeping it behind an event means everything up to and after it can be.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PiggyScreen))]
    public sealed class PiggyPanel : MonoBehaviour
    {
        [Tooltip("Leave empty to use the PiggyScreen on this object.")]
        [SerializeField] private PiggyScreen screen;

        [Header("TESTING - leave OFF in production")]
        [Tooltip("Turns on the piggy's test affordances as a set: the popup opens in the FULL state whatever the " +
                 "server says, and PiggyBlast's Preview Blast becomes available. OFF is ordinary production " +
                 "behaviour with no test hook anywhere - nothing below this reads it.")]
        [SerializeField] private bool testMode;

        /// <summary>
        /// Are the test affordances on? Read by <see cref="PiggyBlast"/>, which refuses to preview without it.
        ///
        /// One switch for all of them on purpose: a build that ships with some test behaviour still live is the
        /// failure this is guarding against, and that is far likelier with three flags than with one.
        /// </summary>
        public bool TestMode => testMode;

        [Tooltip("SEPARATE from Test Mode, and far more dangerous: the buy buttons stop asking the store and call " +
                 "the break endpoint directly, so the whole payoff can be driven end to end without IAP wired.\n\n" +
                 "It cannot give anything away on its own - the SERVER still refuses unless Piggy:BypassPurchase is " +
                 "on there too, so both switches have to be wrong at once. Off in every build.")]
        [SerializeField] private bool testBreakWithoutPurchase;

        [Header("Open / close juice")]
        [Tooltip("The panel BODY that scales — the framed popup, not the canvas root. Empty = the first child when " +
                 "this object is a Canvas (a Canvas rewrites its own scale, so it can never be animated).")]
        [SerializeField] private RectTransform openTarget;
        [Tooltip("The CanvasGroup that fades. Leave EMPTY: one is found on the body, then this object, and added to " +
                 "the body if neither has one.")]
        [SerializeField] private CanvasGroup fadeGroup;

        [SerializeField] private float openFromScale = 0.86f;
        [SerializeField] private float openRise = 60f;
        [SerializeField] private float openSeconds = 0.3f;
        [Range(0f, 0.3f)][SerializeField] private float openOvershoot = 0.06f;
        [Tooltip("Closing runs the opening BACKWARDS — anticipate, then drop away — so it lands on the same scale and " +
                 "position it grew from. A touch quicker than the open: an exit that takes as long as an entrance " +
                 "reads as the UI being slow.")]
        [SerializeField] private float closeSeconds = 0.24f;

        /// <summary>
        /// The player wants to buy — which offer is the argument. Subscribe from the IAP layer, run the store flow,
        /// and on a verified receipt call the break endpoint.
        ///
        /// Nothing happens without a subscriber: with no IAP wired the buttons are inert rather than silently paying
        /// out, which is the correct failure for a purchase.
        /// </summary>
        public event Action<PiggyBreakOption> BreakRequested;

        /// <summary>Raised as the open tween starts, with the object already active.</summary>
        public event Action Opened;

        /// <summary>Raised as the close tween STARTS, while the object is still active, so a listener still exists.</summary>
        public event Action Closing;

        private void Awake()
        {
            if (screen == null) screen = GetComponent<PiggyScreen>();
            screen.BreakRequested += OnBreakRequested;
            screen.CloseRequested += Close;

            screen.ForceFull = testMode;

            // Loud on purpose. A build that shipped with this left on would show every player a bank that claims to
            // be full, and the only symptom would be confused players - so say it every single run.
            if (testMode)
                Debug.LogWarning($"{name}: piggy TEST MODE is ON - the popup will open in the FULL state regardless " +
                                 "of the server. Turn it off on the prefab before shipping.", this);

            if (testBreakWithoutPurchase)
                Debug.LogWarning($"{name}: piggy DIRECT BREAK is ON - the buy buttons call the break endpoint " +
                                 "instead of the store. Harmless unless Piggy:BypassPurchase is also on server-side, " +
                                 "but it must be off before shipping.", this);
        }

        private void OnDestroy()
        {
            if (screen == null) return;
            screen.BreakRequested -= OnBreakRequested;
            screen.CloseRequested -= Close;
        }

        private void OnDisable()
        {
            // Leave the popup at rest. A close tween cut short would otherwise store it shrunk and half faded, and
            // that is what the NEXT open would start from.
            _open?.Kill();
            _open = null;
            if (_body != null) { _body.localScale = _restScale; _body.localPosition = _restPos; }
            if (fadeGroup != null) { fadeGroup.alpha = 1f; fadeGroup.interactable = true; fadeGroup.blocksRaycasts = true; }
        }

        /// <summary>Open it: paint what's cached immediately, then refresh so the numbers are the server's.</summary>
        public async void Open()
        {
            gameObject.SetActive(true);
            screen.ForceFull = testMode;   // re-applied here too: the flag can be flipped in the inspector mid-play
            PrepareOpen();
            Opened?.Invoke();

            // Armed BEFORE the first paint, so the cached render itself starts the run — otherwise the bar shows the
            // real value for a frame, snaps to empty, and only then runs up.
            screen.ArmIntro();

            var cached = PiggyState.Instance.Current;
            if (cached != null) screen.Render(cached);

            if (_openRoutine != null) StopCoroutine(_openRoutine);
            _openRoutine = StartCoroutine(PlayOpenNextFrame());

            var fresh = await PiggyState.Instance.RefreshAsync(force: true);
            if (this != null && fresh != null) screen.Render(fresh);
        }

        public void Close()
        {
            Closing?.Invoke();

            var body = Body();
            if (body == null || closeSeconds <= 0f) { gameObject.SetActive(false); return; }

            _open?.Kill();
            if (fadeGroup != null) { fadeGroup.interactable = false; fadeGroup.blocksRaycasts = false; }

            // The opening, backwards. The open swells past its mark and settles; the close ANTICIPATES — a small
            // swell first — and then drops away to exactly the scale and position it grew from.
            //
            // The anticipation is what makes it read as an exit rather than a fade. Without it the panel simply gets
            // smaller and more transparent at once, which the eye reads as "it stopped being drawn"; with it, the
            // panel looks like it gathers itself and leaves.
            float anticipate = closeSeconds * 0.28f;
            float away = Mathf.Max(0.01f, closeSeconds - anticipate);

            var seq = DOTween.Sequence().SetUpdate(true);

            if (openOvershoot > 0f && anticipate > 0.001f)
                seq.Append(body.DOScale(_restScale * (1f + openOvershoot * 0.6f), anticipate).SetEase(Ease.OutQuad));
            else
                anticipate = 0f;

            seq.Append(body.DOScale(_restScale * openFromScale, away).SetEase(Ease.InBack, 1.2f));

            // INSERT at the anticipation's end, never Join: Join would attach to the last APPENDED tween and inherit
            // its start, which after the anticipation is not where these should begin.
            if (openRise != 0f)
                seq.Insert(anticipate, body.DOLocalMoveY(_restPos.y - openRise, away).SetEase(Ease.InCubic));

            // Fade LAST and fast. Fading over the whole exit hides the movement that is doing the work.
            if (fadeGroup != null)
                seq.Insert(anticipate + away * 0.35f, fadeGroup.DOFade(0f, away * 0.65f).SetEase(Ease.InQuad));

            seq.OnComplete(() => { if (this != null) gameObject.SetActive(false); });
            _open = seq;
        }

        private void OnBreakRequested(PiggyBreakOption option)
        {
            if (BreakRequested == null && !testBreakWithoutPurchase)
            {
                // Say it out loud. A dead buy button with no explanation is the worst outcome here: it looks like a
                // bug in the popup when it is simply an unwired purchase layer.
                Debug.LogWarning($"{name}: '{option}' was chosen but nothing is listening for PiggyPanel.BreakRequested — " +
                                 "the purchase layer isn't wired yet, so nothing will happen.", this);
                return;
            }

            // Lock and switch to BUSY before handing off. The store sheet takes a moment to appear, and a live buy
            // button in that window is a second purchase nobody meant to make.
            screen.SetBuyInteractable(false);
            screen.SetBusy(true);

            if (testBreakWithoutPurchase) { _ = BreakDirectAsync(option); return; }

            BreakRequested.Invoke(option);
        }

        /// <summary>
        /// The purchase did not happen — cancelled, refused, or failed to verify. Put the popup back so the player
        /// can try again.
        ///
        /// Public because only the IAP layer knows a purchase died: a store flow can end in a dozen ways and none of
        /// them reach this component. Leaving it out would strand the popup on a spinner forever.
        /// </summary>
        public void CancelBreak()
        {
            if (screen == null) return;
            screen.SetBusy(false);
            screen.SetBuyInteractable(true);
        }

        /// <summary>
        /// TEST PATH: skip the store and call the break endpoint straight away.
        ///
        /// A fresh purchase id per tap, deliberately. A real store id repeats, and repeating it is what proves the
        /// payout is idempotent - which is the opposite of what you want while tuning, where every tap should pay
        /// out again so the animation can be watched more than once.
        /// </summary>
        private async Task BreakDirectAsync(PiggyBreakOption option)
        {
            var result = await PiggyState.Instance.BreakAsync(option, "test-" + Guid.NewGuid().ToString("N"));
            if (this == null) return;

            if (result == null || !result.Ok)
            {
                Debug.LogWarning($"{name}: test break refused — {result?.Error ?? "no response"}. The SERVER gate is " +
                                 "separate: turn Piggy:BypassPurchase on in Admin ▸ Testing too.", this);
                CancelBreak();
                return;
            }

            var director = GetComponent<PiggyBreakDirector>();
            if (director == null)
            {
                Debug.LogWarning($"{name}: the break paid {result.Amount} but there is no PiggyBreakDirector to " +
                                 "show it.", this);
                CancelBreak();
                return;
            }

            // The SERVER'S figure, never a locally derived one — on an early break the payout is not what the bank
            // held, and showing the wrong number for money just spent is the worst possible place to be wrong.
            director.PlayBreak(result.Amount);
        }

        private IEnumerator PlayOpenNextFrame()
        {
            yield return null;   // let the build's frame — and its delta — go by
            _openRoutine = null;
            PlayOpen();
        }

        private void PrepareOpen()
        {
            var body = Body();
            if (body == null) return;

            _open?.Kill();
            if (fadeGroup != null) { fadeGroup.interactable = true; fadeGroup.blocksRaycasts = true; }

            if (openSeconds <= 0f)
            {
                body.localScale = _restScale;
                body.localPosition = _restPos;
                if (fadeGroup != null) fadeGroup.alpha = 1f;
                return;
            }

            body.localScale = _restScale * openFromScale;
            body.localPosition = _restPos - new Vector3(0f, openRise, 0f);
            if (fadeGroup != null) fadeGroup.alpha = 0f;
            _posed = true;
        }

        private void PlayOpen()
        {
            var body = Body();
            if (body == null || openSeconds <= 0f) return;

            if (!_posed) PrepareOpen();
            _posed = false;

            var seq = DOTween.Sequence().SetUpdate(true);

            // Two beats — past full size, then back — instead of one OutBack. The settle is a separate, slower curve,
            // which is the difference between "it bounced" and "it landed".
            if (openOvershoot > 0f)
            {
                float up = openSeconds * 0.62f;
                seq.Append(body.DOScale(_restScale * (1f + openOvershoot), up).SetEase(Ease.OutCubic));
                seq.Append(body.DOScale(_restScale, openSeconds - up).SetEase(Ease.OutQuad));
            }
            else
            {
                seq.Append(body.DOScale(_restScale, openSeconds).SetEase(Ease.OutBack));
            }

            // INSERT at 0, never Join: Join attaches to the last APPENDED element, which would start these 62% in.
            if (openRise != 0f)
                seq.Insert(0f, body.DOLocalMoveY(_restPos.y, openSeconds * 0.85f).SetEase(Ease.OutCubic));

            if (fadeGroup != null)
                seq.Insert(0f, fadeGroup.DOFade(1f, Mathf.Min(0.12f, openSeconds)).SetEase(Ease.OutQuad));

            _open = seq;
        }

        /// <summary>
        /// What scales, resolved once. A root CANVAS can never be it — the Canvas rewrites its own transform's scale
        /// from its CanvasScaler every frame, so a tween on it is undone before it draws and the popup just appears.
        /// </summary>
        private RectTransform Body()
        {
            if (_bodyResolved) return _body;
            _bodyResolved = true;

            _body = openTarget;
            if (_body == null)
            {
                bool isCanvas = GetComponent<Canvas>() != null;
                if (!isCanvas) _body = transform as RectTransform;
                else
                {
                    for (int i = 0; i < transform.childCount && _body == null; i++)
                        _body = transform.GetChild(i) as RectTransform;

                    if (_body == null)
                        Debug.LogWarning($"{name}: nothing to animate — this object is a Canvas (whose scale is driven " +
                                         "by its CanvasScaler) and it has no child to scale. Assign Open Target.", this);
                }
            }

            if (fadeGroup == null && _body != null) fadeGroup = _body.GetComponent<CanvasGroup>();
            if (fadeGroup == null) fadeGroup = GetComponent<CanvasGroup>();
            if (fadeGroup == null && _body != null) fadeGroup = _body.gameObject.AddComponent<CanvasGroup>();

            if (_body != null)
            {
                var s = _body.localScale;
                _restScale = (s.x <= 0.0001f || s.y <= 0.0001f) ? Vector3.one : s;
                _restPos = _body.localPosition;
            }
            return _body;
        }

        private RectTransform _body;
        private bool _bodyResolved;
        private bool _posed;
        private Vector3 _restScale = Vector3.one;
        private Vector3 _restPos;
        private Tween _open;
        private Coroutine _openRoutine;
    }
}
