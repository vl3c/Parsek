using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pure formatter tests. Locale-stability is checked by flipping
    /// the thread culture to a comma-decimal culture (de-DE) and
    /// asserting the output stays InvariantCulture-formatted.
    /// </summary>
    [Collection("Sequential")]
    public class RouteCreationFormattersTests : IDisposable
    {
        private readonly CultureInfo originalCulture;

        public RouteCreationFormattersTests()
        {
            originalCulture = Thread.CurrentThread.CurrentCulture;
        }

        public void Dispose()
        {
            Thread.CurrentThread.CurrentCulture = originalCulture;
        }

        // -----------------------------------------------------------------
        // Resource / inventory / endpoint
        // -----------------------------------------------------------------

        [Fact]
        public void FormatResourceLine_UsesInvariantCulture()
        {
            // catches: dropping the InvariantCulture format and slipping back
            // into thread-culture formatting. On de-DE the "150.0" would
            // render as "150,0", which leaks into the player-facing summary
            // and breaks any downstream parser that scans the dialog text.
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            string line = RouteCreationFormatters.FormatResourceLine("LiquidFuel", 150.0);
            Assert.Equal("LiquidFuel: 150.0", line);
        }

        [Fact]
        public void FormatInventoryLine_QuantityOne_OmitsMultiplier()
        {
            // catches: a regression that always appends "x1" for single-item
            // payloads, which would clutter the dialog summary and split the
            // visual hierarchy between single and multi-quantity rows.
            InventoryPayloadItem item = new InventoryPayloadItem
            {
                PartName = "evaJetpack",
                Quantity = 1
            };
            Assert.Equal("evaJetpack", RouteCreationFormatters.FormatInventoryLine(item));
        }

        [Fact]
        public void FormatInventoryLine_WithVariant_IncludesVariantInParens()
        {
            // catches: variant name being silently dropped or the quantity
            // multiplier formatting drifting. Either failure makes two
            // visually-distinct payloads collapse to the same dialog line and
            // hides part-variant differences from the player.
            InventoryPayloadItem item = new InventoryPayloadItem
            {
                PartName = "evaJetpack",
                VariantName = "white",
                Quantity = 2
            };
            Assert.Equal("evaJetpack (white) x2",
                RouteCreationFormatters.FormatInventoryLine(item));
        }

        [Fact]
        public void FormatEndpoint_RoundsCoordinatesToThreeDecimals()
        {
            // catches: rounding precision drifting (e.g. F2/F4 instead of F3)
            // or the unit suffixes changing. The dialog summary uses the
            // exact-shape string; an F4 lat would push the body name off the
            // single-line summary on narrow screens.
            RouteEndpoint ep = new RouteEndpoint
            {
                BodyName = "Mun",
                Latitude = 12.3456789,
                Longitude = -45.6789012,
                Altitude = 612.5
            };
            string s = RouteCreationFormatters.FormatEndpoint(ep);
            Assert.Equal("Mun (12.346°, -45.679°, 613m)", s);
        }

        // -----------------------------------------------------------------
        // Reject messages
        // -----------------------------------------------------------------

        [Fact]
        public void FormatRejectMessage_AllEnumValuesProduceNonEmptyText()
        {
            // catches: a new RouteAnalysisStatus value being added without a
            // matching reject-message branch. The default fallback would
            // render an empty string in the dialog, leaving the player with
            // no explanation for why the route is not eligible.
            foreach (RouteAnalysisStatus status in
                Enum.GetValues(typeof(RouteAnalysisStatus)))
            {
                if (status == RouteAnalysisStatus.Eligible) continue;
                string msg = RouteCreationFormatters.FormatRejectMessage(status);
                Assert.False(string.IsNullOrEmpty(msg),
                    $"Reject message for {status} should be non-empty");
            }
        }

        [Fact]
        public void FormatRejectMessage_MixedPickupDelivery_ExplainsCauseAndFix()
        {
            // catches: the MixedPickupDelivery copy regressing to the old terse
            // "must be one-way in v1" line that did not map to the common
            // destination-full playtest case. The message must (1) state plainly
            // that the transport ended the run with more of a resource than it
            // started (it picked up rather than only delivered) and (2) give the
            // actionable fix (re-record, transfer back out before undocking, or
            // disable transport-tank flow before docking).
            string msg = RouteCreationFormatters.FormatRejectMessage(
                RouteAnalysisStatus.MixedPickupDelivery);

            // (1) plain-language cause
            Assert.Contains("more of a resource than it started", msg);
            Assert.Contains("picked the resource up", msg);
            // one-way contract still stated
            Assert.Contains("one-way", msg);
            // (2) actionable fix: re-record without taking from the destination,
            // and the two destination-full workarounds.
            Assert.Contains("Re-record", msg);
            Assert.Contains("transfer that resource back out before undocking", msg);
            Assert.Contains("disable flow", msg);

            // copy guardrail: plain ASCII only (project hard rule, no em dash or
            // other non-ASCII unicode in player-facing copy).
            foreach (char c in msg)
                Assert.True(c < 128,
                    "Reject copy must be plain ASCII (found non-ASCII char)");
        }

        [Fact]
        public void FormatRejectMessage_UndockedStartOrigin_StatesWorkflowRule()
        {
            // catches (M1, D7): the undocked-start rejection copy regressing to a
            // terse / generic line. The message must (1) state the cause plainly
            // (the run started undocked with cargo aboard, so the cargo's source
            // was never witnessed) and (2) name all three workflow fixes: start
            // docked to the origin depot, record the mining that produced the
            // cargo, or launch from KSC.
            string msg = RouteCreationFormatters.FormatRejectMessage(
                RouteAnalysisStatus.UndockedStartOrigin);

            // (1) plain-language cause
            Assert.Contains("starts undocked with cargo already aboard", msg);
            Assert.Contains("never witnessed", msg);
            // (2) the three workflow fixes
            Assert.Contains("docked to the origin depot", msg);
            Assert.Contains("record the mining", msg);
            Assert.Contains("launch it from KSC", msg);

            // copy guardrail: plain ASCII only (project hard rule, no em dash or
            // other non-ASCII unicode in player-facing copy).
            foreach (char c in msg)
                Assert.True(c < 128,
                    "Reject copy must be plain ASCII (found non-ASCII char)");
        }

        [Fact]
        public void FormatRejectMessage_UntrackedCargoGain_NamesQuantity()
        {
            // catches (M2, D6 / finding 12): the untracked-gain rejection not
            // surfacing the exact unaccounted quantity. The detail-carrying
            // overload must embed the detail verbatim; the detail-less call
            // must still render the full guidance without an empty "()".
            string detail = "Ore: 120.0 gained, 100.0 harvested";
            string msg = RouteCreationFormatters.FormatRejectMessage(
                RouteAnalysisStatus.UntrackedCargoGain, detail);

            Assert.Contains("gained cargo during this run with no recorded source", msg);
            Assert.Contains("(Ore: 120.0 gained, 100.0 harvested)", msg);
            Assert.Contains("record the mining with the drill or converter running", msg);
            Assert.Contains("re-record without the unexplained gain", msg);

            string withoutDetail = RouteCreationFormatters.FormatRejectMessage(
                RouteAnalysisStatus.UntrackedCargoGain);
            Assert.DoesNotContain("()", withoutDetail);
            Assert.Contains("gained cargo during this run with no recorded source", withoutDetail);

            // copy guardrail: plain ASCII only (project hard rule).
            foreach (char c in msg)
                Assert.True(c < 128,
                    "Reject copy must be plain ASCII (found non-ASCII char)");
        }

        [Fact]
        public void FormatRejectMessage_DetailOverload_OtherStatusesUnchanged()
        {
            // catches: the detail overload accidentally injecting the detail
            // into statuses that carry no quantity - every other status must
            // render byte-identically with and without a detail argument.
            foreach (RouteAnalysisStatus status in Enum.GetValues(typeof(RouteAnalysisStatus)))
            {
                if (status == RouteAnalysisStatus.UntrackedCargoGain)
                    continue;
                Assert.Equal(
                    RouteCreationFormatters.FormatRejectMessage(status),
                    RouteCreationFormatters.FormatRejectMessage(
                        status, "Ore: 1.0 gained, 0.0 harvested"));
            }
        }

        // -----------------------------------------------------------------
        // Summary block (Career vs. Sandbox conditional)
        // -----------------------------------------------------------------

        private static RouteAnalysisResult EligibleAnalysis()
        {
            Recording source = new Recording
            {
                RecordingId = "src",
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                ExplicitStartUT = 0.0,
                ExplicitEndUT = 600.0,
                RouteConnectionWindows = new List<RouteConnectionWindow>()
            };
            RouteConnectionWindow window = new RouteConnectionWindow
            {
                TransferTargetVesselPid = 9001,
                TransferKind = RouteConnectionKind.DockingPort,
                EndpointAtDock = new RouteEndpoint
                {
                    BodyName = "Mun",
                    Latitude = 1.0,
                    Longitude = 2.0,
                    Altitude = 100.0
                }
            };
            source.RouteConnectionWindows.Add(window);
            return new RouteAnalysisResult
            {
                Status = RouteAnalysisStatus.Eligible,
                SourceRecording = source,
                ConnectionWindow = window,
                ResourceDeliveryManifest = new Dictionary<string, double>
                {
                    { "LiquidFuel", 50.0 }
                },
                InventoryDeliveryManifest = new List<InventoryPayloadItem>()
            };
        }

        // catches (M2, plan D7): a harvest-origin analysis not classifying as
        // RouteOriginKind.Harvest across the shared origin-identity resolver
        // (all three display surfaces read it), or the harvest body not
        // coming from the first window's open location.
        [Fact]
        public void ResolveOriginIdentity_HarvestOrigin_ClassifiesHarvest()
        {
            RouteAnalysisResult analysis = EligibleAnalysis();
            // Undocked start: no launch site, no proof on the source.
            analysis.SourceRecording.StartBodyName = null;
            analysis.SourceRecording.LaunchSiteName = null;
            analysis.IsHarvestOrigin = true;
            analysis.FirstHarvestWindow = new RouteHarvestWindow
            {
                WindowId = "hw",
                StartUT = 10.0,
                BodyName = "Minmus"
            };

            RouteCreationFormatters.RouteOriginIdentity id =
                RouteCreationFormatters.ResolveOriginIdentity(analysis, null);

            Assert.Equal(RouteCreationFormatters.RouteOriginKind.Harvest, id.Kind);
            Assert.Equal("Minmus", id.BodyName);

            // The candidate-table cell shares the classification.
            Assert.Equal("harvested en route",
                LogisticsWindowUI.FormatCandidateOrigin(analysis, null));
        }

        // catches (M2, plan D7): the dialog summary's Origin line for a
        // harvest-origin route not reading "harvested en route".
        [Fact]
        public void BuildSummaryBlock_HarvestOrigin_LabelsHarvestedEnRoute()
        {
            RouteAnalysisResult analysis = EligibleAnalysis();
            analysis.SourceRecording.StartBodyName = null;
            analysis.SourceRecording.LaunchSiteName = null;
            analysis.IsHarvestOrigin = true;
            analysis.FirstHarvestWindow = new RouteHarvestWindow
            {
                WindowId = "hw",
                StartUT = 10.0,
                BodyName = "Minmus"
            };

            string block = RouteCreationFormatters.BuildSummaryBlock(
                analysis, Game.Modes.SANDBOX);

            Assert.Contains("Origin: harvested en route", block);
        }

        private static RouteRunCostCalculator.RouteRunCost ApplicableRunCost(
            double launch, double recovered, int recoveries)
        {
            double net = launch - recovered;
            return new RouteRunCostCalculator.RouteRunCost
            {
                Applicable = true,
                CostKnown = launch > 0.0,
                LaunchCost = launch,
                RecoveredCredits = recovered,
                NetCost = net > 0.0 ? net : 0.0,
                RecoveryEventCount = recoveries
            };
        }

        [Fact]
        public void BuildSummaryBlock_CareerKsc_WithRunCost_IncludesCostBlock()
        {
            // catches: the run-cost block dropping out of the Career + KSC summary.
            // Career players need cost visibility before confirming the route; the
            // old "Dispatch cost: TBD" placeholder was replaced by the real net.
            RouteRunCostCalculator.RouteRunCost cost = ApplicableRunCost(12500.0, 7300.0, 1);
            string block = RouteCreationFormatters.BuildSummaryBlock(
                EligibleAnalysis(), Game.Modes.CAREER, null, cost);
            Assert.Contains("Cost per run: 5,200 funds", block);
            Assert.Contains("Launch: 12,500 funds", block);
            Assert.Contains("Recovered: 7,300 funds", block);
            // The old placeholder must be gone.
            Assert.DoesNotContain("TBD", block);
        }

        [Fact]
        public void BuildSummaryBlock_SandboxMode_OmitsCostBlock()
        {
            // catches: leaking a cost block into Sandbox mode (no funds). A
            // not-applicable RunCost (the caller computes Applicable=false outside
            // Career) must omit the block entirely.
            RouteRunCostCalculator.RouteRunCost cost = new RouteRunCostCalculator.RouteRunCost
            {
                Applicable = false,
                CostKnown = false
            };
            string block = RouteCreationFormatters.BuildSummaryBlock(
                EligibleAnalysis(), Game.Modes.SANDBOX, null, cost);
            Assert.DoesNotContain("Cost per run", block);
            Assert.DoesNotContain("TBD", block);
        }

        [Fact]
        public void BuildSummaryBlock_NoRunCostPassed_OmitsCostBlock()
        {
            // catches: a null run-cost (test / legacy call paths that do not wire
            // the cost) rendering a stray block. The block must only appear when an
            // applicable, known cost is supplied.
            string block = RouteCreationFormatters.BuildSummaryBlock(
                EligibleAnalysis(), Game.Modes.CAREER);
            Assert.DoesNotContain("Cost per run", block);
            Assert.DoesNotContain("TBD", block);
        }

        [Fact]
        public void BuildSummaryBlock_CareerKsc_UnknownCost_OmitsCostBlock()
        {
            // catches: an unhydrated snapshot (launch 0 -> CostKnown false) leaking a
            // misleading "0 funds" block (gotcha G7). The block must be suppressed.
            RouteRunCostCalculator.RouteRunCost cost = new RouteRunCostCalculator.RouteRunCost
            {
                Applicable = true,
                CostKnown = false,
                LaunchCost = 0.0
            };
            string block = RouteCreationFormatters.BuildSummaryBlock(
                EligibleAnalysis(), Game.Modes.CAREER, null, cost);
            Assert.DoesNotContain("Cost per run", block);
        }

        // -----------------------------------------------------------------
        // Origin identity: resolve off the tree ROOT, not the dock child
        // -----------------------------------------------------------------

        /// <summary>
        /// The bug fixture: a KSC-origin docking flight is recorded as a tree
        /// whose ROOT carries the launch (LaunchSiteName="Launch Pad",
        /// StartBodyName="Kerbin") and whose dock-child source recording (the
        /// analysis source) started mid-flight at the dock, so it has
        /// LaunchSiteName=null. Reading origin off the source mis-reports this as
        /// an unknown origin; the formatters must resolve it off the root.
        /// </summary>
        private static RouteAnalysisResult KscOriginDockChildAnalysis(out RecordingTree tree)
        {
            Recording root = new Recording
            {
                RecordingId = "root",
                TreeId = "tree-ksc",
                TreeOrder = 0,
                StartBodyName = "Kerbin",
                LaunchSiteName = "Launch Pad",
                ExplicitStartUT = 0.0,
                ExplicitEndUT = 300.0
            };
            Recording dockChild = new Recording
            {
                RecordingId = "dock-child",
                TreeId = "tree-ksc",
                TreeOrder = 9,
                StartBodyName = "Kerbin",   // started Orbiting Kerbin at the dock
                LaunchSiteName = null,      // no launch site - started mid-flight
                ExplicitStartUT = 300.0,
                ExplicitEndUT = 600.0,
                RouteConnectionWindows = new List<RouteConnectionWindow>()
            };
            RouteConnectionWindow window = new RouteConnectionWindow
            {
                TransferTargetVesselPid = 9001,
                TransferKind = RouteConnectionKind.DockingPort,
                EndpointAtDock = new RouteEndpoint
                {
                    BodyName = "Kerbin",
                    Latitude = 0.0,
                    Longitude = 0.0,
                    Altitude = 80000.0
                }
            };
            dockChild.RouteConnectionWindows.Add(window);

            tree = new RecordingTree
            {
                Id = "tree-ksc",
                RootRecordingId = root.RecordingId,
                ActiveRecordingId = dockChild.RecordingId
            };
            tree.AddOrReplaceRecording(root);
            tree.AddOrReplaceRecording(dockChild);

            return new RouteAnalysisResult
            {
                Status = RouteAnalysisStatus.Eligible,
                SourceRecording = dockChild,
                ConnectionWindow = window,
                ResourceDeliveryManifest = new Dictionary<string, double>
                {
                    { "LiquidFuel", 50.0 }
                },
                InventoryDeliveryManifest = new List<InventoryPayloadItem>()
            };
        }

        [Fact]
        public void ResolveOriginIdentity_KscOriginViaTreeRoot_ResolvesKscFromRoot()
        {
            // catches: the origin resolver reading the dock-child source instead of
            // the tree root. The source has LaunchSiteName=null, so a source-only
            // resolver returns Unknown even though the route is KSC origin.
            RouteAnalysisResult analysis = KscOriginDockChildAnalysis(out RecordingTree tree);
            RouteCreationFormatters.RouteOriginIdentity id =
                RouteCreationFormatters.ResolveOriginIdentity(analysis, tree);
            Assert.Equal(RouteCreationFormatters.RouteOriginKind.Ksc, id.Kind);
            Assert.Equal("Launch Pad", id.LaunchSiteName);
            Assert.Equal("Kerbin", id.BodyName);
        }

        [Fact]
        public void ResolveOriginIdentity_NullTree_FallsBackToSource()
        {
            // catches: the fallback path breaking. With no resolvable tree root the
            // resolver must fall back to the analysis source. The single-recording
            // EligibleAnalysis() source IS its own root and carries the launch site,
            // so it still resolves KSC even with a null tree.
            RouteCreationFormatters.RouteOriginIdentity id =
                RouteCreationFormatters.ResolveOriginIdentity(EligibleAnalysis(), null);
            Assert.Equal(RouteCreationFormatters.RouteOriginKind.Ksc, id.Kind);
            Assert.Equal("LaunchPad", id.LaunchSiteName);
        }

        [Fact]
        public void BuildSummaryBlock_KscOriginViaTreeRoot_OriginLineShowsLaunchSite()
        {
            // catches: the regression the user reported - the Origin line rendering
            // the "unknown" placeholder (seen in game as "????") for a KSC-origin
            // route because origin was read off the launch-site-less dock child.
            RouteAnalysisResult analysis = KscOriginDockChildAnalysis(out RecordingTree tree);
            string block = RouteCreationFormatters.BuildSummaryBlock(
                analysis, Game.Modes.SANDBOX, tree);
            Assert.Contains("Origin: Kerbin (Launch Pad)", block);
            Assert.DoesNotContain("Origin: unknown", block);
        }

        [Fact]
        public void GenerateDefaultRouteName_KscOriginViaTreeRoot_UsesKscNotDockChildBody()
        {
            // catches: the auto-generated name resolving origin off the dock-child
            // body ("Route: Kerbin -> Kerbin") instead of the KSC root
            // ("Route: KSC -> Kerbin").
            RouteAnalysisResult analysis = KscOriginDockChildAnalysis(out RecordingTree tree);
            string name = RouteCreationFormatters.GenerateDefaultRouteName(analysis, tree);
            Assert.Equal("Route: KSC → Kerbin", name);
            Assert.DoesNotContain("Route: Kerbin", name);
        }

        [Fact]
        public void FormatCandidateOrigin_KscOriginViaTreeRoot_ShowsKscLabel()
        {
            // catches: the candidate-table origin cell showing "-" for a KSC route
            // because it read the dock-child source instead of the tree root.
            RouteAnalysisResult analysis = KscOriginDockChildAnalysis(out RecordingTree tree);
            Assert.Equal("KSC (funds)",
                LogisticsWindowUI.FormatCandidateOrigin(analysis, tree));
        }

        [Fact]
        public void BuildSummaryBlock_DialogBody_HasNoAngleBracketPlaceholders()
        {
            // catches: a fallback placeholder regressing back to angle-bracket form
            // ("<unknown>" / "<none>"). The PopupDialog body is rendered by
            // TextMeshPro with rich text enabled, so "<...>" is parsed as a bogus
            // markup tag and renders as garbage ("????"). A genuine fallback must use
            // bracket-free wording. Build the body from a maximally-empty eligible
            // analysis (no origin / endpoint / resources / inventory) so every
            // fallback branch fires.
            Recording bareSource = new Recording
            {
                RecordingId = "bare",
                RouteConnectionWindows = new List<RouteConnectionWindow>()
            };
            RouteAnalysisResult bare = new RouteAnalysisResult
            {
                Status = RouteAnalysisStatus.Eligible,
                SourceRecording = bareSource,
                ConnectionWindow = null,
                ResourceDeliveryManifest = new Dictionary<string, double>
                {
                    { string.Empty, 1.0 }
                },
                InventoryDeliveryManifest = new List<InventoryPayloadItem> { null }
            };
            string block = RouteCreationFormatters.BuildSummaryBlock(
                bare, Game.Modes.SANDBOX, null);
            Assert.DoesNotContain("<", block);
            Assert.DoesNotContain(">", block);
        }

        /// <summary>
        /// Depot-origin fixture: the start-docked depot proof lives on the tree
        /// ROOT (the recording that started docked to the depot), while the
        /// dock-child source recording carries no proof. The formatters must read
        /// the proof off the root - mirroring RouteBuilder's
        /// originRec.RouteOriginProof - so a real depot route does not render as an
        /// unknown origin.
        /// </summary>
        private static RouteAnalysisResult DepotOriginViaTreeRootAnalysis(out RecordingTree tree)
        {
            Recording root = new Recording
            {
                RecordingId = "depot-root",
                TreeId = "tree-depot",
                TreeOrder = 0,
                StartBodyName = "Mun",
                LaunchSiteName = null,   // not a KSC launch
                RouteOriginProof = new RouteOriginProof
                {
                    StartDockedOriginVesselPid = 4242u
                },
                ExplicitStartUT = 0.0,
                ExplicitEndUT = 300.0
            };
            Recording dockChild = new Recording
            {
                RecordingId = "depot-dock-child",
                TreeId = "tree-depot",
                TreeOrder = 7,
                StartBodyName = "Mun",
                LaunchSiteName = null,
                RouteOriginProof = null,   // proof is on the ROOT, not the dock child
                ExplicitStartUT = 300.0,
                ExplicitEndUT = 600.0,
                RouteConnectionWindows = new List<RouteConnectionWindow>()
            };
            RouteConnectionWindow window = new RouteConnectionWindow
            {
                TransferTargetVesselPid = 7007,
                TransferKind = RouteConnectionKind.DockingPort,
                EndpointAtDock = new RouteEndpoint
                {
                    BodyName = "Mun",
                    Latitude = 1.0,
                    Longitude = 2.0,
                    Altitude = 5000.0
                }
            };
            dockChild.RouteConnectionWindows.Add(window);

            tree = new RecordingTree
            {
                Id = "tree-depot",
                RootRecordingId = root.RecordingId,
                ActiveRecordingId = dockChild.RecordingId
            };
            tree.AddOrReplaceRecording(root);
            tree.AddOrReplaceRecording(dockChild);

            return new RouteAnalysisResult
            {
                Status = RouteAnalysisStatus.Eligible,
                SourceRecording = dockChild,
                ConnectionWindow = window,
                ResourceDeliveryManifest = new Dictionary<string, double>
                {
                    { "LiquidFuel", 25.0 }
                },
                InventoryDeliveryManifest = new List<InventoryPayloadItem>()
            };
        }

        [Fact]
        public void ResolveOriginIdentity_DepotProofOnRoot_ResolvesDepotFromRoot()
        {
            // catches: reading the depot proof off the dock-child source (which has
            // none) instead of the tree root. Mirrors RouteBuilder, which reads
            // originRec.RouteOriginProof from the resolved root.
            RouteAnalysisResult analysis = DepotOriginViaTreeRootAnalysis(out RecordingTree tree);
            RouteCreationFormatters.RouteOriginIdentity id =
                RouteCreationFormatters.ResolveOriginIdentity(analysis, tree);
            Assert.Equal(RouteCreationFormatters.RouteOriginKind.Depot, id.Kind);
            Assert.Equal(4242u, id.DepotVesselPid);
            Assert.Equal("Mun", id.BodyName);
        }

        [Fact]
        public void BuildSummaryBlock_DepotProofOnRoot_OriginLineShowsDepotVessel()
        {
            // catches: the dialog origin line falling to "unknown" for a depot route
            // because the proof was read off the proof-less dock-child source.
            RouteAnalysisResult analysis = DepotOriginViaTreeRootAnalysis(out RecordingTree tree);
            string block = RouteCreationFormatters.BuildSummaryBlock(
                analysis, Game.Modes.SANDBOX, tree);
            Assert.Contains("Origin: Mun (vessel #4242)", block);
            Assert.DoesNotContain("Origin: unknown", block);
        }

        [Fact]
        public void FormatCandidateOrigin_DepotProofOnRoot_ShowsDepotPid()
        {
            // catches: the candidate-table cell showing "-" for a depot route.
            RouteAnalysisResult analysis = DepotOriginViaTreeRootAnalysis(out RecordingTree tree);
            Assert.Equal("depot pid=4242",
                LogisticsWindowUI.FormatCandidateOrigin(analysis, tree));
        }
    }
}
