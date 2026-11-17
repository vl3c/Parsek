# Spike S9: which debug actions a mod can observe (<YYYY-MM-DD>)

Verdict: OWED (draft). The code, the run sheet and the summarizer are ready. Every measured
cell below is OWED until `PC-RUN.md` has been run on the owner's PC.

This draft becomes `docs/dev/research/mp-spike-S9-<YYYY-MM-DD>/README.md` (plan 6.4). Fill
it from `summary-s9.md` (run sheet step 26) and the notes of steps 7, 20 and 21 to 24.

## Question
For every stock debug action and switch, can Parsek see it happen, once per press, with
no false positives from normal play; and for every way a milestone completes outside a
flight, can a mod observe it. Settles the 7.12 tool list, its out-of-reach list and the
disclosure wording; waits: R3E-09, R6-01.

## Decides
- Design 7.12: the tool list, the out-of-reach list, the disclosure wording.
- R3E-09: the save-level debug-completion fact, one per completion this spike shows a
  mod can observe (`DISCLOSURE` rows `DEBUG_COMPLETION`, `Standing.DebugCompletions`).
- R6-01: which completions drop from the board and give the `DebugCompleted` mark
  (owner decision D3, design 13.A, OV-34).

## Build
- `spike/mp-r0` at OWED (it must contain `spike/mp-r0-s9`).
- Deployed DLL sha256: OWED (`$RS\deployed-dll-sha256.txt`).
- Instance `spike-r0`, KSP 1.12.5, DLCs: OWED, mods: OWED (from the instance manifest).
- Env: `PARSEK_SPIKE=S9`, `PARSEK_TEST_COMMANDS=1` (segment marks only).

## Procedure as run
As in `spikes/r0/S9/PC-RUN.md`, steps 0 to 27, one KSP session. Saves: four copies of the
`spike-s0` career (`SPIKE-S9-PRESS`, `SPIKE-S9-MS-EARLY`, `SPIKE-S9-MS-FULL`,
`SPIKE-S9-MS-B4D455`) and one new Easy career `SPIKE-S9-CONTROL`. Record any change from
the run sheet here.

1. Presses, N = 3 per button and 6 clicks per switch (on and off three times), 2 s
   apart, in segments marked through the command seam: `career`, `max`, `contracts`,
   `kerbal`, `physics`, `difficulty`, `console-ksc` at the Space Center; `toggles`,
   `heat`, `gravity`, `comets`, `teleport`, `repair`, `thrower` (and `legacy` if
   present) on the pad.
2. Control: a new career, a contract accepted and completed, a facility upgraded, a
   launch, a crew report transmitted, an EVA report, F5 / F9, a recovery, a tech bought.
3. Milestone ways, each from a fresh copy: Max Progression right click (`ms-early`),
   left click (`ms-full`), console `/b4d455` (`ms-b4d455`), three presses each.

Changes from the plan:
- **Prefixes on entry, not postfixes.** Each hook logs when its target is entered and a
  Harmony finalizer closes it, so an event fired inside a debug action (a milestone,
  a currency change) is tagged with the action's tool (`scope=`). Still log only.
- **The Difficulty screen has no apply.** Its seven switches are compiler-generated
  lambdas that write the game parameters at once (decompile), so they are polled, not
  hooked.
- **Currency changes are a layer of their own.** `AddFunds` / `SetFunds`, `AddScience` /
  `SetScience`, `AddReputation` / `SetReputation` log only when the reason is
  `TransactionReasons.Cheating`: the plan's fallback, measured beside the entry points.
- **Max Progression is two ways.** A right click calls `CheatEarlyProgression` (the home
  body); any other click calls `CheatProgression` (every body). Each runs on its own copy.
