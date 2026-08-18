using System;
using System.Threading.Tasks;
using DG.Tweening;
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

        [Header("Open / close juice")]
        [Tooltip("The panel BODY that scales — the framed sheet, not the canvas root. Empty = the first child when " +
                 "this object is a Canvas (a Canvas rewrites its own scale, so it can never be animated).")]
        [SerializeField] private RectTransform openTarget;
        [Tooltip("The CanvasGroup that fades. Leave EMPTY: one is found on the body, then this object, and added to " +
                 "the body if neither has one. It has to cover everything, or parts of the panel fade out of step.")]
        [SerializeField] private CanvasGroup fadeGroup;

        [Header("Open")]
        [Tooltip("Scale it opens FROM. Lower = more of a launch; too low reads as a zoom.")]
        [SerializeField] private float openFromScale = 0.82f;
        [Tooltip("How far BELOW its resting place the sheet starts, in canvas units. The rise is what separates an " +
                 "arrival from a plain pop.")]
        [SerializeField] private float openRise = 70f;
        [Tooltip("Total time for the body to land.")]
        [SerializeField] private float openSeconds = 0.34f;
        [Tooltip("How hard it overshoots past full size before settling. 0 = no overshoot.")]
        [Range(0f, 0.3f)][SerializeField] private float openOvershoot = 0.06f;

        [Header("Open — contents cascade")]
        [Tooltip("Bring the body's children in one after another instead of the whole sheet arriving flat. This is " +
                 "most of what makes an open feel expensive.")]
        [SerializeField] private bool cascadeChildren = true;
        [Tooltip("Gap between one child starting and the next.")]
        [SerializeField] private float cascadeStep = 0.045f;
        [Tooltip("Scale each child starts at. Only SCALE and alpha are animated — never position, which a layout " +
                 "group would fight.")]
        [SerializeField] private float cascadeFromScale = 0.9f;
        [SerializeField] private float cascadeSeconds = 0.24f;

        /// <summary>Hard ceiling on how long the whole cascade may take to START its last child. Beyond this the panel
        /// stops reading as "assembling" and starts reading as "slow".</summary>
        private const float MaxCascadeSpread = 0.18f;

        [Header("Close")]
        [Tooltip("Closing is quicker than opening and moves as ONE piece — a staggered exit is what makes a panel " +
                 "look like it's falling apart on the way out.")]
        [SerializeField] private float closeSeconds = 0.18f;
        [Tooltip("Scale it shrinks to. A small dip reads as a dismissal; a big one reads as a retreat.")]
        [SerializeField] private float closeToScale = 0.93f;
        [Tooltip("Dip the sheet DOWN as it goes, mirroring the rise on the way in. 0 = straight shrink.")]
        [SerializeField] private float closeDrop = 30f;

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

        /// <summary>The panel is opening — raised as the open tween starts, with the panel already active.</summary>
        public event Action Opened;

        /// <summary>The panel is closing — raised as the close tween STARTS, while the object is still active, so a
        /// listener still exists to hear it. Raised on the instant-close path too.</summary>
        public event Action Closing;

        private void Awake()
        {
            if (screen == null) screen = GetComponent<PassScreen>();
            screen.ClaimRequested += OnClaimRequested;
            screen.SubscribeRequested += OnSubscribeRequested;
            screen.CloseRequested += Close;
        }

        private void OnEnable() => PassState.Instance.Changed += OnStateChanged;

        private void OnDisable()
        {
            PassState.Instance.Changed -= OnStateChanged;

            // Leave the sheet at rest. A close tween cut short (a scene change, a parent disabled) would otherwise
            // leave the panel stored shrunk, dropped and half faded — and that is what the NEXT open starts from.
            _open?.Kill();
            _open = null;
            StopCascade(snapToRest: true);

            if (_body != null) { _body.localScale = _restScale; _body.localPosition = _restPos; }
            if (fadeGroup != null) { fadeGroup.alpha = 1f; fadeGroup.interactable = true; fadeGroup.blocksRaycasts = true; }
        }

        private void OnDestroy()
        {
            if (screen == null) return;
            screen.ClaimRequested -= OnClaimRequested;
            screen.SubscribeRequested -= OnSubscribeRequested;
            screen.CloseRequested -= Close;
        }

        /// <summary>
        /// Open the pass: render whatever is cached immediately (no empty frame), then refresh.
        ///
        /// The ORDER here is the whole trick. Building the ladder instantiates ~90 objects and forces a canvas update
        /// — one long frame. Starting the open tween before that meant the tween sat frozen through the hitch and then
        /// jumped, so the first third of the animation never drew and the panel looked like it hadn't reacted to the
        /// tap. So: pose it hidden, pay the build cost while it's invisible, and start the animation on a clean frame.
        /// </summary>
        public async void Open()
        {
            gameObject.SetActive(true);
            PrepareOpen();
            // After SetActive, so anything living on this object (the panel's audio) is enabled and listening.
            Opened?.Invoke();

            var cached = PassState.Instance.Current;
            if (cached != null) screen.Render(cached);

            if (_openRoutine != null) StopCoroutine(_openRoutine);
            _openRoutine = StartCoroutine(PlayOpenNextFrame());

            var fresh = await PassState.Instance.RefreshAsync(passKey: NullIfEmpty(passKey));
            if (this != null && fresh != null) screen.Render(fresh);
        }

        private System.Collections.IEnumerator PlayOpenNextFrame()
        {
            yield return null;   // let the build's frame — and its delta — go by
            _openRoutine = null;
            PlayOpen();
        }

        private Coroutine _openRoutine;

        /// <summary>
        /// Close it, as ONE piece.
        ///
        /// Nothing is staggered on the way out and every child's own tween is killed first. A panel that cascades OUT
        /// looks like it is coming apart — the inner items visibly lag the frame around them — and lengthening the
        /// close only makes that more obvious, which is exactly the failure it had. One group fade plus one scale, on
        /// the same curve and the same clock, is what reads as decisive.
        /// </summary>
        public void Close()
        {
            // Before anything can deactivate this object — a listener on it must still be alive to hear this.
            Closing?.Invoke();

            var body = Body();
            if (body == null || closeSeconds <= 0f) { gameObject.SetActive(false); return; }

            _open?.Kill();
            StopCascade(snapToRest: true);

            // Dead to input the instant it's dismissed — a tap landing on a panel that is visibly leaving is a bug
            // the player will blame on lag.
            if (fadeGroup != null) { fadeGroup.interactable = false; fadeGroup.blocksRaycasts = false; }

            var seq = DOTween.Sequence().SetUpdate(true);
            seq.Join(body.DOScale(_restScale * closeToScale, closeSeconds).SetEase(Ease.InBack, 1.1f));
            if (closeDrop != 0f)
                seq.Join(body.DOLocalMoveY(_restPos.y - closeDrop, closeSeconds).SetEase(Ease.InCubic));
            // The fade is SHORTER than the movement and starts at once, so the sheet is gone before it finishes
            // travelling — the eye never gets to watch the last frames of the shrink.
            if (fadeGroup != null) seq.Join(fadeGroup.DOFade(0f, closeSeconds * 0.8f).SetEase(Ease.InQuad));

            // Deactivate only once it's gone: disabling on the first frame is what makes a panel "blink" shut.
            seq.OnComplete(() => { if (this != null) gameObject.SetActive(false); });
            _open = seq;
        }

        /// <summary>Put the panel in its starting pose and hide it, without animating anything. Cheap, and it means
        /// the expensive first render happens behind an invisible panel instead of a half-drawn one.</summary>
        private void PrepareOpen()
        {
            var body = Body();
            if (body == null) return;

            _open?.Kill();
            StopCascade(snapToRest: true);

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

        private bool _posed;

        /// <summary>
        /// The sheet arriving: it rises, overshoots, settles — and its contents cascade in behind it. Unscaled time,
        /// so it still plays over a paused game.
        /// </summary>
        private void PlayOpen()
        {
            var body = Body();
            if (body == null || openSeconds <= 0f) return;

            if (!_posed) PrepareOpen();
            _posed = false;

            var seq = DOTween.Sequence().SetUpdate(true);

            // Scale in two beats — past full size, then back — instead of one OutBack. The settle is a separate,
            // slower curve, which is the difference between "it bounced" and "it landed".
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

            // INSERT at 0, never Join: Join attaches to the last APPENDED element, so after the two scale beats it
            // would have started the rise 62% of the way in. These run against the whole sequence from the start.
            //
            // The rise is shorter than the scale, so the sheet is still growing after it has stopped moving. Two
            // motions of different lengths is what stops it looking like one canned pop.
            if (openRise != 0f)
                seq.Insert(0f, body.DOLocalMoveY(_restPos.y, openSeconds * 0.85f).SetEase(Ease.OutCubic));

            // Fade in on a FIXED, short curve — never a share of openSeconds. Tying visibility to the total duration
            // meant a slower open was also a LATER one: the panel stayed blank for longer the more time you gave it,
            // which is the opposite of what turning the dial should do. The panel is on screen within ~120ms of the
            // tap no matter how leisurely the rest of the movement is.
            if (fadeGroup != null)
                seq.Insert(0f, fadeGroup.DOFade(1f, Mathf.Min(0.12f, openSeconds)).SetEase(Ease.OutQuad));

            _open = seq;

            if (cascadeChildren) Cascade(body);
        }

        /// <summary>
        /// Bring the body's children in one after another, each on its own short curve.
        ///
        /// Only SCALE and alpha are touched. Position is deliberately left alone: most of these children sit in layout
        /// groups, which rewrite anchoredPosition on every rebuild and would erase a positional tween mid-flight —
        /// scale is the one property layout never touches.
        /// </summary>
        private void Cascade(RectTransform body)
        {
            _cascade.Clear();

            // Cap the WHOLE cascade. Per-child steps are fine for four children and a crawl for twelve — with a fixed
            // step the last one on a busy panel starts half a second after the tap, which is what "it doesn't open"
            // actually looks like. The step shrinks to fit instead.
            int count = 0;
            for (int i = 0; i < body.childCount; i++)
                if (body.GetChild(i) is RectTransform c && c.gameObject.activeSelf) count++;

            float step = count > 1 ? Mathf.Min(cascadeStep, MaxCascadeSpread / (count - 1)) : 0f;

            int shown = 0;
            for (int i = 0; i < body.childCount; i++)
            {
                var child = body.GetChild(i) as RectTransform;
                if (child == null || !child.gameObject.activeSelf) continue;

                var group = child.GetComponent<CanvasGroup>();
                if (group == null) group = child.gameObject.AddComponent<CanvasGroup>();

                var rest = child.localScale;
                if (rest.x <= 0.0001f || rest.y <= 0.0001f) rest = Vector3.one;

                child.localScale = rest * cascadeFromScale;
                group.alpha = 0f;

                // The FIRST child starts with the sheet, not after it. Anything else is dead air on a panel whose
                // every visible part is a child.
                float delay = shown * step;
                shown++;

                var captured = child;
                var seq = DOTween.Sequence().SetUpdate(true).SetDelay(delay);
                seq.Join(captured.DOScale(rest, cascadeSeconds).SetEase(Ease.OutBack, 1.4f));
                seq.Join(group.DOFade(1f, cascadeSeconds * 0.7f).SetEase(Ease.OutQuad));

                _cascade.Add(new CascadeItem { Target = captured, Group = group, Rest = rest, Tween = seq });
            }
        }

        /// <summary>Kill every child tween and, when asked, put the children back at full size and full alpha.
        /// Anything left mid-cascade is what makes a close look ragged.</summary>
        private void StopCascade(bool snapToRest)
        {
            for (int i = 0; i < _cascade.Count; i++)
            {
                var item = _cascade[i];
                item.Tween?.Kill();
                if (!snapToRest) continue;
                if (item.Target != null) item.Target.localScale = item.Rest;
                if (item.Group != null) item.Group.alpha = 1f;
            }
            _cascade.Clear();
        }

        private struct CascadeItem
        {
            public RectTransform Target;
            public CanvasGroup Group;
            public Vector3 Rest;
            public Tween Tween;
        }

        private readonly System.Collections.Generic.List<CascadeItem> _cascade =
            new System.Collections.Generic.List<CascadeItem>();

        /// <summary>
        /// What scales, resolved once — and its REST scale captured before anything has shrunk it.
        ///
        /// A root CANVAS can never be it. The Canvas rewrites its own transform's scale from the CanvasScaler every
        /// frame, so a scale tween on it is undone before it draws and the panel simply appears — which is exactly
        /// what a fallback of "this object" produces on a prefab whose root is the canvas. So when this IS a canvas,
        /// the first child is used instead: on this prefab that's the panel body, which is the right thing anyway.
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

            // The fade must cover the WHOLE panel. With no group at all, only the scale animates and the contents
            // simply vanish when the object is disabled — which is what "the inner items faded after the outside"
            // actually was. One is added rather than left to chance.
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
        private Vector3 _restScale = Vector3.one;
        private Vector3 _restPos;
        private Tween _open;

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
