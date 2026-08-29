using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Rendering;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Regression tests for the two Ensure-pass data-integrity classes filed
    /// 2026-07-13 (see docs/dev/todo-and-known-bugs.md):
    ///
    /// <list type="number">
    /// <item><description><b>Silent in-memory divergence.</b>
    /// <c>RecordingOptimizer.FindSplitCandidatesForOptimizer</c> ran the checkpoint
    /// bridge's Ensure under <c>markDirty: false</c>. Ensure is not read-only — it
    /// adds / clips / reconciles / re-sorts sections — so the analysis pass left the
    /// in-memory model diverged from disk until some unrelated later write silently
    /// persisted a state nobody deliberately saved.</description></item>
    /// <item><description><b>.pann section-index desync.</b>
    /// <see cref="SectionAnnotationStore"/> keys splines / outlier flags / anchor
    /// candidates by (recordingId, sectionIndex). Any Ensure pass that removes,
    /// inserts or re-sorts sections renumbers the later ones while the in-memory
    /// annotations survive, so an annotation fitted for old index k starts applying
    /// to a different section. The .pann freshness gate cannot see it: out-of-band
    /// sidecar writes deliberately do not bump <c>SidecarEpoch</c> (bug #290) and
    /// the section list is not part of the configuration hash.</description></item>
    /// </list>
    /// </summary>
    [Collection("Sequential")]
    public class EnsurePassIntegrityTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool prevSuppress;

        public EnsurePassIntegrityTests()
        {
            prevSuppress = RecordingStore.SuppressLogging;
            RecordingStore.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            SectionAnnotationStore.ResetForTesting();
        }

        public void Dispose()
        {
            SectionAnnotationStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = prevSuppress;
        }

        // --- fixture helpers -------------------------------------------------

        private static OrbitSegment Segment(double startUT, double endUT)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                inclination = 0,
                eccentricity = 0.1,
                semiMajorAxis = 700000,
                longitudeOfAscendingNode = 10,
                argumentOfPeriapsis = 20,
                meanAnomalyAtEpoch = 0.5,
                epoch = startUT,
                bodyName = "Kerbin"
            };
        }

        private static TrackSection PhysicalSection(double startUT, double endUT)
        {
            return new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = startUT,
                endUT = endUT,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = startUT, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = endUT, bodyName = "Kerbin" }
                },
                checkpoints = new List<OrbitSegment>()
            };
        }

        private static TrackSection EmptyAbsoluteSection(double startUT, double endUT)
        {
            return new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = startUT,
                endUT = endUT,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>()
            };
        }

        /// <summary>
        /// Two physical sections with a GAP between them, plus a flat orbit segment
        /// covering that gap. Ensure promotes the segment into a checkpoint section,
        /// appends it at the tail, then has to re-sort — which renumbers the trailing
        /// physical section from index 1 to index 2. That is the exact ordinal shift
        /// both bugs live on.
        /// </summary>
        private static Recording GapPromotionRecording(string id)
        {
            return new Recording
            {
                RecordingId = id,
                TrackSections = new List<TrackSection>
                {
                    PhysicalSection(0, 100),
                    PhysicalSection(200, 300)
                },
                OrbitSegments = new List<OrbitSegment> { Segment(100, 200) },
                FilesDirty = false
            };
        }

        private static SmoothingSpline Spline(double knotUT)
        {
            return new SmoothingSpline
            {
                SplineType = 0,
                Tension = 0.5f,
                KnotsUT = new[] { knotUT, knotUT + 10 },
                ControlsX = new[] { 1f, 2f },
                ControlsY = new[] { 3f, 4f },
                ControlsZ = new[] { 5f, 6f },
                FrameTag = 1,
                IsValid = true
            };
        }

        // --- bug 1: the analysis pass must not diverge silently ---------------

        // Repro (fails on origin/main): the optimizer's ANALYSIS pass mutates the
        // committed recording's section list in memory and leaves FilesDirty false,
        // so RunOptimizationPass's FlushDirtyFiles skips it and the model diverges
        // from disk until some unrelated later write persists it.
        [Fact]
        public void AnalysisPass_WhenEnsureMutatesSections_MarksRecordingDirty()
        {
            Recording rec = GapPromotionRecording("analysis-divergence");
            int sectionsBefore = rec.TrackSections.Count;
            Assert.False(rec.FilesDirty);

            RecordingOptimizer.FindSplitCandidatesForOptimizer(new List<Recording> { rec });

            // The pass really did mutate the in-memory model...
            Assert.Equal(sectionsBefore + 1, rec.TrackSections.Count);
            Assert.Contains(rec.TrackSections,
                s => s.referenceFrame == ReferenceFrame.OrbitalCheckpoint);
            // ...so it must be flagged for persistence rather than left diverged.
            Assert.True(rec.FilesDirty,
                "A mutating analysis-pass Ensure must mark the recording dirty so the "
                + "optimizer flush persists what it changed.");
            Assert.Contains(logLines,
                l => l.Contains("[Optimizer]")
                    && l.Contains("FindSplitCandidatesForOptimizer: normalization Ensure mutated"));
        }

        // The honest markDirty must not turn every optimizer pass into a rewrite:
        // Ensure is idempotent, so a second pass over an already-normalized
        // recording reports no mutation and dirties nothing.
        [Fact]
        public void AnalysisPass_WhenEnsureIsANoOp_LeavesRecordingClean()
        {
            Recording rec = GapPromotionRecording("analysis-idempotent");
            var committed = new List<Recording> { rec };

            RecordingOptimizer.FindSplitCandidatesForOptimizer(committed);
            int sectionsAfterFirst = rec.TrackSections.Count;
            rec.FilesDirty = false;
            logLines.Clear();

            RecordingOptimizer.FindSplitCandidatesForOptimizer(committed);

            Assert.Equal(sectionsAfterFirst, rec.TrackSections.Count);
            Assert.False(rec.FilesDirty,
                "A second, no-op normalization pass must not dirty the recording.");
            Assert.DoesNotContain(logLines,
                l => l.Contains("FindSplitCandidatesForOptimizer: normalization Ensure mutated"));
        }

        // A recording Ensure does not touch at all must stay byte-clean: the
        // markDirty flip is gated on actual mutation, not on the call happening.
        [Fact]
        public void AnalysisPass_OnAlreadyNormalizedRecording_DoesNotDirty()
        {
            var rec = new Recording
            {
                RecordingId = "analysis-untouched",
                TrackSections = new List<TrackSection>
                {
                    PhysicalSection(0, 100),
                    PhysicalSection(100, 200)
                },
                OrbitSegments = new List<OrbitSegment>(),
                FilesDirty = false
            };

            RecordingOptimizer.FindSplitCandidatesForOptimizer(new List<Recording> { rec });

            Assert.Equal(2, rec.TrackSections.Count);
            Assert.False(rec.FilesDirty);
        }

        // The stats struct must report a pure re-sort. Before this change the
        // re-sort lived in a bridge-local bool that never reached the caller, so
        // `Changed` under-reported the mutation (the trap documented at
        // RecordingOptimizer.SplitAtUT).
        [Fact]
        public void Ensure_PureResort_IsReportedAsMutationButNotAsChanged()
        {
            var rec = new Recording
            {
                RecordingId = "pure-resort",
                TrackSections = new List<TrackSection>
                {
                    PhysicalSection(200, 300),
                    PhysicalSection(0, 100)
                },
                // A flat segment already covered by a physical section: the promotion
                // loop skips it, so nothing is added / clipped / reconciled and only
                // the re-sort fires.
                OrbitSegments = new List<OrbitSegment> { Segment(0, 100) },
                FilesDirty = false
            };

            var stats = OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: true);

            Assert.Equal(1, stats.Resorted);
            Assert.True(stats.AnyMutation);
            Assert.True(stats.SectionOrdinalsShifted);
            Assert.Equal(0, rec.TrackSections[0].startUT);
            Assert.True(rec.FilesDirty);
        }

        // --- bug 2: annotations must not survive an ordinal shift --------------

        // Repro (fails on origin/main): the spline / outlier flags / anchor
        // candidates fitted for section index 1 survive an Ensure that re-sorts a
        // freshly promoted checkpoint section into index 1, so they silently start
        // applying to a DIFFERENT section.
        [Fact]
        public void Ensure_ShiftingSectionOrdinals_InvalidatesSectionAnnotations()
        {
            const string id = "annotation-desync";
            Recording rec = GapPromotionRecording(id);

            // Index 1 is the trailing physical section [200,300] before the pass.
            Assert.Equal(200, rec.TrackSections[1].startUT);

            SectionAnnotationStore.PutSmoothingSpline(id, 1, Spline(200));
            SectionAnnotationStore.PutAnchorCandidates(id, 1, new[]
            {
                new AnchorCandidate(250, AnchorSource.OrbitalCheckpoint, AnchorSide.Start)
            });
            SectionAnnotationStore.PutOutlierFlags(id, 1, new OutlierFlags
            {
                SectionIndex = 1,
                SampleCount = 2,
                RejectedCount = 1,
                PackedBitmap = new byte[] { 0x01 }
            });

            var stats = OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: false);

            // The ordinal really did move: index 1 now names the promoted checkpoint.
            Assert.True(stats.SectionOrdinalsShifted);
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, rec.TrackSections[1].referenceFrame);
            Assert.Equal(100, rec.TrackSections[1].startUT);
            Assert.Equal(200, rec.TrackSections[2].startUT);

            // ...so nothing keyed on the old ordinals may survive.
            Assert.False(SectionAnnotationStore.TryGetSmoothingSpline(id, 1, out _));
            Assert.Equal(0, SectionAnnotationStore.GetSplineCountForRecording(id));
            Assert.Equal(0, SectionAnnotationStore.GetAnchorCandidateSectionCountForRecording(id));
            Assert.Equal(0, SectionAnnotationStore.GetOutlierFlagsCountForRecording(id));
            Assert.Contains(logLines,
                l => l.Contains("[Pipeline-Smoothing]")
                    && l.Contains("Section annotations invalidated (section-index-shift)")
                    && l.Contains("recordingId=" + id));
        }

        // Removing a covered empty shell renumbers everything after it. Same class,
        // different mutation (ReconciledEmptySections rather than Resorted).
        [Fact]
        public void Ensure_EmptyShellReconcile_InvalidatesSectionAnnotations()
        {
            const string id = "annotation-desync-reconcile";
            var rec = new Recording
            {
                RecordingId = id,
                TrackSections = new List<TrackSection>
                {
                    PhysicalSection(0, 100),
                    EmptyAbsoluteSection(0, 100),
                    PhysicalSection(100, 200)
                },
                OrbitSegments = new List<OrbitSegment>()
            };
            SectionAnnotationStore.PutSmoothingSpline(id, 2, Spline(100));

            var stats = OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: false);

            Assert.Equal(1, stats.ReconciledEmptySections);
            Assert.True(stats.SectionOrdinalsShifted);
            Assert.Equal(2, rec.TrackSections.Count);
            Assert.Equal(0, SectionAnnotationStore.GetSplineCountForRecording(id));
        }

        // Anti-over-invalidation: an Ensure that changes nothing must leave the
        // annotations in place, or every optimizer pass would silently downgrade
        // ghost rendering to the legacy lerp path until the next commit.
        [Fact]
        public void Ensure_NoSectionChange_KeepsSectionAnnotations()
        {
            const string id = "annotation-kept";
            Recording rec = GapPromotionRecording(id);

            // First pass normalizes (and would invalidate, but nothing is stored yet).
            OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: false);

            SectionAnnotationStore.PutSmoothingSpline(id, 2, Spline(200));

            var stats = OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: false);

            Assert.False(stats.SectionOrdinalsShifted);
            Assert.True(SectionAnnotationStore.TryGetSmoothingSpline(id, 2, out SmoothingSpline kept));
            Assert.True(kept.IsValid);
            Assert.Equal(1, SectionAnnotationStore.GetSplineCountForRecording(id));
        }

        // Anti-over-invalidation, promotion variant: a chronologically-trailing
        // promotion APPENDS at the tail and renumbers nothing, so annotations for
        // the existing sections must survive it.
        [Fact]
        public void Ensure_TrailingPromotionWithoutResort_KeepsSectionAnnotations()
        {
            const string id = "annotation-kept-trailing";
            var rec = new Recording
            {
                RecordingId = id,
                TrackSections = new List<TrackSection>
                {
                    PhysicalSection(0, 100),
                    PhysicalSection(100, 200)
                },
                OrbitSegments = new List<OrbitSegment> { Segment(200, 300) }
            };
            SectionAnnotationStore.PutSmoothingSpline(id, 1, Spline(100));

            var stats = OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: false);

            Assert.Equal(1, stats.Added);
            Assert.Equal(0, stats.Resorted);
            Assert.False(stats.SectionOrdinalsShifted);
            Assert.Equal(3, rec.TrackSections.Count);
            Assert.Equal(100, rec.TrackSections[1].startUT);
            Assert.True(SectionAnnotationStore.TryGetSmoothingSpline(id, 1, out _));
        }

        // The live on-rails append path runs the same clip / reconcile / re-sort
        // machinery, so it carries the same containment.
        [Fact]
        public void Append_ShiftingSectionOrdinals_InvalidatesSectionAnnotations()
        {
            const string id = "annotation-desync-append";
            var rec = new Recording
            {
                RecordingId = id,
                TrackSections = new List<TrackSection>
                {
                    PhysicalSection(0, 100),
                    PhysicalSection(300, 400)
                },
                OrbitSegments = new List<OrbitSegment>()
            };
            SectionAnnotationStore.PutSmoothingSpline(id, 1, Spline(300));

            bool appended = OrbitSegmentCheckpointBridge.TryAppendClosedCheckpointSection(
                rec, Segment(100, 200), markDirty: false, out string skipReason);

            Assert.True(appended, skipReason);
            Assert.Equal(100, rec.TrackSections[1].startUT);
            Assert.Equal(0, SectionAnnotationStore.GetSplineCountForRecording(id));
        }

        // Anti-over-invalidation on the append path: an in-order tail append moves
        // no existing ordinal.
        [Fact]
        public void Append_InOrderTailAppend_KeepsSectionAnnotations()
        {
            const string id = "annotation-kept-append";
            var rec = new Recording
            {
                RecordingId = id,
                TrackSections = new List<TrackSection>
                {
                    PhysicalSection(0, 100),
                    PhysicalSection(100, 200)
                },
                OrbitSegments = new List<OrbitSegment>()
            };
            SectionAnnotationStore.PutSmoothingSpline(id, 1, Spline(100));

            bool appended = OrbitSegmentCheckpointBridge.TryAppendClosedCheckpointSection(
                rec, Segment(200, 300), markDirty: false, out string skipReason);

            Assert.True(appended, skipReason);
            Assert.Equal(3, rec.TrackSections.Count);
            Assert.Equal(100, rec.TrackSections[1].startUT);
            Assert.True(SectionAnnotationStore.TryGetSmoothingSpline(id, 1, out _));
        }

        // The containment is per-recording: a sibling recording's annotations are
        // untouched by another recording's normalization.
        [Fact]
        public void Ensure_Invalidation_IsScopedToTheMutatedRecording()
        {
            const string mutated = "annotation-scope-mutated";
            const string sibling = "annotation-scope-sibling";
            Recording rec = GapPromotionRecording(mutated);
            SectionAnnotationStore.PutSmoothingSpline(mutated, 1, Spline(200));
            SectionAnnotationStore.PutSmoothingSpline(sibling, 1, Spline(200));

            OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: false);

            Assert.Equal(0, SectionAnnotationStore.GetSplineCountForRecording(mutated));
            Assert.Equal(1, SectionAnnotationStore.GetSplineCountForRecording(sibling));
        }
    }
}
