using UnityEditor;
using UnityEngine;


namespace Bozo.ModularCharacters
{
#if UNITY_EDITOR
    public static class CharacterToolSettingsProvider
    {
        private static CharacterToolSettings settings;

        public static CharacterToolSettings Get()
        {
            if (settings != null)
                return settings;

            // Find existing settings
            string[] guids =
                AssetDatabase.FindAssets("t:Bozo.ModularCharacters.CharacterToolSettings");

            if (guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);

                settings =
                    AssetDatabase.LoadAssetAtPath<CharacterToolSettings>(path);

                return settings;
            }

            // Create if missing
            settings = ScriptableObject.CreateInstance<CharacterToolSettings>();

            AssetDatabase.CreateAsset(
                settings,
                "Assets/CharacterToolSettings.asset");

            AssetDatabase.SaveAssets();

            Debug.Log("Created Character Tool Settings");

            return settings;
        }
    }
#endif
}
