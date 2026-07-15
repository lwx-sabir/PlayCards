using System.Collections;
using Invector.vCharacterController;
using UnityEngine;

namespace PlayCard.World
{
    /// <summary>
    /// Configures the spawned world player to the scene's declared <see cref="PlayerMode"/> (see
    /// <see cref="WorldModeContext"/>). One player prefab serves both modes — the scene decides:
    /// <list type="bullet">
    /// <item><b>Social</b> — melee live (encounters can happen anywhere), guns locked and Invector's aim canvas
    /// hidden (<c>SetLockShooterInput</c> does that internally), hunting-only UI off. Bring your own social HUD.</item>
    /// <item><b>Hunting</b> — melee + guns both live, hunting-only UI on.</item>
    /// </list>
    /// Put this on the Invector controller root (the object that holds <see cref="vShooterMeleeInput"/>, next to
    /// the WorldAvatarLoader). Author the <c>huntingOnly</c> objects DISABLED by default so Social never flashes them.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerModeController : MonoBehaviour
    {
        [Tooltip("Fallback when the scene has no WorldModeContext (e.g. a mode baked into a prefab variant).")]
        [SerializeField] private PlayerMode defaultMode = PlayerMode.Social;

        [Tooltip("Invector shooter/melee input on this player. Auto-found on this object/children if left empty.")]
        [SerializeField] private vShooterMeleeInput shooterInput;

        [Tooltip("Objects active ONLY in Hunting (your hunting HUD, reticle, weapon holders). Author them DISABLED by default.")]
        [SerializeField] private GameObject[] huntingOnly;

        [Tooltip("Objects active ONLY in Social (your social HUD). Usually left empty.")]
        [SerializeField] private GameObject[] socialOnly;

        /// <summary>The mode currently applied.</summary>
        public PlayerMode Current { get; private set; }

        private void Reset() => CacheInput();
        private void Awake() => CacheInput();

        private void CacheInput()
        {
            if (shooterInput != null) return;
            shooterInput = GetComponent<vShooterMeleeInput>();
            if (shooterInput == null) shooterInput = GetComponentInChildren<vShooterMeleeInput>(true);
        }

        // Deferred one frame so this runs AFTER Invector's and WorldModeContext's own init — otherwise Invector's
        // Start could clobber the lock we set. One frame is imperceptible (huntingOnly is authored off by default).
        private IEnumerator Start()
        {
            yield return null;
            var ctx = FindAnyObjectByType<WorldModeContext>();
            Apply(ctx != null ? ctx.Mode : defaultMode);
        }

        /// <summary>Switch mode at runtime (e.g. entering / leaving a hunt zone) and reconfigure.</summary>
        public void SetMode(PlayerMode mode) => Apply(mode);

        private void Apply(PlayerMode mode)
        {
            Current = mode;
            bool hunting = mode == PlayerMode.Hunting;

            if (shooterInput != null)
            {
                // Guns gate on mode; locking the shooter also hides Invector's aim canvas/scope internally.
                // Melee is ALWAYS live (mad-animal encounters can happen in any scene) — never lock it.
                shooterInput.SetLockShooterInput(!hunting);
                shooterInput.SetLockMeleeInput(false);
            }
            else
            {
                Debug.LogWarning("[PlayerModeController] no vShooterMeleeInput found — input locks skipped (not a shooter controller?).");
            }

            if (huntingOnly != null) foreach (var go in huntingOnly) if (go != null) go.SetActive(hunting);
            if (socialOnly != null) foreach (var go in socialOnly) if (go != null) go.SetActive(!hunting);
        }
    }
}
