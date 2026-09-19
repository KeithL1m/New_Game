using UnityEngine;

namespace SyncRush
{
    /// <summary>
    /// Knocks a player off a spinning bar (Rotator / RotationPlatform) instead of letting it
    /// carry them. The bar's colliders must be triggers: a solid kinematic collider rotating
    /// into a CharacterController just slides the player along its surface, which reads as
    /// being dragged rather than hit.
    ///
    /// Knockback goes the way the bar is moving at the player's position (angular velocity x
    /// offset from the pivot), so a hit throws you along the swing like a real smack. The
    /// angular velocity is measured from the bar's own rotation each physics tick, so this
    /// works with any spinner script and stays in sync with the networked clock.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class SpinnerHazard : MonoBehaviour
    {
        [Tooltip("Knockback speed along the bar's direction of travel, in m/s.")]
        [SerializeField] private float _knockbackForce = 25f;

        [Tooltip("Extra upward speed so hits arc away instead of sliding along the ground.")]
        [SerializeField] private float _upwardBoost = 7f;

        private Quaternion _lastRotation;
        private Vector3 _angularVelocity; // world space, rad/s

        private void Awake() => _lastRotation = transform.rotation;

        private void FixedUpdate()
        {
            Quaternion delta = transform.rotation * Quaternion.Inverse(_lastRotation);
            delta.ToAngleAxis(out float angleDeg, out Vector3 axis);
            if (angleDeg > 180f) angleDeg -= 360f;

            _angularVelocity = float.IsInfinity(axis.x) || Mathf.Abs(angleDeg) < 0.0001f
                ? Vector3.zero
                : axis * (angleDeg * Mathf.Deg2Rad / Time.fixedDeltaTime);
            _lastRotation = transform.rotation;
        }

        private void OnTriggerEnter(Collider other)
        {
            var player = other.GetComponentInParent<SyncRushPlayerController>();
            if (player == null) return;

            Vector3 offset = player.transform.position - transform.position;
            Vector3 push = Vector3.Cross(_angularVelocity, offset);
            push.y = 0f;

            // Player at the hub (or bar barely moving): fall back to pushing straight away.
            if (push.sqrMagnitude < 0.0001f)
            {
                push = offset;
                push.y = 0f;
                if (push.sqrMagnitude < 0.0001f) push = -transform.forward;
            }

            player.Launch(push.normalized * _knockbackForce + Vector3.up * _upwardBoost, this);
        }
    }
}
