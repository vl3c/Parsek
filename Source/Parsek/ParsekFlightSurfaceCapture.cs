using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    // The flight-scene surface capture: what a kerbal or vessel leaves on, or takes from, the surface
    // while recording. Flag plant and pick-up, deployed ground-science and EVA-construction part
    // placement and retrieval, and foreign inventory / construction transfers, each appended to the
    // recording tree. ParsekFlight keeps the GameEvents subscriptions (its handlers forward here), the
    // DeferredGroundPartPlacement coroutine and the flag statics other files name; it reaches this
    // service through SurfaceCapture.
    internal sealed class ParsekFlightSurfaceCapture
    {
        private readonly ParsekFlight host;

        internal ParsekFlightSurfaceCapture(ParsekFlight host)
        {
            this.host = host;
        }

        internal void OnAfterFlagPlanted(FlagSite flagSite)
        {
            if (flagSite == null || flagSite.vessel == null || flagSite.part == null) return;

            // Always stamp the planting date onto the plaque text
            double ut = Planetarium.GetUniversalTime();
            string dateStr = KSPUtil.PrintDate(ut, includeTime: true);
            flagSite.PlaqueText = ParsekFlight.FormatPlaqueWithDate(flagSite.PlaqueText, dateStr);

            ParsekLog.Verbose("Flight",
                $"Flag planted: '{flagSite.vessel.vesselName}' by '{flagSite.placedBy}' — date stamped");

            // Recording-specific: capture FlagEvent. The planting kerbal may be the
            // foreground recorder's vessel OR a background-tracked tree member: an EVA
            // kerbal re-controlled after a vessel switch stays in the tree's
            // BackgroundMap with the foreground recorder idle, yet can still plant a
            // flag. Capture into whichever recording owns the planting vessel so the
            // flag persists to the sidecar and replays during watch/playback.
            string placedBy = flagSite.placedBy ?? "";

            Vessel flagVessel = flagSite.vessel;
            CelestialBody body = flagVessel.mainBody;

            // Compute surface-relative rotation
            Quaternion worldRot = flagVessel.transform.rotation;
            Quaternion surfRot = Quaternion.Inverse(body.bodyTransform.rotation) * worldRot;

            var fe = new FlagEvent
            {
                ut = ut,
                flagSiteName = flagVessel.vesselName ?? "",
                placedBy = placedBy,
                plaqueText = flagSite.PlaqueText ?? "",
                flagURL = flagSite.part.flagURL ?? "",
                latitude = flagVessel.latitude,
                longitude = flagVessel.longitude,
                altitude = flagVessel.altitude,
                rotX = surfRot.x,
                rotY = surfRot.y,
                rotZ = surfRot.z,
                rotW = surfRot.w,
                bodyName = body.name
            };

            // 1) Foreground recorder owns the planting vessel.
            if (host.recorder != null && host.recorder.IsRecording
                && ParsekFlight.ShouldRecordFlagEvent(placedBy,
                    FlightRecorder.FindVesselByPid(host.recorder.RecordingVesselId)))
            {
                host.recorder.FlagEvents.Add(fe);
                ParsekLog.Info("Flight",
                    $"Flag event captured (foreground recorder pid={host.recorder.RecordingVesselId}): " +
                    $"'{fe.flagSiteName}' by '{fe.placedBy}' at " +
                    $"({fe.latitude:F4},{fe.longitude:F4},{fe.altitude:F1}) on {fe.bodyName}");
                return;
            }

            // 2) A background-tracked tree member owns the planting vessel.
            uint bgOwnerPid = ParsekFlight.ResolveBackgroundFlagOwnerPid(
                placedBy,
                host.activeTree?.BackgroundMap,
                pid => FlightRecorder.FindVesselByPid(pid)?.GetVesselCrew());
            if (bgOwnerPid != 0
                && host.activeTree != null
                && host.activeTree.BackgroundMap.TryGetValue(bgOwnerPid, out string bgRecId)
                && host.activeTree.Recordings.TryGetValue(bgRecId, out Recording bgRec)
                && bgRec != null)
            {
                ParsekFlight.AppendFlagEventToTreeRecording(bgRec, fe);
                ParsekLog.Info("Flight",
                    $"Flag event captured (background tree member pid={bgOwnerPid} rec={bgRecId}): " +
                    $"'{fe.flagSiteName}' by '{fe.placedBy}' at " +
                    $"({fe.latitude:F4},{fe.longitude:F4},{fe.altitude:F1}) on {fe.bodyName}");
                return;
            }

            ParsekLog.Verbose("Flight",
                $"Flag planted by '{placedBy}' but no foreground or background recording owns " +
                $"the planting vessel - skipping (recorderPid={(host.recorder != null ? host.recorder.RecordingVesselId : 0)}, " +
                $"hasTree={host.activeTree != null})");
        }

        /// <summary>
        /// A kerbal took a flag down (<see cref="FlagPickUp"/>): record it on the recording that
        /// owns the picking kerbal, the foreground recorder's vessel or a background tree member,
        /// so a replay takes the flag down at the same moment.
        /// </summary>
        internal void TryCaptureFlagPickUp(Vessel flag, uint pickerPid)
        {
            if (flag == null || pickerPid == 0u)
                return;
            CelestialBody body = flag.mainBody;
            double ut = Planetarium.GetUniversalTime();
            SegmentEvent evt = FlagPickUp.BuildEvent(
                ut, body != null ? body.name : null, flag.latitude, flag.longitude, flag.altitude, flag.vesselName);

            if (host.recorder != null && host.recorder.IsRecording && host.recorder.RecordingVesselId == pickerPid)
            {
                // Straight onto the active tree recording when it exists: the mid-flight
                // serialization flush (FlushRecorderIntoActiveTreeForSerialization) does not move
                // recorder.SegmentEvents, so an event left there would miss an F5 and be lost on F9.
                string activeRecId = host.activeTree?.ActiveRecordingId;
                if (activeRecId != null
                    && host.activeTree.Recordings.TryGetValue(activeRecId, out Recording activeRec)
                    && activeRec != null)
                {
                    ParsekFlight.AppendSegmentEventToTreeRecording(activeRec, evt);
                }
                else
                {
                    host.recorder.SegmentEvents.Add(evt);
                }
                ParsekLog.Info("Flight",
                    $"Flag pick-up captured (foreground recorder pid={pickerPid} rec={activeRecId ?? "(recorder buffer)"}): " +
                    $"'{flag.vesselName}' on {(body != null ? body.name : "?")} ut={ut.ToString("R", CultureInfo.InvariantCulture)}");
                return;
            }

            if (host.activeTree != null
                && host.activeTree.BackgroundMap.TryGetValue(pickerPid, out string bgRecId)
                && host.activeTree.Recordings.TryGetValue(bgRecId, out Recording bgRec)
                && bgRec != null)
            {
                ParsekFlight.AppendSegmentEventToTreeRecording(bgRec, evt);
                ParsekLog.Info("Flight",
                    $"Flag pick-up captured (background tree member pid={pickerPid} rec={bgRecId}): " +
                    $"'{flag.vesselName}' ut={ut.ToString("R", CultureInfo.InvariantCulture)}");
                return;
            }

            ParsekLog.Verbose("Flight",
                $"Flag '{flag.vesselName}' picked up by pid={pickerPid} but no foreground or background " +
                $"recording owns the kerbal - not recorded (recorderPid={(host.recorder != null ? host.recorder.RecordingVesselId : 0)}, " +
                $"hasTree={host.activeTree != null})");
        }

        #region EVA Ground Part Placement

        // Stock announces the placed vessel through onNewVesselCreated (inside
        // Game.AddVessel) and only THEN fires onDeployGroundPart(partName) from the same
        // ModuleInventoryPart.OnUpdate call, so the vessel is cached here and matched by
        // frame. Design: docs/parsek-flight-recorder-design.md section 4.11.
        private Vessel lastCreatedVesselForGroundPart;

        private int lastCreatedVesselFrame = -1;

        // Placed-part vessel pids whose pick-up has been signalled (first
        // onGroundSciencePartRemoved); read at the destroy seam to stamp Disassembled.
        private readonly HashSet<uint> groundPartRetrievalPending = new HashSet<uint>();

        internal void OnNewVesselCreatedForGroundPart(Vessel v)
        {
            lastCreatedVesselForGroundPart = v;
            lastCreatedVesselFrame = Time.frameCount;
        }

        internal void OnDeployGroundPart(string partName)
        {
            RecordGroundPartPlacement(partName, GroundPartPlacementSource.InventoryDeploy);
        }

        /// <summary>
        /// Stock fires <c>onEditorPartEvent(PartDropped, heldPart)</c> from
        /// <c>EVAConstructionModeEditor.DropAttachablePart</c> right after
        /// <c>Game.AddVessel</c> built the dropped part's own <c>DroppedPart</c> vessel (which
        /// fired <c>onNewVesselCreated</c>), in the same call (decompiled KSP 1.12.5). The held
        /// part may have come from any inventory or been detached from any vessel, a foreign
        /// one included; either way the dropped vessel is new and becomes the kerbal's member,
        /// exactly like an inventory ground placement.
        /// </summary>
        internal void OnEditorPartEventForConstructionDrop(ConstructionEventType evt, Part part)
        {
            if (!GroundPartPlacement.IsConstructionDropSignal(evt)) return;
            string partName = part != null && part.partInfo != null ? part.partInfo.name : null;
            RecordGroundPartPlacement(partName, GroundPartPlacementSource.ConstructionDrop);
        }

        void RecordGroundPartPlacement(string partName, GroundPartPlacementSource source)
        {
            Vessel active = FlightGlobals.ActiveVessel;
            Vessel created = lastCreatedVesselForGroundPart;
            bool createdThisFrame = created != null && lastCreatedVesselFrame == Time.frameCount;
            var protoParts = created?.protoVessel?.protoPartSnapshots;
            GroundPartPlacement.ResolveCreatedPartShape(
                protoParts?.Count ?? 0,
                protoParts != null && protoParts.Count > 0 ? protoParts[0]?.partName : null,
                created?.parts?.Count ?? 0,
                created != null && created.parts != null && created.parts.Count > 0
                    ? created.parts[0]?.partInfo?.name : null,
                out int createdPartCount, out string createdPartName);
            uint createdPid = created != null ? created.persistentId : 0u;

            Recording activeRec = null;
            if (host.activeTree != null && !string.IsNullOrEmpty(host.activeTree.ActiveRecordingId))
                host.activeTree.Recordings.TryGetValue(host.activeTree.ActiveRecordingId, out activeRec);

            bool isTreeMember = host.activeTree != null && createdPid != 0
                && (host.activeTree.BackgroundMap.ContainsKey(createdPid)
                    || (activeRec != null && activeRec.VesselPersistentId == createdPid));

            GroundPartPlacementVerdict verdict = GroundPartPlacement.EvaluatePlacement(
                hasActiveTree: host.activeTree != null,
                recorderIsRecording: host.recorder != null && host.recorder.IsRecording,
                activeVesselIsEva: active != null && active.isEVA,
                activeVesselPid: active != null ? active.persistentId : 0u,
                activeRecordingVesselPid: activeRec != null ? activeRec.VesselPersistentId : 0u,
                createdVesselPresent: created != null,
                createdThisFrame: createdThisFrame,
                createdPartCount: createdPartCount,
                createdPartName: createdPartName,
                placedPartName: partName,
                createdVesselIsTreeMember: isTreeMember);

            // The cache is single-use: a later onDeployGroundPart must not match it.
            lastCreatedVesselForGroundPart = null;
            lastCreatedVesselFrame = -1;

            if (verdict != GroundPartPlacementVerdict.Record)
            {
                ParsekLog.Info("Flight", GroundPartPlacement.FormatPlacementSkipLog(
                    verdict, partName, createdPid, active != null ? active.persistentId : 0u,
                    createdPartCount, createdPartName, source));
                return;
            }

            double placementUT = Planetarium.GetUniversalTime();
            ParsekLog.Info("Flight", GroundPartPlacement.FormatPlacementDetectedLog(
                source, partName, createdPid, activeRec.RecordingId, placementUT));
            host.StartCoroutine(host.DeferredGroundPartPlacement(
                host.activeTree.Id, activeRec.RecordingId, createdPid, placementUT, source));
        }

        internal void CreateGroundPartMember(Recording parentRec, Vessel v, double placementUT,
            GroundPartPlacementSource source)
        {
            Part part = v.rootPart;
            string partName = part.partInfo?.name ?? "unknown";
            uint partPid = part.persistentId;

            var (bp, member) = GroundPartPlacement.BuildPlacementBranchData(
                parentRec.RecordingId,
                host.activeTree.Id,
                placementUT,
                v.persistentId,
                Recording.ResolveLocalizedName(v.vesselName) ?? partName,
                parentRec.Generation);

            ConfigNode snapshot = FlightRecorder.TryBackupSnapshot(v);
            member.GhostVisualSnapshot = snapshot;
            member.VesselSnapshot = snapshot != null ? snapshot.CreateCopy() : null;
            member.StartResources = VesselSnapshotOps.ExtractResourceManifest(snapshot);
            int invSlots;
            member.StartInventory = VesselSnapshotOps.ExtractInventoryManifest(snapshot, out invSlots);
            member.StartInventorySlots = invSlots;
            member.StartCrew = VesselSpawner.ExtractCrewManifest(snapshot);
            member.Controllers = ControllerInfo.CaptureFromVessel(v);
            member.RecordedVesselGuid = AnchorDetector.TryReadLiveVesselGuid(v);
            member.AdoptRecordedVesselGuidIfEmpty(VesselLaunchIdentity.TryReadVesselGuid(snapshot));

            GroundPartPlacement.AttachMember(
                host.activeTree, parentRec, bp, member, partPid, partName, placementUT, source);

            if (host.backgroundRecorder != null)
            {
                TrajectoryPoint initialPoint = BackgroundRecorder.CreateAbsoluteTrajectoryPointFromVessel(
                    v, Planetarium.GetUniversalTime(), preferRootPartSurfacePose: true);
                host.backgroundRecorder.OnVesselBackgrounded(
                    v.persistentId, initialTrajectoryPoint: initialPoint);
            }
        }

        /// <summary>
        /// NOT a placement signal: stock also fires it whenever an unlinked experiment
        /// merely starts landed (a load into physics range, a Central Station removal).
        /// Placement is detected from onDeployGroundPart; this is diagnostics only.
        /// </summary>
        internal void OnGroundSciencePartDeployed(ModuleGroundSciencePart deployedPart)
        {
            Part p = deployedPart?.part;
            if (p == null) return;
            uint vesselPid = p.vessel != null ? p.vessel.persistentId : 0u;
            bool member = host.activeTree != null && host.activeTree.BackgroundMap.ContainsKey(vesselPid);
            ParsekLog.Verbose("Flight",
                $"onGroundSciencePartDeployed: part='{p.partInfo?.name ?? "unknown"}' pid={p.persistentId} " +
                $"vesselPid={vesselPid} treeMember={member} (not a placement signal, nothing recorded)");
        }

        internal void OnGroundSciencePartRemoved(ModuleGroundSciencePart removedPart)
        {
            Part p = removedPart?.part;
            if (p == null || p.vessel == null) return;
            GroundPartPlacement.HandleRemovedSignal(
                host.activeTree, p.vessel.persistentId, p.persistentId, p.partInfo?.name ?? "unknown",
                Planetarium.GetUniversalTime(), groundPartRetrievalPending);
        }

        /// <summary>
        /// The ground-part pick-up arm of the disassembly seam: true when the dying vessel
        /// is a placed-part member whose part went back into an inventory. Consumes the
        /// pending-retrieval entry.
        /// </summary>
        internal bool IsGroundPartRetrievalDeath(Vessel v, Recording rec)
        {
            bool isMember = GroundPartPlacement.IsPlacedPartMember(host.activeTree, rec);
            bool removedSeen = groundPartRetrievalPending.Remove(v.persistentId);
            bool? deployedOnGround = isMember ? TryReadDeployedOnGroundCore(v) : null;
            return GroundPartPlacement.IsRetrievalDeath(isMember, removedSeen, deployedOnGround);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool? TryReadDeployedOnGroundCore(Vessel v)
        {
            try
            {
                Part root = v?.rootPart;
                ModuleGroundPart module = root != null ? root.FindModuleImplementing<ModuleGroundPart>() : null;
                BaseField field = module != null ? module.Fields["deployedOnGround"] : null;
                if (field == null) return null;
                object value = field.GetValue(module);
                return value is bool b ? b : (bool?)null;
            }
            catch (System.Exception ex)
            {
                ParsekLog.Verbose("Flight",
                    $"TryReadDeployedOnGroundCore: read failed for vesselPid={v?.persistentId ?? 0u}: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Foreign Vessel Inventory Transfer

        // Last observed (part name, quantity) per container part pid and slot, read after
        // every recorded transfer, so the next change on that slot knows its direction.
        // Design: docs/parsek-flight-recorder-design.md section 4.12.
        private readonly Dictionary<long, KeyValuePair<string, int>> foreignInventorySlotReadings =
            new Dictionary<long, KeyValuePair<string, int>>();

        private static long ForeignInventorySlotKey(uint containerPartPid, int slot)
        {
            return ((long)containerPartPid << 20) ^ (uint)slot;
        }

        /// <summary>
        /// Stock fires <c>onModuleInventorySlotChanged(module, slot)</c> AFTER the slot
        /// changed (decompiled KSP 1.12.5: <c>StoreCargoPartAtSlot</c>,
        /// <c>ClearPartAtSlot</c>, <c>UpdateStackAmountAtSlot</c>,
        /// <c>RemoveNPartsFromInventory</c>). A drag from a container into the kerbal's
        /// inventory fires it twice: the container's slot is cleared first, then the
        /// kerbal's slot is filled. Only the foreign container's change is recorded here.
        /// </summary>
        internal void OnModuleInventorySlotChanged(ModuleInventoryPart module, int slot)
        {
            Part part = module != null ? module.part : null;
            Vessel changed = part != null ? part.vessel : null;
            Vessel active = FlightGlobals.ActiveVessel;
            uint changedPid = changed != null ? changed.persistentId : 0u;
            uint activePid = active != null ? active.persistentId : 0u;
            uint containerPid = part != null ? part.persistentId : 0u;

            ReadLiveSlot(module, slot, out string partNameAfter, out int quantityAfter);

            Recording activeRec = null;
            if (host.activeTree != null && !string.IsNullOrEmpty(host.activeTree.ActiveRecordingId))
                host.activeTree.Recordings.TryGetValue(host.activeTree.ActiveRecordingId, out activeRec);

            ForeignInventoryMembership membership =
                ForeignInventoryTransfer.ResolveMembership(host.activeTree, changedPid);
            ForeignInventoryTransferVerdict verdict = ForeignInventoryTransfer.Evaluate(
                hasActiveTree: host.activeTree != null,
                recorderIsRecording: host.recorder != null && host.recorder.IsRecording,
                parsekWriteInProgress: ParsekInventoryWrites.InProgress,
                activeVesselIsEva: active != null && active.isEVA,
                activeVesselPid: activePid,
                activeRecordingVesselPid: activeRec != null ? activeRec.VesselPersistentId : 0u,
                changedVesselPid: changedPid,
                changedVesselIsGhost: changedPid != 0 && GhostMapPresence.IsGhostMapVessel(changedPid),
                membership: membership);

            if (verdict != ForeignInventoryTransferVerdict.Adopt
                && verdict != ForeignInventoryTransferVerdict.AppendToMember)
            {
                ParsekLog.Verbose("Flight", ForeignInventoryTransfer.FormatSkipLog(
                    verdict, changedPid, activePid, partNameAfter, slot));
                return;
            }

            // Direction: the last reading of this slot, else the vessel's own saved state
            // (its ProtoPartSnapshot, untouched since load or the last save), else unknown.
            long key = ForeignInventorySlotKey(containerPid, slot);
            int? quantityBefore = null;
            string partNameBefore = null;
            if (foreignInventorySlotReadings.TryGetValue(key, out KeyValuePair<string, int> reading))
            {
                partNameBefore = reading.Key;
                quantityBefore = reading.Value;
            }
            else if (TryReadSavedSlotCore(module, slot, out string savedName, out int savedQuantity))
            {
                partNameBefore = savedName;
                quantityBefore = savedQuantity;
            }

            SegmentEventType type = ForeignInventoryTransfer.ClassifySlotChange(quantityBefore, quantityAfter);
            double ut = Planetarium.GetUniversalTime();
            SegmentEvent transfer = ForeignInventoryTransfer.BuildTransferSegmentEvent(
                ut, type, partNameAfter ?? partNameBefore, slot, quantityAfter, containerPid);
            RememberSlotReadings(module);

            if (verdict == ForeignInventoryTransferVerdict.AppendToMember)
            {
                AppendForeignTransfer(changed, transfer);
                return;
            }

            AdoptForeignCargoPartner(activeRec, changed, transfer);
        }

        /// <summary>
        /// Stock fires <c>OnEVAConstructionModePartAttached(parentVessel, part)</c> from
        /// <c>EVAConstructionModeEditor.AttachInput</c> right after <c>AttachPart</c>, and
        /// again from <c>RestoreLastAttachedPart</c> when a detach is undone (decompiled KSP
        /// 1.12.5). The part is already in the vessel's part list, so the snapshot taken here
        /// carries it.
        /// </summary>
        internal void OnEvaConstructionPartAttached(Vessel vessel, Part part)
        {
            RecordForeignConstructionChange(vessel, part, attached: true);
        }

        /// <summary>
        /// Stock fires <c>OnEVAConstructionModePartDetached(vessel, part)</c> from
        /// <c>EVAConstructionModeEditor.DetachInput</c> after <c>DetachPart</c>, with the
        /// vessel captured before the detach. <c>DetachPart</c> -> <c>Part.OnDetachFlight</c>
        /// -> <c>Decoupling</c> -> <c>disconnect</c> has already removed the held part from
        /// <c>vessel.parts</c> (decompiled KSP 1.12.5), so the snapshot taken here is the
        /// vessel without it and needs no frame wait.
        /// </summary>
        internal void OnEvaConstructionPartDetached(Vessel vessel, Part part)
        {
            RecordForeignConstructionChange(vessel, part, attached: false);
        }

        void RecordForeignConstructionChange(Vessel changed, Part part, bool attached)
        {
            Vessel active = FlightGlobals.ActiveVessel;
            uint changedPid = changed != null ? changed.persistentId : 0u;
            uint activePid = active != null ? active.persistentId : 0u;
            uint partPid = part != null ? part.persistentId : 0u;
            string partName = part != null && part.partInfo != null ? part.partInfo.name : null;

            Recording activeRec = null;
            if (host.activeTree != null && !string.IsNullOrEmpty(host.activeTree.ActiveRecordingId))
                host.activeTree.Recordings.TryGetValue(host.activeTree.ActiveRecordingId, out activeRec);

            ForeignInventoryMembership membership =
                ForeignInventoryTransfer.ResolveMembership(host.activeTree, changedPid);
            // Parsek never attaches or detaches a part, so no write fence applies here.
            ForeignInventoryTransferVerdict verdict = ForeignInventoryTransfer.Evaluate(
                hasActiveTree: host.activeTree != null,
                recorderIsRecording: host.recorder != null && host.recorder.IsRecording,
                parsekWriteInProgress: false,
                activeVesselIsEva: active != null && active.isEVA,
                activeVesselPid: activePid,
                activeRecordingVesselPid: activeRec != null ? activeRec.VesselPersistentId : 0u,
                changedVesselPid: changedPid,
                changedVesselIsGhost: changedPid != 0 && GhostMapPresence.IsGhostMapVessel(changedPid),
                membership: membership);

            if (verdict != ForeignInventoryTransferVerdict.Adopt
                && verdict != ForeignInventoryTransferVerdict.AppendToMember)
            {
                ParsekLog.Verbose("Flight", ForeignInventoryTransfer.FormatConstructionSkipLog(
                    verdict, attached, changedPid, activePid, partName, partPid));
                return;
            }

            SegmentEvent change = ForeignInventoryTransfer.BuildConstructionSegmentEvent(
                Planetarium.GetUniversalTime(), attached, partName, partPid);

            if (verdict == ForeignInventoryTransferVerdict.AppendToMember)
            {
                AppendForeignTransfer(changed, change,
                    constructionPartPid: partPid, constructionAttached: attached,
                    constructionPartName: partName);
                return;
            }

            // The adoption snapshot is taken after the change, so the member's ghost starts in
            // the new shape; the shape before it is the claimed vessel's own until the claim.
            AdoptForeignCargoPartner(activeRec, changed, change);
        }

        void AdoptForeignCargoPartner(Recording kerbalRec, Vessel v, SegmentEvent transfer)
        {
            var (bp, member) = BuildAdoptedForeignMember(kerbalRec, v, transfer.ut);

            ForeignInventoryTransfer.AttachMember(host.activeTree, kerbalRec, bp, member, transfer);

            if (host.backgroundRecorder != null)
            {
                TrajectoryPoint initialPoint = BackgroundRecorder.CreateAbsoluteTrajectoryPointFromVessel(
                    v, Planetarium.GetUniversalTime(), preferRootPartSurfacePose: true);
                host.backgroundRecorder.OnVesselBackgrounded(
                    v.persistentId, initialTrajectoryPoint: initialPoint);
            }
            else
            {
                ParsekLog.Warn("Flight", string.Format(CultureInfo.InvariantCulture,
                    "Foreign inventory transfer: no background recorder, member rec={0} " +
                    "vesselPid={1} keeps its adoption snapshot only",
                    member.RecordingId, v.persistentId));
            }
        }

        /// <summary>
        /// The branch point and member recording for a foreign vessel joining the tree, with the
        /// vessel's live snapshot, manifests, controllers and launch guid (the guid is what lets
        /// a later rewind hold the vessel by POSITIVE launch identity, never by pid alone).
        /// </summary>
        (BranchPoint bp, Recording member) BuildAdoptedForeignMember(
            Recording kerbalRec, Vessel v, double ut)
        {
            var (bp, member) = ForeignInventoryTransfer.BuildMemberBranchData(
                kerbalRec.RecordingId,
                host.activeTree.Id,
                ut,
                v.persistentId,
                Recording.ResolveLocalizedName(v.vesselName) ?? "Unknown",
                kerbalRec.Generation);

            ConfigNode snapshot = FlightRecorder.TryBackupSnapshot(v);
            member.GhostVisualSnapshot = snapshot;
            member.VesselSnapshot = snapshot != null ? snapshot.CreateCopy() : null;
            member.StartResources = VesselSnapshotOps.ExtractResourceManifest(snapshot);
            int invSlots;
            member.StartInventory = VesselSnapshotOps.ExtractInventoryManifest(snapshot, out invSlots);
            member.StartInventorySlots = invSlots;
            member.StartCrew = VesselSpawner.ExtractCrewManifest(snapshot);
            member.Controllers = ControllerInfo.CaptureFromVessel(v);
            member.RecordedVesselGuid = AnchorDetector.TryReadLiveVesselGuid(v);
            member.AdoptRecordedVesselGuidIfEmpty(VesselLaunchIdentity.TryReadVesselGuid(snapshot));
            return (bp, member);
        }

        /// <summary>
        /// The EVA-construction pick-up of a loose part that is NOT a member of this tree (todo
        /// CONSTRUCTION-PICKUP-OF-FOREIGN-LOOSE-PART-UNCLAIMED). Called from the pocket seam
        /// (<see cref="TryStampDisassembledTerminal"/>) on a BackgroundMap miss, synchronously
        /// inside <c>onVesselWillDestroy</c>: stock's <c>PickupPart</c> has handed the part to the
        /// kerbal and is killing its vessel. With no record of that death, a rewind to before the
        /// pick-up kept the old loose part beside whatever the replayed kerbal made of it. The
        /// vessel joins the tree as an InventoryTransfer member of the kerbal, stamped
        /// Disassembled at once (<see cref="ForeignInventoryTransfer.AttachPickedUpMember"/>); the
        /// rewind's vessel hold then takes the real part out of the rewound world, draws it as a
        /// hold ghost until the pick-up, and nothing spawns it back. Returns true when a member
        /// was adopted and stamped.
        /// </summary>
        internal bool TryAdoptForeignConstructionPickup(Vessel v)
        {
            VesselDeathEvidence evidence;
            VesselDeathKind deathKind =
                VesselDisassemblyClassifier.ClassifyLiveVesselDeath(v, out evidence);
            // An ordinary death of a vessel outside the tree is not this seam's business.
            if (deathKind != VesselDeathKind.Disassembled)
                return false;

            Vessel active = FlightGlobals.ActiveVessel;
            uint changedPid = v.persistentId;
            uint activePid = active != null ? active.persistentId : 0u;
            Part part = v.parts != null && v.parts.Count > 0 ? v.parts[0] : null;
            uint partPid = part != null ? part.persistentId : 0u;
            string partName = part != null && part.partInfo != null ? part.partInfo.name : null;

            Recording activeRec = null;
            if (!string.IsNullOrEmpty(host.activeTree.ActiveRecordingId))
                host.activeTree.Recordings.TryGetValue(host.activeTree.ActiveRecordingId, out activeRec);

            ForeignInventoryTransferVerdict verdict = ForeignInventoryTransfer.Evaluate(
                hasActiveTree: true,
                recorderIsRecording: host.recorder != null && host.recorder.IsRecording,
                parsekWriteInProgress: false,
                activeVesselIsEva: active != null && active.isEVA,
                activeVesselPid: activePid,
                activeRecordingVesselPid: activeRec != null ? activeRec.VesselPersistentId : 0u,
                changedVesselPid: changedPid,
                changedVesselIsGhost: changedPid != 0 && GhostMapPresence.IsGhostMapVessel(changedPid),
                membership: ForeignInventoryTransfer.ResolveMembership(host.activeTree, changedPid));

            if (activeRec == null
                || !ForeignInventoryTransfer.ShouldAdoptConstructionPickup(deathKind, verdict))
            {
                ParsekLog.Verbose("Flight", ForeignInventoryTransfer.FormatConstructionPickupSkipLog(
                    verdict, changedPid, activePid, partName, partPid));
                return false;
            }

            double ut = Planetarium.GetUniversalTime();
            SegmentEvent pickup = ForeignInventoryTransfer.BuildConstructionPickupSegmentEvent(
                ut, partName, partPid);
            var (bp, member) = BuildAdoptedForeignMember(activeRec, v, ut);
            TrajectoryPoint lastPose = BackgroundRecorder.CreateAbsoluteTrajectoryPointFromVessel(
                v, ut, preferRootPartSurfacePose: true);
            ForeignInventoryTransfer.AttachPickedUpMember(
                host.activeTree, activeRec, bp, member, pickup, lastPose, ut);

            ParsekLog.Info("Flight", ParsekFlight.FormatDisassembledTerminalLog(
                v.vesselName, changedPid, evidence.PartCount, member.RecordingId, ut,
                ForeignInventoryTransfer.ConstructionPickupLogReason));
            return true;
        }

        void AppendForeignTransfer(Vessel v, SegmentEvent transfer,
            uint constructionPartPid = 0u, bool constructionAttached = false,
            string constructionPartName = null)
        {
            string recId;
            Recording member;
            if (!host.activeTree.BackgroundMap.TryGetValue(v.persistentId, out recId)
                || !host.activeTree.Recordings.TryGetValue(recId, out member) || member == null)
            {
                ParsekLog.Warn("Flight", string.Format(CultureInfo.InvariantCulture,
                    "Foreign inventory transfer: cargo partner vesselPid={0} lost its member " +
                    "recording before the append, nothing recorded", v.persistentId));
                return;
            }

            ForeignInventoryTransfer.AppendTransferEvent(member, transfer);
            // The member's end snapshot is the chain tip's spawn form; keep it current.
            ConfigNode snapshot = FlightRecorder.TryBackupSnapshot(v);
            if (snapshot != null)
                member.VesselSnapshot = snapshot;
            ParsekLog.Info("Flight", string.Format(CultureInfo.InvariantCulture,
                "Foreign inventory transfer: event appended to cargo partner rec={0} vesselPid={1} " +
                "ut={2:F2} event={3} details='{4}' snapshotRefreshed={5} events={6}",
                member.RecordingId, v.persistentId, transfer.ut, transfer.type,
                transfer.details ?? "(none)", snapshot != null, member.SegmentEvents.Count));

            if (constructionPartPid == 0u)
                return;
            ConstructionGhostShapeResult shape = ConstructionGhostShape.Apply(
                member, snapshot, constructionAttached, constructionPartPid,
                constructionPartName, transfer.ut);
            ConfigNode[] ghostParts = member.GhostVisualSnapshot != null
                ? member.GhostVisualSnapshot.GetNodes("PART") : null;
            string line = ConstructionGhostShape.FormatLog(member.RecordingId, v.persistentId,
                constructionAttached, constructionPartPid, constructionPartName, transfer.ut, shape,
                ghostParts != null ? ghostParts.Length : 0);
            if (shape.EventWritten)
                ParsekLog.Info("Flight", line);
            else
                ParsekLog.Warn("Flight", line);
        }

        private static void ReadLiveSlot(
            ModuleInventoryPart module, int slot, out string partName, out int quantity)
        {
            partName = null;
            quantity = 0;
            if (module == null || module.storedParts == null) return;
            StoredPart stored;
            if (!module.storedParts.TryGetValue(slot, out stored) || stored == null) return;
            partName = stored.partName;
            quantity = stored.quantity;
        }

        private void RememberSlotReadings(ModuleInventoryPart module)
        {
            Part part = module != null ? module.part : null;
            if (part == null) return;
            uint containerPid = part.persistentId;
            int slots = module.InventorySlots;
            for (int s = 0; s < slots; s++)
            {
                ReadLiveSlot(module, s, out string name, out int quantity);
                foreignInventorySlotReadings[ForeignInventorySlotKey(containerPid, s)] =
                    new KeyValuePair<string, int>(name, quantity);
            }
        }

        /// <summary>
        /// The slot as the vessel's saved state holds it: the matching
        /// <c>ModuleInventoryPart</c> snapshot on the part's <c>ProtoPartSnapshot</c>, which
        /// stock writes on load and on save, not on a slot change. False when it cannot be
        /// read.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryReadSavedSlotCore(
            ModuleInventoryPart module, int slot, out string partName, out int quantity)
        {
            partName = null;
            quantity = 0;
            try
            {
                Part part = module != null ? module.part : null;
                ProtoPartSnapshot proto = part != null ? part.protoPartSnapshot : null;
                if (proto == null || proto.modules == null || part.Modules == null) return false;

                // The same ordinal among the part's ModuleInventoryPart modules.
                int ordinal = 0;
                for (int i = 0; i < part.Modules.Count; i++)
                {
                    if (ReferenceEquals(part.Modules[i], module)) break;
                    if (part.Modules[i] is ModuleInventoryPart) ordinal++;
                }
                int seen = 0;
                for (int i = 0; i < proto.modules.Count; i++)
                {
                    ProtoPartModuleSnapshot m = proto.modules[i];
                    if (m == null || m.moduleName != "ModuleInventoryPart") continue;
                    if (seen++ != ordinal) continue;
                    return ForeignInventoryTransfer.TryReadStoredSlot(
                        m.moduleValues, slot, out partName, out quantity);
                }
                return false;
            }
            catch (System.Exception ex)
            {
                ParsekLog.Verbose("Flight",
                    $"TryReadSavedSlotCore: read failed for slot={slot}: {ex.Message}");
                return false;
            }
        }

        #endregion
    }
}
