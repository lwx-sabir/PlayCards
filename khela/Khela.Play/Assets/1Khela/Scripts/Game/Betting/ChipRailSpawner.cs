using System.Collections.Generic;
using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using UnityEngine;

namespace PlayCard.Game.Betting
{
    /// <summary>
    /// Builds the betting chip rail for the current table. On the first board (and whenever the table's
    /// min/max bet changes) it asks the <see cref="ChipSet"/> for the denominations that fit [minBet, maxBet],
    /// instantiates the matching colour-rank prefab for each, prints the value via <see cref="ChipView"/>, and
    /// lays them in a centered row under <see cref="railAnchor"/>.
    ///
    /// Put <see cref="railAnchor"/> (or this object) in <c>TableModeVisibility ▸ Hide In Play</c> so the chips
    /// only show while betting. The spawned chips are dragged onto the bet spot by <see cref="ChipDragController"/>.
    /// </summary>
    public sealed class ChipRailSpawner : MonoBehaviour
    {
        [SerializeField] private TableController table;
        [SerializeField] private ChipSet chipSet;
        [Tooltip("Parent the chips line up under. The row is laid along this transform's local +X, centered.")]
        [SerializeField] private Transform railAnchor;
        [Tooltip("Spacing between chips along the rail (local units).")]
        [SerializeField] private float spacing = 0.12f;
        [Tooltip("How many chips to show — the rule shows the lowest N denominations that fit the table.")]
        [SerializeField] private int maxChips = 5;

        private readonly List<GameObject> _spawned = new List<GameObject>();
        private decimal _min = -1m, _max = -1m;
        private bool _built;

        private void OnEnable()
        {
            if (table == null) return;
            table.OnBoardChanged += OnBoard;
            if (table.Board != null) OnBoard(table.Board);   // board may have arrived before we enabled
        }

        private void OnDisable()
        {
            if (table != null) table.OnBoardChanged -= OnBoard;
        }

        private void OnBoard(BoardSnapshot board)
        {
            if (board == null || chipSet == null || railAnchor == null) return;
            if (_built && board.MinBet == _min && board.MaxBet == _max) return;   // unchanged → keep the rail
            _min = board.MinBet;
            _max = board.MaxBet;
            _built = true;
            Rebuild();
        }

        private void Rebuild()
        {
            Clear();
            var values = chipSet.Values(_min, _max);                       // minBet × multipliers, ≤ maxBet
            if (values.Count > maxChips) values = values.GetRange(0, maxChips);
            var prefabs = chipSet.LevelPrefabs;
            float mid = (values.Count - 1) * 0.5f;

            for (int i = 0; i < values.Count; i++)
            {
                var prefab = (prefabs != null && i < prefabs.Count) ? prefabs[i] : null;
                if (prefab == null) continue;   // not enough colour ranks authored for this many chips

                var go = Instantiate(prefab, railAnchor);
                go.transform.localPosition = new Vector3(spacing * (i - mid), 0f, 0f);
                go.transform.localRotation = Quaternion.identity;

                var chip = go.GetComponentInChildren<ChipView>();
                if (chip != null) chip.SetValue(values[i]);

                _spawned.Add(go);
            }

            if (values.Count == 0)
                Debug.LogWarning($"[ChipRailSpawner] no chips for [min={_min}, max={_max}] — min bet is 0 or every multiplier exceeds the max. Check the ChipSet multipliers.");
            else if (values.Count < maxChips)
                Debug.Log($"[ChipRailSpawner] showing {values.Count} chips (some multipliers × min={_min} exceed max={_max}).");
        }

        private void Clear()
        {
            for (int i = 0; i < _spawned.Count; i++)
                if (_spawned[i] != null) Destroy(_spawned[i]);
            _spawned.Clear();
        }
    }
}
