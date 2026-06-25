using System.Collections;
using System.Collections.Generic;
using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Win juice: when the local player's hand SETTLES as a win, a burst of UI chip sprites arcs from the bet area
    /// (the <see cref="source"/> point) into the balance <see cref="target"/> icon, and the icon punches ("pops") on
    /// each chip that lands. Display only — the payout already happened server-side. Fires ONCE per round, on the
    /// in-round → settled transition.
    ///
    /// SOURCE and TARGET are both hand-placed UI RectTransforms; positions are computed in <see cref="spawnParent"/>'s
    /// LOCAL space (no camera, no screen-point round-trip) so it behaves identically in edit and play and never
    /// flips/collapses. Put SOURCE over the bet spot as the player sees it; put TARGET on the balance chip icon.
    /// ENABLE <see cref="previewInEditor"/> to see a rough scatter cloud (Chip Count chips at the blast's reach)
    /// around the source in edit mode — move the source / tweak the blast force and it updates live. <see cref="spawnParent"/> must be a plain rect (NO Layout Group,
    /// or it'll fight the chip positions). Put this on an always-active HUD object. Sound comes later.
    /// </summary>
    [ExecuteAlways]
    public sealed class WinChipFly : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] private TableController table;
        [Tooltip("START point — place it over the bet spot as the player sees it (a UI rect on the HUD canvas).")]
        [SerializeField] private RectTransform source;
        [Tooltip("END point — the balance CHIP ICON to fly into and punch (its RectTransform).")]
        [SerializeField] private RectTransform target;
        [Tooltip("A RectTransform on the HUD canvas the flying chips spawn under (a plain rect — NO Layout Group).")]
        [SerializeField] private RectTransform spawnParent;
        [Tooltip("A UI chip prefab (an Image with your chip sprite).")]
        [SerializeField] private GameObject chipPrefab;

        [Header("Blast — all chips explode out of the source at once (physics)")]
        [SerializeField] private int chipCount = 12;
        [Tooltip("How hard the chips are shot out (canvas units/sec, randomised per chip) — the explosion force.")]
        [SerializeField] private float blastSpeed = 750f;
        [Tooltip("Downward pull on the flying chips (units/sec²): 0 = floaty scatter, higher = they arc up then fall.")]
        [SerializeField] private float gravity = 1100f;
        [Tooltip("Air drag — how fast the blast speed bleeds off (per second). Higher = chips settle sooner.")]
        [SerializeField] private float drag = 3f;
        [Tooltip("How long the chips fly free as a blast before the pull takes over.")]
        [SerializeField] private float popTime = 0.55f;
        [Tooltip("Spin speed while blasting + flying (degrees/sec; direction randomised per chip). 0 = no spin.")]
        [SerializeField] private float spinSpeed = 600f;

        [Header("Pull (magnet) into the target")]
        [Tooltip("MAGNET STRENGTH — acceleration toward the icon (canvas units/sec²). THIS is the pull-speed knob: " +
                 "higher = a harder, faster yank. All chips start the pull together; the harder it is, the snappier " +
                 "they're sucked in. Try 6000 (gentle) … 30000 (violent).")]
        [SerializeField] private float pullForce = 14000f;
        [Tooltip("Velocity damping on the pull (per second) — higher = less overshoot, snappier stop. ~6–14.")]
        [SerializeField] private float pullDamping = 9f;
        [Tooltip("Chip scale on arrival (relative to the prefab) — shrinks as it nears the icon.")]
        [SerializeField] private float endScale = 0.55f;

        [Header("Icon pop (per chip that lands)")]
        [SerializeField] private float popScale = 1.25f;
        [SerializeField] private float popDuration = 0.18f;

        [Header("Editor preview")]
        [Tooltip("Freeze the real burst in edit mode — Chip Count chips along the arc with the actual Start Spread + " +
                 "End Scale — so you can dial every spatial setting without pressing Play (timing can't show statically).")]
        [SerializeField] private bool previewInEditor = true;

        private const string PreviewName = "__winflypreview";

        private bool _wasInRound;
        private Coroutine _pop;
        private Vector3 _targetBaseScale = Vector3.one;
        private readonly List<RectTransform> _preview = new List<RectTransform>();
        private Vector3 _chipBaseScale = Vector3.one;

        // ----------------------------------------------------------------- runtime

        private void OnEnable()
        {
#if UNITY_EDITOR
            ClearPreview();   // wipe edit-mode preview leftovers — incl. when ENTERING Play (DontSave survives the switch)
#endif
            if (Application.isPlaying)
            {
                if (target != null) _targetBaseScale = target.localScale;
                if (table != null)
                {
                    table.OnBoardChanged += OnBoard;
                    _wasInRound = table.Board != null && table.Board.RoundInProgress;   // don't fire on a settled join
                }
            }
#if UNITY_EDITOR
            else
            {
                UnityEditor.EditorApplication.update -= EditorTick;
                UnityEditor.EditorApplication.update += EditorTick;
            }
#endif
        }

        private void OnDisable()
        {
            if (table != null) table.OnBoardChanged -= OnBoard;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorTick;
            ClearPreview();
#endif
        }

        // ExecuteAlways Update — fires when the scene changes in edit mode (e.g. you drag the source) so the preview
        // tracks live; EditorApplication.update (below) covers idle. Together they're reliable.
        private void Update()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) EditorRefresh();
