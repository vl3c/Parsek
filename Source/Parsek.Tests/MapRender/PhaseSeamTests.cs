using Parsek.MapRender;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-1 guard for <see cref="PhaseSeam"/> + <see cref="PhaseSeamClassifier"/> (design §6.1).
    /// Covers the seam factories' kind/continuity, the body-change-vs-rigid-vs-switch classification,
    /// and the G1 <c>rigid-seam-tangent-discontinuity</c> predicate the descent re-stitch (Phase 6)
    /// enforces.
    ///
    /// Each assertion states the bug it catches: a wrong classification would assert G1 continuity on
    /// an SOI boundary (false anomalies) or accept a real orbit↔landing discontinuity; a wrong tangent
    /// predicate would either flag a continuous landing seam or miss a real kink.
    /// </summary>
    public class PhaseSeamTests
    {
        // ---- Factories ----

        [Fact]
        public void RigidFactory_IsRigidG1_RequiresTangentMatch()
        {
            var seam = PhaseSeam.Rigid();
            Assert.Equal(PhaseSeamKind.Rigid, seam.Kind);
            Assert.Equal(ContinuityOrder.G1, seam.Continuity);
            Assert.True(seam.RequiresTangentMatch);
            Assert.True(seam.OnCamera);
            Assert.Null(seam.Crossing);
        }

        [Fact]
        public void FlexibleSoiFactory_IsFlexibleG0_CarriesCrossing()
        {
            var crossing = new SoiCrossing("Kerbin", "Sun", crossingUt: 1000, soiRadius: 8.4e7);
            var seam = PhaseSeam.FlexibleSoi(crossing);
            Assert.Equal(PhaseSeamKind.FlexibleSoi, seam.Kind);
            Assert.Equal(ContinuityOrder.G0, seam.Continuity);
            Assert.False(seam.RequiresTangentMatch);
            Assert.Same(crossing, seam.Crossing);
        }

        [Fact]
        public void SwitchContinuationFactory_IsSwitchG0()
        {
            var seam = PhaseSeam.SwitchContinuation();
            Assert.Equal(PhaseSeamKind.SwitchContinuation, seam.Kind);
            Assert.Equal(ContinuityOrder.G0, seam.Continuity);
            Assert.False(seam.RequiresTangentMatch);
        }

        // ---- Kind classification ----

        [Fact]
        public void Classify_BodyChange_IsFlexibleSoi_EvenForRigidKinds()
        {
            // A body change wins over the rigid join: an SOI crossing is never a G1 rigid seam.
            var kind = PhaseSeamClassifier.Classify(
                PhaseKind.Ascent, PhaseKind.DepartureLoiter,
                leadingBody: "Kerbin", trailingBody: "Sun",
                isMemberSwitchBoundary: false);
            Assert.Equal(PhaseSeamKind.FlexibleSoi, kind);
        }

        [Fact]
        public void Classify_AscentToOrbit_SameBody_IsRigid()
        {
            var kind = PhaseSeamClassifier.Classify(
                PhaseKind.Ascent, PhaseKind.DepartureLoiter,
                leadingBody: "Kerbin", trailingBody: "Kerbin",
                isMemberSwitchBoundary: false);
            Assert.Equal(PhaseSeamKind.Rigid, kind);
        }

        [Fact]
        public void Classify_OrbitToLanding_SameBody_IsRigid_BothDirections()
        {
            Assert.Equal(PhaseSeamKind.Rigid, PhaseSeamClassifier.Classify(
                PhaseKind.ArrivalLoiter, PhaseKind.Descent, "Duna", "Duna", false));
            // Symmetric: the classifier treats the join as undirected.
            Assert.Equal(PhaseSeamKind.Rigid, PhaseSeamClassifier.Classify(
                PhaseKind.Descent, PhaseKind.ArrivalLoiter, "Duna", "Duna", false));
        }

        [Fact]
        public void Classify_MemberSwitch_IsSwitchContinuation_OverridesEverything()
        {
            var kind = PhaseSeamClassifier.Classify(
                PhaseKind.Ascent, PhaseKind.DepartureLoiter,
                leadingBody: "Kerbin", trailingBody: "Sun",
                isMemberSwitchBoundary: true);
            Assert.Equal(PhaseSeamKind.SwitchContinuation, kind);
        }

        [Fact]
        public void Classify_NonRigidSameBodyJoin_IsNone()
        {
            // Loiter -> Transfer same body is not a distinguished rigid seam.
            var kind = PhaseSeamClassifier.Classify(
                PhaseKind.DepartureLoiter, PhaseKind.HeliocentricTransfer,
                leadingBody: "Sun", trailingBody: "Sun",
                isMemberSwitchBoundary: false);
            Assert.Equal(PhaseSeamKind.None, kind);
        }

        // ---- G1 tangent discontinuity predicate ----

        [Fact]
        public void TangentDiscontinuity_AlignedTangents_NoAnomaly()
        {
            Assert.False(PhaseSeamClassifier.IsRigidSeamTangentDiscontinuity(
                new Vector3(1, 0, 0), new Vector3(2, 0, 0)));
        }

        [Fact]
        public void TangentDiscontinuity_SmallAngleWithinTolerance_NoAnomaly()
        {
            // ~2.9 degrees < the 0.1 rad (~5.7 deg) default tolerance.
            var a = new Vector3(1, 0, 0);
            var b = new Vector3(Mathf.Cos(0.05f), Mathf.Sin(0.05f), 0);
            Assert.False(PhaseSeamClassifier.IsRigidSeamTangentDiscontinuity(a, b));
        }

        [Fact]
        public void TangentDiscontinuity_LargeAngle_FlagsAnomaly()
        {
            // 90 degrees >> tolerance.
            Assert.True(PhaseSeamClassifier.IsRigidSeamTangentDiscontinuity(
                new Vector3(1, 0, 0), new Vector3(0, 1, 0)));
        }

        [Fact]
        public void TangentDiscontinuity_CustomTolerance_Respected()
        {
            // 0.05 rad angle, tolerance 0.01 -> over.
            var a = new Vector3(1, 0, 0);
            var b = new Vector3(Mathf.Cos(0.05f), Mathf.Sin(0.05f), 0);
            Assert.True(PhaseSeamClassifier.IsRigidSeamTangentDiscontinuity(a, b, toleranceRadians: 0.01));
        }

        [Fact]
        public void TangentDiscontinuity_ZeroOrNonFiniteTangent_NoFalseAnomaly()
        {
            // Unmeasurable tangent -> no anomaly (mirrors the oracle's no-false-positive contract).
            Assert.False(PhaseSeamClassifier.IsRigidSeamTangentDiscontinuity(
                Vector3.zero, new Vector3(1, 0, 0)));
            Assert.False(PhaseSeamClassifier.IsRigidSeamTangentDiscontinuity(
                new Vector3(1, 0, 0), Vector3.zero));
            Assert.False(PhaseSeamClassifier.IsRigidSeamTangentDiscontinuity(
                new Vector3(float.NaN, 0, 0), new Vector3(1, 0, 0)));
        }

        [Fact]
        public void TangentDiscontinuity_NonFiniteTolerance_FallsBackToDefault()
        {
            // 90 deg is over the default; a NaN tolerance must fall back to the default, not pass-through.
            Assert.True(PhaseSeamClassifier.IsRigidSeamTangentDiscontinuity(
                new Vector3(1, 0, 0), new Vector3(0, 1, 0), toleranceRadians: double.NaN));
        }

        [Fact]
        public void KindToken_IsGrepStable()
        {
            Assert.Equal("rigid", PhaseSeamClassifier.KindToken(PhaseSeamKind.Rigid));
            Assert.Equal("flexible-soi", PhaseSeamClassifier.KindToken(PhaseSeamKind.FlexibleSoi));
            Assert.Equal("switch-continuation", PhaseSeamClassifier.KindToken(PhaseSeamKind.SwitchContinuation));
            Assert.Equal("none", PhaseSeamClassifier.KindToken(PhaseSeamKind.None));
        }
    }
}
