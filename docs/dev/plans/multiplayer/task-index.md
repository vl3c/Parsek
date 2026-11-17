# Multiplayer implementation - task index

PLAN (2026-11-14, milestone rework 2026-11-15), not started.

This index joins the eleven task plans in this folder into one dependency graph. It lists every
task once, puts each in a wave and in a milestone (section F), names the files that two plans
both edit, orders the owner's PC time, and lists what the graph does not close. It changes no
plan. Where it recommends a reading, the plan files still need the edit; the master plan decides.
It was rebuilt on 2026-11-14 from the plans as they read after the three reconciliation passes,
and again on 2026-11-15 for the milestone ladder; the plans are the source.

| Plan file | Short name | Task headings | Dropped | Live tasks | Pull requests |
|---|---|---|---|---|---|
| `r0-spikes.md` | r0 | 17 | 0 | 17 | 17 |
| `r0b-prep-small.md` | small | 11 | 2 | 9 | 9 |
| `r0b-prep-playback.md` | playback | 21 | 0 | 21 | 20 |
| `r0b-prep-map-watch-codec.md` | map | 14 | 0 | 14 | 13 |
| `r1-mode-and-setup.md` | r1 | 23 | 2 | 21 | 21 |
| `r2-sites.md` | r2 | 15 | 0 | 15 | 15 |
| `r3-export-and-exchange.md` | r3e | 25 | 1 | 24 | 24 |
| `r3-rival-world.md` | r3w | 32 | 1 | 31 | 31 |
| `r4-r6-board-and-rules.md` | r4 | 26 | 2 | 24 | 23 |
| `r5-looks.md` | r5 | 14 | 1 | 13 | 11 |
| `test-and-harness.md` | T | 59 | 7 | 52 | 52 |
| Total | | 257 | 16 | 241 | 236 |

A live task is a heading that is not dropped. P2a-01 is live but is a specification with no pull
request of its own. Four live tasks land inside another task's pull request (P3b-01, R4-03, R5-07,
R5-10; see the "lands in PR of" column), so the 241 live tasks less P2a-01 (240) make 236 pull
requests. R3E-04's golden folder may be a second pull request of that task (r3e :488), which would
make 237. Ten of the 236 are deferred, conditional or optional: P3b-02, P3b-03 (with P3b-01),
P3b-04, R3W-26 and R5-06 wait for the owner to schedule the Watch camera (map 10.2, answered
2026-11-14: later); R2-10, R3E-23, R5-05, T-34 and T-58 land only on an owner answer, a spike
result or as an option. That leaves 226 firm pull requests. R1-19 to R1-22, R3E-25 and R6-12 are
added (2026-11-15): the owner's rulings on the tech tree and the counted parts (one shipped
content table, the stock tree, the creator setting "allow play without scoring", the part sets,
the locked progression settings, flights scored by the parts they carried). R5-14 is added
(2026-11-15): the owner locked the race suit, so the suit choice of the stock suit picker is
blocked in a race save. T-35 and T-36 are added (2026-11-15): T-29's three host lanes were split
so each flies once its scene has race categories. R1-23 and T-37 to T-59 are added (2026-11-15,
the milestone and test-game rework): the test game key in the race manifest, and the test tasks
that let every milestone end on an automated exit proof (the scripted players T-49, T-50, T-53,
T-57, the harvest T-59, the integration suite T-55, the host lanes T-40 to T-44, the plumbing
T-45). Five test tasks were split at the same time: T-21 (its row and leak scan move to T-38),
T-24 (operator, on the overlay), T-28 (its lane joins through T-39), T-29 (moved earlier, its own
race at slot 1) and T-31 (the early half; each scoring task extends its generator). P1-04 is
dropped (Q-04 answered 2026-11-14: its fix landed outside this plan in the launch pad fixes work).
P12-02 and R1-01 are dropped (2026-11-15): since pull request #2611 the folder is the module, so
no name rule places a `ParsekRace*` file, and a module rule cannot land before its first file;
each race module registers in the task that creates its first file (RaceCore R1-03, RaceWorld
R3W-02, RaceExport R3E-02). Every count here comes from a script over the plan headings and their
`Size` / `Depends on` / `PC` lines, not from hand counting.

Citations read `plan :line`, using the short names above. "go" means the owner's go only.

## How the graph was built

- A dependency is hard when the task cannot start or cannot pass its tests without it. A soft
  dependency (a later row of a lane, an optional fallback) is shown in the table but not counted.
- Hard dependencies are taken from each task's own `Depends on` line as it reads today. An order
  stated in that line ("order: after X", "lands after X") counts as hard, because the task cannot
  merge before X: R3W-01 after P2c-03, R5-03 after P2a-02.
- A spike counts as a dependency. Its column in the tables is "spikes". A spike named in a task's
  `Depends on` line or `Spike` field is counted, even when only one step needs it. A spike named
  only where the plan says the task ships placeholders until it (r5 :66-67: R5-01 to R5-07 do not
  wait for S3) is listed as soft.
- An owner gate (an answer to a section 10 question) is shown but not counted in the waves, since
  the owner can answer at any time. "assumed" marks a question the plan already answers with a
  stated assumption; "answered" marks one the owner answered (the answers of 2026-11-15 and
  2026-11-16 are in `design-corrections.md` part 2).
- A task that lands inside another task's pull request takes that task's wave, and that pull
  request waits for the dependencies of both.
- Wave 0 holds tasks whose only need is the owner's go. Wave N holds tasks whose hard
  dependencies all sit in waves below N. Dropped tasks and the P2a-01 specification are in no
  wave.
- Where a plan's task text and its section 2 or 5 disagree, the stricter reading is used. The
  list is below; each row is also a finding in E so the plan can say it in the task line.

### Plans that name work by content, resolved to ids

| Named in a `Depends on` line | Resolved to |
|---|---|
| "every spike report" (S13, r0 :840) | S0 to S12 except S8a, S8b |
| "P2a-02 to P2a-07" (P2b-02, playback :572) | P2a-02, P2a-03, P2a-04, P2a-05, P2a-06, P2a-07 |
| "P6-05 (or its fallback)" (R3W-04, r3w :432) | P6-05; the fallback (r3w :67-70) is soft |
| "owner's yes (question 2)" (R3E-23, r3e :928) | owner gate r3e Q2, not a task |
| "R3 question 2" (R5-05, r5 :379) | owner gate r3w Q2, not a task |
| "questions 10.1 and 10.2" (R5-12, r5 :577) | owner gates r5 Q1, Q2, not tasks |
| "parallel flights" (T-34, formerly named in T :700) | the parallel runner, on main (pull request #2592), driven by T-54's pair runner, a hard dependency of T-34 (E, item 11) |

### Stricter readings taken

| Task | The `Depends on` line says | Other text says | Taken |
|---|---|---|---|
| P2b-02 | P2a-02 to P2a-07, P2c-03 (playback :572) | R5-03 merges before P2b-02 opens the window (playback :57, :731; r5 :47-50) | R5-03 added |
| T-12 | R1-08 (T :375) | verb PRs land one at a time: R1-08, R1-10, R1-11, T-12, T-19 (T :715-716; r1 :836-838) | R1-11 added |

## A. Task table

One table per plan, in plan order. "hard dependencies" are task ids counted in the waves;
"spikes" are spikes counted in the waves. "soft" is shown, not counted. "owner gate" names the
section 10 question. "PC" is the owner's PC use: no, spike, in-game (one batch or a short manual
check), lanes (existing harness lanes), flight (a new lane flown), tier or full tier (a whole
tier run). "lands in PR of" names the task whose pull request carries this one. "wave" is the
earliest wave (section B). A dropped task keeps its heading and says where its work went.

### A.1 `r0-spikes.md` (17 tasks, 0 dropped, 17 pull requests; S8a and S8b run last)

| id | title (short) | size | hard dependencies | spikes | soft (not counted) | owner gate | PC | lands in PR of | wave |
|---|---|---|---|---|---|---|---|---|---|
| S0 | Spike branch and spike instances | S | go | - | - | r0 Q2 (answered: a) | spike | - | 0 |
| S1b | Finer terrain scan of the three spots | S | - | S0, S4a | - | - | spike | - | 2 |
| S2 | KSC science at a moved site, and none from... | M | - | S4b, S5 | - | - | spike | - | 4 |
| S3 | The four looks, replacement sets, a missing... | M | - | S0 | - | - | spike | - | 1 |
| S4a | Probe: where the KSC is and what reads it | S | - | S0 | - | - | spike | - | 1 |
| S4b | Move the working KSC | M | - | S4a | - | - | spike | - | 2 |
| S5 | KSC copies as scenery, cost, terrain parity | M | - | S4a, S4b | - | - | spike | - | 3 |
| S6 | Race mark survival, main menu entry, create... | M | - | S0 | - | - | spike | - | 1 |
| S7 | Flag replica with the stock plaque dialog | S | - | S0, S4b | - | - | spike | - | 3 |
| S8a | File access cost on sync clients (no game) | S | T-28 (runs last, after the end-to-end race on a plain folder) | S0 | - | r0 Q1 (asked at the end, before S8) | spike (no KSP) | - | 17 |
| S8b | A blocking read in KSP versus a worker thread | S | - | S8a | - | - | spike | - | 18 |
| S9 | Which debug actions a mod can observe | M | - | S0 | - | - | spike | - | 1 |
| S10a | Count the relative stretches and their body... | S | - | S0 | - | - | spike (owner saves only) | - | 1 |
| S10b | How far a flattened stretch drifts from its... | S | - | S10a | - | - | spike | - | 2 |
| S11 | Ghost cost per frame at race scale (new) | S | - | S0 | - | - | spike | - | 1 |
| S12 | Hashing and signing inside KSP's Mono (new,... | S | - | S0 | - | - | spike | - | 1 |
| S13 | Close-out: results into the design | S | - | every spike S0..S12 except S8a, S8b (they run last) | - | r0 Q4 (answered: a) | no | - | 5 |

### A.2 `r0b-prep-small.md` (11 tasks, 2 dropped, 9 pull requests)

| id | title (short) | size | hard dependencies | spikes | soft (not counted) | owner gate | PC | lands in PR of | wave |
|---|---|---|---|---|---|---|---|---|---|
| P12-01 | `[forbidden]` takes a list on the right side | S | go | - | - | - | no | - | 0 |
| P12-02 | The Controllers rule places `ParsekRace*` r... | - | dropped: obsolete since pull request #2611 (the folder is the module) | - | - | - | - | - | - |
| P11-01 | Census specs stop pinning the window count | S | go | - | - | - | no | - | 0 |
| P11-02 | One `ParsekUI` constructor body | S | go | - | - | - | lanes | - | 0 |
| P7-01 | The id check on the floor with a fixed char... | S | go | - | - | - | no | - | 0 |
| P7-02 | A save-root accessor on `RecordingPaths` | S | P7-01 | - | - | - | no | - | 1 |
| P1-01 | `HomeSpaceCenter` in `Core`, read by the KS... | S | go | - | rebases on P7-01 if it landed | - | no | - | 0 |
| P1-02 | One launch-area shape test, called by the 5... | S-M | P1-01 | - | starts after the launch pad fixes are on `multiplayer` (on main before the cut, outside this plan) | small Q1 (answered 2026-11-14) | lanes | - | 1 |
| P1-03 | The launch-site circle cache follows the ho... | S | P1-01 | S4a | starts after the launch pad fixes are on `multiplayer` (on main before the cut, outside this plan) | - | lanes | - | 2 |
| P1-04 | Do not keep a partial first read for the pr... | - | dropped: landed outside this plan by the launch pad fixes work | - | - | - | - | - | - |
| P10-01 | The create-and-start core in production | S-M | go | - | - | - | lanes | - | 0 |

### A.3 `r0b-prep-playback.md` (21 tasks, 0 dropped, 20 pull requests)

