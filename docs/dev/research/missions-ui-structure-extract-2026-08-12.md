# Missions UI — Structural Extraction

Factual extraction of the Parsek Missions UI for downstream UX analysis. No critique, no
recommendations. All line references are `file:line` against the working tree at
`/home/user/Parsek` (2026-08-12).

Primary sources read in full: `Source/Parsek/UI/MissionsWindowUI.cs` (2853 lines),
`Source/Parsek/Mission.cs`, `MissionStore.cs`, `MissionStructure.cs`, `MissionComposition.cs`,
`MissionThroughLine.cs`, `MissionSelection.cs`, `MissionGroupLink.cs`,
`MissionIntervalSelection.cs`, `MissionCrossTreeDock.cs` (link type + FindLinks),
`UI/UiComplexityMode.cs`, plus the host-window seams in `UI/RecordingsTableUI.cs` and
`ParsekUI.cs`, and the designs `docs/dev/design-mission-abstractions.md` and
`docs/dev/design-ui-basic-advanced.md`.

---

## 0. Where this surface lives

The Missions UI is **not a standalone window**. It is the body of the "Missions" tab inside
the window the player opens with the main-window `Missions` button.

- Host window: `RecordingsTableUI`, titled **`"Parsek - Missions"`**
  (`UI/RecordingsTableUI.cs:614`). The host owns the window rect, drag, resize handle,
  input lock, and the bottom `Close` button.
- Tab bar: `TabLabels = { "Missions", "Recordings" }`, `TabMissions = 0` (first + default),
  `TabRecordings = 1` (`UI/RecordingsTableUI.cs:127-130`). Rendered as
  `GUILayout.Toolbar` (`:1381`) followed by `GUILayout.Space(3)`.
- Dispatch: `if (activeTab == TabMissions) { parentUI.GetMissionsUI().DrawMissionsTabContent(); DrawMissionsTabBottomBar(); return; }`
  (`UI/RecordingsTableUI.cs:1394-1399`).
- Bottom bar for this tab: `FlexibleSpace` → `Close` button → resize handle → `GUI.DragWindow()`
  (`UI/RecordingsTableUI.cs:1296-1312`).
- Entry point for the tab body: `MissionsWindowUI.DrawMissionsTabContent()`
  (`UI/MissionsWindowUI.cs:520`).
- Class doc for the whole surface: `UI/MissionsWindowUI.cs:7-19`.

The tab reuses RecordingsTableUI's *visual style and helpers* (column-header style, dark
table-body box, tree-connector glyphs, carets, section-header bar) without inheriting it;
the styles are rebuilt verbatim in `EnsureStyles()` (`UI/MissionsWindowUI.cs:291-372`).

---

## 1. Window layout, top to bottom

### 1.1 Draw order

`DrawMissionsTabContent` (`UI/MissionsWindowUI.cs:520-677`):

1. **Style init** + two click-away commit checks (rename field, loop-period field)
   (`:524-540`).
2. **Deferred cross-link work**: consume a queued Archive-filter clear on a Layout pass
   (`:545-554`).
3. **Seed missions**: `MissionStore.EnsureDefaultsForTrees(RecordingStore.CommittedTrees)`
   (`:557`) — every committed tree gets a default Mission if it has none.
4. **Empty state**: if there are no missions at all, the whole body is one label —
   `"No missions recorded yet."` (`:566`) — and nothing else draws.
5. **Fixed column-header row** (outside the scroll view), `DrawColumnHeader()` (`:572`, body
   at `:2776-2850`).
6. **Deferred cross-link scroll apply** (must precede `BeginScrollView`) (`:576-596`).
7. **Scroll view** (`vertical scrollbar always shown`, `:604`) wrapping a dark
   `tableBodyBoxStyle` vertical (`:608`).
8. **Per mission, in sort order** (`:619-664`):
   a. skip if the mission's tree is missing (`:622-623`);
   b. skip if `MissionStore.HideArchived && mission.Archived` (`:628-629`);
   c. `DrawMissionHeader(...)` — one dark full-width header bar (`:642`);
   d. `DrawCompositionNode(...)` for each composition root — the vessel rows, recursively
      (`:648-658`);
   e. `DrawForeignDockLinkRows(...)` — cross-tree "Partner journey" rows (`:663`).
9. End scroll view; drop any unconsumed reveal target; rate-limited Verbose summary
   `"Missions tab: missions={n} rows={m}"` (`:666-676`).

### 1.2 Columns (left → right)

Header cells, `DrawColumnHeader` (`:2776-2850`):

| # | Header label | Width const | Value | Sortable |
|---|---|---|---|---|
| 1 | *(blank)* + `#` | `ColW_Enable=20` + `ColW_Index=30` (+8 container) | blank enable slot; then mission index / include checkbox | `#` sorts by `MissionSortColumn.Index` (`:2797`) |
| 2 | `Missions and vessels` | expanding | mission name (header row) / vessel-composition label (vessel rows) | sorts by `MissionSortColumn.Name` (`:2804`) |
| 3 | `TTL` | `ColW_TMinus=90` | time-to-launch countdown, **launch row only** (`:2811`) | no |
| 4 | `Start time` | `ColW_StartTime=120` | `KSPUtil.PrintDateCompact(node.StartUT, true)` | sorts by `MissionSortColumn.StartTime` (`:2813`) |
| 5 | `Start event` | `ColW_StartEvent=85` | `node.StartEvent` | no |
| 6 | `End event` | `ColW_EndEvent=85` | `node.EndEvent` | no |
| 7 | `End time` | `ColW_EndTime=120` | `KSPUtil.PrintDateCompact(node.EndUT, true)` | no |
| 8 | `Re-Fly` | `ColW_ReFly=90` | `Fly`/`Seal` (or `Stash`/`Seal`) for unfinished flights | no |
| 9 | `Archive` + global toggle | `ColW_Archive=80` | per-mission Archive checkbox (header row only) | no |
| — | scrollbar gutter | 16 (or skin width) | reserved so header aligns with rows (`:2843-2847`) | — |

