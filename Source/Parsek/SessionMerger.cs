using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Highest-fidelity-wins merge algorithm. Takes a RecordingTree and produces
    /// one merged Recording per vessel with non-overlapping TrackSections selected
    /// by source priority: Active > Background > Checkpoint.
    /// </summary>
    internal static class SessionMerger
    {
        private const string Tag = "Merger";

        /// <summary>
        /// Stock KSP body equatorial radii in meters. Used by ComputeBoundaryDiscontinuity
        /// for lat/lon-to-meters conversion on the correct body.
        /// Modded bodies (RSS, Kopernicus) fall back to Kerbin radius — acceptable for
        /// this diagnostic-only calculation.
        /// </summary>
        private static readonly Dictionary<string, double> BodyRadii = new Dictionary<string, double>
        {
            { "Kerbol", 261600000 },
            { "Moho", 250000 },
            { "Eve", 700000 },
            { "Gilly", 13000 },
            { "Kerbin", 600000 },
            { "Mun", 200000 },
            { "Minmus", 60000 },
            { "Duna", 320000 },
            { "Ike", 130000 },
            { "Dres", 138000 },
            { "Jool", 6000000 },
            { "Laythe", 500000 },
            { "Vall", 300000 },
            { "Tylo", 600000 },
            { "Bop", 65000 },
            { "Pol", 44000 },
            { "Eeloo", 210000 }
        };

        /// <summary>
        /// Returns the equatorial radius for a stock KSP body.
        /// Falls back to Kerbin radius (600,000m) for null or unknown body names.
        /// </summary>
        private static double GetBodyRadius(string bodyName)
        {
            if (bodyName == null)
                return 600000.0;
            if (BodyRadii.TryGetValue(bodyName, out double radius))
                return radius;
            return 600000.0;
        }

        /// <summary>
        /// Merges all recordings in the tree. For each recording, resolves overlapping
        /// TrackSections by source priority and merges PartEvents with deduplication.
        /// Returns a dictionary mapping recordingId to a clean merged Recording.
        /// </summary>
        internal static Dictionary<string, Recording> MergeTree(RecordingTree tree)
        {
            var ic = CultureInfo.InvariantCulture;
            var result = new Dictionary<string, Recording>();

            if (tree == null)
            {
                ParsekLog.Warn(Tag, "MergeTree: null tree, returning empty result");
                return result;
            }

            if (tree.Recordings.Count == 0)
            {
                ParsekLog.Info(Tag, "MergeTree: tree has no recordings, returning empty result");
                return result;
            }

            ParsekLog.Info(Tag,
                $"MergeTree: starting merge for tree='{tree.TreeName}' recordings={tree.Recordings.Count}");

            foreach (var kvp in tree.Recordings)
            {
                string recId = kvp.Key;
                Recording srcRec = kvp.Value;

                int inputSectionCount = srcRec.TrackSections != null ? srcRec.TrackSections.Count : 0;

                // Resolve overlapping TrackSections
                List<TrackSection> mergedSections = ResolveOverlaps(
                    srcRec.TrackSections ?? new List<TrackSection>());

                // Merge PartEvents (source recording may only have one list, just deduplicate+sort)
                List<PartEvent> mergedEvents = MergePartEvents(
                    srcRec.PartEvents ?? new List<PartEvent>(),
                    new List<PartEvent>());

                // Build merged Recording
                var merged = new Recording();
                merged.RecordingId = srcRec.RecordingId;
                merged.VesselName = srcRec.VesselName;
                merged.TreeId = srcRec.TreeId;
                merged.VesselPersistentId = srcRec.VesselPersistentId;
                merged.RecordingFormatVersion = srcRec.RecordingFormatVersion;
                merged.TrackSections = mergedSections;
                merged.PartEvents = mergedEvents;

                // Copy flat lists for backward compat
                if (srcRec.Points != null)
                    merged.Points = new List<TrajectoryPoint>(srcRec.Points);
                if (srcRec.OrbitSegments != null)
                    merged.OrbitSegments = new List<OrbitSegment>(srcRec.OrbitSegments);

                // Copy SegmentEvents
                if (srcRec.SegmentEvents != null)
                    merged.SegmentEvents = new List<SegmentEvent>(srcRec.SegmentEvents);

                // Copy terminal/linkage state
                merged.TerminalStateValue = srcRec.TerminalStateValue;
                merged.ParentBranchPointId = srcRec.ParentBranchPointId;
                merged.ChildBranchPointId = srcRec.ChildBranchPointId;
                merged.ExplicitStartUT = srcRec.ExplicitStartUT;
                merged.ExplicitEndUT = srcRec.ExplicitEndUT;
                if (srcRec.Controllers != null)
                    merged.Controllers = new List<ControllerInfo>(srcRec.Controllers);
                merged.IsDebris = srcRec.IsDebris;

                result[recId] = merged;

                LogMergeDiagnostics(recId, srcRec.VesselName, inputSectionCount,
                    srcRec.TrackSections ?? new List<TrackSection>(), mergedSections);
            }

            ParsekLog.Info(Tag,
                $"MergeTree: completed merge for tree='{tree.TreeName}' merged={result.Count} recordings");

            return result;
        }

        /// <summary>
        /// Logs diagnostics for a single recording's merge result: section counts by source,
        /// overlaps resolved, and boundary discontinuity warnings.
        /// </summary>
        private static void LogMergeDiagnostics(
            string recId, string vesselName, int inputSectionCount,
            List<TrackSection> inputSections, List<TrackSection> mergedSections)
        {
            var ic = CultureInfo.InvariantCulture;

            int outputSectionCount = mergedSections.Count;
            int overlapsResolved = inputSectionCount > outputSectionCount
                ? inputSectionCount - outputSectionCount : 0;
            int overlapCount = CountOverlapsResolved(inputSections, mergedSections);

            int activeCount = 0, bgCount = 0, cpCount = 0;
            for (int i = 0; i < mergedSections.Count; i++)
            {
                switch (mergedSections[i].source)
                {
                    case TrackSectionSource.Active: activeCount++; break;
                    case TrackSectionSource.Background: bgCount++; break;
                    case TrackSectionSource.Checkpoint: cpCount++; break;
                }
            }

            ParsekLog.Info(Tag,
                $"MergeTree: vessel='{vesselName}' id={recId} " +
                $"inputSections={inputSectionCount} outputSections={outputSectionCount} " +
                $"overlapsResolved={overlapCount} " +
                $"active={activeCount} background={bgCount} checkpoint={cpCount}");

            // Log discontinuity warnings
            for (int i = 0; i < mergedSections.Count; i++)
            {
                float disc = mergedSections[i].boundaryDiscontinuityMeters;
                if (disc > 1.0f)
                {
                    ParsekLog.Warn(Tag,
                        $"MergeTree: boundary discontinuity={disc.ToString("F2", ic)}m " +
                        $"at section[{i}] ut={mergedSections[i].startUT.ToString("F2", ic)} " +
                        $"vessel='{vesselName}'");
                }
            }
        }

        /// <summary>
        /// Resolves overlapping TrackSections by highest-fidelity-wins priority.
        /// Active(0) > Background(1) > Checkpoint(2) — lower enum value wins.
        /// Returns non-overlapping sections sorted by startUT with boundary
        /// discontinuity computed at each junction.
        /// </summary>
        internal static List<TrackSection> ResolveOverlaps(List<TrackSection> sections)
        {
            var ic = CultureInfo.InvariantCulture;

            if (sections == null || sections.Count == 0)
            {
                ParsekLog.Verbose(Tag, "ResolveOverlaps: empty input, returning empty list");
                return new List<TrackSection>();
            }

            // Sort by startUT, then by priority (lower source value = higher priority = first)
            var sorted = new List<TrackSection>(sections);
            sorted.Sort((a, b) =>
            {
                int cmp = a.startUT.CompareTo(b.startUT);
                if (cmp != 0) return cmp;
                return ((int)a.source).CompareTo((int)b.source);
            });

            var output = new List<TrackSection>();

            for (int i = 0; i < sorted.Count; i++)
            {
                TrackSection current = sorted[i];

                // Skip sections with zero or negative duration
                if (current.endUT <= current.startUT)
                {
                    ParsekLog.Verbose(Tag,
                        $"ResolveOverlaps: skipping zero-duration section at ut={current.startUT.ToString("F2", ic)} " +
                        $"source={current.source}");
                    continue;
                }

                if (output.Count == 0)
                {
                    output.Add(current);
                    continue;
                }

                // Check against all existing output sections for overlaps
                var newOutput = new List<TrackSection>();

                for (int j = 0; j < output.Count; j++)
                {
                    TrackSection existing = output[j];

                    // No overlap: current is entirely after existing
                    if (current.startUT >= existing.endUT)
                    {
                        newOutput.Add(existing);
                        continue;
                    }

                    // No overlap: current is entirely before existing
                    if (current.endUT <= existing.startUT)
                    {
                        newOutput.Add(existing);
                        continue;
                    }

                    // Overlap detected — resolve by priority
                    bool currentWins = (int)current.source < (int)existing.source;

                    if (currentWins)
                    {
                        // Current has higher priority — trim existing around current
                        ParsekLog.Verbose(Tag,
                            $"ResolveOverlaps: {current.source} overrides {existing.source} " +
                            $"at [{current.startUT.ToString("F2", ic)},{current.endUT.ToString("F2", ic)}]");

                        // Part of existing before current
                        if (existing.startUT < current.startUT)
                        {
                            TrackSection before = existing;
                            before.endUT = current.startUT;
                            before.frames = TrimFrames(existing.frames, existing.startUT, current.startUT);
                            before.checkpoints = TrimCheckpoints(existing.checkpoints, existing.startUT, current.startUT);
                            newOutput.Add(before);
                        }

                        // Part of existing after current
                        if (existing.endUT > current.endUT)
                        {
                            TrackSection after = existing;
                            after.startUT = current.endUT;
                            after.frames = TrimFrames(existing.frames, current.endUT, existing.endUT);
                            after.checkpoints = TrimCheckpoints(existing.checkpoints, current.endUT, existing.endUT);
                            newOutput.Add(after);
                        }
                    }
                    else
                    {
                        // Existing has higher or equal priority — trim current around existing
                        newOutput.Add(existing);

                        ParsekLog.Verbose(Tag,
                            $"ResolveOverlaps: {existing.source} retains over {current.source} " +
                            $"at [{existing.startUT.ToString("F2", ic)},{existing.endUT.ToString("F2", ic)}]");

                        // Emit the portion of current BEFORE existing (if any) —
                        // this prevents losing gap portions when current spans multiple
                        // disjoint higher-priority sections.
                        if (current.startUT < existing.startUT)
                        {
                            TrackSection before = current;
                            before.endUT = existing.startUT;
                            before.frames = TrimFrames(current.frames, current.startUT, existing.startUT);
                            before.checkpoints = TrimCheckpoints(current.checkpoints, current.startUT, existing.startUT);
                            if (before.endUT > before.startUT)
                                newOutput.Add(before);
                        }

                        // Trim current to only the non-overlapping portion after existing
                        if (current.endUT > existing.endUT)
                        {
                            TrackSection remainder = current;
                            remainder.startUT = existing.endUT;
                            remainder.frames = TrimFrames(current.frames, existing.endUT, current.endUT);
                            remainder.checkpoints = TrimCheckpoints(current.checkpoints, existing.endUT, current.endUT);
                            current = remainder;
                        }
                        else
                        {
                            // Current is entirely covered by higher-priority existing
                            current.startUT = current.endUT; // Mark as consumed
                        }
                    }
                }

                output = newOutput;

                // Insert current if it still has valid duration
                if (current.endUT > current.startUT)
                {
                    // Find insertion position to maintain startUT order
                    int insertIdx = output.Count;
                    for (int k = 0; k < output.Count; k++)
                    {
                        if (output[k].startUT > current.startUT)
                        {
                            insertIdx = k;
                            break;
                        }
                    }
                    output.Insert(insertIdx, current);
                }
            }

            // Sort final output by startUT
            output.Sort((a, b) => a.startUT.CompareTo(b.startUT));

            // Compute boundary discontinuities at each junction
            for (int i = 1; i < output.Count; i++)
            {
                float disc = ComputeBoundaryDiscontinuity(output[i - 1], output[i]);
                TrackSection s = output[i];
                s.boundaryDiscontinuityMeters = disc;
                output[i] = s;
            }

            // Clear discontinuity on first section
            if (output.Count > 0)
            {
                TrackSection first = output[0];
                first.boundaryDiscontinuityMeters = 0f;
                output[0] = first;
            }

            ParsekLog.Verbose(Tag,
                $"ResolveOverlaps: input={sections.Count} output={output.Count}");

            return output;
        }

        /// <summary>
        /// Computes the Euclidean distance in meters between the last trajectory point
        /// of the previous section and the first trajectory point of the next section.
        /// Uses lat/lon/alt to compute approximate distance. Returns 0 if either section
        /// has no trajectory frames.
        /// </summary>
        internal static float ComputeBoundaryDiscontinuity(TrackSection prev, TrackSection next)
        {
            if (prev.frames == null || prev.frames.Count == 0)
                return 0f;
            if (next.frames == null || next.frames.Count == 0)
                return 0f;

            TrajectoryPoint lastPrev = prev.frames[prev.frames.Count - 1];
            TrajectoryPoint firstNext = next.frames[0];

            // Use direct position delta from lat/lon/alt
            // For KSP, approximate distance using coordinate differences
            // Altitude difference is straightforward in meters
            double dAlt = firstNext.altitude - lastPrev.altitude;

            // Lat/lon to approximate meters using the body radius from the last point
            // of the previous section. Approximate but sufficient for discontinuity diagnostics.
            double bodyRadius = GetBodyRadius(lastPrev.bodyName);
            double dLat = (firstNext.latitude - lastPrev.latitude) * Math.PI / 180.0;
            double dLon = (firstNext.longitude - lastPrev.longitude) * Math.PI / 180.0;
            double avgLat = (firstNext.latitude + lastPrev.latitude) * 0.5 * Math.PI / 180.0;
            double r = bodyRadius + (firstNext.altitude + lastPrev.altitude) * 0.5;

            double dNorth = dLat * r;
            double dEast = dLon * r * Math.Cos(avgLat);

            double dist = Math.Sqrt(dNorth * dNorth + dEast * dEast + dAlt * dAlt);
            return (float)dist;
        }

        /// <summary>
        /// Merges two PartEvent lists, deduplicating by (ut, partPersistentId, eventType)
        /// and sorting by UT.
        /// </summary>
        internal static List<PartEvent> MergePartEvents(List<PartEvent> eventsA, List<PartEvent> eventsB)
        {
            var merged = new List<PartEvent>();
            var seen = new HashSet<string>();

            AddEventsWithDedup(merged, seen, eventsA);
            AddEventsWithDedup(merged, seen, eventsB);

            merged.Sort((a, b) => a.ut.CompareTo(b.ut));

            ParsekLog.Verbose(Tag,
                $"MergePartEvents: inputA={eventsA?.Count ?? 0} inputB={eventsB?.Count ?? 0} " +
                $"output={merged.Count} deduped={((eventsA?.Count ?? 0) + (eventsB?.Count ?? 0)) - merged.Count}");

            return merged;
        }

        // --- Private helpers ---

        private static void AddEventsWithDedup(
            List<PartEvent> merged, HashSet<string> seen, List<PartEvent> events)
        {
            if (events == null) return;

            var ic = CultureInfo.InvariantCulture;
            for (int i = 0; i < events.Count; i++)
            {
                PartEvent e = events[i];
                string key = string.Concat(
                    e.ut.ToString("R", ic), "|",
                    e.partPersistentId.ToString(ic), "|",
                    ((int)e.eventType).ToString(ic), "|",
                    e.moduleIndex.ToString(ic), "|",
                    e.value.ToString("R", ic));

                if (seen.Add(key))
                    merged.Add(e);
            }
        }

        /// <summary>
        /// Trims a trajectory frame list to only include frames within [startUT, endUT].
        /// Returns null if input is null or result is empty.
        /// </summary>
        private static List<TrajectoryPoint> TrimFrames(
            List<TrajectoryPoint> frames, double startUT, double endUT)
        {
            if (frames == null || frames.Count == 0)
                return null;

            var trimmed = new List<TrajectoryPoint>();
            for (int i = 0; i < frames.Count; i++)
            {
                if (frames[i].ut >= startUT && frames[i].ut <= endUT)
                    trimmed.Add(frames[i]);
            }

            return trimmed.Count > 0 ? trimmed : null;
        }

        /// <summary>
        /// Trims a checkpoint list to only include checkpoints within [startUT, endUT].
        /// Returns null if input is null or result is empty.
        /// </summary>
        private static List<OrbitSegment> TrimCheckpoints(
            List<OrbitSegment> checkpoints, double startUT, double endUT)
        {
            if (checkpoints == null || checkpoints.Count == 0)
                return null;

            var trimmed = new List<OrbitSegment>();
            for (int i = 0; i < checkpoints.Count; i++)
            {
                // Include checkpoint if its time range overlaps with [startUT, endUT]
                if (checkpoints[i].endUT >= startUT && checkpoints[i].startUT <= endUT)
                    trimmed.Add(checkpoints[i]);
            }

            return trimmed.Count > 0 ? trimmed : null;
        }

        /// <summary>
        /// Counts how many overlaps were resolved by comparing input and output sections.
        /// An overlap is resolved when an output section has different UT bounds than
        /// any corresponding input section, or when input sections were dropped entirely.
        /// </summary>
        private static int CountOverlapsResolved(
            List<TrackSection> input, List<TrackSection> output)
        {
            if (input == null || input.Count <= 1)
                return 0;

            // Simple heuristic: count input sections that were trimmed or split
            int count = 0;
            var outputBounds = new HashSet<string>();
            var ic = CultureInfo.InvariantCulture;

            for (int i = 0; i < output.Count; i++)
            {
                outputBounds.Add(string.Concat(
                    output[i].startUT.ToString("R", ic), "|",
                    output[i].endUT.ToString("R", ic), "|",
                    ((int)output[i].source).ToString(ic)));
            }

            for (int i = 0; i < input.Count; i++)
            {
                string key = string.Concat(
                    input[i].startUT.ToString("R", ic), "|",
                    input[i].endUT.ToString("R", ic), "|",
                    ((int)input[i].source).ToString(ic));

                if (!outputBounds.Contains(key))
                    count++;
            }

            return count;
        }
    }
}
