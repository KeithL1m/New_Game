using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class Rotator : MonoBehaviour
{
    [Tooltip("Legacy scale preserved: actual rotation rate is speed*100 deg/s.")]
    public float speed = 3f;

    public float phaseOffsetDegrees = 0f;
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
        float time = NetworkManager.Singleton != null
            ? NetworkManager.Singleton.ServerTime.TimeAsFloat
            : Time.time;

        float angle = time * speed * 100f + phaseOffsetDegrees;
        Quaternion targetRotation = transform.parent != null
            ? transform.parent.rotation * _restLocalRotation * Quaternion.AngleAxis(angle, axis)
            : _restLocalRotation * Quaternion.AngleAxis(angle, axis);

        _rb.MoveRotation(targetRotation);
    }
}