Header cell height is pinned at `ColHeaderHeight = 32` for every cell (`:188`, applied at
each `GUILayout.Height(ColHeaderHeight)`). Sort arrows are `" ▲"` (asc) / `" ▼"`
(desc) appended to the label (`:2795-2796`, and `ParsekUI.DrawSortableHeaderCore`
`ParsekUI.cs:1456-1457`).

Note: the code comments call column 3 "Time to launch" (`:181-184`, `:1902`, `:2807`), but the
rendered header string is the three-letter `"TTL"` (`:2811`).

### 1.3 Mission header bar (one per mission)

`DrawMissionHeader` (`:1251-1403`). The **entire row** is one dark section-header bubble
(`missionHeaderRowStyle`, `:1256`), spanning index → Archive. Contents in order:

1. blank enable cell (`ColW_Enable`) (`:1260`);
2. **index number** — per-tree 1-based index, bold, non-editable, shared by clones
   (`:1263-1264`);
3. `GUILayout.Space(BodyCellTextIndent /*5*/)` then the **mission title** — a bold
   transparent-background button that expands to fill the name column; double-click enters
   inline rename (`:1269-1270`, `DrawMissionTitleOrRename` `:1409-1463`). Empty name displays
   as `"(mission)"` (`:1411`);
4. a fixed-width right-side control block (`MissionHeaderRightBlockWidth`, `:170-175`)
   containing, left→right:
   - `Log` button (`:1281`) → `parentUI.OpenStructureWindowForMission(mission.TreeId, mission.Name)`;
   - `Clone` (`:1291`);
   - `Delete` (`:1294`), wrapped in `GUI.enabled = MissionStore.CanDelete(mission)` (`:1293`);
   - `Warp to...` (`:1532`, drawn by `DrawMissionWarpToWindowButton` `:1519`);
   - the word `Loop` (`:1321`) then a bare checkbox (`:1322`);
   - **conditionally** the label `Looped by route` with tooltip `"Looped by route: {routeName}"`
     (`:1330-1332`) when `RouteTreeGuard.RouteBindingFor(mission.TreeId, ...)` is true; the
     Loop toggle is then rendered disabled (`:1317-1323`);
   - the **loop-period cell** (`DrawMissionLoopPeriodCell` `:2540-2706`), content-sized;
   - `GUILayout.FlexibleSpace()` (`:1368`) — right-pins what follows;
   - `Watch` / `W*` button (`DrawMissionWatchButton` `:1470-1508`);
   - `Rewind` / `Forward` button (delegated to
     `RecordingsTableUI.DrawMissionRewindForwardButton`, `:1382-1384`);
   - **Archive checkbox**, centered in an 80px cell at the row's right edge (`:1389-1393`).

All of `Log`, `Clone`, `Delete`, `Warp to...`, `Watch`, `Rewind`/`Forward` share
`ColW_HeaderButton = 70` so they read as one button group (`:156-158`).

The header bar carries **no** Start/End time, no duration, and a blank TTL slot — the period
label occupies that horizontal space instead (`:1357-1362`, `MissionHeaderPeriodSlack`
`:159-166`).

### 1.4 Composition (vessel) rows

`DrawCompositionRow` (`:881-1023`). Row min height `CompositionRowMinHeight = 22` (`:888`).
Cell order:

1. blank enable cell (`:893`) — "no per-row enable in missions" (`:890-892`);
2. **include checkbox** in the `#` slot for selectable nodes; blank cell for roster atoms
   (`:897-921`);
3. optional grey-out: `GUI.color = DimColor` (white @ 0.45 alpha, `:224`) when the row is
   excluded or its owning interval is (`:923-925`);
4. indent `RecordingsTableUI.SelfConnectorIndent(depth)` = `max(0, depth-1) * connectorWidth`
   (`RecordingsTableUI.cs:2103-2106`), then **connector glyph** `├─ ` / `└─ `
   (`RecordingsTableUI.cs:2062-2065`, U+251C/U+2514 + U+2500 + space), then **caret**
   `▼ ` (expanded) or `▶ ` (collapsed) when the node has children (`:276-279`, `:931`), then
   the label;
5. name cell: a `Button` when the node has children (click toggles collapse), otherwise a
   `Label` (`:939-951`);
6. TTL cell — the live countdown **only on the launch row**, otherwise a blank 90px cell
   (`:962-972`);
7. `Start time`, `Start event`, `End event`, `End time` — populated for intervals/branches,
   all four blank for roster atoms (`:976-989`);
8. Re-Fly cell — `parentUI.GetRecordingsTableUI().DrawReFlyColumnCell(...)` when the node's
   `HeadLegId` resolves to a committed recording, else blank (`:1001-1010`);
9. blank margin-0 trailing cell under the Archive column (`:1018-1020`).

Colour is restored to normal before the Re-Fly and Archive cells so an excluded (greyed)
interval's `Fly`/`Seal` buttons are not dimmed (`:991-994`).

### 1.5 The row-label format strings (quoted)

**Vessel / interval row name** (`:934-936`):

```csharp
string label = (!string.IsNullOrEmpty(node.VesselName) && node.VesselName != node.CompositionLabel)
    ? node.VesselName + " (" + node.CompositionLabel + ")"
    : node.CompositionLabel;
string wide = connector + caret + label;
```

**Composition label** (`MissionComposition.cs:611-629`) — `"pod x1, probe x1, crew x3"`;
categories with count 0 are omitted; empty → `"(no controllers)"`; an EVA-kerbal leg renders
the kerbal name instead of counts:

