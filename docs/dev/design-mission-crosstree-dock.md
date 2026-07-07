# Design Note: Cross-Tree Foreign Dock Missions (M-MIS-8)

Status: DECIDED 2026-07-07 (this note precedes the build, per the M-MIS-8 requirement that
the persistence shape is decided before building).

Closes missions design gap 14.3 / docking-decision gap 4: when vessels A and B are
INDEPENDENT trees and they dock, the combined leg and the post-undock continuation land in
the controller's tree (TA) while the foreign partner's pre-dock flight stays in its own tree
(TB), so "loop the whole shared docked journey from the partner's side" spans two trees and
is not expressible as a single Mission selection today. Playback already follows the
cross-tree dock link via PID matching (`GhostChainWalker.MergeCrossTreeLinks`); this is a
Missions-view + selection gap, not a data gap. No recorder or recording-schema change.

## 1. What the recorder already gives us

A dock to a foreign vessel records, in the CONTROLLER's tree TA, a SINGLE-parent
`BranchPointType.Dock` (or `Board`) branch point whose `TargetVesselPersistentId` is the
partner's persistentId (`HandleTreeDockMerge` persists the couple-event partner pid exactly
for the cross-tree case), and whose single child is the combined-stack recording. A later
undock is a structural fork in TA whose departing child recording carries the partner's
`VesselPersistentId`. The partner's own pre-dock flight is tree TB, whose recordings carry
the same pid (+ `RecordedVesselGuid` for launch identity, since pids are craft-baked and not
launch-unique).

So the cross-tree link is fully DERIVABLE at read time: foreign-tree Dock/Board branch point
with `TargetVesselPersistentId != 0` matching a recording pid in my tree, guid-gated via
`VesselLaunchIdentity.GuidsConclusivelyDiffer` - the same claim rule `GhostChainWalker.
ScanBranchPointClaims` + `MergeCrossTreeLinks` use. Nothing new needs to be recorded.

## 2. Persistence model (the decision)

**Chosen: single-tree Mission + persisted INCLUDED DOCK-LINK ids.** `Mission` stays
single-tree (`TreeId` unchanged). It gains one sparse field:

```
IncludedForeignDockLinkIds: HashSet<string>   // BranchPoint.Id of each INCLUDED foreign
                                              // Dock/Board claim on this tree's vessel(s)
```

A link id is the claiming branch point's GUID in the FOREIGN (controller's) tree. Everything
else about the link - which tree it lives in, the dock UT, which of my recordings it claims,
which foreign legs form the partner journey - is DERIVED live by the same PID+guid matching
playback uses. Empty set = today's behavior exactly; the codec writes the key only when the
set is non-empty, so every pre-existing mission round-trips byte-identically (pinned by
tests). Linked missions write the key SORTED (HashSet enumeration order is
nondeterministic; unsorted writes would churn save bytes with no logical change).

Rejected alternatives:

- **Multi-tree Mission (persisted second TreeId).** Duplicates topology the dock link
  already encodes and can go stale independently of it (tree deletes, re-fly supersedes);
  the pid-match derivation must exist anyway to discover the affordance, so persisting the
  tree id buys nothing. It also entangles `MissionStore` lifecycle (prune, defaults,
  original-mission rules) and `MissionGroupLink` (name <-> root group sync) with a second
  tree for no expressive gain.
- **Linked-mission pairing (two Missions joined by a link id).** Doubles the lifecycle
  surface (clone/delete/prune/normalize must handle pairs), the partner-side mission alone
  still cannot express the combined selection (the docked stretch belongs to neither
  single-tree selection), and one-loop-per-tree becomes one-loop-per-pair anyway. Strictly
  more persistence and coordination.

The chosen shape follows the todo entry's lean: reuse the `GhostChainWalker` link derivation
rather than persisting new topology. The branch-point GUID pins the exact dock event (stable
across optimizer churn - branch points are never renumbered), and staleness is handled by
the existing reconcile pattern (drop + warn).

## 3. Selection composition across the seam

New pure static class `MissionCrossTreeDock` (walker-parity derivation, headless-testable):

