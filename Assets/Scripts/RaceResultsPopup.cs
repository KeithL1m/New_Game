using Unity.Netcode;
using UnityEngine;

namespace SyncRush
{
    /// <summary>
    /// TEMP debug popup: shows the placements on every client once RaceStateMachine reaches
    /// Results, plus the GDD §5.6 scoring split: MVP (first to touch the goal) and the team result
    /// (best average finishing place; same as combined places in 2v2, still fair in a 2v1).
    /// Replace with the real results screen in the M4 presentation pass.
    /// </summary>
    public class RaceResultsPopup : MonoBehaviour
    {
        private static readonly string[] Ordinals = { "1st", "2nd", "3rd", "4th" };

        private bool _dismissed;

        private void OnGUI()
        {
            var race = RaceStateMachine.Instance;
            if (race == null || race.State != RaceState.Results || _dismissed) return;

            const float width = 340f;
            float height = 180f + race.FinishCount * 26f;
            var rect = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);

            var title = new GUIStyle(GUI.skin.label) { fontSize = 28, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            var row = new GUIStyle(GUI.skin.label) { fontSize = 18, alignment = TextAnchor.MiddleCenter };

            GUI.Box(rect, GUIContent.none);
            GUILayout.BeginArea(rect);
            GUILayout.Label("Race Finished!", title, GUILayout.Height(44f));

            ulong localId = NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : ulong.MaxValue;
            var placeSum = new float[NetworkPlayer.MaxTeams];
            var placeCount = new int[NetworkPlayer.MaxTeams];
            for (int i = 0; i < race.FinishCount; i++)
            {
                ulong id = race.GetFinisher(i);
                int team = TeamOf(id);
                if (team >= 0 && team < NetworkPlayer.MaxTeams)
                {
                    placeSum[team] += i + 1;
                    placeCount[team]++;
                }

                string place = i < Ordinals.Length ? Ordinals[i] : $"{i + 1}th";
                string you = id == localId ? "  (you)" : string.Empty;
                string mvp = i == 0 ? "  MVP" : string.Empty;
                Color previous = GUI.contentColor;
                if (team >= 0 && team < NetworkPlayer.TeamColors.Length) GUI.contentColor = NetworkPlayer.TeamColors[team];
                GUILayout.Label($"{place}  -  Player {id}{you}{mvp}", row, GUILayout.Height(26f));
                GUI.contentColor = previous;
            }

            GUILayout.Space(8f);
            GUILayout.Label(TeamResult(placeSum, placeCount), title, GUILayout.Height(40f));

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", GUILayout.Height(28f)))
                _dismissed = true;
            GUILayout.EndArea();
        }

        private static int TeamOf(ulong clientId)
        {
            foreach (var player in NetworkPlayer.All)
                if (player.OwnerClientId == clientId) return player.TeamId;
            return NetworkPlayer.NoTeam;
        }

        /// <summary>
        /// The team with the best (lowest) average place wins. Teams with no finisher don't count.
        /// Empty in a duel or free-for-all (every team solo): there the MVP is the winner.
        /// </summary>
        private static string TeamResult(float[] placeSum, int[] placeCount)
        {
            bool anyPair = false;
            foreach (int count in placeCount) anyPair |= count > 1;
            if (!anyPair) return string.Empty;

            int best = -1;
            float bestAverage = float.MaxValue;
            bool tie = false;
            for (int team = 0; team < placeSum.Length; team++)
            {
                if (placeCount[team] == 0) continue;
                float average = placeSum[team] / placeCount[team];
                if (Mathf.Approximately(average, bestAverage)) tie = true;
                else if (average < bestAverage)
                {
                    best = team;
                    bestAverage = average;
                    tie = false;
                }
            }
            if (best < 0) return string.Empty;
            return tie ? "TEAMS DRAW" : $"{NetworkPlayer.TeamNames[best]} TEAM WINS";
        }
    }
}
