using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Gated render-path observability for ghost rendering in MAP VIEW and the
    /// TRACKING STATION, structurally a sibling of <see cref="GhostRenderTrace"/>
    /// (which instruments the flight-scene mesh placement). Off by default
    /// behind the <c>mapRenderTracing</c> setting; read-only instrumentation
    /// that never mutates renderer, orbit, line, icon, or marker state.
    ///
    /// <para>This Phase-1 skeleton carries the gate, the <see cref="RenderSurface"/>
    /// enum, the detailed-window registry, the line formatters (reproduced from
    /// <see cref="GhostRenderTrace"/> so the two tracers share one
    /// <c>key=value</c> schema), and the <see cref="EmitRaw"/> sink. The
    /// end-of-frame truth probe and the structural / decision hooks land in later
    /// phases. The MVP keys per-ghost state and detailed windows by
    /// <c>Vessel.persistentId</c> (passed as <c>pid.ToString()</c>); the coarser
    /// <c>recordingId</c> key is a later cut.</para>
    /// </summary>
    internal static class MapRenderTrace
    {
        // Single subsystem tag for the whole map / TS render surface, mirroring
        // the GhostRenderTrace tag model so one grep filter lights up every
        // surface around an event.
        internal const string Tag = "MapRenderTrace";

        // Window-length constants mirror GhostRenderTrace's window model.
        internal const double InitialWindowSeconds = 4.0;
        internal const double SegmentChangeWindowSeconds = 2.0;
        internal const double SectionChangeWindowSeconds = 2.0;
        internal const double AnomalyWindowSeconds = 5.0;
        internal const double DestroyWindowSeconds = 1.0;

        // ---- Tier-C anomaly tuning ----

        /// <summary>
        /// Fixed single-frame icon-position jump floor (metres). Carried over
        /// from the <c>GhostRenderStateProbe</c> prototype: a real on-orbit
        /// SOI-exit teleport was tens of millions of metres, so 1000 km/frame
        /// cleanly separates "real teleport" from normal motion. This is a FLOOR
        /// under the orbit-derived expected-motion model (expected =
        /// orbital speed * dt * warpRate) so degenerate / near-zero-velocity
        /// orbits never report a spurious jump while a slow real teleport on a
        /// fast orbit can still exceed the orbit-derived threshold. NOTE: the
        /// caller now measures the delta in the orbit's BODY-RELATIVE frame
        /// (see <see cref="IsIconJump"/>), so this floor compares against the
        /// orbit-relative displacement, not the raw world-frame delta.
        /// </summary>
        internal const double IconJumpFloorMeters = 1_000_000.0;

        /// <summary>
        /// Multiplier applied to the orbit-derived expected per-frame motion
        /// before it becomes a jump threshold (slack for interpolation /
        /// sampling jitter). Mirrors <see cref="GhostRenderTrace"/>'s
        /// <c>VelocityDeltaMultiplier</c>.
        /// </summary>
        internal const double ExpectedMotionMultiplier = 4.0;

        /// <summary>
        /// Minimum angle (degrees) between the proto icon's body-relative position
        /// and the orbit's OWN predicted body-relative position (at the icon's
        /// drive clock) before the <c>icon-off-orbit</c> anomaly fires. The orbit
        /// LINE and the vessel ICON both derive from <c>OrbitDriver.orbit</c>, so a
        /// correctly placed icon sits ON its line (angle ~0); a large angle means
        /// the icon is off its own orbit - the looped / re-aimed rotation bug that
        /// <see cref="IsIconJump"/> (no per-frame delta) and <see cref="IsLineBlink"/>
        /// (line stays active) are both blind to. 1 deg cleanly separates float /
        /// interpolation noise from the real defect (a body-rotation-over-loop-shift
        /// residual is tens of degrees).
        /// </summary>
        internal const double IconOffOrbitMinAngleDeg = 1.0;

        /// <summary>
        /// Floating-origin shift-frame suppression window (frames). On a
        /// stock <c>FloatingOrigin.setOffset</c> rebase every ghost shifts by
        /// the same magnitude on the same frame; the jump detector would read
        /// that as a teleport. Suppress for the shift frame itself plus this
        /// many frames of slack, matching
        /// <see cref="GhostRenderTrace.FloatingOriginSuppressionFrameWindow"/>.
        /// </summary>
        internal const int FloatingOriginSuppressionFrameWindow = 1;

        /// <summary>
        /// Window (frames) within which a <c>line.active</c> toggle out and
        /// back counts as a blink. A renderer that legitimately turns its line
        /// off and leaves it off for many frames is not a blink; a 1-frame
        /// flicker is. The probe samples once per visual frame, so consecutive
        /// frames differ by 1.
        /// </summary>
        internal const int LineBlinkFrameWindow = 8;

        internal struct GateDecision
        {
            public bool Emit;
            public bool Important;
            public string Reason;
        }

        /// <summary>
        /// The map / tracking-station rendering surface a trace line describes.
        /// Every emitted line carries <c>surface=</c> so a grep slices the log by
        /// surface without stitching state flags across patches.
        /// </summary>
        internal enum RenderSurface : byte
        {
            /// <summary>
            /// Default — caller did not specify; surface is unknown to the
            /// trace. Logged as "unknown".
            /// </summary>
            Unknown = 0,

            /// <summary>The scaled-space Vectrosity proto orbit line.</summary>
            ProtoOrbitLine = 1,

            /// <summary>The native KSP map icon driven by the OrbitDriver.</summary>
            ProtoIcon = 2,

            /// <summary>
            /// The non-orbital <c>GhostTrajectoryPolylineRenderer</c> leg.
            /// </summary>
            Polyline = 3,

            /// <summary>
            /// The flight-scene <c>ParsekUI.DrawMapMarkers</c> labeled marker.
            /// </summary>
            ImguiLabeledMarker = 4,

            /// <summary>
            /// The TS <c>ParsekTrackingStation.DrawAtmosphericMarkers</c> marker.
            /// </summary>
            AtmosphericMarker = 5,
        }

        private static string RenderSurfaceToken(RenderSurface surface)
        {
            switch (surface)
            {
                case RenderSurface.ProtoOrbitLine: return "ProtoOrbitLine";
                case RenderSurface.ProtoIcon: return "ProtoIcon";
                case RenderSurface.Polyline: return "Polyline";
                case RenderSurface.ImguiLabeledMarker: return "ImguiLabeledMarker";
                case RenderSurface.AtmosphericMarker: return "AtmosphericMarker";
                default: return "unknown";
            }
        }

        // ---- Marker-decision observability (per-ghost WHY a marker drew / was skipped) ----

        /// <summary>
        /// The terminal OUTCOME of one ghost's per-recording marker-decision pass in
        /// <c>ParsekUI.DrawMapMarkers</c> / <c>ParsekTrackingStation.DrawAtmosphericMarkers</c>.
        /// Every branch in those loops maps to exactly one of these so a single per-pid trace
        /// line says, in one token, WHY this ghost's marker drew or did not this frame.
        /// </summary>
        internal enum MarkerOutcome : byte
        {
            /// <summary>Default / not yet decided.</summary>
            Unknown = 0,

            /// <summary>The Parsek non-proto labeled marker drew (proto icon hidden).</summary>
            DrawnNonProto = 1,

            /// <summary>
            /// The stock proto vessel icon is the visible indicator, so the non-proto
            /// marker was correctly skipped (no gap).
            /// </summary>
            DrawnProtoIcon = 2,

            /// <summary>Skipped: the recording is debris.</summary>
            SkippedDebris = 3,

            /// <summary>Skipped: a non-tip chain member (the tip draws the marker).</summary>
            SkippedChainNonTip = 4,

            /// <summary>
            /// Skipped: the ghost mesh is hidden and we are not in map view, so its
            /// stale transform would project to the wrong place (#245/#247).
            /// </summary>
            SkippedNotOnMap = 5,

            /// <summary>
            /// Skipped: the marker-draw decision (<c>ShouldDrawNonProtoMarkerForGhost</c>)
            /// returned false with no proto icon, or another classify-skip fired
            /// (no trajectory points / outside time range / chain filter / orbit segment).
            /// </summary>
            SkippedDecisionFalse = 6,

            /// <summary>Skipped: the world position could not be resolved this frame.</summary>
            SkippedPositionFail = 7,

            /// <summary>Skipped: a loop member is outside its render window this cycle.</summary>
            SkippedLoopHidden = 8,
        }

        internal static string MarkerOutcomeToken(MarkerOutcome outcome)
        {
            switch (outcome)
            {
                case MarkerOutcome.DrawnNonProto: return "drawn-non-proto";
                case MarkerOutcome.DrawnProtoIcon: return "drawn-proto-icon";
                case MarkerOutcome.SkippedDebris: return "skipped-debris";
                case MarkerOutcome.SkippedChainNonTip: return "skipped-chain-non-tip";
                case MarkerOutcome.SkippedNotOnMap: return "skipped-not-on-map";
                case MarkerOutcome.SkippedDecisionFalse: return "skipped-decision-false";
                case MarkerOutcome.SkippedPositionFail: return "skipped-position-fail";
                case MarkerOutcome.SkippedLoopHidden: return "skipped-loop-hidden";
                default: return "unknown";
            }
        }

        /// <summary>
        /// Why the non-proto marker did (or did not) ride the trajectory polyline this frame.
        /// Surfaces the previously-opaque bool return of
        /// <c>GhostTrajectoryPolylineRenderer.TryAnchorMarkerToPolyline</c> without changing
        /// its ride logic - the caller maps the new <c>out</c> reason straight into the trace line.
        /// </summary>
        internal enum MarkerRideReason : byte
        {
            /// <summary>Default / the marker path did not attempt a ride this frame.</summary>
            NotAttempted = 0,

            /// <summary>Rode a drawn leg (the leg index + interpolation t are logged alongside).</summary>
            RodeLeg = 1,

            /// <summary>Fallback: the head's leg was not drawn THIS frame (stale scratch).</summary>
            FallbackLegNotDrawnThisFrame = 2,

            /// <summary>Fallback: the head UT falls outside every leg's [start,end].</summary>
            FallbackHeadOutsideLegs = 3,

            /// <summary>Fallback: the leg is missing its recorded-UT / scratch arrays.</summary>
            FallbackMissingRecordedUTs = 4,

            /// <summary>Fallback: no polyline cache entry for this recording id.</summary>
            FallbackNoCache = 5,
        }

        internal static string MarkerRideReasonToken(MarkerRideReason reason, int legIndex)
        {
            switch (reason)
            {
                case MarkerRideReason.RodeLeg: return "rode-leg" + legIndex.ToString(CultureInfo.InvariantCulture);
                case MarkerRideReason.FallbackLegNotDrawnThisFrame: return "fallback-leg-not-drawn-this-frame";
                case MarkerRideReason.FallbackHeadOutsideLegs: return "fallback-head-outside-legs";
                case MarkerRideReason.FallbackMissingRecordedUTs: return "fallback-missing-recordedUTs";
                case MarkerRideReason.FallbackNoCache: return "fallback-no-cache";
                default: return "not-attempted";
            }
        }

        /// <summary>
        /// Pure builder for the per-pid marker-decision SIGNATURE (the change-detection key) AND
        /// the human-readable detail tail (they are the same <c>key=value</c> string here, so one
        /// build serves both: identical signature =&gt; identical line =&gt; suppressed). Carries the
        /// four decision disjuncts from <c>ResolveMarkerDrawDecision</c>, the resolved
        /// <c>shouldDrawNonProto</c> bool, the terminal outcome, and (for a drawn non-proto marker)
        /// the polyline ride reason + the fallback position source actually used. Kept pure (no Unity
        /// reads) so the schema is unit-testable. <paramref name="legIndex"/> is only meaningful when
        /// <paramref name="rideReason"/> is <see cref="MarkerRideReason.RodeLeg"/>.
        /// </summary>
        internal static string BuildMarkerDecisionSignature(
            int recordingIndex,
            string vesselName,
            bool gateOn,
            bool directorTracedPathActive,
            bool polylineOwning,
            bool iconSuppressed,
            bool shouldDrawNonProto,
            MarkerOutcome outcome,
            MarkerRideReason rideReason,
            int legIndex,
            string posSource)
        {
            string s = "rec=" + recordingIndex.ToString(CultureInfo.InvariantCulture)
                + " vessel=" + Token(vesselName)
                + " gateOn=" + Bool(gateOn)
                + " directorTracedPathActive=" + Bool(directorTracedPathActive)
                + " polylineOwning=" + Bool(polylineOwning)
                + " iconSuppressed=" + Bool(iconSuppressed)
                + " shouldDrawNonProto=" + Bool(shouldDrawNonProto)
                + " outcome=" + MarkerOutcomeToken(outcome);
            if (outcome == MarkerOutcome.DrawnNonProto)
                s += " ride=" + MarkerRideReasonToken(rideReason, legIndex)
                    + " posSource=" + Token(posSource);
            return s;
        }

        // Per-pid (here pid = recordingId; the marker surfaces are recordingId-keyed, carried in the
        // prefix pid= slot to match EmitMarker) last-emitted signature. CALLER-OWNED change detection:
        // EmitMarkerDecisionOnChange emits only when the composed signature differs from the last one
        // for that key. Cleared in Reset() (driven by MapRenderProbe's scene-switch hook), mirroring
        // lineIntentByPid / detailedUntilByKey so a stale signature never suppresses the first
        // post-re-entry transition.
        private static readonly Dictionary<string, string> lastMarkerDecisionSignatureByPid =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Change-based per-pid marker-decision emit (Tier-B, routed to Verbose via
        /// <see cref="EmitOnChange"/>). Owns the per-pid last-signature dict so the call sites stay
        /// thin: pass the pre-built <paramref name="signature"/> (from
        /// <see cref="BuildMarkerDecisionSignature"/>) and this emits ONE line only when that ghost's
        /// signature changed since its last emit, capturing sub-second transitions without per-frame
        /// spam. No-op when disabled (gate short-circuits before any dict touch, so a closed tracer
        /// pays nothing beyond the caller's own <see cref="IsEnabled"/> guard).
        /// </summary>
        internal static void EmitMarkerDecisionOnChange(
            RenderSurface surface, string pidKey, double currentUT, string signature)
        {
            if (!IsEnabled)
                return;
            if (string.IsNullOrEmpty(pidKey))
                return;

            string last;
            if (lastMarkerDecisionSignatureByPid.TryGetValue(pidKey, out last)
                && string.Equals(last, signature, StringComparison.Ordinal))
                return; // unchanged outcome for this ghost -> suppress

            lastMarkerDecisionSignatureByPid[pidKey] = signature;
            EmitOnChange("MarkerDecision", surface, pidKey, currentUT, currentUT, signature);
        }

        // MVP: detailed windows are keyed by pid.ToString(). recordingId keying
        // (and the shared registry with GhostRenderTrace) is a later cut.
        private static readonly Dictionary<string, double> detailedUntilByKey =
            new Dictionary<string, double>(StringComparer.Ordinal);

        internal static bool ForceEnabledForTesting;

        /// <summary>
        /// Test seam for the ambient Unity frame counter. Production reads
        /// <c>Time.frameCount</c>; xUnit cannot call into Unity natives so tests
        /// override this to a deterministic value. Reset to <c>null</c> in test
        /// teardown.
        /// </summary>
        internal static System.Func<int> FrameCounterOverrideForTesting;

        internal static bool IsEnabled =>
            ForceEnabledForTesting
            || (ParsekSettings.Current != null && ParsekSettings.Current.mapRenderTracing);

        internal static void Reset()
        {
            detailedUntilByKey.Clear();
            lineIntentByPid.Clear();
            renderIntentByPid.Clear();
            lastMarkerDecisionSignatureByPid.Clear();
        }

        private static int CurrentFrameCount()
        {
            var ovr = FrameCounterOverrideForTesting;
            if (ovr != null)
                return ovr();
            return UnityFrameCount();
        }

        // Isolated in its own method so xUnit JIT verification of
        // CurrentFrameCount does not have to walk into a Unity ECall site. Test
        // runs always go through the override above; this method is only ever
        // JIT-compiled when the override is null, which only happens inside the
        // live KSP runtime where the ECall is legal.
        private static int UnityFrameCount()
        {
            return Time.frameCount;
        }

        /// <summary>
        /// Opens (or extends) a detailed-window for a tracked ghost so the
        /// surrounding frames emit full per-frame detail even after the
        /// structural reason that triggered them has passed. The MVP passes
        /// <c>pid.ToString()</c> as the key.
        /// </summary>
        internal static void OpenDetailedWindow(
            string key, double currentUT, double seconds, string reason)
        {
            if (!IsEnabled)
                return;
            if (string.IsNullOrEmpty(key))
                return;
            if (double.IsNaN(currentUT) || double.IsInfinity(currentUT))
                return;

            double until = currentUT + Math.Max(0.0, seconds);
            double existing;
            if (!detailedUntilByKey.TryGetValue(key, out existing)
                || until > existing)
            {
                detailedUntilByKey[key] = until;
            }
        }

        internal static bool IsDetailedWindowOpen(string key, double currentUT)
        {
            if (string.IsNullOrEmpty(key))
                return false;
            double until;
            return detailedUntilByKey.TryGetValue(key, out until)
                && currentUT <= until;
        }

        // ---- Tier-C anomaly predicates (pure; Unity-ECall-free) ----

        /// <summary>
        /// Production reads
        /// <see cref="ReFlySettleStabilityTracker.LastFloatingOriginShiftFrame"/>;
        /// xUnit overrides via <see cref="FloatingOriginFrameOverrideForTesting"/>
        /// so the suppression test can drive the floating-origin frame without
        /// going through the tracker's logging path. Mirrors the equivalent
        /// seam in <see cref="GhostRenderTrace"/>.
        /// </summary>
        internal static System.Func<int> FloatingOriginFrameOverrideForTesting;

        internal static int LastFloatingOriginShiftFrame()
        {
            var ovr = FloatingOriginFrameOverrideForTesting;
            if (ovr != null)
                return ovr();
            return ReFlySettleStabilityTracker.LastFloatingOriginShiftFrame;
        }

        /// <summary>
        /// Pure <c>icon-jump</c> anomaly predicate (Tier C). Returns true when
        /// the observed per-frame <paramref name="dPos"/> exceeds the threshold
        /// AND the frame is not suppressed. The threshold is the larger of the
        /// fixed <see cref="IconJumpFloorMeters"/> floor and the orbit-derived
        /// expected motion (caller computes expected =
        /// orbital speed * unscaledDeltaTime * warpRate) scaled by
        /// <see cref="ExpectedMotionMultiplier"/>, so a degenerate near-zero
        /// orbit falls back to the floor while a slow teleport on a fast orbit
        /// can still trip the orbit-derived threshold.
        ///
        /// <para><paramref name="dPos"/> MUST be measured in the orbit's own
        /// reference-body frame (the caller passes the delta of
        /// <c>GetWorldPos3D - referenceBody.position</c>, i.e. the body-relative
        /// position), NOT the raw <c>GetWorldPos3D</c> world-frame delta. The
        /// expected-motion model is the orbital arc about the reference body, so
        /// the measured delta must be in that same frame. Comparing a raw
        /// world-frame delta against a body-centered orbital speed flags smooth
        /// fast coasts at high warp as false positives: the world-frame delta of
        /// a ghost far from the floating origin is dominated by the
        /// reference-body's own world motion under warp (read at the same live UT
        /// as the ghost, so a re-aimed ghost's loop shift is not a factor here),
        /// which scales with geometry and is unrelated to the ghost's orbital
        /// speed. The body-relative frame cancels all of that
        /// (KSP builds an on-rails vessel's world position as
        /// <c>referenceBody.position + orbitRelative</c>, so the body-relative
        /// delta IS the orbital arc).</para>
        ///
        /// <para>Suppressed on the first frame after a per-pid state reset
        /// (<paramref name="justReset"/>: there is no trustworthy previous
        /// position), on the frame the orbit's reference body changes
        /// (<paramref name="bodyChanged"/>: a body-relative delta across an SOI
        /// crossing compares two different frames and is meaningless), and on
        /// floating-origin shift frames
        /// (<paramref name="floatingOriginShiftFrame"/> within
        /// <see cref="FloatingOriginSuppressionFrameWindow"/> of
        /// <paramref name="currentFrame"/>), mirroring
        /// <see cref="GhostRenderTrace.IsLargeDeltaSignalSuppressed"/>. (The
        /// floating-origin suppression is largely redundant once the delta is
        /// body-relative, since the rebase cancels with the body's own shift, but
        /// it is kept as a cheap belt-and-braces guard.)</para>
        /// </summary>
        internal static bool IsIconJump(
            double dPos,
            double expectedMotionMeters,
            int currentFrame,
            int floatingOriginShiftFrame,
            bool justReset,
            bool bodyChanged)
        {
            // No trustworthy previous position right after a per-pid reset
            // (scene transition / ghost-pid rebuild). A stale prevBodyRelPos
            // would otherwise fire a spurious jump on re-entry.
            if (justReset)
                return false;

            // The orbit's reference body changed this frame (e.g. SOI crossing
            // Kerbin -> Sun). The previous body-relative position was measured in
            // the OLD body's frame, so its delta against this frame's NEW-body
            // position is a frame mismatch, not a teleport.
            if (bodyChanged)
                return false;

            if (double.IsNaN(dPos) || double.IsInfinity(dPos))
                return false;

            // Floating-origin rebase: every ghost shifts the same magnitude on
            // the same frame; the delta is the rebase, not a teleport.
            if (floatingOriginShiftFrame != int.MinValue
                && currentFrame >= floatingOriginShiftFrame
                && currentFrame - floatingOriginShiftFrame
                    <= FloatingOriginSuppressionFrameWindow)
                return false;

            double expected = double.IsNaN(expectedMotionMeters)
                    || double.IsInfinity(expectedMotionMeters)
                ? 0.0
                : Math.Max(0.0, expectedMotionMeters);
            double threshold = Math.Max(
                IconJumpFloorMeters,
                expected * ExpectedMotionMultiplier);
            return dPos > threshold;
        }

        /// <summary>
        /// Pure <c>line-blink</c> anomaly predicate (Tier C). Returns true when
        /// <c>line.active</c> just toggled this frame (<paramref name="toggled"/>)
        /// AND the PREVIOUS toggle for the same ghost happened within
        /// <see cref="LineBlinkFrameWindow"/> frames (<paramref name="currentFrame"/>
        /// - <paramref name="lastToggleFrame"/> &lt;= window). A single steady
        /// transition is not a blink; a toggle out and back within the window is.
        /// The first observed toggle for a pid
        /// (<paramref name="hasLastToggleFrame"/> false) is recorded by the caller
        /// but not reported here. Detectable from the truth read alone, so it is
        /// in the MVP.
        /// </summary>
        internal static bool IsLineBlink(
            bool toggled,
            bool hasLastToggleFrame,
            int lastToggleFrame,
            int currentFrame)
        {
            if (!toggled)
                return false;
            if (!hasLastToggleFrame)
                return false;
            int sinceLast = currentFrame - lastToggleFrame;
            return sinceLast >= 0 && sinceLast <= LineBlinkFrameWindow;
        }

        /// <summary>
        /// Pure <c>icon-off-orbit</c> anomaly predicate (Tier C). Returns true when
        /// the angle between the proto icon's body-relative position and the orbit's
        /// own predicted body-relative position (computed by the caller at the icon's
        /// drive clock <c>effUT = liveUT - loopShift</c>) exceeds
        /// <paramref name="minAngleDeg"/>. The icon and its orbit line share one
        /// <c>OrbitDriver.orbit</c>, so on a correctly placed ghost this angle is ~0;
        /// a large value is the icon sitting off its own drawn line (the looped /
        /// re-aimed rotation defect). A static offset produces no per-frame delta and
        /// keeps <c>line.active</c> true, so neither <see cref="IsIconJump"/> nor
        /// <see cref="IsLineBlink"/> can see it - this predicate is the dedicated
        /// signal. NaN / Infinity (degenerate orbit) returns false.
        /// </summary>
        internal static bool IsIconOffOrbit(double angleDeg, double minAngleDeg)
        {
            if (double.IsNaN(angleDeg) || double.IsInfinity(angleDeg))
                return false;
            return angleDeg > minAngleDeg;
        }

        /// <summary>
        /// Pure gate predicate, shaped like
        /// <see cref="GhostRenderTrace.EvaluateGateForTesting"/>. Decides
        /// whether a frame emits and whether the line is important (routed to
        /// <see cref="ParsekLog.Info"/>). Reason strings:
        /// <c>force</c> / <c>important</c> / <c>initial-window</c> /
        /// <c>window</c> / <c>closed</c>.
        ///
        /// <para>Second-cut scaffolding: the MVP emit paths
        /// (<see cref="EmitStructural"/> / <see cref="EmitOnChange"/> /
        /// <see cref="EmitWindowSnapshot"/> / <see cref="EmitAnomaly"/>) do their
        /// own gating, so this predicate is currently exercised only by tests; it
        /// lands in production with the decision-layer / reconciliation second
        /// cut. Same for the <c>FormatVector3</c> / <c>FormatQuaternion</c> /
        /// <c>ShortId</c> / <c>Bool</c> formatters below.</para>
        /// </summary>
        internal static GateDecision EvaluateGate(
            double currentUT,
            double firstSeenUT,
            bool firstSeen,
            bool important,
            bool force,
            bool windowOpen)
        {
            if (force)
                return Decision(true, true, "force");
            if (important)
                return Decision(true, true, "important");
            if (firstSeen || currentUT - firstSeenUT <= InitialWindowSeconds)
                return Decision(true, false, "initial-window");
            if (windowOpen)
                return Decision(true, false, "window");
            return Decision(false, false, "closed");
        }

        /// <summary>
        /// Builds a single <c>phase= surface= ... key=value</c> trace line and
        /// routes important lines to <see cref="ParsekLog.Info"/> and the rest
        /// to <see cref="ParsekLog.Verbose"/> under the single subsystem tag
        /// <c>MapRenderTrace</c>.
        /// </summary>
        internal static void EmitRaw(
            bool important,
            string phase,
            RenderSurface surface,
            string pidKey,
            double currentUT,
            double effUT,
            string details)
        {
            if (!IsEnabled)
                return;

            string message = BuildPrefix(phase, surface, pidKey, currentUT, effUT, CurrentFrameCount())
                + (string.IsNullOrEmpty(details) ? string.Empty : " " + details);
            if (important)
                ParsekLog.Info(Tag, message);
            else
                ParsekLog.Verbose(Tag, message);
        }

        /// <summary>
        /// Tier-B change-based truth emit: one <c>phase= surface= ...</c> Verbose
        /// line for a field whose value just changed for <paramref name="pidKey"/>.
        ///
        /// <para>Change detection is owned by the CALLER (<see cref="MapRenderProbe"/>
        /// tracks each field's previous value per pid locally and only calls this
        /// when the field actually changed, and clears that per-pid state on scene
        /// switch). This deliberately routes straight to <see cref="EmitRaw"/>
        /// (Verbose) and does NOT re-gate through
        /// <see cref="ParsekLog.VerboseOnChange"/>: that second on-change layer
        /// keyed an identity dict that is not cleared on scene transition, so on a
        /// tracking-station &lt;-&gt; flight re-entry it suppressed the first
        /// post-switch transition (persistentId is craft-baked, so the pre- and
        /// post-switch values usually match). The probe's local dict, cleared on
        /// scene switch, is the single source of on-change truth. <see cref="EmitRaw"/>
        /// early-returns when disabled.</para>
        /// </summary>
        internal static void EmitOnChange(
            string phase,
            RenderSurface surface,
            string pidKey,
            double currentUT,
            double effUT,
            string details)
        {
            EmitRaw(false, phase, surface, pidKey, currentUT, effUT, details);
        }

        /// <summary>
        /// In-window full per-frame snapshot (Tier-B detail). Emits one ungated
        /// (by on-change) Verbose <c>phase=Snapshot</c> line carrying the caller's
        /// full current truth, but ONLY while a detailed window is open for the
        /// pid (a window is opened by a structural event or an anomaly). Outside a
        /// window this is a no-op, so steady state is not spammed; inside a window
        /// the surrounding frames capture continuous motion, not just transitions
        /// (the design doc's "full per-frame snapshot line" promise). Gated by
        /// <see cref="IsEnabled"/>. Callers should still guard the
        /// <paramref name="details"/> string build with
        /// <see cref="IsDetailedWindowOpen"/> so a closed-window frame pays no
        /// formatting cost.
        /// </summary>
        internal static void EmitWindowSnapshot(
            RenderSurface surface,
            string pidKey,
            double currentUT,
            double effUT,
            string details)
        {
            if (!IsEnabled)
                return;
            if (!IsDetailedWindowOpen(pidKey, currentUT))
                return;
            EmitRaw(false, "Snapshot", surface, pidKey, currentUT, effUT, details);
        }

        /// <summary>
        /// Tier-A structural-event emit (always emitted when enabled; routed to
        /// <see cref="ParsekLog.Info"/> as important). Opens a detailed window of
        /// <paramref name="windowSeconds"/> for the pid so the surrounding frames
        /// get full per-frame detail, then emits one
        /// <c>phase=&lt;phase&gt; surface= ... &lt;details&gt;</c> line. Early-returns
        /// when disabled so call sites pass only values already in scope and never
        /// pay a formatting cost in normal play (mirrors the
        /// <see cref="GhostRenderTrace"/> emitters). <paramref name="details"/> is
        /// pre-built by the caller (e.g. via <see cref="BuildLifecycleDetails"/>).
        /// </summary>
        internal static void EmitStructural(
            string phase,
            RenderSurface surface,
            string pidKey,
            double currentUT,
            double effUT,
            double windowSeconds,
            string details)
        {
            if (!IsEnabled)
                return;

            if (windowSeconds > 0.0)
                OpenDetailedWindow(pidKey, currentUT, windowSeconds, phase);

            EmitRaw(true, phase, surface, pidKey, currentUT, effUT, details);
        }

        /// <summary>
        /// Pure builder for the <c>vessel= body= scene=</c>[+ world position] detail
        /// tail of a Tier-A lifecycle line (<c>GhostCreated</c> / <c>GhostDestroyed</c>).
        /// Kept pure (no Unity reads) so the structural-event detail schema is
        /// unit-testable. <paramref name="worldPos"/> is omitted when null (the
        /// destroy path may have no last-known world position).
        /// </summary>
        internal static string BuildLifecycleDetails(
            string vesselName,
            string bodyName,
            string scene,
            Vector3d? worldPos,
            string reason)
        {
            string s = "vessel=" + Token(vesselName)
                + " body=" + Token(bodyName)
                + " scene=" + Token(scene);
            if (worldPos.HasValue)
                s += " worldPos=" + FormatVector3d(worldPos.Value);
            if (!string.IsNullOrEmpty(reason))
                s += " reason=" + Token(reason);
            return s;
        }

        /// <summary>
        /// Pure builder for the <c>worldPos= body= sma= ecc=</c> detail tail of the
        /// Tier-A <c>FirstPosition</c> line (the probe-derived MVP variant: the
        /// ghost's first end-of-frame truth read for a pid). Kept pure so the
        /// schema is unit-testable.
        /// </summary>
        internal static string BuildFirstPositionDetails(
            Vector3d worldPos,
            string bodyName,
            double sma,
            double ecc,
            string reason)
        {
            string s = "worldPos=" + FormatVector3d(worldPos)
                + " body=" + Token(bodyName)
                + " sma=" + FormatDouble(sma, "F0")
                + " ecc=" + FormatDouble(ecc, "F4");
            if (!string.IsNullOrEmpty(reason))
                s += " reason=" + Token(reason);
            return s;
        }

        /// <summary>
        /// Tier-C anomaly emit: routes an important <c>phase=Anomaly</c> line
        /// (carrying <c>reason=</c> + caller details) to
        /// <see cref="ParsekLog.Info"/> via <see cref="EmitRaw"/> and opens an
        /// anomaly detailed window for the pid so the surrounding frames capture
        /// full detail. The caller (the probe) soft-rate-limits per pid+reason
        /// so a runaway hyperbola fling cannot flood the log. Gated by
        /// <see cref="IsEnabled"/>.
        /// </summary>
        internal static void EmitAnomaly(
            RenderSurface surface,
            string pidKey,
            double currentUT,
            double effUT,
            string reason,
            string details)
        {
            if (!IsEnabled)
                return;

            OpenDetailedWindow(pidKey, currentUT, AnomalyWindowSeconds, reason);

            string combined = "reason=" + Token(reason)
                + (string.IsNullOrEmpty(details) ? string.Empty : " " + details);
            EmitRaw(true, "Anomaly", surface, pidKey, currentUT, effUT, combined);
        }

        /// <summary>IMGUI marker-surface decision emit (<c>ImguiLabeledMarker</c> /
        /// <c>AtmosphericMarker</c>). These surfaces draw in OnGUI - AFTER the end-of-frame probe -
        /// so they are decision-only: there is no separate end-of-frame truth read to reconcile (the
        /// marker is blitted at exactly the world position the code computed, so the decision IS the
        /// draw). Keyed by the marker's identity (a recordingId on these surfaces, carried in the
        /// prefix <c>pid=</c> slot). Rate-limited per (surface, key) so a per-marker line does not
        /// flood. Gated by <see cref="IsEnabled"/>.</summary>
        internal static void EmitMarker(
            RenderSurface surface, string key, double currentUT, string details,
            double minIntervalSeconds = 2.0)
        {
            if (!IsEnabled)
                return;
            string message = BuildPrefix("MarkerDraw", surface, key, currentUT, currentUT, CurrentFrameCount())
                + (string.IsNullOrEmpty(details) ? string.Empty : " " + details);
            ParsekLog.VerboseRateLimited(
                Tag, "marker-" + RenderSurfaceToken(surface) + "-" + Token(key), message, minIntervalSeconds);
        }

        // ---- Decision-vs-truth reconciliation (second cut) ----
        //
        // GhostOrbitLinePatch is the authoritative per-render-frame decision for a ghost's orbit
        // line + drawIcons. It records the INTENDED state here (frame-stamped); the end-of-frame
        // MapRenderProbe (execution order 10000, same frame) reads the ACTUAL rendered state and
        // reconciles - but ONLY when the intent was stamped on the same frame (within
        // IntentFreshnessFrames). A same-frame mismatch means KSP or another patch toggled
        // line.active / drawIcons AFTER our Postfix decided it (the blink / post-decision-mutation
        // case the probe exists to catch). Stale intent (e.g. a frame on which KSP skipped
        // OrbitRendererBase.LateUpdate, so no decision ran) is dropped, never flagged - exactly the
        // "our decision log goes silent" gap the prototype could not distinguish.

        /// <summary>Max Unity-frame gap between a recorded decision intent and the probe's truth read
        /// for the two to be reconciled. 0 = same Unity frame only. The ONLY caller of
        /// <see cref="RecordLineIntent"/> is GhostOrbitLinePatch's per-render-frame LateUpdate Postfix,
        /// which runs in the SAME frame as the order-10000 probe LateUpdate (delta 0). Allowing &gt;0
        /// would reconcile a STALE intent against a LATER frame whose decision was made by a grace-defer
        /// branch that does NOT re-record intent (it returns without LogOrbitLineDecision), producing a
        /// spurious drawIcons-changed-after-decision for a change the patch itself made legitimately. If
        /// a per-physics-step decision site is ever wired into RecordLineIntent, revisit this.</summary>
        internal const int IntentFreshnessFrames = 0;

        /// <summary>A decision hook's intended orbit-line / drawIcons state for a ghost, stamped with
        /// the Unity frame it was decided on.</summary>
        internal struct LineRenderIntent
        {
            public int Frame;
            public bool LineActive;
            public string DrawIcons;
            public string Reason;
        }

        private static readonly Dictionary<string, LineRenderIntent> lineIntentByPid =
            new Dictionary<string, LineRenderIntent>(StringComparer.Ordinal);

        /// <summary>Record the authoritative orbit-line decision for a pid this frame (called from
        /// GhostOrbitLinePatch). Keyed by pid; stamped with the current Unity frame. No-op when
        /// disabled.</summary>
        internal static void RecordLineIntent(uint pid, bool lineActive, string drawIcons, string reason)
        {
            if (!IsEnabled)
                return;
            lineIntentByPid[pid.ToString(CultureInfo.InvariantCulture)] = new LineRenderIntent
            {
                Frame = CurrentFrameCount(),
                LineActive = lineActive,
                DrawIcons = drawIcons,
                Reason = reason
            };
        }

        /// <summary>True when a line decision intent for <paramref name="pidKey"/> was stamped within
        /// <see cref="IntentFreshnessFrames"/> of <paramref name="currentFrame"/> (so it is safe to
        /// reconcile against this frame's truth read). Stale intent is dropped.</summary>
        internal static bool TryGetFreshLineIntent(
            string pidKey, int currentFrame, out LineRenderIntent intent)
        {
            if (lineIntentByPid.TryGetValue(pidKey, out intent)
                && Math.Abs(currentFrame - intent.Frame) <= IntentFreshnessFrames)
                return true;
            intent = default(LineRenderIntent);
            return false;
        }

        // ---- New-pipeline render-intent store (Phase 6 reconciler) ----
        //
        // The NEW map/TS render pipeline (Parsek.MapRender) emits one first-class GhostRenderIntent
        // per ghost instance per frame, BEFORE the surfaces draw. In decision-only shadow (Phase 4)
        // the scene writes nothing to the stock surfaces; instead it records the intent's primitives
        // here (frame-stamped) and the end-of-frame MapRenderProbe reconciles the recorded intent
        // against the OLD path's rendered truth (Parsek.MapRender.GhostRenderReconciler). This store
        // holds only PRIMITIVES (no MapRender-namespace dependency in this file), mirroring
        // lineIntentByPid above; the Treatment is carried as a token string ("StockConic" /
        // "TracedPath" / "None"). Freshness reuses IntentFreshnessFrames (same-frame only).

        /// <summary>The new pipeline's intended render decision for a ghost this frame, stamped with
        /// the Unity frame it was decided on. Primitives only.</summary>
        internal struct RenderIntentRecord
        {
            public int Frame;
            public bool Visible;
            public string TreatmentToken;
            public double DriveUT;
        }

        private static readonly Dictionary<string, RenderIntentRecord> renderIntentByPid =
            new Dictionary<string, RenderIntentRecord>(StringComparer.Ordinal);

        /// <summary>Record the new pipeline's render intent for a pid this frame (called from the
        /// shadow scene wiring via GhostRenderReconciler.NoteIntent). Keyed by pid; stamped with the
        /// current Unity frame. No-op when disabled.</summary>
        internal static void RecordRenderIntent(uint pid, bool visible, string treatmentToken, double driveUT)
        {
            if (!IsEnabled)
                return;
            renderIntentByPid[pid.ToString(CultureInfo.InvariantCulture)] = new RenderIntentRecord
            {
                Frame = CurrentFrameCount(),
                Visible = visible,
                TreatmentToken = treatmentToken,
                DriveUT = driveUT
            };
        }

        /// <summary>True when a render intent for <paramref name="pidKey"/> was stamped within
        /// <see cref="IntentFreshnessFrames"/> of <paramref name="currentFrame"/> (safe to reconcile
        /// against this frame's old-path truth). Stale intent is dropped.</summary>
        internal static bool TryGetFreshRenderIntent(
            string pidKey, int currentFrame, out RenderIntentRecord intent)
        {
            if (renderIntentByPid.TryGetValue(pidKey, out intent)
                && Math.Abs(currentFrame - intent.Frame) <= IntentFreshnessFrames)
                return true;
            intent = default(RenderIntentRecord);
            return false;
        }

        /// <summary>Pure reconciliation: compare a decision hook's intended line/icon state against the
        /// probe's actual end-of-frame read. Returns a space-joined mismatch-token string, or empty
        /// when consistent. An "unknown" actual token (null/empty, or a "(...)" sentinel such as
        /// "(field-missing)" while the OrbitLine reflection is unfixed, or "(no-renderer)") is treated
        /// as NO SIGNAL and skipped, so each field's check no-ops until real truth is available.</summary>
        internal static string ReconcileLineState(
            LineRenderIntent intent, string actualLineActive, string actualDrawIcons)
        {
            string mismatch = null;
            bool? actualLine = ParseTriBool(actualLineActive);
            if (actualLine.HasValue && actualLine.Value != intent.LineActive)
                mismatch = AppendToken(mismatch,
                    "line-toggled-after-decision(intended=" + Bool(intent.LineActive)
                    + ",actual=" + Bool(actualLine.Value) + ")");
            if (!string.IsNullOrEmpty(intent.DrawIcons)
                && !IsUnknownToken(actualDrawIcons)
                && intent.DrawIcons != actualDrawIcons)
                mismatch = AppendToken(mismatch,
                    "drawIcons-changed-after-decision(intended=" + intent.DrawIcons
                    + ",actual=" + actualDrawIcons + ")");
            return mismatch ?? string.Empty;
        }

        /// <summary>Pure: a ghost's proto orbit line + icon must NOT draw while the trajectory polyline
        /// owns this recording's current non-orbital leg (they would overlap - the double-draw the
        /// polyline-owns branch in GhostOrbitLinePatch exists to prevent). Given whether the polyline
        /// owns the phase + the actual rendered line/icon tokens, returns a mismatch-reason string
        /// (empty => no overlap). This is a higher-level invariant check independent of what the patch
        /// intended, so it catches a proto draw leaking through during polyline ownership for any
        /// reason. Unknown tokens are skipped (the line facet stays dormant until real line.active
        /// truth exists; the drawIcons facet is live now).</summary>
        internal static string ReconcilePolylineOverlap(
            bool polylineOwns, string actualLineActive, string actualDrawIcons)
        {
            if (!polylineOwns)
                return string.Empty;
            string mismatch = null;
            bool? line = ParseTriBool(actualLineActive);
            if (line.HasValue && line.Value)
                mismatch = AppendToken(mismatch, "orbit-line-active-while-polyline-owns");
            if (!IsUnknownToken(actualDrawIcons) && actualDrawIcons != "NONE")
                mismatch = AppendToken(mismatch,
                    "proto-icon-shown-while-polyline-owns(drawIcons=" + actualDrawIcons + ")");
            return mismatch ?? string.Empty;
        }

        // "True"/"False" (bool.ToString) parse to the bool; any other token (e.g. "(field-missing)")
        // is unknown -> null, so the line check is skipped until real line.active truth exists.
        private static bool? ParseTriBool(string s)
        {
            if (s == "True") return true;
            if (s == "False") return false;
            return null;
        }

        // Unknown / no-signal actual token: null/empty, or a parenthesized sentinel like
        // "(field-missing)" / "(no-renderer)" / "(line-null)".
        private static bool IsUnknownToken(string s)
        {
            return string.IsNullOrEmpty(s) || s[0] == '(';
        }

        private static string AppendToken(string acc, string token)
        {
            return string.IsNullOrEmpty(acc) ? token : acc + " " + token;
        }

        private static string BuildPrefix(
            string phase,
            RenderSurface surface,
            string pidKey,
            double currentUT,
            double effUT,
            int frame)
        {
            return "phase=" + Token(phase)
                + " surface=" + RenderSurfaceToken(surface)
                + " pid=" + Token(pidKey)
                + " frame=" + frame.ToString(CultureInfo.InvariantCulture)
                + " currentUT=" + FormatDouble(currentUT, "F3")
                + " effUT=" + FormatDouble(effUT, "F3");
        }

        internal static string FormatTracePrefixForTesting(
            string phase,
            RenderSurface surface,
            string pidKey,
            double currentUT,
            double effUT)
        {
            return BuildPrefix(phase, surface, pidKey, currentUT, effUT, frame: 0);
        }

        private static GateDecision Decision(bool emit, bool important, string reason)
        {
            return new GateDecision
            {
                Emit = emit,
                Important = important,
                Reason = reason
            };
        }

        // ---- Self-contained formatters, reproducing GhostRenderTrace output
        // exactly so both tracers share one key=value schema. Kept private and
        // independent (the shared-formatter extraction is a deferred second cut;
        // this file must not touch GhostRenderTrace). ----

        internal static string FormatVector3d(Vector3d value)
        {
            return "("
                + FormatDouble(value.x, "F2") + ","
                + FormatDouble(value.y, "F2") + ","
                + FormatDouble(value.z, "F2") + ")";
        }

        internal static string FormatVector3(Vector3 value)
        {
            return "("
                + value.x.ToString("F2", CultureInfo.InvariantCulture) + ","
                + value.y.ToString("F2", CultureInfo.InvariantCulture) + ","
                + value.z.ToString("F2", CultureInfo.InvariantCulture) + ")";
        }

        internal static string FormatQuaternion(Quaternion value)
        {
            return "("
                + value.x.ToString("F4", CultureInfo.InvariantCulture) + ","
                + value.y.ToString("F4", CultureInfo.InvariantCulture) + ","
                + value.z.ToString("F4", CultureInfo.InvariantCulture) + ","
                + value.w.ToString("F4", CultureInfo.InvariantCulture) + ")";
        }

        internal static string FormatDouble(double value, string format)
        {
            if (double.IsNaN(value))
                return "NaN";
            if (double.IsPositiveInfinity(value))
                return "Infinity";
            if (double.IsNegativeInfinity(value))
                return "-Infinity";
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        internal static string ShortId(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "<none>";
            return value.Length > 8 ? value.Substring(0, 8) : value;
        }

        internal static string Token(string value)
        {
            return string.IsNullOrEmpty(value) ? "<none>" : value.Replace(' ', '_');
        }

        internal static string Bool(bool value)
        {
            return value ? "true" : "false";
        }
    }
}
