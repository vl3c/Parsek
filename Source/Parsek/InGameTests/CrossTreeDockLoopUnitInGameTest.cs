using System.Collections.Generic;
using System.Globalization;

namespace Parsek.InGameTests
{
    /// <summary>
    /// (M-MIS-8 merge gate) The one automated proof that a cross-tree foreign-dock
    /// "Partner journey" inclusion drives the REAL production pipeline end to end
    /// inside KSP, replacing the manual two-vessel docking playtest (fly B and
    /// commit; fly A, dock to B, undock, commit; include the Partner journey row;
    /// loop B's mission).
    ///
    /// <para><b>What it drives (all production code, no fakes).</b> Two synthetic
    /// committed trees registered through the real <c>RecordingStore.CommitTree</c>:
    /// partner tree tb (vessel B's solo pre-dock flight) and controller tree ta
    /// (A's flight, the Dock branch point claiming B's pid, the combined docked
    /// stretch, and the undock fork where B departs). Then, in order: (1) link
    /// discovery via <c>MissionCrossTreeDock.FindLinks</c> over
    /// <c>RecordingStore.CommittedTrees</c> - the exact read-model that populates
    /// the Missions window's "Partner journey" row; (2) the include mutation the
    /// row's checkbox commits (<c>IncludedForeignDockLinkIds.Add</c> +
    /// <c>MissionStore.ClearLoopsConflictingWith</c>) with the spanned-set
    /// one-loop enforcement asserted at BOTH sites (toggle-time clear and
    /// <c>NormalizeOneLoopPerTree(trees)</c>); (3) the REAL
    /// <c>MissionLoopUnitBuilder.Build</c> with the LIVE
    /// <c>FlightGlobalsBodyInfo.Instance</c> - one shared span clock whose member
    /// windows land exactly on the recorded dock/undock UTs, periodicity / re-aim
    /// failing CLOSED to faithful (specific unit fields pinned, plus the logged
    /// reason line); (4) the cross-seam checkbox trim (excluding the
    /// docked-stretch interval key drops that member, the offshoot survives);
    /// (5) the sparse <c>foreignDockLink</c> codec key through the production
    /// <c>MissionStore.Save</c>/<c>Load</c>.
    ///
    /// <para><b>Why in-game (not xUnit).</b> The xUnit suite
    /// (<c>MissionCrossTreeDockTests</c>) drives the same math on hand-built lists
    /// with an inert <c>IBodyInfo</c>. This test runs the pipeline against the
    /// REAL stores (<c>CommitTree</c>'s committed lists ARE the engine alignment
    /// contract the loop unit indexes into) and the LIVE
    /// <c>FlightGlobalsBodyInfo.Instance</c> (real stock ephemerides), proving the
    /// fail-closed branch wins even when a non-degenerate body info is present -
    /// the one input xUnit can never supply.
    ///
    /// <para><b>What stays observational (not gated here).</b> The per-scene
    /// visual confirmation (B pre-dock ghost, combined ghost at the dock, B
    /// offshoot after undock, in FLIGHT / KSC / Tracking Station) rides the
    /// existing LoopUnit render machinery, which is not new in this PR - all
    /// three scenes consume the same unit through the global index contract.
    /// Collect that visual pass opportunistically in ordinary play.
    ///
    /// <para><b>Isolation.</b> Batch-safe by construction: the whole
    /// <c>MissionStore</c> is snapshotted to a ConfigNode up front and restored
    /// via the production <c>Load</c> in the finally, both synthetic trees are
    /// removed via <c>RemoveCommittedTreeById</c>, and the log observer is
    /// restored - regardless of pass / fail / skip. All ids are Guid-prefixed
    /// (<c>mmis8-xdock-*</c>) so they can never collide with the
    /// InjectAllRecordings fixtures or live recordings.
    /// </summary>
    public sealed class CrossTreeDockLoopUnitInGameTest
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private const string Tag = "TestRunner";

