using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// SMALL-RECOVERY-FUNDS-ERASED-IN-REFLY: a vessel recovery worth under the recorder's
    /// funds threshold (cheap debris, an EVA kerbal, a small probe) must still reach the
    /// ledger as one exact <c>FundsEarning(Recovery)</c> row. Without the row the gain
    /// survives only as an untagged pool adjustment on a measured patch, and an
    /// authoritative patch (every patch of a Re-Fly session) writes the pool back to the
    /// ledger target and erases it. Also: a career with no funds pool (Science, Sandbox)
    /// queues no recovery pairing request, so the scene-switch eviction Warn never fires.
    ///
    /// <para>The cells drive the real recorder handler (<c>OnFundsChanged</c>, private,
    /// reached by reflection with a test UT source) into the real recovery pairing, then
    /// the pure stock pool core the way <c>KspStatePatcher.PatchFunds</c> runs it.</para>
    /// </summary>
    [Collection("Sequential")]
    public class SmallRecoveryFundsTests : IDisposable
    {
        private const uint RecoveredPid = 424242;
        private readonly List<string> logLines = new List<string>();

        public SmallRecoveryFundsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;

            GameStateRecorder.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = () => false;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            LedgerOrchestrator.SetResourceTrackingAvailabilityForTesting(
                fundsTracked: true, scienceTracked: null, reputationTracked: null);
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            StockPoolReference.ResetForTesting();
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            StockPoolReference.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        /// <summary>Drives the real private funds handler from <paramref name="oldFunds"/>
        /// to <paramref name="newFunds"/> under <paramref name="reason"/> at <paramref name="ut"/>.</summary>
        private static void FireFundsChanged(double oldFunds, double newFunds, TransactionReasons reason, double ut)
        {
            var recorder = new GameStateRecorder();
            FieldInfo lastFunds = typeof(GameStateRecorder).GetField("lastFunds",
                BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo onFundsChanged = typeof(GameStateRecorder).GetMethod("OnFundsChanged",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(lastFunds);
            Assert.NotNull(onFundsChanged);
            lastFunds.SetValue(recorder, oldFunds);
            GameStateRecorder.FundsChangedUtProviderForTesting = () => ut;
            onFundsChanged.Invoke(recorder, new object[] { newFunds, reason });
        }

        /// <summary>Runs <paramref name="body"/> between a patch class's real Prefix and Finalizer.</summary>
        private static void InsidePatch(Type patchType, Action body)
        {
            MethodInfo prefix = patchType.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo finalizer = patchType.GetMethod("Finalizer", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(prefix);
            Assert.NotNull(finalizer);
            var args = new object[] { false };
            prefix.Invoke(null, args);
            try { body(); }
            finally { finalizer.Invoke(null, new object[] { null, args[0] }); }
        }

        /// <summary>Stock's recovery payout: <c>Funding.onVesselRecoveryProcessing</c>'s AddFunds.</summary>
        private static void FirePayout(double oldFunds, double newFunds, double ut)
        {
            InsidePatch(typeof(Parsek.Patches.RecoveryPayoutScopePatch),
                () => FireFundsChanged(oldFunds, newFunds, TransactionReasons.VesselRecovery, ut));
        }

        /// <summary>A strategy's funds yield on a science query under VesselRecovery
        /// (Patents Licensing): <c>Funding.OnCurrenciesModified</c>, outside the payout.</summary>
        private static void FireStrategyYield(double oldFunds, double newFunds, double ut)
        {
            InsidePatch(typeof(Parsek.Patches.RecoveryFundsEffectScopePatch),
                () => FireFundsChanged(oldFunds, newFunds, TransactionReasons.VesselRecovery, ut));
        }

        /// <summary>Stock's recovery processing (the payout context) for one vessel.</summary>
        private static RecoveryPayoutContext RememberPayout(
            string vesselName, VesselType type, double ut, double before, double earned)
        {
            RecoveryPayoutContextStore.Remember(
                persistentId: RecoveredPid,
                rawVesselName: vesselName,
                vesselType: type,
                ut: ut,
                recoveryFactor: 1.0f,
                hasFundsEarned: true,
                fundsEarned: earned,
                beforeMissionFunds: before,
                totalFunds: before + earned);
            Assert.True(RecoveryPayoutContextStore.TryFind(
                RecoveredPid, RecoveredVesselIdentity.FromRawName(vesselName), ut,
                out RecoveryPayoutContext context));
            return context;
        }

        /// <summary>The stock order of one recovery: processing, the payout, then
        /// <c>onVesselRecovered</c> routed into the recovery pairing.</summary>
        private static void RecoverAtKsc(
            string vesselName, VesselType type, double ut, double before, double earned)
        {
            RecoveryPayoutContext context = RememberPayout(vesselName, type, ut, before, earned);
            FirePayout(before, before + earned, ut);
            LedgerOrchestrator.OnVesselRecoveryFunds(
                ut, RecoveredVesselIdentity.FromRawName(vesselName), quick: false, type, context);
        }

        private static List<GameAction> RecoveryRows() => Ledger.Actions
            .Where(a => a.Type == GameActionType.FundsEarning && a.FundsSource == FundsEarningSource.Recovery)
            .ToList();

        private static List<GameStateEvent> RecoveryEvents() => GameStateStore.Events
            .Where(e => e.eventType == GameStateEventType.FundsChanged
                        && e.key == LedgerOrchestrator.VesselRecoveryReasonKey)
            .ToList();

        private static Recording Rec(string id, string vesselName, MergeState state, double startUT, double endUT)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = vesselName,
                PreLaunchFunds = 50000.0,
                MergeState = state,
                TerminalStateValue = TerminalState.Orbiting
            };
            rec.Points.Add(new TrajectoryPoint { ut = startUT, funds = 1000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = endUT, funds = 1000.0 });
            return rec;
        }

        private static void InstallReFlySession(string provisionalId, string originId)
        {
            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = "sess_small",
                    TreeId = "tree_small",
                    ActiveReFlyRecordingId = provisionalId,
                    OriginChildRecordingId = originId,
                    SupersedeTargetId = originId,
                    RewindPointId = "rp_small",
                    InvokedUT = 0.0,
                    PreSessionBranchPointIds = new List<string>(),
                },
            });
        }

        /// <summary>
        /// One funds patch through the pure core, as <c>KspStatePatcher.PatchFunds</c> runs it:
        /// walk at <paramref name="now"/>, measure against the reference, write the target.
        /// </summary>
        private static double PatchFunds(
            FundsModule funds, List<GameAction> els, double now, double live, bool authoritative)
        {
            RecalculationEngine.Recalculate(els, now);
            var state = StockPoolReference.Funds;
            List<StockPoolRow> rows = StockPoolReference.BuildRows(StockPool.Funds, els, funds.GetRowEffects());
            double min = funds.GetProjectionMinBalance();
            var ids = new HashSet<string>(els.Select(a => a.ActionId), StringComparer.Ordinal);
            StockPoolMeasurement m = StockPoolReference.Measure(
                state, rows, now, live, 0.0, authoritative, 0.01, min, ids, clampAtZero: true);
            double write = m.GapAdopted
                ? live
                : StockPoolReference.ResolveCurrencyWriteTarget(
                    min, funds.GetRunningBalance(), live, false, m.Adjustment);
            StockPoolReference.Commit(state, rows, m, write, 0.0);
            return write;
        }

        // ------------------------------------------------------------------
        // The threshold decision
        // ------------------------------------------------------------------

        // Theory data passes the source as an int (the enum is internal to Parsek).
        private const int Payout = (int)RecoveryPayoutScope.FundsEventSource.Payout;
        private const int Effect = (int)RecoveryPayoutScope.FundsEventSource.StrategyEffect;
        private const int Unscoped = (int)RecoveryPayoutScope.FundsEventSource.Unscoped;

        [Theory]
        [InlineData(60.0, TransactionReasons.VesselRecovery, Payout, false)]
        [InlineData(0.5, TransactionReasons.VesselRecovery, Payout, false)]
        [InlineData(150.0, TransactionReasons.VesselRecovery, Payout, false)]
        [InlineData(0.0, TransactionReasons.VesselRecovery, Payout, true)]
        [InlineData(-60.0, TransactionReasons.VesselRecovery, Payout, true)]
        [InlineData(52.5, TransactionReasons.VesselRecovery, Effect, true)]
        [InlineData(150.0, TransactionReasons.VesselRecovery, Effect, false)]
        [InlineData(60.0, TransactionReasons.VesselRecovery, Unscoped, true)]
        [InlineData(150.0, TransactionReasons.VesselRecovery, Unscoped, false)]
        [InlineData(60.0, TransactionReasons.ContractReward, Payout, true)]
        [InlineData(-60.0, TransactionReasons.VesselRollout, Unscoped, true)]
        [InlineData(60.0, TransactionReasons.StrategyOutput, Unscoped, true)]
        [InlineData(150.0, TransactionReasons.ContractReward, Unscoped, false)]
        [InlineData(-150.0, TransactionReasons.VesselRollout, Unscoped, false)]
        public void IsFundsDeltaBelowThreshold_ExemptsOnlyStocksPositiveRecoveryPayout(
            double delta, TransactionReasons reason, int source, bool expectedBelow)
        {
            Assert.Equal(expectedBelow, GameStateRecorder.IsFundsDeltaBelowThreshold(
                delta, reason, (RecoveryPayoutScope.FundsEventSource)source));
        }

        [Theory]
        [InlineData(0, 0, Unscoped)]
        [InlineData(1, 0, Payout)]
        [InlineData(1, 1, Payout)]
        [InlineData(0, 1, Effect)]
        [InlineData(0, 2, Effect)]
        public void Classify_PayoutWinsOverItsOwnEffect(
            int payoutDepth, int effectDepth, int expected)
        {
            Assert.Equal((RecoveryPayoutScope.FundsEventSource)expected,
                RecoveryPayoutScope.Classify(payoutDepth, effectDepth));
        }

        [Fact]
        public void ScopePatches_TargetStocksPayoutAndFundsEffectMethods()
        {
            // The two private stock methods the scopes wrap (KSP 1.12.5 decompiled).
            Assert.NotNull(HarmonyLib.AccessTools.Method(typeof(Funding), "onVesselRecoveryProcessing",
                new[] { typeof(ProtoVessel), typeof(KSP.UI.Screens.MissionRecoveryDialog), typeof(float) }));
            Assert.NotNull(HarmonyLib.AccessTools.Method(typeof(Funding), "OnCurrenciesModified",
                new[] { typeof(CurrencyModifierQuery) }));
            InsidePatch(typeof(Parsek.Patches.RecoveryPayoutScopePatch), () =>
                Assert.Equal(Payout, (int)RecoveryPayoutScope.Current));
            InsidePatch(typeof(Parsek.Patches.RecoveryFundsEffectScopePatch), () =>
                Assert.Equal(Effect, (int)RecoveryPayoutScope.Current));
            Assert.Equal(Unscoped, (int)RecoveryPayoutScope.Current);
        }

        [Fact]
        public void SmallRecoveryReasonChange_OutsideThePayout_IsStillIgnored()
        {
            // Unscoped (a mod's own AddFunds, or the patches did not apply): the old floor.
            FireFundsChanged(1000.0, 1060.0, TransactionReasons.VesselRecovery, 500.0);
            Assert.Empty(RecoveryEvents());
        }

        [Fact]
        public void SmallNonRecoveryFundsChange_IsStillIgnoredByTheHandler()
        {
            FireFundsChanged(1000.0, 1060.0, TransactionReasons.ContractReward, 500.0);

            Assert.DoesNotContain(GameStateStore.Events, e => e.eventType == GameStateEventType.FundsChanged);
            Assert.Empty(Ledger.Actions);
            Assert.Contains(logLines, l =>
                l.Contains("[GameStateRecorder]") && l.Contains("Ignored FundsChanged")
                && l.Contains("ContractReward"));
        }

        // ------------------------------------------------------------------
        // Recorder -> pairing -> one row
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(60.0)]
        [InlineData(150.0)]
        public void Recovery_AtKsc_RecordsTheEvent_AndPairsExactlyOneRow(double earned)
        {
            RecordingStore.AddRecordingWithTreeForTesting(
                Rec("rec-debris", "Cheap Debris", MergeState.Immutable, 100.0, 400.0));

            RecoverAtKsc("Cheap Debris", VesselType.Debris, 500.0, 1000.0, earned);

            GameStateEvent evt = Assert.Single(RecoveryEvents());
            Assert.Equal(earned, evt.valueAfter - evt.valueBefore, 6);
            GameAction row = Assert.Single(RecoveryRows());
            Assert.Equal((float)earned, row.FundsAwarded);
            Assert.Equal("rec-debris", row.RecordingId);
            Assert.Equal(LedgerOrchestrator.BuildRecoveryEventDedupKey(evt), row.DedupKey);
            Assert.Equal(0, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("VesselRecovery funds patched")
                && l.Contains("Cheap Debris"));

            // Only the under-threshold payout says it bypassed the floor; 150 runs exactly
            // the path it always ran.
            bool bypassLogged = logLines.Any(l => l.Contains("recorded anyway"));
            Assert.Equal(earned < GameStateRecorder.FundsThreshold, bypassLogged);
            Assert.DoesNotContain(logLines, l => l.Contains("[WARN][LedgerOrchestrator]")
                                                 || l.Contains("[WARN][GameStateRecorder]"));
        }

        [Fact]
        public void SmallRecovery_CallbackBeforeThePayout_DefersThenPairsTheExactAmount()
        {
            // onVesselRecovered ahead of its FundsChanged: the request waits in the queue and
            // the small payout, now recorded, pairs it.
            RecoveryPayoutContext context = RememberPayout("Tiny Probe", VesselType.Probe, 700.0, 2000.0, 42.0);
            LedgerOrchestrator.OnVesselRecoveryFunds(
                700.0, RecoveredVesselIdentity.FromRawName("Tiny Probe"), quick: false, VesselType.Probe, context);
            Assert.Equal(1, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);

            FirePayout(2000.0, 2042.0, 700.0);

            GameAction row = Assert.Single(RecoveryRows());
            Assert.Equal(42f, row.FundsAwarded);
            Assert.Equal(0, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);
            LedgerOrchestrator.FlushStalePendingRecoveryFunds("scene switch");
            Assert.DoesNotContain(logLines, l => l.Contains("FlushStalePendingRecoveryFunds"));
        }

        // ------------------------------------------------------------------
        // The headline: an authoritative patch keeps the small payout
        // ------------------------------------------------------------------

        [Fact]
        public void SmallRecovery_InAReFlySession_AuthoritativePatchKeepsTheSixtyFunds()
        {
            InstallReFlySession("rec-provisional", "rec-origin");
            RecordingStore.AddRecordingWithTreeForTesting(
                Rec("rec-origin", "Reusable", MergeState.Immutable, 100.0, 900.0));
            RecordingStore.AddRecordingWithTreeForTesting(
                Rec("rec-provisional", "Reusable", MergeState.NotCommitted, 1000.0, 5000.0));

            // Every patch of a Re-Fly session is authoritative.
            Assert.True(LedgerOrchestrator.IsAuthoritativeReduction(
                isRewinding: false,
                hasReFlyMarker: ParsekScenario.Instance.ActiveReFlySessionMarker != null,
                hasMergeJournal: false, tombstonePath: false, rewindResourceAdjustmentInProgress: false));

            // The reference already patched the pool at the seed, before the recovery.
            RecalculationEngine.ClearModules();
            var funds = new FundsModule();
            RecalculationEngine.RegisterModule(funds, RecalculationEngine.ModuleTier.SecondTier);
            var seed = new GameAction
            {
                ActionId = "seedF", UT = 0, Type = GameActionType.FundsInitial, InitialFunds = 1000f,
            };
            Assert.Equal(1000.0, PatchFunds(funds, new List<GameAction> { seed }, 1500.0, 1000.0, false), 6);

            // Inside the attempt: 60-funds debris recovered, stock pays 1000 -> 1060.
            RecoverAtKsc("Reusable", VesselType.Debris, 2000.0, 1000.0, 60.0);
            RecalculationEngine.ClearModules();
            RecalculationEngine.RegisterModule(funds, RecalculationEngine.ModuleTier.SecondTier);

            var els = new List<GameAction> { seed };
            els.AddRange(EffectiveState.ComputeELS());
            double write = PatchFunds(funds, els, 2100.0, 1060.0, authoritative: true);

            // Without the row the authoritative write is the seed-only target, 1000.
            Assert.Equal(1060.0, write, 6);
            GameAction row = Assert.Single(RecoveryRows());
            Assert.Equal(60f, row.FundsAwarded);
            Assert.Equal("rec-provisional", row.RecordingId);
        }

        // ------------------------------------------------------------------
        // A strategy yield under VesselRecovery is never the payout
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(52.5)]
        [InlineData(150.0)]
        public void StrategyYield_CallbackFirst_DoesNotStealThePayoutPairing(double yield)
        {
            // Parsek's onVesselRecovered runs before stock's processing: a request with no
            // context waits. Then stock: the payout context (amount unknown), R&D's recovered
            // science under VesselRecovery yields funds through Patents Licensing
            // (Funding.OnCurrenciesModified), then Funding pays the vessel 1500.
            var id = RecoveredVesselIdentity.FromRawName("Sci Lander");
            LedgerOrchestrator.OnVesselRecoveryFunds(900.0, id, quick: false, VesselType.Lander, null);
            RecoveryPayoutContextStore.Remember(RecoveredPid, "Sci Lander", VesselType.Lander, 900.0, 1.0f,
                hasFundsEarned: true, fundsEarned: 0.0, beforeMissionFunds: 0.0, totalFunds: 0.0);
            FireStrategyYield(10000.0, 10000.0 + yield, 900.0);
            FirePayout(10000.0 + yield, 11500.0 + yield, 900.0);

            GameAction row = Assert.Single(RecoveryRows());
            Assert.Equal(1500f, row.FundsAwarded);
            Assert.Equal(0, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);
        }

        [Theory]
        [InlineData(52.5)]
        [InlineData(150.0)]
        public void StrategyYield_CallbackLast_DoesNotStealThePayoutPairing(double yield)
        {
            // Stock's recovery runs first: both events land, then Parsek's onVesselRecovered.
            var id = RecoveredVesselIdentity.FromRawName("Sci Lander");
            RecoveryPayoutContextStore.Remember(RecoveredPid, "Sci Lander", VesselType.Lander, 900.0, 1.0f,
                hasFundsEarned: true, fundsEarned: 0.0, beforeMissionFunds: 0.0, totalFunds: 0.0);
            FireStrategyYield(10000.0, 10000.0 + yield, 900.0);
            FirePayout(10000.0 + yield, 11500.0 + yield, 900.0);
            Assert.True(RecoveryPayoutContextStore.TryFind(RecoveredPid, id, 900.0, out RecoveryPayoutContext ctx));
            LedgerOrchestrator.OnVesselRecoveryFunds(900.0, id, quick: false, VesselType.Lander, ctx);

            GameAction row = Assert.Single(RecoveryRows());
            Assert.Equal(1500f, row.FundsAwarded);
            // The payout event carries the vessel; a recorded yield carries the not-payout stamp.
            GameStateEvent payout = RecoveryEvents().Single(e => e.valueAfter - e.valueBefore > 1000.0);
            Assert.True(RecoveryPayoutContextStore.ExtractIdentityFromFundsEventDetail(payout.detail).Matches(id));
            foreach (var e in RecoveryEvents().Where(e => e.valueAfter - e.valueBefore < 1000.0))
                Assert.True(RecoveryPayoutContextStore.IsNotPayoutFundsEventDetail(e.detail));
            Assert.Equal(yield >= GameStateRecorder.FundsThreshold ? 2 : 1, RecoveryEvents().Count);
        }

        [Fact]
        public void StrategyYieldOfAHundredOrMore_IsNotACommitTimeRecoveryRow()
        {
            // The commit-time producer uses the same finder: a recorded yield next to the
            // recording's end is not taken for a recovery.
            FireStrategyYield(10000.0, 10150.0, 900.0);
            Assert.Single(RecoveryEvents());
            Assert.False(LedgerRecoveryFundsPairing.TryFindRecoveryFundsEvent(
                GameStateStore.Events, 900.0, skipConsumed: false, out _, out _));
        }

        // ------------------------------------------------------------------
        // No funds pool: no pairing request, no eviction Warn
        // ------------------------------------------------------------------

        [Fact]
        public void Recovery_InACareerWithNoFundsPool_QueuesNoRequest_AndFlushStaysQuiet()
        {
            LedgerOrchestrator.SetResourceTrackingAvailabilityForTesting(
                fundsTracked: false, scienceTracked: null, reputationTracked: null);

            LedgerOrchestrator.OnVesselRecoveryFunds(
                800.0, RecoveredVesselIdentity.FromRawName("Science Lander"), quick: false,
                VesselType.Lander, payoutContext: null);

            Assert.Equal(0, LedgerOrchestrator.PendingRecoveryFundsCountForTesting);
            Assert.Empty(RecoveryRows());
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("Science Lander")
                && l.Contains("career has no funds pool"));

            LedgerOrchestrator.FlushStalePendingRecoveryFunds("scene switch");
            Assert.DoesNotContain(logLines, l => l.Contains("FlushStalePendingRecoveryFunds"));
            Assert.DoesNotContain(logLines, l => l.Contains("[WARN][LedgerOrchestrator]"));
        }
    }
}
