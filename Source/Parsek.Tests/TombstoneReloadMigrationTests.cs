using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// TOMBSTONE-GUARD-SCREENS-AN-INTERVAL-ACTION-BY-ITS-START, reload half: a
    /// pre-rewind-boarded crew death that the merge retired must stay retired across the
    /// next load, where <c>LedgerOrchestrator.MigrateKerbalAssignments</c> re-derives
    /// every committed recording's KerbalAssignment rows. Drives the split-at-rewind
    /// path (the common shape: crew board at launch on a recording that spans the
    /// rewind point) through split -> tombstone -> migrate over one synthetic ledger.
    /// </summary>
    [Collection("Sequential")]
    public class TombstoneReloadMigrationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public TombstoneReloadMigrationTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            KspStatePatcher.ResetForTesting();
        }

        private static TrajectoryPoint PointAt(double ut)
        {
            return new TrajectoryPoint
            {
                ut = ut, altitude = 50000.0, bodyName = "Kerbin",
                rotation = Quaternion.identity, velocity = Vector3.zero,
            };
        }

        private static Recording BuildCrewedOrigin(string id, string treeId,
            double startUT, double midUT, double endUT)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Kerbal X",
                TreeId = treeId,
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Destroyed,
            };
            rec.Points.Add(PointAt(startUT));
            rec.Points.Add(PointAt(midUT));
            rec.Points.Add(PointAt(endUT));
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = startUT,
                endUT = endUT,
                sampleRateHz = 1f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
                frames = new List<TrajectoryPoint> { PointAt(startUT), PointAt(midUT), PointAt(endUT) },
            });
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "Bill Kerman");
            part.AddValue("crew", "Bob Kerman");
            rec.GhostVisualSnapshot = snapshot;
            rec.CrewEndStates = new Dictionary<string, KerbalEndState>
            {
                ["Bill Kerman"] = KerbalEndState.Dead,
                ["Bob Kerman"] = KerbalEndState.Dead,
            };
            rec.CrewEndStatesResolved = true;
            return rec;
        }

        private static void InstallInTree(Recording origin, string treeId)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = treeId,
                RootRecordingId = origin.RecordingId,
                ActiveRecordingId = origin.RecordingId,
            };
            tree.AddOrReplaceRecording(origin);
            RecordingStore.AddCommittedInternal(origin);
            RecordingStore.AddCommittedTreeForTesting(tree);
        }

        private static void Migrate()
        {
            MethodInfo method = typeof(LedgerOrchestrator).GetMethod(
                "MigrateKerbalAssignments", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method.Invoke(null, null);
            EffectiveState.ResetCachesForTesting();
        }

        private static List<GameAction> EffectiveDeathRows(string kerbal)
        {
            return EffectiveState.ComputeELS()
                .Where(a => a.Type == GameActionType.KerbalAssignment
                    && a.KerbalName == kerbal
                    && a.KerbalEndStateField == KerbalEndState.Dead)
                .ToList();
        }

        private ParsekScenario RunSplitAndTombstone(out string tipId)
        {
            const double rewindUT = 34.0;
            var origin = BuildCrewedOrigin("rec_origin", "tree_r", 8.0, rewindUT, 53.0);
            InstallInTree(origin, "tree_r");

            // The load-time derivation of the original flight's rows (what a real save
            // holds before any rewind): Dead, boarded at 8, died at 53.
            Migrate();
            Assert.Equal(2, EffectiveDeathRows("Bill Kerman").Count + EffectiveDeathRows("Bob Kerman").Count);

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_test",
                TreeId = "tree_r",
                ActiveReFlyRecordingId = "rec_fork",
                OriginChildRecordingId = origin.RecordingId,
                SupersedeTargetId = origin.RecordingId,
                RewindPointUT = rewindUT,
                InvokedUT = rewindUT,
            };
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = marker,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            EffectiveState.ResetCachesForTesting();

            var split = RecordingTreeSplitter.SplitOriginAtRewindUT(marker, scenario);
            Assert.False(split.Skipped);
            tipId = split.TipRecordingId;

            // The merge's tombstone pass over the post-split closure root (TIP).
            SupersedeCommit.CommitTombstones(marker, new List<string> { tipId },
                "rec_fork", rewindUT, "2026-09-22T00:00:00Z", scenario);
            EffectiveState.ResetCachesForTesting();
            return scenario;
        }

        [Fact]
        public void SplitThenTombstone_RetiresThePreRewindBoardedDeaths()
        {
            string tipId;
            var scenario = RunSplitAndTombstone(out tipId);
            Assert.Empty(EffectiveDeathRows("Bill Kerman"));
            Assert.Empty(EffectiveDeathRows("Bob Kerman"));
            Assert.Equal(2, scenario.LedgerTombstones.Count);
        }

        // TOMBSTONED-DEATH-RESURRECTS-ON-RELOAD-AFTER-A-RP-SPLIT (rulings of 2026-09-23):
        // the reload re-derived Dead rows for BOTH halves with fresh ActionIds - TIP
        // because the retagged row keeps the origin's UT/StartUT and so mismatched TIP's
        // derivation (fixed by ActionId inheritance, ruling a1), HEAD because the split
        // left the origin's Dead CrewEndStates on HEAD (fixed by the Recovered handoff).
        // MUTATION NOTE: removing either InheritKerbalAssignmentActionIds or
        // MoveCrewEndStatesToSecondHalf reds this cell.
        [Fact]
        public void SplitThenTombstone_ThenReloadMigration_DeathStaysRetired()
        {
            string tipId;
            var scenario = RunSplitAndTombstone(out tipId);

            Migrate(); // the next load

            var bill = EffectiveDeathRows("Bill Kerman");
            var bob = EffectiveDeathRows("Bob Kerman");
            Assert.True(bill.Count == 0 && bob.Count == 0,
                "death resurrected by load-time migration: "
                + string.Join("; ", bill.Concat(bob).Select(a =>
                    a.ActionId + " rec=" + a.RecordingId + " ut=" + a.UT + " endUT=" + a.EndUT)));

            // No orphan tombstone: every tombstone still names a live ledger row.
            var ids = new HashSet<string>(Ledger.Actions.Select(a => a.ActionId));
            Assert.All(scenario.LedgerTombstones, t => Assert.Contains(t.ActionId, ids));

            // HEAD's crew are handed off Recovered (a finite reservation), TIP keeps the
            // (retired) death.
            Assert.All(Ledger.Actions.Where(a => a.Type == GameActionType.KerbalAssignment
                    && a.RecordingId == "rec_origin"),
                a => Assert.Equal(KerbalEndState.Recovered, a.KerbalEndStateField));
            Assert.All(Ledger.Actions.Where(a => a.Type == GameActionType.KerbalAssignment
                    && a.RecordingId == tipId),
                a => Assert.Equal(KerbalEndState.Dead, a.KerbalEndStateField));
        }

        [Fact]
        public void SplitThenTombstone_ThenTwoReloads_StaysRetiredAndStable()
        {
            string tipId;
            RunSplitAndTombstone(out tipId);
            Migrate();
            var idsAfterFirst = Ledger.Actions.Where(a => a.Type == GameActionType.KerbalAssignment)
                .Select(a => a.ActionId).OrderBy(x => x).ToList();
            Migrate();
            var idsAfterSecond = Ledger.Actions.Where(a => a.Type == GameActionType.KerbalAssignment)
                .Select(a => a.ActionId).OrderBy(x => x).ToList();

            Assert.Equal(idsAfterFirst, idsAfterSecond);
            Assert.Empty(EffectiveDeathRows("Bill Kerman"));
            Assert.Empty(EffectiveDeathRows("Bob Kerman"));
        }

        [Fact]
        public void SplitAtRewind_MovesEndStatesToTip_AndHeadRederivesRecovered()
        {
            string tipId;
            RunSplitAndTombstone(out tipId);

            var head = RecordingStore.CommittedRecordings.Single(r => r.RecordingId == "rec_origin");
            var tip = RecordingStore.CommittedRecordings.Single(r => r.RecordingId == tipId);
            Assert.NotNull(tip.CrewEndStates);
            Assert.Equal(KerbalEndState.Dead, tip.CrewEndStates["Bill Kerman"]);
            Assert.True(tip.CrewEndStatesResolved);

            // HEAD qualifies for the chain handoff: chain segment, no terminal vessel
            // snapshot, no terminal state.
            Assert.False(string.IsNullOrEmpty(head.ChainId));
            Assert.Null(head.VesselSnapshot);
            Assert.Null(head.TerminalStateValue);
            Assert.True(KerbalsModule.ShouldUseGhostOnlyChainHandoffEndState(head));

            Migrate();
            Assert.Equal(KerbalEndState.Recovered, head.CrewEndStates["Bill Kerman"]);
            Assert.Equal(KerbalEndState.Recovered, head.CrewEndStates["Bob Kerman"]);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("moved 2 crew end state(s) from rec_origin"));
        }

        // Mirror: a re-commit of TIP after the merge (the merge tail re-commits recordings
        // already in the ledger) dedups against the retired row by (recording, kerbal), so
        // it cannot file a fresh untombstoned death, before or after the reload.
        [Fact]
        public void SplitThenTombstone_ThenRecommitOfTip_ThenReload_StaysRetired()
        {
            string tipId;
            RunSplitAndTombstone(out tipId);
            LedgerOrchestrator.Initialize();
            bool science = false;
            LedgerOrchestrator.OnRecordingCommitted(tipId, 34.0, 53.0, null, ref science);
            EffectiveState.ResetCachesForTesting();
            Assert.Empty(EffectiveDeathRows("Bill Kerman"));
            Assert.Empty(EffectiveDeathRows("Bob Kerman"));

            Migrate();
            Assert.Empty(EffectiveDeathRows("Bill Kerman"));
            Assert.Empty(EffectiveDeathRows("Bob Kerman"));
        }

        // Mirror: a merge that does NOT split (the origin starts at the rewind point, the
        // RF-12S host shape). The rows stay on the origin, match its derivation, and are
        // not replaced at all, so the retirement survives the reload as it did before.
        [Fact]
        public void NonSplitMerge_TombstoneSurvivesReload_WithoutReplacement()
        {
            const double rewindUT = 8.0;
            var origin = BuildCrewedOrigin("rec_nosplit", "tree_n", 8.0, 30.0, 53.0);
            InstallInTree(origin, "tree_n");
            Migrate();
            var idsBefore = Ledger.Actions.Select(a => a.ActionId).OrderBy(x => x).ToList();

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_n", TreeId = "tree_n", ActiveReFlyRecordingId = "rec_fork",
                OriginChildRecordingId = origin.RecordingId, SupersedeTargetId = origin.RecordingId,
                RewindPointUT = rewindUT, InvokedUT = rewindUT,
            };
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = marker,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            var split = RecordingTreeSplitter.SplitOriginAtRewindUT(marker, scenario);
            Assert.True(split.Skipped);
            SupersedeCommit.CommitTombstones(marker, new List<string> { origin.RecordingId },
                "rec_fork", rewindUT, "2026-09-23T00:00:00Z", scenario);
            EffectiveState.ResetCachesForTesting();
            Assert.Empty(EffectiveDeathRows("Bill Kerman"));

            logLines.Clear();
            Migrate();
            Assert.Equal(idsBefore, Ledger.Actions.Select(a => a.ActionId).OrderBy(x => x).ToList());
            Assert.Empty(EffectiveDeathRows("Bill Kerman"));
            Assert.Empty(EffectiveDeathRows("Bob Kerman"));
            Assert.DoesNotContain(logLines, l => l.Contains("MigrateKerbalAssignments: re-derived recording"));
        }

        // Mirror: the split's transactional rollback restores the ORIGINAL end states on
        // the origin (the deep clone taken before SplitAtUT carries both fields).
        [Fact]
        public void SplitAtRewind_MidSplitThrow_RollbackRestoresOriginEndStates()
        {
            var origin = BuildCrewedOrigin("rec_rb", "tree_rb", 8.0, 34.0, 53.0);
            InstallInTree(origin, "tree_rb");
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_rb", TreeId = "tree_rb", ActiveReFlyRecordingId = "rec_fork",
                OriginChildRecordingId = origin.RecordingId, SupersedeTargetId = origin.RecordingId,
                RewindPointUT = 34.0, InvokedUT = 34.0,
            };
            // A post-rewind milestone makes step 9b fire OnTimelineDataChanged, the
            // headless mid-split throw seam (see RecordingTreeSplitterTests 13b).
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "ms_post", StartUT = 40.0, EndUT = 52.0,
                RecordingId = origin.RecordingId, Committed = true,
                Events = new List<GameStateEvent>(),
            });

            int invocations = 0;
            LedgerOrchestrator.OnTimelineDataChanged = () =>
            {
                invocations++;
                if (invocations == 1)
                    throw new InvalidOperationException("injected mid-split failure");
            };
            try
            {
                Assert.Throws<InvalidOperationException>(
                    () => RecordingTreeSplitter.SplitOriginAtRewindUT(marker, null));
            }
            finally
            {
                LedgerOrchestrator.OnTimelineDataChanged = null;
            }

            var restored = RecordingStore.CommittedRecordings.Single(r => r.RecordingId == "rec_rb");
            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.NotNull(restored.CrewEndStates);
            Assert.Equal(KerbalEndState.Dead, restored.CrewEndStates["Bill Kerman"]);
            Assert.Equal(KerbalEndState.Dead, restored.CrewEndStates["Bob Kerman"]);
            Assert.True(restored.CrewEndStatesResolved);
            Assert.Equal(TerminalState.Destroyed, restored.TerminalStateValue);
        }

        // Mirror: a crewless recording. The split moves the resolved-with-no-crew answer,
        // HEAD re-resolves to no crew, and Migrate files no rows for either half.
        [Fact]
        public void SplitAtRewind_CrewlessRecording_FilesNoRows()
        {
            var origin = BuildCrewedOrigin("rec_crewless", "tree_c", 8.0, 34.0, 53.0);
            origin.GhostVisualSnapshot = new ConfigNode("VESSEL");
            origin.GhostVisualSnapshot.AddNode("PART");
            origin.CrewEndStates = null;
            origin.CrewEndStatesResolved = true;
            InstallInTree(origin, "tree_c");
            Migrate();

            string tipId;
            ReFlyAndTombstone(origin.RecordingId, "tree_c", 34.0, out tipId);
            Migrate();

            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.KerbalAssignment);
            var tip = RecordingStore.CommittedRecordings.Single(r => r.RecordingId == tipId);
            Assert.Null(tip.CrewEndStates);
            Assert.True(tip.CrewEndStatesResolved);
        }

        // ---------------------------------------------------------------------------
        // Ruling (3), 2026-09-23: the optimizer's ordinary environment split.
        // ---------------------------------------------------------------------------

        private static Recording BuildTwoEnvironmentCrewedRecording(string id, string treeId,
            bool endStatesPopulated, bool crewSurvives = false)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Kerbal X",
                TreeId = treeId,
                VesselPersistentId = 42,
                MergeState = MergeState.Immutable,
                TerminalStateValue = crewSurvives ? TerminalState.Recovered : TerminalState.Destroyed,
                RecordingFormatVersion = 0,
            };
            double[] atmoUTs = { 8.0, 14.0, 20.0 };
            double[] exoUTs = { 20.0, 27.0, 34.0, 40.0, 53.0 };
            foreach (double ut in atmoUTs) rec.Points.Add(PointAt(ut));
            for (int i = 1; i < exoUTs.Length; i++) rec.Points.Add(PointAt(exoUTs[i]));
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 8.0, endUT = 20.0, sampleRateHz = 1f,
                minAltitude = float.NaN, maxAltitude = float.NaN,
                frames = atmoUTs.Select(PointAt).ToList(),
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 20.0, endUT = 53.0, sampleRateHz = 1f,
                minAltitude = float.NaN, maxAltitude = float.NaN,
                frames = exoUTs.Select(PointAt).ToList(),
            });
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "Bill Kerman");
            part.AddValue("crew", "Bob Kerman");
            rec.GhostVisualSnapshot = snapshot;
            if (endStatesPopulated)
            {
                KerbalEndState fate = crewSurvives ? KerbalEndState.Recovered : KerbalEndState.Dead;
                rec.CrewEndStates = new Dictionary<string, KerbalEndState>
                {
                    ["Bill Kerman"] = fate,
                    ["Bob Kerman"] = fate,
                };
                rec.CrewEndStatesResolved = true;
            }
            return rec;
        }

        private static ParsekScenario ReFlyAndTombstone(string originId, string treeId, double rewindUT,
            out string tipId)
        {
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_opt",
                TreeId = treeId,
                ActiveReFlyRecordingId = "rec_fork",
                OriginChildRecordingId = originId,
                SupersedeTargetId = originId,
                RewindPointUT = rewindUT,
                InvokedUT = rewindUT,
            };
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = marker,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            EffectiveState.ResetCachesForTesting();
            var split = RecordingTreeSplitter.SplitOriginAtRewindUT(marker, scenario);
            Assert.False(split.Skipped, split.SkipReason);
            tipId = split.TipRecordingId;
            SupersedeCommit.CommitTombstones(marker, new List<string> { tipId },
                "rec_fork", rewindUT, "2026-09-23T00:00:00Z", scenario);
            EffectiveState.ResetCachesForTesting();
            return scenario;
        }

        private static List<GameAction> AllEffectiveDeathRows()
        {
            return EffectiveDeathRows("Bill Kerman").Concat(EffectiveDeathRows("Bob Kerman")).ToList();
        }

        private static string Describe(IEnumerable<GameAction> rows)
        {
            return string.Join("; ", rows.Select(a => a.KerbalName + " rec=" + a.RecordingId
                + " " + a.KerbalEndStateField + " " + a.StartUT + ".." + a.EndUT));
        }

        // The lead, measured: an optimizer split of a recording whose end states were
        // ALREADY populated (e.g. a re-fly provisional whose split was deferred at its
        // merge, or a load-time split of an older recording) used to leave the whole
        // flight's Dead on the first segment. A later re-fly of the second segment then
        // cannot retire that death: the first segment is outside the closure and its
        // endUT precedes the rewind point. With the handoff move, the first segment
        // re-derives Recovered at the next load. This cell covers the order where a load
        // separates the optimizer split from the re-fly.
        [Fact]
        public void OptimizerSplitOfPopulatedRecording_ReloadThenReFlyOfSecondSegment_CrewNotDead()
        {
            var rec = BuildTwoEnvironmentCrewedRecording("rec_full", "tree_o", endStatesPopulated: true);
            InstallInTree(rec, "tree_o");
            Migrate();
            RecordingStore.RunOptimizationPass();
            var list = RecordingStore.CommittedRecordings;
            Assert.Equal(2, list.Count);
            Assert.Null(list[0].CrewEndStates);
            Assert.False(list[0].CrewEndStatesResolved);
            Assert.Equal(KerbalEndState.Dead, list[1].CrewEndStates["Bill Kerman"]);

            Migrate(); // a load between the optimizer split and the re-fly
            string seg2 = list[1].RecordingId;
            string tipId;
            ReFlyAndTombstone(seg2, "tree_o", 34.0, out tipId);
            Assert.True(AllEffectiveDeathRows().Count == 0,
                "dead before reload: " + Describe(AllEffectiveDeathRows()));

            Migrate();
            Assert.True(AllEffectiveDeathRows().Count == 0,
                "dead after reload: " + Describe(AllEffectiveDeathRows()));
        }

        // The fresh-commit order (the common one): MergeDialog.MergeCommit runs the
        // optimization pass BEFORE NotifyLedgerTreeCommitted populates end states, so the
        // first segment is populated only after the split and reaches the handoff rule on
        // its own. Nothing moves; the re-fly of the second segment retires the death and
        // it stays retired across the reload.
        [Fact]
        public void FreshCommitOrder_OptimizerSplitThenDerive_ReFlyOfSecondSegment_CrewNotDead()
        {
            var rec = BuildTwoEnvironmentCrewedRecording("rec_fresh", "tree_f", endStatesPopulated: false);
            InstallInTree(rec, "tree_f");
            RecordingStore.RunOptimizationPass();
            var list = RecordingStore.CommittedRecordings;
            Assert.Equal(2, list.Count);
            Migrate(); // the commit-time derivation, after the split
            Assert.Equal(KerbalEndState.Recovered, list[0].CrewEndStates["Bill Kerman"]);
            Assert.Equal(KerbalEndState.Dead, list[1].CrewEndStates["Bill Kerman"]);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("crew end states not populated on rec_fresh - nothing to move"));

            string tipId;
            ReFlyAndTombstone(list[1].RecordingId, "tree_f", 34.0, out tipId);
            Assert.Empty(AllEffectiveDeathRows());
            Migrate();
            Assert.True(AllEffectiveDeathRows().Count == 0,
                "dead after reload: " + Describe(AllEffectiveDeathRows()));
        }

        // ---------------------------------------------------------------------------
        // OPTIMIZER-SPLIT-LEAVES-KERBAL-ROWS-ON-THE-FIRST-SEGMENT.
        // Trigger 1: the optimizer split of an already-derived recording retagged no
        // ledger rows, so a re-fly of the second segment before any load found no row
        // on it to tombstone. Fixed by the split pass's step-2.9 mirror retag
        // (Ledger.RetagActionsForSplitSecondHalf).
        // Trigger 2: the load-time optimizer split a SUPERSEDED TIP, and the fresh-id
        // half escaped the supersede relation. Fixed by skipping superseded recordings
        // in FindSplitCandidatesForOptimizer.
        // ---------------------------------------------------------------------------

        private static List<string> KerbalAssignmentIds()
        {
            return Ledger.Actions.Where(a => a.Type == GameActionType.KerbalAssignment)
                .Select(a => a.ActionId).OrderBy(x => x, StringComparer.Ordinal).ToList();
        }

        private static void AssertNoOrphanRows()
        {
            var live = new HashSet<string>(RecordingStore.CommittedRecordings.Select(r => r.RecordingId));
            var orphans = Ledger.Actions.Where(a => !string.IsNullOrEmpty(a.RecordingId)
                && !live.Contains(a.RecordingId)).ToList();
            Assert.True(orphans.Count == 0, "rows tagged to a recording no longer committed: "
                + string.Join("; ", orphans.Select(a => a.Type + " " + a.ActionId + " rec=" + a.RecordingId)));
        }

        private static GameAction AddFundsEarning(string recordingId, double ut)
        {
            var row = new GameAction
            {
                Type = GameActionType.FundsEarning,
                UT = ut,
                RecordingId = recordingId,
                FundsAwarded = 100f,
            };
            Ledger.AddAction(row);
            return row;
        }

        // MUTATION NOTE: removing the RetagActionsForSplitSecondHalf call in
        // RunOptimizationSplitPass reds this cell (the Dead row stays on segment 1,
        // outside the re-fly closure).
        [Fact]
        public void OptimizerSplitOfPopulatedRecording_ReFlyBeforeAnyReload_CrewNotDead()
        {
            var rec = BuildTwoEnvironmentCrewedRecording("rec_full", "tree_o", endStatesPopulated: true);
            InstallInTree(rec, "tree_o");
            Migrate();
            RecordingStore.RunOptimizationPass();
            string seg2 = RecordingStore.CommittedRecordings[1].RecordingId;
            string tipId;
            ReFlyAndTombstone(seg2, "tree_o", 34.0, out tipId);
            Assert.True(AllEffectiveDeathRows().Count == 0,
                "dead before reload: " + Describe(AllEffectiveDeathRows()));

            Migrate();
            Assert.True(AllEffectiveDeathRows().Count == 0,
                "dead after reload: " + Describe(AllEffectiveDeathRows()));
            var idsAfterFirst = KerbalAssignmentIds();

            Migrate(); // a second load changes nothing
            Assert.Empty(AllEffectiveDeathRows());
            Assert.Equal(idsAfterFirst, KerbalAssignmentIds());
            AssertNoOrphanRows();
        }

        [Fact]
        public void OptimizerSplit_RetagsRowsByAttributionUT_KeepsActionIds()
        {
            var rec = BuildTwoEnvironmentCrewedRecording("rec_tag", "tree_t", endStatesPopulated: true);
            InstallInTree(rec, "tree_t");
            Migrate();
            var deathIds = Ledger.Actions.Where(a => a.Type == GameActionType.KerbalAssignment)
                .Select(a => a.ActionId).ToList();
            Assert.Equal(2, deathIds.Count);
            var early = AddFundsEarning("rec_tag", 10.0);
            var late = AddFundsEarning("rec_tag", 40.0);
            var atCut = AddFundsEarning("rec_tag", 20.0);

            logLines.Clear();
            RecordingStore.RunOptimizationPass();
            var list = RecordingStore.CommittedRecordings;
            Assert.Equal(2, list.Count);
            string seg1 = list[0].RecordingId;
            string seg2 = list[1].RecordingId;
            Assert.Equal("rec_tag", seg1);

            // Death rows are screened by their death (EndUT 53), so they follow the
            // terminal to segment 2 with their ActionIds unchanged.
            foreach (string id in deathIds)
                Assert.Equal(seg2, Ledger.Actions.Single(a => a.ActionId == id).RecordingId);
            Assert.Equal(seg1, early.RecordingId);
            Assert.Equal(seg2, late.RecordingId);
            Assert.Equal(seg2, atCut.RecordingId); // >= splitUT, same sense as step 2.9
            Assert.Contains(logLines, l => l.Contains("[Ledger]")
                && l.Contains("RetagActionsForSplitSecondHalf") && l.Contains("retagged=4")
                && l.Contains("deathIntervalsByEndUT=2"));
            AssertNoOrphanRows();
        }

        // The paired KerbalDeath reputation penalty (stamped at the recording's end)
        // travels with the death, so a re-fly of segment 2 retires both.
        [Fact]
        public void OptimizerSplit_DeathRepPenaltyFollowsTheDeath_ReFlyRetiresBoth()
        {
            var rec = BuildTwoEnvironmentCrewedRecording("rec_rep", "tree_rp", endStatesPopulated: true);
            InstallInTree(rec, "tree_rp");
            Migrate();
            var penalty = new GameAction
            {
                Type = GameActionType.ReputationPenalty,
                UT = 53.0,
                RecordingId = "rec_rep",
                NominalPenalty = 10f,
                RepPenaltySource = ReputationPenaltySource.KerbalDeath,
            };
            Ledger.AddAction(penalty);

            RecordingStore.RunOptimizationPass();
            string seg2 = RecordingStore.CommittedRecordings[1].RecordingId;
            Assert.Equal(seg2, penalty.RecordingId);

            string tipId;
            ReFlyAndTombstone(seg2, "tree_rp", 34.0, out tipId);
            Assert.Empty(AllEffectiveDeathRows());
            Assert.DoesNotContain(EffectiveState.ComputeELS(), a => a.ActionId == penalty.ActionId);
        }

        // Mirror: alive crew. The non-death row is screened by its boarding UT, so it
        // stays on segment 1 (its id is kept, its content becomes the handoff); segment
        // 2 derives its own row at the next load. Nothing dies, nothing orphans, and a
        // second load is stable.
        [Fact]
        public void OptimizerSplit_AliveCrew_RowStaysOnFirstSegment_TwoReloadsStable()
        {
            var rec = BuildTwoEnvironmentCrewedRecording("rec_alive", "tree_a",
                endStatesPopulated: true, crewSurvives: true);
            InstallInTree(rec, "tree_a");
            Migrate();
            var billBefore = Ledger.Actions.Single(a => a.Type == GameActionType.KerbalAssignment
                && a.KerbalName == "Bill Kerman");
            Assert.Equal(KerbalEndState.Recovered, billBefore.KerbalEndStateField);
            string billId = billBefore.ActionId;

            RecordingStore.RunOptimizationPass();
            var list = RecordingStore.CommittedRecordings;
            Assert.Equal(2, list.Count);
            Assert.Equal("rec_alive", Ledger.Actions.Single(a => a.ActionId == billId).RecordingId);

            Migrate();
            Assert.Empty(AllEffectiveDeathRows());
            Assert.Equal("rec_alive", Ledger.Actions.Single(a => a.ActionId == billId).RecordingId);
            foreach (var seg in list)
            {
                var rows = Ledger.Actions.Where(a => a.Type == GameActionType.KerbalAssignment
                    && a.RecordingId == seg.RecordingId).ToList();
                Assert.Equal(2, rows.Count);
                Assert.All(rows, a => Assert.Equal(KerbalEndState.Recovered, a.KerbalEndStateField));
            }
            var idsAfterFirst = KerbalAssignmentIds();
            Migrate();
            Assert.Equal(idsAfterFirst, KerbalAssignmentIds());
            AssertNoOrphanRows();
        }

        // Mirror: a crewless recording. No crew rows before or after; the generic retag
        // still moves its other rows by UT.
        [Fact]
        public void OptimizerSplit_CrewlessRecording_FilesNoCrewRows_RetagsOtherRows()
        {
            var rec = BuildTwoEnvironmentCrewedRecording("rec_nocrew", "tree_nc", endStatesPopulated: false);
            rec.GhostVisualSnapshot = new ConfigNode("VESSEL");
            rec.GhostVisualSnapshot.AddNode("PART");
            rec.CrewEndStatesResolved = true;
            InstallInTree(rec, "tree_nc");
            Migrate();
            var late = AddFundsEarning("rec_nocrew", 45.0);

            RecordingStore.RunOptimizationPass();
            string seg2 = RecordingStore.CommittedRecordings[1].RecordingId;
            Assert.Equal(seg2, late.RecordingId);
            Migrate();
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.KerbalAssignment);
            AssertNoOrphanRows();
        }

        // Mirror, the merge direction: split, then merge the halves back (the optimizer
        // merge pass absorbs segment 2, which now carries the death rows), then the same
        // pass re-splits. The merge must retag the absorbed recording's rows onto the
        // target, or they are orphaned and the next load's Reconcile prunes them.
        // MUTATION NOTE: restricting the merge retag to an absorbed tree ROOT (the old
        // behavior) reds this cell.
        [Fact]
        public void OptimizerSplit_ThenMergeBack_ThenResplit_RowsFollowAndNothingOrphans()
        {
            var rec = BuildTwoEnvironmentCrewedRecording("rec_rt", "tree_rt", endStatesPopulated: true);
            InstallInTree(rec, "tree_rt");
            Migrate();
            var deathIds = Ledger.Actions.Where(a => a.Type == GameActionType.KerbalAssignment)
                .Select(a => a.ActionId).ToList();
            RecordingStore.RunOptimizationPass();
            Migrate(); // segment 1 derives its Recovered handoff rows
            var list = RecordingStore.CommittedRecordings;
            Assert.Equal(2, list.Count);
            string firstSecondId = list[1].RecordingId;

            // Make the halves mergeable: same phase and body.
            list[1].SegmentPhase = list[0].SegmentPhase;
            list[1].SegmentBodyName = list[0].SegmentBodyName;
            Assert.True(RecordingOptimizer.CanAutoMerge(list[0], list[1]));

            logLines.Clear();
            RecordingStore.RunOptimizationPass();
            Assert.Contains(logLines, l => l.Contains("Optimization pass: merged 1 segment pair(s)"));
            Assert.DoesNotContain(RecordingStore.CommittedRecordings, r => r.RecordingId == firstSecondId);
            AssertNoOrphanRows();
            var tip = RecordingStore.CommittedRecordings.Last();
            foreach (string id in deathIds)
                Assert.Equal(tip.RecordingId, Ledger.Actions.Single(a => a.ActionId == id).RecordingId);

            Migrate();
            AssertNoOrphanRows();
            var dead = AllEffectiveDeathRows();
            Assert.Equal(2, dead.Count);
            Assert.All(dead, a => Assert.Equal(tip.RecordingId, a.RecordingId));
            Assert.Equal(deathIds.OrderBy(x => x, StringComparer.Ordinal),
                dead.Select(a => a.ActionId).OrderBy(x => x, StringComparer.Ordinal));
        }

        // Mirror, a merge that LASTS (no re-split): the target then holds its own
        // Recovered handoff row and the absorbed Dead row for each kerbal. The next
        // load re-derives one Dead row, which must inherit the DEATH's id. The ledger
        // order here happens to put the death first, so the order-independent witness
        // of the same-fate preference is the pure cell below.
        [Fact]
        public void OptimizerSplit_ThenLastingMerge_ReloadKeepsTheDeathId()
        {
            var rec = BuildTwoEnvironmentCrewedRecording("rec_lm", "tree_lm", endStatesPopulated: true);
            InstallInTree(rec, "tree_lm");
            Migrate();
            var deathIds = Ledger.Actions.Where(a => a.Type == GameActionType.KerbalAssignment)
                .Select(a => a.ActionId).OrderBy(x => x, StringComparer.Ordinal).ToList();
            RecordingStore.RunOptimizationPass();
            Migrate();
            var list = RecordingStore.CommittedRecordings;
            Assert.Equal(2, list.Count);

            // Same phase, body and environment: the merge sticks, nothing re-splits.
            list[1].SegmentPhase = list[0].SegmentPhase;
            list[1].SegmentBodyName = list[0].SegmentBodyName;
            for (int i = 0; i < list[1].TrackSections.Count; i++)
            {
                var sec = list[1].TrackSections[i];
                sec.environment = SegmentEnvironment.Atmospheric;
                list[1].TrackSections[i] = sec;
            }
            RecordingStore.RunOptimizationPass();
            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal(4, Ledger.Actions.Count(a => a.Type == GameActionType.KerbalAssignment
                && a.RecordingId == "rec_lm"));

            Migrate();
            AssertNoOrphanRows();
            var dead = AllEffectiveDeathRows();
            Assert.Equal(deathIds, dead.Select(a => a.ActionId).OrderBy(x => x, StringComparer.Ordinal).ToList());
            Assert.Equal(2, Ledger.Actions.Count(a => a.Type == GameActionType.KerbalAssignment));
        }

        [Fact]
        public void InheritKerbalAssignmentActionIds_PrefersTheSameFate_OverLedgerOrder()
        {
            var handoff = new GameAction
            {
                Type = GameActionType.KerbalAssignment, RecordingId = "r", KerbalName = "Bill Kerman",
                KerbalEndStateField = KerbalEndState.Recovered, ActionId = "act_handoff",
            };
            var death = new GameAction
            {
                Type = GameActionType.KerbalAssignment, RecordingId = "r", KerbalName = "Bill Kerman",
                KerbalEndStateField = KerbalEndState.Dead, ActionId = "act_death",
            };
            var want = new GameAction
            {
                Type = GameActionType.KerbalAssignment, RecordingId = "r", KerbalName = "Bill Kerman",
                KerbalEndStateField = KerbalEndState.Dead,
            };
            int fresh;
            int inherited = LedgerOrchestrator.InheritKerbalAssignmentActionIds(
                new List<GameAction> { handoff, death }, new List<GameAction> { want }, out fresh);
            Assert.Equal(1, inherited);
            Assert.Equal(0, fresh);
            Assert.Equal("act_death", want.ActionId);

            // No same-fate partner: the first same-name row in order, as before.
            var wantAboard = new GameAction
            {
                Type = GameActionType.KerbalAssignment, RecordingId = "r", KerbalName = "Bill Kerman",
                KerbalEndStateField = KerbalEndState.Aboard,
            };
            LedgerOrchestrator.InheritKerbalAssignmentActionIds(
                new List<GameAction> { handoff, death }, new List<GameAction> { wantAboard }, out fresh);
            Assert.Equal("act_handoff", wantAboard.ActionId);
        }

        private static ParsekScenario InstallSupersedeScenario(string oldId, string newId)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>
                {
                    new RecordingSupersedeRelation
                    {
                        RelationId = "rsr_test", OldRecordingId = oldId, NewRecordingId = newId, UT = 34.0,
                    },
                },
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            EffectiveState.ResetCachesForTesting();
            return scenario;
        }

        [Fact]
        public void FindSplitCandidates_SkipsSupersededRecording_KeepsTheOther()
        {
            var kept = BuildTwoEnvironmentCrewedRecording("rec_kept", "tree_k", endStatesPopulated: false);
            var gone = BuildTwoEnvironmentCrewedRecording("rec_gone", "tree_g", endStatesPopulated: false);
            var list = new List<Recording> { gone, kept };

            // Without a relation both are candidates.
            var both = RecordingOptimizer.FindSplitCandidatesForOptimizer(list);
            Assert.Equal(new[] { 0, 1 }, both.Select(c => c.Item1).OrderBy(i => i).ToArray());

            InstallSupersedeScenario("rec_gone", "rec_fork");
            logLines.Clear();
            var only = RecordingOptimizer.FindSplitCandidatesForOptimizer(list);
            Assert.Single(only);
            Assert.Equal(1, only[0].Item1);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("skipped 1 superseded recording(s)"));
        }

        // Trigger 2: a RP split's TIP is superseded; the next load's optimization pass
        // must not split it. A fresh-id half would name no supersede relation, so it
        // would re-enter ERS (the retired flight's tail plays again) and, without the
        // retag, carry the death a second reload re-derives untombstoned.
        // MUTATION NOTE: removing the superseded skip reds the ERS / split-count asserts.
        [Fact]
        public void ReFlySplitTip_IsSuperseded_LoadTimeOptimizerDoesNotSplitIt_TwoReloads()
        {
            var origin = BuildTwoEnvironmentCrewedRecording("rec_t2", "tree_t2", endStatesPopulated: true);
            InstallInTree(origin, "tree_t2");
            Migrate();

            // Re-fly with the rewind point inside the atmospheric section: TIP keeps the
            // Atmospheric -> ExoBallistic boundary at 20, a split candidate on its own.
            string tipId;
            var scenario = ReFlyAndTombstone("rec_t2", "tree_t2", 14.0, out tipId);
            scenario.RecordingSupersedes.Add(new RecordingSupersedeRelation
            {
                RelationId = "rsr_t2", OldRecordingId = tipId, NewRecordingId = "rec_fork", UT = 14.0,
            });
            scenario.ActiveReFlySessionMarker = null; // the merge cleared it
            EffectiveState.ResetCachesForTesting();
            Assert.Empty(AllEffectiveDeathRows());
            var tip = RecordingStore.CommittedRecordings.Single(r => r.RecordingId == tipId);
            Assert.True(tip.TrackSections.Count >= 2);

            // Reload 1: migrate, then the load-time optimization pass.
            Migrate();
            logLines.Clear();
            RecordingStore.RunOptimizationPass();
            EffectiveState.ResetCachesForTesting();
            Assert.Equal(2, RecordingStore.CommittedRecordings.Count); // HEAD + TIP, TIP not split
            Assert.Contains(logLines, l => l.Contains("skipped 1 superseded recording(s)"));
            Assert.DoesNotContain(EffectiveState.ComputeERS(), r => r.RecordingId != "rec_t2");
            Assert.Empty(AllEffectiveDeathRows());

            // Reload 2.
            Migrate();
            Assert.Empty(AllEffectiveDeathRows());
            var ids = new HashSet<string>(Ledger.Actions.Select(a => a.ActionId));
            Assert.All(scenario.LedgerTombstones, t => Assert.Contains(t.ActionId, ids));
            AssertNoOrphanRows();
        }
    }
}
