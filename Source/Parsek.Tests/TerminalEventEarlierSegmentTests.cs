using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// RECOVERY-STAMPS-EARLIER-SEGMENTS-OF-THE-SAME-VESSEL: a recovery / termination of a
    /// vessel stamps only the pending-tree recording that is the vessel's current tip. An
    /// earlier segment of the same vessel (split parent, dock dominant parent, background
    /// continuation) keeps its own end, snapshot and terminal state, and a tree commit books
    /// the recovery funds once.
    /// </summary>
    [Collection("Sequential")]
    public class TerminalEventEarlierSegmentTests : IDisposable
    {
        private const string Name = "Kestrel";
        private const uint Pid = 5000u;
        private const string Guid = "0123456789abcdef0123456789abcdef";

        private readonly List<string> logLines = new List<string>();

        public TerminalEventEarlierSegmentTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Recording Seg(
            string id, uint pid, double start, double end,
            string vesselName = Name, TerminalState? terminal = null)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = vesselName,
                VesselPersistentId = pid,
                RecordedVesselGuid = Guid,
                TerminalStateValue = terminal,
                VesselSnapshot = new ConfigNode("VESSEL"),
                PreLaunchFunds = 40000.0
            };
            rec.Points.Add(new TrajectoryPoint { ut = start, funds = 40000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = end, funds = 40000.0 });
            return rec;
        }

        private static RecordingTree Tree(string id, params Recording[] recs)
        {
            var tree = new RecordingTree { Id = id, TreeName = Name, RootRecordingId = recs[0].RecordingId };
            foreach (var r in recs)
            {
                r.TreeId = id;
                tree.AddOrReplaceRecording(r);
            }
            return tree;
        }

        private static void Link(RecordingTree tree, string bpId, BranchPointType type,
            Recording[] parents, Recording[] children)
        {
            var bp = new BranchPoint { Id = bpId, Type = type, UT = children[0].StartUT };
            foreach (var p in parents)
            {
                p.ChildBranchPointId = bpId;
                bp.ParentRecordingIds.Add(p.RecordingId);
            }
            foreach (var c in children)
            {
                c.ParentBranchPointId = bpId;
                bp.ChildRecordingIds.Add(c.RecordingId);
            }
            tree.BranchPoints.Add(bp);
        }

        /// <summary>Split parent P (keeps the vessel as child C) plus a separated half S.</summary>
        private static RecordingTree SplitShape(out Recording parent, out Recording child, out Recording half)
        {
            parent = Seg("split-parent", Pid, 100, 200);
            child = Seg("split-child", Pid, 200, 300);
            half = Seg("split-half", 7777u, 200, 260, vesselName: "Kestrel Probe");
            half.RecordedVesselGuid = "fedcba9876543210fedcba9876543210";
            var tree = Tree("tree-split", parent, child, half);
            Link(tree, "bp-split", BranchPointType.Undock,
                new[] { parent }, new[] { child, half });
            return tree;
        }

        private static bool Recover(double ut, TerminalState state = TerminalState.Recovered)
        {
            return ParsekScenario.UpdateRecordingsForTerminalEvent(
                RecoveredVesselIdentity.FromRawName(Name, Guid), state, ut, Pid);
        }

        // catches: the split parent sharing name + pid + guid with the continuing child
        // being stamped Recovered, its EndUT stretched to the recovery UT and its snapshot dropped.
        [Fact]
        public void SplitParent_OnlyContinuingChildIsStamped()
        {
            RecordingStore.StashPendingTree(SplitShape(out var parent, out var child, out var half));

            Assert.True(Recover(350.0));

            Assert.Equal(TerminalState.Recovered, child.TerminalStateValue);
            Assert.Equal(350.0, child.ExplicitEndUT);
            Assert.Null(child.VesselSnapshot);

            Assert.Null(parent.TerminalStateValue);
            Assert.True(double.IsNaN(parent.ExplicitEndUT));
            Assert.NotNull(parent.VesselSnapshot);
            Assert.Equal(200.0, parent.EndUT);

            Assert.Null(half.TerminalStateValue);
            Assert.NotNull(half.VesselSnapshot);
        }

        // catches: the naive "ChildBranchPointId == null" filter, which would skip the
        // breakup-continuous recording (the vessel's live recording) and lose the recovery.
        [Fact]
        public void BreakupContinuous_RecordingWithDebrisOnlyBranchPoint_IsStillStamped()
        {
            var live = Seg("breakup-live", Pid, 100, 300);
            var debris = Seg("breakup-debris", 8888u, 150, 180, vesselName: "Kestrel Debris");
            debris.IsDebris = true;
            var tree = Tree("tree-breakup", live, debris);
            Link(tree, "bp-breakup", BranchPointType.Breakup, new[] { live }, new[] { debris });
            RecordingStore.StashPendingTree(tree);

            Assert.True(Recover(320.0));

            Assert.Equal(TerminalState.Recovered, live.TerminalStateValue);
            Assert.Equal(320.0, live.ExplicitEndUT);
            Assert.Null(live.VesselSnapshot);
            Assert.Null(debris.TerminalStateValue);
        }

        // catches: Docked not being in the overwrite block list, so the dominant dock parent
        // (same pid as the merged vessel) flipped Docked -> Recovered.
        [Fact]
        public void DockMerge_DominantParentKeepsDocked_MergedChildStamped()
        {
            var dominant = Seg("dock-dominant", Pid, 100, 200, terminal: TerminalState.Docked);
            var absorbed = Seg("dock-absorbed", 6000u, 50, 200, vesselName: "Tug",
                terminal: TerminalState.Docked);
            var merged = Seg("dock-merged", Pid, 200, 300);
            var tree = Tree("tree-dock", dominant, absorbed, merged);
            Link(tree, "bp-dock", BranchPointType.Dock,
                new[] { dominant, absorbed }, new[] { merged });
            RecordingStore.StashPendingTree(tree);

            Assert.True(Recover(340.0));

            Assert.Equal(TerminalState.Docked, dominant.TerminalStateValue);
            Assert.NotNull(dominant.VesselSnapshot);
            Assert.Equal(TerminalState.Docked, absorbed.TerminalStateValue);
            Assert.Equal(TerminalState.Recovered, merged.TerminalStateValue);
        }

        // catches: a per-hop check that is not transitive over a background continuation chain.
        [Fact]
        public void BackgroundContinuationChain_OnlyLastSegmentIsStamped()
        {
            var r0 = Seg("chain-r0", Pid, 100, 150);
            var r1 = Seg("chain-r1", Pid, 150, 220);
            var r2 = Seg("chain-r2", Pid, 220, 300);
            var tree = Tree("tree-chain", r0, r1, r2);
            Link(tree, "bp-chain-1", BranchPointType.JointBreak, new[] { r0 }, new[] { r1 });
            Link(tree, "bp-chain-2", BranchPointType.JointBreak, new[] { r1 }, new[] { r2 });
            RecordingStore.StashPendingTree(tree);

            Assert.True(Recover(330.0));

            Assert.Null(r0.TerminalStateValue);
            Assert.Null(r1.TerminalStateValue);
            Assert.Equal(TerminalState.Recovered, r2.TerminalStateValue);
            Assert.Equal(150.0, r0.EndUT);
            Assert.Equal(220.0, r1.EndUT);
        }

        // catches: a pure terminate (tracking-station delete) stamping every same-vessel
        // segment Destroyed, which lets crew end-state inference call a kerbal who left the
        // parent segment alive Dead.
        [Fact]
        public void Terminate_SplitShape_OnlyContinuingChildIsDestroyed()
        {
            RecordingStore.StashPendingTree(SplitShape(out var parent, out var child, out _));

            Assert.True(Recover(360.0, TerminalState.Destroyed));

            Assert.Equal(TerminalState.Destroyed, child.TerminalStateValue);
            Assert.Null(parent.TerminalStateValue);
            Assert.NotNull(parent.VesselSnapshot);
        }

        // catches: the double recovery payout - the stretched parent EndUT paired the same
        // FundsChanged(VesselRecovery) event as the child and emitted a second FundsEarning row.
        [Fact]
        public void TreeCommit_AfterRecovery_BooksExactlyOneRecoveryFundsRow()
        {
            var tree = SplitShape(out var parent, out var child, out var half);
            RecordingStore.StashPendingTree(tree);

            var evt = new GameStateEvent
            {
                ut = 350.0,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                valueBefore = 40000,
                valueAfter = 46500
            };
            GameStateStore.AddEvent(ref evt);

            Assert.True(Recover(350.0));

            var all = new List<GameAction>();
            foreach (var rec in new[] { parent, child, half })
            {
                RecordingStore.AddRecordingWithTreeForTesting(rec);
            }
            foreach (var rec in new[] { parent, child, half })
            {
                all.AddRange(LedgerOrchestrator.CreateVesselCostActions(
                    rec.RecordingId, rec.StartUT, rec.EndUT));
            }

            var recoveryRows = all.Where(a =>
                a.Type == GameActionType.FundsEarning &&
                a.FundsSource == FundsEarningSource.Recovery).ToList();
            Assert.Single(recoveryRows);
            Assert.Equal("split-child", recoveryRows[0].RecordingId);
            Assert.Equal(6500f, recoveryRows[0].FundsAwarded);
        }

        [Fact]
        public void SkippedEarlierSegments_LogOneSummaryLine()
        {
            RecordingStore.StashPendingTree(SplitShape(out _, out _, out _));

            Assert.True(Recover(350.0));

            var summaries = logLines.Where(l =>
                l.Contains("[Scenario]") &&
                l.Contains("UpdateRecordingsForTerminalEvent:") &&
                l.Contains("skippedEarlierSegments=1")).ToList();
            Assert.Single(summaries);
            Assert.Contains("stamped=1", summaries[0]);
            Assert.Contains("state=Recovered", summaries[0]);
        }

        [Fact]
        public void IsTerminalEventTarget_NoChildBranchPoint_True_SamePidContinuation_False()
        {
            var tree = SplitShape(out var parent, out var child, out var half);
            Assert.False(ParsekScenario.IsTerminalEventTarget(parent, tree));
            Assert.True(ParsekScenario.IsTerminalEventTarget(child, tree));
            Assert.True(ParsekScenario.IsTerminalEventTarget(half, tree));
            Assert.False(ParsekScenario.IsTerminalEventTarget(null, tree));
        }
    }
}
