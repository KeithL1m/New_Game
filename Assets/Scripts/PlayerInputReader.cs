using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SyncRush
{
    /// <summary>
    /// ScriptableObject that sits between the raw Input System and gameplay code.
    /// Lives as an asset — drag it onto any component that needs input.
    /// Raises C# events so movement, tether, dart, and UI systems are fully decoupled
    /// from how the input is bound.
    ///
    /// Supports Keyboard+Mouse and Gamepad out of the box via InputSystem_Actions.
    /// </summary>
    [CreateAssetMenu(fileName = "PlayerInputReader", menuName = "SyncRush/Player Input Reader")]
    public class PlayerInputReader : ScriptableObject, InputSystem_Actions.IPlayerActions
    {
        // ── Raw snapshot (read every frame by the controller) ─────────────────
        /// <summary>Normalised XZ movement direction from WASD / left stick.</summary>
        public Vector2 MoveInput { get; private set; }

        /// <summary>Mouse delta / right stick look input.</summary>
        public Vector2 LookInput { get; private set; }

        /// <summary>True while Sprint is held.</summary>
        public bool SprintHeld { get; private set; }

        /// <summary>True while Crouch is held.</summary>
        public bool CrouchHeld { get; private set; }

        // ── One-shot events (consumed by subscribers) ─────────────────────────
        public event Action OnJumpPressed;
        public event Action OnInteractPressed;
        public event Action OnAttackPressed;
        public event Action OnTetherPressed;
        public event Action OnFireDartPressed;

        // ── Internal ──────────────────────────────────────────────────────────
        private InputSystem_Actions _actions;

        // Called by Unity when the asset is enabled (e.g. first reference in play mode)
        private void OnEnable()
        {
            if (_actions == null)
            {
                _actions = new InputSystem_Actions();
                _actions.Player.SetCallbacks(this);
            }
            _actions.Player.Enable();
        }

        private void OnDisable()
        {
            _actions?.Player.Disable();
        }

        // ── IPlayerActions callbacks ──────────────────────────────────────────

        public void OnMove(InputAction.CallbackContext ctx)
            => MoveInput = ctx.ReadValue<Vector2>();

        public void OnLook(InputAction.CallbackContext ctx)
            => LookInput = ctx.ReadValue<Vector2>();

        public void OnSprint(InputAction.CallbackContext ctx)
            => SprintHeld = ctx.performed;

        public void OnCrouch(InputAction.CallbackContext ctx)
            => CrouchHeld = ctx.performed;

        public void OnJump(InputAction.CallbackContext ctx)
        {
            if (ctx.performed) OnJumpPressed?.Invoke();
        }

        public void OnInteract(InputAction.CallbackContext ctx)
        {
            if (ctx.performed) OnInteractPressed?.Invoke();
        }

        public void OnAttack(InputAction.CallbackContext ctx)
        {
            if (ctx.performed) OnAttackPressed?.Invoke();
        }

        // Attack action is reused as Tether until a dedicated binding is added
        // These stubs satisfy the interface for actions not yet wired to gameplay
        public void OnPrevious(InputAction.CallbackContext ctx) { }
        public void OnNext(InputAction.CallbackContext ctx) { }

        // ── Public helpers for future systems ────────────────────────────────

        /// <summary>
        /// Manually raise the Tether event (called by a dedicated input binding
        /// once the Tether action is added to the asset).
        /// </summary>
        public void RaiseTether() => OnTetherPressed?.Invoke();

        /// <summary>
        /// Manually raise the FireDart event.
        /// </summary>
        public void RaiseFireDart() => OnFireDartPressed?.Invoke();
    }
}
