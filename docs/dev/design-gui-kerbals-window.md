# Parsek - Kerbals window: the two column tables

Design authority for `Source/Parsek/UI/KerbalsWindowUI.cs` and its pure half
`Source/Parsek/UI/KerbalsPresentation.cs`. Written with the 2026-09-15 rebuild and revised by
the 2026-09-22 review round (section 2, "Round-2 rulings"); it replaces the row-model half of
`design-gui-inventory.md` section 3.4, which now points here.

## 1. What the window is for

In the mod's own words, from the two tab tooltips it ships:

- Roster: "What each kerbal is doing now: available, aboard, reserved, standing in, retired
  or lost."
- Flights: "Every mission a kerbal flew, and how each one ended for him."

The window answers two questions and nothing else: **who can I fly right now, and why not**,
and **what happened to this kerbal across my career**. It is READ-ONLY - no reserve,
unreserve, swap, clear or dismiss control - and its only mutations are three transient,
unpersisted fold states (the plain-kerbal bucket and a per-kerbal flight-group fold). Every
crew mutation lives in `CrewReservationManager` and runs automatically inside the
recalculation walk.

It draws in BOTH complexity modes since the owner re-ruling of 2026-09-22 (ruling 1 below):
`UiSurface.MainButtonKerbals` is `visibleInBasic = true` (`UI/UiComplexityMode.cs`) and the
window is not in the Advanced -> Basic close set (`ParsekUI.BuildGatedWindowCloseSet`). Same
window, both tabs, no Basic-specific variant.

## 2. The operator's rulings (2026-09-15, binding)

1. The window stays **read-only**. The rebuild is a restructuring inside an existing
   window, which is not a new player-facing surface. **AMENDED 2026-09-22 (owner
   re-ruling): the window is visible in Basic too.** It is the only surface that explains
   why a reserved kerbal is missing from stock crew assignment (the `CrewDialogFilter`
   patch removes him silently), and it is read-only, so showing it costs a Basic player
   nothing he could break. It was Advanced-only from the 2026-09-15 rebuild until then.
