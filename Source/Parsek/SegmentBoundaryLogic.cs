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
        /// <param name="newVesselHasController">Whether any newly-created vessel has a command module.</param>
        /// <returns>Classification of the joint break outcome.</returns>
        internal static JointBreakResult ClassifyJointBreakResult(
            uint originalVesselPid,
            List<uint> postBreakVesselPids,
            bool newVesselHasController)
        {
            // No new vessel appeared -- parts broke but vessel stayed connected
            if (postBreakVesselPids == null || postBreakVesselPids.Count == 0)
            {
                ParsekLog.Info("SegmentBoundary",
                    $"ClassifyJointBreakResult: no new vessels detected after break " +
                    $"(originalPid={originalVesselPid}) => WithinSegment");
                return JointBreakResult.WithinSegment;
            }

            // Only the original vessel is in the list -- no split occurred
            if (postBreakVesselPids.Count == 1 && postBreakVesselPids[0] == originalVesselPid)
            {
                ParsekLog.Info("SegmentBoundary",
                    $"ClassifyJointBreakResult: only original vessel pid={originalVesselPid} " +
                    $"remains after break => WithinSegment");
                return JointBreakResult.WithinSegment;
            }

            // New vessel appeared -- classify by controller presence
            if (newVesselHasController)
            {
                ParsekLog.Info("SegmentBoundary",
                    $"ClassifyJointBreakResult: new controlled vessel detected after break " +
                    $"(originalPid={originalVesselPid}, postBreakCount={postBreakVesselPids.Count}) " +
                    $"=> StructuralSplit");
                return JointBreakResult.StructuralSplit;
            }

            ParsekLog.Info("SegmentBoundary",
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
                ParsekLog.Warn("SegmentBoundary",
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

            ParsekLog.Info("SegmentBoundary",
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

                ParsekLog.Info("SegmentBoundary",
                    $"Emitted ControllerChange segment event: part={destroyedPartName} " +
                    $"pid={destroyedPartPid} details={controllerDetails ?? "(none)"} at UT={ut:F2}");
            }

            ParsekLog.Info("SegmentBoundary",
                $"Within-segment breakage: part={destroyedPartName} pid={destroyedPartPid} " +
                $"wasController={wasController} at UT={ut:F2}");
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
                ParsekLog.Verbose("SegmentBoundary",
                    $"IsControllerPart: null module list for part={partName ?? "(null)"} => false");
                return false;
            }

            bool hasCommand = moduleNames.Contains("ModuleCommand");

            ParsekLog.Verbose("SegmentBoundary",
                $"IsControllerPart: part={partName ?? "(null)"} " +
                $"moduleCount={moduleNames.Count} hasModuleCommand={hasCommand}");

            return hasCommand;
        }
    }
}
