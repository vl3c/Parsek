# Architecture opportunities, 2026-09-14

Phase 4 of the architecture program: the ranked list of structural changes the
three signals justify, with the numbers behind each. The signals are the
reference map (`scripts/arch/archview.py`, text scan, module and type level),
the knot dissection (strongly connected components and greedy sink cuts), and
the change history (git co-change over `Source/Parsek`, 5127 commits since
2025-03-01). Regenerate the evidence with
`python scripts/arch/archview.py --check --place`; the numbers below are the
2026-09-14 reading of the `arch-view` branch.

This is a list of candidates with evidence, not a plan. Each item says what
the numbers show, what a change would buy, and what it would cost. Nothing
here has been started; the code under `Source/Parsek` is untouched by the
program that produced this document.

## How to read the ranking

Three questions per candidate, each answered by a different signal:

- Is it coupled? The reference map: fan-in, upward edges, two-way pairs.
- Is it locked? The knot: whether the types sit in the 391-member cycle and
  whether a cut frees them.
- Does it hurt? The history: commits touching the file, and which files in
  OTHER modules change with it.

A candidate that scores on all three is worth doing. One that scores on
references alone is a diagram problem, not a maintenance problem, and sits at
the bottom.

## The ranked list

### 1. ParsekFlight.cs is the change hub of the whole mod

Evidence:

- 27,591 lines in one file (plus four partial files under 900 lines each).
- Touched in 1,125 of 5,127 commits: 22 percent of all commits to the source
  tree change this file. The next file is `ParsekScenario.cs` at 420.
- Every one of the top cross-module co-change pairs has it on one side:
  `FlightRecorder.cs` (147 commits together), `RecordingStore.cs` (128),
  `GhostPlaybackEngine.cs` (106), `BackgroundRecorder.cs` (90),
  `ParsekUI.cs` (87), `GhostPlaybackLogic.cs` (79), `GhostVisualBuilder.cs`
  (75), `VesselSpawner.cs` (63), `Recording.cs` (60), `GhostMapPresence.cs`
  (47), `MergeDialog.cs` (46).
- In the knot it references 95 or more other knot members; making it a sink is
  the twelfth greedy cut and frees only 9 types, because everything it touches
  is also tangled with everything else.
- The two module pairs with the highest co-change ratio, Controllers with
  Recording (jaccard 0.20) and Controllers with Ghost (0.20), are this file
  changing alongside the recorder and the playback engine.

What it means: the flight-scene controller is where recording, playback,
spawning, chain management, terminal events and the merge flow all meet, and
the history says they are edited together, not just referenced together. A
change to the recorder is a change to ParsekFlight more often than not.

What a change would buy: the co-change pairs name the seams. Recorder hookup
(`FlightRecorder`, `BackgroundRecorder`), playback hosting
(`GhostPlaybackEngine`, `GhostPlaybackLogic`, `GhostVisualBuilder`,
`GhostMapPresence`), spawning (`VesselSpawner`) and the merge flow
(`MergeDialog`) are four candidate extractions, each already partly delegated
(`WatchModeController`, `ParsekPlaybackPolicy`, `ChainSegmentManager` are
prior extractions of the same kind). Each one removes a co-change pair from the
top of the list.

Cost and risk: highest of any item. The file carries the flight-scene
lifecycle order, and the harness flights are the only proof that an extraction
kept it. Do this last among the code changes, after the cheaper cuts below have
made the recorder and playback types easier to reason about, and one seam per
PR with a harness tier behind it.

### 2. Make ParsekLog a pure sink

Evidence:

- Fan-in 295 files, the most-referenced type in the tree.
- Inside the knot because of two references: `ParsekSettings` (the verbose
  flag) and `RecorderStateSnapshot`. Cutting them is the first greedy cut and
  drops the knot from 391 to 335.
- Touched in only 24 commits: it is stable, so the change is cheap and the
  regression surface is small.

What a change would buy: 56 types leave the knot. The logger becomes what the
docs already treat it as, a leaf. `ParsekSettings` stops being reachable from
the bottom of the stack.

How: settings pushes the verbose flag into the logger (a static bool or a
delegate set at settings load) instead of the logger reading settings; the
snapshot reference moves to whichever caller needs it. Afternoon-sized.

### 3. Keep Recording as data: cut its calls into the store and the ledger

Evidence:

- `Recording` is the top hotspot: fan-in 162 files, touched in 132 commits,
  hotspot score 21,384 (next is `LedgerOrchestrator` at 7,488).
- Inside the knot because it references `RecordingStore` and `KerbalsModule`.
  Cutting them is the second greedy cut: 335 to 281.
- `Recording.cs` co-changes with `ParsekFlight.cs` 60 times and with
  `ParsekScenario.cs` 48 times.

What a change would buy: 54 more types leave the knot, and the data type that
everything depends on stops depending on the store that holds it. Combined
with item 2 that is 110 types out of the cycle for two small changes.

How: the store lookups and the kerbals-module call move to the callers or to
`RecordingStore` itself. Any behaviour on `Recording` that needs the store is
a store method in disguise.

### 4. RecordingStore is a god object

Evidence:

- 6,970 lines plus partials; fan-in 91 files; touched in 408 commits (third
  most churned file); its nested `PendingTreeState` is the seventh hotspot on
  churn alone.
- Inside the knot it references 37 other members: stores, codecs, the
  scenario, the flight controller, the ledger orchestrator, the crew manager.
  Making it a sink is the third greedy cut (281 to 253) and the first one that
  names a design problem rather than a stray reference.
