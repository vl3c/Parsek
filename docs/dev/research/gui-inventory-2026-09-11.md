Research note, committed verbatim: the read-only GUI surface census taken 2026-09-11 against commit 4eb427e9e.
Condensed in docs/dev/design-gui-inventory.md; claims below are unedited, and every file:line is as measured.

# Parsek GUI inventory - every player-facing surface, derived from the code

Source of truth: `C:/Users/vlad3/Documents/Code/Parsek/Parsek-gui-census-dump` at
`9e145cca4` (a merge of `origin/main`), read-only. All file paths in this document are
relative to `Source/Parsek/` unless they start with `harness/` or `docs/`.

Rendered evidence: two operator-tier census runs on one career save state.

| run | folder | scene | captures |
|---|---|---|---|
| GUI-1-census-ksc | `harness/results/2026-09-11_0548_GUI-1-census-ksc_shots/` | SPACECENTER | 23 labels |
| GUI-2-census-flight | `harness/results/2026-09-11_0551_GUI-2-census-flight_shots/` | FLIGHT | 4 labels |

Each label is a `<label>.png` plus a `<label>.gui.json` control tree (schema
`parsek-gui-tree/1`: `roots[]` of nodes carrying `kind`, `text`, `tooltip`, `rect`,
`children`). Screen is 1280x720. The host is the operator-local fixture
`fixtures/local-saves/c1-gui` (`harness/scenarios/GUI-1-census-ksc.toml:34-44`), chosen
for DENSITY: a 42 MB long-lived career with 704 recording sidecars and 12 vessels,
because "a window with no rows photographs as an empty box".

## How this inventory was derived

Every program-wide claim below rests on a grep, not on memory. The six that matter:

| grep over `Source/Parsek` | result | consequence |
|---|---|---|
| `ClickThruBlocker.GUILayoutWindow(` / `GUILayout.Window(` / `GUI.Window(` | 16 call sites: 14 `ClickThruBlocker`, 1 raw `GUILayout.Window` at `InGameTests/TestRunnerShortcut.cs:204`, 1 test-only probe at `InGameTests/GuiTreeDumpImguiTest.cs:484` | 15 player-facing window hosts drawing **14 distinct windows** (the main window has two hosts, `ParsekFlight.cs:2118` and `ParsekKSC.cs:245`) |
| `PopupDialog.SpawnPopupDialog(` (excluding `InGameTests/`, `TestCommands/`) | 21 call sites | there are exactly 21 modal dialogs |
| `void OnGUI()` (production) | 6 | `CurrencyReservationOverlay.cs:87`, `OverlayBadge.cs:122`, `ParsekFlight.cs:2087`, `ParsekKSC.cs:227`, `ParsekTrackingStation.cs:350`, `InGameTests/TestRunnerShortcut.cs:177` |
| IMGUI draw calls in files outside `UI/` | `CurrencyReservationOverlay.cs`, `MapMarkerRenderer.cs`, `OverlayBadge.cs`, `ParsekFlight.cs`, `ParsekKSC.cs`, `ParsekUI.cs`, `WatchModeController.cs` (+ the `GuiTree*` recorder, not player-facing) | the overlay/marker layer is exactly these seven files |
| `UiSurfaceVisibility.IsVisible(` | 12 call sites in 5 files | the complexity gate has 12 enforcement points, all launcher/content draw sites; 4 of the 14 enum keys have NO call site (appendix 1) |
| `ParsekLog.ScreenMessage` / `ScreenMessages.PostScreenMessage` | 107 raw, 95 real | appendix 2 |

## The three capture layers, and what each one can and cannot see

The `DumpGuiTree` verb records `GuiTreeRecorder`'s view of ONE Repaint pass. It works by
patching `GUI.DoWindow`, described at `Patches/GuiTreeRecorderPatches.cs:224-227` as "the
single funnel for all six `GUI.Window` overloads AND `GUILayout.Window` (which wraps its
callback and calls `GUI.Window`), which is also where `ClickThruBlocker.GUILayoutWindow`
[lands]"; the reflection binding is `GuiTreeFunnels.cs:112-118`.

That single mechanism sets the census's blind spots, and they are structural rather than
accidental:

| layer | example | in the gui.json? |
|---|---|---|
| IMGUI inside a `GUI.Window` callback | every window and tab in sections 1-8 | YES |
| IMGUI outside any window | watch overlay, ghost map markers, the currency tooltip | NO |
| uGUI (`PopupDialog`, `RawImage` badges, the stock toolbar button) | all 21 dialogs, the R&D / Astronaut / Mission Control badges, the ApplicationLauncher button | NO |
| hover-dependent IMGUI inside a window | the `TooltipEchoBox` strip's TEXT, `DisabledHoverEcho` reasons | the strip node is captured, always EMPTY |

So the 27 captures cover windows and window contents. They cover none of the dialogs,
none of the overlays, none of the markers, none of the badges, and no tooltip in its
populated state.

## The seam vocabulary the census had

From `harness/scenarios/GUI-1-census-ksc.toml:158-310` and its flight sibling, the verbs
that exist today:

| verb | args | what it can do |
|---|---|---|
| `UiAction` | `op=complexity mode=basic\|advanced` | flip the complexity mode |
| `UiAction` | `op=open\|close window=<token>` | toggle a window's `IsOpen` flag |
| `UiAction` | `op=rect window=<token> x y w h` | move/size a window (size is a FLOOR, `TestCommands/TestCommandUiAction.cs:330-356`) |
| `UiAction` | `op=tab window=<token> tab=<token>` | set a tab field |
| `UiAction` | `op=describe` | dump the window table |
| `CaptureScreenshot` | `label=` | write `Screenshots/<label>.png` |
| `DumpGuiTree` | `label=` | write `Screenshots/<label>.gui.json` |

What does NOT exist, and is the reason most of appendix 4 is blocked: there is no verb
that CLICKS a control, EXPANDS a row, SELECTS a row, or MOVES THE POINTER. The window
table at `TestCommands/TestCommandUiAction.cs:382-430` names its own three deliberate
exclusions for exactly that reason: `GroupPickerUI` and the Logistics link picker are
popups over a SELECTION that nothing can arm, and `TestRunnerShortcut` is a separate
MonoBehaviour with no accessor.

## Master window index

The 14 distinct IMGUI windows, in the main window's own button order (the order
`TestCommands/TestCommandUiAction.cs:361-364` pins for the same reason).

| # | window title | class + host line | scenes | seam token | census labels | section |
|---|---|---|---|---|---|---|
| 1 | `Parsek` (main) | `ParsekFlight.cs:2118` (FLIGHT) / `ParsekKSC.cs:245` (SPACECENTER) | FLIGHT, SPACECENTER | `main` | `ksc-main-basic`, `ksc-main-advanced`, `flight-main-basic`, `flight-main-advanced` | 1 |
| 2 | `Parsek - Missions` | `UI/RecordingsTableUI.cs:651` | FLIGHT, SPACECENTER | `missions` (tabs `missions`, `recordings`) | `ksc-missions-missions-advanced`, `ksc-missions-recordings-advanced`, `ksc-missions-basic`, `flight-missions-missions-advanced` | 2, 3 |
| 3 | `Parsek - Timeline` | `UI/TimelineWindowUI.cs:281` | FLIGHT, SPACECENTER | `timeline` (tabs `overview`, `details`, `rewindff`, `refly`) | `ksc-timeline-overview/details/rewindff/refly-advanced`, `ksc-timeline-basic` | 4 |
| 4 | `Parsek - Kerbals` | `UI/KerbalsWindowUI.cs:205` | FLIGHT, SPACECENTER | `kerbals` (tabs `roster`, `outcomes`) | `ksc-kerbals-roster-advanced`, `ksc-kerbals-outcomes-advanced` | 5 |
| 5 | `Parsek - Career State` | `UI/CareerStateWindowUI.cs:1211` | FLIGHT, SPACECENTER | `career` (tabs `contracts`, `strategies`, `facilities`, `milestones`) | the four `ksc-career-*-advanced` | 5 |
| 6 | `Parsek - Logistics` | `UI/LogisticsWindowUI.cs:444` | FLIGHT, SPACECENTER | `logistics` (no tabs) | `ksc-logistics-advanced`, `ksc-logistics-basic` | 6 |
| 7 | Logistics round-trip link picker | `UI/LogisticsWindowUI.cs:1762` | as its host | (deliberately excluded, `TestCommandUiAction.cs:374-377`) | **NONE** | 6 |
| 8 | `Parsek - Structure` | `UI/StructureListWindowUI.cs:174` | FLIGHT, SPACECENTER | `structure` | `ksc-structure-advanced` (EMPTY chrome only) | 3 |
| 9 | `Parsek - Settings` | `UI/SettingsWindowUI.cs:128` | FLIGHT, SPACECENTER | `settings` (no tabs) | `ksc-settings-advanced`, `ksc-settings-basic` | 7 |
| 10 | `Real Spawn Control` | `UI/SpawnControlUI.cs:162` | FLIGHT only | `spawncontrol` | **NONE** | 7 |
| 11 | `Gloops Flight Recorder` | `UI/GloopsRecorderUI.cs:94` | FLIGHT only | `gloops` | `flight-gloops-advanced` | 7 |
| 12 | `Parsek - Test Runner` (Settings-launched) | `UI/TestRunnerUI.cs:122` | FLIGHT, SPACECENTER | `testrunner` | `ksc-testrunner-advanced` (4217 nodes) | 7 |
| 13 | `Parsek - Test Runner` (global Ctrl+Shift+T) | `InGameTests/TestRunnerShortcut.cs:204` | ANY scene | (deliberately excluded, `TestCommandUiAction.cs:378-380`) | **NONE** | 7 |
| 14 | `Set Parent Group` / `Manage Groups` | `UI/GroupPickerUI.cs:224` | as its host | (deliberately excluded, `TestCommandUiAction.cs:369-373`) | **NONE** | 2 |

Two asymmetries in that table are mechanical facts rather than presentation choices:

- Window 13 is the ONLY one that calls raw `GUILayout.Window` instead of
  `ClickThruBlocker.GUILayoutWindow` (`InGameTests/TestRunnerShortcut.cs:204` vs the
  other 14 hosts), so it is the only Parsek window with no click-through protection.
  It compensates with its own `windowRect.Contains(Event.current.mousePosition)` input
  lock at `InGameTests/TestRunnerShortcut.cs:213`.
- The TRACKING STATION hosts no Parsek window at all. `ParsekTrackingStation.cs:350` has
  an `OnGUI` but it only calls `DrawAtmosphericMarkers()`, and the source says so in place
  at `ParsekTrackingStation.cs:394-395`. The window-host grep confirms it: the only two
  main-window hosts are FLIGHT and SPACECENTER.

Kinds used in the per-surface headings below: window, tab, section, dialog, overlay,
marker, badge, screen message, tooltip-only. Counts and the full picture tally are in the
report at the end of this document.

## ASCII substitution legend

This document is plain ASCII. Several product strings and rendered glyphs are not. Where
one appears in a quoted literal it is substituted as below; grep the source for the real
character.

| written here | actual character in source / on screen |
|---|---|
| `>` (as a collapse caret) | U+25B6 black right-pointing triangle |
| `v` (as an expand caret) | U+25BC black down-pointing triangle |
| `^` | U+25B2 black up-pointing triangle |
| `<` (as a caret) | U+25C0 black left-pointing triangle |
| `//` | U+25E2 black lower-right triangle (the window resize grip) |
| `-` (in a tree connector) | U+2500 box drawings light horizontal |
| `\` (in a tree connector) | U+2514 box drawings light up and right |
| `\|` (in a tree connector) | U+251C box drawings light vertical and right |
| `->` | U+2192 rightwards arrow |
| ` - ` inside a quoted message | U+2014 em dash (the product string genuinely contains one) |
| `...` | U+2026 horizontal ellipsis |

## Citation audit

Every `file:line` in this document was machine-checked: all **1361** references resolve to
a file that exists in the checkout, at a line number inside that file. One set (the
`InputFocusGuard` paragraph in section 1) was out of range on the first pass and has been
corrected against the file. A reference written as a bare basename (`TestCommandUiAction.cs`
rather than `TestCommands/TestCommandUiAction.cs`) resolves by basename; every such file
exists exactly once in the tree.

This check proves the line EXISTS, not that it says what the sentence claims. Spot checks
were done by hand on the load-bearing claims (the window-host grep, the dialog grep, the
`UiSurface` call-site grep, the `DrawRecordingTooltip` and `basicUiMode` dead-code findings,
the ungated chapter-header toggle, `MinWindowWidth`, and the `SpawnWarningUI` dead methods).

## Reading order

Sections 1 to 9 are independent; each covers one window or one family and repeats the
(a) to (g) structure per surface. Appendix 1 is the complexity gate, appendix 2 the
95 screen-message producers, appendix 3 the tooltip-only information, appendix 4 the
unphotographed surfaces with the cheapest route to a picture, appendix 5 a cross-check of
appendix 1 against each section's own citations. The closing report has the counts.

---

# Section 01 - The main "Parsek" window, its hosts, and the map markers

Scope: `ParsekUI.cs` (main window, tooltip host, the two Wipe PopupDialogs, `DrawMapMarkers`),
the three scene hosts (`ParsekFlight.OnGUI`, `ParsekKSC.OnGUI`, `ParsekTrackingStation.OnGUI`),
`ParsekToolbarRegistration.cs`, `PauseMenuGate.cs`, `InputFocusGuard.cs`.
All paths are relative to `Source/Parsek`. Evidence: the two census runs named in the brief.

Surface index (detail below):

| # | Surface | Kind | Census label |
|---|---------|------|--------------|
| 1 | Parsek main window | window | ksc-main-basic, ksc-main-advanced, flight-main-basic, flight-main-advanced |
| 2 | Flight status block | section (in 1) | flight-main-basic, flight-main-advanced (Idle variant only) |
| 3 | Launcher button column | section (in 1) | all four main labels |
| 4 | RouteRunPrompt supply-route banner | overlay/banner (in 1) | NONE |
| 5 | Tooltip echo strip | tooltip-only | all four main labels (empty, unhovered) |
| 6 | Version + Close footer | section (in 1) | all four main labels |
| 7 | Confirm: Wipe Recordings | dialog | NONE |
| 8 | Confirm: Wipe Game Actions | dialog | NONE |
| 9 | Flight/map ghost markers (`DrawMapMarkers`) | marker | NONE |
| 10 | Flight-scene host | host | (hosts 1) |
| 11 | KSC-scene host | host | (hosts 1) |
| 12 | Tracking-Station host | host | NONE |
| 13 | Stock ApplicationLauncher button | toolbar button | NONE (stock chrome, not in the GUI tree) |

---

## 1. Parsek main window

**(a) Name and kind.** Title bar text `"Parsek"`; a draggable IMGUI window. It is the mod's
single entry point; every other Parsek window is launched from its button column.

**(b) Class + draw method.** `ParsekUI.DrawWindow(int windowID)`, `ParsekUI.cs:743`.
Instantiated per scene by `ParsekUI(ParsekFlight)` (`ParsekUI.cs:153`) or `ParsekUI(UIMode)`
(`ParsekUI.cs:173`). Drag handled by `GUI.DragWindow()` at `ParsekUI.cs:992`.

**(c) Scenes + gate.**

| Scene | Host | Gate chain |
|-------|------|-----------|
| FLIGHT + MAPVIEW | `ParsekFlight.OnGUI` `ParsekFlight.cs:2087` | `!PauseMenuGate.IsPauseMenuOpen()` (`:2096`) AND `showUI` (`:2108`) AND `ui.GetOpaqueWindowStyle() != null` (`:2111`) |
| SPACECENTER | `ParsekKSC.OnGUI` `ParsekKSC.cs:227` | `showUI` (`:229`) AND `!PauseMenuGate.IsPauseMenuOpen()` (`:233`) AND opaque style non-null (`:238`) |
| TRACKSTATION | none | `ParsekTrackingStation.OnGUI` `:350` draws markers only - see surface 12 |

`showUI` is written only by the toolbar button handlers (`ParsekFlight.cs:1352`/`:1353`,
`ParsekKSC.cs:155`/`:156`) and the Close handler (`ParsekFlight.cs:1362-1364`,
`ParsekKSC.cs:165-167`). No `UiSurface` key gates the window itself.

**(d) STRUCTURE top-down.** No tabs, no filters, no table. Single vertical layout group
(`GUILayout.BeginVertical()` `ParsekUI.cs:745`):

| Order | Element | Line | Present in |
|-------|---------|------|-----------|
| 1 | Flight status block (surface 2) | `:747-748` | FLIGHT only (`InFlight`) |
| 2 | `GUILayout.Space(10f)` | `:750` | always |
| 3 | Real Spawn Control launcher + trailing Space | `:770-792` | FLIGHT + Advanced |
| 4 | Timeline launcher | `:794` | always |
| 5 | Missions launcher | `:806` | always |
| 6 | RouteRunPrompt banner (surface 4) | `:817-855` | when a candidate is pending |
| 7 | Logistics launcher | `:885` | always |
| 8 | Leading `Space` + Kerbals + Career | `:904-932` | Advanced only |
| 9 | `GUILayout.Space(10f)` | `:934` | always |
| 10 | Gloops Flight Recorder launcher | `:941-951` | NEVER (retired, see (e)) |
| 11 | Settings launcher | `:955` | always |
| 12 | `GUILayout.Space(10f)` | `:961` | always |
| 13 | Tooltip echo strip (surface 5) | `:977` | always |
| 14 | Version label + Close (surface 6) | `:979-986` | always |

There is no row model and no column list: the window's body IS the button column.

**(e) Action controls and backends.** Full enumeration in surface 3.

**(f) STATE VARIANTS.** See surface 3 (mode variants) and surface 2 (flight status variants).

**(g) Sizing facts.** Width is host-pinned at `GUILayout.Width(250)` (`ParsekFlight.cs:2124`,
`ParsekKSC.cs:248`); the height is reset to `0f` before every draw so IMGUI re-measures it
from content (`ParsekFlight.cs:2110`, `ParsekKSC.cs:237`). Rect defaults differ per host:
flight `new Rect(20, 100, 250, 250)` (`ParsekFlight.cs:962`), KSC `new Rect(20, 100, 200, 10)`
(`ParsekKSC.cs:23`) - the KSC width 200 is immediately overridden by the 250 pin on the first
draw. The census moved both to `[8,8]` via `UiAction op=rect`, and every label shows the
window rect as `[8, 8, 250, 0]` (height 0 is the pre-layout value the recorder reads).
Measured content heights from the census: KSC Basic 196 px inner group, KSC Advanced 256 px,
FLIGHT Basic 298 px, FLIGHT Advanced 393 px. Every launcher button lays out 230 x 21 px at
x=18. There is no resize handle on the main window (`ParsekUI.DrawResizeHandle` `:2801` and
`HandleResizeDrag` `:2771` are helpers for the sub-windows; `DrawWindow` calls neither).

---

## 2. Flight status block

**(a)** Section inside the main window; labels only, no controls.

**(b)** `ParsekUI.DrawFlightStatus()`, `ParsekUI.cs:995`, called from `DrawWindow` at `:748`.

**(c)** FLIGHT scene only: `if (InFlight) DrawFlightStatus();` (`:747`), where
`InFlight => mode == UIMode.Flight` (`ParsekUI.cs:20`). No `UiSurface` key; it is NOT
mode-gated, so it draws in both Basic and Advanced.

**(d)** Five labels, one conditional:

| Line | Text | Source |
|------|------|--------|
| `:997` | `Status` (boxed header) | literal |
| `:998` | `State: {GetStatusText()}` | `ParsekUI.GetStatusText()` `:2699` |
| `:999` | `Recorded Points: {n}` | `flight.recording.Count` |
| `:1003` | `Duration: {d:F1}s` | last point UT minus first, ONLY when `flight.recording.Count > 0` (`:1000`) |
| `:1005` | `Active Ghosts: {n}` | `flight.TimelineGhostCount` |

**(e)** No action controls.

**(f) STATE VARIANTS.** `GetStatusText()` (`ParsekUI.cs:2699-2705`) has four values:

| Variant | Condition | Census label |
|---------|-----------|--------------|
| `Idle` | not recording, not playing, zero points | flight-main-basic, flight-main-advanced |
| `RECORDING` | `flight.IsRecording` | NONE |
| `PREVIEWING` | `flight.IsPlaying` | NONE |
| `Ready (has recording)` | points exist, idle | NONE |
| `Duration:` line present | `recording.Count > 0` | NONE (census shows `Recorded Points: 0`) |
| `Active Ghosts` > 0 | any live timeline ghost | NONE (census shows 0) |

**(g)** No explicit width/height pins; labels expand to the 230 px content width. Census
heights: `Status` box 23 px, each plain label 21 px.

---

## 3. Launcher button column

**(a)** Section inside the main window. This is the "LAUNCHER BUTTON for each sub-window"
inventory the brief asks for.

**(b)** Inline in `ParsekUI.DrawWindow`, `ParsekUI.cs:770-959`.

**(c)** Scene: wherever the main window draws. The per-button gate is
`UiSurfaceVisibility.IsVisible(key, complexity)` reading the FRAME-LATCHED mode
`ParsekUI.AppliedUiComplexityMode` (`ParsekUI.cs:246`), captured once at `:767`. Never the
settings field - a raw read would change the IMGUI control count between Layout and Repaint
(`ParsekUI.cs:236-244`).

**(d)/(e) The column, with tooltips verified against the census JSON:**

| Button label | Tooltip | Line | Gate | Toggles / calls |
|---|---|---|---|---|
| `Real Spawn Control ({N})` | `Turn a recorded craft passing nearby into a real vessel.` | `:775` | `InFlight && flight != null && IsVisible(MainButtonSpawnControl, ...)` `:770-771` | `spawnControlUI.IsOpen = !spawnControlUI.IsOpen` `:784` |
| `Timeline` | `Every recorded flight and career event on one clock.` | `:794` | none (constant-true both modes) | `timelineUI.IsOpen = !timelineUI.IsOpen` `:798` |
| `Missions` | `Your missions, and the recordings they are built from.` | `:806` | none | `ParsekUI.ToggleRecordingsWindow()` `:809` -> `:1081` flips `recordingsTableUI.IsOpen` |
| `Logistics` | `Supply routes that repeat a delivery you already flew.` | `:885` | none | `logisticsUI.IsOpen = !logisticsUI.IsOpen` `:889` |
| `Kerbals` | `Who is reserved, flying or retired in your timeline.` | `:914` | `IsVisible(MainButtonKerbals, ...)` `:904` | `kerbalsUI.IsOpen = !kerbalsUI.IsOpen` `:918` |
| `Career` | `Contracts, strategies and buildings along the timeline.` | `:925` | `IsVisible(MainButtonCareer, ...)` `:906` | `careerStateUI.IsOpen = !careerStateUI.IsOpen` `:929` |
| `Gloops Flight Recorder` | `Record a ghost-only flight that your career ignores.` | `:943` | `InFlight && IsVisible(MainButtonGloops, ...)` `:941` - **never true**, `UiSurfaceVisibility.IsRetired` returns true for `MainButtonGloops` (`UI/UiComplexityMode.cs:142`) and `IsVisible` short-circuits at `:162-163` | would flip `gloopsUI.IsOpen` `:947` |
| `Settings` | `Recording, looping, ghost and diagnostic options.` | `:955` | none | `ParsekUI.ToggleSettingsWindow()` `:958` -> `:1087` flips `settingsUI.IsOpen` |

The `Kerbals` and `Career` labels come from `GetKerbalsMainButtonLabel()` (`:212`) and
`GetCareerMainButtonLabel()` (`:214`), both constant strings - the per-state counts
deliberately live inside the windows.

**Conditionally DISABLED (not hidden):** the Real Spawn Control button only. `GUI.enabled =
spawnCount > 0` at `:774` with `spawnCount = flight.NearbySpawnCandidates.Count` (`:773`,
backed by `ParsekFlight.cs:511`); restored at `:790`. Its greyed-out reason is published to
the tooltip strip via `DisabledHoverEcho.CarryLastControl(spawnCount > 0,
SpawnControlLauncherDisabledReason(spawnCount))` at `:780`, and that reason string is
`"No recorded craft is passing nearby"` (`ParsekUI.cs:142`).

**Conditional TINT on Logistics** (`:874-884`): the whole button is drawn red
`Color(0.95f,0.45f,0.45f)` when `LogisticsButtonState.AnyRouteHardBroken(...)`
(`UI/LogisticsButtonState.cs:27`) over `RouteStore.CommittedRoutes`; otherwise cyan
`Color(0.45f,0.85f,0.95f)` when `RouteRunPrompt.HasPendingPrompt`. Broken-red outranks the
hint-cyan. `GUI.color` is restored in a `finally` at `:894`.

**Separator policy:** the `Space(10f)` that OPENS the Kerbals/Career group is gated with the
group (`:908-909`), so Basic shows one gap between Logistics and Settings, not two. The
Spawn Control and Gloops blocks carry their trailing `Space` inside their own gate
(`:791`, `:951`).

**(f) STATE VARIANTS and census coverage.**

| Variant | Buttons drawn (census-verified) | Census label |
|---|---|---|
| KSC + Basic | Timeline, Missions, Logistics, Settings (4) | ksc-main-basic |
| KSC + Advanced | Timeline, Missions, Logistics, Kerbals, Career, Settings (6) | ksc-main-advanced, parsek-guitree-probe |
| FLIGHT + Basic | Timeline, Missions, Logistics, Settings (4) | flight-main-basic |
| FLIGHT + Advanced | Real Spawn Control (0), Timeline, Missions, Logistics, Kerbals, Career, Settings (7) | flight-main-advanced |
| Real Spawn Control ENABLED (count > 0) | same set, button not greyed | NONE |
| Logistics broken-red tint | same set, red button | NONE |
| Logistics pending-cyan tint | same set, cyan button | NONE |
| Gloops launcher drawn | n/a - unreachable in shipped code | NONE (and no fixture can produce one) |

Census cross-check passes: no `Gloops Flight Recorder` node appears in any of the four main
labels, and `Real Spawn Control (0)` appears only in flight-main-advanced.

**(g)** No width/height options on any launcher; all take the layout width. Census: every
launcher is 230 x 21 px at x=18, 25 px pitch inside a group, 35 px across a `Space(10f)`
separator.

---

## 4. RouteRunPrompt supply-route banner

**(a)** A non-modal banner drawn INSIDE the main window (deliberately not a dialog, so it
never interrupts gameplay - `ParsekUI.cs:811-816`).

**(b)** Inline in `ParsekUI.DrawWindow`, `ParsekUI.cs:817-855`.

**(c)** Gate: `RouteRunPrompt.HasPendingPrompt` (`:817`, backed by `Logistics/RouteRunPrompt.cs:51`
= `!string.IsNullOrEmpty(PendingPromptTreeId)`). Armed at route-candidate commit time. A
staleness sweep runs ONLY on the Layout event so the Repaint pass sees the same control
count: `if (Event.current.type == EventType.Layout && RouteStore.IsCandidateDismissed(...))
RouteRunPrompt.ClearPendingPrompt("dismissed-elsewhere")` (`:822-827`).

**(d)** One boxed two-line label `Supply Route candidate:\n'{label}'` (`:830-832`, label
falls back to `<unnamed>`), then a horizontal row of two buttons, then `Space(3f)` (`:853`).

**(e)**

| Control | Line | Backend |
|---|---|---|
| `Open Logistics` (no tooltip) | `:834` | sets `logisticsUI.IsOpen = true` (`:836`) then `RouteRunPrompt.ClearPendingPrompt("opened-logistics")` (`:839`) |
| `Dismiss` tooltip `Drops the suggestion; undo it in Logistics > Dismissed.` | `:841` | `RouteStore.DismissCandidateTree(treeId, label)` (`:848`) then `ClearPendingPrompt("dismissed-from-banner")` (`:850`) |

Neither is ever disabled. The whole banner is hidden when no prompt is pending.

**(f) STATE VARIANTS.** One variant (pending), and it has **NO PICTURE** - all four main
census labels were captured with no pending candidate. See APPENDIX-NOPICTURE.

**(g)** `GUILayout.ExpandWidth(true)` on the box label (`:832`); the button row takes the
default split. No height pins.

---

## 5. Tooltip echo strip (the main window's tooltip host)

**(a)** Tooltip-only surface: a permanently visible, fixed-height help strip at the bottom of
the window, empty when nothing is hovered.

**(b)** `TooltipEchoBox.Draw()` (`UI/TooltipEchoBox.cs:215`), called from
`ParsekUI.DrawWindow` at `ParsekUI.cs:977`. The main window's instance is constructed
`new TooltipEchoBox(SpacingSmall)` at `ParsekUI.cs:210`, i.e. the DOUBLE-LINE default
(`UI/TooltipEchoBox.cs:119` -> `DoubleLine`), because the 250 px window is the narrowest in
the mod.

**(c)** Always drawn, in both scenes and both modes, gated by nothing. One instance per
`ParsekUI`, so it never outlives its scene (`ParsekUI.cs:210`).

**(d)** Exactly one `GUILayout.Space(spacing)` plus one `GUILayout.Label`
(`UI/TooltipEchoBox.cs:233`, `:241`/`:249`) every draw, so the control count and reserved
rect are constant across Layout and Repaint. Text is read live from `GUI.tooltip`
(`UI/TooltipEchoBox.cs:219`), with an optional caller override. Overflowing text scrolls as a
marquee rather than clipping (`UI/TooltipEchoBox.cs:44-58`).

**(e)** No action controls. It is a pure echo. The only writer unique to this section is
`DisabledHoverEcho.Carry` (`UI/DisabledHoverEcho.cs:105`), which paints a zero-visual
`GUI.Label` carrying a tooltip over a DISABLED control's rect on the Repaint event only
(`:100`, `:113`), so a greyed-out button can still explain itself. `ShouldCarry` requires
`!controlEnabled && reason != ""` (`UI/DisabledHoverEcho.cs:64`).

**(f) STATE VARIANTS.**

| Variant | Census label |
|---|---|
| Empty (nothing hovered) | all four main labels - the strip appears as `[label] rect=[18, y, 230, 38] text=''` |
| Hovered enabled launcher (tooltip echoed) | NONE |
| Hovered DISABLED Real Spawn Control (`No recorded craft is passing nearby`) | NONE |
| Marquee scrolling | NONE (no main-window tooltip is long enough to overflow 230 px x 2 lines) |

**(g)** Fixed 38 px in the census, matching two lines of the box style plus padding
(measured from probe text `"Ay\nAy"`, `UI/TooltipEchoBox.cs:75`). `NoWrapMeasureWidth =
10000f` (`:79`) is the probe width only. Spacing above is 3 px (`ParsekUI.cs:200`).

---

## 6. Version + Close footer

**(a)** Section inside the main window; the window's last content row (house ordering: the
tooltip strip sits above it, `ParsekUI.cs:972-977`).

**(b)** Inline, `ParsekUI.cs:979-986`.

**(c)** Always drawn.

**(d)** One horizontal row: version label, a 10 px spacer (`:981`), then Close.
`VersionLabel` is `"v" + Assembly.GetName().Version.ToString(3)` (`ParsekUI.cs:104`) - the
census shows `v0.10.5`. Its style is lazily built at `:964-971` (font size 10, 40% white,
`contentOffset (0,3)`).

**(e)**

| Control | Line | Backend |
|---|---|---|
| `Close` (no tooltip) | `:982` | `CloseMainWindow?.Invoke()` (`:985`). Host handlers: `ParsekFlight.cs:1361-1365` and `ParsekKSC.cs:164-168` both set `showUI = false` AND call `toolbarControl.SetFalse()` so the stock toolbar button un-presses. |

Never disabled.

**(f)** One variant, photographed in all four main labels.

**(g)** Version label `GUILayout.ExpandWidth(false)` (`:980`), census 35 x 17 px; Close
`GUILayout.ExpandWidth(true)` (`:982`), census 181 x 21 px at x=67.

---

## 7. Confirm: Wipe Recordings

**(a)** Modal `PopupDialog` (stock `MultiOptionDialog`, KSP `UISkin` - not IMGUI, so the
GuiTree recorder cannot see it).

**(b)** `ParsekUI.ShowWipeRecordingsConfirmation(int count)`, `ParsekUI.cs:1503`; the
`PopupDialog.SpawnPopupDialog` call is `ParsekUI.cs:1505`.

**(c)** Any scene hosting the Settings window. Called only from
`UI/SettingsWindowUI.cs:753` (the Data Management "Wipe All" button), so it inherits that
section's gate. Dialog id `ParsekWipeRecordingsConfirm`, anchored centre-screen
(`0.5,0.5` / `0.5,0.5`), `isModal=false` in the spawn call (`:1533`).

**(d)** Title `Confirm: Wipe Recordings`; body
`Delete all {count} recording(s) and their files?\n\nThis cannot be undone.`; two buttons.

**(e)**

| Control | Line | Backend |
|---|---|---|
| `Wipe All` | `:1513` | for each `RecordingStore.CommittedRecordings`: `CrewReservationManager.UnreserveCrewInSnapshot` (`:1520`); `CrewReservationManager.ClearReplacements()` (`:1521`); `flight.DestroyAllTimelineGhosts()` when `InFlight` (`:1522`); `RecordingStore.ClearCommitted()` (`:1523`); `GameStateStore.ClearScienceSubjects()` (`:1524`); Info log + `ParsekLog.ScreenMessage("All recordings wiped", 2f)` (`:1525-1526`) |
| `Cancel` | `:1528` | Verbose log only (`:1530`) |

Neither is ever disabled. The `[ERS-exempt]` comment at `:1515-1519` records why the raw
committed-list read is correct here (a wholesale wipe must unreserve crew for hidden /
superseded entries too).

**(f)** One variant. **NO PICTURE** - the dialog is a stock `PopupDialog`, outside the IMGUI
tree the recorder walks, and no census label opened it.

**(g)** Sizing is stock `MultiOptionDialog` defaults; Parsek pins nothing.

---

## 8. Confirm: Wipe Game Actions

**(a)** Modal `PopupDialog`, same shape as surface 7.

**(b)** `ParsekUI.ShowWipeActionsConfirmation(int count)`, `ParsekUI.cs:1536`; spawn call
`ParsekUI.cs:1538`.

**(c)** Called only from `UI/SettingsWindowUI.cs:762`. Dialog id
`ParsekWipeActionsConfirm`.

**(d)** Title `Confirm: Wipe Game Actions`; body
`Delete all {count} game action milestone(s)?\n\nThis cannot be undone.`; two buttons.

**(e)**

| Control | Line | Backend |
|---|---|---|
| `Wipe All` | `:1546` | `MilestoneStore.ClearAll()` (`:1548`), Info log, `ParsekLog.ScreenMessage("All game actions wiped", 2f)` (`:1550`) |
| `Cancel` | `:1552` | Verbose log only |

**(f)** One variant. **NO PICTURE**, same reason as surface 7.

**(g)** Stock dialog defaults.

---

## 9. Flight-scene ghost map markers (`DrawMapMarkers`)

**(a)** Marker overlay: per-ghost icon plus an optional hover/sticky text label, drawn
directly with `GUI.DrawTexture` / `GUI.Label` (no window).

**(b)** `ParsekUI.DrawMapMarkers()`, `ParsekUI.cs:1901`; per-marker draw
`ParsekUI.DrawMapMarkerAt(...)` `ParsekUI.cs:2744`, which delegates to
`MapMarkerRenderer.DrawMarkerAtScreen` (`MapMarkerRenderer.cs:208`).

**(c)** FLIGHT scene only, and only in MAP view: `if (MapView.MapIsEnabled)
ui.DrawMapMarkers();` (`ParsekFlight.cs:2099-2100`), inside the pause gate at `:2096`.
(The method itself still carries a flight-view branch at `ParsekUI.cs:1916-1924` and `:1969`,
which the single call site currently never reaches.) No `UiSurface` key.

**(d) STRUCTURE - this is a decision walk, not a layout.** Per frame:

1. Camera resolve, bail on null (`:1903-1924`; the map branch opens at `:1907`).
2. Manual preview ghost: fixed marker key `"preview"`, label `"Preview"`, colour
   `(0.2, 1, 0.4, 0.9)` (`:1928-1932`), gated on `flight.IsPlaying && flight.PreviewGhost != null` (`:1928`).
3. Pass 1: build the per-chain highest-index map `chainTipIndexBuffer` (`:1951-1961`, the walk opens at `:1953`).
4. Pass 2 over `flight.Engine.ghostStates` (`:1987`) - the ROW MODEL is one entry per live
   ghost state, keyed by committed-recording index.
5. Ghost-less fallback pass over `committed` for recordings whose polyline owns the phase but
   which have no ghost state (`:2425` opens the map-view block, `:2454` the walk).
6. `LogMapMarkerSummary(summary)` (`:2507`).

Each drawn marker is: a 20 x 20 px icon rect (`MapMarkerRenderer.cs:34`, `:218`) using the
stock vessel-icon atlas tinted `(0.71,0.71,0.71)` (`MapMarkerRenderer.cs:57`, `:295`) or a
fallback diamond in the ghost's type colour; plus, when sticky or hovered, a 150 x 20 label
`"Ghost: " + name` placed 2 px under the icon (`MapMarkerRenderer.cs:306-311`).

**(e) Action controls.** Two, both inside `MapMarkerRenderer.DrawMarkerAtScreen`:

| Interaction | Hit rect | Line | Backend |
|---|---|---|---|
| LEFT click | icon + 6 px pad each side (`ClickPadding`, `MapMarkerRenderer.cs:35`) | `:260-275` | the caller's `MarkerClickHandler`. `ParsekUI.DrawMapMarkerAt` passes NONE (`ParsekUI.cs:2763-2764`), so in the flight scene a left click falls through to stock. |
| RIGHT click | icon + 24 px pad (`ToggleClickPadding`, `:44`) | `:277-286` | `ToggleSticky(markerKey, stickyMarkers)` - pins/unpins the yellow label |

Marker key is `Recording.RecordingId` (`ParsekUI.cs:2327`) so hover/sticky state survives
index shuffles; the overlap variant uses `recId + "#" + cycle` (`ParsekUI.cs:2567`).

**Every skip condition (each `continue` is a hidden marker):**

| Skip | Line | Exact condition |
|---|---|---|
| `HiddenInFlight` | `:2021-2027` | `!meshActive && !isMapView` |
| per-instance overlap drew | `:2056-2089` | `isMapView && !IsDebris && GhostMapPresence.TryGetLiveOverlapHeadUTs(...)` |
| `NativeIcon` | `:2149-2155` | `GhostMapPresence.HasGhostVesselForRecording(idx)` and (`ghostPid == 0` or `!ShouldDrawNonProtoMarkerForGhost(pid)`) |
| `Debris` | `:2159-2165` | `committed[idx].IsDebris` |
| `ChainNonTip` | `:2167-2178` | chain id set, index != chain tip, and index != `descentIconCarrier` (`:2129`) |
| `LoopHidden` | `:2270-2276` | `ResolveTrackingStationSampleUT` reports the loop unit outside its render window |
| `PositionFailure` / `MissingBody` | `:2287-2296` | `TryComputeGhostWorldPosition` false; reason enum at `:1777` |
| behind camera | `ParsekUI.cs:2761` | `screenPos.z < 0` |

Position source precedence (`:2244-2263`): live mesh transform when `meshActive`, the
transform is farther than 1 m from the floating origin, AND the polyline does not own the
phase; otherwise the trajectory-derived position; and on top of that, when the polyline owns
the leg, the marker RIDES the drawn line via
`GhostTrajectoryPolylineRenderer.TryAnchorMarkerToPolyline` (`:2308`).

**(f) STATE VARIANTS.** ALL of them have **NO PICTURE**. Neither flight census label is a
map-view capture (`flight-main-basic` / `flight-main-advanced` are flight view - the MechJeb
chip and the flight status block prove it), so no marker node exists in any `.gui.json`.
Note that even a map-view capture would show the marker only as a bare
`GUI.Label` / `GUI.DrawTexture`, since markers are not windowed. Distinct unphotographed
variants: preview marker; proto-icon-suppressed marker; polyline-ridden marker; per-instance
overlap markers (N per recording); boundary-overlap secondary marker (`:2105`);
ghost-less fallback marker; sticky (pinned, full opacity) vs hover-transient label.

**(g)** `IconSize = 20` (`MapMarkerRenderer.cs:34`), `ClickPadding = 6` (`:35`),
`ToggleClickPadding = 24` (`:44`), `UnpinnedMarkerAlpha = 0.8f` (`:45`), label rect
`(x-75, y+12, 150, 20)` (`MapMarkerRenderer.cs:308`).

---

## 10. Flight-scene host

**(a)** Host, not a surface.

**(b)** `ParsekFlight.OnGUI()`, `ParsekFlight.cs:2087`.

**(c)** FLIGHT (and MAPVIEW, which is the same scene).

**(d)** Ordered body:

| Line | Action | Gate |
|---|---|---|
| `:2096-2097` | early `return` | `PauseMenuGate.IsPauseMenuOpen()` - skips BOTH Layout and Repaint so layout-driven sizes cannot flicker while paused (`:2094-2095`) |
| `:2099-2100` | `ui.DrawMapMarkers()` | `MapView.MapIsEnabled` |
| `:2102-2103` | `watchMode.DrawWatchModeOverlay()` | `watchMode.IsWatchingGhost` (owned by `WatchModeController`, another section) |
| `:2106` | `DrawGhostLabels()` | unconditional, no-op when ghost GOs are null |
| `:2108` | the whole window block | `showUI` |
| `:2110` | `windowRect.height = 0f` | forces re-measure every frame |
| `:2111-2113` | early `return` | `ui.GetOpaqueWindowStyle() == null` (`ParsekUI.cs:1350`) - headless / pre-skin |
| `:2115`, `:2127` | `ParsekUI.ResetWindowGuiColors` / `RestoreWindowGuiColors` in try/finally | (`ParsekUI.cs:1357`, `:1370`) - neutralises a caller's `GUI.color` so a tinted launcher cannot leak |
| `:2118-2125` | `ClickThruBlocker.GUILayoutWindow(GetInstanceID(), windowRect, ui.DrawWindow, "Parsek", opaqueWindowStyle, GUILayout.Width(250))` | |
| `:2131` | `ui.LogMainWindowPosition(windowRect)` | rate-limited position log (`ParsekUI.cs:1026`, `:1013`) |
| `:2132-2141` | ten `Draw*IfOpen(windowRect)` calls | each sub-window's own `IsOpen`; ALL ten draw regardless of complexity mode (design 7.1: bodies are never gated) |

The ten: Recordings/Missions (`:2132`), Timeline (`:2133`), Kerbals (`:2134`), Career
(`:2135`), Logistics (`:2136`), Structure (`:2137`), Settings (`:2138`), Spawn Control
(`:2139`), Gloops (`:2140`), Test Runner (`:2141`). Gloops additionally self-gates on
`InFlight && flight != null` inside `ParsekUI.DrawGloopsRecorderWindowIfOpen`
(`ParsekUI.cs:1743`).

**(e)** No controls of its own.

**(f)** Paused variant (nothing drawn) has NO PICTURE; the census cannot open the pause menu
(no seam verb does).

**(g)** `GUILayout.Width(250)` at `:2124`; default rect `ParsekFlight.cs:962`.
`ShowUIForTesting` (`:975-978`) and `MainWindowRectForTesting` (`:989-992`) are the automation
seams the census drives; the rect's SIZE is host-controlled, only the POSITION sticks
(`ParsekFlight.cs:983-987`).

---

## 11. KSC-scene host

**(a)** Host.

**(b)** `ParsekKSC.OnGUI()`, `ParsekKSC.cs:227`.

**(c)** SPACECENTER.

**(d)** Same shape as the flight host but with the gates in the OPPOSITE order and a shorter
sub-window list:

| Line | Action |
|---|---|
| `:229` | `if (!showUI) return;` - the showUI check comes FIRST here |
| `:233-234` | `if (PauseMenuGate.IsPauseMenuOpen()) return;` |
| `:237` | `windowRect.height = 0f` |
| `:238-240` | opaque-style null bail |
| `:242`, `:252` | Reset/Restore window GUI colors |
| `:245-247` | `ClickThruBlocker.GUILayoutWindow(GetInstanceID(), windowRect, ui.DrawWindow, "Parsek", opaqueWindowStyle, GUILayout.Width(250))` |
| `:254-261` | EIGHT sub-window draws: Recordings, Timeline, Kerbals, Career, Logistics, Structure, Settings, Test Runner |

**Difference from flight, worth noting:** the KSC host does NOT call
`DrawSpawnControlWindowIfOpen` or `DrawGloopsRecorderWindowIfOpen` (both are flight-only
surfaces), and it does NOT call `ui.LogMainWindowPosition`. It also draws no map markers,
no watch overlay and no ghost labels.

**(e)** No controls of its own.

**(f)** Census: the KSC host is the one that produced all 23 `ksc-*` labels.

**(g)** `GUILayout.Width(250)` at `:248`; default rect `new Rect(20, 100, 200, 10)`
(`ParsekKSC.cs:23`); test seams `ShowUIForTesting` (`:33-36`) and the rect property
(`:44-47`).

---

## 12. Tracking-Station host

**(a)** Host - and the answer to "what does it draw?" is: MARKERS ONLY, no window.

**(b)** `ParsekTrackingStation.OnGUI()`, `ParsekTrackingStation.cs:350`. Its entire body is
the pause gate (`:357-358`) plus one call, `DrawAtmosphericMarkers()` (`:360`, defined `:363`).

**(c)** TRACKSTATION. There is NO `ToolbarControl` in this class (no `AddToAllToolbars` call
anywhere in the file) and no `GUILayoutWindow` / `GUI.Window`. The source states it
outright at `ParsekTrackingStation.cs:390-391`: "No Parsek window is hosted in the Tracking
Station scene, so a marker click can never land on a Parsek window here." The seam design
agrees - `UiAction` answers `REJECTED ui-host-unavailable` in TS
(`docs/dev/design-autotest-command-seam.md`, the `#### UiAction` section).

**(d)** The atmospheric-marker walk plus, separately from `Update`, a stock `PopupDialog`
ghost action popup (`UpdateSelectedGhostPopup` `:1154`, opened at `:1234`, width
`GhostPopupWidth = 180f` `:41`). That popup is a ghost-interaction surface, not a main-window
surface; another section owns the TS marker/popup detail. The only fact this section pins is
that the popup is NOT the main window, and that OnGUI itself hosts no window.

**(e)** `DrawAtmosphericMarkers` deliberately lets MouseDown events through so
`MapMarkerRenderer`'s sticky-label click handling stays alive in TS (`:369-374`); a click is
blocked only when the pointer is over the current ghost popup
(`ShouldBlockAtmosphericMarkerClickForGhostPopup` `:744`).

**(f)** **NO PICTURE**: no census label is a Tracking-Station capture, in either run.

**(g)** No window rect. `GhostPopupWidth = 180f` (`:41`).

---

## 13. Stock ApplicationLauncher toolbar button

**(a)** Toolbar button (stock KSP uGUI chrome, not IMGUI - invisible to the GuiTree recorder).

**(b)** Mod registration: `ParsekToolbarRegistration.Start()`,
`ParsekToolbarRegistration.cs:11`, calling `ToolbarControl.RegisterMod(ParsekFlight.MODID,
ParsekFlight.MODNAME)` at `:21`, guarded by a static `registered` flag (`:9`, `:13-17`).
`[KSPAddon(KSPAddon.Startup.Instantly, true)]` at `:6` makes it a once-per-process DDOL addon.
The per-scene BUTTONS are created by the hosts, not by this class.

**(c)/(d)/(e)** Two button instances, one per hosting scene:

| Host | Line | Button id | Scenes | On-true | On-false |
|---|---|---|---|---|---|
| `ParsekFlight` | `:1350-1358` | `parsekButton` | `AppScenes.FLIGHT \| AppScenes.MAPVIEW` (`:1354`) | `showUI = true` (`:1352`) | `showUI = false` (`:1353`) |
| `ParsekKSC` | `:153-162` | `parsekKSCButton` | `AppScenes.SPACECENTER` (`:157`) | `showUI = true` + Verbose "Toolbar button ON" (`:155`) | `showUI = false` + "Toolbar button OFF" (`:156`) |

Both share `MODID = "Parsek_NS"` (`ParsekFlight.cs:333`), `MODNAME = "Parsek"`
(`ParsekFlight.cs:334`), and the textures `Parsek/Textures/parsek_64` (large) and
`Parsek/Textures/parsek_32` (small) (`ParsekFlight.cs:1356-1357`, `ParsekKSC.cs:159-160`).
The flight host defensively destroys an orphaned control from a rapid scene transition first
(`ParsekFlight.cs:1343-1348`). No TRACKSTATION and no editor button exists.

The Close handler wiring is the mirror direction and is symmetric in both hosts: closing the
window from inside also un-presses the toolbar via `toolbarControl.SetFalse()`
(`ParsekFlight.cs:1364`, `ParsekKSC.cs:167`).

**(f)** **NO PICTURE** - stock uGUI, outside the IMGUI funnels the recorder patches
(`GUI.DoWindow`, `GUI.DoButton`, etc., listed in every `.gui.json` `funnels` array).

**(g)** Stock ApplicationLauncher sizing; Parsek pins nothing.

---

## Cross-cutting: `PauseMenuGate` and `InputFocusGuard`

**`PauseMenuGate`** (`PauseMenuGate.cs:53`) - `IsPauseMenuOpen()` (`:63`) returns
`PauseMenu.exists && PauseMenu.isOpen` (`:71`) inside a try/catch that degrades to "not
paused" and logs `VerboseRateLimited` at 5 s (`:73-79`). `ProbeForTesting` (`:61`) is the
test seam, cleared by `ResetForTesting()` (`:82`). Three consumers, all in this section:
`ParsekFlight.cs:2096`, `ParsekKSC.cs:233`, `ParsekTrackingStation.cs:357`. Reason it exists
(`PauseMenuGate.cs:41-46`): stock map/vessel labels live on KSP's Canvas and sort under the
pause overlay automatically; the IMGUI layer does not, so without the gate ghost icons and
Parsek windows render ON TOP of the pause menu.

**`InputFocusGuard`** (`InputFocusGuard.cs:17`, a 39-line static class) - `IsTextFieldFocused()`
(`InputFocusGuard.cs:24`) returns `GUIUtility.keyboardControl != 0` (`:29`, reading
`GetKeyboardControl()` at `:37`). It gates raw `Input.GetKeyDown` shortcut handlers, NOT
window drawing: no surface in this section calls it, and no control in `ParsekUI.DrawWindow`
is conditioned on it. The documented dead end (`InputFocusGuard.cs:11-15`) is that a uGUI
`EventSystem` check was rejected because clicking the stock ApplicationLauncher button that
opens the Parsek window would itself satisfy it and silently kill watch-mode shortcuts.
`KeyboardControlProviderForTesting` (`:22`) is the test seam, cleared by
`ResetTestOverrides()` (`:32`).

(Citation-audit note: these line numbers were the ONE set this document's automated
citation check found out of range, and they are corrected above.)

---

## Census cross-check notes

- Every `.gui.json` in both runs contains a second, unrelated root: the kRPC server window
  (`rect [10,10,288,290]`), plus in the flight run a `MechJeb` repeat-button chip at
  `[930,0,100,25]`. Neither is Parsek.
- `parsek-guitree-probe` is a recorder self-test window (`Parsek GuiTree Probe`,
  `[60,60,320,300]`, synthetic `parsek-probe-*` controls exercising every funnel including
  scrollview/slider/textfield). It is NOT a player surface and is not documented as one; its
  Parsek main-window root is identical to `ksc-main-advanced`.
- Node counts match the gating exactly: `ksc-main-basic` 10 Parsek nodes vs
  `ksc-main-advanced` 12 (the Kerbals + Career pair); `flight-main-basic` 11 vs
  `flight-main-advanced` 14 (Real Spawn Control + Kerbals + Career).
- No census label photographs a `Duration:` label, a non-`Idle` state, a pending route
  banner, a tinted Logistics button, an enabled Real Spawn Control, a populated tooltip
  strip, either Wipe dialog, any map marker, or the Tracking Station.

---


(No `ParsekLog.ScreenMessage` call exists in `ParsekToolbarRegistration.cs`, `PauseMenuGate.cs`
or `InputFocusGuard.cs`. The two sites in `ParsekTrackingStation.cs` (`:1990`, `:2011`) belong
to the TS ghost-popup surface, which another section owns.)

---

# Section 02 - "Parsek - Missions" window chrome, tab bar, and the RECORDINGS TAB

Scope: `UI/RecordingsTableUI.cs`, `UI/RecordingsTableFormatters.cs`, `UI/UnfinishedFlightsGroup.cs`,
`UI/GroupPickerUI.cs`, `UI/GroupPickerPresentation.cs`. All paths relative to `Source/Parsek`.
The MISSIONS TAB BODY (`UI/MissionsWindowUI.cs`) is owned by another section; only the tab bar that
selects it and the shared chrome are documented here.

`UI/TimeRangeFilter.cs` ownership: SHARED, not Timeline-only. `RecordingsTableUI` both draws its own
"Filtered: ..." indicator + Clear button (`UI/RecordingsTableUI.cs:1489-1518`, called at
`:1635`) and uses `TimeRangeFilterLogic.DoesAnyRecordingOverlapRange` /
`DoesRecordingOverlapRange` to drop non-overlapping roots (`:1685`, `:1716`, `:1737`). The state
object itself lives on the parent (`ParsekUI.cs:110`); the SLIDERS that set it live in
`UI/TimelineWindowUI.cs:902`. Documented below as surface 2.2.

---

## 2.1 Parsek - Missions (window chrome + two-tab bar)

(a) Name and kind: `Parsek - Missions` - WINDOW (IMGUI `GUILayoutWindow`), hosting a two-tab bar.

(b) Class + draw method: `RecordingsTableUI.DrawIfOpen(Rect)` at `UI/RecordingsTableUI.cs:615`;
window created by `ClickThruBlocker.GUILayoutWindow` at `:651` with id `"ParsekRecordings".GetHashCode()`
(`:652`) and title literal `"Parsek - Missions"` (`:655`). Content callback `DrawRecordingsWindow(int)`
at `:1520`.

(c) Scenes + gate: any scene where `ParsekUI` draws (KSC, Flight/Map, Tracking Station - the class
branches on `parentUI.InFlightMode` at `:633`, `:1346`, `:2084` and on `parentUI.Mode ==
UIMode.TrackingStation` at `:4613`). Shown iff `showRecordingsWindow` (`:21`, public `IsOpen` at
`:428`); `DrawIfOpen` early-returns and releases the input lock at `:617-628`. Opened by the main
window's "Missions" button and by `ShowMissionForRecording` (`:465`) / `ScrollToRecording` (`:519`),
both of which force `showRecordingsWindow = true` (`:467`, `:521`).

(d) STRUCTURE top-down:

| Order | Element | file:line | Notes |
|---|---|---|---|
| 1 | `GUILayout.Space(5)` under the title bar | `:1523` | matches Timeline spacing |
| 2 | Tab bar `GUILayout.Toolbar(selectedTab, TabLabels, toggleButtonStyle)` | `:1551` | drawn ONLY when `VisibleTabCount(complexity) > 0` (`:1549`); `GUILayout.Space(3)` after (`:1557`) |
| 3 | Tab content: Missions (early return at `:1564-1578`) or Recordings body | `:1575` / `:1580+` | Missions delegates to `parentUI.GetMissionsUI().DrawMissionsTabContent()` (`:1575`) |
| 4 | Bottom bar | `:1814` (Recordings) / `:1576` (Missions) | `DrawRecordingsBottomBar` `:1399`, `DrawMissionsTabBottomBar` `:1458` |

Tab labels and tooltips (`:150-156`), static readonly `GUIContent[]`:

| Index | Const | Label | Tooltip |
|---|---|---|---|
| 0 | `TabMissions` (`:145`) | `Missions` | `Your flights grouped as whole missions - the higher-level view.` |
| 1 | `TabRecordings` (`:146`) | `Recordings` | `The raw table of every recorded vessel, one row each.` |

Default selection is `TabMissions` (`:147`), transient (never persisted, see comment `:138-144`).

(e) Action controls:

| Control | file:line | Backend | Disabled/hidden condition |
|---|---|---|---|
| Toolbar tab click | `:1551` | writes `selectedTab`; logs via `LogTabSwitch` (`:1483`) | whole toolbar HIDDEN in Basic (`VisibleTabCount == 0`, `:1549`) |
| Window drag | `:1452` (rec) / `:1478` (missions) | `GUI.DragWindow()` | never |
| Resize handle | `:1449` / `:1475` | `ParsekUI.DrawResizeHandle`; drag clamped by `ParsekUI.HandleResizeDrag` at `:642` | clamped to `MinWindowWidth` / `MinWindowHeight` |
| Close (Missions tab) | `:1467` | `showRecordingsWindow = false; groupPicker.Close()` (`:1469-1470`) | never |
| Camera input lock | `:673-684` | `InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, "Parsek_RecordingsWindow")` (`:41`, `:677`) | armed only while the mouse is inside the rect |

Basic-mode side effects owned by this class: `ClampTabForBasic()` (`:216`) forces `selectedTab` to
`TabMissions`, and `CloseGroupPickerForModeChange()` (`:231`) closes the group picker popup, because
the picker draws from `DrawIfOpen` (`:668`) regardless of the selected tab and would otherwise keep
offering group mutations from a surface Basic hides.

(f) STATE VARIANTS:

| Variant | Census label | Evidence |
|---|---|---|
| Advanced, Missions tab selected, KSC | `ksc-missions-missions-advanced` | 922 nodes |
| Advanced, Recordings tab selected, KSC | `ksc-missions-recordings-advanced` | 415 nodes; `buttongrid` at `[10,43,1260,21]` is the tab bar |
| Basic (no tab bar) | `ksc-missions-basic` | window `Parsek - Missions` present at `[0,8,1280,700]`, NO `buttongrid` node anywhere - the zero-tab rule photographed |
| Advanced, Missions tab, FLIGHT | `flight-missions-missions-advanced` | 964 nodes, `buttongrid` present |
| Advanced, RECORDINGS tab, FLIGHT | NONE | `harness/scenarios/GUI-2-census-flight.toml:171-177` opens `missions`, rects it, selects tab `missions`, captures, closes. It never issues `op=tab tab=recordings`, so the flight-only Watch column is unphotographed |

(g) Sizing facts:

| Fact | Value | file:line |
|---|---|---|
| `MinWindowWidth` | `= DefaultCollapsedWindowWidth` (1355) | `:48` |
| `MinWindowHeight` | 150 | `:49` |
| `DefaultCollapsedWindowWidth` | `1205 + ColW_Rewind(60) + ColW_ReFly(90)` = 1355 | `:63` |
| `DefaultExpandedWindowWidth` | `1663 + 60 + 90` = 1813 | `:64` |
| First-open rect | `x = main.x + main.width + 10`, `y = main.y`, `w = DefaultCollapsedWindowWidth`, `h = main.height` in flight else `main.height * 2` | `:631-640` |
| Seeded only when `width < 1` | so a commanded `UiAction op=rect` survives | `:631`, doc `:23-31` |
| Census rect used | `[0, 8, 1280, 700]` (commanded, narrower than the 1355 default) | `GUI-1-census-ksc.toml:175` |
| Resize handle size const | 16 | `:42` |
| Test/automation seam | `WindowRectForTesting` get/set | `:32-36` |

---

## 2.2 Time-range filter indicator (Recordings tab only)

(a) Kind: SECTION (one horizontal strip), Recordings tab only.
(b) `RecordingsTableUI.DrawTimeRangeFilterIndicator()` `UI/RecordingsTableUI.cs:1489`, called at `:1635`.
(c) Same scenes as the host window; gate: `parentUI.TimeRangeFilter.IsActive` - early return at `:1492`.
(d) Structure: one `Label` reading `"Filtered: " + ActivePresetName` (`:1498`) or
`"Filtered: " + FormatSliderLabel(MinUT) + " - " + FormatSliderLabel(MaxUT)` (`:1502-1505`),
`ExpandWidth(true)` (`:1507`), then a `Clear` button of `Width(50)` (`:1511`).
(e) `Clear` -> `filter.Clear()` + `parentUI.GetTimelineUI()?.ResetTimeRangeSliders()` (`:1513-1514`).
Never disabled; the whole strip is hidden when the filter is inactive.
(f) NO PICTURE: the census fixture has no active time-range filter, so the strip is absent from
`ksc-missions-recordings-advanced` (the first child of the window there is the header row at y=71).
(g) Sizing: label `ExpandWidth`, button `Width(50)`; no other pins.

---

## 2.3 RECORDINGS TAB - table header row (fixed, outside the scroll view)

(a) Kind: SECTION (fixed table header).
(b) `RecordingsTableUI.DrawRecordingsTableHeader(IReadOnlyList<Recording>)` `UI/RecordingsTableUI.cs:1196`,
called at `:1647`.
(c) Recordings tab only; drawn only when `committed.Count > 0` (else the single label `"No recordings."`
at `:1639`). Whole tab hidden in Basic (see 2.7).
(d) COLUMN LIST, left to right. Widths are the shared header/body constants at `:52-75`, `:319-324`,
`:331`. "Sort key" is the `SortColumn` a header click assigns (`:134`); `DrawSortableHeader` at
`:4616` routes to `ParsekUI.DrawSortableHeaderCore` which toggles `sortAscending` on a repeat click.

| # | Header label | Width const (px) | Sort key | Tooltip | file:line |
|---|---|---|---|---|---|
| 1 | (toggle, no label) + `#` in ONE boxed cell of `ColW_Enable + ColW_Index + 8` = 58 | `ColW_Enable` 20 (`:52`) / `ColW_Index` 30 (`:54`) | `#` sets `SortColumn.Index` (`:1239`) | toggle: `Turn ghost playback on or off for every recording at once - including ones hidden by the current filters.` ; `#`: `Sort by recording number - the order the recordings were captured in. Click again to reverse.` | `:1215-1243` |
| 2 | `Name` | `ExpandWidth` (width arg 0, expand true) | `SortColumn.Name` | `Sort the list by vessel name. Click again to reverse the order.` | `:1246-1247` |
| 3 | `Phase` | `ColW_Phase` 90 (`:53`) | `SortColumn.Phase` | `Sort by flight phase - whether the flight ends in atmosphere, in space, on approach, or on a surface.` | `:1249-1250` |
| 4 | `Site` | `ColW_Site` 90 (`:74`) | `SortColumn.LaunchSite` | `Sort by launch site - where each flight started from.` | `:1252-1253` |
| 5 | `Launch` | `ColW_Launch` 110 (`:55`) | `SortColumn.LaunchTime` (the DEFAULT, `:135`, ascending `:136`) | `Sort by launch date - when each flight started.` | `:1255-1256` |
| 6 | `Duration` | `ColW_Dur` 80 (`:56`) | `SortColumn.Duration` | `Sort by how long each flight lasted.` | `:1257-1258` |
| 7 | `MaxAlt` | `ColW_MaxAlt` 65 (`:319`) | none (plain Label) | `Highest altitude this flight reached.` | `:1264-1266` |
| 8 | `MaxSpd` | `ColW_MaxSpd` 65 (`:320`) | none | `Highest speed this flight reached.` | `:1267-1269` |
| 9 | `Dist` | `ColW_Dist` 65 (`:321`) | none | `Total distance this flight travelled.` | `:1270-1272` |
| 10 | `Pts` | `ColW_Pts` 35 (`:322`) | none | `How many trajectory points were recorded - a rough measure of how detailed and how large the recording is.` | `:1273-1275` |
| 11 | `Start` | `ColW_StartPos` 120 (`:323`) | none | `Where this flight starts: body and site, or the parent vessel it separated from.` | `:1276-1278` |
| 12 | `End` | `ColW_EndPos` 120 (`:324`) | none | `Where this flight ends up: body and site, or the parent vessel it is attached to.` | `:1279-1281` |
| 13 | `Status` | `ColW_Status` 120 (`:57`) | `SortColumn.Status` | `Sort by status - counting down to launch, flying now, or finished.` | `:1284-1285` |
| 14 | `Group` | `ColW_Group` 60 (`:75`) | none | `Folders each recording belongs to. Use the G button on a row to change them.` | `:1289-1291` |
| 15 | `Loop` label + select-all toggle in one boxed cell | `ColW_Loop` 60 (`:58`) | none | BOTH halves: `Replay every loopable recording on a repeating schedule, including ones hidden by the current filters. Greyed out when any recording is driven by a supply route.` | `:1309-1325` |
| 16 | `Period` | `ColW_Period` 90 (`:331`) | none | `Launch-to-launch period: how often the ghost relaunches. Shorter than the flight means launches overlap. Click the unit to cycle sec / min / hr / auto; auto follows Settings > Looping.` | `:1341-1343` |
| 17 | `Watch` | `ColW_Watch` 50 (`:59`) | none | `Follow a ghost with the camera. Only available in flight, for ghosts on the same body and within 300 km.` | `:1348-1350` |
| 18 | `Rewind` | `ColW_Rewind` 60 (`:60`) | none | `Jump the game clock back to a flight's launch, or forward to a launch that has not happened yet.` | `:1353-1355` |
| 19 | `Re-Fly` | `ColW_ReFly` 90 (`:61`) | none | `Take control of a flight that ended badly and fly it again from the moment it separated.` | `:1357-1359` |
| 20 | `Archive` label + FILTER toggle in one boxed cell | `ColW_Hide` 80 (`:62`) | none | BOTH halves: `Filter: hide archived recordings from this list. It archives nothing - use each row's Archive box for that.` | `:1365-1373` |
| 21 | scrollbar reservation spacer | `GUI.skin.verticalScrollbar.fixedWidth`, fallback 16 | n/a | none | `:1384-1388` |

Columns 7-12 are drawn ONLY when `showExpandedStats` (`:1262`, field `:318`).
Column 17 is drawn ONLY when `parentUI.InFlightMode` (`:1346`).
Every header cell is forced to `ColHeaderHeight = 32` (`:73`) so label-only and label+toggle cells
line up.

Census confirmation (`ksc-missions-recordings-advanced.gui.json`): the pixel rects match the
constants exactly at the commanded 1280 px width - merged `#` cell `[10,71,62,32]`, Name
`[76,71,205,32]` (the expanded remainder), Phase `[285,71,90,32]`, Site `[379,71,90,32]`, Launch
`[473,71,110,32]` with text `Launch ^` (default ascending arrow), Duration `[587,71,80,32]`,
Status `[671,71,120,32]`, Group `[795,71,60,32]`, Loop `[859,71,60,32]`, Period `[923,71,90,32]`,
Rewind `[1017,71,60,32]`, Re-Fly `[1081,71,90,32]`, Archive `[1175,71,80,32]`. No `Watch`, no
`MaxAlt`/`MaxSpd`/`Dist`/`Pts`/`Start`/`End` - i.e. KSC scene with Info collapsed.

(e) Header action controls:

| Control | Backend | Disabled condition |
|---|---|---|
| Select-all playback toggle (`:1218`) | writes `committed[i].PlaybackEnabled` for EVERY committed recording (`:1225-1226`) | never disabled |
| `#` sort button (`:1233`) | `sortColumn = Index` / flip `sortAscending`; `InvalidateSort()` (`:1240`) | never |
| Six sortable headers (`:1246`, `:1249`, `:1252`, `:1255`, `:1257`, `:1284`) | `parentUI.DrawSortableHeaderCore` + `InvalidateSort()` (`:4621`) | never |
| Select-all Loop toggle (`:1317`) | `BulkSetLoopPlayback(committed, indices: null, value, applyAutoRange: false)` (`:1336`, impl `:5631`) | `GUI.enabled = false` when `AnyRecordingRouteBound(committed, null)` (`:1303`, `:1315`); the WRITE is also blocked and logged `[RouteGuard]` at `:1331`. Disabled reason echoed via `DisabledHoverEcho.CarryLastControl(..., LoopToggleDisabledReason(null))` (`:1321-1322`) = `A supply route already drives one of these flights` (`:1018`) |
| `Archive` FILTER toggle (`:1370`) | `GroupHierarchyStore.HideActive = newHideActive` (`:1377`) | never; writes NOTHING to any recording |

(g) Sizing: header row is one `BeginHorizontal` (`:1199`) outside the scroll view; every cell pins
`GUILayout.Height(ColHeaderHeight)` = 32. The merged cell #1 is `Width(ColW_Enable + ColW_Index + 8)`
- the +8 absorbs the body pair's margin budget (rationale `:1210-1214`).

---

## 2.4 RECORDINGS TAB - table body (ROW MODEL)

(a) Kind: SECTION (scroll view + four row kinds).
(b) Body host `RecordingsTableUI.DrawRecordingsWindow` `:1650-1811`; row draws at `:1784-1804`.
(c) Recordings tab only. Scroll view `GUILayout.BeginScrollView(recordingsScrollPos, false, true,
ExpandHeight(true))` at `:1650` - horizontal scrollbar OFF, vertical ALWAYS on (why the header
reserves a scrollbar-width spacer). Dark `tableBodyBoxStyle` vertical at `:1655`.

Root items are unified and sorted together: root GROUPS (`:1679-1698`), root CHAINS (`:1710-1733`),
standalone RECORDINGS (`:1734-1752`), sorted by `CompareRootItemsForSort` (`:1758-1762`, impl `:5405`).
`RootItemType` enum at `:266`. `RootItemType.VirtualGroup` still exists in the enum and the dispatch
(`:1800`) but is never ADDED to `rootItems` any more - STASH is nested under its owning tree group
since 2026-04-24 (comment `:1764-1779`); only the render-audit log line remains (`:1776-1778`).

### Row model

| Row kind | Draw method | file:line | Columns it fills vs blanks |
|---|---|---|---|
| GROUP HEADER row (folder) | `DrawGroupTree` | `:2399` | all aggregate cells; Period is ALWAYS a blank label (`:2650`); Re-Fly always blank (`:2844`) |
| CHAIN BLOCK row | `DrawChainBlock` -> `DrawRecordingBlock` | `:4038` / `:4057` | Phase blank (`:4126`), Period blank (`:4212`), Watch blank (`:4213`), Re-Fly blank (`:4259`), Archive blank (`:4260`) |
| GROUPED BLOCK row (same display identity, non-chain) | `DrawGroupedRecordingBlock` -> `DrawRecordingBlock` | `:4048` / `:4057` | identical to chain block; differs only in the `G` popup target (`:4161-4164`) and the log word `Block` vs `Chain` (`:4045`, `:4054`) |
| VIRTUAL STASH group row | `DrawVirtualUnfinishedFlightsGroup` | `:3036` | Group blank (`:3154`), Loop blank (`:3164`), Period blank (`:3168`), Watch blank (`:3173`), Rewind blank (`:3176`), Re-Fly blank (`:3183`), Archive blank unless `GroupHierarchyStore.CanHide` (`:3189`, `:3213`) |
| RECORDING row (leaf) | `DrawRecordingRow` | `:1820` | all columns |

There is NO separate "expanded detail row": expansion is the caret on a group / chain / STASH header
(`expandedGroups` `:83`, `expandedChains` `:82`) which renders CHILD ROWS of the same five kinds,
prefixed by box-drawing tree connectors `TreeConnector` (`:2283`, glyphs `:2272-2275`) with a
per-level indent equal to the measured connector width (`ConnectorWidth` `:2297`,
`SelfConnectorIndent` `:2313`, `ChildConnectorIndent` `:2320`). Exactly one child section owns the
corner glyph, chosen by the pure `ResolveLastChildSection` (`:2334`) over blocks -> child groups ->
STASH; renderability predicates `IsRowVisible` (`:2349`), `DisplayBlockRendersAnything` (`:2366`),
`ChildGroupRendersAnything` (`:2386`).

Group child render order is fixed (`:2922-2966`): display blocks, then child sub-groups, then the
nested STASH virtual group.

### Recording row - per column

| Column | Content | file:line |
|---|---|---|
| Enable toggle | `rec.PlaybackEnabled`; tooltip `Play this recording back as a ghost. Unticked, the flight stays recorded but no ghost appears.` | `:1847-1857` |
| `#` | `(ri + 1)` in `indexRowStyle` | `:1860` |
| Name | `DrawRecordingNameCell` (`:2192`): connector prefix + `VesselName` or `Untitled`; tooltip `Click to select; double-click to rename this recording.` | `:2238-2242` |
| Phase | `RecordingStore.GetSegmentPhaseLabel(rec)` in one of five colour styles chosen by `GetPhaseStyleKey` (`:4668`); styles built at `:699-712` (atmo blue, exo light purple, space lime, approach cyan, surface orange) | `:1873-1888` |
| Site | `rec.LaunchSiteName` | `:1892` |
| Launch | `KSPUtil.PrintDateCompact(rec.StartUT, true)` or `-` when `Points.Count == 0` | `:1896-1899` |
| Duration | `FormatDuration(EndUT - StartUT)` | `:1902-1903` |
| MaxAlt/MaxSpd/Dist/Pts/Start/End | `GetOrComputeStats` + `RecordingsTableFormatters` (`FormatAltitude` `RecordingsTableFormatters.cs:11`, `FormatSpeed` `:18`, `FormatDistance` `:24`, `FormatStartPosition` `:35`, `FormatEndPosition` `:62`) | `:1907-1917` |
| Status | future -> `SelectiveSpawnUI.FormatCountdown` white; active -> same, green; past -> `TerminalStateValue` name or `past`, grey. Then `FormatRecordingVisualStatusText` may replace it with `static` / `stationary` (`:4690`, `:4708`) | `:1919-1978` |
| Group | `G` alone, or `G` + `X` when `rec.IsGhostOnly && CanOfferGhostOnlyDelete(mode)` (`:1982`, `:4611`) | `:1982-2011` |
| Loop | toggle, or blank placeholder when `ShouldSuppressRowLoopUi` (`:2018`, `:5544`) | `:2014-2064` |
| Period | `DrawLoopPeriodCell` (`:5762`), or blank placeholder under the same suppression | `:2070-2080` |
| Watch | `W` / `W*` button, flight only | `:2084-2130` |
| Rewind | `DrawLegacyRewindForwardCell` (`:3255`) | `:2132` |
| Re-Fly | `DrawReFlyColumnCell` (`:3411`) | `:2135` |
| Archive | per-row `Hidden` toggle | `:2139-2174` |

(e) EVERY action control in the body, with backend and gate:

| Control | Row kind | file:line | Backend | Disabled / hidden condition |
|---|---|---|---|---|
| Enable toggle | recording | `:1847` | `rec.PlaybackEnabled = enabled` | never |
| Enable toggle (folder) | group | `:2441` | writes every descendant | never |
| Enable toggle (block) | chain/block | `:4081` | writes every member | never |
| Enable toggle (STASH) | virtual | `:3079` | writes every member; tooltip `Turn ghost playback on or off for every unfinished flight in STASH.` | never |
| Name button (single click) | recording | `:2239` | records click time for double-click detection | never |
| Name button (double click) | recording | `:2245-2257` | starts inline rename; commit `CommitRecordingRename` (`:4299`) on Return, cancel on Escape (`:2221-2231`) | rename commit DROPS if the id left the committed list (`:4305-4309`) |
| Caret button (folder) | group | `:2495` | single click toggles `expandedGroups`; double click starts rename | rename BLOCKED for `RecordingStore.IsPermanentRootGroup` (`:2503-2508`) and again at commit (`:4325-4329`); invalid chars rejected at `:4333-4336` |
| Caret button (block) | chain/block | `:4115` | toggles `expandedChains`; tooltip `Click to expand or collapse the individual segments of this flight.` | no rename path at all |
| Caret button (STASH) | virtual | `:3103` | toggles `expandedGroups`; tooltip is `UnfinishedFlightsGroup.Tooltip` (`UnfinishedFlightsGroup.cs:42`) | no rename (system group) |
| `G` | recording | `:2003` / `:1985` | `groupPicker.OpenForRecording(ri, mousePos)` (`:2008`) | never disabled; adds later rejected by `GroupPickerUI.CanAddToUserGroup` (see 2.6) |
| `X` (delete ghost-only) | recording | `:1987` | sets `pendingDeleteGhostOnlyRecordingId`; consumed next pass at `:1583-1593` -> `DeleteGhostOnlyRecording` (`:4570`) -> `flight.DeleteGhostOnlyRecording` or `RecordingStore.DeleteRecordingFull` (`:4598`, `:4603`). NO confirmation dialog | rendered only when `rec.IsGhostOnly && parentUI.Mode != UIMode.TrackingStation` (`:1982`, `:4611`) |
| `G` (folder) | group | `:2580` / `:2587` | `groupPicker.OpenForGroup(groupName, mousePos)` (`:2594`) | never |
| `X` (disband folder) | group | `:2582` | `ShowDisbandGroupConfirmation` (`:4399`) -> `RecordingStore.ReplaceGroupOnAll` + `GroupHierarchyStore.RemoveGroupFromHierarchy` (`:4437-4438`) | rendered only when `!RecordingStore.IsPermanentGroup(groupName)` (`:2571`, `:2578`); re-checked at `:4403` |
| `G` (block) | chain/block | `:4156` | `groupPicker.OpenForChain` when there is a chain id, else `OpenForRecordings(members, ...)` (`:4161-4164`) | never |
| Loop toggle | recording | `:2035` | `rec.LoopPlayback = loop` + `ApplyAutoLoopRange(rec, loop)` (`:2057-2058`) | HIDDEN (blank cell) when `ShouldSuppressRowLoopUi` = inside STASH or `!Recording.IsLoopableRecording` (`:5544`). GREYED + write-blocked when `IsRecordingRouteBound(rec)` (`:2029`, `:2033`, block at `:2049-2054`); tooltip becomes `Looped by route: {name}` (`:2038`) |
| Loop toggle (folder) | group | `:2623` | `BulkSetLoopPlayback(committed, descendants, newLoop, applyAutoRange: false)` (`:2643`) | blank cell when `grpLoopAgg.SuppressToggle` (`:2612`, i.e. zero loopable descendants); greyed + blocked when `AnyRecordingRouteBound(committed, descendants)` (`:2610`, `:2621`, block `:2636-2640`) |
| Loop toggle (block) | chain/block | `:4187` | `BulkSetLoopPlayback(..., applyAutoRange: TRUE)` (`:4207`) - the one aggregate that narrows the loop window | same two gates (`:4174-4176`, `:4185`, block `:4200-4205`) |
| Period value field | recording | `:5844` / `:5872` | `CommitLoopPeriodEdit` on Return (`:5882`) or on click-outside (`HandleRecordingsDefocus` `:1092-1098`) | `GUI.enabled = false` when `!rec.LoopPlayback` (`:5774`) or `LoopTimeUnit.Auto` (`:5811`); reasons `Turn Loop on for this flight to set its period` / `Auto uses the shared period from Settings` (`:1029-1032`) |
| Period unit button | recording | `:5890` | `rec.LoopTimeUnit = CycleRecordingUnit(...)` (`:5724`, `:5895-5896`) | greyed with the loop-off reason in the disabled branch (`:5795-5799`) |
| `W` / `W*` | recording | `:2116` | `flight.EnterWatchMode(ri)` / `flight.ExitWatchMode()` (`:2121-2123`) | `GUI.enabled = ShouldEnableWatchButton(canWatch, isWatching)` (`:2112`, `:953`); `canWatch = IsWatchButtonEnabled(hasGhost, sameBody, inRange, rec.IsDebris)` (`:2091`, `:947`). Tooltip carries the reason via `GetWatchButtonTooltip` (`:1037`). Whole column HIDDEN outside flight (`:2084`) |
| `W` / `W*` (folder) | group | `:2754` | `GhostPlaybackLogic.AdvanceGroupWatchCursor` rotation (`:2687`) then `EnterWatchMode(nextTargetIdx)` / `ExitWatchMode()` (`:2761`, `:2767`); cursor kept in `groupWatchCursorByGroupName` (`:363`) | `GUI.enabled = canWatch \|\| isToggleOffPress` (`:2740`); tooltips `no watchable vessels in this group` / `switch to {name}` / `exit watch (no other watchable vessels)` / `enter watch on {name}` (`:2743-2752`) |
| `FF` | recording | `:3288` | `ShowFastForwardConfirmation(rec)` (`:3292`, dialog `:4496`) | rendered only when `ShouldShowForwardButton` (`:3270`, `:3712`); `GUI.enabled = RecordingStore.CanFastForward(...)` (`:3274`, `:3284`), tooltip is the refusal reason |
| `R` | recording | `:3319` | `ShowRewindConfirmation(rec)` (`:3323`, dialog `:4450`) | rendered only when `ShouldShowLegacyRewindButton` (`:3298`, `:3741` - suppressed when THIS row is itself an unfinished flight, `:3754`); `GUI.enabled = RecordingStore.CanRewind(...)` (`:3304`, `:3315`). Otherwise a blank cell, with a one-shot "tree branch" suppression log at `:3345` |
| `FF` / `R` (folder) | group | `:2809` / `:2829` | same two confirmations; targets `FindAggregateForwardRecordingIndex` (`:2800`, `:5112`) / `FindGroupLegacyRewindRecordingIndex` (`:2818`, `:5154`) | blank cell when neither index resolves (`:2838`) |
| `FF` / `R` (block) | chain/block | `:4227` / `:4247` | same; targets `FindAggregateForwardRecordingIndex` (`:4219`) / `FindAggregateLegacyRewindRecordingIndex` (`:4236`, `:5126`) | blank when neither resolves (`:4256`) |
| `Fly` + `Seal` | recording | `:3513` | `RewindInvoker.ShowDialog(rp, slotListIndex)` (`:3521`) / `HandleSealUnfinishedFlightClick` -> `UnfinishedFlightSealHandler.ShowConfirmation` (`:3526`, `:3656`) | rendered only when `ResolveReFlyColumnAction == FlySeal` (`:3416`, `:3692`); `Fly` disabled by `CanInvokeRewindPointSlot` (`:3494`, `:3903`), with the refusal as tooltip (`:3508-3510`). Fully disabled twin drawn by `DrawDisabledUnfinishedFlightRewindButton` (`:3531`) when the RP slot cannot be resolved (`:3478-3491`) |
| `Stash` + `Seal` | recording | `:3606` | `UnfinishedFlightStashHandler.TryStash(rec, out reason)` (`:3633`) / same seal handler | rendered only when `ResolveReFlyColumnAction == StashSeal` (`:3420`, `:3702`) |
| Archive toggle | recording | `:2141` | `rec.Hidden = hidden` + `NotifyTimelineOfArchiveChange()` (`:2170-2171`, `:494`) | REFUSED (flag unchanged) when `EffectiveState.IsUnfinishedFlight(rec)`, with a Warn + ScreenMessage (`:2158-2167`) |
| Archive toggle (folder) | group | `:2853` | writes every descendant `Hidden` + `GroupHierarchyStore.AddHiddenGroup` / `RemoveHiddenGroup` + `NotifyTimelineOfArchiveChange` (`:2860-2867`) | never |
| Archive toggle (STASH) | virtual | `:3199` | same shape | rendered only if `GroupHierarchyStore.CanHide(groupName)` - today always false, so the blank branch at `:3213` is what draws (`:3189-3191`) |

Notable drawing helpers: `DrawBodyCenteredButton` (`:1132`), `DrawRewindColumnButton` (`:1156`, the
compact style for the 60 px Rewind cell), `DrawBodyCenteredTwoButtons` (`:1167`, per-half
`DisabledHoverEcho` carriers so Fly and Seal attribute their own reasons).

(f) STATE VARIANTS photographed:

| Variant | Census label | Evidence |
|---|---|---|
| Group header rows, ALL COLLAPSED, KSC, Info collapsed, no filter | `ksc-missions-recordings-advanced` | 16 group rows at y=109..602, each `> {name} ({n})`, each with `G`, folder Loop toggle, folder `R`, folder Archive toggle |
| Group `R` button enabled with a resolved target | same | tooltips read `Rewind to launch: R.0-S.0` ... `Rewind to launch: L5-B4L-U1-C1` - so `FindGroupLegacyRewindRecordingIndex` resolved on all 16 |
| Status column values | same | `past`, `Destroyed`, `Splashed`, `Landed`, `Orbiting` all present |
| RECORDING leaf rows | NONE | every group is collapsed in the census; the spec issues no expand op |
| CHAIN / GROUPED BLOCK rows | NONE | same reason |
| STASH virtual group | NONE | fixture `c1-gui` has no unfinished flights at capture time |
| Info expanded (columns 7-12) | NONE | the spec never clicks `Info` |
| Watch column | NONE | flight census stays on the Missions tab |
| Route-bound greyed Loop toggles | NONE | needs a logistics route driving a recorded tree |
| Inline rename text field (recording or group) | NONE | needs a double click, which the seam cannot issue |
| Time-range filter indicator | NONE | filter inactive |

(g) Sizing: rows are one `BeginHorizontal` each; every fixed cell pins its `ColW_*` constant and the
Name cell is the single `ExpandWidth(true)` (`:2242`, `:2498`, `:4118`, `:3103`). Body-cell text
inset `BodyCellTextIndent = 5` (`:403`), button left inset `BodyCellButtonLeftInset = 10` (`:408`),
Name lead gap `NameColumnLeadGap = 5` (`:399`) plus a 2 px nudge on recording rows only (`:2199`).
Census row height is 29 px for group rows (`[14,109,1236,29]`). Deferred cross-link scroll assumes
a 22 px row pitch (`:1628`).

---

## 2.5 RECORDINGS TAB - bottom bar (help strip + Info / New Group / Close)

(a) Kind: SECTION (footer).
(b) `RecordingsTableUI.DrawRecordingsBottomBar` `UI/RecordingsTableUI.cs:1399`, called at `:1814`.
(c) Recordings tab only; the Missions tab gets the reduced `DrawMissionsTabBottomBar` (`:1458`).
(d) Structure top-down: `GUILayout.FlexibleSpace()` (`:1402`) pins it to the window bottom; then the
one-line hover help strip `DrawRecordingsWindowTooltip()` (`:1406`, impl `:6064` -> `TooltipEchoBox`
constructed `SingleLine` at `:309-310`); then one horizontal row with Info / New Group / Close; then
the resize handle (`:1449`) and `GUI.DragWindow()` (`:1452`).

(e) Footer controls:

| Control | Width | Tooltip | Backend | Condition |
|---|---|---|---|---|
| `Info >` / `Info <` | `Width(65)` (`:1416`) | `Show or hide the extra statistics columns. Turning them on widens the window.` | flips `showExpandedStats`; on expand forces `recordingsWindowRect.width = DefaultExpandedWindowWidth` if narrower, on collapse forces it back to `DefaultCollapsedWindowWidth` (`:1418-1423`) | drawn only when `committed.Count > 0` (`:1410`) |
| `New Group` | `Width(80)` (`:1430`) | `Create an empty folder and name it. Put recordings in it with the G button on each row.` | `GenerateUniqueGroupName()` (`:4385`) -> `KnownEmptyGroups.Add` + `expandedGroups.Add` + arms inline rename (`:1432-1437`) | always |
| `Close` | remainder | none | `showRecordingsWindow = false; groupPicker.Close()` (`:1442-1443`) | always |

(f) Photographed by `ksc-missions-recordings-advanced`: help strip label `[10,646,1260,23]`,
`Info >` `[10,673,65,21]`, `New Group` `[79,673,80,21]`, `Close` `[163,673,1107,21]`, resize
glyph `//` `[1264,692,16,16]`. The `Info <` (expanded) variant has NO picture.

(g) Sizing: 65 / 80 / expand; the help strip is a permanently-visible box of constant height
regardless of hover (`:304-310`), which is what keeps the control count identical between the
Layout and Repaint passes.

---

## 2.6 Group picker popup - "Manage Groups" / "Set Parent Group"

(a) Kind: DIALOG (a second `GUILayoutWindow`, not a KSP `PopupDialog`).
(b) `GroupPickerUI.Draw()` `UI/GroupPickerUI.cs:193`; window at `:224`, id
`"ParsekGroupPopup".GetHashCode()` (`:225`); contents `DrawGroupPopupContents` (`:239`); node
recursion `DrawGroupPopupNode` (`:310`). Drawn from `RecordingsTableUI.DrawIfOpen:668`, i.e.
OUTSIDE the recordings window so the scroll view cannot clip it.
(c) Same scenes as the host. Gate: `groupPopupOpen` (`:74`), early return `:195`. Opened by four
entry points - `OpenForRecording` (`:103`), `OpenForChain` (`:123`), `OpenForRecordings` (`:140`),
`OpenForGroup` (`:160`). Force-closed on an Advanced -> Basic switch
(`RecordingsTableUI.CloseGroupPickerForModeChange:231` -> `Close():98`).
(d) Structure top-down:

| Order | Element | file:line |
|---|---|---|
| 1 | Title: `Set Parent Group` when opened for a GROUP, else `Manage Groups` | `:215-216` |
| 2 | Scroll view | `:241` |
| 3 | `(None / Root level)` toggle - group-parent mode only | `:244-250` |
| 4 | Recursive checkbox tree: `Toggle("", Width(20))`, optional caret `Button(arrow, label, Width(14))`, `Label(groupName, ExpandWidth)`; indent `depth * 12` | `:321-346` |
| 5 | New-group row: `TextField(ExpandWidth)` + `Button("+", Width(25))` | `:268-286` |
| 6 | `OK` `Width(60)` and `Cancel` `Width(60)`, flex-centred | `:291-303` |
| 7 | Resize handle + `GUI.DragWindow()` | `:305-307` |

(e) Controls and backends:

| Control | file:line | Backend | Condition |
|---|---|---|---|
| Per-group checkbox | `:325` | `GroupPickerPresentation.ApplySelectionToggle(..., singleSelect)` (`:327`) - single-select in group-parent mode | a group and all its descendants are SKIPPED entirely when in `treeModel.CycleInvalid` (`:315`) - you cannot parent a group to itself or a child |
| Caret | `:337` | toggles `groupPopupExpanded` | drawn only when the node has children (`:319`, `:333`) |
| `+` | `:270` | `GroupPickerPresentation.TryCreateGroupName` then `KnownEmptyGroups.Add` (`:273-284`) | silently no-ops when the name is empty or duplicate (the `TryCreate` false branch) |
| `OK` | `:293` | `ApplyGroupPopupChanges()` (`:356`) then closes | - |
| `Cancel` | `:298` | closes, discards `groupPopupChecked` | - |

`ApplyGroupPopupChanges` has four exclusive branches: group-parent (`:365-384`, ->
`GroupHierarchyStore.SetGroupParent`), chain (`:385-426`, all-or-nothing per target group ->
`RecordingStore.AddChainToGroup` / `RemoveChainFromGroup`), multi-recording (`:427-455`, per-recording
gate -> `AddRecordingToGroup` / `RemoveRecordingFromGroup`), single recording (`:456-476`).
The add gate is the pure `CanAddToUserGroup` (`:24`): it rejects when
`!GroupHierarchyStore.IsDropTargetAllowed(targetGroup)` (`:33`) or when
`EffectiveState.IsUnfinishedFlight(rec)` (`:38`). REMOVES are never gated.

(f) NO PICTURE: the popup needs a `G` click, which no census step issues and for which there is no
`UiAction` op. Both titles unphotographed.

(g) Sizing: `GroupPopupMinW = 220`, `GroupPopupMinH = 200` (`:88-89`); first-open rect is
`280 x 300` clamped to the screen at the click point (`:207-213`); rect reset to zero on every open
so it re-homes to the clicked `G` (`InitGroupPopupExpansion:181`).

---

## 2.7 Complexity gating (Basic vs Advanced)

There is EXACTLY ONE `UiSurfaceVisibility.IsVisible` call site in the whole of this section's files:
`UI/RecordingsTableUI.cs:189`, inside `VisibleTabCount(UiComplexityMode)` (`:187-192`). It returns
`TabLabels.Length` (2) in Advanced and `0` in Basic - deliberately zero, not one, because a
single-entry toolbar is noise and the window title already carries the identity (doc `:174-186`).

Three consumers of that one decision:

| Consumer | file:line | Effect in Basic |
|---|---|---|
| `ClampTabIndexForMode` | `:202-208` | any index clamps to `TabMissions` |
| Tab-bar draw guard | `:1549` | the `GUILayout.Toolbar` AND its trailing `Space(3)` are not drawn at all |
| Content dispatch pin | `:1562` | `activeTab` is PINNED to `TabMissions` rather than trusting the clamp |

So the exact surface Basic hides is: the `Recordings` tab button, and with it the entire recordings
table body (header 2.3, rows 2.4, footer 2.5's Info / New Group buttons) and every route to the
group picker popup - which is why `CloseGroupPickerForModeChange` (`:231`) exists. Two clamp sites
call `ApplyTabClamp` (`:253`): the deferred mode-apply via `ClampTabForBasic` (`:216`) and a
defensive on-draw pass at `:1541`.

`MissionsLoopControls` is NOT referenced anywhere in this section's files - it is gated inside
`MissionsWindowUI` (other section). Confirmed by grep over `RecordingsTableUI.cs`,
`GroupPickerUI.cs`, `UnfinishedFlightsGroup.cs`: only the `:189` hit.

---

## 2.8 Surprising findings

1. **`DrawRecordingTooltip` (`:5451`) is DEAD CODE.** A 78-line rich hover panel (max altitude, max
   speed, distance, points, chain status, orbit segments, part events, body, max range, a full
   storage/efficiency breakdown via `DiagnosticsComputation`, plus resource / inventory / crew
   manifests from `RecordingsTableFormatters`) that draws its own `GUI.Box` + `GUI.Label` with
   flip-to-fit positioning. Grep over `Source/Parsek` returns no caller. Consequently
   `FormatResourceManifest` (`RecordingsTableFormatters.cs:121`), `FormatInventoryManifest` (`:188`)
   and `FormatCrewManifest` (`:251`) have no live UI consumer from this file either. Those three
   manifest formatters are the ONLY place resource / inventory / crew deltas are formatted for the
   recordings table, so that information is currently unreachable from this window.
2. **`RootItemType.VirtualGroup` is a vestigial dispatch arm.** The enum member (`:266`) and the
   `switch` case (`:1800-1802`) survive, but nothing adds a `VirtualGroup` root item any more
   (comment `:1764-1779`); STASH only renders nested under its owning tree group.
3. **The `Archive` header toggle is the one header toggle that writes nothing.** Every other
   select-all in the header row is a bulk write; this one flips a view filter
   (`GroupHierarchyStore.HideActive`, `:1377`). Its tooltip is explicitly worded to say so.
4. **`ScrollToRecording` (`:519`) is kept with NO production caller** - documented as a deliberate
   2026-08-01 decision (`:504-514`), with the obligation that `RecordingsTableApiTests` drives the
   whole method. It is also the only remaining write that can produce a Basic-invalid tab index,
   which is why the defensive on-draw clamp exists.
5. **Group and block Loop bulk writes disagree on purpose.** Header and folder aggregates pass
   `applyAutoRange: false` (`:1336`, `:2643`) so a hand-tuned `LoopStartUT`/`LoopEndUT` survives an
   off/on cycle; chain and grouped blocks pass `true` (`:4207`) and re-narrow. Same button glyph,
   different semantics, documented at `:5616-5630`.
6. **The STASH group's aggregate loop toggle is deliberately absent, not merely disabled** (`:3164`,
   rationale `:3156-3163`) - surfacing it would write `LoopPlayback` back onto every member and undo
   the "this is a re-fly TODO list" intent.
7. **The census photographed only collapsed folders.** 415 nodes, 16 group header rows, zero
   recording rows. Everything the row model does - phase colours, status variants, loop period cell,
   Fly/Seal/Stash, tree connectors - is unphotographed.

---

# Section 03 - The Missions tab (and the Log / Structure window)

Scope: the body of the "Missions" tab drawn inside the "Parsek - Missions" window, plus the
separate "Parsek - Structure" (Log) window it launches. The window chrome (title bar, the
two-tab bar, Close, resize, drag, input lock) belongs to `UI/RecordingsTableUI.cs` and is
covered by another section; every claim below is about `UI/MissionsWindowUI.cs` and its pure
feeders unless a cite says otherwise. Paths are relative to `Source/Parsek`.

Census evidence used:
`harness/results/2026-09-11_0548_GUI-1-census-ksc_shots/` (KSC) and
`harness/results/2026-09-11_0551_GUI-2-census-flight_shots/` (flight).
Measured subtree sizes of the `Parsek - Missions` window root in the `.gui.json` dumps:
922 nodes (`ksc-missions-missions-advanced`), 813 (`ksc-missions-basic`), 922
(`flight-missions-missions-advanced` - structurally identical to the KSC advanced dump).
`ksc-structure-advanced` carries 3 nodes for the `Parsek - Structure` window: the window, one
label, one button.

---

## S1. Missions tab (tab body)

**(a) Name and kind** - "Missions" tab body; a tab inside the `Parsek - Missions` window. Not
its own window.

**(b) Class + draw method** - `MissionsWindowUI.DrawMissionsTabContent()`,
`UI/MissionsWindowUI.cs:746`. Class declared at `UI/MissionsWindowUI.cs:20`.

**(c) Scenes + gate** - Wherever the host window draws: FLIGHT and SPACECENTER (both census
halves photographed it). Gate: `UiSurface.TabMissions`, which is visible in BOTH modes
(`UI/UiComplexityMode.cs:175`), so Basic keeps the tab. The tab is also the clamp target when
Basic hides `TabRecordings` (`UI/RecordingsTableUI.cs:1536` comment; the clamp itself lives in
the chrome).

**(d) STRUCTURE top-down**

| Band | What | file:line |
|---|---|---|
| (host) two-tab bar | Missions / Recordings, owned by the chrome | census `buttongrid` at rect [10,43,1260,21], advanced only |
| Fixed column header row | outside the scroll view | `UI/MissionsWindowUI.cs:832` -> `:4245` |
| Scroll view | `BeginScrollView(..., false, true, ExpandHeight(true))` - vertical bar always on | `UI/MissionsWindowUI.cs:864` |
| Dark table-body box | `tableBodyBoxStyle` | `UI/MissionsWindowUI.cs:868` |
| Per mission, in sort order | header bar (S2) -> vessel rows (S3, each optionally preceded by a chapter header S5 and followed by expanded interval rows S4 and lineage children) -> partner-journey rows (S6) -> the event digest (S7) | `:902`, `:931`, `:932`, `:944`, `:948` |
| Empty state | `GUILayout.Label("No missions recorded yet.")` and early return | `UI/MissionsWindowUI.cs:826` |

Column list (left to right). Widths are the constants; the first cell is a MERGED
[enable-slot + index] cell of `ColW_Enable + ColW_Index + 8` so the name column starts at the
same x as the Recordings tab's Name column.

| # | Header text | Width const | Value | Sortable? | file:line |
|---|---|---|---|---|---|
| 1 | (blank enable slot) | `ColW_Enable = 20f` | always blank in this tab | no | `:302`, `:4264` |
| 2 | `#` (+ ` U+25B2/U+25BC` arrow) | `ColW_Index = 30f` | mission header: per-tree index; body rows: the include checkbox | yes -> `MissionSortColumn.Index` | `:303`, `:4266-4275` |
| 3 | `Missions and vessels` | expanding (`0f`, ExpandWidth) | mission title / vessel name + inline event phrase | yes -> `MissionSortColumn.Name` | `:4278` |
| 4 | `Next launch` | `ColW_TMinus = 105f` | live countdown, launch row only | no | `:342`, `:4287` |
| 5 | `Start time` | `ColW_StartTime = 120f` | `KSPUtil.PrintDateCompact` | yes -> `MissionSortColumn.StartTime` | `:304`, `:4289` |
| 6 | `Start event` | `ColW_StartEvent = 110f` | event word, dock-partner-named when resolvable | no | `:309`, `:4291` |
| 7 | `End event` | `ColW_EndEvent = 85f` | terminal word | no | `:310`, `:4292` |
| 8 | `End time` | `ColW_EndTime = 120f` | date | no | `:311`, `:4293` |
| 9 | `Re-Fly` | `ColW_ReFly = 90f` | Fly / Seal cell borrowed from the Recordings tab | no | `:334`, `:4297` |
| 10 | `Archive` + global toggle | `ColW_Archive = 80f` | per-mission checkbox on header rows, blank margin-0 spacer on body rows | no | `:355`, `:4302-4310` |
| 11 | (scrollbar gutter) | `GUI.skin.verticalScrollbar.fixedWidth`, 16f fallback | reserved so header right edges line up | - | `:4317-4322` |

Sort comparison is pure: `CompareMissionRows` at `:3833` (primary key per column, tiebreak
tree index then original list position so clones stay adjacent); the row list is built by
`BuildSortedMissionRows` at `:3802`. Sort changes log via `LogSortChanged` at `:3065`.

There is no footer inside the tab; the Close button and resize handle belong to the chrome.

**(e) Action controls and backends (tab level)**

| Control | Backend | Disabled/hidden when | file:line |
|---|---|---|---|
| `#` header button | flips `sortColumn`/`sortAscending` | never (sortable in BOTH modes since the 2026-08-20 playtest - the T1.7 Basic hide was reverted) | `:4270-4275` |
| `Missions and vessels` header | `parentUI.DrawSortableHeaderCore` | never | `:4278` |
| `Start time` header | same | never | `:4289` |
| Archive header toggle | writes `MissionStore.HideArchived`, logs `Missions Archive toggle: hideArchived=<b>` | never | `:4303-4315` |

Archive FILTER: a mission with `Archived == true` is skipped entirely while
`MissionStore.HideArchived` is on (`:888`). The filter is global, the flag per-mission.

**(f) STATE VARIANTS**

| Variant | Census label | Notes |
|---|---|---|
| Advanced, populated, all loops off | `ksc-missions-missions-advanced` (922 nodes) | 18 missions, every `Loop` toggle off, every period field `10` / `sec` |
| Basic, populated | `ksc-missions-basic` (813 nodes) | tab bar gone, no Clone, no Loop label/toggle, no period cell, no row checkboxes |
| Flight scene, Advanced | `flight-missions-missions-advanced` (922 nodes) | identical node count; Watch drawn but nothing flying |
| Empty list ("No missions recorded yet.") | NONE | needs a save with zero committed trees; `fresh-sandbox` / `fresh-career` fixture + `UiAction op=open window=missions` |
| Archive filter ON with an archived mission hidden | NONE | no seam op ticks a checkbox |
| Sorted by Name or Start time, or descending | NONE | no seam op clicks a header |

**(g) Sizing facts** - This tab pins no window size; the host does.
`RecordingsTableUI.MinWindowWidth = DefaultCollapsedWindowWidth` (`UI/RecordingsTableUI.cs:48`,
`:63` = `1205f + ColW_Rewind(60) + ColW_ReFly(90)` = 1355f), `MinWindowHeight = 150f`
(`:49`), first-open rect = right of the main window, `DefaultCollapsedWindowWidth` wide, main
height (flight) or 2x main height (KSC) tall (`UI/RecordingsTableUI.cs:631-637`). The census
rect [0,8,1280,700] was written by the `UiAction op=rect` seam, not by the default.
Tab-body pins: `ColHeaderHeight = 32f` on every header cell (`:346`),
`CompositionRowMinHeight = 22f` MinHeight on every body row (`:351`),
`MissionHeaderRightBlockWidth` (`:326`) = the 7 data cells + 6x4px margins +
`MissionHeaderPeriodSlack = 48f` (`:322`) + one `ColW_HeaderButton = 70f` + 4f for the Log
button.

---

## S2. Per-mission header bar (section / row)

**(a)** Mission header bar: one dark "bubble" spanning the full row width, carrying two lines
(title row + summary line).

**(b)** `MissionsWindowUI.DrawMissionHeader`, `UI/MissionsWindowUI.cs:2447`; second line
`DrawMissionSummaryLine`, `:2644`.

**(c)** Both scenes. No gate on the bar itself; individual controls are gated (see (e)).

**(d) STRUCTURE**

Title row, left to right:

| Cell | Content | Width | file:line |
|---|---|---|---|
| enable slot | blank | `ColW_Enable` | `:2461` |
| index | per-tree index number, shared by clones, shown in BOTH modes | `ColW_Index` | `:2467-2472` |
| title | bold transparent button; double-click enters inline rename | Expand | `:2477` -> `:2799` |
| right block | fixed `MissionHeaderRightBlockWidth`: `Log`, `Clone`, `Delete`, `Warp to...`, `Loop` + toggle, period cell, FlexibleSpace, `Watch`, `Rewind`/`Forward`, Archive checkbox | `:2481` | |

Summary line (second line in the same bubble, text-only, no controls): narrative
`"<body path> . <duration> . <crew> . <terminal> . Loops ~P . Next launch T- ..."` built by
`MissionPresentation.BuildNarrativeSummaryLine` (`MissionPresentation.cs:377`), tooltip =
`BuildSummaryDetailTooltip` (`MissionPresentation.cs:429`) carrying full span dates, vessel
count and the FULL crew roster on ONE line (`DetailFragmentSeparator = " - "`, never `\n`,
because the help strip is one line: `MissionPresentation.cs:33`). Draw at `:2692-2703`. It
draws at all only when `HasSpan || VesselCount>0 || CrewCount>0 || TerminalWord || BodyPath`
(`:2654-2658`) - deliberately not on the countdown, so the control count cannot move
mid-frame. Census: `label 'Kerbin . 2m 27s'` tip `'Y1, D01, 00:01 -> Y1, D01, 00:03 - 1
vessel'` and `'Kerbin . 48m 51s . Jebediah Kerman . Landed'` tip `'... - Crew: Jebediah
Kerman'`.

**(e) Action controls**

| Control | Backend | Disabled / hidden condition | file:line |
|---|---|---|---|
| Title (single click) | arms double-click timer (`DoubleClickThreshold = 0.3f`, `:380`) | - | `:2833-2857` |
| Title (double click) | opens inline `TextField`; Enter -> `CommitMissionRename` (`:3859`), Escape cancels, click outside the captured rect commits (`:759-765`) | - | `:2806-2830` |
| rename commit | original mission -> `MissionGroupLink.RenameMissionGroup` (renames root group + `/ Debris` + `/ Crew` + `Mission.Name` atomically, `MissionGroupLink.cs:57`); a CLONE -> `MissionStore.RenameMission` alone | a group-name collision refuses the whole rename and warn-logs `Mission rename rejected` | `:3870-3890` |
| `Log` | `parentUI.OpenStructureWindowForMission(mission.TreeId, mission.Name)` (`ParsekUI.cs:1057`) -> S11 | never disabled | `:2488-2494` |
| `Clone` | `MissionStore.Clone(mission)` | HIDDEN in Basic (`loopAuthoring`, `:2505-2509`) - its width is absorbed by the later FlexibleSpace | `:2507` |
| `Delete` | `MissionStore.Delete(mission)` | `GUI.enabled = MissionStore.CanDelete(mission)`; disabled reason `"A flight always keeps its first mission"` (`MissionDeleteDisabledReason`, `:2908`) carried by `DisabledHoverEcho.CarryLastControl`. KEPT in Basic | `:2510-2516` |
| `Warp to...` | confirm dialog then in-place forward jump -> S8 | `GUI.enabled = warpScene && ShouldEnableWarpToWindow(looping, unitBuilt, nextRelaunchUT, now)` (`:3055`: requires `nextRelaunchUT > now + 1.0`, finite). Four ordered reasons from `MissionWarpToDisabledReason` (`:2930`). NOT gated by Basic | `:2953-2987` |
| `Loop` label + toggle | `CommitMissionLoopToggle` (`:2756`) -> `MissionStore.SetLoopEnabled` + `AnnounceClearedLoops` (S9) + `PostDoubleClockAdvisoryIfAny` (S10) | HIDDEN in Basic. Greyed (`GUI.enabled=false`) when `RouteTreeGuard.RouteBindingFor(treeId)` is true; the commit ALSO drops a turn-on on a route-bound tree (belt and suspenders, `:2762-2769`). Disabled reason `RecordingsTableUI.LoopToggleDisabledReason(routeName)` -> `"Looped by route: <name>"` (`UI/RecordingsTableUI.cs:1015`) | `:2543-2562` |
| `Looped by route` label | none (status only) | drawn iff `missionRouteBound`; explicitly NOT part of the Basic gate | `:2564-2575` |
| period cell | S2b below | HIDDEN in Basic | `:2586-2587` |
| `Watch` / `W*` | `flight.EnterWatchMode(target)` / `flight.ExitWatchMode()`; target from `ResolveMissionWatchTarget` (`:3916`, trimmed loop members with an active ghost on the same body in visual range) | `GUI.enabled=false` outside flight ("Watching only works while you are flying") or when nothing of this mission is flying ("Nothing from this mission is flying right now") - `MissionWatchDisabledReason`, `:2918`. NOT gated by Basic | `:2860-2906` |
| `Rewind` / `Forward` | `RecordingsTableUI.DrawMissionRewindForwardButton` (`UI/RecordingsTableUI.cs:3363`) over the mission ROOT recording index (`ResolveMissionRootRecordingIndex`, `:3956`) | label flips to `Forward` when the launch is still in the future; each side greys on `RecordingStore.CanRewind` / `CanFastForward` with the store's reason as tooltip; blank cell when no root resolves | `:2607-2610` |
| Archive checkbox | writes `mission.Archived`, logs `Mission '<n>' archived=<b>` | never disabled; tooltip `ArchiveCheckboxTooltip` | `:2616-2625` |

### S2b. Loop-period cell (inside the header bar)

`DrawMissionLoopPeriodCell`, `UI/MissionsWindowUI.cs:3995`. Content-sized (not fixed width),
`ColW_Period = 90f` only sets the editable value+unit split (`valueW = ColW_Period - 40 - 4`,
`:3997-3998`). Four states:

| State | Rendering | Tooltip | file:line |
|---|---|---|---|
| Phase-locked + constrained, or re-aim | read-only amber label `"~6.4d (Mun window)"` / `"~2.1y (Duna transfer)"` / `"~13d-1mo (Mun window, varies)"` | `PeriodTooltipLocked` | `:4013-4047`; builders `:3689`, `:3701`, `:3722` |
| Auto unit | non-editable TextField showing the global value in the global display unit + unit suffix | `PeriodTooltipAuto` | `:4076-4096` |
| Manual, not focused | TextField with the EFFECTIVE (overlap-capped) cadence, tinted `LoopPeriodClampColor` when raised; focus starts an edit | `PeriodTooltipClamped` when raised, else none | `:4097-4127` |
| Manual, editing | TextField over `loopPeriodEditText`; Enter commits | as above | `:4128-4149` |

Unit button (`40f`) cycles `RecordingsTableUI.CycleRecordingUnit` and carries the cell's state
tooltip because a `TextField` takes no `GUIContent` (`:4151-4171`). Commit path:
`CommitMissionLoopPeriodEdit` (`:4180`) -> `CommitMissionLoopPeriod` (`:4208`): Auto never
commits, unparseable text is ignored, negatives rejected with a Warn, below
`LoopTiming.MinCycleDuration` clamped with an Info. Click-away commit is window-level at
`:787-791`. In Basic an open edit is DROPPED uncommitted (`:772-784`) rather than committed
against a stale rect - that is what keeps the mode visibility-only.

**(f) STATE VARIANTS**

| Variant | Census label |
|---|---|
| Loop off, manual `sec`, period "10", Delete enabled, Watch drawn, Rewind drawn | `ksc-missions-missions-advanced` (all 18 missions) and `flight-missions-missions-advanced` |
| Basic: Log / Delete / Warp to... / Watch / Rewind / Archive only | `ksc-missions-basic` |
| Loop ON + phase-locked read-only period label | NONE - needs a mission looping over a Mun/Minmus recording; cheapest is `mun-orbit-recorded` or `mun-landing-recorded` plus a click on Loop. NO seam op toggles it |
| `Looped by route` label | NONE - needs a route-bound tree (`depot-route-recorded` / `interbody-route-recorded` / `rover-route-recorded`) with the route active |
| `Forward` instead of `Rewind` | NONE - needs a mission whose launch is ahead of now |
| `W*` (watching) | NONE - flight scene with a ghost of the mission in visual range |
| Delete greyed | NONE in the census dumps (the gui tree records no enabled flag; every mission here is an original, so all 18 ARE greyed - the dump cannot show it) |
| Inline rename field | NONE - double-click only |
| Archive ticked | NONE |

**(g) Sizing** - `ColW_HeaderButton = 70f` for Log / Clone / Delete / Warp to... / Watch /
Rewind (`:314`). Header bubble is a `BeginVertical(missionHeaderRowStyle)` wrapping the two
lines (`:2454`); `CaptureRevealAnchor` measures exactly that group's rect (`:671`). Census
rects confirm the pins: Log [394,115,70,21], Clone [468,...], Delete [542,...], Warp to...
[616,...], Loop label [690,115,28,18], toggle [722,115,15,18], period group [741,115,90,21]
split textfield 46 + button 40, Watch [1022,...,70,21], Rewind [1096,...], Archive toggle
[1202,115,15,18].

---

## S3. Per-vessel row (the T2.2 flattened ROW MODEL)

**(a)** One table row per physical vessel or EVA kerbal.

**(b)** `MissionsWindowUI.DrawVesselRow`, `UI/MissionsWindowUI.cs:1413`. Row model built by
`MissionVesselRowBuilder.Build`, `MissionVesselRows.cs:59` (cached per frame by
`GetVesselRows`, `UI/MissionsWindowUI.cs:1235`).

**(c)** Both scenes; drawn unconditionally under each mission header. Only its checkbox and
its expandability are gated.

**(d) ROW MODEL - what exactly is one row**

- One row = one run of composition nodes sharing an `OwnerHeadId` (the through-line head
  recording id). `MissionVesselRows.cs:74-125` walks the same-owner survivor chain; a
  same-owner SECOND child is a contract violation and warn-logs rather than silently dropping
  intervals (`MissionVesselRows.cs:104-112`).
- Depth = SEPARATION LINEAGE ONLY, never time: a different-`OwnerHeadId` selectable child
  becomes a child ROW (`MissionVesselRows.cs:118-123`), sorted by separation UT with an
  ordinal tiebreak (`:135-140`). Indent comes from `RecordingsTableUI.SelfConnectorIndent(depth)`
  plus a `TreeConnector(isLast)` glyph (`UI/MissionsWindowUI.cs:1471-1474`). Mission rows start
  at depth 1 (`UI/MissionsWindowUI.cs:932`).
- Roster atoms are NOT rows here - they are skipped by the builder (`MissionVesselRows.cs:99-101`);
  the crew are named on the header's narrative line and in the row tooltip.
- Fields: `OwnerHeadId`, `VesselName`, `IsPerson`, `StartUT`, `EndUT`, `StartEvent`,
  `EndEvent`, `EventPhrase`, `Intervals`, `Children` (`MissionVesselRows.cs:21-37`).
- `EventPhrase` is the inline chain `"Launch -> Decoupled (Booster) -> Docked (Munport
  Station) -> Landed"`, built by `BuildEventPhrase` (`MissionVesselRows.cs:157`); a separation
  boundary names the piece that left (`ResolveChildAtBoundary`, `:208`), a Dock/Board boundary
  names the same-tree partner through the resolver (`NameEventPiece`, `:186`).

Cells of one row:

| Cell | Content | file:line |
|---|---|---|
| enable slot | blank `ColW_Enable` | `:1436` |
| index slot | per-vessel include checkbox (Advanced) or a blank same-width label (Basic) | `:1442-1467` |
| name (expanding) | `connector + caret + VesselName + " (partial)" + "   " + EventPhrase`; drawn through `DrawWideRowCell` (`:217`) which pins an explicit wrapped height so a wrapping label is not a line short | `:1469-1497` |
| `Next launch` | countdown on the launch (first) row only, blank elsewhere | `:1499-1512` |
| `Start time` | `KSPUtil.PrintDateCompact(row.StartUT, true)` | `:1514` |
| `Start event` | `DrawStartEventCell` (`:1937`) - dock-partner naming, see below | `:1516` |
| `End event` | `row.EndEvent` | `:1517` |
| `End time` | `PrintDateCompact(row.EndUT)` | `:1518` |
| `Re-Fly` | `RecordingsTableUI.DrawReFlyColumnCell` for the head recording, else a blank `ColW_ReFly` | `:1523-1533` |
| trailing | margin-0 `ColW_Archive` spacer (a `bodyCellLabel` here would drift every column 4px) | `:1536-1538` |

Row tooltip = the full event phrase plus `" - Aboard: <last interval composition>"`, joined
with `" - "` and never `\n` (the help strip is one line) - `:1487-1494`. Census confirms:
`'Launch -> Destroyed - Aboard: probe x1'`.

`Start event` cell logic (`:1937-2028`): a non-dock word renders plain; a dock word first tries
`MissionPresentation.ResolveSameTreeDockPartnerVesselName` (`MissionPresentation.cs:599`,
two-parent merge walk) -> `"Docked with <partner>"` non-wrapping with the full text as tooltip;
failing that the dock-event GRAPH fallback `GetIntervalDockPartnerText` (`:1127`) supplies
`"with CD (mission 'CD Freighter')"`, inlined only if it measures inside `ColW_StartEvent`,
otherwise tooltip-only; failing both, the bare word plus a 30s-rate-limited Verbose.

**(e) Action controls**

| Control | Backend | Condition | file:line |
|---|---|---|---|
| include checkbox | `MissionVesselRowBuilder.ApplyVesselInclusion` over the vessel's OWN interval keys (never a child's), then `StampSelectionEdit` (`:264`) and an Info log | HIDDEN in Basic (`loopAuthoring`, `:1419`, `:1442`). Direction rule: a click on a FULL vessel excludes all; a click on None OR Partial INCLUDES all - the destructive direction is reserved for the unambiguous state (`:1456`) | `:1442-1466` |
| name cell as caret button | toggles `expandedVessels` (S4) | `expandable = loopAuthoring && row.Intervals.Count > 1` (`:1427`) - so Basic never expands, and expandability never depends on inclusion state (control count stability) | `:1489-1497` |
| Re-Fly Fly / Seal | `RecordingsTableUI.DrawReFlyColumnCell` (`UI/RecordingsTableUI.cs:3411`) | only for unfinished-flight recordings; otherwise blank | `:1523-1528` |

Inclusion tri-state comes from `MissionVesselRowBuilder.ClassifyInclusion`
(`MissionVesselRows.cs:225`): All / Partial / None over the vessel's own keys, children never
consulted (no cascade). `Partial` adds the literal suffix `" (partial)"` to the name
(`UI/MissionsWindowUI.cs:1477`); `None` dims the row with `DimColor` (`:1468`), and the colour
is restored before the Re-Fly cell so Fly/Seal is never dimmed (`:1520`).

**(f) STATE VARIANTS**

| Variant | Census label |
|---|---|
| Leaf vessel row, checkbox, no caret | `ksc-missions-missions-advanced` e.g. `'\- R.0-S.0   Launch'` |
| Multi-interval vessel row WITH caret (collapsed) | same dump: `'\- > L5-B4-U1-C1   Launch -> Boarded (Jebediah Kerman) -> Boarded (Jebediah Kerman) -> Landed'` |
| EVA child rows at depth 2 (lineage indent, `|-` / `\-`) | same dump: `'|- Jebediah Kerman   EVA -> Boarded'` / `'|- Lars Kerman   EVA -> Boarded'` |
| Dock-partner naming INSIDE the event phrase | same dump (the `Boarded (Jebediah Kerman)` rows) |
| Basic row (blank index cell, plain label, no caret) | `ksc-missions-basic` |
| `(partial)` suffix / dimmed excluded row | NONE - needs a per-interval exclusion authored by a click |
| `Docked with <partner>` in the Start event CELL | NONE in these dumps (every `Start event` cell reads a bare word) - needs a same-tree dock, e.g. `bdock-recorded` / `bdock-station-craft` |
| Fly / Seal in the Re-Fly column | NONE - needs an unfinished-flight recording (`refly-a-recorded`) |
| Amber `Next launch` tint | NONE |

**(g) Sizing** - `GUILayout.MinHeight(CompositionRowMinHeight = 22f)` per row (`:1434`); the
name cell height is computed by `compositionCellLabel.CalcHeight` against the cached measured
width per depth (`wideCellWidthCache`, `:213`, `:217-247`). Census shows a wrapped two-line
vessel row at height 35 ([68,1796,448,35]) versus the normal 22.

---

## S4. Expanded per-vessel interval detail rows

**(a)** Flat sub-rows under an expanded vessel row - one per structural interval.

**(b)** Drawn from `DrawVesselRow` at `UI/MissionsWindowUI.cs:1547-1559`, each through
`DrawCompositionRow` (`:1611`).

**(c)** Both scenes. Advanced ONLY: the detail exists to host the per-interval checkboxes that
Basic hides (`expandable = loopAuthoring && Intervals.Count > 1`, `:1427`).

**(d)** Same column set as S3, one depth deeper and equally indented. The name cell uses
`MissionPresentation.BuildIntervalRowLabel` (`MissionPresentation.cs:479`): the FIRST interval
keeps `"Vessel (composition)"`, a later interval that starts at a separation leads with the
delta - `"after undock: Kerbal X Lander left - (pod x1, crew x2)"` - resolved via
`ResolvePeeledSiblingVesselName` (`MissionPresentation.cs:525`). Roster atoms leave the four
time/event columns blank (`:1706-1712`) and carry no checkbox.

**(e)**

| Control | Backend | Condition | file:line |
|---|---|---|---|
| per-interval include checkbox | adds/removes `node.HeadLegId` in `mission.ExcludedIntervalKeys`, stamps `SelectionSchemaGeneration`, Info-logs | drawn iff `selectable && ShowsLoopAuthoringControls(...)`; else a single blank same-width cell so columns stay aligned | `:1632-1656` |
| name cell as caret | toggles `collapsedLegs` keyed `missionId:headId` | only when the node has children | `:1682-1687` |
| Re-Fly cell | `DrawReFlyColumnCell` for `node.HeadLegId` | non-atom rows that resolve to a committed recording | `:1721-1730` |

No cascade is the contract: excluding the launch interval leaves the post-decouple survivor
checked (`:1573-1577`).

**(f)** Variants: no census picture at all - every vessel row in the dumps is collapsed
(`>`). Cheapest fixture: any multi-interval mission already in the host (the two `>` rows in
`ksc-missions-missions-advanced`) plus ONE click on the caret. NEW SEAM OP REQUIRED to
automate.

**(g)** Same 22f MinHeight; indent `SelfConnectorIndent(depth+1)`.

---

## S5. Chapter group header row

**(a)** A group header row above the vessel row that owns a chapter's anchor interval.

**(b)** `TryDrawChapterHeaderRowForVesselRow` (`UI/MissionsWindowUI.cs:1833`) ->
`TryDrawChapterHeaderRow` (`:1850`) -> `DrawChapterHeaderRow` (`:1865`). Chapters built by
`MissionChapters.CollectChapterRoots` via `GetChapters` (`:1769`), armed per mission at `:922`
and disarmed in a `finally` at `:938`.

**(c)** Both scenes. Ungated by complexity - but it draws a Toggle unconditionally, so it is
one of the few interval-writing controls Basic does NOT hide (see "surprising" note in the
report).

**(d)** Cells: blank enable slot, a tri-state Toggle in the index slot, then the chapter title
expanding, then `DrawBlankDigestCells()` for every data column (`:2349`). No tree connector and
no span columns by design (a chapter's span is the union of the rows below). A `Mixed` state
prefixes the literal marker `"[~] "` to the title (`:1922`); `AllExcluded` dims the row.

**(e)** The toggle: checked means "some of this chapter is included"; one click drops the whole
chapter, the next brings all of it back; it adds/removes every key in `chapter.IntervalKeys`
and stamps `SelectionSchemaGeneration` (`:1885-1912`). The tri-state is read ONCE before the
toggle so the control set cannot change between Layout and Repaint (`:1872-1879`). A chapter
that owns no selectable key, or whose anchor is already headed by another chapter, is DROPPED
with a Verbose line rather than drawn inert (`:1795-1803`).

**(f)** No census picture - the host save produced no chapter header in any of the 18
missions. Chapters come from the dock-event graph, so a dock-bearing fixture is needed:
`bdock-recorded` or `bdock-forge-base`. Steps: `LoadGame` that save -> `UiAction op=open
window=missions` -> `UiAction op=rect` -> `CaptureScreenshot` + `DumpGuiTree`.

**(g)** 22f MinHeight, `ColW_Index` toggle with `ExpandHeight(true)`.

---

## S6. "Docked partner" row and the foreign partner-journey staircase

**(a)** One affordance row per derived cross-tree dock link, plus - when included - the foreign
tree's journey intervals as a child staircase.

**(b)** `DrawForeignDockLinkRows`, `UI/MissionsWindowUI.cs:2032` (called at `:944`);
sub-tree renderers `DrawMaximalJourneyNodes` (`:2182`) and `DrawForeignJourneyNode` (`:2201`),
both of which render through `DrawCompositionNode` (`:1578`) / `DrawCompositionRow` (`:1611`).

**(c)** Both scenes. The ROW is drawn in both modes (it is mission history); only the include
CHECKBOX is Basic-gated (`:2043`, `:2045-2050`).

**(d)** Row cells: blank enable slot; include toggle or blank; name cell
`TreeConnector + "Docked partner: <ForeignVesselName>" + FormatLinkLoiterGap(...)` through
`DrawWideRowCell` at depth 1 (`:2100-2111`); blank `Next launch`; dock date in `Start time`;
`"Docked"` or `"Boarded"` in `Start event`; blank `End event` / `End time` / `Re-Fly`; margin-0
Archive spacer. The loiter phrase is `" (loiter, <duration> - not recorded)"` and fires only
above `MissionEventDigest.GapThresholdSeconds`, sharing threshold and wording with the digest's
own gap row so the two surfaces cannot disagree (`FormatLinkLoiterGap`, `:2165`).

Below an INCLUDED link the journey renders as MAXIMAL journey nodes of the FOREIGN tree, with
label derivations resolved against the foreign tree's own structure/view via a separate
`RowDeriveContext` (`:2128-2141`). `DrawForeignJourneyNode` recurses only into children that
are journey nodes or roster atoms, so the foreign vessel's pre-dock / post-departure intervals
never appear (`:2201-2255`). This staircase is the one remaining caller of the
`DrawCompositionNode` recursion for non-detail rows (`:1571-1577`).

**(e)**

| Control | Backend | Condition | file:line |
|---|---|---|---|
| include toggle | writes `mission.IncludedForeignDockLinkIds`; on an ALREADY-LOOPING mission it also snapshots the other looping missions, calls `MissionStore.ClearLoopsConflictingWith(..., "PartnerJourneyInclude")` and announces the moved loop (S9) | HIDDEN in Basic | `:2052-2083` |
| child interval checkboxes | the same `ExcludedIntervalKeys` binding as S4 (keys are globally unique) | Basic-gated like S4 | `:1632` |

**(f)** No census picture - the host save produced zero cross-tree links (no `Docked partner`
node anywhere in the 922-node dump). Fixture: two trees that docked, i.e. `bdock-recorded` (or
`bdock-station-craft` + `bdock-station-pad`). The row draws automatically once links derive;
the EXPANDED journey additionally needs a click on the toggle (NEW SEAM OP REQUIRED).

**(g)** 22f MinHeight; indent fixed at `SelfConnectorIndent(1)` for the link row, journey nodes
start at depth 2 (`:2135-2137`).

---

## S7. Mission event digest ("Events (N)")

**(a)** A collapsed-by-default foldout row plus, when expanded, one row per event.

**(b)** `DrawEventDigestRows`, `UI/MissionsWindowUI.cs:2256` (called at `:948`). Rows from
`MissionEventDigest.Build` via `GetEventDigest` (`:2367`), cached on the dock-graph instance
plus a fold over the included-link set (`ForeignLinkFingerprint`, `:2397`).

**(c)** Both scenes, both modes - no complexity gate.

**(d)** Foldout row: blank enable + index cells, then a full-width button
`"<caret>Events (<count>)"` in the name column, then `DrawBlankDigestCells()`. Event rows:
indent at depth 2, `TreeConnector + PrintDateCompact(row.UT) + " - " +
MissionEventDigest.FormatRowText(row)` in the name column, all data columns blank, and a
`Go to` button occupying the Re-Fly slot when `row.GoToRecordingId` is set.

**(e)**

| Control | Backend | Condition | file:line |
|---|---|---|---|
| `Events (N)` foldout | writes `digestExpanded[mission.Id]`; DELIBERATELY dictionary-only, so the flip takes effect NEXT frame and a Repaint cannot disagree with its Layout | drawn only when the digest has rows | `:2272-2292` |
| `Go to` | `parentUI.GetRecordingsTableUI().ShowMissionForRecording(row.GoToRecordingId)` (`UI/RecordingsTableUI.cs:465`) -> S12 | drawn only when `GoToRecordingId` is non-empty, else a blank `ColW_ReFly` cell | `:2320-2339` |

`Go to` tooltip: `"Go to the other side of this event"` + `": <PartnerText>"`
(`BuildDigestGoToTooltip`, `:2362`).

**(f)**

| Variant | Census label |
|---|---|
| Collapsed foldout, counts 1 / 3 / 4 / 5 / 6 / 7 / 9 | `ksc-missions-missions-advanced`, `ksc-missions-basic`, `flight-missions-missions-advanced` (18 foldouts each) |
| EXPANDED digest with event rows | NONE |
| `Go to` button | NONE |

Cheapest fixture for both: the SAME census host - one click on any `> Events (N)` button.
No seam op clicks buttons, so: NEW SEAM OP REQUIRED (a `UiAction op=click` or a
missions-specific `op=expand`) to photograph it unattended.

**(g)** 22f MinHeight per row; `Go to` pinned to `ColW_ReFly = 90f`.

---

## S8. "Confirm: Warp to Launch" dialog

**(a)** Stock `PopupDialog` / `MultiOptionDialog` (modal confirmation).

**(b)** `MissionsWindowUI.ShowMissionWarpToWindowConfirmation`, `UI/MissionsWindowUI.cs:2993`.
Dialog name `"ParsekMissionWarpToWindowConfirm"` (`:3010`).

**(c)** FLIGHT or SPACECENTER (the button's own scene gate, `:2955`). Shown only on a click of
an enabled `Warp to...`.

**(d)** Centred at (0.5, 0.5); title `"Confirm: Warp to Launch"`; body
`"Fast-forward to just before \"<mission>\" next launch at <date>?\n\nTime will advance by
<duration>."`; two buttons, `Warp` and `Cancel`.

**(e)** `Warp` -> in flight `ParsekFlight.FastForwardToEventUT(relaunchUT, label)`; at the
Space Center a direct `TimeJumpManager.ExecuteForwardJump(ApplyJumpLead(...))` guarded by
`TimeJumpManager.IsValidJump` (an invalid jump warn-logs and returns without moving the clock),
followed by a `ParsekLog.ScreenMessage` (`:3019-3045`). `Cancel` logs
`"Warp to next window cancelled"` (`:3046-3047`).

**(f)** No census picture - the dialog needs a click on an ENABLED `Warp to...`, which itself
needs a looping mission with a built loop unit. Fixture: `mun-orbit-recorded` (or the census
host) + Loop on + a click. NEW SEAM OP REQUIRED.

**(g)** Stock dialog sizing (`HighLogic.UISkin`); no Parsek pins.

---

## S9. Screen message: "Loop moved to '<mission>'"

**(a)** One-shot screen message (an EVENT that changed something - a loop actually moved).

**(b)** `AnnounceClearedLoops`, `UI/MissionsWindowUI.cs:2727`; text from
`MissionPresentation.BuildLoopMovedScreenMessage` (`MissionPresentation.cs:688`), posted via
`ParsekLog.ScreenMessage(message, 5f)` at `:2746` (which prefixes `"[Parsek] "`).

**(c)** Any scene the tab draws in. Two triggers: enabling a mission's Loop toggle
(`CommitMissionLoopToggle`, `:2782-2784`) and including a partner-journey link on an already
looping mission (`:2078-2084`).

**(d)** Text: `Loop moved to '<winner>' - one loop per recording tree ('A', 'B' unlooped)`.
Returns null (and nothing is posted) when nothing was actually cleared.

**(e)** No controls.

**(f)** No census picture (loop toggles are player clicks). Fixture: the census host, two
missions over one tree (Clone), Loop one then the other.

**(g)** 5 s duration.

---

## S10. Screen message: double-clock advisory

**(a)** One-shot screen message, advisory only - nothing is switched off.

**(b)** `PostDoubleClockAdvisoryIfAny`, `UI/MissionsWindowUI.cs:1076`; text from
`MissionStore.TryDescribeDoubleClockAdvisory`; posted with
`ScreenMessages.PostScreenMessage("[Parsek] " + advisory, 8f, ScreenMessageStyle.UPPER_LEFT)`
at `:1084`. This is the one site in these files that posts directly rather than through
`ParsekLog.ScreenMessage`; the post is wrapped in try/catch so a `ScreenMessages` failure
cannot take the draw pass down (`:1087-1092`).

**(c)** Loop-ENABLE only, and only after the enable succeeded (`:2788-2789`). Deliberately NOT
fired by the link-include path, because that path clears the foreign loop first
(`:1071-1074`).

**(d)** Whatever `MissionStore.TryDescribeDoubleClockAdvisory` returns; empty means nothing is
posted.

**(e)** No controls.

**(f)** No census picture. Fixture: two dock-connected trees (`bdock-recorded`) with both
missions looping.

**(g)** 8 s, UPPER_LEFT.

---

## S11. "Parsek - Structure" window (the mission / route Log)

**(a)** Its own window.

**(b)** `StructureListWindowUI`, `UI/StructureListWindowUI.cs:18`;
`ClickThruBlocker.GUILayoutWindow` at `:174` with title `"Parsek - " + title` (`:178`); body
`DrawWindow` at `:211`.

**(c)** Drawn wherever `ParsekUI` calls `DrawStructureWindowIfOpen` (`ParsekUI.cs:1050`) -
FLIGHT and SPACECENTER. No complexity gate. Opened by `OpenForMission` (`:83`, from the
Missions tab `Log` button, `UI/MissionsWindowUI.cs:2492`) or `OpenForRoute` (`:94`, from the
Logistics window, `UI/LogisticsWindowUI.cs:2033`). One reusable instance; reopening retargets
and rebuilds (`Rebuild`, `:107`).

**(d) STRUCTURE**

Empty state (`:223-232`): a single label - `"Nothing to show."` for a mission target,
`"Nothing to show (source recording unavailable)."` for a route - plus a `Close` button, then
an early return (NO header row, NO scroll view, NO resize handle).

Populated: header row, scroll view (vertical bar FORCED so the header's reserved gutter is
always taken, `:258`), rows, a full-width `Close`, then the resize handle.

| # | Header | Width const | Value | file:line |
|---|---|---|---|---|
| 1 | `#` | `ColW_Index = 28f` | 1-based step number | `:62`, `:243`, `:263` |
| 2 | `Time` | `ColW_Time = 110f` | `KSPUtil.PrintDateCompact`, or `"-"` for the route Origin pseudo-step (NaN UT) | `:63`, `:244`, `:288-293` |
| 3 | `Event` | expanding (`ColW_Event = 0f`) | `step.Label` | `:64`, `:245`, `:265` |
| 4 | `Status` | `ColW_Status = 95f` | vessel situation | `:65`, `:246`, `:266` |
| 5 | `Location` | `ColW_Location = 185f` | "SOI/body, biome" | `:66`, `:247`, `:267` |
| 6 | `Vessel` | `ColW_Vessel = 140f` | vessel name | `:67`, `:248`, `:268` |
| 7 | (scrollbar gutter) | 16f fallback | reserved | `:237-249` |

No sorting, no filters, no per-row controls - the window is read-only over already-recorded
data (`:14-16`).

**(e)** Exactly one action control: `Close` -> `Close()` (`:281`), which lowers `isOpen` and
releases the input lock. Nothing is conditionally disabled. Conditionally HIDDEN: the header
row, the scroll view, the rows and the resize handle, all suppressed by the
`steps.Count == 0` early return (`:223-232`).

**(f) STATE VARIANTS - what populates it and from which row**

The census caught ONLY the empty chrome: `ksc-structure-advanced`, 3 nodes -
`window 'Parsek - Structure' [270,8,1000,420]`, `label 'Nothing to show.'`, `button 'Close'`.
That is the `mode == TargetMode.Mission` empty text, because the seam's `op=open
window=structure` raises `IsOpen` without ever calling `OpenForMission` / `OpenForRoute`, so
`targetId` stays null and `Rebuild` never ran. The census spec says exactly this and files it
as a follow-up rather than faking it (`harness/scenarios/GUI-1-census-ksc.toml` - and see
`Source/Parsek/TestCommands/TestCommandUiAction.cs:414-418`, and the "every raise site is a
GUILayout.Button handler" argument at `:555-562`).

What populates it:
- MISSION mode - the `Log` button on a mission header row (`UI/MissionsWindowUI.cs:2488-2494`)
  passes `mission.TreeId` and `mission.Name`; `Rebuild` runs `MissionStructureBuilder.Build(tree)`
  then `MissionStructureListBuilder.Build(tree, structure)` (`:110-118`). Any mission row in the
  census host would fill it.
- ROUTE mode - the Logistics window's `Log (Route)` / `Log (Mission)` buttons
  (`UI/LogisticsWindowUI.cs:2033`); `Rebuild` resolves `RouteStore.TryGetRoute` and runs
  `RouteStructureListBuilder.Build(route, FindCommittedRecording, VesselSpawner.TryResolveBiome)`
  (`:119-124`). The empty-state route wording is only reachable when the route resolves but the
  builder produces no steps.

| Variant | Census label |
|---|---|
| Empty chrome, mission wording | `ksc-structure-advanced` |
| Populated mission step list | NONE - needs a click on a Missions `Log` button |
| Populated route step list | NONE - needs a route fixture (`depot-route-recorded`) and a Logistics `Log (Route)` click |
| Empty-state ROUTE wording | NONE |

**(g) Sizing** - `MinWindowWidth = 420f`, `MinWindowHeight = 160f` (`:69-70`). First-open
default: `new Rect(mainWindowRect.x + mainWindowRect.width + 10, mainWindowRect.y, 820, 320)`,
seeded only when `windowRect.width < 1f`, so a seam-written rect suppresses the seed rather
than being overwritten (`:159-163`, contract at `:26-34`). The census rect [270,8,1000,420]
came from `UiAction op=rect`. Input lock id `"Parsek_StructureListWindow"` (`:46`), taken while
the mouse is inside the rect (`:190-201`).

---

## S12. Cross-link reveal from the Timeline GoTo (behavior, no surface of its own)

**(a)** Navigation behavior: opens this window on the Missions tab and scrolls the list to the
mission that owns a recording. Draws nothing of its own.

**(b)** `MissionsWindowUI.RevealMissionForRecording`, `UI/MissionsWindowUI.cs:587`; anchor
capture `CaptureRevealAnchor` (`:671`, called at `:904`); apply at `:836-858`; abandon paths
`DropPendingReveal` (`:968`, called from the empty-list branch at `:824` and after a full
Repaint at `:955-956`).

**(c)** Entered from `RecordingsTableUI.ShowMissionForRecording` (`UI/RecordingsTableUI.cs:465`),
whose callers are the two Timeline GoTo buttons (`UI/TimelineWindowUI.cs:1415`, `:1457`), the
Missions digest `Go to` (`UI/MissionsWindowUI.cs:2337`) and the Kerbals window's cross-link
(`UI/KerbalsWindowUI.cs:648` comment). Gate: the GoTo button reuses `UiSurface.TabMissions`, so
it works in BOTH modes (`UI/UiComplexityMode.cs:35-41`).

**(d)** Two-frame handshake: resolution is recording -> `Recording.TreeId` ->
`MissionStore.FindOriginalMission` (`:634`), after an idempotent
`MissionStore.EnsureDefaultsForTrees` seed (`:628`); resolution is ERS-routed via
`EffectiveState.ComputeERS()` (`:598`), so a superseded recording cannot be navigated to. The
scroll target is the DIFFERENCE between the target header's y and the FIRST header's y
(`:693`), measured on a Repaint pass whose own Layout ran this frame (`:679-680`) and whose
header rect is non-zero-height (`:688-689`); it is applied at the top of a later pass BEFORE
the scroll view opens (`:836-846`) and discarded when older than `RevealScrollMaxAgeFrames = 4`
(`:58`, `:841-848`).

**(e)** Archive interaction: when the target is archived and the filter is on, the reveal QUEUES
`pendingClearArchiveFilter` (`:653-660`) and the DRAW consumes it on a LAYOUT pass only
(`:806-813`, Info log `"Cross-link: cleared the Archive filter so the revealed mission is
listed"`). It never touches `mission.Archived`. Every failure warns and lands the player on an
unscrolled tab: recording not in the effective set (`:614`), recording with no tree (`:621`),
tree with no mission (`:639`), target not drawn (`:971`).

**(f)** No census picture - the census never drives a GoTo. Observable state for tests is
`PendingRevealMissionIdForTesting` (`:71`), `PendingClearArchiveFilterForTesting` (`:66`) and
`LastRevealScrollOffsetForTesting` (`:79`); the in-game coverage is
`InGameTests/MissionRevealInGameTests.cs:197`.

**(g)** No sizing of its own.

---

# Section 04 - Timeline window

Scope: `UI/TimelineWindowUI.cs` (1809 lines), `UI/TimeRangeFilter.cs`, the Timeline-driven parts
of `WarpToTimeController.cs`, and `RewindPointDiskUsage.cs`. All file:line paths are relative to
`Source/Parsek`.

Two corrections to the brief, both derived from grep and stated up front because they change the
shape of this section:

- There is **no disk-usage footer in the Timeline window**. `RewindPointDiskUsage` has exactly
  three consumers repo-wide, and all three are in the SETTINGS window
  (`UI/SettingsWindowUI.cs:682`, `:683`, `:685`). `TimelineWindowUI.cs` contains zero references
  to it (grep for `RewindPointDiskUsage` over `Source/Parsek` returns only
  `RewindPointDiskUsage.cs:33` and the three SettingsWindowUI lines). The Timeline's only footer
  is the tooltip-echo strip plus the Close button (`UI/TimelineWindowUI.cs:507`, `:509`).
- There is **no "Delete rewind point" button in the Timeline**. Grep for `Delete` over
  `UI/TimelineWindowUI.cs` returns nothing. The complete per-row action set is W / FF / R / Fly /
  Seal / GoTo (enumerated in (e) below).

---

## 04.1 Parsek - Timeline (window)

### (a) Name and kind
`Parsek - Timeline` - a resizable, draggable IMGUI window. Title literal at
`UI/TimelineWindowUI.cs:285`. Not a tab, not a dialog. It raises one modal dialog of its own
(the Warp-to-Time confirmation, `WarpToTimeController.cs:223`) and delegates three more to
`RecordingsTableUI` / `RewindInvoker`.

### (b) Class + draw method
| Item | file:line |
|---|---|
| Class `TimelineWindowUI` | `UI/TimelineWindowUI.cs:14` |
| Public entry `DrawIfOpen(Rect mainWindowRect)` | `UI/TimelineWindowUI.cs:250` |
| `ClickThruBlocker.GUILayoutWindow` call (window id `"ParsekTimeline".GetHashCode()`) | `UI/TimelineWindowUI.cs:281` |
| Window body delegate `DrawTimelineWindow(int windowID)` | `UI/TimelineWindowUI.cs:426` |
| Construction (two ParsekUI init paths) | `ParsekUI.cs:161`, `ParsekUI.cs:181` |
| Host forwarder `ParsekUI.DrawTimelineWindowIfOpen` | `ParsekUI.cs:1031` |

### (c) Scenes that host it + the gate that shows it
| Scene | Draw call | Addon |
|---|---|---|
| FLIGHT (and map view, same OnGUI) | `ParsekFlight.cs:2133` | `[KSPAddon(KSPAddon.Startup.Flight, false)]`, `ParsekFlight.cs:25` |
| SPACECENTER | `ParsekKSC.cs:255` | `[KSPAddon(KSPAddon.Startup.SpaceCentre, false)]`, `ParsekKSC.cs:17` |

Not drawn in the Tracking Station (`ParsekTrackingStation` has no `DrawTimelineWindowIfOpen`
call; grep over `Source/Parsek` returns only the three sites above).

**The Timeline window IS drawn in flight.** The census seam agrees: its window spec is
`NewSpec(TimelineWindow, true, true, "overview", "details", "rewindff", "refly")` at
`TestCommands/TestCommandUiAction.cs:401`, where the two bools are `inFlight` / `inKsc`
(`TestCommands/TestCommandUiAction.cs:436`). The flight census run simply did not photograph it;
that is a census-coverage gap, not a code gate. See (f) and APPENDIX-NOPICTURE.

Gate to show it: the main-window `Timeline` launcher button at `ParsekUI.cs:794`, which toggles
`timelineUI.IsOpen` (`ParsekUI.cs:798`). The launcher is deliberately **unwrapped** by any
`IsVisible` call - `ParsekUI.cs:762-764` explains that `UiSurface.MainButtonTimeline` is
constant-true in both modes, and `UI/UiComplexityMode.cs:171` is the decision
(`case UiSurface.MainButtonTimeline: // only access to rewind / FF / warp-to` -> `visibleInBasic = true`).
`IsOpen` setter at `UI/TimelineWindowUI.cs:229`; automation open/close and rect/tab drive through
`TestCommands/ParsekTestCommandAddon.UiAction.cs:614-621`.

Input lock: `InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, "Parsek_TimelineWindow")`
while the mouse is inside the rect (`UI/TimelineWindowUI.cs:297-308`, id const `:64`), released at
`:311`.

### (d) STRUCTURE top-down

Draw order inside `DrawTimelineWindow` (`UI/TimelineWindowUI.cs:426`):

| Zone | What | file:line |
|---|---|---|
| 0 | `EnsureStyles()`; lazy `LoadWarpInputs()`; click-away commit of an in-progress warp field | `:428`, `:430-431`, `:434-440` |
| 2 | `DrawFilterBar()` - two rows of toggles | `:443` -> `:753` |
| 2b | `DrawTimeRangeFilterBar()` - preset row + optional sliders | `:446` -> `:902` |
| cache | ERS/ELS rebuild when `ShouldRebuildTimeline` says so | `:450-493` |
| 3 | `DrawEntryList()` - scroll view of rows + "now" divider | `:496` -> `:1074` |
| - | `GUILayout.FlexibleSpace()` | `:498` |
| footer 1 | `DrawWarpRow()` - "Warp to time" + Y/D/H/M fields | `:501` -> `:525` |
| footer 2 | `tooltipEcho.Draw()` - permanent single-line hover-help strip | `:507` (box constructed `:86-87`) |
| footer 3 | `Close` button | `:509` |
| chrome | resize handle + `GUI.DragWindow()` | `:514`, `:517` |

#### Filter bar row 1 (`DrawFilterBar`, `UI/TimelineWindowUI.cs:753-834`)

Six fixed columns, each `GetResponsiveButtonWidth()` wide (`:417`, formula
`Max(93, (windowWidth - 22 - 28) / 6)`; at the census width 1000 that is 158.33, and the json
reports 158 for every one of them).

| Col | Control | Kind | file:line | Tooltip |
|---|---|---|---|---|
| 1 | `Overview` | tier toggle | `:770-777` | "Shows only the headline rows: launches, endings and career events." |
| 2 | `Details` | tier toggle | `:778-785` | "Adds the fine-grained rows Overview hides, such as staging and docking." |
| 3 | `""` | spacer Label, not Space (margin-collapse reason at `:787-791`) | `:792` | - |
| 4 | `Recordings` | source toggle | `:804-812` | "Shows or hides the rows that come from your recorded flights." |
| 5 | `Actions` | source toggle | `:813-821` | "Shows or hides career moves: funds, science, contracts, upgrades." |
| 6 | `Events` | source toggle | `:823-831` | "Shows or hides in-flight happenings such as crew deaths and recoveries." |

#### Filter bar row 2 (`UI/TimelineWindowUI.cs:836-899`)

| Col | Control | file:line | Tooltip |
|---|---|---|---|
| 1 | `Rewind/FF` tier toggle | `:838-849` | "Shows only the flights you can rewind to or fast-forward to." |
| 2 | `Re-Fly` tier toggle | `:852-863` | "Shows only the flights you can fly again or seal as final." |
| 3 | `""` spacer Label | `:868` | - |
| 4 | `Archived` toggle | `:876-891` | "Brings back rows for flights you archived - the same Archive filter the recordings list uses." |

#### Time-range preset row (`DrawTimeRangeFilterBar`, `UI/TimelineWindowUI.cs:941-994`)

Same six-column width. Every preset goes through `DrawPresetButton` (`:1053`), whose preset key is
`content.text` so label and stored name cannot drift (`:1050-1051`).

| Col | Control | file:line | Range written | Tooltip |
|---|---|---|---|---|
| 1 | `Last Day` | `:946-949` | `[now - 86400, now]` (`ParsekTimeFormat.SecsPerDay`, `:943`) | "Narrows the list to rows from the last day of game time." |
| 2 | `Last 7d` | `:950-953` | `[now - 7d, now]` | "Narrows the list to rows from the last seven days of game time." |
| 3 | `Last 30d` | `:954-957` | `[now - 30d, now]` | "Narrows the list to rows from the last thirty days of game time." |
| 4 | `This Year` | `:962-965` | `[floor(now/yr)*yr, +1yr]` (`:960-961`) | "Narrows the list to rows from the current game year." |
| 5 | `All` | `:969-977` | `filter.Clear()` + sliders reset to bounds | "Clears the time filter and shows the whole timeline." |
| 6 | `Custom` | `:982-991` | reveals the two sliders | "Reveals sliders for picking your own start and end time." |

`Custom` (col 6) is drawn only when `hasRange` (`:980`), i.e.
`sliderBoundMax - sliderBoundMin > 1f` (`:935`). Whole slider block early-returns when
`!hasRange` (`:996`).

Custom slider block (`:999-1043`), visible only while `showCustomRange`:
- Optional active-range readout label, ONLY when `filter.IsActive && filter.ActivePresetName == null`
  (`:1002-1007`); text is `FormatSliderLabel(min) + " - " + FormatSliderLabel(max)`.
- `From:` label `Width(38)` + `HorizontalSlider` + value label `Width(120)` (`:1012-1020`).
- `To:` label `Width(38)` + `HorizontalSlider` + value label `Width(120)` (`:1023-1031`).
- `newMin` clamped to `newMax` (`:1034`); applied at >0.5 s delta via `filter.SetRange` (`:1037-1042`).

Backing state is `TimeRangeFilterState` (`UI/TimeRangeFilter.cs:9`): `MinUT`/`MaxUT` nullable
(`:12`, `:15`), `IsActive` = any bound set (`:18`), `ActivePresetName` (`:21`), `Clear()` (`:23`),
`SetRange()` (`:30`). It is **shared cross-window state owned by ParsekUI** (`UI/TimeRangeFilter.cs:6-7`,
`ParsekUI.cs:109`), so the Recordings table reads the same object; it is NOT persisted across
sessions (`UI/TimeRangeFilter.cs:7`). Pure logic in `TimeRangeFilterLogic`
(`UI/TimeRangeFilter.cs:42`): `IsUTInRange` (`:48`), `DoesRecordingOverlapRange` (`:60`),
`ComputeSliderBounds` (`:117`, returns `(0,0)` on an empty set, extends max to currentUT at `:137`),
`FormatSliderLabel` (`:145`, trims the `:SS` off `KSPUtil.PrintDateCompact` to mask float
quantization). `ResetTimeRangeSliders()` (`UI/TimelineWindowUI.cs:332`) is the hook another window
calls after clearing the shared filter.

#### ROW MODEL (`DrawEntryList` -> `DrawEntryRow`)

One row = one `TimelineEntry` (`Timeline/TimelineEntry.cs:73`) that survives `IsEntryVisible`
(`UI/TimelineWindowUI.cs:1162`). The list is built once per dirty cycle by `TimelineBuilder.Build`
(`UI/TimelineWindowUI.cs:456`) from `EffectiveState.ComputeERS()` + `ComputeELS()` +
`MilestoneStore.Milestones` (`:455-462`), and sorted by UT only
(`Timeline/TimelineBuilder.cs:69`, `entries.Sort((a,b) => a.UT.CompareTo(b.UT))`).
**There are no sort controls and no sortable column headers** - chronological is the only order,
and there is no header row at all.

Row cells, in draw order (`UI/TimelineWindowUI.cs:1254-1467`):

| # | Cell | Width | file:line |
|---|---|---|---|
| 1 | Time label - `KSPUtil.PrintDateCompact` or, for exactly one row, a countdown | `GUILayout.Width(160)` (`TimeColumnWidth`, `:70`) | `:1280-1281`, formatter `:1241` |
| 2 | 14 px spacer matching the right-hand button gutter | `GUILayout.Space(14f)` | `:1285` |
| 3 | Description label, `ExpandWidth(true)`; a revealed archived row gets `"   [archived]"` appended into the SAME Label (control-count reason at `:1289-1291`) | expands | `:1292-1295` |
| 4.. | Zero or more action buttons, right-aligned (see (e)) | 40 / 48 | `:1298-1465` |

Row text colour is one of six cached styles picked at `:1269-1277`:
`timelineStrikethroughStyle` when `!entry.IsEffective`, `timelineDimStyle` when the row is in the
future, `timelineBlueStyle` when `entry.IsPlayerAction`, else `GetStyleForColor(entry.DisplayColor)`
(`:1777`) which maps to green / red / white. Style construction at `:356-375`.

Two row-type dispatches decide the action set:
- `TimelineEntryType.RecordingStart` with a non-empty `RecordingId` (`:1298`) -> W / FF-or-R / GoTo.
- `TimelineEntryType.UnfinishedFlightSeparation` or `Separation` (`:1423-1425`) -> Fly+Seal (UF
  flavour only, `:1436-1439`) then GoTo. Explicitly no Watch / R / FF on separation rows - the
  comment at `:1427-1430` calls those "launch-playback affordances, not split affordances".
- Every other `TimelineEntryType` value (the 23 game-action kinds plus `VesselSpawn`, `CrewDeath`,
  `LegacyEvent`; enum at `Timeline/TimelineEntry.cs:10-46`) draws time + description only.

The **"now" divider** is a pseudo-row, not an entry: `DrawNowDivider` (`:1209`) draws
`"-- {date} (now)"` in the 160 px time column plus a 2 px expanding gray rule box
(`:1218-1224`, texture built at `:396-403`). It is emitted before the first future row
(`:1142-1146`) or, when everything is in the past, at the end of the list (`:1153-1156`). The
first visible strictly-future row shows a COUNTDOWN instead of a date, once
(`TryConsumeCountdownTimeLabel`, `:1229`, via `SelectiveSpawnUI.FormatCountdown` at `:1245`).

Empty state: `"No timeline entries."` label at `:1079` when the cache is null or empty.
Note that is the *cache* being empty, not the *filter* - a filter that hides every row still
draws the divider and no message (visible in `ksc-timeline-refly-advanced`).

Cross-link scroll-to: `ScrollToRecording(recordingId)` (`:322`) is called by the Recordings
manager; the next draw resolves the visible row index and sets `timelineScrollPos.y = row * 20f`
(`ApproxRowHeight`, `:69`) at `:1090-1121`, logging a miss at `:1117`.

#### Footers

1. **Warp row** (`DrawWarpRow`, `:525-586`): `Warp to time` button at `Width(FilterButtonWidth * 2f)`
   = 186 px (`:564`), an 8 px space (`:578`), then four `DrawWarpField` groups Year / Day / Hour /
   Minute (`:579-582`), each a `TextField` of `Width(36)` (`WarpInputWidth`, `:155`) plus a plain
   label, plus a 6 px trailing space (`:596`, `:615`, `:623-624`). Then `FlexibleSpace` (`:584`).
2. **Tooltip echo strip** (`:507`): a permanently present, constant-height single-line box
   (`TooltipEchoBox(DefaultSpacing=3f, SingleLine=1)`, `:86-87`, constants
   `UI/TooltipEchoBox.cs:65`, `:68`). Rationale at `:80-85`: nearly every filter and row action in
   this window authored a tooltip that was unreachable because KSP draws no tooltip layer.
3. **Close** button, full width (`:509`), routed through `CloseWindow()` (`:698`) which commits the
   in-progress warp field and persists Y/D/H/M via `ParsekSettingsPersistence.RecordWarpDate`.

### (e) Action controls and their backends; disabled/hidden conditions

#### The four mutually exclusive tier views

`TimelineTierFilterMode` (`UI/TimelineWindowUI.cs:33-39`) - `Overview`, `Details`,
`RewindOrFastForward`, `ReFly`. Field `tierFilterMode`, default `Overview` (`:100`). The enum is
private; the census seam reaches it through the int property `TierFilterModeIndexForTesting`
(`:113`, out-of-range writes silently ignored at `:118-120`), wired at
`TestCommands/ParsekTestCommandAddon.UiAction.cs:619-620` with the wire vocabulary
`overview | details | rewindff | refly` (`TestCommands/TestCommandUiAction.cs:401`).

They are mutually exclusive because each toggle assigns the field rather than OR-ing a flag
(`:775`, `:783`, `:846`, `:860`), and the row predicate `IsEntryVisible` branches on it with
`if / else if / else if` (`:1164`, `:1178`, `:1189`).

| Mode | Row predicate | file:line | Effect on the source toggles |
|---|---|---|---|
| Overview | drops every `SignificanceTier.T2` entry | `:1189-1192` | Recordings/Actions/Events live |
| Details | no tier drop; all T1+T2 | (falls through `:1189`) | Recordings/Actions/Events live |
| Rewind/FF | keeps ONLY rows where `HasActionableRewindOrFastForwardButton` is true | `:1164-1177`, predicate `:1576` | forces `showRecordingEntries = true` (`:796-799`, `:847`) and DISABLES all three source toggles (`GUI.enabled = !actionFilterMode`, `:803`, restored `:832`) |
| Re-Fly | keeps ONLY rows where `HasActionableFlyOrSealButton` is true | `:1178-1188`, predicate `:1587` | same forced/disabled behaviour (`:861`, `:794-795`) |

`HasActionableRewindOrFastForwardButton` (`:1576-1585`) requires `entry.Type == RecordingStart`
AND either (`ShouldShowFastForwardButton` && `canFastForward`) or (`ShouldShowRewindButton` &&
`canRewind`) - i.e. the mode hides rows whose button would be greyed, not merely absent.
`HasActionableFlyOrSealButton` (`:1587-1598`) requires `entry.Type == UnfinishedFlightSeparation`
AND `IsResolvedFlySealRoute` (`:1600`: route == `Resolved`, `rp != null`, `slotListIndex` in range
of `rp.ChildSlots`).

After the tier branch, every mode applies the source toggles (`:1194-1199`) and then the shared
time-range filter (`:1202-1204`).

#### Row action buttons

| Button | Label | Width | Shown when | Enabled when | Backend | file:line |
|---|---|---|---|---|---|---|
| Watch | `W` / `W*` | 40 (`GetRowActionButtonWidth`, `:1637`) | `ShouldShowWatchButton(inFlightMode && flight != null, rec)` (`:1644`, call `:1314`) | `RecordingsTableUI.ShouldEnableWatchButton(canWatch, isWatching)` via `BuildWatchButtonDescriptor` (`:1655`) | `flight.EnterWatchMode(recIndex)` / `flight.ExitWatchMode()` through `ApplyWatchButtonAction` (`:1687`) | `:1314-1359` |
| Fast-Forward | `FF` | 40 | `ShouldShowFastForwardButton(rec, isFuture)` = `isFuture && rec != null` (`:1564`) | `CanFastForwardNow` -> `RecordingStore.CanFastForwardAtUT` (`:1721`, `:1728`) | `tableUI.ShowFastForwardConfirmation(rec)` | `:1364-1380` |
| Rewind | `R` | 40 | `ShouldShowRewindButton(rec, isFuture)` = `!isFuture && rec != null && GetRewindSaveFileName(rec)` non-empty (`:1569`) | `CanRewindWithResolvedSaveState` (`:1740`) | `tableUI.ShowRewindConfirmation(rec)` | `:1381-1397` |
| Fly | `Fly` | 40 (reuses the Rewind width, `:1480`) | row is `UnfinishedFlightSeparation` (`:1436`) | `resolvable && canInvoke` (`:1677`) | `RewindInvoker.ShowDialog(rp, slotListIndex)` | `:1478-1521`, click `:1511` |
| Seal | `Seal` | 40 | same as Fly | `resolvable` only - Seal stays live when Fly is greyed (`:1681`) | `RecordingsTableUI.HandleSealUnfinishedFlightClick(rec, rp, slotListIndex)` | `:1519` |
| GoTo | `GoTo` | 48 (`GoToButtonWidth`, `:73`) | `UiSurfaceVisibility.IsVisible(UiSurface.TabMissions, ParsekUI.AppliedUiComplexityMode)` | `CanGoToMission(rec)` = `rec != null && !string.IsNullOrEmpty(rec.TreeId)` (`:1622`) | `tableUI.ShowMissionForRecording(entry.RecordingId)` | RecordingStart row `:1400-1420`; Separation row `:1442-1463` |

FF and R are mutually exclusive on one row (`if (showFastForward) ... else if (showRewind)`,
`:1364`/`:1381`), so a RecordingStart row carries at most W + one of FF/R + GoTo.

**The GoTo gate.** The single `IsVisible` call in this file is
`UI/TimelineWindowUI.cs:1265-1266`, computed once per row into `showMissionCrossLink` and consumed
twice (`:1400`, `:1442`). It reads the FRAME-LATCHED `ParsekUI.AppliedUiComplexityMode`, never the
settings field, because Layout and Repaint must agree on the control count (`:1263-1264`). It is
gated by the TARGET surface's key, not the host window's (`:1256-1262`, echoed at
`UI/UiComplexityMode.cs:35-41` and `:77`). Because `TabMissions` is `visibleInBasic = true`
(`UI/UiComplexityMode.cs:175`), this predicate reads TRUE in both modes today and hides nothing -
it exists so that retargeting GoTo at a hidden surface would automatically hide the button rather
than strand the player.

**The rewind gate** (what actually greys `R`). `CanRewindWithResolvedSaveState`
(`UI/TimelineWindowUI.cs:1740`) resolves the tree-root save name
(`RecordingStore.GetRewindSaveFileName`, `RecordingStore.cs:5188`), checks file existence through a
per-recording memo (`DoesRewindSaveExist`, `:1749`; uncached probe `:1770`), and calls
`RecordingStore.CanRewindWithResolvedSaveState` (`RecordingStore.cs:5213`). That is a
**five-precondition gate**, evaluated in this exact order, and the failing one's string becomes
both the button tooltip and the disabled-hover echo:

| # | Condition | Refusal text | file:line |
|---|---|---|---|
| 1 | `RecordingStore.IsRewinding` | "Rewind already in progress" | `RecordingStore.cs:5233-5237` |
| 2 | resolved save name empty | "No rewind save available" | `RecordingStore.cs:5240-5244` |
| 3 | `isRecording` (in flight and `Flight.IsRecording`, computed at `UI/TimelineWindowUI.cs:1742`) | "Stop recording before rewinding" | `RecordingStore.cs:5247-5251` |
| 4 | `RecordingStore.HasPendingTree` | "Merge or discard pending tree first" | `RecordingStore.cs:5254-5258` |
| 5 | the `.sfs` file is not on disk | "Rewind save file missing" | `RecordingStore.cs:5219-5223` |

Note precondition 2 ALSO removes the button entirely rather than greying it, because
`ShouldShowRewindButton` (`UI/TimelineWindowUI.cs:1569-1574`) already requires a non-empty resolved
save name. So in practice the reachable greyed-out reasons on a drawn `R` are 1, 3, 4 and 5.

The FF gate is a near-mirror with six conditions (`RecordingStore.cs:5281`, `:5298`):
"Rewind already in progress" (`:5300`), "Recording not available" (`:5307`),
"Recording was rewound out of the active timeline" (`:5319-5325`, the cascade-overload case for a
parent-anchored debris child of a retired parent), "Stop recording before fast-forwarding"
(`:5328`), "Merge or discard pending tree first" (`:5335`), and "Recording is not in the future"
(`:5286-5290`).

Every disabled control in this window re-publishes its reason to the help strip via
`DisabledHoverEcho.CarryLastControl` - `UI/TimelineWindowUI.cs:1343` (Watch), `:1372` (FF), `:1389`
(R), `:1533` / `:1538` (Fly / Seal), `:1412` / `:1454` (GoTo), `:568` (Warp).

#### Warp to time

| Control | file:line | Backend |
|---|---|---|
| `Warp to time` button, `Width(186)` | `UI/TimelineWindowUI.cs:563-564` | `WarpToTimeController.RequestWarp(y, d, h, m, flight, inFlight)` (`:574`, entry point `WarpToTimeController.cs:61`) |
| Year / Day / Hour / Minute text fields, `Width(36)` each | `:579-582` -> `DrawWarpField` `:588` | commit on Enter (`:609`, `:619`), cancel on Escape (`:611`, `:620`), commit on click-away (`:434-440`); parse via `WarpToTimeMath.TryParseField` (`:632`) |

Enabled condition (`:550-562`): `actionable && !warpPending && !warpDeferredKsc`, where
`actionable` means the plan is `ForwardOnly` or `RewindThenForward`. The plan is
`WarpToTimeMath.DecideWarpPlan(targetUT, currentUT, inFlight, hasRewindTarget, landsAtStart)`
(`:547`), and the expensive half - `WarpToTimeController.ResolveRewindTarget` (`:539`, defined
`WarpToTimeController.cs:152`) - is memoised on the entered date plus a `warpResolveDirty` flag
(`:537-544`, flag set in `InvalidateCache` at `:348`) because it enumerates ERS.

The button carries the ENABLED wording even when the two already-warping gates grey it, so the
disabled-hover carrier resolves the reason itself through the pure
`WarpToTimeDisabledReason(actionable, planReason, hasPendingWarp, hasDeferredKscWarp)`
(`:1552-1562`):

| Case | Text | file:line |
|---|---|---|
| not actionable, planner gave a reason | the planner's own wording | `:1556` |
| not actionable, planner silent | "Enter a time to warp to" | `:1556` |
| `WarpToTimeRequest.HasPending` | "A warp is already running" | `:1558` |
| `WarpToTimeRequest.HasDeferredKscWarp` | "This warp continues at the Space Center" | `:1560` |

Planner reasons come from `WarpToTimeMath.DecideWarpPlan`: "Already at this time" (within
`AtTargetEpsilonSeconds`, `WarpToTimeMath.cs:145-147`) and "No launch save to rewind to"
(`WarpToTimeMath.cs:154-155`).

`RequestWarp` (`WarpToTimeController.cs:61`) then either fires a ScreenMessage for the two
non-actionable kinds (`:81`, `:84`) or spawns the confirmation dialog (`:94` -> `ShowConfirmation`
`:217`). The dialog is a stock `PopupDialog` / `MultiOptionDialog` titled
`"Confirm: Warp to Time"` with `Warp` / `Cancel` buttons (`:223-243`); its body text is built by
`BuildConfirmMessage` (`:246`) in four shapes - forward-only (`:251-256`), career-start reset
(`:259-266`), earliest-launch rewind (`:270-274`), and the general rewind-then-forward
(`:276-277`). On confirm: at KSC it executes immediately (`:93` -> `Execute` `:286`); in flight it
NEVER executes in place - it defers the whole warp to the next Space Center arrival
(`DeferWarpToSpaceCenter`, `:133-143`) so `SceneExitInterceptor`'s Merge / Discard dialog handles
the active recording first, and `WarpToTimeConsumer` re-resolves and runs it with no second prompt
(`ExecuteAtKsc`, `:105`).

#### Archive toggle

`Archived` (`UI/TimelineWindowUI.cs:876`) writes the property `ShowArchivedRecordings` (`:734`),
which is the INVERSE of `GroupHierarchyStore.HideActive` (`:736-737`) - deliberately the same
single flag the Recordings tab's Archive header checkbox writes, so one archive filter is
reachable from either list. That matters because the Recordings tab is hidden in Basic
(`UI/UiComplexityMode.cs:183`), which makes this toggle **the only archive control a Basic player
can reach** (`:870-874`). It is ungated on purpose. Because nothing invalidates the timeline cache
when the other window writes the shared flag, the rebuild predicate carries a dedicated arm:
`ShouldRebuildTimeline(dirty, cacheMissing, cachedShowedArchived, showArchivedNow)` (`:747-751`),
consumed at `:450`.

### (f) STATE VARIANTS and census coverage

Evidence run: `harness/results/2026-09-11_0548_GUI-1-census-ksc_shots/`. Seam trail for the
Timeline sequence is `KSP.log:14669` (open) through `KSP.log:14914` (close) for Advanced, and
`KSP.log:15684`-`15747` for Basic. `op=rect` forced `270,8,1000,700` at `KSP.log:14749` and
`KSP.log:15707`.

| Variant | Census label | Timeline-window node count | What it shows |
|---|---|---|---|
| Overview, Advanced, KSC | `ksc-timeline-overview-advanced` | 338 | 22 `R` + 22 `GoTo`; T1 rows only (Launch / Spawn / Milestone / Starting funds-rep-science / EVA / Board / Complete); vertical scrollbar present (`GUI.Slider` hits=1) |
| Details, Advanced, KSC | `ksc-timeline-details-advanced` | 1154 | 22 `R` + **34** `GoTo` - the extra 12 are `Separation:` rows (e.g. `Separation: R2-B2-S5`); adds Crew / Tech / Assign / science-subject rows |
| Rewind/FF, Advanced, KSC | `ksc-timeline-rewindff-advanced` | 152 | exactly the 22 actionable `RecordingStart` rows: 22 `R` + 22 `GoTo`, zero non-recording rows |
| Re-Fly, Advanced, KSC | `ksc-timeline-refly-advanced` | 39 | EMPTY list - filter chrome + the lone "now" divider + warp row + Close. This save has no `UnfinishedFlightSeparation` with a resolved RP slot |
| Basic mode, KSC | `ksc-timeline-basic` | 39 | identical Timeline subtree to the Re-Fly capture |

**Why `ksc-timeline-basic` has only 39 nodes: residual filter state, NOT a mode gate.** Derived
from code plus log plus json:

1. Code: the only `UiSurfaceVisibility.IsVisible` call in the whole file is the GoTo gate at
   `:1265`, and its key `TabMissions` is `visibleInBasic = true` (`UI/UiComplexityMode.cs:175`).
   Nothing else in `TimelineWindowUI.cs` reads the complexity mode (grep for `UiSurface` returns
   only `:1265-1266`). The Archived toggle comment states the intent outright: "the Timeline shows
   the same rows in both modes; nothing here reads the UI complexity mode" (`:874`).
2. Log: the Advanced pass ended on `uiaction tab window=timeline tab=refly index=3`
   (`KSP.log:14869`), then closed the window (`KSP.log:14914`). The Basic pass re-opened the window
   (`KSP.log:15684`) and issued only `op=rect` - **no `op=tab`** - before capturing
   (`KSP.log:15707`, `:15718`).
3. The mode flip at `KSP.log:15525-15527` is a latch change on the SAME `ParsekUI` instance
   ("Mode change queued ... applies next Update"); no window reconstruction is logged, and
   `tierFilterMode` is a plain instance field with no reset in `CloseWindow` (`:698-704`) or in the
   `IsOpen` setter (`:229-243`).
4. json: the Timeline subtree of `ksc-timeline-basic` is 39 nodes, byte-for-byte the same shape as
   `ksc-timeline-refly-advanced` (39). The 73-vs-75 whole-capture difference is entirely in the
   MAIN window, which loses the `Kerbals` and `Career` launchers in Basic.

Conclusion: the Basic capture is a **Re-Fly view photographed in Basic mode**. It proves the
Basic main-window launcher set and that the Timeline chrome is mode-identical; it does NOT show
Basic-mode Timeline rows. A true Basic row picture needs one extra `op=tab window=timeline
tab=overview` before the capture.

Variants with NO picture (full list in APPENDIX-NOPICTURE): every flight-scene state (the whole
`W` / `W*` Watch button, since `ShouldShowWatchButton` requires `inFlightMode`, `:1644`), the `FF`
button (needs a future-UT recording), the `Fly` / `Seal` pair (needs a resolved
`UnfinishedFlightSeparation`), the `Custom` slider block, any non-default time preset, the
`Archived` toggle in its ON state, every disabled-button tooltip, the countdown time label, and
the Warp confirmation dialog.

### (g) Sizing facts

| Fact | Value | file:line |
|---|---|---|
| `DefaultWindowWidth` | 820 (aliased from `CareerStateWindowUI.DefaultWindowWidth`) | `UI/TimelineWindowUI.cs:66`, `UI/CareerStateWindowUI.cs:72` |
| `MinWindowWidth` | 520 (aliased from `CareerStateWindowUI.MinWindowWidth`) | `UI/TimelineWindowUI.cs:67`, `UI/CareerStateWindowUI.cs:74` |
| `MinWindowHeight` | 150 | `UI/TimelineWindowUI.cs:68` |
| First-open rect | `x = mainWindow.x - (820 + 10)`, flipped to the right side when that is negative; `height = Max(600, mainWindow.height)` | `:261-266` |
| Seed suppression | the default is seeded ONLY when `timelineWindowRect.width < 1f`, so a commanded `op=rect` is never overwritten | `:261`, contract at `:46-54` |
| `ApproxRowHeight` (scroll-to arithmetic only) | 20 | `:69` |
| `TimeColumnWidth` | 160 | `:70` (json row labels report 160) |
| `RowActionButtonWidth` (W / FF / R / Fly / Seal) | 40 | `:72`, dispatched by `GetRowActionButtonWidth` `:1637` |
| `GoToButtonWidth` | 48 | `:73` |
| `FilterButtonWidth` (floor for the responsive width) | 93 | `:78` |
| Responsive filter/preset width | `Max(93, (width - 22 - 28) / 6)`; at width 1000 that is 158.33 and the json reports 158 for all 16 toggles | `:417-424` |
| Warp button width | `FilterButtonWidth * 2f` = 186 (json: 186) | `:564` |
| `WarpInputWidth` | 36 (json: 36) | `:155` |
| Toggle button horizontal margin | 4 px each side (the 28 px margin budget above assumes exactly this) | `:385-386` |
| Slider label widths | `From:`/`To:` 38, value 120 | `:1014`, `:1019`, `:1025`, `:1030` |
| "Now" divider rule | 2 px high, `ExpandWidth(true)`, 12 px top margin | `:1223-1224`, `:402` |
| Tooltip echo strip | single line, 3 px spacing, constant height, always drawn | `:86-87`; `UI/TooltipEchoBox.cs:65`, `:68` |
| Census rect (forced) | `[270, 8, 1000, 700]` | `KSP.log:14749`, `KSP.log:15707` |

---

## Notes on the neighbouring files in scope

- `UI/TimeRangeFilter.cs` hosts no draw code at all: it is the shared state object
  (`TimeRangeFilterState`, `:9`) plus pure predicates (`TimeRangeFilterLogic`, `:42`). Its only
  Timeline-visible surfaces are the six preset toggles and the two sliders documented above.
- `WarpToTimeController.cs` contributes exactly one player-facing surface of its own - the
  `"Confirm: Warp to Time"` `MultiOptionDialog` (`:223-243`) - plus seven `ScreenMessage` sites
  (appendix below). Everything else in the file is orchestration.
- `RewindPointDiskUsage.cs` contributes **nothing** to the Timeline window. Its one draw site is
  `UI/SettingsWindowUI.cs:684-686` (a `GUILayout.Label` with the tooltip "Rewind-point quicksave
  disk use, by crashed / stable / concluded."). Documented in whichever section owns Settings.

---

# Section 05 - Kerbals and Career State windows

Scope: `UI/KerbalsWindowUI.cs` (910 lines), `UI/CareerStateWindowUI.cs` (2030 lines), plus the
backend members those two files actually call in `KerbalsModule.cs` / `CrewReservationManager.cs`.
Paths are relative to `Source/Parsek`. Census evidence:
`harness/results/2026-09-11_0548_GUI-1-census-ksc_shots/`.

## Program-level facts that hold for BOTH windows

| Fact | Evidence |
| --- | --- |
| Both are Basic-HIDDEN, so neither has a Basic picture BY CONSTRUCTION | `UI/UiComplexityMode.cs:181` (`MainButtonKerbals`), `UI/UiComplexityMode.cs:182` (`MainButtonCareer`), both in the `visibleInBasic = false` arm at `UI/UiComplexityMode.cs:188` |
| The gate is evaluated on the MAIN window's launcher, not inside these two files | `ParsekUI.cs:904`, `ParsekUI.cs:906`; the buttons themselves at `ParsekUI.cs:912` / `ParsekUI.cs:923` |
| Neither file contains a `UiSurfaceVisibility.IsVisible` call. The only `IsVisible*` identifiers in scope are `CareerStateWindowUI.IsVisibleTimelineAction` (`UI/CareerStateWindowUI.cs:912`, `:918`), which is a ledger-action mode filter, NOT a complexity gate | grep over both files |
| Switching Advanced -> Basic force-CLOSES both windows and releases their input locks | `ParsekUI.cs:492`-`:497` (CareerState), `ParsekUI.cs:498`-`:503` (Kerbals) |
| Hosted in FLIGHT and SPACECENTER only. Tracking Station does not draw either | `ParsekFlight.cs:2134` / `:2135`; `ParsekKSC.cs:256` / `:257`; `ParsekTrackingStation.cs` has no `DrawKerbals*` / `DrawCareer*` call site |
| So the code DOES draw both in flight - the flight census simply never opens them | `harness/scenarios/GUI-2-census-flight.toml` opens only `main`, `missions`, `spawncontrol`, `gloops` (lines 122, 171, 151, 154); no `window = "kerbals"` / `window = "career"` step exists in that spec |
| Neither file emits a single `ParsekLog.ScreenMessage` | grep over both files returns nothing |
| Both windows were photographed at the census rect `[270, 8, 1000, 700]`, commanded by the spec | `harness/scenarios/GUI-1-census-ksc.toml:205` (kerbals), `:217` (career) |

---

## 5.1 Parsek - Kerbals (window)

**(a) Name and kind.** `Parsek - Kerbals` - a draggable, resizable IMGUI window with a
two-entry tab toolbar. It contains no dialogs, overlays, markers or screen messages.

**(b) Class + draw method.**

| Piece | file:line |
| --- | --- |
| Class | `UI/KerbalsWindowUI.cs:16` |
| `DrawIfOpen(Rect mainWindowRect)` | `UI/KerbalsWindowUI.cs:176` |
| `ClickThruBlocker.GUILayoutWindow` call, title `"Parsek - Kerbals"` | `UI/KerbalsWindowUI.cs:205`-`:213` (title literal at `:209`, window id `"ParsekKerbals".GetHashCode()` at `:206`) |
| Window body delegate `DrawKerbalsWindow(int windowID)` | `UI/KerbalsWindowUI.cs:301` |
| Tab 0 body `DrawTopologySection` | `UI/KerbalsWindowUI.cs:393` |
| Tab 0 tail `DrawOrphanRetiredSection` | `UI/KerbalsWindowUI.cs:477` |
| Tab 1 body `DrawEndStatesSection` | `UI/KerbalsWindowUI.cs:598` |
| View-model builder `Build(...)` | `UI/KerbalsWindowUI.cs:757` |

**(c) Scenes + gate.** FLIGHT (`ParsekFlight.cs:2134`, inside the `if (showUI)` block opened at
`ParsekFlight.cs:2108`, after the pause-menu early return at `ParsekFlight.cs:2096`) and
SPACECENTER (`ParsekKSC.cs:256`, after `if (!showUI) return;` at `ParsekKSC.cs:229` and the
pause gate at `ParsekKSC.cs:234`). Shown when `IsOpen == true` (`UI/KerbalsWindowUI.cs:159`),
which only the main-window launcher toggles (`ParsekUI.cs:918`) and that launcher is
Basic-hidden (`ParsekUI.cs:904`). `DrawIfOpen` early-returns and releases the input lock when
closed (`UI/KerbalsWindowUI.cs:178`-`:182`).

**(d) STRUCTURE top-down.**

1. `GUILayout.Space(5)` under the title bar - `UI/KerbalsWindowUI.cs:305`.
2. VM rebuild if `cachedVM == null`: reads `LedgerOrchestrator.Kerbals` (`:309`) and
   `EffectiveState.ComputeERS()` (`:313`). Null-module branch at `:316`, live branch at `:325`.
3. **Tab bar** - `GUILayout.Toolbar(selectedTab, TabLabels, toggleButtonStyle)` at
   `UI/KerbalsWindowUI.cs:337`. No width pin; the toolbar spans the window. Labels and tooltips
   are `GUIContent`s at `UI/KerbalsWindowUI.cs:108`-`:114`:

   | idx | Label | Tooltip |
   | --- | --- | --- |
   | 0 | `Roster State` | `Who fills each crew slot now: reserved, flying or retired stand-ins.` |
   | 1 | `Mission Outcomes` | `Every recorded flight a kerbal took, and how each one ended.` |

   On change: `SwitchTab` logs (`:669`) and `kerbalsScrollPos.y = 0f` (`:342`).
4. **Scroll view** - `GUILayout.BeginScrollView(..., GUILayout.ExpandHeight(true))` at `:345`,
   ended at `:373`.
5. Tab body (below).
6. **Footer 1 - tooltip echo strip.** `tooltipEcho.Draw()` at `UI/KerbalsWindowUI.cs:379`.
   Constructed with the DEFAULT (two-line) height at `UI/KerbalsWindowUI.cs:53`
   (`new TooltipEchoBox()`, which chains to the default-spacing ctor at
   `UI/TooltipEchoBox.cs:113`; two-line is the non-`SingleLine` branch at
   `UI/TooltipEchoBox.cs:137`). Measured at 38 px tall in the census
   (`ksc-kerbals-roster-advanced.gui.json`: `[label] '' rect=[280, 631, 980, 38]`).
7. **Footer 2 - `Close` button.** `UI/KerbalsWindowUI.cs:381`; sets `showKerbalsWindow = false`.
8. **Resize handle** `ParsekUI.DrawResizeHandle` at `UI/KerbalsWindowUI.cs:387`
   (glyph renders as the label `'//'` at `[1254, 692, 16, 16]` in the census json).
9. `GUI.DragWindow()` at `UI/KerbalsWindowUI.cs:390`.

**Tab 0 - Roster State. This is NOT a column table; it is a two-level indented outline.**
There are no `GUILayout.Width` pins and no sort-key selector anywhere in the tab.

ROW MODEL (owner header row):

| Field | Source | Rendered as |
| --- | --- | --- |
| `OwnerName` | `SlotTopologyEntry.OwnerName` (`:133`), from `slots.Keys` sorted `StringComparer.Ordinal` (`UI/KerbalsWindowUI.cs:794`-`:795`) | `FormatOwnerHeader` (`:504`): `"{OwnerName} [{OwnerTrait}] - {status}"` (`:522`) |
| `OwnerTrait` | `KerbalsModule.KerbalSlot.OwnerTrait` (`KerbalsModule.cs:93`) | the `[...]` bracket |
| status | `deceased` when `OwnerPermanentlyGone` (`:510`); `reserved` / `reserved until UT {F0}` when reserved (`:514`-`:516`); else `active` (`:520`) | trailing text |
| count suffix | `CountExpandableChainEntries(entry.Chain)` (`:493`) | `"  ({chainCount})"` (`:442`) |
| style | `deadStyle` when `OwnerPermanentlyGone`, else `groupHeaderStyle` (`:443`) | red vs bold-white |

Sort key: owner name, ordinal ascending - the ONLY ordering in this tab
(`UI/KerbalsWindowUI.cs:795`). There is no user-selectable sort.

ROW MODEL (chain-member subrow, drawn only when the owner row is expanded, `:411`):
`FormatRosterChainMemberText` (`:592`) = `SubitemIndent` (four spaces, `:547`) + tree glyph
(`\- ` for the last non-empty entry, `|- ` otherwise, `:594`) + `FormatChainMember` (`:525`)
= `"{Name} ({tag})"`, tag in `active` / `retired` / `displaced` / `?` (`:530`-`:533`).
Status comes from `Build` (`:827`-`:832`): retired-set membership wins, then
`activeIdx == c` from `KerbalsModule.GetActiveChainIndex(slot)`, else displaced.
Style per status via `StyleForChainMember` (`:466`).

TAIL SECTION - `Unlinked Retired ({count})` (`UI/KerbalsWindowUI.cs:481`-`:484`), drawn only
when `orphans.Count > 0` (`:479`), with one gray label per orphan name (`:488`). Orphans are
the retired names not seen in any chain, sorted ordinal (`:856`-`:864`).

EMPTY STATE: `No reserved crew, stand-ins, or retired kerbals.` at
`UI/KerbalsWindowUI.cs:352`, shown when `vm.Topology.Count == 0 && vm.OrphanRetired.Count == 0`
(`:350`).

**Tab 1 - Mission Outcomes.** Also an outline, not a column table; no width pins, no sort
selector.

ROW MODEL (per-kerbal fold header, `:626`-`:629`): `"{arrow} {headerText}"` where `headerText`
= `FormatMissionOutcomeHeaderText` (`:563`). Unfolded -> just the bold name (`:571`). Folded ->
bold name + `FormatKerbalSummary` remainder (`:573`-`:578`), i.e.
`"{name} ({N mission[s]} - {d Dead}, {r Recovered}, {a Aboard}, {u Unknown})"` with zero
buckets elided (`:701`-`:726`). Rich text is enabled only on `missionOutcomeHeaderStyle`
(`:283`-`:286`) and `<` in a kerbal name is neutered by `EscapeRichText` (`:581`).

ROW MODEL (per-recording subrow, `:641`-`:644`): `FormatMissionOutcomeSubitemText` (`:553`)
= four-space indent + `FormatEndStateRow` (`:739`) =
`"{RecordingName or (unnamed)} - {Dead|Recovered|Aboard|Unknown} at UT {EndUT F0}"`.
Style by end state via `StyleForEndState` (`:728`): red / green / blue / gray.

Sort keys (both applied in `Build`, `UI/KerbalsWindowUI.cs:894`-`:899`): primary
`KerbalName` ordinal ascending, secondary `EndUT` ascending. Grouping in the draw pass is a
run-length scan over that same order (`:607`-`:609`). No user-selectable sort.

EMPTY STATE: `No committed crew history yet.` at `UI/KerbalsWindowUI.cs:364`, shown when
`vm.EndStates.Count == 0` (`:362`).

**(e) Action controls and backends.**

| Control | file:line | Backend it calls | Disabled / hidden condition |
| --- | --- | --- | --- |
| Tab toolbar (2 entries) | `:337` | `SwitchTab` (`:669`) - log only; resets scroll (`:342`) | never hidden |
| Owner-header fold button (label-styled) | `:454`-`:457` | mutates the local `expandedSlots` set (`:459`-`:460`) + Verbose log (`:461`) | DRAWN AS A PLAIN LABEL, not a button, when `expandable == false`, i.e. `CountExpandableChainEntries(chain) == 0` - `:445`-`:450`. That branch also adds two leading spaces so leaf bodies align with arrow rows. |
| Per-kerbal fold button | `:626`-`:629` | `ToggleFold(foldedKerbals, name, j - i)` (`:677`) | never hidden |
| Per-recording row button (cross-link) | `:641`-`:644` | `OnFatesRowClicked(scrollCallback, e.RecordingId)` (`:693`), where the callback is `parentUI.GetTimelineUI().ScrollToRecording` (`:653`-`:656`) | hidden when the kerbal group is folded (`:634`). The callback is NULL-tolerant: `GetTimelineUI()` can be null during cold-start scene transitions, and `OnFatesRowClicked` still logs (`:696`-`:698`) |
| `Close` | `:381` | `showKerbalsWindow = false` | never hidden |
| Resize handle | `:387` | `ParsekUI.DrawResizeHandle` (`ParsekUI.cs:2801`) | never hidden |

BACKEND (this is the whole of it):

| Call | file:line of the call | Backend member |
| --- | --- | --- |
| `LedgerOrchestrator.Kerbals` | `UI/KerbalsWindowUI.cs:309` | `LedgerOrchestrator.cs:6657` -> the `KerbalsModule` singleton |
| `kerbals.Slots` | `:326` | `KerbalsModule.cs:138` (`IReadOnlyDictionary<string, KerbalSlot>`) |
| `kerbals.Reservations` | `:327` | `KerbalsModule.cs:137` (`IReadOnlyDictionary<string, KerbalReservation>`); read at `:806` for `IsPermanent` (`KerbalsModule.cs:83`) and `ReservedUntilUT` (`KerbalsModule.cs:82`) |
| `kerbals.GetRetiredKerbals()` | `:328` | `KerbalsModule.cs:148` - returns a fresh snapshot list |
| `kerbals.GetActiveChainIndex(slot)` | `:330` | `KerbalsModule.cs:2198` -> `:2175`; returns `NoActiveChainOccupant` for a permanently-gone owner (`:2177`), `ActiveOwnerIndex` when the owner is unreserved (`:2180`), else the first unreserved chain index (`:2188`-`:2193`) |
| `EffectiveState.ComputeERS()` | `:313` | the ERS routing required by the grep gate; committed-but-superseded / NotCommitted recordings are excluded from the outcomes tab |

**`CrewReservationManager.cs` is NOT referenced by this window at all** - grep for
`CrewReservationManager` in `UI/KerbalsWindowUI.cs` returns zero hits. The reservation data
the roster tab shows reaches it through `KerbalsModule.Reservations` only. Consequently the
Kerbals window is READ-ONLY: it has no reserve / unreserve / swap / clear / dismiss control,
and nothing in it mutates game state. The only mutations are the two transient fold sets
(`foldedKerbals` `:64`, `expandedSlots` `:70`), neither of which is persisted and neither of
which is cleared by `InvalidateCache` (`:170`, per the comments at `:63` and `:68`).

**(f) STATE VARIANTS and pictures.**

| Variant | Census label | Note |
| --- | --- | --- |
| Roster State, ONE owner row, collapsed, no orphan section | `ksc-kerbals-roster-advanced` (9 nodes) | The single row is `> Jebediah Kerman [Pilot] - deceased  (1)` with the tooltip `Shows the stand-ins who have covered this kerbal's slot.` This is the `OwnerPermanentlyGone` -> `deceased` branch (`:508`-`:511`) rendered in `deadStyle` (`:443`), expandable (chainCount 1) and collapsed. |
| Roster State, EXPANDED owner showing `|-`/`\-` chain members | NO PICTURE | The census never clicked the arrow. The chain-member rows at `:429`-`:431` and the glyph logic at `:594` are unphotographed. |
| Roster State, `Unlinked Retired (N)` tail section | NO PICTURE | Requires an orphan: a retired name not present in any slot chain (`:856`-`:864`). |
| Roster State, `reserved` / `reserved until UT N` / `active` owner status | NO PICTURE | The one photographed owner is `deceased`. |
| Roster State, EMPTY state text | NO PICTURE | Would need a save with zero slots and zero retired. |
| Mission Outcomes, one unfolded kerbal, 15 subrows | `ksc-kerbals-outcomes-advanced` (24 nodes) | Shows `Recovered`, `Unknown`, `Aboard` and `Dead` end-states and both the recording-name and kerbal-name row shapes. |
| Mission Outcomes, FOLDED header with the count summary | NO PICTURE | `FormatKerbalSummary` (`:701`) never rendered; the census left the only group unfolded. |
| Mission Outcomes, EMPTY state text | NO PICTURE | Needs a save with no `CrewEndStatesResolved` recordings. |
| Basic-mode appearance | NO PICTURE BY CONSTRUCTION | `UiSurface.MainButtonKerbals` is `visibleInBasic = false` (`UI/UiComplexityMode.cs:181`/`:188`), so the launcher does not exist in Basic and an Advanced->Basic switch closes the window outright (`ParsekUI.cs:498`-`:503`). |
| FLIGHT-scene appearance | NO PICTURE | The code draws it in flight (`ParsekFlight.cs:2134`); `GUI-2-census-flight.toml` just never opens it. |

**IMPORTANT - the 9-node roster capture is nearly empty and is NOT a picture of a populated
roster.** A populated Roster State tab would be: one owner header row per crew slot ordered by
ordinal name, each with its `[Trait] - status  (N)` suffix; expandable rows carrying a
`>`/`v` arrow; the expanded ones followed by indented `|- Name (retired)` /
`\- Name (active)` chain members; and, when any retired stand-in has lost its slot link, a
`Unlinked Retired (N)` header plus one gray name per line. The census save had exactly one
slot and no orphans.

**(g) Sizing.**

| Quantity | Value | file:line |
| --- | --- | --- |
| `MinWindowWidth` | 280f | `UI/KerbalsWindowUI.cs:42` |
| `MinWindowHeight` | 150f | `UI/KerbalsWindowUI.cs:43` |
| `DefaultWindowWidth` | 410f (deliberately half of Career's 820 so they sit side by side, comment at `:44`-`:45`) | `UI/KerbalsWindowUI.cs:46` |
| `DefaultWindowHeight` | 400f | `UI/KerbalsWindowUI.cs:47` |
| First-open position | `mainWindowRect.x + mainWindowRect.width + 10`, `mainWindowRect.y`; seeded ONLY when `width < 1f` | `UI/KerbalsWindowUI.cs:184`-`:190` |
| Window layout options | `GUILayout.Width(rect.width)`, `GUILayout.Height(rect.height)` | `UI/KerbalsWindowUI.cs:211`-`:212` |
| Column width pins | NONE - this window has no `GUILayout.Width` call in any row | grep of the file |
| Content width options | only `GUILayout.ExpandWidth(true)` on the two fold buttons (`:457`, `:629`) and `GUILayout.ExpandHeight(true)` on the scroll view (`:345`) | as cited |
| Census rect | `[270, 8, 1000, 700]` | `harness/scenarios/GUI-1-census-ksc.toml:205` |
| Input lock | `ControlTypes.CAMERACONTROLS` under id `Parsek_KerbalsWindow`, taken on mouse-inside | `UI/KerbalsWindowUI.cs:41`, `:225`, released at `:241`-`:246` |

---

## 5.2 Parsek - Career State (window)

**(a) Name and kind.** `Parsek - Career State` - a draggable, resizable IMGUI window with a
four-entry tab toolbar, a one-line mode banner above the tabs, and four column-aligned tables.
No dialogs, overlays, markers or screen messages.

**(b) Class + draw method.**

| Piece | file:line |
| --- | --- |
| Class | `UI/CareerStateWindowUI.cs:20` |
| `DrawIfOpen(Rect mainWindowRect)` | `UI/CareerStateWindowUI.cs:1182` |
| `ClickThruBlocker.GUILayoutWindow`, title `"Parsek - Career State"` | `:1211`-`:1219` (title literal `:1215`, id `"ParsekCareerState".GetHashCode()` `:1212`) |
| Body delegate `DrawCareerStateWindow(int windowID)` | `:1311` |
| Mode banner `DrawModeBanner` | `:1401` |
| Tab 0 `DrawContractsTab` / header / row | `:1428` / `:1505` / `:1524` |
| Tab 1 `DrawStrategiesTab` / header / row | `:1534` / `:1610` / `:1629` |
| Tab 2 `DrawFacilitiesTab` / header / row | `:1639` / `:1666` / `:1681` |
| Tab 3 `DrawMilestonesTab` / header / row | `:1690` / `:1720` / `:1739` |
| VM builder `Build(...)` | `:335` |

**(c) Scenes + gate.** Same two hosts as Kerbals: FLIGHT (`ParsekFlight.cs:2135`) and
SPACECENTER (`ParsekKSC.cs:257`). Shown when `IsOpen` (`UI/CareerStateWindowUI.cs:160`,
which logs on every transition, `:167`-`:171`), toggled only from the Basic-hidden launcher
(`ParsekUI.cs:923`-`:930`, gate at `ParsekUI.cs:906`).

**(d) STRUCTURE top-down.**

1. `GUILayout.Space(5)` - `:1315`.
2. **Null-game fallback branch** (`:1319`-`:1333`): when `HighLogic.CurrentGame == null` the
   whole body is replaced by the banner label `Career state unavailable - game not loaded`
   (`:1324`), a `Close` button (`:1325`), the resize handle and `GUI.DragWindow()`. No tabs,
   no tooltip strip.
3. **Cache rebuild** gated by `ShouldRebuildCachedVM` (`:1338`, predicate at `:865`): rebuilds
   on null cache, mode change, transient fallback, backwards UT, a changed F0 UT string, or
   `liveUT >= NextRelevantActionUT`. Data sources are `EffectiveState.ComputeELS()` (`:1343`)
   and the four `LedgerOrchestrator` modules (`:1346`-`:1349`).
4. **Mode banner** (one label, no width pin) - `DrawModeBanner` (`:1401`), text per mode:
   - CAREER: `Career mode - UT {F0}` (`:1406`), plus `  (timeline ends at UT {F0})` appended
     when `vm.HasDivergence` (`:1407`-`:1410`).
   - SCIENCE_SANDBOX: `Science mode - contracts and strategies unavailable` (`:1414`).
   - SANDBOX / MISSION_BUILDER / MISSION: `Sandbox mode - career state is not tracked` (`:1419`).
5. **Tab bar** - `GUILayout.Toolbar(selectedTab, TabLabels, toggleButtonStyle)` at `:1363`,
   no width pin. Labels + tooltips at `UI/CareerStateWindowUI.cs:116`-`:126`:

   | idx | Label | Tooltip |
   | --- | --- | --- |
   | 0 | `Contracts` | `Contracts you hold now, and the ones your recorded flights still complete.` |
   | 1 | `Strategies` | `Strategies running now, and the ones the recorded timeline activates later.` |
   | 2 | `Facilities` | `KSC building levels now, and the levels the recorded timeline ends on.` |
   | 3 | `Milestones` | `First-time achievements your recorded flights claim, and what each one paid.` |

   On change: `SwitchTab` log (`:1984`) and `careerStateScrollPos.y = 0f` (`:1368`).
6. **Scroll view** `:1371`, ended `:1382`. Tab dispatch at `:1373`-`:1380`
   (`default:` falls back to Contracts, `:1379`).
7. **Footer 1 - tooltip echo strip**, `tooltipEcho.Draw()` at `:1388`. Constructed
   SINGLE-LINE at `UI/CareerStateWindowUI.cs:69`-`:70`
   (`new TooltipEchoBox(TooltipEchoBox.DefaultSpacing, TooltipEchoBox.SingleLine)`;
   constants at `UI/TooltipEchoBox.cs:65` and `:68`). Measured 23 px in the census json vs
   Kerbals' 38 px, which is the visible difference between the two footers.
8. **Footer 2 - `Close`** (`:1390`), sets `IsOpen = false` (which logs, `:167`).
9. **Resize handle** `:1395`; `GUI.DragWindow()` `:1398`.

### Tab 0 - Contracts

Column table. Header `DrawContractsColumnHeader` (`:1505`), row `DrawContractRow` (`:1524`).

| Col | Header label | Header tooltip | Width const | Width | Row formatter |
| --- | --- | --- | --- | --- | --- |
| 1 | `Contract` | none (bare string, `:1508`) | `ColW_ContractTitle` (`:83`) | 240f | `FormatContractRow_Title` (`:1785`) - title, else raw id, else `(unknown)` |
| 2 | `Accepted UT` | `When the contract was taken on, in Universal Time (the game's own clock).` (`:1510`-`:1511`) | `ColW_AcceptUT` (`:84`) | 90f | `FormatContractRow_Accept` (`:1794`) - F0 invariant |
| 3 | `Deadline UT` | `When the contract expires, in Universal Time (the game's own clock).` (`:1514`-`:1515`) | `ColW_DeadlineUT` (`:85`) | 90f | `FormatContractRow_Deadline` (`:1803`) - F0, or `--` when NaN |
| 4 | `Status` | `Whether this row is true now or still waiting on a recorded flight.` (`:1518`-`:1519`) | `ColW_PendingTag` (`:86`) | 70f | `FormatContractRow_Pending` (`:1813`) - `(pending)` / `(closing)` / empty |

Sort key: `CompareContractRowByAcceptUT` - `AcceptUT` ascending, then `ContractId` ordinal
(`:1017`-`:1019`), applied to BOTH lists at `:695`-`:696`. Not user-selectable.

Section header above the table: `Mission Control L{n} - slots {a}/{b} now, {c}/{d} at
timeline end` (`:1442`-`:1444`), max slots from `LedgerOrchestrator.GetContractSlots` (`:660`).

Two layouts, chosen by `RowsEqual(CurrentRows, ProjectedRows)` (`:1446`, predicate `:1753`):
- COLLAPSED (equal): one group `Active ({n})` (`:1449`) + header + boxed rows in
  `GUI.skin.label` (`:1455`).
- SPLIT (differ): `Active now ({n})` (`:1460`) + header + boxed rows, then a
  `Pending in timeline ({n})` disclosure toggle (`:1477`-`:1481`) and, when expanded, a second
  header + boxed rows in `pendingStyle` filtered to `IsPendingAccept` (`:1494`-`:1499`).

EMPTY STATES: `  (no active contracts)` (`:1453` collapsed, `:1464` split) when
`CurrentRows.Count == 0`; `  (none)` (`:1493`) when `pendingCount == 0` inside the expanded
pending group. Mode empty states: `Career state is not tracked in Sandbox mode.` (`:1432`) and
`Contracts are unavailable in Science mode.` (`:1437`).

### Tab 1 - Strategies

Header `DrawStrategiesColumnHeader` (`:1610`), row `DrawStrategyRow` (`:1629`).

| Col | Header label | Header tooltip | Width const | Width | Row formatter |
| --- | --- | --- | --- | --- | --- |
| 1 | `Strategy` | none (`:1613`) | `ColW_StrategyTitle` (`:88`) | 220f | `FormatStrategyRow_Title` (`:1839`) |
| 2 | `Activated UT` | `When the strategy was switched on, in Universal Time (the game's own clock).` (`:1615`-`:1616`) | `ColW_ActivateUT` (`:89`) | 90f | `FormatStrategyRow_Activate` (`:1844`) - F0 |
| 3 | `Flow` | `What the strategy converts into what, and at what commitment.` (`:1619`-`:1620`) | `ColW_Flow` (`:90`) | 140f | `FormatStrategyRow_Flow` (`:1853`) - `"{Source} -> {Target} @ {F1}%"` |
| 4 | `Status` | `Whether this row is true now or still waiting on a recorded flight.` (`:1623`-`:1624`) | `ColW_PendingTag` (`:86`) | 70f | `FormatStrategyRow_Pending` (`:1860`) - `(pending)` / `(closing)` / empty |

Sort key: `CompareStrategyRowByActivateUT` - `ActivateUT` ascending, then `StrategyId` ordinal
(`:1024`-`:1026`), applied at `:755`-`:756`.

Section header: `Administration L{n} - slots {a}/{b} now, {c}/{d} at timeline end`
(`:1548`-`:1550`), slots from `LedgerOrchestrator.GetStrategySlots` (`:717`-`:718`).
Collapsed/split layout identical to Contracts, switched by `StrategyRowsEqual` (`:1552`,
predicate `:1764`); disclosure key `GroupKey_StrategiesPending` (`:110`).

EMPTY STATES: `  (no active strategies)` (`:1559`, `:1570`); `  (none)` (`:1598`);
`Career state is not tracked in Sandbox mode.` (`:1538`);
`Strategies are unavailable in Science mode.` (`:1543`).

### Tab 2 - Facilities

Header `DrawFacilitiesColumnHeader` (`:1666`), row `DrawFacilityRow` (`:1681`).

| Col | Header label | Header tooltip | Width const | Width | Row formatter |
| --- | --- | --- | --- | --- | --- |
| 1 | `Facility` | none (`:1669`) | `ColW_FacilityTitle` (`:96`) | 200f | `FormatFacilityRow_Title` (`:1882`); display title is `SpaceBeforeCapitals(fid)` (`:786`, helper `:1151`) so `VehicleAssemblyBuilding` renders `Vehicle Assembly Building` |
| 2 | `Level` | `Upgrade level now, and the level the recorded timeline ends on.` (`:1671`-`:1672`) | `ColW_Level` (`:97`) | 120f | `FormatFacilityRow_Level` (`:1891`) - `L{n}` or `L{a} -> L{b} (upcoming)` |
| 3 | `Status` | `Whether this row is true now or still waiting on a recorded flight.` (`:1675`-`:1676`) | `ColW_Status` (`:98`) | 180f | `FormatFacilityRow_Status` (`:1903`) - `(destroyed)` / `(destroyed, repair pending)` / empty |

Sort key: NONE. Rows are emitted in the FIXED `FACILITY_DISPLAY_ORDER` list
(`UI/CareerStateWindowUI.cs:133`-`:144`), walked at `:771`-`:793`. Facilities with no ledger
action are merged in at L1/not-destroyed (`:776`, `:779`), so the tab always shows all nine.
Per-row style is `pendingStyle` when `HasUpcomingChange` else `GUI.skin.label` (`:1659`).

Section header: the bare string `Facilities` (`:1647`). No split/collapse layout, no
disclosure toggle - this is the only tab with no `Pending in timeline` group.

EMPTY STATES: `  (no facility data)` (`:1652`) when `Rows.Count == 0` (unreachable in CAREER
because of the merge-in default, reachable only when `facilitiesVisible == false`);
`Career state is not tracked in Sandbox mode.` (`:1643`). NOTE there is deliberately NO
Science-mode branch here - facilities ARE tracked in Science mode:
`ModeShowsFacilities` (`:962`) delegates to `ModeHasVisibleTimelineState` (`:972`), which
returns true for CAREER and SCIENCE_SANDBOX (`:974`), whereas `ModeShowsContracts` (`:952`)
and `ModeShowsStrategies` (`:957`) are CAREER-only.

### Tab 3 - Milestones

Header `DrawMilestonesColumnHeader` (`:1720`), row `DrawMilestoneRow` (`:1739`).

| Col | Header label | Header tooltip | Width const | Width | Row formatter |
| --- | --- | --- | --- | --- | --- |
| 1 | `Credited UT` | `When the milestone was reached, in Universal Time (the game's own clock).` (`:1724`-`:1725`) | `ColW_MilestoneUT` (`:92`) | 90f | `FormatMilestoneRow_UT` (`:1922`) - F0 |
| 2 | `Milestone` | none (bare string, `:1727`) | `ColW_MilestoneTitle` (`:93`) | 200f | `FormatMilestoneRow_Title` (`:1927`) |
| 3 | `Rewards` | `Funds, science and reputation this first-time achievement paid out.` (`:1729`-`:1730`) | `ColW_Rewards` (`:94`) | 180f | `FormatMilestoneRow_Rewards` (`:1936`) - `+ {F0} funds  + {F0} rep  + {F1} sci`, zero buckets elided |
| 4 | `Status` | `Whether this row is true now or still waiting on a recorded flight.` (`:1733`-`:1734`) | `ColW_PendingTag` (`:86`) | 70f | `FormatMilestoneRow_Pending` (`:1955`) - `(pending)` or empty |

Sort key: `CompareMilestoneRowByUT` - `CreditedUT` ascending, then `MilestoneId` ordinal
(`:1031`-`:1033`), applied at `:817`.

Section header: `Milestones ({n} credited / {m} at timeline end)` (`:1699`-`:1701`).
Per-row style `pendingStyle` when `IsPendingCredit` (`:1713`).

EMPTY STATES: `  (no milestones credited)` (`:1706`);
`Career state is not tracked in Sandbox mode.` (`:1694`). No Science-mode branch: milestones
are tracked in Science mode via `ModeShowsMilestones` (`:967`) -> `ModeHasVisibleTimelineState`
(`:972`-`:974`).

**(e) Action controls and backends.** This window is READ-ONLY over career state - nothing in
it writes to contracts, strategies, facilities or milestones.

| Control | file:line | Backend it calls | Disabled / hidden condition |
| --- | --- | --- | --- |
| Tab toolbar (4 entries) | `:1363` | `SwitchTab` (`:1984`) - log only; resets scroll | never hidden while a game is loaded; the whole toolbar is SKIPPED in the null-game fallback (`:1319`-`:1333`) |
| `Pending in timeline ({n})` toggle - Contracts | `:1477`-`:1481` | `ToggleSection(foldedGroups, GroupKey_ContractsPending)` (`:1996`) | ONLY drawn in the SPLIT layout, i.e. when `RowsEqual(CurrentRows, ProjectedRows) == false` (`:1446`-`:1458`). In the collapsed layout the toggle does not exist. |
| `Pending in timeline ({n})` toggle - Strategies | `:1582`-`:1586` | `ToggleSection(foldedGroups, GroupKey_StrategiesPending)` (`:1996`) | Only in the split layout, `StrategyRowsEqual == false` (`:1552`-`:1564`) |
| `Close` (normal path) | `:1390` | `IsOpen = false` (`:160`) | never hidden |
| `Close` (null-game fallback) | `:1325` | `IsOpen = false` | only drawn when `HighLogic.CurrentGame == null` |
| Resize handle | `:1395` and `:1329` | `ParsekUI.DrawResizeHandle` (`ParsekUI.cs:2801`) | drawn on both paths |

Whole-tab hides, with their exact conditions:

| Hidden content | Condition | file:line |
| --- | --- | --- |
| Contracts table | `mode == SANDBOX \|\| MISSION_BUILDER \|\| MISSION` | `:1430` |
| Contracts table | `mode == SCIENCE_SANDBOX` | `:1435` |
| Strategies table | `mode == SANDBOX \|\| MISSION_BUILDER \|\| MISSION` | `:1536` |
| Strategies table | `mode == SCIENCE_SANDBOX` | `:1541` |
| Facilities table | `mode == SANDBOX \|\| MISSION_BUILDER \|\| MISSION` | `:1641` |
| Milestones table | `mode == SANDBOX \|\| MISSION_BUILDER \|\| MISSION` | `:1692` |
| ENTIRE window body incl. tabs and tooltip strip | `HighLogic.CurrentGame == null` | `:1320` |

Read-only backends:

| Call | file:line | Backend |
| --- | --- | --- |
| `EffectiveState.ComputeELS()` | `:1343` | ELS routing (tombstoned ledger actions excluded) |
| `LedgerOrchestrator.Contracts / .Strategies / .Facilities / .Milestones` | `:1346`-`:1349` | the four resource modules, passed into `Build` (`:335`) |
| `LedgerOrchestrator.GetContractSlots(level)` | `:659`, `:660` | max-slot derivation for the Contracts section header |
| `LedgerOrchestrator.GetStrategySlots(level)` | `:717`, `:718` | same for Strategies |
| `ContractSystem.Instance.Contracts` title lookup | `:1087`-`:1096` (via `ResolveContractTitle` `:1052`) | live stock lookup, null-guarded; falls back to the raw id with a rate-limited log (`:1074`-`:1077`) |
| live strategy title lookup | `:1099`-`:1115` | same shape |

**(f) STATE VARIANTS and pictures.**

| Variant | Census label | Note |
| --- | --- | --- |
| Contracts, CAREER, collapsed layout, zero rows | `ksc-career-contracts-advanced` (17 nodes) | Banner `Career mode - UT 2054689`, section header `Mission Control L1 - slots 0/2 now, 0/2 at timeline end`, group `Active (0)`, all four column headers with their tooltips, and the `  (no active contracts)` empty row. Confirms widths 240/90/90/70 (header rects `x=284,528,622,716`). |
| Contracts, SPLIT layout with `Pending in timeline` toggle and amber pending rows | NO PICTURE | Needs `RowsEqual == false`, i.e. a career whose recorded timeline accepts or closes a contract after `liveUT`. |
| Contracts, `(closing)` status | NO PICTURE | Needs a contract active now but absent from the terminal snapshot (`:671`). |
| Contracts, `--` deadline | NO PICTURE | Needs a deadline-less contract row. |
| Contracts, Sandbox / Science empty text | NO PICTURE | Census save is CAREER. |
| Strategies, CAREER, collapsed, zero rows | `ksc-career-strategies-advanced` (17 nodes) | Header `Administration L1 - slots 0/1 now, 0/1 at timeline end`, `Active (0)`, `  (no active strategies)`. Widths confirmed 220/90/140/70 (`x=284,508,602,746`). |
| Strategies, POPULATED rows incl. the `Flow` `Src -> Tgt @ N.N%` cell | NO PICTURE | No strategy was ever active on this save. |
| Strategies, split layout / `(closing)` | NO PICTURE | Same reason. |
| Facilities, all nine rows at L1, no status | `ksc-career-facilities-advanced` (50 nodes) | Shows the full `FACILITY_DISPLAY_ORDER` and the `SpaceBeforeCapitals` humanisation (`Vehicle Assembly Building`, `Research And Development`, `Astronaut Complex`). Widths confirmed 200/120/180 (`x=284,488,612`). |
| Facilities, `L1 -> L3 (upcoming)` amber row | NO PICTURE | Needs a facility upgrade action later than `liveUT`. |
| Facilities, `(destroyed)` / `(destroyed, repair pending)` | NO PICTURE | Needs a destroyed-facility action; the Status column is empty on every photographed row. |
| Milestones, 25 populated rows, scrollbar live | `ksc-career-milestones-advanced` (143 nodes) | Best-populated capture in this section. Shows two-line `Rewards` wraps (`+ 13200 funds  + 2 rep  + 1.0 sci` at height 36, i.e. the 180f column is too narrow for a three-part reward and IMGUI wraps it), the `+ N rep` and `+ N.N sci` branches, and the vertical scroll slider node the other three tabs lack. Widths confirmed 90/200/180/70 (`x=284,378,582,766`). |
| Milestones, `(pending)` amber rows | NO PICTURE | All 25 rows are credited at or before `liveUT` on this save; the Status column is empty on every row. |
| Mode banner divergence suffix `(timeline ends at UT N)` | NO PICTURE | `vm.HasDivergence` was false on the census save (banner is the bare `Career mode - UT 2054689`). |
| Science-mode banner / Sandbox banner | NO PICTURE | Census save is CAREER. |
| Null-game fallback (`Career state unavailable - game not loaded`) | NO PICTURE | Reachable only mid-scene-transition. |
| Basic-mode appearance | NO PICTURE BY CONSTRUCTION | `UiSurface.MainButtonCareer` is `visibleInBasic = false` (`UI/UiComplexityMode.cs:182`/`:188`); the Advanced->Basic switch closes the window (`ParsekUI.cs:492`-`:497`). |
| FLIGHT-scene appearance | NO PICTURE | Drawn in flight by `ParsekFlight.cs:2135`; `GUI-2-census-flight.toml` never opens it. |

**(g) Sizing.**

| Quantity | Value | file:line |
| --- | --- | --- |
| `MinWindowWidth` | 520f | `UI/CareerStateWindowUI.cs:74` |
| `MinWindowHeight` | 200f | `UI/CareerStateWindowUI.cs:75` |
| `DefaultWindowWidth` | 820f | `UI/CareerStateWindowUI.cs:72` |
| `DefaultWindowHeight` | 400f | `UI/CareerStateWindowUI.cs:73` |
| First-open position | `mainWindowRect.x + mainWindowRect.width + 10`, `mainWindowRect.y`, seeded only when `width < 1f` | `:1190`-`:1196` |
| Window layout options | `GUILayout.Width(rect.width)`, `GUILayout.Height(rect.height)` | `:1217`-`:1218` |
| Column width pins | 240 / 90 / 90 / 70 (contracts, `:83`-`:86`); 220 / 90 / 140 / 70 (strategies, `:88`-`:90` + `:86`); 200 / 120 / 180 (facilities, `:96`-`:98`); 90 / 200 / 180 / 70 (milestones, `:92`-`:94` + `:86`). Header and row share the same constant in every case. | as cited |
| Other layout options | `GUILayout.ExpandWidth(true)` on the two pending toggles (`:1481`, `:1586`); `GUILayout.ExpandHeight(true)` on the scroll view (`:1371`) | as cited |
| Tooltip strip | single-line (23 px measured) vs Kerbals' two-line 38 px; the comment at `:66`-`:68` pins the reason to the 820 px first-open width | `:69`-`:70` |
| Census rect | `[270, 8, 1000, 700]` | `harness/scenarios/GUI-1-census-ksc.toml:217` |
| Input lock | `ControlTypes.CAMERACONTROLS` under id `Parsek_CareerStateWindow` | `:79`, taken `:1231`, released `:1263`-`:1268` |

---

## Cross-checks worth flagging

1. `ColW_PendingTag` (70f, `UI/CareerStateWindowUI.cs:86`) is shared by THREE tabs
   (contracts `:1520`/`:1530`, strategies `:1625`/`:1635`, milestones `:1735`/`:1745`) even
   though it is declared under the `// Contracts tab.` comment block. Editing it moves three
   tables.
2. The Milestones `Rewards` column at 180f demonstrably overflows: the census json shows the
   two three-part reward cells rendering at height 36 instead of 21
   (`Kerbin/ Escape` and `Kerbin/ Return From Orbit`), i.e. IMGUI wrapped them onto two lines
   inside a 21 px row grid. Formatter at `:1936`.
3. The Facilities tab is the only Career tab with no Science-mode branch and no pending
   disclosure group; that asymmetry is intentional (facilities and milestones ARE tracked in
   Science mode) but it means the Facilities tab has exactly one mode empty-state while
   Contracts and Strategies have two.
4. `FormatContractRow` / `FormatStrategyRow` / `FormatFacilityRow` / `FormatMilestoneRow`
   (`:1825`, `:1870`, `:1912`, `:1963`) are single-string formatters that NO production draw
   path calls; the four tab renderers all use the per-column helpers. They are retained
   test wrappers, per their own doc comments.

---

# Section 06 - Logistics (Supply Routes)

Scope: `UI/LogisticsWindowUI.cs` (3861 lines), `UI/RouteCreationDialog.cs`, `UI/LogisticsButtonState.cs`,
`UI/Logistics*Presentation.cs` (Countdown, Create, Delivery, Dormant, Flow, Hold, Link, Reject, Rename, Sort),
`UI/RouteWindowBasisPresentation.cs`, `Logistics/RouteRunPrompt.cs`, `Logistics/RouteCreationFormatters.cs`.
All paths relative to `Source/Parsek`. Evidence: `harness/results/2026-09-11_0548_GUI-1-census-ksc_shots/`,
labels `ksc-logistics-advanced` / `ksc-logistics-basic`.

## 06.0 Orientation and two corrections to the brief

**Correction 1 - only THREE of the six bubbles are expand-collapse.** The Active / Paused / Candidates
bubbles are drawn unconditionally with a plain centred title and no caret: `DrawRouteSectionBubble`
(`UI/LogisticsWindowUI.cs:624`) and `DrawCandidateSectionBubble` (`:737`) have no `expandedRows` test, and
the title comment at `:544-545` states "Titles are the plain section name (no count), centered". The census
confirms it - "Active Routes" / "Paused Routes" / "Candidates" are `[label]` nodes, not `[button]`. The three
that ARE caret disclosures with a count badge are Dormant Routes (`:675`), Recently-committed-trees-not-yet-eligible
(`:777`) and Dismissed (`:844`). Per-ROW expand (the `>`/`v` caret on the Name cell) is separate and exists on
route rows (`:979-981`) and candidate rows (`:1487-1489`).

**Correction 2 - `UiSurface.MainButtonLogistics` is never queried.** The enum member exists
(`UI/UiComplexityMode.cs:56`) and is classified `visibleInBasic = true` (`UI/UiComplexityMode.cs:173`), but a
repo-wide grep for `MainButtonLogistics` returns exactly those two hits - there is **no
`UiSurfaceVisibility.IsVisible(UiSurface.MainButtonLogistics, ...)` call site anywhere**. The Logistics button
is drawn unconditionally at `ParsekUI.cs:885-892`. The window content is therefore identical in both modes not
because the gate says keep, but because nothing asks the gate. Confirmed mechanically: the two census trees for
the Logistics window are byte-identical (58 nodes each, `identical True` on a structural diff of
kind/text/rect).

**Flight.** The window IS drawn in FLIGHT: `ParsekFlight.cs:2136` calls
`ui.DrawLogisticsWindowIfOpen(windowRect)`, mirrored by `ParsekKSC.cs:258` for SPACECENTER
(`ParsekUI.cs:1046`). It is absent from the flight census run only because that run did not open it. It is NOT
drawn in TRACKSTATION (no third call site).

---

## 06.1 Logistics window

**(a) Name and kind.** "Parsek - Logistics" - window (`ClickThruBlocker.GUILayoutWindow`).

**(b) Class + draw method.** `LogisticsWindowUI` (`UI/LogisticsWindowUI.cs:30`); host
`DrawIfOpen(Rect)` at `:413`, window body `DrawWindow(int)` at `:488`, window call at `:444` with title
`"Parsek - Logistics"` (`:448`).

**(c) Scenes + gate.** FLIGHT (`ParsekFlight.cs:2136`) and SPACECENTER (`ParsekKSC.cs:258`). Gate is the
`IsOpen` flag only (`:393-402`), toggled by the main-window "Logistics" button (`ParsekUI.cs:885-892`) or set
true by the Record-Supply-Run banner's "Open Logistics" (`ParsekUI.cs:836` (banner) / `ParsekUI.cs:889`). No data gate: an empty save opens
the full chrome (this is exactly what the census photographed). The setter also force-closes the link picker
(`:401`) so a re-open cannot resurrect stale picker state.

**(d) Structure top-down.**

| Band | Content | file:line |
|---|---|---|
| 1 | `GUILayout.Space(5)` | `:491` |
| 2 | scroll view (`ExpandHeight`) | `:541` |
| 3 | section bubble "Active Routes" | `:546` |
| 4 | section bubble "Paused Routes" | `:547` |
| 5 | disclosure bubble "Dormant Routes (N)" (conditional) | `:551` |
| 6 | section bubble "Candidates" + near-miss + dismissed disclosures | `:552` |
| 7 | end scroll view | `:554` |
| 8 | tooltip echo strip (permanent, single line) | `:560`, field `:378` |
| 9 | full-width "Close" button | `:564` |
| 10 | resize handle `//` + `GUI.DragWindow()` | `:571-572` |
| 11 | deferred mutations applied | `:575` -> `ApplyPendingActions` `:2426` |

Per-frame preamble before the scroll view: `TryGetCurrentUT()` (`:493`), the throttled candidate cache
(`:495` -> `GetCandidates` `:2885`), the throttled near-miss cache (`:498` -> `GetNearMisses` `:2935`),
click-outside commit of any open inline edit (`:505` -> `HandleLogisticsDefocus` `:588`), the throttled
per-route legibility recompute (`:510` -> `RefreshLegibilityCacheIfDue` `:2963`), the Paused-vs-rest split
(`:514-523`), and the frame-top reset of all 14 deferred-action fields (`:526-539`).

Three ~1 Hz caches feed everything and the draw path only READS them: candidates +
candidate run-cost + dismissed rows (`:2885`, interval const `:75`), near-misses (`:2935`), per-route
legibility (`:2963`, interval const `:113`). `RouteLegibility` struct at `:149-242`.

**(e) Window-level action controls.**

| Control | Backend | file:line | Disabled/hidden condition |
|---|---|---|---|
| Close (full width) | sets `showWindow=false`, `linkPickerOpen=false` | `:564-569` | never |
| Resize handle | `ParsekUI.DrawResizeHandle` | `:571` | never |
| Title-bar drag | `GUI.DragWindow()` | `:572` | never |
| Window input lock | `InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, "Parsek_LogisticsWindow")` | `:471`, id `:61` | acquired only while the mouse is inside `windowRect` (`:467`); released at `:481` |

**(f) State variants.** Photographed by `ksc-logistics-advanced` AND `ksc-logistics-basic` (identical): the
whole window chrome, three section headers, two route sort headers, both "(none)" rows, the candidate header
and its empty-state sentence, and the collapsed near-miss disclosure reading "(18)". No picture: any populated
route table, the Dormant bubble, the Dismissed bubble, any expanded row detail, the link picker, any of the
three dialogs. See 06.9 / APPENDIX-NOPICTURE.

**(g) Sizing facts.**

| Fact | Value | file:line |
|---|---|---|
| `MinWindowWidth` | 1410 | `:390` |
| `MinWindowHeight` | 500 | `:391` |
| First-open default rect | `x = mainWindowRect.x + mainWindowRect.width + 10`, `y = mainWindowRect.y`, `1556 x 500` | `:429-430` |
| Minimum enforced only during a resize drag | `ParsekUI.HandleResizeDrag(ref windowRect, ..., MinWindowWidth, MinWindowHeight, ...)` | `:435` |
| Rect persists across scene changes | `static Rect sessionWindowRect`, written `:460`, restored in ctor `:410` | `:59` |
| Rect writable from the seam | `WindowRectForTesting` (`op=rect`), seeded only when `width < 1` | `:45`, `:421` |
| Tooltip strip | `new TooltipEchoBox(SpacingSmall, TooltipEchoBox.SingleLine)` | `:378-379` |
| Spacings | `SpacingSmall = 3f`, `SpacingLarge = 8f` | `:381-382` |

Census rect was `[0, 8, 1280, 700]` - **below the window's own 1410 minimum**, which is legal because
`op=rect` writes the field directly and only a drag clamps. Consequence visible in the shot: content width
1324 > viewport 1260, so a horizontal scrollbar appears (`[slider] rect=[10,621,1260,15]`) and the
`ExpandWidth` Name column in the ROUTE header collapses to its content width of 60px
(`[button] rect=[52,85,60,23] text='Name ^'`).

---

## 06.2 Section: Active Routes

**(a)** Section (always drawn, no caret, no count badge) - a table bubble.

**(b)** `DrawRouteSectionBubble("Active Routes", activeRoutes, RouteSection.Active, currentUT)` at `:546`;
implementation `:624-644`; header `DrawSectionHeader` `:2385`; column header `DrawRouteSortableHeader` `:929`;
rows `DrawRouteRow` `:968`.

**(c)** Same scenes/gate as the window. Membership: every committed route whose `Status != Paused`, i.e.
Active, InTransit, the three blocked-but-flying waits, and the three hard-broken states (`:517-523`).

**(d) Column list (shared with Paused; `DrawRouteSortableHeader` `:929-944` and `DrawRouteRow` `:968-1132`
must add the same cells in the same order).**

| # | Column | Width const | Value | Sort key |
|---|---|---|---|---|
| 1 | `#` | `ColW_Num` 30 (`:343`) | display position in the sorted list | not sortable (`:933`) |
| 2 | Name | `ExpandWidth(true)` (`:934`) | `>`/`v` caret + `route.Name ?? "<unnamed>"`, a label-styled button (`:979-981`) | `Name` (`LogisticsSortPresentation.cs:120`) |
| 3 | Origin | `ColW_Origin` 95 (`:344`) | `FormatOrigin` `:3664` - "KSC (funds)" / "harvested en route" / `body (surface|orbit) lat,lon` | `Origin`, from `RouteSortKeys.OriginText` (`:3571`) |
| 4 | Destination | `ColW_Destination` 180 (`:345`) | cached `leg.DestinationText` (resolved vessel name, else coords); coords in tooltip (`:1002-1004`) | `Destination` |
| 5 | Interval | `ColW_Interval` 150 (`:349`) | inline `[-] field [+] Nx` stepper - `DrawIntervalCell` `:1163`; **empty cell of the same width while Send-Once-armed** (`:1012-1020`) | `Interval` (`route.DispatchInterval`) |
| 6 | Cyc | `ColW_Cycles` 80 (`:352`) | `FormatCycleCount` `:3738` - "3" or "3 / 1 skipped" | `Cycles` |
| 7 | Next | `ColW_NextDelivery` 135 (`:354`) | `NextDeliveryCellText` `:3586` - bare `T-...` countdown or "-" | `NextDelivery`, seconds + `HasNextDelivery` |
| 8 | Status | `ColW_Status` 240 (`:358`) | `leg.HoldCellText ?? StatusReason(route.Status)` (`:1073`), colour by `StatusStyleFor` `:3813`, forced yellow when a hold overrides a green status (`:1070-1071`) | `Status`, mirrors the visible text (`:3565-3567`) |
| 9 | Delivery | `ColW_Badge` 120 (`:359`) | `LogisticsDeliveryPresentation.DeliveryBadgeLabel(leg.Badge)` - Delivering / Flying, not delivering / New (not yet run) / Paused | `Delivery` |
| 10 | Actions | `ColW_Actions` 190 (`:360`) | fixed-width cell, see (e) | not sortable (`:942`) |

There is NO Transit column: dropped in the L2 narrowing; the transit value now lives only in the Interval
cell's `Nx` tooltip and the expanded detail line (`:1023-1025`, `:352`-adjacent comment `:350-351`).

Empty state: `GUILayout.Label("  (none)", detailStyle)` (`:632`).

**Sort mechanics.** Shared `routeSortColumn` / `routeSortAscending` across Active AND Paused (`:118-119`);
a header click re-sorts both. Header cells route through `parentUI.DrawSortableHeaderCore`
(`DrawRouteSortColumn` `:951-964`), whose `onChanged` callback zeroes both cached counts and Verbose-logs
(`:959-962`). Re-sort is cached per section and happens only when the row count, the sort tuple, or that
section's legibility stamp changed (`GetSortedRoutesForSection` `:3497-3540`; per-section stamps `:140-141`,
the reason for two stamps is spelled out at `:136-139`). Comparer is pure
(`LogisticsSortPresentation.SortRoutes`, `UI/LogisticsSortPresentation.cs:77`), keys projected at
`BuildRouteSortKeys` `:3550`.

**(e) Action controls in the Actions cell.**

| Control | Width | Shown when | Backend | file:line |
|---|---|---|---|---|
| disabled "Sending one cycle..." / "Pausing after this cycle..." | 160 | `ShouldShowSendingButton(route)` (`:1344`): `PauseAfterCurrentCycle` AND status in {Active, InTransit, WaitingForResources, WaitingForFunds, DestinationFull} | none (inert) | `:1088-1109` |
| Pause | 58 | Active section, not armed | `pendingPause` -> `RouteOrchestrator.TryPause` | `:1113-1114`, `:2443` |
| X (delete) | 22 | always | `pendingConfirmDeleteRoute` -> confirm dialog | `:1127-1128`, `:2471` |

The armed label is resolved by `ResolveArmedKind(sendOnceArmed, status)` (`:1431`) - the UI-side provenance
set `sendOnceArmedRouteIds` (`:256`) is authoritative, `ClassifyArmedSend` (`:1415`) is the post-reload status
heuristic. Labels `LabelForArmedState` `:1441`; tooltips `TooltipForArmedState` `:1459`. The disabled button
publishes its tooltip through `DisabledHoverEcho.CarryLastControl(false, ...)` (`:1108`).

**Interval cell controls** (`DrawIntervalCell` `:1163-1272`):

| Control | Width | Disabled condition + exact reason string | file:line |
|---|---|---|---|
| `-` | 20 | `GUI.enabled = !atFloor` where `atFloor = n <= 1`; reason `"Already at the minimum (1x = the fastest the run allows)"` | `:1174-1181` |
| text field (interval) | 64 | suppressed from arming while a rename is active (`editingThis` requires `renamingRouteId == null`, `:1166-1167`); cleared when the cell is hidden by Send-Once (`:1017-1018`) | `:1193-1239` |
| `+` | 20 | never disabled; tooltip "Dispatch less often" | `:1241-1245` |
| `Nx` readout | 28 | label only; tooltip from `LogisticsIntervalPresentation.BuildNxCellTooltip` (`Logistics/LogisticsIntervalPresentation.cs:24`) | `:1263-1267` |
| basis label (windowed only) | `ExpandWidth(false)` | drawn only when `windowed && BasisLabel` non-empty | `:1268-1269` |

Field commit: Enter (`:1227-1231`) or click-outside (`HandleLogisticsDefocus` `:600-607`) -> `CommitIntervalEdit`
`:1286` -> `RouteCadence.ParseAndSnapInterval` then `RouteCadence.ApplyMultiplier` run DIRECTLY (not via a
deferred field). Escape cancels (`:1232-1238`). Reject path Warn-logs and leaves the route unchanged
(`:1300-1304`). Display value `FormatIntervalFieldValue` `:1325` (`"0"` for a non-positive interval).

**(f) State variants.** Photographed (both labels): the EMPTY table - header row plus `  (none)`, with the
Name header reading `Name ^` (default sort Name ascending, `:118-119`). No picture for: any populated row, the
Send-Once-armed row (Interval cell blank + 160px disabled button), a held row (yellow Status cell carrying
`Held: <reason>`), a hard-broken red row, the four badge colours, any non-default sort direction.

**(g) Sizing.** Section box is `GUILayout.BeginVertical(GUI.skin.box)` (`:627`) with a trailing
`GUILayout.Space(SpacingSmall)` (`:643`). Header bar is a box-in-box with a centred clone of the shared
section-header style (`:2391-2408`). Fixed columns total **1220px** (30+95+180+150+80+135+240+120+190), which
makes the `MinWindowWidth` comment at `:383-389` stale - it still totals the old 90px Next column and claims
~1175.

---

## 06.3 Section: Paused Routes

**(a)** Section (always drawn, no caret, no count badge).

**(b)** `DrawRouteSectionBubble("Paused Routes", pausedRoutes, RouteSection.Paused, currentUT)` at `:547`;
same `:624` implementation and same `DrawRouteRow` `:968`.

**(c)** Membership: `r.Status == RouteStatus.Paused` (`:521`).

**(d)** Identical ten columns to Active (same header method `:929`), with two Paused-only differences:

1. **Status cell** shows the L1 never-run classification instead of the generic reason:
   cyan `New (not yet run)` when `CompletedCycles == 0`, grey `Paused` otherwise
   (`:1053-1062`; classifier `LogisticsDeliveryPresentation.ClassifyPausedRoute`, text
   `PausedRouteLabelText` at `UI/LogisticsDeliveryPresentation.cs:388`, computed on the ~1 Hz pass at
   `:3095-3105`). Style picked at `:1055-1057`.
2. **Guidance line**: a full-width line under a never-run row, drawn whether or not the row is expanded -
   `LogisticsDeliveryPresentation.SendOnceGuidanceText` = `"New - use Send Once to test"`
   (`UI/LogisticsDeliveryPresentation.cs:366`), with tooltip "This route has never run. Use Send Once to fire
   one test cycle without activating periodic dispatch." (`:1139-1143`). Rendered OUTSIDE the column row so
   the header/row control counts stay aligned (`:1134-1138`).

**(e) Action controls.**

| Control | Width | Backend | file:line |
|---|---|---|---|
| Send Once | 79 | `pendingSendOnce` -> `RouteOrchestrator.TrySendOneCycleNow`; on success adds the id to `sendOnceArmedRouteIds` | `:1118-1121`, `:2456-2463` |
| Activate | 64 | `pendingActivate` -> `RouteOrchestrator.TryActivate(route, currentUT)` | `:1122-1125`, `:2451-2455` |
| X (delete) | 22 | `pendingConfirmDeleteRoute` -> confirm dialog | `:1127-1128` |

Send Once tooltip: "Fire one cycle at the next moment conditions allow (funds, resources, endpoint,
alignment), then stay Paused." (`:1119`). Activate tooltip: "Turn on periodic auto-dispatch on this route's
interval." (`:1123`). A Paused route can also show the disabled armed button if `PauseAfterCurrentCycle` is
set while the status is one of the five in `ShouldShowSendingButton` - but Paused is explicitly NOT in that
set (`:1357-1360`), so a Paused row always shows the live pair.

The Send-Once RESULT is a ScreenMessage, not a UI surface: four messages built by
`Logistics/RouteSendOncePresentation.cs` (`BuildDeliveredMessage:48`, `BuildCycleDeliveredMessage:69`,
`BuildAlreadyDeliveredMessage:89`, `BuildBlockedMessage:107`, duration `ToastSeconds = 5f` at `:40`) and
posted from `Logistics/RouteOrchestrator.cs:3860,3957,4180,4575` (outside this section's files). The blocked
message reuses `LogisticsHoldPresentation.DescribeHold` verbatim so the toast and the detail panel cannot
disagree (`Logistics/RouteSendOncePresentation.cs:118-120`).

**(f)** Photographed (both labels): the EMPTY table. No picture for a populated Paused row, the cyan
`New (not yet run)` cell, or the guidance line. `harness/fixtures/saves/interbody-route-recorded` carries
exactly this state (route `Route: KSC -> Mun`, `status = Paused`, `completedCycles = 0`).

**(g)** Same sizing as 06.2.

---

## 06.4 Section: Dormant Routes (N)

**(a)** Section - collapsed-by-default caret disclosure with a count badge.

**(b)** `DrawDormantSectionBubble()` at `:551`; implementation `:675-721`; section key
`DormantSectionKey = "dormant:section"` (`:649`).

**(c)** Same scenes as the window. Gate: `LogisticsDormantPresentation.ShouldShowDormantSection(count)` -
`count > 0` (`UI/LogisticsDormantPresentation.cs:24`), read from `RouteStore.DormantRoutes` (`:677`). With no
dormant route the whole bubble renders nothing (`:678-679`) - which is why it is absent from the census.

**(d) Structure.** Header = a label-styled button, `v`/`>` + `DormantSectionTitle(N)` = `"Dormant Routes (N)"`
(`UI/LogisticsDormantPresentation.cs:32`), tooltip `DormantSectionTooltip` (`:658-659`). The
`GUIContent` is cached and rebuilt only when the count or expanded state changes (`:655-657`, `:683-693`).
Row model when expanded: 24px indent, one full-width label `"{display} - {appears}"`, then a Delete button.
`display` = `DormantRouteDisplayName(route.Name, route.Id)` (name -> short id -> `<unnamed>`,
`UI/LogisticsDormantPresentation.cs:42`); `appears` = `DormantAppearsLabel(route.CreatedUT, SafePrintDateCompact(...))`
= `"appears at <date>"` or `"appears at <unknown>"` (`UI/LogisticsDormantPresentation.cs:61`; date formatter
guarded at `:730-735`). No columns, no sort, no expand - the comment at `:670-672` states a dormant route
cannot be activated, edited or expanded because it does not exist on the timeline yet.

**(e)**

| Control | Width | Backend | file:line |
|---|---|---|---|
| header caret | `ExpandWidth(true)` | `ToggleExpanded(DormantSectionKey, "dormant section")` | `:696-697` |
| Delete | 60 | `pendingConfirmDeleteDormantRoute` -> dormant confirm dialog (06.8) | `:712-715`, `:2479` |

Delete tooltip: "Delete this dormant route. It will never re-materialize." (`:713`). Nothing else is
conditionally disabled here.

**(f)** NO PICTURE in any census label - the save had no dormant routes. A dormant route is created only by
rewinding past a route's `CreatedUT`; no committed fixture carries a `DORMANT` node (a grep of
`harness/fixtures/saves/*/persistent.sfs` finds `ROUTES`/`ROUTE` only in `depot-route-recorded` and
`interbody-route-recorded`, neither dormant).

**(g)** Bubble is `BeginVertical(GUI.skin.box)` `:681` + trailing `Space(SpacingSmall)` `:720`; header indent
8px (`:695`), row indent 24px (`:710`).

---

## 06.5 Section: Candidates

**(a)** Section (always drawn, no caret, no count badge) - a table bubble with its own purpose-built header.

**(b)** `DrawCandidateSectionBubble("Candidates", candidates, nearMisses)` at `:552`; implementation
`:737-760`; header `DrawCandidateColumnHeader` `:905-920`; rows `DrawCandidateRow` `:1474-1552`.

**(c)** Same scenes as the window. Rows derive from `RouteCandidateFinder.DeriveCandidates()` on the ~1 Hz
timer (`:2891`) - fully-sealed, analysis-eligible, not-already-promoted, not-dismissed trees. It is
**independent of the route sort machinery** (`:895-904`): no sortable headers here.

**(d) Column list** (`DrawCandidateColumnHeader` `:905-919`, mirrored by `DrawCandidateRow` `:1483-1547`):

| # | Column | Width const | Value |
|---|---|---|---|
| 1 | `#` | `ColW_Num` 30 (`:343`) | row index |
| 2 | Name | `ExpandWidth(true)` (`:910`) | caret + `RouteCreationFormatters.GenerateDefaultRouteName(analysis, tree)` (`:1481`; formatter `Logistics/RouteCreationFormatters.cs:453`, "Route: origin -> endpoint" trimmed to 40 chars) |
| 3 | Origin | `ColW_Origin` 95 (`:911`) | `FormatCandidateOrigin` `:3674` - "KSC (funds)" / "depot ..." / "harvested en route" / "-" |
| 4 | Destination | `ColW_Destination` 180 (`:912`) | `FormatEndpointShort(analysis.ConnectionWindow?.EndpointAtDock)` (`:1492`, formatter `:3696`) |
| 5 | Would deliver | `ColW_WouldDeliver` 260 (`:368`) | `LogisticsDeliveryPresentation.FormatWouldDeliver(resources, inventory)` + optional `LogisticsCostPresentation.FormatCandidateSuffix` (`:1504-1520`) |
| 6 | Transit | `ColW_CandidateTransit` 80 (`:372`) | `FormatDuration(CandidateTransit(candidate))` (`:1527`; span helper `:3654` -> `RouteCreationDialog.ComputeRootToUndockSpan` `UI/RouteCreationDialog.cs:184`) |
| 7 | Actions | `ColW_Actions` 190 (`:918`) | see (e) |

Empty state is a full sentence, not "(none)": "No eligible Supply Runs. Fly a one-way transport that docks,
transfers cargo to the destination, and undocks, then commit and seal the recording." (`:743`).

**(e)**

| Control | Width | Backend | Conditional |
|---|---|---|---|
| Create Route | 100 | `pendingCreate` -> `SpawnCreateRouteConfirmation` (06.8) | always | `:1530-1533` |
| Dismiss | 70 | `pendingDismissTreeId/Label` -> `RouteStore.DismissCandidateTree` | always | `:1538-1544`, `:2516` |

Create Route tooltip: "Promote this Supply Run to a stored route (created Paused; use Send Once to test, then
Activate)." (`:1531`). Dismiss tooltip: "Hide this tree from the Candidates section (it is not meant to become
a route). Restore it any time from the Dismissed list below." (`:1539`). The run-cost suffix on the Would-deliver
cell appears **only** when `candidateRunCostCache` has an entry AND `Applicable && CostKnown` (Career + KSC
origin with a resolvable launch cost) - a miss leaves the cell and its base tooltip byte-identical
(`:1509-1520`); cost computed off the draw path at `ComputeCandidateRunCost` `:3214`.

**(f)** Photographed (both labels): header row with the "Would deliver" header tooltip intact, plus the empty
sentence. No picture for any candidate row, the run-cost suffix, or the expanded candidate detail.

**(g)** Candidate fixed columns total 835px, so at the census's 1280px width the Name column still expands
(observed `[label] rect=[52,287,457,23] text='Name'`) unlike the squeezed route header.

### 06.5.1 Sub-disclosure: Recently committed trees not yet eligible (N)

**(a)** Section - caret disclosure inside the Candidates bubble, collapsed by default.
**(b)** `DrawNearMissSubsection(nearMisses)` at `:751`; implementation `:777-826`; key
`NearMissSectionKey = "nearmiss:section"` (`:763`).
**(c)** Renders nothing when the cached near-miss list is empty (`:779-780`). List built on the ~1 Hz timer
by `RouteCandidateFinder.DeriveNearMisses()` (`:2941`).
**(d)** Header button text `"{arrow} Recently committed trees not yet eligible ({count})"` with tooltip
"Committed trees that are not Supply Run candidates yet, with the reason: not fully sealed, or sealed but the
run does not match the dock-deliver-undock proof." (`:787-791`). Row model when expanded: 24px indent, label
`"{treeName} - {reason}"`, optional Dismiss button. `treeName` = `NearMissTreeLabel` `:884` (TreeName -> short
id -> `<unnamed>`); `reason` = `LogisticsRejectPresentation.DescribeNearMiss(status, notSealed, reflyableCount,
rejectDetail)` (`UI/LogisticsRejectPresentation.cs:55`), which returns
`"not fully sealed (N recording(s) still re-flyable)"` for an unsealed tree and otherwise
`RouteCreationFormatters.FormatRejectMessage` (`Logistics/RouteCreationFormatters.cs:266-305`, ten
player-facing reject sentences).
**(e)** Dismiss (width 70) drawn only when `nm.Tree?.Id` is non-empty (`:816-819`) - a per-row condition the
comment at `:808-811` notes is stable within a frame because the list is cached. Same tooltip as the candidate
Dismiss.
**(f)** Photographed COLLAPSED in both labels: `> Recently committed trees not yet eligible (18)` at
`rect=[26,342,1308,21]`. The 18 expanded rows have NO picture.
**(g)** Header indent 8px (`:786`), row indent 24px (`:813`), preceded by `Space(SpacingSmall)` (`:784`).

### 06.5.2 Sub-disclosure: Dismissed (N)

**(a)** Section - caret disclosure inside the Candidates bubble, collapsed by default.
**(b)** `DrawDismissedSubsection()` at `:756`; implementation `:844-880`; key
`DismissedSectionKey = "dismissed:section"` (`:830`).
**(c)** Renders nothing when `cachedDismissedRows` is empty (`:846-847`). Rows rebuilt inside `GetCandidates`
on the ~1 Hz refresh from `RouteStore.DismissedCandidateTreeIds`, label-resolved and sorted by label then id
(`:2913-2924`).
**(d)** Header `"{arrow} Dismissed ({count})"`, tooltip "Trees you dismissed as route candidates. A dismissed
tree is hidden from the candidates and near-miss lists; Restore brings it back." (`:854-858`). Row: 24px
indent, label, Restore button.
**(e)** Restore (width 70, tooltip "Offer this tree as a Supply Run candidate again.") -> `pendingRestoreTreeId`
-> `RouteStore.RestoreCandidateTree` (`:871-877`, `:2528`). On success both derivation caches are dirtied so
the row moves immediately (`:2532-2536`).
**(f)** NO PICTURE - the census save had nothing dismissed.
`harness/fixtures/saves/interbody-route-recorded` carries `DISMISSED_ROUTE_CANDIDATES` with two `treeId`
entries, so it would photograph a `Dismissed (2)` header and two Restore rows.
**(g)** Same 8/24px indents as 06.5.1.

---

## 06.6 Row detail panel (route) - expanded

**(a)** Section - per-row expand panel (not a separate window).
**(b)** `DrawRouteDetail(route, currentUT)` at `:1146` -> `:1554-1670`, inside
`BeginVertical(GUI.skin.box)` (`:1556`).
**(c)** Shown when `expandedRows` contains the route id (`:972`, `:1145`); toggled by clicking the Name cell
(`:980-981` -> `ToggleExpanded` `:2412`).
**(d) Line order.**

| Line | Condition | file:line |
|---|---|---|
| Name row (rename + 3 buttons) | always | `:1561` -> `DrawRouteRenameRow` `:1983` |
| `Delivers per cycle: ...` | always | `:1563-1564` |
| `Status: {enum} - {StatusReason}` | always | `:1565` |
| `Last cycle blocked: ... (checked X ago)` (yellow) | `leg.HoldText != null` | `:1572-1573` |
| `Last delivery was partial: ... (X ago)` (yellow) | `leg.PartialText != null` | `:1579-1580` |
| `This route owns tree '...'; manual looping is disabled while it exists.` | route resolves a source tree | `:1587` -> `:1682` |
| `Round-trip linked to '...' ...` | `route.LinkedRouteId` non-empty | `:1592` -> `:1700` |
| capacity context (yellow) | `Status == DestinationFull && CapacityContext` non-empty | `:1598-1599` |
| endpoint re-scan row | `Status == EndpointLost` | `:1604-1605` -> `:1871` |
| `Next delivery ...` / `Next launch window ...` / `Rechecks in ...` | branch != None | `:1612-1615` |
| `Last cycle: ...` + `Total delivered: ...` | `leg.HasDeliveries` | `:1620-1625` |
| `Recent cycles:` + up to 5 indented cycle lines | `leg.FlowLines` non-empty | `:1635-1644` |
| `Interval: ... {basis}   Transit: ...   Cycles: N` | always | `:1648-1649` |
| `Cost/run: ... (launch X - recovered Y)` | `RunCost.Applicable && CostKnown` | `:1656-1661` |
| Cadence stepper | always | `:1663` -> `:2210` |
| Priority stepper | always | `:1664` -> `:2262` |
| `Source recordings: ...` | always | `:1668` -> `:2154` |

**(e) Controls.**

| Control | Width | Backend | Disabled/hidden condition + reason string |
|---|---|---|---|
| Rename | 104 (`RouteDetailButtonWidth` `:357`) | arms the rename field; clears any interval edit first | shown only when not editing this route (`:1992-1995`) |
| Log (Route) | 104 | `parentUI.OpenStructureWindowForRoute(route.Id, route.Name)` | never disabled (`:2012-2019`) |
| Log (Mission) | 104 | `parentUI.OpenStructureWindowForMission(sourceTreeId, ...)` | `GUI.enabled = hasSourceMission`; reason `MissionLogButtonDisabledReason` `:1380` = "This route was not built from a recorded mission" (`:2021-2034`) |
| Link round-trip... | 104 | `OpenLinkPicker(route, mousePos)` `:1719` | drawn only when `LinkedRouteId` is empty (`:2044-2052`) |
| Unlink | 104 | `RouteStore.UnlinkRoute(route.Id)` inline | drawn only when linked (`:2053-2064`) |
| Cadence `-` | 24 | `pendingCadenceRoute` + `RouteCadence.StepMultiplier(n,-1)` | `atFloor = n <= 1`; reason "Already at the minimum (1x = the fastest the run allows)" (`:2226-2238`) |
| Cadence `+` | 24 | `RouteCadence.StepMultiplier(n,+1)` | never (`:2247-2251`) |
| Priority `-` | 24 | `pendingPriorityRoute` + `RoutePriority.Step(p,-1)` | `atFloor = p <= 0`; reason "Already at the highest priority (0)" (`:2274-2286`) |
| Priority `+` | 24 | `RoutePriority.Step(p,+1)` | never (`:2290-2294`) |
| Re-scan for endpoint | 180 | `RouteEndpointResolver.TryResolveEndpoint`, clears `NextEligibilityCheckUT` on success | enabled only when `ShouldOfferEndpointRescan(status, endpoint)` (surface endpoint with a body, `UI/LogisticsDeliveryPresentation.cs:617`); otherwise a DISABLED button plus a visible note, both carrying `RescanIneligibleReason` (`UI/LogisticsDeliveryPresentation.cs:631`) (`:1881-1919`) |

Rename commit: Enter (`:2083-2087`) or click-outside (`:595-599`) -> `CommitRouteRename` `:2109`, routed
through the pure `LogisticsRenamePresentation.ComputeRouteRename` (trim + empty-guard + unchanged-guard,
`UI/LogisticsRenamePresentation.cs:34`); Escape cancels (`:2088-2094`). A committed rename writes
`Route.Name` directly (`:2121`) and dirties the legibility cache so a Name sort re-orders at once (`:2125`).
Control name is the CONSTANT `"LogiRouteRename"` (`:1985`) - one rename at a time by construction, unlike the
per-route interval control name (`:1165`).

Cadence readout width switches on the basis: 110px flat (`FormatCadence` `:3714`, "Nx (~14.0m)") vs 170px
windowed (`RouteWindowBasisPresentation.FormatWindowedCadence` `UI/RouteWindowBasisPresentation.cs:56`,
"2x (every 2nd window)") - `:2243-2245`. Priority readout is a fixed 110px label (`:2288`). Both stepper
labels are 70px (`:2223`, `:2271`).

**(f)** NO PICTURE for any part of this panel. Cheapest fixture: `interbody-route-recorded` (two routes), but
there is **no seam op that expands a row** - see APPENDIX-NOPICTURE.

**(g)** Every detail line is `DetailLine` (`:2359`/`:2366`/`:2377`): `BeginHorizontal` + `Space(24f)` +
`ExpandWidth(true)` label. Panel is a nested `GUI.skin.box`.

## 06.6.1 Row detail panel (candidate) - expanded

**(b)** `DrawCandidateDetail(candidate)` at `:1551` -> `:2300-2330`.
**(d)** Three or four lines, no controls at all: `Would deliver per cycle: ...` (`:2303`), `Transit: ...`
(`:2304`), `Cost/run: ...` when `Applicable && CostKnown` (`:2311-2319`), `Source recording: ...` with the short
id in the tooltip (`:2324-2328`, builder `:2342`). Comment at `:2306-2310` notes this is the ONE pre-creation
surface carrying the launch/recovered split.
**(f)** NO PICTURE.

---

## 06.7 Link picker window (round-trip partner)

**(a)** Window - its own `ClickThruBlocker.GUILayoutWindow`, NOT a nested one (a nested `GUILayoutWindow` is
illegal; the comment at `:315-320` names the GroupPickerUI idiom it mirrors).

**(b)** `LogisticsWindowUI.DrawLinkPicker()` (`:1741`), window call `:1762` with title
`"Link round-trip partner"` (`:1766`); body `DrawLinkPickerContents(int)` `:1784-1851`. Armed by
`OpenLinkPicker` `:1719`.

**(c)** Drawn from `DrawIfOpen` AFTER the host window (`:465`), so it exists only while the Logistics window
is open. Gate: `linkPickerOpen` (`:1743`), set true only by the detail-panel "Link round-trip..." button
(`:2050`). Force-cleared when the window closes (`:401`, `:567`).

**(d) Structure.** Header label `"Link '{sourceName}' with:"` (`:1787`); scroll view (`:1793`); either the
empty sentence "No eligible routes. A partner must be another route that is not already linked." (`:1796-1798`)
or one `GUILayout.Toggle` per candidate, label `"  " + c.Name`, single-select by manual de-selection
(`:1802-1811`); candidates from the pure `LogisticsLinkPresentation.BuildLinkCandidates(CommittedRoutes,
sourceId)` (`UI/LogisticsLinkPresentation.cs:36`) - other routes not already linked. Footer is a centred
`Link` / `Cancel` pair between two `FlexibleSpace`s (`:1816-1847`). No columns, no sort.

**(e)**

| Control | Width | Backend | Disabled condition + reason |
|---|---|---|---|
| Link | 70 | `RouteStore.LinkRoutes(src, sel)` inline, then `lastLegibilityComputeRealtime = -1f` | `GUI.enabled = !string.IsNullOrEmpty(linkPickerSelectedId)`; reason `LinkButtonDisabledReason` `:1368` = "Pick a route above to pair this one with" (`:1819-1838`) |
| Cancel | 70 | closes, Verbose-logs | never (`:1840-1844`) |
| resize handle / drag | - | `ParsekUI.DrawResizeHandle`, `GUI.DragWindow()` | never (`:1849-1850`) |

Link tooltip: "Pair these two routes as a round-trip: they alternate, each dispatching only after its partner
completes a run." (`:1822-1823`).

**(f)** NO PICTURE, and it is a DELIBERATE census exclusion: `TestCommands/TestCommandUiAction.cs:373-376`
names the link picker as one of three windows excluded because it "is armed from a Logistics ROW and carries
that row's source state". The `UiAction` handler for `LogisticsWindow`
(`TestCommands/ParsekTestCommandAddon.UiAction.cs:636-641`) exposes only `IsOpen` and
`WindowRectForTesting` - no picker accessor.

**(g)** `LinkPickerMinW = 240f`, `LinkPickerMinH = 180f` (`:329-330`); first-open rect is 340x380 clamped to
the screen at the arming mouse position (`:1748-1754`); resize enforced only during a drag (`:1745-1746`).

---

## 06.8 Dialogs (three)

All three are stock `PopupDialog.SpawnPopupDialog` + `MultiOptionDialog` centred at (0.5, 0.5) with
`HighLogic.UISkin`, spawned once from `ApplyPendingActions` on the click frame; the destructive work runs in
the BUTTON CALLBACK, never in `ApplyPendingActions` (the frame-reset-field trap is documented at
`:2565-2567` and `:2690-2697`).

### 06.8.1 Confirm: Delete Route

**(a)** Dialog (modal). **(b)** `SpawnDeleteRouteConfirmation(Route)` `:2569`, `PopupDialog` at `:2582`,
dialog name `"ParsekLogisticsDeleteRouteConfirm"` (`:2586`), title `"Confirm: Delete Route"` (`:2588`).
**(c)** Raised by the `X` button on any Active or Paused row (`:1127-1128` -> `:2465-2472`).
**(d)** Body from the pure `BuildDeleteConfirmBody` `:2673`: `"Delete route '{name}'?\n\nThis cannot be
undone."`, name falling back to the 8-char short id (`ShortId` `:3766`).
**(e)** `Delete` -> `RouteStore.RemoveRoute(routeId)` (`:2590-2595`); `Cancel` -> Verbose log only
(`:2596-2600`). The id is captured into a LOCAL so the closure survives the frame-top field reset (`:2574-2576`).
**(f)** NO PICTURE. **(g)** Stock dialog sizing.

### 06.8.2 Confirm: Delete Dormant Route

**(a)** Dialog (modal). **(b)** `SpawnDeleteDormantRouteConfirmation(Route)` `:2611`, `PopupDialog` at `:2623`,
dialog name `"ParsekLogisticsDeleteDormantRouteConfirm"` (`:2627`), title `"Confirm: Delete Dormant Route"`
(`:2629`).
**(c)** Raised by the Dormant section's Delete button (`:715` -> `:2473-2480`).
**(d)** Body from `LogisticsDormantPresentation.BuildDeleteDormantConfirmBody(display)`
(`UI/LogisticsDormantPresentation.cs:73`): `"Delete dormant route '{name}'?\n\nIt will never re-materialize
when the timeline reaches its creation point. This cannot be undone."`
**(e)** `Delete` -> `RouteStore.RemoveDormantRoute(routeId)` (`:2633`). Race handling: if the removal fails but
the route is now in the committed list, a ScreenMessage tells the player where it went (`:2641-2657`) - the
one screen message authored in this file's dialog path. `Cancel` -> Verbose only (`:2659-2663`).
**(f)** NO PICTURE. **(g)** Stock dialog sizing.

### 06.8.3 Create Supply Route? (window-raised, three buttons)

**(a)** Dialog (modal). **(b)** `SpawnCreateRouteConfirmation(RouteCandidate)` `:2700`, `PopupDialog` at
`:2726`, dialog name `"ParsekLogisticsCreateRouteConfirm"` (`:2730`), title `"Create Supply Route?"` (`:2732`).
**(c)** Raised by a Candidates-row "Create Route" click (`:1533` -> `:2481-2490`). No-ops with a Warn on a
null candidate/analysis/tree (`:2702-2706`).
**(d)** Body is `RouteCreationFormatters.BuildSummaryBlock(analysis, mode, tree, runCost)`
(`Logistics/RouteCreationFormatters.cs:335`) - Origin / Endpoint (+ `ConnectionKindSuffix`, `:261`) /
Resources / Inventory / Transit, plus the Career+KSC cost block
(`LogisticsCostPresentation.FormatCreationSummaryBlock`, `Logistics/LogisticsCostPresentation.cs:102`) when
`Applicable && CostKnown`. The SAME summary the post-commit dialog renders (06.8.4).
**(e)** Three buttons, all routed through the pure `LogisticsCreatePresentation` decision:
`"Create Paused"` (`:2734`), `"Create and Activate"` (`:2736`), `"Cancel"` (`:2738`) ->
`HandleCreateRouteChoice` `:2756`, whose `ShouldBuild` (`UI/LogisticsCreatePresentation.cs:37`) gates the build
and `ShouldActivate` (`:46`) gates the post-build `RouteOrchestrator.TryActivate` (`:2767-2768`). Build funnels
through `CreateRouteFromCandidate` `:2827` -> `RouteCreationService.CreatePausedFromCandidate` (`:2848`), with
the interval resolved by `ResolveWindowCreateInterval` `:2799` (same `ComputeRootToUndockSpan` helper the
post-commit dialog uses, so a window-create and a dialog-create cannot diverge). None of the three buttons is
ever disabled.
**(f)** NO PICTURE. **(g)** Stock dialog sizing.

### 06.8.4 Create Supply Route? (post-commit, `RouteCreationDialog`)

**(a)** Dialog (modal) - a SEPARATE surface from 06.8.3 despite the identical title.
**(b)** `RouteCreationDialog` (`UI/RouteCreationDialog.cs:24`); spawn at `:241`, `PopupDialog` at `:312`,
dialog name `"ParsekRouteCreation"` (`:27`), title `"Create Supply Route?"` (`:318`).
**(c)** Fired by `MergeDialog.OnTreeCommitted` via `TryShow` (`:81`) when
`RouteAnalysisEngine.AnalyzeTree` returns eligible (`:108-114`); retried per-frame from
`ParsekScenario.Update` through `TryShowDeferredIfPending` (`:136`) when a same-frame scene change killed the
first spawn. Scene-restricted to FLIGHT / SPACECENTER / TRACKSTATION (`:152-157`).
**(d)** Body = the same `BuildSummaryBlock` (`:300`). Two buttons: `"Create Route"` (`:322`) and `"Cancel"`
(`:331`). Name/interval are NOT editable - v0 uses `GenerateDefaultRouteName` (`:310`) and
`ComputeRootToUndockSpan` (`:307`); the comment at `:285-290` records that in-dialog input wiring is deferred.
**(e)** `Create Route` -> `OnConfirm` `:358`, which RE-ANALYSES the tree before committing (stale-tree guard,
`:366-376`), calls `RouteBuilder.BuildRoute` (`:387`), `RouteStore.AddRoute` (`:402`), verifies the store
actually took it (`:407-413`), and clears any manual loop on the source tree
(`RouteTreeGuard.ForceClearManualLoopForRoute`, `:419`). `Cancel` -> `OnCancel` `:436`. Neither button is ever
disabled. The dialog takes a FULL input lock (`ControlTypes.All`, `:257`) - unlike the Logistics window's
camera-only lock - released in `DismissIfOpen` (`:473`).
**(f)** NO PICTURE - it is not reachable from a census `op=open`; it fires on a tree COMMIT.
**(g)** Stock dialog sizing.

---

## 06.9 Adjacent surfaces owned by files in this section

**Main-window Logistics button tint.** `LogisticsButtonState.AnyRouteHardBroken(IEnumerable<RouteStatus>)`
(`UI/LogisticsButtonState.cs:27`) delegates the broken set to `RouteStatusPolicy.Broken` (`:33`) - deliberately
not duplicated, so the tint and the policy cannot diverge. Consumed at `ParsekUI.cs:876-881`: red
`(0.95, 0.45, 0.45)` when any route is hard-broken, else cyan `(0.45, 0.85, 0.95)` while a Record-Supply-Run
prompt is pending (broken wins - "an error outranks a hint", `ParsekUI.cs:879-880`). The button label is the plain
word "Logistics" with tooltip "Supply routes that repeat a delivery you already flew." - photographed in both
census labels at `rect=[18,98,230,21]`.

**Record-Supply-Run banner.** Armed by `RouteRunPrompt.NotifyTreeCommittedCore`
(`Logistics/RouteRunPrompt.cs:142`) after a tree commit, gated by the pure `ShouldPrompt` truth table (`:59`)
- at most once per tree ever, never during a test batch or a restore window. Drawn in the MAIN window, not
here (`ParsekUI.cs:817-856`): a box label `"Supply Route candidate:\n'{label}'"` plus `Open Logistics` /
`Dismiss`. Dismiss also adds the tree to the dismissed set so it leaves the Candidates section
(`ParsekUI.cs:849-850`), with tooltip "Drops the suggestion; undo it in Logistics > Dismissed."
(`ParsekUI.cs:841-843`). Cleared by a successful create at `:2781`. NO PICTURE (no prompt pending in the
census save).

---

# Section 07 - Settings and tools

Scope: the Settings window, the two Test Runner windows, the Gloops Flight Recorder, Real
Spawn Control, and the ghost-label / spawn-warning text surface. All paths are relative to
`Source/Parsek`. Every line number was grepped against the worktree
`C:/Users/vlad3/Documents/Code/Parsek/Parsek-gui-census-dump`.

Census evidence:

| run | dir |
| --- | --- |
| KSC | `harness/results/2026-09-11_0548_GUI-1-census-ksc_shots/` |
| flight | `harness/results/2026-09-11_0551_GUI-2-census-flight_shots/` |

Both runs are `parsek-gui-tree/1` dumps at screen 1280x720, `patched=17/17` funnels,
`recordFaults=0`, `droppedOverCap=0` (`ksc-settings-advanced.gui.json` counts block).

---

## 7.1 Parsek - Settings

**(a) Name and kind.** "Parsek - Settings" - a draggable IMGUI window. Six SECTIONS drawn
top-to-bottom in one pass; no tabs, no scroll view. The census window table records that
explicitly: `NewSpec(SettingsWindow, true, true)` with no tab list, and the comment says the
Basic/Advanced capture pair IS the section coverage (`TestCommands/TestCommandUiAction.cs:420-423`).

**(b) Class + draw method.**

| what | file:line |
| --- | --- |
| class | `UI/SettingsWindowUI.cs:12` |
| outer guard / rect / lock | `UI/SettingsWindowUI.cs:55` (`DrawIfOpen`) |
| `ClickThruBlocker.GUILayoutWindow` | `UI/SettingsWindowUI.cs:128` |
| window callback | `UI/SettingsWindowUI.cs:309` (`DrawSettingsWindow`) |
| pure helpers | `UI/SettingsWindowPresentation.cs:9` |

**(c) Scenes + gate.** FLIGHT (`ParsekFlight.cs:2138`) and SPACECENTER (`ParsekKSC.cs:260`),
both through `ParsekUI.DrawSettingsWindowIfOpen` (`ParsekUI.cs:1731`). The flight call sits
inside the host's `showUI` gate (`ParsekFlight.cs:2108`). Launcher: the ungated "Settings"
button `ParsekUI.cs:955-958` -> `ParsekUI.ToggleSettingsWindow` (`ParsekUI.cs:1087`).
`UiSurface.MainButtonSettings` is visible in BOTH modes by design - it hosts the mode toggle
(`UI/UiComplexityMode.cs:174`), so it is deliberately left unwrapped at the draw site.

**(d) Structure top-down.** No table, no columns. Draw order from `DrawSettingsWindow`:

| # | section | header label | drawn at | gate |
| --- | --- | --- | --- | --- |
| 0 | `GUILayout.Space(5)` under the title bar | - | `:312` | always |
| 0b | null-settings fallback: label + Close | "Settings unavailable (no active game)." | `:316-318` | `ParsekSettings.Current == null` |
| 1 | Interface | "Interface" | `:370` -> `:462` | always |
| 2 | Looping | "Looping" | `:378-382` -> `:530` | `SettingsSectionLooping` |
| 3 | Ghosts | "Ghosts" | `:384` -> `:583` | always |
| 4 | Diagnostics | "Diagnostics" | `:393-397` -> `:614` | `SettingsSectionDiagnostics` |
| 5 | Recorder Sample Density | "Recorder Sample Density" | `:401-405` -> `:689` | `SettingsSectionSampleDensity` |
| 6 | Data Management | "Data Management" | `:407` -> `:734` | always |
| 7 | tooltip echo strip (fixed 2-line box) | - | `:413` | always |
| 8 | footer row: Defaults, Close | - | `:415-447` | always |

Each hidden section's trailing `GUILayout.Space(SpacingSmall)` lives INSIDE its gate
(`:381`, `:396`, `:404`), so Basic shows no double gap.

`complexity` is read ONCE per pass from the frame-latched `ParsekUI.AppliedUiComplexityMode`
at `:330` - never the settings field - because the Interface section hosts the mode toggle and
draws before the gated sections; a raw read would change the IMGUI control count between
Layout and Repaint.

**(e) Every control.**

| section | control | label | tooltip | writes | file:line |
| --- | --- | --- | --- | --- | --- |
| Interface | button (box when selected) | `Basic` | "Show only the core loop: Timeline, Missions, Logistics, and Settings." | `ParsekUI.SetUiComplexityMode(Basic)` | `:488-493`, tooltip `:526-527` |
| Interface | button (box when selected) | `Advanced` | "Show every Parsek window and settings section." | `ParsekUI.SetUiComplexityMode(Advanced)` | `:488-493`, tooltip `:528` |
| Interface | label (hint, never a control) | "Basic hides power-user windows. Advanced is the full UI." (+ " Stop the Gloops recording first." while recording) | - | - | `:497`, text `:514-520` |
| Looping | label | "Auto-launch every" | "Default launch-to-launch period for 'auto' rows. Shorter = overlap." | - | `:534-536` |
| Looping | textfield `Width(45)` named `AutoLoopEdit` | value in the current unit | - | `s.autoLoopIntervalSeconds` via `CommitAutoLoopEdit` (`:268`) | `:544` (idle) / `:560` (editing) |
| Looping | button `Width(40)` | `sec` / `min` / `hr` (`ParsekUI.UnitLabel`) | - | `s.AutoLoopDisplayUnit` (cycles) | `:572-578` |
| Ghosts | label `Width(85)` | "Ghost audio" | "Volume for ghost audio: engines, RCS, events. 0% = muted." | - | `:588-590` |
| Ghosts | `HorizontalSlider` 0..1 | - | - | `s.ghostAudioVolume` (deadband 0.001) | `:591`, commit `:596-601` |
| Ghosts | label `Width(35)` | "NN%" | - | - | `:592-594` |
| Ghosts | toggle | " Show supply route paths on map" | "Draw supply routes' recorded paths on the map and Tracking Station." | `s.showRouteLines` + `ParsekSettingsPersistence.RecordShowRouteLines` | `:603-611` |
| Diagnostics | toggle | " Verbose logging (development default)" | none | `s.verboseLogging` | `:617-622` |
| Diagnostics | toggle | " Ghost render tracing (Warning: huge logs)" | "Log per-ghost render placement to KSP.log. Leave off unless debugging." | `s.ghostRenderTracing` + `RecordGhostRenderTracing` | `:624-632` |
| Diagnostics | toggle | " Map/TS render tracing (Warning: huge logs)" | "Log map and Tracking Station ghost rendering to KSP.log. Leave off." | `s.mapRenderTracing` + `RecordMapRenderTracing` | `:634-642` |
| Diagnostics | toggle | " Ledger apply tracing (Warning: huge logs)" | "Log ledger reconstruction and apply detail to KSP.log. Leave off." | `s.ledgerTracing` + `RecordLedgerTracing` | `:644-652` |
| Diagnostics | toggle | " Write readable sidecar mirrors (Warning: extra disk usage)" | "Also write .txt mirrors of recording sidecars, for debugging." | `s.writeReadableSidecarMirrors` + `RecordReadableSidecarMirrors` + `RecordingStore.ReconcileReadableSidecarMirrorsForKnownRecordings()` | `:654-663` |
| Diagnostics | button | "In-Game Test Runner" | "Run runtime tests for ghosts and playback. Also Ctrl+Shift+T." | `parentUI.ToggleTestRunner()` (`ParsekUI.cs:1767`) - the SETTINGS-launched `TestRunnerUI`, not the global one | `:665-669` |
| Diagnostics | button | "Run Diagnostics Report" | "Compute full diagnostics snapshot and dump report to KSP.log" | `DiagnosticsComputation.RunDiagnosticsReport()` | `:671-676` |
| Diagnostics | label (readout, not a control) | `RewindPointDiskUsage.FormatLine(...)` | "Rewind-point quicksave disk use, by crashed / stable / concluded." | - | `:682-686` |
| Sample Density | button (box when selected) x3 | `Low` / `Medium` / `High` (`ParsekSettings.DensityLabel`) | `ParsekSettings.DensityTooltip(level)` | `s.SamplingDensityLevel` | `:694-707` |
| Sample Density | label | `ParsekSettings.DensitySummary(level)` | - | - | `:710-711` |
| Data Mgmt | button | `Wipe All Recordings ({count})` | none (plain string overload) | `parentUI.ShowWipeRecordingsConfirmation(count)` -> `PopupDialog` (`ParsekUI.cs:1503`) | `:746-753` |
| Data Mgmt | button | `Wipe All Game Actions ({count})` | none | `parentUI.ShowWipeActionsConfirmation(count)` -> `PopupDialog` (`ParsekUI.cs:1536`) | `:756-762` |
| footer | button | `Defaults` | none | resets 9 fields from `SettingsWindowPresentation.BuildDefaults()` (`UI/SettingsWindowPresentation.cs:55`), re-records 5 persistence keys, reconciles mirrors, ends the auto-loop edit | `:416-441` |
| footer | button | `Close` | none | `showSettingsWindow = false` | `:442-446` |

Conditionally disabled / hidden:

| control | condition | file:line |
| --- | --- | --- |
| `Basic` mode button | `GUI.enabled = false` when `IsModeOptionDisabled(Basic, gloopsRecording)`, i.e. `parentUI.Flight != null && parentUI.Flight.IsGloopsRecording` | `:475`, `:487`, predicate `:505-508` |
| `Advanced` mode button | never disabled (Advanced only reveals) | `:505-508` |
| `Wipe All Recordings` | `GUI.enabled = committedCount > 0`; disabled-hover echo text `"There are no recordings to wipe"` | `:745`, `:750-751`, reason `:719-722` |
| `Wipe All Game Actions` | `GUI.enabled = milestoneCount > 0`; echo `"There are no game actions to wipe"` | `:756`, `:759-760`, reason `:729-732` |
| Looping section | hidden in Basic; an in-progress auto-loop edit is DROPPED uncommitted rather than committed against a stale rect | `:345-354` |
| Diagnostics + Sample Density sections | hidden in Basic | `:393`, `:401` |

Settings that NO LONGER have a player-facing control (verified against the code, 2026-08-27
simplification):

| setting | state | evidence |
| --- | --- | --- |
| `autoRecordOnLaunch` / `autoRecordOnEva` / `autoRecordOnFirstModificationAfterSwitch` | HIDDEN field, clamped to `true` on load unless an automation hook is armed; harness-pinned | `ParsekSettings.cs:38,40,42`, clamp `ParsekSettings.cs:290-297`, whitelist `TestCommands/SettingWhitelist.cs:91` |
| `autoMerge` | HIDDEN, clamped `true` | `ParsekSettings.cs:71`, `:296`; whitelist `SettingWhitelist.cs:94` |
| `forceFaithfulLoopPlayback` | HIDDEN, clamped `false` | `ParsekSettings.cs:251`, `:297`; whitelist `SettingWhitelist.cs:98` |
| `autoBackupExistingSaves`, `showCommittedFutureOverlays`, `blockCommittedActions` | DELETED; behaviours permanently on, stale keys ignored on load | `ParsekSettings.cs:93-99` |
| `transitedBodyRotationModeIndex` | DELETED; pinned `TransitedBodyRotationMode.Loose` as a `const` | `ParsekSettings.cs:233-238` |

So the window draws controls for exactly 9 settings (`uiComplexityMode`,
`autoLoopIntervalSeconds`, `autoLoopTimeUnit`, `ghostAudioVolume`, `showRouteLines`,
`verboseLogging`, `ghostRenderTracing`, `mapRenderTracing`, `ledgerTracing`,
`writeReadableSidecarMirrors` - the last five all inside the Basic-hidden Diagnostics
section - plus `samplingDensity`).

**(f) State variants.**

| variant | census label | notes |
| --- | --- | --- |
| Advanced, all six sections | `ksc-settings-advanced` | 39 nodes in the Settings subtree, window rect `[270,8,400,718]` (commanded `w=400 h=700`; GUILayout grew it to 718) |
| Basic, three sections | `ksc-settings-basic` | 19 nodes, rect `[270,8,400,700]` |
| null `ParsekSettings.Current` | NONE | needs a scene with no active game |
| `Basic` button disabled (Gloops recording live) | NONE | needs a live Gloops recorder in FLIGHT |
| auto-loop field mid-edit | NONE | needs keyboard focus in `AutoLoopEdit` |
| either wipe button greyed out | NONE | the census host has 110 recordings / 49 milestones |

The 20-node difference between the two dumps is exactly the three Basic-hidden sections,
diffed node-for-node from the jsons:

| hidden node (kind, text) | section | code |
| --- | --- | --- |
| label "Looping" | Looping | `:532` |
| layoutgroup (the auto-launch row) | Looping | `:533` |
| label "Auto-launch every" | Looping | `:534` |
| textfield "30" | Looping | `:544` |
| button "sec" | Looping | `:572` |
| label "Diagnostics" | Diagnostics | `:616` |
| toggle " Verbose logging (development default)" | Diagnostics | `:617` |
| toggle " Ghost render tracing (Warning: huge logs)" | Diagnostics | `:624` |
| toggle " Map/TS render tracing (Warning: huge logs)" | Diagnostics | `:634` |
| toggle " Ledger apply tracing (Warning: huge logs)" | Diagnostics | `:644` |
| toggle " Write readable sidecar mirrors (Warning: extra disk usage)" | Diagnostics | `:654` |
| button "In-Game Test Runner" | Diagnostics | `:665` |
| button "Run Diagnostics Report" | Diagnostics | `:671` |
| label "Rewind point disk usage: 916.2 KB (1 file; live=1, crashed=0, stable=1, concluded=1)" | Diagnostics | `:684` |
| label "Recorder Sample Density" | SampleDensity | `:691` |
| layoutgroup (the three density buttons) | SampleDensity | `:693` |
| button "Low" | SampleDensity | `:698` |
| button "Medium" | SampleDensity | `:698` |
| button "High" | SampleDensity | `:698` |
| label "Sampling: every 0.20-3.0s, 2.0 deg / 5% thresholds" | SampleDensity | `:710` |

5 + 9 + 6 = 20. Nothing else differs: Interface, Ghosts, Data Management, the echo strip and
the footer row are byte-identical across the pair apart from the two mode buttons' widths
(the selected one renders with `GUI.skin.box`, which measures differently).

**(g) Sizing.**

| fact | value | file:line |
| --- | --- | --- |
| first-open rect | `(mainRect.x + mainRect.width + 10, mainRect.y, 280, 600)` | `:69-72` |
| the 600 is a guess, not a measurement | first open always requests a height fit | `:75-86` |
| no resize handle; height is fixed between fits | - | `:206-209` |
| size options | `Width(rect.width)` always; `Height(rect.height)` DROPPED on a fit pass | `:115-121` |
| height-fit layout rect | zero-height for one Layout pass (`BuildHeightFitLayoutRect`) | `UI/SettingsWindowPresentation.cs:119-125` |
| fit-pass return is kept at the stored height | `KeepStoredHeightAcrossFitPass` | `UI/SettingsWindowPresentation.cs:150-156` |
| fit-report pass budget | `HeightFitLogPassBudget = 12` | `:30` |
| spacing constants | `SpacingSmall = 3f`, `SpacingLarge = 10f` | `:37-38` |
| tooltip echo strip | `new TooltipEchoBox(SpacingSmall)` -> two-line box; measured 38 px in the dump | `:42`, `UI/TooltipEchoBox.cs:69` |
| inner control pins | `Width(45)` auto-loop field, `Width(40)` unit button, `Width(85)` audio label, `Width(35)` percent label | `:544`, `:572`, `:590`, `:594` |
| input lock | `CAMERACONTROLS`, id `Parsek_SettingsWindow` | `:19`, `:193` |
| window id | `"ParsekSettings".GetHashCode()` | `:129` |
| automation rect seam | `WindowRectForTesting` is settable; HEIGHT is a floor, not a pin, because a pending fit hands GUILayout zero | `:242-251` |

---

## 7.2 Parsek - Test Runner (Settings-launched)

**(a) Kind.** Window, resizable, with one scroll view. Reached ONLY from the Settings
Diagnostics section, so Basic removes its only reopen path.

**(b) Class + draw method.**

| what | file:line |
| --- | --- |
| class | `UI/TestRunnerUI.cs:12` |
| `DrawIfOpen` | `UI/TestRunnerUI.cs:88` |
| `ClickThruBlocker.GUILayoutWindow` | `UI/TestRunnerUI.cs:122` |
| window callback | `UI/TestRunnerUI.cs:375` (`DrawTestRunnerWindow`) |
| category/row list | `UI/TestRunnerUI.cs:232` (`DrawTestCategoryList`) |
| pure label/tooltip helpers | `InGameTests/TestRunnerPresentation.cs:9` |
| search filter | `InGameTests/TestRunnerSearchFilter.cs:12` |

**(c) Scenes + gate.** FLIGHT (`ParsekFlight.cs:2141`) and SPACECENTER (`ParsekKSC.cs:261`)
via `ParsekUI.DrawTestRunnerWindowIfOpen` (`ParsekUI.cs:1747`). Launcher: the
"In-Game Test Runner" button inside the Basic-hidden Diagnostics section
(`UI/SettingsWindowUI.cs:665`). The window itself maps to NO `UiSurface`; instead the
Advanced -> Basic close handler force-closes it and releases its lock
(`ParsekUI.cs:516-521`, rationale `ParsekUI.cs:474-478`).

**(d) Structure top-down.**

| band | content | file:line |
| --- | --- | --- |
| header row | `Run All`, `Run All + Isolated`, `Reset`, `Cancel` | `:387-417` |
| summary | one `GUI.skin.box` label, `BuildRunSummary` | `:421-428`, text `TestRunnerPresentation.cs:20-29` |
| scene note | `Scene: {HighLogic.LoadedScene}` | `:431` |
| batch-mode notice | `BuildBatchModeNotice`, 3 possible strings or nothing | `:432-438`, text `TestRunnerPresentation.cs:97-131` |
| filter bar | label `Search:` `Width(48)`, textfield `ExpandWidth`, `x` button `Width(24)` | `:446-460` |
| body | `BeginScrollView(..., ExpandHeight(true))` -> category list | `:475-480` |
| tooltip echo strip | two-line box | `:487` |
| footer | full-width `Close`, then the label "Ctrl+Shift+T to toggle from any scene" | `:492-498` |
| resize handle | `ParsekUI.DrawResizeHandle` | `:500-501` |

ROW MODEL - two row kinds, no columns, no sort keys (categories are ordinal-sorted at
`:190-192`):

*Category header row* (`:259-286`): a label-styled button whose text is
`BuildCategoryButtonLabel` = `"{arrow} {category} ({passed}/{total})"`, or
`"... ({passed}/{total}, {failed} failed)"` when anything failed; arrow is `U+25BC` expanded /
`U+25B6` collapsed (`TestRunnerPresentation.cs:31-61`). Then `Run` `Width(40)` and `Run+`
`Width(44)`. The count is taken from the FULL category via `GetFullCategoryTests` (`:216-230`)
so a name filter never makes the count disagree with what `Run` executes.

*Test row* (`:291-347`): `Space(16)` indent, a status-icon label `Width(20)`, the test-name
label `ExpandWidth(true)`, and a `U+25B6` play button `Width(24)`. Below each row an ALWAYS
-rendered error row (`Space(ErrorIndent=40)` + red label) that collapses to `Height(0)` when
there is nothing to show - conditional begin/end would desync the Layout/Repaint control
count (`:334-346`).

| status | icon | colour | file:line |
| --- | --- | --- | --- |
| Passed | `U+2713` | green | `:510`, `:522` |
| Failed | `U+2717` | red | `:511`, `:523` |
| Running | `U+25CB` | yellow | `:512`, `:524` |
| Skipped | `U+2013` | gray | `:513`, `:525` |
| NotRun | `U+00B7` | white | `:514`, `:526` |

Test label suffixes: `" [isolated]"` when `RestoreBatchFlightBaselineAfterExecution`,
`" [single]"` when `!AllowBatchExecution`, then `" (NNNms)"` once a duration exists
(`TestRunnerPresentation.cs:63-81`). `[single]` rows are tinted light blue
`Color(0.45f,0.65f,1f)` (`:313-314`). The row tooltip is description + batch note +
`"Requires {scene} scene"` + error text (`TestRunnerPresentation.cs:133-158`).

**(e) Action controls.**

| control | calls | disabled when | file:line |
| --- | --- | --- | --- |
| `Run All` | `testRunner.ResetResults(); testRunner.RunAll()` | `testRunner.IsRunning` | `:388-394` |
| `Run All + Isolated` | `ResetResults(); RunAllIncludingFlightRestore()` | `IsRunning` | `:395-401` |
| `Reset` | `testRunner.ClearAllSceneHistory()` (full history wipe, unlike the implicit pre-run reset) | `IsRunning` | `:402-410` |
| `Cancel` | `testRunner.Cancel()` | `GUI.enabled = testRunner.IsRunning` - enabled ONLY while running | `:411-415` |
| `x` (clear search) | clears `testSearchQuery`, drops keyboard focus | `string.IsNullOrEmpty(testSearchQuery)` | `:450-458` |
| category label button | toggles `expandedTestCategories` | never | `:264-268` |
| `Run` | `ResetCategory(cat); RunCategory(cat)` | `IsRunning` | `:270-276` |
| `Run+` | `ResetCategory(cat); RunCategoryIncludingFlightRestore(cat)` | `IsRunning` (shares the same `GUI.enabled` block) | `:277-284` |
| row `U+25B6` | resets the row then `testRunner.RunSingle(test)` | `IsRunning \|\| !eligible` | `:323-329` |
| row name label | - | dimmed (`GUI.enabled=false`) when `!IsEligibleForScene` | `:310-320` |
| `Close` | `showTestRunnerWindow = false` | never | `:493-497` |

There is NO export button: results auto-export after every run (`:489-491`). The search box
filters the DISPLAYED list only; `Run All` / `Run` ignore it (`:44-47`, `:440-444`).

**(f) State variants.**

| variant | census label | notes |
| --- | --- | --- |
| idle, every category expanded, no filter | `ksc-testrunner-advanced` | 4217 nodes in the window subtree, rect `[270,8,620,700]`; summary reads `idle \| 0 passed  0 failed  0 skipped  (624 total)`; 113 category headers, 624 play rows, 0 collapsed categories |
| batch-mode notice, `[single]`-only form | same label | `"[single] tests are skipped by Run All / Run category. Use the row play button for manual-only destructive checks."` |
| RUNNING / Cancel enabled | NONE | needs a batch mid-flight when the dump fires |
| any failed row + red error row | NONE | needs a red test |
| active search filter / "No categories or tests match ..." | NONE | needs a `type` seam op into the search field; no seam verb exists |
| a collapsed category | NONE | every category is expanded on first open (`:100-102`) |
| Basic mode | NONE and NOT REACHABLE | the launcher is inside the hidden Diagnostics section and the close handler shuts an open instance |

**(g) Sizing.**

| fact | value | file:line |
| --- | --- | --- |
| default rect | `(mainRect.x + mainRect.width + 10, mainRect.y, 440, 600)` | `:107-110`, consts `:68-69` |
| min width / height | `320` / `600` (default height is also the minimum) | `:70-73` |
| resize | `ParsekUI.HandleResizeDrag` at `:113`, handle drawn at `:500` | |
| error row pins | `ErrorIndent = 40f`, `ErrorMaxWidth = 380f` | `:74-75` |
| input lock | `CAMERACONTROLS`, id `Parsek_TestRunnerWindow` | `:39`, `:142` |
| window id | `"ParsekTestRunner".GetHashCode()` | `:123` |
| census-commanded rect | `x=270 y=8 w=620 h=700` (`harness/scenarios/GUI-1-census-ksc.toml:266`) | |

---

## 7.3 Parsek - Test Runner (global Ctrl+Shift+T)

**(a) Kind.** A SEPARATE window with the same title, owned by its own `MonoBehaviour`. Not a
`ParsekUI` sub-window.

**(b) Class + draw method.**

| what | file:line |
| --- | --- |
| class / addon attribute | `InGameTests/TestRunnerShortcut.cs:18-19` (`[KSPAddon(Startup.Instantly, true)]` + `DontDestroyOnLoad` at `:119`) |
| `OnGUI` | `InGameTests/TestRunnerShortcut.cs:177` |
| `GUILayout.Window` | `InGameTests/TestRunnerShortcut.cs:203-211` |
| window callback | `InGameTests/TestRunnerShortcut.cs:393` (`DrawWindow`) |
| toggle | `InGameTests/TestRunnerShortcut.cs:159-175` (`Update`) |

**(c) Scenes + gate.** EVERY scene except `GameScenes.LOADING` (`:180`), including scenes
Parsek draws no main UI in (editor, facility interiors). Toggled by
Ctrl+Shift+T read through `Input.GetKey`/`GetKeyDown` rather than the IMGUI `Event`, which is
unreliable across KSP scenes (`:161-164`). NEVER gated by complexity mode - the census window
table notes it has no accessor and no gate (`TestCommands/TestCommandUiAction.cs:430-432`),
and the Settings section comment calls this out explicitly (`UI/SettingsWindowUI.cs:389-392`).

**(d)-(e) How it DIFFERS from `TestRunnerUI`.** The row model, status icons, colours,
category labels and `Run` / `Run+` / play semantics are identical (both call into
`TestRunnerPresentation` and the same `InGameTestRunner` API). The differences:

| difference | global window | Settings window |
| --- | --- | --- |
| search / filter bar | ABSENT - no `Search:` label, textfield or `x` button | present (`UI/TestRunnerUI.cs:446-460`) |
| footer labels | TWO: "Results file auto-updates after each run. Multi-scene runs accumulate." (`:551`) then "Ctrl+Shift+T to toggle from any scene" (`:553`) | ONE: only the Ctrl+Shift+T line (`UI/TestRunnerUI.cs:498`) |
| per-click logging | none on `Run All` / `Reset` / `Cancel` / `Run` / `Run+` | `ParsekLog.Info` / `Verbose` on each (`UI/TestRunnerUI.cs:393`, `:400`, `:409`, `:275`, `:283`, `:496`) |
| window host | raw `GUILayout.Window` | `ClickThruBlocker.GUILayoutWindow` |
| input lock | `CAMERACONTROLS \| EDITOR_ICON_HOVER \| EDITOR_ICON_PICK \| EDITOR_PAD_PICK_PLACE \| EDITOR_PAD_PICK_COPY \| EDITOR_GIZMO_TOOLS \| KSC_ALL`, id `Parsek_TestRunnerGlobal` (`:217-222`, `:29`) | `CAMERACONTROLS` only |
| opaque style | builds and OWNS its own `Texture2D` copies, rebuilt per scene and explicitly destroyed (`:290-374`) | borrows `parentUI.GetOpaqueWindowStyle()` |
| tooltip echo | `new TooltipEchoBox()` (default spacing), styles reset on scene change (`:38`, `:272`) | `new TooltipEchoBox(SpacingSmall)` |
| open-flag accessor | NONE - `showWindow` is private, so no `UiAction op=open` can drive it | `IsOpen` + `WindowRectForTesting` |
| complexity close set | not a member | a member (`ParsekUI.cs:516-521`) |
| extra responsibility | hosts the M-A3 autorun hooks: `PARSEK_AUTORUN_TESTS` / `PARSEK_AUTORUN_EXIT` / `PARSEK_AUTORUN_ISOLATED` parsed once at Awake (`:136-152`), the per-frame settle/fire poll (`:635-710`), `FireAutorun` (`:728`), and the multi-category driver coroutine (`:787`) | none |
| runner lifecycle | one lazy `EnsureRunner()` shared by the autorun path and the window (`:384-391`); exposed as `ActiveRunnerForGating` so the command seam can refuse mid-batch (`:105`) | lazily built inside `DrawIfOpen` (`UI/TestRunnerUI.cs:97-103`) |

Header row, summary, scene label, batch notice, scroll view, `Close`, resize handle are the
same controls in the same order (`:404-557`).

**(f) State variants.** NO census picture in either run. No label anywhere in the two shot
directories corresponds to it, and it is unreachable from the seam: the census window table
resolves the token `testrunner` to `TestRunnerUI` (`TestCommands/ParsekTestCommandAddon.UiAction.cs:665-670`),
and `TestRunnerShortcut.showWindow` has no accessor at all.

**(g) Sizing.**

| fact | value | file:line |
| --- | --- | --- |
| default rect | `(20, 60, 440, 600)` - a FIXED screen position, not anchored to the main window | `:195`, consts `:83-84` |
| min width / height | `320` / `600` | `:85-88` |
| error row pins | `ErrorIndent = 40f`, `ErrorMaxWidth = 380f` | `:89-90` |
| window id | `"ParsekTestRunnerGlobal".GetHashCode()` | `:205` |
| autorun settle target | 30 consecutive qualifying frames (~0.5 s at 60 fps) | `:63` |
| no-scene warn | 60 s one-shot | `:66` |

---

## 7.4 Gloops Flight Recorder

**(a) Kind.** Window, flight-only, no tabs, no scroll view, no resize handle.

**(b) Class + draw method.**

| what | file:line |
| --- | --- |
| class | `UI/GloopsRecorderUI.cs:12` |
| `DrawIfOpen` | `UI/GloopsRecorderUI.cs:62` |
| `ClickThruBlocker.GUILayoutWindow` | `UI/GloopsRecorderUI.cs:94` |
| window callback | `UI/GloopsRecorderUI.cs:207` (`DrawWindow`) |
| recording-status ladder | `UI/GloopsRecorderUI.cs:356` (`DrawRecordingStatus`) |

**(c) Scenes + gate.** FLIGHT only: `ParsekUI.DrawGloopsRecorderWindowIfOpen` guards on
`InFlight && flight != null` (`ParsekUI.cs:1742-1744`), called from `ParsekFlight.cs:2140`;
`ParsekKSC.OnGUI` never calls it. `DrawIfOpen` additionally self-closes when `flight == null`
(`:70-75`).

**Its launcher is RETIRED in BOTH modes.** `UiSurfaceVisibility.IsRetired` returns true for
`UiSurface.MainButtonGloops` (`UI/UiComplexityMode.cs:140-143`), and `IsVisible` answers false
for a retired surface BEFORE consulting the mode (`UI/UiComplexityMode.cs:162-163`). The
launcher block at `ParsekUI.cs:941-952` is therefore dead in Advanced too; it is kept behind
the gate so re-offering it is a one-line decision (`ParsekUI.cs:937-940`). Note that
`MainButtonGloops` has NO `case` in the Basic switch - un-retiring it would throw
`ArgumentOutOfRangeException` until a Basic decision is added, which is the intended fail-loud
re-decision (`UI/UiComplexityMode.cs:158-161`, `:191-196`).

**How the census still photographed it.** The window BODY is never gated - only launchers are
(design 7.1) - so the window still draws whenever `IsOpen` is true. The M-A2 command seam
writes that flag directly: `window=gloops` is in the window table as flight-only
(`TestCommands/TestCommandUiAction.cs:428`, token at `:179`) and the applier hands back a
handle over `GloopsRecorderUI.IsOpen` / `WindowRectForTesting`
(`TestCommands/ParsekTestCommandAddon.UiAction.cs:659-665`). The GUI-2 spec opens it with
`UiAction op=open window=gloops`, sizes it with `op=rect ... w=340 h=300`, captures, dumps,
then closes (`harness/scenarios/GUI-2-census-flight.toml:154-159`), and the header states the
reason: "it is either dead weight to delete or a surface to re-offer, and neither call can be
made from a window nobody has looked at" (`GUI-2-census-flight.toml:23-27`).

What still opens it in the product, other than the seam: nothing player-facing. The only other
writers of `gloopsUI.IsOpen` are the launcher block that no longer draws (`ParsekUI.cs:946`)
and the Advanced -> Basic close handler, which only ever sets it false (`ParsekUI.cs:509`).

**(d) Structure top-down.** One `BeginVertical` (`:209`), `Space(5)` (`:210`), then a FIXED
button order so positions never shift between states (`:226-228`), then the status ladder,
`FlexibleSpace`, the echo strip, and `Close`.

**(e) Controls.**

| control | label | tooltip | calls | enabled when | file:line |
| --- | --- | --- | --- | --- | --- |
| primary button | `Stop Recording` / `Start New Recording` / `Start Recording` | "Records a ghost-only flight; your career never sees it." | recording -> `flight.StopGloopsRecording()`; else `flight.StopPlayback()` if previewing, then `flight.StartGloopsRecording()` | always | `:230-250` |
| preview button | `Stop Preview` / `Preview` | "Replays the last Gloops take as a ghost so you can check it." | previewing -> `flight.StopPlayback()`; else `flight.PreviewGloopsRecording()` | `HasLastRecording && !IsRecording` | `:252-270` |
| discard button | `Discard Recording` | "Throws the current or last Gloops take away for good." | recording -> `flight.DiscardGloopsInProgress()`; else optional `StopPlayback()` then `flight.DiscardLastGloopsRecording()` | `IsRecording \|\| HasLastRecording` | `:274-292` |
| `Close` | `Close` | none | `showWindow = false` + `ReleaseInputLock()` | always | `:345-350` |

Status ladder, selected by `SelectStatusBlock(isRecording, hasLastRecording)` (`:172-177`) on a
snapshot RE-READ after the button handlers (`:305-310`) - the #446 NRE guard, because Discard
and Start-New both null `LastGloopsRecording` mid-dispatch:

| block | labels | file:line |
| --- | --- | --- |
| `Recording` | "Recording..." (section-header style), "Points: N", "Duration: N.Ns" (N>1 only) | `:356-375` |
| `Saved` | `Saved: "{VesselName}"`, "Points: N", "Duration: N.Ns" | `:317-327` |
| `Empty` | `Vessel: {activeVesselName}` or "No vessel", "Ghost-only - loops by default" | `:328-334` |

**(f) State variants.**

| variant | census label | notes |
| --- | --- | --- |
| `Empty` block, idle, Preview + Discard both greyed | `flight-gloops-advanced` | 51 nodes total in the dump; the Gloops window subtree is 8 nodes at rect `[270,8,340,300]`; primary reads `Start Recording`, status reads `Vessel: R2-B2-S7` / `Ghost-only - loops by default` |
| `Recording` block | NONE | needs `flight.StartGloopsRecording()`; no seam verb drives it |
| `Saved` block | NONE | needs a completed take; fixture `gloops-airshow` exists under `harness/fixtures/saves/` |
| `Stop Preview` state | NONE | needs a take plus an active preview |
| Basic mode | NONE | the close handler force-shuts it on the switch (`ParsekUI.cs:504-509`), and there is no launcher in either mode |

**(g) Sizing.**

| fact | value | file:line |
| --- | --- | --- |
| default rect | `(mainRect.x + mainRect.width + 10, mainRect.y, 280, 230)` | `:79-81`, consts `:48-49` |
| no resize handle; the seed height is the only reservation for the echo strip | - | `:45-47` |
| inter-button spacing | `Space(6f)` twice | `:272`, `:295` |
| tooltip echo | `new TooltipEchoBox()` - default 3f spacing, two lines | `:43` |
| input lock | `CAMERACONTROLS`, id `Parsek_GloopsRecorderWindow` | `:35`, `:114` |
| window id | `"ParsekGloopsRecorder".GetHashCode()` | `:95` |
| census-commanded rect | `x=270 y=8 w=340 h=300` (`GUI-2-census-flight.toml:157`) | |

---

## 7.5 Parsek - Real Spawn Control

**(a) Kind.** Window, flight-only, resizable, with a sortable header row over a scrolling
candidate table and a pinned bottom bar.

**(b) Class + draw method.**

| what | file:line |
| --- | --- |
| class | `UI/SpawnControlUI.cs:11` |
| `DrawIfOpen` | `UI/SpawnControlUI.cs:122` |
| `ClickThruBlocker.GUILayoutWindow` | `UI/SpawnControlUI.cs:162` |
| window callback | `UI/SpawnControlUI.cs:223` (`DrawSpawnControlWindow`) |
| row loop | `UI/SpawnControlUI.cs:275` (`DrawSpawnCandidateRows`) |
| bottom bar | `UI/SpawnControlUI.cs:362` (`DrawSpawnControlBottomBar`) |
| pure row / sort rules | `UI/SpawnControlPresentation.cs:45` |
| pure countdown / next-spawn / notification text | `SelectiveSpawnUI.cs:43` |

`Source/Parsek/SelectiveSpawnUI.cs` exists (388 lines) and is the pure helper this window
leans on for `FormatCountdown` (`:169`), `FindNextSpawnCandidate` (`:108`),
`FormatNextSpawnTooltip` (`:208`), `FormatTimeDelta` (`:143`) and `ComputeDepartureInfo`
(`:289`, `:374`).

**(c) Scenes + gate.** FLIGHT only. Launcher gated at `ParsekUI.cs:771`
(`UiSurface.MainButtonSpawnControl`, hidden in Basic per `UI/UiComplexityMode.cs:180`), inside
`if (InFlight && flight != null)` at `ParsekUI.cs:770`. Drawn from `ParsekFlight.cs:2139` via
`ParsekUI.cs:1736-1739`; `ParsekKSC.OnGUI` never calls it.

**Self-close.** `ResolveAutoCloseReason(inFlight, hasFlight, candidateCount)`
(`UI/SpawnControlUI.cs:88-100`) returns `"not-in-flight"`, `"flight-null"` or
`"zero-candidates"`, and `DrawIfOpen` shuts the window on its FIRST draw when any fires
(`:131-142`), logging `Real Spawn Control auto-close: reason={0} candidates={1}` on change
(`:102-120`).

**(d) Structure.** Header row of sortable columns, then the scrolling body, then the pinned
bottom bar. Empty-list fallback: label "No nearby craft to spawn." plus a `Close` button and an
early return (`:232-239`).

| # | column | header label | width const | value | sort key |
| --- | --- | --- | --- | --- | --- |
| 1 | Craft | `Craft` | `SpawnColW_Name = 0f` (expand) | `cand.vesselName` | `SpawnControlSortColumn.Name` (ordinal-ignore-case) |
| 2 | Dist | `Dist` | `SpawnColW_Dist = 55f` | `{0:F0}m` | `Distance` (default) |
| 3 | Rel Speed | `Rel Speed` | `SpawnColW_RelSpeed = 70f` | `FormatRelativeSpeed` - one decimal below 10 m/s, em-dash when the sample is infinite | `RelativeSpeed` |
| 4 | Spawns at | `Spawns at` | `SpawnColW_SpawnTime = 100f` | `KSPUtil.PrintDateCompact(cand.endUT, true)` | `SpawnTime` (`endUT`) |
| 5 | In T- | `In T-` | `SpawnColW_Countdown = 95f` | `SelectiveSpawnUI.FormatCountdown(endUT - now)` | `SpawnTime` (same key as col 4) |
| 6 | State | `State` (NOT sortable - plain label) | `SpawnColW_State = 110f` | `row.StateText` | - |
| 7 | (warp) | empty header | `SpawnColW_Warp = 118f` | `row.WarpButtonLabel` | - |

Widths `UI/SpawnControlUI.cs:56-60`; header row `:242-250`; row loop `:291-356`. Sort state is
`spawnSortColumn = Distance`, `spawnSortAscending = true` (`:38-39`); the sorted list is cached
and rebuilt only when the candidate count, `flight.ProximityCheckGeneration`, or the sort state
changes (`:253-268`). Comparison is in `SpawnControlPresentation.CompareCandidates`
(`UI/SpawnControlPresentation.cs:148-178`).

ROW MODEL: one `NearbySpawnCandidate` per row, wrapped by the pure
`SpawnCandidateRowPresentation` (`UI/SpawnControlPresentation.cs:21-39`) built by
`BuildRowPresentation` (`:88-133`). Colouring:

| condition | effect | file:line |
| --- | --- | --- |
| `ConditionsMet` (`!tooFar && !tooFast`) | Dist + Rel Speed tinted green `(0.55,1,0.55)` | `UI/SpawnControlUI.cs:296-305`, predicate `UI/SpawnControlPresentation.cs:94-96` |
| `StateTone = DepartingNow` | State text orange `(1,0.65,0.2)`, text `Departing -> {destination}` | `:316-322`, `UI/SpawnControlPresentation.cs:118-125` |
| `StateTone = UpcomingDeparture` | State text yellow `(1,1,0.4)`, text `Departs {countdown}` | same |
| `StateTone = None` | empty label of the same width (keeps the control count constant) | `:325-327` |

**(e) Action controls.**

| control | calls | disabled when | file:line |
| --- | --- | --- | --- |
| 5 sortable column headers | `ParsekUI.DrawSortableHeaderCore` -> flips `spawnSortColumn` / `spawnSortAscending` | never | `:215-221`, `ParsekUI.cs:1564` |
| row warp button (`Warp to Spawn` / `Warp to Depart`) | `flight.WarpToDeparture(recordingIndex, departureUT)` when `UsesDepartureWarp`, else `flight.WarpToRecordingEnd(recordingIndex)` (`ParsekFlight.cs:27367`) | `!row.WarpButtonEnabled` | `:330-355` |
| `Warp to Next Spawn` (expand-width) | `flight.WarpToNextCraftSpawn()` | `next == null` | `:388-398` |
| `Close` `Width(132)` | `showSpawnControlWindow = false` | never | `:399-403` |

**The "spawn candidates within N m" gate.** There are TWO envelopes, and the window exists to
show the difference:

| constant | value | role | file:line |
| --- | --- | --- | --- |
| `ParsekFlight.NearbySpawnRadius` | `250.0` m | INNER gate: warp button enable | `ParsekFlight.cs:423` |
| `ParsekFlight.MaxRelativeSpeed` | `2.0` m/s | INNER gate: warp button enable | `ParsekFlight.cs:429` |
| `ParsekFlight.NearbySpawnListRadius` | `1000.0` m (4x) | OUTER "show in list" bound | `ParsekFlight.cs:434` |
| `ParsekFlight.MaxListRelativeSpeed` | `50.0` m/s | OUTER "show in list" bound | `ParsekFlight.cs:435` |
| proximity scan cadence | `ProximityCheckIntervalSec = 1.5f` | - | `ParsekFlight.cs:422` |

Both inner constants are passed into `BuildRowPresentation` (`:287-289`) and into
`FindNextSpawnCandidate` for the bottom bar (`:368`). A candidate between the inner and outer
bounds is LISTED with a disabled warp button and no green tint - the design intent stated at
`ParsekFlight.cs:430-433`.

Disabled-warp reasons, in priority order (`UI/SpawnControlPresentation.cs:71-81`), surfaced
through `DisabledHoverEcho.CarryLastControl` because the row buttons carry no `GUIContent`
tooltip at all (`UI/SpawnControlUI.cs:332-335`):

1. `"Too far away to spawn"` (tooFar)
2. `"Passing too fast to spawn"` (tooFast)
3. `"This pass has already happened"` (`endUT`/`departureUT` <= now)

**(f) State variants.** **NO CAPTURE EXISTS IN EITHER CENSUS RUN.** `flight-spawncontrol-advanced`
is absent from `2026-09-11_0551_GUI-2-census-flight_shots/` by design: the GUI-2 spec declares
`{ cmd = "UiAction", args = { op = "open", window = "spawncontrol" }, expect = "ERROR" }` and
NOTHING follows it (`harness/scenarios/GUI-2-census-flight.toml:152`, rationale `:131-151`).
Runs `2026-09-10_2259` and `_2300` both answered
`uiaction error reason=window-self-closed window=spawncontrol frames=1` because the census
host's sub-orbital probe `R2-B2-S7` has zero nearby spawn candidates - the normal state.
The two-phase `op=open` settle exists precisely to stop a PNG of empty scenery being filed
under a label naming the window (`TestCommands/TestCommandUiAction.cs:655-665`).

Fixture and follow-up are already named in the tree: the open item is
`GUI-CENSUS-SPAWN-CONTROL-NEEDS-A-CANDIDATE-HOST`, whose fix is "a GUI-3 lane on a committed
fixture rather than an edit here" (`GUI-2-census-flight.toml:147-151`). The cheapest candidate
host is a fixture that already has a recorded craft passing within 250 m at under 2 m/s of the
focus, e.g. a station/dock pair - `harness/fixtures/saves/bdock-recorded`,
`bdock-station-craft` or `bdock-station-pad`.

Variants with no picture: empty list ("No nearby craft to spawn."), populated list with all
rows green, populated list with a too-far / too-fast row greyed, a `Departing ->` row, a
`Departs T-...` row, `Warp to Next Spawn` disabled.

**(g) Sizing.**

| fact | value | file:line |
| --- | --- | --- |
| default rect | `(mainRect.x + mainRect.width + 10, mainRect.y, 750, 200)` | `:147` |
| min width / height | `350` / `150` | `:74-75` |
| resize | `HandleResizeDrag` `:153`, handle `:406-407` |
| tooltip echo | `new TooltipEchoBox(SpacingSmall, TooltipEchoBox.SingleLine)` - single line, because the one tooltipped control's text fits at 750 px | `:66-67` |
| echo feed hack | `Warp to Next Spawn` sits BELOW the strip, so its tooltip is handed in manually from `warpButtonRect` captured on Repaint | `:69-71`, `:382-386`, `:396-397` |
| body | `BeginScrollView(..., ExpandHeight(true))` wrapping a `BeginVertical(GUI.skin.box)` dark list area | `:278-280` |
| input lock | `CAMERACONTROLS`, id `Parsek_SpawnControlWindow` | `:35`, `:182` |
| window id | `"ParsekSpawnControl".GetHashCode()` | `:163` |

---

## 7.6 Spawn warning / ghost label text (`SpawnWarningUI.cs`)

**(a) Kind.** Not a window. Two distinct surfaces: an in-world FLOATING OVERLAY label drawn
over each chain ghost, and a TOOLTIP-ONLY/row-text string consumed by the recordings table.
`SpawnWarningUI` itself is pure static text computation with no Unity calls; the file header
says rendering is `ParsekFlight`'s job (`SpawnWarningUI.cs:5-9`).

**(b) Class + draw method.**

| what | file:line |
| --- | --- |
| pure class | `SpawnWarningUI.cs:10` |
| ghost-label renderer | `ParsekFlight.cs:27052` (`DrawGhostLabels`), style at `:27046` |
| OnGUI call site | `ParsekFlight.cs:2106` |
| chain-status text producer | `ParsekFlight.cs:27119` (`GetChainStatusForRecording`) -> `SpawnWarningUI.FormatChainStatus` at `ParsekFlight.cs:27131` |
| chain-status consumers | `UI/RecordingsTableUI.cs:1963`, `UI/RecordingsTableUI.cs:5464` |

**(c) Scenes + gate.** FLIGHT only. `DrawGhostLabels` runs unconditionally in
`ParsekFlight.OnGUI` - OUTSIDE the `showUI` gate at `ParsekFlight.cs:2108` - but after the
pause-menu gate (`ParsekFlight.cs:2096-2097`). It no-ops when there are no active ghost chains
(`:27054-27055`), no `FlightCamera.fetch.mainCamera` (`:27058-27060`), no ghost GameObject
(`:27077-27078`), the ghost is behind the camera (`:27083-27084`) or off-screen
(`:27086-27089`).

**(d) Structure.** One `GUI.Label` per visible chain ghost. Two lines:
`"{vesselName}\n{line2}"` (`SpawnWarningUI.cs:149`). Rect is 250 x 40 px centred horizontally
on the ghost's screen position, bottom-anchored above it (`ParsekFlight.cs:27106-27113`).

| state predicate order | line 2 | file:line |
| --- | --- | --- |
| `isBlocked && isWalkbackExhausted` | `Ghost -- spawn abandoned` | `SpawnWarningUI.cs:130-133` |
| `isBlocked` | `Ghost -- spawn blocked` | `:134-137` |
| `isTerminated` | `Ghost -- chain terminated` | `:138-141` |
| otherwise | `Ghost -- spawns at UT={spawnUT:F0}` | `:142-147` |

Chain-status text for the recordings table, same predicate order:

| state | text | file:line |
| --- | --- | --- |
| blocked + walkback exhausted | `Spawn blocked -- walkback exhausted, manual placement required` | `:86-89` |
| blocked | `Spawn blocked -- waiting for clearance` | `:90-93` |
| terminated | `Ghosted -- chain terminated` | `:94-97` |
| otherwise | `Ghosted -- spawns at UT={SpawnUT:F0}` | `:98-103` |
| recording is a chain TIP (built in `ParsekFlight`, not here) | `Chain tip -- will spawn vessel at UT={0}` | `ParsekFlight.cs:27137-27139` |

**(e) Action controls.** NONE - this surface is entirely non-interactive.

DEAD CODE worth a reviewer's attention: `ShouldShowWarning` (`:23`) and `FormatWarningText`
(`:40`) have ZERO call sites outside the file. A repo-wide grep over `Source/Parsek/**/*.cs`
for those two names returns only their declarations, so the two warning strings
`"Spawn BLOCKED -- {0} overlaps, move vessel to clear"` (`:47-49`) and
`"Vessel '{0}' spawning -- {1}m from spawn point"` (`:53-56`) are never rendered anywhere.

**(f) State variants.** No census label. The gui-tree dumper walks IMGUI window/control
funnels, and these are bare `GUI.Label` calls outside any window, so they would not appear in a
`.gui.json` even if a ghost were on screen - only in the PNG. `flight-gloops-advanced` and
`flight-main-advanced` were both taken with `Active Ghosts: 0` (`flight-gloops-advanced`'s
dump of the main window, status label), so no ghost label was drawable. Producing one needs a flight with an
active ghost CHAIN, i.e. a committed recording plus a spawn chain - `mun-landing-recorded` or
`b2-lko-craft` style hosts.

**(g) Sizing.**

| fact | value | file:line |
| --- | --- | --- |
| label rect | 250 x 40 px, centred on the ghost, bottom edge at the ghost's screen Y | `ParsekFlight.cs:27106-27113` |
| style | cloned `GUI.skin.label`, `fontSize = 12`, Bold, `UpperCenter`, colour `(1, 0.9, 0.5, 0.9)` | `ParsekFlight.cs:27062-27070` |
| Y flip | `guiY = Screen.height - screenPos.y` (Unity screen Y is bottom-up, GUI is top-down) | `ParsekFlight.cs:27105` |

---

## Cross-surface notes

- Every one of the five windows ends with the same house footer order: tooltip echo strip,
  then the `Close` button as the last content row. Settings `:413-447`, TestRunnerUI
  `:487-498`, TestRunnerShortcut `:545-553`, Gloops `:343-350`, SpawnControl `:386-404`.
- Four of the five hold a `CAMERACONTROLS` input lock keyed on `rect.Contains(mousePosition)`;
  the global test runner holds a much wider lock set including `KSC_ALL` and six editor types.
- Four of the five expose `WindowRectForTesting` for the `UiAction op=rect` seam. The Settings
  one is documented as advisory in HEIGHT only (`UI/SettingsWindowUI.cs:245-250`).
  `TestRunnerShortcut` exposes nothing.

---

# Section 08 - Modal dialogs (PopupDialog surfaces)

All paths relative to `Source/Parsek`. Repo read-only; nothing was built, edited or flown.

**Transcription note.** Several shipped dialog strings contain a literal em dash in source
(for example `MergeDialog.DialogText.cs:93`, `MergeDialog.Commit.cs:249`,
`MergeDialog.Commit.cs:372`, `Patches/GhostVesselLoadPatch.cs:103`). House style for this
document is plain ASCII, so every quoted string below renders those as `-`. Read the cited
line for the exact byte.

## Grep confirmation

Program-wide grep re-run in this worktree:

```
grep -rn "PopupDialog.SpawnPopupDialog(" Source/Parsek --include=*.cs \
  | grep -v "InGameTests/" | grep -v "TestCommands/"
```

Returns exactly **21** sites, confirming the count supplied with the task. No site was
missed. Per-file tally: `CommittedActionDialog.cs` 1, `MergeDialog.cs` 3,
`ParsekTrackingStation.cs` 1, `ParsekUI.cs` 2, `Patches/GhostVesselLoadPatch.cs` 1,
`ReFlyRevertDialog.cs` 1, `RewindInvoker.cs` 1, `SceneExitInterceptor.cs` 1,
`UI/LogisticsWindowUI.cs` 3, `UI/MissionsWindowUI.cs` 1, `UI/RecordingsTableUI.cs` 3,
`UI/RouteCreationDialog.cs` 1, `UnfinishedFlightSealHandler.cs` 1,
`WarpToTimeController.cs` 1.

The 9 sites NOT documented here (`ParsekUI.cs:1505`, `ParsekUI.cs:1538`,
`UI/LogisticsWindowUI.cs:2582`, `:2623`, `:2726`, `UI/MissionsWindowUI.cs:3006`,
`UI/RecordingsTableUI.cs:4431`, `:4474`, `:4510`, `UI/RouteCreationDialog.cs:312`) exist
and are covered by other agents' sections.

## The no-picture claim, verified

PopupDialog surfaces are Unity uGUI, not IMGUI. The GUI-tree recorder intercepts only
IMGUI funnels: `GuiTreeFunnels.cs:10-32` enumerates the 17 funnels (`DoWindow`,
`CallWindowDelegate`, `BeginGroup`, ... `Slider`) and `GuiTreeFunnels.Target`
(`GuiTreeFunnels.cs:105-125`) resolves each to a `UnityEngine.GUI` method -
`GuiTreeFunnels.cs:109-114` resolves `DoWindow` to `AccessTools.Method(typeof(GUI),
"DoWindow", ...)`. `Patches/GuiTreeRecorderPatches.cs:223-232` documents and patches that
funnel (`GuiTreeDoWindowPatch.TargetMethod` at `:232-235` returns
`GuiTreeFunnels.Target(GuiFunnel.DoWindow)`). A grep for `PopupDialog`, `uGUI` or
`UnityEngine.UI` across `GuiTreeRecorder.cs`, `Patches/GuiTreeRecorderPatches.cs` and
`GuiTreeFunnels.cs` returns **zero hits**. So no `PopupDialog` in this section is captured
by `DumpGuiTree`.

Evidence runs confirm it empirically: `harness/results/2026-09-11_0548_GUI-1-census-ksc_shots/`
holds 22 PNGs and `.../2026-09-11_0551_GUI-2-census-flight_shots/` holds 4; a listing
filtered on `dialog|popup|merge|confirm|seal` returns nothing in either. Every surface
below is therefore a **no-picture surface**.

What WOULD produce a picture: the `CaptureScreenshot` seam verb
(`TestCommands/ParsekTestCommandAddon.CaptureScreenshot.cs:50`) grabs the framebuffer, so
it captures a uGUI popup - but only if something first raises the popup and holds it
open. That "something" is the missing piece for 10 of the 12 surfaces below.

## Seam answering paths

Two reflection-driven answering paths exist, both in `TestCommands/`:

| Path | Location | What it reaches |
|---|---|---|
| `AnswerMergeDialog` verb | `TestCommands/ParsekTestCommandAddon.cs:2246` (`AnswerMergeDialogImpl`) | Finds the live popup by `MergeDialog.DialogName` (`:2536`), selects a `DialogGUIButton` by ORDER (`TryInvokeMergeButton`, `:2544-2577`: merge -> `buttons[0]`, discard -> `buttons[Count-1]`, seal -> `buttons[1]` and `choice-unavailable` when `Count < 3`), then invokes that button's OWN callback via `DialogGUIButton.OptionSelected` (`:2575`). Choice mapping is pure: `TestCommands/TestCommandMergeAnswer.cs:126-140` (`merge`/`commit`/`discard`/`seal`). |
| `GetDialogButtons` + name scan | `TestCommands/ParsekTestCommandAddon.cs:2579-2594` (shared helper), used by the PlantFlag site-rename path at `TestCommands/ParsekTestCommandAddon.Eva.cs:820-833` (name scan for `"SiteRename"`) and `:839-854` (`TryInvokeSiteRenameDismiss`, invokes the LAST button) | Only the stock `"SiteRename"` popup. `GetDialogButtons` itself is generic and could serve any popup, but no caller targets a Parsek dialog other than `ParsekMerge`. |

**Hard gate on `AnswerMergeDialog`:** `ParsekTestCommandAddon.cs:2258-2260` computes
`markerLive = scenario.ActiveReFlySessionMarker != null` and only calls
`FindReFlyMergePopup()` when `markerLive` is true; with no marker it terminates
`ERROR no-live-dialog` (`:2267-2269`). So the verb can answer the merge dialog **only in
its Re-Fly variant**. An ordinary whole-tree merge dialog is unreachable by the seam.

## (08.1) Tree merge dialog - post-transition (deferred)

**(a) Name and kind.** "Confirm: Merge to Timeline" / "Confirm: Commit to Timeline" - modal dialog.

**(b) Class + spawn method.** `MergeDialog.ShowTreeDialog(RecordingTree)` -
`MergeDialog.cs:101`; spawn at `MergeDialog.cs:274`. Dialog name const `"ParsekMerge"` at
`MergeDialog.cs:14`.

**(c) Scenes + trigger.** Two producers:
- FLIGHT: `ParsekFlight.Finalization.cs:45`, the `OnFlightReady` fallback when a pending
  tree survived into the flight scene (`MaybeShowPendingTreeMergeDialogOnFlightReady`,
  `ParsekFlight.Finalization.cs:32`).
- Non-FLIGHT (SPACECENTER / TRACKSTATION): `ParsekScenario.cs:6114`, the deferred
  coroutine that surfaces the merge decision after `SceneExitInterceptor` missed the
  pre-transition catch (rationale at `ParsekScenario.cs:6095-6112`).

Input is hard-locked while it is open: `LockInput()` sets `ControlTypes.All` under lock id
`"ParsekMergeDialog"` (`MergeDialog.cs:14-15`, `:91-95`), armed at `MergeDialog.cs:272`,
released by `ClearPendingFlag` from every button callback and from `popup.OnDismiss`
(`MergeDialog.cs:288-291`).

**(d) STRUCTURE top-down.**

| Part | Exact text | Source |
|---|---|---|
| Title (ordinary / permanent) | `Confirm: Merge to Timeline` | `MergeDialog.DialogText.cs:38` via `BuildTimelineActionDialogTitle(true)`, called at `MergeDialog.cs:167` / `:199` |
| Title (Re-Fly, not-yet-sealable) | `Confirm: Commit to Timeline` | `MergeDialog.DialogText.cs:38` (`isPermanent == false`) |
| Body (ordinary) | `{TreeName} - {Duration}` | `BuildWholeTreeMergeDialogBody`, `MergeDialog.DialogText.cs:200`; called at `MergeDialog.cs:230`. `TreeName` falls back to `<unnamed>` (`:171`); duration is segment-scoped when a `SwitchSegmentSession` owns a recording in this tree, else tree-wide (`:182`, fallback `:198`) |
| Body (Re-Fly headline) | `<align="center">{vesselLabel} - {FormatDuration(reFlyDuration)}</align>\n\n` | `MergeDialog.DialogText.cs:82-83` |
| Body (Re-Fly, will NOT auto-seal) | `<align="left">Do you want to commit this Re-Fly attempt to the timeline?\n\nCommit (don't seal) keeps this Re-Fly slot open. Merge & Seal permanently closes it - you cannot Re-Fly this line again.</align>` | `MergeDialog.DialogText.cs:89-94` |
| Body (Re-Fly, WILL auto-seal) | `<align="left"><b>If not discarded, this Re-Fly attempt will be merged AND auto-sealed</b> for the following reason(s):\n{reasons}\n\nAuto-seal makes the slot permanent and you cannot Re-Fly this line again.</align>` | `MergeDialog.DialogText.cs:108-112` |
| Reason lines | one `- {phrase}` per reason; fallback `- auto-seal condition met` when the list is empty | `MergeDialog.DialogText.cs:115-129`; phrases from `ReFlyAutoSealPreview.cs:103-121` (`earned science`, `transmitted science`, `recovered science`, `undocked`, `sent a kerbal on EVA`, `broke off a part`, `the vessel broke up`, `docked with another vessel`, `the vessel was recovered`, `the kerbal boarded another vessel`, `landed`, `splashed down`, `reached a stable orbit`) |

The body reports: the tree (or the re-fly recording's) vessel name, the elapsed duration
of the work being decided, and - on the auto-seal branch only - the enumerated reasons the
slot will close.

**(e) Buttons and backends.**

| # | Label | Callback | Backend |
|---|---|---|---|
| 1 | `Merge to Timeline` (ordinary) / `Merge & Seal` (permanent Re-Fly) / `Commit (don't seal)` (non-permanent Re-Fly) - `MergeDialog.DialogText.cs:11-25`, wired at `MergeDialog.cs:243` and `:259` | `MergeCommit(tree, capturedDecisions, capturedSpawnCount)` | `MergeDialog.Commit.cs:35`; refuses on tree mismatch (`:57-67`) then `RecordingStore.CommitPendingTree` |
| 2 (3-button only) | `Merge & Seal` - `BuildReFlyMergeAndSealButtonLabel()`, `MergeDialog.DialogText.cs:33-34`, wired at `MergeDialog.cs:247` | `MergeCommit(..., playerRequestedSeal: true)` (`MergeDialog.cs:249-250`) | same, plus the seal tail that flips the slot tip to `MergeState.Immutable` (`MergeDialog.Commit.cs:371-391`) |
| last | `Discard` - `MergeDialog.cs:252` / `:263` | `MergeDiscard(tree)` | `MergeDialog.ReFlyDiscard.cs:19-22` -> `MergeDiscardRanToCompletion` (`:34`); refuses while a merge journal is active (`:44-50`) |

**Exact conditional predicate:** `MergeDialog.cs:240` -
`(isReFlyDialog && !timelineActionPermanent)` picks the 3-button set
`[mergeLabel, Merge & Seal, Discard]`; otherwise the 2-button set `[mergeLabel, Discard]`.
`isReFlyDialog` is set true at `MergeDialog.cs:171` when
`ParsekScenario.Instance.ActiveReFlySessionMarker != null` (`:168-169`);
`timelineActionPermanent` comes from `DetermineReFlyTimelineActionIsPermanent`
(`MergeDialog.cs:193-196`, defined `MergeDialog.DialogText.cs:41-62`), which prefers
`SupersedeCommit.TryPredictReFlyMergeIsPermanent` and falls back to
`preview.WillAutoSeal`.

**(f) STATE VARIANTS.**

| Variant | Predicate | Buttons | Picture |
|---|---|---|---|
| Ordinary whole-tree merge | no re-fly marker (`MergeDialog.cs:168-169` false) | 2: `Merge to Timeline`, `Discard` | NONE |
| Switch-segment merge | same shape; only a `[SwitchSegment]` log line differs (`MergeDialog.cs:221-227`), body duration is segment-scoped | 2 | NONE |
| Re-Fly, sealing terminal | marker live AND `timelineActionPermanent` | 2: `Merge & Seal`, `Discard` | NONE |
| Re-Fly, not yet sealable | marker live AND `!timelineActionPermanent` | 3: `Commit (don't seal)`, `Merge & Seal`, `Discard` | NONE |

`dismissOnSelect`: all four use the 2-arg `DialogGUIButton(string, Callback)` ctor, leaving
KSP's default. The in-repo statement of that default is `ParsekTrackingStation.cs:1253-1254`
("KSP dismisses DialogGUIButton after the handler returns"). Teardown is additionally
hooked at `MergeDialog.cs:288-291` to always clear the pending flag + input lock.

**(g) Sizing.** `SpawnPopupDialog` anchors `(0.5, 0.5)` / `(0.5, 0.5)`
(`MergeDialog.cs:275-276`); `MultiOptionDialog` built with the NO-width ctor
(`MergeDialog.cs:277-283`), so KSP's 300 px default `dialogRect` applies - the default is
named in-repo at `RewindInvoker.cs:517-519`. `persistAcrossScenes: false`
(`MergeDialog.cs:285`), skin `HighLogic.UISkin`.

## (08.2) Tree merge dialog - pre-transition (scene exit)

**(a) Name and kind.** "Confirm: Merge to Timeline" / "Re-Fly attempt - leaving flight" - modal dialog.

**(b) Class + spawn method.** `MergeDialog.ShowTreeDialog(RecordingTree, MergeDialogButtonLabels, Action, Action)` -
`MergeDialog.cs:322`; spawn at `MergeDialog.cs:468`.

**(c) Scenes + trigger.** FLIGHT only. The Harmony prefix on `HighLogic.LoadScene`
(`SceneExitInterceptor.cs:640`) blocks a flight-exit transition and raises it from three
call sites: the switch-segment-session branch (`SceneExitInterceptor.cs:763`), the
finalized-pending-tree branch (`:796`), and the live-`activeTree` branch (`:843`). The
button handler drives the blocked `LoadScene` via `BuildPostChoice`
(`SceneExitInterceptor.cs:621-631`).

**(d) STRUCTURE top-down.**

| Part | Exact text | Source |
|---|---|---|
| Title (`labels == Default`) | `Confirm: Merge to Timeline` | `MergeDialog.cs:386` |
| Title (`labels == ReFlyAttempt`) | `Re-Fly attempt - leaving flight` | `MergeDialog.cs:344` |
| Body (Default) | `{TreeName} - {Duration}` | `BuildWholeTreeMergeDialogBody`, called `MergeDialog.cs:401` |
| Body (ReFlyAttempt) | identical branch set to 08.1 | `BuildReFlyDialogBody`, called `MergeDialog.cs:373-374` |

**(e) Buttons and backends.** Every button routes through `RunPreTransitionAction`
(`MergeDialog.cs:805`), which runs `preCommitFinalize` (`:812`), rebuilds decisions on the
just-stashed pending tree (`:862-864`), then `MergeCommit` (`:870`) or
`MergeDiscardRanToCompletion` (`:876`), and only then `postChoice` (`:888`) - skipping
`postChoice` when the action refused (`:880-886`), leaving the player in flight.

| # | Label | Callback args | Backend |
|---|---|---|---|
| 1 | `Merge to Timeline` / `Merge & Seal` / `Commit (don't seal)` (`mergeLabel`, `MergeDialog.cs:387` or `:371`) | `RunPreTransitionAction(isMerge: true, ...)` - `MergeDialog.cs:424`, `:434`, `:454` | `MergeDialog.Commit.cs:35` |
| 2 (3-button only) | `Merge & Seal` - `MergeDialog.cs:438` | `RunPreTransitionAction(isMerge: true, ..., playerRequestedSeal: true)` (`:439-443`) | same + seal tail |
| last | `Discard` (`discardLabel`, set `MergeDialog.cs:345` / `:388`) | `RunPreTransitionAction(isMerge: false, ...)` - `MergeDialog.cs:444`, `:458` | `MergeDialog.ReFlyDiscard.cs:34` |

**Exact conditional predicates** - three-way, `MergeDialog.cs:419-461`:
1. `if (journalActive)` (`MergeDialog.cs:420`), where `journalActive` is
   `!object.ReferenceEquals(null, reFlyScenario) && reFlyScenario.ActiveMergeJournal != null`
   (`MergeDialog.cs:410-412`): **1 button**, `[mergeLabel]`. Discard is hidden for
   merge-journal safety (`:406-408`).
2. `else if (isReFlyDialog && !timelineActionPermanent)` (`MergeDialog.cs:430`):
   **3 buttons**, `[mergeLabel, Merge & Seal, discardLabel]`.
3. `else`: **2 buttons**, `[mergeLabel, discardLabel]`.

`isReFlyDialog` here is set purely from the caller's `labels` argument
(`MergeDialog.cs:342-345`), NOT from the live marker - that is the one behavioural
difference from 08.1.

**(f) STATE VARIANTS.** Four: journal-active 1-button (forced merge); Re-Fly
not-yet-sealable 3-button; Re-Fly sealing 2-button; ordinary 2-button. **All NONE.**
A picture needs a flight scene with an unmerged tree plus a `CaptureScreenshot` issued
while the blocked `LoadScene` sits waiting.

**(g) Sizing.** Anchors `(0.5, 0.5)` / `(0.5, 0.5)` (`MergeDialog.cs:469-470`), no-width
`MultiOptionDialog` ctor (`:471-477`), `persistAcrossScenes` false (`:479`).

## (08.3) Pre-switch decision dialog (rapid Switch-To)

**(a) Name and kind.** "Pending switch-segment recording" - modal dialog, Esc-proof.

**(b) Class + spawn method.** `MergeDialog.ShowPreSwitchDecisionDialog(Vessel, Action, Action, RecordingTree)` -
`MergeDialog.cs:736`; spawn at `MergeDialog.cs:726`.

**(c) Scenes + trigger.** FLIGHT map view. Raised from the Harmony Prefix on
`MapContextMenuOptions.FocusObject.OnSelect` at `Patches/MapFocusObjectOnSelectPatch.cs:503`
(Case A: a `SwitchSegmentSession` is armed and the new target differs from its focused PID)
and `:547` (Case B: no session, an in-flight recording exists, and `target.loaded == false`).
Both are filtered by `DecidePreSwitchDialogAction`. Refuses to spawn (returns false) when
the session's tree resolves to the `CommittedTrees` slot (`MergeDialog.cs:614-623`) -
Merge would no-op through the merge-commit-tree-mismatch guard.

**(d) STRUCTURE top-down.**

| Part | Exact text | Source |
|---|---|---|
| Title | `Pending switch-segment recording` | `MergeDialog.cs:646` |
| Body (tree resolved) | `{TreeName} - {Duration}` | `BuildWholeTreeMergeDialogBody`, called `MergeDialog.cs:631` |
| Body (compose threw) | `{priorTree.TreeName}` or `<unnamed>` | `MergeDialog.cs:639` |
| Body (no tree, session has a TreeId) | `Prior switch-segment session (tree id={session.TreeId})` | `MergeDialog.cs:644` |
| Body (no tree, no TreeId) | `Prior switch-segment session` | `MergeDialog.cs:644` |

**(e) Buttons and backends.** Fixed 2-button set, no conditional variant
(`MergeDialog.cs:668-707`):

| # | Label | Callback | Backend |
|---|---|---|---|
| 1 | `Merge` (`MergeDialog.cs:670`) | sets `buttonClicked = true`, logs `pre-switch-dialog-merge-chosen` (suppressed on Case B, `:672-678`), `ClearPendingFlag`, then `mergeAction.Invoke()` (`:680`) | caller-supplied closure from `Patches/MapFocusObjectOnSelectPatch.cs:503` / `:547` - finalize + stash + commit via the pre-transition merge path, then arm a fresh intent and call `FlightGlobals.SetActiveVessel(target)` (contract at `MergeDialog.cs:718-728` doc comment) |
| 2 | `Discard` (`MergeDialog.cs:691`) | same shape, `discardAction.Invoke()` (`:701`) | scoped discard via `RecordingStore.TryDiscardActiveSwitchSegmentAttempt`, then arm + `SetActiveVessel` |

There is deliberately **no Cancel** (`MergeDialog.cs:717-719` doc). Esc is defeated:
KSP's stock `PopupDialog.Update()` hard-codes Escape -> `Dismiss()` with no flag, so the
`OnDismiss` handler re-spawns the same dialog when no button was clicked
(`MergeDialog.cs:747-784`, re-spawn at `:781-782`), logging
`pre-switch-dialog-esc-refused-respawning case=case-A-session` or `case=case-B-no-session`.

**(f) STATE VARIANTS.** Case A (session armed) vs Case B (`priorTreeOverride != null`,
`MergeDialog.cs:666`) differ ONLY in the log lines, not in title, body shape, or buttons.
Both **NONE**. A picture needs two loaded/unloaded vessels in map view plus a
rapid Switch-To, which the seam refuses to model (see below).

**(g) Sizing.** Anchors `(0.5, 0.5)` / `(0.5, 0.5)` (`MergeDialog.cs:727-728`), no-width
`MultiOptionDialog` ctor (`:729-735`), `persistAcrossScenes: false` (`:736`).

**Surprise worth flagging:** this dialog reuses `MergeDialog.DialogName` = `"ParsekMerge"`
(`MergeDialog.cs:730`), the same name `FindReFlyMergePopup` matches on
(`ParsekTestCommandAddon.cs:2536`). Its button order `[Merge, Discard]` happens to satisfy
`TryInvokeMergeButton`'s merge=first / discard=last convention, so a live re-fly marker
plus a live pre-switch dialog would let `AnswerMergeDialog` answer the WRONG popup.

## (08.4) Re-Fly revert dialog

**(a) Name and kind.** "Revert during Re-Fly" - modal dialog.

**(b) Class + spawn method.** `ReFlyRevertDialog.Show(ReFlySessionMarker, RevertTarget, Action, Action, Action)` -
`ReFlyRevertDialog.cs:103`; spawn at `ReFlyRevertDialog.cs:236`. Name const
`"ParsekReFlyRevert"` at `:30`, lock id `"ParsekReFlyRevertDialog"` at `:29`.

**(c) Scenes + trigger.** FLIGHT. The Harmony prefix `RevertInterceptor.Prefix`
(`RevertInterceptor.cs:150`) blocks stock Revert-to-Launch / Revert-to-VAB-or-SPH whenever
`ParsekScenario.Instance.ActiveReFlySessionMarker != null` and calls
`ReFlyRevertDialog.Show` at `RevertInterceptor.cs:176-182`, returning false (`:185`) to
suppress the stock body.

**(d) STRUCTURE top-down.**

| Part | Exact text | Source |
|---|---|---|
| Title | `Revert during Re-Fly` | `ReFlyRevertDialog.cs:119` |
| Body line 1 | `You are in a Re-Fly session. Choose how to handle the current attempt.\n\n` | `ReFlyRevertDialog.cs:282` |
| Body Retry line | `- Retry from Rewind Point: restart this Re-Fly from the split moment in FLIGHT.\n` | `ReFlyRevertDialog.cs:258-259` |
| Body Discard line (journal active) | `- Discard Re-Fly: unavailable while a merge is in progress. Finish the merge or reload the save, then try again.\n` | `ReFlyRevertDialog.cs:264-266` |
| Body Discard line (`RevertTarget.Prelaunch`) | `- Discard Re-Fly: abandon this attempt, restore the rewind-point save, and return to the VAB or SPH. The STASH entry stays available.\n` | `ReFlyRevertDialog.cs:270-272` |
| Body Discard line (`RevertTarget.Launch`) | `- Discard Re-Fly: abandon this attempt, restore the rewind-point save, and return to the Space Center. The STASH entry stays available.\n` | `ReFlyRevertDialog.cs:276-278` |
| Body last line | `- Continue Flying: close this dialog and keep flying.` | `ReFlyRevertDialog.cs:285` |

**(e) Buttons and backends.**

| # | Label | Callback | Backend |
|---|---|---|---|
| 1 | `Retry from Rewind Point` (`ReFlyRevertDialog.cs:166`) | clears `DialogVisible`, `ClearLock()`, `onRetry()` (`:175`) | `RevertInterceptor.RetryHandler(capturedMarker, capturedTarget)` - `RevertInterceptor.cs:179`; discards the provisional, generates a fresh `SessionId`, re-invokes `RewindInvoker.StartInvoke` (`ReFlyRevertDialog.cs:12`) |
| 2 (3-button only) | `Discard Re-Fly` (`ReFlyRevertDialog.cs:183`) | same shape, `onDiscardReFly()` (`:192`) | `RevertInterceptor.DiscardReFlyHandler(capturedMarker, capturedTarget, capturedFacility)` - `RevertInterceptor.cs:180` |
| last | `Continue Flying` (`ReFlyRevertDialog.cs:200`) | same shape, `onCancel()` (`:209`) | `RevertInterceptor.CancelHandler(capturedMarker, capturedTarget)` - `RevertInterceptor.cs:181` |

**Exact conditional predicate:** `ReFlyRevertDialog.cs:219` -
`journalActive ? new MultiOptionDialog(..., retryButton, cancelButton) : new MultiOptionDialog(..., retryButton, discardButton, cancelButton)`,
where `journalActive = IsMergeJournalActive()` (`:120`), defined `:287-292` as
`ParsekScenario.Instance.ActiveMergeJournal != null`. A null callback is logged and
treated as a no-op so a dropped wire cannot strand the player (`:172-174`, `:189-191`,
`:206-208`).

**(f) STATE VARIANTS.** Six renderings: {journal-active 2-button, journal-idle 3-button}
x {Launch copy, Prelaunch copy, journal-notice copy}. The journal-notice discard line
replaces both target variants, so the effective set is 3 bodies x 2 button sets = 3
reachable combinations. **All NONE.** `DialogVisible` (`ReFlyRevertDialog.cs:65`) is the
in-game harness's substitute for a picture.

**(g) Sizing.** Anchors `(0.5, 0.5)` / `(0.5, 0.5)` (`ReFlyRevertDialog.cs:237-238`),
no-width `MultiOptionDialog` (`:220-234`), `persistAcrossScenes` false (`:240`).

## (08.5) Committed-action blocked dialog

**(a) Name and kind.** "Action Blocked" - modal dialog.

**(b) Class + spawn method.** `CommittedActionDialog.ShowBlocked(string, string, string)` -
`CommittedActionDialog.cs:14`; spawn at `CommittedActionDialog.cs:31`. Dialog name
`"ParsekResourceBlock"` (`:35`).

**(c) Scenes + trigger.** KSC-side scenes. Four Harmony patch producers, each passing a
different `actionDescription` / `reason` / `resourceDetail` triple:
`Patches/ContractAcceptPatch.cs:66`, `Patches/FacilityUpgradePatch.cs:73`,
`Patches/KerbalHirePatch.cs:64`, `Patches/TechResearchPatch.cs:67` and `:87`.

**(d) STRUCTURE top-down.**

| Part | Exact text | Source |
|---|---|---|
| Title | `Action Blocked` | `CommittedActionDialog.cs:37` |
| Body | `{actionDescription}\n\n{reason}` plus `\n\n{resourceDetail}` when that argument is non-empty | `CommittedActionDialog.cs:16-18` |

All three fields are caller-supplied; the dialog reports what the player tried, why the
committed timeline forbids it, and (optionally) the resource shortfall.

**(e) Buttons and backends.** One button, no conditional set: `OK`
(`CommittedActionDialog.cs:39`) with an empty lambda `() => { }` - acknowledgement only,
no backend call. The refusal itself already happened in the patch prefix.

**(f) STATE VARIANTS.** Two body shapes (with / without `resourceDetail`,
`CommittedActionDialog.cs:17`). Neither has a picture. A fifth "variant" is the test
bypass: `TestHookForTesting` (`CommittedActionDialog.cs:12`), when non-null, receives the
three strings and **returns before spawning** (`:24-29`) - so the in-game tests that
exercise this path (e.g. `InGameTests/IncompleteBallisticRuntimeTests.cs:2079-2081`)
never render it.

**(g) Sizing.** Anchors `(0.5, 0.5)` / `(0.5, 0.5)` (`CommittedActionDialog.cs:32-33`),
no-width ctor (`:34-40`), `persistAcrossScenes` false (`:41`).

## (08.6) Re-Fly confirmation dialog (rewind invoke)

**(a) Name and kind.** "Confirm: Re-Fly" - modal dialog. The only one in this section with
an explicit width.

**(b) Class + spawn method.** `RewindInvoker.ShowDialog(RewindPoint, int)` -
`RewindInvoker.cs:432`; spawn at `RewindInvoker.cs:509`. Dialog name
`"ParsekRewindInvoke"` (`:513`).

**(c) Scenes + trigger.** Any scene hosting the Parsek UI. Two producers: the Recordings
table's unfinished-flight row (`UI/RecordingsTableUI.cs:3521`) and the Timeline window's
Fly button (`UI/TimelineWindowUI.cs:1511`). Refuses on a null RP (`:434-438`) or an
out-of-range slot list index (`:439-444`).

**(d) STRUCTURE top-down.**

| Part | Exact text | Source |
|---|---|---|
| Title | `Confirm: Re-Fly` | `RewindInvoker.cs:448` |
| Body head | `Do you want to fly this again? This will take you to the moment after separation and you will be in control of the craft / Kerbal.` | `RewindInvoker.cs:449-451` |
| Body advisory para 1 | `The world goes back to how it stood at this rewind point. Your science, tech tree, funds, reputation, facility upgrades, milestones and contracts are carried forward. Anything outside those goes back with the clock: kerbal experience earned since then, resource-survey unlocks, contract waypoint progress, and deployed-science gathered since then.` | `RewindInvoker.cs:668-672`; the facet list is the const `PatchedCareerFacetsSummary` at `RewindInvoker.cs:570-571` |
| Body advisory para 2 (siblings present) | `Put away and replayed as ghosts, from this same separation: {capped prose list}.` | `RewindInvoker.cs:664-665`; cap `MaxAdvisorySlotNamesShown = 8` (`:751`) |
| Body advisory para 2 (no siblings) | `No other craft from this separation are put away.` | `RewindInvoker.cs:663` |
| Body advisory para 3 | `Craft you recovered after this point are back in flight, and their recovery rewards are returned to the ledger.` | `RewindInvoker.cs:674-675` |
| Body advisory para 4 | `Everything else stays where it is - your other vessels, stations, tracked asteroids and comets, and planted flags are all preserved.` | `RewindInvoker.cs:676-677` |

The advisory is appended at `RewindInvoker.cs:482` and degrades to absent on any throw
(`:495-501`), so a broken `RecordingStore` costs the paragraph, not the dialog. It is
skipped entirely when the selected slot is null (`:462-468`).

**(e) Buttons and backends.** Fixed 2-button set, no conditional variant:

| # | Label | Callback | Backend |
|---|---|---|---|
| 1 | `Fly` (`RewindInvoker.cs:521`) | logs `Invoked rec=... rp=... slot=... listIndex=...`, then `StartInvoke(capturedRp, capturedSelected)` (`:526`) | `RewindInvoker.StartInvoke` - the five-precondition gate + RP quicksave copy + post-load Restore/Strip/Activate |
| 2 | `Cancel` (`RewindInvoker.cs:528`) | logs `Cancelled rp=... slot=... listIndex=...` (`:530-532`); no state change | none |

**(f) STATE VARIANTS.** Three bodies: with siblings, without siblings, advisory-absent
(null slot or a throw). **All NONE.** Note the advisory is also emitted to KSP.log at Info
by `LogReFlyAdvisory` (`RewindInvoker.cs:685-694`) precisely so a Cancel still leaves a
record of what the player was told - a deliberate log-instead-of-picture design.

**(g) Sizing.** Anchors `(0.5, 0.5)` / `(0.5, 0.5)` (`RewindInvoker.cs:510-511`);
**explicit width `AdvisoryDialogWidth = 420f`** (`RewindInvoker.cs:559`) passed at
`:520`, with the rationale at `:517-519` / `:553-558`: the no-width ctor's 300 px default
renders the advisory as a tall narrow column. `persistAcrossScenes` false (`:534`).

## (08.7) Scene-exit save-failed popup

**(a) Name and kind.** "Save failed" - modal dialog.

**(b) Class + spawn method.** `SceneExitInterceptor.ShowSaveFailedPopup()` -
`SceneExitInterceptor.cs:581`; spawn at `SceneExitInterceptor.cs:585`. Dialog name
`"ParsekSceneExitSaveFailed"` (`:589`).

**(c) Scenes + trigger.** FLIGHT, and only on a quit-to-main-menu path.
`SafeWritePersistent` catches a throw and, when `destination == GameScenes.MAINMENU`,
Errors and calls `ShowSaveFailedPopup()` then returns false to hard-block the transition
(`SceneExitInterceptor.cs:563-572`). Any other destination logs a Warn and continues -
no dialog (`:573-577`).

**(d) STRUCTURE top-down.**

| Part | Exact text | Source |
|---|---|---|
| Title | `Save failed` | `SceneExitInterceptor.cs:592` |
| Body | `Could not save before quitting to main menu. Try again, or quit to Space Center first.` | `SceneExitInterceptor.cs:590-591` |

**(e) Buttons and backends.** One button, no conditional set: `OK` with an empty lambda
and **explicit `dismissOnSelect: true`** (`SceneExitInterceptor.cs:594`). No backend call;
the block already happened via the `false` return at `:571`. The whole spawn is wrapped in
try/catch - a spawn throw leaves the player with no UI feedback and only a Warn line
(`:598-603`).

**(f) STATE VARIANTS.** One. **NONE.** This is fault-injection territory: reaching it
requires `GamePersistence.SaveGame` to throw during a flight -> MAINMENU exit.

**(g) Sizing.** Anchors `(0.5, 0.5)` / `(0.5, 0.5)` (`SceneExitInterceptor.cs:586-587`),
no-width ctor (`:588-594`), `persistAcrossScenes: false` (`:595`).

## (08.8) Ghost icon context menu (map view)

**(a) Name and kind.** Ghost icon context menu - modal popup positioned at the cursor.
Title is the ghost's vessel name, body is empty.

**(b) Class + spawn method.** `GhostOrbitNodeClickPatch.Prefix(OrbitRendererBase, Mouse.Buttons)` -
`Patches/GhostVesselLoadPatch.cs:253`; spawn at `Patches/GhostVesselLoadPatch.cs:324`.
Dialog name `"ParsekGhostIconMenu"` (`:326`).

**(c) Scenes + trigger.** FLIGHT + map view only. Harmony prefix on
`OrbitRendererBase.objectNode_OnClick` (`Patches/GhostVesselLoadPatch.cs:221`). Four gates,
each returning `true` (pass through to stock): null vessel (`:255`), not a ghost map vessel
(`:256`), not FLIGHT-with-map (`:257`), and non-left click (`:262-263`, via
`TryPassThroughNonLeftClick` at `:241`). Any existing menu is dismissed first (`:284`).
Returns `false` at `:360` to suppress KSP's own context menu.

**(d) STRUCTURE top-down.**

| Part | Exact text | Source |
|---|---|---|
| Title | `{v.vesselName}`, or `Ghost` when null | `Patches/GhostVesselLoadPatch.cs:267`, passed at `:326` |
| Body | `""` (empty string - no message area) | `Patches/GhostVesselLoadPatch.cs:326` |

**(e) Buttons and backends.** Three, fixed set - no conditional variant, but the third
button's LABEL and ENABLED state are dynamic delegates re-evaluated per frame:

| # | Label | Callback | Backend |
|---|---|---|---|
| 1 | `Focus` (`:288`) | nulls `currentGhostMenu`, then `PlanetariumCamera.fetch.SetTarget(v.mapObject)` (`:293`) when camera + map + mapObject are all present; else Warns (`:298`) | stock `PlanetariumCamera` |
| 2 | `Set As Target` (`:301`) | nulls `currentGhostMenu`, then `GhostMapPresence.SetGhostMapNavigationTarget(v, recIndex, "icon click")` (`:304`) | `GhostMapPresence` |
| 3 | dynamic: `Stop Watching` / `Refresh Watch` / `Watch (Too Far)` / `Watch (Inactive)` / `Watch (Other SOI)` / `Watch` - `GhostMapWatchHelper.GetWatchButtonLabel`, `Patches/GhostVesselLoadPatch.cs:46-63`, wired as a delegate at `:306-309` | nulls `currentGhostMenu`, then `GhostMapWatchHelper.HandleWatchRequest(v, recIndex, source: "icon menu", toggleIfAlreadyWatching: true)` (`:312-316`) | `GhostMapWatchHelper` -> `ParsekFlight` watch mode |

Button 3's enabled predicate is `GhostMapWatchHelper.IsWatchActionEnabled` (`:317`, defined
`:65-70`): enabled only for `Start`, `Stop`, `Refresh`. The label state comes from
`GhostMapWatchHelper.ResolveWatchAction` fed live `HasActiveGhost` / `IsGhostOnSameBody` /
`IsGhostWithinVisualRange` reads (`:268-281`).

`dismissOnSelect`: buttons 1 and 2 pass **explicit `dismissOnSelect: true`** (`:300`,
`:305`); button 3 uses the 7-arg ctor with `true` in the `dismissOnSelect` position
(`:318`).

**(f) STATE VARIANTS.** Six label states for button 3, of which three are disabled
(greyed) and three are live. **All NONE.** Positioning is bespoke: `SetDraggable(false)`
(`:335`), a forced layout rebuild (`:339`), and a screen->canvas conversion so the menu
opens below the cursor (`:343-346`).

**(g) Sizing.** Anchors `Vector2.zero` / `Vector2.zero` (`:325`) - deliberately matching
KSP's `MapContextMenu` pattern (`:321-323`); `MultiOptionDialog` **width 160f** (`:327`);
button 3 sized `160f x 30f` (`:318`). `persistAcrossScenes: false` (`:328`).

## (08.9) Tracking Station ghost popup

**(a) Name and kind.** "Ghost" - modal popup positioned at the cursor.

**(b) Class + spawn method.** `ParsekTrackingStation.OpenSelectedGhostPopup(TrackingStationGhostSelectionInfo, string)` -
`ParsekTrackingStation.cs:1234`; spawn at `ParsekTrackingStation.cs:1276`. Dialog name
`"ParsekTrackingStationGhostMenu"` (`:1280`).

**(c) Scenes + trigger.** TRACKSTATION only. Driven each frame from
`UpdateSelectedGhostPopup` (`ParsekTrackingStation.cs:1154`), which dismisses on
pause-menu open (`:1156-1160`), on a cleared ghost selection (`:1162-1166`), on an
already-materialized recording (`:1169-1178`), and on a focus-only selection
(`:1181-1185`, via `ShouldOpenSelectedGhostPopup` at `:1197-1200` reading
`selection.ShowPopup`). It opens (or re-opens) when the composed key changes
(`:1187-1192`); the key folds in the ghost PID and a status phase
(`BuildGhostPopupKey` `:1202-1216`, `BuildGhostPopupStatusPhase` `:1218-1232`:
`no-recording` / `spawned` / `unknown-end` / `before-end` / `endpoint`).

**(d) STRUCTURE top-down.**

| Part | Exact text | Source |
|---|---|---|
| Title | `Ghost` (literal) | `ParsekTrackingStation.cs:1282` |
| Body | `Name: {0}\nRecording: {1}\nEnd state: {2}` | `BuildGhostPopupText`, `ParsekTrackingStation.cs:1460-1465`, called at `:1281` |
| Body field 1 | vessel name with any leading `Ghost:` prefixes stripped; `(ghost)` when empty | `FormatGhostPopupVesselName`, `:1468-1479` |
| Body field 2 | one of `unavailable` / `spawned` / `unknown` / `before endpoint` / `endpoint reached` | `BuildGhostPopupRecordingStatus`, `:1501-1516` |
| Body field 3 | `selection.TerminalState.Value.ToString()`, or `(unknown)` | `:1456-1458` |

**(e) Buttons and backends.** One button, no conditional set, but dynamic label + enabled:

| # | Label | Callback | Backend |
|---|---|---|---|
| 1 | `Warp to Spawn`, or `{baseLabel} ({duration})` when fast-forward-eligible with positive remaining time - `BuildMaterializeButtonLabel`, `ParsekTrackingStation.cs:1481-1499`, wired as a delegate at `:1258-1262` | nulls `currentGhostPopup` + `currentGhostPopupKey`, then `MaterializeSelectedGhost(selection, Planetarium.GetUniversalTime())` (`:1265-1267`) | `MaterializeSelectedGhost` -> `VesselSpawner` materialization |

The enabled predicate is `() => materialize.Enabled` (`:1269`), sourced from
`TrackingStationGhostActionPresentation.BuildActionStates(context)` (`:1248-1251`).
`dismissOnSelect` is the positional `true` at `:1272`; the comment at `:1253-1254`
records that KSP dismisses a `DialogGUIButton` after the handler returns, which is why the
callback nulls the reference FIRST.

**(f) STATE VARIANTS.** Five status phases x {enabled, disabled} x {plain label, label
with remaining-duration suffix}. **All NONE.** A picture needs a Tracking Station scene
with a selected, not-yet-materialized ghost.

**(g) Sizing.** Anchors `Vector2.zero` / `Vector2.zero` (`:1277-1278`);
`GhostPopupWidth = 180f` (`ParsekTrackingStation.cs:41`) passed at `:1284`; the button is
`160f x 30f` (`:1270-1271`). `persistAcrossScenes: false` (`:1286`), then repositioned to
the cursor by `PositionPopupAtCursor` (`:1290`).

## (08.10) Unfinished-flight seal confirmation

**(a) Name and kind.** "Confirm: Seal Unfinished Flight" - modal dialog.

**(b) Class + spawn method.** `UnfinishedFlightSealHandler.ShowConfirmation(Recording)` -
`UnfinishedFlightSealHandler.cs:190`; spawn at `UnfinishedFlightSealHandler.cs:214`.
Dialog name and lock id both `"ParsekUFSealDialog"` (`:16-17`).

**(c) Scenes + trigger.** Any scene hosting the Recordings table. One producer: the Seal
button in the unfinished-flights block, `UI/RecordingsTableUI.cs:3656` (button-click log
at `:3654-3655`). Refuses on a null recording (`:192-196`). Input is locked
`ControlTypes.All` at `:213` (`LockInput`, `:244-249`) and released by both button
callbacks (`:230`, `:237`).

**(d) STRUCTURE top-down.**

| Part | Exact text | Source |
|---|---|---|
| Title | `Confirm: Seal Unfinished Flight` | `UnfinishedFlightSealHandler.cs:220` |
| Body line 1 | `Seal "{vesselName}" ({terminal} at UT {ut})?` | `UnfinishedFlightSealHandler.cs:206` |
| Body line 2 | `This cannot be undone. After sealing, this entry is permanently merged to the timeline in its current state.` | `:207` |
| Body line 3 | `If you might want to re-fly this later, click Cancel.` | `:208` |

`vesselName` falls back to the recording id then `<unnamed>` (`:198`); `terminal` is the
CHAIN TIP's `TerminalStateValue` resolved through
`EffectiveState.ResolveChainTerminalRecording`, or `Unknown` (`:199-202`); `ut` is
`rec.EndUT` formatted `"F1"` under `CultureInfo.InvariantCulture` (`:203`).

**(e) Buttons and backends.** Fixed 2-button set, no conditional variant:

| # | Label | Callback | Backend |
|---|---|---|---|
| 1 | `Seal Permanently` (`:222`) | `TrySeal(captured, out string sealReason)` inside a try/finally that always calls `ClearLock()` (`:224-231`) | `UnfinishedFlightSealHandler.TrySeal` -> persist (`PersistSealBeforeReap`, `:154-188`) then RP reap |
| 2 | `Cancel` (`:233`) | logs `Seal cancelled rec=...` (`:235-236`), `ClearLock()` (`:237`) | none |

**(f) STATE VARIANTS.** One button set; body varies only by the interpolated vessel name,
terminal state and UT. **NONE.**

**(g) Sizing.** Anchors `(0.5, 0.5)` / `(0.5, 0.5)` (`:215-216`), no-width ctor
(`:217-239`), `persistAcrossScenes` false (`:240`).

## (08.11) Warp-to-time confirmation

**(a) Name and kind.** "Confirm: Warp to Time" - modal dialog.

**(b) Class + spawn method.** `WarpToTimeController.ShowConfirmation(WarpPlan, double, double, RewindTarget, Action)` -
`WarpToTimeController.cs:217` (private); spawn at `WarpToTimeController.cs:223`. Dialog
name `"ParsekWarpToTimeConfirm"` (`:227`).

**(c) Scenes + trigger.** FLIGHT and SPACECENTER. Single call site
`WarpToTimeController.cs:94`, inside `RequestWarp` (`:61`), reached from the Timeline
window's Warp button (`UI/TimelineWindowUI.cs:574`). Two earlier plan kinds short-circuit
to a screen message instead of the dialog: `AtTarget` -> `Already at this time` (`:81`)
and `Unreachable` -> `plan.Reason` (`:84`).

**(d) STRUCTURE top-down.**

| Part | Exact text | Source |
|---|---|---|
| Title | `Confirm: Warp to Time` | `WarpToTimeController.cs:229` |
| Body (ForwardOnly) | `Fast-forward to {targetDate}?\n\nTime will advance by {FormatDurationFull(delta)}.` | `WarpToTimeController.cs:254-255` |
| Body (RewindThenForward, CareerStart, target beyond epsilon) | `Reset to the start of the game, then fast-forward to {targetDate}?\n\nResources, facilities and the clock return to career start; your recordings are kept and replay as time advances.` | `:262`, `:264-265` |
| Body (RewindThenForward, CareerStart, at zero) | `Reset to the start of the game (Year 1, Day 1)?\n\nResources, facilities and the clock return to career start; your recordings are kept and replay as time advances.` | `:263`, `:264-265` |
| Body (RewindThenForward, `LandsAtTimelineStart`) | `Rewind to the earliest launch "{ownerName}" at {launchDate} (the start of your timeline)?\n\nAny uncommitted progress will be lost.` | `:272-273` |
| Body (RewindThenForward, ordinary launch target) | `Rewind to "{ownerName}" launch at {launchDate}, then fast-forward to {targetDate}?\n\nAny uncommitted progress will be lost.` | `:276-277` |

`ownerName` falls back to `the earliest launch` (`:268`); dates go through
`SafePrintDate` (`:249`, `:269`).

**(e) Buttons and backends.** Fixed 2-button set, no conditional variant:

| # | Label | Callback | Backend |
|---|---|---|---|
| 1 | `Warp` (`:231`) | logs `User confirmed warp: plan=... targetUT=... rewindKind=...` (`:233-235`), then `onConfirm?.Invoke()` (`:236`) | caller-supplied; in the KSC path `Execute` (`:286`) -> `TimeJumpManager.ExecuteForwardJump` (`:292`) or `StartRewind` (`:297`) |
| 2 | `Cancel` (`:238`) | logs `User cancelled warp confirmation` (`:240`); no state change | none |

**(f) STATE VARIANTS.** Five bodies (forward; career-start x2; timeline-start rewind;
ordinary launch rewind), one button set. **All NONE.**

**(g) Sizing.** Anchors `(0.5, 0.5)` / `(0.5, 0.5)` (`:224-225`), no-width ctor
(`:226-242`), `persistAcrossScenes` false (`:243`).

## Cross-cutting notes

- **Only one of the twelve has an explicit width** (08.6, 420 px). Eight use the no-width
  ctor and inherit KSP's 300 px default; the two cursor-anchored menus use 160 px (08.8)
  and 180 px (08.9).
- **Three surfaces bypass themselves under test.** `CommittedActionDialog.TestHookForTesting`
  (`CommittedActionDialog.cs:12`), `ReFlyRevertDialog.ShowHookForTesting` /
  `BodyHookForTesting` / `ButtonsHookForTesting` (`ReFlyRevertDialog.cs:40`, `:49`, `:57`),
  and `SceneExitInterceptor.ShowDialogForTesting` (referenced at `SceneExitInterceptor.cs:751`,
  `:782`, `:833`). Each short-circuits the spawn - so an in-game test never renders them.
- **Two dialogs the seam actively refuses to reach.** `SimulateStockSwitchClick` is
  documented PLAIN-PATH-ONLY and treats the pre-switch dialog cases as REFUSALS
  (`TestCommands/ParsekTestCommandAddon.SimulateSwitchClick.cs:35-45`); `InvokeRewind`
  routes through `RewindInvoker.CanInvoke` + `StartInvoke` directly, skipping
  `RewindInvoker.ShowDialog` entirely (`TestCommands/ParsekTestCommandAddon.cs:2124-2129`).
  `SealSlot` likewise calls `UnfinishedFlightSealHandler.TrySeal` directly
  (`TestCommands/ParsekTestCommandAddon.SealSlot.cs:21`, `:92`, `:221`), never
  `ShowConfirmation`. No seam verb references `WarpToTimeController` at all (grep over
  `TestCommands/*.cs` returns nothing).

---

# Section 09 - Overlays, map markers, stock-UI badges, tooltip infrastructure

Every player-facing surface Parsek draws that is NOT a window and NOT a modal dialog.
All paths relative to `Source/Parsek/`.

Program-wide derivation for this section (raw greps, not memory):

| grep | result |
|---|---|
| `void OnGUI()` in production code | `CurrencyReservationOverlay.cs:87`, `OverlayBadge.cs:122`, `ParsekFlight.cs:2087`, `ParsekKSC.cs:227`, `ParsekTrackingStation.cs:350`, `InGameTests/TestRunnerShortcut.cs:177` - six, no more |
| files with IMGUI draw calls outside `UI/` | `CurrencyReservationOverlay.cs`, `MapMarkerRenderer.cs`, `OverlayBadge.cs`, `ParsekFlight.cs`, `ParsekKSC.cs`, `ParsekUI.cs`, `WatchModeController.cs`, plus the `GuiTree*` recorder infrastructure (not player-facing) |
| producers of `OverlayBadge` | exactly one: `StockUiOverlayController.cs:980` |

---

## 9.1 Watch Mode overlay

(a) Name and kind: "Watching: <vessel>" heads-up box. Kind: **overlay** (screen-space IMGUI, no window chrome, not interactive).

(b) Class + draw method: `WatchModeController.DrawWatchModeOverlay()`, `WatchModeController.cs:1054`. Called from the flight OnGUI at `ParsekFlight.cs:2103`.

(c) Scenes / gate: FLIGHT only (the only caller is `ParsekFlight.OnGUI`). Gate: `watchedRecordingIndex >= 0`, documented at `WatchModeController.cs:1052`. No `UiSurface` key, no setting - watch mode is entered from a Watch button (Missions tab / recordings table) and the overlay is unconditional while it is active.

(d) Structure top-down (fixed 300x50 box, `WatchModeController.cs:1094`):

| row | rect | content | style |
|---|---|---|---|
| background | `(x, y, 300, 50)`, `WatchModeController.cs:1097` | `Texture2D.whiteTexture` tinted `(0,0,0,0.5)` | `WatchModeController.cs:1099-1100` |
| title | `(x, y+5, 300, 22)`, `WatchModeController.cs:1108` | `"Watching: " + vesselName [+ "  (" + distText + ")"] + modeLabel` | 16 px bold, white, UpperCenter (`WatchModeController.cs:1058-1064`) |
| hint | `(x, y+27, 300, 18)`, `WatchModeController.cs:1109` | literal `"[ ] return  |  V camera  |  W cycle"` | 12 px, white at 0.7 alpha, UpperCenter (`WatchModeController.cs:1065-1070`) |

Position: `x = (Screen.width * 0.5f - 300) / 2f`, `y = 10f` - centred in the LEFT HALF of the screen (`WatchModeController.cs:1095-1096`). At 1280 px that is x = 170.

Content derivations:
- `vesselName` from `RecordingStore.CommittedRecordings[watchedRecordingIndex].VesselName`, bounds-checked (`WatchModeController.cs:1074-1076`).
- `distText` = ghost-to-active-vessel distance, `F0` + `" m"` under 1000 m, otherwise `/1000` `F1` + `" km"`, InvariantCulture (`WatchModeController.cs:1085-1091`). Omitted entirely when the ghost state or `FlightGlobals.ActiveVessel` is null (`WatchModeController.cs:1082-1083`, `1105-1107`).
- `modeLabel` = `" [Horizon]"` when `currentCameraMode == WatchCameraMode.HorizonLocked`, else `" [Free]"` (`WatchModeController.cs:1103-1104`).

(e) Action controls: **none**. The overlay is pure output; the three keys it advertises (`]`, `V`, `W`) are handled elsewhere in input polling, not by this draw.

(f) State variants:

| variant | condition | picture |
|---|---|---|
| with distance + `[Free]` | ghost alive, camera free | NONE |
| with distance + `[Horizon]` | `currentCameraMode == HorizonLocked` (`WatchModeController.cs:1103`) | NONE |
| no distance (`"Watching: X [Free]"`) | ghost state missing or no active vessel (`WatchModeController.cs:1082`) | NONE |
| metres vs kilometres wording | `dist < 1000` (`WatchModeController.cs:1088`) | NONE |

No census label shows it: both census runs photograph windows, and neither entered watch mode. Cheapest reach: a flight fixture with at least one committed recording plus the `EnterWatchMode` seam verb, which reproduces `MissionsWindowUI.DrawMissionWatchButton` (see `TestCommands/ParsekTestCommandAddon.EnterWatchMode.cs:6` and `:31`). The GuiTree recorder would NOT capture it even then - see 9.9.

(g) Sizing: hard-pinned `boxW = 300f, boxH = 50f` (`WatchModeController.cs:1094`); no min/max, no resize, no `GUILayout` at all (three explicit-rect `GUI.*` calls).

---

## 9.2 Currency reservation tooltip (funds / science)

(a) Name and kind: hover tooltip over the STOCK funds and science widgets. Kind: **overlay (tooltip-only surface)**.

(b) Class + draw method: `CurrencyReservationOverlay.OnGUI()`, `CurrencyReservationOverlay.cs:87`, drawing through `DrawTooltipIfHover`, `CurrencyReservationOverlay.cs:99`, final paint `GUI.Box` at `CurrencyReservationOverlay.cs:129`.

(c) Scenes / gate: `[KSPAddon(KSPAddon.Startup.EveryScene, false)]` (`CurrencyReservationOverlay.cs:26`) but `Start` self-idles unless the scene is `SPACECENTER` or `FLIGHT` (`CurrencyReservationOverlay.cs:41-46`). Additional gates: `active` (`:91`), the widget RectTransform having been found by the 1 Hz refresh loop (`:60-85`), the screen rect containing the mouse (`:105`), and the provider returning non-empty text (`:109`).
**There is no setting gate.** `CurrencyReservationOverlay.cs:89-90` states it explicitly: committed-future overlays are unconditional since the 2026-08-27 settings simplification removed `showCommittedFutureOverlays`.
Reputation is deliberately NOT decorated (`CurrencyReservationOverlay.cs:22-24`): it is never reserved, so its tooltip would always read "Reserved: 0".

(d) Structure: a single `GUI.Box` of pinned width 147 px (`CurrencyReservationOverlay.cs:30`), height from `style.CalcHeight` (`:126`), `MiddleLeft`, `wordWrap = false`, padding 10/10/8/8 (`:117-122`). Placed at `mouse.x + 16` clamped to `Screen.width - 147 - 8`, and `Screen.height - mouse.y + 16` clamped to `Screen.height - height - 8` (`:127-128`).

Body text forms (pure builder `BuildReservationTooltip`, `CurrencyReservationOverlay.cs:184`):

| case | condition | text |
|---|---|---|
| deficit, no deeper future | `total < 0` and `minProjected >= total` (`:187-192`) | `Balance: <total>` |
| deficit, deeper future | `total < 0` and `minProjected < total` (`:190-191`) | `Balance: <total>` + newline + `Short by: <-minProjected>` |
| normal | `total >= 0` (`:199-200`) | `Total: <total>` + newline + `Reserved: <total-available, floored at 0>` |
| normal + over-commit | additionally `minProjected < 0` (`:201-202`) | appends newline + `Short by: <-minProjected>` |

Number formats: funds `"N0"` (`CurrencyReservationOverlay.cs:223`), science `"F1"` (`:236`), both `CultureInfo.InvariantCulture` (`:31`).

(e) Action controls: **none** (hover-only, no click handling).
Conditional hides: the whole tooltip is skipped when `LedgerOrchestrator.Funds == null || Funding.Instance == null` (`:216-217`) or `LedgerOrchestrator.Science == null || ResearchAndDevelopment.Instance == null` (`:229-230`).

(f) State variants: four text forms above, times two widgets. **None has a picture** - the tooltip is a mouse-hover state and the census captures have no pointer parked on the stock currency bar. It is also outside the GuiTree recorder's reach (9.9). Reaching one needs a career fixture with a committed future (e.g. `harness/fixtures/saves/` career states) plus a NEW SEAM OP that parks the pointer over the stock funds widget and captures a full-screen shot; no existing verb moves the mouse to a stock uGUI widget rect.

(g) Sizing: width pinned 147 px (`CurrencyReservationOverlay.cs:30`, comment says "2/3 of the original 220px width"); height computed per text; no minimum.

---

## 9.3 Stock-UI overlay badges (R&D / Astronaut Complex / Mission Control)

(a) Name and kind: small 18x18 icon badges injected into stock KSP screens, each with its own hover tooltip. Kind: **overlay + tooltip-only** (the tooltip carries information that exists nowhere else).

(b) Classes:
- producer `StockUiOverlayController`, `[KSPAddon(KSPAddon.Startup.SpaceCentre, false)]`, `StockUiOverlayController.cs:49-50`; attach helper `AttachBadge` at `StockUiOverlayController.cs:967-982`.
- renderer `OverlayBadge` (MonoBehaviour + `IPointerEnterHandler`/`IPointerExitHandler`), `OverlayBadge.cs:7`; tooltip paint `OverlayBadge.OnGUI()`, `OverlayBadge.cs:122`, `GUI.Box` at `OverlayBadge.cs:140`.

(c) Scenes / gate: SPACECENTER addon, but the three decorate passes are driven by stock spawn events registered at `StockUiOverlayController.cs:71-76`:

| screen | spawn hook | decorate method | badge object name |
|---|---|---|---|
| R&D tech tree | `RDController.OnRDTreeSpawn` (`:71`) | `DecorateRd`, badge attach `StockUiOverlayController.cs:233` | `Parsek_TechOverlay` (`:53`) |
| Astronaut Complex | `GameEvents.onGUIAstronautComplexSpawn` (`:73`) | `DecorateAstronaut` (`:244`), badge attach `:293` | `Parsek_KerbalOverlay` (`:54`) |
| Mission Control | `GameEvents.onGUIMissionControlSpawn` (`:75`) | badge attach `StockUiOverlayController.cs:354` | `Parsek_ContractOverlay` (`:55`) |

Also re-decorates on `LedgerOrchestrator.OnTimelineDataChanged` (`StockUiOverlayController.cs:77`). A badge self-destructs when its stock parent changes or is destroyed (`OverlayBadge.cs:112-120`).

(d) Structure of one badge: a `RawImage` anchored to the TOP-RIGHT of the stock row/node, pivot (1,1), `sizeDelta` 18x18, `anchoredPosition` (-6,-6) (`OverlayBadge.cs:35-39`). Texture `Squad/Alarms/Icons/default` (`OverlayBadge.cs:9`), falling back to `Texture2D.whiteTexture` when the GameDatabase lookup fails (`OverlayBadge.cs:81`). `raycastTarget = true` (`:47`) - that is what makes the hover work.

Tint colour is the badge's whole semantic channel:

| screen / kind | colour | file:line |
|---|---|---|
| R&D committed tech | `(1.00, 0.84, 0.22, 0.95)` amber | `StockUiOverlayController.cs:235` |
| Mission Control committed contract | `(0.35, 0.74, 1.00, 0.95)` blue | `StockUiOverlayController.cs:358` |
| applicant `FutureHired` | `(0.33, 0.86, 0.48, 0.95)` green | `StockUiOverlayController.cs:987-988` |
| applicant `FutureRetired` | `(0.62, 0.62, 0.62, 0.95)` grey | `StockUiOverlayController.cs:989-990` |
| applicant `ReservedActive` | `(1.00, 0.70, 0.28, 0.95)` orange | `StockUiOverlayController.cs:991-992` |
| applicant `ReservedRetired` | `(0.78, 0.48, 0.95, 0.95)` violet | `StockUiOverlayController.cs:993-994` |

Tooltip box: pinned 320x54 (`OverlayBadge.cs:10-11`), `GUI.skin.box`, `MiddleLeft`, `wordWrap = true`, padding 10/10/8/8 (`OverlayBadge.cs:129-134`), positioned `mouse.x+16` / `Screen.height-mouse.y+16`, both clamped 8 px from the edge (`OverlayBadge.cs:138-139`).

Tooltip texts:

| form | builder | file:line |
|---|---|---|
| `Committed at UT <ut>` [+ ` - recording '<VesselName>'`] [+ ` (+N more committed)`] | `BuildCommittedTooltip` | `StockUiOverlayController.cs:689-706`; prefix supplied at `:386` |
| `Will be accepted at UT <ut>` + same suffixes | same builder, prefix at `StockUiOverlayController.cs:416` | |
| `Reserved by Parsek for a committed crew slot` | `BuildReservedActiveTooltip` no slot owner | `StockUiOverlayController.cs:621-622` |
| `Reserved by Parsek for slot '<owner>'` | `BuildReservedActiveTooltip` with owner | `StockUiOverlayController.cs:623` |
| `Retired stand-in (managed by Parsek)` | literal | `StockUiOverlayController.cs:489` |

(e) Action controls: **none**. The badge is not clickable; `raycastTarget` exists only to receive pointer enter/exit (`OverlayBadge.cs:102-110`).
Conditionally hidden: a badge is attached only for a row whose key is present in the marks dictionary (`StockUiOverlayController.cs:230-232`, `:288-290`, `:352-356`); an applicant row whose name is not in the CrewRoster is skipped with a log (`StockUiOverlayController.cs:283-285`); an event mark is skipped unless `GameStateStore.IsEventVisibleToCurrentTimeline(ev)` (`StockUiOverlayController.cs:643`).

(f) State variants: 6 tint kinds x 5 tooltip forms. **NONE has a picture.** Neither census run opened the R&D, Astronaut Complex or Mission Control screens, and even if they had, `OverlayBadge` is a uGUI `RawImage` (invisible to the GuiTree recorder, 9.9) whose tooltip only paints on hover. Cheapest reach: the `c2` career fixture (the ledger subject) with a committed tech/contract/hire, plus a NEW SEAM OP to open a stock facility screen and hover a row.

(g) Sizing: badge 18x18 at (-6,-6) from the row's top-right (`OverlayBadge.cs:38-39`); tooltip 320x54 pinned (`OverlayBadge.cs:10-11`).

---

## 9.4 Ghost map markers (flight map view)

(a) Name and kind: per-ghost map icon plus an optional label. Kind: **marker**.

(b) Class + draw method: `MapMarkerRenderer.DrawMarkerAtScreen`, `MapMarkerRenderer.cs:208` (icon paint `MapMarkerRenderer.cs:292-306`, label `MapMarkerRenderer.cs:311`). Driven by `ParsekUI.DrawMapMarkers()`, `ParsekUI.cs:1901`, whose single-marker tail calls it at `ParsekUI.cs:2763`. Hosted by `ParsekFlight.OnGUI` at `ParsekFlight.cs:2100`.

(c) Scenes / gate: FLIGHT (map view). The per-ghost draw decision is `GhostMapPresence.ShouldDrawNonProtoMarkerForGhost(pid)` (`GhostMapPresence.cs:1133`), a Unity wrapper over the pure `ResolveMarkerDrawDecision` (`GhostMapPresence.cs:1120`). The custom marker draws only when the STOCK `MapNode` for the same recording is absent or suppressed (`MapMarkerRenderer.cs:26-29`), so a ghost that has a ProtoVessel shows the stock icon instead and is never double-labelled. `ParsekUI.cs:2546` is the matching suppression check on the polyline tail.

(d) Structure of one marker:

| element | size / value | file:line |
|---|---|---|
| icon | 20 px square (`IconSize`) | `MapMarkerRenderer.cs:34` |
| left-click hit pad | +6 px per side (`ClickPadding`) | `MapMarkerRenderer.cs:35` |
| right-click sticky-toggle hit pad | +24 px per side (`ToggleClickPadding`) | `MapMarkerRenderer.cs:44` |
| icon tint | `(0.71, 0.71, 0.71, 1)` = stock `nodeColor` | `MapMarkerRenderer.cs:57`, applied `:298` |
| fallback sprite tint | `WithMarkerOpacity(color, sticky)`, unpinned alpha 0.8 | `MapMarkerRenderer.cs:45`, `:303`, `:331` |
| sprite choice | `StockIconIndexByVesselType` (Station 0, Base 5, Debris 7, ...) | `MapMarkerRenderer.cs:65-70` |
| label | drawn only when `ShouldDrawLabel(sticky, hover)` = `sticky \|\| hover` | `MapMarkerRenderer.cs:324`, paint `:311` |

The asymmetric hit pads are deliberate and documented at `MapMarkerRenderer.cs:36-43`: a 26 px target moves too far between frames at 125x warp for a right-click to land, so ONLY the toggle rect was widened to 68 px.

(e) Action controls:

| control | condition | calls |
|---|---|---|
| right-click on icon | `IsToggleClick(type, button)` (`MapMarkerRenderer.cs:348`) within `ComputeToggleHitRect` (`:357`) | `ToggleSticky(key, stickyMarkers)` (`MapMarkerRenderer.cs:409`) - pins/unpins the label, keyed by recording id |
| left-click on icon | `IsHandlerClick` (`MapMarkerRenderer.cs:371`) and `ShouldRouteMarkerClickToHandler` (`:374`) | the supplied handler. In FLIGHT map view **no handler is supplied**, so the click falls through to KSP's stock map handlers (`MapMarkerRenderer.cs:22-26`). In the Tracking Station the handler opens the ghost popup (9.5). |
| all clicks | gated by `AllowClickInteraction()` | `MapMarkerRenderer.cs:472` |

Sticky set is cleared on scene change (`MapMarkerRenderer.ResetForSceneChange`, `MapMarkerRenderer.cs:429`).

(f) State variants: icon only / icon + hover label / icon + sticky label; stock-atlas sprite vs fallback diamond (`MapMarkerRenderer.cs:99`, `:304`) when the atlas sprite is missing; one per VesselType. **None has a picture** - both census runs are non-map (KSC scene and flight scene main window), and the markers are painted on the map camera. Reaching them needs a flight fixture with a committed recording plus an `EnterMapView` seam verb (one exists per the M-A7 render-composition work) and a full-screen capture; the GuiTree recorder would still produce nothing because these are bare `GUI.DrawTexture` / `GUI.Label` calls outside any `GUI.Window` (9.9).

(g) Sizing: everything pinned as above; no window, no layout, no minimum.

---

## 9.5 Ghost map markers (Tracking Station)

(a) Name and kind: the same markers, in the TS scene. Kind: **marker**.

(b) Class + draw method: `ParsekTrackingStation.OnGUI()` -> `DrawAtmosphericMarkers()`, `ParsekTrackingStation.cs:350` and `:364`, delegating to `MapMarkerRenderer.DrawMarker` (`MapMarkerRenderer.cs:180`).

(c) Scenes / gate: TRACKSTATION. Gated first by `PauseMenuGate.IsPauseMenuOpen()` (`ParsekTrackingStation.cs:357-358`) - both Layout AND Repaint are skipped so width-clamped layouts cannot flicker (`ParsekTrackingStation.cs:352-356`). Per-recording gate: `ClassifyAtmosphericMarkerSkip` (`ParsekTrackingStation.cs:844`), whose 8c proto gate consults the SAME `GhostMapPresence.ShouldDrawNonProtoMarkerForGhost` the flight map uses (`ParsekTrackingStation.cs:856-864`).

**Structural fact worth flagging:** `ParsekTrackingStation.cs:394-395` states in-source that "No Parsek window is hosted in the Tracking Station scene", and the program-wide `ClickThruBlocker.GUILayoutWindow` grep confirms it - the only two window hosts are `ParsekFlight.cs:2118` and `ParsekKSC.cs:245`. The Tracking Station gets markers and a ghost popup and nothing else.

(d) Structure: identical marker anatomy to 9.4 (same renderer). Additional TS-only behaviour: a marker click is BLOCKED when the pointer is over the currently open ghost popup (`ShouldBlockAtmosphericMarkerClickForGhostPopup`, `ParsekTrackingStation.cs:380-388`), and `MouseDown` events are deliberately allowed through (`ParsekTrackingStation.cs:367-372`) because an earlier Repaint-only gate made click-to-pin dead in TS.

(e) Action controls: left-click routes to the ghost popup handler (9.6); right-click toggles the sticky label as in 9.4.

(f) State variants: same as 9.4 plus popup-open (clicks blocked) vs popup-closed. **No picture** - the census ran KSC and FLIGHT only; there is no TS census lane. A TS lane would need a fixture with committed recordings plus a scene-change seam step into TRACKSTATION.

(g) Sizing: as 9.4.

---

## 9.6 Ghost popup / ghost icon context menu

Both are uGUI `PopupDialog`s and belong with the dialogs section; noted here only so the marker chain is complete:
- Tracking Station ghost popup: `ParsekTrackingStation.cs:1276` (`PopupDialog.SpawnPopupDialog`), buttons built at `ParsekTrackingStation.cs:1255-1273`.
- Flight-map ghost icon menu (`Focus` / `Set As Target` / a third dynamic button): `Patches/GhostVesselLoadPatch.cs:324`, options at `Patches/GhostVesselLoadPatch.cs:286-318`, dialog width/height `160f, 30f` (`Patches/GhostVesselLoadPatch.cs:318`).

---

## 9.7 TooltipEchoBox - the per-window bottom help strip

(a) Name and kind: the permanently visible one- or two-line "hovered control help text" strip at the bottom of most Parsek windows. Kind: **overlay / in-window section**, and the single most load-bearing tooltip surface in the mod.

(b) Class + draw method: `TooltipEchoBox.Draw(string manualOverride = null)`, `UI/TooltipEchoBox.cs:215`; text resolution `ResolveCapturedText`, `UI/TooltipEchoBox.cs:194`.

(c) Hosts and line count (one instance per window; construction site -> draw site):

| window | construction | lines | draw call |
|---|---|---|---|
| Main window (`ParsekUI`) | `ParsekUI.cs:210` | 2 (DoubleLine default) | `ParsekUI.cs:977` |
| Career State | `UI/CareerStateWindowUI.cs:70` | 1 (`SingleLine`) | `UI/CareerStateWindowUI.cs:1388` |
| Gloops Recorder | `UI/GloopsRecorderUI.cs:43` | 2 | `UI/GloopsRecorderUI.cs:343` |
| Kerbals | `UI/KerbalsWindowUI.cs:53` | 2 | `UI/KerbalsWindowUI.cs:379` |
| Logistics | `UI/LogisticsWindowUI.cs:379` | 1 | `UI/LogisticsWindowUI.cs:560` |
| Missions/Recordings | `UI/RecordingsTableUI.cs:310` | 1 | `UI/RecordingsTableUI.cs:6066` (with manual override `recordingsWindowTooltipText`) |
| Settings | `UI/SettingsWindowUI.cs:42` | 2 | `UI/SettingsWindowUI.cs:413` |
| Real Spawn Control | `UI/SpawnControlUI.cs:67` | 1 | `UI/SpawnControlUI.cs:386` (manual override `bottomBarEcho`) |
| Test Runner (Settings-launched) | `UI/TestRunnerUI.cs:65` | 2 | `UI/TestRunnerUI.cs:487` |
| Timeline | `UI/TimelineWindowUI.cs:87` | 1 | `UI/TimelineWindowUI.cs:507` |
| Ctrl+Shift+T Test Runner | `InGameTests/TestRunnerShortcut.cs:38` | 2 | (in that window's draw) |

Windows with NO echo strip (derived by differencing the construction list against the 14 window hosts): `UI/GroupPickerUI.cs`, `UI/StructureListWindowUI.cs`, and the Logistics link picker (`UI/LogisticsWindowUI.cs:1762`).

(d) Structure: exactly one `GUILayout.Space(spacing)` + exactly one `GUILayout.Label` per draw (`UI/TooltipEchoBox.cs:233-250`). Empty when nothing is hovered. Height is pinned to a probe measurement of 1 or 2 lines (`UI/TooltipEchoBox.cs:78-79`, `NoWrapMeasureWidth = 10000f` at `:82`), so the window never changes height on hover (`UI/TooltipEchoBox.cs:12-32`).

(e) Behaviour: text precedence is manual override > `GUI.tooltip` (`UI/TooltipEchoBox.cs:194-199`). Only controls drawn BEFORE the strip can feed `GUI.tooltip` in time; anything below must pass `manualOverride` (`UI/TooltipEchoBox.cs:207-211`). House convention places the strip directly above the Close button row (`UI/TooltipEchoBox.cs:202-204`).

Marquee mode (overflow): decided by the pure `TooltipMarquee.NeedsMarquee` (`UI/TooltipMarquee.cs:44`) from the WRAPPED height; when active the label is rendered NOWRAP at a width PINNED to the last measured strip width so the unwrapped minimum width cannot widen the window (`UI/TooltipEchoBox.cs:234-245`). Scroll speed 60 px/s (`UI/TooltipMarquee.cs:20`), 1.6 s hold at each end (`UI/TooltipMarquee.cs:23`), keyed by the digit-free `ScrollKeyFor` (`UI/TooltipMarquee.cs:64`) so a live countdown tooltip keeps its cycle across per-second re-renders.

(f) State variants: empty / short text / wrapped two-line text / marquee-scrolling text; one-line vs two-line strip. The strip IS present (empty) in every census window capture - the gui.json label node at the bottom of each window. A POPULATED strip has NO picture in either run: the census parks no pointer over a control. Marquee mode has no picture either. Reaching one needs a NEW SEAM OP that positions the pointer over a named control before the capture; today's `op=tab` / `op=rect` verbs change window state, not pointer position.

(g) Sizing: spacing `DefaultSpacing = 3f` (`UI/TooltipEchoBox.cs:66`); height = probe height for 1 or 2 lines; width `ExpandWidth(true)` normally, pinned to `cachedStripWidth` in marquee mode (`UI/TooltipEchoBox.cs:245`).

---

## 9.8 DisabledHoverEcho - "why is this greyed out?"

(a) Name and kind: an invisible zero-layout `GUI.Label` carrier that publishes a DISABLED control's reason into `GUI.tooltip` so the echo strip can show it. Kind: **tooltip-only affordance** (pure plumbing, paints nothing).

(b) Class + methods: `DisabledHoverEcho.CarryLastControl`, `UI/DisabledHoverEcho.cs:92`; explicit-rect overload `Carry`, `UI/DisabledHoverEcho.cs:106`; paint `GUI.Label(rect, Carrier, GUIStyle.none)` at `UI/DisabledHoverEcho.cs:116`.

(c) Gate: `ShouldCarry(controlEnabled, disabledReason)` = `!controlEnabled && !string.IsNullOrEmpty(disabledReason)` (`UI/DisabledHoverEcho.cs:63-66`); plus `Event.current.type == EventType.Repaint` (`:96`, `:110`) and `PointerInside` (`:73-76`, checked at `:112`).

(d) Why it exists (documented at `UI/DisabledHoverEcho.cs:17-27`): in the Unity build KSP 1.12.5 ships there are exactly TWO managed tooltip-publish sites in `UnityEngine.IMGUIModule` - `GUI.DoLabel` and `GUI.DoButtonGrid` - and neither reads `GUI.enabled`. Every other control (including `GUI.Button`) reaches the tooltip through the NATIVE `GUIStyle.Internal_Draw2`, whose behaviour under `GUI.enabled = false` cannot be read from the shipped assembly. Routing the reason through a Label rests the feature on the one proven path.

Zero-layout guarantee (`UI/DisabledHoverEcho.cs:29-37`): `GUI.Label(Rect, ...)` never calls `GetControlID` and reserves no layout slot, so the carrier adds zero controls to the enclosing group - which is the invariant `TooltipEchoBox` depends on.

Cost bound (`UI/DisabledHoverEcho.cs:39-43`): at most ONE carrier per frame across every disabled control in the window; one reused `GUIContent` (`:54`), so a hovered disabled button allocates nothing.

(e) Reasons it carries, per the header at `UI/DisabledHoverEcho.cs:8-15`: `CanRewind` / `CanFastForward` `out reason`, `GetWatchButtonTooltip`, and the Re-Fly slot reason.

(f) State variants: carried vs not. **No picture, and by construction unphotographable in the gui.json**: the carrier's text is empty and it publishes into `GUI.tooltip`, which surfaces in the echo strip - so the visible artefact is 9.7's strip, which the census never populated.

(g) Sizing: none; it reuses the last control's rect (`GUILayoutUtility.GetLastRect()`, `UI/DisabledHoverEcho.cs:99`).

---

## 9.9 Why none of this section is in the census

The GuiTree recorder patches `GUI.DoWindow` - "the single funnel for all six `GUI.Window` overloads AND `GUILayout.Window` (which wraps its callback and calls `GUI.Window`), which is also where `ClickThruBlocker.GUILayoutWindow` [lands]" (`Patches/GuiTreeRecorderPatches.cs:224-227`; the reflection binding is `GuiTreeFunnels.cs:112-118`).

Consequences, all mechanical:
1. Anything drawn OUTSIDE a `GUI.Window` callback is invisible to the recorder. That is every surface in 9.1, 9.2, 9.4 and 9.5.
2. Anything drawn in **uGUI** rather than IMGUI is invisible. That is 9.3 (the badges) and every `PopupDialog` in the mod.
3. 9.7's strip IS captured (it is inside a window) but only ever in its EMPTY state, because the capture parks no pointer.
4. 9.8 paints nothing at all by design.

So the census's 27 captures cover windows and their contents; the overlay/marker/badge/dialog layer is entirely unphotographed, and closing that gap needs pointer-positioning and full-screen (not window-tree) capture, not more window labels.

---

# APPENDIX 1 - UiSurface gate keys

The gate is one pure decision point, `UiSurfaceVisibility.IsVisible(UiSurface, UiComplexityMode)`
at `UI/UiComplexityMode.cs:156`, with an exhaustive switch that THROWS
`ArgumentOutOfRangeException` on an undecided value rather than defaulting
(`UI/UiComplexityMode.cs:191-197`). Retirement outranks the mode decision and short-circuits
before the switch (`UI/UiComplexityMode.cs:162-163`).

Fourteen enum members, twelve enforcement call sites. The call-site column is a program-wide
grep of `UiSurface.<member>` over `Source/Parsek` excluding `UI/UiComplexityMode.cs`,
`InGameTests/` and `TestCommands/`.

| UiSurface key | declared | Basic decision | enforcement call site(s) | exact surface hidden in Basic |
|---|---|---|---|---|
| `MainButtonSpawnControl` | `UI/UiComplexityMode.cs:47` | HIDE (`:180`) | `ParsekUI.cs:771` (launcher); `ParsekUI.cs:262` (`IsSpawnControlReachable`, consumed by `ParsekFlight.NotifyNewProximityCandidates` to decide the WORDING of a screen message) | the "Real Spawn Control" launcher button in the main window, and the proximity toast's "open that window" guidance |
| `MainButtonTimeline` | `:50` | KEEP (`:171`) | **none** | nothing; the launcher draws unconditionally |
| `MainButtonRecordings` | `:53` | KEEP (`:172`) | **none** | nothing; the Missions launcher draws unconditionally. Name is historical: it identifies the window whose code lives in `UI/RecordingsTableUI.cs` (`UI/UiComplexityMode.cs:30-34`) |
| `MainButtonLogistics` | `:56` | KEEP (`:173`) | **none** | nothing; drawn unconditionally at `ParsekUI.cs:885-892` |
| `MainButtonKerbals` | `:59` | HIDE (`:181`) | `ParsekUI.cs:904` | the "Kerbals" launcher button |
| `MainButtonCareer` | `:62` | HIDE (`:182`) | `ParsekUI.cs:906` | the "Career State" launcher button |
| `MainButtonGloops` | `:69` | RETIRED in BOTH modes (`:140-143`) | `ParsekUI.cs:941` | the "Gloops Flight Recorder" launcher button, in Advanced too. The window itself still exists and still draws when its flag is set (the flight census photographed it as `flight-gloops-advanced`) |
| `MainButtonSettings` | `:72` | KEEP (`:174`) | **none** | nothing; it hosts the mode toggle itself |
| `TabRecordings` | `:75` | HIDE (`:183`) | `UI/RecordingsTableUI.cs:189` | the raw per-recording table TAB inside the "Parsek - Missions" window; in Basic the window shows no tab bar at all |
| `TabMissions` | `:78` | KEEP (`:175`) | `UI/TimelineWindowUI.cs:1265-1266` | nothing today. The Timeline `GoTo` cross-link reuses this key by the design rule that a control whose sole purpose is navigating to another surface is gated by the TARGET's key (`UI/UiComplexityMode.cs:35-41`) |
| `MissionsLoopControls` | `:95` | HIDE (`:184`) | `UI/MissionsWindowUI.cs:735` | three controls as one decision: the per-mission "Loop" toggle, the loop-period cell beside it, and the row checkboxes selecting which intervals / partner journeys the loop replays. Deliberately NOT covered: the "Looped by route" status label, the TTL countdown column, "Warp to...", and Watch (`UI/UiComplexityMode.cs:89-93`) |
| `SettingsSectionLooping` | `:110` | HIDE (`:185`) | `UI/SettingsWindowUI.cs:345` (early-out), `:378` (section draw) | the Settings "Looping" section: the global auto-launch period plus its unit button |
| `SettingsSectionDiagnostics` | `:113` | HIDE (`:186`) | `UI/SettingsWindowUI.cs:393` | the Settings "Diagnostics" section: verbose logging, the tracing toggles, and the Test Runner launcher |
| `SettingsSectionSampleDensity` | `:116` | HIDE (`:187`) | `UI/SettingsWindowUI.cs:401` | the Settings recorder-fidelity tuning section |

**Four keys have zero enforcement points.** `MainButtonTimeline`, `MainButtonRecordings`,
`MainButtonLogistics` and `MainButtonSettings` are declared and classified keep-in-Basic, but
nothing in the mod asks the gate about them: their launchers draw unconditionally. Basic and
Advanced agree today by accident rather than by enforcement. Flipping any of the four to
`visibleInBasic = false` would change nothing on screen, because there is no call site to
honour it.

**`HiddenSurfaces` has no production consumer.** `UiSurfaceVisibility.HiddenSurfaces`
(`UI/UiComplexityMode.cs:214`) is referenced exactly once outside its own file, and that
reference is a doc comment at `ParsekUI.cs:471` explaining why the mode-change close set is
NOT derived from it. The real close set is the explicit, hand-ordered
`BuildGatedWindowCloseSet` at `ParsekUI.cs:488-529`:

| close target | input lock | reason it is in the set |
|---|---|---|
| `CareerState` | `CareerStateWindowUI.CareerStateInputLockId` (`ParsekUI.cs:493`) | launcher hidden in Basic |
| `Kerbals` | `KerbalsWindowUI.KerbalsInputLockId` (`ParsekUI.cs:500`) | launcher hidden in Basic |
| `GloopsRecorder` | `GloopsRecorderUI.InputLockId` (`ParsekUI.cs:507`) | launcher retired |
| `SpawnControl` | `SpawnControlUI.SpawnControlInputLockId` (`ParsekUI.cs:514`) | launcher hidden in Basic |
| `TestRunner` | `TestRunnerUI.TestRunnerInputLockId` (`ParsekUI.cs:519`) | maps to NO `UiSurface`; its launcher lives inside the hidden Diagnostics section, so an open instance would have no reopen path (`ParsekUI.cs:474-478`) |
| `GroupPicker` | none, `null` at `ParsekUI.cs:524` - "reachability rule, not a lock rule" | maps to NO `UiSurface`; reachable only from the hidden Recordings tab, but an already-open picker keeps drawing from `RecordingsTableUI.DrawIfOpen` regardless of tab (`ParsekUI.cs:479-482`) |

Deliberately ABSENT from the close set (`ParsekUI.cs:484-487`): `recordingsTableUI` (survives
as the Missions window), `structureListUI` (reachable from Missions and Logistics rows, both
kept), and the ungated `timelineUI` / `logisticsUI` / `settingsUI` / `missionsUI`.

**Window BODIES are never gated.** The gate feeds LAUNCHER and CONTENT draw sites plus the
mode-change close handler only (`UI/UiComplexityMode.cs:124-126`); `ParsekUI.cs:296-300`
states the reason: every window's `DrawIfOpen` prologue is
`if (!IsOpen) { ReleaseInputLock(); return; }`, and a writer that bypassed the
`SetUiComplexityMode` seam would leave hidden windows drawing and holding input locks.

**The mode is frame-latched.** `SetUiComplexityMode` (`ParsekUI.cs:299`) writes the setting
immediately but only sets a PENDING value; `ApplyPendingUiComplexityModeIfAny` latches it from
`Update()`, outside OnGUI (`ParsekUI.cs:294-297`). Every `IsVisible` call in a draw pass reads
the latched `AppliedUiComplexityMode` (`ParsekUI.cs:246`), so Layout and Repaint of one frame
can never disagree about the control count - the same invariant `TooltipEchoBox` depends on.
The one documented exception is the Settings Interface section, which reads the live setting
to decide WHICH of two options renders selected: a style swap on the same two controls, not a
count change (`ParsekUI.cs:241-244`).

---

# APPENDIX 2 - every ScreenMessage producer

Total production sites: 95 (from a program-wide grep of `ParsekLog.ScreenMessage` + `ScreenMessages.PostScreenMessage` over `Source/Parsek`, 107 raw hits minus 12 that are doc comments, test sinks, the local-function wiring at `ParsekFlight.cs:27576`, and the catch-log at `RewindInvoker.cs:4211`).

**Recorder lifecycle**

| file:line | enclosing method | trigger condition | message |
|---|---|---|---|
| `FlightRecorder.cs:6453` | `RotateToNewTrackSection` | `if (Time.timeScale < 0.01f)` | ParsekLog.ScreenMessage("Cannot record while paused", 2f); |
| `FlightRecorder.cs:6564` | `RotateToNewTrackSection` | `if (ShouldShowStartRecordingScreenMessage(isPromotion, suppressStartScreenMessage))` | ParsekLog.ScreenMessage("Recording STARTED", 2f); |
| `FlightRecorder.cs:8394` | `StopRecording` | `(unconditional in that branch)` | ParsekLog.ScreenMessage($"Recording STOPPED: {Recording.Count} points", 3f); |
| `FlightRecorder.cs:8969` | `HandleVesselSwitchDuringRecording` | `if (decision == VesselSwitchDecision.UndockSwitch)` | ParsekLog.ScreenMessage("Recording stopped - vessel changed", 3f); |

**Flight controller (ParsekFlight)**

| file:line | enclosing method | trigger condition | message |
|---|---|---|---|
| `ParsekFlight.cs:12861` | `HandleChainBoardingTransition` | `else` | ParsekLog.ScreenMessage("Recording stopped - vessel changed", 3f); |
| `ParsekFlight.cs:12881` | `CommitBoundaryAndRestart` | `if (IsRecording)` | ParsekLog.ScreenMessage(screenMessage, 2f); |
| `ParsekFlight.cs:13846` | `CommitTreeFlight` | `if (activeTree == null)` | ParsekLog.ScreenMessage("No active tree to commit", 2f); |
| `ParsekFlight.cs:13934` | `CommitTreeFlight` | `if (spawnCount > 0)` | ParsekLog.ScreenMessage($"Tree committed to timeline! {spawnCount} vessel(s) spawned.", 3f); |
| `ParsekFlight.cs:13936` | `CommitTreeFlight` | `else` | ParsekLog.ScreenMessage("Tree committed to timeline!", 3f); |
| `ParsekFlight.cs:14472` | `?` | `(unconditional in that branch)` | ParsekLog.ScreenMessage( "Parsek: re-fly attempt is not recording; it will not replace " + "the original", 8f); |
| `ParsekFlight.cs:17014` | `StartGloopsRecording` | `(unconditional in that branch)` | ParsekLog.ScreenMessage("Gloops Recording STARTED", 2f); |
| `ParsekFlight.cs:17048` | `CheckGloopsAutoStoppedByVesselSwitch` | `if (!gloopsRecorder.GloopsAutoStoppedByVesselSwitch) return;` | ParsekLog.ScreenMessage("Gloops recording auto-saved (vessel switched)", 3f); |
| `ParsekFlight.cs:17085` | `CommitGloopsRecorderData` | `if (rec == null)` | ParsekLog.ScreenMessage("Gloops recording too short - discarded", 2f); |
| `ParsekFlight.cs:17135` | `DiscardGloopsInProgress` | `if (gloopsRecorder.IsRecording)` | ParsekLog.ScreenMessage("Gloops recording discarded", 2f); |
| `ParsekFlight.cs:17260` | `PreviewGloopsRecording` | `(unconditional in that branch)` | ParsekLog.ScreenMessage("Gloops Preview STARTED", 2f); |
| `ParsekFlight.cs:19814` | `DeleteRecording` | `(unconditional in that branch)` | ParsekLog.ScreenMessage($"Recording '{rec.VesselName}' deleted", 2f); |
| `ParsekFlight.cs:19911` | `DeleteGhostOnlyRecording` | `if (!rec.IsGhostOnly)` | ParsekLog.ScreenMessage($"Ghost recording '{rec.VesselName}' deleted", 2f); |
| `ParsekFlight.cs:27345` | `NotifyNewProximityCandidates` | `if (notifiedSpawnRecordingIds.Add(cand.recordingId))` | ParsekLog.ScreenMessage(notifyMsg, 10f); |
| `ParsekFlight.cs:27440` | `WarpToDeparture` | `(unconditional in that branch)` | ParsekLog.ScreenMessage( string.Format(CultureInfo.InvariantCulture, "Warped to departure of \"{0}\" ({1:F0}s)", rec.VesselName, targetUT - currentUT), 5f); |
| `ParsekFlight.cs:27514` | `FastForwardToRecording` | `(unconditional in that branch)` | ParsekLog.ScreenMessage( string.Format(CultureInfo.InvariantCulture, "Fast-forwarded to \"{0}\" ({1:F0}s)", rec.VesselName, targetUT - currentUT), 3f); |
| `ParsekFlight.cs:27551` | `FastForwardToEventUT` | `(unconditional in that branch)` | ParsekLog.ScreenMessage( string.Format(CultureInfo.InvariantCulture, "Fast-forwarded to {0} ({1:F0}s)", label, targetUT - currentUT), 3f); |

**Tree merge / discard**

| file:line | enclosing method | trigger condition | message |
|---|---|---|---|
| `MergeDialog.Commit.cs:153` | `MergeCommit` | `if (spawnCount > 0)` | ParsekLog.ScreenMessage( $"Merged - {spawnCount} vessel(s) will appear after ghost playback", 3f); |
| `MergeDialog.Commit.cs:156` | `MergeCommit` | `else` | ParsekLog.ScreenMessage( "Merged to timeline (no surviving vessels)", 3f); |
| `MergeDialog.Commit.cs:248` | `ConcludeMissingProvisional` | `(unconditional in that branch)` | ParsekLog.ScreenMessage( "Merge interrupted - will finish on next load", 3f); |
| `MergeDialog.Commit.cs:258` | `ConcludeMissingProvisional` | `if (!ok)` | ParsekLog.ScreenMessage("Merge commit skipped (see log)", 3f); |
| `MergeDialog.Commit.cs:371` | `ApplyPlayerRequestedSeal` | `if (provisional == null)` | ParsekLog.ScreenMessage( "Merged, but could not seal - seal it from the Timeline window", 4f); |
| `MergeDialog.Commit.cs:382` | `ApplyPlayerRequestedSeal` | `if (sealed_)` | ParsekLog.ScreenMessage("Re-Fly slot sealed", 3f); |
| `MergeDialog.Commit.cs:389` | `ApplyPlayerRequestedSeal` | `else` | ParsekLog.ScreenMessage( "Merged, but could not seal - seal it from the Timeline window", 4f); |
| `MergeDialog.ReFlyDiscard.cs:48` | `MergeDiscardRanToCompletion` | `if (!object.ReferenceEquals(null, scenario) && scenario.ActiveMergeJournal != null)` | ParsekLog.ScreenMessage("Discard: merge in progress - retry in a moment", 3f); |
| `MergeDialog.ReFlyDiscard.cs:84` | `MergeDiscardRanToCompletion` | `if (switchSegmentDisposition` | ParsekLog.ScreenMessage("Switch segment discarded", 2f); |
| `MergeDialog.ReFlyDiscard.cs:131` | `MergeDiscardRanToCompletion` | `else` | ParsekLog.ScreenMessage("Recording discarded", 2f); |
| `MergeDialog.ReFlyDiscard.cs:266` | `TryDiscardActiveReFlyAttempt` | `if (scenario.ActiveMergeJournal != null)` | ParsekLog.ScreenMessage("Discard Re-Fly: merge in progress - retry in a moment", 3f); |
| `MergeDialog.ReFlyDiscard.cs:331` | `TryDiscardActiveReFlyAttempt` | `(unconditional in that branch)` | ParsekLog.ScreenMessage("Re-Fly attempt discarded", 2f); |

**Switch-to / stock-action intent**

| file:line | enclosing method | trigger condition | message |
|---|---|---|---|
| `Patches/MapFocusObjectOnSelectPatch.cs:596` | `MergePriorAndSwitchTo` | `if (scenario != null && scenario.ActiveMergeJournal != null)` | ParsekLog.ScreenMessage( "Switch-to merge: re-fly merge in progress - retry in a moment", 3f); |
| `Patches/MapFocusObjectOnSelectPatch.cs:646` | `MergePriorAndSwitchTo` | `(unconditional in that branch)` | ParsekLog.ScreenMessage( "Switch-to canceled: failed to commit prior recording", 4f); |
| `Patches/MapFocusObjectOnSelectPatch.cs:728` | `DiscardPriorAndSwitchTo` | `if (!object.ReferenceEquals(null, scenario) && scenario.ActiveMergeJournal != null)` | ParsekLog.ScreenMessage( "Switch-to discard: re-fly merge in progress - retry in a moment", 3f); |
| `Patches/MapFocusObjectOnSelectPatch.cs:800` | `MergeActiveTreeAndSwitchTo` | `if (scenario != null && scenario.ActiveMergeJournal != null)` | ParsekLog.ScreenMessage( "Switch-to merge: re-fly merge in progress - retry in a moment", 3f); |
| `Patches/MapFocusObjectOnSelectPatch.cs:832` | `MergeActiveTreeAndSwitchTo` | `(unconditional in that branch)` | ParsekLog.ScreenMessage( "Switch-to canceled: failed to commit prior recording", 4f); |
| `Patches/MapFocusObjectOnSelectPatch.cs:910` | `DiscardActiveRecordingAndSwitchTo` | `if (scenario != null && scenario.ActiveMergeJournal != null)` | ParsekLog.ScreenMessage( "Switch-to discard: re-fly merge in progress - retry in a moment", 3f); |

**Ghost map presence and ghost blocks**

| file:line | enclosing method | trigger condition | message |
|---|---|---|---|
| `Patches/GhostTrackingStationPatch.cs:629` | `Prefix` | `if (v == null \|\| !GhostMapPresence.IsGhostMapVessel(v.persistentId))` | PParsekLog.ScreenMessage( $"<b>{v.vesselName}</b> is a ghost vessel - it will materialize when its timeline reaches the spawn point.", 5f, ScreenMessageStyle.UPPER_CENTER); |
| `Patches/GhostTrackingStationPatch.cs:744` | `Prefix` | `if (!GhostMapPresence.IsGhostMapVessel(selected.persistentId))` | PParsekLog.ScreenMessage( $"<b>{selected.vesselName}</b> is a ghost vessel and cannot be deleted. " + "It will be removed automatically when its chain resolves.", 5f, ScreenMessageStyle.UPPER_CENTER); |
| `Patches/GhostTrackingStationPatch.cs:790` | `Prefix` | `if (string.IsNullOrEmpty(selectionSource))` | PParsekLog.ScreenMessage( $"<b>{v.vesselName}</b> is a ghost - it shows the predicted orbit of a recorded vessel.", 5f, ScreenMessageStyle.UPPER_CENTER); |
| `Patches/GhostTrackingStationPatch.cs:867` | `Prefix` | `if (!GhostMapPresence.IsGhostMapVessel(selected.persistentId))` | PParsekLog.ScreenMessage( $"<b>{selected.vesselName}</b> is a ghost vessel and cannot be recovered. " + "It will be removed automatically when its chain resolves.", 5f, ScreenMessageStyle.UPPER_CENTER); |
| `Patches/GhostVesselLoadPatch.cs:102` | `HandleWatchRequest` | `if (flight == null)` | PParsekLog.ScreenMessage( $"<b>{vesselName}</b> is a ghost - it will materialize when its timeline reaches the spawn point.", 5f, ScreenMessageStyle.UPPER_CENTER); |
| `Patches/GhostVesselLoadPatch.cs:124` | `HandleWatchRequest` | `case GhostMapWatchAction.Unavailable:` | PParsekLog.ScreenMessage( $"<b>{vesselName}</b> is a ghost - it will materialize when its timeline reaches the spawn point.", 5f, ScreenMessageStyle.UPPER_CENTER); |
| `Patches/GhostVesselLoadPatch.cs:132` | `HandleWatchRequest` | `case GhostMapWatchAction.NoActiveGhost:` | PParsekLog.ScreenMessage( $"<b>{vesselName}</b> - ghost not active yet (recording may be in the future).", 5f, ScreenMessageStyle.UPPER_CENTER); |
| `Patches/GhostVesselLoadPatch.cs:140` | `HandleWatchRequest` | `case GhostMapWatchAction.DifferentBody:` | PParsekLog.ScreenMessage( $"<b>{vesselName}</b> - ghost is in a different SOI.", 5f, ScreenMessageStyle.UPPER_CENTER); |
| `Patches/GhostVesselLoadPatch.cs:148` | `HandleWatchRequest` | `case GhostMapWatchAction.OutOfRange:` | PParsekLog.ScreenMessage( $"<b>{vesselName}</b> - ghost is out of watch range.", 5f, ScreenMessageStyle.UPPER_CENTER); |
| `Patches/GhostVesselLoadPatch.cs:493` | `Prefix` | `if (recIndex < 0)` | PParsekLog.ScreenMessage( $"<b>{v.vesselName}</b> is a ghost vessel - it will materialize when its timeline reaches the spawn point.", 5f, ScreenMessageStyle.UPPER_CENTER); |

**Watch mode**

| file:line | enclosing method | trigger condition | message |
|---|---|---|---|
| `WatchModeController.cs:1839` | `TryResolveWatchEntryState` | `if (!IsWithinWatchEntryRange(distMeters))` | ParsekLog.ScreenMessage($"Ghost too far to watch ({distKm:F0}km, max {maxWatchKm:F0}km)", 3f); |
| `WatchModeController.cs:1858` | `ShowUnattendedFlightWarningIfNeeded` | `if (!IsVesselSituationSafe(av.situation, pe, atmoHeight))` | ParsekLog.ScreenMessage("Your vessel continues unattended", 3f); |
| `WatchModeController.cs:2390` | `ToggleCameraMode` | `if (!TryRestoreRememberedWatchCameraState(` | ParsekLog.ScreenMessage( currentCameraMode == WatchCameraMode.HorizonLocked ? "Camera: Horizon Locked" : "Camera: Free", 2f); |

**Warp / rewind**

| file:line | enclosing method | trigger condition | message |
|---|---|---|---|
| `RecordingStore.cs:5406` | `InitiateRewind` | `if (!SuppressLogging)` | ParsekLog.ScreenMessage("Cannot rewind during an active re-fly merge", 3f); |
| `RecordingStore.cs:5595` | `InitiateRewindToCareerStart` | `if (!SuppressLogging)` | ParsekLog.ScreenMessage("Cannot warp to game start during an active re-fly merge", 3f); |
| `RevertInterceptor.cs:757` | `resultHook` | `if (hook != null)` | PParsekLog.ScreenMessage( message, 5f, ScreenMessageStyle.UPPER_CENTER); |
| `RewindInvoker.cs:4205` | `ShowUserError` | `if (string.IsNullOrEmpty(message)) return;` | PParsekLog.ScreenMessage( message, 5f, ScreenMessageStyle.UPPER_CENTER); |
| `WarpToTimeController.cs:81` | `RequestWarp` | `case WarpToTimeMath.WarpPlanKind.AtTarget:` | ParsekLog.ScreenMessage("Already at this time", 3f); |
| `WarpToTimeController.cs:84` | `RequestWarp` | `case WarpToTimeMath.WarpPlanKind.Unreachable:` | ParsekLog.ScreenMessage(plan.Reason, 3f); |
| `WarpToTimeController.cs:122` | `ExecuteAtKsc` | `case WarpToTimeMath.WarpPlanKind.AtTarget:` | ParsekLog.ScreenMessage("Already at this time", 3f); |
| `WarpToTimeController.cs:125` | `ExecuteAtKsc` | `case WarpToTimeMath.WarpPlanKind.Unreachable:` | ParsekLog.ScreenMessage(plan.Reason, 3f); |
| `WarpToTimeController.cs:312` | `StartRewind` | `if (!CareerStartSnapshot.Exists())` | ParsekLog.ScreenMessage("Game-start snapshot not available", 3f); |
| `WarpToTimeController.cs:330` | `StartRewind` | `if (ownerNow == null)` | ParsekLog.ScreenMessage("Rewind target no longer available", 3f); |
| `WarpToTimeController.cs:340` | `StartRewind` | `if (!RecordingStore.CanRewind(ownerNow, out string reason, isRecording: false))` | ParsekLog.ScreenMessage(reason, 3f); |

**Logistics / supply routes**

| file:line | enclosing method | trigger condition | message |
|---|---|---|---|
| `Logistics/RouteEndpointTransfer.cs:600` | `ApplyTransfers` | `(unconditional in that branch)` | ParsekLog.ScreenMessage( FormatTransferScreenMessage(owner.Route.Name, owner.Role, oldName, resolvedName), 6f); |
| `Logistics/RouteOrchestrator.cs:3859` | `TryHonorArmedPauseOnBlockedCycle` | `if (wasSendOnce)` | ParsekLog.ScreenMessage( RouteSendOncePresentation.BuildBlockedMessage( route.Name, route.Id, route.LastHoldKind, route.LastHoldDetail, route.LastHoldShortfall), RouteSendOncePresentation.ToastSeconds); |
| `Logistics/RouteOrchestrator.cs:3956` | `TryHonorArmedPauseOnCompletedCycle` | `if (wasSendOnce)` | ParsekLog.ScreenMessage( RouteSendOncePresentation.BuildCycleDeliveredMessage( route.Name, route.Id, cyclePartial), RouteSendOncePresentation.ToastSeconds); |
| `Logistics/RouteOrchestrator.cs:4179` | `ApplyDelivery` | `if (wasSendOnce)` | ParsekLog.ScreenMessage( RouteSendOncePresentation.BuildAlreadyDeliveredMessage(route.Name, route.Id), RouteSendOncePresentation.ToastSeconds); |
| `Logistics/RouteOrchestrator.cs:4574` | `AppendPartialDeliverySummary` | `if (wasSendOnce)` | ParsekLog.ScreenMessage( RouteSendOncePresentation.BuildDeliveredMessage( route.Name, route.Id, resourceLinesApplied, inventoryActual, plan.IsPartial), RouteSendOncePresentation.ToastSeconds); |
| `Logistics/RouteRunPrompt.cs:183` | `NotifyTreeCommittedCore` | `(unconditional in that branch)` | ParsekLog.ScreenMessage( "This flight qualifies as a Supply Route - open Logistics to create it", 5f); |
| `Logistics/RouteStore.cs:767` | `RemoveDormantRoute` | `(unconditional in that branch)` | PParsekLog.ScreenMessage( $"[Parsek] Supply route '{route.Name ?? "<unnamed>"}' available again " + "(timeline reached its creation point)", 6f); |
| `UI/LogisticsWindowUI.cs:2648` | `DialogGUIButton` | `if (!ok && RouteStore.TryGetRoute(routeId, out _))` | PParsekLog.ScreenMessage( $"[Parsek] Supply route '{display}' re-materialized while the " + "confirmation was open. Delete it from its routes section instead.", 6f); |
| `UI/LogisticsWindowUI.cs:2867` | `CreateRouteFromCandidate` | `if (LogisticsCreatePresentation.ShouldToastManualLoopCleared(outcome.ManualLoopsCleared))` | ParsekLog.ScreenMessage(toast, 5f); |

**Missions tab**

| file:line | enclosing method | trigger condition | message |
|---|---|---|---|
| `UI/MissionsWindowUI.cs:1084` | `PostDoubleClockAdvisoryIfAny` | `if (string.IsNullOrEmpty(advisory))` | PParsekLog.ScreenMessage( "[Parsek] " + advisory, 8f, ScreenMessageStyle.UPPER_LEFT); |
| `UI/MissionsWindowUI.cs:2746` | `AnnounceClearedLoops` | `if (message == null)` | ParsekLog.ScreenMessage(message, 5f); |
| `UI/MissionsWindowUI.cs:3038` | `DialogGUIButton` | `if (!TimeJumpManager.IsValidJump(curUT, target))` | ParsekLog.ScreenMessage(string.Format(ic, "Fast-forwarded to {0} ({1:F0}s)", label, target - curUT), 3f); |

**Recordings table / group picker**

| file:line | enclosing method | trigger condition | message |
|---|---|---|---|
| `UI/GroupPickerUI.cs:61` | `LogAndToastRejectAdd` | `(unconditional in that branch)` | PParsekLog.ScreenMessage( "Cannot move STASH entries to manual groups", 3f, ScreenMessageStyle.UPPER_CENTER); |
| `UI/RecordingsTableUI.cs:2163` | `GUIContent` | `if (EffectiveState.IsUnfinishedFlight(rec))` | ParsekLog.ScreenMessage( $"Cannot hide '{rec.VesselName}' - it is an Unfinished Flight. " + "Re-fly the rewind point or merge as Immutable to clear it from the list.", 4f); |
| `UI/RecordingsTableUI.cs:3637` | `HandleStashUnfinishedFlightClick` | `if (!UnfinishedFlightStashHandler.TryStash(rec, out stashReason))` | ParsekLog.ScreenMessage( $"Cannot stash '{rec.VesselName ?? rec.RecordingId ?? "<unnamed>"}': " + (stashReason ?? "slot is unavailable"), 4f); |
| `UI/RecordingsTableUI.cs:4552` | `DialogGUIButton` | `(unconditional in that branch)` | ParsekLog.ScreenMessage( string.Format(ic, "Fast-forwarded to \"{0}\" ({1:F0}s)", capturedRec.VesselName, jumpDelta), 3f); |

**Main window**

| file:line | enclosing method | trigger condition | message |
|---|---|---|---|
| `ParsekUI.cs:1526` | `DialogGUIButton` | `if (InFlight) flight.DestroyAllTimelineGhosts();` | ParsekLog.ScreenMessage("All recordings wiped", 2f); |
| `ParsekUI.cs:1550` | `DialogGUIButton` | `(unconditional in that branch)` | ParsekLog.ScreenMessage("All game actions wiped", 2f); |

**Scenario / save lifecycle**

| file:line | enclosing method | trigger condition | message |
|---|---|---|---|
| `ParsekScenario.cs:3855` | `?` | `if (hadPendingTree)` | try { ParsekLog.ScreenMessage("Recording unstashed (revert)", 4f); } |
| `ParsekScenario.cs:6090` | `ShowDeferredMergeDialog` | `if (RecordingStore.PendingTreeStateValue == PendingTreeState.Finalized` | PParsekLog.ScreenMessage("Recording discarded - vessel idle on pad", 4f); |
| `ParsekScenario.cs:7864` | `AutoCommitPendingTreeOutsideFlight` | `else` | PParsekLog.ScreenMessage("[Parsek] Tree recording committed to timeline", 5f); |
| `PreParsekBackup.cs:529` | `PerformBackup` | `else if (pristine == CapturedPristineVerdict.Unverified)` | ParsekLog.ScreenMessage( $"Parsek backed up your save as '{finalName}' (resume it from the main menu if needed)", 8f); |
| `PreParsekBackup.cs:536` | `PerformBackup` | `(unconditional in that branch)` | ParsekLog.ScreenMessage( "Parsek could not auto-backup your save; please back it up manually", 8f); |

**Tracking Station**

| file:line | enclosing method | trigger condition | message |
|---|---|---|---|
| `ParsekTrackingStation.cs:1990` | `MaterializeSelectedGhost` | `if (!endpointMaterialize.needsSpawn)` | ParsekLog.ScreenMessage( string.Format( CultureInfo.InvariantCulture, "Cannot warp to spawn \"{0}\": {1}", recording.VesselName ?? "ghost", endpointMaterialize.reason ?? "endpoint blocked"), 4f); |
| `ParsekTrackingStation.cs:2011` | `MaterializeSelectedGhost` | `(unconditional in that branch)` | ParsekLog.ScreenMessage( string.Format( CultureInfo.InvariantCulture, "Warped to spawn \"{0}\" ({1:F0}s)", recording.VesselName ?? "ghost", jumpDelta), 3f); |

**Spawning**

| file:line | enclosing method | trigger condition | message |
|---|---|---|---|
| `VesselSpawner.cs:2183` | `?` | `if (terminalSpawnWasDeferred && useRecordedTerminalOrbit)` | ParsekLog.ScreenMessage($"Vessel '{rec.VesselName}' has appeared!", 4f); |
| `VesselSpawner.cs:2230` | `?` | `if (terminalSpawnWasDeferred)` | ParsekLog.ScreenMessage($"Vessel '{rec.VesselName}' has appeared!", 4f); |

**Ledger**

| file:line | enclosing method | trigger condition | message |
|---|---|---|---|
| `GameActions/KspStatePatcher.cs:3514` | `EmitDrawdownGuardClamp` | `if (!sessionToastLatch)` | ParsekLog.ScreenMessage(toastText, 2.5f); |

**Playback policy**

| file:line | enclosing method | trigger condition | message |
|---|---|---|---|
| `ParsekPlaybackPolicy.cs:1523` | `CurrentRealTimeOverrideForTesting` | `if (lastSeamMessageRealTime > 0f` | PParsekLog.ScreenMessage("[Parsek] " + text, 6f, ScreenMessageStyle.UPPER_LEFT); |

**In-game test runner**

| file:line | enclosing method | trigger condition | message |
|---|---|---|---|
| `InGameTests/InGameTestRunner.cs:1046` | `NotifyExceptionStormAbort` | `(unconditional in that branch)` | PParsekLog.ScreenMessage( "[Parsek] Test batch aborted: flight-state NRE storm detected. Relaunch KSP to recover.", 12f, ScreenMessageStyle.UPPER_CENTER); |
| `InGameTests/InGameTestRunner.cs:2222` | `AttemptSpaceCenterBounceRecovery` | `(unconditional in that branch)` | PParsekLog.ScreenMessage( "[Parsek] Flight state corrupted after the test batch: bouncing to Space Center to recover.", 12f, ScreenMessageStyle.UPPER_CENTER); |

**The sink itself**

| file:line | enclosing method | trigger condition | message |
|---|---|---|---|
| `ParsekLog.cs:572` | `Write` | `if (sink != null)` | PParsekLog.ScreenMessage( $"[Parsek] {message}", duration, ScreenMessageStyle.UPPER_CENTER); |

---

# APPENDIX 3 - tooltip-only affordances

Information that exists ONLY in a tooltip: no label, no column, no log line a player reads. Collected from each section's own fragment.


**From sec-01-main.md**

ParsekUI.cs:142 | "No recorded craft is passing nearby" | WHY the Real Spawn Control launcher is greyed out. Reaches the player only through the tooltip strip, via DisabledHoverEcho.CarryLastControl (ParsekUI.cs:780) - the disabled button itself shows only "(0)" and no explanation.
ParsekUI.cs:843 | "Drops the suggestion; undo it in Logistics > Dismissed." | That dismissing a route candidate is REVERSIBLE, and exactly where to reverse it. Nothing else in the main window says the Logistics window has a "Dismissed" subsection.
ParsekUI.cs:779 | "Turn a recorded craft passing nearby into a real vessel." | What "Real Spawn Control" means - the label is a bare noun phrase that names no action.
ParsekUI.cs:796 | "Every recorded flight and career event on one clock." | That Timeline unifies FLIGHT and CAREER events; the label says neither.
ParsekUI.cs:808 | "Your missions, and the recordings they are built from." | That the raw recordings table lives behind the Missions button (there is no separate Recordings launcher).
ParsekUI.cs:887 | "Supply routes that repeat a delivery you already flew." | That a route is derived from a flight you already flew - the precondition for the feature.
ParsekUI.cs:916 | "Who is reserved, flying or retired in your timeline." | The three kerbal states the window tracks; the label is one word.
ParsekUI.cs:927 | "Contracts, strategies and buildings along the timeline." | The four content kinds inside the Career window.
ParsekUI.cs:945 | "Record a ghost-only flight that your career ignores." | That a Gloops take does not touch the career ledger. UNREACHABLE in the shipped build (the launcher is retired), so this information currently reaches nobody.
ParsekUI.cs:957 | "Recording, looping, ghost and diagnostic options." | The four Settings sections; note it still advertises "looping" and "diagnostic", both of which Basic hides.

**From sec-02-recordings.md**

UI/RecordingsTableUI.cs:1220 | "Turn ghost playback on or off for every recording at once - including ones hidden by the current filters." | that the header select-all IGNORES the Archive and time-range filters - nothing else states the bulk scope
UI/RecordingsTableUI.cs:1309 | "Replay every loopable recording on a repeating schedule, including ones hidden by the current filters. Greyed out when any recording is driven by a supply route." | both the filter-ignoring scope AND the only in-UI statement of the route/loop mutual exclusion at header level
UI/RecordingsTableUI.cs:1274 | "How many trajectory points were recorded - a rough measure of how detailed and how large the recording is." | that the Pts column is a size proxy; no other surface explains it
UI/RecordingsTableUI.cs:1342 | "Launch-to-launch period: how often the ghost relaunches. Shorter than the flight means launches overlap. Click the unit to cycle sec / min / hr / auto; auto follows Settings > Looping." | the launch-to-launch (not cycle) definition, the overlap consequence, AND the unit-cycle affordance - the unit button carries no label saying it cycles
UI/RecordingsTableUI.cs:1366 | "Filter: hide archived recordings from this list. It archives nothing - use each row's Archive box for that." | that this header toggle is a FILTER and not the select-all its position implies
UI/RecordingsTableUI.cs:1348 | "Follow a ghost with the camera. Only available in flight, for ghosts on the same body and within 300 km." | the 300 km watch radius and the same-body requirement
UI/RecordingsTableUI.cs:1053 | "Ghost is beyond the fixed 300 km watch range" | per-row confirmation of the same number on a greyed W button
UI/RecordingsTableUI.cs:1050 | "Ghost is too far away to be drawn right now, so Parsek cannot tell which body it is at - get closer and the Watch button comes back" | the distinction between "different body" and "body reading not current" - a state with no other indicator
UI/RecordingsTableUI.cs:1987 | "Delete this ghost-only recording permanently. It carries no real flight data to keep." | that this X is a PERMANENT delete with no confirmation dialog (:4570 deletes outright)
UI/RecordingsTableUI.cs:2582 | "Disband this folder. Its recordings and subfolders move up to the parent - nothing is deleted." | that X on a folder is non-destructive, distinguishing it from the identically-glyphed row X above
UI/RecordingsTableUI.cs:2497 | "Click to expand or collapse this folder; double-click to rename it." | the double-click-to-rename affordance (folders)
UI/RecordingsTableUI.cs:2241 | "Click to select; double-click to rename this recording." | the double-click-to-rename affordance (rows)
UI/RecordingsTableUI.cs:4738 | "Static placeholder: landed background continuation with a surface position and time range, but no playable trail. Kept visible for row controls." | the entire meaning of the "static" status word
UI/RecordingsTableUI.cs:4740 | "Stationary tail: terminal leaf with only stationary/coasting sections and no visual events. Kept visible because it may carry end-of-recording spawn state." | the entire meaning of the "stationary" status word
UI/RecordingsTableUI.cs:3509 | "Re-fly this unfinished flight from the separation moment" | that Fly resumes at SEPARATION, not at launch - the column header only says "from the moment it separated" and the button glyph says neither
UI/RecordingsTableUI.cs:3515 | "Close this re-fly slot permanently without changing the recording" | that Seal is irreversible and record-neutral
UI/RecordingsTableUI.cs:3584 | "Stash this stable Rewind Point slot in STASH so it can be re-flown later" | the only explanation of what Stash does with a STABLE slot
UI/RecordingsTableUI.cs:5797 | "Period unit for this recording. Enable Loop to change it." | the recovery instruction on the greyed unit button
UI/RecordingsTableUI.cs:5892 | "Click to cycle the period unit: seconds, minutes, hours, then auto. Auto follows the interval set in Settings > Looping." | the full cycle ORDER and the Settings cross-reference
UI/RecordingsTableUI.cs:1415 | "Show or hide the extra statistics columns. Turning them on widens the window." | warns that the click RESIZES the window (:1420-1423)
UI/RecordingsTableUI.cs:1429 | "Create an empty folder and name it. Put recordings in it with the G button on each row." | the two-step workflow (New Group then per-row G); nothing else pairs them
UI/RecordingsTableUI.cs:1290 | "Folders each recording belongs to. Use the G button on a row to change them." | that the Group column is read-only and G is the editor
UI/RecordingsTableUI.cs:1510 | "Clear the time-range filter so every recording is listed again. Also resets the Timeline window's range sliders." | the cross-window side effect on the Timeline sliders (:1514)
UI/RecordingsTableUI.cs:1976 | "How this flight is doing: counting down to launch, flying now, or how it ended." | the fallback status explanation used when no visual-kind / chain / dock tooltip composed
UI/UnfinishedFlightsGroup.cs:42 | "Vessels and kerbals you may want to re-fly - crashed, abandoned in orbit, stranded on a surface. Fly takes control at the separation moment; Seal closes the slot for good." | the entire definition of the all-caps STASH group; the group name itself explains nothing
UI/RecordingsTableUI.cs:2038 | "Looped by route: {RouteBindingTooltipName(rec)}" | WHICH supply route took the row's loop over - the only place that name appears in this window
UI/RecordingsTableUI.cs:2744 | "no watchable vessels in this group" / :2746 "switch to {name}" / :2748 "exit watch (no other watchable vessels)" / :2750 "enter watch on {name}" | that the folder W button ROTATES through descendants and which vessel is next - the glyph is a static W/W*

**From sec-03-missions-tab.md**

UI/MissionsWindowUI.cs:1487-1494 (cell at :1489) | "<event phrase> - Aboard: <last interval composition label>" | the vessel's FINAL composition ("pod x1, crew x2"): the flattened row never prints it inline, and Basic (no interval expansion) has no other way to see it. Census: 'Launch -> Destroyed - Aboard: probe x1'
UI/MissionsWindowUI.cs:1955-1963 (DrawStartEventCell graph fallback) | "<event word> with <vessel> (mission '<name>')" | the CROSS-TREE / recovered-single-parent dock partner - inlined only when it measures inside ColW_StartEvent (110f), otherwise reachable only on hover
UI/MissionsWindowUI.cs:1977-1979 (same-tree branch) | "Docked with <partner>" | the same phrase clips in the non-wrapping cell; the full phrase exists only as the tooltip
UI/MissionsWindowUI.cs:2700-2701 + MissionPresentation.cs:429 | "<start date> -> <end date> - N vessels - Crew: A, B, C" | the full span DATES, the vessel COUNT and the FULL crew roster; the visible narrative line shows a duration, a capped crew list (NarrativeCrewNameCap = 3, MissionPresentation.cs:311) and no dates. Census: tip='Y1, D01, 00:01 -> Y1, D01, 00:03 - 1 vessel'
UI/MissionsWindowUI.cs:3394-3397 + MissionPresentation.cs:663 | NextLaunchTooltipNotAligned / NextLaunchTooltipContinuous, optionally + the amber reasons | what the two engine state words "not aligned" / "continuous" MEAN, and WHY a countdown is tinted amber (D3 station drift, M4c arrival refusal) - the tint itself says nothing
UI/MissionsWindowUI.cs:4155-4162 (unit button) + MissionPresentation.cs:646 | PeriodTooltipLoopOff / Auto / Clamped / Locked | which of the four period states the cell is in; a GUILayout.TextField takes no GUIContent, so the adjacent unit button is the only hover surface for the field beside it. Census: button 'sec' tip='Loop off - the stored period is shown but nothing relaunches.'
UI/MissionsWindowUI.cs:2557-2560 + UI/RecordingsTableUI.cs:1015 | "Looped by route: <route name>" (disabled-hover echo on the greyed Loop toggle) | WHICH route owns the schedule; the visible label beside it only says "Looped by route"
UI/MissionsWindowUI.cs:2513 + :2908 | "A flight always keeps its first mission" | why Delete is greyed (a tree always keeps its original mission)
UI/MissionsWindowUI.cs:2879 / :2900 + :2918 | "Watching only works while you are flying" / "Nothing from this mission is flying right now" | which of the two Watch gates closed
UI/MissionsWindowUI.cs:2972-2984 + :2930 | four ordered reasons (wrong scene / Loop off / no schedule / next launch not ahead) | which of the four "Warp to..." gates closed; the button's own tooltip describes none of them
UI/MissionsWindowUI.cs:2362 | "Go to the other side of this event: <partner text>" | which mission the digest row's Go to lands on
UI/MissionsWindowUI.cs:251-256 (VesselIncludeCheckboxContent / IntervalIncludeCheckboxContent / PartnerJourneyCheckboxContent) + MissionPresentation.cs:42, :50, :69 | "Include this ... in the mission's loop unit ... Does not hide the ghost" | that the checkbox writes LOOP-UNIT membership and does NOT hide a ghost - the boxes are unlabelled, so this is their only description

**From sec-04-timeline.md**

UI/TimelineWindowUI.cs:772 | "Shows only the headline rows: launches, endings and career events." | that Overview is a T1-only tier filter; the label "Overview" alone does not say what it drops
UI/TimelineWindowUI.cs:780 | "Adds the fine-grained rows Overview hides, such as staging and docking." | the only statement of what the Details tier adds
UI/TimelineWindowUI.cs:806 | "Shows or hides the rows that come from your recorded flights." | that the Recordings toggle filters by SOURCE, not by tier
UI/TimelineWindowUI.cs:815 | "Shows or hides career moves: funds, science, contracts, upgrades." | the definition of "Actions" = player-initiated career moves (entry.IsPlayerAction, :1197)
UI/TimelineWindowUI.cs:825 | "Shows or hides in-flight happenings such as crew deaths and recoveries." | the definition of "Events" = the non-player-action half of the same source
UI/TimelineWindowUI.cs:841 | "Shows only the flights you can rewind to or fast-forward to." | that Rewind/FF hides rows whose button would be GREYED, not merely missing (:1576)
UI/TimelineWindowUI.cs:855 | "Shows only the flights you can fly again or seal as final." | same actionability semantics for the Re-Fly view (:1587)
UI/TimelineWindowUI.cs:887-889 | "Brings back rows for flights you archived - the same Archive filter the recordings list uses." | that this toggle and the Recordings tab's Archive checkbox are ONE shared flag (GroupHierarchyStore.HideActive, :736). Deliberately names no window so it reads correctly in Basic where that tab is hidden (:878-883)
UI/TimelineWindowUI.cs:948 | "Narrows the list to rows from the last day of game time." | that the preset windows are GAME time, not wall time
UI/TimelineWindowUI.cs:951 | "Narrows the list to rows from the last seven days of game time." | (same, 7d)
UI/TimelineWindowUI.cs:955 | "Narrows the list to rows from the last thirty days of game time." | (same, 30d)
UI/TimelineWindowUI.cs:963 | "Narrows the list to rows from the current game year." | that "This Year" is the calendar-year bracket floor(now/yr)*yr .. +1yr, not a trailing 365 days
UI/TimelineWindowUI.cs:970 | "Clears the time filter and shows the whole timeline." | that "All" is a CLEAR, not a widest-range preset
UI/TimelineWindowUI.cs:983-984 | "Reveals sliders for picking your own start and end time." | the only hint that custom sliders exist at all before they are revealed
UI/TimelineWindowUI.cs:553 | "Rewind or fast-forward the game clock to the entered date" | that one button does BOTH directions depending on the entered date
UI/TimelineWindowUI.cs:1558 | "A warp is already running" | a greyed-warp reason reachable ONLY through the DisabledHoverEcho carrier (:568); the GUIContent tooltip still holds the enabled wording (:565-567)
UI/TimelineWindowUI.cs:1560 | "This warp continues at the Space Center" | same - the ONLY surface that explains a warp deferred out of flight
UI/TimelineWindowUI.cs:1556 | "Enter a time to warp to" | the empty-planner-reason fallback, echo-only
UI/TimelineWindowUI.cs:1370 | "Fast-forward to this launch" | what the 2-letter "FF" label means
UI/TimelineWindowUI.cs:1387 | "Rewind to this launch" | what the 1-letter "R" label means
UI/TimelineWindowUI.cs:1633 | "Show this recording's mission" | the GoTo destination
UI/TimelineWindowUI.cs:1634 | "This recording is not part of a mission" | the ONLY explanation for a greyed GoTo - a recording with no TreeId, e.g. a manual Gloops ghost-only recording (rationale :1614-1620)
UI/TimelineWindowUI.cs:1679 | "Re-fly from the separation moment." | what "Fly" does on an Unfinished Flight separation row
UI/TimelineWindowUI.cs:1683 | "Close this slot without changing the recording." | what "Seal" does, and that it is non-destructive
UI/TimelineWindowUI.cs:1674 | "Action unavailable." | the last-resort Fly/Seal fallback when the route reason string is empty
ParsekUI.cs:796 | "Every recorded flight and career event on one clock." | the one-line statement of what the Timeline window IS, on its launcher

**From sec-05-kerbals-career.md**

UI/KerbalsWindowUI.cs:456 | Shows the stand-ins who have covered this kerbal's slot. | the ONLY statement anywhere that the arrow on an owner row opens a stand-in chain; the row text itself shows just a bare "(N)" count with no word for what N counts
UI/KerbalsWindowUI.cs:483 | Retired stand-ins that no longer belong to any crew slot. | the ONLY definition of "Unlinked Retired"; the section header is the bare phrase and the rows are bare names
UI/KerbalsWindowUI.cs:628 | Folds or unfolds this kerbal's recorded mission history. | the only statement that the outcomes header row is clickable at all
UI/KerbalsWindowUI.cs:643 | Scrolls the Timeline window to the flight this row came from. | the ONLY indication that an outcome row is a cross-link into the Timeline window; nothing in the row text says it is clickable or where it goes
UI/KerbalsWindowUI.cs:111 | Who fills each crew slot now: reserved, flying or retired stand-ins. | the only expansion of the two-word tab label "Roster State"
UI/KerbalsWindowUI.cs:113 | Every recorded flight a kerbal took, and how each one ended. | the only expansion of "Mission Outcomes"
UI/CareerStateWindowUI.cs:119 | Contracts you hold now, and the ones your recorded flights still complete. | the only place the current-vs-projected two-part shape of the Contracts tab is stated
UI/CareerStateWindowUI.cs:121 | Strategies running now, and the ones the recorded timeline activates later. | same for Strategies
UI/CareerStateWindowUI.cs:123 | KSC building levels now, and the levels the recorded timeline ends on. | same for Facilities
UI/CareerStateWindowUI.cs:125 | First-time achievements your recorded flights claim, and what each one paid. | the only statement that Milestones rows are FIRST-TIME achievements
UI/CareerStateWindowUI.cs:1511 | When the contract was taken on, in Universal Time (the game's own clock). | the only expansion of "UT" in the Contracts table
UI/CareerStateWindowUI.cs:1515 | When the contract expires, in Universal Time (the game's own clock). | ditto for the deadline column
UI/CareerStateWindowUI.cs:1519 | Whether this row is true now or still waiting on a recorded flight. | the ONLY explanation of the "(pending)" / "(closing)" tags; shared verbatim by Contracts (:1519), Strategies (:1624), Facilities (:1676) and Milestones (:1734)
UI/CareerStateWindowUI.cs:1616 | When the strategy was switched on, in Universal Time (the game's own clock). | ditto for Strategies
UI/CareerStateWindowUI.cs:1620 | What the strategy converts into what, and at what commitment. | the ONLY reading key for the "Src -> Tgt @ N.N%" Flow cell
UI/CareerStateWindowUI.cs:1672 | Upgrade level now, and the level the recorded timeline ends on. | the only explanation of the "L1 -> L3 (upcoming)" two-part level cell
UI/CareerStateWindowUI.cs:1725 | When the milestone was reached, in Universal Time (the game's own clock). | ditto for Milestones
UI/CareerStateWindowUI.cs:1730 | Funds, science and reputation this first-time achievement paid out. | the only key for the "+ N funds  + N rep  + N.N sci" abbreviations
UI/CareerStateWindowUI.cs:1479 | Rows your recorded flights still have to deliver; click to fold. | the only statement that the "Pending in timeline" bar is a fold control; shared verbatim with the Strategies copy at :1584

**From sec-06-logistics.md**

UI/LogisticsWindowUI.cs:1260 (via Logistics/LogisticsIntervalPresentation.cs:24) | "Dispatch cadence = N x run duration (transit {X}). Type an interval (30m, 2h, 1d, or plain seconds) or use -/+; it snaps up to a whole run-multiple." | the route's TRANSIT DURATION on a collapsed row - the standalone Transit column was deleted in the L2 narrowing (UI/LogisticsWindowUI.cs:1023-1025), and the accepted input grammar (30m / 2h / 1d / plain seconds) appears nowhere else
UI/LogisticsWindowUI.cs:1003 | leg.DestinationTooltip = the endpoint coords string | the endpoint lat/lon/alt whenever the vessel name RESOLVED - the cell then shows only the name (UI/LogisticsWindowUI.cs:3626-3629)
UI/LogisticsWindowUI.cs:1060 and :1074 (via UI/LogisticsHoldPresentation.cs:159) | "{RouteStatus enum} - {full hold clause}" | (a) the raw RouteStatus enum token, shown nowhere else on a collapsed row; (b) the UNTRUNCATED hold clause - the cell carries StatusCellText truncated at StatusCellMaxChars = 60 (UI/LogisticsHoldPresentation.cs:325)
UI/LogisticsWindowUI.cs:1030 | "Completed deliveries / blocked cycles (the ghost flew but delivered nothing)." | what the "/ N skipped" half of the Cyc cell means
UI/LogisticsWindowUI.cs:1039 | "Time until this route's next scheduled delivery (next dock crossing). A blocked route shows when it next rechecks eligibility. An inter-body route counts down to its next launch window." | which of the three countdown branches the bare T- number in the Next cell belongs to (the branch wording itself is only in the expanded detail line, LogisticsCountdownPresentation.FormatDetailCountdownLine)
UI/LogisticsWindowUI.cs:1082 | "Whether the route's last cycle actually delivered cargo, or the ghost flew but transferred nothing." | the meaning of "Flying, not delivering"
UI/LogisticsWindowUI.cs:915 | "Each candidate is a sealed, valid Supply Run: resources / inventory it would deliver to the destination per cycle. Create Route promotes it to a Paused route you can Send Once / Activate." | the "eligible / sealed" explanation relocated here when the Candidates Status column was dropped (comment UI/LogisticsWindowUI.cs:900-902)
UI/LogisticsWindowUI.cs:1519 (via Logistics/LogisticsCostPresentation.cs:75) | "Net cost per run = launch - recovered credits (distance-scaled recovery payout), credited one cycle later.[ Recovering the transport lowers future runs.]" | that the candidate's net cost is launch minus a DISTANCE-SCALED recovery credited one cycle LATE
UI/LogisticsWindowUI.cs:2159 | comma-joined 8-char short ids of the route's source recordings | the raw recording identifiers - the visible line shows resolved names + tree positions only (UI/LogisticsWindowUI.cs:2173-2204)
UI/LogisticsWindowUI.cs:2327 | candidate source recording short id | same, for the candidate detail
UI/LogisticsWindowUI.cs:658 | "Routes created after the rewind point you rewound past. Each is dormant (not dispatching, not visible elsewhere) and reappears Paused when the re-flown timeline reaches its creation date. Delete removes one for good." | the ONLY explanation of what dormancy is and when a dormant route comes back
UI/LogisticsWindowUI.cs:790 | "Committed trees that are not Supply Run candidates yet, with the reason: not fully sealed, or sealed but the run does not match the dock-deliver-undock proof." | the two-way split the near-miss reasons fall into
UI/LogisticsWindowUI.cs:857 | "Trees you dismissed as route candidates. A dismissed tree is hidden from the candidates and near-miss lists; Restore brings it back." | that a dismiss hides the tree from BOTH lists
UI/LogisticsWindowUI.cs:1108 (DisabledHoverEcho) | TooltipForArmedState - "Armed: this route will dispatch one cycle at the next dispatch window (funds, resources, endpoint, and alignment permitting), then return to Paused." / "Pause requested: this route finishes its current cycle, then stops auto-dispatching." | the semantics of the greyed armed button
UI/LogisticsWindowUI.cs:1181 and :2233 (DisabledHoverEcho) | "Already at the minimum (1x = the fastest the run allows)" | why the cadence "-" is greyed
UI/LogisticsWindowUI.cs:2280 (DisabledHoverEcho) | "Already at the highest priority (0)" | why the priority "-" is greyed
UI/LogisticsWindowUI.cs:1825 (DisabledHoverEcho) | LinkButtonDisabledReason (:1368) - "Pick a route above to pair this one with" | why the picker's Link button is greyed
UI/LogisticsWindowUI.cs:2027 (DisabledHoverEcho) | MissionLogButtonDisabledReason (:1380) - "This route was not built from a recorded mission" | why Log (Mission) is greyed
UI/LogisticsWindowUI.cs:1914 (DisabledHoverEcho) | RescanIneligibleReason (UI/LogisticsDeliveryPresentation.cs:631) - "Orbital endpoint: only the baked target PID can be matched. Re-create the route to point at a new vessel." | why re-scan cannot recover an orbital endpoint (ALSO drawn as a visible label at :1917, so tooltip-only for the button alone)
UI/LogisticsWindowUI.cs:2222 | "How often the route dispatches, as a multiple of the run duration. 1x is the floor (the fastest the run allows); raise it to launch less often." | the definition of the cadence multiplier
UI/LogisticsWindowUI.cs:2270 | "Lower priority number dispatches first when several routes contend in the same tick. 0 is the highest priority (and the default)." | the inverted priority ordering

**From sec-07-settings-tools.md**

UI/SettingsWindowUI.cs:750 | "There are no recordings to wipe" | why "Wipe All Recordings (0)" is greyed out; the button is a plain-string overload with no GUIContent, so this DisabledHoverEcho line is the only explanation anywhere (text at UI/SettingsWindowUI.cs:719-722)
UI/SettingsWindowUI.cs:759 | "There are no game actions to wipe" | same, for "Wipe All Game Actions (0)" (text at UI/SettingsWindowUI.cs:729-732)
UI/SettingsWindowUI.cs:686 | "Rewind-point quicksave disk use, by crashed / stable / concluded." | the only place the three RP retention buckets behind the one-line readout are named
UI/SettingsWindowUI.cs:666 | "Run runtime tests for ghosts and playback. Also Ctrl+Shift+T." | the ONLY in-UI statement that the Ctrl+Shift+T shortcut exists at all outside the two Test Runner windows' own footer labels
UI/SettingsWindowUI.cs:626 | "Log per-ghost render placement to KSP.log. Leave off unless debugging." | the only in-window statement of what ghostRenderTracing writes (the fuller GameParameters toolTip at ParsekSettings.cs:79 is the stock-difficulty surface, not this window)
UI/SettingsWindowUI.cs:636 | "Log map and Tracking Station ghost rendering to KSP.log. Leave off." | same for mapRenderTracing
UI/SettingsWindowUI.cs:646 | "Log ledger reconstruction and apply detail to KSP.log. Leave off." | same for ledgerTracing
UI/SettingsWindowUI.cs:656 | "Also write .txt mirrors of recording sidecars, for debugging." | same for writeReadableSidecarMirrors
UI/SettingsWindowUI.cs:535 | "Default launch-to-launch period for 'auto' rows. Shorter = overlap." | the only statement that this global IS the period of an Auto-unit mission, and that shorter values overlap launches
UI/SettingsWindowUI.cs:527 | "Show only the core loop: Timeline, Missions, Logistics, and Settings." | the only enumeration of what Basic keeps
UI/SettingsWindowUI.cs:528 | "Show every Parsek window and settings section." | the only statement of what Advanced adds
UI/TestRunnerUI.cs:396 | "Runs batch-safe plus [isolated] FLIGHT tests, quickloading a baseline after each destructive test." | the only place the isolation mechanism (quickload a baseline between destructive tests) is stated on the Run All + Isolated control
UI/TestRunnerUI.cs:403 | "Clears the table AND per-scene history used by the auto-exported results file." | the ONLY statement that explicit Reset differs from the implicit pre-run reset (which preserves cross-scene history)
UI/TestRunnerUI.cs:278 | "Runs this category plus any [isolated] FLIGHT tests by restoring a temporary baseline between destructive tests." | same for the per-category Run+ button
UI/TestRunnerUI.cs:451 | "Clear the search filter." | the only label the bare "x" button has
InGameTests/TestRunnerShortcut.cs:408 | "Runs batch-safe plus [isolated] FLIGHT tests, quickloading a baseline after each destructive test." | duplicate of the Settings-runner text, on the global window
InGameTests/TestRunnerShortcut.cs:418 | "Clears the table AND per-scene history used by the auto-exported results file." | duplicate, global window
InGameTests/TestRunnerShortcut.cs:475 | "Runs this category plus any [isolated] FLIGHT tests by restoring a temporary baseline between destructive tests." | duplicate, global window
InGameTests/TestRunnerPresentation.cs:133 | per-row tooltip = description + InGameTestRunner.GetBatchExecutionNote + "Requires {scene} scene" + error text | a row's test DESCRIPTION, its batch-admission rule and its failure message exist ONLY in this tooltip; the row label carries just the method name and the [isolated]/[single]/duration suffixes
UI/GloopsRecorderUI.cs:236 | "Records a ghost-only flight; your career never sees it." | the ONLY statement that a Gloops take is career-invisible
UI/GloopsRecorderUI.cs:257 | "Replays the last Gloops take as a ghost so you can check it." | the only statement of what Preview does
UI/GloopsRecorderUI.cs:278 | "Throws the current or last Gloops take away for good." | the only statement that Discard is irreversible and targets current-or-last
UI/SpawnControlUI.cs:334 | "Too far away to spawn" / "Passing too fast to spawn" / "This pass has already happened" | the row warp buttons carry NO GUIContent tooltip, so this DisabledHoverEcho line is the only explanation of a greyed row (text at UI/SpawnControlPresentation.cs:71-81)
UI/SpawnControlUI.cs:390 | "Warp to {name} (spawns in {t})" / "Warp to Depart: {name} (departs in {t})" / "No nearby craft to spawn" | which candidate the bottom-bar button would actually pick, and how long until it (text at SelectiveSpawnUI.cs:208-230); fed into the echo strip manually because the button draws below it
ParsekUI.cs:781 | "No recorded craft is passing nearby" | why the "Real Spawn Control (0)" launcher is greyed out (text at ParsekUI.cs:140-143)
ParsekUI.cs:779 | "Turn a recorded craft passing nearby into a real vessel." | the only one-line statement of what Real Spawn Control is for

**From sec-08-dialogs.md**

(empty - no GUIContent tooltip is constructed in any file in this section; PopupDialog
surfaces carry no hover tooltips. Grep for a two-arg GUIContent over MergeDialog*.cs,
ReFlyRevertDialog.cs, UnfinishedFlightSealHandler.cs and WarpToTimeController.cs returned
zero hits.)

**From sec-09-overlays.md**

CurrencyReservationOverlay.cs:199-200 | "Total: <n>\nReserved: <n>" | The Total and Reserved COMPONENTS of the funds/science bar. The bar itself shows only Available = Total - Reserved; nothing else in the mod displays the split.
CurrencyReservationOverlay.cs:201-202 | "Short by: <n>" | The magnitude by which the committed future OVERDRAWS the pool. Not a reservation, so the Total/Reserved pair cannot express it; this line is the only place it appears.
CurrencyReservationOverlay.cs:189-191 | "Balance: <n>" (+ "Short by") | The signed true balance when the bar is floored at zero. The bar shows 0; only this tooltip shows the real deficit.
StockUiOverlayController.cs:689-706 | "Committed at UT <ut> - recording '<name>'" (+ " (+N more committed)") | Which recording committed this tech node, and how many OTHER committed recordings also claim it.
StockUiOverlayController.cs:416 | "Will be accepted at UT <ut> - recording '<name>'" | That a Mission Control contract is already claimed by a committed future.
StockUiOverlayController.cs:621-623 | "Reserved by Parsek for a committed crew slot" / "Reserved by Parsek for slot '<owner>'" | Which committed slot is holding this applicant. The stock Astronaut Complex shows nothing.
StockUiOverlayController.cs:489 | "Retired stand-in (managed by Parsek)" | That this roster entry is a Parsek stand-in, not a real hire.
UI/DisabledHoverEcho.cs:8-15 | (carries CanRewind / CanFastForward / GetWatchButtonTooltip / Re-Fly slot reason) | The REASON a greyed-out Rewind / Forward / Watch / Re-Fly button is disabled. The button itself is only greyed; the reason exists solely in the echo strip.
WatchModeController.cs:1089-1091 | "<n> m" / "<n> km" in the overlay title | Ghost-to-active-vessel distance while watching. Not shown by any window.
WatchModeController.cs:1103-1104 | " [Horizon]" / " [Free]" | Which watch camera mode is active. No window reports it.


---

# APPENDIX 4 - surfaces with no picture yet

Every variant the two census runs did not photograph, with the cheapest route to one. Collected from each section's own fragment.


**From sec-01-main.md**

Flight status non-Idle variants (RECORDING / PREVIEWING / Ready (has recording)) and the Duration: label | both flight labels were captured on an idle pad craft with zero recorded points | b1-pad-craft: LoadGame -> StartRecording -> UiAction op=rect -> DumpGuiTree label=flight-main-recording-advanced. "Ready (has recording)" needs StopRecording before the dump; "PREVIEWING" needs a preview ghost playing, for which no seam verb exists -> NEW SEAM OP REQUIRED: a verb that starts preview playback (ParsekFlight.IsPlaying / PreviewGhost).
Active Ghosts > 0 in the flight status block | the pad fixture had no committed recording to spawn a ghost from | bdock-recorded (or any *-recorded fixture): LoadGame in FLIGHT -> wait for ghost spawn -> DumpGuiTree. Same run also buys the marker variants below.
Real Spawn Control launcher ENABLED (count > 0) | needs a committed recording whose craft passes within the proximity window of the live vessel; the census flew a lone pad craft | bdock-recorded staged so the recorded craft's trajectory passes near the live one, then WarpToUT to the pass, then UiAction op=complexity mode=advanced + DumpGuiTree. This is the same setup the existing flight-spawncontrol census label needs.
Real Spawn Control DISABLED hover (tooltip strip shows "No recorded craft is passing nearby") | DisabledHoverEcho only paints on a Repaint with the pointer inside the control's rect; the seam synthesises no pointer | NEW SEAM OP REQUIRED: a UiAction op that parks the IMGUI mouse position over a named control's rect for one Repaint (or a DumpGuiTree arg that supplies a synthetic Event.current.mousePosition). Nothing in the current verb table moves the cursor.
Tooltip strip populated by ANY hovered launcher | same cause as above | same NEW SEAM OP.
RouteRunPrompt supply-route banner (label + Open Logistics + Dismiss) | RouteRunPrompt.HasPendingPrompt was false in every capture; the prompt is armed at route-candidate commit time | depot-route-recorded or interbody-route-recorded: load the save with an eligible, non-dismissed candidate tree so RouteRunPrompt arms at commit, then UiAction op=rect -> DumpGuiTree label=ksc-main-routeprompt-advanced. Verify RouteStore.IsCandidateDismissed is false for the tree first, or the Layout-event sweep at ParsekUI.cs:822-827 clears the banner before the dump.
Logistics launcher broken-red tint | needs a committed route in EndpointLost / MissingSourceRecording / SourceChanged | depot-route-recorded with the route's source recording deleted via the DeleteRecording verb, then DumpGuiTree. The tint is a GUI.color change, so it shows in the PNG but NOT in the .gui.json (the tree carries no colour field) - pair the dump with CaptureScreenshot.
Logistics launcher pending-cyan tint | rides on the RouteRunPrompt fixture above | same fixture; colour again PNG-only.
Confirm: Wipe Recordings dialog | stock PopupDialog on KSP's UISkin - outside the IMGUI funnels GuiTreeRecorder patches, so DumpGuiTree cannot record it at all | CaptureScreenshot only, and it needs the dialog opened from UI/SettingsWindowUI.cs:753, which is a GUILayout.Button handler the seam does not click -> NEW SEAM OP REQUIRED: a UiAction op that invokes ParsekUI.ShowWipeRecordingsConfirmation directly (destructive - would have to be paired with a throwaway save).
Confirm: Wipe Game Actions dialog | same | same NEW SEAM OP (ShowWipeActionsConfirmation, UI/SettingsWindowUI.cs:762).
ALL map-marker variants (preview, proto-suppressed, polyline-ridden, per-instance overlap, boundary-overlap secondary, ghost-less fallback, sticky vs hover label) | neither flight label is a map-view capture, and markers are not windowed so they would appear in the tree only as bare GUI.Label / GUI.DrawTexture nodes | EnterMapView (an implemented verb) on a *-recorded fixture with live ghosts, then CaptureScreenshot + DumpGuiTree label=flight-map-markers-advanced. The sticky-label variant needs a right-click on a marker -> NEW SEAM OP REQUIRED (no verb synthesises a marker click); the hover-label variant needs the same cursor-parking op as the tooltip rows above.
Tracking Station scene (marker-only host, and the TS ghost popup) | no census label is a TRACKSTATION capture; UiAction answers REJECTED ui-host-unavailable there, so the existing census script has no TS lane | LoadGame with scene=TRACKSTATION on a *-recorded fixture, then CaptureScreenshot + DumpGuiTree. No UiAction is needed (there is no window to open), but the census driver currently sequences every label behind a UiAction op=rect, so the lane needs a TS branch that skips it.
Paused variant of any host (nothing drawn) | no seam verb opens the Esc / pause menu | NEW SEAM OP REQUIRED: a verb that raises PauseMenu (or drives PauseMenuGate.ProbeForTesting, which is a test-only seam and not exposed through the command seam). Low value: the expected picture is "the Parsek window is absent".
Gloops Flight Recorder launcher | retired in BOTH modes (UiSurfaceVisibility.IsRetired, UI/UiComplexityMode.cs:142); the gate at ParsekUI.cs:941 can never be true | UNREACHABLE - no fixture or seam step can produce it without a source change. Listed so the absence is recorded as intentional, not as a census gap.

**From sec-02-recordings.md**

Recordings tab in FLIGHT (Watch column visible) | harness/scenarios/GUI-2-census-flight.toml:171-177 opens the missions window, rects it, selects tab "missions", captures, closes - it never issues op=tab tab=recordings | add two steps to GUI-2 after :174: { cmd = "UiAction", args = { op = "tab", window = "missions", tab = "recordings" } } then CaptureScreenshot + DumpGuiTree with label "flight-missions-recordings-advanced". Uses the existing flight fixture, no new seam op.
Recording leaf rows (all 21 body cells, phase colours, status words, tree connectors) | every group in the KSC census renders collapsed; expansion is a caret CLICK and no UiAction op expands a group | NEW SEAM OP REQUIRED: UiAction op=expand window=missions target=<groupName|all>. Nothing short of it reaches a leaf row - op=rect only resizes, op=tab only switches tabs.
Chain block / grouped block header rows | same collapsed-tree reason; blocks only render as children of an expanded group | NEW SEAM OP REQUIRED: same op=expand. Fixture harness/fixtures/saves/bdock-recorded already carries multi-segment chains once expansion exists.
Info-expanded columns (MaxAlt, MaxSpd, Dist, Pts, Start, End) and the "Info <" label | the census never clicks the footer Info button; showExpandedStats is a private field with no seam | NEW SEAM OP REQUIRED: UiAction op=stats window=missions on=1 (or a generic op=click). Note the click also forces the window to DefaultExpandedWindowWidth=1813 (:1420), which exceeds the 1280 census screen - the capture would need a screen wider than 1813 or an op=rect right after.
STASH virtual group row and the Fly / Seal twin buttons | fixture c1-gui has no recording satisfying EffectiveState.IsUnfinishedFlight at capture time | fly GUI-1 against a fixture with a live rewind point + unresolved sibling. harness/fixtures/saves/ candidates with rewind structure are the -recorded family; the cheapest is a save produced by a rewind lane. Still needs op=expand to see the member rows, but the STASH HEADER row renders nested under its owning tree group only when that group is expanded too.
Stash / Seal twin (StashSeal branch, :3606) | needs a STABLE unfinished-flight slot, a strictly rarer state than the FlySeal branch | same fixture requirement plus a stable (non-crashed) split sibling; no existing committed fixture is known to carry one.
Group picker popup - "Manage Groups" title | opened only by a G button click; no UiAction op targets it | NEW SEAM OP REQUIRED: UiAction op=open window=grouppicker target=recording|chain|group. The popup is a real GUILayoutWindow (GroupPickerUI.cs:224) so DumpGuiTree would capture it once open.
Group picker popup - "Set Parent Group" title + "(None / Root level)" toggle | same, and additionally requires the group-mode entry point OpenForGroup (:160) | same new op, with target=group.
Route-bound greyed Loop toggles (header :1315, row :2033, folder :2621, block :4185) | needs a supply route bound to a recorded tree; c1-gui has none | reuse harness/fixtures/saves/depot-route-recorded or interbody-route-recorded as the GUI-1 saveTemplate; header-level greying then photographs with no expansion at all (AnyRecordingRouteBound scans all committed at :1303).
Inline rename text fields (recording :2211, group :2472) and the New Group auto-rename | armed only by a double click, or by the New Group button which arms renamingGroup (:1435) | the New Group path is reachable TODAY with one extra step: NEW SEAM OP REQUIRED: UiAction op=newgroup window=missions (or generic op=click target=newgroup), then capture - the fresh group renders with its TextField already focused.
Time-range filter indicator strip (:1489) | the filter is inactive in the fixture and is set from the Timeline window's sliders | NEW SEAM OP REQUIRED: UiAction op=timerange window=timeline preset=<name>, then op=tab window=missions tab=recordings and capture. No existing op writes TimeRangeFilterState.
Empty-table state ("No recordings." :1639) | c1-gui has 16 populated trees | point GUI-1's saveTemplate at harness/fixtures/saves/fresh-sandbox (or fresh-career) for one extra capture; no seam change needed.
Ghost-only row G + X twin (:1985) | needs rec.IsGhostOnly true in a non-TrackingStation scene | a save carrying an injected ghost-only recording; harness/fixtures presets that inject synthetic recordings are the cheapest route (scripts/inject-recordings.ps1 presets), plus op=expand to reach the row.
Disband-group confirmation dialog (:4431) and Rewind / Fast-Forward confirmations (:4474, :4510) | these are KSP PopupDialog instances spawned by a button click; no census step clicks a row or folder button | NEW SEAM OP REQUIRED: a generic UiAction op=click that can address a row/folder control by index. The folder R button is the cheapest target since it already renders enabled on all 16 census groups.

**From sec-03-missions-tab.md**

Missions tab - empty state "No missions recorded yet." (:826) | census host has 18 missions | fixtures/saves/fresh-sandbox: LoadGame scene=spacecenter -> UiAction op=open window=missions -> UiAction op=rect -> CaptureScreenshot + DumpGuiTree
Missions tab - Archive filter ON hiding a mission (:888) | needs a ticked per-mission Archive box | NEW SEAM OP REQUIRED: UiAction has only open/close/tab/complexity/rect (TestCommands/TestCommandUiAction.cs:8-29); no op ticks a Toggle
Missions tab - sorted by Name / Start time / descending (:4270, :4278, :4289) | header clicks are player clicks | NEW SEAM OP REQUIRED (a UiAction op that writes MissionsWindowUI.sortColumn/sortAscending)
Mission header - Loop ON, phase-locked read-only period label ("~6.4d (Mun window)") | every toggle in the census is off | fixtures/saves/mun-orbit-recorded (or mun-landing-recorded): load, open missions, tick Loop. NEW SEAM OP REQUIRED to automate the tick
Mission header - scheduled "varies" period ("~13d-1mo (Mun window, varies)") and re-aim "(Duna transfer)" period | as above, plus an interplanetary loop | fixtures/saves/duna-direct-recorded or duna-one-recorded + Loop on. NEW SEAM OP REQUIRED
Mission header - "Looped by route" label + greyed Loop toggle (:2564) | no route bound in the host | fixtures/saves/depot-route-recorded (or interbody-route-recorded / rover-route-recorded): load, open missions - the label draws with no click
Mission header - "Next launch" countdown / "not aligned" / "continuous" / amber tint (:3361) | the cell is blank unless the mission loops | same as the Loop-ON entries; the amber states additionally need a drifted station or a refused dual constraint (rover-relay-recorded family)
Mission header - "Warp to..." ENABLED, and its confirmation dialog (:2993) | needs looping + a built unit + a future relaunch | mun-orbit-recorded + Loop on + a click on Warp to... NEW SEAM OP REQUIRED
Mission header - "Forward" instead of "Rewind" (RecordingsTableUI.cs:3375) | needs a mission whose launch is ahead of now | a save with a scheduled future launch; no committed fixture obviously carries one
Mission header - "W*" (watching) (:2894) | flight scene with a ghost of the mission in visual range | GUI-2-census-flight host + a spawned ghost + EnterWatchMode; the seam HAS an EnterWatchMode verb, so: LoadGame -> UiAction op=open window=missions -> EnterWatchMode -> CaptureScreenshot + DumpGuiTree
Mission header - inline rename TextField (:2806) | double-click only | NEW SEAM OP REQUIRED
Vessel row - "(partial)" suffix and the dimmed excluded row (:1477, :1468) | needs an authored interval exclusion | NEW SEAM OP REQUIRED (a checkbox click, or a seam that writes Mission.ExcludedIntervalKeys)
Vessel row - "Docked with <partner>" in the Start event CELL (:2024) | no same-tree dock in the host | fixtures/saves/bdock-recorded: load, open missions - it draws with no click
Vessel row - Fly / Seal in the Re-Fly column (:1523) | needs an unfinished-flight recording | fixtures/saves/refly-a-recorded: load, open missions - it draws with no click
Expanded interval detail rows (:1547) | every vessel row in the census is collapsed | the census host already has two expandable rows (the "> L5-B4..." ones); one caret click. NEW SEAM OP REQUIRED
Chapter group header row, incl. the "[~] " Mixed marker (:1865, :1922) | no chapter resolved in the host | fixtures/saves/bdock-recorded or bdock-forge-base: load, open missions - the header draws with no click (the Mixed marker additionally needs a partial exclusion)
"Docked partner: <vessel>" row and its loiter phrase (:2098, :2165) | no cross-tree link in the host | fixtures/saves/bdock-recorded (or bdock-station-craft + bdock-station-pad): the ROW draws with no click
Foreign partner-journey staircase (included link) (:2128) | needs the link toggle ticked | as above + a click. NEW SEAM OP REQUIRED
Event digest EXPANDED + its "Go to" buttons (:2294, :2321) | foldouts are collapsed by default | the census host itself - one click on any "> Events (N)". NEW SEAM OP REQUIRED
Cross-link reveal (scrolled list, cleared Archive filter) (:587, :806) | no seam drives a Timeline GoTo | NEW SEAM OP REQUIRED; today's coverage is InGameTests/MissionRevealInGameTests.cs:197
Screen messages S9 / S10 and the S8 dialog | all three need a player click | see the Loop-ON entries; NEW SEAM OP REQUIRED
Structure window - POPULATED mission step list | op=open raises IsOpen without a target, so Rebuild never runs (TestCommandUiAction.cs:414-418) | the census host + one click on any mission's "Log". NEW SEAM OP REQUIRED (a UiAction op that calls ParsekUI.OpenStructureWindowForMission(treeId, name))
Structure window - POPULATED route step list, and the route empty-state wording "Nothing to show (source recording unavailable)." (:225-227) | same, from the Logistics side | fixtures/saves/depot-route-recorded + a click on "Log (Route)". NEW SEAM OP REQUIRED

**From sec-04-timeline.md**

Timeline window in FLIGHT (any view) | the GUI census flight run photographed no timeline label; the code draws it at ParsekFlight.cs:2133 and the seam declares InFlight=true (TestCommands/TestCommandUiAction.cs:401) | add to the flight census spec: UiAction op=open window=timeline; op=rect window=timeline 270,8,1000,700; op=tab window=timeline tab=overview; CaptureScreenshot + DumpGuiTree. Existing fixture, no new seam op.
Watch button "W" (enabled) | ShouldShowWatchButton requires inFlightMode (UI/TimelineWindowUI.cs:1644, call :1314), so it can never appear in a KSC capture | flight capture as above, on a save with a committed recording whose ghost is live, same body and within visual range (RecordingsTableUI.IsWatchButtonEnabled). harness/fixtures/saves/ recorded-flight fixture + a flight lane that spawns the ghost before the capture.
Watch button "W*" (watching state) | same, plus an active watch session | flight capture after a W click; needs a seam click op on a row action. NEW SEAM OP REQUIRED: UiAction op=click targeting a timeline row action button (today op=tab/rect/open/close only, TestCommands/TestCommandUiAction.cs:390-423).
Fast-Forward button "FF" | ShouldShowFastForwardButton needs isFuture, i.e. a committed recording whose StartUT is ahead of current UT (UI/TimelineWindowUI.cs:1564); the census save has none (zero FF buttons across all four advanced captures) | a save with a looped/scheduled recording, or warp the clock back before capturing. Cheapest: an injectedRecordings preset whose recording StartUT > save UT, then op=open + op=tab overview + capture.
"FF" greyed (any of its six reasons) | needs a future recording AND one of IsRewinding / no points / rewind-retired / recording / pending tree (RecordingStore.cs:5298-5340) | same future-recording fixture captured while a pending tree exists (fires "Merge or discard pending tree first"); reachable at KSC with a pending merge.
"R" greyed (five-precondition gate) | the census save produced 22 ENABLED R buttons and zero greyed ones | capture with a pending tree (reason 4) or after deleting one RewindPoints/<id>.sfs from the staged save (reason 5, "Rewind save file missing"). Both are one-file edits on an existing recorded fixture, no new seam op.
"Fly" / "Seal" buttons and the populated Re-Fly view | needs a TimelineEntryType.UnfinishedFlightSeparation whose RP route resolves (IsResolvedFlySealRoute, UI/TimelineWindowUI.cs:1600); ksc-timeline-refly-advanced is empty for exactly this reason | a rewind-point fixture with a live child slot - ScenarioWriter.BuildRewindPointQuicksave shape (see CLAUDE.md "RP quicksave fixtures are production-shaped"). Then op=tab window=timeline tab=refly + capture.
"GoTo" greyed | needs a timeline row for a recording with an empty TreeId - a manual Gloops ghost-only recording (UI/TimelineWindowUI.cs:1614-1620); all 34 GoTo buttons in the census are enabled | inject a treeless committed recording into the staged save, then capture Details view.
Custom time-range sliders | showCustomRange defaults false and no census op clicks "Custom" (UI/TimelineWindowUI.cs:166, :982) | NEW SEAM OP REQUIRED: there is no op that toggles showCustomRange. Alternatives: expose it the way TierFilterModeIndexForTesting is exposed (:113) and add an op=tab-style setter, or add a generic UiAction op=click by control label.
Any active time-range preset (Last Day / 7d / 30d / This Year) | the census leaves the filter cleared, so "All" is the on-state in every capture | same as above - no seam op writes TimeRangeFilterState. NEW SEAM OP REQUIRED: a setter over parentUI.TimeRangeFilter, or a generic click op.
"Archived" toggle ON, and an "[archived]" row suffix | requires flipping GroupHierarchyStore.HideActive (UI/TimelineWindowUI.cs:734-738) AND a recording with Recording.Hidden set | stage a save with one archived recording and HideActive=false in the ParsekScenario node, then the existing open/tab/capture sequence shows the "   [archived]" suffix (:1292-1294). Fixture-only, no new seam op.
Countdown time label on the first future row | TryConsumeCountdownTimeLabel fires only for the first visible strictly-future row (UI/TimelineWindowUI.cs:1229-1239); the census save has none | same future-recording fixture as the FF row above - one capture buys both.
"No timeline entries." empty state | requires a null/empty cache, i.e. a save with zero ERS recordings, zero ledger actions and zero milestones (UI/TimelineWindowUI.cs:1076-1080) | a clean-start save template with no Parsek data; open the Timeline and capture. Distinct from the refly-empty picture, which still draws the divider.
Warp-to-Time confirmation dialog ("Confirm: Warp to Time", 4 body shapes) | a stock PopupDialog raised only on a real button click (WarpToTimeController.cs:223) | NEW SEAM OP REQUIRED: no op clicks "Warp to time", and the dialog is a PopupDialog rather than a Parsek IMGUI window so op=open cannot reach it. Would need either a click op or a dedicated op=warpconfirm test hook.
"Warp to time" greyed with the two echo-only reasons | needs WarpToTimeRequest.HasPending or HasDeferredKscWarp (UI/TimelineWindowUI.cs:559-561) | reachable only mid-warp; a KSC capture immediately after a deferred flight warp arrives. Requires the click op above, so: NEW SEAM OP REQUIRED.
Resize / minimum-size rendering (520x150) | the census forces 1000x700 via op=rect (KSP.log:14749) | add a second op=rect at 520,8,520,150 plus a capture to the existing census spec. No new seam op - op=rect already accepts any rect.
Basic-mode Timeline ROWS | ksc-timeline-basic inherited the Re-Fly filter (see (f)); it photographs Basic chrome only | add one `UiAction op=tab window=timeline tab=overview` before the Basic capture in the census spec. Zero new machinery; this is the single cheapest fix in this list.

**From sec-05-kerbals-career.md**

Kerbals / Roster State POPULATED (expanded owner + chain-member tree rows) | The 9-node ksc-kerbals-roster-advanced capture had exactly one collapsed owner row and no orphan section; the expandedSlots set starts empty (UI/KerbalsWindowUI.cs:70) and nothing clicked the arrow | fixture harness/fixtures/saves/eva3-pad-3crew or any multi-crew career; seam: UiAction op=complexity mode=advanced; UiAction op=open window=main; UiAction op=open window=kerbals; UiAction op=rect window=kerbals x=270 y=8 w=1000 h=700; UiAction op=tab window=kerbals tab=roster; CaptureScreenshot + DumpGuiTree. NEW SEAM OP REQUIRED: nothing can expand an owner row - there is no op=click / op=expand, and expandedSlots has no test seam (unlike SelectedTabForTesting at :95). Either add an expand-state seam op or accept the collapsed picture.
Kerbals / Roster State "Unlinked Retired (N)" tail | Needs a retired stand-in name absent from every slot chain (UI/KerbalsWindowUI.cs:856-864); the census career had none | fixture: a career where a stand-in was retired and its owner slot was later removed. No such fixture exists under harness/fixtures/saves/. Cheapest: the roster-state unit fixtures already exercise Build(); for a PICTURE, NEW FIXTURE REQUIRED (a career save with an orphaned retired stand-in), then the same five-step seam chain as above.
Kerbals / Roster State reserved + active owner statuses | The one photographed owner is deceased (FormatOwnerHeader :508-:511); reserved / active branches at :512-:520 unphotographed | fixture harness/fixtures/saves/eva2-lko-crewed (crew aloft -> a live reservation); same seam chain with tab=roster. No new op needed.
Kerbals / Roster State EMPTY state ("No reserved crew, stand-ins, or retired kerbals.") | Requires zero slots AND zero retired (UI/KerbalsWindowUI.cs:350) | fixture harness/fixtures/saves/fresh-career; same seam chain with tab=roster. Cheapest picture in this whole appendix.
Kerbals / Mission Outcomes FOLDED header with the count summary | foldedKerbals starts empty (UI/KerbalsWindowUI.cs:64) and the census left the group unfolded, so FormatKerbalSummary (:701) never rendered | same fixture (fixtures/local-saves/c1-gui) - the data is already there. NEW SEAM OP REQUIRED: no op toggles foldedKerbals; it is internal but has no getter/setter seam. Add one, or add an op=click-style verb.
Kerbals / Mission Outcomes EMPTY state ("No committed crew history yet.") | Needs zero recordings with CrewEndStatesResolved (UI/KerbalsWindowUI.cs:362) | fixture harness/fixtures/saves/fresh-career; seam chain with tab=outcomes.
Kerbals + Career in the FLIGHT scene | Both are drawn in flight (ParsekFlight.cs:2134/:2135) but GUI-2-census-flight.toml opens only main/missions/spawncontrol/gloops | add to GUI-2-census-flight.toml, after its existing UiAction op=complexity mode=advanced: UiAction op=open window=kerbals; op=rect window=kerbals x=270 y=8 w=1000 h=700; op=tab window=kerbals tab=roster; Capture+Dump; op=tab tab=outcomes; Capture+Dump; op=close window=kerbals; then the same for window=career over its four tabs. No new op needed - both windows are already in the UiAction window table with driveable tabs.
Kerbals + Career in BASIC | No picture BY CONSTRUCTION: UiSurface.MainButtonKerbals / MainButtonCareer are visibleInBasic=false (UI/UiComplexityMode.cs:181-:182, :188), so the launcher does not exist and an Advanced->Basic switch closes an open window (ParsekUI.cs:492-503) | NOT REACHABLE and must not be forced. The Basic evidence for these two surfaces is the ksc-main-basic capture showing the launcher ABSENT, which the census already has.
Career / Contracts POPULATED rows + SPLIT layout + "Pending in timeline" toggle + "(pending)" amber rows | The census career held zero contracts at liveUT and had no future contract actions, so RowsEqual (:1446) took the collapsed branch | fixture harness/fixtures/saves/career-contract-pad; seam: UiAction op=complexity mode=advanced; op=open window=main; op=open window=career; op=rect window=career x=270 y=8 w=1000 h=700; op=tab window=career tab=contracts; CaptureScreenshot + DumpGuiTree. The pending group defaults to EXPANDED (foldedGroups starts empty, :105), so no click is needed to photograph it.
Career / Contracts "(closing)" status and "--" deadline | Needs a contract active now but absent from the terminal snapshot (:671), and a deadline-less contract | same career-contract-pad fixture if its ledger closes a contract after liveUT; otherwise NEW FIXTURE REQUIRED (a career with a recorded flight that completes a held contract). Same five-step seam chain, tab=contracts.
Career / Strategies POPULATED rows (the Flow "Src -> Tgt @ N.N%" cell) | Zero strategies were ever active on the census save; header read "Administration L1 - slots 0/1" | fixture harness/fixtures/saves/strategy-career; same seam chain with tab=strategies. No new op needed.
Career / Facilities upgraded + destroyed rows ("L1 -> L3 (upcoming)", "(destroyed)", "(destroyed, repair pending)") | Every photographed row is L1 with an empty Status column | fixture harness/fixtures/saves/career-earned-ksc (an earned, upgraded KSC); same seam chain with tab=facilities. For the destroyed variants NEW FIXTURE REQUIRED (a career carrying a facility-destroyed ledger action); no existing fixture name implies one.
Career / Milestones "(pending)" amber rows | All 25 census rows credited at or before liveUT, so IsPendingCredit (:814) was false everywhere | NEW FIXTURE REQUIRED: a career whose UNCOMMITTED recorded timeline credits a milestone after the live UT. Cheapest approach is a career fixture plus an uncommitted recording that reaches a first-time achievement; then the standard five-step seam chain with tab=milestones.
Career / mode banner divergence suffix "(timeline ends at UT N)" | vm.HasDivergence was false on the census save; the banner rendered bare (:1406-:1410) | any fixture whose ledger has actions beyond liveUT - the same fixture that buys the Contracts split layout (career-contract-pad) should also buy this, since both derive from the same future-action condition. Same seam chain, any tab.
Career / SCIENCE_SANDBOX mode ("Science mode - contracts and strategies unavailable" banner, "Contracts are unavailable in Science mode.", "Strategies are unavailable in Science mode.", and the Facilities/Milestones tabs still populated) | Census save is CAREER | fixture harness/fixtures/saves/fresh-science; same five-step seam chain, all four tabs. No new op needed - this is the single cheapest way to photograph four otherwise-dead code paths (:1414, :1437, :1543, and the deliberate ABSENCE of a science branch in :1639/:1690).
Career / SANDBOX mode ("Sandbox mode - career state is not tracked" banner + the same empty text on all four tabs) | Census save is CAREER | fixture harness/fixtures/saves/fresh-sandbox; same seam chain, one tab is enough for the banner but all four for the four empty-state labels (:1432, :1538, :1643, :1694).
Career / null-game fallback ("Career state unavailable - game not loaded") | Requires HighLogic.CurrentGame == null with the window open (:1320), i.e. mid-scene-transition | NOT PHOTOGRAPHABLE by the census: UiAction's precondition is "game loaded, any scene that HOSTS the Parsek UI", so the seam refuses in exactly the state this branch needs. Leave it uncovered; it is already unit-reachable through DrawCareerStateWindow's guard.

**From sec-06-logistics.md**

Active Routes populated | census save had zero routes | harness/fixtures/saves/interbody-route-recorded (ROUTE "Route: KSC -> Duna", status = Active) or depot-route-recorded (status = Active, completedCycles = 1). Seam: UiAction op=open target=logistics, then op=rect to >= 1410x500 so the Name column is not squeezed to 60px, then the census capture.
Paused Routes populated + cyan "New (not yet run)" + "New - use Send Once to test" guidance line | same | harness/fixtures/saves/interbody-route-recorded ("Route: KSC -> Mun", status = Paused, completedCycles = 0) gives exactly this row. Same two seam ops.
Send-Once-armed row (blank Interval cell + 160px disabled "Sending one cycle..." button) | needs Route.PauseAfterCurrentCycle set on a non-Paused route | depot-route-recorded already has status = Active with pauseAfterCurrentCycle = True. Alternatively RouteCommand action=send-once on a Paused route, then op=open target=logistics.
Hard-broken (red) Status cell / held (yellow-overridden) Status cell | needs EndpointLost / MissingSourceRecording / SourceChanged, or a persisted Route.LastHold* | NEW SEAM OP REQUIRED: no verb sets a route status or seeds a hold. Cheapest today is a hand-authored fixture save with status = EndpointLost and LastHoldKind set on a ROUTE node.
Dormant Routes (N) disclosure, expanded and collapsed | no fixture carries a dormant route | NEW SEAM OP REQUIRED (no verb creates one). A dormant route is produced by rewinding past a route's CreatedUT; cheapest is a hand-authored DORMANT_ROUTES node in a copy of interbody-route-recorded.
Dismissed (N) disclosure, expanded | census save had nothing dismissed | harness/fixtures/saves/interbody-route-recorded already carries DISMISSED_ROUTE_CANDIDATES with two treeIds -> the collapsed "Dismissed (2)" header photographs with op=open alone. Expanding it needs the row-expand op below.
Near-miss subsection EXPANDED (the 18 rows and their reject sentences) | the disclosure starts collapsed and nothing toggles it | NEW SEAM OP REQUIRED: UiAction op=expand key=<expandedRows key> (keys are the fixed constants "nearmiss:section" :763, "dismissed:section" :830, "dormant:section" :649, and "cand:"+treeId / route.Id for rows).
Candidates populated (rows + Create Route / Dismiss + run-cost suffix) | census save had zero eligible candidates (18 near-misses instead) | harness/fixtures/saves/rover-route-recorded or rover-route-career: both carry PROMPTED_ROUTE_CANDIDATES, i.e. a tree that WAS classified eligible, and no ROUTES node, so the tree is still an un-promoted candidate. Run-cost suffix additionally needs Career + KSC origin -> rover-route-career.
Route detail panel (rename row, 4 buttons, cadence + priority steppers, cost line, flow lines, hold line, countdown line) | requires a row to be expanded | NEW SEAM OP REQUIRED: the same op=expand as above. UiAction's logistics handler exposes only IsOpen and WindowRectForTesting (TestCommands/ParsekTestCommandAddon.UiAction.cs:636-641).
Candidate detail panel | same | NEW SEAM OP REQUIRED (op=expand with key "cand:"+treeId).
Link picker window | armed only from an expanded route row's "Link round-trip..." button | NEW SEAM OP REQUIRED, and it is a DECLARED census exclusion (TestCommands/TestCommandUiAction.cs:373-376): driving it needs a row context, not just a window flag. Cheapest honest fixture is a two-route save (interbody-route-recorded) plus an op=expand and an op that arms OpenLinkPicker.
Confirm: Delete Route dialog | raised by an X click | NEW SEAM OP REQUIRED (no click op). PopupDialogs are also outside the parsek-gui-tree capture (the census funnels patch GUI.DoWindow, not the TMP-backed PopupDialog).
Confirm: Delete Dormant Route dialog | same, plus needs a dormant route | NEW SEAM OP REQUIRED (both a dormant route and a click op).
Create Supply Route? (window-raised, 3 buttons) | raised by a Create Route click on a candidate row | NEW SEAM OP REQUIRED for the click; note RouteCommand action=create drives the BUILD path directly (RouteCreationService) and bypasses the dialog, so it proves the outcome but photographs nothing.
Create Supply Route? (post-commit, RouteCreationDialog) | fires on a tree COMMIT, not on any window flag | NEW SEAM OP REQUIRED. The dialog has a production-shaped test seam (RouteCreationDialog.TestHookForConfirm, UI/RouteCreationDialog.cs:40) but it BYPASSES the real PopupDialog, so it cannot produce a picture either.
Record-Supply-Run banner in the main window | no prompt pending in the census save | rover-route-recorded has the tree already in PROMPTED_ROUTE_CANDIDATES, so it will NOT re-prompt (at-most-once, Logistics/RouteRunPrompt.cs:88-92). NEW SEAM OP REQUIRED, or a fixture whose eligible tree is absent from that set.
Logistics button RED broken tint / CYAN pending-prompt tint | needs a hard-broken route or a pending prompt | same two blockers as the rows above; the tint is in ParsekUI.cs:871-878 (outside this section's files) but is decided by UI/LogisticsButtonState.cs:27.

**From sec-07-settings-tools.md**

Real Spawn Control (every variant) | No capture in either run, by design: GUI-2 declares `op=open window=spawncontrol` as `expect = "ERROR"` and nothing follows it (harness/scenarios/GUI-2-census-flight.toml:152). Runs 2026-09-10_2259 / _2300 answered `window-self-closed ... frames=1` because SpawnControlUI.DrawIfOpen force-closes on its first draw when ResolveAutoCloseReason finds zero candidates (UI/SpawnControlUI.cs:131-142) - the normal state of the census host's sub-orbital probe. Tracked as GUI-CENSUS-SPAWN-CONTROL-NEEDS-A-CANDIDATE-HOST. | A GUI-3 flight lane on a committed fixture whose focus HAS a recorded craft inside the inner gates (<=250 m, <=2 m/s): harness/fixtures/saves/bdock-recorded (or bdock-station-craft / bdock-station-pad). Seam steps: LoadGame save=${runSave} name=persistent -> UiAction op=complexity mode=advanced -> UiAction op=open window=main -> UiAction op=open window=spawncontrol (expect OK) -> UiAction op=rect window=spawncontrol x=270 y=8 w=900 h=420 -> CaptureScreenshot label=flight-spawncontrol-advanced -> DumpGuiTree label=flight-spawncontrol-advanced -> UiAction op=close window=spawncontrol.
Real Spawn Control - empty-list fallback ("No nearby craft to spawn." + Close) | Unreachable by the seam: DrawIfOpen auto-closes on zero candidates BEFORE the window callback runs, so the in-callback empty branch (UI/SpawnControlUI.cs:232-239) can only be reached when the list empties between the auto-close check and the draw. | NEW SEAM OP REQUIRED: nothing can hold the window open through a zero-candidate frame. Either an automation-only bypass of ResolveAutoCloseReason, or accept that this branch is effectively dead and delete it.
Real Spawn Control - too-far / too-fast greyed row with the DisabledHoverEcho text | Needs a candidate between the inner (250 m / 2 m/s) and outer (1000 m / 50 m/s) envelopes AND a pointer resting on its warp button. | Same GUI-3 lane as above on a fixture with a ghost 300-900 m out; the hover half needs a pointer-position op. NEW SEAM OP REQUIRED: no `op=hover` / pointer-move verb exists in TestCommandUiAction (ops are open, close, tab, complexity, rect, describe - TestCommandUiAction.cs:157-161).
Real Spawn Control - "Departing ->" / "Departs T-..." State column | Needs a candidate whose recording has departure info (willDepart = true, SelectiveSpawnUI.ComputeDepartureInfo). | GUI-3 lane on a fixture whose nearby recording departs: harness/fixtures/saves/depot-route-recorded or interbody-route-recorded staged so the departing ghost passes the focus.
Gloops - Recording block (Recording... / Points: N / Duration) | flight-gloops-advanced caught the Empty block only; no seam verb starts a Gloops recording. | NEW SEAM OP REQUIRED: a verb that calls ParsekFlight.StartGloopsRecording (UI/GloopsRecorderUI.cs:248). Cheapest alternative with no new op: none - the window's own primary button is not clickable from the seam.
Gloops - Saved block (Saved: "name" / Points / Duration) and the Stop Preview state | Needs flight.LastGloopsRecording non-null. | Stage harness/fixtures/saves/gloops-airshow as the GUI-2 host and re-run the existing gloops steps; if that fixture's last take does not survive the load, a new seam op for StartGloopsRecording/StopGloopsRecording is required.
Settings - "Basic" mode button disabled (Gloops recording live) | Needs parentUI.Flight.IsGloopsRecording == true while the Settings window draws (UI/SettingsWindowUI.cs:475, :487). | Same missing Gloops-start verb as above. With it: UiAction op=open window=gloops -> <start> -> UiAction op=open window=settings -> Capture/Dump label=flight-settings-gloopsrecording. The seam already models the resulting refusal as `complexity-refused-gloops-recording` (TestCommandUiAction.cs:245).
Settings - "Settings unavailable (no active game)." fallback | ParsekSettings.Current is null only outside a loaded game, and the seam dispatcher waits for a loaded game before running any UiAction. | NEW SEAM OP REQUIRED (or accept none): there is no scene with a ParsekUI host and no active game.
Settings - auto-loop textfield mid-edit, and both wipe buttons greyed | Mid-edit needs keyboard focus in the named control `AutoLoopEdit`; greyed wipes need a store with 0 recordings / 0 milestones (the census host has 110 / 49). | Greyed wipes: a GUI-3 pass on harness/fixtures/saves/fresh-career or fresh-sandbox - steps UiAction op=open window=main -> op=open window=settings -> op=rect window=settings x=270 y=8 w=400 h=700 -> Capture/Dump label=ksc-settings-empty-store. Mid-edit: NEW SEAM OP REQUIRED (no keyboard-focus / type verb).
TestRunnerUI - RUNNING state (Cancel enabled, yellow rows), any failed row + red error row, active search filter, a collapsed category | ksc-testrunner-advanced is an idle, unfiltered, fully expanded snapshot (624 play rows, 113 expanded headers, 0 collapsed). | RUNNING/failed: drive RunTests through the existing command seam against a category with a known red, then DumpGuiTree while it runs - needs a dump issued mid-batch, which the dispatcher's safe-point gate refuses (TestRunnerShortcut.cs:105 ORs batch state into IsBatchRunning). NEW SEAM OP REQUIRED for all four: no verb types into the search field, toggles a category, or dumps mid-batch.
TestRunnerShortcut (global Ctrl+Shift+T window) - every variant | Not photographed and not reachable: the census `testrunner` token resolves to TestRunnerUI (TestCommands/ParsekTestCommandAddon.UiAction.cs:665-670), and TestRunnerShortcut.showWindow is a private field with no accessor and no window-table entry (TestCommandUiAction.cs:430-432). | NEW SEAM OP REQUIRED: add a window-table entry plus an open-flag accessor on TestRunnerShortcut, or a keystroke-injection verb for Ctrl+Shift+T. Worth doing once: this window differs from the Settings one in its footer labels, its missing search bar and its much wider input-lock set, and no picture of it exists anywhere.
Ghost labels (SpawnWarningUI.ComputeGhostLabelText) - all four states | Both flight captures were taken with Active Ghosts: 0. The labels are bare GUI.Label calls outside any window, so they would appear in the PNG only, never in a .gui.json. | A GUI-3 flight lane on a host with an active ghost chain (harness/fixtures/saves/mun-landing-recorded or b2-lko-craft), camera framed on the ghost, then CaptureScreenshot. The blocked / walkback-exhausted / terminated variants additionally need those chain states, which no seam verb produces. NEW SEAM OP REQUIRED for the three non-nominal variants.
SpawnWarningUI.FormatWarningText / ShouldShowWarning | Never rendered: zero call sites outside SpawnWarningUI.cs (grep over Source/Parsek returns only the declarations). | No fixture can produce a picture. This is dead code to delete or wire up, not a missing capture.

**From sec-08-dialogs.md**

Tree merge dialog / post-transition / Re-Fly variant (MergeDialog.cs:274) | PopupDialog is uGUI; GuiTreeFunnels.cs:109-114 patches only GUI.DoWindow (IMGUI) | fixture refly-a-recorded; seam: InvokeRewind{rp,slot} -> AnswerMergeDialog is the ANSWERING verb but answers-and-closes in one step; to photograph, insert CaptureScreenshot between the driven conclusion and the answer - NEW SEAM OP REQUIRED: a "surface only, do not answer" mode on AnswerMergeDialog (today ParsekTestCommandAddon.cs:2260 finds and immediately invokes)
Tree merge dialog / post-transition / ordinary whole-tree variant (MergeDialog.cs:274) | same; plus AnswerMergeDialog is gated on ActiveReFlySessionMarker != null (ParsekTestCommandAddon.cs:2258-2260) | NEW SEAM OP REQUIRED: an AnswerMergeDialog path that does not require a live re-fly marker, or a UiAction op that raises ParsekScenario's deferred merge coroutine
Tree merge dialog / pre-transition / all four button sets (MergeDialog.cs:468) | same uGUI reason; the journal-active 1-button set additionally needs a live ActiveMergeJournal | fixture b1-pad-craft; seam: StartRecording -> ExitToSpaceCenter raises it, but ExitToSpaceCenter completes the transition - NEW SEAM OP REQUIRED: a CaptureScreenshot that runs while the blocked LoadScene is pending, i.e. a scene-exit verb that stops at the dialog
Pre-switch decision dialog / Case A and Case B (MergeDialog.cs:726) | same uGUI reason; SimulateStockSwitchClick is PLAIN-PATH-ONLY and treats both dialog cases as REFUSALS (ParsekTestCommandAddon.SimulateSwitchClick.cs:35-45) | fixture bdock-recorded (two vessels); NEW SEAM OP REQUIRED: a SimulateStockSwitchClick mode that lets the dialog cases proceed instead of refusing
Re-Fly revert dialog / 3-button (journal idle) and 2-button (journal active) (ReFlyRevertDialog.cs:236) | same uGUI reason; no verb drives stock RevertToLaunch / RevertToPrelaunch | fixture refly-a-recorded; NEW SEAM OP REQUIRED: a verb that invokes RevertInterceptor.Prefix (or the stock revert it patches) while a re-fly marker is live, plus a button-answer op keyed on ReFlyRevertDialog.DialogName "ParsekReFlyRevert"
Committed-action blocked dialog / with and without resourceDetail (CommittedActionDialog.cs:31) | same uGUI reason; CommittedActionDialog.TestHookForTesting (:12) short-circuits the spawn whenever set | fixture career-earned-ksc; seam: KscAction against a blocked facility upgrade / tech node / contract / hire - NEW SEAM OP REQUIRED: the seam must leave TestHookForTesting null and then CaptureScreenshot while the OK popup sits open
Re-Fly confirmation dialog / with siblings, without siblings, advisory-absent (RewindInvoker.cs:509) | same uGUI reason; the InvokeRewind verb bypasses ShowDialog entirely (ParsekTestCommandAddon.cs:2124-2129) | fixture refly-a-recorded; NEW SEAM OP REQUIRED: a UiAction op that clicks the Recordings-table / Timeline Fly button (UI/RecordingsTableUI.cs:3521, UI/TimelineWindowUI.cs:1511) instead of calling StartInvoke
Scene-exit save-failed popup / single variant (SceneExitInterceptor.cs:585) | same uGUI reason; requires GamePersistence.SaveGame to THROW on a flight -> MAINMENU exit (SceneExitInterceptor.cs:563-572) | NEW SEAM OP REQUIRED: fault injection on SafeWritePersistent (e.g. a read-only save folder) - no fixture reaches it
Ghost icon context menu / six button-3 label states (Patches/GhostVesselLoadPatch.cs:324) | same uGUI reason; nothing clicks a map orbit node | fixture mun-orbit-recorded; seam: EnterMapView reaches the scene, EnterWatchMode reaches the BACKEND not the menu - NEW SEAM OP REQUIRED: a verb that invokes GhostOrbitNodeClickPatch.Prefix for a named ghost pid, then CaptureScreenshot
Tracking Station ghost popup / five status phases x enabled/disabled (ParsekTrackingStation.cs:1276) | same uGUI reason; no verb sets GhostTrackingStationSelection.SelectedGhost | fixture mun-orbit-recorded staged into TRACKSTATION; NEW SEAM OP REQUIRED: a verb that selects a TS ghost (drives UpdateSelectedGhostPopup at ParsekTrackingStation.cs:1154 past its four dismiss gates), then CaptureScreenshot
Unfinished-flight seal confirmation / single variant (UnfinishedFlightSealHandler.cs:214) | same uGUI reason; the SealSlot verb calls UnfinishedFlightSealHandler.TrySeal directly (TestCommands/ParsekTestCommandAddon.SealSlot.cs:21, :92, :221), never ShowConfirmation | fixture refly-a-recorded; NEW SEAM OP REQUIRED: a UiAction op that clicks the Recordings-table Seal button (UI/RecordingsTableUI.cs:3656), then CaptureScreenshot before answering
Warp-to-time confirmation / five body variants (WarpToTimeController.cs:223) | same uGUI reason; ShowConfirmation is private and only reached from RequestWarp (:94); no seam verb references WarpToTimeController | fixture mun-orbit-recorded (forward) / refly-a-recorded (rewind); NEW SEAM OP REQUIRED: a UiAction op that clicks the Timeline window Warp button (UI/TimelineWindowUI.cs:574), then CaptureScreenshot

**From sec-09-overlays.md**

Watch Mode overlay (all 4 variants) | not inside a GUI.Window, so the GuiTree recorder cannot see it (Patches/GuiTreeRecorderPatches.cs:224) and no census run entered watch mode | flight fixture with >=1 committed recording + the existing EnterWatchMode seam verb (TestCommands/ParsekTestCommandAddon.EnterWatchMode.cs) + NEW SEAM OP: full-screen capture rather than window-tree capture
Currency reservation tooltip (4 text forms x funds/science) | hover-only AND outside a GUI.Window | career fixture with a committed future + NEW SEAM OP: park the pointer over the stock funds/science widget rect, then full-screen capture
Stock-UI badges, R&D (amber) | uGUI RawImage, not IMGUI at all | c2 career fixture with a committed tech + NEW SEAM OP: open the R&D screen and capture
Stock-UI badges, Mission Control (blue) | same | c2 career fixture with a committed contract + NEW SEAM OP: open Mission Control and capture
Stock-UI badges, applicant 4 kinds (green / grey / orange / violet) | same | career fixture with a committed hire and a reserved slot + NEW SEAM OP: open the Astronaut Complex and capture
Stock-UI badge tooltips (5 text forms) | hover-only uGUI | as above, plus pointer parking
Ghost map marker, flight map (icon / hover label / sticky label / fallback diamond) | bare GUI.DrawTexture + GUI.Label outside any window | flight fixture with a committed recording + the existing EnterMapView verb + NEW SEAM OP: full-screen capture
Ghost map marker, Tracking Station (same, plus popup-open click-block) | no TS census lane exists | any fixture with committed recordings + a scene-change step into TRACKSTATION + NEW SEAM OP: full-screen capture
TooltipEchoBox strip, POPULATED | strip is captured but only ever empty; the census parks no pointer | any existing census fixture + NEW SEAM OP: position the pointer over a named control (by gui.json ref) before the capture
TooltipEchoBox strip, MARQUEE mode | needs a text longer than the strip width | same, over a control whose tooltip overflows (e.g. a Logistics refusal reason)
DisabledHoverEcho carrier | paints nothing by design; its visible artefact is the echo strip | same as the populated-strip row, over a DISABLED control (e.g. a greyed Rewind with a reason)


---

# APPENDIX 5 - per-section UiSurface gate citations (cross-check of appendix 1)


**From sec-01-main.md**

MainButtonSpawnControl | ParsekUI.cs:771 | Real Spawn Control launcher (main window, FLIGHT only)
MainButtonSpawnControl | ParsekUI.cs:262 | same key, read by IsSpawnControlReachable (not a draw site; decides the wording of ParsekFlight.NotifyNewProximityCandidates' screen message)
MainButtonKerbals | ParsekUI.cs:904 | Kerbals launcher (main window)
MainButtonCareer | ParsekUI.cs:906 | Career launcher (main window)
MainButtonGloops | ParsekUI.cs:941 | Gloops Flight Recorder launcher - RETIRED in BOTH modes via UiSurfaceVisibility.IsRetired (UI/UiComplexityMode.cs:142), so this gate is never true and the block never draws
MainButtonTimeline | (no call site) | constant-true in both modes; the Timeline launcher at ParsekUI.cs:794 is deliberately UNWRAPPED rather than carrying a predicate that can never be false (ParsekUI.cs:760-765)
MainButtonRecordings | (no call site) | same: the Missions launcher at ParsekUI.cs:806 is unwrapped
MainButtonLogistics | (no call site) | same: the Logistics launcher at ParsekUI.cs:885 is unwrapped
MainButtonSettings | (no call site) | same: the Settings launcher at ParsekUI.cs:955 is unwrapped
TabRecordings / TabMissions / MissionsLoopControls / SettingsSectionLooping / SettingsSectionDiagnostics / SettingsSectionSampleDensity | (outside this section) | owned by RecordingsTableUI / MissionsWindowUI / SettingsWindowUI

**From sec-02-recordings.md**

UiSurface.TabRecordings | UI/RecordingsTableUI.cs:189 (inside VisibleTabCount, the only IsVisible call in this section) | the "Recordings" tab button in the two-tab toolbar, and with it the entire Recordings-tab body: the 21-column table header (:1196), the group/chain/block/STASH/recording row tree (:1784-1804), the Info and New Group footer buttons (:1413, :1427), the time-range filter indicator (:1635), and every route to the GroupPickerUI popup (hence the forced close at :231)

**From sec-03-missions-tab.md**

UiSurface.MissionsLoopControls | UI/MissionsWindowUI.cs:735 (the single IsVisible call, wrapped as ShowsLoopAuthoringControls) | -
UiSurface.MissionsLoopControls | UI/MissionsWindowUI.cs:785 (DrawMissionsTabContent) | not a draw: drops an open loop-period edit uncommitted in Basic
UiSurface.MissionsLoopControls | UI/MissionsWindowUI.cs:1419 + :1444 (DrawVesselRow) | the per-vessel include checkbox (replaced by a blank ColW_Index label)
UiSurface.MissionsLoopControls | UI/MissionsWindowUI.cs:1427 (DrawVesselRow) | the vessel row's expand caret / expanded interval detail rows (expandable = loopAuthoring && Intervals.Count > 1)
UiSurface.MissionsLoopControls | UI/MissionsWindowUI.cs:1632 (DrawCompositionRow) | the per-interval include checkbox on detail + foreign-journey rows
UiSurface.MissionsLoopControls | UI/MissionsWindowUI.cs:2043 + :2045 (DrawForeignDockLinkRows) | the partner-journey include checkbox (the "Docked partner" ROW itself stays)
UiSurface.MissionsLoopControls | UI/MissionsWindowUI.cs:2505 + :2506 (DrawMissionHeader) | the Clone button
UiSurface.MissionsLoopControls | UI/MissionsWindowUI.cs:2546 (the loopAuthoring branch at :2545-2563) | the "Loop" label + loop toggle
UiSurface.MissionsLoopControls | UI/MissionsWindowUI.cs:2586 | the loop-period cell (value field + unit button)
CONFIRMED NOT GATED (checked by grep, no IsVisible call touches them): "Looped by route" label (UI/MissionsWindowUI.cs:2564-2575); the "Next launch" / TTL column (header :4287, cell :1499 -> :3361); "Warp to..." (:2526 -> :2953); Watch (:2595 -> :2860). Also ungated: the "#" sort header (:4266, sortable in BOTH modes), the mission index number (:2467), Delete (:2510), Archive (header :4302, row :2616), Log (:2488), Rewind/Forward (:2607), the event digest + Go to (:2272, :2321), the chapter header row's tri-state toggle (:1885) and the "Docked partner" row body (:2100).
TabRecordings / TabMissions | not called in MissionsWindowUI.cs or StructureListWindowUI.cs | the tab bar is the chrome's (UI/RecordingsTableUI.cs); Basic's missing buttongrid is visible in ksc-missions-basic

**From sec-04-timeline.md**

UiSurface.TabMissions | UI/TimelineWindowUI.cs:1265 | the per-row "GoTo" cross-link button (both the RecordingStart consumer at :1400 and the Separation consumer at :1442). Hides NOTHING today: TabMissions is visibleInBasic=true (UI/UiComplexityMode.cs:175). It is the target-surface gate, present so retargeting GoTo at a hidden surface would auto-hide the button.
UiSurface.MainButtonTimeline | (no IsVisible call; deliberately unwrapped at ParsekUI.cs:794, rationale ParsekUI.cs:762-764) | nothing - Basic KEEPS the Timeline launcher because it is the only access to rewind / FF / warp-to (UI/UiComplexityMode.cs:171)

**From sec-05-kerbals-career.md**

UiSurface.MainButtonKerbals | ParsekUI.cs:904 (no IsVisible call exists inside UI/KerbalsWindowUI.cs) | the "Kerbals" main-window launcher button (ParsekUI.cs:912-921) and, transitively, the whole "Parsek - Kerbals" window (UI/KerbalsWindowUI.cs:205); an Advanced->Basic switch also force-closes an already-open window via ParsekUI.cs:498-503
UiSurface.MainButtonCareer | ParsekUI.cs:906 (no IsVisible call exists inside UI/CareerStateWindowUI.cs) | the "Career" main-window launcher button (ParsekUI.cs:923-931) and, transitively, the whole "Parsek - Career State" window (UI/CareerStateWindowUI.cs:1211); Advanced->Basic force-closes it via ParsekUI.cs:492-497

**From sec-06-logistics.md**

MainButtonLogistics | (no IsVisible call site anywhere in the repo; declared UI/UiComplexityMode.cs:56, classified visibleInBasic=true UI/UiComplexityMode.cs:173) | nothing - the "Logistics" button is drawn unconditionally at ParsekUI.cs:885-891 and the window content is byte-identical in Basic and Advanced (census: 58 nodes each, structurally identical)
(none) | UI/LogisticsWindowUI.cs - zero IsVisible calls in the whole 3861-line file | no section, row, control, tooltip or dialog in this section is complexity-gated

**From sec-07-settings-tools.md**

UiSurface.SettingsSectionLooping | UI/SettingsWindowUI.cs:345 | edit-state teardown guard: drops an uncommitted auto-launch-period edit when the Looping section is not drawn
UiSurface.SettingsSectionLooping | UI/SettingsWindowUI.cs:378 | Settings "Looping" section: the "Auto-launch every" label + value textfield + unit-cycle button (5 gui-tree nodes)
UiSurface.SettingsSectionDiagnostics | UI/SettingsWindowUI.cs:393 | Settings "Diagnostics" section: verboseLogging / ghostRenderTracing / mapRenderTracing / ledgerTracing / writeReadableSidecarMirrors toggles, the "In-Game Test Runner" and "Run Diagnostics Report" buttons, and the rewind-point disk-usage readout (9 gui-tree nodes). Taking the launcher with it is what makes TestRunnerUI unreachable in Basic.
UiSurface.SettingsSectionSampleDensity | UI/SettingsWindowUI.cs:401 | Settings "Recorder Sample Density" section: the Low / Medium / High selector row plus its summary label (6 gui-tree nodes)
UiSurface.MainButtonSpawnControl | ParsekUI.cs:771 | the "Real Spawn Control (N)" launcher button in the main window's flight-only top block (hidden in Basic)
UiSurface.MainButtonSpawnControl | ParsekUI.cs:262 | not a draw gate: ParsekUI.IsSpawnControlReachable, read by ParsekFlight.NotifyNewProximityCandidates to decide whether the proximity screen message names the window
UiSurface.MainButtonGloops | ParsekUI.cs:941 | the "Gloops Flight Recorder" launcher button - RETIRED, so hidden in Advanced too (UiSurfaceVisibility.IsRetired, UI/UiComplexityMode.cs:140-143); the window BODY is never gated

**From sec-08-dialogs.md**

(empty - no UiSurface key or IsVisible call appears in any of the 12 files in this section;
grep for "UiSurface|IsVisible(" over MergeDialog*.cs, ReFlyRevertDialog.cs,
CommittedActionDialog.cs, RewindInvoker.cs, SceneExitInterceptor.cs,
Patches/GhostVesselLoadPatch.cs, ParsekTrackingStation.cs, UnfinishedFlightSealHandler.cs,
WarpToTimeController.cs returned zero hits. Dialogs are not Basic/Advanced gated.)

**From sec-09-overlays.md**

(none - no UiSurfaceVisibility.IsVisible call exists in any file in this section; verified by grep over CurrencyReservationOverlay.cs, OverlayBadge.cs, StockUiOverlayController.cs, MapMarkerRenderer.cs, WatchModeController.cs, UI/TooltipEchoBox.cs, UI/TooltipMarquee.cs, UI/DisabledHoverEcho.cs)


---


# REPORT - counts, picture tally, and the three most surprising findings

## Counts by kind

Every count below is the size of a program-wide grep result, not a hand tally.

| kind | count | how it was derived |
|---|---|---|
| IMGUI windows (distinct) | **14** | 16 `GUILayout.Window` / `ClickThruBlocker.GUILayoutWindow` / `GUI.Window` call sites, minus the test-only probe at `InGameTests/GuiTreeDumpImguiTest.cs:484`, minus the second host of the main window (`ParsekFlight.cs:2118` and `ParsekKSC.cs:245` draw one window) |
| IMGUI window HOSTS | 15 | same grep; 14 use `ClickThruBlocker`, 1 (`InGameTests/TestRunnerShortcut.cs:204`) does not |
| tabs | **12** | Missions 2, Timeline 4, Kerbals 2, Career State 4 |
| modal dialogs | **21** | `PopupDialog.SpawnPopupDialog(` over `Source/Parsek` excluding `InGameTests/` and `TestCommands/` |
| in-window sections (expand/collapse bubbles, table headers, footers, banners, detail panels) | ~38 | sum of the per-section `(a)` entries classified `section`; see sections 1 to 7 |
| overlays / markers / badges | **7** | watch-mode overlay, currency tooltip, stock-UI badge family, flight-map markers, Tracking Station markers, in-world ghost labels, the Logistics launcher tint |
| tooltip infrastructure surfaces | **2** | `TooltipEchoBox` strip (11 host windows), `DisabledHoverEcho` carrier |
| screen-message producers | **95** | `ParsekLog.ScreenMessage` + `ScreenMessages.PostScreenMessage`, 107 raw hits minus 12 doc comments / test sinks / wiring (appendix 2) |
| scene OnGUI hosts | 6 | `ParsekFlight.cs:2087`, `ParsekKSC.cs:227`, `ParsekTrackingStation.cs:350`, `CurrencyReservationOverlay.cs:87`, `OverlayBadge.cs:122`, `InGameTests/TestRunnerShortcut.cs:177` |
| stock toolbar buttons | 2 registrations, 1 button | `ParsekFlight.cs:1354` (FLIGHT + MAPVIEW), `ParsekKSC.cs:157` (SPACECENTER) |
| `UiSurface` gate keys | 14 declared, **12** enforcement call sites, **4 keys with none** | appendix 1 |

## Picture tally

The two census runs produced 27 labels: 23 KSC + 4 flight. One (`parsek-guitree-probe`) is
the recorder's own test window, leaving **26 player-facing captures**.

| kind | photographed | not photographed |
|---|---|---|
| windows | **10 of 14** | Logistics round-trip link picker; Real Spawn Control; the global Ctrl+Shift+T Test Runner; the Group picker |
| tabs | **12 of 12** | none |
| modal dialogs | **0 of 21** | all 21 |
| overlays / markers / badges | **0 of 7** | all 7 |
| tooltip surfaces in a USEFUL state | **0 of 2** | the echo strip is in every window capture but always EMPTY; `DisabledHoverEcho` paints nothing by design |
| screen messages | **0 of 95** | all 95 |

So the census covers **22 of 105** countable surfaces (windows + tabs + dialogs +
overlays/markers/badges + tooltip surfaces + toolbar), and of the screen-message and
section layers it covers only what happened to be on screen.

A second, quieter gap: of the 26 player-facing captures, **8 photograph an essentially
empty surface**. From the node counts in the dumps: `ksc-kerbals-roster-advanced` (9
nodes), `ksc-structure-advanced` (3), `ksc-timeline-refly-advanced` (39),
`ksc-timeline-basic` (39), `ksc-career-contracts-advanced` (17),
`ksc-career-strategies-advanced` (17), `ksc-logistics-advanced` (58) and
`ksc-logistics-basic` (58). The Recordings tab's 415 nodes are 16 collapsed group headers
and zero leaf rows.

Three distinct reasons account for all of it, and only the first is a fixture problem:

| reason | example | fix |
|---|---|---|
| the fixture had no data of that shape | Career contracts, Kerbals roster, Logistics routes | a different fixture (appendix 4 names one per row) |
| the state needs a CLICK and no seam verb clicks | every expanded row, every detail panel, the Group picker, the link picker, populated Structure | a new `UiAction op` (expand / select / click) |
| the surface is outside the recorder's reach by construction | all 21 dialogs, all 7 overlays, every populated tooltip | full-screen capture plus pointer parking, not a window-tree dump |

## The three most surprising things the code does that the census did not show

**1. Parsek injects a whole third UI layer into three STOCK KSP screens, and nothing can
photograph it.** `StockUiOverlayController` (`StockUiOverlayController.cs:49`) hooks
`RDController.OnRDTreeSpawn`, `GameEvents.onGUIAstronautComplexSpawn` and
`GameEvents.onGUIMissionControlSpawn` (`:71-76`) and attaches an 18x18 `OverlayBadge` to
individual tech nodes (`:233`), applicant rows (`:293`) and contract rows (`:354`). Six
distinct tint colours carry the semantics (`:235`, `:358`, `:987-994`) and five tooltip
texts carry information that exists nowhere else in the game: which recording committed a
tech node and how many others also claim it (`:689-706`), that a contract is already
claimed by a committed future (`:416`), which committed slot is holding an applicant
(`:621-623`), and that a roster entry is a Parsek stand-in rather than a real hire
(`:489`). It is uGUI, so the GuiTree recorder cannot see it; the tooltip is hover-only, so
a screenshot cannot either. This family was not in the brief's file list and does not
appear in any of the 27 captures.

**2. A complete, fully unit-tested per-recording hover panel is dead code.**
`RecordingsTableUI.DrawRecordingTooltip` (`UI/RecordingsTableUI.cs:5451`) is 78 lines
rendering stats, chain status, a storage/efficiency breakdown, and resource, inventory and
crew deltas. A repo-wide grep for `DrawRecordingTooltip` returns exactly one hit: its own
declaration. It is also the ONLY production caller of
`FormatResourceManifest` / `FormatInventoryManifest` / `FormatCrewManifest`
(`UI/RecordingsTableUI.cs:5496`, `:5500`, `:5504`, forwarding to
`UI/RecordingsTableFormatters.cs:121`, `:188`, `:251`) - every other reference is in
`Source/Parsek.Tests/FormatResourceManifestTests.cs`,
`FormatInventoryManifestTests.cs` and `FormatCrewManifestTests.cs`. So three formatters
with dozens of green assertions each are kept alive purely by their own tests, and the
resource / inventory / crew delta of a recording is currently unreachable from the UI.

**3. The Basic/Advanced complexity gate is only half-wired, and the half that is missing
is invisible because it happens to agree with the half that exists.** Of the 14
`UiSurface` keys, four - `MainButtonTimeline`, `MainButtonRecordings`,
`MainButtonLogistics`, `MainButtonSettings` - have ZERO `IsVisible` call sites anywhere
(program-wide grep, appendix 1); their launchers draw unconditionally. Separately,
`UiSurfaceVisibility.HiddenSurfaces` (`UI/UiComplexityMode.cs:214`), documented as "the
mode-change close handler" consumer, has no production consumer at all: its only reference
outside its own file is the doc comment at `ParsekUI.cs:471` explaining why the real close
set is NOT derived from it. The real set is the hand-written `BuildGatedWindowCloseSet`
(`ParsekUI.cs:488-529`), which carries two entries that map to no `UiSurface` (TestRunner,
GroupPicker) and omits ones that do. The census photographed Basic/Advanced pairs for four
windows and they matched perfectly - which is exactly the outcome an unenforced gate
produces when its unenforced keys are all classified "keep".

## Honourable mentions

| finding | file:line | why it matters |
|---|---|---|
| Real Spawn Control cannot be opened by the seam | `harness/scenarios/GUI-2-census-flight.toml:44-51`, `UI/SpawnControlUI.cs` `ResolveAutoCloseReason` | `op=open window=spawncontrol` returns `window-self-closed frames=1` because there are zero nearby candidates; the step is pinned `expect = "ERROR"`. The window needs a flight state with candidates before any picture is possible |
| the census forced Logistics below its own minimum | `op=rect ... w = 1280` vs `MinWindowWidth = 1410f` (`UI/LogisticsWindowUI.cs:390`, clamped only on drag at `:436`) | `op=rect` writes the field directly and only a resize DRAG clamps, so the shipped picture shows a squeezed layout with a horizontal scrollbar, not the real one. Any future populated capture must size to >= 1410 first |
| a live layout defect is visible in a shipped capture | Milestones `Rewards` column, `UI/CareerStateWindowUI.cs` 180f pin | two three-part reward cells render at height 36 inside a 21 px row grid in `ksc-career-milestones-advanced.gui.json` - IMGUI wrapped them |
| the pre-switch dialog shares the merge dialog's name | `MergeDialog.cs:730` uses `MergeDialog.DialogName = "ParsekMerge"`; `TestCommands/ParsekTestCommandAddon.cs:2536` matches on exactly that name and picks buttons by ORDER | with a live re-fly marker and an open pre-switch dialog, `AnswerMergeDialog` would answer the wrong popup and report success |
| `basicUiMode` is a dead field with a stale comment | `UI/MissionsWindowUI.cs:281` (declared), `:753` (assigned), never read | its comment at `:277-280` claims Basic renders `#` as a label, but `DrawColumnHeader` makes it a sort button in both modes |
| one interval-writing control escapes the Basic gate | `UI/MissionsWindowUI.cs:1885` draws a tri-state include Toggle with no `ShowsLoopAuthoringControls` check | in Basic a chapter header row still offers a click that writes `Mission.ExcludedIntervalKeys` while every checkbox around it is hidden |
| two `SpawnWarningUI` methods are unreachable | `SpawnWarningUI.cs:23` `ShouldShowWarning`, `:40` `FormatWarningText` | zero call sites; two player-facing strings that are never rendered |
| un-retiring Gloops is not a one-line change | `UI/UiComplexityMode.cs:140-143` returns early for `MainButtonGloops`, so it has no `case` in the Basic switch at `:168-197` | flipping `IsRetired` throws `ArgumentOutOfRangeException` on the first draw until a Basic decision is added - the fail-loud contract working, but not what `ParsekUI.cs:937-940` implies |
| the Tracking Station hosts no Parsek window | `ParsekTrackingStation.cs:350` OnGUI draws markers only; source says so at `:394-395` | any proposal that adds a TS surface starts from zero, not from an existing window |
| the global Test Runner has no click-through protection | `InGameTests/TestRunnerShortcut.cs:204` uses raw `GUILayout.Window`, unlike the other 14 hosts | it compensates with its own mouse-containment input lock at `:213` |