# Parsek Recordings UI — Structural Extraction

Factual extraction of the Recordings window's rendered structure, for downstream UX analysis. No critique, no recommendations.

Primary source: `Source/Parsek/UI/RecordingsTableUI.cs` (5946 lines). Supporting: `RecordingsTableFormatters.cs`, `GroupPickerUI.cs`, `GroupPickerPresentation.cs`, `UnfinishedFlightsGroup.cs`, `RecordingGroupStore.cs`, `GroupHierarchyStore.cs`, `RecordingTree.cs`, `BranchPoint.cs`, `docs/dev/design-ui-basic-advanced.md`.

---

## 0. Identity and framing facts

- The window is **one window with two tabs**. Its title is the literal `"Parsek - Missions"` (`RecordingsTableUI.cs:613`), its IMGUI id is `"ParsekRecordings".GetHashCode()` (`:611`). The main-window launcher button reads `Missions`.
- Tabs: `TabLabels = new[] { "Missions", "Recordings" }` (`:130`), `TabMissions = 0` / `TabRecordings = 1` (`:127-128`), default selection `selectedTab = TabMissions` (`:129`). Tab selection is **transient, not persisted** (`:120-126`).
- The Missions tab's content is delegated entirely to `MissionsWindowUI.DrawMissionsTabContent()` (`:1396`) and is out of scope for this report. Everything below describes the **Recordings tab**.
- Default width when Info is collapsed: `DefaultCollapsedWindowWidth = 1205f + ColW_Rewind + ColW_ReFly` = **1355 px** (`:48`). Expanded: `1663f + 60 + 90` = **1813 px** (`:49`). Minimum resize width equals the collapsed width (`:33`), min height 150 (`:34`).
- The window is opened positioned to the right of the main window; height is `mainWindowRect.height` in flight, doubled elsewhere (`:590-599`).
- The window takes a `CAMERACONTROLS` input lock while the mouse is over it (`:632-643`).
- **The table indexes into the raw committed list, not ERS.** `var committed = RecordingStore.CommittedRecordings;` with an explicit `[ERS-exempt]` block comment: "the recordings-table window is the authoritative management surface — it lists, sorts, renames, groups, and deletes recordings by index into the raw committed list (including `NotCommitted` rows when present)" (`:1410-1418`). Visibility filtering is done per-row instead.

---

## 1. Window layout, top to bottom

### 1.1 Draw order (`DrawRecordingsWindow`, `:1350-1631`)

1. `GUILayout.Space(5)` — breathing room under the title bar (`:1353`).
2. Style init (`EnsurePhaseStyles`) (`:1356`).
3. Read the frame-latched complexity mode; apply the defensive tab-index clamp (`:1361-1371`).
4. **Tab bar** — `GUILayout.Toolbar(selectedTab, TabLabels, toggleButtonStyle)` + `Space(3)`. Drawn only when `VisibleTabCount(complexity) > 0`, i.e. **Advanced only** (`:1379-1388`).
5. If the active tab is Missions: delegate + Missions bottom bar + `return` (`:1394-1399`).
6. Deferred ghost-only delete, if one was queued last frame (`:1403-1408`).
7. Data gathering: `committed`, `supersedes`, `retirements`, `now`; prune stale watch-transition caches; handle rename/loop-period defocus; rebuild sorted indices (`:1418-1438`).
8. Deferred cross-link scroll application: `recordingsScrollPos.y = pendingScrollRowIndex * 22f` (`:1441-1447`) — a hardcoded 22 px row-height assumption.
9. **Time-range filter indicator** (`DrawTimeRangeFilterIndicator`, `:1322-1348`) — only when a filter is active. One row: a label reading `"Filtered: " + <presetName>` or `"Filtered: " + <minLabel> + " — " + <maxLabel>`, and a `Clear` button (width 50).
10. If `committed.Count == 0`: a single label `"No recordings."` (`:1454`) — and nothing else (no header, no scroll view).
11. Otherwise: **fixed header row** (outside the scroll view, so it stays pinned) (`:1462`).
12. `BeginScrollView` (horizontal off, vertical always on) → dark body box → **the tree** (`:1465-1619`).
13. **Bottom bar** (`DrawRecordingsBottomBar`, `:1248-1291`).
14. **Tooltip strip** (`DrawRecordingsWindowTooltip`, `:1759-1771`).

### 1.2 The header row (`DrawRecordingsTableHeader`, `:1095-1246`)

Every header cell is forced to `ColHeaderHeight = 32f` (`:58`). Order and widths:

| # | Header text | Width const | px | Sortable? | Extra widget |
|---|---|---|---|---|---|
| 1 | *(blank)* + `#` | `ColW_Enable` + `ColW_Index` + 8 | 20+30+8 | `#` sorts `SortColumn.Index` | **select-all playback toggle** (writes `PlaybackEnabled` on every committed recording, `:1117-1123`) |
| 2 | `Name` | ExpandWidth | — | `SortColumn.Name` | — |
| 3 | `Phase` | `ColW_Phase` | 90 | `SortColumn.Phase` | — |
| 4 | `Site` | `ColW_Site` | 90 | `SortColumn.LaunchSite` | — |
| 5 | `Launch` | `ColW_Launch` | 110 | `SortColumn.LaunchTime` | — |
| 6 | `Duration` | `ColW_Dur` | 80 | `SortColumn.Duration` | — |
| 6a | `MaxAlt` `MaxSpd` `Dist` `Pts` `Start` `End` | 65/65/65/35/120/120 | | **not** sortable | only when `showExpandedStats` (`:1149-1157`) |
| 7 | `Status` | `ColW_Status` | 120 | `SortColumn.Status` | — |
| 8 | `Group` | `ColW_Group` | 60 | no | — |
| 9 | `Loop` | `ColW_Loop` | 60 | no | **select-all loop toggle**, greyed off if any committed recording is route-bound (`:1175-1199`) |
| 10 | `Period` | `ColW_Period` | 90 | no | tooltip (quoted below) |
| 11 | `Watch` | `ColW_Watch` | 50 | no | **flight scene only** (`:1206-1210`) |
| 12 | `Rewind` | `ColW_Rewind` | 60 | no | — |
| 13 | `Re-Fly` | `ColW_ReFly` | 90 | no | — |
| 14 | `Archive` | `ColW_Hide` | 80 | no | **global archive-filter toggle** → `GroupHierarchyStore.HideActive` (`:1220-1228`) |
| — | scrollbar spacer | `verticalScrollbar.fixedWidth` (16) | | | (`:1231-1237`) |

Sort arrows are appended by `ParsekUI.DrawSortableHeaderCore` as `" ▲"` (`▲`) ascending / `" ▼"` (`▼`) descending (`ParsekUI.cs:1456-1457`). Default sort is `SortColumn.LaunchTime`, ascending (`:117-118`).