- `FindLinks(myTree, allTrees)` -> `ForeignDockLink { LinkId, ForeignTreeId, DockUT,
  PartnerPid, PartnerLaunchGuid, ClaimedRecordingId, MergedChildRecordingId, ForeignVesselName }`.
  Scan every OTHER committed tree's BranchPoints for Dock/Board with a non-zero
  `TargetVesselPersistentId` that matches a recording in MY tree by pid, guid-gated
  (unknown guid falls back to pid-only, walker semantics).
- `ComputePartnerJourneyLegIds(foreignTree, link)` -> the ordered leg ids in the
  foreign tree that carry my vessel: start at the merged child, walk the continuation
  (sequence next / branch-continuation child); at each fork PREFER the child whose
  `VesselPersistentId` matches the partner pid (guid-gated) - that is the partner departing
  (or, when the partner survived as the merged stack, the continuing stack itself); else
  follow the continuation child while the partner is still aboard. Stop at line end.
  Cycle-guarded. This yields: docked stretch legs + the partner's post-undock offshoot legs.
  Heuristic honesty: at a split where neither child carries the partner pid, the walk
  follows the continuation child; an exotic split that moves the partner onto a
  non-continuation, non-pid-matching child would mis-follow - logged, acceptable v1
  (part-level membership tracking is out of scope).

**Interval selection**: the partner journey's selectable intervals come from the FOREIGN
tree's own composition (`MissionCompositionBuilder` over its structure), filtered to nodes
whose interval lies inside a journey WINDOW. Windows are derived PER CONTIGUOUS RUN of
journey legs along each foreign through-line (review fix): a partner that undocks and later
RE-DOCKS the same line contributes two disjoint docked stretches, and a single [min,max]
window would wrongly offer the foreign vessel's solo stretch between them as partner
journey. Claims never match DEBRIS recordings (craft-baked pid collisions on guid-less
debris would otherwise mint false affordances). Interval keys are recording-GUID-rooted
(`<headLegId>`, `<head>/segN`, `<parent>@dockM`), hence globally unique across trees, so
exclusions across the seam live in the SAME `Mission.ExcludedIntervalKeys` set - no second
exclusion namespace.

**Missions window rendering**: for each mission whose tree has at least one derivable link,
render one affordance row per link ("Partner journey - <foreign vessel> (docked <time>)")
with an include toggle bound to `IncludedForeignDockLinkIds` (the explicit player action
that engages the cross-tree path; default OFF). When included, the journey's intervals
render as indented child rows with normal per-interval checkboxes (same
`ExcludedIntervalKeys` binding + the `SelectionSchemaGeneration` stamp on edit, mirroring
the existing handler).

## 4. Loop-unit consequence

`MissionLoopUnitBuilder.TryBuildMissionUnit`, after the own-tree
`ComputeTrimmedMemberWindows`: when `IncludedForeignDockLinkIds` is non-empty, resolve each
link, compute the foreign journey member windows (journey legs -> committed indices, trimmed
by the included-interval windows, first-wins on duplicate indices), and merge them into the
member set. Span = [min trimmed start, max trimmed end] over ALL members - one shared span
clock; members from both trees shift together by the same loop shift, so the recorded dock
alignment is preserved by construction. The `LoopUnit` index contract is already global
(positional indices into `RecordingStore.CommittedRecordings`), so flight / KSC / Tracking
Station consume the cross-tree unit unchanged.

**Periodicity / phasing: FAIL CLOSED.** Launch-identity audit: `BuildPhasingKnobInput`
gathers self-line segments via `VesselLaunchIdentity.RecordingsShareLaunch` against the
OWNER - foreign members are excluded naturally, but `MissionPeriodicity.ExtractConstraints`
and the re-aim classifier only see the mission's OWN tree view, so a phase-locked anchor or
a re-aimed transfer would be derived from half the members (two trees = two launches, two
pads, two constraint sets). v1 therefore SKIPS the whole `bodyInfo` block (phase-lock,
zero-drift schedule, phasing knob, re-aim, arrival hold) whenever at least one foreign
member joined the unit, with a logged reason (`Info`, signature-gated builds):
`"cross-tree members joined; periodicity/re-aim fail closed to faithful"`. The unit keeps
the faithful base anchor + raw cadences. The deliverable is the SELECTION + RENDER
capability; phase-lock across trees is future work.

