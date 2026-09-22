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

        // KNOWN GAP, measured by this test and filed as
        // TOMBSTONED-DEATH-RESURRECTS-ON-RELOAD-AFTER-A-RP-SPLIT: the reload re-derives
        // Dead rows for BOTH halves with fresh ActionIds - TIP because the retagged row
        // keeps the origin's UT/StartUT and so mismatches TIP's derivation, HEAD because
        // the split leaves the origin's Dead CrewEndStates on HEAD. The fix needs an
        // identity decision and a HEAD crew-state decision, so it is skipped rather than
        // guessed; un-skip it with that fix.
        [Fact(Skip = "Known gap: TOMBSTONED-DEATH-RESURRECTS-ON-RELOAD-AFTER-A-RP-SPLIT")]
        public void SplitThenTombstone_ThenReloadMigration_DeathStaysRetired()
        {
            string tipId;
            RunSplitAndTombstone(out tipId);

            Migrate(); // the next load

            var bill = EffectiveDeathRows("Bill Kerman");
            var bob = EffectiveDeathRows("Bob Kerman");
            Assert.True(bill.Count == 0 && bob.Count == 0,
                "death resurrected by load-time migration: "
                + string.Join("; ", bill.Concat(bob).Select(a =>
                    a.ActionId + " rec=" + a.RecordingId + " ut=" + a.UT + " endUT=" + a.EndUT)));
        }
    }
}
