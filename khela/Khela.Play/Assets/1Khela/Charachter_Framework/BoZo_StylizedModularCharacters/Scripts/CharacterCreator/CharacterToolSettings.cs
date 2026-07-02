using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Bozo.ModularCharacters
{
#if UNITY_EDITOR
    public class CharacterToolSettings : ScriptableObject
    {
        [Header("Save Locations")]

        public DefaultAsset _prefabFolder;
        public string prefabFolder { get { return AssetDatabase.GetAssetPath(_prefabFolder); } }

        public DefaultAsset _saveDataFolder;
        public string saveDataFolder { get { return AssetDatabase.GetAssetPath(_saveDataFolder); } }

        public DefaultAsset _iconFolder;
        public string iconFolder { get { return AssetDatabase.GetAssetPath(_iconFolder); } }

        public string PersistantPath = "CustomCharacters";
        public string PersistantIconPath = "CustomCharacters/Icons";
    }
#endif
}