- Co-changes with `ParsekScenario.cs` 131 times and `ParsekFlight.cs` 128.

What it means: the store holds the committed list, but it also drives sidecar
I/O, the optimizer, orphan cleanup, tree discard, group hierarchy, the ledger
recalculation trigger and switch-segment classification. The
`CommittedListNotifications` partial documents one contract; the rest is
implicit.

What a change would buy: the store as a list with notifications, and the
operations that use it (optimizer, purge, sidecar commit, session merge) as
separate services on top. The co-change with the scenario module would drop
because save and load would talk to the services, not the store.

Cost: medium to high. Second among the code changes after items 2 and 3.

### 5. Recording and Rewind are one subsystem on paper

Evidence:

- Rewind references Recording 98 times and Recording references Rewind 28
  (`EffectiveState`, `MergeState`, `ChildSlot`, the merge journal).
- Co-change 136 commits, jaccard 0.09: real but not dominant.
- `EffectiveState` (fan-in 43, 52 commits) is the eighth greedy cut.

What it means: the recording model already knows about supersedes and merge
states, so the boundary between "a recording" and "which recordings count" is
drawn inside the model rather than above it.

Options: accept the boundary as documentation only (cheapest, and the history
says they do not co-change enough to force a merge), or move `MergeState` and
`ChildSlot` into Recording as plain data and keep only the resolution logic in
Rewind. Decide after items 2 to 4 land, since those change what Recording
references.

### 6. Trajectory and Recording reference each other

Evidence:

- Recording references Trajectory 65 times and Trajectory references Recording
  51. Instabilities 0.19 and 0.21, so the map calls it a near tie rather than
  an inversion.
- The types behind the upward edge are `BallisticExtrapolator`, `OrbitReseed`
  and the incomplete-ballistic scene-exit finalizer: Trajectory code that reads
  recording types.

What a change would buy: pure math that references no model. The
extrapolator's Recording-aware half moves to Recording; the numeric half stays.
Low risk, medium size, mostly file moves. Worth doing because Trajectory is
the one module the docs promise is pure.

### 7. VesselSpawner is a controller helper filed as kernel

Evidence:

- Kept in Core by the placement rules because no module owns a majority of its
  references, but it co-changes with `ParsekFlight.cs` 63 times, has 6,897
  lines, and was touched in 163 commits.
- It is the reason Core reaches up into Recording (21 references, the only
  upward edge out of the kernel).

What a change would buy: a kernel that is really eight small vocabulary files.
Move it to Controllers, or split it into the pure snapshot and manifest half
(kernel) and the live spawn and recover half (Controllers). Small to medium.

### 8. Missions and Reaim: cut, do not merge

Evidence:

- 21 references each way, perfectly symmetric.
- Co-change 30 commits, jaccard 0.15: they are edited together in a minority
  of their commits, so the history does not say they are one module.

What it means: the reference cycle is structural, not a sign of a missing
merge. An interface on the Reaim side (arrival hold, descent decisions,
destination constraints as inputs Missions produces) breaks it without moving
files. Low priority; no maintenance pain is visible.

### 9. The Missions to Logistics boundary is crossed in one place

Evidence:

- Two references (`Route`, `RouteStop`) from `MissionRouteStructureList.cs`,
  the declared boundary the checker reports.
- Ten commits touched both sides in the window.

What a change would buy: the declared rule becomes true. Either the route
structure list moves to Logistics (it is a read model over routes) or it reads
through an interface Missions owns. One PR, one file.

### 10. MapRender reaches up into Ghost

Evidence:

- 28 references (`GhostMapPresence`, `LoopUnit`, `LoopCut`, `GhostPlaybackLogic`),
  the second heaviest upward edge.
- Co-change with Ghost is low; with Display it is the third module pair by
  jaccard (0.16), which is expected since Display is the polyline renderer.

What it means: the map renderer asks playback what is present. An interface
would make the direction explicit, but no maintenance signal argues for it
now. Bottom of the list.

## What is not on the list, and why

- The 391-type knot as a whole. After items 2, 3 and 4 the residual is about
  250 types, and the greedy cuts from there name `GameAction`, `Route`,
  `GhostPlaybackLogic`, `LoopUnit`, `RouteOrchestrator` and `ParsekUI` in
  turn. Each is a design decision about a feature, not a wiring fix, and none
  shows up in the history as a pain point. Re-run the dissection after the
  first three cuts and rank again.
- Module boundaries in general. The highest co-change ratio between any two
  modules is 0.20. No declared separation is contradicted by the history; the
  modules are real. The problem is concentrated in three files, not spread
  across the map.
- The text scan's blind spots. Reflection, string-keyed lookups and generic
  inference are invisible to it. Phase 5 (a Roslyn extractor in
  `Parsek.Tests`) replaces the edges and keeps every view and every number
  above comparable.

## Suggested order, if the list is accepted

1. Item 2 (ParsekLog) and item 3 (Recording): small, independent, together
   free 110 types from the knot, and the second one de-risks every later item
   by making the hottest type a leaf.
2. Item 9 (Missions to Logistics) and item 7 (VesselSpawner): one-file moves
   that make two declared facts true.
3. Item 6 (Trajectory purity): file moves, no behaviour change.
4. Item 4 (RecordingStore): the first change with real design content.
5. Item 1 (ParsekFlight seams): one seam per PR, each behind a harness tier,
   starting with the recorder hookup because it is the top co-change pair.
6. Re-run `--check --place` after each step and compare the knot size, the
   upward-edge count and the co-change pairs against this document.

Items 5, 8 and 10 wait for the re-ranking.
