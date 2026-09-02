# SYNC RUSH — Game Design Document
**Version:** 0.1 (concept lock)
**Date:** 2026-08-19
**Engine:** Unity 6 · URP · Netcode for GameObjects 2.x
**Team:** Solo
**Status:** Candidate A of 2. Scope cuts from the 2026-08-19 comparison are applied throughout.

> For current build status against this doc, see [PROGRESS.md](PROGRESS.md).

---
## 1. Pitch
> Four players, two teams, one absurd racecourse. You're roped to your partner and
> you need each other to move fast — right up until the finish line, where only one
> person gets to be the main character.

A 3–5 minute physics comedy race where cooperation is mechanically enforced and
betrayal is mechanically rewarded.

---
## 2. Design pillars
**1. The rope is the game.**
Movement alone should be merely fine. Movement *with a partner* should be
exhilarating. If a mechanic doesn't interact with the tether, question whether it
belongs.

**2. Losing should be funnier than winning.**
Every failure state must produce a watchable moment. A player who gets soap-darted
off a ledge should be laughing before they're annoyed. If a mechanic punishes without
entertaining, cut it.

**3. Every round ends in a betrayal.**
The final ten metres dissolve the teams. Players should feel the turn coming and
still be surprised by who does it first.

> **Tiebreak rule for design disputes:** whichever option produces the better
> 15-second clip wins. Clips are the entire distribution strategy for this genre.

---
## 3. Player experience goals
| Moment | Target feeling |
|---|---|
| First successful slingshot | "Oh, we can *go*." |
| Getting mirror-darted at full speed | Helpless laughter, not rage |
| Watching your partner ragdoll off a cliff | Guilt, then delight |
| The last ten metres | Adrenaline and treachery |
| Post-round MVP cinematic | Smug, or vengeful |

---
## 4. Core loop
```
Lobby (join by code)
   ↓
Seed announced → all clients build the same course
   ↓
┌── RACE (3–5 min) ────────────────────────────────┐
│  Traverse  →  Tether play  →  Earn darts        │
│      ↑                              ↓            │
│      └──────  Sabotage / recover  ──┘            │
└──────────────────────────────────────────────────┘
   ↓
FINAL STRETCH (last ~10m): tethers disabled, shoving enabled
   ↓
MVP cinematic for first to touch → team result → cosmetic currency
   ↓
Rematch (same lobby, new seed)
```
Target round length: **3–5 minutes.** Lobby-to-lobby with no loading screen between
rematches — the rematch button is the retention mechanic.

---
## 5. Mechanics
### 5.1 Movement
Kinematic `CharacterController` capsule. Responsive, predictable, owner-simulated.

| Parameter | Value | Note |
|---|---|---|
| Base speed | 7 m/s | Faster than the foundation's default; this is a race |
| Sprint | 10 m/s | Drains a short stamina meter |
| Air control | 0.4 | Enough to correct, not enough to fly |
| Jump height | 1.8 m | |
| Coyote time | 0.12 s | Already in the foundation |

### 5.2 Ragdoll-on-impact *(scope cut applied)*
**Not** full active ragdoll. The character is kinematic until an impact exceeds a
threshold, then swaps to a ragdoll for a short window and blends back.

```
Kinematic  ──[impulse > 8 m/s]──>  Ragdoll (1.5–2.5s)  ──[blend 0.4s]──>  Kinematic
```

- Ragdoll is **owner-simulated**. The owner replicates its ragdoll root transform;
  remote clients run a local visual ragdoll corrected toward that root.
- Recovery time scales with impact severity — bigger crash, longer sprawl, funnier.
- The ragdoll rig is the same skeleton as the animated rig. One character rig total.

> **Why this cut matters:** full active ragdoll across a network requires solving
> non-deterministic physics with distributed authority. It is the single most likely
> thing to consume three months and produce nothing playable. This version keeps the
> comedy and removes the risk. Revisit active ragdoll only after the loop is proven.

### 5.3 The Sync Tether — the central mechanic
**Critical design translation:** the tether is *not* a physics joint between two
rigidbodies. It is a **gameplay contract simulated by exactly one owner at a time**,
replicated as state plus discrete events.

This is what makes it netcode-safe. A real two-body joint spanning two different
owning clients has no correct authority and will fight itself over the network.

**Tether states:**
| State | Owner | Behaviour |
|---|---|---|
| `Idle` | — | Visual rope only, no gameplay effect |
| `Anchored` | Anchor player | Anchor locks position; tension accumulates with partner distance |
| `Slingshot` | Launched player | Scripted velocity curve applied on release |
| `Tow` | Lead player | Trailing player pulled along a spline, gains % of leader's speed |
| `Recover` | Healthy player | Reels in a ragdolled partner |

**Slingshot detail:**
```
Anchor holds  →  partner runs out to max 12m  →  tension 0→1 over 1.2s
              →  release  →  impulse = lerp(6, 22 m/s) along the rope direction
                          →  IMPACT FRAME + speed lines at tension > 0.8
```
Max rope length 12 m. Beyond that, tension caps and the rope visually strains — no
hard yank, because a hard yank across the network feels like a desync even when it
isn't.

