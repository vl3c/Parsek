using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

using Coverage = Parsek.MapRenderTrace.RenderWindowCoverage;
using Verdict = Parsek.MapRenderTrace.LineToggleVerdict;

namespace Parsek.Tests
{
    /// <summary>
    /// LINE-BLINK-JUMP-STRADDLE-DETECTOR-GAP: the WINDOW-TRANSITION exemption on the gated Tier-C
    /// <c>line-blink</c> anomaly, plus the cells that pin it CANNOT mask a real blink.
    ///
    /// <para>The exemption's claim is one sentence: <b>a toggle pair that leaves the recording's
    /// rendered body-frame window and comes back onto a clock the recording COVERS is two legitimate
    /// transitions, not a flicker.</b> Both halves must be PROVEN - and that is the whole lesson of
    /// this file's history. Judging from ONE half looks sufficient and is not:
    /// <c>parking-conic-loiter-hold</c> holds the line LIT while the clock is outside the window, so a
    /// pair pivoting on it has nothing inside the window and is a real on-screen flash. A
    /// dark-half-only rule swallows it from one edge; a lit-half-only rule swallows it from the
    /// other.</para>
    ///
    /// <para>Fixture UTs / frames / bodies below are the ARCHIVED values from the four measured raises,
    /// not invented ones - see each cell's citation.</para>
    /// </summary>
    public class LineBlinkWindowExitExemptionTests
    {
        // ---------------------------------------------------------------------------------
        // ClassifyLineToggle - the positive discriminator, one cell per FAIL-CLOSED conjunct
        // ---------------------------------------------------------------------------------

        [Fact]
        public void Classify_DarkAndMeasuredOutside_IsWindowExitOff()
        {
            Assert.Equal(Verdict.WindowExitOff, MapRenderTrace.ClassifyLineToggle(
                lineDefinitivelyOff: true, lineDefinitivelyLit: false,
                hasFreshIntent: true, intentLineActive: false,
                intentWindowCoverage: Coverage.Outside));
        }

        [Fact]
        public void Classify_LitAndMeasuredInside_IsInsideWindowOn()
        {
            Assert.Equal(Verdict.InsideWindowOn, MapRenderTrace.ClassifyLineToggle(
                lineDefinitivelyOff: false, lineDefinitivelyLit: true,
                hasFreshIntent: true, intentLineActive: true,
                intentWindowCoverage: Coverage.Inside));
        }

        [Fact]
        public void Classify_DegenerateLineRead_IsOther()
        {
            // "(line-null)" / "(no-renderer)" / "(read-err:...)" land here: neither definitively off
            // nor definitively lit. Not knowing is never evidence of a legitimate transition.
            Assert.Equal(Verdict.Other, MapRenderTrace.ClassifyLineToggle(
                lineDefinitivelyOff: false, lineDefinitivelyLit: false,
                hasFreshIntent: true, intentLineActive: false,
                intentWindowCoverage: Coverage.Outside));
        }

        [Fact]
        public void Classify_NoFreshIntent_IsOther()
        {
            // Our Postfix did not run, so nothing measured the clock. BLOCKER-2 route (a): a line lit
            // behind our back after a stamped window-exit OFF must not complete an exemption.
            // Dark half, no decision this frame:
            Assert.Equal(Verdict.Other, MapRenderTrace.ClassifyLineToggle(
                lineDefinitivelyOff: true, lineDefinitivelyLit: false,
                hasFreshIntent: false, intentLineActive: false,
                intentWindowCoverage: Coverage.Outside));
            // Lit half, no decision this frame:
            Assert.Equal(Verdict.Other, MapRenderTrace.ClassifyLineToggle(
                lineDefinitivelyOff: false, lineDefinitivelyLit: true,
                hasFreshIntent: false, intentLineActive: true,
                intentWindowCoverage: Coverage.Inside));
        }

        [Fact]
        public void Classify_DecisionDisagreesWithTruth_IsOther()
        {
            // That disagreement is the decision-vs-truth anomaly's business and must not be laundered
            // into a line-blink exemption.
            // Truth dark, decision says ON:
            Assert.Equal(Verdict.Other, MapRenderTrace.ClassifyLineToggle(
                lineDefinitivelyOff: true, lineDefinitivelyLit: false,
                hasFreshIntent: true, intentLineActive: true,
                intentWindowCoverage: Coverage.Outside));
            // Truth lit, decision says OFF:
            Assert.Equal(Verdict.Other, MapRenderTrace.ClassifyLineToggle(
                lineDefinitivelyOff: false, lineDefinitivelyLit: true,
                hasFreshIntent: true, intentLineActive: false,
                intentWindowCoverage: Coverage.Inside));
        }

