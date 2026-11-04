using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Parsek
{
    /// <summary>
    /// What the per-frame sampler does with a sample whose body differs from the open
    /// TrackSection's body (an SOI change while the vessel is in physics: a burn through
    /// the boundary, or a 1x / physics-warp coast).
    /// </summary>
    internal enum PhysicsBodyChangeDecision
    {
        /// <summary>No open section, on rails, or no body on one side: nothing to compare.</summary>
        NotApplicable = 0,
        /// <summary>The open section has no frames yet: the sample defines its body.</summary>
        EmptySection,
        /// <summary>The sample is on the section's body.</summary>
        SameBody,
        /// <summary>Close the section at the sample UT and open the next one on the new body.</summary>
        Split,
    }

    public partial class FlightRecorder
    {
        // Set by an SOI change seen while off rails: the next physics frame samples even if
        // the adaptive sampler would skip it, so the new body's first frame lands at the
        // crossing instead of up to one max sample interval later.
        private bool physicsSoiSamplePending;

        internal bool PhysicsSoiSamplePendingForTesting => physicsSoiSamplePending;

        internal void SetCurrentTrackSectionAnchorForTesting(string anchorRecordingId)
        {
            currentTrackSection.anchorRecordingId = anchorRecordingId;
            currentTrackSection.anchorVesselId = 0u;
        }

        /// <summary>
        /// Pure: whether a physics sample on <paramref name="sampleBody"/> must split the
        /// open section whose last frame is on <paramref name="sectionBody"/>. One section
        /// holds one body: the optimizer's body rule (#251) and every per-section body
        /// reader key on it, and a cross-body pair inside one section interpolates across
        /// a frame change.
        /// </summary>
        internal static PhysicsBodyChangeDecision DecidePhysicsBodyChange(
            bool onRails,
            bool sectionActive,
            ReferenceFrame sectionFrame,
            int sectionFrameCount,
            string sectionBody,
            string sampleBody)
        {
            if (onRails || !sectionActive)
                return PhysicsBodyChangeDecision.NotApplicable;
            if (sectionFrame != ReferenceFrame.Absolute && sectionFrame != ReferenceFrame.Relative)
                return PhysicsBodyChangeDecision.NotApplicable;
            if (string.IsNullOrEmpty(sampleBody))
                return PhysicsBodyChangeDecision.NotApplicable;
            if (sectionFrameCount <= 0)
                return PhysicsBodyChangeDecision.EmptySection;
            if (string.IsNullOrEmpty(sectionBody))
                return PhysicsBodyChangeDecision.NotApplicable;
            return string.Equals(sectionBody, sampleBody, StringComparison.Ordinal)
                ? PhysicsBodyChangeDecision.SameBody
                : PhysicsBodyChangeDecision.Split;
        }

        /// <summary>
        /// Off-rails half of <see cref="OnVesselSOIChanged"/>: the section split itself
        /// happens at the first sample on the new body (<see cref="TrySplitSectionAtPhysicsBodyChange"/>),
        /// because a live read inside the SOI callback may not describe one physics step.
        /// This only makes sure that sample is taken on the next frame.
        /// </summary>
        internal void ArmPhysicsSoiSample(string fromBody, string toBody, double ut)
        {
            physicsSoiSamplePending = true;
            ParsekLog.Info("Recorder",
                $"SOI changed off rails {fromBody ?? "(null)"} -> {toBody ?? "(null)"} " +
                $"at UT={ut.ToString("F2", CultureInfo.InvariantCulture)}: " +
                "section split deferred to the first sample on the new body");
        }

        internal void ClearPhysicsSoiSamplePending()
        {
            physicsSoiSamplePending = false;
        }

        private string CurrentSectionLastFrameBody()
        {
            if (!trackSectionActive || currentTrackSection.frames == null
                || currentTrackSection.frames.Count == 0)
                return null;
            return currentTrackSection.frames[currentTrackSection.frames.Count - 1].bodyName;
        }

        /// <summary>
        /// Vessel-free bookkeeping for a physics-frame body change: closes the open section
        /// at <paramref name="sample"/>'s UT and opens the next one in the same reference
        /// frame (Relative keeps its anchor). <paramref name="closingPoint"/>, when given, is
        /// the same position expressed on the OLD body at the same UT and is committed into
        /// the old section first, so no interpolation pair spans the two bodies. Returns true
        /// when it split; the caller then commits <paramref name="sample"/> as the new
        /// section's first frame.
        /// </summary>
        internal bool TrySplitSectionAtPhysicsBodyChange(
            TrajectoryPoint sample,
            TrajectoryPoint? closingPoint)
        {
            string sectionBody = CurrentSectionLastFrameBody();
            PhysicsBodyChangeDecision decision = DecidePhysicsBodyChange(
                isOnRails,
                trackSectionActive,
                trackSectionActive ? currentTrackSection.referenceFrame : ReferenceFrame.Absolute,
                trackSectionActive && currentTrackSection.frames != null ? currentTrackSection.frames.Count : 0,
                sectionBody,
                sample.bodyName);
            if (decision != PhysicsBodyChangeDecision.Split)
                return false;

            double ut = sample.ut;
            ReferenceFrame frame = currentTrackSection.referenceFrame;
            SegmentEnvironment env = environmentHysteresis != null
                ? environmentHysteresis.CurrentEnvironment
                : currentTrackSection.environment;
            TrackSectionSource source = currentTrackSection.source;
            uint anchorVesselId = currentTrackSection.anchorVesselId;
            string anchorRecordingId = currentTrackSection.anchorRecordingId;

            bool closed = false;
            if (closingPoint.HasValue
                && frame == ReferenceFrame.Absolute
                && string.Equals(closingPoint.Value.bodyName, sectionBody, StringComparison.Ordinal)
                && Math.Abs(closingPoint.Value.ut - ut) <= 1e-9)
            {
                int countBefore = Recording.Count;
                TrajectoryPoint lastBefore = countBefore > 0 ? Recording[countBefore - 1] : default(TrajectoryPoint);
                CommitRecordedPoint(closingPoint.Value, null);
                // A refused commit (Re-Fly settle suppression, a #419 reject) leaves the list as it was.
                closed = Recording.Count > countBefore
                    || (Recording.Count > 0 && countBefore > 0
                        && Recording[Recording.Count - 1].ut == closingPoint.Value.ut
                        && !lastBefore.Equals(Recording[Recording.Count - 1]));
            }

            ParsekLog.Info("Recorder",
                $"Reference frame transition: {frame} -> {frame} at SOI boundary " +
                $"UT={ut.ToString("F2", CultureInfo.InvariantCulture)} onRails=0 source={source} " +
                $"from={sectionBody} to={sample.bodyName} closingSample={(closed ? 1 : 0)}");

            CloseCurrentTrackSection(ut);
            StartNewTrackSection(env, frame, ut, source);
            if (frame == ReferenceFrame.Relative)
            {
                currentTrackSection.anchorRecordingId = anchorRecordingId;
                currentTrackSection.anchorVesselId = string.IsNullOrEmpty(anchorRecordingId)
                    ? anchorVesselId
                    : 0u;
            }

            SoiChangePending = true;
            SoiChangeFromBody = sectionBody;
            ClearPhysicsSoiSamplePending();
            return true;
        }

        /// <summary>
        /// Live wrapper run by <see cref="OnPhysicsFrame"/> on every sample before it is
        /// committed: splits the section when the sample is on a new body, then reseeds the
        /// body-keyed boundary state the on-rails SOI path reseeds.
        /// </summary>
        private void SplitSectionIfPhysicsBodyChanged(Vessel v, TrajectoryPoint sample)
        {
            string sectionBody = CurrentSectionLastFrameBody();
            if (DecidePhysicsBodyChange(
                    isOnRails,
                    trackSectionActive,
                    trackSectionActive ? currentTrackSection.referenceFrame : ReferenceFrame.Absolute,
                    trackSectionActive && currentTrackSection.frames != null ? currentTrackSection.frames.Count : 0,
                    sectionBody,
                    sample.bodyName) != PhysicsBodyChangeDecision.Split)
            {
                return;
            }

            TrajectoryPoint? closing = TryBuildSamePositionOnBody(sample, sectionBody);
            if (closing.HasValue)
            {
                TrajectoryPoint canonical = closing.Value;
                TryCanonicalizeActiveReFlyRecordingPoint(ref canonical, "physics-soi-closing");
                closing = canonical;
            }
            if (!TrySplitSectionAtPhysicsBodyChange(sample, closing))
                return;

            ReseedAtmosphereState(v);
            ReseedAltitudeState(v);
            RefreshFinalizationCache(v, "soi_change", force: true);
        }

        /// <summary>
        /// The sample's world position re-expressed as lat/lon/alt on <paramref name="bodyName"/>
        /// (the body the open section is on). Null when either body cannot be found.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static TrajectoryPoint? TryBuildSamePositionOnBody(TrajectoryPoint sample, string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName) || FlightGlobals.Bodies == null)
                return null;
            CelestialBody sampleBody = FlightGlobals.Bodies.Find(b => b != null && b.name == sample.bodyName);
            CelestialBody targetBody = FlightGlobals.Bodies.Find(b => b != null && b.name == bodyName);
            if (sampleBody == null || targetBody == null)
                return null;

            Vector3d world = sampleBody.GetWorldSurfacePosition(sample.latitude, sample.longitude, sample.altitude);
            TrajectoryPoint closing = sample;
            closing.latitude = targetBody.GetLatitude(world);
            closing.longitude = targetBody.GetLongitude(world);
            closing.altitude = targetBody.GetAltitude(world);
            closing.bodyName = targetBody.name;
            // srfRelRotation is relative to the body it was read on.
            closing.rotation = TrajectoryMath.SanitizeQuaternion(
                UnityEngine.Quaternion.Inverse(targetBody.bodyTransform.rotation)
                * (sampleBody.bodyTransform.rotation * sample.rotation));
            closing.recordedGroundClearance = double.NaN;
            if (double.IsNaN(closing.latitude) || double.IsNaN(closing.longitude) || double.IsNaN(closing.altitude))
                return null;
            return closing;
        }
    }
}
