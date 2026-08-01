# Parsek Basic / Advanced UI Mode - Design Document

*Design specification for a Settings-level UI complexity mode that hides non-essential Parsek windows and controls behind an Advanced toggle.*

*Parsek is a KSP1 mod for time-rewind mission recording. Players fly missions, commit recordings to a timeline, rewind to earlier points, and see previously recorded missions play back as ghost vessels alongside new ones. This document specifies the UI complexity mode: which surfaces Basic hides, how the gate is implemented, and the visibility-only guarantee.*

**Status:** IMPLEMENTED (phases 1-8 landed on branch `claude/mods-ui-basic-advanced-amrgy9`, in-game validation pending). All blocking decisions are RESOLVED. First-run default (section 7.3): the stored setting always wins, Basic is the default for new installs only, an existing install is never changed. Basic hide-set (section 4): as specified, with Logistics explicitly kept visible for discoverability (philosophy 7); the conditional "appear once used" variant is rejected in section 10. Naming (section 4.1): the main-window button, window title, and first/default tab all become Missions in BOTH modes, the one deliberate Advanced-visible change in this feature; section 4.2 lists the "Recordings" strings that must NOT be renamed.
**Review:** 2026-07-28 four-lens plan review (inventory, sufficiency, risk/invariants, implementation) folded in. Load-bearing changes from that review: install-level footprint + persist-on-resolve + two-input resolve seam (7.3), the Timeline GoTo disposition (4.1a), the frame-latched mode apply rule (7.2), the DrawIfOpen never-gated rule (7.1), the widened close set (7.2), and the Basic v1 limitation on retroactive playback-disable (4.3).
**Version:** 0.4
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
| Fresh install (no Parsek footprint anywhere), first open of the main window | Four buttons: Timeline, Missions, Logistics, Settings |
| Player sets Settings -> Interface -> Advanced | Main window grows to the full eight-button set on the next frame; nothing else changes |
| Player switches back to Basic while the Career window is open | Career window closes, its input lock is released, its state is preserved |
| Player in Basic mode flies, commits, loops, and rewinds a mission | Every step works; no hidden window is required at any point |
| Existing install (any Parsek footprint), first run after update | Stays on Advanced (see section 7.3), so nothing the player used disappears |

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
7. **Basic is a starting point, not a cage.** The goal is a UI a new player can take in at a glance, not the smallest possible UI. A feature the player should eventually discover and use stays visible in Basic even when it is not strictly required, because a button they grow into is how they learn the mod exists. This is what separates a surface that is HIDDEN (a raw table or a developer panel they would never grow into) from one that is merely NOT YET USED (Logistics). It is also why conditional "appear once used" visibility is rejected in section 10: a surface that materializes only after you already found the feature cannot teach you the feature is there.

---

## 3. Full UI Inventory

### 3.1 Main window (`ParsekUI.DrawWindow`, `ParsekUI.cs:193-385`)

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

The Career window's two `Button(` hits are both `Close` (`CareerStateWindowUI.cs:1264`, `:1323`; its tab bar is a `Toolbar`, not counted); the Kerbals window's four are `Close` (`KerbalsWindowUI.cs:326`), two per-kerbal folds (`:399`, `:565`), and a Mission Outcomes row button (`:577`) that cross-links to a Timeline scroll (UI state only). Neither window mutates game or Parsek state. Both are pure reporting surfaces.

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
| Logistics link picker (second top-level IMGUI window) | `UI/LogisticsWindowUI.cs:1707` | Logistics |
| Settings-launched test runner window | `UI/TestRunnerUI.cs` (`ParsekTestRunner`) | Settings -> Diagnostics |
| Global test runner window (separate window, separate lock) | `InGameTests/TestRunnerShortcut.cs:189-197` (`ParsekTestRunnerGlobal`) | Ctrl+Shift+T, any scene |
| Spawn warning (pure text helper; rendered by ParsekFlight) | `SpawnWarningUI.cs` | Spawn flow |
| Merge / Discard dialogs | `MergeDialog.cs` | Scene exit, pre-switch |
| Missions warp-to-window confirm | `UI/MissionsWindowUI.cs:1282` | Missions `Warp to...` |
| Rewind-flow dialogs | `ReFlyRevertDialog.cs:236`, `RewindInvoker.cs:452`, `WarpToTimeController.cs:223` | Timeline rewind flow |
| Seal confirm | `UnfinishedFlightSealHandler.cs:214` | Missions / Timeline Seal |
| Wipe / delete confirms | `ParsekUI.cs:921`, `:954`, `RecordingsTableUI.cs:3979/4022/4058`, `LogisticsWindowUI.cs:2496/2537/2640` | Settings Data Management, Recordings tab, Logistics |
| Save-failed popup | `SceneExitInterceptor.cs:536` | Scene-exit save failure |
| Blocked-action popup | `CommittedActionDialog.cs:31` | Game event, no UI parent |
| Flight-map ghost icon menu | `Patches/GhostVesselLoadPatch.cs:324` (`GhostIconMenu`) | Clicking a ghost icon in the flight map; NO parent window |
| Tracking Station ghost icon menu | `ParsekTrackingStation.cs:1241` (`ParsekTrackingStationGhostMenu`) | Clicking a ghost icon in the TS; NO parent window; includes Materialize |
| Flight map markers | `ParsekUI.DrawMapMarkers` | Map view |
| Tracking Station markers | `ParsekTrackingStation.cs` OnGUI (`:337`) | Tracking Station |

Most of these are reached only through a parent surface or a game-flow event, so gating the parent gates them implicitly. Explicit exceptions with their own rules: the two test runner windows (section 6.3: only the Settings-launched instance is affected, the global Ctrl+Shift+T window is never gated), the Group picker (must be force-closed on mode change since it can be open when Basic is selected, section 7.2), and the two ghost icon menus (parentless; deliberately NOT gated: they are playback surfaces, not complexity surfaces, and hold no input locks). The Timeline `GoTo` cross-link was the one kept-surface-to-hidden-surface link; the section 4.1a revision retargets it at the Missions tab, so no kept surface now links to a hidden one.

---

## 4. Essentiality Analysis

The test applied to each surface: **can a player complete the core loop (fly -> commit -> loop / watch -> rewind -> supply) without it?**

| Surface | Basic | Rationale |
|---------|-------|-----------|
| Timeline | **Keep** | The only access to rewind (`R`), fast-forward (`FF`), and `Warp to time`. Irreplaceable. |
| Logistics | **Keep** | The only surface for supply routes. Broken-route red tint is a player-visible error channel. Kept visible even for a player with zero routes, per philosophy 7: it is the button that teaches them supply routes exist. See section 10 for the rejected conditional-visibility variant. |
| Settings | **Keep** | Hosts the mode toggle itself. Must always be reachable. |
| Missions tab | **Keep** | The player-facing mission abstraction: name, loop period, Watch, Clone, Delete, Archive, include checkboxes, Log. Sufficient for all routine recording management. |
| Recordings tab | **Hide** | The raw per-recording table (62 buttons, 13 toggles). Almost everything a normal player needs is expressed at the Mission level; the one known exception is retroactive per-recording playback-disable, accepted as a v1 limitation in section 4.3. This is the single largest complexity reduction available. |
| Career window | **Hide** | 2 buttons, 2 toggles, zero mutations. Reports contracts / strategies / facilities / milestones that stock screens already show, with a projected column. Pure power-user reference. |
| Kerbals window | **Hide** | 4 buttons, 0 toggles, zero mutations. Reports roster state and per-kerbal mission outcomes. Stand-in mechanics run correctly whether or not the player watches them. Known comprehension gap: the CrewDialogFilter patch silently removes reserved kerbals from stock crew assignment, and this window is the only surface explaining why; a Basic player can always assign someone else, so the task never blocks. |
| Gloops Flight Recorder | **Hide** | Manual ghost-only recording. An explicitly opt-in power feature; the automatic recorder covers the normal path. The mode switch is refused while a Gloops recording is in progress (section 7.2), so hiding the window can never strand a running manual recording. |
| Real Spawn Control | **Hide** | Proximity spawning of nearby recorded vessels. Advanced staging tool, already conditional (InFlight, disabled at zero candidates). Residual loss: it is also the only surface listing when a still-playing ghost becomes a real craft (`SelectiveSpawnUI.cs:35-42`); spawn-at-end itself stays automatic, so no capability is lost, only the countdown/warp convenience. |
| Settings: Diagnostics | **Hide** | Verbose logging, ghost/map/ledger render tracing, Test Runner. Developer instrumentation; the tracing toggles warn about huge logs. Also hides the RewindPoints disk-usage readout (`SettingsWindowUI.cs:525-531`), a power-user report, not developer instrumentation; accepted. |
| Settings: Sample Density | **Hide** | Recorder fidelity tuning. A wrong value degrades recordings; the Medium default is correct for normal play. |
| Settings: Data Management | **Keep** | Includes the pre-Parsek backup control and destructive data actions the player may legitimately need. Keep, but see section 8 edge case 6. |

