using UnityEngine;

namespace Bozo.ModularCharacters
{
public enum TextureType {Base, Decal, Pattern}
    public class TexturePackage : MonoBehaviour
    {
        public Texture texture;
        //public Texture normalTexture;
        public Sprite icon;
        public Color[] colors;
        public Vector2 maxScale = new Vector2(-1,-1);
        public TextureType type;
        public string catagory;
    }
}
