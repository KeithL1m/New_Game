using System.Collections.Generic;
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
        [SerializeField] private float _jumpHeight = 2.5f;

        [Tooltip("Coyote-time window in seconds (GDD: 0.12 s).")]
        [SerializeField] private float _coyoteTime = 0.09f;

        [Tooltip("How long a jump press is remembered before landing, so pressing jump slightly " +
                 "early still fires once grounded — mirrors coyote time on the other side of contact.")]
        [SerializeField] private float _jumpBufferTime = 0.15f;

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
        private Vector3 _externalVelocity; // horizontal knockback impulses (tether, pendulum, etc.), decays over time
        private float _coyoteTimer;
        private float _jumpBufferTimer;
        private float _stamina;

        [Header("Knockback")]
        [Tooltip("How quickly a horizontal impulse (AddImpulse) decays back to zero. Higher = shorter-lived knockback.")]
        [SerializeField] private float _externalVelocityDamping = 4f;

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

            // ── Camera attachment ─────────────────────────────────────────────
            // The player spawns as soon as the client connects, which happens
            // in LobbyScene — before NGO's NetworkSceneManager loads GameScene.
            // Camera.main at spawn time is the lobby's static camera, not the
            // gameplay camera, so we attach now (covers same-scene spawns) and
            // again whenever a network scene load finishes (covers the lobby
            // → game transition, where Camera.main only becomes valid after).
            AttachCamera();
            if (NetworkManager.SceneManager != null)
                NetworkManager.SceneManager.OnLoadEventCompleted += HandleSceneLoadCompleted;
        }

        public override void OnNetworkDespawn()
        {
            if (_inputReader != null)
                _inputReader.OnJumpPressed -= QueueJump;

            if (IsOwner)
            {
                if (NetworkManager != null && NetworkManager.SceneManager != null)
                    NetworkManager.SceneManager.OnLoadEventCompleted -= HandleSceneLoadCompleted;

                var cam = Camera.main;
                if (cam != null)
                {
                    var camController = cam.GetComponent<PlayerCameraController>();
                    if (camController != null)
                        camController.Detach();
                }
            }
        }

        private void HandleSceneLoadCompleted(string sceneName, UnityEngine.SceneManagement.LoadSceneMode mode,
                                               List<ulong> clientsCompleted, List<ulong> clientsTimedOut)
            => AttachCamera();

        private void AttachCamera()
        {
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

        // ── FixedUpdate ───────────────────────────────────────────────────────

        // All simulation runs at a fixed timestep — frame-rate independent.
        // Jump input arrives via OnJumpPressed event (fired from Input System
        // callback), sets _jumpQueued = true, consumed here each tick.
        private void FixedUpdate()
        {
            if (!IsOwner || !IsSpawned) return;

            // The player object spawns the instant a client connects, which
            // happens in LobbyScene — before the host clicks Start Game. That
            // scene has no floor, so simulating gravity there lets players
            // free-fall for however long the lobby wait lasts; by the time
            // NGO migrates them into GameScene they're already far below the
            // platform and never land. Only simulate once actually in GameScene.
            if (gameObject.scene.name != LobbyManager.GameSceneName) return;

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
            _jumpBufferTimer -= dt;
            if (_jumpBufferTimer > 0f && _coyoteTimer > 0f)
            {
                _velocity.y = JumpSpeed;
                _coyoteTimer = 0f;
                _jumpBufferTimer = 0f;
            }

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
            Vector3 motion = new Vector3(
                horizontal.x + _externalVelocity.x,
                _velocity.y,
                horizontal.z + _externalVelocity.z);
            _cc.Move(motion * dt);
            _currentPosition = transform.position;

            // Knockback fades out over time rather than persisting forever or
            // being instantly overwritten by input like _velocity.x/z would be.
            _externalVelocity = Vector3.Lerp(_externalVelocity, Vector3.zero, dt * _externalVelocityDamping);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void QueueJump() => _jumpBufferTimer = _jumpBufferTime;

        /// <summary>Apply an external velocity impulse (used by Tether slingshot, pendulum/hazard knockback).</summary>
        public void AddImpulse(Vector3 impulse)
        {
            _externalVelocity += new Vector3(impulse.x, 0f, impulse.z);
            _velocity.y += impulse.y;
        }

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
