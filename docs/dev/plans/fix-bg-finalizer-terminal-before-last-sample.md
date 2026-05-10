# Fix Plan: Background Finalizer Terminal Before Last Sample

## Status

Implemented in this branch. The production change follows the plan's confirmed-destruction gate: `DeferredDestructionCheck` and the destroyed/despawned `EndDebrisRecording` path can clamp stale `Destroyed` cache terminals to the last authored sample; non-destruction debris exits do not receive this retrograde-terminal repair.

## Trigger

Retained log bundle:

- `logs/2026-05-10_1713/KSP.log`
- Warns:
  - line 11021: `Apply rejected: consumer=EndDebrisRecording reason=RejectedTerminalBeforeLastSample rec=328046ce... lastAuthoredUT=348.572 terminal=Destroyed terminalUT=348.532`
  - line 11083: `Apply rejected: consumer=EndDebrisRecording reason=RejectedTerminalBeforeLastSample rec=99366566... lastAuthoredUT=348.652 terminal=Destroyed terminalUT=347.972`

The saved recordings were not visibly broken in this case: both retained `.sfs` entries are finalized with `terminalState = 4` and `explicitEndUT` equal to the last authored section endpoint, and both `.prec.txt` sidecars contain the expected section payloads. The bug is still worth fixing because it logs a false rejection and leaves background debris finalization dependent on fallback behavior after the cache apply fails.

## Current Ordering

Relevant files:

- `Source/Parsek/BackgroundRecorder.cs`
- `Source/Parsek/RecordingFinalizationCacheApplier.cs`
- `Source/Parsek/RecordingFinalizationCacheProducer.cs`
- `Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs`
- `Source/Parsek.Tests/RecordingFinalizationCacheTests.cs`
- `Source/Parsek.Tests/BackgroundRecorderTests.cs`

Observed order for `328046ce`:

1. `OnBackgroundPartDie` refreshes the finalizer cache and accepts `Destroyed@348.532`.
2. `OnBackgroundPartJointBreak` appends a structural-event trajectory snapshot at `348.571904`.
3. The joint-break cache refresh declines because the live-orbit fallback is sub-surface and contradicted by recorded data.
4. `TryPreservePreviousCacheAfterFailedRefresh` keeps the earlier `Destroyed@348.532` cache as `Stale`.
5. `OnBackgroundVesselWillDestroy` refreshes again, declines again, closes and flushes the section, and persists the sidecar.
6. `EndDebrisRecording` snapshots `cacheForApply`, calls `OnVesselRemovedFromBackground`, then applies the stale cache with `allowStale=true`.
7. The generic applier computes `lastAuthoredUT=348.572` from authored section data and rejects `terminalUT=348.532`.

Observed order for `99366566` is the same shape, except its preserved stale terminal was much older: `Destroyed@317.972` against `lastAuthoredUT=348.652`.

Important correction to the initial hypothesis: the last authored sample in the retained log is not a post-destroy `OnBackgroundPhysicsFrame` sample. It is the final structural-event snapshot appended before the destroy warning. The warning occurs later because `EndDebrisRecording` applies a previously preserved stale cache after section flush.

## Root Cause

`RecordingFinalizationCacheProducer.TryPreservePreviousCacheAfterFailedRefresh` deliberately preserves a previously applicable terminal cache after the default finalizer declines. That is useful for stable terminal predictions in general, but in this debris path the preserved cache can become chronologically stale when later authored structural snapshots extend the recording.

`RecordingFinalizationCacheApplier.TryApply` correctly rejects any cache whose `TerminalUT` is before the last authored sample. That guard is still valid globally. The bug is that `BackgroundRecorder.TryApplyFinalizationCacheForBackgroundEnd` passes a stale background `Destroyed` cache through unchanged even though confirmed-destruction background finalization has extra context:

- the vessel is being removed from background recording;
- `endUT` is the intended background end;
- section flush has just materialized all authored frames;
- for confirmed destroyed debris / background vessels, the correct terminal cannot precede the final authored structural sample.

## User-Facing Impact

For the retained session, likely none in-game. The rejected cache did not remove authored data and did not leave the recordings unfinalized. `EndDebrisRecording` fell back to `TerminalState.Destroyed`, and the retained saves show the final end UT matches the authored section endpoint.

The risk is indirect:

