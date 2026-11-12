using System.Collections.Generic;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization cells for the private multi-window analysis behind
    /// <see cref="RouteAnalysisEngine.AnalyzeTree"/> and
    /// <see cref="RouteAnalysisEngine.AnalyzeRecording"/>: gate precedence,
    /// which window a reject names, every result field a branch sets, and the
    /// text, level and rate-limit key of every line a refusal writes. Each cell
    /// pins today's behaviour so a restructuring of the method cannot move it.
    /// </summary>
    public partial class RouteAnalysisEngineTests
    {
        private const string AwKscId = "aw-ksc";

        private static Recording AwKscRecording(params RouteConnectionWindow[] windows)
        {
            return new Recording
            {
                RecordingId = AwKscId,
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                RouteConnectionWindows = new List<RouteConnectionWindow>(windows)
            };
        }

        private static Recording AwUndockedRecording(string id, params RouteConnectionWindow[] windows)
        {
            return new Recording
            {
                RecordingId = id,
                StartBodyName = "Mun",
                RouteConnectionWindows = new List<RouteConnectionWindow>(windows)
            };
        }

        // Zero-transfer grapple window (the structural-grab shape).
        private static RouteConnectionWindow AwEmptyWindow(
            string id, RouteConnectionKind kind, double dockUT)
        {
            return new RouteConnectionWindow
            {
                WindowId = id,
                DockUT = dockUT,
                UndockUT = dockUT + 60.0,
                TransferTargetVesselPid = 9001,
                TransferKind = kind,
                DockTransportResources = Manifest(80.0, 100.0),
                UndockTransportResources = Manifest(80.0, 100.0),
                DockEndpointResources = new Dictionary<string, ResourceAmount>(),
                UndockEndpointResources = new Dictionary<string, ResourceAmount>(),
                EndpointAtDock = Endpoint(),
                TransferEndpointSituation = 4
            };
        }

        // An empty grapple window whose transport GAINED `resource` with no
        // endpoint loss (the drilling-while-grappled shape).
        private static RouteConnectionWindow AwUnmatchedGainGrapple(
            string id, double dockUT, string resource)
        {
            RouteConnectionWindow w = AwEmptyWindow(id, RouteConnectionKind.Grapple, dockUT);
            w.DockTransportResources = new Dictionary<string, ResourceAmount>
            {
                [resource] = new ResourceAmount { amount = 0.0, maxAmount = 1000.0 }
            };
            w.UndockTransportResources = new Dictionary<string, ResourceAmount>
            {
                [resource] = new ResourceAmount { amount = 120.0, maxAmount = 1000.0 }
            };
            return w;
        }

        private static RouteConnectionWindow AwInventoryGainWindow(
            string id, double dockUT, string partName)
        {
            RouteConnectionWindow w = BuildDeliveryWindowAt(id, dockUT, dockUT + 60.0);
            InventoryPayloadItem phantom = Payload("phantom-" + partName, partName, 1, slotsTaken: 1);
            w.DockTransportInventory = null;
            w.UndockTransportInventory = new List<InventoryPayloadItem> { phantom.DeepClone() };
            w.DockEndpointInventory = null;
            w.UndockEndpointInventory = null;
            return w;
        }

        private int CountLines(string fragment)
        {
            int n = 0;
            foreach (string l in logLines)
                if (l.Contains(fragment))
                    n++;
            return n;
        }

        // ---------------------------------------------------------------
        // Producer-kind gate
        // ---------------------------------------------------------------

        // catches: the producer-kind gate moving below the endpoint-proof gate
        // (an earlier proof-less window would win), or the reject losing its
        // source, window or detail.
        [Fact]
        public void AW_UnsupportedKindOnLaterWindow_BeatsEarlierMissingProof()
        {
            RouteConnectionWindow proofless = BuildDeliveryWindowAt("proofless", 100.0, 160.0);
            proofless.EndpointAtDock = null;
            RouteConnectionWindow unknown = BuildDeliveryWindowAt("modded", 200.0, 260.0);
            unknown.TransferKind = RouteConnectionKind.Unknown;
            Recording rec = AwKscRecording(proofless, unknown);

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.Equal(RouteAnalysisStatus.UnsupportedConnectionKind, result.Status);
            Assert.Same(rec, result.SourceRecording);
            Assert.Same(unknown, result.ConnectionWindow);
            Assert.Equal("Unknown", result.RejectDetail);
            Assert.Null(result.Stops);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][Route]") &&
                l.Contains("RouteAnalysis rejected: unsupported connection kind kind=Unknown source=aw-ksc window=modded"));
            Assert.Equal(0, CountLines("missing endpoint proof"));
        }

        // catches: a refusal Diag leaking in Quiet mode.
        [Fact]
        public void AW_UnsupportedKind_QuietMode_WritesNothing()
        {
            RouteConnectionWindow unknown = BuildDeliveryWindowAt("modded", 200.0, 260.0);
            unknown.TransferKind = RouteConnectionKind.Unknown;

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(
                AwKscRecording(unknown), RouteAnalysisLogMode.Quiet);

            Assert.Equal(RouteAnalysisStatus.UnsupportedConnectionKind, result.Status);
            Assert.Equal(0, CountLines("[Route]"));
        }

        // ---------------------------------------------------------------
        // Empty-grapple filter
        // ---------------------------------------------------------------

        // catches: the no-stop-bearing reject naming the LAST skipped window, or
        // the skip summary miscounting.
        [Fact]
        public void AW_OnlyEmptyGrapples_RejectNamesFirstSkipped_LogsCounts()
        {
            RouteConnectionWindow g1 = AwEmptyWindow("grab-1", RouteConnectionKind.Grapple, 100.0);
            RouteConnectionWindow g2 = AwEmptyWindow("grab-2", RouteConnectionKind.Grapple, 200.0);
            Recording rec = AwKscRecording(g2, g1);

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.Equal(RouteAnalysisStatus.NoDeliveryManifest, result.Status);
            Assert.Same(rec, result.SourceRecording);
            Assert.Same(g1, result.ConnectionWindow);
            Assert.Null(result.RejectDetail);
            Assert.Contains(logLines, l => l.Contains(
                "RouteAnalysis: skipped 2 empty grapple window(s) as non-stops (structural grabs); " +
                "0 stop-bearing window(s) remain"));
            Assert.Equal(0, CountLines("unmatched transport gain noted"));
            Assert.Contains(logLines, l => l.Contains(
                "RouteAnalysis rejected: no stop-bearing window after empty-grapple skip source=aw-ksc window=grab-1"));
        }

        // catches: the skipped-grapple inventory reject losing its window or
        // source, or the filter running after the endpoint-proof gate.
        [Fact]
        public void AW_ProoflessEmptyGrappleWithInventoryGain_RejectsMixedNamingIt()
        {
            RouteConnectionWindow grab = AwEmptyWindow("inv-grab", RouteConnectionKind.Grapple, 100.0);
            grab.EndpointAtDock = null;
            grab.UndockTransportInventory = new List<InventoryPayloadItem>
            {
                Payload("phantom", "smallCargoContainer", 1, slotsTaken: 1)
            };
            Recording rec = AwKscRecording(grab, BuildDeliveryWindowAt("station", 200.0, 260.0));

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.Equal(RouteAnalysisStatus.MixedPickupDelivery, result.Status);
            Assert.Same(rec, result.SourceRecording);
            Assert.Same(grab, result.ConnectionWindow);
            Assert.Contains(logLines, l => l.Contains(
                "RouteAnalysis rejected: unwitnessed inventory gain on structural grapple window source=aw-ksc window=inv-grab"));
            Assert.Equal(0, CountLines("skipped"));
        }

        // catches: the legacy unmatched-gain reject naming the LAST noted
        // resource / window, or dropping its detail.
        [Fact]
        public void AW_TwoUnmatchedGainGrapples_LegacyRejectNamesFirst()
        {
            RouteConnectionWindow a = AwUnmatchedGainGrapple("grab-a", 100.0, "Ore");
            RouteConnectionWindow b = AwUnmatchedGainGrapple("grab-b", 200.0, "MonoPropellant");
            Recording rec = AwKscRecording(a, b, BuildDeliveryWindowAt("station", 300.0, 360.0));

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.Equal(RouteAnalysisStatus.UntrackedCargoGain, result.Status);
            Assert.Same(rec, result.SourceRecording);
            Assert.Same(a, result.ConnectionWindow);
            Assert.Equal(
                "Ore: gained across a structural grapple window with no complete run manifest to witness it",
                result.RejectDetail);
            Assert.Null(result.HarvestedManifest);
            Assert.Contains(logLines, l => l.Contains(
                "RouteAnalysis: skipped 2 empty grapple window(s) as non-stops (structural grabs); " +
                "1 stop-bearing window(s) remain; unmatched transport gain noted resource=Ore"));
            Assert.Contains(logLines, l => l.Contains(
                "RouteAnalysis rejected: unmatched transport gain in skipped grapple window on legacy tree " +
                "resource=Ore source=aw-ksc window=grab-a"));
        }

        // catches: the undocked-start gate moving ahead of the legacy
        // unmatched-gain reject.
        [Fact]
        public void AW_UnmatchedGainGrapple_BeatsUndockedStartGate()
        {
            Recording rec = AwUndockedRecording("aw-undocked",
                AwUnmatchedGainGrapple("grab", 100.0, "Ore"),
                BuildDeliveryWindowAt("station", 200.0, 260.0));

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.Equal(RouteAnalysisStatus.UntrackedCargoGain, result.Status);
            Assert.Equal(0, CountLines("undocked-start origin"));
        }

        // ---------------------------------------------------------------
        // Endpoint-proof gate
        // ---------------------------------------------------------------

        // catches: the endpoint-proof walk stopping before the LAST window, or
        // the reject naming the wrong window / dropping a log field.
        [Fact]
        public void AW_LastWindowMissingProof_RejectNamesIt()
        {
            RouteConnectionWindow ok = BuildDeliveryWindowAt("ok", 100.0, 160.0);
            RouteConnectionWindow bad = BuildDeliveryWindowAt("no-proof", 200.0, 260.0);
            bad.EndpointAtDock = null;
            Recording rec = AwKscRecording(ok, bad);

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.Equal(RouteAnalysisStatus.MissingEndpointProof, result.Status);
            Assert.Same(rec, result.SourceRecording);
            Assert.Same(bad, result.ConnectionWindow);
            Assert.Contains(logLines, l => l.Contains(
                "RouteAnalysis rejected: missing endpoint proof source=aw-ksc window=no-proof " +
                "targetPid=9001 kind=DockingPort situation=4 endpointAtDock=no"));
        }

        // catches: the endpoint-proof gate moving below the undocked-start gate.
        [Fact]
        public void AW_MissingProof_BeatsUndockedStartGate()
        {
            RouteConnectionWindow bad = BuildDeliveryWindowAt("no-proof", 100.0, 160.0);
            bad.TransferTargetVesselPid = 0;

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(
                AwUndockedRecording("aw-undocked", bad));

            Assert.Equal(RouteAnalysisStatus.MissingEndpointProof, result.Status);
            Assert.Equal(0, CountLines("undocked-start origin"));
        }

        // ---------------------------------------------------------------
        // Non-harvest undocked-start gate
        // ---------------------------------------------------------------

        // catches: the legacy undocked-start reject losing its window, its
        // harvest fields gaining a value, or a diagnostic field moving.
        [Fact]
        public void AW_UndockedStart_Legacy_PinsResultAndLogFields()
        {
            RouteConnectionWindow w = BuildDeliveryWindowAt("only", 100.0, 160.0);
            Recording rec = AwUndockedRecording("aw-undocked", w);

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.Equal(RouteAnalysisStatus.UndockedStartOrigin, result.Status);
            Assert.Same(rec, result.SourceRecording);
            Assert.Same(w, result.ConnectionWindow);
            Assert.Null(result.RejectDetail);
            Assert.Null(result.HarvestedManifest);
            Assert.Null(result.FirstHarvestWindow);
            Assert.Null(result.Stops);
            Assert.False(result.IsHarvestOrigin);
            Assert.Contains(logLines, l => l.Contains(
                "RouteAnalysis rejected: undocked-start origin originRec=aw-undocked launchSite=<none> " +
                "startBody=Mun originProof=no selfOriginProof=no"));
        }

        // catches: the per-window direction gates (inventory gain, no cargo)
        // moving ahead of the legacy undocked-start gate.
        [Fact]
        public void AW_UndockedStart_BeatsInventoryGainAndEmptyWindow()
        {
            RouteAnalysisResult inv = RouteAnalysisEngine.AnalyzeRecording(
                AwUndockedRecording("aw-undocked-inv", AwInventoryGainWindow("inv", 100.0, "smallCargoContainer")));
            RouteAnalysisResult empty = RouteAnalysisEngine.AnalyzeRecording(
                AwUndockedRecording("aw-undocked-empty", AwEmptyWindow("dead", RouteConnectionKind.DockingPort, 100.0)));

            Assert.Equal(RouteAnalysisStatus.UndockedStartOrigin, inv.Status);
            Assert.Equal(RouteAnalysisStatus.UndockedStartOrigin, empty.Status);
            Assert.Equal(0, CountLines("reject reason"));
            Assert.Equal(0, CountLines("no delivery or load manifest"));
        }

        // catches: the single-recording path (no tree) lifting a shuttle-shaped
        // pair as a mid-tree docked origin; with no tree the shape is the
        // recognizable-but-unsupported family (status 9) at the legacy gate.
        [Fact]
        public void AW_UndockedStart_NoTree_ShuttleShape_Status9()
        {
            RouteConnectionWindow depot = BuildDeliveryWindowAt("depot", 100.0, 160.0);
            RouteConnectionWindow drop = BuildDeliveryWindowAt("drop", 300.0, 360.0);
            Recording rec = AwUndockedRecording("aw-undocked", depot, drop);

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.Equal(RouteAnalysisStatus.MidRecordingStartTrimUnsupported, result.Status);
            Assert.False(result.IsMidTreeDockedOrigin);
            Assert.Same(rec, result.SourceRecording);
            Assert.Same(depot, result.ConnectionWindow);
            Assert.Equal("docked origin recorded at UT 100", result.RejectDetail);
            Assert.Null(result.HarvestedManifest);
            Assert.Contains(logLines, l => l.Contains(
                "RouteAnalysis rejected: mid-recording start-trim unsupported (shuttle family) " +
                "originRec=aw-undocked signal=preceding-window originDockUT=100 windows=2"));
            Assert.Equal(0, CountLines("mid-tree docked origin resolved"));
        }

        // ---------------------------------------------------------------
        // Per-window direction gates
        // ---------------------------------------------------------------

        // catches: the per-window inventory gate skipping a later window, or the
        // reject naming the wrong window.
        [Fact]
        public void AW_InventoryGainOnSecondWindow_RejectsNamingIt()
        {
            RouteConnectionWindow ok = BuildDeliveryWindowAt("ok", 100.0, 160.0);
            RouteConnectionWindow gain = AwInventoryGainWindow("gain", 200.0, "smallCargoContainer");
            Recording rec = AwKscRecording(ok, gain);

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.Equal(RouteAnalysisStatus.MixedPickupDelivery, result.Status);
            Assert.Same(rec, result.SourceRecording);
            Assert.Same(gain, result.ConnectionWindow);
            Assert.Null(result.RejectDetail);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][Route]") &&
                l.Contains("RouteAnalysis reject reason: unwitnessed inventory gain source=aw-ksc window=gain "));
            Assert.Contains(logLines, l =>
                l.Contains("RouteAnalysis rejected: unwitnessed inventory gain source=aw-ksc window=gain "));
        }

        // catches: the per-window loop walking a window that already failed
        // (an empty first window rejects before a later inventory gain).
        [Fact]
        public void AW_EmptyFirstWindow_BeatsLaterInventoryGain()
        {
            RouteConnectionWindow dead = AwEmptyWindow("dead", RouteConnectionKind.DockingPort, 100.0);
            Recording rec = AwKscRecording(dead, AwInventoryGainWindow("gain", 200.0, "smallCargoContainer"));

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.Equal(RouteAnalysisStatus.NoDeliveryManifest, result.Status);
            Assert.Same(dead, result.ConnectionWindow);
            Assert.Same(rec, result.SourceRecording);
            Assert.Contains(logLines, l => l.Contains(
                "RouteAnalysis rejected: no delivery or load manifest source=aw-ksc window=dead"));
            Assert.Equal(0, CountLines("reject reason"));
        }

        // catches: the refusal-reason line losing its rate limit (a standing
        // refusal re-analysed at about 1 Hz would bury the log) while the
        // log-mode-gated Diag still prints every time.
        [Fact]
        public void AW_InventoryReason_RateLimitedPerWindowAndReason()
        {
            double now = 1000.0;
            ParsekLog.ClockOverrideForTesting = () => now;
            Recording rec = AwKscRecording(AwInventoryGainWindow("gain", 100.0, "smallCargoContainer"));

            RouteAnalysisEngine.AnalyzeRecording(rec);
            now = 1001.0;
            RouteAnalysisEngine.AnalyzeRecording(rec);

            Assert.Equal(1, CountLines("RouteAnalysis reject reason: unwitnessed inventory gain"));
            Assert.Equal(2, CountLines("RouteAnalysis rejected: unwitnessed inventory gain"));

            // Past the 60 s interval the line prints again with the suppressed count.
            now = 1061.0;
            RouteAnalysisEngine.AnalyzeRecording(rec);
            Assert.Equal(2, CountLines("RouteAnalysis reject reason: unwitnessed inventory gain"));
            Assert.Contains(logLines, l =>
                l.Contains("RouteAnalysis reject reason: unwitnessed inventory gain") &&
                l.Contains("| suppressed=1"));
        }

        // catches: the rate key dropping the window id or the reason, which
        // would swallow a DIFFERENT refusal inside the interval.
        [Fact]
        public void AW_InventoryReason_NewWindowOrReasonPrintsAtOnce()
        {
            ParsekLog.ClockOverrideForTesting = () => 500.0;

            RouteAnalysisEngine.AnalyzeRecording(
                AwKscRecording(AwInventoryGainWindow("gain-a", 100.0, "smallCargoContainer")));
            RouteAnalysisEngine.AnalyzeRecording(
                AwKscRecording(AwInventoryGainWindow("gain-b", 100.0, "smallCargoContainer")));
            RouteAnalysisEngine.AnalyzeRecording(
                AwKscRecording(AwInventoryGainWindow("gain-b", 100.0, "radialDrill")));

            Assert.Equal(3, CountLines("RouteAnalysis reject reason: unwitnessed inventory gain"));
            Assert.Contains(logLines, l => l.Contains("reject reason") && l.Contains("window=gain-a"));
            Assert.Contains(logLines, l => l.Contains("reject reason") && l.Contains("part=radialDrill"));
        }

        // ---------------------------------------------------------------
        // Mid-tree docked origin
        // ---------------------------------------------------------------

        // catches: the resolved ORIGIN window losing its exemption from the
        // no-cargo gate (a parked depot visit with no transfer is legitimate).
        [Fact]
        public void AW_MidTreeOrigin_ParkedEmptyOriginWindow_Eligible()
        {
            RecordingTree tree = BuildShuttleTree(kscOrigin: false, out Recording source);
            RouteConnectionWindow parked = AwEmptyWindow("parked", RouteConnectionKind.DockingPort, 100.0);
            RouteConnectionWindow drop = BuildDeliveryWindowAt("delivery", 300.0, 360.0);
            source.RouteConnectionWindows = new List<RouteConnectionWindow> { parked, drop };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.True(result.IsEligible, $"got {result.Status}");
            Assert.True(result.IsMidTreeDockedOrigin);
            Assert.Same(parked, result.OriginConnectionWindow);
            Assert.Same(source, result.OriginSourceRecording);
            RouteAnalysisStop stop = Assert.Single(result.Stops);
            Assert.Same(drop, stop.ConnectionWindow);
            Assert.Same(drop, result.ConnectionWindow);
            Assert.Contains(logLines, l => l.Contains(
                "RouteAnalysis eligible: source=shuttle-source window=delivery stops=1 resources=1 load=0 " +
                "inventory=1 inventoryLoad=0 harvestData=0 harvestOrigin=0 midTreeOrigin=1"));
        }

        // catches: the origin exemption applying without a resolved mid-tree
        // origin (a KSC run's empty first window is a stop and must reject).
        [Fact]
        public void AW_KscRun_EmptyFirstDockWindow_NotExempt()
        {
            RecordingTree tree = BuildShuttleTree(kscOrigin: true, out Recording source);
            RouteConnectionWindow parked = AwEmptyWindow("parked", RouteConnectionKind.DockingPort, 100.0);
            source.RouteConnectionWindows = new List<RouteConnectionWindow>
            {
                parked, BuildDeliveryWindowAt("delivery", 300.0, 360.0)
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.Equal(RouteAnalysisStatus.NoDeliveryManifest, result.Status);
            Assert.Same(parked, result.ConnectionWindow);
            Assert.Equal(0, CountLines("mid-tree docked origin resolved"));
        }

        // catches: a KSC run's scalar / stop fields shifting when no mid-tree
        // origin resolves: origin fields stay null, the anchor is the first
        // window, and the stops carry their own source recording, DockUT and
        // endpoint with empty load manifests normalised to null.
        [Fact]
        public void AW_KscTwoWindowTree_StopAndScalarFields()
        {
            RecordingTree tree = BuildShuttleTree(kscOrigin: true, out Recording source);
            RouteConnectionWindow first = source.RouteConnectionWindows[0];
            RouteConnectionWindow second = source.RouteConnectionWindows[1];

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.True(result.IsEligible, $"got {result.Status}");
            Assert.False(result.IsMidTreeDockedOrigin);
            Assert.Null(result.OriginConnectionWindow);
            Assert.Null(result.OriginSourceRecording);
            Assert.Same(first, result.ConnectionWindow);
            Assert.Equal(2, result.Stops.Count);
            Assert.Same(first, result.Stops[0].ConnectionWindow);
            Assert.Same(second, result.Stops[1].ConnectionWindow);
            Assert.Equal(160.0 - 60.0, result.Stops[0].DockUT);
            Assert.Same(source, result.Stops[1].SourceRecording);
            Assert.Equal(first.EndpointAtDock.Value.VesselPersistentId,
                result.Stops[0].EndpointAtDock.VesselPersistentId);
            // The pickup stop has a load and no delivery; the delivery stop has
            // no load, and an empty load is reported as null, never empty.
            Assert.NotNull(result.Stops[0].ResourceLoadManifest);
            Assert.Null(result.Stops[1].ResourceLoadManifest);
            Assert.Null(result.Stops[1].InventoryLoadManifest);
            Assert.Null(result.InventoryLoadManifest);
            Assert.Null(result.HarvestedManifest);
            Assert.Null(result.PlausibleHarvestedManifest);
            Assert.Null(result.HarvestCaps);
            Assert.Null(result.FirstHarvestWindow);
            Assert.Null(result.RejectDetail);
            Assert.Contains(logLines, l => l.Contains(
                "RouteAnalysis eligible: source=shuttle-source window=pickup-window stops=2 resources=0 load=1 " +
                "inventory=0 inventoryLoad=0 harvestData=0 harvestOrigin=0 midTreeOrigin=0"));
        }

        // catches: an empty anchor load manifest leaking out as an empty
        // dictionary instead of null.
        [Fact]
        public void AW_DeliveryOnly_AnchorLoadIsNull()
        {
            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeRecording(
                AwKscRecording(BuildDeliveryWindowAt("only", 100.0, 160.0)));

            Assert.True(result.IsEligible);
            Assert.Null(result.ResourceLoadManifest);
            Assert.Null(result.InventoryLoadManifest);
            Assert.Null(result.Stops[0].ResourceLoadManifest);
            Assert.Null(result.Stops[0].InventoryLoadManifest);
            Assert.Equal(100.0, result.Stops[0].DockUT);
        }

        // ---------------------------------------------------------------
        // Non-finite anchor UndockUT warning
        // ---------------------------------------------------------------

        // catches: the eligible-path warning ignoring the log mode (the ~1 Hz
        // Quiet sweep must not write it) or disappearing in Diagnostic mode.
        [Fact]
        public void AW_InfiniteAnchorUndockUT_WarnsOnlyInDiagnosticMode()
        {
            RouteConnectionWindow w = BuildDeliveryWindowAt("inf", 100.0, double.PositiveInfinity);

            RouteAnalysisResult quiet = RouteAnalysisEngine.AnalyzeRecording(
                AwKscRecording(w), RouteAnalysisLogMode.Quiet);
            Assert.True(quiet.IsEligible);
            Assert.Equal(0, CountLines("non-finite UndockUT"));

            RouteAnalysisResult diag = RouteAnalysisEngine.AnalyzeRecording(AwKscRecording(w));
            Assert.True(diag.IsEligible);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN][Route]") &&
                l.Contains("RouteAnalysis: eligible window carries non-finite UndockUT source=aw-ksc window=inf undockUT=Infinity"));
        }

        // ---------------------------------------------------------------
        // Harvest-data path: untracked gain, closure, refined gate
        // ---------------------------------------------------------------

        // catches: the untracked-gain reject dropping its harvest fields or
        // naming another window.
        [Fact]
        public void AW_UntrackedGain_PinsResultFields()
        {
            RecordingTree tree = BuildDrillRunTree(
                out Recording root, out Recording merge,
                rootStartOre: 0.0, dockOre: 120.0, undockOre: 20.0,
                kscOrigin: true);
            root.RouteHarvestWindows = new List<RouteHarvestWindow>
            {
                OreHarvestWindow(150.0, 400.0, 0.0, 50.0)
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.Equal(RouteAnalysisStatus.UntrackedCargoGain, result.Status);
            Assert.Same(merge, result.SourceRecording);
            Assert.Same(merge.RouteConnectionWindows[0], result.ConnectionWindow);
            Assert.Equal("Ore: 120.0 gained, 50.0 harvested", result.RejectDetail);
            Assert.NotNull(result.HarvestedManifest);
            Assert.Equal(50.0, result.HarvestedManifest["Ore"], 6);
            Assert.NotNull(result.FirstHarvestWindow);
            Assert.Null(result.Stops);
            Assert.Contains(logLines, l => l.Contains(
                "RouteAnalysis rejected: untracked cargo gain resource=Ore gained=120 harvested=50 source=drill-merge"));
        }

        // The over-delivery fixture of AnalyzeTree_OverDelivery_RejectsFlowDoesNotClose,
        // optionally with a balanced MonoPropellant pickup on the same window.
        private static RecordingTree BuildAwOverDeliveryTree(bool withMonoPickup)
        {
            var root = new Recording
            {
                RecordingId = "aw-od-root",
                TreeId = "tree-aw-od",
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                ExplicitStartUT = 0.0,
                ExplicitEndUT = 500.0,
                RouteRunManifest = new RouteRunCargoManifest
                {
                    TransportPartPersistentIds = new List<uint> { TransportPid },
                    StartTransportResources = new Dictionary<string, ResourceAmount>
                    {
                        ["LiquidFuel"] = RA(50.0),
                        ["Ore"] = RA(0.0)
                    },
                    EndTransportResources = new Dictionary<string, ResourceAmount>
                    {
                        ["LiquidFuel"] = RA(0.0),
                        ["Ore"] = RA(150.0)
                    },
                    EndCaptured = true
                },
                RouteHarvestWindows = new List<RouteHarvestWindow>
                {
                    OreHarvestWindow(150.0, 400.0, 0.0, 120.0)
                }
            };
            var dockTransport = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = RA(50.0),
                ["Ore"] = RA(120.0)
            };
            var undockTransport = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = RA(0.0),
                ["Ore"] = RA(150.0)
            };
            var dockEndpoint = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = RA(0.0),
                ["Ore"] = RA(0.0)
            };
            var undockEndpoint = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = RA(50.0),
                ["Ore"] = RA(0.0)
            };
            if (withMonoPickup)
            {
                dockTransport["MonoPropellant"] = RA(0.0);
                undockTransport["MonoPropellant"] = RA(10.0);
                dockEndpoint["MonoPropellant"] = RA(10.0);
                undockEndpoint["MonoPropellant"] = RA(0.0);
            }
            var merge = new Recording
            {
                RecordingId = "aw-od-merge",
                TreeId = "tree-aw-od",
                ExplicitStartUT = 500.0,
                ExplicitEndUT = 600.0,
                ParentBranchPointId = "bp-dock",
                RouteRunManifest = new RouteRunCargoManifest
                {
                    TransportPartPersistentIds = new List<uint> { TransportPid, ColonyPid },
                    StartTransportResources = OreAmount(120.0),
                    EndTransportResources = OreAmount(150.0),
                    EndCaptured = true
                },
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        WindowId = "aw-od-window",
                        DockUT = 500.0,
                        UndockUT = 600.0,
                        TransferTargetVesselPid = 9001,
                        TransferKind = RouteConnectionKind.DockingPort,
                        TransportPartPersistentIds = new List<uint> { TransportPid },
                        EndpointPartPersistentIds = new List<uint> { ColonyPid },
                        DockTransportResources = dockTransport,
                        UndockTransportResources = undockTransport,
                        DockEndpointResources = dockEndpoint,
                        UndockEndpointResources = undockEndpoint,
                        EndpointAtDock = Endpoint(),
                        TransferEndpointSituation = 1
                    }
                }
            };
            var tree = new RecordingTree
            {
                Id = "tree-aw-od",
                RootRecordingId = root.RecordingId,
                ActiveRecordingId = merge.RecordingId
            };
            tree.AddOrReplaceRecording(root);
            tree.AddOrReplaceRecording(merge);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-dock",
                UT = 500.0,
                Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { root.RecordingId },
                ChildRecordingIds = new List<string> { merge.RecordingId }
            });
            return tree;
        }

        // catches: the closure reject dropping its harvest fields, its summed
        // load, its source or window.
        [Fact]
        public void AW_FlowDoesNotClose_PinsResultFields()
        {
            RecordingTree tree = BuildAwOverDeliveryTree(withMonoPickup: true);

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.Equal(RouteAnalysisStatus.FlowDoesNotClose, result.Status);
            Assert.Equal("Ore: 30.0 over-delivered", result.RejectDetail);
            Assert.Equal("aw-od-merge", result.SourceRecording.RecordingId);
            Assert.Equal("aw-od-window", result.ConnectionWindow.WindowId);
            Assert.NotNull(result.ResourceLoadManifest);
            Assert.Equal(10.0, result.ResourceLoadManifest["MonoPropellant"], 6);
            Assert.NotNull(result.HarvestedManifest);
            Assert.Equal(120.0, result.HarvestedManifest["Ore"], 6);
            Assert.NotNull(result.FirstHarvestWindow);
            Assert.Null(result.Stops);
            Assert.Contains(logLines, l => l.Contains(
                "RouteAnalysis rejected: flow does not close resource=Ore unaccounted=30 source=aw-od-merge"));
        }

        // catches: a closure reject with no load reporting an empty manifest.
        [Fact]
        public void AW_FlowDoesNotClose_NoLoad_LoadManifestNull()
        {
            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(
                BuildAwOverDeliveryTree(withMonoPickup: false));

            Assert.Equal(RouteAnalysisStatus.FlowDoesNotClose, result.Status);
            Assert.Null(result.ResourceLoadManifest);
        }

        // An undocked-start drill run that delivers inventory as well as the
        // harvested Ore.
        private static RecordingTree BuildAwHarvestRunWithInventoryDelivery(
            out Recording root, out Recording merge)
        {
            RecordingTree tree = BuildDrillRunTree(
                out root, out merge,
                rootStartOre: 0.0, dockOre: 120.0, undockOre: 20.0,
                kscOrigin: false);
            root.RouteHarvestWindows = new List<RouteHarvestWindow>
            {
                OreHarvestWindow(150.0, 400.0, 0.0, 120.0)
            };
            InventoryPayloadItem item = Payload("payload-hash", "evaJetpack", 1, slotsTaken: 1);
            RouteConnectionWindow w = merge.RouteConnectionWindows[0];
            w.DockTransportInventory = new List<InventoryPayloadItem> { item.DeepClone() };
            w.UndockEndpointInventory = new List<InventoryPayloadItem> { item.DeepClone() };
            return tree;
        }

        // catches: the refined gate admitting an inventory delivery as harvest
        // (stored parts have no harvested provenance), or the refined reject
        // dropping its harvest fields.
        [Fact]
        public void AW_RefinedUndocked_InventoryDelivery_Rejects()
        {
            RecordingTree tree = BuildAwHarvestRunWithInventoryDelivery(out _, out Recording merge);

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.Equal(RouteAnalysisStatus.UndockedStartOrigin, result.Status);
            Assert.False(result.IsHarvestOrigin);
            Assert.Same(merge, result.SourceRecording);
            Assert.Same(merge.RouteConnectionWindows[0], result.ConnectionWindow);
            Assert.NotNull(result.HarvestedManifest);
            Assert.Equal(120.0, result.HarvestedManifest["Ore"], 6);
            Assert.NotNull(result.FirstHarvestWindow);
            Assert.Null(result.RejectDetail);
            Assert.Contains(logLines, l => l.Contains(
                "RouteAnalysis rejected: undocked-start origin (harvest-refined) originRec=drill-root " +
                "reason=inventory-delivery-not-harvestable"));
        }

        // catches: the refined verdict losing its harvest fields or reason text.
        [Fact]
        public void AW_RefinedUndocked_PartialHarvest_PinsFieldsAndReason()
        {
            RecordingTree tree = BuildDrillRunTree(
                out Recording root, out Recording merge,
                rootStartOre: 50.0, dockOre: 100.0, undockOre: 20.0,
                kscOrigin: false);
            root.RouteHarvestWindows = new List<RouteHarvestWindow>
            {
                OreHarvestWindow(150.0, 400.0, 50.0, 100.0)
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.Equal(RouteAnalysisStatus.UndockedStartOrigin, result.Status);
            Assert.Same(merge, result.SourceRecording);
            Assert.NotNull(result.HarvestedManifest);
            Assert.Equal(50.0, result.HarvestedManifest["Ore"], 6);
            Assert.NotNull(result.FirstHarvestWindow);
            Assert.Null(result.HarvestCaps);
            Assert.Contains(logLines, l => l.Contains(
                "RouteAnalysis rejected: undocked-start origin (harvest-refined) originRec=drill-root " +
                "reason=delivered-exceeds-harvested resource=Ore delivered=80 harvested=50"));
            Assert.Equal(0, CountLines("harvest origin"));
        }

        // catches: the refined comparison turning inclusive (>=): a delivery of
        // exactly harvested + GainEpsilon is still fully covered.
        [Fact]
        public void AW_RefinedUndocked_DeliveryAtEpsilonBoundary_HarvestOrigin()
        {
            double delivered = 100.0 + RouteHarvestAnalysis.GainEpsilon;
            RecordingTree tree = BuildDrillRunTree(
                out Recording root, out Recording merge,
                rootStartOre: 0.0, dockOre: delivered, undockOre: 0.0,
                kscOrigin: false);
            root.RouteHarvestWindows = new List<RouteHarvestWindow>
            {
                OreHarvestWindow(150.0, 400.0, 0.0, 100.0)
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.True(result.IsEligible, $"got {result.Status}");
            Assert.True(result.IsHarvestOrigin);
            Assert.Equal(delivered, result.ResourceDeliveryManifest["Ore"]);
        }

        // catches: a fully-covered harvest origin with no cap writing the cap
        // line, or reporting null caps instead of an empty list.
        [Fact]
        public void AW_HarvestOrigin_NoCap_EmptyCapsAndNoCapLine()
        {
            RecordingTree tree = BuildDrillRunTree(
                out Recording root, out _,
                rootStartOre: 0.0, dockOre: 120.0, undockOre: 20.0,
                kscOrigin: false);
            root.RouteHarvestWindows = new List<RouteHarvestWindow>
            {
                OreHarvestWindow(150.0, 400.0, 0.0, 120.0)
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.True(result.IsEligible, $"got {result.Status}");
            Assert.True(result.IsHarvestOrigin);
            Assert.NotNull(result.HarvestCaps);
            Assert.Empty(result.HarvestCaps);
            Assert.NotNull(result.PlausibleHarvestedManifest);
            Assert.Equal(0, CountLines("delivery capped to plausible harvest"));
            Assert.Contains(logLines, l => l.Contains(
                "RouteAnalysis: undocked start fully harvest-covered -> harvest origin originRec=drill-root resources=1"));
            Assert.Contains(logLines, l => l.Contains(
                "RouteAnalysis eligible: source=drill-merge window=ore-window stops=1 resources=1 load=0 " +
                "inventory=0 inventoryLoad=0 harvestData=1 harvestOrigin=1 midTreeOrigin=0"));
        }

        // catches: a KSC run on the harvest-data path losing the gain check's
        // caps (not a harvest origin, so no clamp runs and the gain check's own
        // list is reported).
        [Fact]
        public void AW_KscHarvestEngaged_ReportsGainCheckCaps()
        {
            RecordingTree tree = BuildDrillRunTree(
                out Recording root, out _,
                rootStartOre: 0.0, dockOre: 120.0, undockOre: 20.0,
                kscOrigin: true);
            root.RouteHarvestWindows = new List<RouteHarvestWindow>
            {
                OreHarvestWindow(150.0, 400.0, 0.0, 120.0)
            };

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.True(result.IsEligible, $"got {result.Status}");
            Assert.False(result.IsHarvestOrigin);
            Assert.NotNull(result.HarvestCaps);
            Assert.NotNull(result.PlausibleHarvestedManifest);
            Assert.NotNull(result.FirstHarvestWindow);
            Assert.Equal(100.0, result.ResourceDeliveryManifest["Ore"], 6);
            Assert.Contains(logLines, l => l.Contains("harvestData=1 harvestOrigin=0 midTreeOrigin=0"));
        }
        private static Dictionary<string, ResourceAmount> AwRes(double ore, double metal)
        {
            return new Dictionary<string, ResourceAmount> { ["Ore"] = RA(ore), ["Metal"] = RA(metal) };
        }

        // Undocked-start harvest-data run (complete manifests on both legs):
        // 50 Ore harvested before the first dock and 50 Metal aboard from the
        // start. Window 1 delivers the 50 Ore, window 2 (dock 600) delivers the
        // 50 Metal. The kind and undock of window 1 decide whether it can lift
        // as a mid-tree origin or reads as the shuttle family.
        private static RecordingTree BuildAwTwoWindowHarvestTree(
            RouteConnectionKind firstKind, double firstUndockUT)
        {
            var root = new Recording
            {
                RecordingId = "aw-h-root",
                TreeId = "tree-aw-h",
                ExplicitStartUT = 0.0,
                ExplicitEndUT = 500.0,
                RouteRunManifest = new RouteRunCargoManifest
                {
                    TransportPartPersistentIds = new List<uint> { TransportPid },
                    StartTransportResources = AwRes(0.0, 50.0),
                    EndTransportResources = AwRes(50.0, 50.0),
                    EndCaptured = true
                },
                RouteHarvestWindows = new List<RouteHarvestWindow>
                {
                    OreHarvestWindow(150.0, 400.0, 0.0, 50.0)
                }
            };
            var merge = new Recording
            {
                RecordingId = "aw-h-merge",
                TreeId = "tree-aw-h",
                ExplicitStartUT = 500.0,
                ExplicitEndUT = 800.0,
                ParentBranchPointId = "bp-dock",
                RouteRunManifest = new RouteRunCargoManifest
                {
                    TransportPartPersistentIds = new List<uint> { TransportPid, ColonyPid },
                    StartTransportResources = AwRes(50.0, 50.0),
                    EndTransportResources = AwRes(0.0, 0.0),
                    EndCaptured = true
                },
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        WindowId = "aw-h-ore",
                        DockUT = 500.0,
                        UndockUT = firstUndockUT,
                        TransferTargetVesselPid = 9001,
                        TransferKind = firstKind,
                        TransportPartPersistentIds = new List<uint> { TransportPid },
                        EndpointPartPersistentIds = new List<uint> { ColonyPid },
                        DockTransportResources = AwRes(50.0, 50.0),
                        UndockTransportResources = AwRes(0.0, 50.0),
                        DockEndpointResources = AwRes(0.0, 0.0),
                        UndockEndpointResources = AwRes(50.0, 0.0),
                        EndpointAtDock = Endpoint(),
                        TransferEndpointSituation = 1
                    },
                    new RouteConnectionWindow
                    {
                        WindowId = "aw-h-metal",
                        DockUT = 600.0,
                        UndockUT = 800.0,
                        TransferTargetVesselPid = 9001,
                        TransferKind = RouteConnectionKind.DockingPort,
                        TransportPartPersistentIds = new List<uint> { TransportPid },
                        EndpointPartPersistentIds = new List<uint> { ColonyPid },
                        DockTransportResources = AwRes(0.0, 50.0),
                        UndockTransportResources = AwRes(0.0, 0.0),
                        DockEndpointResources = AwRes(50.0, 0.0),
                        UndockEndpointResources = AwRes(50.0, 50.0),
                        EndpointAtDock = Endpoint(),
                        TransferEndpointSituation = 1
                    }
                }
            };
            var tree = new RecordingTree
            {
                Id = "tree-aw-h",
                RootRecordingId = root.RecordingId,
                ActiveRecordingId = merge.RecordingId
            };
            tree.AddOrReplaceRecording(root);
            tree.AddOrReplaceRecording(merge);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-dock",
                UT = 500.0,
                Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { root.RecordingId },
                ChildRecordingIds = new List<string> { merge.RecordingId }
            });
            return tree;
        }

        // catches: the refined gate reading the FIRST window's delivery instead
        // of the gain anchor's (the last window): the harvested Ore of window 1
        // must not cover the unwitnessed Metal of window 2. Overlapping windows
        // keep window 1 from lifting as an origin and from reading as a shuttle.
        [Fact]
        public void AW_RefinedUndocked_ChecksGainAnchorDelivery()
        {
            RecordingTree tree = BuildAwTwoWindowHarvestTree(RouteConnectionKind.DockingPort, 700.0);

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.Equal(RouteAnalysisStatus.UndockedStartOrigin, result.Status);
            Assert.Equal("aw-h-ore", result.ConnectionWindow.WindowId);
            Assert.Contains(logLines, l => l.Contains(
                "RouteAnalysis rejected: undocked-start origin (harvest-refined) originRec=aw-h-root " +
                "reason=delivered-exceeds-harvested resource=Metal delivered=50 harvested=0"));
        }

        // catches: the harvest-refined shuttle reject losing its harvest fields
        // or its own log line. A cargo-bearing GRAPPLE first window never lifts
        // as an origin but still reads as the preceding-window shuttle family.
        [Fact]
        public void AW_RefinedUndocked_GrappleFirstWindow_ShuttleStatus9WithHarvestFields()
        {
            RecordingTree tree = BuildAwTwoWindowHarvestTree(RouteConnectionKind.Grapple, 550.0);

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.Equal(RouteAnalysisStatus.MidRecordingStartTrimUnsupported, result.Status);
            Assert.False(result.IsMidTreeDockedOrigin);
            Assert.Equal("aw-h-ore", result.ConnectionWindow.WindowId);
            Assert.Equal("aw-h-merge", result.SourceRecording.RecordingId);
            Assert.Equal("docked origin recorded at UT 500", result.RejectDetail);
            // The gain check engaged, so its (here empty: the harvest window
            // precedes the checked span) manifest rides on the reject.
            Assert.NotNull(result.HarvestedManifest);
            Assert.Empty(result.HarvestedManifest);
            Assert.Contains(logLines, l => l.Contains(
                "RouteAnalysis rejected: mid-recording start-trim unsupported (shuttle family, harvest-refined) " +
                "originRec=aw-h-root signal=preceding-window originDockUT=500 windows=2 " +
                "reason=delivered-exceeds-harvested resource=Metal"));
            Assert.Equal(0, CountLines("undocked-start origin (harvest-refined)"));
        }

        // catches: the refined verdict naming the LAST offending resource (the
        // walk stops at the first one in delivery-manifest order).
        [Fact]
        public void AW_RefinedUndocked_TwoOffenders_ReasonNamesFirst()
        {
            RecordingTree tree = BuildDrillRunTree(
                out Recording root, out Recording merge,
                rootStartOre: 100.0, dockOre: 100.0, undockOre: 0.0,
                kscOrigin: false);
            root.RouteRunManifest.StartTransportResources = AwRes(100.0, 100.0);
            root.RouteRunManifest.EndTransportResources = AwRes(100.0, 100.0);
            merge.RouteRunManifest.StartTransportResources = AwRes(100.0, 100.0);
            merge.RouteRunManifest.EndTransportResources = AwRes(100.0, 100.0);
            RouteConnectionWindow w = merge.RouteConnectionWindows[0];
            w.DockTransportResources = AwRes(100.0, 100.0);
            w.UndockTransportResources = AwRes(0.0, 0.0);
            w.DockEndpointResources = AwRes(0.0, 0.0);
            w.UndockEndpointResources = AwRes(100.0, 100.0);

            RouteAnalysisResult result = RouteAnalysisEngine.AnalyzeTree(tree);

            Assert.Equal(RouteAnalysisStatus.UndockedStartOrigin, result.Status);
            Assert.Null(result.ResourceDeliveryManifest);
            Assert.Contains(logLines, l => l.Contains(
                "reason=delivered-exceeds-harvested resource=Ore delivered=100 harvested=0"));
            Assert.Equal(0, CountLines("resource=Metal delivered="));
        }
    }
}

