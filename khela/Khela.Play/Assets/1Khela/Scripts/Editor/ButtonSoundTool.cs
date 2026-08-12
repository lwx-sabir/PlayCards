using PlayCard.Audio;
using Sonity;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace PlayCard.EditorTools
{
    /// <summary>
    /// Adds <see cref="ButtonSound"/> to every Button under the selection and assigns the chosen SoundEvents.
    ///
    /// A deliberate editor pass rather than a runtime scan: the result is visible in the Inspector, diffable in the
    /// scene file, and overridable per button afterwards. A runtime "find every Button and add a sound" is invisible,
    /// costs a hierarchy walk on load, and gives you nowhere to make the one exception you always end up needing.
    ///
    /// Re-running is safe — an existing ButtonSound is reconfigured, not duplicated.
    /// </summary>
    public sealed class ButtonSoundTool : EditorWindow
    {
        private SoundEvent _click;
        private SoundEvent _denied;
        private bool _includeInactive = true;
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
                "Select one or more roots in the Hierarchy (a Canvas, a panel), then Apply. Every Button underneath " +
                "gets a ButtonSound with these events.", MessageType.Info);

            _click = (SoundEvent)EditorGUILayout.ObjectField("Click", _click, typeof(SoundEvent), false);
            _denied = (SoundEvent)EditorGUILayout.ObjectField("Denied (optional)", _denied, typeof(SoundEvent), false);
            _includeInactive = EditorGUILayout.Toggle("Include Inactive", _includeInactive);
            _overwriteExisting = EditorGUILayout.Toggle("Overwrite Existing", _overwriteExisting);

            var roots = Selection.gameObjects;
            using (new EditorGUI.DisabledScope(roots == null || roots.Length == 0))
            {
                if (GUILayout.Button("Apply To Selection", GUILayout.Height(30f))) Apply(roots);
            }

            if (roots == null || roots.Length == 0)
                EditorGUILayout.HelpBox("Nothing selected.", MessageType.Warning);
        }

        private void Apply(GameObject[] roots)
        {
            int added = 0, updated = 0, skipped = 0;

            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var button in root.GetComponentsInChildren<Button>(_includeInactive))
                {
                    if (button == null) continue;

                    var sound = button.GetComponent<ButtonSound>();
                    if (sound == null)
                    {
                        // Undo.AddComponent, not AddComponent: this edits scenes and prefab instances, and a bulk pass
                        // with no undo is exactly the kind of thing you want to take back.
                        sound = Undo.AddComponent<ButtonSound>(button.gameObject);
                        added++;
                    }
                    else if (_overwriteExisting)
                    {
                        Undo.RecordObject(sound, "Configure Button Sound");
                        updated++;
                    }
                    else { skipped++; continue; }

                    sound.Configure(_click, _denied);
                    EditorUtility.SetDirty(sound);
                    // Without this a change to a component on a PREFAB INSTANCE is not recorded as an override and is
                    // silently lost on the next prefab reimport.
                    PrefabUtility.RecordPrefabInstancePropertyModifications(sound);
                }
            }

            Debug.Log($"[ButtonSoundTool] added {added}, updated {updated}, skipped {skipped}.");
        }
    }
}
