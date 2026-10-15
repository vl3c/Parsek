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
    /// What the plausibility bound knows about one converter module, read off
    /// the live part by the recorder
    /// (<see cref="RouteHarvestCapture.ComputePlausibleRates"/>).
    /// </summary>
    internal enum HarvestConverterKind
    {
        /// <summary>A converter the bound does not model: the window is unbounded.</summary>
        Unmodeled = 0,
        /// <summary>A surface or ocean drill: rate = abundance x efficiency x multiplier.</summary>
        Drill = 1,
        /// <summary>A recipe converter (ISRU, fuel cell): rate = output ratio x multiplier.</summary>
        Recipe = 2,
        /// <summary>A converter that produces no resource (the science lab).</summary>
        NoResourceOutput = 3,
    }

    /// <summary>One converter's inputs to the plausibility bound.</summary>
    internal struct HarvestConverterRate
    {
        public HarvestConverterKind Kind;
        /// <summary>Drill: the harvested resource name.</summary>
        public string ResourceName;
        /// <summary>Drill: the module's Efficiency field.</summary>
        public double Efficiency;
        /// <summary>Drill: the resource abundance at the vessel's location (0..1).</summary>
        public double Abundance;
        /// <summary>Recipe: output resource name to units/s at full efficiency.</summary>
        public List<KeyValuePair<string, double>> Outputs;
        /// <summary>
        /// The module's efficiency multiplier without its heat throttle (which
        /// only ever lowers the rate): EfficiencyBonus x crew bonus.
        /// </summary>
        public double Multiplier;
    }

    /// <summary>
    /// One resource whose recorded harvest exceeded what the drills could
    /// produce (HARVEST-ROUTE-PLAUSIBILITY-CAP): <see cref="Recorded"/> is the
    /// witnessed gain, <see cref="Cap"/> the plausible amount credited instead.
    /// </summary>
    internal struct HarvestCapEntry
    {
        public string Resource;
        public double Recorded;
        public double Cap;
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
        /// Rails gate for the per-frame poll (plan D4). The poll reads a LIVE
        /// resource manifest at its transitions, so it may only run on an
        /// UNPACKED frame: a packed vessel's resource state is whatever the
        /// last stock catch-up left, and a window opened or closed there
        /// brackets an interval nothing witnessed. The vessel's own
        /// <c>packed</c> flag is the authoritative rails state for this gate -
        /// the recorder's <c>isOnRails</c> bookkeeping is NOT a substitute,
        /// because a LANDED / SPLASHED / PRELAUNCH (or below-atmosphere) vessel
        /// packs without it ever being set.
        /// </summary>
        internal static bool ShouldRunHarvestPoll(bool vesselPacked)
        {
            return !vesselPacked;
        }

        /// <summary>
        /// Funnel-consume rule (plan D4 warp rule): the rails-exit pending flag
        /// is consumed ONLY by a poll that actually emits a transition. A
        /// no-transition poll must leave it armed - otherwise the first
        /// steady-state frame after the rails exit burns the label and the
        /// close that the warp-period toggle earned is attributed
        /// <c>trigger=toggle</c> instead of <c>trigger=rails-exit</c>.
        /// </summary>
        internal static bool ShouldConsumeRailsExitFunnel(HarvestActivityTransition transition)
        {
            return transition != HarvestActivityTransition.None;
        }

        /// <summary>
        /// How long after the rails boundary a transition may still be labelled
        /// <c>trigger=rails-exit</c>, in GAME seconds. The funnel flag deliberately
        /// survives no-transition polls (so a close landing a few frames after unpack
        /// still reads as the boundary's), but left unbounded it would equally label a
        /// player toggle minutes later - asserting a causal link to a warp exit that is
        /// not there. A few seconds covers the settle-and-close a real rails exit
        /// produces and excludes anything a player did afterwards.
        /// </summary>
        internal const double RailsExitLabelMaxAgeSeconds = 5.0;

        /// <summary>
        /// Whether an armed rails-exit label still describes the transition being
        /// emitted. False when nothing is armed, when the arm carries no UT (NaN -
        /// never stamped), when the clock ran backwards past the arm (a quickload or
        /// rewind moved UT), or when more than
        /// <see cref="RailsExitLabelMaxAgeSeconds"/> of game time has elapsed.
        /// </summary>
        internal static bool IsRailsExitLabelStillValid(
            bool railsExitPending, double armedUT, double nowUT)
        {
            if (!railsExitPending) return false;
            if (double.IsNaN(armedUT) || double.IsNaN(nowUT)) return false;
            double age = nowUT - armedUT;
            if (age < 0.0) return false;
            return age <= RailsExitLabelMaxAgeSeconds;
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

        /// <summary>
        /// Margin over drill rate x duration (owner ruling 2026-10-06, "with a
        /// margin"): covers what the bound does not model - efficiency modifiers
        /// other mods add, a rover moving onto richer ground inside one window,
        /// crew changes between the samples. A catch-up burst is hours of
        /// production landing in seconds, so it stays far outside the margin.
        /// </summary>
        internal const double PlausibilityMargin = 1.5;

        /// <summary>
        /// Per-resource plausible production rate (units/s) of a converter set,
        /// summed over EVERY converter whether running or not (any of them can
        /// be switched on inside the window). Null when any converter is
        /// <see cref="HarvestConverterKind.Unmodeled"/> or carries an unusable
        /// figure (NaN, negative, infinite): the bound then does not apply and
        /// the window's harvest is credited uncapped, which is today's behavior.
        /// An empty dictionary is a real answer: nothing here produces a
        /// routable resource. Pure.
        /// </summary>
        internal static Dictionary<string, double> ComputePlausibleRates(
            IList<HarvestConverterRate> converters)
        {
            var rates = new Dictionary<string, double>(System.StringComparer.Ordinal);
            if (converters == null)
                return rates;

            for (int i = 0; i < converters.Count; i++)
            {
                HarvestConverterRate c = converters[i];
                if (!IsUsableFactor(c.Multiplier))
                    return null;
                switch (c.Kind)
                {
                    case HarvestConverterKind.NoResourceOutput:
                        break;
                    case HarvestConverterKind.Drill:
                        if (string.IsNullOrEmpty(c.ResourceName)
                            || !IsUsableFactor(c.Efficiency) || !IsUsableFactor(c.Abundance))
                            return null;
                        AddRate(rates, c.ResourceName, c.Abundance * c.Efficiency * c.Multiplier);
                        break;
                    case HarvestConverterKind.Recipe:
                        if (c.Outputs == null)
                            break;
                        for (int o = 0; o < c.Outputs.Count; o++)
                        {
                            KeyValuePair<string, double> output = c.Outputs[o];
                            if (string.IsNullOrEmpty(output.Key) || !IsUsableFactor(output.Value))
                                return null;
                            AddRate(rates, output.Key, output.Value * c.Multiplier);
                        }
                        break;
                    default:
                        return null;
                }
            }
            return rates;
        }

        /// <summary>
        /// Combines two rate samples of one window (open and close) by taking the
        /// larger rate per resource, so a crew bonus that rose inside the window
        /// still counts. Null (unbounded) when either sample is null. Pure.
        /// </summary>
        internal static Dictionary<string, double> MergePlausibleRates(
            Dictionary<string, double> a, Dictionary<string, double> b)
        {
            if (a == null || b == null)
                return null;
            var merged = new Dictionary<string, double>(a, System.StringComparer.Ordinal);
            foreach (KeyValuePair<string, double> kvp in b)
            {
                if (!merged.TryGetValue(kvp.Key, out double existing) || kvp.Value > existing)
                    merged[kvp.Key] = kvp.Value;
            }
            return merged;
        }

        /// <summary>
        /// The most <paramref name="resource"/> the converter set could plausibly
        /// produce over <paramref name="seconds"/>: rate x seconds x
        /// <see cref="PlausibilityMargin"/>. A resource with no rate entry in a
        /// bounded set has no producer, so its limit is 0. Returns
        /// <see cref="double.PositiveInfinity"/> when <paramref name="rates"/> is
        /// null (unbounded). Pure.
        /// </summary>
        internal static double ComputePlausibleLimit(
            Dictionary<string, double> rates, string resource, double seconds)
        {
            if (rates == null)
                return double.PositiveInfinity;
            if (double.IsNaN(seconds) || seconds <= 0.0)
                return 0.0;
            if (resource == null || !rates.TryGetValue(resource, out double rate))
                return 0.0;
            return rate * seconds * PlausibilityMargin;
        }

        /// <summary>
        /// The window's harvested manifest
        /// (<see cref="ComputeWindowHarvestedManifest"/>) with each resource
        /// limited to <see cref="ComputePlausibleLimit"/> over the window's own
        /// duration. A window without <see cref="RouteHarvestWindow.PlausibleRates"/>
        /// returns the uncapped manifest. Pure.
        /// </summary>
        internal static Dictionary<string, double> ComputeWindowPlausibleHarvest(
            RouteHarvestWindow window)
        {
            Dictionary<string, double> harvested = ComputeWindowHarvestedManifest(window);
            if (window == null || window.PlausibleRates == null || harvested.Count == 0)
                return harvested;

            double seconds = window.EndUT - window.StartUT;
            var names = new List<string>(harvested.Keys);
            for (int i = 0; i < names.Count; i++)
            {
                double limit = ComputePlausibleLimit(window.PlausibleRates, names[i], seconds);
                if (harvested[names[i]] > limit)
                    harvested[names[i]] = limit;
            }
            return harvested;
        }

        private static bool IsUsableFactor(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0.0;
        }

        private static void AddRate(Dictionary<string, double> rates, string name, double rate)
        {
            rates.TryGetValue(name, out double existing);
            rates[name] = existing + rate;
        }
    }
}
