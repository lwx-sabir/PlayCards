using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Bozo.ModularCharacters
{
    [CustomEditor(typeof(Outfit))]
    [CanEditMultipleObjects]
    public class OutfitEditor : Editor
    {
        private bool showColors;
        public override void OnInspectorGUI()
        {
            Outfit outfit = (Outfit)target;
            Color originalColor = GUI.color;
            GUIStyle frameStyle = new GUIStyle(GUI.skin.box);

            serializedObject.Update();


            EditorGUILayout.Space(20);

            GUILayout.Label("Character Creator Settings");
            GUILayout.BeginVertical(frameStyle);
            GUILayout.BeginHorizontal(frameStyle);
            GUILayout.BeginVertical(frameStyle);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("OutfitName"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("OutfitIcon"));

            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ColorChannels"));
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(20);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("material"));
            //EditorGUILayout.PropertyField(serializedObject.FindProperty("materialIndex"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("materialPriority"));
            EditorGUILayout.Space(20);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("TextureCatagory"));
            GUILayout.BeginHorizontal(frameStyle);

            var decalsupport = serializedObject.FindProperty("supportDecals");
            EditorGUILayout.LabelField("Supports Decal", GUILayout.Width(200));
            decalsupport.boolValue = EditorGUILayout.Toggle(decalsupport.boolValue);

            var patternsupport = serializedObject.FindProperty("supportPatterns");
            EditorGUILayout.LabelField("Supports Pattern", GUILayout.Width(200));
            patternsupport.boolValue = EditorGUILayout.Toggle(patternsupport.boolValue);

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal(frameStyle);

            var textureMerge = serializedObject.FindProperty("mergeTexture");
            EditorGUILayout.LabelField("Merge Texture", GUILayout.Width(200));
            textureMerge.boolValue = EditorGUILayout.Toggle(textureMerge.boolValue);

            var meshMerge = serializedObject.FindProperty("mergeMesh");
            EditorGUILayout.LabelField("Merge Mesh", GUILayout.Width(200));
            meshMerge.boolValue = EditorGUILayout.Toggle(meshMerge.boolValue);

            GUILayout.EndHorizontal();

#if MAGICACLOTH2
            //ExtraMaps
            EditorGUILayout.PropertyField(serializedObject.FindProperty("MC2Map"));
#endif
            GUILayout.BeginHorizontal(frameStyle);
            EditorGUILayout.LabelField("Available In Character Creator", GUILayout.Width(200));


            EditorGUILayout.Space(20);

            var buttonText = "";
            if (outfit.showCharacterCreator)
            {
                buttonText = "(Available)";
            }
            else
            {
                GUI.color = Color.yellow;
                buttonText = "(Hidden)";
            }

            if (GUILayout.Button(buttonText))
            {
                Undo.RecordObject(outfit, "Toggle Character Creator");

                outfit.showCharacterCreator = !outfit.showCharacterCreator;

                EditorUtility.SetDirty(outfit);
                PrefabUtility.RecordPrefabInstancePropertyModifications(outfit);
            }

            GUI.color = originalColor;

            GUILayout.EndHorizontal();

            GUILayout.EndVertical();

            if (outfit.OutfitIcon != null)
            {

                // Control the size of the image
                float size = 128;
                Rect rect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));
                EditorGUI.DrawTextureTransparent(rect, outfit.OutfitIcon.texture);
            }

            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            EditorGUILayout.Space(20);

            GUILayout.Label("Outfit Settings");
            GUILayout.BeginVertical(frameStyle);

            EditorGUILayout.PropertyField(serializedObject.FindProperty("Type"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("AttachPoint"));

            GUILayout.BeginHorizontal(frameStyle);

            var customShader = serializedObject.FindProperty("customShader");
            EditorGUILayout.LabelField("Uses Custom Shader", GUILayout.Width(200));
            customShader.boolValue = EditorGUILayout.Toggle(customShader.boolValue);

            var attachEditMode = serializedObject.FindProperty("AttachInEditMode");
            EditorGUILayout.LabelField("Follow Skeleton In Edit Mode", GUILayout.Width(200));
            attachEditMode.boolValue = EditorGUILayout.Toggle(attachEditMode.boolValue);

            GUILayout.EndHorizontal();

            EditorGUI.indentLevel++;
            EditorGUI.indentLevel++;

            showColors = EditorGUILayout.Foldout(showColors, "Color Properties", true);
            if (showColors)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("colors"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("defaultColors"));


                GUILayout.BeginHorizontal(frameStyle);

                GUILayout.Label("Pattern", GUILayout.Width(80));
                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty("pattern"),
                    GUIContent.none,
                    GUILayout.MinWidth(80)
                );

                GUILayout.Label("Pattern Size", GUILayout.Width(80));
                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty("patternSize"),
                    GUIContent.none,
                    GUILayout.MinWidth(50)
                );
                GUILayout.EndHorizontal();
                EditorGUILayout.PropertyField(serializedObject.FindProperty("patternColors"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("decalDatas"));

            }
            EditorGUI.indentLevel--;

            EditorGUILayout.PropertyField(serializedObject.FindProperty("optionalPieces"));

            EditorGUILayout.PropertyField(serializedObject.FindProperty("additionalBones"));

            EditorGUILayout.PropertyField(serializedObject.FindProperty("tags"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("categories"));

            EditorGUILayout.PropertyField(serializedObject.FindProperty("LinkedColorSets"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("IncompatibleSets"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("HideTypes"));
            EditorGUI.indentLevel--;

            GUILayout.EndVertical();

            serializedObject.ApplyModifiedProperties();

            //base.OnInspectorGUI();
        }
    }
}
