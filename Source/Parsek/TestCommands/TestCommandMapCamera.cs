using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>The parsed, validated <c>MapCamera</c> request.</summary>
    internal sealed class MapCameraRequest
    {
        /// <summary>The canonical body name the focus resolved to.</summary>
        internal string Focus;

        /// <summary>Requested pitch in degrees (90 = straight down, 0 = edge-on), or null to keep
        /// the camera's current pitch.</summary>
        internal double? PitchDeg;

        /// <summary>Requested heading in degrees, or null to keep the current heading.</summary>
        internal double? HeadingDeg;

        /// <summary>Requested camera-to-focus distance in metres, or null to keep the current
        /// distance.</summary>
        internal double? DistanceMetres;
    }

    /// <summary>What one poll of an applied <c>MapCamera</c> concludes.</summary>
    internal enum MapCameraSettleOutcome
    {
        /// <summary>The camera is still moving to the new focus / distance.</summary>
        NotYet,

        /// <summary>The camera sits at the applied distance from its focus: terminal OK.</summary>
        Settled,

        /// <summary>The map closed or the scene left while settling: terminal ERROR.</summary>
        SceneLost,

        /// <summary>The budget expired before the camera settled: terminal ERROR.</summary>
        TimedOut,
    }

    /// <summary>
    /// Pure parse / validate / decision half of the ADDITIVE, automation-only
    /// <c>MapCamera focus=&lt;body&gt; [pitch=&lt;deg&gt;] [heading=&lt;deg&gt;]
    /// [distance=&lt;metres&gt;]</c> seam verb. The applier
    /// (<c>ParsekTestCommandAddon.MapCamera.cs</c>) drives stock's <c>PlanetariumCamera</c>
    /// (<c>SetTarget(MapObject)</c>, <c>camPitch</c> / <c>camHdg</c> in radians,
    /// <c>SetDistance</c> in scaled-space units) and reads back what the camera actually
    /// took. Contract: <c>docs/dev/design-map-geometry-dump.md</c>.
    ///
    /// <para><b>WHY IT EXISTS.</b> A map screenshot and a geometry dump are only comparable
    /// across runs when the camera is in the same place. The player sets the map camera by
    /// dragging; this verb sets it by value so a lane can frame a picture reproducibly.</para>
    ///
    /// <para><b>WHY IT IS TWO-PHASE.</b> Stock applies the request over several frames: the
    /// camera's <c>LateUpdate</c> clamps pitch to <c>[minPitch, maxPitch]</c> and the distance
    /// to the focus body's own minimum (two radii), and it LERPS the camera to the distance
    /// and SMOOTH-DAMPS the pivot to the new focus. An OK on the apply frame would report
    /// values the camera does not hold yet and let a following <c>CaptureScreenshot</c>
    /// photograph the transition. The head is held until the camera sits at its distance
    /// from the focus, and the reported values are the camera's own read-back.</para>
    ///
    /// <para>Values are validated against generous FIXED ranges (refused when outside); the
    /// camera's own limits then clamp, and a clamp is reported (<c>pitchClamped</c>,
    /// <c>distanceClamped</c>), never refused - the limits are stock's, they differ by body
    /// and install, and a lane that asks for 90 degrees and gets 89 still gets a usable
    /// picture.</para>
    /// </summary>
    internal static class TestCommandMapCamera
    {
        internal const string Verb = "MapCamera";

        internal const string FocusKey = "focus";
        internal const string PitchKey = "pitch";
        internal const string HeadingKey = "heading";
        internal const string DistanceKey = "distance";

        /// <summary>REJECTED: no <c>focus=</c> arg. Required: a framing without its anchor is
        /// not reproducible.</summary>
        internal const string FocusArgMissingReason = "mapcamera-focus-arg-missing";

        /// <summary>REJECTED: <c>focus=</c> names no celestial body of this game.</summary>
        internal const string UnknownBodyReason = "mapcamera-unknown-body";

        internal const string PitchInvalidReason = "mapcamera-pitch-invalid";
        internal const string PitchOutOfRangeReason = "mapcamera-pitch-out-of-range";
        internal const string HeadingInvalidReason = "mapcamera-heading-invalid";
        internal const string HeadingOutOfRangeReason = "mapcamera-heading-out-of-range";
        internal const string DistanceInvalidReason = "mapcamera-distance-invalid";
        internal const string DistanceOutOfRangeReason = "mapcamera-distance-out-of-range";

        /// <summary>REJECTED: FLIGHT with the map closed (<c>DumpMapGeometry</c>'s token).</summary>
        internal const string MapViewClosedReason = TestCommandDumpMapGeometry.MapViewClosedReason;

        /// <summary>REJECTED: not a map scene (applier belt behind the dispatcher gate).</summary>
        internal const string NotMapSceneReason = TestCommandDumpMapGeometry.NotMapSceneReason;

        /// <summary>ERROR: <c>PlanetariumCamera.fetch</c> is absent in a map scene.</summary>
        internal const string NoCameraReason = "mapcamera-no-camera";

        /// <summary>ERROR: the body resolved but carries no <c>MapObject</c> to target.</summary>
        internal const string FocusNotTargetableReason = "mapcamera-focus-not-targetable";

        /// <summary>ERROR: stock did not take the focus (the camera's target after the apply is
        /// not the requested body).</summary>
        internal const string FocusNotAppliedReason = "mapcamera-focus-not-applied";

        /// <summary>ERROR: the map closed or the scene left while the camera settled.</summary>
        internal const string SceneLostReason = "mapcamera-scene-lost";

        /// <summary>ERROR: the budget ran out before the camera settled.</summary>
        internal const string NotSettledReason = "mapcamera-not-settled";

        /// <summary>The REJECTED vocabulary, in parse-then-admission order. hlib.MAPCAMERA_REASONS
        /// mirrors it (MapGeometrySeamVerbSourceSyncTests).</summary>
        internal static readonly string[] Reasons =
        {
            "mapcamera-focus-arg-missing",
            "mapcamera-unknown-body",
            "mapcamera-pitch-invalid",
            "mapcamera-pitch-out-of-range",
            "mapcamera-heading-invalid",
            "mapcamera-heading-out-of-range",
            "mapcamera-distance-invalid",
            "mapcamera-distance-out-of-range",
            "map-view-closed",
            "not-in-map-scene",
        };

        /// <summary>The ERROR vocabulary (the verb reached stock's camera). hlib.MAPCAMERA_ERROR_REASONS
        /// mirrors it.</summary>
        internal static readonly string[] ErrorReasons =
        {
            "mapcamera-no-camera",
            "mapcamera-focus-not-targetable",
            "mapcamera-focus-not-applied",
            "mapcamera-scene-lost",
            "mapcamera-not-settled",
        };

        internal const double MinPitchDeg = -90.0;
        internal const double MaxPitchDeg = 90.0;
        internal const double MinHeadingDeg = -360.0;
        internal const double MaxHeadingDeg = 360.0;

        /// <summary>Upper bound on a requested distance (metres, about 67 AU). Far past any
        /// stock camera limit; a larger value is a typo, not a framing.</summary>
        internal const double MaxDistanceMetres = 1e13;

        /// <summary>Relative tolerance of the settle test and of the clamp report.</summary>
        internal const double SettleRelativeTolerance = 1e-3;

        /// <summary>Absolute degree tolerance of the pitch / heading clamp report (the camera
        /// stores a float in radians, so a read-back differs from the request by float noise).</summary>
        internal const double AngleToleranceDeg = 1e-3;

        /// <summary>
        /// Resolves <paramref name="requested"/> against the game's body names: an exact
        /// match first, then a single case-insensitive one. Returns the canonical name or
        /// null.
        /// </summary>
        internal static string ResolveBody(string requested, IEnumerable<string> knownBodies)
        {
            if (string.IsNullOrEmpty(requested) || knownBodies == null)
                return null;
            string ci = null;
            int ciCount = 0;
            foreach (string name in knownBodies)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (string.Equals(name, requested, StringComparison.Ordinal))
                    return name;
                if (string.Equals(name, requested, StringComparison.OrdinalIgnoreCase))
                {
                    if (ci == null || !string.Equals(ci, name, StringComparison.Ordinal))
                        ciCount++;
                    ci = name;
                }
            }
            return ciCount == 1 ? ci : null;
        }

        /// <summary>
        /// Parses and validates the four args. Every arg but <c>focus</c> is optional (null
        /// keeps the camera's current value). Returns false with the refusal reason; the
        /// first failing arg in the order focus, pitch, heading, distance decides it.
        /// </summary>
        internal static bool TryParse(
            string focusRaw, string pitchRaw, string headingRaw, string distanceRaw,
            IEnumerable<string> knownBodies,
            out MapCameraRequest request, out string rejectReason)
        {
            request = null;
            if (focusRaw == null || focusRaw.Trim().Length == 0)
            {
                rejectReason = FocusArgMissingReason;
                return false;
            }
            string focus = ResolveBody(focusRaw.Trim(), knownBodies);
            if (focus == null)
            {
                rejectReason = UnknownBodyReason;
                return false;
            }

            if (!TryParseOptional(pitchRaw, MinPitchDeg, MaxPitchDeg, false,
                    PitchInvalidReason, PitchOutOfRangeReason, out double? pitch, out rejectReason))
                return false;
            if (!TryParseOptional(headingRaw, MinHeadingDeg, MaxHeadingDeg, false,
                    HeadingInvalidReason, HeadingOutOfRangeReason, out double? heading, out rejectReason))
                return false;
            if (!TryParseOptional(distanceRaw, 0.0, MaxDistanceMetres, true,
                    DistanceInvalidReason, DistanceOutOfRangeReason, out double? distance, out rejectReason))
                return false;

            request = new MapCameraRequest
            {
                Focus = focus,
                PitchDeg = pitch,
                HeadingDeg = heading,
                DistanceMetres = distance,
            };
            rejectReason = null;
            return true;
        }

        /// <summary>
        /// Parses one optional invariant-culture number into <c>[min, max]</c> (or
        /// <c>(min, max]</c> when <paramref name="minExclusive"/>). An absent arg is valid and
        /// yields null; an unparsable or non-finite one is <paramref name="invalidReason"/>; a
        /// finite one outside the range is <paramref name="rangeReason"/>.
        /// </summary>
        internal static bool TryParseOptional(
            string raw, double min, double max, bool minExclusive,
            string invalidReason, string rangeReason,
            out double? value, out string rejectReason)
        {
            value = null;
            rejectReason = null;
            if (raw == null)
                return true;
            if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                || double.IsNaN(v) || double.IsInfinity(v))
            {
                rejectReason = invalidReason;
                return false;
            }
            bool belowMin = minExclusive ? v <= min : v < min;
            if (belowMin || v > max)
            {
                rejectReason = rangeReason;
                return false;
            }
            value = v;
            return true;
        }

        /// <summary>Degrees to the radians stock's <c>camPitch</c> / <c>camHdg</c> hold.</summary>
        internal static float DegToRad(double deg) => (float)(deg * Math.PI / 180.0);

        /// <summary>Radians (stock's camera fields) to degrees.</summary>
        internal static double RadToDeg(float rad) => rad * 180.0 / Math.PI;

        /// <summary>Metres to the scaled-space units <c>PlanetariumCamera.SetDistance</c> takes.</summary>
        internal static float MetresToScaled(double metres, double scaleFactor)
            => scaleFactor > 0.0 ? (float)(metres / scaleFactor) : 0f;

        /// <summary>
        /// Whether an applied angle differs from its request by more than float noise. False
        /// when nothing was requested.
        /// </summary>
        internal static bool AngleClamped(double? requestedDeg, double appliedDeg)
            => requestedDeg.HasValue && Math.Abs(appliedDeg - requestedDeg.Value) > AngleToleranceDeg;

        /// <summary>
        /// Whether an applied distance differs from its request by more than the relative
        /// tolerance (stock clamped it). False when nothing was requested.
        /// </summary>
        internal static bool DistanceClamped(double? requestedMetres, double appliedMetres)
        {
            if (!requestedMetres.HasValue)
                return false;
            double req = requestedMetres.Value;
            return Math.Abs(appliedMetres - req) > SettleRelativeTolerance * Math.Max(Math.Abs(req), 1.0);
        }

        /// <summary>Consecutive polls the camera must hold still (relative to its focus) to count
        /// as settled when its world distance cannot be compared with the distance it holds.</summary>
        internal const int StableFramesRequired = 3;

        /// <summary>
        /// One settle poll. The camera is settled, at least one frame after the apply (so the
        /// camera's <c>LateUpdate</c> clamps have run), when its actual distance from the focus
        /// matches the distance it holds within <see cref="SettleRelativeTolerance"/>, OR when
        /// its position relative to the focus has held still for
        /// <see cref="StableFramesRequired"/> polls (the backstop for a focus transform whose
        /// scale makes the two distances incomparable). A lost scene is decided first and a
        /// settled camera before the budget.
        /// </summary>
        internal static MapCameraSettleOutcome DecideSettle(
            bool sceneStillMap, int framesSinceApply, double actualDistanceScaled,
            double heldDistanceScaled, int stableFrames, bool budgetExpired)
        {
            if (!sceneStillMap)
                return MapCameraSettleOutcome.SceneLost;
            if (framesSinceApply >= 1
                && (IsAtDistance(actualDistanceScaled, heldDistanceScaled)
                    || stableFrames >= StableFramesRequired))
                return MapCameraSettleOutcome.Settled;
            if (budgetExpired)
                return MapCameraSettleOutcome.TimedOut;
            return MapCameraSettleOutcome.NotYet;
        }

        /// <summary>
        /// Whether the camera moved by no more than a tenth of the settle tolerance between two
        /// polls (<paramref name="moved"/> in scaled units against the held distance).
        /// </summary>
        internal static bool IsStill(double moved, double held)
        {
            if (double.IsNaN(moved) || double.IsInfinity(moved)
                || double.IsNaN(held) || double.IsInfinity(held) || held <= 0.0)
                return false;
            return moved <= 0.1 * SettleRelativeTolerance * held;
        }

        internal static bool IsAtDistance(double actual, double held)
        {
            if (double.IsNaN(actual) || double.IsInfinity(actual)
                || double.IsNaN(held) || double.IsInfinity(held) || held <= 0.0)
                return false;
            return Math.Abs(actual - held) <= SettleRelativeTolerance * held;
        }

        /// <summary>The log subsystem tag the seam writes under.</summary>
        internal const string LogTag = "TestCommands";

        /// <summary>
        /// Logs a settled camera: the values it holds, what was clamped, and the stock pitch
        /// limits a clamp came from.
        /// </summary>
        internal static void LogSettled(
            string focus, double pitchDeg, double headingDeg, double distanceMetres,
            bool pitchClamped, bool distanceClamped, double minPitchDeg, double maxPitchDeg, int frames)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            ParsekLog.Info(LogTag, "mapcamera ok focus=" + (focus ?? string.Empty)
                + " pitch=" + pitchDeg.ToString("R", ic)
                + " heading=" + headingDeg.ToString("R", ic)
                + " distance=" + distanceMetres.ToString("R", ic)
                + " pitchClamped=" + (pitchClamped ? "true" : "false")
                + " distanceClamped=" + (distanceClamped ? "true" : "false")
                + " minPitch=" + minPitchDeg.ToString("R", ic)
                + " maxPitch=" + maxPitchDeg.ToString("R", ic)
                + " frames=" + frames.ToString(ic));
        }

        /// <summary>The OK payload: the values the camera actually holds, and what was
        /// clamped.</summary>
        internal static List<KeyValuePair<string, string>> BuildPayload(
            string focus, double pitchDeg, double headingDeg, double distanceMetres,
            bool pitchClamped, bool distanceClamped, int frames)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("focus", focus ?? string.Empty),
                new KeyValuePair<string, string>("pitch", pitchDeg.ToString("R", ic)),
                new KeyValuePair<string, string>("heading", headingDeg.ToString("R", ic)),
                new KeyValuePair<string, string>("distance", distanceMetres.ToString("R", ic)),
                new KeyValuePair<string, string>("pitchClamped", pitchClamped ? "true" : "false"),
                new KeyValuePair<string, string>("distanceClamped", distanceClamped ? "true" : "false"),
                new KeyValuePair<string, string>("frames", frames.ToString(ic)),
            };
        }
    }
}
