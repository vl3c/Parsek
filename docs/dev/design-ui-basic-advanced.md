# Parsek Basic / Advanced UI Mode - Design Document

*Design specification for a Settings-level UI complexity mode that hides non-essential Parsek windows and controls behind an Advanced toggle.*

*Parsek is a KSP1 mod for time-rewind mission recording. Players fly missions, commit recordings to a timeline, rewind to earlier points, and see previously recorded missions play back as ghost vessels alongside new ones. This document specifies the UI complexity mode: which surfaces Basic hides, how the gate is implemented, and the visibility-only guarantee.*

**Status:** PLANNING (analysis complete, not implemented). All blocking decisions are RESOLVED; ready to implement. First-run default (section 7.3): the stored setting always wins, Basic is the default for new installs only, an existing install is never changed. Basic hide-set (section 4): as specified, with Logistics explicitly kept visible for discoverability (philosophy 7); the conditional "appear once used" variant is rejected in section 10. Naming (section 4.1): the main-window button, window title, and first/default tab all become Missions in BOTH modes, the one deliberate Advanced-visible change in this feature; section 4.2 lists the "Recordings" strings that must NOT be renamed.
**Version:** 0.1
**Out of scope:** any change to recording, playback, logistics dispatch, or ledger behavior. This is a visibility gate only, see section 9.
**Related docs:** `docs/dev/design-mission-abstractions.md` (Missions tab), `docs/parsek-timeline-design.md` (Timeline), `docs/parsek-logistics-supply-routes-design.md` (Logistics).

---

## 1. Introduction

Players report the Parsek UI is too complicated. The main window presents eight launch buttons, which open windows totalling roughly 25,000 lines of IMGUI across 13 distinct surfaces. Several of those surfaces are read-only reference panels or power-user tools that a player never needs in order to use the mod's core loop.

This document specifies a **UI complexity mode**: a single Settings toggle with two values, `Basic` and `Advanced`. `Advanced` is exactly today's UI, unchanged. `Basic` hides the surfaces that are not required to fly, commit, loop, and rewind.

This document covers:
- A full inventory of every Parsek UI surface (section 3).
- The essentiality analysis that decides what Basic hides (section 4).
- The pure-decision-core gate design and its data model (sections 5 and 6).
- Behavior on mode switch, including open windows and input locks (section 7).
- Separate UI improvement proposals, independent of this feature (section 17).

This document does NOT cover: restyling any window, changing any window's internal layout beyond hiding whole sections, or a new onboarding flow (proposed in section 17, deferred).

### 1.1 What the player sees

| Situation | What happens |
|-----------|--------------|
| Fresh Parsek save, first open of the main window | Four buttons: Timeline, Missions, Logistics, Settings |
| Player sets Settings -> Interface -> Advanced | Main window immediately grows to the full eight-button set; nothing else changes |
| Player switches back to Basic while the Career window is open | Career window closes, its input lock is released, its state is preserved |
| Player in Basic mode flies, commits, loops, and rewinds a mission | Every step works; no hidden window is required at any point |
| Existing save with recordings, first run after update | Stays on Advanced (see section 7.3), so nothing the player used disappears |

### 1.2 Worked example

A new player installs Parsek and starts a career save.

1. Main window opens with `Timeline`, `Missions`, `Logistics`, `Settings`. Four buttons instead of eight.
2. They launch a rocket. Recording starts automatically (`autoRecordOnLaunch = true`), no UI needed.
3. They return to the Space Center. The scene-exit Merge dialog commits the recording, unchanged in Basic.
4. They open `Missions`, see one mission, set it to loop every 6 hours, and tick its include checkbox.
5. They open `Timeline`, find the launch row, and click `R` to rewind to it.
6. The ghost of the first flight replays alongside their new one.

At no point did they need Recordings (raw per-recording table), Kerbals, Career, Gloops, or Real Spawn Control. Those four windows plus the Recordings tab are what Basic hides.

---

## 2. Design Philosophy

1. **Visibility only, never behavior.** Basic hides UI. It never disables recording, playback, dispatch, or ledger work. A route keeps delivering whether or not the Logistics window is reachable. This is the single most important invariant, and section 9 lists what it protects.
2. **Nothing is destroyed, only hidden.** Switching Basic -> Advanced restores every window with its state intact. No data is dropped, no setting is reset. The mode is reversible at any time with no cost.
3. **Basic must be sufficient, not merely smaller.** The Basic set is chosen so the full core loop (fly, commit, loop, watch, rewind, supply) is reachable. If a Basic player has to switch to Advanced to finish a normal task, the split is wrong.
4. **One pure decision point.** Every gate call routes through one pure, Unity-free predicate so the split is unit-testable and greppable, rather than eight scattered `if` statements that drift.
5. **Read-only panels are the first thing to cut.** A window that only reports state a player can find on stock screens is the cheapest thing to hide and the least missed.
6. **Advanced is behaviorally identical to today, with one named exception.** The feature must not become an excuse to restyle the existing UI; any improvement in section 17 ships as its own change. The single deliberate exception is the Recordings-to-Missions rename and tab reorder of section 4.1, which applies in BOTH modes by explicit decision, because a label that differs between modes would defeat the consistency it exists to create. No other Advanced-visible change ships with this feature.
7. **Basic is a starting point, not a cage.** The goal is a UI a new player can take in at a glance, not the smallest possible UI. A feature the player should eventually discover and use stays visible in Basic even when it is not strictly required, because a button they grow into is how they learn the mod exists. This is what separates a surface that is HIDDEN (a raw table or a developer panel they would never grow into) from one that is merely NOT YET USED (Logistics). It is also why conditional "appear once used" visibility is rejected in section 15: a surface that materializes only after you already found the feature cannot teach you the feature is there.

