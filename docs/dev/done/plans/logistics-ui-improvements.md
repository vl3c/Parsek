# Logistics Window UI Improvement Plan

Status: REVIEWED, ready for implementation (two review+fix passes against the code; all symbol/line claims verified)
Branch base: `logistics-v0-implementation` (this plan authored on `logistics-ui-plan`, base commit `b1d2611e`)
Scope: the Logistics window (`Source/Parsek/UI/LogisticsWindowUI.cs`) and its creation dialog (`Source/Parsek/UI/RouteCreationDialog.cs`), plus small shared-style touch-ups and two new accessors in the allowlisted Logistics layer (H1, see below). No changes to route runtime/model semantics unless a work item says so explicitly.

This plan was reviewed against the actual code; corrections from that pass are folded in (notably H1, H2, H6).

---

## 1. Goal

Make the Logistics supply-route window intuitive and make it look and behave like Parsek's other windows. The window works today but reads like a debug table: raw enum status names, bare lat/lon and 8-char GUID fragments instead of vessel/recording names, a leftover 30-second debug interval on the window's own Create path, no delete confirmation, and no feedback on whether fuel actually arrived or when the next delivery fires.

Four thrusts:
1. Make creation informed and one-click (a real summary plus "Create and Activate", with the real span interval).
2. Make status and delivery legible at a glance (plain-English status, realized vs planned amounts, a "next delivery in" countdown, an always-visible delivering / not-delivering badge).
3. Name things the player named (destination vessel, source recording / mission), not coordinates and GUIDs.
4. Bring the window in line with house style (shared header style, bottom tooltip echo box, delete confirmation, in-window rename).

This document is the implementation brief. A fresh agent should be able to execute it without the originating conversation.

---

## 2. How to work (process for the implementer)

- Work in a dedicated sibling worktree off `logistics-v0-implementation`. Do not edit the main `Parsek/` checkout.
- Land work as GitHub PRs against `logistics-v0-implementation` (this is a feature branch, not `main`). One PR per phase below is a good granularity; smaller is fine.
- Before each commit, build, deploy, and verify the deployed DLL per `.claude/CLAUDE.md` (byte match plus a UTF-16 string grep for a new label you added). Run `dotnet test`.
- Every new method with logic needs unit tests and verbose logging. Pure/presentation logic should be `internal static` so it is directly testable. Follow the established split: pure formatting/decision helpers in a `*Presentation` or `*Formatters` file, IMGUI drawing in the `*UI` file (see `SpawnControlUI`/`SpawnControlPresentation`, `RecordingsTableUI`/`RecordingsTableFormatters`). Test precedent already exists: `LogisticsWindowUISendingButtonTests.cs`, `RouteCadenceTests.cs`.
- Update `CHANGELOG.md` (1 line per item, user-facing, under `## 0.10.0`) and `docs/dev/todo-and-known-bugs.md` in the same commit that changes behavior.

### Hard constraints (do not violate)
- No em dashes anywhere (chat, code comments, CHANGELOG, commits, PRs). Use a colon, parentheses, comma, or split the sentence.
- Plain ASCII only in markdown and UI strings. No emoji, no special Unicode.
- No `Co-Authored-By` and no AI-attribution lines in commits or PRs.
- The Missions subsystem is consumed read-only. Do not modify any file under the locked Missions set; its git diff must stay empty.
- ERS/ELS grep gate must stay green (`scripts/grep-audit-ers-els.ps1`, enforced by `GrepAuditTests`). The gate matches ONLY the literal patterns `\.CommittedRecordings\b` and `\bLedger\.Actions\b`. Reading the list returned by `EffectiveState.ComputeELS()` / `ComputeERS()` does NOT trip the gate, so the ledger-scan work (H2, H3) is gate-safe with no allowlist change. The risk case is H5 (recording-id -> name): if it literally references `.CommittedRecordings` it WILL trip the gate. `Source/Parsek/UI/LogisticsWindowUI.cs` is NOT currently in `scripts/ers-els-audit-allowlist.txt` (ParsekUI.cs, RecordingsTableUI.cs, GroupPickerUI.cs, SettingsWindowUI.cs, MissionsWindowUI.cs, TimelineWindowUI.cs are). For H5 either use a literal-free by-id name resolver or add LogisticsWindowUI to the allowlist with a one-line rationale.
- InvariantCulture for all numeric formatting in UI strings (`ToString(..., CultureInfo.InvariantCulture)`), including countdowns and amounts.

