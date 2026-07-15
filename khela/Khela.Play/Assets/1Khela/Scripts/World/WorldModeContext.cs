using UnityEngine;

namespace PlayCard.World
{
    /// <summary>The two ways the player exists in a 3D world. Melee is live in BOTH (mad-animal encounters can
    /// happen anywhere); guns + hunting UI are Hunting-only.</summary>
    public enum PlayerMode
    {
        /// <summary>Social hub — melee live, guns locked + Invector aim UI hidden, hunting-only UI off.</summary>
        Social,
        /// <summary>Hunting scene — melee + guns both live, hunting-only UI on.</summary>
        Hunting,
    }

    /// <summary>
    /// Scene-level declaration of the world mode. Drop ONE on a scene object (e.g. the GameController) and pick
    /// Social or Hunting; the spawned player's <see cref="PlayerModeController"/> reads it on spawn and configures
    /// itself. Absent from a scene → the player falls back to its own serialized default (so a mode can also be
    /// baked per prefab-variant instead of declared per-scene).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WorldModeContext : MonoBehaviour
    {
        [Tooltip("Social = melee live, guns + shooter/aim UI off. Hunting = melee + guns live + hunting UI on.")]
        [SerializeField] private PlayerMode mode = PlayerMode.Social;

        /// <summary>The mode this scene declares.</summary>
        public PlayerMode Mode => mode;
    }
}
