using System.Collections;
using System.Collections.Generic;
using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Smoothly fades the other-player avatar cards OUT while the camera is CLOSED IN (betting + the local player's
    /// turn) and back IN whenever it pulls WIDE — including the deal-in and the whole round-end ceremony. It mirrors
    /// <see cref="TableCameraController"/>'s "close" state EXACTLY (same betting / my-turn / RoundEndSettling /
    /// DecisionReady rules), so the fade and the camera move together and the avatars always come back when the FOV
    /// releases. Put this on the parent that holds the avatar layouts (NOT the local bottom HUD) — it adds a
    /// CanvasGroup and tweens its alpha.
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    public sealed class BettingAvatarFader : MonoBehaviour
    {
        [SerializeField] private TableController table;
        [Tooltip("The table view — so the fade tracks the camera through the DEAL and the ROUND-END (DecisionReady / " +
                 "RoundEndSettling), not just the raw board. Auto-found if empty.")]
        [SerializeField] private BlackjackTableView view;
        [SerializeField] private CanvasGroup group;
        [Tooltip("Fade time in seconds — match the camera's Move Time so they move together.")]
        [SerializeField] private float fadeDuration = 0.45f;
        [Tooltip("Optional: the LOCAL player's avatar CanvasGroup. If set, it fades together with the other-player " +
                 "avatars (same alpha + timing); leave empty to keep your own avatar visible.")]
        [SerializeField] private CanvasGroup playerAvatar;

        [Tooltip("Anything ELSE to fade with the betting FOV — drop in the LOCAL player's avatar, table props, decor, " +
                 "whatever. UI objects fade via a CanvasGroup (one is added if missing); 3D objects fade via their " +
                 "renderers' material alpha — which needs a TRANSPARENT-capable material to actually show (an opaque " +
                 "URP/Lit material won't visibly change). Everything here fades OUT on bet FOV and smoothly back IN on " +
                 "release, on the same alpha + timing as the avatars.")]
        [SerializeField] private List<GameObject> alsoFade = new List<GameObject>();

        private readonly List<CanvasGroup> _cgFades = new List<CanvasGroup>();
        private readonly List<(Material mat, int id, float baseA)> _matFades = new List<(Material, int, float)>();

        private Coroutine _fade;
        private bool _closeInit;
        private bool _lastClose;

        private void Awake()
        {
            if (group == null) group = GetComponent<CanvasGroup>();
            ResolveExtraTargets();
        }

        // Resolve the 'alsoFade' list ONCE into cheap per-frame drivers: a CanvasGroup per UI target (added if the UI
        // object lacks one), and an instanced material + its colour property per 3D renderer. `.materials` instances the
        // materials so the tint stays on THIS object's copies, never the shared asset.
        private void ResolveExtraTargets()
        {
            _cgFades.Clear();
            _matFades.Clear();
            if (alsoFade == null) return;
            foreach (var go in alsoFade)
            {
                if (go == null) continue;
                var cg = go.GetComponent<CanvasGroup>();
                if (cg == null && go.GetComponent<RectTransform>() != null) cg = go.AddComponent<CanvasGroup>();
                if (cg != null) { _cgFades.Add(cg); continue; }
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    foreach (var mat in r.materials)
                    {
                        if (mat == null) continue;
                        bool baseCol = mat.HasProperty("_BaseColor");
                        if (!baseCol && !mat.HasProperty("_Color")) continue;
                        int id = Shader.PropertyToID(baseCol ? "_BaseColor" : "_Color");
                        _matFades.Add((mat, id, mat.GetColor(id).a));
                    }
                }
            }
        }

        private void OnEnable()
        {
            if (view == null) view = FindAnyObjectByType<BlackjackTableView>();
            if (table != null) table.OnBoardChanged += OnBoard;
            Refresh();
        }

        private void OnDisable()
        {
            if (table != null) table.OnBoardChanged -= OnBoard;
        }

        private void OnBoard(BoardSnapshot board) => Refresh();

        // DecisionReady + RoundEndSettling change BETWEEN board pushes (cards land; the round-end ceremony ends), so a
        // board-only trigger would leave the fade a beat behind the camera. Re-check every frame; the change-guard in
        // Refresh keeps it free when nothing moved.
        private void Update() => Refresh();

        // TRUE when the camera is closed in — betting, OR my turn once my cards have landed. It stays WIDE (false)
        // through the deal-in and the whole round-end ceremony, so this must NOT treat those as "betting". Matches
        // TableCameraController.Resolve exactly. The OLD check used raw !RoundInProgress, so it faded the avatars out
        // the instant a round settled and left them out through the entire round-end — the "never comes back" bug.
        private bool ComputeClose()
        {
            var board = table != null ? table.Board : null;
            if (board == null) return false;
            int seat = table != null ? table.MySeat : -1;
            bool roundEndSettling = view != null && view.RoundEndSettling;
            bool dealt = view == null || view.DecisionReady(seat);
            bool betting = !board.RoundInProgress && !roundEndSettling;
            bool myTurn = board.RoundInProgress && seat >= 1 && board.CurrentSeatNumber == seat && dealt;
            return betting || myTurn;
        }

        private void Refresh()
        {
            bool close = ComputeClose();
            if (_closeInit && close == _lastClose) return;
            _closeInit = true;
            _lastClose = close;
            FadeTo(close ? 0f : 1f);
        }

        private void FadeTo(float target)
        {
            if (group == null) return;
            if (!isActiveAndEnabled) { Apply(target); return; }       // can't run a coroutine while inactive
            if (_fade != null) StopCoroutine(_fade);
            _fade = StartCoroutine(FadeRoutine(target));
        }

        private IEnumerator FadeRoutine(float target)
        {
            float start = group.alpha;
            if (target < start) SetInteractable(false);               // dropping → kill input immediately

            float t = 0f;
            while (t < fadeDuration && fadeDuration > 0f)
            {
                t += Time.unscaledDeltaTime;
                SetAlpha(Mathf.Lerp(start, target, Mathf.SmoothStep(0f, 1f, t / fadeDuration)));
                yield return null;
            }
            SetAlpha(target);
            if (target > 0.99f) SetInteractable(true);                // fully shown → input back on
            _fade = null;
        }

        private void Apply(float a)
        {
            SetAlpha(a);
            SetInteractable(a > 0.99f);
        }

        // Drive the other-player group AND (if assigned) the local player's avatar together.
        private void SetAlpha(float a)
        {
            group.alpha = a;
            if (playerAvatar != null) playerAvatar.alpha = a;
            for (int i = 0; i < _cgFades.Count; i++)
                if (_cgFades[i] != null) _cgFades[i].alpha = a;
            for (int i = 0; i < _matFades.Count; i++)
            {
                var mf = _matFades[i];
                if (mf.mat == null) continue;
                var c = mf.mat.GetColor(mf.id);
                c.a = a * mf.baseA;   // scale the material's ORIGINAL alpha, so we never push it past what was authored
                mf.mat.SetColor(mf.id, c);
            }
        }

        private void SetInteractable(bool on)
        {
            group.blocksRaycasts = on;
            group.interactable = on;
            if (playerAvatar != null) { playerAvatar.blocksRaycasts = on; playerAvatar.interactable = on; }
            for (int i = 0; i < _cgFades.Count; i++)
                if (_cgFades[i] != null) { _cgFades[i].blocksRaycasts = on; _cgFades[i].interactable = on; }
        }
    }
}
