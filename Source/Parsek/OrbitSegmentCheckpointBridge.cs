using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    internal struct OrbitSegmentCheckpointBridgeStats
    {
        public int Added;
        public int SkippedExisting;
        public int SkippedInvalid;
        public int SkippedPredicted;
        public int SkippedAfterPredicted;
        public int SkippedCovered;
        public int Clipped;
        public int ReconciledEmptySections;

        public bool Changed => Added > 0 || Clipped > 0 || SkippedCovered > 0
            || ReconciledEmptySections > 0;
    }

    /// <summary>
    /// Keeps packed/on-rails orbital payload in the section model. Flat
    /// Recording.OrbitSegments are a runtime cache; OrbitalCheckpoint TrackSections
    /// are the durable representation for section-authoritative paths.
    /// </summary>
    internal static class OrbitSegmentCheckpointBridge
    {
        private const double UtTolerance = 1e-6;
        private const double ScalarTolerance = 1e-9;
        // Dedup tolerance for values produced by the same segment after "R"
        // round-trip serialization; not an orbit-mechanics equivalence threshold.
        private const double DistanceTolerance = 1e-6;
        private const double VectorTolerance = 1e-6;

        internal static TrackSection BuildOpenCheckpointSection(double startUT)
        {
            return new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = startUT,
                source = TrackSectionSource.Checkpoint,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>(),
                minAltitude = float.NaN,
                maxAltitude = float.NaN
            };
        }

        internal static TrackSection BuildClosedCheckpointSection(OrbitSegment segment)
        {
            TrackSection section = BuildOpenCheckpointSection(segment.startUT);
            section.endUT = segment.endUT;
            section.checkpoints.Add(segment);
            return section;
        }

        internal static bool TryAppendClosedCheckpointSection(
            Recording rec,
            OrbitSegment segment,
            bool markDirty,
            out string skipReason)
        {
            skipReason = null;
            if (rec == null)
            {
                skipReason = "no-recording";
                return false;
            }
            if (segment.isPredicted)
            {
                skipReason = "predicted";
                return false;
            }
            if (!IsValidClosedSegment(segment))
            {
                skipReason = "invalid";
                return false;
            }

            if (rec.TrackSections == null)
                rec.TrackSections = new List<TrackSection>();

            if (LastSectionCheckpointMatches(rec.TrackSections, segment))
            {
                skipReason = "duplicate-last";
                return false;
            }

            // Anti-double-cover: the incoming span may already be (partly) owned by
            // existing physical or closed checkpoint sections — existing sections win
            // and only the uncovered remainder(s) are appended. Without this a coarse
            // on-rails close could add a checkpoint enveloping finer sections already
            // in the list, leaving two sections covering the same UT.
            List<OrbitSegment> uncoveredSegments =
                BuildSegmentsOutsideCoveringSections(segment, rec.TrackSections);
            if (uncoveredSegments.Count == 0)
            {
                skipReason = "covered";
                return false;
            }
            bool clippedIncoming = uncoveredSegments.Count != 1
                || !OrbitSegmentNearlyEquals(uncoveredSegments[0], segment);

            bool addedNewSection = false;
            for (int i = 0; i < uncoveredSegments.Count; i++)
            {
                OrbitSegment uncoveredSegment = uncoveredSegments[i];
                if (TryAttachToLastEmptyCheckpointSection(rec.TrackSections, uncoveredSegment))
                {
                    AppendFlatOrbitCache(rec, uncoveredSegment);
                    continue;
                }

                RemoveEmptyCheckpointSectionsMatching(rec.TrackSections, uncoveredSegment);
                rec.TrackSections.Add(BuildClosedCheckpointSection(uncoveredSegment));
                AppendFlatOrbitCache(rec, uncoveredSegment);
                addedNewSection = true;
            }

            int reconciledEmpty = ReconcileEmptySectionsAgainstPayloadCoverage(rec.TrackSections);
            if ((clippedIncoming || reconciledEmpty > 0) && !RecordingStore.SuppressLogging)
            {
                ParsekLog.Verbose("RecordingStore",
                    $"TryAppendClosedCheckpointSection: reconciled overlap for recording={rec.RecordingId} " +
                    $"span=[{segment.startUT.ToString("F2", CultureInfo.InvariantCulture)}," +
                    $"{segment.endUT.ToString("F2", CultureInfo.InvariantCulture)}] " +
                    $"appendedSegments={uncoveredSegments.Count} clipped={(clippedIncoming ? 1 : 0)} " +
                    $"reconciledEmptySections={reconciledEmpty}");
            }

            if (addedNewSection || reconciledEmpty > 0)
                SortTrackSections(rec.TrackSections);
            SortOrbitSegments(rec.OrbitSegments);
            rec.CachedStats = null;
            rec.CachedStatsPointCount = 0;
            if (markDirty)
                rec.MarkFilesDirty();
            return true;
        }

        // reconcileEmptySections gates the empty-shell reconcile pass, which trims or
        // removes EXISTING payload-less sections covered by payload-bearing ones.
        // Producer/write contexts run it so every recording that gets (re)written is
        // overlap-free. The sidecar READ sites pass false: loading must not mutate a
        // committed recording's existing sections. The checkpoint-vs-checkpoint
        // candidate clipping is NOT gated — it only constrains what promotion ADDS,
        // and the read path must not re-create envelope double-cover from a stale
        // flat cache either. Overall contract is normalize-on-rewrite, not
        // byte-freeze: a recording dirtied by any sanctioned flow is rewritten
        // through the write-path Ensure and comes out reconciled; files no flow
        // dirties stay byte-identical.
        internal static OrbitSegmentCheckpointBridgeStats EnsureCheckpointSectionsForTopLevelOrbitSegments(
            Recording rec,
            bool markDirty,
            bool reconcileEmptySections = true)
        {
            var stats = new OrbitSegmentCheckpointBridgeStats();
            if (rec == null)
                return stats;

            if (rec.OrbitSegments == null || rec.OrbitSegments.Count == 0)
            {
                // No flat segments to promote, but the empty-shell reconcile must
                // still run on write paths: an atmospheric/surface-only recording
                // (zero orbit segments) can carry a payload-less shell that
                // double-covers a physical section.
                if (reconcileEmptySections && rec.TrackSections != null)
                {
                    stats.ReconciledEmptySections +=
                        ReconcileEmptySectionsAgainstPayloadCoverage(rec.TrackSections);
                }
                if (stats.Changed)
                {
                    rec.CachedStats = null;
                    rec.CachedStatsPointCount = 0;
                    if (markDirty)
                        rec.MarkFilesDirty();
                }
                return stats;
            }

            if (rec.TrackSections == null)
                rec.TrackSections = new List<TrackSection>();

            stats.Clipped += ClipExistingCheckpointSectionsAgainstPhysicalSections(rec.TrackSections);

            // OrbitSegments are maintained in chronological append order, with
            // predicted terminal tails as a suffix. Once a predicted segment appears,
            // later entries are treated as part of that terminal tail rather than
            // promoted into durable checkpoint bridge sections.
            bool sawPredictedSegment = false;
            for (int i = 0; i < rec.OrbitSegments.Count; i++)
            {
                OrbitSegment segment = rec.OrbitSegments[i];
                if (segment.isPredicted)
                {
                    sawPredictedSegment = true;
                    stats.SkippedPredicted++;
                    continue;
                }
                if (sawPredictedSegment)
                {
                    stats.SkippedAfterPredicted++;
                    continue;
                }
                if (!IsValidClosedSegment(segment))
                {
                    stats.SkippedInvalid++;
                    continue;
                }

                List<OrbitSegment> clippedSegments =
                    BuildSegmentsOutsidePhysicalSections(segment, rec.TrackSections);
                if (clippedSegments.Count == 0)
                {
                    stats.SkippedCovered++;
                    continue;
                }
                if (clippedSegments.Count != 1
                    || !OrbitSegmentNearlyEquals(clippedSegments[0], segment))
                {
                    stats.Clipped++;
                }

                bool addedAny = false;
                bool skippedExistingAny = false;
                for (int j = 0; j < clippedSegments.Count; j++)
                {
                    OrbitSegment clippedSegment = clippedSegments[j];
                    if (AnyCheckpointMatches(rec.TrackSections, clippedSegment))
                    {
                        skippedExistingAny = true;
                        continue;
                    }

                    // Anti-double-cover (checkpoint-vs-checkpoint): spans already owned
                    // by CLOSED checkpoint sections win; only the uncovered remainder(s)
                    // of the candidate are promoted. Without this a coarse flat envelope
                    // segment [X,Z] would be added alongside existing finer checkpoint
                    // sections [X,Y] + [Y,Z], double-covering the whole span.
                    List<OrbitSegment> uncoveredSegments =
                        BuildSegmentsOutsideClosedCheckpointSections(clippedSegment, rec.TrackSections);
                    if (uncoveredSegments.Count == 0)
                    {
                        // Fully covered by existing checkpoint sections (non-exact match):
                        // count as covered so the flat cache is rebuilt from section
                        // content and the next pass sees exact matches.
                        stats.SkippedCovered++;
                        continue;
                    }
                    if (uncoveredSegments.Count != 1
                        || !OrbitSegmentNearlyEquals(uncoveredSegments[0], clippedSegment))
                    {
                        stats.Clipped++;
                    }

                    for (int u = 0; u < uncoveredSegments.Count; u++)
                    {
                        OrbitSegment uncoveredSegment = uncoveredSegments[u];
                        if (TryAttachToAnyEmptyCheckpointSection(rec.TrackSections, uncoveredSegment))
                        {
                            stats.Added++;
                            addedAny = true;
                            continue;
                        }

                        rec.TrackSections.Add(BuildClosedCheckpointSection(uncoveredSegment));
                        stats.Added++;
                        addedAny = true;
                    }
                }

                if (!addedAny && skippedExistingAny)
                    stats.SkippedExisting++;
            }

            // Anti-double-cover (empty-shell reconcile): a payload-less section (no
            // frames, no bodyFixedFrames, no checkpoints — e.g. an empty Absolute shell
            // left by a split or an unattached open checkpoint shell) that is covered by
            // payload-bearing sections is trimmed to the uncovered remainder, or removed
            // when nothing remains. The payload sections own those spans.
            if (reconcileEmptySections)
            {
                stats.ReconciledEmptySections +=
                    ReconcileEmptySectionsAgainstPayloadCoverage(rec.TrackSections);
            }

            bool sorted = EnsureTrackSectionsSorted(rec.TrackSections);
            if (stats.Changed || sorted)
            {
                if (stats.Clipped > 0 || stats.SkippedCovered > 0)
                    RebuildFlatOrbitCacheFromCheckpointSectionsPreservingPredictedTail(rec);
                rec.CachedStats = null;
                rec.CachedStatsPointCount = 0;
                if (markDirty && (stats.Changed || sorted))
                    rec.MarkFilesDirty();
            }

            return stats;
        }

        internal static bool TryTrimOrbitSegmentToRange(
            OrbitSegment segment,
            double startUT,
            double endUT,
            out OrbitSegment trimmed)
        {
            trimmed = segment;
            if (!IsValidClosedSegment(segment)
                || !IsFinite(startUT)
                || !IsFinite(endUT)
                || endUT <= startUT + UtTolerance)
            {
                return false;
            }

            double clippedStartUT = Math.Max(segment.startUT, startUT);
            double clippedEndUT = Math.Min(segment.endUT, endUT);
            if (clippedEndUT <= clippedStartUT + UtTolerance)
                return false;

            trimmed.startUT = clippedStartUT;
            trimmed.endUT = clippedEndUT;
            return true;
        }

        internal static bool AnyCheckpointMatches(List<TrackSection> sections, OrbitSegment segment)
        {
            if (sections == null)
                return false;

            for (int i = 0; i < sections.Count; i++)
            {
                if (CheckpointSectionMatches(sections[i], segment))
                    return true;
            }

            return false;
        }

        private static bool TryAttachToLastEmptyCheckpointSection(
            List<TrackSection> sections,
            OrbitSegment segment)
        {
            if (sections == null || sections.Count == 0)
                return false;

            return TryAttachToEmptyCheckpointSection(sections, sections.Count - 1, segment);
        }

        private static bool TryAttachToAnyEmptyCheckpointSection(
            List<TrackSection> sections,
            OrbitSegment segment)
        {
            if (sections == null)
                return false;

            for (int i = 0; i < sections.Count; i++)
            {
                if (TryAttachToEmptyCheckpointSection(sections, i, segment))
                    return true;
            }

            return false;
        }

        private static int ClipExistingCheckpointSectionsAgainstPhysicalSections(
            List<TrackSection> sections)
        {
            if (sections == null || sections.Count == 0)
                return 0;

            int changed = 0;
            for (int i = sections.Count - 1; i >= 0; i--)
            {
                TrackSection section = sections[i];
                if (section.referenceFrame != ReferenceFrame.OrbitalCheckpoint
                    || section.checkpoints == null
                    || section.checkpoints.Count == 0)
                {
                    continue;
                }

                var replacements = new List<TrackSection>();
                for (int c = 0; c < section.checkpoints.Count; c++)
                {
                    List<OrbitSegment> clippedSegments =
                        BuildSegmentsOutsidePhysicalSections(section.checkpoints[c], sections);
                    for (int j = 0; j < clippedSegments.Count; j++)
                        replacements.Add(BuildClosedCheckpointSection(clippedSegments[j]));
                }

                if (CheckpointReplacementIsUnchanged(section, replacements))
                    continue;

                sections.RemoveAt(i);
                for (int r = replacements.Count - 1; r >= 0; r--)
                    sections.Insert(i, replacements[r]);
                changed++;
            }

            return changed;
        }

        private static bool CheckpointReplacementIsUnchanged(
            TrackSection original,
            List<TrackSection> replacements)
        {
            if (replacements == null
                || original.checkpoints == null
                || replacements.Count != original.checkpoints.Count)
            {
                return false;
            }

            for (int i = 0; i < replacements.Count; i++)
            {
                if (!CheckpointSectionMatches(replacements[i], original.checkpoints[i]))
                    return false;
            }

            return true;
        }

        private static List<OrbitSegment> BuildSegmentsOutsidePhysicalSections(
            OrbitSegment segment,
            List<TrackSection> sections)
        {
            return BuildSegmentsOutsideSections(segment, sections, isHigherPriorityPhysicalSection);
        }

        private static List<OrbitSegment> BuildSegmentsOutsideClosedCheckpointSections(
            OrbitSegment segment,
            List<TrackSection> sections)
        {
            return BuildSegmentsOutsideSections(segment, sections, isClosedCheckpointSection);
        }

        private static List<OrbitSegment> BuildSegmentsOutsideCoveringSections(
            OrbitSegment segment,
            List<TrackSection> sections)
        {
            return BuildSegmentsOutsideSections(segment, sections, isPhysicalOrClosedCheckpointSection);
        }

        private static List<OrbitSegment> BuildSegmentsOutsideSections(
            OrbitSegment segment,
            List<TrackSection> sections,
            Func<TrackSection, bool> subtractsSpan)
        {
            var result = new List<OrbitSegment>();
            if (!IsValidClosedSegment(segment))
                return result;

            var ranges = new List<UtRange>
            {
                new UtRange(segment.startUT, segment.endUT)
            };

            if (sections != null)
            {
                for (int i = 0; i < sections.Count; i++)
                {
                    TrackSection section = sections[i];
                    if (!subtractsSpan(section))
                        continue;

                    SubtractRange(ranges, section.startUT, section.endUT);
                    if (ranges.Count == 0)
                        break;
                }
            }

            for (int i = 0; i < ranges.Count; i++)
            {
                OrbitSegment clipped;
                if (TryTrimOrbitSegmentToRange(
                        segment, ranges[i].StartUT, ranges[i].EndUT, out clipped))
                {
                    result.Add(clipped);
                }
            }

            return result;
        }

        // Cached delegates so per-call BuildSegmentsOutside* invocations do not allocate.
        private static readonly Func<TrackSection, bool> isHigherPriorityPhysicalSection =
            IsHigherPriorityPhysicalSection;
        private static readonly Func<TrackSection, bool> isClosedCheckpointSection =
            IsClosedCheckpointSection;
        private static readonly Func<TrackSection, bool> isPhysicalOrClosedCheckpointSection =
            s => IsHigherPriorityPhysicalSection(s) || IsClosedCheckpointSection(s);

        private static bool IsHigherPriorityPhysicalSection(TrackSection section)
        {
            return section.source < TrackSectionSource.Checkpoint
                && section.referenceFrame != ReferenceFrame.OrbitalCheckpoint
                && section.endUT > section.startUT + UtTolerance
                && ((section.frames != null && section.frames.Count > 0)
                    || (section.bodyFixedFrames != null && section.bodyFixedFrames.Count > 0));
        }

        private static bool IsClosedCheckpointSection(TrackSection section)
        {
            return section.referenceFrame == ReferenceFrame.OrbitalCheckpoint
                && section.endUT > section.startUT + UtTolerance
                && section.checkpoints != null
                && section.checkpoints.Count > 0;
        }

        /// <summary>
        /// Shared "does this section carry any playable payload" predicate (frames,
        /// bodyFixedFrames, or checkpoints). Also consumed by FlightRecorder's
        /// resume-payload check — keep the payload surfaces in ONE place so the
        /// bridge's span-ownership decisions and the recorder's resume decisions
        /// cannot diverge when a new payload surface is added.
        /// </summary>
        internal static bool HasSectionPayload(TrackSection section)
        {
            return (section.frames != null && section.frames.Count > 0)
                || (section.bodyFixedFrames != null && section.bodyFixedFrames.Count > 0)
                || (section.checkpoints != null && section.checkpoints.Count > 0);
        }

        /// <summary>
        /// A reconcilable empty shell: claims a non-degenerate UT span but carries no
        /// playable payload and is not a producer-flagged boundary-seam artifact.
        /// </summary>
        private static bool IsReconcilableEmptySection(TrackSection section)
        {
            return !section.isBoundarySeam
                && !HasSectionPayload(section)
                && section.endUT > section.startUT + UtTolerance;
        }

        /// <summary>
        /// Trims payload-less sections against the spans owned by payload-bearing
        /// sections: an empty shell fully covered elsewhere is removed; a partly
        /// covered shell is replaced by clone(s) spanning only the uncovered
        /// remainder(s). Returns the number of shells removed or trimmed.
        /// </summary>
        internal static int ReconcileEmptySectionsAgainstPayloadCoverage(
            List<TrackSection> sections)
        {
            if (sections == null || sections.Count < 2)
                return 0;

            int reconciled = 0;
            for (int i = sections.Count - 1; i >= 0; i--)
            {
                TrackSection section = sections[i];
                if (!IsReconcilableEmptySection(section))
                    continue;

                var ranges = new List<UtRange>
                {
                    new UtRange(section.startUT, section.endUT)
                };
                for (int j = 0; j < sections.Count && ranges.Count > 0; j++)
                {
                    if (j == i)
                        continue;
                    TrackSection other = sections[j];
                    if (!HasSectionPayload(other))
                        continue;

                    SubtractRange(ranges, other.startUT, other.endUT);
                }

                if (ranges.Count == 1
                    && NearlyEqual(ranges[0].StartUT, section.startUT, UtTolerance)
                    && NearlyEqual(ranges[0].EndUT, section.endUT, UtTolerance))
                {
                    continue;
                }

                sections.RemoveAt(i);
                for (int r = ranges.Count - 1; r >= 0; r--)
                {
                    TrackSection remainder = section;
                    remainder.startUT = ranges[r].StartUT;
                    remainder.endUT = ranges[r].EndUT;
                    remainder.frames = section.frames != null
                        ? new List<TrajectoryPoint>()
                        : null;
                    remainder.bodyFixedFrames = section.bodyFixedFrames != null
                        ? new List<TrajectoryPoint>()
                        : null;
                    remainder.checkpoints = section.checkpoints != null
                        ? new List<OrbitSegment>()
                        : null;
                    sections.Insert(i, remainder);
                }
                reconciled++;
            }

            return reconciled;
        }

        private static void SubtractRange(
            List<UtRange> ranges,
            double removeStartUT,
            double removeEndUT)
        {
            if (ranges == null || ranges.Count == 0)
                return;
            if (!IsFinite(removeStartUT)
                || !IsFinite(removeEndUT)
                || removeEndUT <= removeStartUT + UtTolerance)
            {
                return;
            }

            var updated = new List<UtRange>();
            for (int i = 0; i < ranges.Count; i++)
            {
                UtRange range = ranges[i];
                double clippedStart = Math.Max(range.StartUT, removeStartUT);
                double clippedEnd = Math.Min(range.EndUT, removeEndUT);
                if (clippedEnd <= clippedStart + UtTolerance)
                {
                    updated.Add(range);
                    continue;
                }

                if (clippedStart > range.StartUT + UtTolerance)
                    updated.Add(new UtRange(range.StartUT, clippedStart));
                if (clippedEnd < range.EndUT - UtTolerance)
                    updated.Add(new UtRange(clippedEnd, range.EndUT));
            }

            ranges.Clear();
            ranges.AddRange(updated);
        }

        private struct UtRange
        {
            public readonly double StartUT;
            public readonly double EndUT;

            public UtRange(double startUT, double endUT)
            {
                StartUT = startUT;
                EndUT = endUT;
            }
        }

        private static bool TryAttachToEmptyCheckpointSection(
            List<TrackSection> sections,
            int index,
            OrbitSegment segment)
        {
            TrackSection section = sections[index];
            if (section.referenceFrame != ReferenceFrame.OrbitalCheckpoint
                || !NearlyEqual(section.startUT, segment.startUT, UtTolerance)
                || !NearlyEqual(section.endUT, segment.endUT, UtTolerance)
                || (section.checkpoints != null && section.checkpoints.Count > 0))
            {
                return false;
            }

            section.checkpoints = new List<OrbitSegment> { segment };
            if (section.frames == null)
                section.frames = new List<TrajectoryPoint>();
            section.environment = SegmentEnvironment.ExoBallistic;
            section.source = TrackSectionSource.Checkpoint;
            section.minAltitude = float.NaN;
            section.maxAltitude = float.NaN;
            sections[index] = section;
            return true;
        }

        private static void RebuildFlatOrbitCacheFromCheckpointSectionsPreservingPredictedTail(
            Recording rec)
        {
            if (rec == null)
                return;

            var originalOrbitSegments = rec.OrbitSegments != null
                ? new List<OrbitSegment>(rec.OrbitSegments)
                : new List<OrbitSegment>();
            var rebuilt = new List<OrbitSegment>();

            if (rec.TrackSections != null)
            {
                for (int i = 0; i < rec.TrackSections.Count; i++)
                {
                    TrackSection section = rec.TrackSections[i];
                    if (section.referenceFrame != ReferenceFrame.OrbitalCheckpoint
                        || section.checkpoints == null)
                    {
                        continue;
                    }

                    for (int j = 0; j < section.checkpoints.Count; j++)
                    {
                        OrbitSegment segment = section.checkpoints[j];
                        if (segment.isPredicted)
                            continue;
                        if (rebuilt.Count > 0
                            && OrbitSegmentNearlyEquals(rebuilt[rebuilt.Count - 1], segment))
                        {
                            continue;
                        }

                        rebuilt.Add(segment);
                    }
                }
            }

            int predictedTailStart = FindPredictedTailStart(originalOrbitSegments);
            if (predictedTailStart >= 0)
            {
                for (int i = predictedTailStart; i < originalOrbitSegments.Count; i++)
                {
                    OrbitSegment segment = originalOrbitSegments[i];
                    if (rebuilt.Count > 0
                        && OrbitSegmentNearlyEquals(rebuilt[rebuilt.Count - 1], segment))
                    {
                        continue;
                    }

                    rebuilt.Add(segment);
                }
            }

            rec.OrbitSegments = rebuilt;
        }

        private static int FindPredictedTailStart(List<OrbitSegment> segments)
        {
            if (segments == null || segments.Count == 0)
                return -1;

            for (int i = 0; i < segments.Count; i++)
            {
                if (!segments[i].isPredicted)
                    continue;

                for (int j = i + 1; j < segments.Count; j++)
                {
                    if (!segments[j].isPredicted)
                        return -1;
                }

                return i;
            }

            return -1;
        }

        private static void RemoveEmptyCheckpointSectionsMatching(
            List<TrackSection> sections,
            OrbitSegment segment)
        {
            if (sections == null || sections.Count == 0)
                return;

            for (int i = sections.Count - 1; i >= 0; i--)
            {
                TrackSection section = sections[i];
                if (section.referenceFrame == ReferenceFrame.OrbitalCheckpoint
                    && NearlyEqual(section.startUT, segment.startUT, UtTolerance)
                    && NearlyEqual(section.endUT, segment.endUT, UtTolerance)
                    && (section.checkpoints == null || section.checkpoints.Count == 0))
                {
                    sections.RemoveAt(i);
                }
            }
        }

        private static bool LastSectionCheckpointMatches(List<TrackSection> sections, OrbitSegment segment)
        {
            if (sections == null || sections.Count == 0)
                return false;

            return CheckpointSectionMatches(sections[sections.Count - 1], segment);
        }

        private static bool CheckpointSectionMatches(TrackSection section, OrbitSegment segment)
        {
            if (section.referenceFrame != ReferenceFrame.OrbitalCheckpoint
                || !NearlyEqual(section.startUT, segment.startUT, UtTolerance)
                || !NearlyEqual(section.endUT, segment.endUT, UtTolerance)
                || section.checkpoints == null)
            {
                return false;
            }

            for (int i = 0; i < section.checkpoints.Count; i++)
            {
                if (OrbitSegmentNearlyEquals(section.checkpoints[i], segment))
                    return true;
            }

            return false;
        }

        private static bool IsValidClosedSegment(OrbitSegment segment)
        {
            return IsFinite(segment.startUT)
                && IsFinite(segment.endUT)
                && segment.endUT > segment.startUT + UtTolerance;
        }

        private static void AppendFlatOrbitCache(Recording rec, OrbitSegment segment)
        {
            if (rec.OrbitSegments == null)
                rec.OrbitSegments = new List<OrbitSegment>();

            if (rec.OrbitSegments.Count > 0
                && OrbitSegmentNearlyEquals(rec.OrbitSegments[rec.OrbitSegments.Count - 1], segment))
            {
                return;
            }

            rec.OrbitSegments.Add(segment);
        }

        private static void SortTrackSections(List<TrackSection> sections)
        {
            if (sections == null || sections.Count < 2)
                return;

            sections.Sort((a, b) =>
            {
                int cmp = a.startUT.CompareTo(b.startUT);
                if (cmp != 0) return cmp;
                cmp = ((int)a.source).CompareTo((int)b.source);
                if (cmp != 0) return cmp;
                return a.endUT.CompareTo(b.endUT);
            });
        }

        private static bool EnsureTrackSectionsSorted(List<TrackSection> sections)
        {
            if (sections == null || sections.Count < 2)
                return false;

            bool sorted = true;
            for (int i = 1; i < sections.Count; i++)
            {
                if (CompareTrackSections(sections[i - 1], sections[i]) > 0)
                {
                    sorted = false;
                    break;
                }
            }

            if (sorted)
                return false;

            SortTrackSections(sections);
            return true;
        }

        private static int CompareTrackSections(TrackSection a, TrackSection b)
        {
            int cmp = a.startUT.CompareTo(b.startUT);
            if (cmp != 0) return cmp;
            cmp = ((int)a.source).CompareTo((int)b.source);
            if (cmp != 0) return cmp;
            return a.endUT.CompareTo(b.endUT);
        }

        private static void SortOrbitSegments(List<OrbitSegment> segments)
        {
            if (segments == null || segments.Count < 2)
                return;

            segments.Sort((a, b) =>
            {
                int cmp = a.startUT.CompareTo(b.startUT);
                if (cmp != 0) return cmp;
                return a.endUT.CompareTo(b.endUT);
            });
        }

        private static bool OrbitSegmentNearlyEquals(OrbitSegment a, OrbitSegment b)
        {
            return FieldNearlyEqual(a.startUT, b.startUT, UtTolerance)
                && FieldNearlyEqual(a.endUT, b.endUT, UtTolerance)
                && FieldNearlyEqual(a.inclination, b.inclination, ScalarTolerance)
                && FieldNearlyEqual(a.eccentricity, b.eccentricity, ScalarTolerance)
                && FieldNearlyEqual(a.semiMajorAxis, b.semiMajorAxis, DistanceTolerance)
                && FieldNearlyEqual(a.longitudeOfAscendingNode, b.longitudeOfAscendingNode, ScalarTolerance)
                && FieldNearlyEqual(a.argumentOfPeriapsis, b.argumentOfPeriapsis, ScalarTolerance)
                && FieldNearlyEqual(a.meanAnomalyAtEpoch, b.meanAnomalyAtEpoch, ScalarTolerance)
                && FieldNearlyEqual(a.epoch, b.epoch, UtTolerance)
                && a.bodyName == b.bodyName
                && a.isPredicted == b.isPredicted
                && FieldNearlyEqual(a.orbitalFrameRotation.x, b.orbitalFrameRotation.x, VectorTolerance)
                && FieldNearlyEqual(a.orbitalFrameRotation.y, b.orbitalFrameRotation.y, VectorTolerance)
                && FieldNearlyEqual(a.orbitalFrameRotation.z, b.orbitalFrameRotation.z, VectorTolerance)
                && FieldNearlyEqual(a.orbitalFrameRotation.w, b.orbitalFrameRotation.w, VectorTolerance)
                && FieldNearlyEqual(a.angularVelocity.x, b.angularVelocity.x, VectorTolerance)
                && FieldNearlyEqual(a.angularVelocity.y, b.angularVelocity.y, VectorTolerance)
                && FieldNearlyEqual(a.angularVelocity.z, b.angularVelocity.z, VectorTolerance);
        }

        private static bool NearlyEqual(double a, double b, double tolerance)
        {
            return Math.Abs(a - b) <= tolerance;
        }

        // Structural-equality comparison for orbit-segment fields. Unlike NearlyEqual,
        // two NaNs (and two equal infinities) compare equal: parabolic/degenerate orbits
        // store semiMajorAxis = NaN (eccentricity == 1), and Math.Abs(NaN - NaN) <= tol is
        // false, so such a segment never equals itself. That broke the checkpoint-bridge
        // dedup (AnyCheckpointMatches always missed), re-adding the segment on every
        // serialization in a geometric explosion that bloated the recording and froze load.
        private static bool FieldNearlyEqual(double a, double b, double tolerance)
        {
            if (double.IsNaN(a) || double.IsNaN(b))
                return double.IsNaN(a) && double.IsNaN(b);
            if (a == b)
                return true;
            return Math.Abs(a - b) <= tolerance;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