**Cooldown:** 6 s per pair. Prevents slingshot-spam becoming the only movement.

### 5.4 Player scaling *(scope cut applied)*
**The unifying rule: the tether can target *any* player, not just a teammate.**
This one change makes the core mechanic work at every player count, and creates the
best comedy in the game — tethering to a rival helps them too.

| Players | Mode | Tether targeting |
|---|---|---|
| 2 | 1v1 Duel | Rival only. Mutually assured acceleration. |
| 3 | Triple Threat (FFA) | Any rival. Shifting temporary alliances. |
| 4 | **2v2 (canonical)** | Partner by default, rival by aim-target |
| 1 | Solo vs bots | Bots fill to 4 |

**Bots are a launch requirement, not a stretch goal.** A game that needs four humans
to function dies the first quiet evening. Bot AI is a spline-follower with injected
error, a reaction delay, and a dart budget — deliberately simple.

### 5.5 Sabotage darts
Light projectiles with travel time and arc. Aiming is a skill; the effects are
comedic, never damaging.

| Dart | Effect | Duration |
|---|---|---|
| **Heavy** | Gravity ×2.5 — drops rivals out of jumps | 4 s |
| **Soap** | Ground friction → 0.05 — uncontrollable sliding | 5 s |
| **Mirror** | Horizontal input inverted | 3 s |

**Ammo economy as catch-up:** darts are earned on a timer that runs *faster the
further behind you are*. Last place fills a dart roughly 2.5× faster than first.

> This is rubber-banding expressed through player agency instead of through speed.
> The trailing player is given *tools* rather than free velocity, so a comeback still
> feels earned. Speed-based rubber-banding is the standard solution and it makes
> leading feel meaningless.

**Cap:** 2 darts held. Forces use rather than hoarding.

### 5.6 The final stretch
Last ~10 m of the course:
- All tethers sever with a visible SNAP and a sound cue
- Shove becomes available to everyone (short-range physics impulse)
- Music cuts to a single sustained note

**Scoring split, so betrayal doesn't break the team game:**
- **Team result** — determined by combined finishing positions. This is what
  progression tracks.
- **MVP** — the individual who touches the goal first. Gets the cinematic, a cosmetic
  currency bonus, and bragging rights.

You can lose MVP and still win the round. You can be MVP on the losing team. Both
outcomes are funny, which is the point.

---
## 6. Content plan — modular procedural courses
Hand-authoring courses is an infinite content problem. Generating them from
hand-authored chunks converts it into a finite tooling problem solved once.

**Chunk grammar:**
```
START → [TRAVERSAL × 2-3] → HAZARD → [TRAVERSAL × 2-3] → SET_PIECE
      → [TRAVERSAL × 2-3] → HAZARD → FINAL_STRETCH → FINISH
```

**Chunk library target for v1: 20 chunks.**
| Type | Count | Description |
|---|---|---|
| Traversal | 8 | Gaps, ramps, narrow beams — tether-friendly geometry |
| Hazard | 6 | Rotating blades, collapsing tiles, wind tunnels |
| Set-piece | 4 | Big scripted moments — a collapsing bridge, a giant fan |
| Start / Finish | 2 | Fixed |

**Determinism:** the host picks a seed and sends it in the session start message. All
clients build an identical course from that seed. Assembly is deterministic; only
*physics* is not, and no physics runs during assembly. Networking cost: one integer.

**Themes for v1: one.** The floating spirit city. A second theme is a v1.1 update and
excellent marketing beat.

---
## 7. Technical architecture
### 7.1 What the existing foundation already gives you
| Foundation piece | Used as-is? |
|---|---|
| `ConnectionManager` (Relay, join by code) | ✓ Unchanged |
| `PlayerRegistry` / `NetworkPlayer` | ✓ Add team ID, placement, dart count |
| `GameSceneManager` | ✓ Unchanged |
| `SpawnPointRegistry` | ✓ Spawn points come from the START chunk |
| Client-authoritative movement | ✓ **Correct choice for this game** |
| `PlayerInputReader` snapshot | ✓ Extends to tether + dart inputs |
| UI Toolkit lobby | ✓ Add team assignment and mode selector |

**This is the strongest argument for this idea:** almost nothing in the foundation is
wasted, and the client-authoritative model that would be wrong for a competitive game
is exactly right for a party game.

### 7.2 New systems required
| System | Complexity | Notes |
|---|---|---|
| `RaceStateMachine` | Low | Countdown → Racing → FinalStretch → Results |
| `CheckpointSystem` | Low | Placement, respawn, anti-shortcut |
| `CourseGenerator` | **Medium** | Seeded deterministic chunk assembly |
| `TetherSystem` | **High** | The one genuinely hard piece |
| `RagdollController` | Medium | Kinematic ↔ ragdoll blending, networked |
| `StatusEffectSystem` | Low | Networked timed modifiers |
| `DartProjectile` | Low | Server-validated hit, client-predicted visual |
| `ProximityVoice` | Medium | Vivox positional; Doppler is custom DSP |
| `BotRacer` | Low | Spline follower with noise |
| Presentation layer | Medium | Cel shader, impact frames, SFX text |

