> STATUS: Implemented in PR #923 (narrow zero-logic-change refactor pass). Archived as a historical reference. The "Explicitly NOT proposed" rejection reasoning remains useful guidance for future refactor work.

# Refactor Opportunities: Recording + Rendering Subsystem

Analysis-only inventory. No production code changed by this scan.

## Scope and baseline

This document inventories ZERO-logic-change (behavior-preserving) refactor
opportunities in the recording and rendering subsystem, scanned against the
gen-3 clean baseline (`origin/main`, `RecordingStore.CurrentRecordingFormatVersion = 1`,
`CurrentRecordingSchemaGeneration = 3`). The baseline is freshly cleaned: the
co-bubble subsystem, the re-fly bypass + force-Absolute toggle, and the
pre-reset legacy compatibility seams (legacy v5 world-offset RELATIVE contract,
committed-bool migration, Phase-F tree-resource residual seam, rewind-suppression
marker normalizer, no-op format-version upgrade helpers) were all removed.

Files scanned: recording side (`FlightRecorder.cs`, `BackgroundRecorder.cs`,
`RecordingStore.cs`, `RecordingTree.cs`, `RecordingSidecarStore.cs`,
`RecordingTreeRecordCodec.cs`, `TrajectorySidecarBinary.cs`,
`SnapshotSidecarCodec.cs`, `PannotationsSidecarBinary.cs`); rendering side
(`GhostPlaybackEngine.cs`, `GhostPlaybackLogic.cs`, `TrajectoryMath.cs`,
`RelativeAnchorResolver.cs`, `RecordedRelativeAnchorPoseResolver.cs`,
`AnchorDetector.cs`, `ReFlyAnchorSelection.cs`, `GhostVisualBuilder.cs`,
`Rendering/AnchorCorrection.cs`, `Rendering/AnchorPriority.cs`,
`Rendering/AnchorPropagator.cs`, `Rendering/SmoothingPipeline.cs`).

Every item below is constrained by `docs/dev/refactor-guidelines.md`
(13-item checklist): no condition changes, no reordering, control flow
preserved, deduplication only when blocks are semantically identical
(checklist item 10), extraction position unchanged (item 2), no access-modifier
changes to pre-existing members (item 7 / 13), logging additions must be
observational (item 9). Anything that would need a logic change to be safe is
listed under "Explicitly NOT proposed."

This is an ANALYSIS deliverable. Every item still needs its own focused
proposal, explicit scope, and a clean-context Opus review (per the guidelines)
before any code moves. The maintainer triages this list into separate PRs.

Headline finding: the recent cleanup PRs (co-bubble / bypass / legacy-seam
removals) were thorough. A repo-wide heuristic scan for unused private methods
and write-once/unused private fields across the recording+rendering files
returned ZERO hits, and an explicit-warning build (CS0169 / CS0414 / CS0649 /
CS8321) reported 0 warnings. The few co-bubble remnants that survive
(`AnchorSource.BubbleEntry/BubbleExit/Reserved7`,
`PannotationsSidecarBinary.CanonicalEncodingLength`) are DELIBERATELY preserved
for persisted-byte-layout stability and are documented as such; they are NOT
dead-code stragglers. The behavior-preserving wins are therefore a small number
of high-confidence micro-cleanups plus a handful of extract-method candidates in
the large files.

## Summary counts

- High: 3
- Medium: 4
- Low: 3

## High priority

### H1. Dead ternary in AnchorPropagator failTag initializer

- **File:line**: `Source/Parsek/Rendering/AnchorPropagator.cs:610`
- **What**: `string failTag = !parentOk ? "no-sample-skip" : "no-sample-skip";`
  has two identical ternary arms, and `failTag` is then unconditionally
  reassigned by the `if (...) failTag = "section-not-absolute-skip"; else if
  (...) failTag = "no-sample-skip"; else failTag = "no-spline-skip";` chain
  (lines 614-619) before its only read at line 622. Simplify the initializer to
  `string failTag = "no-sample-skip";` (or restructure as a plain assignment).
- **Why behavior-preserving**: The initializer value is never observed; every
  control-flow path through the `if/else-if/else` chain assigns `failTag` before
  it is read. The ternary evaluates the same string on both branches, so the
  result is identical regardless of `parentOk`. No condition, branch, or read is
  changed (checklist items 3, 4). Pure dead-store removal.
- **Risk**: Low. No behavior depends on the discarded value; the surrounding
  diagnostic Verbose log text is unchanged.
