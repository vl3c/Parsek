using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Pure, Unity-free Step-1 helpers for the forward trajectory render feature
    /// (see docs/dev/plans/forward-trajectory-render.md). Computes the per-ghost
    /// forward render window <c>[currentElementStartUT, forwardStopUT]</c> from the
    /// EFFECTIVE (re-aim-resolved) <see cref="OrbitSegment"/> list, so the map / TS
    /// trajectory line can extend ahead of the icon up to the first hard stop.
    ///
    /// Kept in its own file (not TrajectoryMath.cs) so the forward-window concept
    /// stays decoupled from both the MapRender Director pipeline (where the live
    /// <c>GhostRenderChain</c> is private to <c>ShadowRenderDriver</c>) and the
    /// large TrajectoryMath surface — and so it is directly xUnit-testable.
    ///
    /// The helper is PURE by contract: it never touches KSP / FlightGlobals. The
    /// only KSP-coupled input — per-body gravitational parameter — is injected as a
    /// <see cref="Func{String, Double}"/> delegate (FlightGlobals-backed live,
    /// synthetic in tests). The live caller resolves the EFFECTIVE segment list via
    /// <c>GhostMapPresence.ResolveEffectiveMapOrbitSegments</c> before calling, so a
    /// re-aimed loop ghost's forward arcs are sourced from the re-aimed geometry,
    /// not the raw recorded segments (the (CRITICAL) re-aim sourcing requirement in
    /// the plan).
    /// </summary>
    internal static class ForwardRenderWindow
    {
        private const string Tag = "ForwardRenderWindow";

        /// <summary>
        /// True when <paramref name="seg"/> covers a complete revolution: it is
        /// elliptical (<c>ecc &lt; 1</c>) AND its span <c>(endUT - startUT)</c> is at
        /// least one orbital period <c>T = 2*pi*sqrt(a^3 / mu)</c>. A full-loop closed
        /// orbit terminates the forward chain (we never render a whole repeating
        /// ellipse). Hyperbolic / parabolic arcs (<c>ecc &gt;= 1</c>) are NEVER a full
        /// loop, regardless of span, and return false. A degenerate / non-finite
        /// <c>sma</c> or <c>mu</c> yields a non-finite period and also returns false
        /// (cannot prove a full loop, so do not stop the chain on it).
        /// </summary>
        /// <param name="seg">The orbit segment under test.</param>
        /// <param name="gravParameter">Reference body's GM (m^3/s^2) for the period.</param>
        internal static bool IsFullLoopClosedOrbit(OrbitSegment seg, double gravParameter)
        {
            // Hyperbolic / parabolic is never a closed loop.
            if (seg.eccentricity >= 1.0)
                return false;

            double period = ComputePeriod(seg.semiMajorAxis, gravParameter);

            // Degenerate / non-finite period (sma <= 0, mu <= 0, NaN/Inf): cannot
            // prove a full revolution — do not treat it as a chain-terminating loop.
            if (double.IsNaN(period) || double.IsInfinity(period) || period <= 0.0)
                return false;

            double span = seg.endUT - seg.startUT;
            return span >= period;
        }

        /// <summary>
        /// Orbital period <c>T = 2*pi*sqrt(a^3 / mu)</c>. Returns NaN for a
        /// non-positive / non-finite <paramref name="sma"/> or
        /// <paramref name="gravParameter"/> so <see cref="IsFullLoopClosedOrbit"/>
        /// can reject a degenerate orbit without a full-loop classification.
        /// </summary>
        internal static double ComputePeriod(double sma, double gravParameter)
        {
            if (double.IsNaN(sma) || double.IsInfinity(sma) || sma <= 0.0)
                return double.NaN;
            if (double.IsNaN(gravParameter) || double.IsInfinity(gravParameter) || gravParameter <= 0.0)
                return double.NaN;

            return 2.0 * Math.PI * Math.Sqrt((sma * sma * sma) / gravParameter);
        }

        /// <summary>
        /// Result of <see cref="ComputeForwardStopUT"/>: the forward render window
        /// <c>[CurrentElementStartUT, StopUT]</c> plus the index of the element the
        /// icon currently sits on and a reason describing which stop condition fired.
        /// When the icon is itself on a full-loop closed orbit, <c>StopUT ==
        /// CurrentElementStartUT</c> (empty forward range, current behaviour
        /// unchanged). <c>CurrentIndex == -1</c> means no element brackets the
        /// current UT (no forward window to draw).
        /// </summary>
        internal struct ForwardWindow
        {
            public int CurrentIndex;
            public double CurrentElementStartUT;
            public double StopUT;
            public ForwardStopReason Reason;

            public bool HasForwardRange => CurrentIndex >= 0 && StopUT > CurrentElementStartUT;
        }

        /// <summary>
        /// Why <see cref="ComputeForwardStopUT"/> terminated the forward chain.
        /// </summary>
        internal enum ForwardStopReason
        {
            /// <summary>No element brackets the current UT — no window.</summary>
            NoCurrentElement,
            /// <summary>Icon is already on a full-loop closed orbit; empty forward range.</summary>
            IconOnClosedOrbit,
            /// <summary>Reached the first full-loop closed orbit after the icon.</summary>
            FullLoopClosedOrbit,
            /// <summary>Reached the first body / SOI change after the icon.</summary>
            BodyChange,
            /// <summary>Walked to the end of the segment data with no earlier stop.</summary>
            EndOfData,
        }

        /// <summary>
        /// Convenience wrapper returning just the forward stop UT. The current
        /// element is tested FIRST; see <see cref="ComputeForwardWindow"/> for the
        /// full window + reason. Returns the current element's startUT (empty
        /// forward range) when the icon is on a full-loop closed orbit or no element
        /// brackets <paramref name="currentUT"/>.
        /// </summary>
        /// <param name="effectiveSegments">
        /// The EFFECTIVE (re-aim-resolved) orbit segment list, time-sorted. Live
        /// caller resolves it via <c>ResolveEffectiveMapOrbitSegments</c>; tests pass
        /// a synthetic list. NOT the raw <c>Recording.OrbitSegments</c> for re-aimed
        /// ghosts (the (CRITICAL) sourcing requirement).
        /// </param>
        /// <param name="currentUT">The UT the icon is at, in the same clock the
        /// segment startUT/endUT are expressed in.</param>
        /// <param name="muByBody">Per-body gravitational parameter delegate
        /// (pure-test seam; FlightGlobals-backed live). May be null — a null or
        /// throwing delegate yields a non-finite period, so no segment is classified
        /// a full loop and the chain stops only on body change / end-of-data.</param>
        internal static double ComputeForwardStopUT(
            IReadOnlyList<OrbitSegment> effectiveSegments,
            double currentUT,
            Func<string, double> muByBody)
        {
            ForwardWindow w = ComputeForwardWindow(effectiveSegments, currentUT, muByBody);
            return w.CurrentIndex < 0 ? currentUT : w.StopUT;
        }

        /// <summary>
        /// Walk the EFFECTIVE segment timeline forward from the element containing
        /// <paramref name="currentUT"/> and compute the forward render window.
        ///
        /// Order of operations (matches the plan Step 1):
        ///   1. Locate the CURRENT element (the segment bracketing currentUT; if none
        ///      brackets, the first segment that STARTS at or after currentUT — the
        ///      ghost sits in a gap just before it).
        ///   2. Test the CURRENT element FIRST: if it is itself a full-loop closed
        ///      orbit, return an empty forward range (StopUT == its startUT) so the
        ///      icon-on-closed-orbit case keeps current behaviour, unchanged.
        ///   3. Otherwise advance through later elements and stop at the EARLIEST of:
        ///        - the startUT of the first full-loop closed orbit after the icon,
        ///        - the first body / SOI change (a later element's bodyName differs
        ///          from the current element's bodyName — the next-SOI element is
        ///          EXCLUDED, so the stop is that element's startUT),
        ///        - end of the segment data (the last element's endUT).
        ///
        /// Predicted / extrapolated elements are NOT gated out: <c>isPredicted</c>
        /// segments are walked like any other; only the two stop conditions and
        /// end-of-data terminate the chain.
        /// </summary>
        internal static ForwardWindow ComputeForwardWindow(
            IReadOnlyList<OrbitSegment> effectiveSegments,
            double currentUT,
            Func<string, double> muByBody)
        {
            var window = new ForwardWindow
            {
                CurrentIndex = -1,
                CurrentElementStartUT = currentUT,
                StopUT = currentUT,
                Reason = ForwardStopReason.NoCurrentElement,
            };

            int count = effectiveSegments?.Count ?? 0;
            if (count == 0)
            {
                ParsekLog.VerboseRateLimited(Tag, "empty",
                    string.Format(CultureInfo.InvariantCulture,
                        "No effective segments at UT={0:F1} — no forward window", currentUT));
                return window;
            }

            // 1. Locate the current element. Prefer the segment bracketing currentUT;
            //    fall back to the first segment starting at/after currentUT (gap just
            //    before the next element). The list is assumed time-sorted by startUT.
            int currentIndex = LocateCurrentIndex(effectiveSegments, currentUT);
            if (currentIndex < 0)
            {
                ParsekLog.VerboseRateLimited(Tag, "nocur",
                    string.Format(CultureInfo.InvariantCulture,
                        "No element brackets/follows UT={0:F1} (segs={1}) — no forward window",
                        currentUT, count));
                return window;
            }

            OrbitSegment current = effectiveSegments[currentIndex];
            window.CurrentIndex = currentIndex;
            window.CurrentElementStartUT = current.startUT;

            double currentMu = SafeMu(muByBody, current.bodyName);

            // 2. Icon already on a full-loop closed orbit → empty forward range.
            if (IsFullLoopClosedOrbit(current, currentMu))
            {
                window.StopUT = current.startUT;
                window.Reason = ForwardStopReason.IconOnClosedOrbit;
                ParsekLog.VerboseRateLimited(Tag, "iconclosed",
                    string.Format(CultureInfo.InvariantCulture,
                        "Icon on full-loop closed orbit idx={0} body={1} sma={2:F0} ecc={3:F4} " +
                        "span={4:F1} — empty forward range (stopUT={5:F1})",
                        currentIndex, current.bodyName ?? "?", current.semiMajorAxis,
                        current.eccentricity, current.endUT - current.startUT, window.StopUT));
                return window;
            }

            // 3. Advance through later elements; stop at the earliest stop condition.
            //    Batch-counter convention: one summary line after the walk.
            string currentBody = current.bodyName;
            double stopUT = current.endUT; // default: end of the current element's data
            ForwardStopReason reason = ForwardStopReason.EndOfData;
            int walked = 0;
            int closedSkipped = 0; // later elements inspected before a stop fired

            for (int i = currentIndex + 1; i < count; i++)
            {
                OrbitSegment next = effectiveSegments[i];
                walked++;

                // First body / SOI change → stop, excluding the next-SOI element.
                if (BodyChanged(currentBody, next.bodyName))
                {
                    stopUT = next.startUT;
                    reason = ForwardStopReason.BodyChange;
                    break;
                }

                // First full-loop closed orbit after the icon → stop at its startUT.
                double nextMu = SafeMu(muByBody, next.bodyName);
                if (IsFullLoopClosedOrbit(next, nextMu))
                {
                    stopUT = next.startUT;
                    reason = ForwardStopReason.FullLoopClosedOrbit;
                    break;
                }

                // Same-body open / transfer arc: extend the window through it and
                // keep walking.
                stopUT = next.endUT;
                closedSkipped++;
            }

            window.StopUT = stopUT;
            window.Reason = reason;

            ParsekLog.VerboseRateLimited(Tag, "window",
                string.Format(CultureInfo.InvariantCulture,
                    "Forward window curIdx={0} body={1} curStart={2:F1} stopUT={3:F1} reason={4} " +
                    "walked={5} extendedArcs={6} segs={7} curUT={8:F1}",
                    currentIndex, currentBody ?? "?", window.CurrentElementStartUT, window.StopUT,
                    window.Reason, walked, closedSkipped, count, currentUT));

            return window;
        }

        /// <summary>
        /// Index of the element bracketing <paramref name="currentUT"/>
        /// (<c>startUT &lt;= currentUT &lt; endUT</c>), or — if the UT falls in a gap —
        /// the first element whose startUT is at/after currentUT (the ghost is just
        /// before that element). Returns -1 when currentUT is past the last element's
        /// endUT (nothing ahead). Assumes the list is time-sorted by startUT; a small
        /// per-ghost segment count makes a linear scan adequate.
        /// </summary>
        internal static int LocateCurrentIndex(IReadOnlyList<OrbitSegment> segments, double currentUT)
        {
            int count = segments?.Count ?? 0;
            for (int i = 0; i < count; i++)
            {
                OrbitSegment s = segments[i];
                // Bracketing element (inclusive start, exclusive end).
                if (currentUT >= s.startUT && currentUT < s.endUT)
                    return i;
                // Gap just before this element: currentUT sits before a future element
                // (and, by sort order, after every earlier element's window).
                if (currentUT < s.startUT)
                    return i;
            }
            // currentUT is at/after the last element's endUT — nothing ahead.
            return -1;
        }

        /// <summary>
        /// SOI / body-change predicate: two consecutive elements with different
        /// (non-null) <c>bodyName</c>. A null/empty body on either side is treated as
        /// "no change" (cannot prove a crossing), matching the recorder contract that
        /// always stamps a body name on real segments.
        /// </summary>
        internal static bool BodyChanged(string currentBody, string nextBody)
        {
            if (string.IsNullOrEmpty(currentBody) || string.IsNullOrEmpty(nextBody))
                return false;
            return !string.Equals(currentBody, nextBody, StringComparison.Ordinal);
        }

        /// <summary>
        /// Resolve a body's gravitational parameter through the injected delegate,
        /// tolerating a null delegate / null body / a delegate that throws or returns
        /// a non-finite value (all yield NaN, so the period is non-finite and the
        /// segment is never classified a full loop).
        /// </summary>
        private static double SafeMu(Func<string, double> muByBody, string bodyName)
        {
            if (muByBody == null || string.IsNullOrEmpty(bodyName))
                return double.NaN;
            try
            {
                return muByBody(bodyName);
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited(Tag, "muthrow",
                    string.Format(CultureInfo.InvariantCulture,
                        "muByBody({0}) threw {1} — treating as non-finite",
                        bodyName, ex.GetType().Name));
                return double.NaN;
            }
        }
    }
}
