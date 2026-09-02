using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Stock-runtime checks for the logistics route-proof contracts that xUnit
    /// cannot prove without live KSP vessels and ModuleInventoryPart instances.
    /// </summary>
    public sealed class LogisticsRouteProofRuntimeTests
    {
        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            Description = "Route proof for active-as-target dock merges keeps the absorbed endpoint PID and endpoint coordinates")]
        public void RouteProof_ActiveAsTargetDockWindow_HasEndpointProof()
        {
            RouteWindowRuntimeView view = FindRouteWindow(
                candidate => candidate.Window.TransferTargetVesselPid != 0
                    && candidate.Recording.VesselPersistentId != 0
                    && candidate.Window.TransferTargetVesselPid != candidate.Recording.VesselPersistentId);

            if (view == null)
            {
                InGameAssert.Skip(
                    "No active-as-target logistics dock route window found. Record a route where the active vessel is the dock target, then run this test.");
            }

            AssertEndpointProof(view, "active-as-target");
        }

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            Description = "Route proof for active-as-initiator dock merges keeps the surviving endpoint PID and endpoint coordinates")]
        public void RouteProof_ActiveAsInitiatorDockWindow_HasEndpointProof()
        {
            RouteWindowRuntimeView view = FindRouteWindow(
                candidate => candidate.Window.TransferTargetVesselPid != 0
                    && candidate.Recording.VesselPersistentId != 0
                    && candidate.Window.TransferTargetVesselPid == candidate.Recording.VesselPersistentId);

            if (view == null)
            {
                InGameAssert.Skip(
                    "No active-as-initiator logistics dock route window found. Record a route where the active vessel docks into another vessel, then run this test.");
            }

            AssertEndpointProof(view, "active-as-initiator");
        }

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            Description = "Route proof survives a dock whose partner vessel only has a committed recording from a prior tree (not in the current tree's BackgroundMap)")]
        public void RouteProof_CrossTreeCommittedPartner_HasEndpointProof()
        {
            // The previous resolver bug: FindAbsorbedDockPartnerPid only consulted
            // activeTree.BackgroundMap. A dock partner whose recording was committed
            // in a prior tree is invisible to BackgroundMap, so routeTargetPid
            // resolved to 0 and no RouteConnectionWindow was attached to the merged
            // child. The fix derives the partner PID from the couple event and
            // validates it against CommittedRecordings / activeTree.Recordings.
            RouteWindowRuntimeView view = FindRouteWindow(candidate =>
            {
                RouteConnectionWindow w = candidate.Window;
                if (w == null || w.TransferTargetVesselPid == 0) return false;

                // Active-as-target dock window AND partner PID matches a recording
                // in CommittedRecordings (the cross-tree case).
                if (w.TransferTargetVesselPid == candidate.Recording.VesselPersistentId)
                    return false; // initiator case — covered by sibling test

                IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
                if (committed == null) return false;
                for (int i = 0; i < committed.Count; i++)
                {
                    Recording r = committed[i];
                    if (r != null && r.VesselPersistentId == w.TransferTargetVesselPid)
                        return true;
                }
                return false;
            });

            if (view == null)
            {
                InGameAssert.Skip(
                    "No cross-tree-committed-partner dock route window found. " +
                    "Repro: commit one tree on vessel A, then launch vessel B in a fresh tree and dock B into A; " +
                    "the merged child on B's tree should carry a route window whose TransferTargetVesselPid matches A's committed recording.");
            }

            AssertEndpointProof(view, "cross-tree-committed-partner");
        }

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            Description = "Moving a stock cargo item between live ModuleInventoryPart containers preserves logistics payload identity",
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only - moves a stock cargo item between live ModuleInventoryPart containers; excluded from ordinary Run All / Run category. Use Run All + Isolated or the row play button in a disposable FLIGHT session with two cargo containers.")]
        public IEnumerator InventoryPayloadIdentityHash_LiveStockMove_PreservesIdentity()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            InGameAssert.IsNotNull(vessel, "No active vessel in flight");

            if (!TryFindInventoryMoveCandidate(vessel, out InventoryMoveCandidate candidate, out string skipReason))
                InGameAssert.Skip(skipReason);

            string beforeHash = candidate.SourcePayload.IdentityHash;
            bool storedInDestination = false;
            bool clearedSource = false;

            try
            {
                storedInDestination = candidate.Destination.Module.StoreCargoPartAtSlot(
                    candidate.SourceStoredPartSnapshot,
                    candidate.DestinationSlot);
                InGameAssert.IsTrue(storedInDestination,
                    $"StoreCargoPartAtSlot failed for '{candidate.PartName}' into destination slot {candidate.DestinationSlot}");

                yield return null;

                InventoryPayloadItem destinationPayload = FindPayloadByHash(
                    vessel,
                    candidate.Destination.PartPersistentId,
                    beforeHash);
                InGameAssert.IsNotNull(destinationPayload,
                    "Stored destination payload was not visible in a live vessel snapshot");
                InGameAssert.AreEqual(beforeHash, destinationPayload.IdentityHash,
                    "Destination payload hash changed immediately after stock StoreCargoPartAtSlot");

                clearedSource = candidate.Source.Module.ClearPartAtSlot(candidate.SourceSlot);
                InGameAssert.IsTrue(clearedSource,
                    $"ClearPartAtSlot failed for source slot {candidate.SourceSlot}");

                yield return null;

                destinationPayload = FindPayloadByHash(
                    vessel,
                    candidate.Destination.PartPersistentId,
                    beforeHash);
                InGameAssert.IsNotNull(destinationPayload,
                    "Moved destination payload was not visible after clearing the source slot");
                InGameAssert.AreEqual(beforeHash, destinationPayload.IdentityHash,
                    "Moving a stock cargo item between inventories changed logistics payload identity");

                // The overload driven above, StoreCargoPartAtSlot(ProtoPartSnapshot,
                // int), copies the node VERBATIM, so it cannot re-run any module's
                // OnSave. The player's in-flight move takes the sibling
                // StoreCargoPartAtSlot(Part, int) overload, which builds a fresh
                // ProtoPartSnapshot off the LIVE part and therefore lets a module
                // write a runtime-computed value (the witnessed one was
                // ModuleGroundExpControl.canComm; see the closed defect
                // LOGISTICS-INVENTORY-IDENTITY-HASH-BREAKS-ON-A-LIVE-CARGO-MOVE).
                // This rig cannot instantiate a live cargo Part to drive that
                // overload, so it asserts the property that makes the live path
                // safe under the 2026-09-02 kind ruling: module state does not
                // participate in the KIND key at all. Perturb the MOVED node the
                // way a live re-serialization does, then re-derive the key.
                ConfigNode reSerialized = destinationPayload.StoredPartSnapshot != null
                    ? destinationPayload.StoredPartSnapshot.CreateCopy()
                    : null;
                InGameAssert.IsNotNull(reSerialized,
                    "Moved destination payload carried no STOREDPART snapshot to re-derive from");

                int mutatedModuleValues = SimulateLiveReSerialization(reSerialized);
                InGameAssert.IsTrue(mutatedModuleValues > 0,
                    $"Cargo part '{candidate.PartName}' has no MODULE nodes to perturb; " +
                    "pick a cargo item carrying at least one module so the kind-key claim is exercised");

                InGameAssert.AreEqual(beforeHash,
                    VesselSpawner.ComputeInventoryPayloadKindKey(reSerialized),
                    "Module values written by a live re-serialization changed the payload KIND key");

                ParsekLog.Verbose("TestRunner",
                    $"InventoryPayloadIdentityHash_LiveStockMove_PreservesIdentity: part={candidate.PartName} " +
                    $"sourcePartPid={candidate.Source.PartPersistentId} sourceSlot={candidate.SourceSlot} " +
                    $"destPartPid={candidate.Destination.PartPersistentId} destSlot={candidate.DestinationSlot} " +
                    $"mutatedModuleValues={mutatedModuleValues.ToString(CultureInfo.InvariantCulture)} " +
                    $"hash={beforeHash}");
            }
            finally
            {
                RestoreInventoryMoveCandidate(candidate, storedInDestination, clearedSource);
            }
        }

        private static void AssertEndpointProof(RouteWindowRuntimeView view, string mode)
        {
            RouteConnectionWindow window = view.Window;
            InGameAssert.IsNotNull(window, $"{mode}: route window is null");
            InGameAssert.AreNotEqual(0u, window.TransferTargetVesselPid,
                $"{mode}: TransferTargetVesselPid must be set");
            InGameAssert.AreEqual(RouteConnectionKind.DockingPort, window.TransferKind,
                $"{mode}: TransferKind should be DockingPort");
            InGameAssert.IsTrue(window.EndpointAtDock.HasValue,
                $"{mode}: EndpointAtDock must be populated");
            InGameAssert.IsTrue(window.TransferEndpointSituation >= 0,
                $"{mode}: TransferEndpointSituation must be populated");

            RouteEndpoint endpoint = window.EndpointAtDock.Value;
            InGameAssert.AreEqual(window.TransferTargetVesselPid, endpoint.VesselPersistentId,
                $"{mode}: endpoint PID must match TransferTargetVesselPid");
            InGameAssert.IsFalse(string.IsNullOrEmpty(endpoint.BodyName),
                $"{mode}: endpoint body name must be captured");
            InGameAssert.IsTrue(IsFinite(endpoint.Latitude),
                $"{mode}: endpoint latitude must be finite");
            InGameAssert.IsTrue(IsFinite(endpoint.Longitude),
                $"{mode}: endpoint longitude must be finite");
            InGameAssert.IsTrue(IsFinite(endpoint.Altitude),
                $"{mode}: endpoint altitude must be finite");
            InGameAssert.IsTrue(endpoint.Latitude >= -90.0 && endpoint.Latitude <= 90.0,
                $"{mode}: endpoint latitude out of range: {endpoint.Latitude}");
            InGameAssert.IsTrue(endpoint.Longitude >= -180.0 && endpoint.Longitude <= 180.0,
                $"{mode}: endpoint longitude out of expected KSP range: {endpoint.Longitude}");

            ParsekLog.Verbose("TestRunner",
                $"RouteProof_{mode}: tree={view.TreeId} recording={view.Recording.RecordingId} " +
                $"childPid={view.Recording.VesselPersistentId} targetPid={window.TransferTargetVesselPid} " +
                $"body={endpoint.BodyName} lat={endpoint.Latitude.ToString("R", CultureInfo.InvariantCulture)} " +
                $"lon={endpoint.Longitude.ToString("R", CultureInfo.InvariantCulture)} " +
                $"alt={endpoint.Altitude.ToString("R", CultureInfo.InvariantCulture)} " +
                $"situation={window.TransferEndpointSituation}");
        }

        private static RouteWindowRuntimeView FindRouteWindow(
            Func<RouteWindowRuntimeView, bool> predicate)
        {
            foreach (RecordingTree tree in EnumerateRouteProofTrees())
            {
                if (tree?.Recordings == null)
                    continue;

                foreach (Recording recording in tree.Recordings.Values)
                {
                    if (recording?.RouteConnectionWindows == null)
                        continue;

                    for (int i = 0; i < recording.RouteConnectionWindows.Count; i++)
                    {
                        RouteConnectionWindow window = recording.RouteConnectionWindows[i];
                        if (window == null)
                            continue;

                        var view = new RouteWindowRuntimeView
                        {
                            TreeId = tree.Id,
                            Recording = recording,
                            Window = window
                        };

                        if (predicate(view))
                            return view;
                    }
                }
            }

            return null;
        }

        private static IEnumerable<RecordingTree> EnumerateRouteProofTrees()
        {
            RecordingTree activeTree = ParsekFlight.Instance?.ActiveTreeForSerialization;
            if (activeTree != null)
                yield return activeTree;

            RecordingTree pendingTree = RecordingStore.PendingTree;
            if (pendingTree != null && !ReferenceEquals(pendingTree, activeTree))
                yield return pendingTree;

            if (RecordingStore.CommittedTrees == null)
                yield break;

            for (int i = 0; i < RecordingStore.CommittedTrees.Count; i++)
            {
                RecordingTree tree = RecordingStore.CommittedTrees[i];
                if (tree == null || ReferenceEquals(tree, activeTree) || ReferenceEquals(tree, pendingTree))
                    continue;

                yield return tree;
            }
        }

        private static bool TryFindInventoryMoveCandidate(
            Vessel vessel,
            out InventoryMoveCandidate candidate,
            out string skipReason)
        {
            candidate = null;
            skipReason = null;

            List<InventoryModuleRef> modules = CollectInventoryModules(vessel);
            if (modules.Count < 2)
            {
                skipReason = "Active vessel needs at least two ModuleInventoryPart containers";
                return false;
            }

            ConfigNode beforeSnapshot = VesselSpawner.TryBackupSnapshot(vessel);
            if (beforeSnapshot == null)
            {
                skipReason = "Could not snapshot active vessel before inventory move";
                return false;
            }

            for (int s = 0; s < modules.Count; s++)
            {
                InventoryModuleRef source = modules[s];
                List<StoredPartRef> sourceStoredParts = CollectStoredParts(source.Module);
                for (int sp = 0; sp < sourceStoredParts.Count; sp++)
                {
                    StoredPartRef stored = sourceStoredParts[sp];
                    if (stored.Quantity != 1 || stored.Snapshot == null)
                        continue;

                    InventoryPayloadItem sourcePayload = FindUniquePayloadByPartName(
                        beforeSnapshot,
                        source.PartPersistentId,
                        stored.PartName);
                    if (sourcePayload == null || string.IsNullOrEmpty(sourcePayload.IdentityHash))
                        continue;

                    for (int d = 0; d < modules.Count; d++)
                    {
                        if (d == s)
                            continue;

                        InventoryModuleRef destination = modules[d];
                        int destinationSlot = destination.Module.FirstEmptySlot();
                        if (destinationSlot < 0)
                            continue;
                        if (FindPayloadByHash(
                                beforeSnapshot,
                                destination.PartPersistentId,
                                sourcePayload.IdentityHash) != null)
                        {
                            continue;
                        }

                        candidate = new InventoryMoveCandidate
                        {
                            Source = source,
                            Destination = destination,
                            SourceSlot = stored.SlotIndex,
                            DestinationSlot = destinationSlot,
                            PartName = stored.PartName,
                            SourceStoredPartSnapshot = stored.Snapshot,
                            SourcePayload = sourcePayload
                        };
                        return true;
                    }
                }
            }

            skipReason = "No quantity-1 stored cargo item with a second empty inventory container was found on the active vessel";
            return false;
        }

        private static List<InventoryModuleRef> CollectInventoryModules(Vessel vessel)
        {
            var modules = new List<InventoryModuleRef>();
            if (vessel?.parts == null)
                return modules;

            for (int p = 0; p < vessel.parts.Count; p++)
            {
                Part part = vessel.parts[p];
                if (part?.Modules == null)
                    continue;

                for (int m = 0; m < part.Modules.Count; m++)
                {
                    var module = part.Modules[m] as ModuleInventoryPart;
                    if (module == null)
                        continue;

                    modules.Add(new InventoryModuleRef
                    {
                        Part = part,
                        Module = module,
                        PartPersistentId = part.persistentId
                    });
                }
            }

            return modules;
        }

        /// <summary>
        /// Occupied slots of <paramref name="module"/> through the PUBLIC
        /// compile-time API (the walk <see cref="LogisticsDeliveryRuntimeTests"/>
        /// uses): <c>ModuleInventoryPart.storedParts</c> is a public
        /// <c>DictionaryValueList&lt;int, StoredPart&gt;</c> keyed by slot index,
        /// and <c>StoredPart.partName / quantity / snapshot</c> are public
        /// fields. This used to go through reflection whose
        /// <c>GetProperty("KeysList")</c> bound Public-only against an INTERNAL
        /// property, so every enumeration came back empty and the finder
        /// reported "no stored cargo" on a fully loaded container.
        /// </summary>
        private static List<StoredPartRef> CollectStoredParts(ModuleInventoryPart module)
        {
            var result = new List<StoredPartRef>();
            if (module == null || module.storedParts == null)
                return result;

            for (int slotIndex = 0; slotIndex < module.InventorySlots; slotIndex++)
            {
                if (!module.storedParts.ContainsKey(slotIndex))
                    continue;

                StoredPart storedPart = module.storedParts[slotIndex];
                if (storedPart == null)
                    continue;
                if (string.IsNullOrEmpty(storedPart.partName) || storedPart.quantity <= 0)
                    continue;

                result.Add(new StoredPartRef
                {
                    SlotIndex = slotIndex,
                    PartName = storedPart.partName,
                    Quantity = storedPart.quantity,
                    Snapshot = storedPart.snapshot
                });
            }

            return result;
        }

        private static InventoryPayloadItem FindUniquePayloadByPartName(
            ConfigNode vesselSnapshot,
            uint partPersistentId,
            string partName)
        {
            List<InventoryPayloadItem> items = VesselSpawner.ExtractInventoryPayloadItems(
                vesselSnapshot,
                new List<uint> { partPersistentId });
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

        private static InventoryPayloadItem FindPayloadByHash(
            Vessel vessel,
            uint partPersistentId,
            string identityHash)
        {
            ConfigNode snapshot = VesselSpawner.TryBackupSnapshot(vessel);
            if (snapshot == null)
                return null;

            return FindPayloadByHash(snapshot, partPersistentId, identityHash);
        }

        /// <summary>
        /// Perturbs a STOREDPART node the way stock's live re-serialization does
        /// on <c>StoreCargoPartAtSlot(Part, int)</c>: every MODULE gains a
        /// runtime-computed value it did not carry (the canComm shape) and has an
        /// existing one flipped, and the PART-level placement transients move.
        /// Returns the number of MODULE nodes touched so the caller can refuse to
        /// pass vacuously on a module-less cargo part.
        /// </summary>
        private static int SimulateLiveReSerialization(ConfigNode storedPart)
        {
            if (storedPart == null)
                return 0;

            storedPart.SetValue("slotIndex", "31", true);

            int touched = 0;
            ConfigNode[] parts = storedPart.GetNodes("PART");
            for (int i = 0; i < parts.Length; i++)
            {
                parts[i].SetValue("persistentId", "4294967290", true);
                parts[i].SetValue("state", "6", true);
                parts[i].SetValue("attached", "False", true);

                ConfigNode[] modules = parts[i].GetNodes("MODULE");
                for (int j = 0; j < modules.Length; j++)
                {
                    modules[j].AddValue("parsekLiveResaveProbe", "False");
                    modules[j].SetValue("isEnabled", "False", true);
                    touched++;
                }
            }
            return touched;
        }

        private static InventoryPayloadItem FindPayloadByHash(
            ConfigNode vesselSnapshot,
            uint partPersistentId,
            string identityHash)
        {
            if (vesselSnapshot == null || string.IsNullOrEmpty(identityHash))
                return null;

            List<InventoryPayloadItem> items = VesselSpawner.ExtractInventoryPayloadItems(
                vesselSnapshot,
                new List<uint> { partPersistentId });
            if (items == null)
                return null;

            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] != null && items[i].IdentityHash == identityHash)
                    return items[i];
            }

            return null;
        }

        private static void RestoreInventoryMoveCandidate(
            InventoryMoveCandidate candidate,
            bool storedInDestination,
            bool clearedSource)
        {
            if (candidate == null)
                return;

            try
            {
                if (storedInDestination)
                    candidate.Destination.Module.ClearPartAtSlot(candidate.DestinationSlot);

                if (clearedSource)
                {
                    bool restored = candidate.Source.Module.StoreCargoPartAtSlot(
                        candidate.SourceStoredPartSnapshot,
                        candidate.SourceSlot);
                    if (!restored)
                    {
                        ParsekLog.Warn("TestRunner",
                            $"InventoryPayloadIdentityHash_LiveStockMove_PreservesIdentity: failed to restore " +
                            $"'{candidate.PartName}' to source slot {candidate.SourceSlot}");
                    }
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("TestRunner",
                    $"InventoryPayloadIdentityHash_LiveStockMove_PreservesIdentity: restore threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private sealed class RouteWindowRuntimeView
        {
            public string TreeId;
            public Recording Recording;
            public RouteConnectionWindow Window;
        }

        private sealed class InventoryModuleRef
        {
            public Part Part;
            public ModuleInventoryPart Module;
            public uint PartPersistentId;
        }

        private sealed class StoredPartRef
        {
            public int SlotIndex;
            public string PartName;
            public int Quantity;
            public ProtoPartSnapshot Snapshot;
        }

        private sealed class InventoryMoveCandidate
        {
            public InventoryModuleRef Source;
            public InventoryModuleRef Destination;
            public int SourceSlot;
            public int DestinationSlot;
            public string PartName;
            public ProtoPartSnapshot SourceStoredPartSnapshot;
            public InventoryPayloadItem SourcePayload;
        }
    }
}