2. Both tabs are **column tables** with ONE shared inset: header row and body rows inside the
   same container, through `ParsekUI.GetTableRowStyle` / `GetTableBodyBoxStyle`, so header and
   cells align with zero delta (the house pattern from PRs #1679 / #1680).
3. Tab 1 is renamed **Roster** (from "Roster State"); tab 2 is renamed **Flights** (from
   "Mission Outcomes"). The seam tab TOKENS are unchanged: `roster` and `outcomes`.
4. **Minimal, need-to-know, no spam.** Rows Parsek has something to say about are listed
   first and normally; plain available kerbals with no recorded flight collapse under ONE
   fold row, closed by default.
5. A **stand-in gets its own row**, with status "Stand-in for &lt;owner&gt;" - since
   2026-09-22 drawn directly UNDER the owner whose seat it covers (ruling 14).
6. The Flights tab keeps **per-kerbal grouping**, and the bucket summary is the group HEADER -
   always shown, not a folded-only extra.
7. A Flights row keeps the **Timeline jump** on click.

### Post-capture rulings (2026-09-15, after the GUI-11 reading)

8. **The Flights row unit is the MISSION, not the recorded segment** ("need-to-know, no
   spam"). The GUI-11 capture read five rows per kerbal - four of them repeating the same
   mission name and the same date, one of them `Outcome unknown` for a mid-mission segment
   that simply has no ending - for what the player did as two missions. One row per
   (kerbal, tree); the segments are counted and listed in the row's hover text. Section 4
   carries the collapse rule.
9. **A stand-in row is not expandable.** The replacement chain describes ONE slot and that
   slot belongs to the owner, so only the owner's row expands. A stand-in row expanding
   into a chain containing itself was the reading; it now says whose slot it covers in its
   status cell and stops there. (Superseded 2026-09-22 by ruling 14: NO row expands; the
   chain is drawn as rows.)
10. **"Stand-in for &lt;owner&gt;" is a per-MEMBER fact, not a per-chain one** - only the
    chain's ACTIVE occupant reads as one. See the status table in section 3.

### Round-2 rulings (2026-09-22, the Kerbals review's approved items)

The review (`kerbals-window-review.md`, recommendations 1-9 minus 10) read the c1 and
bdock-recorded captures; the owner approved these, and each is a row of this doc below.

11. **A deleted stand-in is not a row** (rec 1). A saved chain member who is displaced, not
    in the stock roster, not retired and not reserved is exactly the stand-in
    `KerbalsModule.ApplyToRoster` deleted as unused (`Stand-in '...' displaced -> deleted
    (unused)`); the slot keeps the NAME for later rewinds. The window re-added it as an
    `Available` row - the c1 capture's `Lars Kerman / Available / - / -`, Jebediah's
    stand-in after Jebediah was lost. Omitted rather than shown as retired: "retired" has a
    meaning (flew a committed flight, then displaced) that this kerbal does not have, and
    he is in no stock list the player can open. `KerbalsPresentation.IsDeletedStandIn`.
12. **The Since column is removed** (rec 3). It was `-` for four of six statuses, a
    reservation's date could read in the future after a rewind, and it dated a mission by
    its END while the Flights tab dated it by its START. The one useful fact it carried - when
    a kerbal died - moved into the Lost hover text.
13. **A reservation names what holds the kerbal, never a date** (rec 2 + open question 1,
    VERIFIED). `KerbalReservationReleaseTests` drives the real ledger walk
    (`LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT` at clocks before, during,
    at and long after a Recovered flight, plus the uncut full replay): the reservation is
    present at every clock with `ReservedUntilUT` still the flight's end, and the kerbal
    stays filtered from the crew dialog. Only removing the flight from the committed set
    frees him. So `Reserved until <date>` promised a release that never happens; the cell
    now reads `Reserved: aboard <vessel>` / `Reserved: <mission>` / `Reserved for <owner>`
    and the hover ends with the rule. Whether a recovered kerbal SHOULD be released at the
    flight's end is filed for the owner in `todo-and-known-bugs.md`; reservation semantics
    are unchanged.
14. **The Roster is grouped by slot** (rec 7). Each owner row is followed directly by its
    chain members as rows of their own (tree glyph in the Name cell), so the per-owner chain
    fold - which repeated what the stand-in rows already said - is gone, and with it the
    `roster:<kerbal name>` expand key.
15. **Lost points at the way back** (rec 4): the status hover names the mission and says a
    re-fly from a rewind point can undo the loss (TRUE: a Re-Fly merge tombstones the
    superseded flight's `KerbalAssignment`+Dead row - `TombstoneEligibility.IsKerbalDeath` -
    which is what the permanent reservation came from; flown by
    `CL-3-refly-crew-tombstone`), and every Roster row's Last flight cell is the same
    Timeline cross-link a Flights row is.
16. **`On EVA`** replaces `Assigned (<own name>)` (rec 5): an EVA kerbal is his own vessel.
17. **Flights: one date, no Crew column** (rec 8). The Date is the mission's LAUNCH - the
    date the Missions window's first date column and the Timeline place a mission by - and
    with Since gone nothing in the window dates a mission by its end any more (the Lost
    hover says "launched <date>" too). The rare stand-in note moved into the Mission cell as
    `(flown by <stand-in>)`; the Crew column read `-` on every row of every capture.
18. **Crew consequences get their existing channels** (rec 6, outside this window): a
    one-shot screen message when `CrewReservationManager.SwapReservedCrewInFlight` actually
    swaps a reserved kerbal out of a seat, and a refused dismissal raises the existing
    `Action Blocked` dialog (`CommittedActionDialog.ShowBlocked`) like its four siblings.
19. **One crew vocabulary** (rec 9): the Astronaut Complex overlay's badge tooltips use the
    window's words - `Reserved - held by a committed flight (Parsek)`, `Reserved for <owner>
    - ...` only for a stand-in in someone else's slot, `Lost on a committed flight (Parsek)`
    for a permanent (death) reservation, `Retired stand-in (Parsek)` - and no longer read
    "Reserved by Parsek for slot 'Jebediah Kerman'" on Jebediah himself.

### Two readings the rulings did not spell out, decided here

- **"Reserved for &lt;owner&gt;" is dropped when the owner is the kerbal himself.** A reserved
  slot owner is reserved for his own return, so "Jebediah Kerman | Reserved for Jebediah
  Kerman" would repeat the Name column. An owner row names the flight that holds him
  (ruling 13); a reserved STAND-IN reads "Reserved for &lt;owner&gt;", which is the case the
  wording is for.
- **An ASSIGNED kerbal with no history is listed, not folded.** The fold row says "Available,
  no recorded flights", and a kerbal aboard a craft is not available. "Aboard something right
  now" is also the one stock fact the window is asked for, so it stays visible.

## 3. Tab 1 - Roster

One row per kerbal the player can see, plus one row per Parsek stand-in or retiree the stock
list no longer carries.

### Population

| Source | Rule |
|---|---|
| `HighLogic.CurrentGame.CrewRoster.Crew` | every one, always |
| `.Applicants`, `.Tourist` | only when `KerbalsModule.IsManaged(name)` - a hiring pool of forty applicants is not what this window is for |
| `KerbalsModule.Slots` (owners AND chain members) | added when the stock list does not carry the name - EXCEPT a deleted stand-in (ruling 11: displaced, not in the stock roster, not retired, not reserved), which is omitted and counted in the VM summary's `omittedStandIns=` |
| `KerbalsModule.GetRetiredKerbals()` | added when the stock list does not carry the name |

Sort: GROUPED BY SLOT (ruling 14). Top-level rows by kerbal name, `StringComparer.Ordinal`
ascending, within each partition; each slot owner's row is followed directly by his chain
members in CHAIN order (`RosterRow.Depth` 1). A member is attached to the first slot
(ordinal owner order) that lists him; a member who owns a slot of his own (a reserved
stand-in gets one) carries his own members one level deeper. Not user-selectable.

### Columns and their sources

| Column | Width | Source |
|---|---|---|
| Kerbal | 210 px | `KerbalsWindowUI.FormatRosterNameCell`: `"Name [Trait]"`, a nested stand-in row prefixed by the tree glyph (`\u251c\u2500 ` mid-group, `\u2514\u2500 ` on the last). Trait from the live roster, falling back to `KerbalSlot.OwnerTrait`; the bracket is dropped when the trait is unknown |
| Status now | 220 px | `KerbalsPresentation.ClassifyStatus` + `FormatStatus`; see the vocabulary below. Hover text: `FormatStatusTooltip` |
| Last flight | expands | `FormatLastFlight`: `"{mission} - {outcome word}"` from the kerbal's latest flight row; else `-`. A label-styled button: a click scrolls the Timeline to that mission's last segment (`RosterRow.LastFlightRecordingId`, the same `OnFatesRowClicked` path a Flights row takes) |

### The status vocabulary, in resolution order

| Status | Text | Predicate |
|---|---|---|
| Lost | `Lost` | `KerbalSlot.OwnerPermanentlyGone`, or a permanent (`IsPermanent`) reservation |
| Retired | `Retired` | in the retired set |
| Reserved | `Reserved: aboard <vessel>` / `Reserved: <mission>` / `Reserved` / `Reserved for <owner>` | a live reservation. The flight is `ResolveHoldFlight`: for an open-ended (`+inf`) hold the latest mission that ends Still aboard, else the latest-ending mission; `aboard <vessel>` when that mission ends with the kerbal aboard (vessel = its last segment's recorded vessel), the mission name otherwise, bare `Reserved` when the kerbal has no flight of his own; a composed text past `StatusCellMaxChars` falls back to `Reserved: still aboard` / `Reserved`. The `for <owner>` form only when the kerbal is a stand-in in someone else's chain. NEVER a date (ruling 13) |
| Stand-in | `Stand-in for <owner>` / `Stand-in for <owner> (aboard <vessel>)` / `(on EVA)` | the kerbal is the **ACTIVE** occupant of some OTHER kerbal's slot chain |
| Assigned | `Assigned (<vessel>)` / `On EVA` | the kerbal is aboard a live vessel (ghost-map ProtoVessels excluded first); `On EVA` when that vessel is his own EVA vessel (`Vessel.isEVA`, gathered into `RosterKerbal.AssignedVesselIsEva`) |
| Available | `Available` | none of the above |

The status cell's hover text (`FormatStatusTooltip`):

| Status | Hover |
|---|---|
| Lost | `Lost on <mission> (launched <date>). If that mission has a rewind point, re-flying it can undo the loss.` - the death mission is the latest mission that ends Dead; with none, `Lost on a committed flight. ...` |
| Reserved | `Held by the committed flight <mission>, which ends with this kerbal aboard <vessel>` / `recovered` / `, which has no recorded ending`, then `. Passing time does not release it; it lasts while that flight stays in the timeline.` (`ReservationHoldRule`); a reserved stand-in reads `Held by a committed flight flown in <owner>'s seat. ...` |
| Stand-in aboard a craft | `Standing in for <owner>; aboard <vessel>.` (or `; on EVA.`) - only when the inline form does not fit |
| others | none |

"Latest recorded flight" (the Last flight cell) is resolved as the latest-ENDING mission
rather than as the last row, because the rows are ordered by their Date column and two
missions can overlap (a short hop launched during a long station stay).

### Why a stand-in is a per-member fact

Chain MEMBERSHIP is what makes a row point at a slot. It is not what makes the kerbal the one
standing in: a chain has at most ONE active occupant, and the owner's own expansion already
labels the rest `displaced` / `retired`. Reading `Stand-in for X` off membership therefore
contradicted the line directly underneath it. `ClassifyStatus` takes the member's
`ChainMemberStatus` and only `Active` reads as a stand-in; a displaced or retired member falls
through to `Assigned (<vessel>)` / `Available` like any other kerbal.

Three consequences worth naming, each with a unit cell (review probes A-F). A displaced or
retired member is still LISTED under the owner (grouping is by membership); only its status
word stops saying `Stand-in for`:

| Case | Reads |
|---|---|
| owner back home, first chain member displaced | `Available` (or `Assigned (<vessel>)`), and the owner's chain line still says `displaced` |
| owner permanently gone (`OwnerPermanentlyGone`) | `GetActiveChainIndex` answers `NoActiveChainOccupant`, so NO member is active and every one of them is freed - none is left "covering" a slot nobody returns to |
| retired chain member | `Retired`, which outranks both the chain and an assignment |

An ACTIVE stand-in who is also aboard a craft names the vessel INLINE when the composed cell
fits the 220 px column (`KerbalsPresentation.StatusCellMaxChars` = 220 / 7 px = **31**
characters, the same pessimistic advance `TooltipEchoBudgetTests` budgets strips at), and moves
it into the cell's hover text when it does not - `Standing in for <owner>; aboard <vessel>.`
A clipped cell would read as a shorter status rather than as an overflow. The same budget
decides whether a reservation's vessel or mission name fits inline.

### The fold

`IsPlainRow` is exactly `Available` + no slot + no recorded flight. Those rows go to the
`Plain` partition and draw behind one fold row, `Available, no recorded flights (N)`, closed
by default and dimmed when opened (name and trait only in effect - every other cell is `-`).
The fold state lives in the window, never on disk. It is the Roster tab's ONLY fold.

The chain fold is gone (ruling 14). Its successor is the grouping itself: an owner row carries
`SlotMemberCount`, each member row `Depth`, `IsLastInSlot` (the glyph) and `MemberStatus`
(`Active` / `Retired` / `Displaced`, the chain's own classification), and `SlotOwnerName`,
which its status cell spells out when it matters.

### Empty state

One line, `No kerbals in the roster.`, reachable only on a save whose stock roster is empty -
impossible in a career, constructible in a hand-built sandbox or science save. The old
`No reserved crew, stand-ins, or retired kerbals.` is gone: on a career it was a lie about a
roster that had four kerbals in it.

## 4. Tab 2 - Flights

Per-kerbal groups, each a fold header plus **one row per MISSION** - per recording tree the
kerbal appears in, not per recorded segment.

Group header: `Name [Trait] - N missions: n recovered, n lost, n aboard, n unknown`
(`FormatFlightGroupHeader`). Zero buckets are omitted, `1 mission` is singular, and the
trait bracket is dropped when unknown. Always drawn, folded or not. The buckets count each
mission's FINAL outcome, so a mission whose middle segment has no ending is not counted as
unknown.

| Column | Width | Source |
|---|---|---|
| Date | 130 px | `KerbalsWindowUI.FormatRowDate` = `KSPUtil.PrintDateCompact(ut, true)` with the Timeline's own F0 fallback, so both windows date the same flight identically. The UT is the kerbal's FIRST segment start in this mission - the mission's LAUNCH (ruling 17), the one date the whole window uses for a mission |
| Mission | expands | `MissionStore.FindOriginalMission(rec.TreeId).Name`, falling back to `Recording.VesselName`, then `(unnamed)`, plus ` (flown by <stand-in>)` when someone else flew the seat (`FormatMissionCell`; it was a Crew column of its own until 2026-09-22). The raw recording name and id are in the cell's hover text (`DescribeFlightRow`), naming the LAST segment - the one a click jumps to |
| Outcome | 110 px | `FormatOutcome` of the FINAL end state: `Recovered` / `Lost` / `Still aboard` / `Outcome unknown`. Its hover (`DescribeFlightRowOutcome`) is the outcome sentence plus the segment list, e.g. "... `3 segments: Still aboard, Still aboard, Recovered`." - dropped on a single-segment mission, where it would add nothing |

Sort: the Date column ascending within a kerbal (mission start, ties broken on the end), kerbals
by name (ordinal). Every cell is a label-styled button carrying the row's Timeline cross-link,
so a click anywhere on the row scrolls the Timeline to the mission's LAST recorded segment
(`OnFatesRowClicked` -> `TimelineWindowUI.ScrollToRecording`, which OPENS the Timeline when it
is closed - finding P6).

### The mission collapse, per (kerbal, tree)

| Field | Rule |
|---|---|
| mission key | `TrackSection`-free: `Recording.TreeId`, or the recording id when there is no tree - so a standalone / pre-tree recording, an EVA branch that is its own tree included, stays a row of its own (`MissionKeyOf`) |
| Date | the EARLIEST segment start |
| `EndUT` | the LAST segment's end. Not drawn; it picks the Roster's latest flight and breaks row-order ties |
| Mission name | the first segment that resolves a real `MissionStore` name; they share a tree, so they share the name |
| Outcome / `EndState` | the LAST segment by end UT. An `Unknown` segment followed by one with an ending is therefore NOT the answer, which is the half of the defect the capture made obvious |
| Timeline target | that same last segment's recording |
| `SegmentCount` / `SegmentSummaryText` | the count and every segment's own outcome word in end-UT order, `1 segment: Still aboard` / `3 segments: Still aboard, Outcome unknown, Recovered` |
| Stand-in note (`StandInName`, drawn in the Mission cell) | the FIRST segment that names a stand-in wins: a stand-in who flew the launch and handed over mid-mission still flew the mission |

WHY the reading forced this: on `fixtures/saves/bdock-recorded` each of the three reserved
kerbals had FIVE rows - one for the single-segment tree and four for the four-segment one, all
four at `Y1, D01, 02:29` with the identical mission name, one of them `Outcome unknown` for a
2-point mid-mission segment. Two missions, five rows, and the one number the player wanted (how
it ended) was on the row he had no way to identify as last.

Empty state: `No recorded flights with crew yet.`

### Stand-in attribution, and why the primary source is the recording

`KerbalsModule.PopulateCrewEndStates` reverse-maps a stand-in's name back to the OWNER before
writing `Recording.CrewEndStates`, so the owner-keyed end states alone cannot say who flew a
given flight. Two sources can:

1. **PRIMARY - the recording's own raw crew**, `KerbalsModule.RawRecordingCrewByRecordingId`
   (a read-only view of the walk's `rawRecordingCrew`, added for this window). That is
   per-flight truth: the names the recorder wrote into that flight's snapshot. A raw name
   counts as a stand-in only when it is a chain member of that owner's slot, and the owner
   being aboard means no stand-in whatever else flew along - so an unrelated crewmate on a
   multi-seat flight is never read as one.
2. **FALLBACK - `CrewReservationManager.CrewReplacements`**, used ONLY when the recording has
   no raw-crew entry at all. The load-time sweep can null a recording's `VesselSnapshot`,
   which is where raw crew comes from. This map answers the CURRENT stand-in, so on an old
   flight it can name the wrong one; that is a strictly better answer than silence, and the
   Mission cell reads "(flown by &lt;name&gt;)" either way.

The choice is the primary one BECAUSE the fallback is time-blind: the reverse-map's own order
(`ReverseMapCrewNames` first, slot chains second) is about names, not about which flight.

## 5. Sizing

| Number | Value | Arithmetic |
|---|---|---|
| Roster fixed columns | 210 + 220 = **430 px** | longest realistic cells: `\u2514\u2500 Valentina Kerman [Scientist]` (31 chars with the glyph, the Name column went 190 -> 210 for it), `Reserved for Valentina Kerman` (29 chars) |
| Flights fixed columns | 130 + 110 = **240 px** | the compact date and `Outcome unknown` (15 chars); Mission expands |
| the Date column | **130** (80 on the first flight) | MEASURED: `KSPUtil.PrintDateCompact` renders to the minute, so a cell reads `Y1, D01, 02:29` - 14 chars, about 98 px at the skin's ~7 px advance - and the first flight (`2026-09-15_1557` / `_1559`) photographed it clipped inside 80 |
| `DefaultWindowWidth` | **760** (unchanged 2026-09-22) | leaves the expanding Last flight column about 280 px beside the 430 px of fixed columns. The old 410 was half of Career's 820; 760 still leaves Career's 820 room on a 1920-wide screen |
| `MinWindowWidth` | **586** (was 280, then 570, then 700) | 430 fixed + 8 inter-column cell margin + 4 before the expanding column + 100 of readable sliver for it + 28 of window chrome + 16 of scrollbar gutter. Below that IMGUI clips the pinned widths rather than reflowing them. It was 700 while the Roster carried the 130 px Since column beside a 190 px Name column (540 fixed + 12 margin); pinned term by term by `KerbalsWindowUITests.Sizing_*` |
| the margin / chrome terms | 4 per fixed column / 28 / 16 | MEASURED off the 2026-09-15 census dumps: each column consumed its width + 4 (194 / 224 / 134 for 190 / 220 / 130) and the first cell sat 14 px inside the window edge; the gutter is `ParsekUI.DefaultVerticalScrollbarFootprintWidth` |
| `DefaultWindowHeight` / `MinWindowHeight` | 400 / 150 | unchanged |

Tooltip budget: `TooltipEchoBudgetTests.StripWindows` pins this file at `760f, 5,
DoubleLine`, so the budget is `2 * (760 - 30) / 7 = 208` characters. The floor of 5 is
unchanged; the file's literal `GUIContent` tooltips are the two tab labels, three column
headers (`Last flight`, the Flights `Date` and `Mission`), the plain-bucket fold, the Flights
group fold and the Flights row's Date cross-link, plus the Roster Last flight cell's
`LastFlightJumpTooltip` constant. RUNTIME-built tooltips are skipped by the scanner by design:
the Mission cell's `DescribeFlightRow`, the Outcome cell's `DescribeFlightRowOutcome`, and the
Roster status cell's `StatusTooltipText`. The longest runtime tooltip is the Reserved-aboard
hover, about 170 characters with two mission-length names, under the 208 budget; the
Since / Crew header tooltips and the chain-expand tooltip left with their controls.

## 6. Seam contracts

| Contract | Value |
|---|---|
| `WindowIdKey` | `ParsekKerbals`, ONE literal, hashed at both the `GUILayoutWindow` call and `UiWindowHandle.GetWindowId` |
| tab tokens | `roster`, `outcomes` - unchanged by the rename, so every committed census step keeps working |
| `SelectedTabForTesting` / `TabCountForTesting` / `WindowRectForTesting` | unchanged |
| `CachedViewModelForTesting` | NEW: the built view model, so the expand seam's key enumerations are unit-testable headlessly (in production the first drawn frame seeds it, which is why `op=expand` is two-phase) |

**`op=expand window=kerbals` is new.** Two prefixes, one per tab, because the window keeps one
expansion collection per tab:

| Key | Drives |
|---|---|
| `roster:(available)` | the plain-kerbal fold row (`KerbalsWindowUI.PlainBucketKey`) - the ONLY Roster key since the 2026-09-22 slot grouping; `roster:<kerbal name>` (the chain fold) is no longer offered, and a roster with no plain kerbal offers no Roster key at all |
| `flights:<kerbal name>` | that kerbal's flight group (INVERTED on the production side: `foldedKerbals` holds what is FOLDED; the wire speaks "expanded") |
| `all` / `none` | both sets at once - what a census needs, since the ids are save-specific |

Mirrored by `hlib.UIACTION_EXPAND_PREFIXES["kerbals"]`, and the two sides are kept byte-equal
by `test_hlib.GuiCensusSeamVerbTests.test_the_expand_prefix_map_mirrors_the_c_sharp_tables`
(new) plus `GuiCensusApplierSourceGateTests.ResolveExpandSets_WiresExactlyThePrefixesTheParse-
Accepts`.

## 7. Data routing and logging

The view model is gathered once and cached (`InvalidateCache` drops it; the fold sets
deliberately survive).

### What refreshes the tab

`LedgerOrchestrator.OnTimelineDataChanged` covers every LEDGER-side change, and it was the
only thing that did - but the view model is ALSO built from live state that no ledger write
touches: the stock roster walk (`CrewRoster.Crew` / `.Applicants` / `.Tourist`) and the live
crew-to-vessel map (`GatherAssignedVessels`). A transfer, an EVA, a board, a hire or a
dismissal therefore left a stale `Assigned (<vessel>)` cell - or a missing row - until some
unrelated ledger write happened to drop the cache. The window now subscribes eight stock
events of its own:

| Event | What it would otherwise leave stale |
|---|---|
| `onVesselCrewWasModified` | the `Assigned (<vessel>)` cell after any seat change (it is also what `CrewReservationManager` fires after its own swaps) |
| `onVesselChange` | an assignment that changed while the tab was open in another vessel's context |
| `onCrewTransferred` | a kerbal moved between parts / craft |
| `onCrewOnEva` | an EVA: the kerbal's own vessel is now the EVA kerbal |
| `onCrewBoardVessel` | the reverse |
| `onKerbalAdded` / `onKerbalRemoved` | a hire or a dismissal: a whole ROW appearing or going |
| `onKerbalStatusChange` | `Available` / `Assigned` / dead, the stock roster status itself |

All eight funnel into one handler writing ONE Verbose line naming the event
(`DescribeLiveCrewRefresh`), so the log says which of the eight refreshed the tab. They are
subscribed and unsubscribed exactly where the ledger hook is - `ParsekUI`'s two constructors
and `ParsekUI.Cleanup` - so the subscriptions live and die with the owning scene's `ParsekUI`.
Source-gated: `KerbalsWindowUITests.LiveCrewRefresh_*` read `KerbalsWindowUI.cs` and
`ParsekUI.cs`, so an event added to the subscribe half without the documented set, or a
subscribe without its matching remove, reds the suite.
 Recordings come from `EffectiveState.ComputeERS()` - never
`RecordingStore.CommittedRecordings`, which `scripts/grep-audit-ers-els.ps1` gates for this
file. Every `FlightGlobals.Vessels` walk checks `GhostMapPresence.IsGhostMapVessel(pid)`
first, so a ghost's recorded crew never reads as a live assignment.

Batch counters, one summary line each (the house convention): the roster gather
(`crew=`, `managedNonCrew=`, `skipped=`), the live crew map (`vessels=`, `ghostsSkipped=`,
`seated=`), the mission-name resolution, and the composed view model
(`roster=<involved>+<plain> flightGroups= endStates=`). The two gather helpers are
fail-soft with a Verbose line naming the exception type: the headless xUnit host has no
`HighLogic` and no `FlightGlobals`, and the pure builders then run off the ledger's own names.

## 8. What the captures show

Five lanes flew the rebuild on 2026-09-15, every one PASS on attempt 1 against the
DEPLOYED-hash-pinned build `4763698...` (GUI-5 `_1615` 88 s, GUI-11 `_1616` 58 s, GUI-8
`_1617` 66 s, GUI-6 `_1618` 79 s; the first pair `_1557` / `_1559` is the round that found
the two defects the second commit fixes).

**Then the review pass re-flew two of them on the deployed build
`6c100d9029cfd7a9860650c236a5929522f672207f4746da6b5feb42ec81e957`** - GUI-11
`2026-09-15_1744` and GUI-6 `2026-09-15_1746` - because three of the findings it acted on
were READINGS OF THOSE CAPTURES rather than of the code: the Flights tab drawing a row per
segment, a stand-in row carrying an expand arrow, and the minimum width. The rows below are
the AFTER-REVIEW readings; the pre-review ones are named where they differ, because the diff
is the evidence.

| Label | Lane, run | Reads |
|---|---|---|
| `cek-kerbals-roster-advanced` BEFORE | GUI-5, `2026-09-11_1553` | ONE row on a career with four stock kerbals: `> Jebediah Kerman [Pilot] - deceased  (1)` (the reserved form on the wave-2 re-read) |
| `cek-kerbals-outcomes-advanced` BEFORE | GUI-5, `2026-09-11_1553` | `v <b>Jebediah Kerman</b>` over `Jumping Flea - Recovered at UT 342` / `- Aboard at UT 348` |
| `cek-kerbals-roster-advanced` AFTER | GUI-5, `2026-09-15_1615` | `Debwig Kerman [Pilot] / Stand-in for Jebediah Kerman / - / -` and `Jebediah Kerman [Pilot] / Reserved until recovery / Y1, D01, 00:05 / Jumping Flea - Still aboard`, with `Available, no recorded flights (3)` closed under them |
| `cek-kerbals-roster-expanded-advanced` NEW | GUI-5, `2026-09-15_1615` | the same with every fold open: both rows' chain line `(branch) Debwig Kerman (active)`, and Bill / Bob / Valentina listed dimmed as `Available / - / -` |
| `cek-kerbals-outcomes-advanced` AFTER | GUI-5, `2026-09-15_1615` | `Jebediah Kerman [Pilot] - 2 flights: 1 recovered, 1 aboard` over `Y1, D01, 00:05 | Jumping Flea | Recovered | -` and the `Still aboard` twin |
| `bdk-kerbals-roster-collapsed-advanced` | GUI-11, `2026-09-15_1744` | the crewed corpus: Bill / Bob / Valentina each `Reserved until recovery`, `Y1, D01, 02:29`, `Kerbal X #2 - Still aboard`; Jane / Sizon / Kathdan each `Stand-in for <owner>` with `- / -`; `Available, no recorded flights (1)`. The three stand-in rows draw the two-space LEAF prefix, not an arrow - S1 in the picture |
| `bdk-kerbals-roster-expanded-advanced` | GUI-11, `2026-09-15_1744` | THREE chains drawn, one per owner (`(branch) Jane Kerman (active)` / `Sizon` / `Kathdan`), plus Jebediah Kerman listed as the one plain `Available` row. The pre-review reading was SIX, because each chain also hung off its stand-in's row |
| `bdk-kerbals-roster-owner-chain-advanced` | GUI-11, `2026-09-15_1744` | ONE expansion, on the OWNER's row: `(open) Bill Kerman [Engineer]` over `    (branch) Jane Kerman (active)`, with Bob / Valentina still closed. It replaces `bdk-kerbals-roster-standin-chain-advanced`, whose whole subject - a chain hanging off the stand-in - is the thing S1 removed |
| `bdk-kerbals-flights-unfolded-advanced` | GUI-11, `2026-09-15_1744` | three groups, SIX rows - two per kerbal: `Y1, D01, 00:00 \| Kerbal X \| Still aboard \| -` and `Y1, D01, 00:06 \| Kerbal X #2 \| Still aboard \| -`. The pre-review reading was FIFTEEN (five per kerbal), four of them at the identical `Y1, D01, 02:29` with the identical `Kerbal X #2`, one of them `Outcome unknown` - a two-point mid-mission segment. That reading is S2 |
| `bdk-kerbals-flights-folded-advanced` | GUI-11, `2026-09-15_1744` | the three headers alone: `Bill Kerman [Engineer] - 2 missions: 2 aboard`, `Bob Kerman [Scientist] - ...`, `Valentina Kerman [Pilot] - ...`. Pre-review: `5 flights: 4 aboard, 1 unknown` |
| `fs-kerbals-roster-fresh-advanced` | GUI-8, `2026-09-15_1617` | a fresh science save: the column header row plus ONE fold row, `Available, no recorded flights (4)` - where the pre-rebuild window claimed `No reserved crew, stand-ins, or retired kerbals.` on a roster of four |
| `fs-kerbals-outcomes-empty-advanced` | GUI-8, `2026-09-15_1617` | `No recorded flights with crew yet.` |
| `play-kerbals-roster-precrew-advanced` NEW | GUI-6, `2026-09-15_1749` | the FLIGHT scene before a crew change: `Jebediah Kerman [Pilot] / Assigned (mk1-capsule) / - / -` over `Available, no recorded flights (3)`, and `op=rect` answering `min=700,150` - the R3 arithmetic, read off the seam rather than off the source |
| `play-kerbals-roster-postcrew-advanced` NEW | GUI-6, `2026-09-15_1749` | THE SAME TAB after one real `EvaExit`, and the same cell now reads `Assigned (Jebediah Kerman)` - the EVA kerbal is his own vessel. No ledger write happened between the two captures, so this pair is the live-crew refresh: five of the eight subscribed events fired on the EVA and two dropped a live cache (`onKerbalStatusChange`, then `onVesselChange`). Without the subscription the second capture would be a picture of the first one's view model |
| `play-kerbals-flights-postcrew-advanced` NEW | GUI-6, `2026-09-15_1749` | the Flights tab in FLIGHT on a corpus with no crewed recordings: `No recorded flights with crew yet.` |
| `play-kerbals-roster-flight-advanced` | GUI-6, `2026-09-15_1618` | the FLIGHT scene, and the first picture of the live-crew column: `Jebediah Kerman [Pilot] / Assigned (mk1-capsule) / - / -` over `Available, no recorded flights (3)` - on a host with 243 injected ghost recordings, so it also shows the `IsGhostMapVessel` guard holding (no ghost crew reads as an assignment) |

### The 2026-09-22 round (rulings 11-19), flown on the deployed build `e2afe9b2...`

Three lanes, every one PASS on attempt 1 against the DEPLOYED-hash-pinned automation DLL
`e2afe9b25699bbf835893003decf643b4f61d7cf1952fab15500cfc4801d3890` (re-asserted before each
launch): GUI-11 `2026-09-22_2004`, GUI-5 `2026-09-22_2005`, GUI-1 `2026-09-22_2006` (c1; its
analyzer row reads RED on the c1 host's own recordings, report-only by the spec's
declaration, as on every earlier c1 flight).

| Label | Lane, run | Reads |
|---|---|---|
| `ksc-kerbals-roster-advanced` | GUI-1, `2026-09-22_2006` | ONE involved row, `Jebediah Kerman [Pilot] / Lost / L5-B4L-U1-C1 - Lost`, over `Available, no recorded flights (3)`. The phantom `Lars Kerman / Available` row of every earlier c1 capture is GONE (ruling 11): the VM line reads `roster=1+3 omittedStandIns=1`, and the dump carries the Lost hover `Lost on L5-B4L-U1-C1 (launched Y1, D52, 00:10). If that mission has a rewind point, re-flying it can undo the loss.` No Since column |
| `ksc-kerbals-outcomes-advanced` | GUI-1, `2026-09-22_2006` | `Date / Mission / Outcome`, no Crew column: `Y1, D51, 05:17 / L5-B4-U1-C1 / Still aboard` and `Y1, D52, 00:10 / L5-B4L-U1-C1 / Lost` - both dated by launch, the date the Lost hover repeats |
| `ksc-main-basic` | GUI-1, `2026-09-22_2006` | the Basic main window with `Kerbals` between Logistics and Settings, one gap either side (ruling 1 amended) |
| `cek-kerbals-roster-advanced` / `-expanded-` | GUI-5, `2026-09-22_2005` | `Jebediah Kerman [Pilot] / Reserved: aboard Jumping Flea / Jumping Flea - Still aboard` with `\u2514\u2500 Debwig Kerman [Pilot] / Stand-in for Jebediah Kerman / -` directly under it; the expanded capture opens only the plain bucket (Bill / Bob / Valentina `Available`) - there is no chain fold left to open |
| `cek-kerbals-outcomes-advanced` | GUI-5, `2026-09-22_2005` | `Y1, D01, 00:00 / Jumping Flea / Still aboard` under `Jebediah Kerman [Pilot] - 1 mission: 1 aboard` |
| `bdk-kerbals-roster-collapsed-advanced` / `-expanded-` | GUI-11, `2026-09-22_2004` | three slots, each owner `Reserved: aboard Kerbal X` / `Kerbal X #2 - Still aboard` over its own stand-in (`Jane` under Bill, `Sizon` under Bob, `Kathdan` under Valentina). The owner's hold names the VESSEL he ends aboard (`Kerbal X`, the last segment's recorded vessel) while Last flight names the MISSION (`Kerbal X #2`); the hover says both: `Held by the committed flight Kerbal X #2, which ends with this kerbal aboard Kerbal X. Passing time does not release it; ...` |
| `bdk-kerbals-roster-basic` NEW | GUI-11, `2026-09-22_2004` | the same Roster in BASIC: the window was open across the switch and the describe echo after it reads `complexity=basic ... openWindows=main,kerbals` (the window left the close set); the main window beside it shows the Basic launcher set with Kerbals |
| `bdk-kerbals-flights-unfolded-advanced` / `-folded-` | GUI-11, `2026-09-22_2004` | unchanged row content; the Mission column now expands and Outcome sits at the right edge |
| `bdk-kerbals-roster-owner-chain-advanced` RETIRED | - | its subject (one owner's chain fold) no longer exists; the `roster:Bill Kerman` step left the lane with it |

NO PICTURE THIS ROUND: `On EVA` (ruling 16) is photographed only by GUI-6's
`play-kerbals-roster-postcrew-advanced`, which was not re-flown - a sibling session
re-provisioned the automation instance (deployed hash `689f4033...`) between this round's
flights, and re-provisioning over it would have replaced the DLL under that session. The
swap screen message, the dismissal dialog and the Astronaut Complex badge tooltips (rulings
18, 19) are drawn by no census lane; their wording is pinned by `KerbalCrewNoticeTests` and
`StockUiOverlayControllerTests`. The overlay's `Lost on a committed flight (Parsek)` badge
landed after these flights (a `StockUiOverlayController`-only change), so the flown DLL
lacks it; no lane draws that surface either way.

Header-vs-cell delta measured off the 2026-09-15 dumps: **0 px on both tabs**, in both scenes. Roster
header cells at x=284 / 478 / 702 / 836 against body cells at the same four, widths
190 / 220 / 130 / expanding; Flights 284 / 418 / 632 / 746, widths 130 / 210 / 110 /
expanding. That is what the shared inset buys and what `TableRowInsetAlignmentTests`' two
new rows keep true.

NO PICTURE YET: the `as <stand-in>` crew note. Both crewed hosts reserve their owners
because the owners are still ABOARD, so no committed flight in any committed fixture was
flown BY a stand-in - every Crew cell in the readings above is `-`. The note's logic is
covered by six unit cells (raw crew, owner-aboard, out-of-chain crewmate, the replacement
fallback, raw-beats-map, no-slot); a photograph needs a fixture where a stand-in flew, and
none exists. Also unphotographed: `Lost`, `Retired`, `Assigned` beside a reservation on one
host, and the `Reserved for <owner> until <date>` form (it needs a reserved stand-in with a
finite return UT).

## 9. Residue

- **A reservation never lifts with game time** (ruling 13). Pinned by
  `KerbalReservationReleaseTests`; the window's wording tells the truth about it, and
  whether the backend should release a recovered kerbal at the flight's end is an owner
  decision filed in `docs/dev/todo-and-known-bugs.md`. (The old residue here - `Since`
  blank for four of six statuses - left with the column.)
- **The stand-in fallback is time-blind** (section 4). A flight whose recording lost its
  snapshot can be attributed to the CURRENT stand-in rather than the one who flew it. Fixing
  it properly means persisting the flown crew per recording, which is a schema change.
- **No Tracking Station host.** The window draws in FLIGHT and SPACECENTER only, as before.
- The `Assigned (<vessel>)` cell names the vessel but does not link to it; the window has no
  navigation affordance and gaining one would be a new control.
- **A stand-in's own flight is filed under the OWNER, always.** The producer is
  `KerbalsModule.ReverseMapCrewNames` (`KerbalsModule.cs:479-507`): before
  `PopulateCrewEndStates` writes `Recording.CrewEndStates` it maps EVERY chain member's name
  back to the slot owner, with no per-flight test at all - the reverse map and then
  `TryReverseMapCrewNameFromSlots` answer purely off names. So a stand-in who flew a whole
  mission of his own gets it filed under the kerbal he covers, contributes to that kerbal's
  bucket summary, and can FILL the owner's `Last flight` cell with a flight the owner never
  took; the stand-in's own group does not carry it at all. The `(flown by <stand-in>)`
  Mission-cell note is the mitigation, and it is a label on the owner's row rather than a
  fix. The real fix is to
  persist the flown crew PER RECORDING so the end states can be keyed by who actually flew -
  a schema change, so out of scope here. Filed in `docs/dev/todo-and-known-bugs.md`.
