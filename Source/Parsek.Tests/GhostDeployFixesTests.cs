using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// GHOST-SCANNER-STATE-NO-INFO (deploy-driven animation-group loops and the colour-only
    /// animate-generic deploy) and GHOST-RESTOCK-COMMDISH-DEPLOY-POSE (a pose move under a
    /// scaled parent). Every cell is headless: loops and deployables are built with no
    /// transforms, colours with no material.
    /// </summary>
    [Collection("Sequential")]
    public class GhostDeployFixesTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostDeployFixesTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static GhostPlaybackState StateWithLoops(params ConverterLoopGhostInfo[] loops)
        {
            return new GhostPlaybackState
            {
                vesselName = "DeployFixVessel",
                synthesizedMotionInfos = new SynthesizedMotionGhostInfos
                {
                    converterLoops = new List<ConverterLoopGhostInfo>(loops),
                },
            };
        }

        #region Animation-group loop driver

        [Theory]
        [InlineData(false, false, false)] // a drill / ISRU: only converters report active
        [InlineData(false, true, true)]   // the orbital scanner: ModuleResourceScanner / ModuleGPS
        [InlineData(true, false, true)]   // alwaysActive
        [InlineData(true, true, true)]
        public void AnimationGroupLoopFollowsDeploy_MatchesStockCheckForActivity(
            bool alwaysActive, bool nonConverterModule, bool expected)
        {
            Assert.Equal(expected,
                GhostVisualBuilder.AnimationGroupLoopFollowsDeploy(alwaysActive, nonConverterModule));
        }

        [Fact]
        public void DeployDrivenLoop_StartsOnDeployAtEventUT_StopsOnRetract()
        {
            var loop = new ConverterLoopGhostInfo { partPersistentId = 7u, followsDeploy = true };
            var state = StateWithLoops(loop);

            Assert.True(GhostPlaybackLogic.HasDeployDrivenLoop(state, 7u));
            Assert.Equal(GhostPartEventOutcome.Applied,
                GhostPlaybackLogic.ApplyDeployDrivenLoopStateWithOutcome(state, 7u, true, 120.5));
            Assert.True(loop.active);
            Assert.Equal(120.5, loop.activeSinceUT);

            // A start-UT seed after the snapshot baseline keeps the first phase origin.
            Assert.Equal(GhostPartEventOutcome.AlreadyInState,
                GhostPlaybackLogic.ApplyDeployDrivenLoopStateWithOutcome(state, 7u, true, 300.0));
            Assert.Equal(120.5, loop.activeSinceUT);

            Assert.Equal(GhostPartEventOutcome.Applied,
                GhostPlaybackLogic.ApplyDeployDrivenLoopStateWithOutcome(state, 7u, false, 400.0));
            Assert.False(loop.active);
        }

        [Fact]
        public void ConverterEdges_AndDeployEvents_EachDriveOnlyTheirOwnLoops()
        {
            var deployLoop = new ConverterLoopGhostInfo { partPersistentId = 7u, followsDeploy = true };
            var converterLoop = new ConverterLoopGhostInfo { partPersistentId = 8u, followsDeploy = false };
            var state = StateWithLoops(deployLoop, converterLoop);

            // A converter edge never reaches the scanner's loop ...
            Assert.Equal(GhostPartEventOutcome.NoInfoForPart,
                GhostPlaybackLogic.ApplyConverterLoopStateWithOutcome(state, 7u, true, 10.0));
            Assert.False(deployLoop.active);
            // ... and a deploy never spins a drill.
            Assert.False(GhostPlaybackLogic.HasDeployDrivenLoop(state, 8u));
            Assert.Equal(GhostPartEventOutcome.NoInfoForPart,
                GhostPlaybackLogic.ApplyDeployDrivenLoopStateWithOutcome(state, 8u, true, 10.0));
            Assert.False(converterLoop.active);

            Assert.Equal(GhostPartEventOutcome.Applied,
                GhostPlaybackLogic.ApplyConverterLoopStateWithOutcome(state, 8u, true, 10.0));
            Assert.True(converterLoop.active);
        }

        [Fact]
        public void RecordDeployDrivenLoop_TalliesOnlyAPartThatHasOne()
        {
            var loop = new ConverterLoopGhostInfo { partPersistentId = 7u, followsDeploy = true };
            var state = StateWithLoops(loop);

            var plain = new GhostPartEventApplyTally();
            GhostPlaybackLogic.RecordDeployDrivenLoop(plain, state,
                new PartEvent { partPersistentId = 9u, eventType = PartEventType.DeployableExtended, ut = 5.0 },
                deployed: true);
            Assert.Equal(0, plain.DistinctLineCount);

            var scanner = new GhostPartEventApplyTally();
            GhostPlaybackLogic.RecordDeployDrivenLoop(scanner, state,
                new PartEvent { partPersistentId = 7u, eventType = PartEventType.DeployableExtended, ut = 5.0 },
                deployed: true);
            Assert.Equal(1, scanner.DistinctLineCount);
            Assert.True(loop.active);
            Assert.Equal(5.0, loop.activeSinceUT);
        }

        [Fact]
        public void SnapshotBaseline_DeployedAnimationGroup_StartsTheDeployDrivenLoop()
        {
            var loop = new ConverterLoopGhostInfo { partPersistentId = 7u, followsDeploy = true };
            var state = StateWithLoops(loop);
            state.snapshotBaselines = new Dictionary<uint, SnapshotPartBaseline>
            {
                { 7u, new SnapshotPartBaseline { animationGroupDeployed = true } },
            };

            GhostPlaybackLogic.ApplySnapshotBaselines(state);

            Assert.True(loop.active);
            Assert.Equal(0.0, loop.activeSinceUT);
            Assert.Contains(logLines, l => l.Contains("[GhostVisual]")
                && l.Contains("Snapshot baseline applied") && l.Contains("deployLoops=1"));
        }

        [Fact]
        public void SnapshotBaseline_StowedAnimationGroup_LeavesTheLoopStopped()
        {
            var loop = new ConverterLoopGhostInfo { partPersistentId = 7u, followsDeploy = true };
            var state = StateWithLoops(loop);
            state.snapshotBaselines = new Dictionary<uint, SnapshotPartBaseline>
            {
                { 7u, new SnapshotPartBaseline { animationGroupDeployed = false } },
            };

            GhostPlaybackLogic.ApplySnapshotBaselines(state);

            Assert.False(loop.active);
            Assert.Contains(logLines, l => l.Contains("Snapshot baseline applied") && l.Contains("deployLoops=0"));
        }

        #endregion

        #region Colour-only animate-generic deploy

        [Theory]
        [InlineData("ModuleAnimateGeneric", "screenon", "screenon", true)]
        [InlineData("ModuleAnimateGeneric", "screenon", null, false)]   // part has a dedicated handler
        [InlineData("ModuleAnimateGeneric", "glow", "screenon", false)] // another clip
        [InlineData("FXModuleAnimateThrottle", "screenon", "screenon", false)]
        public void IsAnimateGenericDeployColorClip_OnlyTheStandaloneDeployClip(
            string module, string anim, string deployAnim, bool expected)
        {
            Assert.Equal(expected, GhostVisualBuilder.IsAnimateGenericDeployColorClip(module, anim, deployAnim));
        }

        [Fact]
        public void ResolveDeployableColor_EndsExactAndMidLerped()
        {
            var off = new Color(0f, 0f, 0f, 1f);
            var on = new Color(1f, 0.5f, 0.2f, 1f);
            Assert.Equal(off, GhostPlaybackLogic.ResolveDeployableColor(off, on, 0f));
            Assert.Equal(on, GhostPlaybackLogic.ResolveDeployableColor(off, on, 1f));
            Color mid = GhostPlaybackLogic.ResolveDeployableColor(off, on, 0.5f);
            Assert.Equal(0.5, (double)mid.r, 4);
            Assert.Equal(0.25, (double)mid.g, 4);
            Assert.Equal(0.1, (double)mid.b, 4);
        }

        [Fact]
        public void HasDeployablePose_CountsTransformsOrColours()
        {
            Assert.False(GhostPlaybackLogic.HasDeployablePose(null));
            Assert.False(GhostPlaybackLogic.HasDeployablePose(new DeployableGhostInfo
            {
                transforms = new List<DeployableTransformState>(),
            }));
            Assert.True(GhostPlaybackLogic.HasDeployablePose(new DeployableGhostInfo
            {
                transforms = new List<DeployableTransformState>(),
                colors = new List<DeployableColorState> { new DeployableColorState() },
            }));
        }

        [Fact]
        public void ColourOnlyDeployable_AppliesAndReachesTheDeployedEnd()
        {
            var info = new DeployableGhostInfo
            {
                partPersistentId = 5u,
                transforms = new List<DeployableTransformState>(),
                colors = new List<DeployableColorState>
                {
                    new DeployableColorState { property = "_EmissiveColor", stowed = Color.black, deployed = Color.white },
                },
            };
            var state = new GhostPlaybackState
            {
                deployableInfos = new Dictionary<uint, DeployableGhostInfo> { { 5u, info } },
            };

            Assert.Equal(GhostPartEventOutcome.Applied,
                GhostPlaybackLogic.ApplyDeployableStateWithOutcome(
                    state, new PartEvent { partPersistentId = 5u, ut = 10.0 }, deployed: true, immediate: true));
            Assert.Equal(1f, info.deployFraction);
            Assert.True(info.currentDeployed);

            // A duplicate deploy on a colour-only info is still an apply, not a missing visual.
            Assert.Equal(GhostPartEventOutcome.Applied,
                GhostPlaybackLogic.ApplyDeployableStateWithOutcome(
                    state, new PartEvent { partPersistentId = 5u, ut = 11.0 }, deployed: true, immediate: false));
        }

        #endregion

        #region Pose delta under a scaled parent

        [Fact]
        public void IsSampledPoseDelta_KeepsAMoveTheRootFrameSees()
        {
            // ReStock commDish 'Dish/center': 0.0051 local units under a 100x parent.
            float local = 0.0051f * 0.0051f;
            float root = (0.0051f * 100f) * (0.0051f * 100f);
            Assert.False(GhostVisualBuilder.IsSampledPoseDelta(local, local, 0f, 0f));
            Assert.True(GhostVisualBuilder.IsSampledPoseDelta(local, root, 0f, 0f));
        }

        [Fact]
        public void IsSampledPoseDelta_EveryLocallyKeptChangeStaysKept()
        {
            Assert.True(GhostVisualBuilder.IsSampledPoseDelta(0.01f, 0f, 0f, 0f));
            Assert.True(GhostVisualBuilder.IsSampledPoseDelta(0f, 0f, 1f, 0f));
            Assert.True(GhostVisualBuilder.IsSampledPoseDelta(0f, 0f, 0f, 0.01f));
            Assert.False(GhostVisualBuilder.IsSampledPoseDelta(0f, 0f, 0.001f, 0f));
        }

        #endregion
    }
}
