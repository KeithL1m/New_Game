using Unity.Netcode;
using UnityEngine;

// Rewritten from the original Time.deltaTime + Random.Range version: that relied on each
// client's own unsynced RNG, which would desync a solid blocking wall across clients.
// This version is a deterministic hold-at-top/hold-at-bottom cycle driven by ServerTime.
[RequireComponent(typeof(Rigidbody))]
public class WallMovable : MonoBehaviour
{
    public float speed = 2f;
    public float holdTime = 1f;

    [Tooltip("Shifts this instance's position in the cycle in seconds, so paired instances can move out of phase with each other.")]
    public float timeOffsetSeconds = 0f;

    private Rigidbody _rb;
    private Vector3 _topPos;
    private Vector3 _bottomPos;
    private float _travelTime;
    private float _cycleTime;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.isKinematic = true;
        float height = transform.localScale.y;
        _topPos = transform.position;
        _bottomPos = transform.position - new Vector3(0f, height, 0f);
        _travelTime = height / speed;
        _cycleTime = 2f * (_travelTime + holdTime);
    }

    private void FixedUpdate()
    {
        float time = NetworkManager.Singleton != null
            ? NetworkManager.Singleton.ServerTime.TimeAsFloat
            : Time.time;
        time += timeOffsetSeconds;

        float t = time % _cycleTime;
        Vector3 targetPos;
        if (t < _travelTime)
            targetPos = Vector3.Lerp(_topPos, _bottomPos, t / _travelTime);
        else if (t < _travelTime + holdTime)
            targetPos = _bottomPos;
        else if (t < 2f * _travelTime + holdTime)
            targetPos = Vector3.Lerp(_bottomPos, _topPos, (t - _travelTime - holdTime) / _travelTime);
        else
            targetPos = _topPos;

        _rb.MovePosition(targetPos);
    }
}
