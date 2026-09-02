using UnityEngine;

namespace SyncRush
{
    /// <summary>
    /// Fixed spawn points for GameScene. GDD §7.1 calls for spawn points to
    /// come from the course's START chunk once the course generator (§6)
    /// exists; for the M0 greybox straight course these are hand-placed.
    /// </summary>
    public class SpawnPointRegistry : MonoBehaviour
    {
        public static SpawnPointRegistry Instance { get; private set; }

        private Transform[] _spawnPoints;

        private void Awake()
        {
            Instance = this;

            // Populated from direct children so spawn points can just be dragged
            // under this object in the hierarchy — no array wiring in the Inspector.
            _spawnPoints = new Transform[transform.childCount];
            for (int i = 0; i < transform.childCount; i++)
                _spawnPoints[i] = transform.GetChild(i);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>Deterministic per-client assignment — good enough until real lobby seating exists.</summary>
        public Transform GetSpawnPoint(ulong clientId)
        {
            if (_spawnPoints == null || _spawnPoints.Length == 0) return transform;
            return _spawnPoints[(int)(clientId % (ulong)_spawnPoints.Length)];
        }
    }
}
