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
    internal static class RecordingOptimizer
    {
        internal const double MaxEvaBoundaryGapSeconds = 2.0;
        internal const double MaxEvaBoundaryOverlapSeconds = 2.0;

        /// <summary>
        /// Can two consecutive chain segments be auto-merged?
        /// Returns false if any user-intent signal differs from defaults,
        /// if they have different phases/bodies (except continuous EVA atmo/surface),
        /// if a branch point separates them,
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
                    // Split where coarse environment class changes OR body changes (#251)
                    bool envChanged = SplitEnvironmentClass(rec.TrackSections[s].environment)
                        != SplitEnvironmentClass(rec.TrackSections[s - 1].environment);
                    bool bodyChanged = SectionBodyChanged(rec.TrackSections[s - 1], rec.TrackSections[s]);
                    if (!envChanged && !bodyChanged)
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
                    // Split where coarse environment class changes OR body changes (#251)
                    bool envChanged = SplitEnvironmentClass(rec.TrackSections[s].environment)
                        != SplitEnvironmentClass(rec.TrackSections[s - 1].environment);
                    bool bodyChanged = SectionBodyChanged(rec.TrackSections[s - 1], rec.TrackSections[s]);
                    if (!envChanged && !bodyChanged)
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

            // 10. Controllers: keep target's if present, else inherit
            if (target.Controllers == null && absorbed.Controllers != null)
                target.Controllers = absorbed.Controllers;

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
                        reputation = before.reputation + (after.reputation - before.reputation) * tf
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

            // 3b. Forward permanent visual state events as seeds in the second half.
            // Events like ShroudJettisoned/FairingJettisoned in the first half represent
            // state at the split point — the second half's ghost needs them to render correctly.
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

            // 7. Partition OrbitSegments by UT
            if (original.HasOrbitSegments)
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
                second.SegmentPhase = EnvironmentToPhase(env);
            }
            if (original.TrackSections.Count > 0)
            {
                var env = original.TrackSections[0].environment;
                original.SegmentPhase = EnvironmentToPhase(env);
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
            second.VesselSnapshot = original.VesselSnapshot;
            original.VesselSnapshot = null;

            // Resource manifests: first half keeps start, second half gets end (moved with VesselSnapshot)
            second.EndResources = original.EndResources;
            original.EndResources = null;
            // original.StartResources unchanged (keeps the recording-start resources)
            // second.StartResources stays null (no snapshot at environment boundary)

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

            // 11. Copy shared fields to both halves
            second.Controllers = original.Controllers != null
                ? new List<ControllerInfo>(original.Controllers) : null;
            second.AntennaSpecs = original.AntennaSpecs != null
                ? new List<AntennaSpec>(original.AntennaSpecs) : null;
            second.IsDebris = original.IsDebris;
            second.RecordingFormatVersion = original.RecordingFormatVersion;
            // Both halves are the same vessel — share Generation. (#284)
            second.Generation = original.Generation;
            // EVA linkage: both halves represent the same EVA kerbal
            second.EvaCrewName = original.EvaCrewName;
            second.ParentRecordingId = original.ParentRecordingId;

            bool syncedOriginalFlatTrajectory = RecordingStore.TrySyncFlatTrajectoryFromTrackSections(
                original, allowRelativeSections: true);
            bool syncedSecondFlatTrajectory = RecordingStore.TrySyncFlatTrajectoryFromTrackSections(
                second, allowRelativeSections: true);

            // Both halves now have their final trajectory/terminal payloads. Refresh the
            // persisted endpoint decision so optimizer outputs do not save stale or unknown data.
            RecordingEndpointResolver.RefreshEndpointDecision(original, "RecordingOptimizer.SplitAtSection.FirstHalf");
            RecordingEndpointResolver.RefreshEndpointDecision(second, "RecordingOptimizer.SplitAtSection.SecondHalf");

            // 12. Invalidate cached stats
            original.CachedStats = null;
            original.CachedStatsPointCount = 0;
            second.CachedStats = null;
            second.CachedStatsPointCount = 0;

            // 14. Clear explicit UT on both (Points define the range)
            original.ExplicitStartUT = double.NaN;
            original.ExplicitEndUT = double.NaN;
            second.ExplicitStartUT = double.NaN;
            second.ExplicitEndUT = double.NaN;

            ParsekLog.Info("Optimizer",
                $"SplitAtSection: split {original.RecordingId} at UT={splitUT:F1} " +
                $"(first: {original.Points.Count} pts/{original.TrackSections.Count} sections, " +
                $"second: {second.Points.Count} pts/{second.TrackSections.Count} sections, " +
                $"flatSync={syncedOriginalFlatTrajectory}/{syncedSecondFlatTrajectory})");

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
        /// Returns a coarse environment class for split decisions. ExoPropulsive and
        /// ExoBallistic are treated as the same class ("exo") — engine on/off cycles
        /// happen too frequently to be meaningful split boundaries. The optimizer splits
        /// at Atmospheric↔Exo, Exo↔Approach, Approach↔Surface transitions.
        /// Approach is its own class so landing/takeoff on airless bodies can be looped.
        /// </summary>

        /// <summary>
        /// Returns true if two adjacent TrackSections have different celestial bodies.
        /// Detects SOI transitions that should produce a recording split (#251).
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
                case SegmentEnvironment.Atmospheric: return 0;
                case SegmentEnvironment.ExoPropulsive: return 1;
                case SegmentEnvironment.ExoBallistic: return 1;
                case SegmentEnvironment.SurfaceMobile: return 2;
                case SegmentEnvironment.SurfaceStationary: return 2;
                case SegmentEnvironment.Approach: return 3;
                default: return (int)env;
            }
        }

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
                case SegmentEnvironment.Approach: return "approach";
                default: return "exo";
            }
        }

        /// <summary>
        /// Checks if a part event represents a permanent one-way visual state change
        /// that must be seeded in subsequent segments after a split.
        /// </summary>
        internal static bool IsPermanentVisualStateEvent(PartEventType type)
        {
            switch (type)
            {
                case PartEventType.ShroudJettisoned:
                case PartEventType.FairingJettisoned:
                case PartEventType.Decoupled:
                case PartEventType.Destroyed:
                case PartEventType.ParachuteDestroyed:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Copies permanent visual state events from the first half to the start of
        /// the second half as seed events at splitUT. This ensures the ghost for the
        /// second segment reflects the vessel's visual state at the split point
        /// (e.g., shroud already jettisoned, parts already decoupled).
        /// </summary>
        internal static void ForwardPermanentStateEvents(
            List<PartEvent> firstHalf, List<PartEvent> secondHalf, double splitUT)
        {
            if (firstHalf == null || firstHalf.Count == 0) return;

            int forwarded = 0;
            for (int i = 0; i < firstHalf.Count; i++)
            {
                if (!IsPermanentVisualStateEvent(firstHalf[i].eventType)) continue;

                var seed = firstHalf[i];
                seed.ut = splitUT;
                secondHalf.Insert(forwarded, seed);
                forwarded++;
            }

            if (forwarded > 0)
                ParsekLog.Info("Optimizer",
                    $"Forwarded {forwarded} permanent state event(s) as seeds at UT={splitUT:F1}");
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

        #region Boring tail trimming

        /// <summary>
        /// Default buffer to keep past the last interesting activity when trimming.
        /// </summary>
        internal const double DefaultTailBufferSeconds = 10.0;

        /// <summary>
        /// Minimum recording duration to be eligible for tail trimming.
        /// Recordings shorter than this are left untouched.
        /// </summary>
        internal const double MinDurationForTrimSeconds = 30.0;

        /// <summary>
        /// Returns true if the recording is a leaf — no child branch point and
        /// not a mid-chain segment with a successor.
        /// </summary>
        internal static bool IsLeafRecording(Recording rec, List<Recording> allRecordings)
        {
            // Breakup-continuous effective leaf: ChildBranchPointId is set but no
            // same-PID child exists — the recording IS the leaf for its vessel. (#224)
            if (rec.ChildBranchPointId != null
                && !GhostPlaybackLogic.IsEffectiveLeafForVessel(rec))
                return false;

            if (!string.IsNullOrEmpty(rec.ChainId) && rec.ChainIndex >= 0)
            {
                for (int i = 0; i < allRecordings.Count; i++)
                {
                    var other = allRecordings[i];
                    if (other == rec) continue;
                    if (other.ChainId == rec.ChainId && other.ChainBranch == 0
                        && other.ChainIndex > rec.ChainIndex)
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns true when a PartEvent is a zero-effect control-state seed that should
        /// not keep a long boring tail alive. Real positive-throttle / positive-power
        /// transitions still count as interesting activity.
        /// </summary>
        internal static bool IsInertPartEventForTailTrim(PartEvent evt)
        {
            switch (evt.eventType)
            {
                case PartEventType.EngineIgnited:
                case PartEventType.EngineThrottle:
                case PartEventType.RCSActivated:
                case PartEventType.RCSThrottle:
                    return evt.value <= 0f;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Finds the UT of the last interesting activity in a recording.
        /// Interesting = last non-boring TrackSection end, last non-inert PartEvent,
        /// last SegmentEvent, or last FlagEvent — whichever is latest. Returns NaN if
        /// nothing interesting found.
        /// </summary>
        internal static double FindLastInterestingUT(Recording rec)
        {
            double lastUT = double.NaN;

            // Last non-boring TrackSection
            if (rec.TrackSections != null)
            {
                for (int i = rec.TrackSections.Count - 1; i >= 0; i--)
                {
                    if (!GhostPlaybackLogic.IsBoringEnvironment(rec.TrackSections[i].environment))
                    {
                        lastUT = rec.TrackSections[i].endUT;
                        break;
                    }
                }
            }

            // Last non-inert PartEvent. Scans the full list and keeps the max UT so
            // the result is independent of PartEvents sort order — the previous
            // tail-backward break relied on an implicit sort-by-UT invariant
            // (StopRecording sorts, other paths may not).
            if (rec.PartEvents != null && rec.PartEvents.Count > 0)
            {
                for (int i = 0; i < rec.PartEvents.Count; i++)
                {
                    if (IsInertPartEventForTailTrim(rec.PartEvents[i]))
                        continue;

                    double evtUT = rec.PartEvents[i].ut;
                    if (double.IsNaN(lastUT) || evtUT > lastUT) lastUT = evtUT;
                }
            }

            // Last SegmentEvent. Scans the full list for the max UT so the
            // decision is independent of SegmentEvents sort order (same
            // hardening as the PartEvents branch above, #276).
            if (rec.SegmentEvents != null && rec.SegmentEvents.Count > 0)
            {
                for (int i = 0; i < rec.SegmentEvents.Count; i++)
                {
                    double evtUT = rec.SegmentEvents[i].ut;
                    if (double.IsNaN(lastUT) || evtUT > lastUT) lastUT = evtUT;
                }
            }

            // Last FlagEvent. Same full-scan hardening as above (#276).
            if (rec.FlagEvents != null && rec.FlagEvents.Count > 0)
            {
                for (int i = 0; i < rec.FlagEvents.Count; i++)
                {
                    double evtUT = rec.FlagEvents[i].ut;
                    if (double.IsNaN(lastUT) || evtUT > lastUT) lastUT = evtUT;
                }
            }

            return lastUT;
        }

        internal static bool TailPreservesTerminalSpawnState(Recording rec, double trimUT)
        {
            if (rec == null || !rec.TerminalStateValue.HasValue)
                return true;

            switch (rec.TerminalStateValue.Value)
            {
                case TerminalState.Orbiting:
                case TerminalState.SubOrbital:
                case TerminalState.Docked:
                    return TailMatchesTerminalOrbit(rec, trimUT);

                case TerminalState.Landed:
                case TerminalState.Splashed:
                    return TailMatchesTerminalSurfaceState(rec, trimUT);

                default:
                    return true;
            }
        }

        private static bool TailMatchesTerminalOrbit(Recording rec, double trimUT)
        {
            if (rec == null
                || string.IsNullOrEmpty(rec.TerminalOrbitBody)
                || rec.TerminalOrbitSemiMajorAxis <= 0.0)
            {
                return false;
            }

            bool sawTailOrbit = false;

            if (rec.OrbitSegments != null)
            {
                for (int i = 0; i < rec.OrbitSegments.Count; i++)
                {
                    OrbitSegment seg = rec.OrbitSegments[i];
                    if (seg.endUT <= trimUT)
                        continue;

                    sawTailOrbit = true;
                    if (!OrbitShapeMatchesTerminal(rec, seg))
                        return false;
                }
            }

            if (!sawTailOrbit && rec.TrackSections != null)
            {
                for (int i = 0; i < rec.TrackSections.Count; i++)
                {
                    TrackSection sec = rec.TrackSections[i];
                    if (sec.checkpoints == null)
                        continue;

                    for (int j = 0; j < sec.checkpoints.Count; j++)
                    {
                        OrbitSegment checkpoint = sec.checkpoints[j];
                        if (checkpoint.endUT <= trimUT)
                            continue;

                        sawTailOrbit = true;
                        if (!OrbitShapeMatchesTerminal(rec, checkpoint))
                            return false;
                    }
                }
            }

            return sawTailOrbit;
        }

        private static bool OrbitShapeMatchesTerminal(Recording rec, OrbitSegment seg)
        {
            if (string.IsNullOrEmpty(seg.bodyName) || seg.semiMajorAxis <= 0.0)
                return false;

            if (seg.bodyName != rec.TerminalOrbitBody)
                return false;

            if (seg.semiMajorAxis != rec.TerminalOrbitSemiMajorAxis)
                return false;

            if (seg.eccentricity != rec.TerminalOrbitEccentricity)
                return false;

            if (seg.inclination != rec.TerminalOrbitInclination)
                return false;

            if (seg.longitudeOfAscendingNode != rec.TerminalOrbitLAN)
                return false;

            if (seg.argumentOfPeriapsis != rec.TerminalOrbitArgumentOfPeriapsis)
                return false;

            return true;
        }

        private static bool TailMatchesTerminalSurfaceState(Recording rec, double trimUT)
        {
            if (!TryGetTerminalSurfaceReference(rec, out string terminalBody,
                out double terminalLat, out double terminalLon, out double terminalAlt,
                out Quaternion terminalRotation, out bool hasTerminalRotation))
            {
                ParsekLog.Verbose("Optimizer",
                    $"TailMatchesTerminalSurfaceState: no terminal surface reference " +
                    $"for rec '{rec?.RecordingId ?? "(null)"}'");
                return false;
            }

            bool sawTailPoint = false;
            int tailPointCount = 0;
            for (int i = 0; i < rec.Points.Count; i++)
            {
                TrajectoryPoint pt = rec.Points[i];
                if (pt.ut <= trimUT)
                    continue;

                sawTailPoint = true;
                tailPointCount++;
                if (!SurfacePointMatchesTerminal(pt, terminalBody, terminalLat, terminalLon,
                    terminalAlt, terminalRotation, hasTerminalRotation,
                    out string failReason))
                {
                    // Return on the first mismatch; the caller only needs a bool and
                    // the verbose log below already captures the failing point.
                    ParsekLog.Verbose("Optimizer",
                        $"TailMatchesTerminalSurfaceState: rec '{rec.RecordingId}' " +
                        $"point #{i} (ut={pt.ut.ToString("F2", CultureInfo.InvariantCulture)}) " +
                        $"diverges: {failReason} (tail scanned so far: {tailPointCount})");
                    return false;
                }
            }

            return sawTailPoint;
        }

        private static bool TryGetTerminalSurfaceReference(Recording rec,
            out string body, out double lat, out double lon, out double alt,
            out Quaternion rotation, out bool hasRotation)
        {
            body = null;
            lat = 0.0;
            lon = 0.0;
            alt = 0.0;
            rotation = Quaternion.identity;
            hasRotation = false;

            if (rec == null)
                return false;

            if (rec.TerminalPosition.HasValue)
            {
                SurfacePosition pos = rec.TerminalPosition.Value;
                body = pos.body;
                lat = pos.latitude;
                lon = pos.longitude;
                alt = pos.altitude;
                rotation = pos.rotation;
                hasRotation = pos.HasRecordedRotation && HasMeaningfulRotation(pos.rotation);
                if (pos.HasRecordedRotation && !hasRotation)
                {
                    ParsekLog.Verbose("Optimizer",
                        $"TryGetTerminalSurfaceReference: ignoring identity terminal rotation for '{rec.RecordingId ?? "(null)"}'");
                }
                return true;
            }

            if (rec.SurfacePos.HasValue)
            {
                SurfacePosition pos = rec.SurfacePos.Value;
                body = pos.body;
                lat = pos.latitude;
                lon = pos.longitude;
                alt = pos.altitude;
                rotation = pos.rotation;
                hasRotation = pos.HasRecordedRotation && HasMeaningfulRotation(pos.rotation);
                if (pos.HasRecordedRotation && !hasRotation)
                {
                    ParsekLog.Verbose("Optimizer",
                        $"TryGetTerminalSurfaceReference: ignoring identity terminal rotation for '{rec.RecordingId ?? "(null)"}'");
                }
                return true;
            }

            if (rec.Points == null || rec.Points.Count == 0)
                return false;

            TrajectoryPoint lastPt = rec.Points[rec.Points.Count - 1];
            body = lastPt.bodyName;
            lat = lastPt.latitude;
            lon = lastPt.longitude;
            alt = lastPt.altitude;
            rotation = lastPt.rotation;
            hasRotation = HasMeaningfulRotation(lastPt.rotation);
            return true;
        }

        // Tolerance for "tail still matches terminal surface state" checks. A landed
        // vessel at rest has non-trivial jitter in position and rotation — floating
        // point drift, repeated pack/unpack transitions, on-rails terrain snapping,
        // and time-warp re-anchoring can shift the sampled lat/lon/alt by a few
        // meters across a 15-minute idle tail even though the vessel is "at rest"
        // from the player's POV. The previous 1e-6° / 0.25 m / 0.5° tolerances
        // were tight enough to still fail on every real playtest recording. The
        // current values are sized to absorb normal idle drift while still rejecting
        // actual movement: a driving rover covers >1e-4° per second and >1 m altitude
        // when it goes over a bump, so a 10+ second buffer of real movement is well
        // over these thresholds. The post-trim divergence between the ghost's last
        // playback position and the captured TerminalPosition is bounded by these
        // tolerances and is small enough that the visual jump at ghost end is not
        // noticeable.
        internal const double TailPositionLatLonEpsilonDeg = 1e-4;  // ~11 m at Kerbin's equator
        internal const double TailAltitudeEpsilonMeters = 5.0;
        internal const float TailRotationEpsilonDegrees = 5.0f;

        private static bool SurfacePointMatchesTerminal(TrajectoryPoint pt,
            string terminalBody, double terminalLat, double terminalLon, double terminalAlt,
            Quaternion terminalRotation, bool hasTerminalRotation,
            out string failReason)
        {
            failReason = null;
            string pointBody = pt.bodyName;
            if (!string.IsNullOrEmpty(terminalBody) || !string.IsNullOrEmpty(pointBody))
            {
                if ((terminalBody ?? string.Empty) != (pointBody ?? string.Empty))
                {
                    failReason = $"body mismatch (point='{pointBody ?? "null"}' vs terminal='{terminalBody ?? "null"}')";
                    return false;
                }
            }

            double latDelta = pt.latitude - terminalLat;
            if (System.Math.Abs(latDelta) > TailPositionLatLonEpsilonDeg)
            {
                failReason = $"lat delta {latDelta.ToString("R", CultureInfo.InvariantCulture)} > eps {TailPositionLatLonEpsilonDeg.ToString("R", CultureInfo.InvariantCulture)}";
                return false;
            }

            double lonDelta = pt.longitude - terminalLon;
            if (System.Math.Abs(lonDelta) > TailPositionLatLonEpsilonDeg)
            {
                failReason = $"lon delta {lonDelta.ToString("R", CultureInfo.InvariantCulture)} > eps {TailPositionLatLonEpsilonDeg.ToString("R", CultureInfo.InvariantCulture)}";
                return false;
            }

            double altDelta = pt.altitude - terminalAlt;
            if (System.Math.Abs(altDelta) > TailAltitudeEpsilonMeters)
            {
                failReason = $"alt delta {altDelta.ToString("F3", CultureInfo.InvariantCulture)}m > eps {TailAltitudeEpsilonMeters.ToString("F3", CultureInfo.InvariantCulture)}m";
                return false;
            }

            bool pointHasRotation = HasMeaningfulRotation(pt.rotation);
            if (hasTerminalRotation || pointHasRotation)
            {
                if (!(hasTerminalRotation && pointHasRotation))
                {
                    failReason = $"rotation presence mismatch (pt.has={pointHasRotation} terminal.has={hasTerminalRotation})";
                    return false;
                }

                Quaternion pointRot = TrajectoryMath.SanitizeQuaternion(pt.rotation);
                Quaternion terminalRot = TrajectoryMath.SanitizeQuaternion(terminalRotation);
                float rotAngle = Quaternion.Angle(pointRot, terminalRot);
                if (rotAngle > TailRotationEpsilonDegrees)
                {
                    failReason = $"rot angle {rotAngle.ToString("F2", CultureInfo.InvariantCulture)}° > eps {TailRotationEpsilonDegrees.ToString("F2", CultureInfo.InvariantCulture)}°";
                    return false;
                }
            }

            return true;
        }

        private static bool HasMeaningfulRotation(Quaternion rotation)
        {
            if (rotation.x == 0f && rotation.y == 0f && rotation.z == 0f && rotation.w == 0f)
                return false;

            Quaternion sanitized = TrajectoryMath.SanitizeQuaternion(rotation);
            return Quaternion.Angle(sanitized, Quaternion.identity) > 1e-4f;
        }

        /// <summary>
        /// Trims the boring tail of a leaf recording. If the recording ends with a long
        /// idle period (ExoBallistic or SurfaceStationary), removes trailing points and
        /// sections past bufferSeconds after the last interesting activity.
        /// Returns true if the recording was trimmed.
        /// </summary>
        internal static bool TrimBoringTail(Recording rec, List<Recording> allRecordings,
            double bufferSeconds = DefaultTailBufferSeconds)
        {
            if (rec == null || rec.Points.Count < 2) return false;

            // Only trim leaf recordings
            if (!IsLeafRecording(rec, allRecordings)) return false;

            // Recording must be long enough to warrant trimming
            double duration = rec.EndUT - rec.StartUT;
            if (duration <= MinDurationForTrimSeconds) return false;

            // Must have TrackSections to detect boring tails
            if (rec.TrackSections == null || rec.TrackSections.Count == 0) return false;

            // Last section must be boring
            var lastSection = rec.TrackSections[rec.TrackSections.Count - 1];
            if (!GhostPlaybackLogic.IsBoringEnvironment(lastSection.environment)) return false;

            double lastInterestingUT = FindLastInterestingUT(rec);
            if (double.IsNaN(lastInterestingUT))
            {
                // Entire recording is boring (no non-boring sections, no events).
                // This happens after optimizer splits produce an all-SurfaceStationary
                // or all-ExoBallistic leaf. Trim to a minimal window from the start
                // so the ghost finishes quickly and the real vessel spawns promptly.
                // Use the second point's UT as reference to guarantee at least 2 points
                // survive for valid interpolation (the trim logic requires keepCount >= 2).
                if (rec.Points.Count < 3) return false; // too few to trim meaningfully
                lastInterestingUT = rec.Points[1].ut;
            }

            double trimUT = lastInterestingUT + bufferSeconds;
            if (trimUT >= rec.EndUT) return false; // boring tail is shorter than buffer

            if (!TailPreservesTerminalSpawnState(rec, trimUT))
            {
                ParsekLog.Verbose("Optimizer",
                    $"TrimBoringTail: skipped '{rec.VesselName}' ({rec.RecordingId}) " +
                    $"because tail still diverges from terminal state after trimUT={trimUT:F1} " +
                    $"(terminal={rec.TerminalStateValue?.ToString() ?? "null"})");
                return false;
            }

            double originalEndUT = rec.EndUT;
            int originalPointCount = rec.Points.Count;

            // Trim Points — find first point past trimUT, keep everything before it
            int keepCount = 0;
            for (int i = 0; i < rec.Points.Count; i++)
            {
                if (rec.Points[i].ut > trimUT)
                {
                    keepCount = i;
                    break;
                }
            }
            // No points past trimUT — nothing to trim
            if (keepCount == 0) return false;
            // Must keep at least 2 points for valid interpolation
            if (keepCount < 2) return false;
            rec.Points.RemoveRange(keepCount, rec.Points.Count - keepCount);

            // Trim TrackSections — remove trailing sections past trimUT, shorten spanning section
            for (int i = rec.TrackSections.Count - 1; i >= 0; i--)
            {
                var sec = rec.TrackSections[i];
                if (sec.startUT >= trimUT)
                {
                    rec.TrackSections.RemoveAt(i);
                }
                else if (sec.endUT > trimUT)
                {
                    if (TryTrimTrackSectionPayload(ref sec, trimUT))
                        rec.TrackSections[i] = sec;
                    else
                        rec.TrackSections.RemoveAt(i);
                    break;
                }
                else break;
            }

            // Trim OrbitSegments past trimUT
            if (rec.OrbitSegments != null)
            {
                for (int i = rec.OrbitSegments.Count - 1; i >= 0; i--)
                {
                    var os = rec.OrbitSegments[i];
                    if (os.startUT >= trimUT)
                    {
                        rec.OrbitSegments.RemoveAt(i);
                    }
                    else if (os.endUT > trimUT)
                    {
                        os.endUT = trimUT;
                        rec.OrbitSegments[i] = os;
                        break;
                    }
                    else break;
                }
            }

            RecordingStore.TrySyncFlatTrajectoryFromTrackSections(rec, allowRelativeSections: true);

            // Clamp ExplicitEndUT to the new trajectory bounds. Without this, the
            // Recording.EndUT getter falls back to the old finalize-time
            // ExplicitEndUT (set by the recorder when scene exit fires), so rec.EndUT
            // stays at the original value even though the trim physically removed all
            // points, sections, and events past trimUT. That's what the player sees
            // as "trim not applied": the Recordings table keeps showing the full
            // pre-trim duration and ghost playback treats the recording as lasting
            // until the stale ExplicitEndUT. Setting ExplicitEndUT = NaN lets the
            // getter use the now-authoritative actual-trajectory bounds (max of last
            // Points[].ut, last TrackSection endUT, last OrbitSegment endUT).
            rec.ExplicitEndUT = double.NaN;

            // Strip events past the new EndUT (they're inert during playback but
            // waste memory and disk space in serialized sidecar files)
            double newEndUT = rec.EndUT;
            StripEventsPastUT(rec.PartEvents, newEndUT);
            StripEventsPastUT(rec.SegmentEvents, newEndUT);
            StripEventsPastUT(rec.FlagEvents, newEndUT);

            // Invalidate cached stats
            rec.CachedStats = null;
            rec.CachedStatsPointCount = 0;

            double removedSeconds = originalEndUT - newEndUT;
            int removedPoints = originalPointCount - rec.Points.Count;
            ParsekLog.Info("Optimizer",
                $"TrimBoringTail: trimmed '{rec.VesselName}' ({rec.RecordingId}) " +
                $"from endUT={originalEndUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"to {rec.EndUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"(removed {removedSeconds.ToString("F1", CultureInfo.InvariantCulture)}s, {removedPoints} points; " +
                $"trimUT={trimUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"lastInterestingUT={lastInterestingUT.ToString("F1", CultureInfo.InvariantCulture)})");
            return true;
        }

        private static bool TryTrimTrackSectionPayload(ref TrackSection sec, double trimUT)
        {
            switch (sec.referenceFrame)
            {
                case ReferenceFrame.OrbitalCheckpoint:
                    if (sec.frames != null && sec.frames.Count > 0)
                        TrimCheckpointFramesAtUT(sec.frames, trimUT);

                    if (sec.checkpoints == null || sec.checkpoints.Count == 0)
                    {
                        sec.endUT = trimUT;
                        return true;
                    }

                    for (int i = sec.checkpoints.Count - 1; i >= 0; i--)
                    {
                        var checkpoint = sec.checkpoints[i];
                        if (checkpoint.startUT >= trimUT)
                        {
                            sec.checkpoints.RemoveAt(i);
                        }
                        else if (checkpoint.endUT > trimUT)
                        {
                            checkpoint.endUT = trimUT;
                            sec.checkpoints[i] = checkpoint;
                            break;
                        }
                        else break;
                    }

                    if (sec.checkpoints.Count == 0)
                        return false;

                    sec.endUT = sec.checkpoints[sec.checkpoints.Count - 1].endUT;
                    return true;

                default:
                    if (sec.frames == null || sec.frames.Count == 0)
                    {
                        sec.endUT = trimUT;
                        return true;
                    }

                    for (int i = sec.frames.Count - 1; i >= 0; i--)
                    {
                        if (sec.frames[i].ut > trimUT)
                            sec.frames.RemoveAt(i);
                        else
                            break;
                    }

                    if (sec.frames.Count == 0)
                        return false;

                    sec.endUT = sec.frames[sec.frames.Count - 1].ut;
                    return true;
            }
        }

        private static void TrimCheckpointFramesAtUT(List<TrajectoryPoint> frames, double trimUT)
        {
            if (frames == null || frames.Count == 0)
                return;

            int firstAfter = -1;
            for (int i = 0; i < frames.Count; i++)
            {
                if (frames[i].ut > trimUT)
                {
                    firstAfter = i;
                    break;
                }
            }

            if (firstAfter < 0)
                return;

            TrajectoryPoint? trimPoint = null;
            if (firstAfter > 0)
            {
                TrajectoryPoint before = frames[firstAfter - 1];
                TrajectoryPoint after = frames[firstAfter];
                if (before.ut < trimUT && after.ut > trimUT)
                    trimPoint = InterpolateCheckpointFrame(before, after, trimUT);
            }

            frames.RemoveRange(firstAfter, frames.Count - firstAfter);
            if (trimPoint.HasValue)
                frames.Add(trimPoint.Value);
        }

        private static TrajectoryPoint InterpolateCheckpointFrame(
            TrajectoryPoint before,
            TrajectoryPoint after,
            double targetUT)
        {
            double duration = after.ut - before.ut;
            double t = duration > 0.0001
                ? (targetUT - before.ut) / duration
                : 0.0;
            if (t < 0.0) t = 0.0;
            if (t > 1.0) t = 1.0;
            float tf = (float)t;

            return new TrajectoryPoint
            {
                ut = targetUT,
                latitude = before.latitude + (after.latitude - before.latitude) * t,
                longitude = before.longitude + (after.longitude - before.longitude) * t,
                altitude = before.altitude + (after.altitude - before.altitude) * t,
                rotation = Quaternion.Slerp(before.rotation, after.rotation, tf),
                velocity = Vector3.Lerp(before.velocity, after.velocity, tf),
                bodyName = !string.IsNullOrEmpty(after.bodyName) ? after.bodyName : before.bodyName,
                funds = before.funds + (after.funds - before.funds) * t,
                science = before.science + (after.science - before.science) * tf,
                reputation = before.reputation + (after.reputation - before.reputation) * tf
            };
        }

        #endregion
    }
}