**Result.** Basic shows four main-window buttons (Timeline, Missions, Logistics, Settings) instead of eight, one tab instead of two in the Missions window, and six Settings sections instead of eight (this feature itself adds the always-visible `Interface` section that hosts the toggle, making eight total in Advanced; Basic hides Diagnostics and Sample Density).

### 4.1 Naming and tab order (DECIDED, applies to BOTH modes)

Today a button named `Recordings` opens a window titled `Parsek - Recordings` whose first tab is `Recordings` and whose second tab, `Missions`, holds the abstraction players actually work with. In Basic, where the Recordings tab is hidden, that naming would be actively wrong. Making the label mode-dependent would be worse: the same button would carry two names depending on a setting.

**Decision.** Missions becomes the primary identity of this window in both modes:

- The main-window button reads **`Missions`** (`ParsekUI.cs:239`).
- The window title reads **`Parsek - Missions`** (`RecordingsTableUI.cs:435`).
- The tab order becomes **`{ "Missions", "Recordings" }`**, so Missions is both first and the default selection (`RecordingsTableUI.cs:127`).
- The tab constants swap to `TabMissions = 0`, `TabRecordings = 1`, and the default becomes `selectedTab = TabMissions` (`RecordingsTableUI.cs:124-126`).
- `RecordingsTableUI.ScrollToRecording` gains an explicit `selectedTab = TabRecordings` (see section 4.1a; it worked before the reorder only because Recordings happened to be the default tab). Superseded by the section 4.1a revision: the method still does this, but has no production caller.
- The two Timeline GoTo tooltips `"Show in Recordings Manager"` (`TimelineWindowUI.cs:1193`, `:1223`) become `"Show in Recordings tab"`. Superseded by the section 4.1a revision: the target is now the Missions tab and the tooltip reads `"Show this recording's mission"`.

Because the label no longer varies by mode, the planned `GetRecordingsMainButtonLabel(mode)` helper is unnecessary; a constant label is correct, and the `GetKerbalsMainButtonLabel` indirection pattern is not needed here.

**This is safe to reorder.** `selectedTab` is explicitly transient and not persisted (`RecordingsTableUI.cs:120-122`, "Transient (not persisted), matching the Kerbals / Career State tab idiom"). No player has a stored tab index that this reorder could flip, so there is no migration concern.

The dispatch at `RecordingsTableUI.cs:1190` is keyed on the named constants (`if (selectedTab == TabMissions)`), not on literal ints, so swapping the constant values carries it automatically. Verified at review time: `TabRecordings` / `TabMissions` / `TabLabels` / `selectedTab` have zero consumers outside `RecordingsTableUI.cs` (CareerState / Kerbals have their own private homonyms), and nothing externally selects a tab. Re-verify at implementation time.

### 4.1a Timeline GoTo cross-link (NEW, from review; REVISED, see below)

Timeline rows carry a `GoTo` button that navigates from a timeline row to the flight it belongs to. Two `RecordingStart` / separation row flavours carry it (`TimelineWindowUI.cs`, both inside `DrawEntryRow`).

**Original disposition (shipped in phases 1 and 5).** GoTo called `RecordingsTableUI.ScrollToRecording`, which targets the raw **Recordings tab**. Two obligations followed:

- **Phase 1 (both modes):** `ScrollToRecording` had to set `selectedTab = TabRecordings` explicitly, or the tab reorder alone would regress GoTo in Advanced (window opens on Missions, the scroll silently never lands, the pending id dangles).
- **Basic:** the GoTo button was gated by `IsVisible(UiSurface.TabRecordings, mode)` - a control whose sole purpose is navigating to a hidden surface is gated by the target surface's key, not its host window's. Hiding the button also prevented the unhide/HideActive side effects from firing invisibly.

**Revised disposition (current).** Hiding the button left Basic with no way at all to get from a timeline row to its flight, which is a core-loop action, not an advanced one. GoTo now targets the **Missions tab** - the surface Basic keeps - so the button is present and useful in BOTH modes and no gate hides it:

- `TimelineWindowUI` calls `RecordingsTableUI.ShowMissionForRecording(recordingId)`, which force-opens the window, sets `selectedTab = TabMissions`, and delegates to `MissionsWindowUI.RevealMissionForRecording`.
- Resolution is recording -> `Recording.TreeId` -> `MissionStore.FindOriginalMission(treeId)`. A tree may carry several missions (clones); the original is the deterministic, name-linked one (the same pick `MissionGroupLink` makes).
- The reveal schedules a scroll through the same two-frame handshake the recordings tab uses: the target mission's header row is located during a Repaint pass, and the scroll is applied at the top of a later pass, before `BeginScrollView` (writing `scrollPos` afterwards is a no-op for that frame). The scroll target is the DIFFERENCE between the target header's y and the first header's y, so it is a distance between two rects rather than a raw layout coordinate.
- Only the **global** `MissionStore.HideArchived` filter is cleared, and only when it is what would hide the target. `Mission.Archived` is a player decision this navigation never overwrites. This mirrors the `HideActive` half of the old `ScrollToRecording` precedent, and is reversible with one click on the tab's own Archive checkbox.
- Every failure (recording not committed, no `TreeId`, tree not yet seeded with a mission, target never drawn) warns and lands the player on the tab unscrolled. A pending target is never left armed to fire on an unrelated later frame.
- The gate is KEPT, re-keyed to `IsVisible(UiSurface.TabMissions, mode)`. It hides nothing today (Missions is visible in both modes); it exists so that re-pointing GoTo at a hidden surface would automatically hide the button again instead of stranding the player. The "gated by the TARGET surface's key" idiom is unchanged. No new `UiSurface` value is needed.
- Tooltip: `"Show this recording's mission"`.
- `ScrollToRecording` is retained as the Recordings tab's own navigation API but has **no production caller**; the tab-clamp tests use it as their lever for selecting `TabRecordings`.

### 4.2 Strings that must NOT be renamed

"Recordings" appears in several places that are not this button. Renaming any of them is a defect, not a follow-through:

| Site | Why it must not change |
|------|------------------------|
| `"ParsekRecordings".GetHashCode()` (`RecordingsTableUI.cs:432`) | The IMGUI window ID. Changing it gives the window a new identity, resetting its saved position and size and risking an ID collision with another window. |
| `Path.Combine("Parsek", "Recordings", ...)` (`RecordingPaths.cs:16-50`, also `:55`, `:98`; `RecordingStore.OrphanCleanup.cs:275`; `Analyzer/Rules/Inv7bAnnotationStale.cs:38`; ~20 Parsek.Tests sites; `InGameTests/RuntimeTests.cs:8232/8273`; `scripts/collect-logs.py:369/376`; `harness/run.py:1279`; `harness/lib/_fake_ksp.py:169`; `harness/lib/test_run_smoke.py:154`) | The on-disk sidecar directory. Renaming it orphans every existing recording. |
| ALL diagnostic log text containing "Recordings": `LogWindowPosition("Recordings", ...)` (`RecordingsTableUI.cs:445`), `"Recordings window toggled"` (`ParsekUI.cs:500`), resize/log keys (`RecordingsTableUI.cs:423`, `:1109`, `:1130`), tab-switch log `"Recordings window: tab switched"` (`:1137-1141`, asserted verbatim by `RecordingsTableUITests.cs:2180-2185`) | Diagnostic log keys. Changing any breaks grep continuity with every historical KSP.log and collected log snapshot; the tab-switch string additionally has a unit test asserting it. Blanket rule: log strings keep "Recordings". |
| `GUILayout.Toggle(showRecordingEntries, "Recordings", ...)` (`TimelineWindowUI.cs:695`) | A Timeline entry-type filter, an unrelated feature that genuinely filters recording rows. Correctly named. |
| `"Wipe All Recordings ({N})"` button (`SettingsWindowUI.cs:581`) and its confirm popup `"Confirm: Wipe Recordings"` / dialog id `"ParsekWipeRecordingsConfirm"` (`ParsekUI.cs:927`, `:925`) | These name recordings-as-data (the on-disk artifacts being wiped), like the Timeline filter, not the window. Keep; the section Basic retains (Data Management) therefore legitimately still says "Recordings". |

