using UnityEngine;
using UnityEngine.UI;

namespace Bozo.ModularCharacters
{
    public class TextureSelector : MonoBehaviour
    {
        public Image icon;
        private TexturePackage texture;
        private CharacterCreator characterCreator;
        private Button button;
        public void Init(TexturePackage texture, CharacterCreator characterCreator)
        {
            button = GetComponentInChildren<Button>();
            button.onClick.AddListener(OnSelect);

            this.texture = texture;
            this.characterCreator = characterCreator;
            icon.overrideSprite = texture.icon;
        }

        private void OnSelect()
        {
            if (texture.type == TextureType.Decal)
            {
                characterCreator.decalController.SetDecal(texture.texture);
            }
            if (texture.type == TextureType.Pattern)
            {
                var c = characterCreator.GetCurrentCatagory();
                characterCreator.character.GetOutfit(c).SetPattern(texture.texture);
            }

        }

        public void SetVisable(string type)
        {
            if (texture.catagory == type)
            {
                gameObject.SetActive(true);
            }
            else
            {
                gameObject.SetActive(false);
            }
        }
    }

}
