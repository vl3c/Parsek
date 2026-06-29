using System.Collections.Generic;
using System.Globalization;

namespace Parsek.MapRender
{
    /// <summary>
    /// Phase 6 / design §6.8 (L5): the standing reconciliation layer for the new render pipeline.
    /// It does NOT re-implement the tracer; it REUSES <see cref="MapRenderTrace"/> as the emit sink
    /// and the frame-stamped intent store, and adds the new comparison the design wants: the
    /// first-class <see cref="GhostRenderIntent"/> against the OLD path's rendered truth.
    ///
    /// <para>This is the shadow-comparison signal Phase 4 needs (it lands BEFORE the scene wiring it
    /// verifies, per the plan). In decision-only shadow the scene writes nothing to the stock
    /// surfaces: it computes the intent, calls <see cref="NoteIntent"/> early in the frame, and the
    /// end-of-frame <see cref="MapRenderProbe"/> calls <see cref="CheckIntentAgainstOldTruth"/> to
    /// compare the recorded intent against what the old scattered coordination actually drew. A
    /// divergence is the bug class the rewrite exists to surface: it means the new single-owner
    /// Director would have rendered something different from today's path.</para>
    ///
    /// <para><b>Phase 8 (migration plan §10): the LIVE intent-vs-old-truth comparator is UNWIRED.</b>
    /// Through Phases 0-7 the end-of-frame <see cref="MapRenderProbe"/> called
    /// <see cref="CheckIntentAgainstOldTruth"/> to compare the new spine's intent against the OLD path's
    /// rendered truth. Once the spine drives the render (Phases 3-7), that OLD truth IS the spine's own
    /// consequence, so the comparison became CIRCULAR / self-confirming. The Phase-0 recorded-vs-rendered
    /// <c>RenderParityOracle</c> (a DISTINCT axis that coexisted since Phase 0) is now the SOLE acceptance
    /// oracle, so the probe call site was removed (a <c>scripts/grep-audit-render-reconciler-unwired.ps1</c>
    /// gate enforces zero LIVE <see cref="CheckIntentAgainstOldTruth"/> call sites under <c>Source/Parsek/</c>).
    /// This is an UNWIRING, not a rename/promote: the type, the pure predicates, and
    /// <see cref="CheckIntentAgainstOldTruth"/> itself are KEPT and stay exercised by
    /// <c>GhostRenderReconcilerTests</c>; only the production call site is gone.
    /// <see cref="NoteIntent"/> (the shadow PRODUCER side) is UNAFFECTED - it feeds the spine, not this
    /// retired comparator.</para>
    ///
    /// <para>The compare predicates are PURE (primitive-only, Unity-ECall-free) so they are unit
    /// testable; only <see cref="NoteIntent"/> / <see cref="CheckIntentAgainstOldTruth"/> touch the
    /// gated <see cref="MapRenderTrace"/> store/sink. The three new anomaly classes (design §6.8):
    ///   - <c>decision-vs-old-truth</c>: the new intent's TREATMENT disagrees with the old draw
    ///     (StockConic intent but the polyline owned the phase, or TracedPath intent but the stock
    ///     line/icon drew) — the swap/ownership divergence.
    ///   - <c>gap-vs-retire</c>: the new intent's VISIBILITY disagrees (the Director held the ghost
    ///     visible across an interior gap where the old path retired it, or vice versa) — §6.4/§10.7.
    ///   - <c>polyline-origin-shift</c>: on a floating-origin shift frame the polyline and the icon
    ///     shifted by materially different magnitudes (one surface was not re-projected against the
    ///     shared per-cycle origin) — §10.15. The pure predicate lands here; the probe wiring (it
    ///     needs the polyline's world position) lands with the Phase 4 scene adapter.
    /// </para>
    /// </summary>
    internal static class GhostRenderReconciler
    {
        /// <summary>Token for the new pipeline's treatment, matching the design's surface names.</summary>
        internal static string TreatmentToken(Treatment t)
        {
            switch (t)
            {
                case Treatment.StockConic: return "StockConic";
                case Treatment.TracedPath: return "TracedPath";
                default: return "None";
            }
        }

