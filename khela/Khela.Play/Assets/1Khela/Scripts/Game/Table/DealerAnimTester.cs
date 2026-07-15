using System.Collections.Generic;
using Animancer;
using UnityEngine;

namespace PlayCard.Game.Table
{
    /// <summary>
    /// Dev clip previewer. Put this + an <see cref="AnimancerComponent"/> on a BoZo Humanoid rig, press Play, click a
    /// button to run each clip. It AUTO-LOADS every clip from <see cref="folder"/> (no manual assigning) and labels each
    /// button by its source FBX filename (all clips are named "clip"). It auto-picks an "Idle" clip as the resting pose.
    /// Editor/dev tool — not for shipping.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DealerAnimTester : MonoBehaviour
    {
        [Tooltip("Animancer on the rig (the object with the Animator, e.g. BodyRig). Auto-found in children if empty.")]
        [SerializeField] private AnimancerComponent animancer;
        [Tooltip("Folder scanned for clips — every FBX's embedded clip + any standalone .anim. Reload via the context menu.")]
        [SerializeField] private string folder = "Assets/1Khela/Animations/Play_Card";
        [Tooltip("Cross-fade seconds between clips.")]
        [SerializeField] private float fade = 0.15f;
        [Tooltip("After each one-shot, return to an Idle clip (source FBX name contains 'Idle'). Loop that clip's import.")]
        [SerializeField] private bool returnToIdle = true;

        private readonly List<AnimationClip> _clips = new List<AnimationClip>();
        private readonly List<string> _labels = new List<string>();
        private AnimationClip _idle;
        private Vector2 _scroll;

        private void Awake()
        {
            if (animancer == null) animancer = GetComponentInChildren<AnimancerComponent>(true);
            Reload();
            if (returnToIdle && _idle != null && animancer != null) animancer.Play(_idle, fade);
        }

        private void Play(AnimationClip clip)
        {
            if (animancer == null || clip == null) return;
            var state = animancer.Play(clip, fade);
            if (returnToIdle && _idle != null && clip != _idle)
                state.Events(this).OnEnd ??= () => animancer.Play(_idle, fade);
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, 10, 340, Screen.height - 20), GUI.skin.box);
            GUILayout.Label($"Anim Tester — {_clips.Count} clips" + (_idle != null ? "  (idle found)" : ""));
            _scroll = GUILayout.BeginScrollView(_scroll);
            for (int i = 0; i < _clips.Count; i++)
                if (GUILayout.Button(_labels[i], GUILayout.Height(24))) Play(_clips[i]);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        [ContextMenu("Reload clips from folder")]
        private void Reload()
        {
            _clips.Clear(); _labels.Clear(); _idle = null;
#if UNITY_EDITOR
            var seen = new HashSet<AnimationClip>();

            // FBX-embedded clips: scan each model file's sub-asset representations (reliable for FBX clips).
            var modelGuids = UnityEditor.AssetDatabase.FindAssets("t:Model", new[] { folder });
            System.Array.Sort(modelGuids, (a, b) => string.CompareOrdinal(
                UnityEditor.AssetDatabase.GUIDToAssetPath(a), UnityEditor.AssetDatabase.GUIDToAssetPath(b)));
            foreach (var g in modelGuids)
            {
                var path = UnityEditor.AssetDatabase.GUIDToAssetPath(g);
                var label = System.IO.Path.GetFileNameWithoutExtension(path);
                foreach (var rep in UnityEditor.AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
                {
                    if (!(rep is AnimationClip clip) || clip.name.StartsWith("__preview") || !seen.Add(clip)) continue;
                    Add(clip, label);
                }
            }

            // Any standalone .anim files in the same folder.
            foreach (var g in UnityEditor.AssetDatabase.FindAssets("t:AnimationClip", new[] { folder }))
            {
                var path = UnityEditor.AssetDatabase.GUIDToAssetPath(g);
                var clip = UnityEditor.AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip == null || !seen.Add(clip)) continue;
                Add(clip, System.IO.Path.GetFileNameWithoutExtension(path));
            }

            Debug.Log($"[DealerAnimTester] loaded {_clips.Count} clips from {folder}" +
                      (_idle != null ? $" (idle = {_idle.name})" : " (no Idle clip found)") + ".");
#else
            Debug.LogWarning("[DealerAnimTester] clip auto-load is editor-only.");
#endif
        }

        private void Add(AnimationClip clip, string label)
        {
            _clips.Add(clip);
            _labels.Add(label);
            if (_idle == null && label.IndexOf("Idle", System.StringComparison.OrdinalIgnoreCase) >= 0) _idle = clip;
        }
    }
}
