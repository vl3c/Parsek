using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// REFLY-QUALIFY-AND-TIP-WALKS-DISAGREE-ACROSS-SWITCH-CONTINUATIONS.
    ///
    /// <para>
    /// "Does this slot qualify?" and "is its tip open?" used to be answered over
    /// DIFFERENT recordings. PR #1427 repointed
    /// <see cref="UnfinishedFlightClassifier.TryQualify"/> and the candidate-shape
    /// gate at
    /// <see cref="EffectiveState.ResolveTerminalRecordingAcrossSwitchContinuations"/>,
    /// which hops <see cref="BranchPointType.VesselSwitchContinuation"/> branch
    /// points, while the open/closed read, the CommitTree tip promotion and
    /// RewindInvoker's slot resolution all went through
    /// <see cref="ChildSlot.EffectiveRecordingId"/> ->
    /// <see cref="EffectiveState.EffectiveTipRecordingId"/>, which had no such hop.
    /// </para>
    ///
    /// <para>
    /// The ruling: the switch-hopping walk is CANONICAL, because PR #1427's intent
    /// is that a flight continued through a stock Switch-To is ONE flight. So
    /// <c>EffectiveTipRecordingId</c> gained the hop at its chain-hop step and both
    /// questions now name the same recording. These cells pin the agreement and the
    /// four mirror directions the hop must NOT break.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class SwitchContinuationTipWalkAgreementTests : IDisposable
    {
        private const string TreeId = "tree_tipwalk";
        private const string RpBpId = "bp_decouple";
        private const string SwitchBpId = "bp_switch_1";

        private static readonly List<RecordingSupersedeRelation> NoSupersedes =
            new List<RecordingSupersedeRelation>();

        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public SwitchContinuationTipWalkAgreementTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // ------------------------------------------------------------------
        // The defect shape: the two walks must name ONE recording.
        // ------------------------------------------------------------------

        [Fact]
        public void QualifyWalkAndTipWalk_NameTheSameContinuationSegment()
        {
            // Pre-fix this cell failed on the SECOND assert: the qualify walk
            // named rec_segment (it hops the switch branch point) while the tip
            // walk stopped at rec_origin (bare chain walk, and rec_origin has no
            // ChainId at all). Asserting the agreement documents the defect shape.
            var tree = BuildTree(originTerminal: null, segmentTerminal: TerminalState.Orbiting);
            var rp = InstallScenario();
            var origin = tree.Recordings["rec_origin"];

            Recording qualifyWalk =
                EffectiveState.ResolveTerminalRecordingAcrossSwitchContinuations(origin, tree);
            Assert.Equal("rec_segment", qualifyWalk.RecordingId);

            string tipWalk = EffectiveState.EffectiveTipRecordingId(
                "rec_origin", NoSupersedes);
            Assert.Equal("rec_segment", tipWalk);

            // And the slot-facing surface every consumer actually reads.
            Assert.Equal("rec_segment", rp.ChildSlots[1].EffectiveRecordingId(NoSupersedes));
        }

        [Fact]
        public void TipWalk_LogsTheSwitchHop()
        {
            BuildTree(originTerminal: null, segmentTerminal: TerminalState.Orbiting);
            InstallScenario();

            logLines.Clear();
            Assert.Equal("rec_segment",
                EffectiveState.EffectiveTipRecordingId("rec_origin", NoSupersedes));

            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("SwitchContinuationWalk: hop from=rec_origin")
                && l.Contains("to=rec_segment")
                && l.Contains("bp=" + SwitchBpId));
        }

        [Fact]
        public void CommitTree_PromotesTheContinuationTip_AndTheSlotReadsOpen()
        {
            // With the tip walk hopping the switch branch point, the slot's tip is
            // the continuation segment. It is born Immutable, so CommitTree must
            // demote it to CommittedProvisional or open/closed reads the slot as
            // CLOSED the moment the tip identity moved.
            var tree = BuildTree(
                originTerminal: null,
                segmentTerminal: TerminalState.Orbiting,
                registerCommittedTree: false);
            var rp = InstallScenario();

            logLines.Clear();
            RecordingStore.CommitTree(tree);

            Assert.Equal(MergeState.CommittedProvisional,
                tree.Recordings["rec_origin"].MergeState);
            Assert.Equal(MergeState.CommittedProvisional,
                tree.Recordings["rec_segment"].MergeState);
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("CommitTree promoted chain-tip rec=rec_segment")
                && l.Contains("head=rec_origin"));

            // Open/closed now reads the continuation segment's MergeState.
            Assert.True(UnfinishedFlightClassifier.IsSlotEffectiveTipOpen(rp.ChildSlots[1]));
            Assert.False(RewindPointReaper.IsReapEligible(rp, NoSupersedes));
        }

        [Fact]
        public void SealedContinuationTip_ClosesTheSlot()
        {
            // The mirror of the cell above: once the continuation tip is sealed the
            // slot reads CLOSED, proving the open/closed answer is now taken from
            // the segment rather than from the origin's own MergeState.
            var tree = BuildTree(originTerminal: null, segmentTerminal: TerminalState.Orbiting);
            var rp = InstallScenario();
            tree.Recordings["rec_origin"].MergeState = MergeState.CommittedProvisional;
            tree.Recordings["rec_segment"].MergeState = MergeState.Immutable;

            Assert.False(UnfinishedFlightClassifier.IsSlotEffectiveTipOpen(rp.ChildSlots[1]));
        }

        // ------------------------------------------------------------------
        // Mirror 1: a real downstream split still STOPS the walk.
        // ------------------------------------------------------------------

        [Fact]
        public void RealDownstreamSplit_TipWalkStopsAtTheOrigin()
        {
            BuildTree(
                originTerminal: TerminalState.Orbiting,
                segmentTerminal: TerminalState.Orbiting,
                switchBpType: BranchPointType.Undock);
            InstallScenario();

            logLines.Clear();
            Assert.Equal("rec_origin",
                EffectiveState.EffectiveTipRecordingId("rec_origin", NoSupersedes));
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("SwitchContinuationWalk: stop from=rec_origin")
                && l.Contains("reason=notSwitchBranchPoint"));
        }

        // ------------------------------------------------------------------
        // Mirror 2: a switch branch point with more than one claimant is NOT
        // hopped (the walk refuses to guess which child is the continuation).
        // ------------------------------------------------------------------

        [Fact]
        public void AmbiguousSwitchBranchPoint_TipWalkStopsAtTheOrigin()
        {
            var tree = BuildTree(
                originTerminal: TerminalState.Orbiting,
                segmentTerminal: TerminalState.Orbiting);
            var impostor = Rec("rec_impostor", TerminalState.Destroyed,
                parentBranchPointId: SwitchBpId);
            AddToTree(tree, impostor);
            InstallScenario();

            logLines.Clear();
            Assert.Equal("rec_origin",
                EffectiveState.EffectiveTipRecordingId("rec_origin", NoSupersedes));
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("SwitchContinuationWalk: stop from=rec_origin")
                && l.Contains("reason=danglingOrAmbiguousChild"));
        }

        [Fact]
        public void DanglingSwitchBranchPoint_TipWalkStopsAtTheOrigin()
        {
            var tree = BuildTree(
                originTerminal: TerminalState.Orbiting,
                segmentTerminal: TerminalState.Orbiting);
            tree.Recordings.Remove("rec_segment");
            InstallScenario();

            Assert.Equal("rec_origin",
                EffectiveState.EffectiveTipRecordingId("rec_origin", NoSupersedes));
        }

        // ------------------------------------------------------------------
        // Mirror 3: a supersede row anchored on the continuation child is
        // followed AFTER the hop (the composite walk keeps both edge kinds).
        // ------------------------------------------------------------------

        [Fact]
        public void SupersedeOnTheContinuationChild_IsFollowedAfterTheHop()
        {
            var tree = BuildTree(originTerminal: null, segmentTerminal: TerminalState.SubOrbital);
            var fork = Rec("rec_fork", TerminalState.Orbiting);
            AddToTree(tree, fork);
            InstallScenario();

            var supersedes = new List<RecordingSupersedeRelation>
            {
                new RecordingSupersedeRelation
                {
                    OldRecordingId = "rec_segment",
                    NewRecordingId = "rec_fork",
                },
            };

            Assert.Equal("rec_fork",
                EffectiveState.EffectiveTipRecordingId("rec_origin", supersedes));
        }

        // ------------------------------------------------------------------
        // Mirror 4: a cycle through a switch hop terminates and logs.
        // ------------------------------------------------------------------

        [Fact]
        public void CyclicSwitchContinuations_TerminateAndWarn()
        {
            var tree = new RecordingTree
            {
                Id = TreeId,
                TreeName = "tipwalk cycle",
                RootRecordingId = "rec_a",
            };
            var a = Rec("rec_a", TerminalState.Orbiting,
                parentBranchPointId: "bp_ba", childBranchPointId: "bp_ab");
            var b = Rec("rec_b", TerminalState.Orbiting,
                parentBranchPointId: "bp_ab", childBranchPointId: "bp_ba");
            AddToTree(tree, a, b);
            tree.BranchPoints.Add(SwitchBp("bp_ab", "rec_a", "rec_b"));
            tree.BranchPoints.Add(SwitchBp("bp_ba", "rec_b", "rec_a"));
            RecordingStore.AddCommittedTreeForTesting(tree);

            logLines.Clear();
            // The inner walk stops at rec_b (its own visited set refuses the
            // second hop), so the composite walker hops rec_a -> rec_b once and
            // then, from rec_b, is offered rec_a again and rejects it.
            Assert.Equal("rec_b",
                EffectiveState.EffectiveTipRecordingId("rec_a", NoSupersedes));
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("cycle detected")
                && l.Contains("rec_a"));
        }

        // ------------------------------------------------------------------
        // Regression floor: a plain chain with no switch branch point resolves
        // exactly as before, and a recording with neither a ChainId nor a child
        // branch point short-circuits without touching the tree scan.
        // ------------------------------------------------------------------

        [Fact]
        public void PlainChain_TipWalkUnchanged()
        {
            var tree = new RecordingTree
            {
                Id = TreeId,
                TreeName = "plain chain",
                RootRecordingId = "rec_head",
            };
            var head = Rec("rec_head", null);
            head.ChainId = "chain_1";
            head.ChainIndex = 0;
            var tip = Rec("rec_tip", TerminalState.Orbiting);
            tip.ChainId = "chain_1";
            tip.ChainIndex = 1;
            AddToTree(tree, head, tip);
            RecordingStore.AddCommittedTreeForTesting(tree);

            Assert.Equal("rec_tip",
                EffectiveState.EffectiveTipRecordingId("rec_head", NoSupersedes));
        }

        [Fact]
        public void StandaloneRecording_TipWalkReturnsItself()
        {
            var tree = new RecordingTree
            {
                Id = TreeId,
                TreeName = "standalone",
                RootRecordingId = "rec_solo",
            };
            AddToTree(tree, Rec("rec_solo", TerminalState.Landed));
            RecordingStore.AddCommittedTreeForTesting(tree);

            Assert.Equal("rec_solo",
                EffectiveState.EffectiveTipRecordingId("rec_solo", NoSupersedes));
        }

        // ------------------------------------------------------------------
        // Fixtures (same measured GS-3 shape the switch-walk suite uses).
        // ------------------------------------------------------------------

        private static RecordingTree BuildTree(
            TerminalState? originTerminal,
            TerminalState? segmentTerminal,
            BranchPointType switchBpType = BranchPointType.VesselSwitchContinuation,
            bool registerCommittedTree = true)
        {
            var tree = new RecordingTree
            {
                Id = TreeId,
                TreeName = "tipwalk tree",
                RootRecordingId = "rec_focus",
            };

            var focus = Rec("rec_focus", TerminalState.Orbiting, childBranchPointId: RpBpId);
            var origin = Rec("rec_origin", originTerminal,
                parentBranchPointId: RpBpId, childBranchPointId: SwitchBpId);
            var segment = Rec("rec_segment", segmentTerminal, parentBranchPointId: SwitchBpId);
            AddToTree(tree, focus, origin, segment);

            tree.BranchPoints.Add(new BranchPoint
            {
                Id = RpBpId,
                UT = 100.0,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { "rec_focus" },
                ChildRecordingIds = new List<string> { "rec_focus", "rec_origin" },
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = SwitchBpId,
                UT = 150.0,
                Type = switchBpType,
                ParentRecordingIds = new List<string> { "rec_origin" },
                ChildRecordingIds = new List<string> { "rec_segment" },
            });

            if (registerCommittedTree)
                RecordingStore.AddCommittedTreeForTesting(tree);
            return tree;
        }

        private static BranchPoint SwitchBp(string id, string parentRecId, string childRecId)
            => new BranchPoint
            {
                Id = id,
                UT = 200.0,
                Type = BranchPointType.VesselSwitchContinuation,
                ParentRecordingIds = new List<string> { parentRecId },
                ChildRecordingIds = new List<string> { childRecId },
            };

        private static void AddToTree(RecordingTree tree, params Recording[] recordings)
        {
            foreach (var rec in recordings)
            {
                rec.TreeId = tree.Id;
                tree.AddOrReplaceRecording(rec);
                RecordingStore.AddRecordingWithTreeForTesting(rec);
            }
        }

        private static Recording Rec(
            string id,
            TerminalState? terminal,
            string parentBranchPointId = null,
            string childBranchPointId = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                MergeState = MergeState.Immutable,
                TerminalStateValue = terminal,
                ParentBranchPointId = parentBranchPointId,
                ChildBranchPointId = childBranchPointId,
            };
        }

        private static RewindPoint InstallScenario(bool sessionProvisional = false)
        {
            var rp = new RewindPoint
            {
                RewindPointId = "rp_tipwalk",
                BranchPointId = RpBpId,
                UT = 100.0,
                SessionProvisional = sessionProvisional,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_focus", Controllable = true },
                    new ChildSlot { SlotIndex = 1, OriginChildRecordingId = "rec_origin", Controllable = true },
                },
            };

            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint> { rp },
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            scenario.BumpTombstoneStateVersion();
            EffectiveState.ResetCachesForTesting();
            return rp;
        }
    }
}
