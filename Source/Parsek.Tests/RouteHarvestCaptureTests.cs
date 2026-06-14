using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// M2 Phase 3 (plan D4/D5): harvest-window lifecycle. Pins the
    /// threshold-crossing open/close decision, the open-at-start /
    /// close-at-stop flags, the rails-entry re-baseline rule, the false-alarm
    /// reopen unwind (#287 precedent), and the positive-delta harvested
    /// manifest with the D2 direction-sensitive name filter.
    /// </summary>
    [Collection("Sequential")]
    public class RouteHarvestCaptureTests : System.IDisposable
    {
        public RouteHarvestCaptureTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            ResourceTransferability.ResetForTesting();
        }

        public void Dispose()
        {
            ResourceTransferability.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Dictionary<string, ResourceAmount> Res(
            params (string name, double amount)[] entries)
        {
            var dict = new Dictionary<string, ResourceAmount>();
            foreach (var (name, amount) in entries)
                dict[name] = new ResourceAmount { amount = amount, maxAmount = 10000.0 };
            return dict;
        }

        private static RouteHarvestWindow OpenTestWindow(
            double ut = 1000.0,
            Dictionary<string, ResourceAmount> startManifest = null,
            bool atRecordingStart = false)
        {
            return RouteHarvestCapture.OpenWindow(
                ut,
                startManifest ?? Res(("Ore", 0.0)),
                "Minmus", -0.55, 78.25, 2412.5,
                (int)Vessel.Situations.LANDED,
                new List<string> { "100:ModuleResourceHarvester:Drill-O-Matic" },
                atRecordingStart);
        }

        // ---------- Threshold-crossing decision ----------

        [Theory]
        [InlineData(true, false, (int)HarvestActivityTransition.Open)]
        [InlineData(false, true, (int)HarvestActivityTransition.Close)]
        [InlineData(true, true, (int)HarvestActivityTransition.None)]
        [InlineData(false, false, (int)HarvestActivityTransition.None)]
        public void EvaluateTransition_Matrix(bool anyActive, bool windowOpen, int expected)
        {
            Assert.Equal((HarvestActivityTransition)expected,
                RouteHarvestCapture.EvaluateTransition(anyActive, windowOpen));
        }

        // catches: a staged-away / destroyed drill never closing its window.
        // The recorder counts Unity-null cache entries as INACTIVE, so the
        // poll sees anyActive=false against an open window -> Close.
        [Fact]
        public void DestroyedConverter_InactiveScan_ClosesWindow()
        {
            Assert.Equal(HarvestActivityTransition.Close,
                RouteHarvestCapture.EvaluateTransition(anyConverterActive: false, windowOpen: true));
        }

        // ---------- Rails-entry re-baseline (plan D4 warp rule) ----------

        // catches: warp-period production going unwitnessed when activation
        // raced the poll - converters active with no window open at rails
        // entry must open one AT the boundary.
        [Fact]
        public void RailsEntry_ConvertersActiveNoWindow_Opens()
        {
            Assert.True(RouteHarvestCapture.ShouldOpenWindowAtRailsEntry(
                anyConverterActive: true, windowOpen: false));
        }

        // catches: a rails entry disturbing an already-open window (production
        // continues inside it - correct, no action).
        [Fact]
        public void RailsEntry_WindowAlreadyOpen_NoAction()
        {
            Assert.False(RouteHarvestCapture.ShouldOpenWindowAtRailsEntry(
                anyConverterActive: true, windowOpen: true));
        }

        [Fact]
        public void RailsEntry_NoConvertersActive_NoAction()
        {
            Assert.False(RouteHarvestCapture.ShouldOpenWindowAtRailsEntry(
                anyConverterActive: false, windowOpen: false));
        }

        // ---------- Open / close mechanics ----------

        [Fact]
        public void OpenWindow_PopulatesSpanLocationAndFlags()
        {
            RouteHarvestWindow window = OpenTestWindow(ut: 1234.5, atRecordingStart: true);

            Assert.Equal(1234.5, window.StartUT);
            Assert.True(window.IsOpen);
            Assert.True(double.IsNaN(window.EndUT));
            Assert.True(window.OpenedAtRecordingStart);
            Assert.False(window.ClosedAtRecordingStop);
            Assert.Equal(0.0, window.StartTransportResources["Ore"].amount);
            Assert.Null(window.EndTransportResources);
            Assert.Equal("Minmus", window.BodyName);
            Assert.Equal((int)Vessel.Situations.LANDED, window.SituationAtOpen);
            Assert.Single(window.ActiveConverters);
            Assert.StartsWith("harvest-", window.WindowId);
        }

        [Fact]
        public void CloseWindow_SetsEndSpanManifestAndStopFlag()
        {
            RouteHarvestWindow window = OpenTestWindow();

            RouteHarvestCapture.CloseWindow(window, 2000.0, Res(("Ore", 120.0)), atRecordingStop: true);

            Assert.False(window.IsOpen);
            Assert.Equal(2000.0, window.EndUT);
            Assert.True(window.ClosedAtRecordingStop);
            Assert.Equal(120.0, window.EndTransportResources["Ore"].amount);
        }

        // ---------- D4 stop/false-alarm rule (the #287 unwind precedent) ----------

        // catches: a chain-boundary stop abandoned by ResumeAfterFalseAlarm
        // leaving the window closed - post-resume drilling would land outside
        // any window and the gain check would reject it as unaccounted.
        [Fact]
        public void FalseAlarmResume_ReopensWindowClosedByAbandonedStop()
        {
            RouteHarvestWindow window = OpenTestWindow(ut: 1000.0);
            RouteHarvestCapture.CloseWindow(window, 1500.0, Res(("Ore", 60.0)), atRecordingStop: true);
            Assert.False(window.IsOpen);

            RouteHarvestCapture.ReopenWindow(window);

            Assert.True(window.IsOpen);
            Assert.True(double.IsNaN(window.EndUT));
            Assert.False(window.ClosedAtRecordingStop);
            Assert.Null(window.EndTransportResources);
            // The original open-time baseline survives the unwind: the window
            // keeps accounting from its true start.
            Assert.Equal(1000.0, window.StartUT);
            Assert.Equal(0.0, window.StartTransportResources["Ore"].amount);

            // The eventual REAL stop closes it again cleanly.
            RouteHarvestCapture.CloseWindow(window, 2500.0, Res(("Ore", 200.0)), atRecordingStop: true);
            Assert.Equal(2500.0, window.EndUT);
            Assert.Equal(200.0, window.EndTransportResources["Ore"].amount);
        }

        // ---------- Harvested manifest (positive deltas, D2 names) ----------

        [Fact]
        public void ComputeWindowHarvestedManifest_PositiveDeltasOnly()
        {
            ResourceTransferability.DefinitionLookupOverrideForTesting = _ => true;
            RouteHarvestWindow window = OpenTestWindow(
                startManifest: Res(("Ore", 10.0), ("LiquidFuel", 500.0)));
            RouteHarvestCapture.CloseWindow(window, 2000.0,
                Res(("Ore", 130.0), ("LiquidFuel", 450.0)), atRecordingStop: false);

            Dictionary<string, double> harvested =
                RouteHarvestCapture.ComputeWindowHarvestedManifest(window);

            Assert.Single(harvested);
            Assert.Equal(120.0, harvested["Ore"]);
            // The LiquidFuel LOSS (drill power generation etc.) is not a
            // harvest - losses are outside M2's checked surface.
            Assert.False(harvested.ContainsKey("LiquidFuel"));
        }

        // catches: an activated-but-stalled drill (no ground contact, no
        // asteroid) producing a phantom gain - zero delta nets an empty
        // harvested manifest, harmlessly.
        [Fact]
        public void ComputeWindowHarvestedManifest_StalledDrill_NetsZero()
        {
            ResourceTransferability.DefinitionLookupOverrideForTesting = _ => true;
            RouteHarvestWindow window = OpenTestWindow(startManifest: Res(("Ore", 10.0)));
            RouteHarvestCapture.CloseWindow(window, 2000.0, Res(("Ore", 10.0)), atRecordingStop: false);

            Assert.Empty(RouteHarvestCapture.ComputeWindowHarvestedManifest(window));
        }

        // catches (D2 direction pin): always-ignored or undefined names
        // entering the ADMISSION-direction harvested manifest. An
        // undefined-name positive gain stays visible to the Phase 4 gain
        // check (which reads the raw window manifests) and counts as
        // UNACCOUNTED there - it must never be admitted as harvested.
        [Fact]
        public void ComputeWindowHarvestedManifest_ExcludesECAndUndefined()
        {
            ResourceTransferability.DefinitionLookupOverrideForTesting =
                name => name != CrpFixtures.UninstalledModResource;
            RouteHarvestWindow window = OpenTestWindow(startManifest: Res(
                ("Ore", 0.0),
                ("ElectricCharge", 10.0),
                (CrpFixtures.UninstalledModResource, 0.0)));
            RouteHarvestCapture.CloseWindow(window, 2000.0, Res(
                ("Ore", 50.0),
                ("ElectricCharge", 100.0),
                (CrpFixtures.UninstalledModResource, 25.0)), atRecordingStop: false);

            Dictionary<string, double> harvested =
                RouteHarvestCapture.ComputeWindowHarvestedManifest(window);

            Assert.Single(harvested);
            Assert.Equal(50.0, harvested["Ore"]);
        }

        [Fact]
        public void ComputeWindowHarvestedManifest_OpenWindow_Empty()
        {
            RouteHarvestWindow window = OpenTestWindow();

            Assert.Empty(RouteHarvestCapture.ComputeWindowHarvestedManifest(window));
        }

        // catches: a resource that first appears in the END manifest (empty
        // tank pruned from start, or tank added by EVA construction) being
        // missed - absent start counts as 0.
        [Fact]
        public void ComputeWindowHarvestedManifest_ResourceAbsentAtStart_FullEndAmountCounts()
        {
            ResourceTransferability.DefinitionLookupOverrideForTesting = _ => true;
            RouteHarvestWindow window = OpenTestWindow(startManifest: null);
            window.StartTransportResources = null;
            RouteHarvestCapture.CloseWindow(window, 2000.0, Res(("Ore", 42.0)), atRecordingStop: false);

            Assert.Equal(42.0, RouteHarvestCapture.ComputeWindowHarvestedManifest(window)["Ore"]);
        }

        // ---------- D14 forwarding + clone sites ----------

        // catches (D14): the tree-mode stop flush dropping the leg's windows.
        // The capture carries the full recorder-side list; the flush adopts it
        // wholesale per active stop.
        [Fact]
        public void TreeModeStop_ForwardsHarvestWindows()
        {
            var target = new Recording { RecordingId = "tree-rec" };
            var capture = new Recording { RecordingId = "capture" };
            RouteHarvestWindow window = OpenTestWindow();
            RouteHarvestCapture.CloseWindow(window, 2000.0, Res(("Ore", 120.0)), atRecordingStop: true);
            capture.RouteHarvestWindows = new List<RouteHarvestWindow> { window };

            bool changed = ParsekFlight.ApplyCapturedLogisticsMetadataToRecording(
                target, capture, "test");

            Assert.True(changed);
            Assert.NotNull(target.RouteHarvestWindows);
            Assert.Single(target.RouteHarvestWindows);
            Assert.NotSame(window, target.RouteHarvestWindows[0]);
            Assert.Equal(120.0, target.RouteHarvestWindows[0].EndTransportResources["Ore"].amount);
        }

        [Fact]
        public void RecordingCloneSites_CarryHarvestWindows()
        {
            var source = new Recording
            {
                RecordingId = "src",
                RouteHarvestWindows = new List<RouteHarvestWindow> { OpenTestWindow() }
            };

            Recording deepClone = Recording.DeepClone(source);
            Assert.NotNull(deepClone.RouteHarvestWindows);
            Assert.Single(deepClone.RouteHarvestWindows);
            Assert.NotSame(source.RouteHarvestWindows[0], deepClone.RouteHarvestWindows[0]);

            var artifactTarget = new Recording { RecordingId = "dst" };
            artifactTarget.ApplyPersistenceArtifactsFrom(source);
            Assert.NotNull(artifactTarget.RouteHarvestWindows);
            Assert.Single(artifactTarget.RouteHarvestWindows);

            // Null stays null (codec/hasher null-preservation contract).
            Recording nullClone = Recording.DeepClone(new Recording { RecordingId = "empty" });
            Assert.Null(nullClone.RouteHarvestWindows);
        }

        // ---------- Clone independence ----------

        [Fact]
        public void DeepClone_IsIndependent()
        {
            RouteHarvestWindow window = OpenTestWindow();
            RouteHarvestCapture.CloseWindow(window, 2000.0, Res(("Ore", 120.0)), atRecordingStop: true);

            RouteHarvestWindow clone = window.DeepClone();
            window.EndTransportResources["Ore"] = new ResourceAmount { amount = 1.0, maxAmount = 1.0 };
            window.ActiveConverters.Add("mutated");

            Assert.Equal(120.0, clone.EndTransportResources["Ore"].amount);
            Assert.Single(clone.ActiveConverters);
            Assert.Equal(window.WindowId, clone.WindowId);
            Assert.True(clone.ClosedAtRecordingStop);
        }
    }
}
