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
        // The caller saw KSP destroy the vessel (a background destruction the deferred
        // check confirmed). A Destroyed cache applied without it is a prediction.
        public bool DestructionConfirmed;
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
        public int AppendedDescentPointCount;
        public int RemovedDescentPointCount;

        public bool Applied => Status == RecordingFinalizationCacheApplyStatus.Applied;
    }

    internal static class RecordingFinalizationCacheApplier
    {
        private const string LogTag = "FinalizerCache";
        internal const double UtEpsilon = 1e-6;

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

            bool carriesDescent = IsPredictedDescentCache(cache);
            int removedDescentPoints = options.AllowAlreadyFinalizedRepair || carriesDescent
                ? RemovePredictedDescent(recording, "RecordingFinalizationCacheApplier")
                : 0;

            if (retainedSegments.Count > 0)
                recording.OrbitSegments.AddRange(retainedSegments);

            int appendedDescentPoints = carriesDescent
                ? AppendPredictedDescent(
                    recording, cache.PredictedDescentPoints, lastAuthoredUT, cache.TerminalUT,
                    "RecordingFinalizationCacheApplier")
                : 0;

            recording.StampTerminalState(
                cache.TerminalState.Value, "RecordingFinalizationCacheApplier",
                predicted: IsPredictedDestroyedTerminal(cache, options)
                    || (appendedDescentPoints > 0 && IsPredictedTouchdownCache(cache)),
                quietEnd: cache.QuietEnd && appendedDescentPoints > 0);
            MarkPredictedFinalOrbit(recording, CountPredicted(retainedSegments), "RecordingFinalizationCacheApplier");
            ApplyTerminalMetadata(recording, cache, retainedSegments);
            if (appendedDescentPoints > 0 && recording.TerminalPosition.HasValue)
            {
                ApplyPredictedTouchdownToSnapshot(
                    recording.VesselSnapshot,
                    recording.TerminalPosition.Value,
                    cache.TerminalState.Value,
                    recording.RecordingId,
                    "RecordingFinalizationCacheApplier");
            }

            recording.ExplicitEndUT = cache.TerminalUT;
            DebrisRelativeRecorderPolicy.NormalizeParentAnchoredRelativeRecording(
                recording,
                "RecordingFinalizationCacheApplier",
                refreshEndpointDecision: false);

            RecordingEndpointResolver.RefreshEndpointDecision(
                recording,
                "RecordingFinalizationCacheApplier",
                logDecision: false);

            recording.MarkFilesDirty();

            result.Status = RecordingFinalizationCacheApplyStatus.Applied;
            result.NewExplicitEndUT = recording.ExplicitEndUT;
            result.AppendedSegmentCount = retainedSegments.Count;
            result.RemovedPredictedSegmentCount = removedPredictedSegments;
            result.AppendedDescentPointCount = appendedDescentPoints;
            result.RemovedDescentPointCount = removedDescentPoints;

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

        /// <summary>
        /// True when the cache carries a predicted chute descent (owner ruling Q4): descent
        /// points to a touchdown (Landed / Splashed) or, too fast under the canopies, to a
        /// ground impact (Destroyed). Pure.
        /// </summary>
        internal static bool IsPredictedDescentCache(RecordingFinalizationCache cache)
        {
            return cache != null
                && (cache.TerminalState == TerminalState.Landed
                    || cache.TerminalState == TerminalState.Splashed
                    || cache.TerminalState == TerminalState.Destroyed)
                && cache.PredictedDescentPoints != null
                && cache.PredictedDescentPoints.Count > 0;
        }

        /// <summary>A predicted chute descent that ends in a survived touchdown. Pure.</summary>
        internal static bool IsPredictedTouchdownCache(RecordingFinalizationCache cache)
        {
            return IsPredictedDescentCache(cache)
                && cache.TerminalState != TerminalState.Destroyed;
        }

        /// <summary>A section Parsek predicted rather than recorded. Pure.</summary>
        internal static bool IsPredictedSection(TrackSection section)
        {
            return section.source == TrackSectionSource.Predicted;
        }

        /// <summary>
        /// Pure: the earliest start of a predicted section (the predicted tail is a suffix of
        /// the recording), false when the recording carries none.
        /// </summary>
        internal static bool TryGetPredictedSectionStartUT(Recording recording, out double startUT)
        {
            startUT = double.NaN;
            if (recording?.TrackSections == null)
                return false;
            bool found = false;
            for (int i = 0; i < recording.TrackSections.Count; i++)
            {
                TrackSection section = recording.TrackSections[i];
                if (!IsPredictedSection(section) || !IsFinite(section.startUT))
                    continue;
                if (!found || section.startUT < startUT)
                    startUT = section.startUT;
                found = true;
            }
            return found;
        }

        /// <summary>
        /// Appends a predicted chute descent to <paramref name="recording"/>: one Absolute,
        /// Atmospheric <see cref="TrackSectionSource.Predicted"/> section from
        /// <paramref name="lastAuthoredUT"/> to the last descent point at or before
        /// <paramref name="terminalUT"/>, with the same points in the flat list. The section's
        /// first frame anchors it at the last real sample: that sample itself when it is
        /// body-fixed at that UT (no jump; the flat list dedupes it), its body-fixed shadow
        /// when it is a Relative offset, else the descent evaluated there. Every appended point
        /// carries the last real sample's career resources, so the tail moves no funds,
        /// science or reputation. Returns the number of points added to the flat list.
        /// </summary>
        internal static int AppendPredictedDescent(
            Recording recording,
            IList<TrajectoryPoint> descentPoints,
            double lastAuthoredUT,
            double terminalUT,
            string context)
        {
            if (recording == null || descentPoints == null || descentPoints.Count == 0
                || !IsFinite(lastAuthoredUT))
            {
                return 0;
            }

            var kept = new List<TrajectoryPoint>(descentPoints.Count);
            for (int i = 0; i < descentPoints.Count; i++)
            {
                TrajectoryPoint p = descentPoints[i];
                if (!IsFinite(p.ut) || p.ut <= lastAuthoredUT + UtEpsilon)
                    continue;
                if (IsFinite(terminalUT) && p.ut > terminalUT + UtEpsilon)
                    continue;
                kept.Add(p);
            }
            if (kept.Count == 0)
            {
                ParsekLog.Verbose(LogTag, string.Format(CultureInfo.InvariantCulture,
                    "Predicted descent not appended: context={0} rec={1} reason=no-point-past-last-authored " +
                    "lastAuthoredUT={2:F3} descentEndUT={3:F3}",
                    context ?? "(none)", recording.DebugName, lastAuthoredUT,
                    descentPoints[descentPoints.Count - 1].ut));
                return 0;
            }

            if (recording.Points == null)
                recording.Points = new List<TrajectoryPoint>();
            if (recording.TrackSections == null)
                recording.TrackSections = new List<TrackSection>();

            TrajectoryPoint anchor = ResolveDescentAnchor(
                recording, descentPoints, lastAuthoredUT, out bool anchorIsRealSample, out string anchorSource);
            bool hasResources = recording.Points.Count > 0;
            TrajectoryPoint resourceSource = hasResources
                ? recording.Points[recording.Points.Count - 1]
                : default(TrajectoryPoint);

            var frames = new List<TrajectoryPoint>(kept.Count + 1);
            frames.Add(anchorIsRealSample ? anchor : WithResources(anchor, hasResources, resourceSource));
            for (int i = 0; i < kept.Count; i++)
                frames.Add(WithResources(kept[i], hasResources, resourceSource));

            float minAlt = float.NaN;
            float maxAlt = float.NaN;
            for (int i = 0; i < frames.Count; i++)
            {
                float a = (float)frames[i].altitude;
                if (float.IsNaN(minAlt) || a < minAlt) minAlt = a;
                if (float.IsNaN(maxAlt) || a > maxAlt) maxAlt = a;
            }
            double endUT = frames[frames.Count - 1].ut;
            double span = endUT - lastAuthoredUT;
            recording.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = lastAuthoredUT,
                endUT = endUT,
                frames = frames,
                source = TrackSectionSource.Predicted,
                sampleRateHz = span > 0.0 ? (float)((frames.Count - 1) / span) : 0f,
                boundaryDiscontinuityMeters = 0f,
                minAltitude = minAlt,
                maxAltitude = maxAlt,
            });

            int appended = 0;
            for (int i = anchorIsRealSample ? 1 : 0; i < frames.Count; i++)
            {
                recording.Points.Add(frames[i]);
                appended++;
            }
            recording.CachedStats = null;
            recording.CachedStatsPointCount = 0;

            TrajectoryPoint last = frames[frames.Count - 1];
            ParsekLog.Info(LogTag, string.Format(CultureInfo.InvariantCulture,
                "Predicted descent appended: context={0} rec={1} startUT={2:F3} touchdownUT={3:F3} " +
                "startAlt={4:F1} touchdownAlt={5:F1} body={6} frames={7} flatPoints={8} anchor={9}",
                context ?? "(none)",
                recording.DebugName,
                lastAuthoredUT,
                last.ut,
                frames[0].altitude,
                last.altitude,
                last.bodyName ?? "(null)",
                frames.Count,
                appended,
                anchorSource));
            return appended;
        }

        /// <summary>
        /// Removes every predicted section and the flat points it added: the points after its
        /// start, and an anchor frame at its start that is not the real last sample. Returns
        /// the number of flat points removed. Logs once when it removed a section.
        /// </summary>
        internal static int RemovePredictedDescent(Recording recording, string context)
        {
            if (recording?.TrackSections == null || recording.TrackSections.Count == 0)
                return 0;

            int removedSections = 0;
            int removedPoints = 0;
            for (int s = recording.TrackSections.Count - 1; s >= 0; s--)
            {
                TrackSection section = recording.TrackSections[s];
                if (!IsPredictedSection(section))
                    continue;
                recording.TrackSections.RemoveAt(s);
                removedSections++;
                if (recording.Points == null)
                    continue;

                double startUT = section.startUT;
                for (int i = recording.Points.Count - 1; i >= 0; i--)
                {
                    if (recording.Points[i].ut > startUT + UtEpsilon)
                    {
                        recording.Points.RemoveAt(i);
                        removedPoints++;
                    }
                }
                // A non-real anchor frame sits at the start UT right after the real last sample.
                int lastIndex = recording.Points.Count - 1;
                if (section.frames != null && section.frames.Count > 0 && lastIndex >= 1)
                {
                    TrajectoryPoint candidate = recording.Points[lastIndex];
                    TrajectoryPoint before = recording.Points[lastIndex - 1];
                    if (Math.Abs(candidate.ut - startUT) <= UtEpsilon
                        && Math.Abs(before.ut - startUT) <= UtEpsilon
                        && PointsEqual(candidate, section.frames[0]))
                    {
                        recording.Points.RemoveAt(lastIndex);
                        removedPoints++;
                    }
                }
            }

            if (removedSections == 0)
                return 0;

            recording.CachedStats = null;
            recording.CachedStatsPointCount = 0;
            ParsekLog.Info(LogTag, string.Format(CultureInfo.InvariantCulture,
                "Predicted descent removed: context={0} rec={1} sections={2} flatPoints={3}",
                context ?? "(none)", recording.DebugName, removedSections, removedPoints));
            return removedPoints;
        }

        /// <summary>
        /// Writes a predicted touchdown into the end vessel snapshot so the end spawn places
        /// the vessel where the descent comes down, sitting on the ground, instead of where it
        /// hung under its canopy: lat / lon / alt and the LANDED / SPLASHED situation.
        /// </summary>
        internal static bool ApplyPredictedTouchdownToSnapshot(
            ConfigNode snapshot,
            SurfacePosition touchdown,
            TerminalState terminal,
            string recordingId,
            string context)
        {
            if (snapshot == null)
                return false;
            string oldAlt = snapshot.GetValue("alt") ?? "?";
            string oldSit = snapshot.GetValue("sit") ?? "?";
            snapshot.SetValue("lat", touchdown.latitude.ToString("R", CultureInfo.InvariantCulture), true);
            snapshot.SetValue("lon", touchdown.longitude.ToString("R", CultureInfo.InvariantCulture), true);
            snapshot.SetValue("alt", touchdown.altitude.ToString("R", CultureInfo.InvariantCulture), true);
            VesselSnapshotOps.ApplySituationToNode(
                snapshot, terminal == TerminalState.Splashed ? "SPLASHED" : "LANDED");
            snapshot.SetValue("landedAt", string.Empty, true);
            snapshot.SetValue("displaylandedAt", string.Empty, true);
            ParsekLog.Info(LogTag, string.Format(CultureInfo.InvariantCulture,
                "Predicted touchdown written to the end snapshot: context={0} rec={1} sit {2} -> {3} " +
                "alt {4} -> {5:F1} lat={6:F5} lon={7:F5}",
                context ?? "(none)", recordingId ?? "(null)", oldSit, snapshot.GetValue("sit"),
                oldAlt, touchdown.altitude, touchdown.latitude, touchdown.longitude));
            return true;
        }

        private static TrajectoryPoint ResolveDescentAnchor(
            Recording recording,
            IList<TrajectoryPoint> descentPoints,
            double lastAuthoredUT,
            out bool isRealSample,
            out string source)
        {
            isRealSample = false;
            string descentBody = descentPoints[0].bodyName;
            if (recording.Points != null && recording.Points.Count > 0)
            {
                TrajectoryPoint last = recording.Points[recording.Points.Count - 1];
                if (Math.Abs(last.ut - lastAuthoredUT) <= UtEpsilon
                    && string.Equals(last.bodyName, descentBody, StringComparison.Ordinal))
                {
                    FlatPointFrame frame = FlatPointFrameDispatch.Classify(
                        recording.TrackSections, last, out TrajectoryPoint bodyFixed);
                    if (frame == FlatPointFrame.BodyFixed)
                    {
                        isRealSample = true;
                        source = "last-sample";
                        return last;
                    }
                    if (frame == FlatPointFrame.RelativeWithShadow)
                    {
                        source = "last-sample-shadow";
                        return bodyFixed;
                    }
                }
            }

            source = "descent-evaluated";
            TrajectoryPoint? evaluated = ParachuteDescentPlanner.EvaluateAt(descentPoints, lastAuthoredUT);
            return evaluated ?? descentPoints[0];
        }

        private static TrajectoryPoint WithResources(TrajectoryPoint p, bool hasSource, TrajectoryPoint source)
        {
            if (!hasSource)
                return p;
            p.funds = source.funds;
            p.science = source.science;
            p.reputation = source.reputation;
            return p;
        }

        private static bool PointsEqual(TrajectoryPoint a, TrajectoryPoint b)
        {
            return a.ut == b.ut
                && a.latitude == b.latitude
                && a.longitude == b.longitude
                && a.altitude == b.altitude
                && string.Equals(a.bodyName, b.bodyName, StringComparison.Ordinal);
        }

        /// <summary>
        /// True when the Destroyed terminal this cache carries is Parsek's prediction rather
        /// than a destruction KSP performed: a cache built from a live vessel's refresh,
        /// applied by a caller that did not see the vessel destroyed. A destroy-event cache
        /// (the refresh the destruction itself triggered) and a caller-confirmed destruction
        /// are confirmed. Pure. The two destroy reasons are the producer's
        /// (<c>RecordingFinalizationCacheProducer.IsDestroyRefreshReason</c>), spelled here so
        /// the pure applier does not depend on the live producer; a test keeps them equal.
        /// </summary>
        internal static bool IsPredictedDestroyedTerminal(
            RecordingFinalizationCache cache,
            RecordingFinalizationCacheApplyOptions options)
        {
            return cache != null
                && cache.TerminalState == TerminalState.Destroyed
                && !options.DestructionConfirmed
                && !IsDestroyEventReason(cache.RefreshReason);
        }

        /// <summary>
        /// Owner ruling Q8: an Orbiting / SubOrbital ending a predicted tail reached (the
        /// recording carries <paramref name="predictedTailSegments"/> predicted segments past its
        /// last authored sample: an escape followed to its final orbit, a coast through a
        /// patched-conic change) is Parsek's prediction, not a state KSP was seen in; stock holds
        /// the vessel on rails meanwhile and may still meet what the predictor missed. Re-stamps
        /// the ending as predicted (<see cref="Recording.TerminalPredicted"/>) so the stock-fate
        /// reconciler squares it with stock. A stable orbit carries no predicted tail and is left
        /// alone. Returns true when it marked.
        /// </summary>
        internal static bool MarkPredictedFinalOrbit(Recording recording, int predictedTailSegments, string context)
        {
            if (recording == null || predictedTailSegments <= 0 || recording.TerminalPredicted)
                return false;
            if (recording.TerminalStateValue != TerminalState.Orbiting
                && recording.TerminalStateValue != TerminalState.SubOrbital)
                return false;
            recording.StampTerminalState(recording.TerminalStateValue, (context ?? LogTag) + ".predictedFinalOrbit",
                predicted: true);
            ParsekLog.Verbose(LogTag,
                string.Format(CultureInfo.InvariantCulture,
                    "{0}: final orbit marked predicted rec={1} terminal={2} predictedTailSegments={3}",
                    context ?? LogTag,
                    recording.RecordingId ?? "(null)",
                    recording.TerminalStateValue,
                    predictedTailSegments));
            return true;
        }

        internal static int CountPredicted(IList<OrbitSegment> segments)
        {
            if (segments == null)
                return 0;
            int n = 0;
            for (int i = 0; i < segments.Count; i++)
                if (segments[i].isPredicted)
                    n++;
            return n;
        }

        internal static bool IsDestroyEventReason(string reason)
        {
            return string.Equals(reason, "destroy_event", StringComparison.Ordinal)
                || string.Equals(reason, "background_destroy", StringComparison.Ordinal);
        }

        internal static bool TryGetLastAuthoredUT(Recording recording, out double lastAuthoredUT)
        {
            lastAuthoredUT = double.NaN;
            bool found = false;

            if (DebrisRelativeRecorderPolicy.TryGetLastRecorderPersistableAuthoredUT(
                    recording,
                    out lastAuthoredUT))
            {
                return true;
            }

            if (DebrisRelativeRecorderPolicy.ShouldNormalizeParentAnchoredDebris(recording))
                return false;

            // A predicted section (a chute descent) is not authored data, and neither are the
            // flat points it added after its start.
            bool hasPredictedSection = TryGetPredictedSectionStartUT(recording, out double predictedStartUT);
            if (recording?.Points != null)
            {
                for (int i = 0; i < recording.Points.Count; i++)
                {
                    double ut = recording.Points[i].ut;
                    if (hasPredictedSection && ut > predictedStartUT + UtEpsilon)
                        continue;
                    ConsiderUT(ut, ref lastAuthoredUT, ref found);
                }
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
                    if (IsPredictedSection(section))
                        continue;
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

            // A cache without TerminalBodyName can only prove identity through
            // RecordingId/PID plus matching surface state, so preserve the pair
            // rather than mixing partial cache metadata into the surface tuple.
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
