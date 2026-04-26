using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    internal struct RecordingFinalizationOrbitView
    {
        public string ReferenceBodyName;
        public bool ReferenceBodyHasAtmosphere;
        public double ReferenceBodyAtmosphereDepth;
        public double Inclination;
        public double Eccentricity;
        public double SemiMajorAxis;
        public double LongitudeOfAscendingNode;
        public double ArgumentOfPeriapsis;
        public double MeanAnomalyAtEpoch;
        public double Epoch;
        public double PeriapsisAltitude;
    }

    internal interface IRecordingFinalizationVesselView
    {
        uint PersistentId { get; }
        Vessel.Situations Situation { get; }
        string BodyName { get; }
        bool IsInAtmosphere { get; }
        bool IsPacked { get; }
        bool IsLoaded { get; }
        double Latitude { get; }
        double Longitude { get; }
        double Altitude { get; }
        double HeightFromTerrain { get; }
        Quaternion SurfaceRelativeRotation { get; }
        Vessel RawVessel { get; }
        bool TryGetOrbit(out RecordingFinalizationOrbitView orbit);
    }

    internal static class RecordingFinalizationCacheProducer
    {
        internal const double DefaultRefreshIntervalUT = 5.0;
        internal const string AlreadyClassifiedDestroyedDeclineReason = "already-classified-destroyed";
        private const float MeaningfulThrottleThreshold = 0.01f;

        internal delegate bool TryBuildDefaultFinalizationResultDelegate(
            Recording recording,
            Vessel vessel,
            double refreshUT,
            out IncompleteBallisticFinalizationResult result);

        internal static TryBuildDefaultFinalizationResultDelegate TryBuildDefaultFinalizationResultOverrideForTesting;

        internal static void ResetForTesting()
        {
            TryBuildDefaultFinalizationResultOverrideForTesting = null;
            ResetRefreshSummaryDedupForTesting();
        }

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
            return TryBuildFromLiveVessel(
                recording,
                vessel != null ? new VesselFinalizationView(vessel) : null,
                refreshUT,
                owner,
                reason,
                hasMeaningfulThrust,
                out cache);
        }

        internal static bool TryBuildFromLiveVessel(
            Recording recording,
            IRecordingFinalizationVesselView vessel,
            double refreshUT,
            FinalizationCacheOwner owner,
            string reason,
            bool hasMeaningfulThrust,
            out RecordingFinalizationCache cache)
        {
            string recordingId = recording?.RecordingId;
            uint vesselPid = ResolveVesselPid(recording, vessel);
            cache = CreateBase(recordingId, vesselPid, owner, refreshUT, reason);
            int recordingsExamined = recording != null ? 1 : 0;

            if (vessel == null)
                return CompleteRefresh(cache, Fail(cache, "vessel-null"), recordingsExamined, 0, 0);

            PopulateObservedVesselState(cache, vessel, refreshUT, hasMeaningfulThrust);
            RecordingFinalizationCache alreadyDestroyedSkip;
            if (TryBuildAlreadyClassifiedDestroyedSkip(
                    recording,
                    cache.VesselPersistentId,
                    owner,
                    refreshUT,
                    reason,
                    out alreadyDestroyedSkip))
            {
                cache = alreadyDestroyedSkip;
                PopulateObservedVesselState(cache, vessel, refreshUT, hasMeaningfulThrust);
                return CompleteRefresh(cache, false, recordingsExamined, 1, 0);
            }

            if (TryBuildSurfaceTerminalCache(cache, vessel, refreshUT))
                return CompleteRefresh(cache, true, recordingsExamined, 0, 0);

            RecordingFinalizationOrbitView orbit;
            if (!vessel.TryGetOrbit(out orbit))
            {
                if (cache.LastWasInAtmosphere)
                    return CompleteRefresh(cache, PopulateAtmosphericDeletionCache(cache, refreshUT), recordingsExamined, 0, 0);

                return CompleteRefresh(cache, Fail(cache, "vessel-orbit-null"), recordingsExamined, 0, 0);
            }

            double cutoffAltitude = orbit.ReferenceBodyHasAtmosphere && orbit.ReferenceBodyAtmosphereDepth > 0.0
                ? orbit.ReferenceBodyAtmosphereDepth
                : 0.0;

            if (!BallisticExtrapolator.ShouldExtrapolate(
                    vessel.Situation,
                    orbit.Eccentricity,
                    orbit.PeriapsisAltitude,
                    cutoffAltitude))
            {
                return CompleteRefresh(
                    cache,
                    PopulateStableOrbitCache(cache, BuildOrbitSegmentFromVessel(vessel, orbit, refreshUT), refreshUT,
                        DetermineObservedTerminalState(vessel)),
                    recordingsExamined,
                    0,
                    0);
            }

            Recording context = recording ?? BuildContextRecording(recordingId, vesselPid, refreshUT);
            IncompleteBallisticFinalizationResult result;
            if (!TryBuildDefaultFinalizationResult(
                    context,
                    vessel.RawVessel,
                    refreshUT,
                    out result))
            {
                if (cache.LastWasInAtmosphere && (vessel.IsPacked || !vessel.IsLoaded))
                    return CompleteRefresh(cache, PopulateAtmosphericDeletionCache(cache, refreshUT), recordingsExamined, 0, 0);

                return CompleteRefresh(cache, Fail(cache, "default-finalizer-declined"), recordingsExamined, 0, 0);
            }

            if (!result.terminalState.HasValue || !IsFinite(result.terminalUT))
                return CompleteRefresh(cache, Fail(cache, "default-finalizer-invalid-result"), recordingsExamined, 0, 0);

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

            int newlyClassified = result.extrapolationFailureReason == ExtrapolationFailureReason.SubSurfaceStart ? 1 : 0;
            if (newlyClassified > 0)
            {
                IncompleteBallisticSceneExitFinalizer.LogSubSurfaceDestroyedClassificationOnce(
                    recordingId,
                    result.terminalUT,
                    result.subSurfaceDestroyedBodyName,
                    result.subSurfaceDestroyedAltitude,
                    result.subSurfaceDestroyedThreshold);
            }

            return CompleteRefresh(cache, Accept(cache), recordingsExamined, 0, newlyClassified);
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
            cache.LastObservedOrbitDigest = BuildOrbitSegmentDigest(segment);
            int recordingsExamined = string.IsNullOrEmpty(recordingId) ? 0 : 1;
            if (string.IsNullOrEmpty(segment.bodyName) || !IsFinite(segment.semiMajorAxis))
                return CompleteRefresh(cache, Fail(cache, "orbit-segment-invalid"), recordingsExamined, 0, 0);

            cache.LastObservedBodyName = segment.bodyName;
            cache.LastSituation = Vessel.Situations.ORBITING;
            return CompleteRefresh(
                cache,
                PopulateStableOrbitCache(cache, segment, refreshUT, TerminalState.Orbiting),
                recordingsExamined,
                0,
                0);
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
            int recordingsExamined = string.IsNullOrEmpty(recordingId) ? 0 : 1;
            cache.LastObservedBodyName = bodyName;
            cache.LastSituation = Vessel.Situations.FLYING;
            cache.LastWasInAtmosphere = true;
            cache.LastObservedOrbitDigest = BuildStateDigest(bodyName, Vessel.Situations.FLYING, inAtmosphere: true, hasMeaningfulThrust: false);
            return CompleteRefresh(
                cache,
                PopulateAtmosphericDeletionCache(cache, observedUT),
                recordingsExamined,
                0,
                0);
        }

        internal static bool TryBuildAlreadyClassifiedDestroyedSkip(
            Recording recording,
            uint vesselPid,
            FinalizationCacheOwner owner,
            double refreshUT,
            string reason,
            out RecordingFinalizationCache cache)
        {
            cache = null;
            if (!IncompleteBallisticSceneExitFinalizer.IsAlreadyClassifiedDestroyed(recording))
                return false;

            string recordingId = recording?.RecordingId;
            cache = CreateBase(recordingId, vesselPid, owner, refreshUT, reason);
            cache.Status = FinalizationCacheStatus.Failed;
            cache.DeclineReason = AlreadyClassifiedDestroyedDeclineReason;
            cache.TerminalState = TerminalState.Destroyed;
            cache.TerminalUT = ResolveDestroyedTerminalUT(recording, refreshUT);
            cache.TerminalBodyName = ResolveDestroyedTerminalBodyName(recording);
            cache.TailStartsAtUT = cache.TerminalUT;
            cache.PredictedSegments = new List<OrbitSegment>();

            IncompleteBallisticSceneExitFinalizer.LogAlreadyClassifiedDestroyedSkip(
                recording,
                "FinalizerCache",
                reason);
            return true;
        }

        internal static bool IsAlreadyClassifiedDestroyedSkip(RecordingFinalizationCache cache)
        {
            return cache != null
                && cache.Status == FinalizationCacheStatus.Failed
                && string.Equals(
                    cache.DeclineReason,
                    AlreadyClassifiedDestroyedDeclineReason,
                    StringComparison.Ordinal);
        }

        private static readonly HashSet<string> emittedMaterialRefreshSummaryKeys =
            new HashSet<string>(StringComparer.Ordinal);

        internal static void LogRefreshSummary(
            FinalizationCacheOwner owner,
            string reason,
            int recordingsExamined,
            int alreadyClassified,
            int newlyClassified,
            string recordingId = null,
            uint vesselPid = 0,
            FinalizationCacheStatus? status = null,
            TerminalState? terminalState = null,
            string declineReason = null,
            int predictedSegmentCount = -1)
        {
            if (recordingsExamined <= 0 && alreadyClassified <= 0 && newlyClassified <= 0)
                return;

            string message = string.Format(
                CultureInfo.InvariantCulture,
                "FinalizerCache refresh summary: owner={0} reason={1} " +
                "recordingsExamined={2} alreadyClassified={3} newlyClassified={4}{5}",
                owner,
                reason ?? "(none)",
                recordingsExamined,
                alreadyClassified,
                newlyClassified,
                BuildRefreshSummaryContextSuffix(
                    recordingId,
                    vesselPid,
                    status,
                    terminalState,
                    declineReason,
                    predictedSegmentCount));

            string identity = BuildRefreshSummaryIdentity(owner, recordingId, vesselPid);
            string stateKey = BuildRefreshSummaryStateKey(
                reason,
                recordingsExamined,
                alreadyClassified,
                newlyClassified,
                status,
                terminalState,
                declineReason,
                predictedSegmentCount);

            if (newlyClassified > 0)
            {
                string materialKey = identity + "|" + stateKey;
                if (emittedMaterialRefreshSummaryKeys.Add(materialKey))
                {
                    ParsekLog.Info("Extrapolator", message);
                    return;
                }

                ParsekLog.VerboseRateLimited(
                    "Extrapolator",
                    "finalizer-refresh-classified|" + materialKey,
                    message,
                    30.0);
                return;
            }

            ParsekLog.VerboseRateLimited(
                "Extrapolator",
                "finalizer-refresh-stable|" + identity + "|" + stateKey,
                message,
                30.0);
        }

        private static string BuildRefreshSummaryIdentity(
            FinalizationCacheOwner owner,
            string recordingId,
            uint vesselPid)
        {
            if (!string.IsNullOrEmpty(recordingId))
                return string.Format(CultureInfo.InvariantCulture, "{0}|rec={1}", owner, recordingId);
            if (vesselPid != 0)
                return string.Format(CultureInfo.InvariantCulture, "{0}|pid={1}", owner, vesselPid);
            return string.Format(CultureInfo.InvariantCulture, "{0}|aggregate", owner);
        }

        private static string BuildRefreshSummaryStateKey(
            string reason,
            int recordingsExamined,
            int alreadyClassified,
            int newlyClassified,
            FinalizationCacheStatus? status,
            TerminalState? terminalState,
            string declineReason,
            int predictedSegmentCount)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "reason={0}|examined={1}|already={2}|new={3}|status={4}|terminal={5}|decline={6}|segments={7}",
                reason ?? "(none)",
                recordingsExamined,
                alreadyClassified,
                newlyClassified,
                status.HasValue ? status.Value.ToString() : "(null)",
                terminalState.HasValue ? terminalState.Value.ToString() : "(null)",
                declineReason ?? "(none)",
                predictedSegmentCount);
        }

        private static string BuildRefreshSummaryContextSuffix(
            string recordingId,
            uint vesselPid,
            FinalizationCacheStatus? status,
            TerminalState? terminalState,
            string declineReason,
            int predictedSegmentCount)
        {
            bool hasContext = !string.IsNullOrEmpty(recordingId)
                || vesselPid != 0
                || status.HasValue
                || terminalState.HasValue
                || !string.IsNullOrEmpty(declineReason)
                || predictedSegmentCount >= 0;
            if (!hasContext)
                return string.Empty;

            return string.Format(
                CultureInfo.InvariantCulture,
                " rec={0} pid={1} status={2} terminal={3} decline={4} segments={5}",
                recordingId ?? "(pending)",
                vesselPid,
                status.HasValue ? status.Value.ToString() : "(null)",
                terminalState.HasValue ? terminalState.Value.ToString() : "(null)",
                declineReason ?? "(none)",
                predictedSegmentCount);
        }

        internal static void ResetRefreshSummaryDedupForTesting()
        {
            emittedMaterialRefreshSummaryKeys.Clear();
        }

        /// <summary>
        /// Back-compat test hook kept for older test code. Refresh-summary
        /// suppression is now keyed by recording/terminal digest rather than a
        /// global no-delta counter.
        /// </summary>
        internal static void ResetSuppressedNoDeltaCountForTesting()
        {
            ResetRefreshSummaryDedupForTesting();
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

        internal static bool IsPotentiallyApplicableCache(RecordingFinalizationCache cache)
        {
            return cache != null
                && (cache.Status == FinalizationCacheStatus.Fresh
                    || cache.Status == FinalizationCacheStatus.Stale)
                && cache.TerminalState.HasValue
                && IsFinite(cache.TerminalUT);
        }

        internal static bool TryPreservePreviousCacheAfterFailedRefresh(
            RecordingFinalizationCache previous,
            RecordingFinalizationCache failedRefresh,
            double observedUT,
            string reason,
            string observedDigest)
        {
            // Producer failures that escape TryBuild* are expected to flow through
            // Fail(). Empty is only the pre-population state inside a single
            // producer call and should not reach this preservation seam.
            if (!IsPotentiallyApplicableCache(previous)
                || failedRefresh == null
                || failedRefresh.Status != FinalizationCacheStatus.Failed)
            {
                return false;
            }

            previous.Status = FinalizationCacheStatus.Stale;
            previous.CachedAtUT = observedUT;
            previous.CachedAtRealtime = SafeRealtime();
            previous.LastObservedUT = observedUT;
            previous.RefreshReason = reason;
            previous.DeclineReason = failedRefresh.DeclineReason;
            if (!string.IsNullOrEmpty(observedDigest))
                previous.LastObservedOrbitDigest = observedDigest;

            ParsekLog.Warn("FinalizerCache",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Refresh failed; preserving previous cache: owner={0} rec={1} pid={2} " +
                    "reason={3} terminal={4} terminalUT={5:F3}",
                    previous.Owner,
                    previous.RecordingId ?? "(pending)",
                    previous.VesselPersistentId,
                    failedRefresh.DeclineReason ?? "(none)",
                    previous.TerminalState?.ToString() ?? "(null)",
                    previous.TerminalUT));
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

        private static bool CompleteRefresh(
            RecordingFinalizationCache cache,
            bool success,
            int recordingsExamined,
            int alreadyClassified,
            int newlyClassified)
        {
            LogRefreshSummary(
                cache != null ? cache.Owner : FinalizationCacheOwner.Unknown,
                cache?.RefreshReason,
                recordingsExamined,
                alreadyClassified,
                newlyClassified,
                cache?.RecordingId,
                cache != null ? cache.VesselPersistentId : 0,
                cache?.Status,
                cache?.TerminalState,
                cache?.DeclineReason,
                cache?.PredictedSegments?.Count ?? -1);
            return success;
        }

        private static double ResolveDestroyedTerminalUT(Recording recording, double refreshUT)
        {
            if (recording == null)
                return IsFinite(refreshUT) ? refreshUT : double.NaN;
            if (IsFinite(recording.ExplicitEndUT))
                return recording.ExplicitEndUT;
            double endUT = recording.EndUT;
            if (IsFinite(endUT))
                return endUT;
            return IsFinite(refreshUT) ? refreshUT : double.NaN;
        }

        private static string ResolveDestroyedTerminalBodyName(Recording recording)
        {
            if (recording == null)
                return null;
            if (!string.IsNullOrEmpty(recording.TerminalOrbitBody))
                return recording.TerminalOrbitBody;
            if (recording.TerminalPosition.HasValue
                && !string.IsNullOrEmpty(recording.TerminalPosition.Value.body))
                return recording.TerminalPosition.Value.body;
            if (!string.IsNullOrEmpty(recording.EndpointBodyName))
                return recording.EndpointBodyName;
            if (recording.Points != null && recording.Points.Count > 0)
                return recording.Points[recording.Points.Count - 1].bodyName;
            if (recording.OrbitSegments != null && recording.OrbitSegments.Count > 0)
                return recording.OrbitSegments[recording.OrbitSegments.Count - 1].bodyName;
            return null;
        }

        private static void PopulateObservedVesselState(
            RecordingFinalizationCache cache,
            IRecordingFinalizationVesselView vessel,
            double refreshUT,
            bool hasMeaningfulThrust)
        {
            cache.VesselPersistentId = cache.VesselPersistentId != 0
                ? cache.VesselPersistentId
                : vessel.PersistentId;
            cache.LastObservedUT = refreshUT;
            cache.LastObservedBodyName = vessel.BodyName ?? "(unknown-body)";
            cache.LastSituation = vessel.Situation;
            cache.LastWasInAtmosphere = vessel.IsInAtmosphere;
            cache.LastHadMeaningfulThrust = hasMeaningfulThrust;
            cache.LastObservedOrbitDigest = BuildVesselDigest(vessel, hasMeaningfulThrust);
        }

        private static bool TryBuildSurfaceTerminalCache(
            RecordingFinalizationCache cache,
            IRecordingFinalizationVesselView vessel,
            double refreshUT)
        {
            if (!IsSurfaceTerminalSituation(vessel.Situation))
            {
                return false;
            }

            cache.Status = FinalizationCacheStatus.Fresh;
            cache.TerminalState = vessel.Situation == Vessel.Situations.SPLASHED
                ? TerminalState.Splashed
                : TerminalState.Landed;
            cache.TerminalUT = refreshUT;
            cache.TerminalBodyName = cache.LastObservedBodyName;
            cache.TerminalPosition = new SurfacePosition
            {
                body = cache.LastObservedBodyName,
                latitude = vessel.Latitude,
                longitude = vessel.Longitude,
                altitude = vessel.Altitude,
                rotation = vessel.SurfaceRelativeRotation,
                rotationRecorded = true,
                situation = vessel.Situation == Vessel.Situations.SPLASHED
                    ? SurfaceSituation.Splashed
                    : SurfaceSituation.Landed
            };
            cache.TerrainHeightAtEnd = vessel.HeightFromTerrain;
            cache.TailStartsAtUT = refreshUT;
            return Accept(cache);
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
            return Accept(cache);
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
            return Accept(cache);
        }

        private static bool Accept(RecordingFinalizationCache cache)
        {
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
                    cache.PredictedSegments?.Count ?? 0),
                5.0);
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

        private static uint ResolveVesselPid(Recording recording, IRecordingFinalizationVesselView vessel)
        {
            if (recording != null && recording.VesselPersistentId != 0)
                return recording.VesselPersistentId;
            return vessel != null ? vessel.PersistentId : 0;
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

        private static OrbitSegment BuildOrbitSegmentFromVessel(
            IRecordingFinalizationVesselView vessel,
            RecordingFinalizationOrbitView orbit,
            double startUT)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = startUT,
                inclination = orbit.Inclination,
                eccentricity = orbit.Eccentricity,
                semiMajorAxis = orbit.SemiMajorAxis,
                longitudeOfAscendingNode = orbit.LongitudeOfAscendingNode,
                argumentOfPeriapsis = orbit.ArgumentOfPeriapsis,
                meanAnomalyAtEpoch = orbit.MeanAnomalyAtEpoch,
                epoch = orbit.Epoch,
                bodyName = orbit.ReferenceBodyName ?? vessel.BodyName ?? "(unknown-body)",
                isPredicted = true
            };
        }

        private static bool TryBuildDefaultFinalizationResult(
            Recording context,
            Vessel vessel,
            double refreshUT,
            out IncompleteBallisticFinalizationResult result)
        {
            if (TryBuildDefaultFinalizationResultOverrideForTesting != null)
                return TryBuildDefaultFinalizationResultOverrideForTesting(context, vessel, refreshUT, out result);

            if (vessel == null)
            {
                result = default(IncompleteBallisticFinalizationResult);
                return false;
            }

            return IncompleteBallisticSceneExitFinalizer.TryBuildDefaultFinalizationResult(
                context,
                vessel,
                refreshUT,
                out result);
        }

        private static TerminalState DetermineObservedTerminalState(IRecordingFinalizationVesselView vessel)
        {
            if (vessel?.RawVessel != null)
                return RecordingTree.DetermineTerminalState((int)vessel.Situation, vessel.RawVessel);

            if (vessel == null)
                return TerminalState.Orbiting;

            switch (vessel.Situation)
            {
                case Vessel.Situations.LANDED:
                case Vessel.Situations.PRELAUNCH:
                    return TerminalState.Landed;
                case Vessel.Situations.SPLASHED:
                    return TerminalState.Splashed;
                case Vessel.Situations.SUB_ORBITAL:
                    return TerminalState.SubOrbital;
                case Vessel.Situations.DOCKED:
                    return TerminalState.Docked;
                case Vessel.Situations.ORBITING:
                default:
                    return TerminalState.Orbiting;
            }
        }

        private static string BuildVesselDigest(IRecordingFinalizationVesselView vessel, bool hasMeaningfulThrust)
        {
            if (vessel == null)
                return "(null)";

            return BuildStateDigest(vessel.BodyName, vessel.Situation, vessel.IsInAtmosphere, hasMeaningfulThrust);
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

        private sealed class VesselFinalizationView : IRecordingFinalizationVesselView
        {
            private readonly Vessel vessel;

            internal VesselFinalizationView(Vessel vessel)
            {
                this.vessel = vessel;
            }

            public uint PersistentId => vessel.persistentId;
            public Vessel.Situations Situation => vessel.situation;
            public string BodyName => vessel.mainBody?.name
                ?? vessel.orbit?.referenceBody?.name
                ?? "(unknown-body)";
            public bool IsInAtmosphere => RecordingFinalizationCacheProducer.IsInAtmosphere(vessel);
            public bool IsPacked => vessel.packed;
            public bool IsLoaded => vessel.loaded;
            public double Latitude => vessel.latitude;
            public double Longitude => vessel.longitude;
            public double Altitude => vessel.altitude;
            public double HeightFromTerrain => vessel.heightFromTerrain;
            public Quaternion SurfaceRelativeRotation => vessel.srfRelRotation;
            public Vessel RawVessel => vessel;

            public bool TryGetOrbit(out RecordingFinalizationOrbitView orbit)
            {
                orbit = default(RecordingFinalizationOrbitView);
                Orbit source = vessel.orbit;
                CelestialBody referenceBody = source?.referenceBody;
                if (source == null || referenceBody == null)
                    return false;

                orbit = new RecordingFinalizationOrbitView
                {
                    ReferenceBodyName = referenceBody.name,
                    ReferenceBodyHasAtmosphere = referenceBody.atmosphere,
                    ReferenceBodyAtmosphereDepth = referenceBody.atmosphereDepth,
                    Inclination = source.inclination,
                    Eccentricity = source.eccentricity,
                    SemiMajorAxis = source.semiMajorAxis,
                    LongitudeOfAscendingNode = source.LAN,
                    ArgumentOfPeriapsis = source.argumentOfPeriapsis,
                    MeanAnomalyAtEpoch = source.meanAnomalyAtEpoch,
                    Epoch = source.epoch,
                    PeriapsisAltitude = source.PeA
                };
                return true;
            }
        }
    }
}
