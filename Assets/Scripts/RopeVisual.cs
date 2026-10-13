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

        private LineRenderer _line;
        private Vector3[] _points;
        private Vector3[] _previous;
        private bool _live;

        public bool Visible => _line != null && _line.enabled;

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
            _points = new Vector3[count];
            _previous = new Vector3[count];
        }

        public void Hide()
        {
            _line.enabled = false;
            _live = false; // re-laid straight next time it's shown
        }

        /// <summary>
        /// Advance and draw one frame. <paramref name="maxLength"/> caps the slack length (max rope
        /// length); past it the rope is simply straight.
        /// </summary>
        public void Tick(Vector3 start, Vector3 end, float tension, float maxLength, Color color, float width)
        {
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
        }

        private void Lay(Vector3 start, Vector3 end)
        {
            int count = _points.Length;
            for (int i = 0; i < count; i++)
                _points[i] = _previous[i] = Vector3.Lerp(start, end, i / (count - 1f));
            _live = true;
        }

        private void Simulate(Vector3 start, Vector3 end, float segment, float dt, float gravityScale)
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
            _line.SetPositions(_points);
            _line.startColor = _line.endColor = color;
            _line.widthMultiplier = width;
            _line.enabled = true;
        }
    }
}
