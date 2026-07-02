using UnityEngine;
using UnityEngine.UI;

namespace Bozo.ModularCharacters
{
    public class OutfitSelector : MonoBehaviour 
    {
        public Image icon;
        private Outfit outfit;
        private CharacterCreator characterCreator;   
        private Button button;
        public void Init(Outfit outfit, CharacterCreator characterCreator)
        {
            //icon = GetComponentInChildren<Image>();
            button = GetComponentInChildren<Button>();
            button.onClick.AddListener(OnSelect);

            this.outfit = outfit;
            this.characterCreator = characterCreator;
            icon.overrideSprite = this.outfit.OutfitIcon;
        }

        private void OnSelect() 
        {
            characterCreator.SetOutfit(outfit);
        }

        public void SetVisable(string type) 
        {
            if (outfit.Type == null)
            {
               gameObject.SetActive(false);
               Debug.LogWarning(outfit.name + " is missing an outfitType and will not show in the character creator");
               return;
            }

            if(outfit.Type.name == type) 
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
