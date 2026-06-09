# Plan: Mission / Route structure-list window

## Problem

The Missions tab renders a vessel-composition-over-time tree (`MissionCompositionBuilder`):
each row is a *physical vessel during a structural interval*, decomposing into the
offshoots that left it. It answers "what was this stack made of, and how did it
split apart". It does NOT give a flat, chronological "what happened, step by step"
read of a run, and there is no equivalent surface at all for a Supply Route.

The roadmap's Tier-1 logistics item (`docs/roadmap.md`, "Logistics: remaining work
toward feature-complete", first bullet) asks for:

> a popup (one per mission, one per route) that lists the run's structure as an
> ordered list of its segments and intermediary points, each with its time and
> location. For a mission: launch, staging / separation events, dock, undock, and
> the terminal / landing. For a route: origin, the connection / dock point, the
> delivery point, undock, and any stops.

Design doc cross-reference: `docs/parsek-logistics-supply-routes-design.md` §17
("Map view integration" is a *separate, still-deferred* item; this list window is
the readable alternative the roadmap chose over map route lines) and §17.1 Tier 1,
which classifies this as "mostly feature-specific UI over existing data ... adds no
new capture or scheduler systems".

## Scope

Ship the full Tier-1 item in one PR: one reusable popup window driven by an ordered
step read-model, with two pure builders (mission + route) and two entry points
(Missions tab, Logistics window). Mission and route both land together.

Out of scope (explicit): map route lines (§17 "Map view integration", still
deferred); any change to recording capture, the scheduler, or the route data model;
clickable steps that warp/seek (a possible later enhancement, not this PR).

## All data already exists (no new capture)

### Mission steps
`MissionStructureBuilder.Build(RecordingTree)` already produces the controlled-leg
fork tree, and the Missions window caches it per tree per frame
(`MissionsWindowUI.GetCompositionRoots` → `compositionCache`). Each `MissionLeg`
carries everything the step list needs for the controlled spine:

- `StartUT` / `EndUT`
- `OriginBranchPointType` + `OriginCause`, `EndBranchPointType` + `EndCause`
  (Launch / Decoupled / Undocked / Docked / Boarded / EVA / Broke up / Broke off)
- `TerminalStateValue` (Landed / Splashed / Orbiting / Recovered / Destroyed / …)
- `VesselName`, `EvaCrewName`

Labels reuse the existing pure helpers `MissionCompositionBuilder.BranchEventName`
and `TerminalName` (already `internal static`).

Staging / separation events that drop **debris** are excluded from `MissionLeg`
(debris is not a leg). Those come from the recordings' `PartEvents`:
`Decoupled`, `FairingJettisoned`, `ShroudJettisoned`, plus `Docked` / `Undocked`
as a cross-check against the branch-point spine. `Recording.PartEvents` is a
`List<PartEvent>` of `{ double ut; PartEventType type; … }`.

### Route steps
- `Route.Origin` (`RouteEndpoint`: `BodyName`, `Latitude/Longitude/Altitude`,
  `IsSurface`) + `Route.IsKscOrigin`.
- `Route.DockMemberRecordingId` is the recording that carries the delivery binding;
  its `Recording.RouteConnectionWindows` (`List<RouteConnectionWindow>`) carry
  `DockUT`, `UndockUT`, `EndpointAtDock`, `TransferTargetVesselPid`, and the scoped
  resource/inventory manifests.
- `Route.RecordedDockUT` is the delivery phase within the loop.
- `Route.Stops` (`List<RouteStop>`): each `RouteStop` carries `Endpoint`,
  `DeliveryOffsetSeconds`, `DeliveryManifest`, `InventoryDeliveryManifest`.
- `Route.SourceRefs[].RecordingId` / `.TreeId` resolve the backing recordings.

### Location
Per-recording: `StartBodyName`, `StartBiome`, `StartSituation`, `LaunchSiteName`,
`EndBiome`. Per-`TrajectoryPoint`: body + lat/lon/alt to resolve a coordinate at an
arbitrary UT when a finer location than the recording's start/end is wanted.
Existing site/location formatting lives in `RecordingsTableFormatters.cs` (the
Recordings tab "Site" column) and is the formatter to reuse / extend so the window's
location text matches the rest of the UI.

