using System;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace Parsek.Patches
{
    /// <summary>
    /// Prevents KSP from destroying ghost ProtoVessels due to on-rails atmospheric
    /// pressure. Ghost ProtoVessels orbit through the atmosphere (e.g., deorbit orbit
    /// with sub-surface periapsis). KSP's Vessel.CheckKill() destroys on-rails vessels
    /// at > 1 kPa pressure. If the PlanetariumCamera is focused on the ghost, Die()
    /// nulls the camera target → cascade of NullRefs → planet disappears.
    /// </summary>
    [HarmonyPatch(typeof(Vessel), "CheckKill")]
    internal static class GhostCheckKillPatch
    {
        static bool Prefix(Vessel __instance)
        {
            if (GhostMapPresence.IsGhostMapVessel(__instance.persistentId))
                return false; // skip CheckKill entirely for ghost vessels
            return true;
        }
    }

    /// <summary>
    /// Drives a ghost vessel's OrbitDriver to the loop-mapped recorded sample clock
    /// (<c>effUT = liveUT - shift</c>) every physics frame, instead of letting stock propagate
    /// it at the LIVE Planetarium clock. This fixes the frozen-icon-on-short-arc bug: under time
    /// warp (or any loop where the recorded sample clock runs faster than live), stock's live-UT
    /// propagation only lands the right phase at each rate-limited reseed and stalls the icon on a
    /// near-fixed anomaly in between, so the marker freezes on short orbital arcs and teleports at
    /// segment seams while the drawn orbit line stays correct. Driving at <c>effUT</c> makes the
    /// icon a continuous function of (orbit elements, effUT), so it glides in lockstep with the
    /// arc line (which GhostOrbitArcPatch evaluates at the same <c>effUT</c>), with no freeze and
    /// no seam teleport, in both the flight map and the Tracking Station (the patch is keyed only
    /// on IsGhostMapVessel, no scene check).
    ///
    /// It patches the internal <c>updateFromParameters(bool)</c> overload (the one that actually
    /// sets the position) and returns <c>false</c> so stock does NOT re-propagate at live UT and
    /// overwrite us. The parameterless overload that stock's per-FixedUpdate UpdateOrbit() calls
    /// forwards to this one with <c>setPosition: true</c>, so we own every per-frame placement for
    /// ghost map vessels. The body replicates stock OrbitDriver.updateFromParameters(bool)
    /// verbatim (UpdateFromUT → pos/vel → Swizzle → NaN guard → SetPosition with the
    /// driverTransform.rotation * localCoM offset); any null / NaN / hyperbolic / degenerate case
    /// bails (returns true) so stock keeps its existing behavior.
    ///
    /// #212b underground suppression is preserved in effUT space: when <c>effUT</c> is genuinely
    /// past / before the recorded window or off the visible (above-ground) arc, the driver is
    /// clamped to the nearest recorded endpoint and the pid is added to ghostsWithSuppressedIcon
    /// so the below-atmosphere custom-icon handoff still works and the icon never goes underground.
    /// </summary>
    [HarmonyPatch(typeof(OrbitDriver), "updateFromParameters", new[] { typeof(bool) })]
    internal static class GhostOrbitIconDrivePatch
    {
        /// <summary>
        /// Pure: which recorded-clock UT should the OrbitDriver be propagated at this frame, and
        /// is the icon suppressed? The stored arc bounds are in the LIVE frame
        /// (<paramref name="startUTShifted"/> / <paramref name="endUTShifted"/>), so the live-clock
        /// past/before-window checks use them directly; the returned <c>DriveUT</c> is in the
        /// RECORDED frame (effUT space) because the OrbitDriver is now seeded with the raw recorded
        /// epoch. <paramref name="onArc"/> is the orbit-dependent visible-arc result computed by the
        /// caller (true when the orbit is degenerate / full-period, matching IsOnOrbitalArc).
        /// </summary>
        internal static IconDriveDecision ResolveIconDriveDecision(
            double liveUT,
            double startUTShifted,
            double endUTShifted,
            double shift,
            bool onArc)
        {
            // Past the recorded window → clamp to the last recorded position (raw end UT).
            if (liveUT > endUTShifted)
                return new IconDriveDecision(
                    GhostMapPresence.MapLiveUTToEffUT(endUTShifted, shift), suppressed: true,
                    reason: "past-window");
            // Before the recorded window → clamp to the first recorded position (raw start UT).
            if (liveUT < startUTShifted)
                return new IconDriveDecision(
                    GhostMapPresence.MapLiveUTToEffUT(startUTShifted, shift), suppressed: true,
                    reason: "before-window");
            // Within the window but off the visible (above-ground) arc → caller clamps to the
            // nearest endpoint in orbital-time space (needs the Orbit), so signal off-arc here.
            if (!onArc)
                return new IconDriveDecision(
                    driveUT: double.NaN, suppressed: true, reason: "off-arc");
            // On the visible arc → drive at the loop-mapped effUT so the icon glides.
            return new IconDriveDecision(
                GhostMapPresence.MapLiveUTToEffUT(liveUT, shift), suppressed: false,
                reason: "on-arc-drive");
        }

        /// <summary>Result of <see cref="ResolveIconDriveDecision"/>.</summary>
        internal readonly struct IconDriveDecision
        {
            internal readonly double DriveUT;
            internal readonly bool Suppressed;
            internal readonly string Reason;
            internal IconDriveDecision(double driveUT, bool suppressed, string reason)
            {
                DriveUT = driveUT;
                Suppressed = suppressed;
                Reason = reason;
            }
        }

        static bool Prefix(OrbitDriver __instance, bool setPosition, ref double ___updateUT)
        {
            if (__instance == null || __instance.vessel == null) return true;

            uint pid = __instance.vessel.persistentId;
            if (!GhostMapPresence.IsGhostMapVessel(pid)) return true;

            // Ghost map drivers are forward vessel drivers (reverse == false, vessel != null checked
            // above). This patch replicates only stock's "!reverse && vessel != null" SetPosition branch;
            // defer the reverse == true case to stock unchanged (the vessel == null / celestial-body-only
            // branch is already deferred by the null-vessel guard above).
            if (__instance.reverse) return true;

            Orbit orbit = __instance.orbit;
            // Missing / degenerate orbit → let stock handle it unchanged. Hyperbolic is NOT deferred:
            // the ghost orbit is seeded at the RAW recorded epoch (no shift baked in), so deferring a
            // hyperbolic escape to stock would propagate the OPEN trajectory at the LIVE clock (far
            // past the recorded escape, since liveUT = effUT + shift with a huge shift), flinging the
            // icon billions of metres out into deep space - the "icon not rendered on the hyperbolic
            // escape" regression. We drive it at effUT like the elliptical case so it tracks the
            // recorded escape arc within its segment window. (A hyperbolic period is +Infinity, which
            // passes the period<=0 degeneracy guard; NaN/zero periods still defer.)
            if (orbit == null || double.IsNaN(orbit.period) || orbit.period <= 0.0)
                return true;
            bool hyperbolic = orbit.eccentricity >= 1.0;

            double currentUT = Planetarium.GetUniversalTime();
            // No recorded arc bounds (terminal-orbit ghost): let stock propagate at live UT. These
            // ghosts have shift 0 and a full ellipse, so live == effUT and the icon already glides.
            if (!GhostMapPresence.TryGetVisibleOrbitBoundsForGhostVessel(
                    pid, currentUT, out double startUT, out double endUT))
                return true;

            double shift = GhostMapPresence.GetGhostOrbitEpochShift(pid);
            // A hyperbolic escape segment covers a single OUTWARD pass with no below-ground arc, so the
            // icon is on the visible arc whenever the loop clock is within the segment window; the
            // period-based above-ground arc test is meaningless for an open orbit (period is infinite),
            // so treat hyperbolic as always-on-arc and let the window past/before clamp + suppress.
            bool onArc = hyperbolic
                || GhostOrbitArcCheck.IsOnOrbitalArc(orbit, startUT, endUT, currentUT);
            var decision = ResolveIconDriveDecision(currentUT, startUT, endUT, shift, onArc);

            double driveUT = decision.DriveUT;
            if (decision.Reason == "off-arc")
            {
                // Off the visible (above-ground) arc — clamp to the nearest arc endpoint in
                // orbital-time space, then map that live-frame endpoint back to the recorded clock.
                double period = orbit.period;
                double obtNow = ((orbit.getObtAtUT(currentUT) % period) + period) % period;
                double obtStart = ((orbit.getObtAtUT(startUT) % period) + period) % period;
                double obtEnd = ((orbit.getObtAtUT(endUT) % period) + period) % period;
                double distToStart, distToEnd;
                if (obtStart <= obtEnd)
                {
                    distToStart = (obtStart - obtNow + period) % period;
                    distToEnd = (obtNow - obtEnd + period) % period;
                }
                else
                {
                    distToStart = (obtNow <= obtStart) ? (obtStart - obtNow) : (obtStart + period - obtNow);
                    distToEnd = (obtNow >= obtEnd) ? (obtNow - obtEnd) : (obtNow + period - obtEnd);
                }
                double clampUTShifted = (distToEnd <= distToStart) ? endUT : startUT;
                driveUT = GhostMapPresence.MapLiveUTToEffUT(clampUTShifted, shift);
            }

            if (decision.Suppressed)
                GhostMapPresence.ghostsWithSuppressedIcon.Add(pid);
            else
                GhostMapPresence.ghostsWithSuppressedIcon.Remove(pid);

            ParsekLog.VerboseRateLimited("GhostOrbitIcon", "drive-" + pid,
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Icon drive pid={0} reason={1} liveUT={2:F1} effUT/driveUT={3:F1} shift={4:F1} " +
                    "bounds=[{5:F1},{6:F1}] suppressed={7} scene={8}",
                    pid, decision.Reason, currentUT, driveUT, shift,
                    startUT, endUT, decision.Suppressed, HighLogic.LoadedScene),
                1.0);

            // Phase 8a (EXPERIMENTAL, gated by mapRenderDirectorDrive, default off): re-assert the new
            // pipeline's Director conic for this ghost so the icon rides the SAME inertial elements the
            // orbit line is drawn from - one source - which eliminates the looped-re-aim
            // icon-rotated-off-its-line desync (the old path lets a body-fixed reseed move the icon off
            // the inertial line). SeedAndDrive re-seeds the segment elements + propagates at driveUT;
            // the propagate + SetPosition below then place the icon on that conic. No fresh seed (gate
            // off, tracing+shadow not producing one this frame, or a non-StockConic segment) -> the
            // legacy drive runs unchanged. Bodies match for the v1 same-body case; an SOI-mismatched
            // seed body falls back to the driver's reference body.
            if (ParsekSettings.Current != null && ParsekSettings.Current.mapRenderDirectorDrive
                && Parsek.MapRender.ShadowRenderDriver.TryGetFreshStockConicSeed(
                    pid, Time.frameCount, out OrbitSegment dirSeg, out string dirBody))
            {
                CelestialBody seedBody = FlightGlobals.GetBodyByName(dirBody) ?? __instance.referenceBody;
                Parsek.MapRender.StockConicTreatment.SeedAndDrive(orbit, dirSeg, seedBody, driveUT);
            }

            // Replicate stock OrbitDriver.updateFromParameters(bool) verbatim, but propagate at the
            // recorded-clock driveUT instead of the live clock. Stock does updateUT = now then
            // UpdateFromUT(updateUT); we mirror that below by recording driveUT (the UT we actually
            // propagate at) into the private updateUT field. (orbitdriver_decomp.cs:607-726.)
            orbit.UpdateFromUT(driveUT);
            Vector3d pos = orbit.pos;
            Vector3d vel = orbit.vel;
            pos.Swizzle();
            vel.Swizzle();
            // Degenerate propagation: bail to stock's NaN handling (unload + destroy) WITHOUT
            // writing NaN into __instance.pos/vel first. The window clamp keeps driveUT inside the
            // recorded segment for both elliptical and hyperbolic orbits, so the propagation is
            // finite in the normal case; this guard catches any residual NaN (e.g. a degenerate
            // reseed) and bailing before the write keeps the driver state clean, matching stock's
            // contract that the destroy decision runs on a fresh re-propagation rather than on a
            // half-written effUT NaN.
            if (double.IsNaN(pos.x))
                return true;
            // Keep the driver's recorded propagation time faithful to the position we set: stock sets
            // updateUT = now, we propagated at driveUT. updateUT is private, injected here via ___updateUT.
            ___updateUT = driveUT;
            __instance.pos = pos;
            __instance.vel = vel;

            if (!setPosition)
                return false;

            Vessel v = __instance.vessel;
            CelestialBody refBody = __instance.referenceBody;
            if (v != null && refBody != null && __instance.driverTransform != null)
            {
                Vector3d off = (QuaternionD)__instance.driverTransform.rotation * (Vector3d)v.localCoM;
                v.SetPosition(refBody.position + pos - off);
            }

            // The per-frame icon-truth / icon-vs-line divergence / icon-jump diagnostics that used
            // to live here (PR #1003 follow-ups) were always-on, per-ghost, per-frame reads. They
            // are now subsumed by the gated MapRenderProbe (Tier-B body-orbit / line truth +
            // Tier-C icon-jump anomaly), behind the mapRenderTracing setting, so the steady-state
            // per-frame cost is gone from normal play. See docs/dev/design-map-ts-render-tracer.md.
            return false;
        }
    }

    /// <summary>
    /// Suppresses the orbit line for ghost map ProtoVessels below atmosphere.
    /// The orbit line is meaningless during atmospheric flight (drag makes Keplerian
    /// propagation diverge from recorded trajectory).
    ///
    /// Architecture: OrbitRendererBase.LateUpdate calls DrawOrbit() then DrawNodes().
    /// This postfix runs after both, suppressing the Vectrosity line for ghost vessels.
    /// Icon positioning is handled separately by GhostOrbitIconDrivePatch on OrbitDriver.
    /// </summary>
    [HarmonyPatch(typeof(OrbitRendererBase), "LateUpdate")]
    internal static class GhostOrbitLinePatch
    {
        private const string Tag = "GhostOrbitLine";

        /// <summary>
        /// Length of the orbit-line grace window, in RENDER FRAMES. When the line
        /// is genuinely shown (`visible-body-frame`) a grace deadline of
        /// Time.frameCount + this is stamped; a subsequent TRANSIENT off-dip within
        /// the deadline frame is deferred so the line does not blink at a short
        /// phase-boundary segment while the per-frame reseed catches up.
        ///
        /// This is a FRAME count, not a UT window. The blink is per-render-frame
        /// chatter. The original UT window (1.5 s) was defeated by time warp: at
        /// transfer-watching warp one render frame advances UT by far more than
        /// 1.5 s (even a modest ~75x steps ~2.5 UT/frame), so a transient dip's UT
        /// is already past `lastShownUT + 1.5` on the very next frame and the grace
        /// deferred ZERO frames -> the heliocentric orbit line blinked. A frame
        /// count is warp-independent: it defers a few-frame transient dip at any
        /// warp, while a SUSTAINED off (more consecutive off-frames than this, e.g.
        /// the polyline owning a whole below-surface descent) still expires and
        /// hides (the grace deadline was last stamped while the line was visible),
        /// preserving the FIX-2 sustained-descent handoff. Tunable; the observed
        /// transfer-phase chatter dips are ~1-12 render frames.
        /// </summary>
        internal const int OrbitLineGraceFrames = 20;

        /// <summary>
        /// Real-time grace window (seconds) during which the stock orbit icon is held
        /// suppressed after the trajectory polyline releases ownership of a non-orbital phase.
        /// Covers the worst-case lag between polyline release and the next seg-drive dispatcher
        /// tick (MapOrbitUpdateIntervalSec = 0.5s under load, plus the on-vessel apply jitter);
        /// 1.5s is comfortably above that and matches OrbitLineGraceSeconds for consistency.
        /// Once seg-drive applies, the visible-body-frame / out-of-body-frame branches engage
        /// with fresh bounds and the icon shows on the correct mesh position naturally.
        /// </summary>
        internal const float PolylineReleaseGraceSeconds = 1.5f;

        /// <summary>
        /// The two TRANSIENT off reasons that the grace window may defer for one
        /// frame at a short phase-boundary segment.
        /// </summary>
        internal const string OffReasonStaleSegment = "stale-segment-awaiting-reseed";
        internal const string OffReasonPolylineOwns = "polyline-owns-phase";

        /// <summary>
        /// Pure grace decision: should a transient orbit-line hide be DEFERRED
        /// (kept visible) for this frame?
        ///
        /// Returns true only when ALL of:
        /// - the off reason is one of the two TRANSIENT reasons
        ///   (<see cref="OffReasonStaleSegment"/> / <see cref="OffReasonPolylineOwns"/>);
        ///   the durable reasons (below-atmosphere, out-of-body-frame) are never
        ///   graced and hide instantly;
        /// - the grace window is still open (<paramref name="currentFrame"/> &lt;=
        ///   <paramref name="graceUntilFrame"/>); and
        /// - the orbit elements are still finite and elliptical
        ///   (<paramref name="orbitFiniteElliptical"/>), so a real arc exists to
        ///   keep showing (a hyperbolic / degenerate orbit has no meaningful
        ///   ellipse to bridge with).
        ///
        /// A SUSTAINED transient phase (e.g. the polyline owning a whole
        /// below-atmosphere descent) keeps hiding because the grace deadline was
        /// last stamped while the line was visible, so it expires within
        /// <see cref="OrbitLineGraceFrames"/> render frames of the last genuine
        /// show and the off then takes effect normally (the coupling with FIX 2's
        /// sustained descent ownership).
        /// </summary>
        internal static bool ShouldDeferOrbitLineHide(
            string offReason,
            int currentFrame,
            int graceUntilFrame,
            bool orbitFiniteElliptical)
        {
            if (!orbitFiniteElliptical) return false;
            bool transient =
                offReason == OffReasonStaleSegment || offReason == OffReasonPolylineOwns;
            if (!transient) return false;
            return currentFrame <= graceUntilFrame;
        }

        /// <summary>
        /// Pure: are the orbit elements finite and elliptical (eccentricity &lt; 1,
        /// period &gt; 0, both finite)? Only an elliptical arc has a meaningful
        /// shape to keep showing across transient boundary chatter.
        /// </summary>
        internal static bool IsOrbitFiniteElliptical(Orbit orbit)
        {
            return orbit != null
                && !double.IsNaN(orbit.eccentricity)
                && !double.IsInfinity(orbit.eccentricity)
                && orbit.eccentricity < 1.0
                && !double.IsNaN(orbit.period)
                && !double.IsInfinity(orbit.period)
                && orbit.period > 0.0;
        }

        internal static string BuildGhostOrbitLineDecisionStateKey(
            bool lineActive,
            OrbitRendererBase.DrawIcons drawIcons,
            bool iconSuppressed,
            string reason,
            bool hasBounds,
            double startUT,
            double endUT)
        {
            string boundsKey = hasBounds
                ? string.Format(CultureInfo.InvariantCulture, "{0:F1}-{1:F1}", startUT, endUT)
                : "none";
            return string.Format(CultureInfo.InvariantCulture,
                "active={0}|icons={1}|suppressed={2}|reason={3}|bounds={4}",
                lineActive ? 1 : 0,
                drawIcons,
                iconSuppressed ? 1 : 0,
                string.IsNullOrEmpty(reason) ? "unspecified" : reason,
                boundsKey);
        }

        internal static string FormatGhostOrbitLineDecision(
            uint vesselPid,
            string reason,
            bool lineActive,
            OrbitRendererBase.DrawIcons drawIcons,
            bool iconSuppressed,
            bool belowAtmosphere,
            bool hasBounds,
            double currentUT,
            double startUT,
            double endUT)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Orbit line decision: pid={0} reason={1} lineActive={2} drawIcons={3} iconSuppressed={4} belowAtmosphere={5} hasBounds={6} currentUT={7:F1} bounds=[{8:F1},{9:F1}] scene={10}",
                vesselPid,
                string.IsNullOrEmpty(reason) ? "unspecified" : reason,
                lineActive,
                drawIcons,
                iconSuppressed,
                belowAtmosphere,
                hasBounds,
                currentUT,
                hasBounds ? startUT : double.NaN,
                hasBounds ? endUT : double.NaN,
                HighLogic.LoadedScene);
        }

        private static void LogOrbitLineDecision(
            uint vesselPid,
            string reason,
            bool lineActive,
            OrbitRendererBase.DrawIcons drawIcons,
            bool iconSuppressed,
            bool belowAtmosphere,
            bool hasBounds,
            double currentUT,
            double startUT,
            double endUT)
        {
            // Record the authoritative line/icon decision so the end-of-frame MapRenderProbe can
            // reconcile it against the actually-rendered state on this same frame (decision-vs-truth,
            // second cut). Guarded by IsEnabled so disabled play never pays the drawIcons.ToString().
            if (MapRenderTrace.IsEnabled)
                MapRenderTrace.RecordLineIntent(vesselPid, lineActive, drawIcons.ToString(), reason);

            ParsekLog.VerboseOnChange(Tag,
                "pid-" + vesselPid.ToString(CultureInfo.InvariantCulture),
                BuildGhostOrbitLineDecisionStateKey(
                    lineActive,
                    drawIcons,
                    iconSuppressed,
                    reason,
                    hasBounds,
                    startUT,
                    endUT),
                FormatGhostOrbitLineDecision(
                    vesselPid,
                    reason,
                    lineActive,
                    drawIcons,
                    iconSuppressed,
                    belowAtmosphere,
                    hasBounds,
                    currentUT,
                    startUT,
                    endUT));
        }

        static void Postfix(OrbitRendererBase __instance)
        {
            if (__instance.vessel == null)
                return;

            uint pid = __instance.vessel.persistentId;
            if (!GhostMapPresence.IsGhostMapVessel(pid))
                return;

            var line = __instance.OrbitLine;
            if (line == null)
            {
                ParsekLog.VerboseRateLimited(Tag,
                    "missing-line-" + pid.ToString(CultureInfo.InvariantCulture),
                    string.Format(CultureInfo.InvariantCulture,
                        "Orbit line decision skipped: pid={0} reason=missing-orbit-line scene={1}",
                        pid,
                        HighLogic.LoadedScene),
                    5.0);
                return;
            }

            // Grace inputs (FIX #26 blink): the two TRANSIENT off reasons
            // (polyline-owns-phase / stale-segment-awaiting-reseed) can chatter
            // on/off frame-to-frame at a short phase-boundary segment because the
            // per-frame orbit reseed lags the playback head by up to one refresh
            // interval. A short UT grace window, stamped whenever the line is
            // genuinely shown, defers a transient off for one frame so the line
            // does not blink. Computed once here for both branches; the orbit
            // elements must still be finite + elliptical to have an arc to keep
            // showing. Durable off reasons (below-atmosphere / out-of-body-frame)
            // are NEVER graced (they are checked separately below).
            int graceCurrentFrame = Time.frameCount;
            int graceUntilFrame = GhostMapPresence.GetOrbitLineGraceUntilFrame(pid);
            bool orbitFiniteElliptical = IsOrbitFiniteElliptical(__instance.vessel?.orbit);

            // Polyline ownership (PR #970): while the map-view trajectory polyline
            // draws this recording's CURRENT non-orbital leg, hide the orbit LINE so
            // the two visuals do not overlap (and the orbit does not churn under
            // warp). Keep the renderer ENABLED (do NOT touch orbitRenderer.enabled)
            // so this Postfix keeps running every frame and re-shows the line
            // automatically once the polyline relinquishes the phase. line.active is
            // the real visibility control (same idiom as the atmosphere / out-of-
            // bounds suppression below). The vessel icon (OBJ) stays so the ghost's
            // position is still marked. Takes precedence over the atmosphere /
            // body-frame branches, and works identically in flight and the Tracking
            // Station because it is driven by the renderer's own LateUpdate, not the
            // orbit-updater cadence.
            if (GhostMapPresence.IsPolylineOwningGhostPhase(pid))
            {
                // Stamp the "polyline owning" real-time clock so the terminal-visible branch (below)
                // can defer the icon-show for a short grace after the polyline releases. Without
                // this defer the stock orbit icon would appear at the OrbitDriver's STALE mesh
                // transform (the pre-polyline segment's endpoint - e.g. the parking-orbit endpoint
                // before the orbit-raise gap) for the ~0.5s between polyline release and the next
                // seg-drive dispatcher tick, which is the visible "icon teleported far away to the
                // wrong position on the loiter orbit" symptom from the playtest. Stamping every
                // frame the polyline owns means the grace measures time-since-RELEASE precisely.
                GhostMapPresence.StampPolylineOwning(pid);

                // Grace (FIX #26): a TRANSIENT single-frame dip into
                // polyline-owns at a short phase boundary (the ghost is really
                // orbital this frame but a sub-second non-orbital leg covers the
                // instant) defers ONLY the orbit LINE hide while the grace window
                // is open, so the line does not blink. It must NOT re-show the
                // proto ICON: IsPolylineOwningGhostPhase(pid) is TRUE in this
                // branch, so the marker paths (ClassifyAtmosphericMarkerSkip /
                // DrawMapMarkers) still draw the non-proto trajectory marker
                // regardless of the suppressed-icon flag; re-enabling the proto
                // icon (OBJ) here would draw BOTH it and the non-proto marker for
                // the deferred frame (the transient double-icon the polyline-owns
                // branch exists to prevent). So keep drawIcons=NONE and leave the
                // icon suppressed; only line.active is deferred. (The
                // stale-segment grace-defer below DOES re-show OBJ because
                // IsPolylineOwningGhostPhase is FALSE there, so the marker is
                // already skipped and the proto icon is the right indicator.)
                // A SUSTAINED polyline-owns phase (e.g. the whole below-atmosphere
                // descent FIX #27 makes the polyline own) is NOT deferred at all:
                // the grace deadline was last stamped while the line was genuinely
                // shown, so it expires within OrbitLineGraceFrames render frames and
                // the hide takes effect, preventing a double-draw of line + polyline.
                if (ShouldDeferOrbitLineHide(
                        OffReasonPolylineOwns, graceCurrentFrame, graceUntilFrame, orbitFiniteElliptical))
                {
                    line.active = true;
                    __instance.drawIcons = OrbitRendererBase.DrawIcons.NONE;
                    GhostMapPresence.ghostsWithSuppressedIcon.Add(pid);
                    ParsekLog.VerboseRateLimited(Tag,
                        "grace-defer-" + pid.ToString(CultureInfo.InvariantCulture),
                        string.Format(CultureInfo.InvariantCulture,
                            "hide deferred by grace pid={0} reason={1} currentFrame={2} graceUntilFrame={3} (line only, icon stays suppressed)",
                            pid, OffReasonPolylineOwns, graceCurrentFrame, graceUntilFrame),
                        1.0);
                    return;
                }

                // Hide the orbit line AND the proto-vessel icon, and mark the icon
                // suppressed (same as the below-atmosphere branch). During a
                // non-orbital phase the proto orbit is meaningless and
                // GhostOrbitIconDrivePatch already suppresses the icon off-arc,
                // which makes ClassifyAtmosphericMarkerSkip draw the non-proto
                // trajectory marker. Leaving the proto icon as OBJ here would draw
                // BOTH the proto icon and the non-proto marker (the overlapping
                // icons seen in playtest), so the proto icon is hidden and the
                // non-proto marker is the sole position indicator for the phase.
                // Renderer stays enabled, so this re-shows next frame once the
                // polyline relinquishes (visible-body-frame / terminal branches
                // restore OBJ/ALL + remove the suppression).
                line.active = false;
                __instance.drawIcons = OrbitRendererBase.DrawIcons.NONE;
                GhostMapPresence.ghostsWithSuppressedIcon.Add(pid);
                LogOrbitLineDecision(
                    pid,
                    "polyline-owns-phase",
                    line.active,
                    __instance.drawIcons,
                    GhostMapPresence.IsIconSuppressed(pid),
                    belowAtmosphere: false,
                    hasBounds: false,
                    Planetarium.GetUniversalTime(),
                    double.NaN,
                    double.NaN);
                return;
            }

            // Atmosphere suppression — shared by both segment-based and terminal-orbit ghosts.
            // Below the atmosphere boundary, Keplerian orbits are meaningless (drag makes them
            // flicker wildly). Suppress the orbit line and icon, letting the trajectory-interpolated
            // atmospheric marker take over.
            CelestialBody body = __instance.driver?.referenceBody;
            bool belowAtmosphere = body != null && body.atmosphere
                && __instance.vessel.orbit != null
                && __instance.vessel.orbit.altitude < body.atmosphereDepth;

            // Segment-based ghosts: the orbit line is clipped by GhostOrbitArcPatch and the
            // icon position by GhostOrbitIconDrivePatch, both of which use SEGMENT bounds.
            // For the line.active toggle we instead use BODY-FRAME bounds (the run of
            // consecutive same-body OrbitSegments around the playback head). That keeps the
            // line continuously visible across inter-segment burns / sparse-physics gaps
            // inside one body frame, so the ghost icon stays on the map throughout the
            // body frame and only blinks at the actual SOI / body change. Per spec: the
            // ghost should jump from the end of a transfer trajectory to its correct
            // recorded position in the next body frame — no flicker inside a body frame.
            double currentUT = Planetarium.GetUniversalTime();
            if (GhostMapPresence.TryGetBodyFrameOrbitBoundsForGhostVessel(
                pid, currentUT, out double startUT, out double endUT))
            {
                if (currentUT > endUT || currentUT < startUT || belowAtmosphere)
                {
                    line.active = false;
                    __instance.drawIcons = OrbitRendererBase.DrawIcons.NONE;
                    if (belowAtmosphere)
                        GhostMapPresence.ghostsWithSuppressedIcon.Add(pid);
                    string reason = belowAtmosphere
                        ? "below-atmosphere"
                        : (currentUT > endUT ? "past-body-frame-end" : "before-body-frame-start");
                    LogOrbitLineDecision(
                        pid,
                        reason,
                        line.active,
                        __instance.drawIcons,
                        GhostMapPresence.IsIconSuppressed(pid),
                        belowAtmosphere,
                        hasBounds: true,
                        currentUT,
                        startUT,
                        endUT);
                    return;
                }

                // Stale-segment guard: the per-frame orbit reseed lags the head by up to
                // the refresh interval (~0.5 s), so right after a propulsive->orbital
                // handoff the proto-vessel still carries the PREVIOUS segment's orbit
                // elements for a moment. The body-frame bounds above span consecutive
                // same-body segments, so the line would otherwise re-show that STALE
                // (pre-burn) arc until the reseed catches up (the "old orbit then the
                // correct one" handoff flicker). Keep the line hidden (the always-on
                // trajectory polyline / non-proto marker covers the gap) until the APPLIED
                // SEGMENT bounds actually cover the head, so only the correct orbit is ever
                // drawn.
                if (GhostMapPresence.TryGetVisibleOrbitBoundsForGhostVessel(
                        pid, currentUT, out double segStartUT, out double segEndUT)
                    && (currentUT > segEndUT || currentUT < segStartUT))
                {
                    // Grace (FIX #26): the stale-segment guard fires every frame
                    // the head sits in the lag between leaving one segment and the
                    // reseed applying the next. At a phase boundary the head
                    // crosses many short segments, so without a debounce the line
                    // blinks off once per boundary. Defer the hide while the grace
                    // window is open: we are inside the body frame (the durable
                    // out-of-body-frame / below-atmosphere checks already passed
                    // above), so the line genuinely belongs on this frame; only
                    // the reseed has not caught up yet. Once grace expires the
                    // hide takes effect, so a genuinely stale arc is never shown
                    // for long.
                    if (ShouldDeferOrbitLineHide(
                            OffReasonStaleSegment, graceCurrentFrame, graceUntilFrame, orbitFiniteElliptical))
                    {
                        // Re-show the proto ICON here (unlike the polyline-owns
                        // grace-defer above): the polyline does NOT own this
                        // recording's phase in the stale-segment branch
                        // (IsPolylineOwningGhostPhase is false), so the marker
                        // paths already SKIP the non-proto marker (NativeIconActive
                        // is returned), making the proto icon the correct sole
                        // indicator. Showing OBJ here matches the visible-body-frame
                        // branch and does not double up with a non-proto marker.
                        line.active = true;
                        __instance.drawIcons = OrbitRendererBase.DrawIcons.OBJ;
                        GhostMapPresence.ghostsWithSuppressedIcon.Remove(pid);
                        ParsekLog.VerboseRateLimited(Tag,
                            "grace-defer-" + pid.ToString(CultureInfo.InvariantCulture),
                            string.Format(CultureInfo.InvariantCulture,
                                "hide deferred by grace pid={0} reason={1} currentFrame={2} graceUntilFrame={3}",
                                pid, OffReasonStaleSegment, graceCurrentFrame, graceUntilFrame),
                            1.0);
                        return;
                    }

                    line.active = false;
                    __instance.drawIcons = OrbitRendererBase.DrawIcons.NONE;
                    GhostMapPresence.ghostsWithSuppressedIcon.Add(pid);
                    LogOrbitLineDecision(
                        pid,
                        "stale-segment-awaiting-reseed",
                        line.active,
                        __instance.drawIcons,
                        GhostMapPresence.IsIconSuppressed(pid),
                        belowAtmosphere,
                        hasBounds: true,
                        currentUT,
                        segStartUT,
                        segEndUT);
                    return;
                }

                // Inside a body frame, above atmosphere: show line + vessel icon only
                // (no Ap/Pe/AN/DN). The arc shape itself follows the per-segment orbit
                // via GhostOrbitArcPatch, so the visible line continuously redraws when
                // the orbit driver retargets at each segment transition — but line.active
                // stays True, so the user never sees the line blink off mid-body-frame.
                line.active = true;
                __instance.drawIcons = OrbitRendererBase.DrawIcons.OBJ;
                GhostMapPresence.ghostsWithSuppressedIcon.Remove(pid);
                // Grace (FIX #26): the line is genuinely shown this frame, so
                // (re)stamp the grace deadline. A transient off-dip in the next
                // ~OrbitLineGraceFrames render frames is deferred; once the line stops being
                // genuinely shown the deadline stops advancing and any sustained
                // off takes effect when it expires.
                GhostMapPresence.StampOrbitLineGrace(pid, Time.frameCount + OrbitLineGraceFrames);
                LogOrbitLineDecision(
                    pid,
                    "visible-body-frame",
                    line.active,
                    __instance.drawIcons,
                    GhostMapPresence.IsIconSuppressed(pid),
                    belowAtmosphere,
                    hasBounds: true,
                    currentUT,
                    startUT,
                    endUT);
                return;
            }

            // Non-segment ghosts (terminal orbits): atmosphere-based suppression
            if (belowAtmosphere)
            {
                line.active = false;
                __instance.drawIcons = OrbitRendererBase.DrawIcons.NONE;
                GhostMapPresence.ghostsWithSuppressedIcon.Add(pid);
                LogOrbitLineDecision(
                    pid,
                    "terminal-below-atmosphere",
                    line.active,
                    __instance.drawIcons,
                    GhostMapPresence.IsIconSuppressed(pid),
                    belowAtmosphere,
                    hasBounds: false,
                    currentUT,
                    double.NaN,
                    double.NaN);
            }
            else
            {
                // Post-polyline-release icon grace: the trajectory polyline just released ownership
                // of this ghost's non-orbital phase (within PolylineReleaseGraceSeconds, stamped each
                // frame the polyline-owns branch above fired). The seg-drive dispatcher runs on a
                // ~0.5s cadence so the next orbital segment has NOT been applied yet, which means
                // the OrbitDriver mesh transform is still at the pre-polyline segment's endpoint
                // (e.g. the parking-orbit endpoint before the orbit-raise gap). Showing the stock
                // orbit icon now (drawIcons=ALL) would draw it at that stale position - the visible
                // "icon teleported far away to the wrong position on the loiter orbit" playtest
                // seam. Defer the icon-show until seg-drive applies. The orbit LINE is ALSO held off
                // here: with no fresh segment applied yet (hasBounds is false in this terminal
                // branch), the renderer still holds the PREVIOUS segment's ellipse, so re-enabling
                // the line would briefly redraw that stale ellipse (the parking circle flashing for
                // ~0.3-0.5s before the loiter line appears - the "glimpse of another circular orbit
                // before the loiter" playtest report). A hidden line for the few grace frames until
                // seg-drive applies is strictly better than drawing the wrong ellipse; the trajectory
                // polyline already covered the visual through the non-orbital phase. On the next tick,
                // seg-drive applies, hasBounds becomes true, the visible-body-frame branch (above)
                // fires with the fresh loiter orbit, line + icon show in the right place, and the
                // stamp expires naturally over the grace.
                bool postPolylineReleaseGrace =
                    GhostMapPresence.IsPolylineRecentlyOwningGhostPhase(pid, PolylineReleaseGraceSeconds)
                    && !GhostMapPresence.IsPolylineOwningGhostPhase(pid);
                if (postPolylineReleaseGrace)
                {
                    line.active = false;
                    __instance.drawIcons = OrbitRendererBase.DrawIcons.NONE;
                    GhostMapPresence.ghostsWithSuppressedIcon.Add(pid);
                    LogOrbitLineDecision(
                        pid,
                        "post-polyline-release-grace",
                        line.active,
                        __instance.drawIcons,
                        GhostMapPresence.IsIconSuppressed(pid),
                        belowAtmosphere,
                        hasBounds: false,
                        currentUT,
                        double.NaN,
                        double.NaN);
                    return;
                }

                line.active = true;
                __instance.drawIcons = OrbitRendererBase.DrawIcons.ALL;
                GhostMapPresence.ghostsWithSuppressedIcon.Remove(pid);
                // Grace (FIX #26): a terminal-orbit ghost genuinely shown this
                // frame restamps the grace deadline too, so a transient
                // polyline-owns dip on a terminal ghost is debounced as well.
                GhostMapPresence.StampOrbitLineGrace(pid, Time.frameCount + OrbitLineGraceFrames);
                LogOrbitLineDecision(
                    pid,
                    "terminal-visible",
                    line.active,
                    __instance.drawIcons,
                    GhostMapPresence.IsIconSuppressed(pid),
                    belowAtmosphere,
                    hasBounds: false,
                    currentUT,
                    double.NaN,
                    double.NaN);
            }
        }
    }

    /// <summary>
    /// Pure: is the vessel currently on the visible orbital arc between startUT and endUT?
    /// Uses orbital time (getObtAtUT) which is monotonically increasing within a period,
    /// avoiding the sign mismatch bugs in eccentric/true anomaly (#212b).
    /// </summary>
    internal static class GhostOrbitArcCheck
    {
        internal static bool IsOnOrbitalArc(Orbit orbit, double startUT, double endUT, double currentUT)
        {
            double period = orbit.period;
            if (period <= 0) return true; // degenerate orbit — show by default

            // Normalize all orbital times to [0, period)
            double obtStart = ((orbit.getObtAtUT(startUT) % period) + period) % period;
            double obtEnd = ((orbit.getObtAtUT(endUT) % period) + period) % period;
            double obtNow = ((orbit.getObtAtUT(currentUT) % period) + period) % period;

            // If the arc spans the full period (or more), the vessel is always on arc
            if (endUT - startUT >= period) return true;

            // Standard range check with wraparound handling
            if (obtStart <= obtEnd)
                return obtNow >= obtStart && obtNow <= obtEnd;
            else
                return obtNow >= obtStart || obtNow <= obtEnd;
        }
    }

    /// <summary>
    /// Clips the ghost orbit line to only show the arc between the orbit segment's
    /// startUT and endUT, instead of the full Keplerian ellipse. Without this patch,
    /// suborbital ghosts show orbit lines passing through the planet surface.
    ///
    /// Uses the same eccentric anomaly arc-clipping logic as KSP's own
    /// PatchRendering / Trajectory.UpdateFromOrbit() — proven, battle-tested.
    ///
    /// Only applies to segment-based ghosts (those with entries in ghostOrbitBounds).
    /// Terminal-orbit ghosts (stable orbits) render the stock full ellipse.
    /// Hyperbolic orbits and full-period segments fall through to stock rendering.
    /// </summary>
    [HarmonyPatch(typeof(OrbitRendererBase), "UpdateSpline")]
    internal static class GhostOrbitArcPatch
    {
        private const string Tag = "GhostOrbitArc";

        static bool Prefix(OrbitRendererBase __instance)
        {
            if (__instance.vessel == null) return true;
            uint pid = __instance.vessel.persistentId;
            if (!GhostMapPresence.IsGhostMapVessel(pid)) return true;
            double currentUT = Planetarium.GetUniversalTime();
            if (!GhostMapPresence.TryGetVisibleOrbitBoundsForGhostVessel(
                pid, currentUT, out double startUT, out double endUT))
            {
                ParsekLog.VerboseRateLimited(Tag, "nobounds-" + pid,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Ghost pid={0} has NO orbit bounds — stock full ellipse (scene={1})",
                        pid, HighLogic.LoadedScene));
                return true;
            }

            Orbit orbit = __instance.driver?.orbit;
            if (orbit == null) return true;
            if (orbit.eccentricity >= 1.0) return true; // let stock handle hyperbolic

            // Full orbit or more — let stock draw the complete ellipse. The span is
            // shift-invariant, so the stored live-frame bounds give the correct test.
            if (endUT - startUT >= orbit.period) return true;

            // The OrbitDriver is now seeded with the RAW recorded epoch (GhostOrbitIconDrivePatch
            // drives it at effUT = liveUT - shift), but the stored arc bounds are in the LIVE frame.
            // Map them back to the recorded clock so the eccentric-anomaly arc shape is computed in
            // the SAME frame the icon is driven in — keeping the line and the marker in exact
            // lockstep on arbitrarily short arcs. shift is 0 (identity) off the loop path.
            double arcShift = GhostMapPresence.GetGhostOrbitEpochShift(pid);
            double startUTRaw = GhostMapPresence.MapLiveUTToEffUT(startUT, arcShift);
            double endUTRaw = GhostMapPresence.MapLiveUTToEffUT(endUT, arcShift);

            // Convert UT bounds to eccentric anomaly (same as PatchRendering/Trajectory)
            double fromE = orbit.EccentricAnomalyAtUT(startUTRaw);
            double toE = orbit.EccentricAnomalyAtUT(endUTRaw);

            // NaN guard — degenerate orbits or UT outside validity
            if (double.IsNaN(fromE) || double.IsNaN(toE)) return true;

            // Handle wraparound (periapsis crossing) — same logic as Trajectory.UpdateFromOrbit.
            // GetTrueAnomaly returns [0, 2pi] for E in [0, 2pi). When fromV > toV, the arc
            // wraps through periapsis (V=0). Making fromE negative creates a monotonically
            // increasing range that crosses E=0.
            double fromV = orbit.GetTrueAnomaly(fromE);
            double toV = orbit.GetTrueAnomaly(toE);
            if (fromV > toV)
                fromE = -(Math.PI * 2.0 - fromE);

            // Sample the partial arc across all available points
            var orbitPoints = __instance.OrbitPoints;
            double semiMinorAxis = orbit.semiMinorAxis;
            int count = orbitPoints.Length; // 180 at stock sampleResolution=2.0
            double interval = (toE - fromE) / (count - 1);

            for (int i = 0; i < count; i++)
                orbitPoints[i] = orbit.getPositionFromEccAnomalyWithSemiMinorAxis(
                    fromE + interval * (double)i, semiMinorAxis);

            // Convert to scaled space and set draw range (open arc, no loop closing)
            var line = __instance.OrbitLine;
            ScaledSpace.LocalToScaledSpace(orbitPoints, line.points3);

            // Defensive: zero the stale closing point (index 180) and set draw range
            if (line.points3.Count > count)
                line.points3[count] = line.points3[count - 1];
            line.drawStart = 0;
            line.drawEnd = count - 1; // 179 — open arc, same as stock hyperbolic

            // Diagnostic: compute altitude at arc endpoints for verification
            double bodyRadius = orbit.referenceBody != null ? orbit.referenceBody.Radius : 0;
            double startR = orbit.semiMajorAxis * (1.0 - orbit.eccentricity * Math.Cos(fromE));
            double endR = orbit.semiMajorAxis * (1.0 - orbit.eccentricity * Math.Cos(toE));

            ParsekLog.VerboseRateLimited(Tag, pid.ToString(),
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Arc clip pid={0} fromE={1:F3} toE={2:F3} arc={3:F1}deg " +
                    "startAlt={4:F0} endAlt={5:F0} bodyR={6:F0} ecc={7:F4} sma={8:F0} " +
                    "drawEnd={9} pts={10} scene={11}",
                    pid, fromE, toE,
                    (toE - fromE) * (180.0 / Math.PI),
                    startR - bodyRadius, endR - bodyRadius, bodyRadius,
                    orbit.eccentricity, orbit.semiMajorAxis,
                    line.drawEnd, count,
                    HighLogic.LoadedScene));

            return false; // skip original UpdateSpline
        }
    }
}
