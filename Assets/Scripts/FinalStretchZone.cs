using UnityEngine;

namespace SyncRush
{
    /// <summary>
    /// Trigger volume covering the last stretch before the Finish (GDD §5.6). The first player to
    /// enter it starts RaceState.FinalStretch for everyone: every rope snaps and Shove unlocks.
    /// Detection runs on the entering player's own machine (it simulates their movement), which
    /// reports to the server, the same path as a Finish crossing.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    public class FinalStretchZone : MonoBehaviour
    {
        private void Reset()
        {
            GetComponent<BoxCollider>().isTrigger = true;
        }

        private void OnTriggerEnter(Collider other)
        {
            var progress = other.GetComponentInParent<PlayerRaceProgress>();
            if (progress != null)
                progress.NotifyFinalStretch();
        }
    }
}