        /// <summary>Record the new pipeline's intent for a ghost this frame (shadow producer side).
        /// Delegates to the gated <see cref="MapRenderTrace"/> store, frame-stamped. No-op when the
        /// map-render tracing setting is off.</summary>
        internal static void NoteIntent(uint pid, GhostRenderIntent intent)
        {
            MapRenderTrace.RecordRenderIntent(pid, intent.Visible, TreatmentToken(intent.Treatment), intent.DriveUT);
        }

        // "True"/"False" (bool.ToString) parse to the bool; any other token (null/empty, or a
        // "(...)" sentinel such as "(field-missing)" / "(no-renderer)" / "(line-null)") is unknown
        // -> no signal, so the affected check no-ops until real truth is available.
        private static bool? ParseTriBool(string s)
        {
            if (s == "True") return true;
            if (s == "False") return false;
            return null;
        }

        private static bool IsUnknownToken(string s)
        {
            return string.IsNullOrEmpty(s) || s[0] == '(';
        }

        /// <summary>
        /// PURE visibility reconcile (the <c>gap-vs-retire</c> class). Fires only on a DEFINITE
        /// signal, never on an unknown token, so it cannot false-positive while truth is dormant:
        ///   - intent visible (Director held the ghost) but the old path drew DEFINITELY nothing
        ///     (line known off AND icons known NONE AND polyline not owning) → held-vs-retired;
        ///   - intent hidden (Director retired) but the old path was DEFINITELY drawing something →
        ///     retired-vs-drawing.
        /// Returns the mismatch token, or empty when consistent / indeterminate.
        /// </summary>
        internal static string ReconcileVisibility(
            bool intentVisible, string actualLineActive, string actualDrawIcons, bool polylineOwns)
        {
            bool? line = ParseTriBool(actualLineActive);
            bool iconsKnown = !IsUnknownToken(actualDrawIcons);
            bool iconsDrawing = iconsKnown && actualDrawIcons != "NONE";
            bool iconsOff = iconsKnown && actualDrawIcons == "NONE";

            bool stockDrawing = (line == true) || iconsDrawing;
            bool oldDrawingSomething = stockDrawing || polylineOwns;
            bool oldDefinitelyNothing = (line == false) && iconsOff && !polylineOwns;

            if (intentVisible && oldDefinitelyNothing)
                return "gap-vs-retire(intent=held-visible,old=retired)";
            if (!intentVisible && oldDrawingSomething)
                return "gap-vs-retire(intent=retired,old=" + (polylineOwns ? "polyline" : "stock") + ")";
            return string.Empty;
        }

        /// <summary>
        /// PURE treatment reconcile (the <c>decision-vs-old-truth</c> class). Call only when both the
        /// intent is visible AND the old path drew something (so a visibility mismatch is already
        /// ruled out by <see cref="ReconcileVisibility"/>). Fires only on an UNAMBIGUOUS treatment
        /// disagreement: StockConic intent while the polyline owned (and stock did not draw), or
        /// TracedPath intent while the stock line/icon drew (and the polyline did not own). Returns
        /// the mismatch token, or empty.
        /// </summary>
        internal static string ReconcileTreatment(
            string intentTreatment, string actualLineActive, string actualDrawIcons, bool polylineOwns)
        {
            bool? line = ParseTriBool(actualLineActive);
            bool iconsKnown = !IsUnknownToken(actualDrawIcons);
            bool iconsDrawing = iconsKnown && actualDrawIcons != "NONE";
            bool stockDrawing = (line == true) || iconsDrawing;

            if (intentTreatment == "StockConic")
            {
                if (polylineOwns && !stockDrawing)
                    return "decision-vs-old-truth(intent=StockConic,old=Polyline)";
            }
            else if (intentTreatment == "TracedPath")
            {
                if (stockDrawing && !polylineOwns)
                    return "decision-vs-old-truth(intent=TracedPath,old=StockConic)";
            }
            return string.Empty;
        }