### 7.3 Authority model
| Thing | Authority | Reason |
|---|---|---|
| Own movement | Owner | Responsiveness |
| Own ragdoll | Owner | Follows movement authority |
| Tether state | Initiating player | One owner per contract, always |
| Dart projectile | Owner-predicted, server-confirmed | Feel + validation |
| Status effects | Server | Cannot be self-cleared |
| Placement / checkpoints | Server | Cannot be spoofed |
| Course seed | Server | Single source |

---
## 8. Art & audio direction
**Visual:** Cel-shaded low-poly, bold ink outlines, high saturation. Character
silhouettes must be readable at 40 m and at speed — this constrains costume design
more than style does.

**Impact language:**
- Black-and-white impact frames on high-tension slingshots and heavy collisions
  (2–3 frames, ~0.1 s)
- 3D sound-effect text as world-space billboards (`THWACK!`, `DOKI-DOKI`, `SLIP!`)
- Speed lines as a screen-space effect above 9 m/s

**Dynamic music:** Two synchronised stems, crossfaded by placement.
- Leading → triumphant anime-rock
- Trailing → urgent "boss battle" variant, same BPM and key so the crossfade is
  seamless as positions swap

**Proximity voice:** Positional falloff with a Doppler pitch shift proportional to
relative velocity. The design intent is that you hear a rival's scream *pitch up as
they rocket past you*. This is the single most distinctive audio idea in the project
and worth the custom DSP work — but it is scheduled late, after the loop is proven.

---
## 9. Build order with kill gates
Each milestone ends with a question. **If the answer is no, stop and reassess** —
this structure exists so you find out cheaply.

| # | Duration | Deliverable | Kill gate |
|---|---|---|---|
| **M0** | 2 wk | Race state machine, checkpoints, placement, greybox straight course | — (plumbing) |
| **M1** | 4 wk | Tether + ragdoll-on-impact, 4 players over Relay | **Is the tether fun with one real friend?** |
| **M2** | 3 wk | Darts, status effects, ammo economy | **Does sabotage produce laughter or resentment?** |
| **M3** | 3 wk | Course generator, 15 chunks | **Does a generated course read as designed?** |
| **M4** | 4 wk | Presentation pass — cel shading, impact frames, SFX text, dynamic music | **Would a stranger forward a 30-second clip?** |
| **M5** | 4 wk | Proximity voice, MVP cinematic, bots, 2–4 scaling | **Is it still fun on the fifth round?** |

**Total to clippable vertical slice: ~20 weeks.**

M1 is the real gate. If tethered movement isn't fun by week six, this project should
not continue — and you will have spent six weeks, not a year, finding that out.

---
## 10. Explicit non-goals for v1
Writing these down is how the project survives.

- ✗ Full active ragdoll
- ✗ Dedicated servers or matchmaking (join by code only)
- ✗ More than 4 players
- ✗ Progression beyond cosmetic currency
- ✗ Level editor or community courses
- ✗ Console or mobile
- ✗ More than one visual theme
- ✗ Cross-play beyond PC
- ✗ Anti-cheat

---
## 11. Risk register
| Risk | Severity | Mitigation |
|---|---|---|
| Tether feels bad over 80ms latency | **High** | M1 gate. Test on real Relay from day one, never on localhost |
| Genre is crowded post-PEAK | **High** | Compete on the tether hook, not on "physics comedy" generally |
| Comedy virality is a lottery | **High** | Cannot be mitigated, only priced in. Keep budget near zero |
| Ragdoll blending looks broken on remotes | Medium | Owner-authoritative root + local visual ragdoll |
| Procedural courses feel samey | Medium | Set-piece chunks as guaranteed memorable beats |
| Four-player requirement | Medium | **Solved by design** — bots + 2/3/4 scaling |
| Solo dev burnout | Medium | 3–5 min rounds mean the game is testable and rewarding early |

---
## 12. Success criteria
**Primary:** a 30-second gameplay clip that a person who has never heard of the game
would send to a friend unprompted.

Everything else — wishlists, reviews, sales — follows from that or doesn't happen.

**Secondary:**
- Median session contains ≥ 3 rounds (the rematch button gets pressed)
- New players understand the tether without a tutorial, within one round
- Round length holds at 3–5 minutes across skill levels

---
## 13. Reference points
| Game | What to take | What to avoid |
|---|---|---|
| **PEAK** | Scope discipline; 7 people, 1 month, <$200k, 4.5M+ sales | Don't assume the virality repeats |
| **Fall Guys** | Kinematic controller + ragdoll swap | Its 60-player infrastructure |
| **Gang Beasts** | Comedy of failure | Full active ragdoll |
| **Human Fall Flat** | Co-op physics puzzles | Slow pace |
| **Content Warning** | Proximity voice as content generator | — |
