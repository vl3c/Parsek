# Multiplayer implementation - task index

PLAN (2026-11-14), not started.

This index joins the eleven task plans in this folder into one dependency graph. It lists every
task once, puts each in a wave, names the files that two plans both edit, orders the owner's PC
time, and lists what the graph does not close. It changes no plan. Where it recommends a reading,
the plan files still need the edit; the master plan decides. It was rebuilt on 2026-11-14 from
the plans as they read after the three reconciliation passes; the plans are the source.

| Plan file | Short name | Task headings | Dropped | Live tasks | Pull requests |
|---|---|---|---|---|---|
| `r0-spikes.md` | r0 | 17 | 0 | 17 | 17 |
| `r0b-prep-small.md` | small | 11 | 0 | 11 | 11 |
| `r0b-prep-playback.md` | playback | 21 | 0 | 21 | 20 |
| `r0b-prep-map-watch-codec.md` | map | 14 | 0 | 14 | 13 |
| `r1-mode-and-setup.md` | r1 | 18 | 1 | 17 | 17 |
| `r2-sites.md` | r2 | 15 | 0 | 15 | 15 |
| `r3-export-and-exchange.md` | r3e | 24 | 1 | 23 | 23 |
| `r3-rival-world.md` | r3w | 32 | 1 | 31 | 31 |
| `r4-r6-board-and-rules.md` | r4 | 25 | 1 | 24 | 23 |
| `r5-looks.md` | r5 | 13 | 1 | 12 | 10 |
| `test-and-harness.md` | T | 34 | 7 | 27 | 27 |
| Total | | 224 | 12 | 212 | 207 |

A live task is a heading that is not dropped. P2a-01 is live but is a specification with no pull
request of its own. Four live tasks land inside another task's pull request (P3b-01, R4-03, R5-07,
R5-10; see the "lands in PR of" column), so 211 live tasks make 207 pull requests. R3E-04's
golden folder may be a second pull request of that task (r3e :488), which would make 208. Ten of
the 207 are deferred or conditional: P3b-02, P3b-03 (with P3b-01), P3b-04, R3W-26 and R5-06 wait
for the owner to schedule the Watch camera (map 10.2, assumed B); P1-04, R2-10, R3E-23, R5-05 and
T-34 land only on an owner answer or a spike result. That leaves 197 firm pull requests. Every
count here comes from a script over the plan headings and their `Size` / `Depends on` / `PC`
lines, not from hand counting.

Citations read `plan :line`, using the short names above. "go" means the owner's go only.

## How the graph was built

- A dependency is hard when the task cannot start or cannot pass its tests without it. A soft
  dependency (a later row of a lane, an optional fallback) is shown in the table but not counted.
- Hard dependencies are taken from each task's own `Depends on` line as it reads today. An order
  stated in that line ("order: after X", "lands after X") counts as hard, because the task cannot
  merge before X: R1-02 after P10-01, R3W-01 after P2c-03, R5-03 after P2a-02.
- A spike counts as a dependency. Its column in the tables is "spikes". A spike named in a task's
  `Depends on` line or `Spike` field is counted, even when only one step needs it. A spike named
  only where the plan says the task ships placeholders until it (r5 :66-67: R5-01 to R5-07 do not
  wait for S3) is listed as soft.
- An owner gate (an answer to a section 10 question) is shown but not counted in the waves, since
  the owner can answer at any time. "assumed" marks a question the plan already answers with a
  stated assumption.
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
| "every spike report" (S13, r0 :840) | S0 to S12 |
| "P2a-02 to P2a-07" (P2b-02, playback :564) | P2a-02, P2a-03, P2a-04, P2a-05, P2a-06, P2a-07 |
| "P6-05 (or its fallback)" (R3W-04, r3w :431) | P6-05; the fallback (r3w :67-70) is soft |
| "owner's yes (question 2)" (R3E-23, r3e :928) | owner gate r3e Q2, not a task |
| "R3 question 2" (R5-05, r5 :379) | owner gate r3w Q2, not a task |
| "questions 10.1 and 10.2" (R5-12, r5 :577) | owner gates r5 Q1, Q2, not tasks |
| "owner question 10.1" (P1-04, small :617) | owner gate small Q1, not a task |
| "parallel flights" (T-34, T :700) | no task in these plans (E, item 11) |

### Stricter readings taken

| Task | The `Depends on` line says | Other text says | Taken |
|---|---|---|---|
| P2b-02 | P2a-02 to P2a-07, P2c-03 (playback :564) | R5-03 merges before P2b-02 opens the window (playback :57, :731; r5 :47-50) | R5-03 added |
| T-12 | R1-08 (T :375) | verb PRs land one at a time: R1-08, R1-10, R1-11, T-12, T-19 (T :715-716; r1 :835-837) | R1-11 added |

## A. Task table

One table per plan, in plan order. "hard dependencies" are task ids counted in the waves;
"spikes" are spikes counted in the waves. "soft" is shown, not counted. "owner gate" names the
section 10 question. "PC" is the owner's PC use: no, spike, in-game (one batch or a short manual
check), lanes (existing harness lanes), flight (a new lane flown), tier or full tier (a whole
tier run). "lands in PR of" names the task whose pull request carries this one. "wave" is the
earliest wave (section B). A dropped task keeps its heading and says where its work went.

### A.1 `r0-spikes.md` (17 tasks, 0 dropped, 17 pull requests)

| id | title (short) | size | hard dependencies | spikes | soft (not counted) | owner gate | PC | lands in PR of | wave |
|---|---|---|---|---|---|---|---|---|---|
| S0 | Spike branch and spike instances | S | go | - | - | r0 Q2 | spike | - | 0 |
| S1b | Finer terrain scan of the three spots | S | - | S0, S4a | - | - | spike | - | 2 |
| S2 | KSC science at a moved site, and none from... | M | - | S4b, S5 | - | - | spike | - | 4 |
| S3 | The four looks, replacement sets, a missing... | M | - | S0 | - | - | spike | - | 1 |
| S4a | Probe: where the KSC is and what reads it | S | - | S0 | - | - | spike | - | 1 |
| S4b | Move the working KSC | M | - | S4a | - | - | spike | - | 2 |
| S5 | KSC copies as scenery, cost, terrain parity | M | - | S4a, S4b | - | - | spike | - | 3 |
| S6 | Race mark survival, main menu entry, create... | M | - | S0 | - | - | spike | - | 1 |
| S7 | Flag replica with the stock plaque dialog | S | - | S0, S4b | - | - | spike | - | 3 |
| S8a | File access cost on sync clients (no game) | S | - | S0 | - | r0 Q1 | spike (no KSP) | - | 1 |
| S8b | A blocking read in KSP versus a worker thread | S | - | S8a | - | - | spike | - | 2 |
| S9 | Which debug actions a mod can observe | M | - | S0 | - | - | spike | - | 1 |
| S10a | Count the relative stretches and their body... | S | - | S0 | - | - | spike (owner saves only) | - | 1 |
| S10b | How far a flattened stretch drifts from its... | S | - | S10a | - | - | spike | - | 2 |
| S11 | Ghost cost per frame at race scale (new) | S | - | S0 | - | - | spike | - | 1 |
| S12 | Hashing and signing inside KSP's Mono (new,... | S | - | S0 | - | - | spike | - | 1 |
| S13 | Close-out: results into the design | S | - | every spike S0..S12 | - | r0 Q4 | no | - | 5 |

### A.2 `r0b-prep-small.md` (11 tasks, 0 dropped, 11 pull requests)

