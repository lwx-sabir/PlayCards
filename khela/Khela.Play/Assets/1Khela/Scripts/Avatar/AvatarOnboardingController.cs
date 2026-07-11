using PlayCard.App;
using PlayCard.Game.Net;
using PlayCard.Game.Profile;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Avatar
{
    /// <summary>
    /// First-run onboarding logic over the 3D <see cref="AvatarCarousel"/>: the player swipes to a character (the
    /// centred one IS the selection — its gender travels with it), types a username, and hits Continue ("Play as
    /// guest"). Continue saves the avatar + display name to the server and goes Home. Boot only routes here when the
    /// player has no saved avatar yet.
    ///
    /// Selection-only by design: the baked carousel characters are static previews, so the saved avatar is just
    /// {gender, baseId} — the base's default look. Customization (shapes/outfits via AvatarCreator) is a later,
    /// separate screen; the server contract already carries it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AvatarOnboardingController : MonoBehaviour
    {
        [Header("Scene refs")]
        [SerializeField] private AvatarCarousel carousel;
        [SerializeField] private TMP_InputField usernameInput;
        [Tooltip("Optional. If assigned, age is required and must be ≥ Min Age (18+ social-casino gate). Client-side only.")]
        [SerializeField] private TMP_InputField ageInput;
        [Tooltip("The Continue / 'Play as guest' button.")]
        [SerializeField] private Button continueButton;
        [Tooltip("Optional feedback line (validation / save errors).")]
        [SerializeField] private TMP_Text statusLabel;
        [Tooltip("Optional label showing the centred character's name.")]
        [SerializeField] private TMP_Text selectedNameLabel;

        [Header("Rules")]
        [SerializeField] private int minAge = 18;
        [SerializeField] private int minUsernameLen = 3;
        [SerializeField] private int maxUsernameLen = 16;

        private bool _busy;

        private void Start()
        {
            if (carousel == null || continueButton == null)
            {
                Debug.LogError("[AvatarOnboarding] Assign carousel + continueButton.");
                return;
            }

            continueButton.onClick.AddListener(OnContinue);
            carousel.OnSelected += HandleSelected;
            HandleSelected(carousel.Selected);   // paint the initial selection if the carousel beat us to it
        }

        private void OnDestroy()
        {
            if (carousel != null) carousel.OnSelected -= HandleSelected;
        }

        private void HandleSelected(AvatarCarouselItem item)
        {
            if (selectedNameLabel != null)
                selectedNameLabel.text = item != null ? item.DisplayNameText : "";
            Status("");
        }

        private async void OnContinue()
        {
            if (_busy) return;

            var sel = carousel.Selected;
            if (sel == null || string.IsNullOrEmpty(sel.BaseId)) { Status("Pick a character first."); return; }

            string username = (usernameInput != null ? usernameInput.text : "").Trim();
            if (username.Length < minUsernameLen || username.Length > maxUsernameLen)
            {
                Status($"Name must be {minUsernameLen}–{maxUsernameLen} characters.");
                return;
            }

            if (ageInput != null)   // age field present → required + 18+ gated (client-side only; no server field yet)
            {
                if (!int.TryParse((ageInput.text ?? "").Trim(), out int age) || age < minAge)
                {
                    Status($"You must be {minAge} or older to play.");
                    return;
                }
            }

            _busy = true;
            SetInteractable(false);
            Status("Saving…");

            // Selection-only avatar: the chosen base with its default look. The server sanitizes + stores; its echo
            // becomes AvatarService.Mine, so the Boot router sends this player straight Home from now on.
            var avatar = new AvatarData { Gender = sel.Gender.ToString(), BaseId = sel.BaseId };
            bool avatarOk = await AvatarService.Instance.SaveAsync(avatar);

            // DisplayName is unique + server-moderated — a taken name comes back as an error to show the player.
            var (nameOk, nameErr) = await ProfileCrud.SetDisplayNameAsync(username);

            if (avatarOk && nameOk)
            {
                SceneNavigator.GoToHome();
                return;   // scene unloads; leave the UI disabled
            }

            Status(!avatarOk ? "Couldn't save your avatar — try again."
                             : (string.IsNullOrEmpty(nameErr) ? "Couldn't save your name — try again." : nameErr));
            _busy = false;
            SetInteractable(true);
        }

        private void SetInteractable(bool on)
        {
            if (continueButton != null) continueButton.interactable = on;
            if (usernameInput != null) usernameInput.interactable = on;
            if (ageInput != null) ageInput.interactable = on;
        }

        private void Status(string msg)
        {
            if (statusLabel != null) statusLabel.text = msg;
        }
    }
}
