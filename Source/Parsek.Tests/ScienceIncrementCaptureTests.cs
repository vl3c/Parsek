using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// SCIENCE-SUBJECT-RUNNING-TOTAL-OVER-CREDIT with KSP-SETTINGS-AUDIT S1: a science
    /// capture records the increment ONE stock submission added (pre-multiplier subject
    /// units) and freezes the <c>ScienceGainMultiplier</c> beside it, so repeated collections
    /// of one subject credit the ledger exactly what stock credited, at any multiplier. Each
    /// scenario models stock's <c>SubmitScienceData</c> (add the increment to
    /// <c>subject.science</c>, then fire <c>amount = increment * ScienceGainMultiplier</c>),
    /// captures it through the recorder's headless core
    /// (<see cref="GameStateRecorder.CaptureScienceSubject"/>), drives the real ledger commit
    /// paths, and compares the walk against what stock added. The multiplier-1 capture cells
    /// live in <c>DeployedScienceLedgerTests</c>.
    /// </summary>
    [Collection("Sequential")]
    public class ScienceIncrementCaptureTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ScienceIncrementCaptureTests()
        {
            RecalculationEngine.ClearModules();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateRecorder.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Stock model
        // ================================================================

        /// <summary>
        /// One stock subject plus the R&amp;D pool it has fed. <see cref="Submit"/> mirrors
        /// decompiled <c>ResearchAndDevelopment.SubmitScienceData</c>: a diminishing-returns
        /// value against the CURRENT subject total, added to <c>subject.science</c>
        /// pre-multiplier, then multiplied into the pool and fired as the event amount.
        /// </summary>
        private sealed class StockSubject
        {
            public string Id;
            public float Cap;
            public float Science;
            public double Pool;

            public float Submit(float fraction, float multiplier)
            {
                float increment = (Cap - Science) * fraction;
                Science += increment;
                float amount = increment * multiplier;
                Pool += amount;
                return amount;
            }
        }

        /// <summary>
        /// A capture inside a live recording: the core parks the subject in
        /// <c>PendingScienceSubjects</c>, which this takes back out for the commit.
        /// </summary>
        private static PendingScienceSubject CaptureInFlight(
            StockSubject s, float amount, float multiplier, double ut, string recordingId)
        {
            GameStateRecorder.TagResolverForTesting = () => recordingId;
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;
            GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => true;
            new GameStateRecorder().CaptureScienceSubject(
                amount, s.Id, s.Science, s.Cap, multiplier, ut, null, null);
            var pending = Assert.Single(GameStateRecorder.PendingScienceSubjects);
            GameStateRecorder.PendingScienceSubjects.Clear();
            return pending;
        }

        /// <summary>A capture at the KSC: the core writes the row straight to the ledger.</summary>
        private static void CaptureAtKsc(StockSubject s, float amount, float multiplier, double ut)
        {
            GameStateRecorder.TagResolverForTesting = () => "";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => false;
            GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => false;
            new GameStateRecorder().CaptureScienceSubject(
                amount, s.Id, s.Science, s.Cap, multiplier, ut, null, null);
            Assert.Empty(GameStateRecorder.PendingScienceSubjects);
        }

        private static void CommitRecording(
            string recordingId, double startUT, double endUT, params PendingScienceSubject[] subjects)
        {
            bool scienceAdded = false;
            LedgerOrchestrator.OnRecordingCommitted(
                recordingId, startUT, endUT, subjects.ToList(), ref scienceAdded);
            Assert.True(scienceAdded);
        }

        private void AssertLedgerMatchesStock(StockSubject s)
        {
            LedgerOrchestrator.RecalculateAndPatch();

            // Pool: what stock's AddScience received, summed over submissions.
            Assert.Equal(s.Pool, LedgerOrchestrator.Science.GetTotalEffectiveEarnings(), 3);
            // Subject: stock's final subject.science (pre-multiplier).
            Assert.Equal((double)s.Science, LedgerOrchestrator.Science.GetSubjectCredited(s.Id), 3);
            // The committed-science cache fallback agrees with the walk.
            Assert.True(GameStateStore.TryGetCommittedSubjectScience(s.Id, out float cached));
            Assert.Equal((double)s.Science, (double)cached, 3);
        }

        private static StockSubject Goo()
        {
            return new StockSubject { Id = "mysteryGoo@MunInSpaceHigh", Cap = 24f };
        }

        // ================================================================
        // Scenario cells
        // ================================================================

        [Theory]
        [InlineData(1f)]
        [InlineData(2f)]
        [InlineData(0.6f)]
        public void SameSubjectTwiceInOneRecording_CreditsWhatStockAdded(float multiplier)
        {
            var s = Goo();
            float a1 = s.Submit(0.3f, multiplier);
            float a2 = s.Submit(0.3f, multiplier);

            // Both callbacks see the final running total here, so the clamp is exercised
            // without ever binding on a genuine increment.
            var p1 = CaptureInFlight(s, a1, multiplier, 120.0, "rec-a");
            var p2 = CaptureInFlight(s, a2, multiplier, 150.0, "rec-a");
            Assert.Equal((double)multiplier, (double)p1.scienceGainMultiplier, 4);
            CommitRecording("rec-a", 100.0, 200.0, p1, p2);

            AssertLedgerMatchesStock(s);
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(2f)]
        [InlineData(0.6f)]
        public void SameSubjectAcrossTwoRecordings_CreditsWhatStockAdded(float multiplier)
        {
            var s = Goo();
            float a1 = s.Submit(0.4f, multiplier);
            CommitRecording("rec-a", 100.0, 200.0,
                CaptureInFlight(s, a1, multiplier, 150.0, "rec-a"));

            float a2 = s.Submit(0.5f, multiplier);
            CommitRecording("rec-b", 300.0, 400.0,
                CaptureInFlight(s, a2, multiplier, 350.0, "rec-b"));

            AssertLedgerMatchesStock(s);
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(2f)]
        [InlineData(0.6f)]
        public void KscThenRecording_CreditsWhatStockAdded(float multiplier)
        {
            var s = Goo();
            float a1 = s.Submit(0.25f, multiplier);
            CaptureAtKsc(s, a1, multiplier, 50.0);

            float a2 = s.Submit(0.6f, multiplier);
            CommitRecording("rec-a", 100.0, 200.0,
                CaptureInFlight(s, a2, multiplier, 150.0, "rec-a"));

            AssertLedgerMatchesStock(s);
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(2f)]
        [InlineData(0.6f)]
        public void TransmitThenRecover_CreditsWhatStockAdded(float multiplier)
        {
            // Transmit at xmitScalar 0.3 inside a recording, then a partial recovery at the
            // KSC (short of the cap, where the old capture's over-credit was not hidden).
            var s = new StockSubject { Id = "surfaceSample@MunSrfLandedMidlands", Cap = 40f };
            float transmit = s.Submit(0.3f, multiplier);
            CommitRecording("rec-a", 100.0, 200.0,
                CaptureInFlight(s, transmit, multiplier, 150.0, "rec-a"));

            float recover = s.Submit(0.8f, multiplier);
            CaptureAtKsc(s, recover, multiplier, 500.0);

            Assert.True(s.Science < s.Cap);
            AssertLedgerMatchesStock(s);
        }

        // ================================================================
        // Capture core: row units and the frozen multiplier
        // ================================================================

        [Theory]
        [InlineData(2f, 6f, 3f)]
        [InlineData(0.6f, 5.4f, 9f)]
        public void Capture_RowIsSubjectUnitsAndItsPoolCreditIsTheAmount(
            float multiplier, float amount, float expectedIncrement)
        {
            GameStateRecorder.TagResolverForTesting = () => "rec";
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;
            new GameStateRecorder().CaptureScienceSubject(
                amount, "s", 20f, 30f, multiplier, 42.0, null, null);

            var pending = Assert.Single(GameStateRecorder.PendingScienceSubjects);
            Assert.Equal((double)expectedIncrement, (double)pending.science, 4);
            Assert.Equal((double)multiplier, (double)pending.scienceGainMultiplier, 4);
            Assert.Equal(30f, pending.subjectMaxValue);

            // The multiplier is applied exactly once, by the pool credit: not by both the
            // capture and the walk, and not cancelled out.
            var action = GameStateEventConverter.ConvertScienceSubjects(
                new[] { pending }, "rec", 0.0, 100.0).Single();
            Assert.Equal((double)expectedIncrement, (double)action.ScienceAwarded, 4);
            Assert.Equal((double)amount, (double)action.GetScienceAwardedPoolCredit(), 4);
            var module = new ScienceModule();
            module.Reset();
            module.ProcessEarning(action);
            Assert.Equal((double)amount, module.GetTotalEffectiveEarnings(), 4);
            Assert.Equal((double)expectedIncrement, module.GetSubjectCredited("s"), 4);
        }

        // ================================================================
        // Committed-science cache
        // ================================================================

        [Fact]
        public void Cache_OldRunningTotalRow_IsWalkedUnchanged_NewIncrementAddsOnTop()
        {
            // Owner decision: fix going forward only. A row an older build wrote holds the
            // subject's running total (9.1); it is kept, and a new increment (2) adds on top.
            Ledger.AddAction(new GameAction
            {
                UT = 10.0, Type = GameActionType.ScienceEarning, SubjectId = "goo",
                ScienceAwarded = 9.1f, SubjectMaxValue = 24f
            });
            var s = new StockSubject { Id = "goo", Cap = 24f, Science = 9.1f };
            float amount = s.Submit(2f / 14.9f, 1f);
            CommitRecording("rec-a", 100.0, 200.0, CaptureInFlight(s, amount, 1f, 150.0, "rec-a"));

            LedgerOrchestrator.RecalculateAndPatch();
            Assert.True(GameStateStore.TryGetCommittedSubjectScience("goo", out float cached));
            Assert.Equal(11.1, (double)cached, 3);
            Assert.Equal(11.1, LedgerOrchestrator.Science.GetSubjectCredited("goo"), 3);
        }

        [Fact]
        public void Cache_ARowTheLedgerAlreadyHeld_IsNotCountedTwice()
        {
            // KSC direct path files the capture; the same capture then arrives with a
            // recording commit and is deduped. The idempotent max mirror plus the recalc
            // rebuild must leave the cache at stock's total, not double it.
            var s = Goo();
            float a1 = s.Submit(0.3f, 1f);
            CaptureAtKsc(s, a1, 1f, 150.0);
            var filed = Assert.Single(Ledger.Actions.Where(a => a.Type == GameActionType.ScienceEarning));
            var pending = new PendingScienceSubject
            {
                subjectId = s.Id, science = filed.ScienceAwarded, subjectMaxValue = s.Cap,
                captureUT = 150.0, reasonKey = "", recordingId = "rec-a", scienceGainMultiplier = 1f
            };
            CommitRecording("rec-a", 100.0, 200.0, pending);

            Assert.Single(Ledger.Actions.Where(a => a.Type == GameActionType.ScienceEarning));
            Assert.True(GameStateStore.TryGetCommittedSubjectScience(s.Id, out float cached));
            Assert.Equal((double)s.Science, (double)cached, 4);
        }
    }
}
