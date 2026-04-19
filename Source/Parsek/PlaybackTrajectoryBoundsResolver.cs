using System.Collections.Generic;

namespace Parsek
{
    internal static class PlaybackTrajectoryBoundsResolver
    {
        internal static bool TryGetGhostPlayablePayloadBounds(
            IPlaybackTrajectory traj, out double startUT, out double endUT)
        {
            startUT = 0.0;
            endUT = 0.0;
            if (traj == null)
                return false;

            bool found = false;

            if (traj.Points != null && traj.Points.Count > 0)
            {
                startUT = traj.Points[0].ut;
                endUT = traj.Points[traj.Points.Count - 1].ut;
                found = true;
            }

            if (traj.OrbitSegments != null && traj.OrbitSegments.Count > 0)
            {
                double orbitStartUT = traj.OrbitSegments[0].startUT;
                double orbitEndUT = traj.OrbitSegments[traj.OrbitSegments.Count - 1].endUT;
                if (!found || orbitStartUT < startUT)
                    startUT = orbitStartUT;
                if (!found || orbitEndUT > endUT)
                    endUT = orbitEndUT;
                found = true;
            }

            if (TryGetPlayableTrackSectionPayloadBounds(
                traj.TrackSections, out double payloadStartUT, out double payloadEndUT))
            {
                if (!found || payloadStartUT < startUT)
                    startUT = payloadStartUT;
                if (!found || payloadEndUT > endUT)
                    endUT = payloadEndUT;
                found = true;
            }

            return found;
        }

        internal static double ResolveGhostActivationStartUT(IPlaybackTrajectory traj)
        {
            if (traj == null)
                return 0.0;

            if (TryGetGhostPlayablePayloadBounds(traj, out double activationStartUT, out _))
                return activationStartUT;

            return traj.StartUT;
        }

        private static bool TryGetPlayableTrackSectionPayloadBounds(
            List<TrackSection> trackSections, out double startUT, out double endUT)
        {
            startUT = 0.0;
            endUT = 0.0;
            if (trackSections == null || trackSections.Count == 0)
                return false;

            bool found = false;
            for (int i = 0; i < trackSections.Count; i++)
            {
                TrackSection section = trackSections[i];
                if (!HasPlayablePayload(section))
                    continue;

                double candidateStartUT;
                double candidateEndUT;
                if (section.referenceFrame == ReferenceFrame.OrbitalCheckpoint)
                {
                    candidateStartUT = section.checkpoints[0].startUT;
                    candidateEndUT = section.checkpoints[section.checkpoints.Count - 1].endUT;
                }
                else
                {
                    candidateStartUT = section.frames[0].ut;
                    candidateEndUT = section.frames[section.frames.Count - 1].ut;
                }

                if (!found || candidateStartUT < startUT)
                    startUT = candidateStartUT;
                if (!found || candidateEndUT > endUT)
                    endUT = candidateEndUT;
                found = true;
            }

            return found;
        }

        private static bool HasPlayablePayload(TrackSection section)
        {
            if (section.referenceFrame == ReferenceFrame.OrbitalCheckpoint)
                return section.checkpoints != null && section.checkpoints.Count > 0;

            return section.frames != null && section.frames.Count > 0;
        }
    }
}
