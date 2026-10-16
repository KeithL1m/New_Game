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
        // GDD §5.6: the first player into the FinalStretchZone severs every tether and
        // unlocks Shove for the rest of the race. Players can still finish from here.
        FinalStretch,
        Results
    }

    /// <summary>
    /// Race flow: WaitingToStart -> Countdown -> Racing -> FinalStretch -> Results.
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

        /// <summary>True from the final stretch on: ropes are cut and Shove is available (GDD §5.6).</summary>
        public bool RopesCut => _state.Value >= RaceState.FinalStretch;

        /// <summary>Number of players who have crossed the Finish so far (synced to all clients).</summary>
        public int FinishCount => _finishOrder.Count;

        /// <summary>Client ID of the player who finished in the given 0-based place.</summary>
        public ulong GetFinisher(int place) => _finishOrder[place];
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

        /// <summary>
        /// Server-only: a player entered the FinalStretchZone. The first report of the race starts
        /// the final stretch for everyone (one global moment, user decision 2026-10-16).
        /// </summary>
        public void ReportFinalStretch(ulong clientId)
        {
            if (!IsServer || _state.Value != RaceState.Racing) return;

            _state.Value = RaceState.FinalStretch;
            Debug.Log($"[RaceStateMachine] Final stretch started by client {clientId}.");
        }

        /// <summary>Server-only: called when a player's PlayerRaceProgress crosses the Finish checkpoint.</summary>
        public void ReportFinish(ulong clientId)
        {
            if (!IsServer || (_state.Value != RaceState.Racing && _state.Value != RaceState.FinalStretch)) return;
            if (_finishOrder.Contains(clientId)) return;

            _finishOrder.Add(clientId);
            Debug.Log($"[RaceStateMachine] Client {clientId} finished in place {_finishOrder.Count}.");

            if (_finishOrder.Count >= NetworkManager.Singleton.ConnectedClientsIds.Count)
                _state.Value = RaceState.Results;
        }
    }
}
