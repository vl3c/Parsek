# Kerbals Events/Actions Recording Audit (2026-04-13)

## Scope

Reviewed the kerbal-related parts of the events/actions recording path, with emphasis on the crew reservation and replacement-chain system:

- `Source/Parsek/GameActions/LedgerOrchestrator.cs`
- `Source/Parsek/KerbalsModule.cs`
- `Source/Parsek/CrewReservationManager.cs`
- `Source/Parsek/ParsekScenario.cs`
- `Source/Parsek/GameStateRecorder.cs`
- `Source/Parsek/Patches/CrewAutoAssignPatch.cs`
- `Source/Parsek/Patches/CrewDialogFilterPatch.cs`
- `Source/Parsek/Patches/KerbalDismissalPatch.cs`

Design/docs reviewed:

- `docs/parsek-game-actions-and-resources-recorder-design.md`
- `docs/dev/done/game-actions/kerbals-module/task-2-reservation-and-chains.md`
- `docs/dev/done/game-actions/kerbals-module/task-3-apply-to-roster-and-integration.md`
- `docs/dev/done/game-actions/game-actions-deferred.md`
- `docs/dev/todo-and-known-bugs.md`
- `CHANGELOG.md`

## Verification

Ran:

```powershell
dotnet test --filter "FullyQualifiedName~Kerbal|FullyQualifiedName~CrewAutoAssign|FullyQualifiedName~KscCrewSwap|FullyQualifiedName~LedgerOrchestrator"
```

Result: 181 passed, 0 failed, 0 skipped.

There were post-build copy warnings because `GameData\Parsek\Plugins\Parsek.dll` was locked, but the test run itself completed successfully.

## Findings

### 1. High: `CreateKerbalAssignmentActions` reintroduces the stand-in identity bug fixed in `PopulateCrewEndStates`

Code:

- `Source/Parsek/GameActions/LedgerOrchestrator.cs:297-381`
- `Source/Parsek/KerbalsModule.cs:401-420`
- `CHANGELOG.md:224`

What happens:

- `KerbalsModule.PopulateCrewEndStates()` explicitly reverse-maps stand-in names back to the original slot owner before building `CrewEndStates`.
- `LedgerOrchestrator.CreateKerbalAssignmentActions()` then re-extracts crew names from the recording snapshot, but `ExtractCrewFromRecording()` does not do the same reverse-map.
- The action is emitted with `KerbalName = stand-in`, while `CrewEndStates` is keyed by `original owner`.
- The subsequent `TryGetValue(name, out endState)` lookup misses and leaves the action at `Unknown`.

Why this is a bug:

- This is the same identity mismatch that `#254` fixed for end-state inference, but it still exists in action creation.
- The ledger can reserve the stand-in indefinitely instead of the original kerbal, because `KerbalsModule.ProcessAction()` treats `Unknown` as an open-ended temporary reservation (`Source/Parsek/KerbalsModule.cs:175-214`).

Concrete scenario:

- Jeb is reserved by an earlier committed recording.
- Leia is generated as Jeb's stand-in and appears in a later recording snapshot.
- `PopulateCrewEndStates()` reverse-maps Leia back to Jeb.
- `CreateKerbalAssignmentActions()` still emits `KerbalAssignment(KerbalName=Leia, EndState=Unknown)`.
- Recalculation now reserves Leia instead of Jeb, which can create a duplicate replacement cascade and break chain ownership.

Expected behavior:

- `CreateKerbalAssignmentActions()` needs the same reverse-map that `PopulateCrewEndStates()` already applies, so the emitted action and the stored `CrewEndStates` agree on the same kerbal identity.

### 2. High: cold-start load drops persisted `KERBAL_SLOTS`

Code:

- `Source/Parsek/ParsekScenario.cs:606`
- `Source/Parsek/ParsekScenario.cs:1487-1494`
- `Source/Parsek/ParsekScenario.cs:1516-1525`
- `Source/Parsek/ParsekScenario.cs:1172`

Design note:

- `docs/dev/done/game-actions/kerbals-module/task-3-apply-to-roster-and-integration.md:331-346`

What happens:

