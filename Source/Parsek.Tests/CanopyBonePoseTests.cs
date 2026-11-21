using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Rigged parachute canopies (ReStock's Mk16-XL) inflate by moving bones below the canopy
    /// transform. The ghost samples every bone either deploy clip moves and poses it per stage;
    /// these pin the pure halves: which bones count as animated, and which pose a stage picks.
    /// </summary>
    public class CanopyBonePoseTests
    {
        private static readonly Vector3 RestPos = new Vector3(0f, 1f, 0f);
        private static readonly Quaternion RestRot = new Quaternion(0f, 0f, 0f, 1f);
        private static readonly Vector3 RestScale = new Vector3(1f, 1f, 1f);

        [Fact]
        public void IdenticalPose_IsNotAnimated()
        {
            Assert.False(GhostVisualBuilder.CanopyBonePoseDiffers(
                RestPos, RestRot, RestScale, RestPos, RestRot, RestScale));
        }

        [Fact]
        public void SamplingNoise_IsNotAnimated()
        {
            Assert.False(GhostVisualBuilder.CanopyBonePoseDiffers(
                RestPos, RestRot, RestScale,
                new Vector3(0f, 1.00005f, 0f), RestRot, new Vector3(1.00005f, 1f, 1f)));
        }

        [Fact]
        public void MovedBone_IsAnimated()
        {
            Assert.True(GhostVisualBuilder.CanopyBonePoseDiffers(
                RestPos, RestRot, RestScale, new Vector3(0.4f, 1f, 0f), RestRot, RestScale));
        }

        [Fact]
        public void ScaledBone_IsAnimated()
        {
            // A folded canopy panel bone opening from zero scale.
            Assert.True(GhostVisualBuilder.CanopyBonePoseDiffers(
                RestPos, RestRot, Vector3.zero, RestPos, RestRot, RestScale));
        }

        [Fact]
        public void RotatedBone_IsAnimated()
        {
            // About 10 degrees around X: (sin 5 deg, 0, 0, cos 5 deg).
            var rotated = new Quaternion(0.0871557f, 0f, 0f, 0.9961947f);
            Assert.True(GhostVisualBuilder.CanopyBonePoseDiffers(
                RestPos, RestRot, RestScale, RestPos, rotated, RestScale));
        }

        [Fact]
        public void NegatedQuaternion_IsTheSameRotation()
        {
            // q and -q are the same rotation; an animation curve may flip the sign.
            var negated = new Quaternion(0f, 0f, 0f, -1f);
            Assert.False(GhostVisualBuilder.CanopyBonePoseDiffers(
                RestPos, RestRot, RestScale, RestPos, negated, RestScale));
        }

        private static CanopyBonePose Bone(bool semiSampled)
        {
            return new CanopyBonePose
            {
                stowedPos = new Vector3(1f, 0f, 0f),
                stowedRot = RestRot,
                stowedScale = Vector3.zero,
                semiDeployedSampled = semiSampled,
                semiPos = new Vector3(2f, 0f, 0f),
                semiRot = RestRot,
                semiScale = new Vector3(0.5f, 0.5f, 0.5f),
                deployedPos = new Vector3(3f, 0f, 0f),
                deployedRot = RestRot,
                deployedScale = RestScale
            };
        }

        [Theory]
        [InlineData((int)CanopyPoseStage.Stowed, 1f, 0f)]
        [InlineData((int)CanopyPoseStage.SemiDeployed, 2f, 0.5f)]
        [InlineData((int)CanopyPoseStage.Deployed, 3f, 1f)]
        public void EachStage_PicksItsOwnPose(int stage, float expectedX, float expectedScale)
        {
            Assert.True(GhostPlaybackLogic.TrySelectCanopyBonePose(
                Bone(semiSampled: true), (CanopyPoseStage)stage, out Vector3 pos, out _, out Vector3 scale));
            Assert.Equal(expectedX, pos.x);
            Assert.Equal(expectedScale, scale.x);
        }

        [Fact]
        public void UnsampledSemiStage_LeavesTheBone()
        {
            Assert.False(GhostPlaybackLogic.TrySelectCanopyBonePose(
                Bone(semiSampled: false), CanopyPoseStage.SemiDeployed, out _, out _, out _));
        }

        [Fact]
        public void NullBone_SelectsNothing()
        {
            Assert.False(GhostPlaybackLogic.TrySelectCanopyBonePose(
                null, CanopyPoseStage.Deployed, out _, out _, out _));
        }

        [Fact]
        public void ApplyWithoutBones_IsANoOp()
        {
            // Stock canopies carry no bone list; the applier must tolerate both null shapes.
            GhostPlaybackLogic.ApplyCanopyBonePoses(null, CanopyPoseStage.Deployed);
            GhostPlaybackLogic.ApplyCanopyBonePoses(new ParachuteGhostInfo(), CanopyPoseStage.Deployed);
        }
    }
}
