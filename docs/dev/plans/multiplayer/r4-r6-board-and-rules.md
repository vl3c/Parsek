# The board and the rules (R4, R6) - implementation plan

Status: PLAN (2026-11-14), not started. Design sections covered: 6.5, 6.6, 7.8, 7.9, 7.10,
7.11, 7.12 (the scoring and display half), 7.13, 7.14, section 8 cases 7, 10, 11, 12, 14, 19,
20, 21, 28, section 11 (board, verification, no retakes, end date, two dates), 15.10 (checks
pipeline, house rules as functions), 15.12, 15.14 (the GUI gates and the uncalled ratchet),
addition A6. Inputs captured by other slices: A1, A3. Phases: R4 "have a race" and R6
(end date, disclosure, no retakes, notes, poll tuning).

## 1. Goal and scope

Built here:

- **R4.** In `RaceCore` (headless, model layer): the race calendar and whole-day comparison
  (extending R1-10's day-unit check); the ladder table `standard-1`; the ladder filter that
  maps the stock ids of R3E-11's milestone table onto ladder rows when the board is built;
  the claim-check pipeline with its first check ("the replay is the proof"); the board
  (holders, co-holders, exact unrounded shares, scores, every date kept with its status) and
  the per-player records it reads, built from R3E-18's verdicts and R1-08's manifests; the
  holder-change events and their message text, appended to `SyncResult.Messages`; the
  window's row model. In `UI`: the shared table-window chrome (A6) and `SpaceRaceWindowUI`
  (Flight and Space Center), its launcher in the main window (race saves only), its hover
  text, click-to-watch (the hover half; the camera half waits with P3b), its gallery mock
  states and every GUI registration a new window trips; the Sync now button and sync status
  text in R1-18's Settings section. On R1-08's `RaceRuntime` facade: `CurrentBoard`, the
  SHOW setter, the watch stub (if R3W-26 has not landed), `KnownParts` / `KnownBodies`.
- **R6.** The tool-mark check (a tool marks every milestone its flight reaches afterwards,
  across the recordings of its tree) and the debug-completion drop (OV-34); the "no
  retakes" house rule over `SEEN` receipts and `since`; the end date (last complete standing
  stamped before it, the final board, joint winners, "after the end", "arrived late");
  disclosure marks (mods digest, difficulty digest, test tooling); the missing-parts,
  newer-version and cannot-be-watched notes; the end-of-race messages; the no-retakes
  preview the Join dialog shows; poll tuning.

Almost all of it is pure rules over plain values, so it is built and unit-tested headless
against hand-written standings, in parallel with the rest of the feature.

Not built here: capturing the facts in the player's own game (A1 reach rows and the
milestone table `RaceCore/MilestoneTable.cs`: R3E-11; A3 tool use: R3E-07 to R3E-10; the
debug-completion fact: R3E-09; test tooling: R3E-15), the standing codec (R3E-01, R3E-04),
the chain, the witness log and the receipts as files (R3E-19), `RaceSync.Run` (R3E-04), the
import (R3E-18) and its verdict type (declared by R3E-04), the driver that posts messages
(R3E-22) and the sync ticks (R3E-17), ghosts, leftovers, flags, the SHOW reader (R3W-13) and
the watch camera's lane support (R3W-26), the setup dialog (R1-14, R1-17), the manifests,
`RaceSession` and the `RaceRuntime` facade (R1-08), the race mark and gate (R1-03, R1-04),
slots (R1-09), the look table (R5-07), the site table (R2-01), the Settings section (R1-18).
Nothing here writes to the save, the ledger, or any file, except the SHOW setter (R4-10),
which writes `local.cfg` through the `RaceLocal` codec (read side R1-08, write side R1-10).

## 2. Starts after

- **Prerefactors:** P11-01, P11-02 (harness specs derive the window count; the two
  `ParsekUI` constructors share one helper) before R4-09. P12 and R1-01 (the `RaceCore`
  module with its layer, headless entry and forbidden edges) before any `RaceCore` file
  here. P3b (lane-qualified watch target, P3b-04) is deferred out of R0b and built when
  watching a rival flight is scheduled; the camera half of R4-11 waits with it (and with
  R3W-26), and R4-11 ships the hover half only.
- **Additions:** A1 (reach rows over the effective ledger, every row, earliest first, and
  the milestone table with covering resolution and `since`) is R3E-11; A3 (per-flight tool
  fact) is R3E-07, R3E-08, R3E-09, R3E-10. R4-03 and R6-01 read what they produce through
  the standing codec. A6 is R4-08 of this plan.
- **Other plans' artifacts:**
  - R1: `RaceCore` module (R1-01); `RaceLink` and the race scenario (R1-03);
    `RaceGate.IsRaceSave` (R1-04); `RaceManifest`, `PlayerManifest`, `RaceLocal`,
    `RaceSession` and the `RaceRuntime` facade with its one static (R1-08, OV-26);
    `RaceInstallCheck`, its refusal list and the minimal `date-unit-not-day` check created
    under R4-01's name and reason (R1-10, OV-30); the Join dialog (R1-17); the Settings
    section (R1-18); the CLAUDE.md UI exception naming the Space Race window (R1-14,
    OV-42); `RaceFormat.Current` (R1-03, OV-16);
  - R2 and R5: `SiteTable` (R2-01); `RaceLookTable`, look index to UI color (R5-07, OV-41);
  - R3 export and exchange: the standing value types the codec produces (`Standing` and its
    child records, section 4 "Input shapes"; R3E-01, R3E-04), `RaceSync.Run` and
    `SyncResult` (R3E-04), the `MILESTONES` block and `MilestoneTable.cs` (R3E-11, OV-29),
    the `TOOL` children (R3E-10), the debug-completion fact (R3E-09, OV-34), the
    `DISCLOSURE` block (R3E-15), the poll (R3E-17; `RequestSyncNow` is specified there and
    lands with R4-09), the verdict `RivalStandingVerdict` (declared by R3E-04, filled by
    R3E-18), the witness first-seen times (R3E-19), the kept older
    standings (6.3, R3E-21); R3E-22's driver site posts `SyncResult.Messages` (OV-38);
  - R3 rival world: the scene calls on the facade (R3W-08), the SHOW reader (R3W-13), the
    watch call that fills R4-11's stub (R3W-26).
- **Spikes:** S3 (the four look colors) before R4-10 draws final colors (R5-07's table holds
  placeholder colors until S3a); S8a, S8b before R6-10; S9 before R6-01 is final (it fixes
  the tool list A3 captures and which debug completions R3E-09 can observe; the check
  itself does not depend on the list).

## 3. What the code looks like today

Milestones and stock ids:

- The ledger keys a milestone by a body-qualified id: `GameStateRecorder.QualifyMilestoneId`
  returns `body.name + "/" + node.Id` for a node class with a `body` field and the bare id
  otherwise (`Source/Parsek/GameStateRecorder.Handlers.cs:1897-1919`); the body field may
  be public or private (`:1928-1936`).
- `GameAction` carries `RecordingId` (`Source/Parsek/GameActions/GameAction.cs:537`) and
  `MilestoneId` (`:899`). No query returns every reach row of a milestone with its date and
  flight: the nearest is `CommittedMilestoneReach.CollectRowsAtOrBefore`
  (`Source/Parsek/GameActions/CommittedMilestoneReach.cs:177`), and once-ever rows are
  told from the four repeatable records by `IsOnceEverMilestoneRow` (`:101`) and
  `MilestonesModule.IsRepeatableWorldRecordMilestone`
  (`Source/Parsek/GameActions/MilestonesModule.cs:401-407`). A1 adds the query.
- Stock node ids (decompiled `Assembly-CSharp.dll`, KSP 1.12.5, read today with `ikdasm`):
  `KSPAchievements.CelestialBodySubtree::.ctor` builds the same nodes for every body:
  `Orbit`, `Escape`, `Landing`, `Splashdown`, `Science`, `Rendezvous`, `Docking`,
  `Spacewalk`, `SurfaceEVA`, `FlagPlant`, `BaseConstruction`, `StationConstruction`,
  `CelestialBodyTransfer`, `Flight`, `Suborbit`, `Flyby`, and three `CelestialBodyReturn`
  nodes whose id is `"ReturnFrom" + ReturnFrom` (enum `FlyBy`, `Orbit`, `SubOrbit`,
  `Flight`, `Surface`; the subtree builds `FlyBy`, `Orbit` and `Surface`). So the qualified
  ids are `Mun/Flyby`, `Mun/Orbit`, `Mun/Landing`, `Mun/Splashdown`, `Mun/SurfaceEVA`,
  `Mun/FlagPlant`, `Mun/ReturnFromFlyBy`, `Mun/ReturnFromOrbit`, `Mun/ReturnFromSurface`.
  `KSPAchievements.ReachSpace::.ctor` passes `"ReachedSpace"` and the class has no `body`
  field, so its id is the bare `ReachedSpace`. `CelestialBodyOrbit.body` is public,
  `CelestialBodyLanding.body` private (both found by the lookup above).

Dates:

- `ParsekTimeFormat` reads the player's own `GameSettings.KERBIN_TIME` and gives 21600 or
  86400 seconds a day (`Source/Parsek/ParsekTimeFormat.cs:19-22`); it is placed in `UI`
  (`scripts/arch/modules.toml:419-421`). Every window formats a date with
  `KSPUtil.PrintDateCompact(ut, true)`, which follows the same per-player setting (for
  example `Source/Parsek/UI/StructureListWindowUI.cs:675-678`). Neither can serve a race,
  whose calendar is the manifest's (6.6), and `RaceCore` may not name `UI`.
