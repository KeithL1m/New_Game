# Sync Rush — Build Progress

Tracks implementation status against [GDD.md](GDD.md). Update this file whenever a
GDD system is added, changed, or a milestone gate is crossed — don't let it drift.

Last verified against repo: **2026-09-02** (commit `ab0fc98`).

---
## Milestone status (GDD §9)

| # | Deliverable | Status | Notes |
|---|---|---|---|
| **M0** | Race state machine, checkpoints, placement, greybox straight course | ⬜ Not started | No `RaceStateMachine` / `CheckpointSystem` in `Assets/Scripts` yet |
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

## Core systems (GDD §7.2) — not yet started

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