The `Period` header tooltip verbatim (`:1201-1202`):

> `"Launch-to-launch period: how often the ghost relaunches.\nWhen shorter than the recording duration, successive launches overlap.\nClick unit to cycle: sec → min → hr → auto.\n\"auto\" inherits from Settings > Looping."`

### 1.3 Bottom bar (`:1248-1291`)

`FlexibleSpace()` pushes it to the window bottom, then one horizontal row:

- `"Info ▶"` / `"Info ◀"` toggle, width 65, **only when `committed.Count > 0`** (`:1254-1266`). Toggling it also *resizes the window*: expanding forces width to `DefaultExpandedWindowWidth`, collapsing forces `DefaultCollapsedWindowWidth`.
- `"New Group"`, width 80 — creates `"Group N"` (first unused N), adds it to `KnownEmptyGroups`, expands it, and immediately enters rename mode on it (`:1268-1276`).
- `"Close"` — closes the window and the group picker (`:1278-1283`).
- Then a resize handle (bottom-right, `ResizeHandleSize = 16f`) and `GUI.DragWindow()`.

The Missions tab's bottom bar has only `Close` + the same chrome (`:1296-1312`).

### 1.4 Tooltip strip (`:1759-1771`)

A wrapped `GUI.skin.box`-styled label at the very bottom showing `recordingsWindowTooltipText` if set (currently only the loop-period clamp tooltip) else `GUI.tooltip`. When empty it collapses to a zero-height label so the layout doesn't jump.

> Note: there is also a private `DrawRecordingTooltip(Recording)` (`:5144-5221`) that renders a rich floating box (max altitude / speed / distance / points / chain status / orbit segments / part events / body / max range / storage breakdown / resource, inventory and crew manifests). **It has no callers** anywhere in the codebase.

### 1.5 ASCII mockup of a typical rendered window

Advanced mode, flight scene, Info collapsed, `Archive` filter on, sorted by Launch ascending. Names invented; layout and text forms are faithful.

```
┌─ Parsek - Missions ────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                                                                                                                    │
│  ╔══════════╗┌───────────┐                                                                                                         │
│  ║ Missions ║│ Recordings│   <- GUILayout.Toolbar, 2 entries, half-width each (Advanced only; Basic draws NO toolbar)               │
│  ╚══════════╝└───────────┘                                                                                                         │
│                                                                                                                                    │
│ ┌───┬──┬───────────────────────────────┬─────────────┬─────────┬───────────┬────────┬──────────┬───────┬─────┬────────┬─────┬──────┬────────┬─────────┐
│ │[x]│# │ Name ▲                        │ Phase       │ Site    │ Launch    │Duration│ Status   │ Group │Loop │ Period │Watch│Rewind│ Re-Fly │ Archive │
│ └───┴──┴───────────────────────────────┴─────────────┴─────────┴───────────┴────────┴──────────┴───────┴──[x]┴────────┴─────┴──────┴────────┴────[x]──┘
│ ╭────────────────────────────────────────────────────────────── scrolls ─────────────────────────────────────────────────────────╮  ║
│ │[x]│  │ ▶ Gloops - Ghosts Only (2)    │             │         │ Y1 D12    │ 4m 10s │ past     │  G    │ [ ] │        │  W  │      │        │   [ ]   │ ║
│ │[x]│  │ ▼ Jool Probe Mk2 (7)          │             │ LaunchPad│ Y1 D18   │ 1h 22m │ T+2d 4h..│  G    │ [x] │        │  W* │  R   │        │   [ ]   │ ║
│ │[x]│ 3│ ├─ Jool Probe Mk2             │ Kerbin atmo │ LaunchPad│ Y1 D18   │ 6m 02s │ SubOrbital│ G    │ [x] │ 900  hr│  W  │  R   │        │   [ ]   │ ║
│ │[x]│  │ ├─ ▼ Jool Probe Mk2 (3)       │             │ LaunchPad│ Y1 D18   │ 1h 05m │ Orbiting │  G    │ [x] │        │     │      │        │         │ ║
│ │[x]│ 4│ │  ├─ Jool Probe Mk2          │ Kerbin exo  │ LaunchPad│ Y1 D18   │ 21m 4s │ past     │  G    │ [ ] │  auto  │  W  │      │        │   [ ]   │ ║
│ │[ ]│ 5│ │  ├─ Jool Probe Mk2          │ Kerbin -> Jool│       │ Y1 D19    │ 38m 1s │ Orbiting │  G    │ [ ] │  auto  │  W  │      │        │   [ ]   │ ║
│ │[x]│ 6│ │  └─ Jool Probe Mk2          │ Jool approach│        │ Y2 D45    │ 5m 51s │ Landed st│  G    │ [x] │ 30   min│ W  │      │        │   [ ]   │ ║
│ │[x]│  │ ├─ ▶ Jool Probe Mk2 / Crew (1)│             │         │ Y1 D18    │ 3m 12s │ Boarded  │  G    │ [ ] │        │  W  │      │        │   [ ]   │ ║
│ │[x]│  │ ├─ ▶ Jool Probe Mk2 / Debris (2)│           │         │ Y1 D18    │ 12m 8s │ Destroyed│  G    │     │        │  W  │      │        │   [ ]   │ ║
│ │[x]│  │ └─ ▶ STASH (1)                │             │         │ Y1 D18    │ 9m 44s │ Destroyed│       │     │        │     │      │        │         │ ║
│ │[x]│  │ ▶ Munshot 4 #2 (3)            │             │ Runway  │ Y2 D06    │ 44m 2s │ T-1d 6h..│ G   X │ [ ] │        │  W  │  FF  │        │   [ ]   │ ║
│ │[x]│  │ ▶ Kerbal X (2)                │             │ LaunchPad│ Y2 D11   │ 18m 0s │ Recovered│  G    │ [x] │        │     │      │        │         │ ║
│ │[x]│12│ Untitled                      │ Kerbin surface│       │ Y2 D14    │ 0s     │ static   │ G   X │     │        │  W  │      │        │   [ ]   │ ║
│ │[x]│13│ Munshot 4 lander              │ Mun surface │         │ Y2 D19    │ 7m 30s │ Destroyed│  G    │ [x] │ 3600 sec│ W  │      │ Fly Seal│  [ ]   │ ║
│ ╰────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────╯  ║
│                                                                                                                                    │
│ ┌────────┐┌──────────┐┌───────┐                                                                                                    │
│ │ Info ▶ ││ New Group││ Close │                                                                                                    │
│ └────────┘└──────────┘└───────┘                                                                                                    │
│ ┌────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┐  │
│ │ Follow ghost in watch mode                       <- wrapped GUI.tooltip strip (zero-height when empty)                         │  │
│ └────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘  │
│                                                                                                                              ◢ 16px │
└────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

Notes visible in the mockup that are load-bearing facts:

- **Group / chain / block header rows have no `#` value** — the `#` cell is a blank fixed-width spacer (`:2240`, `:3865`, `:2863`). Only leaf recording rows carry a number.
- The `#` value is `(ri + 1)` where `ri` is the **raw committed-list index** (`:1673`), *not* the display row. Under any non-Index sort the numbers appear out of order and with gaps (superseded / hidden rows are skipped).
- `STASH` renders with no `G`, no `X`, no Loop, no Period, no Watch, no Rewind, no Re-Fly and no Archive cell — all blanks (`:2925-2985`).
- Auto-generated mission folders (`Jool Probe Mk2`, its `/ Crew`, `/ Debris`) show only `G`; user-created groups (`Munshot 4 #2` in the mockup, were it user-made) show `G  X`. Determined by `canDisbandGroup = !RecordingStore.IsPermanentGroup(groupName)` (`:2356`).
- Debris rows and non-loopable rows show a **blank Loop cell and blank Period cell** (`ShouldSuppressRowLoopUi`, `:5237-5238`).

