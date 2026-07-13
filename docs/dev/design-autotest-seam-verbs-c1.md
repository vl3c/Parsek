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
  unchanged, MNA-at-epoch shifted by the delta, earlier chain tips auto-spawned),
  the S1.5 "warp past EndUT" step, and the R8 warp-reseed-lag regression.
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
  it defers until the merge popup exists, then invokes the chosen button.
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
                                            |   + AnswerMergeDialog: defer until popup |
                                            |   + TimeJump: RequiresFlight             |
                                            |   + KscAction: defer until career-ready  |
                                            +-----------------------------------------+
                                                             |
                                            journal CLAIMED  |  (WAL, at-most-once)
                                                             v
                                            +-----------------------------------------+
                                            | verb handler (thin Unity applier):       |
                                            |  InvokeRewind  -> RewindInvoker.CanInvoke |
                                            |                   -> StartInvoke (PENDING)|
                                            |  AnswerMergeDialog -> MergeDialog button  |
                                            |                       (in-callback, sync) |
                                            |  TimeJump      -> TimeJumpManager.Execute |
                                            |                   Jump (PENDING)          |
                                            |  KscAction     -> real stock API (sync)   |
                                            +-----------------------------------------+
                                                             |
                                            two-phase PENDING verbs hold the FIFO head
                                            until a pure completion decider settles:
                                              InvokeRewind: marker present after reload
                                              TimeJump:     UT reached + spawn settle
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

### DispatchState extension (`TestCommandDispatcher.cs:66`)

Three new bits are sampled by the addon per poll (in `BuildDispatchState`,
`ParsekTestCommandAddon.cs:887`) and read by the pure `DecideDispatch`:

```
struct DispatchState                      // existing fields unchanged
    ...
    bool MergeDialogPresent;   // ParsekScenario.MergeDialogPending (a live ParsekMerge popup exists)
    bool MergeJournalInFlight; // ParsekScenario.Instance.ActiveMergeJournal != null
    bool CareerPresent;        // career singletons live (Funding/RnD/roster present)
```

- `MergeDialogPresent` mirrors the existing `ParsekScenario.MergeDialogPending` flag
  the dialog sets when it spawns (`MergeDialog.cs:270`) and its `OnDismiss` clears
  (`MergeDialog.cs:288`). It is the bounded-wait signal for `AnswerMergeDialog`.
- `MergeJournalInFlight` mirrors `ParsekScenario.ActiveMergeJournal`
  (`ParsekScenario.cs:81`), the persisted re-fly merge journal. A non-null journal
  means a re-fly merge is mid-finalize; `InvokeRewind` must refuse.
- `CareerPresent` is the `KscAction` readiness bit: `HighLogic.CurrentGame != null &&
  HighLogic.CurrentGame.Mode == Game.Modes.CAREER` and the relevant singleton for the
  sub-action is live (`Funding.Instance`, `ResearchAndDevelopment.Instance`, or the
  roster). Sampling detail is the addon's; the pure decider only reads the bool.

These are ADDITIVE fields on a struct the pure tests build directly; existing rows in
the M-A2 dispatch matrix are unaffected (they leave the new bits default-false, which
none of the ten existing verbs read).

### Per-verb precondition rows (`TestCommandDispatcher.cs:125`)

New entries in the `Preconditions` table:

| verb | VerbSceneRequirement | extra guards in DecideDispatch |
|---|---|---|
| `InvokeRewind` | `RequiresFlight` | refuse `merge-journal-in-flight` (MergeJournalInFlight); refuse `load-in-flight` (LoadInFlight); refuse `recording-active` (Recording) |
| `AnswerMergeDialog` | `RequiresFlight` | defer `no-merge-dialog` while !MergeDialogPresent |
| `TimeJump` | `RequiresFlight` | (none beyond the scene gate) |
| `KscAction` | `AnyScene` | defer `career-not-ready` while !CareerPresent |

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

The addon RESOLVES the live target (an `RDTech`, an `UpgradeableFacility`, a
`ProtoCrewMember`) and reads the current cost / balance / level, passes the primitives
here, and this pure decider returns accept-or-typed-refusal. The applier then invokes
the real stock API only on accept. The `ManifestKind` field is the seam-declared
manifest kind the harness attaches (Behavior below); the verb never writes a ledger
row.

## Behavior

