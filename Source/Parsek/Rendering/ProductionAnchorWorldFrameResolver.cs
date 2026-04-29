using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.Rendering
{
    /// <summary>
    /// Production <see cref="IAnchorWorldFrameResolver"/> — backed by live
    /// KSP API (<see cref="FlightGlobals"/>, <see cref="CelestialBody.GetWorldSurfacePosition"/>,
    /// <see cref="Orbit.getPositionAtUT"/>). Each method composes existing
    /// helpers (<see cref="TrajectoryMath.ResolveRelativePlaybackPosition"/>,
    /// <see cref="TrajectoryMath.FindOrbitSegment"/>) — no new physics math
    /// here, just the dispatch glue Phase 6 needs to translate stored
    /// <see cref="AnchorCandidate"/> metadata into world-frame ε references.
    ///
    /// <para>
    /// The resolver never throws on missing data: each TryResolve* returns
    /// <c>false</c> + an empty position when its inputs are unavailable
    /// (no live vessel, no checkpoint, no absolute shadow). The propagator
    /// then logs the failure and leaves ε = 0 for that slot.
    /// </para>
    /// </summary>
    internal sealed class ProductionAnchorWorldFrameResolver : IAnchorWorldFrameResolver
    {
        public bool TryResolveRelativeBoundaryWorldPos(
            Recording rec, int sectionIndex, AnchorSide side,
            double boundaryUT, out Vector3d worldPos)
        {
            worldPos = default;
            if (rec == null || rec.TrackSections == null) return false;
            if (sectionIndex < 0 || sectionIndex >= rec.TrackSections.Count) return false;

            // The candidate's sectionIndex is the ABSOLUTE side of the
            // boundary by AnchorCandidateBuilder construction. The adjacent
            // RELATIVE section is the previous index when side==Start, and
            // the next index when side==End.
            int relIdx = (side == AnchorSide.Start) ? sectionIndex - 1 : sectionIndex + 1;
            if (relIdx < 0 || relIdx >= rec.TrackSections.Count) return false;

            TrackSection relSection = rec.TrackSections[relIdx];
            if (relSection.referenceFrame != ReferenceFrame.Relative) return false;

            // V7+ absolute shadow path: the recorder stored the focused
            // vessel's true world position alongside the anchor-local
            // offset. When present, treat the boundary sample of the
            // shadow as the high-fidelity reference — this is the exact
            // reference the Phase 6 design doc cites for §7.4.
            if (rec.RecordingFormatVersion >= RecordingStore.RelativeAbsoluteShadowFormatVersion
                && relSection.absoluteFrames != null && relSection.absoluteFrames.Count > 0)
            {
                if (TryFindBoundaryShadowSample(relSection.absoluteFrames, boundaryUT, side, out TrajectoryPoint shadow))
                {
                    CelestialBody body = ResolveBody(shadow.bodyName);
                    if (body != null)
                    {
                        worldPos = body.GetWorldSurfacePosition(shadow.latitude, shadow.longitude, shadow.altitude);
                        return IsFinite(worldPos);
                    }
                }
            }

            // V6 or v7-with-no-shadow path: anchor-local offset → world.
            // Anchor pose comes from the live vessel referenced by the
            // RELATIVE section's anchorVesselId. HR-15: this is a single
            // session-entry read.
            if (relSection.frames == null || relSection.frames.Count == 0) return false;
            if (!TryFindBoundaryFrameSample(relSection.frames, boundaryUT, side, out TrajectoryPoint pt)) return false;

            uint anchorPid = relSection.anchorVesselId;
            if (anchorPid == 0) return false;
            Vessel anchor = TryFindVesselByPid(anchorPid);
            if (anchor == null) return false;

            Vector3d anchorWorldPos;
            Quaternion anchorRot;
            try
            {
                anchorWorldPos = anchor.GetWorldPos3D();
                anchorRot = anchor.transform != null ? anchor.transform.rotation : Quaternion.identity;
            }
            catch
            {
                return false;
            }

            worldPos = TrajectoryMath.ResolveRelativePlaybackPosition(
                anchorWorldPos, anchorRot, pt.latitude, pt.longitude, pt.altitude,
                rec.RecordingFormatVersion);
            return IsFinite(worldPos);
        }

        public bool TryResolveOrbitalCheckpointWorldPos(
            Recording rec, int sectionIndex, AnchorSide side,
            double boundaryUT, out Vector3d worldPos)
        {
            return TryResolveCheckpointSideWorldPos(rec, sectionIndex, side, boundaryUT, out worldPos);
        }

        public bool TryResolveSoiBoundaryWorldPos(
            Recording rec, int sectionIndex, AnchorSide side,
            double boundaryUT, out Vector3d worldPos)
        {
            // §7.6 is a §7.5 specialization — the body-name change is only
            // a *labelling* difference at the candidate emission site (the
            // priority resolver still uses the same SoiTransition/
            // OrbitalCheckpoint rank). The world-frame resolution is the
            // same Kepler propagation against the post-SOI body's orbit.
            return TryResolveCheckpointSideWorldPos(rec, sectionIndex, side, boundaryUT, out worldPos);
        }

        public bool TryResolveLoopAnchorWorldPos(
            Recording rec, int sectionIndex, AnchorSide side,
            double sampleUT, out Vector3d worldPos)
        {
            worldPos = default;
            if (rec == null) return false;
            if (rec.LoopAnchorVesselId == 0u) return false;
            Vessel anchor = TryFindVesselByPid(rec.LoopAnchorVesselId);
            if (anchor == null) return false;
            try
            {
                worldPos = anchor.GetWorldPos3D();
            }
            catch
            {
                return false;
            }
            return IsFinite(worldPos);
        }

        public bool TryResolveBubbleEntryExitWorldPos(
            Recording rec, int sectionIndex, AnchorSide side,
            double boundaryUT, out Vector3d worldPos)
        {
            worldPos = default;
            if (rec == null || rec.TrackSections == null) return false;
            if (sectionIndex < 0 || sectionIndex >= rec.TrackSections.Count) return false;

            // The candidate's sectionIndex always points at the Checkpoint
            // segment (the propagation-only side). Side=Start (BubbleExit)
            // means the physics-active section is at sectionIndex - 1; the
            // reference position is the LAST sample of that section.
            // Side=End (BubbleEntry) means the physics-active section is at
            // sectionIndex + 1; the reference position is the FIRST sample.
            int physIdx = (side == AnchorSide.Start) ? sectionIndex - 1 : sectionIndex + 1;
            if (physIdx < 0 || physIdx >= rec.TrackSections.Count) return false;

            TrackSection phys = rec.TrackSections[physIdx];
            if (phys.source != TrackSectionSource.Active && phys.source != TrackSectionSource.Background)
                return false;

            if (phys.frames == null || phys.frames.Count == 0)
            {
                ParsekLog.Verbose("Pipeline-Anchor", string.Format(CultureInfo.InvariantCulture,
                    "bubble-entry-exit-no-sample recordingId={0} sectionIndex={1} side={2} physIdx={3} reason=frames-empty",
                    rec.RecordingId, sectionIndex, side, physIdx));
                return false;
            }

            // Pick the boundary-adjacent physics-active sample.
            TrajectoryPoint pt = (side == AnchorSide.Start)
                ? phys.frames[phys.frames.Count - 1]   // BubbleExit: LAST physics-active sample
                : phys.frames[0];                      // BubbleEntry: FIRST physics-active sample

            // Convert to world via the section's FrameTag dispatch.
            switch (phys.referenceFrame)
            {
                case ReferenceFrame.Absolute:
                {
                    CelestialBody body = ResolveBody(pt.bodyName);
                    if (body == null) return false;
                    try
                    {
                        worldPos = body.GetWorldSurfacePosition(pt.latitude, pt.longitude, pt.altitude);
                    }
                    catch
                    {
                        return false;
                    }
                    return IsFinite(worldPos);
                }
                case ReferenceFrame.Relative:
                {
                    // RELATIVE+physics-active+adjacent-to-Checkpoint is
                    // uncommon (a vessel docked to its anchor while a
                    // Checkpoint segment splices in). Defer with Verbose
                    // for v0.9.1 — documented residual gap.
                    ParsekLog.Verbose("Pipeline-Anchor", string.Format(CultureInfo.InvariantCulture,
                        "bubble-entry-exit-relative-section-deferred recordingId={0} sectionIndex={1} side={2} physIdx={3}",
                        rec.RecordingId, sectionIndex, side, physIdx));
                    return false;
                }
                case ReferenceFrame.OrbitalCheckpoint:
                    // Impossible by construction (we just verified phys is
                    // Active|Background which never carries OrbitalCheckpoint
                    // frame data). Defensive only.
                    return false;
                default:
                    return false;
            }
        }

        // --- helpers -----------------------------------------------------

        private static bool TryResolveCheckpointSideWorldPos(
            Recording rec, int sectionIndex, AnchorSide side,
            double boundaryUT, out Vector3d worldPos)
        {
            worldPos = default;
            if (rec == null || rec.TrackSections == null) return false;
            if (sectionIndex < 0 || sectionIndex >= rec.TrackSections.Count) return false;

            // sectionIndex points at the ABSOLUTE side. The adjacent
            // OrbitalCheckpoint section is the previous index (side=Start)
            // or next index (side=End).
            int ckIdx = (side == AnchorSide.Start) ? sectionIndex - 1 : sectionIndex + 1;
            if (ckIdx < 0 || ckIdx >= rec.TrackSections.Count) return false;

            TrackSection ckSection = rec.TrackSections[ckIdx];
            if (ckSection.referenceFrame != ReferenceFrame.OrbitalCheckpoint) return false;

            // Pick the OrbitSegment that brackets the boundary UT and
            // evaluate Kepler at it. The shared helper handles the
            // partial-first / partial-last checkpoint endpoint fallback
            // for both §7.5 and §7.7 — see TrajectoryMath.EvaluateOrbitSegmentAtUT.
            Vector3d? maybePos = TrajectoryMath.EvaluateOrbitSegmentAtUT(
                ckSection.checkpoints, boundaryUT, ResolveBody);
            if (!maybePos.HasValue) return false;
            worldPos = maybePos.Value;
            return IsFinite(worldPos);
        }

        private static bool TryFindBoundaryShadowSample(
            List<TrajectoryPoint> shadow, double boundaryUT, AnchorSide side, out TrajectoryPoint pt)
        {
            return TryFindBoundaryFrameSample(shadow, boundaryUT, side, out pt);
        }

        private static bool TryFindBoundaryFrameSample(
            List<TrajectoryPoint> samples, double boundaryUT, AnchorSide side, out TrajectoryPoint pt)
        {
            pt = default;
            if (samples == null || samples.Count == 0) return false;
            // Find sample closest to boundaryUT. For exact alignment (the
            // common case — the boundary is itself a recorded sample) this
            // returns the matching point directly.
            int best = 0;
            double bestDist = Math.Abs(samples[0].ut - boundaryUT);
            for (int i = 1; i < samples.Count; i++)
            {
                double d = Math.Abs(samples[i].ut - boundaryUT);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }
            pt = samples[best];
            return true;
        }

        private static CelestialBody ResolveBody(string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName)) return null;
            try
            {
                return FlightGlobals.Bodies?.Find(b => b != null && b.bodyName == bodyName);
            }
            catch
            {
                return null;
            }
        }

        private static Vessel TryFindVesselByPid(uint pid)
        {
            try
            {
                if (!FlightGlobals.ready) return null;
                var vessels = FlightGlobals.Vessels;
                if (vessels == null) return null;
                for (int i = 0; i < vessels.Count; i++)
                {
                    Vessel v = vessels[i];
                    if (v != null && v.persistentId == pid) return v;
                }
            }
            catch
            {
                // FlightGlobals.Vessels can transiently mutate; treat as miss.
            }
            return null;
        }

        private static bool IsFinite(Vector3d v)
        {
            return !(double.IsNaN(v.x) || double.IsNaN(v.y) || double.IsNaN(v.z)
                || double.IsInfinity(v.x) || double.IsInfinity(v.y) || double.IsInfinity(v.z));
        }
    }
}
