using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// RETAG-ON-SPLIT-MISSES-LATER-ROWS. Both commit paths run the optimizer pass BEFORE
    /// <see cref="LedgerOrchestrator.NotifyLedgerTreeCommitted"/>, so when a fresh
    /// flight is split the ledger still holds none of its event rows: the split retag
    /// (<see cref="Ledger.RetagActionsForSplitSecondHalf"/>) moved nothing, and every
    /// captured event and pending science subject still named the pre-split id, which
    /// the FIRST segment keeps. The converter treats that tag as ownership, so the whole
    /// flight's rows landed on the first segment: a milestone earned after the cut sat
    /// on a segment a Re-Fly of a later segment carves out of its tombstone set, a
    /// transmitted subject's row took the first segment's END as its UT, and the
    /// VesselLoss reputation event never reached the segment that carries the deaths.
    /// The split now partitions the event store and the pending science list at the
    /// cut too, under the same &gt;= sense as the ledger retag.
    /// </summary>
    [Collection("Sequential")]
    public class OptimizerSplitCommitAttributionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorStoreSuppress;

        public OptimizerSplitCommitAttributionTests()
        {
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;

            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
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
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            KspStatePatcher.ResetForTesting();
        }

        private const string TreeId = "tree_split_attr";
        private const double CutUT = 20.0;
        private const double RewindUT = 34.0;

        private static TrajectoryPoint PointAt(double ut)
        {
            return new TrajectoryPoint
            {
                ut = ut, altitude = 50000.0, bodyName = "Kerbin",
                rotation = Quaternion.identity, velocity = Vector3.zero,
            };
        }

        /// <summary>
        /// A crewed flight the optimizer splits once, at UT 20 (Atmospheric 8-20, then
        /// ExoBallistic 20-53, both past the 5 s floor), ending Destroyed with both crew
        /// dead: the shape of TombstoneReloadMigrationTests' two-environment fixture.
        /// </summary>
        private static Recording BuildSplittableCrashedFlight(string id)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Kerbal X",
                TreeId = TreeId,
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
                startUT = 8.0, endUT = CutUT, sampleRateHz = 1f,
                minAltitude = float.NaN, maxAltitude = float.NaN,
                frames = atmoUTs.Select(PointAt).ToList(),
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = CutUT, endUT = 53.0, sampleRateHz = 1f,
                minAltitude = float.NaN, maxAltitude = float.NaN,
                frames = exoUTs.Select(PointAt).ToList(),
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

        private static RecordingTree InstallInTree(Recording rec)
        {
            var tree = new RecordingTree
            {
                Id = TreeId,
                TreeName = TreeId,
                RootRecordingId = rec.RecordingId,
                ActiveRecordingId = rec.RecordingId,
            };
            tree.AddOrReplaceRecording(rec);
            RecordingStore.AddCommittedInternal(rec);
            RecordingStore.AddCommittedTreeForTesting(tree);
            return tree;
        }

        private static void AddEvent(GameStateEventType type, string key, double ut,
            string recordingId, string detail = "", double valueBefore = 0, double valueAfter = 0)
        {
            var e = new GameStateEvent
            {
                ut = ut,
                eventType = type,
                key = key,
                detail = detail,
                recordingId = recordingId ?? "",
                valueBefore = valueBefore,
                valueAfter = valueAfter,
            };
            GameStateStore.AddEvent(ref e);
        }

        private static void AddMilestone(string key, double ut, string recordingId)
        {
            AddEvent(GameStateEventType.MilestoneAchieved, key, ut, recordingId,
                GameStateRecorder.BuildMilestoneDetail(1000.0, 1.0f, 0.0));
        }

        private static void AddPendingScience(string subjectId, double captureUT, string recordingId)
        {
            GameStateRecorder.PendingScienceSubjects.Add(new PendingScienceSubject
            {
                subjectId = subjectId,
                science = 3f,
                subjectMaxValue = 30f,
                captureUT = captureUT,
                reasonKey = "ScienceTransmission",
                recordingId = recordingId,
                scienceGainMultiplier = 1f,
            });
        }

        private static string TagOf(string key)
        {
            return GameStateStore.Events.Single(e => e.key == key).recordingId;
        }

        private static string PendingTagOf(string subjectId)
        {
            return GameStateRecorder.PendingScienceSubjects.Single(s => s.subjectId == subjectId).recordingId;
        }

        [Fact]
        public void OptimizerSplit_PartitionsStoreEventsAndPendingScienceAtTheCut()
        {
            var rec = BuildSplittableCrashedFlight("rec_ev");
            InstallInTree(rec);
            AddMilestone("RecordsSpeed", 12.0, "rec_ev");
            AddMilestone("FirstLaunch", CutUT, "rec_ev");          // at the cut: >= moves
            AddMilestone("ReachedSpace", 40.0, "rec_ev");
            AddMilestone("OtherFlight", 40.0, "rec_other");        // another recording's tag
            AddMilestone("Untagged", 40.0, "");                    // career-level capture
            AddPendingScience("crewReport@KerbinSrfLanded", 45.0, "rec_ev");
            AddPendingScience("mysteryGoo@KerbinFlying", 15.0, "rec_ev");
            AddPendingScience("other@Kerbin", 45.0, "rec_other");

            logLines.Clear();
            RecordingStore.RunOptimizationPass();

            var list = RecordingStore.CommittedRecordings;
            Assert.Equal(2, list.Count);
            string seg1 = list[0].RecordingId;
            string seg2 = list[1].RecordingId;
            Assert.Equal("rec_ev", seg1);

            Assert.Equal(seg1, TagOf("RecordsSpeed"));
            Assert.Equal(seg2, TagOf("FirstLaunch"));
            Assert.Equal(seg2, TagOf("ReachedSpace"));
            Assert.Equal("rec_other", TagOf("OtherFlight"));
            Assert.Equal("", TagOf("Untagged"));
            Assert.Equal(seg2, PendingTagOf("crewReport@KerbinSrfLanded"));
            Assert.Equal(seg1, PendingTagOf("mysteryGoo@KerbinFlying"));
            Assert.Equal("rec_other", PendingTagOf("other@Kerbin"));

            Assert.Contains(logLines, l => l.Contains("[GameStateStore]")
                && l.Contains("RetagEventsForSplitSecondHalf") && l.Contains("retagged=2"));
            Assert.Contains(logLines, l => l.Contains("[GameStateRecorder]")
                && l.Contains("RetagPendingScienceForSplitSecondHalf") && l.Contains("retagged=1"));
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]")
                && l.Contains("Optimization split: retagged 2 event(s) and 1 pending science subject(s)"));
        }

        // The whole commit order of a fresh flight: optimizer split, then the ledger
        // notify, then a Re-Fly of the SECOND segment from a rewind point inside it.
        // MUTATION NOTE: removing the event / pending-science retag in
        // RecordingStore.RetagLedgerActionsAfterOptimizationSplit reds every assertion
        // after the split: the milestone and the science row land on seg1 (the science
        // at seg1's end, UT 20), no KerbalDeath penalty row is filed at all, and the
        // Re-Fly then keeps the milestone and the science it superseded.
        [Fact]
        public void FreshFlightSplitThenCommit_RowsLandOnTheSegmentCoveringTheirUT_ReFlyRetiresThem()
        {
            var rec = BuildSplittableCrashedFlight("rec_fresh");
            var tree = InstallInTree(rec);
            AddMilestone("RecordsSpeed", 12.0, "rec_fresh");
            AddMilestone("ReachedSpace", 40.0, "rec_fresh");
            AddEvent(GameStateEventType.ReputationChanged, KerbalDeathRepPenalty.VesselLossEventKey,
                53.0, "rec_fresh", valueBefore: 20.0, valueAfter: 10.0);
            AddPendingScience("crewReport@KerbinSrfLanded", 45.0, "rec_fresh");

            RecordingStore.RunOptimizationPass();
            var list = RecordingStore.CommittedRecordings;
            Assert.Equal(2, list.Count);
            string seg1 = list[0].RecordingId;
            string seg2 = list[1].RecordingId;

            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);
            EffectiveState.ResetCachesForTesting();

            var early = Ledger.Actions.Single(a => a.Type == GameActionType.MilestoneAchievement
                && a.MilestoneId == "RecordsSpeed");
            var late = Ledger.Actions.Single(a => a.Type == GameActionType.MilestoneAchievement
                && a.MilestoneId == "ReachedSpace");
            var science = Ledger.Actions.Single(a => a.Type == GameActionType.ScienceEarning);
            var penalty = Ledger.Actions.SingleOrDefault(a => a.Type == GameActionType.ReputationPenalty
                && a.RepPenaltySource == ReputationPenaltySource.KerbalDeath);

            Assert.Equal(seg1, early.RecordingId);
            Assert.Equal(seg2, late.RecordingId);
            Assert.Equal(seg2, science.RecordingId);
            Assert.Equal(list[1].EndUT, science.UT);
            Assert.True(penalty != null,
                "no KerbalDeath penalty row: the VesselLoss event never reached the segment with the deaths");
            Assert.Equal(seg2, penalty.RecordingId);
            Assert.Equal(10f, penalty.NominalPenalty);

            // Re-Fly of seg2 from a rewind point inside it. seg1 is a chain sibling of the
            // split's TIP that ends before the rewind, so the merge carves it out of the
            // tombstone set (SupersedeCommit.IsPreRewindCarveOut): a row left on seg1 is
            // kept whatever its UT.
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_split_attr",
                TreeId = TreeId,
                ActiveReFlyRecordingId = "rec_fork",
                OriginChildRecordingId = seg2,
                SupersedeTargetId = seg2,
                RewindPointUT = RewindUT,
                InvokedUT = RewindUT,
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
            string tipId = split.TipRecordingId;
            SupersedeCommit.PreRewindCarveOutReason reason;
            Assert.True(SupersedeCommit.IsPreRewindCarveOut(
                RecordingStore.CommittedRecordings.Single(r => r.RecordingId == seg1), marker, out reason));
            SupersedeCommit.CommitTombstones(marker, new List<string> { tipId },
                "rec_fork", RewindUT, "2026-10-07T00:00:00Z", scenario);
            EffectiveState.ResetCachesForTesting();

            var els = EffectiveState.ComputeELS();
            Assert.Contains(els, a => a.ActionId == early.ActionId);
            Assert.DoesNotContain(els, a => a.ActionId == late.ActionId);
            Assert.DoesNotContain(els, a => a.ActionId == science.ActionId);
            Assert.DoesNotContain(els, a => a.ActionId == penalty.ActionId);
        }

        // Guard: a split with nothing captured past the cut moves nothing and says so.
        [Fact]
        public void OptimizerSplit_NothingPastTheCut_RetagsNothing()
        {
            var rec = BuildSplittableCrashedFlight("rec_quiet");
            InstallInTree(rec);
            AddMilestone("RecordsSpeed", 12.0, "rec_quiet");
            AddPendingScience("mysteryGoo@KerbinFlying", 15.0, "rec_quiet");

            logLines.Clear();
            RecordingStore.RunOptimizationPass();

            Assert.Equal("rec_quiet", TagOf("RecordsSpeed"));
            Assert.Equal("rec_quiet", PendingTagOf("mysteryGoo@KerbinFlying"));
            Assert.Contains(logLines, l => l.Contains("RetagEventsForSplitSecondHalf") && l.Contains("retagged=0"));
            Assert.Contains(logLines, l => l.Contains("RetagPendingScienceForSplitSecondHalf") && l.Contains("retagged=0"));
            Assert.DoesNotContain(logLines, l => l.Contains("Optimization split: retagged")
                && l.Contains("event(s)"));
        }

        [Fact]
        public void RetagEventsForSplitSecondHalf_GuardsDegenerateInputs()
        {
            AddMilestone("A", 30.0, "rec_a");
            Assert.Equal(0, GameStateStore.RetagEventsForSplitSecondHalf(null, "rec_b", 10.0));
            Assert.Equal(0, GameStateStore.RetagEventsForSplitSecondHalf("rec_a", "", 10.0));
            Assert.Equal(0, GameStateStore.RetagEventsForSplitSecondHalf("rec_a", "rec_a", 10.0));
            Assert.Equal(0, GameStateStore.RetagEventsForSplitSecondHalf("rec_a", "rec_b", double.NaN));
            Assert.Equal("rec_a", TagOf("A"));
            Assert.Equal(1, GameStateStore.RetagEventsForSplitSecondHalf("rec_a", "rec_b", 30.0));
            Assert.Equal("rec_b", TagOf("A"));
        }

        [Fact]
        public void RetagPendingScienceForSplitSecondHalf_GuardsDegenerateInputs()
        {
            AddPendingScience("s1", 30.0, "rec_a");
            Assert.Equal(0, GameStateRecorder.RetagPendingScienceForSplitSecondHalf(null, "rec_b", 10.0));
            Assert.Equal(0, GameStateRecorder.RetagPendingScienceForSplitSecondHalf("rec_a", "rec_a", 10.0));
            Assert.Equal(0, GameStateRecorder.RetagPendingScienceForSplitSecondHalf("rec_a", "rec_b", double.PositiveInfinity));
            Assert.Equal(0, GameStateRecorder.RetagPendingScienceForSplitSecondHalf("rec_a", "rec_b", 30.5));
            Assert.Equal("rec_a", PendingTagOf("s1"));
            Assert.Equal(1, GameStateRecorder.RetagPendingScienceForSplitSecondHalf("rec_a", "rec_b", 30.0));
            Assert.Equal("rec_b", PendingTagOf("s1"));
        }
    }
}