- **Effort**: Small (one line).
- **Priority**: High (clear win, zero risk, trivial churn).

### H2. Redundant format-version / schema-generation re-parse in RecordingTreeRecordCodec

- **File:line**: `Source/Parsek/RecordingTreeRecordCodec.cs:481-499`
  (inside `LoadRecordingPlaybackAndLinkage`), redundant with
  `LoadRecordingFrom:345-365`.
- **What**: `LoadRecordingFrom` parses `recordingFormatVersion` and
  `recordingSchemaGeneration` (lines 345-365), then early-returns if the schema
  is incompatible. If it passes, it calls `LoadRecordingPlaybackAndLinkage`
  (line 462), which re-reads the SAME two keys and re-assigns
  `rec.RecordingFormatVersion` / `rec.RecordingSchemaGeneration` with the
  identical default-to-0-on-missing logic. The second parse is redundant work
  that always computes the same values already set. Remove the format/schema
  re-parse from `LoadRecordingPlaybackAndLinkage` (lines 481-499), leaving the
  rest of that method untouched.
- **Why behavior-preserving**: Both blocks read the same ConfigNode keys with
  the same parse + default-0 fallback, so the field values after the second
  block are byte-for-byte identical to the values the first block already set
  (checklist item 10: semantically identical computations). The first block runs
  unconditionally before the call site, and on the only path that reaches
  `LoadRecordingPlaybackAndLinkage` the schema was already accepted. No condition
  or control flow changes.
- **Risk**: Medium. The safety argument requires confirming the two parse
  blocks are exactly equivalent (they are: same key names, same
  `int.TryParse(..., NumberStyles.Integer, ic, ...)`, same default 0) and that
  no caller invokes `LoadRecordingPlaybackAndLinkage` independently of
  `LoadRecordingFrom`. Verify call sites before removing.
- **Effort**: Small.
- **Priority**: High (concrete dedup, low churn; the only caveat is the call-site
  uniqueness check).

### H3. Extract repeated GetValue+TryParse+assign idiom in RecordingTreeRecordCodec load helpers

- **File:line**: `Source/Parsek/RecordingTreeRecordCodec.cs` throughout
  `LoadRecordingPlaybackAndLinkage` (475-592) and `LoadRecordingResourceAndState`
  (599-913). Representative blocks: 502-509 (sidecarEpoch int), 519-525
  (loopStartUT double), 545-551 (loopAnchorPid uint), 616-622 (preLaunchFunds
  double), 786-792 (maxDist double), 820-826 (startInvSlots int), etc.
- **What**: The pattern `string s = recNode.GetValue("k"); if (s != null) { T v;
  if (T.TryParse(s, style, ic, out v)) rec.Field = v; }` repeats ~25 times for
  double / int / uint / float. Extract small private static helpers
  (`TryReadDouble(ConfigNode, string, ref double)`,
  `TryReadInt`, `TryReadUint`, `TryReadFloat`) that exactly mirror the inline
  idiom (read key, parse with the same NumberStyles + InvariantCulture, assign
  only on success). Replace each inline block with one call.
- **Why behavior-preserving**: Each helper reproduces the existing
  read-parse-assign-on-success-only semantics one-for-one. No defaulting is
  added where the original had none; fields keep their prior value on a
  missing/unparseable key exactly as today. Pure mechanical deduplication of
  identical blocks (checklist item 10) with no control-flow change. The helpers
  are newly-extracted, so marking them `private static` does not touch any
  pre-existing access modifier (items 7, 13).
- **Risk**: Medium. Several blocks differ in NumberStyles (`NumberStyles.Float`
  for doubles via the `inv` local vs `NumberStyles.Integer` for ints) and in the
  field type. The proposal must keep one helper per (type, NumberStyles)
  combination so no parse semantics drift. A few blocks have side logic beyond
  the plain assign (e.g. the `spawnedPid` block at 663-678 also sets
  `rec.VesselSpawned`; the `recordingGroup` block at 721-740 does localization +
  rename) and MUST be left inline, not folded into the helper.
- **Effort**: Medium.
- **Priority**: High value if scoped to only the pure read-parse-assign blocks;
  drop every block that carries extra side effects.

## Medium priority

### M1. Extract terminal-clamp section dispatch in RelativeAnchorResolver

- **File:line**: `Source/Parsek/RelativeAnchorResolver.cs:671-700`
  (`TryResolveTerminalClampedPose`), structurally mirrored by the
  `switch (section.referenceFrame)` block in `TryResolveRecordingPose:310-349`.
