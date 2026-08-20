using System.Collections.Generic;
using System.Globalization;

namespace Parsek.InGameTests
{
    /// <summary>
    /// (design-dock-event-graph.md 16.2) The one automated proof that the dock-event graph reads
    /// the LIVE committed store correctly and that its host cache does not rebuild per call.
    ///
    /// <para><b>What it drives (all production code, no fakes).</b> Two synthetic trees registered
    /// through the real <c>RecordingStore.CommitTree</c>, carrying BOTH resolution shapes at once:
    /// the AB/CD CROSS-TREE dock (controller tree ta claims partner tree tb's vessel by pid +
    /// launch guid) and, later on the same controller tree, the A-&gt;D SAME-TREE RECOVERED dock
    /// (a single-parent dock whose target pid resolves to a terminal-stamped leaf in its own
    /// tree - the cross-session shape <c>FindLinks</c> deliberately skips). Then, in order:
    /// (1) <c>DockEventGraphCache.GetOrBuild()</c> over the live store, with the ERS-derived
    /// visibility predicate the host supplies; (2) both node statuses and their claimed
    /// recordings; (3) <c>TryDescribePartner</c> in BOTH directions on both nodes (the
    /// bidirectional naming contract - the controller side is the one that is unnamed today);
    /// (4) the <c>NodesByTreeId</c> double registration that lets the partner mission see a dock
    /// its own tree never recorded; (5) cache stability - a second call with an unchanged store
    /// returns the SAME graph instance.
    ///
    /// <para><b>Why in-game (not xUnit).</b> <c>DockEventGraphTests</c> drives the same math on
    /// hand-built tree lists with an injected predicate. Only this cell exercises the HOST: the
    /// real committed store as the tree source, <c>EffectiveState.ComputeERS()</c> as the
    /// visibility source, and the real topology signature. Catches the graph working headless
    /// while mis-reading the live store, or rebuilding on every UI frame.
    ///
    /// <para><b>Isolation.</b> Batch-safe by construction: both synthetic trees are removed via
    /// <c>RemoveCommittedTreeById</c>, their sidecars reaped via <c>InGameTestSidecarReaper</c>
    /// (<c>CommitTree</c> flushes a .prec / .pann / .prec.txt per recording that memory-only tree
    /// removal cannot reach - the orphan leak M1-mission-loop-unit caught live 2026-07-26), the
    /// graph cache invalidated, and the log seams restored - regardless of pass / fail / skip.
    /// <c>MissionStore</c> is deliberately NOT touched: the mission-name resolver is injected as
    /// a lambda, which is exactly how the pure core stays free of Mission references. All ids are
    /// Guid-prefixed (<c>dockgraph-*</c>) so they can never collide with the InjectAllRecordings
    /// fixtures or live recordings.
    /// </summary>
    public sealed class DockEventGraphInGameTest
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private const string Tag = "TestRunner";

        // A clean synthetic recorded time base, well clear of a live UT=0 cold load.
        private const double T0 = 5_100_000.0;
        private const double PreDockEndUT = T0 + 100.0;     // B0 recorded end (pre-dock solo)
        private const double DockUT = T0 + 150.0;           // cross-tree Dock BP
        private const double UndockUT = T0 + 300.0;         // undock fork (B departs)
        private const double OffshootEndUT = T0 + 380.0;    // B1 recorded end
        private const double D0StartUT = T0 + 320.0;        // D's own solo line (same tree)
        private const double D0EndUT = T0 + 360.0;
        private const double Dock2UT = T0 + 500.0;          // same-tree recovered Dock BP
        private const double AdEndUT = T0 + 600.0;

        // Craft-baked pids for the three synthetic launches. Random-uint space; the guid gate
        // (fresh Guid-per-run RecordedVesselGuid) makes a live-pid collision inert.
        private const uint PidA = 4_041_400_001u;
        private const uint PidB = 4_041_400_002u;
        private const uint PidD = 4_041_400_003u;

