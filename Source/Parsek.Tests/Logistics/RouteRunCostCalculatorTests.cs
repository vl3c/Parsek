using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the per-run net-cost arithmetic for a Supply Route. The effective
    /// ledger and the source-tree member-id set are injected as deterministic
    /// data, so these tests never touch the live ELS, ERS, or RecordingStore.
    /// They cover the Phase 4 matrix in
    /// docs/dev/plans/logistics-run-cost-display.md, including the gotcha-G1
    /// tree-scope guard and the gotcha-G7 unhydrated-snapshot suppression.
    /// </summary>
    [Collection("Sequential")]
    public class RouteRunCostCalculatorTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteRunCostCalculatorTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // --- Fixture builders ---------------------------------------------

        private static Route MakeKscRoute(
            string id = "route-1",
            string backingTreeId = "tree-1")
        {
            return new Route
            {
                Id = id,
                IsKscOrigin = true,
                BackingMissionTreeId = backingTreeId,
            };
        }

        private static GameAction MakeRecoveryRow(string recordingId, float funds)
        {
            return new GameAction
            {
                Type = GameActionType.FundsEarning,
                FundsSource = FundsEarningSource.Recovery,
                RecordingId = recordingId,
                FundsAwarded = funds,
            };
        }

        private static HashSet<string> TreeMembers(params string[] ids)
        {
            return new HashSet<string>(ids, StringComparer.Ordinal);
        }

        // ==================================================================
        // SumRecoveredCredits
        // ==================================================================

        // catches: a launch-only run wrongly crediting phantom recovery.
        [Fact]
        public void NoRecoveryRows_SumsZero()
        {
            var route = MakeKscRoute();
            var els = new List<GameAction>(); // empty ledger

            double sum = RouteRunCostCalculator.SumRecoveredCredits(
                route, els, TreeMembers("rec-a"), out int count);

            Assert.Equal(0.0, sum, 3);
            Assert.Equal(0, count);
        }

        // catches: a single recovery payout being dropped or double-counted.
        [Fact]
        public void SingleRecovery_InTree_Summed()
        {
            var route = MakeKscRoute();
            var els = new List<GameAction> { MakeRecoveryRow("rec-a", 7300f) };

            double sum = RouteRunCostCalculator.SumRecoveredCredits(
                route, els, TreeMembers("rec-a"), out int count);

            Assert.Equal(7300.0, sum, 3);
            Assert.Equal(1, count);
        }

        // catches: only the first/last recovery counting instead of all of them
        // (boosters dropped + transport recovered = multiple rows, must sum).
        [Fact]
        public void MultipleRecoveries_InTree_Summed()
        {
            var route = MakeKscRoute();
            var els = new List<GameAction>
            {
                MakeRecoveryRow("rec-booster", 1200f),
                MakeRecoveryRow("rec-transport", 6100f),
            };

            double sum = RouteRunCostCalculator.SumRecoveredCredits(
                route, els, TreeMembers("rec-booster", "rec-transport"), out int count);

            // 1200 + 6100 = 7300
            Assert.Equal(7300.0, sum, 3);
            Assert.Equal(2, count);
        }

        // The G1 REGRESSION GUARD: the recovery leg is post-undock, so its
        // RecordingId is a TREE member but NOT in Route.RecordingIds. The sum is
        // scoped to the whole source tree, so it must still be counted. Scoping
        // to Route.RecordingIds would silently return zero on a transport that
        // flies home after undocking.
        [Fact]
        public void RecoveryInTreeButNotRouteMembers_StillCounted()
        {
            var route = MakeKscRoute();
            // Route renders only [root..undock]; the route member set is just the
            // dock-child recording. The fly-home/recover leg "rec-flyhome" is a
            // DIFFERENT recording in the SAME tree, excluded from the route
            // member set but present in the resolved tree-member id set.
            route.RecordingIds.Add("rec-dockchild");

            var treeMembers = TreeMembers("rec-dockchild", "rec-flyhome");
            var els = new List<GameAction> { MakeRecoveryRow("rec-flyhome", 5400f) };

            double sum = RouteRunCostCalculator.SumRecoveredCredits(
                route, els, treeMembers, out int count);

            Assert.Equal(5400.0, sum, 3);
            Assert.Equal(1, count);
            // Prove the recovery row is NOT in the route member set, so the only
            // reason it counted is the tree-scoping.
            Assert.DoesNotContain("rec-flyhome", route.RecordingIds);
        }

        // catches: cross-tree leakage where a same-named craft's recovery in a
        // DIFFERENT tree inflates this route's recovered credits.
        [Fact]
        public void RecoveryFromDifferentTree_Excluded()
        {
            var route = MakeKscRoute();
            var treeMembers = TreeMembers("rec-mine-1", "rec-mine-2");
            var els = new List<GameAction>
            {
                MakeRecoveryRow("rec-mine-1", 1000f),
                MakeRecoveryRow("rec-other-tree", 9999f), // different tree, excluded
            };

            double sum = RouteRunCostCalculator.SumRecoveredCredits(
                route, els, treeMembers, out int count);

            Assert.Equal(1000.0, sum, 3);
            Assert.Equal(1, count);
        }

        // catches: a non-recovery funds earning (contract complete, milestone)
        // being summed as recovery.
        [Fact]
        public void NonRecoveryFundsEarning_Excluded()
        {
            var route = MakeKscRoute();
            var contractRow = new GameAction
            {
                Type = GameActionType.FundsEarning,
                FundsSource = FundsEarningSource.ContractComplete,
                RecordingId = "rec-a",
                FundsAwarded = 5000f,
            };
            var els = new List<GameAction>
            {
                contractRow,
                MakeRecoveryRow("rec-a", 800f),
            };

            double sum = RouteRunCostCalculator.SumRecoveredCredits(
                route, els, TreeMembers("rec-a"), out int count);

            Assert.Equal(800.0, sum, 3);
            Assert.Equal(1, count);
        }

        // catches: a superseded recovery still counting. The live ELS strips
        // superseded/tombstoned rows; the calculator reads whatever ELS hands
        // it. We feed an els list that ALREADY omits the superseded row to prove
        // the calculator simply honors the filtered input (it does not re-add it).
        [Fact]
        public void SupersededRecovery_ExcludedByElsInput()
        {
            var route = MakeKscRoute();
            // The superseded "rec-a" recovery is intentionally absent from this
            // list, mirroring what ComputeELS() returns after a supersede.
            var els = new List<GameAction> { MakeRecoveryRow("rec-b", 2000f) };

            double sum = RouteRunCostCalculator.SumRecoveredCredits(
                route, els, TreeMembers("rec-a", "rec-b"), out int count);

            // Only the surviving rec-b recovery is summed; rec-a is gone.
            Assert.Equal(2000.0, sum, 3);
            Assert.Equal(1, count);
        }

        // catches: an NRE / wrong count when the tree resolves to no members.
        [Fact]
        public void EmptyTreeMembers_SumsZero()
        {
            var route = MakeKscRoute();
            var els = new List<GameAction> { MakeRecoveryRow("rec-a", 500f) };

            double sum = RouteRunCostCalculator.SumRecoveredCredits(
                route, els, TreeMembers(), out int count);

            Assert.Equal(0.0, sum, 3);
            Assert.Equal(0, count);
        }

        // catches: an NRE on a null els (early-load tick before the ledger).
        [Fact]
        public void NullEls_SumsZero()
        {
            var route = MakeKscRoute();

            double sum = RouteRunCostCalculator.SumRecoveredCredits(
                route, null, TreeMembers("rec-a"), out int count);

            Assert.Equal(0.0, sum, 3);
            Assert.Equal(0, count);
        }

        // ==================================================================
        // Assemble (net arithmetic + applicability + G7 suppression)
        // ==================================================================

        // catches: a launch-only run not showing the full launch as net cost
        // (the transport is thrown away each cycle).
        [Fact]
        public void LaunchOnly_NoRecovery_NetEqualsLaunch()
        {
            var route = MakeKscRoute();
            var els = new List<GameAction>(); // no recovery

            var cost = RouteRunCostCalculator.Assemble(
                route, isCareer: true, launchCost: 12500.0, els, TreeMembers("rec-a"));

            Assert.True(cost.Applicable);
            Assert.True(cost.CostKnown);
            Assert.Equal(12500.0, cost.LaunchCost, 3);
            Assert.Equal(0.0, cost.RecoveredCredits, 3);
            Assert.Equal(12500.0, cost.NetCost, 3);
            Assert.Equal(0, cost.RecoveryEventCount);
        }

        // catches: the net not subtracting the recovered credits.
        [Fact]
        public void SingleRecovery_NetEqualsLaunchMinusPayout()
        {
            var route = MakeKscRoute();
            var els = new List<GameAction> { MakeRecoveryRow("rec-a", 7300f) };

            var cost = RouteRunCostCalculator.Assemble(
                route, isCareer: true, launchCost: 12500.0, els, TreeMembers("rec-a"));

            Assert.True(cost.Applicable);
            Assert.True(cost.CostKnown);
            Assert.Equal(7300.0, cost.RecoveredCredits, 3);
            // 12500 - 7300 = 5200
            Assert.Equal(5200.0, cost.NetCost, 3);
            Assert.Equal(1, cost.RecoveryEventCount);
        }

        // catches: a partial/multiple-recovery run not summing all payouts into
        // the net.
        [Fact]
        public void MultiplePartialRecoveries_NetUsesSum()
        {
            var route = MakeKscRoute();
            var els = new List<GameAction>
            {
                MakeRecoveryRow("rec-booster", 1200f),
                MakeRecoveryRow("rec-transport", 6100f),
            };

            var cost = RouteRunCostCalculator.Assemble(
                route, isCareer: true, launchCost: 12500.0, els,
                TreeMembers("rec-booster", "rec-transport"));

            Assert.Equal(7300.0, cost.RecoveredCredits, 3);
            // 12500 - 7300 = 5200
            Assert.Equal(5200.0, cost.NetCost, 3);
            Assert.Equal(2, cost.RecoveryEventCount);
        }

        // catches: a negative net leaking to the UI when an odd refund or value
        // bug makes recovered exceed launch. The net must floor at 0.
        [Fact]
        public void RecoveredExceedsLaunch_NetFloorsAtZero()
        {
            var route = MakeKscRoute();
            var els = new List<GameAction> { MakeRecoveryRow("rec-a", 14000f) };

            var cost = RouteRunCostCalculator.Assemble(
                route, isCareer: true, launchCost: 12500.0, els, TreeMembers("rec-a"));

            Assert.Equal(14000.0, cost.RecoveredCredits, 3);
            Assert.Equal(0.0, cost.NetCost, 3);
        }

        // catches: a cost line being shown outside Career. Non-Career means no
        // funds exist, so Applicable must be false (UI then suppresses).
        [Fact]
        public void NonCareer_NotApplicable()
        {
            var route = MakeKscRoute();
            var els = new List<GameAction> { MakeRecoveryRow("rec-a", 1000f) };

            var cost = RouteRunCostCalculator.Assemble(
                route, isCareer: false, launchCost: 12500.0, els, TreeMembers("rec-a"));

            Assert.False(cost.Applicable);
            Assert.False(cost.CostKnown);
        }

        // catches: a Career non-KSC-origin route showing a funds cost. Such a
        // route deducts physical cargo, not funds, so Applicable must be false.
        [Fact]
        public void NonKscOrigin_NotApplicable()
        {
            var route = MakeKscRoute();
            route.IsKscOrigin = false;
            var els = new List<GameAction> { MakeRecoveryRow("rec-a", 1000f) };

            var cost = RouteRunCostCalculator.Assemble(
                route, isCareer: true, launchCost: 12500.0, els, TreeMembers("rec-a"));

            Assert.False(cost.Applicable);
            Assert.False(cost.CostKnown);
        }

        // The G7 REGRESSION GUARD: a null / not-yet-hydrated VesselSnapshot makes
        // ComputeLaunchCost return 0. With launch == 0, NetCost would be
        // max(0, 0 - recovered) = 0, which must NOT render as "0 funds".
        // CostKnown == false is the flag the UI checks to suppress the line.
        [Fact]
        public void UnhydratedSnapshot_LaunchZero_CostUnknown()
        {
            var route = MakeKscRoute();
            var els = new List<GameAction>();

            var cost = RouteRunCostCalculator.Assemble(
                route, isCareer: true, launchCost: 0.0, els, TreeMembers("rec-a"));

            Assert.True(cost.Applicable);
            Assert.False(cost.CostKnown); // suppress the line, do not show "0 funds"
            Assert.Equal(0.0, cost.LaunchCost, 3);
        }

        // catches: a stale verbose log shape regressing (the log proves the path
        // executed and carries the numbers used for the tooltip/debug).
        [Fact]
        public void Assemble_EmitsVerboseRunCostLine()
        {
            var route = MakeKscRoute(id: "route-abc12345", backingTreeId: "tree-xyz67890");
            var els = new List<GameAction> { MakeRecoveryRow("rec-a", 300f) };

            RouteRunCostCalculator.Assemble(
                route, isCareer: true, launchCost: 1000.0, els, TreeMembers("rec-a"));

            // ShortId truncates to the first 8 chars: "route-abc12345" -> "route-ab".
            Assert.Contains(logLines, l =>
                l.Contains("[RouteRunCost]")
                && l.Contains("RunCost route=route-ab")
                && l.Contains("applicable=True")
                && l.Contains("known=True")
                && l.Contains("net=700"));
        }

        // ==================================================================
        // ResolveTreeRecordingIds (degenerate-route handling)
        // ==================================================================

        // catches: an NRE when a route has no backing tree id (degenerate route).
        [Fact]
        public void ResolveTreeRecordingIds_NullBackingTree_ReturnsEmpty()
        {
            var route = MakeKscRoute(backingTreeId: null);

            HashSet<string> ids = RouteRunCostCalculator.ResolveTreeRecordingIds(route);

            Assert.NotNull(ids);
            Assert.Empty(ids);
        }

        // catches: an NRE on a null route.
        [Fact]
        public void ResolveTreeRecordingIds_NullRoute_ReturnsEmpty()
        {
            HashSet<string> ids = RouteRunCostCalculator.ResolveTreeRecordingIds((Route)null);

            Assert.NotNull(ids);
            Assert.Empty(ids);
        }

        // catches: a tree id that does not resolve in CommittedTrees (no test
        // game state) silently throwing instead of degrading to recovered = 0.
        [Fact]
        public void ResolveTreeRecordingIds_UnresolvedTree_ReturnsEmpty()
        {
            var route = MakeKscRoute(backingTreeId: "tree-not-in-store");

            HashSet<string> ids = RouteRunCostCalculator.ResolveTreeRecordingIds(route);

            Assert.NotNull(ids);
            Assert.Empty(ids);
        }

        // ==================================================================
        // Candidate path (no Route object): AssembleForCandidate,
        // ResolveTreeRecordingIds(RecordingTree), ComputeForCandidate
        // ==================================================================

        private static RecordingTree MakeTree(string id, params string[] recordingIds)
        {
            var tree = new RecordingTree { Id = id };
            for (int i = 0; i < recordingIds.Length; i++)
                tree.Recordings[recordingIds[i]] = new Recording { RecordingId = recordingIds[i] };
            return tree;
        }

        // catches: the tree-direct id resolver dropping members or NRE-ing on a
        // populated tree (the candidate path reads tree.Recordings.Keys directly).
        [Fact]
        public void ResolveTreeRecordingIds_Tree_EnumeratesAllMembers()
        {
            RecordingTree tree = MakeTree("tree-1", "rec-a", "rec-b");

            HashSet<string> ids = RouteRunCostCalculator.ResolveTreeRecordingIds(tree);

            Assert.Equal(2, ids.Count);
            Assert.Contains("rec-a", ids);
            Assert.Contains("rec-b", ids);
        }

        // catches: an NRE on a null tree (a candidate with no owning tree).
        [Fact]
        public void ResolveTreeRecordingIds_Tree_Null_ReturnsEmpty()
        {
            HashSet<string> ids = RouteRunCostCalculator.ResolveTreeRecordingIds((RecordingTree)null);

            Assert.NotNull(ids);
            Assert.Empty(ids);
        }

        // catches: a candidate launch-only run not showing the full launch as net.
        [Fact]
        public void AssembleForCandidate_LaunchOnly_NetEqualsLaunch()
        {
            var els = new List<GameAction>();

            var cost = RouteRunCostCalculator.AssembleForCandidate(
                isCareer: true, isKscOrigin: true, launchCost: 12500.0, els,
                TreeMembers("rec-a"));

            Assert.True(cost.Applicable);
            Assert.True(cost.CostKnown);
            Assert.Equal(12500.0, cost.NetCost, 3);
            Assert.Equal(0, cost.RecoveryEventCount);
        }

        // catches: the candidate net not subtracting recovered credits (and the
        // G1 tree-scope: the recovery row is a tree member, not a route member,
        // and there is no Route object here at all).
        [Fact]
        public void AssembleForCandidate_WithRecovery_NetEqualsLaunchMinusPayout()
        {
            var els = new List<GameAction> { MakeRecoveryRow("rec-flyhome", 7300f) };

            var cost = RouteRunCostCalculator.AssembleForCandidate(
                isCareer: true, isKscOrigin: true, launchCost: 12500.0, els,
                TreeMembers("rec-dockchild", "rec-flyhome"));

            Assert.Equal(7300.0, cost.RecoveredCredits, 3);
            Assert.Equal(5200.0, cost.NetCost, 3);
            Assert.Equal(1, cost.RecoveryEventCount);
        }

        // catches: a candidate cost showing outside Career.
        [Fact]
        public void AssembleForCandidate_NonCareer_NotApplicable()
        {
            var els = new List<GameAction> { MakeRecoveryRow("rec-a", 1000f) };

            var cost = RouteRunCostCalculator.AssembleForCandidate(
                isCareer: false, isKscOrigin: true, launchCost: 12500.0, els,
                TreeMembers("rec-a"));

            Assert.False(cost.Applicable);
            Assert.False(cost.CostKnown);
        }

        // catches: a candidate cost showing for a non-KSC origin (deducts cargo,
        // not funds).
        [Fact]
        public void AssembleForCandidate_NonKscOrigin_NotApplicable()
        {
            var els = new List<GameAction>();

            var cost = RouteRunCostCalculator.AssembleForCandidate(
                isCareer: true, isKscOrigin: false, launchCost: 12500.0, els,
                TreeMembers("rec-a"));

            Assert.False(cost.Applicable);
            Assert.False(cost.CostKnown);
        }

        // The G7 candidate guard: an unhydrated snapshot makes launch 0, which
        // must read as cost-unknown (the UI suppresses, no "0 funds").
        [Fact]
        public void AssembleForCandidate_LaunchZero_CostUnknown()
        {
            var els = new List<GameAction>();

            var cost = RouteRunCostCalculator.AssembleForCandidate(
                isCareer: true, isKscOrigin: true, launchCost: 0.0, els,
                TreeMembers("rec-a"));

            Assert.True(cost.Applicable);
            Assert.False(cost.CostKnown);
        }

        // catches: the candidate net not flooring at 0 when recovered exceeds launch.
        [Fact]
        public void AssembleForCandidate_RecoveredExceedsLaunch_NetFloorsAtZero()
        {
            var els = new List<GameAction> { MakeRecoveryRow("rec-a", 20000f) };

            var cost = RouteRunCostCalculator.AssembleForCandidate(
                isCareer: true, isKscOrigin: true, launchCost: 12500.0, els,
                TreeMembers("rec-a"));

            Assert.Equal(0.0, cost.NetCost, 3);
        }

        // catches: ComputeForCandidate not walking the source snapshot for the
        // launch cost (end-to-end: snapshot part-cost walk + tree recovery sum).
        [Fact]
        public void ComputeForCandidate_WalksSnapshot_AndSumsTreeRecovery()
        {
            // A two-part snapshot: pod (1000) + tank (500) + 200 LF * 0.8/u = 160.
            // Launch = 1000 + 500 + 160 = 1660.
            var snapshot = new ConfigNode("VESSEL");
            ConfigNode pod = snapshot.AddNode("PART");
            pod.AddValue("name", "pod");
            ConfigNode tank = snapshot.AddNode("PART");
            tank.AddValue("name", "tank");
            ConfigNode res = tank.AddNode("RESOURCE");
            res.AddValue("name", "LiquidFuel");
            res.AddValue("amount", "200");

            var source = new Recording { RecordingId = "rec-src", VesselSnapshot = snapshot };
            RecordingTree tree = MakeTree("tree-1", "rec-src", "rec-flyhome");

            var els = new List<GameAction> { MakeRecoveryRow("rec-flyhome", 1000f) };

            float PartCost(string n) => n == "pod" ? 1000f : (n == "tank" ? 500f : 0f);
            float ResCost(string n) => n == "LiquidFuel" ? 0.8f : 0f;

            var cost = RouteRunCostCalculator.ComputeForCandidate(
                source, tree, isCareer: true, isKscOrigin: true, els, PartCost, ResCost);

            Assert.True(cost.Applicable);
            Assert.True(cost.CostKnown);
            Assert.Equal(1660.0, cost.LaunchCost, 3);
            Assert.Equal(1000.0, cost.RecoveredCredits, 3);
            Assert.Equal(660.0, cost.NetCost, 3);
            Assert.Equal(1, cost.RecoveryEventCount);
        }

        // catches: ComputeForCandidate NRE-ing or mis-flagging on a null /
        // unhydrated source snapshot (launch 0 -> cost unknown, G7).
        [Fact]
        public void ComputeForCandidate_NullSnapshot_CostUnknown()
        {
            var source = new Recording { RecordingId = "rec-src", VesselSnapshot = null };
            RecordingTree tree = MakeTree("tree-1", "rec-src");
            var els = new List<GameAction>();

            var cost = RouteRunCostCalculator.ComputeForCandidate(
                source, tree, isCareer: true, isKscOrigin: true, els,
                n => 0f, n => 0f);

            Assert.True(cost.Applicable);
            Assert.False(cost.CostKnown);
            Assert.Equal(0.0, cost.LaunchCost, 3);
        }

        // ==================================================================
        // IsCandidateKscOrigin (KSC gate derived from the tree ROOT, not the
        // dock-child source recording). See the SHOULD-FIX: the dock-merged
        // child carries LaunchSiteName == null, so a source-only check wrongly
        // reports non-KSC on the common docking flight.
        // ==================================================================

        // Builds a tree whose ROOT recording carries the launch-site / start-body
        // origin info, and a separate dock-child member that carries neither (the
        // common docking flight: the child started mid-flight at the dock).
        private static RecordingTree MakeDockingTree(
            string treeId,
            string rootId,
            string rootLaunchSite,
            string rootStartBody,
            string dockChildId)
        {
            var tree = new RecordingTree { Id = treeId, RootRecordingId = rootId };
            tree.Recordings[rootId] = new Recording
            {
                RecordingId = rootId,
                LaunchSiteName = rootLaunchSite,
                StartBodyName = rootStartBody,
            };
            tree.Recordings[dockChildId] = new Recording
            {
                RecordingId = dockChildId,
                LaunchSiteName = null,   // dock child has no launch site
                StartBodyName = "Kerbin",
            };
            return tree;
        }

        // THE FINDING'S REGRESSION GUARD: a two-recording docking tree whose
        // source (dock child) has LaunchSiteName == null but whose ROOT has a
        // launch site + StartBodyName == "Kerbin". Reading the predicate off the
        // dock child returns false (wrongly suppressing the cost block); reading
        // it off the tree root returns true, matching the built Route.IsKscOrigin.
        [Fact]
        public void IsCandidateKscOrigin_DockChildSource_RootHasLaunchSite_True()
        {
            RecordingTree tree = MakeDockingTree(
                "tree-1", "rec-root", "LaunchPad", "Kerbin", "rec-dockchild");
            Recording dockChild = tree.Recordings["rec-dockchild"];

            // Sanity: a source-only check (the OLD behavior) would be false.
            Assert.True(string.IsNullOrEmpty(dockChild.LaunchSiteName));

            bool ksc = RouteRunCostCalculator.IsCandidateKscOrigin(dockChild, tree);

            Assert.True(ksc);
        }

        // catches: the root resolution being skipped so a real KSC docking route
        // shows no cost block. Full end-to-end ComputeForCandidate on the docking
        // tree, with the snapshot hydrated on the dock-child source: Applicable
        // and CostKnown must both be true.
        [Fact]
        public void ComputeForCandidate_DockChildSource_RootKsc_Applicable()
        {
            var snapshot = new ConfigNode("VESSEL");
            ConfigNode pod = snapshot.AddNode("PART");
            pod.AddValue("name", "pod");

            RecordingTree tree = MakeDockingTree(
                "tree-1", "rec-root", "LaunchPad", "Kerbin", "rec-dockchild");
            // The source the candidate path feeds is the dock child, carrying the
            // hydrated snapshot (SourceRefs[0]); its LaunchSiteName is null.
            Recording dockChild = tree.Recordings["rec-dockchild"];
            dockChild.VesselSnapshot = snapshot;

            bool isKscOrigin = RouteRunCostCalculator.IsCandidateKscOrigin(dockChild, tree);
            var els = new List<GameAction>();

            var cost = RouteRunCostCalculator.ComputeForCandidate(
                dockChild, tree, isCareer: true, isKscOrigin, els,
                n => n == "pod" ? 1000f : 0f, n => 0f);

            Assert.True(cost.Applicable);
            Assert.True(cost.CostKnown);
            Assert.Equal(1000.0, cost.LaunchCost, 3);
        }

        // catches: the fallback regressing. When the tree has no resolvable root
        // (legacy single-recording flight: the source IS the root), the predicate
        // must read off the source recording directly.
        [Fact]
        public void IsCandidateKscOrigin_NoResolvableRoot_FallsBackToSource()
        {
            // Tree exists but RootRecordingId points at a missing member.
            var tree = new RecordingTree { Id = "tree-1", RootRecordingId = "rec-missing" };
            var source = new Recording
            {
                RecordingId = "rec-src",
                LaunchSiteName = "LaunchPad",
                StartBodyName = "Kerbin",
            };

            bool ksc = RouteRunCostCalculator.IsCandidateKscOrigin(source, tree);

            Assert.True(ksc);
        }

        // catches: a null tree NRE-ing instead of degrading to the source check.
        [Fact]
        public void IsCandidateKscOrigin_NullTree_UsesSource()
        {
            var source = new Recording
            {
                RecordingId = "rec-src",
                LaunchSiteName = "LaunchPad",
                StartBodyName = "Kerbin",
            };

            bool ksc = RouteRunCostCalculator.IsCandidateKscOrigin(source, null);

            Assert.True(ksc);
        }

        // catches: a genuinely non-KSC origin (root launched off-Kerbin, e.g. a
        // Mun-surface depot run) wrongly reading as KSC.
        [Fact]
        public void IsCandidateKscOrigin_RootNonKerbin_False()
        {
            RecordingTree tree = MakeDockingTree(
                "tree-1", "rec-root", "LaunchPad", "Mun", "rec-dockchild");
            Recording dockChild = tree.Recordings["rec-dockchild"];

            bool ksc = RouteRunCostCalculator.IsCandidateKscOrigin(dockChild, tree);

            Assert.False(ksc);
        }

        // catches: a root with no launch site at all (a flight that never touched
        // a launch site) reading as KSC.
        [Fact]
        public void IsCandidateKscOrigin_RootNoLaunchSite_False()
        {
            RecordingTree tree = MakeDockingTree(
                "tree-1", "rec-root", null, "Kerbin", "rec-dockchild");
            Recording dockChild = tree.Recordings["rec-dockchild"];

            bool ksc = RouteRunCostCalculator.IsCandidateKscOrigin(dockChild, tree);

            Assert.False(ksc);
        }

        // catches: a null source AND no resolvable root NRE-ing.
        [Fact]
        public void IsCandidateKscOrigin_NullSourceNoRoot_False()
        {
            bool ksc = RouteRunCostCalculator.IsCandidateKscOrigin(null, null);

            Assert.False(ksc);
        }
    }
}
