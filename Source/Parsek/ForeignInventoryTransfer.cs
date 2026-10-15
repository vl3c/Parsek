using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Verdict of <see cref="ForeignInventoryTransfer.Evaluate"/>. <see cref="Adopt"/> and
    /// <see cref="AppendToMember"/> record; everything else is a skip that names the
    /// condition it failed.
    /// </summary>
    internal enum ForeignInventoryTransferVerdict
    {
        Adopt,
        AppendToMember,
        NoActiveTree,
        NotRecording,
        ParsekWrite,
        ActiveVesselNotEva,
        ActiveVesselNotRecorded,
        NoChangedVessel,
        ChangedVesselIsActive,
        ChangedVesselIsGhost,
        ChangedVesselIsEva,
        ChangedVesselIsTreeMember
    }

    /// <summary>How the vessel whose inventory changed relates to the active tree.</summary>
    internal enum ForeignInventoryMembership
    {
        /// <summary>No recording of the tree carries the vessel: it is foreign.</summary>
        None,
        /// <summary>The vessel joined the tree through an earlier transfer.</summary>
        TransferMember,
        /// <summary>Any other tree vessel; its own recording's end snapshot carries the change.</summary>
        OtherMember
    }

    /// <summary>
    /// Pure decisions behind the foreign-vessel inventory transfer (design:
    /// docs/parsek-flight-recorder-design.md section 4.12, todo
    /// EVA-INVENTORY-MOVE-TO-FOREIGN-VESSEL-UNCLAIMED). When the EVA kerbal the tree is
    /// recording moves a stored part into or out of a container on a vessel that is not a
    /// member of the tree, that vessel joins the tree as a background member under an
    /// <see cref="BranchPointType.InventoryTransfer"/> branch point whose parent is the
    /// kerbal's recording (the kerbal keeps recording, as for a placed ground part). The
    /// member carries the transfer as a <see cref="SegmentEventType.PartAdded"/> /
    /// <see cref="SegmentEventType.PartRemoved"/> event, a ghosting trigger, so the ghost
    /// chain walker claims the vessel through its background-event scan and the member's
    /// own recording is the chain tip: after a rewind the vessel is a ghost until the
    /// member ends and then spawns in its recorded post-transfer form (design 12.5 / 12.6).
    /// </summary>
    internal static class ForeignInventoryTransfer
    {
        internal const string DetailsPrefix = "inventory";

        /// <summary>
        /// Is this <c>onModuleInventorySlotChanged</c> a transfer the tree must record? Stock
        /// fires it from <c>ModuleInventoryPart.StoreCargoPartAtSlot</c>,
        /// <c>ClearPartAtSlot</c>, <c>UpdateStackAmountAtSlot</c> and
        /// <c>RemoveNPartsFromInventory</c> on the module whose slot changed (never from
        /// <c>OnLoad</c>, which fires only <c>onModuleInventoryChanged</c>), so a recorded
        /// transfer is: the tree is recording its active EVA kerbal in the foreground, the
        /// changed module sits on another real vessel (not a ghost, not a kerbal), and the
        /// change is not one of Parsek's own supply-route writes. A vessel with no recording
        /// in the tree is adopted; an earlier cargo partner gets one more event; any other
        /// tree vessel is left to its own recording, whose end snapshot carries the change.
        /// </summary>
        internal static ForeignInventoryTransferVerdict Evaluate(
            bool hasActiveTree,
            bool recorderIsRecording,
            bool parsekWriteInProgress,
            bool activeVesselIsEva,
            uint activeVesselPid,
            uint activeRecordingVesselPid,
            uint changedVesselPid,
            bool changedVesselIsEva,
            bool changedVesselIsGhost,
            ForeignInventoryMembership membership)
        {
            if (!hasActiveTree) return ForeignInventoryTransferVerdict.NoActiveTree;
            if (!recorderIsRecording) return ForeignInventoryTransferVerdict.NotRecording;
            if (parsekWriteInProgress) return ForeignInventoryTransferVerdict.ParsekWrite;
            if (!activeVesselIsEva) return ForeignInventoryTransferVerdict.ActiveVesselNotEva;
            if (activeVesselPid == 0 || activeVesselPid != activeRecordingVesselPid)
                return ForeignInventoryTransferVerdict.ActiveVesselNotRecorded;
            if (changedVesselPid == 0) return ForeignInventoryTransferVerdict.NoChangedVessel;
            if (changedVesselPid == activeVesselPid)
                return ForeignInventoryTransferVerdict.ChangedVesselIsActive;
            if (changedVesselIsGhost) return ForeignInventoryTransferVerdict.ChangedVesselIsGhost;
            if (changedVesselIsEva) return ForeignInventoryTransferVerdict.ChangedVesselIsEva;
            switch (membership)
            {
                case ForeignInventoryMembership.TransferMember:
                    return ForeignInventoryTransferVerdict.AppendToMember;
                case ForeignInventoryMembership.OtherMember:
                    return ForeignInventoryTransferVerdict.ChangedVesselIsTreeMember;
                default:
                    return ForeignInventoryTransferVerdict.Adopt;
            }
        }

        /// <summary>
        /// The event type of one slot change: fewer parts in the slot than before is a
        /// removal, otherwise an addition. With no earlier reading of the slot (the first
        /// change seen on that container) an emptied slot is a removal and an occupied one
        /// an addition. Both types are ghosting triggers, so the direction never changes
        /// the claim; it is the timeline's record of what happened.
        /// </summary>
        internal static SegmentEventType ClassifySlotChange(int? quantityBefore, int quantityAfter)
        {
            if (quantityBefore.HasValue)
                return quantityAfter < quantityBefore.Value
                    ? SegmentEventType.PartRemoved
                    : SegmentEventType.PartAdded;
            return quantityAfter <= 0 ? SegmentEventType.PartRemoved : SegmentEventType.PartAdded;
        }

        /// <summary>
        /// Reads one slot from a saved <c>ModuleInventoryPart</c> MODULE node (the stock
        /// <c>STOREDPARTS / STOREDPART { slotIndex, partName, quantity }</c> layout, as
        /// <c>StoredPart.Save</c> writes it). True with the slot's part and quantity when the
        /// node holds it; true with quantity 0 when the node is readable but the slot is
        /// empty; false when there is no node.
        /// </summary>
        internal static bool TryReadStoredSlot(
            ConfigNode moduleNode, int slot, out string partName, out int quantity)
        {
            partName = null;
            quantity = 0;
            if (moduleNode == null) return false;
            ConfigNode stored = moduleNode.GetNode("STOREDPARTS");
            if (stored == null) return true;
            ConfigNode[] parts = stored.GetNodes("STOREDPART");
            for (int i = 0; i < parts.Length; i++)
            {
                int index;
                if (!int.TryParse(parts[i].GetValue("slotIndex"), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out index) || index != slot)
                    continue;
                partName = parts[i].GetValue("partName");
                int q;
                quantity = int.TryParse(parts[i].GetValue("quantity"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out q) ? q : 1;
                return true;
            }
            return true;
        }

        internal static SegmentEvent BuildTransferSegmentEvent(
            double ut, SegmentEventType type, string partName, int slot, int quantityAfter,
            uint containerPartPid)
        {
            return new SegmentEvent
            {
                ut = ut,
                type = type,
                details = string.Format(CultureInfo.InvariantCulture,
                    "{0} part={1} slot={2} qty={3} container={4}",
                    DetailsPrefix,
                    string.IsNullOrEmpty(partName) ? "unknown" : partName,
                    slot,
                    quantityAfter,
                    containerPartPid)
            };
        }

        /// <summary>
        /// How <paramref name="vesselPid"/> relates to <paramref name="tree"/>: an earlier
        /// cargo partner (its BackgroundMap recording is an InventoryTransfer child), another
        /// tree vessel (any other BackgroundMap entry, the active recording, or any recording
        /// of the tree that carried the pid), or foreign.
        /// </summary>
        internal static ForeignInventoryMembership ResolveMembership(
            RecordingTree tree, uint vesselPid)
        {
            if (tree == null || vesselPid == 0) return ForeignInventoryMembership.None;
            string recId;
            Recording rec;
            if (tree.BackgroundMap != null
                && tree.BackgroundMap.TryGetValue(vesselPid, out recId)
                && tree.Recordings.TryGetValue(recId, out rec) && rec != null)
            {
                return IsTransferMember(tree, rec)
                    ? ForeignInventoryMembership.TransferMember
                    : ForeignInventoryMembership.OtherMember;
            }
            foreach (Recording r in tree.Recordings.Values)
            {
                if (r != null && r.VesselPersistentId == vesselPid)
                    return ForeignInventoryMembership.OtherMember;
            }
            return ForeignInventoryMembership.None;
        }

        /// <summary>
        /// True when <paramref name="rec"/> was created by an
        /// <see cref="BranchPointType.InventoryTransfer"/> branch point of <paramref name="tree"/>.
        /// </summary>
        internal static bool IsTransferMember(RecordingTree tree, Recording rec)
        {
            if (tree?.BranchPoints == null || rec == null
                || string.IsNullOrEmpty(rec.ParentBranchPointId))
                return false;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                BranchPoint bp = tree.BranchPoints[i];
                if (bp == null || bp.Id != rec.ParentBranchPointId) continue;
                return bp.Type == BranchPointType.InventoryTransfer;
            }
            return false;
        }

        /// <summary>
        /// Builds the branch point and the member recording. The member is an ordinary
        /// background-recorded vessel: NOT debris (no TTL), NO parent-anchor contract (the
        /// kerbal walks; the vessel it changed does not follow him), generation = parent + 1.
        /// The branch point names no target vessel: the claim comes from the member's own
        /// transfer event (the chain walker's background-event scan).
        /// </summary>
        internal static (BranchPoint bp, Recording member) BuildMemberBranchData(
            string parentRecordingId,
            string treeId,
            double transferUT,
            uint foreignVesselPid,
            string foreignVesselName,
            int parentGeneration)
        {
            string memberId = Guid.NewGuid().ToString("N");
            string bpId = Guid.NewGuid().ToString("N");

            var bp = new BranchPoint
            {
                Id = bpId,
                UT = transferUT,
                Type = BranchPointType.InventoryTransfer,
                ParentRecordingIds = new List<string> { parentRecordingId },
                ChildRecordingIds = new List<string> { memberId }
            };

            var member = new Recording
            {
                RecordingId = memberId,
                TreeId = treeId,
                VesselPersistentId = foreignVesselPid,
                VesselName = foreignVesselName,
                ParentBranchPointId = bpId,
                ExplicitStartUT = transferUT,
                IsDebris = false,
                Generation = parentGeneration + 1
            };

            return (bp, member);
        }

        /// <summary>
        /// Wires a freshly built member into the tree: the transfer event on the member, the
        /// branch point, the recording and its BackgroundMap entry. The caller then hands the
        /// vessel to the background recorder. The kerbal's ChildBranchPointId is NOT touched:
        /// he keeps recording.
        /// </summary>
        internal static void AttachMember(
            RecordingTree tree, Recording parentRec, BranchPoint bp, Recording member,
            SegmentEvent transfer)
        {
            AppendTransferEvent(member, transfer);
            tree.BranchPoints.Add(bp);
            tree.AddOrReplaceRecording(member);
            tree.BackgroundMap[member.VesselPersistentId] = member.RecordingId;

            ParsekLog.Info("Flight", string.Format(CultureInfo.InvariantCulture,
                "Foreign inventory transfer: vessel joined the tree vesselPid={0} name='{1}' rec={2} " +
                "parentRec={3} bp={4} ut={5:F2} event={6} details='{7}' snapshot={8}",
                member.VesselPersistentId,
                member.VesselName ?? "(null)",
                member.RecordingId,
                parentRec?.RecordingId ?? "(null)",
                bp.Id,
                bp.UT,
                transfer.type,
                transfer.details ?? "(none)",
                member.VesselSnapshot != null));
        }

        /// <summary>
        /// Appends one transfer event to a cargo partner's recording in stable UT order and
        /// marks its sidecar dirty (the background recorder never rewrites a member's
        /// SegmentEvents, so this list is the member's only copy).
        /// </summary>
        internal static void AppendTransferEvent(Recording member, SegmentEvent transfer)
        {
            if (member == null) return;
            if (member.SegmentEvents == null) member.SegmentEvents = new List<SegmentEvent>();
            member.SegmentEvents.Add(transfer);
            var sorted = FlightRecorder.StableSortByUT(member.SegmentEvents, e => e.ut);
            member.SegmentEvents.Clear();
            member.SegmentEvents.AddRange(sorted);
            member.MarkFilesDirty();
        }

        internal static string FormatSkipLog(
            ForeignInventoryTransferVerdict verdict, uint changedVesselPid, uint activeVesselPid,
            string partName, int slot)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Foreign inventory transfer not recorded: reason={0} changedVesselPid={1} activePid={2} " +
                "part='{3}' slot={4}",
                verdict,
                changedVesselPid,
                activeVesselPid,
                string.IsNullOrEmpty(partName) ? "(none)" : partName,
                slot);
        }
    }

    /// <summary>
    /// Marks Parsek's own writes into a live inventory (the supply-route pickup and
    /// delivery writers), so the foreign-transfer handler does not read them as a kerbal's
    /// transfer. Main-thread only; nests.
    /// </summary>
    internal static class ParsekInventoryWrites
    {
        private static int depth;

        internal static bool InProgress => depth > 0;

        internal static IDisposable Begin()
        {
            depth++;
            return new Scope();
        }

        private sealed class Scope : IDisposable
        {
            private bool disposed;

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                if (depth > 0) depth--;
            }
        }

        internal static void ResetForTesting()
        {
            depth = 0;
        }
    }
}
