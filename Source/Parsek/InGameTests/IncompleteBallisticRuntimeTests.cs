using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Contracts;
using HarmonyLib;
using KSP.UI;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek.InGameTests
{
    public class IncompleteBallisticRuntimeTests
    {
        [InGameTest(Category = "IncompleteBallistic", Scene = GameScenes.FLIGHT,
            Description = "Plane-exit extrapolation keeps producing a ballistic tail until the ghost would despawn")]
        public void ExtrapolationIntegration_PlaneExitMidFlight_GhostFallsAndDespawns()
        {
            const double gravParameter = 3.5316e12;
            const double radius = 600000.0;
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = MakeBody(
                    "Kerbin",
                    gravParameter,
                    radius,
                    atmosphereDepth: 70000.0,
                    terrainAltitude: FlatTerrain,
                    surfaceCoordinates: ZeroSurfaceCoordinates)
            };

            var start = new BallisticStateVector
            {
                ut = 0.0,
                bodyName = "Kerbin",
                position = new Vector3d(radius + 3000.0, 0.0, 0.0),
                velocity = new Vector3d(0.0, 250.0, 0.0)
            };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(start, bodies);

            InGameAssert.AreEqual(TerminalState.Destroyed, result.terminalState,
                "low-altitude flying exit should terminate instead of orbit forever");
            InGameAssert.IsTrue(result.terminalUT > start.ut,
                "terminal UT should extend beyond the recorded exit");
            InGameAssert.IsTrue(result.segments != null && result.segments.Count > 0,
                "plane exit should produce at least one extrapolated segment");
        }

        [InGameTest(Category = "IncompleteBallistic", Scene = GameScenes.FLIGHT,
            Description = "Suborbital apoapsis extrapolation keeps the ghost on an orbital arc before termination")]
        public void ExtrapolationIntegration_SuborbitalExitAtApoapsis_GhostFollowsArc()
        {
            const double gravParameter = 3.5316e12;
            const double radius = 600000.0;
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Kerbin"] = MakeBody(
                    "Kerbin",
                    gravParameter,
                    radius,
                    atmosphereDepth: 70000.0,
                    terrainAltitude: FlatTerrain,
                    surfaceCoordinates: ZeroSurfaceCoordinates)
            };

            BallisticStateVector start = MakeApoapsisState(
                "Kerbin",
                gravParameter,
                apoapsisRadius: radius + 70500.0,
                periapsisRadius: radius + 15000.0);

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(start, bodies);

            InGameAssert.AreEqual(TerminalState.Destroyed, result.terminalState,
                "suborbital apoapsis should eventually terminate");
            InGameAssert.IsTrue(result.segments != null && result.segments.Count > 0,
                "suborbital apoapsis should produce at least one extrapolated coast segment");
            InGameAssert.AreEqual("Kerbin", result.segments[0].bodyName,
                "the first extrapolated segment should stay on Kerbin");
        }

        [InGameTest(Category = "IncompleteBallistic", Scene = GameScenes.FLIGHT,
            Description = "Hyperbolic extrapolation crosses the child SOI and continues on the parent body")]
        public void ExtrapolationIntegration_HyperbolicExit_GhostHandsOffToKerbol()
        {
            var bodies = new Dictionary<string, ExtrapolationBody>
            {
                ["Sun"] = MakeBody("Sun", 1.1723328e18, 261600000.0, sphereOfInfluence: 1.0e30),
                ["Kerbin"] = MakeBody(
                    "Kerbin",
                    3.5316e12,
                    600000.0,
                    atmosphereDepth: 0.0,
                    sphereOfInfluence: 10000000.0,
                    parentBodyName: "Sun",
                    parentFrameState: FixedState(Vector3d.zero, new Vector3d(0.0, 9500.0, 0.0)),
                    terrainAltitude: FlatTerrain,
                    surfaceCoordinates: ZeroSurfaceCoordinates)
            };

            var start = new BallisticStateVector
            {
                ut = 0.0,
                bodyName = "Kerbin",
                position = new Vector3d(9500000.0, 0.0, 0.0),
                velocity = new Vector3d(1400.0, 800.0, 0.0)
            };

            ExtrapolationResult result = BallisticExtrapolator.Extrapolate(start, bodies);

            InGameAssert.IsTrue(result.segments != null && result.segments.Count > 1,
                "hyperbolic exit should produce more than one segment");
            InGameAssert.IsTrue(result.segments.Any(seg => seg.bodyName == "Sun"),
                "hyperbolic exit should hand off onto the parent-body frame");
        }

        [InGameTest(Category = "IncompleteBallistic", Scene = GameScenes.FLIGHT,
            Description = "Patched-conic flyby capture yields the same active body the map-view selector would draw")]
        public void PatchedSnapshotIntegration_MunFlybyExit_GhostTrajectoryMatchesMapView()
        {
            var munPatch = MakePatch(200.0, 320.0, "Mun");
            var kerbinPatch = MakePatch(100.0, 200.0, "Kerbin", PatchedConicTransitionType.Encounter);
            kerbinPatch.NextPatch = munPatch;

            var source = new FakePatchedConicSnapshotSource(2)
            {
                RootPatch = kerbinPatch
            };

            PatchedConicSnapshotResult snapshot = PatchedConicSnapshot.SnapshotPatchedConicChain(
                source, 120.0, 8, "MunFlybyRuntime");

            InGameAssert.AreEqual(2, snapshot.Segments.Count,
                "flyby snapshot should capture the encounter and Mun leg");

            bool visible = TrajectoryMath.TryGetOrbitWindowForMapDisplay(
                snapshot.Segments,
                250.0,
                out OrbitSegment segment,
                out _,
                out _,
                out _,
                out _,
                out _);

            InGameAssert.IsTrue(visible, "Mun flyby segment should be selectable for map rendering");
            InGameAssert.AreEqual("Mun", segment.bodyName,
                "map selection should follow the captured Mun leg");
        }

        [InGameTest(Category = "IncompleteBallistic", Scene = GameScenes.FLIGHT,
            Description = "Patched-conic capture strips planned maneuver nodes from the ghost trajectory")]
        public void PatchedSnapshotIntegration_ManeuverNodeStripped_GhostIgnoresBurn()
        {
            var maneuverPatch = MakePatch(100.0, 200.0, "Kerbin", PatchedConicTransitionType.Maneuver);
            maneuverPatch.NextPatch = MakePatch(200.0, 260.0, "Mun");

            var source = new FakePatchedConicSnapshotSource(2)
            {
                RootPatch = maneuverPatch
            };

            PatchedConicSnapshotResult snapshot = PatchedConicSnapshot.SnapshotPatchedConicChain(
                source, 120.0, 8, "ManeuverRuntime");

            InGameAssert.AreEqual(1, snapshot.Segments.Count,
                "capture should keep only the pre-maneuver coast patch");
            InGameAssert.IsTrue(snapshot.EncounteredManeuverNode,
                "snapshot should flag that it encountered a UI maneuver node");
            InGameAssert.AreEqual("Kerbin", snapshot.Segments[0].bodyName,
                "post-maneuver bodies should not leak into the captured chain");
        }

        [InGameTest(Category = "IncompleteBallistic", Scene = GameScenes.FLIGHT,
            Description = "The v1 map selector chooses the predicted/extrapolated segment after the recorded payload ends")]
        public void MapRendering_V1_GhostDrawsLineFromExtrapolatedSegment()
        {
            var segments = new List<OrbitSegment>
            {
                MakeOrbitSegment(100.0, 200.0, "Kerbin", isPredicted: false),
                MakeOrbitSegment(200.0, 500.0, "Kerbin", isPredicted: true)
            };

            bool visible = TrajectoryMath.TryGetOrbitWindowForMapDisplay(
                segments,
                350.0,
                out OrbitSegment segment,
                out _,
                out double visibleEndUT,
                out _,
                out _,
                out _);

            InGameAssert.IsTrue(visible, "predicted tail should remain renderable after payload end");
            InGameAssert.IsTrue(segment.isPredicted,
                "map selection should choose the predicted tail segment after payload end");
            InGameAssert.ApproxEqual(500.0, visibleEndUT, 0.001);
        }

        [InGameTest(Category = "IncompleteBallistic", Scene = GameScenes.FLIGHT,
            Description = "Equivalent same-body tails keep the v1 map line continuous across a recorded-data gap")]
        public void MapRendering_V1_LineContinuesPastRecordedEnd()
        {
            var segments = new List<OrbitSegment>
            {
                MakeOrbitSegment(100.0, 200.0, "Kerbin", isPredicted: false, sma: 700000.0, ecc: 0.01),
                MakeOrbitSegment(300.0, 500.0, "Kerbin", isPredicted: true, sma: 700000.0, ecc: 0.01)
            };

            bool visible = TrajectoryMath.TryGetOrbitWindowForMapDisplay(
                segments,
                250.0,
                out OrbitSegment segment,
                out double visibleStartUT,
                out double visibleEndUT,
                out _,
                out _,
                out bool carriedAcrossGap);

            InGameAssert.IsTrue(visible, "equivalent predicted tail should bridge the recorded-data gap");
            InGameAssert.IsTrue(carriedAcrossGap,
                "equivalent predicted tail should be carried across the gap");
            InGameAssert.AreEqual("Kerbin", segment.bodyName,
                "gap-carry should keep the active body on the same SOI");
            InGameAssert.ApproxEqual(100.0, visibleStartUT, 0.001);
            InGameAssert.ApproxEqual(500.0, visibleEndUT, 0.001);
        }

        [InGameTest(Category = "IncompleteBallistic", Scene = GameScenes.FLIGHT,
            Description = "The v1 map selector does not bridge across foreign-SOI segments")]
        public void MapRendering_V1_ForeignSOISegmentsNotRendered()
        {
            var segments = new List<OrbitSegment>
            {
                MakeOrbitSegment(100.0, 200.0, "Kerbin", isPredicted: false, sma: 700000.0, ecc: 0.01),
                MakeOrbitSegment(300.0, 500.0, "Mun", isPredicted: true, sma: 250000.0, ecc: 0.01)
            };

            bool visible = TrajectoryMath.TryGetOrbitWindowForMapDisplay(
                segments,
                250.0,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _);

            InGameAssert.IsFalse(visible,
                "foreign-SOI segments should not render through a gap in the v1 selector");
        }

        /// <summary>
        /// Bug-repro acceptance test for the split-at-rewind-UT fix
        /// (plan §7d / docs/dev/plans/fix-supersede-identity-scope.md).
        ///
        /// <para>
        /// Installs a synthetic single-recording tree where origin covers
        /// UT [8.42, 52.7] with a Destroyed terminal and one on-board kerbal
        /// marked Dead, plus a provisional fork covering [34.5, ...]. Builds
        /// a re-fly marker with <c>RewindPointUT = 34.24</c>, then drives
        /// <see cref="MergeJournalOrchestrator.RunMerge"/> end-to-end. After
        /// the merge:
        /// </para>
        ///
        /// <list type="bullet">
        ///   <item><description>The origin recording is split into HEAD [8.42, 34.24] (visible) and TIP [34.24, 52.7] (superseded by the fork). HEAD keeps the original id; TIP gets a new id; they share a ChainId.</description></item>
        ///   <item><description>The kerbal's <c>KerbalEndState=Dead</c> ledger action is retagged to TIP, then tombstoned — ELS does not credit the kerbal as dead.</description></item>
        ///   <item><description>The pre-rewind <c>FirstLaunch</c> milestone at UT 9.4 stays attributed to HEAD (not retagged to TIP).</description></item>
        ///   <item><description><c>TimelineBuilder.Build</c> produces a <c>RecordingStart</c> entry for HEAD.</description></item>
        ///   <item><description>The slot's effective tip via the composite walker resolves to the fork, not HEAD.</description></item>
        ///   <item><description><c>IsPreRewindCarveOut(HEAD, marker)</c> returns <c>(true, PreRewindChainHead)</c>.</description></item>
        /// </list>
        ///
        /// <para>
        /// All scenario / store / ledger / milestone mutations are reversed
        /// in the finally block so the live save is restored to its pre-test
        /// state regardless of pass/fail. <c>DurableSaveForTesting</c> is
        /// installed to bypass <c>persistent.sfs</c> writes during the
        /// orchestrator's three durable barriers.
        /// </para>
        /// </summary>
        [InGameTest(Category = "Rewind", Scene = GameScenes.SPACECENTER,
            Description = "Re-Fly on a recording spanning the rewind UT: split keeps launch row, retags+tombstones post-rewind Dead crew action")]
        public void ReFlyFromSpannedRecording_PreservesLaunchRowAndTombstonesPostRewindCrew()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            // Preserve live session/scenario/ledger state so the synthetic
            // fixture doesn't clobber a real save.
            var priorMarker = scenario.ActiveReFlySessionMarker;
            var priorJournal = scenario.ActiveMergeJournal;
            var priorSupersedes = scenario.RecordingSupersedes;
            var priorTombstones = scenario.LedgerTombstones;
            var priorRps = scenario.RewindPoints;
            int priorLedgerCount = Ledger.Actions.Count;
            int priorMilestoneCount = MilestoneStore.Milestones.Count;
            var priorDurableSaveHook = MergeJournalOrchestrator.DurableSaveForTesting;
            var priorSaveGameHook = MergeJournalOrchestrator.SaveGameForTesting;

            string sessId = "spanned_split_" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
            string treeId = "spanned_split_tree_" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
            string originId = "spanned_split_origin_" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
            string forkId = "spanned_split_fork_" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
            string deadActionId = "act_dead_" + System.Guid.NewGuid().ToString("N").Substring(0, 8);

            const double originStartUT = 8.42;
            const double rewindUT = 34.24;
            const double originEndUT = 52.7;
            const double launchMilestoneUT = 9.4;
            const double deadActionUT = 50.0;

            // Build origin recording: spans [8.42, 52.7] with a sample at the
            // rewind UT so SplitAtSection's Unity-runtime-only Slerp branch
            // is bypassed (same pattern as the unit-test BuildSpanningOrigin
            // helper in MergeJournalOrchestratorTests.cs:801).
            var origin = new Recording
            {
                RecordingId = originId,
                VesselName = "Kerbal X (acceptance)",
                TreeId = treeId,
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Destroyed,
                LaunchSiteName = "LaunchPad",
                StartBodyName = "Kerbin",
            };
            origin.Points.Add(new TrajectoryPoint { ut = originStartUT });
            origin.Points.Add(new TrajectoryPoint { ut = rewindUT });
            origin.Points.Add(new TrajectoryPoint { ut = originEndUT });
            origin.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = originStartUT,
                endUT = originEndUT,
                sampleRateHz = 1f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = originStartUT },
                    new TrajectoryPoint { ut = rewindUT },
                    new TrajectoryPoint { ut = originEndUT },
                },
            });
            origin.CrewEndStates = new Dictionary<string, KerbalEndState>
            {
                ["Jebediah Kerman"] = KerbalEndState.Dead,
            };
            origin.CrewEndStatesResolved = true;

            // Build the provisional fork: covers [34.5, ...] with Landed terminal.
            var fork = new Recording
            {
                RecordingId = forkId,
                VesselName = "Kerbal X (acceptance) re-fly",
                TreeId = treeId,
                MergeState = MergeState.NotCommitted,
                TerminalStateValue = TerminalState.Landed,
                SupersedeTargetId = originId,
                CreatingSessionId = sessId,
            };
            fork.Points.Add(new TrajectoryPoint { ut = 34.5 });
            fork.Points.Add(new TrajectoryPoint { ut = 45.0 });

            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "SpannedSplit_" + treeId,
                RootRecordingId = originId,
                ActiveRecordingId = originId,
                BranchPoints = new List<BranchPoint>(),
            };
            tree.AddOrReplaceRecording(origin);
            tree.AddOrReplaceRecording(fork);

            // Install the synthetic tree + recordings into committed storage.
            RecordingStore.AddCommittedInternal(origin);
            RecordingStore.AddCommittedInternal(fork);
            var trees = RecordingStore.CommittedTrees;
            for (int i = trees.Count - 1; i >= 0; i--)
                if (trees[i] != null && trees[i].Id == treeId) trees.RemoveAt(i);
            trees.Add(tree);

            // Pre-rewind milestone: FirstLaunch at UT 9.4. The retag predicate
            // is `StartUT >= rewindUT - epsilon`, so this milestone with
            // StartUT ~0 (window from 0..9.4) MUST stay attributed to HEAD.
            var launchMilestone = new Milestone
            {
                StartUT = 0.0,
                EndUT = launchMilestoneUT,
                RecordingId = originId,
            };
            MilestoneStore.AddMilestoneForTesting(launchMilestone);

            // Post-rewind ledger action: KerbalAssignment for the dead kerbal.
            // After the split, this action's UT (50.0) is post-rewind so its
            // RecordingId should be retagged from origin → TIP, then
            // CommitTombstones writes a LedgerTombstone for its ActionId.
            var deadAction = new GameAction
            {
                ActionId = deadActionId,
                Type = GameActionType.KerbalAssignment,
                RecordingId = originId,
                UT = deadActionUT,
                KerbalName = "Jebediah Kerman",
                KerbalRole = "Pilot",
                KerbalEndStateField = KerbalEndState.Dead,
                StartUT = (float)originStartUT,
                EndUT = (float)deadActionUT,
            };
            Ledger.AddAction(deadAction);

            // Build the marker per plan §7d.
            var marker = new ReFlySessionMarker
            {
                SessionId = sessId,
                TreeId = treeId,
                ActiveReFlyRecordingId = forkId,
                OriginChildRecordingId = originId,
                SupersedeTargetId = originId,
                RewindPointUT = rewindUT,
                InvokedUT = rewindUT,
                InvokedRealTime = System.DateTime.UtcNow.ToString("o"),
            };

            // Install fresh scenario collections so we can deterministically
            // count what AppendRelations + CommitTombstones wrote without
            // having to subtract baselines from real save state.
            scenario.ActiveReFlySessionMarker = marker;
            scenario.ActiveMergeJournal = null;
            scenario.RecordingSupersedes = new List<RecordingSupersedeRelation>();
            scenario.LedgerTombstones = new List<LedgerTombstone>();
            scenario.RewindPoints = new List<RewindPoint>();
            scenario.BumpSupersedeStateVersion();

            // Bypass persistent.sfs writes — the orchestrator fires five
            // synchronous DurableSave barriers (begin / split / durable1 /
            // durable2 / durable3) and we don't want to touch the player's
            // save during a Run All sweep.
            var durableCheckpoints = new List<string>();
            MergeJournalOrchestrator.DurableSaveForTesting =
                label => durableCheckpoints.Add(label);
            MergeJournalOrchestrator.SaveGameForTesting =
                (saveName, saveFolder, mode) => "ok";

            ParsekLog.Info("RewindTest",
                $"ReFlyFromSpannedRecording_PreservesLaunchRowAndTombstonesPostRewindCrew: " +
                $"installed origin={originId} fork={forkId} sess={sessId} " +
                $"rewindUT={rewindUT.ToString("F2", CultureInfo.InvariantCulture)} — invoking RunMerge");

            try
            {
                bool ok = MergeJournalOrchestrator.RunMerge(marker, fork);
                InGameAssert.IsTrue(ok, "MergeJournalOrchestrator.RunMerge returned false");

                // 1. Tree topology: two recordings where origin used to be,
                //    HEAD [originStartUT..rewindUT] keeps the original id,
                //    TIP [rewindUT..originEndUT] has a new id, sharing a ChainId.
                Recording head = null;
                Recording tip = null;
                foreach (var rec in RecordingStore.CommittedRecordings)
                {
                    if (rec == null) continue;
                    if (rec.RecordingId == originId) head = rec;
                    else if (rec.TreeId == treeId
                        && rec.RecordingId != forkId
                        && rec.ChainIndex == 1) tip = rec;
                }
                InGameAssert.IsNotNull(head, "HEAD (origin id preserved) not found post-merge");
                InGameAssert.IsNotNull(tip, "TIP (new chain sibling at ChainIndex=1) not found post-merge");
                InGameAssert.AreNotEqual(originId, tip.RecordingId,
                    "TIP must have a new id, distinct from origin's");
                InGameAssert.AreEqual(head.ChainId, tip.ChainId,
                    "HEAD and TIP must share the same ChainId");
                InGameAssert.IsTrue(!string.IsNullOrEmpty(head.ChainId),
                    "Split must allocate a non-empty ChainId on HEAD");
                InGameAssert.AreEqual(0, head.ChainIndex, "HEAD.ChainIndex should be 0");
                InGameAssert.AreEqual(1, tip.ChainIndex, "TIP.ChainIndex should be 1");
                InGameAssert.ApproxEqual(originStartUT, head.StartUT, 0.001,
                    "HEAD.StartUT should equal origin's pre-split StartUT");
                InGameAssert.ApproxEqual(rewindUT, head.EndUT, 0.001,
                    "HEAD.EndUT should equal the rewind UT");
                InGameAssert.ApproxEqual(rewindUT, tip.StartUT, 0.001,
                    "TIP.StartUT should equal the rewind UT");
                InGameAssert.ApproxEqual(originEndUT, tip.EndUT, 0.001,
                    "TIP.EndUT should equal origin's pre-split EndUT");

                // 2. Supersede graph: TIP is superseded by fork; HEAD is NOT.
                bool tipSupersededByFork = false;
                bool headSupersededByFork = false;
                for (int i = 0; i < scenario.RecordingSupersedes.Count; i++)
                {
                    var rel = scenario.RecordingSupersedes[i];
                    if (rel == null) continue;
                    if (rel.NewRecordingId == forkId)
                    {
                        if (rel.OldRecordingId == tip.RecordingId) tipSupersededByFork = true;
                        if (rel.OldRecordingId == originId) headSupersededByFork = true;
                    }
                }
                InGameAssert.IsTrue(tipSupersededByFork,
                    $"Expected supersede row TIP→fork; got rows={scenario.RecordingSupersedes.Count}");
                InGameAssert.IsFalse(headSupersededByFork,
                    "HEAD must NOT be in the supersede write-set (pre-rewind chain-head carve-out)");

                // 3. ERS includes HEAD (visible) but NOT TIP (superseded). Fork
                //    is also visible (nothing supersedes it yet).
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
                InGameAssert.IsTrue(ersHasHead,
                    "ERS must include HEAD (launch portion stays visible)");
                InGameAssert.IsFalse(ersHasTip,
                    "ERS must NOT include TIP (superseded by fork)");
                InGameAssert.IsTrue(ersHasFork,
                    "ERS must include fork (nothing supersedes the new tip yet)");

                // 4. Ledger action retag + tombstone: the Dead action was on
                //    origin's id pre-merge with UT 50.0 (post-rewind), so the
                //    split retagged it to TIP, then CommitTombstones wrote a
                //    LedgerTombstone targeting its ActionId. ELS does not
                //    include the Dead action.
                GameAction retaggedAction = null;
                for (int i = 0; i < Ledger.Actions.Count; i++)
                {
                    if (Ledger.Actions[i] != null
                        && Ledger.Actions[i].ActionId == deadActionId)
                    {
                        retaggedAction = Ledger.Actions[i];
                        break;
                    }
                }
                InGameAssert.IsNotNull(retaggedAction,
                    "Dead action vanished from the ledger — retag should have rewritten its RecordingId, not removed it");
                InGameAssert.AreEqual(tip.RecordingId, retaggedAction.RecordingId,
                    $"Dead action should be retagged to TIP ({tip.RecordingId}) — got {retaggedAction.RecordingId}");

                bool deadActionTombstoned = false;
                for (int i = 0; i < scenario.LedgerTombstones.Count; i++)
                {
                    var ts = scenario.LedgerTombstones[i];
                    if (ts != null && ts.ActionId == deadActionId)
                    {
                        deadActionTombstoned = true;
                        break;
                    }
                }
                InGameAssert.IsTrue(deadActionTombstoned,
                    $"Dead action {deadActionId} should have a LedgerTombstone after merge");

                var els = EffectiveState.ComputeELS();
                bool elsHasDead = false;
                for (int i = 0; i < els.Count; i++)
                {
                    if (els[i] != null && els[i].ActionId == deadActionId)
                    {
                        elsHasDead = true;
                        break;
                    }
                }
                InGameAssert.IsFalse(elsHasDead,
                    "ELS must NOT credit the Dead action — it should be filtered by its tombstone");

                // 5. Milestone retag: the pre-rewind FirstLaunch milestone
                //    (StartUT=0, EndUT=9.4) stays attributed to HEAD's id
                //    because its events-window start is before the rewind.
                Milestone foundLaunchMs = null;
                var milestones = MilestoneStore.Milestones;
                for (int i = 0; i < milestones.Count; i++)
                {
                    var ms = milestones[i];
                    if (ms == null) continue;
                    if (System.Math.Abs(ms.EndUT - launchMilestoneUT) < 0.01)
                    {
                        foundLaunchMs = ms;
                        break;
                    }
                }
                InGameAssert.IsNotNull(foundLaunchMs, "FirstLaunch milestone vanished after merge");
                InGameAssert.AreEqual(originId, foundLaunchMs.RecordingId,
                    $"Pre-rewind FirstLaunch milestone should stay tagged HEAD ({originId}) — got {foundLaunchMs.RecordingId}");

                // 6. TimelineBuilder.Build produces a Start entry for HEAD.
                var timeline = TimelineBuilder.Build(
                    RecordingStore.CommittedRecordings,
                    Ledger.Actions,
                    MilestoneStore.Milestones,
                    isLegacyEventVisible: null);
                bool sawHeadStart = false;
                for (int i = 0; i < timeline.Count; i++)
                {
                    var entry = timeline[i];
                    if (entry == null) continue;
                    if (entry.Type == TimelineEntryType.RecordingStart
                        && entry.RecordingId == head.RecordingId)
                    {
                        sawHeadStart = true;
                        break;
                    }
                }
                InGameAssert.IsTrue(sawHeadStart,
                    "Timeline must contain a RecordingStart entry for HEAD's id (launch row preserved)");

                // 7. Slot tip via the composite walker: HEAD → (chain) → TIP →
                //    (supersede) → fork. The slot's OriginChildRecordingId
                //    points at HEAD; EffectiveTipRecordingId chases the
                //    chain hop and then the supersede edge.
                string compositeTip = EffectiveState.EffectiveTipRecordingId(
                    originId, scenario.RecordingSupersedes);
                InGameAssert.AreEqual(forkId, compositeTip,
                    $"Composite walker for slot.OriginChildRecordingId={originId} should resolve to fork " +
                    $"({forkId}); got {compositeTip}");

                // 8. Pre-rewind carve-out predicate on HEAD. The marker was
                //    cleared at the end of RunMerge (step 7), so reconstruct
                //    a marker shell for the predicate check using the same
                //    RewindPointUT used by the merge. SupersedeTargetId must
                //    name TIP: post-split the live marker's SupersedeTargetId
                //    points at TIP, and the chain-head branch of
                //    IsPreRewindCarveOut needs it to resolve TIP for the
                //    ChainId/ChainIndex shape match.
                var carveOutMarker = new ReFlySessionMarker
                {
                    RewindPointUT = rewindUT,
                    InvokedUT = rewindUT,
                    SupersedeTargetId = tip.RecordingId,
                };
                SupersedeCommit.PreRewindCarveOutReason carveOutReason;
                bool isCarveOut = SupersedeCommit.IsPreRewindCarveOut(
                    head, carveOutMarker, out carveOutReason);
                InGameAssert.IsTrue(isCarveOut,
                    "IsPreRewindCarveOut(HEAD, marker) should return true after the split");
                InGameAssert.AreEqual(
                    SupersedeCommit.PreRewindCarveOutReason.PreRewindChainHead,
                    carveOutReason,
                    $"IsPreRewindCarveOut reason should be PreRewindChainHead; got {carveOutReason}");

                // 9. Watch button enabledness for HEAD. The predicate is a
                //    pure helper — sanity-check that with the four
                //    preconditions satisfied it returns true for HEAD.
                bool watchEnabled = RecordingsTableUI.IsWatchButtonEnabled(
                    hasGhost: true, sameBody: true, inRange: true,
                    isDebris: head.IsDebris);
                InGameAssert.IsTrue(watchEnabled,
                    "IsWatchButtonEnabled(HEAD) with all preconditions met should return true " +
                    "(Bug 1's playback-window issue is separate and out of scope)");

                // 10. Durable barriers fired in order, including the new
                //     post-Begin Split barrier.
                int beginIdx = durableCheckpoints.IndexOf("begin");
                int splitIdx = durableCheckpoints.IndexOf("split");
                int durable1Idx = durableCheckpoints.IndexOf("durable1");
                InGameAssert.IsTrue(
                    beginIdx >= 0 && splitIdx > beginIdx && durable1Idx > splitIdx,
                    $"Expected ordered durable barriers begin < split < durable1; got [{string.Join(",", durableCheckpoints)}]");

                ParsekLog.Info("RewindTest",
                    $"ReFlyFromSpannedRecording: all 10 invariants passed " +
                    $"(head={head.RecordingId}, tip={tip.RecordingId}, fork={forkId}, " +
                    $"supersedeRows={scenario.RecordingSupersedes.Count}, " +
                    $"tombstones={scenario.LedgerTombstones.Count})");
            }
            finally
            {
                // Restore the live save state in the exact reverse of install.
                MergeJournalOrchestrator.DurableSaveForTesting = priorDurableSaveHook;
                MergeJournalOrchestrator.SaveGameForTesting = priorSaveGameHook;

                // Drop every recording in our synthetic tree (HEAD = origin id,
                // TIP under the synthetic treeId with our chainId, and fork).
                // The split may have inserted TIP with a generated id, so we
                // sweep by TreeId rather than by id alone.
                var committed = RecordingStore.CommittedRecordings;
                for (int i = committed.Count - 1; i >= 0; i--)
                {
                    var rec = committed[i];
                    if (rec != null && rec.TreeId == treeId)
                        RecordingStore.RemoveCommittedInternal(rec);
                }
                var liveTrees = RecordingStore.CommittedTrees;
                for (int i = liveTrees.Count - 1; i >= 0; i--)
                    if (liveTrees[i] != null && liveTrees[i].Id == treeId)
                        liveTrees.RemoveAt(i);

                // Drop the synthetic ledger action(s). Anything we added is
                // tagged with originId pre-split or TIP post-split; the
                // simplest restore is to truncate the ledger back to its
                // pre-test count, since AddAction only appends.
                if (Ledger.Actions.Count > priorLedgerCount)
                {
                    Ledger.TruncateActionsForTesting(priorLedgerCount);
                }

                // Drop the synthetic launch milestone (and any retagged one).
                // MilestoneStore.AddMilestoneForTesting also only appends.
                // Reset to pre-test count by removing trailing entries.
                while (MilestoneStore.Milestones.Count > priorMilestoneCount)
                {
                    MilestoneStore.RemoveLastMilestoneForTesting();
                }

                scenario.ActiveReFlySessionMarker = priorMarker;
                scenario.ActiveMergeJournal = priorJournal;
                scenario.RecordingSupersedes = priorSupersedes;
                scenario.LedgerTombstones = priorTombstones;
                scenario.RewindPoints = priorRps;
                scenario.BumpSupersedeStateVersion();
                EffectiveState.ResetCachesForTesting();
            }
        }

        private static BallisticStateVector MakeApoapsisState(
            string bodyName,
            double gravParameter,
            double apoapsisRadius,
            double periapsisRadius)
        {
            double semiMajorAxis = (apoapsisRadius + periapsisRadius) * 0.5;
            double tangentialSpeed = System.Math.Sqrt(
                gravParameter * ((2.0 / apoapsisRadius) - (1.0 / semiMajorAxis)));
            return new BallisticStateVector
            {
                ut = 0.0,
                bodyName = bodyName,
                position = new Vector3d(apoapsisRadius, 0.0, 0.0),
                velocity = new Vector3d(0.0, tangentialSpeed, 0.0)
            };
        }

        private static ExtrapolationBody MakeBody(
            string name,
            double gravParameter,
            double radius,
            double atmosphereDepth = 0.0,
            double sphereOfInfluence = 0.0,
            string parentBodyName = null,
            TerrainAltitudeResolver terrainAltitude = null,
            ParentFrameStateResolver parentFrameState = null,
            SurfaceCoordinatesResolver surfaceCoordinates = null)
        {
            return new ExtrapolationBody
            {
                Name = name,
                ParentBodyName = parentBodyName,
                GravitationalParameter = gravParameter,
                Radius = radius,
                AtmosphereDepth = atmosphereDepth,
                SphereOfInfluence = sphereOfInfluence,
                TerrainAltitude = terrainAltitude,
                ParentFrameState = parentFrameState,
                SurfaceCoordinates = surfaceCoordinates
            };
        }

        private static OrbitSegment MakeOrbitSegment(
            double startUT,
            double endUT,
            string bodyName,
            bool isPredicted,
            double sma = 700000.0,
            double ecc = 0.01)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                bodyName = bodyName,
                semiMajorAxis = sma,
                eccentricity = ecc,
                inclination = 0.0,
                longitudeOfAscendingNode = 0.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0,
                epoch = startUT,
                isPredicted = isPredicted
            };
        }

        private static ParentFrameStateResolver FixedState(Vector3d position, Vector3d velocity)
        {
            return (double ut, out Vector3d bodyPosition, out Vector3d bodyVelocity) =>
            {
                bodyPosition = position;
                bodyVelocity = velocity;
            };
        }

        private static bool FlatTerrain(double latitude, double longitude, out double altitude)
        {
            altitude = 0.0;
            return true;
        }

        private static void ZeroSurfaceCoordinates(
            double ut,
            Vector3d position,
            out double latitude,
            out double longitude)
        {
            latitude = 0.0;
            longitude = 0.0;
        }

        private static FakePatchedConicOrbitPatch MakePatch(
            double startUT,
            double endUT,
            string bodyName,
            PatchedConicTransitionType transition = PatchedConicTransitionType.Final)
        {
            return new FakePatchedConicOrbitPatch
            {
                StartUT = startUT,
                EndUT = endUT,
                BodyName = bodyName,
                Inclination = 0.0,
                Eccentricity = 0.01,
                SemiMajorAxis = 700000.0,
                LongitudeOfAscendingNode = 0.0,
                ArgumentOfPeriapsis = 0.0,
                MeanAnomalyAtEpoch = 0.0,
                Epoch = startUT,
                EndTransition = transition
            };
        }

        private sealed class FakePatchedConicSnapshotSource : IPatchedConicSnapshotSource
        {
            private int patchLimit;

            public FakePatchedConicSnapshotSource(int initialPatchLimit)
            {
                patchLimit = initialPatchLimit;
            }

            public string VesselName => "Runtime Fake Vessel";
            public bool IsAvailable { get; set; } = true;
            public bool HasPatchLimitAccess { get; set; } = true;
            public IPatchedConicOrbitPatch RootPatch { get; set; }

            public int PatchLimit
            {
                get => patchLimit;
                set => patchLimit = value;
            }

            public void Update() { }
        }

        private sealed class FakePatchedConicOrbitPatch : IPatchedConicOrbitPatch
        {
            public double StartUT { get; set; }
            public double EndUT { get; set; }
            public double Inclination { get; set; }
            public double Eccentricity { get; set; }
            public double SemiMajorAxis { get; set; }
            public double LongitudeOfAscendingNode { get; set; }
            public double ArgumentOfPeriapsis { get; set; }
            public double MeanAnomalyAtEpoch { get; set; }
            public double Epoch { get; set; }
            public string BodyName { get; set; }
            public PatchedConicTransitionType EndTransition { get; set; }
            public IPatchedConicOrbitPatch NextPatch { get; set; }
        }

        // ----------------------------------------------------------------
        // Phase B.2 Harmony-patch registration stubs. Full end-to-end
        // arming / consuming behavior is covered by Phase F in-game tests;
        // these stubs confirm the three patches are registered with Harmony
        // at startup and bound to the correct stock targets. Each stub picks
        // the scene that the corresponding stock UI button is clicked from.
        // ----------------------------------------------------------------

        private const string ParsekHarmonyId = "com.parsek.mod";

        private static bool ParsekHarmonyHasPatchOn(MethodBase target)
        {
            if (target == null)
                return false;
            var info = Harmony.GetPatchInfo(target);
            if (info == null)
                return false;
            return info.Owners != null && info.Owners.Contains(ParsekHarmonyId);
        }

        [InGameTest(
            Category = "SwitchIntentPatch",
            Description = "Tracking Station Fly arming patch is registered with Harmony",
            Scene = GameScenes.TRACKSTATION)]
        public void TrackingStationFlyPatch_RegisteredWithHarmony()
        {
            // Fails if: the patch is not registered at Harmony startup (e.g.,
            // class was removed, [HarmonyPatch] attribute was dropped, or
            // ParsekHarmony.Awake skipped it due to a load-order failure).
            MethodInfo target = typeof(SpaceTracking).GetMethod(
                "FlyVessel",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            InGameAssert.IsNotNull(target, "SpaceTracking.FlyVessel must resolve");
            InGameAssert.IsTrue(
                ParsekHarmonyHasPatchOn(target),
                "Harmony.GetPatchInfo(SpaceTracking.FlyVessel) must list com.parsek.mod as an owner");
        }

        [InGameTest(
            Category = "SwitchIntentPatch",
            Description = "KSC marker Fly arming patch is registered with Harmony",
            Scene = GameScenes.SPACECENTER)]
        public void KscVesselMarkerFlyPatch_RegisteredWithHarmony()
        {
            // Fails if: the patch is not registered at Harmony startup.
            MethodInfo target = typeof(KSCVesselMarkers).GetMethod(
                "FlyVessel",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(Vessel) },
                modifiers: null);
            InGameAssert.IsNotNull(target, "KSCVesselMarkers.FlyVessel(Vessel) must resolve");
            InGameAssert.IsTrue(
                ParsekHarmonyHasPatchOn(target),
                "Harmony.GetPatchInfo(KSCVesselMarkers.FlyVessel) must list com.parsek.mod as an owner");
        }

        [InGameTest(
            Category = "SwitchIntentPatch",
            Description = "Map FocusObject OnSelect arming patch is registered with Harmony",
            Scene = GameScenes.FLIGHT)]
        public void MapFocusObjectOnSelectPatch_RegisteredWithHarmony()
        {
            // Fails if: the patch is not registered at Harmony startup (e.g.,
            // FocusObject namespace renamed, OnSelect method missing).
            System.Type focusObjectType = typeof(
                KSP.UI.Screens.Mapview.MapContextMenuOptions.FocusObject);
            MethodInfo target = focusObjectType.GetMethod(
                "OnSelect",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            InGameAssert.IsNotNull(target, "FocusObject.OnSelect must resolve");
            InGameAssert.IsTrue(
                ParsekHarmonyHasPatchOn(target),
                "Harmony.GetPatchInfo(FocusObject.OnSelect) must list com.parsek.mod as an owner");
        }

        // ----------------------------------------------------------------
        // Phase F in-game promotion: pure-gate predicate coverage for the
        // Map Switch-To Prefix arming logic. The Prefix's three-gate decision
        // (FocusMode == OwnedVessel, CanSwitchVesselsFar, vessel non-null)
        // is factored into Patches.MapFocusObjectOnSelectPatch.ShouldArmMapSwitchTo
        // so we can drive each branch combinatorially here from a live KSP
        // scene without needing to inject a real MapContextMenuOptions.FocusObject
        // instance. Plan test #22.
        // ----------------------------------------------------------------

        [InGameTest(
            Category = "SwitchSegment",
            Description = "Map Switch-To gate arms for OwnedVessel focus mode (FocusObject Prefix)",
            Scene = GameScenes.FLIGHT)]
        public void MapFocusObjectOnSelect_PrefixGate_OwnedVesselFocusMode_AllowsArm()
        {
            // Fails if: the OwnedVessel branch (Switch-To on a real owned
            // vessel with far-switch enabled and a non-null vessel reference)
            // is refused. This is the in-scope happy path.
            bool wouldArm = Parsek.Patches.MapFocusObjectOnSelectPatch.ShouldArmMapSwitchTo(
                isOwnedVesselMode: true,
                canSwitchVesselsFar: true,
                vesselNotNull: true);
            InGameAssert.IsTrue(wouldArm,
                "OwnedVessel + far-switch + non-null vessel must arm");
        }

        [InGameTest(
            Category = "SwitchSegment",
            Description = "Map Switch-To gate refuses UnownedVessel focus mode (routes to TS)",
            Scene = GameScenes.FLIGHT)]
        public void MapFocusObjectOnSelect_PrefixGate_UnownedVesselFocusMode_DoesNotArm()
        {
            // Fails if: the UnownedVessel branch is mistakenly armed. That
            // branch calls SpaceTracking.GoToAndFocusVessel which loads
            // TRACKSTATION — the TS Fly patch is responsible for arming
            // there, not the Map Switch-To patch.
            bool wouldArm = Parsek.Patches.MapFocusObjectOnSelectPatch.ShouldArmMapSwitchTo(
                isOwnedVesselMode: false,
                canSwitchVesselsFar: true,
                vesselNotNull: true);
            InGameAssert.IsFalse(wouldArm,
                "Non-OwnedVessel focus mode must NOT arm Map Switch-To intent");
        }

        [InGameTest(
            Category = "SwitchSegment",
            Description = "Map Switch-To gate refuses CelestialBody focus mode (camera-only)",
            Scene = GameScenes.FLIGHT)]
        public void MapFocusObjectOnSelect_PrefixGate_CelestialBodyFocusMode_DoesNotArm()
        {
            // Fails if: the CelestialBody branch is mistakenly armed. That
            // branch is camera-only (PlanetariumCamera.SetTarget); no
            // vessel switch happens.
            bool wouldArm = Parsek.Patches.MapFocusObjectOnSelectPatch.ShouldArmMapSwitchTo(
                isOwnedVesselMode: false,
                canSwitchVesselsFar: true,
                vesselNotNull: true);
            InGameAssert.IsFalse(wouldArm,
                "CelestialBody focus mode must NOT arm Map Switch-To intent");
        }

        [InGameTest(
            Category = "SwitchSegment",
            Description = "Map Switch-To gate refuses arming when CanSwitchVesselsFar is off",
            Scene = GameScenes.FLIGHT)]
        public void MapFocusObjectOnSelect_PrefixGate_CanSwitchVesselsFarOff_DoesNotArm()
        {
            // Fails if: arming proceeds with far-switch disabled. Stock
            // refuses the switch in that case; arming would leak a stuck
            // marker.
            bool wouldArm = Parsek.Patches.MapFocusObjectOnSelectPatch.ShouldArmMapSwitchTo(
                isOwnedVesselMode: true,
                canSwitchVesselsFar: false,
                vesselNotNull: true);
            InGameAssert.IsFalse(wouldArm,
                "CanSwitchVesselsFar=false must NOT arm Map Switch-To intent");
        }

        [InGameTest(
            Category = "SwitchSegment",
            Description = "Map Switch-To gate refuses arming when target vessel is null (Traverse-failed)",
            Scene = GameScenes.FLIGHT)]
        public void MapFocusObjectOnSelect_PrefixGate_NullVessel_DoesNotArm()
        {
            // Fails if: the gate proceeds with a null vessel (would arm
            // with PID 0). Stock would crash inside SetActiveVessel; our
            // Prefix must bail and log a Warn instead.
            bool wouldArm = Parsek.Patches.MapFocusObjectOnSelectPatch.ShouldArmMapSwitchTo(
                isOwnedVesselMode: true,
                canSwitchVesselsFar: true,
                vesselNotNull: false);
            InGameAssert.IsFalse(wouldArm,
                "vessel=null must NOT arm Map Switch-To intent");
        }

        // ----------------------------------------------------------------
        // Phase F: stock-action intent + segment-session lifecycle from a
        // FLIGHT scene. The full consume site runs from
        // ParsekFlight.TryConsumeStockActionIntent on
        // OnVesselSwitchComplete / OnFlightReady; here we drive only the
        // arm/clear lifecycle on the live ParsekScenario, since arming an
        // intent and watching it clear with the right reason is the
        // smallest atomic in-game contract.
        // ----------------------------------------------------------------

        [InGameTest(
            Category = "SwitchSegment",
            Description = "ParsekScenario arms and clears a stock-action intent marker cleanly",
            Scene = GameScenes.FLIGHT)]
        public void StockActionIntent_ArmAndClear_OnLiveScenario_LeavesNoLeak()
        {
            // Fails if: the live ParsekScenario.Instance does not retain a
            // freshly armed marker, or the clear leaves it lingering. These
            // are the two leaf operations the Phase B Harmony patches and
            // the Phase C consume site rely on.
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario,
                "ParsekScenario.Instance must exist in FLIGHT");

            var marker = new StockActionIntentMarker
            {
                IntentId = System.Guid.NewGuid(),
                Action = StockActionType.MapSwitchTo,
                TargetVesselPersistentId = 7777u,
                SourceScene = StockActionSourceScene.Flight,
                CapturedRealtime = UnityEngine.Time.realtimeSinceStartup,
                CapturedUT = Planetarium.fetch != null
                    ? Planetarium.GetUniversalTime() : 0.0,
                ProcessSessionId = ParsekProcess.ProcessSessionId,
            };

            // Snapshot any prior marker (defensive — should be null in a
            // clean session) and clear before arming so this test is
            // observable independently of prior in-game tests.
            scenario.ClearStockActionIntent("in-game-test-prearm");
            scenario.ArmStockActionIntent(marker);
            InGameAssert.IsNotNull(scenario.CurrentStockActionIntent,
                "Intent must be retrievable after ArmStockActionIntent");
            InGameAssert.AreEqual(
                marker.IntentId, scenario.CurrentStockActionIntent.IntentId,
                "Armed marker's IntentId must match");

            scenario.ClearStockActionIntent("in-game-test-cleanup");
            InGameAssert.IsNull(scenario.CurrentStockActionIntent,
                "Intent must be null after ClearStockActionIntent");
        }

        // M5 (PR #876 round-5 review): four in-game tests that exercised
        // DecidePreSwitchDialogAction with hardcoded args were deleted —
        // they were exact duplicates of the xUnit cases in
        // SwitchIntentPatchSmokeTests.cs and read no live state.
        // Per memory/reference_parsek_scenario_xunit.md, in-game tests
        // are for things that require live KSP runtime; pure decision
        // predicates belong in xUnit.

        #region DrawdownGuard

        // recalc-patch-drawdown-guard plan §10.2: the real AddFunds/AddScience/
        // SetReputation and the live singletons only exist inside KSP, so prove the
        // guard's clamp + signal-bypass wiring against the actual Funding/R&D/Reputation
        // singletons. xUnit covers the pure helpers; this proves the live patch path.
        //
        // SAFETY: this test mutates the live career singletons. It snapshots the original
        // funds/science/rep up front and restores them in a finally so it never alters the
        // player's career, and it never calls the full ledger recalc (which would write to
        // disk) — it drives the patch methods directly with throwaway seeded modules.
        [Parsek.InGameTests.InGameTest(Category = "Ledger", Scene = GameScenes.SPACECENTER,
            Description = "Drawdown guard clamps a too-low funds/science/rep patch with no time-travel context, and applies it when a signal authorizes the reduction")]
        public void DrawdownGuard_ClampsLeakAndAuthorizesSignal()
        {
            if (Funding.Instance == null || ResearchAndDevelopment.Instance == null
                || Reputation.Instance == null)
            {
                Parsek.InGameTests.InGameAssert.Skip(
                    "requires a career game with Funding/R&D/Reputation singletons");
                return;
            }

            double origFunds = Funding.Instance.Funds;
            float origScience = ResearchAndDevelopment.Instance.Science;
            float origRep = Reputation.Instance.reputation;

            // Force a known live baseline well above any module running balance so the
            // would-be patch target is clearly below live.
            const double LiveFunds = 100000.0;
            const float LiveScience = 500f;
            const float LiveRep = 40f;
            // Module running balances are seeded BELOW live to mimic a missing earning
            // channel (the BUG-A signature): running < live with no reservation.
            const double RunningFunds = 60000.0;
            const float RunningScience = 100f;
            const float RunningRep = 10f;

            KspStatePatcher.ResetForTesting();
            try
            {
                Funding.Instance.SetFunds(LiveFunds, TransactionReasons.None);
                ResearchAndDevelopment.Instance.SetScience(LiveScience, TransactionReasons.None);
                Reputation.Instance.SetReputation(LiveRep, TransactionReasons.None);

                // ---- Case 1: NO time-travel signal -> clamp (live preserved) ----
                var funds = MakeSeededFundsModule(RunningFunds);
                var science = MakeSeededScienceModule(RunningScience);
                var rep = MakeSeededReputationModule(RunningRep);

                KspStatePatcher.PatchFunds(funds, authoritativeReduction: false);
                KspStatePatcher.PatchScience(science, authoritativeReduction: false);
                KspStatePatcher.PatchReputation(rep, authoritativeReduction: false);

                Parsek.InGameTests.InGameAssert.IsTrue(
                    System.Math.Abs(Funding.Instance.Funds - LiveFunds) < 0.5,
                    $"Funds must be clamped to live (expected {LiveFunds}, got {Funding.Instance.Funds})");
                Parsek.InGameTests.InGameAssert.IsTrue(
                    System.Math.Abs(ResearchAndDevelopment.Instance.Science - LiveScience) < 0.5f,
                    $"Science must be clamped to live (expected {LiveScience}, got {ResearchAndDevelopment.Instance.Science})");
                Parsek.InGameTests.InGameAssert.IsTrue(
                    System.Math.Abs(Reputation.Instance.reputation - LiveRep) < 0.5f,
                    $"Reputation must be clamped to live (expected {LiveRep}, got {Reputation.Instance.reputation})");

                // ---- Case 2: an authoritative signal -> reduction APPLIES ----
                // Drive a single signal (signal 5) true so authoritativeReduction is true.
                RewindContext.BeginRewindResourceAdjustment();
                try
                {
                    var funds2 = MakeSeededFundsModule(RunningFunds);
                    var science2 = MakeSeededScienceModule(RunningScience);
                    var rep2 = MakeSeededReputationModule(RunningRep);

                    bool authoritative = LedgerOrchestrator.IsAuthoritativeReduction(
                        RewindContext.IsRewinding,
                        false, false, false,
                        RewindContext.RewindResourceAdjustmentInProgress);
                    Parsek.InGameTests.InGameAssert.IsTrue(authoritative,
                        "Signal 5 must make IsAuthoritativeReduction true");

                    KspStatePatcher.PatchFunds(funds2, authoritativeReduction: authoritative);
                    KspStatePatcher.PatchScience(science2, authoritativeReduction: authoritative);
                    KspStatePatcher.PatchReputation(rep2, authoritativeReduction: authoritative);

                    Parsek.InGameTests.InGameAssert.IsTrue(
                        System.Math.Abs(Funding.Instance.Funds - RunningFunds) < 0.5,
                        $"Authorized reduction must apply funds target (expected {RunningFunds}, got {Funding.Instance.Funds})");
                    Parsek.InGameTests.InGameAssert.IsTrue(
                        System.Math.Abs(ResearchAndDevelopment.Instance.Science - RunningScience) < 0.5f,
                        $"Authorized reduction must apply science target (expected {RunningScience}, got {ResearchAndDevelopment.Instance.Science})");
                    Parsek.InGameTests.InGameAssert.IsTrue(
                        System.Math.Abs(Reputation.Instance.reputation - RunningRep) < 0.5f,
                        $"Authorized reduction must apply rep target (expected {RunningRep}, got {Reputation.Instance.reputation})");
                }
                finally
                {
                    RewindContext.EndRewindResourceAdjustment();
                }

                ParsekLog.Info("TestRunner",
                    "DrawdownGuard_ClampsLeakAndAuthorizesSignal: clamp + signal-bypass verified against live singletons");
            }
            finally
            {
                // Restore the player's real career values no matter what.
                Funding.Instance.SetFunds(origFunds, TransactionReasons.None);
                ResearchAndDevelopment.Instance.SetScience(origScience, TransactionReasons.None);
                Reputation.Instance.SetReputation(origRep, TransactionReasons.None);
                KspStatePatcher.ResetForTesting();
            }
        }

        private static FundsModule MakeSeededFundsModule(double running)
        {
            var m = new FundsModule();
            m.ProcessAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.FundsInitial,
                InitialFunds = (float)running,
            });
            return m;
        }

        private static ScienceModule MakeSeededScienceModule(double running)
        {
            var m = new ScienceModule();
            m.ProcessScienceInitial(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.ScienceInitial,
                InitialScience = (float)running,
            });
            return m;
        }

        private static ReputationModule MakeSeededReputationModule(float running)
        {
            var m = new ReputationModule();
            m.ProcessReputationInitial(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.ReputationInitial,
                InitialReputation = running,
            });
            return m;
        }

        // Bug 2 (fix-funds-economy-divergence §4): the symmetric uplift guard. The xUnit
        // ApplyDrawdownGuard facts cover the pure clamp; this proves the LIVE PatchFunds
        // AddFunds write path actually caps DOWN to live when the running balance LEADS live
        // with no time-travel authority (the facility-refund signature: ledger running >
        // live because a real spend the ledger does not model already hit the live singleton).
        //
        // SAFETY: snapshots the live funds up front and restores them in a finally; it never
        // calls the full ledger recalc and drives PatchFunds directly with a throwaway module.
        [Parsek.InGameTests.InGameTest(Category = "Ledger", Scene = GameScenes.SPACECENTER,
            Description = "Symmetric uplift guard caps a too-high funds patch DOWN to live with no time-travel context (facility-refund leak), and applies the higher target when a signal authorizes it")]
        public void DrawdownGuard_RefundLeak_ClampedDownToLive()
        {
            if (Funding.Instance == null)
            {
                Parsek.InGameTests.InGameAssert.Skip(
                    "requires a career game with the Funding singleton");
                return;
            }

            double origFunds = Funding.Instance.Funds;

            // Live is BELOW the module running balance: the player spent on a facility
            // (live already dropped) but the ledger does not model the spend, so running
            // still LEADS live. A non-authoritative recalc would refund the spend.
            const double LiveFunds = 66386.0;
            const double RunningFunds = 415466.0;

            KspStatePatcher.ResetForTesting();
            try
            {
                Funding.Instance.SetFunds(LiveFunds, TransactionReasons.None);

                // ---- Case 1: NO time-travel signal -> cap DOWN to live (spend held) ----
                var funds = MakeSeededFundsModule(RunningFunds);
                KspStatePatcher.PatchFunds(funds, authoritativeReduction: false);

                Parsek.InGameTests.InGameAssert.IsTrue(
                    System.Math.Abs(Funding.Instance.Funds - LiveFunds) < 0.5,
                    $"Funds must be capped DOWN to live (expected {LiveFunds}, got {Funding.Instance.Funds}) — the refund must be held");

                // ---- Case 2: an authoritative signal -> the higher target APPLIES ----
                // A genuine time-travel restore that raises funds must NOT be clamped.
                RewindContext.BeginRewindResourceAdjustment();
                try
                {
                    Funding.Instance.SetFunds(LiveFunds, TransactionReasons.None);
                    var funds2 = MakeSeededFundsModule(RunningFunds);
                    bool authoritative = LedgerOrchestrator.IsAuthoritativeReduction(
                        RewindContext.IsRewinding,
                        false, false, false,
                        RewindContext.RewindResourceAdjustmentInProgress);
                    Parsek.InGameTests.InGameAssert.IsTrue(authoritative,
                        "Signal 5 must make IsAuthoritativeReduction true");

                    KspStatePatcher.PatchFunds(funds2, authoritativeReduction: authoritative);

                    Parsek.InGameTests.InGameAssert.IsTrue(
                        System.Math.Abs(Funding.Instance.Funds - RunningFunds) < 0.5,
                        $"Authorized recalc must apply the higher running target (expected {RunningFunds}, got {Funding.Instance.Funds})");
                }
                finally
                {
                    RewindContext.EndRewindResourceAdjustment();
                }

                ParsekLog.Info("TestRunner",
                    "DrawdownGuard_RefundLeak_ClampedDownToLive: uplift cap + signal-bypass verified against live Funding");
            }
            finally
            {
                Funding.Instance.SetFunds(origFunds, TransactionReasons.None);
                KspStatePatcher.ResetForTesting();
            }
        }

        // Bug 2 (fix-funds-economy-divergence §2.3): a live KSC TECH-UNLOCK window must NOT
        // trip a spurious DOWN (uplift) clamp. This exercises the REAL transient, not a
        // trivial within-epsilon no-op: during a tech unlock KSP debits the live science pool
        // immediately (live drops) but the matching TechResearched -> ScienceSpending ledger
        // action has not landed yet, so the RAW GetRunningScience() reads ABOVE the
        // already-dropped live value (an apparent uplift). The §2.3 guarantee is that the
        // pending-tech-research DEBIT adjuster lowers BOTH the drawdown-guard discriminator
        // (ComputePendingAdjustedRunningScience) AND the patch target by the same pending
        // debit, pulling running back to ~live so no spurious DOWN clamp fires. We set up the
        // pending debit through its real inputs (a recent ScienceChanged(RnDTechResearch)
        // event in the store with no matching committed ScienceSpending) and assert NO
        // GUARDED clamp WARN fires. If the debit adjuster regressed, the raw running would
        // read > live and a spurious uplift clamp WARN would fire — this test would catch it.
        //
        // SAFETY: snapshots the live science up front and restores it in a finally; drives
        // PatchScience directly with a throwaway seeded module and resets the store / UT seam.
        [Parsek.InGameTests.InGameTest(Category = "Ledger", Scene = GameScenes.SPACECENTER,
            Description = "Science patch during a live tech-unlock window (raw running above live, pending debit pending) performs no spurious uplift DOWN clamp")]
        public void DrawdownGuard_ScienceTechUnlockWindow_NoSpuriousDownClamp()
        {
            if (ResearchAndDevelopment.Instance == null)
            {
                Parsek.InGameTests.InGameAssert.Skip(
                    "requires a career/science game with the ResearchAndDevelopment singleton");
                return;
            }

            float origScience = ResearchAndDevelopment.Instance.Science;

            // Pre-unlock balance the module still reflects (ledger spend not yet ingested).
            const float PreUnlockScience = 500f;
            // Stock debit already applied to the live pool by the tech unlock.
            const double UnlockDebit = 45.0;
            // Post-debit live value the pool actually sits at right now.
            const float LiveScience = (float)(PreUnlockScience - UnlockDebit); // 455
            const double WindowUt = 100000.0;

            var captured = new List<string>();
            var prevSink = ParsekLog.TestSinkForTesting;
            ParsekLog.TestSinkForTesting = line => captured.Add(line);

            KspStatePatcher.ResetForTesting();
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.NowUtProviderForTesting = () => WindowUt;
            try
            {
                // Live pool already dropped to the post-debit value.
                ResearchAndDevelopment.Instance.SetScience(LiveScience, TransactionReasons.None);

                // The module still holds the PRE-unlock running balance: raw GetRunningScience()
                // reads 500 > live 455 -> looks like an uplift unless the debit adjuster fires.
                var science = MakeSeededScienceModule(PreUnlockScience);

                // Recent stock tech-unlock debit in the store, no matching committed
                // ScienceSpending -> ComputePendingRecentKscTechResearchScienceDebit == 45.
                var debitEvt = new GameStateEvent
                {
                    ut = WindowUt,
                    eventType = GameStateEventType.ScienceChanged,
                    key = LedgerOrchestrator.TechResearchScienceReasonKey, // "RnDTechResearch"
                    valueBefore = PreUnlockScience,
                    valueAfter = LiveScience,
                    recordingId = ""
                };
                GameStateStore.AddEvent(ref debitEvt);

                // Sanity: the pending debit the adjuster will use must be the unlock debit.
                double pendingDebit = LedgerOrchestrator.GetPendingRecentKscTechResearchScienceDebit();
                Parsek.InGameTests.InGameAssert.IsTrue(
                    System.Math.Abs(pendingDebit - UnlockDebit) < 0.5,
                    $"Pending tech-unlock debit must be ~{UnlockDebit} (got {pendingDebit}) for the transient to be real");

                KspStatePatcher.PatchScience(science, authoritativeReduction: false);

                // No guarded clamp of EITHER direction may fire: the adjusted discriminator
                // and target both sit at ~live, so the guard sees running ~ live.
                bool clampWarned = captured.Exists(l =>
                    l.Contains("[KspStatePatcher]")
                    && (l.Contains("GUARDED UPLIFT clamped") || l.Contains("GUARDED DRAWDOWN clamped")));
                Parsek.InGameTests.InGameAssert.IsFalse(clampWarned,
                    "No spurious DOWN/uplift clamp may fire during a tech-unlock window (the pending debit adjuster holds running at live)");

                // And the live pool must be left at the post-debit value (the adjusted target
                // equals live, so the patch is a no-op).
                Parsek.InGameTests.InGameAssert.IsTrue(
                    System.Math.Abs(ResearchAndDevelopment.Instance.Science - LiveScience) < 0.5f,
                    $"Science must stay at the post-debit live value (expected {LiveScience}, got {ResearchAndDevelopment.Instance.Science})");

                ParsekLog.Info("TestRunner",
                    "DrawdownGuard_ScienceTechUnlockWindow_NoSpuriousDownClamp: pending-debit adjuster held running at live, no spurious clamp");
            }
            finally
            {
                ParsekLog.TestSinkForTesting = prevSink;
                ResearchAndDevelopment.Instance.SetScience(origScience, TransactionReasons.None);
                LedgerOrchestrator.NowUtProviderForTesting = null;
                GameStateStore.ResetForTesting();
                KspStatePatcher.ResetForTesting();
            }
        }

        // Rewind read-back divergence guard (audit rec #1): prove the warn-only guard fires
        // a FLAGGED DIVERGENCE WARN against the LIVE economy modules without altering any live
        // value. Arms the guard with two witnesses deliberately ABOVE the live realized
        // economy (a synthetic downward divergence), runs the runner against the live
        // Funds/Science/Reputation modules built from the live singletons, and asserts the
        // WARN fired and the live values are unchanged (warn-only). Does NOT test abort.
        //
        // SAFETY: snapshots the three pools up front and restores them in a finally; it never
        // calls the full ledger recalc and never enables the abort opt-in, so the player's
        // career is numerically untouched.
        [Parsek.InGameTests.InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "Rewind read-back guard fires a divergence WARN (warn-only) against the live economy without altering any live value")]
        public void RewindReadbackGuard_WarnsOnDivergence_LeavesLiveUnchanged()
        {
            if (Funding.Instance == null || ResearchAndDevelopment.Instance == null
                || Reputation.Instance == null)
            {
                Parsek.InGameTests.InGameAssert.Skip(
                    "requires a career game with Funding/R&D/Reputation singletons");
                return;
            }

            double origFunds = Funding.Instance.Funds;
            float origScience = ResearchAndDevelopment.Instance.Science;
            float origRep = Reputation.Instance.reputation;

            // Capture the test-sink log lines so we can assert the WARN fired.
            var captured = new List<string>();
            var prevSink = ParsekLog.TestSinkForTesting;
            ParsekLog.TestSinkForTesting = line => captured.Add(line);

            RewindReadbackGuard.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            try
            {
                // Live realized running balances seeded BELOW the witnesses to mimic a clobber:
                // both witnesses are above, so floor > target -> downward divergence flagged.
                const double RunningFunds = 60000.0;
                const float RunningScience = 100f;
                const float RunningRep = 10f;
                var funds = MakeSeededFundsModule(RunningFunds);
                var science = MakeSeededScienceModule(RunningScience);
                var rep = MakeSeededReputationModule(RunningRep);

                // Two witnesses deliberately above the running targets (E_before / E_rp).
                RewindReadbackGuard.Arm(
                    new EconomySnapshot { Funds = 100000.0, Science = 500.0, Reputation = 40f },
                    new EconomySnapshot { Funds = 120000.0, Science = 600.0, Reputation = 50f });

                // Abort opt-in stays OFF -> warn-only; runner must return false.
                bool abort = KspStatePatcher.RunRewindReadbackGuard(
                    science, funds, rep, authoritativeReduction: false);

                Parsek.InGameTests.InGameAssert.IsFalse(abort,
                    "Warn-only guard must never request an abort");

                bool warned = captured.Exists(l =>
                    l.Contains("[RewindReadback]")
                    && l.Contains("FLAGGED DIVERGENCE")
                    && l.Contains("resource=funds"));
                Parsek.InGameTests.InGameAssert.IsTrue(warned,
                    "Guard must emit a FLAGGED DIVERGENCE WARN for the funds clobber");

                // Live economy must be untouched (the guard reads modules, never writes KSP).
                Parsek.InGameTests.InGameAssert.IsTrue(
                    System.Math.Abs(Funding.Instance.Funds - origFunds) < 0.5,
                    $"Live funds must be unchanged by the guard (expected {origFunds}, got {Funding.Instance.Funds})");
                Parsek.InGameTests.InGameAssert.IsTrue(
                    System.Math.Abs(ResearchAndDevelopment.Instance.Science - origScience) < 0.5f,
                    $"Live science must be unchanged by the guard (expected {origScience}, got {ResearchAndDevelopment.Instance.Science})");
                Parsek.InGameTests.InGameAssert.IsTrue(
                    System.Math.Abs(Reputation.Instance.reputation - origRep) < 0.5f,
                    $"Live reputation must be unchanged by the guard (expected {origRep}, got {Reputation.Instance.reputation})");

                ParsekLog.Info("TestRunner",
                    "RewindReadbackGuard_WarnsOnDivergence_LeavesLiveUnchanged: divergence WARN fired, live economy untouched (warn-only)");
            }
            finally
            {
                RewindReadbackGuard.Clear();
                ParsekLog.TestSinkForTesting = prevSink;
                // Restore the player's real career values no matter what (the guard does not
                // write KSP, but ResetForTesting + defense-in-depth keeps the career pristine).
                using (SuppressionGuard.Resources())
                {
                    Funding.Instance.SetFunds(origFunds, TransactionReasons.None);
                    ResearchAndDevelopment.Instance.SetScience(origScience, TransactionReasons.None);
                    Reputation.Instance.SetReputation(origRep, TransactionReasons.None);
                }
                RewindReadbackGuard.ResetForTesting();
                KspStatePatcher.ResetForTesting();
            }
        }

        // BUG-G: the affordability gate is the missing other half of the drawdown guard.
        // (1) The gate must reconcile against the live R&D singleton so a missing-earning
        //     leak (running below live, no time-travel context) does NOT falsely block an
        //     affordable purchase — exercised against the real ResearchAndDevelopment.Instance.
        // (2) A Parsek block must be NON-DESTRUCTIVE: stock RDTech.ResearchTech deducts
        //     science BEFORE calling UnlockTech, so the block is gated pre-deduction on
        //     ResearchTech. A blocked research must deduct nothing.
        // SAFETY: snapshots and restores live science in a finally; never unlocks a real
        // node (the synthetic node carries an impossible cost so the gate always blocks).
        [Parsek.InGameTests.InGameTest(Category = "Ledger", Scene = GameScenes.SPACECENTER,
            Description = "BUG-G: affordability gate respects the guard-preserved live science, and a blocked tech research deducts nothing (block before deduction)")]
        public void SpendingGate_RespectsLiveAndBlocksNonDestructively()
        {
            if (ResearchAndDevelopment.Instance == null)
            {
                Parsek.InGameTests.InGameAssert.Skip(
                    "requires a career game with the R&D singleton");
                return;
            }

            float origScience = ResearchAndDevelopment.Instance.Science;
            var prevHook = CommittedActionDialog.TestHookForTesting;
            string blockedReason = null;
            CommittedActionDialog.TestHookForTesting = (a, reason, c) => blockedReason = reason;

            GameObject go = null;
            try
            {
                // ---- Bug 1: gate reconciles against the live R&D singleton ----
                // Force a known live science well above a simulated leaked ledger
                // (available == running == 1.0). The gate must add back the guard-preserved
                // live value so the purchase is affordable.
                const float LiveScience = 500f;
                ResearchAndDevelopment.Instance.SetScience(LiveScience, TransactionReasons.None);

                double effLeak = LedgerOrchestrator.ComputeEffectiveAffordable(
                    1.0, 1.0, ResearchAndDevelopment.Instance.Science,
                    authoritativeReduction: false);
                Parsek.InGameTests.InGameAssert.IsTrue(effLeak >= 45.0,
                    $"Gate must respect live science above a leaked ledger (eff={effLeak}, live={ResearchAndDevelopment.Instance.Science})");

                // The authoritative (time-travel) branch keeps the ledger value as truth.
                double effAuth = LedgerOrchestrator.ComputeEffectiveAffordable(
                    1.0, 1.0, ResearchAndDevelopment.Instance.Science,
                    authoritativeReduction: true);
                Parsek.InGameTests.InGameAssert.IsTrue(effAuth < 45.0,
                    $"Authoritative reduction must gate on the ledger value, not live (eff={effAuth})");

                // ---- Bug 2: blocked RDTech.ResearchTech deducts nothing ----
                go = new GameObject("ParsekTestRDTech");
                var tech = go.AddComponent<RDTech>();
                tech.techID = "parsek_test_unaffordable_node";
                tech.title = "Parsek Test Node";
                tech.scienceCost = 1000000000; // never affordable -> gate always blocks
                tech.state = RDTech.State.Unavailable;
                tech.host = ResearchAndDevelopment.Instance;

                float beforeResearch = ResearchAndDevelopment.Instance.Science;
                RDTech.OperationResult result = tech.ResearchTech();

                // Discriminating assertions (state=Unavailable IS the stock purchase path:
                // 'if (state != Available)' deducts + unlocks). If our prefix had ALLOWED,
                // stock's CurrencyModifierQuery would reject the 1e9 cost and return
                // NotEnoughFunds without deducting; our prefix instead skips the original
                // and returns the Failure we set. So result==Failure uniquely proves OUR
                // pre-deduction block fired (not stock's), and the captured "Insufficient
                // science" reason proves it was OUR affordability gate (stock never uses the
                // Parsek dialog). The science-unchanged assertion is the non-destructive
                // safety check (true under either block, but the load-bearing guarantee).
                Parsek.InGameTests.InGameAssert.IsTrue(
                    result == RDTech.OperationResult.Failure,
                    $"Parsek's pre-deduction block must return Failure, not stock NotEnoughFunds (got {result})");
                Parsek.InGameTests.InGameAssert.IsTrue(
                    System.Math.Abs(ResearchAndDevelopment.Instance.Science - beforeResearch) < 0.001f,
                    $"A blocked tech research must deduct NO science (before={beforeResearch}, after={ResearchAndDevelopment.Instance.Science})");
                Parsek.InGameTests.InGameAssert.IsTrue(
                    blockedReason != null
                        && blockedReason.IndexOf("Insufficient science", System.StringComparison.Ordinal) >= 0,
                    $"The block must be Parsek's affordability gate (reason='{blockedReason}')");

                ParsekLog.Info("TestRunner",
                    "SpendingGate_RespectsLiveAndBlocksNonDestructively: live-reconciled gate + non-destructive ResearchTech block verified");
            }
            finally
            {
                if (go != null) UnityEngine.Object.Destroy(go);
                ResearchAndDevelopment.Instance.SetScience(origScience, TransactionReasons.None);
                CommittedActionDialog.TestHookForTesting = prevHook;
            }
        }

        #endregion

        #region Test-runner campaign isolation contract

        /// <summary>
        /// Enforces the EDITOR isolation contract: EDITOR batches isolate the
        /// campaign DiskOnly (a persistent.sfs .bak only, NO in-memory reload, because
        /// reloading the editor mid-edit is fragile). Any test that declares
        /// Scene == GameScenes.EDITOR could therefore mutate persistent career state
        /// without the in-memory revert covering it. This always-run contract fails
        /// loudly if such a test ever appears, forcing whoever adds it to revisit the
        /// isolation mode (ClassifyBatchIsolationMode) rather than silently relying on
        /// a comment.
        /// </summary>
        [InGameTest(Category = "TestRunnerIsolation", Scene = InGameTestAttribute.AnyScene,
            Description = "Contract: no [InGameTest] declares Scene=EDITOR (EDITOR isolation is DiskOnly only)")]
        public void NoEditorSceneTestsExistContract()
        {
            var editorTests = new List<string>();
            var assembly = Assembly.GetExecutingAssembly();
            foreach (var type in assembly.GetTypes())
            {
                foreach (var method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static))
                {
                    var attr = method.GetCustomAttribute<InGameTestAttribute>();
                    if (attr == null) continue;
                    if (attr.Scene == GameScenes.EDITOR)
                        editorTests.Add($"{type.Name}.{method.Name}");
                }
            }

            InGameAssert.IsTrue(editorTests.Count == 0,
                "EDITOR-scene in-game tests exist but EDITOR batch isolation is DiskOnly "
                + "(no in-memory revert). Revisit ClassifyBatchIsolationMode before adding a "
                + "persistent-state-mutating EDITOR test: " + string.Join(", ", editorTests.ToArray()));
            ParsekLog.Info("TestRunner",
                "NoEditorSceneTestsExistContract: 0 EDITOR-scene tests (DiskOnly isolation contract holds)");
        }

        /// <summary>
        /// Validation gate for a FUTURE SPACECENTER in-memory isolation flip.
        /// SPACECENTER currently ships DiskOnly (safety .bak, no in-memory reload)
        /// because the CommitNonFlightSceneLoad / Game.Start() in-memory reload path
        /// is structurally available but UNPROVEN in this codebase. This test exercises
        /// the in-memory revert (mutate funds, capture a baseline, restore via the
        /// non-flight commit path, assert the scalar returned to baseline). It ships
        /// SKIPPED so it does not fail CI; remove the skip and flip
        /// ClassifyBatchIsolationMode SPACECENTER -> InMemoryAndDisk only after this
        /// passes in a manual playtest.
        /// </summary>
        [InGameTest(Category = "TestRunnerIsolation", Scene = GameScenes.SPACECENTER,
            Description = "Gate: SPACECENTER in-memory restore returns funds/science/rep to baseline (skip-until-implemented)")]
        public IEnumerator SpaceCenterBatchIsolationInMemoryRestore()
        {
            InGameAssert.Skip(
                "SPACECENTER in-memory restore not yet enabled; gate test pending green run. "
                + "Remove this skip and flip ClassifyBatchIsolationMode SPACECENTER -> InMemoryAndDisk "
                + "only after this passes in a manual playtest.");
            yield break;
        }

        /// <summary>
        /// Logistics route live-anchor bind (Step 1/2): when the player is flying the
        /// launch-matched anchor vessel (e.g. the Depot station) and a looped Relative
        /// member (e.g. the Deliverer delivery) is playing, the member ghost must dock
        /// against the LIVE anchor, not the anchor's recorded absolute position ~20 km
        /// away. Asserts the resolver binds the anchor pose to the live vessel's
        /// transform (position AND rotation), so a looped delivery ghost tracks the live
        /// station. Skips when no committed Relative member resolves to a launch-matched
        /// loaded live anchor (the common case without an active supply route).
        /// </summary>
        [InGameTest(Category = "RouteLiveAnchor", Scene = GameScenes.FLIGHT,
            Description = "A looped Relative member's anchor pose binds to the live launch-matched anchor vessel (position + attitude), not its recorded position ~20 km away; no anchor ghost double at the recorded coords")]
        public IEnumerator LoopedRelativeMemberDocksWithLiveAnchor()
        {
            // Let one frame pass so the playback engine has positioned ghosts.
            yield return null;

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || committed.Count == 0)
                InGameAssert.Skip("PRECONDITION: no committed recordings");

            double ut = Planetarium.GetUniversalTime();

            // Find a committed Relative member whose anchorRecordingId resolves to a
            // recording whose launch-matched live vessel is currently loaded.
            Recording boundMember = null;
            Recording anchorRec = null;
            Vessel liveAnchor = null;
            for (int i = 0; i < committed.Count; i++)
            {
                Recording member = committed[i];
                if (member == null || member.TrackSections == null) continue;
                for (int s = 0; s < member.TrackSections.Count; s++)
                {
                    TrackSection sec = member.TrackSections[s];
                    if (sec.referenceFrame != ReferenceFrame.Relative) continue;
                    if (string.IsNullOrEmpty(sec.anchorRecordingId)) continue;

                    string anchorId = sec.anchorRecordingId.Trim();
                    Recording candidateAnchor = null;
                    for (int a = 0; a < committed.Count; a++)
                    {
                        if (committed[a] != null
                            && string.Equals(committed[a].RecordingId, anchorId, System.StringComparison.Ordinal))
                        {
                            candidateAnchor = committed[a];
                            break;
                        }
                    }
                    if (candidateAnchor == null) continue;
                    if (!GhostPlaybackLogic.RealVesselExistsForRecording(candidateAnchor)) continue;

                    Vessel v = FlightRecorder.FindVesselByPid(candidateAnchor.VesselPersistentId);
                    if (v == null || v.transform == null) continue;

                    boundMember = member;
                    anchorRec = candidateAnchor;
                    liveAnchor = v;
                    break;
                }
                if (boundMember != null) break;
            }

            if (boundMember == null)
                InGameAssert.Skip("PRECONDITION: no Relative member resolves to a launch-matched loaded live anchor (no active supply route at a live station)");

            Vector3d liveAnchorWorld = (Vector3d)liveAnchor.transform.position;
            Quaternion liveAnchorRot = liveAnchor.transform.rotation;

            // Resolve the anchor pose through the SAME resolver the map / KSC playback
            // uses. With the live-anchor bind it must return the live vessel pose, not
            // the recorded absolute Mun position ~20 km away.
            TrackSection relSection = default(TrackSection);
            for (int s = 0; s < boundMember.TrackSections.Count; s++)
            {
                if (boundMember.TrackSections[s].referenceFrame == ReferenceFrame.Relative
                    && !string.IsNullOrEmpty(boundMember.TrackSections[s].anchorRecordingId)
                    && string.Equals(
                        boundMember.TrackSections[s].anchorRecordingId.Trim(),
                        anchorRec.RecordingId,
                        System.StringComparison.Ordinal))
                {
                    relSection = boundMember.TrackSections[s];
                    break;
                }
            }

            double resolveUT = ut;
            if (ut < relSection.startUT) resolveUT = relSection.startUT;
            else if (ut > relSection.endUT) resolveUT = relSection.endUT;

            bool resolved = RecordedRelativeAnchorPoseResolver.TryResolveSectionAnchorPose(
                boundMember, relSection, resolveUT, out AnchorPose anchorPose);
            InGameAssert.IsTrue(resolved,
                $"anchor pose should resolve for member '{boundMember.VesselName}' "
                + $"anchor '{anchorRec.VesselName}' at ut={resolveUT.ToString("F1", CultureInfo.InvariantCulture)}");

            // (1) Anchor pose is within a few metres of the LIVE anchor transform, NOT
            // ~20 km at the recorded position.
            double posDelta = (anchorPose.WorldPos - liveAnchorWorld).magnitude;
            InGameAssert.IsTrue(posDelta < 50.0,
                $"anchor pose bound to live vessel: |pose - liveAnchor|={posDelta.ToString("F1", CultureInfo.InvariantCulture)} m "
                + "(expected < 50 m; a recorded-absolute fallback would be ~20000 m)");

            // (2) Anchor pose attitude matches the live anchor rotation (docking
            // attitude resolves against the live frame, not the recorded frame).
            float angleDeg = Quaternion.Angle(anchorPose.WorldRotation, liveAnchorRot);
            InGameAssert.IsTrue(angleDeg < 5.0f,
                $"anchor pose attitude matches live vessel: angle={angleDeg.ToString("F2", CultureInfo.InvariantCulture)} deg (expected < 5 deg)");

            // (3) No anchor ghost double parked at the recorded absolute coords ~20 km
            // from the live anchor (the duplicate the suppression removes).
            int doublesFound = 0;
            foreach (uint pid in GhostMapPresence.ghostMapVesselPids)
            {
                if (!FlightGlobals.FindVessel(pid, out Vessel ghost) || ghost == null) continue;
                if (ghost.persistentId != anchorRec.VesselPersistentId) continue;
                double ghostDelta = ((Vector3d)ghost.GetWorldPos3D() - liveAnchorWorld).magnitude;
                if (ghostDelta > 5000.0)
                    doublesFound++;
            }
            InGameAssert.IsTrue(doublesFound == 0,
                $"no anchor ghost double > 5 km from the live anchor (found {doublesFound})");

            // (4) Step-2 live-bind-event suppression scope. The TryResolveSectionAnchorPose
            // resolve above drove the live-bind ledger: the anchor recording is now stamped
            // as live-bound (it IS being docked this frame), the inbound MEMBER is not (the
            // member is the resolver focus, never the resolved anchor). So:
            //  - the inbound member's OWN ghost is NEVER Step-2 suppressed (its delivery
            //    mesh must draw - this is the over-suppression regression the fix removes);
            //  - the anchor's OWN duplicate IS Step-2 suppressed only while it is the live
            //    docking anchor (its launch-matched live vessel is loaded + it was just
            //    live-bound). Both are evaluated as loop members (loopingLike:true).
            InGameAssert.IsTrue(
                !GhostPlaybackLogic.IsLiveAnchorDoubleSuppressed(boundMember, true),
                $"inbound member '{boundMember.VesselName}' must NOT be live-anchor "
                + "suppressed (its delivery mesh draws docking the live station)");

            bool anchorLiveBound =
                RelativeAnchorResolver.WasLiveBoundThisOrLastFrame(anchorRec.RecordingId);
            InGameAssert.IsTrue(anchorLiveBound,
                $"anchor '{anchorRec.VesselName}' should be live-bound after the resolve "
                + "(the delivery member just docked it through the resolver)");
            // The anchor's own live vessel is loaded (checked above) and it is now
            // live-bound, so while it is being docked its duplicate ghost is suppressed.
            InGameAssert.IsTrue(
                GhostPlaybackLogic.IsLiveAnchorDoubleSuppressed(anchorRec, true),
                $"anchor '{anchorRec.VesselName}' duplicate ghost should be suppressed while "
                + "a delivery member is live-binding its loaded launch-matched vessel");

            ParsekLog.Info("TestRunner",
                $"LoopedRelativeMemberDocksWithLiveAnchor PASS: member='{boundMember.VesselName}' "
                + $"anchor='{anchorRec.VesselName}' posDelta={posDelta.ToString("F1", CultureInfo.InvariantCulture)}m "
                + $"angle={angleDeg.ToString("F2", CultureInfo.InvariantCulture)}deg doubles={doublesFound} "
                + $"anchorLiveBound={anchorLiveBound} memberSuppressed=false");
        }

        #endregion
    }
}