### Where the new logic lives

- Pure core: `TestCommandRewind.cs`, `TestCommandTimeJump.cs`,
  `TestCommandKscAction.cs`, the new `DecideDispatch` rows + `DispatchState` fields in
  `TestCommandDispatcher.cs`, the verb-table move in `TestCommandVerbs.cs`, the new
  deferral budgets in `DeferralBudget.cs` (`TestCommandDispatcher.cs:214`).
- Thin addon: four new verb bodies + four `ITestCommandExecutor` methods on
  `ParsekTestCommandAddon` (`ParsekTestCommandAddon.cs:87` interface,
  `:934` explicit implementations, `:954` `InvokeExecutor` switch). The two-phase
  completion for `InvokeRewind` and `TimeJump` hooks into the existing
  `TryCompleteTwoPhase` dispatch (`ParsekTestCommandAddon.cs:684`), which already
  branches by `completionVerb`.
- One internal-ization outside the seam: `MergeDialog.DialogName` becomes
  `internal const` (it is `private const` today, `MergeDialog.cs:11`) plus a new
  internal `MergeDialog.TryAnswerLiveDialog` entry point (below). This is the phase-3
  change the M-A2 doc already anticipated (`design-autotest-command-seam.md:466-474`).

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

**At-most-once, and what InvokeRewind needs beyond the base WAL.** The re-fly is
irreversible (it creates a provisional fork, writes a `ReFlySessionMarker`, recalcs the
ledger, and mutates the save, `RewindInvoker.cs:891-960`). The base WAL contract is
sufficient and no new mechanism is added, but the reasoning is subtle enough to state:

- The side effect (`StartInvoke`) runs ONLY after CLAIMED durably lands
  (`ParsekTestCommandAddon.cs:577-584`). A crash mid-load leaves the id at CLAIMED; on
  restart `DecideRecovery` (`TestCommandJournal.cs:282`) returns `Interrupted` and the
  addon writes `INTERRUPTED` WITHOUT re-invoking. The rewind is never fired twice.
- The base WAL's EXECUTED-response-rewrite recovery (`TestCommandJournal.cs:331`,
  re-emitting the ORIGINAL OK payload) does NOT apply to `InvokeRewind`, and this is
  the one place it differs from `LoadGame`. The terminal OK payload (the marker
  session id, the active pid) is not known until `ConsumePostLoad` writes the marker,
  which happens ACROSS the scene reload and is driven by KSP's `OnLoad`, NOT by the
  seam's EXECUTED transition. So the journal is only ever CLAIMED (side effect
  initiated) or a single terminal (EXECUTED+DONE written together at completion). There
  is no window where EXECUTED is written but the response is not, so there is nothing to
  rewrite. Consequently a crash anywhere before the terminal always reports
  INTERRUPTED, even if the rewind actually completed (the marker landed but the seam
  never observed it). This is deliberately conservative: INTERRUPTED means "unknown
  outcome, reconcile," and the orchestrator reconciles by reading `RecordingState` /
  inspecting the marker on the next boot. The safety property that matters (never
  re-invoke a rewind) holds unconditionally because the seam never re-calls
  `StartInvoke` from CLAIMED.

**Interaction with in-flight recordings and the merge journal.** The `recording-active`
dispatch refusal keeps a live recorder from being silently discarded by the reload. The
`merge-journal-in-flight` refusal keeps a rewind from racing a re-fly merge's crash-
recovery finisher. Both are dispatch-level (they never reach the CLAIMED side effect).

### AnswerMergeDialog (bounded-wait, in-callback, irreversible)

**Args.** `choice=<merge|discard|seal>`. `merge` (alias `commit`) invokes the Commit
button; `discard` invokes Discard; `seal` invokes the Merge-and-Seal button that only
exists on a not-yet-sealable re-fly dialog (`MergeDialog.cs:237-248`). An unrecognized
choice -> `REJECTED msg=unknown-choice`.

**Dispatch (bounded wait).** `RequiresFlight`. `DecideDispatch` defers with
`no-merge-dialog` while `!MergeDialogPresent`. This is the not-yet-spawned-dialog race:
a re-fly reload lands, the merge dialog spawns a moment later (`MergeDialog.cs:271`),
and the command DEFERS at the FIFO head until it appears, then Executes. If the dialog
never appears, the deferral budget converts it to TIMEOUT
(`ParsekTestCommandAddon.cs:859`), so a mis-sequenced answer never wedges the run. This
is the same head-defer mechanism `StartRecording` uses to wait for FLIGHT
(`design-autotest-command-seam.md:454`).

