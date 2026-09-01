using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// The self-provisioning DOCKING-PORT sibling of
    /// <see cref="GrappleCaptureInGameTest"/>, and the machinery that makes the
    /// roadmap's Tier B supply-route subjects (autotest-roadmap.md, "The
    /// supply-route coverage program", items 4-8) producible WITHOUT a manual
    /// flight.
    ///
    /// <para><b>Shape, shared by every cell.</b> Spawn a <c>dockingPort2</c>
    /// through the production spawn path and couple it into the ACTIVE vessel
    /// pre-recording (the claw cell's move, so the transport gains a real
    /// docking node without touching the committed craft). Spawn a PARTNER rig
    /// - a <c>dockingPort2</c> root plus, per cell, an LF tank and a cargo
    /// container, assembled by pre-recording couples so it is ONE vessel with
    /// ONE pid. Record. Couple the partner port into the transport port: both
    /// endpoints carry <c>ModuleDockingNode</c>, so
    /// <see cref="ConnectionProducerClassifier"/> stamps
    /// <see cref="RouteConnectionKind.DockingPort"/> on the REAL
    /// <c>onPartCouple</c> and a live <c>RouteConnectionWindow</c> is captured.
    /// Move cargo while docked. Undock through <c>Part.Undock</c>, which fires
    /// the real <c>onVesselsUndocking</c> split and completes the window.</para>
    ///
    /// <para><b>Why the classifier is satisfied without the docking FSM.</b>
    /// <c>ConnectionProducerClassifier.Classify</c> returns
    /// <c>DockingPort</c> iff BOTH event parts carry a
    /// <c>ModuleDockingNode</c>; it never consults the FSM state. So the
    /// residual here is the same one the claw cell records: the stock capture
    /// FSM (magnetic acquire, <c>ModuleDockingNode</c>'s own DOCKEDVESSEL
    /// bookkeeping and its synthetic joint) is NOT exercised - everything
    /// Parsek-side is.</para>
    ///
    /// <para><b>KSP fact that shaped the drift cell.</b> <c>Part.Couple</c>
    /// fires <c>onPartCouple</c> UNCONDITIONALLY and destroys the absorbed
    /// vessel (decompiled <c>Part.Couple</c>, KSP 1.12.5), so ANY second
    /// couple inside an open docked window opens a SECOND route window whose
    /// transport pid set already spans the partner - and the later undock then
    /// fails its disjoint-set verifier instead of completing. Real EVA
    /// construction does NOT go through <c>Part.Couple</c>: the attach path is
    /// <c>EVAConstructionModeEditor.AttachPart</c> -> <c>Part.OnAttachFlight(parent)</c>,
    /// which sets <c>parent</c> / <c>vessel</c> and adds the part to
    /// <c>vessel.Parts</c> with NO coupling event. The drift cell therefore
    /// drives <c>OnAttachFlight</c> - the production primitive - which is both
    /// the faithful subject and the only one that leaves the window
    /// completable.</para>
    ///
    /// <para><b>Second KSP fact, and it decides the round-trip assertion.</b>
    /// <c>Part.Undock</c> builds a brand-new <c>Vessel</c> component on the
    /// split half, whose <c>persistentId</c> starts at 0, so
    /// <c>Vessel.Initialize</c> stamps a FRESH
    /// <c>FlightGlobals.GetUniquepersistentId()</c> on it (decompiled, KSP
    /// 1.12.5). Re-docking the same physical partner therefore produces a
    /// window with a DIFFERENT <c>TransferTargetVesselPid</c>. The round-trip
    /// cell asserts sameness on the endpoint PART pids, which do survive the
    /// split, and logs both vessel pids so the first flight records the
    /// change.</para>
    ///
    /// <para><b>Isolation.</b> Isolated tier only, every cell: they spawn
    /// vessels, mutate the ACTIVE vessel's part tree and resources, and drive
    /// live recordings. Everything is torn down in <c>finally</c>; the
    /// per-test baseline quickload is the outer net.</para>
    /// </summary>
    public sealed class RouteDockCaptureInGameTest
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private const string DockPortPartName = "dockingPort2";
        private const string TankPartName = "fuelTank";
        private const string ContainerPartName = "smallCargoContainer";
        // Resource-less by design: a drift part carrying a RESOURCE node would
        // show up as an endpoint resource GAIN across the window and read as a
        // phantom delivery, which is the opposite of what B6 pins.
        private const string DriftPartName = "strutOcto";

        private const string TransferResourceName = "LiquidFuel";
        private const double RequestedTransferUnits = 20.0;

        // Along-track / along-parallel spawn offsets, in the claw cell's proven
        // 15 m spacing (its two fixtures sat at +30 m and +45 m and both
        // settled in 76 frames on the pad host). The transport's own docking
        // port sits closest; each partner part gets its own slot so no two
        // spawns overlap a hull.
        private const double TransportPortOffsetMeters = 20.0;
        private const double PartnerA1OffsetMeters = 35.0;
        private const double PartnerA2OffsetMeters = 50.0;
        private const double PartnerA3OffsetMeters = 65.0;
        private const double PartnerB1OffsetMeters = 80.0;
        private const double PartnerB2OffsetMeters = 95.0;
        private const double DriftPartOffsetMeters = 110.0;

        private const float SpawnLoadTimeoutSeconds = 30f;
        private const float RecordingStartTimeoutSeconds = 5f;
        private const float CoupleEventTimeoutSeconds = 15f;
        private const float UndockEventTimeoutSeconds = 15f;
        private const int SettleFrames = 3;

        private const string IsolatedOnlyBatchSkipReason =
            "Isolated-run only - spawns vessels, couples docking ports into the ACTIVE vessel, moves " +
            "resources and cargo between live parts, and drives a live recording. Use Run All + " +
            "Isolated or the row play button in a disposable FLIGHT session; the baseline quickload " +
            "restores the active vessel afterwards.";

        // ==================================================================
        // (a) B5 delivery half
        // ==================================================================

        [InGameTest(Category = "RouteDockCapture", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "Tier B item 5 (delivery half), self-provisioned: couples a spawned dockingPort2 " +
                "partner rig into a dockingPort2 attached to the active vessel during a live recording, " +
                "moves LiquidFuel transport->partner AND stores a stock cargo item transport->partner while " +
                "docked, then undocks. Asserts the live onPartCouple stamps kind=DockingPort, the route " +
                "window completes on the real onVesselsUndocking split, and BOTH delivery manifests (resource " +
                "+ inventory) carry what was moved")]
        public IEnumerator DockCapture_LiveRecordedDockingPortCouple_StampsDockingPortWindowWithDelivery()
        {
            var ctx = new CellContext("delivery");
            IEnumerator pre = ctx.Begin();
            while (pre.MoveNext()) yield return pre.Current;

            try
            {
                IEnumerator port = ctx.AttachTransportDockPort();
                while (port.MoveNext()) yield return port.Current;

                var rig = new PartnerRig("A");
                IEnumerator build = ctx.BuildPartnerRig(
                    rig, PartnerA1OffsetMeters, PartnerA2OffsetMeters, PartnerA3OffsetMeters,
                    withTank: true, withContainer: true);
                while (build.MoveNext()) yield return build.Current;

                // The partner tank spawns FULL from its prefab; drain the
                // delivered resource so the endpoint has free capacity for it.
                ctx.SetResourceAmount(rig.Tank, TransferResourceName, 0.0);

                IEnumerator rec = ctx.StartRecordingAndWait();
                while (rec.MoveNext()) yield return rec.Current;

                IEnumerator dock = ctx.CoupleAndAwaitWindow(rig);
                while (dock.MoveNext()) yield return dock.Current;

                double lfMoved = ctx.TransferResource(
                    ctx.TransportTank, rig.Tank, TransferResourceName, RequestedTransferUnits);
                InGameAssert.IsGreaterThan(lfMoved, 0.0,
                    "the transport must be able to move " + TransferResourceName +
                    " into the drained partner tank (source amount / destination capacity)");

                string storedHash = ctx.MoveOneStoredCargoItem(
                    fromEndpointSide: false, rig: rig, out string moveSkipReason);
                if (storedHash == null)
                    InGameAssert.Skip(moveSkipReason);

                yield return null;
                yield return null;

                IEnumerator undock = ctx.UndockAndAwaitCompletion(rig);
                while (undock.MoveNext()) yield return undock.Current;

                RouteConnectionWindow window = ctx.Windows[0];
                InGameAssert.AreEqual(RouteConnectionKind.DockingPort, window.TransferKind,
                    "the live port-to-port window must be stamped DockingPort");
                InGameAssert.IsTrue(window.IsComplete,
                    "the window must be complete after the undock split");

                Dictionary<string, double> deliveredRes =
                    RouteAnalysisEngine.BuildResourceDeliveryManifest(
                        window, ctx.WindowRecordingId, RouteAnalysisLogMode.Diagnostic);
                InGameAssert.IsTrue(
                    deliveredRes != null && deliveredRes.ContainsKey(TransferResourceName),
                    "the delivery manifest must carry " + TransferResourceName +
                    " (moved=" + lfMoved.ToString("F2", IC) + ")");
                InGameAssert.ApproxEqual(lfMoved, deliveredRes[TransferResourceName], 0.5,
                    "the delivered " + TransferResourceName +
                    " must match the amount actually moved across the window");

                List<InventoryPayloadItem> deliveredInv =
                    RouteAnalysisEngine.BuildInventoryDeliveryManifest(window);
                InGameAssert.IsTrue(
                    deliveredInv != null && ContainsHash(deliveredInv, storedHash),
                    "the inventory delivery manifest must carry the stored cargo item moved across " +
                    "the window (hash=" + storedHash + ")");

                ctx.RunAnalysisAndPass(
                    completeWindows: 1,
                    kind: window.TransferKind,
                    deliveryResources: deliveredRes.Count,
                    deliveryInventory: deliveredInv.Count,
                    pickupResources: 0,
                    pickupInventory: 0,
                    detail: "lfMoved=" + lfMoved.ToString("F2", IC) + ";item=" + storedHash);
            }
            finally
            {
                ctx.Teardown();
            }
        }

        // ==================================================================
        // (b) B5 pickup + mixed direction
        // ==================================================================

        [InGameTest(Category = "RouteDockCapture", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "Tier B item 5 (pickup + mixed half), self-provisioned: one docked window that " +
                "moves LiquidFuel partner->transport (pickup) AND a stock cargo item transport->partner " +
                "(delivery). Asserts BOTH the load manifest and the delivery manifest are populated off the " +
                "same completed window - the mixed-direction shape no committed fixture carries")]
        public IEnumerator DockCapture_PickupAndMixedDirection_ManifestsBothWays()
        {
            var ctx = new CellContext("mixed");
            IEnumerator pre = ctx.Begin();
            while (pre.MoveNext()) yield return pre.Current;

            try
            {
                IEnumerator port = ctx.AttachTransportDockPort();
                while (port.MoveNext()) yield return port.Current;

                var rig = new PartnerRig("A");
                IEnumerator build = ctx.BuildPartnerRig(
                    rig, PartnerA1OffsetMeters, PartnerA2OffsetMeters, PartnerA3OffsetMeters,
                    withTank: true, withContainer: true);
                while (build.MoveNext()) yield return build.Current;

                // Pickup direction: the partner keeps its full prefab tank and
                // the TRANSPORT supplies the free capacity (the committed rig
                // ships a partially filled tank, which is exactly the shape
                // build_logi_craft.py's L3 property guarantees).
                IEnumerator rec = ctx.StartRecordingAndWait();
                while (rec.MoveNext()) yield return rec.Current;

                IEnumerator dock = ctx.CoupleAndAwaitWindow(rig);
                while (dock.MoveNext()) yield return dock.Current;

                double lfPicked = ctx.TransferResource(
                    rig.Tank, ctx.TransportTank, TransferResourceName, RequestedTransferUnits);
                InGameAssert.IsGreaterThan(lfPicked, 0.0,
                    "the partner must be able to move " + TransferResourceName +
                    " onto the transport (the committed rig's tank ships partially filled)");

                string storedHash = ctx.MoveOneStoredCargoItem(
                    fromEndpointSide: false, rig: rig, out string moveSkipReason);
                if (storedHash == null)
                    InGameAssert.Skip(moveSkipReason);

                yield return null;
                yield return null;

                IEnumerator undock = ctx.UndockAndAwaitCompletion(rig);
                while (undock.MoveNext()) yield return undock.Current;

                RouteConnectionWindow window = ctx.Windows[0];
                Dictionary<string, double> loaded = RouteAnalysisEngine.BuildResourceLoadManifest(
                    window, ctx.WindowRecordingId, RouteAnalysisLogMode.Diagnostic);
                InGameAssert.IsTrue(
                    loaded != null && loaded.ContainsKey(TransferResourceName),
                    "the PICKUP (load) manifest must carry " + TransferResourceName);
                InGameAssert.ApproxEqual(lfPicked, loaded[TransferResourceName], 0.5,
                    "the picked-up " + TransferResourceName + " must match what was actually moved");

                List<InventoryPayloadItem> deliveredInv =
                    RouteAnalysisEngine.BuildInventoryDeliveryManifest(window);
                InGameAssert.IsTrue(
                    deliveredInv != null && ContainsHash(deliveredInv, storedHash),
                    "the DELIVERY manifest must carry the stored cargo item in the same window - " +
                    "that pairing IS the mixed-direction shape");

                ctx.RunAnalysisAndPass(
                    completeWindows: 1,
                    kind: window.TransferKind,
                    deliveryResources: 0,
                    deliveryInventory: deliveredInv.Count,
                    pickupResources: loaded.Count,
                    pickupInventory: 0,
                    detail: "mixed-direction;lfPicked=" + lfPicked.ToString("F2", IC)
                        + ";item=" + storedHash);
            }
            finally
            {
                ctx.Teardown();
            }
        }

        // ==================================================================
        // (c) B6 EVA-construction drift
        // ==================================================================

        [InGameTest(Category = "RouteDockCapture", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "Tier B item 6, self-provisioned: attaches a third part to the PARTNER's exterior " +
                "mid-window through Part.OnAttachFlight - the primitive stock EVA construction actually " +
                "uses, which fires no onPartCouple - then undocks. Asserts the 'Route window part-set drift " +
                "on undock' warning fires (its first firing outside unit tests), the window still completes, " +
                "and the moved part appears in NO manifest: the documented contract")]
        public IEnumerator DockCapture_EvaConstructionDrift_WarnsButRouteStillBuilds()
        {
            var ctx = new CellContext("drift");
            IEnumerator pre = ctx.Begin();
            while (pre.MoveNext()) yield return pre.Current;

            try
            {
                if (PrefabPart(DriftPartName) == null)
                    InGameAssert.Skip("Drift prefab '" + DriftPartName + "' not in PartLoader");

                IEnumerator port = ctx.AttachTransportDockPort();
                while (port.MoveNext()) yield return port.Current;

                var rig = new PartnerRig("A");
                IEnumerator build = ctx.BuildPartnerRig(
                    rig, PartnerA1OffsetMeters, PartnerA2OffsetMeters, PartnerA3OffsetMeters,
                    withTank: true, withContainer: false);
                while (build.MoveNext()) yield return build.Current;

                IEnumerator rec = ctx.StartRecordingAndWait();
                while (rec.MoveNext()) yield return rec.Current;

                IEnumerator dock = ctx.CoupleAndAwaitWindow(rig);
                while (dock.MoveNext()) yield return dock.Current;

                // The EVA-construction attach: a spawned part is re-homed onto
                // the PARTNER's subtree via the production primitive. No
                // onPartCouple fires, so no second route window opens and the
                // pending window stays completable - see the class remarks.
                IEnumerator attach = ctx.AttachDriftPartTo(rig.Root);
                while (attach.MoveNext()) yield return attach.Current;
                uint driftPid = ctx.DriftPartPid;
                if (driftPid == 0u)
                    InGameAssert.Skip("The EVA-construction drift part never became live; nothing to measure");

                int beforeUndock = ctx.CapturedCount;
                IEnumerator undock = ctx.UndockAndAwaitCompletion(rig);
                while (undock.MoveNext()) yield return undock.Current;

                RouteConnectionWindow window = ctx.Windows[0];
                bool driftWarn = ctx.FindFrom(beforeUndock,
                    l => l.Contains("Route window part-set drift on undock"));
                bool inWindowPidSets =
                    (window.TransportPartPersistentIds != null
                        && window.TransportPartPersistentIds.Contains(driftPid))
                    || (window.EndpointPartPersistentIds != null
                        && window.EndpointPartPersistentIds.Contains(driftPid));

                string verdict = RouteDockCaptureMath.ClassifyDriftObservation(
                    driftWarn, window.IsComplete, inWindowPidSets);

                InGameAssert.IsTrue(window.IsComplete,
                    "the route window must still complete despite the part-set drift " +
                    "(the disjoint verifier passes; the warning is observational)");
                InGameAssert.IsTrue(driftWarn,
                    "the undock must emit 'Route window part-set drift on undock' - the warning path " +
                    "this cell exists to fire for the first time outside unit tests");
                InGameAssert.IsFalse(inWindowPidSets,
                    "the EVA-constructed part must appear in NEITHER recorded part-pid set, so it can " +
                    "carry no manifest term (pid=" + driftPid.ToString(IC) + ")");
                InGameAssert.AreEqual(
                    "drift-warned-window-complete-part-unmanifested", verdict,
                    "the documented drift contract did not hold end to end");

                ctx.RunAnalysisAndPass(
                    completeWindows: 1,
                    kind: window.TransferKind,
                    deliveryResources: 0,
                    deliveryInventory: 0,
                    pickupResources: 0,
                    pickupInventory: 0,
                    detail: verdict + ";driftPid=" + driftPid.ToString(IC));
            }
            finally
            {
                ctx.Teardown();
            }
        }

        // ==================================================================
        // (d) B7 multi-stop
        // ==================================================================

        [InGameTest(Category = "RouteDockCapture", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "Tier B item 7, self-provisioned: two spawned partner rigs docked, delivered to and " +
                "undocked from IN TURN inside one recording tree. Asserts two COMPLETE windows ordered by " +
                "DockUT (they live on different dock-merged children of the same tree, which is what " +
                "AnalyzeTree's M4a collection walks) and pins the status + stop count the engine returns")]
        public IEnumerator DockCapture_TwoPartnersSequential_TwoWindowsOneRecording()
        {
            var ctx = new CellContext("multi-stop");
            IEnumerator pre = ctx.Begin();
            while (pre.MoveNext()) yield return pre.Current;

            try
            {
                IEnumerator port = ctx.AttachTransportDockPort();
                while (port.MoveNext()) yield return port.Current;

                var rigA = new PartnerRig("A");
                IEnumerator buildA = ctx.BuildPartnerRig(
                    rigA, PartnerA1OffsetMeters, PartnerA2OffsetMeters, PartnerA3OffsetMeters,
                    withTank: true, withContainer: false);
                while (buildA.MoveNext()) yield return buildA.Current;
                ctx.SetResourceAmount(rigA.Tank, TransferResourceName, 0.0);

                var rigB = new PartnerRig("B");
                IEnumerator buildB = ctx.BuildPartnerRig(
                    rigB, PartnerB1OffsetMeters, PartnerB2OffsetMeters, DriftPartOffsetMeters,
                    withTank: true, withContainer: false);
                while (buildB.MoveNext()) yield return buildB.Current;
                ctx.SetResourceAmount(rigB.Tank, TransferResourceName, 0.0);

                IEnumerator rec = ctx.StartRecordingAndWait();
                while (rec.MoveNext()) yield return rec.Current;

                IEnumerator dockA = ctx.CoupleAndAwaitWindow(rigA);
                while (dockA.MoveNext()) yield return dockA.Current;
                double movedA = ctx.TransferResource(
                    ctx.TransportTank, rigA.Tank, TransferResourceName, RequestedTransferUnits);
                yield return null;
                IEnumerator undockA = ctx.UndockAndAwaitCompletion(rigA);
                while (undockA.MoveNext()) yield return undockA.Current;

                IEnumerator dockB = ctx.CoupleAndAwaitWindow(rigB);
                while (dockB.MoveNext()) yield return dockB.Current;
                double movedB = ctx.TransferResource(
                    ctx.TransportTank, rigB.Tank, TransferResourceName, RequestedTransferUnits);
                yield return null;
                IEnumerator undockB = ctx.UndockAndAwaitCompletion(rigB);
                while (undockB.MoveNext()) yield return undockB.Current;

                InGameAssert.AreEqual(2, ctx.Windows.Count,
                    "the run must carry exactly two route windows, one per partner");
                InGameAssert.IsTrue(ctx.Windows[0].IsComplete && ctx.Windows[1].IsComplete,
                    "both windows must be complete after their own undock");
                InGameAssert.IsTrue(ctx.Windows[1].DockUT > ctx.Windows[0].DockUT,
                    "the second stop's DockUT must strictly follow the first's - AnalyzeTree rejects " +
                    "MultipleConnectionWindows on a duplicate or non-finite DockUT");
                InGameAssert.AreNotEqual(
                    ctx.Windows[0].TransferTargetVesselPid, ctx.Windows[1].TransferTargetVesselPid,
                    "the two stops must name different endpoint vessels");
                InGameAssert.IsGreaterThan(movedA, 0.0, "stop A must have moved cargo");
                InGameAssert.IsGreaterThan(movedB, 0.0, "stop B must have moved cargo");

                ctx.RunAnalysisAndPass(
                    completeWindows: 2,
                    kind: ctx.Windows[0].TransferKind,
                    deliveryResources: ManifestCount(RouteAnalysisEngine.BuildResourceDeliveryManifest(
                        ctx.Windows[0], ctx.WindowRecordingId, RouteAnalysisLogMode.Diagnostic))
                        + ManifestCount(RouteAnalysisEngine.BuildResourceDeliveryManifest(
                        ctx.Windows[1], ctx.WindowRecordingId, RouteAnalysisLogMode.Diagnostic)),
                    deliveryInventory: 0,
                    pickupResources: 0,
                    pickupInventory: 0,
                    detail: "movedA=" + movedA.ToString("F2", IC)
                        + ";movedB=" + movedB.ToString("F2", IC));
            }
            finally
            {
                ctx.Teardown();
            }
        }

        // ==================================================================
        // (e) B8 round-trip pair
        // ==================================================================

        [InGameTest(Category = "RouteDockCapture", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "Tier B item 8, self-provisioned: the SAME partner docked twice in one recording - " +
                "deliver on the first window, pick up on the second. Asserts two complete windows naming the " +
                "same PHYSICAL partner (on the endpoint PART pids - Part.Undock stamps a fresh vessel " +
                "persistentId on the split half, so the vessel pid legitimately differs between legs) with " +
                "OPPOSITE manifest directions (delivery then load), the round-trip shape")]
        public IEnumerator DockCapture_RoundTripPair_SamePartnerTwice()
        {
            var ctx = new CellContext("round-trip");
            IEnumerator pre = ctx.Begin();
            while (pre.MoveNext()) yield return pre.Current;

            try
            {
                IEnumerator port = ctx.AttachTransportDockPort();
                while (port.MoveNext()) yield return port.Current;

                var rig = new PartnerRig("A");
                IEnumerator build = ctx.BuildPartnerRig(
                    rig, PartnerA1OffsetMeters, PartnerA2OffsetMeters, PartnerA3OffsetMeters,
                    withTank: true, withContainer: false);
                while (build.MoveNext()) yield return build.Current;
                // Half-empty so BOTH directions have somewhere to go.
                ctx.SetResourceAmountToHalf(rig.Tank, TransferResourceName);
                // PHYSICAL identity is the PART pid, not the vessel pid.
                // `Part.Undock` builds a brand-new Vessel component whose
                // persistentId defaults to 0, so `Vessel.Initialize` stamps a
                // FRESH `FlightGlobals.GetUniquepersistentId()` on the split
                // half (decompiled `Part.Undock` / `Vessel.Initialize`, KSP
                // 1.12.5). The second leg of a round trip therefore names a
                // DIFFERENT `TransferTargetVesselPid` than the first even
                // though it is the same physical craft - the part pids are
                // what survive the split.
                uint partnerRootPartPid = rig.Root.persistentId;

                IEnumerator rec = ctx.StartRecordingAndWait();
                while (rec.MoveNext()) yield return rec.Current;

                IEnumerator dock1 = ctx.CoupleAndAwaitWindow(rig);
                while (dock1.MoveNext()) yield return dock1.Current;
                double delivered = ctx.TransferResource(
                    ctx.TransportTank, rig.Tank, TransferResourceName, RequestedTransferUnits);
                yield return null;
                IEnumerator undock1 = ctx.UndockAndAwaitCompletion(rig);
                while (undock1.MoveNext()) yield return undock1.Current;

                IEnumerator dock2 = ctx.CoupleAndAwaitWindow(rig);
                while (dock2.MoveNext()) yield return dock2.Current;
                double pickedUp = ctx.TransferResource(
                    rig.Tank, ctx.TransportTank, TransferResourceName, RequestedTransferUnits);
                yield return null;
                IEnumerator undock2 = ctx.UndockAndAwaitCompletion(rig);
                while (undock2.MoveNext()) yield return undock2.Current;

                InGameAssert.AreEqual(2, ctx.Windows.Count,
                    "the round trip must produce exactly two route windows");
                InGameAssert.IsTrue(
                    ctx.Windows[0].EndpointPartPersistentIds != null
                        && ctx.Windows[0].EndpointPartPersistentIds.Contains(partnerRootPartPid)
                        && ctx.Windows[1].EndpointPartPersistentIds != null
                        && ctx.Windows[1].EndpointPartPersistentIds.Contains(partnerRootPartPid),
                    "both legs must name the same PHYSICAL partner - asserted on the endpoint PART " +
                    "pid (" + partnerRootPartPid.ToString(IC) + "), which survives the undock split, " +
                    "NOT on TransferTargetVesselPid, which does not");
                InGameAssert.IsGreaterThan(delivered, 0.0, "the outbound leg must deliver");
                InGameAssert.IsGreaterThan(pickedUp, 0.0, "the return leg must pick up");

                Dictionary<string, double> outboundDelivery =
                    RouteAnalysisEngine.BuildResourceDeliveryManifest(
                        ctx.Windows[0], ctx.WindowRecordingId, RouteAnalysisLogMode.Diagnostic);
                Dictionary<string, double> returnLoad =
                    RouteAnalysisEngine.BuildResourceLoadManifest(
                        ctx.Windows[1], ctx.WindowRecordingId, RouteAnalysisLogMode.Diagnostic);
                InGameAssert.IsTrue(
                    outboundDelivery != null && outboundDelivery.ContainsKey(TransferResourceName),
                    "the FIRST window must carry a delivery manifest");
                InGameAssert.IsTrue(
                    returnLoad != null && returnLoad.ContainsKey(TransferResourceName),
                    "the SECOND window must carry a load (pickup) manifest - the opposite direction");

                ctx.RunAnalysisAndPass(
                    completeWindows: 2,
                    kind: ctx.Windows[0].TransferKind,
                    deliveryResources: outboundDelivery.Count,
                    deliveryInventory: 0,
                    pickupResources: returnLoad.Count,
                    pickupInventory: 0,
                    detail: "delivered=" + delivered.ToString("F2", IC)
                        + ";pickedUp=" + pickedUp.ToString("F2", IC)
                        + ";partnerPartPid=" + partnerRootPartPid.ToString(IC)
                        + ";leg1TargetPid=" + ctx.Windows[0].TransferTargetVesselPid.ToString(IC)
                        + ";leg2TargetPid=" + ctx.Windows[1].TransferTargetVesselPid.ToString(IC));
            }
            finally
            {
                ctx.Teardown();
            }
        }

        // ==================================================================
        // The origin-proof PROBE (an instrument, not a gate)
        // ==================================================================

        [InGameTest(Category = "RouteDockCapture", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "INSTRUMENT for ROUTE-ORIGIN-PROOF-PRODUCER-UNREACHABLE (todo, SUSPECTED): couples a " +
                "spawned dockingPort2 partner into the active vessel, counts the parts satisfying the " +
                "producer's own predicate (p.parent.vessel != v), then starts a recording on the docked " +
                "vessel and reads back whether a RouteOriginProof was captured. Logs " +
                "'OriginProofProbe: externalParentParts=N proofCaptured=<bool> ...' and asserts NO verdict - " +
                "it PASSES whenever it measured. That measurement is what decides whether roadmap Tier B " +
                "item 4 is a flight or a bug fix")]
        public IEnumerator OriginProofProbe_SettledDockLeavesNoExternalParent()
        {
            var ctx = new CellContext("origin-probe");
            IEnumerator pre = ctx.Begin();
            while (pre.MoveNext()) yield return pre.Current;

            try
            {
                IEnumerator port = ctx.AttachTransportDockPort();
                while (port.MoveNext()) yield return port.Current;

                var rig = new PartnerRig("A");
                IEnumerator build = ctx.BuildPartnerRig(
                    rig, PartnerA1OffsetMeters, PartnerA2OffsetMeters, PartnerA3OffsetMeters,
                    withTank: false, withContainer: false);
                while (build.MoveNext()) yield return build.Current;

                // The dock itself, OUTSIDE any recording: this probe is about
                // what the world looks like at the NEXT recording start, which
                // is exactly when the producer runs.
                Part partnerRoot = rig.Root;
                uint partnerPid = rig.Vessel != null ? rig.Vessel.persistentId : 0u;
                partnerRoot.Couple(ctx.TransportPort);
                for (int i = 0; i < SettleFrames; i++)
                    yield return null;

                Vessel v = FlightGlobals.ActiveVessel;
                InGameAssert.IsNotNull(v, "the active vessel disappeared during the probe couple");
                InGameAssert.AreEqual(v, partnerRoot.vessel,
                    "the partner must belong to the active vessel after Part.Couple");

                int externalParentParts = 0;
                for (int i = 0; i < v.parts.Count; i++)
                {
                    Part p = v.parts[i];
                    if (p == null) continue;
                    Part parent = p.parent;
                    bool parentVesselResolves = parent != null && parent.vessel != null;
                    bool parentIsSelf = parentVesselResolves && parent.vessel == v;
                    if (RouteDockCaptureMath.IsExternallyParentedPart(
                            parent != null, parentVesselResolves, parentIsSelf))
                    {
                        externalParentParts++;
                    }
                }

                // Read-back half: run the PRODUCTION producer by starting a
                // recording on the docked vessel and reading its own log
                // branch off the observer channel.
                int beforeStart = ctx.CapturedCount;
                IEnumerator rec = ctx.StartRecordingAndWait();
                while (rec.MoveNext()) yield return rec.Current;

                bool proofCaptured = ctx.FindFrom(beforeStart,
                    l => l.Contains("RouteOriginProof captured:"));
                string outcome = ctx.FirstMatchSuffix(beforeStart, "RouteOriginProof skipped: ")
                    ?? (proofCaptured ? "captured" : "no-producer-line");

                string line = RouteDockCaptureMath.FormatOriginProofProbeLine(
                    externalParentParts, proofCaptured, (int)v.situation, outcome, partnerPid);
                ParsekLog.Info("TestRunner", line);

                // NO VERDICT. The cell passes because it measured; the numbers
                // in the line above are the product.
                InGameAssert.IsTrue(ctx.Flight.IsRecording,
                    "the probe must have actually started a recording, or it measured nothing");
                InGameAssert.IsTrue(externalParentParts >= 0, "counter sanity");
            }
            finally
            {
                ctx.Teardown();
            }
        }

        // ==================================================================
        // Shared per-cell context
        // ==================================================================

        /// <summary>
        /// One cell's world: the captured log channel, the spawned fixtures,
        /// the transport-side docking port, and the teardown ledger. Every
        /// method that yields returns an <see cref="IEnumerator"/> the cell
        /// pumps, because a cell body cannot <c>yield</c> through a helper.
        /// </summary>
        private sealed class CellContext
        {
            internal readonly string CellName;
            internal readonly string RunId;
            internal ParsekFlight Flight;
            internal Vessel ActiveVessel;
            internal Part TransportPort;
            internal Part TransportTank;
            internal uint DriftPartPid;
            internal readonly List<RouteConnectionWindow> Windows = new List<RouteConnectionWindow>();
            internal string WindowRecordingId;

            private readonly List<string> captured = new List<string>();
            private Action<string> priorObserver;
            private readonly List<Vessel> spawnedVessels = new List<Vessel>();
            private readonly List<Part> attachedRoots = new List<Part>();
            private RecordingTree tree;

            internal CellContext(string cellName)
            {
                CellName = cellName;
                RunId = "routedock-" + cellName + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            }

            internal int CapturedCount => captured.Count;

            internal bool FindFrom(int fromIndex, Predicate<string> predicate)
            {
                for (int i = fromIndex; i < captured.Count; i++)
                {
                    if (predicate(captured[i]))
                        return true;
                }
                return false;
            }

            /// <summary>First captured line containing <paramref name="marker"/>,
            /// returned as the first whitespace-delimited token after it (the
            /// producer's skip branches all read "skipped: &lt;reason words&gt;
            /// recId=...", so the first three words identify the branch).</summary>
            internal string FirstMatchSuffix(int fromIndex, string marker)
            {
                for (int i = fromIndex; i < captured.Count; i++)
                {
                    int at = captured[i].IndexOf(marker, StringComparison.Ordinal);
                    if (at < 0) continue;
                    string tail = captured[i].Substring(at + marker.Length);
                    int recAt = tail.IndexOf(" recId=", StringComparison.Ordinal);
                    if (recAt > 0) tail = tail.Substring(0, recAt);
                    return tail.Trim().Replace(' ', '-');
                }
                return null;
            }

            // ----------------------------------------------------------
            // Preconditions + observer arming
            // ----------------------------------------------------------

            internal IEnumerator Begin()
            {
                IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
                while (unpackWait.MoveNext())
                    yield return unpackWait.Current;

                Flight = ParsekFlight.Instance;
                if (Flight == null)
                    InGameAssert.Skip("ParsekFlight.Instance is null; FLIGHT scene controller required");
                ActiveVessel = FlightGlobals.ActiveVessel;
                if (ActiveVessel == null)
                    InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live vessel");
                if (!(ActiveVessel.loaded && !ActiveVessel.packed))
                    InGameAssert.Skip(
                        "Active vessel '" + ActiveVessel.vesselName + "' is not loaded+unpacked " +
                        "(loaded=" + ActiveVessel.loaded + ", packed=" + ActiveVessel.packed + ")");
                if (ActiveVessel.isEVA)
                    InGameAssert.Skip(
                        "Active vessel is an EVA kerbal; a couple involving it would be EVA-suppressed " +
                        "by design - run from a normal vessel");
                if ((ActiveVessel.Landed || ActiveVessel.Splashed)
                    && Math.Abs(ActiveVessel.latitude) > 85.0)
                {
                    InGameAssert.Skip(
                        "Active vessel is landed/splashed within 5 degrees of a pole; the longitude-offset " +
                        "surface spawn math is unreliable there");
                }
                if (PrefabPart(DockPortPartName) == null)
                    InGameAssert.Skip("Docking prefab '" + DockPortPartName + "' not in PartLoader");
                if (PrefabPart(TankPartName) == null)
                    InGameAssert.Skip("Tank prefab '" + TankPartName + "' not in PartLoader");
                if (PrefabPart(ContainerPartName) == null)
                    InGameAssert.Skip("Container prefab '" + ContainerPartName + "' not in PartLoader");

                // The batch host auto-records; stop / discard the ephemeral
                // session so the cell owns the recorder (the claw cell's
                // ALL-TESTS-AUTO self-setup, verbatim in effect).
                if (Flight.IsRecording || Flight.ActiveTreeForSerialization != null)
                {
                    if (!Flight.HasActiveTree)
                    {
                        if (Flight.IsRecording)
                            Flight.StopRecording();
                    }
                    else
                    {
                        System.Reflection.MethodInfo discard = typeof(ParsekFlight).GetMethod(
                            "DiscardActiveTreeForSuppressedSceneExit",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        if (discard == null)
                            InGameAssert.Skip(
                                "ParsekFlight.DiscardActiveTreeForSuppressedSceneExit reflection surface " +
                                "unavailable");
                        try
                        {
                            discard.Invoke(Flight, new object[]
                            {
                                HighLogic.LoadedScene,
                                Planetarium.GetUniversalTime(),
                                "RouteDockCapture gate setup: discard the ephemeral auto-record session tree",
                                false
                            });
                        }
                        catch (System.Reflection.TargetInvocationException ex)
                        {
                            InGameAssert.Fail(
                                "setup: DiscardActiveTreeForSuppressedSceneExit threw " +
                                (ex.InnerException != null ? ex.InnerException.GetType().Name : ex.GetType().Name)
                                + ": " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                        }
                    }
                    InGameAssert.IsFalse(Flight.IsRecording,
                        "setup: the session recording must be stopped before the cell starts its own");
                    InGameAssert.IsTrue(Flight.ActiveTreeForSerialization == null,
                        "setup: the session tree must be discarded before the cell creates its own");
                }

                TransportTank = FindResourceBearingPart(ActiveVessel, TransferResourceName);
                if (TransportTank == null)
                {
                    InGameAssert.Skip(
                        "Active vessel carries no part with a " + TransferResourceName +
                        " resource; this category needs a transport that can move cargo");
                }

                priorObserver = ParsekLog.TestObserverForTesting;
                Action<string> prior = priorObserver;
                ParsekLog.TestObserverForTesting = line =>
                {
                    captured.Add(line);
                    if (prior != null) prior(line);
                };
                ParsekLog.Info("TestRunner",
                    "RouteDockCapture setup: cell=" + CellName + " run=" + RunId +
                    " vessel='" + ActiveVessel.vesselName + "' parts=" + ActiveVessel.parts.Count.ToString(IC));
            }

            // ----------------------------------------------------------
            // Fixture provisioning
            // ----------------------------------------------------------

            internal IEnumerator AttachTransportDockPort()
            {
                uint pid = SpawnSinglePartVessel(
                    DockPortPartName, RunId + "-tport", VesselType.Probe,
                    ActiveVessel, TransportPortOffsetMeters);
                if (pid == 0)
                    InGameAssert.Skip("Transport docking-port spawn failed (SpawnAtPosition returned 0)");

                Vessel spawned = null;
                IEnumerator wait = WaitForSpawnedVesselLive(pid, "transport-port", v => spawned = v);
                while (wait.MoveNext()) yield return wait.Current;
                if (spawned == null)
                {
                    InGameAssert.Skip(
                        "Spawned transport docking port pid=" + pid.ToString(IC) +
                        " never became loaded+unpacked within " +
                        SpawnLoadTimeoutSeconds.ToString("R", IC) + "s");
                }
                spawnedVessels.Add(spawned);

                Part port = spawned.rootPart;
                InGameAssert.IsNotNull(port, "spawned docking-port vessel must have a root part");
                InGameAssert.IsNotNull(port.FindModuleImplementing<ModuleDockingNode>(),
                    "the spawned transport port must carry ModuleDockingNode (live part, not prefab)");

                port.Couple(ActiveVessel.rootPart);
                for (int i = 0; i < SettleFrames; i++)
                    yield return null;
                InGameAssert.AreEqual(ActiveVessel, port.vessel,
                    "the transport docking port must belong to the active vessel after Part.Couple");
                TransportPort = port;
                attachedRoots.Add(port);
            }

            internal IEnumerator BuildPartnerRig(
                PartnerRig rig, double rootOffset, double tankOffset, double containerOffset,
                bool withTank, bool withContainer)
            {
                uint rootPid = SpawnSinglePartVessel(
                    DockPortPartName, RunId + "-p" + rig.Label + "-port", VesselType.Probe,
                    ActiveVessel, rootOffset);
                if (rootPid == 0)
                    InGameAssert.Skip("Partner " + rig.Label + " port spawn failed");
                Vessel partnerVessel = null;
                IEnumerator wait = WaitForSpawnedVesselLive(
                    rootPid, "partner-" + rig.Label + "-port", v => partnerVessel = v);
                while (wait.MoveNext()) yield return wait.Current;
                if (partnerVessel == null)
                    InGameAssert.Skip("Partner " + rig.Label + " port never became loaded+unpacked");
                spawnedVessels.Add(partnerVessel);
                rig.Vessel = partnerVessel;
                rig.Root = partnerVessel.rootPart;
                attachedRoots.Add(rig.Root);
                InGameAssert.IsNotNull(rig.Root.FindModuleImplementing<ModuleDockingNode>(),
                    "the partner root must carry ModuleDockingNode");

                if (withTank)
                {
                    IEnumerator addTank = AddPartToRig(
                        rig, TankPartName, "tank", tankOffset, p => rig.Tank = p);
                    while (addTank.MoveNext()) yield return addTank.Current;
                }
                if (withContainer)
                {
                    IEnumerator addBox = AddPartToRig(
                        rig, ContainerPartName, "box", containerOffset, p => rig.Container = p);
                    while (addBox.MoveNext()) yield return addBox.Current;
                }

                ParsekLog.Verbose("TestRunner",
                    "RouteDockCapture partner rig " + rig.Label + " built: pid=" +
                    rig.Vessel.persistentId.ToString(IC) + " parts=" + rig.Vessel.parts.Count.ToString(IC));
            }

            /// <summary>Pre-recording couple of one extra part onto the partner
            /// root. Pre-recording by construction, so no route window opens.</summary>
            private IEnumerator AddPartToRig(
                PartnerRig rig, string partName, string label, double offset, Action<Part> assign)
            {
                uint pid = SpawnSinglePartVessel(
                    partName, RunId + "-p" + rig.Label + "-" + label, VesselType.Probe,
                    ActiveVessel, offset);
                if (pid == 0)
                    InGameAssert.Skip("Partner " + rig.Label + " " + label + " spawn failed");
                Vessel spawned = null;
                IEnumerator wait = WaitForSpawnedVesselLive(
                    pid, "partner-" + rig.Label + "-" + label, v => spawned = v);
                while (wait.MoveNext()) yield return wait.Current;
                if (spawned == null)
                    InGameAssert.Skip("Partner " + rig.Label + " " + label + " never became live");
                spawnedVessels.Add(spawned);

                Part part = spawned.rootPart;
                part.Couple(rig.Root);
                for (int i = 0; i < SettleFrames; i++)
                    yield return null;
                InGameAssert.AreEqual(rig.Vessel, part.vessel,
                    "the partner " + label + " must join the partner vessel after Part.Couple");
                assign(part);
            }

            /// <summary>
            /// The EVA-construction attach: <c>Part.OnAttachFlight</c> on a
            /// spawned part, mirroring
            /// <c>EVAConstructionModeEditor.AttachPart</c>. The donor vessel is
            /// torn down the way <c>Part.Couple</c> tears its own down, minus
            /// the coupling event that would open a second route window.
            /// </summary>
            internal IEnumerator AttachDriftPartTo(Part parentPart)
            {
                DriftPartPid = 0u;
                uint pid = SpawnSinglePartVessel(
                    DriftPartName, RunId + "-drift", VesselType.Probe,
                    ActiveVessel, DriftPartOffsetMeters);
                if (pid == 0)
                    InGameAssert.Skip("Drift part spawn failed (SpawnAtPosition returned 0)");
                Vessel spawned = null;
                IEnumerator wait = WaitForSpawnedVesselLive(pid, "drift", v => spawned = v);
                while (wait.MoveNext()) yield return wait.Current;
                if (spawned == null)
                    InGameAssert.Skip("Drift part never became loaded+unpacked");

                Part part = spawned.rootPart;
                uint driftPid = part.persistentId;
                Vessel donor = spawned;

                donor.parts.Remove(part);
                if (donor.Parts != null)
                    donor.Parts.Remove(part);
                part.OnAttachFlight(parentPart);
                try
                {
                    donor.OnJustAboutToBeDestroyed();
                }
                catch (Exception ex)
                {
                    ParsekLog.Verbose("TestRunner",
                        "RouteDockCapture drift: donor OnJustAboutToBeDestroyed threw " +
                        ex.GetType().Name + " (ignored; the vessel is being destroyed)");
                }
                UnityEngine.Object.DestroyImmediate(donor);
                spawnedVessels.Remove(spawned);

                for (int i = 0; i < SettleFrames; i++)
                    yield return null;

                DriftPartPid = driftPid;
                ParsekLog.Info("TestRunner",
                    "RouteDockCapture drift attach: part=" + DriftPartName +
                    " pid=" + driftPid.ToString(IC) +
                    " parent=" + (parentPart != null ? parentPart.partInfo.name : "<null>") +
                    " via=Part.OnAttachFlight (no onPartCouple by design)");
            }

            // ----------------------------------------------------------
            // Recording / dock / undock
            // ----------------------------------------------------------

            internal IEnumerator StartRecordingAndWait()
            {
                Flight.StartRecording(suppressStartScreenMessage: true);
                IEnumerator wait = WaitUntil(() => Flight.IsRecording,
                    RecordingStartTimeoutSeconds, "recording start");
                while (wait.MoveNext()) yield return wait.Current;
                InGameAssert.IsTrue(Flight.IsRecording,
                    "StartRecording did not start within " +
                    RecordingStartTimeoutSeconds.ToString("R", IC) + "s");
                tree = Flight.ActiveTreeForSerialization;
                InGameAssert.IsNotNull(tree, "Active tree should exist while recording");
            }

            internal IEnumerator CoupleAndAwaitWindow(PartnerRig rig)
            {
                InGameAssert.IsNotNull(TransportPort, "the transport docking port must be attached first");
                IEnumerator ready = WaitUntil(() => Flight.IsRecording,
                    RecordingStartTimeoutSeconds, "recorder ready before couple");
                while (ready.MoveNext()) yield return ready.Current;
                InGameAssert.IsTrue(Flight.IsRecording,
                    "the recorder must be recording before the dock");

                uint partnerPid = rig.Vessel != null ? rig.Vessel.persistentId : 0u;
                int before = captured.Count;
                rig.Root.Couple(TransportPort);
                IEnumerator wait = WaitUntil(
                    () => FindFrom(before, IsWindowCapturedLine),
                    CoupleEventTimeoutSeconds, "route window capture after dock");
                while (wait.MoveNext()) yield return wait.Current;

                InGameAssert.IsTrue(
                    FindFrom(before, l =>
                        IsProducerClassifiedLine(l) && l.Contains("kind=DockingPort")
                        && l.Contains("fromPart=" + DockPortPartName)
                        && l.Contains("toPart=" + DockPortPartName)
                        && l.Contains("involvesEva=False")),
                    "the port-to-port couple must classify kind=DockingPort on the real onPartCouple " +
                    "(ModuleDockingNode on BOTH endpoints)");
                InGameAssert.IsTrue(
                    FindFrom(before, l => IsWindowCapturedLine(l) && l.Contains("kind=DockingPort")),
                    "the captured route window must log kind=DockingPort (timeout " +
                    CoupleEventTimeoutSeconds.ToString("R", IC) + "s)");

                string childId = tree.ActiveRecordingId;
                Recording child;
                InGameAssert.IsTrue(
                    !string.IsNullOrEmpty(childId)
                        && tree.Recordings.TryGetValue(childId, out child) && child != null,
                    "dock-merged child recording '" + (childId ?? "<null>") + "' not found in the tree");
                tree.Recordings.TryGetValue(childId, out child);
                InGameAssert.IsTrue(
                    child.RouteConnectionWindows != null && child.RouteConnectionWindows.Count == 1,
                    "the dock-merged child must carry exactly one route connection window, got " +
                    (child.RouteConnectionWindows != null
                        ? child.RouteConnectionWindows.Count.ToString(IC) : "0"));

                RouteConnectionWindow window = child.RouteConnectionWindows[0];
                InGameAssert.AreEqual(partnerPid, window.TransferTargetVesselPid,
                    "the window endpoint must be the docked partner vessel's pre-couple pid");
                InGameAssert.IsFalse(double.IsNaN(window.DockUT), "the window must carry a DockUT");
                InGameAssert.IsFalse(window.IsComplete, "the window must be incomplete before the undock");
                Windows.Add(window);
                WindowRecordingId = childId;
                rig.LastWindow = window;
            }

            internal IEnumerator UndockAndAwaitCompletion(PartnerRig rig)
            {
                IEnumerator ready = WaitUntil(() => Flight.IsRecording,
                    RecordingStartTimeoutSeconds, "recorder restart on dock-merged child");
                while (ready.MoveNext()) yield return ready.Current;

                int before = captured.Count;
                var info = new DockedVesselInfo
                {
                    name = RunId + "-p" + rig.Label + "-undocked",
                    vesselType = VesselType.Probe,
                    rootPartUId = rig.Root.flightID
                };
                rig.Root.Undock(info);
                IEnumerator wait = WaitUntil(
                    () => FindFrom(before, IsWindowCompletedLine),
                    UndockEventTimeoutSeconds, "route window completion after undock");
                while (wait.MoveNext()) yield return wait.Current;

                InGameAssert.IsTrue(
                    FindFrom(before, IsWindowCompletedLine),
                    "the undock must complete the route window through the real onVesselsUndocking split " +
                    "(timeout " + UndockEventTimeoutSeconds.ToString("R", IC) + "s)");
                InGameAssert.IsTrue(rig.LastWindow.IsComplete,
                    "the window must be complete after the undock");
                InGameAssert.IsTrue(rig.LastWindow.UndockUT >= rig.LastWindow.DockUT,
                    "the undock UT must not precede the dock UT");
                if (rig.Vessel == null || rig.Vessel.state == Vessel.State.DEAD)
                    rig.Vessel = rig.Root.vessel;
            }

            // ----------------------------------------------------------
            // Cargo mutation
            // ----------------------------------------------------------

            internal void SetResourceAmount(Part part, string resourceName, double amount)
            {
                PartResource res = FindResource(part, resourceName);
                if (res == null) return;
                res.amount = amount;
                ParsekLog.Verbose("TestRunner",
                    "RouteDockCapture resource set: part=" + part.partInfo.name +
                    " pid=" + part.persistentId.ToString(IC) + " " + resourceName +
                    "=" + amount.ToString("F2", IC) + "/" + res.maxAmount.ToString("F2", IC));
            }

            internal void SetResourceAmountToHalf(Part part, string resourceName)
            {
                PartResource res = FindResource(part, resourceName);
                if (res == null) return;
                SetResourceAmount(part, resourceName, res.maxAmount * 0.5);
            }

            /// <summary>Moves as much of <paramref name="resourceName"/> as both
            /// tanks allow and returns the amount ACTUALLY moved (the window's
            /// manifests are corner differences of the real tanks, so the moved
            /// amount is the expectation - never the requested one).</summary>
            internal double TransferResource(Part from, Part to, string resourceName, double requested)
            {
                PartResource src = FindResource(from, resourceName);
                PartResource dst = FindResource(to, resourceName);
                if (src == null || dst == null)
                {
                    ParsekLog.Warn("TestRunner",
                        "RouteDockCapture transfer skipped: " + resourceName +
                        " missing on " + (src == null ? "source" : "destination"));
                    return 0.0;
                }
                double moved = RouteDockCaptureMath.ResolveTransferAmount(
                    src.amount, dst.maxAmount - dst.amount, requested);
                if (moved <= 0.0)
                    return 0.0;
                src.amount -= moved;
                dst.amount += moved;
                ParsekLog.Info("TestRunner",
                    "RouteDockCapture transfer: " + resourceName + " moved=" + moved.ToString("F2", IC) +
                    " fromPid=" + from.persistentId.ToString(IC) +
                    " toPid=" + to.persistentId.ToString(IC));
                return moved;
            }

            /// <summary>
            /// Moves ONE quantity-1 stored cargo item across the docked stack
            /// through the stock API (<c>StoreCargoPartAtSlot</c> +
            /// <c>ClearPartAtSlot</c>, the pair
            /// <c>LogisticsRouteProofRuntimeTests</c> already drives live), and
            /// returns its logistics identity hash. Null (with a skip reason)
            /// when the live containers cannot supply the move.
            /// </summary>
            internal string MoveOneStoredCargoItem(bool fromEndpointSide, PartnerRig rig, out string skipReason)
            {
                skipReason = null;
                if (rig.Container == null)
                {
                    skipReason = "the partner rig carries no cargo container";
                    return null;
                }
                var destModule = rig.Container.FindModuleImplementing<ModuleInventoryPart>();
                if (destModule == null)
                {
                    skipReason = "the partner container has no ModuleInventoryPart";
                    return null;
                }
                int destSlot = destModule.FirstEmptySlot();
                if (destSlot < 0)
                {
                    skipReason = "the partner container has no empty slot";
                    return null;
                }

                Vessel merged = rig.Root.vessel;
                ConfigNode before = VesselSpawner.TryBackupSnapshot(merged);
                if (before == null)
                {
                    skipReason = "could not snapshot the merged vessel before the cargo move";
                    return null;
                }

                for (int p = 0; p < merged.parts.Count; p++)
                {
                    Part part = merged.parts[p];
                    if (part == null || part.Modules == null) continue;
                    bool onEndpointSide = IsPartOfRig(part, rig);
                    if (onEndpointSide != fromEndpointSide) continue;
                    for (int m = 0; m < part.Modules.Count; m++)
                    {
                        var module = part.Modules[m] as ModuleInventoryPart;
                        if (module == null || module.storedParts == null) continue;
                        for (int slot = 0; slot < module.InventorySlots; slot++)
                        {
                            if (!module.storedParts.ContainsKey(slot)) continue;
                            StoredPart stored = module.storedParts[slot];
                            if (stored == null || stored.quantity != 1
                                || string.IsNullOrEmpty(stored.partName) || stored.snapshot == null)
                            {
                                continue;
                            }
                            InventoryPayloadItem payload = FindUniquePayloadByPartName(
                                before, part.persistentId, stored.partName);
                            if (payload == null || string.IsNullOrEmpty(payload.IdentityHash))
                                continue;

                            if (!destModule.StoreCargoPartAtSlot(stored.snapshot, destSlot))
                                continue;
                            module.ClearPartAtSlot(slot);
                            ParsekLog.Info("TestRunner",
                                "RouteDockCapture cargo move: part=" + stored.partName +
                                " hash=" + payload.IdentityHash +
                                " fromPid=" + part.persistentId.ToString(IC) + " slot=" + slot.ToString(IC) +
                                " toPid=" + rig.Container.persistentId.ToString(IC) +
                                " slot=" + destSlot.ToString(IC));
                            return payload.IdentityHash;
                        }
                    }
                }

                skipReason = "no quantity-1 stored cargo item was found on the " +
                    (fromEndpointSide ? "endpoint" : "transport") +
                    " side of the docked stack (the host craft needs a filled ModuleInventoryPart)";
                return null;
            }

            private static bool IsPartOfRig(Part part, PartnerRig rig)
            {
                Part walk = part;
                while (walk != null)
                {
                    if (walk == rig.Root) return true;
                    walk = walk.parent;
                }
                return false;
            }

            // ----------------------------------------------------------
            // Verdict
            // ----------------------------------------------------------

            internal void RunAnalysisAndPass(
                int completeWindows,
                RouteConnectionKind kind,
                int deliveryResources,
                int deliveryInventory,
                int pickupResources,
                int pickupInventory,
                string detail)
            {
                if (Flight.IsRecording)
                    Flight.StopRecording();

                RouteAnalysisResult analysis = RouteAnalysisEngine.AnalyzeTree(tree);
                InGameAssert.IsNotNull(analysis, "AnalyzeTree returned null");
                // The cell's OWN construction rules three statuses out: a
                // completed DockingPort window exists, so proof is present and
                // the kind is supported, and the windows carry distinct finite
                // DockUTs so the M4a ordering cannot fail. Everything else
                // (above all the origin workflow gate, which depends on how the
                // host's root recording was born) is MEASURED into the pass
                // line rather than pinned - a lane that pinned Eligible here
                // would be asserting a property of the fixture, not of the
                // capture path this category exists to gate.
                InGameAssert.AreNotEqual(RouteAnalysisStatus.MissingRouteProof, analysis.Status,
                    "a completed route window exists, so analysis must not report MissingRouteProof");
                InGameAssert.AreNotEqual(RouteAnalysisStatus.UnsupportedConnectionKind, analysis.Status,
                    "a DockingPort window must never fail closed as an unsupported producer");
                InGameAssert.AreNotEqual(RouteAnalysisStatus.MultipleConnectionWindows, analysis.Status,
                    "the windows carry distinct finite DockUTs, so they must be orderable");

                int stops = analysis.Stops != null ? analysis.Stops.Count : 0;
                ParsekLog.Info("TestRunner",
                    RouteDockCaptureMath.FormatPassLine(
                        CellName, RunId, completeWindows, kind, true,
                        deliveryResources, deliveryInventory, pickupResources, pickupInventory,
                        detail + ";analysis=" + analysis.Status + ";stops=" + stops.ToString(IC)));
            }

            // ----------------------------------------------------------
            // Teardown
            // ----------------------------------------------------------

            internal void Teardown()
            {
                ParsekLog.TestObserverForTesting = priorObserver;
                try
                {
                    if (Flight != null)
                    {
                        if (Flight.IsRecording)
                            Flight.StopRecording();
                        if (Flight.ActiveTreeForSerialization != null)
                            Flight.AutoDiscardIdleActiveTree("RouteDockCapture test cleanup");
                    }
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("TestRunner",
                        "RouteDockCapture cleanup: recording/tree teardown failed (" +
                        ex.GetType().Name + ": " + ex.Message + ")");
                }

                int detached = 0, destroyed = 0, failures = 0;
                for (int i = attachedRoots.Count - 1; i >= 0; i--)
                {
                    Part part = attachedRoots[i];
                    try
                    {
                        if (part == null || part.vessel == null) continue;
                        if (ActiveVessel != null && part.vessel == ActiveVessel)
                        {
                            part.Undock(new DockedVesselInfo
                            {
                                name = "RouteDockCapture cleanup",
                                vesselType = VesselType.Probe,
                                rootPartUId = part.flightID
                            });
                            detached++;
                        }
                        if (part.vessel != null && part.vessel != ActiveVessel)
                        {
                            part.vessel.Die();
                            destroyed++;
                        }
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        ParsekLog.Warn("TestRunner",
                            "RouteDockCapture cleanup: part teardown failed (" +
                            ex.GetType().Name + ": " + ex.Message + ")");
                    }
                }
                for (int i = 0; i < spawnedVessels.Count; i++)
                {
                    try
                    {
                        Vessel v = spawnedVessels[i];
                        if (v != null && v != ActiveVessel && v.state != Vessel.State.DEAD)
                        {
                            v.Die();
                            destroyed++;
                        }
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        ParsekLog.Warn("TestRunner",
                            "RouteDockCapture cleanup: spawned vessel teardown failed (" +
                            ex.GetType().Name + ": " + ex.Message + ")");
                    }
                }
                ParsekLog.Verbose("TestRunner",
                    "RouteDockCapture cleanup: cell=" + CellName +
                    " detached=" + detached.ToString(IC) + " destroyed=" + destroyed.ToString(IC) +
                    " failures=" + failures.ToString(IC) +
                    " (baseline quickload restores the active vessel)");
            }
        }

        /// <summary>One spawned partner vessel and the parts of it the cells touch.</summary>
        private sealed class PartnerRig
        {
            internal readonly string Label;
            internal Vessel Vessel;
            internal Part Root;
            internal Part Tank;
            internal Part Container;
            internal RouteConnectionWindow LastWindow;

            internal PartnerRig(string label) { Label = label; }
        }

        // ==================================================================
        // Spawn helpers (deliberate self-contained copies of the claw cell's,
        // relabelled: a shared extraction would refactor a FLOWN gate)
        // ==================================================================

        private static uint SpawnSinglePartVessel(
            string partName, string vesselName, VesselType vtype, Vessel anchor, double offsetMeters)
        {
            CelestialBody body = anchor.mainBody;
            double ut = Planetarium.GetUniversalTime();
            double lat = anchor.latitude;
            double lon = anchor.longitude;
            double alt = anchor.altitude + 1.0;

            TerminalState? terminal = null;
            Orbit spawnOrbit = null;
            if (anchor.Landed || anchor.Splashed)
            {
                double cosLat = Math.Cos(lat * Math.PI / 180.0);
                double lonOffsetDeg = offsetMeters / ((body.Radius + alt) * cosLat) * (180.0 / Math.PI);
                lon += lonOffsetDeg;
                terminal = anchor.Landed ? TerminalState.Landed : TerminalState.Splashed;
            }
            else
            {
                spawnOrbit = BuildCoOrbitalSpawnOrbit(anchor, offsetMeters, ut, out string orbitFailReason);
                if (spawnOrbit == null)
                {
                    ParsekLog.Warn("TestRunner",
                        "RouteDockCapture spawn: cannot derive a co-orbital orbit for '" + vesselName +
                        "' from anchor '" + anchor.vesselName + "' (" + orbitFailReason +
                        "); returning pid=0 so the caller skips");
                    return 0;
                }
                terminal = TerminalState.Orbiting;
            }

            uint flightId = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
            ConfigNode partNode = ProtoVessel.CreatePartNode(partName, flightId);
            Orbit orbit = new Orbit(
                anchor.orbit.inclination, anchor.orbit.eccentricity, anchor.orbit.semiMajorAxis,
                anchor.orbit.LAN, anchor.orbit.argumentOfPeriapsis, anchor.orbit.meanAnomalyAtEpoch,
                anchor.orbit.epoch, body);
            ConfigNode vesselNode = ProtoVessel.CreateVesselNode(
                vesselName, vtype, orbit, 0, new[] { partNode });

            uint pid = VesselSpawner.SpawnAtPosition(
                vesselNode, body, lat, lon, alt,
                anchor.obt_velocity, ut,
                terminalState: terminal,
                orbitOverride: spawnOrbit);
            ParsekLog.Info("TestRunner",
                "RouteDockCapture spawn: part=" + partName + " vessel='" + vesselName +
                "' pid=" + pid.ToString(IC) + " offset=" + offsetMeters.ToString("F0", IC) +
                "m mode=" + (spawnOrbit != null ? "co-orbital" : "surface") +
                " lat=" + lat.ToString("F4", IC) + " lon=" + lon.ToString("F4", IC) +
                " alt=" + alt.ToString("F1", IC) +
                " terminal=" + (terminal.HasValue ? terminal.Value.ToString() : "<none>"));
            return pid;
        }

        private static Orbit BuildCoOrbitalSpawnOrbit(
            Vessel anchor, double offsetMeters, double ut, out string failReason)
        {
            Orbit src = anchor.orbit;
            if (src == null || src.referenceBody == null)
            {
                failReason = "anchor has no orbit / reference body";
                return null;
            }
            if (!TrajectoryMath.TryComputeCoOrbitalMeanAnomalyShift(
                    offsetMeters, anchor.obt_speed, src.semiMajorAxis,
                    src.referenceBody.gravParameter, out double meanAnomalyShift))
            {
                failReason = "degenerate anchor orbit";
                return null;
            }

            var orbit = new Orbit(
                src.inclination, src.eccentricity, src.semiMajorAxis, src.LAN,
                src.argumentOfPeriapsis, src.meanAnomalyAtEpoch + meanAnomalyShift,
                src.epoch, src.referenceBody);
            orbit.Init();
            orbit.UpdateFromUT(ut);
            failReason = null;
            return orbit;
        }

        private static IEnumerator WaitForSpawnedVesselLive(uint pid, string label, Action<Vessel> onLive)
        {
            float deadline = Time.realtimeSinceStartup + SpawnLoadTimeoutSeconds;
            bool hardened = false;
            int waitedFrames = 0;
            while (Time.realtimeSinceStartup < deadline)
            {
                Vessel v = FlightRecorder.FindVesselByPid(pid);
                if (v != null && v.loaded && v.parts != null && v.parts.Count > 0)
                {
                    if (!hardened)
                    {
                        GhostMapPresence.HardenGhostVesselPartPhysics(v, "RouteDockCapture " + label + " fixture");
                        hardened = true;
                    }
                    if (!v.packed)
                    {
                        ParsekLog.Verbose("TestRunner",
                            "RouteDockCapture " + label + " live after " + waitedFrames.ToString(IC) +
                            " frame(s): pid=" + pid.ToString(IC) + " parts=" + v.parts.Count.ToString(IC));
                        onLive(v);
                        yield break;
                    }
                }
                waitedFrames++;
                yield return null;
            }
            ParsekLog.Warn("TestRunner",
                "RouteDockCapture " + label + " wait timed out after " +
                SpawnLoadTimeoutSeconds.ToString("R", IC) + "s (pid=" + pid.ToString(IC) + ")");
        }

        private static IEnumerator WaitUntil(Func<bool> condition, float timeoutSeconds, string what)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (condition())
                    yield break;
                yield return null;
            }
            ParsekLog.Verbose("TestRunner",
                "RouteDockCapture wait timed out after " + timeoutSeconds.ToString("R", IC) + "s: " + what);
        }

        // ==================================================================
        // Small shared predicates
        // ==================================================================

        private static Part PrefabPart(string name)
        {
            AvailablePart info = PartLoader.getPartInfoByName(name);
            return info != null ? info.partPrefab : null;
        }

        private static bool IsProducerClassifiedLine(string line)
        {
            return line.Contains("[Flight]") && line.Contains("OnPartCouple producer classified");
        }

        private static bool IsWindowCapturedLine(string line)
        {
            return line.Contains("[Flight]") && line.Contains("Route proof dock window captured");
        }

        private static bool IsWindowCompletedLine(string line)
        {
            return line.Contains("[Flight]") && line.Contains("Route proof dock window completed on undock");
        }

        private static PartResource FindResource(Part part, string resourceName)
        {
            if (part == null || part.Resources == null)
                return null;
            for (int i = 0; i < part.Resources.Count; i++)
            {
                PartResource res = part.Resources[i];
                if (res != null && res.resourceName == resourceName)
                    return res;
            }
            return null;
        }

        private static Part FindResourceBearingPart(Vessel vessel, string resourceName)
        {
            if (vessel?.parts == null)
                return null;
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                PartResource res = FindResource(vessel.parts[i], resourceName);
                if (res != null && res.maxAmount > 0.0)
                    return vessel.parts[i];
            }
            return null;
        }

        private static int ManifestCount(Dictionary<string, double> manifest)
        {
            return manifest != null ? manifest.Count : 0;
        }

        private static bool ContainsHash(List<InventoryPayloadItem> items, string hash)
        {
            if (items == null || string.IsNullOrEmpty(hash))
                return false;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] != null && items[i].IdentityHash == hash)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The unique payload for <paramref name="partName"/> inside one part's
        /// inventory in <paramref name="vesselSnapshot"/>, or null when it is
        /// absent or ambiguous. Same rule
        /// <c>LogisticsRouteProofRuntimeTests</c> uses; duplicated so the two
        /// cell files stay independently readable.
        /// </summary>
        private static InventoryPayloadItem FindUniquePayloadByPartName(
            ConfigNode vesselSnapshot, uint partPersistentId, string partName)
        {
            List<InventoryPayloadItem> items = VesselSpawner.ExtractInventoryPayloadItems(
                vesselSnapshot, new List<uint> { partPersistentId });
            if (items == null)
                return null;
            InventoryPayloadItem found = null;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] == null || items[i].PartName != partName)
                    continue;
                if (found != null)
                    return null;
                found = items[i];
            }
            return found;
        }
    }
}
