using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Parsek
{
    public partial class ParsekFlight
    {
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
        void OnModuleInventorySlotChanged(ModuleInventoryPart module, int slot)
        {
            Part part = module != null ? module.part : null;
            Vessel changed = part != null ? part.vessel : null;
            Vessel active = FlightGlobals.ActiveVessel;
            uint changedPid = changed != null ? changed.persistentId : 0u;
            uint activePid = active != null ? active.persistentId : 0u;
            uint containerPid = part != null ? part.persistentId : 0u;

            ReadLiveSlot(module, slot, out string partNameAfter, out int quantityAfter);

            Recording activeRec = null;
            if (activeTree != null && !string.IsNullOrEmpty(activeTree.ActiveRecordingId))
                activeTree.Recordings.TryGetValue(activeTree.ActiveRecordingId, out activeRec);

            ForeignInventoryMembership membership =
                ForeignInventoryTransfer.ResolveMembership(activeTree, changedPid);
            ForeignInventoryTransferVerdict verdict = ForeignInventoryTransfer.Evaluate(
                hasActiveTree: activeTree != null,
                recorderIsRecording: recorder != null && recorder.IsRecording,
                parsekWriteInProgress: ParsekInventoryWrites.InProgress,
                activeVesselIsEva: active != null && active.isEVA,
                activeVesselPid: activePid,
                activeRecordingVesselPid: activeRec != null ? activeRec.VesselPersistentId : 0u,
                changedVesselPid: changedPid,
                changedVesselIsEva: changed != null && changed.isEVA,
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
        void OnEvaConstructionPartAttached(Vessel vessel, Part part)
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
        void OnEvaConstructionPartDetached(Vessel vessel, Part part)
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
            if (activeTree != null && !string.IsNullOrEmpty(activeTree.ActiveRecordingId))
                activeTree.Recordings.TryGetValue(activeTree.ActiveRecordingId, out activeRec);

            ForeignInventoryMembership membership =
                ForeignInventoryTransfer.ResolveMembership(activeTree, changedPid);
            // Parsek never attaches or detaches a part, so no write fence applies here.
            ForeignInventoryTransferVerdict verdict = ForeignInventoryTransfer.Evaluate(
                hasActiveTree: activeTree != null,
                recorderIsRecording: recorder != null && recorder.IsRecording,
                parsekWriteInProgress: false,
                activeVesselIsEva: active != null && active.isEVA,
                activeVesselPid: activePid,
                activeRecordingVesselPid: activeRec != null ? activeRec.VesselPersistentId : 0u,
                changedVesselPid: changedPid,
                changedVesselIsEva: changed != null && changed.isEVA,
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
            var (bp, member) = ForeignInventoryTransfer.BuildMemberBranchData(
                kerbalRec.RecordingId,
                activeTree.Id,
                transfer.ut,
                v.persistentId,
                Recording.ResolveLocalizedName(v.vesselName) ?? "Unknown",
                kerbalRec.Generation);

            ConfigNode snapshot = VesselSpawner.TryBackupSnapshot(v);
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

            ForeignInventoryTransfer.AttachMember(activeTree, kerbalRec, bp, member, transfer);

            if (backgroundRecorder != null)
            {
                TrajectoryPoint initialPoint = BackgroundRecorder.CreateAbsoluteTrajectoryPointFromVessel(
                    v, Planetarium.GetUniversalTime(), preferRootPartSurfacePose: true);
                backgroundRecorder.OnVesselBackgrounded(
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

        void AppendForeignTransfer(Vessel v, SegmentEvent transfer,
            uint constructionPartPid = 0u, bool constructionAttached = false,
            string constructionPartName = null)
        {
            string recId;
            Recording member;
            if (!activeTree.BackgroundMap.TryGetValue(v.persistentId, out recId)
                || !activeTree.Recordings.TryGetValue(recId, out member) || member == null)
            {
                ParsekLog.Warn("Flight", string.Format(CultureInfo.InvariantCulture,
                    "Foreign inventory transfer: cargo partner vesselPid={0} lost its member " +
                    "recording before the append, nothing recorded", v.persistentId));
                return;
            }

            ForeignInventoryTransfer.AppendTransferEvent(member, transfer);
            // The member's end snapshot is the chain tip's spawn form; keep it current.
            ConfigNode snapshot = VesselSpawner.TryBackupSnapshot(v);
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
