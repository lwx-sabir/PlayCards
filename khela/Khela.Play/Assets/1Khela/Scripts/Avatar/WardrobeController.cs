using PlayCard.App;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Avatar
{
    /// <summary>
    /// Wardrobe session controller, bound to <see cref="AvatarCreator"/>. Opens on the player's SAVED avatar (loads the
    /// full look — shapes + outfits + colours — onto ONE live actor) and owns the Save / Back / status / busy-overlay
    /// flow. The actual editing UI (shape sliders, outfit grid, colour palettes) is built by the tab-driven components
    /// (WardrobeTabBar / WardrobeShapeSliders / WardrobeItemGrid / WardrobePaletteRow) reading the same creator — this
    /// class no longer builds any tiles itself. Save persists the full <see cref="AvatarData"/> to the server and
    /// returns Home; Back discards (edits were on the in-memory actor only).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WardrobeController : MonoBehaviour
    {
        [Header("Engine")]
        [SerializeField] private AvatarCreator creator;

        [Header("Actions")]
        [SerializeField] private Button saveButton, backButton;
        [SerializeField] private TMP_Text statusLabel;
        [Tooltip("Shown while the creator is loading (bound to creator.IsBusy).")]
        [SerializeField] private GameObject busyOverlay;

        [Header("Fallback (player with no saved avatar)")]
        [SerializeField] private string fallbackBaseId = "Base/1DefaultMale";

        private bool _busy;

        private async void Start()
        {
            if (creator == null) { Debug.LogError("[Wardrobe] Assign the AvatarCreator."); return; }

            if (saveButton != null) saveButton.onClick.AddListener(OnSave);
            if (backButton != null) backButton.onClick.AddListener(() => SceneNavigator.GoToHome());

            var fbGender = creator.Genders().Count > 0 ? creator.Genders()[0] : AvatarConfig.Gender.Male;
            SetBusy(true);
            await creator.LoadSavedOrBaseAsync(fbGender, fallbackBaseId);   // the tab UI reads the loaded actor
            SetBusy(false);
        }

        // ---- save / busy ----

        private async void OnSave()
        {
            Debug.Log($"[Wardrobe] Save clicked — creator={(creator != null)}, busy={_busy}");
            if (_busy || creator == null) return;
            if (creator.SaveWouldClobber)   // stored avatar didn't load — never overwrite it with the fallback base
            {
                Status("Couldn't load your saved avatar. Go back and reopen — not overwriting it.");
                return;
            }
            _busy = true;
            SetButtons(false);
            Status("Saving…");
            bool ok = await creator.SaveAsync();
            if (ok) { SceneNavigator.GoToHome(); return; }   // Home's stage re-renders via AvatarService.MineChanged
            Status("Couldn't save — try again.");
            _busy = false;
            SetButtons(true);
        }

        private void SetBusy(bool on) { if (busyOverlay != null) busyOverlay.SetActive(on); }
        private void SetButtons(bool on)
        {
            if (saveButton != null) saveButton.interactable = on;
            if (backButton != null) backButton.interactable = on;
        }
        private void Status(string msg) { if (statusLabel != null) statusLabel.text = msg; }
    }
}
