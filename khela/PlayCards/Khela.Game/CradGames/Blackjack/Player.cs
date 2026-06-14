using CardGames.Platforms;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace CardGames.Blackjack.CardGames.Blackjack
{
    public class Player
    {
        public string Id { get; set; }
        public decimal Balance { get; private set; }

        [JsonInclude]
        public List<PlayerHandState> Hands { get; private set; } = new List<PlayerHandState> { new PlayerHandState() };

        // Backward-compat helpers for single-hand references
        [JsonIgnore]
        public BlackJackHand Hand => Hands.First().Hand;

        [JsonIgnore]
        public decimal Bet => Hands.First().Bet;

        [JsonIgnore]
        public decimal InsuranceBet => Hands.First().InsuranceBet;

        // [JsonInclude] so these survive the Redis round-trip — private setters that aren't
        // constructor params are otherwise dropped on deserialize, resetting stats every round.
        [JsonInclude]
        public int Wins { get; private set; }
        [JsonInclude]
        public int Losses { get; private set; }
        [JsonInclude]
        public int Push { get; private set; }

        // True only while this player is part of the CURRENT round (had a bet at deal time). A
        // player who sits down mid-round stays false and waits for the next deal.
        [JsonInclude]
        public bool InRound { get; set; }

        public string Image { get; private set; } = string.Empty;
        public string Name { get; private set; } = string.Empty;

        [JsonInclude]
        public int SeatNumber { get; set; }

        [JsonInclude]
        public Deck CurrentDeck { get; set; }

        [JsonIgnore]
        public List<Card> Cards => Hands.First().Hand.Cards;

        public Player(string id, decimal balance, string name = "", string image = "", int seatNumber = 0)
        {
            Id = id;
            Balance = balance;
            Image = image;
            Name = name;
            SeatNumber = seatNumber;
        }

        /// <summary>
        /// Overwrites the in-memory balance to mirror the authoritative wallet. The wallet (MySQL)
        /// is the source of truth; this Balance is a display / round-math mirror that the table
        /// layer syncs from the wallet at seat and after each settle.
        /// </summary>
        public void SetBalance(decimal balance) => Balance = balance;

        public void IncreaseBet(decimal amt, int handIndex = 0)
        {
            var hand = GetHand(handIndex);
            if (Balance - (hand.Bet + amt) < 0)
                throw new InvalidOperationException("Not enough balance to increase bet.");
            hand.Bet += amt;
        }

        public PlaceBetResult PlaceBet(int handIndex = 0)
        {
            var hand = GetHand(handIndex);
            if (Balance - hand.Bet < 0)
                throw new InvalidOperationException("Not enough balance to place bet.");

            Balance -= hand.Bet;
            return new PlaceBetResult
            {
                NewBalance = Balance,
                PlacedBet = hand.Bet
            };
        }

        public void ClearBet(int handIndex = 0) => GetHand(handIndex).Bet = 0;

        public void ClearInsurance(int handIndex = 0) => GetHand(handIndex).InsuranceBet = 0;

        public HitResult Hit(int handIndex = 0)
        {
            var card = CurrentDeck.Draw();
            var hand = GetHand(handIndex).Hand;
            hand.Cards.Add(card);
            int handValue = hand.GetSumOfHand();

            return new HitResult
            {
                DrawnCard = card,
                HandValue = handValue,
                IsBust = handValue > 21,
                IsBlackJack = handValue == 21
            };
        }

        public DoubleDownResult DoubleDown(int handIndex = 0)
        {
            var handState = GetHand(handIndex);
            if (handState.Hand.Cards.Count != 2)
                throw new InvalidOperationException("Double down only on first action.");

            IncreaseBet(handState.Bet, handIndex);
            Balance -= handState.Bet / 2; // deduct extra half of bet
            var hitResult = Hit(handIndex);

            handState.Done = true;

            return new DoubleDownResult
            {
                NewBet = handState.Bet,
                NewBalance = Balance,
                HitResult = hitResult
            };
        }

        public void NewHand()
        {
            Hands = new List<PlayerHandState> { new PlayerHandState() };
        }

        public bool HasBlackJack(int handIndex = 0) => GetHand(handIndex).Hand.GetSumOfHand() == 21 && GetHand(handIndex).Hand.Cards.Count == 2;
        public bool HasBust(int handIndex = 0) => GetHand(handIndex).Hand.GetSumOfHand() > 21;

        public bool HasBlackJack() => HasBlackJack(0);
        public bool HasBust() => HasBust(0);

        public void AddWin(decimal payoutMultiplier = 2, int handIndex = 0)
        {
            var hand = GetHand(handIndex);
            Balance += hand.Bet * payoutMultiplier;
            Wins++;
            hand.Bet = 0;
        }

        public void AddWin() => AddWin(2, 0);

        public void AddLoss(int handIndex = 0)
        {
            var hand = GetHand(handIndex);
            Losses++;
            hand.Bet = 0;
        }

        public void AddLoss() => AddLoss(0);

        public void AddPush(int handIndex = 0)
        {
            var hand = GetHand(handIndex);
            Push++;
            Balance += hand.Bet; // return bet
            hand.Bet = 0;
        }

        public void AddPush() => AddPush(0);

        public int Split(int handIndex = 0)
        {
            var handState = GetHand(handIndex);
            if (handState.Hand.Cards.Count != 2)
                throw new InvalidOperationException("Can only split with two cards.");

            var c1 = handState.Hand.Cards[0];
            var c2 = handState.Hand.Cards[1];
            if (c1.FaceVal != c2.FaceVal)
                throw new InvalidOperationException("Cards must be a pair to split.");

            if (Balance < handState.Bet)
                throw new InvalidOperationException("Not enough balance to split.");

            var newHandState = new PlayerHandState
            {
                Bet = handState.Bet
            };

            // Move second card to new hand
            handState.Hand.Cards.Clear();
            handState.Hand.Cards.Add(c1);
            newHandState.Hand.Cards.Add(c2);

            Balance -= handState.Bet; // pay for second hand

            // Draw one card to each hand
            handState.Hand.Cards.Add(CurrentDeck.Draw());
            newHandState.Hand.Cards.Add(CurrentDeck.Draw());

            Hands.Add(newHandState);
            return Hands.Count - 1;
        }

        public void PlaceInsurance(decimal amount, int handIndex = 0)
        {
            var hand = GetHand(handIndex);
            if (amount <= 0) throw new InvalidOperationException("Insurance must be positive.");
            if (amount > hand.Bet / 2) throw new InvalidOperationException("Insurance cannot exceed half the bet.");
            if (Balance < amount) throw new InvalidOperationException("Not enough balance for insurance.");

            Balance -= amount;
            hand.InsuranceBet = amount;
        }

        public void AddInsurancePayout(decimal amount)
        {
            Balance += amount * 2; // 2:1 payout
        }

        public void Stand(int handIndex = 0)
        {
            var hand = GetHand(handIndex);
            hand.Done = true;
        }

        public PlayerHandState GetHand(int handIndex)
        {
            if (handIndex < 0 || handIndex >= Hands.Count)
                throw new ArgumentOutOfRangeException(nameof(handIndex));
            return Hands[handIndex];
        }
    }

    public class PlayerHandState
    {
        [JsonInclude]
        public BlackJackHand Hand { get; set; } = new BlackJackHand();

        [JsonInclude]
        public decimal Bet { get; set; }

        [JsonInclude]
        public decimal InsuranceBet { get; set; }

        [JsonInclude]
        public bool Done { get; set; }
    }

    public class HitResult
    {
        public Card DrawnCard { get; set; }
        public int HandValue { get; set; }
        public bool IsBust { get; set; }
        public bool IsBlackJack { get; set; }
    }

    public class DoubleDownResult
    {
        public decimal NewBet { get; set; }
        public decimal NewBalance { get; set; }
        public HitResult HitResult { get; set; }
    }

    public class PlaceBetResult
    {
        public decimal NewBalance { get; set; }
        public decimal PlacedBet { get; set; }
    }
}
