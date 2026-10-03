using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// The recorded vessel's body-relative position at the end of one physics step,
    /// stamped with that step's game UT.
    /// </summary>
    internal struct PostPhysicsPose
    {
        public uint VesselPid;
        public string BodyName;
        public double UT;
        public double FixedTime;
        public double Latitude;
        public double Longitude;
        public double Altitude;
    }

    /// <summary>
    /// Where a recorder's physics-frame callback takes its trajectory sample from.
    /// </summary>
    internal enum PhysicsSampleSource
    {
        /// <summary>Read UT and position live from the vessel (the long-standing path).</summary>
        Live,
        /// <summary>Use the post-physics pose of the latest completed physics step.</summary>
        PostPhysicsPose,
        /// <summary>Take no trajectory sample in this callback: the live state is a mix of two steps.</summary>
        Skip,
    }

    /// <summary>
    /// Captures the active vessel's pose once per physics step, after PhysX has moved it,
    /// so the recorder can sample a position that belongs to the UT it stamps.
    ///
    /// <para>Why it exists: the recorder samples from a Harmony postfix on
    /// <c>VesselPrecalculate.CalculatePhysicsStats</c>. For an unpacked vessel KSP runs that
    /// method from <c>VesselPrecalculate.Update</c> (once per render frame, after every
    /// physics step of the frame), and from <c>FixedUpdate</c> only on the second and later
    /// physics steps of a render frame that holds several (decompiled
    /// <c>VesselPrecalculate.MainPhysics</c> / <c>Update</c>, KSP 1.12.5: the
    /// <c>physStatsNotDoneInUpdate</c> flag). The FixedUpdate call runs BEFORE that step's
    /// PhysX simulate but after <c>Planetarium.FixedUpdate</c> advanced UT and the floating
    /// origin moved the bodies, so the live read pairs one step's UT with another step's
    /// rigidbody position, and the first step of such a frame is never observed at all.
    /// Several physics steps per render frame happen whenever a render frame runs long:
    /// a warp-rate change, staging, a slow stretch. PWR-3 caught both shapes: a sample
    /// half a step behind its UT at MechJeb's 4x-to-2x rate change, and a 4x backstop
    /// sample one step late whose position belonged to the missed step.</para>
    ///
    /// <para>A <c>WaitForFixedUpdate</c> loop (ParsekFlight) calls <see cref="Capture"/>
    /// after every physics step, where UT, bodies and rigidbodies all describe the same
    /// instant. The recorder then reads <see cref="TryGetLatest"/> through
    /// <see cref="ResolveSampleSource"/>.</para>
    /// </summary>
    internal static class PostPhysicsPoseCache
    {
        // A pose older than this many physics steps behind the live UT means the capture
        // loop stopped (scene change, a destroyed host); the recorder falls back.
        internal const double MaxPoseLagSteps = 1.5;
        private const double UtEpsilonSeconds = 1e-6;

        private static PostPhysicsPose latest;
        private static bool hasLatest;

        internal static bool TryGetLatest(out PostPhysicsPose pose)
        {
            pose = latest;
            return hasLatest;
        }

        internal static void Clear()
        {
            hasLatest = false;
            latest = default(PostPhysicsPose);
        }

        internal static void SetForTesting(PostPhysicsPose pose)
        {
            latest = pose;
            hasLatest = true;
        }

        internal static void ResetForTesting()
        {
            Clear();
        }

        /// <summary>
        /// Stores the vessel's post-physics pose. Called from a WaitForFixedUpdate
        /// coroutine; clears the cache when the vessel cannot be sampled from PhysX.
        /// </summary>
        internal static void Capture(Vessel v)
        {
            if (v == null || !v.loaded || v.packed || v.mainBody == null || v.transform == null)
            {
                Clear();
                return;
            }

            Vector3d worldPos = v.transform.position;
            CelestialBody body = v.mainBody;
            latest = new PostPhysicsPose
            {
                VesselPid = v.persistentId,
                BodyName = body.name,
                UT = Planetarium.GetUniversalTime(),
                FixedTime = Time.fixedTime,
                Latitude = body.GetLatitude(worldPos),
                Longitude = body.GetLongitude(worldPos),
                Altitude = body.GetAltitude(worldPos),
            };
            hasLatest = true;
        }

        /// <summary>
        /// Reads <c>Time.inFixedTimeStep</c>; false where Unity is absent (xUnit).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool ReadInFixedTimeStep()
        {
            try { return Time.inFixedTimeStep; }
            catch { return false; }
        }

        /// <summary>
        /// False when a mod set <c>VesselPrecalculate.disableRunInUpdate</c>; true where KSP
        /// is absent.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool ReadStatsRunInUpdate()
        {
            try { return !VesselPrecalculate.disableRunInUpdate; }
            catch { return true; }
        }

        /// <summary>
        /// A background recorder's physics callback inside FixedUpdate reads a vessel
        /// whose rigidbodies have not taken this step yet against bodies that have, so it
        /// takes no trajectory sample there; the Update-step read at the end of the render
        /// frame samples instead. A packed vessel (orbit-positioned) and a game where the
        /// stats never run in Update keep sampling.
        /// </summary>
        internal static bool ShouldSkipBackgroundSampleInFixedStep(
            bool inFixedTimeStep, bool statsRunInUpdate, bool vesselPacked)
        {
            return inFixedTimeStep && statsRunInUpdate && !vesselPacked;
        }

        /// <summary>
        /// The active recorder's boundary checks (atmosphere, altitude, environment, anchor)
        /// sample the live vessel when they fire, so a FixedUpdate callback on an unpacked
        /// vessel leaves them to the Update-step callback that ends the same render frame.
        /// </summary>
        internal static bool ShouldDeferBoundaryChecksInFixedStep(
            bool inFixedTimeStep, bool statsRunInUpdate, bool vesselPacked)
        {
            return inFixedTimeStep && statsRunInUpdate && !vesselPacked;
        }

        /// <summary>
        /// Reads the duration of one physics step in game seconds (0.02 * warp rate);
        /// 0.02 where KSP is absent.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static double ReadPhysicsStepGameSeconds()
        {
            // Planetarium.FixedUpdate advances UT by 0.02 * TimeScale per step (decompiled).
            try { return 0.02 * Planetarium.TimeScale; }
            catch { return 0.02; }
        }

        /// <summary>
        /// Decides where a physics-frame callback takes its trajectory sample.
        ///
        /// <list type="bullet">
        /// <item>A packed vessel is positioned by its orbit, not PhysX: live.</item>
        /// <item>A pose of this vessel and body whose UT is not after the live UT and at most
        /// <see cref="MaxPoseLagSteps"/> physics steps behind it is the latest completed
        /// step: use it. In an Update callback its UT equals the live UT; in a FixedUpdate
        /// callback it is the step the live read straddles.</item>
        /// <item>Otherwise a FixedUpdate callback skips (its live state mixes two steps);
        /// an Update callback reads live.</item>
        /// <item>Relative mode in a FixedUpdate callback skips: the anchor offset is
        /// resolved against the live (mixed) world, which no stored pose can repair.</item>
        /// <item>When KSP is told not to run the stats in Update
        /// (<c>VesselPrecalculate.disableRunInUpdate</c>), every callback is a FixedUpdate
        /// one and there is no later read to defer to, so a rejection reads live.</item>
        /// </list>
        /// </summary>
        internal static PhysicsSampleSource ResolveSampleSource(
            bool inFixedTimeStep,
            bool statsRunInUpdate,
            bool vesselPacked,
            bool relativeMode,
            bool hasPose,
            PostPhysicsPose pose,
            uint vesselPid,
            string vesselBodyName,
            double liveUT,
            double physicsStepSeconds,
            out string reason)
        {
            if (vesselPacked)
            {
                reason = "packed";
                return PhysicsSampleSource.Live;
            }

            bool skipOnReject = inFixedTimeStep && statsRunInUpdate;
            if (relativeMode)
            {
                reason = inFixedTimeStep ? "fixed-step-relative" : "update-relative";
                return skipOnReject ? PhysicsSampleSource.Skip : PhysicsSampleSource.Live;
            }

            string poseRejection = ClassifyPoseRejection(
                hasPose, pose, vesselPid, vesselBodyName, liveUT, physicsStepSeconds);
            if (poseRejection == null)
            {
                reason = inFixedTimeStep ? "fixed-step-pose" : "update-pose";
                return PhysicsSampleSource.PostPhysicsPose;
            }

            reason = (inFixedTimeStep ? "fixed-step-" : "update-") + poseRejection;
            return skipOnReject ? PhysicsSampleSource.Skip : PhysicsSampleSource.Live;
        }

        /// <summary>
        /// Null when the pose describes this vessel at the latest completed physics step,
        /// else a short reason.
        /// </summary>
        internal static string ClassifyPoseRejection(
            bool hasPose,
            PostPhysicsPose pose,
            uint vesselPid,
            string vesselBodyName,
            double liveUT,
            double physicsStepSeconds)
        {
            if (!hasPose)
                return "no-pose";
            if (pose.VesselPid != vesselPid)
                return "pose-other-vessel";
            if (!string.Equals(pose.BodyName, vesselBodyName, StringComparison.Ordinal))
                return "pose-other-body";
            if (double.IsNaN(pose.UT) || double.IsNaN(liveUT))
                return "pose-nan";
            if (pose.UT > liveUT + UtEpsilonSeconds)
                return "pose-ahead";
            double step = physicsStepSeconds > 0 && !double.IsInfinity(physicsStepSeconds)
                ? physicsStepSeconds
                : 0.02;
            if (liveUT - pose.UT > step * MaxPoseLagSteps + UtEpsilonSeconds)
                return "pose-stale";
            return null;
        }

    }
}