        [Fact]
        public void Classify_DarkButNotMeasuredOutside_IsOther()
        {
            // THE load-bearing conjunct for the dark half. Every within-window OFF reason
            // (polyline-owns-phase, director-traced-path-suppress, below-atmosphere /
            // terminal-below-atmosphere, stale-segment-awaiting-reseed, post-polyline-release-grace,
            // director-terminal-suppress) leaves coverage Unknown and still raises.
            foreach (Coverage coverage in new[] { Coverage.Unknown, Coverage.Inside })
            {
                Assert.Equal(Verdict.Other, MapRenderTrace.ClassifyLineToggle(
                    lineDefinitivelyOff: true, lineDefinitivelyLit: false,
                    hasFreshIntent: true, intentLineActive: false,
                    intentWindowCoverage: coverage));
            }
        }

        [Fact]
        public void Classify_LitButNotMeasuredInside_IsOther()
        {
            // THE load-bearing conjunct for the lit half, and BLOCKER-2 route (b). `Inside` is
            // POSITIVE, not "not Outside": `terminal-visible` is LIT past the recorded window and
            // stamps NOTHING (Unknown), so a not-Outside test would read it as covered. `Outside` is
            // `parking-conic-loiter-hold`. Both must be Other.
            foreach (Coverage coverage in new[] { Coverage.Unknown, Coverage.Outside })
            {
                Assert.Equal(Verdict.Other, MapRenderTrace.ClassifyLineToggle(
                    lineDefinitivelyOff: false, lineDefinitivelyLit: true,
                    hasFreshIntent: true, intentLineActive: true,
                    intentWindowCoverage: coverage));
            }
        }

        // ---------------------------------------------------------------------------------
        // ResolveWindowTransitionExempt - BOTH halves, symmetric across the two edges
        // ---------------------------------------------------------------------------------

        [Fact]
        public void Resolve_LitEdge_InsideOnAfterWindowExitOff_IsExempt()
        {
            // The V10 dres shape: dark(before-body-frame-start) -> lit(director-stockconic-visible).
            Assert.True(MapRenderTrace.ResolveWindowTransitionExempt(
                lineIsLit: true, currentToggle: Verdict.InsideWindowOn,
                hasPriorToggle: true, priorToggle: Verdict.WindowExitOff));
        }

        [Fact]
        public void Resolve_DarkEdge_WindowExitOffAfterInsideOn_IsExempt()
        {
            // The V8 eve shape: lit(inside) -> dark(past-body-frame-end).
            Assert.True(MapRenderTrace.ResolveWindowTransitionExempt(
                lineIsLit: false, currentToggle: Verdict.WindowExitOff,
                hasPriorToggle: true, priorToggle: Verdict.InsideWindowOn));
        }

        [Fact]
        public void Resolve_LitEdge_LitHalfNotProvenInside_StillRaises()
        {
            // BLOCKER 2 at the predicate: the lit half is `parking-conic-loiter-hold` or
            // `terminal-visible` or an undecided frame. A pair with nothing inside the window is a
            // real on-screen flash.
            Assert.False(MapRenderTrace.ResolveWindowTransitionExempt(
                lineIsLit: true, currentToggle: Verdict.Other,
                hasPriorToggle: true, priorToggle: Verdict.WindowExitOff));
        }

        [Fact]
        public void Resolve_DarkEdge_PriorLitHalfNotProvenInside_StillRaises()
        {
            // BLOCKER 1, THE MIRROR, and the cell whose ABSENCE was the gap. Sequence: dark
            // (past-body-frame-end, stamped) -> ... >8 frames ... -> lit (parking-conic-loiter-hold,
            // stamped Other, no raise because it is out of the frame window) -> the hold disarms one
            // frame later -> dark (past-body-frame-end). Caught on the DARK edge with sinceFrames=1.
            // A dark-half-only rule exempted this, so the whole 1-frame lit flash in the dark region
            // produced ZERO raises. The prior toggle was not a proven inside-window ON, so it raises.
            Assert.False(MapRenderTrace.ResolveWindowTransitionExempt(
                lineIsLit: false, currentToggle: Verdict.WindowExitOff,
                hasPriorToggle: true, priorToggle: Verdict.Other));
        }

