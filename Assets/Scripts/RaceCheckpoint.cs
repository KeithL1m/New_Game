using UnityEngine;

namespace SyncRush
{
    public enum CheckpointKind { Start, Checkpoint, Finish }

    /// <summary>
    /// Trigger gate marking a point along the race course. Order must increase
    /// from Start (0) upward so PlayerRaceProgress can reject out-of-order
    /// crossings (anti-shortcut — GDD §7.2 CheckpointSystem).
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    public class RaceCheckpoint : MonoBehaviour
    {
        [SerializeField] private CheckpointKind _kind;
        [SerializeField] private int _order;

        [Tooltip("World-space point players respawn at after crossing this checkpoint. " +
                 "Set explicitly rather than derived from this object's own transform, " +
                 "since the gate's pivot is not necessarily at its visual/trigger center.")]
        [SerializeField] private Vector3 _respawnPoint;

        public CheckpointKind Kind => _kind;
        public int Order => _order;
        public Vector3 RespawnPoint => _respawnPoint;

        private void Reset()
        {
            GetComponent<BoxCollider>().isTrigger = true;
        }

        private void OnTriggerEnter(Collider other)
        {
            var progress = other.GetComponentInParent<PlayerRaceProgress>();
            if (progress != null)
                progress.NotifyCheckpoint(this);
        }
    }
}
