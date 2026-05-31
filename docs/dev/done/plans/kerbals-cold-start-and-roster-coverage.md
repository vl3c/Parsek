# Kerbals Cold-Start And Roster Coverage Plan

This plan covers the two remaining kerbal-test TODOs from
`docs/dev/todo-and-known-bugs.md`:

- `T62` cold-start kerbal slot migration integration coverage
- `T63` end-to-end `ApplyToRoster()` coverage for repaired historical stand-ins

## Investigation Summary

The current kerbal fixes are mostly correct in isolation, but the tests still stop one level short
of the actual restart path and one level short of the final roster-mutation step.

What already exists:

- `QuickloadResumeTests` proves `ParsekScenario.LoadCrewAndGroupState(...)` now initializes
  `LedgerOrchestrator.Kerbals` before `LoadSlots(...)`.
- `LedgerOrchestratorTests` covers the repair primitives individually:
  `MigrateKerbalAssignments(...)`, EVA-only end-state population, ghost-only chain end states, and
  stand-in reverse-mapping from persisted slots.
- `KerbalReservationTests` covers recomputation rules around displaced stand-ins, retirement, and
  permanent owner loss.

What is still missing:

- one test that runs the cold-start kerbal load sequence in the same order the scenario uses
- one test path that drives the final roster-application behavior instead of only the pre-roster
  reservation state
- one idempotence check proving reload/recompute does not oscillate between deleted / available /
  retired outcomes

## Current Code Anchors

The cold-start path is currently split across these methods:

- `ParsekScenario.LoadCrewAndGroupState(...)`
- `ParsekScenario.LoadExternalFilesAndRestoreEpoch(...)`
- `LedgerOrchestrator.OnLoad()`
- `LedgerOrchestrator.OnKspLoad(...)`
- `LedgerOrchestrator.RecalculateAndPatch()`
- `KerbalsModule.ApplyToRoster(...)`

The important behavior is real, but the code is not packaged in a way that is easy to test as one
coherent startup pipeline:

- `ParsekScenario.OnLoad(...)` is too Unity-heavy to unit-test directly in xUnit
- `LedgerOrchestrator.OnKspLoad(...)` does run the migration and recalculation path, but the test
  suite currently feeds it isolated recordings instead of a persisted save-shaped setup
- `KerbalsModule.ApplyToRoster(...)` mutates a live `KerbalRoster` directly, so most current tests
  stop at `PostWalk()` or private helper decisions like `ShouldEnsureChainEntryInRoster(...)`

## Proposed Direction

### 1. Start from existing seams, not a new synthetic startup helper

Do not begin by extracting a new “cold-start helper”. The current risk is over-synthesizing the
restart path and accidentally testing a cleaner sequence than the real one.

The first implementation pass should use the seams that already exist:

- `ParsekScenario.LoadCrewAndGroupState(...)`
- committed recording/tree loading inside `ParsekScenario.OnLoad(...)`
- `LedgerOrchestrator.OnKspLoad(...)`

Only extract a narrower helper if those seams prove too awkward after one test pass, and if that
helper is still called by the real `ParsekScenario.OnLoad(...)` path.

Non-negotiable constraint:

- any test meant to close `T62` must still depend on already-loaded committed recordings, because
  `MigrateKerbalAssignments()` derives desired rows from `RecordingStore.CommittedRecordings`
  through `CreateKerbalAssignmentActions(...)`

### 2. Build one shared "legacy broken save" fixture for restart coverage

Create one reusable fixture builder for the mixed old-save shape that motivated `T62`.

The fixture should include all of these at once:

- persisted `KERBAL_SLOTS` containing a stand-in chain for Jeb
- persisted `CREW_REPLACEMENTS`
- a committed recording whose saved `KerbalAssignment` row still points at a stand-in name
- an EVA-only committed recording with no vessel snapshot
- enough end-state data to prove the repaired rows converge to the same post-load reservation state

This fixture should be save-shaped, not helper-shaped. The point is to prove that the data loaded
from disk-like inputs converges correctly after the cold-start ordering, not just that individual
repair helpers work in isolation.

### 3. Add true cold-start integration tests around the real load seams

Recommended new test file:

- `Source/Parsek.Tests/KerbalLoadPipelineTests.cs`

Core scenarios:

1. Cold start with persisted slots, replacements, and stale stand-in rows repairs the ledger rows
   back to owner identity after the load path runs.
2. Cold start with EVA-only recordings populates crew end states and produces the same finite
   reservation result as commit-time action creation.
3. Cold start preserves the same slot chain and active occupant choice after load as the already
   initialized in-memory path.
4. Running the load path twice against the same repaired state does not create extra stand-ins,
   change retirement state, or rewrite equivalent rows again.

## `ApplyToRoster()` Coverage Strategy

`T63` is currently blocked more by testability than by missing logic.

