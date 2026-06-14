using CardGames.Platforms;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CardGames.Blackjack
{
    /// <summary>
    /// This class is game-specific.  Creates a BlackJack hand that inherits from class hand
    /// </summary>
    public class BlackJackHand : Hand
    {
        /// <summary>
        /// This method compares two BlackJack hands
        /// </summary>
        /// <param name="otherHand"></param>
        /// <returns></returns>
        public int CompareFaceValue(BlackJackHand otherHand)
        {
            return this.GetSumOfHand().CompareTo(otherHand.GetSumOfHand());
        }

        /// <summary>
        /// Gets the total value of a hand from BlackJack values
        /// </summary>
        /// <returns>int</returns>
        public int GetSumOfHand()
        {
            return GetSumOfHand(out _);
        }

        /// <summary>
        /// Gets the total value of a hand and whether it is soft (has an Ace counted as 11).
        /// </summary>
        public int GetSumOfHand(out bool isSoft)
        {
            int val = 0;
            int numAces = 0;

            foreach (Card c in Cards)
            {
                if (c.FaceVal == FaceValue.Ace)
                {
                    numAces++;
                    val += 11;
                }
                else if (c.FaceVal == FaceValue.Jack || c.FaceVal == FaceValue.Queen || c.FaceVal == FaceValue.King)
                {
                    val += 10;
                }
                else
                {
                    val += (int)c.FaceVal;
                }
            }

            while (val > 21 && numAces > 0)
            {
                val -= 10;
                numAces--;
            }

            isSoft = numAces > 0;
            return val;
        }

        /// <summary>
        /// Hand value counting ONLY face-up cards (Ace-aware). Lets the board show the dealer's
        /// total while the hole card is hidden, without leaking the down card's value.
        /// </summary>
        public int GetVisibleSum()
        {
            int val = 0;
            int numAces = 0;

            foreach (Card c in Cards)
            {
                if (!c.IsCardUp) continue;

                if (c.FaceVal == FaceValue.Ace) { numAces++; val += 11; }
                else if (c.FaceVal == FaceValue.Jack || c.FaceVal == FaceValue.Queen || c.FaceVal == FaceValue.King) val += 10;
                else val += (int)c.FaceVal;
            }

            while (val > 21 && numAces > 0) { val -= 10; numAces--; }
            return val;
        }
    }
}
