using System;
using System.Collections.Generic;
using System.Globalization;
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
        // Review follow-up 1: the SIBLING walker (IsInSupersedeForwardTrail),
        // reached through ResolveRewindPointSlotIndexForRecording. The target is a
        // MID-TRAIL fork, so the composite-tip comparison cannot answer it and the
        // BFS must make the switch hop itself. Kills both mutants: a ChainId-only
        // cheap-exit gate and a hop reverted to the bare chain walk each leave the
        // BFS unable to leave rec_origin (it has neither a ChainId nor a supersede
        // edge of its own), and the lookup returns -1.
        // ------------------------------------------------------------------

        [Fact]
        public void SlotMembership_MidTrailForkBehindASwitchContinuation_ResolvesItsSlot()
        {
            var tree = BuildTree(originTerminal: null, segmentTerminal: TerminalState.SubOrbital);
            var fork1 = Rec("rec_fork1", TerminalState.SubOrbital);
            var fork2 = Rec("rec_fork2", TerminalState.Orbiting);
            AddToTree(tree, fork1, fork2);
            var rp = InstallScenario();

            var supersedes = new List<RecordingSupersedeRelation>
            {
                new RecordingSupersedeRelation
                {
                    OldRecordingId = "rec_segment", NewRecordingId = "rec_fork1",
                },
                new RecordingSupersedeRelation
                {
                    OldRecordingId = "rec_fork1", NewRecordingId = "rec_fork2",
                },
            };

            // Guard the premise: the composite tip is fork2, so the fork1 lookup
            // below cannot be answered by the composite comparison and must fall
            // through to the forward-trail BFS.
            Assert.Equal("rec_fork2",
                EffectiveState.EffectiveTipRecordingId("rec_origin", supersedes));

            Assert.Equal(1, EffectiveState.ResolveRewindPointSlotIndexForRecording(
                rp, tree.Recordings["rec_fork1"], supersedes));
        }

        // ------------------------------------------------------------------
        // Review follow-up 2: the tree-context fallback lookup. CommitTree runs the
        // promotion pass before the tree reaches CommittedTrees AND before its
        // recordings reach CommittedRecordings, so the id lookup inside the tip walk
        // must fall back to the pending tree's own dictionary. This fixture
        // registers NOTHING in the store.
        // ------------------------------------------------------------------

        [Fact]
        public void CommitTree_RecordingsOnlyInThePendingTree_StillPromotesTheContinuationTip()
        {
            var tree = BuildPendingOnlyTree();
            var rp = InstallScenario();

            // The premise: nothing here is in the committed store yet, so the
            // null-context walk cannot resolve past rec_origin at all.
            Assert.Null(EffectiveState.FindCommittedRecordingByIdRaw("rec_origin"));
            Assert.Equal("rec_origin",
                EffectiveState.EffectiveTipRecordingId("rec_origin", NoSupersedes));
            // With the pending tree as context the same walk reaches the segment.
            Assert.Equal("rec_segment", EffectiveState.EffectiveTipRecordingId(
                "rec_origin", NoSupersedes, recById: null, treeContext: tree));

            logLines.Clear();
            RecordingStore.CommitTree(tree);

            Assert.Equal(MergeState.CommittedProvisional,
                tree.Recordings["rec_segment"].MergeState);
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("CommitTree promoted chain-tip rec=rec_segment"));
            Assert.True(UnfinishedFlightClassifier.IsSlotEffectiveTipOpen(rp.ChildSlots[1]));
        }

        // ------------------------------------------------------------------
        // Review follow-up 6: the belt-and-braces hop cap. The visited set makes the
        // walk cycle-safe; this pins the acyclic-but-enormous stop.
        // ------------------------------------------------------------------

        [Fact]
        public void MoreSwitchHopsThanTheCap_StopsAtTheCapAndWarns()
        {
            int chainLength = EffectiveState.MaxSwitchContinuationHops + 3;
            var tree = new RecordingTree
            {
                Id = TreeId,
                TreeName = "long switch chain",
                RootRecordingId = "rec_0",
            };
            var recs = new List<Recording>();
            for (int i = 0; i <= chainLength; i++)
            {
                recs.Add(Rec(Id(i), null,
                    parentBranchPointId: i == 0 ? null : Bp(i - 1),
                    childBranchPointId: i == chainLength ? null : Bp(i)));
            }
            recs[chainLength].TerminalStateValue = TerminalState.Orbiting;
            AddToTree(tree, recs.ToArray());
            for (int i = 0; i < chainLength; i++)
                tree.BranchPoints.Add(SwitchBp(Bp(i), Id(i), Id(i + 1)));
            RecordingStore.AddCommittedTreeForTesting(tree);

            logLines.Clear();
            // ONE pass of the walker stops exactly at the cap, short of the real
            // terminal, and says so.
            Recording capped = EffectiveState.ResolveTerminalRecordingAcrossSwitchContinuations(
                tree.Recordings["rec_0"], tree);
            Assert.Equal(Id(EffectiveState.MaxSwitchContinuationHops), capped.RecordingId);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("SwitchContinuationWalk: hop cap "
                    + EffectiveState.MaxSwitchContinuationHops.ToString(CultureInfo.InvariantCulture)
                    + " reached"));

            // The composite walker calls the walk again from wherever the cap left
            // it, so the cap bounds one pass, never the answer: it still resolves
            // the real tip, and its own visited set bounds the outer loop.
            Assert.Equal(Id(chainLength),
                EffectiveState.EffectiveTipRecordingId("rec_0", NoSupersedes));
        }

        private static string Id(int i) => "rec_" + i.ToString(CultureInfo.InvariantCulture);

        private static string Bp(int i) => "bp_" + i.ToString(CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------
        // Review follow-up 3: the live terminal reads the first pass left on the
        // plain walk. The seal confirmation is PLAYER-FACING - it is the dialog that
        // asks for approval of a permanent, undoable action, and TrySeal applies to
        // the hopped tip.
        // ------------------------------------------------------------------

        [Fact]
        public void SealConfirmationText_NamesTheContinuationTerminal_NotUnknown()
        {
            var tree = BuildTree(originTerminal: null, segmentTerminal: TerminalState.Orbiting);
            InstallScenario();
            var origin = tree.Recordings["rec_origin"];
            origin.VesselName = "Probe One";
            origin.ExplicitEndUT = 150.0;

            string body = UnfinishedFlightSealHandler.BuildConfirmationBody(origin);

            Assert.Contains("(Orbiting at UT 150.0)", body);
            Assert.DoesNotContain("Unknown", body);
        }

        [Fact]
        public void SealConfirmationText_RealDownstreamSplit_StillReadsTheOrigin()
        {
            var tree = BuildTree(
                originTerminal: null,
                segmentTerminal: TerminalState.Orbiting,
                switchBpType: BranchPointType.Dock);
            InstallScenario();
            var origin = tree.Recordings["rec_origin"];
            origin.VesselName = "Probe One";

            Assert.Contains("(Unknown at UT",
                UnfinishedFlightSealHandler.BuildConfirmationBody(origin));
        }

        // ------------------------------------------------------------------
        // Review follow-up: one towards-safety cell per gate the audit moved onto
        // the hopping walk, so reverting any of them reds.
        // ------------------------------------------------------------------

        [Fact]
        public void SafetyGate_ContinuationDestroyed_ReadsAsATerminalFailure()
        {
            var tree = BuildTree(originTerminal: null, segmentTerminal: TerminalState.Destroyed);
            InstallScenario();
            Assert.True(SupersedeCommit.IsTerminalFailureReFlyOutcome(
                tree.Recordings["rec_origin"]));
        }

        [Fact]
        public void SafetyGate_ContinuationRecovered_ReadsAsHardSafety()
        {
            var tree = BuildTree(originTerminal: null, segmentTerminal: TerminalState.Recovered);
            InstallScenario();
            Assert.True(SupersedeCommit.IsHardSafetyTerminal(tree.Recordings["rec_origin"]));
        }

        [Fact]
        public void SafetyGate_ContinuationOrbiting_RequiresSlotAwareClassification()
        {
            var tree = BuildTree(originTerminal: null, segmentTerminal: TerminalState.Orbiting);
            InstallScenario();
            Assert.True(SupersedeCommit.RequiresSlotAwareMergeClassification(
                tree.Recordings["rec_origin"]));
        }

        [Fact]
        public void StashShape_ContinuationSubOrbital_IsAStashCandidate()
        {
            var tree = BuildTree(originTerminal: null, segmentTerminal: TerminalState.SubOrbital);
            InstallScenario();
            Assert.True(UnfinishedFlightClassifier.IsPotentialManualStashShape(
                tree.Recordings["rec_origin"]));
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

        /// <summary>
        /// The same shape as <see cref="BuildTree"/>, but NOTHING is registered in
        /// RecordingStore: neither the tree nor its recordings. That is the state
        /// CommitTree's promotion pass actually runs in.
        /// </summary>
        private static RecordingTree BuildPendingOnlyTree()
        {
            var tree = new RecordingTree
            {
                Id = TreeId,
                TreeName = "pending tipwalk tree",
                RootRecordingId = "rec_focus",
            };

            var focus = Rec("rec_focus", TerminalState.Orbiting, childBranchPointId: RpBpId);
            var origin = Rec("rec_origin", null,
                parentBranchPointId: RpBpId, childBranchPointId: SwitchBpId);
            var segment = Rec("rec_segment", TerminalState.Orbiting,
                parentBranchPointId: SwitchBpId);
            foreach (var rec in new[] { focus, origin, segment })
            {
                rec.TreeId = tree.Id;
                tree.AddOrReplaceRecording(rec);
            }

            tree.BranchPoints.Add(new BranchPoint
            {
                Id = RpBpId,
                UT = 100.0,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { "rec_focus" },
                ChildRecordingIds = new List<string> { "rec_focus", "rec_origin" },
            });
            tree.BranchPoints.Add(SwitchBp(SwitchBpId, "rec_origin", "rec_segment"));
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