---

## 3. Full UI Inventory

### 3.1 Main window (`ParsekUI.DrawWindow`, `ParsekUI.cs:193-380`)

The launcher, opened by the stock ApplicationLauncher toolbar button (`ParsekFlight.cs:1301` for FLIGHT/MAPVIEW, `ParsekKSC.cs:128` for SPACECENTER). Contents in draw order:

| Element | Condition | Line |
|---------|-----------|------|
| Flight status line | InFlight only | `ParsekUI.cs:197` |
| Compact budget line | always | `ParsekUI.cs:200` |
| `Real Spawn Control (N)` | InFlight only, disabled when N=0 | `ParsekUI.cs:216` |
| `Timeline` | always | `ParsekUI.cs:230` |
| `Recordings` | always | `ParsekUI.cs:239` |
| Supply Route candidate banner | when `RouteRunPrompt.HasPendingPrompt` | `ParsekUI.cs:248` |
| `Logistics` | always, red tint when broken, cyan when prompt pending | `ParsekUI.cs:314` |
| `Kerbals` | always | `ParsekUI.cs:329` |
| `Career` | always | `ParsekUI.cs:335` |
| `Gloops Flight Recorder` | InFlight only | `ParsekUI.cs:346` |
| `Settings` | always | `ParsekUI.cs:356` |
| Version footer + `Close` | always | `ParsekUI.cs:371` |

### 3.2 Windows launched from the main window

Interactive-control counts are mechanical (`Button(` and `Toggle(` occurrences) and are used in section 4 as a proxy for "control surface vs reference panel".

