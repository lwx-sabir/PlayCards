using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using PlayCard.UI;

namespace PlayCard.EditorTools
{
    /// <summary>
    /// <b>Khela ▸ UI ▸ Wrap Selection in Safe Area</b> — inserts a full-stretch "SafeArea" child under the selected
    /// Canvas / UI root and reparents all of its current children under it (their layout is preserved because the panel
    /// is identical in size to the parent), then adds the <see cref="SafeArea"/> component. Do this ONCE per HUD canvas.
    ///
    /// Afterwards: drag any FULL-SCREEN art (background/dim/vignette — the 3D table already shows through the camera, not
    /// the canvas) back OUT of the SafeArea panel so it still bleeds to the physical screen edges. Only edge-hugging UI
    /// (top bar, action bar, popups) should live inside. Undoable.
    /// </summary>
    public static class SafeAreaWrapTool
    {
        [MenuItem("Khela/UI/Wrap Selection in Safe Area")]
        private static void Wrap()
        {
            var go = Selection.activeGameObject;
            var parent = go != null ? go.GetComponent<RectTransform>() : null;
            if (parent == null)
            {
                EditorUtility.DisplayDialog("Safe Area", "Select the HUD Canvas (or a UI root with a RectTransform) first.", "OK");
                return;
            }
            if (go.GetComponentInChildren<SafeArea>() != null &&
                !EditorUtility.DisplayDialog("Safe Area", "This hierarchy already contains a SafeArea. Wrap again anyway?", "Wrap", "Cancel"))
                return;

            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Wrap in Safe Area");

            // Snapshot existing children BEFORE creating the panel.
            var children = new List<Transform>();
            foreach (Transform c in parent) children.Add(c);

            // Full-stretch SafeArea panel as the parent's first child.
            var panelGo = new GameObject("SafeArea", typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(panelGo, "Create Safe Area");
            var panel = (RectTransform)panelGo.transform;
            Undo.SetTransformParent(panel, parent, "Wrap in Safe Area");
            panel.anchorMin = Vector2.zero;
            panel.anchorMax = Vector2.one;
            panel.pivot = new Vector2(0.5f, 0.5f);
            panel.offsetMin = Vector2.zero;
            panel.offsetMax = Vector2.zero;
            panel.localScale = Vector3.one;
            panel.SetSiblingIndex(0);

            // Move the original children under the panel (world position kept → nothing visually shifts).
            foreach (var c in children)
                Undo.SetTransformParent(c, panel, "Wrap in Safe Area");

            Undo.AddComponent<SafeArea>(panelGo);

            EditorUtility.SetDirty(parent);
            Selection.activeGameObject = panelGo;
            Undo.CollapseUndoOperations(group);

            Debug.Log($"[SafeArea] Wrapped {children.Count} object(s) under a SafeArea panel on '{parent.name}'. " +
                      "→ Drag any full-screen background/dim art back OUT of it so it still fills the screen; keep only " +
                      "edge-hugging HUD inside. Preview real insets via the Device Simulator in Play mode.", panelGo);
        }
    }
}
