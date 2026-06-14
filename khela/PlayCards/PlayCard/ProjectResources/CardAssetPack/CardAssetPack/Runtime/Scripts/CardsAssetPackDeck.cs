using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CardsAssetPackDeck : MonoBehaviour
{
    // Start is called before the first frame update
    public GameObject CardTemplate;
    public Texture BackSprite;
    public List<CardAssetPackCard> cards = new List<CardAssetPackCard>();

    void Start()
    {
        InitDeck();
    }
    public void ResetDeck()
    {
        DestroyCards();
        InitDeck();

    }
    public void DestroyCards()
    {
        while (cards.Count > 0)
        {
            cards.RemoveAt(0);
        }

    }
    public void InitDeck() {
        foreach (CardAssetPackCard.Suit suit in CardAssetPackCard.Suit.GetValues(typeof(CardAssetPackCard.Suit)))
        {
            for (int i = 2; i < 15; i++)
            {
                var card = new CardAssetPackCard(i, suit);
                cards.Add(card);
            }
        }

    }
    public CardAssetPackCard DrawCard()
    {
        //pops card from deck
        if (cards.Count == 0)
        {
            return null;
        }
        var temp = cards[0];
        cards.Remove(temp);

        return temp;
    }
    public CardInstance DrawAndCreateCard()
    {
        CardAssetPackCard card = DrawCard();
        CardInstance cardInstance = null;
        if (card != null)
        {
            Debug.Log("Created " + card.getSuit() + " " + card.getValue());
             cardInstance = Instantiate(CardTemplate).GetComponent<CardInstance>();
            cardInstance.SetCardValues(card, BackSprite);
        }
        else
        {
            Debug.Log("Deck Empty");
        }
        return cardInstance;
    }
    public void CreateCard()
    {
        DrawAndCreateCard();
    }
    public void Shuffle()
    {
        Shuffle(cards);

    }
    public static void Shuffle<T>(List<T> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            swap(i, Random.Range(i, list.Count), list);

        }
    }

    public static void swap<T>(int a, int b, List<T> list)
    {
        var temp = list[a];
        list[a] = list[b];
        list[b] = temp;
    }
}