The main gap is that `ApplyToRoster(...)` mixes three concerns:

- deciding what should exist in the roster
- deciding what should be retired vs deleted
- performing live `KerbalRoster` mutations

That makes the current tests good at policy coverage and weak at final outcome coverage.

### 4. Improve `ApplyToRoster(...)` testability without redefining the TODO target

Before adding more tests, split decision-heavy logic only where it helps wrapper-level verification.
The TODO still targets end-to-end `ApplyToRoster(...)` behavior, not only planner-level coverage.

Recommended shape:

- keep `ApplyToRoster(KerbalRoster roster)` as the imperative wrapper
- extract a pure or near-pure helper that computes a `RosterMutationPlan` / summary from:
  - current slots
  - reservations
  - retired stand-ins
  - "used in recording" state
  - current roster membership snapshot

The plan should answer:

- which stand-ins must exist
- which missing stand-ins must be recreated
- which displaced stand-ins must be kept as retired history
- which displaced stand-ins must be deleted
- which owner slots have no active occupant because the owner is permanently gone

Once that planning layer exists, the real `ApplyToRoster(...)` method becomes a thin adapter that
executes the plan against `KerbalRoster`.

### 5. Make wrapper-level `ApplyToRoster(...)` coverage mandatory for `T63`

At least one real wrapper-level test must remain part of the acceptance criteria for `T63`.

If xUnit can construct a usable `KerbalRoster`, add direct wrapper tests there.

If not, the TODO should not be considered closed without either:

- a wrapper-level smoke test in a runtime-capable harness, or
- an explicit in-game test that exercises `ApplyToRoster(...)` through the normal recalculation path

### 6. Add end-to-end roster-outcome tests

Recommended test files:

- `Source/Parsek.Tests/KerbalRosterApplyPlanTests.cs` for extracted decision helpers
- one wrapper-level coverage file or extension of `KerbalReservationTests.cs` for
  `ApplyToRoster(...)` itself

Scenarios to lock down:

1. A repaired historical stand-in that is missing from the roster is recreated as retired history,
   not deleted and not made assignable.
2. A displaced stand-in with no historical usage is deleted and stays deleted on the next
   recompute / reload cycle.
3. Permanent owner loss suppresses any active occupant even if old stand-ins still exist in the
   persisted chain.
4. A deeper historical stand-in remains retired while an earlier free occupant reclaims the slot.
5. Re-running the same plan against the already-mutated roster is idempotent.
6. At least one wrapper-level run proves the imperative method recreates / keeps / deletes the
   same stand-ins the planner predicts.

## Harness Requirements

Any new test coverage in this area should spell out the same shared setup instead of rediscovering
it ad hoc:

- `[Collection("Sequential")]`
- `LedgerOrchestrator.ResetForTesting()`
- `RecordingStore.ResetForTesting()`
- `CrewReservationManager.ResetReplacementsForTesting()`
- `GameStateStore.ResetForTesting()` when trait/baseline fallback matters
- `ParsekLog.TestSinkForTesting` when log assertions are part of the acceptance criteria
- `KspStatePatcher.SuppressUnityCallsForTesting = true` when `LedgerOrchestrator.OnKspLoad(...)`
  or `RecalculateAndPatch()` is exercised directly

## Acceptance Criteria

### `T62`

- a save-shaped fixture with persisted slots, replacements, and stale ledger rows runs through the
  real cold-start seams and converges to the repaired state
- the same fixture re-run proves the result is stable and non-oscillating
- the tests depend on real committed recordings loaded into `RecordingStore`, not only handcrafted
  desired action lists

### `T63`

- a repaired historical stand-in that must remain retired is proven through wrapper-level
  `ApplyToRoster(...)` coverage, not only planner-level logic
- a displaced unused stand-in is deleted and stays deleted on the next recompute / reload cycle
- permanent owner loss remains stable through the final roster-mutation step
- the TODO is not considered closed unless at least one wrapper-level imperative path is exercised

## Verification Plan

- Add one cold-start integration-style test fixture that runs through the real kerbal load seams.
- Add one second cold-start test that re-runs the same load after repair and proves the result is
  stable.
- Improve `ApplyToRoster(...)` testability and cover the final outcomes there, while keeping at
  least one wrapper-level test mandatory.
- Keep the existing targeted `LedgerOrchestratorTests` and `KerbalReservationTests`; do not delete
  them. They remain the fast guard rails for the smaller rules.
- After implementation, update the `T62` and `T63` TODO entries together because they are testing
  the same kerbal repair pipeline from opposite ends.

## Open Question

Can a lightweight `KerbalRoster` be constructed reliably in xUnit for direct wrapper coverage?

If yes, prefer that over broad new abstraction work.

If not, the plan must explicitly leave `T63` open until a runtime-capable wrapper test exists.
