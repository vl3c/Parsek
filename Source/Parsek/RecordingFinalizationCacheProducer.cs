using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    internal static class RecordingFinalizationCacheProducer
    {
        internal const double DefaultRefreshIntervalUT = 5.0;
        private const float MeaningfulThrottleThreshold = 0.01f;

        internal static bool ShouldRefresh(
            double lastRefreshUT,
            double currentUT,
            double intervalUT,
            bool force,
            string previousDigest,
            string currentDigest,
            bool requiresPeriodicRefresh)
        {
            if (force)
                return true;
            if (!IsFinite(currentUT))
                return false;
            if (!IsFinite(lastRefreshUT) || lastRefreshUT == double.MinValue)
                return true;
            if (!string.Equals(previousDigest, currentDigest, StringComparison.Ordinal))
                return true;

            return requiresPeriodicRefresh
                && currentUT - lastRefreshUT >= Math.Max(0.0, intervalUT);
        }

        internal static bool TryBuildFromLiveVessel(
            Recording recording,
            Vessel vessel,
            double refreshUT,
            FinalizationCacheOwner owner,
            string reason,
            bool hasMeaningfulThrust,
            out RecordingFinalizationCache cache)
        {
            string recordingId = recording?.RecordingId;
            uint vesselPid = ResolveVesselPid(recording, vessel);
            cache = CreateBase(recordingId, vesselPid, owner, refreshUT, reason);

            if (vessel == null)
                return Fail(cache, "vessel-null");

            PopulateObservedVesselState(cache, vessel, refreshUT, hasMeaningfulThrust);

            if (TryBuildSurfaceTerminalCache(cache, vessel, refreshUT))
                return true;

            if (vessel.orbit == null || vessel.orbit.referenceBody == null)
            {
                if (cache.LastWasInAtmosphere)
                    return PopulateAtmosphericDeletionCache(cache, refreshUT);

                return Fail(cache, "vessel-orbit-null");
            }

            CelestialBody body = vessel.orbit.referenceBody;
            double cutoffAltitude = body.atmosphere && body.atmosphereDepth > 0.0
                ? body.atmosphereDepth
                : 0.0;

            if (!BallisticExtrapolator.ShouldExtrapolate(
                    vessel.situation,
                    vessel.orbit.eccentricity,
                    vessel.orbit.PeA,
                    cutoffAltitude))
            {
                return PopulateStableOrbitCache(cache, BuildOrbitSegmentFromVessel(vessel, refreshUT), refreshUT,
                    RecordingTree.DetermineTerminalState((int)vessel.situation, vessel));
            }

            Recording context = recording ?? BuildContextRecording(recordingId, vesselPid, refreshUT);
            IncompleteBallisticFinalizationResult result;
            if (!IncompleteBallisticSceneExitFinalizer.TryBuildDefaultFinalizationResult(
                    context,
                    vessel,
                    refreshUT,
                    out result))
            {
                if (cache.LastWasInAtmosphere && (vessel.packed || !vessel.loaded))
                    return PopulateAtmosphericDeletionCache(cache, refreshUT);

                return Fail(cache, "default-finalizer-declined");
            }

            if (!result.terminalState.HasValue || !IsFinite(result.terminalUT))
                return Fail(cache, "default-finalizer-invalid-result");

            cache.Status = FinalizationCacheStatus.Fresh;
            cache.TerminalState = result.terminalState.Value;
            cache.TerminalUT = result.terminalUT;
            cache.TerminalBodyName = !string.IsNullOrEmpty(result.terminalOrbit?.bodyName)
                ? result.terminalOrbit.Value.bodyName
                : cache.LastObservedBodyName;
            cache.TerminalOrbit = result.terminalOrbit;
            cache.TerminalPosition = result.terminalPosition;
            cache.TerrainHeightAtEnd = result.terrainHeightAtEnd;
            cache.PredictedSegments = ClonePredictedSegments(result.appendedOrbitSegments);
            cache.TailStartsAtUT = cache.PredictedSegments.Count > 0
                ? cache.PredictedSegments[0].startUT
                : refreshUT;

            if (!cache.TerminalOrbit.HasValue
                && (cache.TerminalState == TerminalState.Orbiting
                    || cache.TerminalState == TerminalState.SubOrbital)
                && cache.PredictedSegments.Count > 0)
            {
                cache.TerminalOrbit =
                    RecordingFinalizationTerminalOrbit.FromSegment(cache.PredictedSegments[cache.PredictedSegments.Count - 1]);
            }

            ParsekLog.VerboseRateLimited("FinalizerCache", $"refresh.{cache.VesselPersistentId}",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Refresh accepted: owner={0} rec={1} pid={2} reason={3} terminal={4} terminalUT={5:F3} segments={6}",
                    cache.Owner,
                    cache.RecordingId ?? "(pending)",
                    cache.VesselPersistentId,
                    cache.RefreshReason ?? "(none)",
                    cache.TerminalState,
                    cache.TerminalUT,
                    cache.PredictedSegments.Count),
                5.0);
            return true;
        }

        internal static bool TryBuildFromStableOrbitSegment(
            string recordingId,
            uint vesselPid,
            OrbitSegment segment,
            double refreshUT,
            FinalizationCacheOwner owner,
            string reason,
            out RecordingFinalizationCache cache)
        {
            cache = CreateBase(recordingId, vesselPid, owner, refreshUT, reason);
            if (string.IsNullOrEmpty(segment.bodyName) || !IsFinite(segment.semiMajorAxis))
                return Fail(cache, "orbit-segment-invalid");

            cache.LastObservedBodyName = segment.bodyName;
            cache.LastSituation = Vessel.Situations.ORBITING;
            cache.LastObservedOrbitDigest = BuildOrbitSegmentDigest(segment);
            return PopulateStableOrbitCache(cache, segment, refreshUT, TerminalState.Orbiting);
        }

        internal static bool TryBuildAtmosphericDeletionCache(
            string recordingId,
            uint vesselPid,
            string bodyName,
            double observedUT,
            FinalizationCacheOwner owner,
            string reason,
            out RecordingFinalizationCache cache)
        {
            cache = CreateBase(recordingId, vesselPid, owner, observedUT, reason);
            cache.LastObservedBodyName = bodyName;
            cache.LastSituation = Vessel.Situations.FLYING;
            cache.LastWasInAtmosphere = true;
            cache.LastObservedOrbitDigest = BuildStateDigest(bodyName, Vessel.Situations.FLYING, inAtmosphere: true, hasMeaningfulThrust: false);
            return PopulateAtmosphericDeletionCache(cache, observedUT);
        }

        internal static bool HasMeaningfulThrust(
            HashSet<ulong> activeEngineKeys,
            Dictionary<ulong, float> lastThrottle,
            HashSet<ulong> activeRcsKeys,
            Dictionary<ulong, float> lastRcsThrottle)
        {
            if (HasMeaningfulThrottle(activeEngineKeys, lastThrottle))
                return true;

            return HasMeaningfulThrottle(activeRcsKeys, lastRcsThrottle);
        }

        internal static bool IsInAtmosphere(Vessel vessel)
        {
            return vessel?.mainBody != null
                && vessel.mainBody.atmosphere
                && vessel.altitude < vessel.mainBody.atmosphereDepth;
        }

        internal static string BuildVesselDigest(Vessel vessel, bool hasMeaningfulThrust)
        {
            if (vessel == null)
                return "(null)";

            string bodyName = vessel.mainBody?.name
                ?? vessel.orbit?.referenceBody?.name
                ?? "(unknown-body)";
            return BuildStateDigest(bodyName, vessel.situation, IsInAtmosphere(vessel), hasMeaningfulThrust);
        }

        internal static string BuildStateDigest(
            string bodyName,
            Vessel.Situations situation,
            bool inAtmosphere,
            bool hasMeaningfulThrust)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}|sit={1}|atmo={2}|thrust={3}",
                bodyName ?? "(unknown-body)",
                situation,
                inAtmosphere,
                hasMeaningfulThrust);
        }

        internal static bool IsSurfaceTerminalSituation(Vessel.Situations situation)
        {
            return situation == Vessel.Situations.LANDED
                || situation == Vessel.Situations.SPLASHED
                || situation == Vessel.Situations.PRELAUNCH;
        }

        internal static string BuildOrbitSegmentDigest(OrbitSegment segment)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}|sma={1:F1}|ecc={2:F8}|inc={3:F6}|lan={4:F6}|arg={5:F6}|mna={6:F8}|epoch={7:F3}",
                segment.bodyName ?? "(unknown-body)",
                segment.semiMajorAxis,
                segment.eccentricity,
                segment.inclination,
                segment.longitudeOfAscendingNode,
                segment.argumentOfPeriapsis,
                segment.meanAnomalyAtEpoch,
                segment.epoch);
        }

        internal static bool TryTouchObservedTerminalCache(
            RecordingFinalizationCache cache,
            double observedUT,
            string reason,
            string observedDigest)
        {
            if (cache == null
                || cache.Status != FinalizationCacheStatus.Fresh
                || !cache.TerminalState.HasValue
                || !IsFinite(observedUT)
                || (cache.PredictedSegments != null && cache.PredictedSegments.Count > 0)
                || !IsObservedStableTerminal(cache.TerminalState.Value))
            {
                return false;
            }

            cache.CachedAtUT = observedUT;
            cache.CachedAtRealtime = SafeRealtime();
            cache.LastObservedUT = observedUT;
            cache.TailStartsAtUT = observedUT;
            cache.TerminalUT = observedUT;
            cache.RefreshReason = reason;
            if (!string.IsNullOrEmpty(observedDigest))
                cache.LastObservedOrbitDigest = observedDigest;
            return true;
        }

        private static RecordingFinalizationCache CreateBase(
            string recordingId,
            uint vesselPid,
            FinalizationCacheOwner owner,
            double refreshUT,
            string reason)
        {
            return new RecordingFinalizationCache
            {
                RecordingId = recordingId,
                VesselPersistentId = vesselPid,
                Owner = owner,
                Status = FinalizationCacheStatus.Empty,
                CachedAtUT = refreshUT,
                CachedAtRealtime = SafeRealtime(),
                RefreshReason = reason,
                LastObservedUT = refreshUT,
                TailStartsAtUT = refreshUT,
                TerminalUT = double.NaN,
                PredictedSegments = new List<OrbitSegment>()
            };
        }

        private static void PopulateObservedVesselState(
            RecordingFinalizationCache cache,
            Vessel vessel,
            double refreshUT,
            bool hasMeaningfulThrust)
        {
            cache.VesselPersistentId = cache.VesselPersistentId != 0
                ? cache.VesselPersistentId
                : vessel.persistentId;
            cache.LastObservedUT = refreshUT;
            cache.LastObservedBodyName = vessel.mainBody?.name
                ?? vessel.orbit?.referenceBody?.name
                ?? "(unknown-body)";
            cache.LastSituation = vessel.situation;
            cache.LastWasInAtmosphere = IsInAtmosphere(vessel);
            cache.LastHadMeaningfulThrust = hasMeaningfulThrust;
            cache.LastObservedOrbitDigest = BuildVesselDigest(vessel, hasMeaningfulThrust);
        }

        private static bool TryBuildSurfaceTerminalCache(
            RecordingFinalizationCache cache,
            Vessel vessel,
            double refreshUT)
        {
            if (!IsSurfaceTerminalSituation(vessel.situation))
            {
                return false;
            }

            cache.Status = FinalizationCacheStatus.Fresh;
            cache.TerminalState = vessel.situation == Vessel.Situations.SPLASHED
                ? TerminalState.Splashed
                : TerminalState.Landed;
            cache.TerminalUT = refreshUT;
            cache.TerminalBodyName = cache.LastObservedBodyName;
            cache.TerminalPosition = new SurfacePosition
            {
                body = cache.LastObservedBodyName,
                latitude = vessel.latitude,
                longitude = vessel.longitude,
                altitude = vessel.altitude,
                rotation = vessel.srfRelRotation,
                rotationRecorded = true,
                situation = vessel.situation == Vessel.Situations.SPLASHED
                    ? SurfaceSituation.Splashed
                    : SurfaceSituation.Landed
            };
            cache.TerrainHeightAtEnd = vessel.heightFromTerrain;
            cache.TailStartsAtUT = refreshUT;
            return true;
        }

        private static bool PopulateStableOrbitCache(
            RecordingFinalizationCache cache,
            OrbitSegment segment,
            double refreshUT,
            TerminalState terminalState)
        {
            cache.Status = FinalizationCacheStatus.Fresh;
            cache.TerminalState = terminalState;
            cache.TerminalUT = refreshUT;
            cache.TerminalBodyName = segment.bodyName;
            cache.TerminalOrbit = RecordingFinalizationTerminalOrbit.FromSegment(segment);
            cache.TailStartsAtUT = refreshUT;
            cache.PredictedSegments = new List<OrbitSegment>();
            return true;
        }

        private static bool PopulateAtmosphericDeletionCache(
            RecordingFinalizationCache cache,
            double terminalUT)
        {
            cache.Status = FinalizationCacheStatus.Fresh;
            cache.TerminalState = TerminalState.Destroyed;
            cache.TerminalUT = terminalUT;
            cache.TerminalBodyName = cache.LastObservedBodyName;
            cache.TailStartsAtUT = terminalUT;
            cache.PredictedSegments = new List<OrbitSegment>();
            return true;
        }

        private static bool Fail(RecordingFinalizationCache cache, string reason)
        {
            cache.Status = FinalizationCacheStatus.Failed;
            cache.DeclineReason = reason;
            ParsekLog.VerboseRateLimited("FinalizerCache", $"refresh-failed.{cache.VesselPersistentId}",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Refresh declined: owner={0} rec={1} pid={2} reason={3}",
                    cache.Owner,
                    cache.RecordingId ?? "(pending)",
                    cache.VesselPersistentId,
                    reason ?? "(none)"),
                5.0);
            return false;
        }

        private static uint ResolveVesselPid(Recording recording, Vessel vessel)
        {
            if (recording != null && recording.VesselPersistentId != 0)
                return recording.VesselPersistentId;
            return vessel != null ? vessel.persistentId : 0;
        }

        private static Recording BuildContextRecording(string recordingId, uint vesselPid, double endUT)
        {
            return new Recording
            {
                RecordingId = recordingId,
                VesselPersistentId = vesselPid,
                ExplicitEndUT = endUT
            };
        }

        private static OrbitSegment BuildOrbitSegmentFromVessel(Vessel vessel, double startUT)
        {
            Orbit orbit = vessel.orbit;
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = startUT,
                inclination = orbit.inclination,
                eccentricity = orbit.eccentricity,
                semiMajorAxis = orbit.semiMajorAxis,
                longitudeOfAscendingNode = orbit.LAN,
                argumentOfPeriapsis = orbit.argumentOfPeriapsis,
                meanAnomalyAtEpoch = orbit.meanAnomalyAtEpoch,
                epoch = orbit.epoch,
                bodyName = orbit.referenceBody?.name ?? vessel.mainBody?.name ?? "(unknown-body)",
                isPredicted = true
            };
        }

        private static List<OrbitSegment> ClonePredictedSegments(IList<OrbitSegment> source)
        {
            var clone = new List<OrbitSegment>();
            if (source == null)
                return clone;

            for (int i = 0; i < source.Count; i++)
            {
                OrbitSegment segment = source[i];
                segment.isPredicted = true;
                clone.Add(segment);
            }
            return clone;
        }

        private static bool HasMeaningfulThrottle(HashSet<ulong> activeKeys, Dictionary<ulong, float> throttles)
        {
            if (activeKeys == null || activeKeys.Count == 0)
                return false;
            if (throttles == null || throttles.Count == 0)
                return true;

            foreach (ulong key in activeKeys)
            {
                float throttle;
                if (!throttles.TryGetValue(key, out throttle) || throttle > MeaningfulThrottleThreshold)
                    return true;
            }
            return false;
        }

        private static bool IsObservedStableTerminal(TerminalState terminalState)
        {
            return terminalState == TerminalState.Orbiting
                || terminalState == TerminalState.Landed
                || terminalState == TerminalState.Splashed
                || terminalState == TerminalState.Docked;
        }

        private static float SafeRealtime()
        {
            // Cache freshness is UT-based. This field is diagnostic only, so avoid
            // UnityEngine.Time: its ECall throws under the xUnit host before it can
            // be caught reliably.
            return Environment.TickCount / 1000f;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
