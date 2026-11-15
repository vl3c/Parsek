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
        // 1 when the pass had to re-sort TrackSections (0 otherwise). Previously a
        // bridge-local `sorted` bool that never reached the caller, which is why
        // `Changed` alone under-reports a pure-re-sort pass (see the note at
        // RecordingOptimizer.SplitAtUT). Kept OUT of `Changed` so the existing
        // Changed semantics (and the tests pinned to them) are unmoved; callers that
        // mean "did this pass touch the recording at all" use AnyMutation.
        public int Resorted;

        public bool Changed => Added > 0 || Clipped > 0 || SkippedCovered > 0
            || ReconciledEmptySections > 0;

        /// <summary>
        /// True when the pass mutated the in-memory recording in ANY way — the
        /// section list, the flat orbit cache, or section order. This is the honest
        /// "the model no longer matches what is on disk" signal: a caller that runs
        /// Ensure and does NOT persist afterwards leaves the divergence standing.
        /// </summary>
        public bool AnyMutation => Changed || Resorted > 0;

        /// <summary>
        /// True when a section's ORDINAL may now name a different section than it did
        /// before the pass. Consumed by the <c>.pann</c> annotation containment
        /// (annotations are keyed by (recordingId, sectionIndex) and must not survive
        /// an ordinal shift).
        ///
        /// <para>
        /// Deliberately narrower than <see cref="AnyMutation"/> in both directions:
        /// <c>SkippedCovered</c> only drives the flat-orbit-cache rebuild and never
        /// touches the section list, and <c>Added</c> alone cannot shift an ordinal —
        /// promotion APPENDS, so a chronologically-trailing promotion leaves every
        /// existing index in place, and a promotion that lands out of order is caught
        /// by <c>Resorted</c>. <c>Clipped</c> over-approximates (it also counts
        /// candidate-side clipping, which replaces nothing in the list); erring toward
        /// invalidation is the safe direction for a regenerable cache.
        /// </para>
        /// </summary>
        public bool SectionOrdinalsShifted => Clipped > 0
            || ReconciledEmptySections > 0 || Resorted > 0;
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
            if (AnyCheckpointMatches(rec.TrackSections, segment))
            {
                skipReason = "duplicate";
                return false;
            }

            // Anti-double-cover, newest-wins: a live on-rails close is fresher truth
            // than any checkpoint section already in the list. Physical sections
            // (real recorded frames) still own their spans — the incoming segment is
            // clipped to the remainder outside them — but overlapping CLOSED
            // checkpoint sections are clipped AGAINST the incoming span, so stale
            // coarse envelopes cannot swallow newly recorded orbital elements.
            List<OrbitSegment> uncoveredSegments =
                BuildSegmentsOutsidePhysicalSections(segment, rec.TrackSections);
            if (uncoveredSegments.Count == 0)
            {
                skipReason = "covered";
                return false;
            }
            bool clippedIncoming = uncoveredSegments.Count != 1
                || !OrbitSegmentNearlyEquals(uncoveredSegments[0], segment);

            int clippedExisting = ClipExistingCheckpointSectionsAgainstSpans(
                rec.TrackSections, uncoveredSegments);

            bool addedNewSection = false;
            int removedEmptyShells = 0;
            for (int i = 0; i < uncoveredSegments.Count; i++)
            {
                OrbitSegment uncoveredSegment = uncoveredSegments[i];
                if (TryAttachToLastEmptyCheckpointSection(rec.TrackSections, uncoveredSegment))
                {
                    AppendFlatOrbitCache(rec, uncoveredSegment);
                    continue;
                }

                removedEmptyShells +=
                    RemoveEmptyCheckpointSectionsMatching(rec.TrackSections, uncoveredSegment);
                rec.TrackSections.Add(BuildClosedCheckpointSection(uncoveredSegment));
                AppendFlatOrbitCache(rec, uncoveredSegment);
                addedNewSection = true;
            }

            int reconciledEmpty = ReconcileEmptySectionsAgainstPayloadCoverage(rec.TrackSections);
            if ((clippedIncoming || clippedExisting > 0 || reconciledEmpty > 0)
                && !RecordingStore.SuppressLogging)
            {
                ParsekLog.Verbose("RecordingStore",
                    $"TryAppendClosedCheckpointSection: reconciled overlap for recording={rec.RecordingId} " +
                    $"span=[{segment.startUT.ToString("F2", CultureInfo.InvariantCulture)}," +
                    $"{segment.endUT.ToString("F2", CultureInfo.InvariantCulture)}] " +
                    $"appendedSegments={uncoveredSegments.Count} clippedIncoming={(clippedIncoming ? 1 : 0)} " +
                    $"clippedExistingSections={clippedExisting} reconciledEmptySections={reconciledEmpty}");
            }

            // EnsureTrackSectionsSorted, not SortTrackSections: identical comparator
            // and identical outcome (it sorts only when the list is out of order), but
            // it REPORTS whether it had to, which is what tells the annotation
            // containment below whether section ordinals moved.
            bool resorted = false;
            if (addedNewSection || clippedExisting > 0 || reconciledEmpty > 0)
                resorted = EnsureTrackSectionsSorted(rec.TrackSections);
            if (clippedExisting > 0)
            {
                // Existing sections lost span to the incoming close; the flat cache
                // entries mirroring them are stale — rebuild from section content.
                RebuildFlatOrbitCacheFromCheckpointSectionsPreservingUncoveredFlat(rec);
            }
            SortOrbitSegments(rec.OrbitSegments);
            rec.CachedStats = null;
            rec.CachedStatsPointCount = 0;
            // Live append shifts section ordinals exactly like the Ensure passes do
            // (empty shells are removed, existing checkpoint sections are replaced by
            // clipped clones, and an out-of-order append forces a re-sort), so the
            // ordinal-keyed .pann annotations must go with them. A plain in-order
            // append at the tail moves no existing index and is left alone.
            if (clippedExisting > 0 || reconciledEmpty > 0
                || removedEmptyShells > 0 || resorted)
            {
                InvalidateSectionAnnotationsForOrdinalShift(
                    rec, "TryAppendClosedCheckpointSection");
            }
            if (markDirty)
                rec.MarkFilesDirty();
            return true;
        }

        /// <summary>
        /// Drops every in-memory derived annotation for a recording whose
        /// TrackSection ordinals just moved: the <c>.pann</c> splines / outlier
        /// flags / anchor candidates in
        /// <see cref="Parsek.Rendering.SectionAnnotationStore"/>, AND the resolved
        /// anchor corrections in <see cref="Parsek.Rendering.RenderSessionState"/>.
        ///
        /// <para>
        /// Both stores key by (recordingId, sectionIndex[, side]). Any bridge pass
        /// that adds, removes, replaces or re-orders sections renumbers every later
        /// section, so an annotation computed for old index k would silently start
        /// applying to a DIFFERENT section. The <c>.pann</c> freshness gate cannot
        /// catch it: out-of-band sidecar writes deliberately do not bump
        /// <c>SidecarEpoch</c> (bug #290), and the section list is not part of the
        /// configuration hash.
        /// </para>
        ///
        /// <para>
        /// The two stores are dropped TOGETHER on purpose. <c>RenderSessionState</c>'s
        /// anchor map is populated FROM the candidates held here
        /// (<c>AnchorPropagator.Run</c>), so clearing only the candidates would leave
        /// a stale ε bound to an index that now names a different section — and the
        /// flight-scene positioning path re-applies that ε every frame, which is
        /// worse than the consistently-stale pair it replaced.
        /// </para>
        ///
        /// <para>
        /// Annotations are a regenerable derived cache (HR-10), so invalidation is
        /// the exact containment: the next
        /// <c>SmoothingPipeline.FitAndStorePerSection</c> (load, commit, or the
        /// <c>PersistAfterCommit</c> that follows every sidecar write) refits against
        /// the post-Ensure sections, and the next session rebuild re-propagates ε.
        /// Until then consumers fall back to the legacy lerp path, which is
        /// correct-but-coarser rather than wrong.
        /// </para>
        /// </summary>
        internal static void InvalidateSectionAnnotationsForOrdinalShift(
            Recording rec, string context)
        {
            string recordingId = rec != null ? rec.RecordingId : null;
            if (string.IsNullOrEmpty(recordingId))
                return;

            // A detached synthetic snapshot SHARES the real recording's id while
            // carrying a DIFFERENT section list (today:
            // Recording.BuildPreReFlyAnchorTrajectoryRecording, serialized through the
            // same TrajectoryTextSidecarCodec.SerializeTrajectoryInto seam real
            // recordings use). Normalizing THAT list says nothing about the ordinals
            // the real recording's annotations are keyed to, and the path writes a
            // ConfigNode rather than a sidecar so no PersistAfterCommit refit follows
            // — a drop here would strand flight positioning on the lerp fallback until
            // the next sidecar write.
            if (rec.IsDetachedSyntheticSnapshot)
                return;

            int splineCount =
                Rendering.SectionAnnotationStore.GetSplineCountForRecording(recordingId);
            int candidateCount =
                Rendering.SectionAnnotationStore.GetAnchorCandidateSectionCountForRecording(recordingId);
            int outlierCount =
                Rendering.SectionAnnotationStore.GetOutlierFlagsCountForRecording(recordingId);
            int anchorCount = Rendering.RenderSessionState.RemoveRecording(recordingId);
            if (splineCount == 0 && candidateCount == 0 && outlierCount == 0 && anchorCount == 0)
                return;

            Rendering.SectionAnnotationStore.RemoveRecording(recordingId);
            if (RecordingStore.SuppressLogging)
                return;

            ParsekLog.Verbose("Pipeline-Smoothing",
                $"Section annotations invalidated (section-index-shift): recordingId={recordingId} " +
                $"context={context} splines={splineCount.ToString(CultureInfo.InvariantCulture)} " +
                $"candidateSections={candidateCount.ToString(CultureInfo.InvariantCulture)} " +
                $"outlierFlags={outlierCount.ToString(CultureInfo.InvariantCulture)} " +
                $"sessionAnchors={anchorCount.ToString(CultureInfo.InvariantCulture)}");
        }

        // reconcileEmptySections gates the empty-shell reconcile pass, which trims or
        // removes EXISTING payload-less sections covered by payload-bearing ones.
        // Producer/write contexts run it so every recording that gets (re)written is
        // overlap-free. The sidecar READ sites pass false so THIS pass never mutates
        // a committed recording's existing sections at load. Precise read-path scope:
        // the checkpoint-vs-checkpoint candidate clipping is NOT gated (it only
        // constrains what promotion ADDS, and the read path must not re-create
        // envelope double-cover from a stale flat cache), and the pre-existing
        // checkpoint-vs-PHYSICAL clip of existing sections also still runs there
        // (legacy heal seam, predates this gate). Overall contract is
        // normalize-on-rewrite, not byte-freeze: a recording dirtied by any
        // sanctioned flow is rewritten through the write-path Ensure and comes out
        // reconciled; files no flow dirties stay byte-identical.
        // invalidateSectionAnnotations gates the ordinal-shift annotation drop. It
        // exists for ONE caller shape: a pass that runs Ensure only to inspect the
        // normalized list and then RESTORES the pre-Ensure sections byte-identically
        // on its guarded returns (RecordingOptimizer.SplitAtUT). There the drop would
        // be a pure false invalidation - the ordinals are put back, the annotations
        // were still valid, and that path has no rewrite behind it to refit them. Such
        // a caller owns the invalidation for its own committed path instead (SplitAtUT
        // calls InvalidateSectionAnnotationsForOrdinalShift once the split lands).
        // Every other caller leaves this true.
        internal static OrbitSegmentCheckpointBridgeStats EnsureCheckpointSectionsForTopLevelOrbitSegments(
            Recording rec,
            bool markDirty,
            bool reconcileEmptySections = true,
            bool invalidateSectionAnnotations = true)
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
                    // A trimmed shell's remainders are inserted mid-list and can
                    // land out of chronological order - re-sort, like the main
                    // path does after its reconcile. Untouched recordings (no
                    // reconcile changes) are deliberately left alone.
                    if (EnsureTrackSectionsSorted(rec.TrackSections))
                        stats.Resorted = 1;
                    rec.CachedStats = null;
                    rec.CachedStatsPointCount = 0;
                    if (markDirty)
                        rec.MarkFilesDirty();
                }
                if (invalidateSectionAnnotations && stats.SectionOrdinalsShifted)
                {
                    InvalidateSectionAnnotationsForOrdinalShift(
                        rec, "EnsureCheckpointSectionsForTopLevelOrbitSegments");
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

            if (EnsureTrackSectionsSorted(rec.TrackSections))
                stats.Resorted = 1;
            if (stats.AnyMutation)
            {
                if (stats.Clipped > 0 || stats.SkippedCovered > 0)
                    RebuildFlatOrbitCacheFromCheckpointSectionsPreservingUncoveredFlat(rec);
                rec.CachedStats = null;
                rec.CachedStatsPointCount = 0;
                if (markDirty)
                    rec.MarkFilesDirty();
            }
            if (invalidateSectionAnnotations && stats.SectionOrdinalsShifted)
            {
                InvalidateSectionAnnotationsForOrdinalShift(
                    rec, "EnsureCheckpointSectionsForTopLevelOrbitSegments");
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

        /// <summary>
        /// Newest-wins clip for the live append path: subtracts the given fresh
        /// span(s) from every CLOSED checkpoint section, replacing each section with
        /// clone(s) covering only the UT outside the spans (a section fully inside
        /// is removed). Physical and empty sections are untouched. Returns the
        /// number of sections changed.
        /// </summary>
        private static int ClipExistingCheckpointSectionsAgainstSpans(
            List<TrackSection> sections,
            List<OrbitSegment> spans)
        {
            if (sections == null || sections.Count == 0 || spans == null || spans.Count == 0)
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
                    OrbitSegment checkpoint = section.checkpoints[c];
                    var ranges = new List<UtRange>
                    {
                        new UtRange(checkpoint.startUT, checkpoint.endUT)
                    };
                    for (int s = 0; s < spans.Count && ranges.Count > 0; s++)
                        SubtractRange(ranges, spans[s].startUT, spans[s].endUT);

                    for (int r = 0; r < ranges.Count; r++)
                    {
                        OrbitSegment clipped;
                        if (TryTrimOrbitSegmentToRange(
                                checkpoint, ranges[r].StartUT, ranges[r].EndUT, out clipped))
                        {
                            replacements.Add(BuildClosedCheckpointSection(clipped));
                        }
                    }
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

        // Cached delegate so the per-call Sort does not allocate a fresh one (same
        // convention as the BuildSegmentsOutside* predicates below).
        private static readonly Comparison<TrackSection> compareTrackSections =
            CompareTrackSections;

        // Cached delegates so per-call BuildSegmentsOutside* invocations do not allocate.
        private static readonly Func<TrackSection, bool> isHigherPriorityPhysicalSection =
            IsHigherPriorityPhysicalSection;
        private static readonly Func<TrackSection, bool> isClosedCheckpointSection =
            IsClosedCheckpointSection;

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

        /// <summary>
        /// Rebuilds the flat OrbitSegments cache from checkpoint-section content,
        /// preserving every original flat segment (predicted or real) whose span is
        /// NOT fully covered by payload sections. Coverage-based preservation
        /// replaced the old pure-predicted-suffix rule (FindPredictedTailStart),
        /// which silently dropped the predicted tail plus trailing segments for
        /// interleaved [real, predicted, real] shapes.
        /// </summary>
        private static void RebuildFlatOrbitCacheFromCheckpointSectionsPreservingUncoveredFlat(
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
                        // Predicted checkpoints included: sections legitimately carry
                        // predicted terminal checkpoints (scene-exit finalization),
                        // and the flat copy under the same section span is dropped
                        // by the coverage rule below - skipping them here would lose
                        // the prediction from the flat cache entirely.
                        rebuilt.Add(section.checkpoints[j]);
                    }
                }
            }

            // Preserve flat-only data: any original segment not fully owned by
            // payload sections (checkpoint or physical) exists nowhere else —
            // dropping it would lose predicted terminal tails and recorder-authored
            // flat-only segments.
            for (int i = 0; i < originalOrbitSegments.Count; i++)
            {
                OrbitSegment original = originalOrbitSegments[i];
                if (!IsValidClosedSegment(original))
                    continue;
                if (IsSpanFullyCoveredByPayloadSections(original, rec.TrackSections))
                    continue;

                rebuilt.Add(original);
            }

            SortOrbitSegments(rebuilt);
            for (int i = rebuilt.Count - 1; i > 0; i--)
            {
                if (OrbitSegmentNearlyEquals(rebuilt[i - 1], rebuilt[i]))
                    rebuilt.RemoveAt(i);
            }

            rec.OrbitSegments = rebuilt;
        }

        private static bool IsSpanFullyCoveredByPayloadSections(
            OrbitSegment segment,
            List<TrackSection> sections)
        {
            var ranges = new List<UtRange>
            {
                new UtRange(segment.startUT, segment.endUT)
            };

            if (sections != null)
            {
                for (int i = 0; i < sections.Count && ranges.Count > 0; i++)
                {
                    TrackSection section = sections[i];
                    if (!IsClosedCheckpointSection(section)
                        && !IsHigherPriorityPhysicalSection(section))
                    {
                        continue;
                    }

                    SubtractRange(ranges, section.startUT, section.endUT);
                }
            }

            return ranges.Count == 0;
        }

        /// <summary>
        /// Removes payload-less checkpoint shells exactly matching the segment's span.
        /// Returns how many were removed (an ordinal shift for every later section).
        /// </summary>
        private static int RemoveEmptyCheckpointSectionsMatching(
            List<TrackSection> sections,
            OrbitSegment segment)
        {
            if (sections == null || sections.Count == 0)
                return 0;

            int removed = 0;
            for (int i = sections.Count - 1; i >= 0; i--)
            {
                TrackSection section = sections[i];
                if (section.referenceFrame == ReferenceFrame.OrbitalCheckpoint
                    && NearlyEqual(section.startUT, segment.startUT, UtTolerance)
                    && NearlyEqual(section.endUT, segment.endUT, UtTolerance)
                    && (section.checkpoints == null || section.checkpoints.Count == 0))
                {
                    sections.RemoveAt(i);
                    removed++;
                }
            }

            return removed;
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

            // MUST stay CompareTrackSections: EnsureTrackSectionsSorted CHECKS with
            // that comparator and sorts with this one. If the two ever disagreed,
            // every Ensure would see the list as unsorted, re-sort it, report
            // Resorted=1, dirty the recording and invalidate its annotations - on
            // every pass, forever. One comparator, no drift.
            sections.Sort(compareTrackSections);
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