        /// <summary>
        /// PURE <c>polyline-origin-shift</c> predicate. On a floating-origin shift frame both surfaces
        /// must shift by the same magnitude (both rebased against the shared per-cycle origin, §6.6),
        /// so the divergence is ~0. A divergence above <paramref name="divergenceToleranceMeters"/>
        /// means one surface (icon or polyline) was not re-projected — the jump §10.15 warns about.
        /// Only meaningful on a shift frame; off a shift frame the icon-jump predicate already covers
        /// teleports, so this returns false. NaN/Inf inputs return false.
        /// </summary>
        internal static bool IsPolylineOriginShiftJump(
            double polylineDeltaMeters, double iconDeltaMeters, bool originShiftFrame,
            double divergenceToleranceMeters)
        {
            if (!originShiftFrame)
                return false;
            if (double.IsNaN(polylineDeltaMeters) || double.IsInfinity(polylineDeltaMeters)
                || double.IsNaN(iconDeltaMeters) || double.IsInfinity(iconDeltaMeters))
                return false;
            double divergence = System.Math.Abs(polylineDeltaMeters - iconDeltaMeters);
            return divergence > System.Math.Max(0.0, divergenceToleranceMeters);
        }

        // Soft rate-limit on the intent-vs-old-truth anomaly LINES. Both the gap-vs-retire (visibility)
        // and decision-vs-old-truth (treatment) classes are PERSISTENT conditions: a sustained
        // divergence re-emits the byte-identical line every frame for the pid (this was the dominant
        // map-tracer log volume - ~2800 lines / 3 pids in a 3-minute session, one per frame). Throttle
        // the LINE to one per pid per class per second of WALL CLOCK (realtime), mirroring
        // MapRenderProbe's off-orbit / polyline-overlap limiters (the other persistent-condition
        // anomalies). Keyed per (pid, class) - separate dict per class - so a class transition still
        // emits immediately.
        //
        // The detailed window that drives the per-frame phase=Snapshot detail is refreshed SEPARATELY
        // (OpenDetailedWindow, on EVERY divergent frame - not only on emit frames). This matters because
        // the window is measured in UT while the line limiter is wall-clock: under time warp a 5 UT-second
        // window would lapse between 1 wall-second heartbeats and the snapshots would gap. Refreshing it
        // every frame the condition holds keeps the snapshot stream continuous at any warp (matching the
        // pre-fix every-frame EmitAnomaly), so no debugging signal is lost - only the redundant duplicate
        // LINES are dropped. Cleared on scene switch via the probe (and for tests); never populated at all
        // when tracing is off (CheckIntentAgainstOldTruth early-returns first).
        private const double IntentReconcileAnomalyMinIntervalSeconds = 1.0;
        private static readonly Dictionary<uint, double> lastGapVsRetireEmitRealtime =
            new Dictionary<uint, double>();
        private static readonly Dictionary<uint, double> lastDecisionVsOldTruthEmitRealtime =
            new Dictionary<uint, double>();

        private static bool PassesReconcileRateLimit(
            Dictionary<uint, double> store, uint pid, double realtime)
        {
            double last;
            if (store.TryGetValue(pid, out last)
                && realtime - last < IntentReconcileAnomalyMinIntervalSeconds)
                return false;
            store[pid] = realtime;
            return true;
        }

        /// <summary>Clear the per-pid intent-reconcile rate-limit timestamps. Called by
        /// <see cref="MapRenderProbe"/> on a scene switch (alongside its own per-pid clear) so a stale
        /// timestamp cannot suppress the first divergence after a TS &lt;-&gt; flight re-entry, and by
        /// tests to isolate the limiter between cases.</summary>
        internal static void ClearRateLimitState()
        {
            lastGapVsRetireEmitRealtime.Clear();
            lastDecisionVsOldTruthEmitRealtime.Clear();
        }

