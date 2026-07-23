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
        // Origin endpoint descriptor (M1): the parent vessel's body + body-fixed
        // coordinates at recording start, so RouteBuilder can build a surface-typed
        // origin endpoint that gets the same proximity rebuild fallback destinations have.
        public readonly string ParentVesselBodyName;
        public readonly double ParentVesselLatitude;
        public readonly double ParentVesselLongitude;
        public readonly double ParentVesselAltitude;

        internal OriginPartnerCandidate(
            uint partPersistentId,
            uint parentVesselPersistentId,
            int parentVesselSituation,
            string parentVesselBodyName,
            double parentVesselLatitude,
            double parentVesselLongitude,
            double parentVesselAltitude)
        {
            PartPersistentId = partPersistentId;
            ParentVesselPersistentId = parentVesselPersistentId;
            ParentVesselSituation = parentVesselSituation;
            ParentVesselBodyName = parentVesselBodyName;
            ParentVesselLatitude = parentVesselLatitude;
            ParentVesselLongitude = parentVesselLongitude;
            ParentVesselAltitude = parentVesselAltitude;
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
        /// Pure / static. True when the partner situation pins the origin to the
        /// surface: LANDED or SPLASHED. Mirrors
        /// <c>RouteEndpointResolver.IsSurfaceSituation</c> minus PRELAUNCH, which
        /// <see cref="TryResolveStartDockedOriginPartner"/> already excludes for
        /// partners.
        /// </summary>
        internal static bool IsSurfaceOriginSituation(int situation)
        {
            return situation == (int)Vessel.Situations.LANDED
                || situation == (int)Vessel.Situations.SPLASHED;
        }

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

                    // The resolver returns only the partner pid, so recover the matched
                    // candidate's origin descriptor by scanning for the first non-PRELAUNCH
                    // entry with the same parent pid (duplicates share one parent vessel,
                    // hence identical descriptors). Always present on the Captured branch;
                    // the guard is defensive.
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        OriginPartnerCandidate c = candidates[i];
                        if (c.ParentVesselPersistentId != partnerPid)
                            continue;
                        if (c.ParentVesselSituation == (int)Vessel.Situations.PRELAUNCH)
                            continue;
                        proof.StartDockedOriginBodyName = c.ParentVesselBodyName;
                        proof.StartDockedOriginLatitude = c.ParentVesselLatitude;
                        proof.StartDockedOriginLongitude = c.ParentVesselLongitude;
                        proof.StartDockedOriginAltitude = c.ParentVesselAltitude;
                        proof.StartDockedOriginSituation = c.ParentVesselSituation;
                        proof.StartDockedOriginIsSurface = IsSurfaceOriginSituation(c.ParentVesselSituation);
                        break;
                    }

                    ParsekLog.Info("Recorder",
                        $"RouteOriginProof captured: recId={recordingVesselId} vessel='{vesselContext}' " +
                        $"partnerPid={partnerPid} candidates={candidateCount} " +
                        $"transportParts={transportPids?.Count ?? 0} " +
                        $"startRes={startRes?.Count ?? 0} startInv={startInv?.Count ?? 0} " +
                        $"partnerBody={(string.IsNullOrEmpty(proof.StartDockedOriginBodyName) ? "<none>" : proof.StartDockedOriginBodyName)} " +
                        $"partnerSituation={proof.StartDockedOriginSituation.ToString(CultureInfo.InvariantCulture)} " +
                        $"surface={(proof.StartDockedOriginIsSurface ? "1" : "0")}");
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

        /// <summary>
        /// M2 / plan D3 birth discriminator (round-2 BLOCKER 1): the run-manifest
        /// START half is captured iff the tree's active Recording is at its BIRTH
        /// (no prior samples of any kind) AND carries no start half yet (the
        /// write-once guard). The flavor of the recorder start (isPromotion or
        /// not) is deliberately NOT the discriminator - split children, merge
        /// children, and chain-segment births all start with isPromotion:true and
        /// MUST capture, while BG-promotion of an existing recording and quickload
        /// resume must NOT (a re-captured mid-run baseline would fold prior gains
        /// into "start cargo" and bypass the gain check).
        ///
        /// Pure / static / testable. Logging happens at the call site.
        /// </summary>
        internal static bool ShouldCaptureRunManifestStartHalf(Recording treeRecording, out string skipReason)
        {
            skipReason = null;
            if (treeRecording == null)
            {
                skipReason = "no-tree-recording";
                return false;
            }
            // Sticky void tombstone (M2 review follow-up): a leg that voided on
            // a background transition must NEVER re-capture, even when it still
            // looks "at birth" (the void can land before the first sample is
            // flushed onto the tree recording). Fail-closed to legacy.
            if (treeRecording.RunManifestVoided)
            {
                skipReason = "manifest-voided";
                return false;
            }
            if (treeRecording.RouteRunManifest != null && treeRecording.RouteRunManifest.HasStartHalf)
            {
                skipReason = "start-half-already-captured";
                return false;
            }
            bool hasPoints = treeRecording.Points != null && treeRecording.Points.Count > 0;
            bool hasOrbitSegments = treeRecording.OrbitSegments != null && treeRecording.OrbitSegments.Count > 0;
            bool hasTrackSections = treeRecording.TrackSections != null && treeRecording.TrackSections.Count > 0;
            if (hasPoints || hasOrbitSegments || hasTrackSections)
            {
                skipReason = "not-at-birth";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Builds the START half of a <see cref="RouteRunCargoManifest"/> from the
        /// recording-start vessel snapshot (M2 / plan D3). Scope = the snapshot's
        /// full part-pid set, identical to the start-docked origin proof's scope
        /// rule. Capture stays PERMISSIVE (plan D2): whatever resource names the
        /// snapshot carries are recorded; undefined-name exclusion happens at
        /// analysis only. Returns null (with a log mirroring the
        /// RouteOriginProof skip branches) for gloops mode, a missing snapshot,
        /// or a snapshot with no usable part pids.
        /// </summary>
        internal static RouteRunCargoManifest BuildRunCargoManifestAtStart(
            ConfigNode snapshot,
            bool isGloopsMode,
            string vesselContext,
            uint recordingVesselId)
        {
            if (isGloopsMode)
            {
                ParsekLog.Verbose("Recorder",
                    $"RouteRunManifest skipped: gloops mode recId={recordingVesselId} vessel='{vesselContext}'");
                return null;
            }
            if (snapshot == null)
            {
                ParsekLog.Warn("Recorder",
                    $"RouteRunManifest skipped: no last good snapshot recId={recordingVesselId} " +
                    $"vessel='{vesselContext}'");
                return null;
            }

            List<uint> transportPids = VesselSpawner.CollectPartPersistentIds(snapshot);
            if (transportPids == null || transportPids.Count == 0)
            {
                ParsekLog.Warn("Recorder",
                    $"RouteRunManifest skipped: snapshot has no part pids recId={recordingVesselId} " +
                    $"vessel='{vesselContext}'");
                return null;
            }

            Dictionary<string, ResourceAmount> startRes =
                VesselSpawner.ExtractResourceManifest(snapshot, transportPids);
            // Empty -> null normalization (M2 review follow-up): the codec
            // drops empty manifests on save (reload yields null) while the
            // hasher emits ".count=0" for an empty dict - an empty-but-non-null
            // manifest would therefore flip the hash after one save/load and
            // mark every route built from it SourceChanged.
            if (startRes != null && startRes.Count == 0)
                startRes = null;

            var manifest = new RouteRunCargoManifest
            {
                TransportPartPersistentIds = transportPids,
                StartTransportResources = startRes,
            };

            ParsekLog.Verbose("Recorder",
                $"RouteRunManifest start: recId={recordingVesselId} vessel='{vesselContext}' " +
                $"parts={transportPids.Count} res={startRes?.Count ?? 0}");
            return manifest;
        }

        /// <summary>
        /// Completes the END half of the pending run manifest at an ACTIVE stop
        /// and forwards a deep clone onto the captured Recording (M2 / plan D3
        /// rule 4). The END manifest is extracted from
        /// <c>capture.VesselSnapshot</c> scoped to the START pid set - NEVER from
        /// a live vessel walk at the stop frame (at the dock pid-change stop the
        /// live vessel is the merged stack and same-frame crossfeed equalization
        /// deflates values; mirrors
        /// <see cref="AttachEndManifestsAndForwardToCapture"/>). The END half is
        /// overwrite-per-active-stop: a chain-boundary stop abandoned by
        /// ResumeAfterFalseAlarm has already completed an END half, and the
        /// eventual real stop must replace it or post-resume drilling
        /// double-counts. No-op when either input is null, or when the capture
        /// carries NO vessel snapshot (M2 review follow-up): completing
        /// against a null snapshot would stamp EndCaptured with a null END that
        /// reads as "complete, resource-less" and inflates the next leg's
        /// bridge delta - leave the manifest start-only instead (degrades to
        /// legacy via the presence gate).
        /// </summary>
        internal static void CompleteRunCargoManifestAtStop(
            Recording capture,
            RouteRunCargoManifest pending)
        {
            if (capture == null || pending == null)
                return;

            if (capture.VesselSnapshot == null)
            {
                ParsekLog.Verbose("Recorder",
                    $"RouteRunManifest end skipped: no capture snapshot " +
                    $"recording={capture.RecordingId ?? "<none>"} (manifest stays start-only)");
                return;
            }

            bool overwrite = pending.EndCaptured;
            Dictionary<string, ResourceAmount> endRes = VesselSpawner.ExtractResourceManifest(
                capture.VesselSnapshot,
                pending.TransportPartPersistentIds);
            // Empty -> null normalization: same hash-stability contract as the
            // START half (the codec drops empty manifests on save).
            if (endRes != null && endRes.Count == 0)
                endRes = null;
            pending.EndTransportResources = endRes;
            pending.EndCaptured = true;
            capture.RouteRunManifest = pending.DeepClone();

            ParsekLog.Verbose("Recorder",
                $"RouteRunManifest end: recording={capture.RecordingId ?? "<none>"} " +
                $"startRes={pending.StartTransportResources?.Count ?? 0} " +
                $"endRes={pending.EndTransportResources?.Count ?? 0} " +
                $"overwrite={(overwrite ? "1" : "0")}");
        }

        /// <summary>
        /// Voids the active tree recording's run manifest on a background
        /// transition (M2 / plan D3 rule 3): the END half of a BG-transiting leg
        /// can never be captured trustworthily, and a voided manifest makes the
        /// analysis presence gate degrade that tree to legacy behavior. ALWAYS
        /// stamps the sticky <see cref="Recording.RunManifestVoided"/> tombstone
        /// (M2 review follow-up) - even when no manifest was captured yet -
        /// so a BG-transited leg that still looks "at birth" can never
        /// re-capture a mid-life START baseline on promotion. Returns true when
        /// a manifest was actually cleared. Warn-logged per the plan's logging
        /// table; tombstone-only marks log Verbose.
        /// </summary>
        internal static bool VoidRunManifestForBackgroundTransition(
            RecordingTree tree,
            string activeRecordingId)
        {
            if (tree == null || string.IsNullOrEmpty(activeRecordingId)
                || tree.Recordings == null
                || !tree.Recordings.TryGetValue(activeRecordingId, out Recording treeRec)
                || treeRec == null)
            {
                return false;
            }

            bool tombstoneNewlySet = !treeRec.RunManifestVoided;
            treeRec.RunManifestVoided = true;

            if (treeRec.RouteRunManifest == null)
            {
                if (tombstoneNewlySet)
                {
                    treeRec.MarkFilesDirty();
                    ParsekLog.Verbose("Recorder",
                        $"RouteRunManifest void tombstone set: recording={activeRecordingId} " +
                        $"reason=background-transition (no manifest captured yet)");
                }
                return false;
            }

            treeRec.RouteRunManifest = null;
            treeRec.MarkFilesDirty();
            ParsekLog.Warn("Recorder",
                $"RouteRunManifest voided: recording={activeRecordingId} reason=background-transition");
            return true;
        }

        internal static RouteConnectionWindow BuildDockRouteConnectionWindow(
            double dockUT,
            uint transferTargetVesselPid,
            RouteConnectionKind transferKind,
            ConfigNode dockedSnapshot,
            ICollection<uint> transportPartPersistentIds,
            ICollection<uint> endpointPartPersistentIds,
            RouteEndpoint? endpointAtDock,
            int transferEndpointSituation,
            ConfigNode endpointPreCoupleSnapshot = null,
            ConfigNode transportPreCoupleSnapshot = null)
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

            // When the caller provides a pre-couple endpoint snapshot, prefer it for the
            // endpoint baseline. Falling back to the merged-vessel snapshot would inflate
            // DOCK_ENDPOINT_RESOURCES with the transport's contribution because the
            // endpoint-part-PID set may include transport parts after a post-couple
            // FindVesselByPid lookup returned the merged vessel.
            ConfigNode endpointSnapshotForBaseline = endpointPreCoupleSnapshot != null
                ? endpointPreCoupleSnapshot
                : dockedSnapshot;

            // Symmetrically, when the caller provides a pre-couple TRANSPORT snapshot,
            // prefer it for the transport baseline. The merged-vessel snapshot is captured
            // frames after the couple, so any same-frame stock crossfeed equalisation that
            // drained the transport tank into the depot deflates DOCK_TRANSPORT_RESOURCES;
            // a later undock reading then looks like a pickup and trips the strict
            // MixedPickupDelivery gate on an otherwise clean delivery run. The selection is
            // self-validating: a pre-couple snapshot is only used when it actually contains
            // the transport part PID set, so a stale / mismatched snapshot can never produce
            // a wrong manifest (it falls back to the merged snapshot, current behaviour).
            ConfigNode transportSnapshotForBaseline =
                (transportPreCoupleSnapshot != null
                 && SnapshotContainsAnyPartPersistentId(transportPreCoupleSnapshot, transportPids))
                    ? transportPreCoupleSnapshot
                    : dockedSnapshot;

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
                    VesselSpawner.ExtractResourceManifest(transportSnapshotForBaseline, transportPids),
                DockEndpointResources =
                    VesselSpawner.ExtractResourceManifest(endpointSnapshotForBaseline, endpointPids),
                DockTransportInventory =
                    VesselSpawner.ExtractInventoryPayloadItems(transportSnapshotForBaseline, transportPids),
                DockEndpointInventory =
                    VesselSpawner.ExtractInventoryPayloadItems(endpointSnapshotForBaseline, endpointPids),
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

            // Observational warning when part configuration drifted during the
            // docked window (EVA construction etc.). Doesn't reject the route —
            // disjoint verifier already passed and resource accounting still works
            // for the originally-listed parts. Stock fuel/inventory transfers
            // don't trip this; only outer part-set changes do.
            LogRoutePartSetEqualityWarnings(
                undockSnapshots,
                window.TransportPartPersistentIds,
                window.EndpointPartPersistentIds,
                window.WindowId);

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

            // Route-window per-resource delta observability (MAJOR 6 / the B-DOCK
            // headline payoff): the recorded net cargo per side (Undock* - Dock*).
            // Info (not Verbose): this is the single observable surface the offline
            // oracle checks the commanded LF/MP transfers against, so it must survive
            // the default log level. Endpoint side is conservation-mirrored.
            ParsekLog.Info("Flight",
                $"Route window delta: window={window.WindowId ?? "<none>"} " +
                $"targetPid={window.TransferTargetVesselPid} " +
                $"transportDelta=[{FormatRouteResourceDelta(window.DockTransportResources, window.UndockTransportResources)}] " +
                $"endpointDelta=[{FormatRouteResourceDelta(window.DockEndpointResources, window.UndockEndpointResources)}]");

            return true;
        }

        /// <summary>
        /// Formats the per-resource net delta (undock - dock) for one route-window side
        /// as a stable ASCII token string, e.g. "LiquidFuel=+40.0 MonoPropellant=-15.0".
        /// Positive = the side GAINED the resource across the docked window; negative =
        /// it lost it. Sorted by resource name (ordinal) for byte-stable output;
        /// "(none)" when there is no delta. Pure / static / testable (the MAJOR-6
        /// route-window delta observability surface asserted by the B-DOCK logContract).
        /// </summary>
        internal static string FormatRouteResourceDelta(
            Dictionary<string, ResourceAmount> dockManifest,
            Dictionary<string, ResourceAmount> undockManifest)
        {
            Dictionary<string, double> delta =
                ResourceManifest.ComputeResourceDelta(dockManifest, undockManifest);
            if (delta == null || delta.Count == 0)
                return "(none)";

            var keys = new List<string>(delta.Keys);
            keys.Sort(StringComparer.Ordinal);
            var parts = new List<string>(keys.Count);
            foreach (string key in keys)
            {
                double d = delta[key];
                parts.Add(string.Format(CultureInfo.InvariantCulture, "{0}={1}{2:F1}",
                    key, d >= 0 ? "+" : string.Empty, d));
            }
            return string.Join(" ", parts.ToArray());
        }

        /// <summary>
        /// Compares an actual part-PID set (captured from a post-undock vessel half)
        /// against the pre-dock expected set. Returns the symmetric difference split
        /// into added (in actual, not in expected) and removed (in expected, not in
        /// actual). Pure / static / testable.
        /// </summary>
        internal static void ComputePartSetDifferences(
            IEnumerable<uint> actualPartPids,
            IEnumerable<uint> expectedPartPids,
            out List<uint> addedPids,
            out List<uint> removedPids)
        {
            addedPids = new List<uint>();
            removedPids = new List<uint>();

            HashSet<uint> actual = actualPartPids != null
                ? new HashSet<uint>(actualPartPids)
                : new HashSet<uint>();
            HashSet<uint> expected = expectedPartPids != null
                ? new HashSet<uint>(expectedPartPids)
                : new HashSet<uint>();

            foreach (uint pid in actual)
            {
                if (!expected.Contains(pid)) addedPids.Add(pid);
            }
            foreach (uint pid in expected)
            {
                if (!actual.Contains(pid)) removedPids.Add(pid);
            }

            addedPids.Sort();
            removedPids.Sort();
        }

        /// <summary>
        /// After the disjoint-set verifier accepts an undock split, walks each
        /// snapshot and warns if its part-PID set is not equal to the expected
        /// pre-dock set. The disjoint verifier is the route-eligibility gate (no
        /// transport/endpoint overlap); this warning is observational — it surfaces
        /// part configuration drift during the docked window (e.g. EVA construction
        /// added or removed parts) without rejecting the route. Stock fuel/inventory
        /// transfers do NOT trip these warnings because they don't change either
        /// side's outer part-PID set.
        /// </summary>
        internal static void LogRoutePartSetEqualityWarnings(
            ConfigNode[] snapshots,
            ICollection<uint> transportPartPersistentIds,
            ICollection<uint> endpointPartPersistentIds,
            string windowId)
        {
            if (snapshots == null) return;
            if (transportPartPersistentIds == null || endpointPartPersistentIds == null) return;

            for (int i = 0; i < snapshots.Length; i++)
            {
                ConfigNode snapshot = snapshots[i];
                if (snapshot == null) continue;

                bool hasTransport = SnapshotContainsAnyPartPersistentId(
                    snapshot, transportPartPersistentIds);
                bool hasEndpoint = SnapshotContainsAnyPartPersistentId(
                    snapshot, endpointPartPersistentIds);
                if (hasTransport == hasEndpoint) continue; // both/neither — disjoint verifier filtered

                ICollection<uint> expected = hasTransport
                    ? transportPartPersistentIds
                    : endpointPartPersistentIds;
                string sideLabel = hasTransport ? "transport" : "endpoint";

                List<uint> actual = VesselSpawner.CollectPartPersistentIds(snapshot)
                    ?? new List<uint>();
                ComputePartSetDifferences(
                    actual,
                    expected,
                    out List<uint> addedPids,
                    out List<uint> removedPids);

                if (addedPids.Count == 0 && removedPids.Count == 0) continue;

                ParsekLog.Warn("Flight",
                    $"Route window part-set drift on undock side='{sideLabel}' " +
                    $"window={windowId ?? "<none>"} " +
                    $"expected={expected.Count} actual={actual.Count} " +
                    $"added={FormatPidList(addedPids)} removed={FormatPidList(removedPids)} " +
                    "(disjoint check still passed — route eligibility unchanged; investigate if " +
                    "ghost replay or resource accounting looks wrong)");
            }
        }

        private static string FormatPidList(List<uint> pids)
        {
            if (pids == null || pids.Count == 0) return "[]";
            var sb = new System.Text.StringBuilder();
            sb.Append('[');
            for (int i = 0; i < pids.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(pids[i].ToString(CultureInfo.InvariantCulture));
            }
            sb.Append(']');
            return sb.ToString();
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
