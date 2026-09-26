using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Verdict of <see cref="GroundPartPlacement.EvaluatePlacement"/>. Everything but
    /// <see cref="Record"/> is a skip, and each skip names the missing condition.
    /// </summary>
    internal enum GroundPartPlacementVerdict
    {
        Record,
        NoActiveTree,
        NotRecording,
        ActiveVesselNotEva,
        ActiveVesselNotRecorded,
        NoCreatedVessel,
        CreatedVesselNotThisFrame,
        CreatedVesselNotSinglePart,
        PartNameMismatch,
        AlreadyTreeMember
    }

    /// <summary>
    /// Pure decisions behind the EVA ground-part placement tree member (design:
    /// docs/parsek-flight-recorder-design.md section 4.11). A ground part a kerbal places
    /// on EVA is its OWN new vessel (stock <c>ModuleInventoryPart.DeployGroundPart</c> ->
    /// <c>Game.AddVessel</c>), so it gets its own recording under a
    /// <see cref="BranchPointType.GroundPartPlaced"/> branch point whose parent is the
    /// kerbal's recording. The kerbal's recording keeps going and its
    /// <c>ChildBranchPointId</c> is left alone.
    /// </summary>
    internal static class GroundPartPlacement
    {
        internal const string RetrievedTerminalReason = "GroundPartRetrieved";
        internal const string RetrievedLogReason = "ground-part-retrieved";

        /// <summary>
        /// Is this <c>onDeployGroundPart</c> a real placement by the kerbal the tree is
        /// recording? Stock fires <c>onNewVesselCreated(vessel)</c> inside
        /// <c>Game.AddVessel</c> and then <c>onDeployGroundPart(partName)</c> from the same
        /// <c>ModuleInventoryPart.OnUpdate</c> call on the ACTIVE vessel, so a real placement
        /// is: the created vessel was announced this frame, is single-part, carries the
        /// placed part's name, and is not already a tree member; and the active vessel is an
        /// EVA kerbal whose recording is the tree's active, recording foreground recording.
        /// <c>onGroundSciencePartDeployed</c> is deliberately NOT an input: it also fires when
        /// an old unlinked experiment merely starts landed.
        /// </summary>
        internal static GroundPartPlacementVerdict EvaluatePlacement(
            bool hasActiveTree,
            bool recorderIsRecording,
            bool activeVesselIsEva,
            uint activeVesselPid,
            uint activeRecordingVesselPid,
            bool createdVesselPresent,
            bool createdThisFrame,
            int createdPartCount,
            string createdPartName,
            string placedPartName,
            bool createdVesselIsTreeMember)
        {
            if (!hasActiveTree) return GroundPartPlacementVerdict.NoActiveTree;
            if (!recorderIsRecording) return GroundPartPlacementVerdict.NotRecording;
            if (!activeVesselIsEva) return GroundPartPlacementVerdict.ActiveVesselNotEva;
            if (activeVesselPid == 0 || activeVesselPid != activeRecordingVesselPid)
                return GroundPartPlacementVerdict.ActiveVesselNotRecorded;
            if (!createdVesselPresent) return GroundPartPlacementVerdict.NoCreatedVessel;
            if (!createdThisFrame) return GroundPartPlacementVerdict.CreatedVesselNotThisFrame;
            if (createdPartCount != 1) return GroundPartPlacementVerdict.CreatedVesselNotSinglePart;
            if (string.IsNullOrEmpty(placedPartName)
                || !string.Equals(createdPartName, placedPartName, StringComparison.Ordinal))
                return GroundPartPlacementVerdict.PartNameMismatch;
            if (createdVesselIsTreeMember) return GroundPartPlacementVerdict.AlreadyTreeMember;
            return GroundPartPlacementVerdict.Record;
        }

        /// <summary>
        /// The created vessel's part count and first part name as the gate reads them. In
        /// the placement frame <c>Game.AddVessel</c> has loaded the ProtoVessel but the live
        /// <c>Vessel.parts</c> list is still empty (measured: EVA-5 flight
        /// <c>2026-09-26_1831</c> refused the real placement as
        /// <c>CreatedVesselNotSinglePart</c>), so the ProtoVessel's part snapshots - the
        /// single PART node stock built - are authoritative; the live list is the fallback.
        /// </summary>
        internal static void ResolveCreatedPartShape(
            int protoPartCount, string protoFirstPartName,
            int livePartCount, string liveFirstPartName,
            out int partCount, out string firstPartName)
        {
            if (protoPartCount > 0)
            {
                partCount = protoPartCount;
                firstPartName = protoFirstPartName;
                return;
            }
            partCount = livePartCount;
            firstPartName = livePartCount > 0 ? liveFirstPartName : null;
        }

        /// <summary>
        /// Builds the branch point and the member recording. The member is a fresh
        /// background-recorded vessel: NOT debris (no TTL), NO parent-anchor contract (it
        /// is static on the ground), generation = parent + 1.
        /// </summary>
        internal static (BranchPoint bp, Recording member) BuildPlacementBranchData(
            string parentRecordingId,
            string treeId,
            double placementUT,
            uint placedVesselPid,
            string placedVesselName,
            int parentGeneration)
        {
            string memberId = Guid.NewGuid().ToString("N");
            string bpId = Guid.NewGuid().ToString("N");

            var bp = new BranchPoint
            {
                Id = bpId,
                UT = placementUT,
                Type = BranchPointType.GroundPartPlaced,
                ParentRecordingIds = new List<string> { parentRecordingId },
                ChildRecordingIds = new List<string> { memberId }
            };

            var member = new Recording
            {
                RecordingId = memberId,
                TreeId = treeId,
                VesselPersistentId = placedVesselPid,
                VesselName = placedVesselName,
                ParentBranchPointId = bpId,
                ExplicitStartUT = placementUT,
                IsDebris = false,
                Generation = parentGeneration + 1
            };

            return (bp, member);
        }

        internal static PartEvent BuildInventoryEvent(
            PartEventType eventType, double ut, uint partPid, string partName)
        {
            return new PartEvent
            {
                ut = ut,
                partPersistentId = partPid,
                eventType = eventType,
                partName = string.IsNullOrEmpty(partName) ? "unknown" : partName,
                moduleIndex = 0
            };
        }

        /// <summary>
        /// True when <paramref name="rec"/> was created by a
        /// <see cref="BranchPointType.GroundPartPlaced"/> branch point of
        /// <paramref name="tree"/>.
        /// </summary>
        internal static bool IsPlacedPartMember(RecordingTree tree, Recording rec)
        {
            if (tree?.BranchPoints == null || rec == null
                || string.IsNullOrEmpty(rec.ParentBranchPointId))
                return false;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                BranchPoint bp = tree.BranchPoints[i];
                if (bp == null || bp.Id != rec.ParentBranchPointId) continue;
                return bp.Type == BranchPointType.GroundPartPlaced;
            }
            return false;
        }

        /// <summary>
        /// Appends the pick-up event to the member, once. Stock fires
        /// <c>onGroundSciencePartRemoved</c> twice per pick-up (<c>RetrievePart</c>, then
        /// <c>OnRetractCompleted</c> about 4.7 s later); the FIRST is the moment the kerbal
        /// took the part, so a second one for the same part is dropped. Returns false when
        /// dropped.
        /// </summary>
        internal static bool TryAppendRemovedEvent(
            Recording member, uint partPid, string partName, double ut)
        {
            if (member == null) return false;
            if (member.PartEvents != null)
            {
                for (int i = 0; i < member.PartEvents.Count; i++)
                {
                    PartEvent e = member.PartEvents[i];
                    if (e.eventType == PartEventType.InventoryPartRemoved
                        && e.partPersistentId == partPid)
                        return false;
                }
            }
            AppendPartEventSorted(member,
                BuildInventoryEvent(PartEventType.InventoryPartRemoved, ut, partPid, partName));
            return true;
        }

        /// <summary>
        /// Appends a part event to a background-recorded tree recording in stable UT order
        /// (the playback cursor walks the list and stops at the first future event) and
        /// marks its sidecar dirty.
        /// </summary>
        internal static void AppendPartEventSorted(Recording rec, PartEvent evt)
        {
            if (rec == null) return;
            rec.PartEvents.Add(evt);
            var sorted = FlightRecorder.StableSortByUT(rec.PartEvents, e => e.ut);
            rec.PartEvents.Clear();
            rec.PartEvents.AddRange(sorted);
            rec.MarkFilesDirty();
        }

        /// <summary>
        /// Did a placed ground part die because the kerbal picked it up? Only a placed-part
        /// member qualifies. Evidence is either the pick-up event already seen for this
        /// vessel (<c>onGroundSciencePartRemoved</c>, science parts) or the part's own
        /// <c>ModuleGroundPart.deployedOnGround</c> reading false at the destroy seam: stock
        /// clears it at the start of <c>RetrievePart</c>, which is the only way the Central
        /// Station (no <c>ModuleGroundSciencePart</c>, so no Removed event) is picked up,
        /// while a crash leaves it true. An unreadable field (<c>null</c>) is no evidence.
        /// </summary>
        internal static bool IsRetrievalDeath(
            bool isPlacedPartMember,
            bool removedSignalSeen,
            bool? deployedOnGround)
        {
            if (!isPlacedPartMember) return false;
            if (removedSignalSeen) return true;
            return deployedOnGround.HasValue && !deployedOnGround.Value;
        }

        /// <summary>
        /// Wires a freshly built member into the tree: the Placed event on the member
        /// (keyed by the placed part's own pid, which its snapshot carries), the branch
        /// point, the recording and its BackgroundMap entry. The caller then hands the
        /// vessel to the background recorder. The parent's ChildBranchPointId is NOT
        /// touched: the kerbal keeps recording.
        /// </summary>
        internal static void AttachMember(
            RecordingTree tree, Recording parentRec, BranchPoint bp, Recording member,
            uint partPid, string partName, double placementUT)
        {
            AppendPartEventSorted(member, BuildInventoryEvent(
                PartEventType.InventoryPartPlaced, placementUT, partPid, partName));
            tree.BranchPoints.Add(bp);
            tree.AddOrReplaceRecording(member);
            tree.BackgroundMap[member.VesselPersistentId] = member.RecordingId;

            ParsekLog.Info("Flight", FormatMemberCreatedLog(
                partName, member.VesselPersistentId, partPid, member.RecordingId,
                parentRec?.RecordingId, bp.Id, placementUT, member.VesselSnapshot != null));
            ParsekLog.Info("Flight", string.Format(CultureInfo.InvariantCulture,
                "Part event captured: InventoryPartPlaced '{0}' pid={1} via onDeployGroundPart rec={2}",
                partName, partPid, member.RecordingId));
        }

        internal enum RemovedSignalOutcome { NotAMember, Recorded, Deduplicated }

        /// <summary>
        /// Handles one <c>onGroundSciencePartRemoved</c>: when the vessel is a placed-part
        /// member of <paramref name="tree"/>, marks it retrieval-pending and records the
        /// FIRST pick-up event on the member. Anything else records nothing.
        /// </summary>
        internal static RemovedSignalOutcome HandleRemovedSignal(
            RecordingTree tree, uint vesselPid, uint partPid, string partName, double ut,
            HashSet<uint> retrievalPending)
        {
            string recId;
            Recording member;
            if (tree == null
                || !tree.BackgroundMap.TryGetValue(vesselPid, out recId)
                || !tree.Recordings.TryGetValue(recId, out member)
                || !IsPlacedPartMember(tree, member))
            {
                ParsekLog.Verbose("Flight", string.Format(CultureInfo.InvariantCulture,
                    "onGroundSciencePartRemoved: part='{0}' vesselPid={1} is not a placed-part " +
                    "tree member, nothing recorded", partName, vesselPid));
                return RemovedSignalOutcome.NotAMember;
            }

            retrievalPending?.Add(vesselPid);
            if (TryAppendRemovedEvent(member, partPid, partName, ut))
            {
                ParsekLog.Info("Flight", string.Format(CultureInfo.InvariantCulture,
                    "Part event captured: InventoryPartRemoved '{0}' pid={1} via " +
                    "onGroundSciencePartRemoved rec={2}", partName, partPid, member.RecordingId));
                return RemovedSignalOutcome.Recorded;
            }
            ParsekLog.Verbose("Flight", string.Format(CultureInfo.InvariantCulture,
                "onGroundSciencePartRemoved: repeat pick-up signal for part='{0}' pid={1} rec={2} " +
                "(retract completed), deduplicated", partName, partPid, member.RecordingId));
            return RemovedSignalOutcome.Deduplicated;
        }

        internal static string FormatMemberCreatedLog(
            string partName, uint vesselPid, uint partPid, string memberRecId,
            string parentRecId, string bpId, double placementUT, bool hasSnapshot)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Ground part placed: tree member created part='{0}' vesselPid={1} partPid={2} " +
                "rec={3} parentRec={4} bp={5} ut={6:F2} snapshot={7}",
                string.IsNullOrEmpty(partName) ? "(null)" : partName,
                vesselPid,
                partPid,
                memberRecId ?? "(null)",
                parentRecId ?? "(null)",
                bpId ?? "(null)",
                placementUT,
                hasSnapshot);
        }

        internal static string FormatPlacementSkipLog(
            GroundPartPlacementVerdict verdict, string placedPartName,
            uint createdVesselPid, uint activeVesselPid,
            int createdPartCount = 0, string createdPartName = null)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Ground part placement not recorded: reason={0} part='{1}' createdVesselPid={2} activePid={3} " +
                "createdParts={4} createdPart='{5}'",
                verdict,
                string.IsNullOrEmpty(placedPartName) ? "(null)" : placedPartName,
                createdVesselPid,
                activeVesselPid,
                createdPartCount,
                string.IsNullOrEmpty(createdPartName) ? "(null)" : createdPartName);
        }
    }
}
