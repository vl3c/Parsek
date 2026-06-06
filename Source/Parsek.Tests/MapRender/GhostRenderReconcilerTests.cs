using System.Collections.Generic;
using Parsek;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-6 guard for the standing reconciler (design §6.8): the PURE intent-vs-old-truth compare
    /// predicates plus the wiring that emits the anomaly lines. This is the shadow-comparison signal
    /// Phase 4 depends on, so it must land correct before the scene wiring it verifies.
    ///
    /// What makes each test fail:
    ///  - a visibility-class (gap-vs-retire) divergence is missed or flagged on indeterminate truth;
    ///  - a treatment-class (decision-vs-old-truth) divergence is missed or false-positives;
    ///  - the origin-shift predicate fires off a shift frame / on a uniform shift;
    ///  - a stale intent (decided on a different frame) is reconciled against this frame's truth;
    ///  - the anomaly LOG LINE loses its reason/detail fields (the only debugging signal here).
    ///
    /// Touches shared MapRenderTrace + ParsekLog static state → Sequential collection.
    /// </summary>
    [Collection("Sequential")]
    public class GhostRenderReconcilerTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private const int Frame = 42;
        private const uint Pid = 7u;
        private static string PidKey => Pid.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public GhostRenderReconcilerTests()
        {
            MapRenderTrace.Reset();
            GhostRenderReconciler.ClearRateLimitState();
            MapRenderTrace.ForceEnabledForTesting = true;
            MapRenderTrace.FrameCounterOverrideForTesting = () => Frame;
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            MapRenderTrace.Reset();
            GhostRenderReconciler.ClearRateLimitState();
            MapRenderTrace.ForceEnabledForTesting = false;
            MapRenderTrace.FrameCounterOverrideForTesting = null;
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ---- Pure: ReconcileVisibility (gap-vs-retire class) ----

        [Fact]
        public void Visibility_HeldVisibleButOldDrewNothing_FlagsGapVsRetire()
        {
            // Director held the ghost across an interior gap; the old path retired it.
            string m = GhostRenderReconciler.ReconcileVisibility(
                intentVisible: true, actualLineActive: "False", actualDrawIcons: "NONE", polylineOwns: false);
            Assert.Contains("gap-vs-retire", m);
            Assert.Contains("intent=held-visible,old=retired", m);
        }

        [Fact]
        public void Visibility_RetiredButOldStillDrawing_FlagsGapVsRetire()
        {
            string m = GhostRenderReconciler.ReconcileVisibility(
                intentVisible: false, actualLineActive: "True", actualDrawIcons: "ALL", polylineOwns: false);
            Assert.Contains("gap-vs-retire", m);
            Assert.Contains("old=stock", m);
        }

        [Fact]
        public void Visibility_UnknownTruth_NoFalsePositive()
        {
            // Truth dormant ("(field-missing)" / "(no-renderer)") → indeterminate → never flag.
            Assert.Equal("", GhostRenderReconciler.ReconcileVisibility(
                true, "(field-missing)", "(no-renderer)", polylineOwns: false));
            Assert.Equal("", GhostRenderReconciler.ReconcileVisibility(
                false, "(field-missing)", "(no-renderer)", polylineOwns: false));
        }

        [Fact]
        public void Visibility_Consistent_Empty()
        {
            // visible + old drawing stock → consistent visibility.
            Assert.Equal("", GhostRenderReconciler.ReconcileVisibility(true, "True", "ALL", false));
            // hidden + old definitely nothing → consistent.
            Assert.Equal("", GhostRenderReconciler.ReconcileVisibility(false, "False", "NONE", false));
        }

        // ---- Pure: ReconcileTreatment (decision-vs-old-truth class) ----

        [Fact]
        public void Treatment_StockConicIntentButPolylineDrew_FlagsDecisionVsTruth()
        {
            string m = GhostRenderReconciler.ReconcileTreatment("StockConic", "False", "NONE", polylineOwns: true);
            Assert.Contains("decision-vs-old-truth", m);
            Assert.Contains("intent=StockConic,old=Polyline", m);
        }

        [Fact]
        public void Treatment_TracedPathIntentButStockDrew_FlagsDecisionVsTruth()
        {
            string m = GhostRenderReconciler.ReconcileTreatment("TracedPath", "True", "ALL", polylineOwns: false);
            Assert.Contains("decision-vs-old-truth", m);
            Assert.Contains("intent=TracedPath,old=StockConic", m);
        }

        [Fact]
        public void Treatment_Agreeing_Empty()
        {
            // StockConic intent + stock drew + polyline not owning → agree.
            Assert.Equal("", GhostRenderReconciler.ReconcileTreatment("StockConic", "True", "ALL", false));
            // TracedPath intent + polyline owns + stock not drawing → agree.
            Assert.Equal("", GhostRenderReconciler.ReconcileTreatment("TracedPath", "False", "NONE", true));
        }

        // ---- Pure: IsPolylineOriginShiftJump ----

        [Fact]
        public void OriginShift_DivergentShiftOnShiftFrame_True()
        {
            // icon rebased by 1000 km, polyline not re-projected (delta ~0) → divergence ~1000 km.
            Assert.True(GhostRenderReconciler.IsPolylineOriginShiftJump(
                polylineDeltaMeters: 0.0, iconDeltaMeters: 1_000_000.0,
                originShiftFrame: true, divergenceToleranceMeters: 1000.0));
        }

        [Fact]
        public void OriginShift_UniformShift_False()
        {
            // both surfaces rebased by the same magnitude → divergence ~0 → not a jump.
            Assert.False(GhostRenderReconciler.IsPolylineOriginShiftJump(
                1_000_000.0, 1_000_050.0, originShiftFrame: true, divergenceToleranceMeters: 1000.0));
        }

        [Fact]
        public void OriginShift_NotAShiftFrame_False()
        {
            Assert.False(GhostRenderReconciler.IsPolylineOriginShiftJump(
                0.0, 1_000_000.0, originShiftFrame: false, divergenceToleranceMeters: 1000.0));
        }

        // ---- Wiring + log-assertion: CheckIntentAgainstOldTruth emits the right anomaly line ----

        private static GhostRenderIntent VisibleIntent(Treatment t, double driveUT = 5.0)
            => new GhostRenderIntent(true, t, driveUT, "Kerbin",
                t == Treatment.StockConic ? SegmentPayload.ForConic(default(OrbitSegment)) : SegmentPayload.Traced, "GhostX");

        [Fact]
        public void Check_GapVsRetire_EmitsAnomalyLine()
        {
            GhostRenderReconciler.NoteIntent(Pid, VisibleIntent(Treatment.StockConic));
            GhostRenderReconciler.CheckIntentAgainstOldTruth(
                Pid, PidKey, Frame, currentUT: 100.0, effUT: 100.0,
                actualLineActive: "False", actualDrawIcons: "NONE", polylineOwns: false, realtime: 1000.0);

            Assert.Contains(logLines, l =>
                l.Contains("[MapRenderTrace]") && l.Contains("phase=Anomaly")
                && l.Contains("reason=gap-vs-retire") && l.Contains("intent=held-visible,old=retired"));
        }

        [Fact]
        public void Check_DecisionVsOldTruth_EmitsAnomalyLine()
        {
            GhostRenderReconciler.NoteIntent(Pid, VisibleIntent(Treatment.StockConic));
            GhostRenderReconciler.CheckIntentAgainstOldTruth(
                Pid, PidKey, Frame, 100.0, 100.0,
                actualLineActive: "False", actualDrawIcons: "NONE", polylineOwns: true, realtime: 1000.0);

            Assert.Contains(logLines, l =>
                l.Contains("[MapRenderTrace]") && l.Contains("phase=Anomaly")
                && l.Contains("reason=decision-vs-old-truth")
                && l.Contains("intent=StockConic,old=Polyline"));
        }

        [Fact]
        public void Check_Consistent_EmitsNoAnomaly()
        {
            GhostRenderReconciler.NoteIntent(Pid, VisibleIntent(Treatment.StockConic));
            GhostRenderReconciler.CheckIntentAgainstOldTruth(
                Pid, PidKey, Frame, 100.0, 100.0,
                actualLineActive: "True", actualDrawIcons: "ALL", polylineOwns: false, realtime: 1000.0);

            Assert.DoesNotContain(logLines, l => l.Contains("phase=Anomaly")
                && (l.Contains("reason=gap-vs-retire") || l.Contains("reason=decision-vs-old-truth")));
        }

        [Fact]
        public void Check_StaleIntent_IsDropped_NotReconciled()
        {
            GhostRenderReconciler.NoteIntent(Pid, VisibleIntent(Treatment.StockConic)); // stamped at Frame=42
            // Reconcile on a LATER frame → freshness (same-frame-only) fails → no anomaly.
            GhostRenderReconciler.CheckIntentAgainstOldTruth(
                Pid, PidKey, currentFrame: Frame + 5, currentUT: 100.0, effUT: 100.0,
                actualLineActive: "False", actualDrawIcons: "NONE", polylineOwns: false, realtime: 1000.0);

            Assert.DoesNotContain(logLines, l => l.Contains("phase=Anomaly")
                && (l.Contains("reason=gap-vs-retire") || l.Contains("reason=decision-vs-old-truth")));
        }

        // ---- Rate-limit: a persistent divergence emits onset + heartbeat, not one line per frame ----

        private int AnomalyCount(string reason)
            => logLines.FindAll(l => l.Contains("phase=Anomaly") && l.Contains("reason=" + reason)).Count;

        [Fact]
        public void Check_GapVsRetire_RepeatWithinInterval_SuppressesDuplicate()
        {
            // Same divergence on consecutive frames within 1 s: the byte-identical line must not repeat.
            for (int i = 0; i < 5; i++)
            {
                GhostRenderReconciler.NoteIntent(Pid, VisibleIntent(Treatment.StockConic));
                GhostRenderReconciler.CheckIntentAgainstOldTruth(
                    Pid, PidKey, Frame, 100.0, 100.0,
                    actualLineActive: "False", actualDrawIcons: "NONE", polylineOwns: false,
                    realtime: 1000.0 + i * 0.1); // 5 frames spanning 0.4 s < 1 s interval
            }

            Assert.Equal(1, AnomalyCount("gap-vs-retire"));
        }

        [Fact]
        public void Check_GapVsRetire_AfterInterval_EmitsAgain()
        {
            // Onset at t=1000, heartbeat after the 1 s interval elapses: two lines, full onset+persistence
            // signal preserved (the suppressed frames in between still feed the open detailed window).
            GhostRenderReconciler.NoteIntent(Pid, VisibleIntent(Treatment.StockConic));
            GhostRenderReconciler.CheckIntentAgainstOldTruth(
                Pid, PidKey, Frame, 100.0, 100.0,
                actualLineActive: "False", actualDrawIcons: "NONE", polylineOwns: false, realtime: 1000.0);

            GhostRenderReconciler.NoteIntent(Pid, VisibleIntent(Treatment.StockConic));
            GhostRenderReconciler.CheckIntentAgainstOldTruth(
                Pid, PidKey, Frame, 100.0, 100.0,
                actualLineActive: "False", actualDrawIcons: "NONE", polylineOwns: false, realtime: 1001.5);

            Assert.Equal(2, AnomalyCount("gap-vs-retire"));
        }

        [Fact]
        public void Check_RateLimit_IsPerClass_ClassTransitionEmitsImmediately()
        {
            // gap-vs-retire at t=1000, then a DIFFERENT class (decision-vs-old-truth) 0.2 s later. The
            // per-class keying must let the new class emit immediately rather than sharing the gap limiter.
            GhostRenderReconciler.NoteIntent(Pid, VisibleIntent(Treatment.StockConic));
            GhostRenderReconciler.CheckIntentAgainstOldTruth(
                Pid, PidKey, Frame, 100.0, 100.0,
                actualLineActive: "False", actualDrawIcons: "NONE", polylineOwns: false, realtime: 1000.0);

            GhostRenderReconciler.NoteIntent(Pid, VisibleIntent(Treatment.StockConic));
            GhostRenderReconciler.CheckIntentAgainstOldTruth(
                Pid, PidKey, Frame, 100.0, 100.0,
                actualLineActive: "False", actualDrawIcons: "NONE", polylineOwns: true, realtime: 1000.2);

            Assert.Equal(1, AnomalyCount("gap-vs-retire"));
            Assert.Equal(1, AnomalyCount("decision-vs-old-truth"));
        }

        [Fact]
        public void Check_RateLimit_IsPerPid_OtherPidNotSuppressed()
        {
            // Two distinct pids diverging on the same frame must each emit; the limiter is keyed per pid.
            const uint OtherPid = 99u;
            string otherKey = OtherPid.ToString(System.Globalization.CultureInfo.InvariantCulture);

            GhostRenderReconciler.NoteIntent(Pid, VisibleIntent(Treatment.StockConic));
            GhostRenderReconciler.CheckIntentAgainstOldTruth(
                Pid, PidKey, Frame, 100.0, 100.0,
                actualLineActive: "False", actualDrawIcons: "NONE", polylineOwns: false, realtime: 1000.0);

            GhostRenderReconciler.NoteIntent(OtherPid, VisibleIntent(Treatment.StockConic));
            GhostRenderReconciler.CheckIntentAgainstOldTruth(
                OtherPid, otherKey, Frame, 100.0, 100.0,
                actualLineActive: "False", actualDrawIcons: "NONE", polylineOwns: false, realtime: 1000.0);

            Assert.Equal(2, AnomalyCount("gap-vs-retire"));
        }
    }
}
