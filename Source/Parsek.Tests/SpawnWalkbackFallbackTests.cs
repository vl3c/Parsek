using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the EvaSpawnWalkbackOnOverlap fallback fix (2026-07-10 sweep):
    /// 1. Deterministic landed reseed: an exactly-zero recorded velocity on a landed
    ///    endpoint reseeds to a NaN-SMA orbit; SpawnAtPosition now substitutes the
    ///    canonical surface orbit instead of flakily rejecting the spawn (#620).
    /// 2. Deliberate position-override stamp: the degraded-fallback snapshot repair
    ///    must not clobber a walkback-corrected position back to the recorded endpoint.
    /// </summary>
    [Collection("Sequential")]
    public class SpawnWalkbackFallbackTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly object originalFlightGlobalsBodies;
        private readonly VesselSpawner.ResolveBodyNameByIndexDelegate originalBodyNameResolver;
        private readonly VesselSpawner.ResolveBodyByNameDelegate originalBodyResolver;
        private readonly VesselSpawner.ResolveBodyIndexDelegate originalBodyIndexResolver;

        public SpawnWalkbackFallbackTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            originalBodyNameResolver = VesselSpawner.BodyNameResolverForTesting;
            originalBodyResolver = VesselSpawner.BodyResolverForTesting;
            originalBodyIndexResolver = VesselSpawner.BodyIndexResolverForTesting;
            originalFlightGlobalsBodies = typeof(FlightGlobals)
                .GetField("bodies", BindingFlags.Static | BindingFlags.NonPublic)
                ?.GetValue(null);
        }

        public void Dispose()
        {
            VesselSpawner.BodyNameResolverForTesting = originalBodyNameResolver;
            VesselSpawner.BodyResolverForTesting = originalBodyResolver;
            VesselSpawner.BodyIndexResolverForTesting = originalBodyIndexResolver;
            typeof(FlightGlobals)
                .GetField("bodies", BindingFlags.Static | BindingFlags.NonPublic)
                ?.SetValue(null, originalFlightGlobalsBodies);
            TestBodyRegistry.Reset();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        private void InstallBodies()
        {
            TestBodyRegistry.Install(("Kerbin", 600000.0, 3.5316e12), ("Mun", 200000.0, 6.5138398e10));
            VesselSpawner.BodyNameResolverForTesting = TestBodyRegistry.ResolveBodyNameByIndex;
            VesselSpawner.BodyResolverForTesting = TestBodyRegistry.ResolveBodyByName;
            VesselSpawner.BodyIndexResolverForTesting = TestBodyRegistry.ResolveBodyIndex;
        }

        private static ConfigNode BuildLandedSnapshotWithStaleOrbit(
            string lat, string lon, string alt, string refIndex)
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "LANDED");
            snapshot.AddValue("lat", lat);
            snapshot.AddValue("lon", lon);
            snapshot.AddValue("alt", alt);
            snapshot.AddNode("PART").AddValue("name", "kerbalEVA");
            var orbitNode = new ConfigNode("ORBIT");
            orbitNode.AddValue("SMA", "700000");
            orbitNode.AddValue("ECC", "0.01");
            orbitNode.AddValue("INC", "0.0");
            orbitNode.AddValue("LPE", "0.0");
            orbitNode.AddValue("LAN", "0.0");
            orbitNode.AddValue("MNA", "0.0");
            orbitNode.AddValue("EPH", "100.0");
            orbitNode.AddValue("REF", refIndex);
            snapshot.AddNode(orbitNode);
            return snapshot;
        }

        #region HasNonFiniteOrbitElement (Half 1 predicate)

        [Fact]
        public void HasNonFiniteOrbitElement_NaNSma_ReturnsTrueNamingSma()
        {
            bool result = OrbitReseed.HasNonFiniteOrbitElement(
                double.NaN, 1.0, 0.0, 0.0, 0.0, 0.0, 100.0,
                out string offenders);

            Assert.True(result);
            Assert.Equal("SMA", offenders);
        }

        [Fact]
        public void HasNonFiniteOrbitElement_NaNEcc_ReturnsTrueNamingEcc()
        {
            bool result = OrbitReseed.HasNonFiniteOrbitElement(
                700000.0, double.NaN, 0.0, 0.0, 0.0, 0.0, 100.0,
                out string offenders);

            Assert.True(result);
            Assert.Equal("ECC", offenders);
        }

        [Fact]
        public void HasNonFiniteOrbitElement_PositiveInfinityEpoch_ReturnsTrueNamingEph()
        {
            bool result = OrbitReseed.HasNonFiniteOrbitElement(
                700000.0, 0.01, 0.0, 0.0, 0.0, 0.0, double.PositiveInfinity,
                out string offenders);

            Assert.True(result);
            Assert.Equal("EPH", offenders);
        }

        [Fact]
        public void HasNonFiniteOrbitElement_NegativeInfinityInclination_ReturnsTrueNamingInc()
        {
            bool result = OrbitReseed.HasNonFiniteOrbitElement(
                700000.0, 0.01, double.NegativeInfinity, 0.0, 0.0, 0.0, 100.0,
                out string offenders);

            Assert.True(result);
            Assert.Equal("INC", offenders);
        }

        [Fact]
        public void HasNonFiniteOrbitElement_MultipleOffenders_ListsAllInElementOrder()
        {
            bool result = OrbitReseed.HasNonFiniteOrbitElement(
                double.NaN, double.NaN, 0.0, 0.0, double.PositiveInfinity, 0.0, 100.0,
                out string offenders);

            Assert.True(result);
            Assert.Equal("SMA,ECC,LAN", offenders);
        }

        [Fact]
        public void HasNonFiniteOrbitElement_AllFinite_ReturnsFalseWithNullOffenders()
        {
            bool result = OrbitReseed.HasNonFiniteOrbitElement(
                700000.0, 0.01, 45.0, 12.0, 33.0, 0.7, 100.0,
                out string offenders);

            Assert.False(result);
            Assert.Null(offenders);
        }

        [Fact]
        public void HasNonFiniteOrbitElement_NullOrbit_ReturnsTrue()
        {
            bool result = OrbitReseed.HasNonFiniteOrbitElement((Orbit)null, out string offenders);

            Assert.True(result);
            Assert.Equal("(null orbit)", offenders);
        }

        #endregion

        #region IsLandedLikeSituation / WriteCanonicalSurfaceOrbitValues (Half 1 helpers)

        [Theory]
        [InlineData("LANDED", true)]
        [InlineData("SPLASHED", true)]
        [InlineData("landed", true)]
        [InlineData("splashed", true)]
        [InlineData("ORBITING", false)]
        [InlineData("FLYING", false)]
        [InlineData("SUB_ORBITAL", false)]
        [InlineData("PRELAUNCH", false)]
        [InlineData("DOCKED", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsLandedLikeSituation_MatchesOnlyLandedAndSplashed(string sit, bool expected)
        {
            Assert.Equal(expected, VesselSpawner.IsLandedLikeSituation(sit));
        }

        [Fact]
        public void WriteCanonicalSurfaceOrbitValues_WritesSurfaceTupleWithBodyIndex()
        {
            var orbitNode = new ConfigNode("ORBIT");

            VesselSpawner.WriteCanonicalSurfaceOrbitValues(orbitNode, 3);

            Assert.Equal("0", orbitNode.GetValue("SMA"));
            Assert.Equal("1", orbitNode.GetValue("ECC"));
            Assert.Equal("0", orbitNode.GetValue("INC"));
            Assert.Equal("0", orbitNode.GetValue("LPE"));
            Assert.Equal("0", orbitNode.GetValue("LAN"));
            Assert.Equal("0", orbitNode.GetValue("MNA"));
            Assert.Equal("0", orbitNode.GetValue("EPH"));
            Assert.Equal("3", orbitNode.GetValue("REF"));
        }

        [Fact]
        public void WriteCanonicalSurfaceOrbitValues_NullNode_DoesNotThrow()
        {
            VesselSpawner.WriteCanonicalSurfaceOrbitValues(null, 0);
        }

        [Fact]
        public void WriteCanonicalSurfaceOrbitValues_MatchesApplySurfaceOrbitToSnapshotShape()
        {
            InstallBodies();
            var snapshot = BuildLandedSnapshotWithStaleOrbit("1.0", "2.0", "3.0", "0");
            Assert.True(VesselSpawner.TryResolveBodyByName("Kerbin", out CelestialBody kerbin));

            Assert.True(VesselSpawner.ApplySurfaceOrbitToSnapshot(snapshot, kerbin, "shape-test"));

            var direct = new ConfigNode("ORBIT");
            VesselSpawner.WriteCanonicalSurfaceOrbitValues(direct, 0);
            ConfigNode applied = snapshot.GetNode("ORBIT");
            foreach (string key in new[] { "SMA", "ECC", "INC", "LPE", "LAN", "MNA", "EPH", "REF" })
                Assert.Equal(direct.GetValue(key), applied.GetValue(key));
        }

        #endregion

        #region Deliberate position-override stamp (Half 2 marker)

        [Fact]
        public void OverrideSnapshotPosition_StampsDeliberateOverrideMarkerAndLogs()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("lat", "0.0");
            snapshot.AddValue("lon", "0.0");
            snapshot.AddValue("alt", "10.0");

            VesselSpawner.OverrideSnapshotPosition(snapshot, 1.5, 2.5, 73.5, 0, "Stamp Test");

            Assert.Equal("True", snapshot.GetValue(VesselSpawner.DeliberatePositionOverrideKey));
            Assert.True(VesselSpawner.HasDeliberatePositionOverrideStamp(snapshot));
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]")
                && l.Contains("Snapshot position override")
                && l.Contains("Stamp Test")
                && l.Contains("[deliberate-override stamped]"));
        }

        [Fact]
        public void HasDeliberatePositionOverrideStamp_AbsentKey_ReturnsFalse()
        {
            var snapshot = new ConfigNode("VESSEL");

            Assert.False(VesselSpawner.HasDeliberatePositionOverrideStamp(snapshot));
        }

        [Fact]
        public void HasDeliberatePositionOverrideStamp_NullSnapshot_ReturnsFalse()
        {
            Assert.False(VesselSpawner.HasDeliberatePositionOverrideStamp(null));
        }

        [Theory]
        [InlineData("True", true)]
        [InlineData("true", true)]
        [InlineData("False", false)]
        [InlineData("garbage", false)]
        public void HasDeliberatePositionOverrideStamp_ParsesValue(string value, bool expected)
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue(VesselSpawner.DeliberatePositionOverrideKey, value);

            Assert.Equal(expected, VesselSpawner.HasDeliberatePositionOverrideStamp(snapshot));
        }

        [Fact]
        public void StripDeliberatePositionOverrideStamp_RemovesKeyAndLogs()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue(VesselSpawner.DeliberatePositionOverrideKey, "True");

            bool stripped = VesselSpawner.StripDeliberatePositionOverrideStamp(snapshot, "strip-test");

            Assert.True(stripped);
            Assert.False(snapshot.HasValue(VesselSpawner.DeliberatePositionOverrideKey));
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]")
                && l.Contains("Stripped deliberate position-override stamp")
                && l.Contains("strip-test"));
        }

        [Fact]
        public void StripDeliberatePositionOverrideStamp_AbsentKey_ReturnsFalse()
        {
            var snapshot = new ConfigNode("VESSEL");

            Assert.False(VesselSpawner.StripDeliberatePositionOverrideStamp(snapshot, "strip-test"));
        }

        // --- Review follow-up (PR #1281 SHOULD-FIX): the stamp is a within-spawn-attempt
        // marker on the DURABLE rec.VesselSnapshot. It must be stripped once the spawn
        // attempt resolves so a dirty-recording sidecar save (<id>_vessel.craft) can never
        // carry it into a later session, where spawn paths that do not re-run the resolved
        // overrides first (KSC end spawn, ghost tip respawn) would mistake stale snapshot
        // coordinates for a fresh deliberate override.

        [Fact]
        public void StripFromRecording_StampedDurableSnapshot_StripsAndReturnsTrue()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue(VesselSpawner.DeliberatePositionOverrideKey, "True");
            var rec = new Recording { VesselSnapshot = snapshot };

            bool stripped = VesselSpawner.StripDeliberatePositionOverrideStampFromRecording(
                rec, "resolve-test");

            Assert.True(stripped);
            Assert.False(rec.VesselSnapshot.HasValue(VesselSpawner.DeliberatePositionOverrideKey));
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]")
                && l.Contains("Stripped deliberate position-override stamp")
                && l.Contains("resolve-test")
                && l.Contains("durable snapshot (spawn resolved)"));
        }

        [Fact]
        public void StripFromRecording_NullRecordingOrSnapshot_Tolerant()
        {
            Assert.False(VesselSpawner.StripDeliberatePositionOverrideStampFromRecording(
                null, "resolve-test"));
            Assert.False(VesselSpawner.StripDeliberatePositionOverrideStampFromRecording(
                new Recording { VesselSnapshot = null }, "resolve-test"));
        }

        [Fact]
        public void StripFromRecording_UnstampedDurableSnapshot_ReturnsFalse()
        {
            var rec = new Recording { VesselSnapshot = new ConfigNode("VESSEL") };

            Assert.False(VesselSpawner.StripDeliberatePositionOverrideStampFromRecording(
                rec, "resolve-test"));
        }

        [Fact]
        public void StripFromRecording_IsWiredAsResolutionFinallyInSpawnOrRecover()
        {
            // Source-text wiring gate (the codebase's wiring-test pattern): the
            // durable-snapshot strip must run in a finally that covers every resolution
            // exit of the spawn attempt, or a dirty sidecar save can persist the stamp.
            string source = File.ReadAllText(FindSourceFile("VesselSpawner.cs"));
            int callIdx = source.IndexOf(
                "StripDeliberatePositionOverrideStampFromRecording(rec, logContext);",
                StringComparison.Ordinal);
            Assert.True(callIdx > 0,
                "the spawn-resolution path must strip the durable-snapshot stamp");
            string preceding = source.Substring(Math.Max(0, callIdx - 1200), 1200);
            Assert.Contains("finally", preceding);
        }

        private static string FindSourceFile(string fileName)
        {
            // xUnit runs from Source/Parsek.Tests/bin/Debug/net472/ (5 segments to repo root).
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(root, "Source", "Parsek", fileName);
            Assert.True(File.Exists(path), $"source file not found: {path}");
            return path;
        }

        #endregion

        #region DecideSurfaceRepairCoordinateSource (Half 2 decision, exhaustive)

        // Exhaustive truth table over (hasStamp, hasSnapshotPos, hasSnapshotBody,
        // hasEndpointCoords, bodyMismatch in {null, false, true}) = 48 rows.
        // Contract:
        //   1. stamp && pos && mismatch != true            -> StampedSnapshot
        //   2. else endpointCoords                          -> Endpoint (pre-stamp behavior)
        //   3. else pos && body && mismatch != true         -> Snapshot (pre-stamp behavior)
        //   4. else                                         -> Reject
        [Theory]
        // stamp=F pos=F body=F ep=F
        [InlineData(false, false, false, false, null, VesselSpawner.SurfaceRepairCoordinateSource.Reject)]
        [InlineData(false, false, false, false, false, VesselSpawner.SurfaceRepairCoordinateSource.Reject)]
        [InlineData(false, false, false, false, true, VesselSpawner.SurfaceRepairCoordinateSource.Reject)]
        // stamp=F pos=F body=F ep=T
        [InlineData(false, false, false, true, null, VesselSpawner.SurfaceRepairCoordinateSource.Endpoint)]
        [InlineData(false, false, false, true, false, VesselSpawner.SurfaceRepairCoordinateSource.Endpoint)]
        [InlineData(false, false, false, true, true, VesselSpawner.SurfaceRepairCoordinateSource.Endpoint)]
        // stamp=F pos=F body=T ep=F
        [InlineData(false, false, true, false, null, VesselSpawner.SurfaceRepairCoordinateSource.Reject)]
        [InlineData(false, false, true, false, false, VesselSpawner.SurfaceRepairCoordinateSource.Reject)]
        [InlineData(false, false, true, false, true, VesselSpawner.SurfaceRepairCoordinateSource.Reject)]
        // stamp=F pos=F body=T ep=T
        [InlineData(false, false, true, true, null, VesselSpawner.SurfaceRepairCoordinateSource.Endpoint)]
        [InlineData(false, false, true, true, false, VesselSpawner.SurfaceRepairCoordinateSource.Endpoint)]
        [InlineData(false, false, true, true, true, VesselSpawner.SurfaceRepairCoordinateSource.Endpoint)]
        // stamp=F pos=T body=F ep=F
        [InlineData(false, true, false, false, null, VesselSpawner.SurfaceRepairCoordinateSource.Reject)]
        [InlineData(false, true, false, false, false, VesselSpawner.SurfaceRepairCoordinateSource.Reject)]
        [InlineData(false, true, false, false, true, VesselSpawner.SurfaceRepairCoordinateSource.Reject)]
        // stamp=F pos=T body=F ep=T
        [InlineData(false, true, false, true, null, VesselSpawner.SurfaceRepairCoordinateSource.Endpoint)]
        [InlineData(false, true, false, true, false, VesselSpawner.SurfaceRepairCoordinateSource.Endpoint)]
        [InlineData(false, true, false, true, true, VesselSpawner.SurfaceRepairCoordinateSource.Endpoint)]
        // stamp=F pos=T body=T ep=F
        [InlineData(false, true, true, false, null, VesselSpawner.SurfaceRepairCoordinateSource.Snapshot)]
        [InlineData(false, true, true, false, false, VesselSpawner.SurfaceRepairCoordinateSource.Snapshot)]
        [InlineData(false, true, true, false, true, VesselSpawner.SurfaceRepairCoordinateSource.Reject)]
        // stamp=F pos=T body=T ep=T
        [InlineData(false, true, true, true, null, VesselSpawner.SurfaceRepairCoordinateSource.Endpoint)]
        [InlineData(false, true, true, true, false, VesselSpawner.SurfaceRepairCoordinateSource.Endpoint)]
        [InlineData(false, true, true, true, true, VesselSpawner.SurfaceRepairCoordinateSource.Endpoint)]
        // stamp=T pos=F body=F ep=F
        [InlineData(true, false, false, false, null, VesselSpawner.SurfaceRepairCoordinateSource.Reject)]
        [InlineData(true, false, false, false, false, VesselSpawner.SurfaceRepairCoordinateSource.Reject)]
        [InlineData(true, false, false, false, true, VesselSpawner.SurfaceRepairCoordinateSource.Reject)]
        // stamp=T pos=F body=F ep=T (stamp never blocks a genuine endpoint repair)
        [InlineData(true, false, false, true, null, VesselSpawner.SurfaceRepairCoordinateSource.Endpoint)]
        [InlineData(true, false, false, true, false, VesselSpawner.SurfaceRepairCoordinateSource.Endpoint)]
        [InlineData(true, false, false, true, true, VesselSpawner.SurfaceRepairCoordinateSource.Endpoint)]
        // stamp=T pos=F body=T ep=F
        [InlineData(true, false, true, false, null, VesselSpawner.SurfaceRepairCoordinateSource.Reject)]
        [InlineData(true, false, true, false, false, VesselSpawner.SurfaceRepairCoordinateSource.Reject)]
        [InlineData(true, false, true, false, true, VesselSpawner.SurfaceRepairCoordinateSource.Reject)]
        // stamp=T pos=F body=T ep=T
        [InlineData(true, false, true, true, null, VesselSpawner.SurfaceRepairCoordinateSource.Endpoint)]
        [InlineData(true, false, true, true, false, VesselSpawner.SurfaceRepairCoordinateSource.Endpoint)]
        [InlineData(true, false, true, true, true, VesselSpawner.SurfaceRepairCoordinateSource.Endpoint)]
        // stamp=T pos=T body=F ep=F
        [InlineData(true, true, false, false, null, VesselSpawner.SurfaceRepairCoordinateSource.StampedSnapshot)]
        [InlineData(true, true, false, false, false, VesselSpawner.SurfaceRepairCoordinateSource.StampedSnapshot)]
        [InlineData(true, true, false, false, true, VesselSpawner.SurfaceRepairCoordinateSource.Reject)]
        // stamp=T pos=T body=F ep=T
        [InlineData(true, true, false, true, null, VesselSpawner.SurfaceRepairCoordinateSource.StampedSnapshot)]
        [InlineData(true, true, false, true, false, VesselSpawner.SurfaceRepairCoordinateSource.StampedSnapshot)]
        [InlineData(true, true, false, true, true, VesselSpawner.SurfaceRepairCoordinateSource.Endpoint)]
        // stamp=T pos=T body=T ep=F
        [InlineData(true, true, true, false, null, VesselSpawner.SurfaceRepairCoordinateSource.StampedSnapshot)]
        [InlineData(true, true, true, false, false, VesselSpawner.SurfaceRepairCoordinateSource.StampedSnapshot)]
        [InlineData(true, true, true, false, true, VesselSpawner.SurfaceRepairCoordinateSource.Reject)]
        // stamp=T pos=T body=T ep=T
        [InlineData(true, true, true, true, null, VesselSpawner.SurfaceRepairCoordinateSource.StampedSnapshot)]
        [InlineData(true, true, true, true, false, VesselSpawner.SurfaceRepairCoordinateSource.StampedSnapshot)]
        [InlineData(true, true, true, true, true, VesselSpawner.SurfaceRepairCoordinateSource.Endpoint)]
        public void DecideSurfaceRepairCoordinateSource_TruthTable(
            bool hasStamp,
            bool hasSnapshotPos,
            bool hasSnapshotBody,
            bool hasEndpointCoords,
            bool? bodyMismatch,
            VesselSpawner.SurfaceRepairCoordinateSource expected)
        {
            Assert.Equal(expected, VesselSpawner.DecideSurfaceRepairCoordinateSource(
                hasStamp, hasSnapshotPos, hasSnapshotBody, hasEndpointCoords, bodyMismatch));
        }

        #endregion

        #region Repair precedence through BuildValidatedRespawnSnapshot (Half 2 integration)

        [Fact]
        public void BuildValidatedRespawnSnapshot_StampedSnapshotWithValidCoords_PreservesWalkbackCoords()
        {
            InstallBodies();

            // Walkback-corrected snapshot: coords differ from the recording endpoint,
            // stale non-surface orbit forces the landed-like repair to run.
            var snapshot = BuildLandedSnapshotWithStaleOrbit(
                "-0.098282", "-74.557675", "73.5", "0");
            snapshot.AddValue(VesselSpawner.DeliberatePositionOverrideKey, "True");

            var rec = new Recording
            {
                VesselName = "Walkback Keeper",
                VesselSnapshot = snapshot,
                TerminalStateValue = TerminalState.Landed,
                EndpointPhase = RecordingEndpointPhase.TerminalPosition,
                EndpointBodyName = "Kerbin",
                TerminalPosition = new SurfacePosition
                {
                    body = "Kerbin",
                    latitude = -0.0983,
                    longitude = -74.5570,
                    altitude = 66.8
                }
            };

            ConfigNode validated = VesselSpawner.BuildValidatedRespawnSnapshot(
                rec,
                currentUT: 123.0,
                logContext: "spawn-test");

            Assert.NotNull(validated);
            // Deliberate walkback coordinates survive; the endpoint did NOT clobber them.
            Assert.Equal("-0.098282", validated.GetValue("lat"));
            Assert.Equal("-74.557675", validated.GetValue("lon"));
            Assert.Equal("73.5", validated.GetValue("alt"));
            // The orbit is still repaired to the canonical surface tuple.
            ConfigNode repairedOrbit = validated.GetNode("ORBIT");
            Assert.NotNull(repairedOrbit);
            Assert.Equal("0", repairedOrbit.GetValue("SMA"));
            Assert.Equal("1", repairedOrbit.GetValue("ECC"));
            Assert.Equal("0", repairedOrbit.GetValue("REF"));
            // The stamp never rides the validated copy toward a ProtoVessel load.
            Assert.False(validated.HasValue(VesselSpawner.DeliberatePositionOverrideKey));
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]")
                && l.Contains("using stamped-override surface coordinates")
                && l.Contains("Walkback Keeper"));
        }

        [Fact]
        public void BuildValidatedRespawnSnapshot_UnstampedSnapshotWithEndpointCoords_EndpointStillWins()
        {
            InstallBodies();

            // Same shape as the stamped test but WITHOUT the stamp: the historical
            // endpoint-first contract must be preserved exactly (stale EVA-start
            // snapshots are moved to the trajectory endpoint).
            var snapshot = BuildLandedSnapshotWithStaleOrbit(
                "-0.098282", "-74.557675", "73.5", "0");

            var rec = new Recording
            {
                VesselName = "Endpoint First",
                VesselSnapshot = snapshot,
                TerminalStateValue = TerminalState.Landed,
                EndpointPhase = RecordingEndpointPhase.TerminalPosition,
                EndpointBodyName = "Kerbin",
                TerminalPosition = new SurfacePosition
                {
                    body = "Kerbin",
                    latitude = 4.0,
                    longitude = 5.0,
                    altitude = 6.0
                }
            };

            ConfigNode validated = VesselSpawner.BuildValidatedRespawnSnapshot(
                rec,
                currentUT: 123.0,
                logContext: "spawn-test");

            Assert.NotNull(validated);
            Assert.Equal("4", validated.GetValue("lat"));
            Assert.Equal("5", validated.GetValue("lon"));
            Assert.Equal("6", validated.GetValue("alt"));
            Assert.False(validated.HasValue(VesselSpawner.DeliberatePositionOverrideKey));
            Assert.Contains(logLines, l =>
                l.Contains("using endpoint surface coordinates")
                && l.Contains("Endpoint First"));
        }

        [Fact]
        public void BuildValidatedRespawnSnapshot_StampedButNonFiniteCoords_EndpointRepairStillApplies()
        {
            InstallBodies();

            // Stamped snapshot whose position is unusable (NaN lat): the stamp must
            // never block a genuine repair: endpoint coordinates still apply.
            var snapshot = BuildLandedSnapshotWithStaleOrbit(
                "NaN", "-74.557675", "73.5", "0");
            snapshot.AddValue(VesselSpawner.DeliberatePositionOverrideKey, "True");

            var rec = new Recording
            {
                VesselName = "Stamp No Blocker",
                VesselSnapshot = snapshot,
                TerminalStateValue = TerminalState.Landed,
                EndpointPhase = RecordingEndpointPhase.TerminalPosition,
                EndpointBodyName = "Kerbin",
                TerminalPosition = new SurfacePosition
                {
                    body = "Kerbin",
                    latitude = 4.0,
                    longitude = 5.0,
                    altitude = 6.0
                }
            };

            ConfigNode validated = VesselSpawner.BuildValidatedRespawnSnapshot(
                rec,
                currentUT: 123.0,
                logContext: "spawn-test");

            Assert.NotNull(validated);
            Assert.Equal("4", validated.GetValue("lat"));
            Assert.Equal("5", validated.GetValue("lon"));
            Assert.Equal("6", validated.GetValue("alt"));
            Assert.False(validated.HasValue(VesselSpawner.DeliberatePositionOverrideKey));
            Assert.Contains(logLines, l =>
                l.Contains("using endpoint surface coordinates")
                && l.Contains("Stamp No Blocker"));
        }

        [Fact]
        public void BuildValidatedRespawnSnapshot_StampedWithBodyMismatch_EndpointRepairStillApplies()
        {
            InstallBodies();

            // Stamped snapshot pointing at Kerbin (REF=0) while the recording endpoint
            // is on Mun: the body mismatch invalidates the stamped coordinates and the
            // endpoint repair applies (a stamp never keeps coordinates on the wrong body).
            var snapshot = BuildLandedSnapshotWithStaleOrbit(
                "-0.098282", "-74.557675", "73.5", "0");
            snapshot.AddValue(VesselSpawner.DeliberatePositionOverrideKey, "True");

            var rec = new Recording
            {
                VesselName = "Wrong Body Stamp",
                VesselSnapshot = snapshot,
                TerminalStateValue = TerminalState.Landed,
                EndpointPhase = RecordingEndpointPhase.TerminalPosition,
                EndpointBodyName = "Mun",
                TerminalPosition = new SurfacePosition
                {
                    body = "Mun",
                    latitude = 4.0,
                    longitude = 5.0,
                    altitude = 6.0
                }
            };

            ConfigNode validated = VesselSpawner.BuildValidatedRespawnSnapshot(
                rec,
                currentUT: 123.0,
                logContext: "spawn-test");

            Assert.NotNull(validated);
            Assert.Equal("4", validated.GetValue("lat"));
            Assert.Equal("5", validated.GetValue("lon"));
            Assert.Equal("6", validated.GetValue("alt"));
            ConfigNode repairedOrbit = validated.GetNode("ORBIT");
            Assert.NotNull(repairedOrbit);
            Assert.Equal("1", repairedOrbit.GetValue("REF"));
            Assert.Contains(logLines, l =>
                l.Contains("using endpoint surface coordinates")
                && l.Contains("Wrong Body Stamp"));
        }

        #endregion
    }
}
