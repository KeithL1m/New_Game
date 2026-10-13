using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace SyncRush
{
    /// <summary>
    /// The Sync Tether: Slingshot and Tow (GDD §5.3, M1).
    ///
    /// Tow: hold Tow (RMB) to hook your partner (teammates only) within max rope length. While held,
    /// the leader runs at _towLeaderSpeedMultiplier, and the towed player's own machine adds a
    /// pull of up to _towSpeedFraction of the leader's speed, steering along the path the leader
    /// ran (breadcrumbs) rather than straight at them. No pull inside _towSlackLength; snaps past
    /// _towBreakLength. Only the leader's state and speed replicate. Each player still moves only
    /// themselves.
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
    /// Recover comes later in M1. A player can be in only one rope at a time: nobody can hook a
    /// player who is anchoring, towing, or already on someone else's rope.
    /// </summary>
    [RequireComponent(typeof(SyncRushPlayerController))]
    public class PlayerTether : NetworkBehaviour
    {
        public enum TetherState : byte { Idle, Anchored, Towing }

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

        // Slingshotting a rival mostly helps them, so its real use is sabotage (launching them into
        // a hazard). The longer cooldown stops that being repeated on the same rival. 12 s still
        // felt abusable in playtest (2026-10-12), so 15 s.
        [Tooltip("Seconds before the same rival (not your teammate) can be slingshot again.")]
        [SerializeField] private float _rivalCooldown = 15f;

        [Header("Tow (GDD §5.3)")]
        [Tooltip("No pull while the towed player is closer than this. Pull ramps up from here to max rope length.")]
        [SerializeField] private float _towSlackLength = 10f;

        // Full pull well before max rope length: ramping all the way to 36 m meant the pull faded
        // as the partner closed in, which felt like a weak rubber band (playtest 2026-10-12).
        [Tooltip("Distance at which the pull reaches full strength. It holds there until the rope snaps.")]
        [SerializeField] private float _towFullPullLength = 20f;

        // Above 1 so a partner who isn't moving (stunned, just respawned) is still dragged along, and
        // a running partner closes fast. 0.6, then 1.1, both felt weak in playtests (2026-10-12).
        [Tooltip("Pull speed at full stretch, as a fraction of the leader's current speed.")]
        [SerializeField] private float _towSpeedFraction = 1.5f;

        [Tooltip("The tow snaps if the players get further apart than this.")]
        [SerializeField] private float _towBreakLength = 54f;

        [Tooltip("Leader's run/sprint speed multiplier while towing: the cost of pulling someone.")]
        [SerializeField] private float _towLeaderSpeedMultiplier = 0.85f;

        [Tooltip("Metres between recorded points of the leader's path, which the towed player is pulled along.")]
        [SerializeField] private float _trailSpacing = 1f;

        [Tooltip("A path point counts as reached within this horizontal distance.")]
        [SerializeField] private float _trailReachRadius = 2f;

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

        [Tooltip("Max droop as a fraction of rope length, so a short rope doesn't hang in a deep V.")]
        [SerializeField] private float _sagPerMetre = 0.12f;

        [Tooltip("Lowest the rope's middle may hang above the ground under it. The rope attaches at " +
                 "capsule centre (2.5 m up), so full sag used to bury the middle in the floor.")]
        [SerializeField] private float _groundClearance = 0.4f;

        [Tooltip("Sideways shake (metres) at the snap point. Past max length the shake builds from a fraction " +
                 "of this up to all of it at _towBreakLength, so players can see the rope is about to go.")]
        [SerializeField] private float _strainShake = 1f;

        [SerializeField] private float _strainShakeSpeed = 40f;

        [Tooltip("Colour the rope darkens toward as it nears the snap point.")]
        [SerializeField] private Color _snapColor = new(0.35f, 0f, 0f);

        // Owner-written contract state; every peer reads it to draw the rope.
        private readonly NetworkVariable<TetherState> _state =
            new(TetherState.Idle, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<ulong> _partnerId =
            new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<float> _tension =
            new(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        // Tow leader's measured horizontal speed; the towed player's pull is a fraction of it.
        private readonly NetworkVariable<float> _leaderSpeed =
            new(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        private static readonly List<PlayerTether> Spawned = new();
        private const int MaxTrailPoints = 128;

        private SyncRushPlayerController _controller;
        private NetworkPlayer _player;
        private CharacterController _cc;
        private Transform _visual;
        // Owner only: who the range preview points at, and whether a press would tether them.
        private PlayerTether _previewTarget;
        private bool _previewInRange;
        private LineRenderer _rope;

        // Owner only: partner NetworkObjectId → Time.time when they can be slingshot again.
        private readonly Dictionary<ulong, float> _nextAllowedTime = new();

        // Owner of a towed player: the leader's path as seen locally, oldest point first.
        private readonly Queue<Vector3> _trail = new();
        private Vector3 _lastTrailPoint;
        private Vector3 _leaderRunDir; // horizontal, from the last two path points; zero until known

        public TetherState State => _state.Value;

        /// <summary>Rope attach point: capsule centre, on the interpolated visual so it moves smoothly.</summary>
        private Vector3 AttachPoint =>
            (_visual != null ? _visual.position : transform.position) + transform.rotation * _cc.center;

        private void Awake()
        {
            _controller = GetComponent<SyncRushPlayerController>();
            _player = GetComponent<NetworkPlayer>();
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
            _controller.InputReader.OnTowPressed     += BeginTow;
            _controller.InputReader.OnTowReleased    += ReleaseTow;
        }

        public override void OnNetworkDespawn()
        {
            Spawned.Remove(this);
            if (!IsOwner || _controller.InputReader == null) return;

            _controller.InputReader.OnTetherPressed  -= BeginAnchor;
            _controller.InputReader.OnTetherReleased -= Release;
            _controller.InputReader.OnTowPressed     -= BeginTow;
            _controller.InputReader.OnTowReleased    -= ReleaseTow;
            _controller.InputLocked = false;
            _controller.SpeedMultiplier = 1f;
            _controller.TowVelocity = Vector3.zero;
        }

        // ── Owner: contract ───────────────────────────────────────────────────

        private bool CanStartContract =>
            _state.Value == TetherState.Idle && FindHolder() == null
            && gameObject.scene.name == LobbyManager.GameSceneName;

        private void BeginAnchor()
        {
            if (!CanStartContract) return;

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
            EndContract();
            if (partner == null || tension < _minReleaseTension) return;

            Vector3 dir = (AttachPoint - partner.AttachPoint).normalized;
            float speed = Mathf.Lerp(_minLaunchSpeed, _maxLaunchSpeed, tension);
            partner.SlingshotRpc(dir * speed + Vector3.up * _launchLift, NetworkObjectId);
            _nextAllowedTime[partner.NetworkObjectId] = Time.time + CooldownFor(partner);
        }

        private void BeginTow()
        {
            if (!CanStartContract) return;

            // Partner only; never aimed: the player you tow is usually behind you.
            PlayerTether partner = FindTowTarget(_maxRopeLength);
            if (partner == null) return;

            _partnerId.Value = partner.NetworkObjectId;
            _tension.Value = 0f;
            _leaderSpeed.Value = 0f;
            _state.Value = TetherState.Towing;
            _controller.SpeedMultiplier = _towLeaderSpeedMultiplier;
        }

        private void ReleaseTow()
        {
            if (_state.Value == TetherState.Towing) EndContract();
        }

        private void EndContract()
        {
            _state.Value = TetherState.Idle;
            _tension.Value = 0f;
            _leaderSpeed.Value = 0f;
            _controller.InputLocked = false;
            _controller.SpeedMultiplier = 1f;
        }

        private void Update()
        {
            // Range preview: who a press would pick right now, else who you're heading toward but
            // still out of range. BeginAnchor re-picks on press.
            _previewTarget = null;
            if (!IsOwner || !IsSpawned || !CanStartContract) return;

            _previewTarget = FindTarget(_maxRopeLength);
            _previewInRange = _previewTarget != null;
            if (!_previewInRange)
                _previewTarget = FindTarget(_maxRopeLength * _previewRangeFactor);
        }

        private void FixedUpdate()
        {
            if (!IsOwner || !IsSpawned) return;

            UpdateBeingTowed();
            if (_state.Value == TetherState.Idle) return;

            PlayerTether partner = ResolvePartner();
            if (partner == null)
            {
                EndContract(); // partner left the session
                return;
            }

            float distance = Vector3.Distance(AttachPoint, partner.AttachPoint);
            if (_state.Value == TetherState.Anchored)
            {
                float stretch = Mathf.Clamp01(distance / _maxRopeLength);
                _tension.Value = Mathf.MoveTowards(_tension.Value, stretch, Time.fixedDeltaTime / _tensionTime);
                return;
            }

            // Towing. Also snaps when either player respawns far away.
            if (distance > _towBreakLength)
            {
                EndContract();
                return;
            }
            _tension.Value = TowPull(distance); // drives the rope's sag and tint like slingshot tension
            _leaderSpeed.Value = _controller.HorizontalSpeed;
        }

        /// <summary>0 inside slack length, rising to 1 at _towFullPullLength, and held at 1 until the rope snaps.</summary>
        private float TowPull(float distance) => Mathf.InverseLerp(_towSlackLength, _towFullPullLength, distance);

        // ── Towed player's owner: pull along the leader's path ────────────────

        /// <summary>
        /// Runs on the towed player's own machine, which simulates their movement. Records the
        /// leader's replicated position as breadcrumbs and pulls toward the oldest one not yet
        /// reached, so the towed player follows the leader around obstacles instead of being
        /// dragged straight at them.
        /// </summary>
        private void UpdateBeingTowed()
        {
            PlayerTether leader = FindHolder(TetherState.Towing);
            if (leader == null)
            {
                _trail.Clear();
                _leaderRunDir = Vector3.zero;
                _controller.TowVelocity = Vector3.zero;
                return;
            }

            Vector3 leaderPos = leader.transform.position;
            if (_trail.Count == 0 || (leaderPos - _lastTrailPoint).sqrMagnitude >= _trailSpacing * _trailSpacing)
            {
                if (_trail.Count > 0)
                    _leaderRunDir = Vector3.ProjectOnPlane(leaderPos - _lastTrailPoint, Vector3.up).normalized;
                if (_trail.Count >= MaxTrailPoints) _trail.Dequeue();
                _trail.Enqueue(leaderPos);
                _lastTrailPoint = leaderPos;
            }

            // Catch-up only: once we're level with or ahead of the leader along the way they're
            // running, stop pulling. Otherwise a partner who ran past would be dragged back.
            // Uses the leader's run direction, not facing, so glancing back doesn't cut the pull.
            if (Vector3.Dot(transform.position - leaderPos, _leaderRunDir) > 0f)
            {
                _controller.TowVelocity = Vector3.zero;
                return;
            }

            Vector3 target = NextTrailPoint(leaderPos);
            Vector3 dir = Vector3.ProjectOnPlane(target - transform.position, Vector3.up);
            if (dir.sqrMagnitude < 0.0001f)
            {
                _controller.TowVelocity = Vector3.zero;
                return;
            }

            float pull = TowPull(Vector3.Distance(AttachPoint, leader.AttachPoint));
            _controller.TowVelocity = dir.normalized * (leader._leaderSpeed.Value * _towSpeedFraction * pull);
        }

        /// <summary>
        /// Drops path points already reached, or already passed (further from the leader than we
        /// are, e.g. after running ahead under our own steam). Falls back to the leader if none remain.
        /// </summary>
        private Vector3 NextTrailPoint(Vector3 leaderPos)
        {
            Vector3 me = transform.position;
            float myDistToLeader = FlatDistance(me, leaderPos);
            while (_trail.Count > 0)
            {
                Vector3 point = _trail.Peek();
                if (FlatDistance(me, point) > _trailReachRadius && FlatDistance(point, leaderPos) < myDistToLeader)
                    return point;
                _trail.Dequeue();
            }
            return leaderPos;
        }

        private static float FlatDistance(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

        /// <summary>
        /// The player closest to the centre of the camera view, within _aimAngle (GDD §5.4: rival
        /// by aim-target); otherwise your partner (partner by default); otherwise the nearest one,
        /// for players with no partner (solo team). Skips anyone beyond maxDistance, on cooldown,
        /// or already in a rope.
        /// </summary>
        private PlayerTether FindTarget(float maxDistance)
        {
            Camera cam = Camera.main;
            PlayerTether aimed = null;
            PlayerTether partner = null;
            PlayerTether nearest = null;
            float bestAngle = _aimAngle;
            float bestDist = maxDistance;

            foreach (var other in Spawned)
            {
                if (other == this || !other.IsFree) continue;
                if (_nextAllowedTime.TryGetValue(other.NetworkObjectId, out float t) && Time.time < t) continue;

                float dist = Vector3.Distance(AttachPoint, other.AttachPoint);
                if (dist > maxDistance) continue;
                if (IsPartner(other)) partner = other;
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
            return aimed != null ? aimed : partner != null ? partner : nearest;
        }

        private bool IsPartner(PlayerTether other) =>
            other != null && _player != null && _player.IsTeammateOf(other._player);

        private float CooldownFor(PlayerTether other) => IsPartner(other) ? _cooldown : _rivalCooldown;

        /// <summary>
        /// Tow target: your partner, if they're free and within maxDistance, else null. Tow is a
        /// teammate catch-up move (user decision 2026-10-12, a deviation from GDD §5.4's "any
        /// player" rule), so solo players (duel, FFA) have no Tow. Ignores aim; no cooldown.
        /// </summary>
        private PlayerTether FindTowTarget(float maxDistance)
        {
            foreach (var other in Spawned)
            {
                if (other == this || !other.IsFree || !IsPartner(other)) continue;
                if (Vector3.Distance(AttachPoint, other.AttachPoint) <= maxDistance) return other;
            }
            return null;
        }

        /// <summary>Not anchoring, towing, or on anyone else's rope.</summary>
        private bool IsFree => _state.Value == TetherState.Idle && FindHolder() == null;

        /// <summary>
        /// The player holding us on their rope (in <paramref name="state"/>, or any state if null),
        /// or null. While held, our own range preview is hidden, since it would just overlap their rope.
        /// </summary>
        private PlayerTether FindHolder(TetherState? state = null)
        {
            foreach (var other in Spawned)
            {
                if (other == this || other._state.Value == TetherState.Idle) continue;
                if (state.HasValue && other._state.Value != state.Value) continue;
                if (other._partnerId.Value == NetworkObjectId) return other;
            }
            return null;
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
            PlayerTether partner = IsSpawned && _state.Value != TetherState.Idle ? ResolvePartner() : null;
            if (partner != null)
            {
                float tension = _tension.Value;
                float distance = Vector3.Distance(AttachPoint, partner.AttachPoint);
                if (distance <= _maxRopeLength)
                {
                    DrawLine(partner, Color.Lerp(_slackColor, _tautColor, tension), _ropeWidth,
                             sag: _maxSag * (1f - tension), shake: 0f, shakeSpeed: 0f);
                }
                else
                {
                    // Overstretched: builds from a light tremble at max length to a violent, dark,
                    // thick shake where a tow snaps. A slingshot rope can stretch further; it just stays maxed.
                    float strain = Mathf.InverseLerp(_maxRopeLength, _towBreakLength, distance);
                    DrawLine(partner, Color.Lerp(_tautColor, _snapColor, strain),
                             Mathf.Lerp(_ropeWidth, _strainedRopeWidth, Mathf.Lerp(0.3f, 1f, strain)),
                             sag: 0f, shake: _strainShake * Mathf.Lerp(0.15f, 1f, strain),
                             shakeSpeed: _strainShakeSpeed * Mathf.Lerp(0.6f, 1.5f, strain));
                }
            }
            else if (_previewTarget != null)
            {
                // Kept straight and thin so it never reads as a real rope.
                Color color = _previewInRange ? ReadyColor : OutOfRangeColor;
                color.a = 0.85f;
                DrawLine(_previewTarget, color, _previewWidth, sag: 0f, shake: 0f, shakeSpeed: 0f);
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
        private void DrawLine(PlayerTether to, Color color, float width, float sag, float shake, float shakeSpeed)
        {
            Vector3 from = AttachPoint;
            Vector3 end = to.AttachPoint;
            sag = ClampSag(from, end, sag);
            Vector3 side = Vector3.Cross(end - from, Vector3.up);
            side = side.sqrMagnitude > 0.0001f ? side.normalized : Vector3.right; // rope straight up/down

            int count = _rope.positionCount;
            for (int i = 0; i < count; i++)
            {
                float t = i / (count - 1f);
                float bulge = 4f * t * (1f - t); // 0 at the ends, 1 in the middle
                Vector3 point = Vector3.Lerp(from, end, t) + Vector3.down * (sag * bulge);
                if (shake > 0f)
                    point += side * (shake * bulge * Mathf.Sin(Time.time * shakeSpeed + t * 12f));
                _rope.SetPosition(i, point);
            }

            _rope.enabled = true;
            _rope.startColor = color;
            _rope.endColor = color;
            _rope.widthMultiplier = width;
        }

        /// <summary>
        /// Limits droop to a fraction of rope length and keeps the middle above whatever ground is
        /// under it. Over a gap the raycast misses, so the rope can still hang down into it.
        /// </summary>
        private float ClampSag(Vector3 from, Vector3 end, float sag)
        {
            if (sag <= 0f) return 0f;
            sag = Mathf.Min(sag, Vector3.Distance(from, end) * _sagPerMetre);

            Vector3 mid = (from + end) * 0.5f;
            if (Physics.Raycast(mid, Vector3.down, out RaycastHit hit, sag + _groundClearance,
                                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                sag = Mathf.Max(0f, hit.distance - _groundClearance);
            return sag;
        }

        // ── Owner: HUD (TEMP greybox OnGUI, replaced in the M4 presentation pass) ──

        private static readonly Color ReadyColor = new(0.2f, 0.75f, 0.35f);
        private static readonly Color CooldownColor = new(0.45f, 0.45f, 0.5f);
        private static readonly Color TowColor = new(0.25f, 0.55f, 0.95f);
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
            if (_state.Value == TetherState.Towing)
            {
                DrawBar(new Rect(x, y, width, height), _tension.Value, TowColor, $"TOWING {PlayerName(_partnerId.Value)}");
                return;
            }
            PlayerTether towedBy = FindHolder(TetherState.Towing);
            if (towedBy != null)
            {
                DrawBar(new Rect(x, y, width, height), towedBy._tension.Value, TowColor,
                        $"TOWED BY {PlayerName(towedBy.NetworkObjectId)}");
                return;
            }

            if (_previewTarget != null)
                DrawTargetMarker(_previewTarget, _previewInRange);

            int rows = 0;
            foreach (var pair in _nextAllowedTime)
            {
                float remaining = pair.Value - Time.time;
                if (remaining <= 0f) continue;

                string name = PlayerName(pair.Key);
                var rect = new Rect(x, y - rows * (height + 6f), width, height);
                float total = NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(pair.Key, out var obj)
                    ? CooldownFor(obj.GetComponent<PlayerTether>())
                    : _cooldown;
                DrawBar(rect, 1f - remaining / total, CooldownColor, $"SLINGSHOT {name}  {remaining:0.0}s");
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

        private string PlayerName(ulong networkObjectId) =>
            NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(networkObjectId, out var obj)
                ? $"Player {obj.OwnerClientId}"
                : "Player";

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
