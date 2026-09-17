# Sync Rush — Build Progress

Tracks implementation status against [GDD.md](GDD.md). Update this file whenever a
GDD system is added, changed, or a milestone gate is crossed — don't let it drift.

Last verified against repo: **2026-09-16**, on top of commit `3215c8d`.

---
## Milestone status (GDD §9)

| # | Deliverable | Status | Notes |
|---|---|---|---|
| **M0** | Race state machine, checkpoints, placement, greybox straight course | 🟨 In progress | Greybox straight course built by hand in ProBuilder (`GameScene`: `Platform`, `Starting`/`Checkpoint`/`Finish` pole gates, `Deathzone`). `RaceStateMachine`, `RaceCheckpoint`, `PlayerRaceProgress`, `DeathZone`, `SpawnPointRegistry` written and wired onto those objects. **2026-09-04:** first real two-client test (host + Multiplayer Play Mode virtual player) — host/join/countdown/GO all fired correctly and remote player movement replicated well on both sides. Checkpoint crossing, respawn, and finish/placement reporting not yet specifically exercised in this pass |
| **M1** | Tether + ragdoll-on-impact, 4 players over Relay | ⬜ Not started | No tether or ragdoll scripts yet |
| **M2** | Darts, status effects, ammo economy | ⬜ Not started | |
| **M3** | Course generator, 15 chunks | ⬜ Not started | |
| **M4** | Presentation pass | ⬜ Not started | |
| **M5** | Proximity voice, MVP cinematic, bots, 2–4 scaling | ⬜ Not started | |

## Foundation (GDD §7.1) — pre-M0 groundwork already underway