- `ParsekTimeFormat.FormatDuration` is a game-time formatter (`:43-70`); real-time "left
  until the end" needs its own 24-hour formatter.

Windows:

- `StructureListWindowUI` is one concrete window with four fixed columns
  (`Source/Parsek/UI/StructureListWindowUI.cs:122-125`). Its chrome is inline: first-open
  rect (`:517-518`), resize (`:520-521`), opaque style and GUI colour reset (`:523-542`),
  `ParsekUiScale.Window` keyed by a window-id key hash (`:529-537`), position logging
  (`:543`), the camera input lock (`:545-564`), the tooltip strip and full-width Close
  (`:592-596`), resize handle and drag (`:598-599`). This is what A6 extracts.
- `ParsekWindowSet` builds every sub-window once (`Source/Parsek/UI/ParsekWindowSet.cs:28-40`)
  and hands typed getters out (`:70-81`); `ParsekUI` reaches each window only through the
  interfaces of `Source/Parsek/UI/ParsekUIWindowContracts.cs` (`IParsekUIWindows`,
  `:90-101`). Both `ParsekUI` constructors copy every window field
  (`Source/Parsek/ParsekUI.cs:127-147`, `:149-169`; P11 merges them).
- The main window's launchers are drawn in `ParsekUI.DrawWindow` (`ParsekUI.cs:672`), each
  gated by a `UiSurface` key (Kerbals at `:751-767`). The sub-windows are drawn by
  `ParsekFlight.DrawFlightWindows` (`Source/Parsek/ParsekFlight.cs:1456-1487`) and
  `ParsekKSC.DrawKscWindows` (`Source/Parsek/ParsekKSC.cs:266-292`), both inside a
  `ParsekUiScale.BeginHost` scope. `ParsekUI.Cleanup` releases every window's lock
  (`ParsekUI.cs:1849-1873`).
- Source gates: every window draws through `ParsekUiScale.Window`
  (`Source/Parsek.Tests/ParsekUiScaleTests.cs:258-268`); the number of draw calls in each
  host is pinned (`Source/Parsek.Tests/ParsekUIWindowSetCharacterizationTests.cs:243`, 8 in
  flight; `:252`, 7 at the Space Center); the hover strip text is budgeted per window
  (`Source/Parsek.Tests/TooltipEchoBudgetTests.cs:72`, `StripWindows`).
- Seam and harness: window tokens are constants in
  `Source/Parsek/TestCommands/TestCommandUiAction.cs:307-317` with a spec table (the Route
  History row at `:657`); hlib mirrors them (`harness/lib/hlib.py:3154-3165`), the mockable
  windows (`:3258`) and the tab vocabularies (`:3521-3526`). The gallery's supported
  windows are `GuiMockCatalogue.SupportedWindows`
  (`Source/Parsek/UI/Gallery/GuiMockCatalogue.cs:31-35`), its builders are registered in
  `EnsureBuilt` (`:136-138`). 100 `windows=11` literals sit in 25 specs (grep today; P11
  removes them).
- `docs/dev/design-gui-inventory.md` holds the window index (3.0, line 373) and the screen
  message census (3.14, line 1473: 88 producers).

Messages:

- `ParsekLog.ScreenMessage` (`Source/Parsek/ParsekLog.cs:605`) forwards to
  `TryScreenMessage` (`:615`), which tests capture through `ScreenMessageSinkForTesting`
  (`:82`). There is no count gate on producers; the census is the inventory doc.

Gates the rules must satisfy:

- Layers and headless modules: `scripts/arch/modules.toml:511-519` and `:531-536`;
  forbidden edges `:562-566` (two today; P12 and R1 add the race ones).
- The uncalled-method ratchet: `[uncalledInAssembly] bound = 491`
  (`scripts/arch/facts_pins.toml:74-75`). Tooling modules (`InGameTests`, `TestCommands`)
  are roots, so their calls make a method live (`docs/dev/arch/README.md:292-296`); a
  method only the unit tests call counts as uncalled (`:509-512`).
- Static lifetimes count reassignable statics and static readonly collections mutated
  outside a static constructor (`docs/dev/arch/README.md:532-536`). This plan adds none:
  the ladder is built in a static initializer and exposed read-only; the previous board for
  messages lives on the session instance.

## 4. Tasks

### Input shapes (agreed with R3E-01, R3E-04, R3E-18)

The rules read only these values. They are the codec's own `RaceCore` value types (one
definition, no parallel view types); member names are binding, type names follow the codec.
R4 adds nothing to the wire (OV-32): `ToolsFromUT` and `ToolIds` are a view the codec
derives from R3E-10's `TOOL { kind detail ut }` children (the view lands with R6-01, its
first caller), `TestToolingUsed` is R3E-15's key `testTooling`, and `DebugCompletions` reads
R3E-09's `DISCLOSURE` rows `DEBUG_COMPLETION`.

```
Standing                         (one parsed, complete standing; immutable)
  string   PlayerId
  int      Seq
  DateTime ExportedRealTimeUtc   (6.5)
  int      RaceFormat
  double   FrontierUT
  IReadOnlyList<StandingRecording> Recordings
  IReadOnlyList<StandingMilestone> Milestones
  IReadOnlyDictionary<string, StandingSeen> Seen        (key: rival player id)
  StandingDisclosure Disclosure
  IReadOnlyCollection<string> PartManifest               (runtime dot-form names)
  IReadOnlyList<StandingDebugCompletion> DebugCompletions
                            (R3E-09's save-level debug-completion fact; empty if none;
                             wire form: `DISCLOSURE` rows `DEBUG_COMPLETION { id ut
                             action }`)

StandingDebugCompletion     string MilestoneId; double UT   (the row's `action` is not read)

StandingRecording
  string RecordingId, TreeId
  double StartUT, EndUT
  double ToolsFromUT        (derived: NaN when the recording has no TOOL child; else the
                             least `ut` of its TOOL children, R3E-10; section 9 item 4)
  IReadOnlyList<string> ToolIds   (derived: the sorted `kind`s of its TOOL children, from
                                   the fixed list of 7.12; empty if none)

StandingMilestone
  string MilestoneId        (the stock qualified id: "Mun/Landing", "ReachedSpace")
  double UT                 (exact, written "R")
  string RecordingId        ("" when the exporter found no covering flight, case 12)
  int    Since              (seq of this player's standing where this exact date first
                             appeared)

StandingSeen     int Seq; string Hash
StandingDisclosure
  string ModDigest; IReadOnlyList<(string id, string version)> Mods
  string DifficultyDigest; bool TestToolingUsed   (key `testTooling`, R3E-15)

RacePlayerRecord                 (one per player, own included; built by R4-04 from the
                                  `RivalStandingVerdict` values (type R3E-04, filled by
                                  R3E-18) plus R1-08's manifests)
  string PlayerId, DisplayName; PlayerKind Kind (Human, Recorded, Ai); int Look;
  int SiteSlot; DateTime JoinedRealTimeUtc
  Standing Applied                       (own: the latest written)
  IReadOnlyList<Standing> Kept           (older complete standings kept by 6.3, by seq)
  Func<int, DateTime?> FirstSeenLocalUtc (witness; null for the own player)
  bool CheckedHere                       (false when Applied.RaceFormat is newer than
                                          RaceFormat.Current: the milestone block parsed,
                                          nothing else is promised)
  string FolderNote                      (the tamper mark of 7.15, display only; "" if none)

BoardRules                        (from RaceLink and the manifest)
  string LadderId; RaceCalendarKind Calendar (Kerbin, Earth); string DateUnit;
  bool NoRetakes; DateTime? EndRealTimeUtc; string ManifestModDigest;
  IReadOnlyList<(string, string)> ManifestMods; string ManifestDifficultyDigest

LocalFacts                        (from the shell, once per board build)
  string LocalPlayerId; DateTime NowRealUtc; double NowUT;
  IReadOnlyCollection<string> KnownBodies; IReadOnlyCollection<string> KnownParts
```