#endif
        }

        private void OnBoard(BoardSnapshot board)
        {
            bool inRound = board != null && board.RoundInProgress;
            if (!inRound && _wasInRound) TryFly(board);   // just settled → celebrate a win
            _wasInRound = inRound;
        }

        private void TryFly(BoardSnapshot board)
        {
            if (table == null || table.MySeat <= 0) return;
            if (source == null || target == null || chipPrefab == null || spawnParent == null) return;

            SeatResultView r = board.LastResults?.Find(x => x.SeatNumber == table.MySeat);
            if (r == null || r.Outcome != "win") return;   // win only — push / lose / bust do nothing

            Burst();
        }

        // All chips spawn TOGETHER and explode outward with their own velocities — a real blast, not a tween to a ring.
        private void Burst()
        {
            Vector3 src = LocalOf(source);
            Vector3 dst = LocalOf(target);
            int n = Mathf.Max(1, chipCount);
            for (int i = 0; i < n; i++)
            {
                var go = Instantiate(chipPrefab, spawnParent);
                if (go.transform is RectTransform rt) StartCoroutine(FlyOne(rt, src, dst, i));
                else Destroy(go);
            }
        }

        // (1) explode out of the source — launched with a random velocity, flung by gravity + drag, popping in
        // (elastic scale) and spinning; (2) brief hang; (3) accelerating yank into the target, still spinning, shrinking.
        private IEnumerator FlyOne(RectTransform rt, Vector3 src, Vector3 dst, int index)
        {
            Vector3 baseScale = rt.localScale;
            float spinDir = Random.value < 0.5f ? -1f : 1f;
            float angle = Random.Range(0f, 360f);

            // launch velocity: random direction (slight upward bias), random force → an organic explosion
            float a = Random.Range(0f, Mathf.PI * 2f);
            Vector3 vel = new Vector3(Mathf.Cos(a), Mathf.Sin(a) + 0.35f, 0f).normalized
                          * Random.Range(blastSpeed * 0.6f, blastSpeed);
            Vector3 pos = src;
            float popIn = Mathf.Max(0.0001f, popTime * 0.45f);

            // (1) blast — integrate velocity (gravity pulls down, drag bleeds it off), elastic scale-in, spin
            float t = 0f;
            while (t < popTime)
            {
                float dt = Time.unscaledDeltaTime;
                t += dt;
                vel.y -= gravity * dt;
                vel *= Mathf.Exp(-drag * dt);
                pos += vel * dt;
                rt.position = spawnParent.TransformPoint(pos);
                rt.localScale = baseScale * EaseOutElastic(Mathf.Clamp01(t / popIn));
                angle += spinDir * spinSpeed * dt;
                rt.localRotation = Quaternion.Euler(0f, 0f, angle);
                yield return null;
            }
            rt.localScale = baseScale;

            // (2) MAGNET pull — every chip starts NOW (together, off the floated cloud) and ACCELERATES toward the
            // icon: the harder Pull Force, the faster the yank (this is the responsive speed knob). Chips arrive ONE
            // BY ONE on their own — the blast left them at different distances, so the nearer ones hit first.
            float startDist = Mathf.Max(1f, (dst - pos).magnitude);
            vel = Vector3.zero;   // reuse the blast velocity var — reset before the magnet takes over
            int guard = 0;
            while (guard++ < 1000)
            {
                float dt = Time.unscaledDeltaTime;
                Vector3 to = dst - pos;
                float dist = to.magnitude;
                if (dist <= 10f) break;                                 // close enough → it's a hit
                vel += (to / dist) * pullForce * dt;                    // accelerate toward the icon
                vel *= Mathf.Exp(-pullDamping * dt);                    // damping → no wild overshoot
                pos += vel * dt;
                rt.position = spawnParent.TransformPoint(pos);
                float prox = 1f - Mathf.Clamp01(dist / startDist);      // 0 far … 1 at the icon
                rt.localScale = baseScale * Mathf.Lerp(1f, endScale, prox);
                angle += spinDir * spinSpeed * dt;
                rt.localRotation = Quaternion.Euler(0f, 0f, angle);
                yield return null;
            }
            Destroy(rt.gameObject);
            Punch();
        }

        private void Punch()
        {
            if (target == null) return;
            if (_pop != null) StopCoroutine(_pop);
            _pop = StartCoroutine(PunchRoutine());
        }

        private IEnumerator PunchRoutine()
        {
            Vector3 up = _targetBaseScale * popScale;
            float upT = popDuration * 0.4f, dnT = popDuration * 0.6f, t = 0f;
            while (t < upT) { t += Time.unscaledDeltaTime; target.localScale = Vector3.Lerp(_targetBaseScale, up, t / upT); yield return null; }
            t = 0f;
            while (t < dnT) { t += Time.unscaledDeltaTime; target.localScale = Vector3.Lerp(up, _targetBaseScale, t / dnT); yield return null; }
            target.localScale = _targetBaseScale;
            _pop = null;
        }

        // ----------------------------------------------------------------- shared math (spawnParent LOCAL space)

        // A UI rect's position expressed in spawnParent's local space — no camera, works in edit + play, any canvas mode.
        private Vector3 LocalOf(RectTransform rt) => spawnParent.InverseTransformPoint(rt.position);

        // Springy overshoot that settles to 1 — the "elastic pop".
        private static float EaseOutElastic(float u)
        {
            if (u <= 0f) return 0f;
            if (u >= 1f) return 1f;
            const float c4 = (2f * Mathf.PI) / 3f;
            return Mathf.Pow(2f, -10f * u) * Mathf.Sin((u * 10f - 0.75f) * c4) + 1f;
        }

        // ----------------------------------------------------------------- editor preview
