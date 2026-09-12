using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class Pendulum : MonoBehaviour
{
	public float speed = 4;
	public float limit = 75f; //Limit in degrees of the movement
	public bool randomStart = false; //If you want to modify the start position
	private float random = 0;

	private Rigidbody _rb;

	// Start is called before the first frame update
	void Awake()
    {
		_rb = GetComponent<Rigidbody>();
		_rb.isKinematic = true;

		if(randomStart)
			random = Random.Range(0f, 1f);
	}

    // Driven from FixedUpdate via Rigidbody.MoveRotation (not a direct transform
    // assignment) so PhysX sweeps the swing between physics steps instead of
    // teleporting it. A plain transform-only rotation has no Rigidbody for the
    // physics engine to track, so at speed the ball can tunnel straight through
    // the player's collider without ever firing the trigger — read as the player
    // "phasing into" the pendulum and the hit feeling weak or missing entirely.
    void FixedUpdate()
    {
		// Driven by the synchronized network clock (not Time.time) so every
		// client swings on the same phase — Time.time is seconds-since-that-
		// process-started and differs per client, which desyncs a hazard
		// players need to dodge in the same place at the same moment.
		float time = NetworkManager.Singleton != null
			? NetworkManager.Singleton.ServerTime.TimeAsFloat
			: Time.time;
		float angle = limit * Mathf.Sin(time + random * speed);
		Quaternion localRotation = Quaternion.Euler(0, 90, angle); // Change the y value to 0 to change swinging direction
		Quaternion worldRotation = transform.parent != null
			? transform.parent.rotation * localRotation
			: localRotation;

		_rb.MoveRotation(worldRotation);
	}
}