Exactly four user-facing strings change: the button at `ParsekUI.cs:239`, the window title at `RecordingsTableUI.cs:435`, and the two GoTo tooltips (section 4.1a; the revision there changed them again, to `"Show this recording's mission"`), plus the tab array and constants. A grep for `"Recordings"` will surface all of the above; the table is the disposition for each hit.

### 4.3 Known Basic v1 limitation: retroactive playback-disable (from review)

The only UI writing `Recording.PlaybackEnabled` is the hidden Recordings tab (per-row toggle `RecordingsTableUI.cs:1459`, select-all `:942`, group aggregates `:2030`, `:2651`, chain block `:3645`). The Missions tab deliberately has no per-row enable ("Blank enable slot", `MissionsWindowUI.cs:619-621`); its include checkboxes write `Mission.ExcludedIntervalKeys`, consumed only by the loop-unit pipeline, while non-loop ghost playback, KSC showcase, and map presence gate on `PlaybackEnabled`. Mission Archive explicitly leaves loop/ghost state untouched (`MissionsWindowUI.cs:378-381`), and mission Delete is view-only and disabled for a tree's last mission.

Consequence: "I committed this flight and later want its ghost gone" is Advanced-only in v1. Pre-commit regret is fully covered in Basic by the Merge dialog's Discard. This is a deliberate, documented exception to philosophy 3, accepted because fixing it inside this feature would require a new Missions-tab control in both modes, violating philosophy 6 (no other Advanced-visible change). The follow-up is section 17.8 (mission-level ghost-visibility toggle). Related: debris recordings never become mission legs (`MissionStructure.cs:139` - debris rides its parent), so individual debris enable/hide is likewise Advanced-only; acceptable because debris follows its parent's loop inclusion.

Not to be confused with `Recording.Hidden`, the OTHER Recordings-tab-only flag, which is a different axis and is NOT a v1 limitation: it is decided in section 4.4.

### 4.4 What `Recording.Hidden` (Archive) means, and the Timeline's reveal (DECIDED 2026-08-01)

Found by the same 2026-07-31 audit as the 4.1a revision, and left open there because it needed a decision rather than a fix.

**The gap.** `Recording.Hidden` suppressed Timeline rows unconditionally (`Timeline/TimelineBuilder.cs`, `if (rec.Hidden) { hiddenSkipped++; continue; }`), but every writer of the flag lives in the Recordings tab (the per-row Archive checkbox `RecordingsTableUI.cs:1897`, the group aggregates `:2594` / `:2934`), which Basic hides. Archive a recording in Advanced, switch to Basic, and the flight was gone from the Timeline with no reachable control able to bring it back. It still appeared in the Missions tab, which does not filter on the flag.

**The decision.** State the flag's meaning first, because both candidate fixes presuppose an answer:

> **Archive is a per-list view filter, never a suppression. Every list that honours `Recording.Hidden` carries its own control to show archived items again.**

That is already how the flag behaves everywhere except the Timeline. The Recordings tab honours it and pairs it with the Archive header checkbox (`GroupHierarchyStore.HideActive`, persisted, default on). The mission-level twin `Mission.Archived` honours it in the Missions tab and pairs it with that tab's own Archive checkbox (`MissionStore.HideArchived`). The Timeline was the single list consuming the flag with no paired reveal - a defect that predates Basic mode; Basic only made it terminal, by hiding the one surface that could undo it.

**The fix.** The Timeline gets an `Archived` toggle in its filter bar, second row, column 4 (directly under `Recordings`, the source toggle it qualifies), in BOTH modes:

- `TimelineBuilder.Build` takes `bool includeArchivedRecordings` (default false, so every existing caller and the whole default row set are unchanged). The builder stays pure and reads no store; the window supplies the value.
- The toggle is bound to the SAME state the Recordings tab's Archive header already owns, through `TimelineWindowUI.ShowArchivedRecordings` (`= !GroupHierarchyStore.HideActive`; the polarity flips because the Recordings-tab label means "hide archived" while every Timeline filter toggle means "show this"). One archive flag, one filter switch, two places to reach it. A Timeline-private second flag was rejected: it would give one flag two switches that could disagree.
- Because that state is shared, the Recordings tab can move it while the Timeline's cache is warm and nothing marks the cache dirty. `TimelineWindowUI.ShouldRebuildTimeline(dirty, cacheMissing, cachedShowedArchived, showArchivedNow)` is the pure predicate that adds the missing trigger.
- Revealed rows are marked `[archived]`, composed into the row's existing single description `Label` (never a second control, so the IMGUI control count is identical in the Layout and Repaint passes). Without the marker the player can see the rows are back but not which ones were archived, and so cannot tell what to un-archive.
- Entries carry `TimelineEntry.IsArchivedRecording`, stamped by the collector over the entry range one recording contributed, rather than threaded through four `Try*Add` signatures.

**Nothing here reads the mode.** The Timeline's row set is identical in Basic and Advanced; the mode symbol does not appear in `Source/Parsek/Timeline/` and the section 13.4 grep gate's allowlist is untouched. Basic reaches the toggle because the Timeline is a surface Basic keeps, not because the gate treats it specially.

**Rejected: have the Timeline ignore `Hidden` in Basic** (audit candidate a). Four reasons, any one sufficient:

- It inverts the mental model. Every other mode difference is Basic is a subset of Advanced; this would make Basic show rows Advanced does not, so switching Basic -> Advanced would silently delete Timeline rows - the exact "a default changed what the player sees" failure section 7.3 exists to prevent.
- It crosses the section 5 / 9.1 line. The mode may decide what a message SAYS, never "whether it fires, what is detected". A per-row inclusion decision inside `TimelineBuilder` is detection.
- It would force `Source/Parsek/Timeline/` onto the 13.4 grep-gate allowlist, weakening the mechanism that keeps section 9 honest, in order to make a data filter mode-dependent - precisely the rot that gate exists to catch.
- It gives the player nothing. The recording stays archived; the mode merely masks the flag, and the player still has no control over it.

**Rejected: an un-archive affordance in the Missions tab** (audit candidate b, as literally written). It builds a second, mission-level archive control beside the `Mission.Archived` one that already exists, which is section 17.8's design space (mission vs row granularity, does debris follow the parent, how it composes with the include checkboxes) - much larger than this gap, and a new both-modes control with open questions. The shipped fix IS a Basic-reachable restore affordance; it is placed on the surface where the loss is felt and bound to the state that already exists.

**Precedent this is consistent with.** §7.33 already refuses to archive an Unfinished Flight (`RecordingsTableUI.cs:1911`, "rewind access must remain visible"), because archiving one would sweep a re-fly opportunity out of view. The codebase therefore already treats "an archive made something unreachable" as a bug class, and already solves it by preserving reachability - not by making the filter mode-dependent.

**Known residual, deliberately not widened here.** The archive filter has always been partial: it gates the four row flavours the recording collector emits (RecordingStart, Separation / UnfinishedFlightSeparation, VesselSpawn, CrewDeath), while the same flight's ledger action rows and legacy event rows come from collectors that never read the flag. Revealing is therefore additive and honest, but archiving still leaves a flight's career actions on the Timeline. Making the flag reach the action collectors is a scope-and-semantics question of its own (it would change what Advanced shows for every archived flight) and is not part of this decision.

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

   Invariant: the gate feeds LAUNCHER/CONTENT draw sites, the mode-change close
              handler, and player-facing TEXT that names a gated surface (9.1)
              ONLY. No recorder, playback, dispatch, or ledger path ever reads it,
              and the text case may decide only what a message SAYS, never whether
              it fires or what any action does. It never decides which DATA a kept
              surface shows either: the fix for a Recordings-tab-only flag reaching
              a Basic-kept list is a reachable control, not a mode-dependent filter
              (4.4). The per-window DrawIfOpen call sites are NEVER gated
              (section 7.1) - their !IsOpen -> ReleaseInputLock() prologue is the
              per-frame lock self-heal and must keep running in both modes.
