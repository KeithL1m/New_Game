using Unity.Netcode;
using UnityEngine;

namespace SyncRush
{
    /// <summary>
    /// Owner-simulated CharacterController movement for Sync Rush.
    /// Reads from a PlayerInputReader ScriptableObject — no direct Input System calls here.
    ///
    /// GDD values (set in Inspector, not in script):
    ///   Base speed   7 m/s
    ///   Sprint speed 10 m/s
    ///   Air control  0.4
    ///   Jump height  1.8 m
    ///   Coyote time  0.12 s
    ///
    /// Non-owners are moved by ClientNetworkTransform (already on the prefab).
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class SyncRushPlayerController : NetworkBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────
        [Header("Input")]
        [Tooltip("Drag the PlayerInputReader ScriptableObject asset here.")]
        [SerializeField] private PlayerInputReader _inputReader;

        [Header("Movement")]
        [SerializeField] private float _baseSpeed = 7f;
        [SerializeField] private float _sprintSpeed = 10f;

        [Range(0f, 1f)]
        [Tooltip("Fraction of full control while airborne (GDD: 0.4).")]
        [SerializeField] private float _airControl = 0.4f;

        [Header("Jump")]
        [Tooltip("Apex height in metres (GDD: 1.8 m).")]
        [SerializeField] private float _jumpHeight = 1.8f;

        [Tooltip("Coyote-time window in seconds (GDD: 0.12 s).")]
        [SerializeField] private float _coyoteTime = 0.12f;

        [Header("Stamina")]
        [SerializeField] private float _staminaMax = 1f;

        [Tooltip("Seconds to drain full stamina while sprinting.")]
        [SerializeField] private float _sprintDrainRate = 3f;

        [Tooltip("Seconds to fully recover stamina when not sprinting.")]
        [SerializeField] private float _staminaRecoverRate = 2f;

        [Header("Gravity")]
        [SerializeField] private float _gravity = 20f;

        // ── Runtime state ─────────────────────────────────────────────────────
        private CharacterController _cc;
        private Vector3 _velocity;
        private float _coyoteTimer;
        private bool _jumpQueued;
        private float _stamina;

        // Interpolation — stores positions from the last two FixedUpdate ticks
        // so the camera can read a smoothly interpolated position every LateUpdate
        private Vector3 _previousPosition;
        private Vector3 _currentPosition;

        // Derived jump speed: v = sqrt(2 * g * h)
        private float JumpSpeed => Mathf.Sqrt(2f * _gravity * _jumpHeight);

        // ── Status effect hooks (set by StatusEffectSystem in M2) ─────────────
        /// <summary>Multiplier applied to horizontal input (Mirror dart sets to -1).</summary>
        [HideInInspector] public float InputDirectionMultiplier = 1f;

        /// <summary>Multiplier applied to ground friction (Soap dart sets near 0).</summary>
        [HideInInspector] public float FrictionMultiplier = 1f;

        /// <summary>Multiplier applied to gravity (Heavy dart sets to 2.5).</summary>
        [HideInInspector] public float GravityMultiplier = 1f;

        // ── Public accessor for InputReader (used by PlayerCameraController) ──
        public PlayerInputReader InputReader => _inputReader;

        // ── NetworkBehaviour ──────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            _cc = GetComponent<CharacterController>();
            _stamina = _staminaMax;

            if (!IsOwner)
            {
                // Non-owners: disable the controller so ClientNetworkTransform
                // can drive the transform without fighting the CharacterController.
                _cc.enabled = false;
                enabled = false;
                return;
            }

            if (_inputReader == null)
            {
                Debug.LogError($"[SyncRushPlayerController] InputReader is not assigned on {gameObject.name}. " +
                               "Drag the PlayerInputReader asset onto this component.");
                enabled = false;
                return;
            }

            _inputReader.OnJumpPressed += QueueJump;

            // ── Hide lobby UI once spawned into the game ──────────────────────
            var lobbyUI = Object.FindFirstObjectByType<LobbyUI>();
            if (lobbyUI != null)
                lobbyUI.HideCanvas();

