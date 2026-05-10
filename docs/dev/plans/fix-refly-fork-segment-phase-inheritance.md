# Fix Plan: Re-Fly Fork Inherits Stale SegmentPhase / SegmentBodyName

Date: 2026-05-10

Worktree: `C:\Users\vlad3\Documents\Code\Parsek\Parsek-fix-refly-fork-segment-phase`

Branch: `fix-refly-fork-segment-phase`

Base: `94d4e7c2 Merge pull request #803 from vl3c/debris-always-shadow` (origin/main)

Evidence bundle: `C:\Users\vlad3\Documents\Code\Parsek\logs\2026-05-10_1713`

## Problem Statement

The Re-Fly in-place fork copies `SegmentPhase` and `SegmentBodyName` from the
parent recording onto the new provisional. The parent's value reflects the
parent's most-recent segment, not the fork's flight. Once the field is non-empty
every runtime tagger that derives the phase from live vessel state guards on
`string.IsNullOrEmpty(SegmentPhase)` and becomes a no-op, so the inherited value
sticks for the lifetime of the fork — even when the new flight ends in a
completely different environment.

User repro (2026-05-10 evidence bundle):

- `Kerbal X Probe` original probe-stage segment `32d9674c…` (treeOrder=7,
  chainIndex=0) ended in atmosphere → `segmentPhase = atmo`,
  `endBiome = Water`.
- The user invoked Re-Fly on that slot. The in-place fork
  `rec_b1566ae4…` (treeOrder=10, `provisionalForRpId=rp_c6c6efeb…`) flew the
  probe stage to a stable Kerbin orbit:
  - `terminalState = 0` (Orbiting)
  - `tOrbEcc = 0.32`, `tOrbSma = 1.19 Mm`
  - `endpointPhase = 2` (OrbitSegment)
- But the saved row still has `segmentPhase = atmo`,
  `segmentBodyName = Kerbin`. The recordings table renders this as
  "Kerbin atmo" even though the vessel is in stable orbit.

Confirming log line (`KSP.log` 17:04:43.113):

```
AtomicMarkerWrite: in-place continuation forked — fork rec_b1566ae4…
  supersedes priorTip 32d9674c… inheritedFrom=origin sourceRec=32d9674c…
  ...
```

`inheritedFrom=origin` — the fork inherited from `32d9674c…`, whose
`SegmentPhase` was `"atmo"`.

## Root Cause

[`RewindInvoker.CopyInheritedIdentityForFork`](Source/Parsek/RewindInvoker.cs:234)
copies the parent's `SegmentPhase` and `SegmentBodyName` onto the fork:

```csharp
provisional.SegmentPhase = inheritFrom.SegmentPhase;       // line 249
provisional.SegmentBodyName = inheritFrom.SegmentBodyName; // line 250
```

The contract for those two fields is "phase / body of the recording's most
recent segment". They identify what kind of segment THIS recording captured —
not what the launch site was. Inheriting them across a Re-Fly fork is wrong:
the fork is a NEW flight that supersedes its parent's segment with a different
trajectory. Its `SegmentPhase` should describe the fork's own segment, not the
parent's.

Every runtime path that would otherwise re-tag the fork from live vessel state
guards on `string.IsNullOrEmpty(SegmentPhase)` and becomes a no-op once
inheritance has populated the field:

- [`ParsekFlight.TagSegmentPhaseIfMissing`](Source/Parsek/ParsekFlight.cs:3773)
  — `if (string.IsNullOrEmpty(pending.SegmentPhase))`
- [`ParsekFlight.StopRecording`](Source/Parsek/ParsekFlight.cs:9847) tag block
  — same guard, and writes to `recorder.CaptureAtStop` rather than to the tree
  recording (`FlushRecorderToTreeRecording` does not propagate that field).
- [`ChainSegmentManager.CommitVesselSwitchTermination`](Source/Parsek/ChainSegmentManager.cs:776)
  — only fires on vessel-switch termination.

