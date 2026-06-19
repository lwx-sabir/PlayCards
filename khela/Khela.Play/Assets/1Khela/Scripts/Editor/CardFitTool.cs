#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace PlayCard.EditorTools
{
    /// <summary>
    /// Dev helper: scale the selected card so it reads correctly against the table, instead of guessing a
    /// number. It measures the table's world width (the "TableRoot" object's combined renderer bounds) and
    /// the card's world width, then uniformly scales the card to <see cref="CardWidthFraction"/> of the
    /// table. Re-runnable; bump the fraction if you want bigger/smaller cards.
    ///
    /// Use: select Card_BJ in the Hierarchy → Khela ▸ Fit Selected Card To Table.
    /// </summary>
    public static class CardFitTool
    {
        // A card ends up ~this fraction of the table's overall width (casino-exaggerated for readability).
        private const float CardWidthFraction = 0.12f;

        [MenuItem("Tools/Khela/Fit Card To Table")]
        public static void Fit()
        {
            var card = Selection.activeGameObject;
            if (card == null) { Debug.LogWarning("[CardFit] Select the card (e.g. Card_BJ) in the Hierarchy first."); return; }

            var table = GameObject.Find("TableRoot");
            if (table == null) { Debug.LogWarning("[CardFit] No GameObject named 'TableRoot' in the open scene."); return; }

            if (!TryWorldBounds(card, out var cardB)) { Debug.LogWarning("[CardFit] The selected card has no Renderer."); return; }
            if (!TryWorldBounds(table, out var tableB)) { Debug.LogWarning("[CardFit] 'TableRoot' has no Renderers."); return; }

            float cardW = Mathf.Max(cardB.size.x, cardB.size.z);     // card's horizontal extent
            float tableW = Mathf.Max(tableB.size.x, tableB.size.z);  // table's horizontal extent
            if (cardW <= 0.0001f) { Debug.LogWarning("[CardFit] Card width is ~0 — check the mesh."); return; }

            float factor = (tableW * CardWidthFraction) / cardW;
            Undo.RecordObject(card.transform, "Fit Card To Table");
            card.transform.localScale *= factor;

            Debug.Log($"[CardFit] table ≈ {tableW:0.##}u wide → card target {tableW * CardWidthFraction:0.###}u; " +
                      $"scaled ×{factor:0.###} → localScale {card.transform.localScale}. " +
                      "Re-run after editing CardWidthFraction if you want it bigger/smaller.");
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
}
#endif