- repeated warnings obscure real finalizer failures;
- future call-site changes might remove or weaken the fallback;
- rejected cache apply skips cache terminal metadata and endpoint refresh that an accepted apply would perform;
- the same stale-cache pattern can mask whether the upstream sub-surface fallback is still producing unreliable destroyed terminals.

## Fix Strategy

Keep the generic applier strict. Add a narrow background-end reconciliation step before calling it.

Preferred implementation:

1. Add explicit destruction context to the background-end apply path. The clamp must require confirmed destruction, not just `consumerPath == "EndDebrisRecording"`.
   - `DeferredDestructionCheck` already represents confirmed destruction and calls `TryApplyFinalizationCacheForBackgroundEnd(... requireDestroyedTerminal: true)`.
   - `EndDebrisRecording` is broader: it handles missing vessel / destroyed-or-despawned, TTL expiry, leaving the bubble, parent recording closed, and parent on-rails/destroyed. Only the missing-vessel destroyed/despawned branch should be treated as confirmed child destruction.
   - Implement this as either an explicit `confirmedDestroyed` boolean or a small enum such as `BackgroundRecordingEndReason` flowing from `CheckDebrisTTL` into `EndDebrisRecording` and then into `TryApplyFinalizationCacheForBackgroundEnd`.
2. In `BackgroundRecorder.TryApplyFinalizationCacheForBackgroundEnd`, after `ScopeFinalizationCacheToBackgroundEnd(cache, endUT)` and `ApplyFinalizationCacheIdentity(scopedCache, recording)`, reconcile `scopedCache.TerminalUT` against authored data only for background-end `Destroyed` caches when confirmed destruction is true.
3. Use `RecordingFinalizationCacheApplier.TryGetLastAuthoredUT(recording, out lastAuthoredUT)`.
4. If:
   - `scopedCache.TerminalState == TerminalState.Destroyed`;
   - `scopedCache.TerminalUT` is finite;
   - `lastAuthoredUT` is finite;
   - `scopedCache.TerminalUT + UtEpsilon < lastAuthoredUT`, using the same `1e-6`-style tolerance as the generic applier;
   - confirmed destruction is true;
   then clone-adjust the scoped cache:
   - `scopedCache.TerminalUT = ResolveClampedBackgroundDestroyedTerminalUT(lastAuthoredUT, endUT)`;
   - `scopedCache.TailStartsAtUT = min(existing TailStartsAtUT, TerminalUT)` only if needed to keep `TailStartsAtUT <= TerminalUT`;
   - do not manually mutate `PredictedSegments`. The generic applier already calls its private `BuildRetainedPredictedSegments` after it recomputes `lastAuthoredUT` and reads the clamped `TerminalUT`.
5. Emit a single diagnostic, preferably `Info`, not `Warn`:
   - tag: `FinalizerCache`;
   - message: `Background terminal clamped: consumer=... rec=... oldTerminalUT=... newTerminalUT=... lastAuthoredUT=... endUT=... cacheStatus=... owner=... confirmedDestroyed=...`;
   - this makes the race observable without treating the guarded outcome as a failure.

Suggested helper shape:

```csharp
private static bool TryClampDestroyedBackgroundTerminalAfterAuthoredData(
    Recording recording,
    RecordingFinalizationCache scopedCache,
    double endUT,
    string consumerPath,
    bool confirmedDestroyed,
    out double oldTerminalUT,
    out double lastAuthoredUT)
```

Keep this helper in `BackgroundRecorder.cs` near `ScopeFinalizationCacheToBackgroundEnd`, because the behavior is a background recorder lifecycle policy, not a generic cache-applier rule.

## Clamp Semantics

Use:

```text
newTerminalUT = lastAuthoredUT
```

not:

```text
newTerminalUT = max(cache.TerminalUT, endUT)
```

Reasoning:

- `lastAuthoredUT` is the invariant the generic applier enforces.
- `endUT` can be slightly later than the last authored point because cleanup may happen after the final structural snapshot. Extending terminal metadata to `endUT` would imply unrecorded trajectory duration.
- `ScopeFinalizationCacheToBackgroundEnd` already bounds destroyed terminal cache UT by `Math.Min(cache.TerminalUT, endUT)`. The new rule should only repair the lower bound for authored data, not invent a later terminal.

Make cleanup use a shared close UT instead of relying on a frame-scale tolerance. `EndDebrisRecording` currently receives `endUT`, but `OnVesselRemovedFromBackground` obtains a fresh `Planetarium.GetUniversalTime()` while closing sections and sampling the final boundary. Add an optional close-UT parameter, or a private cleanup helper, so the close/flush step uses the same `endUT` that the apply path receives.

