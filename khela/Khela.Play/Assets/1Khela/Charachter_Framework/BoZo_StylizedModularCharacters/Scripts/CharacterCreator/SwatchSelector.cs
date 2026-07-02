using UnityEngine;
using UnityEngine.UI;


namespace Bozo.ModularCharacters
{
    public class SwatchSelector : MonoBehaviour
    {
        ColorPickerControl picker;
        public int swatchIndex;
        //these are for setting the swatch icon colors
        public Image color1;
        public Image color2;

        public void Init(ColorPickerControl colorPickerControl, OutfitSwatch swatchData, int swatchIndex)
        {
            picker = colorPickerControl;
            this.swatchIndex = swatchIndex;
            color1.color = swatchData.IconColorTop;
            color2.color = swatchData.IconColorBottom;
        }

        public void SetSwatch()
        {
            picker.SetBaseTexture(swatchIndex);
        }
    }
}
