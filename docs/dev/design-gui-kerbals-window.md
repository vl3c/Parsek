# Parsek - Kerbals window: the two column tables

Design authority for `Source/Parsek/UI/KerbalsWindowUI.cs` and its pure half
`Source/Parsek/UI/KerbalsPresentation.cs`. Written with the 2026-09-15 rebuild; it replaces
the row-model half of `design-gui-inventory.md` section 3.4, which now points here.

## 1. What the window is for

In the mod's own words, from the two tab tooltips it ships:

- Roster: "What each kerbal is doing now: available, aboard, reserved, standing in, retired
  or lost."
- Flights: "Every recorded flight a kerbal took, and how each one ended."

The window answers two questions and nothing else: **who can I fly right now, and why not**,
and **what happened to this kerbal across my career**. It is READ-ONLY - no reserve,
unreserve, swap, clear or dismiss control - and its only mutations are three transient,
unpersisted fold states (a per-row chain expansion, the plain-kerbal bucket, and a per-kerbal
flight-group fold). Every crew mutation lives in `CrewReservationManager` and runs
automatically inside the recalculation walk.

It stays Advanced-only: `UiSurface.MainButtonKerbals` is `visibleInBasic = false`
(`UI/UiComplexityMode.cs`), and an Advanced -> Basic switch force-closes it. Basic has no
picture of it by construction.

## 2. The operator's rulings (2026-09-15, binding)

1. The window stays **Advanced-only and read-only**. The rebuild is a restructuring inside an
   existing window, which is not a new player-facing surface.
2. Both tabs are **column tables** with ONE shared inset: header row and body rows inside the
   same container, through `ParsekUI.GetTableRowStyle` / `GetTableBodyBoxStyle`, so header and
   cells align with zero delta (the house pattern from PRs #1679 / #1680).
3. Tab 1 is renamed **Roster** (from "Roster State"); tab 2 is renamed **Flights** (from
   "Mission Outcomes"). The seam tab TOKENS are unchanged: `roster` and `outcomes`.
4. **Minimal, need-to-know, no spam.** Rows Parsek has something to say about are listed
   first and normally; plain available kerbals with no recorded flight collapse under ONE
   fold row, closed by default.
5. A **stand-in gets its own row**, with status "Stand-in for &lt;owner&gt;".
6. The Flights tab keeps **per-kerbal grouping**, and the bucket summary is the group HEADER -
   always shown, not a folded-only extra.
7. A Flights row keeps the **Timeline jump** on click.

### Two readings the rulings did not spell out, decided here

- **"Reserved for &lt;owner&gt;" is dropped when the owner is the kerbal himself.** A reserved
  slot owner is reserved for his own return, so "Jebediah Kerman | Reserved for Jebediah
  Kerman until Y1, D23" would repeat the Name column. An owner row reads "Reserved until
  &lt;date&gt;"; a reserved STAND-IN reads "Reserved for &lt;owner&gt; until &lt;date&gt;",
  which is the case the wording is for.
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
| `KerbalsModule.Slots` (owners AND chain members) | added when the stock list does not carry the name (Parsek-created stand-ins the roster dropped) |
| `KerbalsModule.GetRetiredKerbals()` | same |

Sort: kerbal name, `StringComparer.Ordinal` ascending, within each partition. Not
user-selectable.

### Columns and their sources

| Column | Width | Source |
|---|---|---|
| Kerbal | 190 px | fold arrow (when the row carries a chain) + `"Name [Trait]"`; trait from the live roster, falling back to `KerbalSlot.OwnerTrait`. The bracket is dropped when the trait is unknown |
| Status now | 220 px | `KerbalsPresentation.ClassifyStatus` + `FormatStatus`; see the vocabulary below |
| Since | 80 px | `FormatSince`: the calendar date the current status started, for the two statuses the mod dates; else `-` |
| Last flight | expands | `FormatLastFlight`: `"{mission} - {outcome word}"` from the kerbal's latest flight row; else `-` |

### The status vocabulary, in resolution order

| Status | Text | Predicate |
|---|---|---|
| Lost | `Lost` | `KerbalSlot.OwnerPermanentlyGone`, or a permanent (`IsPermanent`) reservation |
| Retired | `Retired` | in the retired set |
| Reserved | `Reserved until <date>` / `Reserved until recovery` / `Reserved for <owner> until <date>` | a live reservation; `until recovery` is the open-ended (`+inf`) branch; the `for <owner>` form only when the kerbal is a stand-in in someone else's chain |
| Stand-in | `Stand-in for <owner>` | the kerbal is a chain member of some slot |
| Assigned | `Assigned (<vessel>)` | the kerbal is aboard a live vessel (ghost-map ProtoVessels excluded first) |
| Available | `Available` | none of the above |

`Since` is populated for exactly two of the six: a **loss** is dated by the flight whose end
state is Dead, and a **reservation** by the kerbal's latest recorded flight (the flight that
created the hold). Retired, stand-in, assigned and available carry no recorded start, so they
read `-` rather than a number that would be a guess.