### Note on line numbers
All `file:line` references are as of base commit `b1d2611e` and have already drifted from earlier refactors (e.g. `CreateRouteFromCandidate` is at `LogisticsWindowUI.cs:561`, not the 573 an earlier draft cited). Treat every line ref as a "start here" anchor; grep the named symbol to confirm before editing.

---

## 3. Style references (the house idioms to adopt)

Read these before touching the window so the result matches the rest of the mod:

- Section headers: `parentUI.GetSectionHeaderStyle()` (used in `SettingsWindowUI.cs:256`). The Logistics window currently hand-rolls a tinted-text header (`LogisticsWindowUI.cs:820-821`, `DrawSectionHeader :496-500`).
- Column headers + sorting: `parentUI.DrawSortableHeaderCore` (`ParsekUI.cs:807-839`); cached sorted-results pattern as in `SpawnControlUI`.
- Bottom tooltip echo box: wrapped-tooltip pattern at `SettingsWindowUI.cs:221-226` and `SpawnControlUI.cs:349-358`.
- Confirmation dialog: `MultiOptionDialog` + "This cannot be undone." as used by Wipe-All-Recordings at `ParsekUI.cs:746-801`.
- Deferred-commit text edit (rename, direct entry): `GUI.SetNextControlName` + Enter / click-outside commit. Settings example at `SettingsWindowUI.cs:294-344`; full rename flow with write-back + log in the Recordings table (`SetNextControlName("RecRename")` at `RecordingsTableUI.cs:1797`, click-outside commit at `:801`, write-back at `:3860`).
- Countdown formatting: confirm the class at build time; the plan assumes a `FormatCountdown` helper used by Spawn Control (`SelectiveSpawnUI` / `SpawnControlPresentation`). Grep for `FormatCountdown` and reuse the real one.
- Status color palette: currently duplicated across `RecordingsTableUI.EnsureStatusStyles` and `LogisticsWindowUI.EnsureStyles` (`:805-837`). House palette: green `(0.55,1,0.55)`, yellow `(1,1,0.4)`, red `(0.95,0.45,0.45)`, cyan `(0.65,0.85,1)`. Note the Logistics grey is `(0.7,0.7,0.7)` (`:816`) and detail text `(0.8,0.8,0.8)` (`:836`); pick the canonical grey when centralizing (L4).
- Main-window button with live count: Real Spawn Control button at `ParsekUI.cs:191-208` ("Real Spawn Control ({0})" + `GUI.enabled`). Note it does NOT demonstrate a color tint; for a red broken-state tint hand-roll `GUI.color`/`GUI.backgroundColor`.
- Screen toast: `ParsekLog.ScreenMessage(string, float)` wrapper (`ParsekLog.cs:428`; example usages near `ParsekUI.cs:769` / `:793`). Do NOT copy `GroupPickerUI.cs:59-69`, which calls the raw `ScreenMessages.PostScreenMessage` directly.

---

## 4. Gameplay model the UI must reflect (ground truth)

Read `docs/parsek-logistics-supply-routes-design.md` and the `Source/Parsek/Logistics/` files. Key facts the UI must honor:

