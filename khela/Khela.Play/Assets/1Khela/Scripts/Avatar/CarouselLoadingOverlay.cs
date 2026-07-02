using UnityEngine;

namespace PlayCard.Avatar
{
    /// <summary>
    /// Hides the runtime-merge glitch (lag + darkening) behind a "Loading…" panel: shows <see cref="overlay"/> until the
    /// <see cref="AvatarCarousel"/> reports every character is merged/ready, then hides it. With the carousel preloading
    /// all characters at start, the player only ever sees the finished ring.
    ///
    /// NOTE: this masks the lag/darken only. It does NOT protect against a frozen merge (that freezes everything). Bake
    /// the characters (AvatarPrefabBaker) for a ship-safe, glitch-free result — then the overlay just flashes off instantly.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CarouselLoadingOverlay : MonoBehaviour
    {
        [SerializeField] private AvatarCarousel carousel;
        [Tooltip("Full-screen panel shown while the characters merge (e.g. a dim panel + spinner/'Loading…').")]
        [SerializeField] private GameObject overlay;

        private void Start()
        {
            if (overlay == null) return;

            // Already ready (all baked / merge finished before us) → don't show it at all.
            if (carousel == null || carousel.AllPreloaded)
            {
                overlay.SetActive(false);
                return;
            }

            overlay.SetActive(true);
            carousel.OnAllPreloaded += Hide;
        }

        private void OnDestroy()
        {
            if (carousel != null) carousel.OnAllPreloaded -= Hide;
        }

        private void Hide()
        {
            if (overlay != null) overlay.SetActive(false);
        }
    }
}
