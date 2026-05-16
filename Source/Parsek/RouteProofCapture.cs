using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Candidate part-to-parent mapping used by <see cref="RouteProofCapture.TryResolveStartDockedOriginPartner"/>.
    /// Represents one part on the active vessel whose <c>part.parent</c> belongs to a different vessel
    /// at recording-start time (i.e. an externally-coupled coupling such as a dock or grapple).
    /// </summary>
    internal readonly struct OriginPartnerCandidate
    {
        public readonly uint PartPersistentId;
        public readonly uint ParentVesselPersistentId;
        public readonly int ParentVesselSituation; // (int)Vessel.Situations; -1 = unknown

        internal OriginPartnerCandidate(uint partPersistentId, uint parentVesselPersistentId, int parentVesselSituation)
        {
            PartPersistentId = partPersistentId;
            ParentVesselPersistentId = parentVesselPersistentId;
            ParentVesselSituation = parentVesselSituation;
        }
    }

    /// <summary>
    /// Outcome of the pure start-docked origin-partner resolver. <see cref="Captured"/> is the
    /// only branch that produces a usable partner PID; all others identify the specific reason
    /// the recording should NOT carry a RouteOriginProof.
    /// </summary>
    internal enum OriginProofDetection
    {
        Captured,
        NoExternalCoupling,
        ActiveVesselPrelaunch,
        PartnerPrelaunch,
        PartnerPidZero,
        PartnerAmbiguous,
    }

    internal static class RouteProofCapture
    {
        /// <summary>
        /// Pure resolver: given the active vessel's situation/EVA flag and a list of externally
        /// parented parts (parts whose <c>part.parent.vessel != activeVessel</c>), decide whether
        /// a single valid non-KSC origin partner exists. No KSP dependency; logging happens at the
        /// call site so callers can attach context (vessel name, recording id, etc.).
        ///
        /// Decision contract (in order):
        ///   1. activeVesselIsEva == true          -> NoExternalCoupling
        ///   2. activeVesselSituation == PRELAUNCH -> ActiveVesselPrelaunch
        ///   3. empty candidate list               -> NoExternalCoupling
        ///   4. distinct non-zero, non-PRELAUNCH partners == 1 -> Captured
        ///      else all partner pids == 0         -> PartnerPidZero
        ///      else all valid candidates PRELAUNCH-> PartnerPrelaunch
        ///      else 2+ distinct valid partners    -> PartnerAmbiguous
        /// </summary>
        internal static OriginProofDetection TryResolveStartDockedOriginPartner(
            int activeVesselSituation,
            bool activeVesselIsEva,
            IReadOnlyList<OriginPartnerCandidate> externallyParentedParts,
            out uint partnerVesselPid)
        {
            partnerVesselPid = 0;

            if (activeVesselIsEva)
                return OriginProofDetection.NoExternalCoupling;

            if (activeVesselSituation == (int)Vessel.Situations.PRELAUNCH)
                return OriginProofDetection.ActiveVesselPrelaunch;

            if (externallyParentedParts == null || externallyParentedParts.Count == 0)
                return OriginProofDetection.NoExternalCoupling;

            // Walk candidates; track whether ANY candidate existed at all, whether ALL of them
            // had pid==0, whether ALL valid (pid!=0) candidates were PRELAUNCH, and the set of
            // distinct non-zero, non-PRELAUNCH partner pids.
            bool sawAnyCandidate = false;
            bool sawAnyNonZeroPid = false;
            bool sawAnyNonPrelaunchValid = false;
            var distinctValidPids = new List<uint>();

            for (int i = 0; i < externallyParentedParts.Count; i++)
            {
                OriginPartnerCandidate c = externallyParentedParts[i];
                sawAnyCandidate = true;
                if (c.ParentVesselPersistentId == 0)
                    continue;
                sawAnyNonZeroPid = true;
                if (c.ParentVesselSituation == (int)Vessel.Situations.PRELAUNCH)
                    continue;
                sawAnyNonPrelaunchValid = true;
                if (!distinctValidPids.Contains(c.ParentVesselPersistentId))
                    distinctValidPids.Add(c.ParentVesselPersistentId);
            }

            if (!sawAnyCandidate)
                return OriginProofDetection.NoExternalCoupling;

            if (distinctValidPids.Count == 1)
            {
                partnerVesselPid = distinctValidPids[0];
                return OriginProofDetection.Captured;
            }

            if (distinctValidPids.Count >= 2)
                return OriginProofDetection.PartnerAmbiguous;

            // distinctValidPids.Count == 0: classify why
            if (!sawAnyNonZeroPid)
                return OriginProofDetection.PartnerPidZero;

            if (!sawAnyNonPrelaunchValid)
                return OriginProofDetection.PartnerPrelaunch;

            // Defensive: theoretically unreachable, but keep a deterministic answer.
            return OriginProofDetection.NoExternalCoupling;
        }

        /// <summary>
        /// Logistics start-docked origin proof producer (pure / static).
        /// Handles the gloops-mode early-skip, null-snapshot warn-skip, the
        /// <see cref="TryResolveStartDockedOriginPartner"/> dispatch, and the per-branch
        /// log emission. Returns the populated <paramref name="proof"/> +
        /// <paramref name="transportPartPersistentIds"/> on the Captured branch and
        /// <c>null</c> on every benign-or-degenerate branch.
        ///
        /// <paramref name="vesselContext"/> is interpolated into log strings as
        /// <c>vessel='{vesselContext}'</c>. Production passes the live vessel name;
        /// tests pass <c>&lt;test&gt;</c>.
        ///
        /// <paramref name="recordingVesselId"/> is interpolated into log strings as
        /// <c>recId={recordingVesselId}</c>.
        ///
        /// Both <see cref="FlightRecorder.CaptureStartRouteOriginProofIfDocked"/> and the
        /// unit tests call this helper directly so the producer logic stays in one place.
        /// </summary>
        internal static void BuildStartRouteOriginProof(
            int activeVesselSituation,
            bool activeVesselIsEva,
            IReadOnlyList<OriginPartnerCandidate> candidates,
            ConfigNode snapshot,
            bool isGloopsMode,
            string vesselContext,
            uint recordingVesselId,
            out RouteOriginProof proof,
            out List<uint> transportPartPersistentIds)
        {
            proof = null;
            transportPartPersistentIds = null;

            if (isGloopsMode)
            {
                ParsekLog.Verbose("Recorder",
                    $"RouteOriginProof skipped: gloops mode recId={recordingVesselId} vessel='{vesselContext}'");
                return;
            }
            if (snapshot == null)
            {
                ParsekLog.Warn("Recorder",
                    $"RouteOriginProof skipped: no last good snapshot recId={recordingVesselId} " +
                    $"vessel='{vesselContext}'");
                return;
            }

            int candidateCount = candidates?.Count ?? 0;
            OriginProofDetection outcome = TryResolveStartDockedOriginPartner(
                activeVesselSituation,
                activeVesselIsEva,
                candidates ?? new List<OriginPartnerCandidate>(),
                out uint partnerPid);

            switch (outcome)
            {
                case OriginProofDetection.Captured:
                {
                    var transportPids = VesselSpawner.CollectPartPersistentIds(snapshot);
                    Dictionary<string, ResourceAmount> startRes =
                        VesselSpawner.ExtractResourceManifest(snapshot, transportPids);
                    List<InventoryPayloadItem> startInv =
                        VesselSpawner.ExtractInventoryPayloadItems(snapshot, transportPids);

                    proof = new RouteOriginProof
                    {
                        StartDockedOriginVesselPid = partnerPid,
                        StartTransportResources = startRes,
                        StartTransportInventory = startInv,
                    };
                    transportPartPersistentIds = transportPids;

                    ParsekLog.Info("Recorder",
                        $"RouteOriginProof captured: recId={recordingVesselId} vessel='{vesselContext}' " +
                        $"partnerPid={partnerPid} candidates={candidateCount} " +
                        $"transportParts={transportPids?.Count ?? 0} " +
                        $"startRes={startRes?.Count ?? 0} startInv={startInv?.Count ?? 0}");
                    break;
                }
                case OriginProofDetection.NoExternalCoupling:
                    ParsekLog.Verbose("Recorder",
                        $"RouteOriginProof skipped: no external coupling recId={recordingVesselId} " +
                        $"vessel='{vesselContext}' candidates={candidateCount} isEva={activeVesselIsEva}");
                    break;
                case OriginProofDetection.ActiveVesselPrelaunch:
                    ParsekLog.Verbose("Recorder",
                        $"RouteOriginProof skipped: active vessel PRELAUNCH recId={recordingVesselId} " +
                        $"vessel='{vesselContext}' candidates={candidateCount}");
                    break;
                case OriginProofDetection.PartnerPrelaunch:
                    ParsekLog.Verbose("Recorder",
                        $"RouteOriginProof skipped: partner PRELAUNCH recId={recordingVesselId} " +
                        $"vessel='{vesselContext}' candidates={candidateCount}");
                    break;
                case OriginProofDetection.PartnerPidZero:
                    ParsekLog.Warn("Recorder",
                        $"RouteOriginProof skipped: partner pid=0 recId={recordingVesselId} " +
                        $"vessel='{vesselContext}' candidates={candidateCount}");
                    break;
                case OriginProofDetection.PartnerAmbiguous:
                {
                    var distinctPids = new List<uint>();
                    if (candidates != null)
                    {
                        for (int i = 0; i < candidates.Count; i++)
                        {
                            uint pid = candidates[i].ParentVesselPersistentId;
                            if (pid == 0) continue;
                            if (candidates[i].ParentVesselSituation == (int)Vessel.Situations.PRELAUNCH) continue;
                            if (!distinctPids.Contains(pid)) distinctPids.Add(pid);
                        }
                    }
                    ParsekLog.Warn("Recorder",
                        $"RouteOriginProof skipped: ambiguous partners recId={recordingVesselId} " +
                        $"vessel='{vesselContext}' candidates={candidateCount} " +
                        $"distinctPartnerPids=[{string.Join(",", distinctPids)}]");
                    break;
                }
            }
        }

        /// <summary>
        /// Forwards a start-time <see cref="RouteOriginProof"/> onto a captured
        /// <see cref="Recording"/>, re-extracting the end transport manifests scoped to
        /// the same part-pid set captured at start. No-op when either input is null —
        /// callers do not need to guard.
        ///
        /// Both <see cref="FlightRecorder.BuildCaptureRecording"/> and the unit tests
        /// call this helper directly so the forwarding logic stays in one place. The
        /// v0 decoupled-parts contract note lives at the production callsite — see
        /// <c>FlightRecorder.BuildCaptureRecording</c>.
        /// </summary>
        internal static void AttachEndManifestsAndForwardToCapture(
            Recording capture,
            RouteOriginProof pendingProof,
            ICollection<uint> pendingStartPartPersistentIds)
        {
            if (capture == null || pendingProof == null || pendingStartPartPersistentIds == null)
                return;

            pendingProof.EndTransportResources =
                VesselSpawner.ExtractResourceManifest(capture.VesselSnapshot, pendingStartPartPersistentIds);
            pendingProof.EndTransportInventory =
                VesselSpawner.ExtractInventoryPayloadItems(capture.VesselSnapshot, pendingStartPartPersistentIds);
            capture.RouteOriginProof = pendingProof;
            ParsekLog.Verbose("Recorder",
                $"BuildCaptureRecording: forwarded RouteOriginProof partner={pendingProof.StartDockedOriginVesselPid} " +
                $"startRes={pendingProof.StartTransportResources?.Count ?? 0} " +
                $"endRes={pendingProof.EndTransportResources?.Count ?? 0} " +
                $"startInv={pendingProof.StartTransportInventory?.Count ?? 0} " +
                $"endInv={pendingProof.EndTransportInventory?.Count ?? 0}");
        }

        internal static RouteConnectionWindow BuildDockRouteConnectionWindow(
            double dockUT,
            uint transferTargetVesselPid,
            RouteConnectionKind transferKind,
            ConfigNode dockedSnapshot,
            ICollection<uint> transportPartPersistentIds,
            ICollection<uint> endpointPartPersistentIds,
            RouteEndpoint? endpointAtDock,
            int transferEndpointSituation)
        {
            if (transferTargetVesselPid == 0 || dockedSnapshot == null)
                return null;

            List<uint> transportPids = NormalizePartPids(transportPartPersistentIds);
            if (transportPids == null || transportPids.Count == 0)
                return null;

            List<uint> endpointPids = NormalizePartPids(endpointPartPersistentIds);
            if (endpointPids == null || endpointPids.Count == 0)
                endpointPids = DeriveEndpointPartPids(dockedSnapshot, transportPids);

            if (endpointPids == null || endpointPids.Count == 0)
                return null;

            if (!SnapshotContainsAnyPartPersistentId(dockedSnapshot, transportPids) ||
                !SnapshotContainsAnyPartPersistentId(dockedSnapshot, endpointPids))
            {
                ParsekLog.Warn("Flight",
                    $"Route window dock capture failed: docked snapshot does not contain " +
                    $"transport/endpoint part PID sets targetPid={transferTargetVesselPid} " +
                    $"transportParts={transportPids.Count} endpointParts={endpointPids.Count}");
                return null;
            }

            var window = new RouteConnectionWindow
            {
                WindowId = BuildWindowId(dockUT, transferTargetVesselPid),
                DockUT = dockUT,
                TransferTargetVesselPid = transferTargetVesselPid,
                TransferKind = transferKind != RouteConnectionKind.None
                    ? transferKind
                    : RouteConnectionKind.DockingPort,
                TransportPartPersistentIds = transportPids,
                EndpointPartPersistentIds = endpointPids,
                DockTransportResources =
                    VesselSpawner.ExtractResourceManifest(dockedSnapshot, transportPids),
                DockEndpointResources =
                    VesselSpawner.ExtractResourceManifest(dockedSnapshot, endpointPids),
                DockTransportInventory =
                    VesselSpawner.ExtractInventoryPayloadItems(dockedSnapshot, transportPids),
                DockEndpointInventory =
                    VesselSpawner.ExtractInventoryPayloadItems(dockedSnapshot, endpointPids),
                EndpointAtDock = endpointAtDock,
                TransferEndpointSituation = transferEndpointSituation
            };

            ParsekLog.Verbose("Flight",
                $"Route window dock capture: window={window.WindowId} " +
                $"targetPid={transferTargetVesselPid} transportParts={transportPids.Count} " +
                $"endpointParts={endpointPids.Count} transportRes={window.DockTransportResources?.Count ?? 0} " +
                $"endpointRes={window.DockEndpointResources?.Count ?? 0} " +
                $"transportInv={window.DockTransportInventory?.Count ?? 0} " +
                $"endpointInv={window.DockEndpointInventory?.Count ?? 0}");

            return window;
        }

        internal static bool TryCompleteLatestRouteConnectionWindow(
            Recording recording,
            double undockUT,
            params ConfigNode[] undockSnapshots)
        {
            if (recording?.RouteConnectionWindows == null ||
                recording.RouteConnectionWindows.Count == 0)
            {
                ParsekLog.Verbose("Flight",
                    "Route window undock completion skipped: recording has no connection windows");
                return false;
            }

            for (int i = recording.RouteConnectionWindows.Count - 1; i >= 0; i--)
            {
                RouteConnectionWindow window = recording.RouteConnectionWindows[i];
                if (window == null || window.IsComplete)
                    continue;

                bool completed = CompleteRouteConnectionWindowAtUndock(
                    window,
                    undockUT,
                    undockSnapshots);
                if (completed)
                    recording.MarkFilesDirty();
                return completed;
            }

            ParsekLog.Verbose("Flight",
                $"Route window undock completion skipped: recording={recording.RecordingId ?? "<none>"} " +
                "has no incomplete window");
            return false;
        }

        internal static bool CompleteRouteConnectionWindowAtUndock(
            RouteConnectionWindow window,
            double undockUT,
            params ConfigNode[] undockSnapshots)
        {
            if (window == null || window.TransportPartPersistentIds == null ||
                window.EndpointPartPersistentIds == null)
            {
                ParsekLog.Warn("Flight",
                    "Route window undock completion failed: missing window or part PID sets");
                return false;
            }

            if (!TryVerifyRoutePartSetsSeparated(
                    undockSnapshots,
                    window.TransportPartPersistentIds,
                    window.EndpointPartPersistentIds,
                    out int transportSnapshotCount,
                    out int endpointSnapshotCount,
                    out bool sawOverlap))
            {
                ParsekLog.Warn("Flight",
                    $"Route window undock completion failed: split snapshots do not separate " +
                    $"transport/endpoint part PID sets window={window.WindowId ?? "<none>"} " +
                    $"targetPid={window.TransferTargetVesselPid} snapshots={undockSnapshots?.Length ?? 0} " +
                    $"transportSnapshots={transportSnapshotCount} endpointSnapshots={endpointSnapshotCount} " +
                    $"overlap={sawOverlap}");
                return false;
            }

            window.UndockUT = undockUT;
            window.UndockTransportResources = ExtractResourceManifestFromSnapshots(
                undockSnapshots,
                window.TransportPartPersistentIds);
            window.UndockEndpointResources = ExtractResourceManifestFromSnapshots(
                undockSnapshots,
                window.EndpointPartPersistentIds);
            window.UndockTransportInventory = ExtractInventoryPayloadItemsFromSnapshots(
                undockSnapshots,
                window.TransportPartPersistentIds);
            window.UndockEndpointInventory = ExtractInventoryPayloadItemsFromSnapshots(
                undockSnapshots,
                window.EndpointPartPersistentIds);

            ParsekLog.Verbose("Flight",
                $"Route window undock capture: window={window.WindowId ?? "<none>"} " +
                $"targetPid={window.TransferTargetVesselPid} " +
                $"transportRes={window.UndockTransportResources?.Count ?? 0} " +
                $"endpointRes={window.UndockEndpointResources?.Count ?? 0} " +
                $"transportInv={window.UndockTransportInventory?.Count ?? 0} " +
                $"endpointInv={window.UndockEndpointInventory?.Count ?? 0}");

            return true;
        }

        private static bool TryVerifyRoutePartSetsSeparated(
            ConfigNode[] snapshots,
            ICollection<uint> transportPartPersistentIds,
            ICollection<uint> endpointPartPersistentIds,
            out int transportSnapshotCount,
            out int endpointSnapshotCount,
            out bool sawOverlap)
        {
            transportSnapshotCount = 0;
            endpointSnapshotCount = 0;
            sawOverlap = false;

            if (snapshots == null ||
                transportPartPersistentIds == null || transportPartPersistentIds.Count == 0 ||
                endpointPartPersistentIds == null || endpointPartPersistentIds.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < snapshots.Length; i++)
            {
                bool hasTransport = SnapshotContainsAnyPartPersistentId(
                    snapshots[i],
                    transportPartPersistentIds);
                bool hasEndpoint = SnapshotContainsAnyPartPersistentId(
                    snapshots[i],
                    endpointPartPersistentIds);

                if (hasTransport && hasEndpoint)
                {
                    sawOverlap = true;
                    return false;
                }
                if (hasTransport)
                    transportSnapshotCount++;
                if (hasEndpoint)
                    endpointSnapshotCount++;
            }

            return transportSnapshotCount == 1 && endpointSnapshotCount == 1;
        }

        private static bool SnapshotContainsAnyPartPersistentId(
            ConfigNode snapshot,
            ICollection<uint> partPersistentIds)
        {
            if (snapshot == null || partPersistentIds == null || partPersistentIds.Count == 0)
                return false;

            ConfigNode[] parts = snapshot.GetNodes("PART");
            for (int i = 0; i < parts.Length; i++)
            {
                if (VesselSpawner.TryGetPartPersistentId(parts[i], out uint pid) &&
                    partPersistentIds.Contains(pid))
                {
                    return true;
                }
            }

            return false;
        }

        private static string BuildWindowId(double dockUT, uint transferTargetVesselPid)
        {
            return "dock-" + dockUT.ToString("R", CultureInfo.InvariantCulture)
                + "-target-" + transferTargetVesselPid.ToString(CultureInfo.InvariantCulture);
        }

        private static Dictionary<string, ResourceAmount> ExtractResourceManifestFromSnapshots(
            ConfigNode[] snapshots,
            ICollection<uint> partPersistentIds)
        {
            if (snapshots == null || partPersistentIds == null || partPersistentIds.Count == 0)
                return null;

            Dictionary<string, ResourceAmount> merged = null;
            for (int i = 0; i < snapshots.Length; i++)
            {
                Dictionary<string, ResourceAmount> manifest =
                    VesselSpawner.ExtractResourceManifest(snapshots[i], partPersistentIds);
                if (manifest == null || manifest.Count == 0)
                    continue;

                if (merged == null)
                    merged = new Dictionary<string, ResourceAmount>();
                MergeResourceManifest(merged, manifest);
            }

            return merged != null && merged.Count > 0 ? merged : null;
        }

        private static List<InventoryPayloadItem> ExtractInventoryPayloadItemsFromSnapshots(
            ConfigNode[] snapshots,
            ICollection<uint> partPersistentIds)
        {
            if (snapshots == null || partPersistentIds == null || partPersistentIds.Count == 0)
                return null;

            Dictionary<string, InventoryPayloadItem> merged = null;
            for (int i = 0; i < snapshots.Length; i++)
            {
                List<InventoryPayloadItem> items =
                    VesselSpawner.ExtractInventoryPayloadItems(snapshots[i], partPersistentIds);
                if (items == null || items.Count == 0)
                    continue;

                if (merged == null)
                    merged = new Dictionary<string, InventoryPayloadItem>();
                MergeInventoryPayloadItems(merged, items);
            }

            if (merged == null || merged.Count == 0)
                return null;

            var list = new List<InventoryPayloadItem>(merged.Values);
            list.Sort((a, b) => string.Compare(a.IdentityHash, b.IdentityHash, StringComparison.Ordinal));
            return list;
        }

        private static void MergeResourceManifest(
            Dictionary<string, ResourceAmount> target,
            Dictionary<string, ResourceAmount> source)
        {
            foreach (KeyValuePair<string, ResourceAmount> kvp in source)
            {
                if (target.TryGetValue(kvp.Key, out ResourceAmount existing))
                {
                    existing.amount += kvp.Value.amount;
                    existing.maxAmount += kvp.Value.maxAmount;
                    target[kvp.Key] = existing;
                }
                else
                {
                    target[kvp.Key] = kvp.Value;
                }
            }
        }

        private static void MergeInventoryPayloadItems(
            Dictionary<string, InventoryPayloadItem> target,
            List<InventoryPayloadItem> source)
        {
            for (int i = 0; i < source.Count; i++)
            {
                InventoryPayloadItem item = source[i];
                if (item == null || string.IsNullOrEmpty(item.IdentityHash))
                    continue;

                if (target.TryGetValue(item.IdentityHash, out InventoryPayloadItem existing))
                {
                    existing.Quantity += item.Quantity;
                    existing.SlotsTaken += item.SlotsTaken;
                }
                else
                {
                    target[item.IdentityHash] = item.DeepClone();
                }
            }
        }

        private static List<uint> DeriveEndpointPartPids(
            ConfigNode dockedSnapshot,
            List<uint> transportPartPersistentIds)
        {
            List<uint> allPids = VesselSpawner.CollectPartPersistentIds(dockedSnapshot);
            if (allPids == null || allPids.Count == 0)
                return null;

            var endpoint = new List<uint>();
            for (int i = 0; i < allPids.Count; i++)
            {
                if (!transportPartPersistentIds.Contains(allPids[i]))
                    endpoint.Add(allPids[i]);
            }

            return NormalizePartPids(endpoint);
        }

        private static List<uint> NormalizePartPids(ICollection<uint> source)
        {
            if (source == null || source.Count == 0)
                return null;

            var pids = new List<uint>();
            foreach (uint pid in source)
            {
                if (pid == 0 || pids.Contains(pid))
                    continue;
                pids.Add(pid);
            }

            if (pids.Count == 0)
                return null;

            pids.Sort();
            return pids;
        }
    }
}
