using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    // Public accessors for ParsekUI, the watch-mode diagnostics and the pre-cleanup watch exit and
    // camera retarget.
    public partial class ParsekFlight
    {
        public bool IsRecording => recorder?.IsRecording ?? false;
        public bool IsPlaying => isPlaying;
        public GameObject PreviewGhost => ghostObject;
        public Dictionary<int, GameObject> TimelineGhosts
        {
            get
            {
                int currentFrame = Time.frameCount;
                if (cachedTimelineGhostsFrame != currentFrame)
                {
                    cachedTimelineGhosts.Clear();
                    foreach (var kv in ghostStates)
                        cachedTimelineGhosts[kv.Key] = kv.Value.ghost;
                    cachedTimelineGhostsFrame = currentFrame;
                    ParsekLog.VerboseRateLimited("Flight", "timeline-ghosts-cache",
                        $"TimelineGhosts: rebuilt cache, {cachedTimelineGhosts.Count} ghosts (frame {currentFrame})");
                }
                return cachedTimelineGhosts;
            }
        }
        public bool HasActiveTree => activeTree != null;

        // M-C2 EVA seam-verb read-only accessors (design-autotest-eva-missions.md, Data Model
        // / "What Doesn't Change"): two dispatch/completion bits the ParsekTestCommands seam
        // samples. No behavior change; both are pure reads over existing private state.

        /// <summary>True while a deferred split / branch is in progress
        /// (<c>pendingSplitInProgress</c>). The ParsekTestCommands seam's <c>EvaExit</c> defers
        /// on it so the <c>OnCrewOnEva</c> skip path cannot silently swallow the branch.</summary>
        internal bool StructuralSplitPending => pendingSplitInProgress;

        /// <summary>True when Parsek's board-merge machinery is quiescent: no
        /// <c>ChainToVesselPending</c> recorder flag and no <c>pendingBoardingTargetPid</c>. The
        /// seam's <c>EvaBoard</c> completion holds until this reads true so a next FIFO command
        /// can never land inside the board-merge window (F2). On a no-tree run neither token is
        /// ever armed, so the bit reads true and is inert.</summary>
        internal bool BoardMergeQuiescent =>
            !(recorder != null && recorder.ChainToVesselPending) && pendingBoardingTargetPid == 0;

        // Camera follow (watch mode) — forwarded from WatchModeController
        internal bool IsWatchingGhost => watchMode.IsWatchingGhost;
        internal int WatchedRecordingIndexForDiagnostics => watchMode != null
            ? watchMode.WatchedRecordingIndex
            : -1;
        internal long WatchedLoopCycleIndexForDiagnostics => watchMode != null
            ? watchMode.WatchedLoopCycleIndex
            : -1;
        internal int WatchedRecordingIndex => watchMode.WatchedRecordingIndex;
        internal string DescribeWatchFocusForLogs() => watchMode != null
            ? watchMode.DescribeWatchFocusForLogs()
            : "watch=uninitialized";
        internal string DescribeWatchEligibilityForLogs(int index) => watchMode != null
            ? watchMode.DescribeWatchEligibilityForLogs(index)
            : $"watchEval(rec=#{index} unavailable=watchMode-null)";
        internal bool ExitWatchModeBeforeTimelineGhostCleanup(string context)
        {
            bool isWatchingGhost = watchMode != null && watchMode.IsWatchingGhost;
            string focus = watchMode != null
                ? watchMode.DescribeWatchFocusForLogs()
                : "watch=uninitialized";
            return ExitWatchModeBeforeTimelineGhostCleanup(
                isWatchingGhost,
                watchMode != null
                    ? new Action<bool>(skipCameraRestore => watchMode.ExitWatchMode(skipCameraRestore))
                    : null,
                isWatchingGhost
                    ? new Action(() => RetargetStockCamerasBeforeTimelineGhostCleanup(context, focus))
                    : null,
                context,
                focus);
        }

        internal static bool ExitWatchModeBeforeTimelineGhostCleanup(
            bool isWatchingGhost,
            Action exitWatchMode,
            Action detachStockCameraTarget,
            string context,
            string watchFocusForLogs = null)
        {
            return ExitWatchModeBeforeTimelineGhostCleanup(
                isWatchingGhost,
                exitWatchMode != null
                    ? new Action<bool>(_ => exitWatchMode())
                    : null,
                detachStockCameraTarget,
                context,
                watchFocusForLogs);
        }

        internal static bool ExitWatchModeBeforeTimelineGhostCleanup(
            bool isWatchingGhost,
            Action<bool> exitWatchMode,
            Action detachStockCameraTarget,
            string context,
            string watchFocusForLogs = null)
        {
            string cleanupContext = string.IsNullOrEmpty(context)
                ? "timeline-ghost-cleanup"
                : context;
            string focus = string.IsNullOrEmpty(watchFocusForLogs)
                ? "watch=unknown"
                : watchFocusForLogs;

            if (!isWatchingGhost)
            {
                ParsekLog.Verbose("CameraFollow",
                    $"Watch cleanup guard: no active watch mode before {cleanupContext} ({focus})");
                return false;
            }

            if (exitWatchMode == null)
            {
                ParsekLog.Warn("CameraFollow",
                    $"Watch cleanup guard: active watch mode could not be exited before {cleanupContext} ({focus})");
                return false;
            }

            ParsekLog.Info("CameraFollow",
                $"Exiting watch mode before timeline ghost cleanup: context={cleanupContext} skipCameraRestore=true {focus}");
            detachStockCameraTarget?.Invoke();
            exitWatchMode(true);
            return true;
        }

        private void RetargetStockCamerasBeforeTimelineGhostCleanup(
            string context,
            string watchFocusForLogs)
        {
            string cleanupContext = string.IsNullOrEmpty(context)
                ? "timeline-ghost-cleanup"
                : context;
            string focus = string.IsNullOrEmpty(watchFocusForLogs)
                ? "watch=unknown"
                : watchFocusForLogs;
            FlightCamera flightCamera = FlightCamera.fetch;
            Vessel activeVessel = FlightGlobals.ActiveVessel;

            if (flightCamera != null
                && activeVessel != null
                && activeVessel.gameObject != null)
            {
                flightCamera.SetTargetVessel(activeVessel);
                ParsekLog.Verbose("CameraFollow",
                    $"Watch cleanup guard: FlightCamera retargeted to active vessel '{activeVessel.vesselName}' before {cleanupContext} ({focus})");
            }
            else
            {
                ParsekLog.Warn("CameraFollow",
                    $"Watch cleanup guard: could not retarget FlightCamera before {cleanupContext} ({focus}) " +
                    $"camera={(flightCamera != null)} activeVessel={(activeVessel != null)} " +
                    $"activeVesselGo={(activeVessel != null && activeVessel.gameObject != null)}");
            }

            if (!MapView.MapIsEnabled)
                return;

            if (PlanetariumCamera.fetch != null
                && activeVessel != null
                && activeVessel.mapObject != null)
            {
                PlanetariumCamera.fetch.SetTarget(activeVessel.mapObject);
                ParsekLog.Verbose("GhostMap",
                    $"Watch cleanup guard: PlanetariumCamera retargeted to active vessel '{activeVessel.vesselName}' before {cleanupContext}");
                return;
            }

            ParsekLog.Verbose("GhostMap",
                $"Watch cleanup guard: skipped PlanetariumCamera retarget before {cleanupContext} " +
                $"map={MapView.MapIsEnabled} camera={(PlanetariumCamera.fetch != null)} " +
                $"activeVessel={(activeVessel != null)} mapObject={(activeVessel != null && activeVessel.mapObject != null)}");
        }
    }
}
