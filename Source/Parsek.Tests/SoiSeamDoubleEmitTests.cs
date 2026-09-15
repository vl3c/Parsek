using System.Collections.Generic;
using System.Linq;
using Parsek.Analyzer;
using Parsek.Analyzer.Rules;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Producer-side reproduction for RECORDER-SUSPECTED-DOUBLE-EMIT-AT-SOI-SEAM
    /// (docs/dev/todo-and-known-bugs.md): at an ON-RAILS SOI crossing the active-vessel
    /// recorder opened an Absolute TrackSection that can never receive a frame, so the
    /// section persisted frame-less next to the OrbitalCheckpoint section promoted from
    /// the same span's orbit segment - byte-equal [startUT,endUT], INV2 double cover.
    /// </summary>
    [Collection("Sequential")]
    public class SoiSeamDoubleEmitTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly FlightRecorder recorder;

        public SoiSeamDoubleEmitTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ResetForTesting();
            recorder = new FlightRecorder();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        // Fixture spans lifted from the measured shape on duna-one-recorded's transfer
        // recording 61e9177193444e329247d0e8288cf91e (sections 34/35, the Kerbin -> Sun
        // seam), rounded to keep the arithmetic readable.
        private const double GoOnRailsUT = 63000000.0;
        private const double SoiCrossUT = 64044032.725027621;
        private const double GoOffRailsUT = 65004886.739419721;

        private static OrbitSegment Segment(double startUT, double endUT, string bodyName)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                inclination = 0.5,
                eccentricity = 0.3,
                semiMajorAxis = 1.4e10,
                longitudeOfAscendingNode = 12,
                argumentOfPeriapsis = 34,
                meanAnomalyAtEpoch = 0.25,
                epoch = startUT,
                bodyName = bodyName
            };
        }

        private static List<Finding> Inv2Overlaps(Recording rec)
        {
            var model = new AnalyzerModel
            {
                SaveName = "soi-seam-double-emit-tests",
                Recordings = new List<Recording> { rec }
            };
            return new Inv2NoDoubleCover()
                .Evaluate(model)
                .Where(f => f.RuleId == Inv2NoDoubleCover.OverlapRuleId)
                .ToList();
        }

        /// <summary>
        /// Replays the exact primitive sequence the recorder runs across one on-rails
        /// interplanetary leg: go-on-rails opens an OrbitalCheckpoint section
        /// (FlightRecorder.OnVesselGoOnRails), the SOI crossing closes it and opens the
        /// next one (FlightRecorder.OnVesselSOIChanged), go-off-rails closes that.
        /// </summary>
        private void DriveOnRailsSoiLeg()
        {
            // OnVesselGoOnRails: ABSOLUTE -> ORBITAL_CHECKPOINT.
            recorder.StartNewTrackSection(
                SegmentEnvironment.ExoBallistic,
                ReferenceFrame.OrbitalCheckpoint,
                GoOnRailsUT,
                TrackSectionSource.Checkpoint);

            // OnVesselSOIChanged: close the Kerbin segment into the open section, then
            // hand the section boundary to the recorder's SOI-seam transition.
            OrbitSegment kerbinSegment = Segment(GoOnRailsUT, SoiCrossUT, "Kerbin");
            recorder.OrbitSegments.Add(kerbinSegment);
            recorder.AddOrbitSegmentToCurrentTrackSection(kerbinSegment);
            recorder.TransitionTrackSectionAtSoiBoundary(
                SegmentEnvironment.ExoBallistic, SoiCrossUT, onRails: true);

            // ... the vessel stays PACKED across the whole Sun coast, so OnPhysicsFrame
            // early-returns on isOnRails and nothing is sampled into the open section.

            // OnVesselGoOffRails: close the Sun segment into the open section, close it.
            OrbitSegment sunSegment = Segment(SoiCrossUT, GoOffRailsUT, "Sun");
            recorder.OrbitSegments.Add(sunSegment);
            recorder.AddOrbitSegmentToCurrentTrackSection(sunSegment);
            recorder.CloseCurrentTrackSection(GoOffRailsUT);
        }

        private Recording BuildRecordingFromRecorder()
        {
            return new Recording
            {
                RecordingId = "soi-seam-leg",
                TrackSections = new List<TrackSection>(recorder.TrackSections),
                OrbitSegments = new List<OrbitSegment>(recorder.OrbitSegments)
            };
        }

        // The core producer assertion: an on-rails SOI crossing must not leave a
        // payload-less section behind. Pre-fix this failed - OnVesselSOIChanged opened
        // ReferenceFrame.Absolute while isOnRails was still true, and OnPhysicsFrame
        // early-returns on isOnRails, so that section closed with frames=0.
        [Fact]
        public void OnRailsSoiCrossing_EmitsNoFramelessSection()
        {
            DriveOnRailsSoiLeg();

            List<TrackSection> payloadless = recorder.TrackSections
                .Where(s => !OrbitSegmentCheckpointBridge.HasSectionPayload(s))
                .ToList();

            Assert.True(payloadless.Count == 0,
                "on-rails SOI crossing left payload-less section(s): "
                + string.Join(" | ", payloadless.Select(s =>
                    $"env={s.environment} ref={s.referenceFrame} src={s.source} "
                    + $"[{s.startUT},{s.endUT}]")));
        }

        // The post-SOI section is the on-rails leg's real payload holder: the segment
        // created at the crossing must land in ITS checkpoints list, not only in the
        // flat OrbitSegments cache (AddOrbitSegmentToCurrentTrackSection refuses any
        // section whose referenceFrame is not OrbitalCheckpoint).
        [Fact]
        public void OnRailsSoiCrossing_PostSeamSectionCarriesTheNewSoiSegment()
        {
            DriveOnRailsSoiLeg();

            Assert.Equal(2, recorder.TrackSections.Count);
            TrackSection postSeam = recorder.TrackSections[1];
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, postSeam.referenceFrame);
            Assert.Equal(TrackSectionSource.Checkpoint, postSeam.source);
            Assert.Equal(SoiCrossUT, postSeam.startUT);
            Assert.Equal(GoOffRailsUT, postSeam.endUT);
            Assert.Single(postSeam.checkpoints);
            Assert.Equal("Sun", postSeam.checkpoints[0].bodyName);
        }

        // End-to-end: the recorder's own output, put through the READ-path Ensure (the
        // seam that deliberately does NOT run the empty-shell reconcile), must be INV2
        // clean. This is the exact pass the offline analyzer ran over duna-one-recorded.
        [Fact]
        public void OnRailsSoiCrossing_ReadPathEnsure_IsInv2Clean()
        {
            DriveOnRailsSoiLeg();
            Recording rec = BuildRecordingFromRecorder();

            OrbitSegmentCheckpointBridge.EnsureCheckpointSectionsForTopLevelOrbitSegments(
                rec, markDirty: false, reconcileEmptySections: false);

            List<Finding> overlaps = Inv2Overlaps(rec);
            Assert.True(overlaps.Count == 0,
                "expected no INV2 overlap, got: "
                + string.Join(" | ", overlaps.Select(f => f.Message)));
        }

        // Negative control / legacy shape. Recordings written by the pre-fix producer
        // carry the frame-less Absolute shell on disk; the read path is byte-freeze by
        // contract, so loading one and analyzing it still FAILs INV2. That is the
        // measured duna-one-recorded shape and it stays reproducible - the producer fix
        // stops NEW recordings carrying it, it does not heal committed bytes.
        [Fact]
        public void LegacyFramelessShellBesideCheckpoint_StillFailsInv2OnTheReadPath()
        {
            OrbitSegment sunSegment = Segment(SoiCrossUT, GoOffRailsUT, "Sun");
            var rec = new Recording
            {
                RecordingId = "legacy-soi-shell",
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        environment = SegmentEnvironment.ExoBallistic,
                        referenceFrame = ReferenceFrame.Absolute,
                        source = TrackSectionSource.Active,
                        startUT = SoiCrossUT,
                        endUT = GoOffRailsUT,
                        frames = new List<TrajectoryPoint>(),
                        checkpoints = new List<OrbitSegment>()
                    },
                    OrbitSegmentCheckpointBridge.BuildClosedCheckpointSection(sunSegment)
                },
                OrbitSegments = new List<OrbitSegment> { sunSegment }
            };

            OrbitSegmentCheckpointBridge.EnsureCheckpointSectionsForTopLevelOrbitSegments(
                rec, markDirty: false, reconcileEmptySections: false);

            Assert.Single(Inv2Overlaps(rec));

            // ... and the WRITE path heals the same bytes, which is why a recording
            // rewritten by any sanctioned flow comes out clean.
            OrbitSegmentCheckpointBridge.EnsureCheckpointSectionsForTopLevelOrbitSegments(
                rec, markDirty: false, reconcileEmptySections: true);
            Assert.Empty(Inv2Overlaps(rec));
        }

        /// <summary>
        /// PRODUCER ATTRIBUTION for EMPTY-ABSOLUTE-SECTION-EXACT-SPAN-DUPLICATE-OF-ITS-CHECKPOINT,
        /// taken off the REAL operator bytes rather than asserted in prose: every payload-free
        /// section that stands in those three sidecars after the checkpoint-vs-checkpoint retire
        /// is an <c>Absolute</c> / <c>src=Active</c> shell whose span is byte-equal to an
        /// <c>OrbitalCheckpoint</c> section beside it, AND whose startUT is an ON-RAILS SOI BODY
        /// CHANGE - the section immediately before it is a checkpoint that ends exactly there and
        /// carries a conic of a DIFFERENT body. That is the signature of the pre-fix
        /// <see cref="FlightRecorder.TransitionTrackSectionAtSoiBoundary"/>, which opened Absolute
        /// unconditionally at a crossing the vessel was packed across; the cells above prove the
        /// shipped producer cannot emit it any more.
        ///
        /// <para>Also pinned: none of the shells is <c>isBoundarySeam</c> and every one carries
        /// <c>boundaryDiscontinuityMeters == 0</c>, so nothing the optimizer reads off a section
        /// boundary is different for their presence.</para>
        /// </summary>
        [Theory]
        [InlineData("041770246260406ab85b59495eb51f45")]
        [InlineData("36c7688b8e5141f7809e2d4dbe9dc094")]
        [InlineData("58130506e8f84025b78a95d2497534ab")]
        public void RealBytes_EveryStandingEmptyAbsoluteSitsOnAnOnRailsSoiBodyChange(string id)
        {
            Recording rec = CheckpointDoubleCoverRetireTests.LoadResidueFixture(id);
            CheckpointDoubleCoverRetire.TryRetireRedundantCheckpointSections(rec);

            int matched = 0;
            for (int i = 0; i < rec.TrackSections.Count; i++)
            {
                TrackSection shell = rec.TrackSections[i];
                if (OrbitSegmentCheckpointBridge.HasSectionPayload(shell)) continue;

                matched++;
                Assert.Equal(ReferenceFrame.Absolute, shell.referenceFrame);
                Assert.Equal(TrackSectionSource.Active, shell.source);
                Assert.False(shell.isBoundarySeam);
                Assert.Equal(0.0, shell.boundaryDiscontinuityMeters);

                // The checkpoint section that covers the identical span, and its conic's body.
                TrackSection cover = Assert.Single(rec.TrackSections.Where(s =>
                    s.referenceFrame == ReferenceFrame.OrbitalCheckpoint
                    && s.startUT == shell.startUT && s.endUT == shell.endUT));
                string bodyAfter = Assert.Single(cover.checkpoints).bodyName;

                // ... and the section that ENDS where the shell opens: the pre-crossing coast,
                // on the body the vessel just left.
                Assert.True(i > 0, "a shell cannot be the recording's first section");
                TrackSection before = rec.TrackSections[i - 1];
                Assert.Equal(ReferenceFrame.OrbitalCheckpoint, before.referenceFrame);
                Assert.Equal(shell.startUT, before.endUT);
                string bodyBefore = Assert.Single(before.checkpoints).bodyName;

                Assert.NotEqual(bodyBefore, bodyAfter);
            }

            Assert.True(matched > 0, "fixture " + id + " carries no standing payload-free section");
        }

        // Pure decision cell: the seam's reference frame follows the rails state.
        // Only the onRails==true row is SHIPPED behaviour - the sole production caller
        // (OnVesselSOIChanged) guards `if (!IsRecording || !isOnRails) return;`, so the
        // false row pins the resolver's defensive default, not a live code path.
        [Theory]
        [InlineData(true, ReferenceFrame.OrbitalCheckpoint, TrackSectionSource.Checkpoint)]
        [InlineData(false, ReferenceFrame.Absolute, TrackSectionSource.Active)]
        public void ResolveSoiBoundarySectionFrame_FollowsRailsState(
            bool onRails, ReferenceFrame expectedFrame, TrackSectionSource expectedSource)
        {
            ReferenceFrame frame;
            TrackSectionSource source;
            FlightRecorder.ResolveSoiBoundarySectionFrame(onRails, out frame, out source);

            Assert.Equal(expectedFrame, frame);
            Assert.Equal(expectedSource, source);
        }
    }
}
