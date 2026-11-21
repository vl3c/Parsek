using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The playback half of servo followers: a part riding a servo's moving transform keeps
    /// its pose in that transform's frame, so when the servo turns the part turns with it.
    /// Pure math only (Quaternion.Inverse / AngleAxis / Euler are Unity ECalls and throw
    /// headless, so the rotations here are built by hand).
    /// </summary>
    public class ServoFollowerPoseTests
    {
        private const float Tol = 1e-5f;

        // +90 degrees about local Z.
        private static readonly Quaternion Z90 = new Quaternion(0f, 0f, 0.70710678f, 0.70710678f);
        // +90 degrees about local Y.
        private static readonly Quaternion Y90 = new Quaternion(0f, 0.70710678f, 0f, 0.70710678f);
        private static readonly Quaternion Id = new Quaternion(0f, 0f, 0f, 1f);

        private static void AssertVec(Vector3 expected, Vector3 actual)
        {
            Assert.True(Mathf.Abs(expected.x - actual.x) < Tol
                && Mathf.Abs(expected.y - actual.y) < Tol
                && Mathf.Abs(expected.z - actual.z) < Tol,
                $"expected ({expected.x},{expected.y},{expected.z}) got ({actual.x},{actual.y},{actual.z})");
        }

        private static void AssertRot(Quaternion expected, Quaternion actual)
        {
            float dot = expected.x * actual.x + expected.y * actual.y
                + expected.z * actual.z + expected.w * actual.w;
            Assert.True(Mathf.Abs(Mathf.Abs(dot) - 1f) < 1e-4f,
                $"expected ({expected.x},{expected.y},{expected.z},{expected.w}) " +
                $"got ({actual.x},{actual.y},{actual.z},{actual.w})");
        }

        [Fact]
        public void FollowerAtRestPoseStaysAtItsSnapshotPose()
        {
            var servoPos = new Vector3(0.3f, 1f, -0.2f);
            var partPos = new Vector3(0.3f, 2.5f, 0.4f);
            GhostPlaybackLogic.ComputeServoFollowerRelativePose(servoPos, Y90, 1f, partPos, Z90,
                out Vector3 relPos, out Quaternion relRot);

            GhostPlaybackLogic.ComputeServoFollowerPose(servoPos, Y90, 1f, relPos, relRot,
                out Vector3 pos, out Quaternion rot);

            AssertVec(partPos, pos);
            AssertRot(Z90, rot);
        }

        [Fact]
        public void FollowerSwingsWithTheServoTransform()
        {
            // Hinge moving transform at (0,1,0); a panel 1 m above it on the moving side.
            var servoPos = new Vector3(0f, 1f, 0f);
            GhostPlaybackLogic.ComputeServoFollowerRelativePose(servoPos, Id, 1f,
                new Vector3(0f, 2f, 0f), Id, out Vector3 relPos, out Quaternion relRot);

            // The hinge turns 90 degrees about Z: the panel must swing to the hinge's side,
            // not stay at (0,2,0) as it did when nothing followed the servo.
            GhostPlaybackLogic.ComputeServoFollowerPose(servoPos, Z90, 1f, relPos, relRot,
                out Vector3 pos, out Quaternion rot);

            AssertVec(new Vector3(-1f, 1f, 0f), pos);
            AssertRot(Z90, rot);
            Assert.NotEqual(2f, pos.y, 3);
        }

        [Fact]
        public void ChainScaleIsAppliedToTheFollowerOffset()
        {
            GhostPlaybackLogic.ComputeServoFollowerRelativePose(Vector3.zero, Id, 2f,
                new Vector3(0f, 4f, 0f), Id, out Vector3 relPos, out _);
            AssertVec(new Vector3(0f, 2f, 0f), relPos);

            GhostPlaybackLogic.ComputeServoFollowerPose(Vector3.zero, Z90, 2f, relPos, Id,
                out Vector3 pos, out _);
            AssertVec(new Vector3(-4f, 0f, 0f), pos);
        }

        [Fact]
        public void NestedServoComposesThroughItsCarrierPart()
        {
            // Outer hinge A at origin carries part P (the nested hinge B's part) at (0,1,0).
            // B's moving transform sits 0.5 m above P's origin; part Q rides B, 1 m above it.
            Vector3 aPos = Vector3.zero;
            Vector3 pRest = new Vector3(0f, 1f, 0f);
            Vector3 bLocalInP = new Vector3(0f, 0.5f, 0f);
            Vector3 qRest = new Vector3(0f, 2.5f, 0f);

            GhostPlaybackLogic.ComputeServoFollowerRelativePose(aPos, Id, 1f, pRest, Id,
                out Vector3 pRel, out Quaternion pRelRot);
            GhostPlaybackLogic.ComputeServoFollowerRelativePose(pRest + bLocalInP, Id, 1f, qRest, Id,
                out Vector3 qRel, out Quaternion qRelRot);

            // A turns 90 about Z; B stays at rest inside its own part.
            GhostPlaybackLogic.ComputeServoFollowerPose(aPos, Z90, 1f, pRel, pRelRot,
                out Vector3 pPos, out Quaternion pRot);
            AssertVec(new Vector3(-1f, 0f, 0f), pPos);

            // B's container pose is composed through P's new pose (one chain step).
            Vector3 bPos = Vector3.zero;
            Quaternion bRot = Id;
            float bScale = 1f;
            GhostPlaybackLogic.ComposeServoChainStep(ref bPos, ref bRot, ref bScale, bLocalInP, Id, 1f);
            GhostPlaybackLogic.ComposeServoChainStep(ref bPos, ref bRot, ref bScale, pPos, pRot, 1f);

            GhostPlaybackLogic.ComputeServoFollowerPose(bPos, bRot, bScale, qRel, qRelRot,
                out Vector3 qPos, out Quaternion qRot);
            AssertVec(new Vector3(-2.5f, 0f, 0f), qPos);
            AssertRot(Z90, qRot);
        }

        [Fact]
        public void UnitQuaternionInverseUndoesTheRotation()
        {
            Quaternion q = Z90 * Y90;
            AssertRot(Id, GhostPlaybackLogic.UnitQuaternionInverse(q) * q);
            AssertVec(new Vector3(1f, 2f, 3f),
                GhostPlaybackLogic.UnitQuaternionInverse(q) * (q * new Vector3(1f, 2f, 3f)));
        }

        [Fact]
        public void FirstFollowerRepose_IsConsumedOncePerBind()
        {
            var servo = new RoboticGhostInfo { partPersistentId = 4242u };
            Assert.True(GhostPlaybackLogic.ConsumeFirstFollowerRepose(servo));
            Assert.False(GhostPlaybackLogic.ConsumeFirstFollowerRepose(servo));
            Assert.False(GhostPlaybackLogic.ConsumeFirstFollowerRepose(servo));

            // A re-bind re-arms the line (BindServoFollowersCore clears the flag last).
            servo.followerReposeLogged = false;
            Assert.True(GhostPlaybackLogic.ConsumeFirstFollowerRepose(servo));
            Assert.False(GhostPlaybackLogic.ConsumeFirstFollowerRepose(null));
        }

        [Fact]
        public void FollowerReposeLine_IsInvariantAndCarriesTheLaneTokens()
        {
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");
                string line = GhostPlaybackLogic.FormatFollowerReposeLine(
                    1234567u, "ModuleRoboticServoHinge", 1, 45.5f);
                // SRV-1 pins this shape (harness/scenarios/SRV-1-hinge-followers-ghost.toml).
                Assert.Equal("Servo followers re-posed in playback: servoPid=1234567 " +
                    "module=ModuleRoboticServoHinge followers=1 value=45.50 (first since bind)", line);
                Assert.Contains("module=unknown",
                    GhostPlaybackLogic.FormatFollowerReposeLine(1u, null, 2, 0f));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void FirstRotorSpin_IsConsumedOncePerBuild()
        {
            var rotor = new RoboticGhostInfo
            {
                partPersistentId = 777u, moduleName = "ModuleRoboticServoRotor",
                visualMode = RoboticVisualMode.RotorRpm,
            };
            Assert.False(rotor.rotorSpinLogged);
            Assert.True(GhostPlaybackLogic.ConsumeFirstRotorSpin(rotor));
            Assert.False(GhostPlaybackLogic.ConsumeFirstRotorSpin(rotor));
            Assert.False(GhostPlaybackLogic.ConsumeFirstRotorSpin(rotor));
            // A rebuilt ghost carries a fresh info, armed again.
            Assert.True(GhostPlaybackLogic.ConsumeFirstRotorSpin(new RoboticGhostInfo()));
            Assert.False(GhostPlaybackLogic.ConsumeFirstRotorSpin(null));
        }

        [Fact]
        public void RotorSpinLine_IsInvariantAndCarriesTheLaneTokens()
        {
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");
                float delta = GhostPlaybackLogic.ComputeRotorDeltaDegrees(30f, 0.02);
                string line = GhostPlaybackLogic.FormatRotorSpinLine(
                    1234567u, "ModuleRoboticServoRotor", 30f, delta, 1);
                // SRV-2 pins this shape (harness/scenarios/SRV-2-rotor-spin-ghost.toml).
                Assert.Equal("Rotor spin applied in playback: pid=1234567 " +
                    "module=ModuleRoboticServoRotor rpm=30.00 deltaDeg=3.600 followers=1 " +
                    "(first since build)", line);
                Assert.Contains("module=unknown",
                    GhostPlaybackLogic.FormatRotorSpinLine(1u, null, 0f, 0f, 0));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }
    }
}
