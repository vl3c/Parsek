using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// ROCK-RELEASE-REFLY-SLOT (owner ruling 2026-11-15, "No Re-Fly for rocks"): a released
    /// asteroid or comet records as its own branch and spawns real at its tip, but it is
    /// never a Re-Fly slot and releasing one alone authors no Rewind Point.
    /// </summary>
    [Collection("Sequential")]
    public class RockReleaseReFlySlotTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RockReleaseReFlySlotTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // ---- the pure Re-Fly controllability decision ----

        [Fact]
        public void ReleasedAsteroid_SpaceObjectType_IsNotReFlyControllable()
        {
            Assert.False(ParsekFlight.IsReFlyControllable(
                VesselType.SpaceObject, isEva: false, partCount: 1, commandPartCount: 0,
                rockPartCount: 1, out string reason));
            Assert.Equal("space-object", reason);
        }

        [Fact]
        public void SpaceObjectType_WithACommandPart_IsStillNotReFlyControllable()
        {
            Assert.False(ParsekFlight.IsReFlyControllable(
                VesselType.SpaceObject, isEva: false, partCount: 2, commandPartCount: 1,
                rockPartCount: 1, out string reason));
            Assert.Equal("space-object", reason);
        }

        [Fact]
        public void RockOnlyVessel_WithAnotherType_IsNotReFlyControllable()
        {
            Assert.False(ParsekFlight.IsReFlyControllable(
                VesselType.Probe, isEva: false, partCount: 1, commandPartCount: 0,
                rockPartCount: 1, out string reason));
            Assert.Equal("space-object-parts", reason);
        }

        [Fact]
        public void ShipCarryingARock_KeepsItsCommandModule_IsReFlyControllable()
        {
            Assert.True(ParsekFlight.IsReFlyControllable(
                VesselType.Ship, isEva: false, partCount: 3, commandPartCount: 1,
                rockPartCount: 1, out string reason));
            Assert.Equal("command-carrying-space-object", reason);
        }

        [Theory]
        [InlineData(VesselType.Ship, false, 2, 1, 0, true, "command")]
        [InlineData(VesselType.Probe, false, 1, 1, 0, true, "command")]
        [InlineData(VesselType.EVA, true, 1, 0, 0, true, "eva")]
        [InlineData(VesselType.Debris, false, 2, 0, 0, false, "no-command")]
        [InlineData(VesselType.Ship, false, 0, 0, 0, false, "no-command")]
        public void ReFlyControllable_MatchesTrackableForNonRocks(
            VesselType type, bool isEva, int parts, int command, int rock, bool expected, string expectedReason)
        {
            Assert.Equal(expected, ParsekFlight.IsReFlyControllable(
                type, isEva, parts, command, rock, out string reason));
            Assert.Equal(expectedReason, reason);
        }

        [Fact]
        public void ReleasedRock_StillRecordsAsItsOwnBranch()
        {
            // IsTrackableVessel keeps the SpaceObject (trackable=true), so the released rock
            // gets its own branch and spawns at its tip.
            Assert.True(ParsekFlight.ShouldRecordAsOwnBranch(trackable: true, crewCount: 0));
        }

        // ---- the split classification ----

        [Fact]
        public void ShipReleasingARock_IsASingleControllableSplit_NoRewindPoint()
        {
            const uint shipPid = 1001u;
            const uint rockPid = 2002u;
            var kinds = new Dictionary<uint, (VesselType type, int parts, int command, int rock)>
            {
                { shipPid, (VesselType.Ship, 3, 1, 0) },
                { rockPid, (VesselType.SpaceObject, 1, 0, 1) },
            };
            Func<uint, bool?> predicate = pid =>
            {
                var k = kinds[pid];
                return ParsekFlight.IsReFlyControllable(k.type, false, k.parts, k.command, k.rock, out _);
            };

            List<uint> controllable = SegmentBoundaryLogic.IdentifyControllableChildren(
                shipPid, new List<uint> { shipPid, rockPid }, predicate);

            Assert.Equal(new List<uint> { shipPid }, controllable);
            Assert.False(SegmentBoundaryLogic.IsMultiControllableSplit(controllable.Count));
        }

        [Fact]
        public void ShipCarryingARock_SeparatingFromAProbe_IsMultiControllable()
        {
            const uint shipPid = 1001u;
            const uint probePid = 3003u;
            var kinds = new Dictionary<uint, (VesselType type, int parts, int command, int rock)>
            {
                { shipPid, (VesselType.Ship, 4, 1, 1) },
                { probePid, (VesselType.Probe, 2, 1, 0) },
            };
            Func<uint, bool?> predicate = pid =>
            {
                var k = kinds[pid];
                return ParsekFlight.IsReFlyControllable(k.type, false, k.parts, k.command, k.rock, out _);
            };

            List<uint> controllable = SegmentBoundaryLogic.IdentifyControllableChildren(
                shipPid, new List<uint> { shipPid, probePid }, predicate);

            Assert.Equal(2, controllable.Count);
            Assert.True(SegmentBoundaryLogic.IsMultiControllableSplit(controllable.Count));
        }

        // ---- the snapshot helpers ----

        [Theory]
        [InlineData("PotatoRoid", true)]
        [InlineData("PotatoComet", true)]
        [InlineData("potatoroid", true)]
        [InlineData("PotatoRoid.Variant", true)]
        [InlineData("mk1pod.v2", false)]
        [InlineData("GrapplingDevice", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsRockPartName(string name, bool expected)
        {
            Assert.Equal(expected, VesselSnapshotOps.IsRockPartName(name));
        }

        [Theory]
        [InlineData("ModuleAsteroid", true)]
        [InlineData("ModuleComet", true)]
        [InlineData("ModuleAsteroidDrill", false)]
        [InlineData("ModuleCommand", false)]
        [InlineData(null, false)]
        public void IsRockModuleName(string name, bool expected)
        {
            Assert.Equal(expected, VesselSnapshotOps.IsRockModuleName(name));
        }

        [Fact]
        public void ClawedAsteroidShip_IsNotASpaceObjectOnlySnapshot()
        {
            ConfigNode ship = VesselSnapshotBuilder.ClawedAsteroidShip("Grabber", "Jebediah Kerman", 500u).Build();
            Assert.False(VesselSnapshotOps.IsSpaceObjectOnlySnapshot(ship, out string why));
            Assert.Equal("has-non-rock-part", why);
        }

        [Fact]
        public void ReleasedAsteroidSnapshot_TypedSpaceObject_IsASpaceObjectOnlySnapshot()
        {
            ConfigNode rock = new VesselSnapshotBuilder()
                .WithName("Ast. HSJ-227").WithType("SpaceObject")
                .AddPart("PotatoRoid").Build();
            Assert.True(VesselSnapshotOps.IsSpaceObjectOnlySnapshot(rock, out string why));
            Assert.Equal("type-space-object", why);
        }

        [Fact]
        public void RockOnlySnapshot_WithAnotherType_IsASpaceObjectOnlySnapshot()
        {
            ConfigNode rock = new VesselSnapshotBuilder()
                .WithName("Renamed rock").WithType("Station")
                .AddPart("SomeModdedRock")
                .AddModuleToPart(0, "ModuleComet").Build();
            Assert.True(VesselSnapshotOps.IsSpaceObjectOnlySnapshot(rock, out string why));
            Assert.Equal("rock-parts-only", why);
        }

        // ---- the Unfinished Flights legacy guard ----

        [Fact]
        public void LegacyRewindPointSlot_OnAReleasedRock_IsRefusedAsSpaceObjectNotFlyable()
        {
            const string treeId = "tree_rock";
            const string bpId = "bp_rock_release";
            var rock = new Recording
            {
                RecordingId = "rec_rock",
                VesselName = "Ast. HSJ-227",
                MergeState = MergeState.CommittedProvisional,
                TerminalStateValue = TerminalState.Orbiting,
                ParentBranchPointId = bpId,
                VesselSnapshot = new VesselSnapshotBuilder()
                    .WithName("Ast. HSJ-227").WithType("SpaceObject")
                    .AddPart("PotatoRoid").Build()
            };
            InstallTree(treeId, rock);
            var rp = Rp("rp_rock", bpId, "rec_ship", "rec_rock");
            InstallScenario(new List<RewindPoint> { rp });

            ParsekLog.ResetRateLimitsForTesting();
            logLines.Clear();
            Assert.False(UnfinishedFlightClassifier.TryQualify(
                rock, rp.ChildSlots[1], rp, out string reason));
            Assert.Equal(UnfinishedFlightClassifier.SpaceObjectNotFlyableReason, reason);
            Assert.Equal("spaceObjectNotFlyable", reason);
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("IsUnfinishedFlight=false")
                && l.Contains("rec=rec_rock")
                && l.Contains("reason=spaceObjectNotFlyable")
                && l.Contains("rule=type-space-object")
                && l.Contains("rp=rp_rock"));
        }

        [Fact]
        public void ShipCarryingARock_IsNotRefusedByTheRockGuard()
        {
            const string treeId = "tree_ship";
            const string bpId = "bp_ship_split";
            var ship = new Recording
            {
                RecordingId = "rec_ship",
                VesselName = "Grabber",
                MergeState = MergeState.CommittedProvisional,
                TerminalStateValue = TerminalState.Destroyed,
                ParentBranchPointId = bpId,
                VesselSnapshot = VesselSnapshotBuilder.ClawedAsteroidShip("Grabber", "Jebediah Kerman", 500u).Build()
            };
            InstallTree(treeId, ship);
            var rp = Rp("rp_ship", bpId, "rec_other", "rec_ship");
            InstallScenario(new List<RewindPoint> { rp });

            Assert.False(UnfinishedFlightClassifier.IsSpaceObjectRecording(ship, out string rule));
            Assert.Equal("has-non-rock-part", rule);
            UnfinishedFlightClassifier.TryQualify(ship, rp.ChildSlots[1], rp, out string reason);
            Assert.NotEqual(UnfinishedFlightClassifier.SpaceObjectNotFlyableReason, reason);
        }

        [Fact]
        public void IsSpaceObjectRecording_FallsBackToTheGhostSnapshot()
        {
            var rock = new Recording
            {
                RecordingId = "rec_rock_ghost",
                VesselSnapshot = null,
                GhostVisualSnapshot = new VesselSnapshotBuilder()
                    .WithType("SpaceObject").AddPart("PotatoComet").Build()
            };
            Assert.True(UnfinishedFlightClassifier.IsSpaceObjectRecording(rock, out string rule));
            Assert.Equal("type-space-object", rule);
            Assert.False(UnfinishedFlightClassifier.IsSpaceObjectRecording(
                new Recording { RecordingId = "rec_none" }, out string none));
            Assert.Equal("no-snapshot", none);
        }

        private static void InstallTree(string treeId, params Recording[] recordings)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Test tree",
                RootRecordingId = recordings.FirstOrDefault()?.RecordingId
            };
            foreach (var rec in recordings)
            {
                rec.TreeId = treeId;
                tree.AddOrReplaceRecording(rec);
                RecordingStore.AddRecordingWithTreeForTesting(rec);
            }
            RecordingStore.AddCommittedTreeForTesting(tree);
        }

        private static void InstallScenario(List<RewindPoint> rps)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = rps ?? new List<RewindPoint>()
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            scenario.BumpTombstoneStateVersion();
            EffectiveState.ResetCachesForTesting();
        }

        private static RewindPoint Rp(string rpId, string bpId, params string[] slotRecordingIds)
        {
            var slots = new List<ChildSlot>();
            for (int i = 0; i < slotRecordingIds.Length; i++)
            {
                slots.Add(new ChildSlot
                {
                    SlotIndex = i,
                    OriginChildRecordingId = slotRecordingIds[i],
                    Controllable = true
                });
            }
            return new RewindPoint
            {
                RewindPointId = rpId,
                BranchPointId = bpId,
                UT = 0.0,
                FocusSlotIndex = -1,
                ChildSlots = slots
            };
        }
    }
}