| id | title (short) | size | hard dependencies | spikes | soft (not counted) | owner gate | PC | lands in PR of | wave |
|---|---|---|---|---|---|---|---|---|---|
| P12-01 | `[forbidden]` takes a list on the right side | S | go | - | - | - | no | - | 0 |
| P12-02 | The Controllers rule places `ParsekRace*` r... | S | go | - | shares the P12-01 branch (optional) | - | no | - | 0 |
| P11-01 | Census specs stop pinning the window count | S | go | - | - | - | no | - | 0 |
| P11-02 | One `ParsekUI` constructor body | S | go | - | - | - | lanes | - | 0 |
| P7-01 | The id check on the floor with a fixed char... | S | go | - | - | - | no | - | 0 |
| P7-02 | A save-root accessor on `RecordingPaths` | S | P7-01 | - | - | - | no | - | 1 |
| P1-01 | `HomeSpaceCenter` in `Core`, read by the KS... | S | go | - | rebases on P7-01 if it landed | - | no | - | 0 |
| P1-02 | One launch-area shape test, called by the 5... | S-M | P1-01 | - | - | small Q1 | lanes | - | 1 |
| P1-03 | The launch-site circle cache follows the ho... | S | P1-01 | S4a | - | - | lanes | - | 2 |
| P1-04 | (owner-gated) Do not keep a partial first r... | S | P1-03 | - | - | small Q1 | lanes | - | 3 |
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
| P2b-07 | The late-update helpers take the lane | S | P2b-06 | - | - | - | lanes (daily tier) | - | 9 |
| P2b-08 | Watch and ghost-world queries on the lane | M | P2b-06 | - | - | - | lanes | - | 9 |
| P2b-09 | The catalog moves onto the lane | S | P2b-06, P2a-03, P2a-04 | - | - | - | no | - | 9 |
| P2b-10 | The lane owns its resets | S | P2b-09 | - | - | playback Q1 | full tier | - | 10 |

### A.4 `r0b-prep-map-watch-codec.md` (14 tasks, 0 dropped, 13 pull requests)

| id | title (short) | size | hard dependencies | spikes | soft (not counted) | owner gate | PC | lands in PR of | wave |
|---|---|---|---|---|---|---|---|---|---|
| P3-01 | Pin the primitive's runtime contract with a... | S | go | - | - | - | in-game | - | 0 |
| P3-02 | Move the guard set and the teardown depth i... | S | P3-01, P2a-07 | - | - | - | no | - | 2 |
| P3-03 | Owner record and a teardown that leaves oth... | S | P3-02 | - | - | - | no | - | 3 |
| P3-04 | Move the ProtoVessel mechanics into `GhostM... | M | P3-03 | - | - | - | lanes | - | 4 |
| P3-05 | The neutral "whose ghost is this" query at... | S | P3-03 | - | - | - | no | - | 4 |
| P3b-01 | A watch target value | S (no own PR) | P2c-04, P2b-10 | - | - | map Q2 (assumed B: deferred) | no | P3b-03 | 12 |
| P3b-02 | A lane adapter between the watch camera and... | M | P2c-04, P2b-10 | - | - | map Q2 (deferred) | lanes | - | 11 |
| P3b-03 | Enter and follow by target | M | P3b-02 | - | - | map Q2 (deferred) | lanes | - | 12 |
| P3b-04 | Lane-qualified watch query for the playback... | S | P3b-03, P2b-10 | - | - | map Q2 (deferred) | lanes | - | 13 |
| P6-01 | Commit an identity manifest over every comm... | S | go | - | - | map Q1 (assumed A) | no | - | 0 |
| P6-02 | List-level cores for the flat-fallback heal | S | P6-01 | - | - | - | no | - | 1 |
| P6-03 | The payload type; `Read` becomes its first... | M | P6-02 | - | - | - | no | - | 2 |
| P6-04 | `TryValidatePayload` decodes without a `Rec... | S | P6-03 | - | - | - | no | - | 3 |
| P6-05 | A production entry to load a trajectory fro... | S | P6-04 | - | - | - | no | - | 4 |

### A.5 `r1-mode-and-setup.md` (18 tasks, 1 dropped, 17 pull requests)

