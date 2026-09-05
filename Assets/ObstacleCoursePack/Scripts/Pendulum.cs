using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class Pendulum : MonoBehaviour
{
	public float speed = 1.5f;
	public float limit = 75f; //Limit in degrees of the movement
	public bool randomStart = false; //If you want to modify the start position
	private float random = 0;

	// Start is called before the first frame update
	void Awake()
    {
		if(randomStart)
			random = Random.Range(0f, 1f);
	}

    // Update is called once per frame
    void Update()
    {
		// Driven by the synchronized network clock (not Time.time) so every
		// client swings on the same phase — Time.time is seconds-since-that-
		// process-started and differs per client, which desyncs a hazard
		// players need to dodge in the same place at the same moment.
		float time = NetworkManager.Singleton != null
			? NetworkManager.Singleton.ServerTime.TimeAsFloat
			: Time.time;
		float angle = limit * Mathf.Sin(time + random * speed);
		transform.localRotation = Quaternion.Euler(0, 0, angle);
	}
}
