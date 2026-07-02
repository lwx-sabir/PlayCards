using UnityEngine;
using UnityEngine.UI;
using TMPro;


namespace Bozo.ModularCharacters
{
    public class BlendSlider : MonoBehaviour 
    {
        Slider slider;
        [SerializeField] TMP_Text title;
        [SerializeField] TMP_Text sliderValue;

        [SerializeField] OutfitSystem system;
        public string shape;

        public void Init(OutfitSystem system, string key) 
        {
            slider = GetComponentInChildren<Slider>();
            slider.onValueChanged.AddListener(Apply);

            this.system = system;
            title.text = key;
            shape = key;
            var weight = system.GetShapeValue(key);
            slider.value = weight;

        }

        private void OnEnable()
        {
            if (system == null) return;
            var weight = system.GetShapeValue(shape);
            slider.value = weight;
        }

        private void UpdateSlider()
        {

        }

        public void Apply(float value) 
        {
            system.SetShape(shape, value);
            sliderValue.text = $"{slider.value}%";
        }
    }
}
