using System.Collections.Generic;
using UnityEngine;

namespace PlayCard.Game.Cards
{
    /// <summary>
    /// Free-list of <see cref="CardVisual"/> instances so the table view can reuse cards instead of
    /// instantiating/destroying on every board update. <see cref="PlayCard.Game.Table.BlackjackTableView"/>
    /// owns the active set (it diffs the board frame-to-frame); the pool just hands out (<see cref="Rent"/>)
    /// and reclaims (<see cref="Release"/>) instances.
    /// </summary>
    public sealed class CardPool
    {
        private readonly CardVisual _prefab;
        private readonly Transform _root;
        private readonly Stack<CardVisual> _free = new Stack<CardVisual>();

        public CardPool(CardVisual prefab, Transform root)
        {
            _prefab = prefab;
            _root = root;
        }

        /// <summary>Get a card parented under <paramref name="parent"/>, activated and ready to set.</summary>
        public CardVisual Rent(Transform parent)
        {
            var card = _free.Count > 0 ? _free.Pop() : Object.Instantiate(_prefab, _root);
            card.transform.SetParent(parent, false);
            card.gameObject.SetActive(true);
            return card;
        }

        /// <summary>Return a card to the pool (deactivated, reparented to the pool root).</summary>
        public void Release(CardVisual card)
        {
            if (card == null) return;
            card.gameObject.SetActive(false);
            card.transform.SetParent(_root, false);
            _free.Push(card);
        }
    }
}