| Window | File | Lines | Button( | Toggle( | Nature |
|--------|------|-------|---------|---------|--------|
| Recordings table | `UI/RecordingsTableUI.cs` | 5739 | 62 | 13 | Heavy control surface |
| Missions tab (hosted in the above) | `UI/MissionsWindowUI.cs` | 2581 | 17 | 5 | Control surface |
| Logistics | `UI/LogisticsWindowUI.cs` | 3820 | - | - | Route management |
| Career State | `UI/CareerStateWindowUI.cs` | 1924 | 2 | 2 | Read-only reference |
| Timeline | `UI/TimelineWindowUI.cs` | 1591 | 29 | 10 | Actionable (rewind, warp) |
| Kerbals | `UI/KerbalsWindowUI.cs` | 841 | 4 | 0 | Read-only reference |
| Settings | `UI/SettingsWindowUI.cs` | 591 | - | - | Configuration |
| Real Spawn Control | `UI/SpawnControlUI.cs` | 366 | - | - | InFlight utility |
| Gloops Flight Recorder | `UI/GloopsRecorderUI.cs` | 330 | - | - | Manual ghost-only recorder |

The Career window's two buttons are the tab toolbar and `Close`; the Kerbals window's four are tabs, per-kerbal folds, and `Close`. Neither window mutates game or Parsek state. Both are pure reporting surfaces.

### 3.3 Tabs inside windows

| Host window | Tabs | Source |
|-------------|------|--------|
| Recordings | `Recordings`, `Missions` | `RecordingsTableUI.cs:127` |
| Career State | `Contracts`, `Strategies`, `Facilities`, `Milestones` | `CareerStateWindowUI.cs:87` |
| Kerbals | `Roster State`, `Mission Outcomes` | `KerbalsWindowUI.cs:68` |

### 3.4 Settings window sections

`SettingsWindowUI.cs`, in draw order: `Recording` (262), `Looping` (302), `Ghosts` (401), `Stock UI` (432), `Diagnostics` (459), `Recorder Sample Density` (536), `Data Management` (561). The Diagnostics section also hosts the in-game Test Runner launch (`SettingsWindowUI.cs:511`).

### 3.5 Contextual surfaces (not launched from the main window)

| Surface | File | Opened from |
|---------|------|-------------|
| Log window (mission / route step list) | `UI/StructureListWindowUI.cs` | Missions row `Log`, Logistics `Log (Route)` / `Log (Mission)` |
| Group picker popup | `UI/GroupPickerUI.cs` | Recordings tab group assignment |
| Route creation dialog | `UI/RouteCreationDialog.cs` | Logistics `Create` |
| In-game test runner | `UI/TestRunnerUI.cs` | Ctrl+Shift+T, or Settings -> Diagnostics |
| Spawn warning | `SpawnWarningUI.cs` | Spawn flow |
| Merge / Discard dialogs | `MergeDialog.cs` | Scene exit, pre-switch |
| Map markers | `ParsekUI.DrawMapMarkers` | Map view / Tracking Station |

These are reached only through a parent surface. Gating the parent gates them implicitly, so they need no explicit rule except the Test Runner (section 6.3).

---

## 4. Essentiality Analysis

The test applied to each surface: **can a player complete the core loop (fly -> commit -> loop / watch -> rewind -> supply) without it?**

| Surface | Basic | Rationale |
|---------|-------|-----------|
| Timeline | **Keep** | The only access to rewind (`R`), fast-forward (`FF`), and `Warp to time`. Irreplaceable. |
| Logistics | **Keep** | The only surface for supply routes. Broken-route red tint is a player-visible error channel. Kept visible even for a player with zero routes, per philosophy 7: it is the button that teaches them supply routes exist. See section 10 for the rejected conditional-visibility variant. |
| Settings | **Keep** | Hosts the mode toggle itself. Must always be reachable. |
| Missions tab | **Keep** | The player-facing mission abstraction: name, loop period, Watch, Clone, Delete, Archive, include checkboxes, Log. Sufficient for all routine recording management. |
| Recordings tab | **Hide** | The raw per-recording table (62 buttons, 13 toggles). Everything a normal player needs is expressed at the Mission level. This is the single largest complexity reduction available. |
| Career window | **Hide** | 2 buttons, 2 toggles, zero mutations. Reports contracts / strategies / facilities / milestones that stock screens already show, with a projected column. Pure power-user reference. |
| Kerbals window | **Hide** | 4 buttons, 0 toggles, zero mutations. Reports roster state and per-kerbal mission outcomes. Stand-in mechanics run correctly whether or not the player watches them. |
| Gloops Flight Recorder | **Hide** | Manual ghost-only recording. An explicitly opt-in power feature; the automatic recorder covers the normal path. |
| Real Spawn Control | **Hide** | Proximity spawning of nearby recorded vessels. Advanced staging tool, already conditional (InFlight, disabled at zero candidates). |
| Settings: Diagnostics | **Hide** | Verbose logging, ghost/map/ledger render tracing, Test Runner. Developer instrumentation; the tracing toggles warn about huge logs. |
| Settings: Sample Density | **Hide** | Recorder fidelity tuning. A wrong value degrades recordings; the Medium default is correct for normal play. |
| Settings: Data Management | **Keep** | Includes the pre-Parsek backup control and destructive data actions the player may legitimately need. Keep, but see section 8 edge case 6. |

**Result.** Basic shows four main-window buttons (Timeline, Missions, Logistics, Settings) instead of eight, one tab instead of two in the Missions window, and five Settings sections instead of seven.

### 4.1 Naming and tab order (DECIDED, applies to BOTH modes)

Today a button named `Recordings` opens a window titled `Parsek - Recordings` whose first tab is `Recordings` and whose second tab, `Missions`, holds the abstraction players actually work with. In Basic, where the Recordings tab is hidden, that naming would be actively wrong. Making the label mode-dependent would be worse: the same button would carry two names depending on a setting.

**Decision.** Missions becomes the primary identity of this window in both modes:

- The main-window button reads **`Missions`** (`ParsekUI.cs:239`).
- The window title reads **`Parsek - Missions`** (`RecordingsTableUI.cs:434`).
- The tab order becomes **`{ "Missions", "Recordings" }`**, so Missions is both first and the default selection (`RecordingsTableUI.cs:127`).
- The tab constants swap to `TabMissions = 0`, `TabRecordings = 1`, and the default becomes `selectedTab = TabMissions` (`RecordingsTableUI.cs:124-126`).

Because the label no longer varies by mode, the planned `GetRecordingsMainButtonLabel(mode)` helper is unnecessary; a constant label is correct, and the `GetKerbalsMainButtonLabel` indirection pattern is not needed here.

**This is safe to reorder.** `selectedTab` is explicitly transient and not persisted (`RecordingsTableUI.cs:120-122`, "Transient (not persisted), matching the Kerbals / Career State tab idiom"). No player has a stored tab index that this reorder could flip, so there is no migration concern.

The dispatch at `RecordingsTableUI.cs:1190` is keyed on the named constants (`if (selectedTab == TabMissions)`), not on literal ints, so swapping the constant values carries it automatically. Verify this holds at implementation time rather than assuming it.

### 4.2 Strings that must NOT be renamed

"Recordings" appears in several places that are not this button. Renaming any of them is a defect, not a follow-through:

| Site | Why it must not change |
|------|------------------------|
| `"ParsekRecordings".GetHashCode()` (`RecordingsTableUI.cs:432`) | The IMGUI window ID. Changing it gives the window a new identity, resetting its saved position and size and risking an ID collision with another window. |
| `Path.Combine("Parsek", "Recordings", ...)` (`RecordingPaths.cs:16-50`, `RecordingStore.OrphanCleanup.cs:275`, `Analyzer/Rules/Inv7bAnnotationStale.cs:38`) | The on-disk sidecar directory. Renaming it orphans every existing recording. |
| `LogWindowPosition("Recordings", ...)` (`RecordingsTableUI.cs:445`) | A diagnostic log key. Changing it breaks grep continuity with every historical KSP.log and collected log snapshot. |
| `GUILayout.Toggle(showRecordingEntries, "Recordings", ...)` (`TimelineWindowUI.cs:695`) | A Timeline entry-type filter, an unrelated feature that genuinely filters recording rows. Correctly named. |

Exactly two user-facing strings change: the button at `ParsekUI.cs:239` and the window title at `RecordingsTableUI.cs:434`, plus the tab array and constants. A grep for `"Recordings"` will surface all of the above; the table is the disposition for each hit.

---

## 5. Mental Model

```
                    ParsekSettings.uiComplexityMode  (persisted, global)
                                   |
                                   v
                      UiComplexityMode { Basic=0, Advanced=1 }
                                   |
                                   v
        +-------------------------------------------------------+
        |  UiSurfaceVisibility.IsVisible(surface, mode) : bool   |   <- pure, Unity-free, unit-tested
        +-------------------------------------------------------+
             |            |             |               |
             v            v             v               v
      main-window     window tabs   settings        window
        buttons                     sections      auto-close
     (ParsekUI)     (Recordings,   (SettingsUI)   (mode-change
                     Career,                        handler)
                     Kerbals)

   Invariant: the gate feeds DRAW paths and the mode-change close handler ONLY.
              No recorder, playback, dispatch, or ledger path ever reads it.
```

---

## 6. Data Model

### 6.1 New types

`UiComplexityMode` (new file `Source/Parsek/UI/UiComplexityMode.cs`), explicit int values because the value is persisted:

```
Basic    = 0 - reduced surface set, default for fresh Parsek saves
Advanced = 1 - today's full UI, unchanged
```

`UiSurface` (same file), the enumerated gate keys. Explicit int values are NOT required (never persisted, resolved by name at each call site):

```
MainButtonSpawnControl  - Real Spawn Control launcher
MainButtonTimeline      - Timeline launcher
MainButtonRecordings    - Recordings / Missions launcher
MainButtonLogistics     - Logistics launcher
MainButtonKerbals       - Kerbals launcher
MainButtonCareer        - Career launcher
MainButtonGloops        - Gloops Flight Recorder launcher
MainButtonSettings      - Settings launcher
TabRecordings           - the raw per-recording table tab
TabMissions             - the mission abstraction tab
SettingsSectionDiagnostics    - verbose logging, tracing toggles, Test Runner
SettingsSectionSampleDensity  - recorder fidelity tuning
```

`UiSurfaceVisibility` (same file), a pure static class:

```
IsVisible(UiSurface surface, UiComplexityMode mode) : bool
    - the single decision predicate; Advanced returns true for every surface
HiddenSurfaces(UiComplexityMode mode) : IEnumerable<UiSurface>
    - enumeration used by the mode-change close handler and by tests
ResolveDefaultMode(bool saveHasParsekFootprint) : UiComplexityMode
    - first-run default, see section 7.3
```

### 6.2 Changes to existing types

**`ParsekSettings`** (`Source/Parsek/ParsekSettings.cs`):
- `uiComplexityMode: int` - default `0` (Basic). Stored as int to match the existing `samplingDensity` / `autoLoopTimeUnit` convention rather than introducing an enum-typed persisted field.

**`ParsekSettingsPersistence`** (`Source/Parsek/ParsekSettingsPersistence.cs`):
- `UiComplexityModeKey` const, `RecordUiComplexityMode(int)`, plus the stored-value restore branch and the two diagnostic-line entries, following the `showRouteLines` pattern exactly (`ParsekSettingsPersistence.cs:45`, `:247-252`, `:406`).

**`ParsekUI`** (`Source/Parsek/ParsekUI.cs`):
- `OnUiComplexityModeChanged(UiComplexityMode previous, UiComplexityMode next)` - the close handler of section 7.2.
- `:239` button label `"Recordings"` -> `"Missions"` (constant, both modes; no label-helper indirection needed, see section 4.1).

**`RecordingsTableUI`** (`Source/Parsek/UI/RecordingsTableUI.cs`):
- `:124-125` constants swap to `TabMissions = 0`, `TabRecordings = 1`.
- `:126` default becomes `selectedTab = TabMissions`.
- `:127` `TabLabels` becomes `{ "Missions", "Recordings" }` in Advanced and `{ "Missions" }` in Basic (mode-aware; the bar is not drawn at all in Basic, section 7.4).
- `:434` window title `"Parsek - Recordings"` -> `"Parsek - Missions"`.
- NOT changed: `:432` window ID hash, `:445` log key. See section 4.2.

### 6.3 Serialization

The mode is a global (not per-save) preference, persisted through the existing `ParsekSettingsPersistence` store alongside `showRouteLines` and `blockCommittedActions`. No recording schema change, no ledger change, no `.sfs` change beyond the existing settings node. `RecordingStore.CurrentRecordingSchemaGeneration` is untouched.

The Test Runner keeps its Ctrl+Shift+T global shortcut in both modes; only its Settings -> Diagnostics launch button is gated. The shortcut is a developer entry point that must remain available for the automated-testing harness (`PARSEK_AUTORUN_TESTS`), which never opens the Settings window.

---

## 7. Behavior

### 7.1 Drawing

Each gated draw site wraps its existing block in `if (UiSurfaceVisibility.IsVisible(UiSurface.X, mode))`. No draw-site logic changes beyond the wrap. In Advanced the predicate is constant-true, so Advanced output is identical to today.

Separator spacing needs care: `ParsekUI.cs` emits `GUILayout.Space(SpacingLarge)` between button groups. When Basic removes the Kerbals and Career buttons, the separator that followed them must go too, or Basic shows a double gap. The spacing belongs inside the same visibility block as the buttons it separates.

### 7.2 Switching mode

Handled in `SettingsWindowUI` at the toggle site, delegating to `ParsekUI.OnUiComplexityModeChanged`:

1. Write `s.uiComplexityMode`, call `ParsekSettingsPersistence.RecordUiComplexityMode`.
2. Log at Info: `Setting changed: uiComplexityMode=Basic->Advanced`.
3. Advanced -> Basic: for every surface in `HiddenSurfaces(Basic)` that owns a window, set `IsOpen = false` and call its `ReleaseInputLock()`. This matters: `ParsekUI.cs:2049-2050` shows input locks are released explicitly per window, and a window hidden while holding a lock would leave the player unable to click the game world.
4. Basic -> Advanced: nothing to close. Windows reopen on demand with preserved state.

### 7.3 First-run default (DECIDED)

The governing rule: **the mode is a saved preference, and a default must never change what an existing player already sees.** Basic is the default only where there is nothing to change, which is a new install.

Resolution order:

- **Stored value present -> use it, always.** This wins unconditionally. Once the player has a mode, no default logic runs again, in any version.
- No stored value AND no Parsek footprint -> `Basic`. A genuinely new player, nothing to disrupt.
- No stored value AND an existing Parsek footprint -> `Advanced`. An existing player updating into this feature; every window they used stays exactly where it was.

The footprint test reuses the detection concept `PreParsekBackup` already implements: an on-disk `Parsek/` directory or a populated `SCENARIO{name=ParsekScenario}` node. That code treats the on-disk directory and the scenario node as authoritative and the marker file as a fast path only; the mode default follows the same authority order rather than introducing its own.

This is `ResolveDefaultMode(bool saveHasParsekFootprint)`, pure and unit-testable (section 13.1).

The rejected alternative was defaulting Basic unconditionally: one line, but it silently removes four windows from every existing install on update, which reads as a regression rather than a simplification. The requirement is not "make everyone start in Basic", it is "make Basic the starting point for people who have no history to lose".

**Implication for phase 2.** The stored-value branch must be written and tested before the footprint branch, so that an absent key is the only path that can ever reach footprint resolution. A bug that lets footprint logic override a stored value would flip a player's UI on update, which is precisely the failure this decision exists to prevent. Covered by `StoredValueAlwaysWinsOverFootprint` in section 13.1.

### 7.4 Tab-index clamp

`RecordingsTableUI.selectedTab` is transient runtime state (`RecordingsTableUI.cs:120-122`), not persisted, so this is a within-session concern only.

The section 4.1 reorder materially shrinks it. With `TabMissions = 0`, index 0 is valid and means Missions in BOTH the Advanced array (`{ Missions, Recordings }`) and the Basic array (`{ Missions }`). The only out-of-range case left is a player sitting on the Recordings tab (index 1) when Basic is selected, and clamping that to 0 lands on Missions, which is both in range and the semantically right destination.

Had Missions stayed at index 1, every player on the default tab would have needed a clamp on entering Basic, and a naive clamp would have been correct only by coincidence. Putting Missions at index 0 makes the clamp a rare no-op rather than the common path.

Clamp on mode change and defensively on draw; log the clamp at Verbose with old index, new index, and active tab count. Entering Advanced needs no clamp, since every Basic index is valid in the larger array.

In Basic the tab bar renders zero tabs rather than a single one-button toolbar. `GUILayout.Toolbar` with one entry is visual noise; the window title (`Parsek - Missions`) carries the identity.

---

## 8. Edge Cases

1. **Window open when Basic is selected.** Force-closed and its input lock released (section 7.2). State preserved for the next Advanced session.
2. **Input lock leak.** A gated window that held a lock at hide time would soft-lock the player's mouse. `ReleaseInputLock()` is mandatory in the close handler, and is the highest-risk defect in this feature.
3. **`selectedTab` out of range.** Only reachable from the Recordings tab (index 1) when Basic is selected. Clamped to 0 (Missions) on mode change and defensively on draw (section 7.4).
3a. **Window position reset by the rename.** If the window ID hash at `RecordingsTableUI.cs:432` is changed along with the title, every player's saved window position and size for this window is silently discarded. The ID is deliberately excluded from the rename (section 4.2). A test cannot easily catch this; it is a review checklist item.
3b. **Timeline "Recordings" filter mistaken for the renamed button.** `TimelineWindowUI.cs:695` is an unrelated entry-type filter that correctly says "Recordings". A global find-and-replace would rename it and break the Timeline's filter labelling. Section 4.2 is the disposition table for every hit.
4. **Contextual window orphaned.** The Log window (`StructureListWindowUI`) can be opened from a Missions row, which stays visible in Basic. It remains reachable and is not gated. The Group picker is reachable only from the hidden Recordings tab and so is implicitly unreachable in Basic; it needs no explicit rule because it is modal and opened on demand.
5. **Route candidate banner in Basic.** Kept. It carries `Open Logistics` and `Dismiss`, both of which target a surface Basic keeps.
6. **Data Management destructive actions.** Kept in Basic per section 4, on the grounds that a player who needs to clear data must be able to. If playtesting shows new players clicking destructive actions by accident, the mitigation is a confirmation prompt, not hiding the section.
7. **Scene differences.** The main window is drawn in FLIGHT, SPACECENTER, and Tracking Station variants. The InFlight-only buttons (Spawn Control, Gloops) are already conditional; the Basic gate composes with that condition (`InFlight && IsVisible(...)`), it does not replace it.
8. **Harness and in-game tests.** Any in-game test that drives a gated window must either force Advanced for its duration or assert against the mode. Tests must not assume the Advanced set is present. See section 15.
9. **Mode changed while a gated window is mid-drag / mid-resize.** The close handler runs on the Settings toggle click, which cannot coincide with a drag on another window. No special handling, but the close path must not assume the window was idle.
10. **Toolbar button in Basic.** Unchanged. The launcher is not gated; only the main window's contents are.

---

## 9. What Doesn't Change

The visibility-only invariant (philosophy 1) protects all of the following. None of them read `uiComplexityMode`:

- Recording: `FlightRecorder`, auto-record on launch / EVA / first modification.
- Playback: `GhostPlaybackEngine`, ghost spawning, loop scheduling, map rendering.
- Logistics: route dispatch, delivery, hold and block states, candidate derivation.
- Ledger and career state: `LedgerOrchestrator`, recalculation, `KspStatePatcher`.
- Rewind: `RewindInvoker`, merge journal, supersede.
- Kerbal stand-in mechanics: reservations, retirement, displacement.
- The recording schema, sidecar layout, and `.sfs` contents beyond the settings node.
- Advanced-mode rendering, which stays byte-identical to today.

A grep gate is proposed in section 15 to enforce that the mode symbol appears only in UI draw paths.

---

## 10. Out of Scope

- Restyling or relayout of any window. Section 17 proposes improvements; each ships separately.
- A per-window "show this window" checklist. Two named modes are the requested feature; a bespoke picker is more configuration, not less.
- **Conditional "appear once used" visibility for Logistics** (show the button only after a route or candidate exists, the pattern Real Spawn Control uses for its InFlight / zero-candidate gating). Considered and REJECTED: it inverts philosophy 7. A player who has never made a supply route is exactly the player who needs to see that supply routes exist; a button that appears only after you found the feature cannot introduce you to it. Real Spawn Control can use that pattern because it is an Advanced-only surface whose audience already knows what it does. Do not revive this for Logistics without revisiting philosophy 7 first.
- Per-save mode. The mode is a player preference, not save state.
- A first-run onboarding panel (section 17.4), which is a separate feature with its own design.
- Any third mode.

---

## 11. Backward Compatibility

No recording-schema or ledger change. `CurrentRecordingFormatVersion` and `CurrentRecordingSchemaGeneration` are untouched. A save written by a build with this feature loads on a build without it (the unknown settings key is ignored), and vice versa (the missing key resolves through `ResolveDefaultMode`). No migration path is needed.

---

## 12. Diagnostic Logging

### 12.1 Subsystem tags

| Tag | Owns |
|-----|------|
| `[UI]` | mode changes, gated-surface skips, window auto-close on mode change |

The existing `[UI]` tag is correct here; this feature introduces no new subsystem.

### 12.2 Logged events

| Event | Level | When | Context |
|-------|-------|------|---------|
| Mode changed | Info | Settings toggle click | `previous->next`, resolved default source |
| First-run default resolved | Info | Settings restore, no stored value | `saveHasParsekFootprint`, chosen mode |
| Window auto-closed on mode change | Verbose | Advanced -> Basic, per closed window | window name, whether it held an input lock |
| Tab index clamped | Verbose | Mode change or draw | old index, new index, active tab count |
| Gated surface skipped | (none) | per draw | Deliberately NOT logged: this is a per-frame path and would spam. The mode-change line is sufficient to reconstruct which surfaces were hidden. |

---

## 13. Test Plan

### 13.1 Unit tests (`Source/Parsek.Tests`)

- **`AdvancedShowsEverySurface`** - `IsVisible(s, Advanced)` is true for every `UiSurface` value, walked by reflection over the enum. Fails if a new surface defaults to hidden in Advanced.
- **`BasicHidesExactlyTheDocumentedSet`** - `HiddenSurfaces(Basic)` equals the section 4 set exactly. Fails on silent scope drift in either direction.
- **`BasicKeepsCoreLoopSurfaces`** - Timeline, Logistics, Settings, and TabMissions are visible in Basic. This is the philosophy-3 guard.
- **`ResolveDefaultModeUsesFootprint`** - footprint true -> Advanced, false -> Basic.
- **`StoredValueAlwaysWinsOverFootprint`** - a stored mode is returned unchanged for BOTH footprint values, including the stored-Advanced-on-a-fresh-save and stored-Basic-on-an-existing-save crossings. This is the section 7.3 guard: it fails if footprint logic is ever allowed to override a saved preference, which would flip a player's UI on update.
- **`EverySurfaceIsDecided`** - reflection walk asserting `IsVisible` has an explicit case per enum value, so adding a `UiSurface` without a Basic decision fails the build rather than defaulting silently.
- **`TabIndexClampsIntoRange`** - clamp helper over the Basic and Advanced tab arrays, including the index-1-into-Basic case.
- **`MissionsIsTheDefaultAndFirstTab`** - asserts `TabMissions == 0`, that `selectedTab` initializes to it, and that `TabLabels[0]` is "Missions" in both the Basic and Advanced arrays. Fails if a future edit reorders the tabs back, which would silently restore the Recordings tab as the landing view and re-widen the clamp case of section 7.4.
- **`RecordingStoragePathsAreUnaffectedByRename`** - asserts `RecordingPaths` still resolves the `Parsek/Recordings` directory. Guards the section 4.2 trap where an over-eager rename orphans every recording on disk.

### 13.2 Log-assertion tests

- **`ModeChangeLogsTransition`** - captures via `ParsekLog.TestSinkForTesting`, asserts the `[UI]` line contains both old and new mode. Catches silent removal of the transition diagnostic.
- **`AutoCloseLogsPerWindow`** - asserts one Verbose line per closed window on Advanced -> Basic.

Both classes need `[Collection("Sequential")]` per the `ParsekLog` static-state rule.

### 13.3 In-game tests (`InGameTests/`)

New category `UiComplexityMode`:
- **`BasicModeReleasesInputLocks`** - open every gated window in Advanced, switch to Basic, assert no Parsek input lock remains held. This is the edge-case-2 regression guard and the most valuable test in the set.
- **`ModeRoundTripPreservesWindowState`** - set state in a gated window, round-trip Basic and back, assert state survived.
- **`AdvancedRenderParityAfterRoundTrip`** - assert the Advanced main-window control count is unchanged after a Basic round trip.

Existing in-game tests that drive gated windows must force Advanced for their duration (edge case 8). That audit is part of the implementation, not a follow-up.

### 13.4 Grep gate

`scripts/grep-audit-ui-complexity-mode.ps1`, run from an xUnit test in the style of `GrepAuditTests`: assert `uiComplexityMode` and `UiSurfaceVisibility` appear only in `Source/Parsek/UI/**`, `ParsekUI.cs`, `ParsekSettings*.cs`, and tests. Enforces the section 9 invariant mechanically, so a future change cannot quietly make recording or dispatch mode-dependent.

---

## 14. Implementation Phasing

| Phase | Scope | Notes |
|-------|-------|-------|
| 1 | Recordings -> Missions rename + tab reorder (section 4.1) | Independent of the mode gate and applies to BOTH modes, so it lands first and alone. User-visible: needs a CHANGELOG entry in its own commit. Verify the section 4.2 must-not-rename table before committing. |
| 2 | `UiComplexityMode.cs`: enum, surfaces, pure `UiSurfaceVisibility` + unit tests | No behavior change; pure core lands green before any draw site moves |
| 3 | `ParsekSettings` field + persistence + Settings toggle UI | Toggle visible, gates not yet wired; mode persists across restart. Stored-value branch written and tested BEFORE the footprint branch (section 7.3). |
| 4 | Main-window button gates + separator spacing | The visible payoff, four buttons in Basic |
| 5 | Tab-bar gate in Basic + tab clamp | Smaller than it was pre-rename: phase 1 already put Missions at index 0 |
| 6 | Settings section gates (Diagnostics, Sample Density) | Test Runner Ctrl+Shift+T shortcut stays live |
| 7 | Mode-change close handler + input-lock release + in-game tests | Edge case 2, the highest-risk item |
| 8 | Grep gate + doc updates (CHANGELOG, todo, this doc status) | |

Phase 1 is deliberately first and standalone. It is the only phase whose effect is visible to existing Advanced users, so isolating it in one commit keeps it revertable without unwinding any of the mode work, and keeps its CHANGELOG entry honest about who is affected.

New source files: `Source/Parsek/UI/UiComplexityMode.cs`, `Source/Parsek.Tests/UiComplexityModeTests.cs`, `scripts/grep-audit-ui-complexity-mode.ps1`.

---

## 15. Open Questions

### 15.1 Timeline tier filters

The Timeline has 10 toggles, several being tier filters. Whether those should collapse to a single dropdown in Basic is a within-window simplification, deliberately deferred out of v1 (which gates whole surfaces only). Flagged because Timeline is the window a Basic player uses most.

---

## 16. Code Layout

| Concept | Code |
|---------|------|
| Mode enum + surfaces + predicate | `Source/Parsek/UI/UiComplexityMode.cs` (new) |
| Persisted value | `ParsekSettings.uiComplexityMode`, `ParsekSettingsPersistence` |
| Toggle UI | `UI/SettingsWindowUI.cs` (new `Interface` section, drawn first) |
| Main-window gates | `ParsekUI.cs:193-380` |
| Tab gates + reorder | `UI/RecordingsTableUI.cs:124-127`, `:1182-1190` |
| Rename (button, title) | `ParsekUI.cs:239`, `UI/RecordingsTableUI.cs:434` |
| Rename exclusions | `UI/RecordingsTableUI.cs:432` (window ID), `:445` (log key), `RecordingPaths.cs` (storage), `UI/TimelineWindowUI.cs:695` (unrelated filter) |
| Mode-change close handler | `ParsekUI.OnUiComplexityModeChanged` |
| Invariant enforcement | `scripts/grep-audit-ui-complexity-mode.ps1` |

---

## 17. Separate UI Improvement Proposals

Independent of the Basic/Advanced feature. Each would ship as its own change; none is assumed by this design. Ordered by value against the "too complicated" complaint.

### 17.1 Extract shared window chrome (highest structural value)

Eight files independently implement the same window scaffolding: `*HasInputLock`, `isResizing*`, `IsOpen`, `ReleaseInputLock`, `IsMouseOverOpenWindow` (`CareerStateWindowUI`, `KerbalsWindowUI`, `LogisticsWindowUI`, `RecordingsTableUI`, `SettingsWindowUI`, `SpawnControlUI`, `TestRunnerUI`, `TimelineWindowUI`). A shared `ParsekWindowBase` would remove eight copies of the same lifecycle and, directly relevant here, give the Basic gate and the input-lock release a single choke point instead of eight. Doing this BEFORE phase 6 would materially de-risk edge case 2.

### 17.2 Main window has no visual grouping

Eight buttons in a flat stack with only blank-space separators and no labels. Even in Advanced, three group headers (`Flight`, `History`, `Career`) would make the launcher scannable. Cheap, and it helps Advanced users, who are not served by the mode toggle at all.

### 17.3 No search or filter in the Recordings table

Verified: the only `TextField` uses in `RecordingsTableUI` are rename and loop-period editing. With many recordings the table is a long scroll with sort as the only narrowing tool. A single name-filter field is a small change with a large effect on the window most responsible for the complexity complaint. Note this improves the surface Basic hides, so it mainly benefits Advanced users.

### 17.4 First-run onboarding

A one-time panel stating the three-step loop (fly, commit, rewind) with a "Show advanced features" action that flips the mode. Pairs naturally with this feature and addresses the underlying problem (players do not know what the mod wants them to do) rather than only the symptom (too many buttons). Deferred as its own design.

### 17.5 Toolbar button carries no state

The Logistics button tints red for hard-broken routes, but only once the main window is open. A player with the window closed has no signal. Tinting or badging the ApplicationLauncher icon (`ParsekFlight.cs:1301`, `ParsekKSC.cs:128`) surfaces the error where it can actually be seen. Applies in both modes.

### 17.6 Settings is one long scroll

Seven sections, no folds. The codebase already has caret and foldout helpers in `RecordingsTableUI`. Collapsible sections, with `Interface` and `Recording` open by default, would shorten it considerably. Partly mitigated by this feature (Basic drops two sections), so lower priority.

### 17.7 "Recordings" versus "Missions" naming (RESOLVED, now in scope)

This was raised here as a separate proposal and has since been folded into the feature proper: the rename and tab reorder apply in both modes and ship as phase 1. See section 4.1 for the decision, 4.2 for the strings excluded from it. Retained as a heading so the cross-reference from earlier revisions still resolves.
