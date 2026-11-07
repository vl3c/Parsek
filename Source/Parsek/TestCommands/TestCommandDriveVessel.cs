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
    ///
    /// <para><b>Settle mode.</b> <c>cmd=DriveVessel settleBelow=&lt;m/s, (0,
    /// <see cref="MaxSettleBelow"/>]&gt; seconds=&lt;bound&gt;</c> (no
    /// <c>wheelThrottle=</c>) drives nothing: it waits, touching no control, until the
    /// active landed vessel's surface speed has stayed below <c>settleBelow</c> for
    /// <see cref="SettleHoldSeconds"/> of game time, and answers OK <c>settled=true</c>
    /// then. A vessel still moving when the game clock covers <c>seconds</c> ends ERROR
    /// <c>drivevessel-did-not-settle</c>. A lane puts it before <c>StartRecording</c> so
    /// the recorder's environment hysteresis initializes SurfaceStationary (todo
    /// RVR-21-START-SPEED-ABOVE-STATIONARY: a boot that left the rover drifting at 0.12 m/s
    /// started SurfaceMobile). No wheel motor is required.</para>
    ///
    /// <para><b>Dock mode.</b> <c>cmd=DriveVessel wheelThrottle=&lt;..&gt; seconds=&lt;bound&gt;
    /// untilDocked=true [maxSpeed=&lt;m/s, (0, <see cref="MaxDockSpeed"/>]&gt;]</c> drives the
    /// way the drive mode does, but holds the throttle only while the surface speed is below
    /// <c>maxSpeed</c> (default <see cref="DefaultDockMaxSpeed"/>, under stock
    /// <c>ModuleDockingNode.captureMaxRvel</c> = 0.3 m/s, KSP 1.12.5 decompiled) and ends at
    /// the first stock <c>GameEvents.onPartCouple</c> that names one of the driven vessel's
    /// docking-port parts: the docking port's own capture, so the dock is stock's, not the
    /// verb's. It then answers OK <c>docked=true</c> at once (the driven vessel may be the
    /// one the dock absorbed). A span that ends with no couple is ERROR
    /// <c>drivevessel-did-not-dock</c>. <c>untilDocked=</c> is closed to <c>true</c>
    /// (<c>drivevessel-dock-arg-invalid</c>) and refused with <c>settleBelow=</c>
    /// (<c>drivevessel-dock-arg-conflict</c>); <c>maxSpeed=</c> without it is
    /// <c>drivevessel-maxspeed-arg-unexpected</c>. The gate adds
    /// <c>drivevessel-no-docking-port</c>: no docking port of the vessel is in stock's
    /// <c>Ready</c> state. The did-not-move check does not apply: a dock is the outcome.</para>
    /// </summary>
    internal static class TestCommandDriveVessel
    {
        internal const string Verb = "DriveVessel";
        internal const string ThrottleKey = "wheelThrottle";
        internal const string SecondsKey = "seconds";
        internal const string SettleBelowKey = "settleBelow";
        internal const string UntilDockedKey = "untilDocked";
        internal const string MaxSpeedKey = "maxSpeed";

        /// <summary>The one value <c>untilDocked=</c> takes.</summary>
        internal const string UntilDockedTrueToken = "true";

        /// <summary>The dock-mode speed cap when <c>maxSpeed=</c> is absent: under stock
        /// <c>ModuleDockingNode.captureMaxRvel</c> (0.3 m/s), so the capture is not refused
        /// for closing too fast.</summary>
        internal const double DefaultDockMaxSpeed = 0.25;

        /// <summary>Upper bound on <c>maxSpeed=</c>.</summary>
        internal const double MaxDockSpeed = 1.0;

        /// <summary>The stock docking FSM state a port must be in for a dock drive.</summary>
        internal const string ReadyPortStateName = "Ready";

        /// <summary>Upper bound on <c>settleBelow=</c>: <c>EnvironmentDetector.Classify</c>'s
        /// 0.1 m/s SurfaceMobile threshold, so a settled vessel classifies
        /// SurfaceStationary.</summary>
        internal const double MaxSettleBelow = 0.1;

        /// <summary>How long (game seconds) the speed must stay below <c>settleBelow</c>
        /// for the vessel to count as settled, so a bounce through zero does not.</summary>
        internal const double SettleHoldSeconds = 0.5;

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
            "drivevessel-settle-arg-conflict",
            "drivevessel-settle-arg-invalid",
            "drivevessel-seconds-arg-missing",
            "drivevessel-seconds-arg-invalid",
            "drivevessel-dock-arg-invalid",
            "drivevessel-dock-arg-conflict",
            "drivevessel-maxspeed-arg-invalid",
            "drivevessel-maxspeed-arg-unexpected",
            "drivevessel-no-active-vessel",
            "drivevessel-vessel-packed",
            "drivevessel-not-on-surface",
            "drivevessel-no-wheel-motor",
            "drivevessel-no-docking-port",
        };

        internal const string VesselLostReason = "drivevessel-vessel-lost";
        internal const string DidNotMoveReason = "drivevessel-did-not-move";
        internal const string TimeoutReason = "drivevessel-timeout";
        internal const string DidNotSettleReason = "drivevessel-did-not-settle";
        internal const string DidNotDockReason = "drivevessel-did-not-dock";

        /// <summary>The post-start ERROR vocabulary.</summary>
        internal static readonly string[] ErrorReasons =
        {
            "drivevessel-vessel-lost",
            "drivevessel-did-not-move",
            "drivevessel-timeout",
            "drivevessel-did-not-settle",
            "drivevessel-did-not-dock",
        };

        /// <summary>
        /// Picks the mode from which args are present: <c>settleBelow=</c> alone is settle
        /// mode (parsed invariant culture, finite, in (0, <see cref="MaxSettleBelow"/>]);
        /// both together are a conflict; neither leaves the drive mode, whose
        /// <see cref="ValidateThrottle"/> then names the missing throttle. Null on success
        /// (with <paramref name="settle"/> saying which mode), else the refusal reason.
        /// </summary>
        internal static string ValidateSettle(string throttleRaw, string settleRaw,
            out bool settle, out double settleBelow)
        {
            settle = false;
            settleBelow = 0.0;
            if (settleRaw == null)
                return null;
            if (throttleRaw != null)
                return "drivevessel-settle-arg-conflict";
            if (!double.TryParse(settleRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                || double.IsNaN(parsed) || double.IsInfinity(parsed)
                || parsed <= 0.0 || parsed > MaxSettleBelow)
                return "drivevessel-settle-arg-invalid";
            settle = true;
            settleBelow = parsed;
            return null;
        }

        /// <summary>
        /// Parses the dock-mode args after the mode and the span parsed: <c>untilDocked=</c>
        /// absent leaves the drive or settle mode (and then <c>maxSpeed=</c> is unexpected);
        /// present it must be exactly <c>true</c> and not in settle mode; <c>maxSpeed=</c>
        /// (invariant culture, finite, in (0, <see cref="MaxDockSpeed"/>]) defaults to
        /// <see cref="DefaultDockMaxSpeed"/>. Null on success, else the refusal reason.
        /// </summary>
        internal static string ValidateDock(string untilDockedRaw, string maxSpeedRaw, bool settle,
            out bool untilDocked, out double maxSpeed)
        {
            untilDocked = false;
            maxSpeed = 0.0;
            if (untilDockedRaw == null)
                return maxSpeedRaw == null ? null : "drivevessel-maxspeed-arg-unexpected";
            if (!string.Equals(untilDockedRaw, UntilDockedTrueToken, StringComparison.Ordinal))
                return "drivevessel-dock-arg-invalid";
            if (settle)
                return "drivevessel-dock-arg-conflict";
            double parsed = DefaultDockMaxSpeed;
            if (maxSpeedRaw != null
                && (!double.TryParse(maxSpeedRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                    || double.IsNaN(parsed) || double.IsInfinity(parsed)
                    || parsed <= 0.0 || parsed > MaxDockSpeed))
                return "drivevessel-maxspeed-arg-invalid";
            untilDocked = true;
            maxSpeed = parsed;
            return null;
        }

        /// <summary>The throttle a dock drive writes this frame: none once docked, none at or
        /// above the speed cap (the vessel coasts), else the requested throttle.</summary>
        internal static float ResolveDockThrottle(float throttle, double srfSpeed, double maxSpeed,
            bool docked)
        {
            if (docked || srfSpeed >= maxSpeed) return 0f;
            return throttle;
        }

        /// <summary>
        /// The dock-mode completion decision. A couple seen during the drive ends it OK
        /// first (the driven vessel may be the one the dock absorbed, so it is checked before
        /// the vessel); a lost vessel ends it at once; once the game clock covered the span
        /// with no couple it ends ERROR did-not-dock; a clock that never covered it within
        /// the wall budget ends ERROR timeout.
        /// </summary>
        internal static DriveCompletion DecideDockCompletion(bool docked, bool vesselUsable,
            double gameSecondsElapsed, double requestedSeconds, double wallSecondsElapsed,
            double budgetSeconds)
        {
            if (docked) return DriveCompletion.Docked;
            if (!vesselUsable) return DriveCompletion.VesselLost;
            if (gameSecondsElapsed >= requestedSeconds) return DriveCompletion.DidNotDock;
            if (wallSecondsElapsed >= budgetSeconds) return DriveCompletion.Timeout;
            return DriveCompletion.StillDriving;
        }

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
            bool landedOrSplashed, int wheelMotorCount, bool settle = false,
            bool untilDocked = false, int readyDockingPorts = 0)
        {
            if (!hasActiveVessel) return "drivevessel-no-active-vessel";
            if (!loadedAndUnpacked) return "drivevessel-vessel-packed";
            if (!landedOrSplashed) return "drivevessel-not-on-surface";
            if (!settle && wheelMotorCount <= 0) return "drivevessel-no-wheel-motor";
            if (untilDocked && readyDockingPorts <= 0) return "drivevessel-no-docking-port";
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
            Settled,
            DidNotSettle,
            Docked,
            DidNotDock,
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

        /// <summary>
        /// The settle-mode completion decision. A lost vessel ends it at once; a vessel
        /// whose speed has stayed below the threshold for <see cref="SettleHoldSeconds"/>
        /// (<paramref name="belowForSeconds"/>, negative while above it) ends OK, checked
        /// before the span so a vessel that settles on the last frame still passes; once
        /// the game clock covered the span it ends ERROR did-not-settle; a clock that never
        /// covered it within the wall budget ends ERROR timeout.
        /// </summary>
        internal static DriveCompletion DecideSettleCompletion(bool vesselUsable,
            double gameSecondsElapsed, double requestedSeconds, double belowForSeconds,
            double wallSecondsElapsed, double budgetSeconds)
        {
            if (!vesselUsable) return DriveCompletion.VesselLost;
            if (belowForSeconds >= SettleHoldSeconds) return DriveCompletion.Settled;
            if (gameSecondsElapsed >= requestedSeconds) return DriveCompletion.DidNotSettle;
            if (wallSecondsElapsed >= budgetSeconds) return DriveCompletion.Timeout;
            return DriveCompletion.StillDriving;
        }

        /// <summary>The settle-mode start line.</summary>
        internal static string FormatSettleStartLine(string vesselName, uint pid,
            string situation, double settleBelow, double seconds, bool brakesOn,
            double startUT, double srfSpeed)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "drivevessel settle start: vessel=" + (vesselName ?? string.Empty)
                + " pid=" + pid.ToString(ic)
                + " situation=" + (situation ?? string.Empty)
                + " settleBelow=" + settleBelow.ToString("F3", ic)
                + " seconds=" + seconds.ToString("F1", ic)
                + " brakesOn=" + (brakesOn ? "true" : "false")
                + " startUT=" + startUT.ToString("F2", ic)
                + " srfSpeed=" + srfSpeed.ToString("F3", ic);
        }

        /// <summary>The settle-mode end line, written for every terminal after the
        /// start.</summary>
        internal static string FormatSettleEndLine(string outcome, string vesselName, uint pid,
            double gameSeconds, double startSrfSpeed, double endSrfSpeed, string situation)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "drivevessel settle end: outcome=" + (outcome ?? string.Empty)
                + " vessel=" + (vesselName ?? string.Empty)
                + " pid=" + pid.ToString(ic)
                + " gameSeconds=" + gameSeconds.ToString("F2", ic)
                + " startSrfSpeed=" + startSrfSpeed.ToString("F3", ic)
                + " endSrfSpeed=" + endSrfSpeed.ToString("F3", ic)
                + " situation=" + (situation ?? string.Empty);
        }

        /// <summary>The settle-mode OK payload, in wire order.</summary>
        internal static List<KeyValuePair<string, string>> BuildSettledPayload(
            double gameSeconds, double srfSpeed, string situation)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("settled", "true"),
                new KeyValuePair<string, string>("gameSeconds", gameSeconds.ToString("F2", ic)),
                new KeyValuePair<string, string>("srfSpeed", srfSpeed.ToString("F3", ic)),
                new KeyValuePair<string, string>("situation", situation ?? string.Empty),
            };
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

        /// <summary>The dock-mode start line (after the brakes are released and the callback
        /// and the couple hook are subscribed).</summary>
        internal static string FormatDockStartLine(string vesselName, uint pid, float throttle,
            double seconds, double maxSpeed, int readyPorts, bool brakesWereOn, double startUT,
            double srfSpeed)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "drivevessel dock start: vessel=" + (vesselName ?? string.Empty)
                + " pid=" + pid.ToString(ic)
                + " wheelThrottle=" + throttle.ToString("F2", ic)
                + " seconds=" + seconds.ToString("F1", ic)
                + " maxSpeed=" + maxSpeed.ToString("F2", ic)
                + " readyPorts=" + readyPorts.ToString(ic)
                + " brakesWereOn=" + (brakesWereOn ? "true" : "false")
                + " startUT=" + startUT.ToString("F2", ic)
                + " srfSpeed=" + srfSpeed.ToString("F2", ic);
        }

        /// <summary>The dock-mode end line, written for every terminal after the start. The
        /// partner fields name the other part of the couple and its vessel as they were when
        /// stock fired it; the active fields are read after.</summary>
        internal static string FormatDockEndLine(string outcome, string vesselName, uint pid,
            double gameSeconds, double maxSrfSpeed, double pathMeters, double dockUT,
            string portPart, string partnerPart, uint partnerPid, string partnerName,
            uint activePid, string activeName, int activeParts)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "drivevessel dock end: outcome=" + (outcome ?? string.Empty)
                + " vessel=" + (vesselName ?? string.Empty)
                + " pid=" + pid.ToString(ic)
                + " gameSeconds=" + gameSeconds.ToString("F2", ic)
                + " maxSrfSpeed=" + maxSrfSpeed.ToString("F2", ic)
                + " pathMeters=" + pathMeters.ToString("F1", ic)
                + " dockUT=" + dockUT.ToString("F2", ic)
                + " port='" + (portPart ?? string.Empty) + "'"
                + " partnerPart='" + (partnerPart ?? string.Empty) + "'"
                + " partnerPid=" + partnerPid.ToString(ic)
                + " partnerName='" + (partnerName ?? string.Empty) + "'"
                + " activePid=" + activePid.ToString(ic)
                + " activeName='" + (activeName ?? string.Empty) + "'"
                + " activeParts=" + activeParts.ToString(ic);
        }

        /// <summary>The dock-mode OK payload, in wire order.</summary>
        internal static List<KeyValuePair<string, string>> BuildDockedPayload(double gameSeconds,
            double maxSrfSpeed, double pathMeters, double dockUT, uint partnerPid,
            string partnerName, uint activePid, string activeName, int activeParts)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("docked", "true"),
                new KeyValuePair<string, string>("gameSeconds", gameSeconds.ToString("F2", ic)),
                new KeyValuePair<string, string>("maxSrfSpeed", maxSrfSpeed.ToString("F2", ic)),
                new KeyValuePair<string, string>("pathMeters", pathMeters.ToString("F1", ic)),
                new KeyValuePair<string, string>("dockUT", dockUT.ToString("F2", ic)),
                new KeyValuePair<string, string>("partnerPid", partnerPid.ToString(ic)),
                new KeyValuePair<string, string>("partnerName", partnerName ?? string.Empty),
                new KeyValuePair<string, string>("activePid", activePid.ToString(ic)),
                new KeyValuePair<string, string>("activeName", activeName ?? string.Empty),
                new KeyValuePair<string, string>("activeParts", activeParts.ToString(ic)),
            };
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
