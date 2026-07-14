# Design: Test-command seam verbs, batch 1 (Module M-C1)

Status: DRAFT (2026-07-13). Module M-C1 of the Automated Testing Plan
(`docs/dev/automated-testing-plan.md`, section 6 "the L-track" L0-L5, section 11b).
This is the Step 3 design doc the plan mandates before any code: it turns four of
the fifteen RESERVED phase-3 verb names into implemented v1-envelope verbs so the
harness can drive re-fly, merge answers, time jumps, and KSC career actions from an
external orchestrator. The command envelope is "new data that persists" already
frozen by M-A2; this doc extends BEHAVIOR only, adding no wire-format break.

Consumed contracts (already merged, read as authorities, never re-specified here):

- The command seam `docs/dev/design-autotest-command-seam.md` (M-A2), which OWNS the
  wire grammar (id/cmd/args, percent-encoding), the three-phase WAL journal
  (CLAIMED/EXECUTED/DONE), the verdict taxonomy (OK/ERROR/REJECTED/TIMEOUT/
  INTERRUPTED), the strict-FIFO pump, the safe-point gate, the deferral-budget
  TIMEOUT model, and the two-phase PENDING pattern for long-running verbs. This doc
  pins against those surfaces and NEVER re-defines them.
- The harness core `docs/dev/design-autotest-harness-core.md` (M-A5), which RESERVES
  driver-step kinds and owns the driver-validity verdict overlay (retry-once-then-
  INVALID) and the scenario budgets.
- The ledger oracle `docs/dev/design-autotest-ledger-oracle.md` (M-B2), which owns
  the manifest-entry format, the seam-declared-provenance rule, and the
  compute_expected independence invariant.
- The mission library `docs/dev/design-autotest-mission-library.md` (M-B1), which
  established the mission-validity-vs-Parsek-defect orthogonality this doc reuses for
  verb refusals.

Plain ASCII, no em dashes, no emoji.

---

## Problem

M-A2 shipped ten implemented verbs (`SetSetting`, `StartRecording`, `StopRecording`,
`CommitTree`, `DiscardTree`, `RecordingState`, `RunTests`, `LoadGame`, `MissionMark`,
`FlushAndQuit`) plus fifteen RESERVED names the parser recognizes and rejects with
`not-implemented-v1` (`TestCommandVerbs.cs:46-63`). Reserving the names froze the wire
envelope so phase-3 verbs slot in without a format break, but the reserved verbs do
nothing: every one is `REJECTED msg=not-implemented-v1`
(`design-autotest-command-seam.md:337-340`, edge case 6).

The L-track (plan section 6, L0-L5) needs four of those reserved verbs to reach its
higher tiers. Without them the harness can drive a flown mission (M-B1) and read the
career ledger (M-B2), but it cannot script the Parsek-specific ACTIONS the interesting
scenarios exercise:

- **Re-fly.** L4/L5 scenarios (catalog S4.1 re-fly a crashed booster, S4.7 chain
  rewind with four asserts, S1.5 loop rewind) need to invoke Rewind-to-Separation on a
  chosen rewind point and slot. There is no way to drive `RewindInvoker` except by a
  human clicking the "Fly" button in the confirmation dialog
  (`RewindInvoker.ShowDialog`, `RewindInvoker.cs:426`).
- **Merge answers.** Every re-fly ends in a merge dialog (Commit / Discard, plus a
  third Merge-and-Seal button on a not-yet-sealable re-fly), whose buttons run their
  action INSIDE the `DialogGUIButton` callback (`MergeDialog.cs:240-263`). An
  unattended run cannot click them, so it cannot complete a re-fly.
- **Time jumps.** L1 ("includes warp") + L5 + catalog S4.8 need a scripted forward
  jump to assert the epoch-shift contract (relative position frozen, SMA/ecc/inc
  unchanged, MNA-at-epoch shifted by the delta, earlier chain tips auto-spawned) and
  the S1.5 "warp past EndUT" step. (The R8 warp-reseed-lag regression is NOT a TimeJump
  concern: `ExecuteJump` stops warp and epoch-shifts instantly, so R8 needs a separate
  scripted rails-warp verb, deferred - see Deferred Items.)
- **KSC career actions.** L1-L5 economy scenarios need to research a tech node,
  upgrade a facility, and hire / dismiss a Kerbal on purpose so the ledger oracle
  (M-B2) can cross-check Parsek's recalc against a seam-declared manifest, and so the
  R6/R7 passive guards (no silent facility-upgrade refund on scene change; no N-fold
  contract reward bake) have a real action to guard.

M-C1 implements EXACTLY these four verbs: `InvokeRewind`, `AnswerMergeDialog`,
`TimeJump`, `KscAction`. The other eleven reserved names STAY reserved (Deferred
Items). The design goal is the same discipline M-A2 set: a pure, xUnit-testable
decision core (`internal static`), a thin Unity applier on the addon, at-most-once for
every irreversible side effect via the existing WAL, and honest driver-INVALID-vs-
Parsek-FAIL classification for every refusal.

## Terminology

Terms from M-A2 (Orchestrator, Addon, Command, Verdict, Journal, Safe point, Pump,
Reserved verb) are used unchanged. New terms this doc introduces:

- **Irreversible verb**: a verb whose side effect mutates persistent career / tree /
  world state that a later command cannot cleanly undo (`InvokeRewind`,
  `AnswerMergeDialog`, and each mutating `KscAction`). At-most-once via the WAL is
  load-bearing for these, exactly as it is for `LoadGame`.
- **Bounded-wait verb**: a verb that must DEFER (not reject) until a transient UI or
  world condition appears, then act. `AnswerMergeDialog` is the only one in this batch:
  it defers until a LIVE re-fly merge popup exists (scoped to the re-fly dialog kind,
  not any `ParsekMerge` popup), then locates it by name and invokes the chosen button's
  own callback. Because that popup only exists during a scene-exit that concludes the
  re-fly attempt, the verb also DRIVES that conclusion when a re-fly session marker is
  live but no dialog has spawned yet (Behavior below).
- **Gate reason**: the human-readable decline string a Parsek precondition gate
  produces (`RewindInvoker.CanInvoke(rp, out reason)`, `RewindInvoker.cs:64`). A verb
  that routes through such a gate surfaces the reason VERBATIM in the response `msg` so
  the orchestrator sees WHY, not a bare failure (`design-autotest-command-seam.md:475-478`).
- **Sub-action**: one member of the `KscAction` action family, selected by the
  `action` arg (`research-node`, `upgrade-facility`, `hire-kerbal`, `dismiss-kerbal`).
  One verb, an action arg, per-sub-action refusal semantics.
- **Seam-declared manifest annotation**: the ledger-oracle (M-B2) manifest entry a
  DRIVER step attaches to the run manifest describing the career effect the step is
  about to cause. The verb PERFORMS the real stock action; the DRIVER (harness side)
  DECLARES the expected effect. `compute_expected` consumes seam-declared entries only,
  and amounts NEVER come from Parsek recalc (M-B2 independence invariant).
- **Verb refusal**: a verb that declines to act because a precondition the orchestrator
  should have satisfied is not met (a gate decline, insufficient funds, a backward
  jump, no live dialog). Per the M-B1 / M-A5 doctrine, a refusal is a DRIVER-INVALID
  (retry-once), NEVER a PARSEK-FAIL. Whether Parsek recorded / accounted the action
  correctly is decided separately by the verifier chain over the produced save.

## Mental Model

```
   external orchestrator (Python, M-A5)            automation KSP instance (addon)
   -----------------------------------            --------------------------------
   append command line  --------------------->    parsek-test-commands.txt
     id=... cmd=InvokeRewind rp=rp_ab slot=1                 |
                                                             v  Update() pump (main thread)
                                            +-----------------------------------------+
                                            | DecideDispatch (pure, M-A2 + M-C1 rows): |
                                            |   Execute | Defer | Reject | Interrupted |
                                            |   + InvokeRewind: refuse merge-journal   |
                                            |     / load-in-flight; RequiresFlight     |
                                            |   + AnswerMergeDialog: AnyScene, defer   |
                                            |     until a LIVE re-fly merge popup      |
                                            |   + TimeJump: RequiresFlight             |
                                            |   + KscAction: research/hire/dismiss     |
                                            |     AnyScene; upgrade-facility SPACECENTER|
                                            +-----------------------------------------+
                                                             |
                                            journal CLAIMED  |  (WAL, at-most-once)
                                                             v
                                            +-----------------------------------------+
                                            | verb handler (thin Unity applier):       |
                                            |  InvokeRewind  -> RewindInvoker.CanInvoke |
                                            |                   -> StartInvoke (PENDING)|
                                            |  AnswerMergeDialog -> find live ParsekMerge|
                                            |                   popup by name, invoke   |
                                            |                   chosen button callback  |
                                            |  TimeJump      -> TimeJumpManager.Execute |
                                            |                   Jump (PENDING)          |
                                            |  KscAction     -> real stock API (sync;   |
                                            |                   upgrade = live building)|
                                            +-----------------------------------------+
                                                             |
                                            two-phase PENDING verbs hold the FIFO head
                                            until a pure completion decider settles:
                                              InvokeRewind:     marker present after reload
                                              TimeJump:         UT reached + spawn settle
                                              AnswerMergeDialog: scene settled after answer
                                                             |
                                            journal EXECUTED -> terminal response -> DONE
                                                             v
   read/tail response line  <----------------    parsek-test-responses.txt
     id=... verdict=OK session=sess_...            parsek-test-commands.journal (WAL)
```

Four invariants shape the design, each inherited from a merged contract:

- **The pure core decides; the thin addon acts.** Every new decision
  (`DecideRewindCompletion`, `DecideJumpCompletion`, the `KscAction` resolve/refuse
  predicate, the merge-choice mapper, the new `DecideDispatch` rows) is `internal
  static` and xUnit-covered without Unity, exactly as `TestCommandLoadGame`,
  `SettingWhitelist`, and `DecideDispatch` are today. Only the addon
  (`ParsekTestCommandAddon`) samples live KSP state, calls the real
  `RewindInvoker` / `MergeDialog` / `TimeJumpManager` / stock APIs, and appends
  responses.

- **At-most-once for irreversible side effects via the existing WAL.** The three-phase
  journal (`TestCommandJournal.cs`, `DecideRecovery` at `TestCommandJournal.cs:282`)
  already guarantees a non-idempotent side effect runs zero-or-one times. Each new
  irreversible verb rides the SAME contract: the side effect runs only after CLAIMED
  durably lands; a crash at CLAIMED reports INTERRUPTED and never re-runs. This doc
  reasons explicitly about what `InvokeRewind` and `AnswerMergeDialog` need BEYOND that
  base contract (Behavior below) and concludes they need no new mechanism.

- **A verb refusal is a driver problem, not a Parsek defect.** A gate decline,
  insufficient funds, a backward jump, or a missing dialog is REJECTED (or ERROR) and
  the M-A5 harness classifies it INVALID (retry-once), NEVER PARSEK-FAIL. Whether
  Parsek recorded the re-fly, accounted the tech unlock, or shifted the epoch correctly
  is judged ONLY by the verifier chain over the produced save (analyzer, log rules,
  ledger oracle). The two questions are orthogonal and both must pass for a PASS.

- **The envelope does not change.** No new wire keys are required (verbs add
  verb-specific args, which M-A2 already allows and unknown-key-ignores). The four
  verbs move from the reserved set to the implemented set in `TestCommandVerbs.cs`; a
  v0 addon that predates M-C1 still rejects them cleanly with `not-implemented-v1`.

## Data Model

M-C1 adds no persisted file format. It extends three in-code pure structures and adds
four pure decision helpers. All are `internal static` / `internal struct`, xUnit-
covered.

### Verb-table move (`TestCommandVerbs.cs`)

The four names move from `ReservedVerbs` (`TestCommandVerbs.cs:46`) to
`ImplementedVerbs` (`TestCommandVerbs.cs:31`). `TestCommandVerbClass.Classify`
(`TestCommandVerbs.cs:65`) then returns `Implemented` for them, so `DecideDispatch`
stops short-circuiting them to `Reject("not-implemented-v1")`
(`TestCommandDispatcher.cs:162`). The remaining eleven reserved names are untouched.

> Update (M-C1.1): a fifteenth implemented verb, `SaveGame`, was later added (a NEW name,
> never reserved) for the M-B3 R6 persist-before-reload dependency; see the M-C1.1
> follow-ups section below.

### DispatchState extension (`TestCommandDispatcher.cs:66`)

Three new bits are sampled by the addon per poll (in `BuildDispatchState`,
`ParsekTestCommandAddon.cs:887`) and read by the pure `DecideDispatch`:

