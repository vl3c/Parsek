using System.Collections.Generic;
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

        public MapRenderTraceTests()
        {
            MapRenderTrace.Reset();
            MapRenderTrace.ForceEnabledForTesting = false;
            MapRenderTrace.FrameCounterOverrideForTesting = () => 42;
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
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

            // 2 frames later exceeds IntentFreshnessFrames (1) -> dropped, not reconciled.
            Assert.False(MapRenderTrace.TryGetFreshLineIntent("7", 102, out _));
        }

        [Fact]
        public void TryGetFreshLineIntent_OneFrameSlack_ReturnsTrue()
        {
            MapRenderTrace.ForceEnabledForTesting = true;
            MapRenderTrace.FrameCounterOverrideForTesting = () => 100;
            MapRenderTrace.RecordLineIntent(7u, false, "NONE", "polyline-owns-phase");

            Assert.True(MapRenderTrace.TryGetFreshLineIntent("7", 101, out var intent));
            Assert.Equal("NONE", intent.DrawIcons);
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
    }
}
