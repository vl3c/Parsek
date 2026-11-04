using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The save-time view of a recorder parked by a deferred split (audit R6).
    ///
    /// <para>An EVA, an undock or a decouple stops the live recorder synchronously, parks it in
    /// <c>ParsekFlight.pendingSplitRecorder</c> and finishes the split one frame later, when the new
    /// vessel has settled. Until then the live recorder is null and the parent recording's unflushed
    /// points sit only in the parked recorder. A save in that frame (an F5 pressed with the stage key,
    /// a scene-change save) would otherwise write the active tree without them.</para>
    ///
    /// <para>The overlay appends the parked buffers to the parent recording for the duration of the
    /// active-tree write and then puts the recording back exactly as it was, so the save holds what a
    /// save one frame earlier would have flushed and the deferred split still finds the state it
    /// expects (all three exits read the parked recorder, not the tree: the branch and the fallback
    /// append its stop capture, the false-alarm resume keeps recording into its buffers). The stop's
    /// own terminal events (engine shutdowns at the split) are left out, as the live flush never
    /// carries them: the saved recording is mid-flight, not ended. The restored recording is marked
    /// dirty so the next save rewrites its sidecar from memory.</para>
    ///
    /// <para>What the overlay cannot add is the branch itself: the new vessel exists in the saved
    /// world but its child recording is only created when the deferred split runs.</para>
    /// </summary>
    internal sealed class ParkedSplitSaveOverlay
    {
        internal const string Tag = "Scenario";

        private readonly Recording target;
        private readonly List<TrajectoryPoint> points;
        private readonly List<OrbitSegment> orbitSegments;
        private readonly List<PartEvent> partEvents;
        private readonly List<FlagEvent> flagEvents;
        private readonly List<TrackSection> trackSections;
        private readonly double explicitEndUT;
        private bool restored;

        internal int AddedPoints { get; private set; }
        internal int AddedPartEvents { get; private set; }
        internal int SkippedTerminalEvents { get; private set; }

        private ParkedSplitSaveOverlay(Recording target)
        {
            this.target = target;
            points = new List<TrajectoryPoint>(target.Points);
            orbitSegments = new List<OrbitSegment>(target.OrbitSegments);
            partEvents = new List<PartEvent>(target.PartEvents);
            flagEvents = new List<FlagEvent>(target.FlagEvents);
            trackSections = new List<TrackSection>(target.TrackSections);
            explicitEndUT = target.ExplicitEndUT;
        }

        /// <summary>
        /// Pure: why the parked recorder's buffers must NOT be laid over the active tree's current
        /// recording, or null when they should be. The overlay applies only when the live recorder
        /// is gone, a recorder is parked, the tree's active recording exists, the parked recorder
        /// belongs to this tree (a tree it was stopped for may have been replaced since) and it
        /// recorded that recording's vessel.
        /// </summary>
        internal static string ResolveSkipReason(
            bool liveRecorderPresent,
            bool parkedRecorderPresent,
            RecordingTree activeTree,
            RecordingTree parkedRecorderTree,
            uint parkedRecorderVesselPid,
            out Recording target)
        {
            target = null;
            if (liveRecorderPresent)
                return "live-recorder";
            if (!parkedRecorderPresent)
                return "no-parked-recorder";
            if (activeTree == null)
                return "no-active-tree";
            if (parkedRecorderTree != null && !ReferenceEquals(parkedRecorderTree, activeTree))
                return "parked-recorder-other-tree";
            string recId = activeTree.ActiveRecordingId;
            if (string.IsNullOrEmpty(recId))
                return "no-active-recording-id";
            if (activeTree.Recordings == null || !activeTree.Recordings.TryGetValue(recId, out Recording rec)
                || rec == null)
                return "active-recording-missing";
            if (rec.VesselPersistentId != 0 && parkedRecorderVesselPid != 0
                && rec.VesselPersistentId != parkedRecorderVesselPid)
                return "parked-recorder-other-vessel";
            target = rec;
            return null;
        }

        /// <summary>
        /// Appends the parked buffers to <paramref name="target"/> (stable-sorting the events like
        /// the live flush) and returns the overlay to <see cref="Restore"/> after the write.
        /// <paramref name="terminalEvents"/> are the stop's own terminal events, left out of the
        /// overlay by content match. The buffers themselves are never modified.
        /// </summary>
        internal static ParkedSplitSaveOverlay Apply(
            Recording target,
            IList<TrajectoryPoint> parkedPoints,
            IList<OrbitSegment> parkedOrbitSegments,
            IList<PartEvent> parkedPartEvents,
            IList<FlagEvent> parkedFlagEvents,
            IList<TrackSection> parkedTrackSections,
            List<PartEvent> terminalEvents)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            var overlay = new ParkedSplitSaveOverlay(target);
            try
            {
                overlay.ApplyCore(parkedPoints, parkedOrbitSegments, parkedPartEvents,
                    parkedFlagEvents, parkedTrackSections, terminalEvents);
            }
            catch
            {
                // A half-applied overlay would otherwise stay in memory and the deferred split
                // would append the same data a second time.
                overlay.Restore();
                throw;
            }
            return overlay;
        }

        private void ApplyCore(
            IList<TrajectoryPoint> parkedPoints,
            IList<OrbitSegment> parkedOrbitSegments,
            IList<PartEvent> parkedPartEvents,
            IList<FlagEvent> parkedFlagEvents,
            IList<TrackSection> parkedTrackSections,
            List<PartEvent> terminalEvents)
        {
            var parkedEvents = parkedPartEvents != null
                ? new List<PartEvent>(parkedPartEvents)
                : new List<PartEvent>();
            int beforeTerminalStrip = parkedEvents.Count;
            if (terminalEvents != null && terminalEvents.Count > 0)
                FlightRecorder.RemoveTerminalsFromList(parkedEvents, terminalEvents);
            SkippedTerminalEvents = beforeTerminalStrip - parkedEvents.Count;

            if (parkedPoints != null)
                target.Points.AddRange(parkedPoints);
            if (parkedOrbitSegments != null)
                target.OrbitSegments.AddRange(parkedOrbitSegments);
            if (parkedTrackSections != null)
                target.TrackSections.AddRange(parkedTrackSections);
            target.PartEvents.AddRange(parkedEvents);
            if (parkedFlagEvents != null)
                target.FlagEvents.AddRange(parkedFlagEvents);

            var sortedPartEvents = FlightRecorder.StableSortPartEventsByUT(target.PartEvents);
            target.PartEvents.Clear();
            target.PartEvents.AddRange(sortedPartEvents);
            var sortedFlagEvents = FlightRecorder.StableSortByUT(target.FlagEvents, e => e.ut);
            target.FlagEvents.Clear();
            target.FlagEvents.AddRange(sortedFlagEvents);

            AddedPoints = parkedPoints != null ? parkedPoints.Count : 0;
            AddedPartEvents = parkedEvents.Count;
            target.FilesDirty = true;
        }

        /// <summary>
        /// Puts the recording's lists back exactly as they were before <see cref="Apply"/> and marks
        /// it dirty, so the next save rewrites the sidecar from memory. Idempotent.
        /// </summary>
        internal void Restore()
        {
            if (restored)
                return;
            restored = true;
            ReplaceContents(target.Points, points);
            ReplaceContents(target.OrbitSegments, orbitSegments);
            ReplaceContents(target.PartEvents, partEvents);
            ReplaceContents(target.FlagEvents, flagEvents);
            ReplaceContents(target.TrackSections, trackSections);
            target.ExplicitEndUT = explicitEndUT;
            target.MarkFilesDirty();
        }

        private static void ReplaceContents<T>(List<T> list, List<T> saved)
        {
            list.Clear();
            list.AddRange(saved);
        }

        internal string Describe()
        {
            var ic = CultureInfo.InvariantCulture;
            return $"rec={target.RecordingId ?? "<null>"} addedPoints={AddedPoints.ToString(ic)} " +
                $"addedPartEvents={AddedPartEvents.ToString(ic)} skippedTerminalEvents={SkippedTerminalEvents.ToString(ic)}";
        }
    }
}