#if UNITY_EDITOR
        private void EditorTick()
        {
            if (Application.isPlaying) return;
            if (this == null) { UnityEditor.EditorApplication.update -= EditorTick; return; }   // destroyed — detach
            EditorRefresh();
        }

        // Rebuild the pool if needed, then reposition along the live arc. Driven by Update() (scene changes) and
        // EditorTick (idle), so moving source / target / Arc Height updates immediately.
        private void EditorRefresh()
        {
            if (!previewInEditor || source == null || target == null || spawnParent == null || chipPrefab == null)
            {
                ClearPreview();   // always — also catches orphans left after a recompile (list reset, GOs persisted)
                return;
            }
            EnsurePreview();
            LayoutPreview();
        }

        [ContextMenu("Refresh Preview")] private void RefreshPreviewMenu() { ClearPreview(); EditorRefresh(); }
        [ContextMenu("Clear Preview")]   private void ClearPreviewMenu()   => ClearPreview();

        private void EnsurePreview()
        {
            int want = Mathf.Max(1, chipCount);
            _chipBaseScale = chipPrefab.transform.localScale;

            bool ok = _preview.Count == want;
            if (ok) for (int i = 0; i < _preview.Count; i++) if (_preview[i] == null) { ok = false; break; }
            if (ok) return;

            ClearPreview();
            for (int i = 0; i < want; i++)
            {
                var go = Instantiate(chipPrefab, spawnParent);
                go.name = PreviewName;
                go.hideFlags = HideFlags.HideAndDontSave;
                if (go.transform is RectTransform rt) _preview.Add(rt);
                else DestroyImmediate(go);
            }
        }

        // Rough deterministic scatter cloud at the blast's reach (Chip Count chips) so you can set count + force +
        // source/target placement. The real explosion is physics-driven, so the exact scatter is felt in Play.
        private void LayoutPreview()
        {
            Vector3 src = LocalOf(source);
            float reach = blastSpeed / Mathf.Max(0.5f, drag);   // ~how far a chip carries before drag kills its speed
            int n = _preview.Count;
            for (int i = 0; i < n; i++)
            {
                if (_preview[i] == null) return;   // cleaned up under us — rebuild next tick
                float a = Frac(Mathf.Sin((i + 1) * 12.9898f) * 43758.5453f) * (Mathf.PI * 2f);
                float r = (0.4f + 0.6f * Frac(Mathf.Sin((i + 1) * 78.233f) * 12345.6789f)) * reach;
                _preview[i].position = spawnParent.TransformPoint(src + new Vector3(Mathf.Cos(a), Mathf.Sin(a) + 0.2f, 0f) * r);
                _preview[i].localScale = _chipBaseScale;
            }
        }

        private static float Frac(float x) => x - Mathf.Floor(x);

        private void ClearPreview()
        {
            _preview.Clear();
            if (spawnParent == null) return;
            for (int i = spawnParent.childCount - 1; i >= 0; i--)
            {
                var ch = spawnParent.GetChild(i);
                if (ch == null || !ch.name.StartsWith(PreviewName)) continue;
                if (Application.isPlaying) Destroy(ch.gameObject); else DestroyImmediate(ch.gameObject);
            }
        }
#endif
    }
}
