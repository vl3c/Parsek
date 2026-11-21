using System;
using System.IO;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// todo GHOST-LADDER-SPAWNS-DEPLOYED: a stowed stock retractable ladder drew DEPLOYED on
    /// its ghost. Stock <c>RetractableLadder</c> holds its clip at normalizedTime 1 when
    /// Retracted and 0 when Extended (decompiled KSP 1.12.5 FSM), while the model's rest pose
    /// is the extended ladder, so the endpoint scoring that picks "the end closer to the rest
    /// pose" as stowed made the spawn-time stow pose the ladder extended.
    ///
    /// The sampling itself needs a live Animation (in-game category LadderGhost); these cells
    /// pin the decision headless: the endpoint constants, that the ladder branch samples with
    /// them and without scoring, and the persisted-StateName snapshot baseline that spawns a
    /// ladder recorded extended as extended.
    /// </summary>
    public class RetractableLadderGhostPoseTests
    {
        private static ConfigNode PartWithModules(
            params (string moduleName, (string key, string value)[] kv)[] modules)
        {
            VesselSnapshotBuilder builder = VesselSnapshotBuilder.ProbeShip("LadderProbe");
            for (int i = 0; i < modules.Length; i++)
                builder.AddModuleToPart(0, modules[i].moduleName, modules[i].kv);
            return builder.Build().GetNodes("PART")[0];
        }

        private static (string, (string, string)[]) Module(
            string name, params (string, string)[] kv) => (name, kv);

        #region Sampling endpoints

        [Fact]
        public void LadderEndpoints_AreStockRestStates_RetractedAtOneExtendedAtZero()
        {
            Assert.Equal(1f, GhostVisualBuilder.RetractableLadderRetractedNormalizedTime);
            Assert.Equal(0f, GhostVisualBuilder.RetractableLadderExtendedNormalizedTime);
        }

        [Fact]
        public void LadderBranch_SamplesFixedStockEndpointsWithoutScoring()
        {
            string sampling = ReadGhostSource("GhostVisualBuilder.AnimationSampling.cs");
            string body = MethodBody(sampling, "SampleRetractableLadderStates(");
            Assert.Contains("RetractableLadderRetractedNormalizedTime, RetractableLadderExtendedNormalizedTime", body);
            Assert.Contains("useScoring: false", body);
            Assert.DoesNotContain("useScoring: true", body);

            string builders = ReadGhostSource("GhostVisualBuilder.PartInfoBuilders.cs");
            int branch = builders.IndexOf("if (deployableInfo == null && hasRetractableLadder)", StringComparison.Ordinal);
            Assert.True(branch >= 0, "ladder branch not found in TryBuildDeployableInfo");
            int next = builders.IndexOf("if (deployableInfo == null && hasAnimationGroupDeploy)", branch, StringComparison.Ordinal);
            Assert.True(next > branch, "animation-group branch not found after the ladder branch");
            string ladderBranch = builders.Substring(branch, next - branch);
            Assert.Contains("SampleRetractableLadderStates(prefab, ladderAnimName, ladderAnimRootName)", ladderBranch);
            Assert.DoesNotContain("SampleLadderStates(", ladderBranch);
        }

        private static string ReadGhostSource(string file)
        {
            string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", ".."));
            return ParsekDialogNamePrefixSourceGateTests.StripComments(
                File.ReadAllText(Path.Combine(root, "Source", "Parsek", "Ghost", file)));
        }

        private static string MethodBody(string code, string signatureToken)
        {
            int at = code.IndexOf(signatureToken, StringComparison.Ordinal);
            Assert.True(at >= 0, signatureToken + " not found");
            int open = code.IndexOf('{', at);
            int depth = 0;
            for (int i = open; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}' && --depth == 0)
                    return code.Substring(open, i - open + 1);
            }
            throw new InvalidOperationException("unbalanced braces after " + signatureToken);
        }

        #endregion

        #region Snapshot baseline

        [Fact]
        public void StateNameRetracted_ParsesToLadderStowed()
        {
            SnapshotPartBaseline baseline = GhostVisualBuilder.TryParseSnapshotPartBaseline(
                PartWithModules(Module("RetractableLadder", ("StateName", "Retracted"))));

            Assert.NotNull(baseline);
            Assert.False(baseline.ladderExtended);
        }

        [Fact]
        public void StateNameExtended_ParsesToLadderExtended()
        {
            SnapshotPartBaseline baseline = GhostVisualBuilder.TryParseSnapshotPartBaseline(
                PartWithModules(Module("RetractableLadder", ("StateName", "Extended"))));

            Assert.NotNull(baseline);
            Assert.True(baseline.ladderExtended);
        }

        [Theory]
        [InlineData("Extending")]
        [InlineData("Retracting")]
        [InlineData("")]
        [InlineData("garbage")]
        public void StateNameMidTravelOrUnreadable_NoOpinion(string stateName)
        {
            Assert.Null(GhostVisualBuilder.TryParseSnapshotPartBaseline(
                PartWithModules(Module("RetractableLadder", ("StateName", stateName)))));
        }

        [Fact]
        public void LadderStateName_StillSuppressesStandaloneAnimateGeneric()
        {
            SnapshotPartBaseline baseline = GhostVisualBuilder.TryParseSnapshotPartBaseline(
                PartWithModules(
                    Module("RetractableLadder", ("StateName", "Retracted")),
                    Module("ModuleAnimateGeneric", ("animTime", "1"))));

            Assert.NotNull(baseline);
            Assert.False(baseline.ladderExtended);
            Assert.False(baseline.animateGenericDeployed.HasValue);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Resolver_LadderOpinionDrivesTheDeployablePose(bool extended)
        {
            var actions = GhostPlaybackLogic.ResolveSnapshotBaselineActions(
                new SnapshotPartBaseline { ladderExtended = extended });

            Assert.Equal(extended, actions.deployableTarget);
            Assert.False(actions.deployableThroughCargoBayCascade);
        }

        [Fact]
        public void Resolver_ExplicitDeployStateAndGearOutrankTheLadder()
        {
            Assert.True(GhostPlaybackLogic.ResolveSnapshotBaselineActions(
                new SnapshotPartBaseline { deployableExtended = true, ladderExtended = false }).deployableTarget);
            Assert.False(GhostPlaybackLogic.ResolveSnapshotBaselineActions(
                new SnapshotPartBaseline { gearDeployed = false, ladderExtended = true }).deployableTarget);
        }

        [Fact]
        public void Resolver_LadderOutranksCargoBay()
        {
            var actions = GhostPlaybackLogic.ResolveSnapshotBaselineActions(
                new SnapshotPartBaseline { ladderExtended = true, cargoBayOpen = false });

            Assert.True(actions.deployableTarget);
            Assert.False(actions.deployableThroughCargoBayCascade);
        }

        #endregion
    }
}
