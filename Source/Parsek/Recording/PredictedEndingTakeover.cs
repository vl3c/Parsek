using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// What <see cref="PredictedEndingTakeover.DropPredictedEndingForTakeover"/> did to a
    /// recording.
    /// </summary>
    internal struct PredictedEndingDropResult
    {
        public bool Dropped;
        public string Reason;
        public double CutUT;
        public double LastRealUT;
        public int RemovedSegmentCount;
        public int ClippedSegmentCount;
        public int KeptCoastSegmentCount;
        public TerminalState? PreviousTerminal;
        public bool PreviousTerminalPredicted;
        public double PreviousExplicitEndUT;
        public bool TerminalCleared;
        public bool CrewEndStatesCleared;
        // The vessel followed the predicted conic up to the takeover, so the coast was kept
        // to the takeover UT as recorded history (CoastDecision names why, either way).
        public bool CoastKeptToTakeover;
        public string CoastDecision;
        public double CoastMissMeters;
    }

    /// <summary>
    /// What the live vessel looked like at the takeover, for
    /// <see cref="PredictedEndingTakeover.CoastFollowedPredictedConic"/>: its current orbit as a
    /// segment (KSP-native elements, stored frame) and its body's gravitational parameter.
    /// </summary>
    internal struct TakeoverCoastEvidence
    {
        public OrbitSegment LiveOrbit;
        public double GravParameter;
    }

    /// <summary>
    /// Owner ruling Q1b: when Parsek stopped watching a vessel that KSP still has, the
    /// recording keeps a PREDICTED ending (orbit segments marked <c>isPredicted</c> after the
    /// last real sample, and the terminal state, terminal orbit / surface metadata and
    /// explicit end UT that prediction set). When the player flies that vessel again
    /// (switch, Tracking Station Fly, KSC marker Fly, map Switch-To, alarm Jump, a
    /// committed-tree resume), the prediction is wrong by construction: the recording the
    /// real flight continues from drops it and ends at its last real sample, so the replay
    /// shows the real flight from the takeover on and never a predicted ghost next to the
    /// live vessel or next to the continuation's own ghost.
    /// <para>Pure over <see cref="Recording"/>: no live game reads.</para>
    /// </summary>
    internal static class PredictedEndingTakeover
    {
        private const string LogTag = "PredictedTakeover";
        private const double UtEpsilon = 1e-6;

        /// <summary>
        /// A terminal stamped more than this many seconds after the last real sample was
        /// set by a prediction (a background close that kept a predicted ending's terminal
        /// UT), even when no predicted segment carries it.
        /// </summary>
        internal const double PredictedTerminalUTToleranceSeconds = 1.0;

        /// <summary>
        /// How far the live vessel may sit from the predicted conic at the takeover for the
        /// coast to count as followed: the larger of this and
        /// <see cref="CoastMatchRelativeTolerance"/> times its distance from the body centre.
        /// An on-rails vessel is on that very conic (numerical noise only); one that flew in
        /// atmosphere or burned is kilometres off.
        /// </summary>
        internal const double CoastMatchMinToleranceMeters = 250.0;
        internal const double CoastMatchRelativeTolerance = 1e-4;

        /// <summary>
        /// Pure: the latest UT the recording holds REAL data for (points, track sections,
        /// non-predicted orbit segments), the anchor every predicted tail starts at.
        /// Falls back to the earliest predicted segment start when the recording holds no
        /// real data.
        /// </summary>
        internal static bool TryGetLastRealUT(Recording rec, out double lastRealUT)
        {
            lastRealUT = double.NaN;
            if (rec == null)
                return false;
            if (RecordingFinalizationCacheApplier.TryGetLastAuthoredUT(rec, out lastRealUT)
                && IsFinite(lastRealUT))
            {
                return true;
            }

            bool found = false;
            if (rec.OrbitSegments != null)
            {
                for (int i = 0; i < rec.OrbitSegments.Count; i++)
                {
                    OrbitSegment seg = rec.OrbitSegments[i];
                    if (!seg.isPredicted || !IsFinite(seg.startUT))
                        continue;
                    if (!found || seg.startUT < lastRealUT)
                        lastRealUT = seg.startUT;
                    found = true;
                }
            }
            return found;
        }

        /// <summary>
        /// Pure: does the recording end in a prediction a takeover must drop? True for a
        /// predicted crash (<see cref="Recording.TerminalPredicted"/>), for predicted orbit
        /// segments past the last real sample, and for a terminal stamped well past the
        /// last real sample. Never for Recovered / Docked / Boarded, which no prediction
        /// writes.
        /// </summary>
        internal static bool HasPredictedEnding(Recording rec)
        {
            if (rec == null)
                return false;
            if (IsNeverPredictedTerminal(rec.TerminalStateValue))
                return false;
            if (rec.TerminalPredicted)
                return true;
            if (!TryGetLastRealUT(rec, out double lastRealUT))
                return false;
            if (CountPredictedSegmentsPast(rec, lastRealUT) > 0)
                return true;
            return rec.TerminalStateValue.HasValue
                && IsFinite(rec.ExplicitEndUT)
                && rec.ExplicitEndUT > lastRealUT + PredictedTerminalUTToleranceSeconds;
        }

        /// <summary>
        /// Drops the predicted ending of <paramref name="rec"/> because the player took the
        /// vessel over at <paramref name="takeoverUT"/>. The cut is the last real sample, or
        /// the takeover UT when that is earlier (a non-finite takeover UT reads as the last
        /// real sample): predicted segments past the cut are removed (one straddling it is
        /// clipped to it), and when the ending was predicted the terminal state, the
        /// predicted-crash flag, the destroyed flag, the terminal orbit and surface metadata
        /// and an explicit end past the cut are cleared. A recording with no predicted
        /// ending is left untouched. Logs one Info line when it changed something.
        /// </summary>
        /// <para>With <paramref name="coastEvidence"/> (the live vessel's orbit at the takeover)
        /// and the vessel still on the predicted conic at <paramref name="takeoverUT"/>
        /// (<see cref="CoastFollowedPredictedConic"/>: it stayed on rails, where stock moves it
        /// on exactly that drag-free conic), the cut is the takeover UT instead: the coast up to
        /// it stays and becomes recorded history (its segments lose <c>isPredicted</c>), so the
        /// replay never loses the stage between the last real sample and the takeover. A vessel
        /// off the conic (it flew in physics through air, or burned) keeps today's cut.</para>
        internal static PredictedEndingDropResult DropPredictedEndingForTakeover(
            Recording rec, double takeoverUT, string context,
            TakeoverCoastEvidence? coastEvidence = null)
        {
            var result = new PredictedEndingDropResult
            {
                Dropped = false,
                CutUT = double.NaN,
                LastRealUT = double.NaN,
                PreviousTerminal = rec?.TerminalStateValue,
                PreviousTerminalPredicted = rec != null && rec.TerminalPredicted,
                PreviousExplicitEndUT = rec != null ? rec.ExplicitEndUT : double.NaN,
            };

            if (rec == null)
            {
                result.Reason = "no-recording";
                return result;
            }
            if (IsNeverPredictedTerminal(rec.TerminalStateValue))
            {
                result.Reason = "terminal-never-predicted";
                LogSkip(rec, context, result.Reason);
                return result;
            }
            if (!HasPredictedEnding(rec))
            {
                result.Reason = "no-predicted-ending";
                return result;
            }

            bool hasLastReal = TryGetLastRealUT(rec, out double lastRealUT);
            result.LastRealUT = hasLastReal ? lastRealUT : double.NaN;
            double cutUT = hasLastReal ? lastRealUT : takeoverUT;
            if (IsFinite(takeoverUT) && (!IsFinite(cutUT) || takeoverUT < cutUT))
                cutUT = takeoverUT;
            result.CoastMissMeters = double.NaN;
            if (!coastEvidence.HasValue)
            {
                result.CoastDecision = "no-live-evidence";
            }
            else if (!hasLastReal || !IsFinite(takeoverUT) || takeoverUT <= lastRealUT + UtEpsilon)
            {
                result.CoastDecision = "takeover-not-after-last-real";
            }
            else
            {
                string coastReason;
                double miss;
                if (CoastFollowedPredictedConic(rec, takeoverUT, coastEvidence.Value, out miss, out coastReason))
                {
                    cutUT = takeoverUT;
                    result.CoastKeptToTakeover = true;
                }
                result.CoastMissMeters = miss;
                result.CoastDecision = coastReason;
            }
            if (!IsFinite(cutUT))
            {
                result.Reason = "no-cut-ut";
                LogSkip(rec, context, result.Reason);
                return result;
            }
            result.CutUT = cutUT;

            if (rec.OrbitSegments != null)
            {
                for (int i = rec.OrbitSegments.Count - 1; i >= 0; i--)
                {
                    OrbitSegment seg = rec.OrbitSegments[i];
                    if (!seg.isPredicted || seg.endUT <= cutUT + UtEpsilon)
                        continue;
                    if (seg.startUT < cutUT - UtEpsilon)
                    {
                        seg.endUT = cutUT;
                        rec.OrbitSegments[i] = seg;
                        result.ClippedSegmentCount++;
                        continue;
                    }
                    rec.OrbitSegments.RemoveAt(i);
                    result.RemovedSegmentCount++;
                }
                if (result.CoastKeptToTakeover)
                {
                    // The kept coast is what the vessel did: stock moved it on that conic.
                    for (int i = 0; i < rec.OrbitSegments.Count; i++)
                    {
                        OrbitSegment seg = rec.OrbitSegments[i];
                        if (!seg.isPredicted || seg.endUT > cutUT + UtEpsilon)
                            continue;
                        seg.isPredicted = false;
                        rec.OrbitSegments[i] = seg;
                        result.KeptCoastSegmentCount++;
                    }
                }
            }

            if (rec.TerminalStateValue.HasValue)
            {
                rec.StampTerminalState(null, "PredictedEndingTakeover:" + (context ?? "(none)"));
                result.TerminalCleared = true;
                // The crew answer the dropped ending decided (a predicted crash kills the crew,
                // owner ruling Q6c) is re-derived from the real flight's ending at its commit;
                // the death respawn stamp belonged to that death.
                result.CrewEndStatesCleared = rec.CrewEndStates != null || rec.CrewEndStatesResolved;
                rec.CrewEndStates = null;
                rec.CrewEndStatesResolved = false;
                if (result.PreviousTerminal == TerminalState.Destroyed && rec.CrewDeathRespawns.HasValue)
                {
                    rec.CrewDeathRespawns = null;
                    rec.CrewDeathRespawnSeconds = double.NaN;
                    result.CrewEndStatesCleared = true;
                }
            }
            rec.VesselDestroyed = false;
            ClearTerminalOrbit(rec);
            rec.TerminalPosition = null;
            rec.TerrainHeightAtEnd = double.NaN;
            if (IsFinite(rec.ExplicitEndUT) && rec.ExplicitEndUT > cutUT + UtEpsilon)
                rec.ExplicitEndUT = double.NaN;

            RecordingEndpointResolver.RefreshEndpointDecision(
                rec, "PredictedEndingTakeover", logDecision: false);
            rec.MarkFilesDirty();

            result.Dropped = true;
            result.Reason = "dropped";
            ParsekLog.Info(LogTag,
                string.Format(CultureInfo.InvariantCulture,
                    "Predicted ending dropped for takeover: context={0} rec={1} vessel='{2}' pid={3} " +
                    "takeoverUT={4} lastRealUT={5} cutUT={6} removedSegments={7} clippedSegments={8} " +
                    "previousTerminal={9} previousTerminalPredicted={10} previousExplicitEndUT={11} " +
                    "crewEndStatesCleared={12} coast={13} coastReason={14} coastMiss={15} keptCoastSegments={16}",
                    context ?? "(none)",
                    rec.RecordingId ?? "(null)",
                    rec.VesselName ?? "",
                    rec.VesselPersistentId,
                    FormatUT(takeoverUT),
                    FormatUT(result.LastRealUT),
                    FormatUT(cutUT),
                    result.RemovedSegmentCount,
                    result.ClippedSegmentCount,
                    result.PreviousTerminal.HasValue ? result.PreviousTerminal.Value.ToString() : "null",
                    result.PreviousTerminalPredicted ? "true" : "false",
                    FormatUT(result.PreviousExplicitEndUT),
                    result.CrewEndStatesCleared ? "true" : "false",
                    result.CoastKeptToTakeover ? "kept-to-takeover" : "cut-at-last-real",
                    result.CoastDecision ?? "(none)",
                    IsFinite(result.CoastMissMeters)
                        ? result.CoastMissMeters.ToString("F1", CultureInfo.InvariantCulture) : "NaN",
                    result.KeptCoastSegmentCount));
            return result;
        }

        /// <summary>
        /// Pure: did the vessel follow the recording's predicted conic up to
        /// <paramref name="takeoverUT"/>? Compares the predicted segment that covers the takeover
        /// with the live vessel's orbit (<paramref name="evidence"/>), both propagated in their
        /// stored element frame to the takeover UT (no live frame read), against
        /// <see cref="CoastMatchMinToleranceMeters"/> /
        /// <see cref="CoastMatchRelativeTolerance"/>. A different body, no covering predicted
        /// segment or an unpropagatable orbit is no match. <paramref name="reason"/> names the
        /// outcome; <paramref name="missMeters"/> is the distance, NaN when not measured.
        /// </summary>
        internal static bool CoastFollowedPredictedConic(
            Recording rec, double takeoverUT, TakeoverCoastEvidence evidence,
            out double missMeters, out string reason)
        {
            missMeters = double.NaN;
            if (rec?.OrbitSegments == null || !IsFinite(takeoverUT))
            {
                reason = "no-recording";
                return false;
            }
            bool found = false;
            OrbitSegment predicted = default(OrbitSegment);
            for (int i = 0; i < rec.OrbitSegments.Count; i++)
            {
                OrbitSegment seg = rec.OrbitSegments[i];
                if (!seg.isPredicted)
                    continue;
                if (seg.startUT - UtEpsilon <= takeoverUT && takeoverUT <= seg.endUT + UtEpsilon)
                {
                    predicted = seg;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                reason = "no-predicted-segment-at-takeover";
                return false;
            }
            if (!string.Equals(predicted.bodyName, evidence.LiveOrbit.bodyName, StringComparison.Ordinal))
            {
                reason = "body-mismatch";
                return false;
            }
            if (!(evidence.GravParameter > 0.0) || !IsFinite(evidence.GravParameter)
                || !BallisticExtrapolator.TwoBodyOrbit.TryCreateFromSegmentStoredFrame(
                    predicted, evidence.GravParameter, out BallisticExtrapolator.TwoBodyOrbit predictedOrbit)
                || !BallisticExtrapolator.TwoBodyOrbit.TryCreateFromSegmentStoredFrame(
                    evidence.LiveOrbit, evidence.GravParameter, out BallisticExtrapolator.TwoBodyOrbit liveOrbit))
            {
                reason = "orbit-not-propagatable";
                return false;
            }
            Vector3d a = predictedOrbit.GetPositionAtUT(takeoverUT);
            Vector3d b = liveOrbit.GetPositionAtUT(takeoverUT);
            double miss = (a - b).magnitude;
            if (!IsFinite(miss))
            {
                reason = "orbit-not-propagatable";
                return false;
            }
            missMeters = miss;
            double tolerance = Math.Max(CoastMatchMinToleranceMeters, CoastMatchRelativeTolerance * a.magnitude);
            if (miss > tolerance)
            {
                reason = "off-the-predicted-conic";
                return false;
            }
            reason = "on-the-predicted-conic";
            return true;
        }

        /// <summary>
        /// Pure: the leaf of <paramref name="tree"/> a takeover of the live vessel
        /// (<paramref name="livePid"/>, launch <paramref name="liveGuid"/>, "N" form or null)
        /// continues from: the one PID-coherent terminal leaf of that launch whose ending is a
        /// prediction (<see cref="HasPredictedEnding"/>). Null when there is none or more than
        /// one, so the caller keeps its old route. Read-only.
        /// </summary>
        internal static string ResolveTakeoverLeafInTree(RecordingTree tree, uint livePid, string liveGuid)
        {
            if (tree?.Recordings == null || livePid == 0)
                return null;
            string found = null;
            foreach (KeyValuePair<string, Recording> kvp in tree.Recordings)
            {
                Recording rec = kvp.Value;
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                if (!VesselLaunchIdentity.LiveVesselIsRecordedLaunch(rec, livePid, liveGuid))
                    continue;
                if (!string.IsNullOrEmpty(rec.ChildBranchPointId)
                    && !RecordingTree.IsVesselLineEnd(rec, tree.Recordings, tree.BranchPoints))
                {
                    continue;
                }
                if (!HasPredictedEnding(rec))
                    continue;
                if (found != null)
                {
                    ParsekLog.Info(LogTag,
                        string.Format(CultureInfo.InvariantCulture,
                            "Takeover leaf ambiguous: tree={0} pid={1} candidates={2},{3}",
                            tree.Id ?? "(null)", livePid, found, rec.RecordingId));
                    return null;
                }
                found = rec.RecordingId;
            }
            return found;
        }

        /// <summary>
        /// Pure, cheap enough for the per-frame playback pass: should the ghost of
        /// <paramref name="rec"/> stay hidden at <paramref name="currentUT"/> because it is
        /// past the last real sample, on the recording's predicted tail, while the vessel
        /// that tail predicts is still in the world (<paramref name="realVesselExists"/>, the
        /// launch-gated check)? The live vessel is then the truth, and a ghost on the
        /// prediction would duplicate it. Before the last real sample the recorded flight
        /// replays as before.
        /// </summary>
        internal static bool ShouldHidePredictedTailGhostWhileVesselLives(
            Recording rec, double currentUT, Func<Recording, bool> realVesselExists)
        {
            if (rec?.OrbitSegments == null || rec.OrbitSegments.Count == 0 || realVesselExists == null)
                return false;
            if (!IsFinite(currentUT))
                return false;
            // The predicted tail is a suffix: an untouched last segment means no tail.
            if (!rec.OrbitSegments[rec.OrbitSegments.Count - 1].isPredicted)
                return false;
            double tailStartUT = double.NaN;
            for (int i = rec.OrbitSegments.Count - 1; i >= 0; i--)
            {
                OrbitSegment seg = rec.OrbitSegments[i];
                if (!seg.isPredicted)
                    break;
                tailStartUT = seg.startUT;
            }
            if (!IsFinite(tailStartUT) || currentUT <= tailStartUT + UtEpsilon)
                return false;
            return realVesselExists(rec);
        }

        private static int CountPredictedSegmentsPast(Recording rec, double lastRealUT)
        {
            if (rec?.OrbitSegments == null)
                return 0;
            int count = 0;
            for (int i = 0; i < rec.OrbitSegments.Count; i++)
            {
                OrbitSegment seg = rec.OrbitSegments[i];
                if (seg.isPredicted && seg.endUT > lastRealUT + UtEpsilon)
                    count++;
            }
            return count;
        }

        private static bool IsNeverPredictedTerminal(TerminalState? terminal)
        {
            return terminal.HasValue
                && (terminal.Value == TerminalState.Recovered
                    || terminal.Value == TerminalState.Docked
                    || terminal.Value == TerminalState.Boarded
                    || !Enum.IsDefined(typeof(TerminalState), terminal.Value));
        }

        private static void ClearTerminalOrbit(Recording rec)
        {
            rec.TerminalOrbitInclination = 0.0;
            rec.TerminalOrbitEccentricity = 0.0;
            rec.TerminalOrbitSemiMajorAxis = 0.0;
            rec.TerminalOrbitLAN = 0.0;
            rec.TerminalOrbitArgumentOfPeriapsis = 0.0;
            rec.TerminalOrbitMeanAnomalyAtEpoch = 0.0;
            rec.TerminalOrbitEpoch = 0.0;
            rec.TerminalOrbitBody = null;
        }

        private static void LogSkip(Recording rec, string context, string reason)
        {
            ParsekLog.Verbose(LogTag,
                string.Format(CultureInfo.InvariantCulture,
                    "Predicted ending kept for takeover: context={0} rec={1} reason={2} terminal={3}",
                    context ?? "(none)",
                    rec?.RecordingId ?? "(null)",
                    reason,
                    rec?.TerminalStateValue?.ToString() ?? "null"));
        }

        private static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        private static string FormatUT(double v)
        {
            return IsFinite(v) ? v.ToString("F3", CultureInfo.InvariantCulture) : "NaN";
        }
    }
}
