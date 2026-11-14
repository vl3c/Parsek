# Multiplayer implementation - corrections to the design and open questions

PLAN (2026-11-14). Found while planning; not yet folded into the design.

Sources: the eleven plan files in this folder (sections 3, 4, 9 and 10). "Design" is
`docs/dev/design-competitive-multiplayer.md`; "analysis" is
`docs/dev/research/multiplayer-base-refactoring-2026-11-14.md`. A citation `plan.md:N` is a line
of that plan file. Stock checks were made against `/home/user/ksp-refs` (names and member
signatures only); code checks against this worktree.

## Part 1. Corrections

### Counts

| Class | Items |
|-------|-------|
| WRONG | 17 |
| SILENT | 38 |
| CONTRADICTION | 16 |
| DETAIL | 52 |
| OWNER | 9 |
| REJECTED | 2 |
| Total | 134 |

The plans raised about 170 points; merging duplicates gives the items below. Every WRONG and
REJECTED item carries its check.

### The ten corrections that change the design most

| Item | Section | Change |
|------|---------|--------|
| DC-002 | 6.1, 7.6, 15.13 | Flags are only in the trajectory file; the standing needs an additive `FLAGS` block, or "no payload opened at store build" cannot hold |
| DC-050 | 7.6, 15.13 | Leftovers cannot be built from the standing alone; the store reads each rival tree file (small cfg) as well |
| DC-079 | 7.12 | "A recording of its tree that begins at or before the date" contradicts "does not reach back"; the standing needs a first-use UT per recording |
| DC-016 | 6.3, 15.13 | The ghost payload is always a stripped copy, and the `.prec` epoch rewrite changes bytes; "take each hash from the sidecar version" holds for `.prec` only |
| DC-027 | 7.1, 7.3, 15.7 | Removing the sites "on every return to the main menu" misses in-game loads; reconcile at every scene load request against the loaded game's mark |
| DC-111 | 15.6, 15.11 | `IPlaybackCatalog` cannot sit on the floor: its answers are `Recording` and `RecordingTree` |
| DC-102 | 15.4 | UI cannot create a save through the `RaceWorld` facade (features would call entry code); setup composes in Controllers |
| DC-033 | 7.3, 12 | The KSC area is probably named by collider tags, not the biome map; the biome override becomes conditional on S2 |
| DC-012 | 6.2 | A2 adds a ledger action type; "the tree and ledger types gain nothing" must be reworded |
| DC-031 | 7.2, 15.9 | Validate, Create, Join and the Settings folder change touch the shared folder from a button; 15.9 needs an exception or a port with a main-thread fallback |

### Where two plans disagree

| Topic | Plans | Item | Recommended reading |
|-------|-------|------|---------------------|
| Shape and home of `IPlaybackCatalog` | r0b-prep-playback.md:793-798 vs r3-rival-world.md:951-954 | DC-111 | The playback plan's: port in `Recording`; the rival lane uses an empty catalog of the `Recording` module (`RecordingPlaybackCatalog.None`, added by R3W-09 with its first caller); no `RivalCatalog` type, R3W-05 keeps `RivalStore` only |
| Who registers `raceFormat` and writes golden `v2` | r1-mode-and-setup.md:521-534 vs r3-export-and-exchange.md:917-919 vs test-and-harness.md:879-882 | DC-122 | R1's: R1-12 registers both rows and writes `v2` (the race save and the three manifests); R3E-04 registers nothing and adds `v3` with a standing and its payload at the same format number |
| Where P10's core lives and who calls it | r0b-prep-small.md:792-794 vs r1-mode-and-setup.md:815-818 | DC-102 | R1's: setup composes in Controllers, so the "RaceWorld calls it" reason for LogIO falls away |
| Who builds the A5 style seam | r3-rival-world.md:562-578 vs r5-looks.md:604-607 | DC-118 | R5-03 owns the type and the engine arguments, R5-04 and R5-05 the sites (base Parsek, lands right after P2c); R3W-15 shrinks to the rival lane's style source |
| The missing-part strip and DLC suits | r3-rival-world.md:658-664 vs r5-looks.md:608-611 | DC-048 | R5's: R3W-21 keeps kerbal EVA part names for the dresser |
| The launch-site circle cache | r0b-prep-small.md:783-791 vs r2-sites.md:779-782 | DC-036, DC-037 | The small plan's: the code admits `LaunchPad` and `Runway` names, so the cache can hold the stock KSC |
| Which task fixes the CLAUDE.md "zero Recording references" sentence | r0b-prep-playback.md:807-809 vs r3-rival-world.md:960-963 | DC-133 | P2a-02, the change that removes the reads |
| Where "test tooling ran" lives | r3-export-and-exchange.md:881-883 vs r4-r6-board-and-rules.md:815-818 | DC-008 | Both: persisted on the race scenario node, exported as `DISCLOSURE testTooling` |
| Tool time per recording or per tool | r3-export-and-exchange.md:877-880 vs r4-r6-board-and-rules.md:809-814 | DC-079 | Both: the recording keeps a first-use UT per tool; the standing carries `TOOL` children with `ut`, and the codec derives `ToolsFromUT` (their minimum) |
| Terrain heights "on two machines" | r0-spikes.md:941-943 vs r2-sites.md (R2-02 depends on S5 "heights equal on two machines") | DC-091 | The spike plan's: two processes on one PC gate it; a second machine is a cross-check |

### 6.1 New types

**DC-001 SILENT.** Design 6.1 (lines 480-486) calls `MILESTONES` "the player's ladder table";
7.9 (lines 1278-1279) keys it by `Body/Step`. Found: r4-r6-board-and-rules.md:819-822. A ladder
row unites two stock nodes (landing or splashdown), so each entry stores the stock qualified id
(`Mun/Landing`, `ReachedSpace`) and the board maps it to a ladder row; a retune needs no new
export and `since` stays per exact stock date. Edit: 6.1 says `M id` is the stock qualified id.

**DC-002 CONTRADICTION.** Design 6.1 (line 497): "Flags are inside the recordings (flag
events), not listed separately"; 7.6 (lines 1100-1102) and 15.13 (line 2398): the store is
built from the standing alone and opens no payload; 7.7 (lines 1142-1144) shows a flag from its
plant date. Found: r3-rival-world.md:930-936; flag events are read only from the `.prec`
(`Source/Parsek/TrajectorySidecarBinary.cs:316`). Plan assumes an additive `FLAGS` block in
the standing (site, placer, text, URL, body, position, rotation, UT, pick-up UT). Edit: add it
to 6.1 and drop the sentence at line 497.

**DC-003 SILENT.** Design 6.1 (line 487): `LEFTOVERS { L recordingId fromUT untilUT }` has no
vessel type. Found: r3-rival-world.md:942-945; the tree file has none and today's resolver reads
the vessel snapshot, which is never shared. Plan uses `Ship` until the ghost snapshot is opened;
an additive `vtype` on `L` rows gives the right icon at scene load. Edit: add `vtype`.

**DC-004 SILENT.** Design 6.1 (line 475): `REC id hash ...`, one hash. Found:
r3-export-and-exchange.md:864-866. A recording has two payload files, each with a 16-hex name
and a SHA-256 proof (6.3, lines 586-588). Plan writes `prec`, `precProof`, `ghost`,
`ghostProof`, and `TREE { id hash proof }`. Edit: list these keys.

**DC-005 SILENT.** Design 6.1 (lines 468-470): `prevHash` only. Found:
r3-export-and-exchange.md:867-869. After pruning, `prevHash` alone cannot tell "history
incomplete" from "chain broken". Plan adds `prevSeq`. Edit: add it.

**DC-006 SILENT.** Design 6.1 (line 479): `TREES { TREE id hash }`, no payload shape. Found:
r3-export-and-exchange.md:920-921. The `RACE_TREE` key list, including `sidecarEpoch` (checked
by the `.prec` reader), must be agreed between export (R3E-02) and the rival loader (R3W-03).
Edit: name the tree payload keys in 6.1.

**DC-007 SILENT.** Design 6.1 (lines 499-506): `RaceLink` has no version key; 15.7 (line 2207)
registers only `raceFormat`. Found: r1-mode-and-setup.md:804-805. Plan adds `raceLinkFormat`
and a `race-link` row in `SaveFormat.cs`. Edit: add the key and the row.

**DC-008 SILENT.** Design 7.12 (lines 1395-1396): a save where test tooling ran "says so for
good"; 6.1 (line 491): `DISCLOSURE { modDigest, MOD, difficultyDigest }` has no field for it.
Found: r3-export-and-exchange.md:881-883 (kept in `FactCapture` on the race scenario node) and
r4-r6-board-and-rules.md:815-818 (a `testTooling` disclosure key). The two agree. Edit: add
`testTooling` to `DISCLOSURE` and say it lives on the race scenario node.

