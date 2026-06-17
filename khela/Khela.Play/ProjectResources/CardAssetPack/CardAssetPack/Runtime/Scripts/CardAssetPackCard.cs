using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CardAssetPackCard 
{

    //Data container class for cards
    int value;
    Suit suit;
    public int getValue()
    {
        return value;
    }
    public enum Suit {HEARTS,SPADES,CLUBS,DIAMONDS }



    public CardAssetPackCard(int value, Suit suit)
    {

        this.value = value;
        this.suit = suit;
    }
    public Suit getSuit()
    {
        return suit;
    }
    public int suitToInt()
    {
        switch (suit)
        {
            case Suit.HEARTS:
                return 0;
            case Suit.SPADES:
                return 1;
            case Suit.CLUBS:
                return 2;
            case Suit.DIAMONDS:
                return 3;
                
        }
        return 0;

    }

}