        [Fact]
        public void Resolve_HalvesMustBeOppositeKinds_OtherwiseRaises()
        {
            // A pair is one dark half and one lit half. Two of the same kind is not a transition.
            var cases = new[]
            {
                new object[] { true,  Verdict.InsideWindowOn, Verdict.InsideWindowOn },
                new object[] { true,  Verdict.WindowExitOff,  Verdict.WindowExitOff },
                new object[] { false, Verdict.WindowExitOff,  Verdict.WindowExitOff },
                new object[] { false, Verdict.InsideWindowOn, Verdict.InsideWindowOn },
            };
            foreach (object[] c in cases)
            {
                Assert.False(MapRenderTrace.ResolveWindowTransitionExempt(
                    lineIsLit: (bool)c[0], currentToggle: (Verdict)c[1],
                    hasPriorToggle: true, priorToggle: (Verdict)c[2]));
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Resolve_NoPriorToggle_IsNotExempt(bool lineIsLit)
        {
            Assert.False(MapRenderTrace.ResolveWindowTransitionExempt(
                lineIsLit: lineIsLit,
                currentToggle: lineIsLit ? Verdict.InsideWindowOn : Verdict.WindowExitOff,
                hasPriorToggle: false,
                priorToggle: lineIsLit ? Verdict.WindowExitOff : Verdict.InsideWindowOn));
        }

        // ---------------------------------------------------------------------------------
        // IsLineBlink - the guard itself, and the CANNOT-MASK pins
        // ---------------------------------------------------------------------------------

        [Fact]
        public void IsLineBlink_DefaultWindowTransitionExempt_PreservesLegacyBehavior()
        {
            // Every pre-existing call site omits the new argument and must be byte-identical.
            Assert.True(MapRenderTrace.IsLineBlink(
                toggled: true, hasLastToggleFrame: true,
                lastToggleFrame: 100, currentFrame: 103));
        }

        [Fact]
        public void IsLineBlink_WithinWindow_WindowTransitionExempt_NotBlink()
        {
            Assert.False(MapRenderTrace.IsLineBlink(
                toggled: true, hasLastToggleFrame: true,
                lastToggleFrame: 100, currentFrame: 103,
                bodyChanged: false, offWindowCovered: false,
                windowTransitionExempt: true));
        }

        [Fact]
        public void IsLineBlink_WithinWindow_NotAWindowTransition_StillBlink()
        {
            // THE CANNOT-MASK PIN. Identical to the cell above in every parameter but the
            // discriminator.
            Assert.True(MapRenderTrace.IsLineBlink(
                toggled: true, hasLastToggleFrame: true,
                lastToggleFrame: 100, currentFrame: 103,
                bodyChanged: false, offWindowCovered: false,
                windowTransitionExempt: false));
        }

        [Fact]
        public void IsLineBlink_NoToggle_WindowTransitionExempt_StillNotBlink()
        {
            Assert.False(MapRenderTrace.IsLineBlink(
                toggled: false, hasLastToggleFrame: true,
                lastToggleFrame: 100, currentFrame: 103,
                bodyChanged: false, offWindowCovered: false,
                windowTransitionExempt: true));
        }

        // ---------------------------------------------------------------------------------
        // The FOUR archived raises, replayed end to end through the whole chain
        // ---------------------------------------------------------------------------------

        /// <summary>Replay one archived raise: classify this frame's half, resolve against the stamped
        /// other half, then ask the detector. Coverage values are the ones the cited decision lines
        /// carry.</summary>
        private static bool RaisesAfterExemption(
            bool lineIsLit,
            Coverage thisFrameCoverage,
            Verdict priorToggle,
            int lastToggleFrame,
            int currentFrame,
            bool bodyChanged = false,
            bool offWindowCovered = false)
        {
            Verdict current = MapRenderTrace.ClassifyLineToggle(
                lineDefinitivelyOff: !lineIsLit,
                lineDefinitivelyLit: lineIsLit,
                hasFreshIntent: true,
                intentLineActive: lineIsLit,
                intentWindowCoverage: thisFrameCoverage);
            bool exempt = MapRenderTrace.ResolveWindowTransitionExempt(
                lineIsLit: lineIsLit, currentToggle: current,
                hasPriorToggle: true, priorToggle: priorToggle);
            return MapRenderTrace.IsLineBlink(
                toggled: true, hasLastToggleFrame: true,
                lastToggleFrame: lastToggleFrame, currentFrame: currentFrame,
                bodyChanged: bodyChanged, offWindowCovered: offWindowCovered,
                windowTransitionExempt: exempt);
        }

        [Fact]
        public void ArchivedRaise_V8Eve_1111_DarkEdgeStraddle_IsNowExempt()
        {
            // logs/2026-08-11_1111_V8-eve-player-loop/KSP.log:12230
            //   reason=line-blink lineActive=False prevActive=True lastToggleFrame=7839
            //   sinceFrames=4 body=Eve offWindowCovered=False polylinePainted=False
            // Same frame 7843 decision: reason=past-body-frame-end lineActive=False
            //   currentUT=30451100.0 bounds=[30360218.8,30450249.6]  => clock 850.4 s PAST the end.
            // priorToggle IS LOAD-BEARING and is measured, not assumed: frame 7839's decision in the
            //   same log reads reason=director-stockconic-visible lineActive=True
            //   currentUT=30360400.2 bounds=[30360218.8,30450249.6], i.e. a proven InsideWindowOn -
            //   and note the bounds are IDENTICAL to the dark half's.
            Assert.False(RaisesAfterExemption(
                lineIsLit: false, thisFrameCoverage: Coverage.Outside,
                priorToggle: Verdict.InsideWindowOn,
                lastToggleFrame: 7839, currentFrame: 7843));
        }

        [Fact]
        public void ArchivedRaise_V8Eve_1114_DarkEdgeStraddle_IsNowExempt()
        {
            // logs/2026-08-11_1114_V8-eve-player-loop/KSP.log:12099
            //   lineActive=False prevActive=True lastToggleFrame=7611 sinceFrames=7 body=Sun
            // Same frame 7618 decision: reason=past-body-frame-end lineActive=False
            //   currentUT=30360400.0 bounds=[26616878.0,30360218.8]  => 181.2 s PAST the end.
            // priorToggle measured: frame 7611 reads reason=visible-body-frame lineActive=True
            //   bounds=[26616878.0,30360218.8] - a proven InsideWindowOn, and the ONE archived pair
            //   whose two halves are BOTH body-frame decisions (the other five pair an
            //   applied-segment Inside with a body-frame Outside, though all six happen to carry
            //   identical bounds - see LINE-BLINK-EXEMPTION-DOES-NOT-PIN-THE-BOUNDARY).
            Assert.False(RaisesAfterExemption(
                lineIsLit: false, thisFrameCoverage: Coverage.Outside,
                priorToggle: Verdict.InsideWindowOn,
                lastToggleFrame: 7611, currentFrame: 7618));
        }

        [Fact]
        public void ArchivedRaise_V10Dres_0627_LitEdgeRebind_IsNowExempt()
        {
            // logs/2026-08-12_0627_V10-dres-loop-arrival/KSP.log:11582
            //   lineActive=True prevActive=False lastToggleFrame=7218 sinceFrames=1 body=Sun
            // The OFF half is frame 7218: reason=before-body-frame-start lineActive=False
            //   currentUT=31276682.660 bounds=[31276682.7,43162584.5]  => BEFORE the window start.
            // The lit edge's own decision is director-stockconic-visible, i.e. INSIDE.
            Assert.False(RaisesAfterExemption(
                lineIsLit: true, thisFrameCoverage: Coverage.Inside,
                priorToggle: Verdict.WindowExitOff,
                lastToggleFrame: 7218, currentFrame: 7219));
        }

        [Fact]
        public void ArchivedRaise_V10Dres_0632_LitEdgeRebind_IsNowExempt()
        {
            // logs/2026-08-12_0632_V10-dres-loop-arrival/KSP.log:11618
            //   currentUT=31276442.640 lineActive=True prevActive=False lastToggleFrame=7237
            //   sinceFrames=1 body=Sun. Iteration 3's moved escape bracket.
            Assert.False(RaisesAfterExemption(
                lineIsLit: true, thisFrameCoverage: Coverage.Inside,
                priorToggle: Verdict.WindowExitOff,
                lastToggleFrame: 7237, currentFrame: 7238));
        }

        [Fact]
        public void ArchivedRaiseGeometry_ButAHalfIsNotProven_StillRaises()
        {
            // THE NEGATIVE CONTROLS for all four cells above: byte-identical frame geometry, with ONE
            // fact changed each time. All four must still red the lane.

            // (1) LIT edge, V10 _0627 geometry, but the OFF half was decided INSIDE the window
            //     (polyline-owns-phase, a stale-segment reseed lag, ...).
            Assert.True(RaisesAfterExemption(
                lineIsLit: true, thisFrameCoverage: Coverage.Inside,
                priorToggle: Verdict.Other,
                lastToggleFrame: 7218, currentFrame: 7219));

            // (2) LIT edge, same geometry, but the LIT half is itself OUTSIDE the window
            //     (parking-conic-loiter-hold). Nothing in the pair is inside: a real flash.
            Assert.True(RaisesAfterExemption(
                lineIsLit: true, thisFrameCoverage: Coverage.Outside,
                priorToggle: Verdict.WindowExitOff,
                lastToggleFrame: 7218, currentFrame: 7219));

            // (3) DARK edge, V8 _1114 geometry, but the OFF is not a window exit.
            Assert.True(RaisesAfterExemption(
                lineIsLit: false, thisFrameCoverage: Coverage.Unknown,
                priorToggle: Verdict.InsideWindowOn,
                lastToggleFrame: 7611, currentFrame: 7618));

            // (4) DARK edge, same geometry, but the PRIOR lit half was not proven inside - BLOCKER 1's
            //     swallowed sequence end to end.
            Assert.True(RaisesAfterExemption(
                lineIsLit: false, thisFrameCoverage: Coverage.Outside,
                priorToggle: Verdict.Other,
                lastToggleFrame: 7611, currentFrame: 7618));
        }

        // ---------------------------------------------------------------------------------
        // SOURCE GATE: only the four measuring decisions may stamp coverage
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// The strongest anti-over-reach pin, and the one that survives a refactor. Because the stamp
        /// is an ENUM VALUE rather than a bare <c>true</c>, any stamp - named argument, positional
        /// argument, or via a local - must spell <c>RenderWindowCoverage.Inside</c> /
        /// <c>.Outside</c>, so counting those spellings catches every widening the earlier
        /// <c>outsideRenderWindow: true</c> grep would have missed (a trailing positional
        /// <c>..., endUT, true)</c> slipped straight past it).
        ///
        /// <para>EXACTLY four stamp sites, all in <c>GhostOrbitLinePatch.cs</c>: two <c>Outside</c>
        /// (the window-exit OFF, and the parking-conic hold that is LIT out there) and two
        /// <c>Inside</c> (<c>director-stockconic-visible</c>, <c>visible-body-frame</c>). Every other
        /// decision - including <c>terminal-visible</c>, which is lit PAST the recorded window, and
        /// <c>stale-segment-awaiting-reseed</c>, whose "outside bounds" is the applied-segment bounds
        /// lagging INSIDE the window - must leave it <c>Unknown</c>.</para>
        /// </summary>
        [Fact]
        public void CoverageStamps_AreConfinedToTheFourMeasuringDecisions()
        {
            string repoRoot = ResolveRepoRoot();
            string sourceDir = Path.Combine(repoRoot, "Source", "Parsek");
            Assert.True(Directory.Exists(sourceDir), "Source/Parsek missing: " + sourceDir);

            var stampRe = new Regex(
                @"RenderWindowCoverage\s*\.\s*(Inside|Outside)", RegexOptions.CultureInvariant);
            int inside = 0, outside = 0;
            foreach (string file in Directory.GetFiles(sourceDir, "*.cs", SearchOption.AllDirectories))
            {
                // MapRenderTrace.cs DECLARES the enum and COMPARES against it inside ClassifyLineToggle.
                // Those are reads, not stamps; the writers are LogOrbitLineDecision's callers. Its sole
                // write path (RecordLineIntent) is pinned to one call site by the sibling cell below.
                if (string.Equals(Path.GetFileName(file), "MapRenderTrace.cs", StringComparison.Ordinal))
                    continue;
                MatchCollection hits = stampRe.Matches(File.ReadAllText(file));
                if (hits.Count == 0)
                    continue;
                Assert.True(
                    string.Equals(Path.GetFileName(file), "GhostOrbitLinePatch.cs", StringComparison.Ordinal),
                    "Only GhostOrbitLinePatch's four measuring decisions may stamp render-window "
                    + "coverage, but RenderWindowCoverage.Inside/.Outside appears in: " + file
                    + ". Widening the stamp widens the line-blink exemption - see "
                    + "MapRenderTrace.ClassifyLineToggle.");
                foreach (Match m in hits)
                {
                    if (m.Groups[1].Value == "Inside") inside++; else outside++;
                }
            }

            Assert.True(outside == 2,
                "Expected EXACTLY 2 RenderWindowCoverage.Outside stamps (past-body-frame-end / "
                + "before-body-frame-start, and the parking-conic loiter hold); found " + outside + ".");
            Assert.True(inside == 2,
                "Expected EXACTLY 2 RenderWindowCoverage.Inside stamps (director-stockconic-visible, "
                + "visible-body-frame); found " + inside + ".");
        }

        /// <summary>The stamp must stay OPT-IN and single-sourced: both seams default to
        /// <c>Unknown</c>, and <c>RecordLineIntent</c> has exactly ONE production call site, so
        /// <c>GhostOrbitLinePatch</c> is provably the only thing that can write coverage at all.</summary>
        [Fact]
        public void CoverageStamp_DefaultsToUnknown_AndHasOneWriter()
        {
            string repoRoot = ResolveRepoRoot();
            string sourceDir = Path.Combine(repoRoot, "Source", "Parsek");

            string patch = File.ReadAllText(
                Path.Combine(sourceDir, "Patches", "GhostOrbitLinePatch.cs"));
            string trace = File.ReadAllText(Path.Combine(sourceDir, "MapRenderTrace.cs"));
            Assert.Contains("RenderWindowCoverage.Unknown", patch);
            Assert.Contains("RenderWindowCoverage windowCoverage = RenderWindowCoverage.Unknown", trace);

            var callRe = new Regex(@"RecordLineIntent\s*\(", RegexOptions.CultureInvariant);
            int callSites = 0;
            foreach (string file in Directory.GetFiles(sourceDir, "*.cs", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(file), "MapRenderTrace.cs", StringComparison.Ordinal))
                    continue;   // the declaration itself
                callSites += callRe.Matches(File.ReadAllText(file)).Count;
            }
            Assert.True(callSites == 1,
                "RecordLineIntent must have EXACTLY one production call site (GhostOrbitLinePatch's "
                + "LogOrbitLineDecision); found " + callSites + ". A second writer could stamp coverage "
                + "without tripping the enum-spelling gate above.");

            // AND close the hole the two gates above leave BY CONSTRUCTION: the spelling gate skips
            // MapRenderTrace.cs (it legitimately compares against the enum inside ClassifyLineToggle),
            // and the call-site count skips it too (it declares RecordLineIntent). So a second writer
            // added INSIDE the tracer - `lineIntentByPid[key] = new LineRenderIntent { WindowCoverage =
            // RenderWindowCoverage.Inside }` - would trip neither. The intent store is the single
            // channel every stamp must pass through, so pin its assignment sites directly: exactly one,
            // the one inside RecordLineIntent.
            var storeWriteRe = new Regex(
                @"lineIntentByPid\s*\[[^\]]*\]\s*=", RegexOptions.CultureInvariant);
            int storeWrites = storeWriteRe.Matches(trace).Count;
            Assert.True(storeWrites == 1,
                "lineIntentByPid must have EXACTLY one assignment site (inside RecordLineIntent); found "
                + storeWrites + ". A second writer inside MapRenderTrace.cs is invisible to both gates "
                + "above, because each of them deliberately skips that file.");
        }

        private static string ResolveRepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "scripts"))
                    && Directory.Exists(Path.Combine(dir, "Source")))
                {
                    return dir;
                }
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException(
                "Could not locate repo root from " + AppContext.BaseDirectory);
        }
    }
}
