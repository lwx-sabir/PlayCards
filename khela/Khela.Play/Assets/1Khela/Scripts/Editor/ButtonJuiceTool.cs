using PlayCard.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.EditorTools
{
    /// <summary>
    /// Adds <see cref="ButtonJuice"/> with one shared tuning — the house feel, applied in a pass you can see in the
    /// diff and override per button afterwards.
    ///
    /// Applies to EXACTLY what is selected. Not every button wants the same feel (or any feel), and a canvas-wide
    /// sweep makes the exceptions invisible: you cannot tell afterwards which buttons were chosen and which were
    /// merely caught. Picking them is the authoring decision. The Include Children toggle turns it back into a
    /// recursive sweep for the cases where you do want a whole panel at once.
    ///
    /// Re-running is safe — an existing ButtonJuice is retuned, not duplicated.
    /// </summary>
    public sealed class ButtonJuiceTool : EditorWindow
    {
        private float _pressScale = 0.92f;
        private float _pressSeconds = 0.05f;
        private float _stretch = 0.5f;
        private float _springFrequency = 5.5f;
        private float _springDamping = 0.18f;
        private float _shakePixels = 7f;
        private float _shakeSeconds = 0.28f;

        private bool _includeChildren;            // off = the selected objects and nothing else
        private bool _includeInactive = true;     // children sweep only
        private bool _overwriteExisting = true;

        [MenuItem("Khela/UI/Add Button Juice To Selection")]
        private static void Open()
        {
            var w = GetWindow<ButtonJuiceTool>(true, "Button Juice", true);
            w.minSize = new Vector2(400f, 300f);
            w.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Select the buttons you want in the Hierarchy, then Apply. Only the selected objects are touched.\n\n" +
                "Turn on Include Children to sweep a whole panel instead — that mode takes Buttons and Toggles only " +
                "and skips sliders, scrollbars, input fields and dropdowns.\n\n" +
                "Defaults are the house feel: an 8% squash on touch-down that widens as it flattens, a springy " +
                "release that overshoots and bounces twice before settling, and a refusal shake when a dead button " +
                "is tapped.", MessageType.Info);

            EditorGUILayout.LabelField("Press", EditorStyles.boldLabel);
            _pressScale = EditorGUILayout.Slider("Press Scale", _pressScale, 0.6f, 1f);
            _pressSeconds = EditorGUILayout.Slider("Press Seconds", _pressSeconds, 0.01f, 0.3f);
            _stretch = EditorGUILayout.Slider(
                new GUIContent("Squash & Stretch", "How much the width moves the opposite way to the height. 0 = " +
                                                   "uniform scale, which reads as a zoom rather than a press."),
                _stretch, 0f, 1.5f);

            EditorGUILayout.LabelField("Release Spring", EditorStyles.boldLabel);
            _springFrequency = EditorGUILayout.Slider("Frequency (Hz)", _springFrequency, 1f, 15f);
            _springDamping = EditorGUILayout.Slider("Damping", _springDamping, 0.05f, 1f);

            EditorGUILayout.LabelField("Denied", EditorStyles.boldLabel);
            _shakePixels = EditorGUILayout.Slider("Shake Pixels", _shakePixels, 0f, 40f);
            _shakeSeconds = EditorGUILayout.Slider("Shake Seconds", _shakeSeconds, 0.05f, 1f);

            EditorGUILayout.Space();
            _includeChildren = EditorGUILayout.Toggle(
                new GUIContent("Include Children", "Off: only the objects you selected. On: sweep every Button and " +
                                                   "Toggle underneath them too."), _includeChildren);
            using (new EditorGUI.DisabledScope(!_includeChildren))
                _includeInactive = EditorGUILayout.Toggle("  Include Inactive", _includeInactive);
            _overwriteExisting = EditorGUILayout.Toggle("Overwrite Existing", _overwriteExisting);

            var roots = Selection.gameObjects;
            int count = roots != null ? roots.Length : 0;
            using (new EditorGUI.DisabledScope(count == 0))
            {
                string label = _includeChildren
                    ? $"Apply To {count} Selected + Children"
                    : $"Apply To {count} Selected";
                if (GUILayout.Button(label, GUILayout.Height(30f))) Apply(roots);
            }

            if (count == 0) EditorGUILayout.HelpBox("Nothing selected.", MessageType.Warning);
        }

        private void Apply(GameObject[] roots)
        {
            int added = 0, updated = 0, skipped = 0;

            foreach (var root in roots)
            {
                if (root == null) continue;

                if (!_includeChildren)
                {
                    // Exactly what was picked. No type filter here: selecting an object IS the decision, and a parent
                    // with no Graphic of its own is a legitimate target — Unity bubbles pointer events up from the
                    // child that was hit, so juicing a button GROUP works.
                    ApplyTo(root, ref added, ref updated, ref skipped);
                    continue;
                }

                foreach (var selectable in root.GetComponentsInChildren<Selectable>(_includeInactive))
                {
                    if (selectable == null) continue;
                    // Buttons and Toggles only. Selectable also covers Slider, Scrollbar, InputField and Dropdown —
                    // press-squashing a text field or popping a scrollbar is noise, and worse, the shake would fight
                    // a slider handle whose position is the thing the player is setting. Select those directly (with
                    // Include Children off) if you ever do want them.
                    if (!(selectable is Button) && !(selectable is Toggle)) { skipped++; continue; }
                    ApplyTo(selectable.gameObject, ref added, ref updated, ref skipped);
                }
            }

            string scope = _includeChildren ? "selection + children" : "selection only";
            Debug.Log($"[ButtonJuiceTool] {scope}: added {added}, updated {updated}, skipped {skipped}.");
        }

        private bool ApplyTo(GameObject go, ref int added, ref int updated, ref int skipped)
        {
            var juice = go.GetComponent<ButtonJuice>();
            if (juice == null)
            {
                // Undo.AddComponent so the whole pass can be taken back in one step.
                juice = Undo.AddComponent<ButtonJuice>(go);
                added++;
            }
            else if (_overwriteExisting)
            {
                Undo.RecordObject(juice, "Configure Button Juice");
                updated++;
            }
            else { skipped++; return false; }

            juice.Configure(_pressScale, _pressSeconds, _stretch, _springFrequency, _springDamping,
                            _shakePixels, _shakeSeconds);
            EditorUtility.SetDirty(juice);
            // Without this a change on a PREFAB INSTANCE is not recorded as an override and is silently lost on the
            // next reimport.
            PrefabUtility.RecordPrefabInstancePropertyModifications(juice);
            return true;
        }
    }
}
