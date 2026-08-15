using System.Collections.Generic;
using PlayCard.Audio;
using Sonity;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.EditorTools
{
    /// <summary>
    /// Adds <see cref="ButtonSound"/> to the selected Buttons and assigns the chosen SoundEvents.
    ///
    /// A deliberate editor pass rather than a runtime scan: the result is visible in the Inspector, diffable in the
    /// scene file, and overridable per button afterwards. A runtime "find every Button and add a sound" is invisible,
    /// costs a hierarchy walk on load, and gives you nowhere to make the one exception you always end up needing.
    ///
    /// Applies to EXACTLY what is selected. Buttons do not share one sound — Hit, Stand and Deal each want their own,
    /// and a canvas holds a hundred of them — so a recursive sweep would assign the wrong click to almost everything
    /// and leave you no way to see which ones you meant. Select the group that shares a sound, apply, then select the
    /// next group. <see cref="_includeChildren"/> restores the sweep for the case where a whole panel really is the
    /// same button.
    ///
    /// Re-running is safe — an existing ButtonSound is reconfigured, not duplicated.
    /// </summary>
    public sealed class ButtonSoundTool : EditorWindow
    {
        private SoundEvent _click;
        private SoundEvent _denied;
        private bool _includeChildren;            // off = the selected objects and nothing else
        private bool _includeInactive = true;     // children sweep only
        private bool _overwriteExisting = true;

        [MenuItem("Khela/Audio/Add Button Sound To Selection")]
        private static void Open()
        {
            var w = GetWindow<ButtonSoundTool>(true, "Button Sound", true);
            w.minSize = new Vector2(380f, 190f);
            w.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Select the buttons that share a sound, then Apply. Only the selected objects are touched — do one " +
                "group at a time (all the plain buttons, then Hit, then Stand, then Deal + Repeat).\n\n" +
                "Turn on Include Children to sweep a whole panel instead.", MessageType.Info);

            _click = (SoundEvent)EditorGUILayout.ObjectField("Click", _click, typeof(SoundEvent), false);
            _denied = (SoundEvent)EditorGUILayout.ObjectField("Denied (optional)", _denied, typeof(SoundEvent), false);

            EditorGUILayout.Space();
            _includeChildren = EditorGUILayout.Toggle(
                new GUIContent("Include Children", "Off: only the buttons you selected. On: every Button underneath " +
                                                   "them too."), _includeChildren);
            using (new EditorGUI.DisabledScope(!_includeChildren))
                _includeInactive = EditorGUILayout.Toggle("  Include Inactive", _includeInactive);
            _overwriteExisting = EditorGUILayout.Toggle("Overwrite Existing", _overwriteExisting);

            var roots = Selection.gameObjects;
            int count = roots != null ? roots.Length : 0;
            using (new EditorGUI.DisabledScope(count == 0))
            {
                string label = _includeChildren ? $"Apply To {count} Selected + Children" : $"Apply To {count} Selected";
                if (GUILayout.Button(label, GUILayout.Height(30f))) Apply(roots);
            }

            if (count == 0) EditorGUILayout.HelpBox("Nothing selected.", MessageType.Warning);
        }

        private void Apply(GameObject[] roots)
        {
            int added = 0, updated = 0, skipped = 0;

            var done = new List<string>();
            var notButtons = new List<string>();

            foreach (var root in roots)
            {
                if (root == null) continue;

                if (!_includeChildren)
                {
                    // ButtonSound is [RequireComponent(typeof(Button))], so AddComponent on a non-Button would
                    // SILENTLY ADD A BUTTON to whatever you picked. Refuse and name it instead — quietly turning a
                    // label into a clickable button is the kind of edit you would not find for weeks.
                    if (root.GetComponent<Button>() == null) { notButtons.Add(root.name); skipped++; continue; }
                    ApplyTo(root, done, ref added, ref updated, ref skipped);
                    continue;
                }

                foreach (var button in root.GetComponentsInChildren<Button>(_includeInactive))
                {
                    if (button == null) continue;
                    ApplyTo(button.gameObject, done, ref added, ref updated, ref skipped);
                }
            }

            string scope = _includeChildren ? "selection + children" : "selection only";
            string names = done.Count > 0 ? "\n  " + string.Join("\n  ", done.ToArray()) : "";
            Debug.Log($"[ButtonSoundTool] {scope}: added {added}, updated {updated}, skipped {skipped}." + names);

            // Loud, because it means a button you thought you had just configured is silent.
            if (notButtons.Count > 0)
                Debug.LogWarning("[ButtonSoundTool] no Button component, nothing assigned: " +
                                 string.Join(", ", notButtons.ToArray()));
        }

        private void ApplyTo(GameObject go, List<string> done, ref int added, ref int updated, ref int skipped)
        {
            var sound = go.GetComponent<ButtonSound>();
            if (sound == null)
            {
                // Undo.AddComponent, not AddComponent: this edits scenes and prefab instances, and a pass with no
                // undo is exactly the kind of thing you want to take back.
                sound = Undo.AddComponent<ButtonSound>(go);
                added++;
            }
            else if (_overwriteExisting)
            {
                Undo.RecordObject(sound, "Configure Button Sound");
                updated++;
            }
            else { skipped++; return; }

            sound.Configure(_click, _denied);
            EditorUtility.SetDirty(sound);
            // Without this a change to a component on a PREFAB INSTANCE is not recorded as an override and is
            // silently lost on the next prefab reimport.
            PrefabUtility.RecordPrefabInstancePropertyModifications(sound);
            done.Add($"{go.name}  ←  {(_click != null ? _click.name : "(silent)")}");
        }
    }
}