        [InGameTest(Category = "Missions", Scene = GameScenes.SPACECENTER,
            Description = "Dock-event graph over the LIVE committed store: a cross-tree dock and a same-tree recovered dock in one fixture resolve to CrossTree / SameTreeRecovered with the right claimed recordings, TryDescribePartner names both sides of both docks, the cross-tree node registers under BOTH trees, and a second DockEventGraphCache.GetOrBuild() with an unchanged store returns the SAME instance (signature stability, no per-call rebuild)")]
        public void DockEventGraph_LiveStore_ResolvesBothShapes_NamesBothSides_CachesByStructure()
        {
            string prefix = "dockgraph-" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
            string tbId = prefix + "-tb";
            string taId = prefix + "-ta";
            string idB0 = prefix + "-B0";
            string idA0 = prefix + "-A0";
            string idAB = prefix + "-AB";
            string idA1 = prefix + "-A1";
            string idB1 = prefix + "-B1";
            string idD0 = prefix + "-D0";
            string idAD = prefix + "-AD";
            string dockBpId = prefix + "-dockbp";
            string undockBpId = prefix + "-undockbp";
            string dock2BpId = prefix + "-dockbp2";
            string guidA = System.Guid.NewGuid().ToString();
            string guidB = System.Guid.NewGuid().ToString();
            string guidD = System.Guid.NewGuid().ToString();

            string nameB0 = prefix + " Vessel B";
            string nameAB = prefix + " Stack AB";
            string nameD0 = prefix + " Depot D";
            string nameAD = prefix + " Stack AD";

            System.Action<string> prevObserver = ParsekLog.TestObserverForTesting;
            bool prevSuppress = ParsekLog.SuppressLogging;
            bool prevGraphSuppress = DockEventGraph.SuppressLogging;
            bool prevCrossTreeSuppress = MissionCrossTreeDock.SuppressLogging;
            bool tbCommitted = false;
            bool taCommitted = false;
            var captured = new List<string>();

            try
            {
                // Tee (not replace) the log stream so the build summary is capturable while
                // KSP.log still receives everything.
                ParsekLog.SuppressLogging = false;
                DockEventGraph.SuppressLogging = false;
                MissionCrossTreeDock.SuppressLogging = false;
                ParsekLog.TestObserverForTesting = line =>
                {
                    captured.Add(line);
                    prevObserver?.Invoke(line);
                };

                // ---- (a) Two synthetic committed trees carrying BOTH dock shapes. ----

                RecordingTree tb = BuildPartnerTree(tbId, idB0, guidB, nameB0, prefix);
                RecordingTree ta = BuildControllerTree(
                    taId, idA0, idAB, idA1, idB1, idD0, idAD,
                    dockBpId, undockBpId, dock2BpId, guidA, guidB, guidD,
                    nameAB, nameD0, nameAD, prefix);
                RecordingStore.CommitTree(tb);
                tbCommitted = true;
                RecordingStore.CommitTree(ta);
                taCommitted = true;

                // ---- (1) Build through the HOST against the live store. ----

                DockEventGraphCache.InvalidateForTesting();
                captured.Clear();
                DockEventGraph graph = DockEventGraphCache.GetOrBuild();
                InGameAssert.IsNotNull(graph, "DockEventGraphCache must return a graph");
                InGameAssert.IsFalse(string.IsNullOrEmpty(graph.BuildSignature),
                    "the host must stamp its topology signature on the built graph");

                bool sawBuildLine = false;
                for (int i = 0; i < captured.Count; i++)
                {
                    if (captured[i].Contains("[DockGraph]") && captured[i].Contains("build nodes="))
                    {
                        sawBuildLine = true;
                        break;
                    }
                }
                InGameAssert.IsTrue(sawBuildLine,
                    "a host-driven rebuild must leave the one grep-stable [DockGraph] summary line");

                // ---- (2) Both resolution shapes, read off the LIVE store. ----

                DockEventNode crossNode = FindNode(graph, dockBpId);
                InGameAssert.IsNotNull(crossNode, "the cross-tree Dock branch point must lift into the graph");
                InGameAssert.AreEqual(DockPartnerStatus.CrossTree, crossNode.Partner.Status,
                    "a dock whose target pid resolves in ANOTHER committed tree resolves CrossTree");
                InGameAssert.AreEqual(idB0, crossNode.Partner.PartnerRecordingId,
                    "the cross-tree claim must land on B's own pre-dock recording");
                InGameAssert.AreEqual(tbId, crossNode.Partner.PartnerTreeId,
                    "the cross-tree claim must name the partner's tree");
                InGameAssert.AreEqual(idAB, crossNode.MergedChildRecordingId,
                    "the merged child must be the combined docked stretch");
                InGameAssert.IsFalse(crossNode.VisibilityFlagged,
                    "a live (non-superseded) merged child must not be visibility-flagged");

                DockEventNode sameTreeNode = FindNode(graph, dock2BpId);
                InGameAssert.IsNotNull(sameTreeNode, "the same-tree Dock branch point must lift into the graph");
                InGameAssert.AreEqual(DockPartnerStatus.SameTreeRecovered, sameTreeNode.Partner.Status,
                    "a single-parent dock whose target pid resolves inside its OWN tree resolves SameTreeRecovered");
                InGameAssert.AreEqual(idD0, sameTreeNode.Partner.PartnerRecordingId,
                    "the same-tree recovery must claim D's own terminal-stamped line, not the stack");
                InGameAssert.AreEqual(taId, sameTreeNode.Partner.PartnerTreeId,
                    "a same-tree recovery stays inside the owning tree");

                DockEventNode undockNode = FindNode(graph, undockBpId);
                InGameAssert.IsNotNull(undockNode, "the Undock branch point must lift into the graph");
                InGameAssert.IsFalse(undockNode.IsMerge, "an Undock node is not a merge");
                InGameAssert.IsNull(undockNode.Partner.PartnerRecordingId,
                    "an Undock node carries no partner resolution");

                // ---- (3) Bidirectional naming through the injected resolver. ----

                System.Func<string, string, string> resolver =
                    (treeId, recId) => treeId == tbId ? "Partner Mission" : "Controller Mission";

                DockPartnerDescription d;
                InGameAssert.IsTrue(DockEventGraph.TryDescribePartner(
                        graph, dockBpId, idA0, resolver, out d),
                    "the controller side must name its dock partner (the side that is unnamed today)");
                InGameAssert.AreEqual(nameB0, d.PartnerVesselName, "controller side sees B's vessel name");
                InGameAssert.AreEqual(idB0, d.PartnerRecordingId, "controller side points at B's recording");
                InGameAssert.AreEqual("Partner Mission", d.PartnerMissionName,
                    "the injected mission-name resolver must be plumbed through");

                InGameAssert.IsTrue(DockEventGraph.TryDescribePartner(
                        graph, dockBpId, idB0, resolver, out d),
                    "the partner side must name the merged stack");
                InGameAssert.AreEqual(nameAB, d.PartnerVesselName, "partner side sees the combined stack");
                InGameAssert.AreEqual(idAB, d.PartnerRecordingId, "partner side points at the docked stretch");
                InGameAssert.AreEqual("Controller Mission", d.PartnerMissionName,
                    "the partner side's resolver call must use the CONTROLLER tree");

                InGameAssert.IsTrue(DockEventGraph.TryDescribePartner(
                        graph, dock2BpId, idA1, resolver, out d),
                    "the same-tree dock must name D from A's line");
                InGameAssert.AreEqual(nameD0, d.PartnerVesselName, "A's line sees D");
                InGameAssert.IsTrue(DockEventGraph.TryDescribePartner(
                        graph, dock2BpId, idD0, resolver, out d),
                    "the same-tree dock must name the stack from D's line (the A->D recovery's whole point)");
                InGameAssert.AreEqual(nameAD, d.PartnerVesselName, "D's line sees the AD stack");
                InGameAssert.AreEqual(idAD, d.PartnerRecordingId, "D's line points at the AD recording");

                // ---- (4) The cross-tree node is visible from BOTH trees. ----

                InGameAssert.IsTrue(ContainsNode(graph.NodesForTree(taId), dockBpId),
                    "the owning tree must see its own dock node");
                InGameAssert.IsTrue(ContainsNode(graph.NodesForTree(tbId), dockBpId),
                    "the PARTNER tree must see the dock its own tree never recorded (NodesByTreeId double registration)");
                InGameAssert.IsFalse(ContainsNode(graph.NodesForTree(tbId), dock2BpId),
                    "a same-tree recovery must not leak into an unrelated tree's node list");
                InGameAssert.IsTrue(graph.AreDockConnected(taId, tbId),
                    "the two trees must read as dock-connected (the R6 advisory's adjacency)");

                // ---- (5) Signature stability: no rebuild on an unchanged store. ----

                DockEventGraph again = DockEventGraphCache.GetOrBuild();
                InGameAssert.IsTrue(ReferenceEquals(graph, again),
                    "a second GetOrBuild() with an unchanged committed store must return the SAME graph instance");

                ParsekLog.Info(Tag,
                    "DockEventGraph: PASS nodes=" + graph.Nodes.Count.ToString(IC)
                    + " crossBp=" + dockBpId
                    + " crossClaimed=" + (crossNode.Partner.PartnerRecordingId ?? "?")
                    + " sameTreeBp=" + dock2BpId
                    + " sameTreeClaimed=" + (sameTreeNode.Partner.PartnerRecordingId ?? "?")
                    + " pairs=" + graph.DockConnectedTreePairs.Count.ToString(IC)
                    + " cacheStable=True");
            }
            finally
            {
                // Restore the log seams first so the cleanup lines flow normally.
                ParsekLog.TestObserverForTesting = prevObserver;
                ParsekLog.SuppressLogging = prevSuppress;
                DockEventGraph.SuppressLogging = prevGraphSuppress;
                MissionCrossTreeDock.SuppressLogging = prevCrossTreeSuppress;

                int removedTrees = 0;
                if (tbCommitted && RecordingStore.RemoveCommittedTreeById(tbId, "DockEventGraph-cleanup"))
                    removedTrees++;
                if (taCommitted && RecordingStore.RemoveCommittedTreeById(taId, "DockEventGraph-cleanup"))
                    removedTrees++;
                // The removals bump StateVersion (so the signature moves anyway), but drop the
                // cached graph explicitly: nothing downstream should ever hold a graph built over
                // trees this test invented.
                DockEventGraphCache.InvalidateForTesting();

                // RemoveCommittedTreeById is memory-only; CommitTree already flushed sidecars for
                // every synthetic recording, and once their ids leave the store CleanOrphanFiles
                // preserves those files forever. Reap AFTER the removals so the reaper's
                // known-ids guard reads the post-removal store.
                int reaped = 0;
                if (tbCommitted || taCommitted)
                {
                    reaped = InGameTestSidecarReaper.DeleteSidecarsForIds(
                        new List<string> { idB0, idA0, idAB, idA1, idB1, idD0, idAD },
                        "DockEventGraph");
                }
                ParsekLog.Verbose(Tag,
                    "DockEventGraph cleanup: removedTrees=" + removedTrees.ToString(IC)
                    + " reapedSidecars=" + reaped.ToString(IC));
            }
        }