---

## 2. Grouping model

### 2.1 What a group is

A group is a **string tag** stored on the recording: `Recording.RecordingGroups` is a `List<string>`, so **one recording can appear in several groups simultaneously** and is drawn once per group it belongs to (`BuildGroupTreeData`, `:5831-5845`: "Multi-group: recording appears in each group it belongs to").

Nesting is a *separate* store: `GroupHierarchyStore.groupParents` is a `child → parent` dictionary (`GroupHierarchyStore.cs:15`), with cycle detection via `IsInAncestorChain` (depth cap 100, `:120-134`) and a rule that permanent root groups can never be given a parent (`:147-152`, plus `EnsurePermanentRootGroupsAreRoot` at `:165`).

Root groups = every known group name that has no parent entry (`:5887-5894`), sorted `OrdinalIgnoreCase`. "Known" is the union of: names appearing on any recording, both sides of every `groupParents` entry, and `KnownEmptyGroups` (`:5862-5872`).

`KnownEmptyGroups` (`:106`) is a **runtime-only, non-persisted** list of groups created but not yet populated.

### 2.2 The four kinds of group

| Kind | Created by | Renameable | Disbandable (`X`) | Hideable |
|---|---|---|---|---|
| **Auto-generated tree/mission group** — one folder per committed tree, named from `tree.TreeName`, deduped as `"{base} #2"`, `"#3"`… | `RecordingGroupStore.AutoGroupTreeRecordings` (`RecordingGroupStore.cs:71-150`) | yes — and the rename **cascades** through `MissionGroupLink.RenameMissionGroup` to the mission name and the two auto subgroups (`:4096-4113`) | **no** (`IsPermanentGroup` true) | yes |
| **`X / Debris` subgroup** — suffix `" / Debris"` (`RecordingGroupStore.cs:27`), parented to the root group | same, on the first `rec.IsDebris` member (`:102-111`) | yes (as a plain group, no cascade) | no | yes |
| **`X / Crew` subgroup** — suffix `" / Crew"` (`RecordingGroupStore.cs:28`) | same, on the first member with a non-empty `rec.EvaCrewName` (`:112-123`) | yes | no | yes |
| **User group** — `"Group N"` from `New Group`, or a typed name from the picker's `+` button | `GenerateUniqueGroupName` (`:4140-4152`) / `GroupPickerPresentation.TryCreateGroupName` | yes | **yes** | yes |
| **Permanent root group** — the literal `"Gloops - Ghosts Only"` (`RecordingStore.cs:136`) | ghost-only recording flow | **no** (rename blocked, `:2288-2293`, `:4080-4084`) | no | yes |
| **`STASH` virtual system group** | never stored; membership recomputed every frame | no | no | **no** (`GroupHierarchyStore.CanHide` returns false for system groups, `GroupHierarchyStore.cs:95-99`) |

The `"#N"` dedupe suffix is deliberate rather than `"(N)"`, because the group button already appends ` ({memberCount})` — the code comment spells this out: *"a second mission named 'Kerbal X' would display as 'Kerbal X (2) (3)' — visually ambiguous between mission index and recording count"* (`RecordingGroupStore.cs:878-891`).

### 2.3 Root-level ordering (`:1486-1577`)

Root items are a unified sorted list of four kinds (`RootItemType`, `:240`): `Group`, `Chain`, `Recording`, `VirtualGroup`. Sort keys per item type come from `GetGroupSortKey` / `GetChainSortKey` / `GetRecordingSortKey`, and the comparator is `CompareRootItemsForSort` (`:5098-5113`). **Permanent root groups are pinned to the top** regardless of sort (`ComparePinnedRootGroups`, `:5115-5124`), i.e. `Gloops - Ghosts Only` always sorts first.

For string-based sort columns (Name / Phase / LaunchSite) the comparison uses `SortName`; otherwise the numeric `SortKey`.

`VirtualGroup` is in the enum and dispatched (`:1615-1617`) but **is never inserted into `rootItems`** any more — the comment at `:1579-1594` records that STASH used to render at root level and is now nested under each owning tree's root group instead. The root loop only logs its membership count.

### 2.4 Visibility filters, in the order they apply

1. **Superseded / rewind-retired → row does not exist.** `IsInactiveForDisplay(rec, supersedes, retirements)` (`:5934-5944`) = `EffectiveState.IsSupersededByRelation` OR `EffectiveState.IsRewindRetired`. Applied in `BuildGroupTreeData` (`:5828`), in the root loop (`:1521`), and again at the top of `DrawRecordingRow` (`:1640-1643`).
2. **Archive filter.** `rec.Hidden && GroupHierarchyStore.HideActive` → row skipped (`:1644`). Hidden *groups* are skipped whole (`:2208`).
3. **Time-range filter.** A group is skipped if no descendant overlaps the range; a chain shows entirely if any member overlaps; a standalone row is filtered directly (`:1499-1555`).

**Archive-filter escape hatch:** a hidden group still renders its nested STASH subgroup so re-fly opportunities cannot be buried (`:2206-2214`).

### 2.5 Tree connectors and indentation

Box-drawing prefixes built from char codes (`:2062-2065`):

- `TreeConnectorMid` = `├─ ` (`U+251C U+2500 space`)
- `TreeConnectorLast` = `└─ ` (`U+2514 U+2500 space`)

Expand/collapse arrows are `▼` (`▼`) expanded / `▶` (`▶`) collapsed (`:2248`, `:3873`, `:2870`).

Indentation step = the measured pixel width the connector glyphs add inside a label, cached (`ConnectorWidth`, `:2087-2097`, fallback 15 px). `SelfConnectorIndent(depth) = max(0, depth-1) * ConnectorWidth()` (`:2103-2106`); a child gets `ChildConnectorIndent(parentDepth) = parentDepth * ConnectorWidth()` (`:2110-2113`).

