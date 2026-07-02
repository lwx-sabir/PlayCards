using UnityEditor;
using UnityEngine;

namespace Bozo.ModularCharacters
{
    [CustomEditor(typeof(OutfitSystem))]
    public class OutfitSystemEditor : Editor
    {
        private bool showMergedOptions;
        private bool dependencies;
        private bool showDebug;
        private Texture2D banner;

        private void OnEnable()
        {
            banner = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/BoZo_StylizedModularCharacters/Textures/Editor/Banner_OutfitSystem.png");
        }
        public override void OnInspectorGUI()
        {
            OutfitSystem system = (OutfitSystem)target;
            Color originalColor = GUI.color;


            GUIStyle frameStyle = new GUIStyle(GUI.skin.box);

            serializedObject.Update();

            if (banner != null)
            {
                float maxWidth = EditorGUIUtility.currentViewWidth - 20;
                float aspect = (float)banner.width / banner.height;
                float desiredWidth = Mathf.Min(banner.width, maxWidth);
                float height = desiredWidth / aspect;

                // Center it using flexible space
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label(banner, GUILayout.Width(desiredWidth), GUILayout.Height(height));
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal(frameStyle);
            GUILayout.BeginVertical();

            EditorGUILayout.PropertyField(serializedObject.FindProperty("characterData"));

            EditorGUILayout.PropertyField(serializedObject.FindProperty("SaveID"));

            GUILayout.BeginHorizontal();

            EditorGUILayout.PropertyField(serializedObject.FindProperty("loadMode"), GUILayout.ExpandWidth(true));

            var async = serializedObject.FindProperty("async");
            EditorGUILayout.LabelField("Async", GUILayout.Width(50));
            async.boolValue = EditorGUILayout.Toggle(async.boolValue, GUILayout.Width(50));

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save Character ID"))
            {
                system.SaveByID();
            }
            if (GUILayout.Button("Load Character ID"))
            {
                system.LoadFromID();
            }

            GUILayout.EndHorizontal();



            GUILayout.EndVertical();

            if (system.characterData)
            {
                if (system.characterData.GetCharacterIcon() != null)
                {
                    float size = 64;
                    Rect rect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));
                    EditorGUI.DrawTextureTransparent(rect, system.characterData.GetCharacterIcon());
                }
            }
            GUILayout.EndHorizontal();


            //MERGED OPTIONS
            showMergedOptions = EditorGUILayout.Foldout(showMergedOptions, "Advanced Options", true);

            GUILayout.BeginVertical(frameStyle);
            if (showMergedOptions)
            {
                EditorGUI.indentLevel++;

                EditorGUILayout.PropertyField(serializedObject.FindProperty("mergeMaterial"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("prefabName"));

                EditorGUILayout.PropertyField(serializedObject.FindProperty("materialData"));

                GUILayout.BeginHorizontal(frameStyle);

                if (GUILayout.Button("Merge Character"))
                {
                    system.MergeCharacter();
                }
                if (GUILayout.Button("Save as Prefab"))
                {
                    system.SaveCharacterToPrefab();
                }
                GUILayout.EndHorizontal();
                EditorGUILayout.PropertyField(serializedObject.FindProperty("cleanAfterMerge"));
                EditorGUI.indentLevel--;
            }
            GUILayout.EndVertical();


            //EditorGUILayout.PropertyField(serializedObject.FindProperty("data"));

            dependencies = EditorGUILayout.Foldout(dependencies, "Dependencies", true);
            if (dependencies)
            {
                GUILayout.BeginHorizontal(frameStyle);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("CharacterBody"));
                GUILayout.EndHorizontal();
            }

            showDebug = EditorGUILayout.Foldout(showDebug, "Debug", true);
            if (showDebug)
            {
                if (GUILayout.Button("Mute Body Mods"))
                {
                    system.MuteBodyMods();
                }
                if (GUILayout.Button("Mute Height Changes"))
                {
                    //system.MergeCharacter();
                }
                if (GUILayout.Button("Force Bind Pose"))
                {
                    system.ForceBindPose();
                }

                EditorGUILayout.PropertyField(serializedObject.FindProperty("copyMaterial"));

                EditorGUILayout.Space(10);
                EditorGUILayout.LabelField("BoneMap", EditorStyles.boldLabel);

                if (system.boneMap == null || system.boneMap.Count == 0)
                {
                    EditorGUILayout.HelpBox("Dictionary is empty.", MessageType.Info);
                }

                foreach (var pair in system.boneMap)
                {
                    EditorGUILayout.BeginHorizontal();

                    EditorGUILayout.LabelField(pair.Key, GUILayout.Width(150));
                    EditorGUILayout.LabelField(pair.Value.name.ToString());

                    EditorGUILayout.EndHorizontal();
                }
            }

            if (system.isStatic)
            {
                EditorGUILayout.HelpBox("This System is set to static. This happened because you either saved it as Prefab or you have -Clean After Merging- ON. This System can no longer swap outfits", MessageType.Warning);
            }

            if (system.animator)
            {

                if(system.animator.hasRootMotion) EditorGUILayout.HelpBox("Animator has Root Motion enabled. Height Changes are disabled", MessageType.Warning);
            }


            serializedObject.ApplyModifiedProperties();
        }


    }
}
