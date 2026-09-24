# Sync Rush — Build Progress

Tracks implementation status against [GDD.md](GDD.md). Update this file whenever a
GDD system is added, changed, or a milestone gate is crossed — don't let it drift.

Last verified against repo: **2026-09-23**, on top of commit `8b23a84` plus uncommitted work (obstacle scripts/prefabs
moved out of `Assets/ObstacleCoursePack/`, player/camera/hazard changes below). Checked against the files, not by
a fresh gameplay pass — the 2026-09-23 changes are compile-clean but not yet confirmed in Play Mode.

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
| Camera controller | ✅ Implemented, reworked 2026-09-23 | `Assets/Scripts/PlayerCameraController.cs` — mouse X yaws the player, mouse/stick Y now pitches the camera (see 2026-09-23 section) |
| `PlayerRegistry` / team ID / placement / dart count | ⬜ Not yet | Not present in `LobbyManager` yet |
| `SpawnPointRegistry` | ⬜ Not yet | |

## M0 systems (GDD §7.2, this milestone's slice)

| System | Status | Where |
|---|---|---|
| `RaceStateMachine` | 🟨 Implemented, untested | `Assets/Scripts/RaceStateMachine.cs` — server-authoritative `WaitingToStart → Countdown → Racing → Results`. `FinalStretch` deliberately omitted until M1's tether exists to sever (GDD §5.6) |
| `RaceCheckpoint` | ✅ Verified, host + MPPM virtual client (2026-09-23) | `Assets/Scripts/RaceCheckpoint.cs` — trigger gate with `Kind`/`Order`; gates in `GameScene` run `Starting`(0) → `Checkpoint`(1) → `Checkpoint2`(2) → `Checkpoint3`(3) → `Checkpoint4`(4) → `Checkpoint5`(5) → `Checkpoint6`(6) → `Finish`(7). Crossing in order confirmed normal. **Not tried over a real remote connection** (Multiplayer Play Mode's virtual client is same-machine, no real network latency) |
| `PlayerRaceProgress` | 🟨 Implemented, anti-shortcut not exercised | `Assets/Scripts/PlayerRaceProgress.cs`, added to `PlayerPrefab` — owner-writable checkpoint order (anti-shortcut: rejects out-of-sequence crossings), spawn/respawn placement, reports finish to `RaceStateMachine` via ServerRpc. No shortcut attempted in play so far (none encountered, not deliberately tested), so the rejection path itself is still unconfirmed |
| `DeathZone` | ✅ Verified single-player and non-host client via MPPM (2026-09-23) | `Assets/Scripts/DeathZone.cs`, attached to `Deathzone` (now a trigger) — respawns the player at their last checkpoint. Confirmed for the non-host client using the Multiplayer Play Mode virtual client. **Not confirmed over an actual remote/online connection** — MPPM's virtual client shares the host's machine, so real latency/replication timing hasn't been exercised |
| `SpawnPointRegistry` | 🟨 Implemented, untested | `Assets/Scripts/SpawnPointRegistry.cs`, attached to `SpawnPoints` in `GameScene`; reads its own children (`Spawn_0`–`Spawn_3`) as spawn points, assigned per client by `OwnerClientId % count` |

**Fixed during playtesting (2026-09-02):** respawn point was reading `checkpoint.transform.position` directly, but each gate's own transform pivot isn't at its visual/trigger center (`Checkpoint`'s pivot was at world x≈-16.17, past the Finish line) — falling near the checkpoint respawned the player behind Finish. `RaceCheckpoint` now carries an explicit `_respawnPoint` world-space field set to each gate's real center instead of trusting the transform.

**Fixed and verified (2026-09-04):** `PlayerPrefab`'s `ClientNetworkTransform` had `AuthorityMode` set to `Server` (0), which conflicted with the owner-authoritative movement model `SyncRushPlayerController` and the GDD (§7.3) both assume — non-owner clients would not have seen a remote player's position replicate correctly. Set to `Owner` (1). Confirmed working in a live host + Multiplayer Play Mode two-client test the same day: both sides saw remote movement update smoothly.

**Testing gotcha (2026-09-04):** editing a prefab from outside the Editor (e.g. a direct file edit) while a Multiplayer Play Mode virtual player is already running can leave that VP instance's `AssetDatabase` with a stale/null import for the changed asset, even though the file on disk is correct — it showed up as a spurious `[Netcode] NetworkConfig mismatch` on connect. Fix was to touch/resave the file so the VP instance's file watcher picked up the change and reimported it. If this recurs, restart the Multiplayer Play Mode virtual player rather than debugging the network code.

**Next up (2026-09-23):** checkpoint crossing in order and respawn-to-last-checkpoint both confirmed for the non-host client via Multiplayer Play Mode's virtual client. Still open:
- **Anti-shortcut rejection** — not deliberately tried yet (no shortcut attempted, so the rejection path is unexercised, not passing-by-observation).
- **Finish/placement reporting** — not mentioned as tested; still needs a run where a non-host client actually finishes.
- **Real remote play** — everything so far is host + MPPM virtual client on one machine, which has no real network latency and shares the same `Application.dataPath`/asset state as the host (see the 2026-09-04 testing gotcha above). None of this has been exercised over an actual remote connection (e.g. two machines over Relay, or even two machines on direct connect) — that's the remaining gap before calling M0's kill gate closed. Also worth deciding when to implement real Relay (currently direct-connect only, see Foundation table above) — either before or as part of that pass.

**Course expanded (2026-09-16):** the greybox course now has obstacle variety past the `RotationPlat` gauntlet instead of running straight into `Finish`. `Start`, `Platform`, `Platform (1)`/Pendulums, `Checkpoint`/`Platform (3)`, and the gauntlet itself are unchanged. `Platform (2)` is disabled (`MeshRenderer`/`MeshCollider` `enabled=false` — not deleted, reversible) since discrete tiles now serve as the floor instead. Sequence from the gauntlet exit (x≈-154.85), all tiles sized to match `RotationPlat`'s own proportions (12×0.25×5) with its ~2.2m gap convention:

- **Launch pad tiles ×2** (`Tile_LaunchPad_1-2`, `Bounce.cs`) — single lane (z=7), x=-166/-180.2
- **`Checkpoint3`** (`_order=3`, renumbered 2026-09-18) — rest platform + gate, x=-193.4
- **Spin bar tiles ×2** (`Tile_SpinBar_A/B`, 2 parallel lanes z=7/-8) — x=-206.6
- **`Checkpoint4`** (`_order=4`, was 3) — x=-219.8
- **Hill** (continuous ramp/plateau/ramp, spans both lanes) — x=-229 to -237
- **`Checkpoint5`** (`_order=5`, was 4) — x=-246.2
- **Sliding pillar tiles ×2** (2 parallel lanes) — x=-259.4
- **`Checkpoint6`** (`_order=6`, was 5) — x=-272.6
- **Timing gate tiles ×2** (2 parallel lanes) — x=-285.8
- **`Finish`** (`_order` bumped 2→6, then 6→7 when the hand-placed big `Checkpoint2` gate at x≈-166.6 became order 2 with its own respawn point) — x=-304; `Deathzone` extended (was to x=-258.43, now to -334) to cover it

`Bounce.cs`/`Rotator.cs`/`MovableObs.cs`/`WallMovable.cs` (originally from `Assets/ObstacleCoursePack/`; as of 2026-09-23 the scripts live in `Assets/Scripts/` and the prefabs in `Assets/Prefabs/Obstacles/`) were rewritten from the pack's originals to work against `CharacterController` and this project's netcode/physics setup — each needs a kinematic `Rigidbody` (Unity only raises trigger/collision callbacks when at least one side of an overlap has one, and `CharacterController` has none) and `Rigidbody.Move*` rather than raw `transform` writes (`Physics.autoSyncTransforms` is disabled here). `Bounce` also moved from `OnCollisionEnter` (never fires against `CharacterController`) to a trigger + `SyncRushPlayerController.AddImpulse`. `RotationPlat` gauntlet `speed` reduced 60→40 after measuring via a scripted `CharacterController` rig that the real safe crossing window was ~0.62s/3s cycle.

Verified live in Play Mode and by screenshot; **not yet verified by the user in-editor, and the layout still doesn't fully match what they're picturing** — kept for now per their call, but expect further changes here. Also not yet tested: real checkpoint/anti-shortcut interaction against the new mid-course checkpoints or the moved `Finish`, and none of it has been tried in a multi-client session.

## Playtest fixes and tuning (2026-09-23)

All confirmed in single-player playtesting per the user (2026-09-23). **Not yet tried over a real remote connection** — a moving-platform carry, a launch/knockback, or the camera's player-collider exclusion could all behave differently with actual network latency (owner-authoritative movement, replication timing); the Multiplayer Play Mode virtual client used for the checkpoint/respawn checks above doesn't exercise that.

**Player (`SyncRushPlayerController.cs`)**
- Moving platforms: player is carried by a kinematic platform while its surface is within 15° of level, and slides down it at 6 m/s when tilted (`_platformCarryAngle`, `_platformSlideSpeed`). `CharacterController` doesn't ride kinematic colliders on its own.
- `Launch(velocity)` sets horizontal knockback and takes the max of current/new upward speed, so overlapping pads resolve to the stronger one instead of stacking. `AddImpulse` still adds (pendulum). Used by `Bounce` and `SpinnerHazard`.
- Jitter fix: the `Model` child is drawn at `InterpolatedPosition` in `LateUpdate`, so the mesh and camera share one smooth path (the root only moves on 50 Hz physics ticks). Interpolation restarts after a teleport (respawn).
- **TEMP DEBUG still on:** `[PushDebug]` logging (`_debugLogPushes = true`) — remove once pushing issues are confirmed gone.

**Camera (`PlayerCameraController.cs`)**
- Pitch: mouse Y / right stick Y tilts the camera around a pivot at the player (`_minPitch -30°`, `_maxPitch 70°`, `_invertY`). Resting view unchanged.
- Yaw is taken straight from the player (was smoothed, which low-passed fast swipes so the camera lagged/stalled). Only the follow anchor is still smoothed.
- Collision probe ignores the player's own colliders; previously sprinting + turning cast through the player's body and collapsed the camera.
- Known, not fixed: mouse vs gamepad is guessed from input size (`sqrMagnitude <= 1.01`), so 1-pixel mouse moves get gamepad sensitivity.

**`RotationX` spinner (`SpinnerHazard.cs`, `RotationX.prefab`)**
- Hit on the bar launches the player (25 m/s sideways + 7 up), once per 0.5 s per player. Bar has solid colliders (can't be walked through) plus trigger boxes for the hit.
- Trigger boxes are re-fitted at runtime to the solid collider plus a fixed 0.3 m margin (`_contactMargin`), so they no longer balloon with object scale (the old local-unit sizes had become metres of overreach on the scaled scene instance).
- Scene instance in `GameScene`: both arms 0.4 m thick and ~84.6 m long (scale overrides on the instance and its child arm), so the top is far narrower than the player and hard to stand on.

**`Bounce` pad**
- Active `Bounce` script is on the child pad (outer object's script is disabled to avoid double launches). `force` raised 27 → 30.8 (~1.3× height; apex ≈ force² / 40 at gravity 20, ≈ 24 m).

**Housekeeping**
- Obstacle scripts/prefabs moved out of `Assets/ObstacleCoursePack/` and are **uncommitted** — commit the new `.meta` files too or script references will break.

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
