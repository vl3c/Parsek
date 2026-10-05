# Plan: remove player-authored looping from Parsek

*Plan written 2026-10-05 from an owner interview and four read-only code inventories (main at `7b424ca`). Nothing here is implemented yet. Companion: `docs/dev/gloops-recorder-design.md` (where visual looping goes).*

## 1. Goal and rulings

Parsek is a gameplay mod: timeline, career, logistics. Looping ghosts purely as background visuals is a Gloops concern. The engineering win is a smaller Parsek with one looping path instead of three.

Owner rulings, 2026-10-05:

1. **Per-recording player loops are removed entirely.** The Recordings tab loop toggle, period and time-unit editing, auto-loop range, and every code path that exists only to serve a loop the player set on a recording. Existing saves simply drop their per-recording loops; no backwards compatibility (the mod is in development).
2. **Missions loop only behind logistics routes.** A loop in Parsek is a real gameplay object with real effects (a supply route's runs, dispatches and deliveries), never a purely visual player choice. The Missions tab loses its loop controls.
3. **The loop infrastructure stays.** Span clock, loop units, periodicity, relaunch schedule, re-aim, phasing, arrival hold, seams, overlap, `LoopSyncParentIdx`: routes run on all of it.
4. **Retiring the persisted per-recording loop keys** follows the research recommendation in section 5 (owner asked for the best decision; it is recorded here for ratification at review).
5. **Nothing valuable is lost.** The code removed is the per-recording path; the shared machinery a future Gloops would fork stays in Parsek. The removed code is archived by a git tag and listed in the Gloops design doc (section 8).

## 2. How looping works today (verified)

There are two independent loop paths into the playback engine, plus routes riding the second:

| Path | Data | Engine entry | Who sets it |
|---|---|---|---|
| Per-recording loop | `Recording.LoopPlayback`, `LoopIntervalSeconds`, `LoopTimeUnit`, `LoopStartUT`, `LoopEndUT`, `LoopAnchorVesselId`, `LoopAnchorBodyName` | `GhostPlaybackEngine.ShouldLoopPlayback(traj)` (`GhostPlaybackEngine.LoopBounds.cs`) -> `UpdateLoopingPlayback` | the player, Recordings tab (Advanced only) |
| Mission loop unit | `Mission.LoopPlayback`, `LoopIntervalSeconds`, `LoopTimeUnit`, `LoopAnchorUT` | `MissionLoopUnitBuilder` -> `currentLoopUnits.TryGetUnitForMember` -> `UpdateUnitMemberPlayback` (`GhostPlaybackEngine.cs:1294`, ABOVE the per-recording gate) | the player, Missions tab (Advanced only, `UiSurface.MissionsLoopControls`) |
| Route loop | a runtime-only backing `Mission` built by `RouteBackingMission.BuildMission` every frame, never inserted into `MissionStore` | the same mission loop-unit path | route creation in the Logistics window |

Facts the plan rests on:

- The mission path never reads a member recording's `LoopPlayback` flag (`RecordingStore.SanitizeNonLoopableLoopPlayback` doc comment states the contract). It does read one other member field: `UpdateUnitMemberPlayback` step (1), per-member anchor gating (edge 17, `GhostPlaybackEngine.cs:2214-2227`), skips a member whose `LoopAnchorVesselId` is set and unloaded.
- A route never needs a player loop first: candidates come from recording trees, and `RouteCreationService` force-clears any player loop on the route's tree (`RouteTreeGuard.ForceClearManualLoopForRouteTree`, also run on every load). So every looping mission in `MissionStore` today is a player loop with no route behind it.
- The route backing mission sets `LoopTimeUnit = Sec` (`RouteBackingMission.cs:908`); the Auto time unit and `autoLoopIntervalSeconds` serve only player loops.
- **No player loop has a gameplay effect.** Replays never spawn (only the real first run does), reserve no crew, touch no ledger entry, and relay no CommNet signal (relay eligibility is the real run only). The only gameplay loop is the route's, through the delivery clock (`RouteOrchestrator.ProcessLoopRoute` -> `RouteLoopClock`).
- `LoopAnchorVesselId` / `LoopAnchorBodyName` have no production writer except the codec (legacy or harness-injected only).

## 3. What goes, what stays

### 3.1 Removed (player-authored loops)

**Per-recording loop (about 2.5-3k production lines):**

- UI: the Recordings tab loop column, row / group / block toggles, period cell and commit, bulk and aggregate helpers, `ApplyAutoLoopRange`, the "Looped by route" greying (`UI/RecordingsTableUI.cs`, about 600 lines).
- Engine: the per-recording dispatch, `UpdateLoopingPlayback`, `HandleLoopPauseWindow`, the auto-loop launch queue and schedule helpers (`GhostPlaybackEngine.cs`, about 800 lines); the `LoopBounds` partial; the per-recording half of `GhostPlaybackLogic.WarpLoopPolicy.cs` (`ResolveLoopInterval`, auto-launch queue, loop anchor, `ShouldSpawnLoopedGhost`, `IsAnchorLoaded`; about 310 lines).
- Hosts: KSC per-recording loop playback (`ParsekKSC.cs`, `ParsekKSC.Playback.cs`, about 350 lines); flight adapters (`ParsekFlight.cs`, loop-anchor-loaded tracking, about 220 lines); the per-recording half of `GhostMapPresence.OverlapSchedule.cs`; watch-mode per-recording entry and fallback.
- Loop anchor: `RelativeAnchorResolver`, `AnchorCandidateBuilder`, `DebrisRelativePlaybackPolicy` loop-anchor branches, the per-member anchor gate (edge 17) in the kept `UpdateUnitMemberPlayback` (dead once the field is gone, since nothing writes it), and `GhostPlaybackEngine.ShouldUseLoopAnchoredDebrisChain` (which walks `RecordingStore.PendingTree` / `CommittedTrees` to read `LoopAnchorVesselId`; removing it also removes one of the engine's direct back-edges into the store).
- Data: the seven `Recording` fields, their copy-constructor lines, `Recording.IsLoopableRecording`, the matching `IPlaybackTrajectory` members and `ReaimedTrajectory` pass-throughs, `RecordingSidecarStore.NormalizeDegenerateLoopInterval`, `RecordingStore.SanitizeNonLoopableLoopPlayback`, `RecordingStore.IsChainLooping`.
- Gates that existed only to keep player loops from interfering: crew auto-unreserve skip (`ParsekScenario.cs:4564`), the optimizer merge refusal for looping recordings (`RecordingOptimizer.cs:108-119`), the KSC spawn skip for `rec.LoopPlayback && !loopFirstRun`, the per-recording half of the first-run spawn seam. Removing them restores ordinary behavior.

**Player mission loop (Missions tab, about 1.6k lines):** in `UI/MissionsWindowUI.cs` the loop row and toggle, commit and cleared-loop announcements, double-clock advisory, period cell and editor (including the Auto unit), "Warp to..." and its confirm, next-launch countdown, periodicity display, the tab's own loop-unit set, the link-include loop-conflict clear, and the loop wording on Clone / Delete; the matching `MissionPresentation.cs` strings; Settings > Looping (`UI/SettingsWindowUI.cs`, `SettingsWindowPresentation.cs`) and the `autoLoopIntervalSeconds` / `autoLoopTimeUnit` settings; `MissionStore` loop-conflict machinery (`SetLoopEnabled` ON path, `ClearLoopsConflictingWith`, double-clock BFS, `NormalizeOneLoopPerTree`, the Clone loop-disarm) subject to section 6; the Auto time unit in `MissionLoopUnitBuilder`; `RouteTreeGuard`'s mutual-exclusion clear and its toast (nothing left to clear); `RecordingsTableUI.LoopToggleDisabledReason`; the `UiSurface.MissionsLoopControls` and `UiSurface.SettingsSectionLooping` gate keys.

### 3.2 Stays (route infrastructure)

`MissionLoopUnitBuilder.Build` / `BuildSignature` / `TryBuildLoopUnitForSelection`; the periodicity, phase-lock, zero-drift `MissionRelaunchSchedule`, re-aim, phasing-knob and arrival-hold pipeline; `GhostPlaybackLogic.SpanClock`; `RouteLoopClock`, `RouteCadence`; span-clock seams, `LoopSeamMarkerBuilder` / `Runtime`; overlap math and positioning (`UpdateOverlapPlayback`, `PositionLoopAtPlaybackUT`, `ReusePrimaryGhostAcrossCycle`, boundary-overlap secondary); `UpdateUnitMemberPlayback`; `LoopSyncParentIdx`; watch-mode cycle handling; `ParsekPlaybackPolicy` loop-restart / overlap-expiry / seam handlers; the shared half of `GhostPlaybackLogic.WarpLoopPolicy.cs`; `Mission.LoopPlayback` / `LoopIntervalSeconds` / `LoopTimeUnit` / `LoopAnchorUT` as builder inputs (the backing mission sets them); the `LoopTimeUnit` enum; the hidden `forceFaithfulLoopPlayback` setting; the Missions tab "Looped by route" / "Run by route" label (`RouteTreeGuard.RouteBindingFor`), which is the tab's only route surface.

### 3.3 Needs a decision during implementation

- **M-MIS-8 cross-tree partner merge inside the loop unit** (`MissionLoopUnitBuilder.cs:263-268`). `RouteBackingMission.BuildMission` never copies `IncludedForeignDockLinkIds`, so no route reaches it; only a seam-armed store mission would (section 6). Keep while lanes exercise it, or delete with its lanes. The selection and display halves of M-MIS-8 stay.
- **`MissionsWindowUI.ComputeNextRelaunchUT`** is called by the `StartLoopPlayback` seam; move it to a pure non-UI class before the UI goes.

## 4. Existing saves

- **Per-recording loops** vanish with their keys (section 5): a recording that looped loads as a non-looping recording.
- **Player mission loops:** one load sweep clears `LoopPlayback` on every `MissionStore` mission, in `ParsekScenario` OnLoad where `NormalizeOneLoopPerTree` runs today. No route check is needed: route backing missions are never stored. Log one Info line with the count when non-zero. The sweep is skipped when `ParsekSettings.AutomationEnvPresent` (the precedent is the hidden-settings clamp at `ParsekScenario.cs:3587`), so fixture-carried and seam-armed harness loops survive scene loads (section 6).
- **MISSION loop keys** (`Mission.Save` / `Load`) keep being written and read while the automation seam can arm a store-mission loop; in a player save they only ever hold the swept default.
- **Committed fixtures:** `harness/fixtures/saves/duna-one-recorded/persistent.sfs` carries the one `loopPlayback = True` (a MISSION node); the lanes that stage it (`saveTemplate`: H54-missions and V24W-duna-one-warp-stair) keep it under the automation exemption.

## 5. Retiring the per-recording loop keys (research decision)

**Decision: delete the writer AND the reader for all seven keys; no schema generation bump; committed fixtures are not re-harvested.**

Where they live: only in `RecordingTreeRecordCodec` on the `RECORDING` metadata node (`loopPlayback` and `loopIntervalSeconds` written on every recording; the other five sparse). None are in any sidecar: `.prec` (`TrajectorySidecarBinary`), the text codec, `.craft` and `.pann` carry no loop field, so no binary layout changes.

Why not the alternatives:

- **Generation bump (rejected).** `RecordingStore.IsRecordingSchemaCompatible` rejects the WHOLE recording on `generation-older`; the owner accepted losing loops, not every existing recording. It would also force a re-harvest of about 399 stamped fixtures. The rule's purpose (a loader never sees a shape it was not built for) is not at stake: the new loader handles the old shape by ignoring the keys, and an older loader reads a key-less node as loop-off through its field defaults, which is exactly the post-removal state.
- **Tolerant read that clears and logs (rejected).** The fields are deleted, so there is nothing to clear, and a compatibility read is the kind of seam the schema rule forbids adding.

Precedent: #734 stopped writing and reading `PRE_REFLY_ORIGINAL` on RECORDING nodes (`RecordingTreeRecordCodec.cs:239-244`), and `resumeBoundaryAnchorUT` was removed on RECORDING_TREE with a test pinning that the legacy key is ignored (`QuickloadResumeTests.cs:1641-1659`); neither bumped the generation.

Consequences:

- Committed fixtures keep inert `loopPlayback = False` / `loopIntervalSeconds` lines (437 harness RECORDING nodes in 51 files, 123 in 5 test fixture files; no fixture has a per-recording `True`). Nothing byte-compares C# writer output against a committed fixture; `test_career_earned_pad.py` is text-to-text through a Python builder and is unaffected.
- `RecordingBuilder` stops emitting the keys; its `WithLoop*` helpers go.
- Pin tests: (1) a gen-4 RECORDING node carrying all seven keys with non-default values loads schema-compatible, with no loop and no WARN, and saves back without any of them; (2) a writer pin that no saved RECORDING node contains any of the seven keys.
- The implementation PR adds this ruling to `.claude/CLAUDE.md` "Recording schema" (then `cp .claude/CLAUDE.md AGENTS.md`), proposed wording: "Deleting a retired feature's keys, when the existing reader already maps their absence to the feature-off default, is NOT a shape change and does NOT bump the generation: stop writing AND reading them; old saves load with the keys ignored and drop them on the next save, and committed fixtures keep the inert lines (precedent: #734 `PRE_REFLY_ORIGINAL`, `resumeBoundaryAnchorUT`; operator ruling 2026-10-05, per-recording loop keys)." The same ruling covers the `isGhostOnly` key when the in-Parsek Gloops recorder is deleted.
- The same file's text about loop-relative playback ("Loop Relative playback stays on the live-PID contract via `Recording.LoopAnchorVesselId`", and the loop-anchored debris chain sentence in the parent-anchored contract) is rewritten in the same PR.

## 6. Harness strategy

The harness is the main cost. 60 committed specs loop a `MissionStore` mission WITHOUT a route (48 operator tier, 12 nightly): LF-1/LF-2, GS-12, GUI-17/18/19, the player-loop V*M family, the arrival V*T/K/A family, the re-aim map-dwell lanes (V2, V3F, V3R, V3C, V24W, V8F), and MS-1, MC-5, AP-1, AT-1, EVA-5, SE-1, V7W, W1. They exercise the loop-unit, re-aim, periodicity and arrival pipeline that routes run on; most have no route shape (no supply run to dock), so they cannot simply become route lanes.

**Recommendation: keep store-mission looping reachable from automation only.** The `MissionConfig` seam (arms the loop through `MissionStore.SetLoopEnabled`) and `StartLoopPlayback` stay; the player UI goes; the load sweep is skipped under `AutomationEnvPresent` (section 4). The engine and builder already loop any mission in the list, so this costs only the seam-side helpers. The lanes keep testing exactly the infrastructure routes depend on. Long term, lanes that fit a route shape migrate to `RouteCommand` and the seam path shrinks.

Retired with the per-recording path: `OC-1-overlap-cap-per-recording` (injected `loopPlayback = True`), `RL-1-relative-loop-live-anchor` (`InjectRelativeLoopAnchor`), and the `S1.9-part-showcase-render` required line `SanitizeNonLoopableLoopPlayback: cleared LoopPlayback on ...` (the sanitizer is deleted). CN-1 and GS-6 mention the sanitizer only in comments. Coverage registry: D3 `relative-loop` loses its witness (retire the value); D6 `overlap-expiry-soft-caps` keeps its GS-12 half; D6 `loop-period-modes` loses its third mode (the global Auto period) with the Auto unit: redefine the value to the two remaining modes and re-pin GS-12, the only committed spec that drives `unit=auto`. The GUI census lanes that photograph Missions-tab loop controls (GUI-17, GUI-18, GUI-19) are re-pinned against the new tab.

Every spec, registry and status change goes through `docs/dev/autotest-status.md` (the status authority) in the PR that makes it.

## 7. PR sequence

Each PR keeps xUnit and the harness Python suites green and updates CHANGELOG / todo / design docs in the same commit (CLAUDE.md "Documentation updates").

0. **This plan** (docs only).
1. **[DONE on branch `player-loop-removal`, phase A; tag owed at merge]** **Tag the archive**, then **delete the in-Parsek Gloops recorder** (`docs/dev/gloops-recorder-design.md` section 7 removal inventory; retires GL-1, GL-2, GUI-16; the `isGhostOnly` key under the section 5 ruling). Push annotated tag `archive/gloops-recorder-2026-10` at the main commit before this PR (outward-facing: the implementing session asks first). Independent of the rest; may go first or in parallel. Before merging: one reload test to confirm no `IsGhostOnly` recording can survive in a save (todo GLOOPS-EXTRACTION-2026-10-05 item 1).
2. **Remove the player loop UI and sweep saves** (behavior change, small). Recordings tab loop column; Missions tab loop controls (keep the route label); Settings > Looping; the two `UiSurface` keys (update `docs/dev/design-gui-inventory.md` and `design-ui-basic-advanced.md`; `TooltipEchoBudgetTests` will move); clear per-recording loops on load (widen the existing sanitizer to all recordings for this one PR) and add the store-mission sweep (section 4); retire OC-1, RL-1, the S1.9 line; re-pin the GUI census lanes; rewrite `docs/user-guide.md` loop sections. After this PR nothing in a player build writes a per-recording loop, so that path is provably dead.
3. **Tag the archive**, then **delete the dead per-recording path** (large, mechanical). Push annotated tag `archive/player-loops-2026-10` at the main commit before this PR (outward-facing: the implementing session asks first). Delete everything in section 3.1, the seven codec keys under section 5 with its pin tests and CLAUDE.md ruling, the Auto unit and auto-loop settings, the per-recording xUnit files (about 12 deleted, about 30 edited: AutoLoopTests, LoopAnchorTests, LoopIntervalLoadNormalizationTests, LoopPhaseTests, RecordingBuilderLoopIntervalTests, RelativeLoopAnchorFixtureTests, ResolveLoopIntervalWarnDedupeTests, IsLoopableRecordingTests, LoopRecordingCrewReservationTests, ChainLoopFirstRunSpawnTests, plus the per-recording halves of LoopFirstRunSpawnTests and OverlapPerInstanceTests) and the per-recording in-game tests (`RuntimeTests` loop-cycle reuse group #406 / #461 / #613, and the per-recording cells in `LogisticsRouteTreeGuardRuntimeTests` / `JointArrivalHold`). Decide section 3.3.
4. **Reframe the docs** that still describe player looping (can ride with 2 and 3 per "docs move with code"): `docs/parsek-missions-design.md` (sections 1.1, 1.2, 6, 7, 9), `docs/parsek-flight-recorder-design.md` section 10 "Looped Recordings" and 12.7, `docs/parsek-ghost-trajectory-rendering-design.md` 7.10 and 15.14, `docs/parsek-logistics-supply-routes-design.md` 0.5 / 0.6 (mutual exclusion becomes history), `design-mission-periodicity.md`, `design-mission-phasing-alignment.md`, `design-reaim-launch-hold-seam.md`, `design-reaim-heliocentric-parking-departure.md`, `design-mission-crosstree-dock.md`, `design-mission-multimoon-alignment.md`, `design-mission-abstractions.md`.

## 8. Verification

- Unit: the section 5 pin tests; a sweep test (store mission with `LoopPlayback = true` loads cleared with one Info line; skipped when `AutomationEnvPresent`); the route label still draws for a route-bound tree; a route still loops, dispatches and delivers (existing logistics tests).
- Harness: daily and nightly tiers on request after PR 2 and PR 3 (the operator flies them; CLAUDE.md "Harness flights are run on request only").
- Grep gates: `GrepAuditTests` and `scripts/grep-audit-*.ps1` stay green; grep the tree for the removed identifiers (`ShouldLoopPlayback`, `UpdateLoopingPlayback`, `LoopAnchorVesselId`, `IsLoopableRecording`, `autoLoopIntervalSeconds`, `MissionsLoopControls`) before merging PR 3.
- In-game: Missions, Logistics, MapRender and the loop-cycle categories on the dev instance after PR 3.

## 9. Open items for the owner

1. Ratify section 5 (key deletion without a generation bump, and the CLAUDE.md wording).
2. Confirm section 6 (automation-only store-mission looping keeps the 60 lanes) versus retiring lanes that have no route shape.
3. Whether the Missions tab should show a route-bound mission's next dispatch (read-only) now that the player countdown is gone, or leave that to the Logistics window, which already shows the route's loop-unit countdown.
