using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace SyncRush
{
    /// <summary>
    /// Shove (GDD §5.6): available to everyone once the final stretch starts and the ropes are cut.
    /// Press Attack (LMB, the Slingshot button until then) to knock the nearest player in front of
    /// you sideways and back. Teammates count too: the final stretch dissolves the teams.
    ///
    /// Same network path as the Slingshot launch: the shover picks the target on their own machine
    /// and sends the knockback to the target's owner, who applies it to their own movement.
    /// </summary>
    [RequireComponent(typeof(SyncRushPlayerController))]
    public class PlayerShove : NetworkBehaviour
    {
        [Tooltip("Max distance between capsule centres. Two touching players are 2.5 m apart.")]
        [SerializeField] private float _range = 5f;

        [Tooltip("Half-angle of the cone in front of the player that a target has to be in.")]
        [SerializeField] private float _coneAngle = 60f;

        [Tooltip("Horizontal knockback speed (m/s), away from the shover.")]
        [SerializeField] private float _force = 16f;

        [Tooltip("Upward speed added so the target leaves the ground (knockback only holds while airborne).")]
        [SerializeField] private float _lift = 6f;

        [Tooltip("Seconds the target has no movement input.")]
        [SerializeField] private float _stunTime = 0.3f;

        [Tooltip("Seconds between shoves.")]
        [SerializeField] private float _cooldown = 1.5f;

        [Tooltip("Seconds the ROPES CUT banner stays on screen.")]
        [SerializeField] private float _bannerTime = 2.5f;

        private static readonly List<PlayerShove> Spawned = new();

        private SyncRushPlayerController _controller;
        private float _nextShoveTime;
        private float _bannerTimer;
        private bool _wasCut;
        private string _feedback;
        private float _feedbackTimer;
        private GUIStyle _hudLabel;
        private GUIStyle _bannerLabel;

        private static bool RopesCut => RaceStateMachine.Instance != null && RaceStateMachine.Instance.RopesCut;

        private void Awake()
        {
            _controller = GetComponent<SyncRushPlayerController>();
        }

        public override void OnNetworkSpawn()
        {
            Spawned.Add(this);
            if (IsOwner && _controller.InputReader != null)
                _controller.InputReader.OnAttackPressed += TryShove;
        }

        public override void OnNetworkDespawn()
        {
            Spawned.Remove(this);
            if (IsOwner && _controller.InputReader != null)
                _controller.InputReader.OnAttackPressed -= TryShove;
        }

        private void Update()
        {
            if (!IsOwner) return;

            bool cut = RopesCut;
            if (cut && !_wasCut) _bannerTimer = _bannerTime;
            _wasCut = cut;
            _bannerTimer -= Time.deltaTime;
            _feedbackTimer -= Time.deltaTime;
        }

        private void TryShove()
        {
            if (!IsSpawned || !RopesCut || Time.time < _nextShoveTime) return;

            PlayerShove target = FindTarget();
            if (target == null) return; // a miss costs nothing

            Vector3 away = Vector3.ProjectOnPlane(target.transform.position - transform.position, Vector3.up);
            if (away.sqrMagnitude < 0.0001f) away = transform.forward;
            target.ShoveRpc(away.normalized * _force + Vector3.up * _lift, _stunTime);

            _nextShoveTime = Time.time + _cooldown;
            _feedback = $"SHOVED Player {target.OwnerClientId}";
            _feedbackTimer = 1f;
        }

        /// <summary>Nearest other player within _range and inside the cone in front of this player.</summary>
        private PlayerShove FindTarget()
        {
            PlayerShove best = null;
            float bestDist = _range;
            foreach (var other in Spawned)
            {
                if (other == this || !other.IsSpawned) continue;

                Vector3 offset = other.transform.position - transform.position;
                float dist = offset.magnitude;
                if (dist > bestDist) continue;
                Vector3 flat = Vector3.ProjectOnPlane(offset, Vector3.up);
                if (flat.sqrMagnitude > 0.0001f && Vector3.Angle(transform.forward, flat) > _coneAngle) continue;

                best = other;
                bestDist = dist;
            }
            return best;
        }

        /// <summary>Sent by the shover; runs on this player's owner, who simulates their own movement.</summary>
        [Rpc(SendTo.Owner)]
        private void ShoveRpc(Vector3 velocity, float stunTime)
        {
            _controller.Knockback(velocity, stunTime, this);
        }

        // ── Owner: HUD (TEMP greybox OnGUI, replaced in the M4 presentation pass) ──

        private static readonly Color ShoveColor = new(0.95f, 0.45f, 0.1f);

        private void OnGUI()
        {
            if (!IsOwner || !IsSpawned || !RopesCut) return;
            _hudLabel ??= new GUIStyle(GUI.skin.label)
                { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _bannerLabel ??= new GUIStyle(_hudLabel) { fontSize = 48 };

            if (_bannerTimer > 0f)
            {
                Color previous = GUI.contentColor;
                GUI.contentColor = ShoveColor;
                GUI.Label(new Rect(0f, Screen.height * 0.25f, Screen.width, 60f), "ROPES CUT!  EVERY PLAYER FOR THEMSELVES", _bannerLabel);
                GUI.contentColor = previous;
            }

            const float width = 280f;
            const float height = 30f;
            var rect = new Rect((Screen.width - width) * 0.5f, Screen.height - 80f, width, height);
            float remaining = _nextShoveTime - Time.time;
            string text = _feedbackTimer > 0f ? _feedback
                        : remaining > 0f ? $"SHOVE  {remaining:0.0}s"
                        : "SHOVE READY (LMB)";
            DrawBar(rect, remaining > 0f ? 1f - remaining / _cooldown : 1f, ShoveColor, text);
        }

        private void DrawBar(Rect rect, float fill, Color color, string text)
        {
            GUI.Box(rect, GUIContent.none);
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(rect.x + 2f, rect.y + 2f, (rect.width - 4f) * Mathf.Clamp01(fill), rect.height - 4f),
                            Texture2D.whiteTexture);
            GUI.color = previous;
            GUI.Label(rect, text, _hudLabel);
        }
    }
}
