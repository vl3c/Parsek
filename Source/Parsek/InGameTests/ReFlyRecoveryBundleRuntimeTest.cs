using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// First live end-to-end proof that a Re-Fly merge retires a whole RECOVERY
    /// BUNDLE - the composite path PR #1512 created and that nothing had ever
    /// composed in a real KSP process.
    ///
    /// <para>
    /// A stock vessel recovery does not emit ONE ledger row. It emits a bundle that
    /// shares a <see cref="GameAction.RecordingId"/>:
    /// <see cref="GameActionType.FundsEarning"/> with
    /// <see cref="FundsEarningSource.Recovery"/>,
    /// <see cref="GameActionType.ScienceEarning"/> with
    /// <see cref="ScienceMethod.Recovered"/>,
    /// <see cref="GameActionType.KerbalAssignment"/>, and
    /// <see cref="GameActionType.KerbalExperience"/> carrying the crew's archived
    /// career log. The exact shape is READ OFF a real post-recovery career, the
    /// committed xUnit fixture <c>Source/Parsek.Tests/Fixtures/C2CareerPostFix/</c>
    /// (its <c>Parsek/GameState/ledger.pgld</c>, recording
    /// <c>5436a7e8840b4c5885afcbaedc9dc037</c>): seq 1 KerbalAssignment
    /// <c>endState = 0</c> (Aboard), seq 2/3 ScienceEarning <c>method = 1</c>
    /// (Recovered), seq 4 KerbalExperience
    /// <c>careerEntries = 0,Land,Kerbin|0,Flight,Kerbin|0,Recover,</c>, seq 5
    /// FundsEarning <c>fundsAwarded = 4558 fundsSource = 2</c>. Every field this
    /// fixture stamps below is one of those, so a shape divergence cannot read as a
    /// product defect (the doctrine the R7c spanned-recording cell's RP fixture and
    /// the RP-quicksave-sidecar rule in .claude/CLAUDE.md both exist for).
    /// </para>
    ///
    /// <para>
    /// Three surfaces have to agree for the bundle to retire, and each was unit-
    /// covered in isolation and never composed:
    /// <see cref="TombstoneEligibility.IsSupersedeTombstoneEligible"/> (returns true
    /// for all four types, KerbalExperience explicitly),
    /// <see cref="EffectiveState.ComputeELS"/> (ledger minus tombstones), and
    /// <see cref="KerbalsModule"/>'s APPEND-ONLY monotone career-log re-assert, which
    /// DEFERS while a Re-Fly marker is armed and lands on
    /// <c>MergeJournalOrchestrator</c>'s post-marker-clear recalc. That interplay is
    /// the suspected-bug surface this cell measures.
    /// </para>
    ///
    /// <para>
    /// Shape: the origin SPANS the rewind UT, so the merge takes the splitter's HEAD
    /// / TIP branch and the bundle lands post-rewind on TIP. That is the production
    /// shape for a recovery (you rewind to a separation mid-flight; the recovery at
    /// the end is post-rewind) and it is ALSO required by this spec's log contract,
    /// which FORBIDS <c>SplitOriginAtRewindUT: skip</c> - a non-spanning origin here
    /// would red the whole R7c flight on a token that has nothing to do with this
    /// cell.
    /// </para>
    ///
    /// <para>
    /// The CONTROL is deliberately the tightest one available: a second
    /// <see cref="GameActionType.ScienceEarning"/> on the SAME recording with the
    /// SAME shape, differing ONLY in its UT (pre-rewind). Nothing but the split's UT
    /// partition and the subtree scope can separate the two, so a tombstone scope that
    /// over-reached by one recording - or a retag that ignored the UT - reds here
    /// instead of passing quietly.
    /// </para>
    ///
    /// <para>
    /// Every scenario / store / ledger mutation is reversed in the finally block. The
    /// bundle's funds and science are never applied to the live career: no recalc runs
    /// between install and the merge, and the first one that does
    /// (<c>CommitTombstones</c>'s) already sees the bundle retired.
    /// </para>
    /// </summary>
    public class ReFlyRecoveryBundleRuntimeTest
    {
        private const string Tag = "RecoveryBundleTest";

        /// <summary>Grep marker for the deployed-DLL check and for log triage.</summary>
        private const string Marker = "RecoveryBundleReFly";

        // Production-shaped values lifted from the C2CareerPostFix ledger.
        private const double OriginStartUT = 330.0;
        private const double RewindUT = 340.0;
        private const double CrewRowUT = 342.06;
        private const double OriginEndUT = 348.08;
        private const double ControlSciUT = 335.0;

        private const float RecoveryFunds = 4558f;
        private const float RecoverySubjectAward = 5f;
        private const float RecoverySubjectMax = 6f;
        private const float ControlSubjectAward = 3f;
        private const float ControlSubjectMax = 3f;

        private const string BundleKerbal = "Jebediah Kerman";
        private const string BundleCareerEntries = "0,Land,Kerbin|0,Flight,Kerbin|0,Recover,";

        [InGameTest(Category = "Rewind", Scene = GameScenes.SPACECENTER,
            Description = "Re-Fly merge retires a whole recovery bundle (funds/science/crew/XP) from the ELS and stops the career-log re-assert")]
        public void ReFlyMergeRetiresRecoveryBundleAndStopsCareerLogReassert()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            // Preserve live session / scenario / ledger state.
            var priorMarker = scenario.ActiveReFlySessionMarker;
            var priorJournal = scenario.ActiveMergeJournal;
            var priorSupersedes = scenario.RecordingSupersedes;
            var priorTombstones = scenario.LedgerTombstones;
            var priorRps = scenario.RewindPoints;
            int priorLedgerCount = Ledger.Actions.Count;
            var priorDurableSaveHook = MergeJournalOrchestrator.DurableSaveForTesting;
            var priorSaveGameHook = MergeJournalOrchestrator.SaveGameForTesting;
            var priorReFlyOverride = KerbalsModule.ReFlySessionActiveOverrideForTesting;
            // ApplyToRoster rebuilds the crew-replacement bridge from the module's own
            // (here: empty) reservation set, so every measurement call below would
            // leave it cleared. It is derived state the next recalc rebuilds, but this
            // cell restores it explicitly rather than relying on that.
            var priorReplacements = new Dictionary<string, string>();
            foreach (var kvp in CrewReservationManager.CrewReplacements)
                priorReplacements[kvp.Key] = kvp.Value;

            string suffix = System.Guid.NewGuid().ToString("N").Substring(0, 8);
            string sessId = "recovery_bundle_" + suffix;
            string treeId = "recovery_bundle_tree_" + suffix;
            string originId = "recovery_bundle_origin_" + suffix;
            string forkId = "recovery_bundle_fork_" + suffix;
            string bpId = "recovery_bundle_bp_" + suffix;
            string rpId = "rp_recovery_bundle_" + suffix;
            string launchGuid = System.Guid.NewGuid().ToString();
            const uint launchPid = 987654321u;

            string fundsActionId = "act_rb_funds_" + suffix;
            string scienceActionId = "act_rb_sci_" + suffix;
            string crewActionId = "act_rb_crew_" + suffix;
            string xpActionId = "act_rb_xp_" + suffix;
            string controlActionId = "act_rb_control_" + suffix;

            // -- Origin: a recovered flight spanning the rewind UT ------------
            // Sample AT the rewind UT so RecordingOptimizer.SplitAtUT's
            // Unity-only Slerp branch is bypassed (same reason as the R7c
            // spanned-recording cell).
            var origin = new Recording
            {
                RecordingId = originId,
                VesselName = "Recovery Bundle Probe",
                TreeId = treeId,
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Recovered,
                LaunchSiteName = "LaunchPad",
                StartBodyName = "Kerbin",
                // Launch identity, both halves. ResurrectionRetirementEligibility
                // requires a POSITIVE guid match on both sides and matches nothing
                // when either is unknown, so a pid-only fixture would make the
                // resurrection-path measurement below vacuously empty.
                VesselPersistentId = launchPid,
                RecordedVesselGuid = launchGuid,
                // No ParentBranchPointId: this origin is the launch root (same
                // load-bearing absence the R7c cell documents).
            };
            origin.Points.Add(new TrajectoryPoint { ut = OriginStartUT });
            origin.Points.Add(new TrajectoryPoint { ut = RewindUT });
            origin.Points.Add(new TrajectoryPoint { ut = OriginEndUT });
            origin.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = OriginStartUT,
                endUT = OriginEndUT,
                sampleRateHz = 1f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = OriginStartUT },
                    new TrajectoryPoint { ut = RewindUT },
                    new TrajectoryPoint { ut = OriginEndUT },
                },
            });
            // Recovered crew need no stand-in, so this end state keeps the live
            // roster out of the recalc the merge triggers.
            origin.CrewEndStates = new Dictionary<string, KerbalEndState>
            {
                [BundleKerbal] = KerbalEndState.Recovered,
            };
            origin.CrewEndStatesResolved = true;

            var fork = new Recording
            {
                RecordingId = forkId,
                VesselName = "Recovery Bundle Probe re-fly",
                TreeId = treeId,
                MergeState = MergeState.NotCommitted,
                TerminalStateValue = TerminalState.Landed,
                SupersedeTargetId = originId,
                CreatingSessionId = sessId,
                ParentBranchPointId = bpId,
                ProvisionalForRpId = rpId,
            };
            fork.Points.Add(new TrajectoryPoint { ut = RewindUT + 0.5 });
            fork.Points.Add(new TrajectoryPoint { ut = OriginEndUT + 6.0 });

            var branchPoint = new BranchPoint
            {
                Id = bpId,
                Type = BranchPointType.Undock,
                UT = RewindUT,
                RewindPointId = rpId,
                ParentRecordingIds = new List<string> { "recovery_bundle_upstream_" + treeId },
                ChildRecordingIds = new List<string> { originId, forkId },
            };

            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "RecoveryBundle_" + treeId,
                RootRecordingId = originId,
                ActiveRecordingId = originId,
                BranchPoints = new List<BranchPoint> { branchPoint },
            };
            tree.AddOrReplaceRecording(origin);
            tree.AddOrReplaceRecording(fork);

            RecordingStore.AddCommittedInternal(origin);
            RecordingStore.AddCommittedInternal(fork);
            var trees = RecordingStore.CommittedTrees;
            for (int i = trees.Count - 1; i >= 0; i--)
                if (trees[i] != null && trees[i].Id == treeId) trees.RemoveAt(i);
            trees.Add(tree);

            // -- The recovery bundle, one RecordingId, four row types ---------
            var controlScience = new GameAction
            {
                ActionId = controlActionId,
                Type = GameActionType.ScienceEarning,
                RecordingId = originId,
                UT = ControlSciUT,
                SubjectId = "crewReport@KerbinSrfLandedLaunchPad",
                ScienceAwarded = ControlSubjectAward,
                SubjectMaxValue = ControlSubjectMax,
                // Transmitted, i.e. science the player banked mid-flight and still
                // owns. ResurrectionRetirementEligibility keys the science leg on
                // Method precisely so this row is NOT clawed back.
                Method = ScienceMethod.Transmitted,
                StartUT = (float)ControlSciUT,
                EndUT = (float)ControlSciUT,
            };
            var recoveryFunds = new GameAction
            {
                ActionId = fundsActionId,
                Type = GameActionType.FundsEarning,
                RecordingId = originId,
                UT = OriginEndUT,
                FundsAwarded = RecoveryFunds,
                FundsSource = FundsEarningSource.Recovery,
            };
            var recoveryScience = new GameAction
            {
                ActionId = scienceActionId,
                Type = GameActionType.ScienceEarning,
                RecordingId = originId,
                UT = OriginEndUT,
                SubjectId = "recovery@KerbinFlew",
                ScienceAwarded = RecoverySubjectAward,
                SubjectMaxValue = RecoverySubjectMax,
                Method = ScienceMethod.Recovered,
                StartUT = (float)OriginEndUT,
                EndUT = (float)OriginEndUT,
            };
            var recoveryCrew = new GameAction
            {
                ActionId = crewActionId,
                Type = GameActionType.KerbalAssignment,
                RecordingId = originId,
                UT = CrewRowUT,
                KerbalName = BundleKerbal,
                KerbalRole = "Pilot",
                // Aboard, NOT Recovered - the shape the real post-recovery ledger
                // carries on the recovery segment (C2CareerPostFix seq 1,
                // endState = 0). See the resurrection-path measurement below.
                KerbalEndStateField = KerbalEndState.Aboard,
                StartUT = (float)CrewRowUT,
                EndUT = (float)OriginEndUT,
            };
            var recoveryXp = new GameAction
            {
                ActionId = xpActionId,
                Type = GameActionType.KerbalExperience,
                RecordingId = originId,
                UT = OriginEndUT,
                KerbalName = BundleKerbal,
                KerbalRole = "Pilot",
                KerbalCareerEntries = BundleCareerEntries,
            };

            Ledger.AddAction(controlScience);
            Ledger.AddAction(recoveryCrew);
            Ledger.AddAction(recoveryScience);
            Ledger.AddAction(recoveryXp);
            Ledger.AddAction(recoveryFunds);

            var bundleIds = new List<string>
            {
                fundsActionId, scienceActionId, crewActionId, xpActionId
            };

            var marker = new ReFlySessionMarker
            {
                SessionId = sessId,
                TreeId = treeId,
                ActiveReFlyRecordingId = forkId,
                OriginChildRecordingId = originId,
                SupersedeTargetId = originId,
                RewindPointId = rpId,
                RewindPointUT = RewindUT,
                InvokedUT = RewindUT,
                InvokedRealTime = System.DateTime.UtcNow.ToString("o"),
            };

            var rewindPoint = new RewindPoint
            {
                RewindPointId = rpId,
                BranchPointId = bpId,
                UT = RewindUT,
                QuicksaveFilename = RecordingPaths.BuildRewindPointRelativePath(rpId),
                SessionProvisional = true,
                CreatingSessionId = sessId,
                CreatedRealTime = System.DateTime.UtcNow.ToString("o"),
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = 0,
                        OriginChildRecordingId = originId,
                        Controllable = true,
                    },
                },
            };

            scenario.ActiveReFlySessionMarker = marker;
            scenario.ActiveMergeJournal = null;
            scenario.RecordingSupersedes = new List<RecordingSupersedeRelation>();
            scenario.LedgerTombstones = new List<LedgerTombstone>();
            scenario.RewindPoints = new List<RewindPoint> { rewindPoint };
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();

            var durableCheckpoints = new List<string>();
            MergeJournalOrchestrator.DurableSaveForTesting =
                label => durableCheckpoints.Add(label);
            MergeJournalOrchestrator.SaveGameForTesting =
                (saveName, saveFolder, mode) => "ok";

            try
            {
                // == 1. PRE-MERGE BASELINE =====================================
                var elsBefore = EffectiveState.ComputeELS();
                for (int i = 0; i < bundleIds.Count; i++)
                {
                    InGameAssert.IsTrue(ContainsAction(elsBefore, bundleIds[i]),
                        $"Pre-merge ELS must contain bundle row {bundleIds[i]} - " +
                        "with no tombstones written yet the ELS is the raw ledger, so an " +
                        "absent row means the fixture never installed and every " +
                        "post-merge exclusion below would pass vacuously");
                }
                InGameAssert.IsTrue(ContainsAction(elsBefore, controlActionId),
                    "Pre-merge ELS must contain the control science row");

                double fundsBefore = WalkFunds(elsBefore, originId);
                double scienceBefore = WalkScience(elsBefore, originId);
                InGameAssert.ApproxEqual(RecoveryFunds, fundsBefore, 0.001,
                    "Pre-merge FundsModule walk over this recording's ELS rows must total " +
                    "the recovery award");
                InGameAssert.ApproxEqual(
                    ControlSubjectAward + RecoverySubjectAward, scienceBefore, 0.001,
                    "Pre-merge ScienceModule walk must total control + recovery science");

                // The career-log re-assert BEFORE the merge, both gate states. This is
                // the negative control for the post-merge measurement: without it, a
                // post-merge "appended nothing" is unfalsifiable (an inert module
                // appends nothing too).
                var reassertArmed = MeasureCareerReassert(elsBefore, originId, reFlyActive: true);
                InGameAssert.AreEqual(0, reassertArmed.Appended.Count,
                    "Career-log re-assert must DEFER while a Re-Fly session is armed: the " +
                    "append is irreversible (the roster facade has no remove) and the " +
                    "superseded branch's KerbalExperience rows are still ELS-effective");

                var reassertPreMerge = MeasureCareerReassert(elsBefore, originId, reFlyActive: false);
                InGameAssert.AreEqual(3, reassertPreMerge.Appended.Count,
                    "Pre-merge, with the gate open, the re-assert must append the recovery's " +
                    "THREE archived career-log entries (Land/Flight/Recover) - that is what " +
                    "the merge has to stop, and if it never appends here the post-merge " +
                    "assertion proves nothing");

                // == 2. THE RESURRECTION-PATH CLASSIFIER, on the same bundle ===
                // Pure, so it runs in-process with a synthetic surviving identity.
                var survivors = new List<(uint pid, string guid)> { (launchPid, launchGuid) };
                var classified = ResurrectionRetirementEligibility.Classify(
                    survivors,
                    RecordingStore.CommittedRecordings,
                    Ledger.Actions,
                    RewindUT);
                InGameAssert.AreEqual(1, classified.Count,
                    $"Classify must find exactly one resurrected recovery; got {classified.Count}");
                var retiredByResurrection = classified[0].RetiredActionIds;
                InGameAssert.IsTrue(retiredByResurrection.Contains(fundsActionId),
                    "Resurrection must retire the FundsEarning(Recovery) anchor");
                InGameAssert.IsTrue(retiredByResurrection.Contains(scienceActionId),
                    "Resurrection must retire the Recovered-method science row");
                InGameAssert.IsTrue(retiredByResurrection.Contains(xpActionId),
                    "Resurrection must retire the KerbalExperience row - leaving it would let " +
                    "the monotone roster re-assert put the recovery flight's XP back");
                InGameAssert.IsFalse(retiredByResurrection.Contains(controlActionId),
                    "Resurrection must NOT retire the Transmitted-method science row - science " +
                    "the player banked mid-flight and still owns");
                // MEASURED, and pinned so a change announces itself: the classifier's
                // crew leg matches KerbalEndState.Recovered, and the production shape
                // this fixture copies (C2CareerPostFix seq 1) carries Aboard on the
                // recovery segment, so the crew row is NOT in the resurrection bundle.
                // It does not need to be: the crew are alive in both worlds, and the
                // MERGE path retires it anyway via IsSupersedeTombstoneEligible
                // (asserted below). Recorded here so the asymmetry is a measurement
                // rather than a surprise.
                InGameAssert.IsFalse(retiredByResurrection.Contains(crewActionId),
                    "MEASURED: the production-shaped KerbalAssignment row carries " +
                    "endState=Aboard on the recovery segment, and Classify's crew leg keys " +
                    "on endState=Recovered, so it is not in the resurrection bundle. The " +
                    "merge path retires it regardless.");

                ParsekLog.Info(Tag,
                    Marker + ": installed origin=" + originId + " fork=" + forkId +
                    " sess=" + sessId + " rp=" + rpId + " bp=" + bpId +
                    " rewindUT=" + RewindUT.ToString("F2", CultureInfo.InvariantCulture) +
                    " bundleRows=" + bundleIds.Count.ToString(CultureInfo.InvariantCulture) +
                    " resurrectionRetired=" +
                    retiredByResurrection.Count.ToString(CultureInfo.InvariantCulture) +
                    " - invoking RunMerge");

                // == 3. THE MERGE ==============================================
                bool ok = MergeJournalOrchestrator.RunMerge(marker, fork);
                InGameAssert.IsTrue(ok, "MergeJournalOrchestrator.RunMerge returned false");

                // The origin spans the rewind UT, so the splitter ran: HEAD keeps the
                // origin id, TIP is a new chain sibling carrying the post-rewind tail.
                Recording head = null;
                Recording tip = null;
                var committed = RecordingStore.CommittedRecordings;
                for (int i = 0; i < committed.Count; i++)
                {
                    var rec = committed[i];
                    if (rec == null) continue;
                    if (rec.RecordingId == originId) head = rec;
                    else if (rec.TreeId == treeId
                        && rec.RecordingId != forkId
                        && rec.ChainIndex == 1) tip = rec;
                }
                InGameAssert.IsNotNull(head, "HEAD (origin id preserved) not found post-merge");
                InGameAssert.IsNotNull(tip,
                    "TIP (new chain sibling at ChainIndex=1) not found post-merge - without the " +
                    "split the bundle would have been retired by whole-recording supersede, " +
                    "which is a different code path than the one this cell claims to measure");

                // == 4. EVERY BUNDLE ROW TOMBSTONED, CONTROL UNTOUCHED ========
                for (int i = 0; i < bundleIds.Count; i++)
                {
                    InGameAssert.IsTrue(HasTombstone(scenario, bundleIds[i]),
                        $"Bundle row {bundleIds[i]} must carry a LedgerTombstone after the " +
                        "merge - TombstoneEligibility.IsSupersedeTombstoneEligible returns " +
                        "true for FundsEarning, ScienceEarning, KerbalAssignment and " +
                        "KerbalExperience alike");
                }
                InGameAssert.IsFalse(HasTombstone(scenario, controlActionId),
                    "The pre-rewind control row must NOT be tombstoned: it stayed on HEAD, " +
                    "and HEAD is the pre-rewind chain-head carve-out, outside the supersede " +
                    "subtree");

                // Retag check: every bundle row moved from origin's id onto TIP, and
                // the control did not. Same recording pre-merge, so only the UT
                // partition can separate them.
                for (int i = 0; i < bundleIds.Count; i++)
                {
                    var row = FindAction(Ledger.Actions, bundleIds[i]);
                    InGameAssert.IsNotNull(row,
                        $"Bundle row {bundleIds[i]} vanished from the ledger - retirement is " +
                        "append-only tombstones, never removal");
                    InGameAssert.AreEqual(tip.RecordingId, row.RecordingId,
                        $"Bundle row {bundleIds[i]} should be retagged onto TIP " +
                        $"({tip.RecordingId}); got {row.RecordingId}");
                }
                var controlRow = FindAction(Ledger.Actions, controlActionId);
                InGameAssert.IsNotNull(controlRow, "Control row vanished from the ledger");
                InGameAssert.AreEqual(originId, controlRow.RecordingId,
                    "The pre-rewind control row must stay attributed to HEAD");

                // == 5. ELS AND ERS ============================================
                var elsAfter = EffectiveState.ComputeELS();
                for (int i = 0; i < bundleIds.Count; i++)
                {
                    InGameAssert.IsFalse(ContainsAction(elsAfter, bundleIds[i]),
                        $"ELS must NOT credit bundle row {bundleIds[i]} after the merge");
                }
                InGameAssert.IsTrue(ContainsAction(elsAfter, controlActionId),
                    "ELS must still credit the pre-rewind control row");

                var ers = EffectiveState.ComputeERS();
                bool ersHasHead = false, ersHasTip = false, ersHasFork = false;
                for (int i = 0; i < ers.Count; i++)
                {
                    var rec = ers[i];
                    if (rec == null) continue;
                    if (rec.RecordingId == head.RecordingId) ersHasHead = true;
                    else if (rec.RecordingId == tip.RecordingId) ersHasTip = true;
                    else if (rec.RecordingId == forkId) ersHasFork = true;
                }
                InGameAssert.IsTrue(ersHasHead, "ERS must include HEAD");
                InGameAssert.IsFalse(ersHasTip, "ERS must NOT include TIP (superseded by the fork)");
                InGameAssert.IsTrue(ersHasFork, "ERS must include the fork");

                // == 6. THE POOL RECALC REFLECTS THE RETIREMENT ===============
                // Measured through the SHIPPING modules over the post-merge ELS,
                // scoped to this fixture's recordings, so the numbers are exact and
                // independent of whatever the live career happens to hold.
                double fundsAfter = WalkFunds(elsAfter, originId, tip.RecordingId);
                double scienceAfter = WalkScience(elsAfter, originId, tip.RecordingId);
                InGameAssert.ApproxEqual(0.0, fundsAfter, 0.001,
                    $"FundsModule over the post-merge ELS must total 0 for this tree " +
                    $"(was {fundsBefore.ToString("F2", CultureInfo.InvariantCulture)}): the " +
                    "recovery payout is retired");
                InGameAssert.ApproxEqual(ControlSubjectAward, scienceAfter, 0.001,
                    $"ScienceModule over the post-merge ELS must total only the control award " +
                    $"(was {scienceBefore.ToString("F2", CultureInfo.InvariantCulture)}): the " +
                    "recovery science is retired, the transmitted science is not");

                // == 7. THE ROSTER / CAREER-LOG INTERPLAY =====================
                // (a) Against a roster that does NOT carry the entries, the re-assert
                //     appends nothing now: the XP row left the ELS with the tombstone.
                var reassertPostMerge = MeasureCareerReassert(
                    elsAfter, originId, tip.RecordingId, reFlyActive: false);
                InGameAssert.AreEqual(0, reassertPostMerge.Appended.Count,
                    "Post-merge the career-log re-assert must append NOTHING: the retired " +
                    "KerbalExperience row is out of the ELS, so the accumulator it feeds is " +
                    "empty. It appended 3 on the identical call pre-merge.");
                InGameAssert.IsNull(reassertPostMerge.Module.GetCareerEntriesForTesting(BundleKerbal),
                    "Post-merge the KerbalExperience accumulator must have no entry for the " +
                    "bundle's kerbal at all");

                // (b) Against a roster that ALREADY carries the recovery's entries -
                //     the state a live save is in when the merge runs - the append-only
                //     facade neither re-appends them nor removes them. That is the
                //     MEASURED behaviour of the monotone re-assert, and it is correct
                //     by design: the rewind's quicksave load is what removes the
                //     superseded flight's XP, and the re-assert exists only to put back
                //     what a SURVIVING flight earned. Pinned so a change to the
                //     append-only contract announces itself here.
                var preloaded = new RecordingRosterFacade();
                preloaded.Existing.Add(new KerbalCareerLogEntry(0, "Land", "Kerbin"));
                preloaded.Existing.Add(new KerbalCareerLogEntry(0, "Flight", "Kerbin"));
                preloaded.Existing.Add(new KerbalCareerLogEntry(0, "Recover", ""));
                var preloadedResult = MeasureCareerReassert(
                    elsAfter, preloaded, originId, tip.RecordingId, reFlyActive: false);
                InGameAssert.AreEqual(0, preloadedResult.Appended.Count,
                    "MEASURED: with the recovery's entries already on the roster the " +
                    "post-merge re-assert appends nothing (no double-append)");
                InGameAssert.AreEqual(3, preloaded.Existing.Count,
                    "MEASURED: the re-assert never removes - the facade has no remove verb, " +
                    "and the roster keeps the three entries. Removal of a superseded " +
                    "flight's XP is the rewind quicksave load's job, not this facet's.");

                // == 8. DURABLE BARRIERS ======================================
                int beginIdx = durableCheckpoints.IndexOf("begin");
                int splitIdx = durableCheckpoints.IndexOf("split");
                int durable1Idx = durableCheckpoints.IndexOf("durable1");
                InGameAssert.IsTrue(
                    beginIdx >= 0 && splitIdx > beginIdx && durable1Idx > splitIdx,
                    "Expected ordered durable barriers begin < split < durable1; got [" +
                    string.Join(",", durableCheckpoints.ToArray()) + "]");

                ParsekLog.Info(Tag,
                    Marker + ": all invariants passed head=" + head.RecordingId +
                    " tip=" + tip.RecordingId + " fork=" + forkId +
                    " tombstones=" +
                    scenario.LedgerTombstones.Count.ToString(CultureInfo.InvariantCulture) +
                    " supersedeRows=" +
                    scenario.RecordingSupersedes.Count.ToString(CultureInfo.InvariantCulture) +
                    " fundsBefore=" + fundsBefore.ToString("F2", CultureInfo.InvariantCulture) +
                    " fundsAfter=" + fundsAfter.ToString("F2", CultureInfo.InvariantCulture) +
                    " sciBefore=" + scienceBefore.ToString("F2", CultureInfo.InvariantCulture) +
                    " sciAfter=" + scienceAfter.ToString("F2", CultureInfo.InvariantCulture) +
                    " reassertPreMerge=" +
                    reassertPreMerge.Appended.Count.ToString(CultureInfo.InvariantCulture) +
                    " reassertPostMerge=" +
                    reassertPostMerge.Appended.Count.ToString(CultureInfo.InvariantCulture));
            }
            finally
            {
                MergeJournalOrchestrator.DurableSaveForTesting = priorDurableSaveHook;
                MergeJournalOrchestrator.SaveGameForTesting = priorSaveGameHook;
                KerbalsModule.ReFlySessionActiveOverrideForTesting = priorReFlyOverride;

                CrewReservationManager.ClearReplacementsInternal();
                foreach (var kvp in priorReplacements)
                    CrewReservationManager.SetReplacement(kvp.Key, kvp.Value);

                var live = RecordingStore.CommittedRecordings;
                for (int i = live.Count - 1; i >= 0; i--)
                {
                    var rec = live[i];
                    if (rec != null && rec.TreeId == treeId)
                        RecordingStore.RemoveCommittedInternal(rec);
                }
                var liveTrees = RecordingStore.CommittedTrees;
                for (int i = liveTrees.Count - 1; i >= 0; i--)
                    if (liveTrees[i] != null && liveTrees[i].Id == treeId)
                        liveTrees.RemoveAt(i);

                FlightIntegrationTests.TruncateLedgerForTeardown(
                    priorLedgerCount, "ReFlyRecoveryBundle restore");

                scenario.ActiveReFlySessionMarker = priorMarker;
                scenario.ActiveMergeJournal = priorJournal;
                scenario.RecordingSupersedes = priorSupersedes;
                scenario.LedgerTombstones = priorTombstones;
                scenario.RewindPoints = priorRps;
                scenario.BumpSupersedeStateVersion();
                scenario.BumpTombstoneStateVersion();
                EffectiveState.ResetCachesForTesting();
            }
        }

        // --------------------------------------------------------------------
        // Helpers.
        // --------------------------------------------------------------------

        private static bool ContainsAction(IReadOnlyList<GameAction> actions, string actionId)
        {
            return FindAction(actions, actionId) != null;
        }

        private static GameAction FindAction(IReadOnlyList<GameAction> actions, string actionId)
        {
            if (actions == null) return null;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a != null && a.ActionId == actionId) return a;
            }
            return null;
        }

        private static bool HasTombstone(ParsekScenario scenario, string actionId)
        {
            var tombs = scenario.LedgerTombstones;
            if (tombs == null) return false;
            for (int i = 0; i < tombs.Count; i++)
            {
                var t = tombs[i];
                if (t != null && t.ActionId == actionId) return true;
            }
            return false;
        }

        /// <summary>
        /// The fixture's own rows out of a computed effective set. Scoped by recording
        /// id (origin pre-split, HEAD and TIP after) so the walk totals are this
        /// cell's numbers and not the live career's.
        /// </summary>
        private static List<GameAction> ScopedRows(
            IReadOnlyList<GameAction> actions, params string[] recordingIds)
        {
            var result = new List<GameAction>();
            if (actions == null) return result;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a == null || string.IsNullOrEmpty(a.RecordingId)) continue;
                for (int r = 0; r < recordingIds.Length; r++)
                {
                    if (a.RecordingId == recordingIds[r]) { result.Add(a); break; }
                }
            }
            return result;
        }

        private static double WalkFunds(
            IReadOnlyList<GameAction> actions, params string[] recordingIds)
        {
            var rows = ScopedRows(actions, recordingIds);
            var module = new FundsModule();
            module.Reset();
            module.PrePass(rows);
            for (int i = 0; i < rows.Count; i++)
                module.ProcessAction(rows[i]);
            module.PostWalk();
            return module.GetTotalEarnings();
        }

        private static double WalkScience(
            IReadOnlyList<GameAction> actions, params string[] recordingIds)
        {
            var rows = ScopedRows(actions, recordingIds);
            var module = new ScienceModule();
            module.Reset();
            module.PrePass(rows);
            for (int i = 0; i < rows.Count; i++)
                module.ProcessAction(rows[i]);
            module.PostWalk();
            return module.GetTotalEffectiveEarnings();
        }

        private struct ReassertMeasurement
        {
            public KerbalsModule Module;
            public List<KerbalCareerLogEntry> Appended;
        }

        private static ReassertMeasurement MeasureCareerReassert(
            IReadOnlyList<GameAction> actions, string recordingIdA, bool reFlyActive)
        {
            return MeasureCareerReassert(
                actions, new RecordingRosterFacade(), reFlyActive, recordingIdA);
        }

        private static ReassertMeasurement MeasureCareerReassert(
            IReadOnlyList<GameAction> actions,
            string recordingIdA,
            string recordingIdB,
            bool reFlyActive)
        {
            return MeasureCareerReassert(
                actions, new RecordingRosterFacade(), reFlyActive, recordingIdA, recordingIdB);
        }

        private static ReassertMeasurement MeasureCareerReassert(
            IReadOnlyList<GameAction> actions,
            RecordingRosterFacade roster,
            string recordingIdA,
            string recordingIdB,
            bool reFlyActive)
        {
            return MeasureCareerReassert(
                actions, roster, reFlyActive, recordingIdA, recordingIdB);
        }

        /// <summary>
        /// Drives a fresh <see cref="KerbalsModule"/> over the fixture's slice of an
        /// effective ledger set and records what its career-log re-assert appended,
        /// against an inert recording facade so nothing touches the live roster.
        /// </summary>
        private static ReassertMeasurement MeasureCareerReassert(
            IReadOnlyList<GameAction> actions,
            RecordingRosterFacade roster,
            bool reFlyActive,
            params string[] recordingIds)
        {
            var rows = ScopedRows(actions, recordingIds);
            var module = new KerbalsModule();
            module.Reset();
            module.PrePass(rows);
            for (int i = 0; i < rows.Count; i++)
                module.ProcessAction(rows[i]);
            module.PostWalk();

            var priorOverride = KerbalsModule.ReFlySessionActiveOverrideForTesting;
            try
            {
                KerbalsModule.ReFlySessionActiveOverrideForTesting = reFlyActive;
                module.ApplyToRoster(roster);
            }
            finally
            {
                KerbalsModule.ReFlySessionActiveOverrideForTesting = priorOverride;
            }

            return new ReassertMeasurement { Module = module, Appended = roster.Appended };
        }

        /// <summary>
        /// Inert roster facade that records what the re-assert appended and can be
        /// pre-loaded with entries the roster already carries. Everything outside the
        /// career-log surface declines, so the live roster is never touched.
        /// </summary>
        private sealed class RecordingRosterFacade : KerbalsModule.IKerbalRosterFacade
        {
            public readonly List<KerbalCareerLogEntry> Existing = new List<KerbalCareerLogEntry>();
            public readonly List<KerbalCareerLogEntry> Appended = new List<KerbalCareerLogEntry>();

            public bool TryGetStatus(string name, out ProtoCrewMember.RosterStatus status)
            {
                status = default(ProtoCrewMember.RosterStatus);
                return false;
            }

            public bool TryCreateGeneratedStandIn(string trait, out string generatedName)
            {
                generatedName = null;
                return false;
            }

            public bool TryRecreateStandIn(string desiredName, string trait) { return false; }
            public bool TryRemove(string name) { return false; }
            public bool IsKerbalOnLiveVessel(string kerbalName) { return false; }
            public bool IsKerbalOnVesselWithPid(string kerbalName, ulong vesselPersistentId) { return false; }

            public List<KerbalCareerLogEntry> GetCareerLogEntries(string kerbalName)
            {
                // Every kerbal is "on the roster" so an absent-kerbal skip can never
                // masquerade as a correct empty append.
                return new List<KerbalCareerLogEntry>(Existing);
            }

            public int AppendCareerLogEntries(
                string kerbalName, IReadOnlyList<KerbalCareerLogEntry> entries)
            {
                if (entries == null) return 0;
                for (int i = 0; i < entries.Count; i++)
                {
                    Appended.Add(entries[i]);
                    Existing.Add(entries[i]);
                }
                return entries.Count;
            }
        }
    }
}
