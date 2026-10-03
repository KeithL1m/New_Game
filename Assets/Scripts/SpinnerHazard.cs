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

        [Tooltip("Seconds before the same player can be launched again. The bar has several " +
                 "trigger colliders, each raising its own OnTriggerEnter for one contact.")]
        [SerializeField] private float _relaunchLockout = 0.5f;

        [Tooltip("Seconds of hit-stun (no movement input, no airborne knockback decay) after a hit.")]
        [SerializeField] private float _stunTime = 0.6f;

        [Tooltip("Knockback is at least this multiple of the bar's speed at the contact point. A long " +
                 "bar's outer end outruns a fixed knockback, catches up and re-hits the player along " +
                 "its swing, which reads as being carried.")]
        [SerializeField] private float _outrunFactor = 1.3f;

        private readonly System.Collections.Generic.Dictionary<SyncRushPlayerController, float> _nextLaunchTime = new();

        private Quaternion _lastRotation;
        private Vector3 _angularVelocity; // world space, rad/s

        [Tooltip("How far, in metres, the hit zone extends past the bar's solid collider on every side. " +
                 "Applied at runtime so it stays this size at any object scale.")]
        [SerializeField] private float _contactMargin = 0.3f;

        private void Awake()
        {
            _lastRotation = transform.rotation;
            FitTriggersToSolidColliders();
        }

        /// <summary>
        /// Each solid box collider on the bar has a trigger box next to it that catches the hit.
        /// Trigger sizes are in local units, so a size tuned at one scale balloons when the bar
        /// is scaled up. Re-derive each trigger from its solid partner plus a fixed world margin.
        /// </summary>
        private void FitTriggersToSolidColliders()
        {
            foreach (var trigger in GetComponentsInChildren<BoxCollider>())
            {
                if (!trigger.isTrigger) continue;

                BoxCollider solid = null;
                foreach (var candidate in trigger.GetComponents<BoxCollider>())
                {
                    if (!candidate.isTrigger) { solid = candidate; break; }
                }
                if (solid == null) continue;

                // World length of each local axis, so the margin is metres regardless of scale.
                Matrix4x4 m = trigger.transform.localToWorldMatrix;
                Vector3 size = solid.size;
                for (int axis = 0; axis < 3; axis++)
                {
                    float worldPerLocal = m.GetColumn(axis).magnitude;
                    if (worldPerLocal > 0.0001f)
                        size[axis] += 2f * _contactMargin / worldPerLocal;
                }

                trigger.center = solid.center;
                trigger.size = size;
            }
        }

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

        // Stay, not Enter: Enter fires once per contact, so a player still inside the hit zone
        // when the lockout expires was never hit again and the bar swept straight through them.
        private void OnTriggerStay(Collider other)
        {
            var player = other.GetComponentInParent<SyncRushPlayerController>();
            if (player == null) return;

            if (_nextLaunchTime.TryGetValue(player, out float next) && Time.time < next) return;
            _nextLaunchTime[player] = Time.time + _relaunchLockout;

            Vector3 offset = player.transform.position - transform.position;
            Vector3 push = Vector3.Cross(_angularVelocity, offset);
            push.y = 0f;
            float speed = Mathf.Max(_knockbackForce, push.magnitude * _outrunFactor);

            // Player at the hub (or bar barely moving): fall back to pushing straight away.
            if (push.sqrMagnitude < 0.0001f)
            {
                push = offset;
                push.y = 0f;
                if (push.sqrMagnitude < 0.0001f) push = -transform.forward;
            }

            player.Knockback(push.normalized * speed + Vector3.up * _upwardBoost, _stunTime, this);
        }
    }
}