```csharp
AppendCount(sb, "pod", leg.PodCount);
AppendCount(sb, "probe", leg.ProbeCount);
AppendCount(sb, "seat", leg.SeatCount);
AppendCount(sb, "crew", leg.CrewCount);
...
sb.Append(label).Append(" x").Append(n.ToString(CultureInfo.InvariantCulture));
```

**Roster atom labels**: `"Pod"`, `"Probe"`, `"Seat"` (one row per unit,
`MissionComposition.cs:670-681`), then one row per named kerbal
(`leg.CrewNames[i]`), or a single `"crew x" + N` atom when names are absent
(`MissionComposition.cs:641-668`). Atoms have `VesselName == CompositionLabel`, so they render
as the bare label.

**Partner-journey row name** (`:1116-1119`):

```csharp
RecordingsTableUI.TreeConnector(li == links.Count - 1)
+ $"Partner journey - {link.ForeignVesselName}"
```

(the comment at `:1070` describes the label as `"Partner journey - <vessel> (docked <date>)"`,
but the date is rendered in the `Start time` column and the word `Docked`/`Boarded` in
`Start event`, `:1112`, `:1121-1123`.)

**Event words** (`MissionComposition.BranchEventName`, `MissionComposition.cs:706-729`) —
cause wins over type: `"Decoupled"`, `"Undocked"`, `"Crashed"`, `"Overheated"`, `"Broke up"`,
then by type `"Undocked"`, `"EVA"`, `"Docked"`, `"Boarded"`, `"Broke off"`, `"Broke up"`,
`"Launch"`, `"End"`, `"Switch"`.
**Terminal words** (`TerminalName`, `MissionComposition.cs:731-746`): `"Orbiting"`,
`"Landed"`, `"Splashed"`, `"Suborbital"`, `"Destroyed"`, `"Recovered"`, `"Docked"`,
`"Boarded"`.
The first interval's `StartEvent` is the string passed in by the builder — `"Launch"` for a
root (`MissionComposition.cs:102`) or the peel's `OriginEventName` for a branch
(`MissionComposition.cs:358`, `:368`).

### 1.6 Tooltips

Only three tooltip surfaces exist on this tab:

- TTL cell: the joined amber reasons (D3 drift / M4c arrival), `JoinAmberReasons`
  (`:1940-1944`, `:2008-2017`).
- `Looped by route` label: `"Looped by route: {routeName}"` (`:1331`).
- Buttons drawn by the borrowed RecordingsTableUI cells: `Forward` →
  `"Fast-forward to this launch"` / the disabled reason; `Rewind` → `"Rewind to this launch"` /
  the reason (`RecordingsTableUI.cs:3150`, `:3166`); `Fly` →
  `"Re-fly this unfinished flight from the separation moment"` / reason; `Seal` →
  `"Close this re-fly slot permanently without changing the recording"`
  (`RecordingsTableUI.cs:3277-3287`).

Mission title, index, include checkboxes, Log/Clone/Delete/Warp to.../Loop/Watch/Archive
carry no tooltips.

### 1.7 ASCII mockup of a typical rendered window

Invented content: two missions on two trees, the first with a booster decouple, a lander
peel, an EVA, and a terminal atom expansion; the second looping and phase-locked with a
cross-tree partner journey included. Widths compressed for readability; `[x]`/`[ ]` are
checkboxes.

```
┌ Parsek - Missions ─────────────────────────────────────────────────────────────────────────────────────────────┐
│ ┌────────────┬─────────────┐                                                                                   │
│ │  Missions  │  Recordings │        <- tab bar (Advanced only; Basic draws no toolbar at all)                   │
│ └────────────┴─────────────┘                                                                                   │
│  #   Missions and vessels                     TTL      Start time     Start event  End event   End time   Re-Fly  Archive
│                                                                                                                  [x]    │
│ ┌──────────────────────────────────────────────────────────────────────────────────────────────────────────────┐ ▲ │
│ │ 1  Kerbal X   [Log][Clone][Delete][Warp to...] Loop [ ]  [30   ][sec]              [Watch][Rewind]      [ ] │ █ │
│ │[x] └─▼ Kerbal X (pod x1, probe x1, crew x3)              Y1, D12 3:20:11  Launch     Decoupled  Y1, D12 3:22:40      │
│ │[x]    ├─▼ Kerbal X (pod x1, crew x3)                     Y1, D12 3:22:40  Decoupled  Undocked   Y1, D14 1:07:55      │
│ │[x]    │  ├─▼ Kerbal X (pod x1, crew x2)                  Y1, D14 1:07:55  Undocked   Landed     Y1, D14 6:41:02      │
│ │[x]    │  │  ├─  Bob Kerman                               Y1, D14 2:15:00  EVA        Recovered  Y1, D14 2:44:18      │
│ │       │  │  ├─  Pod                                                                                                  │
│ │       │  │  ├─  Jebediah Kerman                                                                                      │
│ │       │  │  └─  Valentina Kerman                                                                                     │
│ │[x]    │  └─▼ Kerbal X Lander (probe x1)                  Y1, D14 1:07:55  Undocked   Landed     Y1, D14 3:12:44      │
│ │       │     └─  Probe                                                                                                │
│ │[ ]    └─  Kerbal X Booster (probe x1)                    Y1, D12 3:22:40  Decoupled  Destroyed  Y1, D12 3:31:09      │
│ │                                                                                                                      │
│ │ 2  Munport Resupply [Log][Clone][Delete][Warp to...] Loop [x]  ~6.4d (Mun window)  [W*][Forward]         [ ] │   │
│ │[x] └─▼ Munport Tug (pod x1, crew x1)          T- 2d 4h   Y2, D31 8:02:00  Launch     Docked     Y2, D33 0:19:30      │
│ │[x]    └─▼ Munport Tug (pod x2, probe x1, crew x4)        Y2, D33 0:19:30  Docked     Undocked   Y2, D35 4:50:12  Fly Seal│
│ │[x]       └─  Munport Tug (pod x1, crew x1)               Y2, D35 4:50:12  Undocked   Orbiting   Y2, D36 9:11:44      │
│ │[x] └─  Partner journey - Munport Station                 Y2, D33 0:19:30  Docked                                     │
│ │[x]       └─▼ Munport Station (pod x2, probe x2, crew x5) Y2, D33 0:19:30  Docked     Undocked   Y2, D35 4:50:12      │
│ │                                                                                                              ▼ │
│ └──────────────────────────────────────────────────────────────────────────────────────────────────────────────┘   │
│  [                                   Close                                    ]                                ◢ │
└────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

Reading notes on the mockup, all mechanical consequences of the code:

- Each successive structural interval of the *same* vessel is a **child** of the previous one
  (`MissionComposition.cs:346-347`), so a multi-stage vessel renders as a right-drifting
  staircase, one indent level per interval boundary, all rows carrying the same vessel name
  with a shrinking composition.
- Peeled pieces (booster, lander) and EVA kerbals are **siblings after** the survivor
  interval in the same children list (`MissionComposition.cs:351-371`).
- Roster atoms only appear when the last interval has no other children
  (`MissionComposition.cs:375-382`), and a single-atom terminal is a leaf instead
  (`IsSingleAtom`, `MissionComposition.cs:601-607`).
- The `TTL` value appears exactly once per mission, on the first rendered composition row
  (`:653-657`, `:962-968`).
- Excluded rows (`[ ]`, e.g. the Booster) render dimmed via `DimColor`.

---

## 2. The hierarchy model

### 2.1 Levels actually rendered

```
Mission (header bar; one row per Mission object)
└── composition root #1 .. #N   (depth 1; one per MissionStructure.RootLegIds)
    └── interval chain of that physical vessel  (depth+1 per interval boundary)
        ├── next interval of the same vessel      (survivor; first child)
        ├── peeled branch through-line            (probe / lander / undocked partner) → recurses
        ├── EVA-kerbal through-line               (crew peel) → recurses
        └── roster atoms (Pod / Probe / Seat / <kerbal name>)   [only at a bare terminal]
