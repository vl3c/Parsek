using System;
using Parsek.TestCommands;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The ghost canopy's airflow aim (todo GHOST-CHUTE-CANOPY-AIM): stock's
    /// <c>ModuleParachute.FixedUpdate</c> points an open canopy along
    /// <c>(invertCanopy ? -1 : 1) * dragVectorDir</c> with the part's +Z (or, in a symmetry group,
    /// the direction to the vessel's centre) as the up hint, tilts a group outward by
    /// <c>Clamp(spreadAngle * symmetryCount, 0, 45)</c> (a third while semi-deployed) and turns
    /// toward it at <c>rotationSpeedDPS</c>. These pin the pure halves of the ghost's copy.
    /// </summary>
    public class CanopyAimTests
    {
        private static readonly Vector3 Up = new Vector3(0f, 1f, 0f);
        private static readonly Vector3 Zero = new Vector3(0f, 0f, 0f);

        private static void AssertVec(Vector3 expected, Vector3 actual)
        {
            Assert.InRange(actual.x, expected.x - 1e-5f, expected.x + 1e-5f);
            Assert.InRange(actual.y, expected.y - 1e-5f, expected.y + 1e-5f);
            Assert.InRange(actual.z, expected.z - 1e-5f, expected.z + 1e-5f);
        }

        [Fact]
        public void FallingChute_InvertedCanopy_PointsUpAgainstTheAirflow()
        {
            // Stock default invertCanopy = true: a part falling at 6 m/s has dragVectorDir = down,
            // so the canopy's forward is up.
            var source = GhostPlaybackLogic.ResolveCanopyAimForward(
                new Vector3(0f, -6f, 0f), true, Up, true, Up, invertCanopy: true, out Vector3 aim);
            Assert.Equal(CanopyAimSource.Airflow, source);
            AssertVec(Up, aim);
        }

        [Fact]
        public void NonInvertedCanopy_PointsAlongTheAirflow()
        {
            var source = GhostPlaybackLogic.ResolveCanopyAimForward(
                new Vector3(0f, -6f, 0f), true, Up, true, Up, invertCanopy: false, out Vector3 aim);
            Assert.Equal(CanopyAimSource.Airflow, source);
            AssertVec(new Vector3(0f, -1f, 0f), aim);
        }

        [Fact]
        public void DriftingChute_TrailsBehindTheHorizontalMotion()
        {
            // Moving east at 3 m/s while falling 4 m/s: the canopy trails up and west.
            GhostPlaybackLogic.ResolveCanopyAimForward(
                new Vector3(3f, -4f, 0f), true, Up, true, Up, true, out Vector3 aim);
            AssertVec(new Vector3(-0.6f, 0.8f, 0f), aim);
        }

        [Fact]
        public void UnknownVelocity_FallsBackToGravityUp()
        {
            Vector3 gravityUp = new Vector3(0f, 0f, 2f);
            var source = GhostPlaybackLogic.ResolveCanopyAimForward(
                new Vector3(0f, -6f, 0f), false, gravityUp, true, Up, true, out Vector3 aim);
            Assert.Equal(CanopyAimSource.Gravity, source);
            AssertVec(new Vector3(0f, 0f, 1f), aim);
        }

        [Fact]
        public void BelowTheAirspeedFloor_FallsBackToGravityUp()
        {
            var source = GhostPlaybackLogic.ResolveCanopyAimForward(
                new Vector3(0.1f, 0f, 0f), true, Up, true, new Vector3(1f, 0f, 0f), true, out Vector3 aim);
            Assert.Equal(CanopyAimSource.Gravity, source);
            AssertVec(Up, aim);
        }

        [Fact]
        public void NonFiniteVelocity_IsNotAirflow()
        {
            var source = GhostPlaybackLogic.ResolveCanopyAimForward(
                new Vector3(float.NaN, -6f, 0f), true, Up, true, Up, true, out Vector3 _);
            Assert.Equal(CanopyAimSource.Gravity, source);
        }

        [Fact]
        public void NoVelocityNoBody_FallsBackToThePartAxis()
        {
            Vector3 partUp = new Vector3(0f, 0f, -3f);
            var source = GhostPlaybackLogic.ResolveCanopyAimForward(
                Zero, false, Zero, false, partUp, true, out Vector3 aim);
            Assert.Equal(CanopyAimSource.PartUp, source);
            AssertVec(new Vector3(0f, 0f, -1f), aim);

            GhostPlaybackLogic.ResolveCanopyAimForward(Zero, false, Zero, false, partUp, false, out aim);
            AssertVec(new Vector3(0f, 0f, 1f), aim);
        }

        [Fact]
        public void DegeneratePartAxis_StillReturnsAUnitAim()
        {
            GhostPlaybackLogic.ResolveCanopyAimForward(Zero, false, Zero, false, Zero, true, out Vector3 aim);
            AssertVec(Up, aim);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 2)]
        [InlineData(3, 4)]
        public void SymmetryCount_IsCounterpartsPlusOne(int counterparts, int expected)
        {
            Assert.Equal(expected, GhostPlaybackLogic.StockParachuteSymmetryCount(counterparts));
        }

        [Fact]
        public void Spread_FollowsStock()
        {
            // 4x symmetry, spreadAngle 7: 28 deg open, a third semi-deployed.
            Assert.Equal(28f, GhostPlaybackLogic.ResolveCanopySpreadDeg(7f, 4, CanopyPoseStage.Deployed), 1e-4f);
            Assert.Equal(28f / 3f, GhostPlaybackLogic.ResolveCanopySpreadDeg(7f, 4, CanopyPoseStage.SemiDeployed), 1e-4f);
            // Capped at 45 deg.
            Assert.Equal(45f, GhostPlaybackLogic.ResolveCanopySpreadDeg(7f, 8, CanopyPoseStage.Deployed), 4f);
            // No group, no spread angle, or a stowed canopy: none.
            Assert.Equal(0f, GhostPlaybackLogic.ResolveCanopySpreadDeg(7f, 0, CanopyPoseStage.Deployed));
            Assert.Equal(0f, GhostPlaybackLogic.ResolveCanopySpreadDeg(0f, 4, CanopyPoseStage.Deployed));
            Assert.Equal(0f, GhostPlaybackLogic.ResolveCanopySpreadDeg(float.NaN, 4, CanopyPoseStage.Deployed));
            Assert.Equal(0f, GhostPlaybackLogic.ResolveCanopySpreadDeg(7f, 4, CanopyPoseStage.Stowed));
        }

        [Fact]
        public void UpHint_IsThePartForward_OutsideASymmetryGroup()
        {
            Vector3 fwd = new Vector3(0f, 0f, 1f);
            AssertVec(fwd, GhostPlaybackLogic.ResolveCanopyAimUpHint(
                fwd, new Vector3(2f, 0f, 0f), Zero, symmetryCount: 0));
        }

        [Fact]
        public void UpHint_PointsAtTheVesselCentre_InASymmetryGroup()
        {
            Vector3 fwd = new Vector3(0f, 0f, 1f);
            AssertVec(new Vector3(-1f, 0f, 0f), GhostPlaybackLogic.ResolveCanopyAimUpHint(
                fwd, new Vector3(2f, 0f, 0f), Zero, symmetryCount: 4));
            // Part at the centre: no direction, the part forward again.
            AssertVec(fwd, GhostPlaybackLogic.ResolveCanopyAimUpHint(fwd, Zero, Zero, symmetryCount: 4));
        }

        [Fact]
        public void Step_SnapsOnTheFirstAimAndOnUnknownOrBackwardTime()
        {
            Assert.Equal(GhostPlaybackLogic.CanopyAimStep.Snap,
                GhostPlaybackLogic.ResolveCanopyAimStep(false, 10.0, 10.1, 90f, out _));
            Assert.Equal(GhostPlaybackLogic.CanopyAimStep.Snap,
                GhostPlaybackLogic.ResolveCanopyAimStep(true, double.NaN, 10.1, 90f, out _));
            Assert.Equal(GhostPlaybackLogic.CanopyAimStep.Snap,
                GhostPlaybackLogic.ResolveCanopyAimStep(true, 10.0, double.NaN, 90f, out _));
            Assert.Equal(GhostPlaybackLogic.CanopyAimStep.Snap,
                GhostPlaybackLogic.ResolveCanopyAimStep(true, 10.0, 9.0, 90f, out _));
            // A gap longer than the synth sample cap (warp, a hitch): snap rather than lag.
            Assert.Equal(GhostPlaybackLogic.CanopyAimStep.Snap,
                GhostPlaybackLogic.ResolveCanopyAimStep(true, 10.0, 12.0, 90f, out _));
            Assert.Equal(GhostPlaybackLogic.CanopyAimStep.Snap,
                GhostPlaybackLogic.ResolveCanopyAimStep(true, 10.0, 10.1, 0f, out _));
        }

        [Fact]
        public void Step_HoldsOnTheSameUT()
        {
            Assert.Equal(GhostPlaybackLogic.CanopyAimStep.Hold,
                GhostPlaybackLogic.ResolveCanopyAimStep(true, 10.0, 10.0, 90f, out float step));
            Assert.Equal(0f, step);
        }

        [Fact]
        public void Step_TurnsAtTheModuleRate()
        {
            Assert.Equal(GhostPlaybackLogic.CanopyAimStep.Step,
                GhostPlaybackLogic.ResolveCanopyAimStep(true, 10.0, 10.5, 90f, out float step));
            Assert.Equal(45f, step, 1e-3f);
        }

        [Fact]
        public void BeginWithoutACanopy_DoesNotArm()
        {
            var info = new ParachuteGhostInfo { aimCanopy = true, canopyTransform = null };
            GhostPlaybackLogic.BeginCanopyAim(null, info, CanopyPoseStage.Deployed);
            Assert.Equal(CanopyPoseStage.Stowed, info.canopyAimStage);
        }

        [Fact]
        public void StopCanopyAim_ClearsTheAim()
        {
            var info = new ParachuteGhostInfo
            {
                canopyAimStage = CanopyPoseStage.Deployed,
                canopyAimSeeded = true,
                canopyAimUT = 12.0
            };
            GhostPlaybackLogic.StopCanopyAim(info);
            Assert.Equal(CanopyPoseStage.Stowed, info.canopyAimStage);
            Assert.False(info.canopyAimSeeded);
            Assert.True(double.IsNaN(info.canopyAimUT));
        }

        [Fact]
        public void UpdateWithNoGhost_IsANoOp()
        {
            var state = new GhostPlaybackState();
            state.parachuteInfos = new System.Collections.Generic.Dictionary<uint, ParachuteGhostInfo>
            {
                { 1u, new ParachuteGhostInfo { canopyAimStage = CanopyPoseStage.Deployed } }
            };
            GhostPlaybackLogic.UpdateParachuteCanopyAim(state, 1.0);
            GhostPlaybackLogic.UpdateParachuteCanopyAim(null, 1.0);
        }

        // PARTPARITY-RESTOCK-SEMI-READBACK: a rigged canopy keeps one root scale while its
        // bones open it, so the read-back needs the bones to tell semi from full.
        [Fact]
        public void ReadBack_RiggedSemiCanopy_ReadsSemi()
        {
            Assert.Equal(PartParityStateRules.StateSemi, PartParityStateDriver.ClassifyCanopyStage(
                fullScale: true, fullBones: false, semiSampled: true, semiScale: true, semiBones: true));
        }

        [Fact]
        public void ReadBack_RiggedFullCanopy_ReadsFull()
        {
            Assert.Equal(PartParityStateRules.StateFull, PartParityStateDriver.ClassifyCanopyStage(
                fullScale: true, fullBones: true, semiSampled: true, semiScale: true, semiBones: false));
        }

        [Fact]
        public void ReadBack_StockCanopies_ReadByScale()
        {
            Assert.Equal(PartParityStateRules.StateFull, PartParityStateDriver.ClassifyCanopyStage(
                true, true, true, false, true));
            Assert.Equal(PartParityStateRules.StateSemi, PartParityStateDriver.ClassifyCanopyStage(
                false, true, true, true, true));
            Assert.Equal(PartParityStateRules.StateStowed, PartParityStateDriver.ClassifyCanopyStage(
                false, true, true, false, true));
            // Semi not sampled: a semi-sized canopy is not read as semi.
            Assert.Equal(PartParityStateRules.StateStowed, PartParityStateDriver.ClassifyCanopyStage(
                false, true, false, true, true));
        }
    }
}
