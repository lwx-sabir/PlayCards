using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CardInstance : MonoBehaviour
{
    // Start is called before the first frame update
    private CardAssetPackCard card;
    public GameObject cardMesh;

    Texture BackSprite;
    public void SetCardValues(CardAssetPackCard card,Texture texture)
    {
        this.card = card;
        this.BackSprite = texture;
        updateGraphics();
    }
    public CardAssetPackCard getCardValues()
    {
        return card;
    }
    public void updateGraphics()
    {
        Material[] matReference = new Material[2];
        matReference = cardMesh.GetComponent<Renderer>().materials;
        matReference[0].SetTextureOffset("_MainTex",  new Vector2((card.getValue()-2) * (1f / 13), -card.suitToInt()*(1f/5)));
        matReference[1].mainTexture = BackSprite;
        cardMesh.GetComponent<Renderer>().materials = matReference;
    }


}