| id | title (short) | size | hard dependencies | spikes | soft (not counted) | owner gate | PC | lands in PR of | wave |
|---|---|---|---|---|---|---|---|---|---|
| R1-01 | Register the three race modules | S | P12-01, P12-02 | - | - | - | no | - | 1 |
| R1-02 | One helper to find a scenario node (A7, par... | S | P10-01 | - | - | - | no | - | 1 |
| R1-03 | The race mark: `RaceLink` and `ParsekRaceSc... | M | R1-01, P12-02 | S6 | - | - | in-game | - | 2 |
| R1-04 | `RaceGate` and its host | M | R1-02, R1-03 | S4a | - | - | in-game | - | 3 |
| R1-05 | The race mark's row in `LoadReconcilePolicy` | S | R1-03 | - | - | - | no | - | 3 |
| R1-06 | The race grep-audit gates (design 15.4) | M | R1-01, R1-03 | - | - | - | no | - | 3 |
| R1-07 | Logs and test batches leave `Parsek/Race/`... | S | go | - | - | - | no | - | 0 |
| R1-08 | Read side: codecs, timestamps, local race d... | M | R1-04, P7-01 | - | - | - | no (rides T-13) | - | 4 |
| R1-09 | Seats, slots, looks and names (`RaceSlots`) | S-M | R1-08 | - | - | - | no | - | 5 |
| R1-10 | Create a race (setup core, write side) | M | R1-08, R1-09, P10-01 | S6 | - | - | lanes (RACE-1) | - | 6 |
| R1-11 | Join a race and start over in my seat | M | R1-10 | - | - | - | lanes (RACE-1) | - | 7 |
| R1-12 | Register the race formats with a golden folder | S | R1-03, R1-10, T-01 | - | - | - | no | - | 7 |
| R1-13 | Harness lanes MP-1 and MP-2 | - | dropped: work moved to T-13 | - | - | - | - | - | - |
| R1-14 | Main menu entry, setup dialog shell and Con... | M | R1-12, T-13, R1-16 | S6 | - | r1 Q1 (assumed a) | in-game | - | 9 |
| R1-15 | The Create page | M | R1-14 | - | - | r1 Q2, Q3 | in-game | - | 10 |
| R1-16 | Lift the load-and-start route beside P10's... | S | P10-01 | - | - | - | tier run | - | 1 |
| R1-17 | The Join page and "start over in my seat" | M | R1-14 | - | - | - | in-game | - | 10 |
| R1-18 | The Multiplayer section in Settings | M | R1-08, R1-09, R1-10, R1-14 | - | - | - | in-game | - | 10 |

### A.6 `r2-sites.md` (15 tasks, 0 dropped, 15 pull requests)

| id | title (short) | size | hard dependencies | spikes | soft (not counted) | owner gate | PC | lands in PR of | wave |
|---|---|---|---|---|---|---|---|---|---|
| R2-01 | Site table and the reconcile decision, wire... | M | R1-01, R1-03, R1-04, P12-02, P1-02 | S1b, S4a | - | r2 Q1 | in-game | - | 4 |
| R2-02 | Kerbin fingerprint and the modified-Kerbin... | S-M | R2-01, R1-10 | S5 | - | - | in-game | - | 7 |
| R2-03 | The three terrain patches in every race save | M | R2-01 | S4b, S5 | - | - | in-game | - | 5 |
| R2-04 | Move the working space center to the home site | M | R2-03 | S4b | - | - | in-game | - | 6 |
| R2-05 | Parsek's own home position follows the home... | S | R2-04, P1-01, P1-03 | S4a | - | - | in-game | - | 7 |
| R2-06 | One scenery copy of the KSC, built inert | M | R2-04 | S5 | - | - | in-game | - | 7 |
| R2-07 | Copies at every other site, as part of apply | M | R2-06 | S5 | - | - | in-game | - | 8 |
| R2-08 | Launch rules: the own site and, when the ra... | S-M | R2-01, R1-03, R1-10 | S4b | - | r2 Q2 | in-game | - | 7 |
| R2-09 | Science parity: the KSC mini-biomes at home... | S | R2-04, R2-07 | S2 | - | - | in-game | - | 9 |
| R2-10 | (conditional on S2) Biome override around t... | S-M | R2-09 | S2 | - | S2 result | in-game | - | 10 |
| R2-11 | Every stock "home" surface at the home site | S-M | R2-04 | S4b | - | - | in-game | - | 7 |
| R2-12 | Harness lane SITES-1: a launch, a recovery... | M | R2-04, R2-05, R2-07, R2-08, R2-09, R2-11, R1-10, R1-11, T-03, T-07 | - | - | - | flight | - | 10 |
| R2-13 | Harness lane SITES-2: back to stock Kerbin,... | M | R2-12, T-07 | - | - | - | flight | - | 11 |
| R2-14 | Harness lane SITES-3: launch rules and the... | S-M | R2-08, R2-12 | - | - | - | flight | - | 11 |
| R2-15 | Fold the findings into the design and the p... | S | R2-04, R2-09 | S2, S4b, S5 | - | - | no | - | 10 |

### A.7 `r3-export-and-exchange.md` (24 tasks, 1 dropped, 23 pull requests)

| id | title (short) | size | hard dependencies | spikes | soft (not counted) | owner gate | PC | lands in PR of | wave |
|---|---|---|---|---|---|---|---|---|---|
| R3E-01 | Exchange vocabulary and the own-mirror scan | M | P7-01, P12-01, P12-02, R1-01, R1-03, R1-06, R1-07, R1-08, R1-10 | - | - | - | no | - | 7 |
| R3E-02 | Public state from the own model | M | R3E-01 | - | R3W-03 (key list) | - | no | - | 8 |
| R3E-03 | The public ghost snapshot | M | R3E-02 | - | - | - | no | - | 9 |
| R3E-04 | Write a standing to the own mirror; golden v3 | M (golden may be a 2nd PR) | R3E-03, R1-03, R1-08, R1-12, T-01 | - | - | - | no | - | 10 |
| R3E-05 | Synthetic shared-folder builder (test only) | - | dropped: work moved to T-16, R1-08, T-02, R3E-16 tests | - | - | - | - | - | - |
| R3E-06 | The capture switch for race-only facts | S | R1-04, R1-08 | - | - | - | no | - | 5 |
| R3E-07 | The tool-use fact on a recording (A3 data) | S | go | - | - | - | no | - | 0 |
| R3E-08 | Capture the tools Parsek already detects (A3) | M | R3E-06, R3E-07 | - | - | - | in-game | - | 6 |
| R3E-09 | Stock cheat switches and debug actions (A3) | S | R3E-08, R3E-15 | S9 | - | - | in-game | - | 12 |
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
| R3E-20 | Self-heal of the own subfolder | M | R3E-16, R3E-19, R3E-22, T-18 | - | - | r3e Q1 | no | - | 14 |
| R3E-21 | Keep and prune | M | R3E-11, R3E-18 | - | - | - | no | - | 13 |
| R3E-22 | "N shared flights left the race" | S | R3E-04 | - | - | - | no | - | 11 |
| R3E-23 | The copy worker (only with the owner's yes) | M | R3E-16 | S8a, S8b | - | r3e Q2 (yes) | in-game | - | 12 |
| R3E-24 | In-game exchange check | S | R3E-18, R3E-20 | - | - | - | in-game | - | 15 |

### A.8 `r3-rival-world.md` (32 tasks, 1 dropped, 31 pull requests)

| id | title (short) | size | hard dependencies | spikes | soft (not counted) | owner gate | PC | lands in PR of | wave |
|---|---|---|---|---|---|---|---|---|---|
| R3W-01 | Neutral skip reason for host-filtered entries | S | P2c-03 | - | - | - | no | - | 2 |
| R3W-02 | `RivalFlight` value type | S | P2c-01, R1-01 | - | - | - | in-game | - | 2 |
| R3W-03 | Header loader from the tree file | S | R3W-02, R3E-01 | - | - | - | in-game | - | 8 |
| R3W-04 | Payload loader and the flattening of relati... | M | R3W-02, P6-05 | S10a, S10b | P6-05 has a fallback (r3w :67-70) | - | in-game | - | 5 |
| R3W-05 | `RivalStore` | M | R3W-03, R3E-04, T-16 | - | - | - | in-game | - | 12 |
| R3W-06 | `RivalPlaybackPolicy` | S | P2c-03, R1-01 | - | - | - | in-game | - | 2 |
| R3W-07 | Rival frame: flags and the open queue (pure) | S | R3W-01, R3W-02 | - | - | - | in-game | - | 3 |
| R3W-08 | `RaceRuntime` scene calls | S | R1-08, R3W-05, R3W-06 | - | - | - | in-game | - | 13 |
| R3W-09 | Flight composition (milestone M1) | M | R3W-04, R3W-05, R3W-06, R3W-07, R3W-08, P2a-02, P2a-05, P2a-06, P2a-07, P2b-09, P2b-10, P2c-04, R1-10, R3E-04 | - | - | - | in-game | - | 14 |
| R3W-10 | Lazy open budget and payload lifetime | S | R3W-09 | - | - | - | in-game | - | 15 |
| R3W-11 | Leftovers held at their last moment | M | R3W-09, R3E-14 | - | - | - | in-game | - | 15 |
| R3W-12 | Launch areas kept clear | S | R3W-11, R2-01, P1-02 | - | - | - | no | - | 16 |
| R3W-13 | Show or hide a rival (reader side) | S | R3W-09, R1-08 | - | - | - | no | - | 15 |
| R3W-14 | A5: one ghost budget for both engines | S | P2c-04, R5-03, R3W-09 | S11 | - | - | no | - | 15 |
| R3W-15 | A5: per-trajectory style | S | R3W-09, R5-03 | - | - | - | no | - | 15 |
| R3W-16 | Labels on rival ghosts in flight | S | R3W-15 | - | - | r3w Q3 (assumed a) | in-game | - | 16 |
| R3W-17 | Landed variant of the ghost map vessel prim... | S | P3-04, P3-05 | - | - | - | in-game | - | 5 |
| R3W-18 | `RivalMapPresence` | M | R3W-09, R3W-11, R3W-15, R3W-17, P3-04, P3-05, R5-04 | - | - | r3w Q2 (assumed a) | in-game | - | 16 |
| R3W-19 | "Whose ghost" answers for rivals | S | R3W-18, P3-05 | - | - | - | in-game | - | 17 |
| R3W-20 | Tracking Station composition | S | R3W-18, R3W-19 | - | - | - | in-game | - | 18 |
| R3W-21 | Missing parts | S | R3W-04 | - | - | - | in-game | - | 6 |
| R3W-22 | Removal and replacement at scene load | S | R3W-09, R3E-18 | - | - | - | no | - | 15 |
| R3W-23 | Rival flag records | S | R3W-05, R3W-12, R3E-14 | - | - | - | in-game | - | 17 |
| R3W-24 | Plaque replicas in flight | M | R3W-23 | S7 | - | - | in-game | - | 18 |
| R3W-25 | Reading a rival plaque | S | R3W-24 | S7 | - | r3w Q1 | in-game | - | 19 |
| R3W-26 | Watching a live rival ghost (optional) | M | P3b-04, R3W-19 | - | - | optional; deferred with P3b | in-game | - | 18 |
| R3W-27 | Grep gates: rival store names and the loade... | S | R3W-05, R1-06 | - | - | - | no | - | 13 |
| R3W-28 | Source gate: what the rival lane never calls | S | R3W-09 | - | - | - | no | - | 15 |
| R3W-29 | Headless byte-identity test | M | R3W-08, R3W-11, R3W-23, T-01, T-16 | - | - | - | no | - | 18 |
| R3W-30 | In-game isolation category | M | R3W-18, R3W-20, R3W-24 | - | - | - | in-game | - | 19 |
| R3W-31 | Whole-save harness lane | - | dropped: work moved to T-27 | - | - | - | - | - | - |
| R3W-32 | Per-frame cost check | S | R3W-14 | - | - | - | in-game | - | 16 |

### A.9 `r4-r6-board-and-rules.md` (25 tasks, 1 dropped, 23 pull requests)

| id | title (short) | size | hard dependencies | spikes | soft (not counted) | owner gate | PC | lands in PR of | wave |
|---|---|---|---|---|---|---|---|---|---|
| R4-01 | The race calendar and the date rule | S | R1-01, R1-08, R1-10 | - | - | - | no | - | 7 |
| R4-02 | The ladder table standard-1 | S | R1-01, R1-08, R1-10 | - | - | - | no | - | 7 |
| R4-03 | The ladder filter at board time | S | R4-02, R3E-11 | - | - | - | no | R4-04 | 13 |
| R4-04 | The board: shapes, exact points and the bas... | M | R4-01, R4-02, R4-03, R1-08, R3E-01, R3E-04, R3E-18 | - | - | - | no | - | 13 |
| R4-05 | The replay is the proof: claim checks and t... | S | R4-04 | - | - | - | no | - | 14 |
| R4-06 | Rows this install cannot fully read | S | R4-05 | - | - | - | no | - | 15 |
| R4-07 | Holder-change events, message text and posting | S | R4-04, R1-08, R3E-04, R3E-22 | - | - | r4 Q2 | no | - | 14 |
| R4-08 | Shared table-window chrome (A6) | S | go | - | - | - | lanes (optional census) | - | 0 |
| R4-09 | The Space Race window: skeleton and registr... | M | R4-04, R4-08, P11-01, P11-02, R1-04, R1-08, R1-14, R1-18, R3E-17 | - | - | - | in-game (R4-13) | - | 14 |
| R4-10 | The row model and the drawn board | M | R4-09, R4-05, R4-06, R5-07, R1-10 | - | - | - | in-game (R4-13) | - | 16 |
| R4-11 | Click to watch | S | R4-10 | - | camera half: P3b-04, R3W-26 (deferred) | camera half deferred | in-game (camera half) | - | 17 |
| R4-12 | Gallery mock states and the census | S | R4-10 | - | - | - | lanes (census) | - | 17 |
| R4-13 | In-game checks: the ladder against stock, t... | S | R4-02, R4-12 | - | - | - | in-game | - | 18 |
| R4-14 | Harness: the board in the two-player lanes | - | dropped: work moved to T-28, T-32 | - | - | - | - | - | - |
| R6-01 | Tool marks | S | R4-05, R3E-07, R3E-09, R3E-10 | S9 | - | - | no | - | 15 |
| R6-02 | No retakes | M | R6-01, R3E-21 | - | - | r4 Q1 | no | - | 16 |
| R6-03 | The end date and the final board | M | R6-02, R3E-21 | - | - | - | no | - | 17 |
| R6-04 | Arrived late | S | R6-03, R3E-19 | - | - | - | no | - | 18 |
| R6-05 | Disclosure facts | S | R4-10, R6-01, R3E-15 | - | - | - | no | - | 17 |
| R6-06 | Missing-parts, newer-version and cannot-be-... | S | R4-10, R1-08, R3E-18 | - | - | - | no | - | 17 |
| R6-07 | The window at the end and its header | M | R6-03, R6-05, R6-06 | - | - | - | in-game | - | 18 |
| R6-08 | End-of-race messages | S | R6-03, R4-07 | - | - | r4 Q3 | no | - | 18 |
| R6-09 | The no-retakes preview for the Join dialog | S | R6-02, R1-17 | - | - | - | no | - | 17 |
| R6-10 | Poll tuning | S | R3E-17 | S8a, S8b | - | - | in-game | - | 13 |
| R6-11 | No retakes and the end date on generator-bu... | S | R6-02, R6-03, T-16, T-17 | - | - | - | no | - | 18 |

### A.10 `r5-looks.md` (13 tasks, 1 dropped, 10 pull requests)

| id | title (short) | size | hard dependencies | spikes | soft (not counted) | owner gate | PC | lands in PR of | wave |
|---|---|---|---|---|---|---|---|---|---|
| R5-01 | Ghost build: a part-name substitute before... | S | go | - | - | - | in-game | - | 0 |
| R5-02 | Ghost build: a material override at the clo... | S | R5-01 | - | S3d | - | in-game | - | 1 |
| R5-03 | Engine: per-trajectory build options and st... | S | R5-01, P2c-04, P2a-02 | - | - | - | no | - | 3 |
| R5-04 | Ghost map vessels carry the style (A5) | S | P3-04, R5-03 | - | S3a (colors); in-file order after R3W-17 | r5 Q3 (assumed a) | in-game | - | 5 |
| R5-05 | Parsek-drawn map overlays take the style (c... | S | R5-03 | - | - | r3w Q2 = b only (assumed a: not built) | no | - | 4 |
| R5-06 | Watch overlay title carries the prefix (con... | S | P3b-03, R5-03, R3W-26 | - | - | deferred with P3b, R3W-26 | no | - | 19 |
| R5-07 | The look table's visual half (RaceCore) | S | R1-01 | - | S3a, S3b (placeholder colors) | - | no | R5-08 or R5-11, first to land | 3 |
| R5-08 | The rival ghost dresser (RaceWorld) | M | R5-02, R5-07, R5-12 | S3 | - | - | in-game | - | 3 |
| R5-09 | The rival lane wears the looks and colors | M | R3W-09, R3W-15, R3W-18, R3W-20, R3W-21, R5-03, R5-04, R5-08 | - | S3c | - | in-game | - | 19 |
| R5-10 | The dress rules (RaceCore, pure) | S | R5-07 | S3 | - | - | no | R5-11 | 6 |
| R5-11 | `RaceLooks`: own roster dressing | M | R1-03, R1-08, R1-09, R5-10, R5-12 | S3 | - | - | in-game | - | 6 |
| R5-12 | The replacement suit sets | M | - | S3 | - | r5 Q1, Q2 | in-game | - | 2 |
| R5-13 | A harness lane pins the `RaceLooks` category | - | dropped: work moved to T-29 | - | - | - | - | - | - |

### A.11 `test-and-harness.md` (34 tasks, 7 dropped, 27 pull requests)

| id | title (short) | size | hard dependencies | spikes | soft (not counted) | owner gate | PC | lands in PR of | wave |
|---|---|---|---|---|---|---|---|---|---|
| T-01 | Headless save-output driver and its determi... | S | go | - | - | - | no | - | 0 |
| T-02 | Fake synced store and fake clocks | M | go | - | - | - | no | - | 0 |
| T-03 | Shared-folder staging in run.py | M | go | - | - | - | no | - | 0 |
| T-04 | Snapshot every save a lane creates | S | go | - | - | - | no | - | 0 |
| T-05 | Pure race evaluator, part 1: leak scan and... | M | go | - | - | T Q2 (assumed a) | no | - | 0 |
| T-06 | Harness-side folder step | S | T-03 | - | - | - | no | - | 1 |
| T-07 | Seam read kind `ListHandles kind=sites` | M | go | - | - | - | flight | - | 0 |
| T-08 | Race grep gates: shared scanner and "no sto... | - | dropped: work moved to R1-06 | - | - | - | - | - | - |
| T-09 | Race grep gate: Recording types only in the... | - | dropped: work moved to R1-06 | - | - | - | - | - | - |
| T-10 | Single-player inertness test (15.8) | S | T-01, R1-04 | - | - | - | no | - | 4 |
| T-11 | Seam verb `RaceSetup action=create/join` | - | dropped: work moved to R1-10, R1-11 | - | - | - | - | - | - |
| T-12 | Seam action `RaceCommand action=set-folder`... | S | R1-08, R1-11 | - | - | - | no (rides a lane) | - | 8 |
| T-13 | Lane RACE-1-setup-create-join | M | T-03, T-04, R1-10, R1-11 | - | - | - | flight | - | 8 |
| T-14 | Lane RACE-5-stock-kerbin-after-race | - | dropped: work moved to R2-13 | - | - | - | - | - | - |
| T-15 | Race grep gate: no writer of the own model | - | dropped: work moved to R1-06 | - | - | - | - | - | - |
| T-16 | Generator `RaceFolderBuilder` | M | R3E-04, R1-10 | - | - | - | no | - | 11 |
| T-17 | Committed shared-folder fixtures | S | T-16 | - | - | - | no | - | 12 |
| T-18 | Two-machine simulator bound to the ports | M | T-02, R3E-04, R3E-16 | - | R3E-20 (self-heal case) | - | no | - | 12 |
| T-19 | `RaceCommand action=sync` | S | T-12, R3E-17, R3E-18 | - | - | - | no (rides a lane) | - | 13 |
| T-20 | `ListHandles kind=rivals` | S | R3W-05, R3W-09 | - | - | - | no (rides a lane) | - | 15 |
| T-21 | Race evaluator, part 2: structure facets an... | M | T-05, T-03, R3E-04, R3E-19 | - | - | - | no | - | 14 |
| T-22 | Race grep gate: rival store names only in a... | - | dropped: work moved to R3W-27 | - | - | - | - | - | - |
| T-23 | Isolation byte-identity test (6.4) | - | dropped: work moved to R3W-29 | - | - | - | - | - | - |
| T-24 | Lane RACE-2-import-generator-folder | S | T-17, T-19, T-20, T-21, R3W-20 | - | - | - | flight | - | 19 |
| T-25 | Lane RACE-8-export-self-heal-offline | S | T-19, T-21, T-06, R3E-20 | - | - | - | flight | - | 15 |
| T-26 | Lane RACE-3-rival-ghosts-flight | M | T-17, T-19, T-20 | - | - | - | flight | - | 16 |
| T-27 | Lane RACE-4-isolation-whole-save | M | T-04, T-05, T-17, T-19 | - | R3W-18, R3W-24 (content) | T Q2 (assumed a) | flight | - | 14 |
| T-28 | Lane RACE-6-two-saves-in-turn | M | R1-10, R1-11, T-19, T-20, T-21, R3E-16 | - | R4-04 (row assertion) | T Q1 (assumed a) | flight | - | 16 |
| T-29 | In-game race context helper and the host lanes | M | T-17, T-19, R2-03, R2-05, R5-11, R3W-09, R3W-30 | - | first test of each other category | - | flight | - | 20 |
| T-30 | `ListHandles kind=board` | S | R4-04 | - | - | - | no (rides a lane) | - | 14 |
| T-31 | "Same files, same board" and the changed-fi... | M | T-16, T-18, R4-04, R3E-19, R6-03 | - | - | - | no | - | 18 |
| T-32 | Lane RACE-7-holder-change-folder-update | S | T-17, T-19, T-30, T-06, R4-07 | - | - | - | flight | - | 15 |
| T-33 | Lane RACE-12-flown-rival-campaign and the h... | M | R1-10, T-21, R3E-16 | - | - | - | flight | - | 15 |
| T-34 | Lane RACE-13-paired-copies (optional) | M | T-21, T-30 | - | parallel flights (outside the program) | T Q3 (assumed a); optional | flight | - | 15 |
Total: 224 task headings in 11 plans, each listed once: 12 dropped, 212 live, 207 pull requests.

## B. Waves

Wave 0 needs only the owner's go. 21 waves, 0 to 20. Within a wave, tasks of different plans can
run in parallel unless section C names a shared file. Two windows sit across the waves:

- The P2b window (P2b-02 merge to the P2b-10 flight proof, waves 4 to 10): no P6 pull request
  merges inside it (playback :50-52, map :56-57), and no other task edits its hot files
  (`ParsekFlight.cs`, the positioner, the playback host; playback :736-739).
- P6 (P6-01 to P6-05) is built in parallel and merged when green, outside that window (map 10.1,
  assumed A). Its waves 0 to 4 are a floor; P6-05 is needed only by R3W-04 (wave 5).
- P3b (waves 11 to 13 here) is deferred out of the preparation phase (map 10.2, assumed B).
  R3W-26 (wave 18), the camera half of R4-11 and R5-06 (wave 19) wait for it. The waves shown are
  the earliest if it is scheduled right after P2b-10.

### B.1 Waves by plan

**Wave 0** (25 tasks)

- r0-spikes: S0
- r0b-prep-small: P12-01, P12-02, P11-01, P11-02, P7-01, P1-01, P10-01
- r0b-prep-playback: P2c-01, P2c-02, P2a-05, P2b-01
- r0b-prep-map-watch-codec: P3-01, P6-01
- r1-mode-and-setup: R1-07
- r3-export-and-exchange: R3E-07, R3E-12
- r4-r6-board-and-rules: R4-08
- r5-looks: R5-01
- test-and-harness: T-01, T-02, T-03, T-04, T-05, T-07

**Wave 1** (21 tasks)

- r0-spikes: S3, S4a, S6, S8a, S9, S10a, S11, S12
- r0b-prep-small: P7-02, P1-02
- r0b-prep-playback: P2c-03, P2c-04, P2a-03, P2a-06, P2a-07
- r0b-prep-map-watch-codec: P6-02
- r1-mode-and-setup: R1-01, R1-02, R1-16
- r5-looks: R5-02
- test-and-harness: T-06

**Wave 2** (14 tasks)

- r0-spikes: S1b, S4b, S8b, S10b
- r0b-prep-small: P1-03
- r0b-prep-playback: P2a-02, P2a-04
- r0b-prep-map-watch-codec: P3-02, P6-03
- r1-mode-and-setup: R1-03
- r3-rival-world: R3W-01, R3W-02, R3W-06
- r5-looks: R5-12

**Wave 3** (12 tasks)

- r0-spikes: S5, S7
- r0b-prep-small: P1-04
- r0b-prep-map-watch-codec: P3-03, P6-04
- r1-mode-and-setup: R1-04, R1-05, R1-06
- r3-rival-world: R3W-07
- r5-looks: R5-03, R5-07, R5-08

**Wave 4** (9 tasks)

- r0-spikes: S2
- r0b-prep-playback: P2b-02
- r0b-prep-map-watch-codec: P3-04, P3-05, P6-05
- r1-mode-and-setup: R1-08
- r2-sites: R2-01
- r5-looks: R5-05
- test-and-harness: T-10

**Wave 5** (8 tasks)

- r0-spikes: S13
- r0b-prep-playback: P2b-03
- r1-mode-and-setup: R1-09
- r2-sites: R2-03
- r3-export-and-exchange: R3E-06
- r3-rival-world: R3W-04, R3W-17
- r5-looks: R5-04

**Wave 6** (8 tasks)

- r0b-prep-playback: P2b-04
- r1-mode-and-setup: R1-10
- r2-sites: R2-04
- r3-export-and-exchange: R3E-08, R3E-13
- r3-rival-world: R3W-21
- r5-looks: R5-10, R5-11

**Wave 7** (11 tasks)

- r0b-prep-playback: P2b-05
- r1-mode-and-setup: R1-11, R1-12
- r2-sites: R2-02, R2-05, R2-06, R2-08, R2-11
- r3-export-and-exchange: R3E-01
- r4-r6-board-and-rules: R4-01, R4-02

**Wave 8** (6 tasks)

- r0b-prep-playback: P2b-06
- r2-sites: R2-07
- r3-export-and-exchange: R3E-02
- r3-rival-world: R3W-03
- test-and-harness: T-12, T-13

**Wave 9** (6 tasks)

- r0b-prep-playback: P2b-07, P2b-08, P2b-09
- r1-mode-and-setup: R1-14
- r2-sites: R2-09
- r3-export-and-exchange: R3E-03

**Wave 10** (8 tasks)

- r0b-prep-playback: P2b-10
- r1-mode-and-setup: R1-15, R1-17, R1-18
- r2-sites: R2-10, R2-12, R2-15
- r3-export-and-exchange: R3E-04

**Wave 11** (10 tasks)

- r0b-prep-map-watch-codec: P3b-02
- r2-sites: R2-13, R2-14
- r3-export-and-exchange: R3E-10, R3E-11, R3E-14, R3E-15, R3E-16, R3E-22
- test-and-harness: T-16

**Wave 12** (9 tasks)

- r0b-prep-map-watch-codec: P3b-01, P3b-03
- r3-export-and-exchange: R3E-09, R3E-17, R3E-18, R3E-23
- r3-rival-world: R3W-05
- test-and-harness: T-17, T-18

**Wave 13** (9 tasks)

- r0b-prep-map-watch-codec: P3b-04
- r3-export-and-exchange: R3E-19, R3E-21
- r3-rival-world: R3W-08, R3W-27
- r4-r6-board-and-rules: R4-03, R4-04, R6-10
- test-and-harness: T-19

**Wave 14** (8 tasks)

- r3-export-and-exchange: R3E-20
- r3-rival-world: R3W-09
- r4-r6-board-and-rules: R4-05, R4-07, R4-09
- test-and-harness: T-21, T-27, T-30

**Wave 15** (15 tasks)

- r3-export-and-exchange: R3E-24
- r3-rival-world: R3W-10, R3W-11, R3W-13, R3W-14, R3W-15, R3W-22, R3W-28
- r4-r6-board-and-rules: R4-06, R6-01
- test-and-harness: T-20, T-25, T-32, T-33, T-34

**Wave 16** (8 tasks)

- r3-rival-world: R3W-12, R3W-16, R3W-18, R3W-32
- r4-r6-board-and-rules: R4-10, R6-02
- test-and-harness: T-26, T-28

**Wave 17** (8 tasks)

- r3-rival-world: R3W-19, R3W-23
- r4-r6-board-and-rules: R4-11, R4-12, R6-03, R6-05, R6-06, R6-09

**Wave 18** (10 tasks)

- r3-rival-world: R3W-20, R3W-24, R3W-26, R3W-29
- r4-r6-board-and-rules: R4-13, R6-04, R6-07, R6-08, R6-11
- test-and-harness: T-31

**Wave 19** (5 tasks)

- r3-rival-world: R3W-25, R3W-30
- r5-looks: R5-06, R5-09
- test-and-harness: T-24

**Wave 20** (1 tasks)

- test-and-harness: T-29
### B.2 Critical paths to the milestones

A path counts tasks from the first to the milestone, both included; it is one more than the
milestone's wave. A wave number is the earliest wave the milestone can start in; it finishes one
wave later. Every path below starts with S0 -> S6 -> R1-03 or, equally long, P12-01 -> R1-01 ->
R1-03, so the arch track and the owner's first spike day head them all.

| Milestone | Task | Wave | Tasks on the path | Bound by |
|---|---|---|---|---|
| First race save created | R1-10 | 6 | 7 | the R1 core: R1-03, R1-04, R1-08, R1-09 |
| Create and join proven in a lane (RACE-1) | T-13 | 8 | 9 | R1-11 |
| M2: menu entry | R1-14 | 9 | 10 | T-13; R1-12 (wave 7) and R1-16 (wave 1) have slack |
| M2: Create, Join pages, Settings | R1-15, R1-17, R1-18 | 10 | 11 | R1-14 |
| Sites visible: the KSC moved | R2-04 | 6 | 7 | R2-01, R2-03; spikes S4b (wave 2), S5 (wave 3) |
| M3: sites lane green | R2-12 | 10 | 11 | R2-06, R2-07, R2-09 (spike S2, wave 4); R1-11 at 7 |
| First standing exported | R3E-04 | 10 | 11 | R1-10, then R3E-01, R3E-02, R3E-03 |
| Standing copied out, then imported | R3E-16, R3E-18 | 11, 12 | 12, 13 | R3E-04 |
| M1: a rival ghost plays in flight | R3W-09 | 14 | 15 | R3E-04, T-16, R3W-05, R3W-08; P2b-10 (wave 10) has 3 waves of slack |
| Rivals drawn on the map, in the TS | R3W-18, R3W-20 | 16, 18 | 17, 19 | R3W-09, R3W-11, R3W-18, R3W-19 |
| Board computed | R4-04 (with R4-03) | 13 | 14 | R3E-18; R3E-11 (wave 11) has slack |
| Board shown in the window | R4-10 | 16 | 17 | R4-05, R4-06; R4-09 (wave 14) has 1 wave of slack |
| Board checked in game | R4-13 | 18 | 19 | R4-12 |
| M4: two-player lanes | T-32, T-28 | 15, 16 | 16, 17 | R4-07 for T-32; T-20 (R3W-09) for T-28 |
| M5: looks | R5-11, R5-09 | 6, 19 | 7, 20 | R1-09 for R5-11; R3W-20 for R5-09 |
| M6: no retakes and the end date proven | R6-11 | 18 | 19 | R4-05, R6-01, R6-02, R6-03 |
| last | T-29 | 20 | 21 | R3W-30, which needs R3W-20 and R3W-24 |

The paths (the first two steps tie with P12-01 -> R1-01):

- First save: S0 -> S6 -> R1-03 -> R1-04 -> R1-08 -> R1-09 -> R1-10. Then R1-11 -> T-13 ->
  R1-14 -> R1-15, R1-17 or R1-18 (M2).
- M3: S0 -> S6 -> R1-03 -> R1-04 -> R2-01 -> R2-03 -> R2-04 -> R2-06 -> R2-07 -> R2-09 ->
  R2-12. R2-03 to R2-09 also wait on S4b, S5 and S2, which r0 puts on the owner's PC days 2
  and 3 (D.1); the path holds only if those days come before R2-03 is ready.
- Exchange: the first-save path to R1-10, then R3E-01 -> R3E-02 -> R3E-03 -> R3E-04 ->
  R3E-16 -> R3E-18.
- M1: the first-save path to R3E-04, then T-16 -> R3W-05 -> R3W-08 -> R3W-09. R3W-08 now waits
  for R3W-05 (r3w :528) and R3W-05 for T-16's builder (r3w :467), so M1 sits four waves after the
  first standing. The playback side (P2c-02 -> P2c-04 -> P2a-02 -> R5-03 -> P2b-02 ... P2b-10)
  ends at wave 10.
- Rivals on the map and in the TS: M1, then R3W-11 -> R3W-18 -> R3W-19 -> R3W-20. M5's R5-09
  follows R3W-20.
- Board: the exchange path to R3E-18, then R4-04 -> R4-05 -> R4-06 -> R4-10 -> R4-12 -> R4-13.
- M4: R3E-18 -> R4-04 -> R4-07 -> T-32; and M1 -> T-20 -> T-28.
- M6: R3E-18 -> R4-04 -> R4-05 -> R6-01 -> R6-02 -> R6-03 -> R6-11.
- last: T-29 is the only host of the categories RACE-9, RACE-10 and RACE-11 pin (T :745-765).
  Its hard dependencies (T-17, T-19, R2-03, R2-05, R5-11, R3W-09, R3W-30) put it in the last
  wave, so until then those categories run only by hand on the owner's PC (r3e R3E-01 says so
  for its cell). Splitting T-29 per host would let RACE-9 fly once its first categories exist;
  the plan keeps one task.

## C. Parallel tracks

### C.1 Files that tasks of two or more plans edit

Each row gives the tasks in the order the plans now give. Two tasks on one row never run as open
PRs at the same time unless the row says the edits are disjoint.

| File or set | Tasks, in order | Source of the order |
|---|---|---|
| `scripts/arch/modules.toml` | P12-01, P12-02, R1-01, P7-01, P1-01, P1-02, P10-01, R1-02, P2a-05, R5-03, R3W-14 | P10-01 and R1-02 share LogIO :326 (small :724-726; r1 :831-832); P7-01, P1-01, P1-02 share the Core list :478 (small :726-728); R1-01 rebases (E, item 3); R5-03, R3W-14 a rule each (r5 :605; r3w :1072) |
| `scripts/arch/test_archview.py` | P12-01, P12-02, P7-01, P1-01, P1-02, P2a-05, R1-06 | small :730-731 (which says P2a-01; E, item 2) |
| `GhostPlaybackEngine.cs` constructor | P2c-02, P2c-04, P2a-02, R5-03, R3W-09, R3W-14 | playback :729-732; r5 :602-604; r3w :1064-1066 |
| `GhostPlaybackEngine.Diagnostics.cs`, `GhostPlaybackEvents.cs` | P2c-02, P2c-03, R3W-01 | playback :733-734; R3W-01 is written against P2c-03's source gate |
| `GhostMapPresence.StateVectors.cs` (the `GhostMapPresence` partial) | P2a-07, P3-02 | playback :740-741; map :643-645 |
| `GhostMapVessels.cs` | P3-02 to P3-05, R3W-17, R5-04 (`ApplyOrbitStyle` in R3W-18's PR), R3W-19 | map :635-637; r3w :1068-1069; r5 :357-358 |
| `GhostMapPresence.cs` (name composer beside the prefix) | P3-04, R5-04 | map :649-654 |
| `RecordingPlaybackCatalog.cs` and its tests | P2a-05 (creates), P2a-03, P2a-02, R3W-09 (adds `None`) | playback :742-745; r3w :1067 |
| `ParsekFlight.cs` and partials | P2b-02 to P2b-10, then R3W-09, R3W-16; P3b when scheduled | playback :736-737; r3w :1060-1061 |
| `WatchModeController.cs` | P2b-08, then P3b-01 to P3b-04, R3W-26, R5-06 (all deferred) | playback :752-755; map :638 |
| `ParsekFlightRivalLane.cs` | R3W-09, R3W-10, R3W-13, R3W-16, R3W-18, R3W-24, R3W-26, R3W-32, then R5-09 | r3w :1062-1063; r5 :606 |
| `HomeSpaceCenter.cs`, `LaunchSiteExclusionZones.cs` | P1-01, P1-02, P1-03, P1-04, then R2-05 | small :744-745; r2 :742-743 |
| `LaunchArea.cs` | P1-02, then R2-01 (adds `LaunchArea.Strip`) | small :522-526; r2 :350-351 |
| `ParsekRaceBoot.cs` | R1-04 (creates), R2-01, R2-07 | r1 :843-844 |
| `RaceInstallCheck` (refusal list) | R1-10, then R2-02, R4-02 | r2 :386; r4 :817 |
| `RaceCore/RaceCalendar.cs` | R1-10, R4-01, then R4-04, R4-07, R4-10 | r4 :816 |
| `RacePorts.cs`, `RaceFolder.cs`, `RaceIds.cs`, `RaceTimestamp.cs`, `RaceManifest.cs` | R1-08, R1-09, R1-10, R1-11, then R3E-01 | r1 :845-847; r3e :957-958 |
| `RaceFolderCheck.cs`, `RaceSharedFolder.cs` | R1-10, R3E-01, R3E-16 | r3e :958-959 |
| `RaceWorld/RaceRuntime.cs` | R1-08, R3W-08, R4-09, R4-10, R4-11 or R3W-26 (first to land), R6-06 | r4 :812-814 |
| `RaceSync.cs`, `RaceSyncDriver.cs` | R3E-01, R3E-02, R3E-04, R3E-16, R3E-17, R3E-18, R3E-20, R3E-22, then R4-04, R4-07, R4-09, R6-10 | r3e :955-956; r4 :817-819 |
| the standing codec (`StandingCodec.cs`, `Standing.cs`, `PublicState.cs`) | R3E-01, R3E-04, the block tasks R3E-10, R3E-11, R3E-14, R3E-15, R3E-22 one at a time, R3E-09, then R6-01 (the tool view) | r3e :951-952, :957; r4 :818 |
| `ParsekRaceScenario.cs` | R1-03, R1-08, R3E-15, R5-11 | r5 :607 |
| `LoadReconcilePolicy.cs` | R1-05, R1-16 (either order), R3E-15, R5-11 | r1 :833-834; r3e :961; r5 :608 |
| `SaveFormat.cs`, `Fixtures/FormatGolden/` | R1-12 (both rows, `v2`), then R3E-04 (`v3`) | r1 :851-853; r3e :956 |
| `TestCommandVerbs.cs`, hlib verb tables, `design-autotest-command-seam.md` | P11-01 (seam doc), T-07, R1-08, R1-10, R1-11, T-12, T-19, T-20, T-30 | small :735-736; r1 :835-837; T :713-716 |
| `harness/lib/test_hlib.py`, `hlib.py` | P10-01, P11-01, T-03, T-04, T-07, then every lane and verb task | small :732-734; T :707-709 |
| `.claude/CLAUDE.md`, `AGENTS.md` | P10-01, R1-07, P2a-02, P2b-02, P3-04, R1-14, R2-03, R2-04, R2-07, R2-08, R3E-23, the R3W key-file line | small :737-739; playback :869-871; map :720; r1 :962; r2 :829-830; r3e :926; r3w :1144; one mirror copy per PR |
| generated `docs/dev/flight-profiles.md`, `harness/coverage/registry.toml`, `docs/dev/autotest-status.md` | every task that adds an in-game category or a lane (R5-01 and T-07 in wave 0); P3-01 does not change the catalog (map :269-273) | T :717-721; regenerate after merging `main`, never by hand |
| the RACE-9, RACE-10, RACE-11 spec tallies (T-29) | every task that adds a test in a hosted category re-pins its host's `total=` | r2 :738-740; T :632 |
| `GrepAuditTests.Race.cs`, `scripts/race-loader-files-allowlist.txt`, `RaceLogTags` | R1-06 (creates), R3W-03, R3W-04, R3W-06 (tag), R3W-27 | r1 :826; r3w :1074-1075 |
| `scripts/ers-els-audit-allowlist.txt` | P2a-05, P3b-02 | append-only |
| `scripts/arch/lifetimes.toml`, `lifetime_pins.toml`, the reset allowlist | P1-03 first, then P3-02, P3-03, R1-03, R1-04, R1-08, R1-10, R2-03, R2-05, R3E-06, R3E-08, R3W-08, R3W-14, R3W-19, R5-12 | small :742-743; re-pin after each merge |
| `docs/dev/design-gui-inventory.md` | R1-14, R1-15, R1-17, R1-18, R2-02, R2-08, R4-08, R4-09, then the R3E, R3W, R4, R5 tasks that add a surface | counts are re-derived, never hand-summed |
| `UI/SettingsWindowUI.cs` | R1-18, then R4-09 (Sync now, `RequestSyncNow`) | r4 :810-811 |
| `ParsekUI.cs`, `UI/ParsekWindowSet.cs` | P11-02, R4-08, R4-09 | small :741 |
| `ParsekKSC.cs` | R3E-17 (poll at scene entry), R4-09 (draw call), R5-09 only if rivals show at the KSC | r3e :785-786; r4 :807; r5 :733 |
| `InGameTestRunner.cs` | R1-07, R3E-08 (tool mark at the runner) | r1 :823; r3e R3E-08 |
| `Source/Parsek.Tests/Generators/` | T-01, T-16 (the one folder builder), R3W-05 (`RivalCacheBuilder` through T-16) | T :709-711; T :422-425; r3w :1043 |

### C.2 Tracks for waves 0 and 1

One agent per track, tasks in order. The supervisor runs at most four at once, so start ARCH,
SETUP, ENGINE and HARNESS first: ARCH heads every critical path (R1-01 in wave 1), and the owner
runs SPIKES on the PC in parallel (S6 on day 1 ties ARCH for R1-03).

| Track | Tasks in order | PC | Notes |
|---|---|---|---|
| ARCH | P12-01, P12-02, R1-01, P7-01, P1-01, P7-02, P1-02 | P1-02: four lanes | P12-02 may share P12-01's branch; R1-01 rebases on the Core lines (E, item 3); P7-01 before P1-01 / P1-02 (Core list); P1-02 waits on small Q1 |
| SETUP | P10-01, R1-02, R1-16, R1-07 | P10-01: three lanes; R1-16: one tier | R1-02 after P10-01 (LogIO line); R1-07 after P10-01 (CLAUDE.md) |
| ENGINE | P2c-02, P2c-03, P2c-04, P2c-01 | P2c-02 to P2c-04: lanes | R3W-01 follows in wave 2, after P2c-03 |
| CATALOG | P2a-05, P2a-03, P2a-06, P2a-07, P2b-01 | P2a: lanes | P2a-05 creates the catalog files and adds a `modules.toml` rule after ARCH's rows; P2a-07 before P3-02 |
| MAP / CODEC | P3-01, P6-01, P6-02 | P3-01: one in-game run | P6 merges when green, never inside the P2b window |
| GUI | P11-01, P11-02, R4-08 | P11-02: census lanes; R4-08: optional | P11-01 shares `test_hlib.py` with SETUP and HARNESS and lands the seam doc before T-07 |
| FACTS | R3E-07, R3E-12 | no | independent files |
| LOOKS | R5-01, R5-02 | one in-game run each | `GhostVisualBuilder.cs` in order; R5-01 regenerates the lane catalog (new `RaceLooksFlight`) |
| HARNESS | T-03, T-04, T-06, T-05, T-07 | T-07: RACE-0 flight | T-04 and T-06 after T-03 (T :707-709); T-07 shares the verb tables and the lane catalog |
| TESTSIM | T-01, T-02 | no | `Generators/` files only |
| SPIKES (owner) | S0, then S4a, S6, S9, S8a, S10a, S1b scan; S3, S11, S12 on day 4 | spike | D.1 |

Tasks of waves 0 and 1 that need no PC: P12-01, P12-02, P11-01, P7-01, P7-02, P1-01, P2c-01,
P2b-01, P6-01, P6-02, R1-01, R1-02, R1-07, R3E-07, R3E-12, T-01 to T-06 (script output).

Not side by side in waves 0 and 1 (same file):

- `modules.toml` and `test_archview.py`: P12-01, P12-02, R1-01, P7-01, P1-01, P1-02, P10-01,
  R1-02, P2a-05. P10-01 and R1-02 edit the same LogIO line; P7-01, P1-01 and P1-02 the same Core
  list and count.
- `.claude/CLAUDE.md` and `AGENTS.md`: P10-01, R1-07.
- `harness/lib/test_hlib.py` and `hlib.py`: P10-01, P11-01, T-03, T-04, T-06, T-07.
- `GhostPlaybackEngine.cs`: P2c-02, P2c-03, P2c-04.
- the catalog files: P2a-05 first; P2a-03, P2a-06, P2a-07 add rows after it (additive).
- `RecordingPaths.cs`: P7-01, P7-02. `SpawnCollisionDetector.cs`: P1-01, P1-02.
- generated `flight-profiles.md`: R5-01, T-07. `ParsekUI.cs`: P11-02, R4-08.
- `GhostVisualBuilder.cs`: R5-01, R5-02.

## D. Use of the owner's PC

Only one KSP flight runs at a time. `provision.py` and `run.py` share one machine lock, and
spike sessions launched by hand do not take it, so the owner runs no tier while a spike session
runs (r0 :864-867). Seam-only RACE lanes could run side by side only after the parallel-flights
work lands (T :806-811, owner gate T Q3).

### D.1 Spikes (r0 day plan, r0 :874-879; hours from each spike's Duration line)

| Day | Spikes | Hours on the PC |
|---|---|---|
| 1 | S0 1 h, S4a 1 h, S6 2 h, S9 2 h, S8a 2 h (no KSP), S10a 0.5 h, S1b scan | about 9 |
| 2 | S4b 3 h, S1b scoring (S1b 1 h in all), S2 2 h (steps 1 to 3; E, item 7) | about 5.5 |
| 3 | S5 3 h, S7 2 h, S2 step 4 (needs S5) | about 5 |
| 4 | S3 3 h, S10b 1 h, S8b 1 h with S12 10 min, S11 1 h | about 6 |
| any | S13 needs no PC | 0 |

About 26 hours in all (the Duration lines sum to 25.7 h). Never in parallel: S5's frame cost,
S8b and S11 (r0 :862). S4a and S6 go first because R1-04, R2-01, R2-05, P1-03 (S4a) and R1-03,
R1-10, R1-14 (S6) wait on them (r0 :881-882).

### D.2 One in-game run (a test batch or a short manual check)

From each task's `PC` line.

| Wave | Tasks |
|---|---|
| 0 | P3-01, R5-01 |
| 1 | R5-02 |
| 2 | R1-03, R3W-02, R3W-06, R5-12 |
| 3 | R1-04, R3W-07, R5-08 |
| 4 | R2-01 |
| 5 | R2-03, R3W-04, R3W-17, R5-04 |
| 6 | R2-04, R3E-08, R3E-13, R3W-21, R5-11 |
| 7 | R2-02, R2-05, R2-06, R2-08, R2-11 |
| 8 | R2-07, R3W-03 |
| 9 | R1-14, R2-09 |
| 10 | R1-15, R1-17, R1-18, R2-10 |
| 12 | R3E-09, R3E-23, R3W-05 |
| 13 | R3W-08, R6-10 |
| 14 | R3W-09, R4-09 |
| 15 | R3E-24, R3W-10, R3W-11 |
| 16 | R3W-16, R3W-18, R3W-32, R4-10 |
| 17 | R3W-19, R3W-23, R4-11 |
| 18 | R3W-20, R3W-24, R3W-26, R4-13, R6-07 |
| 19 | R3W-25, R3W-30, R5-09 |

### D.3 Harness lanes, a tier, or a new lane flown

From each task's `PC` line. "(flight)" is a new lane's first flights; the rest re-fly existing
lanes. R1-10 and R1-11 are proven through RACE-1 (T-13). T-12, T-19, T-20 and T-30 ride the lanes
that use them (T-13, T-24, T-26, T-32).

| Wave | Tasks |
|---|---|
| 0 | P11-02, P10-01, P2c-02, P2a-05, R4-08 (optional), T-07 (flight) |
| 1 | P1-02, P2c-03, P2c-04, P2a-03, P2a-06, P2a-07, R1-16 (tier) |
| 2 | P1-03, P2a-02, P2a-04 |
| 3 | P1-04 |
| 4 | P3-04 |
| 6 | R1-10 (via RACE-1) |
| 7 | R1-11 (via RACE-1) |
| 8 | T-13 (flight) |
| 9 | P2b-07 (daily tier), P2b-08 |
| 10 | P2b-10 (full tier), R2-12 (flight) |
| 11 | P3b-02, R2-13 (flight), R2-14 (flight) |
| 12 | P3b-03 |
| 13 | P3b-04 |
| 14 | T-27 (flight) |
| 15 | T-25 (flight), T-32 (flight), T-33 (flight), T-34 (flight) |
| 16 | T-26 (flight), T-28 (flight) |
| 17 | R4-12 (census) |
| 19 | T-24 (flight) |
| 20 | T-29 (flight) |

### D.4 Long runs

| When | Run | Hours | Source |
|---|---|---|---|
| after playback tracks A, C, D (P2c, P2a) | nightly tier | about 9.5 | playback :831-833 |
| after P2b-07, P2b-08, P2b-10 | daily tier | about 0.4 | playback :821-822 |
| after P2b-10 | full tier, 523 lanes | about 21.5 p50; plan a day and a half | playback :817-820 |
| after P2b-10, owner option B | daily + nightly + named operator lanes | about 9.9 plus the operator lanes | playback :822-825, Q1 |
| after P3-01, P3-04 | named lanes (H44, S1.6; then 11 lanes) | small | map :677-679 |
| after P6-03 | any daily tier run | about 0.4 | map :680-681 |
| after R1-16 | one tier run | one tier | r1 :774 |
| once RACE lanes exist | added capacity: daily about +45 s, nightly about +1800 s, operator about +400 s | small | T :799-800 |

The plans ask for one full run, after P2b-10 (owner question playback Q1, recommendation A).
T :800-802 still says P2a, P2b, P3, P3b and P6 each want one (E, item 8); five full runs would
cost about 107 hours of a PC that also runs the spikes.

## E. Findings

### E.1 Checks run on the graph

- Every task id with a heading in a plan appears in section A exactly once (224 ids).
- No hard dependency names an unknown id, a dropped task or the P2a-01 specification. No plan's
  `Depends on` line names a dropped task.
- The graph of 207 pull requests has no cycle.
- Every finding of the previous index (missing providers, the R4-03 / R3E-11 cycle, the wrong
  or loose dependencies, the duplicate providers, the wave-0 shared files, the oversized tasks)
  is settled in the plans as they read today and is removed here.

### E.2 Findings closed after this index was built

The first build of this index found eleven places where two plans still disagreed. The plan
files were edited to close them; none changed a wave. For the record:

| # | Was | Closed by |
|---|---|---|
| 1 | map said R3W-17 lands in R3W-18's pull request | map: R3W-17 is its own pull request; only R5-04's `ApplyOrbitStyle` half lands with R3W-18 |
| 2 | small named P2a-01 as an editor of `modules.toml` and `test_archview.py` | small: P2a-05 |
| 3 | small ordered R1-01 after the other `modules.toml` edits | small: R1-01 lands any time after P12-01 and rebases |
| 4 | P2b-02's `Depends on` line left out R5-03 | playback: R5-03 added as an order |
| 5 | T-12's `Depends on` line left out R1-11 | T: R1-11 added (verb file order) |
| 6 | r1's waiter table had the wrong needs for R4-02 and R4-10 | r1: R4-02 needs R1-01, R1-08, R1-10; R4-10 needs R1-10 |
| 7 | r0's day plan ran all of S2 on day 2 | r0: S2 steps 1 to 3 on day 2, step 4 on day 3 after S5 |
| 8 | T asked for a full run after five prerefactors | T: a full run after P2b-10 only; the others name smaller runs |
| 9 | r5 listed R3W-15 as adding a `modules.toml` rule | r5: dropped |
| 10 | R5-07's host could make a pull request of three tasks | r5: R5-08 hosts R5-07 |
| 11 | T-34 waited on "parallel flights" with no name | T: the parallel runner of pull request #2592, outside this plan |

### E.3 Still open

Nothing in the graph. The questions for the owner are in `design-corrections.md` part 2 and
in `README.md` section 7. Line citations in this index were taken before the edits above, so a
cited line of an edited plan can sit a few lines lower than the number says.