        // A clean synthetic recorded time base, well clear of a live UT=0 cold load.
        // B's recorded solo flight ends BEFORE the dock (the [PreDockEnd..Dock] gap is
        // an accepted contract: chronological gaps allowed, no interpolation invented).
        private const double T0 = 5_000_000.0;
        private const double PreDockEndUT = T0 + 100.0;    // B0 recorded end
        private const double DockUT = T0 + 150.0;          // foreign Dock BP (the merge)
        private const double UndockUT = T0 + 300.0;        // undock fork (B departs)
        private const double OffshootEndUT = T0 + 380.0;   // B1 recorded end
        private const double ForeignContEndUT = T0 + 400.0; // A1 (A's own continuation) end
        // Loop enabled AFTER the span end, so the faithful base anchor stays exactly at
        // the enable UT (the first-play floor max(anchor, spanEnd) is a no-op).
        private const double LoopEnableUT = T0 + 1000.0;

        // Craft-baked pids for the two synthetic launches. Random-uint space; the guid
        // gate (fresh Guid-per-run RecordedVesselGuid) makes a live-pid collision inert.
        private const uint PidA = 4_041_300_001u;
        private const uint PidB = 4_041_300_002u;

        [InGameTest(Category = "Missions", Scene = GameScenes.SPACECENTER,
            Description = "M-MIS-8 merge gate: two synthetic committed trees joined by a cross-tree dock link drive the REAL production path - Partner-journey link discovery (the Missions-row read-model), the include mutation with spanned-set one-loop enforcement (toggle + normalize), the REAL MissionLoopUnitBuilder with live FlightGlobalsBodyInfo (one shared span clock over B's pre-dock leg + the docked stretch + B's offshoot, windows exactly at the recorded dock/undock UTs, periodicity/re-aim fail closed to faithful with the logged reason), the cross-seam checkbox trim, and the sparse foreignDockLink codec round-trip")]
        public void CrossTreeDock_PartnerLoopUnit_SharedSpanClock_FailClosed_CodecRoundTrip()
        {
            string prefix = "mmis8-xdock-" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
            string tbId = prefix + "-tb";
            string taId = prefix + "-ta";
            string idB0 = prefix + "-B0";
            string idA0 = prefix + "-A0";
            string idAB = prefix + "-AB";
            string idA1 = prefix + "-A1";
            string idB1 = prefix + "-B1";
            string dockBpId = prefix + "-dockbp";
            string undockBpId = prefix + "-undockbp";
            string guidA = System.Guid.NewGuid().ToString();
            string guidB = System.Guid.NewGuid().ToString();

            // Snapshot every seam we mutate, restored in the finally regardless of outcome.
            var missionSnapshot = new ConfigNode("PARSEK_MMIS8_MISSION_SNAPSHOT");
            MissionStore.Save(missionSnapshot);
            System.Action<string> prevObserver = ParsekLog.TestObserverForTesting;
            bool prevSuppress = ParsekLog.SuppressLogging;
            // The fail-closed reason line is gated on this flag (the Missions window sets and
            // restores it per frame; a prior aborted caller could leak it true) - pin it false
            // for the Build calls below and restore whatever was there.
            bool prevBuilderSuppress = MissionLoopUnitBuilder.SuppressLogging;
            bool prevCrossTreeSuppress = MissionCrossTreeDock.SuppressLogging;
            bool tbCommitted = false;
            bool taCommitted = false;
            var captured = new List<string>();

            try
            {
                // The fail-closed branch only LOGS when a non-null bodyInfo is present; the
                // whole point of the in-game run is the live one.
                InGameAssert.IsNotNull(FlightGlobalsBodyInfo.Instance,
                    "FlightGlobalsBodyInfo.Instance must exist (the live IBodyInfo the builder consumes)");

                // Tee (not replace) the log stream so the fail-closed reason line is
                // capturable while KSP.log still receives everything.
                ParsekLog.SuppressLogging = false;
                MissionLoopUnitBuilder.SuppressLogging = false;
                MissionCrossTreeDock.SuppressLogging = false;
                ParsekLog.TestObserverForTesting = line =>
                {
                    captured.Add(line);
                    prevObserver?.Invoke(line);
                };

                // ---- (a) Two synthetic committed trees joined by a cross-tree dock link. ----

                RecordingTree tb = BuildPartnerTree(tbId, idB0, guidB, prefix);
                RecordingTree ta = BuildControllerTree(
                    taId, idA0, idAB, idA1, idB1, dockBpId, undockBpId, guidA, guidB, prefix);
                RecordingStore.CommitTree(tb);
                tbCommitted = true;
                RecordingStore.CommitTree(ta);
                taCommitted = true;

                // The committed instances + flat-list indices (the engine alignment contract
                // the loop unit's MemberIndices point into).
                RecordingTree tbCommittedTree = FindCommittedTree(tbId);
                RecordingTree taCommittedTree = FindCommittedTree(taId);
                InGameAssert.IsNotNull(tbCommittedTree, "partner tree must be in CommittedTrees after CommitTree");
                InGameAssert.IsNotNull(taCommittedTree, "controller tree must be in CommittedTrees after CommitTree");
                int idxB0 = FindCommittedIndex(idB0);
                int idxA0 = FindCommittedIndex(idA0);
                int idxAB = FindCommittedIndex(idAB);
                int idxA1 = FindCommittedIndex(idA1);
                int idxB1 = FindCommittedIndex(idB1);
                InGameAssert.IsTrue(idxB0 >= 0 && idxA0 >= 0 && idxAB >= 0 && idxA1 >= 0 && idxB1 >= 0,
                    "all five synthetic recordings must be in CommittedRecordings (indices "
                    + idxB0.ToString(IC) + "/" + idxA0.ToString(IC) + "/" + idxAB.ToString(IC)
                    + "/" + idxA1.ToString(IC) + "/" + idxB1.ToString(IC) + ")");

                // Production mission creation (the same path OnLoad takes for a new tree).
                int created = MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { tb, ta });
                InGameAssert.AreEqual(2, created, "one default mission per synthetic tree");
                Mission partnerMission = MissionStore.FindOriginalMission(tbId);
                Mission foreignMission = MissionStore.FindOriginalMission(taId);
                InGameAssert.IsNotNull(partnerMission, "partner (B) mission must exist");
                InGameAssert.IsNotNull(foreignMission, "foreign (A) mission must exist");

                // ---- (1) Link discovery: the exact read-model of the "Partner journey" row. ----

                List<ForeignDockLink> links = MissionCrossTreeDock.FindLinks(
                    tbCommittedTree, RecordingStore.CommittedTrees);
                ForeignDockLink link = null;
                for (int i = 0; i < links.Count; i++)
                    if (links[i] != null && links[i].LinkId == dockBpId)
                        link = links[i];
                InGameAssert.IsNotNull(link,
                    "the foreign Dock claim on B must derive as a Partner-journey link over the REAL committed trees");
                InGameAssert.AreEqual(taId, link.ForeignTreeId, "link must point into the controller's tree");
                InGameAssert.AreEqual(DockUT, link.DockUT, "link dock UT must be the claiming branch point's UT");
                InGameAssert.AreEqual(PidB, link.PartnerPid, "link partner pid must be B's (the claimed vessel)");
                InGameAssert.AreEqual(idB0, link.ClaimedRecordingId, "the claim must land on B's own pre-dock recording");
                InGameAssert.AreEqual(idAB, link.MergedChildRecordingId, "the merged child must be the combined docked stretch");
                InGameAssert.AreEqual(BranchPointType.Dock, link.ClaimType, "the claim type must be Dock");

                // The affordance is partner-side only: the controller's own dock BP never
                // derives as a foreign claim on the controller's tree.
                List<ForeignDockLink> reverse = MissionCrossTreeDock.FindLinks(
                    taCommittedTree, RecordingStore.CommittedTrees);
                for (int i = 0; i < reverse.Count; i++)
                    InGameAssert.IsFalse(reverse[i] != null && reverse[i].LinkId == dockBpId,
                        "the controller-side tree must not be offered its own dock as a partner journey");

                // The journey walk = the child rows under the Partner-journey row: the docked
                // stretch, then B's departing offshoot (never A's continuation).
                List<string> journey = MissionCrossTreeDock.ComputePartnerJourneyLegIds(taCommittedTree, link);
                InGameAssert.AreEqual(2, journey.Count, "journey = docked stretch + B's offshoot");
                InGameAssert.AreEqual(idAB, journey[0], "journey starts at the merged docked stretch");
                InGameAssert.AreEqual(idB1, journey[1], "journey follows B's departing offshoot, not A's continuation");

                // ---- (2) The include mutation + spanned-set one-loop enforcement. ----

                // Both missions loop concurrently while their trees are disjoint (no link yet).
                MissionStore.SetLoopEnabled(foreignMission, true, LoopEnableUT, RecordingStore.CommittedTrees);
                MissionStore.SetLoopEnabled(partnerMission, true, LoopEnableUT, RecordingStore.CommittedTrees);
                InGameAssert.IsTrue(foreignMission.LoopPlayback && partnerMission.LoopPlayback,
                    "disjoint trees loop concurrently before the link is included");

                // The exact mutation the Partner-journey checkbox commits on an ALREADY-LOOPING
                // mission (MissionsWindowUI.DrawForeignDockLinkRows): include the link, then
                // clear conflicting loops over the widened spanned tree set {tb, ta}.
                partnerMission.IncludedForeignDockLinkIds.Add(link.LinkId);
                MissionStore.ClearLoopsConflictingWith(partnerMission, RecordingStore.CommittedTrees,
                    out int clearedSameTree, out int clearedCrossTree, "PartnerJourneyInclude");
                InGameAssert.AreEqual(0, clearedSameTree, "no same-tree sibling loops exist");
                InGameAssert.AreEqual(1, clearedCrossTree,
                    "including the link on a looping mission must clear the loop on the linked foreign tree (spanned-set rule, toggle site)");
                InGameAssert.IsFalse(foreignMission.LoopPlayback,
                    "the foreign tree's looping mission must be cleared by the toggle-time enforcement");
                InGameAssert.IsTrue(partnerMission.LoopPlayback, "the including mission keeps its loop");

                // Normalize site (the OnLoad path for a hand-edited save): hand-set the
                // conflicting foreign loop back on and normalize over spanned sets.
                foreignMission.LoopPlayback = true;
                int normalized = MissionStore.NormalizeOneLoopPerTree(RecordingStore.CommittedTrees);
                InGameAssert.IsTrue(normalized >= 1,
                    "NormalizeOneLoopPerTree(trees) must clear the spanned-set conflict (cleared="
                    + normalized.ToString(IC) + ")");
                InGameAssert.IsFalse(foreignMission.LoopPlayback,
                    "the later-in-list foreign loop must be cleared by the normalize-site enforcement");
                InGameAssert.IsTrue(partnerMission.LoopPlayback,
                    "the first-in-list spanned loop survives normalize");

                // ---- (3) The REAL loop-unit build with the LIVE body info. ----

                captured.Clear();
                GhostPlaybackLogic.LoopUnitSet set = MissionLoopUnitBuilder.Build(
                    new List<Mission> { partnerMission },
                    RecordingStore.CommittedTrees,
                    RecordingStore.CommittedRecordings,
                    600.0,
                    FlightGlobalsBodyInfo.Instance);
                InGameAssert.AreEqual(1, set.Count, "exactly one unit for the one looping partner mission");
                GhostPlaybackLogic.LoopUnit unit = default;
                int unitCount = 0;
                foreach (KeyValuePair<int, GhostPlaybackLogic.LoopUnit> kv in set.UnitsByOwner)
                {
                    unit = kv.Value;
                    unitCount++;
                }
                InGameAssert.AreEqual(1, unitCount, "UnitsByOwner carries the single unit");

                // Member set spans BOTH trees: B pre-dock + docked stretch + B offshoot,
                // in trimmed-start order; A's own legs never join.
                InGameAssert.AreEqual(idxB0, unit.OwnerIndex, "owner = earliest member = B's pre-dock leg");
                InGameAssert.AreEqual(3, unit.MemberIndices.Length,
                    "members = B pre-dock + docked stretch + B offshoot (got " + unit.MemberIndices.Length.ToString(IC) + ")");
                InGameAssert.AreEqual(idxB0, unit.MemberIndices[0], "member order: B pre-dock first");
                InGameAssert.AreEqual(idxAB, unit.MemberIndices[1], "member order: docked stretch second");
                InGameAssert.AreEqual(idxB1, unit.MemberIndices[2], "member order: B offshoot third");
                for (int i = 0; i < unit.MemberIndices.Length; i++)
                    InGameAssert.IsTrue(unit.MemberIndices[i] != idxA0 && unit.MemberIndices[i] != idxA1,
                        "A's own pre-dock / post-undock legs must never join the partner unit");

                // Window boundaries: the exact recorded dock/undock UT math. B0 renders its
                // whole recorded solo flight; the docked stretch window is [DockUT, UndockUT]
                // (the journey run on A's line starts at the dock and ends where B departs);
                // the offshoot window is [UndockUT, OffshootEnd].
                InGameAssert.AreEqual(T0, unit.MemberStartUT(idxB0, double.NaN), "B0 window start = recorded start");
                InGameAssert.AreEqual(PreDockEndUT, unit.MemberEndUT(idxB0, double.NaN), "B0 window end = recorded solo end");
                InGameAssert.AreEqual(DockUT, unit.MemberStartUT(idxAB, double.NaN), "docked-stretch window starts at the dock UT");
                InGameAssert.AreEqual(UndockUT, unit.MemberEndUT(idxAB, double.NaN), "docked-stretch window ends at the undock UT");
                InGameAssert.AreEqual(UndockUT, unit.MemberStartUT(idxB1, double.NaN), "offshoot window starts at the undock UT");
                InGameAssert.AreEqual(OffshootEndUT, unit.MemberEndUT(idxB1, double.NaN), "offshoot window ends at B's recorded end");

                // ONE shared span clock over all three windows: span = [min start, max end]
                // across both trees, cadence = the span (Sec sentinel 10s < span, raised).
                InGameAssert.AreEqual(T0, unit.SpanStartUT, "shared span starts at B's pre-dock start");
                InGameAssert.AreEqual(OffshootEndUT, unit.SpanEndUT, "shared span ends at B's offshoot end");
                InGameAssert.AreEqual(OffshootEndUT - T0, unit.CadenceSeconds,
                    "faithful span-clock cadence = the shared span (one unit, one cadence)");

                // FAIL CLOSED to faithful, asserted on the specific unit fields: the faithful
                // base anchor survives untouched (no phase-lock snap), and every periodicity /
                // re-aim / phasing product is absent.
                InGameAssert.AreEqual(LoopEnableUT, unit.PhaseAnchorUT,
                    "faithful base anchor = the loop-enable UT (no phase-lock snap with a foreign member aboard)");
                InGameAssert.IsNull(unit.RelaunchSchedule, "no zero-drift relaunch schedule (fail closed)");
                InGameAssert.IsFalse(unit.ReaimPlan.HasValue, "no re-aim plan (fail closed)");
                InGameAssert.IsFalse(unit.ReaimSchedule.HasValue, "no re-aim schedule (fail closed)");
                InGameAssert.IsFalse(unit.IsReaim, "unit must not be re-aim (fail closed)");
                InGameAssert.IsNull(unit.LoiterCuts, "no loiter compression (fail closed)");
                InGameAssert.AreEqual(0.0, unit.ArrivalHoldSeconds, "no arrival hold (fail closed)");
                InGameAssert.IsFalse(unit.LaunchHoldEngaged, "no launch hold (fail closed)");
                InGameAssert.AreEqual(-1, unit.TransferMemberIndex, "no transfer member (fail closed)");

                // The logged fail-closed reason (the LIVE bodyInfo was present, so the branch
                // that skips the whole periodicity block must have announced itself).
                bool sawFailClosedLine = false;
                for (int i = 0; i < captured.Count; i++)
                {
                    if (captured[i].Contains("[Mission]")
                        && captured[i].Contains("cross-tree partner-journey member(s)")
                        && captured[i].Contains("fail closed"))
                    {
                        sawFailClosedLine = true;
                        break;
                    }
                }
                InGameAssert.IsTrue(sawFailClosedLine,
                    "the fail-closed periodicity reason line must fire when a foreign member joins under a live bodyInfo");

                // ---- (3b) Cross-seam checkbox trim: excluding the docked-stretch interval
                //           key drops that member; the offshoot survives. ----

                MissionStructure foreignStructure = MissionStructureBuilder.Build(taCommittedTree);
                MissionThroughLineView foreignView = MissionThroughLineBuilder.Build(foreignStructure);
                List<MissionCompositionNode> foreignRoots = MissionCompositionBuilder.Build(foreignStructure);
                var journeySet = new HashSet<string>(journey, System.StringComparer.Ordinal);
                var journeyWindows = MissionCrossTreeDock.ComputeJourneyWindowsByOwner(
                    foreignStructure, foreignView, journeySet);
                var journeyKeys = new HashSet<string>(System.StringComparer.Ordinal);
                MissionCrossTreeDock.CollectJourneySelectableKeys(foreignRoots, journeyWindows, journeyKeys);
                string dockedKey = null;
                foreach (string k in journeyKeys)
                    if (k.StartsWith(idA0, System.StringComparison.Ordinal) && k.Contains("@dock"))
                        dockedKey = k;
                InGameAssert.IsNotNull(dockedKey,
                    "the docked stretch must key as an @dock sub-interval of A's line (keys=" +
                    string.Join(",", new List<string>(journeyKeys).ToArray()) + ")");
                InGameAssert.IsTrue(journeyKeys.Contains(idB1), "B's offshoot must be a selectable journey key");
                InGameAssert.IsFalse(journeyKeys.Contains(idA0), "A's own pre-dock line must never be offered");

                partnerMission.ExcludedIntervalKeys.Add(dockedKey);
                GhostPlaybackLogic.LoopUnitSet trimmedSet = MissionLoopUnitBuilder.Build(
                    new List<Mission> { partnerMission },
                    RecordingStore.CommittedTrees,
                    RecordingStore.CommittedRecordings,
                    600.0,
                    FlightGlobalsBodyInfo.Instance);
                partnerMission.ExcludedIntervalKeys.Remove(dockedKey);
                InGameAssert.AreEqual(1, trimmedSet.Count, "the trimmed mission still builds one unit");
                GhostPlaybackLogic.LoopUnit trimmedUnit = default;
                foreach (KeyValuePair<int, GhostPlaybackLogic.LoopUnit> kv in trimmedSet.UnitsByOwner)
                    trimmedUnit = kv.Value;
                InGameAssert.AreEqual(2, trimmedUnit.MemberIndices.Length,
                    "excluding the docked-stretch key drops that member (checkbox trim across the seam)");
                InGameAssert.AreEqual(idxB0, trimmedUnit.MemberIndices[0], "trimmed member order: B pre-dock");
                InGameAssert.AreEqual(idxB1, trimmedUnit.MemberIndices[1], "trimmed member order: B offshoot");

                // ---- (4) Codec round-trip: the sparse foreignDockLink key through the
                //          production MissionStore Save/Load. ----

                var node = new ConfigNode("PARSEK_MMIS8_ROUNDTRIP");
                MissionStore.Save(node);
                ConfigNode partnerNode = FindMissionNode(node, partnerMission.Id);
                ConfigNode foreignNode = FindMissionNode(node, foreignMission.Id);
                InGameAssert.IsNotNull(partnerNode, "the linked mission must serialize");
                InGameAssert.IsNotNull(foreignNode, "the link-free mission must serialize");
                string[] linkValues = partnerNode.GetValues("foreignDockLink");
                InGameAssert.AreEqual(1, linkValues.Length, "the linked mission writes exactly one foreignDockLink key");
                InGameAssert.AreEqual(link.LinkId, linkValues[0], "the persisted link id is the claiming branch point's GUID");
                InGameAssert.AreEqual(0, foreignNode.GetValues("foreignDockLink").Length,
                    "SPARSE: a link-free mission writes no foreignDockLink key (pre-feature byte-identity)");

                MissionStore.Load(node);
                Mission reloaded = MissionStore.FindOriginalMission(tbId);
                InGameAssert.IsNotNull(reloaded, "the linked mission must reload");
                InGameAssert.IsTrue(reloaded.IncludedForeignDockLinkIds.Contains(link.LinkId),
                    "IncludedForeignDockLinkIds must survive the production Save/Load round-trip");
                InGameAssert.IsTrue(reloaded.LoopPlayback, "the loop flag rides the same round-trip");

                ParsekLog.Info(Tag,
                    "CrossTreeDockLoopUnit: PASS link=" + link.LinkId
                    + " members=" + unit.MemberIndices.Length.ToString(IC)
                    + " span=[" + unit.SpanStartUT.ToString("R", IC) + ".." + unit.SpanEndUT.ToString("R", IC) + "]"
                    + " cadence=" + unit.CadenceSeconds.ToString("R", IC)
                    + " phaseAnchor=" + unit.PhaseAnchorUT.ToString("R", IC)
                    + " failClosedLine=" + sawFailClosedLine
                    + " clearedCrossTree=" + clearedCrossTree.ToString(IC)
                    + " normalized=" + normalized.ToString(IC)
                    + " trimmedMembers=" + trimmedUnit.MemberIndices.Length.ToString(IC)
                    + " codecLinks=" + linkValues.Length.ToString(IC));
            }
            finally
            {
                // Restore the log seams first so the cleanup lines flow normally.
                ParsekLog.TestObserverForTesting = prevObserver;
                ParsekLog.SuppressLogging = prevSuppress;
                MissionLoopUnitBuilder.SuppressLogging = prevBuilderSuppress;
                MissionCrossTreeDock.SuppressLogging = prevCrossTreeSuppress;

                // Remove both synthetic committed trees (drops their recordings from the
                // flat committed list too), then restore the mission store wholesale from
                // the pre-test snapshot (drops the synthetic missions, restores any live
                // mission loop flags this test's enforcement calls may have touched).
                int removedTrees = 0;
                if (tbCommitted && RecordingStore.RemoveCommittedTreeById(tbId, "CrossTreeDockLoopUnit-cleanup"))
                    removedTrees++;
                if (taCommitted && RecordingStore.RemoveCommittedTreeById(taId, "CrossTreeDockLoopUnit-cleanup"))
                    removedTrees++;
                MissionStore.Load(missionSnapshot);
                ParsekLog.Verbose(Tag,
                    "CrossTreeDockLoopUnit cleanup: removedTrees=" + removedTrees.ToString(IC)
                    + " missionsRestored=" + MissionStore.Missions.Count.ToString(IC));
            }
        }

        // -----------------------------------------------------------------
        // Fixture builders (mirror the xUnit CrossTreeDockFixture shapes, but with
        // Guid-prefixed ids and full store registration)
        // -----------------------------------------------------------------

        // Partner tree tb: B's solo pre-dock flight [T0 .. PreDockEnd].
        private static RecordingTree BuildPartnerTree(string tbId, string idB0, string guidB, string prefix)
        {
            var tree = new RecordingTree { Id = tbId, RootRecordingId = idB0, TreeName = prefix + " Partner B" };
            Recording b0 = MakeLeg(idB0, tbId, PidB, guidB, prefix + "-CB", 0, T0, PreDockEndUT,
                prefix + " Vessel B", pods: 0, probes: 1);
            tree.Recordings[b0.RecordingId] = b0;
            return tree;
        }

        // Controller tree ta: A [T0 .. Dock] -> Dock BP (target = B's pid) -> AB
        // [Dock .. Undock] -> Undock fork -> { A1 continuing [Undock .. ForeignContEnd],
        // B1 departing [Undock .. OffshootEnd] }.
        private static RecordingTree BuildControllerTree(
            string taId, string idA0, string idAB, string idA1, string idB1,
            string dockBpId, string undockBpId, string guidA, string guidB, string prefix)
        {
            var tree = new RecordingTree { Id = taId, RootRecordingId = idA0, TreeName = prefix + " Controller A" };

            Recording a0 = MakeLeg(idA0, taId, PidA, guidA, prefix + "-CA", 0, T0, DockUT,
                prefix + " Vessel A", pods: 1, probes: 0);
            a0.ChildBranchPointId = dockBpId;
            Recording ab = MakeLeg(idAB, taId, PidA, guidA, prefix + "-CAB", 0, DockUT, UndockUT,
                prefix + " Stack AB", pods: 1, probes: 1);
            ab.ParentBranchPointId = dockBpId;
            ab.ChildBranchPointId = undockBpId;
            // Continuing stack first (recorder convention), departing partner second.
            Recording a1 = MakeLeg(idA1, taId, PidA, guidA, prefix + "-CA2", 0, UndockUT, ForeignContEndUT,
                prefix + " Vessel A", pods: 1, probes: 0);
            a1.ParentBranchPointId = undockBpId;
            Recording b1 = MakeLeg(idB1, taId, PidB, guidB, prefix + "-CB2", 0, UndockUT, OffshootEndUT,
                prefix + " Vessel B", pods: 0, probes: 1);
            b1.ParentBranchPointId = undockBpId;

            tree.Recordings[a0.RecordingId] = a0;
            tree.Recordings[ab.RecordingId] = ab;
            tree.Recordings[a1.RecordingId] = a1;
            tree.Recordings[b1.RecordingId] = b1;

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
            return tree;
        }

        private static Recording MakeLeg(
            string id, string treeId, uint pid, string guid, string chainId, int chainIndex,
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
                ChainIndex = chainIndex,
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

        private static RecordingTree FindCommittedTree(string treeId)
        {
            List<RecordingTree> trees = RecordingStore.CommittedTrees;
            for (int i = 0; i < trees.Count; i++)
                if (trees[i] != null && trees[i].Id == treeId)
                    return trees[i];
            return null;
        }

        private static int FindCommittedIndex(string recordingId)
        {
            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
                if (committed[i] != null && committed[i].RecordingId == recordingId)
                    return i;
            return -1;
        }

        private static ConfigNode FindMissionNode(ConfigNode root, string missionId)
        {
            ConfigNode[] nodes = root.GetNodes("MISSION");
            for (int i = 0; i < nodes.Length; i++)
                if (nodes[i] != null && nodes[i].GetValue("id") == missionId)
                    return nodes[i];
            return null;
        }
    }
}
