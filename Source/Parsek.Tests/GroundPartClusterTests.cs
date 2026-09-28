using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.Analyzer;
using Parsek.Analyzer.Rules;
using Parsek.Tests.Generators;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A Breaking Ground CLUSTER: many ground parts placed by one EVA kerbal, each its own
    /// GroundPartPlaced tree member (docs/parsek-flight-recorder-design.md section 4.11).
    /// Modelled on the operator's manual run `logs/2026-09-28_2231_deployables` (tree
    /// 'rover science': Bob Kerman placed eleven parts, names, vessel names, placement UTs
    /// and pids below are that run's). GroundPartPlacementTests covers ONE member; these
    /// cells cover what only siblings can break: many branch points under one parent, a
    /// pick-up among standing siblings, a re-placement of the same part name, and the
    /// spawn / trim answers for every member.
    /// </summary>
    [Collection("Sequential")]
    public class GroundPartClusterTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        private const string KerbalRecId = "e44c90aff1f74414a0ffc4725f0a3990";
        private const uint KerbalPid = 3902114811u;

        // (part, vessel name, placement UT, vessel pid, part pid, member rec id)
        private static readonly (string part, string vessel, double ut, uint vesselPid, uint partPid, string rec)[] Cluster =
        {
            ("DeployedRTG", "Mini-NUK-PD Radioisotope Thermoelectric Generator", 120.42, 502661026u, 4263919010u, "05add69e18ff47499570103032eabe83"),
            ("DeployedSolarPanel", "OX-Stat-PD Photovoltaic Panel", 134.84, 3437711096u, 959827262u, "f513300e8a0c486dad2b30e5c89328ca"),
            ("DeployedSeismicSensor", "Grand Slam Passive Seismometer", 152.04, 792851381u, 1560613603u, "ec9f298275c44263b4e4d981b56b0408"),
            ("DeployedCentralStation", "Probodobodyne Experiment Control Station", 173.80, 1531547333u, 2215008812u, "adf043b8296d4d9cbc79543d0d0e9fff"),
            ("DeployedGoExOb", "Go-ob ED Monitor", 187.72, 1958737984u, 748115884u, "eba47c20afd146689259bec1d312773e"),
            ("DeployedSolarPanel", "OX-Stat-PD Photovoltaic Panel", 207.70, 1317523704u, 466038487u, "347cd53256f3401ab8825630325e1477"),
            ("DeployedIONExp", "Ionographer PD-22", 226.12, 3141556975u, 4113875752u, "fc29ffe86e15498db088fc249997ddf7"),
            ("DeployedSolarPanel", "OX-Stat-PD Photovoltaic Panel", 244.98, 922700075u, 745601762u, "447096b5de504e40a134998f2caa758c"),
            ("DeployedWeatherStn", "PD-3 Weather Analyzer", 279.54, 365152627u, 3726894919u, "f9983c069c5741b9a12c375b24200dda"),
            ("DeployedSatDish", "Communotron Ground HG-48", 294.46, 2040272308u, 363951271u, "c4f4499cab13457ea866091c55b2e665"),
            ("groundAnchor", "Stamp-O-Tron Ground Anchor", 307.58, 1242522514u, 542760782u, "0e8f03b0cd814c568f3604d57163b31c"),
        };

        // The recorded site, a couple of kilometres from the pad (the RTG's spawn line).
        private const double SiteLat = 0.0208;
        private const double SiteLon = -74.7195;
        private const double SiteAlt = 64.3;

        public GroundPartClusterTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        private static string BpId(int i) => "bpPlace" + i.ToString("D2");

        // ---- the generator-built eleven-member tree ----

        private static RecordingTree BuildGeneratedCluster(out List<RecordingBuilder> builders)
        {
            builders = new List<RecordingBuilder>();
            builders.Add(new RecordingBuilder("Bob Kerman")
                .WithRecordingId(KerbalRecId)
                .WithVesselPersistentId(KerbalPid)
                .WithVesselSnapshot(new VesselSnapshotBuilder()
                    .WithName("Bob Kerman").WithPersistentId(KerbalPid)
                    .AsLanded(SiteLat, SiteLon, SiteAlt).AddPart("kerbalEVA"))
                .WithTerminalState((int)TerminalState.Landed)
                .AddPoint(110, SiteLat, SiteLon, SiteAlt)
                .AddPoint(337.5, SiteLat, SiteLon, SiteAlt));
            var branchPoints = new List<BranchPoint>();
            for (int i = 0; i < Cluster.Length; i++)
            {
                var c = Cluster[i];
                builders.Add(new RecordingBuilder(c.vessel)
                    .WithRecordingId(c.rec)
                    .WithVesselPersistentId(c.vesselPid)
                    .WithVesselSnapshot(new VesselSnapshotBuilder()
                        .WithName(c.vessel).WithPersistentId(c.vesselPid)
                        .AsLanded(SiteLat + i * 1e-5, SiteLon, SiteAlt).AddPart(c.part))
                    .WithTerminalState((int)TerminalState.Landed)
                    .AddPoint(c.ut, SiteLat + i * 1e-5, SiteLon, SiteAlt)
                    .AddPoint(c.ut + 10, SiteLat + i * 1e-5, SiteLon, SiteAlt)
                    // VesselSnapshotBuilder gives a single-part vessel part pid 100000.
                    .AsPlacedGroundPart(100000u, c.part, placedUT: c.ut));
                branchPoints.Add(ScenarioWriter.GroundPartPlacedBranch(BpId(i), KerbalRecId, c.rec, c.ut));
            }
            return ScenarioWriter.MaterializeTree(builders, KerbalRecId, branchPoints);
        }

        [Fact]
        public void ElevenMemberCluster_SurvivesTheTreeCodec_EveryBranchPointAndMemberIntact()
        {
            RecordingTree built = BuildGeneratedCluster(out _);
            var node = new ConfigNode("RECORDING_TREE");
            built.Save(node);
            RecordingTree loaded = RecordingTree.Load(node);

            Assert.Equal(Cluster.Length + 1, loaded.Recordings.Count);
            Assert.Equal(Cluster.Length, loaded.BranchPoints.Count);
            Assert.All(loaded.BranchPoints, bp => Assert.Equal(BranchPointType.GroundPartPlaced, bp.Type));

            // One branch point per member, every one parented by the kerbal and naming
            // exactly its own member; no member is named twice.
            var childIds = new HashSet<string>();
            for (int i = 0; i < Cluster.Length; i++)
            {
                var c = Cluster[i];
                BranchPoint bp = loaded.BranchPoints.Single(b => b.Id == BpId(i));
                Assert.Equal(c.ut, bp.UT);
                Assert.Equal(new[] { KerbalRecId }, bp.ParentRecordingIds);
                string child = Assert.Single(bp.ChildRecordingIds);
                Assert.Equal(c.rec, child);
                Assert.True(childIds.Add(child));

                Recording member = loaded.Recordings[c.rec];
                Assert.Equal(bp.Id, member.ParentBranchPointId);
                Assert.Null(member.ChildBranchPointId);
                Assert.Equal(c.vesselPid, member.VesselPersistentId);
                Assert.Equal(c.vessel, member.VesselName);
                Assert.Equal(TerminalState.Landed, member.TerminalStateValue);
                Assert.False(member.IsDebris);
                Assert.True(GroundPartPlacement.IsPlacedPartMember(loaded, member));
            }

            // The kerbal kept recording across all eleven: no child pointer, still active,
            // and not itself a placed-part member.
            Recording kerbal = loaded.Recordings[KerbalRecId];
            Assert.Null(kerbal.ChildBranchPointId);
            Assert.Equal(KerbalRecId, loaded.ActiveRecordingId);
            Assert.False(GroundPartPlacement.IsPlacedPartMember(loaded, kerbal));
            // Three same-named solar panels stay three distinct members.
            Assert.Equal(3, loaded.Recordings.Values.Count(r => r.VesselName == "OX-Stat-PD Photovoltaic Panel"));
        }

        [Fact]
        public void ElevenMemberCluster_EveryMembersPlacedEventResolvesOnItsOwnSnapshot()
        {
            BuildGeneratedCluster(out List<RecordingBuilder> builders);
            var recordings = new List<Recording>();
            foreach (RecordingBuilder b in builders.Skip(1))
            {
                var rec = new Recording { RecordingId = b.GetRecordingId(), VesselSnapshot = b.GetVesselSnapshot() };
                RecordingStore.DeserializePartEvents(b.BuildTrajectoryNode(), rec);
                PartEvent placed = Assert.Single(rec.PartEvents);
                Assert.Equal(PartEventType.InventoryPartPlaced, placed.eventType);
                Assert.Equal(100000u, placed.partPersistentId);
                recordings.Add(rec);
            }
            Assert.Equal(Cluster.Length, recordings.Count);

            var findings = new Inv4PartEventPid().Evaluate(new AnalyzerModel
            {
                SaveName = "deployables",
                Recordings = recordings
            }).ToList();
            Assert.DoesNotContain(findings, f => f.Level == VerdictLevel.Fail);
        }

        // ---- the live wiring, member by member ----

        private static (RecordingTree tree, Recording kerbal) LiveTreeWithKerbal()
        {
            var tree = new RecordingTree { Id = "treeCluster", TreeName = "rover science" };
            var kerbal = new Recording
            {
                RecordingId = KerbalRecId,
                TreeId = tree.Id,
                VesselPersistentId = KerbalPid,
                VesselName = "Bob Kerman",
                Generation = 1
            };
            tree.AddOrReplaceRecording(kerbal);
            tree.ActiveRecordingId = kerbal.RecordingId;
            return (tree, kerbal);
        }

        private static Recording Place(RecordingTree tree, Recording kerbal, string part, string vessel,
            double ut, uint vesselPid, uint partPid)
        {
            var (bp, member) = GroundPartPlacement.BuildPlacementBranchData(
                kerbal.RecordingId, tree.Id, ut, vesselPid, vessel, kerbal.Generation);
            member.VesselSnapshot = new ConfigNode("VESSEL");
            GroundPartPlacement.AttachMember(tree, kerbal, bp, member, partPid, part, ut);
            return member;
        }

        [Fact]
        public void LiveCluster_EachPlacementAddsItsOwnMember_AndTheKerbalRecordsNothing()
        {
            var (tree, kerbal) = LiveTreeWithKerbal();
            var members = new List<Recording>();
            foreach (var c in Cluster)
                members.Add(Place(tree, kerbal, c.part, c.vessel, c.ut, c.vesselPid, c.partPid));

            Assert.Equal(Cluster.Length, tree.BranchPoints.Count);
            Assert.Equal(Cluster.Length, members.Select(m => m.RecordingId).Distinct().Count());
            Assert.Equal(Cluster.Length, members.Select(m => m.ParentBranchPointId).Distinct().Count());
            for (int i = 0; i < Cluster.Length; i++)
            {
                Assert.Equal(members[i].RecordingId, tree.BackgroundMap[Cluster[i].vesselPid]);
                PartEvent placed = Assert.Single(members[i].PartEvents);
                Assert.Equal(Cluster[i].partPid, placed.partPersistentId);
                Assert.Equal(Cluster[i].ut, placed.ut);
                Assert.Equal(members[i].Generation, kerbal.Generation + 1);
            }
            Assert.Empty(kerbal.PartEvents);
            Assert.Null(kerbal.ChildBranchPointId);
            Assert.Equal(Cluster.Length,
                logLines.Count(l => l.Contains("Ground part placed: tree member created")));
            Assert.Equal(Cluster.Length,
                logLines.Count(l => l.Contains("Part event captured: InventoryPartPlaced")));
        }

        [Fact]
        public void LiveCluster_PickUpAmongSiblings_OnlyThatMemberIsDisassembled_RePlaceIsANewMember()
        {
            var (tree, kerbal) = LiveTreeWithKerbal();
            var members = new List<Recording>();
            foreach (var c in Cluster.Take(5))
                members.Add(Place(tree, kerbal, c.part, c.vessel, c.ut, c.vesselPid, c.partPid));
            var seismo = Cluster[2];
            Recording first = members[2];
            var pending = new HashSet<uint>();

            // The pick-up: ONE Removed event on the seismometer's member, the stock repeat
            // signal deduplicated, and not one event on any sibling.
            Assert.Equal(GroundPartPlacement.RemovedSignalOutcome.Recorded,
                GroundPartPlacement.HandleRemovedSignal(tree, seismo.vesselPid, seismo.partPid, seismo.part, 160.0, pending));
            Assert.Equal(GroundPartPlacement.RemovedSignalOutcome.Deduplicated,
                GroundPartPlacement.HandleRemovedSignal(tree, seismo.vesselPid, seismo.partPid, seismo.part, 164.7, pending));
            Assert.True(GroundPartPlacement.IsRetrievalDeath(
                GroundPartPlacement.IsPlacedPartMember(tree, first), pending.Contains(seismo.vesselPid), true));
            ParsekFlight.ApplyDisassembledTerminal(first, 164.7, GroundPartPlacement.RetrievedTerminalReason);
            for (int i = 0; i < members.Count; i++)
            {
                if (i == 2) continue;
                Assert.Single(members[i].PartEvents);
                Assert.False(pending.Contains(Cluster[i].vesselPid));
                Assert.False(GroundPartPlacement.IsRetrievalDeath(
                    GroundPartPlacement.IsPlacedPartMember(tree, members[i]),
                    pending.Contains(Cluster[i].vesselPid), true));
            }

            // The re-placement: stock makes a NEW vessel with a new part pid, so it is a new
            // member under a new branch point; the picked-up member keeps its history.
            const uint rePlacedVesselPid = 700000001u;
            const uint rePlacedPartPid = 700000002u;
            Recording second = Place(tree, kerbal, seismo.part, seismo.vessel, 170.0, rePlacedVesselPid, rePlacedPartPid);
            Assert.NotEqual(first.RecordingId, second.RecordingId);
            Assert.NotEqual(first.ParentBranchPointId, second.ParentBranchPointId);
            Assert.True(GroundPartPlacement.IsPlacedPartMember(tree, second));
            Assert.Equal(new[] { PartEventType.InventoryPartPlaced, PartEventType.InventoryPartRemoved },
                first.PartEvents.Select(e => e.eventType).ToArray());
            PartEvent rePlaced = Assert.Single(second.PartEvents);
            Assert.Equal(rePlacedPartPid, rePlaced.partPersistentId);
            Assert.Equal(TerminalState.Disassembled, first.TerminalStateValue);
            Assert.Null(second.TerminalStateValue);

            // A pick-up of the NEW placement lands on the new member, never on the old one,
            // and a late repeat signal for the old vessel is still only a duplicate.
            Assert.Equal(GroundPartPlacement.RemovedSignalOutcome.Recorded,
                GroundPartPlacement.HandleRemovedSignal(tree, rePlacedVesselPid, rePlacedPartPid, seismo.part, 180.0, pending));
            Assert.Equal(2, second.PartEvents.Count);
            Assert.Equal(2, first.PartEvents.Count);
            Assert.Equal(GroundPartPlacement.RemovedSignalOutcome.Deduplicated,
                GroundPartPlacement.HandleRemovedSignal(tree, seismo.vesselPid, seismo.partPid, seismo.part, 181.0, pending));
        }

        [Fact]
        public void LiveCluster_CentralStationPickUp_IsARetrievalWithoutARemovedEvent_ACrashIsNot()
        {
            // The Central Station carries no ModuleGroundSciencePart, so stock fires no
            // Removed signal for it; the destroy seam reads deployedOnGround instead.
            var (tree, kerbal) = LiveTreeWithKerbal();
            var cs = Cluster[3];
            Recording station = Place(tree, kerbal, cs.part, cs.vessel, cs.ut, cs.vesselPid, cs.partPid);
            Recording rtg = Place(tree, kerbal, Cluster[0].part, Cluster[0].vessel, Cluster[0].ut,
                Cluster[0].vesselPid, Cluster[0].partPid);
            bool isMember = GroundPartPlacement.IsPlacedPartMember(tree, station);

            Assert.True(GroundPartPlacement.IsRetrievalDeath(isMember, false, deployedOnGround: false));
            Assert.False(GroundPartPlacement.IsRetrievalDeath(isMember, false, deployedOnGround: true));
            Assert.False(GroundPartPlacement.IsRetrievalDeath(isMember, false, deployedOnGround: null));
            // The kerbal dying next to the station is never a retrieval, whatever it reads.
            Assert.False(GroundPartPlacement.IsRetrievalDeath(
                GroundPartPlacement.IsPlacedPartMember(tree, kerbal), true, false));
            Assert.Single(rtg.PartEvents);
        }

        [Fact]
        public void PlacementGate_RefusesASiblingReAnnounced_AndAPlacementByAnotherKerbal()
        {
            // The recorder's own tree-member test (ParsekFlight.OnDeployGroundPart) reads the
            // BackgroundMap; a sibling vessel re-announced in a later frame is a member, and
            // the gate must refuse it rather than add a second member for one vessel.
            var (tree, kerbal) = LiveTreeWithKerbal();
            var rtg = Cluster[0];
            Place(tree, kerbal, rtg.part, rtg.vessel, rtg.ut, rtg.vesselPid, rtg.partPid);
            bool isTreeMember = tree.BackgroundMap.ContainsKey(rtg.vesselPid);
            Assert.True(isTreeMember);
            Assert.Equal(GroundPartPlacementVerdict.AlreadyTreeMember,
                GroundPartPlacement.EvaluatePlacement(true, true, true, KerbalPid, KerbalPid,
                    true, true, 1, rtg.part, rtg.part, isTreeMember));
            // The NEXT part's fresh vessel is not a member and records.
            var solar = Cluster[1];
            Assert.Equal(GroundPartPlacementVerdict.Record,
                GroundPartPlacement.EvaluatePlacement(true, true, true, KerbalPid, KerbalPid,
                    true, true, 1, solar.part, solar.part, tree.BackgroundMap.ContainsKey(solar.vesselPid)));
            // A second kerbal placing while the tree records Bob is not this recording's
            // placement: refused, however many siblings Bob already placed.
            Assert.Equal(GroundPartPlacementVerdict.ActiveVesselNotRecorded,
                GroundPartPlacement.EvaluatePlacement(true, true, true, 42u, KerbalPid,
                    true, true, 1, solar.part, solar.part, false));
            Assert.Single(tree.BranchPoints);
        }

        // ---- the spawn decisions ----

        private static RecordingTree SpawnReadyCluster(out Recording kerbal, out Recording pickedUp,
            out Recording rePlaced)
        {
            var (tree, k) = LiveTreeWithKerbal();
            kerbal = k;
            foreach (var c in Cluster)
            {
                Recording m = Place(tree, kerbal, c.part, c.vessel, c.ut, c.vesselPid, c.partPid);
                m.TerminalStateValue = TerminalState.Landed;
            }
            pickedUp = tree.Recordings[tree.BackgroundMap[Cluster[2].vesselPid]];
            ParsekFlight.ApplyDisassembledTerminal(pickedUp, 164.7, GroundPartPlacement.RetrievedTerminalReason);
            rePlaced = Place(tree, kerbal, Cluster[2].part, Cluster[2].vessel, 170.0, 700000001u, 700000002u);
            rePlaced.TerminalStateValue = TerminalState.Landed;
            kerbal.VesselSnapshot = new ConfigNode("VESSEL");
            kerbal.TerminalStateValue = TerminalState.Landed;
            return tree;
        }

        [Fact]
        public void SpawnDecision_EveryStandingMemberAndTheKerbalSpawn_ThePickedUpMemberDoesNot()
        {
            RecordingTree tree = SpawnReadyCluster(out Recording kerbal, out Recording pickedUp, out Recording rePlaced);

            int spawning = 0;
            foreach (Recording rec in tree.Recordings.Values)
            {
                var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, false, tree);
                bool expected = !ReferenceEquals(rec, pickedUp);
                Assert.True(expected == needsSpawn,
                    $"{rec.VesselName} ({rec.RecordingId}): needsSpawn={needsSpawn} reason={reason}");
                Assert.Equal(expected, RecordingTree.IsSpawnableLeaf(rec));
                if (needsSpawn) spawning++;
            }
            // Eleven placements, one picked up and placed again: 11 standing parts + Bob.
            Assert.Equal(Cluster.Length + 1, spawning);
            // Eleven placement branch points under the kerbal never make him a non-leaf.
            Assert.False(GhostPlaybackLogic.IsNonLeafInTree(kerbal, tree));
            Assert.True(GhostPlaybackLogic.IsFinalSpawnSegment(rePlaced, tree));
            Assert.Equal(Cluster.Length + 1, tree.GetSpawnableLeaves().Count);
        }

        [Fact]
        public void AreAllLeavesTerminal_ClusterStaysAliveWhileAnyMemberStands()
        {
            RecordingTree tree = SpawnReadyCluster(out Recording kerbal, out _, out _);
            kerbal.TerminalStateValue = TerminalState.Boarded;
            Assert.False(RecordingTree.AreAllLeavesTerminal(tree.Recordings, null, false));

            // Pick every standing part up: only then is every leaf terminal.
            foreach (Recording rec in tree.Recordings.Values)
                if (GroundPartPlacement.IsPlacedPartMember(tree, rec))
                    rec.TerminalStateValue = TerminalState.Disassembled;
            Assert.True(RecordingTree.AreAllLeavesTerminal(tree.Recordings, null, false));
        }

        // ---- TrimBoringTail on a stationary placed member ----

        private static Recording StationaryMember(double placedUT, double endUT, double deployUT)
        {
            var rec = new Recording
            {
                RecordingId = "05add69e18ff47499570103032eabe83",
                VesselName = "Mini-NUK-PD Radioisotope Thermoelectric Generator",
                VesselSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = TerminalState.Landed,
                TerminalPosition = new SurfacePosition
                {
                    body = "Kerbin",
                    latitude = SiteLat,
                    longitude = SiteLon,
                    altitude = SiteAlt,
                    rotation = Quaternion.identity,
                    situation = SurfaceSituation.Landed
                },
                ExplicitEndUT = endUT
            };
            // A placed part never moves: 0.25 s samples at one spot, like the operator's 989.
            for (double t = placedUT; t <= endUT + 1e-9; t += 0.25)
                rec.Points.Add(new TrajectoryPoint
                {
                    ut = t, bodyName = "Kerbin", latitude = SiteLat, longitude = SiteLon,
                    altitude = SiteAlt, rotation = Quaternion.identity
                });
            rec.TrackSections.Add(new TrackSection
                { environment = SegmentEnvironment.SurfaceStationary, startUT = placedUT, endUT = endUT });
            rec.PartEvents.Add(GroundPartPlacement.BuildInventoryEvent(
                PartEventType.InventoryPartPlaced, placedUT, 4263919010u, "DeployedRTG"));
            rec.PartEvents.Add(new PartEvent
            {
                ut = deployUT, partPersistentId = 4263919010u, partName = "DeployedRTG",
                eventType = PartEventType.DeployableExtended
            });
            return rec;
        }

        [Fact]
        public void TrimBoringTail_StationaryPlacedMember_KeepsASpawnableEndpointAtThePlacedSpot()
        {
            // The operator's RTG: placed at 120.42, deployed by 123.5, recorded to 337.5;
            // the commit trimmed it to 133.4 (`trimUT=133.5 lastInterestingUT=123.5`).
            Recording rec = StationaryMember(120.42, 337.5, 123.5);
            var all = new List<Recording> { rec };

            Assert.True(RecordingOptimizer.TrimBoringTail(rec, all));

            double expectedEnd = 123.5 + RecordingOptimizer.DefaultTailBufferSeconds;
            Assert.InRange(rec.EndUT, expectedEnd - 0.26, expectedEnd);
            Assert.Equal(rec.EndUT, rec.ExplicitEndUT);
            Assert.True(rec.Points.Count >= 2);
            // The endpoint is still the placed spot, so the spawn lands where the part stood.
            TrajectoryPoint last = rec.Points[rec.Points.Count - 1];
            Assert.Equal(SiteLat, last.latitude);
            Assert.Equal(SiteLon, last.longitude);
            Assert.Equal(SiteAlt, last.altitude);
            Assert.Equal(TerminalState.Landed, rec.TerminalStateValue);
            Assert.True(RecordingTree.IsSpawnableLeaf(rec));
            // Both events survive (they precede the trim point) and the placement stays first.
            Assert.Equal(new[] { PartEventType.InventoryPartPlaced, PartEventType.DeployableExtended },
                rec.PartEvents.Select(e => e.eventType).ToArray());
            Assert.Contains(logLines, l => l.Contains("TrimBoringTail: trimmed")
                && l.Contains("05add69e18ff47499570103032eabe83"));
        }

        [Fact]
        public void TrimBoringTail_TheDeployEventHoldsTheTrimPoint_NotThePlacement()
        {
            // Without the deploy event the placement itself is the last interesting moment,
            // so the trim comes earlier; the deploy animation is what the buffer protects.
            Recording withDeploy = StationaryMember(120.42, 337.5, 123.5);
            Recording placedOnly = StationaryMember(120.42, 337.5, 123.5);
            placedOnly.PartEvents.RemoveAll(e => e.eventType == PartEventType.DeployableExtended);

            Assert.True(RecordingOptimizer.TrimBoringTail(withDeploy, new List<Recording> { withDeploy }));
            Assert.True(RecordingOptimizer.TrimBoringTail(placedOnly, new List<Recording> { placedOnly }));
            Assert.True(placedOnly.EndUT < withDeploy.EndUT,
                $"placedOnly.EndUT={placedOnly.EndUT} withDeploy.EndUT={withDeploy.EndUT}");
            Assert.InRange(placedOnly.EndUT,
                120.42 + RecordingOptimizer.DefaultTailBufferSeconds - 0.26,
                120.42 + RecordingOptimizer.DefaultTailBufferSeconds);
            Assert.True(RecordingTree.IsSpawnableLeaf(placedOnly));
        }
    }
}