        // -----------------------------------------------------------------
        // Fixture builders (mirror the xUnit DockEventGraphTests shapes, but with
        // Guid-prefixed ids and full store registration)
        // -----------------------------------------------------------------

        // Partner tree tb: B's solo pre-dock flight [T0 .. PreDockEnd].
        private static RecordingTree BuildPartnerTree(
            string tbId, string idB0, string guidB, string nameB0, string prefix)
        {
            var tree = new RecordingTree { Id = tbId, RootRecordingId = idB0, TreeName = prefix + " Partner B" };
            Recording b0 = MakeLeg(idB0, tbId, PidB, guidB, prefix + "-CB", T0, PreDockEndUT,
                nameB0, pods: 0, probes: 1);
            tree.Recordings[b0.RecordingId] = b0;
            return tree;
        }

        // Controller tree ta:
        //   A0 [T0 .. Dock] -> Dock BP (target = B's pid, CROSS-TREE) -> AB [Dock .. Undock]
        //   -> Undock fork -> { A1 continuing [Undock .. Dock2], B1 departing [Undock .. OffshootEnd] }
        //   A1 -> Dock BP (target = D's pid, SAME-TREE RECOVERED) -> AD [Dock2 .. AdEnd]
        //   D0 [D0Start .. D0End] is D's own terminal-stamped leaf in the SAME tree, with no
        //   edge to the second dock - the exact cross-session shape FindLinks cannot see.
        private static RecordingTree BuildControllerTree(
            string taId, string idA0, string idAB, string idA1, string idB1, string idD0, string idAD,
            string dockBpId, string undockBpId, string dock2BpId,
            string guidA, string guidB, string guidD,
            string nameAB, string nameD0, string nameAD, string prefix)
        {
            var tree = new RecordingTree { Id = taId, RootRecordingId = idA0, TreeName = prefix + " Controller A" };

            Recording a0 = MakeLeg(idA0, taId, PidA, guidA, prefix + "-CA", T0, DockUT,
                prefix + " Vessel A", pods: 1, probes: 0);
            a0.ChildBranchPointId = dockBpId;
            Recording ab = MakeLeg(idAB, taId, PidA, guidA, prefix + "-CAB", DockUT, UndockUT,
                nameAB, pods: 1, probes: 1);
            ab.ParentBranchPointId = dockBpId;
            ab.ChildBranchPointId = undockBpId;
            // Continuing stack first (recorder convention), departing partner second.
            Recording a1 = MakeLeg(idA1, taId, PidA, guidA, prefix + "-CA2", UndockUT, Dock2UT,
                prefix + " Vessel A", pods: 1, probes: 0);
            a1.ParentBranchPointId = undockBpId;
            a1.ChildBranchPointId = dock2BpId;
            Recording b1 = MakeLeg(idB1, taId, PidB, guidB, prefix + "-CB2", UndockUT, OffshootEndUT,
                prefix + " Vessel B", pods: 0, probes: 1);
            b1.ParentBranchPointId = undockBpId;
            Recording d0 = MakeLeg(idD0, taId, PidD, guidD, prefix + "-CD", D0StartUT, D0EndUT,
                nameD0, pods: 0, probes: 1);
            Recording ad = MakeLeg(idAD, taId, PidA, guidA, prefix + "-CAD", Dock2UT, AdEndUT,
                nameAD, pods: 1, probes: 1);
            ad.ParentBranchPointId = dock2BpId;

            tree.Recordings[a0.RecordingId] = a0;
            tree.Recordings[ab.RecordingId] = ab;
            tree.Recordings[a1.RecordingId] = a1;
            tree.Recordings[b1.RecordingId] = b1;
            tree.Recordings[d0.RecordingId] = d0;
            tree.Recordings[ad.RecordingId] = ad;

            tree.BranchPoints.Add(new BranchPoint
            {
                Id = dockBpId,
                Type = BranchPointType.Dock,
                UT = DockUT,
                ParentRecordingIds = new List<string> { idA0 },
                ChildRecordingIds = new List<string> { idAB },
                TargetVesselPersistentId = PidB,
                MergeCause = "DOCK",
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = undockBpId,
                Type = BranchPointType.Undock,
                UT = UndockUT,
                ParentRecordingIds = new List<string> { idAB },
                ChildRecordingIds = new List<string> { idA1, idB1 },
                SplitCause = "UNDOCK",
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = dock2BpId,
                Type = BranchPointType.Dock,
                UT = Dock2UT,
                ParentRecordingIds = new List<string> { idA1 },
                ChildRecordingIds = new List<string> { idAD },
                TargetVesselPersistentId = PidD,
                MergeCause = "DOCK",
            });
            return tree;
        }

