using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// QUICKLOAD-REPUTATION-WALK-TWICE-LIVE (QL-2 2026-10-22_1146, QL-2b 2026-10-22_1153).
    /// A live-pool reputation seed read while a recorded flight was still uncommitted
    /// already holds that flight's awards; the flight's commit then filed them as rows
    /// against a PreExisting seed and the walk counted them twice (QL-2: seed 2.0 plus two
    /// +1 milestones walked 4.0 against a live 2.0). The seed now names the uncommitted
    /// recordings and the read's UT, and the commit stamps their earlier rows inside.
    /// </summary>
    [Collection("Sequential")]
    public class ReputationSeedUncommittedRecordingTests : IDisposable
    {
        private const string RecA = "rec-ql2-flea";
        private const string RecB = "rec-later-flight";
        private const double CaptureUT = 352.5;

        private readonly List<string> logLines = new List<string>();

        public ReputationSeedUncommittedRecordingTests()
        {
            RecalculationEngine.ClearModules();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            CurrencyScenarioReadiness.ResetForTesting();
            LedgerOrchestrator.ExpectedCurrencySingletonsForTesting = () => CurrencySingletons.All;
            LedgerOrchestrator.SetResourceTrackingAvailabilityForTesting(true, true, true);
            LedgerOrchestrator.NowUtProviderForTesting = () => CaptureUT;
        }

        public void Dispose()
        {
            LedgerOrchestrator.SetResourceTrackingAvailabilityForTesting(null, null, null);
            RecalculationEngine.ClearModules();
            CurrencyScenarioReadiness.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------

        /// <summary>The pools DeferredSeed read after QL-2's F9: the flight's +2 is in.</summary>
        private static LedgerOrchestrator.CurrencyPoolProbe LivePoolAfterTheFlightsAwards()
        {
            return new LedgerOrchestrator.CurrencyPoolProbe
            {
                FundingPresent = true,
                FundingLoaded = true,
                Funds = 530400.0,
                ScienceSingletonPresent = true,
                ScienceSingletonLoaded = true,
                Science = 100f,
                ReputationSingletonPresent = true,
                ReputationValue = 2f
            };
        }

        private static void AddMilestone(string id, double ut, string recordingId, float rep)
        {
            var e = new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.MilestoneAchieved,
                key = id,
                detail = GameStateRecorder.BuildMilestoneDetail(14400.0, rep, 0.0),
                recordingId = recordingId,
            };
            GameStateStore.AddEvent(ref e);
        }

        private static GameAction Seed()
        {
            return Ledger.Actions.SingleOrDefault(a => a.Type == GameActionType.ReputationInitial);
        }

        private static GameAction SeedWith(double captureUT, params string[] ids)
        {
            return new GameAction
            {
                Type = GameActionType.ReputationInitial,
                InitialReputation = 2f,
                SeedCaptureUT = captureUT,
                SeedPendingRecordingIds = new List<string>(ids),
            };
        }

        private static void Commit(string recordingId, double startUT, double endUT)
        {
            bool science = false;
            LedgerOrchestrator.OnRecordingCommitted(recordingId, startUT, endUT, null, ref science);
        }

        /// <summary>The QL-2 flight: two +1 record milestones on the ascent, then the F9's
        /// DeferredSeed reads the live 2.0 while the flight is still uncommitted.</summary>
        private void SeedWhileTheFlightIsUncommitted()
        {
            AddMilestone("RecordsSpeed", 11.6, RecA, 1f);
            AddMilestone("RecordsAltitude", 15.0, RecA, 1f);
            LedgerOrchestrator.UncommittedRecordingIdsForTesting = () => new List<string> { RecA };
            LedgerOrchestrator.CurrencyPoolProbeForTesting = LivePoolAfterTheFlightsAwards;
            LedgerOrchestrator.RecalculateAndPatch();
            LedgerOrchestrator.UncommittedRecordingIdsForTesting = () => new List<string>();
        }

        // ------------------------------------------------------------------
        // The pure predicate
        // ------------------------------------------------------------------

        [Fact]
        public void IsRecordedBeforeSeedCapture_ListedRecordingBeforeTheRead_IsInside()
        {
            Assert.True(ReputationSeedMembership.IsRecordedBeforeSeedCapture(
                SeedWith(CaptureUT, RecA), RecA, 11.6));
        }

        [Theory]
        [InlineData(CaptureUT)]       // same instant: not provably before, so applied
        [InlineData(360.0)]           // the flight went on after the read
        public void IsRecordedBeforeSeedCapture_AtOrAfterTheRead_IsOutside(double rowUT)
        {
            Assert.False(ReputationSeedMembership.IsRecordedBeforeSeedCapture(
                SeedWith(CaptureUT, RecA), RecA, rowUT));
        }

        [Fact]
        public void IsRecordedBeforeSeedCapture_UnlistedRecording_IsOutside_EvenWithAnEarlierUT()
        {
            // A re-fly or any later flight records into a new recording: its rows apply
            // although their UT can be smaller than the read's.
            Assert.False(ReputationSeedMembership.IsRecordedBeforeSeedCapture(
                SeedWith(CaptureUT, RecA), RecB, 20.0));
        }

        [Fact]
        public void IsRecordedBeforeSeedCapture_EveryUnknown_IsOutside()
        {
            Assert.False(ReputationSeedMembership.IsRecordedBeforeSeedCapture(null, RecA, 11.6));
            Assert.False(ReputationSeedMembership.IsRecordedBeforeSeedCapture(
                SeedWith(CaptureUT, RecA), null, 11.6));
            Assert.False(ReputationSeedMembership.IsRecordedBeforeSeedCapture(
                SeedWith(double.NaN, RecA), RecA, 11.6));
            Assert.False(ReputationSeedMembership.IsRecordedBeforeSeedCapture(
                SeedWith(CaptureUT, RecA), RecA, double.NaN));
            Assert.False(ReputationSeedMembership.IsRecordedBeforeSeedCapture(
                new GameAction { Type = GameActionType.ReputationInitial, InitialReputation = 2f },
                RecA, 11.6));
            var notASeed = SeedWith(CaptureUT, RecA);
            notASeed.Type = GameActionType.FundsInitial;
            Assert.False(ReputationSeedMembership.IsRecordedBeforeSeedCapture(notASeed, RecA, 11.6));
        }

        // ------------------------------------------------------------------
        // Serialization
        // ------------------------------------------------------------------

        [Fact]
        public void SeedFields_RoundTrip_AndAnOldSeedReadsAsNone()
        {
            var parent = new ConfigNode("LEDGER");
            SeedWith(CaptureUT, RecA, RecB).SerializeInto(parent);
            var node = parent.GetNodes("GAME_ACTION")[0];
            Assert.Equal(2, node.GetValues(GameAction.SeedPendingRecordingKey).Length);
            var read = GameAction.DeserializeFrom(node);
            Assert.Equal(CaptureUT, read.SeedCaptureUT);
            Assert.Equal(new[] { RecA, RecB }, read.SeedPendingRecordingIds);

            // A seed with nothing to record writes neither key, and reads back as none.
            var plainParent = new ConfigNode("LEDGER");
            new GameAction { Type = GameActionType.ReputationInitial, InitialReputation = 2f }
                .SerializeInto(plainParent);
            var plainNode = plainParent.GetNodes("GAME_ACTION")[0];
            Assert.Null(plainNode.GetValue(GameAction.SeedCaptureUTKey));
            Assert.Null(plainNode.GetValue(GameAction.SeedPendingRecordingKey));
            var plain = GameAction.DeserializeFrom(plainNode);
            Assert.Null(plain.SeedPendingRecordingIds);
            Assert.True(double.IsNaN(plain.SeedCaptureUT));
        }

        // ------------------------------------------------------------------
        // The QL-2 replay
        // ------------------------------------------------------------------

        [Fact]
        public void LivePoolSeed_RecordsTheUncommittedRecordingsAndTheReadUT()
        {
            SeedWhileTheFlightIsUncommitted();

            var seed = Seed();
            Assert.NotNull(seed);
            Assert.Equal(2f, seed.InitialReputation);
            Assert.Equal(new[] { RecA }, seed.SeedPendingRecordingIds);
            Assert.Equal(CaptureUT, seed.SeedCaptureUT);
            Assert.Contains(logLines, l => l.Contains("[Ledger]")
                && l.Contains("Seeded initial reputation: amount=2")
                && l.Contains("uncommittedRecordings=1 captureUT=352.5"));
        }

        [Fact]
        public void QL2_CommitAfterTheSeed_WalksToTheLivePool_NotTwiceIt()
        {
            SeedWhileTheFlightIsUncommitted();
            Commit(RecA, 11.0, 350.6);

            // Before the fix: 2 (seed) + 1 + 1 = 4 against a live 2.
            Assert.Equal(2.0, (double)LedgerOrchestrator.Reputation.GetRunningRep(), 3);
            var milestones = Ledger.Actions
                .Where(a => a.Type == GameActionType.MilestoneAchievement && a.RecordingId == RecA)
                .ToList();
            Assert.Equal(2, milestones.Count);
            Assert.All(milestones, m => Assert.True(m.InsideReputationSeed));
            // The funds leg of the same rows still applies: only reputation is suppressed.
            Assert.Equal(2, milestones.Count(m => m.MilestoneFundsAwarded == 14400f));
            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("Reputation seed stamp: 2 row(s) stamped insideRepSeed=True")
                && l.Contains("recorded before the live-pool seed was read at UT 352.5"));
        }

        [Fact]
        public void SameRecording_AnAwardAfterTheRead_StillCounts()
        {
            SeedWhileTheFlightIsUncommitted();
            // The flight went on after the read and earned another +1 (owned by its tag
            // past the trajectory window).
            AddMilestone("Kerbin/Landing", 360.0, RecA, 1f);
            Commit(RecA, 11.0, 350.6);

            Assert.Equal(3.0, (double)LedgerOrchestrator.Reputation.GetRunningRep(), 3);
            var landing = Ledger.Actions.Single(a =>
                a.Type == GameActionType.MilestoneAchievement && a.MilestoneId == "Kerbin/Landing");
            Assert.False(landing.InsideReputationSeed);
            Assert.Contains(logLines, l => l.Contains("1 later row(s) stay outside"));
        }

        [Fact]
        public void AnotherRecording_WithAnEarlierUT_StillCounts()
        {
            // The mirror direction: a recording that did not exist at the read (a later
            // flight, a re-fly from an earlier rewind point) keeps every award it earns,
            // whatever its UT.
            SeedWhileTheFlightIsUncommitted();
            Commit(RecA, 11.0, 350.6);
            AddMilestone("RecordsDistance", 20.0, RecB, 1f);
            Commit(RecB, 18.0, 40.0);

            Assert.Equal(3.0, (double)LedgerOrchestrator.Reputation.GetRunningRep(), 3);
            var later = Ledger.Actions.Single(a =>
                a.Type == GameActionType.MilestoneAchievement && a.MilestoneId == "RecordsDistance");
            Assert.False(later.InsideReputationSeed);
        }

        [Fact]
        public void LivePoolSeed_WithNothingUncommitted_RecordsNothing_AndAppliesEveryRow()
        {
            // The pre-fix shape: a seed read with no flight in progress names nothing, and a
            // flight committed after it applies every award on top.
            LedgerOrchestrator.UncommittedRecordingIdsForTesting = () => new List<string>();
            LedgerOrchestrator.CurrencyPoolProbeForTesting = LivePoolAfterTheFlightsAwards;
            LedgerOrchestrator.RecalculateAndPatch();
            Assert.Null(Seed().SeedPendingRecordingIds);

            AddMilestone("RecordsSpeed", 400.0, RecA, 1f);
            Commit(RecA, 390.0, 420.0);
            Assert.Equal(3.0, (double)LedgerOrchestrator.Reputation.GetRunningRep(), 3);
        }

        [Fact]
        public void LiveRead_NamesThePendingTreesRecordings()
        {
            // The quickload shape: the F5 save's flight is restored into the pending slot.
            var tree = new RecordingTree { Id = "tree-ql2", TreeName = "Jumping Flea" };
            tree.Recordings[RecA] = new Recording { RecordingId = RecA, VesselName = "Jumping Flea" };
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);

            AddMilestone("RecordsSpeed", 11.6, RecA, 1f);
            AddMilestone("RecordsAltitude", 15.0, RecA, 1f);
            LedgerOrchestrator.CurrencyPoolProbeForTesting = LivePoolAfterTheFlightsAwards;
            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Equal(new[] { RecA }, Seed().SeedPendingRecordingIds);
        }
    }
}
