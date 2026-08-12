using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// A world-space label that pins to a hand's last card (the value badge, the blackjack banner, …). Exposing the
    /// prefab + its placement lets the editor <c>CardAnchorGizmo</c> preview ALL such labels uniformly, so you can
    /// position each one before Play without per-type code.
    /// </summary>
    public interface IAnchorLabel
    {
        GameObject LabelPrefab { get; }

        /// <summary>
        /// The flat-lay rotation this label uses, in the frame it anchors to. Takes <paramref name="tucked"/> for the
        /// same reason <see cref="OffsetFor"/> does: a tucked hand is smaller, sits elsewhere, and can want the label
        /// turned differently from the one authored against a full-size hand.
        /// </summary>
        Vector3 FlatEulerFor(bool tucked);

        /// <summary>
        /// true  → anchor to the HAND CENTRE, hand-aligned: the same spot however many cards the hand holds (for a
        ///         per-hand banner — a result belongs to the hand, not to whichever card happened to arrive last);
        /// false → anchor to the hand's LAST card, tilted with it (for a per-card label like the value badge).
        /// Takes <paramref name="tucked"/> because a label can reasonably do both — pinned to the last card at full
        /// size, and lifted to the hand centre once the hand is tucked and there is no room to hang off its edge.
        /// </summary>
        bool AnchorsAtHandCenter(bool tucked);

        /// <summary>
        /// The offset this label actually uses, in the frame it anchors to. ONE method rather than a raw offset plus
        /// flags, because a label's placement is no longer a single number — a TUCKED (finished, shrunken) split hand
        /// can carry its own hand-authored offset. The gizmo calls this, so a preview cannot drift from the runtime:
        /// there is only one implementation of the rule, on the label itself.
        /// </summary>
        /// <param name="tucked">This hand is drawn tucked (finished split hand).</param>
        /// <param name="cardScale">The card's scale multiplier, for offsets that track a shrunken card's corner.</param>
        Vector3 OffsetFor(bool tucked, float cardScale);
    }
}
