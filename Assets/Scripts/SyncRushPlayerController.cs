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

        [Header("Moving Platforms")]
        [Tooltip("A moving platform carries the player while its surface is within this many degrees of level. " +
                 "Steeper than this and the player is no longer carried and slides off instead.")]
        [SerializeField] private float _platformCarryAngle = 15f;

        [Tooltip("Speed in m/s at which a tilted moving platform slides the player down its surface.")]
        [SerializeField] private float _platformSlideSpeed = 6f;

        // Moving-platform state. Contact is stored in the platform's local space so the carry
        // is exactly how far that point on the platform moved since the previous tick.
        private Transform _platform;
        private Vector3 _platformLocalContact;
        private Vector3 _platformLastWorldContact;
        private Vector3 _platformSlideDir;
        private bool _platformFlat;
        private Transform _platformHitThisMove;
        private Vector3 _platformHitPoint;
        private Vector3 _platformHitNormal;

        // Interpolation — stores positions from the last two FixedUpdate ticks
        // so the camera can read a smoothly interpolated position every LateUpdate
        private Vector3 _previousPosition;
        private Vector3 _currentPosition;

        // The root transform only moves on FixedUpdate ticks, so a mesh parented directly to it
        // steps at the physics rate while the camera glides on InterpolatedPosition; the two
        // beat against each other and read as camera jitter. The Model child is drawn at the
        // interpolated position instead so mesh and camera share one smooth path.
        private Transform _visual;
        private bool _interpolationActive;

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
            _visual = transform.Find("Model");

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
            // ── Moving platform: carry while roughly level, slide off when tilted ──
            // CharacterController doesn't ride kinematic colliders, it only gets shoved out of
            // them, which read as random pushes. So ride explicitly, but only while the surface
            // is near-level — past that the player slides, keeping the spin timing challenge.
            Vector3 platformCarry = Vector3.zero;
            Vector3 platformSlide = Vector3.zero;
            if (_platform != null)
            {
                if (_platformFlat)
                    platformCarry = _platform.TransformPoint(_platformLocalContact) - _platformLastWorldContact;
                else
                    platformSlide = _platformSlideDir * _platformSlideSpeed;
            }
            _platformHitThisMove = null;

            // First tick, or something teleported the root (respawn): restart interpolation from
            // here instead of lerping across the jump from the stale position.
            if (!_interpolationActive || (transform.position - _currentPosition).sqrMagnitude > 0.01f)
            {
                _currentPosition = transform.position;
                _interpolationActive = true;
            }

            _previousPosition = _currentPosition;
            Vector3 positionBeforeMove = transform.position;
            Vector3 motion = new Vector3(
                horizontal.x + _externalVelocity.x,
                _velocity.y,
                horizontal.z + _externalVelocity.z);
            motion += platformSlide;
            Vector3 step = motion * dt + platformCarry;
            _cc.Move(step);
            _currentPosition = transform.position;
            DebugLogUnexpectedMotion(positionBeforeMove, step, platformCarry, platformSlide);

            UpdatePlatformContact();

            // Knockback fades out over time rather than persisting forever or
            // being instantly overwritten by input like _velocity.x/z would be.
            _externalVelocity = Vector3.Lerp(_externalVelocity, Vector3.zero, dt * _externalVelocityDamping);
        }

        private void LateUpdate()
        {
            if (_visual == null) return;

            if (_interpolationActive)
                _visual.position = InterpolatedPosition;
            else
                _visual.localPosition = Vector3.zero;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        // ── TEMP DEBUG: find what pushes the player with no input. Remove when found. ──
        [Header("Debug")]
        [SerializeField] private bool _debugLogPushes = true;
        private string _lastHitName;

        private void OnControllerColliderHit(ControllerColliderHit hit)
        {
            _lastHitName = hit.collider.name;

            // Keep the most upward-facing contact this Move as the thing we're standing on.
            // Only moving (kinematic-body) surfaces count; static ramps are left to CharacterController.
            var rb = hit.collider.attachedRigidbody;
            if (rb == null || !rb.isKinematic || hit.normal.y < 0.3f) return;
            if (_platformHitThisMove != null && hit.normal.y <= _platformHitNormal.y) return;

            _platformHitThisMove = rb.transform;
            _platformHitPoint    = hit.point;
            _platformHitNormal   = hit.normal;
        }

        private void UpdatePlatformContact()
        {
            if (_platformHitThisMove == null)
            {
                _platform = null;
                return;
            }

            _platform                 = _platformHitThisMove;
            _platformLocalContact     = _platform.InverseTransformPoint(_platformHitPoint);
            _platformLastWorldContact = _platformHitPoint;
            _platformFlat             = _platformHitNormal.y >= Mathf.Cos(_platformCarryAngle * Mathf.Deg2Rad);
            _platformSlideDir         = Vector3.ProjectOnPlane(Vector3.down, _platformHitNormal).normalized;
        }

        private void DebugLogImpulse(string kind, Vector3 v, Object source)
        {
            if (_debugLogPushes)
                Debug.Log($"[PushDebug] {kind} {v} from '{(source != null ? source.name : "unknown")}'", source);
        }

        private float _debugNextLogTime;

        private void DebugLogUnexpectedMotion(Vector3 before, Vector3 intended, Vector3 carry, Vector3 slide)
        {
            if (!_debugLogPushes || Time.time < _debugNextLogTime) return;

            Vector3 actual = transform.position - before;
            Vector3 extra = actual - intended;
            extra.y = 0f;
            if (extra.magnitude < 0.05f) return;
            _debugNextLogTime = Time.time + 0.25f;

            Vector3 want = new Vector3(intended.x, 0f, intended.z);
            // Extra pointing against the intended move is just being blocked; only the rest is a push.
            string kind = want.sqrMagnitude > 0.0001f && Vector3.Dot(extra, want) < 0f ? "BLOCKED" : "PUSHED";
            Vector3 local = transform.InverseTransformDirection(extra);
            Debug.Log($"[PushDebug] {kind} {extra.magnitude:F2}m world({extra.x:F2},{extra.z:F2}) " +
                      $"player-local(right {local.x:F2}, fwd {local.z:F2}) | wanted {want.magnitude:F2}m " +
                      $"| carry {carry.magnitude:F2}m slide {slide.magnitude:F2}m/s | " +
                      $"platform '{(_platform != null ? _platform.name : "none")}' flat={_platformFlat} " +
                      $"normalY={_platformHitNormal.y:F2} | last touched '{_lastHitName}' grounded={_cc.isGrounded}");
        }

        private void QueueJump() => _jumpBufferTimer = _jumpBufferTime;

        /// <summary>Apply an external velocity impulse (used by Tether slingshot, pendulum/hazard knockback).</summary>
        public void AddImpulse(Vector3 impulse, Object source = null)
        {
            DebugLogImpulse("AddImpulse", impulse, source);
            _externalVelocity += new Vector3(impulse.x, 0f, impulse.z);
            _velocity.y += impulse.y;
        }

        /// <summary>
        /// Overwrite (not add to) the current vertical and knockback velocity. Use for
        /// bounce pads, where repeated contact must give the same launch every time
        /// instead of stacking like AddImpulse does. Upward speed only ever goes up
        /// here, so two overlapping pad triggers resolve to the stronger one no matter
        /// which fires first.
        /// </summary>
        public void Launch(Vector3 velocity, Object source = null)
        {
            DebugLogImpulse("Launch", velocity, source);
            _externalVelocity = new Vector3(velocity.x, 0f, velocity.z);
            _velocity.y = Mathf.Max(_velocity.y, velocity.y);
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
