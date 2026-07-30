using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Parsek.Tests.Generators;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins VesselSpawner's spawn-rotation family against the project's two-rotation
    /// contract (`.claude/CLAUDE.md`, "Rotation / world frame"):
    ///
    ///   capture   : srfRelRotation = Inverse(body.bodyTransform.rotation) * v.transform.rotation
    ///   live place: worldRot       = body.bodyTransform.rotation * srfRelRotation
    ///   ProtoVessel: VESSEL.rot IS ProtoVessel.rotation, and ProtoVessel.Load() assigns it
    ///                straight to vesselRef.srfRelRotation - so a Parsek-authored spawn node
    ///                must carry the RAW recorded surface-relative value, never the
    ///                body-composed world rotation.
    ///
    /// Mixing the two conventions is the historical bug class here, and it is INVISIBLE under
    /// an identity body rotation: every fixture below therefore uses a deliberately
    /// non-identity body rotation so a body multiply that leaked into the ProtoVessel path
    /// moves the written value by tens of degrees.
    ///
    /// Headless notes: Quaternion.Euler / Quaternion.Inverse are Unity ECalls and throw a
    /// SecurityException outside the game, so every rotation here is a literal unit
    /// quaternion and the inverse is taken as the conjugate. KSPUtil.WriteQuaternion formats
    /// with the ambient culture, so the class pins InvariantCulture for the duration of each
    /// test (KSP itself runs invariant; a comma-decimal machine locale would otherwise split
    /// one component into two tokens).
    /// </summary>
    [Collection("Sequential")]
    public class SpawnRotationContractTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly CultureInfo originalCulture;
        private readonly VesselSpawner.ResolveBodyByNameDelegate originalBodyResolver;
        private readonly bool originalSuppressLogging;

        // Yaw +90 deg about world Y. Stands in for "the body has rotated under the vessel".
        private static readonly Quaternion BodyRotation =
            new Quaternion(0f, 0.70710678f, 0f, 0.70710678f);

        // An arbitrary recorded surface-relative attitude, normalized by construction
        // (0.3^2 + 0.1^2 + 0.2^2 + 0.9273618^2 == 1).
        private static readonly Quaternion RecordedSurfaceRelative =
            new Quaternion(0.3f, 0.1f, 0.2f, 0.9273618f);

        public SpawnRotationContractTests()
        {
            originalCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            originalBodyResolver = VesselSpawner.BodyResolverForTesting;
            originalSuppressLogging = RecordingStore.SuppressLogging;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            VesselSpawner.BodyResolverForTesting = originalBodyResolver;
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = originalSuppressLogging;
            Thread.CurrentThread.CurrentCulture = originalCulture;
        }

        #region quaternion helpers (no Unity ECalls)

        /// <summary>Unity's Quaternion multiplication, reimplemented so no ECall is needed.</summary>
        private static Quaternion Mul(Quaternion a, Quaternion b)
        {
            return new Quaternion(
                a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
                a.w * b.y + a.y * b.w + a.z * b.x - a.x * b.z,
                a.w * b.z + a.z * b.w + a.x * b.y - a.y * b.x,
                a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);
        }

        /// <summary>Inverse of a unit quaternion (Quaternion.Inverse is an ECall).</summary>
        private static Quaternion Conjugate(Quaternion q)
        {
            return new Quaternion(-q.x, -q.y, -q.z, q.w);
        }

        private static void AssertSameRotation(Quaternion expected, Quaternion actual, string what)
        {
            float angle = Quaternion.Angle(expected, actual);
            Assert.True(angle < 0.01f,
                what + ": expected " + Fmt(expected) + " but got " + Fmt(actual) +
                " (off by " + angle.ToString("F4", CultureInfo.InvariantCulture) + " deg)");
        }

        private static void AssertDifferentRotation(Quaternion a, Quaternion b, string what)
        {
            float angle = Quaternion.Angle(a, b);
            Assert.True(angle > 1.0f,
                what + ": fixture is degenerate, " + Fmt(a) + " and " + Fmt(b) +
                " are only " + angle.ToString("F4", CultureInfo.InvariantCulture) + " deg apart");
        }

        private static string Fmt(Quaternion q)
        {
            var ic = CultureInfo.InvariantCulture;
            return "(" + q.x.ToString("F5", ic) + "," + q.y.ToString("F5", ic) + "," +
                   q.z.ToString("F5", ic) + "," + q.w.ToString("F5", ic) + ")";
        }

        private static Quaternion ReadRot(ConfigNode node)
        {
            return KSPUtil.ParseQuaternion(node.GetValue("rot"));
        }

        #endregion

        #region fixture sanity

        [Fact]
        public void Fixture_WorldAndSurfaceRelativeRotationsAreDistinguishable()
        {
            // The whole family is only testable because the body rotation is non-identity.
            // If this ever collapses, every "raw, not composed" assertion below goes vacuous.
            Quaternion worldComposed = Mul(BodyRotation, RecordedSurfaceRelative);
            AssertDifferentRotation(RecordedSurfaceRelative, worldComposed,
                "surface-relative vs body-composed world rotation");

            // And the capture convention really does invert the placement convention.
            Quaternion recaptured = Mul(Conjugate(BodyRotation), worldComposed);
            AssertSameRotation(RecordedSurfaceRelative, recaptured, "capture(place(srfRel))");
        }

        #endregion

        #region ComputeProtoVesselSpawnRotationFromSurfaceRelative

        [Fact]
        public void ComputeProtoVesselSpawnRotation_ReturnsRecordedSurfaceRelative_NotBodyComposed()
        {
            Quaternion result =
                VesselSpawner.ComputeProtoVesselSpawnRotationFromSurfaceRelative(
                    RecordedSurfaceRelative);

            AssertSameRotation(RecordedSurfaceRelative, result,
                "ProtoVessel spawn rotation must stay in the recorded surface-relative frame");
            AssertDifferentRotation(Mul(BodyRotation, RecordedSurfaceRelative), result,
                "ProtoVessel spawn rotation must NOT be the body-composed world rotation");
        }

        [Fact]
        public void ComputeProtoVesselSpawnRotation_SanitizesNonFiniteComponents()
        {
            Quaternion result =
                VesselSpawner.ComputeProtoVesselSpawnRotationFromSurfaceRelative(
                    new Quaternion(float.NaN, 0f, float.PositiveInfinity, 1f));

            Assert.False(float.IsNaN(result.x) || float.IsInfinity(result.x));
            Assert.False(float.IsNaN(result.y) || float.IsInfinity(result.y));
            Assert.False(float.IsNaN(result.z) || float.IsInfinity(result.z));
            Assert.False(float.IsNaN(result.w) || float.IsInfinity(result.w));
            AssertSameRotation(Quaternion.identity, result,
                "a wholly non-finite recorded rotation degrades to identity");
        }

        #endregion

        #region ApplyProtoVesselSpawnRotationToNode

        [Fact]
        public void ApplyProtoVesselSpawnRotationToNode_WritesRawSurfaceRelative_UnderNonIdentityBody()
        {
            ConfigNode node = new VesselSnapshotBuilder()
                .WithName("Rotator")
                .AsLanded(1.0, 2.0, 30.0)
                .Build();

            Quaternion written = VesselSpawner.ApplyProtoVesselSpawnRotationToNode(
                node,
                "Kerbin",
                BodyRotation,
                RecordedSurfaceRelative,
                "unit-test raw surface-relative");

            AssertSameRotation(RecordedSurfaceRelative, written, "returned rotation");
            AssertSameRotation(RecordedSurfaceRelative, ReadRot(node), "written VESSEL.rot");
            AssertDifferentRotation(Mul(BodyRotation, RecordedSurfaceRelative), ReadRot(node),
                "written VESSEL.rot must not be the world rotation");
        }

        [Fact]
        public void ApplyProtoVesselSpawnRotationToNode_WrittenValueRecoversWorldRotationOnLoad()
        {
            // Full round trip through the contract: a vessel whose LIVE world rotation is
            // worldRot gets captured as srfRel, authored into VESSEL.rot, read back by KSP as
            // vessel.srfRelRotation, and placed again as bodyRot * srfRelRotation. That must
            // land back on the original world rotation.
            Quaternion worldRot = Mul(BodyRotation, RecordedSurfaceRelative);
            Quaternion captured = Mul(Conjugate(BodyRotation), worldRot);

            ConfigNode node = new VesselSnapshotBuilder().AsLanded(0.0, 0.0, 10.0).Build();
            VesselSpawner.ApplyProtoVesselSpawnRotationToNode(
                node, "Kerbin", BodyRotation, captured, "unit-test round trip");

            Quaternion loadedSrfRelRotation = ReadRot(node);
            Quaternion placedWorldRotation = Mul(BodyRotation, loadedSrfRelRotation);

            AssertSameRotation(worldRot, placedWorldRotation,
                "capture -> author -> load -> place must return the original world rotation");
        }

        [Fact]
        public void ApplyProtoVesselSpawnRotationToNode_LogsSurfaceRelativeFrameAndBothQuaternions()
        {
            ConfigNode node = new VesselSnapshotBuilder().AsLanded(0.0, 0.0, 10.0).Build();

            VesselSpawner.ApplyProtoVesselSpawnRotationToNode(
                node, "Kerbin", BodyRotation, RecordedSurfaceRelative, "unit-test logging");

            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]") &&
                l.Contains("Spawn rotation prep (unit-test logging)") &&
                l.Contains("frame=surface-relative(format-v0)") &&
                l.Contains("no body multiply") &&
                l.Contains("body=Kerbin"));
        }

        [Fact]
        public void ApplyProtoVesselSpawnRotationToNode_LogsBodyRotationUnavailableWhenNotResolved()
        {
            ConfigNode node = new VesselSnapshotBuilder().AsLanded(0.0, 0.0, 10.0).Build();

            VesselSpawner.ApplyProtoVesselSpawnRotationToNode(
                node, "Kerbin", null, RecordedSurfaceRelative, "unit-test no body rotation");

            // An unresolvable body rotation is diagnostic only - the written value is the
            // recorded surface-relative rotation either way.
            AssertSameRotation(RecordedSurfaceRelative, ReadRot(node),
                "written VESSEL.rot with no body rotation available");
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]") && l.Contains("bodyRot=(unavailable)"));
        }

        #endregion

        #region TryApplySpawnRotationFromSurfaceRelative

        [Fact]
        public void TryApplySpawnRotation_NullNode_ReturnsFalse()
        {
            Assert.False(VesselSpawner.TryApplySpawnRotationFromSurfaceRelative(
                null, "Kerbin", BodyRotation, RecordedSurfaceRelative, "unit-test null node"));
        }

        [Fact]
        public void TryApplySpawnRotation_NoRecordedRotation_LeavesNodeRotUntouched()
        {
            Quaternion preExisting = new Quaternion(0f, 0f, 0.38268343f, 0.92387953f);
            ConfigNode node = new VesselSnapshotBuilder()
                .AsLanded(0.0, 0.0, 10.0)
                .WithSurfaceRelativeRotation(preExisting)
                .Build();

            bool applied = VesselSpawner.TryApplySpawnRotationFromSurfaceRelative(
                node, "Kerbin", BodyRotation, null, "unit-test absent rotation");

            Assert.False(applied);
            AssertSameRotation(preExisting, ReadRot(node),
                "an absent recorded rotation must not rewrite the snapshot's rot");
        }

        [Fact]
        public void TryApplySpawnRotation_PresentRotation_OverwritesNodeRot()
        {
            ConfigNode node = new VesselSnapshotBuilder()
                .AsLanded(0.0, 0.0, 10.0)
                .WithSurfaceRelativeRotation(new Quaternion(0f, 0f, 0.38268343f, 0.92387953f))
                .Build();

            bool applied = VesselSpawner.TryApplySpawnRotationFromSurfaceRelative(
                node, "Kerbin", BodyRotation, RecordedSurfaceRelative, "unit-test overwrite");

            Assert.True(applied);
            AssertSameRotation(RecordedSurfaceRelative, ReadRot(node), "overwritten VESSEL.rot");
        }

        #endregion

        #region TryGetPreferredSpawnRotationFrame / TryResolvePreferredSpawnRotation

        private static Recording LandedRecordingWithTerminalPose(Quaternion rotation, string body = "Kerbin")
        {
            return new Recording
            {
                RecordingId = "rec-rot",
                VesselName = "Lander",
                TerminalStateValue = TerminalState.Landed,
                TerminalPosition = new SurfacePosition
                {
                    body = body,
                    latitude = -0.0972,
                    longitude = -74.5577,
                    altitude = 72.0,
                    rotation = rotation,
                    rotationRecorded = true,
                    situation = SurfaceSituation.Landed,
                },
            };
        }

        private static TrajectoryPoint Point(Quaternion rotation, string body = "Kerbin")
        {
            return new TrajectoryPoint
            {
                ut = 100.0,
                latitude = 1.0,
                longitude = 2.0,
                altitude = 30.0,
                bodyName = body,
                rotation = rotation,
                velocity = Vector3.zero,
            };
        }

        [Fact]
        public void TryGetPreferredSpawnRotationFrame_LandedTerminalPose_PrefersTerminalPose()
        {
            Quaternion fallbackRotation = new Quaternion(0f, 0f, 0.38268343f, 0.92387953f);

            bool ok = VesselSpawner.TryGetPreferredSpawnRotationFrame(
                LandedRecordingWithTerminalPose(RecordedSurfaceRelative),
                Point(fallbackRotation),
                out string bodyName,
                out Quaternion surfaceRelativeRotation,
                out string source);

            Assert.True(ok);
            Assert.Equal("Kerbin", bodyName);
            Assert.Equal("terminal surface pose", source);
            AssertSameRotation(RecordedSurfaceRelative, surfaceRelativeRotation,
                "terminal pose rotation wins over the trajectory tail");
        }

        [Fact]
        public void TryGetPreferredSpawnRotationFrame_OrbitingTerminal_IsNotASurfaceFrame()
        {
            Recording rec = LandedRecordingWithTerminalPose(RecordedSurfaceRelative);
            rec.TerminalStateValue = TerminalState.Orbiting;

            bool ok = VesselSpawner.TryGetPreferredSpawnRotationFrame(
                rec,
                Point(RecordedSurfaceRelative),
                out _,
                out Quaternion surfaceRelativeRotation,
                out string source);

            // The preferred-rotation frame exists only for landed/splashed endpoints; an
            // orbiting endpoint must not borrow a surface attitude.
            Assert.False(ok);
            Assert.Null(source);
            AssertSameRotation(Quaternion.identity, surfaceRelativeRotation,
                "rejected frame reports identity");
        }

        [Fact]
        public void TryGetPreferredSpawnRotationFrame_SplashedTerminal_IsASurfaceFrame()
        {
            Recording rec = LandedRecordingWithTerminalPose(RecordedSurfaceRelative);
            rec.TerminalStateValue = TerminalState.Splashed;

            Assert.True(VesselSpawner.TryGetPreferredSpawnRotationFrame(
                rec, null, out string bodyName, out Quaternion rotation, out string source));
            Assert.Equal("Kerbin", bodyName);
            Assert.Equal("terminal surface pose", source);
            AssertSameRotation(RecordedSurfaceRelative, rotation, "splashed terminal pose rotation");
        }

        [Fact]
        public void TryGetPreferredSpawnRotationFrame_TerminalPoseWithoutRotation_FallsBackToTrajectoryTail()
        {
            Recording rec = LandedRecordingWithTerminalPose(RecordedSurfaceRelative);
            SurfacePosition pose = rec.TerminalPosition.Value;
            pose.rotationRecorded = false;
            rec.TerminalPosition = pose;

            Quaternion tailRotation = new Quaternion(0f, 0f, 0.38268343f, 0.92387953f);
            bool ok = VesselSpawner.TryGetPreferredSpawnRotationFrame(
                rec, Point(tailRotation, "Mun"),
                out string bodyName,
                out Quaternion rotation,
                out string source);

            Assert.True(ok);
            Assert.Equal("Mun", bodyName);
            Assert.Equal("last trajectory point", source);
            AssertSameRotation(tailRotation, rotation, "fallback rotation comes from the tail point");
        }

        [Fact]
        public void TryGetPreferredSpawnRotationFrame_NoBodyAnywhere_ReportsNoFrame()
        {
            Recording rec = LandedRecordingWithTerminalPose(RecordedSurfaceRelative, body: null);

            // No terminal pose body, no fallback point, and no endpoint body to recover: a
            // surface-relative rotation is meaningless without the body it is relative TO.
            Assert.False(VesselSpawner.TryGetPreferredSpawnRotationFrame(
                rec, null, out string bodyName, out _, out string source));
            Assert.Null(bodyName);
            Assert.Null(source);
        }

        [Fact]
        public void TryGetPreferredSpawnRotationFrame_SanitizesNonFiniteTerminalRotation()
        {
            Recording rec = LandedRecordingWithTerminalPose(
                new Quaternion(float.NaN, float.NaN, float.NaN, float.NaN));

            Assert.True(VesselSpawner.TryGetPreferredSpawnRotationFrame(
                rec, null, out _, out Quaternion rotation, out _));
            Assert.False(float.IsNaN(rotation.x) || float.IsNaN(rotation.w));
            AssertSameRotation(Quaternion.identity, rotation,
                "a non-finite recorded terminal rotation degrades to identity, not NaN");
        }

        [Fact]
        public void TryResolvePreferredSpawnRotation_UnresolvableBody_KeepsSurfaceRelativeAndNullsBodyRotation()
        {
            VesselSpawner.BodyResolverForTesting = (string name, out CelestialBody body) =>
            {
                body = null;
                return false;
            };

            bool ok = VesselSpawner.TryResolvePreferredSpawnRotation(
                LandedRecordingWithTerminalPose(RecordedSurfaceRelative),
                null,
                out string bodyName,
                out Quaternion? bodyRotation,
                out Quaternion surfaceRelativeRotation,
                out string source);

            // Failing to resolve the live body must NOT block the spawn rotation: the recorded
            // value is already in the frame ProtoVessel.rot wants, and the body rotation is
            // only carried for the diagnostic log line.
            Assert.True(ok);
            Assert.Equal("Kerbin", bodyName);
            Assert.Equal("terminal surface pose", source);
            Assert.False(bodyRotation.HasValue);
            AssertSameRotation(RecordedSurfaceRelative, surfaceRelativeRotation,
                "surface-relative rotation survives an unresolvable body");
        }

        [Fact]
        public void TryApplyPreferredSpawnRotation_NonSurfaceTerminal_LeavesSnapshotRotUntouched()
        {
            Quaternion preExisting = new Quaternion(0f, 0f, 0.38268343f, 0.92387953f);
            ConfigNode node = new VesselSnapshotBuilder()
                .AsOrbiting(700000.0, 0.01, 0.0)
                .WithSurfaceRelativeRotation(preExisting)
                .Build();

            Recording rec = LandedRecordingWithTerminalPose(RecordedSurfaceRelative);
            rec.TerminalStateValue = TerminalState.Orbiting;

            Assert.False(VesselSpawner.TryApplyPreferredSpawnRotation(
                node, rec, null, "unit-test orbiting"));
            AssertSameRotation(preExisting, ReadRot(node),
                "no preferred surface frame means the snapshot keeps its recorded rot");
        }

        #endregion

        #region ApplyResolvedSpawnStateToSnapshot / PrepareSpawnNodeAtPosition

        [Fact]
        public void ApplyResolvedSpawnStateToSnapshot_LandedRecording_WritesRawSurfaceRelativeRot()
        {
            VesselSpawner.BodyResolverForTesting = (string name, out CelestialBody body) =>
            {
                body = null;
                return false;
            };

            ConfigNode node = new VesselSnapshotBuilder()
                .WithName("Lander")
                .AsLanded(0.0, 0.0, 0.0)
                .WithSurfaceRelativeRotation(Quaternion.identity)
                .Build();

            VesselSpawner.ApplyResolvedSpawnStateToSnapshot(
                node,
                LandedRecordingWithTerminalPose(RecordedSurfaceRelative),
                Point(Quaternion.identity),
                spawnLat: -0.0972,
                spawnLon: -74.5577,
                spawnAlt: 72.0,
                index: 3,
                logContext: "unit-test resolved spawn state");

            AssertSameRotation(RecordedSurfaceRelative, ReadRot(node),
                "resolved spawn state writes the raw recorded surface-relative rotation");
            AssertDifferentRotation(Mul(BodyRotation, RecordedSurfaceRelative), ReadRot(node),
                "resolved spawn state must not write a body-composed world rotation");
            Assert.Equal("-0.0972", node.GetValue("lat"));
            Assert.True(VesselSpawner.HasDeliberatePositionOverrideStamp(node));
        }

        [Fact]
        public void ApplyResolvedSpawnStateToSnapshot_PreferredRotationDisallowed_KeepsRecordedRot()
        {
            Quaternion preExisting = new Quaternion(0f, 0f, 0.38268343f, 0.92387953f);
            ConfigNode node = new VesselSnapshotBuilder()
                .AsLanded(0.0, 0.0, 0.0)
                .WithSurfaceRelativeRotation(preExisting)
                .Build();

            VesselSpawner.ApplyResolvedSpawnStateToSnapshot(
                node,
                LandedRecordingWithTerminalPose(RecordedSurfaceRelative),
                Point(Quaternion.identity),
                spawnLat: 1.0,
                spawnLon: 2.0,
                spawnAlt: 3.0,
                index: 0,
                logContext: "unit-test rotation disallowed",
                allowPreferredRotation: false);

            AssertSameRotation(preExisting, ReadRot(node),
                "allowPreferredRotation:false must leave the snapshot's own rot in place");
            Assert.Equal("3", node.GetValue("alt"));
        }

        [Fact]
        public void PrepareSpawnNodeAtPosition_WithSurfaceRelativeRotation_WritesRawValue()
        {
            ConfigNode node = new VesselSnapshotBuilder().AsLanded(0.0, 0.0, 0.0).Build();

            string sit = VesselSpawner.PrepareSpawnNodeAtPosition(
                node,
                "Kerbin",
                BodyRotation,
                lat: -0.0972,
                lon: -74.5577,
                alt: 72.0,
                speed: 0.0,
                orbitalSpeed: 2246.0,
                overWater: false,
                terminalState: TerminalState.Landed,
                surfaceRelativeRotation: RecordedSurfaceRelative);

            Assert.Equal("LANDED", sit);
            AssertSameRotation(RecordedSurfaceRelative, ReadRot(node),
                "SpawnAtPosition's node prep writes the raw surface-relative rotation");
            AssertDifferentRotation(Mul(BodyRotation, RecordedSurfaceRelative), ReadRot(node),
                "SpawnAtPosition's node prep must not compose the body rotation");
        }

        [Fact]
        public void PrepareSpawnNodeAtPosition_WithoutRotation_LeavesRecordedRotUntouched()
        {
            Quaternion preExisting = new Quaternion(0f, 0f, 0.38268343f, 0.92387953f);
            ConfigNode node = new VesselSnapshotBuilder()
                .AsLanded(0.0, 0.0, 0.0)
                .WithSurfaceRelativeRotation(preExisting)
                .Build();

            VesselSpawner.PrepareSpawnNodeAtPosition(
                node, "Kerbin", BodyRotation,
                lat: 0.0, lon: 0.0, alt: 72.0,
                speed: 0.0, orbitalSpeed: 2246.0, overWater: false,
                terminalState: TerminalState.Landed,
                surfaceRelativeRotation: null);

            AssertSameRotation(preExisting, ReadRot(node),
                "no rotation override means the snapshot's recorded rot survives");
        }

        [Fact]
        public void OverrideSnapshotPosition_WritesRawSurfaceRelativeRotAndLogsSource()
        {
            ConfigNode node = new VesselSnapshotBuilder().AsLanded(0.0, 0.0, 0.0).Build();

            VesselSpawner.OverrideSnapshotPosition(
                node,
                lat: 5.0, lon: 6.0, alt: 7.0,
                index: 2,
                vesselName: "Lander",
                rotationBodyName: "Kerbin",
                rotationBodyRotation: BodyRotation,
                surfaceRelativeRotation: RecordedSurfaceRelative,
                rotationSource: "terminal surface pose");

            AssertSameRotation(RecordedSurfaceRelative, ReadRot(node),
                "snapshot position override writes the raw surface-relative rotation");
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]") &&
                l.Contains("Snapshot position override for #2 (Lander)") &&
                l.Contains("rot updated from surface-relative frame") &&
                l.Contains("source=terminal surface pose"));
        }

        #endregion

        #region generator contract

        [Fact]
        public void VesselSnapshotBuilder_DefaultRotationIsIdentityLiteral()
        {
            // Existing fixtures (and the byte-identity gates over generated saves) depend on
            // the historical default literal, so the new rotation parameter must not perturb
            // any snapshot that does not ask for one.
            ConfigNode node = new VesselSnapshotBuilder().AsLanded(0.0, 0.0, 0.0).Build();
            Assert.Equal("0,0,0,1", node.GetValue("rot"));
        }

        [Fact]
        public void VesselSnapshotBuilder_NonIdentityRotationRoundTripsThroughKspParser()
        {
            ConfigNode node = new VesselSnapshotBuilder()
                .AsLanded(0.0, 0.0, 0.0)
                .WithSurfaceRelativeRotation(RecordedSurfaceRelative)
                .Build();

            Assert.Equal(4, node.GetValue("rot").Split(',').Length);
            AssertSameRotation(RecordedSurfaceRelative, ReadRot(node),
                "the generator's rot value must parse back as the requested rotation");
        }

        #endregion
    }
}
