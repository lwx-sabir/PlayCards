using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.App
{
    /// <summary>
    /// Scene-side UI for the global <see cref="DevLogRecorder"/>. Put this on an object in whatever scene holds your
    /// debug panel and assign the Send/Clear buttons (+ optional status label). It wires them to the running recorder
    /// (<see cref="DevLogRecorder.Instance"/>) — the recorder itself starts automatically at boot and is not in any
    /// scene. Safe in Release builds: the recorder doesn't exist there, so this hides/disables its controls.
    /// </summary>
    public sealed class DevLogBinder : MonoBehaviour
    {
        [Header("Assign your debug buttons")]
        [SerializeField] private Button sendButton;    // → DevLogRecorder.SendLogs (upload all logs to the server)
        [SerializeField] private Button clearButton;   // → DevLogRecorder.ClearOldLogs (delete old device logs)
        [SerializeField] private TMP_Text statusLabel; // optional: shows recording path / "Sent…" / errors

        [Tooltip("Optional root object to hide entirely when the recorder isn't running (i.e. a Release build).")]
        [SerializeField] private GameObject panel;

        private DevLogRecorder _rec;

        private void OnEnable()
        {
            _rec = DevLogRecorder.Instance;   // created at boot in dev builds; null in Release

            if (_rec == null)
            {
                if (panel != null) panel.SetActive(false);
                if (sendButton != null) sendButton.interactable = false;
                if (clearButton != null) clearButton.interactable = false;
                if (statusLabel != null) statusLabel.text = string.Empty;
                return;
            }

            if (sendButton != null) sendButton.onClick.AddListener(_rec.SendLogs);
            if (clearButton != null) clearButton.onClick.AddListener(_rec.ClearOldLogs);
            _rec.OnStatus += ShowStatus;
            ShowStatus(_rec.Status);   // reflect current state immediately
        }

        private void OnDisable()
        {
            if (_rec == null) return;
            if (sendButton != null) sendButton.onClick.RemoveListener(_rec.SendLogs);
            if (clearButton != null) clearButton.onClick.RemoveListener(_rec.ClearOldLogs);
            _rec.OnStatus -= ShowStatus;
            _rec = null;
        }

        private void ShowStatus(string s)
        {
            if (statusLabel != null) statusLabel.text = s;
        }
    }
}
