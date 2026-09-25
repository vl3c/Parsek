using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Todo STRATEGY-EXPIRY-REPLAY-DUPLICATE-DEACTIVATE-ROW, option c: the
    /// <c>Strategy.Update()</c> prefix applies a committed strategy deactivation before
    /// stock's per-frame expiry (KSPCommunityFixes' StrategyDuration fix) can replay it
    /// after a rewind, so no second StrategyDeactivate row is recorded.
    /// </summary>
    [Collection("Sequential")]
    public class StrategyExpiryReplayTests : IDisposable
    {
        private const string Committed = "rec-committed";
        private const string Uncommitted = "rec-live";

        private readonly List<string> logLines = new List<string>();
        private double now = 100.0;

        public StrategyExpiryReplayTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateStore.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            GameStateRecorder.IsReplayingActions = false;
            GameStateRecorder.SuppressResourceEvents = false;
            EffectiveState.ResetCachesForTesting();
            CommittedFutureIndexCache.ResetForTesting();
            StrategyExpiryGate.ResetForTesting();
            StrategyStatePatcher.ResetForTesting();
            CommittedFutureIndexCache.NowUtProviderForTesting = () => now;
            StrategyExpiryGate.IsCareerProviderForTesting = () => true;
            StrategyExpiryGate.AdministrationRefreshForTesting = () => { };
        }

        public void Dispose()
        {
            StrategyExpiryGate.ResetForTesting();
            CommittedFutureIndexCache.ResetForTesting();
            StrategyStatePatcher.ResetForTesting();
            GameStateRecorder.IsReplayingActions = false;
            GameStateRecorder.SuppressResourceEvents = false;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // ================================================================
        // Builders
        // ================================================================

        private static GameAction On(double ut, string id, string recordingId = null)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.StrategyActivate,
                StrategyId = id,
                RecordingId = recordingId,
                Commitment = 0.25f,
                SetupCost = 1000f
            };
        }

        private static GameAction Off(double ut, string id, string recordingId = null)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.StrategyDeactivate,
                StrategyId = id,
                RecordingId = recordingId
            };
        }

        private static CommittedFutureIndex Index(params GameAction[] rows)
        {
            return CommittedFutureIndex.Build(rows, id => id == Committed, id => null, null);
        }

        private static StrategyCommittedRows Rows(string id, params GameAction[] rows)
        {
            return Index(rows).StrategyRows(id);
        }

        private static StrategyStockUpdateDecision Decide(
            StrategyCommittedRows rows, double dateActivated, bool due, double nowUT, bool active = true)
        {
            return StrategyReservationPredicates.DecideStockUpdate(rows, active, dateActivated, due, nowUT);
        }

        /// <summary>A stock effect whose Unregister and Update hooks let a cell observe the
        /// apply and the held update.</summary>
        private sealed class ProbeEffect : Strategies.StrategyEffect
        {
            internal int Updates;
            internal int Unregisters;
            internal bool ReplayFlagAtUnregister;

            internal ProbeEffect(Strategies.Strategy parent) : base(parent) { }

            protected override void OnUnregister()
            {
                Unregisters++;
                ReplayFlagAtUnregister = GameStateRecorder.IsReplayingActions;
                // What a Deactivate() inside this scope would do: the capture postfix.
                StrategyDeactivatePatch.Postfix(Parent, true);
            }

            protected override void OnUpdate()
            {
                Updates++;
            }
        }

        /// <summary>
        /// A stock strategy object without Unity: the constructor is skipped and the fields
        /// the prefix reads are set directly. With no KSPCF in the test host, stock's
        /// <c>LongestDuration</c> reads <c>minLeastDuration</c> / <c>maxLeastDuration</c>
        /// (the stock bug KSPCF's getter prefix fixes), so those set the duration here.
        /// </summary>
        private static Strategies.Strategy MakeStrategy(
            string id, bool active, double dateActivated, double longestDuration, out ProbeEffect probe)
        {
            var type = typeof(Strategies.Strategy);
            var s = (Strategies.Strategy)FormatterServices.GetUninitializedObject(type);
            var config = (Strategies.StrategyConfig)FormatterServices.GetUninitializedObject(typeof(Strategies.StrategyConfig));
            typeof(Strategies.StrategyConfig).GetField("name", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(config, id);
            type.GetProperty("Config").SetValue(s, config);
            probe = new ProbeEffect(s);
            SetField(s, "effects", new List<Strategies.StrategyEffect> { probe });
            SetField(s, "isActive", active);
            SetField(s, "dateActivated", dateActivated);
            SetField(s, "factor", 0.5f);
            SetField(s, "minLeastDuration", longestDuration);
            SetField(s, "maxLeastDuration", longestDuration);
            return s;
        }

        private static void SetField(object target, string name, object value)
        {
            var f = typeof(Strategies.Strategy).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(f);
            f.SetValue(target, value);
        }

        private int DeactivationRows(string id)
        {
            return Ledger.Actions.Count(a => a.Type == GameActionType.StrategyDeactivate && a.StrategyId == id);
        }

        // ================================================================
        // The pure decision
        // ================================================================

        [Fact]
        public void Decide_CommittedOffAtNow_AppliesTheCommittedDeactivation()
        {
            var d = Decide(Rows("A", On(10.0, "A"), Off(110.0, "A")), 10.0, due: true, nowUT: 120.0);
            Assert.Equal(StrategyStockUpdateAction.ApplyCommittedDeactivation, d.Action);
            Assert.Equal(110.0, d.CommittedUT);
        }

        [Fact]
        public void Decide_CommittedPlayerDeactivationBeforeTheExpiry_AppliesOnTime()
        {
            // Mirror direction: a committed player cancel at 60, stock's expiry not due yet.
            var d = Decide(Rows("A", On(10.0, "A"), Off(60.0, "A")), 10.0, due: false, nowUT: 60.0);
            Assert.Equal(StrategyStockUpdateAction.ApplyCommittedDeactivation, d.Action);
            Assert.Equal(60.0, d.CommittedUT);
        }

        [Fact]
        public void Decide_CommittedOnAtNow_RunsStock()
        {
            var d = Decide(Rows("A", On(10.0, "A"), Off(110.0, "A")), 10.0, due: false, nowUT: 50.0);
            Assert.Equal(StrategyStockUpdateAction.RunStock, d.Action);
            Assert.Equal("committed-on", d.Reason);
        }

        [Fact]
        public void Decide_ReActivationAfterTheDeactivation_IsOnAtNow_NeverSwitchedOff()
        {
            var rows = Rows("A", On(10.0, "A"), Off(60.0, "A"), On(80.0, "A"));
            // Stock carries the re-activation.
            Assert.Equal(StrategyStockUpdateAction.RunStock, Decide(rows, 80.0, due: false, nowUT: 100.0).Action);
            // Stock still carries the first activation, but the committed timeline has it on now.
            Assert.Equal(StrategyStockUpdateAction.RunStock, Decide(rows, 10.0, due: false, nowUT: 100.0).Action);
            // A genuine expiry of the re-activation (no committed row ahead) is stock's.
            var expiry = Decide(rows, 80.0, due: true, nowUT: 200.0);
            Assert.Equal(StrategyStockUpdateAction.RunStock, expiry.Action);
            Assert.Equal("no-committed-row-ahead", expiry.Reason);
        }

        [Fact]
        public void Decide_BetweenDeactivationAndReActivation_AppliesTheDeactivation()
        {
            var rows = Rows("A", On(10.0, "A"), Off(60.0, "A"), On(80.0, "A"));
            var d = Decide(rows, 10.0, due: false, nowUT: 70.0);
            Assert.Equal(StrategyStockUpdateAction.ApplyCommittedDeactivation, d.Action);
            Assert.Equal(60.0, d.CommittedUT);
        }

        [Fact]
        public void Decide_StockActivationAtOrAfterTheCommittedDeactivation_IsLeftOn()
        {
            // A newer activation (e.g. uncommitted, in flight) the committed timeline does not end.
            var rows = Rows("A", On(10.0, "A"), Off(60.0, "A"));
            var later = Decide(rows, 65.0, due: false, nowUT: 70.0);
            Assert.Equal(StrategyStockUpdateAction.RunStock, later.Action);
            Assert.Equal("activated-after-committed-off", later.Reason);
            // The ambiguous tie reads as on.
            Assert.Equal(StrategyStockUpdateAction.RunStock, Decide(rows, 60.0, due: false, nowUT: 70.0).Action);
        }

        [Fact]
        public void Decide_UtBoundary_RowAtNowIsPast_OneTickEarlierHolds()
        {
            var rows = Rows("A", On(10.0, "A"), Off(110.0, "A"));
            Assert.Equal(StrategyStockUpdateAction.ApplyCommittedDeactivation,
                Decide(rows, 10.0, due: true, nowUT: 110.0).Action);
            var early = Decide(rows, 10.0, due: true, nowUT: 109.999);
            Assert.Equal(StrategyStockUpdateAction.HoldExpiryUntilCommittedDeactivation, early.Action);
            Assert.Equal(110.0, early.CommittedUT);
            // Not due: nothing to hold.
            Assert.Equal(StrategyStockUpdateAction.RunStock, Decide(rows, 10.0, due: false, nowUT: 109.999).Action);
        }

        [Fact]
        public void Decide_ReplayFramesAheadOfTheCommittedExpiryFrame_HoldThenApply()
        {
            // Threshold 10 + 100 = 110; the original run's frame landed at 110.7.
            var rows = Rows("A", On(10.0, "A"), Off(110.7, "A"));
            var hold = Decide(rows, 10.0, due: true, nowUT: 110.2);
            Assert.Equal(StrategyStockUpdateAction.HoldExpiryUntilCommittedDeactivation, hold.Action);
            Assert.Equal(110.7, hold.CommittedUT);
            Assert.Equal(StrategyStockUpdateAction.ApplyCommittedDeactivation,
                Decide(rows, 10.0, due: true, nowUT: 110.7).Action);
        }

        [Fact]
        public void Decide_NextCommittedRowIsAnActivation_DoesNotHold()
        {
            var d = Decide(Rows("A", On(10.0, "A"), On(200.0, "A")), 10.0, due: true, nowUT: 150.0);
            Assert.Equal(StrategyStockUpdateAction.RunStock, d.Action);
            Assert.Equal("committed-activation-ahead", d.Reason);
        }

        [Fact]
        public void Decide_UncommittedRecordingRows_AreIgnored()
        {
            // The deactivation is tagged to a recording that is not committed.
            var rows = Rows("A", On(10.0, "A"), Off(110.0, "A", Uncommitted));
            Assert.Equal(1, rows.Count);
            var d = Decide(rows, 10.0, due: true, nowUT: 120.0);
            Assert.Equal(StrategyStockUpdateAction.RunStock, d.Action);
            // A committed flight's row counts like a KSC row.
            var committed = Rows("A", On(10.0, "A"), Off(110.0, "A", Committed));
            Assert.Equal(StrategyStockUpdateAction.ApplyCommittedDeactivation,
                Decide(committed, 10.0, due: true, nowUT: 120.0).Action);
        }

        [Fact]
        public void Decide_UnmanagedStrategy_IsLeftAlone()
        {
            // The committed timeline never activated it (activated before the ledger).
            var onlyOff = Rows("A", Off(110.0, "A"));
            var d = Decide(onlyOff, 5.0, due: true, nowUT: 120.0);
            Assert.Equal(StrategyStockUpdateAction.RunStock, d.Action);
            Assert.Equal("unmanaged", d.Reason);
            Assert.Equal("unmanaged", Decide(null, 5.0, due: true, nowUT: 120.0).Reason);
            Assert.Equal("unmanaged", Decide(Rows("A", On(10.0, "B")), 5.0, due: true, nowUT: 120.0).Reason);
        }

        [Fact]
        public void Decide_StockInactive_RunsStock()
        {
            var d = Decide(Rows("A", On(10.0, "A"), Off(110.0, "A")), 10.0, due: true, nowUT: 120.0, active: false);
            Assert.Equal(StrategyStockUpdateAction.RunStock, d.Action);
            Assert.Equal("not-active", d.Reason);
        }

        // ================================================================
        // Committed rows: order, search, and the predicate shared with the state patch
        // ================================================================

        [Fact]
        public void Rows_SameUt_DeactivationSortsFirst_SoTheStateAfterTheTieIsOn()
        {
            var rows = Rows("A", On(50.0, "A"), Off(50.0, "A"));
            Assert.False(rows.IsActivation(0));
            Assert.True(rows.IsActivation(1));
            Assert.Equal(1, rows.LastAtOrBefore(60.0));
            Assert.Equal(-1, rows.LastAtOrBefore(49.0));
            Assert.Equal(0, rows.FirstAfter(49.0));
            Assert.Equal(-1, rows.FirstAfter(50.0));
            Assert.True(rows.NextRowIsDeactivation(49.0));
        }

        /// <summary>
        /// The prefix's hold and the state patch's FutureDeactivation read the same
        /// predicate; it answers exactly what the PR 5 FirstFutureRow reading answered.
        /// </summary>
        [Fact]
        public void Rows_NextRowIsDeactivation_MatchesFirstFutureRowAtEveryUt()
        {
            var actions = new[]
            {
                On(10.0, "A"), Off(60.0, "A"), On(60.0, "A"), Off(110.0, "A"), On(200.0, "A"),
                Off(250.0, "A", Uncommitted), On(10.0, "B")
            };
            var index = Index(actions);
            var rows = index.StrategyRows("A");
            for (double ut = 0.0; ut <= 300.0; ut += 5.0)
            {
                var next = StrategyReservationPredicates.FirstFutureRow(index, "A", ut);
                bool expected = next != null && next.Kind == CommittedFutureKind.StrategyDeactivate;
                Assert.Equal(expected, rows.NextRowIsDeactivation(ut));
            }
        }

        [Fact]
        public void StrategyRows_CachedPerIndexInstance_RebuiltWithTheIndex()
        {
            Ledger.AddAction(On(10.0, "A"));
            var first = CommittedFutureIndexCache.Current;
            var rowsA = first.StrategyRows("A");
            Assert.Same(first, CommittedFutureIndexCache.Current);
            Assert.Same(rowsA, CommittedFutureIndexCache.Current.StrategyRows("A"));
            Assert.Equal(1, CommittedFutureIndexCache.RebuildCount);
            Assert.False(rowsA.NextRowIsDeactivation(50.0));
            Assert.Null(first.StrategyRows(""));

            // A new committed row rebuilds the index, and with it the rows.
            Ledger.AddAction(Off(110.0, "A"));
            var second = CommittedFutureIndexCache.Current;
            Assert.NotSame(first, second);
            Assert.Equal(2, CommittedFutureIndexCache.RebuildCount);
            Assert.NotSame(rowsA, second.StrategyRows("A"));
            Assert.True(second.StrategyRows("A").NextRowIsDeactivation(50.0));
        }

        /// <summary>The rows restate the index's future boundary (a row AT now is past)
        /// to stay free of the index type; they must agree with IsFuture.</summary>
        [Fact]
        public void Rows_FutureBoundary_AgreesWithTheIndex()
        {
            var rows = new StrategyCommittedRows(new[] { 100.0 }, new double[0]);
            foreach (double now in new[] { 99.999, 100.0, 100.001 })
                Assert.Equal(CommittedFutureIndex.IsFuture(100.0, now), rows.FirstAfter(now) == 0);
        }

        // ================================================================
        // The live gate on a stock Strategy object
        // ================================================================

        /// <summary>
        /// The todo's repro: activated at 0, expired at the committed 110 (one
        /// StrategyDeactivate row), rewound to before it, and the replay's clock has passed
        /// the expiry with stock still active. The prefix switches it off silently: stock's
        /// Update is skipped, no row is appended, one deactivation row stands.
        /// </summary>
        [Fact]
        public void Repro_ReplayedExpiryAfterRewind_AppliedSilently_OneDeactivationRowTotal()
        {
            Ledger.AddAction(On(10.0, "A"));
            Ledger.AddAction(Off(110.0, "A"));
            ProbeEffect probe;
            var s = MakeStrategy("A", active: true, dateActivated: 10.0, longestDuration: 100.0, out probe);
            Assert.Equal(100.0, s.LongestDuration);
            now = 110.0;

            Assert.False(StrategyExpiryGate.ShouldRunStockUpdate(s));

            Assert.False(s.IsActive);
            Assert.Equal(1, probe.Unregisters);
            Assert.Equal(1, DeactivationRows("A"));
            Assert.Equal(2, Ledger.Actions.Count);
            Assert.Equal(0, GameStateStore.EventCount);
            Assert.False(GameStateRecorder.IsReplayingActions);
            Assert.Contains(logLines, l => l.Contains("[StrategyReservation]")
                && l.Contains("applied the committed deactivation of strategy=A")
                && l.Contains("committedUT=110") && l.Contains("nowUT=110"));
        }

        /// <summary>
        /// The apply runs under the replay suppression: a capture postfix fired inside it
        /// (what a <c>Deactivate()</c> would trigger) is suppressed and appends nothing.
        /// </summary>
        [Fact]
        public void Apply_RunsUnderCaptureSuppression_AppendsNoLedgerRowOrEvent()
        {
            Ledger.AddAction(On(10.0, "A"));
            Ledger.AddAction(Off(60.0, "A"));
            ProbeEffect probe;
            var s = MakeStrategy("A", active: true, dateActivated: 10.0, longestDuration: 0.0, out probe);
            now = 70.0;
            int before = Ledger.Actions.Count;

            Assert.False(StrategyExpiryGate.ShouldRunStockUpdate(s));

            Assert.True(probe.ReplayFlagAtUnregister);
            Assert.Contains(logLines, l => l.Contains("Deactivate postfix: suppressed during action replay"));
            Assert.Equal(before, Ledger.Actions.Count);
            Assert.Equal(0, GameStateStore.EventCount);
            Assert.False(GameStateRecorder.IsReplayingActions);
            Assert.False(GameStateRecorder.SuppressResourceEvents);
        }

        [Fact]
        public void Gate_ReplayFrameBeforeTheCommittedExpiryFrame_HoldsThenApplies()
        {
            Ledger.AddAction(On(10.0, "A"));
            Ledger.AddAction(Off(110.7, "A"));
            ProbeEffect probe;
            var s = MakeStrategy("A", active: true, dateActivated: 10.0, longestDuration: 100.0, out probe);

            now = 110.2;
            Assert.False(StrategyExpiryGate.ShouldRunStockUpdate(s));
            now = 110.5;
            Assert.False(StrategyExpiryGate.ShouldRunStockUpdate(s));
            Assert.True(s.IsActive);
            Assert.Equal(2, probe.Updates); // the rest of Update still runs while held
            Assert.Equal(1, logLines.Count(l => l.Contains("holding stock's expiry of strategy=A")));

            now = 110.7;
            Assert.False(StrategyExpiryGate.ShouldRunStockUpdate(s));
            Assert.False(s.IsActive);
            Assert.Equal(1, DeactivationRows("A"));
        }

        [Fact]
        public void Gate_GenuineExpiryInTheNewTimeline_RunsStock()
        {
            Ledger.AddAction(On(10.0, "A"));
            ProbeEffect probe;
            var s = MakeStrategy("A", active: true, dateActivated: 10.0, longestDuration: 100.0, out probe);
            now = 115.0;
            Assert.True(StrategyExpiryGate.ShouldRunStockUpdate(s));
            Assert.True(s.IsActive);
            Assert.Equal(0, probe.Unregisters);
            Assert.Contains(logLines, l => l.Contains("stock expiry of strategy=A proceeds (no-committed-row-ahead)"));
        }

        [Fact]
        public void Gate_UnmanagedStrategy_RunsStockBeforeReadingTheDuration()
        {
            Ledger.AddAction(Off(110.0, "A"));
            ProbeEffect probe;
            var s = MakeStrategy("A", active: true, dateActivated: 5.0, longestDuration: 100.0, out probe);
            now = 120.0;
            Assert.True(StrategyExpiryGate.ShouldRunStockUpdate(s));
            Assert.True(s.IsActive);
        }

        [Fact]
        public void Gate_ReplaySuppressionOrNotCareer_Bypasses()
        {
            Ledger.AddAction(On(10.0, "A"));
            Ledger.AddAction(Off(60.0, "A"));
            ProbeEffect probe;
            var s = MakeStrategy("A", active: true, dateActivated: 10.0, longestDuration: 0.0, out probe);
            now = 70.0;

            GameStateRecorder.IsReplayingActions = true;
            Assert.True(StrategyExpiryGate.ShouldRunStockUpdate(s));
            GameStateRecorder.IsReplayingActions = false;
            GameStateRecorder.SuppressResourceEvents = true;
            Assert.True(StrategyExpiryGate.ShouldRunStockUpdate(s));
            GameStateRecorder.SuppressResourceEvents = false;
            StrategyExpiryGate.IsCareerProviderForTesting = () => false;
            Assert.True(StrategyExpiryGate.ShouldRunStockUpdate(s));
            Assert.True(s.IsActive);

            StrategyExpiryGate.IsCareerProviderForTesting = () => true;
            Assert.False(StrategyExpiryGate.ShouldRunStockUpdate(s));
            Assert.False(s.IsActive);
        }

        // ================================================================
        // Patch target
        // ================================================================

        [Fact]
        public void Target_StrategyUpdate_IsThePublicParameterlessPerFrameMethod()
        {
            var m = StrategyUpdateExpiryPatch.ResolveTargetMethodForTesting();
            Assert.NotNull(m);
            Assert.Equal(typeof(Strategies.Strategy), m.DeclaringType);
            Assert.Equal("Update", m.Name);
            Assert.Empty(m.GetParameters());
            Assert.True(m.IsPublic);
            Assert.False(m.IsVirtual); // one patch covers every Strategy subclass
            Assert.Equal(typeof(void), ((MethodInfo)m).ReturnType);

            // The held update's reflection targets.
            Assert.NotNull(typeof(Strategies.Strategy).GetMethod("OnUpdate",
                BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null));
            Assert.NotNull(typeof(Strategies.Strategy).GetProperty("Effects"));
            Assert.NotNull(typeof(Strategies.StrategyEffect).GetMethod("Update", Type.EmptyTypes));

            var prefix = typeof(StrategyUpdateExpiryPatch).GetMethod("Prefix",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(prefix);
            Assert.Equal(typeof(bool), prefix.ReturnType);
        }
    }
}
