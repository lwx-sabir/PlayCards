using System;
using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using Khela.Common.Daily;
using UnityEngine;

namespace PlayCard.Daily
{
    /// <summary>
    /// The glue on the daily popup's root: fetches through <see cref="DailyState"/>, feeds <see cref="DailyScreen"/>,
    /// and turns the screen's intents back into server calls. Also owns the open/close animation.
    ///
    /// Nothing below decides anything about the reward. A claim is sent and the server's answer is re-read; a refusal
    /// is shown, not worked around. That's what keeps the client honest about which days are collectable.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(DailyScreen))]
    public sealed class DailyPanel : MonoBehaviour
    {
        [Tooltip("Leave empty to use the DailyScreen on this object.")]
        [SerializeField] private DailyScreen screen;

        [Header("Open / close juice")]
        [Tooltip("The panel BODY that scales — the framed popup, not the canvas root. Empty = the first child when " +
                 "this object is a Canvas (a Canvas rewrites its own scale, so it can never be animated).")]
        [SerializeField] private RectTransform openTarget;
        [Tooltip("The CanvasGroup that fades. Leave EMPTY: one is found on the body, then this object, and added to " +
                 "the body if neither has one. It has to cover everything, or parts fade out of step.")]
        [SerializeField] private CanvasGroup fadeGroup;

        [SerializeField] private float openFromScale = 0.86f;
        [Tooltip("How far BELOW its resting place the popup starts. The rise is what separates an arrival from a pop.")]
        [SerializeField] private float openRise = 60f;
        [SerializeField] private float openSeconds = 0.3f;
        [Range(0f, 0.3f)][SerializeField] private float openOvershoot = 0.06f;

        [Tooltip("Closing is quicker than opening and moves as ONE piece — a staggered exit reads as falling apart.")]
        [SerializeField] private float closeSeconds = 0.17f;
        [SerializeField] private float closeToScale = 0.93f;

        /// <summary>
        /// A rewarded ad needs to play for <c>day</c>, using the server-issued <c>token</c> as the ad's custom data.
        /// The ad SDK integration subscribes here; the CREDIT arrives from the network's server-to-server callback,
        /// never from whatever the SDK tells the client, so this handler's only job is to show the ad.
        /// </summary>
        public event Action<int, string> AdRequested;

        /// <summary>The popup is opening — raised as the tween starts, with the object already active.</summary>
        public event Action Opened;

        /// <summary>The popup is closing — raised as the close tween STARTS, while the object is still active, so a
        /// listener still exists to hear it.</summary>
        public event Action Closing;

        private void Awake()
        {
            if (screen == null) screen = GetComponent<DailyScreen>();
            screen.ClaimRequested += OnClaimRequested;
            screen.CloseRequested += Close;
        }

        private void OnEnable() => DailyState.Instance.Changed += OnStateChanged;

        private void OnDisable()
        {
            DailyState.Instance.Changed -= OnStateChanged;

            // Leave the popup at rest. A close tween cut short would otherwise store it shrunk and half faded, and
            // that is what the NEXT open would start from.
            _open?.Kill();
            _open = null;
            if (_body != null) { _body.localScale = _restScale; _body.localPosition = _restPos; }
            if (fadeGroup != null) { fadeGroup.alpha = 1f; fadeGroup.interactable = true; fadeGroup.blocksRaycasts = true; }
        }

        private void OnDestroy()
        {
            if (screen == null) return;
            screen.ClaimRequested -= OnClaimRequested;
            screen.CloseRequested -= Close;
        }

        /// <summary>
        /// Open it: render whatever is cached immediately (no empty frame), then refresh.
        ///
        /// The ORDER matters. Building the ladder instantiates 28 tiles and forces a layout pass — one long frame.
        /// Starting the tween before that leaves it frozen through the hitch and then jumping, so the opening's first
        /// third never draws. Pose it hidden, pay the build cost while invisible, animate on a clean frame.
        /// </summary>
        public async void Open()
        {
            gameObject.SetActive(true);
            PrepareOpen();
            Opened?.Invoke();          // after SetActive, so anything on this object is enabled and listening

            var cached = DailyState.Instance.Current;
            if (cached != null) screen.Render(cached);

            if (_openRoutine != null) StopCoroutine(_openRoutine);
            _openRoutine = StartCoroutine(PlayOpenNextFrame());

            var fresh = await DailyState.Instance.RefreshAsync();
            if (this != null && fresh != null) screen.Render(fresh);
        }

