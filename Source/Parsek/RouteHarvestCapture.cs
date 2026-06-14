using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;

namespace Parsek
{
    /// <summary>
    /// Decision returned by <see cref="RouteHarvestCapture.EvaluateTransition"/>
    /// for one harvest-activity poll.
    /// </summary>
    internal enum HarvestActivityTransition
    {
        None = 0,
        Open = 1,
        Close = 2,
    }

    /// <summary>
    /// Pure / static harvest-window lifecycle (M2 / plan D4), sibling of
    /// <see cref="RouteProofCapture"/>. A window opens when the recorded
    /// transport's converter set (any <c>BaseConverter</c>-derived module)
    /// becomes ACTIVE and closes when it becomes IDLE - threshold crossings of
    /// player actions, inherently debounced, never per-frame data. The
    /// recorder (<see cref="FlightRecorder"/>) owns the converter cache, the
    /// live-vessel manifest extraction at transitions, and all log emission;
    /// everything here is directly unit-testable.
    ///
    /// At most ONE window is open at a time per recording leg. Special
    /// transitions:
    /// - recording start with converters already active -> open with
    ///   <see cref="RouteHarvestWindow.OpenedAtRecordingStart"/> (the stock
    ///   load-time catch-up burst lands inside the open window, plan D5);
    /// - recording stop -> close with
    ///   <see cref="RouteHarvestWindow.ClosedAtRecordingStop"/>;
    /// - rails entry with converters active and no window open -> open at the
    ///   rails boundary (warp production stays witnessed, plan D4 warp rule);
    /// - a close performed by an abandoned chain-boundary stop is UNWOUND by
    ///   <see cref="ReopenWindow"/> (the #287 terminal-event precedent).
    /// </summary>
    internal static class RouteHarvestCapture
    {
        /// <summary>
        /// Threshold-crossing decision for the per-frame poll. Pure.
        /// </summary>
        internal static HarvestActivityTransition EvaluateTransition(
            bool anyConverterActive,
            bool windowOpen)
        {
            if (anyConverterActive && !windowOpen)
                return HarvestActivityTransition.Open;
            if (!anyConverterActive && windowOpen)
                return HarvestActivityTransition.Close;
            return HarvestActivityTransition.None;
        }

        /// <summary>
        /// Rails-entry re-baseline rule (plan D4 warp rule): with a window
        /// already open, production continues inside it (no action); with
        /// converters active and NO window open (activation raced the poll),
        /// a window must open AT the rails boundary so warp-period production
        /// is witnessed.
        /// </summary>
        internal static bool ShouldOpenWindowAtRailsEntry(
            bool anyConverterActive,
            bool windowOpen)
        {
            return anyConverterActive && !windowOpen;
        }

        internal static RouteHarvestWindow OpenWindow(
            double ut,
            Dictionary<string, ResourceAmount> startTransportResources,
            string bodyName,
            double latitude,
            double longitude,
            double altitude,
            int situationAtOpen,
            List<string> activeConverters,
            bool atRecordingStart)
        {
            return new RouteHarvestWindow
            {
                WindowId = "harvest-" + ut.ToString("R", CultureInfo.InvariantCulture),
                StartUT = ut,
                OpenedAtRecordingStart = atRecordingStart,
                StartTransportResources = startTransportResources,
                ActiveConverters = activeConverters,
                BodyName = bodyName,
                Latitude = latitude,
                Longitude = longitude,
                Altitude = altitude,
                SituationAtOpen = situationAtOpen
            };
        }

        internal static void CloseWindow(
            RouteHarvestWindow window,
            double ut,
            Dictionary<string, ResourceAmount> endTransportResources,
            bool atRecordingStop)
        {
            if (window == null)
                return;

            window.EndUT = ut;
            window.EndTransportResources = endTransportResources;
            window.ClosedAtRecordingStop = atRecordingStop;
        }

        /// <summary>
        /// Unwinds a close performed by an abandoned chain-boundary stop
        /// (plan D4 stop/false-alarm rule, mirroring the #287
        /// RemoveLastEmittedTerminals unwind): clears the end UT, the
        /// close-at-stop flag, and the end manifest so subsequent drilling
        /// stays accounted inside the same window.
        /// </summary>
        internal static void ReopenWindow(RouteHarvestWindow window)
        {
            if (window == null)
                return;

            window.EndUT = double.NaN;
            window.EndTransportResources = null;
            window.ClosedAtRecordingStop = false;
        }

        /// <summary>
        /// The harvested manifest of one window: per-resource POSITIVE delta
        /// (end minus start), routable names only - always-ignored
        /// (EC/IntakeAir) and undefined names are excluded here because this
        /// is an ADMISSION-direction output (plan D2; an undefined-name
        /// positive gain still counts as UNACCOUNTED in the Phase 4 gain
        /// check, which reads the raw window manifests, not this helper).
        /// Returns an empty dictionary (never null) for an open window or a
        /// window with no positive routable deltas - an activated-but-stalled
        /// drill nets 0 harmlessly.
        /// </summary>
        internal static Dictionary<string, double> ComputeWindowHarvestedManifest(
            RouteHarvestWindow window)
        {
            var harvested = new Dictionary<string, double>();
            if (window == null || window.EndTransportResources == null)
                return harvested;

            foreach (KeyValuePair<string, ResourceAmount> kvp in window.EndTransportResources)
            {
                if (!ResourceTransferability.IsRoutableResource(kvp.Key, out _))
                    continue;

                double startAmount = 0.0;
                if (window.StartTransportResources != null
                    && window.StartTransportResources.TryGetValue(kvp.Key, out ResourceAmount startRa))
                {
                    startAmount = startRa.amount;
                }

                double delta = kvp.Value.amount - startAmount;
                if (delta > 0.0)
                    harvested[kvp.Key] = delta;
            }

            return harvested;
        }
    }
}
