using System;
using System.Collections.Generic;
using System.Globalization;

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

        /// <summary>
        /// Rewind-to-Staging (Phase 4, design §5.1 + §7.19): filters
        /// <paramref name="postBreakVesselPids"/> to the subset whose live
        /// <c>Vessel</c> passes the <c>IsTrackableVessel</c> predicate
        /// (SpaceObject OR has <c>ModuleCommand</c>). The result is the list of
        /// controllable children of a split event; a list of length >= 2 means
        /// the split is multi-controllable (see <see cref="IsMultiControllableSplit"/>)
        /// and requires a <see cref="RewindPoint"/>.
        ///
        /// <para>
        /// The <paramref name="isTrackable"/> delegate defaults to
        /// <see cref="ParsekFlight.IsTrackableVessel"/> but is injected so
        /// unit tests can exercise the classifier without a live
        /// <c>FlightGlobals</c>. Vessels that cannot be resolved from
        /// <c>FlightGlobals.Vessels</c> are logged and skipped (they are treated
        /// as non-controllable).
        /// </para>
        /// </summary>
        /// <param name="originalVesselPid">The parent vessel's PID (for logging).</param>
        /// <param name="postBreakVesselPids">PIDs of vessels produced by the split (children only; do NOT include the parent).</param>
        /// <param name="isControllable">Predicate mapping vessel PID -> controllable. Null uses the default that reads <c>FlightGlobals.Vessels</c> + <see cref="ParsekFlight.IsTrackableVessel"/>.</param>
        /// <returns>List of controllable child PIDs (subset of <paramref name="postBreakVesselPids"/>).</returns>
        internal static List<uint> IdentifyControllableChildren(
            uint originalVesselPid,
            List<uint> postBreakVesselPids,
            Func<uint, bool?> isControllable = null)
        {
            var result = new List<uint>();
            if (postBreakVesselPids == null || postBreakVesselPids.Count == 0)
            {
                ParsekLog.Info("Rewind",
                    $"Controllable split children: (none) (orig={originalVesselPid})");
                return result;
            }

            var predicate = isControllable ?? DefaultIsControllable;

            int unresolved = 0;
            for (int i = 0; i < postBreakVesselPids.Count; i++)
            {
                uint pid = postBreakVesselPids[i];
                bool? controllable = predicate(pid);
                if (controllable == null)
                {
                    unresolved++;
                    continue;
                }
                if (controllable.Value)
                    result.Add(pid);
            }

            string joined = string.Join(",", ListToStrings(result));
            ParsekLog.Info("Rewind",
                $"Controllable split children: [{joined}] (orig={originalVesselPid}, " +
                $"checked={postBreakVesselPids.Count}, unresolved={unresolved})");

            return result;
        }

        private static readonly Func<uint, bool?> DefaultIsControllable = pid =>
        {
            Vessel v = FlightRecorder.FindVesselByPid(pid);
            if (v == null) return null;
            return ParsekFlight.IsTrackableVessel(v);
        };

        private static string[] ListToStrings(List<uint> pids)
        {
            if (pids == null) return Array.Empty<string>();
            var arr = new string[pids.Count];
            for (int i = 0; i < pids.Count; i++)
                arr[i] = pids[i].ToString(CultureInfo.InvariantCulture);
            return arr;
        }

        /// <summary>
        /// Rewind-to-Staging (Phase 4, design §5.1 / §7.2 / §7.19): true iff the
        /// split has at least 2 controllable outputs. A multi-controllable split
        /// is the trigger for writing a <see cref="RewindPoint"/>.
        /// </summary>
        internal static bool IsMultiControllableSplit(int controllableCount) => controllableCount >= 2;
    }

    /// <summary>
    /// Capture-time snapshot of a live vessel's identity fields. Test seam for
    /// <see cref="RewindPointAuthor"/>: the deferred body only needs the vessel's
    /// <c>persistentId</c> and its root part's <c>persistentId</c> to build
    /// <see cref="RewindPoint.PidSlotMap"/> / <see cref="RewindPoint.RootPartPidMap"/>,
    /// so the provider returns this struct instead of a live <c>Vessel</c>.
    /// Production reads both fields from <c>FlightGlobals.Vessels</c>.
    /// </summary>
    internal readonly struct VesselSnapshot
    {
        public readonly uint VesselPersistentId;
        public readonly uint RootPartPersistentId;
        public readonly bool HasRootPart;

        public VesselSnapshot(uint vesselPid, uint rootPartPid, bool hasRootPart)
        {
            VesselPersistentId = vesselPid;
            RootPartPersistentId = rootPartPid;
            HasRootPart = hasRootPart;
        }
    }

    /// <summary>
    /// Test seam for <see cref="SegmentBoundaryLogic.IdentifyControllableChildren"/>
    /// and <see cref="RewindPointAuthor"/>. The default implementation reads
    /// <c>FlightGlobals.Vessels</c>; unit tests install a mock so they can exercise
    /// live-vessel lookup paths without KSP's scene state.
    /// </summary>
    internal interface IFlightGlobalsProvider
    {
        /// <summary>Returns the vessel whose <c>persistentId</c> equals <paramref name="pid"/>, or null.</summary>
        Vessel FindVesselByPid(uint pid);

        /// <summary>
        /// Returns an identity <see cref="VesselSnapshot"/> for the vessel whose
        /// <c>persistentId</c> equals <paramref name="pid"/>. False when no such
        /// vessel is known. Prefer this over <see cref="FindVesselByPid"/> for the
        /// RewindPointAuthor capture path: it lets tests drive the body without
        /// constructing a live <c>Vessel</c>.
        /// </summary>
        bool TryGetVesselSnapshot(uint pid, out VesselSnapshot snapshot);

        /// <summary>
        /// Returns the currently focused vessel's persistentId, or null when
        /// there is no active vessel/focus signal.
        /// </summary>
        uint? GetActiveVesselPid();
    }

    /// <summary>
    /// Default <see cref="IFlightGlobalsProvider"/>. Reads <c>FlightGlobals.Vessels</c>
    /// via <see cref="FlightRecorder.FindVesselByPid"/>, which is the shared vessel
    /// lookup used across the rest of the mod.
    /// </summary>
    internal sealed class FlightGlobalsProvider : IFlightGlobalsProvider
    {
        public static readonly IFlightGlobalsProvider Default = new FlightGlobalsProvider();

        private FlightGlobalsProvider() { }

        public Vessel FindVesselByPid(uint pid)
            => FlightRecorder.FindVesselByPid(pid);

        public bool TryGetVesselSnapshot(uint pid, out VesselSnapshot snapshot)
        {
            Vessel v = FlightRecorder.FindVesselByPid(pid);
            if (v == null)
            {
                snapshot = default;
                return false;
            }
            Part root = v.rootPart;
            snapshot = new VesselSnapshot(
                vesselPid: v.persistentId,
                rootPartPid: root != null ? root.persistentId : 0u,
                hasRootPart: root != null);
            return true;
        }

        public uint? GetActiveVesselPid()
        {
            Vessel active = null;
            try { active = FlightGlobals.ActiveVessel; }
            catch { active = null; }
            if (active == null) return null;
            return active.persistentId;
        }
    }
}