**DC-009 SILENT.** Design 6.1 (lines 508-512): `RaceLocal` holds folder, name, look, digest,
show toggles. Found: r1-mode-and-setup.md:809-812. Continue shows "last sync" (7.2, line 790)
and Settings rebuilds the shared files "from its local copies" (lines 828-829), but no key or
copy is listed. Plan adds `lastSyncRealTime`, `Race/race.cfg` and `Race/own/player.cfg`.

**DC-010 SILENT.** Design 15.7 (lines 2201-2204): `RaceLink` holds only facts that never change
and `RaceLocal` is outside the save. Found: r5-looks.md:615-618. The "dressed with look N"
marker must roll back with the roster on every load, so it belongs in the save. Plan: a
`RACE_LOOK_DRESS` child of the race scenario node, save lifetime, one load-policy row.

### 6.2 Changes to existing types

**DC-011 SILENT.** Design 6.2 (lines 555-557) and 7.1 (lines 721-722): the gate reads the mark
as the save is read; nothing says when a new save gets the mark. Found:
r1-mode-and-setup.md:800-803. `SpaceCenterMain` re-reads `persistent.sfs`, so the mark must be
added and the save written before `Game.Start`; P10's core offers that step. Edit: 7.2 Create.

**DC-012 CONTRADICTION.** Design 6.2 (line 533): "The tree and ledger types gain nothing";
lines 543-545 leave A2's home to planning. Found: r3-export-and-exchange.md:884-887. A2 becomes
an additive `GameActionType` member (`VesselEnded`), which follows ELS, tombstones and
deduplication for free. Edit: reword line 533 to "no new fields; one additive action type".

**DC-013 DETAIL.** Design 6.2 (lines 543-545). Found: r3-export-and-exchange.md:888-891. A
`VesselEnded` row behaves like a recovery row after a rewind before it; the plan matches single
player's recovery handling (VERIFY in R3E-12).

**DC-014 DETAIL.** Design 6.2 (line 543): "destroyed outside any recording". Found:
r3-export-and-exchange.md:892-894. Destroy events also fire on scene unload and quit; the plan
hooks `Vessel.Die` (VERIFY in R3E-13).

**DC-015 SILENT.** Design 6.2 (lines 553-554): S6 checks that stock keeps an unknown scenario
node. Found: r0-spikes.md:953-954. Parsek also rewrites saves (rewind copies, the Discard
Re-fly `persistent.sfs` write, the crash reconcile). Edit: S6 covers those too.

### 6.3 Exchange format

**DC-016 WRONG, checked.** Design 6.3 (lines 584-585): "The exporter takes each hash from the
sidecar version the save already recorded, so no file is re-hashed"; 15.13 (line 2400) repeats
it. Found: r3-export-and-exchange.md:858-861 and :80-84. A ghost snapshot node keeps `crew`
values on its parts, so every ghost payload is a stripped copy with its own hash; and the
`.prec` carries the sidecar epoch, so a commit flush and the next save write different bytes
for the same trajectory. Check: `Source/Parsek/Recording.cs:433` (ghost snapshot is a full
`ConfigNode`), `Source/Parsek/CrewReservationManager.cs:53` (reads `crew` from part nodes),
`Source/Parsek/TrajectorySidecarBinary.cs:251` (epoch written into the file). Edit: the hash
comes from the sidecar version for `.prec` only, epoch-pending recordings wait, and ghost files
are hashed once at export.

**DC-017 SILENT.** Design 6.3 (lines 591-592): "a ghost snapshot derived from it (parts only)".
Found: r3-export-and-exchange.md:862-863. Plan strips `crew` and keeps parts, modules and
resources (VERIFY on a real snapshot). Edit: define "parts only" that way.

**DC-018 SILENT.** Design 6.3 (lines 568-574): seq allocation and copy-out order. Found:
r3-export-and-exchange.md:906-908. An unpublished own standing can meet an unknown file with
the same seq written by another machine. Plan renumbers the unpublished tail (no receipt names
it yet).

**DC-019 SILENT.** Design 6.3 (line 602): "after a grace period". Found:
r3-export-and-exchange.md:909. Plan: 3 days of real time. Edit: name it.

**DC-020 CONTRADICTION.** Design 6.3 (lines 605-609): a newer `raceFormat` OR newer recordings
mark the rows "not checked here"; 7.8 (lines 1257-1260) ties the mark to a standing a newer
build wrote. Found: r4-r6-board-and-rules.md:826-829. The check reads only the standing, so the
plan marks rows only for a newer `raceFormat`; newer recordings only stop the ghosts.
Recommended: the plan's reading. Edit: 6.3.

**DC-021 SILENT.** Design 6.3 (lines 612-615): "a fixed character set", not named. Found:
r0b-prep-small.md:781-782. Plan uses today's Windows set (no change on Windows).

**DC-022 SILENT.** Design 6.3 (lines 616-620) fixes the local cache layout, not the shared
folder's layout under `players/<id>/`. Found: r1-mode-and-setup.md:841-843. R1's validator
uses a longest-path constant that R3 owns.

**DC-023 SILENT.** Design 6.3 (lines 621-623): shared-folder safe-writes. Found:
r3-export-and-exchange.md:898-900. `FileIOUtils` leaves `.tmp` and `.bak.<guid>` names that
sync clients upload; the standing grammar must ignore them. Edit: say so.

**DC-024 DETAIL.** Design 6.3 (lines 621-623) and 15.3 (line 1974): the folder adapter sits
"on `FileIOUtils`". Found: r3-export-and-exchange.md:901-902. `FileIOUtils` logs through
`ParsekLog` and has no retry, so the copy worker keeps its own small copy routine.

### 6.4 The isolation invariant

**DC-025 OWNER.** Design 6.4 (lines 669-672) and 11 (lines 1781-1782): the two flown sessions
"must be identical". Found: test-and-harness.md:868-871. Explicit-UT warps land near the UT and
flight physics does not repeat, so byte identity is reachable headless only; the plan uses a
canonical diff with a short, reasoned mask. See Q-13.

### 6.6 Game dates

**DC-026 SILENT.** Design 6.6 (line 705) and 7.8 (line 1269) write "day 30", never a year.
Found: r4-r6-board-and-rules.md:849-850. Plan counts days from day 1 with no year in both
calendars, time of day in the hover. A related DETAIL: `ParsekTimeFormat` and
`KSPUtil.PrintDateCompact` follow the player's own setting, so the window needs its own day
formatter (r4-r6-board-and-rules.md:853-855).

### 7.1 The Multiplayer mode and the race mark

**DC-027 WRONG, checked.** Design 7.1 (lines 728-730), 7.3 (lines 993-994) and 15.7 (line
2206): the sites are removed "on every return to the main menu". Found: r2-sites.md:746-750 and
test-and-harness.md:872-874. Stock loads another save from inside a game, and the harness
`LoadGame` does too, without the main menu. Plan reconciles at every scene load request against
the loaded game's mark, which also covers race to race (case 31). Check: `harness/run.py:1450`
states that the seam's LoadGame bypasses the Main Menu; the stock `HighLogic.cs:795` cite was
not reopened (no decompile here). Edit: 7.1, 7.3 and 15.7.

**DC-028 SILENT.** Design 7.1 (lines 720-722) and 15.8 (line 2211): one predicate, the race
gate. Found: r1-mode-and-setup.md:837-840. Nothing says how it composes with
`ParsekGameModeGate` (in Patches). Plan: the Controllers host passes the inert flag in; an inert
Parsek means no race, mark kept verbatim.

### 7.2 The race setup dialog

**DC-029 OWNER.** Design 7.2 (lines 739-751): the Create list has no calendar; 6.1 (line 446)
and 6.6 (lines 703-705): the manifest carries one. Found: r1-mode-and-setup.md:813-814. Plan
takes the creator's stock setting. See Q-09.

**DC-030 DETAIL.** Design 15.14 (lines 2426-2430): Continue is a load through a declared
initiator. Found: r1-mode-and-setup.md:830-832. The only production-quality load-and-start
sequence is in the seam; R1-16 lifts it beside P10's core.

**DC-031 CONTRADICTION.** Design 7.2 (lines 745-747, 823-833): Validate, Create, Join and the
Settings change read and write the shared folder; 15.9 (lines 2230-2232): "the game never
touches the shared folder from a Unity callback". Found: r1-mode-and-setup.md:819-822. R1 has
no copy worker; the plan uses a port with 15.9's main-thread fallback adapter, replaced in R3.
Edit: 15.9 names the setup dialog as the one place that touches the folder directly, through
the port.

### 7.3 The four sites

**DC-032 CONTRADICTION.** Design 7.3 (line 905) and 12 (line 1792): the move takes "the stock
space center object and its flattening terrain patch"; 7.3 (lines 991-993): "Site 0's stock
patch stays where it is". Found: r2-sites.md:751-755. Plan follows the second: the stock decal
stays, and every race save adds the patches of sites 1 to 3. Edit: line 905.

