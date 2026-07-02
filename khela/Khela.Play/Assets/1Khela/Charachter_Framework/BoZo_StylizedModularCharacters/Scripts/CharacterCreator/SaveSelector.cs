using UnityEngine;
using UnityEngine.UI;

namespace Bozo.ModularCharacters
{
    public class SaveSelector : MonoBehaviour
    {
        public Image icon;
        public CharacterData data;
        private CharacterCreator characterCreator;
        private Button button;
        public void Init(CharacterData characterData, Sprite icon, CharacterCreator characterCreator)
        {
            button = GetComponentInChildren<Button>();
            button.onClick.AddListener(OnSelect);

            this.data = characterData;
            this.characterCreator = characterCreator;
            this.icon.overrideSprite = icon;
        }

        private void OnSelect()
        {
            characterCreator.LoadCharacter(data);
        }
    }
}
