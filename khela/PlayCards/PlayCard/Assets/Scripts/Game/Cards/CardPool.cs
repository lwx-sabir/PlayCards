using System.Collections.Generic;
using UnityEngine;

namespace PlayCard.Game.Cards
{
    /// <summary>
    /// Reuses <see cref="CardVisual"/> instances instead of instantiating/destroying on every board
    /// update. Per render: call <see cref="Recycle"/> to reclaim everything, then <see cref="Rent"/>
    /// one card per card on the board. Cards left over from a previously larger board are deactivated.
    /// </summary>
    public sealed class CardPool
    {
        private readonly CardVisual _prefab;
        private readonly Transform _root;
        private readonly Stack<CardVisual> _free = new Stack<CardVisual>();
        private readonly List<CardVisual> _active = new List<CardVisual>();

        public CardPool(CardVisual prefab, Transform root)
        {
            _prefab = prefab;
            _root = root;
        }

        /// <summary>Reclaim every live card back into the pool (deactivated) before a fresh layout.</summary>
        public void Recycle()
        {
            foreach (var card in _active)
            {
                card.gameObject.SetActive(false);
                card.transform.SetParent(_root, false);
                _free.Push(card);
            }
            _active.Clear();
        }

        /// <summary>Get a card parented under <paramref name="parent"/>, activated and ready to set.</summary>
        public CardVisual Rent(Transform parent)
        {
            CardVisual card = _free.Count > 0 ? _free.Pop() : Object.Instantiate(_prefab, _root);
            card.transform.SetParent(parent, false);
            card.gameObject.SetActive(true);
            _active.Add(card);
            return card;
        }
    }
}
