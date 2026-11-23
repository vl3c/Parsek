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
        public int RemovedDescentPointCount;
        // Takeover after the end: predicted descent sections kept as recorded history.
        public int KeptDescentSectionCount;
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
        /// Where the RECORDED part of <paramref name="rec"/> ends: its last UT that is not Parsek's
        /// prediction. Loop and supply-route timing reads this end, playback reads
        /// <see cref="Recording.EndUT"/> (owner ruling 2026-11-24: a dropped stage's predicted fall plays
        /// in mission loops but does not count toward loop timing). Read from its data in this order:
        /// <list type="bullet">
        /// <item>no predicted tail: <see cref="Recording.EndUT"/> unchanged (an explicit end a close or
        /// a commit stamped past the last sample is recorded time: a packed lander records no
        /// samples);</item>
        /// <item>a predicted tail: the last real sample, section or non-predicted orbit segment
        /// (<see cref="RecordingFinalizationCacheApplier.TryGetLastAuthoredUT"/>), never past
        /// <see cref="Recording.EndUT"/>;</item>
        /// <item>predicted data only (a piece cut out of a tail): NaN, the recording holds no recorded
        /// time.</item>
        /// </list>
        /// The tail shapes are one rule: a fall to impact, an escape followed to its final orbit (the
        /// settle span included), a ground-scan or horizon cap and a predicted coast that stays in
        /// orbit are predicted orbit segments; a chute descent is a predicted track section; a
        /// predicted crash UT kept with no segment is a predicted Destroyed ending past the last
        /// sample.
        /// </summary>
        internal static double ResolveRecordedEndUT(Recording rec)
        {
            if (rec == null)
                return double.NaN;
            double endUT = rec.EndUT;
            // Most recordings carry no prediction at all: answered without walking their samples.
            if (!HasPredictedData(rec))
                return endUT;
            bool hasRecorded = RecordingFinalizationCacheApplier.TryGetLastAuthoredUT(rec, out double lastRecordedUT)
                && IsFinite(lastRecordedUT);
            if (!HasPredictedTailPast(rec, hasRecorded ? lastRecordedUT : double.NegativeInfinity))
                return endUT;
            if (!hasRecorded)
                return double.NaN;
            return lastRecordedUT < endUT ? lastRecordedUT : endUT;
        }

        private static bool HasPredictedData(Recording rec)
        {
            return rec.TerminalPredicted || HasPredictedPayloadPast(rec, double.NegativeInfinity);
        }

        /// <summary>
        /// True when <paramref name="rec"/> carries a predicted tail past
        /// <paramref name="lastRecordedUT"/>: a predicted orbit segment or predicted descent section
        /// ending after it, or a predicted Destroyed ending stamped past it. A breakup remnant KSP
        /// keeps carries the predicted flag for its crew only and forecasts nothing. Pure.
        /// </summary>
        internal static bool HasPredictedTailPast(Recording rec, double lastRecordedUT)
        {
            if (rec == null)
                return false;
            if (HasPredictedPayloadPast(rec, lastRecordedUT + UtEpsilon))
                return true;
            return rec.TerminalPredicted
                && rec.TerminalStateValue == TerminalState.Destroyed
                && !IsSurvivingBreakupRemnant(rec)
                && !double.IsNegativeInfinity(lastRecordedUT)
                && rec.ExplicitEndUT > lastRecordedUT
                    + PredictedTerminalUTToleranceSeconds;
        }

        private static bool HasPredictedPayloadPast(Recording rec, double ut)
        {
            if (rec.OrbitSegments != null)
                for (int i = rec.OrbitSegments.Count - 1; i >= 0; i--)
                    if (rec.OrbitSegments[i].isPredicted && rec.OrbitSegments[i].endUT > ut)
                        return true;
            if (rec.TrackSections != null)
                for (int i = rec.TrackSections.Count - 1; i >= 0; i--)
                    if (RecordingFinalizationCacheApplier.IsPredictedSection(rec.TrackSections[i])
                        && rec.TrackSections[i].endUT > ut)
                        return true;
            return false;
        }

        /// <summary>
        /// Pure: a Destroyed ending sealed over a breakup remnant KSP keeps
        /// (<see cref="Recording.TerminalRemnantSurvives"/>). It carries the predicted flag only
        /// so the remnant's crew are decided per part (owner ruling Q6b); it forecasts no crash,
        /// so a remnant stock still holds is no contradiction (and a pass that "applied" one
        /// would re-stamp the same identity loss and commit an unchanged tree on every scene).
        /// </summary>
        internal static bool IsSurvivingBreakupRemnant(Recording rec)
        {
            return rec != null
                && rec.TerminalStateValue == TerminalState.Destroyed
                && rec.TerminalRemnantSurvives;
        }

        /// <summary>
        /// Pure: does the recording end in a prediction a takeover must drop? Decided from
        /// provenance: a predicted terminal (<see cref="Recording.TerminalPredicted"/>), a
        /// predicted descent section (<see cref="TrackSectionSource.Predicted"/>, which is what
        /// makes a chute touchdown's Landed / Splashed a prediction) or predicted orbit segments
        /// past the last real sample. The one end-UT clue kept is a
        /// Destroyed terminal stamped well past the last real sample (a background close that
        /// kept a predicted crash UT with no segment; a confirmed crash leaves no vessel to take
        /// over). A Landed / Splashed / Orbiting / SubOrbital end past the last sample is NOT a
        /// prediction: the background recorder moves every member's explicit end to the commit
        /// UT and a packed landed vessel records no samples, so a lander committed an hour after
        /// touchdown carries an end an hour past its last sample. Never for Recovered / Docked /
        /// Boarded, which no prediction writes.
        /// </summary>
        internal static bool HasPredictedEnding(Recording rec)
        {
            if (rec == null)
                return false;
            if (IsNeverPredictedTerminal(rec.TerminalStateValue))
                return false;
            // A breakup remnant KSP keeps is not a prediction: its Destroyed sealed the loss of
            // the recorded controllers, and its crew are decided per part (owner ruling Q6b).
            if (IsSurvivingBreakupRemnant(rec))
                return false;
            if (rec.TerminalPredicted)
                return true;
            // A predicted descent section (a chute descent to a Landed / Splashed touchdown,
            // or to a crash) is provenance like a predicted segment.
            if (RecordingFinalizationCacheApplier.TryGetPredictedSectionStartUT(rec, out _))
                return true;
            if (!TryGetLastRealUT(rec, out double lastRealUT))
                return false;
            if (CountPredictedSegmentsPast(rec, lastRealUT) > 0)
                return true;
            return rec.TerminalStateValue == TerminalState.Destroyed
                && IsFinite(rec.ExplicitEndUT)
                && rec.ExplicitEndUT > lastRealUT + PredictedTerminalUTToleranceSeconds;
        }

        /// <summary>
        /// Pure: did the takeover at <paramref name="takeoverUT"/> come at or after the end of
        /// a predicted ending that leaves the vessel in the game (Landed, Splashed, Orbiting,
        /// SubOrbital)? Then the replay reached that ending and spawned the vessel there (a chute
        /// touchdown, a Q8 final orbit), so the predicted tail already happened in the replayed
        /// timeline. A predicted crash never qualifies: a vessel alive after its predicted crash
        /// UT means stock contradicted the prediction. <paramref name="predictedEndUT"/> is the
        /// recording's end (its explicit end or its last payload, predicted data included).
        /// </summary>
        internal static bool IsTakeoverAfterPredictedEnd(
            Recording rec, double takeoverUT, out double predictedEndUT)
        {
            predictedEndUT = double.NaN;
            if (rec == null || !rec.TerminalStateValue.HasValue || !IsFinite(takeoverUT))
                return false;
            TerminalState terminal = rec.TerminalStateValue.Value;
            if (terminal != TerminalState.Landed && terminal != TerminalState.Splashed
                && terminal != TerminalState.Orbiting && terminal != TerminalState.SubOrbital)
                return false;
            double endUT = rec.EndUT;
            if (!IsFinite(endUT) || endUT <= 0.0)
                return false;
            if (TryGetLastRealUT(rec, out double lastRealUT) && !(endUT > lastRealUT + UtEpsilon))
                return false;
            predictedEndUT = endUT;
            return takeoverUT + UtEpsilon >= endUT;
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
            TakeoverCoastEvidence? coastEvidence = null,
            bool predictionContradicted = false)
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
            if (!predictionContradicted
                && IsTakeoverAfterPredictedEnd(rec, takeoverUT, out double predictedEndUT))
                return KeepPredictedTailForTakeoverAfterEnd(rec, takeoverUT, predictedEndUT, context, result);
            double cutUT = hasLastReal ? lastRealUT : takeoverUT;
            if (IsFinite(takeoverUT) && (!IsFinite(cutUT) || takeoverUT < cutUT))
                cutUT = takeoverUT;
            DecideCoastToTakeover(rec, hasLastReal, lastRealUT, takeoverUT, coastEvidence, ref result);
            if (result.CoastKeptToTakeover)
                cutUT = takeoverUT;
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
                    result.KeptCoastSegmentCount = MarkKeptCoastRecorded(rec, cutUT);
            }

            result.RemovedDescentPointCount = RecordingFinalizationCacheApplier.RemovePredictedDescent(
                rec, "PredictedEndingTakeover:" + (context ?? "(none)"));

            if (rec.TerminalStateValue.HasValue)
            {
                rec.StampTerminalState(null, "PredictedEndingTakeover:" + (context ?? "(none)"));
                result.TerminalCleared = true;
                result.CrewEndStatesCleared = ClearDroppedEndingCrewAnswer(rec, result.PreviousTerminal);
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
                    "crewEndStatesCleared={12} coast={13} coastReason={14} coastMiss={15} keptCoastSegments={16} " +
                    "removedDescentPoints={17}",
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
                    result.KeptCoastSegmentCount,
                    result.RemovedDescentPointCount));
            return result;
        }

        /// <summary>
        /// The takeover-after-the-end branch of <see cref="DropPredictedEndingForTakeover"/>
        /// (<see cref="IsTakeoverAfterPredictedEnd"/>): the whole predicted tail stays and
        /// becomes recorded history (every predicted segment loses <c>isPredicted</c>, every
        /// predicted descent section becomes a Background section); only
        /// the ending goes (terminal, predicted flag, terminal orbit and surface metadata, the
        /// crew answer it decided), so the flight continues from the spawn and its commit
        /// stamps the real ending. Logs one Info line.
        /// </summary>
        private static PredictedEndingDropResult KeepPredictedTailForTakeoverAfterEnd(
            Recording rec, double takeoverUT, double predictedEndUT, string context,
            PredictedEndingDropResult result)
        {
            result.CutUT = double.NaN;
            result.KeptCoastSegmentCount = MarkWholePredictedTailRecorded(
                rec, out result.KeptDescentSectionCount);
            rec.StampTerminalState(null, "PredictedEndingTakeover.AfterEnd:" + (context ?? "(none)"));
            result.TerminalCleared = true;
            result.CrewEndStatesCleared = ClearDroppedEndingCrewAnswer(rec, result.PreviousTerminal);
            rec.VesselDestroyed = false;
            ClearTerminalOrbit(rec);
            rec.TerminalPosition = null;
            rec.TerrainHeightAtEnd = double.NaN;

            RecordingEndpointResolver.RefreshEndpointDecision(
                rec, "PredictedEndingTakeover", logDecision: false);
            rec.MarkFilesDirty();

            result.Dropped = false;
            result.Reason = "takeover-after-predicted-end";
            ParsekLog.Info(LogTag,
                string.Format(CultureInfo.InvariantCulture,
                    "Predicted ending kept for takeover after its end: context={0} rec={1} vessel='{2}' pid={3} " +
                    "takeoverUT={4} lastRealUT={5} predictedEndUT={6} keptSegments={7} previousTerminal={8} " +
                    "previousTerminalPredicted={9} crewEndStatesCleared={10} keptDescentSections={11} " +
                    "(the replay reached the ending and spawned the vessel; the flight continues from the spawn)",
                    context ?? "(none)",
                    rec.RecordingId ?? "(null)",
                    rec.VesselName ?? "",
                    rec.VesselPersistentId,
                    FormatUT(takeoverUT),
                    FormatUT(result.LastRealUT),
                    FormatUT(predictedEndUT),
                    result.KeptCoastSegmentCount,
                    result.PreviousTerminal.HasValue ? result.PreviousTerminal.Value.ToString() : "null",
                    result.PreviousTerminalPredicted ? "true" : "false",
                    result.CrewEndStatesCleared ? "true" : "false",
                    result.KeptDescentSectionCount));
            return result;
        }

        /// <summary>
        /// Every predicted segment of <paramref name="rec"/> becomes recorded history (the
        /// takeover-after-the-end branch), and so does every predicted descent section
        /// (<see cref="TrackSectionSource.Predicted"/> becomes
        /// <see cref="TrackSectionSource.Background"/>; its frames and the flat points it added
        /// are unchanged), so the last real sample now counts the descent. Returns how many
        /// segments it marked; <paramref name="sectionsMarked"/> is how many sections.
        /// </summary>
        private static int MarkWholePredictedTailRecorded(Recording rec, out int sectionsMarked)
        {
            sectionsMarked = 0;
            if (rec == null)
                return 0;
            if (rec.TrackSections != null)
            {
                for (int i = 0; i < rec.TrackSections.Count; i++)
                {
                    TrackSection section = rec.TrackSections[i];
                    if (!RecordingFinalizationCacheApplier.IsPredictedSection(section))
                        continue;
                    section.source = TrackSectionSource.Background;
                    rec.TrackSections[i] = section;
                    sectionsMarked++;
                }
                if (sectionsMarked > 0)
                {
                    rec.CachedStats = null;
                    rec.CachedStatsPointCount = 0;
                }
            }
            if (rec.OrbitSegments == null)
                return 0;
            int marked = 0;
            for (int i = 0; i < rec.OrbitSegments.Count; i++)
            {
                OrbitSegment seg = rec.OrbitSegments[i];
                if (!seg.isPredicted)
                    continue;
                seg.isPredicted = false;
                rec.OrbitSegments[i] = seg;
                marked++;
            }
            return marked;
        }

        /// <summary>
        /// The coast decision of <see cref="DropPredictedEndingForTakeover"/>: with live evidence
        /// and a takeover after the last real sample, keeps the coast to the takeover when the
        /// vessel is still on the predicted conic (<see cref="CoastFollowedPredictedConic"/>).
        /// Writes <see cref="PredictedEndingDropResult.CoastKeptToTakeover"/>, the reason and the
        /// miss into <paramref name="result"/>.
        /// </summary>
        private static void DecideCoastToTakeover(
            Recording rec, bool hasLastReal, double lastRealUT, double takeoverUT,
            TakeoverCoastEvidence? coastEvidence, ref PredictedEndingDropResult result)
        {
            result.CoastMissMeters = double.NaN;
            if (!coastEvidence.HasValue)
            {
                result.CoastDecision = "no-live-evidence";
                return;
            }
            if (!hasLastReal || !IsFinite(takeoverUT) || takeoverUT <= lastRealUT + UtEpsilon)
            {
                result.CoastDecision = "takeover-not-after-last-real";
                return;
            }
            result.CoastKeptToTakeover = CoastFollowedPredictedConic(
                rec, takeoverUT, coastEvidence.Value, out double miss, out string coastReason);
            result.CoastMissMeters = miss;
            result.CoastDecision = coastReason;
        }

        /// <summary>
        /// The kept coast is what the vessel did (stock moved it on that conic): every predicted
        /// segment ending at or before <paramref name="cutUT"/> becomes recorded history.
        /// Returns how many it marked.
        /// </summary>
        private static int MarkKeptCoastRecorded(Recording rec, double cutUT)
        {
            int marked = 0;
            for (int i = 0; i < rec.OrbitSegments.Count; i++)
            {
                OrbitSegment seg = rec.OrbitSegments[i];
                if (!seg.isPredicted || seg.endUT > cutUT + UtEpsilon)
                    continue;
                seg.isPredicted = false;
                rec.OrbitSegments[i] = seg;
                marked++;
            }
            return marked;
        }

        /// <summary>
        /// The crew answer the dropped ending decided (a predicted crash kills the crew, owner
        /// ruling Q6c) is re-derived from the real flight's ending at its commit, so it goes with
        /// the ending; after a Destroyed the death respawn stamp belonged to that death and goes
        /// too. Returns true when anything was cleared.
        /// </summary>
        private static bool ClearDroppedEndingCrewAnswer(Recording rec, TerminalState? previousTerminal)
        {
            bool cleared = rec.CrewEndStates != null || rec.CrewEndStatesResolved;
            rec.CrewEndStates = null;
            rec.CrewEndStatesResolved = false;
            if (previousTerminal == TerminalState.Destroyed && rec.CrewDeathRespawns.HasValue)
            {
                rec.CrewDeathRespawns = null;
                rec.CrewDeathRespawnSeconds = double.NaN;
                cleared = true;
            }
            return cleared;
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
