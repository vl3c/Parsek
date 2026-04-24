using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    internal enum RecordingFinalizationCacheApplyStatus
    {
        Applied = 0,
        RejectedNullRecording = 1,
        RejectedNullCache = 2,
        RejectedEmpty = 3,
        RejectedStale = 4,
        RejectedFailed = 5,
        RejectedMismatchedRecording = 6,
        RejectedMismatchedVessel = 7,
        RejectedAlreadyFinalized = 8,
        RejectedNoAuthoredData = 9,
        RejectedUnsetTerminalState = 10,
        RejectedInvalidTerminalState = 11,
        RejectedInvalidTerminalUT = 12,
        RejectedTerminalBeforeLastSample = 13
    }

    internal struct RecordingFinalizationCacheApplyOptions
    {
        public string ConsumerPath;
        public bool AllowStale;
        public bool AllowAlreadyFinalizedRepair;
    }

    internal struct RecordingFinalizationCacheApplyResult
    {
        public RecordingFinalizationCacheApplyStatus Status;
        public double LastAuthoredUT;
        public double OldExplicitEndUT;
        public double NewExplicitEndUT;
        public double TerminalUT;
        public TerminalState? TerminalState;
        // Segment counts are populated only for Applied results; rejected results
        // leave them at 0 so callers can log the status without branching.
        public int AppendedSegmentCount;
        public int RemovedPredictedSegmentCount;

        public bool Applied => Status == RecordingFinalizationCacheApplyStatus.Applied;
    }

    internal static class RecordingFinalizationCacheApplier
    {
        private const string LogTag = "FinalizerCache";
        private const double UtEpsilon = 1e-6;

        internal static bool TryApply(
            Recording recording,
            RecordingFinalizationCache cache,
            out RecordingFinalizationCacheApplyResult result)
        {
            return TryApply(recording, cache, default(RecordingFinalizationCacheApplyOptions), out result);
        }

        internal static bool TryApply(
            Recording recording,
            RecordingFinalizationCache cache,
            RecordingFinalizationCacheApplyOptions options,
            out RecordingFinalizationCacheApplyResult result)
        {
            result = default(RecordingFinalizationCacheApplyResult);
            result.Status = RecordingFinalizationCacheApplyStatus.Applied;
            result.LastAuthoredUT = double.NaN;
            result.OldExplicitEndUT = recording != null ? recording.ExplicitEndUT : double.NaN;
            result.NewExplicitEndUT = result.OldExplicitEndUT;
            result.TerminalUT = cache != null ? cache.TerminalUT : double.NaN;
            result.TerminalState = cache?.TerminalState;

            if (recording == null)
                return Reject(recording, cache, options, ref result, RecordingFinalizationCacheApplyStatus.RejectedNullRecording);
            if (cache == null)
                return Reject(recording, cache, options, ref result, RecordingFinalizationCacheApplyStatus.RejectedNullCache);
            if (cache.Status == FinalizationCacheStatus.Empty)
                return Reject(recording, cache, options, ref result, RecordingFinalizationCacheApplyStatus.RejectedEmpty);
            if (cache.Status == FinalizationCacheStatus.Failed)
                return Reject(recording, cache, options, ref result, RecordingFinalizationCacheApplyStatus.RejectedFailed);
            if (cache.Status == FinalizationCacheStatus.Stale && !options.AllowStale)
                return Reject(recording, cache, options, ref result, RecordingFinalizationCacheApplyStatus.RejectedStale);
            if (!MatchesRecordingId(recording.RecordingId, cache.RecordingId))
                return Reject(recording, cache, options, ref result, RecordingFinalizationCacheApplyStatus.RejectedMismatchedRecording);
            if (!MatchesVesselPersistentId(recording.VesselPersistentId, cache.VesselPersistentId))
                return Reject(recording, cache, options, ref result, RecordingFinalizationCacheApplyStatus.RejectedMismatchedVessel);
            if (recording.TerminalStateValue.HasValue && !options.AllowAlreadyFinalizedRepair)
                return Reject(recording, cache, options, ref result, RecordingFinalizationCacheApplyStatus.RejectedAlreadyFinalized);
            if (!cache.TerminalState.HasValue)
                return Reject(recording, cache, options, ref result, RecordingFinalizationCacheApplyStatus.RejectedUnsetTerminalState);
            if (!Enum.IsDefined(typeof(TerminalState), cache.TerminalState.Value))
                return Reject(recording, cache, options, ref result, RecordingFinalizationCacheApplyStatus.RejectedInvalidTerminalState);
            if (!IsFinite(cache.TerminalUT))
                return Reject(recording, cache, options, ref result, RecordingFinalizationCacheApplyStatus.RejectedInvalidTerminalUT);
            if (!TryGetLastAuthoredUT(recording, out double lastAuthoredUT))
                return Reject(recording, cache, options, ref result, RecordingFinalizationCacheApplyStatus.RejectedNoAuthoredData);
            result.LastAuthoredUT = lastAuthoredUT;
            if (cache.TerminalUT + UtEpsilon < lastAuthoredUT)
                return Reject(recording, cache, options, ref result, RecordingFinalizationCacheApplyStatus.RejectedTerminalBeforeLastSample);

            List<OrbitSegment> retainedSegments = BuildRetainedPredictedSegments(
                cache.PredictedSegments,
                lastAuthoredUT,
                cache.TerminalUT);

            int removedPredictedSegments = options.AllowAlreadyFinalizedRepair
                ? RemoveExistingPredictedTail(recording, lastAuthoredUT)
                : 0;

            if (retainedSegments.Count > 0)
                recording.OrbitSegments.AddRange(retainedSegments);

            recording.TerminalStateValue = cache.TerminalState.Value;
            ApplyTerminalMetadata(recording, cache, retainedSegments);

            recording.ExplicitEndUT = cache.TerminalUT;

            RecordingEndpointResolver.RefreshEndpointDecision(
                recording,
                "RecordingFinalizationCacheApplier",
                logDecision: false);

            recording.MarkFilesDirty();

            result.Status = RecordingFinalizationCacheApplyStatus.Applied;
            result.NewExplicitEndUT = recording.ExplicitEndUT;
            result.AppendedSegmentCount = retainedSegments.Count;
            result.RemovedPredictedSegmentCount = removedPredictedSegments;

            ParsekLog.Info(LogTag,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Apply accepted: consumer={0} rec={1} cacheStatus={2} owner={3} staleAllowed={4} " +
                    "appendedSegments={5} removedPredictedSegments={6} lastAuthoredUT={7:F3} " +
                    "oldExplicitEndUT={8} newExplicitEndUT={9} terminal={10} terminalUT={11:F3}",
                    SafeConsumer(options),
                    recording.DebugName,
                    cache.Status,
                    cache.Owner,
                    options.AllowStale,
                    retainedSegments.Count,
                    removedPredictedSegments,
                    lastAuthoredUT,
                    FormatUT(result.OldExplicitEndUT),
                    FormatUT(result.NewExplicitEndUT),
                    cache.TerminalState.Value,
                    cache.TerminalUT));
            return true;
        }

        internal static bool TryGetLastAuthoredUT(Recording recording, out double lastAuthoredUT)
        {
            lastAuthoredUT = double.NaN;
            bool found = false;

            if (recording?.Points != null)
            {
                for (int i = 0; i < recording.Points.Count; i++)
                    ConsiderUT(recording.Points[i].ut, ref lastAuthoredUT, ref found);
            }

            if (recording?.OrbitSegments != null)
            {
                for (int i = 0; i < recording.OrbitSegments.Count; i++)
                {
                    OrbitSegment segment = recording.OrbitSegments[i];
                    if (!segment.isPredicted)
                        ConsiderUT(segment.endUT, ref lastAuthoredUT, ref found);
                }
            }

            if (recording?.TrackSections != null)
            {
                for (int i = 0; i < recording.TrackSections.Count; i++)
                {
                    TrackSection section = recording.TrackSections[i];
                    bool hasAuthoredPayload = false;
                    if (section.frames != null)
                    {
                        for (int j = 0; j < section.frames.Count; j++)
                        {
                            ConsiderUT(section.frames[j].ut, ref lastAuthoredUT, ref found);
                            hasAuthoredPayload = true;
                        }
                    }

                    if (section.checkpoints != null)
                    {
                        for (int j = 0; j < section.checkpoints.Count; j++)
                        {
                            OrbitSegment checkpoint = section.checkpoints[j];
                            if (!checkpoint.isPredicted)
                            {
                                ConsiderUT(checkpoint.endUT, ref lastAuthoredUT, ref found);
                                hasAuthoredPayload = true;
                            }
                        }
                    }

                    if (hasAuthoredPayload)
                        ConsiderUT(section.endUT, ref lastAuthoredUT, ref found);
                }
            }

            return found;
        }

        private static bool Reject(
            Recording recording,
            RecordingFinalizationCache cache,
            RecordingFinalizationCacheApplyOptions options,
            ref RecordingFinalizationCacheApplyResult result,
            RecordingFinalizationCacheApplyStatus status)
        {
            result.Status = status;
            result.NewExplicitEndUT = recording != null ? recording.ExplicitEndUT : result.OldExplicitEndUT;
            ParsekLog.Warn(LogTag,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Apply rejected: consumer={0} reason={1} rec={2} cacheRec={3} recPid={4} cachePid={5} " +
                    "cacheStatus={6} owner={7} lastAuthoredUT={8} terminal={9} terminalUT={10}",
                    SafeConsumer(options),
                    status,
                    recording != null ? recording.DebugName : "(null)",
                    cache?.RecordingId ?? "(null)",
                    recording != null ? recording.VesselPersistentId.ToString(CultureInfo.InvariantCulture) : "(null)",
                    cache != null ? cache.VesselPersistentId.ToString(CultureInfo.InvariantCulture) : "(null)",
                    cache != null ? cache.Status.ToString() : "(null)",
                    cache != null ? cache.Owner.ToString() : "(null)",
                    FormatUT(result.LastAuthoredUT),
                    cache?.TerminalState?.ToString() ?? "(null)",
                    cache != null ? FormatUT(cache.TerminalUT) : "(null)"));
            return false;
        }

        private static List<OrbitSegment> BuildRetainedPredictedSegments(
            IList<OrbitSegment> source,
            double lastAuthoredUT,
            double terminalUT)
        {
            var retained = new List<OrbitSegment>();
            if (source == null || source.Count == 0)
                return retained;

            for (int i = 0; i < source.Count; i++)
            {
                OrbitSegment segment = source[i];
                if (!IsFinite(segment.startUT) || !IsFinite(segment.endUT))
                    continue;
                if (segment.endUT <= lastAuthoredUT + UtEpsilon)
                    continue;
                if (segment.startUT >= terminalUT - UtEpsilon)
                    continue;

                if (segment.startUT < lastAuthoredUT)
                    segment.startUT = lastAuthoredUT;
                if (segment.endUT > terminalUT)
                    segment.endUT = terminalUT;
                if (segment.endUT <= segment.startUT + UtEpsilon)
                    continue;

                segment.isPredicted = true;
                retained.Add(segment);
            }

            return retained;
        }

        private static int RemoveExistingPredictedTail(Recording recording, double lastAuthoredUT)
        {
            if (recording?.OrbitSegments == null || recording.OrbitSegments.Count == 0)
                return 0;

            int removed = 0;
            for (int i = recording.OrbitSegments.Count - 1; i >= 0; i--)
            {
                OrbitSegment segment = recording.OrbitSegments[i];
                if (!segment.isPredicted || segment.endUT <= lastAuthoredUT + UtEpsilon)
                    continue;

                recording.OrbitSegments.RemoveAt(i);
                removed++;
            }

            return removed;
        }

        private static void ApplyTerminalMetadata(
            Recording recording,
            RecordingFinalizationCache cache,
            List<OrbitSegment> retainedSegments)
        {
            TerminalState terminalState = cache.TerminalState.Value;
            ClearTerminalOrbit(recording);

            if (terminalState == TerminalState.Landed || terminalState == TerminalState.Splashed)
            {
                if (cache.TerminalPosition.HasValue)
                {
                    recording.TerminalPosition = cache.TerminalPosition;
                    recording.TerrainHeightAtEnd = cache.TerrainHeightAtEnd.HasValue
                        ? cache.TerrainHeightAtEnd.Value
                        : double.NaN;
                }
                else if (!CanPreserveSurfaceMetadata(recording, cache, terminalState))
                {
                    recording.TerminalPosition = null;
                    recording.TerrainHeightAtEnd = double.NaN;
                }

                return;
            }

            recording.TerminalPosition = null;
            recording.TerrainHeightAtEnd = double.NaN;

            if (terminalState != TerminalState.Orbiting
                && terminalState != TerminalState.SubOrbital
                && terminalState != TerminalState.Docked)
            {
                return;
            }

            if (cache.TerminalOrbit.HasValue)
            {
                StampTerminalOrbit(recording, cache.TerminalOrbit.Value);
                return;
            }

            for (int i = retainedSegments.Count - 1; i >= 0; i--)
            {
                OrbitSegment segment = retainedSegments[i];
                if (!string.IsNullOrEmpty(segment.bodyName) && segment.semiMajorAxis > 0.0)
                {
                    StampTerminalOrbit(recording, RecordingFinalizationTerminalOrbit.FromSegment(segment));
                    return;
                }
            }
        }

        private static bool CanPreserveSurfaceMetadata(
            Recording recording,
            RecordingFinalizationCache cache,
            TerminalState terminalState)
        {
            if (!recording.TerminalPosition.HasValue)
                return false;

            SurfacePosition existing = recording.TerminalPosition.Value;
            SurfaceSituation expectedSituation = terminalState == TerminalState.Splashed
                ? SurfaceSituation.Splashed
                : SurfaceSituation.Landed;
            if (existing.situation != expectedSituation)
                return false;
            if (string.IsNullOrEmpty(existing.body))
                return false;

            if (!string.IsNullOrEmpty(cache.TerminalBodyName)
                && !string.Equals(existing.body, cache.TerminalBodyName, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        private static void StampTerminalOrbit(
            Recording recording,
            RecordingFinalizationTerminalOrbit terminalOrbit)
        {
            recording.TerminalOrbitInclination = terminalOrbit.inclination;
            recording.TerminalOrbitEccentricity = terminalOrbit.eccentricity;
            recording.TerminalOrbitSemiMajorAxis = terminalOrbit.semiMajorAxis;
            recording.TerminalOrbitLAN = terminalOrbit.longitudeOfAscendingNode;
            recording.TerminalOrbitArgumentOfPeriapsis = terminalOrbit.argumentOfPeriapsis;
            recording.TerminalOrbitMeanAnomalyAtEpoch = terminalOrbit.meanAnomalyAtEpoch;
            recording.TerminalOrbitEpoch = terminalOrbit.epoch;
            recording.TerminalOrbitBody = terminalOrbit.bodyName;
        }

        private static void ClearTerminalOrbit(Recording recording)
        {
            recording.TerminalOrbitInclination = 0.0;
            recording.TerminalOrbitEccentricity = 0.0;
            recording.TerminalOrbitSemiMajorAxis = 0.0;
            recording.TerminalOrbitLAN = 0.0;
            recording.TerminalOrbitArgumentOfPeriapsis = 0.0;
            recording.TerminalOrbitMeanAnomalyAtEpoch = 0.0;
            recording.TerminalOrbitEpoch = 0.0;
            recording.TerminalOrbitBody = null;
        }

        private static bool MatchesRecordingId(string recordingId, string cacheRecordingId)
        {
            return !string.IsNullOrEmpty(recordingId)
                && !string.IsNullOrEmpty(cacheRecordingId)
                && string.Equals(recordingId, cacheRecordingId, StringComparison.Ordinal);
        }

        private static bool MatchesVesselPersistentId(uint recordingPid, uint cachePid)
        {
            // 0 means "unknown" in legacy/test recordings. RecordingId remains the
            // required identity match, while PID tightens ownership when both sides
            // have a real persistent id.
            return recordingPid == 0 || cachePid == 0 || recordingPid == cachePid;
        }

        private static void ConsiderUT(double ut, ref double lastAuthoredUT, ref bool found)
        {
            if (!IsFinite(ut))
                return;

            if (!found || ut > lastAuthoredUT)
            {
                lastAuthoredUT = ut;
                found = true;
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string SafeConsumer(RecordingFinalizationCacheApplyOptions options)
        {
            return string.IsNullOrEmpty(options.ConsumerPath) ? "unspecified" : options.ConsumerPath;
        }

        private static string FormatUT(double ut)
        {
            return IsFinite(ut) ? ut.ToString("F3", CultureInfo.InvariantCulture) : "-";
        }
    }
}
