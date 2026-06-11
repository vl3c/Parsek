# Plan: M-MIS-9 - freeze route backing selection to creation-time members

**Branch:** `mmis9-new-branch-inclusion` (origin/main tip 6c8cf23d2).
**Origin:** todo M-MIS-9 ("new-branch inclusion policy"). Investigation (2026-06-11, this branch) overturned the framing - the fix is ROUTE-SIDE, and no Mission/MissionStore/codec change is needed.

## 1. Findings (verified)

**Mission side (as the todo stated):**
- `Mission` persists only EXCLUDED sets (`Source/Parsek/Mission.cs:18,25`); an interval is included unless excluded (`MissionIntervalSelection.cs:65-66`), so any new selectable key joins every Mission. `MissionStore.ReconcileSelections` (`MissionStore.cs:101-162`) only drops stale excluded ids, load-time only (`ParsekScenario.cs:3172`).
- `MissionStructureBuilder.Build` walks ALL tree recordings including superseded (`MissionStructure.cs:133-159`) - no ERS filter in the structure/composition/loop-unit pipeline.
- BUT playback suppresses superseded recordings per-recording: `ParsekFlight.ComputePlaybackFlags` (`ParsekFlight.cs:19141-19199`) flags `SupersededByRelation` via `EffectiveState.ComputeTimelineInactiveRecordingIds` and blocks render+spawn. After a re-fly the superseded subtree goes dark and the new fork joins -> net effect for the default all-included mission is REPLACE, the desired semantics (`RecordingTreeSplitter.cs:340`: HEAD keeps the origin id, TIP gets a fresh id).

**Route side (the load-bearing part):**
- A route's backing Mission is synthesized per tick from `Route.BackingMissionTreeId` + the CREATION-TIME-frozen `Route.ExcludedIntervalKeys` (`RouteBackingMission.cs:421-464`, `Route.cs:195-223`, captured once in `RouteBuilder.cs:233-296`), evaluated against the CURRENT tree.
- The delivery clock is NOT independent of the render: `RouteOrchestrator.ResolveLoopUnit` (`RouteOrchestrator.cs:840-908`) rebuilds the loop unit per tick; `RouteLoopClock` derives dispatch cycles from `SpanStartUT/SpanEndUT/CadenceSeconds` (`RouteLoopClock.cs:107-117,273-304`); cadence = `max(DispatchInterval, span)` (`MissionLoopUnitBuilder.cs:194-197`). **Span extension = delivery-cadence change.**
- Routes ARE protected on their member path: every supersede bump triggers `RouteStore.RevalidateSources` immediately (`ParsekScenario.cs:252-263`), checking every `SourceRef` against ERS + a field fingerprint -> `MissingSourceRecording`/`SourceChanged` -> not ghost-driving, no dispatch (`RouteStatusPolicy.cs:79-101`).
- Routes are NOT protected from branches landing OUTSIDE the member path: (a) a re-fly rooted in the excluded post-undock subtree (supersedes only excluded recordings; SourceRefs stay ERS-valid), (b) a switch-fly continuation segment merging onto the SAME committed tree id (`ParsekFlight.cs:8806-8945`) with no supersede bump at all.
- PR #1119 (mission-vesselorbital-tier1) touched only periodicity/constraint extraction; picture unchanged.

## 2. The real exposure

- **Player missions:** new-branch join is mostly the INTENDED replace semantics. Residual gap (fork of an excluded branch joining a curated mission) is render-only, cosmetic. No fix now.
- **Routes (real, delivery-affecting):** a new branch landing on a route-bound tree without touching the member path silently joins the synthesized backing mission -> loop-unit span extends past the dock -> cadence inflates -> dispatch/delivery cycles stretch and the rendered loop shows the foreign branch.

## 3. Chosen fix

**Route-side freeze, derived from data the Route already persists.** Known-at-creation base recording ids = `SourceRefs[].RecordingId` UNION base leg ids of `ExcludedIntervalKeys` (`/segN` stripped). Any current selectable composition key whose base recording id is NOT in that union is a post-creation branch -> auto-excluded from the synthesized backing mission.

Why: closes the only non-cosmetic gap; no Mission/MissionStore/codec/schema change (works on existing saves, no migration); applied at the single synthesis chokepoint (`RouteBackingMission.BuildMission`) so render, delivery clock, and signature rebuilds all see it the same frame - no staleness window. The base-id rule keeps a new `/segN` re-peel of a known member recording included.

## 4. Implementation steps

1. `Source/Parsek/Logistics/RouteBackingMission.cs`:
   - Pure `internal static HashSet<string> ComputeAutoExcludedNewIntervalKeys(RecordingTree tree, Route route)`: guards (null tree/route -> empty; empty `route.ExcludedIntervalKeys` -> empty, preserving the honest whole-segment-fallback contract); build structure+composition (reuse existing walk); known bases = SourceRef ids UNION `StripSegMarker(excludedKey)`; return selectable keys whose base is not known. Batch-count Verbose log.
   - `BuildMission`: gate derivation behind a topology signature (`BranchPoints.Count`/`Recordings.Count`, mirroring `MissionLoopUnitBuilder.BuildSignature`'s fold) cached on the Route; on change, re-derive, `ParsekLog.Info` once ("route X auto-excluded N new interval key(s); new branch joined tree Y after creation"), union the cached keys into the synthesized mission's `ExcludedIntervalKeys`. Fold the count into the existing rate-limited BuildMission log.
2. `Source/Parsek/Logistics/Route.cs`: two NON-serialized runtime cache fields (`AutoExcludedNewIntervalKeys`, `AutoExcludeTopologySignature`) with doc comments. No codec change.
3. No change to `MissionStore`, `MissionIntervalSelection`, `MissionLoopUnitBuilder`, UI, or call sites.

## 5. Tests (extend `RouteBackingMissionTests.cs` + `RouteBackingMissionLoopUnitTests.cs`, reuse `BuildLaunchDockUndockTree`)

- Post-creation fork at the undock BP -> key auto-excluded; loop-unit span end stays the dock UT (the delivery-cadence assertion).
- Continuation segment appended to a member through-line -> trimmed window drops it, span stable.
- New `/segN` peel of a known member recording -> NOT auto-excluded (base-id rule).
- Empty `ExcludedIntervalKeys` (honest-fallback route) -> derivation returns empty.
- Signature cache: unchanged tree -> no re-derivation (log-sink assert); changed counts -> re-derives.
- Existing BuildMission tests stay green. In-game testing not required - all touched seams are pure.

## 6. Docs

- CHANGELOG: "Supply routes: branches added to a route's source tree after creation (re-fly fork, switch-fly continuation) no longer silently join the route's rendered loop and delivery span; the backing selection freezes to the creation-time member set."
- todo: flip M-MIS-9 with the verdict (routes were already protected on the member path; real gap was out-of-path branches, fixed route-side; player-mission join is intended replace semantics, curated-exclusion residual stays parked as cosmetic).
- Design doc section 0: one-paragraph note that the backing-mission selection freezes to creation-time members.