**DC-033 WRONG, partly checked.** Design 7.3 (lines 966-968) and 12 (line 1797): "The
surrounding KSC biome comes from Kerbin's biome map ... the design requires a biome override".
Found: r2-sites.md:756-762 and r0-spikes.md:944-946. Stock names the landed-at place from
collider and trigger tags and the experiment biome reads that name first; the survey's biome
read finds no KSC biome at the KSC's longitudes. Check: `Part.SetLandedAt`, `MiniBiome` and
`ScienceUtil.GetExperimentBiome` exist in ksp-refs; bodies not read (S2 decides). Edit: the
override is conditional on S2 (R2-10).

**DC-034 WRONG, checked.** Design 7.3 (lines 985-991): the move list is the space center's
cache, "each stock launch site caches its spawn points", Parsek's constants and circles, and
the patches. Found: r2-sites.md:763-768 and r0-spikes.md:947-949. Also needed: a longitude
rotation correction for the city, the facility spawn points
(`PSystemSetup.SpaceCenterFacility.SpawnPoint`, refreshed only on a level change), every reader
of the space center cache (survey contracts, morning warp, records, random surface points,
music) and `CommNetHome`. Check: `SpaceCenterFacility`, `SpawnPoint` and `CommNetHome` exist in
ksp-refs. Edit: extend the list.

**DC-035 DETAIL.** Design 7.3 (lines 917-922): fully built copies. Found: r2-sites.md:769-774.
A copy of the live KSC shows the local levels and registers duplicate ids; the recipe uses the
last `UpgradeLevel.facilityPrefab` and strips before `Awake`.

**DC-036 WRONG, checked.** Design 7.3 (lines 988-990): Parsek "caches the circles of the other
stock launch sites"; 15.11 P1 (line 2324). Found: r0b-prep-small.md:783-791. The runtime read
admits the names `LaunchPad` and `Runway`, so the cache probably also holds the stock KSC's own
pad and runway, which in a race save at another site would retire flights parked at the stock
KSC. Check: `Source/Parsek/LaunchSiteExclusionZones.cs:79-83` (the name list) and `:269-270`
(the filter that admits them). Edit: "the stock launch sites, possibly the KSC's own"; the
`runtime=N` log line settles it.

**DC-037 REJECTED.** Design 7.3 (lines 988-990). Found: r2-sites.md:779-782 says the cache
needs no reset for the move because "the cached circles are the Making History sites, which do
not move". Reason: the same read admits `LaunchPad` and `Runway`
(`Source/Parsek/LaunchSiteExclusionZones.cs:269-270`), so that claim is not established; keep
P1-03's version key until the log check of DC-036 says otherwise.

**DC-038 OWNER.** Design 7.3 (lines 948-951) and 8 case 21 (line 1640). Found:
r2-sites.md:775-778. The race's "alternate sites off" maps onto stock `AllowOtherLaunchSites`,
which a player can change mid-race, and case 21 allows difficulty changes. See Q-11.

**DC-039 DETAIL.** Design 12 (line 1792) lists the optimizer's launch-site rule as a reader of
the space center position. Found: r2-sites.md:783-784; it reads no position
(`Source/Parsek/RecordingOptimizer.cs:647-650` reads only `LaunchSiteName`). No work.

**DC-040 DETAIL.** Design 15.11 P1 (line 2324): "a version for caches to key on". Found:
r2-sites.md:785-787. `LaunchSiteOnlyGhost` caches on `RecordingStore.StateVersion` only
(`Source/Parsek/LaunchSiteOnlyGhost.cs:36-39`); the version must reach it (R2-05 if P1 does
not).

**DC-041 SILENT.** Design 7.3 (lines 934-935): Parsek's readers of launch-site names must not
match a copy. Found: r2-sites.md:788-790. At every home site the stock names are `LaunchPad`
and `Runway`, so a recording's launch-site name does not say which site; R3 takes the site
from the standing's player (`siteSlot`).

**DC-042 DETAIL.** Design 7.3 (lines 852-856), 12 (line 1796) and 13.C.2 (lines 1868-1871).
Found: r2-sites.md:791-793 and r0-spikes.md:932-935. No fine survey grid covers the spots, the
fine step is about 262 m and no row is the KSC latitude (-0.074); S1b scans that row at about
20 m and checks the whole footprint is dry.

**DC-043 DETAIL.** Design 7.3 (line 844): the KSC footprint centre. Found:
r0-spikes.md:958-960. The scan tool's centre is the pad-runway midpoint, not the `PQSCity`
origin a move uses; site offsets come from S4a's measured origin.

**DC-044 DETAIL.** Design 7.3 (lines 978-979) and 13.C.2 (lines 1870-1871): the survey lists
what is near each site. Found: r0-spikes.md:936-940. The survey's biome data is unreliable by
its own README; biomes come from the stock function in game (S2).

### 7.4 Looks

**DC-045 WRONG, partly checked.** Design 7.4 (lines 1028-1031): a missing kerbal part makes the
"whole ghost fall back to the plain placeholder sphere". Found: r5-looks.md:600-603. True for an
EVA kerbal (a one-part vessel); a kerbal in an external seat of a larger vessel just drops out.
Check: `Source/Parsek/GhostVisualBuilder.cs:346-351` returns no visual when nothing was built;
the engine's sphere fallback (`GhostPlaybackEngine.GhostVisuals.cs:163-167`) was not reopened.
Edit: the substitution of A4 covers both cases.

**DC-046 DETAIL.** Design 7.4 (line 1004): look 1 (slim) needs nothing. Found:
r0-spikes.md:961-963. Which GameData folder ships the slim EVA parts is not in the DLL; S3
checks on the no-DLC copy.

**DC-047 OWNER.** Design 7.4 (lines 1013-1015) ships the replacement sets as `SUITCOMBOS`;
15.8 (lines 2211-2216) and section 0 (line 113) keep single-player saves untouched. Found:
r5-looks.md:622-624. A `SUITCOMBOS` node under `GameData` shows in the stock suit picker of
every save. See Q-19.

**DC-048 DETAIL.** Design 7.6 (lines 1103-1105): missing parts are skipped. Found:
r5-looks.md:608-611 against r3-rival-world.md:658-664 (R3W-21). The strip removes every part
with no `PartLoader` entry, so a rival EVA kerbal in a DLC suit part becomes unplayable before
the dresser sees it. Recommended: R3W-21 keeps the kerbal EVA part names R5-09 accepts.

**DC-049 SILENT.** Design 7.4 (lines 1016-1017): a machine without the DLC uses the replacement
on the player's own kerbals. Found: r5-looks.md:625-627. Stock resets an uninstalled suit to
Default at load, before Parsek runs, so a re-dress is needed (R5-10, S3c). A related DETAIL:
the starting crew is added in `GenerateInitialCrewRoster` before any scenario exists, so the
first load of a race save dresses it (r5-looks.md:619-621). Also DETAIL: `RaceCore` uses its
own suit enum because the stock one is not inert (r5-looks.md:628-629).

### 7.5 Export

**DC-050 WRONG, checked.** Design 7.6 (lines 1100-1101) and 15.13 (line 2398): "The rival store
is built from the standing alone". Found: r3-rival-world.md:937-941. End position, terminal
orbit, endpoint body, terminal state and the debris flag are not in `RECORDINGS`; they are in
the tree file. The plan reads the rival trees' small cfg files at store build and never a
`.prec` or `.craft`. Check: `Source/Parsek/RecordingTreeRecordCodec.cs:386` writes `isDebris`
in the tree record. Edit: "from the standing and the tree files"; the no-payload test is
scoped to `.prec` and `.craft`.

(DC-050 sits here because it changes what export must ship; its design text is in 7.6.)

**DC-051 DETAIL.** Design 7.5 (lines 1071-1073) and 15.13 (line 2401). Found:
r3-export-and-exchange.md:910-911. With the main-thread transport an export in flight writes
the own mirror only and copies out after the flight.

**DC-052 DETAIL.** Design 7.5 (lines 1069-1070): a tick "after a tree commit". Found:
r3-export-and-exchange.md:912-914. `MergeDialog.OnTreeCommitted` is a static delegate field
(`Source/Parsek/MergeDialog.cs:73`); a second adder would be a static written by another type,
so the plan polls the cached ERS / ELS instances.

**DC-053 DETAIL.** Design 7.5 (lines 1057-1060): nothing is exported during a Re-Fly. Found:
r3-export-and-exchange.md:915-916. The marker accessor is in Controllers
(`Source/Parsek/SessionSuppressionState.cs:50`); the plan adds
`EffectiveState.HasActiveReFlySession`.

### 7.6 Import and the rival store

**DC-054 DETAIL.** Design 7.6 (lines 1100-1102): flights load lazily. Found:
r3-rival-world.md:964-967. The engine reads `Points` before activation; unopened entries are
skipped by the host with a new neutral skip reason (R3W-01).

**DC-055 DETAIL.** Design 7.6 (lines 1103-1105). Found: r3-rival-world.md:968-969. Today one
warning per missing part floods the log; the loader strips them first and logs once.

