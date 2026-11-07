using System;
using System.Collections.Generic;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling 2026-11-04 (career settings audit section 3.4): "Allow negative funds /
    /// science" is locked at career start. Every loss is judged under the locked value, a live
    /// value that drifts from it is set back, and the stock switch is greyed mid-career.
    /// </summary>
    [Collection("Sequential")]
    public class CareerSettingsLockTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private bool live;
        private readonly List<bool> writes = new List<bool>();

        public CareerSettingsLockTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            Ledger.ResetForTesting();
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = () => live;
            CareerSettingsLock.LiveWriterForTesting = v => { writes.Add(v); live = v; return true; };
        }

        public void Dispose()
        {
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = null;
            Ledger.ResetForTesting();
            CareerSettingsLock.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static FundsModule Walk(params GameAction[] actions)
        {
            var funds = new FundsModule();
            funds.Reset();
            var list = new List<GameAction>(actions);
            funds.PrePass(list);
            foreach (var a in list)
            {
                a.Effective = true;
                funds.ProcessAction(a);
            }
            return funds;
        }

        private static GameAction Seed(double funds) => new GameAction
        {
            ActionId = "seed", UT = 0, Type = GameActionType.FundsInitial, InitialFunds = (float)funds,
        };

        private static GameAction Fail(string id, double ut, float penalty) => new GameAction
        {
            ActionId = id, UT = ut, CaptureSequence = 1, Type = GameActionType.ContractFail,
            ContractId = id, FundsPenalty = penalty,
        };

        private static GameAction Earn(string id, double ut, float funds) => new GameAction
        {
            ActionId = id, UT = ut, CaptureSequence = 3, Type = GameActionType.FundsEarning, FundsAwarded = funds,
        };

        [Fact]
        public void Decide_LocksWhenNone_RewritesADrift_ElseNothing()
        {
            Assert.Equal(CareerSettingsLock.LockDecision.Lock, CareerSettingsLock.Decide(null, true));
            Assert.Equal(CareerSettingsLock.LockDecision.None, CareerSettingsLock.Decide(false, false));
            Assert.Equal(CareerSettingsLock.LockDecision.RewriteLive, CareerSettingsLock.Decide(false, true));
            Assert.Equal(CareerSettingsLock.LockDecision.RewriteLive, CareerSettingsLock.Decide(true, false));
            Assert.True(CareerSettingsLock.Resolve(null, true));
            Assert.False(CareerSettingsLock.Resolve(false, true));
        }

        [Fact]
        public void FirstLoad_LocksTheLiveValue_ALaterDriftIsSetBack()
        {
            live = false;
            CareerSettingsLock.EnsureAndEnforce(KspStatePatcher.ReadLiveAllowNegativeCurrency(), "ledger-load");
            Assert.Equal(false, CareerSettingsLock.AllowNegative);
            Assert.Empty(writes);
            Assert.Contains(logLines, l => l.Contains("[CareerSettings]")
                && l.Contains("Allow negative currency locked off for this career (ledger-load)"));

            live = true;
            CareerSettingsLock.EnsureAndEnforce(KspStatePatcher.ReadLiveAllowNegativeCurrency(), "difficulty-dialog");
            Assert.Equal(new[] { false }, writes);
            Assert.False(live);
            Assert.Contains(logLines, l => l.Contains("[CareerSettings]")
                && l.Contains("is locked off for this career but the game had it on (difficulty-dialog): set it back"));
        }

        [Fact]
        public void SwitchingItOnLater_DoesNotUnforgiveAnOldLoss()
        {
            // Locked off: a 8000 penalty at 5000 funds stopped at 0. The live switch turns on
            // (a hand-edited save) before the walk: the walk keeps the lock.
            live = false;
            CareerSettingsLock.EnsureAndEnforce(live, "test");
            CareerSettingsLock.LiveWriterForTesting = v => false;
            live = true;

            Assert.False(KspStatePatcher.ReadAllowNegativeCurrency());
            FundsModule funds = Walk(Seed(5000), Fail("f", 2000, 8000f), Earn("e", 9000, 40000f));
            Assert.Equal(40000.0, funds.GetRunningBalance(), 6);
            Assert.Equal(3000.0, funds.GetForgivenBelowZero(), 6);
        }

        [Fact]
        public void SwitchingItOffLater_DoesNotForgiveAnOldDebt()
        {
            live = true;
            CareerSettingsLock.EnsureAndEnforce(live, "test");
            CareerSettingsLock.LiveWriterForTesting = v => false;
            live = false;

            FundsModule funds = Walk(Seed(5000), Fail("f", 2000, 8000f), Earn("e", 9000, 43000f));
            Assert.Equal(40000.0, funds.GetRunningBalance(), 6);
            Assert.Equal(0.0, funds.GetForgivenBelowZero(), 6);
        }

        [Fact]
        public void TheLock_RoundTripsThroughTheLedgerNode_AndAnUnreadableValueRelocks()
        {
            live = true;
            CareerSettingsLock.EnsureAndEnforce(live, "test");
            ConfigNode node = Ledger.BuildSaveNode();
            Assert.Equal("1", node.GetValue(CareerSettingsLock.AllowNegativeKey));

            CareerSettingsLock.ResetForTesting();
            Assert.True(Ledger.LoadFromNode(node));
            Assert.Equal(true, CareerSettingsLock.AllowNegative);

            var bad = new ConfigNode("LEDGER");
            bad.AddValue(CareerSettingsLock.AllowNegativeKey, "maybe");
            CareerSettingsLock.LoadFrom(bad, "test");
            Assert.Null(CareerSettingsLock.AllowNegative);
            Assert.Contains(logLines, l => l.Contains("[CareerSettings]") && l.Contains("Unreadable allowNegativeLocked='maybe'"));
        }

        [Fact]
        public void TheSwitch_IsGreyedOnlyMidCareer_WithTheReasonAppendedToItsTooltip()
        {
            Assert.True(AllowNegativeCurrencyLockPatch.ShouldLock(false, true));
            Assert.True(AllowNegativeCurrencyLockPatch.ShouldLock(false, false));
            Assert.False(AllowNegativeCurrencyLockPatch.ShouldLock(true, true));
            Assert.False(AllowNegativeCurrencyLockPatch.ShouldLock(false, null));

            Assert.True(AllowNegativeCurrencyLockPatch.BlocksMember(true, "AllowNegativeCurrency"));
            Assert.False(AllowNegativeCurrencyLockPatch.BlocksMember(true, "PressurePartLimits"));
            Assert.False(AllowNegativeCurrencyLockPatch.BlocksMember(false, "AllowNegativeCurrency"));

            Assert.Equal("Allow it. Parsek: locked for this career, because changing it would rewrite past penalties.",
                AllowNegativeCurrencyLockPatch.LockedTooltip("Allow it."));
        }
    }
}
