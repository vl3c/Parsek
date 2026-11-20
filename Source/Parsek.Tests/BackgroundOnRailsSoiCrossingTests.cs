using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Xunit;
using static Parsek.Tests.BackgroundRecorderCharacterizationSupport;

namespace Parsek.Tests
{
    /// <summary>
    /// A BACKGROUND vessel that changes SOI while packed / on rails (test gap batch 3, lane
    /// SOI-3; SOI-2 covers only the loaded case). Drives the public
    /// <see cref="BackgroundRecorder.OnBackgroundVesselSOIChanged"/> end to end over a vessel
    /// stub whose orbit already names the NEW body (stock fires onVesselSOIChanged after the
    /// orbit driver re-bases the orbit), closes the new body's segment the way a commit does,
    /// and then reads the result through the pure playback seams the flight positioner and the
    /// map presence use. Pins the recording shape the code is meant to author:
    /// <list type="bullet">
    /// <item>one OrbitalCheckpoint section per body, the old body's ending and the new body's
    /// starting at the SOI event UT, each wrapping the closed OrbitSegment on its own body;</item>
    /// <item>the flat OrbitSegment cache in the same order, so <c>FindCrossBodySoiSeams</c>
    /// sees exactly one Mun -> Kerbin seam at that UT;</item>
    /// <item>playback (<c>FindOrbitSegment</c>) and the map line
    /// (<c>FindOrbitSegmentForMapDisplay</c>) both answer the old body before the seam and the
    /// new body after it, to the end of the recording.</item>
    /// </list>
    /// The live-body half (the resolved position actually sits outside the Mun's SOI after the
    /// seam) needs stock bodies and is the in-game category <c>BgSoiCrossingPlayback</c>.
    /// </summary>
    [Collection("Sequential")]
    public class BackgroundOnRailsSoiCrossingTests : IDisposable
    {
        private const uint Pid = 7310u;
        private const string RecId = "rec_bg_soi_probe";
        private const double SegmentStartUT = 420.0;
        private const double SoiUT = 1316.5;
        private const double CommitUT = 1500.0;

        private readonly List<string> logLines = new List<string>();
        private readonly CultureInfo savedCulture;
        private readonly Planetarium savedPlanetarium;

        public BackgroundOnRailsSoiCrossingTests()
        {
            savedCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ClockOverrideForTesting = () => 1000.0;
            savedPlanetarium = InstallPlanetarium(SoiUT);
        }

        public void Dispose()
        {
            Planetarium.fetch = savedPlanetarium;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            Thread.CurrentThread.CurrentCulture = savedCulture;
        }

        private static RecordingTree MakeTree()
        {
            var tree = new RecordingTree
            {
                Id = "tree_bg_soi",
                TreeName = "Kerbal X",
                RootRecordingId = "rec_pod_stack",
                ActiveRecordingId = "rec_pod_stack",
            };
            tree.Recordings["rec_pod_stack"] = new Recording
            {
                RecordingId = "rec_pod_stack",
                VesselName = "Kerbal X",
            };
            tree.Recordings[RecId] = new Recording
            {
                RecordingId = RecId,
                VesselName = "Kerbal X Probe",
                VesselPersistentId = Pid,
                ParentAnchorRecordingId = "rec_pod_stack",
            };
            tree.BackgroundMap[Pid] = RecId;
            return tree;
        }

        // The Mun escape hyperbola the vessel was packed on before the boundary.
        private static OrbitSegment MunEscapeSegment()
        {
            return new OrbitSegment
            {
                startUT = SegmentStartUT,
                inclination = 0.08,
                eccentricity = 1.2,
                semiMajorAxis = -2500000.0,
                longitudeOfAscendingNode = 142.0,
                argumentOfPeriapsis = 178.3,
                meanAnomalyAtEpoch = 0.42,
                epoch = SegmentStartUT,
                bodyName = "Mun",
            };
        }

        // A vessel stub whose orbit is already re-based on Kerbin (a bound Kerbin orbit near
        // the Mun's, which is what a slow Mun escape becomes).
        private static Vessel VesselOnKerbin()
        {
            Vessel v = Vessel(Pid, alive: true, body: Body("Kerbin", true, 70000.0), packed: true);
            Orbit o = v.orbitDriver.orbit;
            o.inclination = 0.6;
            o.eccentricity = 0.21;
            o.semiMajorAxis = 11200000.0;
            o.LAN = 140.5;
            o.argumentOfPeriapsis = 12.25;
            o.meanAnomalyAtEpoch = 2.75;
            o.epoch = SoiUT;
            return v;
        }

        private static BackgroundRecorder CrossOnRails(RecordingTree tree)
        {
            var bg = new BackgroundRecorder(tree);
            Assert.True(bg.HasOnRailsState(Pid));
            bg.InjectOpenOrbitSegmentForTesting(Pid, MunEscapeSegment());

            bg.OnBackgroundVesselSOIChanged(VesselOnKerbin(), Body("Mun", false, 0.0));
            return bg;
        }

        [Fact]
        public void OnRailsSoiChange_ClosesTheMunSegment_AndOpensAKerbinSegmentFromTheVesselOrbit()
        {
            RecordingTree tree = MakeTree();
            BackgroundRecorder bg = CrossOnRails(tree);

            // The state survives the crossing with the NEW body's segment open.
            Assert.True(bg.HasOnRailsState(Pid));
            Assert.True(bg.GetOnRailsHasOpenSegment(Pid));

            Recording rec = tree.Recordings[RecId];
            OrbitSegment closedMun = Assert.Single(rec.OrbitSegments);
            Assert.Equal("Mun", closedMun.bodyName);
            Assert.Equal(SegmentStartUT, closedMun.startUT);
            Assert.Equal(SoiUT, closedMun.endUT);
            TrackSection munSection = Assert.Single(rec.TrackSections);
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, munSection.referenceFrame);
            Assert.Equal(SegmentStartUT, munSection.startUT);
            Assert.Equal(SoiUT, munSection.endUT);
            Assert.Equal("Mun", Assert.Single(munSection.checkpoints).bodyName);
            Assert.Equal(SoiUT, rec.ExplicitEndUT);