`ChainSegmentManager.CommitBoundarySplit` (line 735) DOES write unconditionally,
but only the chain manager owns it. The Re-Fly fork does not inherit chain
identity (`ChainId`/`ChainIndex`/`ChainBranch` are intentionally NOT copied per
the existing comment) and does not run inside the source's chain, so chain
boundary commits cannot tag the fork.

Result: the inherited stale phase rides through unmodified to OnSave.

## Other Inherited Fields — Are They Correct?

`CopyInheritedIdentityForFork` also copies these fields. Each must be evaluated
for the same staleness risk:

| Field | Inherit? | Rationale |
|---|---|---|
| `VesselPersistentId` | yes | The fork records the same live vessel; the recorder's per-vessel tracking depends on PID continuity. |
| `VesselName` | yes | Same vessel identity. |
| `IsDebris` | yes | Debris-flag is intrinsic to the part-set being followed. |
| `DebrisParentRecordingId` | yes | Load-bearing v12 debris-anchor contract (PR 3b documents this). |
| `Generation` | yes | Generation depth is identity, not state. |
| `SegmentPhase` | **no — drop** | Most-recent-segment classification of the parent, not of the fork. |
| `SegmentBodyName` | **no — drop** | Same as above; tracks the parent's last segment. |
| `StartBodyName` / `StartBiome` / `StartSituation` | yes (TBD) | Describe the recording's launch identity. The Re-Fly fork conceptually continues the original launch (same `VesselName`, same launch site) — keeping these is consistent. |
| `LaunchSiteName` | yes | Same launch identity. |
| `VesselSnapshot` / `GhostVisualSnapshot` | yes (then refreshed) | `TryRefreshForkSnapshotsFromLiveVessel` overwrites these from the live post-Strip vessel; the inherited copy is a defensive fallback. |

The two segment-phase fields are the only ones whose semantic does not survive
the fork. Drop them, and let the existing runtime taggers populate them when
the fork's recorder commits / stops.

## Will Dropping Inheritance Leave the Fork's `SegmentPhase` Empty?

Possible but acceptable, and we will close that gap explicitly as part of the
fix.

`SegmentPhase` write sites that DO operate on tree recordings:

1. `ChainSegmentManager.CommitBoundarySplit` — fires when the recorder's chain
   manager crosses an environment boundary. Sets `SegmentPhase` on the
   just-completed chain segment unconditionally. **Active for the fork only if
   the fork participates in a chain.** For an in-place Re-Fly fork that does
   not branch into a chain, this never fires.
2. `ChainSegmentManager.CommitVesselSwitchTermination` — fires on vessel
   switch. Tags the final chain segment from the live recorded vessel
   unconditionally. **Active only if the fork is part of a chain that
   terminates on vessel switch.**
3. `ParsekFlight.TagSegmentPhaseIfMissing` — called from
   `CommitBranchedRecording` when committing a branched recording (line 4407).
   Guards on empty SegmentPhase. **Fires only on branch commits, not for
   linear continuations.**
4. `RecordingOptimizer.SplitAtSection` — when the optimizer splits a recording
   at a track-section boundary, both halves get tagged from their first
   `TrackSection.environment`. **Fires only when the optimizer actually splits.**

