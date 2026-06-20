using System.Collections.Generic;
using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Shows/hides groups of HUD objects based on the table's mode — BETTING (no round in progress) vs
    /// PLAY (round in progress). Put this on the HUD Canvas and fill the two lists in the inspector:
    ///   • <see cref="hideInPlay"/>    — hidden once a round starts; shown while betting (e.g. Deal, Repeat, bet field).
    ///   • <see cref="hideInBetting"/> — hidden while betting; shown once a round starts (e.g. Hit, Stand, Double, Split).
    /// Mode is read from the live board (<c>RoundInProgress</c>) via <see cref="TableController"/>. View-only —
    /// no game logic. This toggles whole objects on/off; it's independent of the per-turn enable/disable gating
    /// that <see cref="TableActionBar"/> already does. Every field is null-tolerant.
    /// </summary>
    public sealed class TableModeVisibility : MonoBehaviour
    {
        [Header("Source")]
        [SerializeField] private TableController table;

        [Header("Groups (GameObjects / buttons)")]
        [Tooltip("Hidden while a round is in progress; shown while betting — e.g. Deal, Repeat, the bet input.")]
        [SerializeField] private List<GameObject> hideInPlay = new List<GameObject>();

        [Tooltip("Hidden while betting; shown once a round is in progress — e.g. Hit, Stand, Double, Split.")]
        [SerializeField] private List<GameObject> hideInBetting = new List<GameObject>();

        private void OnEnable()
        {
            if (table == null) { Debug.LogWarning("[TableModeVisibility] No TableController assigned."); return; }
            table.OnBoardChanged += Apply;
            Apply(table.Board);
        }

        private void OnDisable()
        {
            if (table != null) table.OnBoardChanged -= Apply;
        }

        private void Apply(BoardSnapshot board)
        {
            bool inPlay = board != null && board.RoundInProgress;
            SetAll(hideInPlay, !inPlay);    // betting-only objects: visible only when NOT in a round
            SetAll(hideInBetting, inPlay);  // play-only objects:   visible only when in a round
        }

        private static void SetAll(List<GameObject> list, bool active)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null) list[i].SetActive(active);
        }
    }
}
