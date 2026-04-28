using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the spawn-time stow baseline in
    /// <see cref="GhostPlaybackLogic.PopulateGhostInfoDictionaries"/>.
    ///
    /// Bug: stock retractable ladders (and any deployable whose prefab default
    /// pose is the deployed state) rendered extended in the ghost even when
    /// the recorded vessel had them stowed. The recorder correctly emits NO
    /// seed event for stowed ladders (only deployed ladders get a
    /// <c>DeployableExtended</c> seed event from <see cref="PartStateSeeder"/>),
    /// so without an explicit stow at ghost-spawn time the ghost inherits the
    /// prefab's default pose. The fix mirrors the loop-rewind baseline in
    /// <see cref="GhostPlaybackLogic.ReapplySpawnTimeModuleBaselinesForLoopCycle"/>:
    /// every entry in <c>state.deployableInfos</c> is set to its stowed pose
    /// at spawn, and <c>DeployableExtended</c> seed events at <c>startUT</c>
    /// re-deploy already-extended parts on the first frame applied.
    /// </summary>
    [Collection("Sequential")]
    public class GhostSpawnDeployableBaselineTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostSpawnDeployableBaselineTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            DiagnosticsState.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            DiagnosticsState.ResetForTesting();
        }

        [Fact]
        public void PopulateGhostInfoDictionaries_DeployableInfosPresent_LogsStowBaseline()
        {
            // Three deployables in the build result. Transforms are null (we
            // can't construct Unity Transforms in a unit test), so
            // ApplyDeployableState returns false on each and the per-info
            // applied count stays 0 — but the iteration log still fires
            // because deployableInfos.Count > 0. That log is the testable
            // signal that the new spawn baseline ran.
            var result = new GhostBuildResult
            {
                deployableInfos = new List<DeployableGhostInfo>
                {
                    new DeployableGhostInfo
                    {
                        partPersistentId = 100000,
                        transforms = new List<DeployableTransformState>()
                    },
                    new DeployableGhostInfo
                    {
                        partPersistentId = 101111,
                        transforms = new List<DeployableTransformState>()
                    },
                    new DeployableGhostInfo
                    {
                        partPersistentId = 102222,
                        transforms = new List<DeployableTransformState>()
                    },
                },
            };

            var state = new GhostPlaybackState { vesselName = "TestLadderVessel" };

            GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, result, traj: null);

            Assert.NotNull(state.deployableInfos);
            Assert.Equal(3, state.deployableInfos.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostVisual]") &&
                l.Contains("Spawn baseline: stowed") &&
                l.Contains("0/3 deployable(s)") &&
                l.Contains("TestLadderVessel"));
        }

        [Fact]
        public void PopulateGhostInfoDictionaries_NoDeployableInfos_DoesNotLogStowBaseline()
        {
            // Recordings without any deployable parts (e.g. a Kerbal on EVA)
            // must not emit a noisy "stowed 0/0" line. The log is gated on
            // deployableInfos.Count > 0.
            var result = new GhostBuildResult
            {
                deployableInfos = null,
            };

            var state = new GhostPlaybackState { vesselName = "EvaWalker" };

            GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, result, traj: null);

            Assert.Null(state.deployableInfos);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[GhostVisual]") && l.Contains("Spawn baseline"));
        }

        [Fact]
        public void PopulateGhostInfoDictionaries_EmptyDeployableInfosList_DoesNotLogStowBaseline()
        {
            // An empty list (vs null) should also be treated as "no deployables"
            // — the stowed-count denominator would be 0 and the line would be
            // pure log noise.
            var result = new GhostBuildResult
            {
                deployableInfos = new List<DeployableGhostInfo>(),
            };

            var state = new GhostPlaybackState { vesselName = "BareVessel" };

            GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, result, traj: null);

            Assert.NotNull(state.deployableInfos);
            Assert.Empty(state.deployableInfos);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[GhostVisual]") && l.Contains("Spawn baseline"));
        }

        [Fact]
        public void PopulateGhostInfoDictionaries_DeployableInfo_BuildsDictKeyedByPartPersistentId()
        {
            // The dict is keyed by partPersistentId so DeployableExtended /
            // DeployableRetracted events from PartStateSeeder (and from
            // per-frame transitions during playback) can find the matching
            // info via TryGetValue. If the key collapses or the dict is
            // dropped, the seed event silently no-ops and the ghost is stuck
            // at whatever pose the spawn baseline left it in — which for an
            // already-extended ladder means it appears stowed forever. Pin
            // the keying contract.
            var result = new GhostBuildResult
            {
                deployableInfos = new List<DeployableGhostInfo>
                {
                    new DeployableGhostInfo
                    {
                        partPersistentId = 100000,
                        transforms = new List<DeployableTransformState>()
                    },
                    new DeployableGhostInfo
                    {
                        partPersistentId = 101111,
                        transforms = new List<DeployableTransformState>()
                    },
                },
            };

            var state = new GhostPlaybackState();

            GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, result, traj: null);

            Assert.True(state.deployableInfos.ContainsKey(100000u));
            Assert.True(state.deployableInfos.ContainsKey(101111u));
            Assert.Equal(100000u, state.deployableInfos[100000u].partPersistentId);
            Assert.Equal(101111u, state.deployableInfos[101111u].partPersistentId);
        }

        [Fact]
        public void ApplyDeployableState_DeployedFalse_NullTransforms_ReturnsFalse_NoThrow()
        {
            // Defensive contract: the spawn baseline iterates every deployable
            // and calls ApplyDeployableState(deployed: false). For deployables
            // whose ghost transforms didn't resolve (path mismatch from the
            // animation sample → ghost mirror), the transform `t` is null.
            // ApplyDeployableState must skip those entries silently and not
            // NRE — otherwise a single unresolved ladder would crash the
            // entire spawn baseline and leave every other deployable at
            // prefab default.
            var info = new DeployableGhostInfo
            {
                partPersistentId = 100000,
                transforms = new List<DeployableTransformState>
                {
                    new DeployableTransformState { t = null },
                    new DeployableTransformState { t = null },
                },
            };
            var state = new GhostPlaybackState
            {
                deployableInfos = new Dictionary<uint, DeployableGhostInfo> { { 100000u, info } },
            };

            bool applied = GhostPlaybackLogic.ApplyDeployableState(
                state, new PartEvent { partPersistentId = 100000u }, deployed: false);

            Assert.False(applied);
        }

        [Fact]
        public void ApplyDeployableState_UnknownPid_ReturnsFalse()
        {
            // Spawn baseline iterates state.deployableInfos directly so this
            // case won't fire from the baseline itself — but the same dict is
            // consulted by ApplyFrameVisuals when a DeployableExtended seed
            // event arrives for a part the snapshot dropped (e.g. decoupled
            // before ghost build). The lookup must miss cleanly.
            var state = new GhostPlaybackState
            {
                deployableInfos = new Dictionary<uint, DeployableGhostInfo>(),
            };

            bool applied = GhostPlaybackLogic.ApplyDeployableState(
                state, new PartEvent { partPersistentId = 999u }, deployed: false);

            Assert.False(applied);
        }

        [Fact]
        public void ShouldEvaluateOrphanEnginePlayback_NullTrajectory_ReturnsFalse()
        {
            var state = new GhostPlaybackState
            {
                engineInfos = new Dictionary<ulong, EngineGhostInfo>
                {
                    { 1UL, new EngineGhostInfo() },
                },
            };

            Assert.False(GhostPlaybackLogic.ShouldEvaluateOrphanEnginePlayback(state, traj: null));
        }

        [Fact]
        public void ShouldEvaluateOrphanEnginePlayback_NoEngineOrAudioInfos_ReturnsFalse()
        {
            var state = new GhostPlaybackState();

            Assert.False(GhostPlaybackLogic.ShouldEvaluateOrphanEnginePlayback(state, new MockTrajectory()));
        }

        [Fact]
        public void ShouldEvaluateOrphanEnginePlayback_EngineOrAudioInfosPresent_ReturnsTrue()
        {
            var traj = new MockTrajectory();

            Assert.True(GhostPlaybackLogic.ShouldEvaluateOrphanEnginePlayback(
                new GhostPlaybackState
                {
                    engineInfos = new Dictionary<ulong, EngineGhostInfo>
                    {
                        { 1UL, new EngineGhostInfo() },
                    },
                },
                traj));

            Assert.True(GhostPlaybackLogic.ShouldEvaluateOrphanEnginePlayback(
                new GhostPlaybackState
                {
                    audioInfos = new Dictionary<ulong, AudioGhostInfo>
                    {
                        { 1UL, new AudioGhostInfo() },
                    },
                },
                traj));
        }
    }
}
