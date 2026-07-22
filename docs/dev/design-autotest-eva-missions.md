# Design: EVA seam verbs + EVA missions (Module M-C2)

Status: DRAFT (2026-07-22). Module M-C2 of the Automated Testing Plan
(`docs/dev/automated-testing-plan.md`). Step 3 design doc, design only, no code.
Goal (operator directive 2026-07-22): missions that exercise how Parsek records
kerbal EVA interactions - ground EVA, orbital EVA, flag plants, board / re-board,
single and multi-kerbal crews - plus the seam verbs that make them drivable.

Consumed contracts (read as authorities, never re-specified here):
`design-autotest-command-seam.md` (M-A2: wire grammar, WAL journal, verdicts,
strict-FIFO pump, deferral budgets, two-phase PENDING);
`design-autotest-seam-verbs-c1.md` (M-C1: the PATTERN AUTHORITY for verb
specs - preconditions, two-phase completion, effect-confirmation-before-OK,
failure taxonomy, refusal-is-driver-INVALID, KILLED-is-terminal at-most-once,
dialog answering via the live button's own callback);
`design-autotest-mission-library.md` (M-B1: driver kinds, steps contract);
`automated-testing-scenario-catalog.md` (registry D1-D18, blocks B3 / S0.2 /
S2.2 / A4, regression R18). Plain ASCII, no em dashes, no emoji.

---

## Problem

The catalog names EVA coverage in four places - B3 "EVA branch: pad craft, EVA,
walk, board", S0.2 pad-walk EVA ghost, S2.2 mid-flight EVA branch, A4 orbital
EVA (rated HARD) - and the registry carries the cells (`D1 auto-record-eva`,
`D5 eva-branch`, `D7 flag-plant`, `D2 structural-event-snapshots`). None is
reachable by an automated scenario today:

- kRPC cannot spawn an EVA kerbal, cannot board one, and has no flag-plant
  surface. The autopilot driver (M-B1) is structurally unable to drive ANY
  EVA interaction - even the trivial exit-and-reboard-on-the-ladder case has
  no entry point (A4's HARD rating is only about jetpack control).
- The seam's 15 implemented verbs (`TestCommandVerbs.cs`, mirrored at
  `hlib.py` `IMPLEMENTED_SEAM_VERBS`) cover lifecycle, recording, re-fly,
  jumps, and KSC career actions, but nothing kerbal-physical; no reserved
  name covers EVA either.
- Parsek's EVA machinery is branch-heavy recording code: mid-recording EVA
  tree branches (`ParsekFlight.OnCrewOnEva`, `ParsekFlight.cs:7838`), the
  background-parent route (`TryStartEvaBranchFromBackgroundParent`, `:7734`),
  deferred EVA auto-record (`ShouldQueueAutoRecordOnEva`, `:8040`), the
  boarding chain-back (`:3388-3399`, `HandleTreeBoardMerge`, `:11878`), and
  flag capture (`OnAfterFlagPlanted`, `:10839`). Today only the in-game test
  batch (e.g. `EvaTwiceFromSameCapsule`) and manual play exercise it.

M-C2 adds the verb family `EvaExit` / `EvaBoard` / `PlantFlag` plus three
seam-driven mission specs (EVA-1 ground, EVA-2 orbital, EVA-3 multi-kerbal)
that turn those registry cells into runnable scenarios.

**Why PlantFlag must be a verb (decided up front).** Flag planting is not a
single stock call: `KerbalEVA.PlantFlag()` only enqueues an FSM sequence,
and at completion `FlagSite.OnPlacementComplete` opens the "SiteRename"
`PopupDialog`; `GameEvents.afterFlagPlanted` - the ONLY event Parsek
captures the `FlagEvent` from - fires exclusively from that dialog's button
callbacks (stock ground truth 3). A headless run that merely invoked
`PlantFlag()` would leave an unanswered modal dialog and Parsek would never
record the flag. The plant therefore needs the full seam treatment:
FSM-gated precondition, two-phase completion across the animation, and a
dialog answer via the live button's own callback (the `AnswerMergeDialog`
pattern). kRPC offers nothing here. PlantFlag is in scope as the third verb.

## Terminology

Terms from M-A2 (Command, Verdict, Journal, Pump, Safe point) and M-C1
(Irreversible verb, Bounded-wait verb, Verb refusal, Gate reason) are used
unchanged. New terms:

- **EVA controller**: the `KerbalEVA` PartModule on an EVA kerbal vessel's
  single part (`vessel.isEVA`); resolved from the active vessel.
- **Ladder release**: running the public `KerbalEVA.fsm` event
  `On_ladderLetGo` so a freshly spawned kerbal (hanging on the hatch ladder)
  drops to the ground - required before a flag plant (ground-contact gate).
- **Foreground EVA branch**: the mid-recording EVA path - `OnCrewOnEva` stops
  the live recorder, defers an EVA tree branch (`BranchPointType.EVA`,
  `BranchPoint.cs:9`), recording follows the kerbal, the capsule recording
  parks in the tree `BackgroundMap`.
- **Board merge**: the return path - `onCrewBoardVessel` stamps
  `pendingBoardingTargetPid`, the switch back sets `ChainToVesselPending`,
  and `HandleTreeBoardMerge` creates the Board branch point and chains
  recording back onto the craft.
- **Background-parent EVA branch**: EVA from a backgrounded tree member
  (`TryStartEvaBranchFromBackgroundParent`); needs a focus switch first and
  stays in-game-test-only in v1 (Deferred Items).

## Mental Model

```
  orchestrator appends id=..cmd=EvaExit kerbal=Bob release=true
        |
        v  DecideDispatch (pure): RequiresFlight for all three;
           EvaExit defers on split-pending, PlantFlag/EvaBoard on !isEVA
        |
        v  journal CLAIMED (WAL, at-most-once)
        |
        v  thin applier: EvaExit  -> FlightEVA.fetch.spawnEVA(...)
                                     (+ optional ladder release + dwell)
                         PlantFlag-> bounded-wait the stock plant gate,
                                     THEN KerbalEVA.PlantFlag(); then
                                     answer the "SiteRename" popup via
                                     its own button callback
                         EvaBoard -> KerbalEVA.BoardPart(part)
        |
        v  two-phase PENDING holds the FIFO head until:
             EvaExit:   EVA vessel live + active + settled (+ dwell)
             PlantFlag: gate opened -> plant fired -> flag vessel exists
                        + dialog answered + settled
             EvaBoard:  EVA vessel gone + crew aboard target + target is
                        active vessel + board-merge quiescent + settled
        |
        v  journal EXECUTED -> terminal response -> DONE
```

The M-C1 invariants carry over verbatim: pure deciders / thin applier,
at-most-once on the WAL's CLAIMED gate alone (KILLED is terminal, never
retried mid-save), refusal = driver-INVALID never PARSEK-FAIL, and the verb
reports the primitive while the verifier chain judges the recording.

## Stock ground truths (verified against the local KSP 1.12.5 install)

Behavioral facts the verb specs stand on, verified against the install this
harness drives; the implementation PR re-verifies before wiring.

