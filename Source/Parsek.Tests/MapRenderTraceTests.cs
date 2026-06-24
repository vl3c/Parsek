using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase-1 unit coverage for the <see cref="MapRenderTrace"/> skeleton: the
    /// pure gate reason strings, the enabled/disabled toggle via
    /// <c>ForceEnabledForTesting</c>, the pid-keyed detailed-window registry, an
    /// <c>EmitRaw</c> log-line schema assertion, and the <c>mapRenderTracing</c>
    /// persistence round-trip. Touches shared static state (MapRenderTrace
    /// registry, ParsekLog sink, ParsekSettings override, the settings store), so
    /// it runs in the Sequential collection.
    /// </summary>
    [Collection("Sequential")]
    public class MapRenderTraceTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        private double clock = 1000.0;

        public MapRenderTraceTests()
        {
            MapRenderTrace.Reset();
            MapRenderTrace.ForceEnabledForTesting = false;
            MapRenderTrace.FrameCounterOverrideForTesting = () => 42;
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.ClockOverrideForTesting = () => clock;
            ParsekSettingsPersistence.ResetForTesting();
        }

        public void Dispose()
        {
            MapRenderTrace.Reset();
            MapRenderTrace.ForceEnabledForTesting = false;
            MapRenderTrace.FrameCounterOverrideForTesting = null;
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekSettingsPersistence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.SuppressLogging = true;
        }

        // ---- EvaluateGate reason strings ----

        [Fact]
        public void EvaluateGate_Force_EmitsImportantWithForceReason()
        {
            var gate = MapRenderTrace.EvaluateGate(
                currentUT: 100.0,
                firstSeenUT: 100.0,
                firstSeen: false,
                important: false,
                force: true,
                windowOpen: false);

            Assert.True(gate.Emit);
            Assert.True(gate.Important);
            Assert.Equal("force", gate.Reason);
        }

        [Fact]
        public void EvaluateGate_Important_EmitsImportantWithImportantReason()
        {
            var gate = MapRenderTrace.EvaluateGate(
                currentUT: 100.0,
                firstSeenUT: 100.0,
                firstSeen: false,
                important: true,
                force: false,
                windowOpen: false);

            Assert.True(gate.Emit);
            Assert.True(gate.Important);
            Assert.Equal("important", gate.Reason);
        }

        [Fact]
        public void EvaluateGate_FirstSeen_EmitsInitialWindow()
        {
            var gate = MapRenderTrace.EvaluateGate(
                currentUT: 100.0,
                firstSeenUT: 100.0,
                firstSeen: true,
                important: false,
                force: false,
                windowOpen: false);

            Assert.True(gate.Emit);
            Assert.False(gate.Important);
            Assert.Equal("initial-window", gate.Reason);
        }

        [Fact]
        public void EvaluateGate_WithinInitialWindow_EmitsInitialWindow()
        {
            var gate = MapRenderTrace.EvaluateGate(
                currentUT: 100.0 + MapRenderTrace.InitialWindowSeconds,
                firstSeenUT: 100.0,
                firstSeen: false,
                important: false,
                force: false,
                windowOpen: false);

            Assert.True(gate.Emit);
            Assert.False(gate.Important);
            Assert.Equal("initial-window", gate.Reason);
        }

        [Fact]
        public void EvaluateGate_WindowOpenAfterInitial_EmitsWindow()
        {
            var gate = MapRenderTrace.EvaluateGate(
                currentUT: 200.0,
                firstSeenUT: 100.0,
                firstSeen: false,
                important: false,
                force: false,
                windowOpen: true);

            Assert.True(gate.Emit);
            Assert.False(gate.Important);
            Assert.Equal("window", gate.Reason);
        }

        [Fact]
        public void EvaluateGate_NoWindowAfterInitial_Closed()
        {
            var gate = MapRenderTrace.EvaluateGate(
                currentUT: 200.0,
                firstSeenUT: 100.0,
                firstSeen: false,
                important: false,
                force: false,
                windowOpen: false);

            Assert.False(gate.Emit);
            Assert.False(gate.Important);
            Assert.Equal("closed", gate.Reason);
        }

        // ---- IsEnabled via ForceEnabledForTesting ----

        [Fact]
        public void OpenDetailedWindow_NoOpWhenDisabled()
        {
            MapRenderTrace.ForceEnabledForTesting = false;

            MapRenderTrace.OpenDetailedWindow("100037", 100.0, 5.0, "test");

            Assert.False(MapRenderTrace.IsDetailedWindowOpen("100037", 100.0));
        }

        [Fact]
        public void OpenDetailedWindow_OpensWhenForceEnabled()
        {
            MapRenderTrace.ForceEnabledForTesting = true;

            MapRenderTrace.OpenDetailedWindow("100037", 100.0, 5.0, "test");

            Assert.True(MapRenderTrace.IsDetailedWindowOpen("100037", 100.0));
        }

        [Fact]
        public void EmitRaw_NoOpWhenDisabled()
        {
            MapRenderTrace.ForceEnabledForTesting = false;

            MapRenderTrace.EmitRaw(
                important: true,
                phase: "GhostCreated",
                surface: MapRenderTrace.RenderSurface.ProtoIcon,
                pidKey: "100037",
                currentUT: 100.0,
                effUT: 100.0,
                details: "vessel=Munar_Probe");

            Assert.Empty(logLines);
        }

        // ---- OpenDetailedWindow / IsDetailedWindowOpen ----

        [Fact]
        public void DetailedWindow_OpenThenExpires()
        {
            MapRenderTrace.ForceEnabledForTesting = true;

            MapRenderTrace.OpenDetailedWindow("100037", 100.0, 2.0, "first");

            Assert.True(MapRenderTrace.IsDetailedWindowOpen("100037", 101.5));
            Assert.True(MapRenderTrace.IsDetailedWindowOpen("100037", 102.0));
            Assert.False(MapRenderTrace.IsDetailedWindowOpen("100037", 102.5));
        }

        [Fact]
        public void DetailedWindow_KeyedByPid()
        {
            MapRenderTrace.ForceEnabledForTesting = true;

            MapRenderTrace.OpenDetailedWindow("100037", 100.0, 5.0, "first");

            Assert.True(MapRenderTrace.IsDetailedWindowOpen("100037", 102.0));
            Assert.False(MapRenderTrace.IsDetailedWindowOpen("999999", 102.0));
        }

        [Fact]
        public void DetailedWindow_ExtendsToLatestUntil()
        {
            MapRenderTrace.ForceEnabledForTesting = true;

            MapRenderTrace.OpenDetailedWindow("100037", 100.0, 5.0, "first");
            // A shorter window must not shrink the already-open one.
            MapRenderTrace.OpenDetailedWindow("100037", 100.0, 1.0, "second");

            Assert.True(MapRenderTrace.IsDetailedWindowOpen("100037", 104.0));
        }

        // ---- EmitRaw line schema ----

        [Fact]
        public void EmitRaw_Important_RoutesToInfoWithPhaseAndSurfaceTokens()
        {
            MapRenderTrace.ForceEnabledForTesting = true;

            MapRenderTrace.EmitRaw(
                important: true,
                phase: "GhostCreated",
                surface: MapRenderTrace.RenderSurface.ProtoIcon,
                pidKey: "100037",
                currentUT: 1234.5,
                effUT: 1234.5,
                details: "vessel=Munar_Probe");

            Assert.Contains(logLines, l =>
                l.Contains("[MapRenderTrace]")
                && l.Contains("phase=GhostCreated")
                && l.Contains("surface=ProtoIcon")
                && l.Contains("pid=100037")
                && l.Contains("vessel=Munar_Probe"));
        }

        [Fact]
        public void EmitRaw_NonImportant_RoutesToVerbose()
        {
            MapRenderTrace.ForceEnabledForTesting = true;

            MapRenderTrace.EmitRaw(
                important: false,
                phase: "FirstPosition",
                surface: MapRenderTrace.RenderSurface.ProtoOrbitLine,
                pidKey: "100037",
                currentUT: 1234.5,
                effUT: 1234.5,
                details: "reason=first-apply");

            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE]")
                && l.Contains("[MapRenderTrace]")
                && l.Contains("phase=FirstPosition")
                && l.Contains("surface=ProtoOrbitLine"));
        }

        [Fact]
        public void FormatTracePrefix_CarriesPhaseSurfaceAndPid()
        {
            string prefix = MapRenderTrace.FormatTracePrefixForTesting(
                phase: "SegmentApplied",
                surface: MapRenderTrace.RenderSurface.AtmosphericMarker,
                pidKey: "100037",
                currentUT: 10.0,
                effUT: 9.5);

            Assert.Contains("phase=SegmentApplied", prefix);
            Assert.Contains("surface=AtmosphericMarker", prefix);
            Assert.Contains("pid=100037", prefix);
            Assert.Contains("currentUT=10.000", prefix);
            Assert.Contains("effUT=9.500", prefix);
            // recId defaults to <none> when not supplied, so every line is greppable by recId= uniformly.
            Assert.Contains("recId=<none>", prefix);
        }

        [Fact]
        public void FormatTracePrefix_CarriesRecId()
        {
            string prefix = MapRenderTrace.FormatTracePrefixForTesting(
                phase: "LineVisibilityChange",
                surface: MapRenderTrace.RenderSurface.ProtoOrbitLine,
                pidKey: "100037",
                currentUT: 10.0,
                effUT: 10.0,
                recId: "ab12cd34");

            Assert.Contains("pid=100037", prefix);
            Assert.Contains("recId=ab12cd34", prefix);
        }

        [Fact]
        public void FormatTracePrefix_PolylineForwardArcSurfaceToken()
        {
            string prefix = MapRenderTrace.FormatTracePrefixForTesting(
                phase: "PolylineLegChange",
                surface: MapRenderTrace.RenderSurface.PolylineForwardArc,
                pidKey: "rec-1",
                currentUT: 1.0,
                effUT: 1.0);

            // The forward arc is a DISTINCT surface from Polyline (fixes the FWD-ARC string-prefix conflation).
            Assert.Contains("surface=PolylineForwardArc", prefix);
        }

        // ---- Tier-A structural events (GhostCreated / GhostDestroyed / FirstPosition) ----

        [Fact]
        public void EmitStructural_NoOpWhenDisabled()
        {
            MapRenderTrace.ForceEnabledForTesting = false;

            MapRenderTrace.EmitStructural(
                "GhostCreated",
                MapRenderTrace.RenderSurface.ProtoIcon,
                "100037",
                100.0,
                100.0,
                MapRenderTrace.InitialWindowSeconds,
                "vessel=Munar_Probe");

            Assert.Empty(logLines);
            Assert.False(MapRenderTrace.IsDetailedWindowOpen("100037", 101.0));
        }

        [Fact]
        public void EmitStructural_GhostCreated_RoutesToInfoAndOpensWindow()
        {
            MapRenderTrace.ForceEnabledForTesting = true;

            MapRenderTrace.EmitStructural(
                "GhostCreated",
                MapRenderTrace.RenderSurface.ProtoIcon,
                "100037",
                1234.5,
                1234.5,
                MapRenderTrace.InitialWindowSeconds,
                MapRenderTrace.BuildLifecycleDetails(
                    "Munar Probe", "Mun", "TRACKSTATION",
                    new Vector3d(1.0, 2.0, 3.0), "tracking-station-lifecycle"));

            Assert.Contains(logLines, l =>
                l.Contains("[MapRenderTrace]")
                && l.Contains("phase=GhostCreated")
                && l.Contains("surface=ProtoIcon")
                && l.Contains("pid=100037")
                && l.Contains("vessel=Munar_Probe")
                && l.Contains("body=Mun")
                && l.Contains("scene=TRACKSTATION"));
            // The initial window opened so the surrounding frames get full detail.
            Assert.True(MapRenderTrace.IsDetailedWindowOpen(
                "100037", 1234.5 + MapRenderTrace.InitialWindowSeconds - 0.1));
        }

        [Fact]
        public void EmitStructural_GhostDestroyed_OpensShortWindow()
        {
            MapRenderTrace.ForceEnabledForTesting = true;

            MapRenderTrace.EmitStructural(
                "GhostDestroyed",
                MapRenderTrace.RenderSurface.ProtoIcon,
                "100037",
                2000.0,
                2000.0,
                MapRenderTrace.DestroyWindowSeconds,
                MapRenderTrace.BuildLifecycleDetails(
                    "Munar Probe", "Mun", "FLIGHT", null, "engine-destroyed"));

            Assert.Contains(logLines, l =>
                l.Contains("phase=GhostDestroyed")
                && l.Contains("reason=engine-destroyed"));
            Assert.True(MapRenderTrace.IsDetailedWindowOpen("100037", 2000.0));
            Assert.False(MapRenderTrace.IsDetailedWindowOpen(
                "100037", 2000.0 + MapRenderTrace.DestroyWindowSeconds + 0.1));
        }

        [Fact]
        public void BuildLifecycleDetails_OmitsWorldPosWhenNull()
        {
            string details = MapRenderTrace.BuildLifecycleDetails(
                "Munar Probe", "Mun", "FLIGHT", worldPos: null, reason: "destroy");

            Assert.Contains("vessel=Munar_Probe", details);
            Assert.Contains("body=Mun", details);
            Assert.Contains("scene=FLIGHT", details);
            Assert.DoesNotContain("worldPos=", details);
            Assert.Contains("reason=destroy", details);
        }

        [Fact]
        public void BuildLifecycleDetails_IncludesWorldPosWhenPresent()
        {
            string details = MapRenderTrace.BuildLifecycleDetails(
                "Probe", "Kerbin", "FLIGHT",
                new Vector3d(10.0, 20.0, 30.0), reason: null);

            Assert.Contains("worldPos=(10.00,20.00,30.00)", details);
            // A null reason is omitted entirely (no trailing reason= token).
            Assert.DoesNotContain("reason=", details);
        }

        [Fact]
        public void BuildLifecycleDetails_NullVesselAndBody_RenderAsNoneToken()
        {
            string details = MapRenderTrace.BuildLifecycleDetails(
                vesselName: null, bodyName: null, scene: "TRACKSTATION",
                worldPos: null, reason: "destroy");

            Assert.Contains("vessel=<none>", details);
            Assert.Contains("body=<none>", details);
        }

        [Fact]
        public void BuildFirstPositionDetails_CarriesWorldPosAndOrbitFields()
        {
            string details = MapRenderTrace.BuildFirstPositionDetails(
                new Vector3d(100.0, 200.0, 300.0),
                "Mun",
                sma: 850000.0,
                ecc: 0.0123,
                reason: "first-truth-read");

            Assert.Contains("worldPos=(100.00,200.00,300.00)", details);
            Assert.Contains("body=Mun", details);
            Assert.Contains("sma=850000", details);
            Assert.Contains("ecc=0.0123", details);
            Assert.Contains("reason=first-truth-read", details);
        }

        [Fact]
        public void EmitStructural_FirstPosition_RoutesToInfoWithOrbitFields()
        {
            MapRenderTrace.ForceEnabledForTesting = true;

            MapRenderTrace.EmitStructural(
                "FirstPosition",
                MapRenderTrace.RenderSurface.ProtoOrbitLine,
                "100037",
                500.0,
                500.0,
                MapRenderTrace.InitialWindowSeconds,
                MapRenderTrace.BuildFirstPositionDetails(
                    new Vector3d(1.0, 2.0, 3.0), "Mun", 850000.0, 0.01, "first-truth-read"));

            Assert.Contains(logLines, l =>
                l.Contains("[MapRenderTrace]")
                && l.Contains("phase=FirstPosition")
                && l.Contains("surface=ProtoOrbitLine")
                && l.Contains("pid=100037")
                && l.Contains("body=Mun")
                && l.Contains("sma=850000"));
        }

        // ---- In-window per-frame snapshot (Tier-B detail) ----

        [Fact]
        public void EmitWindowSnapshot_NoOpWhenNoWindowOpen()
        {
            MapRenderTrace.ForceEnabledForTesting = true;

            // No window opened for this pid: the snapshot is a no-op so steady
            // state is not spammed.
            MapRenderTrace.EmitWindowSnapshot(
                MapRenderTrace.RenderSurface.ProtoOrbitLine,
                "100037",
                1000.0,
                1000.0,
                "lineActive=True body=Mun");

            Assert.DoesNotContain(logLines, l => l.Contains("phase=Snapshot"));
        }

        [Fact]
        public void EmitWindowSnapshot_EmitsVerboseWhileWindowOpen()
        {
            MapRenderTrace.ForceEnabledForTesting = true;
            MapRenderTrace.OpenDetailedWindow("100037", 1000.0, 4.0, "test");

            MapRenderTrace.EmitWindowSnapshot(
                MapRenderTrace.RenderSurface.ProtoOrbitLine,
                "100037",
                1001.0,
                1001.0,
                "lineActive=True body=Mun");

            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE]")
                && l.Contains("[MapRenderTrace]")
                && l.Contains("phase=Snapshot")
                && l.Contains("surface=ProtoOrbitLine")
                && l.Contains("pid=100037")
                && l.Contains("body=Mun"));
        }

        [Fact]
        public void EmitWindowSnapshot_NoOpAfterWindowExpires()
        {
            MapRenderTrace.ForceEnabledForTesting = true;
            MapRenderTrace.OpenDetailedWindow("100037", 1000.0, 4.0, "test");

            // currentUT past the window end (1000 + 4): the snapshot stops.
            MapRenderTrace.EmitWindowSnapshot(
                MapRenderTrace.RenderSurface.ProtoOrbitLine,
                "100037",
                1005.0,
                1005.0,
                "lineActive=True body=Mun");

            Assert.DoesNotContain(logLines, l => l.Contains("phase=Snapshot"));
        }

        [Fact]
        public void EmitWindowSnapshot_NoOpWhenDisabled()
        {
            MapRenderTrace.ForceEnabledForTesting = false;
            // Even if a window were somehow open, a disabled tracer emits nothing.
            MapRenderTrace.OpenDetailedWindow("100037", 1000.0, 4.0, "test");

            MapRenderTrace.EmitWindowSnapshot(
                MapRenderTrace.RenderSurface.ProtoOrbitLine,
                "100037",
                1001.0,
                1001.0,
                "lineActive=True body=Mun");

            Assert.Empty(logLines);
        }

        // ---- mapRenderTracing persistence round-trip ----

        [Fact]
        public void GetStoredMapRenderTracing_DefaultsNull()
        {
            Assert.Null(ParsekSettingsPersistence.GetStoredMapRenderTracing());
        }

        [Fact]
        public void SetStoredMapRenderTracing_RoundTrips()
        {
            ParsekSettingsPersistence.SetStoredMapRenderTracingForTesting(true);
            Assert.True(ParsekSettingsPersistence.GetStoredMapRenderTracing().Value);
        }

        [Fact]
        public void RecordMapRenderTracing_UpdatesInMemoryStore()
        {
            ParsekSettingsPersistence.RecordMapRenderTracing(true);

            Assert.True(ParsekSettingsPersistence.GetStoredMapRenderTracing().Value);
        }

        [Fact]
        public void ResetForTesting_ClearsStoredMapRenderTracing()
        {
            ParsekSettingsPersistence.SetStoredMapRenderTracingForTesting(true);
            ParsekSettingsPersistence.ResetForTesting();
            Assert.Null(ParsekSettingsPersistence.GetStoredMapRenderTracing());
        }

        [Fact]
        public void ApplyTo_RestoresStoredMapRenderTracing()
        {
            ParsekSettingsPersistence.SetStoredMapRenderTracingForTesting(true);
            var settings = new ParsekSettings { mapRenderTracing = false };

            ParsekSettingsPersistence.ApplyTo(settings);

            Assert.True(settings.mapRenderTracing);
        }

        [Fact]
        public void IsEnabled_FollowsParsekSettingsCurrent()
        {
            MapRenderTrace.ForceEnabledForTesting = false;
            ParsekSettings.CurrentOverrideForTesting =
                new ParsekSettings { mapRenderTracing = true };

            MapRenderTrace.OpenDetailedWindow("100037", 100.0, 5.0, "settings-on");

            Assert.True(MapRenderTrace.IsDetailedWindowOpen("100037", 102.0));
        }

        // ---- Decision-vs-truth reconciliation (second cut) ----

        [Fact]
        public void ReconcileLineState_Consistent_ReturnsEmpty()
        {
            var intent = new MapRenderTrace.LineRenderIntent
            { Frame = 1, LineActive = true, DrawIcons = "OBJ", Reason = "visible-body-frame" };
            Assert.Equal(string.Empty,
                MapRenderTrace.ReconcileLineState(intent, "True", "OBJ"));
        }

        [Fact]
        public void ReconcileLineState_LineToggledAfterDecision_ReportsMismatch()
        {
            var intent = new MapRenderTrace.LineRenderIntent
            { Frame = 1, LineActive = true, DrawIcons = "OBJ", Reason = "visible-body-frame" };
            string mismatch = MapRenderTrace.ReconcileLineState(intent, "False", "OBJ");
            Assert.Contains("line-toggled-after-decision", mismatch);
            Assert.Contains("intended=true", mismatch);
            Assert.Contains("actual=false", mismatch);
        }

        [Fact]
        public void ReconcileLineState_UnknownActualLine_SkipsLineCheck()
        {
            // Until the OrbitLine reflection is fixed, line.active reads "(field-missing)" -> no
            // signal, so the line check no-ops; the matching drawIcons keeps it consistent.
            var intent = new MapRenderTrace.LineRenderIntent
            { Frame = 1, LineActive = true, DrawIcons = "OBJ", Reason = "visible-body-frame" };
            Assert.Equal(string.Empty,
                MapRenderTrace.ReconcileLineState(intent, "(field-missing)", "OBJ"));
        }

        [Fact]
        public void ReconcileLineState_DrawIconsChangedAfterDecision_ReportsMismatch()
        {
            var intent = new MapRenderTrace.LineRenderIntent
            { Frame = 1, LineActive = false, DrawIcons = "NONE", Reason = "polyline-owns-phase" };
            string mismatch = MapRenderTrace.ReconcileLineState(intent, "False", "OBJ");
            Assert.Contains("drawIcons-changed-after-decision", mismatch);
            Assert.Contains("intended=NONE", mismatch);
            Assert.Contains("actual=OBJ", mismatch);
        }

        [Fact]
        public void ReconcileLineState_UnknownActualDrawIcons_SkipsIconCheck()
        {
            var intent = new MapRenderTrace.LineRenderIntent
            { Frame = 1, LineActive = false, DrawIcons = "NONE", Reason = "polyline-owns-phase" };
            Assert.Equal(string.Empty,
                MapRenderTrace.ReconcileLineState(intent, "False", "(no-renderer)"));
        }

        [Fact]
        public void RecordLineIntent_Disabled_DoesNotStore()
        {
            MapRenderTrace.ForceEnabledForTesting = false;
            MapRenderTrace.RecordLineIntent(7u, true, "OBJ", "visible-body-frame");
            Assert.False(MapRenderTrace.TryGetFreshLineIntent("7", 42, out _));
        }

        [Fact]
        public void RecordLineIntent_Enabled_StoresFreshSameFrameIntent()
        {
            MapRenderTrace.ForceEnabledForTesting = true;
            MapRenderTrace.FrameCounterOverrideForTesting = () => 100;

            MapRenderTrace.RecordLineIntent(7u, true, "OBJ", "visible-body-frame");

            Assert.True(MapRenderTrace.TryGetFreshLineIntent("7", 100, out var intent));
            Assert.True(intent.LineActive);
            Assert.Equal("OBJ", intent.DrawIcons);
            Assert.Equal("visible-body-frame", intent.Reason);
            Assert.Equal(100, intent.Frame);
        }

        [Fact]
        public void TryGetFreshLineIntent_StaleFrame_ReturnsFalse()
        {
            MapRenderTrace.ForceEnabledForTesting = true;
            MapRenderTrace.FrameCounterOverrideForTesting = () => 100;
            MapRenderTrace.RecordLineIntent(7u, true, "OBJ", "visible-body-frame");

            // A later frame exceeds IntentFreshnessFrames (0) -> dropped, not reconciled.
            Assert.False(MapRenderTrace.TryGetFreshLineIntent("7", 102, out _));
        }

        [Fact]
        public void TryGetFreshLineIntent_OneFrameLater_ReturnsFalse()
        {
            // freshness=0: intent recorded at frame 100 is reconciled only on frame 100. The next
            // frame is stale - a grace-defer branch may have legitimately changed the rendered state
            // without re-recording intent - so it is dropped rather than producing a false mismatch.
            MapRenderTrace.ForceEnabledForTesting = true;
            MapRenderTrace.FrameCounterOverrideForTesting = () => 100;
            MapRenderTrace.RecordLineIntent(7u, false, "NONE", "polyline-owns-phase");

            Assert.False(MapRenderTrace.TryGetFreshLineIntent("7", 101, out _));
        }

        // ---- warp safeguard: per-cycle marker-decision keys do not grow the dict unbounded ----

        [Fact]
        public void EmitMarkerDecisionOnChange_PerCycleKeysAtWarp_DictStaysBounded()
        {
            // GAP-1's per-instance overlap decision key is recordingId#cycle; at high warp the cycle
            // index advances without bound WITHIN a scene (Reset only fires on scene switch), minting a
            // fresh key every cycle. The MaxTrackedMarkerDecisionKeys cap must keep the change-detection
            // dict bounded so a long tracing session at warp does not leak. Push far past the cap.
            MapRenderTrace.ForceEnabledForTesting = true;

            for (int i = 0; i < 12000; i++)
                MapRenderTrace.EmitMarkerDecisionOnChange(
                    MapRenderTrace.RenderSurface.AtmosphericMarker,
                    "recA#" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    1000.0 + i,
                    "outcome=DrawnNonProto ride=rode-leg0 posSource=polyline");

            // Bounded by the cap (<= 4096), never approaching the unbounded 12000.
            Assert.True(MapRenderTrace.MarkerDecisionSignatureCountForTesting <= 4096,
                "marker-decision dict grew to "
                    + MapRenderTrace.MarkerDecisionSignatureCountForTesting + " (cap 4096)");
        }

        // ---- polyline-orbit-overlap anomaly ----

        [Fact]
        public void ReconcilePolylineOverlap_NotOwning_ReturnsEmpty()
        {
            Assert.Equal(string.Empty,
                MapRenderTrace.ReconcilePolylineOverlap(false, "True", "OBJ"));
        }

        [Fact]
        public void ReconcilePolylineOverlap_OwningWithIconShown_Flags()
        {
            string overlap = MapRenderTrace.ReconcilePolylineOverlap(true, "False", "OBJ");
            Assert.Contains("proto-icon-shown-while-polyline-owns", overlap);
            Assert.Contains("drawIcons=OBJ", overlap);
        }

        [Fact]
        public void ReconcilePolylineOverlap_OwningWithIconNone_NoOverlap()
        {
            Assert.Equal(string.Empty,
                MapRenderTrace.ReconcilePolylineOverlap(true, "False", "NONE"));
        }

        [Fact]
        public void ReconcilePolylineOverlap_OwningWithLineActive_Flags()
        {
            string overlap = MapRenderTrace.ReconcilePolylineOverlap(true, "True", "NONE");
            Assert.Contains("orbit-line-active-while-polyline-owns", overlap);
        }

        [Fact]
        public void ReconcilePolylineOverlap_OwningWithUnknownLine_SkipsLineFacet()
        {
            // line facet dormant until the OrbitLine reflection is fixed; icon NONE => no overlap.
            Assert.Equal(string.Empty,
                MapRenderTrace.ReconcilePolylineOverlap(true, "(field-missing)", "NONE"));
        }

        // ---- IMGUI marker-surface emit ----

        [Fact]
        public void EmitMarker_Enabled_EmitsMarkerDrawWithSurface()
        {
            MapRenderTrace.ForceEnabledForTesting = true;

            MapRenderTrace.EmitMarker(
                MapRenderTrace.RenderSurface.ImguiLabeledMarker, "rec-marker-enabled", 100.0,
                "vessel=Munar_Probe markerPos=(1.00,2.00,3.00)");

            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE]")
                && l.Contains("[MapRenderTrace]")
                && l.Contains("phase=MarkerDraw")
                && l.Contains("surface=ImguiLabeledMarker")
                && l.Contains("pid=rec-marker-enabled"));
        }

        [Fact]
        public void EmitMarker_Disabled_NoOp()
        {
            MapRenderTrace.ForceEnabledForTesting = false;
            MapRenderTrace.EmitMarker(
                MapRenderTrace.RenderSurface.AtmosphericMarker, "rec-marker-disabled", 100.0, "vessel=X");
            Assert.Empty(logLines);
        }

        // ---- GAP-2: first-class Polyline-surface trace at the shared Driver's per-leg draw site ----

        [Fact]
        public void EmitMarker_Polyline_EmitsMarkerDrawWithPolylineSurface()
        {
            // GAP-2: the polyline leg draw now emits a first-class MapRenderTrace line so the
            // surface=Polyline slot is greppable in BOTH scenes (the insertion lives in the shared
            // Driver). Phase is MarkerDraw (EmitMarker's phase).
            MapRenderTrace.ForceEnabledForTesting = true;

            MapRenderTrace.EmitMarker(
                MapRenderTrace.RenderSurface.Polyline, "rec-polyline", 500.0,
                "scene=TRACKSTATION leg=2 body=Kerbin pts=40 owned=False");

            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE]")
                && l.Contains("[MapRenderTrace]")
                && l.Contains("phase=MarkerDraw")
                && l.Contains("surface=Polyline")
                && l.Contains("pid=rec-polyline")
                && l.Contains("body=Kerbin"));
        }

        [Fact]
        public void EmitMarker_Polyline_SameKeyWithinInterval_CollapsesToOneLine()
        {
            // GAP-2 warp-stability: the rate-limit KEY is (Polyline, recId) only - warp-stable per the
            // #1063 rule. Two draws of the SAME recording inside the interval (the per-leg index / UT
            // advance every frame and live in the BODY, never the key) collapse to a single emitted
            // line. Clock held fixed via ClockOverrideForTesting so both calls fall in one window.
            MapRenderTrace.ForceEnabledForTesting = true;

            MapRenderTrace.EmitMarker(
                MapRenderTrace.RenderSurface.Polyline, "rec-warp", 1000.0,
                "scene=FLIGHT leg=0 body=Kerbin pts=12 owned=True");
            // Same (Polyline, recId) key, DIFFERENT leg + UT in the body, clock unchanged.
            MapRenderTrace.EmitMarker(
                MapRenderTrace.RenderSurface.Polyline, "rec-warp", 1001.0,
                "scene=FLIGHT leg=5 body=Kerbin pts=18 owned=True");

            int polylineLines = logLines.Count(l =>
                l.Contains("[MapRenderTrace]")
                && l.Contains("surface=Polyline")
                && l.Contains("pid=rec-warp"));
            Assert.Equal(1, polylineLines);
        }

        [Fact]
        public void EmitMarker_Polyline_DifferentRecordings_TrackedIndependently()
        {
            // Two distinct recordingIds => two distinct (Polyline, recId) keys => one emit each
            // (recording A does not throttle recording B inside the same window).
            MapRenderTrace.ForceEnabledForTesting = true;

            MapRenderTrace.EmitMarker(
                MapRenderTrace.RenderSurface.Polyline, "rec-A", 1000.0, "scene=FLIGHT leg=0");
            MapRenderTrace.EmitMarker(
                MapRenderTrace.RenderSurface.Polyline, "rec-B", 1000.0, "scene=FLIGHT leg=0");

            Assert.Contains(logLines, l => l.Contains("surface=Polyline") && l.Contains("pid=rec-A"));
            Assert.Contains(logLines, l => l.Contains("surface=Polyline") && l.Contains("pid=rec-B"));
        }

        [Fact]
        public void EmitMarker_Polyline_AfterIntervalElapses_EmitsAgain()
        {
            // Advancing the wall clock past the 2 s interval re-opens the gate for the same key.
            MapRenderTrace.ForceEnabledForTesting = true;

            MapRenderTrace.EmitMarker(
                MapRenderTrace.RenderSurface.Polyline, "rec-interval", 1000.0, "scene=FLIGHT leg=0");
            clock += 2.5; // past the default 2 s interval
            MapRenderTrace.EmitMarker(
                MapRenderTrace.RenderSurface.Polyline, "rec-interval", 1001.0, "scene=FLIGHT leg=3");

            int polylineLines = logLines.Count(l =>
                l.Contains("surface=Polyline") && l.Contains("pid=rec-interval"));
            Assert.Equal(2, polylineLines);
        }

        // ---- Marker-decision observability (per-pid WHY a marker drew / was skipped) ----

        [Fact]
        public void MarkerOutcomeToken_MapsEveryBranch()
        {
            Assert.Equal("drawn-non-proto",
                MapRenderTrace.MarkerOutcomeToken(MapRenderTrace.MarkerOutcome.DrawnNonProto));
            Assert.Equal("drawn-proto-icon",
                MapRenderTrace.MarkerOutcomeToken(MapRenderTrace.MarkerOutcome.DrawnProtoIcon));
            Assert.Equal("skipped-debris",
                MapRenderTrace.MarkerOutcomeToken(MapRenderTrace.MarkerOutcome.SkippedDebris));
            Assert.Equal("skipped-chain-non-tip",
                MapRenderTrace.MarkerOutcomeToken(MapRenderTrace.MarkerOutcome.SkippedChainNonTip));
            Assert.Equal("skipped-not-on-map",
                MapRenderTrace.MarkerOutcomeToken(MapRenderTrace.MarkerOutcome.SkippedNotOnMap));
            Assert.Equal("skipped-decision-false",
                MapRenderTrace.MarkerOutcomeToken(MapRenderTrace.MarkerOutcome.SkippedDecisionFalse));
            Assert.Equal("skipped-position-fail",
                MapRenderTrace.MarkerOutcomeToken(MapRenderTrace.MarkerOutcome.SkippedPositionFail));
            Assert.Equal("skipped-loop-hidden",
                MapRenderTrace.MarkerOutcomeToken(MapRenderTrace.MarkerOutcome.SkippedLoopHidden));
            Assert.Equal("unknown",
                MapRenderTrace.MarkerOutcomeToken(MapRenderTrace.MarkerOutcome.Unknown));
        }

        [Fact]
        public void MarkerRideReasonToken_RodeLeg_CarriesIndex()
        {
            Assert.Equal("rode-leg3",
                MapRenderTrace.MarkerRideReasonToken(MapRenderTrace.MarkerRideReason.RodeLeg, 3));
            Assert.Equal("fallback-leg-not-drawn-this-frame",
                MapRenderTrace.MarkerRideReasonToken(
                    MapRenderTrace.MarkerRideReason.FallbackLegNotDrawnThisFrame, -1));
            Assert.Equal("fallback-head-outside-legs",
                MapRenderTrace.MarkerRideReasonToken(
                    MapRenderTrace.MarkerRideReason.FallbackHeadOutsideLegs, -1));
            Assert.Equal("fallback-missing-recordedUTs",
                MapRenderTrace.MarkerRideReasonToken(
                    MapRenderTrace.MarkerRideReason.FallbackMissingRecordedUTs, -1));
            Assert.Equal("fallback-no-cache",
                MapRenderTrace.MarkerRideReasonToken(
                    MapRenderTrace.MarkerRideReason.FallbackNoCache, -1));
            Assert.Equal("not-attempted",
                MapRenderTrace.MarkerRideReasonToken(
                    MapRenderTrace.MarkerRideReason.NotAttempted, -1));
        }

        [Fact]
        public void BuildMarkerDecisionSignature_DrawnNonProto_CarriesDisjunctsRideAndSource()
        {
            string sig = MapRenderTrace.BuildMarkerDecisionSignature(
                recordingIndex: 4,
                vesselName: "Munar Probe",
                directorTracedPathActive: true,
                polylineOwning: false,
                iconSuppressed: false,
                shouldDrawNonProto: true,
                outcome: MapRenderTrace.MarkerOutcome.DrawnNonProto,
                rideReason: MapRenderTrace.MarkerRideReason.RodeLeg,
                legIndex: 2,
                posSource: "polyline");

            Assert.Contains("rec=4", sig);
            Assert.Contains("vessel=Munar_Probe", sig);
            Assert.Contains("directorTracedPathActive=true", sig);
            Assert.Contains("polylineOwning=false", sig);
            Assert.Contains("iconSuppressed=false", sig);
            Assert.Contains("shouldDrawNonProto=true", sig);
            Assert.Contains("outcome=drawn-non-proto", sig);
            Assert.Contains("ride=rode-leg2", sig);
            Assert.Contains("posSource=polyline", sig);
        }

        [Fact]
        public void BuildMarkerDecisionSignature_SkipOutcome_OmitsRideAndSource()
        {
            // A non-draw outcome carries no ride / posSource tokens (they are only meaningful
            // for a drawn non-proto marker), so the signature stays compact and stable.
            string sig = MapRenderTrace.BuildMarkerDecisionSignature(
                recordingIndex: 1,
                vesselName: "Probe",
                directorTracedPathActive: false,
                polylineOwning: false,
                iconSuppressed: false,
                shouldDrawNonProto: false,
                outcome: MapRenderTrace.MarkerOutcome.SkippedDebris,
                rideReason: MapRenderTrace.MarkerRideReason.NotAttempted,
                legIndex: -1,
                posSource: "?");

            Assert.Contains("outcome=skipped-debris", sig);
            Assert.DoesNotContain("ride=", sig);
            Assert.DoesNotContain("posSource=", sig);
        }

        [Fact]
        public void EmitMarkerDecisionOnChange_NoOpWhenDisabled()
        {
            MapRenderTrace.ForceEnabledForTesting = false;

            MapRenderTrace.EmitMarkerDecisionOnChange(
                MapRenderTrace.RenderSurface.ImguiLabeledMarker, "rec-1", 100.0, "outcome=drawn-non-proto");

            Assert.Empty(logLines);
        }

        [Fact]
        public void EmitMarkerDecisionOnChange_FirstSignatureEmits()
        {
            MapRenderTrace.ForceEnabledForTesting = true;

            MapRenderTrace.EmitMarkerDecisionOnChange(
                MapRenderTrace.RenderSurface.ImguiLabeledMarker, "rec-1", 100.0,
                "rec=1 outcome=drawn-non-proto");

            Assert.Contains(logLines, l =>
                l.Contains("[MapRenderTrace]")
                && l.Contains("phase=MarkerDecision")
                && l.Contains("surface=ImguiLabeledMarker")
                && l.Contains("pid=rec-1")
                && l.Contains("outcome=drawn-non-proto"));
        }

        [Fact]
        public void EmitMarkerDecisionOnChange_RepeatSignatureSuppressed()
        {
            MapRenderTrace.ForceEnabledForTesting = true;

            MapRenderTrace.EmitMarkerDecisionOnChange(
                MapRenderTrace.RenderSurface.ImguiLabeledMarker, "rec-1", 100.0, "outcome=drawn-non-proto");
            int afterFirst = logLines.Count;
            // Same signature, later UT -> suppressed (no new line).
            MapRenderTrace.EmitMarkerDecisionOnChange(
                MapRenderTrace.RenderSurface.ImguiLabeledMarker, "rec-1", 101.0, "outcome=drawn-non-proto");

            Assert.Equal(afterFirst, logLines.Count);
        }

        [Fact]
        public void EmitMarkerDecisionOnChange_ChangedSignatureReEmits()
        {
            MapRenderTrace.ForceEnabledForTesting = true;

            MapRenderTrace.EmitMarkerDecisionOnChange(
                MapRenderTrace.RenderSurface.ImguiLabeledMarker, "rec-1", 100.0, "outcome=drawn-non-proto");
            int afterFirst = logLines.Count;
            // A transition (different outcome) re-emits, capturing the sub-second change.
            MapRenderTrace.EmitMarkerDecisionOnChange(
                MapRenderTrace.RenderSurface.ImguiLabeledMarker, "rec-1", 100.5, "outcome=skipped-position-fail");

            Assert.True(logLines.Count > afterFirst);
            Assert.Contains(logLines, l => l.Contains("outcome=skipped-position-fail"));
        }

        [Fact]
        public void EmitMarkerDecisionOnChange_PerPidIndependent()
        {
            MapRenderTrace.ForceEnabledForTesting = true;

            // Two different ghosts with the same signature each emit once (change detection is per-pid).
            MapRenderTrace.EmitMarkerDecisionOnChange(
                MapRenderTrace.RenderSurface.ImguiLabeledMarker, "rec-1", 100.0, "outcome=drawn-non-proto");
            MapRenderTrace.EmitMarkerDecisionOnChange(
                MapRenderTrace.RenderSurface.ImguiLabeledMarker, "rec-2", 100.0, "outcome=drawn-non-proto");

            Assert.Contains(logLines, l => l.Contains("pid=rec-1"));
            Assert.Contains(logLines, l => l.Contains("pid=rec-2"));
        }

        [Fact]
        public void EmitMarkerDecisionOnChange_ReEmitsAfterReset()
        {
            MapRenderTrace.ForceEnabledForTesting = true;

            MapRenderTrace.EmitMarkerDecisionOnChange(
                MapRenderTrace.RenderSurface.ImguiLabeledMarker, "rec-1", 100.0, "outcome=drawn-non-proto");
            // Reset() (driven by the scene-switch hook) clears the per-pid signature dict, so the
            // SAME signature emits again on re-entry rather than being silently suppressed.
            MapRenderTrace.Reset();
            int afterReset = logLines.Count;
            MapRenderTrace.EmitMarkerDecisionOnChange(
                MapRenderTrace.RenderSurface.ImguiLabeledMarker, "rec-1", 200.0, "outcome=drawn-non-proto");

            Assert.True(logLines.Count > afterReset);
        }

        // ---- EmitLineVisibilityOnChange (orbit-line / icon visibility EVENT) ----

        [Fact]
        public void EmitLineVisibilityOnChange_NoOpWhenDisabled()
        {
            MapRenderTrace.ForceEnabledForTesting = false;

            MapRenderTrace.EmitLineVisibilityOnChange(
                "100037", "ab12cd34", 100.0, "active=1|reason=visible-body-frame", "reason=visible-body-frame");

            Assert.Empty(logLines);
        }

        [Fact]
        public void EmitLineVisibilityOnChange_FirstSignatureEmits_CarriesSurfaceReasonAndRecId()
        {
            MapRenderTrace.ForceEnabledForTesting = true;

            MapRenderTrace.EmitLineVisibilityOnChange(
                "100037", "ab12cd34", 100.0,
                "active=0|icons=NONE|reason=polyline-owns-phase",
                "Orbit line decision: pid=100037 reason=polyline-owns-phase lineActive=False");

            Assert.Contains(logLines, l =>
                l.Contains("[MapRenderTrace]")
                && l.Contains("phase=LineVisibilityChange")
                && l.Contains("surface=ProtoOrbitLine")
                && l.Contains("pid=100037")
                && l.Contains("recId=ab12cd34")
                && l.Contains("reason=polyline-owns-phase"));
        }

        [Fact]
        public void EmitLineVisibilityOnChange_RepeatSignatureSuppressed()
        {
            MapRenderTrace.ForceEnabledForTesting = true;

            MapRenderTrace.EmitLineVisibilityOnChange(
                "100037", "ab12cd34", 100.0, "active=1|reason=visible-body-frame", "reason=visible-body-frame");
            int afterFirst = logLines.Count;
            MapRenderTrace.EmitLineVisibilityOnChange(
                "100037", "ab12cd34", 101.0, "active=1|reason=visible-body-frame", "reason=visible-body-frame");

            Assert.Equal(afterFirst, logLines.Count);
        }

        [Fact]
        public void EmitLineVisibilityOnChange_ChangedSignatureReEmits()
        {
            MapRenderTrace.ForceEnabledForTesting = true;

            MapRenderTrace.EmitLineVisibilityOnChange(
                "100037", "ab12cd34", 100.0, "active=1|reason=visible-body-frame", "reason=visible-body-frame");
            int afterFirst = logLines.Count;
            // A real visibility transition (line hidden by the polyline-owns branch) re-emits.
            MapRenderTrace.EmitLineVisibilityOnChange(
                "100037", "ab12cd34", 100.5,
                "active=0|reason=polyline-owns-phase", "reason=polyline-owns-phase");

            Assert.True(logLines.Count > afterFirst);
            Assert.Contains(logLines, l => l.Contains("reason=polyline-owns-phase"));
        }

        [Fact]
        public void EmitLineVisibilityOnChange_ReEmitsAfterReset()
        {
            MapRenderTrace.ForceEnabledForTesting = true;

            MapRenderTrace.EmitLineVisibilityOnChange(
                "100037", "ab12cd34", 100.0, "active=1|reason=visible-body-frame", "reason=visible-body-frame");
            MapRenderTrace.Reset();
            int afterReset = logLines.Count;
            MapRenderTrace.EmitLineVisibilityOnChange(
                "100037", "ab12cd34", 200.0, "active=1|reason=visible-body-frame", "reason=visible-body-frame");

            Assert.True(logLines.Count > afterReset);
        }

        [Fact]
        public void EmitLineVisibilityOnChange_PerCycleKeysAtWarp_DictStaysBounded()
        {
            MapRenderTrace.ForceEnabledForTesting = true;

            // Mint far more than the 4096 cap of distinct pid keys (the warp-safety bound). The dict must
            // not grow without limit even though each key is fresh.
            for (int i = 0; i < 5000; i++)
                MapRenderTrace.EmitLineVisibilityOnChange(
                    "pid-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "rec-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    100.0 + i, "active=1|seq=" + i, "reason=visible-body-frame");

            Assert.True(MapRenderTrace.LineVisibilitySignatureCountForTesting <= 4096,
                "line-visibility signature dict unbounded: "
                    + MapRenderTrace.LineVisibilitySignatureCountForTesting + " (cap 4096)");
        }
    }
}
