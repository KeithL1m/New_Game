using System;
using System.Collections;
using Unity.Netcode;
using UnityEngine;

namespace SyncRush
{
    public enum RaceState : byte
    {
        WaitingToStart,
        Countdown,
        Racing,
        // FinalStretch belongs here once the tether system (M1) exists to sever
        // tethers in the last ~10m of the course (GDD §5.6) — not wired up yet.
        Results
    }

    /// <summary>
    /// M0 race flow: WaitingToStart -> Countdown -> Racing -> Results.
    /// Server-authoritative (GDD §7.3: placement/checkpoints are server-owned).
    /// One instance per GameScene load, placed on a NetworkObject in the scene.
    /// </summary>
    public class RaceStateMachine : NetworkBehaviour
    {
        public static RaceStateMachine Instance { get; private set; }

        [Tooltip("Grace period after scene load before the countdown begins, so players finish spawning in.")]
        [SerializeField] private float _startDelay = 3f;

        [SerializeField] private float _countdownDuration = 3f;

        private readonly NetworkVariable<RaceState> _state =
            new(RaceState.WaitingToStart, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly NetworkList<ulong> _finishOrder = new();

        public RaceState State => _state.Value;
        public event Action<RaceState> OnStateChanged;

        private void Awake()
        {
            Instance = this;
        }

        public override void OnDestroy()
        {
            if (Instance == this) Instance = null;
            base.OnDestroy();
        }

        public override void OnNetworkSpawn()
        {
            _state.OnValueChanged += HandleStateChanged;
            if (IsServer)
                StartCoroutine(RunRaceServer());
        }

        public override void OnNetworkDespawn()
        {
            _state.OnValueChanged -= HandleStateChanged;
        }

        private void HandleStateChanged(RaceState previous, RaceState current)
            => OnStateChanged?.Invoke(current);

        private IEnumerator RunRaceServer()
        {
            yield return new WaitForSeconds(_startDelay);

            _state.Value = RaceState.Countdown;
            Debug.Log("[RaceStateMachine] Countdown started.");
            yield return new WaitForSeconds(_countdownDuration);

            _state.Value = RaceState.Racing;
            Debug.Log("[RaceStateMachine] GO.");
        }

        /// <summary>Server-only: called when a player's PlayerRaceProgress crosses the Finish checkpoint.</summary>
        public void ReportFinish(ulong clientId)
        {
            if (!IsServer || _state.Value != RaceState.Racing) return;
            if (_finishOrder.Contains(clientId)) return;

            _finishOrder.Add(clientId);
            Debug.Log($"[RaceStateMachine] Client {clientId} finished in place {_finishOrder.Count}.");

            if (_finishOrder.Count >= NetworkManager.Singleton.ConnectedClientsIds.Count)
                _state.Value = RaceState.Results;
        }
    }
}
