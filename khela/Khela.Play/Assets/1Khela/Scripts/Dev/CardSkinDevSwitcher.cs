using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using PlayCard.App;          // SceneNavigator.Table
using PlayCard.Game.Cards;   // CardSkin
using PlayCard.Game.Table;   // BlackjackTableView

namespace PlayCard.Dev
{
    /// <summary>
    /// DEV-ONLY card-skin picker. Drop on ANY scene, pair up to 4 Buttons with 4 <see cref="CardSkin"/>s.
    /// Pressing a button ONLY selects that skin and SAVES it locally (PlayerPrefs) — it does NOT navigate
    /// anywhere. Open the Blackjack table however you normally do (Lobby / Play-Now) and it renders every
    /// card with the saved skin.
    ///
    /// How the table picks it up: the choice is saved by name and kept in a static; a runtime hook applies
    /// it to the table's <see cref="BlackjackTableView"/> the instant Blackjack_Table loads. On a fresh
    /// Play the saved choice is restored from PlayerPrefs when this switcher wakes (so keep it on a scene
    /// that loads before the table — a menu/boot scene). No production code is modified: the view's private
    /// <c>skin</c> field is set via reflection.
    /// </summary>
    public sealed class CardSkinDevSwitcher : MonoBehaviour
    {
        [System.Serializable]
        public struct Entry
        {
            public Button button;   // the UI button to wire
            public CardSkin skin;   // the skin it selects
        }

        [Tooltip("Pair each button with the skin it selects. Sized to 4 by default; add more if you like.")]
        [SerializeField] private Entry[] entries = new Entry[4];

        private void Awake()
        {
            CardSkinDevSelection.RestoreFrom(entries);   // re-apply the locally-saved choice on a fresh Play
            foreach (var e in entries)
            {
                if (e.button == null) continue;
                var skin = e.skin;                       // capture per-iteration for the closure
                e.button.onClick.AddListener(() => Select(skin));
            }
        }

        /// <summary>Select + save a skin (also usable from a Button's OnClick in the Inspector).</summary>
        public void Select(CardSkin skin)
        {
            if (skin == null) { Debug.LogWarning("[CardSkinDev] button has no skin assigned."); return; }
            CardSkinDevSelection.Select(skin);
            Debug.Log($"[CardSkinDev] selected + saved '{skin.name}'. Open the table normally to see it.");
        }
    }

    /// <summary>
    /// Persists the dev-selected skin (PlayerPrefs, by name) and pushes it onto every
    /// <see cref="BlackjackTableView"/> whenever Blackjack_Table loads. Reflection so no production change.
    /// </summary>
    public static class CardSkinDevSelection
    {
        private const string PrefKey = "khela.dev.cardSkin";

        public static CardSkin Selected { get; private set; }

        public static void Select(CardSkin skin)
        {
            Selected = skin;
            PlayerPrefs.SetString(PrefKey, skin != null ? skin.name : "");
            PlayerPrefs.Save();
            Apply();                                     // live-swap if a table is already loaded
        }

        /// <summary>On a fresh Play, resolve the saved name against a switcher's entries (it's the catalog).</summary>
        public static void RestoreFrom(CardSkinDevSwitcher.Entry[] entries)
        {
            if (Selected != null || entries == null) return;   // already chosen this session
            var saved = PlayerPrefs.GetString(PrefKey, "");
            if (string.IsNullOrEmpty(saved)) return;
            foreach (var e in entries)
                if (e.skin != null && e.skin.name == saved) { Selected = e.skin; Apply(); return; }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Hook()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;   // guard against domain-reload double-subscribe
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (Selected != null && scene.name == SceneNavigator.Table) Apply();
        }

        // Cached once — the private field the view applies to every spawned card.
        private static readonly FieldInfo SkinField =
            typeof(BlackjackTableView).GetField("skin", BindingFlags.NonPublic | BindingFlags.Instance);

        private static void Apply()
        {
            if (Selected == null) return;
            if (SkinField == null)
            {
                Debug.LogWarning("[CardSkinDev] BlackjackTableView.skin not found (renamed?) — skin not applied.");
                return;
            }
            var views = Object.FindObjectsByType<BlackjackTableView>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var v in views) SkinField.SetValue(v, Selected);
            if (views.Length > 0)
                Debug.Log($"[CardSkinDev] applied '{Selected.name}' to {views.Length} table view(s).");
        }
    }
}