```

---

## 6. Data Model

### 6.1 New types

`UiComplexityMode` (new file `Source/Parsek/UI/UiComplexityMode.cs`), explicit int values because the value is persisted:

```
Basic    = 0 - reduced surface set, default for new installs only (section 7.3)
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
    - the single decision predicate; Advanced returns true for every surface.
      Contract: an unhandled UiSurface value THROWS (no silent default), so the
      EverySurfaceIsDecided reflection test can fail on an undecided addition.
HiddenSurfaces(UiComplexityMode mode) : IEnumerable<UiSurface>
    - enumeration used by the mode-change close handler and by tests
ResolveMode(int? storedValue, bool installHasParsekFootprint) : UiComplexityMode
    - first-run default, see section 7.3. Takes the STORED VALUE as an input so
      that stored-vs-footprint precedence is expressed inside the pure seam and
      StoredValueAlwaysWinsOverFootprint can actually fail if it inverts.
```

The Timeline GoTo button (section 4.1a) reuses `UiSurface.TabMissions` as its gate key; no dedicated surface value exists for it. That key is visible in both modes, so the gate hides nothing today - it is what makes "GoTo can never point at a hidden destination" mechanical rather than a comment.

### 6.2 Changes to existing types

**`ParsekSettings`** (`Source/Parsek/ParsekSettings.cs`):
- `uiComplexityMode: int` - stored as int to match the existing `samplingDensity` / `autoLoopTimeUnit` convention rather than introducing an enum-typed persisted field. The RAW field default is `Advanced` (fail-open): the default is nearly irrelevant because `ApplyTo` always resolves the effective value (stored key, else first-run resolution), and if any path reads the field before a restore, showing everything is the safe wrong answer. No `CustomParameterUI` attribute: the stock difficulty screen would be a second writer bypassing the setter seam.
- A clamping typed accessor following the `SamplingDensity` precedent (`ParsekSettings.cs:108-114`): an out-of-range stored int resolves to **Advanced** (fail-open: showing everything is the safe wrong answer; hiding windows is not).

**`ParsekSettingsPersistence`** (`Source/Parsek/ParsekSettingsPersistence.cs`), following the full `showRouteLines`-analog wiring, adjusted for int (showRouteLines is bool via `TryLoadBool` `:159-171`; the int path is `ParseStoredInt` `:173-180`):
- `UiComplexityModeKey` const (`:45` area) and `RecordUiComplexityMode(int)`.
- The `LoadIfNeeded` parse call, the stored-value restore branch in `ApplyTo` (`:246-253` shape), the `Save()` AddValue branch (`:382-383` shape), and BOTH diagnostic lines (load-side `:142` shape, save-side `:406` shape).
- `ResetForTesting` (`:426`) and the `GetStored...` / `SetStored...ForTesting` seams (`:447`, `:494`) that every other persisted setting carries; section 13.1's tests need them.
- A new `HasAnyStoredValue()` query (or equivalent settings-file-exists check): footprint signal 3 of section 7.3 needs "does the store contain any stored keys", which no current member exposes.

**Single setter seam (from review).** ALL mode writes route through one entry point, `ParsekUI.SetUiComplexityMode(UiComplexityMode next)` (or an equivalent static seam reachable from SettingsWindowUI and tests), which performs: settings write + `RecordUiComplexityMode` + scheduling the deferred apply of section 7.2 (close handler + clamp). Rationale: window BODIES are deliberately not gated, so any writer that bypasses the seam (an in-game test writing `ParsekSettings.Current.uiComplexityMode` directly, or a future Settings "Defaults" button) would leave hidden windows open in Basic. The 13.3 in-game tests MUST call the seam, not the field, or they test nothing.

**`ParsekUI`** (`Source/Parsek/ParsekUI.cs`):
- `OnUiComplexityModeChanged(UiComplexityMode previous, UiComplexityMode next)` - the close handler of section 7.2, invoked from the deferred apply, never mid-OnGUI.
- `:239` button label `"Recordings"` -> `"Missions"` (constant, both modes; no label-helper indirection needed, see section 4.1).

**`RecordingsTableUI`** (`Source/Parsek/UI/RecordingsTableUI.cs`):
- `:124-125` constants swap to `TabMissions = 0`, `TabRecordings = 1`, made `internal` (with `TabLabels`) so 13.1's tests can assert them.
- `:126` default becomes `selectedTab = TabMissions`.
- `:127` `TabLabels` stays the single two-entry array, reordered to `{ "Missions", "Recordings" }`. There is NO separate Basic array: in Basic the toolbar is simply not drawn (section 7.4). A pure `internal static int VisibleTabCount(UiComplexityMode mode)` (or equivalent) is the testable seam for the zero-toolbar rule.
- `:435` window title `"Parsek - Recordings"` -> `"Parsek - Missions"`.
- `ScrollToRecording` sets `selectedTab = TabRecordings` (section 4.1a, a phase 1 obligation). Its production caller is gone after the 4.1a revision; `ShowMissionForRecording` sets `TabMissions` instead, which is Basic-valid by construction.
- NOT changed: `:432` window ID hash, `:445` log key. See section 4.2.

### 6.3 Serialization

The mode is a global (not per-save) preference, persisted through the existing `ParsekSettingsPersistence` store alongside `showRouteLines` and `blockCommittedActions`. No recording schema change, no ledger change, no `.sfs` change beyond the existing settings node. `RecordingStore.CurrentRecordingSchemaGeneration` is untouched.

There are TWO test runner windows, not one with two entry points (review correction): the Settings-launched `ParsekTestRunner` (`UI/TestRunnerUI.cs`, opened via `SettingsWindowUI.cs:508-512` -> `ParsekUI.ToggleTestRunner`) and the DDOL global `ParsekTestRunnerGlobal` (`TestRunnerShortcut.cs:189-197`, Ctrl+Shift+T), with separate locks. Basic gates only the former's launcher and force-closes an open instance on mode change (section 7.2); the global window and its shortcut are never gated in either mode. The shortcut is a developer entry point that must remain available for the automated-testing harness (`PARSEK_AUTORUN_TESTS`), which never opens the Settings window.

---

## 7. Behavior

### 7.1 Drawing

Each gated draw site wraps its existing block in `if (UiSurfaceVisibility.IsVisible(UiSurface.X, mode))`, where `mode` is the FRAME-LATCHED applied mode of section 7.2, never a raw read of the settings field. No draw-site logic changes beyond the wrap. In Advanced the predicate is constant-true, so Advanced output is identical to today.

**What is gated: launcher buttons, the tab bar + tab-content dispatch, settings sections, the Timeline GoTo button (4.1a), and player-facing text that names a gated surface (section 9.1). What is NEVER gated: the per-window `Draw*WindowIfOpen` / `DrawIfOpen` call sites** (`ParsekFlight.cs:2058-2067`, `ParsekKSC.cs:225-232`). Every window's `DrawIfOpen` begins `if (!IsOpen) { ReleaseInputLock(); return; }` and also releases on mouse-leave; this per-frame prologue is the self-heal that makes the 7.2 close path leak-proof in any ordering. Wrapping those calls in `IsVisible(...)` would silently delete the safety net and convert any missed release into a scene-long soft-lock. Cautionary precedent that this mistake is easy to make: `DrawGloopsRecorderWindowIfOpen` already skips `DrawIfOpen` entirely when `!InFlight` (`ParsekUI.cs:1155-1159`), bypassing the `!IsOpen` release path; do not replicate that shape.

Separator spacing needs care: `ParsekUI.cs` emits `GUILayout.Space(SpacingLarge)` between button groups. When Basic removes the Kerbals and Career buttons, the separator that followed them must go too, or Basic shows a double gap. The spacing belongs inside the same visibility block as the buttons it separates.

### 7.2 Switching mode

**Frame-latched apply (from review).** The mode toggle is the first Parsek setting whose value changes IMGUI control counts. Unity IMGUI requires the control count to match between the Layout and Repaint passes of one frame; an immediate mid-event flip (the toggle click lands during an event pass, and Diagnostics + Sample Density draw AFTER the Interface section in the same window callback, `SettingsWindowUI.cs:166-178`) raises `ArgumentException: Getting control N's position in a group with only M controls`. The codebase already defers exactly this class of change: the RouteRunPrompt banner is cleared only on the Layout event "so the same frame's Repaint pass sees the identical control count" (`ParsekUI.cs:253-258`).

