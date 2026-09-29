using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// <see cref="CoexistingTreeSiblingSpawn"/>: a spawn overlap with a live vessel Parsek spawned
    /// from another member of the SAME committed tree, which stood beside the spawning member in
    /// the recording, is not a collision. Numbers are the 2026-09-28 'rover science' tree
    /// (logs/2026-09-28_2231_deployables): an EVA kerbal placed a Breaking Ground cluster whose
    /// members stand 2.9 m apart; before the fix only the RTG spawned and every later member was
    /// refused ("Spawn overlaps with Mini-NUK-PD ... at 2.9m", then "Trajectory walkback EXHAUSTED").
    /// </summary>
    [Collection("Sequential")]
    public class CoexistingTreeSiblingSpawnTests : IDisposable
    {
        private const string TreeId = "fcd57c9d73fb4e4dac0b61d22f472099";
        private const string OtherTreeId = "0badc0de0badc0de0badc0de0badc0de";
        private const double KerbinRadius = 600000.0;
        private const double RtgLat = 0.020840233700624471;
        private const double RtgLon = -74.719450212315294;
        private const uint RtgPid = 502661026u;
        private const string RtgGuid = "43829823b8e24bffaf0d24eddb42e51d";
        private const uint PanelPid = 3314560011u;
        private const string PanelGuid = "7a1d2c3e4f5061728394a5b6c7d8e9f0";

        // Metres -> degrees of latitude on Kerbin.
        private static readonly double MetersToDegLat = 180.0 / (Math.PI * KerbinRadius);

        private readonly List<string> logLines = new List<string>();

        public CoexistingTreeSiblingSpawnTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static Recording LandedMember(
            string id, string treeId, string name, uint pid, string guid,
            double startUT, double endUT, double lat, double lon, uint spawnedPid)
        {
            return new Recording
            {
                RecordingId = id,
                TreeId = treeId,
                VesselName = name,
                VesselPersistentId = pid,
                RecordedVesselGuid = guid,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                TerminalStateValue = TerminalState.Landed,
                SpawnedVesselPersistentId = spawnedPid,
                VesselSpawned = spawnedPid != 0,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = endUT, latitude = lat, longitude = lon, altitude = 64.34, bodyName = "Kerbin",
                    },
                },
            };
        }

        // The RTG (placed first, trimmed to 120.44-133.42) already spawned with its preserved identity.
        private static Recording Rtg(string treeId = TreeId, uint spawnedPid = RtgPid) =>
            LandedMember("05add69e18ff47499570103032eabe83", treeId,
                "Mini-NUK-PD Radioisotope Thermoelectric Generator", RtgPid, RtgGuid,
                120.44, 133.42, RtgLat, RtgLon, spawnedPid);

        // The first photovoltaic panel (134.86-147.84), 2.9 m north of the RTG.
        private static Recording Panel(uint spawnedPid = 0) =>
            LandedMember("f513300e8a0c486dad2b30e5c89328ca", TreeId,
                "OX-Stat-PD Photovoltaic Panel", PanelPid, PanelGuid,
                134.86, 147.84, RtgLat + 2.9 * MetersToDegLat, RtgLon, spawnedPid);

        private static TreeSiblingOverlapVerdict EvaluateAgainst(
            List<Recording> tree, Recording spawning, uint candidatePid, string candidateGuid,
            double candidateLat, double candidateLon, string body = "Kerbin")
        {
            return CoexistingTreeSiblingSpawn.Evaluate(
                tree, spawning, candidatePid, candidateGuid, body, candidateLat, candidateLon,
                KerbinRadius, out _, out _);
        }

        // -- The operator's cluster --------------------------------------------------

        [Fact]
        public void PanelOverlappingSpawnedRtgSibling_IsExempt()
        {
            Recording rtg = Rtg();
            Recording panel = Panel();
            var tree = new List<Recording> { rtg, panel };

            Assert.Equal(TreeSiblingOverlapVerdict.Exempt,
                EvaluateAgainst(tree, panel, RtgPid, RtgGuid, RtgLat, RtgLon));
        }

        [Fact]
        public void MirrorOrder_RtgOverlappingSpawnedPanelSibling_IsExempt()
        {
            // The later member spawned first (e.g. both past their end on a scene load):
            // the RTG ended at 133.42, before the panel was even placed at 134.86, yet the RTG
            // stays on the ground (Landed leaf), so the two co-existed.
            Recording rtg = Rtg(spawnedPid: 0);
            Recording panel = Panel(spawnedPid: 1904455877u);
            var tree = new List<Recording> { rtg, panel };

            double panelLat = RtgLat + 2.9 * MetersToDegLat;
            Assert.Equal(TreeSiblingOverlapVerdict.Exempt,
                EvaluateAgainst(tree, rtg, 1904455877u, PanelGuid, panelLat, RtgLon));
        }

        [Fact]
        public void PlayersOwnCraftParkedThere_StillBlocks()
        {
            var tree = new List<Recording> { Rtg(), Panel() };
            Assert.Equal(TreeSiblingOverlapVerdict.NotTreeSibling,
                EvaluateAgainst(tree, tree[1], 2905720181u, "11112222333344445555666677778888",
                    RtgLat, RtgLon));
        }

        [Fact]
        public void RelaunchReusingSiblingsBakedPid_StillBlocks()
        {
            // Same craft-baked pid as the RTG's preserved-identity spawn, different launch Guid.
            var tree = new List<Recording> { Rtg(), Panel() };
            Assert.Equal(TreeSiblingOverlapVerdict.NotTreeSibling,
                EvaluateAgainst(tree, tree[1], RtgPid, "99998888777766665555444433332222",
                    RtgLat, RtgLon));
        }

        [Fact]
        public void AnotherTreesSpawn_StillBlocks()
        {
            Recording foreignRtg = Rtg(treeId: OtherTreeId);
            Recording panel = Panel();
            // Even handed a list that contains it, Classify refuses a different tree.
            var mixed = new List<Recording> { foreignRtg, panel };
            Assert.Equal(TreeSiblingOverlapVerdict.NotTreeSibling,
                EvaluateAgainst(mixed, panel, RtgPid, RtgGuid, RtgLat, RtgLon));
        }

        [Fact]
        public void SiblingDrivenAwayFromItsRecordedSpot_StillBlocks()
        {
            var tree = new List<Recording> { Rtg(), Panel() };
            double movedLat = RtgLat + 20.0 * MetersToDegLat;
            Assert.Equal(TreeSiblingOverlapVerdict.Displaced,
                EvaluateAgainst(tree, tree[1], RtgPid, RtgGuid, movedLat, RtgLon));
        }

        [Fact]
        public void SiblingOnAnotherBody_StillBlocks()
        {
            var tree = new List<Recording> { Rtg(), Panel() };
            Assert.Equal(TreeSiblingOverlapVerdict.Displaced,
                EvaluateAgainst(tree, tree[1], RtgPid, RtgGuid, RtgLat, RtgLon, body: "Mun"));
        }

        [Theory]
        [InlineData(2.9, true)]
        [InlineData(3.0, true)]
        [InlineData(3.1, false)]
        public void DisplacementTolerance_IsHorizontalThreeMetres(double meters, bool expectExempt)
        {
            TreeSiblingOverlapVerdict expected = expectExempt
                ? TreeSiblingOverlapVerdict.Exempt
                : TreeSiblingOverlapVerdict.Displaced;
            Assert.Equal(expected, CoexistingTreeSiblingSpawn.Classify(
                Panel(), Rtg(), meters, CoexistingTreeSiblingSpawn.PlacementToleranceMeters));
        }

        [Fact]
        public void EndOfRecordingGate_IsDefensiveOnlyForSpawnableLeaves()
        {
            // At the end-of-recording position (no candidate UT) the time gate is recorded-interval
            // overlap. Every spawnable leaf persists past its end, so two of them always overlap:
            // the RTG ended (trimmed) before the panel was even placed and still passes.
            Assert.True(CoexistingTreeSiblingSpawn.RecordedIntervalsCoexist(Panel(), Rtg()));
            // The gate only bites on a recording that did not persist, which never has a live
            // spawn; it is kept so the predicate stays correct if that ever changes.
            Recording early = Rtg();
            early.TerminalStateValue = null;
            early.ExplicitEndUT = 130.0;
            Assert.Equal(TreeSiblingOverlapVerdict.NotCoexisting,
                CoexistingTreeSiblingSpawn.Classify(Panel(), early, 0.0,
                    CoexistingTreeSiblingSpawn.PlacementToleranceMeters));
        }

        // -- Walkback: the sibling must already stand at its spot at the candidate UT ---------

        private const uint RoverPid = 1234567u;
        private const string RoverGuid = "cb932f9b6a224c929c20adf1236ccc47";
        private const uint PartPid = 7654321u;
        private const string PartGuid = "abcdefabcdefabcdefabcdefabcdef01";

        private static TrajectoryPoint NorthPoint(double ut, double metersNorth) =>
            new TrajectoryPoint
            {
                ut = ut, latitude = RtgLat + metersNorth * MetersToDegLat, longitude = RtgLon,
                altitude = 64.34, bodyName = "Kerbin",
            };

        // A rover drives north through the point 20 m short of where it parks (at UT 150), parks
        // at UT 180 and sits there to UT 300.
        private static Recording DriveThroughRover() => new Recording
        {
            RecordingId = "rover", TreeId = TreeId, VesselName = "rover science",
            VesselPersistentId = RoverPid, RecordedVesselGuid = RoverGuid,
            TerminalStateValue = TerminalState.Landed,
            Points = new List<TrajectoryPoint>
            {
                NorthPoint(100, -100), NorthPoint(150, -20), NorthPoint(180, 0), NorthPoint(300, 0),
            },
        };

        // A part placed at that drive-through spot LATER (UT 200), already spawned there.
        private static Recording PartPlacedLater() => new Recording
        {
            RecordingId = "part", TreeId = TreeId, VesselName = "Grand Slam Passive Seismometer",
            VesselPersistentId = PartPid, RecordedVesselGuid = PartGuid,
            TerminalStateValue = TerminalState.Landed,
            SpawnedVesselPersistentId = 99887766u, VesselSpawned = true,
            Points = new List<TrajectoryPoint> { NorthPoint(200, -20), NorthPoint(213, -20) },
        };

        [Fact]
        public void WalkbackCandidateBeforeThePartWasPlaced_StillBlocks()
        {
            var tree = new List<Recording> { DriveThroughRover(), PartPlacedLater() };
            double partLat = RtgLat - 20.0 * MetersToDegLat;
            Assert.Equal(TreeSiblingOverlapVerdict.NotAtSpotAtCandidateUT,
                CoexistingTreeSiblingSpawn.Evaluate(tree, tree[0], 99887766u, PartGuid,
                    "Kerbin", partLat, RtgLon, KerbinRadius, out _, out _, candidateUT: 160.0));
        }

        [Fact]
        public void WalkbackCandidateAfterThePartWasPlaced_IsExempt()
        {
            var tree = new List<Recording> { DriveThroughRover(), PartPlacedLater() };
            double partLat = RtgLat - 20.0 * MetersToDegLat;
            Assert.Equal(TreeSiblingOverlapVerdict.Exempt,
                CoexistingTreeSiblingSpawn.Evaluate(tree, tree[0], 99887766u, PartGuid,
                    "Kerbin", partLat, RtgLon, KerbinRadius, out _, out _, candidateUT: 250.0));
        }

        [Fact]
        public void RoverWalkback_DoesNotStopInsideAPartPlacedThereLater()
        {
            // A foreign vessel blocks the rover's parking spot. Walking back, the first candidate
            // clear of it (about 12.9 m back, UT ~161) still overlaps the part's box; the part was
            // only placed at UT 200, so the rover never stood there beside it and the walkback
            // must go on past the part's box instead of spawning the rover inside it.
            var tree = new List<Recording> { DriveThroughRover(), PartPlacedLater() };
            Recording rover = tree[0];
            const double box = 12.6; // 2 x (1.25 m part half-extent + 5 m padding)
            double partNorth = -20.0;
            double partLat = RtgLat + partNorth * MetersToDegLat;

            var result = SpawnCollisionDetector.WalkbackAlongTrajectorySubdividedDetailed(
                rover.Points,
                KerbinRadius,
                SpawnCollisionDetector.DefaultWalkbackStepMeters,
                (lat, lon, alt) => new Vector3d((lat - RtgLat) / MetersToDegLat, 0, 0),
                (pos, candidateUT) =>
                {
                    double north = pos.x;
                    bool foreign = Math.Abs(north - 0.0) < box;
                    bool part = Math.Abs(north - partNorth) < box
                        && CoexistingTreeSiblingSpawn.Evaluate(tree, rover, 99887766u, PartGuid,
                                "Kerbin", partLat, RtgLon, KerbinRadius, out _, out _, candidateUT)
                            != TreeSiblingOverlapVerdict.Exempt;
                    return foreign || part;
                });

            Assert.True(result.found);
            double foundNorth = (result.point.latitude - RtgLat) / MetersToDegLat;
            Assert.True(foundNorth <= partNorth - box,
                "walkback stopped at " + foundNorth.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                + " m, inside the part's box");
        }

        [Fact]
        public void SiblingArrival_PlacedPartIsItsFirstPoint_ParkedVehicleIsItsParkingPoint()
        {
            double partLat = RtgLat - 20.0 * MetersToDegLat;
            Assert.Equal(200.0, CoexistingTreeSiblingSpawn.SiblingArrivalAtSpotUT(
                PartPlacedLater(), "Kerbin", partLat, RtgLon, KerbinRadius, 3.0));
            Assert.Equal(180.0, CoexistingTreeSiblingSpawn.SiblingArrivalAtSpotUT(
                DriveThroughRover(), "Kerbin", RtgLat, RtgLon, KerbinRadius, 3.0));
        }

        [Fact]
        public void SiblingArrival_StopsAtRelativeSectionsAndOffSpotEnds()
        {
            Recording part = PartPlacedLater();
            part.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative, startUT = 195.0, endUT = 205.0,
            });
            part.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute, startUT = 205.0, endUT = 213.0,
            });
            double partLat = RtgLat - 20.0 * MetersToDegLat;
            // The UT-200 point sits in a Relative section (anchor-local metres), so only 213 counts.
            Assert.Equal(213.0, CoexistingTreeSiblingSpawn.SiblingArrivalAtSpotUT(
                part, "Kerbin", partLat, RtgLon, KerbinRadius, 3.0));
            // Spot not where the trajectory ends: never arrived.
            Assert.Equal(double.PositiveInfinity, CoexistingTreeSiblingSpawn.SiblingArrivalAtSpotUT(
                PartPlacedLater(), "Kerbin", RtgLat, RtgLon, KerbinRadius, 3.0));
        }

        [Fact]
        public void OwnPreviousSpawn_IsNotASibling()
        {
            Recording rtg = Rtg();
            var tree = new List<Recording> { rtg, Panel() };
            Assert.Equal(TreeSiblingOverlapVerdict.NotTreeSibling,
                EvaluateAgainst(tree, rtg, RtgPid, RtgGuid, RtgLat, RtgLon));
        }

        [Fact]
        public void StandaloneSpawningRecording_IsNeverExempt()
        {
            Recording rtg = Rtg();
            Recording standalone = Panel();
            standalone.TreeId = null;
            Assert.Equal(TreeSiblingOverlapVerdict.NotTreeSibling,
                CoexistingTreeSiblingSpawn.Classify(standalone, rtg, 0.0,
                    CoexistingTreeSiblingSpawn.PlacementToleranceMeters));
        }

        [Fact]
        public void SnapshotPositionIsTheRecordedSpot()
        {
            // A non-EVA spawn places the vessel at the snapshot lat/lon, so displacement is
            // measured from there, not from the trajectory endpoint.
            Recording rtg = Rtg();
            rtg.VesselSnapshot = new ConfigNode("VESSEL");
            rtg.VesselSnapshot.AddValue("lat", (RtgLat + 10.0 * MetersToDegLat).ToString("R",
                System.Globalization.CultureInfo.InvariantCulture));
            rtg.VesselSnapshot.AddValue("lon", RtgLon.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            rtg.VesselSnapshot.AddValue("alt", "64.34");

            double atSnapshot = CoexistingTreeSiblingSpawn.SiblingDisplacementMeters(
                rtg, "Kerbin", RtgLat + 10.0 * MetersToDegLat, RtgLon, KerbinRadius);
            double atEndpoint = CoexistingTreeSiblingSpawn.SiblingDisplacementMeters(
                rtg, "Kerbin", RtgLat, RtgLon, KerbinRadius);
            Assert.True(atSnapshot < 0.01, "snapshot spot should read ~0 m, got " + atSnapshot);
            Assert.True(atEndpoint > 9.9 && atEndpoint < 10.1, "endpoint should read ~10 m, got " + atEndpoint);
        }

        [Theory]
        [InlineData(TerminalState.Landed, null, true)]
        [InlineData(TerminalState.Splashed, null, true)]
        [InlineData(TerminalState.Orbiting, null, true)]
        [InlineData(TerminalState.SubOrbital, null, true)]
        [InlineData(TerminalState.Destroyed, null, false)]
        [InlineData(TerminalState.Recovered, null, false)]
        [InlineData(TerminalState.Boarded, null, false)]
        [InlineData(TerminalState.Landed, "bp-child", false)]
        public void PersistsPastEnd_OnlyForIntactLeaves(
            TerminalState terminal, string childBranchPointId, bool expected)
        {
            var rec = new Recording { TerminalStateValue = terminal, ChildBranchPointId = childBranchPointId };
            Assert.Equal(expected, CoexistingTreeSiblingSpawn.PersistsPastEnd(rec));
        }

        // -- Live wrapper: committed-tree lookup + log ------------------------------

        private static void CommitTree(params Recording[] members)
        {
            var tree = new RecordingTree { Id = TreeId, TreeName = "rover science" };
            foreach (Recording r in members)
                tree.Recordings[r.RecordingId] = r;
            RecordingStore.AddCommittedTreeForTesting(tree);
        }

        [Fact]
        public void IsExemptBlocker_CommittedCluster_ExemptsAndLogsBothVesselsAndTree()
        {
            Recording rtg = Rtg();
            Recording panel = Panel();
            CommitTree(rtg, panel);

            bool exempt = CoexistingTreeSiblingSpawn.IsExemptBlocker(
                panel, RtgPid, RtgGuid, "Mini-NUK-PD Radioisotope Thermoelectric Generator",
                "Kerbin", RtgLat, RtgLon, KerbinRadius, "chain-tip");

            Assert.True(exempt);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][SpawnCollision]")
                && l.Contains("Overlap exempt: co-existing tree sibling spawn:")
                && l.Contains("site=chain-tip")
                && l.Contains("tree=" + TreeId)
                && l.Contains("spawning='OX-Stat-PD Photovoltaic Panel'")
                && l.Contains("rec=f513300e8a0c486dad2b30e5c89328ca")
                && l.Contains("blocker='Mini-NUK-PD Radioisotope Thermoelectric Generator'")
                && l.Contains("pid=502661026")
                && l.Contains("siblingRec=05add69e18ff47499570103032eabe83")
                && l.Contains("verdict=Exempt")
                && l.Contains("displacement=0.00m"));
        }

        [Fact]
        public void IsExemptBlocker_DisplacedSibling_BlocksAndLogsReason()
        {
            Recording rtg = Rtg();
            Recording panel = Panel();
            CommitTree(rtg, panel);

            bool exempt = CoexistingTreeSiblingSpawn.IsExemptBlocker(
                panel, RtgPid, RtgGuid, "Mini-NUK-PD Radioisotope Thermoelectric Generator",
                "Kerbin", RtgLat + 20.0 * MetersToDegLat, RtgLon, KerbinRadius, "end-of-recording");

            Assert.False(exempt);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][SpawnCollision]")
                && l.Contains("Overlap not exempt: tree sibling spawn:")
                && l.Contains("verdict=Displaced")
                && l.Contains("displacement=20.00m"));
        }

        [Fact]
        public void IsExemptBlocker_NonSibling_BlocksSilently()
        {
            CommitTree(Rtg(), Panel());
            Recording panel = RecordingStore.CommittedTrees[0].Recordings["f513300e8a0c486dad2b30e5c89328ca"];

            bool exempt = CoexistingTreeSiblingSpawn.IsExemptBlocker(
                panel, 2905720181u, "11112222333344445555666677778888", "Player Rover",
                "Kerbin", RtgLat, RtgLon, KerbinRadius, "chain-tip");

            Assert.False(exempt);
            Assert.DoesNotContain(logLines, l => l.Contains("tree sibling"));
        }

        [Fact]
        public void IsExemptBlocker_UncommittedTree_Blocks()
        {
            // Nothing committed: the spawning recording's tree cannot vouch for the blocker.
            Assert.False(CoexistingTreeSiblingSpawn.IsExemptBlocker(
                Panel(), RtgPid, RtgGuid, "Mini-NUK-PD Radioisotope Thermoelectric Generator",
                "Kerbin", RtgLat, RtgLon, KerbinRadius, "chain-tip"));
        }

        // -- A vessel already standing there when the recording was made (ruling 2026-09-29) --
        //
        // Numbers are EVA-6's green run 2026-09-28_2101_a2 (kerbin-splashdown-recorded): the
        // Kerbal X capsule landed in an EARLIER committed tree (c05c834c, three chain segments of
        // one launch, the adoption stamp on the HEAD segment, the Landed leaf 77dc5686 ending at UT
        // 1274.68). Jebediah left it, a recording was started on the kerbal (tree fd03bd24, the
        // kerbal's own recording, no EVA branch), he placed a seismometer, the tree was committed,
        // and after a rewind the Space Center spawned both 15 m from their spots ("De-overlap for #9
        // \"Jebediah Kerman\": nudged 15.0m", the only blocker the live capsule).

        private const string CapsuleTreeId = "c05c834cd2754892b4588e7ce9220c3f";
        private const string JebTreeId = "fd03bd24fd774f0bbd40d1b673490566";
        private const uint CapsulePid = 2708531065u;
        private const string CapsuleGuid = "5493223fe49b42b181998849a9a2aefa";
        private const double CapsuleLat = -0.12039427646756619;
        private const double CapsuleLon = 86.6977045051095;
        private const double LiveCapsuleLat = -0.12039477325647301;
        private const double LiveCapsuleLon = 86.697704434225471;
        private const string PlacedBpId = "456832faa84043269fb55605de5268cd";

        private static Recording CapsuleSegment(string id, double startUT, double endUT,
            double lat, double lon, double alt, TerminalState? terminal, uint spawnedPid)
        {
            return new Recording
            {
                RecordingId = id, TreeId = CapsuleTreeId, VesselName = "Kerbal X",
                VesselPersistentId = CapsulePid, RecordedVesselGuid = CapsuleGuid,
                ExplicitStartUT = startUT, ExplicitEndUT = endUT, TerminalStateValue = terminal,
                SpawnedVesselPersistentId = spawnedPid, VesselSpawned = spawnedPid != 0,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = endUT, latitude = lat, longitude = lon, altitude = alt, bodyName = "Kerbin" },
                },
            };
        }

        // Head segment: carries the adoption stamp, ends mid-flight 70 km up.
        private static Recording CapsuleHead() => CapsuleSegment(
            "28b6e543d67c4d1c9e4763b451c01df5", 34.54, 212.20, -0.05, 20.0, 70000.0, null, CapsulePid);

        private static Recording CapsuleMid() => CapsuleSegment(
            "b8bee12bd86544a5b82ca91add7c1e5f", 212.20, 853.72, -0.1, 60.0, 90000.0, null, 0u);

        private static Recording CapsuleLeaf(double endUT = 1274.6835614006252) => CapsuleSegment(
            "77dc568671cd442bb682c549ca50b7a6", 853.72, endUT, CapsuleLat, CapsuleLon, 152.48,
            TerminalState.Landed, 0u);

        private static Recording Jeb() => new Recording
        {
            RecordingId = "818d9a8690e64413a5aa64ce827bfdfd", TreeId = JebTreeId, VesselName = "Jebediah Kerman",
            VesselPersistentId = 715601038u, RecordedVesselGuid = "c0855845a7f44fe29802a1b0806861bb",
            ExplicitStartUT = 1279.5235614006208, ExplicitEndUT = 1280.7035614006197,
            TerminalStateValue = TerminalState.Landed, StartSituation = "EVA",
            Points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 1280.70, latitude = -0.120551, longitude = 86.697712, altitude = 152.19, bodyName = "Kerbin" },
            },
        };

        private static Recording Seismometer() => new Recording
        {
            RecordingId = "ad570ea403684d28be3fe769547cc094", TreeId = JebTreeId,
            VesselName = "Grand Slam Passive Seismometer",
            VesselPersistentId = 3696585714u, RecordedVesselGuid = "c56e235e71f74fa9a42e156bf58b30f7",
            ExplicitStartUT = 1280.46356140062, ExplicitEndUT = 1282.3635614006182,
            TerminalStateValue = TerminalState.Landed, ParentBranchPointId = PlacedBpId,
            Points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 1282.36, latitude = -0.120634, longitude = 86.697718, altitude = 151.89, bodyName = "Kerbin" },
            },
        };

        private static List<Recording> CapsuleTree() =>
            new List<Recording> { CapsuleHead(), CapsuleMid(), CapsuleLeaf() };

        private static RecordingTree JebTreeWith(params Recording[] members)
        {
            var tree = new RecordingTree { Id = JebTreeId, TreeName = "Jebediah Kerman" };
            tree.BranchPoints.Add(new BranchPoint { Id = PlacedBpId, UT = 1280.46, Type = BranchPointType.GroundPartPlaced });
            foreach (Recording r in members) tree.Recordings[r.RecordingId] = r;
            return tree;
        }

        // Capsule tree committed first (index 0), the kerbal's tree second (index 1).
        private static int CapsuleFirst(string treeId) =>
            treeId == CapsuleTreeId ? 0 : treeId == JebTreeId ? 1 : -1;

        private static TreeSiblingOverlapVerdict EvaluateCapsuleBlocker(
            Recording spawning, bool kerbalOrPart, List<Recording> capsuleTree,
            Func<string, int> order, uint pid = CapsulePid, string guid = CapsuleGuid,
            double lat = LiveCapsuleLat, double lon = LiveCapsuleLon, double candidateUT = double.NaN)
        {
            return CoexistingTreeSiblingSpawn.EvaluateBlocker(
                new List<Recording> { spawning }, capsuleTree, order, spawning, kerbalOrPart,
                pid, guid, "Kerbin", lat, lon, KerbinRadius,
                out _, out _, out _, candidateUT);
        }

        [Fact]
        public void Eva6_KerbalBesideTheCapsuleOfAnEarlierTree_IsExempt()
        {
            Recording jeb = Jeb();
            Assert.Equal(TreeSiblingOverlapVerdict.Exempt, CoexistingTreeSiblingSpawn.EvaluateBlocker(
                new List<Recording> { jeb, Seismometer() }, CapsuleTree(), CapsuleFirst, jeb, true,
                CapsulePid, CapsuleGuid, "Kerbin", LiveCapsuleLat, LiveCapsuleLon, KerbinRadius,
                out Recording blocker, out OverlapBlockerKind kind, out double displacement));
            // The adoption stamp sits on the head segment (ends 70 km up) and identifies the live
            // capsule; the rule measures the vessel's LATEST segment, the Landed leaf.
            Assert.Equal("77dc568671cd442bb682c549ca50b7a6", blocker.RecordingId);
            Assert.Equal(OverlapBlockerKind.Spawn, kind);
            Assert.True(displacement < 0.1, "live capsule should stand on its recorded spot, got " + displacement);
        }

        [Fact]
        public void EarlierTreesCapsuleWithNoSpawnStamp_IsIdentifiedAsTheOriginal()
        {
            var capsule = new List<Recording> { CapsuleSegment(
                "28b6e543d67c4d1c9e4763b451c01df5", 34.54, 212.20, -0.05, 20.0, 70000.0, null, 0u),
                CapsuleMid(), CapsuleLeaf() };
            Recording jeb = Jeb();
            Assert.Equal(TreeSiblingOverlapVerdict.Exempt, CoexistingTreeSiblingSpawn.EvaluateBlocker(
                new List<Recording> { jeb }, capsule, CapsuleFirst, jeb, true,
                CapsulePid, CapsuleGuid, "Kerbin", LiveCapsuleLat, LiveCapsuleLon, KerbinRadius,
                out _, out OverlapBlockerKind kind, out _));
            Assert.Equal(OverlapBlockerKind.Original, kind);
        }

        [Fact]
        public void Eva6_PlacedPartBesideTheCapsuleOfAnEarlierTree_IsExempt()
        {
            Assert.Equal(TreeSiblingOverlapVerdict.Exempt,
                EvaluateCapsuleBlocker(Seismometer(), true, CapsuleTree(), CapsuleFirst));
        }

        [Fact]
        public void CapsuleTreeCommittedAfterTheKerbalsTree_StillPushes()
        {
            // The capsule's history was recorded in a timeline flown after the kerbal's: it was not
            // there when the kerbal's recording was made.
            Assert.Equal(TreeSiblingOverlapVerdict.NotCommittedBefore,
                EvaluateCapsuleBlocker(Jeb(), true, CapsuleTree(),
                    id => id == CapsuleTreeId ? 1 : id == JebTreeId ? 0 : -1));
            Assert.Equal(TreeSiblingOverlapVerdict.NotCommittedBefore,
                EvaluateCapsuleBlocker(Jeb(), true, CapsuleTree(), id => -1));
        }

        [Fact]
        public void CapsuleThatArrivedAfterTheKerbalsRecordingEnded_StillPushes()
        {
            // The capsule's recorded history runs to UT 1290, past the kerbal's end (1280.70): at
            // the kerbal's end it was not yet standing there (or was only a replay).
            var capsule = new List<Recording> { CapsuleHead(), CapsuleMid(), CapsuleLeaf(endUT: 1290.0) };
            Assert.Equal(TreeSiblingOverlapVerdict.NotStandingByThen,
                EvaluateCapsuleBlocker(Jeb(), true, capsule, CapsuleFirst));
        }

        [Fact]
        public void CapsuleWhoseHistoryDoesNotEndStanding_StillPushes()
        {
            // Only the flying head and mid segments committed: no evidence it ever stood there.
            var capsule = new List<Recording> { CapsuleHead(), CapsuleMid() };
            Assert.Equal(TreeSiblingOverlapVerdict.NotStandingByThen,
                EvaluateCapsuleBlocker(Jeb(), true, capsule, CapsuleFirst));
        }

        [Fact]
        public void CapsuleMovedSince_StillPushes()
        {
            Assert.Equal(TreeSiblingOverlapVerdict.Displaced,
                EvaluateCapsuleBlocker(Jeb(), true, CapsuleTree(), CapsuleFirst,
                    lat: CapsuleLat + 20.0 * MetersToDegLat, lon: CapsuleLon));
        }

        [Fact]
        public void VehicleSpawningBesideAnEarlierTreesVessel_StillPushes()
        {
            // The ruling covers an EVA kerbal or a placed ground part; a vehicle keeps the
            // duplicate-delivery de-overlap.
            Assert.Equal(TreeSiblingOverlapVerdict.SpawningNotKerbalOrPlacedPart,
                EvaluateCapsuleBlocker(Jeb(), false, CapsuleTree(), CapsuleFirst));
        }

        [Fact]
        public void RelaunchOfTheSameCraft_OrUnknownGuid_IsNotTheRecordedCapsule()
        {
            // Same craft-baked pid, a different launch Guid: another launch, not the recorded one.
            Assert.Equal(TreeSiblingOverlapVerdict.NotTreeSibling,
                EvaluateCapsuleBlocker(Jeb(), true, CapsuleTree(), CapsuleFirst,
                    guid: "11112222333344445555666677778888"));
            // An unknown live Guid is not positive evidence in another tree (no pid-only fallback).
            Assert.Equal(TreeSiblingOverlapVerdict.NotTreeSibling,
                EvaluateCapsuleBlocker(Jeb(), true, CapsuleTree(), CapsuleFirst, guid: null));
        }

        [Fact]
        public void OtherTreeWalkback_CountsTheCapsuleOnlyOnceItsHistoryHadEnded()
        {
            Assert.Equal(TreeSiblingOverlapVerdict.NotStandingByThen,
                EvaluateCapsuleBlocker(Jeb(), true, CapsuleTree(), CapsuleFirst, candidateUT: 1270.0));
            Assert.Equal(TreeSiblingOverlapVerdict.Exempt,
                EvaluateCapsuleBlocker(Jeb(), true, CapsuleTree(), CapsuleFirst, candidateUT: 1279.9));
        }

        [Fact]
        public void SameTreeOriginalVessel_WithoutAnySpawnStamp_IsExempt()
        {
            // The capsule is the recorded vessel of a same-tree member, still live, never stamped
            // as a spawn: identified by a positive pid + launch Guid match.
            Recording capsule = CapsuleLeaf();
            capsule.TreeId = JebTreeId;
            Recording seis = Seismometer();
            Assert.Equal(TreeSiblingOverlapVerdict.Exempt, CoexistingTreeSiblingSpawn.EvaluateBlocker(
                new List<Recording> { capsule, seis }, null, null, seis, false,
                CapsulePid, CapsuleGuid, "Kerbin", LiveCapsuleLat, LiveCapsuleLon, KerbinRadius,
                out _, out OverlapBlockerKind kind, out _));
            Assert.Equal(OverlapBlockerKind.Original, kind);
        }

        [Fact]
        public void SameTreeAdoptionStampOnTheHeadSegment_MeasuresTheLeaf()
        {
            // Same shape as the capsule tree, all in the spawning member's tree: before, the
            // head segment's mid-flight end was the "spot" and the capsule read as Displaced.
            var members = CapsuleTree();
            foreach (Recording r in members) r.TreeId = JebTreeId;
            Recording seis = Seismometer();
            members.Add(seis);
            Assert.Equal(TreeSiblingOverlapVerdict.Exempt,
                CoexistingTreeSiblingSpawn.Evaluate(members, seis, CapsulePid, CapsuleGuid,
                    "Kerbin", LiveCapsuleLat, LiveCapsuleLon, KerbinRadius, out Recording sibling, out _));
            Assert.Equal("77dc568671cd442bb682c549ca50b7a6", sibling.RecordingId);
        }

        [Fact]
        public void TheSpawningVesselsOwnEarlierSegment_IsNeverAnExemptBlocker()
        {
            // The spawning member is the capsule's leaf; the live capsule is its own launch (a
            // duplicate of itself), whichever segment carries the stamp.
            var members = CapsuleTree();
            Recording leaf = members[2];
            Assert.Equal(TreeSiblingOverlapVerdict.NotTreeSibling,
                CoexistingTreeSiblingSpawn.Evaluate(members, leaf, CapsulePid, CapsuleGuid,
                    "Kerbin", LiveCapsuleLat, LiveCapsuleLon, KerbinRadius, out _, out _));
        }

        [Fact]
        public void KerbalOrPlacedPartMember_Scope()
        {
            RecordingTree tree = JebTreeWith(Jeb(), Seismometer());
            Assert.True(CoexistingTreeSiblingSpawn.IsKerbalOrPlacedPartMember(tree, Jeb()));
            Assert.True(CoexistingTreeSiblingSpawn.IsKerbalOrPlacedPartMember(tree, Seismometer()));
            Assert.True(CoexistingTreeSiblingSpawn.IsKerbalOrPlacedPartMember(
                tree, new Recording { EvaCrewName = "Bob Kerman" }));
            var snapshotEva = new Recording { VesselSnapshot = new ConfigNode("VESSEL") };
            snapshotEva.VesselSnapshot.AddValue("type", "EVA");
            Assert.True(CoexistingTreeSiblingSpawn.IsKerbalOrPlacedPartMember(tree, snapshotEva));
            Assert.False(CoexistingTreeSiblingSpawn.IsKerbalOrPlacedPartMember(tree, CapsuleLeaf()));
            Assert.False(CoexistingTreeSiblingSpawn.IsKerbalOrPlacedPartMember(tree, null));
        }

        private static void CommitCapsuleThenJeb(bool capsuleFirst)
        {
            var capsuleTree = new RecordingTree { Id = CapsuleTreeId, TreeName = "Kerbal X" };
            foreach (Recording r in CapsuleTree()) capsuleTree.Recordings[r.RecordingId] = r;
            RecordingTree jebTree = JebTreeWith(Jeb(), Seismometer());
            RecordingTree[] order = capsuleFirst
                ? new[] { capsuleTree, jebTree }
                : new[] { jebTree, capsuleTree };
            foreach (RecordingTree t in order)
            {
                RecordingStore.AddCommittedTreeForTesting(t);
                foreach (Recording r in t.Recordings.Values)
                {
                    r.MergeState = MergeState.Immutable;
                    RecordingStore.AddCommittedInternal(r);
                }
            }
            EffectiveState.ResetCachesForTesting();
        }

        [Fact]
        public void IsExemptBlocker_Eva6Capsule_ExemptsAtTheSpaceCenterAndLogs()
        {
            CommitCapsuleThenJeb(capsuleFirst: true);
            Recording seis = RecordingStore.CommittedTrees[1].Recordings["ad570ea403684d28be3fe769547cc094"];

            Assert.Equal(OverlapExemptionScope.OtherTree, CoexistingTreeSiblingSpawn.ResolveExemption(
                seis, CapsulePid, CapsuleGuid, "Kerbal X",
                "Kerbin", LiveCapsuleLat, LiveCapsuleLon, KerbinRadius, "landed-de-overlap"));
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][SpawnCollision]")
                && l.Contains("Overlap exempt: other-tree vessel standing there first:")
                && l.Contains("site=landed-de-overlap")
                && l.Contains("tree=" + JebTreeId)
                && l.Contains("rec=ad570ea403684d28be3fe769547cc094")
                && l.Contains("blocker='Kerbal X'")
                && l.Contains("pid=2708531065")
                && l.Contains("blockerKind=spawn")
                && l.Contains("blockerTree=" + CapsuleTreeId)
                && l.Contains("blockerRec=77dc568671cd442bb682c549ca50b7a6")
                && l.Contains("blockerEndUT=1274.68")
                && l.Contains("referenceUT=1282.36")
                && l.Contains("verdict=Exempt"));
        }

        [Fact]
        public void IsExemptBlocker_CapsuleCommittedLater_BlocksAndLogsReason()
        {
            CommitCapsuleThenJeb(capsuleFirst: false);
            Recording seis = RecordingStore.CommittedTrees[0].Recordings["ad570ea403684d28be3fe769547cc094"];

            Assert.False(CoexistingTreeSiblingSpawn.IsExemptBlocker(
                seis, CapsulePid, CapsuleGuid, "Kerbal X",
                "Kerbin", LiveCapsuleLat, LiveCapsuleLon, KerbinRadius, "end-of-recording"));
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][SpawnCollision]")
                && l.Contains("Overlap not exempt: other-tree vessel:")
                && l.Contains("verdict=NotCommittedBefore"));
        }

        // -- Wiring: every spawn-collision site hands over the spawning recording ------

        private static string ReadSource(string file)
        {
            string srcRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Parsek"));
            string raw = File.ReadAllText(Path.Combine(srcRoot, file)).Replace("\r\n", "\n");
            return SourceScanText.StripCSharpComments(raw);
        }

        private static List<string> CallArgumentLists(string code, string callee)
        {
            var result = new List<string>();
            string needle = callee + "(";
            int at = 0;
            while ((at = code.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
            {
                int open = at + needle.Length - 1;
                int depth = 0;
                int close = -1;
                for (int i = open; i < code.Length; i++)
                {
                    if (code[i] == '(') depth++;
                    else if (code[i] == ')' && --depth == 0) { close = i; break; }
                }
                Assert.True(close > open, "unbalanced call to " + callee);
                result.Add(code.Substring(open + 1, close - open - 1));
                at = close;
            }
            return result;
        }

        [Theory]
        [InlineData("VesselGhoster.cs", 4)]
        [InlineData("VesselSpawner.cs", 3)]
        public void EverySpawnOverlapCheck_PassesTheSpawningRecording(string file, int expectedSites)
        {
            List<string> calls = CallArgumentLists(
                ReadSource(file), "SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels");
            Assert.Equal(expectedSites, calls.Count);
            int walkbackSites = 0;
            foreach (string args in calls)
            {
                Assert.Contains("spawningRecording:", args);
                if (args.Contains("walkback"))
                {
                    walkbackSites++;
                    Assert.Contains("candidateUT: candidateUT", args);
                }
            }
            Assert.Equal(file == "VesselGhoster.cs" ? 2 : 1, walkbackSites);
        }

        [Fact]
        public void KscLandedDeOverlap_PassesTheSpawningRecording()
        {
            List<string> calls = CallArgumentLists(
                ReadSource("ParsekKSC.cs"), "VesselSpawner.GatherExistingLandedVesselPositions");
            Assert.Single(calls);
            string[] args = calls[0].Split(',');
            Assert.Equal(3, args.Length);
            Assert.Equal("rec", args[2].Trim());
        }
    }
}