- `ParsekScenario.OnLoad()` calls `LoadCrewAndGroupState(node)` before `LoadExternalFilesAndRestoreEpoch(node)`.
- `LoadCrewAndGroupState(node)` does `LedgerOrchestrator.Kerbals?.LoadSlots(node)`.
- On a true cold start, `LedgerOrchestrator.OnLoad()` has not run yet, so `LedgerOrchestrator.Kerbals` is still null.
- The saved `KERBAL_SLOTS` node is therefore skipped silently on the first load after starting KSP.

Why this is a bug:

- The task document explicitly says slot data must be loaded alongside crew replacements.
- Persisted slot chains are supposed to preserve deterministic stand-in names and chain lineage across restarts.
- After `LedgerOrchestrator.OnKspLoad(...)` recalculates from ledger state, the module is working from an empty slot graph rather than the saved one.

Concrete scenario:

- Save the game with a deep replacement chain and retired stand-ins already established.
- Exit KSP and relaunch.
- On the first load of that save, `KERBAL_SLOTS` never gets loaded.
- Recalculation rebuilds only from reservations, so chain naming/history can drift and previously persisted lineage is lost.

Expected behavior:

- Ensure the kerbals module exists before slot load, or move slot loading to a point after `LedgerOrchestrator.OnLoad()` initializes the module.

### 3. High: the owner-free reclaim path clears chains too early and loses deeper stand-in lineage

Code:

- `Source/Parsek/KerbalsModule.cs:507-536`
- `Source/Parsek/KerbalsModule.cs:736-775`

Design:

- `docs/parsek-game-actions-and-resources-recorder-design.md:1542-1545`
- `docs/parsek-game-actions-and-resources-recorder-design.md:1587-1601`

What happens:

- `ComputeRetiredSet()` only retires a used stand-in when its predecessor is free and the stand-in itself is not reserved.
- `ApplyToRoster()` clears the entire chain as soon as `slot.OwnerName` is not reserved, without checking whether deeper stand-ins in that same chain are still reserved.

Why this is a bug:

- If a timeline edit leaves the owner free while a used stand-in in that slot is still reserved, the code deletes the chain metadata that would later classify that stand-in as retired.
- Once the chain is cleared, the stand-in is no longer attached to its original owner slot, so a later recalculation cannot derive the correct retirement outcome from scratch.

Concrete scenario:

- Jeb's slot already contains a used stand-in Hanley.
- A later timeline edit removes the recording that reserved Jeb, but keeps a recording that still reserves Hanley.
- `ComputeRetiredSet()` does not retire Hanley yet because Hanley is still reserved.
- `ApplyToRoster()` still clears Jeb's entire chain immediately because Jeb is no longer reserved.
- When Hanley's reservation disappears in a later recalculation, there is no remaining slot lineage to retire him; he falls back to normal available crew instead of staying retired/unassignable.

Expected behavior:

- Reclaim should only collapse the chain when the chain no longer has any deeper reservation that still depends on it, or the lineage must be preserved some other way until retirement can be derived correctly.

### 4. High: permanent owner loss does not actually exit the chain system if the slot already had stand-ins

Code:

- `Source/Parsek/KerbalsModule.cs:221-232`
- `Source/Parsek/KerbalsModule.cs:634-653`
- `Source/Parsek/KerbalsModule.cs:740-775`

Design:

- `docs/parsek-game-actions-and-resources-recorder-design.md:1547-1551`
- `docs/parsek-game-actions-and-resources-recorder-design.md:1588-1589`
- `docs/parsek-game-actions-and-resources-recorder-design.md:1772-1779`
- `docs/dev/done/game-actions/kerbals-module/task-2-reservation-and-chains.md:613-625`

What happens:

- `PostWalk()` marks `OwnerPermanentlyGone = true` for the slot owner, but it does not clear any existing chain entries.
- `GetActiveOccupant()` will still return the deepest free stand-in in that chain for the permanently reserved owner.
- `ApplyToRoster()` never clears those chains because its reclaim condition explicitly excludes `OwnerPermanentlyGone`.

Why this is a bug:

- The design says permanent loss of the slot owner means no auto-replacement and the roster shrinks.
- With the current code, an old stand-in chain can keep filling the dead kerbal's slot, which is the exact behavior the design forbids.

