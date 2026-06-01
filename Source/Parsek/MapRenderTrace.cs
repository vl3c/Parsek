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
        /// from the <c>GhostRenderStateProbe</c> prototype: the heliocentric
        /// coast under high warp moves the icon a few km/frame, while an
        /// SOI-exit teleport in the log was tens of millions of metres, so
        /// 1000 km/frame cleanly separates "real teleport" from "fast warp".
        /// This is now a FLOOR under the orbit-derived expected-motion model
        /// (expected = orbital speed * dt * warpRate) so degenerate /
        /// near-zero-velocity orbits never report a spurious jump while a slow
        /// real teleport on a fast orbit can still exceed the orbit-derived
        /// threshold.
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
        /// the observed per-frame world-position delta <paramref name="dPos"/>
        /// exceeds the threshold AND the frame is not suppressed. The threshold
        /// is the larger of the fixed <see cref="IconJumpFloorMeters"/> floor
        /// and the orbit-derived expected motion (caller computes expected =
        /// orbital speed * unscaledDeltaTime * warpRate) scaled by
        /// <see cref="ExpectedMotionMultiplier"/>, so a degenerate near-zero
        /// orbit falls back to the floor while a slow teleport on a fast orbit
        /// can still trip the orbit-derived threshold. Suppressed on the first
        /// frame after a per-pid state reset (<paramref name="justReset"/>:
        /// there is no trustworthy previous position) and on floating-origin
        /// shift frames (<paramref name="floatingOriginShiftFrame"/> within
        /// <see cref="FloatingOriginSuppressionFrameWindow"/> of
        /// <paramref name="currentFrame"/>), mirroring
        /// <see cref="GhostRenderTrace.IsLargeDeltaSignalSuppressed"/>.
        /// </summary>
        internal static bool IsIconJump(
            double dPos,
            double expectedMotionMeters,
            int currentFrame,
            int floatingOriginShiftFrame,
            bool justReset)
        {
            // No trustworthy previous position right after a per-pid reset
            // (scene transition / ghost-pid rebuild). A stale prevWorldPos
            // would otherwise fire a spurious jump on re-entry.
            if (justReset)
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
