using System;

namespace SyncRush
{
    public enum GameModeId : byte
    {
        Teams,
        FreeForAll,
    }

    /// <summary>
    /// One selectable game mode: how the lobby sorts players into teams. Everything in the game
    /// itself reads the teams (NetworkPlayer.Partner / IsTeammateOf), never the mode, so a mode is
    /// only these team rules plus a name. Add a new mode by adding an entry to GameModes.All.
    /// </summary>
    public sealed class GameModeDef
    {
        public GameModeId Id { get; }
        public string DisplayName { get; }

        /// <summary>Team ids 0 .. TeamCount-1 are used; at most NetworkPlayer.MaxTeams.</summary>
        public int TeamCount { get; }
        public int MaxTeamSize { get; }

        /// <summary>Players can switch teams in the lobby (JOIN buttons). Otherwise the server assigns.</summary>
        public bool PlayersChooseTeams { get; }

        public GameModeDef(GameModeId id, string displayName, int teamCount, int maxTeamSize, bool playersChooseTeams)
        {
            Id = id;
            DisplayName = displayName;
            TeamCount = teamCount;
            MaxTeamSize = maxTeamSize;
            PlayersChooseTeams = playersChooseTeams;
        }
    }

    /// <summary>
    /// The host picks the mode in the lobby (user design, 2026-10-16). The host's choice is pushed
    /// to every peer by LobbyManager with the player count. It resets to Teams for each new lobby.
    /// </summary>
    public static class GameModes
    {
        public static readonly GameModeDef[] All =
        {
            // BLUE vs RED, auto-balanced, players can switch: 4 = 2v2, 3 = 2v1, 2 = 1v1 (or partners).
            new(GameModeId.Teams, "Teams", teamCount: 2, maxTeamSize: 2, playersChooseTeams: true),
            // Everyone on their own team: no partner rope, leash or Tow; Slingshot targets anyone.
            new(GameModeId.FreeForAll, "Free-for-all", teamCount: 4, maxTeamSize: 1, playersChooseTeams: false),
        };

        public static GameModeDef Default => All[0];

        public static GameModeDef Current { get; private set; } = Default;

        /// <summary>Raised on every peer when the mode changes.</summary>
        public static event Action OnChanged;

        public static GameModeDef Get(GameModeId id)
        {
            foreach (var mode in All)
                if (mode.Id == id) return mode;
            return Default;
        }

        /// <summary>Sets the local copy. The host goes through LobbyManager.SetMode, which also reassigns and syncs.</summary>
        public static void SetCurrent(GameModeId id)
        {
            var mode = Get(id);
            if (mode == Current) return;
            Current = mode;
            OnChanged?.Invoke();
        }
    }
}
