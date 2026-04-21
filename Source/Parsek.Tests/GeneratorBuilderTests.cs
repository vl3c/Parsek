using System;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class GeneratorBuilderTests
    {
        [Fact]
        public void VesselSnapshotBuilder_AsOrbiting_AddPartAndResource_WritesExpectedNodes()
        {
            ConfigNode vessel = new VesselSnapshotBuilder()
                .WithName("Orbiter")
                .WithPersistentId(42)
                .AsOrbiting(700000, 0.01, 1.5, lan: 2, argPe: 3, mna: 4, epoch: 5, refBody: 2)
                .AddPart("mk1pod.v2", crew: "Jebediah Kerman")
                .AddPart("batteryPack", position: "0,1,0", parentIndex: 0)
                .AddResourceToPart(1, "ElectricCharge", 10, 50)
                .Build();

            Assert.Equal("Orbiter", vessel.GetValue("name"));
            Assert.Equal("ORBITING", vessel.GetValue("sit"));
            Assert.Equal("False", vessel.GetValue("landed"));

            ConfigNode orbit = vessel.GetNode("ORBIT");
            Assert.NotNull(orbit);
            Assert.Equal("700000", orbit.GetValue("SMA"));
            Assert.Equal("0.01", orbit.GetValue("ECC"));
            Assert.Equal("1.5", orbit.GetValue("INC"));
            Assert.Equal("2", orbit.GetValue("REF"));

            ConfigNode[] parts = vessel.GetNodes("PART");
            Assert.Equal(2, parts.Length);
            Assert.Equal("Jebediah Kerman", parts[0].GetValue("crew"));
            Assert.Equal("100000", parts[0].GetValue("persistentId"));
            Assert.Equal("0", parts[1].GetValue("parent"));
            Assert.Equal("101111", parts[1].GetValue("persistentId"));

            ConfigNode resource = parts[1].GetNode("RESOURCE");
            Assert.NotNull(resource);
            Assert.Equal("ElectricCharge", resource.GetValue("name"));
            Assert.Equal("10", resource.GetValue("amount"));
            Assert.Equal("50", resource.GetValue("maxAmount"));
        }

        [Fact]
        public void VesselSnapshotBuilder_AddResourceToPart_OutOfRangeThrows()
        {
            var builder = VesselSnapshotBuilder.ProbeShip("Probe", pid: 7);

            var ex = Assert.Throws<ArgumentOutOfRangeException>(
                () => builder.AddResourceToPart(3, "ElectricCharge", 5, 5));

            Assert.Contains("Part index 3 out of range", ex.Message);
        }

        [Fact]
        public void RecordingBuilder_WithDefaultRotation_OnlyAppliesToIdentityInputs()
        {
            ConfigNode trajectory = new RecordingBuilder("Test Vessel")
                .WithDefaultRotation(0.1f, 0.2f, 0.3f, 0.4f)
                .AddPoint(100, 1, 2, 3)
                .AddPoint(110, 4, 5, 6, rotX: 9f, rotY: 8f, rotZ: 7f, rotW: 6f)
                .BuildTrajectoryNode();

            ConfigNode[] points = trajectory.GetNodes("POINT");
            Assert.Equal(2, points.Length);

            Assert.Equal("0.1", points[0].GetValue("rotX"));
            Assert.Equal("0.2", points[0].GetValue("rotY"));
            Assert.Equal("0.3", points[0].GetValue("rotZ"));
            Assert.Equal("0.4", points[0].GetValue("rotW"));

            Assert.Equal("9", points[1].GetValue("rotX"));
            Assert.Equal("8", points[1].GetValue("rotY"));
            Assert.Equal("7", points[1].GetValue("rotZ"));
            Assert.Equal("6", points[1].GetValue("rotW"));
        }

        [Fact]
        public void RecordingBuilder_WithBuilderSnapshots_BuildsExpectedInlineSnapshotNodes()
        {
            ConfigNode node = new RecordingBuilder("Probe Vessel")
                .AddPoint(100, 0, 0, 0)
                .WithVesselSnapshot(VesselSnapshotBuilder.ProbeShip("Vessel Snapshot", pid: 90))
                .WithGhostVisualSnapshot(VesselSnapshotBuilder.ProbeShip("Ghost Snapshot", pid: 91))
                .Build();

            ConfigNode vesselSnapshot = node.GetNode("VESSEL_SNAPSHOT");
            ConfigNode ghostSnapshot = node.GetNode("GHOST_VISUAL_SNAPSHOT");

            Assert.NotNull(vesselSnapshot);
            Assert.NotNull(ghostSnapshot);
            Assert.Equal("Vessel Snapshot", vesselSnapshot.GetValue("name"));
            Assert.Equal("Ghost Snapshot", ghostSnapshot.GetValue("name"));
        }

        [Fact]
        public void RecordingBuilder_WithRecordingGroups_BuildV3Metadata_DeduplicatesGroups()
        {
            ConfigNode node = new RecordingBuilder("Probe Vessel")
                .AddPoint(100, 0, 0, 0)
                .WithRecordingGroup("Alpha")
                .WithRecordingGroup("Alpha")
                .WithRecordingGroup("Beta")
                .BuildV3Metadata();

            string[] groups = node.GetValues("recordingGroup");
            Assert.Equal(2, groups.Length);
            Assert.Equal("Alpha", groups[0]);
            Assert.Equal("Beta", groups[1]);
        }
    }
}
