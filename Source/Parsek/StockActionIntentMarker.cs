using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Which stock-UI button click armed an intent marker. See
    /// <c>docs/dev/plans/segment-scoped-switch-fly-autorecord.md</c>
    /// (Required Stock-Action Intent Marker).
    /// </summary>
    internal enum StockActionType
    {
        TrackingStationFly = 0,
        KscMarkerFly = 1,
        MapSwitchTo = 2,
    }

    /// <summary>
    /// Which scene was active when the stock-UI button was clicked. The
    /// consume site (in FLIGHT) uses this to distinguish cross-scene
    /// markers (TS Fly / KSC marker Fly survive scene tear-down and
    /// FLIGHT load) from in-scene markers (Map Switch-To arms and
    /// consumes inside the same FLIGHT scene).
    /// </summary>
    internal enum StockActionSourceScene
    {
        TrackingStation = 0,
        SpaceCenter = 1,
        Flight = 2,
    }

    /// <summary>
    /// Classification returned by
    /// <see cref="StockActionIntentMarker.EvaluateStaleness"/>: the OnLoad
    /// tail uses this to decide whether to keep the marker armed or
    /// clear it with a logged reason. Exposed so the predicate can be
    /// driven from unit tests without a live Planetarium / Time clock.
    /// </summary>
    internal enum StockActionIntentStaleness
    {
        /// <summary>Marker is fresh and matches the current process — keep it armed.</summary>
        Fresh = 0,

        /// <summary>
        /// <see cref="StockActionIntentMarker.ProcessSessionId"/> does not match
        /// <see cref="ParsekProcess.ProcessSessionId"/>. The marker was armed in
        /// a different AppDomain; clear with reason <c>stale-cross-run</c>.
        /// </summary>
        StaleCrossRun = 1,

        /// <summary>
        /// Marker exceeded its per-action wall-clock TTL (10 s for TS / KSC
        /// Fly, 2 s for Map Switch-To). Clear with reason <c>stale-intent</c>.
        /// </summary>
        StaleIntentTtlExpired = 2,

        /// <summary>
        /// Planetarium UT regressed since arm time (quickload between arm
        /// and consume). Clear with reason <c>stale-intent</c>.
        /// </summary>
        StaleIntentUtRegressed = 3,
    }

    /// <summary>
    /// Positive intent marker armed only by confirmed stock-UI Fly /
    /// Switch-To button handlers. Consumed in FLIGHT after the focused
    /// vessel arrives to authorize an immediate switch-segment start.
    ///
    /// <para>Lifetime, TTL rules, cross-run-orphan handling, and all
    /// callable sites are defined in
    /// <c>docs/dev/plans/segment-scoped-switch-fly-autorecord.md</c>
    /// (Required Stock-Action Intent Marker). This Phase A.3 type
    /// defines the data shape, serialization, and pure staleness
    /// predicate only — the Harmony patches that arm it (Phase B) and
    /// the scene-load consume site (Phase C) land in later phases.</para>
    /// </summary>
    internal sealed class StockActionIntentMarker
    {
        /// <summary>
        /// Wall-clock TTL for Tracking Station Fly / KSC marker Fly. These
        /// arm in TRACKSTATION / SPACECENTER and consume in FLIGHT after
        /// a scene load (which on slow installs can take several seconds);
        /// 10 s is the plan's documented headroom.
        /// </summary>
        internal const double TrackingStationOrKscFlyTtlSeconds = 10.0;

        /// <summary>
        /// Wall-clock TTL for Map view "Switch To". Consume happens
        /// same-frame in stock; anything longer than 2 s is a stuck
        /// marker (e.g., a mod yielded inside the patched OnSelect).
        /// </summary>
        internal const double MapSwitchToTtlSeconds = 2.0;

        /// <summary>
        /// Allowable UT-regression slack (seconds) before the OnLoad tail
        /// declares the marker stale-on-quickload. Mirrors the slack
        /// other UT-comparison gates in the codebase use to absorb
        /// floating-point drift.
        /// </summary>
        internal const double UtRegressionToleranceSeconds = 1.0;

        /// <summary>Stable GUID for the pending UI action.</summary>
        internal Guid IntentId;

        /// <summary>Which stock UI button fired.</summary>
        internal StockActionType Action;

        /// <summary>Expected target vessel persistentId.</summary>
        internal uint TargetVesselPersistentId;

        /// <summary>Scene the marker was armed in.</summary>
        internal StockActionSourceScene SourceScene;

        /// <summary>
        /// <see cref="UnityEngine.Time.realtimeSinceStartup"/> at arm time.
        /// Compared against the current value at consume time to enforce
        /// <see cref="TrackingStationOrKscFlyTtlSeconds"/> /
        /// <see cref="MapSwitchToTtlSeconds"/>.
        /// </summary>
        internal float CapturedRealtime;

        /// <summary>Planetarium UT at arm time, when available.</summary>
        internal double CapturedUT;

        /// <summary>
        /// Process / AppDomain identity captured at arm time. Compared
        /// against <see cref="ParsekProcess.ProcessSessionId"/> at consume
        /// time to detect cross-run orphans (player saved with marker
        /// armed, quit, loaded on a fresh process).
        /// </summary>
        internal Guid ProcessSessionId;

        internal const string NodeName = "STOCK_ACTION_INTENT_MARKER";

        /// <summary>Writes this marker as a child node on <paramref name="parent"/>.</summary>
        internal void SaveInto(ConfigNode parent)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            var ic = CultureInfo.InvariantCulture;
            ConfigNode node = parent.AddNode(NodeName);
            node.AddValue("intentId", IntentId.ToString("D", ic));
            node.AddValue("action", Action.ToString());
            node.AddValue("targetVesselPersistentId", TargetVesselPersistentId.ToString(ic));
            node.AddValue("sourceScene", SourceScene.ToString());
            node.AddValue("capturedRealtime", CapturedRealtime.ToString("R", ic));
            node.AddValue("capturedUT", CapturedUT.ToString("R", ic));
            node.AddValue("processSessionId", ProcessSessionId.ToString("D", ic));
        }

        /// <summary>
        /// Attempts to load a marker from a node previously written by
        /// <see cref="SaveInto"/>. Returns false when the node lacks the
        /// required fields (legacy / partial data); callers treat that
        /// as "no marker armed".
        /// </summary>
        internal static bool TryLoadFrom(ConfigNode node, out StockActionIntentMarker marker)
        {
            marker = null;
            if (node == null)
                return false;
            var ic = CultureInfo.InvariantCulture;

            string intentIdStr = node.GetValue("intentId");
            string actionStr = node.GetValue("action");
            string pidStr = node.GetValue("targetVesselPersistentId");
            string sceneStr = node.GetValue("sourceScene");
            string realtimeStr = node.GetValue("capturedRealtime");
            string utStr = node.GetValue("capturedUT");
            string processIdStr = node.GetValue("processSessionId");

            Guid intentId;
            if (string.IsNullOrEmpty(intentIdStr)
                || !Guid.TryParseExact(intentIdStr, "D", out intentId))
            {
                return false;
            }

            StockActionType action;
            if (string.IsNullOrEmpty(actionStr)
                || !TryParseEnum(actionStr, out action))
            {
                return false;
            }

            uint targetPid = 0u;
            if (!string.IsNullOrEmpty(pidStr))
                uint.TryParse(pidStr, NumberStyles.Integer, ic, out targetPid);

            StockActionSourceScene sourceScene;
            if (string.IsNullOrEmpty(sceneStr)
                || !TryParseEnum(sceneStr, out sourceScene))
            {
                return false;
            }

            float realtime = 0f;
            if (!string.IsNullOrEmpty(realtimeStr))
                float.TryParse(realtimeStr, NumberStyles.Float, ic, out realtime);

            double ut = 0.0;
            if (!string.IsNullOrEmpty(utStr))
                double.TryParse(utStr, NumberStyles.Float, ic, out ut);

            Guid processSessionId;
            if (string.IsNullOrEmpty(processIdStr)
                || !Guid.TryParseExact(processIdStr, "D", out processSessionId))
            {
                return false;
            }

            marker = new StockActionIntentMarker
            {
                IntentId = intentId,
                Action = action,
                TargetVesselPersistentId = targetPid,
                SourceScene = sourceScene,
                CapturedRealtime = realtime,
                CapturedUT = ut,
                ProcessSessionId = processSessionId,
            };
            return true;
        }

        /// <summary>
        /// Returns the wall-clock TTL applicable to <paramref name="action"/>.
        /// Pure helper exposed for tests.
        /// </summary>
        internal static double GetTtlSeconds(StockActionType action)
        {
            switch (action)
            {
                case StockActionType.TrackingStationFly:
                case StockActionType.KscMarkerFly:
                    return TrackingStationOrKscFlyTtlSeconds;
                case StockActionType.MapSwitchTo:
                    return MapSwitchToTtlSeconds;
                default:
                    // New action enum values must be added explicitly; fall
                    // back to the shorter TTL so an unmapped value cannot
                    // outlive its arm point indefinitely.
                    return MapSwitchToTtlSeconds;
            }
        }

        /// <summary>
        /// Pure staleness predicate. Returns the classification the OnLoad
        /// tail should act on. Exposed as a pure static method so unit
        /// tests can pin every branch without driving the live KSP
        /// clock or Planetarium.
        /// </summary>
        /// <param name="marker">Marker to classify. Must not be null.</param>
        /// <param name="currentProcessSessionId">Current <see cref="ParsekProcess.ProcessSessionId"/>.</param>
        /// <param name="currentRealtime">Current <see cref="UnityEngine.Time.realtimeSinceStartup"/>.</param>
        /// <param name="currentUT">Current Planetarium UT.</param>
        internal static StockActionIntentStaleness EvaluateStaleness(
            StockActionIntentMarker marker,
            Guid currentProcessSessionId,
            float currentRealtime,
            double currentUT)
        {
            if (marker == null) throw new ArgumentNullException(nameof(marker));

            if (marker.ProcessSessionId != currentProcessSessionId)
                return StockActionIntentStaleness.StaleCrossRun;

            double ttl = GetTtlSeconds(marker.Action);
            double elapsed = (double)currentRealtime - (double)marker.CapturedRealtime;
            // Negative elapsed (clock regressed somehow) is treated as
            // fresh — the wall-clock can monotonically increase only;
            // a regression is below noise on a single-process run.
            if (elapsed > ttl)
                return StockActionIntentStaleness.StaleIntentTtlExpired;

            if (currentUT < marker.CapturedUT - UtRegressionToleranceSeconds)
                return StockActionIntentStaleness.StaleIntentUtRegressed;

            return StockActionIntentStaleness.Fresh;
        }

        private static bool TryParseEnum<T>(string text, out T value) where T : struct
        {
            try
            {
                value = (T)Enum.Parse(typeof(T), text, ignoreCase: true);
                return Enum.IsDefined(typeof(T), value);
            }
            catch
            {
                value = default(T);
                return false;
            }
        }
    }
}
