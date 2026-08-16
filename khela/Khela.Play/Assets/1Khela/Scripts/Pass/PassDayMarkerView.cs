using TMPro;
using UnityEngine;

namespace PlayCard.Pass
{
    /// <summary>Where a day sits relative to the player's own today.</summary>
    public enum PassDayMarkerState
    {
        /// <summary>A day that has already come round (gold in the mockup).</summary>
        Past = 0,
        /// <summary>Today.</summary>
        Current = 1,
        /// <summary>Still to come (grey).</summary>
        Future = 2,
    }

    /// <summary>
    /// The numbered pip on the progress bar between the two reward rows. This is where "you can take this today"
    /// versus "this hasn't arrived yet" is expressed — the cards themselves don't distinguish the two.
    ///
    /// Every reference optional: assign only the states this art has.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PassDayMarkerView : MonoBehaviour
    {
        [SerializeField] private TMP_Text dayText;

        [Header("States — leave empty where the art has none")]
        [SerializeField] private GameObject past;
        [SerializeField] private GameObject current;
        [SerializeField] private GameObject future;

        public int Day { get; private set; }
        public PassDayMarkerState State { get; private set; }

        public void Bind(int day, PassDayMarkerState state)
        {
            Day = day;
            if (dayText != null) dayText.text = day.ToString();
            SetState(state);
        }

        public void SetState(PassDayMarkerState state)
        {
            State = state;

            // Two states may share one object — past and current are often the same lit background. So turn
            // everything off FIRST and switch on only the current state's object; toggling field by field would let
            // a later "off" undo an earlier "on" and leave the pip blank.
            Show(past, false);
            Show(current, false);
            Show(future, false);

            switch (state)
            {
                case PassDayMarkerState.Past: Show(past, true); break;
                case PassDayMarkerState.Current: Show(current, true); break;
                default: Show(future, true); break;
            }
        }

        private static void Show(GameObject target, bool on)
        {
            if (target != null && target.activeSelf != on) target.SetActive(on);
        }
    }
}
