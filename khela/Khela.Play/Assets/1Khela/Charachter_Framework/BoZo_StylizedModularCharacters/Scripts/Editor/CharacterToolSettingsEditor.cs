using UnityEditor;
using UnityEngine;


namespace Bozo.ModularCharacters
{
#if UNITY_EDITOR
    [CustomEditor(typeof(CharacterToolSettings))]
    public class CharacterToolSettingsEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.LabelField(
                "Character Tool Settings",
                EditorStyles.boldLabel);

            EditorGUILayout.PropertyField(serializedObject.FindProperty("_prefabFolder"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_iconFolder"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_saveDataFolder"));

            EditorGUILayout.LabelField(
            "Persistant Path Settings",
            EditorStyles.boldLabel);

            EditorGUILayout.PropertyField(serializedObject.FindProperty("PersistantPath"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("PersistantIconPath"));

            serializedObject.ApplyModifiedProperties();
        }
    }

    public static class CharacterToolMenu
    {
        [MenuItem("Tools/BoZo Tools/Settings")]
        static void OpenSettings()
        {
            Selection.activeObject =
                CharacterToolSettingsProvider.Get();
        }
    }
#endif
}
