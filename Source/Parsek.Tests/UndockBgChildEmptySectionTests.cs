using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Parsek.Analyzer;
using Parsek.Analyzer.Rules;
using Xunit;
using Xunit.Abstractions;

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
        private readonly ITestOutputHelper output;

        public UndockBgChildEmptySectionTests(ITestOutputHelper output)
        {
            this.output = output;
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
        // sections must take the body-fixed section walk (the flat list is frame-blind);
        // a sections-less recording keeps the flat path, because the section walk leaves
        // MaxDistanceFromLaunch untouched when it finds no body-fixed sample and such a
        // recording would otherwise stay at 0 and be discarded as idle-on-pad.
        [Fact]
        public void BackfillRoute_SectionsPresent_TakesBodyFixedSections()
        {
            Recording rec = UndockChildRecording();

            Assert.Equal(
                VesselSpawner.MaxDistanceBackfillRoute.BodyFixedSections,
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

        // (c) The flat fallback fires ONLY where the flat list cannot be carrying
        // anchor-local metres. A recording whose only body-fixed surface is a Relative
        // section's bodyFixedFrames takes the section walk even though its flat list is
        // long enough to tempt the fallback - that list is exactly the poisoned one.
        [Fact]
        public void BackfillRoute_RelativeBodyFixedOnly_TakesBodyFixedSectionsNotFlat()
        {
            Recording rec = RelativeBodyFixedOnlyRecording();
            Assert.True(rec.Points.Count >= 2, "the poisoned flat list must be long enough to tempt the fallback");

            Assert.Equal(
                VesselSpawner.MaxDistanceBackfillRoute.BodyFixedSections,
                VesselSpawner.ClassifyMaxDistanceBackfillRoute(rec));
        }

        // A recording that has Relative sections but NO body-fixed sample anywhere stays
        // on the section walk and keeps maxDist = 0: fail CLOSED. Reading its flat list
        // would resolve anchor-local metres as lat/lon - the defect itself.
        [Fact]
        public void BackfillRoute_RelativeFramesOnly_StaysOnTheSectionWalk()
        {
            var rec = new Recording { RecordingId = "relative-frames-only" };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 110.0,
                frames = new List<TrajectoryPoint>
                {
                    AnchorLocalOffsetPoint(100.0), AnchorLocalOffsetPoint(110.0),
                },
            });
            rec.Points.AddRange(rec.TrackSections[0].frames);

            Assert.Equal(
                VesselSpawner.MaxDistanceBackfillRoute.BodyFixedSections,
                VesselSpawner.ClassifyMaxDistanceBackfillRoute(rec));
        }

        // The one sectioned shape that still takes the flat list: no body-fixed surface
        // AND no Relative section at all (an on-rails checkpoint-only recording), so the
        // flat list cannot have been poured full of anchor-local metres.
        [Fact]
        public void BackfillRoute_CheckpointOnlySectionsWithFlatPoints_TakesFlatPoints()
        {
            var rec = new Recording { RecordingId = "checkpoint-only-with-points" };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 200.0,
                endUT = 800.0,
                checkpoints = new List<OrbitSegment> { new OrbitSegment { startUT = 200.0, endUT = 800.0 } },
            });
            rec.Points.Add(BodyFixedPoint(200.0));
            rec.Points.Add(BodyFixedPoint(800.0));

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

        // ---------- Site 3: what the walk READS ----------

        // A deterministic stand-in for CelestialBody.GetWorldSurfacePosition: a flat plane
        // at 1000 m per degree. It is only a unit system - these cells assert WHICH samples
        // the walk read, not KSP geodesy, and the production walk resolves the same
        // TrajectoryPoints through FlightGlobals.
        private const double MetresPerDegree = 1000.0;

        private static Vector3d? FlatSurfaceResolver(TrajectoryPoint pt)
        {
            if (string.IsNullOrEmpty(pt.bodyName)) return null;
            return new Vector3d(
                pt.latitude * MetresPerDegree, pt.longitude * MetresPerDegree, pt.altitude);
        }

        /// <summary>
        /// A parent-anchored child whose ONLY body-fixed surface is the Relative section's
        /// bodyFixedFrames - the corpus shape the reviewer counted (14 debris plus the
        /// bdock-recorded dock partner). Its flat list carries the anchor-local metres,
        /// whose chord is deliberately far larger than the true one.
        /// </summary>
        private static Recording RelativeBodyFixedOnlyRecording()
        {
            var relative = new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 120.0,
                anchorRecordingId = "5f76d136e3dc4316bff71f4cfb0688a4",
                frames = new List<TrajectoryPoint>
                {
                    // Anchor-local metres in the lat/lon/alt fields: a 3.0-"degree" spread
                    // that the flat resolver would read as a 3000 m chord.
                    new TrajectoryPoint { ut = 100.0, latitude = 0.0, longitude = 0.0, altitude = 0.0, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 120.0, latitude = 3.0, longitude = 0.0, altitude = 0.0, bodyName = "Kerbin" },
                },
                bodyFixedFrames = new List<TrajectoryPoint>
                {
                    // The true body-fixed surface: a 0.5-"degree" = 500 m chord.
                    new TrajectoryPoint { ut = 100.0, latitude = 0.0, longitude = 0.0, altitude = 0.0, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 120.0, latitude = 0.5, longitude = 0.0, altitude = 0.0, bodyName = "Kerbin" },
                },
            };

            var rec = new Recording { RecordingId = "relative-body-fixed-only", VesselName = "undock partner" };
            rec.TrackSections.Add(relative);
            rec.Points.AddRange(relative.frames);
            return rec;
        }

        // (a) The should-fix this follow-up closes. Under the Absolute-only walk this
        // recording finalized with MaxDistanceFromLaunch untouched at 0, so IsIdleOnPad
        // read TRUE and HasPadLocalizedMotionOverride bailed under 30 m: a NEW fail-CLOSED
        // on live debris and undock partners, the mirror of the defect this PR fixes. The
        // walk must read the Relative section's bodyFixedFrames and land on the TRUE
        // body-fixed chord - not 0, and not the anchor-local chord.
        [Fact]
        public void Walk_RelativeBodyFixedOnly_MeasuresTheBodyFixedChord()
        {
            Recording rec = RelativeBodyFixedOnlyRecording();

            Assert.True(VesselSpawner.TryComputeMaxDistanceFromBodyFixedSurfaces(
                rec, FlatSurfaceResolver,
                out double maxDist,
                out VesselSpawner.MaxDistanceReferenceSurface referenceSurface,
                out int sampleCount,
                out int unresolved,
                out int sectionsWithoutSurface));

            Assert.Equal(500.0, maxDist, 6);            // the true body-fixed chord
            Assert.NotEqual(0.0, maxDist);              // not the old fail-closed 0
            Assert.NotEqual(3000.0, maxDist);           // not the anchor-local chord
            Assert.Equal(
                VesselSpawner.MaxDistanceReferenceSurface.RelativeBodyFixedFrames, referenceSurface);
            Assert.Equal(2, sampleCount);
            Assert.Equal(0, unresolved);
            Assert.Equal(0, sectionsWithoutSurface);
        }

        // (b) Mixed shapes read BOTH surfaces, and the launch reference is the EARLIEST
        // sample by UT - here a Relative bodyFixedFrames entry, because the recording opens
        // on its anchor window. Taking the first ABSOLUTE frame instead would measure the
        // chord from the middle of the trajectory: 300 m rather than 500 m.
        [Fact]
        public void Walk_MixedSurfaces_UsesBothAndTheEarliestSampleAsReference()
        {
            var rec = new Recording { RecordingId = "mixed-surfaces" };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 200.0,
                frames = new List<TrajectoryPoint>
                {
                    AnchorLocalOffsetPoint(100.0), AnchorLocalOffsetPoint(200.0),
                },
                bodyFixedFrames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, latitude = 0.0, longitude = 0.0, altitude = 0.0, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 200.0, latitude = 0.5, longitude = 0.0, altitude = 0.0, bodyName = "Kerbin" },
                },
            });
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 300.0,
                endUT = 300.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 300.0, latitude = 0.3, longitude = 0.0, altitude = 0.0, bodyName = "Kerbin" },
                },
            });

            List<VesselSpawner.BodyFixedSectionSample> samples =
                VesselSpawner.CollectBodyFixedSectionSamples(rec, out int sectionsWithoutSurface);
            Assert.Equal(3, samples.Count);             // 2 bodyFixedFrames + 1 Absolute frame
            Assert.Equal(0, sectionsWithoutSurface);
            Assert.Equal(0, VesselSpawner.ResolveLaunchReferenceIndex(samples));

            Assert.True(VesselSpawner.TryComputeMaxDistanceFromBodyFixedSurfaces(
                rec, FlatSurfaceResolver,
                out double maxDist,
                out VesselSpawner.MaxDistanceReferenceSurface referenceSurface,
                out int sampleCount,
                out int unresolved,
                out sectionsWithoutSurface));

            Assert.Equal(500.0, maxDist, 6);
            Assert.Equal(
                VesselSpawner.MaxDistanceReferenceSurface.RelativeBodyFixedFrames, referenceSurface);
            Assert.Equal(3, sampleCount);
            Assert.Equal(0, unresolved);
        }

        // A Relative section's own `frames` are NEVER a distance source: with no
        // bodyFixedFrames beside them the walk finds no reference and writes nothing, so
        // the caller's existing MaxDistanceFromLaunch survives.
        [Fact]
        public void Walk_RelativeFramesOnly_RefusesAndWritesNothing()
        {
            var rec = new Recording { RecordingId = "relative-frames-only" };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 110.0,
                frames = new List<TrajectoryPoint>
                {
                    AnchorLocalOffsetPoint(100.0), AnchorLocalOffsetPoint(110.0),
                },
            });

            Assert.False(VesselSpawner.TryComputeMaxDistanceFromBodyFixedSurfaces(
                rec, FlatSurfaceResolver,
                out double maxDist,
                out VesselSpawner.MaxDistanceReferenceSurface referenceSurface,
                out int sampleCount,
                out int unresolved,
                out int sectionsWithoutSurface));

            Assert.Equal(0.0, maxDist);
            Assert.Equal(VesselSpawner.MaxDistanceReferenceSurface.None, referenceSurface);
            Assert.Equal(0, sampleCount);
            Assert.Equal(1, sectionsWithoutSurface);
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

        // ---------- (d) the committed fixture corpus ----------

        /// <summary>
        /// Corpus ground truth for the routing, over every committed `.prec.txt` under
        /// `harness/fixtures/saves/`. Two claims, both measured rather than argued:
        ///
        /// <para>(1) NO recording that carries a TrackSection routes to
        /// <c>None</c> - i.e. finalization never silently leaves such a recording at
        /// maxDist = 0 for want of a route.</para>
        ///
        /// <para>(2) Every recording whose sections carry NO Absolute frames but DO carry
        /// Relative <c>bodyFixedFrames</c> resolves a launch reference. That population is
        /// the whole point of the follow-up: under the Absolute-only walk each of them
        /// finalized with maxDist untouched at 0, which reads as idle-on-pad and bails
        /// <c>HasPadLocalizedMotionOverride</c> under 30 m.</para>
        ///
        /// The measured counts are written to the test output so the PR can quote them.
        /// </summary>
        [Fact]
        public void BackfillRoute_CommittedFixtureCorpus_EverySectionedRecordingHasARoute()
        {
            string savesRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..",
                "harness", "fixtures", "saves"));
            Assert.True(Directory.Exists(savesRoot), "fixture saves root must exist at " + savesRoot);

            int sidecarsScanned = 0;
            int sectioned = 0;
            int sectionedNoAbsoluteFrames = 0;
            int sectionedNoAbsoluteButRelativeBodyFixed = 0;
            int routedBodyFixedSections = 0;
            int routedFlatPoints = 0;
            var routedNone = new List<string>();
            var noLaunchReference = new List<string>();
            var withoutAbsoluteFrames = new List<string>();

            foreach (string precTxt in Directory
                .GetFiles(savesRoot, "*.prec.txt", SearchOption.AllDirectories)
                .OrderBy(x => x, StringComparer.Ordinal))
            {
                ConfigNode node;
                try { node = ConfigNode.Load(precTxt); }
                catch { continue; }
                if (node == null) continue;

                string label = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(
                                   Path.GetDirectoryName(precTxt)))) + "/" + Path.GetFileName(precTxt);
                var rec = new Recording { RecordingId = label };
                try { TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(node, rec); }
                catch { continue; }
                sidecarsScanned++;

                if (rec.TrackSections == null || rec.TrackSections.Count == 0) continue;
                sectioned++;

                // The finalization precondition: maxDist has not been computed yet.
                rec.MaxDistanceFromLaunch = 0.0;
                VesselSpawner.MaxDistanceBackfillRoute route =
                    VesselSpawner.ClassifyMaxDistanceBackfillRoute(rec);
                if (route == VesselSpawner.MaxDistanceBackfillRoute.None)
                    routedNone.Add(label);
                else if (route == VesselSpawner.MaxDistanceBackfillRoute.FlatPoints)
                    routedFlatPoints++;
                else
                    routedBodyFixedSections++;

                bool hasAbsoluteFrames = rec.TrackSections.Any(s =>
                    s.referenceFrame == ReferenceFrame.Absolute && s.frames != null && s.frames.Count > 0);
                if (hasAbsoluteFrames) continue;

                sectionedNoAbsoluteFrames++;
                withoutAbsoluteFrames.Add(label);
                bool hasRelativeBodyFixed = rec.TrackSections.Any(s =>
                    s.referenceFrame == ReferenceFrame.Relative
                    && s.bodyFixedFrames != null && s.bodyFixedFrames.Count > 0);
                if (!hasRelativeBodyFixed) continue;

                sectionedNoAbsoluteButRelativeBodyFixed++;
                List<VesselSpawner.BodyFixedSectionSample> samples =
                    VesselSpawner.CollectBodyFixedSectionSamples(rec, out int _);
                if (VesselSpawner.ResolveLaunchReferenceIndex(samples) < 0)
                    noLaunchReference.Add(label);
            }

            output.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "fixture corpus: sidecars={0} sectioned={1} routed(body-fixed-sections)={2} " +
                "routed(flat-points)={3} routed(none)={4} sectioned-without-absolute-frames={5} " +
                "of which relative-body-fixed={6}",
                sidecarsScanned, sectioned, routedBodyFixedSections, routedFlatPoints,
                routedNone.Count, sectionedNoAbsoluteFrames,
                sectionedNoAbsoluteButRelativeBodyFixed));
            foreach (string label in withoutAbsoluteFrames)
                output.WriteLine("  without-absolute-frames: " + label);

            Assert.True(sidecarsScanned > 100, "the committed corpus must actually have loaded");
            Assert.True(sectioned > 0, "the corpus must carry sectioned recordings");
            Assert.True(sectionedNoAbsoluteButRelativeBodyFixed > 0,
                "the corpus must carry the population this follow-up is about: sectioned " +
                "recordings whose only body-fixed surface is Relative bodyFixedFrames");
            Assert.Empty(routedNone);
            Assert.Empty(noLaunchReference);
        }
    }
}
