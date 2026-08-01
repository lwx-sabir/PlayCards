using System.Linq;
using PlayCard.Game.Table;
using PlayCard.UI;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PlayCard.EditorTools
{
    /// <summary>
    /// Builds the betting-window countdown widget in the open Table scene by CLONING the existing Turn_Popup — so it
    /// inherits the art, font, anchoring and slide distance that were already tuned by hand, instead of asking anyone
    /// to rebuild and re-align a second popup from scratch.
    ///
    /// Clones Turn_Popup → "Bet_Popup", retitles its Text_Info to "Place Your Bets", adds
    /// <see cref="BetTimerPopup"/> to the always-active TableHUD (the controller-on-active-object rule every popup
    /// here follows) and wires panel / timerLabel / captionLabel / table / view. Everything is Undo-registered.
    /// </summary>
    public static class BetTimerPopupBuilder
    {
        private const string PopupName = "Bet_Popup";
        private const string SourceName = "Turn_Popup";

        [MenuItem("Khela/Table/Create Bet Timer Popup")]
        public static void Create()
        {
            var turnPopup = Object.FindObjectsByType<TurnPopup>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault();
            if (turnPopup == null)
            {
                EditorUtility.DisplayDialog("Create Bet Timer Popup",
                    "No TurnPopup found in the open scene.\n\nOpen Assets/1Khela/_Scenes/Table.unity and try again — " +
                    "this tool clones the existing Turn_Popup so the two countdowns match.", "OK");
                return;
            }

            // The Turn_Popup VISUAL, found by name under the controller's object. Read via SerializedObject so we
            // pick up the exact reference the scene has wired, not a guess.
            var so = new SerializedObject(turnPopup);
            var srcPanel = so.FindProperty("panel")?.objectReferenceValue as RectTransform;
            if (srcPanel == null)
            {
                var found = turnPopup.GetComponentsInChildren<RectTransform>(true).FirstOrDefault(t => t.name == SourceName);
                srcPanel = found;
            }
            if (srcPanel == null)
            {
                EditorUtility.DisplayDialog("Create Bet Timer Popup",
                    $"TurnPopup was found but its 'panel' field is empty and no child named '{SourceName}' exists, " +
                    "so there is nothing to clone.", "OK");
                return;
            }

            var host = turnPopup.gameObject;   // TableHUD — always active, the correct controller host

            // IDEMPOTENT: re-running the menu re-wires what's already there rather than stacking duplicate popups.
            // Any manual repositioning of an existing Bet_Popup is preserved — only the references are refreshed.
            var existing = srcPanel.parent != null
                ? srcPanel.parent.Cast<Transform>().FirstOrDefault(t => t.name == PopupName)
                : null;

            GameObject clone;
            if (existing != null)
            {
                clone = existing.gameObject;
                Debug.Log($"[BetTimerPopup] '{PopupName}' already exists — re-wiring it instead of cloning again.", clone);
            }
            else
            {
                clone = Object.Instantiate(srcPanel.gameObject, srcPanel.parent);
                clone.name = PopupName;
                Undo.RegisterCreatedObjectUndo(clone, "Create Bet Timer Popup");
                var newRect = clone.GetComponent<RectTransform>();
                newRect.anchoredPosition = srcPanel.anchoredPosition;   // identical placement; the two never show together
                newRect.localScale = srcPanel.localScale;
            }
            var cloneRect = clone.GetComponent<RectTransform>();
            clone.SetActive(false);                                     // hidden by default — the controller shows it

            // Text_Info is the caption ("Your Turn" on the source); Text_Time is the countdown.
            var labels = clone.GetComponentsInChildren<TMP_Text>(true);
            var caption = labels.FirstOrDefault(t => t.name == "Text_Info");
            var timer = labels.FirstOrDefault(t => t.name == "Text_Time");
            if (caption != null) caption.text = "Place Your Bets";
            if (timer != null) timer.text = "00:15s";

            // ---- add + wire the controller ----
            var ctrl = host.GetComponent<BetTimerPopup>();
            if (ctrl == null) ctrl = Undo.AddComponent<BetTimerPopup>(host);
            var cso = new SerializedObject(ctrl);
            cso.FindProperty("panel").objectReferenceValue = cloneRect;
            if (timer != null) cso.FindProperty("timerLabel").objectReferenceValue = timer;
            if (caption != null) cso.FindProperty("captionLabel").objectReferenceValue = caption;

            var tso = new SerializedObject(turnPopup);
            cso.FindProperty("table").objectReferenceValue =
                tso.FindProperty("table")?.objectReferenceValue
                ?? Object.FindAnyObjectByType<TableController>();
            cso.FindProperty("view").objectReferenceValue =
                Object.FindObjectsByType<BlackjackTableView>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();

            // Match the source's slide so both popups move identically.
            var srcSlide = tso.FindProperty("slideDistance");
            var srcDur = tso.FindProperty("slideDuration");
            if (srcSlide != null) cso.FindProperty("slideDistance").floatValue = srcSlide.floatValue;
            if (srcDur != null) cso.FindProperty("slideDuration").floatValue = srcDur.floatValue;
            cso.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(host.scene);
            Selection.activeGameObject = clone;
            EditorGUIUtility.PingObject(clone);

            Debug.Log($"[BetTimerPopup] '{PopupName}' is wired to BetTimerPopup on '{host.name}' " +
                      $"(panel + timer '{(timer != null ? timer.name : "?")}' + caption '{(caption != null ? caption.name : "?")}'). " +
                      "It slides up between rounds while the server's betting window counts down, and away when the round " +
                      $"starts. It currently sits exactly on top of {SourceName} — reposition it, then SAVE THE SCENE.", clone);
        }
    }
}