└── "Partner journey - <foreign vessel>" affordance rows (depth 1)
    └── maximal foreign journey nodes (depth 2+), with their own intervals + atoms
```

There is no explicit "vessel" level distinct from "interval": a vessel is the set of rows
sharing an `OwnerHeadId`, chained by nesting.

### 2.2 What determines nesting

Three derivation stages, all pure, rebuilt per frame and cached by `Time.frameCount`
(`:706-774`):

1. **`MissionStructureBuilder.Build(tree)`** → `MissionStructure`
   (`MissionStructure.cs:122-217`). Nodes = controlled recordings only; `IsDebris` recordings
   are excluded outright (`MissionStructure.cs:139-143`). Edges: within-run *sequence* links
   from shared `(ChainId, ChainBranch)` ordered by `ChainIndex` (`:258-291`), and cross-run
   *branch* links from `RecordingTree.BranchPoints` (`:331-392`). Roots = legs with no
   sequence predecessor and no branch parent (`:190-200`).
2. **`MissionThroughLineBuilder.Build(structure)`** → `MissionThroughLineView`
   (`MissionThroughLine.cs:38-122`). Collapses legs into per-vessel through-lines by walking
   `ContinuationSuccessor` (env-split next, else the branch child marked
   `IsBranchContinuation`, else the first non-anchored non-EVA child;
   `MissionThroughLine.cs:124-149`). Everything that is *not* the continuation becomes an
   `OffshootHeadIds` child.
3. **`MissionCompositionBuilder.Build(structure)`** → `List<MissionCompositionNode>`
   (`MissionComposition.cs:92-112`). This is what the rows are drawn from. It splits each
   through-line into **intervals**:
   - a **structural peel** (a controller separating: decouple / undock / probe release) ends
     an interval and starts the survivor's (`MissionComposition.cs:229-239`);
   - a **crew peel** (a kerbal going EVA) does *not* end an interval; the kerbal hangs off the
     interval it left during (`MissionComposition.cs:363-371`);
   - a **Dock / Board merge** on the continuing line subdivides the structural interval
     (M-MIS-5 D1, `MissionComposition.cs:180-291`); sub-interval keys after the first are
     `"<parentKey>@dockM"` (`MissionComposition.cs:286`), while structural sub-segments key as
     `"<headLegId>/segN"` (`MissionComposition.cs:267-269`).

### 2.3 How the docked composition, branches, debris and crew map onto rows

| Data-model thing | Row treatment |
|---|---|
| Docked composition (Dock/Board merge) | A new interval on the continuing vessel's line, `StartEvent = "Docked"`/`"Boarded"`, whose label **rebases** to the merge leg's own combined composition (`MissionComposition.cs:401-496`); the partner's own pre-dock line is *not* pulled in (it lives in its own root / tree) |
| Branch (fork / peel) | A sibling child row under the interval it separated from, recursing into its own interval chain (`MissionComposition.cs:351-361`) |
| Debris (`IsDebris = true`) | **Never a row.** Excluded at `MissionStructure.cs:139-143`; rides its parent at playback |
| Crew, as a vessel property | Folded into the interval label as `crew xN` (surviving roster at the interval's *end*, `MissionComposition.cs:478-479`) |
| Crew, as EVA | A child through-line row labelled with the kerbal's name (`MissionComposition.cs:632-635`, `:696`) |
| Crew, at a bare terminal | One named atom row per kerbal (`MissionComposition.cs:647-655`) |
| Cross-tree dock (foreign vessel) | A `Partner journey - X` row plus, when included, the foreign journey's interval rows (`:1075-1216`) |

### 2.4 Collapse state

`collapsedLegs` is a transient `HashSet<string>` keyed `"missionId:headLegId"`
(`:93`, `CollapseKey` `:2768-2771`), so two Missions over one tree collapse independently.
Not persisted. Clicking the name cell of any row with children toggles it (`:939-946`).

---

## 3. What a Mission IS in the data model

### 3.1 `Mission` fields (`Mission.cs:13-105`)

| Field | Meaning |
|---|---|
| `Id` | GUID (`"N"` format), assigned on creation / on load when absent |
| `TreeId` | the one `RecordingTree` this Mission is a selection over |
| `Name` | display name; **not** required to be unique |
| `ExcludedThroughLineHeadIds` | legacy coarse selection: excluded through-line heads, cascading down offshoots. **The Missions window never writes this set** (noted at `:2317`, `:2474-2475`) |
| `ExcludedIntervalKeys` | the live selection: excluded composition-interval keys (`MissionCompositionNode.HeadLegId`), **no cascade**. Also holds excluded *foreign* journey keys (keys are recording-GUID-rooted, hence globally unique) |
| `IncludedForeignDockLinkIds` | included cross-tree dock links, each id a foreign `BranchPoint.Id`. Default empty (off) |
| `LoopPlayback` | mission-level loop on/off |
| `LoopIntervalSeconds` | launch-to-launch period; default is the "untouched" sentinel |
| `LoopTimeUnit` | display unit `Sec`/`Min`/`Hour`/`Auto`, persisted explicitly |
| `LoopAnchorUT` | UT the loop was last enabled at (NaN = unset); the span clock phases from here |
| `Archived` | list-management flag only; explicitly does not change looping or ghost playback (`Mission.cs:59-63`) |
| `SelectionSchemaGeneration` | 0 = pre-M-MIS-5 selection semantics, 1 = current; the one-time `@dock` upgrade seam |

`Save`/`Load` at `Mission.cs:107-189`; `foreignDockLink` values are written sparsely and
sorted (`Mission.cs:130-137`). Persistence is driven by `MissionStore.Save`/`Load`
(`MissionStore.cs:659-686`), which also persists `missionHideArchived`.

### 3.2 Mission ↔ RecordingTree

- **1 tree : N missions.** `MissionStore.EnsureDefaultsForTrees` creates an all-included
  default for any committed tree that has none, naming it from
  `tree.AutoGeneratedRootGroupName`, falling back to `tree.TreeName`, then `"Mission"`
  (`MissionStore.cs:42-67`).
- **"Main" vs clone (the only sub-mission concept present).** The *original* / main mission of
  a tree is the **first** mission in list order for that tree
  (`FindOriginalMission` `MissionStore.cs:625-634`, `IsOriginalMission` `:638-641`). Clones are
  inserted directly after their source (`Clone` `:573-590`). `CanDelete` returns false for the
  original, so a tree always keeps at least one mission (`:596-608`). There is no other
  parent/child relation between Missions — no nesting of missions inside missions.
- Orphan pruning (`PruneOrphans` `:81-100`) and stale-selection reconcile
  (`ReconcileSelections` `:132-349`) run at load; the reconcile drops excluded ids that no
  longer resolve and warns.
- One loop per tree (and per *spanned* tree set once cross-tree links are included):
  `SetLoopEnabled` (`:418-443`) → `ClearLoopsConflictingWith` (`:454-493`);
  `NormalizeOneLoopPerTree` (`:544-571`) enforces it after load.

### 3.3 The through-line concept (`MissionThroughLine.cs`)

A **through-line** is one continuous controlled *vessel*, merging all its legs — env-split
continuations plus the vessel-continuation child at each branch point — into a single entry
(`MissionThroughLine.cs:5-23`). Fields: `HeadLegId`, `TailLegId`, `VesselName`, `StartUT`,
`EndUT`, `MemberLegIds`, `OffshootHeadIds`. `MissionThroughLineView` indexes them
`ByHeadId` with `RootHeadIds` as entry points.

The Missions window uses the view for four things only: the mission span start for sorting
(`MissionSpanStartUT` `:1639-1648`), the mission's root/launch recording for Rewind/Forward
(`ResolveMissionRootRecordingIndex` `:2501-2532`), the watch-target resolution
(`ResolveMissionWatchTarget` `:2461-2493`), and periodicity extraction (`:1767-1772`). The
**rows** come from the composition model, not the through-line view.

`MissionSelection.ComputeIncludedHeadIds` (`MissionSelection.cs:23-58`) is the cascading
head-level include rule (exclude a head → its offshoots go too). It is documented as the
shared cascade but is not what the window's checkboxes bind to; the checkboxes bind to
`ExcludedIntervalKeys`, which is explicitly non-cascading
(`MissionIntervalSelection.cs:13-21`, `:846-857`). `MissionIntervalSelection.ComputeRenderWindows`
turns the interval selection into one `[min included start, max included end]` **render
window per vessel** (`MissionIntervalSelection.cs:35-82`).

### 3.4 Naming / renaming (`MissionGroupLink.cs`)

The main mission's name and the tree's Recordings-window root folder are **the same
abstraction shown twice**, kept in lockstep:

- `MissionsWindowUI.CommitMissionRename` (`:2404-2441`): if `MissionStore.IsOriginalMission`,
  route through `MissionGroupLink.RenameMissionGroup(tree, newName, out reason)`; otherwise
  (a clone) rename the mission alone via `MissionStore.RenameMission`.
- `RenameMissionGroup` (`MissionGroupLink.cs:57-150`) atomically renames: the root group
  (recording tags + tree field + hierarchy), the auto `"<root> / Debris"` and `"<root> / Crew"`
  subgroups **only if still in derived form**, and `Mission.Name`. It refuses the whole
  rename on empty name, invalid characters, a reserved/permanent root name, or any group-name
  collision (`:65-79`, `:125-130`), logging
  `"Mission rename rejected: '{newName}' ({reason})"` on the UI side (`:2424`).
- Mission names need not be unique; the *group* side enforces uniqueness
  (`MissionStore.cs:646-655`).

---

## 4. Interactions — every click and what it does

### 4.1 Column headers

| Control | Effect |
|---|---|
| `#` header button | sort by tree index; re-click flips direction (`:2797-2802`) |
| `Missions and vessels` header | sort by mission name, `OrdinalIgnoreCase` (`:2804-2805`, compare at `:2386-2389`) |
| `Start time` header | sort by mission span start (`:2813-2814`) |
| `Archive` header checkbox | toggles the global `MissionStore.HideArchived`; archived missions drop out of the list entirely (`:2831-2838`) |

