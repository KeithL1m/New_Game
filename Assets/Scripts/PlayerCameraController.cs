using UnityEngine;

namespace SyncRush
{
    /// <summary>
    /// Fixed third-person camera that always sits directly behind the player.
    ///
    /// Behaviour:
    ///   - Camera is locked at a fixed offset behind and above the player.
    ///   - Mouse X / right stick X rotates the PLAYER (yaw). The camera follows.
    ///   - Mouse Y / right stick Y tilts the camera up and down around the player (pitch).
    ///   - No free yaw orbit — the camera never drifts from the player's back.
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

        // ── Pitch ─────────────────────────────────────────────────────────────
        [Header("Pitch")]
        [Tooltip("Lowest camera angle in degrees (negative = camera below the player, looking up).")]
        [SerializeField] private float _minPitch = -30f;

        [Tooltip("Highest camera angle in degrees (camera above the player, looking down).")]
        [SerializeField] private float _maxPitch = 70f;

        [Tooltip("Flip vertical look: pushing the mouse up looks down.")]
        [SerializeField] private bool _invertY = false;

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

        // Smoothed anchor point (player position). The camera offset is built from it plus
        // the player's yaw, so the camera orbits at a constant distance instead of cutting
        // a chord across the turn. The SmoothDamp velocity must persist.
        private Vector3 _smoothedAnchor;
        private Vector3 _anchorVelocity;
        private readonly RaycastHit[] _probeHits = new RaycastHit[16];
        private float   _pitch; // degrees; positive = camera above the pivot, looking down

        // The camera orbits a pivot at _height * LookHeightFraction above the player. The
        // default pitch is whatever angle _height / _distance already produced, so the
        // resting view is unchanged and mouse Y just tilts it from there.
        private const float LookHeightFraction = 0.4f;

        private float PivotHeight  => _height * LookHeightFraction;
        private float OrbitRadius  => Mathf.Sqrt(_distance * _distance + (_height - PivotHeight) * (_height - PivotHeight));
        private float DefaultPitch => Mathf.Atan2(_height - PivotHeight, _distance) * Mathf.Rad2Deg;

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
            _anchorVelocity = Vector3.zero;
            _pitch          = Mathf.Clamp(DefaultPitch, _minPitch, _maxPitch);

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

            // Mouse up = look up = camera swings down, so pitch decreases.
            float pitchDelta = look.y * sensitivity * (_invertY ? -1f : 1f);
            _pitch = Mathf.Clamp(_pitch - pitchDelta, _minPitch, _maxPitch);
        }

        private void FollowPlayer()
        {
            // Use the interpolated position to eliminate FixedUpdate/LateUpdate
            // timing stutter — this gives a smooth position between physics ticks
            Vector3 playerPos = _playerController != null
                ? _playerController.InterpolatedPosition
                : _playerRoot.position;

            float smoothTime = 1f / _followSmoothing;

            // Only the anchor (player position) is smoothed. Yaw is taken straight from the
            // player, because the mouse already drives it: smoothing it too acts as a low-pass
            // filter, so fast left-right swipes cancel out and the camera barely follows while
            // the character spins. The offset is still built from a yaw angle around the
            // smoothed anchor, so the camera orbits at a constant distance instead of cutting
            // a chord across the turn.
            _smoothedAnchor = Vector3.SmoothDamp(
                _smoothedAnchor, playerPos, ref _anchorVelocity, smoothTime);

            // The camera orbits a pivot above the player: yaw follows the player, pitch is the
            // mouse-controlled elevation angle. Direction from pivot to camera is "back",
            // tilted up by _pitch.
            Quaternion orbitRot = Quaternion.Euler(_pitch, _playerRoot.eulerAngles.y, 0f);
            Vector3    toCamera = orbitRot * Vector3.back;
            Vector3    pivot    = _smoothedAnchor + Vector3.up * PivotHeight;
            float      radius   = OrbitRadius;

            // ── Collision probe ──────────────────────────────────────────────
            // The pivot trails the player while it moves (SmoothDamp lag grows with speed), so
            // sprinting can leave it outside the player's own colliders. Swinging the camera
            // round then casts straight through the player's body; that hit has to be ignored
            // or the camera collapses into the pivot. Take the nearest hit that isn't the player.
            float nearest = radius;
            bool  blocked = false;
            int   count   = Physics.SphereCastNonAlloc(
                pivot, _collisionRadius, toCamera, _probeHits, radius,
                _collisionMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                RaycastHit h = _probeHits[i];
                if (h.collider.transform.IsChildOf(_playerRoot)) continue;
                if (h.distance < nearest) { nearest = h.distance; blocked = true; }
            }

            float desiredDist = blocked
                ? Mathf.Max(_collisionRadius * 2f, nearest - _collisionRadius)
                : radius;

            transform.position = pivot + toCamera * desiredDist;

            // Camera position and look point both derive from the smoothed anchor, so the
            // look vector is already stable and needs no extra rotation easing.
            transform.rotation = Quaternion.LookRotation(pivot - transform.position);
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
