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
            bool endStatesPopulated)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Kerbal X",
                TreeId = treeId,
                VesselPersistentId = 42,
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Destroyed,
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
                rec.CrewEndStates = new Dictionary<string, KerbalEndState>
                {
                    ["Bill Kerman"] = KerbalEndState.Dead,
                    ["Bob Kerman"] = KerbalEndState.Dead,
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

        // KNOWN GAP, filed as OPTIMIZER-SPLIT-LEAVES-KERBAL-ROWS-ON-THE-FIRST-SEGMENT:
        // when NO load separates the optimizer split from the re-fly, the ledger rows the
        // commit filed for the whole flight are still tagged to the first segment (the
        // optimizer split retags no ledger rows, and the load runs MigrateKerbalAssignments
        // BEFORE the optimization pass). The re-fly of the second segment finds no row to
        // retag or tombstone, the first segment's whole-flight Dead row stays live, and
        // after the reload the TIP derives a fresh untombstoned Dead. Needs a design
        // decision (retag at the optimizer split, or re-derive after it), so it is skipped
        // rather than guessed.
        [Fact(Skip = "Known gap: OPTIMIZER-SPLIT-LEAVES-KERBAL-ROWS-ON-THE-FIRST-SEGMENT")]
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
        }
    }
}
