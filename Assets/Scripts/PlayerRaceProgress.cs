using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SyncRush
{
    /// <summary>
    /// Tracks one player's progress through the race's checkpoint sequence and
    /// handles spawn/respawn placement.
    ///
    /// Checkpoint order is owner-writable, matching this game's client-authoritative
    /// movement model (GDD §7.3: "Own movement | Owner"). Anti-cheat is an explicit
    /// non-goal for v1 (GDD §10) so trusting the owner's crossing report is intentional,
    /// not an oversight.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerRaceProgress : NetworkBehaviour
    {
        private readonly NetworkVariable<int> _checkpointOrder =
            new(-1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        public int CheckpointOrder => _checkpointOrder.Value;

        private CharacterController _cc;
        private Vector3 _respawnPosition;
        private bool _hasRespawnPosition;

        private void Awake()
        {
            _cc = GetComponent<CharacterController>();
        }

        public override void OnNetworkSpawn()
        {
            if (!IsOwner) return;

            // The player object spawns while still in LobbyScene (it migrates into
            // GameScene rather than being recreated), so SpawnPointRegistry.Instance
            // may not exist yet. Re-attempt placement whenever a scene finishes loading.
            SceneManager.sceneLoaded += HandleSceneLoaded;
            TryPositionAtSpawn();
        }

        public override void OnNetworkDespawn()
        {
            if (!IsOwner) return;
            SceneManager.sceneLoaded -= HandleSceneLoaded;
        }

        private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == LobbyManager.GameSceneName)
                TryPositionAtSpawn();
        }

        private void TryPositionAtSpawn()
        {
            var registry = SpawnPointRegistry.Instance;
            if (registry == null) return; // not in GameScene yet

            _checkpointOrder.Value = -1;
            var spawn = registry.GetSpawnPoint(OwnerClientId);
            _respawnPosition = spawn.position;
            _hasRespawnPosition = true;
            Teleport(spawn.position);
        }

        /// <summary>Invoked by RaceCheckpoint.OnTriggerEnter — filters to the local owner.</summary>
        public void NotifyCheckpoint(RaceCheckpoint checkpoint)
        {
            if (!IsOwner) return;
            if (checkpoint.Order != _checkpointOrder.Value + 1) return; // anti-shortcut

            _checkpointOrder.Value = checkpoint.Order;
            _respawnPosition = checkpoint.RespawnPoint;
            _hasRespawnPosition = true;

            if (checkpoint.Kind == CheckpointKind.Finish)
                ReportFinishServerRpc();
        }

        /// <summary>Invoked by DeathZone.OnTriggerEnter — filters to the local owner.</summary>
        public void RespawnAtLastCheckpoint()
        {
            if (!IsOwner || !_hasRespawnPosition) return;
            Teleport(_respawnPosition);
        }

        private void Teleport(Vector3 position)
        {
            _cc.enabled = false;
            transform.position = position;
            _cc.enabled = true;
        }

        [ServerRpc]
        private void ReportFinishServerRpc()
        {
            RaceStateMachine.Instance?.ReportFinish(OwnerClientId);
        }
    }
}
