using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Pure static logic for recording optimization: merge redundant segments,
    /// split monolithic recordings at TrackSection boundaries.
    /// All methods are internal static for direct testability.
    /// </summary>
    internal static class RecordingOptimizer
    {
        /// <summary>
        /// Can two consecutive chain segments be auto-merged?
        /// Returns false if any user-intent signal differs from defaults,
        /// if they have different phases/bodies, if a branch point separates them,
        /// or if either has ghosting-trigger part events (snapshot would be wrong).
        /// </summary>
        internal static bool CanAutoMerge(Recording a, Recording b)
        {
            if (a == null || b == null) return false;

            // Must be in the same chain, consecutive, primary branch
            if (string.IsNullOrEmpty(a.ChainId) || a.ChainId != b.ChainId) return false;
            if (a.ChainIndex < 0 || b.ChainIndex < 0) return false;
            if (b.ChainIndex != a.ChainIndex + 1) return false;
            if (a.ChainBranch != 0 || b.ChainBranch != 0) return false;

            // No branch point between them
            if (!string.IsNullOrEmpty(a.ChildBranchPointId)) return false;

            // Same phase and body
            if (a.SegmentPhase != b.SegmentPhase) return false;
            if (a.SegmentBodyName != b.SegmentBodyName) return false;

            // Neither has ghosting-trigger events (snapshot would be wrong for merged recording)
            if (GhostingTriggerClassifier.HasGhostingTriggerEvents(a)) return false;
            if (GhostingTriggerClassifier.HasGhostingTriggerEvents(b)) return false;

            // User intent: any non-default setting blocks merge
            if (a.LoopPlayback || b.LoopPlayback) return false;
            if (!double.IsNaN(a.LoopStartUT) || !double.IsNaN(a.LoopEndUT)) return false;
            if (!double.IsNaN(b.LoopStartUT) || !double.IsNaN(b.LoopEndUT)) return false;
            if (!a.PlaybackEnabled || !b.PlaybackEnabled) return false;
            if (a.Hidden || b.Hidden) return false;
            if (a.LoopIntervalSeconds != 10.0 || b.LoopIntervalSeconds != 10.0) return false;
            if (a.LoopAnchorVesselId != 0 || b.LoopAnchorVesselId != 0) return false;

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
        /// at TrackSection boundaries where the environment changes.
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
                    // Only split where environment changes
                    if (rec.TrackSections[s].environment == rec.TrackSections[s - 1].environment)
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
        /// Same as FindSplitCandidates but uses CanAutoSplitIgnoringGhostTriggers.
        /// Used by the optimizer split pass where ghosting triggers don't block splitting.
        /// </summary>
        internal static List<(int, int)> FindSplitCandidatesForOptimizer(List<Recording> committed)
        {
            var candidates = new List<(int, int)>();
            if (committed == null) return candidates;

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec.TrackSections == null || rec.TrackSections.Count < 2) continue;

                for (int s = 1; s < rec.TrackSections.Count; s++)
                {
                    // Only split where environment changes
                    if (rec.TrackSections[s].environment == rec.TrackSections[s - 1].environment)
                        continue;

                    if (CanAutoSplitIgnoringGhostTriggers(rec, s))
                    {
                        candidates.Add((i, s));
                        break; // One split per recording per pass (re-scan after split)
                    }
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
            // 1. Concatenate Points (already UT-ordered within each recording)
            if (absorbed.Points != null && absorbed.Points.Count > 0)
                target.Points.AddRange(absorbed.Points);

            // 2. Merge + re-sort PartEvents by UT
            if (absorbed.PartEvents != null && absorbed.PartEvents.Count > 0)
            {
                target.PartEvents.AddRange(absorbed.PartEvents);
                target.PartEvents.Sort((a, b) => a.ut.CompareTo(b.ut));
            }

            // 3. Merge + re-sort SegmentEvents by UT
            if (absorbed.SegmentEvents != null && absorbed.SegmentEvents.Count > 0)
            {
                target.SegmentEvents.AddRange(absorbed.SegmentEvents);
                target.SegmentEvents.Sort((a, b) => a.ut.CompareTo(b.ut));
            }

            // 4. Concatenate TrackSections
            if (absorbed.TrackSections != null && absorbed.TrackSections.Count > 0)
                target.TrackSections.AddRange(absorbed.TrackSections);

            // 5. Merge OrbitSegments
            if (absorbed.OrbitSegments != null && absorbed.OrbitSegments.Count > 0)
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

            // 8. TerminalState: absorbed is the later segment, inherit its terminal state
            if (absorbed.TerminalStateValue.HasValue)
                target.TerminalStateValue = absorbed.TerminalStateValue;

            // 9. Clear explicit UT ranges (Points now cover the full range)
            target.ExplicitStartUT = double.NaN;
            target.ExplicitEndUT = double.NaN;

            // 10. Controllers: keep target's if present, else inherit
            if (target.Controllers == null && absorbed.Controllers != null)
                target.Controllers = absorbed.Controllers;

            // 11. AntennaSpecs: keep target's if present, else inherit
            if (target.AntennaSpecs == null && absorbed.AntennaSpecs != null)
                target.AntennaSpecs = absorbed.AntennaSpecs;

            // 12. Invalidate ghost geometry (covers only first half's vessel config)
            target.GhostGeometryAvailable = false;
            target.GhostGeometryRelativePath = null;

            // 13. Invalidate cached stats
            target.CachedStats = null;
            target.CachedStatsPointCount = 0;

            ParsekLog.Info("Optimizer",
                $"MergeInto: absorbed {absorbed.RecordingId} into {target.RecordingId} " +
                $"(target now has {target.Points.Count} points, {target.TrackSections.Count} sections)");

            return absorbed.RecordingId;
        }

        /// <summary>
        /// Splits a recording at the given TrackSection boundary index.
        /// Returns the new Recording (second half). The original is mutated to keep the first half.
        /// Caller must assign chain linkage, save files, and add to store.
        /// </summary>
        internal static Recording SplitAtSection(Recording original, int sectionIndex)
        {
            double splitUT = original.TrackSections[sectionIndex].startUT;

            var second = new Recording();

            // 1-2. Partition Points by UT
            int splitPointIdx = 0;
            for (int i = 0; i < original.Points.Count; i++)
            {
                if (original.Points[i].ut >= splitUT) { splitPointIdx = i; break; }
            }
            second.Points = new List<TrajectoryPoint>(
                original.Points.GetRange(splitPointIdx, original.Points.Count - splitPointIdx));
            original.Points.RemoveRange(splitPointIdx, original.Points.Count - splitPointIdx);

            // 3. Partition PartEvents by UT
            PartitionPartEvents(original.PartEvents, second.PartEvents, splitUT);

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

            // 7. Partition OrbitSegments by UT
            if (original.OrbitSegments != null && original.OrbitSegments.Count > 0)
            {
                second.OrbitSegments = new List<OrbitSegment>();
                for (int i = original.OrbitSegments.Count - 1; i >= 0; i--)
                {
                    if (original.OrbitSegments[i].startUT >= splitUT)
                    {
                        second.OrbitSegments.Insert(0, original.OrbitSegments[i]);
                        original.OrbitSegments.RemoveAt(i);
                    }
                }
            }

            // 8. Clone GhostVisualSnapshot (safe: CanAutoSplit ensures no ghosting triggers)
            if (original.GhostVisualSnapshot != null)
                second.GhostVisualSnapshot = original.GhostVisualSnapshot.CreateCopy();

            // 9. Tag SegmentPhase from environment
            if (second.TrackSections.Count > 0)
            {
                var env = second.TrackSections[0].environment;
                second.SegmentPhase = EnvironmentToPhase(env);
            }
            if (original.TrackSections.Count > 0)
            {
                var env = original.TrackSections[0].environment;
                original.SegmentPhase = EnvironmentToPhase(env);
            }

            second.SegmentBodyName = original.SegmentBodyName;

            // 10. Transfer terminal-state fields to second half (represents end-of-recording state)
            second.VesselSnapshot = original.VesselSnapshot;
            original.VesselSnapshot = null;

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

            // 11. Copy shared fields to both halves
            second.Controllers = original.Controllers != null
                ? new List<ControllerInfo>(original.Controllers) : null;
            second.AntennaSpecs = original.AntennaSpecs != null
                ? new List<AntennaSpec>(original.AntennaSpecs) : null;
            second.IsDebris = original.IsDebris;
            second.RecordingFormatVersion = original.RecordingFormatVersion;

            // 12. Invalidate ghost geometry on both
            original.GhostGeometryAvailable = false;
            original.GhostGeometryRelativePath = null;
            second.GhostGeometryAvailable = false;

            // 13. Invalidate cached stats
            original.CachedStats = null;
            original.CachedStatsPointCount = 0;

            // 14. Clear explicit UT on both (Points define the range)
            original.ExplicitStartUT = double.NaN;
            original.ExplicitEndUT = double.NaN;
            second.ExplicitStartUT = double.NaN;
            second.ExplicitEndUT = double.NaN;

            ParsekLog.Info("Optimizer",
                $"SplitAtSection: split {original.RecordingId} at UT={splitUT:F1} " +
                $"(first: {original.Points.Count} pts/{original.TrackSections.Count} sections, " +
                $"second: {second.Points.Count} pts/{second.TrackSections.Count} sections)");

            return second;
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

        /// <summary>
        /// Maps a SegmentEnvironment to a phase tag for post-split recordings.
        /// Only used by SplitAtSection — not a general-purpose mapping.
        /// </summary>
        private static string EnvironmentToPhase(SegmentEnvironment env)
        {
            switch (env)
            {
                case SegmentEnvironment.Atmospheric: return "atmo";
                case SegmentEnvironment.SurfaceMobile: return "surface";
                case SegmentEnvironment.SurfaceStationary: return "surface";
                default: return "exo";
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
