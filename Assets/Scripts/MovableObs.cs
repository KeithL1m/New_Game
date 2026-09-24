using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class MovableObs : MonoBehaviour
{
    public float distance = 5f;
    public bool horizontal = true;
    public float speed = 3f;
    public float offset = 0f;

    [Tooltip("Shifts this instance's position in the ping-pong cycle in seconds, so paired instances can move out of phase with each other.")]
    public float timeOffsetSeconds = 0f;

    private Rigidbody _rb;
    private Vector3 _startPos;
    private Vector3 _axis;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.isKinematic = true;
        _axis = horizontal ? Vector3.right : Vector3.forward;
        _startPos = transform.position + _axis * offset;
    }

    private void FixedUpdate()
    {
        float time = NetworkManager.Singleton != null
            ? NetworkManager.Singleton.ServerTime.TimeAsFloat
            : Time.time;
        time += timeOffsetSeconds;

        float halfPeriod = distance / speed;
        float m = time % (2f * halfPeriod);
        float travel = m <= halfPeriod ? speed * m : distance - speed * (m - halfPeriod);

        _rb.MovePosition(_startPos + _axis * travel);
    }
}
