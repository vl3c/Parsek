using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class SpawnRotationTests : IDisposable
    {
        // Surface-relative upright-at-KSC fixture captured from v.srfRelRotation in runtime.
        private static readonly Quaternion KerbinPadSurfaceRelativeRotation =
            new Quaternion(-0.7009714f, -0.09230039f, -0.09728389f, 0.7004681f);

        private readonly List<string> logLines = new List<string>();

        public SpawnRotationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        [Fact]
        public void ApplyWorldSpawnRotationToNode_KerbinPadCase_ReconstructsWorldRotationAndLogsFrame()
        {
            var snapshot = new ConfigNode("VESSEL");
            Quaternion bodyRotation = Quaternion.Euler(-12f, 25f, 4f);

            Quaternion expected = TrajectoryMath.SanitizeQuaternion(
                bodyRotation * KerbinPadSurfaceRelativeRotation);

            Quaternion written = VesselSpawner.ApplyWorldSpawnRotationToNode(
                snapshot,
                "Kerbin",
                bodyRotation,
                KerbinPadSurfaceRelativeRotation,
                "Kerbin pad spawn test");

            AssertQuaternionEquivalent(expected, written);
            Assert.Equal(KSPUtil.WriteQuaternion(expected), snapshot.GetValue("rot"));
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]") &&
                l.Contains("Kerbin pad spawn test") &&
                l.Contains("surface-relative(format-v0)") &&
                l.Contains("body.bodyTransform.rotation * srfRelRotation"));
        }

        [Fact]
        public void ApplyWorldSpawnRotationToNode_MunCase_ReconstructsWorldRotation()
        {
            var snapshot = new ConfigNode("VESSEL");
            Quaternion bodyRotation = Quaternion.Euler(18f, -40f, 12f);
            Quaternion munSurfaceRelativeRotation = Quaternion.Euler(-8f, 15f, 27f);

            Quaternion expected = TrajectoryMath.SanitizeQuaternion(
                bodyRotation * munSurfaceRelativeRotation);

            Quaternion written = VesselSpawner.ApplyWorldSpawnRotationToNode(
                snapshot,
                "Mun",
                bodyRotation,
                munSurfaceRelativeRotation,
                "Mun spawn test");

            AssertQuaternionEquivalent(expected, written);
            Assert.Equal(KSPUtil.WriteQuaternion(expected), snapshot.GetValue("rot"));
        }

        [Fact]
        public void TryApplySpawnRotationFromSurfaceRelative_NullBody_LeavesRotUnchangedAndWarns()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("rot", "1,0,0,0");

            bool applied = VesselSpawner.TryApplySpawnRotationFromSurfaceRelative(
                snapshot,
                body: null,
                surfaceRelativeRotation: Quaternion.identity,
                context: "missing body test");

            Assert.False(applied);
            Assert.Equal("1,0,0,0", snapshot.GetValue("rot"));
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]") &&
                l.Contains("Spawn rotation prep skipped") &&
                l.Contains("missing body test"));
        }

        [Fact]
        public void TryGetPreferredSpawnRotationFrame_SurfaceTerminal_PrefersTerminalSurfacePoseAndBody()
        {
            Quaternion terminalRotation = Quaternion.Euler(4f, 15f, -22f);
            Quaternion lastPointRotation = Quaternion.Euler(-8f, 33f, 11f);
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Landed,
                TerminalPosition = new SurfacePosition
                {
                    body = "Kerbin",
                    rotation = terminalRotation,
                    situation = SurfaceSituation.Landed
                }
            };

            var lastPt = new TrajectoryPoint
            {
                bodyName = "Mun",
                rotation = lastPointRotation
            };

            bool found = VesselSpawner.TryGetPreferredSpawnRotationFrame(
                rec,
                lastPt,
                out string bodyName,
                out Quaternion surfaceRelativeRotation,
                out string source);

            Assert.True(found);
            Assert.Equal("Kerbin", bodyName);
            Assert.Equal("terminal surface pose", source);
            AssertQuaternionEquivalent(
                TrajectoryMath.SanitizeQuaternion(terminalRotation),
                surfaceRelativeRotation);
        }

        [Fact]
        public void TryGetPreferredSpawnRotationFrame_NoTerminalSurfacePose_FallsBackToLastTrajectoryPoint()
        {
            Quaternion lastPointRotation = Quaternion.Euler(-6f, 21f, 13f);
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Landed
            };

            var lastPt = new TrajectoryPoint
            {
                bodyName = "Mun",
                rotation = lastPointRotation
            };

            bool found = VesselSpawner.TryGetPreferredSpawnRotationFrame(
                rec,
                lastPt,
                out string bodyName,
                out Quaternion surfaceRelativeRotation,
                out string source);

            Assert.True(found);
            Assert.Equal("Mun", bodyName);
            Assert.Equal("last trajectory point", source);
            AssertQuaternionEquivalent(
                TrajectoryMath.SanitizeQuaternion(lastPointRotation),
                surfaceRelativeRotation);
        }

        [Fact]
        public void TryGetPreferredSpawnRotationFrame_LegacyTerminalPoseWithoutRotation_FallsBackToLastTrajectoryPoint()
        {
            var legacyTerminalNode = new ConfigNode("TERMINAL_POSITION");
            legacyTerminalNode.AddValue("body", "Kerbin");
            legacyTerminalNode.AddValue("lat", "0");
            legacyTerminalNode.AddValue("lon", "0");
            legacyTerminalNode.AddValue("alt", "0");
            legacyTerminalNode.AddValue("situation", ((int)SurfaceSituation.Landed).ToString());

            Quaternion lastPointRotation = Quaternion.Euler(-11f, 8f, 29f);
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Landed,
                TerminalPosition = SurfacePosition.LoadFrom(legacyTerminalNode)
            };

            var lastPt = new TrajectoryPoint
            {
                bodyName = "Mun",
                rotation = lastPointRotation
            };

            bool found = VesselSpawner.TryGetPreferredSpawnRotationFrame(
                rec,
                lastPt,
                out string bodyName,
                out Quaternion surfaceRelativeRotation,
                out string source);

            Assert.True(found);
            Assert.False(rec.TerminalPosition.Value.HasRecordedRotation);
            Assert.Equal("Mun", bodyName);
            Assert.Equal("last trajectory point", source);
            AssertQuaternionEquivalent(
                TrajectoryMath.SanitizeQuaternion(lastPointRotation),
                surfaceRelativeRotation);
        }

        [Fact]
        public void OverrideSnapshotPosition_WithRotationFrame_RewritesRotAndLogsSource()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("lat", "1.0");
            snapshot.AddValue("lon", "2.0");
            snapshot.AddValue("alt", "3.0");
            snapshot.AddValue("rot", "1,0,0,0");

            Quaternion bodyRotation = Quaternion.Euler(-12f, 25f, 4f);
            Quaternion surfaceRelativeRotation = Quaternion.Euler(7f, -18f, 33f);
            Quaternion expected = VesselSpawner.ComputeWorldSpawnRotationFromSurfaceRelative(
                bodyRotation,
                surfaceRelativeRotation);

            VesselSpawner.OverrideSnapshotPosition(
                snapshot,
                10.5,
                20.5,
                30.5,
                4,
                "Fallback",
                "Kerbin",
                bodyRotation,
                surfaceRelativeRotation,
                "terminal surface pose");

            Assert.Equal("10.5", snapshot.GetValue("lat"));
            Assert.Equal("20.5", snapshot.GetValue("lon"));
            Assert.Equal("30.5", snapshot.GetValue("alt"));
            Assert.Equal(KSPUtil.WriteQuaternion(expected), snapshot.GetValue("rot"));
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]") &&
                l.Contains("Snapshot position override for #4 (Fallback)") &&
                l.Contains("rot updated from surface-relative frame") &&
                l.Contains("source=terminal surface pose"));
        }

        private static void AssertQuaternionEquivalent(
            Quaternion expected,
            Quaternion actual,
            float epsilon = 1e-4f)
        {
            float dot = expected.x * actual.x + expected.y * actual.y +
                        expected.z * actual.z + expected.w * actual.w;
            if (dot < 0f)
            {
                actual = new Quaternion(-actual.x, -actual.y, -actual.z, -actual.w);
            }

            Assert.InRange(Mathf.Abs(expected.x - actual.x), 0f, epsilon);
            Assert.InRange(Mathf.Abs(expected.y - actual.y), 0f, epsilon);
            Assert.InRange(Mathf.Abs(expected.z - actual.z), 0f, epsilon);
            Assert.InRange(Mathf.Abs(expected.w - actual.w), 0f, epsilon);
        }
    }
}
