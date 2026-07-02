using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;


namespace Bozo.ModularCharacters 
{
    public class OutfitVisibilityToggler : MonoBehaviour
    {
        private Outfit outfit;
        [SerializeField] GameObject toggle;
        List<GameObject> toggles = new List<GameObject>();
        GameObject[] pieces;

        public void Set(Outfit outfit)
        {
            this.outfit = outfit;
            foreach (var item in toggles)
            {
                Destroy(item);
            }
            toggles.Clear();
            pieces = new GameObject[0];

            if (outfit == null) { gameObject.SetActive(false); return; };
            if (outfit.optionalPieces.Length == 0) { gameObject.SetActive(false); return; };

            gameObject.SetActive(true);

            pieces = outfit.optionalPieces;

            for (int i = 0; i < outfit.optionalPieces.Length; i++)
            {
                if (outfit.optionalPieces[i] == null) continue;
                var target = outfit.optionalPieces[i];
                var tog = Instantiate(toggle, transform);
                toggles.Add(tog);

                var text = tog.GetComponentInChildren<TMP_Text>();
                text.text = outfit.optionalPieces[i].name;

                var button = tog.GetComponentInChildren<Toggle>();
                button.isOn = outfit.optionalPieces[i].activeSelf;
                var index = i;
                button.onValueChanged.AddListener(isOn => { onToggle(index, isOn); });
            }
        }

        void onToggle(int index, bool value)
        {
            outfit.SetPartActive(index, value);
        }

        
    }
}
