using System.Collections.Generic;
using PlayCard.Game.Table;
using UnityEngine;

namespace PlayCard.Game.Cards
{
    /// <summary>
    /// Replaces a baked card-stack model (the shoe, the discard tray) with a pile of REAL cards wearing the table's
    /// current <see cref="CardSkin"/>, so the felt's scenery matches the cards actually in play instead of whatever
    /// backs were painted into the FBX.
    ///
    /// Point <see cref="stackModel"/> at the existing stack object and press <b>Fit To Model</b>. It MEASURES that
    /// model — footprint, height, orientation — and works out the card scale and the stacking step from it, then
    /// builds the pile in its place and hides the original renderer. Nothing about the size is assumed: the numbers
    /// come out of the mesh you already have, and every one of them stays editable afterwards.
    ///
    /// Purely decorative. It is NOT the shoe — the server owns that, and how many cards remain is not something the
    /// felt may imply — so <see cref="count"/> is how tall this should LOOK.
    ///
    /// Setup: add it to the stack object itself, press Fit To Model, then Rebuild. Live in the editor.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class CardStackProp : MonoBehaviour
    {
        [Header("1 — What to replace")]
        [Tooltip("The existing baked stack. EMPTY = the renderer on this object. Measured by Fit To Model, and its " +
                 "renderer is switched off while the generated pile stands in for it (never deleted, so removing " +
                 "this component restores exactly what was there).")]
        [SerializeField] private Renderer stackModel;

        [Tooltip("Hide the model's renderer once the pile is built. Turn OFF to see both at once while lining them up.")]
        [SerializeField] private bool hideModel = true;

        [Header("2 — What one card is")]
        [Tooltip("The card prefab the table deals (BlackjackTableView ▸ Card Prefab). Its own scale and rotation are " +
                 "preserved; Card Scale below multiplies them.")]
        [SerializeField] private GameObject cardPrefab;

        [Tooltip("Skin for the backs. EMPTY = the table's, so the pile follows a skin change on its own.")]
        [SerializeField] private CardSkin skinOverride;

        [SerializeField] private BlackjackTableView view;

        [Header("3 — Fit (Fit To Model fills these in; tune freely afterwards)")]
        [Tooltip("Uniform multiplier on the card prefab's own scale. 1 = exactly the size the table deals at.")]
        [SerializeField] private float cardScale = 1f;

        [Tooltip("Extra rotation for the whole pile, on top of the prefab's. Fit To Model sets the yaw to match the " +
                 "model it measured.")]
        [SerializeField] private Vector3 pileEuler = Vector3.zero;

        [Tooltip("Local offset for the whole pile — where the bottom card sits.")]
        [SerializeField] private Vector3 pileOffset = Vector3.zero;

        [Header("4 — Shape")]
        [Tooltip("How many cards. Every one is a renderer, so this is the cost knob for the whole prop.")]
        [SerializeField, Range(1, 200)] private int count = 24;

        [Tooltip("Height gained per card. Fit To Model divides the measured model height by Count, so the generated " +
                 "pile ends up the same height as the one it replaces.")]
        [SerializeField] private float cardStep = 0.0016f;

        [Tooltip("Sideways scatter per card, so the pile does not read as one extruded block.")]
        [SerializeField] private float positionJitter = 0.0012f;

        [Tooltip("Yaw scatter per card, in degrees.")]
        [SerializeField] private float rotationJitter = 1.2f;

        [Tooltip("Fixed seed, so the scatter is identical in the editor and at runtime.")]
        [SerializeField] private int seed = 12345;

        [Header("5 — Runtime")]
        [Tooltip("Rebuild on Awake, so a scene saved against one skin comes up wearing the table's actual one.")]
        [SerializeField] private bool rebuildOnAwake = true;

        [Tooltip("Mark the cards static so they batch — they never move.")]
        [SerializeField] private bool markStatic = true;

        private const string GeneratedName = "GenCard";
        private readonly List<GameObject> _built = new List<GameObject>();

        private void Awake()
        {
            if (Application.isPlaying && rebuildOnAwake) Rebuild();
        }

        /// <summary>
        /// MEASURE the model and derive the fit — card scale, stacking step and yaw — instead of anyone guessing them.
        /// Sets the fields; it does not build. Press Rebuild after.
        /// </summary>
        [ContextMenu("Fit To Model")]
        public void FitToModel()
        {
            var model = ResolveModel();
            if (model == null) { Debug.LogWarning($"[{nameof(CardStackProp)}] no stack model to measure.", this); return; }
            if (cardPrefab == null) { Debug.LogWarning($"[{nameof(CardStackProp)}] assign a card prefab first.", this); return; }

            Vector3 modelSize = WorldSize(model.transform, model.gameObject);
            Vector3 cardSize = WorldSize(cardPrefab.transform, cardPrefab);
            if (cardSize.x <= 0f || cardSize.z <= 0f) { Debug.LogWarning($"[{nameof(CardStackProp)}] card prefab has no measurable mesh.", this); return; }

            // A card lies flat, so its FOOTPRINT is the two largest axes and its thickness is the smallest. Comparing
            // footprint-to-footprint rather than axis-to-axis is what makes this survive a prefab that was authored
            // lying on a different plane than the model.
            float modelFoot = Mathf.Max(Footprint(modelSize), 0.0001f);
            float cardFoot = Mathf.Max(Footprint(cardSize), 0.0001f);
            cardScale = modelFoot / cardFoot;

            // Same height as the pile it replaces, spread over however many cards were asked for.
            float modelHeight = Mathf.Max(modelSize.y, 0.0001f);
            cardStep = modelHeight / Mathf.Max(1, count);

            // Sit where the model sits, and face the way it faces.
            pileOffset = transform.InverseTransformPoint(model.bounds.center);
            pileOffset.y -= modelHeight * 0.5f;                       // bounds centre → bottom of the pile
            pileEuler = new Vector3(0f, model.transform.eulerAngles.y - transform.eulerAngles.y, 0f);

            Debug.Log($"[{nameof(CardStackProp)}] fitted to '{model.name}': footprint {modelFoot:0.####} vs card " +
                      $"{cardFoot:0.####} → scale {cardScale:0.###}, height {modelHeight:0.####} over {count} cards " +
                      $"→ step {cardStep:0.#####}.", this);
        }

        [ContextMenu("Rebuild")]
        public void Rebuild()
        {
            ClearGenerated();
            if (cardPrefab == null) return;

            var skin = ResolveSkin();
            var model = ResolveModel();

            var previousRandom = Random.state;
            Random.InitState(seed);

            var pileRot = Quaternion.Euler(pileEuler);
            for (int i = 0; i < count; i++)
            {
                var card = Instantiate(cardPrefab, transform);
                card.name = $"{GeneratedName}_{i:000}";
                card.hideFlags = HideFlags.DontSave;

                var t = card.transform;
                var jitter = Random.insideUnitCircle * positionJitter;
                var up = new Vector3(jitter.x, cardStep * i, jitter.y);

                // The prefab's OWN transform is the baseline and is never discarded — its scale is the card's size and
                // its rotation is what makes it lie flat. Everything here is applied on top of that.
                t.localPosition = pileOffset + pileRot * up;
                t.localRotation = pileRot * Quaternion.Euler(0f, Random.Range(-rotationJitter, rotationJitter), 0f) * t.localRotation;
                t.localScale *= cardScale;

                var visual = card.GetComponent<CardVisual>() ?? card.GetComponentInChildren<CardVisual>();
                if (visual != null)
                {
                    if (skin != null) visual.Skin = skin;
                    visual.SetCard(new CardId(CardRank.Two, CardSuit.Spades, faceUp: false));
                }

                if (markStatic) card.isStatic = true;
                _built.Add(card);
            }

            Random.state = previousRandom;

            _appliedSkin = skin;   // built with this one — the watcher must not immediately re-apply it

            if (model != null) model.enabled = !hideModel;   // disabled, never destroyed — removing this restores it
        }

        private CardSkin _appliedSkin;

        // Watches for a skin swap. Cards are a cosmetic the player will be able to choose, so the skin can change at
        // any time and from anywhere — including while this pile is sitting on the felt. A reference compare per frame
        // is cheaper than an event contract every future caller has to remember to fire, and it means the pile cannot
        // be left wearing the previous skin by a code path nobody thought to hook.
        private void Update()
        {
            var skin = ResolveSkin();
            if (skin != _appliedSkin) ApplySkin();
        }

        /// <summary>
        /// Repaint the existing cards for the current skin. NOT a rebuild — a skin change is a texture change, so the
        /// pile keeps every card it already has and only the backs are re-pointed. Nothing is instantiated.
        /// </summary>
        [ContextMenu("Apply Skin")]
        public void ApplySkin()
        {
            var skin = ResolveSkin();
            _appliedSkin = skin;
            if (skin == null) return;

            // Walk the children, not the built list: that list is empty after a domain reload or on entering play,
            // and a pile that silently stops following the skin then is worse than one that never did.
            for (int i = 0; i < transform.childCount; i++)
            {
                var child = transform.GetChild(i);
                if (child == null || !child.name.StartsWith(GeneratedName)) continue;
                var visual = child.GetComponent<CardVisual>() ?? child.GetComponentInChildren<CardVisual>();
                if (visual != null) visual.Skin = skin;   // CardVisual re-renders on assignment
            }
        }

        [ContextMenu("Clear")]
        public void ClearGenerated()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i);
                if (child == null || !child.name.StartsWith(GeneratedName)) continue;
                if (Application.isPlaying) Destroy(child.gameObject); else DestroyImmediate(child.gameObject);
            }
            _built.Clear();

            var model = ResolveModel();
            if (model != null) model.enabled = true;   // put the original back the moment the pile goes
        }

        /// <summary>World-space size of an object's mesh, scale included. Vector3.zero when it has no mesh.</summary>
        private static Vector3 WorldSize(Transform t, GameObject go)
        {
            var mf = go.GetComponent<MeshFilter>() ?? go.GetComponentInChildren<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return Vector3.zero;
            return Vector3.Scale(mf.sharedMesh.bounds.size, t.lossyScale);
        }

        /// <summary>The larger of a flat object's two broad axes — its footprint, ignoring thickness.</summary>
        private static float Footprint(Vector3 size)
        {
            float a = Mathf.Max(size.x, size.y);
            return Mathf.Max(a, size.z);
        }

        private Renderer ResolveModel()
            => stackModel != null ? stackModel : GetComponent<Renderer>();

        private CardSkin ResolveSkin()
        {
            if (skinOverride != null) return skinOverride;
            if (view == null) view = FindAnyObjectByType<BlackjackTableView>(FindObjectsInactive.Include);
            return view != null ? view.PreviewSkin : null;   // the view's existing accessor — same field the cards use
        }
    }
}