For a "Re-Fly fork that flies a single linear trajectory and ends in orbit"
shape (the user's case after my fix is applied), none of the above necessarily
fires. The fork's saved `SegmentPhase` would be empty/null.

That is not acceptable for the recordings-table UX. The Phase column should
not silently disappear on Re-Fly forks where the original showed it.

### Closing the gap

Add an explicit, low-risk tag immediately after `CopyInheritedIdentityForFork`
in the in-place continuation block. The fork's first sample comes from the
live post-Strip vessel — exactly the right vessel state to classify. That
vessel handle is already in scope as `stripResult.SelectedVessel` and is
already used by `TryRefreshForkSnapshotsFromLiveVessel`.

The existing helper `ParsekFlight.TagSegmentPhaseIfMissing(pending, vessel)`
implements the same body/altitude/situation classification used by every other
phase tagger and will work directly. (It currently lives on `ParsekFlight`,
which is a Unity behaviour, but the implementation body has no Unity
dependencies aside from reading `Vessel.mainBody` and `Vessel.altitude`. We
can either invoke it directly from the in-place block, or extract the pure
classifier into `RewindInvoker` / a small static helper for testability — see
"Implementation Strategy" below.)

This means the ladder for the fork's `SegmentPhase` becomes:

1. Initial tag from live post-Strip vessel at fork creation (NEW).
2. If a boundary split fires later in the fork's flight, the chain manager
   updates the OLD segment correctly (unchanged behaviour).
3. If the fork branches via `CommitBranchedRecording`, `TagSegmentPhaseIfMissing`
   currently does NOT overwrite (the field is non-empty after step 1). That's
   acceptable — branching produces a new child recording with its own phase.
4. The optimizer's split-time tagging (`SplitAtSection` lines 875-884) also
   does not check IsNullOrEmpty — it overwrites both halves from their first
   track section's environment. So if the optimizer later splits the fork,
   both halves get correctly classified.

The UI column will end up showing the fork's classification at recorder-arm
time. That is consistent with what every brand-new recording started from
KSC currently shows for its first segment, before any boundary crossing.

### Edge cases

- **`stripResult.SelectedVessel == null`** (in-place strip somehow yielded no
  vessel handle): the helper is a no-op for null vessel. Field stays empty.
  This is the same pre-existing behaviour we'd see for any recording that
  lacks a live vessel handle at creation time. Surface this with a `Warn`
  log line at the call site so an operator can spot upstream regressions
  in the strip pipeline.
- **`SelectedVessel.mainBody == null`** (test stubs / Unity-null case): same —
  no-op. No log.
- **`SelectedVessel.situation == PRELAUNCH`**: the helper would tag
  `phase = "surface"`. Re-Fly RPs are taken at separation events mid-flight,
  so post-Strip vessels should have `FLYING` / `SUB_ORBITAL` / `ORBITING`,
  never `PRELAUNCH`. If a defensive code path ever lands the strip vessel on
  the pad, the tag would say `surface` — at worst the user sees "Kerbin
  surface" instead of empty. Acceptable; not worth special-casing.
- **Fork started in a chain (rare second-Re-Fly-of-an-unsealed-slot path)**:
  the fork still doesn't carry `ChainId`/`ChainIndex` (intentionally per the
  existing call-site comment). `inheritFrom` resolves to `chain-tip` rather
  than `originChild`, but the tag input is `stripResult.SelectedVessel`, not
  the inheritance source — so the chain-tip vs origin distinction does not
  affect the tag. Initial tag at fork time is correct.

## Why Not "Just Drop The Inheritance Without Adding A Tag"

That option would leave forks with an empty Phase column for pure linear
flights (no chain, no branch, no optimizer split). That is a UX regression
from current state — the user would see the Phase column simply blank, even
though it was always populated before. The explicit tag at fork creation
keeps parity with current UX.

## Why Not "Re-Tag On Stop" (and What This Fix Leaves On The Table)

**Stage 1 of a two-stage fix**: the fork-creation tag closes the user-visible
"phase shows wrong inherited value" symptom. It does NOT make the saved phase
reflect the fork's END state — for a Re-Fly that takes off in atmo and stops
in orbit, this fix saves `phase=atmo` (the fork-creation tag), which is BETTER
than the current bug (which saved `atmo` inherited from the parent's last
segment) but still not "fork in orbit shows orbit".

A complete fix would also propagate `recorder.CaptureAtStop.SegmentPhase` into
the tree recording at flush/finalize time. `ParsekFlight.StopRecording`
already writes to `recorder.CaptureAtStop.SegmentPhase` (line 9847), but
`FlushRecorderToTreeRecording` does not propagate that field to the tree
recording, so the saved phase never reflects the recorder's stop-time state
for any non-chain, non-branch recording. That's a separate latent bug that
this PR makes more visible — once inheritance no longer hides it, every
"linear flight that changes phase" recording will show its start phase, not
its end phase, in the recordings table.