A group's children render in **three fixed sections, in this order** (`:2650-2740`): (1) display blocks (chain/vessel blocks and single rows), (2) child sub-groups, (3) the nested STASH group. Exactly one section owns the `└─` corner; `ResolveLastChildSection` (`:2124-2131`) picks it with precedence Virtual > ChildGroup > Block, and the "last visible" computation skips rows that would render nothing (`DisplayBlockRendersAnything` `:2156-2168`, `ChildGroupRendersAnything` `:2176-2184`, `IsRowVisible` `:2139-2148`).

### 2.6 Group-header aggregate values

Computed over the recursive descendant set (`CollectDescendantRecordings`, `:4043-4057`):

- **Enable toggle**: checked iff all descendants enabled; writing it sets `PlaybackEnabled` on all (`:2226-2237`).
- **Site**: `LaunchSiteName` of the group's "main" recording = earliest non-debris descendant (`FindAggregateMainRecordingIndex`, `:4770-4790`).
- **Launch**: earliest `StartUT` among descendants, or `"-"` (`GetGroupEarliestStartUT`, `:4716-4725`).
- **Duration**: **sum** of descendant durations (`GetGroupTotalDuration`, `:4730-4739`).
- **Status**: `GetGroupStatus` (`:4858-4932`) — prefers the active descendant closest to now, then the nearest future one, then past. Past uses the terminal state of the latest-ending non-debris descendant, else `"past"`, else `"-"`.
- **Loop**: aggregate over *loopable* descendants only; the cell renders blank when there are none (`LoopAggregateState.SuppressToggle`, `:5252-5262`).
- **Archive**: checked iff all descendants hidden; writing it sets `Hidden` on all **and** adds/removes the group from `GroupHierarchyStore.hiddenGroups` (`:2622-2644`).

### 2.7 How a RecordingTree's branches become rows

There is **no direct rendering of tree topology.** `RecordingTree.BranchPoints` is never read by `RecordingsTableUI` (grep-verified). A tree becomes visible through four indirect mechanisms:

1. Every committed tree gets one auto folder (its `AutoGeneratedRootGroupName`), so a mission = a folder.
2. Debris members land in `/ Debris`, EVA members in `/ Crew`.
3. Inside a group, members are collapsed into **display blocks** keyed by `GetGroupDisplayIdentity` (`:4947-4956`):
   - `treevessel:{TreeId}:{VesselPersistentId}` when both are present, else
   - `chain:{ChainId}` when a chain id is present, else
   - no identity → the recording renders as its own single row.
   So **all segments of one physical vessel within one tree collapse into one expandable block**, named after the first member with a non-empty `VesselName` (`ResolveGroupDisplayBlockName`, `:4971-4983`, fallback `"Recording"`). Blocks with one member degrade to a plain row (`BuildGroupDisplayBlocks`, `:4985-5064`).
4. Split siblings that are re-flyable surface in `STASH`.

### 2.8 The STASH virtual group

`UnfinishedFlightsGroup` (`UnfinishedFlightsGroup.cs`):

- Display name is the literal `"STASH"` (`:40`) — all-caps as the signal that it is system-controlled.
- Tooltip verbatim (`:42-43`):
  > `"Vessels and kerbals that ended up in a state where you might want to re-fly them -- crashed, abandoned in orbit, stranded on a surface. Click Fly to take control at the separation moment; click Seal to close the slot permanently if you're done with it."`
- Membership is derived **every frame** from `EffectiveState.ComputeERS()` filtered by `EffectiveState.IsUnfinishedFlight` (`:72-92`); nothing is stored.
- It is nested under each owning tree's auto root group, one instance per tree, filtered to that tree's members (`CollectUnfinishedFlightsForTreeGroup`, `:2753-2785`).
- It is a **mirror, not a destination** — the same recordings still render under their natural mission folder as well (comment at `:2689-2695`).
- Member rows inside it are sorted by `StartUT` (`:2993-2999`) and drawn with `unfinishedFlightRowDepth++` so their Loop/Period cells are suppressed (`:3001-3019`).
- It is not a valid drop target (`GroupHierarchyStore.IsDropTargetAllowed`, `GroupHierarchyStore.cs:82-86`) and cannot be hidden (`CanHide`, `:95-99`).

---

## 3. Chain blocks

### 3.1 What "chain" means here

`Recording.ChainId` is a string tag. `BuildGroupTreeData` builds `chainToRecs: chainId → List<int>` (`:5847-5857`). "**Root chains**" are chains where **no** member has any `RecordingGroups` at all (`:5896-5908`); those render as top-level chain blocks. A chain whose members are in groups renders inside the group instead, via the display-block mechanism.

### 3.2 Rendering

Both chain blocks and vessel/grouped blocks go through one method, `DrawRecordingBlock` (`:3828-4038`); `DrawChainBlock` (`:3809`) and `DrawGroupedRecordingBlock` (`:3819`) are thin wrappers differing only in the log-kind string (`"Chain"` vs `"Block"`) and whether a chain id is passed for the group picker.

The block header row:

- Label: `$"{treeConnector}{arrow} {blockName} ({members.Count})"` (`:3883`). `blockName` is the **first member's `VesselName`** for a chain (`:3814`), or the block display name for a vessel block. If empty it falls back to the log-kind literal `"Chain"` / `"Block"` (`:3853`).
- Expansion state lives in a separate set, `expandedChains`, keyed by block id (`:67`, `:3872`).
- Aggregate enable toggle over members (`:3848-3860`).
- `#` cell blank; Phase blank; Site = `members[0].LaunchSiteName`; Launch = **earliest** member `StartUT`; Duration = `blockEnd - blockStart`, i.e. the **span**, not the sum (contrast group headers, which sum) (`:3875-3901`).
- Status via `GetChainStatus` → delegates to `GetGroupStatus` (`:4938-4945`).
- Group cell: `G` only, **never an X** (`:3921-3930`). The picker opens `OpenForChain(chainId)` for a real chain, `OpenForRecordings(members)` for a vessel block.
- Loop aggregate; unlike group rows, chain/block loop writes **do** call `ApplyAutoLoopRange` (`applyAutoRange: true`, `:3967`).
- Period, Watch, Re-Fly, Archive cells are all **blank** (`:3972-4020`). Rewind/Forward is present (`:3975-4018`).
- Members are drawn indented on expand, corner connector on the last *visible* member (`:4024-4036`).

### 3.3 What branch relationships are and are not visible

Not shown anywhere in the table: undock/dock/EVA/decouple branch points, `ChainIndex` ordering, which segment continued from which, parent/child recording links, dock partners. `BranchPointType` (`BranchPoint.cs:6-28`: `Undock`, `EVA`, `Dock`, `Board`, `JointBreak`, `Launch`, `Breakup`, `Terminal`, `VesselSwitchContinuation`) never reaches this UI.

