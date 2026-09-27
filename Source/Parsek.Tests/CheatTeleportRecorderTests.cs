using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// KSP-SETTINGS-FOLLOWUPS-RECORDING-2026-09-27 item 1: stock Alt+F12 teleports (Set Orbit,
    /// Rendezvous, Set Position) end in FlightGlobals.PostOrbitSet, which
    /// Patches.CheatTeleportPatch postfixes into FlightRecorder.OnCheatTeleport. These cells
    /// drive the pure decision and the vessel-free section bookkeeping.
    /// </summary>
    [Collection("Sequential")]
    public class CheatTeleportRecorderTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly FlightRecorder recorder;

        public CheatTeleportRecorderTests()
        {
            RecordingStore.SuppressLogging = true;
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
            RecordingStore.ResetForTesting();
        }

        private static TrajectoryPoint Pt(string body)
        {
            return new TrajectoryPoint
            {
                latitude = 1.0,
                longitude = 2.0,
                altitude = 100.0,
                bodyName = body,
                rotation = UnityEngine.Quaternion.identity,
            };
        }

        private static OrbitSegment Seg(double startUT, string body)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = startUT,
                semiMajorAxis = 700000.0,
                eccentricity = 0.01,
                epoch = startUT,
                bodyName = body,
            };
        }

        private Recording BuildRecording()
        {
            return new Recording
            {
                RecordingId = "cheat-teleport",
                TrackSections = new List<TrackSection>(recorder.TrackSections),
                Points = new List<TrajectoryPoint>(recorder.Recording),
            };
        }

        // ---------------- pure decision ----------------

        [Theory]
        [InlineData(false, true, false, false, true, (int)CheatTeleportAction.NoLiveRecording)]
        [InlineData(true, false, true, true, true, (int)CheatTeleportAction.OtherVessel)]
        [InlineData(true, true, true, false, true, (int)CheatTeleportAction.OnRailsRecapture)]
        [InlineData(true, true, true, true, true, (int)CheatTeleportAction.CrossBodyHandledBySoi)]
        [InlineData(true, true, true, false, false, (int)CheatTeleportAction.OnRailsToAbsolute)]
        [InlineData(true, true, true, true, false, (int)CheatTeleportAction.OnRailsToAbsolute)]
        [InlineData(true, true, false, false, true, (int)CheatTeleportAction.OffRailsSameBody)]
        [InlineData(true, true, false, true, false, (int)CheatTeleportAction.OffRailsCrossBody)]
        public void Decide_CoversEveryShape(
            bool live, bool recorded, bool onRails, bool bodyChanged, bool coast,
            int expected)
        {
            Assert.Equal((CheatTeleportAction)expected,
                CheatTeleportDecision.Decide(live, recorded, onRails, bodyChanged, coast));
        }

        [Theory]
        [InlineData(true, 80000.0, 70000.0, 75000.0, true)]    // Kerbin orbit above the atmosphere
        [InlineData(true, 20000.0, 70000.0, -500000.0, false)] // inside Kerbin's atmosphere
        [InlineData(false, 30.0, 0.0, -190000.0, false)]       // Set Position onto the Mun surface
        [InlineData(false, 20000.0, 0.0, 15000.0, true)]       // Mun orbit
        [InlineData(false, double.NaN, 0.0, 15000.0, false)]
        public void IsPostTeleportOrbitACoast_FollowsTheRailsRules(
            bool atmo, double alt, double depth, double pe, bool expected)
        {
            Assert.Equal(expected, CheatTeleportDecision.IsPostTeleportOrbitACoast(atmo, alt, depth, pe));
        }

        [Fact]
        public void FormatLogLine_NamesVesselBodiesUtAndAction()
        {
            string line = CheatTeleportDecision.FormatLogLine(
                CheatTeleportAction.NoLiveRecording, "Flea", 42u, "Kerbin", "Mun", 1234.5);
            Assert.Contains("Cheat teleport detected", line);
            Assert.Contains("vessel='Flea' pid=42 from=Kerbin to=Mun ut=1234.50", line);
            Assert.Contains("action=NoLiveRecording", line);
        }

        // ---------------- no live recording: log only ----------------

        [Fact]
        public void NoLiveRecording_TouchesNoSection()
        {
            recorder.StartNewTrackSection(SegmentEnvironment.SurfaceStationary, ReferenceFrame.Absolute, 100.0);
            recorder.ApplyCheatTeleportBookkeeping(
                CheatTeleportAction.NoLiveRecording, 110.0, SegmentEnvironment.ExoBallistic,
                "Kerbin", "Kerbin", null);
            Assert.Empty(recorder.TrackSections);
            Assert.False(recorder.CheatTeleportSeamPendingForTesting);
            Assert.False(recorder.SoiChangePending);
            Assert.Equal(100.0, recorder.CurrentTrackSectionForTesting.startUT);
        }

        // ---------------- same body: seam section ----------------

        [Fact]
        public void SameBodyTeleportFromTheSurface_WritesAOneFrameSeamAtGoOffRails_AndNeverSplits()
        {
            recorder.StartNewTrackSection(SegmentEnvironment.SurfaceStationary, ReferenceFrame.Absolute, 100.0);
            recorder.AppendSectionStartSeamPointForTesting(Pt("Kerbin"), "pad");

            recorder.ApplyCheatTeleportBookkeeping(
                CheatTeleportAction.OffRailsSameBody, 110.0, SegmentEnvironment.SurfaceStationary,
                "Kerbin", "Kerbin", null);
            Assert.True(recorder.CheatTeleportSeamPendingForTesting);
            Assert.Empty(recorder.TrackSections);

            bool consumed = recorder.FinishPendingCheatTeleportAtOffRails(
                110.2, SegmentEnvironment.ExoBallistic,
                () => recorder.AppendSectionStartSeamPointForTesting(Pt("Kerbin"), "orbit"));
            Assert.True(consumed);
            Assert.False(recorder.CheatTeleportSeamPendingForTesting);

            Assert.Equal(2, recorder.TrackSections.Count);
            TrackSection seam = recorder.TrackSections[1];
            Assert.True(seam.isBoundarySeam);
            Assert.Single(seam.frames);
            Assert.Equal(110.2, seam.startUT);
            Assert.Equal(110.2, seam.endUT);
            Assert.False(recorder.CurrentTrackSectionForTesting.isBoundarySeam);

            recorder.CloseCurrentTrackSection(130.0);
            Recording rec = BuildRecording();
            // Surface -> Exo would split by rule 5; the seam suppresses both of its sides.
            RecordingOptimizer.SplitBoundaryReason reason;
            Assert.False(RecordingOptimizer.IsSplittableEnvOrBodyBoundary(rec, 1, out reason));
            Assert.Equal(RecordingOptimizer.SplitBoundaryReason.SuppressedBoundarySeam, reason);
            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("Cheat teleport: seam section written at go-off-rails UT=110.20")
                && l.Contains("seam=1"));
        }

        [Fact]
        public void SeamWithoutAFrame_IsNotPersisted()
        {
            recorder.StartNewTrackSection(SegmentEnvironment.SurfaceStationary, ReferenceFrame.Absolute, 100.0);
            recorder.AppendSectionStartSeamPointForTesting(Pt("Kerbin"), "pad");
            recorder.ApplyCheatTeleportBookkeeping(
                CheatTeleportAction.OffRailsSameBody, 110.0, SegmentEnvironment.SurfaceStationary,
                "Kerbin", "Kerbin", null);

            recorder.FinishPendingCheatTeleportAtOffRails(110.2, SegmentEnvironment.ExoBallistic, () => { });

            Assert.DoesNotContain(recorder.TrackSections, s => s.isBoundarySeam);
            Assert.Single(recorder.TrackSections);
        }

        [Fact]
        public void FinishPending_WithNothingArmed_IsANoOp()
        {
            int calls = 0;
            Assert.False(recorder.FinishPendingCheatTeleportAtOffRails(
                5.0, SegmentEnvironment.ExoBallistic, () => calls++));
            Assert.Equal(0, calls);
        }

        // ---------------- cross body: split by body ----------------

        [Fact]
        public void CrossBodyTeleportFromTheSurface_SplitsTheSectionByBody()
        {
            recorder.StartNewTrackSection(SegmentEnvironment.SurfaceStationary, ReferenceFrame.Absolute, 100.0);
            recorder.AppendSectionStartSeamPointForTesting(Pt("Kerbin"), "pad");

            recorder.ApplyCheatTeleportBookkeeping(
                CheatTeleportAction.OffRailsCrossBody, 110.0, SegmentEnvironment.SurfaceStationary,
                "Kerbin", "Mun", null);
            Assert.True(recorder.SoiChangePending);
            Assert.Equal("Kerbin", recorder.SoiChangeFromBody);
            Assert.Single(recorder.TrackSections);
            Assert.Equal(110.0, recorder.CurrentTrackSectionForTesting.startUT);

            Assert.True(recorder.FinishPendingCheatTeleportAtOffRails(
                110.2, SegmentEnvironment.SurfaceStationary,
                () => recorder.AppendSectionStartSeamPointForTesting(Pt("Mun"), "mun")));
            recorder.CloseCurrentTrackSection(130.0);

            Recording rec = BuildRecording();
            Assert.Equal(2, rec.TrackSections.Count);
            Assert.DoesNotContain(rec.TrackSections, s => s.isBoundarySeam);
            RecordingOptimizer.SplitBoundaryReason reason;
            Assert.True(RecordingOptimizer.IsSplittableEnvOrBodyBoundary(rec, 1, out reason));
            Assert.Equal(RecordingOptimizer.SplitBoundaryReason.BodyChange, reason);
            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("section split by body Kerbin -> Mun"));
        }

        // ---------------- on rails ----------------

        [Fact]
        public void OnRailsSameBody_ClosesTheOldSegmentAndRecaptures()
        {
            recorder.StartNewTrackSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint,
                100.0, TrackSectionSource.Checkpoint);
            recorder.SetOnRailsStateForTesting(true, Seg(100.0, "Kerbin"));

            recorder.ApplyCheatTeleportBookkeeping(
                CheatTeleportAction.OnRailsRecapture, 110.0, SegmentEnvironment.ExoBallistic,
                "Kerbin", "Kerbin", Seg(110.0, "Kerbin"));

            Assert.True(recorder.IsOnRailsForTesting);
            Assert.Single(recorder.OrbitSegments);
            Assert.Equal(110.0, recorder.OrbitSegments[0].endUT);
            Assert.Single(recorder.TrackSections);
            Assert.Single(recorder.TrackSections[0].checkpoints);
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, recorder.CurrentTrackSectionForTesting.referenceFrame);
            Assert.Equal(110.0, recorder.CurrentTrackSectionForTesting.startUT);
            Assert.False(recorder.CheatTeleportSeamPendingForTesting);
        }

        [Fact]
        public void OnRails_ZeroLengthPreTeleportSegment_IsDropped()
        {
            // PrepForOrbitSet's GoOnRails opened the segment in the same frame.
            recorder.StartNewTrackSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint,
                110.0, TrackSectionSource.Checkpoint);
            recorder.SetOnRailsStateForTesting(true, Seg(110.0, "Kerbin"));

            recorder.ApplyCheatTeleportBookkeeping(
                CheatTeleportAction.OnRailsRecapture, 110.0, SegmentEnvironment.ExoBallistic,
                "Kerbin", "Kerbin", Seg(110.0, "Kerbin"));

            Assert.Empty(recorder.OrbitSegments);
            Assert.Empty(recorder.TrackSections); // payload-free, zero duration: discarded
        }

        [Fact]
        public void OnRailsToANonCoast_LeavesRailsAndArmsTheSeam()
        {
            recorder.StartNewTrackSection(SegmentEnvironment.ExoBallistic, ReferenceFrame.OrbitalCheckpoint,
                100.0, TrackSectionSource.Checkpoint);
            recorder.SetOnRailsStateForTesting(true, Seg(100.0, "Kerbin"));

            recorder.ApplyCheatTeleportBookkeeping(
                CheatTeleportAction.OnRailsToAbsolute, 110.0, SegmentEnvironment.ExoBallistic,
                "Kerbin", "Kerbin", null);

            Assert.False(recorder.IsOnRailsForTesting);
            Assert.True(recorder.CheatTeleportSeamPendingForTesting);
            Assert.Equal(ReferenceFrame.Absolute, recorder.CurrentTrackSectionForTesting.referenceFrame);
            Assert.Single(recorder.OrbitSegments);
        }
    }
}
