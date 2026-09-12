using UnityEngine;

namespace SyncRush
{
    /// <summary>
    /// Fixed third-person camera that always sits directly behind the player.
    ///
    /// Behaviour:
    ///   - Camera is locked at a fixed offset behind and above the player.
    ///   - Mouse X / right stick X rotates the PLAYER (yaw). The camera follows.
    ///   - No free orbit — the camera never drifts from the player's back.
    ///   - Smooth positional follow via LateUpdate lerp.
    ///   - SphereCast collision pulls the camera in if geometry is in the way.
    ///   - Cursor is locked on attach, released on Escape.
    ///
    /// All tuning values are set in the Inspector. The script holds no defaults.
    /// </summary>
    public class PlayerCameraController : MonoBehaviour
    {
        // ── Follow offset ─────────────────────────────────────────────────────
        [Header("Follow Offset")]
        [Tooltip("How far behind the player the camera sits.")]
        [SerializeField] private float _distance = 7f;

        [Tooltip("How high above the player's root the camera sits.")]
        [SerializeField] private float _height = 4.5f;

        [Tooltip("How quickly the camera catches up. Higher = snappier.")]
        [SerializeField] private float _followSmoothing = 12f;

        // ── Rotation sensitivity ──────────────────────────────────────────────
        [Header("Rotation Sensitivity")]
        [Tooltip("Mouse horizontal sensitivity.")]
        [SerializeField] private float _mouseSensitivity = 0.15f;

        [Tooltip("Gamepad right stick sensitivity in degrees per second.")]
        [SerializeField] private float _stickSensitivity = 90f;

        // ── Collision ─────────────────────────────────────────────────────────
        [Header("Collision")]
        [Tooltip("Radius of the sphere used to probe for geometry between player and camera.")]
        [SerializeField] private float _collisionRadius = 0.2f;

        [SerializeField] private LayerMask _collisionMask = ~0;

        // ── Runtime state ─────────────────────────────────────────────────────
        private Transform _playerRoot;
        private SyncRushPlayerController _playerController;
        private PlayerInputReader _inputReader;
        private bool _attached;

        // SmoothDamp velocity reference — must persist between frames
        private Vector3 _followVelocity;

        // ── Public API ────────────────────────────────────────────────────────

        public void AttachToPlayer(Transform playerRoot, PlayerInputReader inputReader)
        {
            _playerRoot       = playerRoot;
            _playerController = playerRoot.GetComponent<SyncRushPlayerController>();
            _inputReader      = inputReader;
            _attached         = true;

            LockCursor(true);
        }

        public void Detach()
        {
            _attached    = false;
            _playerRoot  = null;
            _inputReader = null;
            LockCursor(false);
        }

        // ── Unity ─────────────────────────────────────────────────────────────

        private void Update()
        {
            if (!_attached || _inputReader == null) return;

            HandleCursorToggle();
            RotatePlayer();
        }

        private void LateUpdate()
        {
            if (!_attached || _playerRoot == null) return;

            FollowPlayer();
        }

        // ── Private ───────────────────────────────────────────────────────────

        private void RotatePlayer()
        {
            Vector2 look = _inputReader.LookInput;
            if (look.sqrMagnitude < 0.0001f) return;

            // Detect gamepad (normalised –1..1) vs mouse (raw pixel delta)
            bool isGamepad = look.sqrMagnitude <= 1.01f;
            float sensitivity = isGamepad
                ? _stickSensitivity * Time.deltaTime
                : _mouseSensitivity;

            float yawDelta = look.x * sensitivity;
            _playerRoot.Rotate(0f, yawDelta, 0f, Space.World);
        }

        private void FollowPlayer()
        {
            // Use the interpolated position to eliminate FixedUpdate/LateUpdate
            // timing stutter — this gives a smooth position between physics ticks
            Vector3 smoothedPlayerPos = _playerController != null
                ? _playerController.InterpolatedPosition
                : _playerRoot.position;

            float desiredDist = _distance;

            // ── Collision probe ──────────────────────────────────────────────
            if (Physics.SphereCast(
                    smoothedPlayerPos + Vector3.up * _height,
                    _collisionRadius,
                    -_playerRoot.forward,
                    out RaycastHit hit,
                    _distance,
                    _collisionMask,
                    QueryTriggerInteraction.Ignore))
            {
                desiredDist = Mathf.Max(_collisionRadius * 2f, hit.distance - _collisionRadius);
            }

            Vector3 targetPos = smoothedPlayerPos
                                - _playerRoot.forward * desiredDist
                                + Vector3.up * _height;

            // ── SmoothDamp follow — frame-rate independent, no stutter ────────
            // Unlike Lerp, SmoothDamp accumulates velocity so it produces
            // consistent motion regardless of frame rate variation.
            float smoothTime = 1f / _followSmoothing;
            transform.position = Vector3.SmoothDamp(
                transform.position,
                targetPos,
                ref _followVelocity,
                smoothTime);

            // Rotation eased the same way position is — snapping LookAt straight to a
            // target that only updates at the physics tick rate is what read as "jittery"
            // while moving, since position had damping to hide that but rotation didn't.
            //
            // Look direction is computed from targetPos (the ideal, unlagged camera spot),
            // not transform.position (the actual, SmoothDamp-lagged position). Using the
            // lagged position here fed the camera's own follow-lag back into its rotation:
            // targetPos jumps instantly with the player's forward vector on a fast turn,
            // but transform.position hasn't caught up yet, so the look vector swung through
            // a much wider angle than the turn itself, making the camera whip around.
            Vector3 lookPoint = smoothedPlayerPos + Vector3.up * (_height * 0.4f);
            Quaternion targetRotation = Quaternion.LookRotation(lookPoint - targetPos);
            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                targetRotation,
                1f - Mathf.Exp(-_followSmoothing * Time.deltaTime));
        }

        private void HandleCursorToggle()
        {
            if (UnityEngine.InputSystem.Keyboard.current != null &&
                UnityEngine.InputSystem.Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                LockCursor(Cursor.lockState != CursorLockMode.Locked);
            }
        }

        private static void LockCursor(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible   = !locked;
        }
    }
}