What *is* visible, and only as a side effect: chain membership (a block), tree+vessel identity (a block), debris-ness (a `/ Debris` folder), EVA-ness (a `/ Crew` folder), unfinished-split-ness (`STASH`). Members inside a block are listed in `sortedIndices`/`directMembers` order, not chain order.

---

## 4. Row content, with actual strings

`DrawRecordingRow` (`:1636-1980`), cell by cell.

**Enable checkbox** — `rec.PlaybackEnabled`, width 20 (`:1663`).

**`#`** — `(ri + 1).ToString()`, MiddleCenter, width 30 (`:1673`).

**Name** (`DrawRecordingNameCell`, `:1986-2055`) — `Space(2)` uniform nudge, then the tree indent, then either an inline `TextField` (rename mode) or a label-styled button whose text is `(treeConnector ?? "") + name` where `name = string.IsNullOrEmpty(rec.VesselName) ? "Untitled" : rec.VesselName`.

**Phase** — `RecordingStore.GetSegmentPhaseLabel(rec)` (`:1686`). Format is `"{bodyLabel} {segmentPhase}"`, e.g. `"Kerbin atmo"`, `"Jool approach"`, or a multi-body path `"Kerbin -> Jool"` when the trajectory crossed SOIs (`RecordingStore.SegmentLabels.cs:17-29`, `:135-142`). Empty for untagged recordings. Segment-phase vocabulary is `"surface"`, `"atmo"`, `"exo"`, `"approach"` (`SegmentPhaseClassifier.ClassifyFromValues`). Colors (`EnsurePhaseStyles`, `:653-671`):

| key | RGB | described in source as |
|---|---|---|
| `atmo` | (0.4, 0.7, 1) | blue |
| `exo` | (0.75, 0.55, 1) | light purple |
| `space` | (0.2, 1, 0.6) | lime green *(no phase string produces this key today)* |
| `approach` | (0.3, 0.8, 1) | cyan |
| `surface` | (1, 0.6, 0.2) | orange |

**Site** — `rec.LaunchSiteName ?? ""` (`:1705`).

**Launch** — `KSPUtil.PrintDateCompact(rec.StartUT, true)` if the recording has points, else the literal `"-"` (`:1709-1712`).

**Duration** — `FormatDuration(rec.EndUT - rec.StartUT)` → `ParsekTimeFormat.FormatDuration`: `"{n}s"` under a minute, `"{m}m {s}s"`, `"{h}h {m}m"`, `"{d}d {h}h"` / `"{d}d"`, `"{y}y {d}d"` / `"{y}y"` (`ParsekTimeFormat.cs:43-70`).

**Expanded stats** (only when Info is on, `:1720-1730`):

- MaxAlt / Dist — `FormatAltitude` / `FormatDistance`: `"{int}m"` / `"{x.x}km"` / `"{x.x}Mm"` (`RecordingsTableFormatters.cs:11-29`).
- MaxSpd — `"{int}m/s"` / `"{x.x}km/s"`.
- Pts — raw `pointCount`.
- Start — `FormatStartPosition` (`RecordingsTableFormatters.cs:35-53`). Priority: `"EVA from {parentVessel}"` → `"{launchSite}, {body}"` → `"{situation}, {biome}, {body}"` → `"{biome}, {body}"` → `"{body}"` → `"-"`.
- End — `FormatEndPosition` (`:62-111`). With a terminal state: `"Orbiting {body}"`, `"Docked, {body}"`, `"{endBiome}, {body}"` (Landed/Splashed), `"Destroyed, {body}"`, `"Recovered, {body}"`, `"SubOrbital, {body}"`, `"Boarded {parentVessel}"`. Without one: the segment phase label, else the body, else `"-"`.

**Status** (`:1732-1785`), a merged countdown + lifecycle + visual-kind cell:

- `now < StartUT` → future: `SelectiveSpawnUI.FormatCountdown(StartUT - now)` (`"T-1y 2d 3h 4m 5s"` shape, `ParsekTimeFormat.cs:107-154`), or the literal `"future"` if no points.
- `now <= EndUT && !TerminalStateValue.HasValue` → active: same countdown (renders as `T+…` since the delta is negative), or `"active"`.
- else past: `rec.TerminalStateValue.Value.ToString()` when set and not debris — one of `Orbiting`, `Landed`, `Splashed`, `SubOrbital`, `Destroyed`, `Recovered`, `Docked`, `Boarded` (`TerminalState.cs`) — else the literal `"past"`.
- Visual-kind override (`FormatRecordingVisualStatusText`, `:4437-4453`): `RecordingVisualKind.StaticPlaceholder` → `"static"`, `StationaryTail` → `"stationary"`. When a terminal state is already shown, the suffix `" still"` is appended instead (`GetTerminalVisualStatusSuffix`, `:4468-4478`) — so e.g. `"Landed still"`.
- Colors (`EnsureStatusStyles`, `:5773-5797`): future = white, active = green, past = grey (0.5,0.5,0.5), static = (1, 0.72, 0.25), stationary = the shared house cyan (0.65, 0.85, 1).
- Tooltip = visual-kind explanation + chain status, joined by newline (`CombineTooltipText`, `:4506-4513`). Visual-kind tooltips verbatim (`:4480-4491`):
  > `"Static placeholder: landed background continuation with a surface position and time range, but no playable trail. Kept visible for row controls."`
  > `"Stationary tail: terminal leaf with only stationary/coasting sections and no visual events. Kept visible because it may carry end-of-recording spawn state."`
- Chain status comes from `ParsekFlight.GetChainStatusForRecording` → `SpawnWarningUI.FormatChainStatus` (`SpawnWarningUI.cs:75-111`), one of: `"Ghosted -- spawns at UT={n}"`, `"Ghosted -- chain terminated"`, `"Spawn blocked -- waiting for clearance"`, `"Spawn blocked -- walkback exhausted, manual placement required"`, or `"Chain tip -- will spawn vessel at UT={n}"` (`ParsekFlight.cs:26545-26548`).

**Group cell** — `"G"`, or `"G"` + `"X"` split when `rec.IsGhostOnly && CanOfferGhostOnlyDelete(mode)` (mode != TrackingStation) (`:1789-1813`, `:4366-4369`).

**Loop checkbox** (`:1816-1862`) — blank cell when `ShouldSuppressRowLoopUi` (inside STASH, or `!Recording.IsLoopableRecording(rec)`, i.e. debris or a pure orbital coast). Greyed and commit-blocked when the recording's tree is route-bound, with tooltip `$"Looped by route: {RouteBindingTooltipName(rec)}"` (name falls back to the literal `"route"`, `:5401-5410`).

**Period cell** (`DrawLoopPeriodCell`, `:5455-5583`) — a `[value TextField][unit Button]` pair (unit button 40 px):