        /// <summary>Close it, on the way out.</summary>
        public void Close()
        {
            Closing?.Invoke();         // before anything can deactivate this object

            var body = Body();
            if (body == null || closeSeconds <= 0f) { gameObject.SetActive(false); return; }

            _open?.Kill();
            if (fadeGroup != null) { fadeGroup.interactable = false; fadeGroup.blocksRaycasts = false; }

            var seq = DOTween.Sequence().SetUpdate(true);
            seq.Join(body.DOScale(_restScale * closeToScale, closeSeconds).SetEase(Ease.InBack, 1.1f));
            if (openRise != 0f) seq.Join(body.DOLocalMoveY(_restPos.y - openRise * 0.4f, closeSeconds).SetEase(Ease.InCubic));
            if (fadeGroup != null) seq.Join(fadeGroup.DOFade(0f, closeSeconds * 0.8f).SetEase(Ease.InQuad));

            // Deactivate only once it's gone: disabling on the first frame is what makes a panel "blink" shut.
            seq.OnComplete(() => { if (this != null) gameObject.SetActive(false); });
            _open = seq;
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

            // Fade on a FIXED, short curve — never a share of openSeconds, or a slower open would also be a LATER one.
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

            // The fade must cover the WHOLE popup, so one is added rather than left to chance.
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

        private void OnStateChanged(DailyStateDto state)
        {
            if (this == null || !gameObject.activeInHierarchy) return;
            screen.Render(state);
        }

        private async void OnClaimRequested(int day, bool useAds)
        {
            // NO busy guard here. DailyState queues claims, so every tap is sent in turn. Dropping the tap instead —
            // which is what a guard does — left the tile optimistically collected with nothing to confirm or revert
            // it, and the next refresh quietly rebound it from the server as unclaimed. That is the "it comes back":
            // not a server refusal, a request that was never made.
            if (useAds)
            {
                await HandleAdUnlockAsync(day);
                return;
            }

            var result = await DailyState.Instance.ClaimAsync(day);

            // The tile already flipped on the tap (see DailyScreen.BeginCollect). Confirm it, or put it back.
            if (this == null || screen == null) return;
            if (result != null && result.Ok) screen.ConfirmCollect(day);
            else screen.RevertCollect(day);

            Report(result, day);
        }

        /// <summary>
        /// A missed day bought with rewarded ads. If the player already holds enough VERIFIED credits (the network
        /// called us back earlier), the claim goes straight through; otherwise we ask the server for an intent token
        /// and hand it to whoever shows ads. We never claim on the SDK's say-so.
        /// </summary>
        private async System.Threading.Tasks.Task HandleAdUnlockAsync(int day)
        {
            var state = DailyState.Instance.Current;
            bool enoughCredits = state != null && state.AdCreditsHeld >= Mathf.Max(1, state.AdsPerUnlock);

            if (enoughCredits)
            {
                var claimed = await DailyState.Instance.ClaimAsync(day, useAds: true);
                Report(claimed, day);
                return;
            }

            var intent = await DailyState.Instance.RequestAdIntentAsync(day);
            if (intent == null || !intent.Ok)
            {
                Debug.LogWarning($"[Daily] day {day} can't be unlocked with ads: {intent?.Error}");
                return;
            }

            if (AdRequested == null)
            {
                Debug.LogWarning($"[Daily] no ad handler wired — day {day} needs {intent.AdsRequired} ad view(s). " +
                                 "Subscribe to DailyPanel.AdRequested when the ad SDK lands.");
                return;
            }

            AdRequested.Invoke(day, intent.Token);
            // Nothing else happens here: the credit lands via the network's callback, and the next refresh sees it.
        }

        private static void Report(DailyClaimResultDto result, int day)
        {
            if (result == null) return;
            if (!result.Ok) { Debug.LogWarning($"[Daily] claim day {day} refused: {result.Error}"); return; }
            // The reward animation is driven by DailyState.RewardsGranted, so whatever HUD is in the current scene
            // gets it — the panel deliberately doesn't own that.
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
