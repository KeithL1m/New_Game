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
    /// Teams follow the host's chosen game mode (GameModes.Current, picked in the lobby, user
    /// design 2026-10-16). The server auto-assigns each joining player to the open team with the
    /// fewest players, which is balanced BLUE/RED in Teams and a team of their own in
    /// Free-for-all. If the mode lets players choose, they can ask to switch (RequestTeam), which
    /// the server allows if the team has room. Changing the mode reassigns everyone. Teams lock
    /// once the game scene loads.
    ///
    /// Other code should only ask "who is my partner / are we teammates", never compare team
    /// numbers to fixed values or read the mode: in Free-for-all every player is on their own
    /// team, so partner lookups return null with no special casing.
    /// </summary>
    public class NetworkPlayer : NetworkBehaviour
    {
        /// <summary>Most teams any mode can use (one per player in a 4-player Free-for-all).</summary>
        public const int MaxTeams = 4;
        public const int NoTeam = -1;

        public static readonly string[] TeamNames = { "BLUE", "RED", "GREEN", "YELLOW" };
        public static readonly Color[] TeamColors =
            { new(0.2f, 0.45f, 0.95f), new(0.9f, 0.2f, 0.2f), new(0.2f, 0.75f, 0.3f), new(0.95f, 0.8f, 0.15f) };

        /// <summary>Raised on every peer when a player spawns, despawns or changes team.</summary>
        public static event Action OnRosterChanged;

        private static readonly List<NetworkPlayer> Spawned = new();
        public static IReadOnlyList<NetworkPlayer> All => Spawned;

        /// <summary>This machine's own player, or null before it spawns.</summary>
        public static NetworkPlayer Local { get; private set; }

        private readonly NetworkVariable<int> _teamId =
            new(NoTeam, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Renderers tinted with the team colour: the character's body, not its joints (Y Bot's
        // Alpha_Joints stay dark so the limbs still read).
        private Renderer[] _teamRenderers;

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

        /// <summary>
        /// Lobby label: the mode name, plus the team sizes in Teams (e.g. "Teams · 2v1"), or just
        /// the mode name with fewer than 2 players.
        /// </summary>
        public static string ModeLabel
        {
            get
            {
                var mode = GameModes.Current;
                if (Spawned.Count < 2 || !mode.PlayersChooseTeams) return mode.DisplayName;
                var sizes = new List<int>();
                for (int team = 0; team < mode.TeamCount; team++)
                {
                    int count = CountOnTeam(team);
                    if (count > 0) sizes.Add(count);
                }
                sizes.Sort((a, b) => b.CompareTo(a));
                return sizes.Count < 2 ? $"{mode.DisplayName} · all on one team"
                                       : $"{mode.DisplayName} · {string.Join("v", sizes)}";
            }
        }

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
            var all = (model != null ? model : transform).GetComponentsInChildren<Renderer>(true);
            _teamRenderers = System.Array.FindAll(all, r => !(r is LineRenderer) && !r.name.Contains("Joints"));
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
            var mode = GameModes.Current;
            if (TeamsLocked || !mode.PlayersChooseTeams || team < 0 || team >= mode.TeamCount || team == TeamId) return;
            if (CountOnTeam(team) >= mode.MaxTeamSize) return;
            _teamId.Value = team;
        }

        /// <summary>
        /// Server: the mode's team with the fewest players, lowest number on a tie. Excludes this
        /// player. Balanced BLUE/RED in Teams; an empty team (one each) in Free-for-all.
        /// </summary>
        private int SmallestTeam()
        {
            var mode = GameModes.Current;
            int best = 0;
            int bestCount = int.MaxValue;
            for (int team = 0; team < mode.TeamCount; team++)
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

        /// <summary>
        /// Server: after the host changes the mode, clear every team and auto-assign everyone again
        /// in join order, so the teams fit the new mode's rules.
        /// </summary>
        public static void ReassignAllTeams()
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            foreach (var p in Spawned)
                p._teamId.Value = NoTeam;
            foreach (var p in Spawned)
                p._teamId.Value = p.SmallestTeam();
        }

        private void HandleTeamChanged(int previous, int current)
        {
            ApplyTeamColor();
            OnRosterChanged?.Invoke();
        }

        private void ApplyTeamColor()
        {
            if (TeamId < 0 || TeamId >= TeamColors.Length) return;
            foreach (var r in _teamRenderers)
                r.material.color = TeamColors[TeamId];
        }
    }
}
