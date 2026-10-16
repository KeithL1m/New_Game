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

        // ── TEMP DEBUG: F9 skips every player to the last bounce pad (Bounce (8)), which launches
        // onto the final platform and the FinalStretchZone, so the final stretch can be tested
        // without running the course. Also marks every checkpoint before Finish as passed (so
        // finishing isn't rejected) and makes the pad the respawn point. Remove with F8 auto-run.
        [Header("TEMP debug: F9 skip to end")]
        [SerializeField] private Vector3 _debugSkipPoint = new(-278.2f, 32.96f, 7.7f);

        [Tooltip("Sideways spacing between players at the skip point, so they don't land on each other.")]
        [SerializeField] private float _debugSkipSpacing = 3f;

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

        private void Update()
        {
            if (IsOwner && IsSpawned && gameObject.scene.name == LobbyManager.GameSceneName
                && UnityEngine.InputSystem.Keyboard.current?.f9Key.wasPressedThisFrame == true)
                DebugSkipToEndRpc();
        }

        /// <summary>Runs on every peer; each one moves only its own player (owner-simulated movement).</summary>
        [Rpc(SendTo.Everyone)]
        private void DebugSkipToEndRpc()
        {
            var local = NetworkManager.LocalClient?.PlayerObject;
            var progress = local != null ? local.GetComponent<PlayerRaceProgress>() : null;
            if (progress != null) progress.DebugSkipToEnd();
        }

        private void DebugSkipToEnd()
        {
            int finishOrder = -1;
            foreach (var checkpoint in FindObjectsByType<RaceCheckpoint>(FindObjectsSortMode.None))
                if (checkpoint.Kind == CheckpointKind.Finish) finishOrder = checkpoint.Order;
            if (finishOrder < 0) return;

            // Pad is 8 m wide along z: alternate players either side of its centre.
            float side = (OwnerClientId % 2 == 0 ? -1f : 1f) * _debugSkipSpacing * 0.5f;
            Vector3 point = _debugSkipPoint + new Vector3(0f, 0f, side);

            _checkpointOrder.Value = finishOrder - 1;
            _respawnPosition = point;
            _hasRespawnPosition = true;
            Teleport(point);
            Debug.Log($"[PlayerRaceProgress] DEBUG F9: skipped to {point}, checkpoint order {finishOrder - 1}.");
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

        /// <summary>Invoked by FinalStretchZone.OnTriggerEnter — filters to the local owner.</summary>
        public void NotifyFinalStretch()
        {
            if (!IsOwner) return;
            var race = RaceStateMachine.Instance;
            if (race == null || race.State != RaceState.Racing) return; // already started; don't spam the server
            ReportFinalStretchServerRpc();
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

        [ServerRpc]
        private void ReportFinalStretchServerRpc()
        {
            RaceStateMachine.Instance?.ReportFinalStretch(OwnerClientId);
        }
    }
}