After cleanup uses a shared close UT, treat `lastAuthoredUT > endUT + UtEpsilon` as suspicious. Do not clamp beyond `endUT` silently; log a warning and fall back to the existing rejection/fallback path. In normal cases, `lastAuthoredUT` should equal or precede `endUT`.

## Non-Goals

Do not change `RecordingFinalizationCacheApplier.TryApply` to accept retrograde terminals globally. That guard protects active recording, scene-exit finalization, and repaired cache paths from corrupting recordings.

Do not remove the structural-event snapshot. It is authored data and is more trustworthy than the live-orbit fallback that produced the stale terminal.

Do not rewrite `TryPreservePreviousCacheAfterFailedRefresh` in this fix. The upstream NullSolver/sub-surface fallback issue is broader and already tracked in `docs/dev/todo-and-known-bugs.md`. The narrow clamp prevents the known false rejection without changing periodic cache behavior.

Do not clamp non-destroyed terminal states. `Landed`, `Splashed`, `Orbiting`, and `SubOrbital` cache semantics should remain tied to their producer-specific terminal evidence.

Do not clamp stale `Destroyed` caches for non-destruction `EndDebrisRecording` exits such as TTL expiry, leaving the physics bubble, parent recording closed, or parent on-rails. Those paths can involve still-live child vessels, and today they may correctly fall back to live situation/orbit if a stale destroyed cache is rejected.

## Tests

Add focused unit coverage before implementation.

### 1. Generic applier remains strict

Existing test:

- `RecordingFinalizationCacheTests.TryApply_RejectsTerminalBeforeLastAuthoredSample`

Keep it unchanged. It proves the generic guard remains load-bearing.

### 2. Confirmed background destroyed cache clamps to last authored UT

Add to `BackgroundRecorderTests` or a new small `BackgroundRecorderFinalizationTests` class:

- build a recording with `RecordingId = "rec-bg-debris"`, `VesselPersistentId = 2209731480`, authored point or track-section frame at `348.572`;
- build a `RecordingFinalizationCache` for the same record/pid with:
  - `Status = Stale`;
  - `Owner` inherited from the prior refresh, typically `BackgroundLoaded` but not part of the contract under test;
  - `TerminalState = Destroyed`;
  - `TerminalUT = 348.532`;
- call `TryApplyFinalizationCacheForBackgroundEnd(recording, cache, pid, endUT: 348.572, consumerPath: "DeferredDestructionCheck", allowStale: true, requireDestroyedTerminal: true, confirmedDestroyed: true, out result)` or the equivalent new signature;
- assert:
  - returns `true`;
  - `result.Status == Applied`;
  - `recording.TerminalStateValue == Destroyed`;
  - `recording.ExplicitEndUT == 348.572`;
  - no `RejectedTerminalBeforeLastSample` appears in `ParsekLog.TestSinkForTesting`;
  - clamp diagnostic appears with old and new UTs.

### 3. `EndDebrisRecording` destroyed/despawned path exercises real flush ordering

Add a test that exercises the production ordering:

- seed a background debris recording with loaded-state TrackSection-only authored data whose last frame is after the stale destroyed cache terminal;
- snapshot/adopt a stale `Destroyed` cache before cleanup;
- trigger `CheckDebrisTTL` through the missing-vessel branch so the new end reason is confirmed destroyed/despawned;
- assert the TrackSection is flushed before apply, the stale terminal is clamped, the apply succeeds, and no `RejectedTerminalBeforeLastSample` warning is emitted.

This catches placement mistakes that a direct `TryApplyFinalizationCacheForBackgroundEnd` test cannot see.

### 4. Background clamp does not apply for non-destruction debris ends

Use a still-live child-vessel end reason such as TTL expiry, left bubble, parent recording closed, or parent on-rails. Provide a stale `Destroyed` cache with `TerminalUT < lastAuthoredUT`. Assert the clamp helper does not repair it merely because the consumer is `EndDebrisRecording`; expected behavior is the existing reject/fallback path or live situation/orbit fallback.

### 5. Background clamp does not apply when last authored UT exceeds endUT

Create a recording whose last authored UT is `110.0`, cache terminal is `100.0`, and background `endUT` is `105.0`. Assert the helper does not silently set terminal to `110.0`. Expected behavior can be either:

- generic applier rejects as today; or
- helper returns false and caller falls back.

The important invariant: once cleanup uses a shared close UT, no accepted finalization should set `ExplicitEndUT > endUT` unless that is already a documented background lifecycle rule. Tiny floating-point equality should use the same `UtEpsilon` style as the finalizer applier; frame-later cleanup skew should be eliminated by the shared close UT.

### 6. Non-destroyed background cache behavior stays unchanged

The background-end path scopes non-destroyed cache terminal UT to `endUT`, and existing tests such as `CheckDebrisTTL_MissingVessel_UsesStableCacheInsteadOfDestroyedFallback` assert stable-cache application. Do not write a background-path test expecting `RejectedTerminalBeforeLastSample` solely because the original non-destroyed cache UT was earlier than authored data.

Instead, assert either:

- the generic applier directly still rejects non-destroyed retrograde terminals; or
- the background path rejects when `endUT` itself is materially before `lastAuthoredUT`.

### 7. Predicted tail trimming remains owned by the generic applier

Optional but useful:

- cache has predicted segments before and after `lastAuthoredUT`;
- helper only adjusts `TerminalUT`;
- after apply, `RecordingFinalizationCacheApplier` retains or discards segments according to its existing rules;
- for the same-UT destroyed clamp, expected appended segment count is usually zero.

## Validation

Minimum local validation:

```powershell
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter RecordingFinalizationCache
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter BackgroundRecorder
dotnet build Source/Parsek.Tests/Parsek.Tests.csproj --no-restore
```

Full validation when practical:

```powershell
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj
```

Runtime validation, if the user reruns the KSP scenario:

- no `Apply rejected: consumer=EndDebrisRecording reason=RejectedTerminalBeforeLastSample`;
- no `Apply rejected: consumer=DeferredDestructionCheck reason=RejectedTerminalBeforeLastSample` for the same confirmed-destroyed stale-cache race;
- one clamp diagnostic per affected debris recording;
- saved debris recordings still have `terminalState = Destroyed`;
- `explicitEndUT` equals the final authored section endpoint;
- `.prec.txt` sidecars retain their final structural-event points.

## Documentation Updates

If implementation proceeds and behavior changes:

- update the existing `docs/dev/todo-and-known-bugs.md` item under the sub-surface / terminal-state evidence section;
- describe the narrow background-end clamp, not a global finalizer-cache relaxation;
- no schema or serialization docs should need changes because no persisted format changes are planned;
- update `CHANGELOG.md` under the current version with a short bug-fix entry if this becomes a committed code change.

## Decisions

1. Clamp eligibility is not based on `consumerPath == "EndDebrisRecording"`. It requires `TerminalState.Destroyed` plus explicit confirmed-destruction context. This covers `DeferredDestructionCheck` and only the destroyed/despawned `EndDebrisRecording` branch.
2. Diagnostic level:
   - Recommendation: `Info` initially, because it replaces a `Warn` and confirms the race was repaired. If frequent, downgrade later.
3. Should `TryPreservePreviousCacheAfterFailedRefresh` stop preserving a destroyed cache when the failed refresh was suppressed by recorded-point contradiction?
   - Recommendation: not in this patch. That is the broader upstream finalizer-quality task from the todo entry.

## Proposed Implementation Sequence

1. Add failing unit tests for the confirmed-destruction clamp, the non-destruction no-clamp case, and the `lastAuthoredUT > endUT` guard.
2. Add an explicit confirmed-destruction context to the background-end apply signature and propagate it from `DeferredDestructionCheck` and `CheckDebrisTTL` / `EndDebrisRecording`.
3. Make `EndDebrisRecording` cleanup use a shared close UT.
4. Add the background-only clamp helper in `BackgroundRecorder.cs`.
5. Call the helper immediately before `RecordingFinalizationCacheApplier.TryApply`.
6. Add clamp diagnostic logging.
7. Run focused tests.
8. Update `todo-and-known-bugs.md` and `CHANGELOG.md` if code changes are committed.
9. Re-run the retained-log mental model against the new behavior:
   - stale `Destroyed@348.532`;
   - authored `lastAuthoredUT=348.572`;
   - confirmed destroyed/despawned end context is true;
   - helper clamps to `348.572`;
   - generic applier accepts;
   - no fallback needed;
   - persisted recording remains identical in trajectory data, with cleaner terminal metadata.
