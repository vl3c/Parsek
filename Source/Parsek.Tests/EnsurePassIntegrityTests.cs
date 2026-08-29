using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Parsek;
using Parsek.Rendering;
using UnityEngine;
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
            RenderSessionState.ResetForTesting();
        }

        public void Dispose()
        {
            SectionAnnotationStore.ResetForTesting();
            RenderSessionState.ResetForTesting();
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

        // --- F1: the resolved anchor map rides the same ordinal ----------------

        private static AnchorCorrection Anchor(string recordingId, int sectionIndex, AnchorSide side)
        {
            return new AnchorCorrection(
                recordingId, sectionIndex, side, 250, new Vector3d(1, 2, 3),
                AnchorSource.OrbitalCheckpoint);
        }

        // RenderSessionState.Anchors is keyed by the SAME (recordingId, sectionIndex)
        // ordinal and is POPULATED FROM the candidates the store holds. Dropping only
        // the candidates would leave a stale ε bound to an index that now names a
        // different section — and the flight positioning path re-applies that ε every
        // frame, which is worse than the consistently-stale pair it replaced.
        [Fact]
        public void Ensure_ShiftingSectionOrdinals_AlsoClearsResolvedSessionAnchors()
        {
            const string id = "anchor-desync";
            const string sibling = "anchor-desync-sibling";
            Recording rec = GapPromotionRecording(id);

            RenderSessionState.PutAnchorWithPriority(Anchor(id, 1, AnchorSide.Start));
            RenderSessionState.PutAnchorWithPriority(Anchor(id, 1, AnchorSide.End));
            RenderSessionState.PutAnchorWithPriority(Anchor(sibling, 1, AnchorSide.Start));
            SectionAnnotationStore.PutAnchorCandidates(id, 1, new[]
            {
                new AnchorCandidate(250, AnchorSource.OrbitalCheckpoint, AnchorSide.Start)
            });
            Assert.Equal(3, RenderSessionState.Count);

            OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: false);

            Assert.False(RenderSessionState.TryLookup(id, 1, AnchorSide.Start, out _));
            Assert.False(RenderSessionState.TryLookup(id, 1, AnchorSide.End, out _));
            Assert.True(RenderSessionState.TryLookup(sibling, 1, AnchorSide.Start, out _),
                "the containment must be scoped to the recording whose ordinals moved");
            Assert.Equal(1, RenderSessionState.Count);
            Assert.Contains(logLines,
                l => l.Contains("Section annotations invalidated (section-index-shift)")
                    && l.Contains("sessionAnchors=2"));
        }

        // The anchor map alone is enough to make the pass "have something to drop":
        // a recording with no .pann annotations but a live ε must still be cleared.
        [Fact]
        public void Ensure_ShiftingSectionOrdinals_ClearsAnchorsEvenWithNoPannAnnotations()
        {
            const string id = "anchor-only-desync";
            Recording rec = GapPromotionRecording(id);
            RenderSessionState.PutAnchorWithPriority(Anchor(id, 1, AnchorSide.Start));

            OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: false);

            Assert.Equal(0, RenderSessionState.Count);
        }

        // Anti-over-invalidation for the anchor half.
        [Fact]
        public void Ensure_NoSectionChange_KeepsResolvedSessionAnchors()
        {
            const string id = "anchor-kept";
            Recording rec = GapPromotionRecording(id);
            OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: false);

            RenderSessionState.PutAnchorWithPriority(Anchor(id, 2, AnchorSide.Start));

            OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: false);

            Assert.True(RenderSessionState.TryLookup(id, 2, AnchorSide.Start, out _));
        }

        // --- F3: a detached synthetic snapshot must not invalidate the real one ---

        // Recording.BuildPreReFlyAnchorTrajectoryRecording hands a synthetic Recording
        // that SHARES the real recording's id but carries the PRE-Re-Fly section list
        // to the same TrajectoryTextSidecarCodec.SerializeTrajectoryInto seam real
        // recordings use. A shift there says nothing about the real recording's
        // ordinals, and the path writes a ConfigNode (no PersistAfterCommit refit),
        // so a drop would strand flight positioning on the lerp fallback.
        [Fact]
        public void Ensure_DetachedSyntheticSnapshot_DoesNotInvalidateTheRealRecording()
        {
            const string id = "detached-synthetic";
            Recording snapshot = GapPromotionRecording(id);
            snapshot.IsDetachedSyntheticSnapshot = true;

            SectionAnnotationStore.PutSmoothingSpline(id, 1, Spline(200));
            RenderSessionState.PutAnchorWithPriority(Anchor(id, 1, AnchorSide.Start));

            var stats = OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(snapshot, markDirty: false);

            // The snapshot's OWN sections are still normalized...
            Assert.True(stats.SectionOrdinalsShifted);
            Assert.Equal(3, snapshot.TrackSections.Count);
            // ...but the real recording's annotations are untouched.
            Assert.Equal(1, SectionAnnotationStore.GetSplineCountForRecording(id));
            Assert.True(RenderSessionState.TryLookup(id, 1, AnchorSide.Start, out _));
        }

        // The production builder must actually set the flag — a fixture-only bool
        // would leave the real path exposed.
        [Fact]
        public void PreReFlyAnchorSnapshotRecording_IsFlaggedDetachedSynthetic()
        {
            var rec = new Recording
            {
                RecordingId = "pre-refly-owner",
                PreReFlyAnchorSessionId = "sess-1",
                PreReFlyAnchorPoints = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 0, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 10, bodyName = "Kerbin" }
                },
                PreReFlyAnchorOrbitSegments = new List<OrbitSegment>(),
                PreReFlyAnchorTrackSections = new List<TrackSection>
                {
                    PhysicalSection(0, 10)
                }
            };

            Recording snapshot = rec.BuildPreReFlyAnchorTrajectoryRecording("sess-1");

            Assert.NotNull(snapshot);
            Assert.Equal(rec.RecordingId, snapshot.RecordingId);
            Assert.True(snapshot.IsDetachedSyntheticSnapshot,
                "the pre-Re-Fly anchor snapshot shares the real recording's id and must "
                + "not be able to invalidate its annotations");
            Assert.False(rec.IsDetachedSyntheticSnapshot);
        }

        // --- F4: SplitAtUT's guarded returns restore, so they must not drop -----

        // The opt-out exists so a caller that restores the pre-Ensure sections
        // byte-identically does not take an unrecoverable, unrefittable drop.
        [Fact]
        public void Ensure_WithInvalidationSuppressed_StillNormalizesButKeepsAnnotations()
        {
            const string id = "suppressed-invalidation";
            Recording rec = GapPromotionRecording(id);
            SectionAnnotationStore.PutSmoothingSpline(id, 1, Spline(200));
            RenderSessionState.PutAnchorWithPriority(Anchor(id, 1, AnchorSide.Start));

            var stats = OrbitSegmentCheckpointBridge.EnsureCheckpointSectionsForTopLevelOrbitSegments(
                rec, markDirty: false, reconcileEmptySections: true,
                invalidateSectionAnnotations: false);

            Assert.True(stats.SectionOrdinalsShifted);
            Assert.Equal(3, rec.TrackSections.Count);
            Assert.Equal(1, SectionAnnotationStore.GetSplineCountForRecording(id));
            Assert.True(RenderSessionState.TryLookup(id, 1, AnchorSide.Start, out _));
        }

        // --- F5: one comparator, or every pass re-sorts forever -----------------

        // EnsureTrackSectionsSorted CHECKS with CompareTrackSections and sorts via
        // SortTrackSections. If those ever disagreed, an already-sorted list would
        // read as unsorted on every pass: Resorted=1 forever, so every load would
        // dirty, flush and invalidate every recording. Second pass must be a no-op.
        [Fact]
        public void Ensure_SortIsStableAcrossPasses_SoResortDoesNotPingPong()
        {
            var rec = new Recording
            {
                RecordingId = "sort-stability",
                TrackSections = new List<TrackSection>
                {
                    PhysicalSection(200, 300),
                    PhysicalSection(0, 100),
                    EmptyAbsoluteSection(100, 150),
                    PhysicalSection(100, 200)
                },
                OrbitSegments = new List<OrbitSegment> { Segment(300, 400) }
            };

            var first = OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: false);
            Assert.Equal(1, first.Resorted);

            for (int pass = 0; pass < 3; pass++)
            {
                var repeat = OrbitSegmentCheckpointBridge
                    .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: false);
                Assert.Equal(0, repeat.Resorted);
                Assert.False(repeat.AnyMutation,
                    "a converged recording must not re-sort on every pass — that would "
                    + "dirty, flush and invalidate everything on every load");
            }
        }
    }

    /// <summary>
    /// Source gate fencing the <c>invalidateSectionAnnotations: false</c> opt-out on
    /// <c>EnsureCheckpointSectionsForTopLevelOrbitSegments</c>.
    ///
    /// <para>
    /// The opt-out disarms the ordinal-shift annotation containment, so it is sound
    /// for exactly ONE caller shape: a pass that runs Ensure purely to inspect the
    /// normalized list, restores the pre-Ensure sections byte-identically on every
    /// guarded return, and takes the drop itself on its committed path
    /// (<c>RecordingOptimizer.SplitAtUT</c>). A second production caller almost
    /// certainly does NOT restore, and would silently re-open the
    /// (recordingId, sectionIndex) desync this package closed — a reviewer can catch
    /// that only by knowing to look, so the gate looks instead. Mirrors
    /// <c>PolylineDriverWalkDeleteGateTests</c>' fence style.
    /// </para>
    /// </summary>
    public class EnsureInvalidationOptOutGateTests
    {
        private const string OptOutToken = "invalidateSectionAnnotations: false";

        [Fact]
        public void TheInvalidationOptOut_HasExactlyOneProductionCaller()
        {
            var callers = new List<string>();
            int total = 0;
            foreach (string file in Directory.EnumerateFiles(
                         ParsekSourceRoot(), "*.cs", SearchOption.AllDirectories))
            {
                string src = StripLineComments(File.ReadAllText(file));
                int occurrences = CountOccurrences(src, OptOutToken);
                if (occurrences == 0) continue;
                total += occurrences;
                callers.Add(Path.GetFileName(file) + " x" + occurrences.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            }

            Assert.True(total == 1 && callers.Count == 1
                    && callers[0] == "RecordingOptimizer.cs x1",
                "The ordinal-shift annotation-invalidation opt-out must have exactly one "
                + "production caller (RecordingOptimizer.SplitAtUT, which restores its "
                + "pre-Ensure sections byte-identically on every guarded return and takes "
                + "the drop itself once the split commits). Found: "
                + (callers.Count == 0 ? "<none>" : string.Join(", ", callers))
                + ". A new caller that does NOT restore re-opens the (recordingId, "
                + "sectionIndex) desync this gate exists to keep closed.");
        }

        [Fact]
        public void TheOptOutCaller_TakesTheDropOnItsCommittedPath()
        {
            // The opt-out DEFERS the drop; it must not delete it. SplitAtUT's committed
            // arm invalidates explicitly after SplitAtSection lands, because the cut
            // itself renumbers the head's sections.
            string src = StripLineComments(ReadParsekSource("RecordingOptimizer.cs"));
            Assert.Contains(OptOutToken, src);
            Assert.Contains(
                "OrbitSegmentCheckpointBridge.InvalidateSectionAnnotationsForOrdinalShift(", src);
        }

        private static string ParsekSourceRoot()
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(root, "Source", "Parsek");
            if (!Directory.Exists(path))
                path = Path.Combine(root, "Parsek");
            Assert.True(Directory.Exists(path), "Parsek source root not found at " + path);
            return path;
        }

        private static string ReadParsekSource(string relPath)
        {
            string path = Path.Combine(
                ParsekSourceRoot(), relPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "Source file not found at " + path);
            return File.ReadAllText(path);
        }

        // Strip line comments so the doc-comments that NAME the opt-out on purpose
        // (the bridge's contract note, this gate's own rationale) are not counted as
        // callers.
        private static string StripLineComments(string source)
        {
            var sb = new StringBuilder(source.Length);
            foreach (string line in source.Split('\n'))
            {
                int idx = line.IndexOf("//", StringComparison.Ordinal);
                sb.Append(idx >= 0 ? line.Substring(0, idx) : line);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            int idx = haystack.IndexOf(needle, StringComparison.Ordinal);
            while (idx >= 0)
            {
                count++;
                idx = haystack.IndexOf(needle, idx + needle.Length, StringComparison.Ordinal);
            }
            return count;
        }
    }

    /// <summary>
    /// The load-seam half of the .pann containment. The bridge's ordinal-shift
    /// invalidation cannot help here: at load, <c>TrajectorySidecarBinary.Read</c>'s
    /// Ensure can shift ordinals while the annotation store is still EMPTY, so there
    /// is nothing to drop — and <c>ClassifyDrift</c> then accepts the pre-shift
    /// <c>.pann</c> because none of its six cache-key fields is section-derived.
    /// <c>LoadOrCompute</c> therefore refuses the entries that are provably wrong:
    /// an ordinal at or past the end of the recording's section list.
    /// </summary>
    [Collection("Sequential")]
    public class PannotationsOrdinalBoundsTests : IDisposable
    {
        private readonly string tempDir;
        private readonly List<string> logLines = new List<string>();
        private const double KerbinRotationPeriod = 21549.425;

        public PannotationsOrdinalBoundsTests()
        {
            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek_pann_bounds_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
            SmoothingPipeline.ResetForTesting();
            TrajectoryMath.FrameTransform.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;

            CelestialBody fakeKerbin = TestBodyRegistry.CreateBody(
                "Kerbin", radius: 600000.0, gravParameter: 3.5316e12);
            SmoothingPipeline.BodyResolverForTesting =
                name => name == "Kerbin" ? fakeKerbin : null;
            TrajectoryMath.FrameTransform.RotationPeriodForTesting = b =>
                object.ReferenceEquals(b, fakeKerbin) ? KerbinRotationPeriod : double.NaN;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            SmoothingPipeline.ResetForTesting();
            TrajectoryMath.FrameTransform.ResetForTesting();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static TrackSection FittableSection(double startUT, int frameCount)
        {
            var frames = new List<TrajectoryPoint>(frameCount);
            for (int i = 0; i < frameCount; i++)
            {
                frames.Add(new TrajectoryPoint
                {
                    ut = startUT + i,
                    latitude = 0.1 + i * 0.01,
                    longitude = 1.0 + i * 0.05,
                    altitude = 80000 + i * 100,
                    rotation = Quaternion.identity,
                    bodyName = "Kerbin",
                });
            }
            return new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                startUT = startUT,
                endUT = startUT + frameCount - 1,
                frames = frames,
                checkpoints = new List<OrbitSegment>(),
                sampleRateHz = 1f,
            };
        }

        private static Recording TwoSectionRecording(string id)
        {
            var rec = new Recording
            {
                RecordingId = id,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration,
                SidecarEpoch = 1,
            };
            rec.TrackSections.Add(FittableSection(100.0, 10));
            rec.TrackSections.Add(FittableSection(200.0, 10));
            return rec;
        }

        // Repro of the load-seam resurrection route: a .pann carrying an entry for
        // section index 1 is read back against a recording that now has ONE section.
        // Every cache-key field still matches, so the file is accepted — but index 1
        // names no section, and installing it would hand a consumer a spline for a
        // section that does not exist.
        [Fact]
        public void LoadOrCompute_OrdinalPastEndOfSectionList_IsNotInstalled()
        {
            const string id = "pann-ordinal-bounds";
            string pannPath = Path.Combine(tempDir, id + ".pann");

            Recording writer = TwoSectionRecording(id);
            SmoothingPipeline.PersistAfterCommit(writer, pannPath);
            Assert.Equal(2, SectionAnnotationStore.GetSplineCountForRecording(id));
            Assert.True(File.Exists(pannPath));

            // Same recording id / epoch / format / config — no drift — but the section
            // list shrank underneath the cached file.
            Recording reader = TwoSectionRecording(id);
            reader.TrackSections.RemoveAt(1);
            SectionAnnotationStore.ResetForTesting();
            logLines.Clear();

            SmoothingPipeline.LoadOrCompute(reader, pannPath);

            // The read path ran (not a recompute) ...
            Assert.Contains(logLines, l => l.Contains("Pannotations read OK"));
            // ... and installed only the in-range ordinal.
            Assert.True(SectionAnnotationStore.TryGetSmoothingSpline(id, 0, out _));
            Assert.False(SectionAnnotationStore.TryGetSmoothingSpline(id, 1, out _),
                "an ordinal at or past the end of the section list names no section and "
                + "must never be installed");
            Assert.Equal(1, SectionAnnotationStore.GetSplineCountForRecording(id));
            Assert.Contains(logLines, l => l.Contains("[WARN][Pipeline-Sidecar]")
                && l.Contains("Pannotations ordinal out of range")
                && l.Contains("reason=section-index-out-of-range"));
        }

        // Anti-over-gating: an unchanged section list installs everything and warns
        // about nothing.
        [Fact]
        public void LoadOrCompute_AllOrdinalsInRange_InstallsEverythingAndDoesNotWarn()
        {
            const string id = "pann-ordinal-in-range";
            string pannPath = Path.Combine(tempDir, id + ".pann");

            Recording writer = TwoSectionRecording(id);
            SmoothingPipeline.PersistAfterCommit(writer, pannPath);

            Recording reader = TwoSectionRecording(id);
            SectionAnnotationStore.ResetForTesting();
            logLines.Clear();

            SmoothingPipeline.LoadOrCompute(reader, pannPath);

            Assert.Equal(2, SectionAnnotationStore.GetSplineCountForRecording(id));
            Assert.DoesNotContain(logLines,
                l => l.Contains("Pannotations ordinal out of range"));
        }

        [Theory]
        [InlineData(-1, 3, false)]
        [InlineData(0, 3, true)]
        [InlineData(2, 3, true)]
        [InlineData(3, 3, false)]
        [InlineData(0, 0, false)]
        public void IsInstallableSectionIndex_BoundsAreHalfOpen(
            int sectionIndex, int sectionCount, bool expected)
        {
            Assert.Equal(expected,
                SmoothingPipeline.IsInstallableSectionIndex(sectionIndex, sectionCount));
        }
    }
}
