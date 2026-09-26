using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// SCIENCE-CUMULATIVE-CAPTURE-OVER-CREDIT: a science capture records the increment ONE
    /// stock submission added (pre-multiplier subject units), not the subject's running
    /// total, so repeated collections of one subject credit the ledger exactly what stock
    /// credited. Each scenario models stock's <c>SubmitScienceData</c> (add the increment to
    /// <c>subject.science</c>, then fire <c>amount = increment * ScienceGainMultiplier</c>),
    /// builds the pending subject through the recorder's own builder, drives the real
    /// ledger commit paths, and compares the walk against what stock added.
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

        private static PendingScienceSubject Capture(
            StockSubject s, float amount, float multiplier, double ut, string reasonKey, string recordingId)
        {
            return GameStateRecorder.BuildCapturedScienceSubject(
                s.Id, s.Cap, amount, multiplier, ut, reasonKey, recordingId);
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
        public void SameSubjectTwiceInOneRecording_CreditsWhatStockAdded(float multiplier)
        {
            var s = Goo();
            float a1 = s.Submit(0.3f, multiplier);
            float a2 = s.Submit(0.3f, multiplier);

            CommitRecording("rec-a", 100.0, 200.0,
                Capture(s, a1, multiplier, 120.0, "ScienceTransmission", "rec-a"),
                Capture(s, a2, multiplier, 150.0, "ScienceTransmission", "rec-a"));

            AssertLedgerMatchesStock(s);
            Assert.Contains(logLines, l => l.Contains("[GameStateStore]")
                && l.Contains("CommitScienceActions: 1 added, 1 updated")
                && l.Contains("increments=2"));
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(2f)]
        public void SameSubjectAcrossTwoRecordings_CreditsWhatStockAdded(float multiplier)
        {
            var s = Goo();
            float a1 = s.Submit(0.4f, multiplier);
            CommitRecording("rec-a", 100.0, 200.0,
                Capture(s, a1, multiplier, 150.0, "ScienceTransmission", "rec-a"));

            float a2 = s.Submit(0.5f, multiplier);
            CommitRecording("rec-b", 300.0, 400.0,
                Capture(s, a2, multiplier, 350.0, "VesselRecovery", "rec-b"));

            AssertLedgerMatchesStock(s);
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(2f)]
        public void KscThenRecording_CreditsWhatStockAdded(float multiplier)
        {
            var s = Goo();
            float a1 = s.Submit(0.25f, multiplier);
            Assert.True(LedgerOrchestrator.TryRecordKscScienceSubject(
                Capture(s, a1, multiplier, 50.0, "ScienceTransmission", ""), vesselName: null));

            float a2 = s.Submit(0.6f, multiplier);
            CommitRecording("rec-a", 100.0, 200.0,
                Capture(s, a2, multiplier, 150.0, "ScienceTransmission", "rec-a"));

            AssertLedgerMatchesStock(s);
        }

        [Theory]
        [InlineData(1f)]
        [InlineData(2f)]
        public void TransmitThenRecover_CreditsWhatStockAdded(float multiplier)
        {
            // Transmit at xmitScalar 0.3 inside a recording, then a partial recovery at the
            // KSC (short of the cap, where the old capture's over-credit was not hidden).
            var s = new StockSubject { Id = "surfaceSample@MunSrfLandedMidlands", Cap = 40f };
            float transmit = s.Submit(0.3f, multiplier);
            CommitRecording("rec-a", 100.0, 200.0,
                Capture(s, transmit, multiplier, 150.0, "ScienceTransmission", "rec-a"));

            float recover = s.Submit(0.8f, multiplier);
            Assert.True(LedgerOrchestrator.TryRecordKscScienceSubject(
                Capture(s, recover, multiplier, 500.0, LedgerOrchestrator.VesselRecoveryReasonKey, ""),
                vesselName: null));

            Assert.True(s.Science < s.Cap);
            AssertLedgerMatchesStock(s);
        }

        // The pre-fix shape, for contrast: the same two submissions captured as RUNNING
        // totals walk to more than stock credited. Pins why the capture must be an increment.
        [Fact]
        public void RunningTotalCaptures_WouldOverCredit()
        {
            var s = Goo();
            s.Submit(0.3f, 1f);
            float total1 = s.Science;
            s.Submit(0.3f, 1f);
            float total2 = s.Science;

            var module = new ScienceModule();
            module.Reset();
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.ScienceEarning, SubjectId = s.Id,
                ScienceAwarded = total1, SubjectMaxValue = s.Cap
            });
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.ScienceEarning, SubjectId = s.Id,
                ScienceAwarded = total2, SubjectMaxValue = s.Cap
            });

            Assert.Equal((double)(total1 + total2), module.GetTotalEffectiveEarnings(), 3);
            Assert.True(module.GetTotalEffectiveEarnings() > s.Pool + 1.0);
        }

        // ================================================================
        // Capture builder
        // ================================================================

        [Theory]
        [InlineData(6f, 2f, 3f)]
        [InlineData(6f, 1f, 6f)]
        [InlineData(3f, 0.6f, 5f)]
        [InlineData(6f, 0f, 6f)]
        [InlineData(6f, -1f, 6f)]
        public void ComputeSubjectScienceIncrement_DividesOutThePositiveMultiplier(
            float amount, float multiplier, float expected)
        {
            Assert.Equal((double)expected, (double)GameStateRecorder.ComputeSubjectScienceIncrement(amount, multiplier), 4);
        }

        [Fact]
        public void BuildCapturedScienceSubject_IsAFlaggedIncrementWhosePoolCreditIsTheAmount()
        {
            var pending = GameStateRecorder.BuildCapturedScienceSubject(
                "s", 30f, 5.4f, 0.6f, 42.0, "ScienceTransmission", "rec");

            Assert.True(pending.scienceIsIncrement);
            Assert.Equal(9.0, (double)pending.science, 4);
            Assert.Equal(0.6f, pending.scienceGainMultiplier);
            Assert.Equal(30f, pending.subjectMaxValue);

            var action = GameStateEventConverter.ConvertScienceSubjects(
                new[] { pending }, "rec", 0.0, 100.0).Single();
            Assert.True(action.ScienceAwardedIsIncrement);
            Assert.Equal(5.4, (double)action.GetScienceAwardedPoolCredit(), 4);
        }

        [Fact]
        public void Recorder_CapturesTheIncrementThroughTheBuilder_NotTheSubjectTotal()
        {
            string body = ReadRecorderMethod("private void OnScienceReceived(", "#endregion");
            string collapsed = Regex.Replace(body, @"\s+", " ");
            Assert.Contains("var pendingSubject = BuildCapturedScienceSubject( subject.id, subject.scienceCap, amount,", collapsed);
            Assert.DoesNotContain("science = subject.science", collapsed);
        }

        // ================================================================
        // Committed-science cache
        // ================================================================

        [Theory]
        [InlineData(0f, 3f, 10f, 3f)]      // first increment
        [InlineData(3f, 4f, 10f, 7f)]      // summed
        [InlineData(8f, 4f, 10f, 10f)]     // capped at the subject cap
        [InlineData(12f, 4f, 10f, 12f)]    // an older total above the cap is never lowered
        [InlineData(3f, 4f, 0f, 7f)]       // no cap known: plain sum
        [InlineData(3f, 0f, 10f, 3f)]      // non-positive increment: unchanged
        public void MergeCommittedScienceIncrement_IsACappedSum(
            float existing, float increment, float cap, float expected)
        {
            Assert.Equal((double)expected, (double)GameStateStore.MergeCommittedScienceIncrement(existing, increment, cap), 4);
        }

        [Fact]
        public void Cache_OldTotalFromASave_IsNotSummedAgain_NewIncrementAddsOnTop()
        {
            // A save written by an older build holds a running total (9.1) in its cache.
            var root = new ConfigNode("ROOT");
            var sci = root.AddNode("SCIENCE_SUBJECTS");
            var entry = sci.AddNode("SUBJECT");
            entry.AddValue("id", "goo");
            entry.AddValue("science", "9.1");
            GameStateStore.DeserializeScienceSubjectsFrom(root);

            // A non-increment row (a running total) still max-merges: 9.1 stays.
            GameStateStore.CommitScienceActions(new List<GameAction>
            {
                new GameAction
                {
                    Type = GameActionType.ScienceEarning, SubjectId = "goo",
                    ScienceAwarded = 9.1f, SubjectMaxValue = 24f
                }
            });
            Assert.True(GameStateStore.TryGetCommittedSubjectScience("goo", out float afterTotal));
            Assert.Equal(9.1, (double)afterTotal, 4);

            // A new capture's increment adds once on top of the stored total.
            GameStateStore.CommitScienceActions(new List<GameAction>
            {
                new GameAction
                {
                    Type = GameActionType.ScienceEarning, SubjectId = "goo",
                    ScienceAwarded = 2f, SubjectMaxValue = 24f, ScienceAwardedIsIncrement = true
                }
            });
            Assert.True(GameStateStore.TryGetCommittedSubjectScience("goo", out float afterIncrement));
            Assert.Equal(11.1, (double)afterIncrement, 4);
            Assert.Contains(logLines, l => l.Contains("[GameStateStore]")
                && l.Contains("CommitScienceActions: 0 added, 1 updated")
                && l.Contains("increments=1"));
        }

        [Fact]
        public void Cache_ARowTheLedgerAlreadyHeld_IsNotMirroredTwice()
        {
            // KSC direct path files the capture; the same capture then arrives with a
            // recording commit and is deduped. The cache must not add it a second time.
            var s = Goo();
            float a1 = s.Submit(0.3f, 1f);
            var pending = Capture(s, a1, 1f, 150.0, "ScienceTransmission", "");
            Assert.True(LedgerOrchestrator.TryRecordKscScienceSubject(pending, vesselName: null));

            var tagged = pending;
            tagged.recordingId = "rec-a";
            CommitRecording("rec-a", 100.0, 200.0, tagged);

            Assert.Single(Ledger.Actions.Where(a => a.Type == GameActionType.ScienceEarning));
            Assert.True(GameStateStore.TryGetCommittedSubjectScience(s.Id, out float cached));
            Assert.Equal((double)s.Science, (double)cached, 4);
            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("FilterSurvivingScienceActions: 1 science row(s) already in the ledger"));
        }

        [Fact]
        public void FilterSurvivingScienceActions_KeepsOnlySurvivorsInOrder()
        {
            var a = new GameAction { Type = GameActionType.ScienceEarning, SubjectId = "a" };
            var b = new GameAction { Type = GameActionType.ScienceEarning, SubjectId = "b" };
            var c = new GameAction { Type = GameActionType.ScienceEarning, SubjectId = "c" };
            var other = new GameAction { Type = GameActionType.FundsEarning };

            var kept = LedgerOrchestrator.FilterSurvivingScienceActions(
                new List<GameAction> { a, b, c }, new List<GameAction> { other, c, a });

            Assert.Equal(new[] { a, c }, kept);
            Assert.Empty(LedgerOrchestrator.FilterSurvivingScienceActions(null, new List<GameAction> { a }));
        }

        // ================================================================

        private static string ReadRecorderMethod(string signature, string endAnchor)
        {
            string path = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "Source", "Parsek", "GameStateRecorder.cs"));
            Assert.True(File.Exists(path), "source file not found for scan: " + path);
            string source = File.ReadAllText(path).Replace("\r\n", "\n");
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, signature + " not found");
            int end = source.IndexOf(endAnchor, start, StringComparison.Ordinal);
            Assert.True(end > start, endAnchor + " not found after " + signature);
            return source.Substring(start, end - start);
        }
    }
}
