# Refactor-4 Deferred Items

Status: archived with the completed Refactor-4 docs. Remaining refactor
opportunities are tracked in
`docs/dev/plans/refactor-remaining-opportunities.md`.

Items found during planning or implementation that are worth remembering but
should not be folded into an unrelated extraction.

## D1. Full `InjectAllRecordings` baseline while KSP is open

**What:** The full baseline `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj`
currently runs 8,628 tests but fails `SyntheticRecordingTests.InjectAllRecordings`
because `KSP.log` is locked by another process.

**Why deferred:** This is an environment/runtime setup issue, not a refactor bug.
The filtered baseline excluding `InjectAllRecordings` passes 8,625 tests and is
usable while KSP is open.

**Revisit when:** Close KSP or provide a clean `KSPDIR`, then rerun the full
suite before final merge.

## D2. `UI/RecordingsTableUI` major split

**What:** `UI/RecordingsTableUI.cs` is now 4,868 lines.

**Why deferred:** Refactor-3 explicitly deferred this extraction when the table
was much smaller because it had high coupling and 30+ shared fields. Size alone
does not prove a safe split.

**Revisit when:** Pass 2 has a field ownership map and can identify a sub-window,
row renderer, group tree, sorting, or edit-state boundary that does not require
large callback plumbing.

## D3. `LedgerOrchestrator` decomposition

**What:** `LedgerOrchestrator.cs` grew from 900 lines in refactor-3 to 6,976.

**Why deferred:** Refactor-3 judged the 900-line version a clean hub with a
narrow API. The new size demands investigation, but the first read must separate
real responsibility growth from long but coherent lifecycle/reconciliation code.

**Revisit when:** Pass 2 dependency and method maps identify separable owners
such as migration, lifecycle hooks, reconciliation, report generation, or
post-walk repair.

## D4. Binary sidecar/schema redesign

**What:** `TrajectorySidecarBinary.cs`, snapshot sidecar codecs, and
`RecordingStore.cs` may contain repeated storage patterns.

**Why deferred:** Refactor-4 is behavior-neutral. Binary format or snapshot
schema redesigns are too risky to hide inside structural cleanup.

**Revisit when:** Duplication is proven to be purely mechanical, or a separate
storage redesign is explicitly scoped.

## D5. Rewind-to-separation v2 semantic follow-ups

**What:** `docs/dev/todo-and-known-bugs.md` lists carryover work such as
index-to-recording-id refactor, cross-tree effective-id guards, and wider
tombstone scope.

**Why deferred:** These are behavior/semantic follow-ups, not generic
refactor-4 cleanup.

**Revisit when:** A dedicated rewind-to-separation follow-up is opened, or Pass 2
finds a behavior-neutral preparatory extraction that directly lowers the risk of
that future work.

## D6. In-game runtime validation

**What:** Runtime tests under `Source/Parsek/InGameTests` require KSP and are
not part of the headless xUnit baseline.

**Why deferred:** Planning/inventory docs do not require live KSP validation.
Runtime evidence becomes relevant after code changes touch ghost visuals,
recording lifecycle, UI, Tracking Station, or scene transitions.

**Revisit when:** Pass 1/Pass 3 changes affect runtime-only behavior; run the
appropriate Ctrl+Shift+T categories and keep `parsek-test-results.txt` /
`KSP.log` evidence.

## D7. Cross-file owners from the large-file sweep

**What:** The large-file opportunity map found several plausible future owners:
visual FX builders, storage/sidecar codecs, foreground/background part-event
pollers, playback loop scheduling, Tracking Station map presence, watch-mode
camera/overlap services, event handler families, KSC/flight playback sharing,
and rewind invocation ownership.

**Why deferred:** These are architectural changes. Refactor-4 Pass 1 is
zero-logic-change same-file extraction only, and any cross-file movement or
deduplication needs a specific proposal and discussion before implementation.

**Revisit when:** Pass 2 has dependency maps, static state ownership, public
method references, duplication include/reject decisions, validation scope, and
a rollback plan for each proposed split.

## D8. Pass 1 large-file deferrals

**What:** Pass 1 reviewed the remaining mapped large files after the validated
same-file helper extractions and intentionally left several candidates inline:
`GhostVisualBuilder`, `UI/RecordingsTableUI`, `GameStateRecorder`,
`KspStatePatcher`, `BallisticExtrapolator`, `RecordingOptimizer`,
`RecordingTree`, `ParsekKSC`, `UI/TimelineWindowUI`, and `RewindInvoker`. It
also closed `CrewReservationManager`, `EngineFxBuilder`, `KerbalsModule`,
`ParsekPlaybackPolicy`, `VesselGhoster`, `TrajectorySidecarBinary`,
`DiagnosticsComputation`, the remaining `ParsekFlight` finalization split, the
smaller leftover `FlightRecorder` / `GhostPlaybackLogic` candidates, and Tier 3
examples other than the `TimelineBuilder` canary as deferrals.

**Why deferred:** The remaining useful splits are not simple zero-logic helper
moves. They need runtime visual validation, IMGUI field ownership maps,
event-handler ownership maps, KSP state-family patcher boundaries, math/order
regression review, serialization codec design, KSC/flight playback architecture,
or rewind-invocation checkpoint ownership.

**Revisit when:** A Pass 2 proposal names the owner boundary, exact moved
methods/state, validation scope, architectural tradeoffs, and rollback plan.
