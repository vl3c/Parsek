using System.Collections.Generic;

namespace Parsek
{
    internal static class PlaybackTrajectoryBoundsResolver
    {
        internal static bool HasPlayablePayload(TrackSection section)
        {
            if (section.referenceFrame == ReferenceFrame.OrbitalCheckpoint)
                return section.checkpoints != null && section.checkpoints.Count > 0;

            return section.frames != null && section.frames.Count > 0;
        }

        /// <summary>
        /// Minimum <see cref="TrackSection.bodyFixedFrames"/> sample count that makes
        /// the body-fixed primary surface renderable coverage. Two, because the shadow
        /// renderer INTERPOLATES between samples: a single body-fixed point cannot cover
        /// a span and playback refuses to clamp it into a stale ghost. Same threshold
        /// as <see cref="DebrisRelativeCoveragePrimitives.BodyFixedPrimaryFramesCoverUT"/>
        /// and <c>TryGetBodyFixedPrimaryCoverageEndUT</c>, which are the recorder- and
        /// playback-side statements of the same parent-anchored contract.
        /// </summary>
        internal const int MinBodyFixedPrimarySamples = 2;

        /// <summary>
        /// BODYFIXEDFRAMES-INVISIBLE-TO-BOTH-EMPTINESS-PREDICATES: the EMPTINESS notion
        /// — "does this section carry any authored surface a consumer could render?" —
        /// as distinct from <see cref="HasPlayablePayload"/>, which answers the narrower
        /// "does this section carry the surface the BOUNDS walk knows how to read?".
        ///
        /// <para>
        /// A parent-anchored Relative section records TWO surfaces:
        /// <see cref="TrackSection.frames"/> (anchor-local offsets) and
        /// <see cref="TrackSection.bodyFixedFrames"/> (body-fixed points), and the
        /// parent-anchored contract names the LATTER the primary playback surface.
        /// <see cref="HasPlayablePayload"/> reads only the former, so a section whose
        /// only authored surface is <c>bodyFixedFrames</c> read as EMPTY to both
        /// emptiness predicates built on it — <c>SupersedeCommit.HasPlayableSupersedePayload</c>
        /// (merge) and <c>ParsekFlight.IsZeroPointLeaf</c> (prune) — while
        /// <see cref="DebrisRelativeRecorderPolicy"/> treated the same bytes as
        /// renderable coverage. The prune therefore deleted recordings the recorder had
        /// just declared covered.
        /// </para>
        ///
        /// <para>
        /// This is DELIBERATELY a separate predicate rather than a widened
        /// <see cref="HasPlayablePayload"/>. That helper gates
        /// <see cref="TryGetPlayableTrackSectionPayloadBounds"/>, whose non-checkpoint
        /// branch indexes <c>section.frames[0]</c> directly: widening it in place would
        /// admit a frames-empty section into a walk that then dereferences the empty
        /// list. Bounds resolution for the body-fixed surface is its own question with
        /// its own consumers (<see cref="DebrisRelativeCoveragePrimitives"/>,
        /// <c>DebrisRelativePlaybackPolicy</c>) and is untouched here.
        /// </para>
        /// </summary>
        internal static bool HasAuthoredRenderablePayload(TrackSection section)
        {
            if (HasPlayablePayload(section))
                return true;

            // Body-fixed primary is authored only on Relative sections (the recorder
            // allocates the list for that frame and no other), so the frame check is a
            // contract assertion rather than a filter — but keeping it explicit stops a
            // future producer from smuggling body-fixed points onto an Absolute or
            // OrbitalCheckpoint section and having them silently count as coverage.
            return section.referenceFrame == ReferenceFrame.Relative
                && section.bodyFixedFrames != null
                && section.bodyFixedFrames.Count >= MinBodyFixedPrimarySamples;
        }

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

            // Activation tracks the first playable payload sample when one exists.
            // The StartUT fallback is the trajectory's outer semantic boundary:
            // Recording widens that via ExplicitStartUT, but never shrinks inside the
            // playable payload window, so StartUT remains ordered at-or-before payload start.
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
    }
}