- **What**: Both methods switch on `section.referenceFrame` and dispatch to
  `TryResolveAbsoluteSectionPose` / `TryResolveRelativeSectionPose` /
  `TryResolveOrbitalSectionPose` with the same argument shape (the only
  difference is the UT passed: the requested UT vs the clamped UT, plus the
  `default:` arm). The dispatch table could be extracted into one private helper
  `TryResolveSectionPoseByFrame(context, recording, section, sectionIndex, ut,
  visited, out pose, out failure)` called from both sites with the appropriate
  UT.
- **Why behavior-preserving**: The two switches are the same mapping
  frame->resolver with identical call argument order; extracting preserves the
  exact dispatch and is called from the same position in each caller (checklist
  items 2, 3). Control flow (the `resolved` bool capture in the clamp path) is
  preserved by returning the resolver result.
- **Risk**: Medium. The two existing switches are NOT byte-identical: the
  `TryResolveRecordingPose` version's `default:` arm calls `WarnUnresolved` with
  `sectionIndex`, while the clamp version's `default:` (not shown above the cut)
  must be checked for the same failure shape before claiming identity. Verify the
  `default:` arms match before deduplicating; if they differ, scope the
  extraction to the three known frame cases only and leave each `default:`
  inline.
- **Effort**: Medium.
- **Priority**: Medium (real structural duplication, but identity of the
  `default:` arms must be proven first).

### M2. Extract the per-frame "retired post-position" preamble in GhostPlaybackEngine

- **File:line**: `Source/Parsek/GhostPlaybackEngine.cs` repeated preamble at
  1664-1668 (non-loop), 1848-1849 + 2284-2285 (loop-primary / past-end),
  2281-2285, 2523-2527 (overlap-primary), 2707-2713 (overlap),
  2752-2753 (loop-pause).
- **What**: Each playback path opens with the same three-step preamble:
  `state.anchorRetiredThisFrame = false;` is set earlier, then
  `bool X = RelativeAnchorResolution.ShouldSkipPostPositionPipeline(state.anchorRetiredThisFrame);`
  followed by a `GhostRenderTrace.EmitPostUpdate(..., X, ResolveRenderSurface(usedBodyFixed, X), ...)`
  call. The `ShouldSkipPostPositionPipeline` + `ResolveRenderSurface` pairing is
  the stable, identical part across paths. A narrow helper that returns
  `(bool retired, RenderSurface surface)` from `(state, usedBodyFixed)` could
  replace the two-line pairing at each site.
- **Why behavior-preserving**: The `ShouldSkipPostPositionPipeline(state.anchorRetiredThisFrame)`
  call and `ResolveRenderSurface(usedBodyFixed, retired)` call are pure and
  identical at every site; extracting only that pairing keeps the
  `GhostRenderTrace.EmitPostUpdate` call (with its per-path string label and
  side-effect bookkeeping) inline and unchanged.
- **Risk**: Medium. This is a hot per-frame, per-ghost path (multiplies across
  all active ghosts). The extraction must not add allocation (return a value
  tuple / out params, not a heap object) and must not move the
  `EmitPostUpdate` call or the divergent post-`retired` branches. The bulk of
  each block (the `if (retired) { ... } else { ... }` bodies) is genuinely
  DIFFERENT per path (different log keys, different FX suppression, different
  side effects) and must stay inline; only the identical 2-line pairing is a
  safe target.
- **Effort**: Medium.
- **Priority**: Medium (touches a hot path; the safe surface is small).

### M3. Extract the env-classified frame-decision logging dedup pattern

- **File:line**: `Source/Parsek/Rendering/SmoothingPipeline.cs` `EmitClusterWarnOnce`
  (354-377) and `LogFrameDecisionOnce` (379-400).
- **What**: Both methods implement the identical "dedup-by-key with capped set,
  clear-and-reseed-on-overflow" pattern: build `key = recordingId + "|" + sectionIndex`,
  lock, `if (!set.Add(key)) return;`, `if (set.Count >= Cap) { clear; re-add key;
  Info log }`, then emit the payload log. The cap-overflow housekeeping block
  (lines 361-369 vs 387-395) is structurally the same modulo the cap constant,
  set, lock, and Info text. A private helper `AddToCappedDedupSet(set, lock, key,
  cap, overflowSubsystem, overflowMessage)` returning a bool ("is new key") could
  back both.
