using System;
using System.Collections.Generic;
using Parsek;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-7 guard for <see cref="FailClosedClassifier"/> (migration plan §9 / design §4 / §9.2 / §10):
    /// the fail-closed DECISION that classifies the three UNSUPPORTED synthetic producers (nested-SOI Jool
    /// tour / moving-target station / cross-SOI whole-chain) as
    /// <see cref="SegmentProvenance.FaithfulFallback"/> — render the recorded trajectory verbatim, never a
    /// broken synthetic guess.
    ///
    /// Covers (1) the fail-closed decision PER CASE (cross-SOI single-level stays SUPPORTED; nested-SOI ->
    /// fail closed; station -> fail closed), (2) the geometry-neutral provenance, and (3) the Tier-A
    /// <c>fail-closed-to-faithful</c> tracer event WIRING + once-per-event dedup, asserted via the pure
    /// detail builder + the MapRenderTrace signature-dict count seam (NOT the global
    /// <c>ParsekLog.TestSinkForTesting</c>, whose cross-test routing is the established flake source — see
    /// project memory).
    ///
    /// Each assertion states the bug it catches: a single-level cross-SOI auto-failing would needlessly
    /// degrade every ordinary interplanetary mission; a nested-SOI / station NOT failing would hand an
    /// unsupported producer a broken synthetic arc; a per-frame trace emit would spam the log.
    ///
    /// <para>The tracer-integration cases touch the shared <c>MapRenderTrace</c> static state, so this
    /// class runs in the Sequential collection.</para>
    /// </summary>
    [Collection("Sequential")]
    public class FailClosedClassifierTests
    {
        // A stock-shaped body parent chain: Sun (root) -> {Kerbin, Duna}; Kerbin -> {Mun, Minmus};
        // Jool -> {Laythe, Vall, Tylo}. ReferenceBodyName returns null for the root (Sun).
        private static readonly Dictionary<string, string> StockParents =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "Sun", null },
                { "Kerbin", "Sun" }, { "Duna", "Sun" }, { "Jool", "Sun" },
                { "Mun", "Kerbin" }, { "Minmus", "Kerbin" },
                { "Laythe", "Jool" }, { "Vall", "Jool" }, { "Tylo", "Jool" },
                { "Ike", "Duna" },
            };

        private static string Parent(string body)
            => StockParents.TryGetValue(body, out string p) ? p : null;

        // The LIVE KSP convention: the root body (Sun) is SELF-REFERENTIAL - the live
        // IBodyInfo.ReferenceBodyName("Sun") returns "Sun" (CelestialBody.referenceBody for the Sun is the
        // Sun itself), NOT null. The StockParents fake (Sun -> null) masked the in-game false-positive that
        // fail-closed every ordinary interplanetary transfer; this fake reproduces the live tree headlessly.
        private static readonly Dictionary<string, string> LiveSunParents =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "Sun", "Sun" }, // self-referential root (live KSP)
                { "Kerbin", "Sun" }, { "Duna", "Sun" }, { "Jool", "Sun" },
                { "Mun", "Kerbin" }, { "Minmus", "Kerbin" },
                { "Laythe", "Jool" }, { "Vall", "Jool" }, { "Tylo", "Jool" },
                { "Ike", "Duna" },
            };

        private static string LiveSunParent(string body)
            => LiveSunParents.TryGetValue(body, out string p) ? p : null;

        // ---- (1) The fail-closed decision per case ----

        [Fact]
        public void Classify_SingleLevelCrossSoiTransfer_IsSupported_NotFailClosed()
        {
            // Kerbin -> Sun -> Duna: an ORDINARY interplanetary mission that already renders correctly
            // through the per-crossing FlexibleSoi G0 path. v1 must NOT auto-fail it (that would degrade
            // every interplanetary mission to "fail-closed" for nothing — the cross-SOI kink is define-only,
            // render current behavior unchanged, design §9.2).
            var bodies = new List<string> { "Kerbin", "Sun", "Duna" };
            FailClosedClassifier.FailClosedDecision d = FailClosedClassifier.Classify(
                bodies, hasLiveVesselArrivalAnchor: false, referenceBodyName: Parent);

            Assert.False(d.IsFailClosed);
            Assert.Equal(FailClosedClassifier.FailClosedReason.None, d.Reason);
        }

        [Fact]
        public void Classify_SingleLevelCrossSoiTransfer_LiveSelfReferentialSun_IsSupported()
        {
            // The LIVE-tree regression for the in-game sign-off failure (FailClosedFaithfulInGameTest cross-SOI
            // facet): with the self-referential Sun (Parent("Sun") == "Sun", exactly what the live resolver
            // returns), an ordinary Kerbin -> Sun -> Duna interplanetary transfer must STILL be SUPPORTED.
            // Before the FindNestedRoot self-reference guard, the live convention flagged it nested-SOI and
            // fail-closed every interplanetary mission; the headless Sun -> null fake never exercised it.
            var bodies = new List<string> { "Kerbin", "Sun", "Duna" };
            FailClosedClassifier.FailClosedDecision d = FailClosedClassifier.Classify(
                bodies, hasLiveVesselArrivalAnchor: false, referenceBodyName: LiveSunParent);

            Assert.False(d.IsFailClosed);
            Assert.Equal(FailClosedClassifier.FailClosedReason.None, d.Reason);

            // The guard must NOT over-correct: a real Jool moon tour is still fail-closed under the live tree.
            FailClosedClassifier.FailClosedDecision jool = FailClosedClassifier.Classify(
                new List<string> { "Jool", "Laythe", "Tylo" }, hasLiveVesselArrivalAnchor: false,
                referenceBodyName: LiveSunParent);
            Assert.True(jool.IsFailClosed);
            Assert.Equal(FailClosedClassifier.FailClosedReason.NestedSoi, jool.Reason);
        }

        [Fact]
        public void Classify_KerbinMunTour_IsSupported_NotNested()
        {
            // Kerbin -> Mun -> Kerbin is single-level (only Mun under Kerbin; no sibling moon hop), so it is
            // SUPPORTED — not a nested-SOI Jool-style tour.
            var bodies = new List<string> { "Kerbin", "Mun", "Kerbin" };
            FailClosedClassifier.FailClosedDecision d = FailClosedClassifier.Classify(
                bodies, hasLiveVesselArrivalAnchor: false, referenceBodyName: Parent);

            Assert.False(d.IsFailClosed);
        }

        [Fact]
        public void Classify_JoolMoonTour_IsFailClosed_NestedSoi()
        {
            // Jool -> Laythe -> Tylo: a moon-rich tour (Laythe + Tylo are SIBLINGS under Jool, a non-root
            // ancestor) -> the synthetic moon-to-moon re-aim is unsupported -> fail closed to faithful.
            var bodies = new List<string> { "Jool", "Laythe", "Tylo" };
            FailClosedClassifier.FailClosedDecision d = FailClosedClassifier.Classify(
                bodies, hasLiveVesselArrivalAnchor: false, referenceBodyName: Parent);

            Assert.True(d.IsFailClosed);
            Assert.Equal(FailClosedClassifier.FailClosedReason.NestedSoi, d.Reason);
            Assert.Equal(SegmentProvenance.FaithfulFallback, d.Provenance);
            Assert.NotNull(d.NestedSubtree);
            Assert.Equal("Jool", d.NestedSubtree.RootBody);
            Assert.True(d.NestedSubtree.IsNested);
        }

        [Fact]
        public void Classify_KerbinTwoMoonsTour_IsFailClosed_NestedSoi()
        {
            // Kerbin -> Mun -> Minmus: Mun and Minmus are SIBLINGS under Kerbin (a non-root ancestor: Kerbin
            // itself orbits the Sun), so a two-moon hop under one planet is a nested-SOI tour -> fail closed
            // to faithful, with RootBody == the shared parent "Kerbin" (NOT the Jool-only signature). This is
            // the stock-system nested-SOI case (proves the classifier is not Jool-hardcoded): the moon-tour
            // signature is "two visited bodies share a non-root parent", which Kerbin's moons satisfy.
            var bodies = new List<string> { "Kerbin", "Mun", "Minmus" };
            FailClosedClassifier.FailClosedDecision d = FailClosedClassifier.Classify(
                bodies, hasLiveVesselArrivalAnchor: false, referenceBodyName: Parent);

            Assert.True(d.IsFailClosed);
            Assert.Equal(FailClosedClassifier.FailClosedReason.NestedSoi, d.Reason);
            Assert.Equal(SegmentProvenance.FaithfulFallback, d.Provenance);
            Assert.NotNull(d.NestedSubtree);
            Assert.Equal("Kerbin", d.NestedSubtree.RootBody);
            Assert.True(d.NestedSubtree.IsNested);
        }

        [Fact]
        public void Classify_StationArrivalAnchor_IsFailClosed_MovingTargetStation_HighestPrecedence()
        {
            // A live-vessel arrival anchor (a station) is the least-supported phase -> fail closed, and it
            // takes PRECEDENCE over the body sequence (even a Jool tour that ALSO has a live arrival anchor
            // reports moving-target-station, since the target is the moving station).
            var bodies = new List<string> { "Jool", "Laythe", "Tylo" };
            FailClosedClassifier.FailClosedDecision d = FailClosedClassifier.Classify(
                bodies, hasLiveVesselArrivalAnchor: true, referenceBodyName: Parent);

            Assert.True(d.IsFailClosed);
            Assert.Equal(FailClosedClassifier.FailClosedReason.MovingTargetStation, d.Reason);
            Assert.Equal(SegmentProvenance.FaithfulFallback, d.Provenance);
            Assert.Null(d.NestedSubtree); // station carries no nested subtree payload
        }

        [Fact]
        public void Classify_NullOrSingleBody_IsSupported()
        {
            Assert.False(FailClosedClassifier.Classify(
                null, hasLiveVesselArrivalAnchor: false, referenceBodyName: Parent).IsFailClosed);
            Assert.False(FailClosedClassifier.Classify(
                new List<string> { "Kerbin" }, hasLiveVesselArrivalAnchor: false, referenceBodyName: Parent)
                .IsFailClosed);
        }

        [Fact]
        public void Classify_NullReferenceDelegate_IsSupported_NotNested()
        {
            // A null parent-chain delegate cannot decide nesting -> not nested (the safe default), so a Jool
            // sequence with no body info is SUPPORTED (renders faithfully anyway). It must never NRE.
            var bodies = new List<string> { "Jool", "Laythe", "Tylo" };
            FailClosedClassifier.FailClosedDecision d = FailClosedClassifier.Classify(
                bodies, hasLiveVesselArrivalAnchor: false, referenceBodyName: null);
            Assert.False(d.IsFailClosed);
        }

        // ---- (2) The cross-SOI-chain reason (test/future-producer seam, NOT the live path) ----

        [Fact]
        public void ClassifyCrossSoiChainForTesting_MultiHop_IsCrossSoiChain()
        {
            // The deferred whole-patched-conic-chain synthesis's home: a >=2-crossing interplanetary chain.
            // This is a SEPARATE seam from the live Classify (the live path keeps it SUPPORTED so the
            // cross-SOI G0 kink renders unchanged); it exists only so the reason has a tested home.
            var bodies = new List<string> { "Kerbin", "Sun", "Duna", "Ike" }; // 3 single-level crossings
            FailClosedClassifier.FailClosedDecision d =
                FailClosedClassifier.ClassifyCrossSoiChainForTesting(bodies);
            Assert.True(d.IsFailClosed);
            Assert.Equal(FailClosedClassifier.FailClosedReason.CrossSoiChain, d.Reason);
        }

        [Fact]
        public void ClassifyCrossSoiChainForTesting_SingleHop_IsSupported()
        {
            var bodies = new List<string> { "Kerbin", "Sun" }; // 1 crossing
            Assert.False(FailClosedClassifier.ClassifyCrossSoiChainForTesting(bodies).IsFailClosed);
        }

        [Theory]
        [InlineData(new string[] { }, 0)]
        [InlineData(new[] { "Kerbin" }, 0)]
        [InlineData(new[] { "Kerbin", "Kerbin" }, 0)]
        [InlineData(new[] { "Kerbin", "Mun" }, 1)]
        [InlineData(new[] { "Kerbin", "Mun", "Kerbin" }, 2)]
        public void CountBodyChanges_CountsAdjacentDistinctRuns(string[] bodies, int expected)
        {
            Assert.Equal(expected, FailClosedClassifier.CountBodyChanges(bodies));
        }

        // ---- (3) The reason tokens ----

        [Fact]
        public void ReasonToken_AreGrepStableLowercase()
        {
            Assert.Equal("nested-soi", FailClosedClassifier.ReasonToken(
                FailClosedClassifier.FailClosedReason.NestedSoi));
            Assert.Equal("moving-target-station", FailClosedClassifier.ReasonToken(
                FailClosedClassifier.FailClosedReason.MovingTargetStation));
            Assert.Equal("cross-soi-chain", FailClosedClassifier.ReasonToken(
                FailClosedClassifier.FailClosedReason.CrossSoiChain));
            Assert.Equal("none", FailClosedClassifier.ReasonToken(
                FailClosedClassifier.FailClosedReason.None));
        }

        // ---- (4) MapRenderTrace integration: the fail-closed-to-faithful Tier-A event ----

        [Fact]
        public void FailClosedToFaithful_EventTokenIsStable()
        {
            Assert.Equal("fail-closed-to-faithful", MapRenderTrace.EventFailClosedToFaithful);
        }

        [Fact]
        public void BuildFailClosedDetails_NestedSoi_NamesProducerAndProvenanceAndPayload()
        {
            // The Tier-A detail line names the unsupported producer, the faithful-fallback provenance, the
            // render action, and (for nested-SOI) the subtree summary. Asserting the PURE builder (no global
            // sink) is deterministic in the full parallel suite.
            NestedSoiSubtree subtree = NestedSoiSubtree.TryBuildFromBodySequence(
                new List<string> { "Jool", "Laythe", "Tylo" }, Parent);
            var decision = new FailClosedClassifier.FailClosedDecision(
                FailClosedClassifier.FailClosedReason.NestedSoi, subtree);

            string details = FailClosedClassifier.BuildFailClosedDetails(decision);

            Assert.Contains("producer=nested-soi", details);
            Assert.Contains("provenance=faithful-fallback", details);
            Assert.Contains("action=render-recorded-verbatim", details);
            Assert.Contains("root=Jool", details);
        }

        [Fact]
        public void BuildFailClosedDetails_Station_NamesProducer_NoSubtreePayload()
        {
            var decision = new FailClosedClassifier.FailClosedDecision(
                FailClosedClassifier.FailClosedReason.MovingTargetStation, null);
            string details = FailClosedClassifier.BuildFailClosedDetails(decision);

            Assert.Contains("producer=moving-target-station", details);
            Assert.Contains("provenance=faithful-fallback", details);
            Assert.DoesNotContain("root=", details); // no nested subtree payload for the station case
        }

        [Fact]
        public void EmitFailClosedToFaithful_RecordsTraceOnce_OnChange()
        {
            // WIRING + rate-limit (deterministic, no ParsekLog global): a fail-closed decision reaches the
            // Tier-A emit gate exactly once, and a second same-reason emit is suppressed by the
            // once-per-event guard (no per-frame spam). Asserting via the MapRenderTrace signature-dict seam
            // (which EmitFailClosedToFaithful populates only when it actually reaches the emit) proves the
            // wiring without depending on the shared ParsekLog.TestSinkForTesting (the established flake
            // source — see project memory).
            NestedSoiSubtree subtree = NestedSoiSubtree.TryBuildFromBodySequence(
                new List<string> { "Jool", "Laythe", "Tylo" }, Parent);
            var decision = new FailClosedClassifier.FailClosedDecision(
                FailClosedClassifier.FailClosedReason.NestedSoi, subtree);

            bool prevForce = MapRenderTrace.ForceEnabledForTesting;
            try
            {
                MapRenderTrace.Reset(); // clear the per-pid once-per-event signature dict
                MapRenderTrace.ForceEnabledForTesting = true;
                MapRenderTrace.FrameCounterOverrideForTesting = () => 0; // Time.frameCount is a Unity ECall
                Assert.Equal(0, MapRenderTrace.FailClosedSignatureCountForTesting);

                FailClosedClassifier.EmitFailClosedToFaithful("rec-jool", 0, 1000.0, decision);
                Assert.Equal(1, MapRenderTrace.FailClosedSignatureCountForTesting); // wired: gate reached once

                FailClosedClassifier.EmitFailClosedToFaithful("rec-jool", 0, 1001.0, decision);
                Assert.Equal(1, MapRenderTrace.FailClosedSignatureCountForTesting); // unchanged onset => no dup
            }
            finally
            {
                MapRenderTrace.ForceEnabledForTesting = prevForce;
                MapRenderTrace.FrameCounterOverrideForTesting = null;
                MapRenderTrace.Reset();
            }
        }

        [Fact]
        public void ShouldEmitFailClosedOnChange_DirectPredicate_TrueRepeatFalse_ChangeTrue()
        {
            // Review S16: the wiring test above asserts via the signature-DICT SIZE, which cannot detect a
            // broken on-change predicate (a predicate that returns true every frame still keeps the dict at
            // size 1 - green while production spams one line per frame). Assert the predicate's RETURN
            // VALUES directly, mirroring the TangentSeamOnChange direct tests: true on first, false on the
            // unchanged repeat, true again on a signature change.
            bool prevForce = MapRenderTrace.ForceEnabledForTesting;
            try
            {
                MapRenderTrace.Reset();
                MapRenderTrace.ForceEnabledForTesting = true;
                MapRenderTrace.FrameCounterOverrideForTesting = () => 0;

                Assert.True(MapRenderTrace.ShouldEmitFailClosedOnChange("pid-1", "rec|nested-soi"));
                Assert.False(MapRenderTrace.ShouldEmitFailClosedOnChange("pid-1", "rec|nested-soi")); // steady
                Assert.True(MapRenderTrace.ShouldEmitFailClosedOnChange("pid-1", "rec|moving-target")); // change
                Assert.True(MapRenderTrace.ShouldEmitFailClosedOnChange("pid-2", "rec|nested-soi")); // other pid
            }
            finally
            {
                MapRenderTrace.ForceEnabledForTesting = prevForce;
                MapRenderTrace.FrameCounterOverrideForTesting = null;
                MapRenderTrace.Reset();
            }
        }

        [Fact]
        public void ShouldEmitFailClosedOnChange_Disabled_NeverEmits()
        {
            bool prevForce = MapRenderTrace.ForceEnabledForTesting;
            try
            {
                MapRenderTrace.Reset();
                MapRenderTrace.ForceEnabledForTesting = false;
                Assert.False(MapRenderTrace.ShouldEmitFailClosedOnChange("pid-1", "rec|nested-soi"));
            }
            finally
            {
                MapRenderTrace.ForceEnabledForTesting = prevForce;
                MapRenderTrace.Reset();
            }
        }

        [Fact]
        public void EmitFailClosedToFaithful_SupportedDecision_EmitsNothing()
        {
            // A SUPPORTED decision must NOT reach the emit gate (no spurious fail-closed line for a
            // correctly-rendering ordinary mission).
            bool prevForce = MapRenderTrace.ForceEnabledForTesting;
            try
            {
                MapRenderTrace.Reset();
                MapRenderTrace.ForceEnabledForTesting = true;
                MapRenderTrace.FrameCounterOverrideForTesting = () => 0;

                FailClosedClassifier.EmitFailClosedToFaithful(
                    "rec-supported", 0, 1000.0, FailClosedClassifier.FailClosedDecision.Supported);
                Assert.Equal(0, MapRenderTrace.FailClosedSignatureCountForTesting);
            }
            finally
            {
                MapRenderTrace.ForceEnabledForTesting = prevForce;
                MapRenderTrace.FrameCounterOverrideForTesting = null;
                MapRenderTrace.Reset();
            }
        }

        [Fact]
        public void EmitFailClosedToFaithful_TracingDisabled_EmitsNothing_FreeInNormalPlay()
        {
            // Tracing OFF (normal play): the emit is a no-op and never touches the signature dict, so the
            // fail-closed decision is free in normal play.
            bool prevForce = MapRenderTrace.ForceEnabledForTesting;
            try
            {
                MapRenderTrace.Reset();
                MapRenderTrace.ForceEnabledForTesting = false; // tracing off
                var decision = new FailClosedClassifier.FailClosedDecision(
                    FailClosedClassifier.FailClosedReason.NestedSoi, null);
                FailClosedClassifier.EmitFailClosedToFaithful("rec", 0, 1000.0, decision);
                Assert.Equal(0, MapRenderTrace.FailClosedSignatureCountForTesting);
            }
            finally
            {
                MapRenderTrace.ForceEnabledForTesting = prevForce;
                MapRenderTrace.Reset();
            }
        }
    }
}
