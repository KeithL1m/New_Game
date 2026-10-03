using UnityEngine;

namespace SyncRush
{
    /// <summary>Knocks back any player whose collider enters it — used by the pendulum's ball.</summary>
    [RequireComponent(typeof(Collider))]
    public class PendulumHazard : MonoBehaviour
    {
        [Tooltip("Knockback speed applied away from this hazard, in m/s.")]
        [SerializeField] private float _knockbackForce = 25f;

        [Tooltip("Extra upward speed added to the knockback so hits arc away instead of just sliding.")]
        [SerializeField] private float _upwardBoost = 7f;

        [Tooltip("Seconds of hit-stun (no movement input, no airborne knockback decay) after a hit.")]
        [SerializeField] private float _stunTime = 0.6f;

        private void Reset()
        {
            GetComponent<Collider>().isTrigger = true;
        }

        private void OnTriggerEnter(Collider other)
        {
            var player = other.GetComponentInParent<SyncRushPlayerController>();
            if (player == null)
                return;

            Vector3 away = player.transform.position - transform.position;
            away.y = 0f;
            if (away.sqrMagnitude < 0.0001f)
                away = -transform.forward;
            away.Normalize();

            // Knockback overwrites rather than adds, so the player's two colliders both entering
            // the ball on the same tick no longer stack into a double-strength hit.
            player.Knockback(away * _knockbackForce + Vector3.up * _upwardBoost, _stunTime, this);
        }
    }
}