- Loop off → both controls greyed; value shows the effective number + unit suffix.
- `LoopTimeUnit.Auto` → greyed TextField showing the **global** value from `Settings > Looping` plus its suffix, unit button reads `"auto"`.
- Manual → editable TextField. When not focused it shows the runtime-**effective** cadence; on focus the buffer is reseeded from the stored raw value. `Enter` commits, click-outside commits (`HandleRecordingsDefocus`, `:994-1000`).
- When the runtime cadence differs from the stored value the text is tinted `LoopPeriodClampColor = (1.0, 0.8, 0.4)` (`:284`) and a clamp tooltip is shown in the bottom strip. Three verbatim forms (`BuildLoopPeriodClampTooltip`, `:5618-5677`):
  > `"Runtime cadence clamped to {0}s to keep concurrent cycles <= {1} (requested: {2}s, minimum period: {3}s, duration: {4}s)."`
  > `"Runtime cadence clamped to {0}s to keep concurrent cycles <= {1} (requested: {2}s, duration: {3}s)."`
  > `"Runtime cadence raised to {0}s because the minimum period is {1}s (requested: {2}s)."`
  > `"Runtime cadence repaired to {0}s from an invalid stored value (minimum period: {1}s)."`
- Unit button labels come from `ParsekUI.UnitLabel`: `"sec"`, `"min"`, `"hr"`, `"auto"`. Clicking cycles `sec → min → hr → auto → sec` (`CycleRecordingUnit`, `:5417-5421`). Suffixes used inside the value field are `"s"` / `"m"` / `"h"` (`UnitSuffix`, `:5412-5415`).

**Watch cell** (flight only, `:1881-1926`) — label `"W"`, or `"W*"` when this row is the watched one. Tooltips verbatim (`GetWatchButtonTooltip`, `:943-957`):
> `"Exit watch mode"` / `"Debris is not watchable"` / `"No active ghost - recording is in the past/future or has no trajectory points"` / `"Ghost is on a different body"` / `"Ghost is beyond the fixed 300 km watch range"` / `"Follow ghost in watch mode"`

Enabled iff `hasGhost && sameBody && inRange && !isDebris`, or already watching (`:906-915`).

**Rewind/Forward cell** (`DrawLegacyRewindForwardCell`, `:3026-3126`) — a single compact button, `"FF"` (`FastForwardActionLabel`, `:51`) or `"R"` (`RewindActionLabel`, `:50`), or a blank cell. Tooltips: `"Fast-forward to this launch"` / `"Rewind to this launch"`, or the disable reason from `RecordingStore.CanFastForward` / `CanRewind`.

**Re-Fly cell** (`DrawReFlyColumnCell`, `:3182-3196`) — three states from `ResolveReFlyColumnAction` (`:3463-3477`):
- `FlySeal` → twin buttons `"Fly"` + `"Seal"`. Tooltips: `"Re-fly this unfinished flight from the separation moment"` and `"Close this re-fly slot permanently without changing the recording"` (`:3279-3287`). Disabled variant shows both greyed with the block reason (`:3302-3322`).
- `StashSeal` → twin buttons `"Stash"` + `"Seal"`, tooltips `"Stash this stable Rewind Point slot in STASH so it can be re-flown later"` and `"Close this Rewind Point slot permanently without changing the recording"` (`:3354-3358`).
- `None` → blank cell.

**Archive checkbox** (`:1934-1968`) — writes `rec.Hidden` and invalidates the Timeline cache. **Refused for Unfinished Flights** with an on-screen message (`:1952-1961`):
> `$"Cannot hide '{rec.VesselName}' — it is an Unfinished Flight. Re-fly the rewind point or merge as Immutable to clear it from the list."`

---

## 5. Interactions — every control

### 5.1 Header
| Control | Action |
|---|---|
| `#` / `Name` / `Phase` / `Site` / `Launch` / `Duration` / `Status` | Set sort column; re-click flips direction; `InvalidateSort()` (`:1126-1134`, `:4371-4378`) |
| select-all enable toggle | `PlaybackEnabled = value` on **every committed recording** (`:1117-1123`) |
| select-all `Loop` toggle | `BulkSetLoopPlayback(all, applyAutoRange: false)` over *loopable* recordings; blocked entirely if any committed recording is route-bound (`:1187-1199`) |
| `Archive` toggle | `GroupHierarchyStore.HideActive = value` — the shared archive filter, also read by the Timeline (design 4.4) (`:1224-1228`) |

### 5.2 Leaf recording row
| Control | Action |
|---|---|
| enable checkbox | `rec.PlaybackEnabled` |
| Name single click | arms double-click timer (`DoubleClickThreshold = 0.3f`) |
| Name double click | enter inline rename; `Enter`/`KeypadEnter` commits, `Escape` cancels, click-outside commits (`:1997-2054`, `:4059-4072`). Empty/unchanged names are ignored. |
| `G` | open the Group Picker popup for this recording (`groupPicker.OpenForRecording(ri, mousePos)`) |
| `X` (ghost-only rows only) | queue `pendingDeleteGhostOnlyIndex`; next frame `DeleteGhostOnlyRecording` runs — **no confirmation dialog** ("ghost-only recordings are low-commitment", `:4321-4324`). In flight it routes through `ParsekFlight.DeleteGhostOnlyRecording` for ghost cleanup; in KSC through `RecordingStore.DeleteRecordingFull`. |
| `Loop` checkbox | `rec.LoopPlayback` + `ApplyAutoLoopRange` (on: auto-narrow to the "interesting" span; off: reset `LoopStartUT`/`LoopEndUT` to NaN, `:5428-5451`) |
| Period value field | edit + commit; negatives rejected, below-minimum clamped to `LoopTiming.MinCycleDuration` (`:5679-5725`) |
| Period unit button | cycle `sec → min → hr → auto` |
| `W` / `W*` | `flight.EnterWatchMode(ri)` / `flight.ExitWatchMode()` |
| `FF` | `ShowFastForwardConfirmation(rec)` → modal `"Confirm: Fast-Forward"`, message `"Fast-forward to just before \"{name}\" launch at {date}?\n\nTime will advance by {n} seconds."`, buttons `Fast-Forward` / `Cancel` (`:4251-4319`) |
| `R` | `ShowRewindConfirmation(rec)` → modal `"Confirm: Rewind"`, buttons `Rewind` / `Cancel` (`:4205-4249`). Message quoted in §7. |
| `Fly` | `RewindInvoker.ShowDialog(rp, slotListIndex)` |
| `Seal` | `UnfinishedFlightSealHandler.ShowConfirmation(rec)` |
| `Stash` | `UnfinishedFlightStashHandler.TryStash(rec, out reason)`; on failure a screen message `$"Cannot stash '{name}': {reason}"` (`:3391-3413`) |
| `Archive` checkbox | `rec.Hidden` + Timeline cache invalidation; refused for Unfinished Flights |

