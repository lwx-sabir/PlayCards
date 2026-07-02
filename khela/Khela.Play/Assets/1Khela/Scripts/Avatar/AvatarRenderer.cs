using System;
using System.Threading.Tasks;
using Bozo.ModularCharacters;
using PlayCard.Game.Net;
using UnityEngine;

namespace PlayCard.Avatar
{
    /// <summary>
    /// Renders a player's avatar onto a BoZo actor. Attach next to (or point it at) an <see cref="OutfitSystem"/> — the
    /// profile stage, a table seat, or any place another player's body appears — and call <see cref="RenderMine"/>,
    /// <see cref="RenderUser"/>, or <see cref="RenderAsync"/>. Read-only display; the editable creator is
    /// <see cref="AvatarCreator"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AvatarRenderer : MonoBehaviour
    {
        [Tooltip("The BoZo actor to dress. If empty, searched on this GameObject / its children at Awake.")]
        [SerializeField] private OutfitSystem outfitSystem;

        [Tooltip("Fallback base when a player has no avatar (or its base is missing).")]
        [SerializeField] private string defaultBaseId = "Base/1DefaultMale";

        public OutfitSystem Target => outfitSystem;
        public bool IsBusy { get; private set; }

        private void Awake()
        {
            if (outfitSystem == null) outfitSystem = GetComponentInChildren<OutfitSystem>(true);
        }

        /// <summary>Render the signed-in player's own avatar (fetches once if not cached).</summary>
        public async void RenderMine()
        {
            var a = AvatarService.Instance.Mine ?? await AvatarService.Instance.LoadMineAsync();
            await RenderAsync(a);
        }

        /// <summary>Render another player's avatar (for their seat). Falls back to the default base if they have none.</summary>
        public async void RenderUser(string userId)
        {
            var a = await AvatarService.Instance.LoadUserAsync(userId);
            await RenderAsync(a);
        }

        /// <summary>Core: build base+overlay and load it onto the rig. Awaitable so callers can sequence (e.g. seat spawn).</summary>
        public async Task RenderAsync(AvatarData avatar)
        {
            if (outfitSystem == null)
            {
                Debug.LogWarning("[AvatarRenderer] no OutfitSystem assigned/found.");
                return;
            }

            var data = avatar != null ? AvatarService.BuildCharacter(avatar) : null;
            if (data == null) data = AvatarService.LoadBaseData(defaultBaseId);   // no avatar / bad base → default body
            if (data == null) return;

            IsBusy = true;
            try
            {
                await BMAC_SaveSystem.LoadCharacter(outfitSystem, data);
            }
            catch (Exception e)
            {
                Debug.LogError($"[AvatarRenderer] load failed: {e.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