**Execution (sync, in-callback).** On Execute the body calls a new internal
`MergeDialog.TryAnswerLiveDialog(MergeDialogChoice choice, out string reason)`:
- The dialog's button lambdas run their action INSIDE the `DialogGUIButton` callback
  (`MergeDialog.cs:240-263`: Commit -> `MergeCommit`, Discard -> `MergeDiscard`,
  Merge-and-Seal -> `MergeCommit(..., playerRequestedSeal: true)`). Per the project's
  deferred-field PopupDialog callback trap (MEMORY:
  project_deferred_field_popup_callback_trap), the answer MUST invoke the chosen
  action DIRECTLY, NEVER set a `pendingChoice` field that a `DrawWindow` reads a frame
  later. `TryAnswerLiveDialog` invokes the captured action synchronously in the command
  execution frame and lets `MergeCommit` / `MergeDiscard` dismiss the popup as they do
  today (`MergeDialog.cs:268`).
- To invoke the captured action without reconstructing its `(tree, decisions,
  spawnCount)` arguments, `MergeDialog` stashes a `LiveDialogContext` (the same
  captured locals the button lambdas close over, plus which buttons the spawned dialog
  actually has) into a static when it spawns the popup (`MergeDialog.cs:271`), and
  clears it in the popup `OnDismiss` (`MergeDialog.cs:286`). `TryAnswerLiveDialog`
  reads that context and calls the matching handler. This is NOT a pending field the
  draw loop polls; it is a synchronous invoke off a captured context, which is the
  distinction the trap warns about.
- A `seal` choice against a 2-button (already-sealable) dialog -> the context reports no
  seal button -> `ERROR msg=choice-unavailable`. A flag-set-but-no-context race (the
  dialog dismissed between the dispatch sample and the execute) -> `ERROR
  msg=no-live-dialog`.

**Payload.** `OK choice=<choice> result=<committed|discarded|sealed>`.

**At-most-once composition.** The merge answer is irreversible (a commit writes
supersede rows + may open a `MergeJournal`; a discard tears down a subtree,
`MergeDialog.cs`). It leans on TWO recovery systems, and the composition is the point:
- The seam WAL: CLAIMED lands before the button is invoked; a crash mid-commit leaves
  CLAIMED -> INTERRUPTED on restart, never re-invoked.
- The merge journal's own crash-recovery: a `MergeCommit` that opened a `MergeJournal`
  and then crashed is driven forward-or-back on the next `OnLoad` by
  `MergeJournalOrchestrator.RunFinisher` (`MergeJournalOrchestrator.cs`), INDEPENDENT of
  the seam. So the seam guarantees "the answer button is invoked at most once," and the
  merge journal guarantees "a half-done merge reaches a consistent terminal." The seam
  does NOT try to re-drive the merge; it reports INTERRUPTED and the merge journal
  finishes the work.

### TimeJump (two-phase, forward-only, epoch-shift)

**Args.** Exactly one of `ut=<absoluteUT>` or `deltaSeconds=<positive-delta>`,
InvariantCulture floats, percent-encoded. Both absent or both present ->
`REJECTED msg=missing-jump-target`.

**Dispatch.** `RequiresFlight` (the epoch-shift jump operates on loaded vessels and
`FlightGlobals`). No extra guard beyond the scene gate.

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

**Two-phase completion.** `SetUniversalTime` lands the clock synchronously, but the
spawn-queue drain and the warp-reseed-lag (R8) settle over a few subsequent frames, so
the terminal response is deferred. The `TimeJump` branch of `TryCompleteTwoPhase`
routes `elapsed`, `Planetarium.GetUniversalTime()`, the captured target, the fixed
tolerance, and a settle-frame countdown through
`TestCommandTimeJump.DecideJumpCompletion`:
- `StillWaiting`: UT not yet within tolerance, or the settle window has not elapsed.
- `CompleteOk`: UT within tolerance AND settle frames drained. Terminal `OK`, payload
  `ut=<reachedUT> target=<target> delta=<target-startUT>`.
