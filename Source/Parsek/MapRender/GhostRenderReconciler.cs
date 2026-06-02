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

        /// <summary>
        /// Wiring (called by the end-of-frame <see cref="MapRenderProbe"/>): if a fresh new-pipeline
        /// intent was recorded for <paramref name="pid"/> THIS frame (decision-only shadow producer),
        /// reconcile it against the old path's rendered truth and emit the matching anomaly. A
        /// visibility mismatch is the <c>gap-vs-retire</c> class; otherwise a treatment mismatch is
        /// the <c>decision-vs-old-truth</c> class. No fresh intent → no-op (nothing recorded the
        /// shadow intent this frame, e.g. before Phase 4 wiring is live). Gated by the
        /// <see cref="MapRenderTrace.TryGetFreshRenderIntent"/> store, itself off when tracing is off.
        /// </summary>
        internal static void CheckIntentAgainstOldTruth(
            uint pid, string pidKey, int currentFrame, double currentUT, double effUT,
            string actualLineActive, string actualDrawIcons, bool polylineOwns)
        {
            if (!MapRenderTrace.TryGetFreshRenderIntent(pidKey, currentFrame, out var rec))
                return;

            string visMismatch = ReconcileVisibility(rec.Visible, actualLineActive, actualDrawIcons, polylineOwns);
            if (!string.IsNullOrEmpty(visMismatch))
            {
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
                MapRenderTrace.EmitAnomaly(
                    MapRenderTrace.RenderSurface.ProtoOrbitLine, pidKey, currentUT, effUT,
                    "decision-vs-old-truth",
                    string.Format(CultureInfo.InvariantCulture,
                        "{0} intentDriveUT={1}",
                        trMismatch, MapRenderTrace.FormatDouble(rec.DriveUT, "F3")));
        }
    }
}
