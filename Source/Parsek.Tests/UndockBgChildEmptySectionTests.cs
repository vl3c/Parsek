using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.Analyzer;
using Parsek.Analyzer.Rules;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// UNDOCK-BG-CHILD-WRITES-RELATIVE-METRES-AS-FLAT-LAT-LON.
    ///
    /// <para>
    /// THE SHAPE. When a docked pair undocks in flight, the undocked half becomes a
    /// <c>BackgroundRecorder</c> subject whose section list opens with an ABSOLUTE
    /// section carrying ZERO frames (opened and closed 0.02 s apart at the undock
    /// instant), then a Relative section anchored to the active sibling, then Absolute
    /// sections. That one empty shell made
    /// <c>TrajectoryTextSidecarCodec.HasCompleteTrackSectionPayloadForFlatSync</c> and
    /// <c>TryBuildBodyFixedPrimaryFlatPointsForRelativeSections</c> answer "payload
    /// incomplete" for the WHOLE recording, so the sidecar was written flat-authoritative
    /// AND without the safe body-fixed substitution: the Relative section's anchor-local
    /// Cartesian METRES landed in the flat <c>POINT</c> list as lat/lon/alt, and
    /// <c>VesselSpawner.BackfillMaxDistance</c> resolved them through
    /// <c>GetWorldSurfacePosition</c> into <c>maxDist</c> ~735 km for a rover that sat
    /// ~1 m from the undock point. <c>MaxDistanceFromLaunch</c> feeds
    /// <c>IsIdleOnPad</c> / <c>IsPadFailure</c>, so the inflated value FAILS OPEN.
    /// </para>
    ///
    /// <para>
    /// The cells below pin the three fix sites in dependency order: the recorders no
    /// longer close a payload-free section into the list
    /// (<see cref="TrackSectionCloseClassifier"/>), the codec predicates SKIP one rather
    /// than giving up on the recording, and finalization routes maxDist through the
    /// Absolute-only walk whenever the recording carries sections at all.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class UndockBgChildEmptySectionTests : IDisposable
    {
        public UndockBgChildEmptySectionTests()
        {
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        // ---------- shared builders: the measured undock-child shape ----------

        private static TrajectoryPoint BodyFixedPoint(double ut)
        {
            // The real values off the relay fixture's rover B: KSC-area lat/lon.
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = -0.0936,
                longitude = -74.7253,
                altitude = 65.99,
                bodyName = "Kerbin",
            };
        }

        private static TrajectoryPoint AnchorLocalOffsetPoint(double ut)
        {
            // A Relative section's frame: anchor-local Cartesian METRES in the
            // latitude/longitude/altitude fields (CLAUDE.md RELATIVE contract). These are
            // the numbers that read as a lon-0.77 surface point when a flat reader
            // resolves them.
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = 0.0177,
                longitude = 0.7790,
                altitude = -0.0500,
                bodyName = "Kerbin",
            };
        }

        /// <summary>
        /// The measured undock background-child section list:
        /// [Absolute 0 frames][Relative frames + bodyFixedFrames][Absolute].
        /// </summary>
        private static Recording UndockChildRecording()
        {
            var emptyShell = new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 276.00,
                endUT = 276.02,
                frames = new List<TrajectoryPoint>(),
            };

            var relative = new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Relative,
                startUT = 276.02,
                endUT = 277.16,
                anchorRecordingId = "5f76d136e3dc4316bff71f4cfb0688a4",
                frames = new List<TrajectoryPoint>
                {
                    AnchorLocalOffsetPoint(276.02),
                    AnchorLocalOffsetPoint(276.60),
                    AnchorLocalOffsetPoint(277.16),
                },
                bodyFixedFrames = new List<TrajectoryPoint>
                {
                    BodyFixedPoint(276.02),
                    BodyFixedPoint(276.60),
                    BodyFixedPoint(277.16),
                },
            };

            var tail = new TrackSection
            {
                environment = SegmentEnvironment.SurfaceMobile,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 277.16,
                endUT = 293.18,
                frames = new List<TrajectoryPoint>
                {
                    BodyFixedPoint(277.16),
                    BodyFixedPoint(285.00),
                    BodyFixedPoint(293.18),
                },
            };

            var rec = new Recording
            {
                RecordingId = "49eaec92876041efa53deb1f5e5c96f4",
                VesselName = "rover B",
                ParentAnchorRecordingId = "5f76d136e3dc4316bff71f4cfb0688a4",
            };
            rec.TrackSections.AddRange(new[] { emptyShell, relative, tail });

            // The flat list as the dual-write produces it: the Relative section's
            // anchor-local metres verbatim, then the Absolute tail.
            rec.Points.AddRange(relative.frames);
            rec.Points.AddRange(tail.frames);
            return rec;
        }

        // ---------- Site 2: the codec predicates ----------

        // Guards the headline claim. On the measured undock-child shape BOTH codec
        // defences must be available: the safe body-fixed substitution builds, and the
        // sidecar is written section-authoritative. Before the fix both answered false
        // because of the leading zero-frame Absolute section, which is exactly how the
        // anchor-local metres reached the on-disk flat POINT list.
        [Fact]
        public void UndockChildShape_BothCodecDefencesAvailable()
        {
            Recording rec = UndockChildRecording();

            Assert.True(
                TrajectoryTextSidecarCodec.TryBuildBodyFixedPrimaryFlatPointsForRelativeSections(
                    rec, out List<TrajectoryPoint> safePoints),
                "a leading zero-frame section must not disable the body-fixed substitution");
            Assert.True(
                TrajectoryTextSidecarCodec.ShouldWriteSectionAuthoritativeTrajectory(rec),
                "a leading zero-frame section must not force the flat list to be authoritative");

            // The substituted list carries the section's BODY-FIXED samples, never the
            // anchor-local metres: no point may sit at the offset longitude.
            Assert.NotEmpty(safePoints);
            Assert.DoesNotContain(safePoints, p => Math.Abs(p.longitude - 0.7790) < 1e-9);
            Assert.All(safePoints, p => Assert.InRange(p.longitude, -180.0, 180.0));
        }

        // Guards the READ-side consequence, which is what repairs the recordings already
        // on disk: a damaged child loads with sectionAuthoritative=false and non-empty
        // sections, so TrajectorySidecarBinary.Read runs the malformed-flat-fallback heal.
        // That heal was unavailable for exactly the same reason the write-side
        // substitution was; with the empty shell skipped it now replaces the anchor-local
        // metres in the flat list with the section's body-fixed samples.
        [Fact]
        public void DamagedFlatList_HealsOnLoad()
        {
            Recording rec = UndockChildRecording();
            Assert.Contains(rec.Points, p => Math.Abs(p.longitude - 0.7790) < 1e-9);

            Assert.True(
                TrajectoryTextSidecarCodec.TryHealMalformedFlatFallbackTrajectoryFromTrackSections(
                    rec, allowRelativeSections: true),
                "a recording carrying anchor-local metres in its flat list must heal");

            Assert.DoesNotContain(rec.Points, p => Math.Abs(p.longitude - 0.7790) < 1e-9);
            Assert.All(rec.Points, p => Assert.InRange(p.longitude, -180.0, 180.0));
        }

        // Guards the "skip, do not give up" reading: a recording whose sections are ALL
        // payload-free still answers "no payload" (skipping must not manufacture one).
        [Fact]
        public void AllSectionsPayloadFree_StillReportsNoPayload()
        {
            var rec = new Recording { RecordingId = "empty-shells" };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 10.0,
                endUT = 15.0,
                frames = new List<TrajectoryPoint>(),
            });

            Assert.False(TrajectoryTextSidecarCodec.HasCompleteTrackSectionPayloadForFlatSync(
                rec.TrackSections, allowRelativeSections: true));
            Assert.False(TrajectoryTextSidecarCodec.ShouldWriteSectionAuthoritativeTrajectory(rec));
        }

        // Guards the OTHER half of the substitution contract, the one the skip must not
        // swallow: a Relative section that HAS frames but no bodyFixedFrames is a
        // genuinely incomplete substitution and still refuses.
        [Fact]
        public void RelativeSectionWithoutBodyFixedFrames_StillRefusesSubstitution()
        {
            Recording rec = UndockChildRecording();
            TrackSection relative = rec.TrackSections[1];
            relative.bodyFixedFrames = new List<TrajectoryPoint>();
            rec.TrackSections[1] = relative;

            Assert.False(
                TrajectoryTextSidecarCodec.TryBuildBodyFixedPrimaryFlatPointsForRelativeSections(
                    rec, out _),
                "a Relative section with frames but no body-fixed twin cannot be substituted");
        }

        // ---------- Site 1: the pure close-section decision ----------

        // Guards the discard the producer fix rests on: no frames, no body-fixed frames,
        // no checkpoints -> dropped, whatever the reference frame. 0.02 s is the measured
        // undock shell; the BG recorder had no zero-payload discard at all before this
        // fix, so the Absolute 0.02 s row is the defect's own shape.
        [Theory]
        [InlineData(ReferenceFrame.Absolute, 0.02)]
        [InlineData(ReferenceFrame.Absolute, 0.04)]
        [InlineData(ReferenceFrame.Relative, 0.02)]
        [InlineData(ReferenceFrame.OrbitalCheckpoint, 0.9)]
        public void Classify_PayloadFreeSection_Discarded(ReferenceFrame frame, double duration)
        {
            Assert.Equal(
                TrackSectionCloseDisposition.DiscardPayloadFree,
                TrackSectionCloseClassifier.Classify(
                    frameCount: 0,
                    bodyFixedFrameCount: 0,
                    checkpointCount: 0,
                    sectionDurationSeconds: duration,
                    referenceFrame: frame,
                    isBoundarySeam: false));
        }

        // Guards the span bound, and states what it costs. The threshold is the one
        // FlightRecorder's zero-frame discard has always used, kept so this fix does not
        // change how the flight recorder treats a long open-but-unsampled section (the
        // recorder test corpus closes such sections deliberately). A long payload-free
        // section therefore still reaches disk - two exist in the committed fixture
        // corpus, both in rover-relay-c-recorded - and is covered by the OTHER two
        // layers: the codec predicates skip it whatever its span, and INV11 reports it.
        [Fact]
        public void Classify_LongPayloadFreeSection_PersistsAndIsCoveredElsewhere()
        {
            Assert.Equal(
                TrackSectionCloseDisposition.Persist,
                TrackSectionCloseClassifier.Classify(
                    frameCount: 0,
                    bodyFixedFrameCount: 0,
                    checkpointCount: 0,
                    sectionDurationSeconds: 5.04,
                    referenceFrame: ReferenceFrame.Absolute,
                    isBoundarySeam: false));

            // The codec's coverage of the same shape, at any span: a recording whose
            // leading shell spans 5 s still gets both defences.
            Recording rec = UndockChildRecording();
            TrackSection shell = rec.TrackSections[0];
            shell.endUT = shell.startUT + 5.04;
            rec.TrackSections[0] = shell;

            Assert.True(TrajectoryTextSidecarCodec.ShouldWriteSectionAuthoritativeTrajectory(rec));
            Assert.True(
                TrajectoryTextSidecarCodec.TryBuildBodyFixedPrimaryFlatPointsForRelativeSections(
                    rec, out _));

            // And the analyzer names it.
            Assert.Single(new Inv11EmptyTrackSection().Evaluate(ModelWith(rec)));
        }

        // Guards the shape the discard must NOT eat: the single-frame, zero-duration
        // Absolute finalization FinalizeAllForCommit emits when only one sample existed.
        [Fact]
        public void Classify_SingleFrameAbsoluteFinalization_Persists()
        {
            Assert.Equal(
                TrackSectionCloseDisposition.Persist,
                TrackSectionCloseClassifier.Classify(
                    frameCount: 1,
                    bodyFixedFrameCount: 0,
                    checkpointCount: 0,
                    sectionDurationSeconds: 0.0,
                    referenceFrame: ReferenceFrame.Absolute,
                    isBoundarySeam: false));
        }

        // Guards the pre-existing discard, unchanged by the widening: a seed-only
        // Relative transient closed inside one physics frame.
        [Fact]
        public void Classify_SeedOnlyRelativeTransient_Discarded()
        {
            Assert.Equal(
                TrackSectionCloseDisposition.DiscardSeedOnlyRelativeTransient,
                TrackSectionCloseClassifier.Classify(
                    frameCount: 1,
                    bodyFixedFrameCount: 0,
                    checkpointCount: 0,
                    sectionDurationSeconds: 0.02,
                    referenceFrame: ReferenceFrame.Relative,
                    isBoundarySeam: false));
        }

        // Guards the optimizer's split-suppression contract: an isBoundarySeam section is
        // recorder bookkeeping and persists even when it carries no payload at all.
        [Fact]
        public void Classify_BoundarySeam_PersistsEvenWhenPayloadFree()
        {
            Assert.Equal(
                TrackSectionCloseDisposition.Persist,
                TrackSectionCloseClassifier.Classify(
                    frameCount: 0,
                    bodyFixedFrameCount: 0,
                    checkpointCount: 0,
                    sectionDurationSeconds: 0.0,
                    referenceFrame: ReferenceFrame.Absolute,
                    isBoundarySeam: true));
        }

        // Guards the parent-anchored contract against the widening: a Relative section
        // whose ONLY authored surface is bodyFixedFrames is renderable coverage, not an
        // empty shell (BODYFIXEDFRAMES-INVISIBLE-TO-BOTH-EMPTINESS-PREDICATES).
        [Fact]
        public void Classify_BodyFixedOnlyRelativeSection_Persists()
        {
            Assert.Equal(
                TrackSectionCloseDisposition.Persist,
                TrackSectionCloseClassifier.Classify(
                    frameCount: 0,
                    bodyFixedFrameCount: 2,
                    checkpointCount: 0,
                    sectionDurationSeconds: 4.0,
                    referenceFrame: ReferenceFrame.Relative,
                    isBoundarySeam: false));
        }

        // Guards that a checkpoint-only OrbitalCheckpoint section (no frames) persists:
        // checkpoints are its authored surface.
        [Fact]
        public void Classify_CheckpointOnlySection_Persists()
        {
            Assert.Equal(
                TrackSectionCloseDisposition.Persist,
                TrackSectionCloseClassifier.Classify(
                    frameCount: 0,
                    bodyFixedFrameCount: 0,
                    checkpointCount: 1,
                    sectionDurationSeconds: 600.0,
                    referenceFrame: ReferenceFrame.OrbitalCheckpoint,
                    isBoundarySeam: false));
        }

        // ---------- Site 3: the finalization backfill routing ----------

        // Guards the routing that turns the shape into a number. A recording WITH
        // sections must take the Absolute-only walk (the flat list is frame-blind); a
        // sections-less recording keeps the flat path, because the Absolute-only walk
        // leaves MaxDistanceFromLaunch untouched when it finds no Absolute section and
        // such a recording would otherwise stay at 0 and be discarded as idle-on-pad.
        [Fact]
        public void BackfillRoute_SectionsPresent_TakesAbsoluteOnly()
        {
            Recording rec = UndockChildRecording();

            Assert.Equal(
                VesselSpawner.MaxDistanceBackfillRoute.AbsoluteSections,
                VesselSpawner.ClassifyMaxDistanceBackfillRoute(rec));
        }

        [Fact]
        public void BackfillRoute_NoSections_FallsBackToFlatPoints()
        {
            var rec = new Recording { RecordingId = "legacy-flat" };
            rec.Points.Add(BodyFixedPoint(10.0));
            rec.Points.Add(BodyFixedPoint(20.0));

            Assert.Equal(
                VesselSpawner.MaxDistanceBackfillRoute.FlatPoints,
                VesselSpawner.ClassifyMaxDistanceBackfillRoute(rec));
        }

        [Fact]
        public void BackfillRoute_AlreadyComputedOrEmpty_IsNone()
        {
            Recording computed = UndockChildRecording();
            computed.MaxDistanceFromLaunch = 781.0;
            Assert.Equal(
                VesselSpawner.MaxDistanceBackfillRoute.None,
                VesselSpawner.ClassifyMaxDistanceBackfillRoute(computed));

            var onePoint = new Recording { RecordingId = "one-point" };
            onePoint.Points.Add(BodyFixedPoint(10.0));
            Assert.Equal(
                VesselSpawner.MaxDistanceBackfillRoute.None,
                VesselSpawner.ClassifyMaxDistanceBackfillRoute(onePoint));

            Assert.Equal(
                VesselSpawner.MaxDistanceBackfillRoute.None,
                VesselSpawner.ClassifyMaxDistanceBackfillRoute(null));
        }

        // ---------- Site 4: the analyzer rule ----------

        private static AnalyzerModel ModelWith(params Recording[] recordings)
        {
            return new AnalyzerModel
            {
                SaveName = "undock-bg-child",
                Recordings = new List<Recording>(recordings),
                Trees = new List<RecordingTree>(),
                Tombstones = new List<LedgerTombstone>(),
                SupersedeRelations = new List<RecordingSupersedeRelation>(),
                Ledger = new List<GameAction>(),
                LoadFaults = new List<LoadFault>(),
            };
        }

        // Positive: the damaged shape produces exactly one INV11 finding, naming the
        // empty section's index, at WARN (see the rule header for why the severity is a
        // corpus decision: two committed harness fixtures ship these bytes and the
        // harness verifier runs in BaselineMode.Forbid).
        [Fact]
        public void Inv11_EmptySection_WarnsNamingTheSection()
        {
            var rule = new Inv11EmptyTrackSection();

            List<Finding> findings = rule.Evaluate(ModelWith(UndockChildRecording())).ToList();

            Finding f = Assert.Single(findings);
            Assert.Equal("INV11-EMPTY-SECTION", f.RuleId);
            Assert.Equal(VerdictLevel.Warn, f.Level);
            Assert.Equal(0, f.SectionIndex);
            Assert.Equal("49eaec92876041efa53deb1f5e5c96f4", f.Target);
            Assert.Contains("empty-section", f.Message);
        }

        // Negative: the SAME recording with the empty shell removed - what the fixed
        // recorders now produce - yields nothing, and neither does a bodyFixedFrames-only
        // Relative section or a checkpoint-only section.
        [Fact]
        public void Inv11_HealthyRecordings_NoFindings()
        {
            Recording fixedChild = UndockChildRecording();
            fixedChild.TrackSections.RemoveAt(0);

            var bodyFixedOnly = new Recording { RecordingId = "body-fixed-only" };
            bodyFixedOnly.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 104.0,
                frames = new List<TrajectoryPoint>(),
                bodyFixedFrames = new List<TrajectoryPoint>
                {
                    BodyFixedPoint(100.0), BodyFixedPoint(104.0),
                },
            });

            var checkpointOnly = new Recording { RecordingId = "checkpoint-only" };
            checkpointOnly.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 200.0,
                endUT = 800.0,
                checkpoints = new List<OrbitSegment> { new OrbitSegment { startUT = 200.0, endUT = 800.0 } },
            });

            var rule = new Inv11EmptyTrackSection();

            Assert.Empty(rule.Evaluate(ModelWith(fixedChild, bodyFixedOnly, checkpointOnly)));
        }
    }
}
