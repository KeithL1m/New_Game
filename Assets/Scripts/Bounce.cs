using SyncRush;
using UnityEngine;

// Rewritten from OnCollisionEnter + CharacterControls (the pack's own demo player script):
// CharacterController never raises OnCollisionEnter on the thing it touches, so the original
// never fired against this project's player. Switched to a trigger + AddImpulse, matching the
// existing PendulumHazard pattern.
//
// Also needs a Rigidbody even though it never moves: Unity only raises trigger callbacks when
// at least one side of the overlap has a Rigidbody, kinematic or not. CharacterController has
// none, so without one here neither OnTriggerEnter would ever fire (matches why Pendulum and
// RotationPlatform both carry a kinematic Rigidbody too).
[RequireComponent(typeof(Collider))]
[RequireComponent(typeof(Rigidbody))]
public class Bounce : MonoBehaviour
{
    [Tooltip("Main vertical launch strength — always applied, regardless of where you land on the pad.")]
    public float force = 10f;

    [Tooltip("Sideways nudge based on how far off-center you land. Keep well below force so it reads as a bounce, not a shove.")]
    public float horizontalKick = 3f;

    [Tooltip("Minimum seconds between launches, so one landing can't trigger the pad twice.")]
    public float retriggerCooldown = 0.3f;

    public float stunTime = 0.3f; // not yet wired up — SyncRushPlayerController has no stun state

    // Keyed per player, not a single float: every client runs this trigger locally against
    // every player replica on that machine (owner and non-owner alike), since a bounce pad
    // isn't a NetworkBehaviour. A shared cooldown meant one player's touch — even a non-owner
    // replica, whose Launch() call is otherwise inert — could silently eat the next player's
    // real bounce if they landed within retriggerCooldown of each other on the same client.
    private readonly System.Collections.Generic.Dictionary<SyncRushPlayerController, float> _nextAllowedTime = new();

    private void Awake()
    {
        var rb = GetComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;
    }

    private void Reset()
    {
        GetComponent<Collider>().isTrigger = true;
    }

    private void OnTriggerEnter(Collider other)
    {
        var player = other.GetComponentInParent<SyncRushPlayerController>();
        if (player == null) return;

        // Split like PendulumHazard: a guaranteed vertical launch plus a smaller sideways
        // kick from how far off-center the landing was — a purely direction-normalized
        // impulse would spread most of its force sideways on any off-center hit, which is
        // what read as a slide instead of a bounce.
        Vector3 away = player.transform.position - transform.position;
        away.y = 0f;
        away = away.sqrMagnitude < 0.0001f ? Vector3.zero : away.normalized;

        // Launch overwrites velocity instead of adding to it, and the cooldown stops the
        // player's multiple colliders / re-contact on the way up from firing it again —
        // either one alone would let repeated contact stack into a bigger and bigger launch.
        if (_nextAllowedTime.TryGetValue(player, out float next) && Time.time < next) return;
        _nextAllowedTime[player] = Time.time + retriggerCooldown;

        player.Launch(away * horizontalKick + Vector3.up * force, this);
    }
}
