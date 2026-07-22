using PlayCard.Game.Dtos;
using PlayCard.Game.Table;
using UnityEngine;

namespace PlayCard.UI
{
    /// <summary>
    /// Swaps the HUD layout to match the seat the local player is in. You author ONE layout per seat (each a
    /// GameObject holding that view's player cards, positioned by hand, with its own <see cref="SeatPlates"/>
    /// driver). This enables the layout for the local player's current seat and disables the rest — so each
    /// seat-view looks exactly how you placed it, no projection or per-frame math. Put this on a persistent HUD
    /// object (not inside any layout).
    ///
    /// PREVIEW: tick <see cref="preview"/> to force the SEATED seat's layout ON with no live board/player — so you can
    /// eyeball your seat-view in the editor / a dev build. The seat is taken AUTOMATICALLY from where you sit
    /// (<see cref="TableController.MySeat"/> — the live seat, else the lobby/debug pick), falling back to seat 1 so edit
    /// mode always shows something. IGNORED in a release build (which always follows the real seat).
    /// </summary>
    [ExecuteAlways]
    public sealed class SeatLayoutSwitcher : MonoBehaviour
    {
        [SerializeField] private TableController table;
        [Tooltip("One layout GameObject per seat — element 0 = local seat 1, element 1 = seat 2, … " +
                 "Each holds that view's hand-placed cards + a SeatPlates driver.")]
        [SerializeField] private GameObject[] layoutsByLocalSeat;

        [Header("Preview (dev only — ignored in a release build)")]
        [Tooltip("ON = force the layout for the seat you're SITTING in to show with no live board/player, so you can " +
                 "eyeball it in the editor / dev play. The seat is auto-picked from MySeat (else the lobby/debug seat, " +
                 "else seat 1). OFF = normal (follow the live seat).")]
        [SerializeField] private bool preview = false;

        // Honoured in the editor + development builds only, so a forgotten Preview toggle can never force a layout in a
        // shipped game (which always follows the real seat).
        private bool PreviewActive => preview && (Application.isEditor || Debug.isDebugBuild);

        // The seat the preview shows: where you actually sit (MySeat already resolves board → lobby/debug pick), else
        // seat 1 so edit mode / no session still previews something.
        private int PreviewSeatNumber()
        {
            int mySeat = table != null ? table.MySeat : -1;
            return mySeat > 0 ? mySeat : 1;
        }

        private void OnEnable()
        {
            if (Application.isPlaying && table != null)
            {
                table.OnBoardChanged += OnBoard;
                OnBoard(null);                    // apply immediately (MySeat is read from the table, board may be null)
            }
            else if (PreviewActive)
            {
                ApplyLayout(PreviewSeatNumber()); // edit mode: force the seated seat (off leaves your authoring untouched)
            }
        }

        private void OnDisable()
        {
            if (table != null) table.OnBoardChanged -= OnBoard;
        }

        private void OnBoard(BoardSnapshot board)
        {
            int mySeat = table != null ? table.MySeat : -1;    // 1-based, or -1 if not seated
            ApplyLayout(PreviewActive ? PreviewSeatNumber() : mySeat);  // preview auto-follows the seated seat (dev only)
        }

        private void ApplyLayout(int seat)
        {
            if (layoutsByLocalSeat == null) return;
            for (int i = 0; i < layoutsByLocalSeat.Length; i++)
            {
                if (layoutsByLocalSeat[i] == null) continue;
                bool active = (i == seat - 1);                 // layout index 0 → seat 1, etc.
                if (layoutsByLocalSeat[i].activeSelf != active)
                    layoutsByLocalSeat[i].SetActive(active);
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Live preview while you toggle it in edit mode. Deferred so SetActive isn't called inside OnValidate.
            // OFF leaves the layouts as you have them, so it doesn't fight your authoring.
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this == null || Application.isPlaying) return;
                if (preview) ApplyLayout(PreviewSeatNumber());
            };
        }
#endif
    }
}
