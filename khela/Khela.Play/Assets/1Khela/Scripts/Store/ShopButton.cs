using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.Store
{
    /// <summary>
    /// The "open the shop" button, for any scene — the same shape as <c>PassButton</c>. Put it on the button, drag in
    /// the shop canvas prefab, and it spawns the shop on demand: no copy of the shop in every scene, and no lookup by
    /// name.
    ///
    /// The shop is spawned ONCE per session and kept, so the catalog it fetched, the art it downloaded and the cards it
    /// built survive closing it and moving between scenes. Reopening is then instant, which matters for the screen a
    /// player bounces in and out of from a table.
    ///
    /// ⚠️ Put this on an ALWAYS-ACTIVE object (the button itself), never on something it hides — a component on a
    /// disabled object never runs, a trap this project has hit before.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShopButton : MonoBehaviour
    {
        [Header("Panel")]
        [Tooltip("The shop canvas prefab. Spawned once, then reused for the rest of the session.")]
        [SerializeField] private ShopScreen shopPrefab;
        [Tooltip("Already in this scene? Assign it here instead and leave the prefab empty.")]
        [SerializeField] private ShopScreen existingPanel;

        private Button button;
        /// <summary>One shop across the whole session, whatever scene opened it.</summary>
        private static ShopScreen spawned;

        private void Awake()
        {
            button = GetComponent<Button>();
            if (button != null) button.onClick.AddListener(Open);
        }

        private void OnDestroy()
        {
            if (button != null) button.onClick.RemoveListener(Open);
        }

        /// <summary>Open the shop. Wired to this object's Button automatically; also callable from any other event.</summary>
        public void Open()
        {
            var panel = Resolve();
            if (panel == null)
            {
                Debug.LogWarning($"{name}: no shop assigned — set either Shop Prefab or Existing Panel.", this);
                return;
            }
            panel.Open();
        }

        private ShopScreen Resolve()
        {
            // A panel that already lives in a SCENE can be driven directly. A prefab ASSET cannot: SetActive on it does
            // nothing and spawning cards would try to parent them into the asset's own transforms. Dragging the prefab
            // into the wrong field is an easy slip, so an asset found in `existingPanel` is treated as a prefab source.
            if (IsInScene(existingPanel)) return existingPanel;
            if (spawned != null) return spawned;

            var source = shopPrefab != null ? shopPrefab : existingPanel;
            if (source == null) return null;

            // At the scene ROOT with its own canvas — never parented into this scene's UI, so the shop cannot inherit
            // someone else's scaling or draw order.
            spawned = Instantiate(source);
            spawned.name = source.name;
            DontDestroyOnLoad(spawned.gameObject);
            spawned.gameObject.SetActive(false);
            return spawned;
        }

        /// <summary>True only for a real scene object — false for a prefab asset dragged in from the Project window.</summary>
        private static bool IsInScene(ShopScreen panel) => panel != null && panel.gameObject.scene.IsValid();
    }
}
