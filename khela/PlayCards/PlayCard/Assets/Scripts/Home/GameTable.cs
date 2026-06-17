using UnityEngine;

namespace PlayCard.Home
{
    /// <summary>
    /// A selectable game in the home carousel — selection only, NOT an actual table. Lives on the 3D prop
    /// and links it to its <see cref="GameDefinition"/> config. The carousel centres these and reads
    /// title/availability/routing from the definition; PLAY NOW uses <see cref="tableId"/> to seat directly
    /// (handy for testing the Home→Table loop) or falls back to the game's lobby.
    /// </summary>
    public sealed class GameMode : MonoBehaviour
    {
        [Tooltip("The game this represents — drives the HUD title, availability and routing.")]
        public GameDefinition definition;

        [Tooltip("Optional: seat straight at this table id on Play Now. Empty = auto-match / lobby.")]
        public string tableId = "";

        public string DisplayName => definition ? definition.displayName : name;
        public string Key => definition ? definition.key : string.Empty;
        public bool Available => definition && definition.available;
    }
}