- **Not pressed:** Physics Save (writes the instance's physics file) and the console's
  `targetv` (targets only); both are hooked.
- **Control in an Easy career**, so a facility upgrade can be bought with normal funds
  (250000 starting funds) without editing a save.

## Measurements
### Hooks and segments (from `summary-s9.md`)
| Measure | Value | Pass number | Result |
|---------|-------|-------------|--------|
| `HOOKS:` line | OWED | `applied=85 missing=0 failed=0` | OWED |
| Segments marked | OWED | all of step 26 | OWED |
| `PRESSES:` line (rows, fails) | OWED | `fail=0` | OWED |
| Observed lines outside every press segment | OWED | 0 (or each explained) | OWED |
| Inventory: `DebugToolbar` objects, SPACECENTER / FLIGHT | OWED / OWED | - | - |

### Lines per press (the rows of "Presses per segment" that FAIL, or "none")
OWED. Paste the FAIL rows; for a switch row the `values` note shows the on / off order.

### Classification of each tool (from "Classification of each tool")
| tool group | class | entry lines per press | delay | note |
|---|---|---|---|---|
| funds / science / reputation buttons (12) | OWED | OWED | 0 (entry) | each also seen as a Cheating currency change |
| Max Tech / Facilities / XP | OWED | OWED | 0 | effect `CheatTechnology` / `CheatFacilities` / `CheatExperience` |
| Repair | OWED | OWED | 0 | `CheatRepair` once per deployable module |
| Max Progression | OWED | OWED | 0 | milestone table below |
| 12 cheat switches (`CheatOptions`) | OWED | OWED | entry 0; poll within 1 s | |
| Hack Gravity switch, slider, reset | OWED | OWED | OWED | `SetGravityFactor` effect; factor polled |
| Set Orbit, Rendezvous, Set Position | OWED | OWED | 0 | `PostOrbitSet` effect |
| Comets (10 spawns, colliders, kill) | OWED | OWED | 0 | |
| Object thrower: arm switch | OWED | OWED | entry 0; poll within 1 s | |
| Object thrower: a throw | not observable by an entry point (decompile) | - | - | thrown from `Update` on mouse input |
| Contracts: accept / complete, cancel / decline, generate, 4 tools | OWED | OWED | 0 | |
| Kerbal create | OWED | OWED | 0 | |
| Physics Load / Save | OWED / hooked, not pressed | OWED | 0 | |
| Heat generation switch | OWED | OWED | 0 | |
| Difficulty screen (7 switches) | OWED (expected: polling only) | 0 | within 1 s | no apply button |
| Console `funds` / `science` / `reputation` | OWED | OWED | 0 | |
| Console `rendezvous` | OWED | OWED | 0 | |
| Console `b4d455` | OWED | OWED | 0 | milestone table below |
| Legacy cheat window | OWED (present: OWED) | OWED | OWED | |
| Fill resources | does not exist in 1.12.5 (decompile) | - | - | |
| Mod teleports (HyperEdit and similar) | out of reach | - | - | disclosure line |

### Control session (from "Control session")
| Measure | Value | Pass number | Result |
|---------|-------|-------------|--------|
| Observed lines | OWED | 0 | OWED |
| Poll changes | OWED | 0 | OWED |
| Events inside a debug scope | OWED | 0 | OWED |
| Currency events with reason Cheating | OWED | 0 | OWED |
| Control steps covered | OWED | 7 of 7 | OWED |
| `CONTROL:` line | OWED | PASS | OWED |

### Milestones completed outside a flight (R3E-09's list)
| way | entry point | nodes newly completed | observed at (UT of the first) | events fired in scope (Reached / Complete / Achieved) | same events as a real completion | Result |
|---|---|---|---|---|---|---|
| Max Progression, right click | `MaxProgression.OnPointerClick` -> `CheatEarlyProgression` -> `ProgressNode.CheatComplete` | OWED | OWED | OWED | OWED | OWED |
| Max Progression, left click | -> `CheatProgression` -> `CheatComplete` | OWED | OWED | OWED | OWED | OWED |
| Console `b4d455` | `ConsoleCommands.OnBadassCommand` -> `CheatEarlyProgression` | OWED | OWED | OWED | OWED | OWED |
| Legacy cheat window | OWED (only if present) | OWED | OWED | OWED | OWED | OWED |
| `ProgressNode.CheatComplete` alone | reached only through the two `ProgressTracking` cheats (decompile) | - | - | - | - | - |

Real completions in the control session fired: OWED (the summary's "Real completions"
line). Node lists per way: OWED (paste the "Nodes newly completed by ..." lines).

## What the design must change
Known before the run (decompile of KSP 1.12.5 `Assembly-CSharp.dll`):
- **7.12, the tool list.** Hack gravity is the `HackGravity` widget's state, not a
  `CheatOptions` field. There is no stock "fill resources" debug action. Missing from the
  list: `IgnoreEVAConstructionMassLimit`, `IgnoreKerbalInventoryLimits`,
  `IgnoreAgencyMindsetOnContracts`, `MiddleMouseClickSetPosition`, Max Tech, Max
  Facilities, Max XP, Repair, the console's `funds` / `science` / `reputation` /
  `rendezvous` / `b4d455`, the Physics, Difficulty, Kerbal and contract-tool screens,
  comets and the object thrower (plan corrections 1, 3, 4).
- **R3E-09 must key on the debug entry point, not on `CheatTechnology` alone.**
  `ResearchAndDevelopment.CheatTechnology()` has a caller outside the debug tools: the
  Making History mission editor (`<GenerateMissionLaunchSites>d__129` from
  `OnEditorStarted`). A "tech cheated" fact taken from that method alone would mark a
  player who only opened the mission builder.
- **Every stock debug currency change carries `TransactionReasons.Cheating`** (career
  buttons through `AddFunds` / `CheatAddScience` -> `AddScience` / `AddReputation`; the
  console and `b4d455` through `SetFunds` / `SetScience` / `SetReputation`), so
  `GameStateRecorder.IsCheatCurrencyReason` already sees each one. The run confirms
  the counts.
- **Max Progression is one button with two actions** (right click: home body only).
  7.12 names both.
- **`b4d455`** sets funds (31415926), science (1337) and reputation absolutely, researches
  every tech, maxes every facility, completes the home-body progression, maxes kerbal
  experience and adds a KSC waypoint, in one command.
- **The Difficulty screen writes six game parameters and one cheat option with no
  apply**, so a mod sees it only by polling (expected delay: within the poll period).
OWED after the run: one line per tool the run finds "not seen", and the milestone ways
whose completions fire different events than a real completion (the A3 risk of 9.2).

## Fallback taken
OWED. Planned: none beyond the currency layer, which runs in every session (an action with
no entry point is classified by its Cheating currency change or listed as not
observable).

## Raw artifacts
`<umbrella>/logs/spikes/S9/<YYYY-MM-DD>/`:
- `KSP.log`, `observed.csv`, `seam-responses.txt`.
- `summary-s9.md`, `deployed-dll.txt`, `deployed-dll-sha256.txt`.
These are spike saves only; none of the owner's own saves is read. Curated into this
folder: `summary-s9.md`.

## Follow-ups
OWED. Owner questions raised by the draft:
- Does a switch seen only by the 1 Hz poll (the Difficulty screen) count as observed for
  the disclosure, or as out of reach?
- Should the Making History mission builder's technology unlock be ignored (keyed on the
  debug entry point), as proposed above?
- If no scene builds the legacy cheat window, is it dropped from 7.12 or kept as an
  out-of-reach line?
