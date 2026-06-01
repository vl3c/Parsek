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
        // Window-length constants mirror GhostRenderTrace's window model.
        internal const double InitialWindowSeconds = 4.0;
        internal const double SegmentChangeWindowSeconds = 2.0;
        internal const double SectionChangeWindowSeconds = 2.0;
        internal const double AnomalyWindowSeconds = 5.0;
        internal const double DestroyWindowSeconds = 1.0;

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

        private static bool IsEnabled =>
            ForceEnabledForTesting
            || (ParsekSettings.Current != null && ParsekSettings.Current.mapRenderTracing);

        internal static void Reset()
        {
            detailedUntilByKey.Clear();
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

        /// <summary>
        /// Pure gate predicate, shaped like
        /// <see cref="GhostRenderTrace.EvaluateGateForTesting"/>. Decides
        /// whether a frame emits and whether the line is important (routed to
        /// <see cref="ParsekLog.Info"/>). Reason strings:
        /// <c>force</c> / <c>important</c> / <c>initial-window</c> /
        /// <c>window</c> / <c>closed</c>.
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
                ParsekLog.Info("MapRenderTrace", message);
            else
                ParsekLog.Verbose("MapRenderTrace", message);
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
