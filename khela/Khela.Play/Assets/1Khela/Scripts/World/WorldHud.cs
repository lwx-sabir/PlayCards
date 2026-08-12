using PlayCard.App;
using PlayCard.Game.Net;      // WalletBalances
using PlayCard.Game.Wallet;   // WalletManager
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.World
{
    /// <summary>
    /// The shared heads-up display for the 3D virtual-world rooms (DiveBar, DanceClub, NightClub, RooftopBar …).
    ///
    /// This lives on ONE prefab that every room drops in, so it must never depend on anything a particular room
    /// provides — no scene lookups, no hard references out. Everything it needs comes from the systems that already
    /// survive a scene load (<see cref="WalletManager"/>, <see cref="SceneNavigator"/>), and every field below is
    /// optional: a room that hides part of the HUD simply leaves that reference empty rather than needing a
    /// different prefab or a null-check at the call site.
    ///
    /// Deliberately NOT the hunting HUD. That one shows a different set of things (ammo, target, timer) and will be
    /// its own prefab and its own controller — keeping them separate is why this one can stay this small.
    ///
    /// Balances are display-only and server-authoritative: this reads the wallet, it never writes to it.
    /// </summary>
    public sealed class WorldHud : MonoBehaviour
    {
        [Header("Navigation")]
        [Tooltip("Leaves the world and returns to Home. Optional — a room with no exit button can leave this empty.")]
        [SerializeField] private Button homeButton;

        [Header("Balances (optional — leave any of them empty to hide that currency)")]
        [SerializeField] private TMP_Text chipsLabel;
        [SerializeField] private TMP_Text kashLabel;
        [SerializeField] private TMP_Text gemsLabel;

        [Tooltip("Numeric format for the balance labels, e.g. \"#,0\". Compact mode overrides this.")]
        [SerializeField] private string balanceFormat = "#,0";
        [Tooltip("Write balances the way the chips do — 1.2M, 250K. Better for a narrow world HUD than seven digits.")]
        [SerializeField] private bool compactBalances = true;

        [Header("Panels (optional — for rooms that show a reduced HUD)")]
        [Tooltip("The whole play HUD. Toggled by ShowHud() so a cutscene or a menu can hide it without disabling " +
                 "this component, which would stop it tracking the wallet.")]
        [SerializeField] private GameObject playHudRoot;

        private void Reset()
        {
            // Best-effort auto-wire so dropping the prefab in gives something that works before it is configured.
            if (homeButton == null) homeButton = GetComponentInChildren<Button>(true);
        }

        private void Awake()
        {
            if (homeButton != null) homeButton.onClick.AddListener(GoHome);
            else Debug.LogWarning("[WorldHud] Home Button is not assigned — the button cannot do anything. " +
                                  "Drag Button_Home into the Home Button field on this component.");
        }

        private void OnEnable()
        {
            var wallet = WalletManager.Instance;
            if (wallet == null) return;

            wallet.OnBalancesChanged += ShowBalances;
            if (wallet.Balances != null) ShowBalances(wallet.Balances);   // paint immediately, don't wait for a fetch
            _ = wallet.RefreshAsync();
        }

        private void OnDisable()
        {
            if (WalletManager.Instance != null) WalletManager.Instance.OnBalancesChanged -= ShowBalances;
        }

        private void OnDestroy()
        {
            if (homeButton != null) homeButton.onClick.RemoveListener(GoHome);
        }

        // ---- navigation ----------------------------------------------------------------------------------------

        /// <summary>Leave the world for Home. Public so a room can also bind it to a door trigger or a menu item.</summary>
        public void GoHome()
        {
            // Invector locks the cursor to the centre of the screen for camera look, and a locked cursor cannot
            // land on a button in the corner — so release it on the way out, or Home would be unreachable the next
            // time this HUD is shown.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            Debug.Log("[WorldHud] Home pressed → loading Home scene.");
            SceneNavigator.GoToHome();
        }

        // ---- HUD visibility ------------------------------------------------------------------------------------

        /// <summary>
        /// Show or hide the play HUD. Toggles the ROOT object rather than this component, so the HUD keeps tracking
        /// the wallet while hidden and comes back already correct instead of blank for a frame.
        /// </summary>
        public void ShowHud(bool visible)
        {
            if (playHudRoot != null) playHudRoot.SetActive(visible);
        }

        // ---- balances ------------------------------------------------------------------------------------------

        private void ShowBalances(WalletBalances b)
        {
            if (b == null) return;
            Write(chipsLabel, b.Chips);
            Write(kashLabel, b.Kash);
            Write(gemsLabel, b.Gems);
        }

        private void Write(TMP_Text label, decimal value)
        {
            if (label == null) return;

            // A ChipCountJuice may be rolling this exact label (the table HUD does this). Writing over it mid-roll
            // makes the number jump, so leave it to whoever owns the animation.
            if (UI.ChipCountJuice.Owns(label)) return;

            label.text = compactBalances
                ? Game.Betting.ChipView.Format((long)value)
                : value.ToString(balanceFormat);
        }
    }
}
