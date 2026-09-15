using System;
using System.Collections.Generic;
using System.IO;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for <see cref="ForwardRenderWindow"/>, the pure Step-1 forward-window
    /// helper (docs/dev/plans/forward-trajectory-render.md). Covers
    /// <see cref="ForwardRenderWindow.IsFullLoopClosedOrbit"/> (period boundary,
    /// hyperbolic, degenerate sma) and <see cref="ForwardRenderWindow.ComputeForwardStopUT"/>
    /// / <see cref="ForwardRenderWindow.ComputeForwardWindow"/> (SOI-change stop,
    /// full-loop stop, multi transfer-arc chain, icon-on-closed-orbit returns its own
    /// startUT, predicted-included, and the (CRITICAL) effective-vs-recorded sourcing).
    ///
    /// The helper logs through <see cref="ParsekLog"/> rate-limited verbose lines (shared
    /// static state), so the class is <c>[Collection("Sequential")]</c> and resets the log
    /// rate-limit dict / overrides around each test.
    /// </summary>
    [Collection("Sequential")]
    public class ForwardRenderWindowTests : IDisposable
    {
        // Kerbin-ish GM so periods are realistic; exact value is irrelevant to the logic.
        private const double KerbinMu = 3.5316000e12;
        private const double MunMu = 6.5138398e10;
        private const double SunMu = 1.1723328e18;

        private readonly List<string> _logLines = new List<string>();

        public ForwardRenderWindowTests()
        {
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.TestSinkForTesting = line => _logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.ResetRateLimitsForTesting();
        }

        private static Func<string, double> Mu(params (string body, double mu)[] entries)
        {
            var map = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var e in entries) map[e.body] = e.mu;
            return name => name != null && map.TryGetValue(name, out double v) ? v : double.NaN;
        }

        private static OrbitSegment Seg(
            string body, double startUT, double endUT, double ecc, double sma,
            bool predicted = false)
        {
            return new OrbitSegment
            {
                bodyName = body,
                startUT = startUT,
                endUT = endUT,
                eccentricity = ecc,
                semiMajorAxis = sma,
                isPredicted = predicted,
            };
        }

        // ---------------------------------------------------------------------
        // IsFullLoopClosedOrbit
        // ---------------------------------------------------------------------

        // A circular orbit whose span >= its period is a full-loop closed orbit.
        [Fact]
        public void IsFullLoopClosedOrbit_TrueWhenEllipticalAndSpanAtLeastPeriod()
        {
            double sma = 700000.0; // ~LKO
            double period = ForwardRenderWindow.ComputePeriod(sma, KerbinMu);
            var seg = Seg("Kerbin", 0, period + 1.0, ecc: 0.01, sma: sma);
            Assert.True(ForwardRenderWindow.IsFullLoopClosedOrbit(seg, KerbinMu));
        }

        // A span just under one period is NOT a full loop (boundary, exclusive below).
        [Fact]
        public void IsFullLoopClosedOrbit_FalseJustBelowPeriodBoundary()
        {
            double sma = 700000.0;
            double period = ForwardRenderWindow.ComputePeriod(sma, KerbinMu);
            var seg = Seg("Kerbin", 0, period - 1.0, ecc: 0.01, sma: sma);
            Assert.False(ForwardRenderWindow.IsFullLoopClosedOrbit(seg, KerbinMu));
        }

        // Exactly one period is a full loop (span >= period is inclusive at the boundary).
        [Fact]
        public void IsFullLoopClosedOrbit_TrueExactlyAtPeriodBoundary()
        {
            double sma = 700000.0;
            double period = ForwardRenderWindow.ComputePeriod(sma, KerbinMu);
            var seg = Seg("Kerbin", 100.0, 100.0 + period, ecc: 0.0, sma: sma);
            Assert.True(ForwardRenderWindow.IsFullLoopClosedOrbit(seg, KerbinMu));
        }

        // Hyperbolic (ecc >= 1) is never a full loop, even with an enormous span.
        [Fact]
        public void IsFullLoopClosedOrbit_FalseForHyperbolicRegardlessOfSpan()
        {
            var seg = Seg("Kerbin", 0, 1.0e9, ecc: 1.4, sma: -800000.0);
            Assert.False(ForwardRenderWindow.IsFullLoopClosedOrbit(seg, KerbinMu));
        }

        // Exactly parabolic (ecc == 1) is also never a full loop.
        [Fact]
        public void IsFullLoopClosedOrbit_FalseForParabolic()
        {
            var seg = Seg("Kerbin", 0, 1.0e9, ecc: 1.0, sma: 1.0e9);
            Assert.False(ForwardRenderWindow.IsFullLoopClosedOrbit(seg, KerbinMu));
        }

        // Degenerate sma (<= 0 elliptical) yields a non-finite period → not a full loop.
        [Fact]
        public void IsFullLoopClosedOrbit_FalseForDegenerateSma()
        {
            var seg = Seg("Kerbin", 0, 1.0e9, ecc: 0.1, sma: 0.0);
            Assert.False(ForwardRenderWindow.IsFullLoopClosedOrbit(seg, KerbinMu));
        }

        // A non-finite / non-positive mu (e.g. null delegate path) → no full-loop classification.
        [Fact]
        public void IsFullLoopClosedOrbit_FalseForNonFiniteMu()
        {
            var seg = Seg("Kerbin", 0, 1.0e9, ecc: 0.1, sma: 700000.0);
            Assert.False(ForwardRenderWindow.IsFullLoopClosedOrbit(seg, double.NaN));
        }

        // ---------------------------------------------------------------------
        // ComputeForwardStopUT / ComputeForwardWindow
        // ---------------------------------------------------------------------

        // SOI change: ascent (Kerbin) then heliocentric (Sun) → stop at the Sun segment's startUT.
        [Fact]
        public void ComputeForwardStopUT_StopsAtFirstBodyChange()
        {
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 0, 100, ecc: 0.9, sma: 700000.0),   // current: escape arc
                Seg("Kerbin", 100, 200, ecc: 1.2, sma: -900000.0), // hyperbolic escape, same body
                Seg("Sun", 200, 5000, ecc: 0.1, sma: 1.0e10),      // SOI change → stop here
            };
            var mu = Mu(("Kerbin", KerbinMu), ("Sun", SunMu));

            double stop = ForwardRenderWindow.ComputeForwardStopUT(segs, currentUT: 50.0, mu);
            Assert.Equal(200.0, stop, 6);

            var w = ForwardRenderWindow.ComputeForwardWindow(segs, 50.0, mu);
            Assert.Equal(ForwardRenderWindow.ForwardStopReason.BodyChange, w.Reason);
            Assert.Equal(0, w.CurrentIndex);
            Assert.True(w.HasForwardRange);
        }

        // Full-loop closed orbit ahead: transfer arc then a parking ellipse (span >= period)
        // → stop at the parking orbit's startUT.
        [Fact]
        public void ComputeForwardStopUT_StopsAtFirstFullLoopClosedOrbit()
        {
            double sma = 700000.0;
            double period = ForwardRenderWindow.ComputePeriod(sma, KerbinMu);
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 0, 100, ecc: 0.4, sma: sma),                 // current: capture transfer arc (partial)
                Seg("Kerbin", 100, 100 + period + 5.0, ecc: 0.02, sma: sma), // full parking loop → stop here
            };
            var mu = Mu(("Kerbin", KerbinMu));

            double stop = ForwardRenderWindow.ComputeForwardStopUT(segs, currentUT: 50.0, mu);
            Assert.Equal(100.0, stop, 6);

            var w = ForwardRenderWindow.ComputeForwardWindow(segs, 50.0, mu);
            Assert.Equal(ForwardRenderWindow.ForwardStopReason.FullLoopClosedOrbit, w.Reason);
        }

        // Multi transfer-arc chain: several same-body partial arcs (none a full loop, none a
        // body change) walk to the end of the data.
        [Fact]
        public void ComputeForwardStopUT_WalksMultipleTransferArcsToEndOfData()
        {
            // Heliocentric-scale sma so the period (~years) dwarfs each 100s arc span — every
            // arc is partial (never a full loop), so the walk reaches end-of-data.
            double sma = 1.5e10;
            var segs = new List<OrbitSegment>
            {
                Seg("Sun", 0, 100, ecc: 0.3, sma: sma),
                Seg("Sun", 100, 200, ecc: 0.35, sma: sma),
                Seg("Sun", 200, 300, ecc: 0.4, sma: sma),
                Seg("Sun", 300, 400, ecc: 0.45, sma: sma), // end-of-data
            };
            var mu = Mu(("Sun", SunMu));

            double stop = ForwardRenderWindow.ComputeForwardStopUT(segs, currentUT: 50.0, mu);
            Assert.True(double.IsPositiveInfinity(stop)); // no forward boundary → run reaches end of data

            var w = ForwardRenderWindow.ComputeForwardWindow(segs, 50.0, mu);
            Assert.Equal(ForwardRenderWindow.ForwardStopReason.EndOfData, w.Reason);
            Assert.Equal(0, w.CurrentIndex);
            Assert.Equal(0.0, w.CurrentElementStartUT, 6);
            Assert.True(double.IsPositiveInfinity(w.StopUT));
            // Current is the first element → no backward boundary → run reaches the start of data.
            Assert.True(double.IsNegativeInfinity(w.RunStartUT));
        }

        // Icon already on a full-loop closed orbit → empty forward range (stop == its own startUT).
        [Fact]
        public void ComputeForwardStopUT_IconOnClosedOrbitReturnsOwnStartUT()
        {
            double sma = 700000.0;
            double period = ForwardRenderWindow.ComputePeriod(sma, KerbinMu);
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 1000.0, 1000.0 + period + 50.0, ecc: 0.01, sma: sma), // current: full parking loop
                Seg("Kerbin", 1000.0 + period + 50.0, 1.0e9, ecc: 0.5, sma: sma),    // would-be next, must be ignored
            };
            var mu = Mu(("Kerbin", KerbinMu));

            // currentUT sits inside the closed orbit.
            double stop = ForwardRenderWindow.ComputeForwardStopUT(segs, currentUT: 1500.0, mu);
            Assert.Equal(1000.0, stop, 6); // its own startUT, empty range

            var w = ForwardRenderWindow.ComputeForwardWindow(segs, 1500.0, mu);
            Assert.Equal(ForwardRenderWindow.ForwardStopReason.IconOnClosedOrbit, w.Reason);
            Assert.False(w.HasForwardRange);
            Assert.Equal(1000.0, w.CurrentElementStartUT, 6);
        }

        // Predicted future elements are NOT gated out: a predicted transfer arc is walked, then
        // the stop fires on the following body change (not on the isPredicted flag).
        [Fact]
        public void ComputeForwardStopUT_PredictedElementsIncluded()
        {
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 0, 100, ecc: 0.9, sma: 700000.0),                 // current
                Seg("Kerbin", 100, 200, ecc: 1.3, sma: -900000.0, predicted: true), // predicted escape arc — walked
                Seg("Sun", 200, 5000, ecc: 0.1, sma: 1.0e10, predicted: true),      // predicted, SOI change → stop
            };
            var mu = Mu(("Kerbin", KerbinMu), ("Sun", SunMu));

            double stop = ForwardRenderWindow.ComputeForwardStopUT(segs, currentUT: 50.0, mu);
            Assert.Equal(200.0, stop, 6);

            var w = ForwardRenderWindow.ComputeForwardWindow(segs, 50.0, mu);
            Assert.Equal(ForwardRenderWindow.ForwardStopReason.BodyChange, w.Reason);
        }

        // The helper is pure and takes its segment list as a parameter, so this cell can only show
        // that the stop tracks the geometry it is HANDED: two lists whose body change lands at
        // different UTs give different stops. WHICH list the renderer supplies is a caller
        // property that no argument to a pure function can witness; it is pinned separately by
        // ForwardWindowSource_IsBuiltFromTheEffectiveReAimedSegments below. This cell used to be
        // named for that caller claim.
        [Fact]
        public void ComputeForwardStopUT_TracksTheSuppliedGeometry()
        {
            // "Recorded": SOI change at UT=200.
            var recorded = new List<OrbitSegment>
            {
                Seg("Kerbin", 0, 100, ecc: 0.9, sma: 700000.0),
                Seg("Kerbin", 100, 200, ecc: 1.2, sma: -900000.0),
                Seg("Sun", 200, 5000, ecc: 0.1, sma: 1.0e10),
            };
            // "Effective" (re-aimed): the same ghost re-aimed so the Kerbin escape is shorter and
            // the Sun (SOI change) leg starts EARLIER, at UT=150.
            var effective = new List<OrbitSegment>
            {
                Seg("Kerbin", 0, 150, ecc: 0.9, sma: 700000.0),
                Seg("Sun", 150, 4800, ecc: 0.12, sma: 1.1e10),
            };
            var mu = Mu(("Kerbin", KerbinMu), ("Sun", SunMu));

            double stopRecorded = ForwardRenderWindow.ComputeForwardStopUT(recorded, 50.0, mu);
            double stopEffective = ForwardRenderWindow.ComputeForwardStopUT(effective, 50.0, mu);

            Assert.Equal(200.0, stopRecorded, 6);  // recorded geometry
            Assert.Equal(150.0, stopEffective, 6); // effective geometry — the value the helper must use
            Assert.NotEqual(stopRecorded, stopEffective);
        }

        // (CRITICAL, caller side) The forward window's segment source is built from the EFFECTIVE
        // (re-aimed) per-member lists, never from the recorded rec.OrbitSegments. The renderer's
        // chain-run pass needs FlightGlobals, so the claim is pinned as a source gate over the
        // method that hosts the ComputeForwardWindow call: windowSegs is assembled only from the
        // coalesced effective scratch lists, and those are fed from
        // GhostMapPresence.ResolveEffectiveMapOrbitSegments. Pointing windowSegs at a recorded
        // list puts a .OrbitSegments read into that assignment and reds this cell. The scan is
        // over comment-stripped, literal-masked text and a brace-matched method body, so a
        // commented-out or quoted mention cannot satisfy it. The other rec.OrbitSegments reads in
        // the same method (leg-anchor candidacy, hover text) are deliberately NOT covered: they
        // are not the window source.
        [Fact]
        public void ForwardWindowSource_IsBuiltFromTheEffectiveReAimedSegments()
        {
            string path = LocatePolylineRendererSource();
            Assert.True(File.Exists(path), $"GhostTrajectoryPolylineRenderer.cs not found at {path}");

            string prepared = SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(path));
            int callIdx = prepared.IndexOf(
                "ForwardRenderWindow.ComputeForwardWindow(", StringComparison.Ordinal);
            Assert.True(callIdx >= 0,
                "the renderer no longer calls ForwardRenderWindow.ComputeForwardWindow");

            List<int> open = SourceScanText.OpenBlockStack(prepared, callIdx);
            Assert.Equal(4, open.Count); // namespace -> static class -> Driver -> method
            string body = BraceMatchedBlock(prepared, open[3]);

            // 1. The window is computed from windowSegs.
            int argStart = body.IndexOf(
                "ForwardRenderWindow.ComputeForwardWindow(", StringComparison.Ordinal)
                + "ForwardRenderWindow.ComputeForwardWindow(".Length;
            string firstArg = body.Substring(argStart, body.IndexOf(',', argStart) - argStart).Trim();
            Assert.Equal("windowSegs", firstArg);

            // 2. windowSegs is assigned exactly once, and from the scratch lists only - no
            //    recorded-segment read anywhere in that assignment.
            const string decl = "List<OrbitSegment> windowSegs =";
            int declIdx = body.IndexOf(decl, StringComparison.Ordinal);
            Assert.True(declIdx >= 0, "windowSegs is no longer declared in the chain-run method");
            Assert.Equal(declIdx, body.LastIndexOf(decl, StringComparison.Ordinal));
            string assignment = body.Substring(declIdx, body.IndexOf(';', declIdx) - declIdx);
            Assert.DoesNotContain(".OrbitSegments", assignment);
            Assert.Contains("chainRunMemberSegsScratch", assignment);
            Assert.Contains("chainRunConcatScratch", assignment);

            // 3. Both scratch lists carry the COALESCED EFFECTIVE member list.
            Assert.Contains("chainRunMemberSegsScratch.Add(memberCoalesced)", body);
            Assert.Contains("chainRunConcatScratch.AddRange(memberCoalesced)", body);

            // 4. memberCoalesced coalesces memberEffective, which comes from the effective
            //    (re-aim aware) resolver, not from the recording's own list.
            int coalescedIdx = body.IndexOf("memberCoalesced =", StringComparison.Ordinal);
            Assert.True(coalescedIdx >= 0, "memberCoalesced is no longer assigned");
            string coalesced = body.Substring(coalescedIdx, body.IndexOf(';', coalescedIdx) - coalescedIdx);
            Assert.Contains("CoalesceSameOrbitFragments(memberEffective)", coalesced);

            int effectiveIdx = body.IndexOf("memberEffective =", StringComparison.Ordinal);
            Assert.True(effectiveIdx >= 0, "memberEffective is no longer assigned");
            string effective = body.Substring(effectiveIdx, body.IndexOf(';', effectiveIdx) - effectiveIdx);
            Assert.Contains("GhostMapPresence.ResolveEffectiveMapOrbitSegments(", effective);
        }

        private static string BraceMatchedBlock(string prepared, int openBrace)
        {
            int depth = 0;
            for (int i = openBrace; i < prepared.Length; i++)
            {
                if (prepared[i] == '{') depth++;
                else if (prepared[i] == '}')
                {
                    depth--;
                    if (depth == 0) return prepared.Substring(openBrace, i - openBrace + 1);
                }
            }
            throw new InvalidOperationException("unbalanced braces from " + openBrace);
        }

        private static string LocatePolylineRendererSource()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                string candidate = Path.Combine(
                    dir, "Source", "Parsek", "Display", "GhostTrajectoryPolylineRenderer.cs");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }

            return Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "Parsek", "Display", "GhostTrajectoryPolylineRenderer.cs"));
        }

        // No element brackets/follows the current UT (icon past the last endUT) and NO dataEndUT is
        // supplied (default -inf): no window; the convenience wrapper returns currentUT unchanged.
        // This is the STATIC-recording behaviour (headUT = live now, far past the data).
        [Fact]
        public void ComputeForwardStopUT_ReturnsCurrentUTWhenNoElementAhead()
        {
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 0, 100, ecc: 0.5, sma: 700000.0),
            };
            var mu = Mu(("Kerbin", KerbinMu));

            double stop = ForwardRenderWindow.ComputeForwardStopUT(segs, currentUT: 5000.0, mu);
            Assert.Equal(5000.0, stop, 6);

            var w = ForwardRenderWindow.ComputeForwardWindow(segs, 5000.0, mu);
            Assert.Equal(-1, w.CurrentIndex);
            Assert.Equal(ForwardRenderWindow.ForwardStopReason.NoCurrentElement, w.Reason);
            Assert.False(w.HasForwardRange);
        }

        // PAST END within the recorded data (review MAJOR-1): the icon rides a TRAILING leg past the
        // last conic (the final landing descent). The run STAYS ALIVE - back to the previous boundary,
        // forward to end-of-data - instead of clearing the whole line the moment the icon enters the
        // trailing leg (asymmetric with the leading-edge gap-before case it mirrors).
        [Fact]
        public void ComputeForwardWindow_PastEndWithinData_RunStaysAlive()
        {
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 0, 100, ecc: 0.5, sma: 700000.0),
                Seg("Kerbin", 150, 400, ecc: 0.4, sma: 720000.0),
            };
            var mu = Mu(("Kerbin", KerbinMu));

            // Icon at 450 (past the last conic's endUT=400) but within the recorded data (end 500):
            // the trailing-leg case. Run = [-inf, +inf] (no backward boundary in this list).
            var w = ForwardRenderWindow.ComputeForwardWindow(segs, 450.0, mu, dataEndUT: 500.0);
            Assert.Equal(1, w.CurrentIndex);
            Assert.Equal(ForwardRenderWindow.ForwardStopReason.EndOfData, w.Reason);
            Assert.Equal(double.NegativeInfinity, w.RunStartUT);
            Assert.Equal(double.PositiveInfinity, w.StopUT);
            Assert.True(w.HasForwardRange);
        }

        // PAST END behind a full-loop boundary: the run starts AFTER the ellipse (its endUT), so a
        // trailing descent past a parking-orbit loiter never re-includes the boundary ellipse.
        [Fact]
        public void ComputeForwardWindow_PastEndBehindFullLoop_RunStartsAfterIt()
        {
            double sma = 700000.0;
            double period = ForwardRenderWindow.ComputePeriod(sma, KerbinMu);
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 0, period + 10.0, ecc: 0.01, sma: sma), // full-loop boundary
                Seg("Kerbin", period + 50.0, period + 300.0, ecc: 0.3, sma: 650000.0),
            };
            var mu = Mu(("Kerbin", KerbinMu));

            double pastUT = period + 350.0;
            var w = ForwardRenderWindow.ComputeForwardWindow(segs, pastUT, mu, dataEndUT: pastUT + 100.0);
            Assert.Equal(ForwardRenderWindow.ForwardStopReason.EndOfData, w.Reason);
            Assert.Equal(period + 10.0, w.RunStartUT, 6); // after the ellipse, not -inf
            Assert.Equal(double.PositiveInfinity, w.StopUT);
            Assert.True(w.HasForwardRange);
        }

        // PAST END beyond the recorded data (a STATIC recording with headUT = live now): the dataEndUT
        // guard refuses the run, so historical recordings never paint their full paths.
        [Fact]
        public void ComputeForwardWindow_PastDataEnd_NoRun()
        {
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 0, 100, ecc: 0.5, sma: 700000.0),
            };
            var mu = Mu(("Kerbin", KerbinMu));

            var w = ForwardRenderWindow.ComputeForwardWindow(
                segs, currentUT: 5000.0, mu, dataEndUT: 200.0);
            Assert.Equal(-1, w.CurrentIndex);
            Assert.Equal(ForwardRenderWindow.ForwardStopReason.NoCurrentElement, w.Reason);
            Assert.False(w.HasForwardRange);
        }

        // Empty / null effective list → no window, currentUT returned.
        [Fact]
        public void ComputeForwardStopUT_EmptyListReturnsCurrentUT()
        {
            var mu = Mu(("Kerbin", KerbinMu));
            Assert.Equal(123.0, ForwardRenderWindow.ComputeForwardStopUT(new List<OrbitSegment>(), 123.0, mu), 6);
            Assert.Equal(123.0, ForwardRenderWindow.ComputeForwardStopUT(null, 123.0, mu), 6);
        }

        // A null mu delegate must not throw and must not classify any segment a full loop: a
        // would-be parking loop is then walked (no full-loop stop), stopping only on body change.
        [Fact]
        public void ComputeForwardWindow_NullMuDelegateDoesNotThrowAndSkipsFullLoopStop()
        {
            double sma = 700000.0;
            double period = ForwardRenderWindow.ComputePeriod(sma, KerbinMu);
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 0, 100, ecc: 0.4, sma: sma),                          // current
                Seg("Kerbin", 100, 100 + period + 5.0, ecc: 0.02, sma: sma),        // would be a full loop, but mu is null
                Seg("Sun", 100 + period + 5.0, 1.0e9, ecc: 0.1, sma: 1.0e10),       // body change → stop here instead
            };

            var w = ForwardRenderWindow.ComputeForwardWindow(segs, 50.0, muByBody: null);
            Assert.Equal(ForwardRenderWindow.ForwardStopReason.BodyChange, w.Reason);
            Assert.Equal(100.0 + period + 5.0, w.StopUT, 3);
        }

        // The decision logging fires (verbose default-on in tests): the forward-window summary
        // line records the current index, body, and stop reason.
        [Fact]
        public void ComputeForwardWindow_LogsWindowDecision()
        {
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 0, 100, ecc: 0.9, sma: 700000.0),
                Seg("Sun", 100, 5000, ecc: 0.1, sma: 1.0e10),
            };
            var mu = Mu(("Kerbin", KerbinMu), ("Sun", SunMu));

            ForwardRenderWindow.ComputeForwardWindow(segs, 50.0, mu);

            Assert.Contains(_logLines, l =>
                l.Contains("[ForwardRenderWindow]") && l.Contains("Render run") && l.Contains("reason=BodyChange"));
        }

        // ---------------------------------------------------------------------
        // Render run (revised rule 2026-06-09): backward boundary + past persists
        // ---------------------------------------------------------------------

        // No backward boundary (current element is the first) → RunStartUT = -inf, so all earlier legs
        // (e.g. the whole ascent before the first orbit segment) are included and PERSIST in the run.
        [Fact]
        public void ComputeForwardWindow_RunStartIsNegInfWhenNoBackwardBoundary()
        {
            double sma = 700000.0;
            double period = ForwardRenderWindow.ComputePeriod(sma, KerbinMu);
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 200, ecc: 0.4, sma: sma),                 // current: coast arc
                Seg("Kerbin", 200, 200 + period + 5.0, ecc: 0.02, sma: sma), // parking loop → forward stop
            };
            var mu = Mu(("Kerbin", KerbinMu));

            var w = ForwardRenderWindow.ComputeForwardWindow(segs, currentUT: 150.0, mu);
            Assert.True(w.HasForwardRange);
            Assert.True(double.IsNegativeInfinity(w.RunStartUT)); // run reaches data start (earlier legs persist)
            Assert.Equal(200.0, w.StopUT, 6);                     // parking-loop startUT
            Assert.Equal(ForwardRenderWindow.ForwardStopReason.FullLoopClosedOrbit, w.Reason);
        }

        // Gap just BEFORE a full-loop closed orbit (icon on the ascent leg before the parking ellipse):
        // NOT IconOnClosedOrbit — the run is the backward span up to the ellipse start, so the ascent draws.
        [Fact]
        public void ComputeForwardWindow_GapBeforeClosedOrbitIsNotIconOnClosed()
        {
            double sma = 700000.0;
            double period = ForwardRenderWindow.ComputePeriod(sma, KerbinMu);
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 100 + period + 5.0, ecc: 0.01, sma: sma), // first orbit element IS the parking loop
            };
            var mu = Mu(("Kerbin", KerbinMu));

            // Icon at UT=50, in the gap BEFORE the parking loop (on the ascent leg).
            var w = ForwardRenderWindow.ComputeForwardWindow(segs, currentUT: 50.0, mu);
            Assert.NotEqual(ForwardRenderWindow.ForwardStopReason.IconOnClosedOrbit, w.Reason);
            Assert.Equal(ForwardRenderWindow.ForwardStopReason.FullLoopClosedOrbit, w.Reason);
            Assert.True(w.HasForwardRange);
            Assert.Equal(100.0, w.StopUT, 6);                     // the ellipse is the forward boundary
            Assert.True(double.IsNegativeInfinity(w.RunStartUT)); // backward run reaches data start (the ascent)
        }

        // Backward boundary = a prior full-loop closed orbit: the run starts AFTER it (at its endUT),
        // so a post-parking transfer run does not redraw the parking ellipse behind it.
        [Fact]
        public void ComputeForwardWindow_RunStartsAfterPriorClosedOrbit()
        {
            double sma = 700000.0;
            double period = ForwardRenderWindow.ComputePeriod(sma, KerbinMu);
            double loopEnd = 100 + period + 5.0;
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, loopEnd, ecc: 0.01, sma: sma),          // prior parking loop (backward boundary)
                Seg("Kerbin", loopEnd, loopEnd + 100, ecc: 0.6, sma: sma), // current: transfer arc after parking
                Seg("Sun", loopEnd + 100, 1.0e9, ecc: 0.1, sma: 1.0e10),   // SOI change → forward stop
            };
            var mu = Mu(("Kerbin", KerbinMu), ("Sun", SunMu));

            var w = ForwardRenderWindow.ComputeForwardWindow(segs, currentUT: loopEnd + 50.0, mu);
            Assert.Equal(1, w.CurrentIndex);
            Assert.Equal(loopEnd, w.RunStartUT, 3);   // run starts AFTER the prior closed orbit
            Assert.Equal(loopEnd + 100, w.StopUT, 3); // SOI change
            Assert.Equal(ForwardRenderWindow.ForwardStopReason.BodyChange, w.Reason);
        }

        // Backward boundary = a prior SOI change: the run starts at the first same-SOI element after it.
        [Fact]
        public void ComputeForwardWindow_RunStartsAtPriorSoiBoundary()
        {
            var segs = new List<OrbitSegment>
            {
                Seg("Sun", 0, 100, ecc: 0.1, sma: 1.0e10),        // prior SOI (Sun)
                Seg("Kerbin", 100, 200, ecc: 0.7, sma: 700000.0), // current run's first element (Kerbin capture)
                Seg("Kerbin", 200, 300, ecc: 0.5, sma: 700000.0), // current
            };
            var mu = Mu(("Kerbin", KerbinMu), ("Sun", SunMu));

            var w = ForwardRenderWindow.ComputeForwardWindow(segs, currentUT: 250.0, mu);
            Assert.Equal(2, w.CurrentIndex);
            Assert.Equal(100.0, w.RunStartUT, 6);             // first same-SOI element after the Sun boundary
            Assert.True(double.IsPositiveInfinity(w.StopUT)); // no forward boundary → end of data
            Assert.Equal(ForwardRenderWindow.ForwardStopReason.EndOfData, w.Reason);
        }
    }
}