Concrete scenario:

- Jeb previously had a temporary replacement chain such as `[Hanley, Kirrim]`.
- A later committed recording kills Jeb permanently.
- Recalculation marks Jeb permanently gone, but the chain remains attached to his slot.
- `GetActiveOccupant("Jeb")` can still pick Hanley or Kirrim as the live replacement.
- The slot therefore continues to auto-fill instead of shrinking and forcing the player to hire a new independent kerbal.

Expected behavior:

- When the slot owner becomes permanently gone, any existing temporary replacement chain for that owner should be retired/cleared in a way that preserves recording references but does not continue supplying active occupants.

### 5. Medium: EVA-only recordings are handled at commit time but skipped by the migration/safety-net population path

Code:

- `Source/Parsek/GameActions/LedgerOrchestrator.cs:114-120`
- `Source/Parsek/GameActions/LedgerOrchestrator.cs:555-556`
- `Source/Parsek/GameActions/LedgerOrchestrator.cs:586-589`

What happens:

- Commit-time population correctly allows `PopulateCrewEndStates()` when either `VesselSnapshot` exists or `EvaCrewName` is set.
- The migration path (`MigrateKerbalAssignments`) and the safety-net path (`PopulateUnpopulatedCrewEndStates`) only do this when `rec.VesselSnapshot != null`.

Why this is a bug:

- EVA recordings can legitimately carry crew identity only through `EvaCrewName`.
- Older saves, migrated ledgers, or partially populated recordings can therefore miss end-state population even though the commit-time path already knows how to handle them.

Concrete scenario:

- Load an older save whose EVA recordings have `EvaCrewName` but no `VesselSnapshot`.
- Migration generates `KerbalAssignment` actions without first populating correct end states.
- Those actions stay `Unknown`, which makes the EVA kerbal look open-ended reserved instead of correctly recovered/dead/aboard.

Expected behavior:

- The migration and safety-net guards should match the commit-time guard and allow the EVA-name-only case.

### 6. Medium: tourists are still being managed by the kerbals reservation system

Code:

- `Source/Parsek/GameActions/LedgerOrchestrator.cs:297-381`
- `Source/Parsek/KerbalsModule.cs:150-214`

Design:

- `docs/parsek-game-actions-and-resources-recorder-design.md:1463-1464`
- `docs/dev/done/game-actions/game-actions-deferred.md:45-49`

What happens:

- `CreateKerbalAssignmentActions()` emits actions for every extracted crew member.
- `KerbalsModule.ProcessAction()` reserves every `KerbalAssignment`.
- There is no tourist exclusion anywhere in this path.

Why this is a bug:

- The design explicitly says tourists are contract-only temporary passengers and should not enter the managed kerbal reservation/chain system.
- Treating them as managed kerbals can create artificial reservations, stand-ins, crew-dialog filtering, and dismissal protection for contract passengers who should disappear with normal contract flow.

Concrete scenario:

- A tourist contract recording commits with one pilot and two tourists aboard.
- Parsek emits `KerbalAssignment` actions for all three passengers.
- The tourists can then become reserved from ledger history, participate in replacement logic, and get filtered from crew UI even though they were never supposed to be part of the managed roster model.

Expected behavior:

- Tourist crew should be excluded from `KerbalAssignment` creation entirely, or explicitly ignored by the kerbals module.

## Coverage Gaps

Existing tests cover some neighboring behavior, but not these failure paths end to end:

- `Source/Parsek.Tests/KerbalEndStateTests.cs` covers reverse-mapping in `PopulateCrewEndStates()`, but not the missing reverse-map in `CreateKerbalAssignmentActions()`.
- `Source/Parsek.Tests/KerbalReservationTests.cs` covers slot serialization and some permanent-reservation behavior, but not the cold-start `ParsekScenario.OnLoad()` ordering bug.
- I did not find regression coverage for:
  - permanent-owner chain cleanup after an existing temporary chain
  - owner-free/deeper-stand-in-still-reserved chain preservation
  - EVA-name-only migration/safety-net population
  - tourist exclusion from kerbal action generation

