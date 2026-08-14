using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// LINE-BLINK-JUMP-STRADDLE-DETECTOR-GAP: the WINDOW-EXIT exemption on the gated Tier-C
    /// <c>line-blink</c> anomaly, plus the cells that pin it CANNOT mask a real blink.
    ///
    /// <para>The exemption's whole claim is a single sentence: <b>a line that goes dark because the
    /// drive clock left the recording's rendered body-frame window is a legitimate transition, not a
    /// flicker.</b> A REAL blink is its negation - a line that toggles off and back on while the ghost
    /// is STILL INSIDE its window - so the two are mutually exclusive by construction rather than by
    /// threshold. These cells hold that line from both directions: the "still raises" cells here would
    /// fail the moment the exemption widened past its positive discriminator, and
    /// <see cref="WindowExitStamp_IsConfinedToTheTwoWindowExitDecisions"/> fails if a new decision site
    /// starts claiming the exemption at the source.</para>
    ///
    /// <para>Fixture UTs / frames / bodies below are the ARCHIVED values from the four measured raises,
    /// not invented ones - see each cell's citation.</para>
    /// </summary>
    public class LineBlinkWindowExitExemptionTests
    {
        // ---------------------------------------------------------------------------------
        // IsWindowExitOffToggle - the positive discriminator, one cell per FAIL-CLOSED conjunct
        // ---------------------------------------------------------------------------------

        [Fact]
        public void WindowExitOffToggle_AllFourConjuncts_IsWindowExit()
        {
            Assert.True(MapRenderTrace.IsWindowExitOffToggle(
                lineDefinitivelyOff: true,
                hasFreshIntent: true,
                intentLineActive: false,
                intentOutsideRenderWindow: true));
        }

        [Fact]
        public void WindowExitOffToggle_DegenerateLineRead_IsNotWindowExit()
        {
            // "(line-null)" / "(no-renderer)" / "(read-err:...)" reads land here: we do not know what
            // the line did, and not knowing is never evidence of a legitimate transition.
            Assert.False(MapRenderTrace.IsWindowExitOffToggle(
                lineDefinitivelyOff: false,
                hasFreshIntent: true,
                intentLineActive: false,
                intentOutsideRenderWindow: true));
        }

        [Fact]
        public void WindowExitOffToggle_NoFreshIntent_IsNotWindowExit()
        {
            // Our decision hook did not run this frame, so nothing measured the clock against the
            // window. A line KSP or another patch darkened behind our back must still raise.
            Assert.False(MapRenderTrace.IsWindowExitOffToggle(
                lineDefinitivelyOff: true,
                hasFreshIntent: false,
                intentLineActive: false,
                intentOutsideRenderWindow: true));
        }

        [Fact]
        public void WindowExitOffToggle_DecisionDisagreesWithTruth_IsNotWindowExit()
        {
            // Decision says ON, truth reads OFF. That is the decision-vs-truth anomaly's business and
            // must not be laundered into a line-blink exemption.
            Assert.False(MapRenderTrace.IsWindowExitOffToggle(
                lineDefinitivelyOff: true,
                hasFreshIntent: true,
                intentLineActive: true,
                intentOutsideRenderWindow: true));
        }

        [Fact]
        public void WindowExitOffToggle_InsideWindowReason_IsNotWindowExit()
        {
            // THE load-bearing conjunct. Every within-window OFF reason (polyline-owns-phase,
            // director-traced-path-suppress, below-atmosphere, stale-segment-awaiting-reseed,
            // post-polyline-release-grace, director-terminal-suppress) leaves this false.
            Assert.False(MapRenderTrace.IsWindowExitOffToggle(
                lineDefinitivelyOff: true,
                hasFreshIntent: true,
                intentLineActive: false,
                intentOutsideRenderWindow: false));
        }

        // ---------------------------------------------------------------------------------
        // ResolveOffEdgeOutsideRenderWindow - picking the OFF half of the toggle pair
        // ---------------------------------------------------------------------------------

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void OffEdgeResolve_DarkEdge_UsesThisFramesVerdict(bool currentVerdict)
        {
            // Line just went out => THIS frame is the OFF half. Both V8 straddles land here.
            Assert.Equal(currentVerdict, MapRenderTrace.ResolveOffEdgeOutsideRenderWindow(
                lineIsLit: false,
                currentToggleIsWindowExitOff: currentVerdict,
                hasPriorToggleVerdict: true,
                priorToggleWasWindowExitOff: !currentVerdict));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void OffEdgeResolve_LitEdge_UsesPriorToggleVerdict(bool priorVerdict)
        {
            // Line just came back => the OFF half was the PREVIOUS toggle. All three V10 raises land
            // here. Note the current verdict is deliberately the OPPOSITE and must be ignored: a LIT
            // frame is never the OFF half of its own pair.
            Assert.Equal(priorVerdict, MapRenderTrace.ResolveOffEdgeOutsideRenderWindow(
                lineIsLit: true,
                currentToggleIsWindowExitOff: !priorVerdict,
                hasPriorToggleVerdict: true,
                priorToggleWasWindowExitOff: priorVerdict));
        }

        [Fact]
        public void OffEdgeResolve_LitEdge_NoPriorVerdict_IsNotExempt()
        {
            Assert.False(MapRenderTrace.ResolveOffEdgeOutsideRenderWindow(
                lineIsLit: true,
                currentToggleIsWindowExitOff: true,
                hasPriorToggleVerdict: false,
                priorToggleWasWindowExitOff: true));
        }

        // ---------------------------------------------------------------------------------
        // IsLineBlink - the guard itself, and the CANNOT-MASK pins
        // ---------------------------------------------------------------------------------

        [Fact]
        public void IsLineBlink_DefaultOffEdgeOutsideRenderWindow_PreservesLegacyBehavior()
        {
            // Every pre-existing call site omits the new argument and must be byte-identical.
            Assert.True(MapRenderTrace.IsLineBlink(
                toggled: true,
                hasLastToggleFrame: true,
                lastToggleFrame: 100,
                currentFrame: 103));
        }

        [Fact]
        public void IsLineBlink_WithinWindow_OffEdgeOutsideRenderWindow_NotBlink()
        {
            Assert.False(MapRenderTrace.IsLineBlink(
                toggled: true,
                hasLastToggleFrame: true,
                lastToggleFrame: 100,
                currentFrame: 103,
                bodyChanged: false,
                offWindowCovered: false,
                offEdgeOutsideRenderWindow: true));
        }

        [Fact]
        public void IsLineBlink_WithinWindow_OffEdgeInsideRenderWindow_StillBlink()
        {
            // THE CANNOT-MASK PIN. Identical to the cell above in every parameter except the
            // discriminator. If the exemption ever widened to "any OFF near a window", this reds.
            Assert.True(MapRenderTrace.IsLineBlink(
                toggled: true,
                hasLastToggleFrame: true,
                lastToggleFrame: 100,
                currentFrame: 103,
                bodyChanged: false,
                offWindowCovered: false,
                offEdgeOutsideRenderWindow: false));
        }

        [Fact]
        public void IsLineBlink_NoToggle_OffEdgeOutsideRenderWindow_StillNotBlink()
        {
            // The new guard must not invent a raise where there was no toggle.
            Assert.False(MapRenderTrace.IsLineBlink(
                toggled: false,
                hasLastToggleFrame: true,
                lastToggleFrame: 100,
                currentFrame: 103,
                bodyChanged: false,
                offWindowCovered: false,
                offEdgeOutsideRenderWindow: true));
        }

        // ---------------------------------------------------------------------------------
        // The FOUR archived raises, replayed end to end through both predicates
        // ---------------------------------------------------------------------------------

        /// <summary>Replay one archived raise: resolve the OFF half, then ask the detector.</summary>
        private static bool RaisesAfterExemption(
            bool lineIsLit,
            bool currentToggleIsWindowExitOff,
            bool priorToggleWasWindowExitOff,
            int lastToggleFrame,
            int currentFrame,
            bool bodyChanged,
            bool offWindowCovered)
        {
            bool offEdge = MapRenderTrace.ResolveOffEdgeOutsideRenderWindow(
                lineIsLit: lineIsLit,
                currentToggleIsWindowExitOff: currentToggleIsWindowExitOff,
                hasPriorToggleVerdict: true,
                priorToggleWasWindowExitOff: priorToggleWasWindowExitOff);
            return MapRenderTrace.IsLineBlink(
                toggled: true,
                hasLastToggleFrame: true,
                lastToggleFrame: lastToggleFrame,
                currentFrame: currentFrame,
                bodyChanged: bodyChanged,
                offWindowCovered: offWindowCovered,
                offEdgeOutsideRenderWindow: offEdge);
        }

        [Fact]
        public void ArchivedRaise_V8Eve_1111_DarkEdgeStraddle_IsNowExempt()
        {
            // logs/2026-08-11_1111_V8-eve-player-loop/KSP.log:12230
            //   reason=line-blink lineActive=False prevActive=True lastToggleFrame=7839
            //   sinceFrames=4 body=Eve offWindowCovered=False polylinePainted=False
            // Same frame 7843 decision: reason=past-body-frame-end lineActive=False
            //   currentUT=30451100.0 bounds=[30360218.8,30450249.6]  => clock 850.4 s PAST the end.
            bool windowExit = MapRenderTrace.IsWindowExitOffToggle(
                lineDefinitivelyOff: true, hasFreshIntent: true,
                intentLineActive: false, intentOutsideRenderWindow: true);
            Assert.True(windowExit);
            Assert.False(RaisesAfterExemption(
                lineIsLit: false, currentToggleIsWindowExitOff: windowExit,
                priorToggleWasWindowExitOff: false,
                lastToggleFrame: 7839, currentFrame: 7843,
                bodyChanged: false, offWindowCovered: false));
        }

        [Fact]
        public void ArchivedRaise_V8Eve_1114_DarkEdgeStraddle_IsNowExempt()
        {
            // logs/2026-08-11_1114_V8-eve-player-loop/KSP.log:12099
            //   lineActive=False prevActive=True lastToggleFrame=7611 sinceFrames=7 body=Sun
            // Same frame 7618 decision: reason=past-body-frame-end lineActive=False
            //   currentUT=30360400.0 bounds=[26616878.0,30360218.8]  => 181.2 s PAST the end.
            Assert.False(RaisesAfterExemption(
                lineIsLit: false, currentToggleIsWindowExitOff: true,
                priorToggleWasWindowExitOff: false,
                lastToggleFrame: 7611, currentFrame: 7618,
                bodyChanged: false, offWindowCovered: false));
        }

        [Fact]
        public void ArchivedRaise_V10Dres_0627_LitEdgeRebind_IsNowExempt()
        {
            // logs/2026-08-12_0627_V10-dres-loop-arrival/KSP.log:11582
            //   lineActive=True prevActive=False lastToggleFrame=7218 sinceFrames=1 body=Sun
            // The OFF half is frame 7218: reason=before-body-frame-start lineActive=False
            //   currentUT=31276682.660 bounds=[31276682.7,43162584.5]  => BEFORE the window start.
            // The OPPOSITE edge from the V8 pair, which is why the prior-toggle stamp exists at all.
            Assert.False(RaisesAfterExemption(
                lineIsLit: true, currentToggleIsWindowExitOff: false,
                priorToggleWasWindowExitOff: true,
                lastToggleFrame: 7218, currentFrame: 7219,
                bodyChanged: false, offWindowCovered: false));
        }

        [Fact]
        public void ArchivedRaise_V10Dres_0632_LitEdgeRebind_IsNowExempt()
        {
            // logs/2026-08-12_0632_V10-dres-loop-arrival/KSP.log:11618
            //   currentUT=31276442.640 lineActive=True prevActive=False lastToggleFrame=7237
            //   sinceFrames=1 body=Sun. Iteration 3's moved escape bracket - the blink FOLLOWED the
            //   middle jump to its new UT, which is what proved the trigger is "any pre-D0 jump".
            Assert.False(RaisesAfterExemption(
                lineIsLit: true, currentToggleIsWindowExitOff: false,
                priorToggleWasWindowExitOff: true,
                lastToggleFrame: 7237, currentFrame: 7238,
                bodyChanged: false, offWindowCovered: false));
        }

        [Fact]
        public void ArchivedRaiseGeometry_ButOffHalfInsideWindow_StillRaises()
        {
            // THE NEGATIVE CONTROL for all four cells above: byte-identical frame geometry to the V10
            // _0627 raise (lit edge, sinceFrames=1, same body, uncovered dark window) with ONE fact
            // changed - the OFF half was decided INSIDE the window (e.g. polyline-owns-phase or a
            // stale-segment reseed lag). A real flicker at a real seam must still red the lane.
            Assert.True(RaisesAfterExemption(
                lineIsLit: true, currentToggleIsWindowExitOff: false,
                priorToggleWasWindowExitOff: false,
                lastToggleFrame: 7218, currentFrame: 7219,
                bodyChanged: false, offWindowCovered: false));

            // And the dark-edge direction of the same control (the V8 _1114 geometry).
            Assert.True(RaisesAfterExemption(
                lineIsLit: false, currentToggleIsWindowExitOff: false,
                priorToggleWasWindowExitOff: false,
                lastToggleFrame: 7611, currentFrame: 7618,
                bodyChanged: false, offWindowCovered: false));
        }

        // ---------------------------------------------------------------------------------
        // SOURCE GATE: the exemption may only be claimed by the two window-exit decisions
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// The strongest anti-over-reach pin, and the one that survives a future refactor: the
        /// <c>outsideRenderWindow: true</c> stamp must exist in EXACTLY ONE file
        /// (<c>GhostOrbitLinePatch.cs</c>) and EXACTLY TWICE - the two
        /// <c>LogOrbitLineDecision</c> calls inside the <c>pastEnd || beforeStart</c> block, whose
        /// branch condition IS the measurement. Any new site claiming the stamp widens the exemption
        /// and reds here rather than silently swallowing raises on a gated lane. The house analogue is
        /// the log validator's cannot-mask guarantee (<c>ParseSuppressionList</c> refusing to suppress
        /// FMT/WRN).
        /// </summary>
        [Fact]
        public void WindowExitStamp_IsConfinedToTheTwoWindowExitDecisions()
        {
            string repoRoot = ResolveRepoRoot();
            string sourceDir = Path.Combine(repoRoot, "Source", "Parsek");
            Assert.True(Directory.Exists(sourceDir), "Source/Parsek missing: " + sourceDir);

            var stampRe = new Regex(@"outsideRenderWindow\s*:\s*true", RegexOptions.CultureInvariant);
            int totalStamps = 0;
            foreach (string file in Directory.GetFiles(sourceDir, "*.cs", SearchOption.AllDirectories))
            {
                int hits = stampRe.Matches(File.ReadAllText(file)).Count;
                if (hits == 0)
                    continue;
                Assert.True(
                    string.Equals(Path.GetFileName(file), "GhostOrbitLinePatch.cs", StringComparison.Ordinal),
                    "The line-blink window-exit exemption may only be stamped by GhostOrbitLinePatch's "
                    + "past-body-frame-end / before-body-frame-start block, but 'outsideRenderWindow: true' "
                    + "appears in: " + file + ". Widening the stamp widens the exemption - see "
                    + "MapRenderTrace.IsWindowExitOffToggle.");
                totalStamps += hits;
            }

            Assert.True(totalStamps == 2,
                "Expected EXACTLY 2 'outsideRenderWindow: true' stamps in GhostOrbitLinePatch.cs (the "
                + "line-active parking-conic hold and the line-inactive window exit, both inside the "
                + "pastEnd || beforeStart block); found " + totalStamps + ".");
        }

        /// <summary>The stamp must stay OPT-IN: both the patch helper and the trace recorder default it
        /// to false, so a decision site that says nothing claims nothing.</summary>
        [Fact]
        public void WindowExitStamp_DefaultsToFalseAtBothSeams()
        {
            string repoRoot = ResolveRepoRoot();
            string patch = File.ReadAllText(
                Path.Combine(repoRoot, "Source", "Parsek", "Patches", "GhostOrbitLinePatch.cs"));
            string trace = File.ReadAllText(
                Path.Combine(repoRoot, "Source", "Parsek", "MapRenderTrace.cs"));

            Assert.Contains("bool outsideRenderWindow = false", patch);
            Assert.Contains("bool outsideRenderWindow = false", trace);
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
