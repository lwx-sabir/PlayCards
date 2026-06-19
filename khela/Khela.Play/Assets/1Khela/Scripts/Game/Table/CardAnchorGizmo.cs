using UnityEngine;

namespace PlayCard.Game.Table
{
    /// <summary>
    /// Editor visual aid: drop on a seat/dealer anchor (an otherwise-invisible empty) to SEE where cards will
    /// land. It draws the fanned card footprint for a hand of <see cref="previewCount"/> cards using the REAL
    /// layout from the parent <see cref="BlackjackTableView"/> (<c>CardLocalPos</c>), so the preview matches
    /// exactly what gets dealt — including the fit-to-width compression for 5–7 card hands.
    ///
    /// Place + orient the anchor until the fan lies flat on the felt and fits without overflowing or
    /// overlapping the neighbours; tune the view's Max Hand Width / Card Step against this. Gizmos are
    /// editor-only — this does nothing in a build (remove it once anchors are placed if you like).
    /// </summary>
    public sealed class CardAnchorGizmo : MonoBehaviour
    {
        [Tooltip("How many cards to preview — a blackjack hand can reach ~7.")]
        [Range(1, 10)] [SerializeField] private int previewCount = 7;

        [Tooltip("Card footprint to draw: X = width, Y = length on the felt. Set ≈ your Card_BJ size.")]
        [SerializeField] private Vector2 cardSize = new Vector2(0.6f, 0.85f);

        [SerializeField] private Color color = new Color(0.2f, 1f, 0.45f, 0.9f);

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            var view = GetComponentInParent<BlackjackTableView>();
            Gizmos.color = color;

            for (int i = 0; i < previewCount; i++)
            {
                Vector3 local = view != null
                    ? view.CardLocalPos(0, i, previewCount)
                    : new Vector3(0.35f * i, 0f, -0.05f * i); // fallback if no view found
                DrawCard(transform.TransformPoint(local));
            }

            // anchor origin marker
            Gizmos.color = new Color(color.r, color.g, color.b, 0.5f);
            Gizmos.DrawSphere(transform.position, cardSize.x * 0.08f);
        }

        // Card rectangle in the anchor's plane (right = width, forward = length) — lies flat if the anchor is.
        private void DrawCard(Vector3 center)
        {
            Vector3 r = transform.right * (cardSize.x * 0.5f);
            Vector3 f = transform.forward * (cardSize.y * 0.5f);
            Vector3 a = center - r - f, b = center + r - f, c = center + r + f, d = center - r + f;
            Gizmos.DrawLine(a, b); Gizmos.DrawLine(b, c); Gizmos.DrawLine(c, d); Gizmos.DrawLine(d, a);
        }
#endif
    }
}
