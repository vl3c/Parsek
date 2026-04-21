using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    public static class SpawnRotationInGameTests
    {
        private static readonly Quaternion KerbinPadSurfaceRelativeRotation =
            new Quaternion(-0.7009714f, -0.09230039f, -0.09728389f, 0.7004681f);

        [InGameTest(Category = "SpawnRotation", Scene = GameScenes.FLIGHT,
            Description = "ApplyWorldSpawnRotationToNode reconstructs the Kerbin world-frame spawn rotation and logs the source frame")]
        public static void ApplyWorldSpawnRotationToNode_KerbinPadCase_ReconstructsWorldRotationAndLogsFrame()
        {
            List<string> logLines = CaptureLogLines(() =>
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
                InGameAssert.AreEqual(KSPUtil.WriteQuaternion(expected), snapshot.GetValue("rot"));
            });

            AssertContains(logLines, "[Spawner]", "Kerbin pad spawn test",
                "surface-relative(format-v0)", "body.bodyTransform.rotation * srfRelRotation");
        }

        [InGameTest(Category = "SpawnRotation", Scene = GameScenes.FLIGHT,
            Description = "ApplyWorldSpawnRotationToNode reconstructs the Mun world-frame spawn rotation")]
        public static void ApplyWorldSpawnRotationToNode_MunCase_ReconstructsWorldRotation()
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
            InGameAssert.AreEqual(KSPUtil.WriteQuaternion(expected), snapshot.GetValue("rot"));
        }

        [InGameTest(Category = "SpawnRotation", Scene = GameScenes.FLIGHT,
            Description = "PrepareSpawnNodeAtPosition rewrites Kerbin spawn nodes into world-frame rotation")]
        public static void PrepareSpawnNodeAtPosition_KerbinPadCase_RewritesSpawnNodeRotToWorldFrame()
        {
            var spawnNode = new ConfigNode("VESSEL");
            Quaternion bodyRotation = Quaternion.Euler(-12f, 25f, 4f);
            Quaternion expected = VesselSpawner.ComputeWorldSpawnRotationFromSurfaceRelative(
                bodyRotation,
                KerbinPadSurfaceRelativeRotation);

            string sit = VesselSpawner.PrepareSpawnNodeAtPosition(
                spawnNode,
                "Kerbin",
                bodyRotation,
                lat: -0.0972,
                lon: -74.5577,
                alt: 70.0,
                speed: 0.0,
                orbitalSpeed: 2200.0,
                overWater: false,
                terminalState: TerminalState.Landed,
                surfaceRelativeRotation: KerbinPadSurfaceRelativeRotation);

            InGameAssert.AreEqual("LANDED", sit);
            InGameAssert.AreEqual(KSPUtil.WriteQuaternion(expected), spawnNode.GetValue("rot"));
        }

        [InGameTest(Category = "SpawnRotation", Scene = GameScenes.FLIGHT,
            Description = "PrepareSpawnNodeAtPosition rewrites Mun spawn nodes into world-frame rotation")]
        public static void PrepareSpawnNodeAtPosition_MunCase_RewritesSpawnNodeRotToWorldFrame()
        {
            var spawnNode = new ConfigNode("VESSEL");
            Quaternion bodyRotation = Quaternion.Euler(18f, -40f, 12f);
            Quaternion munSurfaceRelativeRotation = Quaternion.Euler(-8f, 15f, 27f);
            Quaternion expected = VesselSpawner.ComputeWorldSpawnRotationFromSurfaceRelative(
                bodyRotation,
                munSurfaceRelativeRotation);

            string sit = VesselSpawner.PrepareSpawnNodeAtPosition(
                spawnNode,
                "Mun",
                bodyRotation,
                lat: -1.534,
                lon: 27.245,
                alt: 12.0,
                speed: 0.0,
                orbitalSpeed: 560.0,
                overWater: false,
                terminalState: TerminalState.Landed,
                surfaceRelativeRotation: munSurfaceRelativeRotation);

            InGameAssert.AreEqual("LANDED", sit);
            InGameAssert.AreEqual(KSPUtil.WriteQuaternion(expected), spawnNode.GetValue("rot"));
        }

        [InGameTest(Category = "SpawnRotation", Scene = GameScenes.FLIGHT,
            Description = "Spawn rotation prep leaves rot unchanged and warns when body resolution is unavailable")]
        public static void TryApplySpawnRotationFromSurfaceRelative_NullBody_LeavesRotUnchangedAndWarns()
        {
            List<string> logLines = CaptureLogLines(() =>
            {
                var snapshot = new ConfigNode("VESSEL");
                snapshot.AddValue("rot", "1,0,0,0");

                bool applied = VesselSpawner.TryApplySpawnRotationFromSurfaceRelative(
                    snapshot,
                    body: null,
                    surfaceRelativeRotation: Quaternion.identity,
                    context: "missing body test");

                InGameAssert.IsFalse(applied, "Expected null-body spawn prep to decline");
                InGameAssert.AreEqual("1,0,0,0", snapshot.GetValue("rot"));
            });

            AssertContains(logLines, "[Spawner]", "Spawn rotation prep skipped", "missing body test");
        }

        [InGameTest(Category = "SpawnRotation", Scene = GameScenes.FLIGHT,
            Description = "Terminal surface pose takes precedence over the last trajectory point when choosing a spawn rotation frame")]
        public static void TryGetPreferredSpawnRotationFrame_SurfaceTerminal_PrefersTerminalSurfacePoseAndBody()
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

            InGameAssert.IsTrue(found, "Expected a preferred spawn rotation frame");
            InGameAssert.AreEqual("Kerbin", bodyName);
            InGameAssert.AreEqual("terminal surface pose", source);
            AssertQuaternionEquivalent(
                TrajectoryMath.SanitizeQuaternion(terminalRotation),
                surfaceRelativeRotation);
        }

        [InGameTest(Category = "SpawnRotation", Scene = GameScenes.FLIGHT,
            Description = "Spawn rotation falls back to the last trajectory point when terminal surface pose is unavailable")]
        public static void TryGetPreferredSpawnRotationFrame_NoTerminalSurfacePose_FallsBackToLastTrajectoryPoint()
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

            InGameAssert.IsTrue(found, "Expected last-point fallback to resolve");
            InGameAssert.AreEqual("Mun", bodyName);
            InGameAssert.AreEqual("last trajectory point", source);
            AssertQuaternionEquivalent(
                TrajectoryMath.SanitizeQuaternion(lastPointRotation),
                surfaceRelativeRotation);
        }

        [InGameTest(Category = "SpawnRotation", Scene = GameScenes.FLIGHT,
            Description = "Legacy terminal surface poses without rotation still fall back to the last trajectory point")]
        public static void TryGetPreferredSpawnRotationFrame_LegacyTerminalPoseWithoutRotation_FallsBackToLastTrajectoryPoint()
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

            InGameAssert.IsTrue(found, "Expected last-point fallback to resolve");
            InGameAssert.IsFalse(rec.TerminalPosition.Value.HasRecordedRotation,
                "Legacy terminal pose should not report a recorded rotation");
            InGameAssert.AreEqual("Mun", bodyName);
            InGameAssert.AreEqual("last trajectory point", source);
            AssertQuaternionEquivalent(
                TrajectoryMath.SanitizeQuaternion(lastPointRotation),
                surfaceRelativeRotation);
        }

        [InGameTest(Category = "SpawnRotation", Scene = GameScenes.FLIGHT,
            Description = "Orbiting terminals do not reconstruct a surface-relative spawn rotation")]
        public static void TryGetPreferredSpawnRotationFrame_OrbitingTerminal_DoesNotReconstructSurfaceRotation()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Orbiting
            };

            var lastPt = new TrajectoryPoint
            {
                bodyName = "Kerbin",
                rotation = Quaternion.Euler(-11f, 8f, 29f)
            };

            bool found = VesselSpawner.TryGetPreferredSpawnRotationFrame(
                rec,
                lastPt,
                out string bodyName,
                out Quaternion surfaceRelativeRotation,
                out string source);

            InGameAssert.IsFalse(found, "Orbiting terminal should not reconstruct a surface-relative frame");
            InGameAssert.IsNull(bodyName, "Expected null body name for orbiting terminal");
            InGameAssert.IsNull(source, "Expected null source for orbiting terminal");
            InGameAssert.AreEqual(Quaternion.identity, surfaceRelativeRotation);
        }

        [InGameTest(Category = "SpawnRotation", Scene = GameScenes.FLIGHT,
            Description = "OverrideSnapshotPosition rewrites snapshot rotation from a supplied surface-relative frame and logs the source")]
        public static void OverrideSnapshotPosition_WithRotationFrame_RewritesRotAndLogsSource()
        {
            List<string> logLines = CaptureLogLines(() =>
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

                InGameAssert.AreEqual("10.5", snapshot.GetValue("lat"));
                InGameAssert.AreEqual("20.5", snapshot.GetValue("lon"));
                InGameAssert.AreEqual("30.5", snapshot.GetValue("alt"));
                InGameAssert.AreEqual(KSPUtil.WriteQuaternion(expected), snapshot.GetValue("rot"));
            });

            AssertContains(logLines, "[Spawner]", "Snapshot position override for #4 (Fallback)",
                "rot updated from surface-relative frame", "source=terminal surface pose");
        }

        private static List<string> CaptureLogLines(Action action)
        {
            var lines = new List<string>();
            Action<string> priorSink = ParsekLog.TestSinkForTesting;
            bool? priorVerbose = ParsekLog.VerboseOverrideForTesting;
            try
            {
                ParsekLog.VerboseOverrideForTesting = true;
                ParsekLog.TestSinkForTesting = line =>
                {
                    lines.Add(line);
                    priorSink?.Invoke(line);
                };
                action();
            }
            finally
            {
                ParsekLog.TestSinkForTesting = priorSink;
                ParsekLog.VerboseOverrideForTesting = priorVerbose;
            }

            return lines;
        }

        private static void AssertContains(List<string> lines, params string[] fragments)
        {
            foreach (string line in lines)
            {
                bool matched = true;
                foreach (string fragment in fragments)
                {
                    if (!line.Contains(fragment))
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                    return;
            }

            InGameAssert.Fail("Expected captured logs to contain: " + string.Join(", ", fragments));
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

            InGameAssert.ApproxEqual(expected.x, actual.x, epsilon, "Quaternion x mismatch");
            InGameAssert.ApproxEqual(expected.y, actual.y, epsilon, "Quaternion y mismatch");
            InGameAssert.ApproxEqual(expected.z, actual.z, epsilon, "Quaternion z mismatch");
            InGameAssert.ApproxEqual(expected.w, actual.w, epsilon, "Quaternion w mismatch");
        }
    }
}
