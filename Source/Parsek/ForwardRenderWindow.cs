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
            // The render RUN is the UT interval [RunStartUT, StopUT] used by the leg/arc overlap tests.
            // RunStartUT is the BACKWARD boundary (the previous full-loop closed orbit's endUT, or the
            // previous SOI-change boundary), or double.NegativeInfinity when the run reaches the start of the
            // trajectory data (so EVERY earlier non-orbital leg — e.g. the whole ascent before the first
            // orbit segment — is included). StopUT is the FORWARD boundary (the next full-loop / SOI element's
            // startUT), or double.PositiveInfinity when the run reaches the end of data. Past + current +
            // future elements of the run are all drawn and PERSIST as the icon advances; the line resets only
            // when the icon crosses a boundary (enters a full-loop closed orbit, or changes SOI).
            public double RunStartUT;
            public double StopUT;
            public ForwardStopReason Reason;

            // A run exists (something to draw) unless the icon is itself ON a full-loop closed orbit (the
            // ellipse is drawn by stock and the line clears) or no element brackets/follows the icon.
            public bool HasForwardRange =>
                CurrentIndex >= 0 && Reason != ForwardStopReason.IconOnClosedOrbit && StopUT > RunStartUT;
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
            /// <summary>Reached the destination body's first full-loop closed orbit AFTER
            /// crossing exactly one SOI (the capture / parking ellipse). The window
            /// therefore spans the seam: the icon and the stop are in DIFFERENT SOIs, so
            /// readers MUST NOT infer "icon and stop share an SOI" from this reason.</summary>
            DestinationClosedOrbit,
            /// <summary>Reached a SECOND SOI change after one was already crossed (the
            /// single-SOI cap; e.g. Kerbin-&gt;Sun-&gt;Duna stops at the Sun-&gt;Duna seam
            /// rather than running the whole heliocentric transfer). The window spans one
            /// seam, like <see cref="DestinationClosedOrbit"/>.</summary>
            SecondSoiBound,
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
        /// Backward-boundary walk shared by the bracketed and past-end paths: the run start is the
        /// previous full-loop closed orbit's endUT, or the first same-SOI element's startUT after a
        /// body change, or -inf (start of trajectory data) when no boundary exists. Walks from
        /// <paramref name="fromIndex"/> down; <paramref name="fromIndex"/> itself is INCLUDED in the
        /// walk (pass currentIndex-1 to exclude the current element, currentIndex to include it -
        /// the past-end case, where the last element is BEHIND the icon and may itself be the
        /// boundary).
        /// </summary>
        private static double WalkBackwardBoundary(
            IReadOnlyList<OrbitSegment> effectiveSegments, int fromIndex, string currentBody,
            Func<string, double> muByBody, out int walkedBack)
        {
            walkedBack = 0;
            for (int i = fromIndex; i >= 0; i--)
            {
                OrbitSegment prev = effectiveSegments[i];
                walkedBack++;
                if (BodyChanged(currentBody, prev.bodyName))
                    return effectiveSegments[i + 1].startUT;
                if (IsFullLoopClosedOrbit(prev, SafeMu(muByBody, prev.bodyName)))
                    return prev.endUT;
                // prev is part of the run; keep walking.
            }
            return double.NegativeInfinity;
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
        ///   3. Otherwise advance through later elements, SPANNING exactly one SOI
        ///      transition into the destination body (so a Kerbin→Mun transfer draws
        ///      the Mun-approach arcs ahead of the icon while it is still Kerbin-side),
        ///      and stop at the EARLIEST of:
        ///        - the destination body's first full-loop closed orbit after crossing
        ///          one SOI (the capture / parking ellipse → reason
        ///          <see cref="ForwardStopReason.DestinationClosedOrbit"/>),
        ///        - a full-loop closed orbit in the SAME SOI as the icon, before any SOI
        ///          crossing (reason <see cref="ForwardStopReason.FullLoopClosedOrbit"/>),
        ///        - a SECOND SOI change after one was already crossed — the single-SOI
        ///          cap, so an interplanetary Kerbin→Sun→Duna walk stops at the second
        ///          seam instead of the unbounded heliocentric transfer (reason
        ///          <see cref="ForwardStopReason.SecondSoiBound"/>),
        ///        - end of the segment data (StopUT = +inf, reason
        ///          <see cref="ForwardStopReason.EndOfData"/>).
        ///      The SOI walk is bounded by an explicit counter capped at ONE crossing.
        ///
        /// Predicted / extrapolated elements are NOT gated out: <c>isPredicted</c>
        /// segments are walked like any other; only the stop conditions and end-of-data
        /// terminate the chain.
        /// </summary>
        internal static ForwardWindow ComputeForwardWindow(
            IReadOnlyList<OrbitSegment> effectiveSegments,
            double currentUT,
            Func<string, double> muByBody,
            double dataEndUT = double.NegativeInfinity)
        {
            var window = new ForwardWindow
            {
                CurrentIndex = -1,
                CurrentElementStartUT = currentUT,
                RunStartUT = currentUT,
                StopUT = currentUT,
                Reason = ForwardStopReason.NoCurrentElement,
            };

            int count = effectiveSegments?.Count ?? 0;
            if (count == 0)
            {
                ParsekLog.VerboseRateLimited(Tag, "empty",
                    string.Format(CultureInfo.InvariantCulture,
                        "No effective segments at UT={0:F1} — no render run", currentUT));
                return window;
            }

            // 1. Locate the current element. Prefer the segment bracketing currentUT;
            //    fall back to the first segment starting at/after currentUT (gap just
            //    before the next element). The list is assumed time-sorted by startUT.
            int currentIndex = LocateCurrentIndex(effectiveSegments, currentUT);
            if (currentIndex < 0)
            {
                // PAST END (review MAJOR-1): the icon is past the LAST element's endUT. When it is
                // still within the recorded data (a TRAILING leg past the last conic - the final
                // landing descent of a recording whose conics end before touchdown), the run is
                // STILL ALIVE: it reaches back to the previous boundary and forward to end-of-data,
                // mirroring the leading-edge gap-before case - previously the whole run (legs, arcs,
                // bridges) cleared the moment the icon entered the trailing leg. The dataEndUT guard
                // keeps STATIC recordings (headUT = live now, far past the data) from painting their
                // full paths: callers that do not pass dataEndUT (default -inf) keep the old
                // no-run behaviour.
                if (currentUT <= dataEndUT + 1.0)
                {
                    OrbitSegment last = effectiveSegments[count - 1];
                    window.CurrentIndex = count - 1;
                    window.CurrentElementStartUT = last.startUT;
                    window.RunStartUT = WalkBackwardBoundary(
                        effectiveSegments, count - 1, last.bodyName, muByBody, out int walkedBackPast);
                    window.StopUT = double.PositiveInfinity;
                    window.Reason = ForwardStopReason.EndOfData;
                    ParsekLog.VerboseRateLimited(Tag, "window",
                        string.Format(CultureInfo.InvariantCulture,
                            "Render run (past end) lastIdx={0} body={1} runStart={2:F1} stopUT=Inf " +
                            "walkedBack={3} segs={4} curUT={5:F1} dataEnd={6:F1}",
                            window.CurrentIndex, last.bodyName ?? "?", window.RunStartUT,
                            walkedBackPast, count, currentUT, dataEndUT));
                    return window;
                }
                ParsekLog.VerboseRateLimited(Tag, "nocur",
                    string.Format(CultureInfo.InvariantCulture,
                        "No element brackets/follows UT={0:F1} (segs={1} dataEnd={2:F1}) — no render run",
                        currentUT, count, dataEndUT));
                return window;
            }

            OrbitSegment current = effectiveSegments[currentIndex];
            window.CurrentIndex = currentIndex;
            window.CurrentElementStartUT = current.startUT;

            string currentBody = current.bodyName;
            double currentMu = SafeMu(muByBody, current.bodyName);
            bool bracketed = currentUT >= current.startUT && currentUT < current.endUT;
            bool currentIsClosed = IsFullLoopClosedOrbit(current, currentMu);

            // 2. Icon GENUINELY ON a full-loop closed orbit → no run: the stock OrbitRenderer draws the whole
            //    ellipse and the chained line clears (the revised reset-on-ellipse rule). This fires ONLY when
            //    the icon is bracketed BY the closed orbit; a ghost in the gap just BEFORE one (e.g. on the
            //    ascent leg before a launch-to-parking ellipse) still gets its backward run drawn (step 4).
            if (bracketed && currentIsClosed)
            {
                window.RunStartUT = current.startUT;
                window.StopUT = current.startUT;
                window.Reason = ForwardStopReason.IconOnClosedOrbit;
                ParsekLog.VerboseRateLimited(Tag, "iconclosed",
                    string.Format(CultureInfo.InvariantCulture,
                        "Icon on full-loop closed orbit idx={0} body={1} sma={2:F0} ecc={3:F4} " +
                        "span={4:F1} — line clears (no run)",
                        currentIndex, current.bodyName ?? "?", current.semiMajorAxis,
                        current.eccentricity, current.endUT - current.startUT));
                return window;
            }

            // 3. FORWARD boundary (run stop).
            double stopUT;
            ForwardStopReason reason;
            int walkedFwd = 0;
            int soiTransitionsConsumed = 0; // how many SOI seams the forward walk spanned (capped at ONE)
            if (!bracketed && currentIsClosed)
            {
                // Gap just before a full-loop closed orbit: the ellipse is the immediate forward boundary and
                // is NOT part of the run; the run is the backward span only (step 4).
                stopUT = current.startUT;
                reason = ForwardStopReason.FullLoopClosedOrbit;
            }
            else
            {
                // current is an OPEN element of the run (bracketed on it, or in a gap just before it). Extend
                // forward through open arcs, SPANNING exactly ONE SOI transition into the destination body so
                // the approach arcs (and capture ellipse) draw ahead of the icon while it is still on the
                // departure body (Bug 2: the Kerbin→Mun seam was previously a hard stop). The walk stops at the
                // destination body's first full-loop closed orbit (the capture ellipse), at a SECOND SOI change
                // (the single-SOI cap so an interplanetary Kerbin→Sun→Duna walk never runs the whole
                // heliocentric transfer), at a same-SOI full-loop closed orbit before any crossing, or at the
                // end of data (StopUT = +inf so trailing legs past the last orbit segment are included).
                stopUT = current.endUT;
                reason = ForwardStopReason.EndOfData;
                string activeBody = currentBody; // body the walk is currently inside
                for (int i = currentIndex + 1; i < count; i++)
                {
                    OrbitSegment next = effectiveSegments[i];
                    walkedFwd++;
                    if (BodyChanged(activeBody, next.bodyName))
                    {
                        if (soiTransitionsConsumed >= 1)
                        {
                            // Second distinct SOI change — single-SOI cap. Stop BEFORE it.
                            stopUT = next.startUT;
                            reason = ForwardStopReason.SecondSoiBound;
                            break;
                        }
                        // FIRST SOI change: do NOT stop. Cross into the destination body and keep walking the
                        // same element through the closed-orbit test below (no continue — the destination's
                        // first element may itself be the capture ellipse).
                        soiTransitionsConsumed++;
                        activeBody = next.bodyName;
                    }
                    if (IsFullLoopClosedOrbit(next, SafeMu(muByBody, next.bodyName)))
                    {
                        stopUT = next.startUT;
                        // Distinguish a closed orbit reached AFTER crossing the seam (the Bug 2 capture ellipse,
                        // icon and stop in different SOIs) from one in the icon's own SOI (the pre-existing case).
                        reason = soiTransitionsConsumed >= 1
                            ? ForwardStopReason.DestinationClosedOrbit
                            : ForwardStopReason.FullLoopClosedOrbit;
                        break;
                    }
                    stopUT = next.endUT;
                }
                if (reason == ForwardStopReason.EndOfData)
                    stopUT = double.PositiveInfinity; // no forward boundary — include trailing legs
            }

            // 4. BACKWARD boundary (run start). Walk back from the element before the current one to the
            //    previous boundary: a full-loop closed orbit (run starts AFTER it, at its endUT) or an SOI
            //    change (run starts at the first same-SOI element after it). If NO backward boundary is found,
            //    the run reaches the start of the trajectory data → RunStartUT = -inf, so every earlier
            //    non-orbital leg (e.g. the whole ascent before the first orbit segment) is included.
            double runStartUT = WalkBackwardBoundary(
                effectiveSegments, currentIndex - 1, currentBody, muByBody, out int walkedBack);

            window.RunStartUT = runStartUT;
            window.StopUT = stopUT;
            window.Reason = reason;

            ParsekLog.VerboseRateLimited(Tag, "window",
                string.Format(CultureInfo.InvariantCulture,
                    "Render run curIdx={0} body={1} runStart={2:F1} curStart={3:F1} stopUT={4:F1} reason={5} " +
                    "walkedFwd={6} walkedBack={7} bracketed={8} segs={9} curUT={10:F1} soiCrossed={11}",
                    currentIndex, currentBody ?? "?", window.RunStartUT, window.CurrentElementStartUT,
                    window.StopUT, window.Reason, walkedFwd, walkedBack, bracketed, count, currentUT,
                    soiTransitionsConsumed));

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