### The fold

`IsPlainRow` is exactly `Available` + no slot + no recorded flight. Those rows go to the
`Plain` partition and draw behind one fold row, `Available, no recorded flights (N)`, closed
by default and dimmed when opened (name and trait only in effect - every other cell is `-`).
The fold state lives in the window, never on disk.

A row carrying a replacement chain (the owner's row and each stand-in's row, both pointing at
the same chain) expands to the retained chain view: `FormatRosterChainMemberText` +
`FormatChainMember`, tags `active` / `retired` / `displaced`, unchanged from the pre-rebuild
window.

### Empty state

One line, `No kerbals in the roster.`, reachable only on a save whose stock roster is empty -
impossible in a career, constructible in a hand-built sandbox or science save. The old
`No reserved crew, stand-ins, or retired kerbals.` is gone: on a career it was a lie about a
roster that had four kerbals in it.

## 4. Tab 2 - Flights

Per-kerbal groups, each a fold header plus one row per recorded flight.

Group header: `Name [Trait] - N flights: n recovered, n lost, n aboard, n unknown`
(`FormatFlightGroupHeader`). Zero buckets are omitted, `1 flight` is singular, and the
trait bracket is dropped when unknown. Always drawn, folded or not.

| Column | Width | Source |
|---|---|---|
| Date | 80 px | `KerbalsWindowUI.FormatRowDate` = `KSPUtil.PrintDateCompact(ut, true)` with the Timeline's own F0 fallback, so both windows date the same flight identically |
| Mission | 210 px | `MissionStore.FindOriginalMission(rec.TreeId).Name`, falling back to `Recording.VesselName`, then `(unnamed)`. The raw recording name and id are in the cell's hover text (`DescribeFlightRow`) |
| Outcome | 110 px | `FormatOutcome`: `Recovered` / `Lost` / `Still aboard` / `Outcome unknown`; the last carries the hover "The flight has no recorded ending." |
| Crew | expands | `"as <stand-in>"` when someone else flew the seat, else `-` |

Sort: date ascending within a kerbal, kerbals by name (ordinal). Every cell is a
label-styled button carrying the row's Timeline cross-link, so a click anywhere on the row
scrolls the Timeline to that flight (`OnFatesRowClicked` -> `TimelineWindowUI.ScrollToRecording`,
which OPENS the Timeline when it is closed - finding P6).

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
   column reads "as &lt;name&gt;" either way.

The choice is the primary one BECAUSE the fallback is time-blind: the reverse-map's own order
(`ReverseMapCrewNames` first, slot chains second) is about names, not about which flight.

## 5. Sizing

| Number | Value | Arithmetic |
|---|---|---|
| Roster fixed columns | 190 + 220 + 80 = **490 px** | longest realistic cells: `Valentina Kerman [Scientist]` (28 chars), `Reserved for Valentina Kerman until Y1, D23` (43 chars), one compact date |
| Flights fixed columns | 80 + 210 + 110 = **400 px** | one compact date, a mission name, `Outcome unknown` (15 chars) |
| `DefaultWindowWidth` | **700** (was 410) | 490 + 200 for the expanding "Last flight" column + chrome. The old 410 was half of Career's 820 so the two could sit side by side; two column tables do not fit in 410, and 700 still leaves Career's 820 room on a 1920-wide screen |
| `MinWindowWidth` | **520** (was 280) | 490 of fixed columns plus a readable sliver for the expanding one. Below that IMGUI clips the pinned widths rather than reflowing them |
| `DefaultWindowHeight` / `MinWindowHeight` | 400 / 150 | unchanged |

