using PlayCard.Game.Dtos;
using UnityEngine;

namespace PlayCard.Game.Table
{
    /// <summary>
    /// Per-seat table camera. Each seat has a wide "table" pose (deal/play) and a close "bet" pose (zoomed on
    /// that seat's bet spot). The camera follows the LOCAL player's seat (<see cref="TableController.MySeat"/>):
    /// while betting it eases to that seat's bet pose, during the round to its table pose; not seated → a
    /// shared spectate pose. Author each pose as an empty Transform placed in the scene.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class TableCameraController : MonoBehaviour
    {
        [System.Serializable]
        public struct SeatView
        {
            [Tooltip("Wide view from this seat (during the round).")]
            public Transform tablePose;
            [Tooltip("Close zoom on this seat's bet spot (while betting).")]
            public Transform betPose;
        }

        [SerializeField] private TableController table;

        [Tooltip("One entry per seat — element 0 = seat 1, element 1 = seat 2, …")]
        [SerializeField] private SeatView[] seats;
        [Tooltip("Used when not seated (spectating) or before a seat is known.")]
        [SerializeField] private Transform spectatePose;

        [Header("FOV / easing")]
        [SerializeField] private float tableFov = 38f;
        [SerializeField] private float betFov = 30f;
        [SerializeField] private float moveTime = 0.5f;

        private Camera _cam;
        private Transform _target;
        private float _targetFov;
        private Vector3 _posVel;
        private float _fovVel;

        private void Awake()
        {
            _cam = GetComponent<Camera>();
            _target = spectatePose != null ? spectatePose : (seats != null && seats.Length > 0 ? seats[0].tablePose : null);
            _targetFov = tableFov;
            if (_target != null) transform.SetPositionAndRotation(_target.position, _target.rotation);
            if (_cam) _cam.fieldOfView = tableFov;
        }

        private void OnEnable()
        {
            if (table != null) table.OnBoardChanged += OnBoard;
        }

        private void OnDisable()
        {
            if (table != null) table.OnBoardChanged -= OnBoard;
        }

        private void OnBoard(BoardSnapshot board)
        {
            int seat = table.MySeat;                 // 1-based, or -1 if not seated
            bool betting = board != null && !board.RoundInProgress;

            if (seat >= 1 && seats != null && seat <= seats.Length)
            {
                var v = seats[seat - 1];
                var pose = betting ? v.betPose : v.tablePose;
                if (pose == null) pose = v.tablePose ?? v.betPose;   // fall back to whichever is set
                if (pose != null) _target = pose;
                _targetFov = betting ? betFov : tableFov;
            }
            else
            {
                if (spectatePose != null) _target = spectatePose;
                _targetFov = tableFov;
            }
        }

        private void LateUpdate()
        {
            if (_target == null) return;

            transform.position = Vector3.SmoothDamp(transform.position, _target.position, ref _posVel, moveTime);

            float t = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.0001f, moveTime));
            transform.rotation = Quaternion.Slerp(transform.rotation, _target.rotation, t);

            if (_cam) _cam.fieldOfView = Mathf.SmoothDamp(_cam.fieldOfView, _targetFov, ref _fovVel, moveTime);
        }
    }
}
