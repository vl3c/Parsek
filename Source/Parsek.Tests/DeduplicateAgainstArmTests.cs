using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests on the two per-candidate arms of
    /// <see cref="LedgerOrchestrator.DeduplicateAgainst"/>:
    /// <see cref="LedgerOrchestrator.DedupScienceCaptureCandidate"/> and
    /// <see cref="LedgerOrchestrator.DedupEventOrEconomicCandidate"/>. The whole-method
    /// behaviour is pinned by <see cref="DeduplicateAgainstCharacterizationTests"/>.
    /// </summary>
    [Collection("Sequential")]
    public class DeduplicateAgainstArmTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public DeduplicateAgainstArmTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static GameAction Capture(string id) => new GameAction
        {
            ActionId = id,
            Type = GameActionType.ScienceEarning,
            UT = 10.0,
            SubjectId = "s1",
            HasScienceCaptureIdentity = true,
        };

        private static GameAction Spend(string id, double ut, string node, long cseq = 0, bool eventId = false) =>
            new GameAction
            {
                ActionId = id,
                Type = GameActionType.ScienceSpending,
                UT = ut,
                NodeId = node,
                CaptureSequence = cseq,
                HasEventCaptureIdentity = eventId,
            };

        [Fact]
        public void ScienceArm_NewId_IsKeptAndCreatesTheBatchSet()
        {
            var result = new List<GameAction>();
            HashSet<string> seen = null;
            int matches = 0, repeats = 0;
            var c = Capture("cap-a");

            LedgerOrchestrator.DedupScienceCaptureCandidate(
                new List<GameAction>(), c, result, ref seen, ref matches, ref repeats);

            Assert.Same(c, Assert.Single(result));
            Assert.NotNull(seen);
            Assert.Contains("cap-a", seen);
            Assert.Equal(0, matches);
            Assert.Equal(0, repeats);
        }

        [Fact]
        public void ScienceArm_RepeatInBatch_IsDroppedWithoutReadingTheLedger()
        {
            var result = new List<GameAction>();
            var seen = new HashSet<string> { "cap-a" };
            int matches = 0, repeats = 0;

            // The existing list also holds the id: a batch repeat is counted as a repeat, not a match.
            LedgerOrchestrator.DedupScienceCaptureCandidate(
                new List<GameAction> { Capture("cap-a") }, Capture("cap-a"), result,
                ref seen, ref matches, ref repeats);

            Assert.Empty(result);
            Assert.Equal(0, matches);
            Assert.Equal(1, repeats);
        }

        [Fact]
        public void ScienceArm_IdAlreadyFiled_IsDroppedAndCounted()
        {
            var result = new List<GameAction>();
            HashSet<string> seen = null;
            int matches = 0, repeats = 0;

            LedgerOrchestrator.DedupScienceCaptureCandidate(
                new List<GameAction> { Spend("x", 1.0, "n"), Capture("cap-a") }, Capture("cap-a"), result,
                ref seen, ref matches, ref repeats);

            Assert.Empty(result);
            Assert.Equal(1, matches);
            Assert.Equal(0, repeats);
            Assert.Contains("cap-a", seen);
        }

        [Fact]
        public void EconomicArm_SameCapture_IsDroppedAsEventMatch()
        {
            var result = new List<GameAction>();
            int ev = 0, legacy = 0, kept = 0;

            LedgerOrchestrator.DedupEventOrEconomicCandidate(
                new List<GameAction> { Spend("e1", 100.0, "n1", 7, true) },
                Spend("c1", 100.0, "n1", 7, true), result, ref ev, ref legacy, ref kept);

            Assert.Empty(result);
            Assert.Equal(1, ev);
            Assert.Equal(0, legacy);
            Assert.Equal(0, kept);
            Assert.Empty(logLines);
        }

        [Fact]
        public void EconomicArm_OtherCaptureSameKey_IsKeptAndLoggedAsSecondEvent()
        {
            var result = new List<GameAction>();
            int ev = 0, legacy = 0, kept = 0;
            var c = Spend("c1", 100.0, "n1", 9, true);

            LedgerOrchestrator.DedupEventOrEconomicCandidate(
                new List<GameAction> { Spend(null, 100.0, "n1", 7, true) },
                c, result, ref ev, ref legacy, ref kept);

            Assert.Same(c, Assert.Single(result));
            Assert.Equal(0, ev);
            Assert.Equal(0, legacy);
            Assert.Equal(1, kept);
            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("kept ScienceSpending key='n1'")
                && l.Contains("row (none)")
                && l.Contains("capture cseq=7"));
        }

        [Fact]
        public void EconomicArm_RowWithoutCaptureNumber_FallsBackToTheLegacyMatch()
        {
            var result = new List<GameAction>();
            int ev = 0, legacy = 0, kept = 0;

            LedgerOrchestrator.DedupEventOrEconomicCandidate(
                new List<GameAction> { Spend("old", 100.0625, "n1") },
                Spend("c1", 100.0, "n1", 9, true), result, ref ev, ref legacy, ref kept);

            Assert.Empty(result);
            Assert.Equal(0, ev);
            Assert.Equal(1, legacy);
            Assert.Equal(0, kept);
        }

        [Fact]
        public void EconomicArm_OutsideTheWindowOrOtherType_IsKeptSilently()
        {
            var result = new List<GameAction>();
            int ev = 0, legacy = 0, kept = 0;
            var other = new GameAction { ActionId = "o", Type = GameActionType.FundsSpending, UT = 100.0 };
            var c = Spend("c1", 100.125, "n1");

            LedgerOrchestrator.DedupEventOrEconomicCandidate(
                new List<GameAction> { other, Spend("old", 100.0, "n1") },
                c, result, ref ev, ref legacy, ref kept);

            Assert.Same(c, Assert.Single(result));
            Assert.Equal(0, ev + legacy + kept);
            Assert.Empty(logLines);
        }

        [Fact]
        public void EconomicArm_CountersAccumulateAcrossCalls()
        {
            var result = new List<GameAction>();
            int ev = 2, legacy = 3, kept = 4;

            LedgerOrchestrator.DedupEventOrEconomicCandidate(
                new List<GameAction> { Spend("old", 100.0, "n1") },
                Spend("c1", 100.0, "n1"), result, ref ev, ref legacy, ref kept);

            Assert.Empty(result);
            Assert.Equal(2, ev);
            Assert.Equal(4, legacy);
            Assert.Equal(4, kept);
        }
    }
}
