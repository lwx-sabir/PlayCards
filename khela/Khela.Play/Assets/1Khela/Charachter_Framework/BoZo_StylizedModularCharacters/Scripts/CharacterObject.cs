using UnityEngine;

namespace Bozo.ModularCharacters
{

    [CreateAssetMenu(fileName = "BSMC_CharacterObject", menuName = "BoZo/BSMC_CharacterObject")]
    public class CharacterObject : DataObject
    {
        public Texture2D icon;
        public CharacterData data;

        public override CharacterData GetCharacterData()
        {
            return data;
        }

        public override Texture2D GetCharacterIcon()
        {
            return icon;
        }
    }
}
