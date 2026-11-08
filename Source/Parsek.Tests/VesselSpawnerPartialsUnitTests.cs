using System;
using System.Collections.Generic;
using System.Security;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for the small decision and formatting units of the VesselSpawner partial
    /// files that had no direct test: the headless-access classifier, the overlap exemption
    /// tally, the spawn-failure counter, the relocation altitude (non-landed path), the
    /// endpoint state vector read, the spawn-coordinate source choice, the no-override
    /// velocity early return, the orbit period fallback, the optional-double formatter,
    /// the materialization-context and validation-context labels, the duplicate-bypass
    /// log key, the surface-terminal test, the retirement terminal and the rotation-body
    /// null path. None of these enters a body that calls a Unity native.
    /// </summary>
    [Collection("Sequential")]
    public class VesselSpawnerPartialsUnitTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public VesselSpawnerPartialsUnitTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        // ---- VesselSpawner.cs ----

        [Fact]
        public void HeadlessKspAccessFailure_WalksTheInnerChainForTheThreeKinds()
        {
            Assert.False(VesselSpawner.IsHeadlessKspAccessFailure(null));
            Assert.False(VesselSpawner.IsHeadlessKspAccessFailure(new InvalidOperationException("x")));
            Assert.True(VesselSpawner.IsHeadlessKspAccessFailure(new SecurityException("ecall")));
            Assert.True(VesselSpawner.IsHeadlessKspAccessFailure(new MethodAccessException("x")));
            Assert.True(VesselSpawner.IsHeadlessKspAccessFailure(new MissingMethodException("x")));
            // The kind may sit any depth down the inner chain (a type initializer wraps it).
            Assert.True(VesselSpawner.IsHeadlessKspAccessFailure(
                new TypeInitializationException("FlightGlobals",
                    new InvalidOperationException("wrap", new MissingMethodException("x")))));
            Assert.False(VesselSpawner.IsHeadlessKspAccessFailure(
                new TypeInitializationException("FlightGlobals", new NullReferenceException())));
            // A sibling member-access kind does not count: MissingFieldException shares
            // MissingMemberException with MissingMethodException but is neither listed type.
            Assert.False(VesselSpawner.IsHeadlessKspAccessFailure(new MissingFieldException("x")));
            Assert.False(VesselSpawner.IsHeadlessKspAccessFailure(new MemberAccessException("x")));
        }

        // ---- VesselSpawner.EndOfRecording.cs ----

        [Fact]
        public void CountExemption_TalliesByScopeAndAnswersWhetherExempt()
        {
            int same = 0, other = 0;
            Assert.False(VesselSpawner.CountExemption(OverlapExemptionScope.None, ref same, ref other));
            Assert.Equal(0, same);
            Assert.Equal(0, other);

            Assert.True(VesselSpawner.CountExemption(OverlapExemptionScope.SameTree, ref same, ref other));
            Assert.True(VesselSpawner.CountExemption(OverlapExemptionScope.SameTree, ref same, ref other));
            Assert.True(VesselSpawner.CountExemption(OverlapExemptionScope.OtherTree, ref same, ref other));
            Assert.Equal(2, same);
            Assert.Equal(1, other);

            // An undeclared scope value counts nowhere but still reads as exempt (!= None).
            Assert.True(VesselSpawner.CountExemption((OverlapExemptionScope)7, ref same, ref other));
            Assert.Equal(2, same);
            Assert.Equal(1, other);
        }

        [Fact]
        public void SpawnFailure_CountsTheAttemptThenWarnsUntilTheLastAttemptErrors()
        {
            var rec = new Recording { VesselName = "Lander" };

            VesselSpawner.LogSpawnFailure(rec, 4, 3);
            Assert.Equal(1, rec.SpawnAttempts);
            Assert.Contains(logLines, l => l.Contains("[WARN][Spawner]")
                && l.Contains("Vessel spawn failed for recording #4 (Lander)")
                && l.Contains("attempt 1/3"));

            VesselSpawner.LogSpawnFailure(rec, 4, 3);
            Assert.Equal(2, rec.SpawnAttempts);
            Assert.DoesNotContain(logLines, l => l.Contains("failed permanently"));

            VesselSpawner.LogSpawnFailure(rec, 4, 3);
            Assert.Equal(3, rec.SpawnAttempts);
            Assert.Contains(logLines, l => l.Contains("[ERROR][Spawner]")
                && l.Contains("Vessel spawn failed permanently for recording #4 (Lander) after 3 attempts"));

            // Past the cap it keeps counting and keeps reporting the permanent failure.
            logLines.Clear();
            VesselSpawner.LogSpawnFailure(rec, 4, 3);
            Assert.Equal(4, rec.SpawnAttempts);
            Assert.Contains(logLines, l => l.Contains("failed permanently"));
        }

        [Fact]
        public void RelocatedAltitude_NotLandedLike_UsesTheSnapshotAltElseZero()
        {
            // The non-landed path never reads the body, so a null body is fine there.
            var orbiting = new ConfigNode("VESSEL");
            orbiting.AddValue("sit", "ORBITING");
            orbiting.AddValue("alt", "123456.5");
            var withAlt = new Recording { VesselSnapshot = orbiting };
            Assert.Equal(123456.5, VesselSpawner.ResolveRelocatedAltitude(withAlt, null, 1.0, 2.0));
            Assert.Contains(logLines, l => l.Contains("[Spawner]")
                && l.Contains("Relocation altitude selected=")
                && l.Contains(" source=snapshot landedLike=False"));

            logLines.Clear();
            var noAlt = new ConfigNode("VESSEL");
            noAlt.AddValue("sit", "FLYING");
            Assert.Equal(0.0, VesselSpawner.ResolveRelocatedAltitude(
                new Recording { VesselSnapshot = noAlt }, null, 1.0, 2.0));
            Assert.Contains(logLines, l => l.Contains("Relocation altitude selected=")
                && l.Contains(" source=fallback landedLike=False"));

            logLines.Clear();
            Assert.Equal(0.0, VesselSpawner.ResolveRelocatedAltitude(null, null, 1.0, 2.0));
            Assert.Contains(logLines, l => l.Contains("source=fallback") && l.Contains("landedLike=False"));
        }

        [Fact]
        public void EndpointStateVector_LastPointOnTheBodyWithAFiniteVelocity()
        {
            TrajectoryPoint point;
            Assert.False(VesselSpawner.TryResolveEndpointStateVector(null, "Kerbin", out point));
            Assert.False(VesselSpawner.TryResolveEndpointStateVector(new Recording(), "Kerbin", out point));

            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 10, bodyName = "Mun", velocity = new Vector3(9, 9, 9) });
            rec.Points.Add(new TrajectoryPoint { ut = 20, bodyName = "Kerbin", velocity = new Vector3(1, 2, 3) });

            Assert.False(VesselSpawner.TryResolveEndpointStateVector(rec, null, out point));
            Assert.False(VesselSpawner.TryResolveEndpointStateVector(rec, "", out point));

            Assert.True(VesselSpawner.TryResolveEndpointStateVector(rec, "Kerbin", out point));
            Assert.Equal(20, point.ut);
            Assert.Equal(new Vector3(1, 2, 3), point.velocity);

            // Only the LAST point is read, and the body compare is ordinal.
            Assert.False(VesselSpawner.TryResolveEndpointStateVector(rec, "Mun", out point));
            Assert.Equal(20, point.ut);
            Assert.False(VesselSpawner.TryResolveEndpointStateVector(rec, "kerbin", out point));

            // A non-finite velocity refuses but still hands back the point.
            rec.Points.Add(new TrajectoryPoint { ut = 30, bodyName = "Kerbin", velocity = new Vector3(float.NaN, 0, 0) });
            Assert.False(VesselSpawner.TryResolveEndpointStateVector(rec, "Kerbin", out point));
            Assert.Equal(30, point.ut);
        }

        private static ConfigNode SnapshotAt(double lat, double lon, double alt)
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("lat", lat.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            snapshot.AddValue("lon", lon.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            snapshot.AddValue("alt", alt.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            return snapshot;
        }

        [Fact]
        public void SpawnCoordinates_NullRecording_IsNone()
        {
            var lastPt = new TrajectoryPoint { bodyName = "Kerbin", latitude = 1, longitude = 2, altitude = 3 };
            Assert.Equal(VesselSpawner.SpawnCoordinateSource.None, VesselSpawner.SelectSpawnCoordinates(
                null, lastPt, out string body, out double lat, out double lon, out double alt,
                out bool haveEndpoint, out double endLat, out double endLon));
            Assert.Null(body);
            Assert.False(haveEndpoint);
            Assert.Equal(0, lat);
            Assert.Equal(0, endLat);
        }

        [Fact]
        public void SpawnCoordinates_EvaAndBreakupUseTheEndpointOrNothing()
        {
            var lastPt = new TrajectoryPoint { bodyName = "Mun", latitude = 10, longitude = 20, altitude = 30 };
            var eva = new Recording { EvaCrewName = "Jebediah Kerman", VesselSnapshot = SnapshotAt(1, 2, 3) };

            Assert.Equal(VesselSpawner.SpawnCoordinateSource.EvaEndpoint, VesselSpawner.SelectSpawnCoordinates(
                eva, lastPt, out string body, out double lat, out double lon, out double alt,
                out bool haveEndpoint, out double endLat, out double endLon));
            Assert.Equal("Mun", body);
            Assert.Equal((10.0, 20.0, 30.0), (lat, lon, alt));
            Assert.True(haveEndpoint);
            Assert.Equal((10.0, 20.0), (endLat, endLon));

            Assert.Equal(VesselSpawner.SpawnCoordinateSource.None, VesselSpawner.SelectSpawnCoordinates(
                eva, null, out body, out lat, out lon, out alt));
            Assert.Equal((0.0, 0.0, 0.0), (lat, lon, alt));

            // Breakup-continuous needs BOTH a child branch point and a terminal state.
            var breakup = new Recording
            {
                ChildBranchPointId = "bp-1",
                TerminalStateValue = TerminalState.Splashed,
                VesselSnapshot = SnapshotAt(1, 2, 3),
            };
            Assert.Equal(VesselSpawner.SpawnCoordinateSource.BreakupEndpoint, VesselSpawner.SelectSpawnCoordinates(
                breakup, lastPt, out body, out lat, out lon, out alt));
            Assert.Equal((10.0, 20.0, 30.0), (lat, lon, alt));

            var branchNoTerminal = new Recording { ChildBranchPointId = "bp-1", VesselSnapshot = SnapshotAt(1, 2, 3) };
            Assert.Equal(VesselSpawner.SpawnCoordinateSource.Snapshot, VesselSpawner.SelectSpawnCoordinates(
                branchNoTerminal, lastPt, out body, out lat, out lon, out alt));
        }

        [Fact]
        public void SpawnCoordinates_VesselPrefersTheSnapshotThenTheEndpoint()
        {
            var lastPt = new TrajectoryPoint { bodyName = "Kerbin", latitude = 10, longitude = 20, altitude = 30 };
            var rec = new Recording { VesselSnapshot = SnapshotAt(1, 2, 3) };

            Assert.Equal(VesselSpawner.SpawnCoordinateSource.Snapshot, VesselSpawner.SelectSpawnCoordinates(
                rec, lastPt, out string body, out double lat, out double lon, out double alt,
                out bool haveEndpoint, out double endLat, out double endLon));
            Assert.Equal((1.0, 2.0, 3.0), (lat, lon, alt));
            // The endpoint outputs still report the trajectory end, whichever source won.
            Assert.Equal("Kerbin", body);
            Assert.True(haveEndpoint);
            Assert.Equal((10.0, 20.0), (endLat, endLon));

            // A snapshot missing any of the three keys falls back to the endpoint.
            var partial = new ConfigNode("VESSEL");
            partial.AddValue("lat", "1");
            partial.AddValue("lon", "2");
            var noAlt = new Recording { VesselSnapshot = partial };
            Assert.Equal(VesselSpawner.SpawnCoordinateSource.EndpointNoSnapshotPosition, VesselSpawner.SelectSpawnCoordinates(
                noAlt, lastPt, out body, out lat, out lon, out alt));
            Assert.Equal((10.0, 20.0, 30.0), (lat, lon, alt));

            Assert.Equal(VesselSpawner.SpawnCoordinateSource.None, VesselSpawner.SelectSpawnCoordinates(
                new Recording(), null, out body, out lat, out lon, out alt));
            Assert.Null(body);
        }

        // Assert.Equal on two equal Vector3d values fails under this xunit (it prints both
        // as [1, 2, 3]), so compare the components.
        private static void AssertSameVector(Vector3d expected, Vector3d actual)
        {
            Assert.Equal(expected.x, actual.x);
            Assert.Equal(expected.y, actual.y);
            Assert.Equal(expected.z, actual.z);
        }

        [Fact]
        public void NoOverrideSpawnVelocity_NullBody_KeepsTheRecordedVector()
        {
            // Pins the null-body early return only: every call passes a null body (the null
            // literal, so no Unity object is compared), and the other inputs vary to show
            // none of them reaches past that return. The predicate's own arms (sample,
            // recording, orbit override, terminal) are covered by
            // RemainingVelocitySeedFrameTests through ShouldResolveNoOverrideSpawnVelocity.
            var recorded = new Vector3d(1, 2, 3);
            var sample = new TrajectoryPoint { bodyName = "Kerbin" };
            var orbiting = new Recording { TerminalStateValue = TerminalState.Orbiting };

            AssertSameVector(recorded, VesselSpawner.ResolveNoOverrideSpawnVelocity(
                orbiting, sample, recorded, null, 0, 0, 0, null, "test", "ctx"));
            AssertSameVector(recorded, VesselSpawner.ResolveNoOverrideSpawnVelocity(
                orbiting, null, recorded, null, 0, 0, 0, null, "test", "ctx"));
            AssertSameVector(recorded, VesselSpawner.ResolveNoOverrideSpawnVelocity(
                null, sample, recorded, null, 0, 0, 0, null, "test", "ctx"));
            AssertSameVector(recorded, VesselSpawner.ResolveNoOverrideSpawnVelocity(
                orbiting, sample, recorded, null, 0, 0, 0, new Orbit(), "test", "ctx"));
            Assert.DoesNotContain(logLines, l => l.Contains("Recorded spawn velocity"));
        }

        // ---- VesselSpawner.TerminalOrbit.cs ----

        [Fact]
        public void OrbitPeriod_UsesTheOrbitPeriodElseKeplerElseNaN()
        {
            var withPeriod = new Orbit { period = 3600.0, semiMajorAxis = 1.0e6 };
            Assert.Equal(3600.0, VesselSpawner.TryGetOrbitPeriod(withPeriod, null));

            CelestialBody body = TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12);
            var noPeriod = new Orbit { period = 0.0, semiMajorAxis = 1.0e6 };
            double expected = 2.0 * Math.PI * Math.Sqrt(1.0e18 / 3.5316e12);
            Assert.Equal(expected, VesselSpawner.TryGetOrbitPeriod(noPeriod, body), 6);

            // A non-finite period also falls through to Kepler.
            var nanPeriod = new Orbit { period = double.NaN, semiMajorAxis = 1.0e6 };
            Assert.Equal(expected, VesselSpawner.TryGetOrbitPeriod(nanPeriod, body), 6);

            Assert.True(double.IsNaN(VesselSpawner.TryGetOrbitPeriod(noPeriod, null)));
            Assert.True(double.IsNaN(VesselSpawner.TryGetOrbitPeriod(
                new Orbit { period = -1.0, semiMajorAxis = -5.0 }, body)));
            // A null orbit throws inside both reads; both are caught.
            Assert.True(double.IsNaN(VesselSpawner.TryGetOrbitPeriod(null, body)));
        }

        [Fact]
        public void OptionalDouble_RoundTripsFiniteValuesElseNa()
        {
            Assert.Equal("101325", VesselSpawner.FormatOptionalDouble(101325.0));
            Assert.Equal("0.1", VesselSpawner.FormatOptionalDouble(0.1));
            Assert.Equal("-2.5", VesselSpawner.FormatOptionalDouble(-2.5));
            Assert.Equal("n/a", VesselSpawner.FormatOptionalDouble(double.NaN));
            Assert.Equal("n/a", VesselSpawner.FormatOptionalDouble(double.PositiveInfinity));
            Assert.Equal("n/a", VesselSpawner.FormatOptionalDouble(double.NegativeInfinity));
        }

        // ---- VesselSpawner.Validation.cs ----

        [Fact]
        public void MaterializationContext_AppendsTheRecordingShape()
        {
            Assert.Equal("bad", VesselSpawner.AddRecordingSnapshotMaterializationContext("bad", null));
            Assert.Null(VesselSpawner.AddRecordingSnapshotMaterializationContext(null, null));

            var vessel = new ConfigNode("VESSEL");
            vessel.AddNode("PART");
            vessel.AddNode("PART");
            var ghost = new ConfigNode("VESSEL");
            ghost.AddNode("PART");
            var rec = new Recording
            {
                RecordingId = "rec-7",
                TerminalStateValue = TerminalState.Landed,
                VesselSnapshot = vessel,
                GhostVisualSnapshot = ghost,
            };
            Assert.Equal(
                "no parts (terminalState=Landed, vesselSnapshotParts=2, ghostSnapshotParts=1, recordingId=rec-7)",
                VesselSpawner.AddRecordingSnapshotMaterializationContext("no parts", rec));

            Assert.Equal(
                "(unknown) (terminalState=(unset), vesselSnapshotParts=0, ghostSnapshotParts=0, recordingId=<none>)",
                VesselSpawner.AddRecordingSnapshotMaterializationContext(null, new Recording { RecordingId = "" }));
        }

        [Fact]
        public void ValidationContext_NamesTheContextAndTheVessel()
        {
            Assert.Equal("(unknown)", VesselSpawner.DescribeSpawnValidationContext(null, null));
            Assert.Equal("(unknown)", VesselSpawner.DescribeSpawnValidationContext(new Recording(), ""));
            Assert.Equal("Rover", VesselSpawner.DescribeSpawnValidationContext(
                new Recording { VesselName = "Rover" }, null));
            Assert.Equal("chain tip", VesselSpawner.DescribeSpawnValidationContext(null, "chain tip"));
            Assert.Equal("Rover", VesselSpawner.DescribeSpawnValidationContext(
                new Recording { VesselName = "Rover" }, "Rover"));
            Assert.Equal("chain tip (Rover)", VesselSpawner.DescribeSpawnValidationContext(
                new Recording { VesselName = "Rover" }, "chain tip"));
            // The equality is ordinal: a case difference still appends the name.
            Assert.Equal("rover (Rover)", VesselSpawner.DescribeSpawnValidationContext(
                new Recording { VesselName = "Rover" }, "rover"));
        }

        [Fact]
        public void MaterializationRejected_LogsOneWarnWithContextAndReason()
        {
            VesselSpawner.LogMaterializationSnapshotRejected("chain tip (Rover)", "no PART nodes");
            Assert.Contains(logLines, l => l.Contains("[WARN][Spawner]")
                && l.Contains("Spawn validation rejected materialization for chain tip (Rover): no PART nodes")
                && l.Contains("(#620)"));
        }

        // ---- VesselSpawner.SourceIdentity.cs / SpawnRotation.cs / KscRetirement.cs ----

        [Fact]
        public void ExistingSourceDuplicateLogKey_CarriesTheBranchAndEveryPid()
        {
            Assert.Equal("existing-source-dup|current-flight|11|22|33",
                VesselSpawner.BuildExistingSourceDuplicateLogKey("current-flight", 11u, 22u, 33u));
            Assert.Equal("existing-source-dup||0|0|4294967295",
                VesselSpawner.BuildExistingSourceDuplicateLogKey(null, 0u, 0u, uint.MaxValue));
        }

        [Fact]
        public void SurfaceTerminal_IsLandedOrSplashedOnly()
        {
            Assert.True(VesselSpawner.IsSurfaceTerminal(TerminalState.Landed));
            Assert.True(VesselSpawner.IsSurfaceTerminal(TerminalState.Splashed));
            Assert.False(VesselSpawner.IsSurfaceTerminal(null));
            Assert.False(VesselSpawner.IsSurfaceTerminal(TerminalState.Orbiting));
            Assert.False(VesselSpawner.IsSurfaceTerminal(TerminalState.SubOrbital));
            Assert.False(VesselSpawner.IsSurfaceTerminal(TerminalState.Destroyed));
        }

        [Fact]
        public void RetirementTerminal_StampedElseTheSnapshotSituationElseNull()
        {
            Assert.Null(VesselSpawner.ResolveEffectiveTerminalForRetirement(null));
            Assert.Null(VesselSpawner.ResolveEffectiveTerminalForRetirement(new Recording()));

            var landedSnapshot = new ConfigNode("VESSEL");
            landedSnapshot.AddValue("sit", "PRELAUNCH");
            // The stamped value wins over the snapshot.
            Assert.Equal(TerminalState.Orbiting, VesselSpawner.ResolveEffectiveTerminalForRetirement(
                new Recording { TerminalStateValue = TerminalState.Orbiting, VesselSnapshot = landedSnapshot }));
            // Un-finalized: PRELAUNCH maps to Landed.
            Assert.Equal(TerminalState.Landed, VesselSpawner.ResolveEffectiveTerminalForRetirement(
                new Recording { VesselSnapshot = landedSnapshot }));

            var noSit = new ConfigNode("VESSEL");
            Assert.Null(VesselSpawner.ResolveEffectiveTerminalForRetirement(new Recording { VesselSnapshot = noSit }));
            var unknownSit = new ConfigNode("VESSEL");
            unknownSit.AddValue("sit", "NOT_A_SITUATION");
            Assert.Null(VesselSpawner.ResolveEffectiveTerminalForRetirement(new Recording { VesselSnapshot = unknownSit }));
        }

        [Fact]
        public void SpawnRotationBody_NoBodyNameAnywhere_IsNullWithoutABodyLookup()
        {
            Assert.Null(VesselSpawner.ResolveSpawnRotationBody(null));
            Assert.Null(VesselSpawner.ResolveSpawnRotationBody(new Recording(), new TrajectoryPoint { bodyName = "" }));
        }
    }
}
