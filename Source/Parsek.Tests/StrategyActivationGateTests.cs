using System;
using System.Collections.Generic;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A committed strategy activation the clock crosses mid-scene is switched on at its UT
    /// (todo STRATEGY-ACTIVATION-CROSSED-IN-FLIGHT-NOT-APPLIED), and a strategy's own
    /// duration running out is the clock's event, filed untagged
    /// (todo STRATEGY-EXPIRY-IN-FLIGHT-LOST-AT-DISCARD).
    /// </summary>
    public class StrategyActivationGateTests
    {
        private static Func<string, StrategyCommittedRows> Rows(
            Dictionary<string, StrategyCommittedRows> map)
        {
            return id =>
            {
                StrategyCommittedRows rows;
                return map.TryGetValue(id, out rows) ? rows : null;
            };
        }

        private static StrategyCommittedRows R(double[] activations, double[] deactivations)
        {
            return new StrategyCommittedRows(activations, deactivations);
        }

        // catches: a mid-scene crossing never switching the strategy on, so a flight across
        // the committed activation earns rewards without the committed diversion.
        [Fact]
        public void SelectCrossedActivations_ActivationInsideTheFrame_Selected()
        {
            var map = new Dictionary<string, StrategyCommittedRows>
            {
                { "OutsourcedRD", R(new[] { 500.0 }, new double[0]) },
            };
            var crossed = StrategyActivationGate.SelectCrossedActivations(
                new[] { "OutsourcedRD" }, Rows(map), 499.9, 500.0);
            Assert.Equal(new[] { "OutsourcedRD" }, crossed);
        }

        // catches: re-firing every frame on a strategy stock has off for its own reason.
        [Fact]
        public void SelectCrossedActivations_ActivationBeforeTheFrame_NotSelected()
        {
            var map = new Dictionary<string, StrategyCommittedRows>
            {
                { "OutsourcedRD", R(new[] { 500.0 }, new double[0]) },
            };
            Assert.Empty(StrategyActivationGate.SelectCrossedActivations(
                new[] { "OutsourcedRD" }, Rows(map), 500.0, 500.1));
        }

        [Fact]
        public void SelectCrossedActivations_ActivationStillAhead_NotSelected()
        {
            var map = new Dictionary<string, StrategyCommittedRows>
            {
                { "OutsourcedRD", R(new[] { 600.0 }, new double[0]) },
            };
            Assert.Empty(StrategyActivationGate.SelectCrossedActivations(
                new[] { "OutsourcedRD" }, Rows(map), 400.0, 500.0));
        }

        // catches: switching on a strategy whose committed deactivation came in the same
        // frame (a warp jump across both rows).
        [Fact]
        public void SelectCrossedActivations_ActivationAndDeactivationBothCrossed_NotSelected()
        {
            var map = new Dictionary<string, StrategyCommittedRows>
            {
                { "OutsourcedRD", R(new[] { 450.0 }, new[] { 480.0 }) },
            };
            Assert.Empty(StrategyActivationGate.SelectCrossedActivations(
                new[] { "OutsourcedRD" }, Rows(map), 400.0, 500.0));
        }

        [Fact]
        public void SelectCrossedActivations_SecondActivationAfterADeactivation_Selected()
        {
            var map = new Dictionary<string, StrategyCommittedRows>
            {
                { "OutsourcedRD", R(new[] { 100.0, 450.0 }, new[] { 300.0 }) },
            };
            Assert.Equal(new[] { "OutsourcedRD" }, StrategyActivationGate.SelectCrossedActivations(
                new[] { "OutsourcedRD" }, Rows(map), 400.0, 500.0));
        }

        [Fact]
        public void SelectCrossedActivations_ClockBackOrNaN_NothingSelected()
        {
            var map = new Dictionary<string, StrategyCommittedRows>
            {
                { "OutsourcedRD", R(new[] { 450.0 }, new double[0]) },
            };
            Assert.Empty(StrategyActivationGate.SelectCrossedActivations(
                new[] { "OutsourcedRD" }, Rows(map), 500.0, 400.0));
            Assert.Empty(StrategyActivationGate.SelectCrossedActivations(
                new[] { "OutsourcedRD" }, Rows(map), double.NaN, 500.0));
        }

        [Fact]
        public void SelectCrossedActivations_NoRowsOrEmptyId_Skipped_ResultSortedOrdinal()
        {
            var map = new Dictionary<string, StrategyCommittedRows>
            {
                { "b", R(new[] { 450.0 }, new double[0]) },
                { "a", R(new[] { 460.0 }, new double[0]) },
            };
            var crossed = StrategyActivationGate.SelectCrossedActivations(
                new[] { "b", "", "none", "a" }, Rows(map), 400.0, 500.0);
            Assert.Equal(new[] { "a", "b" }, crossed);
        }

        [Fact]
        public void NextActivationAfter_EarliestStrictlyAfter()
        {
            var entries = new List<CommittedFutureEntry>
            {
                new CommittedFutureEntry(CommittedFutureKind.StrategyActivate, "b", 700, null, null),
                new CommittedFutureEntry(CommittedFutureKind.StrategyActivate, "a", 500, null, null),
                new CommittedFutureEntry(CommittedFutureKind.StrategyActivate, "c", 600, null, null),
            };
            Assert.Equal(600.0, StrategyActivationGate.NextActivationAfter(entries, 500.0));
            Assert.Equal(500.0, StrategyActivationGate.NextActivationAfter(entries, 499.0));
            Assert.Equal(double.PositiveInfinity, StrategyActivationGate.NextActivationAfter(entries, 700.0));
            Assert.Equal(double.PositiveInfinity, StrategyActivationGate.NextActivationAfter(null, 0.0));
        }

        // ---------- the patch reads the committed state at now ----------

        private static readonly Func<string, double, float> NoFactor = (id, ut) => float.NaN;

        // catches: a walk past now (the commit path) ending with the re-activation at 900,
        // so the patch reads the strategy on now (activated 100) as future-dated and leaves
        // it off for the whole 100..800 stretch.
        [Fact]
        public void ResolveLedgerActiveAtNow_OnNowReactivatedLater_OnFromTheEarlierRow()
        {
            var map = new Dictionary<string, StrategyCommittedRows>
            {
                { "s", R(new[] { 100.0, 900.0 }, new[] { 800.0 }) },
            };
            var walk = new Dictionary<string, double> { { "s", 900.0 } };
            var factor = new Dictionary<string, float> { { "s", 0.5f } };
            var atNow = StrategyStatePatcher.ResolveLedgerActiveAtNow(
                walk, factor, new[] { "s" }, Rows(map), (id, ut) => ut == 100.0 ? 0.25f : float.NaN, 500.0);
            Assert.Equal(100.0, atNow.ActivateUT["s"]);
            Assert.Equal(0.25f, atNow.Factor["s"]);
        }

        // catches: the end-of-walk snapshot keeping on a strategy the committed timeline
        // has off now (deactivated 200, re-activated only at 900).
        [Fact]
        public void ResolveLedgerActiveAtNow_OffNowReactivatedLater_Off()
        {
            var map = new Dictionary<string, StrategyCommittedRows>
            {
                { "s", R(new[] { 100.0, 900.0 }, new[] { 200.0 }) },
            };
            var walk = new Dictionary<string, double> { { "s", 900.0 } };
            var atNow = StrategyStatePatcher.ResolveLedgerActiveAtNow(
                walk, null, new[] { "s" }, Rows(map), NoFactor, 500.0);
            Assert.False(atNow.ActivateUT.ContainsKey("s"));
            Assert.Contains("s", atNow.Managed);
        }

        // catches: a strategy on now but deactivated later dropping out of the active set
        // because the walk ended after its deactivation.
        [Fact]
        public void ResolveLedgerActiveAtNow_OnNowDeactivatedLater_OnFromManagedIds()
        {
            var map = new Dictionary<string, StrategyCommittedRows>
            {
                { "s", R(new[] { 100.0 }, new[] { 800.0 }) },
            };
            var atNow = StrategyStatePatcher.ResolveLedgerActiveAtNow(
                new Dictionary<string, double>(), null, new[] { "s" }, Rows(map), NoFactor, 500.0);
            Assert.Equal(100.0, atNow.ActivateUT["s"]);
            Assert.True(float.IsNaN(atNow.Factor["s"]));
        }

        [Fact]
        public void ResolveLedgerActiveAtNow_NoCommittedRows_KeepsTheWalkState()
        {
            var walk = new Dictionary<string, double> { { "s", 300.0 } };
            var factor = new Dictionary<string, float> { { "s", 0.5f } };
            var atNow = StrategyStatePatcher.ResolveLedgerActiveAtNow(
                walk, factor, new[] { "s", "t" }, Rows(new Dictionary<string, StrategyCommittedRows>()),
                NoFactor, 500.0);
            Assert.Equal(300.0, atNow.ActivateUT["s"]);
            Assert.Equal(0.5f, atNow.Factor["s"]);
            Assert.False(atNow.ActivateUT.ContainsKey("t"));
        }

        [Fact]
        public void ResolveLedgerActiveAtNow_SameRowAsTheWalk_KeepsTheWalkFactorWhenTheLedgerHasNone()
        {
            var map = new Dictionary<string, StrategyCommittedRows>
            {
                { "s", R(new[] { 100.0 }, new double[0]) },
            };
            var walk = new Dictionary<string, double> { { "s", 100.0 } };
            var factor = new Dictionary<string, float> { { "s", 0.5f } };
            var atNow = StrategyStatePatcher.ResolveLedgerActiveAtNow(
                walk, factor, new[] { "s" }, Rows(map), NoFactor, 500.0);
            Assert.Equal(0.5f, atNow.Factor["s"]);
        }

        // catches: the resolved state feeding the plan wrongly (the end-to-end shape the
        // patch runs): on now -> activated, off now -> switched off.
        [Fact]
        public void ResolvedStateIntoComputePlan_ActivatesOnNow_DeactivatesOffNow()
        {
            var map = new Dictionary<string, StrategyCommittedRows>
            {
                { "on", R(new[] { 100.0, 900.0 }, new[] { 800.0 }) },
                { "off", R(new[] { 100.0, 900.0 }, new[] { 200.0 }) },
            };
            var walk = new Dictionary<string, double> { { "on", 900.0 }, { "off", 900.0 } };
            var managed = new[] { "on", "off" };
            var atNow = StrategyStatePatcher.ResolveLedgerActiveAtNow(
                walk, null, managed, Rows(map), NoFactor, 500.0);
            var plan = StrategyStatePatcher.ComputePlan(
                atNow.ActivateUT, id => Array.IndexOf(managed, id) >= 0,
                new HashSet<string> { "off" }, new HashSet<string> { "on", "off" }, 500.0,
                id => map[id].NextRowIsDeactivation(500.0));
            Assert.Equal(new[] { "on" }, plan.ToActivate);
            Assert.Equal(new[] { "off" }, plan.ToDeactivate);
            Assert.Empty(plan.FutureDated);
        }

        // catches: a player deactivation in flight filed untagged (it is the flight's own act)
        // or a duration expiry tagged to the live flight (lost at discard).
        [Theory]
        [InlineData(1000.0, 100.0, 1100.0, true)]
        [InlineData(1000.0, 100.0, 1200.0, true)]
        [InlineData(1000.0, 100.0, 1099.0, false)]
        [InlineData(0.0, 100.0, 1100.0, false)]
        [InlineData(double.NaN, 100.0, 1100.0, false)]
        [InlineData(1000.0, double.NaN, 1100.0, false)]
        public void IsStrategyExpiry_OnlyWhenTheLongestDurationHasRunOut(
            double longest, double activated, double now, bool expected)
        {
            Assert.Equal(expected, GameStateRecorder.IsStrategyExpiry(longest, activated, now));
        }
    }
}
