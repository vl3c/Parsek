using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>What one poll of an armed map-geometry capture concludes.</summary>
    internal enum MapGeometryPollOutcome
    {
        /// <summary>The end-of-frame capture has not run yet: poll again next frame.</summary>
        NotYet,

        /// <summary>The dump for THIS arm was captured and written: terminal OK.</summary>
        Written,

        /// <summary>The capture ran and failed (the map closed or the scene left between the
        /// arm and the capture, a read threw, or the write threw): terminal ERROR naming the
        /// recorder's reason.</summary>
        Failed,

        /// <summary>The verb's deferral budget expired before the capture ran: terminal
        /// ERROR.</summary>
        TimedOut,
    }

    /// <summary>
    /// Pure decision / payload half of the ADDITIVE, automation-only
    /// <c>DumpMapGeometry label=&lt;name&gt;</c> seam verb. The applier
    /// (<c>ParsekTestCommandAddon.DumpMapGeometry.cs</c>) arms <c>MapGeometryRecorder</c> and
    /// polls it; the arg parse, the scene admission, the poll decision and the payload live
    /// here so a spec's <c>expect</c> has xUnit cells behind them. Contract:
    /// <c>docs/dev/design-map-geometry-dump.md</c>.
    ///
    /// <para><b>WHY IT EXISTS.</b> A map-view screenshot shows that a ghost line looks wrong,
    /// never where its vertices are. This verb writes the geometry the map actually drew
    /// in one frame - every ghost's proto icon and orbit line, every Parsek polyline and
    /// forward arc, the route overview lines and the body ring context - as JSON in the
    /// same <c>Screenshots</c> folder <c>CaptureScreenshot</c> writes to, under the same
    /// label grammar, so an offline page can plot it beside the picture.</para>
    ///
    /// <para><b>WHY IT IS TWO-PHASE.</b> The map draws in pieces across a frame: stock orbit
    /// lines in <c>LateUpdate</c>, Parsek's polylines in the map camera's <c>OnPreCull</c>,
    /// Parsek's IMGUI markers in <c>OnGUI</c>. Only a read after all of them is coherent, so
    /// the capture runs at <c>WaitForEndOfFrame</c> of the frame AFTER the arm (the arm
    /// frame's own <c>OnGUI</c> may already have drawn some markers before the marker tap
    /// was on). The head is held until the file is written, so the next step is ordered
    /// after the capture.</para>
    ///
    /// <para><b>NON-MUTATING.</b> The capture reads; the only side effect is the file. It
    /// needs no tracing setting (<c>mapRenderTracing</c> may be off) and draws nothing.</para>
    /// </summary>
    internal static class TestCommandDumpMapGeometry
    {
        /// <summary>No <c>label=</c> arg: <c>CaptureScreenshot</c>'s token, the same rule.</summary>
        internal const string LabelArgMissingReason =
            TestCommandCaptureScreenshot.LabelArgMissingReason;

        /// <summary>The <c>label=</c> value is not filename-safe: <c>CaptureScreenshot</c>'s
        /// token, the same rule, because a picture and its geometry pair under one label.</summary>
        internal const string LabelArgInvalidReason =
            TestCommandCaptureScreenshot.LabelArgInvalidReason;

        /// <summary>REJECTED: FLIGHT with the map view closed. The flight camera shows no map
        /// geometry, so a dump would describe nothing on screen; <c>EnterMapView</c> first.</summary>
        internal const string MapViewClosedReason = "map-view-closed";

        /// <summary>REJECTED: a scene with no map (the dispatcher's scene gate defers those,
        /// so this is the applier's belt for a scene that changed under the dispatch).</summary>
        internal const string NotMapSceneReason = "not-in-map-scene";

        /// <summary>ERROR: the capture ran and failed. The recorder's own reason rides on the
        /// message (<c>reason=&lt;token&gt;</c>).</summary>
        internal const string CaptureFailedReason = "map-geometry-capture-failed";

        /// <summary>The capture-failure reason (on <see cref="CaptureFailedReason"/>'s message)
        /// when the scene changed between the arm and the end-of-frame capture. The map
        /// closing in FLIGHT reports <see cref="MapViewClosedReason"/> there instead, and a
        /// thrown read or write reports <c>threw-&lt;ExceptionType&gt;</c>.</summary>
        internal const string SceneChangedFailure = "scene-changed";

        /// <summary>ERROR: the budget ran out before the end-of-frame capture ran.</summary>
        internal const string TimeoutReason = "map-geometry-timeout";

        /// <summary>The REJECTED vocabulary, in parse-then-admission order.
        /// hlib.DUMPMAPGEOMETRY_REASONS mirrors it (MapGeometrySeamVerbSourceSyncTests).</summary>
        internal static readonly string[] Reasons =
        {
            "label-arg-missing",
            "label-arg-invalid",
            "map-view-closed",
            "not-in-map-scene",
        };

        /// <summary>The ERROR vocabulary (the capture was armed). hlib.DUMPMAPGEOMETRY_ERROR_REASONS
        /// mirrors it.</summary>
        internal static readonly string[] ErrorReasons =
        {
            "map-geometry-capture-failed",
            "map-geometry-timeout",
        };

        /// <summary>The extension every dump is written with, mirroring
        /// <c>MapGeometryRecorder.OutputSuffix</c> (a unit cell pins the two).</summary>
        internal const string DumpSuffix = ".map.json";

        /// <summary>The <c>scene</c> token for FLIGHT.</summary>
        internal const string FlightSceneToken = "FLIGHT";

        /// <summary>The <c>scene</c> token for the Tracking Station.</summary>
        internal const string TrackingStationSceneToken = "TRACKSTATION";

        internal static bool IsValidLabel(string label)
            => TestCommandCaptureScreenshot.IsValidLabel(label);

        /// <summary>Parses the required <c>label=</c> arg (the <c>DumpGuiTree</c> rule).</summary>
        internal static bool TryParseLabel(string raw, out string label, out string rejectReason)
        {
            label = null;
            if (raw == null)
            {
                rejectReason = LabelArgMissingReason;
                return false;
            }
            if (!IsValidLabel(raw))
            {
                rejectReason = LabelArgInvalidReason;
                return false;
            }
            label = raw;
            rejectReason = null;
            return true;
        }

        internal static string FileNameFor(string label)
            => (label ?? string.Empty) + DumpSuffix;

        internal static string RelativePathFor(string label)
            => TestCommandCaptureScreenshot.ScreenshotsDirName + "/" + FileNameFor(label);

        /// <summary>
        /// Whether a dump may be taken in <paramref name="scene"/> with the flight map
        /// <paramref name="mapViewEnabled"/>. Returns null when admitted, else the refusal:
        /// FLIGHT needs the map open (<see cref="MapViewClosedReason"/>); the Tracking Station
        /// is always a map; every other scene is <see cref="NotMapSceneReason"/>.
        /// </summary>
        internal static string DecideAdmission(TestCommandScene scene, bool mapViewEnabled)
        {
            if (scene == TestCommandScene.TrackingStation)
                return null;
            if (scene == TestCommandScene.Flight)
                return mapViewEnabled ? null : MapViewClosedReason;
            return NotMapSceneReason;
        }

        /// <summary>The <c>scene</c> token the dump carries, or null for a non-map scene.</summary>
        internal static string SceneToken(TestCommandScene scene)
        {
            if (scene == TestCommandScene.Flight) return FlightSceneToken;
            if (scene == TestCommandScene.TrackingStation) return TrackingStationSceneToken;
            return null;
        }

        /// <summary>
        /// One poll of the armed capture. A finished capture is decided BEFORE the budget,
        /// so a dump written on the frame the budget expired is still OK (the
        /// <c>CaptureScreenshot.DecidePoll</c> rule).
        /// </summary>
        internal static MapGeometryPollOutcome DecidePoll(
            bool writtenForThisArm, bool failedForThisArm, bool budgetExpired)
        {
            if (failedForThisArm)
                return MapGeometryPollOutcome.Failed;
            if (writtenForThisArm)
                return MapGeometryPollOutcome.Written;
            if (budgetExpired)
                return MapGeometryPollOutcome.TimedOut;
            return MapGeometryPollOutcome.NotYet;
        }

        /// <summary>The log subsystem tag the seam writes under.</summary>
        internal const string LogTag = "TestCommands";

        /// <summary>
        /// Logs a written dump: one Info line with the counts and, when the dump hit a cap,
        /// one Warn naming every dropped total (a cap is never silent in the log either).
        /// </summary>
        internal static void LogWritten(
            string label, string scene, long bytes, int frame, MapGeometryCounts counts, string elapsedText)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            MapGeometryCounts c = counts ?? new MapGeometryCounts();
            ParsekLog.Info(LogTag, "dumpmapgeometry ok label=" + (label ?? string.Empty)
                + " scene=" + (scene ?? string.Empty)
                + " path=" + RelativePathFor(label)
                + " bytes=" + bytes.ToString(ic)
                + " frame=" + frame.ToString(ic)
                + " ghosts=" + c.Ghosts.ToString(ic)
                + " lines=" + c.Lines.ToString(ic)
                + " points=" + c.Points.ToString(ic)
                + " truncated=" + (c.Truncated ? "true" : "false")
                + " unknownBodyItems=" + c.UnknownBodyItems.ToString(ic)
                + " elapsed=" + (elapsedText ?? "?") + "s");
            if (c.Truncated)
            {
                ParsekLog.Warn(LogTag, "dumpmapgeometry truncated label=" + (label ?? string.Empty)
                    + " droppedGhosts=" + c.DroppedGhosts.ToString(ic)
                    + " droppedLines=" + c.DroppedLines.ToString(ic)
                    + " droppedRoutes=" + c.DroppedRoutes.ToString(ic)
                    + " droppedLegs=" + c.DroppedLegs.ToString(ic)
                    + " droppedBodies=" + c.DroppedBodies.ToString(ic));
            }
        }

        /// <summary>The OK payload. <c>bytes</c> is -1 when the written file could not be
        /// stat'ed (a reporting gap, not a missing dump).</summary>
        internal static List<KeyValuePair<string, string>> BuildPayload(
            string label, string scene, long bytes, int frame, int ghosts, int lines, long points,
            bool truncated)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("label", label ?? string.Empty),
                new KeyValuePair<string, string>("path", RelativePathFor(label)),
                new KeyValuePair<string, string>("scene", scene ?? string.Empty),
                new KeyValuePair<string, string>("bytes", bytes.ToString(ic)),
                new KeyValuePair<string, string>("frame", frame.ToString(ic)),
                new KeyValuePair<string, string>("ghosts", ghosts.ToString(ic)),
                new KeyValuePair<string, string>("lines", lines.ToString(ic)),
                new KeyValuePair<string, string>("points", points.ToString(ic)),
                new KeyValuePair<string, string>("truncated", truncated ? "true" : "false"),
            };
        }
    }
}
