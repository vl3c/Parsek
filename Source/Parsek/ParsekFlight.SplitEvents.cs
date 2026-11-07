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

        /// <summary>
        /// Not-recording half of <see cref="HandleKerbalLeftVehicleUndock"/>: the same routes
        /// as a hatch EVA (a tracked background parent branch, else the deferred auto-record),
        /// but only once the kerbal holds the focus. A seat dismount moves focus to the kerbal
        /// right after its undock (<c>ForceSetActiveVessel</c>); a claw release keeps it on the
        /// claw's vessel, and an auto-record armed there would wait for, and start on, whatever
        /// EVA kerbal the player next switches to.
        /// </summary>
        internal IEnumerator DeferredKerbalLeftVehicleNotRecording(
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
    }
}
