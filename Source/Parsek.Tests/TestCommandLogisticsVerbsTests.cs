using System;
using System.Collections.Generic;
using Parsek.Logistics;
using Parsek.TestCommands;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the <c>SealSlot</c> decision core (the fifth strict promotion
    /// out of the M-A2 reserved list). The verb's whole arg grammar and its
    /// already-sealed / incomplete classification are decidable without KSP, so they
    /// are pinned here; the Unity applier only calls
    /// <c>UnfinishedFlightSealHandler.TrySeal</c> and reads the store.
    /// </summary>
    public class TestCommandSealSlotTests
    {
        // ---- ResolveTarget ----

        [Fact]
        public void ResolveTarget_NoArgs_RejectsTargetArgMissing()
        {
            SealTargetSelection sel = TestCommandSealSlot.ResolveTarget(null, null, null);
            Assert.False(sel.Ok);
            Assert.Equal(TestCommandSealSlot.TargetArgMissingReason, sel.RejectReason);
            Assert.Equal(SealTargetMode.None, sel.Mode);
        }

        [Fact]
        public void ResolveTarget_EmptyArgs_RejectsTargetArgMissing()
        {
            SealTargetSelection sel = TestCommandSealSlot.ResolveTarget("", "", "");
            Assert.False(sel.Ok);
            Assert.Equal(TestCommandSealSlot.TargetArgMissingReason, sel.RejectReason);
        }

        [Fact]
        public void ResolveTarget_TreeOnly_SelectsTreeMode()
        {
            SealTargetSelection sel = TestCommandSealSlot.ResolveTarget(null, null, "tree-1");
            Assert.True(sel.Ok);
            Assert.Equal(SealTargetMode.Tree, sel.Mode);
            Assert.Equal("tree-1", sel.TreeId);
        }

        [Fact]
        public void ResolveTarget_RpAndSlot_SelectsSlotMode()
        {
            SealTargetSelection sel = TestCommandSealSlot.ResolveTarget("rp-7", "2", null);
            Assert.True(sel.Ok);
            Assert.Equal(SealTargetMode.Slot, sel.Mode);
            Assert.Equal("rp-7", sel.RewindPointId);
            Assert.Equal(2, sel.SlotIndex);
        }

        // An absent / unparseable / negative slot all ride InvokeRewind's unknown-slot
        // token rather than a separate missing-arg reason - that verb's documented
        // choice, kept so the two slot-addressed verbs share one vocabulary.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("1.5")]
        [InlineData("-1")]
        public void ResolveTarget_RpWithBadSlot_RejectsUnknownSlot(string slotArg)
        {
            SealTargetSelection sel = TestCommandSealSlot.ResolveTarget("rp-7", slotArg, null);
            Assert.False(sel.Ok);
            Assert.Equal(TestCommandSealSlot.UnknownSlotReason, sel.RejectReason);
        }

        // The SimulateStockSwitchClick pid-beats-vessel precedent: a caller who supplies
        // both spellings gets the PRECISE selector, never a refusal to debug.
        [Fact]
        public void ResolveTarget_BothSpellings_RpWins()
        {
            SealTargetSelection sel = TestCommandSealSlot.ResolveTarget("rp-7", "0", "tree-1");
            Assert.True(sel.Ok);
            Assert.Equal(SealTargetMode.Slot, sel.Mode);
            Assert.Null(sel.TreeId);
        }

        // ---- RecordingIdsNeedingSeal / CountUnsealed ----

        [Fact]
        public void RecordingIdsNeedingSeal_NullTree_IsEmpty()
        {
            Assert.Empty(TestCommandSealSlot.RecordingIdsNeedingSeal(null));
            Assert.Equal(0, TestCommandSealSlot.CountUnsealed(new RecordingTree { Id = "t" }));
        }

        [Fact]
        public void RecordingIdsNeedingSeal_AllImmutable_IsEmpty()
        {
            RecordingTree tree = TreeWith(MergeState.Immutable, MergeState.Immutable);
            Assert.Empty(TestCommandSealSlot.RecordingIdsNeedingSeal(tree));
            Assert.Equal(0, TestCommandSealSlot.CountUnsealed(tree));
        }

        [Fact]
        public void RecordingIdsNeedingSeal_ReportsEveryOpenMember()
        {
            RecordingTree tree = TreeWith(
                MergeState.Immutable,
                MergeState.CommittedProvisional,
                MergeState.NotCommitted);

            List<string> pending = TestCommandSealSlot.RecordingIdsNeedingSeal(tree);
            Assert.Equal(2, pending.Count);
            Assert.Contains("r1", pending);
            Assert.Contains("r2", pending);
            Assert.DoesNotContain("r0", pending);
            Assert.Equal(2, TestCommandSealSlot.CountUnsealed(tree));
        }

        // A null slot cannot prove un-sealed - the same reason
        // RouteCandidateFinder.IsTreeFullySealed skips it. If this ever counted a null
        // as open, a tree with a stale dictionary hole could never be sealed and the
        // verb would report seal-incomplete forever.
        [Fact]
        public void RecordingIdsNeedingSeal_SkipsNullSlots()
        {
            RecordingTree tree = TreeWith(MergeState.Immutable);
            tree.Recordings["hole"] = null;
            Assert.Empty(TestCommandSealSlot.RecordingIdsNeedingSeal(tree));
        }

        // ---- terminal classification ----

        [Fact]
        public void ClassifyTreeSealVerdict_SplitsOnRemaining()
        {
            Assert.Equal("OK", TestCommandSealSlot.ClassifyTreeSealVerdict(0));
            Assert.Equal("ERROR", TestCommandSealSlot.ClassifyTreeSealVerdict(1));
        }

        // The incomplete tail must NEVER be "unknown". TrySeal seals a recording's
        // slot's EFFECTIVE tip rather than the recording, so a member that is not its
        // own slot's tip stays open after a SUCCESSFUL seal and nothing ever closes it -
        // that is a KNOWN, named, fixture-shape state, and calling it unknown sends an
        // operator hunting a handler failure that never happened.
        [Fact]
        public void ResolveIncompleteReason_NamesTheNoRefusalCase()
        {
            Assert.Equal("member-not-slot-tip",
                TestCommandSealSlot.ResolveIncompleteReason(null));
            Assert.Equal("member-not-slot-tip",
                TestCommandSealSlot.ResolveIncompleteReason(""));
            // A real handler refusal still wins - it is the more specific fact.
            Assert.Equal("tip-unresolvable",
                TestCommandSealSlot.ResolveIncompleteReason("tip-unresolvable"));
        }

        // The RVR-2 "no-op guard" shape: a lane may issue SealSlot against a fixture
        // whose trees are already sealed and must get OK, never a reject.
        [Fact]
        public void IsAlreadySealed_OnlyWhenNothingSealedAndNothingLeft()
        {
            Assert.True(TestCommandSealSlot.IsAlreadySealed(0, 0));
            Assert.False(TestCommandSealSlot.IsAlreadySealed(2, 0));
            Assert.False(TestCommandSealSlot.IsAlreadySealed(0, 1));
        }

        private static RecordingTree TreeWith(params MergeState[] states)
        {
            var tree = new RecordingTree { Id = "t", RootRecordingId = "r0" };
            for (int i = 0; i < states.Length; i++)
            {
                tree.AddOrReplaceRecording(new Recording
                {
                    RecordingId = "r" + i,
                    TreeId = "t",
                    MergeState = states[i]
                });
            }
            return tree;
        }
    }

    /// <summary>
    /// Pure coverage for the <c>RouteCommand</c> decision core (the sixth strict
    /// promotion out of the M-A2 reserved list): the sub-action grammar, the optional
    /// args, the three-tier route selector, and the create-refusal classification that
    /// tells a lane WHICH candidacy gate closed.
    /// </summary>
    public class TestCommandRouteCommandTests
    {
        // ---- action grammar ----

        [Theory]
        [InlineData("create")]
        [InlineData("send-once")]
        [InlineData("pause")]
        [InlineData("activate")]
        public void IsKnownAction_AcceptsTheV1Vocabulary(string action)
        {
            Assert.True(TestCommandRouteCommand.IsKnownAction(action));
        }

        // Fail-closed and case-sensitive, the scene= / site= convention: a silently
        // accepted mis-spelling would drive the wrong sub-command.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Create")]
        [InlineData("CREATE")]
        [InlineData("send_once")]
        [InlineData("sendonce")]
        [InlineData("delete")]
        public void IsKnownAction_RefusesEverythingElse(string action)
        {
            Assert.False(TestCommandRouteCommand.IsKnownAction(action));
        }

        [Fact]
        public void IsRouteOperation_IsTheThreeNonCreateActions()
        {
            Assert.False(TestCommandRouteCommand.IsRouteOperation("create"));
            Assert.True(TestCommandRouteCommand.IsRouteOperation("send-once"));
            Assert.True(TestCommandRouteCommand.IsRouteOperation("pause"));
            Assert.True(TestCommandRouteCommand.IsRouteOperation("activate"));
        }

        // ---- interval= ----

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void TryParseIntervalArg_AbsentIsTheDefaultSentinel(string raw)
        {
            double seconds;
            Assert.True(TestCommandRouteCommand.TryParseIntervalArg(raw, out seconds));
            Assert.Equal(0.0, seconds);
        }

        [Fact]
        public void TryParseIntervalArg_AcceptsInvariantCultureDouble()
        {
            double seconds;
            Assert.True(TestCommandRouteCommand.TryParseIntervalArg("451.25", out seconds));
            Assert.Equal(451.25, seconds, 9);
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("0")]
        [InlineData("-5")]
        [InlineData("NaN")]
        [InlineData("Infinity")]
        [InlineData("451,25")]  // comma locale must not sneak through
        public void TryParseIntervalArg_RefusesNonPositiveOrNonFinite(string raw)
        {
            double seconds;
            Assert.False(TestCommandRouteCommand.TryParseIntervalArg(raw, out seconds));
        }

        [Fact]
        public void ResolveName_EmptyMeansBuilderNamesIt()
        {
            Assert.Null(TestCommandRouteCommand.ResolveName(null));
            Assert.Null(TestCommandRouteCommand.ResolveName(""));
            Assert.Equal("Runway Fuel Run", TestCommandRouteCommand.ResolveName("Runway Fuel Run"));
        }

        // ---- ResolveRoute ----

        [Fact]
        public void ResolveRoute_MissingSelector_RejectsRouteArgMissing()
        {
            RouteSelection sel = TestCommandRouteCommand.ResolveRoute(Routes(), null);
            Assert.False(sel.Ok);
            Assert.Equal(TestCommandRouteCommand.RouteArgMissingReason, sel.RejectReason);
        }

        [Fact]
        public void ResolveRoute_NoRoutes_RejectsUnknownRoute()
        {
            RouteSelection sel = TestCommandRouteCommand.ResolveRoute(new List<Route>(), "abc");
            Assert.False(sel.Ok);
            Assert.Equal(TestCommandRouteCommand.UnknownRouteReason, sel.RejectReason);
        }

        [Fact]
        public void ResolveRoute_ExactId_Matches()
        {
            List<Route> routes = Routes(("aaaa1111", "Alpha"), ("bbbb2222", "Beta"));
            RouteSelection sel = TestCommandRouteCommand.ResolveRoute(routes, "bbbb2222");
            Assert.True(sel.Ok);
            Assert.Equal("bbbb2222", sel.Route.Id);
            Assert.Equal("id", sel.MatchKind);
        }

        // The tier that makes a logged handle usable: every route line prints
        // RouteIds.Short (the first 8 chars), which is what a lane HAS - a full
        // Guid("N") id is minted at create time and cannot be pinned in a spec.
        [Fact]
        public void ResolveRoute_UniqueIdPrefix_Matches()
        {
            List<Route> routes = Routes(("aaaa1111ffff", "Alpha"), ("bbbb2222ffff", "Beta"));
            RouteSelection sel = TestCommandRouteCommand.ResolveRoute(routes, "bbbb2222");
            Assert.True(sel.Ok);
            Assert.Equal("bbbb2222ffff", sel.Route.Id);
            Assert.Equal("id-prefix", sel.MatchKind);
        }

        [Fact]
        public void ResolveRoute_IdPrefixIsCaseInsensitive()
        {
            List<Route> routes = Routes(("abcdef0123", "Alpha"));
            RouteSelection sel = TestCommandRouteCommand.ResolveRoute(routes, "ABCDEF");
            Assert.True(sel.Ok);
            Assert.Equal("id-prefix", sel.MatchKind);
        }

        [Fact]
        public void ResolveRoute_AmbiguousPrefix_RejectsRouteAmbiguous()
        {
            List<Route> routes = Routes(("aaaa1111", "Alpha"), ("aaaa2222", "Beta"));
            RouteSelection sel = TestCommandRouteCommand.ResolveRoute(routes, "aaaa");
            Assert.False(sel.Ok);
            Assert.Equal(TestCommandRouteCommand.RouteAmbiguousReason, sel.RejectReason);
            Assert.Equal(2, sel.Matches);
        }

        // An exact id wins outright, so an unrelated route appearing later can never
        // turn a caller's precise handle into an ambiguity.
        [Fact]
        public void ResolveRoute_ExactIdBeatsAnAmbiguousPrefix()
        {
            List<Route> routes = Routes(("aaaa", "Alpha"), ("aaaa1111", "Beta"));
            RouteSelection sel = TestCommandRouteCommand.ResolveRoute(routes, "aaaa");
            Assert.True(sel.Ok);
            Assert.Equal("Alpha", sel.Route.Name);
            Assert.Equal("id", sel.MatchKind);
        }

        [Fact]
        public void ResolveRoute_UniqueName_Matches()
        {
            List<Route> routes = Routes(("aaaa1111", "Runway Fuel Run"), ("bbbb2222", "Beta"));
            RouteSelection sel = TestCommandRouteCommand.ResolveRoute(routes, "Runway Fuel Run");
            Assert.True(sel.Ok);
            Assert.Equal("aaaa1111", sel.Route.Id);
            Assert.Equal("name", sel.MatchKind);
        }

        // RouteBuilder generates DEFAULT names, so duplicates are ordinary rather than
        // exotic; an arbitrary pick would silently operate the wrong route.
        [Fact]
        public void ResolveRoute_DuplicateName_RejectsRouteAmbiguous()
        {
            List<Route> routes = Routes(("aaaa1111", "Supply Run"), ("bbbb2222", "Supply Run"));
            RouteSelection sel = TestCommandRouteCommand.ResolveRoute(routes, "Supply Run");
            Assert.False(sel.Ok);
            Assert.Equal(TestCommandRouteCommand.RouteAmbiguousReason, sel.RejectReason);
        }

        [Fact]
        public void ResolveRoute_NoMatch_RejectsUnknownRoute()
        {
            List<Route> routes = Routes(("aaaa1111", "Alpha"));
            RouteSelection sel = TestCommandRouteCommand.ResolveRoute(routes, "zzzz");
            Assert.False(sel.Ok);
            Assert.Equal(TestCommandRouteCommand.UnknownRouteReason, sel.RejectReason);
        }

        // ---- ClassifyCreateRefusal ----

        [Fact]
        public void ClassifyCreateRefusal_EligibleCandidate_IsNone()
        {
            Assert.Equal(RouteCreateRefusal.None,
                TestCommandRouteCommand.ClassifyCreateRefusal(
                    treeFound: true, dismissed: false, treeSealed: true,
                    analysisEligible: true, alreadyPromoted: false));
        }

        // The gate order mirrors RouteCandidateFinder.DeriveCandidates' own walk
        // (dismissed -> sealed -> eligible -> promoted) so the token names the FIRST
        // closed gate. Each row below fails EVERY later gate too, so a reordering
        // shows up as the wrong token rather than as a silent pass.
        [Fact]
        public void ClassifyCreateRefusal_NamesTheFirstClosedGate()
        {
            Assert.Equal(RouteCreateRefusal.UnknownTree,
                TestCommandRouteCommand.ClassifyCreateRefusal(false, true, false, false, true));
            Assert.Equal(RouteCreateRefusal.CandidateDismissed,
                TestCommandRouteCommand.ClassifyCreateRefusal(true, true, false, false, true));
            Assert.Equal(RouteCreateRefusal.TreeNotSealed,
                TestCommandRouteCommand.ClassifyCreateRefusal(true, false, false, false, true));
            Assert.Equal(RouteCreateRefusal.CandidateIneligible,
                TestCommandRouteCommand.ClassifyCreateRefusal(true, false, true, false, true));
            Assert.Equal(RouteCreateRefusal.CandidateAlreadyPromoted,
                TestCommandRouteCommand.ClassifyCreateRefusal(true, false, true, true, true));
        }

        [Fact]
        public void RefusalToken_MapsEveryRefusal()
        {
            Assert.Equal("unknown-tree",
                TestCommandRouteCommand.RefusalToken(RouteCreateRefusal.UnknownTree));
            Assert.Equal("candidate-dismissed",
                TestCommandRouteCommand.RefusalToken(RouteCreateRefusal.CandidateDismissed));
            Assert.Equal("tree-not-sealed",
                TestCommandRouteCommand.RefusalToken(RouteCreateRefusal.TreeNotSealed));
            Assert.Equal("candidate-ineligible",
                TestCommandRouteCommand.RefusalToken(RouteCreateRefusal.CandidateIneligible));
            Assert.Equal("candidate-already-promoted",
                TestCommandRouteCommand.RefusalToken(RouteCreateRefusal.CandidateAlreadyPromoted));
            Assert.Null(TestCommandRouteCommand.RefusalToken(RouteCreateRefusal.None));
        }

        // The analysis status rides as a compound tail so a lane learns WHY the
        // analysis rejected, not merely that it did. The harness classifies off the
        // head token (the refly-gate shape), so the tail costs nothing there.
        [Fact]
        public void RefusalMsg_IneligibleCarriesTheAnalysisStatus()
        {
            Assert.Equal("candidate-ineligible MissingRouteProof",
                TestCommandRouteCommand.RefusalMsg(
                    RouteCreateRefusal.CandidateIneligible,
                    RouteAnalysisStatus.MissingRouteProof));
            Assert.Equal("candidate-ineligible UndockedStartOrigin",
                TestCommandRouteCommand.RefusalMsg(
                    RouteCreateRefusal.CandidateIneligible,
                    RouteAnalysisStatus.UndockedStartOrigin));
            // Every other refusal is a bare token - the status would be noise.
            Assert.Equal("tree-not-sealed",
                TestCommandRouteCommand.RefusalMsg(
                    RouteCreateRefusal.TreeNotSealed, RouteAnalysisStatus.Eligible));
        }

        [Fact]
        public void CompoundMsgs_CarryTheirDetailAndNeverGoEmpty()
        {
            // `source-no-longer-eligible` rather than `interval-below-transit`: the
            // funnel always passes allowIntervalBelowTransit:true, so the builder
            // CLAMPS a short interval instead of rejecting it and that reason is
            // unreachable from this verb. An example that pins an unreachable token
            // teaches a spec author the wrong thing to grep for.
            Assert.Equal("route-build-rejected source-no-longer-eligible",
                TestCommandRouteCommand.BuildRejectedMsg("source-no-longer-eligible"));
            Assert.Equal("route-build-rejected unknown",
                TestCommandRouteCommand.BuildRejectedMsg(null));
            Assert.Equal("route-action-refused activate",
                TestCommandRouteCommand.ActionRefusedMsg("activate"));
            Assert.Equal("route-action-refused unknown",
                TestCommandRouteCommand.ActionRefusedMsg(""));
        }

        private static List<Route> Routes(params (string id, string name)[] rows)
        {
            var list = new List<Route>();
            foreach ((string id, string name) in rows)
                list.Add(new Route { Id = id, Name = name });
            return list;
        }
    }

    /// <summary>
    /// End-to-end coverage for what the two logistics verbs actually DRIVE, using the
    /// production surfaces they call: the candidacy gate the seal opens
    /// (<c>RouteCandidateFinder</c>), the shared create funnel
    /// (<c>RouteCreationService</c>, which the Logistics window's "Create Route"
    /// button now also calls), and the three <c>RouteOrchestrator</c> operations.
    ///
    /// <para>The fixture is the committed ground-supply shape
    /// (<see cref="RouteWindowFixtures"/>, save <c>logistics-rover-a</c>): a
    /// KSC-Runway launch, a surface dock, a fuel + inventory transfer.</para>
    /// </summary>
    [Collection("Sequential")]
    public class TestCommandLogisticsVerbSeamTests : IDisposable
    {
        private const string TreeId = "tree-seam-supply";
        private const string RootRecordingId = "seam-rover-root";
        private const string DockChildRecordingId = "seam-rover-dock-merge";
        private const string BranchPointId = "bp-seam-rover-dock";

        private readonly List<string> logLines = new List<string>();

        public TestCommandLogisticsVerbSeamTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ResourceTransferability.ResetForTesting();
            RouteStore.ResetForTesting();
        }

        public void Dispose()
        {
            RouteStore.ResetForTesting();
            ResourceTransferability.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // SealSlot: what the seal is FOR
        // ------------------------------------------------------------------

        // catches: the candidacy gate the verb exists to open moving. H35's fixture is
        // documented as deliberately NOT a candidate for exactly this reason - one
        // CommittedProvisional member and the whole tree is invisible to the finder.
        [Fact]
        public void UnsealedTree_IsNotACandidate_AndSealingMakesItOne()
        {
            RecordingTree tree = BuildSupplyTree();
            tree.Recordings[DockChildRecordingId].MergeState = MergeState.CommittedProvisional;

            Assert.False(RouteCandidateFinder.IsTreeFullySealed(tree));
            Assert.Empty(RouteCandidateFinder.DeriveCandidates(
                new List<RecordingTree> { tree }, new List<Route>()));

            // What the seal does, stated as the decision core sees it: one member open,
            // and after the production flip nothing is.
            List<string> pending = TestCommandSealSlot.RecordingIdsNeedingSeal(tree);
            Assert.Equal(new[] { DockChildRecordingId }, pending);

            tree.Recordings[DockChildRecordingId].MergeState = MergeState.Immutable;

            Assert.Equal(0, TestCommandSealSlot.CountUnsealed(tree));
            Assert.Equal("OK", TestCommandSealSlot.ClassifyTreeSealVerdict(
                TestCommandSealSlot.CountUnsealed(tree)));
            Assert.True(RouteCandidateFinder.IsTreeFullySealed(tree));
            Assert.Single(RouteCandidateFinder.DeriveCandidates(
                new List<RecordingTree> { tree }, new List<Route>()));
        }

        // The gate inputs the create executor feeds ClassifyCreateRefusal, read off the
        // real finder rather than asserted by hand: an unsealed tree must produce the
        // tree-not-sealed token that tells a lane to run SealSlot first.
        [Fact]
        public void CreateGate_UnsealedTree_ClassifiesTreeNotSealed()
        {
            RecordingTree tree = BuildSupplyTree();
            tree.Recordings[RootRecordingId].MergeState = MergeState.CommittedProvisional;

            RouteCreateRefusal refusal = TestCommandRouteCommand.ClassifyCreateRefusal(
                treeFound: true,
                dismissed: RouteStore.IsCandidateDismissed(TreeId),
                treeSealed: RouteCandidateFinder.IsTreeFullySealed(tree),
                analysisEligible: false,
                alreadyPromoted: false);

            Assert.Equal(RouteCreateRefusal.TreeNotSealed, refusal);
            Assert.Equal("tree-not-sealed",
                TestCommandRouteCommand.RefusalMsg(refusal, RouteAnalysisStatus.Eligible));
        }

        // A sealed but analysis-rejected tree must carry the ANALYSIS token, not a
        // generic refusal: the fix for "no route proof" is a different flight, and the
        // status is the only thing that says so.
        [Fact]
        public void CreateGate_IneligibleTree_CarriesTheAnalysisStatus()
        {
            // Strip the root's origin proof: a sealed tree that starts undocked.
            RecordingTree tree = BuildSupplyTree();
            tree.Recordings[RootRecordingId].StartBodyName = null;
            tree.Recordings[RootRecordingId].LaunchSiteName = null;

            Assert.True(RouteCandidateFinder.IsTreeFullySealed(tree));
            RouteAnalysisResult analysis =
                RouteAnalysisEngine.AnalyzeTree(tree, RouteAnalysisLogMode.Quiet);
            Assert.False(analysis.IsEligible);

            RouteCreateRefusal refusal = TestCommandRouteCommand.ClassifyCreateRefusal(
                true, false, true, analysis.IsEligible, false);
            Assert.Equal(RouteCreateRefusal.CandidateIneligible, refusal);
            Assert.Equal("candidate-ineligible " + analysis.Status,
                TestCommandRouteCommand.RefusalMsg(refusal, analysis.Status));
        }

        // ------------------------------------------------------------------
        // RouteCommand action=create: the shared production funnel
        // ------------------------------------------------------------------

        // catches: the seam verb and the Logistics window's "Create Route" button
        // drifting apart. Both call this one method, so what is pinned here (Paused,
        // stored, manual loop cleared, snapped cadence) is what a player click does.
        [Fact]
        public void CreatePausedFromCandidate_StoresAPausedRouteThroughTheProductionFunnel()
        {
            RouteCandidate candidate = BuildCandidate();
            double span = RouteWindowFixtures.RoverDockUT - RouteWindowFixtures.RoverRootSpanStartUT;

            RouteCreationService.RouteCreateOutcome outcome =
                RouteCreationService.CreatePausedFromCandidate(
                    candidate, "Seam Fuel Run", span, Game.Modes.SANDBOX, currentUT: 1000.0);

            Assert.Null(outcome.RejectReason);
            Assert.NotNull(outcome.Route);
            Route route = outcome.Route;

            // Paused, never Active: the operator verifies with send-once first, and an
            // activate is a separate explicit act.
            Assert.Equal(RouteStatus.Paused, route.Status);
            Assert.Equal("Seam Fuel Run", route.Name);
            Assert.Single(route.Stops);

            // The interval is SNAPPED to N * TransitDuration by the builder; feeding the
            // rendered span back yields the N=1 floor.
            Assert.Equal(1, route.CadenceMultiplier);
            Assert.Equal(span, route.TransitDuration, 6);
            Assert.Equal(span, route.DispatchInterval, 6);

            // Stored, and findable by the selector the operation actions use.
            Route stored;
            Assert.True(RouteStore.TryGetRoute(route.Id, out stored));
            Assert.Same(route, stored);
            RouteSelection sel = TestCommandRouteCommand.ResolveRoute(
                RouteStore.CommittedRoutes, route.Id.Substring(0, 8));
            Assert.True(sel.Ok);
            Assert.Same(route, sel.Route);

            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("CreatePausedFromCandidate created")
                && l.Contains("status=Paused"));
        }

        // Absent name= must leave the builder's default naming alone rather than
        // stamping an empty string over it.
        [Fact]
        public void CreatePausedFromCandidate_NullName_LetsTheBuilderName()
        {
            RouteCandidate candidate = BuildCandidate();
            double span = RouteWindowFixtures.RoverDockUT - RouteWindowFixtures.RoverRootSpanStartUT;

            RouteCreationService.RouteCreateOutcome outcome =
                RouteCreationService.CreatePausedFromCandidate(
                    candidate, null, span, Game.Modes.SANDBOX, currentUT: 1000.0);

            Assert.NotNull(outcome.Route);
            Assert.False(string.IsNullOrEmpty(outcome.Route.Name));
        }

        // The builder's own reject must surface verbatim as the compound tail so the
        // seam never reports a bare failure for a typed refusal. An interval below
        // transit is rejected only when the permissive flag is off, so drive the
        // reject the funnel CAN produce: a null analysis.
        [Fact]
        public void CreatePausedFromCandidate_NullCandidate_RejectsWithoutStoring()
        {
            RouteCreationService.RouteCreateOutcome outcome =
                RouteCreationService.CreatePausedFromCandidate(
                    null, null, 100.0, Game.Modes.SANDBOX, currentUT: 0.0);

            Assert.Null(outcome.Route);
            Assert.Equal(RouteCreationService.NullCandidateReason, outcome.RejectReason);
            Assert.Empty(RouteStore.CommittedRoutes);
            Assert.Equal("route-build-rejected null-candidate",
                TestCommandRouteCommand.BuildRejectedMsg(outcome.RejectReason));
        }

        // Once a route claims the source recording the tree stops being a candidate;
        // the executor reads exactly this and answers candidate-already-promoted.
        [Fact]
        public void CreatedRoute_MakesItsTreeAlreadyPromoted()
        {
            RouteCandidate candidate = BuildCandidate();
            double span = RouteWindowFixtures.RoverDockUT - RouteWindowFixtures.RoverRootSpanStartUT;
            RouteCreationService.CreatePausedFromCandidate(
                candidate, null, span, Game.Modes.SANDBOX, currentUT: 1000.0);

            Assert.Empty(RouteCandidateFinder.DeriveCandidates(
                new List<RecordingTree> { candidate.Tree },
                new List<Route>(RouteStore.CommittedRoutes)));

            Assert.Equal(RouteCreateRefusal.CandidateAlreadyPromoted,
                TestCommandRouteCommand.ClassifyCreateRefusal(
                    true, false, true, true, alreadyPromoted: true));
        }

        // ------------------------------------------------------------------
        // RouteCommand action=send-once | pause | activate
        // ------------------------------------------------------------------

        [Fact]
        public void Activate_OnAPausedRoute_Applies()
        {
            Route route = StoreMinimalRoute("route-activate", RouteStatus.Paused);

            Assert.True(RouteOrchestrator.TryActivate(route, 500.0));
            Assert.Equal(RouteStatus.Active, route.Status);
        }

        // TryActivate is Paused-only, so an already-Active route declines - and that
        // decline is the verb's ERROR route-action-refused, never a false OK.
        [Fact]
        public void Activate_OnAnActiveRoute_IsRefused()
        {
            Route route = StoreMinimalRoute("route-active", RouteStatus.Active);

            Assert.False(RouteOrchestrator.TryActivate(route, 500.0));
            Assert.Equal(RouteStatus.Active, route.Status);
            Assert.Equal("route-action-refused activate",
                TestCommandRouteCommand.ActionRefusedMsg(TestCommandRouteCommand.ActionActivate));
        }

        [Fact]
        public void Pause_OnAnActiveRoute_Applies_AndIsRefusedWhenAlreadyPaused()
        {
            Route route = StoreMinimalRoute("route-pause", RouteStatus.Active);

            Assert.True(RouteOrchestrator.TryPause(route, 500.0, null));
            Assert.Equal(RouteStatus.Paused, route.Status);

            // Idempotence is NOT silent success here: a second pause declines, which the
            // verb reports as ERROR. That is deliberate - "pause" answering OK on an
            // already-paused route would make a lane's assertion vacuous.
            Assert.False(RouteOrchestrator.TryPause(route, 500.0, null));
        }

        [Fact]
        public void SendOnce_OnAPausedRoute_ArmsOneCycle()
        {
            Route route = StoreMinimalRoute("route-sendonce", RouteStatus.Paused);

            Assert.True(RouteOrchestrator.TrySendOneCycleNow(route, 500.0));
            Assert.True(route.SendOnceArmed);
            Assert.True(route.PauseAfterCurrentCycle);
            Assert.Equal(RouteStatus.Active, route.Status);
        }

        // The statuses TrySendOneCycleNow refuses are the ones a lane must see as a
        // typed ERROR rather than as a green no-op.
        [Fact]
        public void SendOnce_OnAnInTransitRoute_IsRefused()
        {
            Route route = StoreMinimalRoute("route-intransit", RouteStatus.InTransit);

            Assert.False(RouteOrchestrator.TrySendOneCycleNow(route, 500.0));
            Assert.False(route.SendOnceArmed);
        }

        // The unknown / ambiguous selector rejects, read off the REAL store rather than
        // a hand-built list.
        [Fact]
        public void ResolveRoute_AgainstTheLiveStore_RejectsAnUnknownSelector()
        {
            StoreMinimalRoute("route-live", RouteStatus.Paused);

            RouteSelection miss = TestCommandRouteCommand.ResolveRoute(
                RouteStore.CommittedRoutes, "no-such-route");
            Assert.False(miss.Ok);
            Assert.Equal(TestCommandRouteCommand.UnknownRouteReason, miss.RejectReason);

            RouteSelection hit = TestCommandRouteCommand.ResolveRoute(
                RouteStore.CommittedRoutes, "route-live");
            Assert.True(hit.Ok);
        }

        // ------------------------------------------------------------------
        // Fixture
        // ------------------------------------------------------------------

        private static Route StoreMinimalRoute(string id, RouteStatus status)
        {
            var route = new Route
            {
                Id = id,
                Name = "Parsek Seam " + id,
                Status = status,
                CreatedUT = 0.0,
                NextDispatchUT = double.MaxValue,
                RecordingIds = new List<string>(),
                SourceRefs = new List<RouteSourceRef>(),
                Stops = new List<RouteStop> { new RouteStop() },
            };
            RouteStore.AddRoute(route);
            return route;
        }

        private static RouteCandidate BuildCandidate()
        {
            RecordingTree tree = BuildSupplyTree();
            RouteAnalysisResult analysis =
                RouteAnalysisEngine.AnalyzeTree(tree, RouteAnalysisLogMode.Quiet);
            Assert.True(analysis.IsEligible,
                $"the seam fixture must analyze Eligible, got {analysis.Status}");
            return new RouteCandidate { Tree = tree, Analysis = analysis };
        }

        /// <summary>The committed ground-supply shape: KSC-Runway root + a dock-merge
        /// child carrying the one surface delivery window. Every recording is born
        /// <see cref="MergeState.Immutable"/> (the Recording default), so the tree is
        /// SEALED unless a cell deliberately opens a member.</summary>
        private static RecordingTree BuildSupplyTree()
        {
            Recording root = Materialize(
                new RecordingBuilder("Supply Rover")
                    .WithRecordingId(RootRecordingId)
                    .WithLaunchIdentity(RouteWindowFixtures.RoverLaunchSiteName),
                RootRecordingId,
                treeOrder: 0,
                startUT: RouteWindowFixtures.RoverRootSpanStartUT,
                endUT: RouteWindowFixtures.RoverDockUT);

            Recording dockChild = Materialize(
                new RecordingBuilder("Supply Rover + Depot Rover")
                    .WithRecordingId(DockChildRecordingId)
                    .WithRouteConnectionWindow(RouteWindowFixtures.SurfaceDeliveryWindow()),
                DockChildRecordingId,
                treeOrder: 1,
                startUT: RouteWindowFixtures.RoverDockUT,
                endUT: RouteWindowFixtures.RoverUndockUT,
                parentBranchPointId: BranchPointId);

            var tree = new RecordingTree
            {
                Id = TreeId,
                RootRecordingId = RootRecordingId,
                ActiveRecordingId = DockChildRecordingId
            };
            tree.AddOrReplaceRecording(root);
            tree.AddOrReplaceRecording(dockChild);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = BranchPointId,
                ParentRecordingIds = new List<string> { RootRecordingId },
                ChildRecordingIds = new List<string> { DockChildRecordingId }
            });
            return tree;
        }

        private static Recording Materialize(
            RecordingBuilder builder,
            string recordingId,
            int treeOrder,
            double startUT,
            double endUT,
            string parentBranchPointId = null)
        {
            ConfigNode node = builder.BuildV3Metadata();

            var rec = new Recording { RecordingId = recordingId };
            RecordingTree.LoadRecordingResourceAndState(node, rec);

            rec.TreeId = TreeId;
            rec.TreeOrder = treeOrder;
            rec.ParentBranchPointId = parentBranchPointId;
            rec.StartBodyName = RouteWindowFixtures.RoverBodyName;
            rec.ExplicitStartUT = startUT;
            rec.ExplicitEndUT = endUT;
            return rec;
        }
    }
}
