using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class RotationPlatform : MonoBehaviour
{
    [Tooltip("Rotation speed in degrees per second.")]
    public float speed = 60f;

    [Tooltip("Starting phase offset in degrees — stagger this per instance so platforms " +
             "aren't all level (or all vertical) at the same moment.")]
    public float phaseOffsetDegrees = 0f;

    [Tooltip("Local axis to spin around.")]
    public Vector3 axis = Vector3.forward;

    private Rigidbody _rb;
    private Quaternion _restLocalRotation;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.isKinematic = true;
        _restLocalRotation = transform.localRotation;
    }

    private void FixedUpdate()
    {
        // Driven by the synchronized network clock (not Time.time) so every
        // client sees the platform at the same angle at the same moment — see
        // the identical reasoning in Pendulum.cs.
        float time = NetworkManager.Singleton != null
            ? NetworkManager.Singleton.ServerTime.TimeAsFloat
            : Time.time;

        float angle = time * speed + phaseOffsetDegrees;
        Quaternion targetRotation = transform.parent != null
            ? transform.parent.rotation * _restLocalRotation * Quaternion.AngleAxis(angle, axis)
            : _restLocalRotation * Quaternion.AngleAxis(angle, axis);

        // MoveRotation (not a direct transform assignment) so the physics engine
        // gives correct swept collision response to anything standing on it.
        _rb.MoveRotation(targetRotation);
    }
}