```
struct DispatchState                      // existing fields unchanged
    ...
    bool ReFlyMergeDialogPresent; // a live ParsekMerge popup exists AND it is the
                                  // re-fly merge dialog (kind-scoped, see below)
    bool ActiveReFlyMarker;       // ParsekScenario.Instance.ActiveReFlySessionMarker != null
    bool MergeJournalInFlight;    // ParsekScenario.Instance.ActiveMergeJournal != null
    bool CareerPresent;           // career singletons live (Funding/RnD/roster present)
    bool AtSpaceCenter;           // HighLogic.LoadedScene == GameScenes.SPACECENTER
```

- `ReFlyMergeDialogPresent` is the bounded-wait signal for `AnswerMergeDialog`. It is
  NOT the raw `ParsekScenario.MergeDialogPending` flag: that flag is set by THREE spawn
  sites that all share the same `MergeDialog.DialogName` "ParsekMerge" popup (the
  whole-tree `ShowTreeDialog` 1-arg overload `MergeDialog.cs:270`, the pre-transition
  4-arg overload `MergeDialog.cs:464`, and the pre-switch decision dialog
  `MergeDialog.cs:716`), so waiting on it alone would answer a NON-re-fly dialog (the
  pre-switch Merge/Discard popup, for instance) with a re-fly `choice`. The signal must
  be dialog-kind-scoped: a live `ParsekMerge` popup AND `ActiveReFlySessionMarker != null`
  (the re-fly merge dialog is the only "ParsekMerge" popup that spawns while a re-fly
  marker is live). The wrong-dialog hazard is called out in Edge Cases.
- `ActiveReFlyMarker` mirrors `ParsekScenario.Instance.ActiveReFlySessionMarker != null`.
  `AnswerMergeDialog` uses it to decide whether it may DRIVE the re-fly conclusion
  scene-exit (Behavior below); `InvokeRewind` does not need it (its own gate,
  `RewindInvoker.CanInvoke`, already refuses when a marker exists).
- `MergeJournalInFlight` mirrors `ParsekScenario.ActiveMergeJournal`
  (`ParsekScenario.cs:81`), the persisted re-fly merge journal. A non-null journal
  means a re-fly merge is mid-finalize; `InvokeRewind` must refuse.
- `CareerPresent` is the `KscAction` readiness bit for the game-level singletons:
  `HighLogic.CurrentGame != null && HighLogic.CurrentGame.Mode == Game.Modes.CAREER` and
  the relevant singleton for the sub-action is live (`Funding.Instance`,
  `ResearchAndDevelopment.Instance`, or the roster). `research-node` / `hire-kerbal` /
  `dismiss-kerbal` operate on these game-level singletons and are AnyScene.
  > Update (M-C1.1): `research-node` was later widened to admit SCIENCE_SANDBOX too, via a
  > separate `RnDPresent` bit; the shared CAREER `CareerPresent` gate is unchanged and still
  > gates hire / dismiss / upgrade-facility. See the M-C1.1 follow-ups section below.
- `AtSpaceCenter` gates ONLY `upgrade-facility`: the funds debit lives in
  `SpaceCenterBuilding.UpgradeFacility(bool)`, a SPACECENTER-scene MonoBehaviour instance
  method with no singleton, so the verb must resolve a live `SpaceCenterBuilding` in the
  SPACECENTER scene (Behavior below).

These are ADDITIVE fields on a struct the pure tests build directly; existing rows in
the M-A2 dispatch matrix are unaffected (they leave the new bits default-false, which
none of the ten existing verbs read).

### Per-verb precondition rows (`TestCommandDispatcher.cs:125`)

New entries in the `Preconditions` table:

| verb | VerbSceneRequirement | extra guards in DecideDispatch |
|---|---|---|
| `InvokeRewind` | `RequiresFlight` | refuse `merge-journal-in-flight` (MergeJournalInFlight); refuse `load-in-flight` (LoadInFlight); refuse `recording-active` (Recording) |
| `AnswerMergeDialog` | `AnyScene` | defer `no-refly-dialog` while `!ReFlyMergeDialogPresent && !ActiveReFlyMarker` (nothing to answer and no attempt to conclude) |
| `TimeJump` | `RequiresFlight` | (none beyond the scene gate) |
| `KscAction` (research-node / hire-kerbal / dismiss-kerbal) | `AnyScene` | defer `career-not-ready` while !CareerPresent |
| `KscAction` (upgrade-facility) | `AnyScene` gate + SPACECENTER sub-gate | defer `career-not-ready` while !CareerPresent; defer `not-at-space-center` while !AtSpaceCenter |

`VerbSceneRequirement` (`TestCommandDispatcher.cs:102`) is reused unchanged. The extra
guards are added to `DecideDispatch` step 4 (the LoadGame-guard block,
`TestCommandDispatcher.cs:183`) as a per-verb switch, mirroring how the LoadGame
guards live there today.

### Pure completion deciders (new files)

Two two-phase verbs get pure completion deciders, each modeled on
`TestCommandLoadGame.DecideLoadCompletion` (`TestCommandLoadGame.cs:71`):

`TestCommandRewind.cs`:

```
enum RewindCompletionDecision { StillWaiting, CompleteOk, RewindFailed, RewindTimeout }

internal static RewindCompletionDecision DecideRewindCompletion(
    double elapsedSeconds, bool contextPending, bool markerPresent, double budgetSeconds)
{
    if (contextPending) return StillWaiting;      // pre-load or mid-load straddle
    if (markerPresent)  return CompleteOk;        // ConsumePostLoad wrote the marker
    if (elapsedSeconds >= budgetSeconds) return RewindTimeout;
    return RewindFailed;                          // context cleared, no marker: StartInvoke/ConsumePostLoad aborted
}
```

`contextPending` maps to `RewindInvokeContext.Pending` (`RewindInvokeContext.cs`; the
static context survives the scene reload). `markerPresent` maps to
`ParsekScenario.Instance.ActiveReFlySessionMarker != null`. Because
`RewindInvoker.CanInvoke` refuses when a marker already exists
(`RewindInvoker.cs:115-120`), a marker appearing AFTER `StartInvoke` is unambiguously
this command's. The order (context first, then marker, then a fast failure BEFORE the
budget, then timeout as the catch-all) mirrors `DecideLoadCompletion`'s ordering
rationale (`TestCommandLoadGame.cs:57-70`).

`TestCommandTimeJump.cs`:

```
enum JumpCompletionDecision { StillWaiting, CompleteOk, JumpTimeout }

internal static bool IsForwardJump(double nowUT, double targetUT)
    => targetUT > nowUT;                          // mirrors TimeJumpManager.IsValidJump (TimeJumpManager.cs:274)

internal static double ResolveTargetUt(double nowUT, string utArg, string deltaArg, out string error)
    // exactly one of ut / deltaSeconds; InvariantCulture parse; error != null on a bad/absent pair

internal static JumpCompletionDecision DecideJumpCompletion(
    double elapsedSeconds, double currentUT, double targetUT,
    double toleranceSeconds, int settleFramesRemaining, double budgetSeconds)
{
    bool reached = Math.Abs(currentUT - targetUT) <= toleranceSeconds;
    if (reached && settleFramesRemaining <= 0) return CompleteOk;
    if (elapsedSeconds >= budgetSeconds) return JumpTimeout;
    return StillWaiting;
}
```

`toleranceSeconds` is a small fixed epsilon (e.g. `1e-3` s) since
`Planetarium.SetUniversalTime` lands the clock exactly; the tolerance only absorbs
float representation. `settleFramesRemaining` counts down a short settle window (a few
frames) so the engine playback loop can drain the spawn queue (chain tips crossed
during the jump, `TimeJumpManager.ExecuteJump` step 3) before the terminal response
claims completion.

### KscAction pure resolve/refuse decider (new file)

`TestCommandKscAction.cs` holds the pure part: parse the `action` arg into a
`KscActionKind` enum, validate the action-specific arg is present, and (given the
sampled cost / current-state primitives the addon passes in) decide accept vs a typed
refusal reason. It mirrors `SettingWhitelist.TryApply` (`SettingWhitelist.cs:112`) the
pure-decider-plus-thin-applier split:

```
enum KscActionKind { ResearchNode, UpgradeFacility, HireKerbal, DismissKerbal, Unknown }

struct KscActionDecision
{
    bool   Accepted;
    string RejectReason;      // unknown-action / missing-arg / unknown-tech-node /
                              // node-already-unlocked / insufficient-science /
                              // unknown-facility / facility-at-max / insufficient-funds /
                              // unknown-kerbal / kerbal-not-applicant / kerbal-not-dismissable
    KscActionKind Kind;
    string Target;            // node id / facility id / kerbal name
    string ManifestKind;      // tech-unlock / facility-upgrade / kerbal-hire / kerbal-dismiss
}

internal static KscActionKind ParseKind(string action);   // exact match, kebab-case
internal static KscActionDecision Decide(
    string action, string target,
    bool targetResolves, bool alreadyApplied,
    double costAmount, double availableAmount, bool costIsFunds);
```

The addon RESOLVES the live target (an `RDTech`, a live SPACECENTER-scene
`SpaceCenterBuilding` for the named facility, a `ProtoCrewMember`) and reads the current
cost / balance / level, passes the primitives here, and this pure decider returns
accept-or-typed-refusal. The applier then invokes the real stock API only on accept, and
CONFIRMS the stock call's EFFECT before reporting OK (Behavior below) - Parsek's own
committed-action guard patches can silently block the stock call, and a guard-blocked
call is a REJECTED refusal, never a false OK. The `ManifestKind` field is the
seam-declared manifest kind the harness attaches (Behavior below); the verb never writes
a ledger row.

## Behavior

### Where the new logic lives

- Pure core: `TestCommandRewind.cs`, `TestCommandTimeJump.cs`,
  `TestCommandKscAction.cs`, `TestCommandMergeAnswer.cs` (the `DecideAnswerCompletion` +
  choice mapper), the new `DecideDispatch` rows + `DispatchState` fields in
  `TestCommandDispatcher.cs`, the verb-table move in `TestCommandVerbs.cs`, the new
  deferral budgets in `DeferralBudget.cs` (`TestCommandDispatcher.cs:214`).
- Thin addon: four new verb bodies + four `ITestCommandExecutor` methods on
  `ParsekTestCommandAddon` (`ParsekTestCommandAddon.cs:87` interface,
  `:934` explicit implementations, `:954` `InvokeExecutor` switch). The two-phase
  completion for `InvokeRewind`, `TimeJump`, and `AnswerMergeDialog` (which holds the head
  through the post-answer scene settle) hooks into the existing `TryCompleteTwoPhase`
  dispatch (`ParsekTestCommandAddon.cs:684`), which already branches by `completionVerb`.
- One internal-ization outside the seam: `MergeDialog.DialogName` becomes
  `internal const` (it is `private const` today, `MergeDialog.cs:11`). This is the
  phase-3 change the M-A2 doc already anticipated
  (`design-autotest-command-seam.md:466-474`), which explicitly prescribes locating the
  live popup by `DialogName` and invoking the chosen button's action directly. No new
  `MergeDialog` entry point is added: `AnswerMergeDialog` finds the live `PopupDialog`
  and invokes the chosen `DialogGUIButton`'s own callback (below), which is the wiring
  each button already carries across all three `ShowTreeDialog` / `ShowPreSwitchDecisionDialog`
  overloads.

### ITestCommandExecutor grows four methods

`ITestCommandExecutor` (`ParsekTestCommandAddon.cs:87`) gains
`InvokeRewind`, `AnswerMergeDialog`, `TimeJump`, `KscAction`. The addon adds the four
explicit interface implementations (mirroring `ParsekTestCommandAddon.cs:943-952`) and
four cases in `InvokeExecutor` (`ParsekTestCommandAddon.cs:957`). Each body stashes its
verdict / payload / msg via `SetExecResult` (or the `PendingVerdict` sentinel for the
two-phase verbs) exactly as the ten existing bodies do.

### InvokeRewind (two-phase, irreversible, gate-routed)

**Args.** `rp=<RewindPointId>` (required), `slot=<SlotIndex>` (required). Both
percent-encoded like every arg. `rp` matches `RewindPoint.RewindPointId`
(`RewindPoint.cs:28`); `slot` matches a `ChildSlot.SlotIndex` in
`rp.ChildSlots` (`RewindPoint.cs:40`).

