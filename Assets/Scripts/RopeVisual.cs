using UnityEngine;

namespace SyncRush
{
    /// <summary>
    /// A visual-only simulated rope between two points (GDD §5.3: the tether is never a physics
    /// joint). Verlet points with gravity, kept no further apart than ropeLength / segments, ends
    /// pinned, resting on whatever ground is under each point, so the rope swings, trails, drapes
    /// over edges and whips after a slingshot.
    ///
    /// Every peer simulates its own copy from player positions it already has. Nothing here is
    /// networked, and nothing here affects movement.
    ///
    /// Rope length adapts like a retractable leash: a bit longer than the gap between the ends
    /// when slack, pulled in to the straight-line distance as tension goes to 1.
    /// </summary>
    public class RopeVisual : MonoBehaviour
    {
        [Tooltip("Slack rope length as a multiple of the gap between the ends.")]
        [SerializeField] private float _slackFactor = 1.2f;

        [Tooltip("Slack rope is at least this much longer than the gap, so a short rope still hangs.")]
        [SerializeField] private float _minSlack = 3f;

        [SerializeField] private float _gravity = 20f;

        [Tooltip("Velocity kept each frame (1 = no damping). Lower settles the rope faster.")]
        [SerializeField] private float _damping = 0.985f;

        [SerializeField] private int _iterations = 12;

        [Tooltip("How high above a surface a rope point rests.")]
        [SerializeField] private float _thickness = 0.15f;

        [Tooltip("Horizontal speed kept by a point resting on the ground each frame (friction).")]
        [SerializeField] private float _groundFriction = 0.6f;

        [Tooltip("An end moving further than this in one frame (respawn) restarts the rope.")]
        [SerializeField] private float _teleportDistance = 8f;

        [Header("Snap (final stretch, GDD §5.6)")]
        [Tooltip("Recoil speed (m/s) of the broken ends at full tension, toward the player each half belongs to.")]
        [SerializeField] private float _snapRecoilSpeed = 30f;

        [Tooltip("Recoil kept at zero tension, as a fraction of full: a slack rope still visibly snaps.")]
        [SerializeField] private float _slackSnapFraction = 0.35f;

        [Tooltip("Seconds the broken halves stay visible before they're gone.")]
        [SerializeField] private float _snapFadeTime = 1.5f;

        private LineRenderer _line;
        private LineRenderer _tailLine; // second half of a snapped rope (a LineRenderer can't draw a gap)
        private Vector3[] _points;
        private Vector3[] _previous;
        private bool _live;

        // Snapped: two halves, each still pinned to its own end, with the link between
        // _breakIndex and _breakIndex + 1 gone. Rope length is frozen at the moment of the snap.
        private float _snapTimer;
        private float _snapSegment;
        private int _breakIndex;
        private Color _snapColor;
        private float _snapWidth;
        private float _lastSegment;
        private Color _lastColor;
        private float _lastWidth;

        public bool Visible => _line != null && _line.enabled;
        public bool Snapped { get; private set; }

        /// <summary>A rope that snapped and finished fading. It stays hidden until Restore.</summary>
        public bool Gone => Snapped && _snapTimer <= 0f;

        public static RopeVisual Create(Transform parent, string name, Material material, int segments)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rope = go.AddComponent<RopeVisual>();
            rope.Setup(material, Mathf.Max(3, segments));
            return rope;
        }

        private void Setup(Material material, int count)
        {
            _line = gameObject.AddComponent<LineRenderer>();
            _line.useWorldSpace = true;
            _line.positionCount = count;
            _line.material = material;
            _line.numCapVertices = 2;
            _line.enabled = false;

            var tail = new GameObject("SnappedHalf");
            tail.transform.SetParent(transform, false);
            _tailLine = tail.AddComponent<LineRenderer>();
            _tailLine.useWorldSpace = true;
            _tailLine.material = material;
            _tailLine.numCapVertices = 2;
            _tailLine.enabled = false;

            _points = new Vector3[count];
            _previous = new Vector3[count];
        }

        public void Hide()
        {
            _line.enabled = false;
            _tailLine.enabled = false;
            _live = false; // re-laid straight next time it's shown
        }

        /// <summary>
        /// Breaks the rope in the middle. Each half recoils toward its own end (harder the tighter the
        /// rope was), then falls and fades over _snapFadeTime. Keep calling TickSnapped every frame.
        /// A rope that isn't on screen just counts as gone.
        /// </summary>
        public void Snap(float tension)
        {
            if (Snapped) return;
            Snapped = true;
            if (!Visible || !_live)
            {
                _snapTimer = 0f;
                Hide();
                return;
            }

            int last = _points.Length - 1;
            _breakIndex = last / 2;
            _snapTimer = _snapFadeTime;
            _snapSegment = _lastSegment;
            _snapColor = _lastColor;
            _snapWidth = _lastWidth;

            // Verlet velocity is (point - previous) per step, so set previous behind each point.
            float speed = _snapRecoilSpeed * Mathf.Lerp(_slackSnapFraction, 1f, Mathf.Clamp01(tension));
            float dt = 1f / 60f;
            for (int i = 1; i < last; i++)
            {
                Vector3 toEnd = (i <= _breakIndex ? _points[0] : _points[last]) - _points[i];
                if (toEnd.sqrMagnitude < 1e-4f) continue;
                // Points near the break whip back fastest, like the free end of a cut rope.
                float nearBreak = 1f - Mathf.Abs(i - (_breakIndex + 0.5f)) / (last * 0.5f);
                Vector3 velocity = toEnd.normalized * (speed * nearBreak) + Vector3.up * (speed * 0.15f * nearBreak);
                _previous[i] = _points[i] - velocity * dt;
            }
        }