Stage 2 is tracked in `docs/dev/todo-and-known-bugs.md`. It needs an audit
of every `FlushRecorderToTreeRecording` call site and a careful precedence
decision (don't clobber chain-set phases; do clobber inherited start tags).
Out of scope for this PR.

## Vocabulary Alignment Check

`TagSegmentPhaseIfMissing` produces lowercase tags: `"atmo"`, `"exo"`,
`"approach"`, `"surface"`. `RecordingOptimizer.EnvironmentToPhase` (line 1329)
produces the same lowercase tags. The recordings-table colour-coding
(`UI/RecordingsTableUI.cs:1387-1391`) and label rendering
(`RecordingStore.GetSegmentPhaseLabel` line 2570) both consume those exact
lowercase keys. No vocabulary mismatch — the new initial tag round-trips
through the same UI path as every other phase-tagged recording.

Note: the existing `DebrisParentAnchorContractTests` test data sets
`SegmentPhase = "Atmospheric"` (capitalized). That's a test-data smell — it
doesn't match the runtime vocabulary — but it doesn't cause a test bug
because the helper just copies the string as-is. When the assertions are
flipped to `Assert.Null`, the test data becomes irrelevant for those two
fields and we can leave the bogus capitalization or update it for clarity.

## `StopRecording`'s Tag Block Is Effectively Dead For Tree Recordings

[`ParsekFlight.StopRecording`](Source/Parsek/ParsekFlight.cs:9847) writes the
final phase tag to `recorder.CaptureAtStop.SegmentPhase`, not to the tree
recording. Since `FlushRecorderToTreeRecording` does not propagate the field,
this tag never lands on disk for tree-mode recordings. It survives only
because some legacy non-tree code paths read `CaptureAtStop` directly.

After this fix, that block is even more of a trap — it looks like it's
tagging the saved phase but it isn't. Track its removal-or-rewire in
`docs/dev/todo-and-known-bugs.md` alongside the Stage 2 flush-propagation
fix. Out of scope for this PR; flagged for follow-up.

## Implementation Strategy

### 1. Drop the two stale-inheritance lines

In [`RewindInvoker.CopyInheritedIdentityForFork`](Source/Parsek/RewindInvoker.cs:234),
remove:

```csharp
provisional.SegmentPhase = inheritFrom.SegmentPhase;
provisional.SegmentBodyName = inheritFrom.SegmentBodyName;
```

### 2. Tag from live post-Strip vessel at fork time

In `RewindInvoker.AtomicMarkerWrite` immediately after the existing
`CopyInheritedIdentityForFork(provisional, inheritFrom)` call (line 1099),
invoke `ParsekFlight.TagSegmentPhaseIfMissing(provisional, stripResult.SelectedVessel)`.

`TagSegmentPhaseIfMissing` is currently `internal static` on `ParsekFlight`.
It uses no Unity-only globals (only `Vessel.mainBody`, `Vessel.altitude`,
`Vessel.situation`, and `FlightRecorder.ComputeApproachAltitude`). Calling it
from `RewindInvoker` is fine — both files already share many static helpers.

Add a one-line `ParsekLog.Verbose("Rewind", $"Initial segment phase tagged on
fork rec={provisional.RecordingId}: body={...} phase={...}")` for
observability, matching the existing per-action log discipline in
`AtomicMarkerWrite`.

### 3. Update the existing field-copy contract test

[`DebrisParentAnchorContractTests.CopyInheritedIdentityForFork_DebrisProvisional_PropagatesParentRecordingId`](Source/Parsek.Tests/DebrisParentAnchorContractTests.cs:303)
asserts the helper copies `SegmentPhase = "Atmospheric"` and
`SegmentBodyName = "Kerbin"`. Replace those two assertions with their
inverse:

```csharp
// SegmentPhase / SegmentBodyName are intentionally NOT inherited — the
// fork is a new flight whose phase classification must come from its own
// trajectory, not the parent's most-recent segment. See
// fix-refly-fork-segment-phase-inheritance.md for the rationale.
Assert.Null(provisional.SegmentPhase);
Assert.Null(provisional.SegmentBodyName);
```

Preserve the every-field enumeration pattern in the test — the test
serves as a contract that future additions to the helper must update one
specific test, not a global "did anything change" assertion. Don't drop
the two lines wholesale; flip them to `Assert.Null` so an accidental
re-introduction of the inheritance trips the test.

Also add a new test that asserts the contract directly with realistic
lowercase vocabulary: parent has `SegmentPhase = "atmo"`, fork is built via
`CopyInheritedIdentityForFork`, fork's `SegmentPhase` is null and
`SegmentBodyName` is null.

### 4. Add a regression test for the behaviour-level outcome

The full `AtomicMarkerWrite` path requires Unity (`stripResult.SelectedVessel`
is a Unity `Vessel`), so the live-tag step itself is exercised by the in-game
test framework. xUnit covers:

- The new field-copy contract test (step 3) — pure data-model assertion that
  the inheritance no longer carries `SegmentPhase` / `SegmentBodyName`.
- A test that simulates the post-fix flow on a recording without ever calling
  the live tagger and asserts that the fork's `SegmentPhase` remains null
  (no phantom tag).

The behaviour-level "fork that reaches orbit ends up with `SegmentPhase=exo`"
is exercised by an in-game test in `RuntimeTests.cs`. The test:

1. Creates a `Recording` with `SegmentPhase = "atmo"` (parent).
2. Sets up a fake `stripResult.SelectedVessel` whose situation is `ORBITING`
   (or simulates the equivalent state).
3. Calls the fork-creation flow.
4. Asserts the fork's `SegmentPhase == "exo"` and `SegmentBodyName == "Kerbin"`.

If a full in-game harness for `AtomicMarkerWrite` is too heavy, the in-game
test can call `ParsekFlight.TagSegmentPhaseIfMissing(rec, FlightGlobals.ActiveVessel)`
directly with the active vessel in flight scene and assert the result, which
proves the helper does the right thing for the live-vessel tag step.

### 5. Logging

Three log sites at the new tag call, all Verbose:

- **Tag fired** (`SelectedVessel != null && mainBody != null` produced a
  classification): `ParsekLog.Verbose("Rewind", $"Fork initial segment phase
  tagged: rec={...} pid={...} body={...} phase={...} situation={...}")`.
  One-shot, Verbose level per codebase convention.
- **Tag skipped — null `SelectedVessel`**: `ParsekLog.Verbose("Rewind", ...)`.
  Aligns with the sibling `TryRefreshForkSnapshotsFromLiveVessel` helper
  which uses Verbose for the same null-vessel-in-test-fixture case; both
  helpers run on the same call site and both handle the same null branch.
- **Tag skipped — `SelectedVessel != null` but `mainBody == null`**:
  `ParsekLog.Verbose(...)`. Test-stub / Unity-null shape.

Earlier draft of this plan recommended Warn for the null-`SelectedVessel`
branch as a diagnostic for upstream strip-pipeline regressions. Demoted to
Verbose during PR review (#806) because the existing
`AtomicMarkerWriteTests` in-place test paths use a null `SelectedVessel`
stub (Vessel is a Unity type, can't be constructed in xUnit), and a Warn
would pollute the test log sink without an assertion attached. The
production case where the strip pipeline genuinely returns null in the
in-place branch is already guarded earlier in `AtomicMarkerWrite`, so the
Warn was effectively unreachable in production anyway.

The existing `AtomicMarkerWrite: in-place continuation forked — fork ...
inheritedFrom=origin` line stays; it does not enumerate every inherited
field, so dropping two of them does not change the line.

## Risks and Trade-Offs

- **Fork's `SegmentPhase` differs from parent's** — by design. If a user is
  confused that "Re-Flying an atmo segment shows phase=exo", the explanation
  is that the fork actually flew to orbit; the column is correct.
- **`stripResult.SelectedVessel` not yet settled** — at the moment
  `CopyInheritedIdentityForFork` runs, the post-Strip vessel has been
  selected but FlightGlobals.ActiveVessel may still be the pre-strip ghost.
  Using `stripResult.SelectedVessel` directly is the documented contract
  in the surrounding code (`TryRefreshForkSnapshotsFromLiveVessel` uses the
  same handle). No risk.
- **Optimizer overwrite** — the optimizer's `SplitAtSection` overwrites
  `SegmentPhase` from the first track section's environment, regardless of
  prior value. If the fork later goes through a split, the live-vessel tag
  will be replaced by the env-derived tag. Both come from real data; the
  later one wins, which is the correct precedence.
- **Save-format compatibility** — none. `SegmentPhase` and `SegmentBodyName`
  are nullable strings already; both null and non-null values round-trip via
  the existing codec.
- **Documentation** — `CHANGELOG.md` and `docs/dev/todo-and-known-bugs.md`
  get a 1–2 sentence entry. No CLAUDE.md update needed (no new file or
  workflow changes).

## Out of Scope (Mention Only)

- A general "tag all stale-phase recordings on load" sweep. The save format
  evolution covers this implicitly — this is a pre-1.0 dev project per the
  feedback memory and there is no migration path for old recordings. Any
  recording that already has a stale `SegmentPhase` from a pre-fix Re-Fly
  retains it on disk; the fix prevents new occurrences.
- **Stage 2 — propagate `CaptureAtStop.SegmentPhase` to the tree recording**
  at flush/finalize. After this PR, a Re-Fly that takes off in atmo and stops
  in orbit saves with `phase=atmo` (the new fork-creation tag) rather than the
  inherited stale phase from the parent. That's better than today, but the
  saved value still doesn't reflect the fork's END state. Tracked in
  `docs/dev/todo-and-known-bugs.md` for a separate PR.
- **Cleaning up the dead-code phase-tag block in `ParsekFlight.StopRecording`**
  (line 9847). The block writes to `recorder.CaptureAtStop.SegmentPhase`,
  which is never propagated to the saved tree recording. After this PR it
  becomes a more visible trap. Tracked alongside Stage 2.
- **Extracting the duplicated phase classifier** (three near-identical copies
  in `ParsekFlight.TagSegmentPhaseIfMissing`, `ParsekFlight.StopRecording`,
  and `ChainSegmentManager.CommitVesselSwitchTermination`) into a single
  static helper. Borderline gold-plating for this fix; tracked for follow-up.

## Validation Steps

1. `cd Source/Parsek && dotnet build` — clean build, post-build copy DLL is
   verified per `.claude/CLAUDE.md` recipe.
2. `cd Source/Parsek.Tests && dotnet test` — all tests pass; new tests cover
   the inheritance-drop and the post-tag behaviour.
3. Verify deployed DLL contains a distinctive new UTF-16 string from this
   change (e.g. the new log line text).
4. In-game smoke test: load the user's `s14` save, perform the same Re-Fly
   on Kerbal X Probe slot, fly to orbit, scene-exit, and confirm the
   recordings table shows "Kerbin exo" (not "Kerbin atmo") for the fork.

## Acceptance Criteria

- `RewindInvoker.CopyInheritedIdentityForFork` does not write
  `SegmentPhase` or `SegmentBodyName`.
- The in-place fork creation site invokes `TagSegmentPhaseIfMissing` (or its
  equivalent) on the post-Strip live vessel, producing a correct initial tag.
- The existing `DebrisParentAnchorContractTests` test is updated to match the
  new contract, and a new test asserts that the helper leaves both fields
  null.
- An in-game test verifies the fork's phase tag is correct for an
  atmo→orbit Re-Fly.
- All existing tests still pass.
- `CHANGELOG.md` has a one-line entry under the next release section.
- `docs/dev/todo-and-known-bugs.md` records the fix.

## File Touch List

- `Source/Parsek/RewindInvoker.cs` — drop two inheritance lines, add tag call.
- `Source/Parsek.Tests/DebrisParentAnchorContractTests.cs` — flip assertions.
- `Source/Parsek.Tests/RewindForkSnapshotRefreshTests.cs` (or new
  `RewindForkSegmentPhaseTests.cs`) — new tests.
- `Source/Parsek/InGameTests/RuntimeTests.cs` — in-game test.
- `CHANGELOG.md` — entry.
- `docs/dev/todo-and-known-bugs.md` — entry.
- `docs/dev/plans/fix-refly-fork-segment-phase-inheritance.md` — this file.
