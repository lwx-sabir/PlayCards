using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Bozo.ModularCharacters
{
    public class DecalController : MonoBehaviour
    {
        public CharacterCreator characterCreator;
        public OutfitSystem system;

        public DecalBaker decalBaker;

        public TMP_Dropdown anchorSelect;
        public TMP_Text indexText;

        public Slider XSlider;
        public Slider YSlider;
        public Slider ZSlider;

        public Slider YawSlider;
        public Slider RollSlider;
        public Slider pitchSlider;

        public Slider ScaleSlider;

        public Texture decal;
        public Color color = Color.white;

        public int DecalIndex;
        public int DecalIndexMax = 1;

        Outfit outfit;



        private void OnEnable()
        {
            characterCreator.onCategoryChanged += Init;
            characterCreator.onCharacterChanged += SetCharacter;
            if (characterCreator.type == null) return;
            
            Init(characterCreator.type.name);
        }

        private void OnDisable()
        {
            characterCreator.onCategoryChanged -= Init;
            characterCreator.onCharacterChanged -= SetCharacter;
        }

        private void SetCharacter(GameObject character)
        {
            if (character == null) return;
            var os = character.GetComponent<OutfitSystem>();
            if (os == null) return;

            system = os;
        }

        private void Init(string type)
        {
            outfit = characterCreator.character.GetOutfit(type);
            if (!outfit) return;

            if (outfit.decalDatas.Count == 0)
            {
                decal = null;
                DecalIndexMax = 0;
                indexText.text = "-/-";
                return;
            }

            DecalIndexMax = outfit.decalDatas.Count;
            DecalIndex = Mathf.Clamp(DecalIndex, 0, DecalIndexMax - 1);
            indexText.text = DecalIndex + 1 + "/" + DecalIndexMax;

            var data = outfit.decalDatas[DecalIndex];
            color = data.color;

            string boneToFind = data.parent;
            int index = anchorSelect.options.FindIndex(option => option.text == boneToFind);

            if (index != -1)
            {
                anchorSelect.SetValueWithoutNotify(index);
                anchorSelect.RefreshShownValue();
            }

            XSlider.value = data.pos.x;
            YSlider.value = data.pos.y;
            ZSlider.value = data.pos.z;

            YawSlider.value = data.yaw;
            RollSlider.value = data.roll;
            pitchSlider.value = data.pitch;
            ScaleSlider.value = data.scale;
        }

        public void SetDecal(Texture texture)
        {
            if (outfit == null) return;

            decal = texture;
            UpdateDecal();
        }

        public void UpdateDecal()
        {
            if (decal == null) return;

            var x = XSlider.value;
            var y = YSlider.value;
            var z = ZSlider.value;
            var anchor = anchorSelect.options[anchorSelect.value].text;
            var data = new DecalData(decal.name, anchor, new Vector3(x, y, z), YawSlider.value, RollSlider.value, pitchSlider.value, ScaleSlider.value, color);
            data.color.a = 1;
            data.decalTexture = decal;
            outfit.SetDecal(data, DecalIndex);
        }

        public void SetColor(Color color)
        {
            this.color = color;
            UpdateDecal();
        }

        public void AddDecal()
        {
            outfit.AddDecal();
            DecalIndexMax++;
            DecalIndex = Mathf.Clamp(DecalIndex, 0, DecalIndexMax -1);
            indexText.text = DecalIndex + 1 + "/" + DecalIndexMax;
        }

        public void RemoveDecal()
        {
            outfit.RemoveDecal();
            //decalBaker.RemoveDecal();
            DecalIndexMax--;
            DecalIndex = Mathf.Clamp(DecalIndex, 0, DecalIndexMax - 1);
            indexText.text = DecalIndex + 1 + "/" + DecalIndexMax;
        }

        public void IndexUp()
        {
            DecalIndex++;
            DecalIndex = Mathf.Clamp(DecalIndex, 0, DecalIndexMax - 1);
            indexText.text = DecalIndex + 1 + "/" + DecalIndexMax;

            Init(outfit.Type.name);

        }

        public void IndexDown()
        {
            DecalIndex--;
            DecalIndex = Mathf.Clamp(DecalIndex, 0, DecalIndexMax - 1);
            indexText.text = DecalIndex + 1 + "/" + DecalIndexMax;

            Init(outfit.Type.name);
        }
    }

}

