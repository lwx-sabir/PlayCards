#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace PlayCard.EditorTools
{
    /// <summary>
    /// Dev helper: scale the selected card so it reads correctly against the table, instead of guessing a
    /// number. It measures the table's world width (the "TableRoot" object's combined renderer bounds) and
    /// the card's world width, then uniformly scales the card to a chosen fraction of the table.
    ///
    /// Use: select Card_BJ in the Hierarchy → Tools ▸ Khela ▸ Fit Card To Table → enter the width fraction
    /// (pre-filled with <see cref="DefaultWidthFraction"/>) → Fit. Re-runnable.
    /// </summary>
    public static class CardFitTool
    {
        /// <summary>A card ends up ~this fraction of the table's overall width (casino-exaggerated for readability).
        /// This is the value the dialog pre-fills with.</summary>
        public const float DefaultWidthFraction = 0.12f;

        [MenuItem("Tools/Khela/Fit Card To Table")]
        public static void Open()
        {
            // Validate the selection up-front so the dialog only opens when it can actually do something.
            if (Selection.activeGameObject == null)
            {
                EditorUtility.DisplayDialog("Fit Card To Table",
                    "Select the card (e.g. Card_BJ) in the Hierarchy first.", "OK");
                return;
            }
            CardFitWizard.Open(Selection.activeGameObject);
        }

        /// <summary>Scales <paramref name="card"/> uniformly so its horizontal width becomes
        /// <paramref name="widthFraction"/> of TableRoot's width. Returns the applied factor, or 0 on failure
        /// (reason logged).</summary>
        public static float Fit(GameObject card, float widthFraction)
        {
            if (card == null) { Debug.LogWarning("[CardFit] No card selected."); return 0f; }
            if (widthFraction <= 0f) { Debug.LogWarning("[CardFit] Width fraction must be > 0."); return 0f; }

            var table = GameObject.Find("TableRoot");
            if (table == null) { Debug.LogWarning("[CardFit] No GameObject named 'TableRoot' in the open scene."); return 0f; }

            if (!TryWorldBounds(card, out var cardB)) { Debug.LogWarning("[CardFit] The selected card has no Renderer."); return 0f; }
            if (!TryWorldBounds(table, out var tableB)) { Debug.LogWarning("[CardFit] 'TableRoot' has no Renderers."); return 0f; }

            float cardW = Mathf.Max(cardB.size.x, cardB.size.z);     // card's horizontal extent
            float tableW = Mathf.Max(tableB.size.x, tableB.size.z);  // table's horizontal extent
            if (cardW <= 0.0001f) { Debug.LogWarning("[CardFit] Card width is ~0 — check the mesh."); return 0f; }

            float factor = (tableW * widthFraction) / cardW;
            Undo.RecordObject(card.transform, "Fit Card To Table");
            card.transform.localScale *= factor;

            Debug.Log($"[CardFit] table ≈ {tableW:0.##}u wide → card target {tableW * widthFraction:0.###}u " +
                      $"(fraction {widthFraction:0.###}); scaled ×{factor:0.###} → localScale {card.transform.localScale}.");
            return factor;
        }

        private static bool TryWorldBounds(GameObject go, out Bounds b)
        {
            b = default;
            var rends = go.GetComponentsInChildren<Renderer>();
            if (rends.Length == 0) return false;
            b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            return true;
        }
    }

    /// <summary>Tiny input dialog: asks for the card-width fraction (pre-filled with the code default) and fits.</summary>
    public sealed class CardFitWizard : ScriptableWizard
    {
        [Tooltip("Card width as a fraction of the table's width. Code default = 0.12.")]
        public float cardWidthFraction = CardFitTool.DefaultWidthFraction;

        private GameObject _card;

        public static void Open(GameObject card)
        {
            var w = DisplayWizard<CardFitWizard>("Fit Card To Table", "Fit");
            w._card = card;
            w.cardWidthFraction = CardFitTool.DefaultWidthFraction;   // always start from the code default
            w.helpString = $"Scales '{card.name}' to this fraction of TableRoot's width, then closes.";
        }

        // Called when the "Fit" button is pressed.
        private void OnWizardCreate()
        {
            CardFitTool.Fit(_card, cardWidthFraction);
        }
    }
}
#endif