- **Why behavior-preserving**: The two housekeeping blocks perform the same
  operations in the same order; the only differences (cap constant, set
  instance, lock object, Info subsystem/message) become parameters. The payload
  log after the helper stays inline in each method (different tags/fields), so
  no log text is altered.
- **Risk**: Medium. The two locks (`s_clusterWarnLock`, `s_frameDecisionLock`)
  guard different sets; the helper must take both the set and its lock so the
  locking discipline is preserved exactly (no lock widening / no shared lock).
  The overflow Info-log text differs between the two and must be passed through,
  not unified. Verify the `Add`-before-overflow-check ordering is preserved
  (current code adds the key, then checks count: a subtle ordering the helper
  must reproduce).
- **Effort**: Medium.
- **Priority**: Medium (clean identical pattern, but the lock+set parameterization
  must be exact).

### M4. Split the two save-helper bodies in RecordingTreeRecordCodec by the "AddValue-if-non-default" idiom

- **File:line**: `Source/Parsek/RecordingTreeRecordCodec.cs`
  `SaveRecordingResourceAndState:136-334` and
  `SaveRecordingPlaybackAndLinkage:82-130`.
- **What**: These two methods are long sequential walls of
  `if (cond) recNode.AddValue("key", value.ToString(...));`. They are already
  decomposed into two helpers, but each remains 50-200 lines of independent,
  side-effect-free `AddValue` blocks separated by comment headers ("Pre-launch
  resources", "Rewind save metadata", "Mutable playback state", etc.). The
  comment-delimited sections are candidates for further extract-method into
  named private helpers (`SaveTerminalOrbit`, `SaveRewindMetadata`,
  `SaveResourceAndInventoryManifests`, etc.) called from the same positions.
- **Why behavior-preserving**: Each comment-delimited block is a pure sequential
  run of `AddValue` calls with no early returns, no shared mutable local that a
  later block reads (the only shared local is `ic`, a culture handle), and no
  inter-block ordering dependency beyond the on-disk key order, which extraction
  preserves by calling the helpers in the same order (checklist items 2, 5, 8).
- **Risk**: Medium. ConfigNode key ORDER on disk is observable (saves are
  diffed). The extraction must call the new helpers in the exact original
  sequence so the emitted node ordering is byte-identical. The `PRE_REFLY_ANCHOR`
  block (305-326) reads live `rec` state and builds a child node; keep it inline
  or extract it whole, never split.
- **Effort**: Medium to large (many blocks; mechanical but needs care on order).
- **Priority**: Medium (improves readability of a 200-line method; ordering risk
  is the main caveat).

## Low priority

### L1. Rename misleading `IsDebrisFocusRecording` in RelativeAnchorResolver

- **File:line**: `Source/Parsek/RelativeAnchorResolver.cs:175-180`.
- **What**: The method is named `IsDebrisFocusRecording` but its body checks
  `DebrisParentRecordingId` (parent-anchored, true for both genuine debris AND
  controlled-decoupled children), not `IsDebris`. The existing doc-comment
  already flags this: "Method name keeps the historical 'IsDebrisFocusRecording'
  because the rename is sibling-plan scope." A purely cosmetic rename to
  `HasParentAnchorSurface` (or `IsParentAnchoredFocusRecording`) would match the
  body.
- **Why behavior-preserving**: A private-method rename with all call sites
  updated is purely cosmetic; no logic, signature, or access modifier changes.
- **Risk**: Low. Private method, few call sites in one file.
- **Effort**: Small.
- **Priority**: Low. The doc-comment notes the canonical rename
  (`DebrisParentRecordingId` -> `ParentAnchorRecordingId`) is "sibling-plan
  scope"; renaming this one helper ahead of the field rename may create churn
  that the field rename then re-touches. Defer to the sibling field-rename plan
  rather than doing it in isolation.

### L2. Consolidate the duplicate-but-near-identical IsFinite overloads note

- **File:line**: `Source/Parsek/TrajectoryMath.cs:269-283` (three top-level
  `IsFinite` overloads: double / Vector3d / Vector3) and `:1170`
  (`CatmullRomFit.IsFinite(double)`).
- **What**: `CatmullRomFit.IsFinite(double)` (line 1170) duplicates the top-level
  `IsFinite(double)` (line 269). The nested copy could call the outer one.
- **Why behavior-preserving**: IF the two `double` bodies are byte-identical
  (both `!double.IsNaN(value) && !double.IsInfinity(value)`), the nested one can
  delegate to the outer with no behavior change.
- **Risk**: Low to medium. Must confirm the two `double` bodies are identical;
  the nested helper lives inside a nested static class, so visibility is fine
  (the outer is `private static` on the enclosing class, accessible from the
  nested class). Verify accessibility before collapsing - if the outer were not
  reachable from the nested scope this would be out of scope (no access-modifier
  change allowed, item 13).
- **Effort**: Small.
- **Priority**: Low (tiny helper; churn rarely worth it).

### L3. Drop the dead local `string failTag` initial value style elsewhere - none found beyond H1

- **File:line**: subsystem-wide scan result.
- **What**: A targeted scan for `? X : X` dead ternaries and reassigned-before-read
  string locals across the recorder + rendering files found only the
  `AnchorPropagator` case (H1). This entry records the negative result so the
  maintainer knows the pattern was searched and is not widespread.
- **Why behavior-preserving**: N/A (no change proposed).
- **Risk**: N/A.
- **Effort**: N/A.
- **Priority**: Low (informational; nothing to do).

## Explicitly NOT proposed (considered and rejected)

These look like refactors but would require a logic or behavior change to be
safe, so they are OUT OF SCOPE for a zero-logic-change pass:

- **Dedup the orphan engine/audio auto-start across
  `GhostPlaybackLogic.AutoStartOrphanEnginePlayback` (1298-1334) and
  `ReapplySpawnTimeModuleBaselinesForLoopCycle` (1734-1758).** The class comment
  says the loop-cycle block "duplicates the zero-engine-event branch," but the
  two blocks are NOT semantically identical: `AutoStartOrphanEnginePlayback`
  emits per-engine and per-audio `ParsekLog.Verbose` lines ("Auto-started audio
  for orphan engine ...", "Auto-started engine FX for orphan engine ...") while
  the loop-cycle version intentionally omits them, and the two guard structures
  differ (one builds the key set internally and checks `== 0`; the other
  early-returns on `Count != 0`). Deduplicating would either add or remove log
  lines, violating checklist items 3 and 9. Rejected.

- **Deduplicate `TrajectoryMath.CatmullRomFit.WrapLongitude` (1160-1168) with
  `FrameTransform.WrapLongitudeDegrees` (1376-1387).** The two are NOT identical:
  `WrapLongitudeDegrees` has an extra `if (double.IsNaN(lonDeg) ||
  double.IsInfinity(lonDeg)) return lonDeg;` guard that `WrapLongitude` lacks.
  Collapsing them would change behavior for one caller (either adding a NaN guard
  to the body-fixed path or removing it from the inertial path). Rejected
  (checklist item 10 fails: blocks not semantically identical).

- **Remove `AnchorSource.BubbleEntry` / `BubbleExit` / `Reserved7`
  (`Rendering/AnchorCorrection.cs:27-32`) and shrink
  `PannotationsSidecarBinary.CanonicalEncodingLength` (132) accordingly.** These
  are deliberately preserved for persisted-byte-layout stability of the `.pann`
  `AnchorCandidate` type byte (documented at AnchorCorrection.cs:29-31 and
  PannotationsSidecarBinary.cs:113-132). `BubbleEntry`/`BubbleExit` are LIVE
  (emitted by `AnchorCandidateBuilder`, resolved by `AnchorPropagator` and
  `ProductionAnchorWorldFrameResolver`), and `Reserved7` is an intentional gap.
  Removing or renumbering would force a `.pann` algorithm-stamp bump and cache
  invalidation, which is a behavior change. Rejected (not dead code).

- **Merge the divergent `if (retired) { ... } else { ... }` bodies in the
  GhostPlaybackEngine playback paths (M2's surrounding blocks).** The retired vs
  non-retired bodies and the per-path variants (non-loop / loop-primary /
  overlap / loop-pause) carry different FX-suppression flags, different log
  dedup keys, and different side-effect calls. They are similar in shape but not
  semantically identical, so merging is not a checklist-item-10 dedup. Only the
  small 2-line `ShouldSkipPostPositionPipeline` + `ResolveRenderSurface` pairing
  (M2) is safe. Rejected for the bodies.

- **Convert `RelativeAnchorResolver` / `GhostPlaybackEngine` private instance
  helpers to `internal static` for testability.** Several would require touching
  pre-existing access modifiers or pulling in instance state, which violates
  checklist items 7, 12, 13. The correct resolution per the guidelines is to
  scale back, not to change pre-existing modifiers. Rejected.
