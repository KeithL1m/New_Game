using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace SyncRush
{
    /// <summary>
    /// The Sync Tether, slingshot slice (GDD §5.3, M1).
    ///
    /// Hold Tether near another player to anchor to them: the anchor is locked in place and
    /// tension builds with the partner's distance (0→1 over 1.2 s with the rope fully out at
    /// 36 m — 3x the GDD's 12 m, tuned for this game's scale). Release to fire: the partner is launched along the rope toward the anchor at
    /// lerp(12, 40) m/s (GDD: 6–22, scaled up for the 5 m-tall player). Past max length tension just caps and the rope strains — no yank.
    ///
    /// Not a physics joint. The anchor owns the contract (GDD §7.3) and replicates it as state
    /// (NetworkVariables, read by every peer to draw the rope) plus one discrete event: the
    /// launch RPC to the partner's owner, who applies it to their own owner-simulated movement.
    ///
    /// Targets the player you're looking at (within _aimAngle of screen centre), falling back to
    /// the nearest player in range. While idle, the owner sees a range preview: a thin line to that
    /// player plus their distance over their head (green in range, grey if still too far).
    /// Tow and Recover come later in M1.
    /// </summary>
    [RequireComponent(typeof(SyncRushPlayerController))]
    public class PlayerTether : NetworkBehaviour
    {
        public enum TetherState : byte { Idle, Anchored }

        [Header("Slingshot (GDD §5.3)")]
        [SerializeField] private float _maxRopeLength = 36f;

        [Tooltip("Seconds for tension to fill from 0 to 1 with the rope fully stretched.")]
        [SerializeField] private float _tensionTime = 1.2f;

        // GDD says 6–22 m/s with 12 m rope, for a human-sized character. The player here is 5 m
        // tall (2.5x), so those read as barely a nudge; speeds and lift are scaled up to match.
        [SerializeField] private float _minLaunchSpeed = 12f;
        [SerializeField] private float _maxLaunchSpeed = 40f;

        [Tooltip("Extra upward speed so the partner leaves the ground — launch velocity only holds while airborne.")]
        [SerializeField] private float _launchLift = 14f;

        [Tooltip("Seconds the launched partner passes through the anchor's body. The launch aims straight " +
                 "at the anchor, so without this the partner is stopped dead against them.")]
        [SerializeField] private float _passThroughTime = 1f;

        [Tooltip("Releasing below this tension cancels without firing or starting the cooldown.")]
        [SerializeField] private float _minReleaseTension = 0.1f;

        [Tooltip("Seconds before the same partner can be slingshot again (GDD: 6 s per pair).")]
        [SerializeField] private float _cooldown = 6f;

        [Header("Targeting (GDD §5.4)")]
        [Tooltip("Degrees from the centre of the camera view within which a player counts as aimed at. " +
                 "With nobody aimed at, the nearest player in range is picked.")]
        [SerializeField] private float _aimAngle = 25f;

        [Tooltip("Players out to this multiple of max rope length still get a (grey) range preview, " +
                 "so you can see how much closer you need to get.")]
        [SerializeField] private float _previewRangeFactor = 2f;

        [Header("Rope Visual (greybox)")]
        [Tooltip("Optional. Needs a vertex-colour shader (e.g. Sprites/Default) for the tension tint to show.")]
        [SerializeField] private Material _ropeMaterial;
        [SerializeField] private Color _slackColor = Color.white;
        [SerializeField] private Color _tautColor = Color.red;
        [SerializeField] private float _ropeWidth = 0.2f;

        [Tooltip("Rope width once stretched past max length, so the strain reads visually.")]
        [SerializeField] private float _strainedRopeWidth = 0.35f;

        [Tooltip("Width of the owner-only preview line to the aimed player while not anchored.")]
        [SerializeField] private float _previewWidth = 0.15f;

        [Tooltip("Points along the rope. More = smoother sag curve.")]
        [SerializeField] private int _ropeSegments = 24;

        [Tooltip("How far the rope droops at its middle with no tension. It straightens as tension builds.")]
        [SerializeField] private float _maxSag = 4f;

        [Tooltip("Sideways shake (metres) when stretched past max length, so the strain reads visually.")]
        [SerializeField] private float _strainShake = 0.35f;

        [SerializeField] private float _strainShakeSpeed = 40f;

        // Owner-written contract state; every peer reads it to draw the rope.
        private readonly NetworkVariable<TetherState> _state =
            new(TetherState.Idle, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<ulong> _partnerId =
            new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<float> _tension =
            new(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        private static readonly List<PlayerTether> Spawned = new();

        private SyncRushPlayerController _controller;
        private CharacterController _cc;
        private Transform _visual;
        // Owner only: who the range preview points at, and whether a press would tether them.
        private PlayerTether _previewTarget;
        private bool _previewInRange;
        private LineRenderer _rope;

        // Owner only: partner NetworkObjectId → Time.time when they can be slingshot again.
        private readonly Dictionary<ulong, float> _nextAllowedTime = new();

        public TetherState State => _state.Value;

        /// <summary>Rope attach point: capsule centre, on the interpolated visual so it moves smoothly.</summary>
        private Vector3 AttachPoint =>
            (_visual != null ? _visual.position : transform.position) + transform.rotation * _cc.center;

        private void Awake()
        {
            _controller = GetComponent<SyncRushPlayerController>();
            _cc = GetComponent<CharacterController>();
            _visual = transform.Find("Model");

            _rope = gameObject.AddComponent<LineRenderer>();
            _rope.positionCount = Mathf.Max(2, _ropeSegments);
            _rope.useWorldSpace = true;
            _rope.material = _ropeMaterial != null ? _ropeMaterial : new Material(Shader.Find("Sprites/Default"));
            _rope.enabled = false;
        }

        public override void OnNetworkSpawn()
        {
            Spawned.Add(this);
            if (!IsOwner || _controller.InputReader == null) return;

            _controller.InputReader.OnTetherPressed  += BeginAnchor;
            _controller.InputReader.OnTetherReleased += Release;
        }

        public override void OnNetworkDespawn()
        {
            Spawned.Remove(this);
            if (!IsOwner || _controller.InputReader == null) return;

            _controller.InputReader.OnTetherPressed  -= BeginAnchor;
            _controller.InputReader.OnTetherReleased -= Release;
            _controller.InputLocked = false;
        }

        // ── Owner: contract ───────────────────────────────────────────────────

        private void BeginAnchor()
        {
            if (_state.Value != TetherState.Idle) return;
            if (gameObject.scene.name != LobbyManager.GameSceneName) return;

            PlayerTether partner = FindTarget(_maxRopeLength);
            if (partner == null) return;

            _partnerId.Value = partner.NetworkObjectId;
            _tension.Value = 0f;
            _state.Value = TetherState.Anchored;
            _controller.InputLocked = true;
        }

        private void Release()
        {
            if (_state.Value != TetherState.Anchored) return;

            float tension = _tension.Value;
            PlayerTether partner = ResolvePartner();
            EndAnchor();
            if (partner == null || tension < _minReleaseTension) return;

            Vector3 dir = (AttachPoint - partner.AttachPoint).normalized;
            float speed = Mathf.Lerp(_minLaunchSpeed, _maxLaunchSpeed, tension);
            partner.SlingshotRpc(dir * speed + Vector3.up * _launchLift, NetworkObjectId);
            _nextAllowedTime[partner.NetworkObjectId] = Time.time + _cooldown;
        }

        private void EndAnchor()
        {
            _state.Value = TetherState.Idle;
            _tension.Value = 0f;
            _controller.InputLocked = false;
        }

        private void Update()
        {
            // Range preview: who a press would pick right now, else who you're heading toward but
            // still out of range. BeginAnchor re-picks on press.
            _previewTarget = null;
            bool canTarget = IsOwner && IsSpawned && _state.Value == TetherState.Idle
                             && gameObject.scene.name == LobbyManager.GameSceneName;
            if (!canTarget) return;

            _previewTarget = FindTarget(_maxRopeLength);
            _previewInRange = _previewTarget != null;
            if (!_previewInRange)
                _previewTarget = FindTarget(_maxRopeLength * _previewRangeFactor);
        }

        private void FixedUpdate()
        {
            if (!IsOwner || !IsSpawned || _state.Value != TetherState.Anchored) return;

            PlayerTether partner = ResolvePartner();
            if (partner == null)
            {
                EndAnchor(); // partner left the session
                return;
            }

            float stretch = Mathf.Clamp01(Vector3.Distance(AttachPoint, partner.AttachPoint) / _maxRopeLength);
            _tension.Value = Mathf.MoveTowards(_tension.Value, stretch, Time.fixedDeltaTime / _tensionTime);
        }

        /// <summary>
        /// The player closest to the centre of the camera view, within _aimAngle (GDD §5.4: rival
        /// by aim-target); otherwise the nearest one. Nearest stands in for "partner by default"
        /// until teams exist. Skips anyone beyond maxDistance or on cooldown.
        /// </summary>
        private PlayerTether FindTarget(float maxDistance)
        {
            Camera cam = Camera.main;
            PlayerTether aimed = null;
            PlayerTether nearest = null;
            float bestAngle = _aimAngle;
            float bestDist = maxDistance;

            foreach (var other in Spawned)
            {
                if (other == this) continue;
                if (_nextAllowedTime.TryGetValue(other.NetworkObjectId, out float t) && Time.time < t) continue;

                float dist = Vector3.Distance(AttachPoint, other.AttachPoint);
                if (dist > maxDistance) continue;
                if (dist <= bestDist)
                {
                    nearest = other;
                    bestDist = dist;
                }

                if (cam == null) continue;
                float angle = Vector3.Angle(cam.transform.forward, other.AttachPoint - cam.transform.position);
                if (angle < bestAngle)
                {
                    aimed = other;
                    bestAngle = angle;
                }
            }
            return aimed != null ? aimed : nearest;
        }

        private PlayerTether ResolvePartner()
        {
            return NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(_partnerId.Value, out var obj)
                ? obj.GetComponent<PlayerTether>()
                : null;
        }

        // ── Partner: launch event ─────────────────────────────────────────────

        /// <summary>Sent by the anchor; runs on this player's owner, who simulates their own movement.</summary>
        [Rpc(SendTo.Owner)]
        private void SlingshotRpc(Vector3 velocity, ulong anchorObjectId)
        {
            // No stun, so the partner can still steer; the launch holds until they land.
            _controller.Knockback(velocity, 0f, this);

            if (NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(anchorObjectId, out var anchor))
                StartCoroutine(PassThroughAnchor(anchor.GetComponentsInChildren<Collider>()));
        }

        private IEnumerator PassThroughAnchor(Collider[] anchorColliders)
        {
            SetIgnoreCollision(anchorColliders, true);
            yield return new WaitForSeconds(_passThroughTime);
            SetIgnoreCollision(anchorColliders, false);
        }

        private void SetIgnoreCollision(Collider[] others, bool ignore)
        {
            foreach (var other in others)
                if (other != null) // anchor may have left mid-flight
                    Physics.IgnoreCollision(_cc, other, ignore);
        }

        // ── Rope visual: the real rope on every peer, the range preview for the owner ──

        private void LateUpdate()
        {
            PlayerTether partner = IsSpawned && _state.Value == TetherState.Anchored ? ResolvePartner() : null;
            if (partner != null)
            {
                float tension = _tension.Value;
                bool strained = Vector3.Distance(AttachPoint, partner.AttachPoint) > _maxRopeLength;
                Color color = Color.Lerp(_slackColor, _tautColor, tension);
                DrawLine(partner, color, strained ? _strainedRopeWidth : _ropeWidth,
                         sag: _maxSag * (1f - tension), shake: strained ? _strainShake : 0f);
            }
            else if (_previewTarget != null)
            {
                // Kept straight and thin so it never reads as a real rope.
                Color color = _previewInRange ? ReadyColor : OutOfRangeColor;
                color.a = 0.85f;
                DrawLine(_previewTarget, color, _previewWidth, sag: 0f, shake: 0f);
            }
            else
            {
                _rope.enabled = false;
            }
        }

        /// <summary>
        /// Draws the line to another player as a curve: a parabolic droop of <paramref name="sag"/>
        /// metres at the middle, plus a sideways wobble of <paramref name="shake"/> metres that is
        /// zero at both ends. Purely visual, computed locally on each peer from replicated state.
        /// </summary>
        private void DrawLine(PlayerTether to, Color color, float width, float sag, float shake)
        {
            Vector3 from = AttachPoint;
            Vector3 end = to.AttachPoint;
            Vector3 side = Vector3.Cross(end - from, Vector3.up);
            side = side.sqrMagnitude > 0.0001f ? side.normalized : Vector3.right; // rope straight up/down

            int count = _rope.positionCount;
            for (int i = 0; i < count; i++)
            {
                float t = i / (count - 1f);
                float bulge = 4f * t * (1f - t); // 0 at the ends, 1 in the middle
                Vector3 point = Vector3.Lerp(from, end, t) + Vector3.down * (sag * bulge);
                if (shake > 0f)
                    point += side * (shake * bulge * Mathf.Sin(Time.time * _strainShakeSpeed + t * 12f));
                _rope.SetPosition(i, point);
            }

            _rope.enabled = true;
            _rope.startColor = color;
            _rope.endColor = color;
            _rope.widthMultiplier = width;
        }

        // ── Owner: HUD (TEMP greybox OnGUI, replaced in the M4 presentation pass) ──

        private static readonly Color ReadyColor = new(0.2f, 0.75f, 0.35f);
        private static readonly Color CooldownColor = new(0.45f, 0.45f, 0.5f);
        // Amber, not grey: the course floor is light grey, so a grey preview line vanished against it.
        private static readonly Color OutOfRangeColor = new(1f, 0.6f, 0.1f);
        private GUIStyle _hudLabel;

        /// <summary>
        /// Bottom-centre tether status for the local player: tension while anchored, otherwise
        /// one cooldown bar per partner still cooling down (cooldown is per partner), or READY.
        /// </summary>
        private void OnGUI()
        {
            if (!IsOwner || !IsSpawned || gameObject.scene.name != LobbyManager.GameSceneName) return;
            _hudLabel ??= new GUIStyle(GUI.skin.label)
                { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };

            const float width = 280f;
            const float height = 30f;
            float x = (Screen.width - width) * 0.5f;
            float y = Screen.height - 80f;

            if (_state.Value == TetherState.Anchored)
            {
                DrawBar(new Rect(x, y, width, height), _tension.Value, _tautColor, $"TENSION {_tension.Value * 100f:0}%");
                return;
            }

            if (_previewTarget != null)
                DrawTargetMarker(_previewTarget, _previewInRange);

            int rows = 0;
            foreach (var pair in _nextAllowedTime)
            {
                float remaining = pair.Value - Time.time;
                if (remaining <= 0f) continue;

                string name = NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(pair.Key, out var obj)
                    ? $"Player {obj.OwnerClientId}"
                    : "Player";
                var rect = new Rect(x, y - rows * (height + 6f), width, height);
                DrawBar(rect, 1f - remaining / _cooldown, CooldownColor, $"SLINGSHOT {name}  {remaining:0.0}s");
                rows++;
            }

            if (rows == 0)
                DrawBar(new Rect(x, y, width, height), 1f, ReadyColor, "SLINGSHOT READY");
        }

        /// <summary>
        /// Marker above the aimed player's head with their distance: green if a press would tether
        /// them, grey with the max range if they're still too far.
        /// </summary>
        private void DrawTargetMarker(PlayerTether target, bool inRange)
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            Vector3 head = target.AttachPoint + Vector3.up * (target._cc.height * 0.5f + 1f);
            Vector3 screen = cam.WorldToScreenPoint(head);
            if (screen.z <= 0f) return; // behind the camera

            float distance = Vector3.Distance(AttachPoint, target.AttachPoint);
            string text = inRange ? $"▼ SLINGSHOT {distance:0}m" : $"▼ {distance:0}m / {_maxRopeLength:0}m";

            var rect = new Rect(screen.x - 120f, Screen.height - screen.y - 32f, 240f, 32f);
            Color previous = GUI.contentColor;
            GUI.contentColor = inRange ? ReadyColor : OutOfRangeColor;
            GUI.Label(rect, text, _hudLabel);
            GUI.contentColor = previous;
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
