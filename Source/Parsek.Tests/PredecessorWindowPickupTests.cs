using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// THE PICKUP THAT PREDATES THE RECORDING, pinned headlessly
    /// (ROUTE-ORIGIN-PROOF-PICKUP-PREDATING-THE-RECORDING). The player docks the tanker to
    /// the base, transfers fuel, quicksaves and quits; the next session's recording opens
    /// already docked and never witnesses the inflow. The evidence is on the PREVIOUS
    /// recording of the same launch - its <see cref="RouteConnectionWindow"/> brackets the
    /// load - and this file pins what makes that window admissible and what refuses it.
    ///
    /// <para>Derivation: docs/dev/research/pickup-predating-the-recording.md.</para>
    /// </summary>
    [Collection("Sequential")]
    public class PredecessorWindowPickupTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public PredecessorWindowPickupTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private const string GuidA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string GuidB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        // The seam being bound: half A is the run's transport, half B the depot it is docked
        // to. Same shape as StartDockedOriginBindingTests so the two files describe one world.
        private static readonly List<uint> TransportParts = new List<uint> { 110u, 111u, 112u };
        private static readonly List<uint> DepotParts = new List<uint> { 113u, 114u };
        private const uint DepotRootFlightId = 555u;
        private const uint TransportRootFlightId = 200u;

        private static Dictionary<string, ResourceAmount> Manifest(params object[] pairs)
        {
            var dict = new Dictionary<string, ResourceAmount>();
            for (int i = 0; i < pairs.Length; i += 2)
            {
                dict[(string)pairs[i]] = new ResourceAmount
                {
                    amount = System.Convert.ToDouble(pairs[i + 1]),
                    maxAmount = 1000.0
                };
            }
            return dict;
        }

        private static List<InventoryPayloadItem> Inv(params object[] pairs)
        {
            var items = new List<InventoryPayloadItem>();
            for (int i = 0; i < pairs.Length; i += 2)
            {
                items.Add(new InventoryPayloadItem
                {
                    IdentityHash = (string)pairs[i],
                    PartName = (string)pairs[i],
                    Quantity = System.Convert.ToInt32(pairs[i + 1]),
                    SlotsTaken = 1,
                });
            }
            return items;
        }

        /// <summary>
        /// The window the PREVIOUS recording holds: the transport docked to the depot at
        /// UT 100 carrying 0 LiquidFuel, and it was STILL DOCKED when that recording ended
        /// (no undock UT) - which is exactly why the next recording starts docked.
        /// </summary>
        private static RouteConnectionWindow OpenLoadWindow(
            double dockTransportLf = 0.0,
            uint endpointRoot = DepotRootFlightId,
            List<uint> transportPids = null,
            List<uint> endpointPids = null)
        {
            return new RouteConnectionWindow
            {
                WindowId = "dock-100-target-42",
                DockUT = 100.0,
                TransferTargetVesselPid = 42u,
                EndpointRootPartUId = endpointRoot,
                TransferKind = RouteConnectionKind.DockingPort,
                TransportPartPersistentIds = transportPids ?? new List<uint>(TransportParts),
                EndpointPartPersistentIds = endpointPids ?? new List<uint>(DepotParts),
                DockTransportResources = Manifest("LiquidFuel", dockTransportLf),
                DockEndpointResources = Manifest("LiquidFuel", 800.0),
            };
        }

        private static RouteConnectionWindow ClosedLoadWindow(
            double dockLf, double undockLf, double undockUT)
        {
            RouteConnectionWindow w = OpenLoadWindow(dockLf);
            w.UndockUT = undockUT;
            w.UndockTransportResources = Manifest("LiquidFuel", undockLf);
            return w;
        }

        private static RouteProofCapture.PredecessorPickupEvidence Classify(
            RouteConnectionWindow window,
            double recordingStartUT = 500.0,
            Dictionary<string, ResourceAmount> transportStartResources = null,
            List<InventoryPayloadItem> transportStartInventory = null,
            List<uint> transportHalfPids = null,
            List<uint> originHalfPids = null,
            uint originRoot = DepotRootFlightId)
        {
            return RouteProofCapture.ClassifyPredecessorPickup(
                window == null ? null : new List<RouteConnectionWindow> { window },
                transportHalfPids ?? TransportParts,
                originHalfPids ?? DepotParts,
                originRoot,
                recordingStartUT,
                transportStartResources ?? Manifest("LiquidFuel", 20.0),
                transportStartInventory);
        }

        // ==============================================================
        // 1. WHICH WINDOW COUNTS - the (c) rules
        // ==============================================================

        [Fact]
        public void OpenWindow_TransportGainedBeforeThisRecordingStarted_Validates()
        {
            // THE PLAYER CASE. The previous recording docked at UT 100 with an empty tank and
            // ended still docked; this recording's transport half opens with 20 LiquidFuel
            // aboard. The rise is real and it was witnessed - one recording back.
            RouteProofCapture.PredecessorPickupEvidence evidence = Classify(OpenLoadWindow());

            Assert.Equal(RouteProofCapture.PredecessorPickupOutcome.GainFromPredecessorWindow, evidence.Outcome);
            Assert.True(evidence.IsValidating);
            Assert.True(evidence.WindowWasOpen);
            Assert.Equal("dock-100-target-42", evidence.WindowId);
            Assert.Equal(100.0, evidence.DockUT);
        }

        [Fact]
        public void CompleteWindow_UndockedBeforeThisRecordingStarted_Validates()
        {
            // The todo's stated shape: a window that closed at or before this recording's
            // start is measured INSIDE itself, dock manifest -> undock manifest, and needs
            // nothing from this recording.
            RouteProofCapture.PredecessorPickupEvidence evidence = Classify(
                ClosedLoadWindow(dockLf: 0.0, undockLf: 200.0, undockUT: 150.0));

            Assert.Equal(RouteProofCapture.PredecessorPickupOutcome.GainFromPredecessorWindow, evidence.Outcome);
            Assert.False(evidence.WindowWasOpen);
            Assert.Equal(150.0, evidence.UndockUT);
        }

        [Fact]
        public void CompleteWindow_ThatClosesAfterThisRecordingStarted_IsRefused()
        {
            // A window still running when this recording began is THIS recording's business,
            // and the in-recording rule already measures it. Admitting it here would
            // double-count the same span through a second door.
            Assert.Equal(
                RouteProofCapture.PredecessorPickupOutcome.WindowAfterRecordingStart,
                Classify(ClosedLoadWindow(0.0, 200.0, undockUT: 900.0), recordingStartUT: 500.0)
                    .Outcome);
        }

        [Fact]
        public void Window_DockedAfterThisRecordingStarted_IsRefused()
        {
            RouteConnectionWindow late = OpenLoadWindow();
            late.DockUT = 900.0;
            Assert.Equal(
                RouteProofCapture.PredecessorPickupOutcome.WindowAfterRecordingStart,
                Classify(late, recordingStartUT: 500.0).Outcome);
        }

        [Fact]
        public void UnknownRecordingStartUT_RefusesRatherThanAdmittingEverything()
        {
            // FAIL-CLOSED ON A MISSING CLOCK. A NaN start makes every ordering comparison
            // false, so an unguarded implementation would admit any window at all.
            Assert.Equal(
                RouteProofCapture.PredecessorPickupOutcome.WindowAfterRecordingStart,
                Classify(OpenLoadWindow(), recordingStartUT: double.NaN).Outcome);
        }

        [Fact]
        public void NoWindows_AndNoDockUT_ReadAsNoWindows()
        {
            Assert.Equal(RouteProofCapture.PredecessorPickupOutcome.NoWindows, Classify(null).Outcome);
            Assert.Equal(
                RouteProofCapture.PredecessorPickupOutcome.NoWindows,
                RouteProofCapture.ClassifyPredecessorPickup(
                    new List<RouteConnectionWindow>(), TransportParts, DepotParts,
                    DepotRootFlightId, 500.0, Manifest("LiquidFuel", 20.0), null).Outcome);

            RouteConnectionWindow undated = OpenLoadWindow();
            undated.DockUT = double.NaN;
            Assert.Equal(RouteProofCapture.PredecessorPickupOutcome.NoWindows, Classify(undated).Outcome);
        }

        [Fact]
        public void OnlyTheLATESTWindowIsEVALUATED_NoCherryPicking()
        {
            // THE ANTI-CHERRY-PICK CELL. The transport loaded at one seam and then made a
            // DELIVERY at the next one against the same partner. Scanning for "any window
            // that gained" would validate an origin that the transport, by the time this
            // recording started, had given cargo back to. What the transport left the partner
            // with is what the LAST thing at that seam says.
            var gaining = ClosedLoadWindow(dockLf: 0.0, undockLf: 200.0, undockUT: 150.0);
            var delivering = ClosedLoadWindow(dockLf: 200.0, undockLf: 0.0, undockUT: 300.0);
            delivering.WindowId = "dock-250-target-42";
            delivering.DockUT = 250.0;

            RouteProofCapture.PredecessorPickupEvidence evidence = RouteProofCapture.ClassifyPredecessorPickup(
                new List<RouteConnectionWindow> { gaining, delivering },
                TransportParts, DepotParts, DepotRootFlightId, 500.0,
                Manifest("LiquidFuel", 20.0), null);

            Assert.Equal(RouteProofCapture.PredecessorPickupOutcome.NoRise, evidence.Outcome);
            Assert.Equal("dock-250-target-42", evidence.WindowId);
        }

        [Fact]
        public void WindowWithADifferentPARTNER_IsNotEvidence()
        {
            // The endpoint side of the window is some third vessel. It says nothing about the
            // partner this bind is about to name as the origin.
            Assert.Equal(
                RouteProofCapture.PredecessorPickupOutcome.NoPartnerMatch,
                Classify(OpenLoadWindow(endpointPids: new List<uint> { 900u, 901u })).Outcome);

            // ... and the mirror direction: the window's TRANSPORT side is not the half now
            // flying the run, so the manifests it holds are some other vessel's.
            Assert.Equal(
                RouteProofCapture.PredecessorPickupOutcome.NoPartnerMatch,
                Classify(OpenLoadWindow(transportPids: new List<uint> { 900u, 901u })).Outcome);
        }

        [Fact]
        public void PartnerRootFlightIdMismatch_RefusesEvenWhenThePartPidsOverLAP()
        {
            // THE CRAFT-BAKED TRAP, closed. Two launches of one craft file share every part
            // persistentId, so the pid overlap above cannot separate two identical bases. A
            // root part flightID is assigned per launch and can, and when both sides know it
            // a disagreement is conclusive.
            Assert.Equal(
                RouteProofCapture.PredecessorPickupOutcome.PartnerRootMismatch,
                Classify(OpenLoadWindow(endpointRoot: 777u), originRoot: DepotRootFlightId)
                    .Outcome);
        }

        [Fact]
        public void UnknownRootFlightIdOnEitherSide_DegradesToThePartPidOverlap()
        {
            // The same degradation every identity site here applies to an unknown launch
            // guid: an id nobody knows cannot refuse. A window recorded by a producer that
            // could not read the endpoint's root carries 0, and so does a seam half whose
            // vesselInfo had none.
            Assert.Equal(
                RouteProofCapture.PredecessorPickupOutcome.GainFromPredecessorWindow,
                Classify(OpenLoadWindow(endpointRoot: 0u)).Outcome);
            Assert.Equal(
                RouteProofCapture.PredecessorPickupOutcome.GainFromPredecessorWindow,
                Classify(OpenLoadWindow(), originRoot: 0u).Outcome);
        }

        [Fact]
        public void OpenWindow_TransportPartSetDrifted_RefusesRatherThanDifferenceTwoScopes()
        {
            // ONLY THE OPEN SHAPE CROSSES TWO RECORDS, so only it can difference manifests
            // that were scoped to different part sets. A staging or EVA-construction drift
            // across the docked span would otherwise read as a rise (or hide one).
            Assert.Equal(
                RouteProofCapture.PredecessorPickupOutcome.TransportPartSetDrift,
                Classify(OpenLoadWindow(transportPids: new List<uint> { 110u, 111u }),
                    transportHalfPids: new List<uint>(TransportParts)).Outcome);
        }

        [Fact]
        public void CompleteWindow_NeedsNoPartSetEqualityCheck()
        {
            // The mirror of the cell above: a CLOSED window's two manifests are both its own,
            // taken on one part set at both ends, so a superset/subset relation against the
            // seam-derived half is not a drift and must not refuse.
            RouteConnectionWindow closed =
                ClosedLoadWindow(dockLf: 0.0, undockLf: 200.0, undockUT: 150.0);
            closed.TransportPartPersistentIds = new List<uint> { 110u, 111u };
            Assert.Equal(
                RouteProofCapture.PredecessorPickupOutcome.GainFromPredecessorWindow,
                Classify(closed).Outcome);
        }

        // ==============================================================
        // 2. WHAT "RISE" MEANS - the (d) rules, reusing ClassifyOriginPickup
        // ==============================================================

        [Fact]
        public void PureDeliveryAcrossThePredecessorWindow_IsNotAPickup()
        {
            // THE WRONG-DEBIT THE WHOLE FEATURE EXISTS TO AVOID, one recording back: the
            // transport arrived full, handed cargo to the depot, and is still docked. It
            // still HAS fuel aboard, and "undocked with cargo aboard" is exactly the reading
            // that would name the vessel it just delivered to as its supply origin.
            Assert.Equal(
                RouteProofCapture.PredecessorPickupOutcome.NoRise,
                Classify(OpenLoadWindow(dockTransportLf: 500.0),
                    transportStartResources: Manifest("LiquidFuel", 20.0)).Outcome);
        }

        [Fact]
        public void NothingMovedAcrossThePredecessorWindow_IsNotAPickup()
        {
            Assert.Equal(
                RouteProofCapture.PredecessorPickupOutcome.NoRise,
                Classify(OpenLoadWindow(dockTransportLf: 20.0),
                    transportStartResources: Manifest("LiquidFuel", 20.0)).Outcome);
        }

        [Fact]
        public void InventoryRiseAcrossThePredecessorWindow_ValidatesLikeAResource()
        {
            // The 2026-09-02 ruling ("a route candidate comes from ACTIONS - docked, took
            // fuel OR CARGO from it, undocked") reaches this door too, because the rise is
            // ClassifyOriginPickup and not a second transfer rule.
            RouteConnectionWindow window = OpenLoadWindow(dockTransportLf: 20.0);
            window.DockTransportInventory = Inv();
            Assert.Equal(
                RouteProofCapture.PredecessorPickupOutcome.GainFromPredecessorWindow,
                Classify(window,
                    transportStartResources: Manifest("LiquidFuel", 20.0),
                    transportStartInventory: Inv("DeployedCentralStation", 1)).Outcome);
        }

        [Fact]
        public void NoBaselineAndNoEndManifest_ReadUnmeasurable_NeverValidating()
        {
            RouteConnectionWindow noEnd = OpenLoadWindow();
            RouteProofCapture.PredecessorPickupEvidence evidence = RouteProofCapture.ClassifyPredecessorPickup(
                new List<RouteConnectionWindow> { noEnd },
                TransportParts, DepotParts, DepotRootFlightId, 500.0, null, null);
            Assert.Equal(RouteProofCapture.PredecessorPickupOutcome.Unmeasurable, evidence.Outcome);
            Assert.False(evidence.IsValidating);

            // A complete window whose undock manifests were never captured is the same
            // statement: nothing was measured, so nothing is validated.
            RouteConnectionWindow closedNoManifest = OpenLoadWindow();
            closedNoManifest.UndockUT = 150.0;
            Assert.Equal(
                RouteProofCapture.PredecessorPickupOutcome.Unmeasurable,
                Classify(closedNoManifest).Outcome);
        }

        [Fact]
        public void MissingDockBaseline_CannotFabricateARise()
        {
            // The window has no dock-side transport manifest at all. ClassifyOriginPickup's
            // null-baseline rule then reads Carried, not Gain, and this door must not
            // upgrade it: no baseline means no delta.
            RouteConnectionWindow noBaseline = OpenLoadWindow();
            noBaseline.DockTransportResources = null;
            Assert.Equal(RouteProofCapture.PredecessorPickupOutcome.NoRise, Classify(noBaseline).Outcome);
        }

        // ==============================================================
        // 3. THE PREDECESSOR WALK - the (b) rules
        // ==============================================================

        private static Recording Rec(string id, uint pid = 400u, string guid = GuidA)
        {
            return new Recording
            {
                RecordingId = id,
                VesselPersistentId = pid,
                RecordedVesselGuid = guid,
            };
        }

        private static RecordingTree TreeOf(params Recording[] recs)
        {
            var tree = new RecordingTree { Id = "tree-1" };
            for (int i = 0; i < recs.Length; i++)
                tree.Recordings[recs[i].RecordingId] = recs[i];
            if (recs.Length > 0)
                tree.RootRecordingId = recs[0].RecordingId;
            return tree;
        }

        [Fact]
        public void Walk_FollowsTheParentBranchPoint_TheSwitchContinuationEdge()
        {
            // THE EDGE THAT CARRIES THE PLAYER CASE. Re-entering flight through the stock
            // Fly / Switch-To route builds a NEW recording in the SAME tree linked ONLY by a
            // VesselSwitchContinuation branch point - no ChainId, no ParentRecordingId.
            Recording prev = Rec("prev");
            Recording current = Rec("current");
            current.ParentBranchPointId = "bp-1";
            RecordingTree tree = TreeOf(prev, current);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-1",
                Type = BranchPointType.VesselSwitchContinuation,
                ParentRecordingIds = new List<string> { "prev" },
                ChildRecordingIds = new List<string> { "current" },
            });

            Assert.True(RouteProofCapture.TryResolveOriginProofPredecessor(
                tree, current, out Recording predecessor, out string reason));
            Assert.Same(prev, predecessor);
            Assert.Equal("parent-recording", reason);
        }

        [Fact]
        public void Walk_RefusesAPredecessorFromADIFFERENTLAUNCH()
        {
            // THE GUID GATE, and it is not decoration: persistentId is craft-baked and reused
            // verbatim by every launch of a craft file, so a bare pid match would let one
            // launch's windows validate another launch's pickup.
            Recording prev = Rec("prev", pid: 400u, guid: GuidB);
            Recording current = Rec("current", pid: 400u, guid: GuidA);
            current.ParentBranchPointId = "bp-1";
            RecordingTree tree = TreeOf(prev, current);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-1",
                ParentRecordingIds = new List<string> { "prev" },
            });

            Assert.False(RouteProofCapture.TryResolveOriginProofPredecessor(
                tree, current, out Recording predecessor, out string reason));
            Assert.Null(predecessor);
            Assert.Equal("predecessor-not-same-launch", reason);
        }

        [Fact]
        public void Walk_RefusesACHAINPredecessorFromADIFFERENTLAUNCH()
        {
            // THE SAME GATE ON THE THIRD EDGE, which is the one a same-craft relaunch reaches:
            // chain segments are matched by ChainId + ChainBranch + ChainIndex, all of which a
            // second launch of the same craft file can carry, so without the guid check one
            // launch's still-open dock window would validate the next launch's pickup.
            Recording seg0 = Rec("seg0", pid: 400u, guid: GuidB);
            seg0.ChainId = "chain-1";
            seg0.ChainIndex = 0;
            Recording seg1 = Rec("seg1", pid: 400u, guid: GuidA);
            seg1.ChainId = "chain-1";
            seg1.ChainIndex = 1;

            Assert.False(RouteProofCapture.TryResolveOriginProofPredecessor(
                TreeOf(seg0, seg1), seg1, out Recording predecessor, out string reason));
            Assert.Null(predecessor);
            Assert.Equal("predecessor-not-same-launch", reason);
        }

        [Fact]
        public void Walk_FallsBackToParentRecordingIdThenToTheChainPredecessor()
        {
            Recording prev = Rec("prev");
            Recording current = Rec("current");
            current.ParentRecordingId = "prev";
            Assert.True(RouteProofCapture.TryResolveOriginProofPredecessor(
                TreeOf(prev, current), current, out Recording byParentId, out _));
            Assert.Same(prev, byParentId);

            Recording seg0 = Rec("seg0");
            seg0.ChainId = "chain-1";
            seg0.ChainIndex = 0;
            Recording seg1 = Rec("seg1");
            seg1.ChainId = "chain-1";
            seg1.ChainIndex = 1;
            Recording seg2 = Rec("seg2");
            seg2.ChainId = "chain-1";
            seg2.ChainIndex = 2;
            Assert.True(RouteProofCapture.TryResolveOriginProofPredecessor(
                TreeOf(seg0, seg1, seg2), seg2, out Recording byChain, out string chainReason));
            // The IMMEDIATE predecessor, not the chain head: the window that bracketed the
            // load is on the segment that ended still docked.
            Assert.Same(seg1, byChain);
            Assert.Equal("chain-predecessor", chainReason);
        }

        [Fact]
        public void Walk_TheTreeROOTHasNoPredecessor()
        {
            Recording root = Rec("root");
            Assert.False(RouteProofCapture.TryResolveOriginProofPredecessor(
                TreeOf(root), root, out Recording predecessor, out string reason));
            Assert.Null(predecessor);
            Assert.Equal("no-predecessor", reason);

            // ... and neither does a null tree or a null recording.
            Assert.False(RouteProofCapture.TryResolveOriginProofPredecessor(
                null, root, out _, out string nullReason));
            Assert.Equal("no-tree", nullReason);
        }

        [Fact]
        public void Walk_ASelfReferencingParentEdgeIsNotAPredecessor()
        {
            Recording current = Rec("current");
            current.ParentRecordingId = "current";
            Assert.False(RouteProofCapture.TryResolveOriginProofPredecessor(
                TreeOf(current), current, out Recording predecessor, out _));
            Assert.Null(predecessor);
        }

        [Fact]
        public void Walk_AChainSegmentZeroLooksNoFurtherBack()
        {
            Recording seg0 = Rec("seg0");
            seg0.ChainId = "chain-1";
            seg0.ChainIndex = 0;
            Assert.False(RouteProofCapture.TryResolveOriginProofPredecessor(
                TreeOf(seg0), seg0, out _, out _));
        }

        // ==============================================================
        // 4. THE WHOLE BINDER, over a two-recording chain
        // ==============================================================

        private static RouteOriginProof PendingProof(double transportStartLf)
        {
            return new RouteOriginProof
            {
                StartDockedOriginBindState = StartDockedOriginBindState.PairPendingBinding,
                StartDockedPair = new StartDockedSeamPair
                {
                    HalfA = new StartDockedSeamHalf
                    {
                        RootPartUId = TransportRootFlightId,
                        VesselName = "tanker",
                        VesselType = (int)VesselType.Ship,
                        PartPersistentIds = new List<uint>(TransportParts),
                        StartResources = Manifest("LiquidFuel", transportStartLf),
                        StartInventory = new List<InventoryPayloadItem>(),
                    },
                    HalfB = new StartDockedSeamHalf
                    {
                        RootPartUId = DepotRootFlightId,
                        VesselName = "base",
                        VesselType = (int)VesselType.Base,
                        PartPersistentIds = new List<uint>(DepotParts),
                        StartResources = Manifest("LiquidFuel", 800.0),
                        StartInventory = new List<InventoryPayloadItem>(),
                    },
                },
                StartTransportResources = Manifest("LiquidFuel", 810.0),
            };
        }

        private static ConfigNode SnapshotWithResource(uint partPid, string resource, double amount)
        {
            var vessel = new ConfigNode("VESSEL");
            var part = new ConfigNode("PART");
            part.AddValue("persistentId", partPid.ToString(CultureInfo.InvariantCulture));
            part.AddValue("name", "fuelTank");
            var res = part.AddNode("RESOURCE");
            res.AddValue("name", resource);
            res.AddValue("amount", amount.ToString("R", CultureInfo.InvariantCulture));
            res.AddValue("maxAmount", "1000");
            vessel.AddNode(part);
            return vessel;
        }

        private bool Bind(
            RouteOriginProof proof,
            double undockLf,
            List<RouteConnectionWindow> predecessorWindows,
            List<RouteConnectionWindow> ownWindows = null,
            double recordingStartUT = 500.0)
        {
            ConfigNode snapshot = SnapshotWithResource(110u, "LiquidFuel", undockLf);
            return RouteProofCapture.TryBindStartDockedOriginAtUndock(
                proof, TransportParts, DepotParts, snapshot, snapshot,
                originLiveVesselPid: 500u, originLiveVesselGuid: GuidB,
                recordedVesselPid: 400u, recordedVesselGuid: GuidA,
                undockUT: 1234.5, recordingContext: "rec-current",
                backgroundSideSnapshot: null,
                recordingConnectionWindows: ownWindows,
                activeSideLiveVesselPid: 0u,
                activeSideLiveVesselGuid: null,
                predecessorConnectionWindows: predecessorWindows,
                recordingStartUT: recordingStartUT,
                predecessorContext: "rec-prev");
        }

        [Fact]
        public void Bind_LoadedYesterday_UndockedToday_ValidatesAndBecomesAnOrigin()
        {
            // END TO END, over the two-recording chain the player actually produces. The
            // previous recording docked with an empty tank and ended still docked; this one
            // opens with 20 LiquidFuel aboard and undocks without ever seeing the transfer.
            // Before this pass the bind stamped pickup=Carried pickupValidated=0 and
            // HasDockedOriginProof refused the run.
            RouteOriginProof proof = PendingProof(transportStartLf: 20.0);

            Assert.True(Bind(proof, undockLf: 20.0,
                predecessorWindows: new List<RouteConnectionWindow> { OpenLoadWindow() }));

            Assert.Equal(
                OriginPickupKind.GainFromPredecessorWindow,
                proof.StartDockedOriginPickupKind);
            Assert.True(proof.StartDockedOriginPickupValidated);
            Assert.Equal(DepotRootFlightId, proof.StartDockedOriginRootPartUId);
            Assert.Equal(
                StartDockedOriginBindState.BoundAtUndock, proof.StartDockedOriginBindState);

            var rec = new Recording { RecordingId = "r", RouteOriginProof = proof };
            Assert.True(Parsek.Logistics.RouteAnalysisEngine.HasDockedOriginProof(rec));

            Assert.Contains(logLines, l => l.Contains("RouteOriginProof bound at undock:")
                && l.Contains("pickup=GainFromPredecessorWindow")
                && l.Contains("pickupValidated=1")
                && l.Contains("gate=BindGain")
                && l.Contains("dockWitnessed=0")
                && l.Contains("predecessor=rec-prev")
                && l.Contains("predecessorPickup=GainFromPredecessorWindow")
                && l.Contains("predecessorWindow=dock-100-target-42"));
        }

        [Fact]
        public void Bind_WithNoPredecessorEvidence_StillStampsTheUnvalidatedCarried()
        {
            // THE UNCHANGED DEFAULT. Nothing about the pre-existing shape moves: without a
            // partner-matched window showing a rise, the proof is stamped exactly as it
            // always was and is not an origin.
            RouteOriginProof proof = PendingProof(transportStartLf: 20.0);

            Assert.True(Bind(proof, undockLf: 20.0, predecessorWindows: null));

            Assert.Equal(OriginPickupKind.Carried, proof.StartDockedOriginPickupKind);
            Assert.False(proof.StartDockedOriginPickupValidated);
            var rec = new Recording { RecordingId = "r", RouteOriginProof = proof };
            Assert.False(Parsek.Logistics.RouteAnalysisEngine.HasDockedOriginProof(rec));
            Assert.Contains(logLines, l => l.Contains("RouteOriginProof bound at undock:")
                && l.Contains("predecessorPickup=NoWindows")
                && l.Contains("predecessor=rec-prev"));
        }

        [Fact]
        public void Bind_APredecessorDELIVERYWindowDoesNotValidate()
        {
            RouteOriginProof proof = PendingProof(transportStartLf: 20.0);

            Assert.True(Bind(proof, undockLf: 20.0,
                predecessorWindows: new List<RouteConnectionWindow>
                {
                    OpenLoadWindow(dockTransportLf: 500.0),
                }));

            Assert.Equal(OriginPickupKind.Carried, proof.StartDockedOriginPickupKind);
            Assert.False(proof.StartDockedOriginPickupValidated);
            Assert.Contains(logLines, l => l.Contains("RouteOriginProof bound at undock:")
                && l.Contains("predecessorPickup=NoRise"));
        }

        [Fact]
        public void Bind_AWITNESSEDDockThatMovedNothing_IsNotRescuedByAPredecessorWindow()
        {
            // THE ONE-WAY DOOR. When THIS recording watched the dock and measured no rise,
            // that is POSITIVE delivery evidence and the seam is refused outright. A window
            // one recording back must never overturn what this recording saw - otherwise a
            // stale load would re-validate the vessel the run has since delivered to.
            RouteOriginProof proof = PendingProof(transportStartLf: 20.0);
            var ownWindow = new RouteConnectionWindow
            {
                WindowId = "own-window",
                DockUT = 600.0,
                UndockUT = 1234.5,
                TransportPartPersistentIds = new List<uint>(TransportParts),
                EndpointPartPersistentIds = new List<uint>(DepotParts),
            };

            Assert.False(Bind(proof, undockLf: 20.0,
                predecessorWindows: new List<RouteConnectionWindow> { OpenLoadWindow() },
                ownWindows: new List<RouteConnectionWindow> { ownWindow }));

            Assert.Equal(
                StartDockedOriginBindState.PairPendingBinding, proof.StartDockedOriginBindState);
            Assert.Contains(logLines, l => l.Contains("RouteOriginProof bind skipped:")
                && l.Contains("reason=SkipDeliveryWindow")
                && l.Contains("predecessorPickup=not-consulted"));
        }

        [Fact]
        public void Bind_AnInRecordingGainKeepsItsOwnSpelling()
        {
            // The predecessor is not consulted when this recording measured the rise itself:
            // pickup=Gain must stay Gain, which is what H57's literal pin reads.
            RouteOriginProof proof = PendingProof(transportStartLf: 20.0);

            Assert.True(Bind(proof, undockLf: 220.0,
                predecessorWindows: new List<RouteConnectionWindow> { OpenLoadWindow() }));

            Assert.Equal(OriginPickupKind.Gain, proof.StartDockedOriginPickupKind);
            Assert.True(proof.StartDockedOriginPickupValidated);
            Assert.Contains(logLines, l => l.Contains("RouteOriginProof bound at undock:")
                && l.Contains("pickup=Gain pickupValidated=1")
                && l.Contains("predecessorPickup=not-consulted"));
        }

        [Fact]
        public void Bind_APredecessorWindowNamingADifferentPartnerDoesNotValidate()
        {
            RouteOriginProof proof = PendingProof(transportStartLf: 20.0);

            Assert.True(Bind(proof, undockLf: 20.0,
                predecessorWindows: new List<RouteConnectionWindow>
                {
                    OpenLoadWindow(endpointRoot: 777u),
                }));

            Assert.Equal(OriginPickupKind.Carried, proof.StartDockedOriginPickupKind);
            Assert.False(proof.StartDockedOriginPickupValidated);
            Assert.Contains(logLines, l => l.Contains("RouteOriginProof bound at undock:")
                && l.Contains("predecessorPickup=PartnerRootMismatch"));
        }

        // ==============================================================
        // 5. THE PERSISTED PARTNER IDENTITY
        // ==============================================================

        [Fact]
        public void EndpointRootPartUId_SurvivesACodecRoundTripAndADeepClone()
        {
            // The key is only useful if it reaches the NEXT session: the whole point is that
            // the load happened before the recording that reads it.
            var recording = new Recording
            {
                RecordingId = "rec-with-window",
                RouteConnectionWindows = new List<RouteConnectionWindow> { OpenLoadWindow() },
            };
            var node = new ConfigNode("RECORDING");
            RouteProofCodec.SerializeRouteProofMetadata(node, recording);

            var reloaded = new Recording { RecordingId = "rec-with-window" };
            RouteProofCodec.DeserializeRouteProofMetadata(node, reloaded);

            Assert.NotNull(reloaded.RouteConnectionWindows);
            Assert.Single(reloaded.RouteConnectionWindows);
            Assert.Equal(DepotRootFlightId, reloaded.RouteConnectionWindows[0].EndpointRootPartUId);
            Assert.Equal(
                DepotRootFlightId,
                reloaded.RouteConnectionWindows[0].DeepClone().EndpointRootPartUId);

            // ... and the key is SPARSE: a window whose endpoint root could not be read writes
            // no key at all rather than a zero, because zero is the "unknown" reading the
            // partner match degrades on and a written 0 would be indistinguishable from a real
            // flightID of 0 to any future reader.
            var unknownRootRecording = new Recording
            {
                RecordingId = "rec-unknown-root",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    OpenLoadWindow(endpointRoot: 0u),
                },
            };
            var sparseNode = new ConfigNode("RECORDING");
            RouteProofCodec.SerializeRouteProofMetadata(sparseNode, unknownRootRecording);
            ConfigNode sparseWindow = sparseNode
                .GetNode("ROUTE_CONNECTION_WINDOWS")
                .GetNode("WINDOW");
            Assert.False(sparseWindow.HasValue("endpointRootPartUId"));

            var sparseReloaded = new Recording { RecordingId = "rec-unknown-root" };
            RouteProofCodec.DeserializeRouteProofMetadata(sparseNode, sparseReloaded);
            Assert.Equal(0u, sparseReloaded.RouteConnectionWindows[0].EndpointRootPartUId);
        }

        [Fact]
        public void RootPartFlightId_IsReadFromTheSnapshotsOwnRootIndex()
        {
            // The producer's source for EndpointRootPartUId: the PRE-COUPLE partner snapshot,
            // whose `root` index names the part whose `uid` is the launch-unique id. Reading
            // the FIRST part instead would be wrong on any craft whose root is not part 0.
            var vessel = new ConfigNode("VESSEL");
            vessel.AddValue("root", "1");
            foreach (uint uid in new uint[] { 9001u, 9002u })
            {
                ConfigNode part = vessel.AddNode("PART");
                part.AddValue("name", "tank");
                part.AddValue("uid", uid.ToString(CultureInfo.InvariantCulture));
            }
            Assert.True(VesselSpawner.TryReadRootPartFlightId(vessel, out uint flightId));
            Assert.Equal(9002u, flightId);

            // Fail-closed on every unreadable shape rather than returning a neighbour's id.
            var noRoot = new ConfigNode("VESSEL");
            noRoot.AddNode("PART").AddValue("uid", "9001");
            Assert.False(VesselSpawner.TryReadRootPartFlightId(noRoot, out _));

            var outOfRange = new ConfigNode("VESSEL");
            outOfRange.AddValue("root", "7");
            outOfRange.AddNode("PART").AddValue("uid", "9001");
            Assert.False(VesselSpawner.TryReadRootPartFlightId(outOfRange, out _));

            Assert.False(VesselSpawner.TryReadRootPartFlightId(null, out _));
        }

        [Fact]
        public void GainFromPredecessorWindow_IsAValidatingPickupKind()
        {
            Assert.True(RouteProofCapture.IsPickupValidated(
                OriginPickupKind.GainFromPredecessorWindow));
            Assert.True(RouteProofCapture.IsPickupValidated(OriginPickupKind.Gain));
            Assert.False(RouteProofCapture.IsPickupValidated(OriginPickupKind.Carried));
            Assert.False(RouteProofCapture.IsPickupValidated(OriginPickupKind.None));
            Assert.False(RouteProofCapture.IsPickupValidated(OriginPickupKind.NoUndockManifest));

            // Both validating spellings must open the SAME gate, or the evidence would
            // validate the pickup and then be refused by a gate that knows one spelling.
            Assert.Equal(
                RouteProofCapture.OriginBindGate.BindGain,
                RouteProofCapture.ClassifyOriginBindGate(
                    OriginPickupKind.GainFromPredecessorWindow, dockWitnessedInRecording: false));
        }

        [Fact]
        public void PickupKind_SurvivesTheProofCodecUnderItsOwnName()
        {
            var proof = PendingProof(20.0);
            proof.StartDockedOriginBindState = StartDockedOriginBindState.BoundAtUndock;
            proof.StartDockedOriginRootPartUId = DepotRootFlightId;
            proof.StartDockedOriginPickupKind = OriginPickupKind.GainFromPredecessorWindow;
            proof.StartDockedOriginPickupValidated = true;
            var recording = new Recording { RecordingId = "rec", RouteOriginProof = proof };

            var node = new ConfigNode("RECORDING");
            RouteProofCodec.SerializeRouteProofMetadata(node, recording);
            var reloaded = new Recording { RecordingId = "rec" };
            RouteProofCodec.DeserializeRouteProofMetadata(node, reloaded);

            Assert.NotNull(reloaded.RouteOriginProof);
            Assert.Equal(
                OriginPickupKind.GainFromPredecessorWindow,
                reloaded.RouteOriginProof.StartDockedOriginPickupKind);
            Assert.True(reloaded.RouteOriginProof.StartDockedOriginPickupValidated);
            Assert.True(Parsek.Logistics.RouteAnalysisEngine.HasDockedOriginProof(reloaded));
        }
    }
}