- A route is backed by a Mission segment built from a recorded launch-to-dock run. Every v0 route is a loop route (`Route.cs:232-240`).
- Delivery fires on the loop clock when it crosses the recorded dock UT, not on a self-timer. The legacy `NextDispatchUT` self-timer is dead for loop routes; the live truth is the loop clock. IMPORTANT (see H1): `RouteLoopClock` has no "seconds to next crossing" accessor and the loop unit it needs is built privately inside the ERS-allowlisted `RouteOrchestrator`.
- Cadence is `N x span` (the natural recorded span times an integer), floored at 1x. `RouteCadence` owns `StepMultiplier` / `ApplyMultiplier` / `DeriveDispatchInterval` (no target-duration snap helper exists today).
- Delivery amounts are clamped to live destination capacity by `RouteDeliveryPlanner.PrepareDelivery`, so realized delivered can be far less than planned. Each delivery writes a `RouteCargoDelivered` ledger row carrying `RouteResourceManifest` (actual) and, when short, `RouteRequestedResourceManifest` (requested), around `RouteOrchestrator.cs:1226-1227`; the read shape exists at `RouteOrchestrator.cs:1284-1312` (`IsDeliveryAlreadyInLedger`).
- Source -> destination identity: the endpoint carries a target vessel PID; resolve it with `RouteEndpointResolver.TryResolveEndpoint(endpoint, out Vessel v, out string reason)` (`RouteEndpointResolver.cs:41`), which returns a live `Vessel` (use `.vesselName`). PID is the route's primary identity.
- Status lives in `RouteStatus` / `RouteStatusPolicy`. Blocked-but-active states (WaitingForResources, WaitingForFunds, DestinationFull, EndpointLost) still fly the ghost but transfer nothing; they retry on `NextEligibilityCheckUT`. `SkippedCycles` increments on a blocked cycle. `RouteStatusPolicy.GhostDriving(RouteStatus)` (`RouteStatusPolicy.cs:75`) tells you whether the ghost is flying.
- Creating or activating a route calls `RouteTreeGuard.ForceClearManualLoopForRoute`, which disables the backing tree's manual Loop toggle (mutual exclusion). Deleting the route re-enables the toggle but does not restore the prior loop-on state.

---

## 5. Work breakdown

Each item: ID, dimension, priority, effort, the problem, the change, acceptance criteria, tests. Implement phases in order; within a phase, items are independent unless noted.

### Phase 1: Quick wins (one PR)

**QW1. Remove the 30s debug interval from the window Create path**
- Dimension: ScenarioCoverage. Priority: High. Effort: S.
- Problem: `CreateRouteFromCandidate` (`LogisticsWindowUI.cs:561`) hardcodes a 30s interval and bypasses the dialog, so window-created routes differ from dialog-created ones and show a nonsense "Interval: 30s".
- Change: compute the real interval via `RouteCreationDialog.ComputeRootToUndockSpan` (`RouteCreationDialog.cs:184`, the exact helper the dialog uses) and pass it to `RouteBuilder.BuildRoute`. (The helper name says "undock" for historical reasons; per recent logistics work the recorded segment ends at the dock, so confirm it returns the dock-based span, but use the SAME helper the dialog uses so the two paths match.)
- Acceptance: a route created from the window has the same interval a dialog-created route would; no "30s" appears.
- Tests: unit test asserting the window path interval == dialog path interval for the same candidate analysis.

**QW2. Delete confirmation on the route X button**
- Dimension: UX. Priority: High. Effort: S.
- Problem: the X deletes immediately (`LogisticsWindowUI.cs:337-338`, `:533-537`).
- Change: route the delete through a `MultiOptionDialog` confirm ("Delete route '<name>'? This cannot be undone.") reusing the Wipe-All pattern at `ParsekUI.cs:746-801`.
- Acceptance: clicking X opens a confirm dialog; route is deleted only on confirm.
- Tests: if a pure "should-confirm/which-button" decision is extracted, unit-test it; otherwise assert the dialog-spawn path via a presentation helper.

**QW3. Shared section header style**
- Dimension: StyleConsistency. Priority: Medium. Effort: S.
- Problem: hand-rolled tinted-text headers diverge from the house bold recessed box bar.
- Change: replace `sectionHeaderStyle` usage in `DrawSectionHeader (:496-500)` with `parentUI.GetSectionHeaderStyle()`.
- Acceptance: section headers visually match Settings/Recordings windows.

**QW4. Plain-English status in the Status cell**
- Dimension: UX. Priority: High. Effort: S.
- Problem: the cell shows `route.Status.ToString()` (`:301-302`); the readable reason exists (`:766-781`) but only on hover.
- Change: render the mapped status reason text in the cell using the existing window-private `StatusReason(RouteStatus)` helper (`LogisticsWindowUI.cs:766-781`), moving it from the tooltip into the cell; keep the raw enum (if useful) in the tooltip.
- Acceptance: a WaitingForResources route reads "Waiting for resources" (the existing reason string), not "WaitingForResources".

**QW5. Show SkippedCycles alongside CompletedCycles**
- Dimension: GameplayLogic. Priority: Medium. Effort: S.
- Problem: the Cyc column shows only `CompletedCycles` (`:299`); blocked cycles are invisible.
- Change: render "3 / 1 skipped" when `SkippedCycles > 0`.
- Acceptance: a yellow blocked route shows a nonzero skipped count.