**One-loop-per-tree** generalizes to SPANNED tree sets (own tree + linked foreign trees),
enforced at three sites (review fix closed the gaps): `MissionStore.SetLoopEnabled(trees)`
on enable, `NormalizeOneLoopPerTree(trees)` after load (first in list order wins), and the
Missions-window link toggle (including a link on an ALREADY-LOOPING mission widens its span,
so it calls `ClearLoopsConflictingWith` immediately). Builder-side owner/member collision
handling (first claimant wins + warn) stays as the final safety net.

**BuildSignature**: folds in each looping mission's sorted `IncludedForeignDockLinkIds` and
each linked foreign tree's BranchPoints/Recordings counts, so link edits and foreign-tree
topology changes rebuild the cached unit.

## 5. Store lifecycle

- `PruneOrphans`: unchanged (own tree governs the mission's existence).
- `ReconcileSelections`: (a) drop link ids that no longer derive (foreign tree gone, branch
  point gone, claim no longer matches) - warn, same pattern as stale head/interval ids;
  (b) the valid-interval-key set for a mission = own tree's selectable keys UNION each
  surviving included link's journey selectable keys, so cross-seam exclusions are not
  wrongly dropped as stale; (c) while any PARKED tree (quickload-resume Limbo node restored
  later in the same OnLoad - the population PruneOrphans already protects via
  additionalLiveTreeIds) is uncommitted, the link drop AND the linked missions'
  interval-key stale-drop are DEFERRED wholesale (review fix: the parked tree may be the
  link's foreign tree, and a stale link id does not name its tree, so dropping early would
  permanently lose the player's selection - the Limbo data-loss class).
- `Clone`: copies `IncludedForeignDockLinkIds` (definition-only copy, unchanged contract).
- `CanDelete` / `Delete` / `FindOriginalMission` / `EnsureDefaultsForTrees` /
  `MissionGroupLink`: unchanged. The mission name syncs to its OWN tree's root group only;
  the partner tree's group is untouched.

## 6. Rendering / playback notes

- Between the partner tree's recorded end and the dock UT the partner is unrecorded
  (loitering): its ghost retires at its recorded end and the combined ghost appears at the
  dock UT. Chronological gaps are an accepted contract ("contiguity is causal /
  chronological with UT gaps allowed"); no interpolation is invented.
- Non-looping playback is untouched: mission selections only shape loop units and the
  Missions view; committed recordings render as today.
- Watch mode / KSC / TS consume the unit through the existing index contract; no
  scene-specific work.

## 7. Out of scope (stated)

- **Logistics route derivation across the pair**: `RouteBackingMission` stays single-tree.
  A docking route whose journey spans a cross-tree link is a follow-up (noted in
  todo-and-known-bugs).
- **Recorder / recording-schema changes**: none; the dock link is already recorded.
- **Cross-tree periodicity / phase-lock / re-aim**: fail-closed v1 (section 4).
- **Part-level partner tracking through exotic splits** (section 3 heuristic).

## 8. Test plan

- `MissionCrossTreeDockTests`: link derivation (match, guid-mismatch decline, zero-pid
  skip, Board claim, multi-link), journey walk (dock -> undock departure follow,
  never-undocked whole line, partner-survives-as-merged-stack case, cycle guard).
- `MissionLoopUnitBuilderTests`: cross-tree unit membership + windows from both trees,
  shared span, fail-closed periodicity with the logged reason, duplicate-index first-wins,
  signature folds link state.
- `MissionStoreTests`: reconcile drops stale links + keeps cross-seam interval keys valid,
  clone copies links, SetLoopEnabled clears intersecting spanned-set loops, lifecycle with
  a linked pair (delete/prune).
- Codec: `Mission` Save/Load round-trip with links; SPARSE omission when empty;
  byte-identity pins - a mission without links serializes with an identical value/key set
  to pre-feature output (explicit key-list assertion), and a full `MissionStore` round-trip
  of a pre-feature node is unchanged.
- UI wiring: source-text gate for the link-toggle handler (IMGUI), mirroring
  `MissionSelectionGenerationStampWiringTests`.
