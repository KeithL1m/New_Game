using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Client-authoritative player movement with Rigidbody gravity.
/// Uses the New Input System directly (project is set to activeInputHandler: 1).
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class GravityPlayerMovement : NetworkBehaviour
{
    [Header("Movement")]
    public float MoveSpeed = 6f;
    public float JumpForce = 5f;

    [Header("Ground Check")]
    public float GroundCheckDistance = 0.55f;
    public LayerMask GroundMask = ~0;

    private Rigidbody _rb;
    private bool _isGrounded;

    public override void OnNetworkSpawn()
    {
        _rb = GetComponent<Rigidbody>();

        // Only the owner simulates physics; non-owners are kinematic
        // (position is synced via ClientNetworkTransform)
        if (!IsOwner)
        {
            _rb.isKinematic = true;
        }
    }

    private void FixedUpdate()
    {
        if (!IsOwner || !IsSpawned) return;

        CheckGrounded();
        HandleMovement();
    }

    private void Update()
    {
        if (!IsOwner || !IsSpawned) return;

        HandleJump();
    }

    private void CheckGrounded()
    {
        _isGrounded = Physics.Raycast(
            transform.position,
            Vector3.down,
            GroundCheckDistance,
            GroundMask
        );
    }

    private void HandleMovement()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        float h = 0f;
        float v = 0f;

        if (keyboard.aKey.isPressed) h = -1f;
        else if (keyboard.dKey.isPressed) h = 1f;

        if (keyboard.wKey.isPressed) v = 1f;
        else if (keyboard.sKey.isPressed) v = -1f;

        Vector3 move = new Vector3(h, 0f, v).normalized * MoveSpeed;

        // Preserve current vertical velocity so gravity is unaffected
        move.y = _rb.linearVelocity.y;
        _rb.linearVelocity = move;
    }

    private void HandleJump()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        if (keyboard.spaceKey.wasPressedThisFrame && _isGrounded)
        {
            _rb.AddForce(Vector3.up * JumpForce, ForceMode.Impulse);
        }
    }
}
