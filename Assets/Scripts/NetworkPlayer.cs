using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace SyncRush
{
    /// <summary>
    /// Per-player session data (GDD §7.1 PlayerRegistry / NetworkPlayer). For now: team only.
    /// Placement and dart count join it in later milestones.
    ///
    /// Teams: the server auto-assigns each joining player to the smaller team, so four players
    /// make a 2v2. In the lobby a player can ask to switch (RequestTeam), which the server allows
    /// if the team has room. Teams lock once the game scene loads.
    ///
    /// Other code should only ask "who is my partner / are we teammates", never compare team
    /// numbers to fixed values. A future free-for-all can then just put every player on their
    /// own team, and partner lookups return null with no special casing.
    /// </summary>
    public class NetworkPlayer : NetworkBehaviour
    {
        public const int TeamCount = 2;
        public const int MaxTeamSize = 2;
        public const int NoTeam = -1;

        public static readonly string[] TeamNames = { "BLUE", "RED" };
        public static readonly Color[] TeamColors = { new(0.2f, 0.45f, 0.95f), new(0.9f, 0.2f, 0.2f) };

        /// <summary>Raised on every peer when a player spawns, despawns or changes team.</summary>
        public static event Action OnRosterChanged;

        private static readonly List<NetworkPlayer> Spawned = new();
        public static IReadOnlyList<NetworkPlayer> All => Spawned;

        /// <summary>This machine's own player, or null before it spawns.</summary>
        public static NetworkPlayer Local { get; private set; }

        private readonly NetworkVariable<int> _teamId =
            new(NoTeam, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private Renderer _renderer;

        public int TeamId => _teamId.Value;
        public string DisplayName => $"Player {OwnerClientId}";

        /// <summary>Teams can change only in the lobby.</summary>
        public bool TeamsLocked => gameObject.scene.name == LobbyManager.GameSceneName;

        /// <summary>The first other player on this player's team, or null (solo team, or no team yet).</summary>
        public NetworkPlayer Partner
        {
            get
            {
                foreach (var other in Spawned)
                    if (IsTeammateOf(other)) return other;
                return null;
            }
        }

        public bool IsTeammateOf(NetworkPlayer other) =>
            other != null && other != this && TeamId != NoTeam && other.TeamId == TeamId;

        public static int CountOnTeam(int team)
        {
            int count = 0;
            foreach (var p in Spawned)
                if (p.TeamId == team) count++;
            return count;
        }

        private void Awake()
        {
            var model = transform.Find("Model");
            _renderer = model != null ? model.GetComponent<Renderer>() : GetComponentInChildren<Renderer>();
        }

        public override void OnNetworkSpawn()
        {
            Spawned.Add(this);
            if (IsOwner) Local = this;
            _teamId.OnValueChanged += HandleTeamChanged;

            if (IsServer && TeamId == NoTeam)
                _teamId.Value = SmallestTeam();

            ApplyTeamColor();
            OnRosterChanged?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            Spawned.Remove(this);
            if (Local == this) Local = null;
            _teamId.OnValueChanged -= HandleTeamChanged;
            OnRosterChanged?.Invoke();
        }

        /// <summary>Owner asks the server to move them to <paramref name="team"/>. Ignored if full or locked.</summary>
        public void RequestTeam(int team) => RequestTeamRpc(team);

        [Rpc(SendTo.Server)]
        private void RequestTeamRpc(int team)
        {
            if (TeamsLocked || team < 0 || team >= TeamCount || team == TeamId) return;
            if (CountOnTeam(team) >= MaxTeamSize) return;
            _teamId.Value = team;
        }

        /// <summary>Server: the team with the fewest players, lowest number on a tie. Excludes this player.</summary>
        private int SmallestTeam()
        {
            int best = 0;
            int bestCount = int.MaxValue;
            for (int team = 0; team < TeamCount; team++)
            {
                int count = 0;
                foreach (var p in Spawned)
                    if (p != this && p.TeamId == team) count++;
                if (count < bestCount)
                {
                    best = team;
                    bestCount = count;
                }
            }
            return best;
        }

        private void HandleTeamChanged(int previous, int current)
        {
            ApplyTeamColor();
            OnRosterChanged?.Invoke();
        }

        private void ApplyTeamColor()
        {
            if (_renderer == null || TeamId < 0 || TeamId >= TeamColors.Length) return;
            _renderer.material.color = TeamColors[TeamId];
        }
    }
}