- `JumpTimeout`: budget expired without reaching the target (should not happen for a
  synchronous jump, but bounds a pathological SetUniversalTime failure). Terminal
  `ERROR msg=jump-timeout`.

**At-most-once.** A time jump mutates the world clock and epoch-shifts orbits
(irreversible in-place). The WAL protects it identically: CLAIMED before `ExecuteJump`;
a crash mid-jump leaves CLAIMED -> INTERRUPTED, never re-jumped. Unlike `InvokeRewind`,
the jump does NOT straddle a scene reload, so it CAN ride the EXECUTED response-rewrite
recovery (the OK payload is known at completion, all within one scene), exactly like
`RunTests`.

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

**Dispatch.** `AnyScene` with a `career-not-ready` defer while `!CareerPresent` (the
game is not a career with the sub-action's singleton live). Career actions are normally
scripted at SPACECENTER, but the stock APIs work headlessly in any scene with the
singletons present, so the gate is career-readiness, not a specific scene. A never-
career game deferring here converts to TIMEOUT and the orchestrator reconciles.

**The verb performs the REAL stock action.** KscAction never writes a ledger row; it
invokes the exact stock method the player's UI would, so the Parsek patches and the
ledger recalc observe the change organically (ERS/ELS routing note; the ledger reads
KSP's own state, `docs/dev/design-autotest-ledger-oracle.md`). Per sub-action, after
the pure `TestCommandKscAction.Decide` accepts:

| sub-action | stock API invoked (observed by) | manifest kind |
|---|---|---|
| `research-node` | resolve the `RDTech` for the node, drive the stock research-buy path `RDTech.ResearchTech()` (spends science) which fires `RDTech.UnlockTech` -- observed by `TechResearchSpendPatch` (`RDTech.ResearchTech`, `TechResearchSpendPatch.cs:17`) + `TechResearchPatch` (`RDTech.UnlockTech`, `TechResearchPatch.cs:14`) | `tech-unlock` |
| `upgrade-facility` | resolve the `UpgradeableFacility` by id, drive the funds-spending upgrade so both the level bump `UpgradeableFacility.SetLevel` (`FacilityUpgradePatch.cs:13`) and the funds debit `SpaceCenterBuilding.UpgradeFacility(bool)` (`FacilityUpgradeSpendPatch.cs:15`) are the real stock ones | `facility-upgrade` |
| `hire-kerbal` | resolve the applicant from `CrewRoster.Applicants`, `KerbalRoster.HireApplicant(ProtoCrewMember)` (`KerbalHirePatch.cs:19-22`) plus the stock hire-cost funds debit | `kerbal-hire` |
| `dismiss-kerbal` | resolve the crew member, `KerbalRoster.Remove(ProtoCrewMember)` (`KerbalDismissalPatch.cs:16-18`) | `kerbal-dismiss` |

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
- `upgrade-facility`: `unknown-facility`, `facility-at-max` (already top level),
  `insufficient-funds`.
- `hire-kerbal`: `unknown-kerbal` / `kerbal-not-applicant` (not in the applicant pool),
  `insufficient-funds` (hire cost exceeds funds).
- `dismiss-kerbal`: `unknown-kerbal` / `kerbal-not-dismissable` (assigned / tourist /
  protected).

Each refusal is a REFUSAL, not a Parsek fault: the orchestrator mis-sequenced (it did
not ensure funds / science, or named a wrong id). The verifier chain still judges
whether Parsek accounted whatever DID happen.

**Payload.** `OK action=<action> target=<target> applied=true` plus an observed-after
field for logging only (`scienceAfter` / `fundsAfter` / `level` / `crewCount`). The
observed-after values are for the KSP.log / debugging; they are NEVER the oracle's
source of truth (the manifest amounts are author constants, M-B2 independence
invariant).

**Seam-declared manifest annotation.** The DRIVER (harness `run.py`), when it emits a
`KscAction` step, attaches the corresponding manifest entry to the run manifest
(`harness/results/<runId>.manifest.json`, M-B2) with `provenance = "seam-declared"` and
AUTHOR-CONSTANT amounts (funds / science deltas the scenario author computed from the
node / facility / hire cost). The verb does not compute or write these; the seam-
declared entry is the oracle's `compute_expected` input, and the stock-log capture
cross-checks via `unmatched_captured_awards`. The verb's only oracle-facing
contribution is causing the real KSP state change the log capture and the Parsek recalc
both observe. A `KscAction` step's manifest kind is the `ManifestKind` in the table
above.

### M-A5 driver integration (budgets, INVALID subkinds)

Deferral budgets (`DeferralBudget.BudgetSeconds`, `TestCommandDispatcher.cs:238`) gain
four rows:

| verb | deferral budget | rationale |
|---|---|---|
| `InvokeRewind` | rewind budget (~300 s, LoadGame-class) | a re-fly copies a quicksave, reloads the scene, and runs `ConsumePostLoad`; sized like `LoadGame` (`TestCommandDispatcher.cs:220`) |
| `AnswerMergeDialog` | dialog-wait budget (default 60 s) | bounded wait for the merge popup to spawn after a reload; the default covers scene settle |
| `TimeJump` | jump budget (~120 s) | the jump is synchronous but the settle + ledger recalc want a bound; well under an infinite hang |
| `KscAction` | default 60 s | covers the career-ready wait; the action itself is immediate |

Verdict-to-harness-classification (a REFUSAL is always driver-INVALID retry-once, never
PARSEK-FAIL):

| verb outcome | harness classification |
|---|---|
| `InvokeRewind REJECTED msg=refly-gate ...` | INVALID(driver-gate), retry-once |
| `InvokeRewind ERROR msg=rewind-failed / rewind-timeout` | INVALID(driver-rewind), retry-once |
| `AnswerMergeDialog TIMEOUT msg=no-merge-dialog` | INVALID(driver-dialog), retry-once |
| `AnswerMergeDialog ERROR msg=choice-unavailable / no-live-dialog` | INVALID(driver-dialog), retry-once |
| `TimeJump REJECTED msg=backward-jump / missing-jump-target` | INVALID(driver-arg), retry-once |
| `KscAction REJECTED msg=insufficient-* / unknown-* / *-already-*` | INVALID(driver-career), retry-once |
| any verb `OK` but the verifier chain reds the produced save | PARSEK-FAIL (orthogonal) |

The subkind names above are proposed for `hlib`'s `RETRYABLE_INVALID_SUBKINDS`; their
exact spelling is a harness-side (M-A5) wiring detail reconciled when the driver steps
are added, not a Parsek contract.

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
- `TimeJump`: D9 `fast-forward`; D8 `epoch-isolation`, `recalc-engine`.
- `KscAction`: D8 `science`, `funds`, `kerbals`, `facilities`, `ksp-state-patcher`,
  `orchestrator`, `recalc-engine`, `action-blocking` (the R6/R7 passive guards).

If, when the driver steps are authored, a token proves genuinely absent from the
registry, it is added in the SAME PR per the growth rule; none is anticipated.

### Example scenario spec fragments (one per verb)

These match the M-B1 `steps` array shape (`design-autotest-mission-library.md:213`); a
`cmd`-kind seam step per M-C1 verb. Substitutions (`${runSave}`) are the harness's.

InvokeRewind (a re-fly of an injected tree with a Crashed sibling + RP, fixture block
B9):

```toml
[driver]
kind  = "seam"
steps = [
  { cmd = "LoadGame",     args = { save = "${runSave}", name = "persistent" }, expect = "OK", budget = 240 },
  { cmd = "SetSetting",   args = { name = "autoRecordOnLaunch", value = "false" }, expect = "OK" },
  { cmd = "InvokeRewind", args = { rp = "rp_b9_root", slot = "1" }, expect = "OK", budget = 300 },
  { cmd = "AnswerMergeDialog", args = { choice = "merge" }, expect = "OK", budget = 60 },
  { cmd = "CommitTree",   expect = "OK" },
  { cmd = "FlushAndQuit", expect = "OK" },
]
[expectations.recordings]
count = { min = 1 }
[expectations.logContracts]
required  = ["Re-Fly (Rewind-to-Separation) StartInvoke", "Invocation complete"]
forbidden = ["\\[Parsek\\]\\[ERROR\\]"]
```

AnswerMergeDialog appears as the step immediately after `InvokeRewind` above (the
bounded-wait defer absorbs the reload-to-dialog gap).

TimeJump (warp past a recording EndUT, S1.5 / S4.8):

```toml
  { cmd = "TimeJump", args = { deltaSeconds = "600.0" }, expect = "OK", budget = 120 },
```

KscAction (research a node so the ledger oracle cross-checks, L1):

```toml
  { cmd = "KscAction", args = { action = "research-node", node = "basicRocketry" }, expect = "OK", budget = 60 },
```

with the seam-declared manifest entry the DRIVER attaches for that step:

```toml
[[expectations.ledger.manifest]]
  ut         = 0.0
  kind       = "tech-unlock"
  science    = -45.0          # author constant: the node cost; NEVER from Parsek recalc
  provenance = "seam-declared"
```

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
5. **InvokeRewind crashes mid-reload.** Journal at CLAIMED -> INTERRUPTED on restart;
   never re-invoked. The rewind may have partially or fully completed; the orchestrator
   reconciles via `RecordingState` / marker inspection. v1.
6. **InvokeRewind completes but Parsek recorded the fork wrong.** Verb is `OK`; the
   verifier chain (analyzer / ledger oracle) reds the produced save -> PARSEK-FAIL,
   orthogonal to the verb verdict. v1.
7. **AnswerMergeDialog before the dialog spawns.** Defers `no-merge-dialog` at the FIFO
   head until the popup appears, then Executes; or TIMEOUT if it never appears. v1.
8. **AnswerMergeDialog choice=seal on a 2-button dialog.** The live context reports no
   seal button -> `ERROR msg=choice-unavailable`. v1.
9. **AnswerMergeDialog: flag set but the dialog dismissed between sample and execute.**
   `TryAnswerLiveDialog` finds no live context -> `ERROR msg=no-live-dialog`. v1.
10. **AnswerMergeDialog crashes mid-commit.** Seam WAL -> INTERRUPTED (never re-invokes
    the button); the merge journal's `RunFinisher` drives the half-done merge to a
    consistent terminal on the next OnLoad, independent of the seam. v1.
11. **TimeJump backward or zero delta.** `IsForwardJump` false ->
    `REJECTED msg=backward-jump`. v1.
12. **TimeJump with both ut and deltaSeconds (or neither).** `ResolveTargetUt` errors
    -> `REJECTED msg=missing-jump-target`. v1.
13. **TimeJump outside FLIGHT.** Dispatch defers `not-in-flight` until FLIGHT or its
    budget expires. v1.
14. **TimeJump then InvokeRewind (composition).** The jump leaves no seam residual (no
    marker, no context); the following `InvokeRewind` sees a clean gate. "Jump then
    rewind leaves no state" (S4.8). v1.
15. **TimeJump crashes mid-jump.** Journal at CLAIMED -> INTERRUPTED; never re-jumped
    (SetUniversalTime + epoch-shift is not idempotent). v1.
16. **KscAction unknown action arg.** `ParseKind` returns `Unknown` ->
    `REJECTED msg=unknown-action`. v1.
17. **KscAction insufficient funds / science.** The pure `Decide` refuses before the
    stock call -> `REJECTED msg=insufficient-funds` / `insufficient-science`; no state
    touched. Driver-INVALID. v1.
18. **KscAction research a node already unlocked (idempotency).** `Decide` returns
    `node-already-unlocked` -> REJECTED, no double-unlock. v1.
19. **KscAction on a non-career game.** Dispatch defers `career-not-ready` (no
    `Funding`/`RnD`/roster singleton) until a career loads or TIMEOUT. v1.
20. **KscAction upgrade-facility then scene change (R6 passive guard).** The upgrade is
    real (funds debited, level bumped); a following `LoadGame` scene change must NOT
    produce a phantom `facility-refund`. The verb exercises the setup; the ledger
    oracle asserts no phantom refund. v1 (the guard is the assertion, not the verb).
21. **A reserved verb (one of the other eleven) arrives.** Still
    `REJECTED msg=not-implemented-v1` (`TestCommandVerbs.cs` unchanged for them). v1.
22. **A v0 addon (pre-M-C1) receives InvokeRewind.** It rejects `not-implemented-v1`;
    the orchestrator detects the capability gap and does not confuse it with a typo.
    Backward-compatible. v1.
23. **Two-phase verb budget expiry (InvokeRewind / TimeJump never settles).** The
    existing `TryCompleteTwoPhase` budget path converts to `ERROR` (rewind-timeout /
    jump-timeout), advancing the head so the run is not wedged. v1.
24. **KscAction hire cost debit path needs a UI context.** Open question (below): the
    stock hire-cost funds debit lives in the AstronautComplex UI, not in
    `HireApplicant`. v1 flags this as a resolution to confirm live; the verb debits the
    hire cost through the same `Funding` path so the ledger observes a real debit.

## What Doesn't Change

- No recording format, schema generation, sidecar, tree, ledger, or save-file field
  changes. `RecordingStore.CurrentRecordingFormatVersion` /
  `CurrentRecordingSchemaGeneration` are untouched; no migration path is added.
- No wire-protocol change. The command line grammar, percent-encoding, response
  grammar, journal grammar, lock grammar, and verdict set are exactly M-A2's. The four
  verbs use only verb-specific args M-A2 already allows.
- No gameplay behavior in normal play. The addon stays env-gated
  (`PARSEK_TEST_COMMANDS=1`, `ParsekTestCommandAddon.cs:158`) and inert otherwise; the
  four verbs are reachable only through the armed channel. `MergeDialog.DialogName`
  going `private -> internal const` and the new `TryAnswerLiveDialog` /
  `ParsekFlight.TimeJumpTo` methods are dormant unless the addon calls them.
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
- `MergeDialog.DialogName` widening to `internal const` and the new internal helpers do
  not alter any serialized shape or public API.
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
- `AnswerMergeDialog`: `Info` "answermergedialog choice=<choice> result=<result>"; on
  the bounded-wait defer the standard rate-limited "dispatch id=<id> -> DEFER
  reason=no-merge-dialog"; on failure `Warn` "answermergedialog failed
  reason=<choice-unavailable|no-live-dialog>".
- `TimeJump`: `Info` "timejump start target=<ut> delta=<s>s" and "timejump complete
  reachedUT=<ut>"; on refuse `Warn` "timejump refused reason=<backward-jump|
  missing-jump-target>".
- `KscAction`: `Info` "kscaction action=<action> target=<target> applied=true
  manifestKind=<kind> observedAfter=<value>"; on refuse `Warn` "kscaction refused
  action=<action> reason=<...> target=<target>".

Dispatch decisions reuse the M-A2 lines (`TestCommandDiagnostics.DispatchExecute /
DispatchReject / DispatchDefer / DispatchInterrupted`, `ParsekTestCommandAddon.cs:507`),
so the four new refusals / defers ("merge-journal-in-flight", "no-merge-dialog",
"career-not-ready", "backward-jump", the refly-gate reason) appear in the standard
"dispatch id=<id> -> REJECT/DEFER reason=<...>" shape.

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
  ready -> Execute. `AnswerMergeDialog` with `!MergeDialogPresent` -> Defer(no-merge-dialog);
  with the dialog present -> Execute. `TimeJump` outside FLIGHT -> Defer; in FLIGHT ->
  Execute. `KscAction` with `!CareerPresent` -> Defer(career-not-ready); ready -> Execute.
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
  `unknown-kerbal`, `kerbal-not-applicant`, `kerbal-not-dismissable`) fires on its
  boundary. The `ManifestKind` is the right kind per sub-action. Fails if an
  unaffordable action is admitted (a false OK), or a refusal maps to the wrong reason.
- **Merge-choice mapper.** `merge` / `commit` / `discard` / `seal` map to the right
  `MergeDialogChoice`; an unknown choice -> reject. Fails if a choice string drift
  invokes the wrong button.
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
  and reason; each new dispatch branch (merge-journal-in-flight, no-merge-dialog,
  career-not-ready, backward-jump, refly-gate) is non-silent. Fails if a decision branch
  is a debugging blind spot or a marker format drifts.

### In-game tests (InGameTests, live KSP only)

Delivered as automated in-game tests PLUS a PENDING-OPERATOR runbook (an agent cannot
pilot KSP):

- With `PARSEK_TEST_COMMANDS` unset, the addon performs no file access and the four new
  verbs are never reachable (inert). Fails if the shipped default is not inert.
- A file-channel `InvokeRewind` -> `AnswerMergeDialog merge` -> `RecordingState`
  sequence on an injected B9 tree (Crashed sibling + RP) drives a real re-fly to a
  merged tree in FLIGHT and the following `RecordingState` reflects it. Fails if the
  two-phase completion, the bounded-wait dialog answer, or the in-callback invoke is
  wrong. (PENDING-OPERATOR.)
- A `TimeJump deltaSeconds=600` from a known FLIGHT state lands the clock within
  tolerance and (per S4.8) leaves SMA/ecc/inc unchanged, MNA-at-epoch shifted by the
  delta, relative positions frozen, and earlier chain tips auto-spawned. The
  assertions are the verifier chain's; the verb test asserts the terminal `OK ut=`.
  (PENDING-OPERATOR for the physics assertions.)
- A `KscAction research-node` on a career fixture unlocks the node, spends science, and
  the ledger oracle cross-checks the recalc against the seam-declared `tech-unlock`
  manifest entry. Repeat for `upgrade-facility` (R6 refund guard after a following
  `LoadGame`), `hire-kerbal`, `dismiss-kerbal`. Fails if the stock action does not trip
  the Parsek patch (grep the patch's own log line to confirm the mutation, per MEMORY:
  verify-harness-seeder-mutation), or the ledger mis-accounts. (PENDING-OPERATOR.)

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
  unknown-key-ignore rule so it is additive.
- **InvokeRewind confirmation-dialog bypass.** The verb calls `StartInvoke` directly
  (past the "Fly" confirmation `PopupDialog`, `RewindInvoker.cs:452-473`), which is
  correct for automation. If a future scenario wants to exercise the confirmation dialog
  itself, that is an `AnswerMergeDialog`-style dialog verb, deferred.

### Open questions for the review panel

1. **KscAction funds-debit path for upgrade-facility and hire-kerbal.** The stock funds
   debit for a facility upgrade lives in `SpaceCenterBuilding.UpgradeFacility(bool)`
   (`FacilityUpgradeSpendPatch.cs:15`), a MonoBehaviour instance method that normally
   runs from the KSC building UI; the hire-cost debit lives in the AstronautComplex UI,
   not in `KerbalRoster.HireApplicant`. Can the verb drive those UI-context methods
   headlessly (to get the REAL debit the Parsek spend patch observes), or must it invoke
   `UpgradeableFacility.SetLevel` / `HireApplicant` and debit the cost through
   `Funding.Instance` explicitly (a faithful mirror, but not literally the stock UI
   path)? The former is "the real stock action" the design prefers; the latter is a
   pragmatic fallback. This needs a decompile of `SpaceCenterBuilding.UpgradeFacility` /
   the AstronautComplex hire flow to confirm headless invokability, and it decides
   whether `FacilityUpgradeSpendPatch` / the hire funds accounting fire.
2. **research-node stock-buy entry point.** Does calling `RDTech.ResearchTech()` on a
   node constructed from the tech id reproduce the full RDController buy (science spend +
   `UnlockTech` + part availability), or does the RDController do additional work the
   patches rely on? A decompile of `RDController.PartUnlock` / the research-buy path
   confirms the exact call sequence that trips both `TechResearchSpendPatch` and
   `TechResearchPatch` without a live RDController.
3. **InvokeRewind completion when the marker was written but the seam crashed.** The
   design reports INTERRUPTED (conservative) rather than reconstructing OK, because the
   OK payload is not known until the marker lands across the reload. Is conservative-
   INTERRUPTED acceptable to the M-A5 harness (it reconciles via `RecordingState`), or
   should the seam attempt a marker-driven recovery on restart (read the marker, emit a
   late OK)? The former is simpler and safe; the latter is more precise but adds a
   marker-to-payload recovery path.
4. **AnswerMergeDialog live-context capture vs. button-callback invocation.** The design
   stashes a `LiveDialogContext` (the captured `tree` / `decisions` / `spawnCount`) when
   the dialog spawns and invokes the handler synchronously. An alternative reads the
   live `PopupDialog` by `DialogName` and invokes the matching `DialogGUIButton`'s own
   callback directly (no separate stash). The M-A2 note (`design-autotest-command-seam.md:466-474`)
   says "invoke the chosen button's action directly." Which is cleaner and less likely
   to drift from the dialog's own button wiring if `MergeDialog` adds a fourth button?
5. **D-token registry reconciliation.** The verbs unlock existing D8/D9 tokens; if the
   driver authors find a genuinely missing token (e.g. a distinct `time-jump` token
   separate from `fast-forward`), it is added per the growth rule. Confirm no new token
   is required before wiring the driver steps.