        /// <summary>Back to a whole rope (e.g. a new race). It's laid straight the next time it's shown.</summary>
        public void Restore()
        {
            Snapped = false;
            _snapTimer = 0f;
            Hide();
        }

        /// <summary>Advance and draw one frame of a snapped rope, with its two ends still following the players.</summary>
        public void TickSnapped(Vector3 start, Vector3 end)
        {
            if (Gone)
            {
                if (Visible) Hide();
                return;
            }

            float dt = Mathf.Min(Time.deltaTime, 1f / 30f);
            _snapTimer -= dt;
            Simulate(start, end, _snapSegment, dt, 1f, _breakIndex);

            Color color = _snapColor;
            color.a *= Mathf.Clamp01(_snapTimer / _snapFadeTime);

            int split = _breakIndex + 1;
            DrawLine(_line, 0, split, color, _snapWidth);
            DrawLine(_tailLine, split, _points.Length - split, color, _snapWidth);
        }

        /// <summary>
        /// Advance and draw one frame. <paramref name="maxLength"/> caps the slack length (max rope
        /// length); past it the rope is simply straight.
        /// </summary>
        public void Tick(Vector3 start, Vector3 end, float tension, float maxLength, Color color, float width)
        {
            if (Snapped)
            {
                TickSnapped(start, end);
                return;
            }

            int count = _points.Length;
            if (!_live
                || (_points[0] - start).sqrMagnitude > _teleportDistance * _teleportDistance
                || (_points[count - 1] - end).sqrMagnitude > _teleportDistance * _teleportDistance)
                Lay(start, end);

            float gap = Vector3.Distance(start, end);
            float slack = Mathf.Max(gap, Mathf.Min(Mathf.Max(gap * _slackFactor, gap + _minSlack), maxLength));
            float segment = Mathf.Lerp(slack, gap, Mathf.Clamp01(tension)) / (count - 1);

            // Gravity fades out with tension: the solver can't fully straighten a heavy rope in a few
            // passes, and a taut rope read as sagging at 100% tension in testing.
            Simulate(start, end, segment, Mathf.Min(Time.deltaTime, 1f / 30f), 1f - Mathf.Clamp01(tension));
            Draw(color, width);
            _lastSegment = segment;
            _lastColor = color;
            _lastWidth = width;
        }

        private void Lay(Vector3 start, Vector3 end)
        {
            int count = _points.Length;
            for (int i = 0; i < count; i++)
                _points[i] = _previous[i] = Vector3.Lerp(start, end, i / (count - 1f));
            _live = true;
        }

        /// <param name="brokenLink">Index i whose link to i + 1 is cut (snapped rope), or -1 for a whole rope.</param>
        private void Simulate(Vector3 start, Vector3 end, float segment, float dt, float gravityScale, int brokenLink = -1)
        {
            int last = _points.Length - 1;
            Vector3 fall = Vector3.down * (_gravity * gravityScale * dt * dt);

            for (int i = 1; i < last; i++)
            {
                Vector3 velocity = (_points[i] - _previous[i]) * _damping;
                _previous[i] = _points[i];
                _points[i] += velocity + fall;
            }
            _points[0] = _previous[0] = start;
            _points[last] = _previous[last] = end;

            // Rope, not rods: only pull points together when a segment is too long, never push
            // them apart, so slack rope bunches and piles instead of staying stiff.
            for (int pass = 0; pass < _iterations; pass++)
            {
                for (int i = 0; i < last; i++)
                {
                    if (i == brokenLink) continue;
                    Vector3 delta = _points[i + 1] - _points[i];
                    float length = delta.magnitude;
                    if (length <= segment || length < 1e-5f) continue;

                    Vector3 correction = delta * ((length - segment) / length);
                    if (i == 0)             _points[i + 1] -= correction;
                    else if (i + 1 == last) _points[i]     += correction;
                    else
                    {
                        _points[i]     += correction * 0.5f;
                        _points[i + 1] -= correction * 0.5f;
                    }
                }
            }

            for (int i = 1; i < last; i++)
                RestOnGround(i);
        }

        /// <summary>Keeps a point on top of the surface under it, with friction so it doesn't skate.</summary>
        private void RestOnGround(int i)
        {
            const float probe = 1f;
            Vector3 p = _points[i];
            if (!Physics.Raycast(p + Vector3.up * probe, Vector3.down, out RaycastHit hit, probe + _thickness,
                                 Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return;
            if (hit.collider is CharacterController) return; // don't drape over the players themselves

            float floor = hit.point.y + _thickness;
            if (p.y >= floor) return;

            _points[i].y = floor;
            _previous[i].y = floor; // no bounce
            Vector3 slide = p - _previous[i];
            _previous[i].x = p.x - slide.x * _groundFriction;
            _previous[i].z = p.z - slide.z * _groundFriction;
        }

        private void Draw(Color color, float width)
        {
            _line.positionCount = _points.Length;
            _line.SetPositions(_points);
            _line.startColor = _line.endColor = color;
            _line.widthMultiplier = width;
            _line.enabled = true;
        }

        private void DrawLine(LineRenderer line, int first, int count, Color color, float width)
        {
            line.positionCount = count;
            for (int i = 0; i < count; i++)
                line.SetPosition(i, _points[first + i]);
            line.startColor = line.endColor = color;
            line.widthMultiplier = width;
            line.enabled = true;
        }
    }
}
