using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Pure static logic for recording optimization: merge redundant segments,
    /// split monolithic recordings at TrackSection boundaries.
    /// All methods are internal static for direct testability.
    /// </summary>
    internal static partial class RecordingOptimizer
    {
        internal const double MaxEvaBoundaryGapSeconds = 2.0;
        internal const double MaxEvaBoundaryOverlapSeconds = 2.0;
        private const double SplitSeedTimeEpsilon = 1e-6;

        /// <summary>
        /// Can two consecutive chain segments be auto-merged?
        /// Returns false if any user-intent signal differs from defaults,
        /// if they have different phases/bodies (except continuous EVA atmo/surface),
        /// if a branch point separates them,
        /// or if either has ghosting-trigger part events (snapshot would be wrong).
        /// </summary>
        internal static bool CanAutoMerge(Recording a, Recording b)
        {
            // NOTE: Two defenses prevent HEAD+TIP from being re-merged after Re-Fly's
            // SplitOriginAtRewindUT splits them:
            //
            //   1. Explicit supersede-row guard (below): rejects merging if either
            //      `a` or `b` appears as `OldRecordingId` in scenario.RecordingSupersedes.
            //      TIP is on the OldRecordingId side of the `TIP -> fork` row written
            //      by SupersedeCommit.AppendRelations. Robust, by-design defense.
            //
            //   2. Incidental ghosting-trigger defense (further down): the rewindUT BP
            //      is at a CRASH/DECOUPLE/STAGING/BREAKUP point, so TIP gets a
            //      ghosting-trigger PartEvent (Decoupled/Destroyed) at UT==rewindUT
            //      and HasGhostingTriggerEvents(TIP) returns true. This defends the
            //      typical case even without defense (1), but a future PartEvent-type
            //      refactor that removed a trigger type would silently re-open the bug.
            //      Defense (1) is the load-bearing one — keep it.
            if (a == null || b == null) return false;

            // Guard against re-merging HEAD/TIP across a supersede boundary.
            //
            // Background: Re-Fly's SplitOriginAtRewindUT (RecordingTreeSplitter) splits
            // the origin recording into HEAD (visible, kept on the timeline) and TIP
            // (superseded by the fork) at the rewind UT. After the merge commits, HEAD
            // and TIP are two adjacent chain siblings sharing a ChainId. Without this
            // guard, the optimizer's next pass could see HEAD+TIP as merge candidates,
            // glue them back together, and silently undo the split.
            //
            // In practice the rewindUT BP is at a CRASH/DECOUPLE/STAGING/BREAKUP point,
            // so TIP gets a Decoupled/Destroyed PartEvent at UT==rewindUT and
            // HasGhostingTriggerEvents(TIP) returns true → CanAutoMerge already rejects.
            // But that defense is incidental — a future PartEvent-type refactor that
            // removed a trigger type from the list at GhostingTriggerClassifier.cs
            // would silently re-open the bug. The explicit supersede-row check below
            // makes the defense robust.
            //
            // Uses ReferenceEquals(null, scenario) (not `scenario != null`) to bypass
            // Unity's overloaded == operator that produces false positives in unit-test
            // fixtures where no scenario is installed. Same pattern as EffectiveState /
            // MergeJournalOrchestrator.
            var scenario = ParsekScenario.Instance;
            if (!object.ReferenceEquals(null, scenario))
            {
                var supersedes = scenario.RecordingSupersedes;
                if (supersedes != null && supersedes.Count > 0)
                {
                    if (EffectiveState.IsSupersededByRelation(a, supersedes)
                        || EffectiveState.IsSupersededByRelation(b, supersedes))
                    {
                        ParsekLog.Verbose("Optimizer",
                            $"CanAutoMerge: rejecting merge of '{a.RecordingId}' + '{b.RecordingId}' " +
                            "— at least one is on the OldRecordingId side of a supersede row " +
                            "(post-split HEAD/TIP invariant)");
                        return false;
                    }
                }
            }

            // Must be in the same chain, consecutive, primary branch
            if (string.IsNullOrEmpty(a.ChainId) || a.ChainId != b.ChainId) return false;
            if (a.ChainIndex < 0 || b.ChainIndex < 0) return false;
            if (b.ChainIndex != a.ChainIndex + 1) return false;
            if (a.ChainBranch != 0 || b.ChainBranch != 0) return false;

            // No branch point between them
            if (!string.IsNullOrEmpty(a.ChildBranchPointId)) return false;

            bool samePhase = a.SegmentPhase == b.SegmentPhase;
            if (samePhase)
            {
                if (a.SegmentBodyName != b.SegmentBodyName) return false;
            }
            else if (!CanMergeContinuousEvaAtmoSurfaceBoundary(a, b))
            {
                return false;
            }

            // Neither has ghosting-trigger events (snapshot would be wrong for merged recording)
            if (GhostingTriggerClassifier.HasGhostingTriggerEvents(a)) return false;
            if (GhostingTriggerClassifier.HasGhostingTriggerEvents(b)) return false;

            // User intent: any non-default setting blocks merge
            if (a.LoopPlayback || b.LoopPlayback) return false;
            if (!double.IsNaN(a.LoopStartUT) || !double.IsNaN(a.LoopEndUT)) return false;
            if (!double.IsNaN(b.LoopStartUT) || !double.IsNaN(b.LoopEndUT)) return false;
            if (!a.PlaybackEnabled || !b.PlaybackEnabled) return false;
            if (a.Hidden || b.Hidden) return false;
            // Sentinel == Recording.LoopIntervalSeconds field initializer. Any value
            // other than the sentinel signals the user explicitly configured this recording's
            // loop interval — in that case auto-merge is blocked. Deliberately NOT comparing
            // against DefaultLoopIntervalSeconds, which is the UI default and may differ.
            if (a.LoopIntervalSeconds != LoopTiming.UntouchedLoopIntervalSentinel
                || b.LoopIntervalSeconds != LoopTiming.UntouchedLoopIntervalSentinel) return false;
            if (a.LoopAnchorVesselId != 0 || b.LoopAnchorVesselId != 0) return false;

            // M2 (plan D13, narrowed by round-2 correction 3): refuse ONLY when
            // either side carries harvest WINDOWS - they are the bridge
            // witnesses for the boundary-bridging rule (plan D5) and a merge
            // would destroy the leg-boundary operands. Blocking on ANY run
            // manifest instead would disable the merge pass for every post-M2
            // recording (RunOptimizationPass runs on every save load) - a
            // silent global regression. Window-less run manifests COMPOSE in
            // MergeInto on matching pid scope and void on mismatch.
            int harvestWindowsA = a.RouteHarvestWindows?.Count ?? 0;
            int harvestWindowsB = b.RouteHarvestWindows?.Count ?? 0;
            if (harvestWindowsA > 0 || harvestWindowsB > 0)
            {
                ParsekLog.Verbose("Optimizer",
                    $"Merge refused: harvest windows present " +
                    $"(a={harvestWindowsA.ToString(CultureInfo.InvariantCulture)} " +
                    $"b={harvestWindowsB.ToString(CultureInfo.InvariantCulture)}) " +
                    $"recordings='{a.RecordingId}'+'{b.RecordingId}'");
                return false;
            }

            // PR 3b: contract-corruption defenses. Auto-merge only operates on
            // consecutive chain segments of the same vessel, so these mismatches
            // shouldn't normally arise — but if a previously-split debris recording
            // ever loses the contract on one half, do not silently glue the halves
            // back together with a partially-set contract. Two debris with different
            // parents cannot auto-merge either.
            if (a.IsDebris != b.IsDebris) return false;
            if (!string.Equals(a.ParentAnchorRecordingId, b.ParentAnchorRecordingId, StringComparison.Ordinal))
                return false;

            // Different recording groups = user organized them differently
            if (!GroupsEqual(a.RecordingGroups, b.RecordingGroups)) return false;

            return true;
        }

        /// <summary>
        /// Can a recording be auto-split at the given TrackSection boundary?
        /// Returns false if ghosting-trigger events exist anywhere (snapshot
        /// would be invalid for the second half), if the split would create
        /// too-short halves, or if the section index is out of range.
        /// </summary>
        internal static bool CanAutoSplit(Recording rec, int sectionIndex)
        {
            if (rec == null) return false;
            if (rec.TrackSections == null || rec.TrackSections.Count < 2) return false;
            if (sectionIndex < 1 || sectionIndex >= rec.TrackSections.Count) return false;
            if (ShouldKeepContinuousEvaAtmoSurfaceTogether(rec, sectionIndex)) return false;

            // No ghosting triggers anywhere — snapshot is valid for both halves
            if (GhostingTriggerClassifier.HasGhostingTriggerEvents(rec)) return false;

            // Both halves must be longer than 5 seconds
            double splitUT = rec.TrackSections[sectionIndex].startUT;
            double firstHalfDuration = splitUT - rec.StartUT;
            double secondHalfDuration = rec.EndUT - splitUT;
            if (firstHalfDuration < 5.0 || secondHalfDuration < 5.0) return false;

            return true;
        }

        /// <summary>
        /// Same as CanAutoSplit but without the ghosting-trigger check.
        /// Used by the optimizer split pass: both halves inherit the GhostVisualSnapshot
        /// and part events are correctly partitioned by SplitAtSection, so ghosting
        /// triggers do not block splitting (they DO block merging, where the snapshot
        /// would be wrong for the merged recording).
        /// </summary>
        internal static bool CanAutoSplitIgnoringGhostTriggers(Recording rec, int sectionIndex)
        {
            if (rec == null) return false;
            if (rec.TrackSections == null || rec.TrackSections.Count < 2) return false;
            if (sectionIndex < 1 || sectionIndex >= rec.TrackSections.Count) return false;
            if (ShouldKeepContinuousEvaAtmoSurfaceTogether(rec, sectionIndex)) return false;

            // Both halves must be longer than 5 seconds
            double splitUT = rec.TrackSections[sectionIndex].startUT;
            double firstHalfDuration = splitUT - rec.StartUT;
            double secondHalfDuration = rec.EndUT - splitUT;
            if (firstHalfDuration < 5.0 || secondHalfDuration < 5.0) return false;

            return true;
        }

        /// <summary>
        /// Scans committed recordings for consecutive chain segments that can be merged.
        /// Returns pairs of indices (a, b) where b can be merged into a.
        /// </summary>
        internal static List<(int, int)> FindMergeCandidates(List<Recording> committed)
        {
            var candidates = new List<(int, int)>();
            if (committed == null || committed.Count < 2) return candidates;

            // Build chain index: chainId → list of (commitIndex, chainIndex) sorted by chainIndex
            var chainMembers = new Dictionary<string, List<(int commitIdx, int chainIdx)>>();
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (string.IsNullOrEmpty(rec.ChainId) || rec.ChainIndex < 0) continue;
                if (rec.ChainBranch != 0) continue;

                List<(int, int)> members;
                if (!chainMembers.TryGetValue(rec.ChainId, out members))
                {
                    members = new List<(int, int)>();
                    chainMembers[rec.ChainId] = members;
                }
                members.Add((i, rec.ChainIndex));
            }

            foreach (var kvp in chainMembers)
            {
                var members = kvp.Value;
                members.Sort((x, y) => x.chainIdx.CompareTo(y.chainIdx));

                for (int m = 0; m < members.Count - 1; m++)
                {
                    int idxA = members[m].commitIdx;
                    int idxB = members[m + 1].commitIdx;
                    if (CanAutoMerge(committed[idxA], committed[idxB]))
                        candidates.Add((idxA, idxB));
                }
            }

            return candidates;
        }

        /// <summary>
        /// Scans committed recordings for monolithic recordings that can be split
        /// at TrackSection boundaries where the environment changes, or at non-Exo
        /// body changes that would otherwise collapse distinct gameplay phases.
        /// Returns (commitIndex, sectionIndex) pairs.
        /// </summary>
        internal static List<(int, int)> FindSplitCandidates(List<Recording> committed)
        {
            var candidates = new List<(int, int)>();
            if (committed == null) return candidates;

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec.TrackSections == null || rec.TrackSections.Count < 2) continue;

                for (int s = 1; s < rec.TrackSections.Count; s++)
                {
                    int nextClass = SplitEnvironmentClass(rec.TrackSections[s].environment);
                    int prevClass = SplitEnvironmentClass(rec.TrackSections[s - 1].environment);
                    // Split where coarse environment class changes OR where a body change
                    // should still split. Coasting ExoBallistic body changes intentionally
                    // stay cohesive so transfer coasts render as one loopable recording.
                    bool envChanged = nextClass != prevClass;
                    bool bodyChanged = SectionBodyChanged(rec.TrackSections[s - 1], rec.TrackSections[s]);
                    bool bodyChangeSplits = bodyChanged
                        && !ShouldKeepCohesiveCrossBodyExoCoast(
                            rec.TrackSections[s - 1], rec.TrackSections[s], prevClass, nextClass);
                    if (!envChanged && !bodyChangeSplits)
                        continue;

                    if (CanAutoSplit(rec, s))
                    {
                        candidates.Add((i, s));
                        break; // One split per recording per pass (re-scan after split)
                    }
                }
            }

            return candidates;
        }

        /// <summary>
        /// Cumulative duration threshold (seconds) below which a `SplitEnvironmentClass` run is
        /// considered "brief" and therefore eligible to be treated as a graze pattern by the
        /// persistence predicate. A run shorter than this — when bracketed by the same env
        /// class on the other side — collapses into the surrounding chain segment instead of
        /// producing its own split. Aerobrake passes, eccentric Pe dips, and Karman-line
        /// tourist hops all fall under this threshold; sustained suborbital arcs, real
        /// ascents, and reentries do not.
        ///
        /// 120 s is a starting point grounded in the §3.1 calibration table (Karman hop Exo
        /// dwell crosses K at ~150 km apogee on Kerbin; LKO orbital periods are ~30 min so
        /// grazing exits never bracket-match across orbits). See
        /// docs/dev/plans/optimizer-persistence-split.md §3.1 for the rationale and
        /// docs/dev/research/optimizer-meaningful-split-rule.md for the historical
        /// PartEvent-window dead end (the reverted PR #625) that this threshold replaces.
        /// </summary>
        internal const double BriefSectionMaxSeconds = 120.0;

        private const int AtmosphericSplitClass = 0;
        private const int ExoSplitClass = 1;
        private const int SurfaceSplitClass = 2;
        private const int ApproachSplitClass = 3;

        /// <summary>
        /// Result of the optimizer's per-boundary classification, used for diagnostic logging.
        /// The accept reasons (BodyChange / SurfaceInvolved / ExoPropulsiveAtCrossing /
        /// PersistedPhaseChange) drive the Verbose accept log; the suppress reasons
        /// (SuppressedGrazeForward / SuppressedGrazeBackward / SuppressedSurfaceGrazeForward /
        /// SuppressedSurfaceGrazeBackward / SuppressedBoundarySeam / SuppressedExoCoastBodyChange)
        /// feed the per-recording
        /// aggregate suppression-counter log. NotABoundary is the case
        /// where env class is unchanged AND body is unchanged — not a decision to log.
        /// See docs/dev/plans/optimizer-persistence-split.md §8.
        /// </summary>
        internal enum SplitBoundaryReason
        {
            NotABoundary = 0,
            BodyChange,
            SurfaceInvolved,
            ExoPropulsiveAtCrossing,
            PersistedPhaseChange,
            SuppressedGrazeForward,
            SuppressedGrazeBackward,
            SuppressedSurfaceGrazeForward,
            SuppressedSurfaceGrazeBackward,
            SuppressedBoundarySeam,
            SuppressedExoCoastBodyChange
        }

        /// <summary>
        /// Boundary predicate for the optimizer split pass. Encodes the §3 ordering from
        /// docs/dev/plans/optimizer-persistence-split.md:
        ///   1. Seam short-circuit (hard "always wins" override — Producer-C boundary seam).
        ///   2. Not a boundary (env unchanged AND body unchanged) — caller skips.
        ///   3. Same-class ExoBallistic body change — keep as one cohesive transfer coast.
        ///   4. Other body change (#251) — always meaningful.
        ///   5. Surface involved — split unless the boundary is a brief Atmo/Approach run
        ///      bracketed by Surface on both sides.
        ///   6. ExoPropulsive at the crossing — engine firing, direct gameplay event.
        ///   7. Persistence predicate (graze-pattern detection via collapse-walk).
        /// </summary>
        /// <param name="reason">
        /// Set on every call where env or body changed (i.e., the function inspected the
        /// boundary). NotABoundary when env class is the same and body did not change —
        /// caller treats this as "skip without counting." Seam-flagged sections also return
        /// false but with reason = SuppressedBoundarySeam, which the caller tallies.
        /// </param>
        private static bool IsSplittableEnvOrBodyBoundary(
            Recording rec, int s, out SplitBoundaryReason reason)
        {
            var prev = rec.TrackSections[s - 1];
            var next = rec.TrackSections[s];

            // Step 1: seam short-circuit. Hard "always wins" override regardless of env class
            // or body. The contract: "this section is a recorder bookkeeping artifact, never
            // a split candidate, full stop." Producer-C today only emits seams on a
            // loaded->on-rails transition where body and Surface state cannot change at the
            // seam, but the ordering must hold for future producers.
            if (prev.isBoundarySeam || next.isBoundarySeam)
            {
                reason = SplitBoundaryReason.SuppressedBoundarySeam;
                return false;
            }

            // Step 2: not a boundary at all.
            int prevClass = SplitEnvironmentClass(prev.environment);
            int nextClass = SplitEnvironmentClass(next.environment);
            bool envChanged = prevClass != nextClass;
            bool bodyChanged = SectionBodyChanged(prev, next);
            if (!envChanged && !bodyChanged)
            {
                reason = SplitBoundaryReason.NotABoundary;
                return false;
            }

            // Step 3: same-class ExoBallistic body changes stay cohesive. Orbit/map renderers still
            // read body names from OrbitSegments/points at runtime, while UI display builds a
            // multi-body label from the recorded payload.
            if (bodyChanged && ShouldKeepCohesiveCrossBodyExoCoast(prev, next, prevClass, nextClass))
            {
                reason = SplitBoundaryReason.SuppressedExoCoastBodyChange;
                return false;
            }

            // Step 4: other body changes (#251) remain meaningful, never gated.
            if (bodyChanged)
            {
                reason = SplitBoundaryReason.BodyChange;
                return true;
            }

            // Step 5: Surface (class 2) on either side — meaningful unless the non-surface
            // side is a brief Atmospheric/Approach run bracketed by Surface. Body changes
            // that should split have already returned above this branch.
            if (prevClass == SurfaceSplitClass || nextClass == SurfaceSplitClass)
            {
                if (IsSurfaceGrazePattern(rec, s, out var surfaceGrazeDirection))
                {
                    reason = surfaceGrazeDirection;
                    return false;
                }

                reason = SplitBoundaryReason.SurfaceInvolved;
                return true;
            }

            // Step 6: ExoPropulsive at the crossing. EnvironmentDetector.Classify only
            // assigns ExoPropulsive when ModuleEngines thrust is positive, so this directly
            // encodes "engines were firing across the boundary."
            if (prev.environment == SegmentEnvironment.ExoPropulsive
                || next.environment == SegmentEnvironment.ExoPropulsive)
            {
                reason = SplitBoundaryReason.ExoPropulsiveAtCrossing;
                return true;
            }

            // Step 7: persistence predicate. Atmospheric <-> ExoBallistic and
            // Approach <-> ExoBallistic (the noise cluster) — split iff this isn't a graze
            // pattern.
            if (IsGrazePattern(rec, s, out var grazeDirection))
            {
                reason = grazeDirection;
                return false;
            }

            reason = SplitBoundaryReason.PersistedPhaseChange;
            return true;
        }

        /// <summary>
        /// Persistence predicate: returns true when the boundary at index <paramref name="s"/>
        /// is a graze pattern (one side is a brief run that's bracketed by the same env class
        /// on the other side, i.e. <c>A → [brief run of B] → A'</c>). Walks
        /// <see cref="SplitEnvironmentClass"/> runs on both sides to handle same-split-class
        /// adjacent sections (raw <see cref="SegmentEnvironment"/> transitions within a class,
        /// or forced section breaks that restart the same env). See §3.1 / §3.2 of
        /// docs/dev/plans/optimizer-persistence-split.md.
        /// </summary>
        /// <remarks>
        /// The walks have a built-in upper bound: as soon as cumulative duration crosses
        /// <see cref="BriefSectionMaxSeconds"/>, the run is "long enough to be a phase" and
        /// no bracket check is needed. Cumulative cost across all boundaries in a recording
        /// is O(N) (each section visited at most twice — once forward from a left-side
        /// boundary, once backward from a right-side boundary).
        /// </remarks>
        internal static bool IsGrazePattern(
            Recording rec, int s, out SplitBoundaryReason direction)
        {
            var sections = rec.TrackSections;
            var prev = sections[s - 1];
            var next = sections[s];
            int prevClass = SplitEnvironmentClass(prev.environment);
            int nextClass = SplitEnvironmentClass(next.environment);

            // (A) Forward bracket: walk forward through next's split-class run; if cumulative
            // duration is brief AND the section after the run is in prev's class, suppress.
            int nextRunEndIdx = s;
            while (nextRunEndIdx + 1 < sections.Count
                && SplitEnvironmentClass(sections[nextRunEndIdx + 1].environment) == nextClass)
            {
                nextRunEndIdx++;
            }
            double nextRunCumDur = sections[nextRunEndIdx].endUT - next.startUT;
            int forwardBracketIdx = nextRunEndIdx + 1;
            if (nextRunCumDur < BriefSectionMaxSeconds
                && forwardBracketIdx < sections.Count
                && SplitEnvironmentClass(sections[forwardBracketIdx].environment) == prevClass)
            {
                direction = SplitBoundaryReason.SuppressedGrazeForward;
                return true;
            }

            // (B) Backward bracket: walk backward through prev's split-class run; if
            // cumulative duration is brief AND the section before the run is in next's class,
            // suppress.
            int prevRunStartIdx = s - 1;
            while (prevRunStartIdx - 1 >= 0
                && SplitEnvironmentClass(sections[prevRunStartIdx - 1].environment) == prevClass)
            {
                prevRunStartIdx--;
            }
            double prevRunCumDur = prev.endUT - sections[prevRunStartIdx].startUT;
            int backwardBracketIdx = prevRunStartIdx - 1;
            if (prevRunCumDur < BriefSectionMaxSeconds
                && backwardBracketIdx >= 0
                && SplitEnvironmentClass(sections[backwardBracketIdx].environment) == nextClass)
            {
                direction = SplitBoundaryReason.SuppressedGrazeBackward;
                return true;
            }

            direction = SplitBoundaryReason.PersistedPhaseChange; // unused on false return
            return false;
        }

        /// <summary>
        /// Surface-specific graze predicate: suppresses a brief Atmospheric/Approach run
        /// bracketed by Surface on both sides. Deliberately does not suppress a brief
        /// Surface run bracketed by Atmospheric/Approach; a first touchdown/bounce remains
        /// a meaningful Surface boundary and the subsequent brief descent can be folded into it.
        /// </summary>
        internal static bool IsSurfaceGrazePattern(
            Recording rec, int s, out SplitBoundaryReason direction)
        {
            var sections = rec.TrackSections;
            var prev = sections[s - 1];
            var next = sections[s];
            int prevClass = SplitEnvironmentClass(prev.environment);
            int nextClass = SplitEnvironmentClass(next.environment);

            // (A) Forward surface bracket: Surface -> [brief Atmo/Approach run] -> Surface.
            int nextRunEndIdx = s;
            while (nextRunEndIdx + 1 < sections.Count
                && SplitEnvironmentClass(sections[nextRunEndIdx + 1].environment) == nextClass)
            {
                nextRunEndIdx++;
            }
            double nextRunCumDur = sections[nextRunEndIdx].endUT - next.startUT;
            int forwardBracketIdx = nextRunEndIdx + 1;
            if (prevClass == SurfaceSplitClass
                && IsSurfaceGrazeBriefClass(nextClass)
                && nextRunCumDur < BriefSectionMaxSeconds
                && forwardBracketIdx < sections.Count
                && SplitEnvironmentClass(sections[forwardBracketIdx].environment)
                    == SurfaceSplitClass)
            {
                direction = SplitBoundaryReason.SuppressedSurfaceGrazeForward;
                return true;
            }

            // (B) Backward surface bracket: Surface -> [brief Atmo/Approach run] -> Surface.
            int prevRunStartIdx = s - 1;
            while (prevRunStartIdx - 1 >= 0
                && SplitEnvironmentClass(sections[prevRunStartIdx - 1].environment) == prevClass)
            {
                prevRunStartIdx--;
            }
            double prevRunCumDur = prev.endUT - sections[prevRunStartIdx].startUT;
            int backwardBracketIdx = prevRunStartIdx - 1;
            if (nextClass == SurfaceSplitClass
                && IsSurfaceGrazeBriefClass(prevClass)
                && prevRunCumDur < BriefSectionMaxSeconds
                && backwardBracketIdx >= 0
                && SplitEnvironmentClass(sections[backwardBracketIdx].environment)
                    == SurfaceSplitClass)
            {
                direction = SplitBoundaryReason.SuppressedSurfaceGrazeBackward;
                return true;
            }

            direction = SplitBoundaryReason.PersistedPhaseChange; // unused on false return
            return false;
        }

        private static bool IsSurfaceGrazeBriefClass(int splitClass)
        {
            return splitClass == AtmosphericSplitClass || splitClass == ApproachSplitClass;
        }

        /// <summary>
        /// Same as FindSplitCandidates but uses CanAutoSplitIgnoringGhostTriggers and applies
        /// the §3 / §3.1 boundary predicate (seam short-circuit, body hard split,
        /// Surface default split with surface-graze suppression, ExoPropulsive short-circuit,
        /// persistence predicate). Used by the optimizer split pass.
        /// </summary>
        /// <remarks>
        /// Splits are driven by `rec.TrackSections`. Legacy top-level non-predicted
        /// `rec.OrbitSegments` are first normalized into OrbitalCheckpoint sections so
        /// split/save paths cannot drop packed coasts. On-rails BG vessels still never emit
        /// env-classified per-frame TrackSections (see `BackgroundRecorder.BackgroundOnRailsState`),
        /// so an eccentric grazing-Pe orbit coasted for thousands of orbits cannot produce
        /// Atmospheric<->ExoBallistic toggle candidates. The invariant is guarded by
        /// `EccentricOrbitOptimizerInvariantTests`.
        /// Producer-C boundary seams (`TrackSection.isBoundarySeam == true`) are skipped at
        /// step 1 of the predicate — see `BackgroundRecorder.FlushLoadedStateForOnRailsTransition`.
        /// </remarks>
        internal static List<(int, int)> FindSplitCandidatesForOptimizer(List<Recording> committed)
        {
            var candidates = new List<(int, int)>();
            if (committed == null) return candidates;

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                RecordingStore.EnsureCheckpointSectionsForTopLevelOrbitSegments(
                    rec,
                    markDirty: false,
                    context: "RecordingOptimizer.FindSplitCandidatesForOptimizer");
                if (rec.TrackSections == null || rec.TrackSections.Count < 2) continue;

                // Per-recording aggregate counters (CLAUDE.md "Batch counting convention" —
                // an eccentric grazing recording can present hundreds of suppressed boundaries,
                // exactly the spam this gate is meant to avoid).
                int evaluatedBoundaries = 0;
                int suppressedGrazeForward = 0;
                int suppressedGrazeBackward = 0;
                int suppressedSurfaceGrazeForward = 0;
                int suppressedSurfaceGrazeBackward = 0;
                int suppressedBoundarySeam = 0;
                int suppressedExoCoastBodyChange = 0;
                int splittableButRejected = 0;

                for (int s = 1; s < rec.TrackSections.Count; s++)
                {
                    SplitBoundaryReason reason;
                    bool isSplittable = IsSplittableEnvOrBodyBoundary(rec, s, out reason);

                    if (isSplittable)
                    {
                        if (CanAutoSplitIgnoringGhostTriggers(rec, s))
                        {
                            // Per-candidate accept log — bounded by the `break` after one
                            // split per recording per pass × the maxSplitsPerPass cap from
                            // RunOptimizationSplitPass.
                            ParsekLog.Verbose("Optimizer",
                                $"Split candidate ({reason}): rec={rec.RecordingId} sec={s} " +
                                $"splitUT={rec.TrackSections[s].startUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                                $"prev={rec.TrackSections[s - 1].environment} next={rec.TrackSections[s].environment}");
                            candidates.Add((i, s));
                            break; // One split per recording per pass (re-scan after split).
                        }
                        // Splittable boundary that CanAutoSplit rejects (e.g. too-short halves,
                        // EVA atmo↔surface continuous gate). Counted as evaluated and as
                        // "splittable-but-rejected" so the summary log surfaces a recording
                        // where every boundary is rejected for the same downstream reason.
                        evaluatedBoundaries++;
                        splittableButRejected++;
                        continue;
                    }

                    // NotABoundary → env unchanged + body unchanged. Not a decision to count
                    // or log; just keep scanning later boundaries.
                    if (reason == SplitBoundaryReason.NotABoundary) continue;

                    // Suppression cases (env or body changed but the predicate said no).
                    evaluatedBoundaries++;
                    switch (reason)
                    {
                        case SplitBoundaryReason.SuppressedGrazeForward:
                            suppressedGrazeForward++;
                            break;
                        case SplitBoundaryReason.SuppressedGrazeBackward:
                            suppressedGrazeBackward++;
                            break;
                        case SplitBoundaryReason.SuppressedSurfaceGrazeForward:
                            suppressedSurfaceGrazeForward++;
                            break;
                        case SplitBoundaryReason.SuppressedSurfaceGrazeBackward:
                            suppressedSurfaceGrazeBackward++;
                            break;
                        case SplitBoundaryReason.SuppressedBoundarySeam:
                            suppressedBoundarySeam++;
                            break;
                        case SplitBoundaryReason.SuppressedExoCoastBodyChange:
                            suppressedExoCoastBodyChange++;
                            break;
                    }
                }

                // Aggregate per-recording summary line. Covers both predicate-suppressed
                // boundaries (graze patterns, coasting cross-body Exo transitions, seam short-circuits)
                // AND splittable boundaries
                // that CanAutoSplit later rejected. The line title is "Split summary" because
                // "splittableButRejected" is downstream-rejected by CanAutoSplit, not
                // suppressed by the §3 predicate — calling the whole line "Split suppressed"
                // would imply the predicate caused all of it.
                if (suppressedGrazeForward > 0 || suppressedGrazeBackward > 0
                    || suppressedSurfaceGrazeForward > 0 || suppressedSurfaceGrazeBackward > 0
                    || suppressedBoundarySeam > 0 || suppressedExoCoastBodyChange > 0
                    || splittableButRejected > 0)
                {
                    ParsekLog.Verbose("Optimizer",
                        $"Split summary: rec={rec.RecordingId} " +
                        $"evaluated={evaluatedBoundaries} " +
                        $"grazeForward={suppressedGrazeForward} " +
                        $"grazeBackward={suppressedGrazeBackward} " +
                        $"surfaceGrazeForward={suppressedSurfaceGrazeForward} " +
                        $"surfaceGrazeBackward={suppressedSurfaceGrazeBackward} " +
                        $"seamSkipped={suppressedBoundarySeam} " +
                        $"exoCoastBodyChangeKept={suppressedExoCoastBodyChange} " +
                        $"splittableButRejected={splittableButRejected}");
                }
            }

            return candidates;
        }

        /// <summary>
        /// Merges recording B into recording A (A absorbs B).
        /// Points, events, sections, and orbit segments are concatenated.
        /// Returns B's RecordingId (caller deletes files + removes from store).
        /// </summary>
        internal static string MergeInto(Recording target, Recording absorbed)
        {
            // Destroyed recordings carry a sealed terminal verdict — VesselDestroyed,
            // TerminalStateValue=Destroyed, ExplicitEndUT=terminalUT, and cleared
            // landed/orbital metadata (see Recording.MarkDestroyedAtTerminal). The
            // unconditional ExplicitEndUT/ExplicitStartUT clear at the bottom of this
            // method would break the seal, and the per-event/section concatenations
            // would extend Recording.EndUT past the destruction UT. Skip the merge
            // entirely — there is nothing to absorb into a sealed recording.
            if (target != null && target.VesselDestroyed)
            {
                ParsekLog.VerboseRateLimited("Optimizer",
                    $"merge-into-destroyed.{target.RecordingId}",
                    $"MergeInto: skipping — target is already destroyed " +
                    $"(target={target.RecordingId}, absorbed={absorbed?.RecordingId ?? "(null)"})",
                    60.0);
                return absorbed?.RecordingId;
            }

            bool normalizeEvaBoundaryMerge = CanMergeContinuousEvaAtmoSurfaceBoundary(target, absorbed);

            // 1. Concatenate Points (already UT-ordered within each recording)
            if (absorbed.Points != null && absorbed.Points.Count > 0)
                target.Points.AddRange(absorbed.Points);

            // 2. Merge + re-sort PartEvents by UT.
            // STABLE sort: same-UT events preserve insertion order so terminal Shutdowns
            // stay before continuation seed EngineIgnited events (#287).
            if (absorbed.PartEvents != null && absorbed.PartEvents.Count > 0)
            {
                target.PartEvents.AddRange(absorbed.PartEvents);
                var sortedMerge = FlightRecorder.StableSortPartEventsByUT(target.PartEvents);
                target.PartEvents.Clear();
                target.PartEvents.AddRange(sortedMerge);
            }

            // 3. Merge + re-sort SegmentEvents by UT with STABLE semantics for consistency
            // with the PartEvents path (#287) — same-UT events keep their insertion order.
            if (absorbed.SegmentEvents != null && absorbed.SegmentEvents.Count > 0)
            {
                target.SegmentEvents.AddRange(absorbed.SegmentEvents);
                var sortedSegs = FlightRecorder.StableSortByUT(target.SegmentEvents, e => e.ut);
                target.SegmentEvents.Clear();
                target.SegmentEvents.AddRange(sortedSegs);
            }

            // 4. Concatenate TrackSections
            if (absorbed.TrackSections != null && absorbed.TrackSections.Count > 0)
                target.TrackSections.AddRange(absorbed.TrackSections);

            // 5. Merge OrbitSegments
            if (absorbed.HasOrbitSegments)
                target.OrbitSegments.AddRange(absorbed.OrbitSegments);

            // 6. Union FlagEvents
            if (absorbed.FlagEvents != null && absorbed.FlagEvents.Count > 0)
            {
                if (target.FlagEvents == null)
                    target.FlagEvents = new List<FlagEvent>();
                target.FlagEvents.AddRange(absorbed.FlagEvents);
            }

            // 7. VesselSnapshot: if absorbed was chain tip (non-null), target inherits it
            if (absorbed.VesselSnapshot != null)
                target.VesselSnapshot = absorbed.VesselSnapshot;
            if (!string.IsNullOrEmpty(absorbed.ChildBranchPointId))
                target.ChildBranchPointId = absorbed.ChildBranchPointId;

            // Resource manifests: absorbed recording's end resources win (later segment)
            if (absorbed.EndResources != null)
                target.EndResources = absorbed.EndResources;
            // target.StartResources intentionally unchanged — represents the earlier start

            // M2 (plan D13): compose window-less run manifests on matching pid
            // scope (first's START + second's END, scope kept); void on scope
            // mismatch or a one-sided manifest. CanAutoMerge already refused
            // any pair carrying harvest windows, so the bridge witnesses are
            // never destroyed here; the compose is verdict-preserving (a
            // positive boundary delta is unaccounted both pre-merge - no window
            // at the seam - and post-merge - inside fullRunGain, uncovered).
            target.RouteRunManifest = ComposeRunManifestsForMerge(
                target.RouteRunManifest,
                absorbed.RouteRunManifest,
                target.RecordingId,
                absorbed.RecordingId);

            // Inventory manifests: same pattern — absorbed end wins
            if (absorbed.EndInventory != null)
                target.EndInventory = absorbed.EndInventory;
            if (absorbed.EndInventorySlots != 0)
                target.EndInventorySlots = absorbed.EndInventorySlots;
            // target.StartInventory intentionally unchanged — represents the earlier start

            // Crew manifests: same pattern — absorbed end wins
            if (absorbed.EndCrew != null)
                target.EndCrew = absorbed.EndCrew;
            // target.StartCrew intentionally unchanged — represents the earlier start

            // Dock target PID: absorbed wins if non-zero (dock may be in later segment)
            if (absorbed.DockTargetVesselPid != 0)
                target.DockTargetVesselPid = absorbed.DockTargetVesselPid;

            // 8. TerminalState: absorbed is the later segment, inherit its terminal state
            if (absorbed.TerminalStateValue.HasValue)
                target.TerminalStateValue = absorbed.TerminalStateValue;
            if (absorbed.TerminalOrbitBody != null)
            {
                target.TerminalOrbitInclination = absorbed.TerminalOrbitInclination;
                target.TerminalOrbitEccentricity = absorbed.TerminalOrbitEccentricity;
                target.TerminalOrbitSemiMajorAxis = absorbed.TerminalOrbitSemiMajorAxis;
                target.TerminalOrbitLAN = absorbed.TerminalOrbitLAN;
                target.TerminalOrbitArgumentOfPeriapsis = absorbed.TerminalOrbitArgumentOfPeriapsis;
                target.TerminalOrbitMeanAnomalyAtEpoch = absorbed.TerminalOrbitMeanAnomalyAtEpoch;
                target.TerminalOrbitEpoch = absorbed.TerminalOrbitEpoch;
                target.TerminalOrbitBody = absorbed.TerminalOrbitBody;
            }
            if (absorbed.TerminalPosition.HasValue)
                target.TerminalPosition = absorbed.TerminalPosition;
            if (!double.IsNaN(absorbed.TerrainHeightAtEnd))
                target.TerrainHeightAtEnd = absorbed.TerrainHeightAtEnd;
            if (absorbed.SurfacePos.HasValue)
                target.SurfacePos = absorbed.SurfacePos;

            // 9. Clear explicit UT ranges (Points now cover the full range)
            target.ExplicitStartUT = double.NaN;
            target.ExplicitEndUT = double.NaN;

            // 10. Controllers: keep target's if present, else inherit (defensive
            // copy to match every other Recording.Controllers propagation site).
            if (target.Controllers == null && absorbed.Controllers != null)
                target.Controllers = new List<ControllerInfo>(absorbed.Controllers);

            // 11. AntennaSpecs: keep target's if present, else inherit
            if (target.AntennaSpecs == null && absorbed.AntennaSpecs != null)
                target.AntennaSpecs = absorbed.AntennaSpecs;

            if (normalizeEvaBoundaryMerge)
                NormalizeContinuousEvaBoundaryMerge(target);

            // Merged recordings inherit later terminal/body state from the absorbed segment.
            // Re-resolve the authoritative endpoint from the merged payload before persistence.
            RecordingEndpointResolver.RefreshEndpointDecision(target, "RecordingOptimizer.MergeInto");

            // 12. Invalidate cached stats
            target.CachedStats = null;
            target.CachedStatsPointCount = 0;

            ParsekLog.Info("Optimizer",
                $"MergeInto: absorbed {absorbed.RecordingId} into {target.RecordingId} " +
                $"(target now has {target.Points.Count} points, {target.TrackSections.Count} sections)");

            return absorbed.RecordingId;
        }

        /// <summary>
        /// M2 (plan D13): merge-time composition of two window-less run
        /// manifests. Returns the composed manifest (first's START half +
        /// second's END half, scope set kept) when both sides are present with
        /// MATCHING pid scope (set equality - the same physical transport);
        /// returns null (void, Verbose-logged) on scope mismatch or when only
        /// one side carries a manifest (the merged span cannot be described
        /// honestly). Pure / static / testable.
        /// </summary>
        internal static RouteRunCargoManifest ComposeRunManifestsForMerge(
            RouteRunCargoManifest first,
            RouteRunCargoManifest second,
            string firstRecordingId,
            string secondRecordingId)
        {
            if (first == null && second == null)
                return null;

            if (first == null || second == null)
            {
                ParsekLog.Verbose("Optimizer",
                    $"RouteRunManifest voided: recording={firstRecordingId ?? "<none>"} " +
                    $"reason=merge-one-sided " +
                    $"(first={(first != null ? "present" : "absent")} " +
                    $"second={(second != null ? "present" : "absent")} " +
                    $"absorbed={secondRecordingId ?? "<none>"})");
                return null;
            }

            if (!PartPidScopesMatch(first.TransportPartPersistentIds, second.TransportPartPersistentIds))
            {
                ParsekLog.Verbose("Optimizer",
                    $"RouteRunManifest voided: recording={firstRecordingId ?? "<none>"} " +
                    $"reason=merge-scope-mismatch " +
                    $"(firstParts={first.TransportPartPersistentIds?.Count ?? 0} " +
                    $"secondParts={second.TransportPartPersistentIds?.Count ?? 0} " +
                    $"absorbed={secondRecordingId ?? "<none>"})");
                return null;
            }

            var composed = new RouteRunCargoManifest
            {
                TransportPartPersistentIds = first.TransportPartPersistentIds != null
                    ? new List<uint>(first.TransportPartPersistentIds)
                    : null,
                StartTransportResources =
                    RouteProofMetadata.CloneResourceManifest(first.StartTransportResources),
                EndTransportResources =
                    RouteProofMetadata.CloneResourceManifest(second.EndTransportResources),
                EndCaptured = second.EndCaptured
            };

            ParsekLog.Verbose("Optimizer",
                $"Merge: run manifests composed (scope match) " +
                $"target={firstRecordingId ?? "<none>"} absorbed={secondRecordingId ?? "<none>"} " +
                $"parts={composed.TransportPartPersistentIds?.Count ?? 0} " +
                $"endCaptured={(composed.EndCaptured ? "1" : "0")}");
            return composed;
        }

        /// <summary>
        /// Order-insensitive part-pid set equality (capture order is authored
        /// by part-tree walks, not load-bearing for scope identity).
        /// </summary>
        internal static bool PartPidScopesMatch(List<uint> a, List<uint> b)
        {
            int countA = a?.Count ?? 0;
            int countB = b?.Count ?? 0;
            if (countA == 0 || countB == 0)
                return false;

            var setA = new HashSet<uint>(a);
            var setB = new HashSet<uint>(b);
            return setA.SetEquals(setB);
        }

        /// <summary>
        /// Splits a recording at the given TrackSection boundary index.
        /// Returns the new Recording (second half). The original is mutated to keep the first half.
        /// Caller must assign chain linkage, save files, and add to store.
        /// </summary>
        internal static Recording SplitAtSection(Recording original, int sectionIndex)
        {
            RecordingStore.EnsureCheckpointSectionsForTopLevelOrbitSegments(
                original,
                markDirty: false,
                context: "RecordingOptimizer.SplitAtSection");
            double splitUT = original.TrackSections[sectionIndex].startUT;
            List<PartEvent> transientStateSeeds = BuildTransientStateSeeds(original.PartEvents, splitUT);

            var second = new Recording();

            // 1-2. Partition Points by UT
            int splitPointIdx = 0;
            for (int i = 0; i < original.Points.Count; i++)
            {
                if (original.Points[i].ut >= splitUT) { splitPointIdx = i; break; }
            }
            // Guard: if no point >= splitUT, keep all points in original (nothing to split)
            if (splitPointIdx == 0 && original.Points.Count > 0 && original.Points[0].ut < splitUT)
            {
                splitPointIdx = original.Points.Count;
            }

            // If there's a gap at the split boundary (no point at exactly splitUT),
            // interpolate a synthetic boundary point so both halves have continuous coverage.
            TrajectoryPoint? boundaryPoint = null;
            if (splitPointIdx > 0 && splitPointIdx < original.Points.Count)
            {
                var before = original.Points[splitPointIdx - 1];
                var after = original.Points[splitPointIdx];
                if (before.ut < splitUT && after.ut > splitUT)
                {
                    double t = (splitUT - before.ut) / (after.ut - before.ut);
                    float tf = (float)t;
                    // NOTE: Longitude is linearly lerped here. This does not handle the 360/0
                    // wraparound edge case, but adjacent adaptive-sampled points should never
                    // straddle the antimeridian — the sampling interval is far too short.
                    boundaryPoint = new TrajectoryPoint
                    {
                        ut = splitUT,
                        latitude = before.latitude + (after.latitude - before.latitude) * t,
                        longitude = before.longitude + (after.longitude - before.longitude) * t,
                        altitude = before.altitude + (after.altitude - before.altitude) * t,
                        rotation = UnityEngine.Quaternion.Slerp(before.rotation, after.rotation, tf),
                        velocity = UnityEngine.Vector3.Lerp(before.velocity, after.velocity, tf),
                        bodyName = after.bodyName,
                        funds = before.funds + (after.funds - before.funds) * t,
                        science = before.science + (after.science - before.science) * tf,
                        reputation = before.reputation + (after.reputation - before.reputation) * tf,
                        // Phase 7: lerp clearance between adjacent points; if either
                        // is NaN (legacy / non-surface) the result is NaN and
                        // playback falls through to the legacy altitude path.
                        recordedGroundClearance = before.recordedGroundClearance
                            + (after.recordedGroundClearance - before.recordedGroundClearance) * t
                    };
                    // Insert as last point of first half (at splitPointIdx, before the second half starts)
                    original.Points.Insert(splitPointIdx, boundaryPoint.Value);
                    splitPointIdx++; // Advance so second half still starts at the original after-point

                    ParsekLog.Verbose("Optimizer",
                        $"SplitAtSection: interpolated boundary point at UT={splitUT:F2} " +
                        $"(between UT={before.ut:F2} and UT={after.ut:F2}, t={t:F4})");
                }
            }

            second.Points = new List<TrajectoryPoint>(
                original.Points.GetRange(splitPointIdx, original.Points.Count - splitPointIdx));
            original.Points.RemoveRange(splitPointIdx, original.Points.Count - splitPointIdx);

            // If we interpolated a boundary point, prepend it to the second half as well
            // so it starts exactly at splitUT with no gap
            if (boundaryPoint.HasValue && (second.Points.Count == 0 || second.Points[0].ut > splitUT))
            {
                second.Points.Insert(0, boundaryPoint.Value);
            }

            // 3. Partition PartEvents by UT
            PartitionPartEvents(original.PartEvents, second.PartEvents, splitUT);

            // 3b. Seed visual state at the split boundary. Permanent one-way events
            // come first, followed by transient engine/RCS state at the same UT.
            // Events like ShroudJettisoned/FairingJettisoned in the first half represent
            // state at the split point — the second half's ghost needs them to render correctly.
            // ForwardPermanentStateEvents inserts at the front, so run it after
            // transient insertion to preserve permanent -> transient seed order.
            InsertTransientStateSeeds(transientStateSeeds, second.PartEvents, splitUT);
            ForwardPermanentStateEvents(original.PartEvents, second.PartEvents, splitUT);

            // 4. Partition SegmentEvents by UT
            PartitionSegmentEvents(original.SegmentEvents, second.SegmentEvents, splitUT);

            // 5. Partition FlagEvents by UT
            if (original.FlagEvents != null)
            {
                second.FlagEvents = new List<FlagEvent>();
                PartitionFlagEvents(original.FlagEvents, second.FlagEvents, splitUT);
            }

            // 6. Partition TrackSections
            second.TrackSections = new List<TrackSection>(
                original.TrackSections.GetRange(sectionIndex, original.TrackSections.Count - sectionIndex));
            original.TrackSections.RemoveRange(sectionIndex, original.TrackSections.Count - sectionIndex);
            TrimFirstHalfTrackSectionsAtSplit(original.TrackSections, splitUT);

            // 7. Partition OrbitSegments by UT.
            //    Whole post-split segments (startUT >= splitUT) move to TIP.
            //    A straddler (startUT < splitUT < endUT) is HEAD-trimmed by
            //    TrimFirstHalfOrbitSegmentsAtSplit (endUT clamped to splitUT) AND
            //    tail-cloned into TIP so the post-split portion of the orbit is
            //    preserved. The orbit's Kepler elements describe the WHOLE conic
            //    (inclination, eccentricity, semiMajorAxis, LAN, AoP,
            //    meanAnomalyAtEpoch, epoch, bodyName, isPredicted,
            //    orbitalFrameRotation, angularVelocity) and remain valid for both
            //    halves — only startUT / endUT differ.
            if (original.HasOrbitSegments)
            {
                second.OrbitSegments = new List<OrbitSegment>();

                // Build tail-clones for any straddling segment BEFORE the
                // whole-segment moves, so the clones can be inserted at the
                // front of TIP's list in UT-ascending order.
                List<OrbitSegment> tailClones = CreateTailHalfOrbitSegmentsAtSplit(
                    original.OrbitSegments, splitUT);

                int wholeMoved = 0;
                for (int i = original.OrbitSegments.Count - 1; i >= 0; i--)
                {
                    if (original.OrbitSegments[i].startUT >= splitUT)
                    {
                        second.OrbitSegments.Insert(0, original.OrbitSegments[i]);
                        original.OrbitSegments.RemoveAt(i);
                        wholeMoved++;
                    }
                }

                // Tail-clones precede every wholly-post-split segment in UT
                // order because each clone's startUT == splitUT, which is by
                // definition <= the startUT of any wholly-post-split segment.
                for (int i = 0; i < tailClones.Count; i++)
                {
                    second.OrbitSegments.Insert(i, tailClones[i]);
                }

                TrimFirstHalfOrbitSegmentsAtSplit(original.OrbitSegments, splitUT);

                if (wholeMoved > 0 || tailClones.Count > 0)
                {
                    ParsekLog.Verbose("Optimizer",
                        $"SplitAtSection: partitioned OrbitSegments at UT=" +
                        $"{splitUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                        $"(whole moved={wholeMoved.ToString(CultureInfo.InvariantCulture)}, " +
                        $"tail-clones={tailClones.Count.ToString(CultureInfo.InvariantCulture)})");
                }
            }

            // 8. Clone GhostVisualSnapshot (safe: CanAutoSplit ensures no ghosting triggers).
            // Bug #271 safety net: if GhostVisualSnapshot is null but VesselSnapshot exists,
            // create GhostVisualSnapshot from VesselSnapshot before transferring. Without this,
            // the original half ends up with both fields null after step 10 transfers
            // VesselSnapshot to the second half.
            if (original.GhostVisualSnapshot == null && original.VesselSnapshot != null)
                original.GhostVisualSnapshot = original.VesselSnapshot.CreateCopy();
            if (original.GhostVisualSnapshot != null)
                second.GhostVisualSnapshot = original.GhostVisualSnapshot.CreateCopy();

            // 9. Tag SegmentPhase from environment
            if (second.TrackSections.Count > 0)
            {
                var env = second.TrackSections[0].environment;
                second.SegmentPhase = SegmentPhaseClassifier.EnvironmentToPhase(env);
            }
            if (original.TrackSections.Count > 0)
            {
                var env = original.TrackSections[0].environment;
                original.SegmentPhase = SegmentPhaseClassifier.EnvironmentToPhase(env);
            }

            second.SegmentBodyName = original.SegmentBodyName;

            // 9b. Propagate location context (Phase 10)
            // First half keeps original Start* fields.
            // Second half derives start from its first point; keeps original EndBiome.
            second.EndBiome = original.EndBiome;
            original.EndBiome = null;

            if (second.Points.Count > 0)
            {
                var firstPt = second.Points[0];
                second.StartBodyName = firstPt.bodyName;
                second.StartBiome = VesselSpawner.TryResolveBiome(firstPt.bodyName, firstPt.latitude, firstPt.longitude);
                // StartSituation left null — ambiguous from environment alone
            }

            if (original.Points.Count > 0)
            {
                var lastPt = original.Points[original.Points.Count - 1];
                original.EndBiome = VesselSpawner.TryResolveBiome(lastPt.bodyName, lastPt.latitude, lastPt.longitude);
            }

            // 10. Transfer terminal-state fields to second half (represents end-of-recording state)
            TransferTerminalFieldsToSecondHalf(original, second);

            // 11. Copy shared fields to both halves
            second.Controllers = original.Controllers != null
                ? new List<ControllerInfo>(original.Controllers) : null;
            second.AntennaSpecs = original.AntennaSpecs != null
                ? new List<AntennaSpec>(original.AntennaSpecs) : null;
            second.IsDebris = original.IsDebris;
            // PR 3b: propagate the debris parent-anchor contract to both halves of
            // a SplitAtSection split. The `original` half retains its field by virtue of
            // in-place mutation; the `second` (newly-allocated) half needs the explicit copy.
            second.ParentAnchorRecordingId = original.ParentAnchorRecordingId;
            second.RecordingFormatVersion = original.RecordingFormatVersion;
            // Both halves are the same vessel — share Generation. (#284)
            second.Generation = original.Generation;
            // EVA linkage: both halves represent the same EVA kerbal
            second.EvaCrewName = original.EvaCrewName;
            second.ParentRecordingId = original.ParentRecordingId;

            var secondFlatTailSource = new Recording
            {
                Points = second.Points != null
                    ? new List<TrajectoryPoint>(second.Points)
                    : null,
                OrbitSegments = second.OrbitSegments != null
                    ? new List<OrbitSegment>(second.OrbitSegments)
                    : null
            };

            bool syncedOriginalFlatTrajectory = RecordingStore.TrySyncFlatTrajectoryFromTrackSections(
                original, allowRelativeSections: true);
            string originalFlatSyncMode = syncedOriginalFlatTrajectory
                ? "track-sections"
                : "unchanged";

            bool preservedSecondFlatTail =
                RecordingStore.TrySyncFlatTrajectoryFromTrackSectionsPreservingFlatTail(
                    second,
                    secondFlatTailSource,
                    second.TrackSections,
                    allowRelativeSections: true);
            string secondFlatSyncMode;
            if (preservedSecondFlatTail)
            {
                secondFlatSyncMode = "track-sections-preserved-flat-tail:" +
                    CountPredictedOrbitSegments(second.OrbitSegments).ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                bool syncedSecondFlatTrajectory = RecordingStore.TrySyncFlatTrajectoryFromTrackSections(
                    second, allowRelativeSections: true);
                secondFlatSyncMode = syncedSecondFlatTrajectory
                    ? "track-sections"
                    : "unchanged";
            }

            // Both halves now have their final trajectory/terminal payloads. Refresh the
            // persisted endpoint decision so optimizer outputs do not save stale or unknown data.
            RecordingEndpointResolver.RefreshEndpointDecision(original, "RecordingOptimizer.SplitAtSection.FirstHalf");
            RecordingEndpointResolver.RefreshEndpointDecision(second, "RecordingOptimizer.SplitAtSection.SecondHalf");

            // 12. Invalidate cached stats
            original.CachedStats = null;
            original.CachedStatsPointCount = 0;
            second.CachedStats = null;
            second.CachedStatsPointCount = 0;

            // Persist exact payload bounds as a metadata fallback. If a future
            // load cannot hydrate the sidecar, the .sfs still carries the split
            // half's real time window instead of a stale finalize-time range.
            StampExplicitBoundsFromPayload(original, "RecordingOptimizer.SplitAtSection.FirstHalf");
            StampExplicitBoundsFromPayload(second, "RecordingOptimizer.SplitAtSection.SecondHalf");

            ParsekLog.Info("Optimizer",
                $"SplitAtSection: split {original.RecordingId} at UT={splitUT:F1} " +
                $"(first: {original.Points.Count} pts/{original.TrackSections.Count} sections, " +
                $"second: {second.Points.Count} pts/{second.TrackSections.Count} sections, " +
                $"flatSync={originalFlatSyncMode}/{secondFlatSyncMode})");

            return second;
        }

        /// <summary>
        /// SplitAtSection step 10: transfer terminal-state fields from the original
        /// (first half) to the newly-allocated second half (which represents the
        /// end-of-recording state). The first half keeps its start-state fields.
        /// </summary>
        private static void TransferTerminalFieldsToSecondHalf(Recording original, Recording second)
        {
            second.VesselSnapshot = original.VesselSnapshot;
            original.VesselSnapshot = null;

            // Resource manifests: first half keeps start, second half gets end (moved with VesselSnapshot)
            second.EndResources = original.EndResources;
            original.EndResources = null;
            // original.StartResources unchanged (keeps the recording-start resources)
            // second.StartResources stays null (no snapshot at environment boundary)

            // M2 (plan D13): a split run manifest would leave an END describing
            // the original FULL span on the first half, and harvest windows can
            // land on the wrong side of the cut - semantically wrong data the
            // gain analysis would consume. NULL both new fields on BOTH halves;
            // the presence gate degrades the tree to legacy analysis (clean
            // degrade, never wrong math).
            int voidedHarvestWindows = original.RouteHarvestWindows?.Count ?? 0;
            bool voidedRunManifest = original.RouteRunManifest != null;
            original.RouteRunManifest = null;
            original.RouteHarvestWindows = null;
            second.RouteRunManifest = null;
            second.RouteHarvestWindows = null;
            if (voidedRunManifest || voidedHarvestWindows > 0)
            {
                ParsekLog.Verbose("Optimizer",
                    $"Split: run manifest + {voidedHarvestWindows.ToString(CultureInfo.InvariantCulture)} " +
                    $"harvest window(s) voided on both halves (recording={original.RecordingId})");
            }

            // Inventory manifests: same pattern — second half gets end
            second.EndInventory = original.EndInventory;
            second.EndInventorySlots = original.EndInventorySlots;
            original.EndInventory = null;
            original.EndInventorySlots = 0;
            // original.StartInventory unchanged (keeps the recording-start inventory)
            // second.StartInventory stays null (no snapshot at environment boundary)

            // Crew manifests: same pattern — second half gets end
            second.EndCrew = original.EndCrew;
            original.EndCrew = null;
            // original.StartCrew unchanged (keeps the recording-start crew)
            // second.StartCrew stays null (no snapshot at environment boundary)

            second.TerminalStateValue = original.TerminalStateValue;
            original.TerminalStateValue = null;

            second.TerminalOrbitInclination = original.TerminalOrbitInclination;
            second.TerminalOrbitEccentricity = original.TerminalOrbitEccentricity;
            second.TerminalOrbitSemiMajorAxis = original.TerminalOrbitSemiMajorAxis;
            second.TerminalOrbitLAN = original.TerminalOrbitLAN;
            second.TerminalOrbitArgumentOfPeriapsis = original.TerminalOrbitArgumentOfPeriapsis;
            second.TerminalOrbitMeanAnomalyAtEpoch = original.TerminalOrbitMeanAnomalyAtEpoch;
            second.TerminalOrbitEpoch = original.TerminalOrbitEpoch;
            second.TerminalOrbitBody = original.TerminalOrbitBody;
            original.TerminalOrbitInclination = 0;
            original.TerminalOrbitEccentricity = 0;
            original.TerminalOrbitSemiMajorAxis = 0;
            original.TerminalOrbitLAN = 0;
            original.TerminalOrbitArgumentOfPeriapsis = 0;
            original.TerminalOrbitMeanAnomalyAtEpoch = 0;
            original.TerminalOrbitEpoch = 0;
            original.TerminalOrbitBody = null;

            second.TerminalPosition = original.TerminalPosition;
            original.TerminalPosition = null;

            second.TerrainHeightAtEnd = original.TerrainHeightAtEnd;
            original.TerrainHeightAtEnd = double.NaN;

            second.SurfacePos = original.SurfacePos;
            original.SurfacePos = null;
        }

        /// <summary>
        /// Splits a recording at an arbitrary UT (typically the Re-Fly rewind-point UT)
        /// into HEAD (in-place, covers [original.StartUT, splitUT]) and TIP (returned,
        /// covers [splitUT, original.EndUT]). If the splitUT falls inside an existing
        /// TrackSection the section is cloned into a head/tail pair at splitUT before
        /// delegating to <see cref="SplitAtSection"/>. The returned Recording has a
        /// fresh-but-blank state: callers must assign <c>RecordingId</c>, chain linkage,
        /// tree wiring, and sidecar files.
        ///
        /// Returns null (without mutating <paramref name="original"/>) on any guard
        /// failure: bad pre-conditions (null input, NaN splitUT, recording's UT bounds
        /// don't strictly span splitUT) or a debris contract violation (post-split
        /// half ends up with fewer than 2 bodyFixedFrames samples). Straddling
        /// OrbitSegments are safe — <see cref="SplitAtSection"/>'s OrbitSegments
        /// partition tail-clones them into TIP at startUT=splitUT (the Kepler
        /// elements describe the whole conic, so a value-copy plus startUT update
        /// is sufficient). The boundary-seam case logs an override Warn but does
        /// not return null. Caller treats null as "skip the split for this
        /// recording" and falls back to its today-behavior path.
        /// </summary>
        internal static Recording SplitAtUT(Recording original, double splitUT)
        {
            // 1. Pre-conditions (fail-loud Warn + null on violation)
            if (original == null)
            {
                ParsekLog.Warn("Optimizer",
                    "SplitAtUT: precondition violation — original recording is null");
                return null;
            }
            if (double.IsNaN(splitUT))
            {
                ParsekLog.Warn("Optimizer",
                    $"SplitAtUT: precondition violation — splitUT is NaN " +
                    $"(recording={original.RecordingId})");
                return null;
            }
            double recStart = original.StartUT;
            double recEnd = original.EndUT;
            if (!(recStart < splitUT) || !(recEnd > splitUT))
            {
                ParsekLog.Warn("Optimizer",
                    $"SplitAtUT: precondition violation — recording {original.RecordingId} " +
                    $"UT bounds [{recStart.ToString("F2", CultureInfo.InvariantCulture)}," +
                    $"{recEnd.ToString("F2", CultureInfo.InvariantCulture)}] do not strictly span " +
                    $"splitUT={splitUT.ToString("F2", CultureInfo.InvariantCulture)}");
                return null;
            }

            // 2. (Removed) Orbit-segment-straddle guard. Straddling OrbitSegments are
            //    now handled in SplitAtSection's partition step by tail-cloning the
            //    straddler into TIP at startUT=splitUT (Kepler elements describe the
            //    whole conic — a value-copy plus startUT update is sufficient). See
            //    SplitAtSection step 7 and CreateTailHalfOrbitSegmentsAtSplit.

            // 3. Ensure checkpoint sections (mirrors SplitAtSection's call at line 776-779).
            //
            // Pass 2 review Opus-H2 (byte-identical contract): Ensure may
            // clip checkpoint sections, append new sections, re-sort
            // TrackSections, rebuild the flat orbit cache, and reset
            // CachedStats — all in place on `original`. The plan §r4
            // mutation-ordering rule requires "a guarded return leaves
            // the input recording byte-identical." Snapshot the affected
            // fields BEFORE Ensure so the debris guard / gap-fallback /
            // boundary-search guarded-return paths below can restore.
            //
            // Cheap on the happy path: list shallow-copies of structs,
            // never used unless we hit a guarded return. TrackSection and
            // OrbitSegment are value types; Ensure replaces struct values
            // in list slots and (for OrbitSegments rebuild) assigns a
            // fresh list reference — the snapshot captures the old
            // contents either way.
            List<TrackSection> trackSectionsPreEnsure = original.TrackSections != null
                ? new List<TrackSection>(original.TrackSections)
                : null;
            List<OrbitSegment> orbitSegmentsPreEnsure = original.OrbitSegments != null
                ? new List<OrbitSegment>(original.OrbitSegments)
                : null;
            var cachedStatsPreEnsure = original.CachedStats;
            int cachedStatsPointCountPreEnsure = original.CachedStatsPointCount;

            var ensureStats = RecordingStore.EnsureCheckpointSectionsForTopLevelOrbitSegments(
                original,
                markDirty: false,
                context: "RecordingOptimizer.SplitAtUT");

            // Pass 3 review subtle gap: the prior gate keyed on
            // `ensureStats.Changed`, which is true only when sections were
            // Added / Clipped / SkippedCovered. But Ensure's cleanup block at
            // OrbitSegmentCheckpointBridge.cs:188 also fires its CachedStats
            // wipe under `Changed || sorted` — a pure-re-sort case
            // (sorted=true, Changed=false) wipes CachedStats but the
            // Changed flag stays false. Restoration is cheap (list-clear +
            // AddRange of struct values), and this is only reached on
            // guarded null returns (off the hot path). Drop the gate and
            // always restore.
            void RestorePreEnsureSnapshot()
            {
                if (trackSectionsPreEnsure != null)
                {
                    if (original.TrackSections == null)
                        original.TrackSections = new List<TrackSection>(trackSectionsPreEnsure);
                    else
                    {
                        original.TrackSections.Clear();
                        original.TrackSections.AddRange(trackSectionsPreEnsure);
                    }
                }
                if (orbitSegmentsPreEnsure != null)
                {
                    if (original.OrbitSegments == null)
                        original.OrbitSegments = new List<OrbitSegment>(orbitSegmentsPreEnsure);
                    else
                    {
                        original.OrbitSegments.Clear();
                        original.OrbitSegments.AddRange(orbitSegmentsPreEnsure);
                    }
                }
                original.CachedStats = cachedStatsPreEnsure;
                original.CachedStatsPointCount = cachedStatsPointCountPreEnsure;
                ParsekLog.Verbose("Optimizer",
                    $"SplitAtUT: restored pre-Ensure snapshot on guarded return for " +
                    $"{original.RecordingId} (Ensure stats added={ensureStats.Added} " +
                    $"clipped={ensureStats.Clipped} changed={ensureStats.Changed})");
            }

            // 4. Find or insert TrackSection boundary at splitUT.
            //    All work happens in LOCAL variables; original.TrackSections is mutated
            //    only after every guard below passes (mutation-ordering invariant).
            const double epsilon = EffectiveState.PidPeerStartUtEpsilonSeconds;
            int sectionIndex = -1;
            bool syntheticBoundaryInserted = false;
            int straddleIndex = -1;

            if (original.TrackSections != null && original.TrackSections.Count > 0)
            {
                for (int i = 0; i < original.TrackSections.Count; i++)
                {
                    var s = original.TrackSections[i];
                    if (s.startUT <= splitUT && splitUT < s.endUT)
                    {
                        straddleIndex = i;
                        break;
                    }
                }
            }

            if (straddleIndex >= 0)
            {
                var straddle = original.TrackSections[straddleIndex];
                if (Math.Abs(straddle.startUT - splitUT) < epsilon)
                {
                    // splitUT aligns within epsilon to an existing section boundary.
                    // Use that section's index directly; no synthetic insertion.
                    sectionIndex = straddleIndex;
                }
                else
                {
                    // splitUT falls in the interior of a section. Build head + tail
                    // clones in LOCAL variables before mutating original.TrackSections.
                    if (straddle.isBoundarySeam)
                    {
                        ParsekLog.Warn("Optimizer",
                            $"SplitAtUT: synthetic rewindUT split overriding seam protection " +
                            $"on section {straddleIndex} of recording {original.RecordingId} — " +
                            "single one-off override for re-fly commit");
                    }

                    // Partition straddle.checkpoints by UT (mirrors SplitAtSection
                    // step 7's top-level OrbitSegment partition). Without this,
                    // both halves would inherit the full pre-split checkpoints list
                    // verbatim, and TrySyncFlatTrajectoryFromTrackSectionsPreservingFlatTail
                    // (called downstream by SplitAtSection) could rebuild TIP's
                    // OrbitSegments from those un-partitioned checkpoints via
                    // RebuildOrbitSegmentsFromTrackSections, silently undoing the
                    // tail-clone work A7 added to SplitAtSection. The same logic also
                    // protects HEAD's `TrimFirstHalfTrackSectionsAtSplit` pass — because
                    // headSection.endUT is already set to splitUT below, that pass would
                    // not re-trim the head's checkpoints either. Whole pre-split
                    // checkpoints go to head; whole post-split go to tail; straddlers
                    // are head-trimmed (endUT clamped to splitUT) into head AND
                    // tail-cloned (startUT set to splitUT) into tail. Kepler elements
                    // describe the whole conic so a struct value-copy is sufficient.
                    List<OrbitSegment> headCheckpoints = null;
                    List<OrbitSegment> tailCheckpoints = null;
                    if (straddle.checkpoints != null)
                    {
                        headCheckpoints = new List<OrbitSegment>();
                        tailCheckpoints = new List<OrbitSegment>();
                        for (int ci = 0; ci < straddle.checkpoints.Count; ci++)
                        {
                            OrbitSegment cp = straddle.checkpoints[ci];
                            if (cp.endUT <= splitUT)
                            {
                                headCheckpoints.Add(cp);
                            }
                            else if (cp.startUT >= splitUT)
                            {
                                tailCheckpoints.Add(cp);
                            }
                            else
                            {
                                OrbitSegment headClone = cp;
                                headClone.endUT = splitUT;
                                headCheckpoints.Add(headClone);

                                OrbitSegment tailClone = cp;
                                tailClone.startUT = splitUT;
                                tailCheckpoints.Add(tailClone);
                            }
                        }
                    }

                    TrackSection headSection = new TrackSection
                    {
                        environment = straddle.environment,
                        referenceFrame = straddle.referenceFrame,
                        startUT = straddle.startUT,
                        endUT = splitUT,
                        anchorVesselId = straddle.anchorVesselId,
                        anchorRecordingId = straddle.anchorRecordingId,
                        sampleRateHz = straddle.sampleRateHz,
                        source = straddle.source,
                        // boundaryDiscontinuityMeters: preserve the head's pre-existing value
                        boundaryDiscontinuityMeters = straddle.boundaryDiscontinuityMeters,
                        minAltitude = straddle.minAltitude,
                        maxAltitude = straddle.maxAltitude,
                        // Synthetic rewind-UT split is NOT a recorder bookkeeping artifact.
                        isBoundarySeam = false,
                        frames = straddle.frames != null
                            ? new List<TrajectoryPoint>() : null,
                        bodyFixedFrames = straddle.bodyFixedFrames != null
                            ? new List<TrajectoryPoint>() : null,
                        checkpoints = headCheckpoints,
                    };
                    TrackSection tailSection = new TrackSection
                    {
                        environment = straddle.environment,
                        referenceFrame = straddle.referenceFrame,
                        startUT = splitUT,
                        endUT = straddle.endUT,
                        anchorVesselId = straddle.anchorVesselId,
                        anchorRecordingId = straddle.anchorRecordingId,
                        sampleRateHz = straddle.sampleRateHz,
                        source = straddle.source,
                        // Tail synthetic boundary is continuous in world space.
                        boundaryDiscontinuityMeters = 0f,
                        minAltitude = straddle.minAltitude,
                        maxAltitude = straddle.maxAltitude,
                        isBoundarySeam = false,
                        frames = straddle.frames != null
                            ? new List<TrajectoryPoint>() : null,
                        bodyFixedFrames = straddle.bodyFixedFrames != null
                            ? new List<TrajectoryPoint>() : null,
                        checkpoints = tailCheckpoints,
                    };

                    // Partition the section's per-section frames by UT.
                    if (straddle.frames != null)
                    {
                        for (int fi = 0; fi < straddle.frames.Count; fi++)
                        {
                            var frame = straddle.frames[fi];
                            if (frame.ut < splitUT)
                                headSection.frames.Add(frame);
                            else
                                tailSection.frames.Add(frame);
                        }
                    }

                    // Partition body-fixed primary surface samples by UT.
                    bool hadBodyFixedFrames = straddle.bodyFixedFrames != null
                        && straddle.bodyFixedFrames.Count > 0;
                    if (straddle.bodyFixedFrames != null)
                    {
                        for (int fi = 0; fi < straddle.bodyFixedFrames.Count; fi++)
                        {
                            var frame = straddle.bodyFixedFrames[fi];
                            if (frame.ut < splitUT)
                                headSection.bodyFixedFrames.Add(frame);
                            else
                                tailSection.bodyFixedFrames.Add(frame);
                        }
                    }

                    // Debris frame contract guard: each half must retain at least 2
                    // bodyFixedFrames samples when the straddling section had them.
                    if (hadBodyFixedFrames)
                    {
                        if (headSection.bodyFixedFrames.Count < 2)
                        {
                            ParsekLog.Warn("Optimizer",
                                $"SplitAtUT: v13 debris contract — head-half bodyFixedFrames " +
                                $"sample count {headSection.bodyFixedFrames.Count} below minimum " +
                                $"after rewindUT split on section {straddleIndex} of recording " +
                                $"{original.RecordingId}; skipping split");
                            RestorePreEnsureSnapshot();
                            return null;
                        }
                        if (tailSection.bodyFixedFrames.Count < 2)
                        {
                            ParsekLog.Warn("Optimizer",
                                $"SplitAtUT: v13 debris contract — tail-half bodyFixedFrames " +
                                $"sample count {tailSection.bodyFixedFrames.Count} below minimum " +
                                $"after rewindUT split on section {straddleIndex} of recording " +
                                $"{original.RecordingId}; skipping split");
                            RestorePreEnsureSnapshot();
                            return null;
                        }
                    }

                    // All guards passed — commit the mutation by writing the head/tail
                    // pair back to original.TrackSections.
                    original.TrackSections[straddleIndex] = headSection;
                    original.TrackSections.Insert(straddleIndex + 1, tailSection);
                    sectionIndex = straddleIndex + 1;
                    syntheticBoundaryInserted = true;
                }
            }
            else
            {
                // splitUT lies in a gap between sections (no straddling section).
                // Pick the index of the first section whose startUT >= splitUT.
                if (original.TrackSections != null)
                {
                    for (int i = 0; i < original.TrackSections.Count; i++)
                    {
                        if (original.TrackSections[i].startUT >= splitUT)
                        {
                            sectionIndex = i;
                            break;
                        }
                    }
                    if (sectionIndex < 0)
                    {
                        // Pass 2 review Opus-H1: splitUT past every section's
                        // startUT means there is no TrackSection covering
                        // [lastSection.endUT, splitUT) — the recording's flat
                        // Points may extend that far but no section does.
                        // Falling through with sectionIndex = TrackSections.Count
                        // would crash in SplitAtSection's first line
                        // (`original.TrackSections[sectionIndex].startUT` — index
                        // out of range). Return null with a Warn so the
                        // splitter's caller falls back to whole-recording
                        // supersede instead of the merge crashing on an
                        // ArgumentOutOfRangeException.
                        ParsekLog.Warn("Optimizer",
                            $"SplitAtUT: defensive guard — splitUT=" +
                            $"{splitUT.ToString("F2", CultureInfo.InvariantCulture)} is past every " +
                            $"TrackSection's startUT for recording {original.RecordingId} " +
                            $"(sectionCount={original.TrackSections.Count.ToString(CultureInfo.InvariantCulture)}, " +
                            $"recBounds=[{recStart.ToString("F2", CultureInfo.InvariantCulture)}," +
                            $"{recEnd.ToString("F2", CultureInfo.InvariantCulture)}]) — " +
                            "skipping split (no section to anchor the cut)");
                        RestorePreEnsureSnapshot();
                        return null;
                    }
                }
                else
                {
                    // Pass 2 review Opus-H1: TrackSections null on a recording
                    // that passed the strict-span pre-condition is structurally
                    // unexpected (sampler emits at least one section before the
                    // recorder ever records points outside one). Defensive
                    // null-return matches the splitter's contract.
                    ParsekLog.Warn("Optimizer",
                        $"SplitAtUT: defensive guard — recording {original.RecordingId} " +
                        "has null TrackSections list; skipping split");
                    RestorePreEnsureSnapshot();
                    return null;
                }
            }

            // 5. Delegate to SplitAtSection. The interpolated-boundary-point branch
            //    (lines 797-848) handles flat point-list partitioning across the cut.
            Recording tip = SplitAtSection(original, sectionIndex);

            // 6. Success log.
            ParsekLog.Info("Optimizer",
                $"SplitAtUT: split {original.RecordingId} at UT=" +
                $"{splitUT.ToString("F2", CultureInfo.InvariantCulture)} (head=[" +
                $"{original.StartUT.ToString("F2", CultureInfo.InvariantCulture)}.." +
                $"{original.EndUT.ToString("F2", CultureInfo.InvariantCulture)}], tail=[" +
                $"{tip.StartUT.ToString("F2", CultureInfo.InvariantCulture)}.." +
                $"{tip.EndUT.ToString("F2", CultureInfo.InvariantCulture)}], " +
                $"syntheticBoundaryInserted={(syntheticBoundaryInserted ? "true" : "false")})");

            return tip;
        }

        private static void TrimFirstHalfTrackSectionsAtSplit(
            List<TrackSection> trackSections,
            double splitUT)
        {
            if (trackSections == null || trackSections.Count == 0)
                return;

            for (int i = trackSections.Count - 1; i >= 0; i--)
            {
                TrackSection section = trackSections[i];
                if (section.startUT >= splitUT)
                {
                    trackSections.RemoveAt(i);
                    continue;
                }

                if (section.endUT > splitUT)
                {
                    if (TryTrimTrackSectionPayload(ref section, splitUT))
                        trackSections[i] = section;
                    else
                        trackSections.RemoveAt(i);
                }
            }
        }

        private static void TrimFirstHalfOrbitSegmentsAtSplit(
            List<OrbitSegment> orbitSegments,
            double splitUT)
        {
            if (orbitSegments == null || orbitSegments.Count == 0)
                return;

            for (int i = orbitSegments.Count - 1; i >= 0; i--)
            {
                OrbitSegment segment = orbitSegments[i];
                if (segment.startUT >= splitUT)
                {
                    orbitSegments.RemoveAt(i);
                    continue;
                }

                if (segment.endUT > splitUT)
                {
                    segment.endUT = splitUT;
                    if (segment.endUT > segment.startUT)
                        orbitSegments[i] = segment;
                    else
                        orbitSegments.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Returns tail clones of every OrbitSegment in <paramref name="originalSegments"/>
        /// that straddles <paramref name="splitUT"/> (i.e., <c>seg.startUT &lt; splitUT
        /// &lt; seg.endUT</c>). Each clone is a value-copy of the original struct with
        /// <c>startUT</c> set to <paramref name="splitUT"/> and <c>endUT</c> unchanged.
        ///
        /// All other fields (inclination, eccentricity, semiMajorAxis, LAN, AoP,
        /// meanAnomalyAtEpoch, epoch, bodyName, isPredicted, orbitalFrameRotation,
        /// angularVelocity) are preserved because the Kepler elements describe the
        /// WHOLE conic — only the UT range slices it. Non-straddling segments are
        /// NOT included in the output; those are partitioned by the caller's
        /// whole-segment moves and head-half trim. Symmetric counterpart to
        /// <see cref="TrimFirstHalfOrbitSegmentsAtSplit"/>.
        ///
        /// Output ordering mirrors input ordering: clones are emitted in the order
        /// their originals appear in <paramref name="originalSegments"/>.
        /// </summary>
        private static List<OrbitSegment> CreateTailHalfOrbitSegmentsAtSplit(
            List<OrbitSegment> originalSegments,
            double splitUT)
        {
            var clones = new List<OrbitSegment>();
            if (originalSegments == null || originalSegments.Count == 0)
                return clones;

            for (int i = 0; i < originalSegments.Count; i++)
            {
                OrbitSegment seg = originalSegments[i];
                if (seg.startUT < splitUT && seg.endUT > splitUT)
                {
                    // Value-copy of the struct duplicates every Kepler element.
                    OrbitSegment tail = seg;
                    tail.startUT = splitUT;
                    // tail.endUT unchanged.
                    clones.Add(tail);
                }
            }
            return clones;
        }

        /// <summary>
        /// Re-indexes ChainIndex for all branch-0 recordings with the given ChainId.
        /// Sorts by StartUT, assigns sequential indices starting from 0.
        /// </summary>
        internal static void ReindexChain(List<Recording> committed, string chainId)
        {
            if (committed == null || string.IsNullOrEmpty(chainId)) return;

            var members = new List<Recording>();
            for (int i = 0; i < committed.Count; i++)
            {
                if (committed[i].ChainId == chainId && committed[i].ChainBranch == 0)
                    members.Add(committed[i]);
            }

            members.Sort((a, b) => a.StartUT.CompareTo(b.StartUT));
            for (int i = 0; i < members.Count; i++)
                members[i].ChainIndex = i;

            ParsekLog.Verbose("Optimizer",
                $"ReindexChain: chainId={chainId}, {members.Count} branch-0 members re-indexed");
        }

        #region Private helpers

        private static int CountPredictedOrbitSegments(List<OrbitSegment> orbitSegments)
        {
            if (orbitSegments == null)
                return 0;

            int count = 0;
            for (int i = 0; i < orbitSegments.Count; i++)
            {
                if (orbitSegments[i].isPredicted)
                    count++;
            }

            return count;
        }

        /// <summary>
        /// Returns a coarse environment class for split decisions. ExoPropulsive and
        /// ExoBallistic are treated as the same class ("exo") — engine on/off cycles
        /// happen too frequently to be meaningful split boundaries. The optimizer splits
        /// at Atmospheric↔Exo, Exo↔Approach, Approach↔Surface transitions.
        /// Approach is its own class so landing/takeoff on airless bodies can be looped.
        /// </summary>

        /// <summary>
        /// Returns true if two adjacent TrackSections have different celestial bodies.
        /// Detects SOI transitions. Same-class ExoBallistic coast transitions are kept
        /// cohesive by the optimizer split predicate and surfaced through display labels
        /// instead; ExoPropulsive body boundaries still split.
        /// Uses orbit segment body if available, otherwise first trajectory point body.
        /// </summary>
        internal static bool SectionBodyChanged(TrackSection prev, TrackSection next)
        {
            string prevBody = GetSectionBody(prev);
            string nextBody = GetSectionBody(next);
            if (string.IsNullOrEmpty(prevBody) || string.IsNullOrEmpty(nextBody))
                return false;
            return prevBody != nextBody;
        }

        private static bool ShouldKeepCohesiveCrossBodyExoCoast(
            TrackSection prev,
            TrackSection next,
            int prevClass,
            int nextClass)
        {
            return prevClass == ExoSplitClass
                && nextClass == ExoSplitClass
                && prev.environment == SegmentEnvironment.ExoBallistic
                && next.environment == SegmentEnvironment.ExoBallistic;
        }

        private static string GetSectionBody(TrackSection section)
        {
            if (section.checkpoints != null && section.checkpoints.Count > 0)
                return section.checkpoints[0].bodyName;
            if (section.frames != null && section.frames.Count > 0)
                return section.frames[0].bodyName;
            return null;
        }

        private static bool IsEvaRecording(Recording rec)
        {
            return rec != null && !string.IsNullOrEmpty(rec.EvaCrewName);
        }

        internal static bool CanMergeContinuousEvaAtmoSurfaceBoundary(Recording a, Recording b)
        {
            if (!HasSameEvaIdentity(a, b)) return false;
            if (!TryGetCommonRecordingBody(a, b, out _)) return false;
            if (!IsAtmoSurfacePhasePair(a.SegmentPhase, b.SegmentPhase)) return false;
            if (!HasContinuousBoundaryTiming(a.EndUT, b.StartUT)) return false;
            return true;
        }

        private static bool HasSameEvaIdentity(Recording a, Recording b)
        {
            if (!IsEvaRecording(a) || !IsEvaRecording(b)) return false;
            if (a.EvaCrewName != b.EvaCrewName) return false;

            if ((!string.IsNullOrEmpty(a.ParentRecordingId) || !string.IsNullOrEmpty(b.ParentRecordingId))
                && a.ParentRecordingId != b.ParentRecordingId)
                return false;

            if (a.VesselPersistentId != 0 && b.VesselPersistentId != 0
                && a.VesselPersistentId != b.VesselPersistentId)
                return false;

            return true;
        }

        private static bool TryGetCommonRecordingBody(Recording a, Recording b, out string bodyName)
        {
            bodyName = null;
            string bodyA = GetRecordingBody(a);
            string bodyB = GetRecordingBody(b);
            if (string.IsNullOrEmpty(bodyA) || string.IsNullOrEmpty(bodyB) || bodyA != bodyB)
                return false;
            bodyName = bodyA;
            return true;
        }

        private static string GetRecordingBody(Recording rec)
        {
            if (rec == null) return null;
            if (!string.IsNullOrEmpty(rec.SegmentBodyName))
                return rec.SegmentBodyName;
            if (rec.Points != null)
            {
                for (int i = 0; i < rec.Points.Count; i++)
                {
                    if (!string.IsNullOrEmpty(rec.Points[i].bodyName))
                        return rec.Points[i].bodyName;
                }
            }
            if (!string.IsNullOrEmpty(rec.StartBodyName))
                return rec.StartBodyName;
            return null;
        }

        private static bool ShouldKeepContinuousEvaAtmoSurfaceTogether(Recording rec, int sectionIndex)
        {
            if (!IsEvaRecording(rec)) return false;
            var prev = rec.TrackSections[sectionIndex - 1];
            var next = rec.TrackSections[sectionIndex];
            if (!IsAtmoSurfaceEnvironmentPair(prev.environment, next.environment)) return false;
            if (!TryGetCommonSectionBody(rec, prev, next, out _)) return false;
            if (!HasContinuousBoundaryTiming(prev.endUT, next.startUT)) return false;
            return true;
        }

        private static bool TryGetCommonSectionBody(
            Recording rec, TrackSection prev, TrackSection next, out string bodyName)
        {
            bodyName = null;
            string prevBody = GetSectionBody(rec, prev);
            string nextBody = GetSectionBody(rec, next);
            if (string.IsNullOrEmpty(prevBody) || string.IsNullOrEmpty(nextBody) || prevBody != nextBody)
                return false;
            bodyName = prevBody;
            return true;
        }

        private static string GetSectionBody(Recording rec, TrackSection section)
        {
            string body = GetSectionBody(section);
            if (!string.IsNullOrEmpty(body))
                return body;

            if (rec?.Points != null)
            {
                for (int i = 0; i < rec.Points.Count; i++)
                {
                    if (rec.Points[i].ut >= section.startUT && !string.IsNullOrEmpty(rec.Points[i].bodyName))
                        return rec.Points[i].bodyName;
                }

                for (int i = rec.Points.Count - 1; i >= 0; i--)
                {
                    if (!string.IsNullOrEmpty(rec.Points[i].bodyName))
                        return rec.Points[i].bodyName;
                }
            }

            return null;
        }

        private static bool IsAtmoSurfacePhasePair(string phaseA, string phaseB)
        {
            return (phaseA == "atmo" && phaseB == "surface")
                || (phaseA == "surface" && phaseB == "atmo");
        }

        private static bool IsAtmoSurfaceEnvironmentPair(SegmentEnvironment a, SegmentEnvironment b)
        {
            int classA = SplitEnvironmentClass(a);
            int classB = SplitEnvironmentClass(b);
            return (classA == 0 && classB == 2) || (classA == 2 && classB == 0);
        }

        private static bool HasContinuousBoundaryTiming(double earlierEndUT, double laterStartUT)
        {
            double delta = laterStartUT - earlierEndUT;
            if (delta > MaxEvaBoundaryGapSeconds) return false;
            if (delta < -MaxEvaBoundaryOverlapSeconds) return false;
            return true;
        }

        private static void NormalizeContinuousEvaBoundaryMerge(Recording target)
        {
            if (target.TrackSections != null && target.TrackSections.Count > 1)
            {
                target.TrackSections = FlightRecorder.StableSortByUT(target.TrackSections, s => s.startUT);
                TrimOverlappingSectionFrames(target.TrackSections);
            }

            bool rebuilt = RecordingStore.TrySyncFlatTrajectoryFromTrackSections(
                target, allowRelativeSections: true);

            if (!rebuilt && target.Points != null && target.Points.Count > 1)
                target.Points = FlightRecorder.StableSortByUT(target.Points, p => p.ut);
        }

        private static void TrimOverlappingSectionFrames(List<TrackSection> trackSections)
        {
            double? previousEndUT = null;

            for (int i = 0; i < trackSections.Count; i++)
            {
                var section = trackSections[i];
                if ((section.referenceFrame == ReferenceFrame.Absolute
                        || section.referenceFrame == ReferenceFrame.Relative)
                    && section.frames != null
                    && section.frames.Count > 0)
                {
                    section.frames = FlightRecorder.StableSortByUT(section.frames, p => p.ut);

                    if (previousEndUT.HasValue)
                    {
                        int firstKeep = 0;
                        while (firstKeep < section.frames.Count
                            && section.frames[firstKeep].ut <= previousEndUT.Value)
                        {
                            firstKeep++;
                        }

                        if (firstKeep >= section.frames.Count)
                            section.frames = new List<TrajectoryPoint>();
                        else if (firstKeep > 0)
                            section.frames = section.frames.GetRange(firstKeep, section.frames.Count - firstKeep);
                    }

                    TrimRelativeBodyFixedPrimaryForLeadingOverlap(ref section, previousEndUT);

                    if (section.frames.Count > 0)
                    {
                        section.startUT = section.frames[0].ut;
                        section.endUT = section.frames[section.frames.Count - 1].ut;
                        previousEndUT = section.endUT;
                    }
                }

                trackSections[i] = section;
            }
        }

        internal static int SplitEnvironmentClass(SegmentEnvironment env)
        {
            switch (env)
            {
                case SegmentEnvironment.Atmospheric: return AtmosphericSplitClass;
                case SegmentEnvironment.ExoPropulsive: return ExoSplitClass;
                case SegmentEnvironment.ExoBallistic: return ExoSplitClass;
                case SegmentEnvironment.SurfaceMobile: return SurfaceSplitClass;
                case SegmentEnvironment.SurfaceStationary: return SurfaceSplitClass;
                case SegmentEnvironment.Approach: return ApproachSplitClass;
                default: return (int)env;
            }
        }

        private static void PartitionPartEvents(List<PartEvent> source,
            List<PartEvent> target, double splitUT)
        {
            if (source == null) return;
            for (int i = source.Count - 1; i >= 0; i--)
            {
                if (source[i].ut >= splitUT)
                {
                    target.Insert(0, source[i]);
                    source.RemoveAt(i);
                }
            }
        }

        private static void PartitionSegmentEvents(List<SegmentEvent> source,
            List<SegmentEvent> target, double splitUT)
        {
            if (source == null) return;
            for (int i = source.Count - 1; i >= 0; i--)
            {
                if (source[i].ut >= splitUT)
                {
                    target.Insert(0, source[i]);
                    source.RemoveAt(i);
                }
            }
        }

        private static void PartitionFlagEvents(List<FlagEvent> source,
            List<FlagEvent> target, double splitUT)
        {
            if (source == null) return;
            for (int i = source.Count - 1; i >= 0; i--)
            {
                if (source[i].ut >= splitUT)
                {
                    target.Insert(0, source[i]);
                    source.RemoveAt(i);
                }
            }
        }

        private static void StripEventsPastUT(List<PartEvent> events, double ut)
        {
            if (events == null) return;
            for (int i = events.Count - 1; i >= 0; i--)
            {
                if (events[i].ut > ut) events.RemoveAt(i);
                else break;
            }
        }

        private static void StripEventsPastUT(List<SegmentEvent> events, double ut)
        {
            if (events == null) return;
            for (int i = events.Count - 1; i >= 0; i--)
            {
                if (events[i].ut > ut) events.RemoveAt(i);
                else break;
            }
        }

        private static void StripEventsPastUT(List<FlagEvent> events, double ut)
        {
            if (events == null) return;
            for (int i = events.Count - 1; i >= 0; i--)
            {
                if (events[i].ut > ut) events.RemoveAt(i);
                else break;
            }
        }

        private static bool GroupsEqual(List<string> a, List<string> b)
        {
            bool aEmpty = a == null || a.Count == 0;
            bool bEmpty = b == null || b.Count == 0;
            if (aEmpty && bEmpty) return true;
            if (aEmpty != bEmpty) return false;

            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        #endregion
    }
}