The board's output (`RaceBoardResult`): `Present` and, once an end date is set, `Final`;
each a `RaceBoard` with per ladder milestone a `BoardRow` (holders, holder day, share each
as `RacePoints`, one `BoardCell` per player holding every entry earliest first with a
`ClaimStatus` flag set and its proof, and the index of the date that counts), per player
`RacePoints` total, held count and marks (R6-01's debug-completion mark), and for `Final`
the winners and the cut-off seq per player. `RacePoints` is an exact fraction (two
`long`s, reduced), so shares are unrounded and "level on points" is exact (section 9,
item 13).

`ClaimStatus` flags: `Unverified`, `ToolMarked`, `SeenFirst`, `NotCheckedHere`,
`CannotWatchHere`, `AfterTheEnd`, `ArrivedLate`, `HistoryIncomplete`, `BadDate`. A date
scores when it has none of `Unverified`, `ToolMarked`, `SeenFirst`, `BadDate`.

### R4-01 The race calendar and the date rule

- **Change:** R1-10 creates `RaceCore/RaceCalendar.cs` first, under this task's name and
  reason (OV-30, A4): `TryAcceptDateRule(dateUnit, calendar, out reason)` refuses any
  `dateUnit` other than `DAY` with reason `date-unit-not-day` and does not check
  `calendar`; `RaceInstallCheck` calls it. This task's pull request extends the rule only:
  `RaceCalendarKind { Kerbin, Earth }` and a refusal of any calendar other than `KERBIN` /
  `EARTH` with reason `calendar-unknown`; the `date-unit-not-day` rule and its reason
  string stay as R1-10 wrote them (one rule, one reason). The day arithmetic is specified
  here and lands with its first production caller (A1, uncalled-method ratchet), each as
  an edit to this file: `SecondsPerDay(kind)` (21600, 86400), `DayIndex(ut, kind)` =
  floor(ut / day) (day 1 is index 0), `SameDay(a, b, kind)` and `IsValidDate(ut)` (finite,
  not negative) with R4-04's board; `DayNumber` = index + 1 with R4-07's message text;
  `TimeOfDay(ut, kind, out h, out m, out s)` with R4-10's date hover. No game type, no
  culture-sensitive formatting.
- **Proof:** `RaceCalendarTests`: the `calendar-unknown` refusal (R1-10's cell for
  `date-unit-not-day` stays); each arithmetic member's cells land with it: day boundaries
  (21599.999 and 21600 are different Kerbin days, the same Earth day), day 1 at UT 0, NaN
  and negative refused, `de-DE` culture does not move anything.
- **Registers:** caller: R1-10's `RaceInstallCheck` already calls `TryAcceptDateRule` (the
  11 section's "a manifest whose dateUnit is not DAY is refused"); the new refusal needs no
  new call. No new static.
- **Size:** S. **Depends on:** R1-01, R1-08, R1-10. **PC:** no. **Risk:** low; arithmetic
  only.

### R4-02 The ladder table standard-1

- **Change:** `RaceCore/Ladder.cs` (`LadderMilestone`: `Id` ("Mun/Landing"), `Body`,
  `Step`, `DisplayName` ("Mun Landing"), `Points` (int), `MemberStockIds`, `Order`;
  `Ladder.TryGet(ladderId, out Ladder)`, `Ladder.IsKnown`, `Ladder.MemberOf(stockId)`) and
  `RaceCore/LadderStandard1.cs` (the data, built once in a static initializer as a
  read-only list). Proposed numbers, tunable until the play test (13.C.1), every value in
  the one data file:

  | Step | Members (stock id after the body) | Base |
  |------|-----------------------------------|------|
  | Flyby | `Flyby` | 1 |
  | Orbit | `Orbit` | 2 |
  | Landing | `Landing`, `Splashdown` (whichever came first) | 3 |
  | Kerbal on the surface | `SurfaceEVA`, `FlagPlant` | 1 |
  | Return from orbit | `ReturnFromOrbit` | 2 |
  | Return from the surface | `ReturnFromSurface` | 4 |

  Bodies and multipliers: Mun, Minmus 1; Duna, Ike, Eve, Gilly 2; Moho, Dres, Laythe, Vall,
  Tylo, Bop, Pol 3; Eeloo 4; override Eve return from the surface multiplier 6. Jool:
  flyby, orbit, return from orbit only, multiplier 3 (section 9 item 3). Kerbin: two fixed
  rows, `ReachedSpace` 1 and `Kerbin/Orbit` 1. Left out: the Sun, `FirstLaunch`, the four
  records, and every other stock node. 89 rows, grouped by body in stock order.
- **Proof:** `LadderStandard1Tests`: ids unique; every member id is spelled as
  `QualifyMilestoneId` would produce (`Body/Node` or a bare top-level id) and is in a fixed
  list copied from the decompiled node ids of section 3; no member in two rows; points are
  positive integers; a golden table (id, points) that a retune changes on purpose;
  `TryGet` of an unknown id fails. The live check is R4-13.
- **Registers:** callers: R1-10's `RaceInstallCheck` refusal list refuses an unknown
  `ladderId` (`ladder-unknown`) through `Ladder.IsKnown`, added here; that call is this
  pull request's only production caller, so it lands the data and `IsKnown` only (A1).
  `TryGet` and `MemberOf` land in R4-04's pull request (R4-03's filter and the board are
  their first callers), and each `LadderMilestone` member lands with its first reader if
  `IsKnown` does not already read it (`Points`, `MemberStockIds`, `Order` with R4-04;
  `DisplayName` with R4-07; `Body`, `Step` with R4-10); `LadderStandard1Tests` cells move
  with them.
- **Size:** S. **Depends on:** R1-01, R1-08, R1-10. **PC:** no.
  **Risk:** low; a misspelt stock id would score nothing, caught by R4-13.

### R4-03 The ladder filter at board time

- **Change:** R3E-11 owns `RaceCore/MilestoneTable.cs` (OV-29): the 4-field
  `MilestoneReachRow`, covering resolution, `since` assignment, and every once-ever stock id
  exported in `MILESTONES` (so a ladder retune needs no new export); the lowest-recording-id
  rule for an exact duplicate (same id, same UT) is its `Build`'s tie-break. This task adds
  only the filter the board applies when it is built: `RaceCore/LadderFilter.cs`,
  `LadderFilter.Apply(standing.Milestones, ladder)` gives, per ladder row, the entries whose
  stock id `Ladder.MemberOf` maps to that row, earliest first (UT, then recording id);
  entries whose id is in no row are ignored by the board and stay in the standing.
- **Proof:** `LadderFilterTests` (row tables): `Mun/Landing` and `Mun/Splashdown` fall in one
  row; a stock id outside the ladder is ignored and the standing is not changed; a rewind
  and an earlier re-earn give two entries in the row, earliest first; the result is
  independent of the input order.
- **Registers:** caller: `RaceBoard.Compute` (R4-04); this task lands in R4-04's pull
  request so the filter has its production caller (uncalled-method ratchet).
- **Size:** S. **Depends on:** R4-02, R3E-11 (the block it filters; the tests use
  hand-written entries). **PC:** no. **Risk:** low; the VERIFY items of 7.8 (later row kept
  after an earlier re-earn; a Re-Fly that removes the first flight) belong to R3E-11's
  tests.

### R4-04 The board: shapes, exact points and the base rule

- **Change:** `RaceCore/RacePoints.cs` (exact fraction; `Add`, `DivideBy(int)`, `CompareTo`,
  `ToDisplay()` with two decimals, invariant culture); `RaceCore/RaceBoard.cs`
  (`RaceBoardResult`, `RaceBoard`, `BoardRow`, `BoardCell`, `BoardEntry`, `ClaimStatus`;
  `RaceBoard.Compute(BoardRules, players, LocalFacts)`); `RaceCore/RacePlayerRecords.cs`:
  `Build(verdicts, manifests, ownStanding, kept, firstSeen)` makes one `RacePlayerRecord`
  per player (OV-37) from the `RivalStandingVerdict` values (declared by R3E-04, filled by
  R3E-18) (`Applied` = the verdict's
  standing; `CheckedHere` false for status `NewerFormat`; `FolderNote` from its marks) plus
  R1-08's player manifests (name, kind, look, slot, joined time) and the own latest
  standing; `Kept` comes from R3E-21's kept standings and `FirstSeenLocalUtc` from R3E-19's
  witness log, empty and null until those land. The base rule: per ladder row and player,
  the entries R4-03's filter gives for `Applied.Milestones`, earliest first; a `BadDate`
  entry is kept and marked; the date that counts is the earliest scoring one; the holder day
  is the smallest day index among the players' dates that count; the holders are every
  player with that day; each gets `Points / holders`. A player's score is the sum of their
  shares. Players are processed in player id order, so the result does not depend on input
  order. Rules are a fixed pipeline: claim checks (R4-05, R6-01), install notes (R4-06),
  house rules (R6-02), the end-date stage (R6-03), each a function the pipeline calls in
  order.
