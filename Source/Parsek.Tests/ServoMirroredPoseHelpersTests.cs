using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.InGameTests;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure halves of the ServoMirroredPose in-game cell (lane SRV-3): finding a mirrored
    /// hinge symmetry pair in a stock-shaped snapshot, the signed turn of a moving transform
    /// about its axis, the mirror residual and the settled sample UT. The live half (a real
    /// ghost build over the committed recording) runs only in game.
    /// </summary>
    public class ServoMirroredPoseHelpersTests
    {
        private static Quaternion AxisAngle(Vector3 axis, float degrees)
        {
            // Built by hand: Quaternion.AngleAxis is a Unity ECall the test host cannot run.
            float len = (float)Math.Sqrt(axis.x * axis.x + axis.y * axis.y + axis.z * axis.z);
            float half = degrees * (float)Math.PI / 360f;
            float s = (float)Math.Sin(half) / len;
            return new Quaternion(axis.x * s, axis.y * s, axis.z * s, (float)Math.Cos(half));
        }

        private static ConfigNode HingePart(ConfigNode vessel, string name, uint pid, string target,
            string mirror, params int[] sym)
        {
            ConfigNode part = vessel.AddNode("PART");
            part.AddValue("name", name);
            part.AddValue("persistentId", pid.ToString(CultureInfo.InvariantCulture));
            foreach (int s in sym) part.AddValue("sym", s.ToString(CultureInfo.InvariantCulture));
            ConfigNode module = part.AddNode("MODULE");
            module.AddValue("name", "ModuleRoboticServoHinge");
            if (target != null) module.AddValue("targetAngle", target);
            if (mirror != null) module.AddValue("mirrorRotation", mirror);
            return part;
        }

        private static ConfigNode PlainPart(ConfigNode vessel, string name, uint pid)
        {
            ConfigNode part = vessel.AddNode("PART");
            part.AddValue("name", name);
            part.AddValue("persistentId", pid.ToString(CultureInfo.InvariantCulture));
            return part;
        }

        [Fact]
        public void FindsTheSymLinkedPair_PlainAndMirroredHalvesNamed()
        {
            var v = new ConfigNode("VESSEL");
            PlainPart(v, "structuralMiniNode", 10);              // index 0
            HingePart(v, "hinge.01", 20, "60", "False", 2);      // index 1
            HingePart(v, "hinge.01", 30, "60", "True", 1);       // index 2
            HingePart(v, "hinge.03", 40, "90", "False");         // index 3

            Assert.True(ServoMirroredPoseInGameTests.TryFindMirroredHingePair(v, out var pair));
            Assert.Equal("hinge.01", pair.partName);
            Assert.Equal(20u, pair.plainPid);
            Assert.Equal(30u, pair.mirroredPid);
            Assert.Equal(60f, pair.plainTarget);
            Assert.Equal(60f, pair.mirroredTarget);
            Assert.True(pair.linkedBySym);
        }

        [Fact]
        public void PrefersTheSymLinkedHalfOverAnEarlierSameNameHinge()
        {
            var v = new ConfigNode("VESSEL");
            HingePart(v, "hinge.01", 11, "0", "False");          // index 0, unlinked
            HingePart(v, "hinge.01", 21, "45", "False", 2);      // index 1
            HingePart(v, "hinge.01", 31, "45", "True", 1);       // index 2

            Assert.True(ServoMirroredPoseInGameTests.TryFindMirroredHingePair(v, out var pair));
            Assert.Equal(21u, pair.plainPid);
            Assert.Equal(31u, pair.mirroredPid);
        }

        [Fact]
        public void FallsBackToASameNameHalfWithoutSymLinks()
        {
            var v = new ConfigNode("VESSEL");
            HingePart(v, "hinge.01", 21, "45", "False");
            HingePart(v, "hinge.01", 31, "45", "True");

            Assert.True(ServoMirroredPoseInGameTests.TryFindMirroredHingePair(v, out var pair));
            Assert.Equal(21u, pair.plainPid);
            Assert.Equal(31u, pair.mirroredPid);
            Assert.False(pair.linkedBySym);
        }

        [Fact]
        public void NoPair_WhenNothingIsMirroredOrNamesDiffer()
        {
            var v = new ConfigNode("VESSEL");
            HingePart(v, "hinge.01", 21, "45", "False");
            HingePart(v, "hinge.03", 31, "45", "True");
            Assert.False(ServoMirroredPoseInGameTests.TryFindMirroredHingePair(v, out _));

            var w = new ConfigNode("VESSEL");
            HingePart(w, "hinge.01", 21, "45", "False", 1);
            HingePart(w, "hinge.01", 31, "45", "False", 0);
            Assert.False(ServoMirroredPoseInGameTests.TryFindMirroredHingePair(w, out _));

            Assert.False(ServoMirroredPoseInGameTests.TryFindMirroredHingePair(null, out _));
        }

        [Fact]
        public void SkipsAHingeWithoutAReadableTargetAngleOrPid()
        {
            var v = new ConfigNode("VESSEL");
            HingePart(v, "hinge.01", 21, null, "False", 1);
            HingePart(v, "hinge.01", 31, "45", "True", 0);
            Assert.False(ServoMirroredPoseInGameTests.TryFindMirroredHingePair(v, out _));

            var w = new ConfigNode("VESSEL");
            HingePart(w, "hinge.01", 0, "45", "False", 1);
            HingePart(w, "hinge.01", 31, "45", "True", 0);
            Assert.False(ServoMirroredPoseInGameTests.TryFindMirroredHingePair(w, out _));
        }

        [Fact]
        public void OnlyTheFirstRoboticModuleCounts()
        {
            // A part whose robotic ordinal 0 is not a hinge is not a hinge half (the ghost keys
            // the pose on ordinal 0).
            var v = new ConfigNode("VESSEL");
            ConfigNode a = PlainPart(v, "hinge.01", 21);
            a.AddNode("MODULE").AddValue("name", "ModuleRoboticRotationServo");
            ConfigNode ah = a.AddNode("MODULE");
            ah.AddValue("name", "ModuleRoboticServoHinge");
            ah.AddValue("targetAngle", "45");
            HingePart(v, "hinge.01", 31, "45", "True");
            Assert.False(ServoMirroredPoseInGameTests.TryFindMirroredHingePair(v, out _));
        }

        [Theory]
        [InlineData(60f)]
        [InlineData(-60f)]
        [InlineData(0f)]
        [InlineData(179f)]
        [InlineData(-150f)]
        public void SignedAngle_RecoversTheTurnAboutTheAxis(float degrees)
        {
            Vector3 axis = new Vector3(1f, 0f, 0f);
            Quaternion rest = AxisAngle(new Vector3(0f, 1f, 0f), 90f);
            Quaternion current = rest * AxisAngle(axis, degrees);
            float got = ServoMirroredPoseInGameTests.SignedAngleAboutAxisDegrees(rest, current, axis);
            Assert.InRange(got, degrees - 0.01f, degrees + 0.01f);
        }

        [Fact]
        public void SignedAngle_IsNaNForAZeroAxisAndScaleFreeOtherwise()
        {
            Quaternion q = AxisAngle(new Vector3(0f, 0f, 1f), 30f);
            Assert.True(float.IsNaN(ServoMirroredPoseInGameTests.SignedAngleAboutAxisDegrees(
                Quaternion.identity, q, Vector3.zero)));
            float got = ServoMirroredPoseInGameTests.SignedAngleAboutAxisDegrees(
                Quaternion.identity, q, new Vector3(0f, 0f, 5f));
            Assert.InRange(got, 29.99f, 30.01f);
        }

        [Fact]
        public void MirrorResidual_ZeroForNegativesAndTwiceTheAngleForTheOldBug()
        {
            Assert.Equal(0f, ServoMirroredPoseInGameTests.MirrorResidualDegrees(60f, -60f), 0.001f);
            // The defect: the mirrored half posed at +targetAngle like the plain one.
            Assert.Equal(120f, ServoMirroredPoseInGameTests.MirrorResidualDegrees(60f, 60f), 0.001f);
            // Wraps across +/-180.
            Assert.Equal(0f, ServoMirroredPoseInGameTests.MirrorResidualDegrees(180f, 180f), 0.001f);
            Assert.Equal(2f, ServoMirroredPoseInGameTests.MirrorResidualDegrees(179f, -177f), 0.001f);
        }

        private static PartEvent Ev(double ut, uint pid, PartEventType type, float value, int midx = 0)
            => new PartEvent { ut = ut, partPersistentId = pid, eventType = type, value = value, moduleIndex = midx };

        [Fact]
        public void SettledSampleUT_IsTheLaterLastStop_WithEachHalfsValueAtIt()
        {
            var events = new List<PartEvent>
            {
                Ev(36.0, 20, PartEventType.RoboticMotionStarted, 59.4f),
                Ev(36.0, 30, PartEventType.RoboticMotionStarted, -59.4f),
                Ev(37.9, 20, PartEventType.RoboticMotionStopped, -89.9f),
                Ev(38.0, 30, PartEventType.RoboticMotionStopped, 89.9f),
                Ev(41.0, 20, PartEventType.RoboticMotionStarted, -89.3f),
                Ev(41.5, 20, PartEventType.RoboticPositionSample, 0.1f),
                Ev(41.0, 30, PartEventType.RoboticMotionStarted, 89.3f),
                Ev(43.2, 20, PartEventType.RoboticMotionStopped, 89.95f),
                Ev(43.3, 30, PartEventType.RoboticMotionStopped, -89.95f),
                Ev(43.4, 20, PartEventType.RoboticMotionStopped, 1f, midx: 1),
                Ev(44.0, 99, PartEventType.RoboticMotionStopped, 5f),
            };
            Assert.True(ServoMirroredPoseInGameTests.TryResolveSettledSampleUT(
                events, 20, 30, out double ut, out float a, out float b));
            Assert.Equal(43.3, ut, 6);
            Assert.Equal(89.95f, a);
            Assert.Equal(-89.95f, b);
        }

        [Fact]
        public void SettledSampleUT_FalseWhenEitherHalfNeverStopped()
        {
            var events = new List<PartEvent>
            {
                Ev(36.0, 20, PartEventType.RoboticMotionStarted, 59.4f),
                Ev(37.9, 20, PartEventType.RoboticMotionStopped, -89.9f),
                Ev(36.0, 30, PartEventType.RoboticMotionStarted, -59.4f),
            };
            Assert.False(ServoMirroredPoseInGameTests.TryResolveSettledSampleUT(
                events, 20, 30, out _, out _, out _));
            Assert.False(ServoMirroredPoseInGameTests.TryResolveSettledSampleUT(
                null, 20, 30, out _, out _, out _));
        }

        [Fact]
        public void OnlyThePoseFamiliesArePoseEvents()
        {
            Assert.True(ServoMirroredPoseInGameTests.IsRoboticPoseEvent(PartEventType.RoboticMotionStarted));
            Assert.True(ServoMirroredPoseInGameTests.IsRoboticPoseEvent(PartEventType.RoboticPositionSample));
            Assert.True(ServoMirroredPoseInGameTests.IsRoboticPoseEvent(PartEventType.RoboticMotionStopped));
            Assert.False(ServoMirroredPoseInGameTests.IsRoboticPoseEvent(PartEventType.EngineIgnited));
        }
    }
}
