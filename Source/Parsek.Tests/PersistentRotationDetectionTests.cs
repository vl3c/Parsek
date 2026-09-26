using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The PersistentRotation detection predicate and the spin-vector frame the recorder
    /// stores. Detection used to require <c>name == "PersistentRotation"</c>, which the only
    /// KSP 1.12 build (PersistentRotationUpgraded 1.9.2.1) never satisfies: its DLL is
    /// <c>PersistentRotationUpgraded.dll</c> and KSP overwrites <c>LoadedAssembly.name</c>
    /// with its KSPAssembly name "Persistent Rotation Upgraded".
    /// </summary>
    public class PersistentRotationDetectionTests
    {
        [Fact]
        public void Upgraded112Build_MatchesOnKspAssemblyName()
        {
            // What AssemblyLoader reports for the 1.12 build: name from [KSPAssembly],
            // dllName from the file name.
            Assert.Equal("Persistent Rotation Upgraded",
                FlightRecorder.MatchPersistentRotationAssembly(
                    "Persistent Rotation Upgraded", "PersistentRotationUpgraded"));
        }

        [Fact]
        public void Upgraded112Build_MatchesOnDllNameAlone()
        {
            Assert.Equal("PersistentRotationUpgraded",
                FlightRecorder.MatchPersistentRotationAssembly(
                    "SomethingElse", "PersistentRotationUpgraded"));
        }

        [Fact]
        public void OriginalBuild_StillMatches()
        {
            Assert.Equal("PersistentRotation",
                FlightRecorder.MatchPersistentRotationAssembly("PersistentRotation", "PersistentRotation"));
        }

        [Theory]
        [InlineData("Parsek", "Parsek")]
        [InlineData("persistentrotation", "persistentrotationupgraded")]
        [InlineData("PersistentRotationX", "XPersistentRotation")]
        [InlineData(null, null)]
        [InlineData("", "")]
        public void UnrelatedOrNearMissNames_DoNotMatch(string name, string dllName)
        {
            Assert.Null(FlightRecorder.MatchPersistentRotationAssembly(name, dllName));
        }

        [Fact]
        public void SpinVector_ReferenceEqualsVessel_IsUnchanged()
        {
            Quaternion rot = TrajectoryMath.PureMultiply(
                TrajectoryMath.PureAngleAxis(40f, Vector3.up),
                TrajectoryMath.PureAngleAxis(25f, Vector3.right));
            Vector3 local = new Vector3(0.1f, 0.5f, -0.2f);

            Vector3 stored = TrajectoryMath.ComputeSpinAngularVelocityVesselLocal(rot, rot, local);

            Assert.Equal((double)(local.x), (double)(stored.x), 4);
            Assert.Equal((double)(local.y), (double)(stored.y), 4);
            Assert.Equal((double)(local.z), (double)(stored.z), 4);
        }

        [Fact]
        public void SpinVector_DecodedByPlayback_RecoversTheWorldSpinAxis()
        {
            // The mirror direction: ComputeOrbitalRotation's spin-forward branch lifts the
            // stored vector with `boundaryWorldRot * angularVelocity`, where boundaryWorldRot
            // is the decoded vessel rotation. That must give back the world angular velocity
            // KSP's reference-local vector describes, also when the control reference is a
            // different part than the vessel transform.
            Quaternion vesselRot = TrajectoryMath.PureAngleAxis(30f, Vector3.forward);
            Quaternion referenceRot = TrajectoryMath.PureMultiply(
                vesselRot, TrajectoryMath.PureAngleAxis(90f, Vector3.up));
            Vector3 referenceLocal = new Vector3(0f, 0f, 0.8f);
            Vector3 world = TrajectoryMath.PureRotateVector(referenceRot, referenceLocal);

            Vector3 stored = TrajectoryMath.ComputeSpinAngularVelocityVesselLocal(
                vesselRot, referenceRot, referenceLocal);
            Vector3 decoded = TrajectoryMath.PureRotateVector(vesselRot, stored);

            Assert.Equal((double)(world.x), (double)(decoded.x), 4);
            Assert.Equal((double)(world.y), (double)(decoded.y), 4);
            Assert.Equal((double)(world.z), (double)(decoded.z), 4);
            Assert.Equal((double)(referenceLocal.magnitude), (double)(stored.magnitude), 4);
            // The pre-fix encode (Inverse(vesselRot) * referenceLocal, treating the local
            // vector as world) lands on a different axis for this pose.
            Vector3 preFix = TrajectoryMath.PureRotateVector(
                TrajectoryMath.PureInverse(vesselRot), referenceLocal);
            Assert.True((preFix - stored).magnitude > 0.5f);
        }
    }
}
