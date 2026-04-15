using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Result of classifying a joint break event.
    /// </summary>
    internal enum JointBreakResult
    {
        /// <summary>Parts broke off but the vessel stayed as one connected assembly.</summary>
        WithinSegment,

        /// <summary>Vessel physically split and the new vessel has a controller (command pod/probe core).</summary>
        StructuralSplit,

        /// <summary>Vessel physically split but the new vessel is uncontrolled debris.</summary>
        DebrisSplit
    }

    /// <summary>
    /// Pure static logic for the segment boundary rule: only physical structural
    /// separation creates new tree events. Non-splitting breakage emits SegmentEvents instead.
    /// All methods are internal static for direct testability.
    /// </summary>
    internal static class SegmentBoundaryLogic
    {
        /// <summary>
        /// Classifies the result of a joint break: did the vessel actually split into
        /// separate vessels, or did parts just break off while staying connected?
        /// </summary>
        /// <param name="originalVesselPid">The persistentId of the vessel being recorded before the break.</param>
        /// <param name="postBreakVesselPids">PersistentIds of all vessels present after the break event.</param>
        /// <param name="anyNewVesselHasController">Whether any newly-created vessel has a command module.</param>
        /// <returns>Classification of the joint break outcome.</returns>
        internal static JointBreakResult ClassifyJointBreakResult(
            uint originalVesselPid,
            List<uint> postBreakVesselPids,
            bool anyNewVesselHasController)
        {
            // No new vessel appeared -- parts broke but vessel stayed connected
            if (postBreakVesselPids == null || postBreakVesselPids.Count == 0)
            {
                ParsekLog.Info("Boundary",
                    $"ClassifyJointBreakResult: no new vessels detected after break " +
                    $"(originalPid={originalVesselPid}) => WithinSegment");
                return JointBreakResult.WithinSegment;
            }

            // Only the original vessel is in the list -- no split occurred
            if (postBreakVesselPids.Count == 1 && postBreakVesselPids[0] == originalVesselPid)
            {
                ParsekLog.Info("Boundary",
                    $"ClassifyJointBreakResult: only original vessel pid={originalVesselPid} " +
                    $"remains after break => WithinSegment");
                return JointBreakResult.WithinSegment;
            }

            // New vessel appeared -- classify by controller presence
            if (anyNewVesselHasController)
            {
                ParsekLog.Info("Boundary",
                    $"ClassifyJointBreakResult: new controlled vessel detected after break " +
                    $"(originalPid={originalVesselPid}, postBreakCount={postBreakVesselPids.Count}) " +
                    $"=> StructuralSplit");
                return JointBreakResult.StructuralSplit;
            }

            ParsekLog.Info("Boundary",
                $"ClassifyJointBreakResult: new debris vessel detected after break " +
                $"(originalPid={originalVesselPid}, postBreakCount={postBreakVesselPids.Count}) " +
                $"=> DebrisSplit");
            return JointBreakResult.DebrisSplit;
        }

        /// <summary>
        /// Emits PART_DESTROYED and optionally CONTROLLER_CHANGE SegmentEvents
        /// when parts break off but the vessel stays connected (within-segment breakage).
        /// </summary>
        /// <param name="segmentEvents">The list to append events to.</param>
        /// <param name="ut">Universal time of the breakage event.</param>
        /// <param name="destroyedPartName">Display name of the destroyed part.</param>
        /// <param name="destroyedPartPid">PersistentId of the destroyed part.</param>
        /// <param name="wasController">Whether the destroyed part had ModuleCommand.</param>
        /// <param name="controllerDetails">Details string for CONTROLLER_CHANGE event (e.g. lost/remaining info).</param>
        internal static void EmitBreakageSegmentEvents(
            List<SegmentEvent> segmentEvents,
            double ut,
            string destroyedPartName,
            uint destroyedPartPid,
            bool wasController,
            string controllerDetails)
        {
            if (segmentEvents == null)
            {
                ParsekLog.Warn("Boundary",
                    $"EmitBreakageSegmentEvents: segmentEvents list is null, cannot emit events " +
                    $"for part={destroyedPartName} pid={destroyedPartPid}");
                return;
            }

            segmentEvents.Add(new SegmentEvent
            {
                ut = ut,
                type = SegmentEventType.PartDestroyed,
                details = $"part={destroyedPartName} pid={destroyedPartPid}"
            });

            ParsekLog.Info("Boundary",
                $"Emitted PartDestroyed segment event: part={destroyedPartName} " +
                $"pid={destroyedPartPid} at UT={ut:F2}");

            if (wasController)
            {
                segmentEvents.Add(new SegmentEvent
                {
                    ut = ut,
                    type = SegmentEventType.ControllerChange,
                    details = controllerDetails ?? ""
                });

                ParsekLog.Info("Boundary",
                    $"Emitted ControllerChange segment event: part={destroyedPartName} " +
                    $"pid={destroyedPartPid} details={controllerDetails ?? "(none)"} at UT={ut:F2}");
            }

            ParsekLog.Info("Boundary",
                $"Within-segment breakage: part={destroyedPartName} pid={destroyedPartPid} " +
                $"wasController={wasController} at UT={ut:F2}");
        }

        /// <summary>
        /// Collects PIDs of new vessels that were captured synchronously by
        /// <c>onPartDeCoupleNewVesselComplete</c> during recording, reading them from a
        /// PID-keyed controller-status dictionary instead of from a <c>List&lt;Vessel&gt;</c>.
        ///
        /// Iterating the original <c>List&lt;Vessel&gt;</c> is unsafe at terminal crash time:
        /// KSP has already destroyed the fragment <c>GameObject</c>s, so Unity's overloaded
        /// <c>UnityEngine.Object ==</c> returns <c>true</c> when compared against <c>null</c>
        /// for every fragment, the filter drops them all, and the classifier collapses to
        /// <see cref="JointBreakResult.WithinSegment"/> with zero new vessels. See bug #362.
        ///
        /// The PID-keyed dictionary's keys are plain managed <c>uint</c>s, so it survives
        /// terminal destruction and gives us an authoritative list of the synchronously
        /// captured fragments to feed into <see cref="ClassifyJointBreakResult"/>.
        ///
        /// This helper is pure — it does not call into Unity, KSP, or <c>ParsekLog</c>.
        /// </summary>
        /// <param name="recordedPid">PersistentId of the vessel being recorded (skipped if present).</param>
        /// <param name="decoupleControllerStatus">PID-keyed controller-status dict captured synchronously during recording.</param>
        /// <param name="backgroundMap">Optional background-map of tree-tracked PIDs to skip; may be null.</param>
        /// <param name="newVesselPids">In/out list: PIDs collected by this helper are appended to it (dedup against existing entries).</param>
        /// <param name="newVesselHasController">In/out dict: controller status is written for each collected PID.</param>
        /// <param name="anyNewVesselHasController">Output flag: true if any newly collected PID has a controller.</param>
        internal static void CollectSynchronouslyCapturedNewVesselPids(
            uint recordedPid,
            IReadOnlyDictionary<uint, bool> decoupleControllerStatus,
            IReadOnlyDictionary<uint, string> backgroundMap,
            List<uint> newVesselPids,
            Dictionary<uint, bool> newVesselHasController,
            out bool anyNewVesselHasController)
        {
            anyNewVesselHasController = false;

            if (decoupleControllerStatus == null || decoupleControllerStatus.Count == 0)
                return;
            if (newVesselPids == null || newVesselHasController == null)
                return;

            foreach (var kvp in decoupleControllerStatus)
            {
                uint pid = kvp.Key;

                if (pid == recordedPid) continue;
                if (backgroundMap != null && backgroundMap.ContainsKey(pid)) continue;
                if (newVesselPids.Contains(pid)) continue;

                newVesselPids.Add(pid);

                bool hasController = kvp.Value;
                newVesselHasController[pid] = hasController;
                if (hasController)
                    anyNewVesselHasController = true;
            }
        }

        /// <summary>
        /// Checks whether a part provides command authority (has ModuleCommand).
        /// ModuleCommand is the KSP module for command pods and probe cores.
        /// </summary>
        /// <param name="partName">Name of the part (for logging).</param>
        /// <param name="moduleNames">List of module class names on the part.</param>
        /// <returns>True if the part has ModuleCommand.</returns>
        internal static bool IsControllerPart(string partName, List<string> moduleNames)
        {
            if (moduleNames == null)
            {
                ParsekLog.Verbose("Boundary",
                    $"IsControllerPart: null module list for part={partName ?? "(null)"} => false");
                return false;
            }

            bool hasCommand = moduleNames.Contains("ModuleCommand");

            ParsekLog.Verbose("Boundary",
                $"IsControllerPart: part={partName ?? "(null)"} " +
                $"moduleCount={moduleNames.Count} hasModuleCommand={hasCommand}");

            return hasCommand;
        }
    }
}
