using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling 2026-11-04 (replay audit T2): after a rewind, a science subject earned
    /// first at a lower multiplier and later (on the committed timeline) at a higher one pays
    /// the earliest row its own value and the later row the difference, so the subject total
    /// is the larger timeline's, never lower.
    /// </summary>
    [Collection("Sequential")]
    public class RewindTopUpScienceTests : IDisposable
    {
        private const string Subject = "crewReport@MunInSpaceHigh";
        private readonly List<string> logLines = new List<string>();
        private readonly ScienceModule science = new ScienceModule();

        public RewindTopUpScienceTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = () => false;
            Ledger.ResetForTesting();
        }

        public void Dispose()
        {
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = null;
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static GameAction Earn(string id, double ut, long cseq, float units, float mult, float cap = 10f)
            => new GameAction
            {
                ActionId = id, UT = ut, CaptureSequence = cseq, Type = GameActionType.ScienceEarning,
                SubjectId = Subject, ScienceAwarded = units, SubjectMaxValue = cap,
                ScienceGainMultiplier = mult,
            };

        private void Walk(params GameAction[] rows)
        {
            science.Reset();
            foreach (GameAction a in rows.OrderBy(r => r.UT))
                science.ProcessAction(a);
        }

        private static void Rewound() => StockShadowTakeBack.AddBreakForTesting(1, 1000.0, "PlainRewind");

        [Fact]
        public void OwnersExample_EarlierPaysTen_LaterPaysTheFiveMore()
        {
            // T1 = 5000 earned first at 150% (15); rewind; T0 = 2000 earned at 100% (10).
            var later = Earn("t1", 5000, 1, 10f, 1.5f);
            Rewound();
            var earlier = Earn("t0", 2000, 2, 10f, 1f);

            Walk(later, earlier);

            Assert.Equal(10.0, earlier.EffectiveScience, 3);
            Assert.Equal(5.0, later.EffectiveScience, 3);
            Assert.Equal(15.0, science.GetRunningScience(), 3);
            Assert.Contains(logLines, l => l.Contains("[ScienceModule]") && l.Contains("Earning topped up")
                && l.Contains("id=t1") && l.Contains("topUp=5"));
        }

        [Fact]
        public void TwoCommittedRows_TheCumulativeFloorKeepsTheCommittedTimelinesWholeTotal()
        {
            // Committed: A 6 units x2 (12) at 5000, B 3 units x2 (6) at 6000. Rewind; C 9 x1 at 2000.
            var a = Earn("A", 5000, 1, 6f, 2f);
            var b = Earn("B", 6000, 2, 3f, 2f);
            StockShadowTakeBack.AddBreakForTesting(2, 1000.0, "PlainRewind");
            var c = Earn("C", 2000, 3, 9f, 1f);

            Walk(a, b, c);

            Assert.Equal(9.0, c.EffectiveScience, 3);
            Assert.Equal(3.0, a.EffectiveScience, 3);
            Assert.Equal(6.0, b.EffectiveScience, 3);
            Assert.Equal(18.0, science.GetRunningScience(), 3);
        }

        [Fact]
        public void NeverRewound_TheLaterRowAtTheCapPaysNothing_AsBefore()
        {
            var first = Earn("first", 2000, 1, 10f, 1f);
            var second = Earn("second", 5000, 2, 10f, 1.5f);

            Walk(first, second);

            Assert.Equal(10.0, first.EffectiveScience, 3);
            Assert.Equal(0.0, second.EffectiveScience, 3);
            Assert.DoesNotContain(logLines, l => l.Contains("Earning topped up"));
        }

        [Fact]
        public void HigherMultiplierEarlier_TheLaterRowStaysAtZero()
        {
            var later = Earn("later", 5000, 1, 10f, 1f);
            Rewound();
            var earlier = Earn("earlier", 2000, 2, 10f, 2f);

            Walk(later, earlier);

            Assert.Equal(20.0, earlier.EffectiveScience, 3);
            Assert.Equal(0.0, later.EffectiveScience, 3);
            Assert.Equal(20.0, science.GetRunningScience(), 3);
        }

        [Fact]
        public void SameMultiplier_TheRepeatAfterARewindStillPaysNothing()
        {
            var committed = Earn("committed", 5000, 1, 10f, 1f);
            Rewound();
            var again = Earn("again", 2000, 2, 10f, 1f);

            Walk(committed, again);

            Assert.Equal(10.0, again.EffectiveScience, 3);
            Assert.Equal(0.0, committed.EffectiveScience, 3);
        }

        [Fact]
        public void RowWithoutACaptureNumber_GetsTheUnitsRuleOnly()
        {
            var later = Earn("old", 5000, 0, 10f, 1.5f);
            Rewound();
            var earlier = Earn("t0", 2000, 2, 10f, 1f);

            Walk(later, earlier);

            Assert.Equal(0.0, later.EffectiveScience, 3);
        }

        [Fact]
        public void OwnTimelinePool_CountsOnlyRowsCapturedBeforeAndAtOrBeforeTheRow()
        {
            var walked = new List<ScienceModule.SubjectCapture>
            {
                new ScienceModule.SubjectCapture { CaptureSequence = 1, UT = 100, Pool = 4 },
                new ScienceModule.SubjectCapture { CaptureSequence = 5, UT = 150, Pool = 7 }, // captured later
                new ScienceModule.SubjectCapture { CaptureSequence = 2, UT = 300, Pool = 9 }, // after the row's UT
            };
            Assert.Equal(6.0, ScienceModule.OwnTimelinePool(walked, 3, 200, 2, null), 6);
            Assert.Equal(2.0, ScienceModule.OwnTimelinePool(null, 3, 200, 2, null), 6);
            // A break after capture 1 that loaded a state from before UT 100: stock forgot it.
            var forgot = new List<TimelineBreak> { new TimelineBreak { CaptureHighWater = 1, UT = 50 } };
            Assert.Equal(2.0, ScienceModule.OwnTimelinePool(walked, 3, 200, 2, forgot), 6);
        }

        [Fact]
        public void StockForgotTheEarlierCredit_TheRepeatIsTakenBack_NotToppedUp()
        {
            // Rewind to UT 50; the committed row at UT 100 is not in stock's loaded subject,
            // so stock pays the repeat at UT 120 in full: that is a duplicate, not a top-up.
            StockShadowTakeBack.SetEligibleFromForTesting(1);
            var committed = Earn("s1", 100, 10, 8f, 1f);
            StockShadowTakeBack.AddBreakForTesting(10, 50.0, "PlainRewind");
            var repeat = Earn("s2", 120, 20, 8f, 1f);

            Walk(committed, repeat);

            Assert.Equal(2.0, repeat.EffectiveScience, 3);
            Assert.Equal(6.0, repeat.TakeBackScience, 3);
            Assert.DoesNotContain(logLines, l => l.Contains("Earning topped up"));
        }

        [Theory]
        [InlineData(0.0, 20.0, 10.0, 10.0)] // the floor binds
        [InlineData(4.0, 20.0, 18.0, 4.0)]  // the cap pays more than the floor
        [InlineData(0.0, 10.0, 20.0, 0.0)]  // never below 0
        public void TopUpFloor_IsTheLargerOfTheCapAndTheOwnTimelineRemainder(
            double capPaid, double own, double credited, double expected)
        {
            Assert.Equal(expected, ScienceModule.TopUpFloor(capPaid, own, credited), 6);
        }
    }
}
