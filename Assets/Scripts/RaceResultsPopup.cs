using Unity.Netcode;
using UnityEngine;

namespace SyncRush
{
    /// <summary>
    /// TEMP debug popup for M0: shows "Race Finished" and the placements on every client once
    /// RaceStateMachine reaches Results, to confirm the state machine and its replication work.
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

            const float width = 320f;
            float height = 110f + race.FinishCount * 26f;
            var rect = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);

            var title = new GUIStyle(GUI.skin.label) { fontSize = 28, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            var row = new GUIStyle(GUI.skin.label) { fontSize = 18, alignment = TextAnchor.MiddleCenter };

            GUI.Box(rect, GUIContent.none);
            GUILayout.BeginArea(rect);
            GUILayout.Label("Race Finished!", title, GUILayout.Height(44f));

            ulong localId = NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : ulong.MaxValue;
            for (int i = 0; i < race.FinishCount; i++)
            {
                ulong id = race.GetFinisher(i);
                string place = i < Ordinals.Length ? Ordinals[i] : $"{i + 1}th";
                string you = id == localId ? "  (you)" : string.Empty;
                GUILayout.Label($"{place}  -  Player {id}{you}", row, GUILayout.Height(26f));
            }

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", GUILayout.Height(28f)))
                _dismissed = true;
            GUILayout.EndArea();
        }
    }
}
