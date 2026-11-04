using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // Part GameEvent subscriptions: part death, undock, decouple and joint-break handlers that queue background split checks.
    internal partial class BackgroundRecorder
    {
        #region GameEvent Subscriptions

        /// <summary>
        /// Subscribes to GameEvents for discrete part events (onPartDie, onPartJointBreak)
        /// that cannot be captured by per-frame polling. These events fire globally for all
        /// vessels; the handlers check if the part belongs to a background vessel.
        /// </summary>
        internal void SubscribePartEvents()
        {
            if (partEventsSubscribed) return;
            GameEvents.onPartDie.Add(OnBackgroundPartDie);
            GameEvents.onPartJointBreak.Add(OnBackgroundPartJointBreak);
            GameEvents.onPartUndock.Add(OnBackgroundPartUndock);
            GameEvents.onPartDeCouple.Add(OnBackgroundPartDeCouple);
            partEventsSubscribed = true;
            ParsekLog.Verbose("BgRecorder", "Subscribed to onPartDie, onPartJointBreak, onPartUndock and onPartDeCouple for background vessels");
        }

        internal void UnsubscribePartEvents()
        {
            if (!partEventsSubscribed) return;
            GameEvents.onPartDie.Remove(OnBackgroundPartDie);
            GameEvents.onPartJointBreak.Remove(OnBackgroundPartJointBreak);
            GameEvents.onPartUndock.Remove(OnBackgroundPartUndock);
            GameEvents.onPartDeCouple.Remove(OnBackgroundPartDeCouple);
            partEventsSubscribed = false;
            pendingBackgroundUndockPartPid = 0;
            pendingBackgroundUndockUT = double.NaN;
            ParsekLog.Verbose("BgRecorder", "Unsubscribed from onPartDie, onPartJointBreak, onPartUndock and onPartDeCouple");
        }

        /// <summary>
        /// Handles part death for background vessels. Looks up the dying part's vessel
        /// in the tree's BackgroundMap; if found and in loaded state, emits a Destroyed
        /// or ParachuteDestroyed event.
        /// </summary>
        internal void OnBackgroundPartDie(Part p)
        {
            if (tree == null) return;
            if (p?.vessel == null)
            {
                ParsekLog.VerboseRateLimited("BgRecorder", "bg-part-die-null",
                    "OnBackgroundPartDie: part or vessel is null");
                return;
            }

            uint vesselPid = p.vessel.persistentId;

            // Only handle background vessels (not the active/focused vessel)
            string recordingId;
            if (!tree.BackgroundMap.TryGetValue(vesselPid, out recordingId)) return;

            // Destroyed-recording guard: do not append new part events to a sealed
            // recording. The recording's PartEvents list is part of its terminal
            // payload — appending here after destruction would shift playback events
            // past the identity-loss UT.
            if (IsBackgroundRecordingDestroyed(recordingId))
            {
                ParsekLog.VerboseRateLimited("BgRecorder",
                    $"destroyed-skip.part_die.{vesselPid}",
                    $"OnBackgroundPartDie: skipping destroyed recording pid={vesselPid} rec={recordingId}",
                    60.0);
                return;
            }

            // Must have loaded state (part events only meaningful for loaded vessels)
            BackgroundVesselState state;
            if (!loadedStates.TryGetValue(vesselPid, out state)) return;

            Recording treeRec;
            if (!tree.Recordings.TryGetValue(recordingId, out treeRec)) return;

            bool hasChute = p.FindModuleImplementing<ModuleParachute>() != null;
            var evtType = FlightRecorder.ClassifyPartDeath(
                p.persistentId, hasChute, state.parachuteStates);

            treeRec.PartEvents.Add(new PartEvent
            {
                ut = Planetarium.GetUniversalTime(),
                partPersistentId = p.persistentId,
                eventType = evtType,
                partName = p.partInfo?.name ?? "unknown"
            });
            treeRec.MarkFilesDirty();

            ParsekLog.Verbose("BgRecorder",
                $"Part death on background vessel: {evtType} '{p.partInfo?.name}' " +
                $"pid={p.persistentId} vesselPid={vesselPid}");
            RefreshFinalizationCacheForVessel(p.vessel, recordingId, FinalizationCacheOwner.BackgroundLoaded,
                "background_part_die", force: true);
        }

        /// <summary>
        /// Notes a docking-port undock on a background vessel. A player reaches this by
        /// clicking Undock on a docked vessel from EVA (ModuleDockingNode.Undock is
        /// guiActiveUnfocused, externalToEVAOnly, unfocusedRange = 2 m) while the kerbal is
        /// the active vessel and the docked vessel is background-recorded. The joint break
        /// that follows in the same Part.Undock call reads this to stamp SplitCause UNDOCK.
        /// </summary>
        internal void OnBackgroundPartUndock(Part undockedPart)
        {
            if (tree == null || undockedPart?.vessel == null) return;
            uint vesselPid = undockedPart.vessel.persistentId;
            if (!tree.BackgroundMap.ContainsKey(vesselPid)) return;
            NoteBackgroundUndock(undockedPart.persistentId, Planetarium.GetUniversalTime());
            ParsekLog.Verbose("BgRecorder",
                $"OnBackgroundPartUndock: docking-port undock on background vessel " +
                $"part='{undockedPart.partInfo?.name}' pid={undockedPart.persistentId} vesselPid={vesselPid}");
        }

        /// <summary>
        /// Notes a clicked pre-attached docking-port Undock on a background vessel. Stock
        /// routes that Undock through Decouple() and Part.decouple, so no onPartUndock fires;
        /// onPartDeCouple(part) fires first, before the part's attachJoint is destroyed. The
        /// click itself is caught by DockingNodeUndockIntentPatch; staging the same port
        /// never arms it and stays DECOUPLE.
        /// </summary>
        internal void OnBackgroundPartDeCouple(Part decoupledPart)
        {
            if (tree == null || decoupledPart?.vessel == null) return;
            uint vesselPid = decoupledPart.vessel.persistentId;
            if (!tree.BackgroundMap.ContainsKey(vesselPid)) return;
            double ut = Planetarium.GetUniversalTime();
            if (!DockingPortSeparation.TryConsumePreAttachedUndock(
                    decoupledPart.persistentId, ut, "BgRecorder")) return;
            NoteBackgroundUndock(decoupledPart.persistentId, ut);
            ParsekLog.Verbose("BgRecorder",
                $"OnBackgroundPartDeCouple: pre-attached docking-port undock on background vessel " +
                $"part='{decoupledPart.partInfo?.name}' pid={decoupledPart.persistentId} vesselPid={vesselPid}");
        }

        internal void NoteBackgroundUndock(uint partPid, double ut)
        {
            pendingBackgroundUndockPartPid = partPid;
            pendingBackgroundUndockUT = ut;
        }

        /// <summary>
        /// Pure decision: the SplitCause of a background joint break.
        /// A joint that broke under force (breakForce &gt; 0: PartJoint.OnJointBreak, an
        /// overstress or impact) returns "BROKE_OFF", read as "Broke off". It is
        /// checked first because KSP's force path then calls Part.decouple too; the
        /// foreground marks the same break the same way. Otherwise the cause is "UNDOCK" when
        /// an undock note (onPartUndock, or onPartDeCouple of a pre-attached port) named the
        /// joint's child part at the same UT, else "DECOUPLE" (a decoupler, a dying part
        /// shedding its children, or a docking port's "Decouple Node").
        /// </summary>
        internal static string ClassifyBackgroundJointBreakCause(
            uint jointChildPartPid, double jointBreakUT, float breakForce,
            uint pendingUndockPartPid, double pendingUndockUT)
        {
            bool sameUT = pendingUndockUT == jointBreakUT;
            bool undock = pendingUndockPartPid != 0
                && jointChildPartPid == pendingUndockPartPid
                && sameUT;
            bool brokeUnderForce = breakForce > 0f;
            string cause = brokeUnderForce
                ? SegmentBoundaryLogic.BrokeOffSplitCause
                : (undock ? "UNDOCK" : "DECOUPLE");
            ParsekLog.Verbose("BgRecorder",
                $"ClassifyBackgroundJointBreakCause: partPid={jointChildPartPid} " +
                $"breakForce={breakForce.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"pendingUndockPartPid={pendingUndockPartPid} " +
                $"sameUT={(sameUT ? "true" : "false")} => cause={cause ?? "BROKE_OFF"}");
            return cause;
        }

        /// <summary>
        /// Handles part joint breaks for background vessels. Looks up the child part's
        /// vessel in the tree's BackgroundMap; if found and in loaded state, emits a
        /// Decoupled event (with structural-joint and dedup guards).
        /// </summary>
        internal void OnBackgroundPartJointBreak(PartJoint joint, float breakForce)
        {
            if (tree == null) return;
            if (joint?.Child?.vessel == null)
            {
                ParsekLog.VerboseRateLimited("BgRecorder", "bg-joint-break-null",
                    "OnBackgroundPartJointBreak: joint, child, or vessel is null");
                return;
            }

            uint vesselPid = joint.Child.vessel.persistentId;

            // Only handle background vessels
            string recordingId;
            if (!tree.BackgroundMap.TryGetValue(vesselPid, out recordingId)) return;

            // Destroyed-recording guard: a sealed recording must not accept new
            // joint-break / decouple events after destruction.
            if (IsBackgroundRecordingDestroyed(recordingId))
            {
                ParsekLog.VerboseRateLimited("BgRecorder",
                    $"destroyed-skip.part_joint_break.{vesselPid}",
                    $"OnBackgroundPartJointBreak: skipping destroyed recording pid={vesselPid} rec={recordingId}",
                    60.0);
                return;
            }

            BackgroundVesselState state;
            if (!loadedStates.TryGetValue(vesselPid, out state)) return;

            Recording treeRec;
            if (!tree.Recordings.TryGetValue(recordingId, out treeRec)) return;

            // Skip non-structural joint breaks (same logic as FlightRecorder)
            var childAttachJoint = joint.Child.attachJoint;
            if (!FlightRecorder.IsStructuralJointBreak(joint == childAttachJoint, childAttachJoint != null))
            {
                ParsekLog.VerboseRateLimited("BgRecorder",
                    $"bg-joint-break-nonstructural-{joint.Child.persistentId}",
                    $"OnBackgroundPartJointBreak: non-structural joint break on " +
                    $"'{joint.Child.partInfo?.name}' pid={joint.Child.persistentId} " +
                    $"vesselPid={vesselPid} (breakForce={breakForce:F1}), skipping");
                return;
            }

            // Skip duplicate decoupled events for the same part
            if (state.decoupledPartIds.Contains(joint.Child.persistentId))
            {
                ParsekLog.VerboseRateLimited("BgRecorder",
                    $"bg-joint-break-dup-{joint.Child.persistentId}",
                    $"OnBackgroundPartJointBreak: duplicate for already-decoupled " +
                    $"'{joint.Child.partInfo?.name}' pid={joint.Child.persistentId} " +
                    $"vesselPid={vesselPid}, skipping");
                return;
            }
            state.decoupledPartIds.Add(joint.Child.persistentId);
            double jointBreakUT = Planetarium.GetUniversalTime();
            string splitCause = ClassifyBackgroundJointBreakCause(
                joint.Child.persistentId, jointBreakUT, breakForce,
                pendingBackgroundUndockPartPid, pendingBackgroundUndockUT);
            if (splitCause == "UNDOCK")
            {
                pendingBackgroundUndockPartPid = 0;
                pendingBackgroundUndockUT = double.NaN;
            }

            treeRec.PartEvents.Add(new PartEvent
            {
                ut = jointBreakUT,
                partPersistentId = joint.Child.persistentId,
                eventType = PartEventType.Decoupled,
                partName = joint.Child.partInfo?.name ?? "unknown"
            });
            treeRec.MarkFilesDirty();

            ParsekLog.Verbose("BgRecorder",
                $"Part joint break on background vessel: Decoupled " +
                $"'{joint.Child.partInfo?.name}' pid={joint.Child.persistentId} " +
                $"vesselPid={vesselPid} breakForce={breakForce:F1} cause={splitCause ?? "BROKE_OFF"}");

            var jointBreakInvolved = new List<Vessel>(2);
            if (joint.Child.vessel != null) jointBreakInvolved.Add(joint.Child.vessel);
            if (joint.Host?.vessel != null && joint.Host.vessel != joint.Child.vessel)
                jointBreakInvolved.Add(joint.Host.vessel);
            AppendStructuralEventSnapshot(
                jointBreakUT, jointBreakInvolved, "JointBreak", preferRootPartSurfacePose: true);

            RefreshFinalizationCacheForVessel(joint.Child.vessel, recordingId, FinalizationCacheOwner.BackgroundLoaded,
                "background_joint_break", force: true);

            // Schedule a deferred split check for this background vessel.
            // KSP needs one frame to finalize vessel splits after joint breaks.
            if (!pendingBackgroundSplitChecks.ContainsKey(vesselPid))
            {
                double branchUT = jointBreakUT;

                // Snapshot all current vessel PIDs so we can identify NEW ones next frame
                var snapshot = new HashSet<uint>();
                if (FlightGlobals.Vessels != null)
                {
                    for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
                    {
                        Vessel v = FlightGlobals.Vessels[i];
                        if (v == null) continue;
                        if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;
                        snapshot.Add(v.persistentId);
                    }
                }
                preBreakVesselPidSnapshots[vesselPid] = snapshot;
                TrajectoryPoint? parentBoundaryPoint = joint.Child.vessel != null
                    ? (TrajectoryPoint?)CreateAbsoluteTrajectoryPointFromVessel(
                        joint.Child.vessel, branchUT, preferRootPartSurfacePose: true)
                    : null;
                if (parentBoundaryPoint.HasValue)
                    parentBoundaryPoint = FlightRecorder.ApplyStructuralEventFlag(parentBoundaryPoint.Value);
                pendingBackgroundSplitChecks[vesselPid] = (branchUT, recordingId, parentBoundaryPoint, splitCause);

                ParsekLog.Info("BgRecorder",
                    $"Scheduled deferred split check for background vessel: " +
                    $"parentPid={vesselPid} branchUT={branchUT:F1} " +
                    $"preBreakVesselCount={snapshot.Count} cause={splitCause ?? "BROKE_OFF"}");
            }
        }

        #endregion
    }
}
