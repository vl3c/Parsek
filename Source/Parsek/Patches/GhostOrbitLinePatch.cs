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
    /// Clamps ghost vessel OrbitDriver propagation to the visible arc of the orbit
    /// segment (#212b). Without this, the OrbitDriver propagates the vessel along
    /// the full Keplerian ellipse, positioning the icon underground during the
    /// periapsis passage. The orbit LINE is clipped by GhostOrbitArcPatch, but the
    /// vessel ICON follows the OrbitDriver position — this patch keeps the icon
    /// on the visible arc by clamping the propagated UT.
    ///
    /// When the vessel would be on the underground portion, the UT is clamped to
    /// the nearest arc endpoint (startUT or endUT), freezing the icon at the edge
    /// of the visible arc.
    ///
    /// Architecture: OrbitDriver.updateFromParameters() calls orbit.UpdateFromUT(UT)
    /// which sets the vessel's world position. This prefix intercepts the UT before
    /// propagation and clamps it to the visible arc if needed.
    /// </summary>
    [HarmonyPatch(typeof(OrbitDriver), "updateFromParameters", new Type[0])]
    internal static class GhostOrbitIconClampPatch
    {
        static void Prefix(OrbitDriver __instance)
        {
            if (__instance.vessel == null) return;

            uint pid = __instance.vessel.persistentId;
            if (!GhostMapPresence.IsGhostMapVessel(pid)) return;
            double currentUT = Planetarium.GetUniversalTime();
            if (!GhostMapPresence.TryGetVisibleOrbitBoundsForGhostVessel(
                pid, currentUT, out double startUT, out double endUT))
                return;

            Orbit orbit = __instance.orbit;
            if (orbit == null || orbit.eccentricity >= 1.0 || orbit.period <= 0) return;

            // Past recording bounds → clamp to endUT so icon sits at last recorded position
            if (currentUT > endUT)
            {
                orbit.UpdateFromUT(endUT);
                GhostMapPresence.ghostsWithSuppressedIcon.Add(pid);
                return;
            }
            if (currentUT < startUT)
            {
                orbit.UpdateFromUT(startUT);
                GhostMapPresence.ghostsWithSuppressedIcon.Add(pid);
                return;
            }

            // Within bounds — check if on the visible arc
            bool onArc = GhostOrbitArcCheck.IsOnOrbitalArc(orbit, startUT, endUT, currentUT);
            if (!onArc)
            {
                // Off the visible arc (underground) — clamp to the nearest arc endpoint
                double period = orbit.period;
                double obtNow = ((orbit.getObtAtUT(currentUT) % period) + period) % period;
                double obtStart = ((orbit.getObtAtUT(startUT) % period) + period) % period;
                double obtEnd = ((orbit.getObtAtUT(endUT) % period) + period) % period;

                // Pick the closer endpoint in orbital-time space
                double distToStart, distToEnd;
                if (obtStart <= obtEnd)
                {
                    // No wraparound: gap is [obtEnd, obtStart+period]
                    distToStart = (obtStart - obtNow + period) % period;
                    distToEnd = (obtNow - obtEnd + period) % period;
                }
                else
                {
                    // Wraparound: gap is [obtEnd, obtStart]
                    distToStart = (obtNow <= obtStart) ? (obtStart - obtNow) : (obtStart + period - obtNow);
                    distToEnd = (obtNow >= obtEnd) ? (obtNow - obtEnd) : (obtNow + period - obtEnd);
                }

                double clampUT = (distToEnd <= distToStart) ? endUT : startUT;
                orbit.UpdateFromUT(clampUT);
                GhostMapPresence.ghostsWithSuppressedIcon.Add(pid);

                ParsekLog.VerboseRateLimited("GhostOrbitIcon", "clamp-" + pid,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Icon clamped pid={0} UT={1:F1} clampUT={2:F1} bounds=[{3:F1},{4:F1}]",
                        pid, currentUT, clampUT, startUT, endUT));
                return;
            }

            GhostMapPresence.ghostsWithSuppressedIcon.Remove(pid);
        }
    }

    /// <summary>
    /// Suppresses the orbit line for ghost map ProtoVessels below atmosphere.
    /// The orbit line is meaningless during atmospheric flight (drag makes Keplerian
    /// propagation diverge from recorded trajectory).
    ///
    /// Architecture: OrbitRendererBase.LateUpdate calls DrawOrbit() then DrawNodes().
    /// This postfix runs after both, suppressing the Vectrosity line for ghost vessels.
    /// Icon clamping is handled separately by GhostOrbitIconClampPatch on OrbitDriver.
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
                // GhostOrbitIconClampPatch already suppresses the icon off-arc,
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
            // icon position by GhostOrbitIconClampPatch, both of which use SEGMENT bounds.
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

            // Full orbit or more — let stock draw the complete ellipse
            if (endUT - startUT >= orbit.period) return true;

            // Convert UT bounds to eccentric anomaly (same as PatchRendering/Trajectory)
            double fromE = orbit.EccentricAnomalyAtUT(startUT);
            double toE = orbit.EccentricAnomalyAtUT(endUT);

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