Rule: the toggle click calls the setter seam (section 6.2), which writes + persists the setting and records a PENDING mode. The pending mode is APPLIED outside OnGUI (in the controller's `Update()`), which latches the effective mode all `IsVisible` calls read for the whole next frame and runs the close handler. Every gate therefore sees one stable mode per frame; the UI change appears one frame after the click, imperceptibly.

Apply sequence (in `Update()`, not mid-OnGUI):

1. Latch the applied mode; log at Info: `Mode changed: uiComplexityMode=Advanced->Basic`.
2. Advanced -> Basic close set, each wrapped in its own try/catch (`InputLockManager.RemoveControlLock` fires `GameEvents.onInputLocksModified`; a throwing listener must not abort the loop - precedent `RouteCreationDialog.cs:466-480`): for each of `careerStateUI`, `kerbalsUI`, `gloopsUI`, `spawnControlUI`, and the Settings-launched `testRunnerUI`, set `IsOpen = false` and call `ReleaseInputLock()`; also call `groupPicker.Close()` (reachable only from the hidden Recordings tab, but it can already be open at switch time and would otherwise keep drawing from `RecordingsTableUI.DrawIfOpen:448`; existing close precedent `RecordingsTableUI.cs:1102`). Apply the tab clamp (7.4). Log one Verbose line per closed window (window name, whether it held a lock).
3. Basic -> Advanced: nothing to close. Windows reopen on demand with preserved state.
4. BOTH directions: request a one-shot height re-measure of the Settings window (`SettingsWindowUI.RequestHeightRemeasure`), the one window whose own content changes with the mode - Basic drops the Diagnostics + Sample Density sections and Advanced restores them, and its height is fixed with no resize handle, so without this it keeps dead space in Basic and clips in Advanced (playtest 2026-07-28). Only the height is re-derived (x / y / width untouched); the request just sets a flag the next Layout pass consumes by dropping the `GUILayout.Height` option for that single pass, so no control count changes mid-frame. The consume waits for a pass with the bottom tooltip box down (the pointer sits on the mode toggle right after the click, and measuring then would bake in a height that vanishes with the tooltip). The Missions window is deliberately NOT re-measured: its height is player-owned (resize handle) and its tab bar sits above a scroll view that absorbs the freed space.

Notes on the close set:
- It is enumerated from `HiddenSurfaces(Basic)` PLUS the two review additions that do not map 1:1 to a surface: `testRunnerUI` (its launcher lives in the hidden Diagnostics section; without closing it, an open instance has no reopen path in Basic - the Ctrl+Shift+T shortcut opens the SEPARATE global `ParsekTestRunnerGlobal` window, which is never gated) and `groupPicker`.
- `RecordingsTableUI` itself is NOT closed (it survives as the Missions window); `StructureListWindowUI` is NOT closed (reachable from Missions and Logistics rows, both kept).
- Do not copy `ParsekUI.Cleanup()` (`ParsekUI.cs:2047-2053`) as the enumeration source: it omits `gloopsUI`, `logisticsUI`, and `testRunnerUI`. The handler owns its own explicit list, and 13.1 has a test pinning that list to the lock-owning gated windows.
- A missed close self-heals in bounded time: `DrawIfOpen` keeps running ungated (7.1) and releases the lock next frame once `IsOpen` is false, and KSP clears all input locks on scene transition regardless.

**Gloops in-progress guard (from review).** Gloops manual-recording state lives in `ParsekFlight` (`IsGloopsRecording`, driven from `GloopsRecorderUI.cs:199-251`), not in the window; hiding the window does not stop the recording, which would leave it sampling with no reachable Stop/Discard control in Basic. Rule: while `IsGloopsRecording` is true, the Basic option in the Settings toggle is disabled with the inline reason `Stop the Gloops recording first`; the refusal is logged at Info. Zero behavior change, one rare interaction. In SPACECENTER the UI has no `ParsekFlight` (`parentUI.Flight` is null under the `UIMode.KSC` constructor); the check falls back to "not recording", which is sound - Gloops live-recording state dies with the FLIGHT-scene `ParsekFlight`, so it can never be in progress there.

### 7.3 First-run default (DECIDED; mechanism reworked after review)

The governing rule: **the mode is a saved preference, and a default must never change what an existing player already sees.** Basic is the default only where there is nothing to change, which is a new INSTALL. The decision (stored wins; Basic for new installs only; existing installs never changed) is settled; the review found the original per-save mechanism could not deliver it, so the mechanism below is install-level.

Resolution order, expressed by the pure seam `ResolveMode(int? storedValue, bool installHasParsekFootprint)`:

- **Stored value present -> use it, always.** This wins unconditionally. Once the player has a mode, no default logic runs again, in any version.
- No stored value AND no install footprint -> `Basic`. A genuinely new player, nothing to disrupt.
- No stored value AND an install footprint -> `Advanced`. An existing player updating into this feature; every window they used stays exactly where it was.

**The footprint is INSTALL-level, not per-save.** The settings store is per-install (`GameData/Parsek/PluginData/settings.cfg`, `ParsekSettingsPersistence.cs:80-84`), so a per-save footprint would make the resolved default depend on which save happens to load first: an existing player whose first post-update load is a brand-new sandbox would resolve Basic and lose four windows on every veteran save. `installHasParsekFootprint` is true when ANY of:

1. Any `saves/<name>/Parsek/` directory exists (cheap `Directory.Exists` walk over the saves root; no `.sfs` parsing).
2. The CURRENT save's `SCENARIO{name=ParsekScenario}` node is populated (available for free in `ParsekScenario.OnLoad`; reuses `PreParsekBackup`'s authority concept, `PreParsekBackup.cs:29-32`, `:137`).
3. The settings store already contains any stored keys (Parsek ran before this feature existed; the strongest cheap signal).

**The resolved default is persisted immediately.** When resolution runs (no stored value), the result is written back via `RecordUiComplexityMode` in the same step. Without this, resolution re-runs every session, and by session 2 a fresh install HAS a footprint (its own `Parsek/` sidecar dir and populated scenario node), silently flipping the new player from Basic to Advanced - exactly the "default changed what the player sees" failure this section exists to prevent. Persisting on first resolve makes resolution run at most once per install, ever, and makes "stored value present" the steady state.

**When it runs.** Once, at the first settings restore that finds no `uiComplexityMode` key, during a COLD `ParsekScenario.OnLoad` (settings restore runs at `ParsekScenario.cs:2773`; the scenario node is at hand there). Never on warm OnLoads (rewind quickloads, scene changes) - by then the value is stored anyway.

The rejected alternative was defaulting Basic unconditionally: one line, but it silently removes four windows from every existing install on update, which reads as a regression rather than a simplification. The requirement is not "make everyone start in Basic", it is "make Basic the starting point for people who have no history to lose".

**Implication for phase 3.** The stored-value branch must be written and tested before the footprint branch, so that an absent key is the only path that can ever reach footprint resolution. Because the seam takes the stored value as an INPUT, `StoredValueAlwaysWinsOverFootprint` (section 13.1) genuinely fails if the precedence inverts; `ResolutionIsSticky` covers the persist-on-resolve rule.

### 7.4 Tab-index clamp

`RecordingsTableUI.selectedTab` is transient runtime state (`RecordingsTableUI.cs:120-122`), not persisted, so this is a within-session concern only.

The section 4.1 reorder materially shrinks it. With `TabMissions = 0`, index 0 is valid and means Missions in both modes. The only out-of-range case left is a player sitting on the Recordings tab (index 1) when Basic is selected, and clamping that to 0 lands on Missions, which is both in range and the semantically right destination.

Had Missions stayed at index 1, every player on the default tab would have needed a clamp on entering Basic, and a naive clamp would have been correct only by coincidence. Putting Missions at index 0 makes the clamp a rare no-op rather than the common path.

Clamp in the deferred mode-apply step (7.2) and defensively on draw; the on-draw clamp reads the frame-latched mode, so it is deterministic within a frame (same result in Layout and Repaint) and layout-safe. Log the clamp at Verbose with old index, new index, and active tab count. Entering Advanced needs no clamp, since every Basic index is valid.

In Basic the tab bar renders zero tabs rather than a single one-button toolbar - `GUILayout.Toolbar` with one entry is visual noise; the window title (`Parsek - Missions`) carries the identity. `TabLabels` remains the single two-entry array (section 6.2); Basic simply skips drawing the toolbar and pins the dispatch to Missions. `VisibleTabCount(mode)` is the pure, testable expression of this rule.