            Assert.Contains(logLines, l => l.Contains("[BgRecorder]")
                && l.Contains("Orbit segment closed: pid=7310 UT=420.0-1316.5 body=Mun checkpointSection=added"));
            Assert.Contains(logLines, l => l.Contains("[BgRecorder]")
                && l.Contains("SOI change for background vessel: pid=7310 from=Mun to=Kerbin"));
            // The loaded route's line must not fire for an on-rails vessel.
            Assert.DoesNotContain(logLines, l => l.Contains("SOI changed off rails for background vessel"));
        }

        [Fact]
        public void OnRailsSoiChange_ThenCommit_AuthorsOneCheckpointSectionPerBody_TouchingAtTheSoiUT()
        {
            RecordingTree tree = MakeTree();
            BackgroundRecorder bg = CrossOnRails(tree);
            SetPlanetariumUT(CommitUT);

            bg.FinalizeAllForCommit(CommitUT);

            Recording rec = tree.Recordings[RecId];
            Assert.Equal(2, rec.TrackSections.Count);
            TrackSection mun = rec.TrackSections[0];
            TrackSection kerbin = rec.TrackSections[1];
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, mun.referenceFrame);
            Assert.Equal(ReferenceFrame.OrbitalCheckpoint, kerbin.referenceFrame);
            Assert.Equal("Mun", Assert.Single(mun.checkpoints).bodyName);
            OrbitSegment kerbinSeg = Assert.Single(kerbin.checkpoints);
            Assert.Equal("Kerbin", kerbinSeg.bodyName);
            // Contiguous at the event UT: no gap, no overlap, no section spanning two bodies.
            Assert.Equal(SoiUT, mun.endUT);
            Assert.Equal(SoiUT, kerbin.startUT);
            Assert.Equal(CommitUT, kerbin.endUT);
            // The Kerbin segment carries the vessel's re-based elements, not the Mun ones.
            Assert.Equal(11200000.0, kerbinSeg.semiMajorAxis);
            Assert.Equal(0.21, kerbinSeg.eccentricity);
            Assert.Equal(SoiUT, kerbinSeg.epoch);
            Assert.Equal(CommitUT, rec.ExplicitEndUT);

            // The flat cache playback reads mirrors the sections, in order.
            Assert.Equal(new[] { "Mun", "Kerbin" }, rec.OrbitSegments.Select(s => s.bodyName).ToArray());
            var seams = TrajectoryMath.FindCrossBodySoiSeams(rec.OrbitSegments);
            var seam = Assert.Single(seams);
            Assert.Equal(SoiUT, seam.seamUT);
            Assert.Equal("Mun", rec.OrbitSegments[seam.beforeIndex].bodyName);
            Assert.Equal("Kerbin", rec.OrbitSegments[seam.afterIndex].bodyName);
        }

        [Theory]
        [InlineData(SegmentStartUT, "Mun")]
        [InlineData(SoiUT - 30.0, "Mun")]
        [InlineData(SoiUT - 0.001, "Mun")]
        [InlineData(SoiUT, "Kerbin")]
        [InlineData(SoiUT + 30.0, "Kerbin")]
        [InlineData(CommitUT, "Kerbin")]
        public void Playback_AndTheMapLine_ReadTheBodyThatOwnsEachSideOfTheSeam(double ut, string body)
        {
            RecordingTree tree = MakeTree();
            BackgroundRecorder bg = CrossOnRails(tree);
            SetPlanetariumUT(CommitUT);
            bg.FinalizeAllForCommit(CommitUT);
            List<OrbitSegment> segments = tree.Recordings[RecId].OrbitSegments;

            OrbitSegment? playback = TrajectoryMath.FindOrbitSegment(segments, ut);
            Assert.True(playback.HasValue, "playback covering segment at ut=" + ut.ToString("R", CultureInfo.InvariantCulture));
            Assert.Equal(body, playback.Value.bodyName);

            OrbitSegment? map = TrajectoryMath.FindOrbitSegmentForMapDisplay(segments, ut);
            Assert.True(map.HasValue, "map display segment at ut=" + ut.ToString("R", CultureInfo.InvariantCulture));
            Assert.Equal(body, map.Value.bodyName);
        }

        [Fact]
        public void OnRailsSoiChange_ThenGoOffRails_ClosesTheKerbinSegmentAtTheUnpack()
        {
            // The other way the new body's segment ends: the probe unpacks near the active
            // vessel after the warp. The Kerbin section still starts at the SOI UT.
            RecordingTree tree = MakeTree();
            BackgroundRecorder bg = CrossOnRails(tree);

            Assert.True(bg.EndOnRailsStateForGoOffRails(Pid, 1400.0));

            Recording rec = tree.Recordings[RecId];
            Assert.Equal(new[] { "Mun", "Kerbin" }, rec.OrbitSegments.Select(s => s.bodyName).ToArray());
            Assert.Equal(SoiUT, rec.OrbitSegments[1].startUT);
            Assert.Equal(1400.0, rec.OrbitSegments[1].endUT);
            Assert.Equal(SoiUT, rec.TrackSections[1].startUT);
        }
    }
}