## Architecture (mirror the existing pure-builder + IMGUI-renderer split)

The codebase consistently separates a pure, Unity-free, unit-tested read-model
builder (`MissionStructureBuilder`, `MissionCompositionBuilder`) from the IMGUI
renderer (`MissionsWindowUI`). This follows the same split.

### 1. Read model + builders (pure, `internal static`, headless-testable)

New file `Source/Parsek/MissionRouteStructureList.cs`:

```csharp
internal enum StructureStepKind
{
    Launch, Staging, Separation, Dock, Undock, Eva,
    Origin, Delivery, Stop, Terminal
}

internal struct StructureStep
{
    public double UT;
    public StructureStepKind Kind;
    public string Label;      // "Launch", "Decoupled booster", "Dock with Station Alpha", "Landed"
    public string Location;   // "Kerbin – Launch Pad", "Mun – Midlands (Landed)", "Kerbin orbit"
    public string VesselName; // the controlled vessel / piece this step concerns (may be empty)
}

internal static class MissionStructureListBuilder
{
    internal static bool SuppressLogging;
    internal static List<StructureStep> Build(RecordingTree tree, MissionStructure structure);
}

internal static class RouteStructureListBuilder
{
    internal static bool SuppressLogging;
    // sourceLookup resolves a RecordingId -> Recording (committed store), injected
    // for headless testability (no RecordingStore singleton in the pure builder).
    internal static List<StructureStep> Build(
        Logistics.Route route, System.Func<string, Recording> sourceLookup);
}
```

- `MissionStructureListBuilder.Build` takes the already-built `MissionStructure`
  (so the window passes its cached structure; no rebuild) and the `tree` (for
  `PartEvents` on member recordings). It emits, ordered by UT:
  - one `Launch` step from each root leg's origin,
  - `Separation` / `Dock` / `Undock` / `Eva` steps from branch points (reusing
    `BranchEventName`),
  - `Staging` steps from member `PartEvents`:
    - `Decoupled` events are de-duplicated against Separation branch points on the
      **part-PID key**, NOT UT: drop a `Decoupled` `PartEvent` iff a decouple
      `BranchPoint` shares its `partPersistentId` (`BranchPoint.DecouplerPartId ==
      PartEvent.partPersistentId`, with UT used only as a tolerance sanity check).
      The two recorder paths stamp slightly different UTs, so UT-equality dedup
      would miss and double-count. A *debris* decouple produces a `Decoupled`
      PartEvent but no controlled-leg branch point (debris is not a leg), so the
      PartEvent and branch-point sets are deliberately not 1:1 — this is exactly the
      "staging that drops debris" we want to surface.
    - `FairingJettisoned` / `ShroudJettisoned` have no branch-point counterpart and
      pass through unconditionally (no dedup).
  - one `Terminal` step per controlled leg that ends in a terminal state
    (`TerminalName`).
  - Stable sort by `(UT, Kind, VesselName, partPersistentId)` for deterministic
    output (matches the `SortLegIds` `(StartUT, RecordingId)` convention). The PID
    tiebreak matters because debris-only staging rows have an empty `VesselName`
    (a `PartEvent` carries `partName`/`partPersistentId`, not a vessel name).
  - Note: the `PartEvent` struct field is `eventType` (not `type`); use it.
