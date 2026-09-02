using UnityEngine;

namespace SyncRush
{
    /// <summary>Kills (respawns) any player whose collider enters it — GDD M0 "greybox course" requirement.</summary>
    [RequireComponent(typeof(Collider))]
    public class DeathZone : MonoBehaviour
    {
        private void Reset()
        {
            GetComponent<Collider>().isTrigger = true;
        }

        private void OnTriggerEnter(Collider other)
        {
            var progress = other.GetComponentInParent<PlayerRaceProgress>();
            if (progress != null)
                progress.RespawnAtLastCheckpoint();
        }
    }
}
