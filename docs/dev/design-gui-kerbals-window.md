# Parsek - Kerbals window: the two column tables

Design authority for `Source/Parsek/UI/KerbalsWindowUI.cs` and its pure half
`Source/Parsek/UI/KerbalsPresentation.cs`. `design-gui-inventory.md` section 3.4 summarizes
the window and points here for the row model, the status vocabulary, the sizing and the seam.

## 1. What the window is for

In the mod's own words, from the two tab tooltips it ships:

- Roster: "What each kerbal is doing now: available, aboard, reserved, standing in, retired
  or lost."
- Flights: "Every mission a kerbal flew, and how each one ended for him."

The window answers two questions and nothing else: **who can I fly right now, and why not**,
and **what happened to this kerbal across my career**. It is READ-ONLY - no reserve,
unreserve, swap, clear or dismiss control - and its only mutations are transient, unpersisted
fold states (the plain-kerbal bucket and a per-kerbal flight-group fold). Every
crew mutation lives in `CrewReservationManager` and runs automatically inside the
recalculation walk.

It draws in BOTH complexity modes (ruling 1 below):
`UiSurface.MainButtonKerbals` is `visibleInBasic = true` (`UI/UiComplexityMode.cs`) and the
window is not in the Advanced -> Basic close set (`ParsekUI.BuildGatedWindowCloseSet`). Same
window, both tabs, no Basic-specific variant.

## 2. The operator's rulings (binding)

1. The window stays **read-only**, and it is **visible in Basic too**: it is the one
   surface that lists every reserved, stand-in, retired and lost kerbal with the reason, and
   it is read-only, so showing it costs a Basic player nothing he could break. (The VAB/SPH
   crew dialog lists a reserved kerbal greyed with the reason in his tooltip; whether that
   moves the window back to Advanced is the open decision D3 in
   `docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md`.)
2. Both tabs are **column tables** with ONE shared inset: header row and body rows inside the
   same container, through `ParsekUI.GetTableRowStyle` / `GetTableBodyBoxStyle`, and every body
   cell on a style built from `ParsekUI.GetTableCellStyle`, so header and cell TEXT align with
   zero delta (the house table pattern, pinned by `TableRowInsetAlignmentTests`).
3. Tab 1 is **Roster**, tab 2 is **Flights**. The seam tab TOKENS are `roster` and
   `outcomes`.
4. **Minimal, need-to-know, no spam.** Rows Parsek has something to say about are listed
   first and normally; plain available kerbals with no recorded flight collapse under ONE
   fold row, closed by default.
5. A **stand-in gets its own row**, with status "Stand-in for &lt;owner&gt;", drawn directly
   UNDER the owner whose seat it covers (ruling 14).
6. The Flights tab keeps **per-kerbal grouping**, and the bucket summary is the group HEADER -
   always shown, not a folded-only extra.
7. A Flights row keeps the **Timeline jump** on click.

