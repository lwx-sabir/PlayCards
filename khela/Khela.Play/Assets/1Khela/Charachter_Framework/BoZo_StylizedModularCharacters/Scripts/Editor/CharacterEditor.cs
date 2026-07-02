using UnityEditor;
using UnityEngine;

namespace Bozo.ModularCharacters
{
    [CustomEditor(typeof(CharacterObject))]
    public class CharacterEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            CharacterObject myObj = (CharacterObject)target;
            serializedObject.Update();
            float size = 128f;

            EditorGUILayout.PropertyField(serializedObject.FindProperty("icon"));

            if (myObj.icon != null)
            {

                // Define a clean Unity-style frame using GUIStyle
                GUIStyle frameStyle = new GUIStyle(GUI.skin.box);

                frameStyle.padding = new RectOffset(10, 10, 10, 10);
                frameStyle.margin = new RectOffset(5, 5, 5, 5);
                frameStyle.margin = new RectOffset(5, 5, 5, 5);

                GUILayout.BeginHorizontal(frameStyle);
                Rect previewRect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(true));
                EditorGUI.DrawPreviewTexture(previewRect, myObj.icon, null, ScaleMode.ScaleToFit);
                GUILayout.EndHorizontal();
            }

            EditorGUILayout.PropertyField(serializedObject.FindProperty("data"));
            serializedObject.ApplyModifiedProperties();
        }
    }
}