**DC-056 CONTRADICTION.** Design 7.6 (line 1107), 8 case 28 (lines 1663-1664) and 7.13 (line
1431) use "not checked here" for a missing body; 7.8 (lines 1251-1260) says that row is still
checked and scores. Found: r4-r6-board-and-rules.md:823-825. Edit: word the first mark "cannot
be watched here" everywhere.

### 7.7 What a rival's world looks like

**DC-057 DETAIL.** Design 7.7 (lines 1137-1139) and 15.6 (lines 2167-2171): leftovers are
entries "in a cheap hold mode". Found: r3-rival-world.md:955-959. No engine mode is needed: the
engine holds the last pose when the end extends and plays an orbit tail after the last point;
the loader widens `EndUT` and appends a tail. An empty `untilUT` maps to a finite horizon.

**DC-058 SILENT.** Design 15.6 (lines 2172-2177) and 15.11 P3 (line 2328): rivals use the
extracted map-vessel primitive. Found: r3-rival-world.md:946-947. The primitive builds orbiting
vessels only; landed leftovers in map view and the Tracking Station need a landed variant
(R3W-17).

**DC-059 DETAIL.** Design 7.7 (lines 1143-1151) and 12 (line 1806): the replica is "the stock
flag model" and S7 must prove the dialog takes supplied text. Found: r3-rival-world.md:948-950
and r0-spikes.md:950-952. The flag part has no mesh file to clone, so S7 also says how to build
the model; the dialog entry `FlagPlaqueDialog.Spawn(string, string, Callback)` is public
static, so S7's dialog half is small.

**DC-060 SILENT.** Design 7.7 (lines 1143-1144): "the rival's flag texture". Found:
r3-rival-world.md:975-976. `flagURL` is a path in the writer's install and is not shared; the
plan falls back to the default flag.

**DC-061 DETAIL.** Design 7.7 (lines 1192-1193): the show toggle. Found:
r3-rival-world.md:973-974. Its setter has no caller before the R4 window, so R3 reads `SHOW`
only (uncalled-method ratchet).

**DC-062 REJECTED.** Found: r5-looks.md:633-634 says the design is silent on rival ghosts in the
Space Center scene. Reason: design 7.7 (lines 1180-1183) and 15.6 (line 2137) say that scene
shows rival scenery only and nothing that moves in the first build. The options source for a
later KSC rival lane is a later concern.

### 7.8 Milestones and the board

**DC-063 DETAIL.** Design 7.8 (lines 1209-1210 and 1229-1230): two VERIFY items. Found:
r3-export-and-exchange.md:147-155. Both answered: the later row stays in the effective ledger
(`Source/Parsek/GameActions/MilestonesModule.cs:157-158` only flips it to not effective), and a
Re-Fly tombstones only eligible rows of the superseded subtree; untagged rows never
(`Source/Parsek/GameActions/TombstoneEligibility.cs:59`). Edit: drop the two VERIFY notes.

**DC-064 SILENT.** Design 7.8 (lines 1226-1228) and 8 case 12 (lines 1608-1610) cover rows with
no flight. Found: r3-export-and-exchange.md:895-897. A milestone row tagged to a flight outside
ERS is not covered; the plan drops it (logged) rather than crediting a different flight.

**DC-065 DETAIL.** Design 7.8 (lines 1243-1245): shares are not rounded. Found:
r4-r6-board-and-rules.md:845-846. With doubles three thirds do not add to 1; the board uses
exact fractions.

**DC-066 OWNER.** Design 7.8 (lines 1268-1273): messages when a sync changes a holder. Found:
r4-r6-board-and-rules.md:838-842. Not said: what the first board of a session compares against
(plan: the board built at load, nothing persisted) and whether an uncontested own first is
announced. See Q-14 and Q-16.

### 7.9 The ladder

**DC-067 WRONG, checked.** Design 7.9 (line 1291): left out is "anything stock does not define
(a landing on Jool or the Sun)". Found: r4-r6-board-and-rules.md:798-801. Stock builds
`Landing`, `Splashdown` and the return nodes for every body. Check: ksp-refs
`KSPAchievements.CelestialBodySubtree::.ctor` creates `CelestialBodyLanding` and
`CelestialBodySplashdown` before any conditional branch. Edit: "left out by choice".

**DC-068 WRONG, checked.** Design 7.9 (lines 1278-1286): ids are `Body/Step`, with "reach
space" a Kerbin first. Found: r4-r6-board-and-rules.md:802-804. The reach-space id is the bare
`ReachedSpace` (no body), and the returns are `ReturnFromFlyBy`, `ReturnFromOrbit`,
`ReturnFromSurface`. Check: ksp-refs holds the `ReachedSpace` and `ReturnFrom` literals and
`ReachSpace` has no body field in the plan's read. Edit: give the exact ids.

**DC-069 SILENT.** Design 7.9 (lines 1284-1288) and 13.C.1. Found:
r4-r6-board-and-rules.md:805-808. No multiplier for Jool itself or the Sun, and "surface EVA or
flag 1 more" reads as one step or two. Plan assumes Jool 3 (flyby, orbit, return from orbit),
the Sun out, one step "kerbal on the surface" worth 1. Tunable.

### 7.10 The end date

**DC-070 SILENT.** Design 7.10 (lines 1326-1327) and 7.12 (line 1415): "long after". Found:
r4-r6-board-and-rules.md:847-848. Plan: first seen locally more than 1 hour after the end
date, a tunable constant.

**DC-071 SILENT.** Design 7.10 (lines 1312-1316) and 7.2 (lines 787-788). Found:
r4-r6-board-and-rules.md:851-852. A player with no pre-end standing (joined after the end) is
left off the final board.

### 7.11 House rule: no retakes

**DC-072 OWNER.** Design 7.11 (lines 1342-1344 and 1358-1361) and 13.B (line 1854): only a
scoring date blocks. Found: r4-r6-board-and-rules.md:830-832. Open: whether a date that is
itself blocked ("seen first") still blocks. See Q-15.

**DC-073 DETAIL.** Design 7.11 (lines 1359-1361): a date that becomes scoring blocks "from the
standing in which the scoring date first appeared". Found: r4-r6-board-and-rules.md:833-837.
The kept standings cannot find that standing; the plan uses the entry's `since`, which differs
only in a case the exporter does not produce.

**DC-074 SILENT.** Design 7.11 (lines 1374-1375) and 6.3 (lines 597-601). Found:
r4-r6-board-and-rules.md:843-844. If the standing named by `since` is missing, the receipts
cannot be read; the plan does not block and marks "history incomplete".

### 7.12 Disclosure

**DC-075 WRONG, checked.** Design 7.12 (line 1389): "hack gravity" among the stock cheat
options. Found: r0-spikes.md:916-919. `CheatOptions` has no gravity field; it is the
`HackGravity` widget's state, which Parsek already reads. Check: ksp-refs `CheatOptions` fields
(no gravity field); `Source/Parsek/GravityHackDetector.cs:81`. Edit: move it to the debug
widgets.

**DC-076 WRONG, partly checked.** Design 7.12 (line 1392): debug actions include "fill
resources". Found: r0-spikes.md:925-926; no such action among the 1.12.5 cheat types. Check: no
type or string named for it in ksp-refs (one `refillTank` literal elsewhere); S9 confirms in
game. Edit: drop it unless S9 finds it.

**DC-077 WRONG, checked.** Design 7.12 (lines 1388-1394): the tool list. Found:
r0-spikes.md:927-931. Missing: `IgnoreEVAConstructionMassLimit`, `IgnoreKerbalInventoryLimits`,
`IgnoreAgencyMindsetOnContracts`, `MiddleMouseClickSetPosition`, max tech, max facilities, max
XP, repair, the console's funds / science / reputation sets, the Physics, Difficulty, Kerbal and
contract screens, comets and the object thrower. Check: the four `CheatOptions` fields exist in
ksp-refs. Edit: list them, or say which S9 puts out of reach.

**DC-078 OWNER.** Design 7.12 (lines 1391-1394) is silent on milestones completed outside any
flight. Found: r0-spikes.md:920-924. `MaxProgression`, the console's `b4d455` and the legacy
cheat window reach `ProgressNode.CheatComplete()`, which completes the node; such a first could
be credited to a covering flight. Check (names): `CheatComplete` and `MaxProgression` exist in
ksp-refs. See Q-26.

**DC-079 CONTRADICTION.** Design 7.12 (lines 1401-1403): a date is marked "when a recording of
its tree that begins at or before that date carries a tool", and "a tool used later in the same
flight does not reach back"; 13.B (line 1858) repeats the first; 6.1 (line 475) carries one
`tools` flag. Found: r3-export-and-exchange.md:877-880 and r4-r6-board-and-rules.md:809-814.
With a per-recording flag, a tool first used after the date inside the same recording marks it.
Decided: the recording keeps a first-use UT per tool; the `REC` entry carries one `TOOL` child
per tool with that `ut` (no second key); the standing codec exposes `ToolsFromUT` (their
minimum) and the tool ids as a derived view; a date is marked when a recording of its tree has
`ToolsFromUT <= date`. Edit: 7.12, 6.1 and 13.B.

