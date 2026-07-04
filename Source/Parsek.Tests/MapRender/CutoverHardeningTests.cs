using System;
using System.Collections.Generic;
using System.IO;
using Parsek;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Cutover-hardening instrument layer (a stacked PR on Phase 8): the cold-load clock-readiness guard
    /// (B4/D2, design §11.2) and the three previously-unraised Tier-C cutover anomalies (C1:
    /// clock-not-ready / retire-not-held / anchor-resolve-fail). This is the HEADLESS half:
    ///
    ///  - the PURE predicates (<see cref="ShadowRenderDriver.IsLiveClockReady"/>,
    ///    <see cref="MapRenderTrace.IsRetireNotHeld"/>, <see cref="AnchorFrameResolver.OutcomeToken"/>);
    ///  - the once-per-event dedup gate (<see cref="MapRenderTrace.ShouldEmitCutoverAnomalyOnChange"/>);
    ///  - the three emit helpers land the right <c>reason=</c> token through the real
    ///    <see cref="MapRenderTrace"/> sink (pure-builder + global-sink, the robust pattern);
    ///  - source gates proving the RunFrame wiring (the flag-ON-only cold-load defer + the C4
    ///    flag-ON-assembler-fallback warn) exists and is flag-gated, so the flag-OFF path stays
    ///    byte-identical.
    ///
    /// The live Unity orchestration (RunFrame actually deferring at UT&lt;=0, the C4 one-shot firing) is an
    /// in-game test (RunFrame reads Time.frameCount + a live scene); these gates lock the decision logic +
    /// the schema it feeds. Touches the shared <see cref="MapRenderTrace"/> registry + the
    /// <see cref="ParsekLog"/> sink + the <see cref="MapRenderTrace.FrameCounterOverrideForTesting"/> seam,
    /// so it runs Sequential (mirroring MapRenderTraceTests / MapRenderTracerCoverageTests).
    /// </summary>
    [Collection("Sequential")]
    public class CutoverHardeningTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CutoverHardeningTests()
        {
            MapRenderTrace.Reset();
            MapRenderTrace.FrameCounterOverrideForTesting = () => 42;
            MapRenderTrace.ForceEnabledForTesting = true;
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            MapRenderTrace.Reset();
            MapRenderTrace.ForceEnabledForTesting = false;
            MapRenderTrace.FrameCounterOverrideForTesting = null;
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.SuppressLogging = true;
        }

        // ---- B4/D2 cold-load clock-readiness predicate ----

        [Theory]
        [InlineData(0.0, false)]      // the Planetarium UT=0 cold-load trap
        [InlineData(-1.0, false)]     // pre-time-init negative
        [InlineData(double.NaN, false)]
        [InlineData(double.PositiveInfinity, false)]
        [InlineData(double.NegativeInfinity, false)]
        [InlineData(1.0, true)]       // any positive finite UT is ready
        [InlineData(2.5e9, true)]     // a Duna-region looped UT is ready
        public void IsLiveClockReady_OnlyPositiveFiniteUt(double liveUT, bool expected)
        {
            // Mirrors LedgerOrchestrator.IsCurrentUtReadyForCutoff (ut > 0), extended with a finite guard
            // since the render path multiplies UT into geometry. A regression that let UT=0 through would
            // reintroduce the degenerate cold-load TS ghost the guard exists to prevent.
            Assert.Equal(expected, ShadowRenderDriver.IsLiveClockReady(liveUT));
        }

        [Fact]
        public void IsLiveClockReady_MatchesLedgerCutoffReadiness_ForPositiveUt()
        {
            // The two readiness gates must agree on the positive-UT contract (the guard is documented as
            // "mirroring LedgerOrchestrator.IsCurrentUtReadyForCutoff"). The render guard is STRICTER only
            // on non-finite UT (which the ledger never sees), so for finite UT they are identical.
            foreach (double ut in new[] { 0.0, 0.0001, 1.0, 1e6, 2.5e9 })
            {
                Assert.Equal(
                    LedgerOrchestrator.IsCurrentUtReadyForCutoff(ut),
                    ShadowRenderDriver.IsLiveClockReady(ut));
            }
        }

        // ---- C1 retire-not-held pure predicate ----

        [Fact]
        public void IsRetireNotHeld_OutsideWindowAndStillVisible_IsTrue()
        {
            // The defect: a sample resolved OUTSIDE its window (should retire) yet the member is still
            // visible because the prior intent was visible and was held.
            Assert.True(MapRenderTrace.IsRetireNotHeld(
                sampleOutsideWindow: true, priorVisible: true, resolvedVisible: true));
        }

        [Theory]
        [InlineData(false, true, true)]   // in-window (interior gap / in-segment): a hold is legitimate
        [InlineData(true, false, true)]   // nothing was held (no prior) -> a fresh visible is not a held-not-retire
        [InlineData(true, true, false)]   // correctly retired (resolved hidden) -> no defect
        [InlineData(false, false, false)] // nothing anywhere
        public void IsRetireNotHeld_OtherCombinations_AreFalse(
            bool outside, bool priorVisible, bool resolvedVisible)
        {
            Assert.False(MapRenderTrace.IsRetireNotHeld(outside, priorVisible, resolvedVisible));
        }

        // ---- AnchorFrameResolver outcome tokens (carried on the anchor-resolve-fail line) ----

        // BodyResolveOutcome is internal, so a public xUnit theory cannot take it directly; pass the
        // underlying byte and cast inside (mirroring MapRenderTracerCoverageTests' RenderSurface pattern).
        [Theory]
        [InlineData((byte)AnchorFrameResolver.BodyResolveOutcome.Resolved, "resolved")]
        [InlineData((byte)AnchorFrameResolver.BodyResolveOutcome.FailClosedMissingName, "fail-closed-missing-name")]
        [InlineData((byte)AnchorFrameResolver.BodyResolveOutcome.FailClosedUnknownBody, "fail-closed-unknown-body")]
        public void AnchorFrameResolver_OutcomeToken_IsGrepStable(byte outcomeByte, string expected)
        {
            var outcome = (AnchorFrameResolver.BodyResolveOutcome)outcomeByte;
            Assert.Equal(expected, AnchorFrameResolver.OutcomeToken(outcome));
        }

        [Fact]
        public void AnchorFrameResolver_OutcomeToken_OutOfRangeValue_IsUnknown()
        {
            // A defensive default so a future enum addition that forgets a token still emits a grep-stable
            // (non-empty, non-null) outcome rather than a blank slot on the anomaly line.
            var bogus = (AnchorFrameResolver.BodyResolveOutcome)200;
            Assert.Equal("unknown", AnchorFrameResolver.OutcomeToken(bogus));
        }

        [Fact]
        public void ResolveBodyAndRaise_FailClosedMissingName_EmitsAnchorResolveFailWithNoneBody()
        {
            // A null / empty frame body name fails closed as MISSING-NAME (distinct from unknown-body) and
            // still raises the anomaly; the empty body renders as the grep-stable <none> token so the line
            // is never a blank body= slot.
            var outcome = AnchorFrameResolver.ResolveBodyAndRaise(
                pid: 100037u, recordingId: "rec-anchor", currentUT: 9.0,
                bodyName: "", bodyExists: _ => true);

            Assert.Equal(AnchorFrameResolver.BodyResolveOutcome.FailClosedMissingName, outcome);
            Assert.Contains(logLines, l =>
                l.Contains("[MapRenderTrace]")
                && l.Contains("reason=" + MapRenderTrace.AnomalyAnchorResolveFail)
                && l.Contains("outcome=fail-closed-missing-name")
                && l.Contains("body=<none>")
                && l.Contains("recId=rec-anchor"));
        }

        [Fact]
        public void ResolveBodyAndRaise_NoOpWhenTracingDisabled()
        {
            // Tracing OFF (the default in normal play): the resolve still returns the fail-closed outcome
            // (the caller branches on it) but pays NO emit cost - the flag-OFF / tracing-OFF path is free.
            MapRenderTrace.ForceEnabledForTesting = false;
            var outcome = AnchorFrameResolver.ResolveBodyAndRaise(
                pid: 100037u, recordingId: "rec-anchor", currentUT: 9.0,
                bodyName: "Gone", bodyExists: _ => false);

            Assert.Equal(AnchorFrameResolver.BodyResolveOutcome.FailClosedUnknownBody, outcome);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("reason=" + MapRenderTrace.AnomalyAnchorResolveFail));
        }

        [Fact]
        public void ResolveBodyAndRaise_DedupesSamePidBodyOutcome_ReEmitsOnChangedBody()
        {
            // Steady-state: a body anchor that fails the same way every frame emits ONE line, not one per
            // frame (once-per-event via ShouldEmitCutoverAnomalyOnChange keyed on pid+reason, signature on
            // outcome+body). A DIFFERENT failing body on the same pid re-emits (the event changed).
            AnchorFrameResolver.ResolveBodyAndRaise(100037u, "rec", 1.0, "Gone", _ => false);
            int afterFirst = CountAnomaly(MapRenderTrace.AnomalyAnchorResolveFail);
            AnchorFrameResolver.ResolveBodyAndRaise(100037u, "rec", 2.0, "Gone", _ => false);
            AnchorFrameResolver.ResolveBodyAndRaise(100037u, "rec", 3.0, "Gone", _ => false);
            int afterRepeat = CountAnomaly(MapRenderTrace.AnomalyAnchorResolveFail);
            AnchorFrameResolver.ResolveBodyAndRaise(100037u, "rec", 4.0, "AlsoGone", _ => false);
            int afterChanged = CountAnomaly(MapRenderTrace.AnomalyAnchorResolveFail);

            Assert.Equal(1, afterFirst);
            Assert.Equal(1, afterRepeat); // repeated identical event deduped
            Assert.Equal(2, afterChanged); // changed body re-emits
        }

        [Fact]
        public void ResolveBodyAndRaise_FailClosedUnknownBody_EmitsAnchorResolveFail()
        {
            // A non-empty name the probe rejects (renamed / removed modded body) fails closed AND raises the
            // anchor-resolve-fail anomaly naming the outcome. The render result is the fail-closed (hide)
            // the caller already takes; this only makes it observable.
            var outcome = AnchorFrameResolver.ResolveBodyAndRaise(
                pid: 100037u, recordingId: "rec-anchor", currentUT: 1234.0,
                bodyName: "PlanetThatVanished", bodyExists: _ => false);

            Assert.Equal(AnchorFrameResolver.BodyResolveOutcome.FailClosedUnknownBody, outcome);
            Assert.Contains(logLines, l =>
                l.Contains("[MapRenderTrace]")
                && l.Contains("reason=" + MapRenderTrace.AnomalyAnchorResolveFail)
                && l.Contains("surface=ProtoOrbitLine")
                && l.Contains("body=PlanetThatVanished")
                && l.Contains("outcome=fail-closed-unknown-body")
                && l.Contains("recId=rec-anchor"));
        }

        [Fact]
        public void ResolveBodyAndRaise_Resolved_EmitsNothing()
        {
            var outcome = AnchorFrameResolver.ResolveBodyAndRaise(
                pid: 100037u, recordingId: "rec-anchor", currentUT: 1234.0,
                bodyName: "Kerbin", bodyExists: _ => true);

            Assert.Equal(AnchorFrameResolver.BodyResolveOutcome.Resolved, outcome);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("reason=" + MapRenderTrace.AnomalyAnchorResolveFail));
        }

        // ---- The three emit helpers land the right reason token ----

        [Fact]
        public void EmitClockNotReady_LandsClockNotReadyReason_OnDefer()
        {
            MapRenderTrace.EmitClockNotReady(liveUT: 0.0, ghostCount: 3);
            Assert.Contains(logLines, l =>
                l.Contains("[MapRenderTrace]")
                && l.Contains("phase=Anomaly")
                && l.Contains("reason=" + MapRenderTrace.AnomalyClockNotReady)
                && l.Contains("action=defer-render")
                && l.Contains("ghosts=3"));
        }

        [Fact]
        public void EmitRetireNotHeld_LandsRetireNotHeldReason()
        {
            MapRenderTrace.EmitRetireNotHeld(
                pid: 100037u, recId: "rec-retire", currentUT: 50.0, effUT: 50.0, treatmentToken: "StockConic");
            Assert.Contains(logLines, l =>
                l.Contains("[MapRenderTrace]")
                && l.Contains("reason=" + MapRenderTrace.AnomalyRetireNotHeld)
                && l.Contains("heldTreatment=StockConic")
                && l.Contains("action=should-retire")
                && l.Contains("recId=rec-retire"));
        }

        [Fact]
        public void EmitAnchorResolveFail_LandsAnchorResolveFailReason_Directly()
        {
            // The raise helper exercised directly (not via ResolveBodyAndRaise): the reason + body + outcome
            // tokens land on a ProtoOrbitLine anomaly line.
            MapRenderTrace.EmitAnchorResolveFail(
                pid: 100037u, recId: "rec-direct", currentUT: 7.0,
                bodyName: "Gone", outcomeToken: "fail-closed-unknown-body");
            Assert.Contains(logLines, l =>
                l.Contains("[MapRenderTrace]")
                && l.Contains("reason=" + MapRenderTrace.AnomalyAnchorResolveFail)
                && l.Contains("surface=ProtoOrbitLine")
                && l.Contains("body=Gone")
                && l.Contains("outcome=fail-closed-unknown-body")
                && l.Contains("action=fail-closed")
                && l.Contains("recId=rec-direct"));
        }

        [Fact]
        public void EmitRetireNotHeld_ReEmitsWhenHeldTreatmentChanges_OnSamePid()
        {
            // The signature includes the held treatment so a member that flips from one held treatment to a
            // different one re-emits (the defect changed character), while a steady identical hold dedups.
            MapRenderTrace.EmitRetireNotHeld(100037u, "rec", 1.0, 1.0, "StockConic");
            int afterFirst = CountAnomaly(MapRenderTrace.AnomalyRetireNotHeld);
            MapRenderTrace.EmitRetireNotHeld(100037u, "rec", 2.0, 2.0, "StockConic");
            int afterRepeat = CountAnomaly(MapRenderTrace.AnomalyRetireNotHeld);
            MapRenderTrace.EmitRetireNotHeld(100037u, "rec", 3.0, 3.0, "TracedPath");
            int afterChanged = CountAnomaly(MapRenderTrace.AnomalyRetireNotHeld);

            Assert.Equal(1, afterFirst);
            Assert.Equal(1, afterRepeat); // identical hold deduped
            Assert.Equal(2, afterChanged); // changed held treatment re-emits
        }

        [Fact]
        public void EmitHelpers_AreNoOpWhenTracingDisabled()
        {
            // All three raises short-circuit on IsEnabled BEFORE any formatting work, so the flag-OFF /
            // tracing-OFF default-play path is byte-identical (no log line, no dict mutation).
            MapRenderTrace.ForceEnabledForTesting = false;
            MapRenderTrace.EmitClockNotReady(0.0, 3);
            MapRenderTrace.EmitRetireNotHeld(100037u, "rec", 1.0, 1.0, "StockConic");
            MapRenderTrace.EmitAnchorResolveFail(100037u, "rec", 1.0, "Gone", "fail-closed-unknown-body");

            Assert.DoesNotContain(logLines, l => l.Contains("[MapRenderTrace]"));
            Assert.Equal(0, MapRenderTrace.CutoverAnomalySignatureCountForTesting);
        }

        // ---- ShouldEmitCutoverAnomalyOnChange once-per-event dedup ----

        [Fact]
        public void ShouldEmitCutoverAnomaly_EmitsOncePerSignatureThenSuppresses()
        {
            const string key = "100037:retire-not-held";
            const string sig = "retire-not-held:StockConic";
            Assert.True(MapRenderTrace.ShouldEmitCutoverAnomalyOnChange(key, sig)); // first -> emit
            Assert.False(MapRenderTrace.ShouldEmitCutoverAnomalyOnChange(key, sig)); // unchanged -> suppress
            Assert.False(MapRenderTrace.ShouldEmitCutoverAnomalyOnChange(key, sig)); // still suppressed
            // A changed signature for the same key re-emits (e.g. the held treatment changed).
            Assert.True(MapRenderTrace.ShouldEmitCutoverAnomalyOnChange(key, "retire-not-held:TracedPath"));
        }

        [Fact]
        public void ShouldEmitCutoverAnomaly_NoOpWhenDisabled()
        {
            MapRenderTrace.ForceEnabledForTesting = false;
            Assert.False(MapRenderTrace.ShouldEmitCutoverAnomalyOnChange("k", "s"));
        }

        [Fact]
        public void ShouldEmitCutoverAnomaly_EmptyKey_AllowedThrough()
        {
            // A headless / no-resolvable-ghost path passes an empty key; the gate lets it through (the caller
            // still bound the emit + the IsEnabled gate already fired), mirroring the sibling gates.
            Assert.True(MapRenderTrace.ShouldEmitCutoverAnomalyOnChange("", "anything"));
            Assert.True(MapRenderTrace.ShouldEmitCutoverAnomalyOnChange(null, "anything"));
        }

        [Fact]
        public void EmitClockNotReady_IsDedupedOncePerEvent_NotPerFrame()
        {
            // The cold-load defer runs every frame the clock is not ready; the anomaly must fire ONCE per
            // event, not per frame (the VerboseRateLimited convention the sibling structural events use).
            MapRenderTrace.EmitClockNotReady(0.0, 2);
            int afterFirst = CountAnomaly(MapRenderTrace.AnomalyClockNotReady);
            MapRenderTrace.EmitClockNotReady(0.0, 2);
            MapRenderTrace.EmitClockNotReady(0.0, 2);
            int afterThird = CountAnomaly(MapRenderTrace.AnomalyClockNotReady);

            Assert.Equal(1, afterFirst);
            Assert.Equal(1, afterThird); // still one: the repeat frames are deduped
        }

        [Fact]
        public void ShouldEmitCutoverAnomaly_WarpCapBoundsTheDict()
        {
            // At high time warp a fresh per-cycle key could be minted without bound within a scene
            // (Reset only fires on scene switch). The dict is capped (clearing is correctness-neutral:
            // each active site simply re-emits its current signature on its next change). Mint well past
            // the cap and assert it never grows unbounded.
            for (int i = 0; i < 5000; i++)
                MapRenderTrace.ShouldEmitCutoverAnomalyOnChange(
                    "key" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), "sig");
            Assert.True(MapRenderTrace.CutoverAnomalySignatureCountForTesting <= 4096,
                "the cutover-anomaly dict must be warp-capped, was "
                    + MapRenderTrace.CutoverAnomalySignatureCountForTesting);
        }

        private int CountAnomaly(string reason)
        {
            int n = 0;
            foreach (string l in logLines)
                if (l.Contains("[MapRenderTrace]") && l.Contains("reason=" + reason))
                    n++;
            return n;
        }

        // ---- Source gates: the RunFrame wiring is flag-gated (flag-OFF byte-identical) ----

        [Fact]
        public void RunFrame_ColdLoadGuard_Unconditional_AndDefers_SourceGate()
        {
            // Phase 5b (flag removed): the cold-load defer is UNCONDITIONAL - the spine always drives, so
            // the guard must gate the whole frame on the live clock alone and RETURN (defer) when the
            // clock is not ready. A regression that dropped the guard or sampled anyway at UT<=0 is
            // caught here.
            string src = CollapseWhitespace(StripLineComments(ReadMapRenderSource("ShadowRenderDriver.cs")));
            Assert.Contains("if (!IsLiveClockReady(currentUT))", src);
            Assert.Contains("MapRenderTrace.EmitClockNotReady(currentUT, pids?.Count ?? 0);", src);
        }

        [Fact]
        public void RunFrame_AssemblerExceptionFallback_WarnsLoudly_SourceGate()
        {
            // Phase 5b keep-decision: the assembler chain survives ONLY as the exception fallback for a
            // PhaseFactory throw (phaseChain null), and that fallback must WARN loudly (once per pid) so
            // a throwing factory is visible. A regression that deleted the warn (a silent fallback) or
            // deleted the fallback itself (dropping ghosts on a factory throw) is caught here.
            string src = CollapseWhitespace(StripLineComments(ReadMapRenderSource("ShadowRenderDriver.cs")));
            Assert.Contains("WarnSpineAssemblerFallback(pid, traj.RecordingId, currentUT);", src);
            Assert.Contains("ChainSampler.Sample(chain, currentUT, units)", src);
        }

        [Fact]
        public void RunFrame_RetireNotHeld_IsTracingGated_SourceGate()
        {
            // The retire-not-held raise must be behind MapRenderTrace.IsEnabled (tracing-gated) so it is
            // free in normal play, and it reads the pure IsRetireNotHeld predicate on the resolved sample.
            string src = CollapseWhitespace(StripLineComments(ReadMapRenderSource("ShadowRenderDriver.cs")));
            Assert.Contains("MapRenderTrace.IsEnabled && MapRenderTrace.IsRetireNotHeld(", src);
            Assert.Contains("sample.Coverage == Coverage.OutsideWindow, prior.Visible, intent.Visible", src);
        }

        [Fact]
        public void RunFrame_AnchorResolveFail_IsTracingGated_SourceGate()
        {
            // The anchor-resolve-fail raise must be behind MapRenderTrace.IsEnabled and route through the
            // pure AnchorFrameResolver.ResolveBodyAndRaise decision (not a bespoke inline body check).
            string src = CollapseWhitespace(StripLineComments(ReadMapRenderSource("ShadowRenderDriver.cs")));
            Assert.Contains("AnchorFrameResolver.ResolveBodyAndRaise(", src);
        }

        // ---- C4 one-shot warn seam: the per-pid fallback-warned set is scene-switch cleared ----

        [Fact]
        public void SpineFallbackWarnedPidSet_IsClearedOnReset()
        {
            // The C4 silent-fallback warn is one-shot PER PID, guarded by a HashSet cleared in Reset()
            // (scene switch) + PruneStaleState (ghost retire) so a re-entered scene re-warns the operator
            // on a still-stale flag toggle. The warn site itself is private (driven only from the Unity
            // RunFrame), so the live one-shot firing is the in-game test's job; here we lock the seam +
            // the scene-switch clear (a regression that forgot to clear the set would silence the warn
            // forever after the first scene). The count is 0 in a fresh fixture and stays 0 after Reset.
            Parsek.MapRender.ShadowRenderDriver.Reset();
            Assert.Equal(0, Parsek.MapRender.ShadowRenderDriver.SpineFallbackWarnedPidCountForTesting);
        }

        [Fact]
        public void RunFrame_ColdLoadGuard_DoesNotPruneStaleState_SourceGate()
        {
            // The cold-load defer is a HOLD, not a teardown: it must NOT call PruneStaleState (which would
            // drop the cached chains / prior intents the next ready frame resumes from). A regression that
            // pruned on a transient UT=0 frame would lose the resume state. The documented intent marker
            // (a comment, read from the RAW source) is the contract.
            string rawSrc = ReadMapRenderSource("ShadowRenderDriver.cs");
            Assert.Contains("PruneStaleState is intentionally NOT called", rawSrc);
            // The defer block ends in a bare `return;` (no PruneStaleState before it): with comments stripped
            // the rate-limit log's close `5.0);` is immediately followed by the defer `return;` and the block
            // close `}`.
            string collapsed = CollapseWhitespace(StripLineComments(rawSrc));
            Assert.Contains("5.0); return; }", collapsed);
        }

        // Mirrors ShadowRenderDriverTests.ReadMapRenderSource (root resolve + Parsek/-rooted fallback).
        private static string ReadMapRenderSource(string fileName)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(root, "Source", "Parsek", "MapRender", fileName);
            if (!File.Exists(path))
                path = Path.Combine(root, "Parsek", "MapRender", fileName);
            Assert.True(File.Exists(path), $"Source file not found at {path}");
            return File.ReadAllText(path);
        }

        private static string StripLineComments(string source)
        {
            var sb = new System.Text.StringBuilder(source.Length);
            foreach (string line in source.Split('\n'))
            {
                int idx = line.IndexOf("//", StringComparison.Ordinal);
                sb.Append(idx >= 0 ? line.Substring(0, idx) : line);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static string CollapseWhitespace(string source)
        {
            var sb = new System.Text.StringBuilder(source.Length);
            bool inWs = false;
            foreach (char c in source)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!inWs) { sb.Append(' '); inWs = true; }
                }
                else { sb.Append(c); inWs = false; }
            }
            return sb.ToString();
        }
    }
}