- **Proof:** `RaceBoardBaseTests`: rows T1 and T6 of section 6; a milestone nobody reached
  has no holder; a player's two dates count once (the earliest); shuffled player and entry
  order give an equal board; the board for one folder is the same under either local
  calendar setting (the manifest's calendar is the only one read); `RacePointsTests`: three
  thirds add to exactly 1, display `0.33`, comparison is exact; `RacePlayerRecordsTests`:
  each verdict status (Applied, KeptPrevious, NewerFormat, Refused) maps to the record as
  stated above, a manifest with no verdict gives a player with no dates. Log:
  `[RaceBoard]` Info `Board computed: players=N rows=M held=K` once per build (Verbose when
  unchanged); T-28 RACE-6 and T-32 RACE-7 read this line in their `logContract` (OV-07).
- **Registers:** caller: `RaceSync.Run` (R3E-04), after R3E-18's import step, builds the
  records and puts `RaceBoard.Compute`'s result in `SyncResult`; the one call is added
  here as an edit to R3E-04's `RaceSync.cs`, with R4-03's filter and the calendar and
  ladder members R4-01 and R4-02 specify (A1). Members with a later first reader land with
  it: `RacePoints.ToDisplay` with R4-10; result members the board itself does not read
  with R4-07's diff or R4-10's row model. The codec's `ToolsFromUT` / `ToolIds` view is
  not derived here (R6-01 lands it). New log tag `RaceBoard`, added to R1-06's race tag list
  (OV-57) and `scripts/arch/log_tags.json` refresh, report-only.
- **Size:** M. **Depends on:** R4-01, R4-02, R4-03, R1-08, R3E-01, R3E-04, R3E-18.
  **PC:** no. **Risk:** low; the shapes are the contract with two other plans, so a
  mismatch shows as a compile error in the call.

### R4-05 The replay is the proof: claim checks and the cover check

- **Change:** `RaceCore/ClaimChecks.cs`: `IClaimCheck { ClaimStatus Check(entry, standing,
  ctx) }` and the ordered list (15.10). First check `CoveredByFlight`: find the recording
  the entry names in `standing.Recordings`; none, or an empty id: `Unverified` (reason
  `no-flight` / `flight-not-listed`); else any listed recording with the same `TreeId`
  whose `[StartUT - 1, EndUT + 1]` contains the date: verified; else `Unverified`
  (`not-covered`). Skipped (no mark) when the player's `CheckedHere` is false (R4-06).
- **Proof:** `ClaimChecksCoverTests`: inside the named recording; inside a sibling of the
  same tree; 0.9 s past the end (verified) and 1.1 s past (unverified); a recording of
  another tree covering the date does not verify; an empty id; a named id that is not
  listed (row T4). Board rows: an unverified earlier date holds nothing and the next
  scoring date holds.
- **Registers:** lands with its stage call in `RaceBoard.Compute` (an edit to R4-04's
  file in this pull request, A1).
- **Size:** S. **Depends on:** R4-04. **PC:** no. **Risk:** low.

### R4-06 Rows this install cannot fully read

- **Change:** in the pipeline: a player with `CheckedHere == false` has every entry marked
  `NotCheckedHere` and the cover check skipped (its rows count as stated, 7.8); an entry
  whose ladder body is not in `LocalFacts.KnownBodies` is marked `CannotWatchHere` and
  still scores. Wording in section 9 item 7.
- **Proof:** row T8; a missing body still scores and is marked; both marks together.
- **Registers:** its stage call in `RaceBoard.Compute` lands in this pull request.
- **Size:** S. **Depends on:** R4-05. **PC:** no. **Risk:** low.

### R4-07 Holder-change events, message text and posting

- **Change:** `RaceCore/RaceMessages.cs`: `Diff(previous, next, localPlayerId,
  displayNames, calendar)` returns ordered `RaceMessage { Kind, Text, MilestoneId }`.
  Rules: no message when `previous` is null (the board built from the cache at load is the
  baseline); a row is considered only when the local player is a holder before or after
  AND a rival is a holder before or after; kinds `Taken` ("Bogdan took Mun Landing: day 30
  beats your day 42"), `Regained` ("Mun Landing is yours again: day 42"), `Won` ("Mun
  Landing is yours: day 31 beats Bogdan's day 40"), `Shared` ("Bogdan matched your day 42
  on Mun Landing: you share its points"), `LostShare`; at most 2 messages, more becomes one
  `Summary` ("Space Race: 5 firsts changed hands - see the Space Race window"). Day text
  is `"day " + DayNumber` (invariant). `RaceSession` (R1-08) keeps the previous
  `RaceBoardResult` on its instance; `RaceSync.Run` (R3E-04), after R4-04's board call in
  each tick (an own export and an import share one tick), diffs against it, appends each
  message to `SyncResult.Messages` (OV-38) and logs `[RaceBoard] Holder changed:
  milestone=Mun/Landing from=<ids> to=<ids> day=30` (Info, one per change up to 20, then a
  summary line). It posts nothing itself: the driver shows `SyncResult.Messages` with
  `ParsekLog.ScreenMessage`, the one producer site R3E-22 lands and R3E-18 / R3E-20 also use.
- **Proof:** `RaceMessagesTests`, rows M1 to M9 of section 6, asserting the
  `SyncResult.Messages` content and the log sink for the Info lines (the posting is the
  driver's, tested by R3E's driver tests).
- **Registers:** no new screen message producer site (design-gui-inventory 3.14: a "Space
  Race" message class under the driver's one site); caller: `RaceSync.Run`'s board step,
  an edit to R3E-04's `RaceSync.cs` in this pull request (A1), with the previous-board
  field on R1-08's `RaceSession`.
  T-28 RACE-6 and T-32 RACE-7 carry the `Holder changed: milestone=Kerbin/Orbit` line and
  the message-posted line in their `logContract` (OV-07).
- **Size:** S. **Depends on:** R4-04, R1-08, R3E-04, R3E-22. **PC:** no (the in-game sight of it
  is T-32 RACE-7). **Risk:** low; the risk is noise, bounded by the rival-involved rule and
  the cap.

### R4-08 Shared table-window chrome (A6)

- **Change:** `UI/TableWindowChrome.cs`: an instance helper holding window id key, rect,
  first-open placement callback, minimum size, resize state, input lock id, last logged
  rect, the tooltip strip, and `Draw(mainRect, title, drawBody)` that does exactly what
  `StructureListWindowUI.DrawIfOpen` and its footer do today (section 3), plus
  `ReleaseInputLock`. `StructureListWindowUI` moves onto it with no behavior change (same
  window ids, lock ids, sizes, log lines, footer order).
- **Proof:** the existing `StructureListWindowTitleTests`, `TableRowInsetAlignmentTests`,
  `ParsekUiScaleTests`, `TooltipEchoBudgetTests`, `ParsekUIWindowSetCharacterizationTests`
  and the GUI census lanes GUI-1 / GUI-3 stay green; a new `TableWindowChromeTests` for the
  pure placement and lock decisions.
- **Registers:** none new (a refactor). The helper holds only the members
  `StructureListWindowUI` calls, so each has a production caller from this pull request
  (A1); anything the Space Race window needs beyond them lands with R4-09. Update the 3.7
  paragraph of design-gui-inventory if it names the inline methods.
- **Size:** S. **Depends on:** nothing (base Parsek). **PC:** the census lanes on the
  owner's PC prove the pictures did not move (optional; the unit gates cover the code).
  **Risk:** low; source gates that grep `StructureListWindowUI.cs` for the inline calls may
  need their needle moved, which the PR shows.

### R4-09 The Space Race window: skeleton and registration

- **Change:** `UI/SpaceRaceWindowUI.cs` on `TableWindowChrome`: title `Parsek - Space Race`,
  window id key `ParsekSpaceRace`, lock id `Parsek_SpaceRaceWindow`, first-open 760 x 420,
  minimum 480 x 200, Flight and Space Center. Body: the race name line and an empty table
  (milestone column, one column per player, points column) with the empty-state row "No
  milestone reached yet." Registration, every site in one change: `ISpaceRaceWindow` in
  `ParsekUIWindowContracts.cs` and in `IParsekUIWindows`; the field, constructor line and
  getter in `ParsekWindowSet`; `ParsekUI` draw method `DrawSpaceRaceWindowIfOpen`, its
  `Cleanup` lock release, and a `Space Race` launcher (hover "Who holds each first, and
  the points.", under 62 characters) drawn only when `RaceGate.IsRaceSave` and the
  `RaceRuntime` facade is present, in both complexity modes, with no `UiSurface` key
  (15.14); the draw call in `ParsekFlight.DrawFlightWindows` and `ParsekKSC.DrawKscWindows`;
  the seam token `spacerace` in `TestCommandUiAction` (spec row, Flight and Space Center),
  its handle in `ParsekTestCommandAddon.UiAction.cs`, a refusal `window-race-only` for
  `op=open` outside a race save; hlib `UIACTION_WINDOW_VALUES` and its reason table if one
  mirrors refusals. The window reads its data only through the facade and the row model;
  it names no store. This task adds `RaceRuntime.CurrentBoard` (the last `SyncResult`'s
  board, players and rules) on R1-08's facade, its first caller (OV-39). In R1-18's
  Multiplayer section of Settings it adds a `Sync now` button and the sync status text
  (the last `SyncResult.Status` of R3E-16: offline, exporting n, up to date), read through
  the facade beside `CurrentBoard` (OV-40). The button is `RequestSyncNow()`'s first
  production caller, so this task adds that member (and its `SyncNow` tick) to R3E-17's
  driver file as an edit in this pull request (A1); R3E-17 specifies it.
- **Proof:** `ParsekUIWindowSetCharacterizationTests` (window count, draw calls 9 / 8),
  `GuiCensusApplierSourceGateTests` row, `TestCommandUiActionTests` token cases,
  `GuiCensusSeamVerbTests` (hlib mirror), `TooltipEchoBudgetTests` (new strip row, two
  lines at 760 px, and the main window launcher), `ParsekUiScaleTests` (automatic),
  a launcher-gate test (absent in a single-player save, present in a race save) with the
  facade faked; `TooltipEchoBudgetTests` for the `Sync now` hover; a status-text test per
  `SyncResult.Status` value; a test that the button calls `RequestSyncNow` once. Log: `UI`
  Verbose `Space Race window toggled: open|closed` and `Sync now requested`.
- **Registers:** design-gui-inventory 3.0 (new row), a new section for the window, 3.1
  (the launcher), the Settings section (the button and the status line), 3.12 / 3.14
  untouched; check that R1-14's `.claude/CLAUDE.md` UI exception names the Space Race
  window (R1-14 owns the edit, OV-42); CHANGELOG entry "Space Race window" (inert outside
  race saves); the lane catalog only if a spec changes.
- **Size:** M. **Depends on:** R4-04, R4-08, P11-01, P11-02, R1-04, R1-08, R1-14, R1-18,
  R3E-17. **PC:** an in-game look (R4-13). **Risk:** medium; many registration sites, each
  with a gate that names the miss.

### R4-10 The row model and the drawn board

- **Change:** `RaceCore/RaceBoardPresentation.cs` (no text, numbers and codes only):
  `Build(RaceBoardResult, players, rules, LocalFacts, view)` gives a `SpaceRaceView`:
  player blocks (name, look index, points, held count, frontier day, last shared UTC, site
  slot, kind, is-local, show state from the facade), ladder rows grouped by body (row name,
  points, per player a `CellView`: day number of the date that counts, or of the earliest
  date when none counts, count of other dates, holder or co-holder, the status flags, the
  watch target `(playerId, recordingId, ut)`), and the chronological list (every entry of
  every player by UT, then player id). `UI/SpaceRaceText.cs` (pure, invariant): `day 31`,
  the hover sentences per flag (unverified, flown with a tool, seen first with its proof,
  not checked here, cannot be watched here, after the end, arrived late, history
  incomplete, co-holders and their share, every date of the cell), the date hover `day
  31, 03:12 (a race day is 6 hours)`, a real-time duration formatter (24-hour days). The
  window draws: holder cell in the look's color, co-holder cells in the same color dimmed,
  a non-scoring date in the house muted grey with a one-character mark (`?` unverified,
  `!` tool, `s` seen first, `~` not checked here) and `+N` for more dates, every meaning in
  the hover; the points column; a two-button view switch `By milestone` / `By date`.
  Colors come from R5-07's `RaceLookTable` (look index to UI color, OV-41), whose colors
  are placeholders until S3a. Each rival's player block carries a show / hide toggle; this
  task adds the SHOW setter on R1-08's facade, its first caller (OV-39): it writes the
  rival's entry of `RaceLocal`'s `SHOW` block (R1-08's codec key, OV-27) to `local.cfg`
  through the `RaceLocal` writer (R1-10); R3W-13's reader applies it at the next scene.
- **Proof:** `RaceBoardPresentationTests` (grouping, cell choice, chronological order, the
  watch target, the show state), `SpaceRaceTextTests` (every sentence under `de-DE`), the
  `TooltipEchoBudgetTests` direct assertions for the longest built sentences; a setter
  test over R1-08's in-memory `IRaceFolder` (hide then show round-trips `SHOW` through the
  codec). Log: `UI` Verbose `Rival shown: player=.. shown=..`.
- **Registers:** caller: `SpaceRaceWindowUI`. hlib `UIACTION_WINDOW_TABS` gains
  `spacerace: ("milestones", "dates")` with the C# tab handling, so a census can photograph
  both views.
- **Size:** M. **Depends on:** R4-09, R4-05, R4-06, R5-07, R1-10. **PC:** in-game look
  (R4-13). **Risk:** medium; the hover corpus is the window's main surface and must stay in
  budget.

### R4-11 Click to watch

- **Change:** `RaceCore/WatchCellDecision.cs` (pure): from the cell's target, the scene,
  whether the facade reports the ghost live, and `NowUT`: `Watch`, `FliesLater(dayNumber)`,
  `FlewBefore(dayNumber)` ("rewind to before day 30 to watch it"), `NotInThisScene` (the
  Space Center shows no rival ghosts, 15.6), `CannotWatchHere`. The window turns a cell into
  a click target; `Watch` calls `RaceRuntime.TryWatchRivalFlight(playerId, recordingId)`;
  every other outcome is the hover sentence. `TryWatchRivalFlight` on R1-08's facade is
  added by whichever of this task and R3W-26 lands first (A1): if this task is first it adds
  a stub that answers "not available" (OV-39), called by the window, so the hover is shown,
  and R3W-26 fills it; if R3W-26 is first, this task only calls it. The hover half ships
  now. The camera half waits with P3b, which is deferred out of R0b and built when watching
  a rival flight is scheduled (with R3W-26 and R5-06); the first race build has no Watch
  camera for rivals.
- **Proof:** `WatchCellDecisionTests` (truth table over scene x live x past / future); a
  stub test when this task adds the stub (the outcome reads "not available"); log
  `[RaceBoard]` Verbose `Watch requested: player=.. rec=.. outcome=..`.
- **Registers:** none new.
- **Size:** S. **Depends on:** R4-10; camera half: P3b-04, R3W-26. **PC:** yes, in flight,
  for the camera half when it is scheduled. **Risk:** medium for the camera half (the
  watch code is the most fragile on the path), low for the hover half.

### R4-12 Gallery mock states and the census

- **Change:** `UI/Gallery/GuiMockSpaceRaceStates.cs`: hand-written `Standing` and
  `RacePlayerRecord` values fed through the real `RaceBoard.Compute` and
  `RaceBoardPresentation.Build` (the gallery rule), states: empty race, opening day with a
  three-way tie, mid-race with every mark, final board with joint winners. A mock
  injection seam on the window (the `StructureListWindowUI` shape: capture, restore, open
  with a supplied view). `GuiMockSession.SpaceRaceWindow` token, `SupportedWindows`, hlib
  `UIACTION_MOCKABLE_WINDOWS`. A mock is allowed outside a race save (it is synthetic);
  `op=open` is not (R4-09).
- **Proof:** the gallery's unit cells (unique ids, every state builds), the
  `grep-audit-gui-mock-writeset` gate (nothing names a store or a file API), one operator
  census lane step that photographs the four states (GUI-1 or a new census spec, operator
  tier).
- **Registers:** design-gui-inventory "Pictures" line for the window; `autotest-status.md`
  if a lane is added.
- **Size:** S. **Depends on:** R4-10. **PC:** the census photographs. **Risk:** low.

### R4-13 In-game checks: the ladder against stock, the window drawn

- **Change:** `InGameTests/SpaceRaceTests.cs`, one category per scene (OV-09): category
  `SpaceRace` (SPACECENTER): every member id of every ladder row exists in the live
  `ProgressTracking` tree under the qualification `QualifyMilestoneId` gives, and the
  window opens, draws a mocked view and closes with its lock released; category
  `SpaceRaceFlight` (FLIGHT): the same open, draw and close.
- **Proof:** the test results file on the owner's PC; once T-29 lands, its host lanes run
  them in a race save: `SpaceRace` on RACE-9 (SPACECENTER), `SpaceRaceFlight` on RACE-10
  (FLIGHT).
- **Registers:** two new categories: `python harness/tools/lane_catalog.py` regenerated
  `docs/dev/flight-profiles.md` in the same commit; T-29's host table names both (a host
  slot each); no committed spec pins them before T-29.
- **Size:** S. **Depends on:** R4-02, R4-12. **PC:** yes. **Risk:** low.

### R4-14 Harness: the board in the two-player lanes

Dropped: replaced by T-28 and T-32 (OV-07).

### R6-01 Tool marks

- **Change:** second claim check `ToolMarked` in `ClaimChecks.cs`: for an entry, take the
  `TreeId` of the recording it names; the entry is `ToolMarked` when any listed recording of
  that tree has `ToolsFromUT <= entry.UT` (so a tool in an earlier recording of the same
  flight marks it, a tool used later does not reach back); the proof carries the recording
  id and tool ids. Applied also when `CheckedHere` is false if the recordings block was
  read. `ToolsFromUT` and `ToolIds` are the codec's derived view of R3E-10's `TOOL` children
  (OV-32). Debug completions (OV-34, owner decision D3: a debug tool means no score): before
  the claim checks, every entry of `Applied.Milestones` (and of the standings R6-02 / R6-03
  read) whose id and UT match an entry of the standing's `Standing.DebugCompletions`
  (R3E-09's save-level debug-completion fact, wire form `DISCLOSURE` rows
  `DEBUG_COMPLETION`, persisted through R3E-15's race facts child) is dropped from the
  board, so it holds nothing and blocks nothing, and the player is marked (a per-player
  `DebugCompleted` mark with the count, shown with the disclosure marks, R6-05). A
  completion S9 shows a mod cannot observe is out of reach (design 7.12).
- **Proof:** `ClaimChecksToolTests`: row T3 and its variants (earlier recording, later
  recording, later in the same recording); a marked date holds nothing and does not keep
  points from the next player; with a later clean date that one counts.
  `DebugCompletionTests`: a debug-completed date is not on the board and the next player
  holds; the player carries the mark with the count; a debug completion on another
  milestone or another UT drops nothing. Log: `[RaceBoard]` Verbose `Debug completions
  dropped: player=.. count=..`.
- **Registers:** its stage call in `RaceBoard.Compute` lands in this pull request. The
  standing codec's derived `ToolsFromUT` / `ToolIds` view (specified by R3E-10) lands here
  too, as an edit to R3E's codec file, since this check is its first production caller
  (A1); its codec cells (two uses, none) move with it.
- **Size:** S. **Depends on:** R4-05, R3E-07, R3E-09, R3E-10. **PC:** no. **Spikes:** S9.
  **Risk:** low.

### R6-02 No retakes

- **Change:** `RaceCore/HouseRules.cs`: `NoRetakes.Apply(board, players)` when
  `BoardRules.NoRetakes`. For player P's entry e of ladder row m: find P's standing with
  seq `e.Since` (Applied or Kept); missing: mark `HistoryIncomplete`, not blocked. Else for
  every rival R of kind `Human` in R's standing in effect (Applied, or the pre-end one on
  the final board): every entry f of R for m that passes the claim checks (blockers are
  judged by 7.8's scoring, not by their own block status, section 10 Q1) blocks e when
  `f.Since <= seen(P@e.Since, R).Seq`. A blocked entry is `SeenFirst` with the proof
  (P's seq, R's seq, R's day). Entries of a `Recorded` or `Ai` player are never blocked and
  never block (7.11, 15.10). The holder is then recomputed from the dates that still score.
- **Proof:** `NoRetakesTests`, rows T2, T5, T7, T10, T11, T12, T13 of section 6; a rival
  standing with no `Seen` entry for P; a pure cycle in forged receipts cannot loop (the
  rule is not recursive).
- **Registers:** called by the pipeline when the flag is on (the stage call lands in this
  pull request); log `[RaceBoard]` Verbose
  `No retakes: blocked=N incompleteHistory=M`.
- **Size:** M. **Depends on:** R6-01, R3E-21 (the kept standings). **PC:** no.
  **Risk:** medium; it decides points from receipts, so its truth table is the review
  focus.

### R6-03 The end date and the final board

- **Change:** `RaceCore/RaceFinal.cs`: when `EndRealTimeUtc` is set, per player the
  standing with the highest seq whose `ExportedRealTimeUtc < End` among Applied and Kept;
  a player with none is left out of the final board. The final board runs the same
  pipeline over those standings (blockers from the pre-end standings). Winners: the
  players with the highest exact total, every one of them when level ("joint winners"); no
  winner when nobody holds anything. `IsFinal` when `NowRealUtc >= End`. The present board
  marks an entry `AfterTheEnd` when it first appears in a standing stamped at or after the
  end (its `Since` standing's stamp).
- **Proof:** `RaceFinalTests`: row T9 (before and after the late standing arrives); a
  player who joined after the end; joint winners from exact thirds; stamps equal to the end
  are after it; no end date gives no final board.
- **Registers:** called by `RaceBoard.Compute` (the stage call lands in this pull
  request); log `[RaceBoard]` Info `Final board:
  winners=.. cutoffs=p_a:7,p_b:9`.
- **Size:** M. **Depends on:** R6-02, R3E-21 (the kept pre-end standing, 6.3).
  **PC:** no. **Risk:** medium; correctness rests on the importers keeping the pre-end
  standing, which R3E-21's keep rule tests.

### R6-04 Arrived late

- **Change:** in `RaceFinal`: an entry of a rival's pre-end standing is `ArrivedLate` when
  `FirstSeenLocalUtc(seq) > End + RaceTuning.ArrivedLateGrace` (1 hour, section 9 item 14).
  The own player never. Still counts.
- **Proof:** `RaceFinalTests` late cases: inside the grace, past it, unknown first-seen
  time (not marked).
- **Registers:** `RaceCore/RaceTuning.cs` (constants, no statics that mutate); the check
  runs inside `RaceFinal`'s stage, so it has R6-03's caller.
- **Size:** S. **Depends on:** R6-03, R3E-19 (the witness first-seen times). **PC:** no.
  **Risk:** low.

### R6-05 Disclosure facts

- **Change:** `RaceCore/RaceDisclosure.cs`: per player `ModsDiffer` (digest differs from
  the manifest's) with the added and missing mod ids and versions, `DifficultyDiffers`,
  `TestToolingUsed` (R3E-15's key `testTooling`, OV-32), the count of tool-marked dates,
  and R6-01's debug-completion mark with its count (OV-34). Exposed to the row model's
  player block.
- **Proof:** `RaceDisclosureTests`: equal digests, differing lists, a version change only,
  test tooling.
- **Registers:** called by `RaceBoardPresentation.Build` (the call lands in this pull
  request).
- **Size:** S. **Depends on:** R4-10, R6-01, R3E-15 (the `DISCLOSURE` block). **PC:** no.
  **Risk:** low.

### R6-06 Missing-parts, newer-version and cannot-be-watched notes

- **Change:** `RaceCore/RaceNotes.cs`: per player `MissingParts` = `PartManifest` minus
  `LocalFacts.KnownParts` (sorted, capped at 10 in the hover with a count),
  `NewerParsek` when `CheckedHere` is false or a recording format is newer (R3E-18's
  verdict status `NewerFormat` reports the latter), bodies not known here. This task adds
  `KnownParts` and `KnownBodies` on R1-08's `RaceRuntime` facade, their first caller
  (OV-39): `RaceWorld` fills them from `PartLoader` and `FlightGlobals.Bodies` once per
  scene, in a `[MethodImpl(NoInlining)]` core, and the board build reads them into
  `LocalFacts`.
- **Proof:** `RaceNotesTests`; the window header shows "Carol uses parts you do not have"
  and "Ana runs a newer Parsek" (text tests in `SpaceRaceTextTests`).
- **Registers:** `RaceNotes` is called by `RaceBoardPresentation.Build` and the facade
  members by the board build; both calls land in this pull request (A1).
- **Size:** S. **Depends on:** R4-10, R1-08, R3E-18. **PC:** no. **Risk:** low.

### R6-07 The window at the end and its header

- **Change:** `SpaceRaceWindowUI` and the row model: header "12d 4h left" (real time) or
  "Final" or nothing; with a final board, a winner line ("Ana wins with 14.00 points",
  "Joint winners: Ana and Carol, 9.50 points each") and a two-button switch `Final board`
  / `Now`; disclosure marks and notes after each player's name with their hover; the
  folder note of 7.15 when set.
- **Proof:** `RaceBoardPresentationTests` and `SpaceRaceTextTests` additions; a gallery
  state for each (R4-12 builder).
- **Registers:** design-gui-inventory window section; tooltip budget assertions.
- **Size:** M. **Depends on:** R6-03, R6-05, R6-06. **PC:** in-game look. **Risk:** low.

### R6-08 End-of-race messages

- **Change:** `RaceMessages`: when `Final` first becomes final in a session, one message
  "The race is over: Ana wins" (or joint winners); when a later sync changes the final
  board's winners (a late standing), one message "A late standing changed the final
  board: Bogdan now wins". After the end, holder changes on the present board send nothing.
- **Proof:** `RaceMessagesTests` end rows.
- **Registers:** appended to `SyncResult.Messages` like R4-07's; the driver's (R3E-22)
  one producer site posts them (no new site, OV-38).
- **Size:** S. **Depends on:** R6-03, R4-07. **PC:** no. **Risk:** low.

### R6-09 The no-retakes preview for the Join dialog

Dropped: the design asks only that the Join dialog says so before the player commits (7.2,
7.11), and R1-17's Join page already shows that sentence when the race's `noRetakes` is set.
A list of the rows still open would need a board in the main menu, where there is no session
and no import. If the owner wants the list, it is a later task that reads the shared folder
from the Join button.

### R6-10 Poll tuning

- **Change:** `RaceCore/RacePollPolicy.cs` (pure): next poll delay from the last poll's
  outcome: 60 s steady; 5 s while the copy step reports files still arriving (a first large
  sync, 7.2); doubling from 60 s to 10 min while the folder is unreachable; never in
  flight. R3E-17's 60 s poll reads it instead of its constant. Numbers from S8a, S8b.
- **Proof:** `RacePollPolicyTests`; a measurement on the owner's PC (folder on each common
  sync client) logged as `[RaceSync] Poll: delay=.. reason=..` (the `RaceSync` tag,
  OV-57).
- **Registers:** the call from R3E-17's poll lands in this pull request, as an edit to
  R3E-17's driver file (A1).
- **Size:** S. **Depends on:** R3E-17, S8a, S8b. **PC:** yes, for the numbers.
  **Risk:** low.

### R6-11 No retakes and the end date on generator-built rival folders

- **Change:** not a flown lane (OV-08, supervisor decision): a harness-flown race save is
  tool-marked for good, so its own dates never score and a flown rewind cannot show a
  scoring `SeenFirst`. No-retakes and the end date are proven headless, on rival folders
  built by T-16's `RaceFolderBuilder`, in which no seam ran, so they carry no mark. The
  two folders are T-17's, written by its env-gated fixture writer and kept current by its
  currency test: `noretakes-seen-first` and `enddate-between`. This task adds no folder.
  `Source/Parsek.Tests/Race/RaceRulesFolderTests.cs`: each folder is read with the production codec
  (R3E-01), its standings become verdicts and R4-04's
  player records, and `RaceBoard.Compute` runs. Case 1 (`noretakes-seen-first`,
  `noRetakes = true`): A reaches `Kerbin/Orbit` (standing A3); B's standing B4 lists
  `Seen` A3 and an earlier date for `Kerbin/Orbit` since B4; B's date is `SeenFirst`, A
  holds, and the log shows `No retakes: blocked=1`. Case 2 (`enddate-between`, an end date
  between B's standings): the final board takes the pre-end standings, its `Final board:`
  line names the cutoffs, and a date first listed after the end is `AfterTheEnd` on the
  present board only.
- **Proof:** the tests (xUnit, headless).
- **Registers:** `harness/fixtures/race/README.md` (two rows, T-17's list); no lane, no
  catalog row.
- **Size:** S. **Depends on:** R6-02, R6-03, T-16, T-17. **PC:** no.
  **Risk:** low; the rules' truth tables are R6-02's and R6-03's, this task proves them on
  folders the production reader accepts.

## 5. Parallel tracks

- **Track A, rules (RaceCore, headless):** R4-01 and R4-02 first (after R1-10; R3E-11
  depends on no R4 task, so nothing here lands inside R3); then R4-03 with R4-04 (one pull
  request, after R3E-11 and R3E-18); then R4-05 and R4-07 in parallel; then R4-06, R6-01,
  R6-05, R6-06, R6-10 in parallel; then R6-02, R6-03, R6-04, R6-08 in that order.
- **Track B, window (UI):** R4-08 can start at once (base Parsek only). R4-09 needs R4-04,
  P11-01, P11-02, R1-14, R1-18 and R3E-17; then R4-10, then R4-11 (hover half), R4-12,
  R6-07 in parallel; R4-13 last. The camera half of R4-11 waits with P3b and R3W-26.
- **Track C, folder proof:** R6-11 after R6-02, R6-03 and T-16 / T-17 (headless, no lane).
  The board's log lines ride T-28 RACE-6 and T-32 RACE-7 (OV-07).

Hot files (one task at a time):

- `RaceCore/RaceBoard.cs` (the pipeline): every rule task adds one stage call. Conflicts
  are one-line; land in the order of track A.
- `RaceCore/ClaimChecks.cs`: R4-05, R6-01.
- `ParsekUI.cs`, `UI/ParsekWindowSet.cs`, `UI/ParsekUIWindowContracts.cs`,
  `ParsekFlight.cs`, `ParsekKSC.cs`, `TestCommands/TestCommandUiAction.cs`,
  `harness/lib/hlib.py`, `docs/dev/design-gui-inventory.md`: R4-09 (and anything else that
  adds a window or a launcher in the same period).
- `UI/SettingsWindowUI.cs`: R1-18's Multiplayer section first, then R4-09's `Sync now`
  button and status text.
- `RaceWorld/RaceRuntime.cs` (R1-08's facade): R4-09 (`CurrentBoard`), R4-10 (SHOW
  setter), R4-11 or R3W-26, whichever lands first (`TryWatchRivalFlight`), R6-06
  (`KnownParts` / `KnownBodies`), beside R3W-08's scene calls.
- Other plans' files this plan edits (A1: a member lands with its first production
  caller): `RaceCalendar.cs` (R1-10; R4-01, then R4-04, R4-07, R4-10 for the arithmetic),
  `RaceInstallCheck` (R1-10; R4-02), `RaceSync.cs` (R3E-04; R4-04, R4-07), the standing
  codec file (R3E; R6-01 lands the derived `ToolsFromUT` / `ToolIds` view), R3E-17's
  driver file (R4-09 lands `RequestSyncNow`, R6-10 the poll call), `RaceSession` (R1-08;
  R4-07), T-17's fixtures (R6-11 reads them).

## 6. Tests and proof

Fixture convention for the rows below: Kerbin calendar; "day d" means UT = (d - 1) * 21600
+ 3600 unless a row says otherwise; players A (Ana), B (Bogdan), C (Carol), D (Dan), all
`Human` unless stated; `Ax` is A's standing with seq x; `Seen` lists a standing's
receipts; every entry is covered by a clean recording of its tree unless stated. Points:
`ReachedSpace` 1, `Kerbin/Orbit` 1, `Mun/Orbit` 2, `Mun/Landing` 3, `Minmus/Flyby` 1.

| Row | Case | Input | Expected (no retakes off) | Expected (no retakes on) |
|-----|------|-------|---------------------------|--------------------------|
| T1 | Same milestone, same game day | `Kerbin/Orbit`: A UT 43300, B UT 63000 (both day 3) | A and B hold, 0.50 each | same |
| T1b | Day boundary | A UT 21599.9 (day 1), B UT 21600 (day 2) | A holds alone | same |
| T1c | Earth calendar | A UT 50000, B UT 80000 (Earth day 1; Kerbin days 3 and 4) | Earth: A and B share; Kerbin: A alone | same |
| T2 | Rewind and an earlier re-earn | `Mun/Landing`: A [day 31 since A6, day 55 since A3], A6 Seen C2, A3 Seen none; C [day 40 since C2] | A holds with day 31, 3 points | A's day 31 "seen first" (C2 <= 2); A's 55 counts but is later; C holds with 40 |
| T2b | Improving never hurts | as T2 but A's older date is day 38 since A3 (blind) | A holds with 31 | A holds with 38 |
| T3 | Tool-marked earliest, no later clean one | `Mun/Orbit`: B [day 12, tree recording with tools from day 11]; A [day 20] | A holds; B's 12 marked, scores 0 | same |
| T3b | Tool-marked earliest and a later clean one | B [day 12 tool, day 25 clean]; A [day 20] / A [day 30] | A holds / B holds with 25 | same |
| T3c | Where the tool was | B's day 12 in recording r2; tool in r1 of the same tree starting day 10 / tool in r3 starting day 13 / tool in r2 itself from day 14 | marked / not marked / not marked | same |
| T4 | Unverified date | `Minmus/Flyby`: C [day 8, recording ""]; A [day 9] | A holds; C's 8 "unverified" | same; A had seen C's 8: not blocked |
| T4b | Cover tolerance | C's date 0.9 s after its recording's end / 1.1 s after | verified / unverified | same |
| T5 | A rival who lists two dates | `Mun/Landing`: B [day 20 since B5, day 35 since B2], B5 Seen A3; A [day 25 since A3] | B holds with 20 | B's 20 seen first; B's 35 is later than A's 25; A holds |
| T6 | Three-way tie on a 1-point milestone | `ReachedSpace`: A, B, C all day 1, nothing else | each 1/3 (shown 0.33); totals equal exactly; joint winners at the end | same |
| T7 | Late joiner | D joined last; D2 Seen A9, B7, C5; D [`Mun/Landing` day 20 since D2, `Duna/Orbit` day 300 since D2]; C [`Mun/Landing` day 40 since C2]; later A [`Duna/Orbit` day 280 since A10], A10 Seen D2 | D holds both; then A takes `Duna/Orbit` | D's 20 seen first, C holds `Mun/Landing`; D holds `Duna/Orbit`; A's 280 seen first, D keeps it |
| T8 | Standing from a newer build | A Applied raceFormat 2 (local 1), `CheckedHere` false; A [`Mun/Landing` day 31, named recording not listed]; C [day 40] | A holds, cell "not checked here"; header "Ana runs a newer Parsek" | same; A's entries never blocked when its receipts did not parse |
| T8b | Same with a readable standing | as T8 but `CheckedHere` true | A's 31 unverified; C holds | same |
| T9 | End date, late-arriving standing | End E. B8 stamped E-2d (no `Mun/Landing`), B9 stamped E-10min [day 28], B10 stamped E+1d [day 20]; A7 stamped E-1d [day 31]. Before B9 arrives: B Applied B10, Kept B8. After: B9 first seen E+1d | before: final A holds; present B holds with 20 "after the end". After: final B holds with 28, "arrived late"; B10 never on the final board | same, blockers taken from B9 and A7 |
| T10 | Only a scoring date blocks | C [`Minmus/Flyby` day 8, tool-marked] seen by A before A's day 9 | A holds | A holds (not blocked) |
| T11 | A block lasts while the claim stands | T2 with no retakes, then C's next standing drops day 40 | - | A's 31 counts again; A holds |
| T12 | Recorded opponent | R (`Recorded`) [`Mun/Landing` day 10]; A saw it, A [day 9] | A holds | A holds; R never blocked and blocks nobody |
| T13 | Missing since standing | A's entry since A4; A4 neither Applied nor Kept | normal | not blocked, cell "history incomplete" |
| T14 | Same files, same board | T2 to T9 with player order, entry order and local calendar setting permuted | identical boards | identical boards |

Messages (local player A):

| Row | Before | After | Message |
|-----|--------|-------|---------|
| M1 | A holds `Mun/Landing` day 42 | B holds day 30 | "Bogdan took Mun Landing: day 30 beats your day 42" |
| M2 | B holds day 30, A has day 42 | B's date left; A holds 42 | "Mun Landing is yours again: day 42" |
| M3 | A holds day 42 | A and B hold day 42 | "Bogdan matched your day 42 on Mun Landing: you share its points" |
| M4 | B holds day 40 | A holds day 31 (own export) | "Mun Landing is yours: day 31 beats Bogdan's day 40" |
| M5 | B holds | C holds, A never involved | none |
| M6 | no previous board (load) | any | none |
| M7 | 5 rows change for A | | one summary |
| M8 | nobody holds | A holds, no rival has a date | none |
| M9 | B holds day 30, A has 42 | B holds day 29 | none (A did not hold before or after) |

Other proof:

- Unit (xUnit, headless, `Source/Parsek.Tests/Race/`): the classes named per task, a small
  test-only `BoardFixture` builder for `Standing` / `RacePlayerRecord`, every text test
  under `de-DE`; R6-11's no-retakes and end-date cases on T-16 / T-17 folders. All rule
  classes are `[Collection("Sequential")]` only where they capture `ParsekLog`.
- Source gates: the registration gates of R4-09, the tooltip budget, the UI-scale gates,
  the gallery write-set gate.
- In-game (owner's PC): R4-13 (ladder ids live, window draws in both scenes; categories
  `SpaceRace` and `SpaceRaceFlight`, hosted by T-29's RACE-9 and RACE-10), R4-11 camera
  half in flight once P3b is scheduled, R6-07 look.
- Harness (owner's PC): the census photographs of R4-12, the poll measurement of R6-10.
  The board's log lines (`Board computed`, `Holder changed`, the message-posted line) are
  asserted by T-28 RACE-6 and T-32 RACE-7 (OV-07); no lane is planned here.

## 7. Gates and registrations

- **Architecture map:** no new module, no root file (all files go under `RaceCore/`, `UI/`,
  `UI/Gallery/`, `InGameTests/`, plus members on R1-08's `RaceWorld/RaceRuntime.cs`).
  `RaceCore` stays headless: no game type in any file of this plan except the shell halves
  that live in `RaceWorld` (the facade members of R4-09, R4-10, R4-11; R6-06 part and body
  lists). Message posting is R3E-22's driver site, not this plan's. No `RaceCore` file names
  `UI` or a `*Store` (15.4 grep gate). The UI references only `RaceCore` and the
  `RaceRuntime` facade (15.4).
- **Uncalled-method ratchet (A1):** no task adds a production member without a production
  caller in the same pull request; a member a later task first calls lands with that task,
  as an edit to the earlier file (R4-01's arithmetic, R4-02's lookups, the codec's derived
  tool view with R6-01, `RequestSyncNow` with R4-09). Every task names its production
  caller in "Registers"; each rule stage lands with its call in `RaceBoard.Compute`; the
  pipeline is called by `RaceSync.Run` from R4-04 on (R4-03's filter lands in R4-04's pull
  request); the row model by the window; the gallery adds a second caller; R4-08's chrome
  holds only what `StructureListWindowUI` calls; each facade member lands with its first
  caller (R4-09, R4-10, R4-11 or R3W-26, R6-06; OV-39). No task lands a rule without its
  call.
- **Statics:** none new and mutable; the ladder is a static initializer's read-only list;
  the previous board lives on the `RaceSession` instance; no `*ForTesting` hooks (rules take
  their inputs as arguments).
- **Complexity gate:** keep each rule function under 45; the pipeline is a list of stage
  calls, not one method.
- **Log tags:** `RaceBoard` (new, in R1-06's race tag list, OV-57); the poll logs under
  R3E's `RaceSync`; refresh `scripts/arch/log_tags.json`.
- **Save format:** nothing here is persisted except the SHOW setter's write to
  `local.cfg`, a key R1-08's codec already carries (OV-27). R4 adds nothing to the wire
  (OV-32): `ToolsFromUT` and `ToolIds` are the codec's derived view of R3E-10's `TOOL`
  children, `TestToolingUsed` is R3E-15's `testTooling`, the debug-completion fact is
  R3E-09's; their golden coverage is R3E-04's `v3` folder (OV-15).
- **GUI gates:** R4-09 list (window count, draw calls, census applier row, seam token,
  hlib mirrors, tooltip budget, UI-scale, the `Sync now` hover); R4-10 tab vocabulary;
  R4-12 gallery tables; design-gui-inventory 3.0, 3.1, the window section, the Settings
  section, 3.14 (a message class under the driver's one site, no new producer). No
  `UiSurface` key.
- **Docs:** CHANGELOG one entry (R4-09) edited by later window tasks; todo entries for the
  items of section 9 the owner leaves open; R4-09 checks that R1-14's `.claude/CLAUDE.md` UI
  exception names the window (OV-42); the lane catalog with R4-13's two categories, and
  T-29's host table naming them (OV-09); `autotest-status.md` only if R4-12 adds a census
  lane.

## 8. Risks

- **The input contract drifts.** Three plans meet in the `Standing` shape. Noticed at
  compile time (one type) and by R3E-04's codec round-trip feeding R4-04's fixtures; the
  plan's shape block is the review checklist.
- **A1 does not keep the later row after an earlier re-earn.** Then T2 is unreachable in
  play and under no retakes a player can lose a date they should keep. Noticed by R3E-11's
  own tests (7.8 VERIFY); this plan's rules are correct either way.
- **The exporter loses `since` continuity** (local race directory rebuilt, previous own
  standing unreadable): every date gets a new `since` and, under no retakes, may become
  "seen first". Noticed by a test in R3E-11: its `Build` must be fed the latest own
  standing from the mirror or, failing that, the shared folder copy.
- **Noise.** Too many messages early in a race. Bounded by the rival-involved rule and the
  cap of two; T-28 RACE-6 and T-32 RACE-7 read the log.
- **Float creep.** Avoided by exact fractions; `RacePointsTests` pin it.
- **The hover corpus outgrows the strip.** The tooltip budget gate reds the PR.
- **Stock id spelling** (`ReachedSpace`, `ReturnFromFlyBy`): R4-13 checks against the live
  tree.
- **Tool marks over-mark** if a `TOOL` child's `ut` is not the tool's real first
  use: visible as marked dates whose tool came later. Settled in shape by OV-32 (the
  codec derives `ToolsFromUT` from R3E-10's `ut` values); R3E-07's capture tests own the
  value.

## 9. Corrections and gaps

1. **Stock defines landings on every body.** 7.9 leaves out "anything stock does not define
   (a landing on Jool or the Sun)". `CelestialBodySubtree::.ctor` builds `Landing`,
   `Splashdown` and the return nodes for every body, Jool and the Sun included. The ladder
   leaves them out by choice; the plan says so in `LadderStandard1.cs`.
2. **Stock id spellings.** The "reach space" node id is `ReachedSpace` (unqualified) and
   the return ids are `ReturnFromFlyBy`, `ReturnFromOrbit`, `ReturnFromSurface`. The
   design's prose names would not match.
3. **The ladder is underspecified.** 7.9 gives no multiplier for Jool itself or the Sun,
   and "surface EVA or flag 1 more" can be read as one step or two. The plan assumes Jool 3
   (flyby, orbit, return from orbit), the Sun out, and one step "kerbal on the surface"
   (`SurfaceEVA` or `FlagPlant`, first wins) worth 1. All numbers stay tunable (13.C.1).
4. **Tool marks need a time inside the recording.** 7.12 marks a date "when a recording of
   its tree that begins at or before that date carries a tool" and also says "a tool used
   later in the same flight does not reach back". With only a per-recording flag, a tool
   used after the date inside the same recording marks it. The plan asks A3 and the
   standing's `REC` entry for `toolsFromUT` (the first UT a listed tool was on or used) and
   marks when any recording of the tree has `toolsFromUT <= date`. Settled (OV-32, E.1.11):
   no new key; R3E-10's `TOOL { kind detail ut }` children are the wire form and the codec
   derives `ToolsFromUT` (least `ut`) and `ToolIds` (the kinds).
5. **Test tooling is per save.** 7.12 says a race save in which Parsek's test tooling ran
   "says so for good". The design's `DISCLOSURE` block has no field for it; the plan adds
   `testTooling` there (a player mark). Per-flight scoring follows only from A3 marking the
   recordings flown with it. Settled (OV-32, DC-008): R3E-15 owns the key `testTooling`
   (persisted through R1-03's child hooks), read here as `TestToolingUsed`.
6. **What the `MILESTONES` id is.** 6.1 and 7.9 call it the ladder id `Body/Step`. With
   ladder rows that unite two stock nodes (landing or splashdown), the plan stores the stock
   qualified id per entry and maps to ladder rows in the board, so a ladder retune needs no
   new export and `since` stays per exact stock date. Settled (OV-29): R3E-11 owns
   `MilestoneTable.cs` and exports every once-ever stock id; R4-03 maps them to ladder rows
   when the board is built.
7. **Two marks called "not checked here".** 7.6 uses it for a body this install lacks (the
   date is still checked and scores) and 7.8 for a newer build (not checked, counts as
   stated). The plan words the first "cannot be watched here".
8. **What "a newer build" is.** 6.3 treats a newer `raceFormat` and newer recordings alike;
   7.8 says a newer build's rows "cannot be checked". The check reads only the standing, so
   the plan marks rows "not checked here" only for a newer `raceFormat`; newer recordings
   only stop the ghosts.
9. **Can a "seen first" date block?** 7.11 says only a scoring date blocks; whether a date
   that is itself blocked still blocks is open. The plan says yes (judged by 7.8's scoring
   alone), which is not recursive and cannot loop on forged receipts. Question Q1.
10. **A date whose status changes while it stands.** 7.11 says a date that becomes scoring
    later blocks "from the standing in which the scoring date first appeared". The kept
    standings (6.3) do not hold enough history to find that standing, so the plan uses the
    entry's `since`. It differs only when a flight is added later that covers an older
    unverified date, which the exporter does not produce.
11. **Messages are underspecified.** 7.8 does not say what the first board of a session
    compares against, nor whether an uncontested own first is announced. The plan: the
    board built from the rival cache at load is the baseline (no persistence), and only a
    change with a rival involved is announced (Q2). After the end only final-board changes
    are announced (Q3).
12. **A missing `since` standing.** If P's standing named by `since` is not available, the
    receipts cannot be read. The plan does not block and marks "history incomplete".
13. **Unrounded shares need exact arithmetic.** With doubles, three thirds do not add to 1
    and "level on points" can fail. The plan uses exact fractions.
14. **"Arrived late" has no threshold.** 7.10 says "long after". The plan: first seen
    locally more than 1 hour after the end date (a tunable constant).
15. **Day display.** The design writes "day 30" and never a year. The plan counts days from
    day 1 with no year, in both calendars, with the time of day in the hover.
16. **A player with no pre-end standing** (joined after the end, 7.2) is left out of the
    final board; the design is silent.
17. **The race calendar cannot use the house formatters.** `ParsekTimeFormat` and
    `KSPUtil.PrintDateCompact` follow the player's own calendar setting (section 3); the
    window needs its own day and real-time formatters.
18. **Message text lives in `RaceCore`.** 15.2 says the row model's text is formatted in
    `UI`; the messages are posted by the shell in `RaceWorld`, which may not name `UI`, so
    their text is built in `RaceCore` (plain words, integers only). Settled (OV-38): the
    text stays in `RaceCore` and goes into `SyncResult.Messages`; R3E-22's driver site
    posts it.

## 10. Questions for the owner

- **Q1. With "no retakes" on: Bogdan's earlier Mun landing is itself "seen first" (he knew
  about yours when he flew it) and does not count. Does Ana, who saw Bogdan's landing,
  still count as having "seen" it when she flies an even earlier one?**
  Options: (a) yes, any real flight you had seen blocks you, whether or not it counts
  itself; (b) no, only a date that counts can block. Recommendation: (a). It matches "you
  cannot take a first you already knew about", it never depends on a chain of other
  players' receipts, and it cannot loop on edited files.
- **Q2. Should the game announce a first you take that no rival had reached ("Mun Landing
  is yours: day 31") with an on-screen message?** Options: (a) only when a rival is
  involved (you beat, lost to, or tied with someone); (b) every first you take. Recommendation:
  (a): the opening hour would otherwise post a message for every new first, and the window
  already shows it.
- **Q3. After the end date, should holder changes on the "now" board still send messages?**
  Options: (a) no, only a change to the final board (a late standing) and the one "race is
  over" message; (b) yes, as before the end. Recommendation: (a): after the end the race is
  decided, and the window shows the "now" board for anyone who keeps playing.