Sort tiebreak: tree index, then original list position, so a clone stays adjacent to its
source (`CompareMissionRows` `:2378-2401`). Default `sortColumn = Index`, ascending (`:211-212`).

### 4.2 Mission header bar

| Control | Effect | Enable rule |
|---|---|---|
| Mission title (single click) | arms double-click detection (`:1444-1461`) | always |
| Mission title (double click, <0.3 s) | enter inline rename; Enter commits, Escape cancels, click-away commits (`:1415-1437`, `:526-531`) | always |
| `Log` | opens `StructureListWindowUI` for this tree with the mission name as the title (`:1281-1286`, `StructureListWindowUI.OpenForMission`) | always |
| `Clone` | `MissionStore.Clone(mission)` — duplicates the definition (selection + loop settings + archived + schema gen), names it `"<Name> copy"`, inserts after the source (`:1291-1292`, `Mission.cs:87-105`) | always |
| `Delete` | `MissionStore.Delete(mission)` | disabled unless `CanDelete` (i.e. not the tree's original) (`:1293-1296`) |
| `Warp to...` | confirmation dialog `"Confirm: Warp to Launch"` then an in-place forward jump to the next relaunch (`:1519-1594`) | `ShouldEnableWarpToWindow`: looping ∧ unit built ∧ finite `NextRelaunchUT` > now+1 s, ∧ scene is FLIGHT or SPACECENTER (`:1521-1531`, `:1603-1611`) |
| `Loop` checkbox | `MissionStore.SetLoopEnabled(mission, on, UT, CommittedTrees)`: stamps `LoopAnchorUT`, clears conflicting loops on the same tree / spanned tree set (`:1334-1355`) | disabled when the tree is route-bound; a turn-on is additionally blocked by a commit guard that only logs (`:1317-1345`) |
| Loop-period value field | manual mode: begins an edit on keyboard focus, commits on Enter or click-away; parses via `ParsekUI.TryParseLoopInput`, rejects negatives, clamps below `LoopTiming.MinCycleDuration` (`:2638-2689`, `:2739-2766`) | greyed when loop off; non-editable in `Auto`; replaced by a read-only label when phase-locked or re-aim |
| Loop unit button (`sec`/`min`/`hr`/`auto`) | cycles `Sec→Min→Hour→Auto→Sec` via `RecordingsTableUI.CycleRecordingUnit` and drops the edit focus (`:2693-2701`, `RecordingsTableUI.cs:5417-5421`) | greyed when loop off; absent in the locked-period state |
| `Watch` / `W*` | `flight.EnterWatchMode(target)` or, when already watching a member, `flight.ExitWatchMode()` (`:1493-1506`) | disabled outside flight (greyed placeholder, `:1472-1479`); enabled when a trimmed member has an active ghost, same body, within visual range (`:2482-2491`) |
| `Rewind` / `Forward` | delegates to `RecordingsTableUI.DrawMissionRewindForwardButton` on the mission's earliest-start committed member; each opens its own confirmation dialog (`:1380-1384`, `RecordingsTableUI.cs:3134-3177`) | per `CanRewind`/`CanFastForward`; blank cell if no root member resolves |
| Archive checkbox | sets `mission.Archived`, logs `"Mission '{name}' archived={bool}"` (`:1391-1398`) | always |

### 4.3 Composition rows

| Control | Effect |
|---|---|
| Include checkbox (selectable nodes only) | adds/removes `node.HeadLegId` in `mission.ExcludedIntervalKeys`, **no cascade**, and stamps `SelectionSchemaGeneration` to current; logs `"Mission '{name}' interval '{headLegId}' included={bool}"` (`:897-917`) |
| Row name (nodes with children) | toggles collapse for `"missionId:headLegId"` (`:939-946`) |
| `Fly` / `Seal` (Re-Fly cell) | reused verbatim from the Recordings tab: `Fly` → `RewindInvoker.ShowDialog(rp, slot)`; `Seal` → `HandleSealUnfinishedFlightClick`; a stash-eligible slot shows `Stash`/`Seal` instead (`:1001-1006`, `RecordingsTableUI.cs:3182-3196`, `:3282-3299`, `:3378-3379`) |

Roster atoms carry no checkbox and no buttons; they grey with their owning interval
(`:918-921`, `:857`).

### 4.4 Partner-journey rows (`DrawForeignDockLinkRows` `:1075-1152`)

| Control | Effect |
|---|---|
| Link include checkbox | adds/removes `link.LinkId` in `mission.IncludedForeignDockLinkIds`; on a turn-on while already looping, calls `MissionStore.ClearLoopsConflictingWith(..., "PartnerJourneyInclude")` because the spanned tree set just widened (`:1089-1107`) |
| (implicit) | when included, the foreign journey's **maximal** journey nodes render as child rows with the normal per-interval checkboxes; a not-included link renders dimmed with no children (`:1134-1147`, `DrawMaximalJourneyNodes` `:1157-1169`, `DrawForeignJourneyNode` `:1175-1216`) |

### 4.5 Cross-link in (Timeline `GoTo`)

`RevealMissionForRecording(recordingId)` (`:389-466`): resolves recording → `TreeId` →
`MissionStore.FindOriginalMission` through the **effective** recording set (ERS), seeds default
missions, queues a global Archive-filter clear when that filter would hide the target, and
arms a two-frame scroll handshake (capture on a Repaint pass whose own Layout ran, apply
before `BeginScrollView`, max age 4 frames) (`:473-510`, `:576-596`, `:58`). Every failure
warns and lands the player on the tab unscrolled (`:410-443`, `:683-690`).

---

## 5. Basic vs Advanced mode

`UiSurfaceVisibility.IsVisible` (`UI/UiComplexityMode.cs:104-143`):

- `UiSurface.TabMissions` → **visible in Basic** (`:116`), with the comment "mission
  abstraction; also gates Timeline GoTo".
- `UiSurface.TabRecordings` → **hidden in Basic** (`:125`).
- `UiSurface.MainButtonRecordings` (which is the *Missions* launcher, `:52`, `:32-35`) →
  visible in Basic (`:113`).

Consequences for this window:

1. **The tab bar disappears entirely in Basic.** `VisibleTabCount(Basic) == 0`, so
   `GUILayout.Toolbar` is not drawn at all and its trailing `Space(3)` goes with it; content
   dispatch is pinned to Missions (`RecordingsTableUI.cs:151-166`, `:1378-1392`;
   design `design-ui-basic-advanced.md:447`).
2. **`selectedTab` is clamped to `TabMissions`** on mode apply and defensively on draw
   (`RecordingsTableUI.cs:169-181`, `:1368`).
3. **The Missions tab body itself is identical in both modes.** `MissionsWindowUI.cs`
   contains zero references to `UiComplexityMode` / `UiSurface` / `AppliedUiComplexityMode`
   (verified by grep) — no control inside the tab is mode-gated.
4. The window title is `"Parsek - Missions"` and the main-window button reads `Missions` in
   **both** modes (`design-ui-basic-advanced.md:170-183`).
5. The `Log` window (`StructureListWindowUI`) opened from a Missions row is deliberately not
   gated and stays reachable in Basic (`design-ui-basic-advanced.md:460`).
6. Design rationale for keeping the tab: "The player-facing mission abstraction: name, loop
   period, Watch, Clone, Delete, Archive, include checkboxes, Log. Sufficient for all routine
   recording management." (`design-ui-basic-advanced.md:154`).

---

## 6. State communicated to the player

### 6.1 Loop / schedule state

The **loop-period cell** (`DrawMissionLoopPeriodCell` `:2540-2706`) is a four-state control:

| State | Rendering |
|---|---|
| Loop off | value field + unit button, greyed, showing the configured value as typed (`:2546`, `:2650`, `:2692`) |
| Loop on, unit = `Auto` | non-editable field showing the global auto-loop interval (or the overlap-capped effective cadence) plus a unit suffix; unit button reads `auto` (`:2617-2637`) |
| Loop on, manual | editable field; when the overlap cap raised the cadence above what was requested, the field shows the **effective** cadence tinted amber `LoopPeriodClampColor` = `(1, 0.8, 0.4)` (`:226-228`, `:2612-2615`, `:2644-2655`) |
| Loop on, phase-locked or re-aim | **read-only** amber label, no unit button (`:2565-2591`) |

Locked-label format strings:
- `BuildPeriodCellDisplay` → `"~6h (Kerbin rot)"`, `"~1.6d (Mun window)"`, `"(station window)"`
  for a `VesselOrbital` constraint (`:2218-2239`).
- `BuildReaimPeriodCellDisplay` → `"~2.1y (Duna transfer)"` (`:2246-2250`).
- `BuildScheduledPeriodCellDisplay` → `"~13d-1mo (Mun window, varies)"`, collapsing to
  `"~13d (Mun window, varies)"` when the range ends are within ~5% (`:2267-2311`).
- `FormatPeriodCompact` → single-unit `~` values: `"~6h"`, `"~1.6d"`, `"~36m"`, `"~9s"`
  (`:2191-2209`).

The **TTL cell** (`BuildTMinusCellText` `:2117-2147`) has exactly four outputs:

| Condition | Text |
|---|---|
| not looping, or no periodicity solution | `""` (blank) |
| unsupported config (`ShouldPhaseLock == false`) **or** no engine loop unit built | `"not aligned"` |
| unconstrained free loop (`P ≈ MinCycleDuration`), or a locked P with no resolvable relaunch | `"continuous"` |
| supported + constrained, or a re-aim unit | `"T- " + FormatCountdownCompact(delta)` → `"T- 12m 30s"`, `"T- 2h 14m"`, `"T- 3d 5h"`, `"T- 1y 42d"` (`:2156-2184`) |

The TTL cell tints amber (`ShouldTintTMinusAmber` `:1979-2004`) when the actual relaunch
alignment misses tolerance, when a station's live orbit has drifted (D3), or when a re-aim
arrival hold failed closed (M4c); the reason(s) ride as the tooltip.

### 6.2 Route ownership

When the tree is bound to a supply route, the Loop toggle renders disabled and an inline
`Looped by route` label appears with the route name in the tooltip (`:1317-1333`).

### 6.3 Watch state

`W*` replaces `Watch` while one of the mission's members is the watched recording
(`:1492`); the button is greyed outside flight.

### 6.4 Time ranges and durations

- Per-row `Start time` / `End time` via `KSPUtil.PrintDateCompact(ut, true)`
  (`:978`, `:981`), plus `Start event` / `End event` words.
- Roster atoms show **no** times (`:984-989`).
- Mission-level: **no duration and no span times are shown anywhere.** The mission span is
  computed (`MissionSpanStartUT` `:1639-1648` for sorting, `MissionSpanSeconds` `:2319-2342`
  for the cadence cap) but never rendered.

### 6.5 Selection / inclusion state

Include checkbox ticked = interval included; unticked = that interval only (no cascade), and
the row plus its non-selectable atoms render dimmed at `DimColor` (0.45 alpha)
(`:854-857`, `:923-925`). Re-Fly and Archive cells deliberately stay un-dimmed (`:991-994`).

### 6.6 Docked-with-other-mission relationships

- **Same-tree** dock/board: an interval boundary on the continuing vessel with
  `StartEvent = "Docked"`/`"Boarded"` and a rebased (larger) composition label. The partner's
  own pre-dock line is a separate root row or a separate mission; nothing on the row names the
  partner.
- **Cross-tree** dock: one `Partner journey - <foreign vessel>` row per derived
  `ForeignDockLink`, with the dock UT in `Start time`, `Docked`/`Boarded` in `Start event`,
  blank `End event` / `End time`, and an include checkbox that is **off by default**
  (`:1085-1131`). Everything about the link beyond its id is derived live via PID +
  launch-guid matching (`Mission.cs:31-40`, `MissionCrossTreeDock.cs:49-110`).

### 6.7 Unfinished-flight state

The `Re-Fly` column surfaces `Fly`/`Seal` (or `Stash`/`Seal`) on any composition row whose
`HeadLegId` resolves to a committed recording that the Recordings tab classifies as an
unfinished flight; every other row is a blank cell (`:996-1010`).

### 6.8 Archived state

A per-mission checkbox in the rightmost column, paired with the global header toggle.
`Mission.Archived` is documented as list-management only — a still-looping archived mission
keeps looping (`Mission.cs:59-63`).

---

## 7. What the UI conspicuously does NOT show

Factual observations only.

1. **No recording/commit status per row.** The tab renders from `RecordingStore.CommittedTrees`
   only (`:556`, `:1747`); an in-flight, uncommitted recording has no row. No row bears a
   "recording now", "committed", or "pending" marker.
2. **No supersede / re-fly visual state.** `MissionsWindowUI.cs` contains no supersede or ERS
   filtering in its row path (the only ERS use is the incoming Timeline cross-link, `:400`);
   composition rows are built from raw tree recordings and Re-Fly / Watch / Rewind are keyed on
   raw committed indices with explicit `[ERS-exempt]` notes (`:783-784`, `:1220-1221`,
   `:1378-1380`, `:1482-1485`, `:2604-2606`). A superseded leg therefore renders like any
   other, unmarked.
3. **No per-recording playback-enable.** The first column's enable slot is deliberately blank
   ("no per-row enable in missions", `:890-893`). The include checkboxes write
   `Mission.ExcludedIntervalKeys`, consumed only by the loop-unit pipeline — non-loop ghost
   playback, KSC showcase, and map presence gate on `Recording.PlaybackEnabled`, which only the
   (Basic-hidden) Recordings tab writes (`design-ui-basic-advanced.md:221-223`).
4. **No `Recording.Hidden` filtering.** The Missions tab does not consume the per-recording
   Archive/Hidden flag (`design-ui-basic-advanced.md:231`); only `Mission.Archived` affects
   this list.
5. **Debris has no representation at all** — no row, no count, no indicator that debris rides a
   given interval (`MissionStructure.cs:139-143`;
   `design-mission-abstractions.md:122-124`, `design-ui-basic-advanced.md:223`).
6. **`ExcludedThroughLineHeadIds` is invisible and unwritable here.** The field persists and
   `MissionSelection.ComputeIncludedHeadIds` implements its cascade, but the window never sets
   it and never renders it (`:2317`, `:2474-2475`); a value loaded from a save has no UI
   affordance.
7. **No mission-level duration, span, or member count.** Computed internally
   (`MissionSpanSeconds` `:2319-2342`) but never displayed. The `Warp to...` confirmation
   dialog is the only place a duration-like value appears
   (`ParsekTimeFormat.FormatDurationFull(previewDelta)`, `:1547-1551`).
8. **No name/text filter and no search.** Narrowing is limited to sort (3 columns) + the
   Archive hide toggle (noted as a review observation in
   `design-ui-basic-advanced.md:655`).
9. **The `TreeId`, `Mission.Id`, recording ids, and interval keys are never shown.** They
    appear only in log lines (`:914-915`, `:1095-1098`, `:1283-1284`).
10. **`SelectionSchemaGeneration`, `LoopAnchorUT`, and the `@dock` / `/segN` key structure**
    have no visual representation, though all three change behaviour.
11. **The partner side of a same-tree dock is not named on the row.** The docked interval
    label shows a larger composition (`pod x2, probe x1, crew x4`) but nothing identifies which
    vessel joined; only a *cross-tree* dock produces a named `Partner journey - X` row.
12. **Which recording a row maps to is not surfaced** beyond the presence or absence of a
    Re-Fly button; a row whose `HeadLegId` is a synthetic interval key (`.../seg2`,
    `...@dock1`) resolves to no committed recording, so its Re-Fly cell is silently blank
    (`:1001-1010`).
13. **Clone lineage is not marked.** A clone is visually identical to the original except that
    its `Delete` is enabled and its default name ends in `" copy"`; the shared tree index is
    the only hint they are the same tree (`:1619-1621`, `Mission.cs:89`).
14. **Loop-conflict outcomes are silent in the UI.** Enabling a loop can clear loops on
    same-tree siblings and on cross-tree-linked missions; that only appears in the log
    (`MissionStore.cs:429-434`, `:485-489`) — the affected mission's row simply shows its Loop
    box unticked on the next frame.
15. **The mission header's buttons do not line up with the data columns** by design: the right
    block is widened by `MissionHeaderPeriodSlack = 48` so a long period label fits on one
    line, so the buttons begin slightly left of the column boundary while only the Archive
    checkbox stays column-aligned (`:159-175`).
