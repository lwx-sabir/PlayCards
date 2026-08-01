using UnityEngine;
using UnityEngine.UI;
using PlayCard.Quality;
using Tier = PlayCard.Quality.GraphicsQualityManager.Tier;

namespace PlayCard.UI
{
    /// <summary>
    /// Minimal Settings panel — testing scaffold. For now it wires ONLY the close button and the four
    /// graphics-quality toggles. Audio / language / push / vibration come later.
    ///
    /// Selecting a toggle routes through <see cref="GraphicsQualityManager"/> (the single source of truth),
    /// which swaps the URP asset, sets the FPS ceiling, persists the choice, and — crucially — is also the
    /// thing that applies the tier at startup so it holds across ALL scenes, not just Home.
    ///
    /// NOTE the label vs enum mismatch: the UI says "Medium" → <see cref="Tier.Mid"/>.
    /// </summary>
    public sealed class SettingsPanel : MonoBehaviour
    {
        [Header("Panel")]
        [Tooltip("The panel GameObject the Close button hides. Defaults to THIS GameObject if empty.")]
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private Button closeButton;

        [Header("Graphics quality (put all four in ONE ToggleGroup for radio behaviour)")]
        [SerializeField] private Toggle lowToggle;
        [SerializeField] private Toggle mediumToggle;   // UI "Medium" → Tier.Mid
        [SerializeField] private Toggle highToggle;
        [SerializeField] private Toggle ultraToggle;

        [Tooltip("Optional small label/panel, e.g. \"Graphics changes apply when you enter a game.\" Shown when " +
                 "the player changes tier — resolution/MSAA/HDR only take full effect on the next scene load.")]
        [SerializeField] private GameObject applyHint;

        private void Awake()
        {
            if (panelRoot == null) panelRoot = gameObject;
            if (closeButton != null) closeButton.onClick.AddListener(Close);

            Wire(lowToggle,    Tier.Low);
            Wire(mediumToggle, Tier.Mid);
            Wire(highToggle,   Tier.High);
            Wire(ultraToggle,  Tier.Ultra);
        }

        private void OnEnable()
        {
            SyncTogglesToCurrent();
            if (applyHint != null) applyHint.SetActive(false);   // fresh each open
        }

        /// <summary>Show the panel (for whatever opens Settings — e.g. the gear button).</summary>
        public void Open() { panelRoot.SetActive(true); }

        /// <summary>Hide the panel (close button).</summary>
        public void Close() { panelRoot.SetActive(false); }

        private void Wire(Toggle t, Tier tier)
        {
            if (t == null) return;
            t.onValueChanged.AddListener(isOn => { if (isOn) ApplyTier(tier); });
        }

        private void ApplyTier(Tier tier)
        {
            // Saves + applies the tier. Shadows / SSAO / post change live; render scale / MSAA / HDR take full
            // effect on the NEXT scene load (they can't safely resize a live camera) — hence the apply hint.
            GraphicsQualityManager.SetTier(tier);
            if (applyHint != null) applyHint.SetActive(true);
        }

        /// <summary>Reflect the active tier in the radios without firing their callbacks.</summary>
        private void SyncTogglesToCurrent()
        {
            var cur = GraphicsQualityManager.Current;
            Set(lowToggle,    cur == Tier.Low);
            Set(mediumToggle, cur == Tier.Mid);
            Set(highToggle,   cur == Tier.High);
            Set(ultraToggle,  cur == Tier.Ultra);
        }

        private static void Set(Toggle t, bool on) { if (t != null) t.SetIsOnWithoutNotify(on); }
    }
}
