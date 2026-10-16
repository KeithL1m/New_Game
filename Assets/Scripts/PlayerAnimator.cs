using UnityEngine;

namespace SyncRush
{
    /// <summary>
    /// Drives the character's locomotion animation (Idle → Running blend on "Speed") from how fast
    /// the visual is actually moving across the ground. Measured from the smoothed Model position
    /// on every peer, so remote players animate too without any extra network traffic.
    ///
    /// Above _runAnimSpeed the whole animator plays faster, so sprinting doesn't read as sliding.
    /// Visual only: movement is still the CharacterController on the root.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class PlayerAnimator : MonoBehaviour
    {
        private static readonly int SpeedParam = Animator.StringToHash("Speed");

        [Tooltip("Ground speed (m/s) at which the Running clip's feet match the ground. Faster plays the animation faster.")]
        [SerializeField] private float _runAnimSpeed = 9f;

        [Tooltip("How quickly the measured speed follows the real speed. Higher = snappier, lower = smoother.")]
        [SerializeField] private float _speedSmoothing = 10f;

        [Tooltip("A jump in position larger than this in one frame (respawn) isn't counted as movement.")]
        [SerializeField] private float _teleportDistance = 8f;

        private Animator _animator;
        private Vector3 _lastPosition;
        private bool _hasLast;
        private float _speed;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            _animator.applyRootMotion = false;
        }

        private void LateUpdate()
        {
            Vector3 position = transform.position;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            float measured = 0f;
            if (_hasLast)
            {
                Vector3 step = Vector3.ProjectOnPlane(position - _lastPosition, Vector3.up);
                if (step.sqrMagnitude < _teleportDistance * _teleportDistance)
                    measured = step.magnitude / dt;
            }
            _lastPosition = position;
            _hasLast = true;

            _speed = Mathf.Lerp(_speed, measured, 1f - Mathf.Exp(-_speedSmoothing * dt));
            _animator.SetFloat(SpeedParam, _speed);
            _animator.speed = _speed > _runAnimSpeed ? _speed / _runAnimSpeed : 1f;
        }
    }
}
