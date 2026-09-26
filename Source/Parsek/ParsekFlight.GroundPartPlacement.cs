using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    public partial class ParsekFlight
    {
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

        void OnNewVesselCreatedForGroundPart(Vessel v)
        {
            lastCreatedVesselForGroundPart = v;
            lastCreatedVesselFrame = Time.frameCount;
        }

        void OnDeployGroundPart(string partName)
        {
            Vessel active = FlightGlobals.ActiveVessel;
            Vessel created = lastCreatedVesselForGroundPart;
            bool createdThisFrame = created != null && lastCreatedVesselFrame == Time.frameCount;
            string createdPartName = created != null && created.parts != null && created.parts.Count > 0
                ? created.parts[0]?.partInfo?.name
                : null;
            uint createdPid = created != null ? created.persistentId : 0u;

            Recording activeRec = null;
            if (activeTree != null && !string.IsNullOrEmpty(activeTree.ActiveRecordingId))
                activeTree.Recordings.TryGetValue(activeTree.ActiveRecordingId, out activeRec);

            bool isTreeMember = activeTree != null && createdPid != 0
                && (activeTree.BackgroundMap.ContainsKey(createdPid)
                    || (activeRec != null && activeRec.VesselPersistentId == createdPid));

            GroundPartPlacementVerdict verdict = GroundPartPlacement.EvaluatePlacement(
                hasActiveTree: activeTree != null,
                recorderIsRecording: recorder != null && recorder.IsRecording,
                activeVesselIsEva: active != null && active.isEVA,
                activeVesselPid: active != null ? active.persistentId : 0u,
                activeRecordingVesselPid: activeRec != null ? activeRec.VesselPersistentId : 0u,
                createdVesselPresent: created != null,
                createdThisFrame: createdThisFrame,
                createdPartCount: created?.parts?.Count ?? 0,
                createdPartName: createdPartName,
                placedPartName: partName,
                createdVesselIsTreeMember: isTreeMember);

            // The cache is single-use: a later onDeployGroundPart must not match it.
            lastCreatedVesselForGroundPart = null;
            lastCreatedVesselFrame = -1;

            if (verdict != GroundPartPlacementVerdict.Record)
            {
                ParsekLog.Info("Flight", GroundPartPlacement.FormatPlacementSkipLog(
                    verdict, partName, createdPid, active != null ? active.persistentId : 0u));
                return;
            }

            double placementUT = Planetarium.GetUniversalTime();
            ParsekLog.Info("Flight", string.Format(CultureInfo.InvariantCulture,
                "Ground part placement detected: part='{0}' vesselPid={1} kerbalRec={2} ut={3:F2}; " +
                "creating tree member next frame",
                partName, createdPid, activeRec.RecordingId, placementUT));
            StartCoroutine(DeferredGroundPartPlacement(
                activeTree.Id, activeRec.RecordingId, createdPid, placementUT));
        }

        /// <summary>
        /// Creates the placed part's tree member one frame after the placement: in the
        /// placement frame the parts exist but have not started, and the snapshot and
        /// first pose are taken from the settled vessel.
        /// </summary>
        IEnumerator DeferredGroundPartPlacement(
            string treeId, string parentRecId, uint vesselPid, double placementUT)
        {
            yield return null;

            if (activeTree == null || activeTree.Id != treeId)
            {
                ParsekLog.Info("Flight",
                    $"Ground part placement dropped: active tree changed before the deferred frame " +
                    $"(vesselPid={vesselPid} tree={treeId})");
                yield break;
            }
            Recording parentRec;
            if (!activeTree.Recordings.TryGetValue(parentRecId, out parentRec) || parentRec == null)
            {
                ParsekLog.Warn("Flight",
                    $"Ground part placement dropped: parent recording {parentRecId} left the tree " +
                    $"(vesselPid={vesselPid})");
                yield break;
            }
            Vessel v = FlightRecorder.FindVesselByPid(vesselPid);
            if (v == null || v.rootPart == null)
            {
                ParsekLog.Warn("Flight",
                    $"Ground part placement dropped: placed vessel pid={vesselPid} is gone before the deferred frame");
                yield break;
            }
            if (activeTree.BackgroundMap.ContainsKey(vesselPid))
            {
                ParsekLog.Verbose("Flight",
                    $"Ground part placement: vessel pid={vesselPid} already a tree member, skipping");
                yield break;
            }

            CreateGroundPartMember(parentRec, v, placementUT);
        }

        void CreateGroundPartMember(Recording parentRec, Vessel v, double placementUT)
        {
            Part part = v.rootPart;
            string partName = part.partInfo?.name ?? "unknown";
            uint partPid = part.persistentId;

            var (bp, member) = GroundPartPlacement.BuildPlacementBranchData(
                parentRec.RecordingId,
                activeTree.Id,
                placementUT,
                v.persistentId,
                Recording.ResolveLocalizedName(v.vesselName) ?? partName,
                parentRec.Generation);

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

            GroundPartPlacement.AttachMember(
                activeTree, parentRec, bp, member, partPid, partName, placementUT);

            if (backgroundRecorder != null)
            {
                TrajectoryPoint initialPoint = BackgroundRecorder.CreateAbsoluteTrajectoryPointFromVessel(
                    v, Planetarium.GetUniversalTime(), preferRootPartSurfacePose: true);
                backgroundRecorder.OnVesselBackgrounded(
                    v.persistentId, initialTrajectoryPoint: initialPoint);
            }
        }

        /// <summary>
        /// NOT a placement signal: stock also fires it whenever an unlinked experiment
        /// merely starts landed (a load into physics range, a Central Station removal).
        /// Placement is detected from onDeployGroundPart; this is diagnostics only.
        /// </summary>
        void OnGroundSciencePartDeployed(ModuleGroundSciencePart deployedPart)
        {
            Part p = deployedPart?.part;
            if (p == null) return;
            uint vesselPid = p.vessel != null ? p.vessel.persistentId : 0u;
            bool member = activeTree != null && activeTree.BackgroundMap.ContainsKey(vesselPid);
            ParsekLog.Verbose("Flight",
                $"onGroundSciencePartDeployed: part='{p.partInfo?.name ?? "unknown"}' pid={p.persistentId} " +
                $"vesselPid={vesselPid} treeMember={member} (not a placement signal, nothing recorded)");
        }

        void OnGroundSciencePartRemoved(ModuleGroundSciencePart removedPart)
        {
            Part p = removedPart?.part;
            if (p == null || p.vessel == null) return;
            GroundPartPlacement.HandleRemovedSignal(
                activeTree, p.vessel.persistentId, p.persistentId, p.partInfo?.name ?? "unknown",
                Planetarium.GetUniversalTime(), groundPartRetrievalPending);
        }

        /// <summary>
        /// The ground-part pick-up arm of the disassembly seam: true when the dying vessel
        /// is a placed-part member whose part went back into an inventory. Consumes the
        /// pending-retrieval entry.
        /// </summary>
        bool IsGroundPartRetrievalDeath(Vessel v, Recording rec)
        {
            bool isMember = GroundPartPlacement.IsPlacedPartMember(activeTree, rec);
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
    }
}
