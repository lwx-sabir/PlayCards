using PlayCard.App;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Home
{
    /// <summary>
    /// A selectable game in the home carousel (selection only — not a real table). A self-contained prefab:
    /// it carries the game's <see cref="GameDefinition"/> config and owns its world-space title + Play Now /
    /// Lobby buttons. As an <see cref="ICarouselItem"/> the carousel reveals its buttons only when centred.
    /// Button onClick is wired here in code and routes by the definition — assign the title/buttons ONCE on
    /// the prefab and every instance works; only <see cref="definition"/> (and the 3D table) vary per instance.
    /// </summary>
    public sealed class GameMode : MonoBehaviour, ICarouselItem
    {
        [Header("Config")]
        public GameDefinition definition;

        [Tooltip("Optional: seat straight at this table id on Play Now. Empty = auto-match / lobby.")]
        public string tableId = "";

        [Header("World-space UI (lives on this prefab)")]
        [Tooltip("Name label; text auto-set from the definition. Its PARENT object is shown/hidden per focus, " +
                 "so the whole label group (icon + text) toggles together.")]
        [SerializeField] private TMP_Text titleText;
        [SerializeField] private Button playNowButton;
        [SerializeField] private Button lobbyButton;

        public Transform Transform => transform;
        public string DisplayName => definition ? definition.displayName : name;
        public string Key => definition ? definition.key : string.Empty;
        public bool Available => definition && definition.available;

        private void Awake()
        {
            if (titleText) titleText.text = DisplayName;
            if (playNowButton) playNowButton.onClick.AddListener(QuickPlay);
            if (lobbyButton) lobbyButton.onClick.AddListener(OpenLobby);
            SetSelected(false);   // hidden until the carousel centres this game
        }

        private void OnDestroy()
        {
            if (playNowButton) playNowButton.onClick.RemoveListener(QuickPlay);
            if (lobbyButton) lobbyButton.onClick.RemoveListener(OpenLobby);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Keep the visible label in sync with the assigned definition while editing.
            if (titleText && definition) titleText.text = definition.displayName;
        }
#endif

        /// <summary>Carousel calls this on every selection change — only the centred game shows its buttons.</summary>
        public void SetSelected(bool selected)
        {
            // Hide the whole label by toggling the title's PARENT (holds icon + text), not just the text.
            if (titleText && titleText.transform.parent)
                titleText.transform.parent.gameObject.SetActive(selected);

            bool showButtons = selected && Available;
            if (playNowButton) playNowButton.gameObject.SetActive(showButtons);
            if (lobbyButton) lobbyButton.gameObject.SetActive(showButtons);
        }

        /// <summary>Direct join: auto-match into a seat (seats at <see cref="tableId"/> if set, else lobby).</summary>
        public void QuickPlay()
        {
            if (!Available) return;
            GameSession.SelectedGame = Key;
            if (!string.IsNullOrEmpty(tableId)) SceneNavigator.GoToTable(tableId);
            else SceneNavigator.GoToLobby();
        }

        /// <summary>Open the lobby (table browser) for this game.</summary>
        public void OpenLobby()
        {
            if (!Available) return;
            GameSession.SelectedGame = Key;
            SceneNavigator.GoToLobby();
        }
    }
}