One more GoTo consequence (4.1a): after the revision, the only production tab mover is `ShowMissionForRecording`, which writes `TabMissions` - the very index Basic clamps TO, so it can never produce a Basic-invalid selection. `ScrollToRecording` (no production caller) is the one that still writes `TabRecordings`; the defensive clamp covers it and any future caller regardless.

---

## 8. Edge Cases

1. **Window open when Basic is selected.** Force-closed and its input lock released (section 7.2). State preserved for the next Advanced session.
2. **Input lock leak.** A gated window that held a lock at hide time would soft-lock the player's mouse. `ReleaseInputLock()` is mandatory in the close handler (per-window try/catch, section 7.2), and is the highest-risk defect in this feature. Two backstops bound the blast radius: the ungated `DrawIfOpen` prologue releases the lock on the next frame once `IsOpen` is false (7.1), and KSP clears all input locks on scene transition. Neither excuses the handler: a leak would still cost the player up to a scene session.
3. **`selectedTab` out of range.** Only reachable from the Recordings tab (index 1) when Basic is selected. Clamped to 0 (Missions) in the deferred mode-apply and defensively on draw (section 7.4).
3a. **Window position reset by the rename.** If the window ID hash at `RecordingsTableUI.cs:432` is changed along with the title, every player's saved window position and size for this window is silently discarded. The ID is deliberately excluded from the rename (section 4.2). A test cannot easily catch this; it is a review checklist item.
3b. **Timeline "Recordings" filter mistaken for the renamed button.** `TimelineWindowUI.cs:695` is an unrelated entry-type filter that correctly says "Recordings". A global find-and-replace would rename it and break the Timeline's filter labelling. Section 4.2 is the disposition table for every hit.
4. **Contextual window orphaned.** The Log window (`StructureListWindowUI`) can be opened from a Missions row, which stays visible in Basic. It remains reachable and is not gated. The Group picker is reachable only from the hidden Recordings tab, BUT an already-open picker survives the switch (it draws from `RecordingsTableUI.DrawIfOpen:448` regardless of tab) and could still mutate group assignments; the close handler calls `groupPicker.Close()` (section 7.2). It holds no input lock, so this is a reachability rule, not a lock rule.
5. **Route candidate banner in Basic.** Kept. It carries `Open Logistics` and `Dismiss`, both of which target a surface Basic keeps.
6. **Data Management destructive actions.** Kept in Basic per section 4, on the grounds that a player who needs to clear data must be able to. If playtesting shows new players clicking destructive actions by accident, the mitigation is a confirmation prompt, not hiding the section. Its "Wipe All Recordings" label deliberately keeps the word Recordings (section 4.2).
7. **Scene differences.** The main window exists in exactly TWO scenes: FLIGHT (`ParsekFlight.cs:1268`) and SPACECENTER (`ParsekKSC.cs:120`), both through the single `ParsekUI.DrawWindow` path; the Tracking Station has NO main window (`ParsekTrackingStation.cs` draws markers and the ghost menu only), so no gated window can be open in a scene where the Settings toggle is unreachable. The InFlight-only buttons (Spawn Control, Gloops) are already conditional; the Basic gate composes with that condition (`InFlight && IsVisible(...)`), it does not replace it.
8. **Harness and in-game tests.** Any in-game test that drives a gated window must either force Advanced for its duration or assert against the mode. Tests must not assume the Advanced set is present. Review audit result: NO existing in-game test opens or draws a gated window through the UI (they call mode-independent internal statics; `LogisticsTooltipEchoImguiTest` drives Logistics, which is kept), and the harness autorun path uses the ungated global `TestRunnerShortcut` window. Expect the phase-7 audit to confirm near-zero forcing is needed.
9. **Mode changed while a gated window is mid-drag / mid-resize.** The apply runs in `Update()`, decoupled from the click; it may therefore coincide with any window state. The close path must not assume the window was idle (it only sets `IsOpen` and releases the lock, both safe mid-resize).
10. **Toolbar button in Basic.** Unchanged. The launcher is not gated; only the main window's contents are.
11. **Gloops recording in progress.** Switching to Basic is refused while `IsGloopsRecording` (section 7.2); otherwise the running ghost-only recording would keep sampling with no reachable Stop/Discard control.
12. **Timeline GoTo.** See section 4.1a: phase 1 made `ScrollToRecording` select the Recordings tab and Basic hid the button; the revision retargets GoTo at the Missions tab through `ShowMissionForRecording`, so it is visible in both modes and its gate key is `TabMissions`.
13. **Two test runner windows.** The Settings-launched `ParsekTestRunner` window is in the close set (no reopen path in Basic); the global Ctrl+Shift+T `ParsekTestRunnerGlobal` window is a separate window with a separate lock and is never gated (section 6.3).
14. **`autoRecordOnLaunch` off in Basic.** The Recording section stays visible, so a Basic player can turn auto-record off; with Gloops hidden there is then no manual recorder at all. Pre-existing shape (Advanced has no manual tree-recorder start either) and the toggle is the player's own explicit act; no rule, just noted.
15. **A Recordings-tab-only flag reaching a Basic-kept surface.** `Recording.Hidden` (Archive) is written only from the hidden Recordings tab yet filtered the Timeline, so an archive was irreversible in Basic. Resolved in section 4.4 by giving the Timeline its own reveal toggle over the SAME shared filter state, in both modes. The general rule it establishes: when a hidden surface owns the only writer of a flag a KEPT surface consumes, give the kept surface a control, never a mode-dependent filter. Any future flag with that shape gets the same treatment.

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

A grep gate is proposed in section 13.4 to enforce that the mode symbol appears only in UI draw paths.

### 9.1 Player-facing text that names a gated surface

One narrow extension of the gate's consumer set, added with the section 4.1a revision. A message telling the player to open a window whose launcher Basic has removed is worse than saying nothing: it is a 10-second on-screen instruction with no button behind it. So such text may vary by mode.

The rule is deliberately tight:

- The mode may change what a message SAYS, never whether it fires, what is detected, or what any action does. Everything past the wording is behavior the mode is not allowed to touch.
- Non-UI code must not name the mode vocabulary. It asks a plain reachability question of the UI coordinator (`ParsekUI.IsSpawnControlReachable`), which keeps the `IsVisible` read where every other one lives and keeps the grep gate's allowlist rationale honest.
- The message formatter itself stays pure and mode-blind: it takes a bool.

Current instance: the proximity notification in `ParsekFlight.NotifyNewProximityCandidates`, formatted by `SelectiveSpawnUI.FormatProximityNotification`. Basic keeps the observation ("Nearby craft: X (departs to Mun in 2m 0s).") and drops the "Open Real Spawn Control" call to action.

The related case that is NOT this: a message naming a window that is visible in both modes but is the wrong place to send the player. `MergeDialog`'s post-merge seal guidance pointed at the "Recordings window" when Seal is actually reachable from the Timeline and from the Missions tab's Re-Fly cell. That is a plain wording fix with no mode read.

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

No recording-schema or ledger change. `CurrentRecordingFormatVersion` and `CurrentRecordingSchemaGeneration` are untouched. A save written by a build with this feature loads on a build without it (the unknown settings key is ignored), and vice versa (the missing key resolves through `ResolveMode`, section 7.3). No migration path is needed.

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
| Mode changed | Info | Deferred apply (Update), after a toggle click | `previous->next` |
| Mode switch refused | Info | Toggle attempt while `IsGloopsRecording` | reason string |
| First-run default resolved | Info | First settings restore with no stored value; at most ONCE per install (persisted on resolve, 7.3) | `installHasParsekFootprint` with which of the three signals fired, chosen mode, `persisted=true` |
| Window auto-closed on mode change | Verbose | Advanced -> Basic, per closed window | window name, whether it held an input lock |
| Close-handler exception swallowed | Warn | Per-window try/catch caught | window name, exception type + message |
| Tab index clamped | Verbose | Deferred apply or draw | old index, new index, active tab count |
| Gated surface skipped | (none) | per draw | Deliberately NOT logged: this is a per-frame path and would spam. The mode-change line is sufficient to reconstruct which surfaces were hidden. |

---

## 13. Test Plan

### 13.1 Unit tests (`Source/Parsek.Tests`)

