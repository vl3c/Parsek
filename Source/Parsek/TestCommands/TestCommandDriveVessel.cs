using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure half of the automation-only <c>DriveVessel</c> verb (D4 <c>hysteresis</c>): drive
    /// the ACTIVE landed vessel on its own wheel motors for a span of GAME time, so a lane has
    /// a vessel that genuinely moves on the surface for longer than the 3.0 s
    /// SurfaceStationary &lt;-&gt; SurfaceMobile debounce
    /// (<see cref="EnvironmentHysteresis.SurfaceSpeedDebounceSeconds"/>) while a recording is
    /// live.
    ///
    /// <para><b>Mechanism.</b> The applier subscribes one <c>Vessel.OnFlyByWire</c> callback
    /// that writes <c>FlightCtrlState.wheelThrottle</c>. Stock's
    /// <c>FlightInputHandler.FixedUpdate</c> copies the player's input into the active
    /// vessel's <c>ctrlState</c> and then runs <c>Vessel.FeedInputFeed</c>, whose LAST
    /// delegate is <c>OnFlyByWire</c>, and <c>ModuleWheelMotor.FixedUpdate</c> reads
    /// <c>vessel.ctrlState.wheelThrottle</c> (decompiled, KSP 1.12.5). So the motors see the
    /// value exactly as if the player held the drive key, and stock's own motor, electric
    /// charge and traction rules decide whether the vessel moves. The Brakes action group is
    /// released for the drive and restored to its prior state at the end.</para>
    ///
    /// <para><b>Grammar.</b> <c>cmd=DriveVessel wheelThrottle=&lt;-1..1, not 0&gt;
    /// seconds=&lt;game seconds, (0, <see cref="MaxSeconds"/>]&gt;</c>, invariant culture.
    /// TWO-PHASE, <c>RequiresFlight</c>, default budget: the head is held until the game
    /// clock has advanced <c>seconds</c> past the start.</para>
    ///
    /// <para><b>Completion.</b> OK only when the vessel really moved: the largest surface
    /// speed sampled during the drive reached <see cref="MinMovedSrfSpeed"/>. A drive whose
    /// vessel never moved ends ERROR <c>drivevessel-did-not-move</c> (a missing charge, a
    /// disabled motor, a stuck wheel), so a lane cannot read a stationary vessel as
    /// driven.</para>
    /// </summary>
    internal static class TestCommandDriveVessel
    {
        internal const string Verb = "DriveVessel";
        internal const string ThrottleKey = "wheelThrottle";
        internal const string SecondsKey = "seconds";

        /// <summary>Upper bound on <c>seconds=</c>. The verb rides the default 60 s wall
        /// budget, and a game second can cost more than a wall second on a slow frame, so the
        /// span stays well under it.</summary>
        internal const double MaxSeconds = 30.0;

        /// <summary>The surface speed (m/s) a drive must reach to count as moved: five times
        /// <c>EnvironmentDetector.Classify</c>'s 0.1 m/s SurfaceMobile threshold, so wheel
        /// creep or suspension settle cannot pass for a drive.</summary>
        internal const double MinMovedSrfSpeed = 0.5;

        /// <summary>The REJECTED vocabulary, in gate order. hlib.DRIVEVESSEL_REASONS mirrors
        /// it (DriveVesselSourceSyncTests).</summary>
        internal static readonly string[] Reasons =
        {
            "drivevessel-throttle-arg-missing",
            "drivevessel-throttle-arg-invalid",
            "drivevessel-seconds-arg-missing",
            "drivevessel-seconds-arg-invalid",
            "drivevessel-no-active-vessel",
            "drivevessel-vessel-packed",
            "drivevessel-not-on-surface",
            "drivevessel-no-wheel-motor",
        };

        internal const string VesselLostReason = "drivevessel-vessel-lost";
        internal const string DidNotMoveReason = "drivevessel-did-not-move";
        internal const string TimeoutReason = "drivevessel-timeout";

        /// <summary>The post-start ERROR vocabulary.</summary>
        internal static readonly string[] ErrorReasons =
        {
            "drivevessel-vessel-lost",
            "drivevessel-did-not-move",
            "drivevessel-timeout",
        };

        /// <summary>Parses <c>wheelThrottle=</c> (invariant culture): a finite value in
        /// [-1, 1] other than zero (negative drives backwards). Null on success, else the
        /// refusal reason.</summary>
        internal static string ValidateThrottle(string raw, out float throttle)
        {
            throttle = 0f;
            if (string.IsNullOrEmpty(raw))
                return "drivevessel-throttle-arg-missing";
            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                || float.IsNaN(parsed) || float.IsInfinity(parsed)
                || parsed == 0f || parsed < -1f || parsed > 1f)
                return "drivevessel-throttle-arg-invalid";
            throttle = parsed;
            return null;
        }

        /// <summary>Parses <c>seconds=</c> (invariant culture): finite, in
        /// (0, <see cref="MaxSeconds"/>]. Null on success, else the refusal reason.</summary>
        internal static string ValidateSeconds(string raw, out double seconds)
        {
            seconds = 0.0;
            if (string.IsNullOrEmpty(raw))
                return "drivevessel-seconds-arg-missing";
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                || double.IsNaN(parsed) || double.IsInfinity(parsed)
                || parsed <= 0.0 || parsed > MaxSeconds)
                return "drivevessel-seconds-arg-invalid";
            seconds = parsed;
            return null;
        }

        /// <summary>The live-state gate, in order, after the args parsed: an active vessel,
        /// loaded and unpacked, LANDED or SPLASHED (a PRELAUNCH vessel's first motion belongs
        /// to the launch path, not to a drive), carrying at least one
        /// <c>ModuleWheelMotor</c>. Null to proceed.</summary>
        internal static string DecideGate(bool hasActiveVessel, bool loadedAndUnpacked,
            bool landedOrSplashed, int wheelMotorCount)
        {
            if (!hasActiveVessel) return "drivevessel-no-active-vessel";
            if (!loadedAndUnpacked) return "drivevessel-vessel-packed";
            if (!landedOrSplashed) return "drivevessel-not-on-surface";
            if (wheelMotorCount <= 0) return "drivevessel-no-wheel-motor";
            return null;
        }

        /// <summary>The completion outcome of one poll.</summary>
        internal enum DriveCompletion
        {
            StillDriving,
            CompleteOk,
            VesselLost,
            DidNotMove,
            Timeout,
        }

        /// <summary>
        /// The completion decision. A lost vessel (destroyed, DEAD, unloaded or packed) ends
        /// the drive at once. Once the game clock covered the requested span the drive ends
        /// OK when the sampled maximum surface speed reached <see cref="MinMovedSrfSpeed"/>,
        /// else ERROR did-not-move. A drive whose clock never covered the span within the wall
        /// budget (a paused game) ends ERROR timeout.
        /// </summary>
        internal static DriveCompletion DecideCompletion(bool vesselUsable,
            double gameSecondsElapsed, double requestedSeconds, double maxSrfSpeed,
            double wallSecondsElapsed, double budgetSeconds)
        {
            if (!vesselUsable) return DriveCompletion.VesselLost;
            if (gameSecondsElapsed >= requestedSeconds)
                return maxSrfSpeed >= MinMovedSrfSpeed
                    ? DriveCompletion.CompleteOk
                    : DriveCompletion.DidNotMove;
            if (wallSecondsElapsed >= budgetSeconds) return DriveCompletion.Timeout;
            return DriveCompletion.StillDriving;
        }

        /// <summary>Great-circle distance (m) between two surface points of a body of
        /// <paramref name="radius"/>, haversine form (stable at the few-metre scale a drive
        /// covers).</summary>
        internal static double ComputeSurfaceDistanceMeters(double lat1Deg, double lon1Deg,
            double lat2Deg, double lon2Deg, double radius)
        {
            const double degToRad = Math.PI / 180.0;
            double dLat = (lat2Deg - lat1Deg) * degToRad;
            double dLon = (lon2Deg - lon1Deg) * degToRad;
            double sinLat = Math.Sin(dLat * 0.5);
            double sinLon = Math.Sin(dLon * 0.5);
            double a = sinLat * sinLat
                + Math.Cos(lat1Deg * degToRad) * Math.Cos(lat2Deg * degToRad) * sinLon * sinLon;
            if (a > 1.0) a = 1.0;
            return 2.0 * radius * Math.Asin(Math.Sqrt(a));
        }

        /// <summary>The start line (after the brakes are released and the callback is
        /// subscribed).</summary>
        internal static string FormatStartLine(string vesselName, uint pid, string situation,
            float throttle, double seconds, int wheelMotors, bool brakesWereOn, double startUT,
            double srfSpeed)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "drivevessel start: vessel=" + (vesselName ?? string.Empty)
                + " pid=" + pid.ToString(ic)
                + " situation=" + (situation ?? string.Empty)
                + " wheelThrottle=" + throttle.ToString("F2", ic)
                + " seconds=" + seconds.ToString("F1", ic)
                + " wheelMotors=" + wheelMotors.ToString(ic)
                + " brakesWereOn=" + (brakesWereOn ? "true" : "false")
                + " startUT=" + startUT.ToString("F2", ic)
                + " srfSpeed=" + srfSpeed.ToString("F2", ic);
        }

        /// <summary>The grep-stable end line, written for every terminal after the start
        /// (OK and the post-start ERRORs alike), so a red drive still shows what it
        /// measured.</summary>
        internal static string FormatEndLine(string outcome, string vesselName, uint pid,
            double gameSeconds, double startSrfSpeed, double endSrfSpeed, double maxSrfSpeed,
            double pathMeters, double displacementMeters, string situation)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "drivevessel end: outcome=" + (outcome ?? string.Empty)
                + " vessel=" + (vesselName ?? string.Empty)
                + " pid=" + pid.ToString(ic)
                + " gameSeconds=" + gameSeconds.ToString("F2", ic)
                + " startSrfSpeed=" + startSrfSpeed.ToString("F2", ic)
                + " endSrfSpeed=" + endSrfSpeed.ToString("F2", ic)
                + " maxSrfSpeed=" + maxSrfSpeed.ToString("F2", ic)
                + " pathMeters=" + pathMeters.ToString("F1", ic)
                + " displacementMeters=" + displacementMeters.ToString("F1", ic)
                + " situation=" + (situation ?? string.Empty);
        }

        /// <summary>The OK payload, in wire order.</summary>
        internal static List<KeyValuePair<string, string>> BuildCompletePayload(
            double gameSeconds, double maxSrfSpeed, double pathMeters, double displacementMeters,
            string situation)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("moved", "true"),
                new KeyValuePair<string, string>("gameSeconds", gameSeconds.ToString("F2", ic)),
                new KeyValuePair<string, string>("maxSrfSpeed", maxSrfSpeed.ToString("F2", ic)),
                new KeyValuePair<string, string>("pathMeters", pathMeters.ToString("F1", ic)),
                new KeyValuePair<string, string>("displacementMeters", displacementMeters.ToString("F1", ic)),
                new KeyValuePair<string, string>("situation", situation ?? string.Empty),
            };
        }
    }
}
