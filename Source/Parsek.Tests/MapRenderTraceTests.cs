using System.Collections.Generic;
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
    }
}