**DC-080 CONTRADICTION.** Design 11 (lines 1767-1768): "B reaches the milestone earlier; A
syncs; the lane asserts the holder change"; 7.12 (lines 1395-1399): a save where the seam or
the test runner ran is marked for good and a marked flight scores nothing. Found:
test-and-harness.md:857-861. A harness-flown B never takes a milestone. Plan proves the holder
change with generator-built rival folders (RACE-7) and asserts the marked rows (RACE-6). Edit:
11's second lane. See Q-24.

**DC-081 SILENT.** Design 7.12 (lines 1395-1396): "injected recordings" mark the save. Found:
test-and-harness.md:862-865. Injection runs offline before boot, so Parsek cannot see it ran;
the plan never injects into a race save, and the disclosure slice decides whether the injector
stamps the mark or refuses race saves. Also open: what "ran" means for the seam (an armed addon
with no verb, or a verb executed); the plan assumes any received verb marks the save
(test-and-harness.md:866-867).

**DC-082 DETAIL.** Design 7.12 (lines 1410-1411): single player detects a cheat currency
change. Found: r3-export-and-exchange.md:199-201. A stock `Set` call fires no change query and
is not seen.

### 7.13 The Space Race window

**DC-083 CONTRADICTION.** Design 15.2 (line 1950): the row model's dates are numbers and "the
text is formatted in `UI`"; 7.8 (lines 1268-1270) has `RaceWorld`'s shell post the messages.
Found: r4-r6-board-and-rules.md:856-858. `RaceWorld` may not name `UI`, so message text is built
in `RaceCore` (plain words, integers). Edit: 15.2 says message text lives in `RaceCore`.

### 8. Edge cases

**DC-084 CONTRADICTION.** Design 8 case 3 (lines 1573-1576): same player on two machines, the
importer only warns; 7.15 (lines 1492-1495) and the failure table (line 1686): a file this
game wrote is put back. Found: r3-export-and-exchange.md:903-905. Two machines would put back
each other's files forever. See Q-12.

**DC-085 CONTRADICTION.** Design 8 case 29 (lines 1665-1667): the others see a new name or look
"at their next sync"; 7.6 (lines 1095-1096): ghosts update only at scene changes. Found:
r5-looks.md:635-636. Edit: "at the next scene after their next sync".

**DC-086 DETAIL.** Design 7.4 (lines 1036-1037): map icons carry the color. Found:
r5-looks.md:637-638. A focused rival map vessel shows stock lime (the stock focus color);
accepted as stock behavior.

### 11. Test plan

**DC-087 SILENT.** Design 11 (line 1764): "a temporary shared folder". Found:
test-and-harness.md:891-893. The harness may not touch a folder outside the instance
(`harness/README.md:110`); the plan stages it inside the instance, so a folder elsewhere on
disk is never exercised.

**DC-088 SILENT.** Design 15.2 (line 1956) names seam verbs for the lanes; 11 (lines
1764-1775) needs them. Found: test-and-harness.md:894-896. No seam surface is designed; the plan
adds `RaceSetup`, `RaceCommand`, three `ListHandles` kinds and one harness folder step (T-06).

**DC-089 DETAIL.** Design 11 (lines 1764-1782). Found: test-and-harness.md:883-890 and
:897-899. A recursive fixture glob would read rival payload under `harness/fixtures/saves/`
(race folders go to `harness/fixtures/race/`); one `RunTests` per spec; race lanes load at the
Space Center (known gate 6); the offline analyzer never reads the rival cache.

### 12. Feasibility

