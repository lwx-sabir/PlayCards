using UnityEngine;


namespace Bozo.ModularCharacters
{
    public abstract class DataObject : ScriptableObject
    {

        public virtual CharacterData GetCharacterData()
        {
            return null;
        }

        public virtual Texture2D GetCharacterIcon()
        {
            return null;
        }
    }
}
