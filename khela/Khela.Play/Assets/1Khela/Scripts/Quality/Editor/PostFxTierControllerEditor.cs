using System;
using System.Collections.Generic;
using System.Linq;
using PlayCard.Quality;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace PlayCard.QualityEditor
{
    /// <summary>
    /// Inspector for <see cref="PostFxTierController"/>. Its job is to answer, at a glance, the question that
    /// keeps costing time: "is my override actually applying?" — by showing the resolved tier, which profiles
    /// are live, how many parameters each override really overrides, and the specific mistakes that silently
    /// make an override do nothing (bad priority, ticked component with no ticked parameters, cloned profile).
    /// </summary>
    [CustomEditor(typeof(PostFxTierController))]
    public sealed class PostFxTierControllerEditor : Editor
    {
        // Keep the readout live while playing so tier switches are visible as they happen.
        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            serializedObject.Update();

            var ctrl       = (PostFxTierController)target;
            var baseVol    = Obj<Volume>("volume");
            var ovrVol     = Obj<Volume>("overrideVolume");
            var sharedBase = Obj<VolumeProfile>("sharedBase");
            var lowO       = Obj<VolumeProfile>("lowOverride");
            var midO       = Obj<VolumeProfile>("midOverride");
            var highO      = Obj<VolumeProfile>("highOverride");
            float ovrPrio  = serializedObject.FindProperty("overridePriority").floatValue;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Resolved state", EditorStyles.boldLabel);

            if (Application.isPlaying)
            {
                EditorGUILayout.HelpBox(ctrl.DescribeResolvedState(), MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Edit mode — profiles are assigned in Awake, so this is what WILL resolve on Play:\n" +
                    $"• Base: {(sharedBase != null ? sharedBase.name : "(using the per-tier full profiles)")}\n" +
                    $"• Low  override: {Describe(lowO)}\n" +
                    $"• Mid  override: {Describe(midO)}\n" +
                    $"• High override: {Describe(highO)}",
                    MessageType.None);
            }

            // ---- the mistakes that make an override silently do nothing -------------------------------
            if (baseVol == null)
            {
                EditorGUILayout.HelpBox("No base Volume assigned — nothing can be applied.", MessageType.Error);
            }
            else
            {
                if (ovrVol != null && ovrPrio <= baseVol.priority)
                    EditorGUILayout.HelpBox(
                        $"Override priority ({ovrPrio}) must be GREATER than the base volume's priority " +
                        $"({baseVol.priority}). URP applies volumes low→high, so as configured the override is " +
                        "applied first and the base overwrites it. Equal priorities are worse: the winner falls " +
                        "back to registration order, which changes between scene loads.", MessageType.Error);

                if (baseVol.HasInstantiatedProfile())
                    EditorGUILayout.HelpBox(
                        "The base Volume holds an INSTANTIATED profile — something read Volume.profile (the " +
                        "getter deep-copies). While that clone exists it shadows sharedProfile, so tier " +
                        "switching is permanently ignored. Recreate the Volume.", MessageType.Error);

                if (!Application.isPlaying && baseVol.sharedProfile == null)
                    EditorGUILayout.HelpBox(
                        "The base Volume has no profile in Edit mode, so the scene renders ungraded until you " +
                        "press Play. Assign PP_Base to its Profile slot to author WYSIWYG — the controller " +
                        "overwrites it at runtime anyway, so it costs nothing.", MessageType.Warning);
            }

            if (ovrVol != null && ovrVol.HasInstantiatedProfile())
                EditorGUILayout.HelpBox(
                    "The override Volume holds an INSTANTIATED profile (Volume.profile was read). Overrides " +
                    "will stop responding to tier changes. Recreate the Volume.", MessageType.Error);

            bool anyOverrideWired = lowO != null || midO != null || highO != null;
            if (anyOverrideWired && ovrVol == null)
                EditorGUILayout.HelpBox(
                    "Override profiles are wired but no override Volume is assigned — one will be auto-created " +
                    "at runtime. Assign an in-scene Volume instead if you want to see overrides in Edit mode " +
                    "(Khela ▸ Post FX ▸ Add Tier Override Volume).", MessageType.Info);

            WarnAboutProfile(lowO,  sharedBase, "Low",  isLowTier: true);
            WarnAboutProfile(midO,  sharedBase, "Mid",  isLowTier: false);
            WarnAboutProfile(highO, sharedBase, "High", isLowTier: false);

            if (GUILayout.Button("Log resolved state to Console"))
                Debug.Log("[PostFxTierController] " + ctrl.DescribeResolvedState(), ctrl);

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>Flags the two ways a tier override quietly misbehaves: overriding nothing, or adding GPU cost.</summary>
        private static void WarnAboutProfile(VolumeProfile p, VolumeProfile baseProfile, string tier, bool isLowTier)
        {
            if (p == null) return;

            if (PostFxTierController.CountOverriddenParameters(p) == 0 && p.components.Count > 0)
                EditorGUILayout.HelpBox(
                    $"{tier} override '{p.name}' contains components but overrides ZERO parameters. Adding a " +
                    "component isn't enough — tick the checkbox next to each individual parameter you want to " +
                    "change. Un-ticked parameters fall through to the base by design.", MessageType.Warning);

            var added = AddedComponents(p, baseProfile);
            if (added.Length > 0)
                EditorGUILayout.HelpBox(
                    $"{tier} override adds effect(s) the base doesn't have: {string.Join(", ", added)}. " +
                    (isLowTier
                        ? "On the LOW tier this switches on real GPU work for the weakest devices — that is the " +
                          "opposite of what Low is for. Move it to Mid/High unless the cost is intentional."
                        : "That's real GPU cost, not a free parameter tweak — intended here, just be aware."),
                    isLowTier ? MessageType.Warning : MessageType.Info);
        }

        private static string[] AddedComponents(VolumeProfile ovr, VolumeProfile baseProfile)
        {
            if (ovr == null || baseProfile == null) return Array.Empty<string>();
            var baseTypes = new HashSet<Type>(
                baseProfile.components.Where(c => c != null).Select(c => c.GetType()));
            return ovr.components
                      .Where(c => c != null && c.active && !baseTypes.Contains(c.GetType()))
                      .Select(c => c.GetType().Name)
                      .ToArray();
        }

        private static string Describe(VolumeProfile p) =>
            p == null
                ? "(none — base only)"
                : $"{p.name} — {PostFxTierController.CountOverriddenParameters(p)} parameter(s) overriding";

        private T Obj<T>(string prop) where T : UnityEngine.Object =>
            serializedObject.FindProperty(prop).objectReferenceValue as T;

        // ---- authoring helper ------------------------------------------------------------------------

        /// <summary>
        /// Creates the higher-priority override Volume as a child of the selected controller and wires it up,
        /// with the correct priority/global flags. This is the one tool here that intentionally edits the scene.
        /// </summary>
        [MenuItem("Khela/Post FX/Add Tier Override Volume")]
        private static void AddTierOverrideVolume()
        {
            var go = Selection.activeGameObject;
            var ctrl = go != null ? go.GetComponent<PostFxTierController>() : null;
            if (ctrl == null)
            {
                EditorUtility.DisplayDialog("Add Tier Override Volume",
                    "Select the GameObject holding the PostFxTierController first.", "OK");
                return;
            }

            var so = new SerializedObject(ctrl);
            if (so.FindProperty("overrideVolume").objectReferenceValue != null)
            {
                EditorUtility.DisplayDialog("Add Tier Override Volume",
                    "This controller already has an override Volume assigned.", "OK");
                return;
            }

            var child = new GameObject("PostFX_TierOverride");
            Undo.RegisterCreatedObjectUndo(child, "Add Tier Override Volume");
            child.transform.SetParent(go.transform, false);
            child.layer = go.layer;   // the camera's Volume Mask filters by layer

            var vol = Undo.AddComponent<Volume>(child);
            vol.isGlobal = true;
            vol.weight = 1f;
            vol.blendDistance = 0f;

            var baseVol = so.FindProperty("volume").objectReferenceValue as Volume;
            float basePrio = baseVol != null ? baseVol.priority : 0f;
            float prio = so.FindProperty("overridePriority").floatValue;
            if (prio <= basePrio) prio = basePrio + 10f;
            vol.priority = prio;

            so.FindProperty("overridePriority").floatValue = prio;
            so.FindProperty("overrideVolume").objectReferenceValue = vol;
            so.ApplyModifiedProperties();

            Selection.activeGameObject = child;
            EditorGUIUtility.PingObject(child);
        }
    }
}