**DC-090 SILENT.** Design 15.13 (lines 2393-2394): costs "to be measured in the spikes". Found:
r0-spikes.md:955-957. Only S5 measures a cost; the plan adds S11 (per-ghost frame cost, lazy
first open) and S12 (signing inside KSP's Mono, for 12 line 1810, optional). Edit: 14's R0 row
and 15.11's spike list name S11 and S12 (and S13, the close-out).

**DC-091 DETAIL.** Design 12 (line 1795): "S5: sample heights on two machines". Found:
r0-spikes.md:941-943. Planned as two processes on one PC; a second machine is a cross-check,
not a gate. r2-sites.md R2-02 still reads "two machines"; the spike plan's reading stands.

### 14. Implementation phasing

**DC-092 CONTRADICTION.** Design 14 (lines 1903-1904): R3 exports standings; milestone tables
are R4. Found: r4-r6-board-and-rules.md:67-68 and the export slice's `MILESTONES` writer
(R3E-11). The standing's `MILESTONES` block needs the calendar, the ladder and the milestone
table, so R4-01 to R4-03 land in R3. Edit: move them to R3's row.

### 15.2 to 15.4 Modules, ports and dependency rules

**DC-093 SILENT.** Design 15.3 (line 1975): `IRaceTransport` asks for copies and reports
arrivals. Found: r3-export-and-exchange.md:870-872. Copy-out needs the folder's highest seq,
import the standings list, heal the proofs of shared files. Plan adds `RequestListing` and
`RequestProof`.

**DC-094 CONTRADICTION.** Design 15.3 (line 1978): `IRacePayload`'s adapter is in `RaceWorld`
"over the save's sidecar versions and the rival cache"; 15.2 (line 1951): only `RaceExport`
reads the own model; 15.4 (lines 1999-2000): `RaceWorld` names `Recording` only in its loader.
Found: r3-export-and-exchange.md:873-876. Edit: the own half is in `RaceExport`, the cache half
in `RaceWorld`.

**DC-095 DETAIL.** Design 15.2 (line 1952) calls `RaceWorld` runtime-coupled; 15.14 (lines
2410-2414) does not list `[size].runtimeCoupled`. Found: r1-mode-and-setup.md:835-836. R1-01
adds it.

**DC-096 CONTRADICTION.** Design 15.11 P7 (line 2333): a save-root accessor on `RecordingPaths`
for the local race directory; 15.4 (lines 1999-2000): `RaceWorld` names `Recording` types only
in its loader. Found: r1-mode-and-setup.md:826-829. Plan computes the path in `RaceWorld` from
`KSPUtil.ApplicationRootPath` and `HighLogic.SaveFolder`. Edit: P7 drops the accessor or says
who uses it.

**DC-097 DETAIL.** Design 15.4 (lines 2067-2072) and 6.4 (lines 661-663): "Recording types only
in the loader" as a grep-audit gate. Found: r1-mode-and-setup.md:833-834. A grep cannot know a
type's module; it becomes an arch-suite cell over the text scan's map (T-09).

**DC-098 DETAIL.** Design 15.4 (lines 2007-2009): P12 teaches both parsers the list form.
Found: r0b-prep-small.md:805-808. Today `parse_edge_spec("A -> B | C")` returns `("A", "B | C")`
and the report skips it silently; P12-01 makes it refuse `|`.

**DC-099 SILENT.** Design 15.11 P10 (line 2336) does not name the module of the
create-and-start core. Found: r0b-prep-small.md:792-794 puts it on the floor (`LogIO`) "so that
`RaceWorld` can call it". See DC-102 for the disagreement.

**DC-100 DETAIL.** Design 15.6 (line 2131): one engine reads one store. Found:
r0b-prep-playback.md:815-816. In-game tests (`RuntimeTests.cs:11787`, `:22974`) build a
second engine on the own positioner, so two engines share one lane there; they are not a model
for the rival lane.

**DC-101 DETAIL.** Design 15.6 (line 2131): a spawn budget shared by both engines. Found:
r0b-prep-playback.md:819-820. `FrameContext` has no frame number, so the race host resets the
shared budget once per frame before the first engine runs.

**DC-102 WRONG, checked.** Design 15.4 (lines 2002-2003): "`UI` references ... the `RaceWorld`
facade (commands: ... create, join)". Found: r1-mode-and-setup.md:815-818. Creating a save
needs P10's core and `GamePersistence`, entry-layer work, so a `RaceWorld` (features) call into
it is a backward edge. Plan: `ParsekRaceSetup` in Controllers, behind an interface declared in
`UI`. Check: against 15.2 (lines 1952 and 1954: `RaceWorld` features, Controllers entry). The
small plan (r0b-prep-small.md:792-794) instead places P10 in `LogIO` so `RaceWorld` can call
it. Recommended: R1's reading; with setup in Controllers, P10's placement no longer depends on
a `RaceWorld` caller.

### 15.5 Rival data is its own type

**DC-103 SILENT.** Design 15.5 (lines 2113-2114): every key held for a rival ghost is the pair
(player id, recording id). Found: r0b-prep-playback.md:810-814. Static caches keyed by a bare
recording id stay shared across lanes (tail-lift plan caches, `s_previewRecordingForTailLift`,
`SectionAnnotationStore`, `RenderSessionState`). Safe while a rival trajectory never reaches
them (flattened, no tail lift); R3W-28's gate pins that. Edit: state the exception.

### 15.6 The rival ghost lane

**DC-104 CONTRADICTION.** Design 15.11 P2c (line 2327): `LoopSyncParentIdx` "leaves
`IPlaybackTrajectory`"; 15.6 (line 2132): "the setter leaves the interface". Found:
r0b-prep-playback.md:789-792. Two engine readers need the getter. Check:
`Source/Parsek/IPlaybackTrajectory.cs:82` is `{ get; set; }`. Recommended: remove the setter only;
`RivalFlight` returns -1. Edit: 15.11's row.

**DC-105 SILENT.** Design 15.6 (line 2131) and A5 (line 2354). Found:
r0b-prep-playback.md:817-818. `GhostMapPresence` has its own spawn throttle, outside P2c's
shared budget; the P3 slice owns it.

**DC-106 DETAIL.** Design 15.11 A5 (line 2354): "one ghost budget object". Found:
r0b-prep-playback.md:825-826. P2c-04's object is a per-frame spawn count; it is named
`GhostSpawnFrameBudget` so the two do not collide.

**DC-107 DETAIL.** Design 15.6 (line 2133): loop units are host state. Found:
r0b-prep-playback.md:832-833. `builtLoopUnits` stays on the playback host; a rival lane has no
loop units.

**DC-108 SILENT.** Design 15.6 (lines 2172-2177): rivals register in the same guard set. Found:
r0b-prep-map-watch-codec.md:677-680. `RemoveAllGhostVessels` clears the whole guard set, so
an own cleanup or rewind would unregister live rival pids. Check (code):
`Source/Parsek/GhostMapPresence.ProtoVesselLifecycle.cs:405` (`ghostMapVesselPids.Clear()`).
P3-03 adds an owner record. Edit: 15.6 says teardown is per owner.

**DC-109 SILENT.** Design 15.11 P3 (line 2328): extract "about 400 lines". Found:
r0b-prep-map-watch-codec.md:681-685. The creation log and the `GhostCreated` trace stay in
`GhostMapPresence` (pinned by `MapRenderTracerCoverageTests.cs:224-249`); a rival map presence
emits its own creation line and trace. 15.6 is silent on rival tracing.

**DC-110 DETAIL.** Design 15.11 P3 (line 2328) lists "strip at save" in the per-lane primitive.
Found: r0b-prep-map-watch-codec.md:686-689. One strip at save removes every registered ghost
whatever its lane (`SavePathHardeningTests.cs:163` pins one production call); the plan keeps
one strip.

**DC-111 WRONG, checked.** Design 15.6 (line 2143) and 15.11 P2a (line 2325): `IPlaybackCatalog`
"on the floor". Found: r0b-prep-playback.md:793-798. Its answers are `Recording` and
`RecordingTree` (model layer), so a floor placement adds Core-to-Recording backward pairs; the
plan puts it in the `Recording` module, below every consumer, and a rival catalog that answers
"none" to every lookup. r3-rival-world.md:951-954 asks for a neutral return instead, or for
`RivalCatalog.cs` to join the loader allowlist. Check: the engine's lookup returns
`out RecordingTree tree` (`Source/Parsek/GhostPlaybackEngine.SectionPositioning.cs:355-372`).
Decided: the playback plan's placement. The rival lane is given an empty catalog that lives in
the `Recording` module (`RecordingPlaybackCatalog.None`, added by R3W-09 with its first caller),
since the engine asks only for a debris parent a rival never has (r3-rival-world.md:369-371).
There is no `RivalCatalog` type in `RaceWorld`, so the loader rule holds without an allowlist
entry. Edit: 15.6 (the rival store is not a catalog) and P2a's row.

**DC-112 DETAIL.** Design 15.6 (line 2136) and P3b (line 2329): 45 watch sites. Found:
r0b-prep-map-watch-codec.md:705-708. Today 16 committed-list read lines in the controller and
about 40 outside reads in 15 files; P3b keeps the outside reads but one.

### 15.7 State and lifetimes

**DC-113 WRONG, checked.** Design A7 (line 2356) and 15.7 (line 2205): "the file collector and
the test-batch crash reconcile do sweep the whole folder". Found: r1-mode-and-setup.md:796-799.
`ParsekFileCollector` never copies `Parsek/Race/`; the sweeps that do are `collect-logs.py`
(15.14 line 2446 names it), the test-batch reconcile and the in-game runner's batch snapshot and
restore (`InGameTestRunner.cs:1965-1995`, `:2579`), which A7 does not name. Check:
`Source/Parsek/ParsekFileCollector.cs:203` and `:235-236` walk only `Recordings`,
`RewindPoints` and `Saves`. Edit: A7 names `collect-logs.py` and the runner's snapshot.

**DC-114 DETAIL.** Design A7 (line 2356): "seven copies" of the scenario-node lookup. Found:
r1-mode-and-setup.md:792-795. Measured: six over a proto list and three over a GAME node in
production, plus one in tooling; the plan replaces nine.

### 15.9 File access and the main thread

The owner's yes for the copy worker (15.9, lines 2239-2243; 13.B line 1849) is Q-18.

### 15.11 Prerefactors, additions and extra spikes

**DC-115 OWNER.** Design 15.11 (lines 2319-2320) and 14 (line 1900): every prerefactor has no
behavior change; lines 2342-2343: the longitude wrap is "worth doing for single player alone".
Found: r0b-prep-small.md:772-775. Committed fixtures hold longitudes outside [-180, 180],
including about -434.5 over the KSC, so the wrap changes single-player results. Check (code):
`harness/fixtures/saves/eve-gilly-recorded/` and `bdock-recorded-career/` hold `-434.`
values. See Q-04.

**DC-116 CONTRADICTION.** Design 15.11 P1 (line 2324): `HomeSpaceCenter` carries the runway end,
the strip half-width and a setter; 15.14 (lines 2418-2420): no method may land with test
callers only. Found: r0b-prep-small.md:776-780. The code has only the west threshold
(`Source/Parsek/SpawnCollisionDetector.cs:219-225`) and a setter has no production caller in
R0b; R2 adds the three with S4's numbers. Edit: P1's row.

**DC-117 DETAIL.** Design 15.11 P11 (line 2337): specs "derive" the window count. Found:
r0b-prep-small.md:767-771. Spec files are read raw by at least five loaders, so specs write
`windows=\d+` and the hlib cells hold the derivation.

**DC-118 DETAIL.** Design 15.11 A5 (line 2354) and 14 (lines 1903, 1905): colors are R5, the
style seam lands with its first user. Found: r3-rival-world.md:970-972 (a grey placeholder in
R3, R5 fills colors) and r5-looks.md:604-607 (R5 builds the type and sites). Decided: R5-03
owns the type (a sealed class, null means no style) and the engine arguments, and R5-04 and
R5-05 own the sites; they are base Parsek changes that land right after P2c. R3W-15 shrinks to
the rival lane's style source with a neutral placeholder color until R5-07. Also DETAIL:
`MapMarkerRenderer` is in Controllers (`scripts/arch/modules.toml:102`), so a rival marker's
color is handed in by a scene controller (r5-looks.md:612-614).

**DC-119 DETAIL.** Design 15.11 (line 2341): P2b "on its own". Found:
r0b-prep-playback.md:821-824. P2b is nine code PRs and a tooling PR in one uninterrupted window
with one flight proof at the end; `il_compare` cannot fold a renamed field or a new parameter,
and CI does not run `scripts/refactor` tests.

**DC-120 OWNER.** Design 15.11 (lines 2341-2342): "P6 last, because it touches saved bytes".
Found: r0b-prep-map-watch-codec.md:690-692. P6 shares no file with P2a to P3b and writes no byte
differently. See Q-06.

**DC-121 WRONG, checked.** Design 15.11 P6 (line 2332): the load from a given path "exists only
under test-hook names". Found: r0b-prep-map-watch-codec.md:693-697. For snapshots a production
entry exists; only the trajectory needs one, and the whole-recording loader needs no twin.
Check: `Source/Parsek/SnapshotSidecarCodec.cs:123` (`internal static bool TryLoad(string path,
...)`). Edit: "today the trajectory load exists only under test-hook names".

### 15.12 to 15.14 Layout, cost and gates

**DC-122 DETAIL.** Design 15.7 (line 2207) and 15.14 (lines 2431-2432): `raceFormat` registered
with a golden folder "although it is not a save format". Found: r1-mode-and-setup.md:806-808
(a frozen folder is a save, so `v2` is a race save with the mark and local files),
r3-export-and-exchange.md:917-919 (one row and one folder, registered in R3E-04, not R1) and
test-and-harness.md:879-882 (a full `v2` with `persistent.sfs` and `formats.txt`; R3 writes it).
Check (code): `Source/Parsek.Tests/SaveFormatGoldenGateTests.cs:43` (`FindCoverageProblems`
over every format). Decided: R1-12 registers `race-link` and `race-exchange` and writes
`v2` (a race save with the mark, `race.cfg`, `player.cfg`, `local.cfg`), since `race.cfg`
exists from R1. R3E-04 registers nothing and bumps nothing; it adds `v3` carrying a standing,
its payload and an own mirror at the same format number (the gate allows a new folder without
a bump).

**DC-123 DETAIL.** Design 15.12 (line 2386): suit sets under `GameData/Parsek/Suits/`. Found:
r5-looks.md:630-632. The zip, the dev deploy and the harness deploy carry explicit lists
(`release.py:82-88`, `Parsek.csproj:112-117`, `provlib.py:257`); R5-12 changes all three.

**DC-124 WRONG, not checked (needs S6).** Design 15.14 (lines 2441-2442): the menu entry and the
dialog need their own UI-scale host. Found: r1-mode-and-setup.md:823-825. Built from stock uGUI
(a cloned button, `PopupDialog`) they follow the stock scale; a host is needed only for IMGUI
fallbacks. Not checked: the claim is about stock behavior under the UI-scale setting, which S6
shows. Edit after S6.

**DC-125 DETAIL.** Design 15.14 (lines 2405-2450). Found: test-and-harness.md:875-878. Missing
gates the first commits meet: the OnSave / OnLoad characterization goldens, the hlib per-verb
tables and their source-sync cells, the lane catalog regeneration, the registry growth rule,
the batch tally pins, `INTERIM_PIN_IDS` and the multi-category `GROUP`.

### Analysis document

**DC-126 DETAIL.** Analysis line 72 and design 15.6 (line 2144): "five copies" of the tree
lookup. Found: r0b-prep-playback.md:799-802. They are not copies (pending-first, committed-first,
tree-id match, flat-list fallback, list only); the port carries named variants. Design 15.6
already says "two different search orders"; "copies" should become "variants".

**DC-127 DETAIL.** Analysis section 3.1. Found: r0b-prep-playback.md:803-806.
`ParsekFlight.GhostPositioner.cs:323` is a watch-protection read, not a store lookup, and the
three pending-tree reads in the anchor context builders are missing.

**DC-128 DETAIL.** Analysis and design 15.6 (line 2133) / 15.11 P2b (line 2326): "about 60
references to 14 members". Found: r0b-prep-playback.md:827-831. Measured: 54 `host.`
references in the positioner, 52 sites in `ParsekFlight.cs` and `ParsekFlightPlaybackHost.cs`,
14 `GhostPosEntry` references; four of the 14 members stay on `ParsekFlight`.

**DC-129 DETAIL.** Analysis (readers of the KSC zone). Found: r0b-prep-small.md:795-797.
`IsWithinKscExclusionZone` has no production caller; the live reader is
`ClassifyKscExclusionZone`. Checked: the only callers are in `InGameTests/RuntimeTests.cs`.

**DC-130 DETAIL.** Analysis counts. Found: r0b-prep-small.md:802-804 and
r0b-prep-map-watch-codec.md:674-676. 99 test references to the KSC constants (not 64); 28
production calls of the id check plus about 25 in tests (not 32); `scripts/arch/modules.toml:12`
says Core has nine files, it has eight; `IsGhostMapVessel` has 164 uses in 77 files (not 120 in
61). No task changes.

**DC-131 DETAIL.** Analysis (the trajectory readers are "already list-based"). Found:
r0b-prep-map-watch-codec.md:698-704. `Read` also changes `Recording` caches and logs; and
`Inv10CodecRoundtrip` compares decoded lists, not bytes, so an analyzer pass proves decode
stability, not byte identity (P6-01's digest is the byte proof).

**DC-132 DETAIL.** Code comments. Found: r0b-prep-small.md:798-801.
`Bug278SnapshotPersistenceTests.cs:57` and `Bug280PersistFinalizedRecordingTests.cs:112` say an
id with spaces is refused; a space is valid on every OS. P7-01 fixes the comments.

### Outside the design: `.claude/CLAUDE.md`

**DC-133 DETAIL.** CLAUDE.md "Key source files": `GhostPlaybackEngine.cs` has "Zero `Recording`
references". Found: r0b-prep-playback.md:807-809 and r3-rival-world.md:960-963. Check (code):
`Source/Parsek/GhostPlaybackEngine.SectionPositioning.cs:355-372` reads `PendingTreeSlot`,
`RecordingStore.CommittedTrees` and `RecordingTree`. Recommended: P2a-02 rewrites the sentence
when it removes the reads.

**DC-134 DETAIL.** CLAUDE.md names `/workspace/ksp-refs`. Found: r0-spikes.md:964-965. This
session's references are at `/home/user/ksp-refs`.

## Part 2. Questions for the owner

Ordered by class: NOW, BEFORE-PHASE, AFTER-SPIKE, DESIGNER, ANSWERED. "Waits" names the tasks
that need the answer.

### NOW

**Q-01 Should the spikes run on their own copy of the game?** The spikes need a test copy of
KSP and a copy without the expansions, so your own game and the test copies stay as they are.
- Options: (a) yes, a separate copy (about one hour once); (b) run them in your own dev game.
- Recommended: (a).
- Source: r0-spikes.md:974-977. Waits: S0, and so every spike. Spike first: none. Class: NOW.

**Q-02 Keep the spike work on GitHub as a record?**
- Options: (a) push the spike branch, never merged and never opened as a pull request; (b)
  keep it on your PC only.
- Recommended: (a); the spike reports cite its commits.
- Source: r0-spikes.md:982-984. Waits: S0, S13. Spike first: none. Class: NOW.

**Q-03 Which cloud sync programs should the file-speed test cover?** The answer decides
whether copying race files needs its own background worker.
- Options: (a) OneDrive only; (b) OneDrive plus Dropbox or Google Drive, plus a network share
  that is offline; (c) every program you use.
- Recommended: (b); friends will use a mix, and the offline share is the worst case.
- Source: r0-spikes.md:969-973. Waits: S8a, S8b; then R3E-23 and R6-10. Spike first: this is
  the setup of S8. Class: NOW.

**Q-04 Land two small single-player fixes with the launch-pad rule?** Today a flight that ends
parked on the pad or runway is retired. (a) If its stored longitude has wound past the date
line, the rule misses it and the vessel appears on the pad. (b) If the expansion's launch sites
were read before they were built, approximate positions are kept until KSP restarts.
- Options: land both (each with a changelog line); land (a) only; land neither (the
  prerefactor stays strictly behavior-neutral).
- Recommended: land both.
- Source: r0b-prep-small.md:812-819; correction DC-115. Waits: P1-02, P1-04. Spike first: none
  (a log read on your PC settles the wording of P1-03). Class: NOW.

**Q-05 How long should your PC fly to prove the ghost-position change before the next step?**
The change touches where every ghost is drawn every frame.
- Options: (a) the full run, every lane on every install, about a day and a half; (b) the daily
  and nightly tiers (about 10 hours) plus the watch, loop and map picture lanes.
- Recommended: (a).
- Source: r0b-prep-playback.md:837-843. Waits: P2b-10, and every prerefactor after it. Spike
  first: none. Class: NOW.

**Q-06 May the trajectory file change land in parallel with the other prerefactors?** The
design puts it last because it touches saved files; in practice it writes nothing differently
and shares no file with them.
- Options: (a) build it in parallel and merge when green; (b) keep it last.
- Recommended: (a).
- Source: r0b-prep-map-watch-codec.md:712-718; correction DC-120. Waits: P6-01 to P6-05,
  R3W-04. Spike first: none. Class: NOW.

**Q-07 Build the groundwork for watching a rival's flight now, or later?** Without it a rival
flight is still seen on the map, in the Tracking Station and near you, but the Watch camera
cannot follow it.
- Options: (a) build it with the other prerefactors; (b) build it when watching rivals is
  scheduled.
- Recommended: (a) only if watching is in the first race build; otherwise (b).
- Source: r0b-prep-map-watch-codec.md:720-728. Waits: P3b-01 to P3b-04, R3W-26, the camera
  half of R4-11, R5-06. Spike first: none. Class: NOW.

### BEFORE-PHASE

**Q-08 When does the Multiplayer entry appear in the main menu?** After the first phase a race
save plays like single player: no rivals, no board.
- Options: (a) hidden behind a developer switch until rivals and the board exist; (b) visible
  from the first phase.
- Recommended: (a), so nobody creates a race that shows nothing yet.
- Source: r1-mode-and-setup.md:847-850. Waits: R1-14. Spike first: none.
  Class: BEFORE-PHASE R1.

**Q-09 Who chooses how long a race day is (6 hours Kerbin or 24 hours Earth)?**
- Options: (a) the creator's own game calendar setting, shown in the dialog; (b) a choice in
  the setup dialog.
- Recommended: (a); one less field, and the creator already chose it.
- Source: r1-mode-and-setup.md:855-858; correction DC-029. Waits: R1-10, R1-15, R4-01. Spike
  first: none. Class: BEFORE-PHASE R1 (the choice is written into the race file at creation).

**Q-10 What happens when a race save's space center cannot be set up?** For example Kerbin was
changed by a planet mod after the race started. The player's vessels would stand on unflattened
ground and the game may destroy them.
- Options: (a) stop the load, return to the main menu with one message, save untouched; (b) load
  on stock Kerbin with one warning.
- Recommended: (a), because (b) can destroy vessels and the save would be written.
- Source: r2-sites.md:800-806. Waits: R2-01, R2-02, R2-04. Spike first: S4 helps (what can
  fail). Class: BEFORE-PHASE R2. The design covers only a refusal at race start (8 case 24,
  lines 1653-1655).

**Q-11 A player turns stock's "allow other launch sites" on in a race created without them.**
- Options: (a) the race setting wins: launches from those sites stay refused and the option is
  greyed; (b) allow it and only disclose it next to the player's name.
- Recommended: (a); the creator's choice decides how far every player travels.
- Source: r2-sites.md:807-812; correction DC-038. Waits: R2-08, R2-14. Spike first: none.
  Class: BEFORE-PHASE R2.

**Q-12 The same player plays one race on two computers at once.** Each game puts back the files
the other wrote, so they overwrite each other forever.
- Options: (a) keep putting files back every time; (b) put back twice, then stop and say on
  screen that the race folder is being written from two computers.
- Recommended: (b).
- Source: r3-export-and-exchange.md:925-929; correction DC-084. Waits: R3E-20. Spike first:
  none. Class: BEFORE-PHASE R3.

**Q-13 How exactly must the two saves of the "rivals change nothing" test match?** Two flown
sessions never land on exactly the same time values.
- Options: (a) a comparison where only listed time values may differ, each with a written
  reason; (b) byte-identical saves, which forces a session where no time passes and so cannot
  see a rival flag or landing.
- Recommended: (a).
- Source: test-and-harness.md:908-911; correction DC-025. Waits: T-05, T-27. Spike
  first: none. Class: BEFORE-PHASE R3.

**Q-14 Should the game announce a first you take that no rival had reached?** ("Mun Landing is
yours: day 31".)
- Options: (a) only when a rival is involved (you beat, lost to or tied someone); (b) every
  first you take.
- Recommended: (a); the opening hour would otherwise post a message for every new first.
- Source: r4-r6-board-and-rules.md:869-873; correction DC-066. Waits: R4-07. Spike first: none.
  Class: BEFORE-PHASE R4.

**Q-15 With "no retakes" on: Bogdan's earlier Mun landing itself does not count (he had seen
yours first). Does Ana, who saw Bogdan's landing, still count as having seen it when she flies
an even earlier one?**
- Options: (a) yes, any real flight you had seen blocks you, whether or not it counts itself;
  (b) no, only a date that counts can block.
- Recommended: (a); it never depends on a chain of other players' receipts and cannot loop on
  edited files.
- Source: r4-r6-board-and-rules.md:862-868; correction DC-072. Waits: R6-02, R6-09, R6-11.
  Spike first: none. Class: BEFORE-PHASE R6.

**Q-16 After the end date, do changes on the "now" board still send messages?**
- Options: (a) no: only a change to the final board (a late file) and the one "race is over"
  message; (b) yes, as before the end.
- Recommended: (a).
- Source: r4-r6-board-and-rules.md:874-877; correction DC-066. Waits: R6-08. Spike first: none.
  Class: BEFORE-PHASE R6.

### AFTER-SPIKE

**Q-17 If the stock difficulty panel cannot be opened from the Multiplayer dialog, what does the
creator get?**
- Options: (a) the four stock presets (Easy, Normal, Moderate, Hard) only, custom settings
  later; (b) wait for a custom panel before Create ships.
- Recommended: (a); the race file stores the full settings either way.
- Source: r1-mode-and-setup.md:851-854. Waits: R1-15. Spike first: S6. Class: AFTER-SPIKE S6.

**Q-18 May the race copy files on one background worker?** Shared folders on cloud drives can
freeze the game for seconds when a file is opened the first time. The worker would be the one
exception to the "no threads" rule.
- Options: (a) allow one worker that only copies and checks files; (b) keep everything on the
  main thread, with copying paused during flights and a possible freeze in the Space Center.
- Recommended: (a) if the file-speed test shows freezes longer than one second on any common
  sync program; otherwise (b). The plan builds (b) first either way.
- Source: r3-export-and-exchange.md:930-935; design 15.9 (lines 2239-2243) and 13.B (line 1849)
  ask for this yes. Waits: R3E-23, R6-10. Spike first: S8. Class: AFTER-SPIKE S8.

**Q-19 Should the two replacement suit sets appear in the stock suit picker?**
- Options: (a) yes: every save, single player too, lists two extra suits in the Astronaut
  Complex; (b) no: they are applied only to race kerbals and rival ghosts, and single-player
  saves see nothing new.
- Recommended: (b), if the suit spike shows the applied texture in EVA, IVA and the portrait;
  else (a).
- Source: r5-looks.md:642-647; correction DC-047. Waits: R5-11, R5-12. Spike first: S3 (S3b).
  Class: AFTER-SPIKE S3. Note: (a) puts a race item into every single-player save, which design
  0 (line 113) and 15.8 rule out unless you allow it.

**Q-20 Where does the replacement suit art come from?** The expansions' textures cannot be
shipped.
- Options: (a) original stock-like art drawn for Parsek (needs an artist, ships files); (b)
  Parsek recolors the player's own base suit in memory at load (nothing shipped).
- Recommended: (b) first; (a) only if it looks wrong.
- Source: r5-looks.md:648-652. Waits: R5-08, R5-12. Spike first: S3 (S3b).
  Class: AFTER-SPIKE S3.

**Q-21 If the stock plaque window cannot show a rival's text, how does a player read a rival's
plaque?**
- Options: (a) a small Parsek dialog with the plaque text, opened by the same click (a new
  window, which needs a ruling like the Route History one); (b) the flag stands but cannot be
  read; (c) no rival flags in the first build.
- Recommended: (a), as one stock-styled popup.
- Source: r3-rival-world.md:980-984. Waits: R3W-25 (and R3W-24). Spike first: S7.
  Class: AFTER-SPIKE S7. Note: your ruling that rival flags are readable (design 0, line 67)
  already rules out (b) and (c), and the UI exception (lines 112-119) does not list (a); so
  the real question is whether (a) may be a fourth exception.

### DESIGNER

**Q-22 Do rival ghosts show their owner's name?** Two surfaces: the floating label beside a
rival ghost in flight ("Carol: Kerbal X"; today only chain ghosts have one), and the name on
the map and in the Tracking Station (`Ghost: Ana - Kerbal X` or `Ghost: Kerbal X`).
- Options: (a) yes on both, inside the existing label range; (b) color only.
- Recommended: (a) by both plans; colors are hard to tell apart for some players, and the
  Tracking Station list has no color.
- Source: r3-rival-world.md:990-992 and r5-looks.md:653-656 (merged). Waits: R3W-15, R3W-16,
  R5-04. Spike first: none. Class: DESIGNER; assume (a). It uses existing surfaces and follows
  your ruling that each player has a name and a color (design 0, lines 59-61).

**Q-23 Does a rival ghost flying inside an atmosphere show a marker in the flight map view?**
Own ghosts get a Parsek-drawn marker there; the design gives rivals none in the Tracking Station
and says nothing about flight.
- Options: (a) nothing, as in the Tracking Station; (b) the own-ghost marker, tinted later.
- Recommended: (a) in R3, revisit with the looks phase.
- Source: r3-rival-world.md:985-989. Waits: R5-05 (conditional), R3W-18. Spike first: none.
  Class: DESIGNER; assume (a), which matches design 15.6 (line 2138).

**Q-24 Do the test races the automated flights play score anything?** Every race the harness
plays is marked by the test tooling, so its own flights never score.
- Options: (a) keep it so, and prove scoring with generated rival folders (no product change);
  (b) a "test race" flag only the test tooling can set, under which marks show but do not zero
  the score.
- Recommended: (a).
- Source: test-and-harness.md:903-907; correction DC-080. Waits: T-28, T-32, R6-01. Spike
  first: none. Class: DESIGNER; assume (a), which keeps your ruling that a flight flown with a
  debug tool does not score (design 0, lines 102-103).

**Q-25 Test two game copies racing on one PC at the same moment?**
- Options: (a) an optional operator lane, once parallel flights exist; (b) leave it to the
  two-PC play test.
- Recommended: (a); the only proof of two live writers short of a two-PC test.
- Source: test-and-harness.md:912-915. Waits: T-34. Spike first: none. Class: DESIGNER; assume
  (a), built only when parallel flights exist.

### ANSWERED

**Q-26 A milestone completed with the debug menu (Max Progression, the console):** should the
race (a) drop those completions from the board and mark the player as having used debug tools,
(b) only mark the player, or (c) ignore it?
- Recommended by the plan: (a); otherwise one debug click takes every world first.
- Source: r0-spikes.md:978-981; correction DC-078. Waits: R3E-09, R6-01. Spike first: S9
  (which of these completions a mod can see).
- Class: ANSWERED by design 0 (lines 102-103) and 13.A D3 (line 1825): a debug tool means no
  score, so only (a) fits. What remains is S9's finding; a completion S9 cannot observe is
  listed as out of reach (7.12, line 1393).
