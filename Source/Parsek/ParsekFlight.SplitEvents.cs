using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    // Split event handlers: dock, undock, EVA, board and joint-break GameEvents, OnPartCouple and
    // OnVesselsUndocking, and the per-frame split helpers Update calls.
    public partial class ParsekFlight
    {
        void OnCrewBoardVessel(GameEvents.FromToAction<Part, Part> data)
        {
            RecorderStateLog.RecState("OnCrewBoardVessel:entry", CaptureRecorderState());
            if (activeTree == null)
            {
                ParsekLog.Verbose("Flight", "OnCrewBoardVessel: no active tree — ignoring");
                return;
            }
            if (pendingSplitInProgress)
            {
                LogSplitSkip(
                    "OnCrewBoardVessel",
                    "pendingSplitInProgress",
                    data.from?.vessel?.persistentId ?? 0u,
                    data.to?.vessel?.persistentId ?? 0u);
                return; // split must complete first
            }
            if (data.to?.vessel == null)
            {
                LogSplitSkip(
                    "OnCrewBoardVessel",
                    "target-vessel-null",
                    data.from?.vessel?.persistentId ?? 0u,
                    0u);
                ParsekLog.Verbose("Flight", "OnCrewBoardVessel: target vessel is null — ignoring");
                return;
            }

            pendingBoardingTargetPid = data.to.vessel.persistentId;
            boardingConfirmFrames = 0;

            // Store whether the boarding target is in the tree
            pendingBoardingTargetInTree = (activeTree != null &&
                activeTree.BackgroundMap.ContainsKey(data.to.vessel.persistentId));

            Log($"onCrewBoardVessel: target vessel pid={pendingBoardingTargetPid}" +
                (activeTree != null ? $", inTree={pendingBoardingTargetInTree}" : ""));

            TryPromoteEvaRecordingForBoardMerge(data.from?.vessel);
        }

        /// <summary>
        /// Rebinds the live recorder to a boarding kerbal's EVA branch recording when that
        /// recording is still only background-tracked, so the board merge can run.
        /// </summary>
        /// <remarks>
        /// An EVA branch parks the kerbal's recording in <c>BackgroundMap</c> and leaves the
        /// promotion to the post-switch first-modification watcher. A kerbal that exits and
        /// re-boards without moving (hatch/ladder hop, no release) never trips a trigger, so
        /// the watcher is still armed when the board arrives: the boarding detection in
        /// <see cref="OnVesselSwitchComplete"/> requires <c>recorder.IsRecording</c> and
        /// <see cref="HandleTreeBoardMerge"/> requires the EVA recording to be
        /// <c>activeTree.ActiveRecordingId</c>, so without this rebind the Board branch
        /// point is silently dropped and the EVA recording terminates as Destroyed instead
        /// of Boarded. onCrewBoardVessel fires before KSP's focus switch, so the EVA kerbal
        /// is still the active vessel here and the normal promotion path applies.
        /// </remarks>
        private void TryPromoteEvaRecordingForBoardMerge(Vessel evaVessel)
        {
            uint evaPid = evaVessel != null ? evaVessel.persistentId : 0u;
            uint activePid = FlightGlobals.ActiveVessel != null
                ? FlightGlobals.ActiveVessel.persistentId
                : 0u;
            string trackedRecordingId = null;
            bool trackedInBackground = activeTree != null && evaPid != 0
                && activeTree.BackgroundMap.TryGetValue(evaPid, out trackedRecordingId);

            var decision = DecideEvaBoardPromotion(
                hasActiveTree: activeTree != null,
                restoringActiveTree: restoringActiveTree,
                sourceIsEva: evaVessel != null && evaVessel.isEVA,
                sourceIsGhostVessel: evaPid != 0 && GhostMapPresence.IsGhostMapVessel(evaPid),
                sourceVesselPid: evaPid,
                hasLiveRecorder: recorder != null,
                liveRecorderVesselPid: recorder != null ? recorder.RecordingVesselId : 0u,
                activeVesselPid: activePid,
                sourceTrackedInBackground: trackedInBackground,
                trackedRecordingId: trackedRecordingId,
                treeActiveRecordingId: activeTree != null ? activeTree.ActiveRecordingId : null);

            if (decision != EvaBoardPromotionDecision.Promote)
            {
                ParsekLog.Verbose("Flight",
                    $"EVA board promotion skipped: decision={decision} evaPid={evaPid} " +
                    $"activePid={activePid} targetPid={pendingBoardingTargetPid} " +
                    $"tracked={trackedInBackground} " +
                    $"treeActiveRec={activeTree?.ActiveRecordingId ?? "-"} " +
                    $"trackedRec={trackedRecordingId ?? "-"}");
                return;
            }

            ParsekLog.Info("Flight",
                $"EVA board promotion: rebinding background EVA recording " +
                $"'{trackedRecordingId}' to the live recorder before the board merge " +
                $"(evaPid={evaPid}, targetPid={pendingBoardingTargetPid})");

            backgroundRecorder?.OnVesselRemovedFromBackground(evaPid);
            PromoteRecordingFromBackground(trackedRecordingId, evaVessel);

            if (recorder != null && recorder.IsRecording)
                PostSwitchAutoRecorder.DisarmPostSwitchAutoRecord("eva board promotion");
            else
            {
                ParsekLog.Warn("Flight",
                    $"EVA board promotion failed to start a recorder for evaPid={evaPid} - " +
                    $"board merge will be skipped");
            }
        }

        private bool TryStartEvaBranchFromBackgroundParent(GameEvents.FromToAction<Part, Part> data)
        {
            uint sourcePid = data.from?.vessel?.persistentId ?? 0u;
            uint targetPid = data.to?.vessel?.persistentId ?? 0u;

            // Only the structural preconditions matter before we can safely dereference activeTree/sourceVessel.
            var initialDecision = DecideEvaBackgroundParentRoute(
                isRecording: IsRecording,
                pendingSplitInProgress: pendingSplitInProgress,
                hasActiveTree: activeTree != null,
                hasSourceVessel: data.from?.vessel != null,
                trackedParentResolved: false,
                mappedRecordingExists: false,
                hasCrewName: false);

            if (initialDecision == EvaBackgroundParentRouteDecision.NotHandled)
                return false;

            if (initialDecision == EvaBackgroundParentRouteDecision.HandledSkipPendingSplit)
            {
                LogSplitSkip(
                    "OnCrewOnEva",
                    "pendingSplitInProgress",
                    sourcePid,
                    targetPid);
                PostSwitchAutoRecorder.ClearPendingEvaAutoRecordState();
                return true;
            }

            Vessel sourceVessel = data.from?.vessel;
            string parentRecordingId;
            string diagnostic;
            bool trackedParentResolved = TryResolveTrackedBackgroundParentRecording(
                    activeTree,
                    sourceVessel.persistentId,
                    out parentRecordingId,
                    out diagnostic);
            bool mappedRecordingExists = !string.IsNullOrEmpty(parentRecordingId)
                && activeTree.Recordings != null
                && activeTree.Recordings.ContainsKey(parentRecordingId);
            string kerbalName = ExtractEvaKerbalName(data);

            var decision = DecideEvaBackgroundParentRoute(
                isRecording: false,
                pendingSplitInProgress: false,
                hasActiveTree: true,
                hasSourceVessel: true,
                trackedParentResolved: trackedParentResolved,
                mappedRecordingExists: mappedRecordingExists,
                hasCrewName: !string.IsNullOrEmpty(kerbalName));

            if (decision == EvaBackgroundParentRouteDecision.HandledUntrackedSource)
            {
                ParsekLog.Warn("Flight",
                    $"OnCrewOnEva: source vessel pid={sourceVessel.persistentId} is not the live recorder " +
                    $"and is not a tracked background parent ({diagnostic}); suppressing deferred auto-record");
                ScreenMessage("Recording blocked (EVA source not tracked)", 2f);
                PostSwitchAutoRecorder.ClearPendingEvaAutoRecordState();
                return true;
            }

            if (decision == EvaBackgroundParentRouteDecision.HandledInvalidMapEntry)
            {
                ParsekLog.Warn("Flight",
                    $"OnCrewOnEva: background parent map entry is invalid " +
                    $"(sourcePid={sourceVessel.persistentId}, parentRec={parentRecordingId ?? "null"}, " +
                    $"diagnostic={diagnostic}); suppressing deferred auto-record");
                ScreenMessage("Recording blocked (invalid EVA tree state)", 2f);
                PostSwitchAutoRecorder.ClearPendingEvaAutoRecordState();
                return true;
            }

            if (decision == EvaBackgroundParentRouteDecision.HandledMissingCrewName)
            {
                ParsekLog.Warn("Flight",
                    $"OnCrewOnEva: source vessel pid={sourceVessel.persistentId} is tracked in background " +
                    "but EVA kerbal name could not be extracted; suppressing deferred auto-record");
                ScreenMessage("Recording blocked (EVA crew unknown)", 2f);
                PostSwitchAutoRecorder.ClearPendingEvaAutoRecordState();
                return true;
            }

            if (decision != EvaBackgroundParentRouteDecision.StartDeferredBackgroundParentBranch)
                return false;

            PostSwitchAutoRecorder.ClearPendingEvaAutoRecordState();
            pendingSplitInProgress = true;
            Log($"Background-parent EVA detected: '{kerbalName}' from pid={sourceVessel.persistentId} " +
                $"parentRec={parentRecordingId} - starting deferred tree branch");
            StartCoroutine(DeferredEvaBranchFromBackgroundParent(
                parentRecordingId,
                sourceVessel.persistentId,
                kerbalName,
                data));
            return true;
        }

        void OnCrewOnEva(GameEvents.FromToAction<Part, Part> data)
        {
            RecorderStateLog.RecState("OnCrewOnEva:entry", CaptureRecorderState());
            // Mid-recording EVA: tree branch (replaces legacy chain continuation)
            if (IsRecording)
            {
                if (pendingSplitInProgress)
                {
                    LogSplitSkip(
                        "OnCrewOnEva",
                        "pendingSplitInProgress",
                        data.from?.vessel?.persistentId ?? 0u,
                        data.to?.vessel?.persistentId ?? 0u);
                    return; // another split is being processed
                }

                double evaEventUT = Planetarium.GetUniversalTime();
                var evaInvolved = new List<Vessel>(2);
                if (data.from?.vessel != null) evaInvolved.Add(data.from.vessel);
                if (data.to?.vessel != null && data.to.vessel != data.from?.vessel)
                    evaInvolved.Add(data.to.vessel);
                backgroundRecorder?.AppendStructuralEventSnapshot(evaEventUT, evaInvolved, "EVA");

                // Only trigger if EVA is from the vessel we're recording
                if (data.from?.vessel == null ||
                    data.from.vessel.persistentId != recorder.RecordingVesselId)
                {
                    LogSplitSkip(
                        "OnCrewOnEva",
                        data.from?.vessel == null ? "source-vessel-null" : "source-vessel-mismatch",
                        data.from?.vessel?.persistentId ?? 0u,
                        data.to?.vessel?.persistentId ?? 0u);
                    return;
                }

                string kerbalName = ExtractEvaKerbalName(data);
                if (string.IsNullOrEmpty(kerbalName))
                {
                    Log("EVA during recording but could not extract kerbal name — ignoring");
                    return;
                }

                // Phase 9 (design doc §12, §18 Phase 9): structural-event snapshot at
                // the exact EVA UT for the recorded vessel (the parent capsule). The
                // EVA kerbal-as-vessel is also "involved" per §12; AppendStructuralEventSnapshot
                // dedups by RecordingVesselId so only the parent capsule's snapshot is
                // committed in the typical case where the parent is the recorded vessel.
                recorder.AppendStructuralEventSnapshot(evaEventUT, evaInvolved, "EVA");

                // Tree mode: stop recorder synchronously, defer tree branch creation
                recorder.StopRecordingForChainBoundary();
                pendingSplitRecorder = recorder;
                recorder = null;
                pendingSplitInProgress = true;
                Log($"Mid-recording EVA detected: '{kerbalName}' — starting deferred tree branch");
                StartCoroutine(DeferredEvaBranch(kerbalName, data));
                return;
            }

            if (!IsRecording && TryStartEvaBranchFromBackgroundParent(data))
                return;

            bool hasSourceVessel = data.from?.vessel != null;
            if (!ParsekFlightPostSwitchAutoRecord.ShouldQueueAutoRecordOnEva(
                hasSourceVessel,
                autoRecordOnEvaEnabled: ParsekSettings.Current?.autoRecordOnEva != false))
            {
                if (!hasSourceVessel)
                {
                    LogSplitSkip(
                        "OnCrewOnEva",
                        "source-vessel-null",
                        0u,
                        data.to?.vessel?.persistentId ?? 0u);
                    ParsekLog.Verbose("Flight", "OnCrewOnEva: source vessel is null — ignoring");
                }
                else
                    ParsekLog.Verbose("Flight", "OnCrewOnEva: auto-record on EVA disabled in settings");
                return;
            }

            // The EVA kerbal may not yet be the active vessel, defer to Update()
            pendingAutoRecord = true;
            Log($"EVA detected (sit={data.from.vessel.situation}) — pending auto-record");
        }

        private void LogSplitSkip(string source, string reason, uint sourcePid, uint targetPid)
        {
            string activeRecordingId = activeTree?.ActiveRecordingId;
            string identity = string.Format(CultureInfo.InvariantCulture,
                "split-skip-{0}-{1}-{2}-{3}",
                string.IsNullOrEmpty(source) ? "unknown" : source,
                string.IsNullOrEmpty(activeRecordingId) ? "none" : activeRecordingId,
                sourcePid,
                targetPid);
            string stateKey = string.Format(CultureInfo.InvariantCulture,
                "{0}|pending={1}",
                string.IsNullOrEmpty(reason) ? "unspecified" : reason,
                pendingSplitInProgress ? 1 : 0);
            ParsekLog.VerboseOnChange("Flight",
                identity,
                stateKey,
                ParsekFlightPostSwitchAutoRecord.FormatSplitSkipSummary(
                    source,
                    reason,
                    activeRecordingId,
                    sourcePid,
                    targetPid,
                    pendingSplitInProgress));
        }

        internal static string ExtractEvaKerbalName(GameEvents.FromToAction<Part, Part> data)
        {
            if (data.to == null) return null;
            var crew = data.to.protoModuleCrew;
            if (crew != null && crew.Count > 0) return crew[0].name;
            if (data.to.vessel != null) return data.to.vessel.vesselName;
            return null;
        }

        void OnPartCouple(GameEvents.FromToAction<Part, Part> data)
        {
            // Vessel names (design 7.7): stock fires onPartCouple from Part.Couple before the
            // merge, with data.from on the vessel being absorbed (ModuleDockingNode.DockToVessel
            // couples its own part to the dominant's). Recorded whatever the recorder state.
            VesselNameLiveSync.OnDockCouple(data.from?.vessel, data.to?.vessel);

            // #267: skip if restore coroutine is mid-yield — it owns activeTree/recorder.
            // OnPartCouple's own null checks (L3784, L3845) already prevent most interference,
            // but the narrow window after recorder.StartRecording in the coroutine could allow
            // a dock event on the just-restored recorder before the coroutine completes.
            if (restoringActiveTree)
            {
                ParsekLog.Warn("Flight",
                    "OnPartCouple: skipped — restore coroutine in progress");
                return;
            }

            RecorderStateLog.RecState("OnPartCouple:entry", CaptureRecorderState());
            if (data.to?.vessel == null) return;
            uint mergedPid = data.to.vessel.persistentId;

            // Classify the connection producer NOW, while both event parts still
            // resolve (design-logistics-claw-producer.md 2.1). A claw couple fires
            // the same onPartCouple as a dock (findings 1.2) but must be stamped
            // Grapple, and an unrecognized (modded) coupling producer is stamped
            // Unknown so route admission fails closed instead of treating it as a
            // dock. An EVA kerbal grab is a real couple but never a logistics
            // transfer: suppress its route window by dropping the route target.
            RouteConnectionKind coupleTransferKind =
                ConnectionProducerClassifier.Classify(data.from, data.to);
            bool coupleInvolvesEva =
                ConnectionProducerClassifier.InvolvesEvaVessel(data.from, data.to);
            ParsekLog.Info("Flight",
                $"OnPartCouple producer classified: kind={coupleTransferKind} " +
                $"fromPart={data.from?.partInfo?.name ?? "<null>"} " +
                $"toPart={data.to?.partInfo?.name ?? "<null>"} involvesEva={coupleInvolvesEva}");

            // Phase 9 (design doc §12, §18 Phase 9): structural-event snapshot at the
            // exact dock UT for the recorded vessel. Done BEFORE
            // StopRecordingForChainBoundary so the snapshot lands in the active section
            // (the helper no-ops once IsRecording flips false). Both vessels in the
            // dock pair are passed; the focused recorder filters by RecordingVesselId
            // and the background recorder filters by BackgroundMap membership.
            //
            // Skip the snapshot when this couple is the abort half of a split-in-
            // progress: the tree-mode path below early-returns at the
            // `pendingSplitInProgress` guard without committing the merge, and a
            // synthetic flagged point would otherwise land in a recording whose
            // section is about to be torn down by the in-flight split. Mirror the
            // exact same guard so the snapshot only fires on couples that the
            // downstream merge path will actually act on.
            if (recorder != null && recorder.IsRecording && !pendingSplitInProgress)
            {
                double dockEventUT = Planetarium.GetUniversalTime();
                var dockInvolved = new List<Vessel>(2);
                if (data.from?.vessel != null) dockInvolved.Add(data.from.vessel);
                if (data.to?.vessel != null && data.to.vessel != data.from?.vessel)
                    dockInvolved.Add(data.to.vessel);
                recorder.AppendStructuralEventSnapshot(dockEventUT, dockInvolved, "Dock");
                backgroundRecorder?.AppendStructuralEventSnapshot(dockEventUT, dockInvolved, "Dock");

                // Capture partner pre-couple snapshot for route window endpoint baseline.
                // When data.from / data.to still reference distinct vessels (KSP has not
                // reparented yet), the partner is the side that isn't the recorder. The
                // route window's DOCK_ENDPOINT_RESOURCES must capture the partner's
                // pre-dock state; falling back to the post-couple merged-vessel snapshot
                // inflates the baseline with the transport's tank.
                pendingDockPartnerSnapshot = null;
                pendingDockPartnerSnapshotPid = 0u;
                pendingDockSelfSnapshot = null;
                pendingDockSelfSnapshotPid = 0u;
                if (data.from?.vessel != null && data.to?.vessel != null &&
                    data.from.vessel != data.to.vessel)
                {
                    Vessel partner = null;
                    if (data.from.vessel.persistentId == recorder.RecordingVesselId
                        && data.to.vessel.persistentId != recorder.RecordingVesselId)
                    {
                        partner = data.to.vessel;
                    }
                    else if (data.to.vessel.persistentId == recorder.RecordingVesselId
                        && data.from.vessel.persistentId != recorder.RecordingVesselId)
                    {
                        partner = data.from.vessel;
                    }
                    else
                    {
                        // Neither side matches the recorder's vessel (rare; e.g. the
                        // recorder was on a third vessel observing). Prefer the side
                        // whose pid will not be the surviving merged pid so the snapshot
                        // captures pre-couple state. data.to is the survivor in KSP.
                        partner = data.from.vessel.persistentId != mergedPid
                            ? data.from.vessel
                            : data.to.vessel;
                    }

                    if (partner != null && partner.parts != null && partner.parts.Count > 0)
                    {
                        pendingDockPartnerSnapshot = FlightRecorder.TryBackupSnapshot(partner);
                        pendingDockPartnerSnapshotPid = partner.persistentId;
                        ParsekLog.Verbose("Flight",
                            $"OnPartCouple: captured pre-couple partner snapshot " +
                            $"partnerPid={pendingDockPartnerSnapshotPid} parts={partner.parts.Count}");
                    }

                    // Capture the recorder's own (transport) pre-couple snapshot for the
                    // route window transport baseline (mirror of the partner snapshot above).
                    Vessel self = ResolveSelfVessel(data, recorder.RecordingVesselId);
                    if (self != null && self.parts != null && self.parts.Count > 0)
                    {
                        pendingDockSelfSnapshot = FlightRecorder.TryBackupSnapshot(self);
                        pendingDockSelfSnapshotPid = self.persistentId;
                        ParsekLog.Verbose("Flight",
                            $"OnPartCouple: captured pre-couple self snapshot " +
                            $"selfPid={pendingDockSelfSnapshotPid} parts={self.parts.Count}");
                    }
                }
            }

            // --- TREE MODE: create merge branch ---
            if (activeTree != null && recorder != null)
            {
                if (pendingSplitInProgress) return; // split must complete first

                if (recorder.IsRecording)
                {
                    bool isTarget = (recorder.RecordingVesselId == mergedPid);
                    uint absorbedPid;
                    uint fromPid = data.from?.vessel != null ? data.from.vessel.persistentId : 0u;
                    uint toPid = data.to?.vessel != null ? data.to.vessel.persistentId : 0u;
                    uint partnerPidFromEvent = ResolveDockPartnerPidFromEvent(
                        fromPid, toPid, recorder.RecordingVesselId);

                    if (isTarget)
                    {
                        // We are the TARGET -- the absorbed partner is the couple event's
                        // partner when it is a BackgroundMap member of this tree, else 0.
                        absorbedPid = ResolveTargetSideAbsorbedPid(
                            partnerPidFromEvent, mergedPid, activeTree.BackgroundMap.Keys);
                        ParsekLog.Verbose("Flight",
                            $"OnPartCouple target-side absorbed partner: absorbedPid={absorbedPid} " +
                            $"partnerPidFromEvent={partnerPidFromEvent} " +
                            $"partnerInBackgroundMap={activeTree.BackgroundMap.ContainsKey(partnerPidFromEvent)}");
                    }
                    else
                    {
                        // We are the INITIATOR -- we are being absorbed
                        absorbedPid = recorder.RecordingVesselId;
                    }

                    // Resolve the route-partner PID from the couple event directly.
                    // BackgroundMap is intra-tree only, so a partner with a committed
                    // recording from a prior tree is not a BackgroundMap member.
                    //
                    // Accept the partner as route-eligible when EITHER:
                    //  (a) the pre-couple partner snapshot was captured successfully
                    //      (the partner is a real KSP vessel with parts that was
                    //      distinct from the recorder at couple time — third-playtest
                    //      scenario where the destination is loaded from save state
                    //      but has no prior Parsek recording), OR
                    //  (b) the partner has a known Parsek recording in the active tree
                    //      or in committed recordings (yesterday's cross-tree case).
                    // Both paths trust real evidence; the stricter "must have prior
                    // recording" gate from the first iteration was over-conservative
                    // for the destination-loaded-from-save workflow.
                    bool partnerSnapshotCaptured = pendingDockPartnerSnapshot != null
                        && pendingDockPartnerSnapshotPid == partnerPidFromEvent
                        && partnerPidFromEvent != 0u;
                    bool partnerKnown = IsKnownDockPartnerForRoute(
                        partnerPidFromEvent,
                        recorder.RecordingVesselId,
                        activeTree?.Recordings?.Values,
                        RecordingStore.CommittedRecordings);
                    bool partnerEligible = partnerPidFromEvent != 0u
                        && partnerPidFromEvent != recorder.RecordingVesselId
                        && (partnerSnapshotCaptured || partnerKnown);
                    uint routeTargetPid = SuppressRouteWindowForEvaGrab(
                        partnerEligible ? partnerPidFromEvent : 0u,
                        coupleInvolvesEva, coupleTransferKind, pathLabel: "");
                    bool partnerIsEva = ConnectionProducerClassifier.PartnerIsEvaVessel(
                        data.from, data.to, partnerPidFromEvent);
                    uint partnerStampPid = ResolveBranchPartnerStampPid(
                        partnerPidFromEvent, recorder.RecordingVesselId, partnerIsEva);
                    string partnerStampGuid = ResolveDockPartnerGuidFromEvent(
                        partnerStampPid,
                        fromPid, AnchorDetector.TryReadLiveVesselGuid(data.from?.vessel),
                        toPid, AnchorDetector.TryReadLiveVesselGuid(data.to?.vessel));

                    ParsekLog.Verbose("Flight",
                        $"OnPartCouple route-partner resolve: selfPid={recorder.RecordingVesselId} " +
                        $"mergedPid={mergedPid} isTarget={isTarget} absorbedPid={absorbedPid} " +
                        $"partnerPidFromEvent={partnerPidFromEvent} " +
                        $"partnerSnapshotCaptured={partnerSnapshotCaptured} partnerKnown={partnerKnown} " +
                        $"partnerEligible={partnerEligible} routeTargetPid={routeTargetPid} " +
                        $"partnerIsEva={partnerIsEva} partnerPidStamped={partnerStampPid} " +
                        $"partnerGuidStamped={partnerStampGuid ?? "(none)"}");

                    CapturePendingDockRouteEndpointProof(data, routeTargetPid);

                    // Stop recorder synchronously
                    recorder.StopRecordingForChainBoundary();

                    // Set pending state for tree merge
                    pendingTreeDockMerge = true;
                    pendingDockMergedPid = mergedPid;
                    pendingDockAbsorbedPid = absorbedPid;
                    pendingDockRouteTargetPid = routeTargetPid;
                    pendingDockPartnerPid = partnerStampPid;
                    pendingDockPartnerGuid = partnerStampGuid;
                    pendingDockTransferKind = coupleTransferKind;
                    dockConfirmFrames = 0;

                    // Race condition guard: prevent Task 6 from misclassifying absorption as destruction
                    if (absorbedPid != 0)
                        dockingInProgress.Add(absorbedPid);

                    Log($"onPartCouple (tree): dock merge pending " +
                        $"(merged={mergedPid}, absorbed={absorbedPid}, isTarget={isTarget})");
                    return;
                }
                else if (!recorder.IsRecording && recorder.CaptureAtStop != null)
                {
                    // OnPhysicsFrame already stopped us (initiator, late event).
                    // Target-side recorders are stopped synchronously by us at the top of
                    // this handler, never by OnPhysicsFrame, so reaching here with a
                    // target-side recorder (recorder PID still equal to mergedPid) means
                    // some other code path got there first -- bail out of route capture
                    // rather than misroute the endpoint.
                    uint absorbedPid = recorder.RecordingVesselId;
                    if (absorbedPid == mergedPid)
                    {
                        ParsekLog.Warn("Flight",
                            $"onPartCouple (tree, retroactive): unexpected target-side pre-stop " +
                            $"mergedPid={mergedPid}; skipping route endpoint capture");
                        return;
                    }
                    // Mirror the recording-path resolver: derive partner from event,
                    // validate via known-recordings registry, and emit the same diagnostic.
                    uint fromPidR = data.from?.vessel != null ? data.from.vessel.persistentId : 0u;
                    uint toPidR = data.to?.vessel != null ? data.to.vessel.persistentId : 0u;
                    uint partnerPidFromEventR = ResolveDockPartnerPidFromEvent(
                        fromPidR, toPidR, recorder.RecordingVesselId);

                    // Gap §5.1: also try to capture a pre-couple partner snapshot in
                    // the retroactive path. By the time we reach this branch
                    // OnPhysicsFrame has already stopped the recorder, so KSP may
                    // have ALREADY reparented data.from.vessel onto data.to.vessel
                    // and the snapshot would be the merged vessel (over-counted). We
                    // only capture when data.from.vessel and data.to.vessel are
                    // still distinct; if they aren't, fallbacks downstream
                    // (TryDeriveEndpointPartPidsFromPartner) carry the accepted
                    // baseline-inflation risk noted in §5.1. Captured BEFORE the
                    // eligibility check so partnerSnapshotCaptured can vote yes.
                    pendingDockPartnerSnapshot = null;
                    pendingDockPartnerSnapshotPid = 0u;
                    pendingDockSelfSnapshot = null;
                    pendingDockSelfSnapshotPid = 0u;
                    if (data.from?.vessel != null && data.to?.vessel != null &&
                        data.from.vessel != data.to.vessel)
                    {
                        Vessel partnerR = data.from.vessel.persistentId != absorbedPid
                            ? data.from.vessel
                            : (data.to.vessel.persistentId != absorbedPid ? data.to.vessel : null);
                        if (partnerR != null && partnerR.parts != null && partnerR.parts.Count > 0)
                        {
                            pendingDockPartnerSnapshot = FlightRecorder.TryBackupSnapshot(partnerR);
                            pendingDockPartnerSnapshotPid = partnerR.persistentId;
                            ParsekLog.Verbose("Flight",
                                $"OnPartCouple (retroactive): captured pre-couple partner snapshot " +
                                $"partnerPid={pendingDockPartnerSnapshotPid} parts={partnerR.parts.Count}");
                        }

                        // Mirror: the recorder's own (transport) pre-couple snapshot. In the
                        // retroactive path the recorder is already stopped and KSP may have
                        // reparented; this only fires while from/to are still distinct, else
                        // it stays null and the merged snapshot is used (accepted §5.1 risk).
                        Vessel selfR = ResolveSelfVessel(data, recorder.RecordingVesselId);
                        if (selfR != null && selfR.parts != null && selfR.parts.Count > 0)
                        {
                            pendingDockSelfSnapshot = FlightRecorder.TryBackupSnapshot(selfR);
                            pendingDockSelfSnapshotPid = selfR.persistentId;
                            ParsekLog.Verbose("Flight",
                                $"OnPartCouple (retroactive): captured pre-couple self snapshot " +
                                $"selfPid={pendingDockSelfSnapshotPid} parts={selfR.parts.Count}");
                        }
                    }

                    // Eligibility: same dual-evidence test as the live-recording path.
                    // Accept the partner when EITHER the pre-couple snapshot was
                    // captured (real KSP vessel) OR it has a known Parsek recording.
                    bool partnerSnapshotCapturedR = pendingDockPartnerSnapshot != null
                        && pendingDockPartnerSnapshotPid == partnerPidFromEventR
                        && partnerPidFromEventR != 0u;
                    bool partnerKnownR = IsKnownDockPartnerForRoute(
                        partnerPidFromEventR,
                        recorder.RecordingVesselId,
                        activeTree?.Recordings?.Values,
                        RecordingStore.CommittedRecordings);
                    bool partnerEligibleR = partnerPidFromEventR != 0u
                        && partnerPidFromEventR != recorder.RecordingVesselId
                        && (partnerSnapshotCapturedR || partnerKnownR);
                    uint routeTargetPid = SuppressRouteWindowForEvaGrab(
                        partnerEligibleR ? partnerPidFromEventR : 0u,
                        coupleInvolvesEva, coupleTransferKind, pathLabel: " (retroactive)");
                    bool partnerIsEvaR = ConnectionProducerClassifier.PartnerIsEvaVessel(
                        data.from, data.to, partnerPidFromEventR);
                    uint partnerStampPidR = ResolveBranchPartnerStampPid(
                        partnerPidFromEventR, recorder.RecordingVesselId, partnerIsEvaR);
                    string partnerStampGuidR = ResolveDockPartnerGuidFromEvent(
                        partnerStampPidR,
                        fromPidR, AnchorDetector.TryReadLiveVesselGuid(data.from?.vessel),
                        toPidR, AnchorDetector.TryReadLiveVesselGuid(data.to?.vessel));

                    ParsekLog.Verbose("Flight",
                        $"OnPartCouple route-partner resolve (retroactive): " +
                        $"selfPid={recorder.RecordingVesselId} mergedPid={mergedPid} " +
                        $"absorbedPid={absorbedPid} partnerPidFromEvent={partnerPidFromEventR} " +
                        $"partnerSnapshotCaptured={partnerSnapshotCapturedR} partnerKnown={partnerKnownR} " +
                        $"partnerEligible={partnerEligibleR} routeTargetPid={routeTargetPid} " +
                        $"partnerIsEva={partnerIsEvaR} partnerPidStamped={partnerStampPidR} " +
                        $"partnerGuidStamped={partnerStampGuidR ?? "(none)"}");

                    CapturePendingDockRouteEndpointProof(data, routeTargetPid);

                    pendingTreeDockMerge = true;
                    pendingDockMergedPid = mergedPid;
                    pendingDockAbsorbedPid = absorbedPid;
                    pendingDockRouteTargetPid = routeTargetPid;
                    pendingDockPartnerPid = partnerStampPidR;
                    pendingDockPartnerGuid = partnerStampGuidR;
                    pendingDockTransferKind = coupleTransferKind;
                    dockConfirmFrames = 0;

                    if (absorbedPid != 0)
                        dockingInProgress.Add(absorbedPid);

                    Log($"onPartCouple (tree, retroactive): dock merge pending " +
                        $"(merged={mergedPid}, absorbed={absorbedPid})");
                    return;
                }
            }

            // No live tree recorder in a state a dock merge applies to: no tree, no
            // recorder, or a recorder stopped without a capture. Every recording runs
            // inside a tree, so nothing else consumes the couple.
            ParsekLog.Verbose("Flight",
                $"onPartCouple: no dock merge armed (mergedPid={mergedPid}, hasTree={activeTree != null}, " +
                $"hasRecorder={recorder != null}, recording={recorder != null && recorder.IsRecording}, " +
                $"hasCapture={recorder != null && recorder.CaptureAtStop != null})");
        }

        void OnPartUndock(Part undockedPart)
        {
            // #267: skip if restore coroutine is mid-yield — it owns activeTree/recorder
            if (restoringActiveTree)
            {
                ParsekLog.Warn("Flight",
                    $"OnPartUndock: skipped — restore coroutine in progress " +
                    $"(part '{undockedPart?.partInfo?.name}')");
                ClearPendingUndockSeed();
                return;
            }

            RecorderStateLog.RecState("OnPartUndock:entry", CaptureRecorderState());
            ClearPendingUndockSeed();
            if (recorder == null || !recorder.IsRecording) return;
            if (pendingSplitInProgress) return; // another split is already being processed
            if (undockedPart?.vessel == null)
            {
                ParsekLog.Verbose("Flight", "OnPartUndock: undocked part or vessel is null — ignoring");
                return;
            }

            // onPartUndock fires FIRST inside KSP's Part.Undock(), BEFORE the vessel is
            // split (the part still reports the OLD combined vessel persistentId here, so
            // we deliberately do NOT branch off this pid). KSP creates the new vessel and
            // fires onVesselsUndocking(oldVessel, newVessel) at the END of the same call,
            // where both pids are final. We capture the structural-event snapshot and the
            // departing root part's pre-split origin seed NOW (combined vessel intact, part
            // still attached, the ideal moment) and stash the seed for OnVesselsUndocking,
            // which authors the actual Undock branch (and, via CreateSplitBranch, completes
            // any open logistics route-connection window). See the undock-not-recorded fix.
            double undockEventUT = Planetarium.GetUniversalTime();
            var undockInvolved = new List<Vessel>(1) { undockedPart.vessel };
            recorder.AppendStructuralEventSnapshot(undockEventUT, undockInvolved, "Undock");
            backgroundRecorder?.AppendStructuralEventSnapshot(undockEventUT, undockInvolved, "Undock");

            uint undockedPartPid = undockedPart.persistentId;
            uint undockedRootPartPid = undockedPart.vessel.rootPart?.persistentId ?? 0u;
            if (ShouldUseUndockPartOriginSeed(undockedPartPid, undockedRootPartPid)
                && FlightRecorder.TryCreateAbsoluteTrajectoryPointFromPartOrigin(
                    undockedPart,
                    undockEventUT,
                    out TrajectoryPoint capturedUndockSeed))
            {
                pendingUndockRootPartSeed = capturedUndockSeed;
                ParsekLog.Verbose("Flight",
                    $"Undock root part-origin seed captured: part='{undockedPart.partInfo?.name}' " +
                    $"pid={undockedPartPid} preSplitVesselPid={undockedPart.vessel.persistentId} " +
                    $"ut={undockEventUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"(deferring branch to onVesselsUndocking)");
            }
            else
            {
                string reason = ShouldUseUndockPartOriginSeed(undockedPartPid, undockedRootPartPid)
                    ? "capture-failed"
                    : "undocked-part-not-root";
                ParsekLog.Verbose("Flight",
                    $"Undock part-origin seed skipped: reason={reason} " +
                    $"undockedPartPid={undockedPartPid} rootPartPid={undockedRootPartPid} " +
                    $"preSplitVesselPid={undockedPart.vessel.persistentId} " +
                    $"(deferring branch to onVesselsUndocking)");
            }
        }

        /// <summary>
        /// Authoritative docking-port undock handler. KSP fires
        /// <c>onVesselsUndocking(oldVessel, newVessel)</c> at the end of <c>Part.Undock()</c>
        /// with both vessels fully split and carrying their final persistentIds, so unlike a
        /// stack/radial decoupler (which fires <c>onPartDeCoupleNewVesselComplete</c> and is
        /// caught by the joint-break fallback) we can map the undock directly. This is the
        /// fix for docking-port undocks being mis-recorded as within-segment part loss: the
        /// joint-break fallback can never see the new vessel for an undock, so it classifies
        /// the departing pod as PartDestroyed instead of branching an Undock.
        /// </summary>
        void OnVesselsUndocking(Vessel oldVessel, Vessel newVessel)
        {
            // Vessel names (design 7.7): the leaving vessel (fresh guid, re-rooted on its
            // pre-dock root part) continues the absorbed identity when a dock-absorb row names
            // its root. Linked whatever the recorder state.
            VesselNameLiveSync.OnUndockSplit(newVessel);

            // #267: skip if restore coroutine is mid-yield; it owns activeTree/recorder.
            if (restoringActiveTree)
            {
                ParsekLog.Warn("Flight",
                    "OnVesselsUndocking: skipped, restore coroutine in progress");
                ClearPendingUndockSeed();
                return;
            }

            RecorderStateLog.RecState("OnVesselsUndocking:entry", CaptureRecorderState());

            // Snapshot + clear the pre-split seed stashed by OnPartUndock this same frame.
            TrajectoryPoint? undockRootPartSeed = pendingUndockRootPartSeed;
            ClearPendingUndockSeed();

            // A vehicle and a kerbal at the end is an EVA, never an Undock (owner ruling
            // 2026-10-24): stock's seat dismount and claw release of a kerbal reach here
            // through Part.Undock and fire no onCrewOnEva.
            if (oldVessel != null && newVessel != null
                && SegmentBoundaryLogic.ClassifyUndockShape(oldVessel.isEVA, newVessel.isEVA)
                    == UndockShape.KerbalLeftVehicle)
            {
                HandleKerbalLeftVehicleUndock(oldVessel, newVessel);
                return;
            }

            if (recorder == null || !recorder.IsRecording)
            {
                ParsekLog.Verbose("Flight",
                    "OnVesselsUndocking: not recording, ignoring");
                return;
            }
            if (pendingSplitInProgress)
            {
                ParsekLog.Verbose("Flight",
                    "OnVesselsUndocking: another split is already being processed, ignoring");
                return;
            }
            if (oldVessel == null || newVessel == null)
            {
                ParsekLog.Verbose("Flight",
                    $"OnVesselsUndocking: null vessel (old={(oldVessel != null ? "set" : "null")}, " +
                    $"new={(newVessel != null ? "set" : "null")}), ignoring");
                return;
            }

            uint recordedPid = recorder.RecordingVesselId;
            uint oldPid = oldVessel.persistentId;
            uint newPid = newVessel.persistentId;

            UndockSplitDecision decision =
                SegmentBoundaryLogic.ClassifyUndockSplit(recordedPid, oldPid, newPid);

            ParsekLog.Info("Flight",
                $"OnVesselsUndocking: recordedPid={recordedPid} oldPid={oldPid} " +
                $"(name='{oldVessel.vesselName}') newPid={newPid} (name='{newVessel.vesselName}') " +
                $"decision={decision}");

            if (decision == UndockSplitDecision.NotRecordedVessel)
            {
                ParsekLog.Verbose("Flight",
                    $"OnVesselsUndocking: undock does not involve the recorded vessel " +
                    $"(recordedPid={recordedPid}), leaving recorder running");
                return;
            }

            // The recorder maps to KSP's oldVessel (which keeps its persistentId); the new
            // vessel is the freshly split-off side. The deferred branch resolves which side
            // KSP gives focus to and backgrounds the OTHER, so we pass both pids rather than
            // pre-deciding the background here (focus can move to the departing vessel one
            // frame later via WaitAndSwitchFocus when the player kept controlling that pod).

            // The undock event IS the authoritative split signal. Consume the recorder's
            // pending joint-break check (set by the synchronous onPartJointBreak that
            // KSP's attachJoint.DestroyJoint() fired earlier in the same Part.Undock() call)
            // so HandleJointBreakDeferredCheck does NOT also run next frame and misclassify
            // the departing pod as within-segment PartDestroyed. Note: a genuine within-
            // segment break in the SAME physics frame as the undock would also be swallowed
            // here, but the broken part is still hidden on playback via its Decoupled
            // PartEvent (recorded by FlightRecorder.OnPartJointBreak); only the PartDestroyed
            // SegmentEvent label is lost, an acceptable trade for not double-processing.
            bool consumedJointBreak = recorder.ConsumePendingJointBreakCheck(out double jointBreakUT);
            if (consumedJointBreak)
            {
                ParsekLog.Verbose("Flight",
                    $"OnVesselsUndocking: consumed recorder pending joint-break check " +
                    $"(ut={jointBreakUT.ToString("F2", CultureInfo.InvariantCulture)}); " +
                    "undock owns this split, suppressing within-segment fallback");
            }

            // Tree mode: stop recorder synchronously to prevent OnPhysicsFrame interference,
            // then defer one frame so KSP fully settles the freshly created vessel before we
            // snapshot it (mirrors the proven decouple/EVA deferred-branch pattern).
            recorder.StopRecordingForChainBoundary();
            pendingSplitRecorder = recorder;
            recorder = null;
            pendingSplitInProgress = true;
            Log($"onVesselsUndocking: vessel split off (recordedPid={recordedPid}, " +
                $"oldPid={oldPid}, newPid={newPid}); starting deferred Undock branch");
            StartCoroutine(DeferredUndockBranch(oldPid, newPid, undockRootPartSeed));
        }

        private void ClearPendingUndockSeed()
        {
            pendingUndockRootPartSeed = null;
        }

        /// <summary>
        /// Not-recording half of <see cref="HandleKerbalLeftVehicleUndock"/>: the same routes
        /// as a hatch EVA (a tracked background parent branch, else the deferred auto-record),
        /// but only once the kerbal holds the focus. A seat dismount moves focus to the kerbal
        /// right after its undock (<c>ForceSetActiveVessel</c>); a claw release keeps it on the
        /// claw's vessel, and an auto-record armed there would wait for, and start on, whatever
        /// EVA kerbal the player next switches to.
        /// </summary>
        private IEnumerator DeferredKerbalLeftVehicleNotRecording(
            GameEvents.FromToAction<Part, Part> evaData, Vessel kerbalVessel)
        {
            yield return null;
            bool kerbalActive = ShouldRouteKerbalLeftVehicleNotRecording(
                isRecording: IsRecording,
                kerbalVesselAlive: kerbalVessel != null,
                kerbalIsActive: kerbalVessel != null
                    && ReferenceEquals(FlightGlobals.ActiveVessel, kerbalVessel));
            if (!kerbalActive)
            {
                ParsekLog.Verbose("Flight",
                    $"OnVesselsUndocking: kerbal-left-vehicle not routed as a not-recording EVA " +
                    $"(recording={IsRecording} kerbalAlive={kerbalVessel != null} " +
                    $"activePid={FlightGlobals.ActiveVessel?.persistentId ?? 0u})");
                yield break;
            }
            OnCrewOnEva(evaData);
        }

        /// <summary>Pure gate of <see cref="DeferredKerbalLeftVehicleNotRecording"/>.</summary>
        internal static bool ShouldRouteKerbalLeftVehicleNotRecording(
            bool isRecording, bool kerbalVesselAlive, bool kerbalIsActive)
        {
            return !isRecording && kerbalVesselAlive && kerbalIsActive;
        }

        /// <summary>
        /// An undock whose two ends are a vehicle and an EVA kerbal (a kerbal leaving an
        /// external command seat, a claw releasing a kerbal) is recorded as an EVA: the same
        /// deferred EVA branch a hatch EVA takes (kerbal name on the child, no Rewind Point,
        /// the vehicle keeps its own background leg whatever its crew or command state), and
        /// when nothing is recording, the same auto-record. The stock-EVA event shape is
        /// rebuilt from the two vessels (from = the vehicle, to = the kerbal's part); every
        /// consumer reads only the two parts' vessels and the kerbal part's crew.
        /// </summary>
        private void HandleKerbalLeftVehicleUndock(Vessel oldVessel, Vessel newVessel)
        {
            Vessel kerbalVessel = newVessel.isEVA ? newVessel : oldVessel;
            Vessel vehicleVessel = newVessel.isEVA ? oldVessel : newVessel;
            var evaData = new GameEvents.FromToAction<Part, Part>(
                vehicleVessel.rootPart, kerbalVessel.rootPart);
            string kerbalName = ExtractEvaKerbalName(evaData);
            uint recordedPid = recorder != null ? recorder.RecordingVesselId : 0u;

            ParsekLog.Info("Flight",
                $"OnVesselsUndocking: kerbal left vehicle, recording as EVA " +
                $"(kerbal='{kerbalName ?? "<none>"}' kerbalPid={kerbalVessel.persistentId} " +
                $"vehiclePid={vehicleVessel.persistentId} (name='{vehicleVessel.vesselName}') " +
                $"kerbalIsNewVessel={newVessel.isEVA} recording={IsRecording} recordedPid={recordedPid})");

            if (!IsRecording)
            {
                StartCoroutine(DeferredKerbalLeftVehicleNotRecording(evaData, kerbalVessel));
                return;
            }
            if (pendingSplitInProgress)
            {
                LogSplitSkip(
                    "OnVesselsUndocking-EVA",
                    "pendingSplitInProgress",
                    vehicleVessel.persistentId,
                    kerbalVessel.persistentId);
                return;
            }
            if (SegmentBoundaryLogic.ClassifyUndockSplit(
                    recordedPid, oldVessel.persistentId, newVessel.persistentId)
                == UndockSplitDecision.NotRecordedVessel)
            {
                ParsekLog.Verbose("Flight",
                    $"OnVesselsUndocking: kerbal-left-vehicle does not involve the recorded vessel " +
                    $"(recordedPid={recordedPid}), leaving recorder running");
                return;
            }
            if (string.IsNullOrEmpty(kerbalName))
            {
                ParsekLog.Warn("Flight",
                    $"OnVesselsUndocking: kerbal-left-vehicle has no crew name " +
                    $"(kerbalPid={kerbalVessel.persistentId}); not branching");
                return;
            }

            // Part.Undock destroyed the attach joint first (synchronous onPartJointBreak); the
            // EVA branch owns this split, so the within-segment fallback must not run too.
            if (recorder.ConsumePendingJointBreakCheck(out double jointBreakUT))
            {
                ParsekLog.Verbose("Flight",
                    $"OnVesselsUndocking: consumed recorder pending joint-break check " +
                    $"(ut={jointBreakUT.ToString("F2", CultureInfo.InvariantCulture)}); " +
                    "kerbal-left-vehicle EVA owns this split");
            }

            double evaEventUT = Planetarium.GetUniversalTime();
            var evaInvolved = new List<Vessel>(2) { vehicleVessel, kerbalVessel };
            recorder.AppendStructuralEventSnapshot(evaEventUT, evaInvolved, "EVA");
            backgroundRecorder?.AppendStructuralEventSnapshot(evaEventUT, evaInvolved, "EVA");

            recorder.StopRecordingForChainBoundary();
            pendingSplitRecorder = recorder;
            recorder = null;
            pendingSplitInProgress = true;
            Log($"Kerbal left vehicle through an undock: '{kerbalName}' - starting deferred EVA branch");
            StartCoroutine(DeferredEvaBranch(kerbalName, evaData));
        }

        /// <summary>
        /// Appends a segment event to a tree recording in stable UT order and marks its sidecar
        /// dirty, the segment-event twin of <see cref="AppendFlagEventToTreeRecording"/>.
        /// </summary>
        internal static void AppendSegmentEventToTreeRecording(Recording rec, SegmentEvent evt)
        {
            if (rec == null)
                return;
            rec.SegmentEvents.Add(evt);
            var sorted = FlightRecorder.StableSortByUT(rec.SegmentEvents, e => e.ut);
            rec.SegmentEvents.Clear();
            rec.SegmentEvents.AddRange(sorted);
            rec.MarkFilesDirty();
        }

        /// <summary>
        /// Zeros all dock pending-transition fields.
        /// Called from scene change and flight ready.
        /// </summary>
        private void ClearDockUndockState()
        {
            pendingDockMergedPid = 0;
            dockConfirmFrames = 0;
            pendingDockAbsorbedPid = 0;
            pendingDockRouteTargetPid = 0;
            pendingDockPartnerPid = 0;
            pendingDockPartnerGuid = null;
            pendingDockTransferKind = RouteConnectionKind.None;
            pendingDockPartnerSnapshot = null;
            pendingDockPartnerSnapshotPid = 0u;
            pendingDockSelfSnapshot = null;
            pendingDockSelfSnapshotPid = 0u;
            pendingDockRouteEndpointAtDock = null;
            pendingDockRouteEndpointSituation = -1;
        }

        /// <summary>
        /// Handles tree dock merge: creates merge branch point when recorder is stopped
        /// and dock merge is pending. Consumes pending dock state.
        /// </summary>
        private void HandleTreeDockMerge()
        {
            if (activeTree == null || !pendingTreeDockMerge || recorder == null
                || recorder.IsRecording || recorder.CaptureAtStop == null)
                return;

            double mergeUT = Planetarium.GetUniversalTime();
            if (recorder.CaptureAtStop.Points.Count > 0)
                mergeUT = recorder.CaptureAtStop.Points[recorder.CaptureAtStop.Points.Count - 1].ut;

            // Determine parent recording IDs
            string activeParentId = activeTree.ActiveRecordingId;
            string bgParentId = null;
            // The background partner is whichever vessel (absorbed OR merged) is in BackgroundMap.
            // When we're the TARGET: absorbed vessel is in BackgroundMap.
            // When we're the INITIATOR: merged (surviving) vessel is in BackgroundMap.
            if (pendingDockAbsorbedPid != 0)
                activeTree.BackgroundMap.TryGetValue(pendingDockAbsorbedPid, out bgParentId);
            if (bgParentId == null && pendingDockMergedPid != 0)
                activeTree.BackgroundMap.TryGetValue(pendingDockMergedPid, out bgParentId);

            var stoppedRecorder = recorder;
            recorder = null;
            // Route target was already resolved from the couple event in OnPartCouple
            // and persisted on pendingDockRouteTargetPid. Falling back to the legacy
            // BackgroundMap-derived ResolveDockRouteTargetPid would drop the partner
            // for cross-tree dock partners (committed-recording case). The ungated
            // partner stamp rides alongside and feeds ONLY the branch point.
            uint routeTargetPid = pendingDockRouteTargetPid;
            uint branchPartnerPid = pendingDockPartnerPid;
            string branchPartnerGuid = pendingDockPartnerGuid;

            CreateMergeBranch(
                BranchPointType.Dock,
                pendingDockMergedPid,
                activeParentId,
                bgParentId,
                mergeUT,
                stoppedRecorder,
                routeTargetPid,
                pendingDockTransferKind,
                branchPartnerPid,
                branchPartnerGuid);

            // Clean up
            if (pendingDockAbsorbedPid != 0)
                dockingInProgress.Remove(pendingDockAbsorbedPid);

            pendingTreeDockMerge = false;
            pendingDockMergedPid = 0;
            pendingDockAbsorbedPid = 0;
            pendingDockRouteTargetPid = 0;
            pendingDockPartnerPid = 0;
            pendingDockPartnerGuid = null;
            pendingDockTransferKind = RouteConnectionKind.None;
            pendingDockPartnerSnapshot = null;
            pendingDockPartnerSnapshotPid = 0u;
            pendingDockSelfSnapshot = null;
            pendingDockSelfSnapshotPid = 0u;
            pendingDockRouteEndpointAtDock = null;
            pendingDockRouteEndpointSituation = -1;
            dockConfirmFrames = 0;

            Log("Tree dock merge completed");
        }

        /// <summary>
        /// Handles tree board merge: creates merge branch point when boarding is detected
        /// in tree mode and the recorder is stopped with ChainToVesselPending.
        /// </summary>
        private void HandleTreeBoardMerge()
        {
            if (activeTree == null || recorder == null || !recorder.ChainToVesselPending
                || recorder.IsRecording || recorder.CaptureAtStop == null
                || pendingBoardingTargetPid == 0
                || FlightGlobals.ActiveVessel == null
                || FlightGlobals.ActiveVessel.persistentId != pendingBoardingTargetPid)
                return;

            double mergeUT = Planetarium.GetUniversalTime();
            if (recorder.CaptureAtStop.Points.Count > 0)
                mergeUT = recorder.CaptureAtStop.Points[recorder.CaptureAtStop.Points.Count - 1].ut;

            uint mergedPid = pendingBoardingTargetPid;

            string activeParentId = activeTree.ActiveRecordingId;
            string bgParentId = null;
            if (pendingBoardingTargetInTree)
                activeTree.BackgroundMap.TryGetValue(mergedPid, out bgParentId);

            string boardPartnerGuid;
            uint boardPartnerPid = ResolveBoardPartnerStamp(
                pendingBoardingTargetInTree, mergedPid,
                AnchorDetector.TryReadLiveVesselGuid(FlightGlobals.ActiveVessel),
                out boardPartnerGuid);
            ParsekLog.Verbose("Flight",
                $"Board merge partner stamp: targetPid={mergedPid} inTree={pendingBoardingTargetInTree} " +
                $"partnerPidStamped={boardPartnerPid} partnerGuidStamped={boardPartnerGuid ?? "(none)"}");

            // Fix 5: clear ChainToVesselPending before CreateMergeBranch
            recorder.ChainToVesselPending = false;
            var stoppedRecorder = recorder;
            recorder = null;

            CreateMergeBranch(
                BranchPointType.Board,
                mergedPid,
                activeParentId,
                bgParentId,
                mergeUT,
                stoppedRecorder,
                branchPartnerPid: boardPartnerPid,
                branchPartnerGuid: boardPartnerGuid);

            pendingBoardingTargetPid = 0;
            boardingConfirmFrames = 0;
            pendingBoardingTargetInTree = false;

            Log("Tree board merge completed");
        }

        /// <summary>
        /// An EVA recording stopped on boarding (ChainToVesselPending) that
        /// <see cref="HandleTreeBoardMerge"/> did not consume, because the boarding
        /// confirmation is missing or names another vessel: treat it as a normal stop and
        /// leave CaptureAtStop for the ordinary revert/merge handling.
        /// </summary>
        private void HandleUnconfirmedBoardingStop()
        {
            if (recorder == null || !recorder.ChainToVesselPending
                || recorder.IsRecording || recorder.CaptureAtStop == null)
                return;

            recorder.ChainToVesselPending = false;
            pendingBoardingTargetPid = 0;
            boardingConfirmFrames = 0;
            Log("ChainToVessel without boarding confirmation \u2014 treating as normal stop");
            ParsekLog.ScreenMessage("Recording stopped - vessel changed", 3f);
        }

        /// <summary>
        /// Handles deferred split checks: stops the recorder and starts a deferred vessel-creation
        /// check when FlightRecorder signals a joint break or a decouple-created vessel indicates
        /// a split without a matching joint-break callback.
        /// </summary>
        internal static DeferredSplitCheckTrigger ResolveDeferredSplitCheckTrigger(
            bool pendingSplitInProgress,
            bool recorderIsRecording,
            bool jointBreakPending,
            bool decoupleCreatedVesselsPending)
        {
            if (pendingSplitInProgress || !recorderIsRecording)
                return DeferredSplitCheckTrigger.None;

            if (jointBreakPending)
                return DeferredSplitCheckTrigger.JointBreak;

            if (decoupleCreatedVesselsPending)
                return DeferredSplitCheckTrigger.DecoupleCreatedVessel;

            return DeferredSplitCheckTrigger.None;
        }

        internal static bool ShouldCaptureDecoupleCreatedVessel(
            uint recordedVesselPid,
            uint originalVesselPid)
        {
            return recordedVesselPid != 0 && originalVesselPid == recordedVesselPid;
        }

        internal static bool TrySelectDecouplePartOriginSeed(
            uint rootPartPersistentId,
            TryResolveTrajectoryPoint resolvePartOriginSeed,
            out TrajectoryPoint seed)
        {
            seed = default;
            if (rootPartPersistentId == 0u || resolvePartOriginSeed == null)
                return false;

            return resolvePartOriginSeed(rootPartPersistentId, out seed);
        }

        internal static bool ShouldUseUndockPartOriginSeed(
            uint undockedPartPersistentId,
            uint rootPartPersistentId)
        {
            return undockedPartPersistentId != 0u
                && rootPartPersistentId != 0u
                && undockedPartPersistentId == rootPartPersistentId;
        }

        private static string DescribeDecouplePartForDiagnostics(string label, Part part)
        {
            if (part == null)
                return label + "=null";

            string name = part.partInfo?.name ?? part.name ?? "unknown";
            uint parentPid = part.parent?.persistentId ?? 0u;
            string origin = part.transform != null
                ? DiagnosticFormatters.FormatVector3d(part.transform.position)
                : "no-transform";
            string rotation = part.transform != null
                ? DiagnosticFormatters.FormatQuaternion(part.transform.rotation)
                : "no-transform";
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}='{1}' pid={2} parentPid={3} origin={4} rot={5} srfAttach={6}",
                label,
                name,
                part.persistentId,
                parentPid,
                origin,
                rotation,
                DiagnosticFormatters.DescribeSurfaceAttachNode(part));
        }

        private static Part FindFirstDirectChildPart(Part rootPart, IList<Part> parts)
        {
            if (rootPart == null || parts == null)
                return null;

            for (int i = 0; i < parts.Count; i++)
            {
                Part part = parts[i];
                if (part != null && part.parent == rootPart)
                    return part;
            }

            return null;
        }

        internal static double ResolveDeferredSplitBranchUT(
            double fallbackUT,
            double exactTriggerUT,
            IEnumerable<uint> newVesselPids,
            IReadOnlyDictionary<uint, TrajectoryPoint> capturedTrajectoryPoints)
        {
            double branchUT = !double.IsNaN(exactTriggerUT) ? exactTriggerUT : fallbackUT;
            if (newVesselPids == null || capturedTrajectoryPoints == null)
                return branchUT;

            foreach (uint pid in newVesselPids)
            {
                TrajectoryPoint point;
                if (!capturedTrajectoryPoints.TryGetValue(pid, out point) || double.IsNaN(point.ut))
                    continue;

                if (double.IsNaN(branchUT) || point.ut < branchUT)
                    branchUT = point.ut;
            }

            return double.IsNaN(branchUT) ? fallbackUT : branchUT;
        }

        private void HandleJointBreakDeferredCheck()
        {
            if (pendingSplitInProgress || recorder == null || !recorder.IsRecording)
                return;

            double pendingJointBreakUT;
            bool jointBreakPending = recorder.ConsumePendingJointBreakCheck(out pendingJointBreakUT);
            bool decoupleCreatedVesselsPending =
                pendingDeferredSplitCheckTrigger == DeferredSplitCheckTrigger.DecoupleCreatedVessel
                && decoupleCreatedVessels != null
                && decoupleCreatedVessels.Count > 0;

            var trigger = ResolveDeferredSplitCheckTrigger(
                pendingSplitInProgress,
                recorderIsRecording: true,
                jointBreakPending,
                decoupleCreatedVesselsPending);

            if (trigger == DeferredSplitCheckTrigger.None)
                return;

            pendingDeferredSplitCheckTrigger = trigger;
            pendingSplitTriggerUT = jointBreakPending ? pendingJointBreakUT : double.NaN;

            // Snapshot existing vessel PIDs so the deferred check can identify NEW vessels.
            // This only makes sense for true joint-break-triggered checks.
            preBreakVesselPids = null;
            if (trigger == DeferredSplitCheckTrigger.JointBreak && FlightGlobals.Vessels != null)
            {
                preBreakVesselPids = new HashSet<uint>();
                for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
                {
                    Vessel v = FlightGlobals.Vessels[i];
                    if (v != null) preBreakVesselPids.Add(v.persistentId);
                }
            }
            recorder.StopRecordingForChainBoundary();
            pendingSplitRecorder = recorder;
            recorder = null;
            pendingSplitInProgress = true;

            if (trigger == DeferredSplitCheckTrigger.JointBreak)
                Log("Joint break detected \u2014 starting deferred vessel split check");
            else
                Log("Decouple-created vessel detected without joint-break callback \u2014 starting deferred vessel split check");
            StartCoroutine(DeferredJointBreakCheck());
        }

        /// <summary>
        /// Callback for GameEvents.onVesselCreate during the split check window.
        /// Captures vessels immediately at creation, before KSP's debris cleanup can destroy them.
        /// </summary>
        /// <summary>
        /// Callback for GameEvents.onPartDeCoupleNewVesselComplete during active recording.
        /// Fires synchronously inside Part.decouple(), before KSP's debris cleanup can destroy the vessel.
        /// If no structural onPartJointBreak callback reaches the recorder for this split,
        /// arm a decouple-only deferred split check so these captured vessels are processed
        /// promptly instead of lingering until a later unrelated joint break.
        /// </summary>
        private void OnDecoupleNewVesselDuringSplitCheck(Vessel originalVessel, Vessel newVessel)
        {
            if (newVessel == null) return;
            uint recordedVesselPid = recorder?.RecordingVesselId
                ?? pendingSplitRecorder?.RecordingVesselId
                ?? 0;
            uint originalVesselPid = originalVessel?.persistentId ?? 0;
            if (originalVesselPid != 0u)
                decoupleOriginByNewPid[newVessel.persistentId] = originalVesselPid;
            if (!ShouldCaptureDecoupleCreatedVessel(recordedVesselPid, originalVesselPid))
            {
                ParsekLog.VerboseRateLimited("Flight",
                    $"decouple-created-foreign-{originalVesselPid}-{newVessel.persistentId}",
                    $"Ignoring decouple-created vessel pid={newVessel.persistentId}: " +
                    $"originalPid={originalVesselPid}, recordedPid={recordedVesselPid}");
                return;
            }
            // Only capture vessels that didn't exist before the break
            if (preBreakVesselPids != null && preBreakVesselPids.Contains(newVessel.persistentId))
                return;
            decoupleCreatedVessels?.Add(newVessel);
            if (!pendingSplitInProgress && preBreakVesselPids == null)
                pendingDeferredSplitCheckTrigger = DeferredSplitCheckTrigger.DecoupleCreatedVessel;
            // Capture controller status NOW — vessel parts may be cleared by the deferred check.
            // A crewed vessel with no command module counts: it records as its own branch.
            bool hasController = ShouldRecordAsOwnBranch(newVessel);
            if (decoupleControllerStatus != null)
                decoupleControllerStatus[newVessel.persistentId] = hasController;
            double splitUT = Planetarium.GetUniversalTime();
            uint rootPartPersistentId = newVessel.rootPart?.persistentId ?? 0u;
            string seedSource = "none";
            string pendingJointChildSeedPids = "none";
            bool rootMatchesPendingJointChildSeed = false;
            if (decoupleCreatedTrajectoryPoints != null
                && !decoupleCreatedTrajectoryPoints.ContainsKey(newVessel.persistentId))
            {
                FlightRecorder seedRecorder = recorder ?? pendingSplitRecorder;
                TryResolveTrajectoryPoint resolvePartOriginSeed = null;
                if (seedRecorder != null)
                {
                    rootMatchesPendingJointChildSeed =
                        seedRecorder.ContainsPendingJointChildPartOriginSeed(rootPartPersistentId);
                    resolvePartOriginSeed = seedRecorder.TryConsumePendingJointChildPartOriginSeed;
                    pendingJointChildSeedPids =
                        seedRecorder.DescribePendingJointChildPartOriginSeedIdsForDiagnostics();
                }
                // On stock decoupler splits, KSP roots the new debris vessel at
                // joint.Child; if it chooses a different root, this misses cleanly and
                // falls back to the post-split root-part sample below.
                if (TrySelectDecouplePartOriginSeed(
                        rootPartPersistentId,
                        resolvePartOriginSeed,
                        out TrajectoryPoint partOriginSeed))
                {
                    decoupleCreatedTrajectoryPoints[newVessel.persistentId] = partOriginSeed;
                    seedSource = "joint-child-part-origin";
                }
                else
                {
                    decoupleCreatedTrajectoryPoints[newVessel.persistentId] =
                        BackgroundRecorder.CreateAbsoluteTrajectoryPointFromVessel(
                            newVessel, splitUT, preferRootPartSurfacePose: true);
                    seedSource = "new-vessel-root-part";
                }
                // ---- Trace-Sep: log seed source + new-vessel state at split ----
                if (TraceSeparation.RecordingWindowActive)
                {
                    Vessel original = originalVessel;
                    CelestialBody body = newVessel.mainBody ?? original?.mainBody;
                    Vector3d newLlaWorld = body != null
                        ? body.GetWorldSurfacePosition(newVessel.latitude, newVessel.longitude, newVessel.altitude)
                        : Vector3d.zero;
                    Vector3d newTransformPos = newVessel.transform != null
                        ? (Vector3d)newVessel.transform.position : Vector3d.zero;
                    Vector3d originalLlaWorld = body != null && original != null
                        ? body.GetWorldSurfacePosition(original.latitude, original.longitude, original.altitude)
                        : Vector3d.zero;
                    Vector3 newSrfVel = newVessel.srf_velocity;
                    Vector3 originalSrfVel = original != null ? original.srf_velocity : Vector3.zero;
                    float dt = Time.fixedDeltaTime;
                    Vector3d newWorldDelta = newTransformPos - newLlaWorld;
                    TraceSeparation.RecordLog("DecoupleSeed",
                        "newPid=" + newVessel.persistentId +
                        " name='" + newVessel.vesselName + "'" +
                        " seedSource=" + seedSource +
                        " splitUT=" + splitUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                        " newLLA=(" + newVessel.latitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                        "," + newVessel.longitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                        "," + newVessel.altitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ")" +
                        " newLLAWorld=" + TraceSeparation.FormatVector3d(newLlaWorld) +
                        " newTransformPos=" + TraceSeparation.FormatVector3d(newTransformPos) +
                        " newTransformVsLLA=" + TraceSeparation.FormatVector3d(newWorldDelta) +
                        " |newDelta|=" + newWorldDelta.magnitude.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                        " originalLLAWorld=" + TraceSeparation.FormatVector3d(originalLlaWorld) +
                        " newVsOriginalLLA=" + TraceSeparation.FormatVector3d(newLlaWorld - originalLlaWorld) +
                        " newSrfVel=" + TraceSeparation.FormatVector3(newSrfVel) + " |vs|=" + newSrfVel.magnitude.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                        " predictedNewSrfStep=" + (newSrfVel.magnitude * dt).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                        " originalSrfVel=" + TraceSeparation.FormatVector3(originalSrfVel) + " |ovs|=" + originalSrfVel.magnitude.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                }
                // ---- /Trace-Sep ----
            }
            else if (decoupleCreatedTrajectoryPoints != null
                     && decoupleCreatedTrajectoryPoints.ContainsKey(newVessel.persistentId))
            {
                seedSource = "already-captured";
            }
            ParsekLog.Info("Flight",
                $"Decouple created vessel during recording: pid={newVessel.persistentId} " +
                $"name='{newVessel.vesselName}' type={newVessel.vesselType} " +
                $"parts={newVessel.parts?.Count ?? 0} hasController={hasController} " +
                $"rootPartPid={rootPartPersistentId} seedSource={seedSource} " +
                $"pendingJointChildSeedPids={pendingJointChildSeedPids} " +
                $"splitUT={splitUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"capturedSeed={(decoupleCreatedTrajectoryPoints != null && decoupleCreatedTrajectoryPoints.ContainsKey(newVessel.persistentId) ? "T" : "F")}");
            Part rootPart = newVessel.rootPart;
            ParsekLog.Verbose("Flight",
                $"Decouple created vessel diagnostics: pid={newVessel.persistentId} " +
                $"rootMatchesPendingJointChildSeed={(rootMatchesPendingJointChildSeed ? "T" : "F")} " +
                $"{DescribeDecouplePartForDiagnostics("root", rootPart)} " +
                $"{DescribeDecouplePartForDiagnostics("firstChild", FindFirstDirectChildPart(rootPart, newVessel.parts))}");
        }
    }
}
