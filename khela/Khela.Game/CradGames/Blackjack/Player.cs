using CardGames.Platforms;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace CardGames.Blackjack
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

        // True once this player has insured OR declined during the insurance phase — lets the table close the
        // insurance window early when everyone eligible has decided. Reset each deal.
        [JsonInclude]
        public bool InsuranceDecided { get; set; }

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

            // Stake an ADDITIONAL bet equal to the original (total 2x), then draw exactly one card.
            // Funds are enforced authoritatively at the wallet boundary (the server debits the extra stake
            // before this runs); Balance here is a display mirror, so there is no balance guard — a stale
            // mirror must never reject (and strand) a stake the wallet already accepted.
            var extra = handState.Bet;
            Balance -= extra;
            handState.Bet += extra;
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

        /// <summary>
        /// Blackjack split value of a single card: any 10-value card (10/J/Q/K) is 10, an Ace is 11, others
        /// their pip value. Used to decide a splittable pair — casino-standard "equal value" lets any two
        /// 10-value cards (e.g. K+Q) split, while keeping rank pairs (7+7) and aces splittable.
        /// </summary>
        public static int SplitValue(Card c) =>
            c.FaceVal == FaceValue.Ace ? 11 :
            (c.FaceVal == FaceValue.Jack || c.FaceVal == FaceValue.Queen || c.FaceVal == FaceValue.King) ? 10 :
            (int)c.FaceVal;

        /// <summary>Whether two cards form a splittable pair (equal blackjack value).</summary>
        public static bool CanSplitPair(Card a, Card b) => SplitValue(a) == SplitValue(b);

        public int Split(int handIndex = 0)
        {
            var handState = GetHand(handIndex);
            if (handState.Hand.Cards.Count != 2)
                throw new InvalidOperationException("Can only split with two cards.");

            var c1 = handState.Hand.Cards[0];
            var c2 = handState.Hand.Cards[1];
            if (!CanSplitPair(c1, c2))
                throw new InvalidOperationException("Cards must be a pair (equal value) to split.");

            // Funds enforced at the wallet boundary (server debits the second stake before this runs);
            // Balance is a display mirror, so no balance guard (a stale mirror must not strand a stake).
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

            // Split aces get exactly one card each and cannot be hit/doubled/re-split (standard rule). Lock
            // both hands so the turn engine skips them and the action endpoints reject further play. A
            // resulting 21 is an ordinary 21 (pays 1:1), not a natural — settlement already enforces that.
            if (c1.FaceVal == FaceValue.Ace)
            {
                handState.Done = true;
                newHandState.Done = true;
            }

            Hands.Add(newHandState);
            return Hands.Count - 1;
        }

        public void PlaceInsurance(decimal amount, int handIndex = 0)
        {
            var hand = GetHand(handIndex);
            if (amount <= 0) throw new InvalidOperationException("Insurance must be positive.");
            if (amount > hand.Bet / 2) throw new InvalidOperationException("Insurance cannot exceed half the bet.");
            // Funds enforced at the wallet boundary (server debits before this runs); no balance guard.

            Balance -= amount;
            hand.InsuranceBet = amount;
        }

        public void AddInsurancePayout(decimal amount)
        {
            // Insurance pays 2:1. The stake was deducted when placed, so return the stake plus twice
            // it (total 3x) — a net +2x the insurance bet when the dealer has blackjack.
            Balance += amount * 3;
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

        /// <summary>
        /// Wallet transaction id(s) of the stake debit(s) that funded THIS hand — the deal/split stake, with a
        /// double-down debit appended (comma-separated) if doubled. Set by the table layer at debit time and
        /// round-tripped through Redis so settle can record a per-hand audit trail. Null until a stake is taken.
        /// </summary>
        [JsonInclude]
        public string StakeTxId { get; set; }
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
