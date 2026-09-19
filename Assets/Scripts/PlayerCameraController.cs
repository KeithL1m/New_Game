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

        // Smoothed anchor point (player position) and smoothed yaw. The camera offset
        // is built from these two, so the camera orbits at a constant distance instead
        // of cutting a chord across the turn. SmoothDamp velocities must persist.
        private Vector3 _smoothedAnchor;
        private Vector3 _anchorVelocity;
        private float   _smoothedYaw;
        private float   _yawVelocity;

        // ── Public API ────────────────────────────────────────────────────────

        public void AttachToPlayer(Transform playerRoot, PlayerInputReader inputReader)
        {
            _playerRoot       = playerRoot;
            _playerController = playerRoot.GetComponent<SyncRushPlayerController>();
            _inputReader      = inputReader;
            _attached         = true;

            _smoothedAnchor = _playerController != null
                ? _playerController.InterpolatedPosition
                : _playerRoot.position;
            _smoothedYaw    = _playerRoot.eulerAngles.y;
            _anchorVelocity = Vector3.zero;
            _yawVelocity    = 0f;

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
            Vector3 playerPos = _playerController != null
                ? _playerController.InterpolatedPosition
                : _playerRoot.position;

            float smoothTime = 1f / _followSmoothing;

            // Smooth the anchor and the yaw separately, then build the offset from them.
            // Smoothing the camera's world position directly makes it travel a straight
            // line to the new spot on the orbit circle, which cuts inside the circle on
            // a fast turn and reads as a zoom-in. Smoothing the angle keeps the camera
            // on the circle at a constant distance.
            _smoothedAnchor = Vector3.SmoothDamp(
                _smoothedAnchor, playerPos, ref _anchorVelocity, smoothTime);
            _smoothedYaw = Mathf.SmoothDampAngle(
                _smoothedYaw, _playerRoot.eulerAngles.y, ref _yawVelocity, smoothTime);

            Quaternion yawRot   = Quaternion.Euler(0f, _smoothedYaw, 0f);
            Vector3    backward = yawRot * Vector3.back;
            Vector3    probeOrigin = _smoothedAnchor + Vector3.up * _height;

            // ── Collision probe ──────────────────────────────────────────────
            float desiredDist = _distance;
            if (Physics.SphereCast(
                    probeOrigin,
                    _collisionRadius,
                    backward,
                    out RaycastHit hit,
                    _distance,
                    _collisionMask,
                    QueryTriggerInteraction.Ignore))
            {
                desiredDist = Mathf.Max(_collisionRadius * 2f, hit.distance - _collisionRadius);
            }

            transform.position = probeOrigin + backward * desiredDist;

            // Camera position and look point both derive from the smoothed anchor, so the
            // look vector is already stable and needs no extra rotation easing.
            Vector3 lookPoint = _smoothedAnchor + Vector3.up * (_height * 0.4f);
            transform.rotation = Quaternion.LookRotation(lookPoint - transform.position);
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
