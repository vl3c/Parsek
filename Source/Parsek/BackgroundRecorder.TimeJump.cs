using System.Globalization;

namespace Parsek
{
    /// <summary>What <see cref="BackgroundRecorder.RecaptureOnRailsOrbit"/> did for one vessel.</summary>
    internal enum BackgroundRecaptureOutcome
    {
        /// <summary>The open segment closed at the jump UT and a fresh one opened on the placed orbit.</summary>
        Recaptured = 0,
        /// <summary>The open segment started at this same UT: its elements were replaced in place.</summary>
        ReplacedSameUT,
        /// <summary>The vessel is not an on-rails background vessel of this tree.</summary>
        NotTracked,
        /// <summary>Tracked but no open orbit segment (landed, or no orbit).</summary>
        NoOpenSegment,
        /// <summary>The recording is sealed destroyed; it must not grow past its terminal UT.</summary>
        Destroyed,
        /// <summary>The segment closed but the vessel or its orbit could not be found.</summary>
        NoVessel,
    }

    internal partial class BackgroundRecorder
    {
        /// <summary>
        /// A Real Spawn Control time jump moved this on-rails background vessel onto a new
        /// orbit (design docs/dev/design-realspawn-time-jump-move.md, Section 6): close its
        /// open orbit segment at <paramref name="ut"/> and open a fresh one on the vessel's
        /// current (placed) orbit. The close-and-reopen of <see cref="CheckpointAllVessels"/>
        /// for one vessel; a segment opened at this same UT is replaced instead, so no
        /// zero-length segment is written.
        /// </summary>
        internal BackgroundRecaptureOutcome RecaptureOnRailsOrbit(uint vesselPid, double ut)
        {
            return RecaptureOnRailsOrbit(vesselPid, ut, vesselFinderOverride);
        }

        internal BackgroundRecaptureOutcome RecaptureOnRailsOrbit(
            uint vesselPid, double ut, System.Func<uint, Vessel> vesselFinder)
        {
            BackgroundRecaptureOutcome outcome = RecaptureOnRailsOrbitCore(
                vesselPid, ut, vesselFinder, out string recordingId);
            string utText = ut.ToString("F2", CultureInfo.InvariantCulture);
            if (outcome == BackgroundRecaptureOutcome.Recaptured
                || outcome == BackgroundRecaptureOutcome.ReplacedSameUT)
            {
                ParsekLog.Info("BgRecorder",
                    "Time jump: orbit segment re-captured on rails: pid="
                    + vesselPid.ToString(CultureInfo.InvariantCulture)
                    + " rec=" + (recordingId ?? "(null)") + " UT=" + utText
                    + " outcome=" + outcome);
            }
            else
            {
                ParsekLog.Verbose("BgRecorder",
                    "Time jump: no background re-capture: pid="
                    + vesselPid.ToString(CultureInfo.InvariantCulture)
                    + " rec=" + (recordingId ?? "(none)") + " UT=" + utText
                    + " outcome=" + outcome);
            }
            return outcome;
        }

        private BackgroundRecaptureOutcome RecaptureOnRailsOrbitCore(
            uint vesselPid, double ut, System.Func<uint, Vessel> vesselFinder, out string recordingId)
        {
            recordingId = null;
            if (tree == null || !onRailsStates.TryGetValue(vesselPid, out BackgroundOnRailsState state)
                || state == null)
                return BackgroundRecaptureOutcome.NotTracked;
            recordingId = state.recordingId;
            if (!state.hasOpenOrbitSegment)
                return BackgroundRecaptureOutcome.NoOpenSegment;
            if (IsBackgroundRecordingDestroyed(state.recordingId))
                return BackgroundRecaptureOutcome.Destroyed;

            Vessel v = vesselFinder != null
                ? vesselFinder(vesselPid)
                : FlightRecorder.FindVesselByPid(vesselPid);

            if (IsDuplicateOrbitSegmentBoundary(state.currentOrbitSegment, ut))
            {
                if (v == null || v.orbit == null)
                    return BackgroundRecaptureOutcome.NoVessel;
                state.currentOrbitSegment = CreateOrbitSegmentFromVessel(v, ut, "Unknown");
                TryCanonicalizeBackgroundReFlyRecordingOrbitSegment(
                    state.recordingId, v, ut, ref state.currentOrbitSegment, "time-jump");
                return BackgroundRecaptureOutcome.ReplacedSameUT;
            }

            CloseOrbitSegment(state, ut);
            if (v == null || v.orbit == null)
                return BackgroundRecaptureOutcome.NoVessel;

            state.currentOrbitSegment = CreateOrbitSegmentFromVessel(v, ut, "Unknown");
            TryCanonicalizeBackgroundReFlyRecordingOrbitSegment(
                state.recordingId, v, ut, ref state.currentOrbitSegment, "time-jump");
            state.hasOpenOrbitSegment = true;
            return BackgroundRecaptureOutcome.Recaptured;
        }
    }
}