        private static Recording MakeLeg(
            string id, string treeId, uint pid, string guid, string chainId,
            double start, double end, string vesselName, int pods, int probes)
        {
            var rec = new Recording
            {
                RecordingId = id,
                TreeId = treeId,
                VesselName = vesselName,
                VesselPersistentId = pid,
                RecordedVesselGuid = guid,
                ChainId = chainId,
                ChainIndex = 0,
                ChainBranch = 0,
                IsDebris = false,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
            };
            var controllers = new List<ControllerInfo>();
            for (int i = 0; i < pods; i++) controllers.Add(new ControllerInfo { type = "CrewedPod" });
            for (int i = 0; i < probes; i++) controllers.Add(new ControllerInfo { type = "ProbeCore" });
            if (controllers.Count > 0) rec.Controllers = controllers;
            return rec;
        }

        // -----------------------------------------------------------------
        // Live-state helpers
        // -----------------------------------------------------------------

        private static DockEventNode FindNode(DockEventGraph graph, string branchPointId)
        {
            DockEventNode node;
            return graph.NodesByBranchPointId.TryGetValue(branchPointId, out node) ? node : null;
        }

        private static bool ContainsNode(List<DockEventNode> nodes, string branchPointId)
        {
            for (int i = 0; i < nodes.Count; i++)
                if (nodes[i] != null && nodes[i].BranchPointId == branchPointId)
                    return true;
            return false;
        }
    }
}
