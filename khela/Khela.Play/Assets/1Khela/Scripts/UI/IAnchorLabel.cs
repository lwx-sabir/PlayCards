using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// A world-space label that pins to a hand's last card (the value badge, the blackjack banner, …). Exposing the
    /// prefab + its corner offset + flat-lay rotation lets the editor <c>CardAnchorGizmo</c> preview ALL such labels
    /// uniformly, so you can position each one before Play without per-type code.
    /// </summary>
    public interface IAnchorLabel
    {
        GameObject LabelPrefab { get; }
        Vector3 CornerOffset { get; }
        Vector3 LabelFlatEuler { get; }
        // true  → the offset scales with the (split-)shrunk card, so it stays glued to the card corner;
        // false → fixed offset (a banner that shouldn't drift when split-cards shrink).
        bool ScaleOffsetWithCard { get; }
        // true  → anchor to the HAND CENTRE, hand-aligned (consistent regardless of card count — for a per-hand banner);
        // false → anchor to the hand's LAST card, tilted with it (for a per-card label like the value badge).
        bool AnchorAtHandCenter { get; }
    }
}