1. **Exit.** `FlightEVA.fetch.spawnEVA(ProtoCrewMember pCrew, Part fromPart,
   Transform fromAirlock, bool tryAllHatches = false)` returns the spawned
   `KerbalEVA`, or **null** on refusal: kerbal not in the part's crew, no
   airlock transform, hatch obstructed (including by another kerbal standing
   on it), hatch inside a fairing, or a mod veto via `onAttemptEva`. On
   success it fires `GameEvents.onCrewOnEva(fromPart, kerbalPart)` +
   `onCrewTransferred` and starts a coroutine that switches the ACTIVE
   VESSEL to the new EVA kerbal a few frames later. The seam calls it with
   `tryAllHatches: true` (the stock UI's own shape); `FlightEVA.fetch` is a
   FLIGHT-scene singleton.
2. **Board.** `KerbalEVA.BoardPart(Part p)` is public and void. Gates, in
   order: the `CanBoard` game parameter, target crew capacity, EVA inventory
   stow, and a science-data check that can prompt when experiments are aboard
   the kerbal. It performs NO distance check - a distant call would
   teleport-board - so the seam imposes its own proximity precondition.
   Refusals surface only as screen messages plus an unchanged crew manifest,
   so the verb's effect confirmation is the crew-membership change itself.
   Success fires `onCrewBoardVessel(fromPart, toPart)` and destroys the EVA
   vessel; KSP switches focus to the boarded craft.
3. **Flag plant.** `KerbalEVA.PlantFlag()` is public but decrements the
   kerbal's `flagItems` BEFORE running the FSM event, and the event only fires
   from the grounded-idle FSM states. The honest precondition surface is the
   stock UI gate itself, `kerbalEVA.Events["PlantFlag"].active`, which the FSM
   keeps equal to `CanPlantFlag()` (active vessel + part ground contact +
   `flagItems > 0` + not ragdoll + astronaut-complex flag unlock + not in
   construction mode); checking it BEFORE calling `PlantFlag()` avoids leaking
   a flag item on a refused plant. The sequence then runs: heading acquire ->
   plant animation -> `FlagSite.CreateFlag` (fires `onFlagPlant`) -> on
   completion `FlagSite.OnPlacementComplete` opens the "SiteRename"
   `PopupDialog` -> `GameEvents.afterFlagPlanted(FlagSite)` fires from EITHER
   button's callback (accept, gated on a valid typed name, or dismiss).
   Parsek records the `FlagEvent` only in its `afterFlagPlanted` handler, so
   the dialog MUST be answered.
4. **Ladder state.** A pad/hatch exit leaves the kerbal ON the ladder
   (`KerbalEVA.OnALadder` true, no ground contact). `KerbalEVA.fsm` and
   `On_ladderLetGo` are public, so a headless release is a plain
   `fsm.RunEvent(...)` - no Harmony, no reflection.
5. **Second-EVA obstruction.** While a kerbal hangs on the only hatch, a
   second `spawnEVA` from the same part is refused (null return, no
   `onCrewOnEva`) - established by the `EvaTwiceFromSameCapsule` in-game
   test, which moves the first kerbal 12 m clear before the second exit.
   Sequential exit->board->exit avoids this; the missions are sequenced
   accordingly.

No Harmony patch and no guard bypass is needed anywhere in this batch: every
surface is public, and the one non-call interaction (the SiteRename dialog)
is answered by locating the live popup by name and invoking the button's own
callback, exactly as M-C1 prescribed for `AnswerMergeDialog` (the
deferred-field PopupDialog callback trap applies verbatim).

## Data Model

No persisted format changes. Three NEW implemented verb names (never in the
M-A2 reserved list, so this is additive like `SaveGame`, not a promotion):
`EvaExit`, `EvaBoard`, `PlantFlag`. Verb table moves 15 -> 18 implemented;
the 11 reserved names are untouched (`TestCommandVerbs.cs`). The hlib
companion changes (below, Behavior) land in the same PR or the harness
rejects the specs before KSP ever sees them (M-C1 precedent).

### DispatchState extension (`TestCommandDispatcher.cs`)

Three additive bits sampled per poll by `BuildDispatchState`:

```
bool ActiveVesselIsEva;     // FlightGlobals.ActiveVessel?.isEVA == true
bool StructuralSplitPending; // ParsekFlight.Instance reports a deferred
                             // split/branch in progress (new internal
                             // read-only accessor over pendingSplitInProgress)
bool FlightEvaPresent;       // FlightEVA.fetch != null
```

Existing dispatch rows are unaffected (new bits default false, unread by the
15 existing verbs).

### Per-verb precondition rows

| verb | VerbSceneRequirement | extra guards in DecideDispatch |
|---|---|---|
| `EvaExit` | `RequiresFlight` | defer `split-pending` while StructuralSplitPending; defer `flighteva-not-ready` while !FlightEvaPresent; refuse `load-in-flight` |
| `PlantFlag` | `RequiresFlight` | defer `not-eva` while !ActiveVesselIsEva (the preceding EvaExit's auto-switch may still be settling) |
| `EvaBoard` | `RequiresFlight` | defer `not-eva` while !ActiveVesselIsEva |

The finer preconditions (kerbal aboard, proximity, the stable flag lock)
need live object resolution and are executor-side refusals, mirroring how
`KscAction` resolves targets in the applier and refuses via the pure
decider. The stock PLANT GATE is deliberately NOT a dispatch precondition
or an executor refusal: it is a bounded WAIT inside PlantFlag's two-phase
execution (F1, Behavior below), because it is transiently false while the
kerbal lands.

### Pure deciders (new files `TestCommandEvaExit/PlantFlag/EvaBoard.cs`)

```
// TestCommandEvaExit
enum EvaExitCompletionDecision { StillWaiting, CompleteOk, ExitTimeout }
internal static string ResolveKerbalArg(string arg, IList<string> crewNames,
    out string error);
    // null/empty -> first crew member; named arg must match exactly
    // (InvariantCulture ordinal) else "kerbal-not-aboard".
internal static EvaExitCompletionDecision DecideEvaExitCompletion(
    double elapsed, bool evaVesselExists, bool evaVesselIsActive,
    bool sceneSettled, bool releaseRequested, bool releaseApplied,
    bool settleElapsed, double budget);
    // CompleteOk = exists AND active AND settled AND (!requested OR applied)
    // AND settleElapsed (the optional settleSeconds dwell, F7: lets Parsek's
    // deferred EVA auto-record arm before the next FIFO command).

// TestCommandPlantFlag
enum PlantGateDecision { KeepWaiting, ProceedToPlant, RejectStableLock,
                         GateTimeout }
internal static PlantGateDecision DecidePlantGateWait(
    double elapsed, bool gateOpen, bool stableLockClosed, double budget);
    // BOUNDED-WAIT pre-plant phase (F1): gateOpen samples
    // Events["PlantFlag"].active EVERY poll; a transiently-closed gate
    // (mid-fall, stumble, ragdoll recovery) is KeepWaiting, NEVER a terminal
    // reject. stableLockClosed = the AC flag unlock read directly
    // (GameVariables.UnlockedEVAFlags) - the one cause that cannot flip
    // mid-mission -> RejectStableLock. Budget expiry with the gate never
    // open -> GateTimeout.
enum FlagPlantCompletionDecision { StillWaiting, CompleteOk, FlagTimeout }
internal static FlagPlantCompletionDecision DecideFlagPlantCompletion(
    double elapsed, bool flagSiteVesselExists, bool dialogAnswered,
    bool sceneSettled, double budget);
    // CompleteOk = flag vessel exists AND dialogAnswered AND settled.
    // dialogAnswered is set by the applier WHEN it invokes the button -
    // never inferred from popup absence (a never-spawned popup would
    // false-OK otherwise).

// TestCommandEvaBoard
enum BoardCompletionDecision { StillWaiting, CompleteOk, BoardTimeout }
internal static bool IsWithinBoardRange(double distanceMeters, double bound);
    // default bound 10.0 m inclusive; the seam's honesty bound over the
    // stock API's missing distance check.
internal static BoardCompletionDecision DecideBoardCompletion(
    double elapsed, bool evaVesselGone, bool crewAboardTarget,
    bool targetIsActiveVessel, bool boardMergeQuiescent,
    bool sceneSettled, double budget);
    // CompleteOk = EVA vessel destroyed AND kerbal in target part crew AND
    // the TARGET is the active vessel AND Parsek's board-merge is quiescent
    // AND settled (F2, see EvaBoard Behavior). crewAboardTarget IS the
    // effect confirmation (BoardPart is void).
```

All three follow `TestCommandLoadGame.DecideLoadCompletion` ordering: positive
completion first, then budget, `StillWaiting` as the default.

## Behavior

### EvaExit (two-phase, irreversible)

**Args.** `kerbal=<name>` (optional; default = first crew member of the
active vessel), `release=<true|false>` (optional, default `false`; run the
ladder let-go after the spawn settles, so the kerbal drops to the ground for
a following `PlantFlag`), `settleSeconds=<float>` (optional, default `0`;
extra dwell held AFTER the base completion conjuncts before CompleteOk -
opt-in, keeps the default Parsek-agnostic; EVA-2 uses it so Parsek's
DEFERRED EVA AUTO-RECORD arms and samples before the next FIFO command
races it, F7). Spec authors write arg values RAW ("Valentina Kerman");
`run.py encode_value` performs the wire percent-encoding and escapes `%`
itself, so a pre-encoded value would double-encode and the verb would
refuse `kerbal-not-aboard`.

**Resolution + refusals (executor).** Refuse `no-crew` when the active
vessel has no crew, `kerbal-not-aboard` when the named kerbal does not
resolve (`ResolveKerbalArg`), `no-airlock` when the kerbal's part has no
airlock transform.

**Execution (CLAIMED side effect).** Call
`FlightEVA.fetch.spawnEVA(pcm, fromPart, airlock, tryAllHatches: true)`. A
null return is `REJECTED msg=eva-refused` - a stock refusal with NO side
effect (obstructed hatch / fairing / mod veto; stock's specific reason is
only a screen message, and its own line is in KSP.log), so it sits in the
no-side-effect REJECTED family like every other refusal. Non-null:
`PendingVerdict`, FIFO head held; the applier remembers `fromPart.vessel`'s
pid in an in-memory `lastEvaExitFromPid` (non-durable, cleared on process
restart) for `EvaBoard`'s default target (F9).

**Two-phase completion.** Poll `DecideEvaExitCompletion`: the spawned EVA
vessel exists, it is `FlightGlobals.ActiveVessel` (auto-switch completed),
scene settled, when `release=true` the release applied, and the
`settleSeconds` dwell (if any) elapsed. The applier performs the release
during polling: once the EVA vessel is active and `OnALadder` is true, run
`fsm.RunEvent(On_ladderLetGo)` once; a kerbal already off the ladder marks
releaseApplied without the event (logged). NOTE: release completion is
deliberately NOT gated on ground contact - the kerbal may complete mid-fall;
the ground-contact wait belongs to `PlantFlag`'s bounded-wait gate (F1),
which is the consumer that needs it. Terminal `OK` payload `kerbal=<name>
evaPid=<pid> released=<bool>`; budget expiry -> `ERROR
msg=eva-exit-timeout`.

**What completion deliberately does NOT assert.** Whether Parsek branched the
tree, started an auto-record, or did nothing (settings off) is NOT part of the
verb contract - that is the verifier chain's job over logs and the produced
save (M-C1 TimeJump precedent: the verb reports the primitive). One verb stays
valid across all three recording modes the missions exercise.

**At-most-once (shared by all three verbs).** Each side effect is
irreversible (crew moved, flag planted, vessel destroyed) and rides the
standard M-C1 WAL reasoning unchanged: the stock call fires only after
CLAIMED durably lands; a crash mid-verb dies before a terminal response, the
M-A5 watchdog kills the run, KILLED is terminal (never retried, never
re-staged), so the at-most-once property stands on the CLAIMED gate alone;
the conservative-INTERRUPTED path stays as the defensive contract for the
restart the v1 harness never performs. Not repeated per verb below.

### PlantFlag (two-phase, bounded-wait gate, irreversible, dialog-answering)

**Args.** none in v1 (the site keeps the stock default name; a `name=` arg
needs a private-field write into the dialog state and is deferred).

**Bounded-wait plant gate (F1).** The stock gate
(`Events["PlantFlag"].active`) is TRANSIENTLY false while the kerbal is
mid-fall / stumbling / ragdoll-recovering after an `EvaExit release=true` -
whose completion deliberately does not wait for ground contact - so a
single one-shot read would terminally REJECT a plant that succeeds two
seconds later (the EVA-1 near-deterministic failure). The verb therefore
treats the gate as a WAIT, not a precondition snapshot:
- Instant refusals, only for STABLY-closed causes: `not-eva` (no
  `KerbalEVA` on the active vessel) and `flag-lock-stable` when
  `GameVariables.UnlockedEVAFlags(AC level)` is false (the facility level
  cannot change mid-mission).
- Otherwise the verb returns `PendingVerdict` immediately and polls the
  gate through `DecidePlantGateWait` within its budget; a closed gate is
  `KeepWaiting`. On gate-open the applier calls `PlantFlag()` (the CLAIMED
  side effect fires exactly once, on that transition) and moves to the
  dialog phase. Budget expiry with the gate never open -> `ERROR
  msg=flag-gate-timeout`, `msg` carrying the last observed gate state. A
  zero-flag-items kerbal is not headlessly distinguishable (the counter is
  not public) and rides the bounded wait to `flag-gate-timeout`
  (documented). Because `PlantFlag()` is only called on an OPEN gate, no
  flag item is ever leaked on a refusal path (stock decrements first,
  ground truth 3).

**Dialog phase (after the gate opened and `PlantFlag()` ran).** The FSM
runs heading acquire + animation (seconds), spawns the `FlagSite` vessel,
opens the "SiteRename" popup. The applier polls: when a live `PopupDialog`
named "SiteRename" exists, invoke the DISMISS button's own callback
(deterministic default site name; accept is gated on a typed-in name the
seam does not provide) and set dialogAnswered. `afterFlagPlanted` fires
inside that callback, synchronously in the command frame - exactly when
`OnAfterFlagPlanted` captures the `FlagEvent` (`ParsekFlight.cs:10839`).
CompleteOk once flag vessel exists + dialogAnswered + settled; terminal
`OK` payload `flagSite=<vesselName> body=<body> lat=<lat> lon=<lon>`.
Budget expiry after the plant fired (animation never completed or dialog
never spawned) -> `ERROR msg=flag-timeout` (the decremented flag item is
lost; documented, not recovered).

### EvaBoard (two-phase, irreversible)

**Args.** `targetPid=<vesselPersistentId>` (optional; default = the addon's
in-memory `lastEvaExitFromPid` from the preceding `EvaExit` if that vessel
is still loaded - non-durable, cleared on process restart, F9 - else the
nearest loaded non-EVA vessel).

**Resolution + refusals (executor).** Refuse `not-eva` (active vessel not an
EVA kerbal), `unknown-target` (`targetPid` not a loaded vessel),
`no-boardable-part` (no part with free crew capacity and an airlock),
`target-full` (every crewable part at capacity), and `not-near-target`
(kerbal-to-part distance over the `IsWithinBoardRange` bound, default 10 m -
the seam's honesty bound, since stock `BoardPart` would teleport).

**Execution.** Call `evaCtl.BoardPart(part)`; `PendingVerdict`. `BoardPart`
is void and refuses via screen message only, so nothing is concluded from
the call itself.

**Two-phase completion (effect confirmation + board-merge quiescence, F2).**
Poll `DecideBoardCompletion` over FIVE conjuncts: EVA vessel gone, the
kerbal's name in the target part's `protoModuleCrew`, the TARGET is
`FlightGlobals.ActiveVessel` (KSP's post-board focus switch completed),
Parsek's board-merge is QUIESCENT, and scene settled. The quiescence
conjunct exists because crew-aboard + vessel-gone is true BEFORE
`OnVesselSwitchComplete` sets `ChainToVesselPending`
(`ParsekFlight.cs:3388-3399`, the ONLY emitter of "detected boarding from
EVA") and before `HandleTreeBoardMerge` (`ParsekFlight.cs:11878`) runs in
`Update()`; a next FIFO command landing in that window corrupts the merge
(a `StopRecording` kills the merge tokens; EVA-3's second `EvaExit`
mis-routes through auto-record while `IsRecording` is momentarily false).
`StructuralSplitPending` does NOT cover this window. The applier samples
quiescence via a second internal read-only accessor on `ParsekFlight`
(same pattern as the pending-split bit): quiescent =
`!(recorder?.ChainToVesselPending ?? false) && pendingBoardingTargetPid == 0`.
On a no-tree run Parsek never arms either token (`OnCrewBoardVessel`
returns early with no chain/tree), so the conjunct reads true and is
inert. Terminal `OK` payload `kerbal=<name> boardedPid=<pid>`. If the crew
manifest never changes (stock refused: `CanBoard` off, capacity raced away,
science-data prompt pending) the budget converts to `ERROR
msg=board-timeout` - never a false OK on a silently refused board (the
M-C1 blocked-committed doctrine applied to a void API).

### Budgets, hlib companions, harness classification

The C# seam has ONE per-verb budget: `DeferralBudget.BudgetSeconds` governs
BOTH the head-deferral wait (`ParsekTestCommandAddon.cs:937`) AND the
two-phase completion wait (`TryCompleteTwoPhase`,
`ParsekTestCommandAddon.cs:775`). M-C2 therefore declares ONE per-verb
`DeferralBudget` constant each, sized to cover the verb's worst case of
head-defer PLUS completion (no new completion-budget seam is introduced):

| verb | DeferralBudget | sizing rationale |
|---|---|---|
| `EvaExit` | 120 s | dispatch defer (split-pending / scene settle) + spawn + auto-switch + release + optional settleSeconds dwell |
| `PlantFlag` | 180 s | not-eva defer + the F1 bounded-wait plant gate (landing settle) + heading acquire + animation + dialog answer |
| `EvaBoard` | 120 s | not-eva defer + board + vessel teardown + focus switch + board-merge quiescence + settle |

hlib companion changes (SAME PR, or the harness misclassifies before KSP
ever sees a command):

1. Move the three names into `IMPLEMENTED_SEAM_VERBS` (mirror of the C#
   table).
2. Add `DISPATCH_DEFERRAL_BUDGET_SECONDS` entries `EvaExit: 120`,
   `PlantFlag: 180`, `EvaBoard: 120` (F5). Without them the harness
   step-wait uses the 60 s `DISPATCH_DEFERRAL_DEFAULT_SECONDS` + margin and
   would stop out-waiting PlantFlag at ~120 s while the seam's own verdict
   lands at up to 180 s - converting a retryable seam TIMEOUT into a
   terminal KILLED (budget watchdog kill). None is a `DEFERRED_SEAM_VERB`
   (all well under the 540 s cap); they ride the per-verb dict like
   `AnswerMergeDialog` / `KscAction`.
3. Teach `hlib.spec_expects_live_recording` that a `SetSetting
   autoRecordOnEva=true` step implies live recording (F6): today it returns
   True only for `StartRecording` / `autoRecordOnLaunch=true`, so EVA-2 - a
   genuinely-recording run - would have its REC-001/REC-003 marker rules
   SUPPRESSED (oracle invariant 5 would be silently false).

Harness classification follows M-C1 verbatim: EVERY refusal or timeout
above (`no-crew`, `kerbal-not-aboard`, `no-airlock`, `eva-refused`,
`eva-exit-timeout`, `not-eva`, `flag-lock-stable`, `flag-gate-timeout`,
`flag-timeout`, `unknown-target`, `no-boardable-part`, `target-full`,
`not-near-target`, `board-timeout`) is driver-INVALID retry-once (proposed
subkind `driver-eva`); a verb `OK` whose produced save reds the verifier
chain is PARSEK-FAIL, orthogonal. The subkind spelling is a harness wiring
detail; an `expect = "OK"` mismatch already classifies
retry-once-then-INVALID today.

## Driver architecture decision

**Decision: the EVA missions are SEAM-step-driven (`kind = "seam"`), not
mlib mission machines, not hybrid.**

1. **kRPC cannot participate.** No EVA spawn, board, or flag surface; EVA
   locomotion control is effectively absent (A4 HARD). An autopilot driver
   would contribute only subprocess overhead.
2. **EVA interactions are discrete, not closed-loop.** mlib machines exist
   for telemetry-feedback flight phases; every EVA step is a discrete stock
   invocation plus a settle wait - exactly the seam's two-phase verb shape.
   No jetpack flying is attempted in v1, so there is no loop to close.
3. **Interleaving is already solved.** Strict FIFO + the verbs' own
   completion deciders hold the head through every wait; no wait-step or
   telemetry poll is needed. `MissionMark` gives UT-stamped grep anchors.
4. **Flight-free fixtures keep it cheap.** Pre-placed pad/orbit fixtures
   mean no ascent, no venv, no MechJeb - daily-tier cost, B10-style.

Fallback: only if the EVA-2 orbital fixture proves un-committable does EVA-2
become the one hybrid (`kind = "autopilot"`, the proven `b2_lko_ascent`
mission to reach LKO, then the EVA verb tail).

## Mission specs

Shared conventions: `instanceProfile = "stock-minimal"`,
`injectedRecordings = "none"`, `autoRecordOnLaunch=false` +
`verboseLogging=true` pinned after LoadGame, analyzer Forbid as always,
sandbox fixtures (no career gates on EVA/flags; ledger oracle inert by
design - oracle section). Specs carry arg values RAW (`"Valentina Kerman"`,
`"eva1 exit"`): `run.py encode_value` performs the wire percent-encoding
and escapes `%` itself (`harness/run.py:117`), so a spec-side pre-encoded
value ("Valentina%20Kerman") would DOUBLE-ENCODE on the wire and the verb
would refuse it (F3). REQUIRED-TOKEN LEVEL DEPENDENCY (F8): several
required log tokens below route through `ParsekFlight.Log`, which is
`ParsekLog.Verbose("Flight", ...)` (`ParsekFlight.cs:26240`) - specifically
"Mid-recording EVA detected", "Tree board merge completed", and
"Auto-record started" are VERBOSE lines, and the `[Pipeline-Smoothing]`
structural-snapshot lines are Verbose too; they exist in KSP.log ONLY under
the `verboseLogging=true` pin, which is therefore load-bearing for these
specs, not a nicety. "Tree branch created: type=EVA", "detected boarding
from EVA", and "Flag event captured" are `ParsekLog.Info` and do not depend
on the pin.

### EVA-1: ground EVA + flag + board (single kerbal, pad)

Covers block B3 / S0.2. Fixture: `fixtures/saves/gloops-airshow` - already
committed, and the repo copy confirms it fits: the active vessel is a
single `mk1pod.v2` crewed with Jebediah Kerman, PRELAUNCH on the LaunchPad
(`harness/fixtures/saves/gloops-airshow/persistent.sfs`). No new fixture
needed (F11).

```toml
[driver]
kind  = "seam"
steps = [
  { cmd = "LoadGame",     args = { save = "${runSave}", name = "persistent" }, expect = "OK", budget = 300 },
  { cmd = "SetSetting",   args = { name = "autoRecordOnLaunch", value = "false" }, expect = "OK" },
  { cmd = "SetSetting",   args = { name = "verboseLogging", value = "true" }, expect = "OK" },
  { cmd = "StartRecording",                                             expect = "OK" },
  { cmd = "MissionMark",  args = { label = "eva1 exit" },               expect = "OK" },
  { cmd = "EvaExit",      args = { release = "true" },                  expect = "OK", budget = 120 },
  { cmd = "PlantFlag",                                                  expect = "OK", budget = 180 },
  { cmd = "EvaBoard",                                                   expect = "OK", budget = 120 },
  { cmd = "StopRecording",                                              expect = "OK" },
  { cmd = "CommitTree",                                                 expect = "OK" },
  { cmd = "FlushAndQuit",                                               expect = "OK" },
]
```

Phase flow: recorder live on the pod -> `EvaExit` triggers the FOREGROUND
EVA branch (structural snapshot at the EVA UT, recorder stop, deferred tree
branch, recording follows the kerbal, pod parks in `BackgroundMap`) ->
ladder release drops the kerbal to the pad -> `PlantFlag` (FlagEvent
captured into the foreground recorder, now the kerbal's) -> `EvaBoard`
(board merge chains back to the pod with a Board branch point) -> stop,
commit.

Expectations:

```toml
[dimensionsCovered]
D2  = ["structural-event-snapshots"]    # EVA-UT snapshot on the pod recording
D5  = ["eva-branch"]
D7  = ["flag-plant"]
D14 = ["kerbin", "sandbox", "scene-flight"]

[expectations.recordings]
# Pod + EVA branch recordings are certain (min 2); whether the board merge
# adds a chain-segment recording and whether the stationary pod survives the
# sub-2-point drop are pinned by the first live run (P3).
count = { min = 2, max = 4 }

[expectations.logContracts]
# "Mid-recording EVA detected" / "Tree board merge completed" /
# "[Pipeline-Smoothing]" are VERBOSE (ParsekFlight.Log / snapshot lines) -
# they require the verboseLogging=true pin above. The [Pipeline-Smoothing]
# token is what makes the D2 structural-event-snapshots claim ASSERTED
# rather than nominal (exact message pinned at implementation).
required  = ["Recording started", "Mid-recording EVA detected",
             "Tree branch created: type=EVA", "[Pipeline-Smoothing]",
             "Flag event captured", "detected boarding from EVA",
             "Tree board merge completed", "committree committed=true"]
forbidden = ["\\[Parsek\\]\\[ERROR\\]"]

[runtime]
# Budget arithmetic (S0.6 pattern): 840 >= 300 LoadGame + 120 EvaExit +
# 180 PlantFlag + 120 EvaBoard + fast verbs (SetSetting/Start/Stop/Commit/
# MissionMark/FlushAndQuit, seconds each) + 60 margin. LoadGame is the only
# DEFERRED_SEAM_VERB step; the EVA verbs ride their per-verb dispatch
# budgets (120/180/120), each under the 540 cap.
budgetSeconds = 840
```

Tier: daily once live-proven (flight-free), nightly until the operator run
pins the windows.

### EVA-2: orbital EVA + re-board (auto-record-on-EVA path)

Covers the orbital variant of B3 / A4-trivial. Fixture: NEW
`fixtures/saves/eva2-lko-crewed` - a crewed pod in stable ~80 km LKO,
sandbox (operator commits once; a manual ascent-and-quicksave or a set-orbit
edit both work, since the fixture is a START state, not a recording). The
stock "EVA in Kerbin Orbit" scenario save was considered and rejected:
scenario-mode saves carry game restrictions the harness has never run under.
Fallback if un-committable: the autopilot hybrid (driver decision above).

Deliberate contrast with EVA-1: NO StartRecording; `autoRecordOnEva=true`
(whitelisted, M-A2) so the exit exercises the DEFERRED EVA AUTO-RECORD path
(`ShouldQueueAutoRecordOnEva` -> recorder starts ON the EVA kerbal once
active) - the `D1 auto-record-eva` cell. The kerbal stays on the ladder (no
`release`), so no drift and no jetpack need; `EvaBoard` re-boards at ~0 m.
The deferred auto-record is a RACE against the next command (F7):
`HandleDeferredAutoRecordEva` starts the recorder a frame or more AFTER the
exit settles, and a same-frame `BoardPart` could end the flight before (or
frames after) the recorder arms, yielding a degenerate sub-2-point capture.
EVA-2 therefore sets `settleSeconds = "10"` on `EvaExit`: the verb holds
the FIFO head an extra 10 s after its base conjuncts, guaranteeing the
recorder is live and has sampled before `EvaBoard` fires. The dwell is the
mission's dwell, not the verb's default (opt-in keeps EvaExit
Parsek-agnostic).

```toml
steps = [
  { cmd = "LoadGame",     args = { save = "${runSave}", name = "persistent" }, expect = "OK", budget = 300 },
  { cmd = "SetSetting",   args = { name = "autoRecordOnLaunch", value = "false" }, expect = "OK" },
  { cmd = "SetSetting",   args = { name = "autoRecordOnEva", value = "true" }, expect = "OK" },
  { cmd = "SetSetting",   args = { name = "verboseLogging", value = "true" }, expect = "OK" },
  { cmd = "EvaExit",      args = { settleSeconds = "10" },               expect = "OK", budget = 120 },
  { cmd = "EvaBoard",                                                    expect = "OK", budget = 120 },
  { cmd = "StopRecording",                                               expect = "OK" },
  { cmd = "CommitTree",                                                  expect = "OK" },
  { cmd = "FlushAndQuit",                                                expect = "OK" },
]

[dimensionsCovered]
D1  = ["auto-record-eva"]
D5  = []            # single-kerbal auto-record tree; eva-branch claimed by EVA-1/3
# D14 "situation" deliberately NOT cited: nothing in this spec asserts the
# orbital situation (no orbit-parameter expectation); kerbin/sandbox/
# scene-flight are carried by the fixture + LoadGame completion.
D14 = ["kerbin", "sandbox", "scene-flight"]

[expectations.recordings]
count = { min = 1, max = 3 }   # EVA recording certain; board chain-back pinned by P3

[expectations.logContracts]
# "Auto-record started" is VERBOSE (ParsekFlight.Log) - requires the
# verboseLogging pin; "detected boarding from EVA" is Info.
required  = ["Auto-record started", "detected boarding from EVA",
             "committree committed=true"]
forbidden = ["\\[Parsek\\]\\[ERROR\\]"]

[runtime]
# 780 >= 300 LoadGame + 120 EvaExit (incl. the 10 s dwell) + 120 EvaBoard +
# fast verbs + 60 margin (S0.6 pattern).
budgetSeconds = 780
```

Same-PR hlib dependency: `spec_expects_live_recording` must learn the
`autoRecordOnEva=true` pin (F6) or this genuinely-recording run gets its
REC marker rules suppressed. P4 pins the exact auto-record wording in the
orbital case (the known line is "Auto-record started (EVA from pad)"; the
orbital suffix is unverified, so the required token stays the stable
prefix). Daily once proven.

### EVA-3: multi-kerbal sequential EVA (3-crew pod, pad)

Covers the multi-crew axis: two kerbals of a 3-crew pod exit and re-board
SEQUENTIALLY within one tree - two EVA branch points, two board merges, and
multi-crew Start/End crew manifests on the pod recording. Sequencing is
forced by stock ground truth 5 (a kerbal on the hatch blocks the next exit);
exit A -> board A -> exit B -> board B never hits the obstruction.
Simultaneous multi-EVA needs a focus-switch verb and is deferred.

Fixture: NEW `fixtures/saves/eva3-pad-3crew` - a BARE Mk1-3 pod (3 named
crew) on the pad, sandbox. The Kerbal X's pod also holds 3 but sits ~20 m up
a full stack (ladder release lethal, pad boarding unreachable); the bare pod
gives the same 3-crew pod at ground level. No flag in EVA-3 (D7 claimed by
EVA-1); `release` stays false, board from the ladder.

```toml
steps = [
  { cmd = "LoadGame",     args = { save = "${runSave}", name = "persistent" }, expect = "OK", budget = 300 },
  { cmd = "SetSetting",   args = { name = "autoRecordOnLaunch", value = "false" }, expect = "OK" },
  { cmd = "SetSetting",   args = { name = "verboseLogging", value = "true" }, expect = "OK" },
  { cmd = "StartRecording",                                              expect = "OK" },
  { cmd = "EvaExit",      args = { kerbal = "Valentina Kerman" },        expect = "OK", budget = 120 },
  { cmd = "EvaBoard",                                                    expect = "OK", budget = 120 },
  { cmd = "EvaExit",      args = { kerbal = "Bob Kerman" },              expect = "OK", budget = 120 },
  { cmd = "EvaBoard",                                                    expect = "OK", budget = 120 },
  { cmd = "StopRecording",                                               expect = "OK" },
  { cmd = "CommitTree",                                                  expect = "OK" },
  { cmd = "FlushAndQuit",                                                expect = "OK" },
]

[dimensionsCovered]
D2  = ["structural-event-snapshots"]   # asserted by the [Pipeline-Smoothing] token
D5  = ["eva-branch"]
D14 = ["kerbin", "sandbox", "scene-flight"]

[expectations.recordings]
count = { min = 3, max = 6 }   # pod + 2 EVA branches certain; chain-backs pinned by P3

[expectations.logContracts]
# Verbose tokens (need the verboseLogging pin): "Tree board merge
# completed", "[Pipeline-Smoothing]". Info: the rest.
required  = ["Tree branch created: type=EVA", "[Pipeline-Smoothing]",
             "detected boarding from EVA", "Tree board merge completed",
             "committree committed=true"]
forbidden = ["\\[Parsek\\]\\[ERROR\\]", "dropping recorder data"]

[runtime]
# 960 >= 300 LoadGame + 4 x 120 EVA verbs + fast verbs + 60 margin
# (S0.6 pattern).
budgetSeconds = 960
```

The `dropping recorder data` forbidden token is lifted from the
`EvaTwiceFromSameCapsule` assertion (no recorder data dropped across
repeated branch setup). The kerbal args are RAW (F3); the fixture bakes the
named crew. The second `EvaExit` is exactly the command the F2 quiescence
conjunct protects: without it, a board-merge still in flight would leave
`IsRecording` momentarily false and mis-route the exit through auto-record.
Nightly at first, daily candidate after flake data.

### Coverage cells and named registry gaps

Cited (all exist in `harness/coverage/registry.toml`): `D1 auto-record-eva`,
`D2 structural-event-snapshots` (asserted via the `[Pipeline-Smoothing]`
required token), `D5 eva-branch`, `D7 flag-plant`,
`D14 kerbin / sandbox / scene-flight`.

Named gaps for registry growth (add in the SAME PR as the mission that first
asserts them, per the growth rule; none is invented preemptively here):

- **D5 `eva-branch` is one value over two code routes** (foreground vs
  background-parent). If a future scenario claims the bg-parent route
  (needs the focus-switch verb), split the token in that PR; until then the
  in-game `EvaTwiceFromSameCapsule` test is the only bg-parent coverage.
- **No D12 token for recording crew manifests** (`StartCrew` / `EndCrew` /
  `CrewEndStates`). If EVA-3's oracle is upgraded to an analyzer crew
  invariant (below), a `D12 crew-manifest` value is added in that PR.
- **No token distinguishing flag RECORD from flag PLAYBACK**
  (`ApplyFlagEvents` ghost-side spawning). These missions assert recording
  only; a playback-side flag scenario decides the split in its own PR.

## Recording-correctness oracle

What proves the EVA was RECORDED correctly - not merely that the flight
happened. Observable invariants, strongest first, each mapped to the v1
mechanism that checks it and (where v1 is log-shaped) the named deeper
follow-up:

1. **Tree topology.** A committed EVA tree must contain an EVA branch point
   at the exit UT, a child recording whose `EvaCrewName` (`Recording.cs:176`)
   equals the exited kerbal, and a Board merge back. v1: the recordings
   count window + the required Info lines (`Tree branch created: type=EVA`,
   `Tree board merge completed`). Named follow-up: an analyzer invariant
   "every recording with `EvaCrewName` set descends from an EVA branch
   point whose UT lies inside the parent's span" - a normal rule PR that
   upgrades the topology check from log-shaped to structural.
2. **FlagEvent fidelity.** Exactly one `FlagEvent` with `placedBy` = the
   kerbal, `bodyName` = Kerbin, lat/lon inside a pad box, captured into the
   recording that OWNS the planter (`ShouldRecordFlagEvent`,
   `ParsekFlight.cs:10939`). v1: the required `Flag event captured` Info
   line, which carries owner, planter, coordinates, and body, including the
   "(foreground recorder" discriminator. Follow-up: analyzer
   FlagEvents-vs-span containment + UT-sortedness (#287 contract).
3. **Structural snapshot at the EVA UT** on the pod recording
   (`AppendStructuralEventSnapshot`, `ParsekFlight.cs:7859/:7885`). v1: the
   `[Pipeline-Smoothing]` Verbose snapshot token is a REQUIRED logContract
   line in EVA-1/EVA-3 (exact message pinned at implementation), which is
   what makes the `D2 structural-event-snapshots` claim asserted; it exists
   only under the `verboseLogging=true` pin (F8).
4. **Crew conservation.** Everyone re-boarded means pod `EndCrew` trait
   counts equal `StartCrew`, and the EVA recording's `StartCrew` is exactly
   one kerbal. v1: the Verbose "captured N start crew trait(s)" lines
   (`FlightRecorder.cs:6535/:6575`) - weak but real. Follow-up: the
   `D12 crew-manifest` analyzer invariant named above.
5. **Recording-rules pairing.** Live recording declared, so the REC marker
   rules stay UNSUPPRESSED (S0.5/S0.6 precedent). For EVA-2 this REQUIRES
   the same-PR hlib change (F6): `spec_expects_live_recording` today keys
   only on `StartRecording` / `autoRecordOnLaunch=true`, so without the
   `autoRecordOnEva=true` clause EVA-2's REC rules would be suppressed on a
   genuinely-recording run and this invariant would be silently false.
6. **No error floor.** Forbidden `[Parsek][ERROR]` plus (EVA-3) `dropping
   recorder data`: a branch handoff that loses recorder data reds.
7. **Analyzer Forbid pass.** RED=0 over every produced sidecar - the EVA
   kerbal recording must satisfy every existing invariant. The strongest
   existing structural check; runs unchanged.
8. **Ledger orthogonality (deliberately inert).** Sandbox fixtures have no
   career facets, so the ledger oracle contributes nothing BY DESIGN; the
   career EVA family (EVA science, flag milestones) is a different lineage
   (B10/L1), deferred. `D8 kerbals` is deliberately NOT cited.

## Edge Cases

All v1. Scenario -> expected behavior:

1. **EvaExit arg refusals** - uncrewed vessel -> `no-crew`; kerbal not
   aboard -> `kerbal-not-aboard`; part without airlock -> `no-airlock`.
2. **EvaExit refused by stock** (hatch obstructed - including a kerbal
   hanging on it from a mis-sequenced simultaneous attempt - fairing, mod
   veto) -> null return -> `REJECTED msg=eva-refused` (no side effect ran,
   so it rides the REJECTED family, F10); stock's reason line is in
   KSP.log. The sequential mission shapes never hit the obstruction.
3. **EvaExit while a Parsek split is pending** -> Defer `split-pending`,
   preventing the `OnCrewOnEva` skip path (`ParsekFlight.cs:7844`) from
   silently swallowing the branch.
4. **Auto-switch never completes** -> `ERROR msg=eva-exit-timeout` (the
   `EvaTwiceFromSameCapsule` Skip case).
5. **release=true but not on a ladder** -> release marked applied without
   the FSM event (logged `release=noop`); completion proceeds.
6. **PlantFlag, active vessel not EVA** -> Defer `not-eva` (auto-switch
   settling), then TIMEOUT if mis-sequenced; never wedges.
7. **PlantFlag gate transiently closed** (kerbal mid-fall after the
   release, stumble, ragdoll recovery) -> the F1 bounded wait: PENDING,
   gate re-polled every frame, plant fires on the open transition. This is
   the EVA-1 common case, NOT an error; consistent with risk R-D (the same
   wait absorbs a bouncing landing).
8. **PlantFlag gate stably closed** -> `REJECTED msg=not-eva` /
   `flag-lock-stable` (AC flag lock, read directly) refuse instantly; a
   zero-flag-items kerbal is indistinguishable headless and exhausts the
   bounded wait -> `ERROR msg=flag-gate-timeout` carrying the last gate
   state. No flag item ever leaks (the stock call fires only on an open
   gate).
9. **Plant FSM stalls after the plant fired** (heading acquire, ragdoll
   mid-animation) -> no dialog -> `ERROR msg=flag-timeout`; the flag item
   is lost (stock behaves identically on an interrupted plant).
10. **SiteRename answered externally mid-run** -> flag vessel exists, popup
    gone without the applier's invoke -> treated as answered (logged
    `dialog-answered-externally`); unreachable unattended.
11. **EvaBoard target full** -> pre-refused `target-full`; a capacity race
    surfaces as stock's silent refusal -> crew unchanged ->
    `ERROR msg=board-timeout`, never a false OK.
12. **EvaBoard too far** -> `REJECTED msg=not-near-target` (10 m bound; no
    move verb exists in v1).
13. **EvaBoard crew-aboard but merge still in flight** (the F2 window:
    crew moved, EVA vessel gone, but `OnVesselSwitchComplete` /
    `HandleTreeBoardMerge` have not run) -> the targetIsActiveVessel +
    board-merge-quiescence conjuncts hold `StillWaiting`; the next FIFO
    command can never land inside the merge window.
14. **EvaBoard with science data aboard** -> stock may prompt; v1 fixtures
    are science-free so unreachable; a data-carrying fixture would surface
    it as `board-timeout` (prompt-answering deferred).
15. **Kerbal dies** (fall from a bad fixture) -> `ERROR board-timeout`; the
    death is in the save for the verifier chain; the fixture hatch-height
    requirement (P5) exists for this. Driver-INVALID (fixture fault).
16. **Crash mid-verb** -> no terminal response -> M-A5 watchdog -> KILLED,
    terminal, never re-driven; at-most-once on the WAL CLAIMED gate alone.
17. **Pre-M-C2 addon receives the verbs** -> `REJECTED msg=unknown-command`
    (never-reserved names); capability probing as with `SaveGame`.

## What Doesn't Change

- No recording format, schema generation, sidecar, tree, ledger, or save
  field changes; `FlagEvent`, `BranchPoint`, `EvaCrewName`, StartCrew /
  EndCrew are consumed as-is.
- No wire-protocol change: new verb names + verb-specific args, all within
  the M-A2 envelope.
- No gameplay behavior in normal play: env-gated addon; the verbs call the
  SAME public stock entry points a player's hatch click / B key /
  plant-flag click uses, so a seam-driven EVA is byte-identical to a
  hand-driven one for every Parsek observer.
- No new Harmony patches, guard bypasses, or GameEvents subscriptions; the
  only source touches outside `TestCommands/` are TWO internal read-only
  accessors on `ParsekFlight`: the pending-split bit
  (`DispatchState.StructuralSplitPending`) and the board-merge-quiescence
  bit (`ChainToVesselPending` / `pendingBoardingTargetPid`, F2).
- `ParsekFlight`'s EVA / board / flag handlers are unchanged; the M-A2
  pump, journal, budgets, and two-phase machinery are reused unchanged.

## Backward Compatibility

- Channel files are ephemeral test artifacts; no save migration concern.
- The three names were never reserved, so older addons reject them
  `unknown-command`; capability probing distinguishes tiers exactly as with
  `SaveGame` (M-C1.1 precedent). The hlib verb-table move ships in the same
  PR. New args are additive (unknown-key-ignore).
- No existing recordings, saves, or settings are read or rewritten beyond
  the live state the three real stock actions already mutate.

## Diagnostic Logging

Subsystem tag `TestCommands`, standard format, InvariantCulture, every
dispatch branch and outcome logged (house requirement); the underlying
actions also log under their own tags (`Flight`, `Recorder`), so KSP.log
shows both layers. Per verb: Info start line with args
("evaexit start kerbal=<name> fromPid=<pid> release=<bool>", "plantflag
start kerbal=<name>", "evaboard start kerbal=<name> targetPid=<pid>
dist=<m>"), Info progress lines ("evaexit release applied" /
"release=noop", rate-limited "plantflag gate wait" while the F1 bounded
wait polls, "plantflag gate open - planting", "plantflag dialog answered
site=<name>"), Info complete line with the OK payload, Warn refused line
with the typed reason (`not-eva`, `flag-lock-stable`, `eva-refused`, the
board refusals), Error failed line with the timeout reason
(`eva-exit-timeout` / `flag-gate-timeout` / `flag-timeout` /
`board-timeout`) and elapsed seconds. Dispatch decisions reuse the M-A2
lines, so `split-pending` / `not-eva` / `flighteva-not-ready` appear in the
standard "dispatch id=<id> -> DEFER reason=<...>" shape.

## Test Plan

Pure core xUnit-covered without Unity; Unity bodies via in-game tests plus a
PENDING-OPERATOR runbook (an agent cannot pilot KSP).

Pure unit tests (each names the regression it catches):

- **Verb-table move.** Implemented for the three names; counts 18/11. Fails
  if a name is mis-bucketed.
- **Dispatch rows.** EvaExit outside FLIGHT / split-pending -> Defer;
  PlantFlag / EvaBoard with !ActiveVesselIsEva -> Defer(not-eva); ready ->
  Execute. Fails if a verb fires in an unsafe state.
- **ResolveKerbalArg.** Default-first-crew; exact match; unknown errors.
  Fails if a typo silently EVAs the wrong kerbal.
- **DecideEvaExitCompletion.** Every conjunct gates (missing / not active /
  unsettled / release pending / settle dwell remaining -> StillWaiting);
  budget -> ExitTimeout. Fails if the head advances before the auto-switch
  or before the F7 dwell has elapsed.
- **DecidePlantGateWait (F1).** Gate closed + not stably locked ->
  KeepWaiting (NEVER a terminal reject: the mid-fall regression); gate open
  -> ProceedToPlant exactly once; stableLockClosed -> RejectStableLock;
  budget expiry gate-never-open -> GateTimeout. Fails if a transiently
  closed gate terminally rejects (the EVA-1 near-deterministic failure) or
  a stably locked plant waits the full budget pointlessly.
- **DecideFlagPlantCompletion.** dialogAnswered=false never CompleteOk even
  with the flag vessel present (the false-OK-over-unanswered-dialog guard).
- **IsWithinBoardRange + DecideBoardCompletion (F2).** Inclusive bound;
  crew unchanged -> never CompleteOk (silent-stock-refusal guard); EVA
  vessel gone but crew absent -> never OK (a lost kerbal is never reported
  boarded); crew aboard but target NOT active vessel -> StillWaiting; crew
  aboard + active but board-merge NOT quiescent -> StillWaiting. Fails if
  the head advances inside the board-merge window (the StopRecording /
  second-EvaExit mis-route regressions).
- **Journal at-most-once.** Each verb id at CLAIMED -> Interrupted; DONE ->
  skip. Fails if a crash mid-EVA re-fires a spawn.
- **Response formatter stability + budget rows.** Payload keys
  percent-encoded, InvariantCulture; the three completion budgets resolve.

Log-assertion tests (ParsekLog.TestSinkForTesting): every start / refuse /
complete / timeout line is emitted with id and reason; no silent branch.

In-game tests (live KSP; PENDING-OPERATOR for the full sequences):

- Seam-channel `EvaExit release=true -> PlantFlag -> EvaBoard` on a crewed
  pad pod drives the full EVA-1 verb tail; assert OK payloads, branch line,
  flag capture line, board merge line. Reuses the existing runtime-test
  helpers (`WaitForEvaBranchCount`, hatch-obstruction guard).
- `PlantFlag` issued while the kerbal still hangs on the ladder holds
  PENDING (bounded gate wait), fires the plant after a release lands the
  kerbal, and never decrements a flag item while the gate is closed;
  `EvaBoard` against a full pod returns `target-full` without invoking
  `BoardPart`.
- With `PARSEK_TEST_COMMANDS` unset the three verbs are unreachable (inert
  default, M-A2 gate test extended).

## Risks, unknowns, live-prove list, budgets, fixtures

Risks / unknowns:

- **R-A: dialog-name coupling.** "SiteRename" is stock's popup name; a KSP
  patch could rename it. Lookup failure surfaces as `flag-timeout` with the
  popup scan logged; single constant; KSP 1.12.x is frozen, so low risk.
- **R-B: FSM timing variance.** Plant animation + heading acquire are
  frame-rate/terrain dependent; the 120 s budget is generous, flake data
  may tighten.
- **R-C: recordings-count windows are provisional.** Board-merge chain-back
  recordings yes/no and pod sub-2-point survival are pinned by the first
  live runs (B2 WATCH-4 precedent: window over guess).
- **R-D: ladder-release ground contact.** A bouncing/ragdolled landing
  keeps the plant gate closed for a while; the F1 bounded-wait gate ABSORBS
  it (KeepWaiting until the kerbal settles), so the failure mode is only a
  `flag-gate-timeout` on a landing that never stabilizes within 180 s;
  low-hatch fixtures minimize it. (Consistent with edge case 7 by
  construction: both describe the same wait.)

Live-prove list (operator, one KSP session, in order):

- P1: load-and-focus sanity check of `gloops-airshow` only - the repo copy
  already answers the crewing question (mk1pod.v2, crew = Jebediah Kerman,
  PRELAUNCH on LaunchPad, F11); no new EVA-1 fixture expected.
- P2: commit `eva2-lko-crewed` (crewed pod, ~80 km LKO, sandbox) and
  `eva3-pad-3crew` (bare Mk1-3 pod, 3 named crew, pad, sandbox).
- P3: first live EVA-1/2/3 runs pin the recordings-count windows and
  tighten `count` in the same PR.
- P4: pin the orbital auto-record log wording for EVA-2's required token.
- P5: confirm ladder release at the fixture hatch heights lands with ground
  contact and zero kerbal damage.
- P6: confirm the SiteRename button-callback invocation fires
  `afterFlagPlanted` and Parsek's capture line appears (the dialog-answer
  mechanism's one live proof, mirroring the AnswerMergeDialog proof).

Estimated budgets: EVA-1 840 s, EVA-2 780 s, EVA-3 960 s runtime (per the
in-spec S0.6-pattern arithmetic); all flight-free, daily-tier cost class
once proven. Implementation cost: three pure decider files + three executor
bodies + dispatch rows + the hlib companions (verb move, budget dict rows,
`spec_expects_live_recording` clause) + three scenario specs + one new
fixture pair; no analyzer work in this batch (the two analyzer invariants
are named follow-ups).

## Deferred Items and Open Questions

Deferred (named, out of this batch):

- **Focus-switch verb / simultaneous multi-EVA.** The background-parent EVA
  route and simultaneous two-kerbals-out coverage need a vessel-switch verb
  (`SimulateStockSwitchClick` stays reserved) plus kerbal-move; the in-game
  `EvaTwiceFromSameCapsule` test remains the bg-parent coverage owner.
- **EvaMove / jetpack locomotion.** None headless in v1 (A4 HARD stands);
  blocks flag-at-distance / walk-away scenarios (R18's EVA walkback is
  playback-side and untouched).
- **PlantFlag name arg.** Needs a private-field write into the dialog
  state; v1 dismiss-default is deterministic and sufficient.
- **Career EVA science / flag milestones.** Ledger-facing EVA belongs to
  the KscAction batch-2 / L-track lineage (oracle point 8).
- **Board-into-foreign-vessel.** Cross-recording crew transfer via a
  two-vessel fixture; its own fixture + assertion story.
- **Flag playback assertion.** Ghost-side `ApplyFlagEvents` spawning is an
  S1.4-style playback scenario with its own registry decision.

Open questions (flagged for the implementation PR / review):

- OQ1: EvaBoard proximity bound - 10 m is a guess; live-tune, and consider
  upgrading to the kerbal's airlock-trigger state (protected member, needs
  an internal accessor) if the distance bound proves flaky.
- OQ2: if the LKO fixture cannot be committed, does EVA-2 go autopilot
  hybrid, or is the fixture produced by a set-orbit edit (operator call)?
- OQ3: registry growth (D5 route split, D12 crew-manifest, flag
  record/playback split) - each decided in the PR that first asserts the
  cell, per the growth rule.
- OQ4: pin the EVA-1 "(foreground recorder" log discriminator immediately,
  or only after P6 confirms the owner routing live?
