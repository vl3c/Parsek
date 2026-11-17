using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The landed variant of the ghost map vessel primitive
    /// (<see cref="GhostMapVessels.BuildLanded"/>): the pure VESSEL node builder, the pure input
    /// check, and the refusal path of the entry point, which returns before any game call.
    /// </summary>
    [Collection("Sequential")]
    public class GhostMapVesselsLandedTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly CultureInfo priorCulture;

        public GhostMapVesselsLandedTests()
        {
            priorCulture = Thread.CurrentThread.CurrentCulture;
            GhostMapPresence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            Thread.CurrentThread.CurrentCulture = priorCulture;
            GhostMapPresence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static ConfigNode BuildNode(
            string name = "Ghost: Kerbal X",
            int bodyIndex = 1,
            double lat = -0.0972077655,
            double lon = -74.5576726244,
            double alt = 67.1234567891,
            VesselType vtype = VesselType.Lander)
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", "sensorBarometer");
            var discovery = new ConfigNode("DISCOVERY");
            discovery.AddValue("state", "29");
            return GhostMapVessels.BuildLandedGhostVesselNode(
                name, vtype, bodyIndex, lat, lon, alt,
                4242424242u, "0123456789abcdef0123456789abcdef", 12345.678,
                part, discovery);
        }

        [Fact]
        public void Node_IsLandedSituation()
        {
            ConfigNode node = BuildNode();

            Assert.Equal("VESSEL", node.name);
            Assert.Equal("LANDED", node.GetValue("sit"));
            Assert.Equal("True", node.GetValue("landed"));
            Assert.Equal("False", node.GetValue("splashed"));
            Assert.Equal("True", node.GetValue("skipGroundPositioning"));
            Assert.Equal("False", node.GetValue("vesselSpawning"));
            Assert.Equal(string.Empty, node.GetValue("landedAt"));
        }

        [Fact]
        public void Node_WritesLatLonAltRoundTrip_UnderACommaCulture()
        {
            // de-DE writes 1,5 for 1.5: the node must read the same on every machine.
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            const double lat = -0.0972077655;
            const double lon = -74.5576726244;
            const double alt = 67.1234567891;

            ConfigNode node = BuildNode(lat: lat, lon: lon, alt: alt);

            Assert.Equal(lat.ToString("R", CultureInfo.InvariantCulture), node.GetValue("lat"));
            Assert.Equal(lon.ToString("R", CultureInfo.InvariantCulture), node.GetValue("lon"));
            Assert.Equal(alt.ToString("R", CultureInfo.InvariantCulture), node.GetValue("alt"));
            Assert.Equal(lat, double.Parse(node.GetValue("lat"), CultureInfo.InvariantCulture));
            Assert.Equal(lon, double.Parse(node.GetValue("lon"), CultureInfo.InvariantCulture));
            Assert.Equal(alt, double.Parse(node.GetValue("alt"), CultureInfo.InvariantCulture));
            Assert.Equal("12345.678", node.GetValue("lct"));
            Assert.DoesNotContain(",", node.GetValue("alt"));
        }

        [Fact]
        public void Node_KeepsTheCallersNameAndItsPrefix()
        {
            string name = GhostMapPresence.GhostVesselNamePrefix + "Ana - Kerbal X";

            ConfigNode node = BuildNode(name: name);

            Assert.Equal("Ghost: Ana - Kerbal X", node.GetValue("name"));
            Assert.StartsWith(GhostMapPresence.GhostVesselNamePrefix, node.GetValue("name"));
        }

        [Fact]
        public void Node_CarriesTheCanonicalSurfaceOrbitOnTheBody()
        {
            ConfigNode node = BuildNode(bodyIndex: 3);

            Assert.True(VesselSnapshotOps.HasCanonicalSurfaceOrbitForBody(node, 3));
            Assert.False(VesselSnapshotOps.HasCanonicalSurfaceOrbitForBody(node, 1));
            Assert.Single(node.GetNodes("ORBIT"));
        }

        [Fact]
        public void Node_IsTheOnePartGhostSignature_WithTheOrbitBuildSettings()
        {
            ConfigNode node = BuildNode(vtype: VesselType.Rover);

            ConfigNode[] parts = node.GetNodes("PART");
            Assert.Single(parts);
            Assert.Equal("sensorBarometer", parts[0].GetValue("name"));
            Assert.Equal("True", node.GetValue("prst"));
            Assert.Equal("False", node.GetValue("cln"));
            Assert.Equal("Rover", node.GetValue("type"));
            Assert.Equal("4242424242", node.GetValue("persistentId"));
            Assert.Equal("0123456789abcdef0123456789abcdef", node.GetValue("pid"));
            Assert.Equal("0", node.GetValue("root"));
            Assert.Equal("0,0,0,1", node.GetValue("rot"));
            Assert.NotNull(node.GetNode("DISCOVERY"));
            Assert.NotNull(node.GetNode("ACTIONGROUPS"));
            Assert.NotNull(node.GetNode("FLIGHTPLAN"));
            Assert.NotNull(node.GetNode("CTRLSTATE"));
            Assert.NotNull(node.GetNode("VESSELMODULES"));
        }

        [Theory]
        [InlineData(true, 0.0, 0.0, 0.0, "Ghost: A", null)]
        [InlineData(true, 90.0, 540.0, -12.5, "Ghost: A", null)]
        [InlineData(true, -90.0, -180.0, 0.0, "Ghost: A", null)]
        [InlineData(false, 0.0, 0.0, 0.0, "Ghost: A", "no-body")]
        [InlineData(false, double.NaN, 0.0, 0.0, null, "no-body")]
        [InlineData(true, 0.0, 0.0, 0.0, null, "no-name")]
        [InlineData(true, 0.0, 0.0, 0.0, "", "no-name")]
        [InlineData(true, 90.0001, 0.0, 0.0, "Ghost: A", "lat-invalid")]
        [InlineData(true, -90.0001, 0.0, 0.0, "Ghost: A", "lat-invalid")]
        [InlineData(true, double.NaN, 0.0, 0.0, "Ghost: A", "lat-invalid")]
        [InlineData(true, 0.0, double.PositiveInfinity, 0.0, "Ghost: A", "lon-invalid")]
        [InlineData(true, 0.0, double.NaN, 0.0, "Ghost: A", "lon-invalid")]
        [InlineData(true, 0.0, 0.0, double.NaN, "Ghost: A", "alt-invalid")]
        [InlineData(true, 0.0, 0.0, double.NegativeInfinity, "Ghost: A", "alt-invalid")]
        public void ClassifyLandedRefusal_TruthTable(
            bool hasBody, double lat, double lon, double alt, string name, string expected)
        {
            Assert.Equal(expected, GhostMapVessels.ClassifyLandedRefusal(hasBody, lat, lon, alt, name));
        }

        [Fact]
        public void BuildLanded_NullBody_RefusesWithAWarnLine_AndRegistersNothing()
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

            bool built = GhostMapVessels.BuildLanded(
                null, 1.5, -74.25, 70.5, "Ghost: Witness", VesselType.Probe,
                new GhostMapVesselOwner("lane-7", "rec-9"), "landed-test",
                out GhostMapVesselLoad load);

            Assert.False(built);
            Assert.Null(load.Vessel);
            Assert.Equal(0, GhostMapVessels.RegisteredCount);
            Assert.Contains(logLines, l => l.Contains("[GhostMap]")
                && l.Contains("[WARN]")
                && l.Contains("BuildLanded refused: reason=no-body name='Ghost: Witness' body=(unknown)")
                && l.Contains("lat=1.5 lon=-74.25 alt=70.5")
                && l.Contains("vtype=Probe owner=lane=lane-7/recording=rec-9 for landed-test"));
        }

        [Fact]
        public void BuildLanded_OwnOwnerAndNoContext_AreNamedInTheRefusal()
        {
            bool built = GhostMapVessels.BuildLanded(
                null, 0.0, 0.0, 0.0, null, VesselType.Ship,
                GhostMapVesselOwner.OwnTimeline, null, out _);

            Assert.False(built);
            Assert.Contains(logLines, l => l.Contains("[GhostMap]")
                && l.Contains("reason=no-body name='(null)'")
                && l.Contains("owner=own for (none)"));
        }
    }
}
