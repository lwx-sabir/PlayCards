using System.Threading.Tasks;
using PlayCard.App;
using PlayCard.Game.Net;
using PlayCard.Game.Profile;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Avatar
{
    /// <summary>
    /// First-run onboarding: the player swipes the 3D character <see cref="AvatarCarousel"/> to pick a default MALE or
    /// FEMALE avatar, enters a username (and age), and continues. No customization here — that's a later screen. The
    /// picked avatar decides the player's gender (it's saved with the base). Continue saves the avatar + username to the
    /// server and goes Home. Boot only routes here when the player has no avatar yet.
    ///
    /// SCENE-BOUND: build the Canvas (username field, optional age field, Continue button) in the editor and assign the
    /// refs. Age is a client-side 18+ gate only (no server age field yet) — assign <see cref="ageInput"/> to require it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AvatarOnboardingController : MonoBehaviour
    {
        [Header("Selection")]
        [Tooltip("The 3D character ring (an AvatarCarousel + CarouselController).")]
        [SerializeField] private AvatarCarousel carousel;
        [Tooltip("Optional: shows the centred character's name.")]
        [SerializeField] private TMP_Text nameLabel;

        [Header("Form — assign in the scene")]
        [SerializeField] private TMP_InputField usernameInput;
        [Tooltip("Optional. If assigned, age is required and must be ≥ Min Age (18+ social-casino gate).")]
        [SerializeField] private TMP_InputField ageInput;
        [SerializeField] private Button continueButton;
        [Tooltip("Optional feedback line (validation / save errors).")]
        [SerializeField] private TMP_Text statusLabel;

        [Header("Rules")]
        [SerializeField] private int minAge = 18;
        [SerializeField] private int minUsernameLen = 3;
        [SerializeField] private int maxUsernameLen = 16;

        private AvatarCarouselItem _selected;
        private bool _busy;

        private void Start()
        {
            if (carousel == null || continueButton == null)
            {
                Debug.LogError("[AvatarOnboarding] Assign carousel + continueButton.");
                return;
            }
            carousel.OnSelected += HandleSelected;
            HandleSelected(carousel.Selected);        // in case the ring already reported one
            continueButton.onClick.AddListener(OnContinue);
            RefreshContinue();
        }

        private void OnDestroy()
        {
            if (carousel != null) carousel.OnSelected -= HandleSelected;
        }

        private void HandleSelected(AvatarCarouselItem item)
        {
            _selected = item;
            if (nameLabel != null) nameLabel.text = item != null ? item.DisplayNameText : "";
            RefreshContinue();
        }

        private void RefreshContinue()
        {
            if (continueButton != null) continueButton.interactable = !_busy && _selected != null;
        }

        private async void OnContinue()
        {
            if (_busy) return;
            if (_selected == null) { Status("Swipe to pick a character."); return; }

            string username = (usernameInput != null ? usernameInput.text : "").Trim();
            if (username.Length < minUsernameLen || username.Length > maxUsernameLen)
            {
                Status($"Username must be {minUsernameLen}–{maxUsernameLen} characters.");
                return;
            }

            if (ageInput != null)   // age field present → required + 18+ gated
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

            // A base pick is just gender + base id (no customization); the server rebuilds the exact base from it.
            var avatar = new AvatarData { Gender = _selected.Gender.ToString(), BaseId = _selected.BaseId };
            bool avatarOk = await AvatarService.Instance.SaveAsync(avatar);
            var (nameOk, nameErr) = await ProfileCrud.SetDisplayNameAsync(username);

            if (avatarOk && nameOk)
            {
                SceneNavigator.GoToHome();
                return;   // scene unloads; leave UI disabled
            }

            Status(!avatarOk ? "Couldn't save your avatar — try again." : (nameErr ?? "Couldn't save your name — try again."));
            _busy = false;
            SetInteractable(true);
        }

        private void SetInteractable(bool on)
        {
            RefreshContinue();
            if (usernameInput != null) usernameInput.interactable = on;
            if (ageInput != null) ageInput.interactable = on;
        }

        private void Status(string msg)
        {
            if (statusLabel != null) statusLabel.text = msg;
        }
    }
}