**QW6. Bottom tooltip echo box**
- Dimension: StyleConsistency. Priority: Medium. Effort: S.
- Problem: the window's many `GUIContent` tooltips render only as KSP's floating tooltip, unlike the rest of the mod.
- Change: add the wrapped `GUI.tooltip` echo box at the window bottom (pattern at `SettingsWindowUI.cs:221-226`).
- Acceptance: hovering any control echoes its tooltip in-window.

**QW7. Remove or relabel the inert single tab**
- Dimension: UX. Priority: Low. Effort: S.
- Problem: the single-element "Supply Routes" toolbar tab (`TabLabels :79`, drawn `:194`) reads as an interactive control that does nothing.
- Change: drop the tab strip until a second tab exists (preferred), or render it as a static title.
- Acceptance: no dead tab control.

**QW8. Live count + tint on the main Logistics button**
- Dimension: UX. Priority: Low. Effort: S.
- Problem: the main-window Logistics button (`ParsekUI.cs:224`) has no at-a-glance state.
- Change: show "Logistics (N)" with N = route count (reuse the live-count label idiom from the Real Spawn Control button, `ParsekUI.cs:191-208`). For the broken-state red tint there is no precedent at that button (count + `GUI.enabled` only), so hand-roll it: wrap the button in `GUI.color`/`GUI.backgroundColor` set to the house red `(0.95,0.45,0.45)` when any route is in a hard-broken state (EndpointLost / invalid), then restore.
- Acceptance: button reflects route count and a broken-state tint.

### Phase 2: Legibility (status, delivery, naming) [High priority]

**H1. "Next delivery in M:SS" driven by the loop clock**
- Dimension: GameplayLogic. Effort: M. NOT UI-only: requires a new accessor in the allowlisted Logistics/orchestrator layer (see below).
- Problem: the detail panel's "Next dispatch in <countdown>" reads the dead `NextDispatchUT` self-timer (`:425-431`), meaningless for loop routes.
- Reality check: `RouteLoopClock` has NO "seconds to next dock crossing" accessor (its surface is `TryGetRouteLoopState` / `IsDockUTInSpan` / `ComputeDockCycleIndex` / `IsDockCrossing` / `DescribeState`), and all of them need a `GhostPlaybackLogic.LoopUnit`, which is built only by the `private static RouteOrchestrator.ResolveLoopUnit` (`RouteOrchestrator.cs:621`) over the raw `RecordingStore.CommittedRecordings`/`CommittedTrees` (the reason `RouteOrchestrator.cs` is ERS/ELS-allowlisted). The window cannot get a LoopUnit on its own without tripping the grep gate.
- Change: add a new `internal static` accessor in the allowlisted Logistics layer, e.g. `RouteOrchestrator.TryComputeSecondsToNextDockCrossing(Route route, double nowUT, out double seconds)`, that builds/uses the LoopUnit internally (keeping the raw-list read inside the already-allowlisted orchestrator). The window calls only that accessor and formats the result with the real `FormatCountdown` helper in a new always-visible "Next delivery" column + the detail panel. For blocked wait states, show the `NextEligibilityCheckUT` retry countdown ("rechecks in 0:23") instead.
- Cost: the LoopUnit build is not free. Do NOT call the accessor per IMGUI frame. Throttle/cache it like the existing candidate cache (recompute on a timer or on route/recording change) and reuse the cached value while drawing.
- Acceptance: an active route shows a live, decreasing "Next delivery" time that aligns with when a cycle actually fires; the new accessor lives in the allowlisted Logistics layer so `grep-audit-ers-els` stays green.
- Tests: unit-test the pure crossing-to-seconds math by injecting a fake LoopUnit via the existing `RouteOrchestrator.LoopUnitResolverForTesting` seam (`RouteOrchestrator.cs:429`) so no live KSP is needed; also test the wait-state fallback; keep the raw-list build behind the orchestrator accessor.