Tooltip budget: `TooltipEchoBudgetTests.StripWindows` pins this file at `700f, 5,
DoubleLine`, so the budget is `2 * (700 - 30) / 7 = 191` characters (it was
`2 * 380 / 7 = 108`). Every literal in the file is still under the OLD 108, so the raise
loosens nothing in practice - it stops the gate budgeting a width the window no longer opens
at. The floor of 5 is unchanged and the file carries 9 literal `GUIContent` tooltips (two tab
labels, three column headers, the plain-bucket fold, the chain expand, the Flights fold, the
row cross-link), so removing a control still reds the row.

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
| `roster:<kerbal name>` | that row's replacement-chain view |
| `roster:(available)` | the plain-kerbal fold row (`KerbalsWindowUI.PlainBucketKey`) |
| `flights:<kerbal name>` | that kerbal's flight group (INVERTED on the production side: `foldedKerbals` holds what is FOLDED; the wire speaks "expanded") |
| `all` / `none` | both sets at once - what a census needs, since the ids are save-specific |

Mirrored by `hlib.UIACTION_EXPAND_PREFIXES["kerbals"]`, and the two sides are kept byte-equal
by `test_hlib.GuiCensusSeamVerbTests.test_the_expand_prefix_map_mirrors_the_c_sharp_tables`
(new) plus `GuiCensusApplierSourceGateTests.ResolveExpandSets_WiresExactlyThePrefixesTheParse-
Accepts`.

## 7. Data routing and logging

The view model is gathered once and cached (`InvalidateCache` drops it; the fold sets
deliberately survive). Recordings come from `EffectiveState.ComputeERS()` - never
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

| Label | Lane, run | Reads |
|---|---|---|
| `cek-kerbals-roster-advanced` (BEFORE) | GUI-5, `2026-09-11_1553` | ONE row: `> Jebediah Kerman [Pilot] - reserved  (1)` on a career with four stock kerbals and a stand-in |
| `cek-kerbals-outcomes-advanced` (BEFORE) | GUI-5, `2026-09-11_1553` | `v <b>Jebediah Kerman</b>` plus two rows, `Jumping Flea - Recovered at UT 342` / `- Aboard at UT 348` |
| `cek-kerbals-roster-advanced` (AFTER) | GUI-5, `2026-09-15_1745` | Jeb `Reserved until Y1, D1` with his `Since` and `Last flight` cells, Debwig Kerman as `Stand-in for Jebediah Kerman`, the other stock kerbals behind `Available, no recorded flights (4)` |
| `cek-kerbals-roster-expanded-advanced` (NEW) | GUI-5, `2026-09-15_1745` | the same with every fold open: the four plain kerbals listed dimmed and Debwig drawn as Jeb's chain member |
| `cek-kerbals-outcomes-advanced` (AFTER) | GUI-5, `2026-09-15_1745` | `Jebediah Kerman [Pilot] - 2 flights: 1 recovered, 1 aboard` over two dated rows |
| `bdk-kerbals-*` (6) | GUI-11, `2026-09-15_1802` | the crewed corpus: three reserved slots, three stand-in rows, per-kerbal flight groups, and the stand-in chain view |

Header-vs-cell delta measured off the dumps: **0 px on both tabs**, which is what the shared
inset buys and what `TableRowInsetAlignmentTests`' two new rows keep true.

## 9. Residue

- **`Since` is blank for four of six statuses.** Retirement, stand-in placement and a live
  crew assignment have no recorded start date anywhere in the ledger; dating them would mean
  a producer change (a new persisted field), which this rebuild deliberately did not make.
- **The stand-in fallback is time-blind** (section 4). A flight whose recording lost its
  snapshot can be attributed to the CURRENT stand-in rather than the one who flew it. Fixing
  it properly means persisting the flown crew per recording, which is a schema change.
- **No Tracking Station host.** The window draws in FLIGHT and SPACECENTER only, as before.
- The `Assigned (<vessel>)` cell names the vessel but does not link to it; the window has no
  navigation affordance and gaining one would be a new control.
