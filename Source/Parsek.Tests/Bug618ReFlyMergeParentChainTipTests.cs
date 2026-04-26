using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug #618: Re-Fly merge cleanup/default decisions covered only the active
    /// probe chain. A parent upper-stage recording split by the optimizer kept
    /// its terminal payload on the chain tip, so the stale upper-stage vessel
    /// could survive the merge as a real spawn.
    /// </summary>
    [Collection("Sequential")]
    public class Bug618ReFlyMergeParentChainTipTests : IDisposable
    {
        private const string TreeId = "tree_618";
        private const string UpperHead = "854fdf00000000000000000000000618";
        private const string UpperTip = "e77d9000000000000000000000000618";
        private const string ActiveProbe = "b5c29200000000000000000000000618";
        private const string ProbeTip = "probe_tip_618";
        private const string UpperChain = "chain_upper_618";
        private const string ProbeChain = "chain_probe_618";
        private const string ProbeBranchPoint = "bp_probe_618";

        private readonly List<string> logLines = new List<string>();

        public Bug618ReFlyMergeParentChainTipTests()
        {
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        [Fact]
        public void BuildDefaultVesselDecisions_ParentChainOptimizerTip_DefaultsGhostOnly()
        {
            var tree = BuildUpperStageToProbeTopology(
                activeProbeTerminal: TerminalState.Orbiting,
                probeTipTerminal: TerminalState.Destroyed);
            var suppressed = new HashSet<string>(StringComparer.Ordinal)
            {
                ActiveProbe,
                ProbeTip,
            };

            var decisions = MergeDialog.BuildDefaultVesselDecisions(
                tree,
                suppressed,
                ActiveProbe);

            Assert.True(decisions.ContainsKey(UpperTip));
            Assert.False(decisions[UpperTip]);

            Assert.True(decisions.ContainsKey(ActiveProbe));
            Assert.True(decisions[ActiveProbe]);

            Assert.True(decisions.ContainsKey(ProbeTip));
            Assert.False(decisions[ProbeTip]);

            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("parent-chain terminal tip")
                && l.Contains(UpperTip)
                && l.Contains("ghost-only"));
        }

        [Fact]
        public void BuildDefaultVesselDecisions_ActiveTargetOnProbeTip_KeepsActiveTipSpawnable()
        {
            var tree = BuildUpperStageToProbeTopology(
                activeProbeTerminal: null,
                probeTipTerminal: TerminalState.Orbiting);
            tree.Recordings[ActiveProbe].VesselSnapshot = null;
            var suppressed = new HashSet<string>(StringComparer.Ordinal)
            {
                ActiveProbe,
                ProbeTip,
            };

            var decisions = MergeDialog.BuildDefaultVesselDecisions(
                tree,
                suppressed,
                ProbeTip);

            Assert.True(decisions.ContainsKey(ProbeTip));
            Assert.True(decisions[ProbeTip]);
            Assert.False(decisions[ActiveProbe]);
            Assert.True(decisions.ContainsKey(UpperTip));
            Assert.False(decisions[UpperTip]);
        }

        [Fact]
        public void CollectParentChainTips_SkipsMultiParentBranch()
        {
            var tree = BuildUpperStageToProbeTopology(
                activeProbeTerminal: TerminalState.Orbiting,
                probeTipTerminal: TerminalState.Destroyed);
            tree.BranchPoints[0].ParentRecordingIds.Add("other_parent");
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = "other_parent",
                TreeId = TreeId,
                VesselName = "Docked Partner",
                VesselPersistentId = 300u,
                TerminalStateValue = TerminalState.Orbiting,
                VesselSnapshot = Snapshot(),
            });

            var tips = MergeDialog.CollectActiveReFlyParentChainTerminalTipIds(
                tree,
                ActiveProbe);

            Assert.Empty(tips);
        }

        [Fact]
        public void ResolveChainTerminalRecording_UsesPendingTreeContext()
        {
            var tree = new RecordingTree { Id = TreeId, TreeName = "Pending" };
            var head = new Recording
            {
                RecordingId = UpperHead,
                TreeId = TreeId,
                ChainId = UpperChain,
                ChainIndex = 0,
                ChainBranch = 0,
            };
            var tip = new Recording
            {
                RecordingId = UpperTip,
                TreeId = TreeId,
                ChainId = UpperChain,
                ChainIndex = 1,
                ChainBranch = 0,
                TerminalStateValue = TerminalState.Orbiting,
            };
            tree.AddOrReplaceRecording(head);
            tree.AddOrReplaceRecording(tip);

            Assert.Same(tip, EffectiveState.ResolveChainTerminalRecording(head, tree));
            Assert.Same(head, EffectiveState.ResolveChainTerminalRecording(head));
        }

        private static RecordingTree BuildUpperStageToProbeTopology(
            TerminalState? activeProbeTerminal,
            TerminalState? probeTipTerminal)
        {
            var tree = new RecordingTree
            {
                Id = TreeId,
                TreeName = "Kerbal X #618",
                RootRecordingId = UpperHead,
                ActiveRecordingId = ActiveProbe,
            };

            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = UpperHead,
                TreeId = TreeId,
                VesselName = "Kerbal X Upper Stage",
                VesselPersistentId = 100u,
                ChainId = UpperChain,
                ChainIndex = 0,
                ChainBranch = 0,
                TerminalStateValue = null,
                VesselSnapshot = null,
            });
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = UpperTip,
                TreeId = TreeId,
                VesselName = "Kerbal X Upper Stage",
                VesselPersistentId = 100u,
                ChainId = UpperChain,
                ChainIndex = 1,
                ChainBranch = 0,
                ChildBranchPointId = ProbeBranchPoint,
                TerminalStateValue = TerminalState.Orbiting,
                VesselSnapshot = Snapshot(),
            });
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = ActiveProbe,
                TreeId = TreeId,
                VesselName = "Kerbal X Probe",
                VesselPersistentId = 200u,
                ParentBranchPointId = ProbeBranchPoint,
                ChainId = ProbeChain,
                ChainIndex = 0,
                ChainBranch = 0,
                TerminalStateValue = activeProbeTerminal,
                VesselSnapshot = activeProbeTerminal.HasValue ? Snapshot() : null,
            });
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = ProbeTip,
                TreeId = TreeId,
                VesselName = "Kerbal X Probe",
                VesselPersistentId = 200u,
                ChainId = ProbeChain,
                ChainIndex = 1,
                ChainBranch = 0,
                TerminalStateValue = probeTipTerminal,
                VesselSnapshot = probeTipTerminal.HasValue ? Snapshot() : null,
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = ProbeBranchPoint,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { UpperHead },
                ChildRecordingIds = new List<string> { ActiveProbe },
            });

            return tree;
        }

        private static ConfigNode Snapshot()
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("sit", "ORBITING");
            return node;
        }
    }
}