8. **The Flights row unit is the MISSION, not the recorded segment** ("need-to-know, no
   spam"). A row per segment repeats one mission name and one date per segment and shows
   `Outcome unknown` for a mid-mission segment that simply has no ending. One row per
   (kerbal, tree); the segments are counted and listed in the row's hover text. Section 4
   carries the collapse rule.
9. **No Roster row expands.** A slot's chain is drawn as rows (ruling 14); a stand-in row
   says whose slot it covers in its status cell and stops there.
10. **"Stand-in for &lt;owner&gt;" is a per-MEMBER fact, not a per-chain one** - only the
    chain's ACTIVE occupant reads as one. See the status table in section 3.

11. **A deleted stand-in is not a row** (rec 1). A saved chain member who is displaced, not
    in the stock roster, not retired and not reserved is exactly the stand-in
    `KerbalsModule.ApplyToRoster` deleted as unused (`Stand-in '...' displaced -> deleted
    (unused)`); the slot keeps the NAME for later rewinds. Listing it would show an
    `Available` kerbal the player cannot find anywhere. Omitted rather than shown as retired: "retired" has a
    meaning (flew a committed flight, then displaced) that this kerbal does not have, and
    he is in no stock list the player can open. `KerbalsPresentation.IsDeletedStandIn`.
12. **There is no Since column** (rec 3). It would be `-` for most statuses, a reservation's
    date can read in the future after a rewind, and it would date a mission by its END while
    the Flights tab dates it by its START. When a kerbal died is in the Lost hover text.
13. **A finite hold reads its release date; an open-ended hold names what holds the
    kerbal** (rec 2 + open question 1). The kerbal is free once game time reaches the
    flight's recorded end (design 9.3), and the window only ever receives the reservations
    in force now (`KerbalsModule.ActiveReservations`). So a finite hold reads
    `Reserved until <date>` (`KerbalsPresentation.FormatReleaseDate`, the window's own date
    formatter) and its hover ends `Free again from <date>, when that flight ends.`
    (`FormatReservationReleaseRule`). An open-ended hold (the flight ends with the kerbal
    aboard, or has no recorded ending) still reads `Reserved: aboard <vessel>` /
    `Reserved: <mission>` and its hover ends with `ReservationHoldRule`, which is still
    true for it. A reserved stand-in reads `Reserved for <owner>` either way. The date is
    the LAST walk's view: the Space Center, the Tracking Station and the crew dialog run a
    crossed-an-end check, but in flight the release lands at the next warp exit, commit or
    scene change, so the cell can briefly show a date that has just passed.
14. **The Roster is grouped by slot** (rec 7). Each owner row is followed directly by its
    chain members as rows of their own (tree glyph in the Name cell), so there is no
    per-owner chain fold and no `roster:<kerbal name>` expand key.
15. **Lost points at the way back** (rec 4): the status hover names the mission and says a
    re-fly from a rewind point can undo the loss - only when an open Re-Fly actually reaches
    that death (owner ruling 2026-09-27; see the Status hover table) (TRUE: a Re-Fly merge tombstones the
    superseded flight's `KerbalAssignment`+Dead row - `TombstoneEligibility.IsKerbalDeath` -
    which is what the permanent reservation came from; flown by
    `CL-3-refly-crew-tombstone`), and every Roster row's Last flight cell is the same
    Timeline cross-link a Flights row is.
16. **`On EVA`**, not `Assigned (<own name>)` (rec 5): an EVA kerbal is his own vessel.
17. **Flights: one date, no Crew column** (rec 8). The Date is the mission's LAUNCH - the
    date the Missions window's `Start time` column and the Timeline place a mission by - and
    nothing in the window dates a mission by its end (the Lost hover says "launched <date>"
    too). The rare stand-in note is in the Mission cell as `(flown by <stand-in>)`.
18. **Crew consequences get their existing channels** (rec 6, outside this window): a
    one-shot screen message when `CrewReservationManager.SwapReservedCrewInFlight` actually
    swaps a reserved kerbal out of a seat, and a refused dismissal raises the existing
    `Action Blocked` dialog (`CommittedActionDialog.ShowBlocked`) like its four siblings.
19. **One crew vocabulary** (rec 9): the Astronaut Complex overlay's badge tooltips use the
    window's words - `Reserved - held by a committed flight (Parsek)`, `Reserved for <owner>
    - ...` only for a stand-in in someone else's slot, `Lost on a committed flight (Parsek)`
    for a death reservation (permanent, or with the stock respawn pending: the Status cell then
    reads `Lost until <date>`, owner ruling S8), `Retired stand-in (Parsek)` - and an owner is
    never labelled as reserved for his own slot.

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
| Lost | `Lost` / `Lost until <date>` | `KerbalSlot.OwnerPermanentlyGone`, or a loss hold (`KerbalsModule.IsLossHold`: a permanent death, or a death whose stock respawn is still pending). `Lost until <date>` names the respawn (`FormatLostUntilDate`) when one is pending and the text fits the cell |
| Retired | `Retired` | in the retired set |
| Reserved | `Reserved until <date>` / `Reserved: aboard <vessel>` / `Reserved: <mission>` / `Reserved` / `Reserved for <owner>` | a reservation in force NOW (`KerbalsModule.ActiveReservations`). A finite hold (a Recovered flight's end) reads `Reserved until <date>` (ruling 13). Otherwise the flight is `ResolveHoldFlight`: for an open-ended (`+inf`) hold the latest mission that ends Still aboard, else the latest-ending mission; `aboard <vessel>` when that mission ends with the kerbal aboard (vessel = its last segment's recorded vessel), the mission name otherwise, bare `Reserved` when the kerbal has no flight of his own; a composed text past `StatusCellMaxChars` falls back to `Reserved: still aboard` / `Reserved`. The `for <owner>` form only when the kerbal is a stand-in in someone else's chain |
| Stand-in | `Stand-in for <owner>` / `Stand-in for <owner> (aboard <vessel>)` / `(on EVA)` | the kerbal is the **ACTIVE** occupant of some OTHER kerbal's slot chain |
| Assigned | `Assigned (<vessel>)` / `On EVA` | the kerbal is aboard a live vessel (ghost-map ProtoVessels excluded first); `On EVA` when that vessel is his own EVA vessel (`Vessel.isEVA`, gathered into `RosterKerbal.AssignedVesselIsEva`) |
| Available | `Available` | none of the above |

The status cell's hover text (`FormatStatusTooltip`):

| Status | Hover |
|---|---|
| Lost | `Lost on <mission> (launched <date>).` - the death mission is the latest mission that ends Dead; with none, `Lost on a flight on timeline.` With a stock respawn pending, `Stock respawn returns this kerbal on <date>.` follows (`FormatLostRespawnRule`). Then `If that mission has a rewind point, re-flying it can undo the loss.` (`LostReFlyRemedy`), shown ONLY when an open Unfinished Flight exists whose Re-Fly would reach this loss (the death recording lies in the stretch an open slot's re-fly rewrites: he died aboard that slot's vessel or on an EVA from it; `KerbalsPresentation.ShouldOfferLostReFlyRemedy` over `EffectiveState.ComputeOpenSlotReFlyReachRecordingIds`, owner ruling 2026-09-27); otherwise it is omitted. The stock-screen Lost explanation (`ReservationExplanation.KerbalLost`) follows the same rule. |
| Reserved | `Held on timeline by the flight <mission>, which ends with this kerbal aboard <vessel>` / `recovered` / `, which has no recorded ending`, then the release rule: `. Free again from <date>, when that flight ends.` for a finite hold (`FormatReservationReleaseRule`), `. Passing time does not release it; it lasts while that flight stays on timeline.` for an open-ended one (`ReservationHoldRule`); a reserved stand-in reads `Held by a flight on timeline flown in <owner>'s seat. ...` |
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

There is no chain fold (ruling 14); the grouping itself carries the chain: an owner row carries
`SlotMemberCount`, each member row `Depth`, `IsLastInSlot` (the glyph) and `MemberStatus`
(`Active` / `Retired` / `Displaced`, the chain's own classification), and `SlotOwnerName`,
which its status cell spells out when it matters.

### Empty state

One line, `No kerbals in the roster.`, reachable only on a save whose stock roster is empty -
impossible in a career, constructible in a hand-built sandbox or science save.

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
| Mission | expands | `MissionStore.FindOriginalMission(rec.TreeId).Name`, falling back to `Recording.VesselName`, then `(unnamed)`, plus ` (flown by <stand-in>)` when someone else flew the seat (`FormatMissionCell`). The raw recording name and id are in the cell's hover text (`DescribeFlightRow`), naming the LAST segment - the one a click jumps to |
| Outcome | 110 px | `FormatOutcome` of the FINAL end state: `Recovered` / `Lost` / `Still aboard` / `Outcome unknown`. Its hover (`DescribeFlightRowOutcome`) is the outcome sentence plus the segment list, e.g. "... `3 segments: Still aboard, Still aboard, Recovered`." - dropped on a single-segment mission, where it would add nothing |

Sort: the Date column ascending within a kerbal (mission start, ties broken on the end), kerbals
by name (ordinal). Every cell is a label-styled button carrying the row's Timeline cross-link,
so a click anywhere on the row scrolls the Timeline to the mission's LAST recorded segment
(`OnFatesRowClicked` -> `TimelineWindowUI.ScrollToRecording`, which OPENS the Timeline when it
is closed).

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

Why: on `fixtures/saves/bdock-recorded` a per-segment row model gives each of the three
reserved kerbals FIVE rows for two missions - four at `Y1, D01, 02:29` with one mission name,
one of them `Outcome unknown` for a 2-point mid-mission segment - and the one fact the player
wants (how it ended) sits on a row he cannot identify as last.

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
| Roster fixed columns | 210 + 220 = **430 px** | longest realistic cells: `\u2514\u2500 Valentina Kerman [Scientist]` (31 chars with the glyph), `Reserved for Valentina Kerman` (29 chars) |
| Flights fixed columns | 130 + 110 = **240 px** | the compact date and `Outcome unknown` (15 chars); Mission expands |
| the Date column | **130** | `KSPUtil.PrintDateCompact` renders to the minute, so a cell reads `Y1, D01, 02:29` - 14 chars, about 98 px at the skin's ~7 px advance (it clips inside 80, measured) |
| `DefaultWindowWidth` | **760** | leaves the expanding Last flight column about 280 px beside the 430 px of fixed columns |
| `MinWindowWidth` | **586** | 430 fixed + 8 inter-column cell margin + 4 before the expanding column + 100 of readable sliver for it + 28 of window chrome + 16 of scrollbar gutter. Below that IMGUI clips the pinned widths rather than reflowing them. Pinned term by term by `KerbalsWindowUITests.Sizing_*` |
| the margin / chrome terms | 4 per fixed column / 28 / 16 | measured off census dumps: each column consumes its width + 4 and the first cell sits 14 px inside the window edge; the gutter is `ParsekUI.DefaultVerticalScrollbarFootprintWidth` |
| `DefaultWindowHeight` / `MinWindowHeight` | 400 / 150 | |

Tooltip budget: `TooltipEchoBudgetTests.StripWindows` pins this file at `760f, 5,
DoubleLine`, so the budget is `2 * (760 - 30) / 7 = 208` characters. The floor is 5;
the file's literal `GUIContent` tooltips are the two tab labels, three column
headers (`Last flight`, the Flights `Date` and `Mission`), the plain-bucket fold, the Flights
group fold and the Flights row's Date cross-link, plus the Roster Last flight cell's
`LastFlightJumpTooltip` constant. RUNTIME-built tooltips are skipped by the scanner by design:
the Mission cell's `DescribeFlightRow`, the Outcome cell's `DescribeFlightRowOutcome`, and the
Roster status cell's `StatusTooltipText`. The longest runtime tooltip is the Reserved-aboard
hover, about 170 characters with two mission-length names, under the 208 budget.

## 6. Seam contracts

| Contract | Value |
|---|---|
| `WindowIdKey` | `ParsekKerbals`, ONE literal, hashed at both the `GUILayoutWindow` call and `UiWindowHandle.GetWindowId` |
| tab tokens | `roster`, `outcomes` |
| `SelectedTabForTesting` / `TabCountForTesting` / `WindowRectForTesting` | the seam's window accessors |
| `CachedViewModelForTesting` | the built view model, so the expand seam's key enumerations are unit-testable headlessly (in production the first drawn frame seeds it, which is why `op=expand` is two-phase) |

**`op=expand window=kerbals`.** Two prefixes, one per tab, because the window keeps one
expansion collection per tab:

| Key | Drives |
|---|---|
| `roster:(available)` | the plain-kerbal fold row (`KerbalsWindowUI.PlainBucketKey`) - the ONLY Roster key; a roster with no plain kerbal offers no Roster key at all |
| `flights:<kerbal name>` | that kerbal's flight group (INVERTED on the production side: `foldedKerbals` holds what is FOLDED; the wire speaks "expanded") |
| `all` / `none` | both sets at once - what a census needs, since the ids are save-specific |

Mirrored by `hlib.UIACTION_EXPAND_PREFIXES["kerbals"]`, and the two sides are kept byte-equal
by `test_hlib.GuiCensusSeamVerbTests.test_the_expand_prefix_map_mirrors_the_c_sharp_tables`
plus `GuiCensusApplierSourceGateTests.ResolveExpandSets_WiresExactlyThePrefixesTheParse-
Accepts`.

## 7. Data routing and logging

The view model is gathered once and cached (`InvalidateCache` drops it; the fold sets
deliberately survive).

### What refreshes the tab

`LedgerOrchestrator.OnTimelineDataChanged` covers every LEDGER-side change, but the view model
is ALSO built from live state that no ledger write touches: the stock roster walk
(`CrewRoster.Crew` / `.Applicants` / `.Tourist`) and the live crew-to-vessel map
(`GatherAssignedVessels`). Without more, a transfer, an EVA, a board, a hire or a dismissal
would leave a stale `Assigned (<vessel>)` cell - or a missing row - until some unrelated ledger
write dropped the cache. So the window subscribes eight stock events of its own:

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
subscribe without its matching remove, reds the suite. Recordings come from `EffectiveState.ComputeERS()` - never
`RecordingStore.CommittedRecordings`, which `scripts/grep-audit-ers-els.ps1` gates for this
file. Every `FlightGlobals.Vessels` walk checks `GhostMapPresence.IsGhostMapVessel(pid)`
first, so a ghost's recorded crew never reads as a live assignment.

Batch counters, one summary line each (the house convention): the roster gather
(`crew=`, `managedNonCrew=`, `skipped=`), the live crew map (`vessels=`, `ghostsSkipped=`,
`seated=`), the mission-name resolution, and the composed view model
(`roster=<involved>+<plain> flightGroups= endStates=`). The two gather helpers are
fail-soft with a Verbose line naming the exception type: the headless xUnit host has no
`HighLogic` and no `FlightGlobals`, and the pure builders then run off the ledger's own names.

## 8. Pictures

Census captures of the window (PNG plus `.gui.json`), all read against the rulings above:

| Label | Lane | Shows |
|---|---|---|
| `ksc-kerbals-roster-advanced` / `ksc-kerbals-outcomes-advanced` | GUI-1 (`c1-gui`) | one involved row (`Lost`, with the Lost hover in the dump) over `Available, no recorded flights (3)`; the Flights tab dated by launch |
| `cek-kerbals-roster-advanced` / `-expanded-advanced` | GUI-5 (`career-earned-ksc`) | `Reserved: aboard Jumping Flea` with `\u2514\u2500 Debwig Kerman [Pilot] / Stand-in for Jebediah Kerman` directly under it; the expanded form opens the plain bucket |
| `cek-kerbals-outcomes-advanced` | GUI-5 | one mission row under `Jebediah Kerman [Pilot] - 1 mission: 1 aboard` |
| `bdk-kerbals-roster-collapsed-advanced` / `-expanded-advanced` / `-basic` | GUI-11 (`bdock-recorded`) | three slots, each owner `Reserved: aboard Kerbal X` over its own stand-in; the Basic capture is the same Roster after a switch to Basic with the window left open |
| `bdk-kerbals-flights-unfolded-advanced` / `-folded-advanced` | GUI-11 | three groups, two mission rows each; the folded form shows the headers alone |
| `fs-kerbals-roster-fresh-advanced` / `fs-kerbals-outcomes-empty-advanced` | GUI-8 (fresh science save) | the header plus one fold row `Available, no recorded flights (4)`; `No recorded flights with crew yet.` |
| `play-kerbals-roster-precrew-advanced` / `-postcrew-advanced` / `play-kerbals-flights-postcrew-advanced` | GUI-6 (FLIGHT) | the live-crew column before and after one real `EvaExit` with no ledger write between, i.e. the live-crew refresh; the EVA kerbal reads as his own vessel |

No picture: the `(flown by <stand-in>)` note (no committed fixture has a flight flown BY a
stand-in; the logic is covered by unit cells), `Retired`, `Lost until <date>`, `Reserved for
<owner> until <date>`, and `Assigned` beside a reservation on one host. The crew-swap screen
message, the dismissal dialog and the Astronaut Complex labels (rulings 18, 19) are drawn by no
census lane; their wording is pinned by `KerbalCrewNoticeTests`,
`ReservationExplanationTests` and `StockUiDecorationQueryTests`. The Astronaut Complex labels
read the shared reservation explanation (`ReservationExplanation.KerbalOnFlight` /
`KerbalLost`), which reuses this window's statuses and its Lost remedy.

## 9. Residue

- **The stand-in fallback is time-blind** (section 4). A flight whose recording lost its
  snapshot can be attributed to the CURRENT stand-in rather than the one who flew it. Fixing
  it properly means persisting the flown crew per recording, which is a schema change.
- **No Tracking Station host.** The window draws in FLIGHT and SPACECENTER only.
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
