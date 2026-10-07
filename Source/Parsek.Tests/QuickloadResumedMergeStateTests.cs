using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The merge state a quickload-resumed tree member carries (todo
    /// QUICKLOAD-RESUMED-MEMBER-KEEPS-ABANDONED-FUTURE-MERGE-STATE). A commit after the quicksave
    /// promotes a member to CommittedProvisional from its end; the restore copies that onto the
    /// quicksave's member; <c>ParsekScenario.TrimAndReconcileForQuickloadResume</c> clears the
    /// member's end state and must hand back the quicksave's merge state with it, so the final
    /// commit re-derives it from the replayed end and the reaper can close the slot. The
    /// end-to-end restore routes live in <see cref="QuickloadCommittedAfterQuicksaveTests"/>;
    /// this class holds the decision table and the mirror directions: committed history, the
    /// Re-Fly scope, and the final commit plus reaper after the reset.
    /// </summary>
    [Collection("Sequential")]
    public class QuickloadResumedMergeStateTests : IDisposable
    {
        private const double CutoffUT = 115.0;

        private readonly List<string> logLines = new List<string>();
        private readonly List<string> deletedRpIds = new List<string>();

        public QuickloadResumedMergeStateTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ResetAll();
            RecordingStore.SuppressLogging = false;
            GameStateStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            RecordingStore.SkipSidecarCurrencyCheckForTesting = true;
            RewindPointReaper.DeleteQuicksaveForTesting = rpId =>
            {
                deletedRpIds.Add(rpId);
                return true;
            };
        }

        public void Dispose()
        {
            ResetAll();
            RewindPointReaper.ResetTestOverrides();
            KspStatePatcher.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
        }

        private static void ResetAll()
        {
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.ClearPendingQuickloadResumeContext();
            ParsekScenario.ClearRestoredQuicksaveTreeFactsForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            RewindContext.ResetForTesting();
            RevertDetector.ResetForTesting();
        }

        // ============================================================
        // Pure pieces
        // ============================================================

        [Theory]
        [InlineData(MergeState.CommittedProvisional, MergeState.Immutable, true, "abandoned-future-merge-state")]
        [InlineData(MergeState.Immutable, MergeState.CommittedProvisional, true, "abandoned-future-merge-state")]
        [InlineData(MergeState.Immutable, MergeState.Immutable, false, "unchanged")]
        [InlineData(MergeState.CommittedProvisional, MergeState.CommittedProvisional, false, "unchanged")]
        [InlineData(MergeState.NotCommitted, MergeState.Immutable, false, "live-provisional")]
        [InlineData(MergeState.NotCommitted, MergeState.NotCommitted, false, "live-provisional")]
        [InlineData(MergeState.Immutable, MergeState.NotCommitted, false, "quicksave-provisional")]
        [InlineData(MergeState.CommittedProvisional, MergeState.NotCommitted, false, "quicksave-provisional")]
        public void ShouldResetMergeStateForResume_Table(
            MergeState current, MergeState quicksave, bool expected, string expectedReason)
        {
            Assert.Equal(expected,
                ParsekScenario.ShouldResetMergeStateForResume(current, quicksave, out string reason));
            Assert.Equal(expectedReason, reason);
        }

        [Fact]
        public void ShouldResetMergeStateForResume_NoQuicksaveState_Keeps()
        {
            Assert.False(ParsekScenario.ShouldResetMergeStateForResume(
                MergeState.CommittedProvisional, null, out string reason));
            Assert.Equal("no-quicksave-state", reason);
        }

        [Fact]
        public void CaptureQuicksaveTreeFacts_RecordsEachMembersLoadedMergeState()
        {
            var tree = new RecordingTree { Id = "facts", TreeName = "facts", RootRecordingId = "a", ActiveRecordingId = "a" };
            tree.AddOrReplaceRecording(new Recording { RecordingId = "a", TreeId = "facts" });
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = "b", TreeId = "facts", MergeState = MergeState.CommittedProvisional,
            });
            tree.AddOrReplaceRecording(new Recording
            {
                RecordingId = "c", TreeId = "facts", MergeState = MergeState.NotCommitted,
            });

            // Round-trip through the save node: a member with no `mergeState` key reads Immutable.
            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);
            RecordingTree loaded = RecordingTree.Load(node);

            ParsekScenario.QuicksaveTreeFacts facts = ParsekScenario.CaptureQuicksaveTreeFacts(
                loaded, new HashSet<string>());

            Assert.Equal(MergeState.Immutable, facts.Members["a"].QuicksaveMergeState);
            Assert.Equal(MergeState.CommittedProvisional, facts.Members["b"].QuicksaveMergeState);
            Assert.Equal(MergeState.NotCommitted, facts.Members["c"].QuicksaveMergeState);
            Assert.Null(new ParsekScenario.QuicksaveMemberFacts().QuicksaveMergeState);
        }

        // ============================================================
        // Mirror (i) and (iii): committed history keeps its merge state.
        // ============================================================

        [Fact]
        public void CommittedHistory_MergeStateLeftAlone()
        {
            var tree = NewTree("hist", "pod");
            // Stashed (CommittedProvisional) in the quicksave, sealed after it: committed history.
            Recording sealedAfter = AddMember(tree, "sealed", MergeState.Immutable, TerminalState.Destroyed, 100.0, 150.0);
            // Crashed before the quicksave: its terminal is already in the quicksave.
            Recording crashedBefore = AddMember(tree, "crashed", MergeState.CommittedProvisional, TerminalState.Destroyed, 100.0, 150.0);
            // Still committed in memory (another tree's copy).
            Recording stillCommitted = AddMember(tree, "committed", MergeState.CommittedProvisional, TerminalState.SubOrbital, 100.0, 150.0);
            RecordingStore.AddCommittedInternal(Recording.DeepClone(stillCommitted));

            var facts = Facts(tree);
            facts.Members["sealed"] = new ParsekScenario.QuicksaveMemberFacts
            {
                CommittedInQuicksave = true,
                QuicksaveMergeState = MergeState.CommittedProvisional,
                ExplicitStartUT = double.NaN, ExplicitEndUT = double.NaN,
                StartBranchUT = double.NaN, EndBranchUT = double.NaN,
            };
            facts.Members["crashed"] = new ParsekScenario.QuicksaveMemberFacts
            {
                HasTerminal = true,
                QuicksaveMergeState = MergeState.Immutable,
                ExplicitStartUT = double.NaN, ExplicitEndUT = double.NaN,
                StartBranchUT = double.NaN, EndBranchUT = double.NaN,
            };

            ParsekScenario.TrimAndReconcileForQuickloadResume(
                tree, tree.Recordings["pod"], CutoffUT, ParsekScenario.QuickloadTrimScope.TreeWide,
                LoadKind.QuickloadFlight, CutoffUT, facts);

            Assert.Equal(MergeState.Immutable, sealedAfter.MergeState);
            Assert.Equal(MergeState.CommittedProvisional, crashedBefore.MergeState);
            Assert.Equal(MergeState.CommittedProvisional, stillCommitted.MergeState);
            Assert.Equal(TerminalState.Destroyed, sealedAfter.TerminalStateValue);
            Assert.DoesNotContain(logLines, l => l.Contains("Quickload abandoned-future merge state reset:"));
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]")
                && l.Contains("Quickload abandoned-future reconcile: tree='hist'")
                && l.Contains("skippedCommitted=1")
                && l.Contains("skippedQuicksaveHistory=2")
                && l.EndsWith("mergeStatesReset=0", StringComparison.Ordinal));
        }

        // ============================================================
        // Mirror (ii): the Re-Fly scope touches only the session's provisional, never its state.
        // ============================================================

        [Fact]
        public void ReFlyScope_ProvisionalAndSiblingKeepTheirMergeStateAndSessionFields()
        {
            var tree = NewTree("refly", "prov");
            Recording prov = tree.Recordings["prov"];
            prov.MergeState = MergeState.NotCommitted;
            prov.TerminalStateValue = TerminalState.Destroyed;
            prov.CreatingSessionId = "sess_1";
            prov.SupersedeTargetId = "origin_1";
            prov.ProvisionalForRpId = "rp_1";
            SetPoints(prov, 100.0, 150.0);
            // The sibling slot the refresh opened (CommittedProvisional from the committed tree).
            Recording sibling = AddMember(tree, "sibling", MergeState.CommittedProvisional, TerminalState.SubOrbital, 100.0, 150.0);

            var facts = Facts(tree);
            facts.Members["prov"] = WithMergeState(facts.Members["prov"], MergeState.NotCommitted);

            ParsekScenario.TrimAndReconcileForQuickloadResume(
                tree, prov, CutoffUT, ParsekScenario.QuickloadTrimScope.ActiveRecOnly,
                LoadKind.QuickloadFlight, CutoffUT, facts);

            Assert.Null(prov.TerminalStateValue);
            Assert.Equal(MergeState.NotCommitted, prov.MergeState);
            Assert.Equal("sess_1", prov.CreatingSessionId);
            Assert.Equal("origin_1", prov.SupersedeTargetId);
            Assert.Equal("rp_1", prov.ProvisionalForRpId);
            Assert.Equal(MergeState.CommittedProvisional, sibling.MergeState);
            Assert.Equal(TerminalState.SubOrbital, sibling.TerminalStateValue);
            Assert.Contains(logLines, l =>
                l.Contains("Quickload abandoned-future merge state kept: rec=prov mergeState=NotCommitted")
                && l.Contains("reason=live-provisional"));
            Assert.Contains(logLines, l =>
                l.Contains("Quickload abandoned-future reconcile: tree='refly'")
                && l.Contains("scope=ActiveRecOnly")
                && l.Contains("endStatesCleared=1")
                && l.EndsWith("mergeStatesReset=0", StringComparison.Ordinal));
        }

        // ============================================================
        // Mirror (iv): the final commit re-derives the merge state from the replayed end, and
        // the reaper closes the rewind point once every slot is concluded.
        // ============================================================

        [Theory]
        [InlineData(TerminalState.Landed, true)]
        [InlineData(TerminalState.SubOrbital, true)]
        [InlineData(TerminalState.Landed, false)]
        public void FinalCommit_RederivesTheBoosterMergeState_AndReapsWhenEverySlotIsClosed(
            TerminalState replayedEnd, bool runReconcile)
        {
            RecordingTree tree = MakeSeparationTree("fc");
            var rp = new RewindPoint
            {
                RewindPointId = "rp_fc",
                BranchPointId = "bp_fc",
                UT = 100.0,
                SessionProvisional = true,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "pod", Controllable = true },
                    new ChildSlot { SlotIndex = 1, OriginChildRecordingId = "booster", Controllable = true },
                },
            };
            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint> { rp },
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();

            // The quicksave at 115: pod and booster live (Immutable, the codec default).
            var facts = Facts(tree);
            // What the restore leaves after the abandoned in-flight commit: the booster
            // promoted from its abandoned SubOrbital end.
            Recording booster = tree.Recordings["booster"];
            booster.MergeState = MergeState.CommittedProvisional;

            if (runReconcile)
            {
                ParsekScenario.TrimAndReconcileForQuickloadResume(
                    tree, tree.Recordings["pod"], CutoffUT, ParsekScenario.QuickloadTrimScope.TreeWide,
                    LoadKind.QuickloadFlight, CutoffUT, facts);
                Assert.Equal(MergeState.Immutable, booster.MergeState);
                Assert.Null(booster.TerminalStateValue);
            }

            // The replay: the pod lands, the booster ends as the theory says.
            tree.Recordings["pod"].TerminalStateValue = TerminalState.Landed;
            booster.TerminalStateValue = replayedEnd;
            RecordingStore.CommitTree(tree);
            Assert.False(rp.SessionProvisional);

            int reaped = RewindPointReaper.ReapOrphanedRPs();

            if (!runReconcile)
            {
                // The leak: the abandoned state survives the commit and holds the slot open.
                Assert.Equal(MergeState.CommittedProvisional, booster.MergeState);
                Assert.Equal(0, reaped);
                Assert.Single(scenario.RewindPoints);
                return;
            }

            if (replayedEnd == TerminalState.SubOrbital)
            {
                // Still unconcluded: re-promoted (its first commit, the abandoned copy detached).
                Assert.Equal(MergeState.CommittedProvisional, booster.MergeState);
                Assert.Equal(0, reaped);
                Assert.Single(scenario.RewindPoints);
                Assert.Contains(logLines, l =>
                    l.Contains("CommitTree promoted rec=booster") && l.Contains("to CommittedProvisional"));
            }
            else
            {
                Assert.Equal(MergeState.Immutable, booster.MergeState);
                Assert.Equal(1, reaped);
                Assert.Empty(scenario.RewindPoints);
                Assert.Equal(new[] { "rp_fc" }, deletedRpIds.ToArray());
            }
        }

        // ============================================================
        // Helpers
        // ============================================================

        private static RecordingTree NewTree(string id, string activeId)
        {
            var tree = new RecordingTree { Id = id, TreeName = id, RootRecordingId = activeId, ActiveRecordingId = activeId };
            var active = new Recording { RecordingId = activeId, TreeId = id, VesselName = activeId, VesselPersistentId = 111u };
            SetPoints(active, 100.0, 150.0);
            tree.AddOrReplaceRecording(active);
            return tree;
        }

        private static Recording AddMember(RecordingTree tree, string id, MergeState mergeState,
            TerminalState terminal, double startUT, double endUT)
        {
            var rec = new Recording
            {
                RecordingId = id, TreeId = tree.Id, VesselName = id,
                VesselPersistentId = (uint)(200 + tree.Recordings.Count),
                MergeState = mergeState, TerminalStateValue = terminal,
            };
            SetPoints(rec, startUT, endUT);
            tree.AddOrReplaceRecording(rec);
            return rec;
        }

        // The QL-4 shape: a parent that separates at 100 into the pod (slot 0, the focus) and
        // the probe booster (slot 1), both recorded past the 115 quicksave.
        private static RecordingTree MakeSeparationTree(string id)
        {
            var tree = new RecordingTree
            {
                Id = id, TreeName = id, RootRecordingId = "parent", ActiveRecordingId = "pod",
            };
            var parent = new Recording
            {
                RecordingId = "parent", TreeId = id, VesselName = "parent", VesselPersistentId = 100u,
                ChildBranchPointId = "bp_" + id,
            };
            SetPoints(parent, 50.0, 100.0);
            tree.AddOrReplaceRecording(parent);
            var pod = new Recording
            {
                RecordingId = "pod", TreeId = id, VesselName = "pod", VesselPersistentId = 111u,
                ParentBranchPointId = "bp_" + id,
            };
            SetPoints(pod, 100.0, 121.0);
            tree.AddOrReplaceRecording(pod);
            var booster = new Recording
            {
                RecordingId = "booster", TreeId = id, VesselName = "booster", VesselPersistentId = 222u,
                ParentBranchPointId = "bp_" + id, TerminalStateValue = TerminalState.SubOrbital,
            };
            SetPoints(booster, 100.0, 150.0);
            tree.AddOrReplaceRecording(booster);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp_" + id,
                UT = 100.0,
                Type = BranchPointType.Undock,
                RewindPointId = "rp_" + id,
                ParentRecordingIds = new List<string> { "parent" },
                ChildRecordingIds = new List<string> { "pod", "booster" },
            });
            tree.RebuildBackgroundMap();
            return tree;
        }

        // Facts as the quicksave at the cutoff holds the tree: every member live, never committed,
        // with no `mergeState` key (Immutable).
        private static ParsekScenario.QuicksaveTreeFacts Facts(RecordingTree tree)
        {
            var facts = new ParsekScenario.QuicksaveTreeFacts
            {
                TreeId = tree.Id,
                ActiveRecordingId = tree.ActiveRecordingId,
            };
            foreach (string id in tree.Recordings.Keys.ToList())
            {
                facts.Members[id] = new ParsekScenario.QuicksaveMemberFacts
                {
                    ExplicitStartUT = double.NaN,
                    ExplicitEndUT = double.NaN,
                    StartBranchUT = double.NaN,
                    EndBranchUT = double.NaN,
                    QuicksaveMergeState = MergeState.Immutable,
                };
            }
            return facts;
        }

        private static ParsekScenario.QuicksaveMemberFacts WithMergeState(
            ParsekScenario.QuicksaveMemberFacts member, MergeState state)
        {
            member.QuicksaveMergeState = state;
            return member;
        }

        private static void SetPoints(Recording rec, params double[] uts)
        {
            rec.Points.Clear();
            rec.TrackSections.Clear();
            foreach (double ut in uts)
            {
                rec.Points.Add(new TrajectoryPoint
                {
                    ut = ut, altitude = 1000.0, bodyName = "Kerbin",
                    rotation = Quaternion.identity, velocity = Vector3.zero,
                });
            }
            rec.ExplicitStartUT = uts[0];
            rec.ExplicitEndUT = uts[uts.Length - 1];
        }
    }
}