- **`AdvancedShowsEverySurface`** - `IsVisible(s, Advanced)` is true for every `UiSurface` value, walked by reflection over the enum. Fails if a new surface defaults to hidden in Advanced.
- **`BasicHidesExactlyTheDocumentedSet`** - `HiddenSurfaces(Basic)` equals the section 4 set exactly. Fails on silent scope drift in either direction.
- **`BasicKeepsCoreLoopSurfaces`** - Timeline, Logistics, Settings, and TabMissions are visible in Basic. This is the philosophy-3 guard.
- **`ResolveModeUsesFootprintOnlyWhenNoStoredValue`** - stored null: footprint true -> Advanced, false -> Basic.
- **`StoredValueAlwaysWinsOverFootprint`** - `ResolveMode(stored, footprint)` returns the stored mode unchanged for BOTH footprint values, including the stored-Advanced-with-no-footprint and stored-Basic-with-footprint crossings. Genuinely falsifiable because the stored value is a seam INPUT (section 7.3); it fails if footprint logic is ever allowed to override a saved preference.
- **`ResolutionIsSticky`** - after a no-stored-value resolution, the resolved mode is recorded (via the `SetStored...ForTesting` / `GetStored...` seams), so a second resolution sees a stored value and the footprint no longer matters. Guards the session-2 flip failure of section 7.3.
- **`OutOfRangeStoredValueResolvesToAdvanced`** - the clamping accessor maps any out-of-range int to Advanced (fail-open, section 6.2).
- **`EverySurfaceIsDecided`** - reflection walk asserting `IsVisible` throws on an unhandled enum value (the documented contract, section 6.1), so adding a `UiSurface` without a Basic decision fails the build rather than defaulting silently.
- **`TabIndexClampsIntoRange`** - clamp helper over both modes' visible tab counts, including the index-1-into-Basic case.
- **`MissionsIsTheDefaultAndFirstTab`** - asserts `TabMissions == 0`, that `selectedTab` initializes to it, that `TabLabels[0]` is "Missions" (single array, section 6.2), and that `VisibleTabCount(Basic) == 0` / `VisibleTabCount(Advanced) == 2`. Requires the constants and `TabLabels` to be `internal` (6.2). Fails if a future edit reorders the tabs back, which would silently restore the Recordings tab as the landing view and re-widen the clamp case of section 7.4.
- **`RecordingStoragePathsAreUnaffectedByRename`** - asserts `RecordingPaths` still resolves the `Parsek/Recordings` directory (`RecordingPaths` already has xUnit precedent). Guards the section 4.2 trap where an over-eager rename orphans every recording on disk.
- **`CloseHandlerCoversEveryGatedLockOwner`** - pins the section 7.2 close set: every lock-owning window whose launcher Basic hides (career, kerbals, gloops, spawn control, settings-launched test runner) appears in the handler's list, plus the group picker close. Guards the drift failure the existing `Cleanup()` sweep exhibits (it omits three windows).
- **`ScrollToRecordingSelectsRecordingsTab`** - phase 1 guard for section 4.1a, asserting the explicit `selectedTab = TabRecordings` write. After the 4.1a revision it guards the Recordings tab's own navigation API rather than a live cross-link; the cross-link's own cells live in `TimelineGoToMissionTests` (happy path, tab move off Recordings, mid-scene default-mission seeding, original-not-clone pick, the three Archive-filter rules, the three headless-reachable failure paths, the stale-target clear, the gate key in both modes, and a source-text gate over both button sites). The fourth failure path - target armed but never drawn - is draw-loop-only and has no headless cell.
- **Section 4.4 archive reveal** - `TimelineArchivedRowsTests` (13 cells: the default still hides, the reveal includes and is purely additive, the `IsArchivedRecording` stamp follows the RECORDING and not the build flag, the collector's `hidden=` / `archivedShown=` diagnostic, both directions of the `ShowArchivedRecordings` polarity and of its write-through to the shared filter, the untouched-save default, and the `ShouldRebuildTimeline` truth table including the archive-filter arm no invalidation call announces) plus `TimelineArchiveFilterWiringTests` (4 loose source-text cells over the IMGUI wiring `DrawTimelineWindow` / `DrawFilterBar` / `DrawEntryRow` cannot expose headlessly - the silent regression being a dropped `Build` argument, which leaves the toggle rendering and storing while the row set never moves). No mode cell is needed or wanted: 4.4 reads no mode, and the existing 13.4 grep gate is what proves it. After the 4.1a revision it guards the Recordings tab's own navigation API rather than a live cross-link; the cross-link's own cells live in `TimelineGoToMissionTests` (happy path, tab move off Recordings, mid-scene default-mission seeding, original-not-clone pick, the three Archive-filter rules, the three headless-reachable failure paths, the stale-target clear, the gate key in both modes, and a source-text gate over both button sites). The fourth failure path - target armed but never drawn - is draw-loop-only and has no headless cell.

### 13.2 Log-assertion tests

- **`ModeChangeLogsTransition`** - captures via `ParsekLog.TestSinkForTesting`, asserts the `[UI]` line contains both old and new mode. Catches silent removal of the transition diagnostic.
- **`AutoCloseLogsPerWindow`** - asserts one Verbose line per closed window on Advanced -> Basic.

Both classes need `[Collection("Sequential")]` per the `ParsekLog` static-state rule.

### 13.3 In-game tests (`InGameTests/`)

New category `UiComplexityMode`:
- **`BasicModeReleasesInputLocks`** - open every gated window in Advanced, switch to Basic THROUGH THE SETTER SEAM (section 6.2; writing the field directly bypasses the close handler and tests nothing), let the deferred apply run, assert no Parsek input lock remains held. This is the edge-case-2 regression guard and the most valuable test in the set.
- **`ModeRoundTripPreservesWindowState`** - set state in a gated window, round-trip Basic and back via the seam, assert state survived.
- **`AdvancedRenderParityAfterRoundTrip`** - assert the Advanced main-window control count is unchanged after a Basic round trip.
- Settings flip/restore follows the existing try/finally pattern (`RouteLineDrawInGameTest.cs:62-126` precedent), with the seam call replacing the raw field write for the mode itself.

Existing in-game tests that drive gated windows must force Advanced for their duration (edge case 8). The review audit found none that currently do (edge case 8); the phase-7 audit re-confirms this rather than assuming it.

### 13.4 Grep gate

`scripts/grep-audit-ui-complexity-mode.ps1`, modeled on `scripts/grep-audit-ers-els.ps1` specifically (allowlist file with directory-prefix entries, exit codes 0/1/2, scan root `Source/Parsek` - which makes "tests allowed" free), NOT on the zero-reference gates (`grep-audit-active-leg-recordings.ps1` hardcodes per-file patterns). Run from an xUnit test in the `GrepAuditTests` style (pwsh-on-PATH check with graceful skip, 60s timeout, exit-code assert, repo-root walk-up): assert `uiComplexityMode` and `UiSurfaceVisibility` appear only in `Source/Parsek/UI/**`, `Source/Parsek/InGameTests/**` (the phase-7 `UiComplexityMode` in-game category legitimately drives the mode through the setter seam, and unlike `Source/Parsek.Tests` it lives inside the `Source/Parsek` scan root), `ParsekUI.cs`, `ParsekFlight.cs` and `ParsekKSC.cs` (the two deferred-apply hosts), and `ParsekSettings*.cs`. `ParsekScenario.cs` is deliberately NOT allowlisted: the 7.3 footprint resolution keeps the mode symbols out of it by passing the scenario-node signal as a bare bool into the persistence layer (the scenario knows "is my node populated", not "what is the mode"). Enforces the section 9 invariant mechanically, so a future change cannot quietly make recording or dispatch mode-dependent.

---

## 14. Implementation Phasing

| Phase | Scope | Notes |
|-------|-------|-------|
| 1 | Recordings -> Missions rename + tab reorder + GoTo tab-select fix (sections 4.1, 4.1a) | Independent of the mode gate and applies to BOTH modes, so it lands first and alone. User-visible: needs a CHANGELOG entry in its own commit. Verify the section 4.2 must-not-rename table before committing. Guard tests land IN this commit: `MissionsIsTheDefaultAndFirstTab`, `RecordingStoragePathsAreUnaffectedByRename`, `ScrollToRecordingSelectsRecordingsTab`. |
| 2 | `UiComplexityMode.cs`: enum, surfaces, pure `UiSurfaceVisibility` + `ResolveMode` + unit tests | No behavior change; pure core lands green before any draw site moves |
| 3 | `ParsekSettings` field + full persistence wiring + install-footprint resolution + Settings toggle UI + setter seam | Toggle live and persisting, gates not yet wired. Stored-value branch written and tested BEFORE the footprint branch; persist-on-resolve included (section 7.3). |
| 4 | Main-window button gates + separator spacing + frame-latched mode apply skeleton | The visible payoff, four buttons in Basic. The Update-side latch lands here since the first gate needs it. |
| 5 | Tab-bar gate in Basic + tab clamp + GoTo button gate | Smaller than it was pre-rename: phase 1 already put Missions at index 0 |
| 6 | Settings section gates (Diagnostics, Sample Density) | Test Runner Ctrl+Shift+T global window stays live; only the Settings-launched instance loses its launcher |
| 7 | Mode-change close handler + input-lock release + Gloops guard + in-game tests | Edge case 2, the highest-risk item. Explicit per-window close list (NOT `Cleanup()` as source, NOT a pre-landed base-class refactor - section 17.1), `CloseHandlerCoversEveryGatedLockOwner` pins it. |
| 8 | Grep gate + doc updates (CHANGELOG, todo, this doc status) | |

Phase 1 is deliberately first and standalone. It is the only phase whose effect is visible to existing Advanced users, so isolating it in one commit keeps it revertable without unwinding any of the mode work, and keeps its CHANGELOG entry honest about who is affected. (Revertability is scoped to the landing window: once phase 5 lands, the clamp and zero-toolbar logic assume Missions == 0, so a later phase-1 revert would touch gate code too.)

Phases land as sequential commits on this one branch and release together; no release ships between phases. That is why the phase-3 "toggle live before gates exist" intermediate state is acceptable - no player ever sees it (the repo precedent for intermediate landings is inert-INTERNAL states; a visible inert control would not pass on its own).

New source files: `Source/Parsek/UI/UiComplexityMode.cs`, `Source/Parsek.Tests/UiComplexityModeTests.cs`, `scripts/grep-audit-ui-complexity-mode.ps1`.

---

## 15. Open Questions

### 15.1 Timeline tier filters

The Timeline has 10 toggles, several being tier filters. Whether those should collapse to a single dropdown in Basic is a within-window simplification, deliberately deferred out of v1 (which gates whole surfaces only). Flagged because Timeline is the window a Basic player uses most.

---

## 16. Code Layout

| Concept | Code |
|---------|------|
| Mode enum + surfaces + predicate + `ResolveMode` | `Source/Parsek/UI/UiComplexityMode.cs` (new) |
| Persisted value | `ParsekSettings.uiComplexityMode` + clamping accessor, `ParsekSettingsPersistence` (full showRouteLines-analog wiring, section 6.2) |
| Setter seam (all mode writes) | `ParsekUI.SetUiComplexityMode` (section 6.2) |
| Frame-latched deferred apply | controller `Update()` (`ParsekFlight` / `ParsekKSC`), section 7.2 |
| Toggle UI | `UI/SettingsWindowUI.cs` (new `Interface` section, drawn first; Basic option disabled while `IsGloopsRecording`) |
| Main-window gates | `ParsekUI.cs:193-385` |
| Tab gates + reorder + clamp | `UI/RecordingsTableUI.cs:124-127`, `:1182-1190` |
| GoTo cross-link fix + gate | `UI/RecordingsTableUI.ShowMissionForRecording`, `UI/MissionsWindowUI.RevealMissionForRecording`, `UI/TimelineWindowUI.DrawEntryRow` (section 4.1a) |
| Archive reveal (section 4.4) | `Timeline/TimelineBuilder.Build(..., includeArchivedRecordings)` + `TimelineEntry.IsArchivedRecording`; `UI/TimelineWindowUI.ShowArchivedRecordings` / `ShouldRebuildTimeline` / `DrawFilterBar` / `DrawEntryRow`; shared filter state `GroupHierarchyStore.HideActive` |
| Rename (button, title, tooltips) | `ParsekUI.cs:239`, `UI/RecordingsTableUI.cs:435`, `UI/TimelineWindowUI.cs:1193/:1223` |
| Rename exclusions | `UI/RecordingsTableUI.cs:432` (window ID), log strings (section 4.2 blanket rule), `RecordingPaths.cs` (storage), `UI/TimelineWindowUI.cs:695` (unrelated filter), `SettingsWindowUI.cs:581` + `ParsekUI.cs:927` (wipe strings) |
| Mode-change close handler | `ParsekUI.OnUiComplexityModeChanged` (explicit per-window list, section 7.2) |
| Invariant enforcement | `scripts/grep-audit-ui-complexity-mode.ps1` |

---

## 17. Separate UI Improvement Proposals

Independent of the Basic/Advanced feature. Each would ship as its own change; none is assumed by this design. Ordered by value against the "too complicated" complaint.

### 17.1 Extract shared window chrome (highest structural value; NOT a prerequisite)

TEN files independently implement the same window scaffolding: `*HasInputLock`, `isResizing*`, `IsOpen`, `ReleaseInputLock`, `IsMouseOverOpenWindow` (`CareerStateWindowUI`, `KerbalsWindowUI`, `LogisticsWindowUI`, `RecordingsTableUI`, `SettingsWindowUI`, `SpawnControlUI`, `TestRunnerUI`, `TimelineWindowUI`, plus the two the original count missed: `GloopsRecorderUI`, `StructureListWindowUI`). A shared `ParsekWindowBase` would remove ten copies of the same lifecycle.

Review verdict (settled for this feature): do NOT pre-land this refactor before phase 7. The windows are instance classes with uniform chrome shape but non-uniform signatures (`DrawIfOpen(Rect)` vs `(Rect, ParsekFlight, bool)` vs `(Rect, MonoBehaviour)`; Timeline's close routes through `CloseWindow()` with warp-date persistence while Kerbals is a plain field set), so the extraction is a mid-size refactor of hot IMGUI paths inside a feature whose core invariant is "Advanced stays byte-identical". The de-risking it promised is achieved more cheaply by the explicit per-window close list + `CloseHandlerCoversEveryGatedLockOwner` (section 7.2), backstopped by the ungated `DrawIfOpen` self-heal (7.1). Ship the feature first; extract afterwards if ever.

### 17.2 Main window has no visual grouping

Eight buttons in a flat stack with only blank-space separators and no labels. Even in Advanced, three group headers (`Flight`, `History`, `Career`) would make the launcher scannable. Cheap, and it helps Advanced users, who are not served by the mode toggle at all.

### 17.3 No search or filter in the Recordings table

Verified: the only `TextField` uses in `RecordingsTableUI` are rename and loop-period editing. With many recordings the table is a long scroll with sort as the only narrowing tool. A single name-filter field is a small change with a large effect. Review note: the same argument now applies to the Missions tab (sort + archive-hide but no name filter, `MissionsWindowUI.cs:2504-2545`), which becomes the primary landing view in both modes; if this ships, cover both tabs.

### 17.4 First-run onboarding

A one-time panel stating the three-step loop (fly, commit, rewind) with a "Show advanced features" action that flips the mode. Pairs naturally with this feature and addresses the underlying problem (players do not know what the mod wants them to do) rather than only the symptom (too many buttons). Deferred as its own design.

### 17.5 Toolbar button carries no state

The Logistics button tints red for hard-broken routes, but only once the main window is open. A player with the window closed has no signal. Tinting or badging the ApplicationLauncher icon (`ParsekFlight.cs:1301`, `ParsekKSC.cs:128`) surfaces the error where it can actually be seen. Applies in both modes.

### 17.6 Settings is one long scroll

Seven sections, no folds. The codebase already has caret and foldout helpers in `RecordingsTableUI`. Collapsible sections, with `Interface` and `Recording` open by default, would shorten it considerably. Partly mitigated by this feature (Basic drops two sections), so lower priority.

### 17.7 "Recordings" versus "Missions" naming (RESOLVED, now in scope)

This was raised here as a separate proposal and has since been folded into the feature proper: the rename and tab reorder apply in both modes and ship as phase 1. See section 4.1 for the decision, 4.2 for the strings excluded from it. Retained as a heading so the cross-reference from earlier revisions still resolves.

### 17.8 Mission-level ghost-visibility toggle (follow-up for the section 4.3 limitation)

A per-mission (or per-composition-row) control in the Missions tab that writes `Recording.PlaybackEnabled` for the mission's recordings, giving Basic players retroactive "hide this ghost" without the raw table. Deliberately NOT in this feature: it is a new control visible in both modes, which philosophy 6 forbids here. Design questions for its own change: mission-level vs row-level granularity, interaction with the include checkboxes (loop intervals vs playback enable are different axes), and whether debris follows the parent toggle. Until it ships, section 4.3 documents the limitation.
