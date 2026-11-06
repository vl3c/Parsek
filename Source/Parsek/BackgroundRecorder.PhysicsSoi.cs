using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// SOI changes of LOADED (physics) background vessels: the mirror of
    /// FlightRecorder.PhysicsSoi.cs for a second vessel flying with the active one through a
    /// sphere-of-influence edge. One section holds one body, so the section is split at the
    /// vessel's first sample on the new body. On-rails background vessels keep their own
    /// orbit-segment path in <see cref="OnBackgroundVesselSOIChanged"/> and are not touched
    /// here (they never reach the per-frame sampler; see BackgroundOnRailsState).
    /// </summary>
    internal partial class BackgroundRecorder
    {
        /// <summary>
        /// Loaded half of <see cref="OnBackgroundVesselSOIChanged"/>: the split itself happens
        /// at the first sample on the new body, so this only makes sure that sample is taken
        /// on the next tick.
        /// </summary>
        private static void ArmBackgroundPhysicsSoiSample(
            BackgroundVesselState state,
            string fromBody,
            string toBody,
            double ut)
        {
            if (state == null)
                return;
            state.physicsSoiSamplePending = true;
            ParsekLog.Info("BgRecorder",
                $"SOI changed off rails for background vessel: pid={state.vesselPid} " +
                $"rec={state.recordingId ?? "(null)"} {fromBody ?? "(null)"} -> {toBody ?? "(null)"} " +
                $"at UT={ut.ToString("F2", CultureInfo.InvariantCulture)}: " +
                "section split deferred to the first sample on the new body");
        }

        /// <summary>
        /// Vessel-free bookkeeping for a loaded background vessel's physics-frame body change
        /// (the mirror of <see cref="FlightRecorder.TrySplitSectionAtPhysicsBodyChange"/>):
        /// commits <paramref name="closingPoint"/> (the same position on the OLD body at the
        /// same UT; Absolute only) into the flat list and the old section, closes the section
        /// at <paramref name="sample"/>'s UT and opens the next one in the same reference frame
        /// and environment (Relative keeps its anchor). Returns true when it split; the caller
        /// then appends <paramref name="sample"/> as the new section's first frame.
        /// </summary>
        private bool TrySplitBackgroundSectionAtPhysicsBodyChange(
            BackgroundVesselState state,
            Recording treeRec,
            TrajectoryPoint sample,
            TrajectoryPoint? closingPoint)
        {
            if (state == null)
                return false;
            // A loaded state is off rails by construction: OnBackgroundPhysicsFrame returns on
            // a packed vessel before any sample is taken.
            if (FlightRecorder.DecidePhysicsBodyChangeForSection(
                    false,
                    state.trackSectionActive,
                    state.currentTrackSection,
                    sample.bodyName,
                    out string sectionBody) != PhysicsBodyChangeDecision.Split)
            {
                return false;
            }

            double ut = sample.ut;
            ReferenceFrame frame = state.currentTrackSection.referenceFrame;
            SegmentEnvironment env = state.environmentHysteresis != null
                ? state.environmentHysteresis.CurrentEnvironment
                : state.currentTrackSection.environment;
            TrackSectionSource source = state.currentTrackSection.source;
            string anchorRecordingId = state.currentTrackSection.anchorRecordingId;

            bool closed = false;
            if (FlightRecorder.IsCommittableSoiClosingPoint(closingPoint, frame, sectionBody, ut))
            {
                // Bug #419: a refused flat append keeps the frame out of the section too, so
                // the flush cannot re-materialize it.
                closed = treeRec == null || ApplyTrajectoryPointToRecording(treeRec, closingPoint.Value);
                if (closed)
                    AddFrameToActiveTrackSection(state, closingPoint.Value);
            }

            ParsekLog.Info("BgRecorder",
                FlightRecorder.FormatPhysicsSoiSplitTransition(
                    frame, ut, source, sectionBody, sample.bodyName, closed) +
                $" pid={state.vesselPid} rec={state.recordingId ?? "(null)"}");

            CloseBackgroundTrackSection(state, ut);
            StartBackgroundTrackSection(state, env, frame, ut);
            state.currentTrackSection.source = source;
            if (frame == ReferenceFrame.Relative)
            {
                state.currentTrackSection.anchorRecordingId = anchorRecordingId;
                state.currentTrackSection.anchorVesselId = 0u;
            }

            // Mirror of the active split's reseed: the active recorder also reseeds its
            // atmosphere / approach-altitude chain-boundary state and raises SoiChangePending.
            // The background recorder has neither: its environment classifier reads the
            // vessel's current body (atmosphere depth, approach altitude) on every tick, and
            // background vessels record only inside a tree, where chain splits do not apply.
            state.physicsSoiSamplePending = false;
            return true;
        }

        /// <summary>
        /// Live wrapper run by <see cref="OnBackgroundPhysicsFrame"/> on every sample before
        /// the Relative rewrite and the append: splits the section when the sample is on a new
        /// body and refreshes the vessel's finalization cache, as the on-rails SOI path does.
        /// </summary>
        private void SplitBackgroundSectionIfPhysicsBodyChanged(
            BackgroundVesselState state,
            Recording treeRec,
            Vessel v,
            TrajectoryPoint sample)
        {
            if (state == null
                || FlightRecorder.DecidePhysicsBodyChangeForSection(
                    false,
                    state.trackSectionActive,
                    state.currentTrackSection,
                    sample.bodyName,
                    out string sectionBody) != PhysicsBodyChangeDecision.Split)
            {
                return;
            }

            TrajectoryPoint? closing = FlightRecorder.TryBuildSamePositionOnBody(sample, sectionBody);
            if (!TrySplitBackgroundSectionAtPhysicsBodyChange(state, treeRec, sample, closing))
                return;

            RefreshFinalizationCacheForVessel(v, state.recordingId, FinalizationCacheOwner.BackgroundLoaded,
                "background_soi_change", force: true);
        }
    }
}