        /// <summary>
        /// <b>Phase 8: NO LONGER CALLED FROM PRODUCTION (the live probe call site was unwired - see the
        /// type-level note). KEPT and exercised by <c>GhostRenderReconcilerTests</c>.</b>
        /// Wiring (historically called by the end-of-frame <see cref="MapRenderProbe"/>): if a fresh
        /// new-pipeline intent was recorded for <paramref name="pid"/> THIS frame (decision-only shadow producer),
        /// reconcile it against the old path's rendered truth and emit the matching anomaly. A
        /// visibility mismatch is the <c>gap-vs-retire</c> class; otherwise a treatment mismatch is
        /// the <c>decision-vs-old-truth</c> class. No fresh intent → no-op (nothing recorded the
        /// shadow intent this frame, e.g. before Phase 4 wiring is live). Gated by the
        /// <see cref="MapRenderTrace.TryGetFreshRenderIntent"/> store, itself off when tracing is off.
        /// Each anomaly LINE is soft-rate-limited per (pid, class) via <paramref name="realtime"/>
        /// (see <see cref="IntentReconcileAnomalyMinIntervalSeconds"/>) so a persistent divergence does
        /// not flood the log one line per frame; the detailed window driving the per-frame snapshots is
        /// refreshed every divergent frame regardless, so no snapshot detail is lost under time warp.
        /// </summary>
        internal static void CheckIntentAgainstOldTruth(
            uint pid, string pidKey, int currentFrame, double currentUT, double effUT,
            string actualLineActive, string actualDrawIcons, bool polylineOwns, double realtime)
        {
            if (!MapRenderTrace.TryGetFreshRenderIntent(pidKey, currentFrame, out var rec))
                return;

            string visMismatch = ReconcileVisibility(rec.Visible, actualLineActive, actualDrawIcons, polylineOwns);
            if (!string.IsNullOrEmpty(visMismatch))
            {
                // Keep the detailed window open every divergent frame (cheap UT-keyed dict write, no log)
                // so the per-frame phase=Snapshot detail keeps flowing even under time warp; only the
                // anomaly LINE below is wall-clock rate-limited (one per pid per class per second).
                MapRenderTrace.OpenDetailedWindow(
                    pidKey, currentUT, MapRenderTrace.AnomalyWindowSeconds, "gap-vs-retire");
                if (PassesReconcileRateLimit(lastGapVsRetireEmitRealtime, pid, realtime))
                    MapRenderTrace.EmitAnomaly(
                        MapRenderTrace.RenderSurface.ProtoOrbitLine, pidKey, currentUT, effUT,
                        "gap-vs-retire",
                        string.Format(CultureInfo.InvariantCulture,
                            "{0} intentTreatment={1} intentDriveUT={2}",
                            visMismatch, rec.TreatmentToken,
                            MapRenderTrace.FormatDouble(rec.DriveUT, "F3")));
                return;
            }

            if (!rec.Visible)
                return; // hidden + old also nothing → consistent

            string trMismatch = ReconcileTreatment(rec.TreatmentToken, actualLineActive, actualDrawIcons, polylineOwns);
            if (!string.IsNullOrEmpty(trMismatch))
            {
                // Same window-refresh / line-rate-limit split as the gap-vs-retire branch above.
                MapRenderTrace.OpenDetailedWindow(
                    pidKey, currentUT, MapRenderTrace.AnomalyWindowSeconds, "decision-vs-old-truth");
                if (PassesReconcileRateLimit(lastDecisionVsOldTruthEmitRealtime, pid, realtime))
                    MapRenderTrace.EmitAnomaly(
                        MapRenderTrace.RenderSurface.ProtoOrbitLine, pidKey, currentUT, effUT,
                        "decision-vs-old-truth",
                        string.Format(CultureInfo.InvariantCulture,
                            "{0} intentDriveUT={1}",
                            trMismatch, MapRenderTrace.FormatDouble(rec.DriveUT, "F3")));
            }
        }
    }
}
