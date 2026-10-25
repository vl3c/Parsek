using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling 2026-10-25 (todo PAD-VESSEL-GHOST-HOLDS-THE-PAD): a vessel whose whole flight
    /// stays inside a launch-site circle and ends parked there never shows a ghost; the EVA
    /// kerbal who left it keeps his.
    /// </summary>
    [Collection("Sequential")]
    public class LaunchSiteOnlyGhostTests : IDisposable
    {
        private const double KerbinRadius = 600000.0;
        private const double PadLat = SpawnCollisionDetector.KscPadLatitude;
        private const double PadLon = SpawnCollisionDetector.KscPadLongitude;
        private readonly List<string> logLines = new List<string>();

        public LaunchSiteOnlyGhostTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            LaunchSiteOnlyGhost.ResetForTesting();
            LaunchSiteOnlyGhost.RetirementForTesting = PadRetirement;
            LaunchSiteOnlyGhost.HomeRadiusForTesting = HomeRadius;
        }

        public void Dispose()
        {
            LaunchSiteOnlyGhost.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static double? HomeRadius(string body)
            => body == "Kerbin" ? (double?)KerbinRadius : null;

        // Stand-in for the 2026-09-23 retirement: a Landed / Splashed end on the pad circle.
        private static VesselSpawner.KscRetirementDecision PadRetirement(Recording rec)
        {
            var d = new VesselSpawner.KscRetirementDecision { Zone = KscExclusionZone.None };
            if (rec.TerminalStateValue != TerminalState.Landed
                && rec.TerminalStateValue != TerminalState.Splashed)
                return d;
            TrajectoryPoint last = rec.Points[rec.Points.Count - 1];
            if (last.bodyName != "Kerbin")
                return d;
            d.Zone = SpawnCollisionDetector.ClassifyKscExclusionZone(
                last.latitude, last.longitude, KerbinRadius,
                SpawnCollisionDetector.DefaultKscExclusionRadiusMeters);
            d.IsKsc = d.Zone != KscExclusionZone.None;
            d.SiteName = d.IsKsc ? "KSC" : null;
            return d;
        }

        private static double MetersToDegrees(double m) => m / (KerbinRadius * Math.PI / 180.0);

        private static Recording Rec(string id, uint pid, string eva, TerminalState? terminal,
            double startUT, double endUT, double eastMetersAtEnd = 0.0, string body = "Kerbin")
        {
            var rec = new Recording
            {
                RecordingId = id,
                TreeId = "tree-pad",
                VesselName = eva ?? "Pad Rocket",
                VesselPersistentId = pid,
                EvaCrewName = eva,
                TerminalStateValue = terminal,
            };
            rec.Points.Add(new TrajectoryPoint { ut = startUT, bodyName = body, latitude = PadLat, longitude = PadLon });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = endUT,
                bodyName = body,
                latitude = PadLat,
                longitude = PadLon + MetersToDegrees(eastMetersAtEnd),
            });
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                environment = SegmentEnvironment.SurfaceStationary,
                startUT = startUT,
                endUT = endUT,
            });
            return rec;
        }

        // Rocket on the pad (root, branched at the EVA), its continuation parked on the pad,
        // and the kerbal's EVA that walked 400 m away.
        private static RecordingTree PadEvaTree(Recording continuation = null)
        {
            var tree = new RecordingTree { Id = "tree-pad", TreeName = "Pad Rocket" };
            Recording root = Rec("rocket-root", 1111u, null, null, 100.0, 160.0);
            Recording cont = continuation ?? Rec("rocket-after-eva", 1111u, null, TerminalState.Landed, 160.0, 3700.0);
            Recording eva = Rec("bill-eva", 2222u, "Bill Kerman", TerminalState.Landed, 160.0, 3700.0, eastMetersAtEnd: 400.0);
            tree.Recordings[root.RecordingId] = root;
            tree.Recordings[cont.RecordingId] = cont;
            tree.Recordings[eva.RecordingId] = eva;
            return tree;
        }

        private static HashSet<string> Hidden(params RecordingTree[] trees)
            => LaunchSiteOnlyGhost.ComputeHiddenRecordingIds(
                trees, PadRetirement, HomeRadius, null, logChanges: true);

        [Fact]
        public void PadRocketTheKerbalLeft_HidesEveryRocketRecording_NotTheKerbal()
        {
            HashSet<string> hidden = Hidden(PadEvaTree());

            Assert.Contains("rocket-root", hidden);
            Assert.Contains("rocket-after-eva", hidden);
            Assert.DoesNotContain("bill-eva", hidden);
            Assert.Contains(logLines, l => l.Contains("[LaunchSiteGhost]")
                && l.Contains("Ghost hidden: vessel 'Pad Rocket' pid=1111 never left the launch site (KSC pad)")
                && l.Contains("recordings=2"));
            Assert.Contains(logLines, l => l.Contains("[LaunchSiteGhost]")
                && l.Contains("vesselsHidden=1 recordingsHidden=2"));
        }

        [Fact]
        public void KerbalStandingOnThePad_KeepsHisGhost()
        {
            RecordingTree tree = PadEvaTree();
            tree.Recordings["bill-eva"] = Rec("bill-eva", 2222u, "Bill Kerman", TerminalState.Landed, 160.0, 3700.0);

            Assert.DoesNotContain("bill-eva", Hidden(tree));
        }

        [Fact]
        public void RocketThatLaterLeavesThePad_KeepsEveryGhost()
        {
            Recording launched = Rec("rocket-after-eva", 1111u, null, TerminalState.Landed, 160.0, 3700.0,
                eastMetersAtEnd: 2000.0);

            HashSet<string> hidden = Hidden(PadEvaTree(launched));

            Assert.DoesNotContain("rocket-root", hidden);
            Assert.DoesNotContain("rocket-after-eva", hidden);
        }

        [Fact]
        public void HopThatLandsBackOnThePad_KeepsItsGhost()
        {
            Recording hop = Rec("rocket-after-eva", 1111u, null, TerminalState.Landed, 160.0, 3700.0);
            hop.Points.Insert(1, new TrajectoryPoint
            {
                ut = 400.0, bodyName = "Kerbin", latitude = PadLat, longitude = PadLon, altitude = 500.0,
            });

            Assert.Empty(Hidden(PadEvaTree(hop)));
        }

        [Fact]
        public void PadWobbleInsideTheGroundBand_StillHides()
        {
            Recording wobble = Rec("rocket-after-eva", 1111u, null, TerminalState.Landed, 160.0, 3700.0);
            wobble.Points.Insert(1, new TrajectoryPoint
            {
                ut = 400.0, bodyName = "Kerbin", latitude = PadLat, longitude = PadLon,
                altitude = LaunchSiteOnlyGhost.GroundBandMeters - 1.0,
            });

            Assert.Contains("rocket-after-eva", Hidden(PadEvaTree(wobble)));
        }

        [Theory]
        [InlineData(TerminalState.Destroyed)]
        [InlineData(TerminalState.Recovered)]
        [InlineData(TerminalState.Docked)]
        [InlineData(TerminalState.Boarded)]
        public void PadRocketThatDidNotEndParked_KeepsItsGhost(TerminalState terminal)
        {
            Recording cont = Rec("rocket-after-eva", 1111u, null, terminal, 160.0, 3700.0);

            Assert.Empty(Hidden(PadEvaTree(cont)));
        }

        [Fact]
        public void NoTerminalAnywhere_KeepsItsGhost()
        {
            Recording cont = Rec("rocket-after-eva", 1111u, null, null, 160.0, 3700.0);

            Assert.Empty(Hidden(PadEvaTree(cont)));
        }

        [Fact]
        public void RelativeSectionOrOrbitSegment_KeepsItsGhost()
        {
            Recording relative = Rec("rocket-after-eva", 1111u, null, TerminalState.Landed, 160.0, 3700.0);
            relative.TrackSections.Add(new TrackSection { referenceFrame = ReferenceFrame.Relative });
            Assert.Empty(Hidden(PadEvaTree(relative)));

            Recording orbit = Rec("rocket-after-eva", 1111u, null, TerminalState.Landed, 160.0, 3700.0);
            orbit.OrbitSegments.Add(new OrbitSegment());
            Assert.Empty(Hidden(PadEvaTree(orbit)));
        }

        [Fact]
        public void NotTheHomeWorld_KeepsItsGhost()
        {
            var tree = new RecordingTree { Id = "tree-mun", TreeName = "Mun lander" };
            Recording rec = Rec("mun-lander", 3333u, null, TerminalState.Landed, 100.0, 3700.0, body: "Mun");
            tree.Recordings[rec.RecordingId] = rec;

            Assert.Empty(Hidden(tree));
        }

        [Fact]
        public void GhostVisible_ReadsTheRuleThroughTheCommittedTrees_AndTheToggleStaysUntouched()
        {
            RecordingTree tree = PadEvaTree();
            RecordingStore.AddCommittedTreeForTesting(tree);
            Recording root = tree.Recordings["rocket-root"];
            Recording eva = tree.Recordings["bill-eva"];

            Assert.False(root.GhostVisible);
            Assert.False(((IPlaybackTrajectory)root).PlaybackEnabled);
            Assert.True(root.PlaybackEnabled);
            Assert.True(eva.GhostVisible);
            Assert.False(ParsekKSC.ShouldShowInKSC(
                root, new List<RecordingSupersedeRelation>(), new List<RecordingRewindRetirement>()));
        }

        [Fact]
        public void HiddenRocket_StillCompletesPastEnd_SoRetirementAndCrewRun()
        {
            RecordingTree tree = PadEvaTree();
            RecordingStore.AddCommittedTreeForTesting(tree);
            Recording cont = tree.Recordings["rocket-after-eva"];
            var flags = new TrajectoryPlaybackFlags { chainEndUT = cont.EndUT };

            Assert.True(GhostPlaybackLogic.ShouldFireHiddenPastEndCompletion(
                cont, flags, cont.EndUT + 1.0, completionAlreadyFired: false, earlyDebrisCompletion: false));
        }

        [Fact]
        public void UnchangedHiddenSet_LogsOnce()
        {
            RecordingTree tree = PadEvaTree();
            Hidden(tree);
            int first = logLines.FindAll(l => l.Contains("Ghost hidden:")).Count;
            Hidden(tree);

            Assert.Equal(1, first);
            Assert.Equal(1, logLines.FindAll(l => l.Contains("Ghost hidden:")).Count);
        }
    }
}