| id | title (short) | size | hard dependencies | spikes | soft (not counted) | owner gate | PC | lands in PR of | wave |
|---|---|---|---|---|---|---|---|---|---|
| P2c-01 | Make LoopSyncParentIdx read-only on the int... | S | go | - | - | - | no | - | 0 |
| P2c-02 | Engine diagnostics sink: health counters | S | go | - | - | - | lanes | - | 0 |
| P2c-03 | Engine diagnostics sink: frame budget, hist... | S | P2c-02 | - | - | - | lanes | - | 1 |
| P2c-04 | Spawn budget object an engine can share | S | P2c-02 | - | - | - | lanes | - | 1 |
| P2a-01 | The port and the own-timeline catalog, no c... | none (spec) | - | - | P2a-05 builds it | - | no | no PR | - |
| P2a-02 | Engine tree lookup through the catalog (L1) | S | P2a-05, P2c-04 | - | - | - | lanes | - | 2 |
| P2a-03 | Positioner and flight context builder throu... | S | P2a-05 | - | - | - | lanes | - | 1 |
| P2a-04 | Tail-lift recording lookup through the cata... | S | P2a-03 | - | - | - | lanes | - | 2 |
| P2a-05 | RecordedRelativeAnchorPoseResolver through... | M | go | - | - | - | lanes | - | 0 |
| P2a-06 | ProductionAnchorWorldFrameResolver through... | S | P2a-05 | - | - | - | lanes | - | 1 |
| P2a-07 | Map state vectors through the catalog (L6) | S | P2a-05 | - | - | - | lanes | - | 1 |
| P2b-01 | il_compare folds a renamed reference field | S | go | - | - | - | no | - | 0 |
| P2b-02 | The lane type and the seven log-once sets (... | S | P2a-02, P2a-03, P2a-04, P2a-05, P2a-06, P2a-07, P2c-03, R5-03 | - | - | - | no | - | 4 |
| P2b-03 | Orbit cache on the lane (fold) | S | P2b-02 | - | - | - | no | - | 5 |
| P2b-04 | Ghost position entries and reapply stamps o... | S | P2b-03 | - | - | - | no | - | 6 |
| P2b-05 | Loop-unit lookup on the lane (fold) | S | P2b-04 | - | - | - | no | - | 7 |
| P2b-06 | The positioner holds the lane (fold with al... | S | P2b-01, P2b-05 | - | - | - | no | - | 8 |
| P2b-07 | The late-update helpers take the lane | S | P2b-06 | - | - | - | lanes (daily tier, H15) | - | 9 |
| P2b-08 | Watch and ghost-world queries on the lane | M | P2b-06 | - | - | - | lanes | - | 9 |
| P2b-09 | The catalog moves onto the lane | S | P2b-06, P2a-03, P2a-04 | - | - | - | no | - | 9 |
| P2b-10 | The lane owns its resets | S | P2b-09 | - | - | playback Q1 (answered 2026-11-14) | lanes (18-lane set + daily tier) | - | 10 |

### A.4 `r0b-prep-map-watch-codec.md` (14 tasks, 0 dropped, 13 pull requests)

| id | title (short) | size | hard dependencies | spikes | soft (not counted) | owner gate | PC | lands in PR of | wave |
|---|---|---|---|---|---|---|---|---|---|
| P3-01 | Pin the primitive's runtime contract with a... | S | go | - | - | - | in-game | - | 0 |
| P3-02 | Move the guard set and the teardown depth i... | S | P3-01, P2a-07 | - | - | - | no | - | 2 |
| P3-03 | Owner record and a teardown that leaves oth... | S | P3-02 | - | - | - | no | - | 3 |
| P3-04 | Move the ProtoVessel mechanics into `GhostM... | M | P3-03 | - | - | - | lanes | - | 4 |
| P3-05 | The neutral "whose ghost is this" query at... | S | P3-03 | - | - | - | no | - | 4 |
| P3b-01 | A watch target value | S (no own PR) | P2c-04, P2b-10 | - | - | map Q2 (answered 2026-11-14: later, deferred) | no | P3b-03 | 12 |
| P3b-02 | A lane adapter between the watch camera and... | M | P2c-04, P2b-10 | - | - | map Q2 (deferred) | lanes | - | 11 |
| P3b-03 | Enter and follow by target | M | P3b-02 | - | - | map Q2 (deferred) | lanes | - | 12 |
| P3b-04 | Lane-qualified watch query for the playback... | S | P3b-03, P2b-10 | - | - | map Q2 (deferred) | lanes | - | 13 |
| P6-01 | Commit an identity manifest over every comm... | S | go | - | - | map Q1 (assumed A) | no | - | 0 |
| P6-02 | List-level cores for the flat-fallback heal | S | P6-01 | - | - | - | no | - | 1 |
| P6-03 | The payload type; `Read` becomes its first... | M | P6-02 | - | - | - | no | - | 2 |
| P6-04 | `TryValidatePayload` decodes without a `Rec... | S | P6-03 | - | - | - | no | - | 3 |
| P6-05 | A production entry to load a trajectory fro... | S | P6-04 | - | - | - | no | - | 4 |

### A.5 `r1-mode-and-setup.md` (23 tasks, 2 dropped, 21 pull requests)

| id | title (short) | size | hard dependencies | spikes | soft (not counted) | owner gate | PC | lands in PR of | wave |
|---|---|---|---|---|---|---|---|---|---|
| R1-01 | Register the three race modules | - | dropped: each module registers with its first file (R1-03, R3W-02, R3E-02) | - | - | - | - | - | - |
| R1-02 | One helper to find a scenario node (A7, par... | S | go | - | - | - | no | - | 0 |
| R1-03 | The race mark: `RaceLink` and `ParsekRaceSc... | M | P12-01 | S6 | - | - | in-game | - | 2 |
| R1-04 | `RaceGate` and its host | M | R1-02, R1-03 | S4a | - | - | in-game | - | 3 |
| R1-05 | The race mark's row in `LoadReconcilePolicy` | S | R1-03 | - | - | - | no | - | 3 |
| R1-06 | The race grep-audit gates (design 15.4) | M | R1-03 | - | - | - | no | - | 3 |
| R1-07 | Logs and test batches leave `Parsek/Race/`... | S | go | - | - | - | no | - | 0 |
| R1-08 | Read side: codecs, timestamps, local race d... | M | R1-04, P7-01, R3W-02 | - | - | - | no (rides T-13) | - | 4 |
| R1-09 | Seats, slots, looks and names (`RaceSlots`) | S-M | R1-08 | - | - | - | no | - | 5 |
| R1-10 | Create a race (setup core, write side) | M | R1-08, R1-09, P10-01 | S6 | - | - | lanes (RACE-1) | - | 6 |
| R1-11 | Join a race and start over in my seat | M | R1-10 | - | - | - | lanes (RACE-1) | - | 7 |
| R1-12 | Register the race formats with a golden folder | S | R1-03, R1-10, T-01 | - | - | - | no | - | 7 |
| R1-13 | Harness lanes MP-1 and MP-2 | - | dropped: work moved to T-13 | - | - | - | - | - | - |
| R1-14 | Main menu entry, setup dialog shell and Con... | M | R1-12, T-13, R1-16 | S6 | - | r1 Q1 (answered: never hidden) | in-game | - | 9 |
| R1-15 | The Create page | M | R1-14, R1-19, R1-20 | - | - | r1 Q2, Q3 (answered) | in-game | - | 10 |
| R1-16 | Lift the load-and-start route beside P10's... | S | P10-01 | - | - | - | tier run | - | 1 |
| R1-17 | The Join page and "start over in my seat" | M | R1-14, R1-20 | - | - | - | in-game | - | 10 |
| R1-18 | The Multiplayer section in Settings | M | R1-08, R1-09, R1-10, R1-14 | - | - | - | in-game | - | 10 |
| R1-19 | The race content table and its generator | M | R1-10 | S6 | - | - | in-game | - | 7 |
| R1-20 | The tree and part-value check at create,... | M | R1-19, R1-11, R2-02 | S4a, S6 | - | - | in-game | - | 8 |
| R1-21 | The progression settings are held | S | R2-08, R1-10 | - | - | - | in-game | - | 8 |
| R1-22 | The launch warning | S | R1-19, R1-20 | - | - | - | in-game | - | 9 |
| R1-23 | The test game key in the race manifest | S | R1-10, R1-06 | - | - | - | no (rides T-13) | - | 7 |

### A.6 `r2-sites.md` (15 tasks, 0 dropped, 15 pull requests)

| id | title (short) | size | hard dependencies | spikes | soft (not counted) | owner gate | PC | lands in PR of | wave |
|---|---|---|---|---|---|---|---|---|---|
| R2-01 | Site table and the reconcile decision, wire... | M | R1-03, R1-04, P1-02, R3W-02 | S1b, S4a | - | r2 Q1 (answered: refuse the load, R2-02) | in-game | - | 4 |
| R2-02 | Kerbin fingerprint and the modified-Kerbin... | M | R2-01, R1-10 | S4a, S5 | - | - | in-game | - | 7 |
| R2-03 | The three terrain patches in every race save | M | R2-01 | S4b, S5 | - | - | in-game | - | 5 |
| R2-04 | Move the working space center to the home site | M | R2-03 | S4b | - | - | in-game | - | 6 |
| R2-05 | Parsek's own home position follows the home... | S | R2-04, P1-01, P1-03 | S4a | - | - | in-game | - | 7 |
| R2-06 | One scenery copy of the KSC, built inert | M | R2-04 | S5 | - | - | in-game | - | 7 |
| R2-07 | Copies at every other site, as part of apply | M | R2-06 | S5 | - | - | in-game | - | 8 |
| R2-08 | Launch rules: the own site and, when the ra... | M | R2-01, R1-03, R1-10 | S4b | - | r2 Q2 (answered: greyed) | in-game | - | 7 |
| R2-09 | Science parity: the KSC mini-biomes at home... | S | R2-04, R2-07 | S2 | - | - | in-game | - | 9 |
| R2-10 | (conditional on S2) Biome override around t... | S-M | R2-09 | S2 | - | S2 result | in-game | - | 10 |
| R2-11 | Every stock "home" surface at the home site | S-M | R2-04 | S4b | - | - | in-game | - | 7 |
| R2-12 | Harness lane SITES-1: a launch, a recovery... | M | R2-04, R2-05, R2-07, R2-08, R2-09, R2-11, R1-10, R1-11, T-03, T-07 | - | T-46 | - | flight | - | 10 |
| R2-13 | Harness lane SITES-2: back to stock Kerbin,... | M | R2-12, T-07, R1-20 | - | T-46 | - | flight | - | 11 |
| R2-14 | Harness lane SITES-3: launch rules and the... | S-M | R2-08, R2-12 | - | - | - | flight | - | 11 |
| R2-15 | Fold the findings into the design and the p... | S | R2-04, R2-09 | S2, S4b, S5 | - | - | no | - | 10 |

### A.7 `r3-export-and-exchange.md` (25 tasks, 1 dropped, 24 pull requests)

| id | title (short) | size | hard dependencies | spikes | soft (not counted) | owner gate | PC | lands in PR of | wave |
|---|---|---|---|---|---|---|---|---|---|
| R3E-01 | Exchange vocabulary and the own-mirror scan | M | P7-01, P12-01, R1-03, R1-06, R1-07, R1-08, R1-10 | - | - | - | no | - | 7 |
| R3E-02 | Public state from the own model | M | R3E-01 | - | R3W-03 (key list) | - | no | - | 8 |
| R3E-03 | The public ghost snapshot | M | R3E-02 | - | - | - | no | - | 9 |
| R3E-04 | Write a standing to the own mirror; golden v3 | M (golden may be a 2nd PR) | R3E-03, R1-03, R1-08, R1-12, T-01 | - | - | - | no | - | 10 |
| R3E-05 | Synthetic shared-folder builder (test only) | - | dropped: work moved to T-16, R1-08, T-02, R3E-16 tests | - | - | - | - | - | - |
| R3E-06 | The capture switch for race-only facts | S | R1-04, R1-08 | - | - | - | no | - | 5 |
| R3E-07 | The tool-use fact on a recording (A3 data) | S | go | - | - | - | no | - | 0 |
| R3E-08 | Capture the tools Parsek already detects (A3) | M | R3E-06, R3E-07 | - | - | - | in-game | - | 6 |
| R3E-09 | Stock cheat switches and debug actions (A3) | S-M | R3E-08, R3E-15 | S9 | - | - | in-game | - | 12 |
| R3E-10 | Tools in the standing | S | R3E-04, R3E-07 | - | - | - | no | - | 11 |
| R3E-11 | Milestone reach rows (A1) and the MILESTONE... | M | R3E-04 | - | - | - | no | - | 11 |
| R3E-12 | The vessel-ended ledger row type (A2 data) | M | go | - | - | - | no | - | 0 |
| R3E-13 | Record a vessel's end outside a recording (... | M | R3E-06, R3E-12 | - | - | - | in-game | - | 6 |
| R3E-14 | The stand query (A2) and the LEFTOVERS block | M | R3E-04, R3E-12 | - | - | - | no | - | 11 |
| R3E-15 | Disclosure facts | S | R3E-04, R3E-06, R1-03, R1-10 | - | - | - | no | - | 11 |
| R3E-16 | The main-thread transport and the copy-out | M | R3E-04, R1-08, R1-10, T-02 | - | - | - | no | - | 11 |
| R3E-17 | Sync ticks | S | R3E-16 | - | - | - | no | - | 12 |
| R3E-18 | Import: copy-in, completeness and the verdict | M | R3E-16, R3E-22, R1-08, R1-09, T-16 | - | R1-11 (writes the false) | - | no | - | 12 |
| R3E-19 | Witness log, chain, receipts and the neutra... | M | R3E-18, T-18 | - | - | - | no | - | 13 |
| R3E-20 | Self-heal of the own subfolder | M | R3E-16, R3E-19, R3E-22, T-18 | - | - | r3e Q1 (answered: b) | no | - | 14 |
| R3E-21 | Keep and prune | M | R3E-11, R3E-18 | - | - | - | no | - | 13 |
| R3E-22 | "N shared flights left the race" | S | R3E-04 | - | - | - | no | - | 11 |
| R3E-23 | The copy worker (only with the owner's yes) | M | R3E-16 | S8a, S8b | - | r3e Q2 (yes) | in-game | - | 19 |
| R3E-24 | In-game exchange check | S | R3E-18, R3E-20 | - | - | - | in-game | - | 15 |
| R3E-25 | Uncounted parts, save marks and `contentDi... | M | R3E-03, R3E-04, R3E-08, R3E-09, R3E-10, R3E-15, R3E-18, R3E-19, R1-20, R1-22 | - | - | - | no | - | 14 |

### A.8 `r3-rival-world.md` (32 tasks, 1 dropped, 31 pull requests)

| id | title (short) | size | hard dependencies | spikes | soft (not counted) | owner gate | PC | lands in PR of | wave |
|---|---|---|---|---|---|---|---|---|---|
| R3W-01 | Neutral skip reason for host-filtered entries | S | P2c-03 | - | - | - | no | - | 2 |
| R3W-02 | `RivalFlight` value type | S | P2c-01, P12-01 | - | - | - | in-game | - | 1 |
| R3W-03 | Header loader from the tree file | S | R3W-02, R3E-01 | - | - | - | in-game | - | 8 |
| R3W-04 | Payload loader and the flattening of relati... | M | R3W-02, P6-05 | S10a, S10b | P6-05 has a fallback (r3w :67-70) | - | in-game | - | 5 |
| R3W-05 | `RivalStore` | M | R3W-03, R3E-04, T-16 | - | - | - | in-game | - | 12 |
| R3W-06 | `RivalPlaybackPolicy` | S | P2c-03, R3W-02 | - | - | - | in-game | - | 2 |
| R3W-07 | Rival frame: flags and the open queue (pure) | S | R3W-01, R3W-02 | - | - | - | in-game | - | 3 |
| R3W-08 | `RaceRuntime` scene calls | S | R1-08, R3W-05, R3W-06 | - | - | - | in-game | - | 13 |
| R3W-09 | Flight composition (milestone MS-1d) | M | R3W-04, R3W-05, R3W-06, R3W-07, R3W-08, P2a-02, P2a-05, P2a-06, P2a-07, P2b-09, P2b-10, P2c-04, R1-10, R3E-04, T-17, T-19, T-48 | - | - | - | in-game | - | 14 |
| R3W-10 | Lazy open budget and payload lifetime | S | R3W-09 | - | - | - | in-game | - | 15 |
| R3W-11 | Leftovers held at their last moment | M | R3W-09, R3E-14 | - | - | - | in-game | - | 15 |
| R3W-12 | Launch areas kept clear | S | R3W-11, R2-01, P1-02 | - | - | - | no | - | 16 |
| R3W-13 | Show or hide a rival (reader side) | S | R3W-09, R1-08 | - | - | - | no | - | 15 |
| R3W-14 | A5: one ghost budget for both engines | S | P2c-04, R5-03, R3W-09 | S11 | - | - | no | - | 15 |
| R3W-15 | A5: per-trajectory style | S | R3W-09, R5-03 | - | - | - | no | - | 15 |
| R3W-16 | Labels on rival ghosts in flight | S | R3W-15 | - | - | r3w Q3 (answered: a) | in-game | - | 16 |
| R3W-17 | Landed variant of the ghost map vessel prim... | S | P3-04, P3-05 | - | - | - | in-game | - | 5 |
| R3W-18 | `RivalMapPresence` | M | R3W-09, R3W-11, R3W-15, R3W-17, P3-04, P3-05, R5-04 | - | - | r3w Q2 (assumed a) | in-game | - | 16 |
| R3W-19 | "Whose ghost" answers for rivals | S | R3W-18, P3-05 | - | - | - | in-game | - | 17 |
| R3W-20 | Tracking Station composition | S | R3W-18, R3W-19, T-17, T-19, T-48 | - | adds the Tracking Station step to RACE-2 (T-24) | - | in-game | - | 18 |
| R3W-21 | Missing parts | S | R3W-04 | - | - | - | in-game | - | 6 |
| R3W-22 | Removal and replacement at scene load | S | R3W-09, R3E-18 | - | - | - | no | - | 15 |
| R3W-23 | Rival flag records | S | R3W-05, R3W-12, R3E-14 | - | - | - | in-game | - | 17 |
| R3W-24 | Plaque replicas in flight | M | R3W-23 | S7 | - | - | in-game | - | 18 |
| R3W-25 | Reading a rival plaque | S | R3W-24 | S7 | - | r3w Q1 (answered: popup only if S7 fails) | in-game | - | 19 |
| R3W-26 | Watching a live rival ghost (optional) | M | P3b-04, R3W-19 | - | - | optional; deferred with P3b | in-game | - | 18 |
| R3W-27 | Grep gates: rival store names and the loade... | S | R3W-05, R1-06 | - | - | - | no | - | 13 |
| R3W-28 | Source gate: what the rival lane never calls | S | R3W-09 | - | - | - | no | - | 15 |
| R3W-29 | Headless byte-identity test | M | R3W-08, R3W-11, R3W-23, T-01, T-16 | - | - | - | no | - | 18 |
| R3W-30 | In-game isolation category | M | R3W-18, R3W-20, R3W-24 | - | - | - | in-game | - | 19 |
| R3W-31 | Whole-save harness lane | - | dropped: work moved to T-27 | - | - | - | - | - | - |
| R3W-32 | Per-frame cost check | S | R3W-14 | - | T-47 (the scale folder) | - | in-game | - | 16 |

### A.9 `r4-r6-board-and-rules.md` (26 tasks, 2 dropped, 23 pull requests)

| id | title (short) | size | hard dependencies | spikes | soft (not counted) | owner gate | PC | lands in PR of | wave |
|---|---|---|---|---|---|---|---|---|---|
| R4-01 | The race calendar and the date rule | S | R1-08, R1-10 | - | - | - | no | - | 7 |
| R4-02 | The ladder table standard-1 | S | R1-08, R1-10 | - | - | - | no | - | 7 |
| R4-03 | The ladder filter at board time | S | R4-02, R3E-11 | - | - | - | no | R4-04 | 13 |
| R4-04 | The board: shapes, exact points and the bas... | M | R4-01, R4-02, R4-03, R1-08, R3E-01, R3E-04, R3E-18 | - | - | - | no | - | 13 |
| R4-05 | The replay is the proof: claim checks and t... | S | R4-04 | - | - | - | no | - | 14 |
| R4-06 | Rows this install cannot fully read | S | R4-05 | - | - | - | no | - | 15 |
| R4-07 | Holder-change events, message text and posting | S | R4-04, R1-08, R3E-04, R3E-22 | - | - | r4 Q2 (answered: a) | no | - | 14 |
| R4-08 | Shared table-window chrome (A6) | S | go | - | - | - | lanes (optional census) | - | 0 |
| R4-09 | The Space Race window: skeleton and registr... | M | R4-04, R4-08, P11-01, P11-02, R1-04, R1-08, R1-14, R1-18, R3E-17, R1-23 | - | - | - | in-game (R4-13) | - | 14 |
| R4-10 | The row model and the drawn board | M | R4-09, R4-05, R4-06, R5-07, R1-10, R2-01 | - | - | - | in-game (R4-13) | - | 16 |
| R4-11 | Click to watch | S | R4-10 | - | camera half: P3b-04, R3W-26 (deferred) | camera half deferred | in-game (camera half) | - | 17 |
| R4-12 | Gallery mock states and the census | S | R4-10 | - | - | - | lanes (census) | - | 17 |
| R4-13 | In-game checks: the ladder against stock, t... | S | R4-02, R4-12 | - | - | - | in-game | - | 18 |
| R4-14 | Harness: the board in the two-player lanes | - | dropped: work moved to T-28, T-32 | - | - | - | - | - | - |
| R6-01 | Tool marks | S | R4-05, R3E-07, R3E-09, R3E-10, R1-23 | S9 | - | - | no | - | 15 |
| R6-02 | No retakes | M | R6-01, R3E-21 | - | - | r4 Q1 (answered: a) | no | - | 16 |
| R6-03 | The end date and the final board | M | R6-02, R3E-21 | - | - | - | no | - | 17 |
| R6-04 | Arrived late | S | R6-03, R3E-19 | - | - | - | no | - | 18 |
| R6-05 | Disclosure facts | S | R4-10, R6-01, R3E-15 | - | - | - | no | - | 17 |
| R6-06 | Missing-parts, newer-version and cannot-be-... | S | R4-10, R1-08, R3E-18 | - | - | - | no | - | 17 |
| R6-07 | The window at the end and its header | M | R6-03, R6-05, R6-06 | - | - | - | in-game | - | 18 |
| R6-08 | End-of-race messages | S | R6-03, R4-07 | - | - | r4 Q3 (answered: a) | no | - | 18 |
| R6-09 | The no-retakes preview for the Join dialog | - | dropped: R1-17's Join page shows the sentence | - | - | - | - | - | - |
| R6-10 | Poll tuning | S | R3E-17 | S8a, S8b | - | - | in-game | - | 19 |
| R6-11 | No retakes and the end date on generator-bu... | S | R6-02, R6-03, T-16, T-17 | - | - | - | no | - | 18 |
| R6-12 | The parts mark, the save marks and "not sco... | S-M | R4-05, R4-10, R6-01, R6-06, R3E-25, R1-19, T-16 | - | - | - | no | - | 18 |

### A.10 `r5-looks.md` (14 tasks, 1 dropped, 11 pull requests)

| id | title (short) | size | hard dependencies | spikes | soft (not counted) | owner gate | PC | lands in PR of | wave |
|---|---|---|---|---|---|---|---|---|---|
| R5-01 | Ghost build: a part-name substitute before... | S | go | - | - | - | in-game | - | 0 |
| R5-02 | Ghost build: a material override at the clo... | S | R5-01 | - | S3d | - | in-game | - | 1 |
| R5-03 | Engine: per-trajectory build options and st... | S | R5-01, P2c-04, P2a-02 | - | - | - | no | - | 3 |
| R5-04 | Ghost map vessels carry the style (A5) | S | P3-04, R5-03 | - | S3a (colors); in-file order after R3W-17 | r5 Q3 (answered: a) | in-game | - | 5 |
| R5-05 | Parsek-drawn map overlays take the style (c... | S | R5-03 | - | - | r3w Q2 = b only (assumed a: not built) | no | - | 4 |
| R5-06 | Watch overlay title carries the prefix (con... | S | P3b-03, R5-03, R3W-26 | - | - | deferred with P3b, R3W-26 | no | - | 19 |
| R5-07 | The look table's visual half (RaceCore) | S | R1-03 | - | S3a, S3b (placeholder colors); lands with its first caller, R5-08 or R4-10 | - | no | R5-08 | 3 |
| R5-08 | The rival ghost dresser (RaceWorld) | M | R5-02, R5-07, R5-12 | S3 | - | - | in-game | - | 3 |
| R5-09 | The rival lane wears the looks and colors | M | R3W-09, R3W-15, R3W-18, R3W-20, R3W-21, R5-03, R5-04, R5-08 | - | S3c | - | in-game | - | 19 |
| R5-10 | The dress rules (RaceCore, pure) | S | R5-07 | S3 | - | - | no | R5-11 | 6 |
| R5-11 | `RaceLooks`: own roster dressing | M | R1-03, R1-08, R1-09, R5-08, R5-10, R5-12 | S3 | - | - | in-game | - | 6 |
| R5-12 | The replacement suit sets | M | R3W-02 | S3 | - | r5 Q1, Q2 | in-game | - | 2 |
| R5-13 | A harness lane pins the `RaceLooks` category | - | dropped: work moved to T-29 | - | - | - | - | - | - |
| R5-14 | Block the suit choice of the stock suit picker in a race save | M | R5-11, R1-04 | - | - | - | in-game | - | 7 |

### A.11 `test-and-harness.md` (59 tasks, 7 dropped, 52 pull requests)

| id | title (short) | size | hard dependencies | spikes | soft (not counted) | owner gate | PC | lands in PR of | wave |
|---|---|---|---|---|---|---|---|---|---|
| T-01 | Headless save-output driver and its determi... | S | go | - | - | - | no | - | 0 |
| T-02 | Fake synced store and fake clocks | M | go | - | - | - | no | - | 0 |
| T-03 | Shared-folder staging in run.py | M | go | - | - | - | no | - | 0 |
| T-04 | Snapshot every save a lane creates | S | go | - | - | - | no | - | 0 |
| T-05 | Pure race evaluator, part 1: leak scan and... | M | go | - | - | T Q2 (answered: a) | no | - | 0 |
| T-06 | Harness-side folder step | S | T-03 | - | - | - | no | - | 1 |
| T-07 | Seam read kind `ListHandles kind=sites` | M | go | - | - | - | flight | - | 0 |
| T-08 | Race grep gates: shared scanner and "no sto... | - | dropped: work moved to R1-06 | - | - | - | - | - | - |
| T-09 | Race grep gate: Recording types only in the... | - | dropped: work moved to R1-06 | - | - | - | - | - | - |
| T-10 | Single-player inertness test (15.8) | S | T-01, R1-04 | - | - | - | no | - | 4 |
| T-11 | Seam verb `RaceSetup action=create/join` | - | dropped: work moved to R1-10, R1-11 | - | - | - | - | - | - |
| T-12 | Seam action `RaceCommand action=set-folder`... | S | R1-08, R1-11 | - | - | - | no (rides a lane) | - | 8 |
| T-13 | Lane RACE-1-setup-create-join | M | T-03, T-04, R1-10, R1-11, R1-23 | - | R2-01 (the `[RaceSites] Reconcile:` assertion joins in the later of the two) | - | flight | - | 8 |
| T-14 | Lane RACE-5-stock-kerbin-after-race | - | dropped: work moved to R2-13 | - | - | - | - | - | - |
| T-15 | Race grep gate: no writer of the own model | - | dropped: work moved to R1-06 | - | - | - | - | - | - |
| T-16 | Generator `RaceFolderBuilder` | M | R3E-04, R1-10 | - | - | - | no | - | 11 |
| T-17 | Committed shared-folder fixtures | S | T-16 | - | - | - | no | - | 12 |
| T-18 | Two-machine simulator bound to the ports | M | T-02, R3E-04, R3E-16 | - | R3E-20 (self-heal case) | - | no | - | 12 |
| T-19 | `RaceCommand action=sync` | S | T-12, R3E-17, R3E-18 | - | - | - | no (rides a lane) | - | 13 |
| T-20 | `ListHandles kind=rivals` | S | R3W-05, R3W-09 | - | - | - | no (rides a lane) | - | 15 |
| T-21 | Race evaluator, part 2: structure facets an... | M | T-05, T-03, R3E-04, T-38 | - | R3E-19 (adds the witness-log facet) | - | no | - | 11 |
| T-22 | Race grep gate: rival store names only in a... | - | dropped: work moved to R3W-27 | - | - | - | - | - | - |
| T-23 | Isolation byte-identity test (6.4) | - | dropped: work moved to R3W-29 | - | - | - | - | - | - |
| T-24 | Lane RACE-2-import-generator-folder | S | T-17, T-19, T-20, T-21 | - | R3W-20 (adds the Tracking Station step) | - | flight | - | 16 |
| T-25 | Lane RACE-8-export-self-heal-offline | S | T-19, T-21, T-06, R3E-20 | - | - | - | flight | - | 15 |
| T-26 | Lane RACE-3-rival-ghosts-flight | M | T-17, T-19, T-20 | - | - | - | flight | - | 16 |
| T-27 | Lane RACE-4-isolation-whole-save | M | T-04, T-05, T-17, T-19 | - | R3W-18, R3W-24 (content) | T Q2 (answered: a) | flight | - | 14 |
| T-28 | Lane RACE-6-two-saves-in-turn | M | R1-10, R1-11, T-19, T-20, T-21, R3E-16, T-39 | - | R4-04 (row assertion) | T Q1 (assumed a) | flight | - | 16 |
| T-29 | In-game race context helper and the Space Center host lane RACE-9 | M | T-03, R1-10, R1-11 | - | R1-07 (logContract); T-17, T-19, R2-03, R2-05, R5-11, R3W-05 and the first test of each other Space Center category (each joins RACE-9 in its own PR) | - | flight | - | 8 |
| T-30 | `ListHandles kind=board` | S | R4-04, R1-23 | - | T-40 (extends it to every mark) | - | no (rides a lane) | - | 14 |
| T-31 | "Same files, same board" (early half; T-37 is the late half) | M | T-16, T-18, R4-04 | - | each scoring task extends its generator | - | no | - | 14 |
| T-32 | Lane RACE-7-holder-change-folder-update | S | T-17, T-19, T-30, T-06, R4-07 | - | - | - | flight | - | 15 |
| T-33 | Lane RACE-12-flown-rival-campaign (harvest by T-59) | S | R1-10, T-21, R3E-16, T-59 | - | - | - | flight | - | 12 |
| T-34 | Lane RACE-13-paired-copies (optional; a spec on T-54) | S | T-54, T-21, T-30 | - | - | T Q3 (assumed a); optional | flight | - | 15 |
| T-35 | Lane RACE-10-flight-batch (the FLIGHT host) | S | T-29 | - | R3W-09 (moves it onto the staged race), R3W-30 and the first test of each other FLIGHT category | - | flight | - | 9 |
| T-36 | Lane RACE-11-ts-batch (the TRACKSTATION host) | S | T-29 | - | R3W-20 (moves it onto the staged race), R3W-30 | - | flight | - | 9 |
| T-37 | The changed-file scenario and the keep rule after the end date (late half of T-31) | S | T-31, R3E-19, R3E-20, R6-03 | - | - | - | no | - | 18 |
| T-38 | `raceParse` verifier row with the gated leak scan (early half of T-21) | S | T-03, T-05 | - | - | - | no | - | 1 |
| T-39 | Lane RACE-6, first form: two saves exchange through the seam only | M | R3E-17, R3E-18, T-03, T-04, T-38, R1-11 | - | T-28 completes the lane | - | flight | - | 13 |
| T-40 | Lane RACE-14-scoring-marks; `kind=board` and `RaceCommand status` show every mark | M | T-17, T-19, T-30, R6-12, R6-02, R6-03 | - | - | - | flight | - | 19 |
| T-41 | Lane RACE-15-content-check-launch-warning (stock-minimal) | S | R1-20, R1-22, R1-11, T-03, T-04 | - | - | - | flight | - | 10 |
| T-42 | Committed race-save fixtures and the refused-load lanes RACE-16, RACE-17 | M | T-13, R2-02, R1-20 | - | - | - | flight | - | 9 |
| T-43 | Lane RACE-19-setup-screens: setup screens through the seam | M | R1-15, R1-17, R1-18 | - | - | - | flight | - | 11 |
| T-44 | Single-player negative contract and lane RACE-20-single-player-hosts | S | R1-04, R1-06 | - | - | - | flight | - | 4 |
| T-45 | Race lane plumbing: `race` tag, `parallel_runner --tag`, CI push on `multiplayer` | S | go | - | - | - | no | - | 0 |
| T-46 | Sites tolerance comparator (`ListHandles kind=sites ... tolerance=`) | S | T-07, R2-01 | - | - | - | no | - | 5 |
| T-47 | Rival fixture content for hosted cells | M | T-16 | - | - | - | no | - | 12 |
| T-48 | Race overlay at staging (`[fixture.raceOverlay]`) | M | T-03, R1-11, R1-12, R1-23 | - | - | - | no (rides T-53) | - | 8 |
| T-49 | Campaign chain runner | M | go | - | - | - | lanes (one CS chain replay) | - | 0 |
| T-50 | Race campaign spec generator (RCA-k / RCB-k) | M | T-03, T-12, T-19, T-30, T-48 | - | - | - | no | - | 15 |
| T-51 | Board oracle and its mutation checks | M | T-21, T-30, R4-02 | - | - | - | no | - | 15 |
| T-52 | Real-ledger board, headless | S | R3E-11, R4-04, T-16 | - | - | - | no | - | 14 |
| T-53 | First change of hands: lanes RCA-1, RCB-1, RCA-2 | M | T-48, T-59, T-19, T-30, R4-04, T-50 | - | T-20 (`kind=rivals` steps), R4-07 (holder-change line) | - | flight | - | 16 |
| T-54 | Paired lanes in the parallel runner | M | T-03, T-06 | - | - | - | lanes (one pair) | - | 2 |
| T-55 | Race integration variants (lanes RCI-1 to RCI-9) | M | T-48, T-50, T-21, T-19 | - | T-20 | - | flight | - | 16 |
| T-56 | Retake, no retakes, Re-Fly against a rival (stage S4) | M | T-53, R6-02, R4-07, T-20 | - | - | - | flight | - | 17 |
| T-57 | Both careers to the Mun (stage S5) | S | T-49, T-50, T-51, T-56, R2-12 | - | R6-03 (the end-date step) | - | flight (about 2.24 h on two slots) | - | 18 |
| T-58 | True new race from the seam and an accept-by-type selector (optional) | S | T-50, R1-10, R1-11 | - | - | optional | flight | - | 16 |
| T-59 | Race harvest flag on `harvest_bdock_station.py` | S | T-03, T-05 | - | T-21 (chain facets) | - | no | - | 1 |
Total: 257 task headings in 11 plans, each listed once: 16 dropped, 241 live, 236 pull requests.

## B. Waves

Wave 0 needs only the owner's go. 20 waves, 0 to 19. Within a wave, tasks of different plans can
run in parallel unless section C names a shared file. Two windows sit across the waves:

- The P2b window (P2b-02 merge to the P2b-10 flight proof, waves 4 to 10): no P6 pull request
  merges inside it (playback :50-52, map :56-57), and no other task edits its hot files
  (`ParsekFlight.cs`, the positioner, the playback host; playback :747-750).
- P6 (P6-01 to P6-05) is built in parallel and merged when green, outside that window (map 10.1,
  assumed A). Its waves 0 to 4 are a floor; P6-05 is needed only by R3W-04 (wave 5).
- P3b (waves 11 to 13 here) is deferred out of the preparation phase (map 10.2, answered
  2026-11-14: later, not in the first build).
  R3W-26 (wave 18), the camera half of R4-11 and R5-06 (wave 19) wait for it. The waves shown are
  the earliest if it is scheduled right after P2b-10.
- S8 runs last (owner, 2026-11-14, on Q-03: "leave this at the end, its an external
  dependency"). S8a sits one wave after T-28 (wave 16), the lane RACE-6 that proves a real
  export, sync and board cycle between two players' saves on a plain shared folder; S8b
  follows in wave 18, and R3E-23 and R6-10 in wave 19. Nothing depends on R3E-23 or R6-10,
  so S8 adds no wave: the plan has 20 waves, 0 to 19; until S8 the race runs the main-thread
  form with the design's default poll numbers.

### B.1 Waves by plan

**Wave 0** (27 tasks)

- r0-spikes: S0
- r0b-prep-small: P12-01, P11-01, P11-02, P7-01, P1-01, P10-01
- r0b-prep-playback: P2c-01, P2c-02, P2a-05, P2b-01
- r0b-prep-map-watch-codec: P3-01, P6-01
- r1-mode-and-setup: R1-02, R1-07
- r3-export-and-exchange: R3E-07, R3E-12
- r4-r6-board-and-rules: R4-08
- r5-looks: R5-01
- test-and-harness: T-01, T-02, T-03, T-04, T-05, T-07, T-45, T-49

**Wave 1** (21 tasks)

- r0-spikes: S3, S4a, S6, S9, S10a, S11, S12
- r0b-prep-small: P7-02, P1-02
- r0b-prep-playback: P2c-03, P2c-04, P2a-03, P2a-06, P2a-07
- r0b-prep-map-watch-codec: P6-02
- r1-mode-and-setup: R1-16
- r3-rival-world: R3W-02
- r5-looks: R5-02
- test-and-harness: T-06, T-38, T-59

**Wave 2** (13 tasks)

- r0-spikes: S1b, S4b, S10b
- r0b-prep-small: P1-03
- r0b-prep-playback: P2a-02, P2a-04
- r0b-prep-map-watch-codec: P3-02, P6-03
- r1-mode-and-setup: R1-03
- r3-rival-world: R3W-01, R3W-06
- r5-looks: R5-12
- test-and-harness: T-54

**Wave 3** (11 tasks)

- r0-spikes: S5, S7
- r0b-prep-map-watch-codec: P3-03, P6-04
- r1-mode-and-setup: R1-04, R1-05, R1-06
- r3-rival-world: R3W-07
- r5-looks: R5-03, R5-07, R5-08

**Wave 4** (10 tasks)

- r0-spikes: S2
- r0b-prep-playback: P2b-02
- r0b-prep-map-watch-codec: P3-04, P3-05, P6-05
- r1-mode-and-setup: R1-08
- r2-sites: R2-01
- r5-looks: R5-05
- test-and-harness: T-10, T-44

**Wave 5** (9 tasks)

- r0-spikes: S13
- r0b-prep-playback: P2b-03
- r1-mode-and-setup: R1-09
- r2-sites: R2-03
- r3-export-and-exchange: R3E-06
- r3-rival-world: R3W-04, R3W-17
- r5-looks: R5-04
- test-and-harness: T-46

**Wave 6** (8 tasks)

- r0b-prep-playback: P2b-04
- r1-mode-and-setup: R1-10
- r2-sites: R2-04
- r3-export-and-exchange: R3E-08, R3E-13
- r3-rival-world: R3W-21
- r5-looks: R5-10, R5-11

**Wave 7** (14 tasks)

- r0b-prep-playback: P2b-05
- r1-mode-and-setup: R1-11, R1-12, R1-19, R1-23
- r2-sites: R2-02, R2-05, R2-06, R2-08, R2-11
- r3-export-and-exchange: R3E-01
- r4-r6-board-and-rules: R4-01, R4-02
- r5-looks: R5-14

**Wave 8** (10 tasks)

- r0b-prep-playback: P2b-06
- r1-mode-and-setup: R1-20, R1-21
- r2-sites: R2-07
- r3-export-and-exchange: R3E-02
- r3-rival-world: R3W-03
- test-and-harness: T-12, T-13, T-29, T-48

**Wave 9** (10 tasks)

- r0b-prep-playback: P2b-07, P2b-08, P2b-09
- r1-mode-and-setup: R1-14, R1-22
- r2-sites: R2-09
- r3-export-and-exchange: R3E-03
- test-and-harness: T-35, T-36, T-42

**Wave 10** (9 tasks)

- r0b-prep-playback: P2b-10
- r1-mode-and-setup: R1-15, R1-17, R1-18
- r2-sites: R2-10, R2-12, R2-15
- r3-export-and-exchange: R3E-04
- test-and-harness: T-41

**Wave 11** (12 tasks)

- r0b-prep-map-watch-codec: P3b-02
- r2-sites: R2-13, R2-14
- r3-export-and-exchange: R3E-10, R3E-11, R3E-14, R3E-15, R3E-16, R3E-22
- test-and-harness: T-16, T-21, T-43

**Wave 12** (10 tasks)

- r0b-prep-map-watch-codec: P3b-01, P3b-03
- r3-export-and-exchange: R3E-09, R3E-17, R3E-18
- r3-rival-world: R3W-05
- test-and-harness: T-17, T-18, T-33, T-47

**Wave 13** (9 tasks)

- r0b-prep-map-watch-codec: P3b-04
- r3-export-and-exchange: R3E-19, R3E-21
- r3-rival-world: R3W-08, R3W-27
- r4-r6-board-and-rules: R4-03, R4-04
- test-and-harness: T-19, T-39

**Wave 14** (10 tasks)

- r3-export-and-exchange: R3E-20, R3E-25
- r3-rival-world: R3W-09
- r4-r6-board-and-rules: R4-05, R4-07, R4-09
- test-and-harness: T-27, T-30, T-31, T-52

**Wave 15** (16 tasks)

- r3-export-and-exchange: R3E-24
- r3-rival-world: R3W-10, R3W-11, R3W-13, R3W-14, R3W-15, R3W-22, R3W-28
- r4-r6-board-and-rules: R4-06, R6-01
- test-and-harness: T-20, T-25, T-32, T-34, T-50, T-51

**Wave 16** (12 tasks)

- r3-rival-world: R3W-12, R3W-16, R3W-18, R3W-32
- r4-r6-board-and-rules: R4-10, R6-02
- test-and-harness: T-24, T-26, T-28, T-53, T-55, T-58

**Wave 17** (9 tasks)

- r0-spikes: S8a (runs last, after T-28)
- r3-rival-world: R3W-19, R3W-23
- r4-r6-board-and-rules: R4-11, R4-12, R6-03, R6-05, R6-06
- test-and-harness: T-56

**Wave 18** (13 tasks)

- r0-spikes: S8b
- r3-rival-world: R3W-20, R3W-24, R3W-26, R3W-29
- r4-r6-board-and-rules: R4-13, R6-04, R6-07, R6-08, R6-11, R6-12
- test-and-harness: T-37, T-57

**Wave 19** (7 tasks)

- r3-export-and-exchange: R3E-23 (after S8)
- r3-rival-world: R3W-25, R3W-30
- r4-r6-board-and-rules: R6-10 (after S8)
- r5-looks: R5-06, R5-09
- test-and-harness: T-40
### B.2 The milestone ladder and its critical paths

Section F lists each milestone's tasks and exit proof; README "Milestones" says what each one
shows. Here: when each milestone's last task can land, the longest chain of hard dependencies
from day one that sets it, and how many of its tasks can start two or more milestones early
("early starters": every dependency sits in a milestone at least two rungs below). A chain
counts tasks, both ends included, so it is one more than the wave of its last task when nothing
else holds it. MS-2 closes earlier in waves than MS-1c and MS-1d, but milestones are signed
off in ladder order (README rule 11), so MS-2 is signed off after MS-1d.

| Milestone | Tasks | Waves | Last task | Chain from day one | Early starters |
|---|---|---|---|---|---|
| MS-1a Test bench and prepared ground | 56 | 0 to 5 | wave 5 | 6 tasks | - |
| MS-1b A race save, created and joined | 17 | 0 to 8 | wave 8 | 9 tasks | 1 |
| MS-1c Headless skeleton: two machines, one board | 18 | 0 to 14 | wave 14 | 15 tasks | 2 |
| MS-1d Live skeleton: two games, one folder, one flight, one board | 35 | 0 to 16 | wave 16 | 17 tasks | 6 |
| MS-2 The four sites | 19 | 4 to 11 | wave 11 | 12 tasks | 3 |
| MS-3 Edit evidence and self-heal | 6 | 2 to 15 | wave 15 | 16 tasks | 2 |
| MS-4 The rival world beyond flight | 29 | 0 to 19 | wave 19 | 20 tasks | 15 |
| MS-5 Screens and messages | 17 | 0 to 18 | wave 18 | 19 tasks | 4 |
| MS-6 Scoring rules | 13 | 0 to 18 | wave 18 | 19 tasks | 4 |
| MS-7 Looks | 7 | 1 to 19 | wave 19 | 20 tasks | 2 |
| MS-8 End date and no retakes | 10 | 13 to 19 | wave 19 | 20 tasks | 1 |
| MS-9 Sync tuning (S8, last) | 4 | 17 to 19 | wave 19 | 20 tasks | 1 |
| MS-10 Two scripted careers to the Mun (capstone) | 2 | 16 to 18 | wave 18 | 19 tasks | 2 |

The chains (one of the longest where several tie):

- MS-1a: S0 -> S4a -> S4b -> S5 -> S2 -> S13.
- MS-1b: S0 -> S6 -> R1-03 -> R1-04 -> R1-08 -> R1-09 -> R1-10 -> R1-12 -> T-48.
- MS-1c: S0 -> S6 -> R1-03 -> R1-04 -> R1-08 -> R1-09 -> R1-10 -> R3E-01 -> R3E-02 -> R3E-03 ->
  R3E-04 -> T-16 -> R3E-18 -> R4-04 -> T-52.
- MS-1d: S0 -> S6 -> R1-03 -> R1-04 -> R1-08 -> R1-09 -> R1-10 -> R3E-01 -> R3E-02 -> R3E-03 ->
  R3E-04 -> T-16 -> R3E-18 -> R4-04 -> T-30 -> T-50 -> T-55.
- MS-2: S0 -> S6 -> R1-03 -> R1-04 -> R2-01 -> R2-03 -> R2-04 -> R2-06 -> R2-07 -> R2-09 ->
  R2-12 -> R2-13.
- MS-3: S0 -> S6 -> R1-03 -> R1-04 -> R1-08 -> R1-09 -> R1-10 -> R3E-01 -> R3E-02 -> R3E-03 ->
  R3E-04 -> T-16 -> R3E-18 -> R4-04 -> T-30 -> T-34.
- MS-4: S0 -> S6 -> R1-03 -> R1-04 -> R1-08 -> R1-09 -> R1-10 -> R3E-01 -> R3E-02 -> R3E-03 ->
  R3E-04 -> T-16 -> R3W-05 -> R3W-08 -> R3W-09 -> R3W-11 -> R3W-12 -> R3W-23 -> R3W-24 ->
  R3W-30.
- MS-5: S0 -> S6 -> R1-03 -> R1-04 -> R1-08 -> R1-09 -> R1-10 -> R3E-01 -> R3E-02 -> R3E-03 ->
  R3E-04 -> T-16 -> R3E-18 -> R4-04 -> R4-05 -> R4-06 -> R4-10 -> R4-12 -> R4-13.
- MS-6: S0 -> S6 -> R1-03 -> R1-04 -> R1-08 -> R1-09 -> R1-10 -> R3E-01 -> R3E-02 -> R3E-03 ->
  R3E-04 -> T-16 -> R3E-18 -> R4-04 -> R4-05 -> R4-06 -> R4-10 -> R6-06 -> R6-12.
- MS-7: S0 -> S6 -> R1-03 -> R1-04 -> R1-08 -> R1-09 -> R1-10 -> R3E-01 -> R3E-02 -> R3E-03 ->
  R3E-04 -> T-16 -> R3W-05 -> R3W-08 -> R3W-09 -> R3W-11 -> R3W-18 -> R3W-19 -> R3W-20 -> R5-09.
- MS-8: S0 -> S6 -> R1-03 -> R1-04 -> R1-08 -> R1-09 -> R1-10 -> R3E-01 -> R3E-02 -> R3E-03 ->
  R3E-04 -> T-16 -> R3E-18 -> R4-04 -> R4-05 -> R4-06 -> R4-10 -> R6-06 -> R6-12 -> T-40.
- MS-9: S0 -> S6 -> R1-03 -> R1-04 -> R1-08 -> R1-09 -> R1-10 -> R3E-01 -> R3E-02 -> R3E-03 ->
  R3E-04 -> T-16 -> R3W-05 -> R3W-08 -> R3W-09 -> T-20 -> T-28 -> S8a -> S8b -> R3E-23.
- MS-10: S0 -> S6 -> R1-03 -> R1-04 -> R1-08 -> R1-09 -> R1-10 -> R3E-01 -> R3E-02 -> R3E-03 ->
  R3E-04 -> T-16 -> R3E-18 -> R4-04 -> T-30 -> T-50 -> T-53 -> T-56 -> T-57.

Notes:

- The playback side (P2c-02 -> P2c-04 -> P2a-02 -> R5-03 -> P2b-02 ... P2b-10) ends at wave 10
  and has slack inside MS-1d; the exchange stem through R3E-04 sets MS-1c to MS-10.
- S8 runs last (owner, 2026-11-14; B, the S8 note): MS-9 holds it and nothing else waits on it.
- The ladder replaces this section's earlier list of milestones; a rival ghost playing in flight
  (R3W-09), the plan's first milestone before the ladder, is part of MS-1d.

## C. Parallel tracks

### C.1 Files that tasks of two or more plans edit

Each row gives the tasks in the order the plans now give. Two tasks on one row never run as open
PRs at the same time unless the row says the edits are disjoint.

| File or set | Tasks, in order | Source of the order |
|---|---|---|
| `scripts/arch/modules.toml`, `atlas.toml` | P12-01 (the `[forbidden]` comment), then R1-03, R3W-02, R3E-02 (one module registration each) | since pull request #2611 a new file in an existing module edits neither; the three registrations add disjoint rules, and the 15.4 edges each adds name only modules that exist (r1 :276-290, :855-856) |
| `scripts/arch/test_archview.py` | P12-01 (CheckerHelperTests), then P7-01, P1-01, P1-02, R5-03, R3W-14 (the Core count and kernel list, one file each), R1-06 (the loader cell) | small :741-743; r5 :613-614 |
| `GhostPlaybackEngine.cs` constructor | P2c-02, P2c-04, P2a-02, R5-03, R3W-09, R3W-14 | playback :740-743; r5 :602-604; r3w :1065-1067 |
| `GhostPlaybackEngine.Diagnostics.cs`, `GhostPlaybackEvents.cs` | P2c-02, P2c-03, R3W-01 | playback :744-745; R3W-01 is written against P2c-03's source gate |
| `GhostMapPresence.StateVectors.cs` (the `GhostMapPresence` partial) | P2a-07, P3-02 | playback :751-752; map :643-645 |
| `GhostMapVessels.cs` | P3-02 to P3-05, R3W-17, R5-04 (`ApplyOrbitStyle` in R3W-18's PR), R3W-19 | map :635-637; r3w :1069-1070; r5 :357-358 |
| `GhostMapPresence.cs` (name composer beside the prefix) | P3-04, R5-04 | map :649-654 |
| `RecordingPlaybackCatalog.cs` and its tests | P2a-05 (creates), P2a-03, P2a-02, R3W-09 (adds `None`) | playback :753-761; r3w :1068 |
| `ParsekFlight.cs` and partials | P2b-02 to P2b-10, then R3W-09, R3W-16; P3b when scheduled | playback :747-748; r3w :1061-1062 |
| `WatchModeController.cs` | P2b-08, then P3b-01 to P3b-04, R3W-26, R5-06 (all deferred) | playback :768-771; map :638 |
| `ParsekFlightRivalLane.cs` | R3W-09, R3W-10, R3W-13, R3W-16, R3W-18, R3W-24, R3W-26, R3W-32, then R5-09 | r3w :1063-1064; r5 :606 |
| `HomeSpaceCenter.cs`, `LaunchSiteExclusionZones.cs` | the launch pad fixes (outside this plan, on main first), then P1-01, P1-02, P1-03, then R2-05 | small :745-746; r2 :742-743 |
| `LaunchArea.cs` | P1-02, then R2-01 (adds `LaunchArea.Strip`) | small :523-527; r2 :350-351 |
| `ParsekRaceBoot.cs` | R1-04 (creates), R2-01, R2-07, R1-22 (its two event handlers) | r1 :844-845; r1 R1-22 |
| `RaceInstallCheck` (refusal list) | R1-10, then R2-02, R1-19 (`content-table-unknown`), R4-02 | r2 :386; r4 :817; r1 R1-19 |
| `RaceCore/RaceCalendar.cs` | R1-10, R4-01, then R4-04, R4-07, R4-10 | r4 :816 |
| `RacePorts.cs`, `RaceFolder.cs`, `RaceIds.cs`, `RaceTimestamp.cs`, `RaceManifest.cs` | R1-08, R1-09, R1-10, R1-11, R1-19 (the content keys in the manifest and link codecs), then R3E-01 | r1 :846-848; r3e :957-958 |
| `RaceFolderCheck.cs`, `RaceSharedFolder.cs` | R1-10, R3E-01, R3E-16 | r3e :958-959 |
| `RaceWorld/RaceRuntime.cs` | R1-08, R3W-08, R4-09, R4-10, R4-11 or R3W-26 (first to land), R6-06, R6-12 | r4 :812-814 |
| `RaceSync.cs`, `RaceSyncDriver.cs` (and `RaceSync.Import.cs`) | R3E-01, R3E-02, R3E-04, R3E-16, R3E-17, R3E-18, R3E-20, R3E-22, R3E-25 (the import check), then R4-04, R4-07, R4-09, R6-10 | r3e :955-956; r4 :817-819 |
| the standing codec (`StandingCodec.cs`, `Standing.cs`, `PublicState.cs`) | R3E-01, R3E-04, the block tasks R3E-10, R3E-11, R3E-14, R3E-15, R3E-22 one at a time, R3E-09, R3E-25, then R6-01 (the tool view) | r3e :951-952, :957; r4 :818 |
| `ParsekRaceScenario.cs` | R1-03, R1-08, R3E-15, R5-11 (the load pass call) | r5 :651 |
| `LoadReconcilePolicy.cs` | R1-05, R1-16 (either order), R3E-15 | r1 :834-835; r3e :961 |
| `SaveFormat.cs`, `Fixtures/FormatGolden/` | R1-12 (both rows, golden expected today `v2`), then R3E-04 (expected today `v3`); each takes the next free folder when it lands (README rules 5, 10) | r1 :852-854; r3e :956 |
| `TestCommandVerbs.cs`, hlib verb tables, `design-autotest-command-seam.md` | P11-01 (seam doc), T-07, R1-08, R1-10, R1-11, R1-19 (`parts=`, `unscored=`), R1-20 (`unscoredConfirm` on `RaceSetup` and `LoadGame`), T-12, T-19, T-20, T-30 | small :736-737; r1 :836-838; T :713-716 |
| `harness/lib/test_hlib.py`, `hlib.py` | P10-01, P11-01, T-03, T-04, T-07, then every lane and verb task | small :733-735; T :707-709 |
| `.claude/CLAUDE.md`, `AGENTS.md` | P10-01, R1-07, P2a-02, P2b-02, P3-04, R1-14, R2-03, R2-04, R2-07, R2-08, R3E-23, R5-14, R3W-25 (only if S7 fails), the R3W key-file line | small :738-740; playback :885-887; map :720; r1 :963; r2 :829-830; r3e :926; r3w :1145; one mirror copy per PR |
| generated `docs/dev/flight-profiles.md`, `harness/coverage/registry.toml`, `docs/dev/autotest-status.md` | every task that adds an in-game category or a lane (R5-01 and T-07 in wave 0); P3-01 does not change the catalog (map :269-273) | T :717-721; regenerate after merging `origin/multiplayer`, never by hand |
| the RACE-9, RACE-10, RACE-11 spec tallies (T-29, T-35, T-36) | every task that adds a test in a hosted category re-pins its host's `total=` | r2 :738-740; T :632 |
| `GrepAuditTests.Race.cs`, `scripts/race-loader-files-allowlist.txt`, `RaceLogTags` | R1-06 (creates the test file and `RaceLogTags`, seeded with `Race` and R3W-06's `RivalLane`); the allowlist file is created by R3W-02 or R1-06, whichever lands first (a data file with no reader breaks no rule); then R3W-03, R3W-04, R3W-27 | r1 R1-06; r3w R3W-02, R3W-06 |
| `scripts/ers-els-audit-allowlist.txt` | P2a-05, P3b-02 | append-only |
| `scripts/arch/lifetimes.toml`, `lifetime_pins.toml`, the reset allowlist | P1-03 first, then P3-02, P3-03, R1-03, R1-04, R1-08, R1-10, R2-03, R2-05, R3E-06, R3E-08, R3W-08, R3W-14, R3W-19, R5-12 | small :743-744; re-pin after each merge |
| `docs/dev/design-gui-inventory.md` | R1-14, R1-15, R1-17, R1-18, R2-02, R2-08, R1-20, R1-21, R1-22, R5-14, R4-08, R4-09, then the R3E, R3W, R4, R5 tasks that add a surface | counts are re-derived, never hand-summed |
| `UI/SettingsWindowUI.cs` | R1-18, then R4-09 (Sync now, `RequestSyncNow`) | r4 :810-811 |
| `Patches/RaceLoadGatePatch.cs` | R2-02 (creates), then R1-20 (the content check after the Kerbin check) | r2 R2-02; r1 R1-20 |
| R2-08's difficulty hold and greying (`RaceLaunchRules`, its window postfix) | R2-08, then R1-21 (the nine progression settings) | r2 R2-08; r1 R1-21 |
| `Config/FactCapture.cs`, `FlightRecorder.ToolUse.cs`, `GameStateRecorder.Handlers.cs` | R3E-06, R3E-08, R3E-09, R3E-15, then R3E-25 | r3e section 5 |
| `RaceCore/ClaimChecks.cs`, `UI/SpaceRaceText.cs`, `RaceBoardPresentation.cs` | R4-05, R4-10, R6-01, R6-05, R6-06, then R6-12 | r4 section 5 |
| T-16's `RaceFolderBuilder` | T-16, then R3W-05, R6-12 (the uncounted-parts and save-mark writers) | r4 R6-12 |
| `ParsekUI.cs`, `UI/ParsekWindowSet.cs` | P11-02, R4-08, R4-09 | small :742 |
| `ParsekKSC.cs` | R3E-17 (poll at scene entry), R4-09 (draw call), R5-09 only if rivals show at the KSC | r3e :785-786; r4 :807; r5 :733 |
| `InGameTestRunner.cs` | R1-07, R3E-08 (tool mark at the runner) | r1 :824; r3e R3E-08 |
| `Source/Parsek.Tests/Generators/` | T-01, T-16 (the one folder builder), R3W-05 (`RivalCacheBuilder` through T-16) | T :709-711; T :422-425; r3w :1044 |

### C.2 Tracks for waves 0 and 1

One agent per track, tasks in order. These are MS-1a's tracks plus the early starters of later
milestones (B.2). The supervisor runs at most four at once, so start ARCH, SETUP, ENGINE and
HARNESS first: ARCH heads every critical path (P12-01 -> R1-03), and the owner runs SPIKES on
the PC in parallel (S6 on day 1 ties ARCH for R1-03). RUNNER and TESTSIM follow as slots free.

| Track | Tasks in order | PC | Notes |
|---|---|---|---|
| ARCH | P12-01, P7-01, P1-01, P7-02, P1-02 | P1-02: four lanes | P12-02 and R1-01 are dropped (2026-11-15); P7-01 before P1-01 / P1-02 (each adds a `Core/` file and moves the Core count in `test_archview.py`); P1-02 starts after the launch pad fixes are on `multiplayer` (on main before the cut; Q-04 answered 2026-11-14) |
| SETUP | P10-01, R1-02, R1-16, R1-07 | P10-01: three lanes; R1-16: one tier | R1-02 no longer orders after P10-01 (both only add a `LogIO/` file); R1-07 after P10-01 (CLAUDE.md) |
| ENGINE | P2c-02, P2c-03, P2c-04, P2c-01, R3W-02 | P2c-02 to P2c-04: lanes; R3W-02: one in-game run | R3W-01 follows in wave 2, after P2c-03; R3W-02 after P2c-01 and ARCH's P12-01 (it registers the `RaceWorld` module) |
| CATALOG | P2a-05, P2a-03, P2a-06, P2a-07, P2b-01 | P2a: lanes | P2a-05 creates the catalog files in `Recording/` (no `modules.toml` edit); P2a-07 before P3-02 |
| MAP / CODEC | P3-01, P6-01, P6-02 | P3-01: one in-game run | P6 merges when green, never inside the P2b window |
| GUI | P11-01, P11-02, R4-08 | P11-02: census lanes; R4-08: optional | P11-01 shares `test_hlib.py` with SETUP and HARNESS and lands the seam doc before T-07 |
| FACTS | R3E-07, R3E-12 | no | independent files |
| LOOKS | R5-01, R5-02 | one in-game run each (their cells run on H15) | `GhostVisualBuilder.cs` in order; each re-pins H15's `total=` (`GhostVisuals`) |
| HARNESS | T-03, T-04, T-06, T-05, T-38, T-45, T-07 | T-07: RACE-0 flight | T-04 and T-06 after T-03 (T :707-709); T-38 after T-05 (the row gates its leak scan); T-45 (the `race` tag, `--tag`, CI push) early, so RACE-0 can carry the tag (an order, not a dependency); T-07 shares the verb tables and the lane catalog |
| RUNNER | T-49, T-59, T-54 | T-49: one CS chain replay; T-54: one pair | harness Python only; T-59 after T-03 and T-05; T-54 after T-06, in wave 2 |
| TESTSIM | T-01, T-02 | no | `Generators/` files only |
| SPIKES (owner) | S0, then S4a, S6, S9, S10a, S1b scan; S3, S10b with S12, S11 on day 4; S8a, S8b last (waves 17, 18) | spike | D.1 |

Tasks of waves 0 and 1 that need no PC: P12-01, P11-01, P7-01, P7-02, P1-01, P2c-01, P2b-01,
P6-01, P6-02, R1-02, R1-07, R3E-07, R3E-12, T-01 to T-06, T-38, T-45, T-59 (script output).

Not side by side in waves 0 and 1 (same file):

- `test_archview.py`: P12-01, P7-01, P1-01, P1-02. P7-01, P1-01 and P1-02 move the same Core
  count and kernel list. `modules.toml`: P12-01 (a comment) and R3W-02 (the `RaceWorld`
  registration), disjoint lines.
- `.claude/CLAUDE.md` and `AGENTS.md`: P10-01, R1-07.
- `harness/lib/test_hlib.py` and `hlib.py`: P10-01, P11-01, T-03, T-04, T-06, T-07, T-38, T-45.
- `harness/tools/parallel_runner.py`: T-45 (`--tag`), then T-49 (the dispatcher it reuses).
- `GhostPlaybackEngine.cs`: P2c-02, P2c-03, P2c-04.
- the catalog files: P2a-05 first; P2a-03, P2a-06, P2a-07 add rows after it (additive).
- `RecordingPaths.cs`: P7-01, P7-02. `SpawnCollisionDetector.cs`: P1-01, P1-02.
- generated `flight-profiles.md`: T-07, T-45. `ParsekUI.cs`: P11-02, R4-08.
- `H15-corpus-ghost-visuals.toml` (its pinned `total=`): R5-01, R5-02.
- `GhostVisualBuilder.cs`: R5-01, R5-02.

## D. Use of the owner's PC

`provision.py`, `run.py` and the parallel runner share one machine lock, and spike sessions
launched by hand do not take it, so the owner runs no tier while a spike session runs (r0
:864-867).

PC lanes run through the parallel runner (`harness/tools/parallel_runner.py`, on main since pull
request #2592; `harness/README.md`, "Parallel runs") wherever it applies: two KSP copies while
the owner works at the PC (`--slots 2`), three when away. The one measurement so far is the
daily tier, 12 min 35 s on three copies against about 25 minutes one lane at a time, so a
selection of ordinary lanes takes about half as long on three copies (an estimate from that one
ratio). It does not fly by default a lane that moves the cursor or takes the focus (LT-1,
GUI-7; `--include-exclusive` flies them last, alone) or a lane on an unstaged operator-local
fixture, and a lane needs a provisioned copy of its install; the full tier on the parallel
runner is not yet measured. What still runs alone: the spikes' frame-cost sessions (S5's frame
cost, S8b, S11), which need an otherwise idle PC, and the in-game test batches run by hand.

### D.1 Spikes (r0 day plan, r0 :874-879; hours from each spike's Duration line)

| Day | Spikes | Hours on the PC |
|---|---|---|
| 1 | S0 1 h, S4a 1.5 h, S6 3.5 h, S9 2 h, S10a 0.5 h, S1b scan | about 9 |
| 2 | S4b 3 h, S1b scoring (S1b 1 h in all), S2 2 h (steps 1 to 3; E, item 7) | about 5.5 |
| 3 | S5 3 h, S7 2 h, S2 step 4 (needs S5) | about 5 |
| 4 | S3 3 h, S10b 1 h with S12 10 min, S11 1 h | about 5 |
| any | S13 needs no PC | 0 |
| last (waves 17, 18) | S8a 2 h (no KSP), S8b 1 h, after T-28 (wave 16) | about 3 |

About 25 hours over the four days (the Duration lines of the day-plan spikes sum to 24.7 h),
then about 3 hours for S8 at the end. Never in parallel: S5's frame cost, S8b and S11 (r0
:862). S4a and S6 go first because R1-04, R2-01, R2-05, P1-03, R1-20 (S4a) and R1-03,
R1-10, R1-14, R1-19, R1-20 (S6) wait on them (r0 :881-882).

### D.2 One in-game run (a test batch, through its host lane where it has one)

From each task's `PC` line (63 tasks). A cell that needs no race save runs in an existing
category on its existing host the day it lands (R5-01, R5-02, R5-08 on H15; R5-04 on S1.6); a
race-only cell runs on RACE-9, RACE-10 or RACE-11 once its host is in (T-29, T-35, T-36; the
gaps are in README rule 11), and its single-player half on RACE-20 (T-44).

| Wave | Tasks |
|---|---|
| 0 | P3-01, R5-01 |
| 1 | R3W-02, R5-02 |
| 2 | R1-03, R3W-06, R5-12 |
| 3 | R1-04, R3W-07, R5-08 |
| 4 | R2-01 |
| 5 | R2-03, R3W-04, R3W-17, R5-04 |
| 6 | R2-04, R3E-08, R3E-13, R3W-21, R5-11 |
| 7 | R1-19, R2-02, R2-05, R2-06, R2-08, R2-11, R5-14 |
| 8 | R1-20, R1-21, R2-07, R3W-03 |
| 9 | R1-14, R1-22, R2-09 |
| 10 | R1-15, R1-17, R1-18, R2-10 |
| 12 | R3E-09, R3W-05 |
| 13 | R3W-08 |
| 14 | R3W-09, R4-09 (on R4-13's hosts) |
| 15 | R3E-24, R3W-10, R3W-11 |
| 16 | R3W-16, R3W-18, R3W-32, R4-10 (on R4-13's hosts) |
| 17 | R3W-19, R3W-23, R4-11 (camera half) |
| 18 | R3W-20, R3W-24, R3W-26, R4-13, R6-07 |
| 19 | R3E-23, R3W-25, R3W-30, R6-10, R5-09 |

### D.3 Harness lanes, a tier, or a new lane flown

From each task's `PC` line. "(flight)" is a new lane's first flights; the rest re-fly existing
lanes. R1-08, R1-10, R1-11 and R1-23 are proven through RACE-1 (T-13). T-12, T-19, T-20 and T-30
ride the lanes that use them (T-13, T-24, T-26, T-32); T-48 rides T-53. Every new race lane is
tier operator with the tag `race` and flies through `parallel_runner.py run --tag race` (T-45).
T-53 and T-56 fly their lanes in turn on one install; T-57 is pipelined on two slots (the same
lanes, B-k one stage behind A-k); only T-54's paired lanes and T-34 on them take two slots at
once, both copies on one folder.

| Wave | Tasks |
|---|---|
| 0 | P11-02, P10-01, P2c-02, P2a-05, R4-08 (optional census), T-07 (flight), T-49 (one CS chain replay) |
| 1 | P1-02, P2c-03, P2c-04, P2a-03, P2a-06, P2a-07, R1-16 (tier) |
| 2 | P1-03, P2a-02, P2a-04, T-54 (one pair) |
| 4 | P3-04, T-44 (flight) |
| 6 | R1-10 (RACE-1) |
| 7 | R1-11 (RACE-1) |
| 8 | T-13 (flight), T-29 (flight) |
| 9 | P2b-07 (daily tier, H15), P2b-08, T-35 (flight), T-36 (flight), T-42 (flight) |
| 10 | P2b-10 (18-lane set + daily tier), R2-12 (flight), T-41 (flight) |
| 11 | P3b-02, R2-13 (flight), R2-14 (flight), T-43 (flight) |
| 12 | P3b-03, T-33 (flight) |
| 13 | P3b-04, T-39 (flight) |
| 14 | T-27 (flight) |
| 15 | T-25 (flight), T-32 (flight), T-34 (flight) |
| 16 | T-24 (flight), T-26 (flight), T-28 (flight), T-53 (flight), T-55 (flight), T-58 (flight) |
| 17 | R4-12 (census), T-56 (flight) |
| 18 | T-57 (flight, about 2.24 h on two slots) |
| 19 | T-40 (flight) |

### D.4 Long runs

| When | Run | Hours | Source |
|---|---|---|---|
| after playback tracks A, C, D (P2c, P2a) | nightly tier | about 9.5 | playback :847-849 |
| after P2b-07, P2b-08, P2b-10 | daily tier | about 0.4 | playback :837-838 |
| after P2b-10 | the representative set (18 lanes) plus the daily tier, through the parallel runner | set about 0.43 measured p50 one lane at a time (two lanes not measured), daily about 0.4; about half on three copies (estimate) | playback 6.4, Q-05 |
| at release, outside this plan | full tier, 589 lanes on main ab1cc9478 (595 specs less 6 pending-fixture) | about 21.5 p50 one lane at a time, measured 2026-10-31 over the 523 lanes of then; not yet measured on the parallel runner | owner's answer to Q-05 |
| after P3-01, P3-04 | named lanes (H44, S1.6; then 11 lanes) | small | map :677-679 |
| after P6-03 | any daily tier run | about 0.4 | map :680-681 |
| after R1-16 | one tier run | one tier | r1 :775 |
| after T-49 | one CS chain replay, CS-3 to CS-4b | about 0.4 (1413 s of p50) | T-49 |
| MS-1d, after T-53 | lanes RCA-1, RCB-1, RCA-2 (T-50's specs) | about 0.81 (2915 s from the CS p50s) | T-53 |
| MS-1d, after T-55 | the integration lanes RCI-1 to RCI-9 | about 0.63 (2268 s, estimate: 9 x 252 s) | T-55 |
| MS-10, after T-57 | both Mun ladders on two slots | about 2.24 wall to RCA-5's end (8076 s), then RCB-5 | T-57, test-and-harness 4b |
| once the race lanes exist | the operator tier grows by the new race lanes' first flights: 7428 s (about 2.1 h) one lane at a time, of which T-53 and T-55 are the long ones; the daily and nightly tiers gain nothing (every race lane is operator) | about 2.1 | test-and-harness 6 |

The plans ask for no full run. The owner's answer to Q-05 (2026-11-14) replaced the one after
P2b-10 with a representative set of lanes (playback 6.4); the full run happens once, at release
time.

Per milestone (`pc.py` over this index and the plans' stated numbers; a lane with no estimate
counts 0 until its first flight is measured; the spike hours are D.1's):

| Milestone | Spike hours | New race lanes, first flights (s) | Long runs (h) | In-game runs | Lanes with no estimate yet |
|---|---|---|---|---|---|
| MS-1a | 24.7 | 45 | 10.29 | 1 | - |
| MS-1b | 0.0 | 240 | 0.00 | 3 | T-44 |
| MS-1c | 0.0 | 0 | 0.00 | 0 | - |
| MS-1d | 0.0 | 6193 | 1.63 | 8 | T-39 |
| MS-2 | 0.0 | 90 | 0.00 | 12 | R2-13, R2-12, R2-14, T-42 |
| MS-3 | 0.0 | 70 | 0.00 | 1 | T-54, T-34 |
| MS-4 | 0.0 | 700 | 0.00 | 15 | - |
| MS-5 | 0.0 | 90 | 0.00 | 8 | T-43 |
| MS-6 | 0.0 | 0 | 0.00 | 4 | T-41 |
| MS-7 | 0.0 | 0 | 0.00 | 6 | - |
| MS-8 | 0.0 | 0 | 0.00 | 1 | T-40, T-56 |
| MS-9 | 3.0 | 0 | 0.00 | 2 | - |
| MS-10 | 0.0 | 0 | 2.24 | 0 | T-58 |
| unanchored | 0.0 | 0 | 0.00 | 2 | - |
| total | 27.7 | 7428 | 14.17 | 63 | - |


## E. Findings

### E.1 Checks run on the graph

- Every task id with a heading in a plan appears in section A exactly once (257 ids).
- No hard dependency names an unknown id, a dropped task or the P2a-01 specification. No plan's
  `Depends on` line names a dropped task.
- The graph has no cycle: 240 nodes (the live tasks less the P2a-01 specification), which are
  the 236 pull requests and the four tasks that land inside another's.
- Every live task is in exactly one milestone of section F or in its unanchored list
  (`ladder.py`: 233 in milestones, 8 unanchored, 241 in all), and each milestone's exit proof
  is the anchor set the script reads.
- Every finding of the previous index (missing providers, the R4-03 / R3E-11 cycle, the wrong
  or loose dependencies, the duplicate providers, the wave-0 shared files, the oversized tasks)
  is settled in the plans as they read today and is removed here.

### E.2 Findings closed after this index was built

The first build of this index found eleven places where two plans still disagreed. The plan
files were edited to close them; none changed a wave. For the record:

| # | Was | Closed by |
|---|---|---|
| 1 | map said R3W-17 lands in R3W-18's pull request | map: R3W-17 is its own pull request; only R5-04's `ApplyOrbitStyle` half lands with R3W-18 |
| 2 | small named P2a-01 as an editor of `modules.toml` and `test_archview.py` | small: P2a-05 (since 2026-11-15 P2a-05 edits neither; its files sit in `Recording/`) |
| 3 | small ordered R1-01 after the other `modules.toml` edits | small: R1-01 lands any time after P12-01 and rebases (R1-01 dropped 2026-11-15; each race module registers with its first file) |
| 4 | P2b-02's `Depends on` line left out R5-03 | playback: R5-03 added as an order |
| 5 | T-12's `Depends on` line left out R1-11 | T: R1-11 added (verb file order) |
| 6 | r1's waiter table had the wrong needs for R4-02 and R4-10 | r1: R4-02 needs R1-08, R1-10 (R1-01 dropped 2026-11-15); R4-10 needs R1-10 |
| 7 | r0's day plan ran all of S2 on day 2 | r0: S2 steps 1 to 3 on day 2, step 4 on day 3 after S5 |
| 8 | T asked for a full run after five prerefactors | T: a full run after P2b-10 only; the others name smaller runs (since replaced by the representative set, Q-05) |
| 9 | r5 listed R3W-15 as adding a `modules.toml` rule | r5: dropped (since 2026-11-15 no R5 task edits `modules.toml`) |
| 10 | R5-07's host could make a pull request of three tasks | r5: R5-08 hosts R5-07 |
| 11 | T-34 waited on "parallel flights" with no name | T: the parallel runner of pull request #2592, outside this plan; now on main, so T-34 no longer waits on it |

A fact check against main ab1cc9478 (2026-11-15) dropped P12-02 and R1-01 (the folder is the
module since pull request #2611; each race module registers with its first file) and the
P10-01 -> R1-02 order (both only add a `LogIO/` file). Two tasks moved: R3W-02 from wave 2 to
wave 1 (its needs are now P2c-01 and P12-01) and R1-02 from wave 1 to wave 0. No other wave
moved; the wave count stayed 21 then. The split of T-29 (2026-11-15) later moved T-29 from wave 20
to wave 14, which emptied wave 20: the plan then had 20 waves, 0 to 19.

The milestone rework (2026-11-15) added R1-23 and T-37 to T-59, moved T-29 earlier (its own race
at slot 1, wave 8), and took the five dependency edits of the milestone analysis (c1 T-31 split
into an early half and T-37; c2 RACE-2's Tracking Station step moved to R3W-20; c3 T-29 creates
and joins its own race, T-35 and T-36 follow it to wave 9; c4 the witness facet moved to R3E-19;
c5 R5-07 lands with its first caller). The plan still has 20 waves, 0 to
19; the wave sizes are B.1's. Dependencies the writer added beyond the analysis, none moving a
wave: R1-11 for T-39, T-39 for T-28, T-38 for T-21, R1-23 for T-13, T-30, R4-09 and R6-01, and
the soft T-46 for SITES-1 and SITES-2.

### E.3 Still open

Nothing in the graph. The questions for the owner are in `design-corrections.md` part 2 and
in `README.md` section 7. Line citations in this index were taken before the edits above, so a
cited line of an edited plan can sit a few lines lower than the number says.

## F. Milestones

A milestone is a point where the race does something a player could see, proved by an exit
proof that runs without the owner's eyes. The ladder (README "Milestones") is MS-1a to MS-1d,
MS-2 to MS-10. MS-1, the skeleton (two games, one folder, one flight, one board), is milestone
one, built in four steps MS-1a to MS-1d, and its tasks are taken first (owner, 2026-11-15).
MS-2 to MS-9 are the rings that widen it; MS-10 is the capstone, both scripted careers to the
Mun. The labels are milestone names, not lane ids: the existing lane
`MS-1-mission-leg-trim-clone` and r4's message rows M1 to M10 are unrelated.

Each task is in the earliest milestone whose exit proof needs it: the proof's tasks and
everything they depend on, hard dependencies only (a task that lands in another task's pull
request counts with its host; R5-07 lands with its first caller, R5-08 or R4-10). A milestone
therefore lists only the tasks no earlier milestone needed. The waves are section A's; a
milestone's exit proof flies in the wave after its last task, and milestones are signed off
in ladder order (README rule 11). Every live task is in exactly one table below or in the
unanchored list (`ladder.py` check, E.1).

The two scripted players (test-and-harness section 4b) are a track through the ladder: their
stage S0 joins MS-1a (the runner T-49) and MS-1b (the overlay T-48), S1 and S2 join MS-1d
(T-53, with T-20 for S2), S3 joins MS-2 (the R2-12 / T-50 schedule row), S4 joins MS-8 (T-56)
and S5 is MS-10 (T-57).

### F.1 MS-1a Test bench and prepared ground (56 tasks, waves 0 to 5)

DO / SEE: the race test bench runs headless (the save-output driver, the fake synced store and
clocks, the staged shared folder, a snapshot of every save a lane makes, the race evaluator's
leak scan, `ListHandles kind=sites`, the `race` lane tag), the campaign chain runner replays a
stretch of the single-player career, the prerefactors are in with single-player Parsek
unchanged, and the spike answers are written into the design.

Exit proof: `SaveWriterDriverDeterminismTests` (T-01); `FakeSyncedStoreTests` (T-02); the
`test_hlib` and `test_run_smoke` cells of T-03, T-04 and T-06; `test_raceparse.py`'s leak scan
(T-05) and the gated `raceParse` row (T-38); `parallel_runner.py run --tag race` and the CI push
trigger (T-45); lane RACE-0-stock-sites-baseline green on stock-minimal (T-07); the campaign
chain runner's `test_campaign.py` and its replay of CS-3 to CS-4b (T-49, stage S0); the
prerefactor proofs of D.4 (the nightly tier after P2c and P2a, the daily tier after P6-03, the
named lanes after P3-01 and P3-04); S13's reports with `test_doc_spec_sync.py` green.

| Id | Title | Wave |
|---|---|---|
| P2a-01 | The port and the own-timeline catalog, no c... | - |
| P1-01 | `HomeSpaceCenter` in `Core`, read by the KS... | 0 |
| P10-01 | The create-and-start core in production | 0 |
| P11-01 | Census specs stop pinning the window count | 0 |
| P11-02 | One `ParsekUI` constructor body | 0 |
| P12-01 | `[forbidden]` takes a list on the right side | 0 |
| P2a-05 | RecordedRelativeAnchorPoseResolver through... | 0 |
| P2b-01 | il_compare folds a renamed reference field | 0 |
| P2c-01 | Make LoopSyncParentIdx read-only on the int... | 0 |
| P2c-02 | Engine diagnostics sink: health counters | 0 |
| P3-01 | Pin the primitive's runtime contract with a... | 0 |
| P6-01 | Commit an identity manifest over every comm... | 0 |
| P7-01 | The id check on the floor with a fixed char... | 0 |
| S0 | Spike branch and spike instances | 0 |
| T-01 | Headless save-output driver and its determi... | 0 |
| T-02 | Fake synced store and fake clocks | 0 |
| T-03 | Shared-folder staging in run.py | 0 |
| T-04 | Snapshot every save a lane creates | 0 |
| T-05 | Pure race evaluator, part 1: leak scan and... | 0 |
| T-07 | Seam read kind `ListHandles kind=sites` | 0 |
| T-45 | Race lane plumbing: `race` tag, `parallel_runner --tag`, CI push on `multiplayer` | 0 |
| T-49 | Campaign chain runner | 0 |
| P1-02 | One launch-area shape test, called by the 5... | 1 |
| P2a-03 | Positioner and flight context builder throu... | 1 |
| P2a-06 | ProductionAnchorWorldFrameResolver through... | 1 |
| P2a-07 | Map state vectors through the catalog (L6) | 1 |
| P2c-03 | Engine diagnostics sink: frame budget, hist... | 1 |
| P2c-04 | Spawn budget object an engine can share | 1 |
| P6-02 | List-level cores for the flat-fallback heal | 1 |
| P7-02 | A save-root accessor on `RecordingPaths` | 1 |
| S10a | Count the relative stretches and their body... | 1 |
| S11 | Ghost cost per frame at race scale (new) | 1 |
| S12 | Hashing and signing inside KSP's Mono (new,... | 1 |
| S3 | The four looks, replacement sets, a missing... | 1 |
| S4a | Probe: where the KSC is and what reads it | 1 |
| S6 | Race mark survival, main menu entry, create... | 1 |
| S9 | Which debug actions a mod can observe | 1 |
| T-06 | Harness-side folder step | 1 |
| T-38 | `raceParse` verifier row with the gated leak scan (early half of T-21) | 1 |
| P1-03 | The launch-site circle cache follows the ho... | 2 |
| P2a-02 | Engine tree lookup through the catalog (L1) | 2 |
| P2a-04 | Tail-lift recording lookup through the cata... | 2 |
| P3-02 | Move the guard set and the teardown depth i... | 2 |
| P6-03 | The payload type; `Read` becomes its first... | 2 |
| S10b | How far a flattened stretch drifts from its... | 2 |
| S1b | Finer terrain scan of the three spots | 2 |
| S4b | Move the working KSC | 2 |
| P3-03 | Owner record and a teardown that leaves oth... | 3 |
| P6-04 | `TryValidatePayload` decodes without a `Rec... | 3 |
| S5 | KSC copies as scenery, cost, terrain parity | 3 |
| S7 | Flag replica with the stock plaque dialog | 3 |
| P3-04 | Move the ProtoVessel mechanics into `GhostM... | 4 |
| P3-05 | The neutral "whose ghost is this" query at... | 4 |
| P6-05 | A production entry to load a trajectory fro... | 4 |
| S2 | KSC science at a moved site, and none from... | 4 |
| S13 | Close-out: results into the design | 5 |

### F.2 MS-1b A race save, created and joined (17 tasks, waves 0 to 8)

DO / SEE: through the seam, a race save is created into an empty shared folder and three more
players join it; slots 0 to 3, the four looks and every refusal show in `[Race]` lines; a
test game says so (`testGame = True`); a single-player save is byte-identical with the race
code present; the Space Center race categories run in a race save at slot 1.

Exit proof: lane RACE-1-setup-create-join (T-13); lane RACE-9-sc-batch, its own race at slot 1,
with at least `RaceMode` (T-29); `SinglePlayerInertnessTests` (T-10) and the single-player
negative contract with lane RACE-20-single-player-hosts (T-44); R1-23's manifest key cells;
`SaveFormatTests`, `SaveFormatGoldenGateTests`, `FormatGoldenV2Tests`, `RaceFormatPinTests`
(R1-12); `LoadReconcilePolicyTests` (R1-05); R1-06's race grep gates with their anti-vacuity
cells; the race overlay on a campaign save (T-48, stage S0), shown headless here by its hlib
validator cells and the `test_run_smoke` staging cells (spliced, snapshotted, the committed
fixture byte-equal after the run); its first flight is T-53's lanes (wave 16).

| Id | Title | Wave |
|---|---|---|
| R1-02 | One helper to find a scenario node (A7, par... | 0 |
| R3W-02 | `RivalFlight` value type | 1 |
| R1-03 | The race mark: `RaceLink` and `ParsekRaceSc... | 2 |
| R1-04 | `RaceGate` and its host | 3 |
| R1-05 | The race mark's row in `LoadReconcilePolicy` | 3 |
| R1-06 | The race grep-audit gates (design 15.4) | 3 |
| R1-08 | Read side: codecs, timestamps, local race d... | 4 |
| T-10 | Single-player inertness test (15.8) | 4 |
| T-44 | Single-player negative contract and lane RACE-20-single-player-hosts | 4 |
| R1-09 | Seats, slots, looks and names (`RaceSlots`) | 5 |
| R1-10 | Create a race (setup core, write side) | 6 |
| R1-11 | Join a race and start over in my seat | 7 |
| R1-12 | Register the race formats with a golden folder | 7 |
| R1-23 | The test game key in the race manifest | 7 |
| T-13 | Lane RACE-1-setup-create-join | 8 |
| T-29 | In-game race context helper and the Space Center host lane RACE-9 | 8 |
| T-48 | Race overlay at staging (`[fixture.raceOverlay]`) | 8 |

### F.3 MS-1c Headless skeleton: two machines, one board (18 tasks, waves 0 to 14)

DO / SEE: in xUnit alone, N simulated machines export standings, copy them out through the fake
synced store, import each other's and compute byte-equal boards for any arrival order, from
generated folders and from real ledgers.

Exit proof: `RaceFolderBuilderTests` (T-16); `RaceSyncSimulatorTests` (T-18); "same files, same
board", the early half (T-31); `RaceSyncImportTests` (R3E-18); `RaceBoardBaseTests` (R4-04) and
T-52's real-ledger cells; R3E-22's driver tests; T-59's harvest-flag cells.

| Id | Title | Wave |
|---|---|---|
| R1-07 | Logs and test batches leave `Parsek/Race/`... | 0 |
| T-59 | Race harvest flag on `harvest_bdock_station.py` | 1 |
| R3E-01 | Exchange vocabulary and the own-mirror scan | 7 |
| R4-01 | The race calendar and the date rule | 7 |
| R4-02 | The ladder table standard-1 | 7 |
| R3E-02 | Public state from the own model | 8 |
| R3E-03 | The public ghost snapshot | 9 |
| R3E-04 | Write a standing to the own mirror; golden v3 | 10 |
| R3E-11 | Milestone reach rows (A1) and the MILESTONE... | 11 |
| R3E-16 | The main-thread transport and the copy-out | 11 |
| R3E-22 | "N shared flights left the race" | 11 |
| T-16 | Generator `RaceFolderBuilder` | 11 |
| R3E-18 | Import: copy-in, completeness and the verdict | 12 |
| T-18 | Two-machine simulator bound to the ports | 12 |
| R4-03 | The ladder filter at board time | 13 |
| R4-04 | The board: shapes, exact points and the bas... | 13 |
| T-31 | "Same files, same board" (early half; T-37 is the late half) | 14 |
| T-52 | Real-ledger board, headless | 14 |

### F.4 MS-1d Live skeleton: two games, one folder, one flight, one board (35 tasks, waves 0 to 16)

DO / SEE: two real game saves share one folder: A flies a pad hop and exports; B joins, syncs,
sees A's ghost launch from site 0 and A's row on the board; the ghost and the row go after A's
quickload; a staged rival folder imports with every refusal named; rival ghosts in flight are
no vessel, collider or CommNet node; the two scripted players trade a first and each sees the
other's ghost. Before the ladder this was the plan's first milestone (R3W-09).

Exit proof: lanes RACE-6-two-saves-in-turn (T-28) and its seam-only first form (T-39),
RACE-3-rival-ghosts-flight (T-26), RACE-2-import-generator-folder without its Tracking Station
step (T-24), RACE-10-flight-batch (T-35); the `raceParse` structure facets (T-21); the
`ListHandles kind=rivals` and `kind=board` formatter tests (T-20, T-30); the committed
fixtures' currency test (T-17); the P2b proof (D.4: the daily tier after P2b-07 and P2b-08,
H15 with the two late-update categories, the representative set after P2b-10); lanes RCA-1,
RCB-1 and RCA-2 to its sync head (T-53, the first flight of T-50's generated specs, stages S0
to S2); the generated-spec byte gate (T-50); the board oracle and its mutation checks (T-51);
the integration lanes RCI-1 to RCI-9 (T-55).

| Id | Title | Wave |
|---|---|---|
| R5-01 | Ghost build: a part-name substitute before... | 0 |
| R3W-01 | Neutral skip reason for host-filtered entries | 2 |
| R3W-06 | `RivalPlaybackPolicy` | 2 |
| R3W-07 | Rival frame: flags and the open queue (pure) | 3 |
| R5-03 | Engine: per-trajectory build options and st... | 3 |
| P2b-02 | The lane type and the seven log-once sets (... | 4 |
| P2b-03 | Orbit cache on the lane (fold) | 5 |
| R3W-04 | Payload loader and the flattening of relati... | 5 |
| P2b-04 | Ghost position entries and reapply stamps o... | 6 |
| P2b-05 | Loop-unit lookup on the lane (fold) | 7 |
| P2b-06 | The positioner holds the lane (fold with al... | 8 |
| R3W-03 | Header loader from the tree file | 8 |
| T-12 | Seam action `RaceCommand action=set-folder`... | 8 |
| P2b-07 | The late-update helpers take the lane | 9 |
| P2b-08 | Watch and ghost-world queries on the lane | 9 |
| P2b-09 | The catalog moves onto the lane | 9 |
| T-35 | Lane RACE-10-flight-batch (the FLIGHT host) | 9 |
| P2b-10 | The lane owns its resets | 10 |
| T-21 | Race evaluator, part 2: structure facets an... | 11 |
| R3E-17 | Sync ticks | 12 |
| R3W-05 | `RivalStore` | 12 |
| T-17 | Committed shared-folder fixtures | 12 |
| R3W-08 | `RaceRuntime` scene calls | 13 |
| T-19 | `RaceCommand action=sync` | 13 |
| T-39 | Lane RACE-6, first form: two saves exchange through the seam only | 13 |
| R3W-09 | Flight composition (milestone MS-1d) | 14 |
| T-30 | `ListHandles kind=board` | 14 |
| T-20 | `ListHandles kind=rivals` | 15 |
| T-50 | Race campaign spec generator (RCA-k / RCB-k) | 15 |
| T-51 | Board oracle and its mutation checks | 15 |
| T-24 | Lane RACE-2-import-generator-folder | 16 |
| T-26 | Lane RACE-3-rival-ghosts-flight | 16 |
| T-28 | Lane RACE-6-two-saves-in-turn | 16 |
| T-53 | First change of hands: lanes RCA-1, RCB-1, RCA-2 | 16 |
| T-55 | Race integration variants (lanes RCI-1 to RCI-9) | 16 |

### F.5 MS-2 The four sites (19 tasks, waves 4 to 11)

DO / SEE: in a race save at slot 1 to 3 the space center sits at the player's site; launches,
recoveries and the editor's launch rules follow it; leaving the race restores stock Kerbin; a
modified Kerbin is refused at load; scripted player B flies from slot 2.

Exit proof: lanes SITES-1 (R2-12), SITES-2 with its comparisons against RACE-0 (R2-13) and
SITES-3 (R2-14), compared through T-46's tolerance comparator; R2-15's fold into the design with
`test_doc_spec_sync.py` green; lane RACE-11-ts-batch with `RaceSitesTrackingStation` (T-36);
the refused-load lanes RACE-16 and RACE-17 (T-42); stage S3, the schedule row that whichever of
R2-12 and T-50 lands second adds. MS-2's own tasks close at wave 11; S3 flies with or after
T-53 (wave 16).

| Id | Title | Wave |
|---|---|---|
| R2-01 | Site table and the reconcile decision, wire... | 4 |
| R2-03 | The three terrain patches in every race save | 5 |
| T-46 | Sites tolerance comparator (`ListHandles kind=sites ... tolerance=`) | 5 |
| R2-04 | Move the working space center to the home site | 6 |
| R1-19 | The race content table and its generator | 7 |
| R2-02 | Kerbin fingerprint and the modified-Kerbin... | 7 |
| R2-05 | Parsek's own home position follows the home... | 7 |
| R2-06 | One scenery copy of the KSC, built inert | 7 |
| R2-08 | Launch rules: the own site and, when the ra... | 7 |
| R2-11 | Every stock "home" surface at the home site | 7 |
| R1-20 | The tree and part-value check at create,... | 8 |
| R2-07 | Copies at every other site, as part of apply | 8 |
| R2-09 | Science parity: the KSC mini-biomes at home... | 9 |
| T-36 | Lane RACE-11-ts-batch (the TRACKSTATION host) | 9 |
| T-42 | Committed race-save fixtures and the refused-load lanes RACE-16, RACE-17 | 9 |
| R2-12 | Harness lane SITES-1: a launch, a recovery... | 10 |
| R2-15 | Fold the findings into the design and the p... | 10 |
| R2-13 | Harness lane SITES-2: back to stock Kerbin,... | 11 |
| R2-14 | Harness lane SITES-3: launch rules and the... | 11 |

### F.6 MS-3 Edit evidence and self-heal (6 tasks, waves 2 to 15)

DO / SEE: a changed or deleted standing shows as a receipt disagreement in the witness log; an
offline export lands in the own mirror and copies out with continuous seq when back online; a
foreign edit of the own subfolder heals.

Exit proof: `RaceSyncWitnessTests` and `RaceSyncLoopbackTests` (R3E-19) with T-21's witness
facet; R3E-20's heal tests; R3E-24's in-game `RaceExchange` cell on RACE-9; lane
RACE-8-export-self-heal-offline (T-25); the paired runner's cells (T-54); lane
RACE-13-paired-copies when T-34 is taken (optional).

| Id | Title | Wave |
|---|---|---|
| T-54 | Paired lanes in the parallel runner | 2 |
| R3E-19 | Witness log, chain, receipts and the neutra... | 13 |
| R3E-20 | Self-heal of the own subfolder | 14 |
| R3E-24 | In-game exchange check | 15 |
| T-25 | Lane RACE-8-export-self-heal-offline | 15 |
| T-34 | Lane RACE-13-paired-copies (optional; a spec on T-54) | 15 |

### F.7 MS-4 The rival world beyond flight (29 tasks, waves 0 to 19)

DO / SEE: rivals everywhere outside the one flight: orbit and surface leftovers, map and
Tracking Station objects that cannot be flown, labels, flags and plaques, the ghost budget,
and the whole-save proof that none of it leaks into the player's career.

Exit proof: lane RACE-4-isolation-whole-save (T-27); R3W-29's isolation test; R3W-30's
`RaceRivalIsolation` and `RaceRivalIsolationTrackingStation` on RACE-10 and RACE-11; the
`RaceRivalLane` cells of R3W-10 to R3W-25 over T-47's rival fixture content, and
`RaceRivalLaneTrackingStation.TrackingStationListsRivalOrbit` (R3W-20); RACE-2's Tracking
Station step (R3W-20); `GrepAuditTests.GrepAudit_RivalStore*` and R3W-28's gate; R3E-13's
`RaceFactsTrackingStation`; R3W-16's GUI-tree key assertion and R3W-21's `logContract` on
RACE-10; R5-04's `GhostMap` cell on S1.6; lane RACE-12-flown-rival-campaign over the folder
T-59 harvests (T-33).

| Id | Title | Wave |
|---|---|---|
| R3E-12 | The vessel-ended ledger row type (A2 data) | 0 |
| R3E-06 | The capture switch for race-only facts | 5 |
| R3W-17 | Landed variant of the ghost map vessel prim... | 5 |
| R5-04 | Ghost map vessels carry the style (A5) | 5 |
| R3E-13 | Record a vessel's end outside a recording (... | 6 |
| R3W-21 | Missing parts | 6 |
| R3E-14 | The stand query (A2) and the LEFTOVERS block | 11 |
| T-33 | Lane RACE-12-flown-rival-campaign (harvest by T-59) | 12 |
| T-47 | Rival fixture content for hosted cells | 12 |
| R3W-27 | Grep gates: rival store names and the loade... | 13 |
| T-27 | Lane RACE-4-isolation-whole-save | 14 |
| R3W-10 | Lazy open budget and payload lifetime | 15 |
| R3W-11 | Leftovers held at their last moment | 15 |
| R3W-13 | Show or hide a rival (reader side) | 15 |
| R3W-14 | A5: one ghost budget for both engines | 15 |
| R3W-15 | A5: per-trajectory style | 15 |
| R3W-22 | Removal and replacement at scene load | 15 |
| R3W-28 | Source gate: what the rival lane never calls | 15 |
| R3W-12 | Launch areas kept clear | 16 |
| R3W-16 | Labels on rival ghosts in flight | 16 |
| R3W-18 | `RivalMapPresence` | 16 |
| R3W-32 | Per-frame cost check | 16 |
| R3W-19 | "Whose ghost" answers for rivals | 17 |
| R3W-23 | Rival flag records | 17 |
| R3W-20 | Tracking Station composition | 18 |
| R3W-24 | Plaque replicas in flight | 18 |
| R3W-29 | Headless byte-identity test | 18 |
| R3W-25 | Reading a rival plaque | 19 |
| R3W-30 | In-game isolation category | 19 |

### F.8 MS-5 Screens and messages (17 tasks, waves 0 to 18)

DO / SEE: from the main menu a player opens Space Race, creates or joins through the dialog,
changes folder, name or look in Settings, and reads the board in the Space Race window with
holder-change messages; a test game is named in the window header and the join summary.

Exit proof: lane RACE-7-holder-change-folder-update (T-32); lane RACE-19-setup-screens (T-43)
over the setup screens and Settings section of R1-14, R1-15, R1-17 and R1-18; R4-13's
`SpaceRace` on RACE-9 and `SpaceRaceFlight` on RACE-10; the R1 form and model tests
(`RaceSaveListingTests`, `RaceSetupDialogModelTests`, `RaceSetupFormTests`,
`RaceJoinFormTests`, `RacePlayerSettingsTests`); `RaceBoardPresentationTests`,
`RaceMessagesTests`, `WatchCellDecisionTests`, `ClaimChecksCoverTests`; R4-12's gallery cells
and census step; R4-09's window-set and header tests; R4-11's camera cell through
`UiAction op=state`; R1-16's tier run (D.4).

| Id | Title | Wave |
|---|---|---|
| R4-08 | Shared table-window chrome (A6) | 0 |
| R1-16 | Lift the load-and-start route beside P10's... | 1 |
| R5-07 | The look table's visual half (RaceCore) | 3 |
| R1-14 | Main menu entry, setup dialog shell and Con... | 9 |
| R1-15 | The Create page | 10 |
| R1-17 | The Join page and "start over in my seat" | 10 |
| R1-18 | The Multiplayer section in Settings | 10 |
| T-43 | Lane RACE-19-setup-screens: setup screens through the seam | 11 |
| R4-05 | The replay is the proof: claim checks and t... | 14 |
| R4-07 | Holder-change events, message text and posting | 14 |
| R4-09 | The Space Race window: skeleton and registr... | 14 |
| R4-06 | Rows this install cannot fully read | 15 |
| T-32 | Lane RACE-7-holder-change-folder-update | 15 |
| R4-10 | The row model and the drawn board | 16 |
| R4-11 | Click to watch | 17 |
| R4-12 | Gallery mock states and the census | 17 |
| R4-13 | In-game checks: the ladder against stock, t... | 18 |

### F.9 MS-6 Scoring rules (13 tasks, waves 0 to 18)

DO / SEE: the board marks what it should: tool use and debug completions (except the test-tools
mark in a test game), save marks, parts outside the counted sets, a content table or
fingerprint that differs, locked difficulty settings, and "not scored" play; the window names
the differences; a differing content table warns at launch.

Exit proof: `ToolUseDecisionTests` and in-game `RaceFacts` on RACE-10, its single-player half on
RACE-20 (R3E-08); the `CheatOptionsWatch.Diff` tests (R3E-09); `GameplayFingerprintTests`
(R1-19); `RaceContentCheckTests` (R1-20); `RaceDifficultyHoldTests` and
`RaceModeTests.ProgressionSettingsHeld` (R1-21); `UncountedPartsTests` (R1-22); lane
RACE-15-content-check-launch-warning (T-41); `ClaimChecksToolTests` with the test-game cells
(R6-01); `RaceDisclosureTests` (R6-05); `RaceNotesTests` (R6-06); `ClaimChecksPartsTests`, rows
T15 and T16 (R6-12).

| Id | Title | Wave |
|---|---|---|
| R3E-07 | The tool-use fact on a recording (A3 data) | 0 |
| R3E-08 | Capture the tools Parsek already detects (A3) | 6 |
| R1-21 | The progression settings are held | 8 |
| R1-22 | The launch warning | 9 |
| T-41 | Lane RACE-15-content-check-launch-warning (stock-minimal) | 10 |
| R3E-10 | Tools in the standing | 11 |
| R3E-15 | Disclosure facts | 11 |
| R3E-09 | Stock cheat switches and debug actions (A3) | 12 |
| R3E-25 | Uncounted parts, save marks and `contentDi... | 14 |
| R6-01 | Tool marks | 15 |
| R6-05 | Disclosure facts | 17 |
| R6-06 | Missing-parts, newer-version and cannot-be-... | 17 |
| R6-12 | The parts mark, the save marks and "not sco... | 18 |

### F.10 MS-7 Looks (7 tasks, waves 1 to 19)

DO / SEE: each player's kerbals wear their look's suit at the Space Center and on EVA ghosts; a
rival's EVA ghost wears the rival's look even without Making History; the stock suit picker is
blocked in a race save and free in a single-player one.

Exit proof: `RaceLooks` on RACE-9, including `SuitChoiceBlockedInRaceSave` and
`ReplacementSetsResolve` (R5-11, R5-14, R5-12); `RaceLooksFlight` `RivalEvaGhostInRivalLook` on
RACE-10 (R5-09); the `GhostVisuals` cells on H15 (R5-02, R5-08); `RaceLookDresserDecideTests`
and `RaceLookDressTests`; R5-12's zip-listing cell; the single-player halves on RACE-20
(R5-11's log contract, R5-14's `SuitChoiceFreeInSinglePlayer`).

| Id | Title | Wave |
|---|---|---|
| R5-02 | Ghost build: a material override at the clo... | 1 |
| R5-12 | The replacement suit sets | 2 |
| R5-08 | The rival ghost dresser (RaceWorld) | 3 |
| R5-10 | The dress rules (RaceCore, pure) | 6 |
| R5-11 | `RaceLooks`: own roster dressing | 6 |
| R5-14 | Block the suit choice of the stock suit picker in a race save | 7 |
| R5-09 | The rival lane wears the looks and colors | 19 |

### F.11 MS-8 End date and no retakes (10 tasks, waves 13 to 19)

DO / SEE: the board applies no retakes, closes at the end date with a final board and joint
winners, handles a late standing inside and past the grace, and keeps old files by the keep
rule after the end; a scripted player retakes by rewind and flies a Re-Fly against a rival.

Exit proof: `NoRetakesTests` (R6-02); `RaceFinalTests` (R6-03, R6-04); `KeepRuleTests`
(R3E-21); R6-11's generator-built tests; the changed-file scenario with the keep rule after the
end date (T-37); `RaceMessagesTests` end rows (R6-08); lane RACE-14-scoring-marks with the
header rows of R6-07 through `UiAction op=state` (T-40); lanes RCA-3r to RCA-3f and RCB-3r to
RCB-3f (T-56, stage S4).

| Id | Title | Wave |
|---|---|---|
| R3E-21 | Keep and prune | 13 |
| R6-02 | No retakes | 16 |
| R6-03 | The end date and the final board | 17 |
| T-56 | Retake, no retakes, Re-Fly against a rival (stage S4) | 17 |
| R6-04 | Arrived late | 18 |
| R6-07 | The window at the end and its header | 18 |
| R6-08 | End-of-race messages | 18 |
| R6-11 | No retakes and the end date on generator-bu... | 18 |
| T-37 | The changed-file scenario and the keep rule after the end date (late half of T-31) | 18 |
| T-40 | Lane RACE-14-scoring-marks; `kind=board` and `RaceCommand status` show every mark | 19 |

### F.12 MS-9 Sync tuning (S8, last) (4 tasks, waves 17 to 19)

DO / SEE: the poll numbers are tuned from a measurement on the common sync clients; the copy
worker exists only with the owner's yes (Q-18).

Exit proof: the S8a and S8b reports; `RacePollPolicyTests` and the diagnostics-line budget on a
lane (R6-10), which also writes the 1.1.0 CHANGELOG warning; R3E-23's worker tests over an
injected slow file layer. The rerun on real sync clients is on the owner list
(test-and-harness section 1).

| Id | Title | Wave |
|---|---|---|
| S8a | File access cost on sync clients (no game) | 17 |
| S8b | A blocking read in KSP versus a worker thread | 18 |
| R3E-23 | The copy worker (only with the owner's yes) | 19 |
| R6-10 | Poll tuning | 19 |

### F.13 MS-10 Two scripted careers to the Mun (capstone) (2 tasks, waves 16 to 18)

DO / SEE: both scripted players fly their whole careers to the Mun in one race, from two sites,
and the board oracle agrees at every stage.

Exit proof: lanes RCA-1 to RCA-5 and RCB-1 to RCB-5 flown by T-49's runner on two slots with the
board oracle armed (T-57, stage S5); lanes RCA-0 and RCB-0 when T-58 is taken (optional).

| Id | Title | Wave |
|---|---|---|
| T-58 | True new race from the seam and an accept-by-type selector (optional) | 16 |
| T-57 | Both careers to the Mun (stage S5) | 18 |

### F.14 Unanchored (8 tasks)

No exit proof needs these. P3b-01 to P3b-04, R3W-26 and R5-06 wait for the deferred Watch
camera (map 10.2: later); attach them to a new milestone if the owner schedules it. R2-10
lands only if spike S2 asks for it (then MS-2). R5-05 lands only on r3w question 2 = (b),
assumed not built.

| Id | Title | Wave |
|---|---|---|
| R5-05 | Parsek-drawn map overlays take the style (c... | 4 |
| R2-10 | (conditional on S2) Biome override around t... | 10 |
| P3b-02 | A lane adapter between the watch camera and... | 11 |
| P3b-01 | A watch target value | 12 |
| P3b-03 | Enter and follow by target | 12 |
| P3b-04 | Lane-qualified watch query for the playback... | 13 |
| R3W-26 | Watching a live rival ghost (optional) | 18 |
| R5-06 | Watch overlay title carries the prefix (con... | 19 |