| Piece | Status | Where |
|---|---|---|
| Join-by-code host/client session (`ConnectionManager` equivalent) | 🟨 Implemented, direct-connect only | `Assets/Scripts/LobbyManager.cs` — 6-char host-chosen code, `PlayerCount` synced via named message. Despite earlier notes here, the code (and its own doc comment) confirms this is still a direct `UnityTransport` connection (`127.0.0.1:7777` in `LobbyScene`'s `NetworkManager`) — actual Unity Relay wrapping is **not implemented yet** ("Relay wrapping comes in Pre-Work"). Matters for the GDD §11 risk register item "test on real Relay from day one, never on localhost" — that risk is not yet being exercised |
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
| `RaceCheckpoint` | 🟨 Implemented, untested | `Assets/Scripts/RaceCheckpoint.cs` — trigger gate with `Kind`/`Order`; gates in `GameScene` run `Starting`(0) → `Checkpoint`(1) → `Checkpoint2`(2) → `Checkpoint3`(3) → `Checkpoint4`(4) → `Checkpoint5`(5) → `Finish`(6), the last five added with the course expansion below |
| `PlayerRaceProgress` | 🟨 Implemented, untested | `Assets/Scripts/PlayerRaceProgress.cs`, added to `PlayerPrefab` — owner-writable checkpoint order (anti-shortcut: rejects out-of-sequence crossings), spawn/respawn placement, reports finish to `RaceStateMachine` via ServerRpc |
| `DeathZone` | 🟨 Implemented, untested | `Assets/Scripts/DeathZone.cs`, attached to `Deathzone` (now a trigger) — respawns the player at their last checkpoint |
| `SpawnPointRegistry` | 🟨 Implemented, untested | `Assets/Scripts/SpawnPointRegistry.cs`, attached to `SpawnPoints` in `GameScene`; reads its own children (`Spawn_0`–`Spawn_3`) as spawn points, assigned per client by `OwnerClientId % count` |

**Fixed during playtesting (2026-09-02):** respawn point was reading `checkpoint.transform.position` directly, but each gate's own transform pivot isn't at its visual/trigger center (`Checkpoint`'s pivot was at world x≈-16.17, past the Finish line) — falling near the checkpoint respawned the player behind Finish. `RaceCheckpoint` now carries an explicit `_respawnPoint` world-space field set to each gate's real center instead of trusting the transform.

**Fixed and verified (2026-09-04):** `PlayerPrefab`'s `ClientNetworkTransform` had `AuthorityMode` set to `Server` (0), which conflicted with the owner-authoritative movement model `SyncRushPlayerController` and the GDD (§7.3) both assume — non-owner clients would not have seen a remote player's position replicate correctly. Set to `Owner` (1). Confirmed working in a live host + Multiplayer Play Mode two-client test the same day: both sides saw remote movement update smoothly.

**Testing gotcha (2026-09-04):** editing a prefab from outside the Editor (e.g. a direct file edit) while a Multiplayer Play Mode virtual player is already running can leave that VP instance's `AssetDatabase` with a stale/null import for the changed asset, even though the file on disk is correct — it showed up as a spurious `[Netcode] NetworkConfig mismatch` on connect. Fix was to touch/resave the file so the VP instance's file watcher picked up the change and reimported it. If this recurs, restart the Multiplayer Play Mode virtual player rather than debugging the network code.

**Next up:** exercise checkpoint crossing, anti-shortcut rejection, respawn-to-last-checkpoint, and finish/placement reporting specifically (this session confirmed lobby → countdown → movement replication, but not the checkpoint systems themselves) — that's what actually closes the M0 kill gate before M1's tether. Also worth deciding when to implement real Relay (currently direct-connect only, see Foundation table above) — either before or as part of that next pass.

**Course expanded (2026-09-16):** the greybox course now has obstacle variety past the `RotationPlat` gauntlet instead of running straight into `Finish`. `Start`, `Platform`, `Platform (1)`/Pendulums, `Checkpoint`/`Platform (3)`, and the gauntlet itself are unchanged. `Platform (2)` is disabled (`MeshRenderer`/`MeshCollider` `enabled=false` — not deleted, reversible) since discrete tiles now serve as the floor instead. Sequence from the gauntlet exit (x≈-154.85), all tiles sized to match `RotationPlat`'s own proportions (12×0.25×5) with its ~2.2m gap convention:

- **Launch pad tiles ×2** (`Tile_LaunchPad_1-2`, `Bounce.cs`) — single lane (z=7), x=-166/-180.2
- **`Checkpoint2`** (`_order=2`) — rest platform + gate, x=-193.4
- **Spin bar tiles ×2** (`Tile_SpinBar_A/B`, 2 parallel lanes z=7/-8) — x=-206.6
- **`Checkpoint3`** (`_order=3`) — x=-219.8
- **Hill** (continuous ramp/plateau/ramp, spans both lanes) — x=-229 to -237
- **`Checkpoint4`** (`_order=4`) — x=-246.2
- **Sliding pillar tiles ×2** (2 parallel lanes) — x=-259.4
- **`Checkpoint5`** (`_order=5`) — x=-272.6
- **Timing gate tiles ×2** (2 parallel lanes) — x=-285.8
- **`Finish`** (`_order` bumped 2→6) — x=-304; `Deathzone` extended (was to x=-258.43, now to -334) to cover it

`Bounce.cs`/`Rotator.cs`/`MovableObs.cs`/`WallMovable.cs` (from `Assets/ObstacleCoursePack/`) were rewritten from the pack's originals to work against `CharacterController` and this project's netcode/physics setup — each needs a kinematic `Rigidbody` (Unity only raises trigger/collision callbacks when at least one side of an overlap has one, and `CharacterController` has none) and `Rigidbody.Move*` rather than raw `transform` writes (`Physics.autoSyncTransforms` is disabled here). `Bounce` also moved from `OnCollisionEnter` (never fires against `CharacterController`) to a trigger + `SyncRushPlayerController.AddImpulse`. `RotationPlat` gauntlet `speed` reduced 60→40 after measuring via a scripted `CharacterController` rig that the real safe crossing window was ~0.62s/3s cycle.

Verified live in Play Mode and by screenshot; **not yet verified by the user in-editor, and the layout still doesn't fully match what they're picturing** — kept for now per their call, but expect further changes here. Also not yet tested: real checkpoint/anti-shortcut interaction against the new mid-course checkpoints or the moved `Finish`, and none of it has been tried in a multi-client session.

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
