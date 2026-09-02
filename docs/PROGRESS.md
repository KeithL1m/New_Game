# Sync Rush — Build Progress

Tracks implementation status against [GDD.md](GDD.md). Update this file whenever a
GDD system is added, changed, or a milestone gate is crossed — don't let it drift.

Last verified against repo: **2026-09-02** (in progress, on top of commit `5d63a72`).

---
## Milestone status (GDD §9)

| # | Deliverable | Status | Notes |
|---|---|---|---|
| **M0** | Race state machine, checkpoints, placement, greybox straight course | 🟨 In progress | Greybox straight course built by hand in ProBuilder (`GameScene`: `Platform`, `Starting`/`Checkpoint`/`Finish` pole gates, `Deathzone`). `RaceStateMachine`, `RaceCheckpoint`, `PlayerRaceProgress`, `DeathZone`, `SpawnPointRegistry` written and wired onto those objects — not yet playtested with a real second client |
| **M1** | Tether + ragdoll-on-impact, 4 players over Relay | ⬜ Not started | No tether or ragdoll scripts yet |
| **M2** | Darts, status effects, ammo economy | ⬜ Not started | |
| **M3** | Course generator, 15 chunks | ⬜ Not started | |
| **M4** | Presentation pass | ⬜ Not started | |
| **M5** | Proximity voice, MVP cinematic, bots, 2–4 scaling | ⬜ Not started | |

## Foundation (GDD §7.1) — pre-M0 groundwork already underway

| Piece | Status | Where |
|---|---|---|
| Join-by-code host/client session (`ConnectionManager` equivalent) | ✅ Implemented | `Assets/Scripts/LobbyManager.cs` — 6-char host-chosen code, NGO relay, `PlayerCount` synced via named message |
| Lobby ↔ Game scene split | ✅ Implemented | `Assets/Scenes/LobbyScene.unity`, `Assets/Scenes/GameScene.unity` (commit `ab0fc98`) |
| Lobby UI | ✅ Implemented | `Assets/Scripts/LobbyUI.cs` (+ `Assets/Editor/RebuildLobbyUI.cs`) |
| Owner-simulated movement (`CharacterController`) | ✅ Implemented, GDD values wired in Inspector | `Assets/Scripts/SyncRushPlayerController.cs` — base 7 m/s, sprint 10 m/s, air control 0.4, jump 1.8 m, coyote 0.12 s, stamina drain/recover |
| `PlayerInputReader` snapshot | ✅ Implemented | `Assets/Scripts/PlayerInputReader.cs`, `Assets/Scripts/InputSystem_Actions.cs` |
| Camera controller | ✅ Implemented | `Assets/Scripts/PlayerCameraController.cs` |
| `PlayerRegistry` / team ID / placement / dart count | ⬜ Not yet | Not present in `LobbyManager` yet |
| `SpawnPointRegistry` | ⬜ Not yet | |

## M0 systems (GDD §7.2, this milestone's slice)

| System | Status | Where |
|---|---|---|
| `RaceStateMachine` | 🟨 Implemented, untested | `Assets/Scripts/RaceStateMachine.cs` — server-authoritative `WaitingToStart → Countdown → Racing → Results`. `FinalStretch` deliberately omitted until M1's tether exists to sever (GDD §5.6) |
| `RaceCheckpoint` | 🟨 Implemented, untested | `Assets/Scripts/RaceCheckpoint.cs` — trigger gate with `Kind`/`Order`; attached to `Starting` (0), `Checkpoint` (1), `Finish` (2) in `GameScene`, sized to each gate's pole span |
| `PlayerRaceProgress` | 🟨 Implemented, untested | `Assets/Scripts/PlayerRaceProgress.cs`, added to `PlayerPrefab` — owner-writable checkpoint order (anti-shortcut: rejects out-of-sequence crossings), spawn/respawn placement, reports finish to `RaceStateMachine` via ServerRpc |
| `DeathZone` | 🟨 Implemented, untested | `Assets/Scripts/DeathZone.cs`, attached to `Deathzone` (now a trigger) — respawns the player at their last checkpoint |
| `SpawnPointRegistry` | 🟨 Implemented, untested | `Assets/Scripts/SpawnPointRegistry.cs`, attached to `SpawnPoints` in `GameScene`; reads its own children (`Spawn_0`–`Spawn_3`) as spawn points, assigned per client by `OwnerClientId % count` |

**Fixed during playtesting (2026-09-02):** respawn point was reading `checkpoint.transform.position` directly, but each gate's own transform pivot isn't at its visual/trigger center (`Checkpoint`'s pivot was at world x≈-16.17, past the Finish line) — falling near the checkpoint respawned the player behind Finish. `RaceCheckpoint` now carries an explicit `_respawnPoint` world-space field set to each gate's real center instead of trusting the transform.

**Known issue to check before multi-client testing:** `PlayerPrefab`'s `ClientNetworkTransform` has `AuthorityMode` set to `Server`, which conflicts with the owner-authoritative movement model `SyncRushPlayerController` and the GDD (§7.3) both assume. This likely means non-owner clients won't see a remote player's position replicate correctly. It doesn't block testing the checkpoint logic itself (each client's own crossings are detected locally), but will show up as "other players don't visually move right." Flagged, not yet fixed — wasn't in scope of this pass without checking with you first.

## Remaining core systems (GDD §7.2) — not started

`TetherSystem`, `RagdollController`, `CourseGenerator`, `StatusEffectSystem`,
`DartProjectile`, `ProximityVoice`, `BotRacer`, presentation layer (cel shader,
impact frames, SFX text) — none present in `Assets/Scripts` as of last verification.

---
## Reading this file
- ✅ = implemented and roughly matches the GDD spec
- 🟨 = started / partial, needs follow-up
- ⬜ = not started
- When you finish a system, move it here with the file path and a one-line note on
  any deviation from the GDD spec (deviations are expected and fine — just record
  them so the GDD and the code don't silently diverge).