            // ── Camera attachment ─────────────────────────────────────────────
            var cam = Camera.main;
            if (cam != null)
            {
                var camController = cam.GetComponent<PlayerCameraController>();
                if (camController != null)
                    camController.AttachToPlayer(transform, _inputReader);
                else
                    Debug.LogWarning("[SyncRushPlayerController] Main Camera has no PlayerCameraController component.");
            }
            else
            {
                Debug.LogWarning("[SyncRushPlayerController] No Main Camera found in scene.");
            }
        }

        public override void OnNetworkDespawn()
        {
            if (_inputReader != null)
                _inputReader.OnJumpPressed -= QueueJump;

            if (IsOwner)
            {
                // Detach camera
                var cam = Camera.main;
                if (cam != null)
                {
                    var camController = cam.GetComponent<PlayerCameraController>();
                    if (camController != null)
                        camController.Detach();
                }

                // Re-show lobby UI when returning from game
                var lobbyUI = Object.FindFirstObjectByType<LobbyUI>();
                if (lobbyUI != null)
                    lobbyUI.ShowCanvas();
            }
        }

        // ── FixedUpdate ───────────────────────────────────────────────────────

        // All simulation runs at a fixed timestep — frame-rate independent.
        // Jump input arrives via OnJumpPressed event (fired from Input System
        // callback), sets _jumpQueued = true, consumed here each tick.
        private void FixedUpdate()
        {
            if (!IsOwner || !IsSpawned) return;

            float dt = Time.fixedDeltaTime;
            bool grounded = _cc.isGrounded;

            // ── Coyote time ──────────────────────────────────────────────────
            if (grounded)
            {
                _coyoteTimer = _coyoteTime;
                if (_velocity.y < 0f) _velocity.y = -2f;
            }
            else
            {
                _coyoteTimer -= dt;
            }

            // ── Jump ─────────────────────────────────────────────────────────
            if (_jumpQueued && _coyoteTimer > 0f)
            {
                _velocity.y = JumpSpeed;
                _coyoteTimer = 0f;
            }
            _jumpQueued = false;

            // ── Stamina ──────────────────────────────────────────────────────
            bool wantsSprint = _inputReader.SprintHeld && _stamina > 0f;
            if (wantsSprint)
                _stamina = Mathf.Max(0f, _stamina - dt / _sprintDrainRate);
            else
                _stamina = Mathf.Min(_staminaMax, _stamina + dt / _staminaRecoverRate);

            float currentSpeed = wantsSprint ? _sprintSpeed : _baseSpeed;

            // ── Horizontal movement ──────────────────────────────────────────
            Vector2 rawInput = _inputReader.MoveInput * InputDirectionMultiplier;
            Vector3 wishDir = new Vector3(rawInput.x, 0f, rawInput.y);
            wishDir = transform.TransformDirection(wishDir);

            float control = grounded ? FrictionMultiplier : _airControl;
            Vector3 horizontal = wishDir * currentSpeed * control;

            // ── Gravity ──────────────────────────────────────────────────────
            _velocity.y -= _gravity * GravityMultiplier * dt;

            // ── Final move ───────────────────────────────────────────────────
            _previousPosition = _currentPosition;
            Vector3 motion = new Vector3(horizontal.x, _velocity.y, horizontal.z);
            _cc.Move(motion * dt);
            _currentPosition = transform.position;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void QueueJump() => _jumpQueued = true;

        /// <summary>Apply an external velocity impulse (used by Tether slingshot).</summary>
        public void AddImpulse(Vector3 impulse) => _velocity += impulse;

        /// <summary>Current stamina normalised 0–1 (for HUD display).</summary>
        public float StaminaNormalized => _stamina / _staminaMax;

        /// <summary>
        /// Smoothly interpolated world position between the last two FixedUpdate ticks.
        /// Use this in LateUpdate instead of transform.position to eliminate camera stutter
        /// caused by the FixedUpdate / Update timing mismatch.
        /// </summary>
        public Vector3 InterpolatedPosition
        {
            get
            {
                float t = (Time.time - Time.fixedTime) / Time.fixedDeltaTime;
                return Vector3.Lerp(_previousPosition, _currentPosition, t);
            }
        }
    }
}