**Resolution.** The addon scans `ParsekScenario.Instance.RewindPoints`
(`ParsekScenario.cs:52`) for the RP whose `RewindPointId == rp`, then finds the
`ChildSlot` whose `SlotIndex == slot`. A missing RP -> `REJECTED msg=unknown-rp`; a
missing slot -> `REJECTED msg=unknown-slot`. (These are stable identifiers a scenario
spec can carry: a re-fly fixture injects a tree with a known RP id and slot, and the
spec cites them verbatim.)

**Dispatch gate.** `RequiresFlight` (a re-fly is invoked from FLIGHT). Beyond the scene
gate, `DecideDispatch` refuses:
- `merge-journal-in-flight` when `MergeJournalInFlight` is true (a re-fly merge is
  mid-finalize; firing another rewind on top of it would race
  `MergeJournalOrchestrator`'s crash-recovery finisher, `MergeJournalOrchestrator.cs`).
  This is the explicit "InvokeRewind arriving mid-merge-journal must be refused" rule.
- `load-in-flight` when a `LoadGame` is already mid-flight (`LoadInFlight`).
- `recording-active` when a recorder is live (`Recording`): a re-fly captures a
  reconciliation bundle and reloads the scene; the orchestrator should `CommitTree` /
  `DiscardTree` first, mirroring the LoadGame `recording-active` guard
  (`TestCommandDispatcher.cs:185`).

**Gate + initiation (the CLAIMED side effect).** On Execute the body:
1. Calls `RewindInvoker.CanInvoke(rp, out reason)` (`RewindInvoker.cs:64`), the real
   five-precondition gate (invokable scene, no pending invocation, RP not corrupted,
   quicksave present on disk, no active re-fly marker, deep-parse precondition). A
   decline is a REFUSAL: `SetExecResult("REJECTED", null, "refly-gate " + reason)` so
   the gate reason rides the response `msg` verbatim
   (`design-autotest-command-seam.md:475-478`). No side effect ran, so the WAL
   CLAIMED->EXECUTED->DONE completes with no mutation.
2. On a passing gate, calls `RewindInvoker.StartInvoke(rp, selected)`
   (`RewindInvoker.cs:489`), which runs the synchronous PRE-LOAD phase (capture
   reconciliation bundle, copy the RP quicksave to save-root,
   `GamePersistence.LoadGame` + `FlightDriver.StartAndFocusVessel`,
   `RewindInvoker.cs:588-651`). It returns `PendingVerdict`; `loadInFlight`-analogue is
   set via the `RewindInvokeContext.Pending` static the invoker already sets
   (`RewindInvoker.cs:588`). The FIFO head is held.

**Two-phase completion.** `TryCompleteTwoPhase` (`ParsekTestCommandAddon.cs:684`) gets
an `InvokeRewind` branch, polled only at settled scenes (the pump gates off during
LOADING / transition / settle, `ParsekTestCommandAddon.cs:224-235`). It routes
`elapsed`, `RewindInvokeContext.Pending`, and
`ParsekScenario.Instance.ActiveReFlySessionMarker != null` through
`TestCommandRewind.DecideRewindCompletion`:
- `StillWaiting`: keep holding the head (the scene reload + `ConsumePostLoad` are still
  running, `RewindInvoker.cs:682`).
- `CompleteOk`: the new scenario's `OnLoad` ran `ConsumePostLoad` -> Restore -> Strip
  -> Activate -> `AtomicMarkerWrite` (`RewindInvoker.cs:789-1010`) and a fresh marker
  exists. Terminal `OK`, payload `rewound=true session=<marker.SessionId> rp=<rp>
  slot=<slot> activePid=<marker active pid>`.
- `RewindFailed`: the context cleared but no marker landed (StartInvoke's LoadGame
  returned null, or `ConsumePostLoad` aborted, `RewindInvoker.cs:610-620` /
  `:699-707`). Terminal `ERROR msg=rewind-failed`.
- `RewindTimeout`: the reload never settled within the budget. Terminal
  `ERROR msg=rewind-timeout`.

**At-most-once, and how a crashed rewind actually surfaces.** The re-fly is
irreversible (it creates a provisional fork, writes a `ReFlySessionMarker`, recalcs the
ledger, and mutates the save, `RewindInvoker.cs:891-960`). The base WAL contract is
sufficient and no new mechanism is added, but the crash-recovery reasoning is subtle
enough to state, and it must NOT contradict the M-A5 harness model (INTERRUPTED is
unreachable in a v1 run: the harness NEVER restarts the seam addon mid-scenario -
`design-autotest-harness-core.md`, M-A5):

- The side effect (`StartInvoke`) runs ONLY after CLAIMED durably lands
  (`ParsekTestCommandAddon.cs:577-584`). The journal is only ever CLAIMED (side effect
  initiated) or a single terminal (EXECUTED+DONE written together at completion): the
  terminal OK payload (the marker session id, the active pid) is not known until
  `ConsumePostLoad` writes the marker, which happens ACROSS the scene reload driven by
  KSP's `OnLoad`, NOT by the seam's EXECUTED transition. So there is no window where
  EXECUTED is written but the response is not, hence nothing for the base WAL's
  EXECUTED-response-rewrite recovery (`TestCommandJournal.cs:331`) to rewrite.
- What a crash DURING the rewind reload looks like to the harness: the KSP process hangs
  or dies mid-load, the seam never appends a terminal response, the M-A5 budget watchdog
  fires, and `run.py` process-tree-kills the instance and records the scenario as
  KILLED. KILLED is TERMINAL: `hlib.should_retry` never retries a KILLED verdict (a hang
  recurs), so the scenario is NOT re-staged and NOT re-driven - the half-executed rewind
  save is simply reported KILLED and skipped. The at-most-once property does NOT rest on
  a retry-from-clean-template argument; it rests on the WAL ALONE: "never fire
  `StartInvoke` twice within one run," which the CLAIMED gate guarantees because the seam
  never re-calls `StartInvoke` from CLAIMED. A killed run makes at most one `StartInvoke`
  call and then stops.
- The conservative-INTERRUPTED path is kept seam-side purely as a defensive contract:
  IF the addon were ever restarted with an id stuck at CLAIMED (a hypothetical the v1
  harness does not exercise), `DecideRecovery` (`TestCommandJournal.cs:282`) returns
  `Interrupted` and the addon writes `INTERRUPTED` WITHOUT re-invoking. It never
  reconstructs a late OK from the marker. There is no `RecordingState`-reconcile story
  in v1: a killed rewind is a terminal KILLED run (no retry, no re-stage), and the
  at-most-once property stands on the WAL's CLAIMED gate alone, full stop.

**Interaction with in-flight recordings and the merge journal.** The `recording-active`
dispatch refusal keeps a live recorder from being silently discarded by the reload. The
`merge-journal-in-flight` refusal keeps a rewind from racing a re-fly merge's crash-
recovery finisher. Both are dispatch-level (they never reach the CLAIMED side effect).

### AnswerMergeDialog (AnyScene, find-popup-by-name, conclude-and-answer, irreversible)

**Args.** `choice=<merge|discard|seal>`. `merge` (alias `commit`) invokes the Commit
button; `discard` invokes Discard; `seal` invokes the Merge-and-Seal button that only
exists on a not-yet-sealable re-fly dialog (`MergeDialog.cs:237-248`). An unrecognized
choice -> `REJECTED msg=unknown-choice`.

**When the re-fly merge dialog actually spawns (why this verb is more than a click).**
The re-fly merge dialog does NOT auto-spawn "a moment later" after the rewind reload.
While a re-fly session owns the pending tree, `OnFlightReady` deliberately SUPPRESSES the
merge dialog (`ParsekFlight.Finalization.cs:32-58`, `MaybeShowPendingTreeMergeDialogOnFlightReady`
skips when `RewindInvokeContext.Pending` or an in-place-continuation Re-Fly session owns
the pending tree). The dialog spawns only when a scene-exit CONCLUDES the attempt, on one
of two paths:
- **pre-transition:** the `SceneExitInterceptor` Harmony prefix on `HighLogic.LoadScene`
  sees `ActiveReFlySessionMarker != null` and spawns the pre-transition re-fly merge
  dialog (the 4-arg `ShowTreeDialog` overload, `SceneExitInterceptor.cs:788` / `:835`),
  BLOCKING the stock scene change (`return false`); the dialog's own button `postChoice`
  re-invokes `LoadScene` to complete the transition. This path is reachable while the
  scene is still FLIGHT.
- **post-transition:** the deferred non-FLIGHT coroutine (`ParsekScenario.cs`, "Showing
  deferred tree merge dialog") spawns the 1-arg `ShowTreeDialog` overload in a non-FLIGHT
  scene, only when the pre-transition catch was missed.

So the answerable dialog straddles FLIGHT (pre-transition) and non-FLIGHT
(post-transition), which is why the scene gate is `AnyScene`, not `RequiresFlight` (the
prior draft's `RequiresFlight` gate excluded the post-transition dialog outright).

**Strict-FIFO constraint: the verb concludes AND answers.** A separate scene-changing
seam step (e.g. a `LoadGame` between `InvokeRewind` and `AnswerMergeDialog`) does NOT
compose here: the pre-transition dialog BLOCKS that scene change, and a two-phase verb
holds the FIFO head until its own scene settles (`TryCompleteTwoPhase`,
`ParsekTestCommandAddon.cs:741` "StillWaiting keeps holding the FIFO head"). A blocking
`LoadGame` conclusion would therefore sit at the head - unanswered, because the only
command that CAN answer the dialog is starved behind it - until it hits `load-timeout`.
That is the AnswerMergeDialog FIFO-starvation deadlock (finding A-B1 of the design review panel). v1 resolves it by folding the scene-exit conclusion INTO
`AnswerMergeDialog`: it is the single command that both surfaces and answers the dialog,
so no second FIFO command is ever needed.

**Dispatch (bounded wait).** `AnyScene`. `DecideDispatch` defers with `no-refly-dialog`
while `!ReFlyMergeDialogPresent && !ActiveReFlyMarker` - nothing to answer and no re-fly
attempt to conclude. As soon as either a live re-fly merge popup exists OR a re-fly
session marker is present, the command Executes. If neither ever appears, the deferral
budget converts it to TIMEOUT (`ParsekTestCommandAddon.cs:859`), so a mis-sequenced
answer never wedges the run. This is the same head-defer mechanism `StartRecording` uses
to wait for FLIGHT (`design-autotest-command-seam.md:454`).

**Execution (find live popup by name, invoke the button's own callback).** On Execute,
two-phase:
1. If a LIVE re-fly merge dialog is present (`ReFlyMergeDialogPresent`), skip to step 3.
2. Else (`ActiveReFlyMarker` true, no dialog yet): DRIVE the re-fly conclusion by
   requesting the scene-exit a human's "exit flight" takes (a `HighLogic.LoadScene` to
   SPACECENTER, the natural post-flight destination). The `SceneExitInterceptor` prefix
   fires synchronously, spawns the pre-transition re-fly merge dialog, and blocks the
   stock transition. The popup is now live in the same execution frame.
3. Locate the live `PopupDialog` by `MergeDialog.DialogName` ("ParsekMerge"), confirm it
   is the re-fly merge dialog (a re-fly marker is live), and invoke the chosen
   `DialogGUIButton`'s OWN callback directly. The button lambdas run their action INSIDE
   the callback (`MergeDialog.cs:240-263` / `:416-458`: Commit -> `MergeCommit`, Discard
   -> `MergeDiscard`, Merge-and-Seal -> `MergeCommit(..., playerRequestedSeal: true)`, or
   the pre-transition overload's `RunPreTransitionAction(isMerge, preCommitFinalize,
   postChoice)`). Invoking the button's OWN callback - rather than reconstructing
   `MergeCommit`'s `(tree, decisions, spawnCount)` arguments through a stashed context -
   is the M-A2-prescribed design (`design-autotest-command-seam.md:466-474`): it survives
   ALL overloads (1-arg post-transition, 4-arg pre-transition, pre-switch decision) and
   cannot drift if `MergeDialog` ever adds a fourth button, because the seam never has to
   know what each button DOES. It also honors the project's deferred-field PopupDialog
   callback trap (MEMORY: project_deferred_field_popup_callback_trap): the callback runs
   synchronously in the command frame; the seam never sets a `pendingChoice` field a
   `DrawWindow` reads a frame later.
4. The button's action (and, on the pre-transition path, its `postChoice`) completes the
   blocked transition. The verb returns `PendingVerdict` and holds the FIFO head, and the
   `AnswerMergeDialog` branch of `TryCompleteTwoPhase` routes `elapsed`,
   `HighLogic.LoadedScene`, the budget, AND an answer-applied flag through a pure
   `TestCommandMergeAnswer.DecideAnswerCompletion` (mirroring `DecideLoadCompletion`).
   Scene-settle ALONE is never `OK`: if the driven exit takes the POST-transition path
   instead (the pre-transition prefix passes because `HasActiveTree` is false - see the
   `HasActiveTree` dependency note below), the deferred dialog spawns AFTER the scene
   settles, and a settle-keyed decider would report a false `OK` over an orphaned
   unanswered dialog. The completion contract is therefore: answer-applied (the button
   callback was invoked in step 3, OR a post-settle re-scan finds the deferred
   "ParsekMerge" popup and invokes the chosen button THEN) AND the post-answer scene
   settled (`ClearedFlight` / a non-LOADING settled scene) -> terminal `OK`; budget
   expired with the answer never applied -> terminal `ERROR msg=answer-timeout`.

**`HasActiveTree` dependency (load-bearing).** The pre-transition dialog fires only when
`ShouldShowDialogBeforeSceneChange` returns `ReFlyAttempt`, which requires
`hasActiveTree || switchSegmentActive` - the re-fly marker ALONE is insufficient
(`SceneExitInterceptor.cs:161-165`). This holds for the normal in-place-continuation
re-fly because `RewindInvoker.RestoreActiveTreeFromPending` restarts the recorder into
the fork (`RewindInvoker.cs:1470`), so `HasActiveTree` is true for the whole attempt.
The exception is a placeholder-mode re-fly where the recorder never armed
(`ParsekFlight.Finalization.cs:26-29`): there the driven exit passes the prefix
un-intercepted and the dialog arrives via the deferred POST-transition coroutine - the
post-settle re-scan branch of step 4 exists precisely for this path. Step 2's driven
exit also implicitly assumes FLIGHT (only a FLIGHT exit is intercepted); the degenerate
"marker live + no dialog + non-FLIGHT" state is practically unreachable (a
post-transition dialog would already be live and satisfy `ReFlyMergeDialogPresent` at
step 1), noted here for completeness.

- A `seal` choice against a 2-button (already-sealable) dialog -> the located popup has
  no Merge-and-Seal button -> `ERROR msg=choice-unavailable`. If the popup was dismissed
  between the dispatch sample and the execute (no live "ParsekMerge" popup and no re-fly
  marker to re-surface one) -> `ERROR msg=no-live-dialog`.

**Payload.** `OK choice=<choice> result=<committed|discarded|sealed>`.

**At-most-once composition.** The merge answer is irreversible (a commit writes
supersede rows + may open a `MergeJournal`; a discard tears down a subtree,
`MergeDialog.cs`). It leans on TWO recovery systems, and the composition is the point:
- The seam WAL: CLAIMED lands before the button is invoked, so the button is invoked at
  most once. A crash mid-commit means the KSP process dies before a terminal response;
  the M-A5 watchdog kills the instance (KILLED). KILLED is terminal - `hlib.should_retry`
  never retries it, so the scenario is NOT re-staged or re-driven (as for `InvokeRewind`
  above); the at-most-once property stands on the WAL's CLAIMED gate alone (the button is
  invoked at most once within the run). The conservative-INTERRUPTED path is kept
  seam-side for the hypothetical restart the v1 harness does not exercise; it never
  re-invokes the button.
- The merge journal's own crash-recovery: a `MergeCommit` that opened a `MergeJournal`
  and then crashed is driven forward-or-back on the next `OnLoad` by
  `MergeJournalOrchestrator.RunFinisher` (`MergeJournalOrchestrator.cs`), INDEPENDENT of
  the seam. So the seam guarantees "the answer button is invoked at most once," and the
  merge journal guarantees "a half-done merge reaches a consistent terminal."

**`restoringActiveTree` is not reachable in the harness.** `OnFlightReady`'s merge-dialog
fallback also skips while a `#293` clone-restore coroutine owns the pending tree
(`restoringActiveTree`, `ParsekFlight.Finalization.cs:37-52`). The v1 harness never drives
a committed-tree clone-restore (no `SimulateStockSwitchClick` yet - it stays reserved), so
`restoringActiveTree` is never set during an `AnswerMergeDialog` sequence; it is called out
here only so a future switch-segment batch knows to re-examine this interaction rather than
assume it is inert.

### TimeJump (two-phase, forward-only, epoch-shift)

**Args.** Exactly one of `ut=<absoluteUT>` or `deltaSeconds=<positive-delta>`,
InvariantCulture floats, percent-encoded. Both absent or both present ->
`REJECTED msg=missing-jump-target`.

**Dispatch.** `RequiresFlight` (the epoch-shift jump operates on loaded vessels and
`FlightGlobals`). No extra guard beyond the scene gate. Map view is a FLIGHT sub-mode
(`HighLogic.LoadedScene == FLIGHT` with `MapView.MapIsEnabled`), so a jump requested from
the map is covered by the same flight gate - no separate map-scene handling is needed.

**Refuse backward jumps.** On Execute the body resolves the target UT via
`TestCommandTimeJump.ResolveTargetUt(now, utArg, deltaArg, out error)` and checks
`IsForwardJump(now, target)` (mirrors `TimeJumpManager.IsValidJump`,
`TimeJumpManager.cs:274`, which is strictly `target > current`). A backward or zero
jump -> `REJECTED msg=backward-jump` (a driver refusal: the orchestrator asked for a
non-forward jump). No side effect ran.

**Initiation (the CLAIMED side effect).** A forward jump calls a new thin
`ParsekFlight.TimeJumpTo(double targetUT)` wrapper that mirrors `WarpToRecordingEnd`
(`ParsekFlight.cs:26001`) but takes an arbitrary target: it validates
`TimeJumpManager.IsValidJump`, notifies the recorder
(`TimeJumpManager.NotifyRecorder`, `TimeJumpManager.cs:436`), and calls
`TimeJumpManager.ExecuteJump(targetUT, null, vesselGhoster)`
(`TimeJumpManager.cs:317`) with null chains (spawning handled by the engine playback
loop, as `WarpToRecordingEnd` does at `ParsekFlight.cs:26031`). `ExecuteJump` sets
`Planetarium` UT, epoch-shifts all vessel orbits keeping position/velocity (freezing
relative positions), processes the spawn queue (chain tips crossed during the jump),
and recalcs the ledger at the post-jump UT. The body returns `PendingVerdict`.

Why `ExecuteJump` (epoch-shift) and not `ExecuteForwardJump` (orbits propagate,
`TimeJumpManager.cs:477`): S4.8 asserts relative position delta within tolerance
(frozen positions), SMA/ecc/inc unchanged, MNA-at-epoch shifted exactly by the delta,
and earlier chain tips auto-spawned. That is precisely the `ExecuteJump` contract
(freeze relative geometry, update epoch, drain spawn queue). `ExecuteForwardJump` is a
DIFFERENT contract (positions advance along the orbit) used by the fast-forward button;
TimeJump v1 uses `ExecuteJump` only. A future `mode=` arg selecting the propagate
variant is Deferred.

**Two-phase completion.** `ExecuteJump` first STOPS time warp (`TimeWarp.SetRate(0,
true)` when `CurrentRateIndex > 0`, `TimeJumpManager.cs:323-326`) and then
`SetUniversalTime` lands the clock synchronously, so there is NO warp-reseed-lag here to
wait out - the jump is not a rails-warp coast (R8, warp-reseed-lag, is a DIFFERENT
regime; see the note below). The settle window exists only to let the engine playback
loop drain the spawn queue (chain tips crossed during the jump) and let the newly spawned
end-of-recording ghosts settle over the next few frames. The `TimeJump` branch of
`TryCompleteTwoPhase` routes `elapsed`, `Planetarium.GetUniversalTime()`, the captured
target, the fixed tolerance, and a settle-frame countdown through
`TestCommandTimeJump.DecideJumpCompletion`:
- `StillWaiting`: UT not yet within tolerance, or the settle window has not elapsed.
- `CompleteOk`: UT within tolerance AND settle frames drained. Terminal `OK`, payload
  `ut=<reachedUT> target=<target> delta=<target-startUT>`.
- `JumpTimeout`: budget expired without reaching the target (should not happen for a
  synchronous jump, but bounds a pathological SetUniversalTime failure). Terminal
  `ERROR msg=jump-timeout`.

**Completion boundary (what the terminal OK asserts, and what it does NOT).** The
completion decider confirms EXACTLY two things: the clock reached the target UT within
tolerance, and the settle-frame window elapsed. It deliberately does NOT confirm the
S1.5 post-jump vessel spawn (the chain tip that crosses the jumped UT and spawns as a real
vessel). Confirming a specific spawn from inside the completion decider would couple the
verb to spawn-queue internals and to which recording the scenario happens to use;
instead the scenario confirms the spawn with a FOLLOWING `RecordingState` step (or the
verifier chain over the produced save), which is the seam's normal division of labour -
the verb reports the primitive (the epoch reached and settled), the verifier chain judges
the consequences (spawns, orbital elements, ledger). A vessel-count-delta observation was
considered and rejected for v1: the count delta is not cleanly attributable to THIS jump
(other ghosts loop/retire on the same frames), whereas `RecordingState` is an existing,
already-parsed truth surface.

**Note - warp-reseed-lag (R8) is a deferred gap, not a TimeJump concern.** R8 is the
map-render lag seen when a real rails-warp is reseeded across a threshold; `ExecuteJump`
never rails-warps (it stops warp and epoch-shifts instantly), so TimeJump does not
exercise R8. Covering R8 needs a genuine scripted rails-warp verb (drive `TimeWarp` up,
observe, drive it back down), which is out of scope for M-C1 and listed under Deferred
Items.

**At-most-once.** A time jump mutates the world clock and epoch-shifts orbits
(irreversible in-place). The WAL protects it identically: CLAIMED lands before
`ExecuteJump`, so the jump fires at most once; a crash mid-jump dies before a terminal,
the M-A5 watchdog kills the instance (KILLED). KILLED is terminal (never retried by
`hlib.should_retry`), so the scenario is not re-staged or re-driven; the at-most-once
property stands on the WAL's CLAIMED gate alone (`ExecuteJump` fires at most once within
the run, never re-driving the mutated clock). Unlike `InvokeRewind`, the jump
does NOT straddle a scene reload, so its OK payload IS known at completion within one
scene and it CAN ride the EXECUTED response-rewrite recovery for the hypothetical restart
the v1 harness does not exercise, exactly like `RunTests`.

**Ledger annotation.** `ExecuteJump` triggers a ledger recalc at the post-jump UT
(`TimeJumpManager` `RecalculateLedgerAfterTimeJump`). If a scenario asserts the ledger
across a jump, the DRIVER attaches no manifest entry for the jump itself (a jump causes
no career award); it relies on the ledger oracle's seed-vs-recalc cross-check. A
jump-then-rewind sequence leaves no seam-side residual (no marker, no context from the
jump), so the following `InvokeRewind` sees a clean gate. This composability is an edge
case below.

### KscAction (sync, real stock action, ledger observes organically)

**Args.** `action=<research-node|upgrade-facility|hire-kerbal|dismiss-kerbal>` plus the
action-specific target: `node=<techId>` / `facility=<facilityId>` / `kerbal=<name>`.

**Dispatch (per sub-action).** The scene gate splits by sub-action:
- `research-node` / `hire-kerbal` / `dismiss-kerbal` are `AnyScene` with a
  `career-not-ready` defer while `!CareerPresent`. These act on game-level singletons
  (`ResearchAndDevelopment.Instance`, `Funding.Instance`, the roster / `KerbalRoster`),
  which work headlessly in any scene once the singletons are live, so the gate is
  career-readiness, not a specific scene.
- `upgrade-facility` additionally requires SPACECENTER. Its funds debit is NOT a
  singleton call: it lives in `SpaceCenterBuilding.UpgradeFacility(bool)`
  (`FacilityUpgradeSpendPatch.cs:15`), an instance method on a SPACECENTER-scene
  `SpaceCenterBuilding` MonoBehaviour. There is no headless entry point that produces the
  real debit, so the verb must resolve a LIVE building instance (below), and
  `DecideDispatch` defers `not-at-space-center` while `!AtSpaceCenter` (on top of the
  `career-not-ready` defer).

A never-career game (or, for `upgrade-facility`, a never-SPACECENTER run) deferring here
converts to TIMEOUT and the orchestrator reconciles.

**The verb performs the REAL stock action, then CONFIRMS its effect.** KscAction never
writes a ledger row; it invokes the exact stock method the player's UI would, so Parsek's
recorder observers and the ledger recalc see the change organically (the ledger reads
KSP's own state, `docs/dev/design-autotest-ledger-oracle.md`). The state changes are
picked up by the `GameStateRecorder` observers - `GameEvents.OnTechnologyResearched`
(`GameStateRecorder.cs:315`), `GameEvents.OnCrewmemberHired` (`:323`),
`GameEvents.onKerbalRemoved` (`:324`), and facility POLLING via `GameStateFacilityRecorder`
(`GameStateRecorder.PollFacilityState`) - NOT by the committed-action guard patches (those
BLOCK, they do not record; see the confirmation requirement below).

CRUCIAL: after the stock call the applier CONFIRMS the stock call's EFFECT before
reporting OK, because Parsek's committed-action guard patches can silently BLOCK the stock
call - `TechResearchSpendPatch` (`RDTech.ResearchTech`), `FacilityUpgradeSpendPatch`
(returns false, no deduction + no level bump), `KerbalHirePatch.ShouldAllowHire`, and
`KerbalDismissalPatch`'s `IsManaged` block all return false on a committed target and skip
the stock method entirely. A guard-blocked call is a REJECTED refusal with a reason (below),
NEVER a false OK. The applier confirms via the effect surface, not a bare return:
`RDTech.OperationResult` / the node's researched state, the roster membership change, the
facility level change. Per sub-action, after the pure `TestCommandKscAction.Decide`
accepts:

| sub-action | stock API invoked, then effect confirmed | recorder observer | manifest kind |
|---|---|---|---|
| `research-node` | resolve the `RDTech` for the node, drive the stock research-buy path `RDTech.ResearchTech()` (spends science) which fires `RDTech.UnlockTech`; confirm `RDTech.OperationResult` / node researched | `GameEvents.OnTechnologyResearched` (`GameStateRecorder.cs:315`) | `tech-unlock` |
| `upgrade-facility` | resolve the LIVE SPACECENTER-scene `SpaceCenterBuilding` for the facility id (below) and invoke its `UpgradeFacility(bool)` so both the funds debit and the `Facility.SetLevel(level+1)` are the real stock ones; confirm the facility level rose | facility POLLING via `GameStateFacilityRecorder.PollFacilityState` | `facility-upgrade` |
| `hire-kerbal` | resolve the applicant from `CrewRoster.Applicants`, mirror the stock debit then call `KerbalRoster.HireApplicant(ProtoCrewMember)` (below); confirm roster membership | `GameEvents.OnCrewmemberHired` (`GameStateRecorder.cs:323`) | `kerbal-hire` |
| `dismiss-kerbal` | resolve the crew member, `KerbalRoster.Remove(ProtoCrewMember)`; confirm roster removal | `GameEvents.onKerbalRemoved` (`GameStateRecorder.cs:324`) | `kerbal-dismiss` |

**Resolving the live building for `upgrade-facility`.** The addon finds the
`SpaceCenterBuilding` instances in the SPACECENTER scene (they are scene MonoBehaviours
with a `Facility` id, no singleton), matches the one whose `Facility.id` equals the
`facility=` arg, and invokes its real `UpgradeFacility(true)`. That is the only path that
produces the genuine funds debit `Funding.Instance.AddFunds(-GetUpgradeCost(),
StructureConstruction)` the spend patch would otherwise refund-bug (BUG-G), which is why
the sub-gate forces SPACECENTER - the building instances do not exist in FLIGHT.

**Resolving the hire debit for `hire-kerbal`.** `KerbalRoster.HireApplicant` moves the
applicant into the roster but does NOT itself debit funds (the stock debit lives in the
Astronaut Complex UI). The verb mirrors the stock debit explicitly: it runs the stock
affordability check (`CurrencyModifierQuery`) and, on accept, calls
`Funding.Instance.AddFunds(-hireCost, TransactionReasons.CrewRecruited)` BEFORE
`KerbalRoster.HireApplicant(ProtoCrewMember)` (`KerbalHirePatch.cs:19-22`). The
`CrewRecruited` reason key is load-bearing: the ledger's KSC-action expectation
classifier keys the hire funds leg on `FundsChanged` / `"CrewRecruited"` / `-HireCost`
(`KscActionExpectationClassifier.cs:140-148`); a debit under any other reason would not
reconcile. `hireCost` comes from the stock `GameVariables` recruit-cost curve (it is
state-dependent - it rises with the roster size - so it is NOT a hardcoded constant; see
the manifest note below).

The `facility-refund` manifest kind (M-B2, `design-autotest-ledger-oracle.md:257`) is
NOT an active sub-action: it is the PASSIVE R6 guard (no silent facility-upgrade refund
on scene change). A scenario exercises it by `KscAction upgrade-facility` -> `LoadGame`
(a scene change) -> asserting the ledger shows no phantom refund; the verb family adds
no refund sub-action.

**Refusal semantics (driver-INVALID via REJECTED).** The pure `Decide` returns a typed
refusal, each mapped to a distinct `msg` and classified INVALID (retry-once) by the
harness:
- `research-node`: `unknown-tech-node` (the id does not resolve), `node-already-unlocked`
  (idempotency: the node is already researched), `insufficient-science` (the science
  pool is below the node cost).
- `upgrade-facility`: `unknown-facility` (no live `SpaceCenterBuilding` for the id),
  `facility-at-max` (already top level), `insufficient-funds`.
- `hire-kerbal`: `unknown-kerbal` / `kerbal-not-applicant` (not in the applicant pool),
  `insufficient-funds` (hire cost exceeds funds).
- `dismiss-kerbal`: `unknown-kerbal` / `kerbal-not-dismissable` (assigned / tourist /
  protected), and `kerbal-parsek-managed` when `LedgerOrchestrator.Kerbals.IsManaged(name)`
  is true - the `KerbalDismissalPatch` `IsManaged` block would refuse the stock `Remove`
  (`KerbalDismissalPatch.cs:35-40`), so the precondition mirrors it and declines up front
  rather than watching the stock call get blocked.

A second class of refusal is the guard-blocked call: if the applier invokes the stock
method and a committed-action guard patch silently blocks it (the confirmation step sees
no effect - no `OperationResult`, no level change, no roster change), the verb reports
`REJECTED msg=blocked-committed` rather than a false OK. Each refusal is a REFUSAL, not a
Parsek fault: the orchestrator mis-sequenced (it did not ensure funds / science, named a
wrong id, or targeted a committed action). The verifier chain still judges whether Parsek
accounted whatever DID happen.

**Payload.** `OK action=<action> target=<target> applied=true` plus an observed-after
field for logging only (`scienceAfter` / `fundsAfter` / `level` / `crewCount`). The
observed-after values are for the KSP.log / debugging; they are NEVER the oracle's
source of truth (the manifest amounts are author constants, M-B2 independence
invariant).

**Seam-declared manifest annotation.** The manifest entries are free-standing
`[[expectations.ledger.manifest]]` rows the scenario AUTHOR declares in the spec (M-B2);
they are NOT attached per-step by `run.py`. The harness ACCUMULATES the declared rows
(sorted by `ut` / `seq`) into the run manifest (`harness/results/<runId>.manifest.json`)
with `provenance = "seam-declared"`, and that accumulated manifest is the oracle's
`compute_expected` input; the stock-log capture cross-checks via
`unmatched_captured_awards`. The verb does not compute or write any of this; its only
oracle-facing contribution is causing the real KSP state change the log capture and the
Parsek recalc both observe. A `KscAction` step's manifest kind is the `ManifestKind` in
the table above.

Which facet carries the author constant follows the M-B2 facet policy:
- `tech-unlock` declares an AUTHOR-CONSTANT `science` delta (the node cost is fixed data,
  not state-dependent), exactly as the worked example below shows.
- `facility-upgrade` and `kerbal-hire` funds deltas ride the funds facet, which is the
  pool-only fill-eligible facet in the M-B2 model. IMPORTANT: fill-from-capture is only
  usable once a matching stock-log capture pattern EXISTS. Today `STOCK_AWARD_PATTERNS`
  (`harness/lib/hlib.py`) enumerates only `science-transmit` and `contract-complete`
  (funds / reputation) - there is NO hire (`CrewRecruited`) capture pattern, and the stock
  hire debit does not emit a line any current pattern can match. So leaving the
  `kerbal-hire` funds delta null to fill-from-capture is a GUARANTEED FALSE-RED today (the
  oracle expects the seed unchanged while the produced save actually debited the hire
  cost). Therefore, for `kerbal-hire` NOW declare a FIXTURE-PINNED author constant: pin the
  fixture roster so the recruit-cost curve yields a known cost and hardcode that constant.
  Switch `kerbal-hire` to null-to-fill-from-capture ONLY AFTER the capture patterns are
  rewritten against real stock KSP.log hire lines (the M-B3 sequenced work item).
  `facility-upgrade`'s cost is fixed per level, so a declared author constant is natural
  there too; both may move to fill-from-capture once a matching capture pattern lands.

### M-A5 driver integration (budgets, INVALID subkinds)

Deferral budgets (`DeferralBudget.BudgetSeconds`, `TestCommandDispatcher.cs:238`) gain
four rows:

| verb | deferral budget | rationale |
|---|---|---|
| `InvokeRewind` | rewind budget (~300 s, LoadGame-class) | a re-fly copies a quicksave, reloads the scene, and runs `ConsumePostLoad`; sized like `LoadGame` (`TestCommandDispatcher.cs:220`) |
| `AnswerMergeDialog` | scene-exit budget (~120 s) | it may DRIVE the conclusion scene-exit that surfaces the pre-transition dialog, then holds the head through the post-answer scene settle; the wait-plus-settle wants a real bound, not the bare default |
| `TimeJump` | jump budget (~120 s) | the jump is synchronous but the settle + ledger recalc want a bound; well under an infinite hang |
| `KscAction` | default 60 s | covers the career-ready / SPACECENTER wait; the action itself is immediate |

Verdict-to-harness-classification (a REFUSAL is always driver-INVALID retry-once, never
PARSEK-FAIL):

| verb outcome | harness classification |
|---|---|
| `InvokeRewind REJECTED msg=refly-gate ...` | INVALID(driver-gate), retry-once |
| `InvokeRewind ERROR msg=rewind-failed / rewind-timeout` | INVALID(driver-rewind), retry-once |
| `AnswerMergeDialog TIMEOUT msg=no-refly-dialog` | INVALID(driver-dialog), retry-once |
| `AnswerMergeDialog ERROR msg=choice-unavailable / no-live-dialog / answer-timeout` | INVALID(driver-dialog), retry-once |
| `TimeJump REJECTED msg=backward-jump / missing-jump-target` | INVALID(driver-arg), retry-once |
| `KscAction REJECTED msg=insufficient-* / unknown-* / *-already-* / blocked-committed / kerbal-parsek-managed / not-at-space-center` | INVALID(driver-career), retry-once |
| any verb `OK` but the verifier chain reds the produced save | PARSEK-FAIL (orthogonal) |

**Harness-side companion changes (hlib, same PR).** The Parsek verb-table move has a
mirror on the Python side that must land with it, or the harness rejects the four verbs
before they ever reach KSP:
- `hlib.py:96-101` currently lists all four (`InvokeRewind`, `AnswerMergeDialog`,
  `KscAction`, `TimeJump`) in `RESERVED_SEAM_VERBS`, so `hlib` HARD-REJECTS a spec that
  uses them. Move the four from `RESERVED_SEAM_VERBS` into `IMPLEMENTED_SEAM_VERBS`
  (`hlib.py:92-95`), mirroring the C# `ReservedVerbs` -> `ImplementedVerbs` move.
- Add `InvokeRewind` and `TimeJump` to `DEFERRED_SEAM_VERBS` (`hlib.py:126`, currently
  `("RunTests", "LoadGame")`). These two are the two-phase / long-running verbs whose
  per-step budget the 540s cap + step-wait margin (`MAX_DEFERRED_STEP_BUDGET_SECONDS` /
  `STEP_WAIT_MARGIN_SECONDS`) must govern. `AnswerMergeDialog` and `KscAction` are
  bounded-wait but complete quickly, so they are NOT added to `DEFERRED_SEAM_VERBS`
  (their budgets are the ordinary per-verb deferral budgets above).

The subkind names above are proposed for `hlib`'s `RETRYABLE_INVALID_SUBKINDS`; their
exact spelling is a harness-side (M-A5) wiring detail reconciled when the driver steps
are added, not a Parsek contract. Even before the finer subkinds land, these refusals
already classify RETRYABLE today: a step that declares `expect = "OK"` and receives a
`REJECTED` / `ERROR` / `TIMEOUT` is a driver-verdict-mismatch, which the M-A5 overlay
already treats as retry-once-then-INVALID. The subkinds only refine the reporting, they
do not gate retryability.

### D8/D9 coverage tokens unlocked

The coverage registry `harness/coverage/registry.toml` already enumerates the relevant
tokens; these verbs make them REACHABLE by an automated scenario (they do not add new
tokens, because no NEW Parsek feature is introduced -- the growth rule only requires a
token for a new feature). The verbs unlock:

- `InvokeRewind`: D9 `rewind-to-separation`, `refly-gate`, `reconciliation-bundle`,
  `read-back-guard`, `head-tip-split`, `supersede-relation`, `terminal-kind-classify`
  (all downstream of a real re-fly invocation).
- `AnswerMergeDialog`: D9 `merge-journal`, `supersede-relation`, `tombstones`,
  `revert-during-refly-dialog`.
- `TimeJump`: D6 `time-jump`; D18 `time-jump-observables`; D8 `epoch-isolation`,
  `recalc-engine` (the epoch-shift + post-jump recalc). D9 `fast-forward` stays
  UNCOVERED - it is the deferred `mode=propagate` variant (`ExecuteForwardJump`), which
  M-C1 does not wire.
- `KscAction`: D8 `science`, `funds`, `kerbals`, `facilities`, `ksp-state-patcher`,
  `orchestrator`, `recalc-engine`, `action-blocking` (the R6/R7 passive guards).

If, when the driver steps are authored, a token proves genuinely absent from the
registry, it is added in the SAME PR per the growth rule; none is anticipated.

### Example scenario spec fragments (one per verb)

These match the M-B1 `steps` array shape (`design-autotest-mission-library.md:213`); a
`cmd`-kind seam step per M-C1 verb. Substitutions (`${runSave}`) are the harness's.

InvokeRewind (a re-fly of an injected tree with a Crashed sibling + RP, fixture block
B9). The `InvokeRewind` -> `AnswerMergeDialog` pair is the whole re-fly cycle: `InvokeRewind`
spawns and settles the fresh attempt in FLIGHT, then `AnswerMergeDialog` DRIVES the
scene-exit that concludes it (surfacing the pre-transition merge dialog) and answers it.
There is deliberately NO separate scene-changing conclusion step between them: a blocking
`LoadGame` conclusion would hold the FIFO head waiting for a scene-settle that the
pre-transition dialog blocks, starving the very `AnswerMergeDialog` that would unblock it
(the AnswerMergeDialog FIFO-starvation deadlock; see AnswerMergeDialog Behavior).

**B9 fixture RP-usability prerequisites.** For `InvokeRewind rp=rp_b9_root slot=1` to pass
`CanInvoke` rather than decline, the injected B9 tree must satisfy three things the fixture
builder owns:
1. The RP quicksave sidecar must exist on disk at
   `saves/<save>/Parsek/RewindPoints/<rpId>.sfs`, or `CanInvoke` declines "Quicksave file
   missing on disk" (`RewindInvoker.cs:108-113`). The fixture injects it alongside the
   tree, it is not synthesized at invoke time.
2. The RP's `CreatingSessionId` must be null (or match), or `LoadTimeSweep` discards it as
   a session-scoped provisional RP before the driver can use it
   (`LoadTimeSweep.cs:157-167`).
3. The RP's `ChildSlots` / `PidSlotMap` must reference the staged vessels' craft-baked
   `persistentId`s, so `slot=1` resolves to a real child slot.

```toml
[driver]
kind  = "seam"
steps = [
  { cmd = "LoadGame",     args = { save = "${runSave}", name = "persistent" }, expect = "OK", budget = 240 },
  { cmd = "SetSetting",   args = { name = "autoRecordOnLaunch", value = "false" }, expect = "OK" },
  { cmd = "InvokeRewind", args = { rp = "rp_b9_root", slot = "1" }, expect = "OK", budget = 300 },
  { cmd = "AnswerMergeDialog", args = { choice = "merge" }, expect = "OK", budget = 120 },
  { cmd = "RecordingState", expect = "OK" },
  { cmd = "FlushAndQuit", expect = "OK" },
]
[expectations.recordings]
count = { min = 1 }
[expectations.logContracts]
required  = ["Re-Fly (Rewind-to-Separation) StartInvoke", "Invocation complete"]
forbidden = ["\\[Parsek\\]\\[ERROR\\]"]
```

The `AnswerMergeDialog merge` step leaves the run in SPACECENTER with the re-fly fork
merged and committed, so the following `RecordingState` (AnyScene) reports the merged tree
- it is the confirmation of the merge, not a second commit (a `CommitTree` here would
`ERROR msg=no-active-tree`, since the merge already committed the fork).

TimeJump (warp past a recording EndUT, S1.5 / S4.8):

```toml
  { cmd = "TimeJump", args = { deltaSeconds = "600.0" }, expect = "OK", budget = 120 },
```

KscAction (research a node so the ledger oracle cross-checks, L1):

```toml
  { cmd = "KscAction", args = { action = "research-node", node = "basicRocketry" }, expect = "OK", budget = 60 },
```

with the free-standing manifest row the scenario AUTHOR declares in the same spec (a
`[[expectations.ledger.manifest]]` entry the harness accumulates by `ut` / `seq`, NOT a
per-step attachment `run.py` synthesizes):

```toml
[[expectations.ledger.manifest]]
  ut         = 0.0
  kind       = "tech-unlock"
  science    = -45.0          # author constant: the node cost; NEVER from Parsek recalc
  provenance = "seam-declared"
```

(For a `hire-kerbal` or `upgrade-facility` step, the funds facet on the manifest row
rides the fill-eligible funds facet, but fill-from-capture needs a matching stock-log
capture pattern to exist. No hire (`CrewRecruited`) capture pattern exists today, so a
null `hire-kerbal` funds delta is a guaranteed false-red: declare a FIXTURE-PINNED author
constant now, and switch to null-to-fill only after the capture patterns are rewritten
against real stock logs, M-B3. `upgrade-facility` likewise declares a constant until a
matching capture pattern lands.)

## Edge Cases

Exhaustive. Each: scenario -> expected behavior -> v1 or deferred.

1. **InvokeRewind names a nonexistent RP / slot.** Resolution fails ->
   `REJECTED msg=unknown-rp` / `unknown-slot`. No gate call, no side effect. v1.
2. **InvokeRewind gate declines (scene transition, corrupted RP, quicksave missing,
   active marker, deep-parse fail).** `RewindInvoker.CanInvoke` returns the reason ->
   `REJECTED msg=refly-gate <reason>` verbatim. Driver-INVALID. v1.
3. **InvokeRewind mid-merge-journal.** `DecideDispatch` refuses
   `merge-journal-in-flight` at dispatch (never reaches CLAIMED). v1.
4. **InvokeRewind with a live recorder.** Dispatch refuses `recording-active`; the
   orchestrator commits / discards first. v1.
5. **InvokeRewind crashes mid-reload.** The KSP process dies before a terminal response;
   the M-A5 budget watchdog kills the instance (KILLED). KILLED is terminal - it is never
   retried (`hlib.should_retry`), so the scenario is NOT re-staged or re-driven; the
   half-executed save is reported KILLED and skipped. The at-most-once guarantee is that
   `StartInvoke` fired at most once (the CLAIMED gate, on the WAL alone); there is no v1
   `RecordingState`-reconcile of a half-executed rewind, because the harness never restarts
   the seam mid-run (M-A5). v1.
6. **InvokeRewind completes but Parsek recorded the fork wrong.** Verb is `OK`; the
   verifier chain (analyzer / ledger oracle) reds the produced save -> PARSEK-FAIL,
   orthogonal to the verb verdict. v1.
7. **AnswerMergeDialog with no dialog and no re-fly to conclude.** Defers `no-refly-dialog`
   at the FIFO head; if neither a live re-fly popup nor a re-fly marker ever appears it
   TIMEOUTs (a mis-sequenced answer). When a re-fly marker IS present but no dialog is up,
   the verb DRIVES the conclusion scene-exit itself and answers the resulting dialog (it
   does not wait for a separate scene-change step, which would deadlock under strict FIFO
   - AnswerMergeDialog Behavior, A-B1). v1.
8. **AnswerMergeDialog answers the WRONG "ParsekMerge" popup.** Three spawn sites share
   `MergeDialog.DialogName` "ParsekMerge" (whole-tree merge, pre-transition re-fly merge,
   and the pre-switch decision dialog). The bounded-wait signal is dialog-kind-scoped
   (`ReFlyMergeDialogPresent` = a live ParsekMerge popup AND `ActiveReFlySessionMarker != null`),
   so a stray pre-switch decision popup with no re-fly marker does NOT satisfy the
   precondition and is never answered with a re-fly `choice`. If somehow a non-re-fly
   popup is live at execute time with no marker, the verb reports `ERROR msg=no-live-dialog`
   rather than clicking a foreign button. v1.
9. **AnswerMergeDialog choice=seal on a 2-button dialog.** The located popup has no
   Merge-and-Seal button -> `ERROR msg=choice-unavailable`. v1.
10. **AnswerMergeDialog: the popup was dismissed between sample and execute.** No live
    "ParsekMerge" popup and no re-fly marker to re-surface one -> `ERROR msg=no-live-dialog`.
    v1.
11. **AnswerMergeDialog crashes mid-commit.** The process dies before a terminal; the M-A5
    watchdog kills it (KILLED). KILLED is terminal (never retried), so the scenario is not
    re-staged or re-driven; the seam never re-invokes the button (at-most-once on the WAL
    alone). Independently, a `MergeCommit` that opened a `MergeJournal` and then crashed is
    driven to a consistent terminal on the next `OnLoad` by
    `MergeJournalOrchestrator.RunFinisher`, so even a save reused outside the harness
    converges. v1.
12. **TimeJump backward or zero delta.** `IsForwardJump` false ->
    `REJECTED msg=backward-jump`. v1.
13. **TimeJump with both ut and deltaSeconds (or neither).** `ResolveTargetUt` errors
    -> `REJECTED msg=missing-jump-target`. v1.
14. **TimeJump outside FLIGHT.** Dispatch defers `not-in-flight` until FLIGHT or its
    budget expires. v1.
15. **TimeJump then InvokeRewind (composition).** The jump leaves no seam residual (no
    marker, no context); the following `InvokeRewind` sees a clean gate. "Jump then
    rewind leaves no state" (S4.8). v1.
16. **TimeJump crashes mid-jump.** The process dies before a terminal; the M-A5 watchdog
    kills it (KILLED). KILLED is terminal (never retried), so the scenario is not re-staged
    or re-driven. The at-most-once guarantee is the CLAIMED gate on the WAL alone
    (`ExecuteJump` fired at most once; SetUniversalTime + epoch-shift is not idempotent, so
    it must never be re-driven within a run). v1.
17. **KscAction unknown action arg.** `ParseKind` returns `Unknown` ->
    `REJECTED msg=unknown-action`. v1.
18. **KscAction insufficient funds / science.** The pure `Decide` refuses before the
    stock call -> `REJECTED msg=insufficient-funds` / `insufficient-science`; no state
    touched. Driver-INVALID. v1.
19. **KscAction research a node already unlocked (idempotency).** `Decide` returns
    `node-already-unlocked` -> REJECTED, no double-unlock. v1.
20. **KscAction on a non-career game.** Dispatch defers `career-not-ready` (no
    `Funding`/`RnD`/roster singleton) until a career loads or TIMEOUT. v1.
21. **KscAction upgrade-facility outside SPACECENTER.** Dispatch defers
    `not-at-space-center` until the run is in SPACECENTER (where the `SpaceCenterBuilding`
    instances exist) or the budget expires. The other three sub-actions are AnyScene and
    do not defer here. v1.
22. **KscAction guard-blocked by a committed action.** The applier invokes the stock
    method but a committed-action guard patch (`TechResearchSpendPatch` /
    `FacilityUpgradeSpendPatch` / `KerbalHirePatch.ShouldAllowHire` / `KerbalDismissalPatch`
    `IsManaged`) silently blocks it; the effect-confirmation step sees no change ->
    `REJECTED msg=blocked-committed` (dismiss also pre-declines `kerbal-parsek-managed`
    when `IsManaged` is known up front). Never a false OK. v1.
23. **KscAction upgrade-facility then scene change (R6 passive guard).** The upgrade is
    real (the live `SpaceCenterBuilding.UpgradeFacility(bool)` debited funds and bumped the
    level); a following scene change must NOT produce a phantom `facility-refund`. The verb
    exercises the setup; the ledger oracle asserts no phantom refund. v1 (the guard is the
    assertion, not the verb).
24. **A reserved verb (one of the other eleven) arrives.** Still
    `REJECTED msg=not-implemented-v1` (`TestCommandVerbs.cs` unchanged for them). v1.
25. **A v0 addon (pre-M-C1) receives InvokeRewind.** It rejects `not-implemented-v1`;
    the orchestrator detects the capability gap and does not confuse it with a typo.
    Backward-compatible. v1.
26. **Two-phase verb budget expiry (InvokeRewind / TimeJump / AnswerMergeDialog never
    settles).** The existing `TryCompleteTwoPhase` budget path converts to `ERROR`
    (rewind-timeout / jump-timeout) or `TIMEOUT` (no-refly-dialog), advancing the head so
    the run is not wedged. v1.

## What Doesn't Change

- No recording format, schema generation, sidecar, tree, ledger, or save-file field
  changes. `RecordingStore.CurrentRecordingFormatVersion` /
  `CurrentRecordingSchemaGeneration` are untouched; no migration path is added.
- No wire-protocol change. The command line grammar, percent-encoding, response
  grammar, journal grammar, lock grammar, and verdict set are exactly M-A2's. The four
  verbs use only verb-specific args M-A2 already allows.
- No gameplay behavior in normal play. The addon stays env-gated
  (`PARSEK_TEST_COMMANDS`, `ParsekTestCommandAddon.cs:38`; armed only when it is exactly
  `"1"`, logged at `:189`) and inert otherwise; the
  four verbs are reachable only through the armed channel. `MergeDialog.DialogName`
  going `private -> internal const` and the new `ParsekFlight.TimeJumpTo` method are
  dormant unless the addon calls them (the const widening exposes no behavior; the verb
  invokes existing `DialogGUIButton` callbacks, adding no new `MergeDialog` method).
- `RewindInvoker`, `MergeDialog`, `TimeJumpManager`, and the KSC patches keep their
  existing behavior. The verbs call the SAME entry points a human click uses
  (`RewindInvoker.StartInvoke`, `MergeCommit` / `MergeDiscard`,
  `TimeJumpManager.ExecuteJump`, the stock research / upgrade / hire / dismiss APIs), so
  a re-fly / merge / jump / career action driven by the seam is byte-identical to a
  hand-driven one.
- No new `GameEvents` subscriptions that affect gameplay; the addon only reads state.
- The M-A2 pump, journal, deferral-budget, and two-phase machinery are reused
  unchanged; M-C1 only adds rows to the pure decision tables and branches to the
  existing `TryCompleteTwoPhase` and `InvokeExecutor` switches.

## Backward Compatibility

- The channel files are ephemeral test artifacts, not versioned save data; there is no
  save migration concern.
- Protocol forward/backward compatibility is preserved. A v1 (M-A2) addon rejects the
  four new verbs with `not-implemented-v1`; a v2 (M-C1) addon implements them. New
  verb-specific args are additive and unknown-key-ignored by older readers. An
  orchestrator that probes capability by sending `InvokeRewind` and seeing
  `not-implemented-v1` vs `OK` learns the addon's tier.
- The verb-table move (reserved -> implemented) is source-only; the wire tokens
  (`InvokeRewind`, `AnswerMergeDialog`, `TimeJump`, `KscAction`) are byte-identical
  before and after, so a scenario spec authored against the reserved names (expecting
  `not-implemented-v1`) still parses; only the RESPONSE changes.
- `MergeDialog.DialogName` widening from `private const` to `internal const` does not
  alter any serialized shape or public API; no new `MergeDialog` entry point is added (the
  verb invokes the existing `DialogGUIButton` callbacks by locating the live popup).
- Old journal inherited across the addon upgrade replays safely. The command journal
  (`parsek-test-commands.journal`) is an ephemeral test artifact, but if a v2 (M-C1) addon
  boots on a channel that still carries a v1 journal, `DecideRecovery` treats any id at
  DONE as already-processed and SKIPS it (`TestCommandJournal.cs:282`), so an inherited
  completed command is never re-executed; there is nothing M-C1-specific to migrate.
- No existing recordings, saves, or settings are read or rewritten by M-C1 beyond the
  live state the four real actions already mutate through their existing, unchanged
  paths.

## Diagnostic Logging

Subsystem tag `TestCommands` (the seam's, `ParsekTestCommandAddon.cs:35`), standard
`[Parsek][LEVEL][TestCommands] message` format. The underlying actions ALSO log under
their own tags (`Rewind` / `RewindUI`, `MergeDialog` / `MergeJournal`, `TimeJump`,
`TechResearch` / `FacilityUpgrade` / `KerbalHire` / `KerbalDismissal`), so a KSP.log
reader sees both the seam's command-level lines and the action's internal lines. Every
dispatch branch and every verb outcome is logged (no silent path), per the house
logging requirement. InvariantCulture for all numerics.

Per-verb Info lines (command counts are small, so per-command Info is allowed, matching
M-A2, `design-autotest-command-seam.md:699-730`):

- `InvokeRewind`: `Info` "invokerewind start rp=<rp> slot=<slot>"; on gate decline
  `Warn` "invokerewind refused: refly-gate <reason>"; on completion `Info`
  "invokerewind complete session=<sess> activePid=<pid>" or `Error` "invokerewind
  failed reason=<rewind-failed|rewind-timeout> elapsed=<s>s".
- `AnswerMergeDialog`: `Info` "answermergedialog choice=<choice> result=<result>"; when it
  drives the conclusion scene-exit itself, `Info` "answermergedialog driving re-fly
  conclusion scene-exit"; on the bounded-wait defer the standard rate-limited "dispatch
  id=<id> -> DEFER reason=no-refly-dialog"; on failure `Warn` "answermergedialog failed
  reason=<choice-unavailable|no-live-dialog>".
- `TimeJump`: `Info` "timejump start target=<ut> delta=<s>s" and "timejump complete
  reachedUT=<ut>"; on refuse `Warn` "timejump refused reason=<backward-jump|
  missing-jump-target>".
- `KscAction`: `Info` "kscaction action=<action> target=<target> applied=true
  manifestKind=<kind> observedAfter=<value>"; on refuse `Warn` "kscaction refused
  action=<action> reason=<...> target=<target>" (reasons include `blocked-committed`,
  `kerbal-parsek-managed`, `not-at-space-center`, and the affordability / unknown-id
  set).

Dispatch decisions reuse the M-A2 lines (`TestCommandDiagnostics.DispatchExecute /
DispatchReject / DispatchDefer / DispatchInterrupted`, `ParsekTestCommandAddon.cs:507`),
so the new refusals / defers ("merge-journal-in-flight", "no-refly-dialog",
"career-not-ready", "not-at-space-center", "backward-jump", the refly-gate reason) appear
in the standard "dispatch id=<id> -> REJECT/DEFER reason=<...>" shape.

Goal (unchanged from M-A2): a developer reading KSP.log can reconstruct, for every
command id, that it was received, which dispatch branch it took and why, whether the
side effect ran, and the terminal verdict, without the source.

## Test Plan

Every test states the regression it catches. The pure decision core is `internal
static` and xUnit-covered without Unity (mirroring the M-A2 suites:
`TestCommandDispatchTests`, `TestCommandLoadGameTests`, `TestCommandSettingApplierTests`,
`TestCommandJournalTests`). The Unity-touching verb bodies are exercised by an in-game
test plus a PENDING-OPERATOR runbook, because an agent cannot pilot KSP (MEMORY:
in-game-sweep-needs-operator).

### Pure unit tests (xUnit, no Unity)

- **Verb-table move.** `TestCommandVerbs.Classify` returns `Implemented` for the four
  names and `Reserved` for the remaining eleven; the counts (14 implemented, 11
  reserved) are asserted. Fails if a verb is mis-bucketed or a reserved name is
  accidentally implemented.
- **DecideDispatch new rows.** For each new verb x state: `InvokeRewind` outside FLIGHT
  -> Defer(not-in-flight); with `MergeJournalInFlight` -> Reject(merge-journal-in-flight);
  with `Recording` -> Reject(recording-active); with `LoadInFlight` -> Reject(load-in-flight);
  ready -> Execute. `AnswerMergeDialog` with `!ReFlyMergeDialogPresent && !ActiveReFlyMarker`
  -> Defer(no-refly-dialog); with either a live re-fly dialog OR a re-fly marker -> Execute.
  `TimeJump` outside FLIGHT -> Defer; in FLIGHT -> Execute. `KscAction` (research/hire/dismiss)
  with `!CareerPresent` -> Defer(career-not-ready); ready -> Execute. `KscAction upgrade-facility`
  with `CareerPresent && !AtSpaceCenter` -> Defer(not-at-space-center); at SPACECENTER -> Execute.
  Fails if a verb executes in an unsafe state or a guard is skipped (the safety matrix).
- **DecideRewindCompletion.** `contextPending` -> StillWaiting; `!pending &&
  markerPresent` -> CompleteOk; `!pending && !marker && !budget` -> RewindFailed;
  `!pending && !marker && budget-expired` -> RewindTimeout. Fails if a mid-reload poll
  prematurely terminates, or a genuine failure is read as success.
- **DecideJumpCompletion + IsForwardJump + ResolveTargetUt.** A forward `ut` and a
  positive `deltaSeconds` each resolve to the right target; a backward / zero / both /
  neither errors. Completion: reached-and-settled -> CompleteOk; reached-not-settled ->
  StillWaiting; budget-expired -> JumpTimeout; exactly-on-tolerance -> CompleteOk
  (inclusive). A comma-locale `deltaSeconds=600,0` -> error (InvariantCulture only).
  Fails if a backward jump is admitted, a locale comma is parsed, or the settle window
  is skipped.
- **KscAction Decide + ParseKind.** Each action parses to its kind; an unknown action
  -> Unknown. Accept when the target resolves, is not already applied, and the cost is
  affordable; each typed refusal (`unknown-tech-node`, `node-already-unlocked`,
  `insufficient-science`, `unknown-facility`, `facility-at-max`, `insufficient-funds`,
  `unknown-kerbal`, `kerbal-not-applicant`, `kerbal-not-dismissable`, `kerbal-parsek-managed`)
  fires on its boundary. The `ManifestKind` is the right kind per sub-action. Fails if an
  unaffordable action is admitted (a false OK), or a refusal maps to the wrong reason.
- **Merge-choice mapper + DecideAnswerCompletion.** `merge` / `commit` / `discard` /
  `seal` map to the right `MergeDialogChoice`; an unknown choice -> reject. Fails if a
  choice string drift invokes the wrong button. `DecideAnswerCompletion`: a settled
  post-answer scene -> CompleteOk; still LOADING / transitioning -> StillWaiting;
  budget-expired -> AnswerTimeout. Fails if the head advances before the scene settles or a
  stuck transition wedges the run.
- **Deferral budgets.** `DeferralBudget.BudgetSeconds` returns the rewind / dialog /
  jump / KscAction budgets for the four verbs and the default otherwise. Fails if a
  never-satisfiable verb inherits the wrong bound.
- **Journal at-most-once for the irreversible verbs.** A journal with an `InvokeRewind`
  / `AnswerMergeDialog` / `TimeJump` id at CLAIMED -> `DecideRecovery` = Interrupted (no
  re-execute); at DONE -> Skip; a fresh id -> Execute. Fails if a crash mid-rewind /
  mid-merge / mid-jump could re-fire the side effect (the core correctness guarantee).
- **Response formatter stability for the new payloads.** The `OK` payloads
  (`rewound=`/`session=`, `choice=`/`result=`, `ut=`/`delta=`, `action=`/`applied=`)
  and the refusal `msg`s are percent-encoded and grep-stable. Fails if a payload key is
  dropped or a locale comma leaks into a UT.

### Log-assertion tests (via ParsekLog.TestSinkForTesting)

- Each new verb emits its start / complete / refuse `[TestCommands]` line with the id
  and reason; each new dispatch branch (merge-journal-in-flight, no-refly-dialog,
  career-not-ready, not-at-space-center, backward-jump, refly-gate) is non-silent. Fails if
  a decision branch is a debugging blind spot or a marker format drifts.

### In-game tests (InGameTests, live KSP only)

Delivered as automated in-game tests PLUS a PENDING-OPERATOR runbook (an agent cannot
pilot KSP):

- With `PARSEK_TEST_COMMANDS` unset, the addon performs no file access and the four new
  verbs are never reachable (inert). Fails if the shipped default is not inert.
- A file-channel `InvokeRewind` -> `AnswerMergeDialog merge` -> `RecordingState`
  sequence on an injected B9 tree (Crashed sibling + RP) drives a real re-fly: `InvokeRewind`
  settles the attempt in FLIGHT, `AnswerMergeDialog merge` DRIVES the conclusion scene-exit
  (surfacing the pre-transition dialog) and invokes the Commit button's own callback, and
  the following `RecordingState` (now in SPACECENTER) reflects the merged tree. Fails if
  the two-phase completion, the conclusion-drive, the by-name popup lookup, or the button
  callback invoke is wrong. (PENDING-OPERATOR.)
- A `TimeJump deltaSeconds=600` from a known FLIGHT state lands the clock within
  tolerance and (per S4.8) leaves SMA/ecc/inc unchanged, MNA-at-epoch shifted by the
  delta, relative positions frozen, and earlier chain tips auto-spawned. The
  assertions are the verifier chain's; the verb test asserts the terminal `OK ut=`.
  (PENDING-OPERATOR for the physics assertions.)
- A `KscAction research-node` on a career fixture unlocks the node, spends science, and
  the ledger oracle cross-checks the recalc against the seam-declared `tech-unlock`
  manifest entry. Repeat for `upgrade-facility` (SPACECENTER scene, R6 refund guard after a
  following scene change), `hire-kerbal`, `dismiss-kerbal`. Confirm the state change
  reached Parsek's recorder observers by grepping the `GameStateRecorder` event lines the
  real handlers emit (`OnTechnologyResearched` / `OnCrewmemberHired` / `onKerbalRemoved`
  handlers, `GameStateRecorder.cs:315`/`:323`/`:324`, and the facility POLLING line from
  `GameStateFacilityRecorder`), NOT the guard-patch lines (a guard patch that fired would
  mean the stock call was BLOCKED, i.e. a REJECTED, not an OK). Per MEMORY:
  verify-harness-seeder-mutation. Fails if the stock action did not reach the observer, or
  the ledger mis-accounts. (PENDING-OPERATOR.)

## M-C1.1 follow-ups (Science-mode research gate + SaveGame verb)

Two follow-ups the M-B3 design (`docs/dev/design-autotest-ledger-scripts-b3.md`) ratified
against this M-C1 seam and named as its own blocking dependencies. Both were implemented
after the M-C1 merge (branch `autotest-c1-followups`). They extend BEHAVIOR only; no wire
grammar changes.

### Follow-up 1: sub-action-scoped Science-mode research-node gate (M-B3 OQ1)

As shipped, `research-node` deferred `career-not-ready` in SCIENCE_SANDBOX because the
KscAction dispatch shared a single readiness gate (`!CareerPresent`) whose
`CareerPresent` bit (`IsCareerReady`) hard-requires `Mode == Game.Modes.CAREER`. R&D and
node research are LIVE in Science mode, so this under-covered the "Science mode has
science" axis (M-B3 module activation matrix, ScienceModule row).

The ratified fix is a per-sub-action widen for `research-node` ALONE, following the same
per-sub-action gate pattern the `upgrade-facility` SPACECENTER sub-gate uses:

- A new `DispatchState.RnDPresent` bit: `(Mode == CAREER || Mode == SCIENCE_SANDBOX) &&
  ResearchAndDevelopment.Instance != null`, sampled by the addon's `IsResearchReady()`
  (mode-and-singleton, action-independent).
- The KscAction dispatch case now reads a per-sub-action `ready` bit:
  `research-node` admits on `CareerPresent || RnDPresent`; `hire-kerbal` /
  `dismiss-kerbal` / `upgrade-facility` STAY CAREER-only (`CareerPresent`) because their
  funds legs need `Funding.Instance`, which is null in Science mode and would NRE / be
  meaningless.

The shared top-level CAREER `Mode` bit (`CareerPresent`) was NOT relaxed - this is the
ratified warning (do NOT widen the gate all four sub-actions share). The pure decider
covers every mode x sub-action cell: CAREER (`CareerPresent=true, RnDPresent=true`) all
four admit-with-preconditions; SCIENCE_SANDBOX (`CareerPresent=false, RnDPresent=true`)
research admits, the other three defer `career-not-ready`; SANDBOX
(`CareerPresent=false, RnDPresent=false`) all four defer `career-not-ready`.

### Follow-up 2: the SaveGame batch-2 seam verb (M-B3 L2/R6 dependency)

M-B3's R6 facility-refund-window script needs to PERSIST an in-scene career mutation and
RELOAD it within a single launch (`upgrade-facility -> SaveGame -> LoadGame -> assert`),
which the M-C1 verb set could not do: the only `SaveGame` lived inside `FlushAndQuit`,
which QUITS, so a bare `LoadGame` after `upgrade-facility` reloaded the PRE-upgrade disk
fixture and manufactured a false signal. M-B3 named a standalone `SaveGame` verb (route b)
as the minimal honest dependency inside the existing one-launch-per-attempt model.

`SaveGame` was NEVER in the M-A2 reserved envelope (no save verb was reserved), so it is a
NEW implemented verb name (15 implemented / 11 reserved), taking the M-B3 `SaveGame`
naming. Design surface (house pattern):

- **Sync** (SaveGame is fast): standard journal CLAIMED -> EXECUTED -> DONE. A replayed
  DONE id is skipped upstream (the processed-set filter), so the save is never re-written
  on recovery; and re-saving a save re-serializes identical live state, so the DONE=skip
  is a cleanliness property, not a correctness dependency.
- **Precondition:** `AnyScene` with an in-executor no-game refusal (the AnyScene-with-a-game
  precondition: `ERROR msg=no-game` at MAINMENU / null `CurrentGame`, decided by the pure
  `TestCommandSaveGame.CanSave(gameLoaded, saveFolderPresent)`).
- **Args:** `{name?}` defaulting to `persistent` (`TestCommandSaveGame.ResolveName`).
- **Executor:** `GamePersistence.SaveGame(name, HighLogic.SaveFolder, SaveMode.OVERWRITE)`,
  mirroring `FlushAndQuitImpl`'s save call shape minus the quit.
- **Payload:** `OK saved=<name>`. A save-write failure (throw or empty result) ->
  `ERROR msg=save-failed`. Every path logged.

The pure `TestCommandSaveGame` decider (name default / can-save gate / payload) is
xUnit-covered; the hlib companion moved `SaveGame` into `IMPLEMENTED_SEAM_VERBS` (+ an
acceptance test). SaveGame is sync and completes immediately, so it is NOT a
`DEFERRED_SEAM_VERB` and rides the default dispatch deferral budget.

## Deferred Items and Open Questions

### The eleven reserved verbs stay reserved (one-line rationale each)

- `StartLoopPlayback` / `StopPlayback`: ghost playback control; needs the playback
  engine's loop/overlap surface wired to the seam, a later playback-verbs batch.
- `EnterWatchMode`: camera-follow / watch-mode entry; belongs with the playback batch.
- `SealSlot` / `StashSlot` / `FlySlot`: the unfinished-flights slot lifecycle (D9
  `unfinished-flights-stash` / `seal-stash-fly`); a distinct slot-verbs batch.
- `RouteCommand`: logistics route dock/transfer/undock scripting (D10); a logistics-
  verbs batch.
- `MissionConfig`: mission-tree / loop configuration (D11); a missions-verbs batch.
- `SimulateStockSwitchClick`: arms a `StockActionIntentMarker` to drive the switch/Fly
  continuation path; a switch-segment batch (touches
  `MapFocusObjectOnSelectPatch` / `SwitchSegmentSession`).
- `CrashAfterJournalPhase`: a fault-injection verb to force a crash at a chosen journal
  phase for at-most-once testing; a diagnostics batch, gated so it is inert unless armed.
- `RunInvariantReport`: runs the offline analyzer's `RecordingInvariants` in-game and
  returns the report; an analyzer-verbs batch.

### Within-batch deferrals

- **KscAction contract / strategy / milestone / EVA-science sub-actions.** The L1 action
  list also names complete-contract, strategy activate/convert, milestone, and EVA
  science. Batch 1 implements only the four sub-actions mapping to the M-B2 kinds
  `tech-unlock` / `facility-upgrade` / `kerbal-hire` / `kerbal-dismiss` (plus the passive
  `facility-refund` guard). The contract / strategy / milestone / science-transmit
  sub-actions are a KscAction batch 2, gated on the M-B2 oracle covering those facets
  end-to-end.
- **TimeJump propagate mode.** v1 uses `ExecuteJump` (epoch-shift, frozen relative
  positions) only. A `mode=propagate` arg selecting `ExecuteForwardJump` (orbits
  advance, the fast-forward contract) is deferred; the arg slot is reserved by the
  unknown-key-ignore rule so it is additive. This is why D9 `fast-forward` stays
  uncovered by M-C1.
- **Scripted rails-warp verb (R8 warp-reseed-lag).** `TimeJump` STOPS warp and
  epoch-shifts instantly (`TimeJumpManager.cs:323-326`), so it never exercises R8, the
  map-render lag seen when a real rails-warp is reseeded across a threshold. Covering R8
  needs a distinct verb that drives `TimeWarp` up, holds it, observes the render, and
  drives it back down - a separate warp-verbs batch. It is NOT a `TimeJump mode=`, because
  the regression is about the live warp coast, not a discrete jump.
- **InvokeRewind confirmation-dialog bypass.** The verb calls `StartInvoke` directly
  (past the "Fly" confirmation `PopupDialog`, `RewindInvoker.cs:452-473`), which is
  correct for automation. If a future scenario wants to exercise the confirmation dialog
  itself, that is an `AnswerMergeDialog`-style dialog verb, deferred.

### Open questions (RESOLVED by the review panel)

1. **KscAction funds-debit path for upgrade-facility and hire-kerbal. RESOLVED.**
   Split by sub-action. `upgrade-facility` invokes the REAL live SPACECENTER-scene
   `SpaceCenterBuilding.UpgradeFacility(bool)` instance (resolved by facility id among the
   scene's building MonoBehaviours), which produces the genuine stock funds debit and level
   bump - hence the SPACECENTER sub-gate. `hire-kerbal` does NOT have a headless stock debit
   (`KerbalRoster.HireApplicant` moves the applicant but does not charge funds; the stock
   debit is in the Astronaut Complex UI), so the verb MIRRORS the stock debit: the
   `CurrencyModifierQuery` affordability check plus `Funding.Instance.AddFunds(-hireCost,
   TransactionReasons.CrewRecruited)` (the `CrewRecruited` reason key is load-bearing -
   `KscActionExpectationClassifier.cs:140-148` keys the hire funds leg on it), with
   `hireCost` from the `GameVariables` recruit-cost curve, then `HireApplicant`. See KscAction
   Behavior.
2. **research-node stock-buy entry point. RESOLVED.** Drive `RDTech.ResearchTech()` (which
   spends science and fires `RDTech.UnlockTech`) and CONFIRM the effect via
   `RDTech.OperationResult` / the node's researched state before reporting OK. Part-availability
   propagation rides `UnlockTech` and is a downstream verifier-chain concern, not a verb
   precondition. The confirmation step is required because `TechResearchSpendPatch` can block a
   committed node (a guard-blocked call is `REJECTED msg=blocked-committed`, never a false OK).
3. **InvokeRewind completion when the marker was written but the seam crashed. RESOLVED.**
   Conservative is correct, and the reconcile framing is dropped. In the v1 harness INTERRUPTED
   is UNREACHABLE (the harness never restarts the seam mid-run, M-A5): a crashed rewind surfaces
   as KILLED (budget watchdog), and KILLED is terminal - `hlib.should_retry` never retries it, so
   the scenario is NOT re-staged or re-driven. The at-most-once property stands on the WAL's
   CLAIMED gate alone (`StartInvoke` fires at most once within the run). No
   `RecordingState`-reconcile and no marker-driven late-OK recovery is built;
   conservative-INTERRUPTED stays seam-side only as a defensive contract for the hypothetical
   restart.
4. **AnswerMergeDialog live-context capture vs. button-callback invocation. RESOLVED:
   button-callback invocation.** Find the live `PopupDialog` by `MergeDialog.DialogName` and
   invoke the chosen `DialogGUIButton`'s OWN callback (no `LiveDialogContext` stash). It survives
   all three `ShowTreeDialog` / `ShowPreSwitchDecisionDialog` overloads and cannot drift if a
   fourth button is added, because the seam never has to know what a button DOES. This is the
   M-A2-prescribed design (`design-autotest-command-seam.md:466-474`). The re-fly dialog kind is
   discriminated by `ActiveReFlySessionMarker != null` so a foreign "ParsekMerge" popup is never
   answered.
5. **D-token registry reconciliation. RESOLVED.** No new token is required. `TimeJump` credits
   D6 `time-jump` + D18 `time-jump-observables` (both already in `registry.toml`); D9
   `fast-forward` stays uncovered (the deferred propagate mode). The other verbs credit existing
   D8/D9 tokens. If a driver author later finds a genuinely absent token, it is added in the same
   PR per the growth rule.
