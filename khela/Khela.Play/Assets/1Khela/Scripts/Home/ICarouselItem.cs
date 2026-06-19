using UnityEngine;

namespace PlayCard.Home
{
    /// <summary>
    /// An item the <see cref="CarouselController"/> can position and focus. Implemented by the home game
    /// tiles (<see cref="GameMode"/>) and the lobby's table cards, so both reuse the same ring + drag logic.
    /// </summary>
    public interface ICarouselItem
    {
        /// <summary>The item's transform (the carousel positions/scales this).</summary>
        Transform Transform { get; }

        /// <summary>Carousel calls this on every selection change — only the centred item shows its buttons.</summary>
        void SetSelected(bool selected);
    }
}
