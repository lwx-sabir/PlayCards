using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace CardGames.Platforms
{
    public class Hand
    { 
        public int NumCards { get { return Cards.Count; } }

        [JsonInclude]
        public List<Card> Cards { get; private set; } = new List<Card>();

        public Hand() { }

        /// <summary>
        /// Checks to see if the hand contains a card of a certain face value
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public bool ContainsCard(FaceValue item)
        {
            foreach (Card c in Cards)
            {
                if (c.FaceVal == item)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
