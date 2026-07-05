using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 8 (map/TS render overhaul, migration plan section 10) - the HEADLESS half of the
    /// tracer-coverage matrix guard: EMIT MACHINERY + SCHEMA coverage, NOT production call-site wiring
    /// (both halves - this and the in-game <c>MapRenderTracerCoverageInGameTest</c> - drive the emit
    /// entry points directly; the production call-site wiring is asserted by the source-text gate below,
    /// <c>MapRenderTracerCallSiteSourceGateTests</c>, plus tracing-on play sessions). This proves the
    /// tracer SCHEMA is complete without a live KSP:
    ///
    ///  - every <see cref="MapRenderTrace.RenderSurface"/> enum value (except <c>Unknown</c>) maps to a
    ///    distinct, non-"unknown" <c>surface=</c> token, so a new surface added without a token mapping (the
    ///    line would log <c>surface=unknown</c> and a grep slice would miss it) is caught here;
    ///  - each Tier-A / Tier-B / Tier-C emit (driven through the real <see cref="MapRenderTrace"/> sink with
    ///    the pure builders) lands a line carrying the expected <c>surface=</c> + <c>phase=</c> / <c>reason=</c>
    ///    token, so a renamed phase token / dropped surface argument / inverted gate is caught.
    ///
    /// Pure-builder + global-sink assertions (the robust pattern; no Unity-runtime dependency). Touches the
    /// shared <see cref="MapRenderTrace"/> registry + the <see cref="ParsekLog"/> sink, and sets
    /// <see cref="MapRenderTrace.FrameCounterOverrideForTesting"/> (Time.frameCount is a Unity ECall), so it
    /// runs Sequential, mirroring <c>MapRenderTraceTests</c>.
    /// </summary>
    [Collection("Sequential")]
    public class MapRenderTracerCoverageTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private const string Pid = "424242";
        private const string RecId = "tracer-matrix-rec";
        private const double Ut = 1000.0;

        public MapRenderTracerCoverageTests()
        {
            MapRenderTrace.Reset();
            MapRenderTrace.FrameCounterOverrideForTesting = () => 42;
            MapRenderTrace.ForceEnabledForTesting = true;
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true; // Tier-B lines route to Verbose
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

        // ---- Surface-token completeness: every RenderSurface maps to a distinct non-unknown token ----

        [Fact]
        public void EveryRenderSurface_HasDistinctNonUnknownToken()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (MapRenderTrace.RenderSurface surface in
                Enum.GetValues(typeof(MapRenderTrace.RenderSurface)))
            {
                if (surface == MapRenderTrace.RenderSurface.Unknown)
                    continue;

                string prefix = MapRenderTrace.FormatTracePrefixForTesting(
                    "Probe", surface, Pid, Ut, Ut, RecId);

                // The token slice the log grep relies on (surface=<token>).
                Assert.Contains("surface=", prefix);
                int idx = prefix.IndexOf("surface=", StringComparison.Ordinal) + "surface=".Length;
                int end = prefix.IndexOf(' ', idx);
                string token = end < 0 ? prefix.Substring(idx) : prefix.Substring(idx, end - idx);

                Assert.False(string.Equals(token, "unknown", StringComparison.Ordinal),
                    "RenderSurface." + surface + " has no surface= token (logs surface=unknown) - a grep "
                        + "slice on this surface would miss every line. Add it to RenderSurfaceToken.");
                Assert.True(seen.Add(token),
                    "RenderSurface." + surface + " shares the surface= token '" + token + "' with another "
                        + "value - the surfaces would be indistinguishable in the log.");
            }

            // Sanity floor: the v1 matrix has 6 named surfaces (ProtoOrbitLine / ProtoIcon / Polyline /
            // ImguiLabeledMarker / AtmosphericMarker / PolylineForwardArc). A drop below that is a regression.
            Assert.True(seen.Count >= 6,
                "expected at least 6 distinct render-surface tokens, found " + seen.Count);
        }

        // ---- Tier-A structural events carry surface= + phase= ----

        // RenderSurface is internal, so a public xUnit theory method cannot take it directly; pass the
        // underlying byte and cast inside (the InlineData enum literal is a compile-time constant byte).
        [Theory]
        [InlineData("GhostCreated", (byte)MapRenderTrace.RenderSurface.ProtoOrbitLine)]
        [InlineData("GhostDestroyed", (byte)MapRenderTrace.RenderSurface.ProtoOrbitLine)]
        [InlineData("FirstPosition", (byte)MapRenderTrace.RenderSurface.ProtoOrbitLine)]
        [InlineData("PhaseChainAssembled", (byte)MapRenderTrace.RenderSurface.ProtoOrbitLine)]
        [InlineData("DescentStitched", (byte)MapRenderTrace.RenderSurface.Polyline)]
        [InlineData("PolylineLegChange", (byte)MapRenderTrace.RenderSurface.Polyline)]
        [InlineData("PolylineLegChange", (byte)MapRenderTrace.RenderSurface.PolylineForwardArc)]
        [InlineData(MapRenderTrace.EventFailClosedToFaithful, (byte)MapRenderTrace.RenderSurface.ProtoOrbitLine)]
        public void TierA_StructuralEvent_RoutesToInfoWithSurfaceAndPhase(string phase, byte surfaceByte)
        {
            var surface = (MapRenderTrace.RenderSurface)surfaceByte;
            MapRenderTrace.EmitStructural(
                phase, surface, Pid, Ut, Ut, MapRenderTrace.SegmentChangeWindowSeconds,
                details: "k=v", recId: RecId);

            string surfaceToken = SurfaceToken(surface);
            Assert.Contains(logLines, l =>
                l.Contains("[MapRenderTrace]")
                && l.Contains("[INFO]")
                && l.Contains("phase=" + phase)
                && l.Contains("surface=" + surfaceToken)
                && l.Contains("recId=" + RecId));
        }

        // ---- Tier-B change-based events carry surface= + phase= and route to Verbose ----

        [Theory]
        [InlineData("MarkerDecision", (byte)MapRenderTrace.RenderSurface.ImguiLabeledMarker)]
        [InlineData("MarkerDecision", (byte)MapRenderTrace.RenderSurface.AtmosphericMarker)]
        [InlineData("LineVisibilityChange", (byte)MapRenderTrace.RenderSurface.ProtoOrbitLine)]
        [InlineData("icon-suppressed", (byte)MapRenderTrace.RenderSurface.ProtoIcon)]
        public void TierB_OnChangeEvent_RoutesToVerboseWithSurfaceAndPhase(string phase, byte surfaceByte)
        {
            var surface = (MapRenderTrace.RenderSurface)surfaceByte;
            MapRenderTrace.EmitOnChange(phase, surface, Pid, Ut, Ut, details: "k=v", recId: RecId);

            string surfaceToken = SurfaceToken(surface);
            Assert.Contains(logLines, l =>
                l.Contains("[MapRenderTrace]")
                && l.Contains("phase=" + phase)
                && l.Contains("surface=" + surfaceToken));
        }

        // ---- Tier-C anomalies carry surface= + reason= ----

        [Theory]
        [InlineData("icon-off-orbit", (byte)MapRenderTrace.RenderSurface.ProtoOrbitLine)]
        [InlineData("decision-vs-truth", (byte)MapRenderTrace.RenderSurface.ProtoOrbitLine)]
        [InlineData(MapRenderTrace.AnomalyParityDrift, (byte)MapRenderTrace.RenderSurface.ProtoOrbitLine)]
        [InlineData("polyline-orbit-overlap", (byte)MapRenderTrace.RenderSurface.Polyline)]
        [InlineData(MapRenderTrace.AnomalyRigidSeamTangentDiscontinuity, (byte)MapRenderTrace.RenderSurface.Polyline)]
        public void TierC_Anomaly_RoutesToInfoWithSurfaceAndReason(string reason, byte surfaceByte)
        {
            var surface = (MapRenderTrace.RenderSurface)surfaceByte;
            MapRenderTrace.EmitAnomaly(surface, Pid, Ut, Ut, reason, details: "k=v", recId: RecId);

            string surfaceToken = SurfaceToken(surface);
            Assert.Contains(logLines, l =>
                l.Contains("[MapRenderTrace]")
                && l.Contains("phase=Anomaly")
                && l.Contains("reason=" + reason)
                && l.Contains("surface=" + surfaceToken));
        }

        // ---- The polyline appear/disappear diff is pure: it carries BOTH event tokens ----

        [Fact]
        public void PolylineLegChange_DiffDrawnSets_ProducesAppearAndDisappear()
        {
            var appeared = new List<string>();
            var disappeared = new List<string>();

            // {} -> {rec} => appear
            Parsek.Display.GhostTrajectoryPolylineRenderer.DiffDrawnSets(
                new HashSet<string>(), new HashSet<string> { RecId }, appeared, disappeared);
            Assert.Contains(RecId, appeared);
            Assert.Empty(disappeared);

            // {rec} -> {} => disappear
            appeared.Clear();
            disappeared.Clear();
            Parsek.Display.GhostTrajectoryPolylineRenderer.DiffDrawnSets(
                new HashSet<string> { RecId }, new HashSet<string>(), appeared, disappeared);
            Assert.Empty(appeared);
            Assert.Contains(RecId, disappeared);
        }

        // The same token mapping production uses, so the matrix assertions match the live log slice exactly.
        private static string SurfaceToken(MapRenderTrace.RenderSurface surface)
        {
            string prefix = MapRenderTrace.FormatTracePrefixForTesting("X", surface, Pid, Ut, Ut, RecId);
            int idx = prefix.IndexOf("surface=", StringComparison.Ordinal) + "surface=".Length;
            int end = prefix.IndexOf(' ', idx);
            return end < 0 ? prefix.Substring(idx) : prefix.Substring(idx, end - idx);
        }
    }

    /// <summary>
    /// The PRODUCTION CALL-SITE half of the tracer-coverage guard (S13): a SOURCE-TEXT gate (the repo's
    /// established pattern, mirroring <c>FailClosedWiringSourceGateTests</c>) asserting the production
    /// files still CONTAIN the live tracer call tokens. The two tracer-coverage matrix tests above/in-game
    /// drive the emit MACHINERY directly and would stay green if a production call site were silently
    /// removed; this gate catches exactly that removal. Tokens are minimal + stable (the emit call name,
    /// plus the event token where the call carries one), read from the real call sites. Pure file reads,
    /// no shared static state - NOT in the Sequential collection.
    /// </summary>
    public class MapRenderTracerCallSiteSourceGateTests
    {
        private static string ReadProductionSource(params string[] relativeSegments)
        {
            string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string relative = System.IO.Path.Combine(relativeSegments);
            string path = System.IO.Path.Combine(root, "Source", "Parsek", relative);
            if (!System.IO.File.Exists(path))
                path = System.IO.Path.Combine(root, "Parsek", relative); // alt layout (mirrors sibling gates)
            Assert.True(System.IO.File.Exists(path), "Source file not found at " + path);
            return System.IO.File.ReadAllText(path);
        }

        [Fact]
        public void GhostMapPresence_EmitsGhostCreatedStructuralEvent_SourceGate()
        {
            // The Tier-A GhostCreated lifecycle event is emitted from the GhostMapPresence create funnel.
            // Losing this call site would silence ghost-lifecycle tracing while the matrix tests stay green.
            string src = ReadProductionSource("GhostMapPresence.cs");
            Assert.Contains("MapRenderTrace.EmitStructural(", src);
            Assert.Contains("\"GhostCreated\"", src);
        }

        [Fact]
        public void GhostOrbitLinePatch_EmitsLineVisibilityOnChange_SourceGate()
        {
            // The Tier-B LineVisibilityChange on-change event is emitted from the orbit-line decision
            // point (GhostOrbitLinePatch.LogOrbitLineDecision).
            string src = ReadProductionSource("Patches", "GhostOrbitLinePatch.cs");
            Assert.Contains("MapRenderTrace.EmitLineVisibilityOnChange(", src);
        }

        [Fact]
        public void MapRenderProbe_EmitsAnomalies_SourceGate()
        {
            // The Tier-C anomaly raises (icon-jump / line-blink / decision-vs-truth / parity-drift / ...)
            // are emitted from the end-of-frame truth probe.
            string src = ReadProductionSource("MapRenderProbe.cs");
            Assert.Contains("MapRenderTrace.EmitAnomaly(", src);
        }
    }
}