### 5.3 Group header row
| Control | Action |
|---|---|
| enable checkbox | set on all descendants |
| name single click | expand / collapse (`expandedGroups`) |
| name double click | inline rename; **blocked for permanent root groups** (`:2288-2293`). Commit routes through `MissionGroupLink.RenameMissionGroup` for a tree root group (renames the mission + `/ Debris` + `/ Crew` atomically), else `RecordingStore.RenameGroup` + `GroupHierarchyStore.RenameGroupInHierarchy` (`:4074-4126`). Invalid characters (`=`, `{`, `}`, newlines) and name collisions are rejected with a Warn. |
| `G` | open picker in **parent-group mode** (`OpenForGroup`) — single-select, titled `"Set Parent Group"` |
| `X` | `ShowDisbandGroupConfirmation` → modal `"Confirm: Disband Group"` (`:4154-4203`), message: `"Disband group \"{name}\"?\n\n{n} recording(s) will be {moved to \"parent\"|become standalone}.\n{n} sub-group(s) will {move under \"parent\"|become top-level}.\n\nNo recordings are deleted."`, buttons `Disband Group` / `Cancel` |
| `Loop` | bulk write over loopable descendants, `applyAutoRange: false` (so a user-customized loop window survives an off/on toggle, `:2383-2385`) |
| `W` | **rotates** through eligible descendants on repeated presses (bug #382, `:2433-2568`), with a per-group cursor. Tooltips: `"no watchable vessels in this group"`, `$"switch to {vesselName}"`, `"exit watch (no other watchable vessels)"`, `$"enter watch on {vesselName}"` |
| `FF` / `R` | Forward targets the group's main recording; Rewind scans descendants for the earliest launch-save owner ("a parent folder is an escalation surface for any child launch rewind", `:2571-2574`). Rewind tooltip: `$"Rewind to launch: {targetName}"` |
| `Archive` | set `Hidden` on all descendants **and** add/remove the group from the hidden set |

Group rows deliberately have **no** Period cell and **no** aggregate Re-Fly ("Stash/Fly/Seal remain per-recording because each action targets a specific RP slot", `:2618-2620`).

### 5.4 Chain / block header row
Enable toggle, expand/collapse (`expandedChains`), `G` (chain → `OpenForChain`, vessel block → `OpenForRecordings`), aggregate `Loop` (with `applyAutoRange: true`), `FF`/`R`. **No** X, no Period, no Watch, no Re-Fly, no Archive.

### 5.5 STASH header row
Only two interactive controls: the aggregate enable checkbox and expand/collapse. Everything else is a blank spacer cell.

### 5.6 Group Picker popup (`GroupPickerUI.cs`)

Rendered *outside* the recordings window to avoid scroll clipping (`:626-627`). Title is `"Manage Groups"`, or `"Set Parent Group"` in group-parent mode (`GroupPickerUI.cs:216`). Default rect 280×300, min 220×200, clamped on screen.

Contents: a scrollable checkbox tree (12 px indent per depth, `▶`/`▼` carets, `:322-341`), an optional leading `"(None / Root level)"` toggle in parent mode, a `[TextField][+]` new-group row, then `OK` / `Cancel` (both width 60). Multi-select for recordings/chains; single-select for group parenting (`ApplySelectionToggle`, `GroupPickerPresentation.cs:175-202`). Self and all descendants are omitted from the tree in parent mode (cycle prevention, `CycleInvalid` / `:315-316`).

`OK` applies a computed add/remove delta (`ComputeMembershipDelta`). Adds are gated by `CanAddToUserGroup` (`GroupPickerUI.cs:24-42`): an Unfinished Flight cannot be added to any manual group, and system groups are never valid targets. Rejection posts a screen message `"Cannot move STASH entries to manual groups"` (`:61-63`). For chains the rejection is all-or-nothing per target group; for a bulk recording selection it is per recording.

---

## 6. Basic vs Advanced for this window

Per `docs/dev/design-ui-basic-advanced.md` §4 and the code:

- **Basic hides the entire Recordings tab.** `UiSurface.TabRecordings` → `visibleInBasic = false` (`UiComplexityMode.cs:125`). The design table's rationale: *"The raw per-recording table (62 buttons, 13 toggles). Almost everything a normal player needs is expressed at the Mission level… This is the single largest complexity reduction available."*
- **Basic draws no toolbar at all**, not a one-button one: `VisibleTabCount(mode)` returns `TabLabels.Length` in Advanced and **0** in Basic (`:161-166`), with the trailing `Space(3)` separator going with it. Rationale in the XML doc: *"a `GUILayout.Toolbar` with a single entry is visual noise, and the window title ('Parsek - Missions') already carries the identity."*
- **Content dispatch is pinned**, not merely clamped: `int activeTab = VisibleTabCount(complexity) > 0 ? selectedTab : TabMissions;` (`:1392`).
- **Tab-index clamp**: `ClampTabIndexForMode` sends index 1 → 0 in Basic, Advanced passes through unchanged (`:176-182`). Applied both from the deferred mode-apply (`ClampTabForBasic`, `:190-193`) and defensively on draw (`:1371`), logged at Verbose when it actually moves.
- **The group picker is force-closed on Advanced → Basic** (`CloseGroupPickerForModeChange`, `:205-211`) because it is owned by this window, draws regardless of the selected tab, and would otherwise keep offering group mutations from an unreachable surface.
- **The window itself survives** the mode switch — it becomes the Missions window.
- Two documented consequences of hiding this tab (design §4.3, §4.4): (a) `Recording.PlaybackEnabled` has **no writer outside this tab** (per-row toggle, select-all, group aggregates, chain block), so retroactive per-recording playback-disable is Advanced-only in v1; (b) `Recording.Hidden` also has no writer outside this tab, which is why the Timeline gained its own `Archived` toggle bound to the same `GroupHierarchyStore.HideActive` state.
- Nothing else in this window reads the mode. Row content, columns, sorting and grouping are identical in both modes whenever the tab is drawn.

The one navigation API that targets this tab, `ScrollToRecording` (`:470-564`), has **no production caller** (documented decision, `:450-469`): the Timeline `GoTo` button routes to `ShowMissionForRecording` (`:416-428`) instead, so it works in Basic. `ScrollToRecording` un-archives the target recording, expands every ancestor group, un-hides hidden groups, and schedules the two-frame scroll.

---

## 7. Cross-references between recordings — what the UI does and does not show

Factual inventory. Each relationship field, and where (if anywhere) it surfaces.

| Relationship / field | Read by `RecordingsTableUI`? | Visible to the player? |
|---|---|---|
| `Recording.ParentAnchorRecordingId` (parent-anchored debris / controlled-decoupled children) | **No** — zero occurrences in the file | Not shown. It affects the row only indirectly, via `Recording.IsLoopableRecording` (`Recording.cs:1280`), which decides whether the Loop/Period cells render at all. |
| `TrackSection.anchorRecordingId` (per-section anchor for Relative frames) | **No** | Not shown. |
| `Recording.ParentRecordingId` | Only inside `ResolveParentVesselName` (`:4648-4661`), and only when `rec.EvaCrewName` is non-empty — a non-EVA parent link is never resolved | Surfaces only in the **expanded stats Start/End cells**, as `"EVA from {parentVessel}"` and `"Boarded {parentVessel}"`. Requires Info to be toggled on. |
| `Recording.DockTargetVesselPid` (dock partner) | No | Not shown. The End position cell says `"Docked, {body}"` — the *partner vessel is never named*. Affects `IsLoopableRecording` only. |
| `RecordingSupersedeRelation` rows | Yes, but **only as a visibility filter** (`IsSupersededForDisplay` / `IsInactiveForDisplay`, `:5927-5944`), plus `IsEffectiveReplacementForLaunchRewindOwner` (`:3578-3599`) to decide which row inherits the `R` button from a hidden owner | A superseded recording simply **disappears** from the table. No row, no marker, no "superseded by X" text, no way to see the relation existed. |
| `RecordingRewindRetirement` rows | Same — visibility filter only (`:5934-5944`) | Retired rows and their parent-anchored debris disappear. |
| `Recording.ChainId` | Yes — becomes a chain block | Visible only as *co-membership in one expandable block*. `ChainIndex` / ordering / which segment continued which is never rendered. |
| `Recording.TreeId` | Yes, three uses: display-block identity `treevessel:{TreeId}:{pid}` (`:4951-4952`), STASH tree filtering (`:2781`), route-binding lookup (`:5367`, `:5403`) | Indirectly: same-tree+same-vessel segments collapse into one block; the tree's auto folder name; the greyed loop tooltip `"Looped by route: {name}"`. The tree id itself is never displayed. |
| `RecordingTree.BranchPoints` / `BranchPointType` | **No** — never read | The topology of undocks, docks, EVAs, joint breaks, breakups and switch continuations is entirely invisible in this table. |
| `RecordingTree.RootRecordingId` / `ActiveRecordingId` | No | Not shown. The "main" recording of a folder is recomputed as *earliest non-debris descendant* (`FindAggregateMainRecordingIndex`), not read from the tree. |
| `Recording.IsDebris` | Yes, in several predicates | Visible only as membership in the auto `/ Debris` folder, plus a suppressed Loop cell and a non-watchable `W` button. Debris terminal state is deliberately not shown in the Status column (`:1753`). |
| `Recording.EvaCrewName` | Yes (phase-label suppression, `ResolveParentVesselName`) | Visible as membership in the auto `/ Crew` folder, and as the `"EVA from …"` start position. The kerbal's name itself is not a column. |
| `Recording.VesselPersistentId` | Yes — half of the display-block identity | Not displayed as a value. |
| `Recording.RecordingId` | Yes (cache keys, cross-link matching, STASH member matching) | Never displayed. It appears only in log lines and in some screen messages' fallbacks. |
| Rewind-Point child slots (`RewindPoint` / `ChildSlot.OriginChildRecordingId`) | Yes — resolves which row gets `Fly`/`Seal`/`Stash` (`:3607-3665`, `:3801-3804`) | Visible only as the presence of those buttons and their disable reasons (e.g. `"Rewind point slot not found"`, `"rewind slot disabled: {reason}"`). The slot index and the RP id are log-only. |
| Ghost chain relationships (live playback) | Yes — `ParsekFlight.GetChainStatusForRecording` | The **only place** a runtime cross-recording relation is worded for the player, and only in the Status column's hover tooltip: `"Chain tip -- will spawn vessel at UT={n}"`, `"Ghosted -- spawns at UT={n}"`, etc. |

The single place where a parent/branch relationship is stated in plain language is the **Rewind confirmation modal** (`ShowRewindConfirmation`, `:4205-4249`). When the clicked row is not the rewind-save owner, the message inserts a branch note:

```
Rewind to "{owner.VesselName}" launch at {launchDate}?
(from branch "{rec.VesselName}")

{n} recording(s) after this launch will replay as ghosts.

This flight replays as a ghost; its recorded vessel reappears at the end if you do not re-fly it. Watch the ghost to bring it back.

Any uncommitted progress will be lost.
```

(`branchNote = $"\n(from branch \"{rec.VesselName}\")"` at `:4218-4220`; `futureText = $"\n\n{futureCount} recording(s) after this launch will replay as ghosts."` at `:4212-4215`.)

Related: the `R` button is *suppressed* on non-owner tree branches so the table does not draw several buttons that all rewind to the same root launch (`ShouldShowLegacyRewindButton`, `:3512-3527`; suppression logged at `:3100-3125` with the reason string `"suppressed — tree branch, use root recording's Rewind button"`). The player sees an empty Rewind cell with no explanation of why.

---

## 8. Miscellaneous mechanics worth knowing

- **Sorting is memoized on count only.** `RebuildSortedIndices` returns early when `sortedIndices != null && lastSortedCount == committed.Count` (`:4385-4406`); a re-sort happens only when the count changes or a header click calls `InvalidateSort()`. `SortColumn.Index` is implemented as "identity order, reversed when descending" (`:4395-4400`), and `CompareRecordings` returns 0 for it, relying on `Array.Sort` stability (`:4580-4582`).
- **Two independent expansion sets**: `expandedGroups` (groups + STASH) and `expandedChains` (chain and vessel blocks) (`:67-68`). Neither is persisted.
- **Rename is deferred by one frame** to avoid IMGUI layout mismatch (`:84-93`); only one rename (recording or group) is active at a time and starting a new one commits the old.
- **Alignment debug instrumentation** is armed on style init (`ArmAlignmentDebug`, `:1011-1019`) and dumps actual header/row rects once at Info level (`AlignDbg HEADER:` / `AlignDbg ROW:`).
- The whole file carries heavy per-cell layout compensation constants (`NameColumnLeadGap = 5f`, `BodyCellTextIndent = 5`, `BodyCellButtonLeftInset = 10f`, zero-margin style clones) whose only purpose is keeping body cells aligned under the fixed header — see the comment blocks at `:326-365`.
- **Transition logging** is pervasive: watch-button enable/disable (Info level, keyed by `RecordingId`), Forward/Rewind enable/disable (Verbose, keyed by row index), Re-Fly slot invocability (Verbose, keyed by `rp/slot`), tree-branch rewind suppression, and route-guard blocks (`RouteGuard` subsystem).
- `GetCachedBudget()` (`:569-572`) always returns `default(BudgetSummary)` — `cachedBudget` (`:377`) is never assigned in this file, yet `ParsekUI.GetCachedBudget()` forwards to it and `TimelineWindowUI` consumes the result.