**H2. Realized delivery (actual vs planned) plus cumulative total**
- Dimension: GameplayLogic. Effort: M. NO new persisted field, NO `RouteCodec` change.
- Problem: only the planned manifest shows ("Delivers per cycle: LiquidFuel 150.0", `:419-436`); the clamped realized amount is logged only.
- Change: compute realized + cumulative entirely from the ledger by scanning `EffectiveState.ComputeELS()` for `RouteCargoDelivered` rows matching `route.Id` (this does NOT trip the grep gate). Each row carries `RouteResourceManifest` (actual) and, when short, `RouteRequestedResourceManifest` (requested); read shape exists at `RouteOrchestrator.cs:1284-1312`, written at `:1226-1227`. Show "Last cycle: delivered 40 of 150 LF (110 did not fit)" in yellow on shortfall (latest row by UT), plus a cumulative "Total delivered: 1,240 LF" line = sum of `RouteResourceManifest` across all this route's rows.
- Acceptance: after a clamped cycle the panel shows the real delivered amount and the shortfall; the total accumulates across cycles; no save-format change.
- Tests: a pure formatter `FormatRealizedDelivery(requested, actual)` and a pure ledger-summary helper taking a list of (actual, requested) manifests; unit-test full / partial / zero cases.

**H3. Always-visible delivering vs "flying, not delivering" badge**
- Dimension: GameplayLogic. Effort: M.
- Problem: blocked-but-active routes still fly the ghost but transfer nothing; the flying ghost reads as success and the truth is buried in a tooltip.
- Change: add a compact glyph/column. There is no "last outcome" field on `Route`; derive it from the latest `RouteCargoDelivered` ELS row for the route (reuse H2's scan): full vs partial (was `RouteRequestedResourceManifest` set) vs none. Combine with `RouteStatusPolicy.GhostDriving(route.Status)` (`RouteStatusPolicy.cs:75`): green "Delivering" when driving and the last cycle delivered, yellow "Flying, not delivering" when `GhostDriving` is true but the last cycle was blocked (SkippedCycles incremented / no fresh row).
- Acceptance: a WaitingForResources route that is visibly flying shows "Flying, not delivering".
- Tests: a pure `ClassifyDeliveryBadge(ghostDriving, lastOutcome, skippedCycles)` -> enum; unit-test the delivering / blocked / paused / new permutations.

**H4. Name the destination vessel instead of bare coordinates**
- Dimension: UX. Effort: M.
- Problem: destination shows "<Body> (orbit|surface) <lat>,<lon>" (`FormatEndpointShort`, `:669-690`); two bases read alike and no name appears though the endpoint carries a PID.
- Change: in `FormatDestination`, resolve the endpoint PID via `RouteEndpointResolver.TryResolveEndpoint(endpoint, out Vessel v, out string reason)` (`RouteEndpointResolver.cs:41`) and show `v.vesselName` when it returns true; fall back to body + situation + coords only when it returns false. Keep coords in the hover tooltip for the fallback case.
- Acceptance: routes to named, loaded vessels show the vessel name in the Destination column. Note: the window opens in both FLIGHT and SPACECENTER (`ParsekUI.cs:222`); in SPACECENTER the target may be unloaded, so resolution can legitimately miss and fall back to coords. That is expected, not a bug.
- Tests: a pure formatter taking a resolved-or-null name + endpoint and producing the display string; unit-test resolvable and fallback paths.

**H5. Recording / mission names instead of 8-char GUID fragments**
- Dimension: UX. Effort: M.
- Problem: detail panels expose raw 8-char ids ("Source recordings: <id, id...>", `FormatSourceRecordingIds :746-756`; candidate detail `:478-486`).
- Change: resolve each source recording id to its display name (and owning tree/mission name) and show "Source: Mun Fuel Run (rec 3 of tree 'Munar Logistics')". Keep the short id as a hover tooltip only.
- ERS/ELS gate: resolve names via a by-id lookup that does NOT literally reference `.CommittedRecordings` (an `EffectiveState`/`RecordingStore` by-id name resolver). If no literal-free accessor exists, either add one or add `Source/Parsek/UI/LogisticsWindowUI.cs` to `scripts/ers-els-audit-allowlist.txt` with a one-line rationale (it is NOT currently allowlisted).
- Acceptance: source rows show names that cross-reference the Recordings/Missions windows.
- Tests: a pure formatter mapping (id -> name) lookups to the display string; unit-test with a stub resolver.

### Phase 3: Informed creation flow [High priority]

**H6. Wire Create Route to a real summary plus "Create and Activate"**
- Dimension: ScenarioCoverage. Effort: M.
- Problem: the window Create button (`CreateRouteFromCandidate`, `:561`) builds with the placeholder interval and lands silently in Paused, so first-timers think the feature is broken because nothing flies.
- Reality check: this is a NEW dialog flow, not a button swap. `RouteCreationDialog.Spawn` and its `OnConfirm` are `private static` and hardwire a two-button ("Create Route"/"Cancel") dialog that only builds and never activates (`RouteCreationDialog.cs:233-319, 342-418`); editing it would also change the post-commit auto-dialog. The only reusable pieces are `RouteCreationFormatters.BuildSummaryBlock(result, mode, tree)` (`RouteCreationFormatters.cs:112`) and `ComputeRootToUndockSpan` (`RouteCreationDialog.cs:184`).
- Change: add a NEW candidate-driven confirm (a window-owned `MultiOptionDialog`, or a new public entry on the dialog class) that renders `BuildSummaryBlock` for the selected candidate and offers three buttons: "Create Paused", "Create and Activate", "Cancel". "Create Paused" calls `RouteBuilder.BuildRoute(...)`; "Create and Activate" calls `RouteBuilder.BuildRoute(...)` then `RouteOrchestrator.TryActivate(route, currentUT)`.
- Acceptance: creating a route shows a summary; the player can confirm the destination and start delivering in one click; the existing post-commit auto-dialog behavior is unchanged.
- Tests: a pure decision for the three-button outcome -> (build-only | build-and-activate | cancel); assert the activate path calls TryActivate.

### Phase 4: Medium polish

**M1. Inline cadence stepper plus direct interval entry**
- Dimension: UX. Effort: L.
- Problem: cadence is read-only in the Interval column ("1x (~14.0m)", `:294-297`) with the actual -/+ stepper hidden in the expanded `DrawCadenceStepper (:442-476)`; only a multiplier is selectable, never a target time.
- Change: render the -/+ stepper inline in the Interval cell (reuse `RouteCadence.StepMultiplier`/`ApplyMultiplier`). Add a small editable interval text field using the deferred-commit idiom (`SettingsWindowUI.cs:294-344`). `RouteCadence` has NO target-duration snap today, so add a new pure `ParseAndSnapInterval(text, span) -> N` (ceil to nearest multiple, floor 1) and feed N into `RouteCadence.ApplyMultiplier`.
- Acceptance: the player can change cadence from the row and type a target time that snaps to a valid multiple.
- Tests: unit-test `ParseAndSnapInterval` snapping, the 1x floor, and reject of garbage input.

**M2. In-window route rename**
- Dimension: ScenarioCoverage. Effort: M.
- Problem: routes have an editable `Route.Name` (`Route.cs:48-49`) but the window only displays it; auto names are similar across runs.
- Change: add a rename affordance in the detail panel using the deferred-commit TextField pattern from the Recordings table (`SetNextControlName("RecRename")` at `RecordingsTableUI.cs:1797`, click-outside commit `:801`, write-back `:3860`); write back to `Route.Name`, log on change. `Route.Name` is already persisted, so no codec work.
- Acceptance: the player can rename a route; the new name persists across save/load and shows in the row.
- Tests: covered by the shared rename-commit helper if extracted; otherwise assert the write-back path.

**M3. Show why a flown run is NOT a candidate**
- Dimension: ScenarioCoverage. Effort: L.
- Problem: when a run is ineligible the Candidates section is empty with one generic sentence (`:249-250`). The reasons split two ways: (a) the 5 `RouteAnalysisStatus` values `MissingRouteProof` / `MultipleConnectionWindows` / `NoDeliveryManifest` / `MixedPickupDelivery` / `MissingEndpointProof` (`RouteAnalysisEngine.cs:7-15`), and (b) the not-fully-sealed gate, which is NOT a `RouteAnalysisStatus`: it is `IsTreeFullySealed` in `RouteCandidateFinder.cs:52` (tracked as `notSealed`, `:92,108-110`). Both are logged only.
- Change: add a collapsible "Recently committed trees not yet eligible" subsection listing each near-miss tree with its blocking reason. For the 5 statuses reuse `RouteCreationFormatters.FormatRejectMessage(RouteAnalysisStatus)` (`RouteCreationFormatters.cs:75`; Eligible returns empty). For the not-fully-sealed gate there is no existing message helper, so add a hand-written string ("not fully sealed (N recording(s) still re-flyable)").
- Acceptance: an ineligible run shows an actionable reason.
- Tests: the status -> message mapping is pure; unit-test each of the 5 statuses plus the not-sealed string.

**M4. Disambiguate DestinationFull and add endpoint re-scan**
- Dimension: GameplayLogic. Effort: M.
- Problem: DestinationFull shows the same text whether the tank is genuinely full or the wrong target was picked; EndpointLost advises "re-target or recreate" but there is no re-target control, so the only path reproduces the baked endpoint.
- Change: for DestinationFull, append the resolved destination's current free capacity ("Munar Station tanks full: 0 of 150 LF free"). For EndpointLost where the surface fallback applies, add a "Re-scan for endpoint" button that re-runs `RouteEndpointResolver.TryResolveEndpoint` on demand; otherwise show a disabled-with-explanation note.
- Acceptance: full-vs-misrouted is distinguishable; a recoverable surface endpoint can be re-scanned without delete-and-recreate.
- Tests: pure capacity-context formatter; the re-scan eligibility decision unit-tested.

**M5. Toast when route creation disables a manual loop**
- Dimension: ScenarioCoverage. Effort: S.
- Problem: creating/activating a route silently clears the tree's manual Loop toggle (`RouteTreeGuard.ForceClearManualLoopForRoute`), log-only.
- Change: when `CreateRouteFromCandidate` triggers a manual-loop clear, post a `ParsekLog.ScreenMessage` toast ("Manual loop on '<tree>' turned off: a route now owns this tree") and add a one-line note in the route detail panel ("This route owns tree <name>; manual looping is disabled while it exists.").
- Acceptance: the player sees an in-window/toast notice when their manual loop is taken over.
- Tests: assert the toast/log fires on the clear path.

**M6. Fix the Pause-mid-cycle "Sending..." label collision**
- Dimension: UX. Effort: S.
- Problem: pausing a mid-cycle route arms `PauseAfterCurrentCycle` and shows the same disabled "Sending..." button as Send Once (`ShouldShowSendingButton :360-378`), so Pause reads as its opposite.
- Change: distinguish the armed states by label: "Pausing after this cycle..." when armed via Pause, "Sending one cycle..." when armed via Send Once (track which action armed it, or infer from prior status). Keep both disabled with explanatory tooltips.
- Acceptance: pausing shows a pause-flavored label, not "Sending...".
- Tests: pure `LabelForArmedState(armedBy, priorStatus)`; unit-test both arming paths. (`LogisticsWindowUISendingButtonTests.cs` already covers this area.)

### Phase 5: Low priority / style polish

**L1. Separate never-run-yet from intentionally-paused routes**
- Dimension: UX. Effort: S.
- Change: in the Paused section, label `CompletedCycles == 0` routes "New (not yet run)" in cyan `(0.65,0.85,1)` and show the "Send Once to test" guidance only on those rows; deliberately paused routes (cycles > 0) read "Paused" in grey.
- Tests: pure status-label classifier.

**L2. Sortable column headers and a narrower column set**
- Dimension: StyleConsistency. Effort: L.
- Change: route the Active/Paused tables through `parentUI.DrawSortableHeaderCore` with cached sorted results; compress columns (fold Interval+Transit context into the cadence cell) toward the ~750px house width (currently 1000px, the widest window in the mod).
- Tests: pure sort-key/comparer helpers.

**L3. Give Candidates its own header (stop reusing route columns)**
- Dimension: StyleConsistency. Effort: M.
- Change: a purpose-built Candidates header (Name / Origin / Destination / Would-deliver / Transit / action) via `parentUI.GetColumnHeaderStyle()`, dropping the Interval/Cyc/Status columns that show literal "-" / "eligible" today (`:399-401`).
- Tests: none beyond visual; presentation-only.

**L4. Centralize the status color palette**
- Dimension: StyleConsistency. Effort: S.
- Change: extract the palette into one shared helper on `ParsekUI` (next to `GetSectionHeaderStyle`/`GetColumnHeaderStyle`) and have both `RecordingsTableUI` and `LogisticsWindowUI` consume it, removing the duplicate copies. Confirm the canonical grey when centralizing (Logistics uses `0.7` at `:816`, not `0.6`).
- Tests: none; assert both call sites reference the shared source.

---

## 6. Suggested ordering and PR grouping

1. PR 1 = Phase 1 (QW1-QW8). Low risk, high visible payoff, no model changes. QW1 (kill 30s) and QW2 (delete confirm) first.
2. PR 2 = Phase 2 (H1-H5). The legibility core. H1 adds an orchestrator accessor; H2/H3/H5 read the ledger via `ComputeELS()`. Do H1/H2/H3 together so the row/detail layout is reworked once. Mind the ERS/ELS gate on H5 only (ledger reads are gate-safe).
3. PR 3 = Phase 3 (H6). Creation flow; depends on `BuildSummaryBlock` + `ComputeRootToUndockSpan` already existing.
4. PR 4 = Phase 4 (M1-M6). Independent polish; can be split further.
5. PR 5 = Phase 5 (L1-L4). Style polish; L2 is the largest and most optional.

Dependencies: H1/H2/H3 share the new row columns, so reserve the column layout in one pass. H3 reuses H2's ledger scan. L4 (shared palette) should land before or with L1 to avoid re-touching colors.

---

## 7. Testing and validation

- Unit-test every pure helper introduced (formatters, classifiers, parsers, countdown math). Follow the `*Presentation`/`*Formatters` split so IMGUI is not in the test path.
- Use the `ParsekLog.TestSinkForTesting` log-capture pattern for any new logged decision.
- In-game validation (Ctrl+Shift+T plus manual): create a route from the window (summary appears, real interval), Create and Activate (ghost flies, delivery fires), watch the "Next delivery" countdown reach zero on a real cycle, confirm realized-vs-planned shows a shortfall when the destination is near-full, rename a route, delete a route (confirm dialog), pause mid-cycle (label reads pause-flavored), and confirm the destination shows a vessel name.
- Build, deploy, and verify the deployed DLL before each playtest (grep a new UTF-16 label you added).

## 8. Out of scope (do not do here)

- No change to delivery timing, cadence math, or route lifecycle semantics beyond surfacing them. The one exception is H6 wiring Create to activate, which is UI orchestration of existing calls (`BuildRoute` then `TryActivate`), not new semantics. H1 adds a read-only next-crossing accessor in the Logistics layer; it must not change the loop clock.
- No multi-stop / round-trip routes (v1 is one-way).
- No new persisted route fields. H2's realized + cumulative numbers are computed from the ledger (ELS scan), so they need NO persistence and NO `RouteCodec` change. If some later item genuinely needs a new persisted field, add it through `RouteCodec`, cover OnSave/OnLoad, and call it out in the PR.
- No touching the locked Missions subsystem.

## 9. Pre-resolved facts (confirmed by the plan review against code)

- QW1/H6 span helper: `RouteCreationDialog.ComputeRootToUndockSpan` (`RouteCreationDialog.cs:184`). Use this exact helper from the window path so the two match. (Name says "undock" historically; confirm it returns the dock-based span per recent logistics work.)
- H1: `RouteLoopClock` has no countdown accessor; the LoopUnit it needs is built by the private, ERS-allowlisted `RouteOrchestrator.ResolveLoopUnit (:621)`. H1 must add a new `internal static` next-crossing accessor in the orchestrator/Logistics layer and throttle it (not per-frame).
- H2/H3: realized + cumulative come from scanning `EffectiveState.ComputeELS()` for `RouteCargoDelivered` rows (`RouteResourceManifest` = actual, `RouteRequestedResourceManifest` = requested-when-short, `RouteOrchestrator.cs:1226-1227`; read shape `:1284-1312`). ELS reads do not trip the grep gate. No persistence.
- H6: `RouteCreationDialog.Spawn`/`OnConfirm` are private and never activate; only `BuildSummaryBlock` and `ComputeRootToUndockSpan` are reusable. H6 is a new dialog flow, not a button swap.
- M3: the analysis enum value is `MissingRouteProof` (not "MissingProof"); the not-fully-sealed gate is separate (`RouteCandidateFinder.cs:52`) with no message helper.
- M1: `RouteCadence` has no target-duration snap; add a new `ParseAndSnapInterval` and feed `ApplyMultiplier`.
- H4: `RouteEndpointResolver.TryResolveEndpoint` returns a live `Vessel`; a SPACECENTER miss (unloaded target) is expected and falls back to coords.
- ERS/ELS allowlist: `LogisticsWindowUI.cs` is NOT currently allowlisted; only H5 (recording-id -> name) risks the gate, and only if it literally references `.CommittedRecordings`.