- `RouteStructureListBuilder.Build` emits: `Origin` (from `Route.Origin` +
  `IsKscOrigin`), `Dock` (`RouteConnectionWindow.DockUT` +
  `EndpointAtDock`/`TransferTargetVesselPid`), `Delivery` (`RecordedDockUT` + each
  stop's `DeliveryOffsetSeconds`, labeled with the delivered manifest summary),
  `Undock` (`UndockUT`), and a `Stop` per `Route.Stops` entry. Reads the connection
  window from `DockMemberRecordingId` via the injected `sourceLookup`. Note
  `Route.Origin` is a `struct` (`RouteEndpoint`), so it is never null — treat an
  empty `BodyName` as the "unresolved origin" signal, not a null check.
- A shared pure helper `StructureLocationFormatter.Describe(...)` (in the same file)
  produces the `Location` string. For a mission step it reuses
  `RecordingsTableFormatters`: the public `FormatStartPosition(Recording, …)` /
  `FormatEndPosition(Recording, …)` take a whole `Recording`; the loose
  situation+biome+body helper `FormatSituationLocation` is currently **`private`**
  and must be promoted to `internal static` to reuse it for an arbitrary
  body/situation/biome (a one-line visibility change in
  `RecordingsTableFormatters.cs`). For a `RouteEndpoint` (body + lat/lon/alt +
  `IsSurface`, no backing `Recording`) there is **no existing formatter** — that
  path is new code in `StructureLocationFormatter`. All numeric output (coords,
  altitude, manifest amounts) uses `CultureInfo.InvariantCulture` per the hard rule.
- Each `Build` emits ONE `Verbose` summary line (`[Mission]` / `[Route]` subsystem,
  step counts by kind) gated by `SuppressLogging`, matching
  `MissionStructureBuilder`'s logging convention. These builders are called only on
  window open (not per frame), so logging is one-shot `Verbose`, not rate-limited.

### 2. Renderer: `Source/Parsek/UI/StructureListWindowUI.cs` (one reusable sub-window)

- Owned by `ParsekUI` like the `SpawnControlUI` / `CareerStateWindowUI` sub-windows
  (NOT `GroupPickerUI` — that one is owned by `RecordingsTableUI`, so it is the wrong
  model). The exact pattern, verified against the code:
  - The **sub-window class owns its own state**: its `Rect`, its `IsOpen` flag
    (`public bool IsOpen { get; set; }`, cf. `SpawnControlUI.cs:50`), and its
    scroll/sort fields. `ParsekUI` holds only the **instance** (e.g.
    `private StructureListWindowUI structureListUI;`) and calls
    `structureListUI.DrawIfOpen(...)` from its OnGUI pass (cf.
    `ParsekUI.cs:1074` `spawnControlUI.DrawIfOpen(...)`, `:398`
    `careerStateUI.DrawIfOpen(mainWindowRect)`).
  - The window id is an **inline `"literal".GetHashCode()`** (e.g.
    `"ParsekStructureList".GetHashCode()`, cf. `SpawnControlUI.cs:136`), NOT an
    allocated `int` field.
  - Copy the existing window scaffolding from `SpawnControlUI` verbatim:
    `ClickThruBlocker.GUILayoutWindow` + `parentUI.GetOpaqueWindowStyle()` +
    `ResetWindowGuiColors` / `RestoreWindowGuiColors` + the input-lock management
    (`SpawnControlUI.cs:129-162`). **No new styling**: reuse those helpers and the
    column/label conventions already used by `MissionsWindowUI` (the
    `compositionCellLabel`, header bubble styles, `KSPUtil.PrintDateCompact`).
- Holds a small open target: `{ Mode (Mission|Route); string treeId / routeId;
  string title }`. On open it builds the step list once and caches it (rebuild only
  on explicit reopen), then renders a scrollable table:
  **Time | Event | Location | Vessel**. Time via
  `KSPUtil.PrintDateCompact(step.UT, true)` to match the Missions tab.
- Empty / unresolved cases render a single explanatory row (e.g. route whose source
  recording is missing), never a blank window.

### 3. Entry points (two buttons, existing button styling)

- Missions tab: add a `"Structure"` button in `MissionsWindowUI.DrawMissionHeader`
  (the Clone / Delete / Warp-to… group, `MissionsWindowUI.cs:770`), sharing
  `ColW_HeaderButton` so it reads as one group. **Layout caveat:** that group sits
  in a fixed-width right block `MissionHeaderRightBlockWidth`
  (`MissionsWindowUI.cs:100`, used at `:765`) that is already ~6px from overflow
  (its comment warns of this). Adding a 70px button requires bumping
  `MissionHeaderRightBlockWidth` by `ColW_HeaderButton + 4f`, or the looped-period
  label wraps. It calls a `ParsekUI` coordinator method to open the window in
  Mission mode for that tree.
- Logistics window: add the `"Structure"` button in
  `LogisticsWindowUI.DrawRouteDetail` (the expand panel, `LogisticsWindowUI.cs:1161`),
  **not** in the per-row action cell. The row action cell is a fixed-width
  `GUILayout.Width(ColW_Actions)` with `ColW_Actions = 190f` (`:246`) that is already
  densely packed (Send Once 79 + Activate 64 + delete 22 ≈ 165px) and would overflow.
  `DrawRouteDetail` is a free-form `GUILayout.BeginVertical(GUI.skin.box)` with no
  width constraint, so the button drops in cleanly next to the existing detail
  controls. Opens the window in Route mode for that route.
- Both entry points go through one `ParsekUI` method pair
  (`OpenStructureWindowForMission(treeId)` / `OpenStructureWindowForRoute(routeId)`)
  so the window stays single-owner.

## Files touched

New:
- `Source/Parsek/MissionRouteStructureList.cs` (read model + both builders +
  location formatter)
- `Source/Parsek/UI/StructureListWindowUI.cs` (renderer)
- `Source/Parsek.Tests/StructureListBuilderTests.cs` (xUnit)

Edited:
- `Source/Parsek/UI/MissionsWindowUI.cs` — one "Structure" button + open call;
  bump `MissionHeaderRightBlockWidth` by `ColW_HeaderButton + 4f`
- `Source/Parsek/UI/LogisticsWindowUI.cs` — one "Structure" button in
  `DrawRouteDetail` + open call
- `Source/Parsek/ParsekUI.cs` — own + draw the sub-window, two open methods
- `Source/Parsek/UI/RecordingsTableFormatters.cs` — promote `FormatSituationLocation`
  from `private` to `internal static` for reuse by the location formatter
- `Source/Parsek/Properties/AssemblyInfo.cs` + `Parsek.version` — bump to 0.11.0
  (lockstep; the release script validates they match)
- `CHANGELOG.md` (new `## 0.11.0` section), `docs/roadmap.md` (mark Tier-1 item),
  `docs/dev/todo-and-known-bugs.md`
- `docs/parsek-logistics-supply-routes-design.md` — note the §17.1 Tier-1 item shipped

## Data-model / scope decisions (validate with plan reviewer)

1. **Target version.** Current `main` is `0.10.1` (a maintenance/bug-fix release per
   its CHANGELOG header). This is a new feature, so it should open a new `## 0.11.0`
   CHANGELOG section + bump `AssemblyInfo.cs` / `Parsek.version`. Confirm 0.11.0 is
   the intended line (vs folding into a different planned release).
2. **Staging granularity.** Mission steps include debris-producing staging via
   `PartEvents` (`Decoupled` / fairing / shroud). Confirm we want every separation
   event, or only "significant" ones (e.g. suppress fairing/shroud, keep
   decouple/dock/undock). Default proposed: include decouple + fairing + shroud +
   dock + undock + EVA + terminal; exclude pure cosmetic part events (lights, gear,
   RCS, deployables).
3. **Location granularity.** Default proposed: body + situation/biome from the
   recording's start/end context, plus launch-site name for the launch step.
   Resolving a per-step coordinate from `TrajectoryPoint`s at an arbitrary UT is
   possible but heavier; propose deferring per-step coordinate lookup unless the
   reviewer wants it.
4. **Window vs PopupDialog.** Proposed: a non-modal `GUILayout.Window` sub-window
   (scrollable, readable alongside the table), matching `SpawnControlUI` /
   `GroupPickerUI`, NOT a `PopupDialog` (which suits confirmations). Confirm.
5. **One window, single target.** Proposed: one shared window instance showing one
   target at a time (reopening retargets it), not N simultaneous popups. Confirm
   that matches the "a popup, one per mission, one per route" intent.

## Tests

### Builder unit tests (xUnit, pure, headless) — `StructureListBuilderTests.cs`
- Mission: single-leg launch→landed run → exactly `Launch` + `Terminal`, ordered,
  correct labels/locations.
- Mission: decouple fork (booster debris) → `Launch`, `Separation` at the decouple
  UT, `Terminal`; debris separation present even though debris is not a leg.
- Mission: dock + undock run → `Dock` then `Undock` in UT order, correct partner
  label.
- Mission: EVA → `Eva` step with the kerbal name.
- Determinism: two builds of the same tree produce byte-identical step lists.
- Route: KSC-origin route with one stop → `Origin (KSC)`, `Dock`, `Delivery`,
  `Undock`, ordered; delivery label summarizes the manifest.
- Route: vessel-origin route → `Origin` shows the depot, not KSC.
- Route: missing source recording (`sourceLookup` returns null) → empty list +
  logged reason, no throw.
- Location formatter: launch-site, surface biome, and orbit cases.

### Log-assertion tests
- Assert each builder emits its `[Mission]` / `[Route]` `Verbose` summary line with
  the expected step counts (canonical `ParsekLog.TestSinkForTesting` pattern,
  `[Collection("Sequential")]`). Mirror the full reset pattern from
  `MissionStructureTests` ctor/Dispose: set `ParsekLog.SuppressLogging = false` +
  `MissionStructureListBuilder.SuppressLogging = false` in the ctor and restore in
  `Dispose`, not just `TestSinkForTesting`.

### Test fixture notes (verified feasible headlessly)
- `RouteFixtureBuilder` already exists (`Generators/RouteFixtureBuilder.cs`:
  `WithOrigin` / `WithKscOrigin` / `WithStop` / `WithDockBinding(recordedDockUT,
  dockMemberRecId)` / `WithSourceRef`), so a `Route` with origin/stops/dock-binding
  is buildable without Unity. A connection-window-bearing `Recording` is built inline
  today (`new Recording { RouteConnectionWindows = new List<RouteConnectionWindow>{…} }`,
  cf. `RouteAnalysisEngineTests`). The injected `Func<string,Recording> sourceLookup`
  is satisfied in tests by `id => dict[id]`.
- In inline-`Recording` mission fixtures, `StartUT`/`EndUT` are **computed
  properties**; set `ExplicitStartUT` / `ExplicitEndUT` (as `MissionStructureTests.Leg`
  does), not `StartUT`/`EndUT` directly.
- Staging-dedup tests use `RecordingBuilder.AddPartEvent(ut, pid, type, …)` (or a
  direct `PartEvents.Add(new PartEvent{…})`) and assert that a `Decoupled` event
  sharing a Separation branch point's `DecouplerPartId` is dropped while a
  debris-only `Decoupled` (no matching branch point) and fairing/shroud pass through.

### In-game test (`InGameTests/RuntimeTests.cs`)
- One `[InGameTest(Category = "UI")]` that opens the window for an injected synthetic
  recording's tree and asserts the built step list is non-empty and ordered. (No
  PartLoader/roster dependency expected; promote to Isolated batch if it touches
  shared static state.)

## Diagnostic logging
- Builders: one-shot `Verbose` summary per build (counts by kind), `SuppressLogging`
  guard present for safety even though the call is open-time, not per-frame.
- Window: `Info` on open (`mode`, target id, step count) and on close, per the
  "every state transition is logged" requirement.

## Rollout / risk
- Low risk: additive read-only UI over existing committed data; no capture,
  scheduler, serialization, or route-model change; nothing runs per frame except the
  window draw while it is open.
- The only shared-state touch is `ParsekUI` owning one more sub-window; follows the
  existing sub-window ownership pattern exactly.

## CHANGELOG entry (wording, user-facing, 1–2 sentences)
> Added a Structure window (opened from a mission's "Structure" button on the
> Missions tab and a route's "Structure" button in the Logistics window) that lists
> a run step by step — launch, staging, dock/undock, deliveries, and the
> landing/terminal — each with its time and location.
