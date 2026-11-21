# Mission Profile foundation notes (2026-11-21)

Status: research notes for roadmap Phase 17 (Mission Profile). Not built. Collected during the recording split / trim /
merge fix batch (PRs #2718-#2730, owner task "recording-split-review"), from two independent Fable reviews: the
phase-model review of 2026-11-20 and the whole-batch review of 2026-11-21. The owner decided (2026-11-21) not to build
any of this in that task: "we will not work on Mission Profiles feature in this task, focus on finishing the optimizer
here so we can close this". Read this before designing Phase 17 or touching the optimizer's row roles.

## 1. What the batch built that Phase 17 can use

- Split rule versions (`Recording.SplitRuleVersion`, key `splitRuleVersion`; absent = 1): a recording keeps the rules it
  was committed under (owner ruling Q5). Version 2 = atmosphere-pass rules; version 3 (step 2, in progress at the time of
  writing) = cuts where a burn changes the orbit kind. Design: `docs/dev/done/plans/optimizer-persistence-split.md`
  section 14.
- Row roles (`Recording.SegmentRole`, key `segmentRole`): ascent, space, aerobraking, reentry, approach, surface,
  atmosphere (v2), plus the v3 roles for circularization / orbit / transfer / capture / deorbit / descent. Set on both
  halves of a cut; `CanAutoMerge` refuses different roles and joins the current rules would cut again.
- `BranchPoint.ParentContinues` (key `parentContinues`): the parent recording runs on past a breakup / decouple branch
  point (focused and, since #2729, background separations share this shape).
- `BranchPoint.SplitCause` = UNDOCK and `DecouplerPartId` on focused undocks (#2720).
- The owner's row model (rulings 2026-11-20): rows read like mission-profile elements and each row loops sensibly on its
  own; ascent ends at the atmosphere exit, then a circularization row; a burn that arrives in orbit ends the row before
  it, a burn that leaves an orbit opens the next row; deorbit row on every body; reentry from the first atmosphere touch;
  aerobraking dips after the first touch stay in one row; aerocapture = arrival / capture; an SOI change is never a cut.

## 2. Why rows alone cannot be the profile elements

Verified in code by the 2026-11-21 review:
- Some recordings are never cut or carry no role: supply-route sources (held), recordings with a loop range, superseded
  and held recordings, version-1 recordings, EVA kerbals, debris (no kind cuts).
- Rendezvous and docking have no row of their own.
- The capture burn sits inside the transfer row (section 7 case table), so "capture" as a profile event is not a row
  boundary.
- Today the phase classification lives inside the split predicate, one recording at a time, and uses the role as INPUT
  state (`RecordingOptimizer.SplitRules.cs`, `rowHasPassRole`).

So Phase 17 must classify a whole vessel LINE from sections, branch points and part events; row cuts are a subset of
element boundaries.

## 3. Recommended foundation (from the review; not built)

- F1 One pure analyzer that returns the whole plan for a recording / vessel line: ordered profile elements, the cut
  list, the role per piece, by rule version. The optimizer split pass applies its cut list, the merge guard asks it,
  Phase 0-style measurements print it, Phase 17 reads it at full detail. Roles become outputs only, never input state.
- F2 One vessel-line walker. Three "same vessel continues" mechanisms exist: the recording running past a
  ParentContinues branch point, chain pieces (ChainId / ChainIndex), and same-pid children / switch continuations /
  Re-Fly supersede forks. Build one ordered line on `VesselIdentity` / `EffectiveState` and make `ReaimClassifier` use
  it too (todo REAIM-CLASSIFIER-FRAGILE-TO-MEMBER-SPLITS, which needs parking + coast + arrival in one member today).
- F3 Recorder stamps with ONE trajectory sidecar format bump. The binary section layout is fixed
  (`Recording/TrajectorySidecarBinary.cs`), so a new per-section field is a SHAPE change (format bump + upgrade step +
  new `FormatGolden` folder per the save-format policy). Do one bump that adds an extensible per-section extension block,
  then: raw osculating Pe / Ap / inc / ecc and the stock patch-end transition (encounter / escape) at space-section open
  and close; delta-v expended and mass per powered section. Burns with delta-v and orbit before / after are not derivable
  from today's data: no mass, the recorded point velocity frame is unreliable near the planet (surface-relative below the
  inverse-rotation threshold, which is above Kerbin's atmosphere), and exact elements exist only on on-rails checkpoint
  sections.
- F4 One role registry: role -> phase class, loopable, label, profile element, pass-state flag. Roles made total
  (stamped at commit, inherited along the line by continuations); keep `space` as "unclassified space". Loopability and
  labels read the table (today `Recording.IsLoopableRecording` keys on `SegmentPhase`, so an aerobraking row is "atmo"
  and a circularization row is "exo").
- F5 Terminal state: `SubOrbital` conflates flying, falling and escaping; the escape-spawn test (#2725,
  `EscapeTrajectoryTerminal`) needs the snapshot's situation. Reuse the F3 orbit stamp at the recording end.
- F6 Can wait: null SplitCause on old focused decouples, ParentContinues absent on pre-#2729 branch points (keep the
  inference), stage numbers, first-class burn events (derive from engine part events), aerobraking vs aerocapture naming.

## 4. Orbit-kind model notes (phase-model review, 2026-11-20)

- Use stock's own situations so rows match what the player sees: falling = periapsis below the atmosphere top (or below
  0 on an airless body) and not yet passed, open or closed (direct descent is hyperbolic); in orbit = closed, periapsis
  above that, apoapsis inside the SOI; leaving = everything else. Do not use the terrain-height floor.
- Judge a burn's kind change only inside the body where the burn ends (a burn straddling an SOI line); never cut at the
  SOI line itself.
- "Leaving" for a Mun / Minmus transfer is an encounter, not a conic property; prefer a stamped patch-end transition over
  looking ahead at what the recording did next (lookahead changes rows when a vessel is flown again).
- The departure burn is the last burn before the SOI change that materially changed the orbit; reuse ReaimClassifier's
  correction threshold for "materially".
- Merge engine-off gaps up to ~10 s into one burn (staging); a burn broken by on-rails time is two burns; drop a kind cut
  that would leave a row shorter than ~30-60 s next to another cut; a recording that starts / ends mid-burn uses the kind
  at its first / last sample, unknown means no cut.
- Biggest risk to any burn cut: interplanetary re-aim (one extra split was measured to turn a Kerbin-Dres transfer from
  re-aimed to faithful; `docs/dev/done/plans/optimizer-split-transfer-cohesion.md`). Measure over the recorded fixtures
  before shipping cuts on transfer seams.

## 5. Where the full texts are

The working files of that task live outside git, in the umbrella folder
`docs/handoff/recording-split-review/` (`atmosphere-pass-analysis.md` sections 6-8 with the case table and the owner's
answers, `fable-whole-batch-review.md`, `q15-design.md`, `STATUS.md` with every owner ruling). This note copies the parts
Phase 17 needs, so it stands on its own if those files are gone.
