using System;
using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.Tests.Analyzer;
using Parsek.Tests.Analyzer.Rules;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Producer-side anti-double-cover regression tests for OrbitSegmentCheckpointBridge.
    /// Guards the two overlap sub-patterns the offline analyzer (INV2-NO-DOUBLE-COVER)
    /// confirmed across real saves (c1, s15, orbital supply route, mun transfer mission):
    ///   1. an EMPTY physical shell (Absolute, frames=0) duplicating a checkpoint span
    ///      (c1 rec 1cc165308fbb4ac78016c28462bb17ff sections [22]+[23]);
    ///   2. a coarse checkpoint envelope [X,Z] coexisting with sub-spans [X,Y]+[Y,Z]
    ///      (c1 rec 6a15ca9e-d734-4c99-9da5-7be92d2974ab sections [24]-[26]).
    /// </summary>
    [Collection("Sequential")]
    public class CheckpointDoubleCoverTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool prevSuppress;

        public CheckpointDoubleCoverTests()
        {
            prevSuppress = RecordingStore.SuppressLogging;
            RecordingStore.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = prevSuppress;
        }

        // --- fixture helpers -------------------------------------------------

        private static OrbitSegment Segment(double startUT, double endUT, double sma = 700000)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                inclination = 0,
                eccentricity = 0.1,
                semiMajorAxis = sma,
                longitudeOfAscendingNode = 10,
                argumentOfPeriapsis = 20,
                meanAnomalyAtEpoch = 0.5,
                epoch = startUT,
                bodyName = "Kerbin"
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

        private static TrackSection EmptyCheckpointSection(double startUT, double endUT)
        {
            TrackSection section = OrbitSegmentCheckpointBridge.BuildOpenCheckpointSection(startUT);
            section.endUT = endUT;
            return section;
        }

        private static List<Finding> Inv2Findings(Recording rec)
        {
            var model = new AnalyzerModel
            {
                SaveName = "double-cover-tests",
                Recordings = new List<Recording> { rec }
            };
            return new Inv2NoDoubleCover().Evaluate(model).ToList();
        }

        private static void AssertNoDoubleCover(Recording rec)
        {
            List<Finding> overlaps = Inv2Findings(rec)
                .Where(f => f.RuleId == Inv2NoDoubleCover.OverlapRuleId)
                .ToList();
            Assert.True(overlaps.Count == 0,
                "expected no INV2 overlap findings, got: "
                + string.Join(" | ", overlaps.Select(f => f.Message)));
        }

        // --- sub-pattern 1: empty physical shell duplicating a checkpoint span ---

        // Guards: the exact c1 shape — an empty Absolute shell (frames=0) and a closed
        // checkpoint section both spanning the same on-rails window. The shell carries
        // no playable data; the checkpoint owns the span, so Ensure must remove the shell.
        [Fact]
        public void Ensure_EmptyAbsoluteShellDuplicatingCheckpointSpan_RemovesShell()
        {
            OrbitSegment seg = Segment(233256, 234304);
            var rec = new Recording
            {
                RecordingId = "shell-dup",
                TrackSections = new List<TrackSection>
                {
                    PhysicalSection(233200, 233256),
                    EmptyAbsoluteSection(233256, 234304),
                    OrbitSegmentCheckpointBridge.BuildClosedCheckpointSection(seg)
                },
                OrbitSegments = new List<OrbitSegment> { seg }
            };

            var stats = OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: false);

            Assert.Equal(1, stats.ReconciledEmptySections);
            Assert.Equal(2, rec.TrackSections.Count);
            Assert.DoesNotContain(rec.TrackSections,
                s => s.referenceFrame == ReferenceFrame.Absolute
                    && (s.frames == null || s.frames.Count == 0));
            AssertNoDoubleCover(rec);
        }

        // Guards: the same shape when the checkpoint section does not exist yet —
        // promotion must add the checkpoint AND reconcile the shell in one pass.
        [Fact]
        public void Ensure_EmptyAbsoluteShellWithFlatSegmentOnly_PromotesAndRemovesShell()
        {
            OrbitSegment seg = Segment(1000, 2000);
            var rec = new Recording
            {
                RecordingId = "shell-promote",
                TrackSections = new List<TrackSection>
                {
                    EmptyAbsoluteSection(1000, 2000)
                },
                OrbitSegments = new List<OrbitSegment> { seg }
            };

            OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: false);

            Assert.Single(rec.TrackSections);
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, rec.TrackSections[0].referenceFrame);
            AssertNoDoubleCover(rec);
        }

        // Guards: the reconcile must run even when the recording has NO flat orbit
        // segments (atmospheric/surface-only recordings) - the old early return
        // skipped the whole pass for that population, so an empty shell double-
        // covering a physical section survived every rewrite.
        [Fact]
        public void Ensure_NoOrbitSegments_StillReconcilesCoveredShell()
        {
            var rec = new Recording
            {
                RecordingId = "no-orbit-segments",
                TrackSections = new List<TrackSection>
                {
                    PhysicalSection(1000, 2000),
                    EmptyAbsoluteSection(1000, 2000)
                },
                OrbitSegments = new List<OrbitSegment>()
            };

            var stats = OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: false);

            Assert.Equal(1, stats.ReconciledEmptySections);
            Assert.True(stats.Changed);
            Assert.Single(rec.TrackSections);
            AssertNoDoubleCover(rec);

            // Read-gate variant: same shape stays untouched with reconcile off.
            var recRead = new Recording
            {
                RecordingId = "no-orbit-segments-read",
                TrackSections = new List<TrackSection>
                {
                    PhysicalSection(1000, 2000),
                    EmptyAbsoluteSection(1000, 2000)
                },
                OrbitSegments = new List<OrbitSegment>()
            };
            var readStats = OrbitSegmentCheckpointBridge.EnsureCheckpointSectionsForTopLevelOrbitSegments(
                recRead, markDirty: false, reconcileEmptySections: false);
            Assert.False(readStats.Changed);
            Assert.Equal(2, recRead.TrackSections.Count);
        }

        // Guards: an empty shell whose span is NOT covered by any payload section is a
        // legitimate coverage marker and must be kept untouched.
        [Fact]
        public void Ensure_UncoveredEmptyShell_IsKept()
        {
            // The non-overlapping flat segment [300,400] makes Ensure run its full pass
            // (it early-returns on an empty OrbitSegments list) so the shell's survival
            // is proven against an executed reconcile, not a no-op.
            var rec = new Recording
            {
                RecordingId = "shell-kept",
                TrackSections = new List<TrackSection>
                {
                    PhysicalSection(0, 100),
                    EmptyAbsoluteSection(100, 200)
                },
                OrbitSegments = new List<OrbitSegment> { Segment(300, 400) }
            };

            var stats = OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: false);

            Assert.Equal(1, stats.Added);
            Assert.Equal(0, stats.ReconciledEmptySections);
            Assert.Equal(3, rec.TrackSections.Count);
            Assert.Contains(rec.TrackSections,
                s => s.referenceFrame == ReferenceFrame.Absolute
                    && s.startUT == 100 && s.endUT == 200
                    && (s.frames == null || s.frames.Count == 0));
        }

        // Guards: a partially covered shell is trimmed to the uncovered remainder rather
        // than removed outright (the uncovered tail still documents a genuine gap) and
        // rather than left overlapping (INV2 FAIL).
        [Fact]
        public void Ensure_PartiallyCoveredEmptyShell_IsTrimmedToRemainder()
        {
            OrbitSegment seg = Segment(1000, 1500);
            var rec = new Recording
            {
                RecordingId = "shell-trim",
                TrackSections = new List<TrackSection>
                {
                    EmptyAbsoluteSection(1000, 2000),
                    OrbitSegmentCheckpointBridge.BuildClosedCheckpointSection(seg)
                },
                OrbitSegments = new List<OrbitSegment> { seg }
            };

            var stats = OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: false);

            Assert.Equal(1, stats.ReconciledEmptySections);
            TrackSection trimmed = rec.TrackSections.Single(
                s => s.referenceFrame == ReferenceFrame.Absolute);
            Assert.Equal(1500, trimmed.startUT, 6);
            Assert.Equal(2000, trimmed.endUT, 6);
            AssertNoDoubleCover(rec);
        }

        // Guards: seam-flagged bookkeeping sections are producer artifacts the optimizer
        // depends on and must never be reconciled away, covered or not.
        [Fact]
        public void Ensure_BoundarySeamSection_IsNeverReconciled()
        {
            OrbitSegment seg = Segment(1000, 2000);
            TrackSection seam = EmptyAbsoluteSection(1000, 2000);
            seam.isBoundarySeam = true;
            var rec = new Recording
            {
                RecordingId = "seam-kept",
                TrackSections = new List<TrackSection>
                {
                    seam,
                    OrbitSegmentCheckpointBridge.BuildClosedCheckpointSection(seg)
                },
                OrbitSegments = new List<OrbitSegment> { seg }
            };

            var stats = OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: false);

            Assert.Equal(0, stats.ReconciledEmptySections);
            Assert.Contains(rec.TrackSections, s => s.isBoundarySeam);
        }

        // --- sub-pattern 2: coarse envelope vs finer sub-span checkpoints ---

        // Guards: the exact c1 envelope shape — an unattached empty checkpoint shell
        // [X,Y], a closed checkpoint [Y,Z], and a coarse flat envelope segment [X,Z].
        // The envelope must be clipped to the uncovered remainder [X,Y] (attaching into
        // the empty shell), never promoted whole over the finer sections.
        [Fact]
        public void Ensure_FlatEnvelopeOverExistingCheckpoints_ClipsAndAttachesIntoEmptyShell()
        {
            const double X = 288785, Y = 290565, Z = 453543;
            OrbitSegment sub = Segment(Y, Z, sma: 800000);
            OrbitSegment envelope = Segment(X, Z);
            var rec = new Recording
            {
                RecordingId = "envelope-attach",
                TrackSections = new List<TrackSection>
                {
                    EmptyCheckpointSection(X, Y),
                    OrbitSegmentCheckpointBridge.BuildClosedCheckpointSection(sub)
                },
                OrbitSegments = new List<OrbitSegment> { envelope }
            };

            var stats = OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: false);

            Assert.True(stats.Clipped > 0);
            Assert.Equal(2, rec.TrackSections.Count);
            Assert.All(rec.TrackSections, s =>
            {
                Assert.Equal(ReferenceFrame.OrbitalCheckpoint, s.referenceFrame);
                Assert.NotNull(s.checkpoints);
                Assert.Single(s.checkpoints);
            });
            // No section may span the whole envelope.
            Assert.DoesNotContain(rec.TrackSections,
                s => s.endUT - s.startUT > (Z - Y) + 1);
            AssertNoDoubleCover(rec);
            // Flat cache rebuilt from section content: two sub-spans, no envelope.
            Assert.Equal(2, rec.OrbitSegments.Count);
            Assert.Equal(X, rec.OrbitSegments[0].startUT, 6);
            Assert.Equal(Y, rec.OrbitSegments[0].endUT, 6);
            Assert.Equal(Y, rec.OrbitSegments[1].startUT, 6);
            Assert.Equal(Z, rec.OrbitSegments[1].endUT, 6);
        }

        // Guards: a flat envelope fully covered by existing checkpoint sections is not
        // promoted at all, and the pass converges (second run makes no changes — the
        // NaN-sma geometric-explosion regression shape, but for span coverage).
        [Fact]
        public void Ensure_FlatEnvelopeFullyCovered_IsSkippedAndConverges()
        {
            OrbitSegment subA = Segment(1000, 2000);
            OrbitSegment subB = Segment(2000, 3000, sma: 800000);
            var rec = new Recording
            {
                RecordingId = "envelope-covered",
                TrackSections = new List<TrackSection>
                {
                    OrbitSegmentCheckpointBridge.BuildClosedCheckpointSection(subA),
                    OrbitSegmentCheckpointBridge.BuildClosedCheckpointSection(subB)
                },
                OrbitSegments = new List<OrbitSegment> { Segment(1000, 3000, sma: 900000) }
            };

            var stats = OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: false);

            Assert.Equal(0, stats.Added);
            Assert.True(stats.SkippedCovered > 0);
            Assert.Equal(2, rec.TrackSections.Count);
            Assert.Equal(2, rec.OrbitSegments.Count);
            AssertNoDoubleCover(rec);

            var second = OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: false);
            Assert.False(second.Changed);
            Assert.Equal(2, rec.TrackSections.Count);
        }

        // --- live append path (BackgroundRecorder CloseOrbitSegment) ---

        // Guards newest-wins: a live close enveloping an existing checkpoint section
        // (carrying different, older elements) replaces it entirely - the fresh close
        // is the newer truth, and the span must not be double-covered.
        [Fact]
        public void TryAppend_NewestWins_FullyCoveredExistingSection_IsReplaced()
        {
            OrbitSegment existing = Segment(2000, 3000);
            var rec = new Recording
            {
                RecordingId = "append-clip",
                TrackSections = new List<TrackSection>
                {
                    OrbitSegmentCheckpointBridge.BuildClosedCheckpointSection(existing)
                },
                OrbitSegments = new List<OrbitSegment> { existing }
            };

            OrbitSegment fresh = Segment(1000, 3000, sma: 800000);
            bool appended = OrbitSegmentCheckpointBridge.TryAppendClosedCheckpointSection(
                rec, fresh, markDirty: false, out string skipReason);

            Assert.True(appended);
            Assert.Null(skipReason);
            TrackSection only = Assert.Single(rec.TrackSections);
            Assert.Equal(1000, only.startUT, 6);
            Assert.Equal(3000, only.endUT, 6);
            Assert.Equal(800000, only.checkpoints[0].semiMajorAxis, 3);
            AssertNoDoubleCover(rec);
            // Flat cache rebuilt to match sections: the stale entry is gone.
            OrbitSegment onlyFlat = Assert.Single(rec.OrbitSegments);
            Assert.Equal(800000, onlyFlat.semiMajorAxis, 3);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]")
                && l.Contains("TryAppendClosedCheckpointSection: reconciled overlap")
                && l.Contains("recording=append-clip")
                && l.Contains("clippedExistingSections=1"));
        }

        // Guards newest-wins: a fresh close landing INSIDE a stale coarse envelope
        // splits the envelope around it; the fresh elements own the middle span.
        // Pre-fix (sections-win) the fresh close was discarded entirely, losing the
        // newly recorded orbit.
        [Fact]
        public void TryAppend_NewestWins_InsideStaleEnvelope_SplitsEnvelopeAroundFreshClose()
        {
            OrbitSegment envelope = Segment(1000, 3000);
            var rec = new Recording
            {
                RecordingId = "append-split",
                TrackSections = new List<TrackSection>
                {
                    OrbitSegmentCheckpointBridge.BuildClosedCheckpointSection(envelope)
                },
                OrbitSegments = new List<OrbitSegment> { envelope }
            };

            bool appended = OrbitSegmentCheckpointBridge.TryAppendClosedCheckpointSection(
                rec, Segment(1500, 2500, sma: 800000), markDirty: false, out string skipReason);

            Assert.True(appended);
            Assert.Null(skipReason);
            Assert.Equal(3, rec.TrackSections.Count);
            Assert.Equal(1000, rec.TrackSections[0].startUT, 6);
            Assert.Equal(1500, rec.TrackSections[0].endUT, 6);
            Assert.Equal(1500, rec.TrackSections[1].startUT, 6);
            Assert.Equal(2500, rec.TrackSections[1].endUT, 6);
            Assert.Equal(800000, rec.TrackSections[1].checkpoints[0].semiMajorAxis, 3);
            Assert.Equal(2500, rec.TrackSections[2].startUT, 6);
            Assert.Equal(3000, rec.TrackSections[2].endUT, 6);
            AssertNoDoubleCover(rec);
            Assert.Equal(3, rec.OrbitSegments.Count);
        }

        // Guards: physical sections (real recorded frames) still own their spans -
        // a live close fully inside physical coverage skips with an explicit reason.
        [Fact]
        public void TryAppend_CoveredByPhysicalSection_SkipsWithCoveredReason()
        {
            var rec = new Recording
            {
                RecordingId = "append-covered",
                TrackSections = new List<TrackSection>
                {
                    PhysicalSection(1000, 3000)
                },
                OrbitSegments = new List<OrbitSegment>()
            };

            bool appended = OrbitSegmentCheckpointBridge.TryAppendClosedCheckpointSection(
                rec, Segment(1500, 2500, sma: 800000), markDirty: false, out string skipReason);

            Assert.False(appended);
            Assert.Equal("covered", skipReason);
            Assert.Single(rec.TrackSections);
        }

        // Guards idempotence under newest-wins: an exact re-append of an existing
        // (non-last) checkpoint must skip, not churn the section it duplicates.
        [Fact]
        public void TryAppend_ExactDuplicateOfNonLastSection_SkipsAsDuplicate()
        {
            OrbitSegment first = Segment(1000, 2000);
            OrbitSegment last = Segment(3000, 4000, sma: 800000);
            var rec = new Recording
            {
                RecordingId = "append-dup-nonlast",
                TrackSections = new List<TrackSection>
                {
                    OrbitSegmentCheckpointBridge.BuildClosedCheckpointSection(first),
                    OrbitSegmentCheckpointBridge.BuildClosedCheckpointSection(last)
                },
                OrbitSegments = new List<OrbitSegment> { first, last }
            };

            bool appended = OrbitSegmentCheckpointBridge.TryAppendClosedCheckpointSection(
                rec, first, markDirty: false, out string skipReason);

            Assert.False(appended);
            Assert.Equal("duplicate", skipReason);
            Assert.Equal(2, rec.TrackSections.Count);
        }

        // Guards: the pre-existing exact-duplicate fast path is unchanged.
        [Fact]
        public void TryAppend_ExactDuplicateOfLastSection_SkipsAsDuplicateLast()
        {
            OrbitSegment existing = Segment(1000, 2000);
            var rec = new Recording
            {
                RecordingId = "append-dup-last",
                TrackSections = new List<TrackSection>
                {
                    OrbitSegmentCheckpointBridge.BuildClosedCheckpointSection(existing)
                },
                OrbitSegments = new List<OrbitSegment> { existing }
            };

            bool appended = OrbitSegmentCheckpointBridge.TryAppendClosedCheckpointSection(
                rec, existing, markDirty: false, out string skipReason);

            Assert.False(appended);
            Assert.Equal("duplicate-last", skipReason);
            Assert.Single(rec.TrackSections);
        }

        // Guards: the append path also removes an empty physical shell whose span the
        // freshly appended checkpoint now covers (sub-pattern 1 at its earliest producer
        // moment, before any Ensure pass runs).
        [Fact]
        public void TryAppend_RemovesEmptyShellCoveredByAppendedCheckpoint()
        {
            var rec = new Recording
            {
                RecordingId = "append-shell",
                TrackSections = new List<TrackSection>
                {
                    EmptyAbsoluteSection(1000, 2000)
                },
                OrbitSegments = new List<OrbitSegment>()
            };

            bool appended = OrbitSegmentCheckpointBridge.TryAppendClosedCheckpointSection(
                rec, Segment(1000, 2000), markDirty: false, out _);

            Assert.True(appended);
            Assert.Single(rec.TrackSections);
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, rec.TrackSections[0].referenceFrame);
            AssertNoDoubleCover(rec);
        }

        // Guards the immutability line: the sidecar READ path calls Ensure with
        // reconcileEmptySections:false (and markDirty:true), so loading an old committed
        // recording must NOT mutate its existing double-covered sections — that would
        // rewrite (migrate) old on-disk data on the next save. Old saves stay
        // analyzer-RED by design.
        [Fact]
        public void Ensure_WithReconcileDisabled_LeavesDoubleCoverUntouched()
        {
            OrbitSegment seg = Segment(233256, 234304);
            var rec = new Recording
            {
                RecordingId = "read-path-untouched",
                TrackSections = new List<TrackSection>
                {
                    EmptyAbsoluteSection(233256, 234304),
                    OrbitSegmentCheckpointBridge.BuildClosedCheckpointSection(seg)
                },
                OrbitSegments = new List<OrbitSegment> { seg }
            };

            var stats = OrbitSegmentCheckpointBridge.EnsureCheckpointSectionsForTopLevelOrbitSegments(
                rec, markDirty: false, reconcileEmptySections: false);

            Assert.Equal(0, stats.ReconciledEmptySections);
            Assert.False(stats.Changed);
            Assert.Equal(2, rec.TrackSections.Count);
            Assert.Contains(rec.TrackSections,
                s => s.referenceFrame == ReferenceFrame.Absolute
                    && (s.frames == null || s.frames.Count == 0));
        }

        // Guards the flat-cache rebuild's preservation rule: flat-only segments
        // (a predicted terminal tail AND a real segment sitting after it) whose spans
        // no payload section covers must survive the rebuild. The old pure-suffix
        // rule (FindPredictedTailStart) returned -1 for the interleaved
        // [real, predicted, real] shape and silently dropped both.
        [Fact]
        public void Ensure_RebuildPreservesUncoveredPredictedTailAndTrailingFlatSegment()
        {
            OrbitSegment sectioned = Segment(1000, 2000);
            OrbitSegment coveredCandidate = Segment(1000, 1500, sma: 800000);
            var predictedTail = Segment(5000, 6000, sma: 900000);
            predictedTail.isPredicted = true;
            OrbitSegment trailingReal = Segment(7000, 8000, sma: 950000);

            var rec = new Recording
            {
                RecordingId = "rebuild-preserve",
                TrackSections = new List<TrackSection>
                {
                    OrbitSegmentCheckpointBridge.BuildClosedCheckpointSection(sectioned)
                },
                // coveredCandidate triggers SkippedCovered -> flat rebuild;
                // predictedTail + trailingReal form the interleaved tail shape.
                OrbitSegments = new List<OrbitSegment>
                {
                    coveredCandidate, predictedTail, trailingReal
                }
            };

            var stats = OrbitSegmentCheckpointBridge
                .EnsureCheckpointSectionsForTopLevelOrbitSegments(rec, markDirty: false);

            Assert.True(stats.SkippedCovered > 0);
            Assert.Equal(3, rec.OrbitSegments.Count);
            Assert.Equal(1000, rec.OrbitSegments[0].startUT, 6);
            Assert.Equal(2000, rec.OrbitSegments[0].endUT, 6);
            Assert.True(rec.OrbitSegments[1].isPredicted);
            Assert.Equal(5000, rec.OrbitSegments[1].startUT, 6);
            Assert.Equal(7000, rec.OrbitSegments[2].startUT, 6);
            Assert.False(rec.OrbitSegments[2].isPredicted);
        }

        // --- wrapper logging contract ---

        // Guards: the RecordingStore wrapper logs the reconcile counter so a KSP.log
        // grep can attribute a section-count change to the anti-double-cover pass.
        [Fact]
        public void EnsureWrapper_LogsReconciledEmptySections()
        {
            OrbitSegment seg = Segment(1000, 2000);
            var rec = new Recording
            {
                RecordingId = "wrapper-log",
                TrackSections = new List<TrackSection>
                {
                    EmptyAbsoluteSection(1000, 2000),
                    OrbitSegmentCheckpointBridge.BuildClosedCheckpointSection(seg)
                },
                OrbitSegments = new List<OrbitSegment> { seg }
            };

            RecordingStore.EnsureCheckpointSectionsForTopLevelOrbitSegments(
                rec, markDirty: false, context: "double-cover-test");

            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]")
                && l.Contains("recording=wrapper-log")
                && l.Contains("reconciledEmptySections=1"));
        }

        // --- end-to-end: RecordingBuilder fixture through the codec write path ---

        // Guards: a RecordingBuilder fixture carrying BOTH real-save sub-pattern shapes
        // comes out of a full deserialize + serialize cycle (the write path runs the
        // bridge) with no INV2 double-cover — the analyzer contract this fix restores.
        [Fact]
        public void EndToEnd_BuilderFixtureWithBothSubPatterns_SerializesWithoutDoubleCover()
        {
            OrbitSegment railsA = Segment(233256, 234304);
            OrbitSegment subB = Segment(290565, 453543, sma: 800000);

            ConfigNode node = new RecordingBuilder("Double Cover Probe")
                .WithRecordingId("e2e-double-cover")
                // sub-pattern 1: empty Absolute shell + checkpoint over the same span
                .AddTrackSection(
                    SegmentEnvironment.ExoBallistic, ReferenceFrame.Absolute,
                    TrackSectionSource.Active, 233256, 234304)
                .AddOrbitalCheckpointSection(233256, 234304, railsA)
                // sub-pattern 2: empty checkpoint shell + closed sub-span, envelope in flat
                .AddTrackSection(
                    SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint,
                    TrackSectionSource.Checkpoint, 288785, 290565)
                .AddOrbitalCheckpointSection(290565, 453543, subB)
                .AddOrbitSegment(288785, 453543, ecc: 0.1, epoch: 288785)
                .BuildTrajectoryNode();

            var rec = new Recording { RecordingId = "e2e-double-cover" };
            RecordingStore.DeserializeTrajectoryFrom(node, rec);

            // The write path runs the bridge (mirrors every sidecar save).
            var outNode = new ConfigNode("TEST");
            RecordingStore.SerializeTrajectoryInto(outNode, rec);

            AssertNoDoubleCover(rec);

            // Round-trip the serialized form and re-check: what lands on disk is clean.
            var restored = new Recording { RecordingId = "e2e-double-cover" };
            RecordingStore.DeserializeTrajectoryFrom(outNode, restored);
            AssertNoDoubleCover(restored);
        }
    }
}
