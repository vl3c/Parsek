using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // Background membership reconciliation: closing members missing at load and the background state drift check.
    internal partial class BackgroundRecorder
    {
        /// <summary>
        /// One background member whose vessel is not in the loaded scene when the recorder
        /// is rebuilt from a restored tree.
        /// </summary>
        internal struct MissingBackgroundMember
        {
            public uint VesselPid;
            public string RecordingId;
            public bool IsDebris;
        }

        /// <summary>
        /// Pure selection for the load-time check: every <c>BackgroundMap</c> entry whose
        /// recording exists, is not already sealed Destroyed, and whose vessel pid (and, for a
        /// recording Parsek already materialized, its spawned pid) is not in the loaded scene.
        /// The usual cause is stock's save-time vessel budget (<c>MAX_VESSELS_BUDGET</c>, which
        /// drops non-persistent Debris-typed vessels). Sorted by pid so the summary line is
        /// stable.
        /// </summary>
        internal static List<MissingBackgroundMember> FindMissingBackgroundMembers(
            RecordingTree tree,
            Func<uint, bool> vesselExists)
        {
            var missing = new List<MissingBackgroundMember>();
            if (tree == null || tree.BackgroundMap == null || tree.Recordings == null
                || vesselExists == null)
                return missing;

            foreach (var kvp in tree.BackgroundMap)
            {
                Recording rec;
                if (string.IsNullOrEmpty(kvp.Value)
                    || !tree.Recordings.TryGetValue(kvp.Value, out rec)
                    || rec == null)
                    continue; // map/recording drift; reported by the drift check
                if (rec.VesselDestroyed)
                    continue; // already sealed; the ctor gives it no tracking state
                if (vesselExists(kvp.Key))
                    continue;
                if (rec.SpawnedVesselPersistentId != 0
                    && vesselExists(rec.SpawnedVesselPersistentId))
                    continue; // the real vessel lives on under its spawn pid
                missing.Add(new MissingBackgroundMember
                {
                    VesselPid = kvp.Key,
                    RecordingId = kvp.Value,
                    IsDebris = rec.IsDebris
                });
            }

            missing.Sort((a, b) => a.VesselPid.CompareTo(b.VesselPid));
            return missing;
        }

        /// <summary>How the load check closed one missing background member.</summary>
        internal enum MissingMemberCloseOutcome
        {
            /// <summary>
            /// The recording already carries a terminal state, written by the event that
            /// removed the vessel (the KSC declutter autoclean fires <c>onVesselRecovered</c>,
            /// which <c>ParsekScenario.UpdateRecordingsForTerminalEvent</c> stamps Recovered).
            /// The terminal and <c>ExplicitEndUT</c> stay as that path wrote them.
            /// </summary>
            LeftToTerminalEvent = 0,

            /// <summary>
            /// No event ended it (vessel-budget prune): closed at its last known UT with the
            /// situation its own recorded data supports.
            /// </summary>
            ClosedAtLastKnownUT = 1,

            /// <summary>
            /// No event ended it and its recorded state was a fall or an open coast: it follows
            /// that path from its last recorded orbit or sample to the ending the path reaches
            /// (a predicted impact, or the coast at the extrapolation horizon), the tail a
            /// missing-vessel commit applies from a finalization cache
            /// (<see cref="TryBuildDroppedMemberRecordedTail"/>).
            /// </summary>
            ClosedOnRecordedTail = 2
        }

        internal struct MissingMemberClosePlan
        {
            public MissingMemberCloseOutcome Outcome;
            public TerminalState Terminal;
            public double EndUT;
            public string Evidence;
        }

        /// <summary>
        /// Pure decision for a background member stock left out of the save. A recording that
        /// already has a terminal state is left to the event that set it. Otherwise it closes
        /// at its current <see cref="Recording.EndUT"/> (the last UT the previous session
        /// knew the vessel existed; nothing has advanced it since the load) with
        /// <see cref="ResolveDroppedMemberTerminal"/>. Never Destroyed: the vessel was
        /// dropped, not destroyed, so its replay must not end in an explosion.
        /// </summary>
        internal static MissingMemberClosePlan DecideMissingMemberClose(Recording rec)
        {
            var plan = new MissingMemberClosePlan
            {
                Outcome = MissingMemberCloseOutcome.ClosedAtLastKnownUT,
                Terminal = TerminalState.SubOrbital,
                EndUT = double.NaN,
                Evidence = "no-recording"
            };
            if (rec == null)
                return plan;

            plan.EndUT = rec.EndUT;
            if (rec.TerminalStateValue.HasValue)
            {
                plan.Outcome = MissingMemberCloseOutcome.LeftToTerminalEvent;
                plan.Terminal = rec.TerminalStateValue.Value;
                plan.Evidence = "existing-terminal";
                return plan;
            }

            string evidence;
            plan.Terminal = ResolveDroppedMemberTerminal(rec, out evidence);
            plan.Evidence = evidence;
            return plan;
        }

        /// <summary>
        /// Situation terminal for a dropped member from its last recorded state:
        /// <list type="number">
        /// <item>The last orbit segment, when it ends at or after the last flat point:
        /// Orbiting when its periapsis clears the body's periapsis floor (the atmosphere top,
        /// or the highest terrain on an airless body; <see cref="OrbitClearance"/>), else
        /// SubOrbital.</item>
        /// <item>No flat points: the on-rails surface capture (<c>SurfacePos</c>, Landed or
        /// Splashed), else SubOrbital.</item>
        /// <item>Otherwise the scene-exit inference over the last point
        /// (<see cref="ParsekFlight.InferTerminalStateFromTrajectory"/>), with the on-rails
        /// surface capture refining Landed to Splashed and standing in for its
        /// no-evidence SubOrbital default.</item>
        /// </list>
        /// </summary>
        internal static TerminalState ResolveDroppedMemberTerminal(Recording rec, out string evidence)
        {
            evidence = "no-trajectory";
            if (rec == null)
                return TerminalState.SubOrbital;

            bool hasPoints = rec.Points != null && rec.Points.Count > 0;
            double lastPointUT = hasPoints
                ? rec.Points[rec.Points.Count - 1].ut
                : double.NegativeInfinity;
            bool hasSurface = rec.SurfacePos.HasValue;
            TerminalState surfaceTerminal = hasSurface
                && rec.SurfacePos.Value.situation == SurfaceSituation.Splashed
                    ? TerminalState.Splashed
                    : TerminalState.Landed;

            if (rec.OrbitSegments != null && rec.OrbitSegments.Count > 0)
            {
                OrbitSegment lastOrbit = rec.OrbitSegments[rec.OrbitSegments.Count - 1];
                if (lastOrbit.endUT >= lastPointUT)
                {
                    if (ParsekFlight.HasStableOrbitEvidenceForTerminalInference(lastOrbit))
                    {
                        evidence = "last-orbit-segment";
                        return TerminalState.Orbiting;
                    }
                    evidence = "last-orbit-segment-unstable";
                    return TerminalState.SubOrbital;
                }
            }

            if (!hasPoints)
            {
                if (hasSurface)
                {
                    evidence = "surface-position";
                    return surfaceTerminal;
                }
                return TerminalState.SubOrbital;
            }

            TerminalState inferred = ParsekFlight.InferTerminalStateFromTrajectory(rec);
            if (hasSurface
                && (inferred == TerminalState.SubOrbital || inferred == TerminalState.Landed))
            {
                evidence = "last-point+surface-position";
                return surfaceTerminal;
            }
            evidence = "last-point";
            return inferred;
        }

        internal static string FormatMissingBackgroundMembersSummary(
            List<MissingBackgroundMember> missing,
            List<MissingMemberClosePlan> plans,
            int backgroundMapCount,
            string reason)
        {
            int debris = 0;
            int closed = 0;
            int onTail = 0;
            int leftToEvent = 0;
            var pids = new List<string>();
            var ids = new List<string>();
            var outcomes = new List<string>();
            if (missing != null)
            {
                for (int i = 0; i < missing.Count; i++)
                {
                    if (missing[i].IsDebris)
                        debris++;
                    pids.Add(missing[i].VesselPid.ToString(CultureInfo.InvariantCulture));
                    ids.Add(string.IsNullOrEmpty(missing[i].RecordingId)
                        ? "(null)"
                        : missing[i].RecordingId);
                    if (plans == null || i >= plans.Count)
                    {
                        outcomes.Add("unplanned");
                        continue;
                    }
                    MissingMemberClosePlan plan = plans[i];
                    string endText = double.IsNaN(plan.EndUT)
                        ? "NaN"
                        : plan.EndUT.ToString("F1", CultureInfo.InvariantCulture);
                    if (plan.Outcome == MissingMemberCloseOutcome.LeftToTerminalEvent)
                    {
                        leftToEvent++;
                        outcomes.Add(string.Format(CultureInfo.InvariantCulture,
                            "{0}:kept-{1}@{2}", missing[i].VesselPid, plan.Terminal, endText));
                    }
                    else if (plan.Outcome == MissingMemberCloseOutcome.ClosedOnRecordedTail)
                    {
                        onTail++;
                        outcomes.Add(string.Format(CultureInfo.InvariantCulture,
                            "{0}:tail-{1}@{2}({3})",
                            missing[i].VesselPid, plan.Terminal, endText, plan.Evidence));
                    }
                    else
                    {
                        closed++;
                        outcomes.Add(string.Format(CultureInfo.InvariantCulture,
                            "{0}:{1}@{2}({3})",
                            missing[i].VesselPid, plan.Terminal, endText, plan.Evidence));
                    }
                }
            }

            return string.Format(CultureInfo.InvariantCulture,
                "Load check: {0} of {1} background member(s) have no vessel in the loaded " +
                "scene (debris={2}; likely dropped by the stock vessel budget or KSC " +
                "declutter at save): reason={3} pids=[{4}] recIds=[{5}] - closed={6} " +
                "(situation terminal at last known UT, no Destroyed) keptTerminal={7} " +
                "(already ended by a recovery/termination event) followedRecordedTail={9} " +
                "(predicted ending from the member's own recorded path) outcomes=[{8}]",
                missing != null ? missing.Count : 0,
                backgroundMapCount,
                debris,
                string.IsNullOrEmpty(reason) ? "unspecified" : reason,
                string.Join(",", pids.ToArray()),
                string.Join(",", ids.ToArray()),
                closed,
                leftToEvent,
                string.Join(",", outcomes.ToArray()),
                onTail);
        }

        /// <summary>
        /// Load-time check run once when a recorder is attached to a restored tree: every
        /// background member whose vessel is gone (stock left it out of the save) stops
        /// being tracked, so <see cref="UpdateOnRails"/> and
        /// <see cref="FinalizeAllForCommit"/> no longer advance its <c>ExplicitEndUT</c> past
        /// the save, and ONE summary line names each member and how it was closed.
        /// A member with no terminal yet is stamped with its situation terminal at its last
        /// known UT (<see cref="DecideMissingMemberClose"/>); one that already carries a
        /// terminal (a declutter autoclean the recovery path stamped Recovered) keeps it.
        /// A member whose own recorded state is a fall or an open coast (the situation rule
        /// says SubOrbital) instead follows that path to its natural ending, applied as a
        /// finalization cache built from its last recorded orbit or sample
        /// (<see cref="TryBuildDroppedMemberRecordedTail"/>), so its ghost does not stop in
        /// mid-air at the save. A predicted impact is marked predicted, not observed.
        /// This deliberately does not route through <see cref="EndDebrisRecording"/>: a
        /// restored member has no finalization cache, so that path stamps Destroyed at the
        /// drop itself, which would replay a pruned debris as an explosion in mid-air.
        /// No sidecar write here: an applied tail marks the files dirty, and the terminal
        /// fields travel in the tree metadata the next save writes.
        /// Returns the missing count.
        /// </summary>
        internal int CloseMissingBackgroundMembersAtLoad(Func<uint, bool> vesselExists, string reason)
        {
            return CloseMissingBackgroundMembersAtLoad(
                vesselExists,
                reason,
                ResolveLiveDroppedMemberBodies,
                TryReseedLiveRecordedPoint);
        }

        /// <summary>
        /// The load-time check with its two live inputs injected: the body facts the
        /// extrapolator needs (resolved at most once, and only when a member has a recorded
        /// path to follow; null when unavailable) and the orbit reseed of a recorded sample
        /// (live: <see cref="IncompleteBallisticSceneExitFinalizer.TryBuildReseededSegmentFromRecordedPoint"/>;
        /// null when the reseed fails).
        /// </summary>
        internal int CloseMissingBackgroundMembersAtLoad(
            Func<uint, bool> vesselExists,
            string reason,
            Func<IReadOnlyDictionary<string, ExtrapolationBody>> resolveBodies,
            Func<Recording, TrajectoryPoint, OrbitSegment?> reseedPoint)
        {
            if (tree == null || tree.BackgroundMap == null || vesselExists == null)
                return 0;

            List<MissingBackgroundMember> missing = FindMissingBackgroundMembers(tree, vesselExists);
            if (missing.Count == 0)
            {
                ParsekLog.Verbose("BgRecorder",
                    string.Format(CultureInfo.InvariantCulture,
                        "Load check: all {0} background member(s) have a vessel in the loaded " +
                        "scene: reason={1}",
                        tree.BackgroundMap.Count,
                        string.IsNullOrEmpty(reason) ? "unspecified" : reason));
                return 0;
            }

            int mapCountBefore = tree.BackgroundMap.Count;
            var plans = new List<MissingMemberClosePlan>(missing.Count);
            IReadOnlyDictionary<string, ExtrapolationBody> bodies = null;
            bool bodiesResolved = false;
            for (int i = 0; i < missing.Count; i++)
            {
                Recording rec;
                tree.Recordings.TryGetValue(missing[i].RecordingId, out rec);
                MissingMemberClosePlan plan = DecideMissingMemberClose(rec);
                // The member stopped existing at its last known UT; nothing is appended there.
                double closeUT = plan.EndUT;

                if (rec != null
                    && plan.Outcome == MissingMemberCloseOutcome.ClosedAtLastKnownUT
                    && IsRecordedTailEvidence(plan.Evidence))
                {
                    if (!bodiesResolved)
                    {
                        bodiesResolved = true;
                        bodies = resolveBodies != null ? resolveBodies() : null;
                    }
                    TryCloseDroppedMemberOnRecordedTail(rec, bodies, reseedPoint, ref plan);
                }

                if (rec != null && plan.Outcome == MissingMemberCloseOutcome.ClosedAtLastKnownUT)
                {
                    rec.StampTerminalState(plan.Terminal, "BackgroundRecorder.droppedFromSave");
                    if (plan.Terminal == TerminalState.Orbiting
                        || plan.Terminal == TerminalState.SubOrbital)
                    {
                        ParsekFlight.PopulateTerminalOrbitFromLastSegment(rec);
                    }
                    else if (rec.SurfacePos.HasValue)
                    {
                        rec.TerminalPosition = rec.SurfacePos.Value;
                    }
                    else
                    {
                        ParsekFlight.PopulateTerminalPositionFromLastPoint(rec, plan.Terminal);
                    }
                }
                plans.Add(plan);

                // The normal "stops being recorded" shape: drop every per-vessel tracking
                // structure, then the map entry. The restored member's on-rails state has no
                // open orbit segment, so nothing is appended at closeUT.
                OnVesselRemovedFromBackground(missing[i].VesselPid, closeUT);
                tree.BackgroundMap.Remove(missing[i].VesselPid);
            }

            ParsekLog.Info("BgRecorder",
                FormatMissingBackgroundMembersSummary(missing, plans, mapCountBefore, reason));
            return missing.Count;
        }

        /// <summary>The refresh reason a dropped member's recorded-tail cache carries.</summary>
        internal const string DroppedFromSaveRefreshReason = "dropped_from_save";

        /// <summary>
        /// True for the two pieces of SubOrbital evidence a recorded path can be followed
        /// from: the last orbit segment (unbound, or its low point under the floor) and the
        /// last recorded sample with no surface reading. Every other verdict (Orbiting,
        /// Landed, Splashed, no trajectory) keeps the situation stamp.
        /// </summary>
        internal static bool IsRecordedTailEvidence(string evidence)
        {
            return string.Equals(evidence, "last-orbit-segment-unstable", StringComparison.Ordinal)
                || string.Equals(evidence, "last-point", StringComparison.Ordinal);
        }

        private static void TryCloseDroppedMemberOnRecordedTail(
            Recording rec,
            IReadOnlyDictionary<string, ExtrapolationBody> bodies,
            Func<Recording, TrajectoryPoint, OrbitSegment?> reseedPoint,
            ref MissingMemberClosePlan plan)
        {
            RecordingFinalizationCache cache;
            string source;
            string declineReason;
            if (!TryBuildDroppedMemberRecordedTail(
                    rec,
                    plan.Evidence,
                    bodies,
                    reseedPoint,
                    out cache,
                    out source,
                    out declineReason))
            {
                ParsekLog.Verbose("BgRecorder",
                    string.Format(CultureInfo.InvariantCulture,
                        "Load check: dropped member rec={0} pid={1} keeps the {2} stamp " +
                        "(evidence={3}; recorded tail declined: {4})",
                        rec.RecordingId ?? "(null)",
                        rec.VesselPersistentId,
                        plan.Terminal,
                        plan.Evidence ?? "(none)",
                        declineReason ?? "(none)"));
                return;
            }

            var options = new RecordingFinalizationCacheApplyOptions
            {
                ConsumerPath = "BackgroundRecorder.droppedFromSave",
                AllowStale = false
            };
            RecordingFinalizationCacheApplyResult result;
            if (!RecordingFinalizationCacheApplier.TryApply(rec, cache, options, out result))
            {
                ParsekLog.Verbose("BgRecorder",
                    string.Format(CultureInfo.InvariantCulture,
                        "Load check: dropped member rec={0} pid={1} keeps the {2} stamp " +
                        "(recorded tail from {3} rejected by the applier: {4})",
                        rec.RecordingId ?? "(null)",
                        rec.VesselPersistentId,
                        plan.Terminal,
                        source,
                        result.Status));
                return;
            }

            plan.Outcome = MissingMemberCloseOutcome.ClosedOnRecordedTail;
            plan.Terminal = result.TerminalState ?? plan.Terminal;
            plan.EndUT = result.TerminalUT;
            plan.Evidence = "recorded-tail:" + source;
            ParsekLog.Info("BgRecorder",
                string.Format(CultureInfo.InvariantCulture,
                    "Load check: dropped member rec={0} pid={1} follows its recorded path from " +
                    "{2}: terminal={3} terminalUT={4:F1} tailStartUT={5:F1} appendedSegments={6}",
                    rec.RecordingId ?? "(null)",
                    rec.VesselPersistentId,
                    source,
                    plan.Terminal,
                    result.TerminalUT,
                    cache.TailStartsAtUT,
                    result.AppendedSegmentCount));
        }

        /// <summary>
        /// Builds the finalization cache a dropped member would have carried had it been
        /// refreshed while its vessel existed, from its own recorded data only: the last
        /// orbit segment (evidence <c>last-orbit-segment-unstable</c>), propagated from its
        /// end, or the orbit reseeded from the last body-fixed recorded sample (evidence
        /// <c>last-point</c>), both run through
        /// <see cref="RecordingFinalizationCacheProducer.TryBuildFromStableOrbitSegment"/>
        /// (periapsis floor check, then the drag-free extrapolation to the ground, the
        /// deletion altitude, an SOI hand-off or the horizon). Adopted only when it carries a
        /// predicted tail ending Destroyed or SubOrbital: a predicted Orbiting, Landed or
        /// Splashed ending would make a vessel stock removed spawnable, so those keep the
        /// situation stamp. Declines a sample under an open parachute (no drag model), a
        /// Relative-section sample with no body-fixed reading, and a body with no facts
        /// (the floor could not be checked).
        /// </summary>
        internal static bool TryBuildDroppedMemberRecordedTail(
            Recording rec,
            string evidence,
            IReadOnlyDictionary<string, ExtrapolationBody> bodies,
            Func<Recording, TrajectoryPoint, OrbitSegment?> reseedPoint,
            out RecordingFinalizationCache cache,
            out string source,
            out string declineReason)
        {
            cache = null;
            source = null;
            declineReason = null;
            if (rec == null)
            {
                declineReason = "no-recording";
                return false;
            }
            if (bodies == null)
            {
                declineReason = "body-facts-unavailable";
                return false;
            }

            OrbitSegment segment;
            double refreshUT;
            if (string.Equals(evidence, "last-orbit-segment-unstable", StringComparison.Ordinal))
            {
                if (rec.OrbitSegments == null || rec.OrbitSegments.Count == 0)
                {
                    declineReason = "no-orbit-segment";
                    return false;
                }
                segment = rec.OrbitSegments[rec.OrbitSegments.Count - 1];
                refreshUT = IsFiniteUT(segment.endUT) && segment.endUT >= segment.startUT
                    ? segment.endUT
                    : segment.startUT;
                source = "last-orbit-segment";
            }
            else if (string.Equals(evidence, "last-point", StringComparison.Ordinal))
            {
                TrajectoryPoint point;
                if (!ParsekFlight.TryResolveLastPointBodyFixed(
                        rec, "Load check dropped member", out point))
                {
                    declineReason = "last-point-not-body-fixed";
                    return false;
                }
                uint chutePid;
                if (IncompleteBallisticSceneExitFinalizer.TryFindOpenParachuteAtUT(
                        rec, point.ut, out chutePid))
                {
                    declineReason = string.Format(CultureInfo.InvariantCulture,
                        "open-parachute pid={0}", chutePid);
                    return false;
                }
                OrbitSegment? reseeded = reseedPoint != null ? reseedPoint(rec, point) : null;
                if (!reseeded.HasValue)
                {
                    declineReason = reseedPoint == null ? "reseed-unavailable" : "reseed-failed";
                    return false;
                }
                segment = reseeded.Value;
                refreshUT = point.ut;
                source = "last-point";
            }
            else
            {
                declineReason = "evidence-" + (evidence ?? "(none)");
                return false;
            }

            if (!IsFiniteUT(refreshUT))
            {
                declineReason = "start-ut-non-finite";
                return false;
            }
            ExtrapolationBody body;
            if (string.IsNullOrEmpty(segment.bodyName)
                || !bodies.TryGetValue(segment.bodyName, out body)
                || body == null)
            {
                // TryBuildFromStableOrbitSegment would cache an unchecked segment as Orbiting.
                declineReason = "body-unknown:" + (segment.bodyName ?? "(null)");
                return false;
            }

            RecordingFinalizationCache built;
            bool success = RecordingFinalizationCacheProducer.TryBuildFromStableOrbitSegment(
                rec.RecordingId,
                rec.VesselPersistentId,
                segment,
                refreshUT,
                FinalizationCacheOwner.BackgroundOnRails,
                DroppedFromSaveRefreshReason,
                out built,
                bodies);
            if (!success || built == null || built.Status != FinalizationCacheStatus.Fresh)
            {
                declineReason = "no-cache:" + (built?.DeclineReason ?? "(none)");
                return false;
            }
            if (built.GroundScanEnd && built.TerminalState == TerminalState.Orbiting)
            {
                // A vessel stock dropped from the save is never made spawnable: the
                // ground-scan-end Orbiting ending (owner ruling 2026-11-22) keeps the floor's
                // SubOrbital verdict here, with its tail.
                built.GroundScanEnd = false;
                built.TerminalState = TerminalState.SubOrbital;
                ParsekLog.Verbose("BgRecorder", string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Dropped member {0}: ground-scan-end Orbiting kept SubOrbital (vessel gone from the save)",
                    rec.RecordingId ?? "(null)"));
            }
            if (!built.TerminalState.HasValue
                || (built.TerminalState.Value != TerminalState.Destroyed
                    && built.TerminalState.Value != TerminalState.SubOrbital))
            {
                declineReason = "ending-not-adopted:" + (built.TerminalState?.ToString() ?? "(null)");
                return false;
            }
            if (built.PredictedSegments == null || built.PredictedSegments.Count == 0)
            {
                declineReason = "no-predicted-tail";
                return false;
            }

            cache = built;
            return true;
        }

        // Body facts for the dropped-member tail, or null when FlightGlobals is not available
        // (headless tests, scene teardown). Kept out of line so the live read never JITs into
        // a caller a test drives.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static IReadOnlyDictionary<string, ExtrapolationBody> ResolveLiveDroppedMemberBodies()
        {
            try
            {
                if (!IncompleteBallisticSceneExitFinalizer.IsFlightGlobalsRuntimeAvailable("dropped-member"))
                    return null;
                Dictionary<string, ExtrapolationBody> bodies;
                return IncompleteBallisticSceneExitFinalizer.TryBuildExtrapolationBodies(
                        "dropped-member",
                        ReadLiveUniversalTimeForDroppedMember(),
                        out bodies)
                    ? bodies
                    : null;
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose("BgRecorder",
                    "Load check: body facts unavailable for dropped-member tails: " + ex.GetType().Name);
                return null;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static double ReadLiveUniversalTimeForDroppedMember()
        {
            return Planetarium.GetUniversalTime();
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static OrbitSegment? TryReseedLiveRecordedPoint(Recording recording, TrajectoryPoint point)
        {
            OrbitSegment segment;
            string failureReason;
            if (IncompleteBallisticSceneExitFinalizer.TryBuildReseededSegmentFromRecordedPoint(
                    recording,
                    point,
                    "dropped-member",
                    out segment,
                    out failureReason))
                return segment;
            ParsekLog.Verbose("BgRecorder",
                string.Format(CultureInfo.InvariantCulture,
                    "Load check: reseed of the last recorded sample of rec={0} at ut={1:F2} failed: {2}",
                    recording?.RecordingId ?? "(null)",
                    point.ut,
                    failureReason ?? "(none)"));
            return null;
        }

        /// <summary>One live background member whose finalization cache a restore rebuilds.</summary>
        internal struct RestoreCacheRebuildTarget
        {
            public uint VesselPid;
            public string RecordingId;
        }

        /// <summary>The refresh reason of the cache a restore rebuilds for a live member.</summary>
        internal const string RestoreRebuildRefreshReason = "restore_rebuild";

        /// <summary>
        /// Pure selection for the restore rebuild: every <c>BackgroundMap</c> entry whose
        /// recording exists, is not sealed Destroyed, has no terminal yet, and whose vessel is
        /// in the loaded scene. Sorted by pid so the summary line is stable.
        /// </summary>
        internal static List<RestoreCacheRebuildTarget> SelectRestoreCacheRebuildTargets(
            RecordingTree tree,
            Func<uint, bool> vesselExists)
        {
            var targets = new List<RestoreCacheRebuildTarget>();
            if (tree == null || tree.BackgroundMap == null || tree.Recordings == null
                || vesselExists == null)
                return targets;

            foreach (var kvp in tree.BackgroundMap)
            {
                Recording rec;
                if (string.IsNullOrEmpty(kvp.Value)
                    || !tree.Recordings.TryGetValue(kvp.Value, out rec)
                    || rec == null
                    || rec.VesselDestroyed
                    || rec.TerminalStateValue.HasValue
                    || !vesselExists(kvp.Key))
                    continue;
                targets.Add(new RestoreCacheRebuildTarget
                {
                    VesselPid = kvp.Key,
                    RecordingId = kvp.Value
                });
            }

            targets.Sort((a, b) => a.VesselPid.CompareTo(b.VesselPid));
            return targets;
        }

        /// <summary>The cache owner a live member's vessel state implies.</summary>
        internal static FinalizationCacheOwner ResolveRestoreCacheOwner(bool loaded, bool packed)
        {
            return loaded && !packed
                ? FinalizationCacheOwner.BackgroundLoaded
                : FinalizationCacheOwner.BackgroundOnRails;
        }

        /// <summary>
        /// Run once when a recorder is attached to a restored tree (an F9 or a resumed save):
        /// the finalization caches are in-memory only, so every background member starts with
        /// none, and an on-rails member gets no periodic refresh until it opens an orbit
        /// segment (a warp checkpoint, an SOI change, going off rails). Without this, a member
        /// that ends before its next refresh (stock deleting it on rails, a commit after it is
        /// gone) is finalized with no cache, by situation inference. Forces one refresh per
        /// live member from its vessel now (<see cref="SelectRestoreCacheRebuildTargets"/>)
        /// and logs ONE summary. <paramref name="refresher"/> replaces the live forced
        /// refresh (tests). Returns the number of members refreshed.
        /// </summary>
        internal int RebuildFinalizationCachesAfterRestore(
            Func<uint, Vessel> findVessel,
            string reason,
            Action<Vessel, string, FinalizationCacheOwner> refresher = null)
        {
            if (tree == null || tree.BackgroundMap == null || findVessel == null)
                return 0;

            List<RestoreCacheRebuildTarget> targets = SelectRestoreCacheRebuildTargets(
                tree,
                pid => !ReferenceEquals(findVessel(pid), null));
            int refreshed = 0;
            int applicable = 0;
            int loaded = 0;
            var outcomes = new List<string>(targets.Count);
            for (int i = 0; i < targets.Count; i++)
            {
                Vessel v = findVessel(targets[i].VesselPid);
                if (ReferenceEquals(v, null))
                    continue;
                FinalizationCacheOwner owner = ResolveRestoreCacheOwner(v.loaded, v.packed);
                if (owner == FinalizationCacheOwner.BackgroundLoaded)
                    loaded++;
                if (refresher != null)
                    refresher(v, targets[i].RecordingId, owner);
                else
                    RefreshFinalizationCacheForVessel(
                        v,
                        targets[i].RecordingId,
                        owner,
                        RestoreRebuildRefreshReason,
                        force: true);
                refreshed++;

                RecordingFinalizationCache cache;
                finalizationCaches.TryGetValue(targets[i].VesselPid, out cache);
                if (RecordingFinalizationCacheProducer.IsPotentiallyApplicableCache(cache))
                    applicable++;
                outcomes.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0}:{1}/{2}",
                    targets[i].VesselPid,
                    cache != null ? cache.Status.ToString() : "none",
                    cache?.TerminalState?.ToString() ?? "-"));
            }

            ParsekLog.Info("BgRecorder",
                string.Format(CultureInfo.InvariantCulture,
                    "Restore cache rebuild: reason={0} map={1} liveMembers={2} refreshed={3} " +
                    "loaded={4} onRails={5} applicable={6} outcomes=[{7}]",
                    string.IsNullOrEmpty(reason) ? "unspecified" : reason,
                    tree.BackgroundMap.Count,
                    targets.Count,
                    refreshed,
                    loaded,
                    refreshed - loaded,
                    applicable,
                    string.Join(",", outcomes.ToArray())));
            return refreshed;
        }

        internal struct BackgroundStateDriftSummary
        {
            public int BackgroundMapCount;
            public int MissingRecording;
            public int MissingTrackingState;
            public int LoadedWithoutLoadedState;
            public int PackedWithoutOnRailsState;
            public int PackedButLoadedState;
            public int LoadedButOnRailsState;

            public bool HasDrift =>
                MissingRecording > 0
                || MissingTrackingState > 0
                || LoadedWithoutLoadedState > 0
                || PackedWithoutOnRailsState > 0
                || PackedButLoadedState > 0
                || LoadedButOnRailsState > 0;
        }

        internal static string FormatBackgroundStateDriftSummary(
            BackgroundStateDriftSummary summary,
            string reason)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Background map/state drift: reason={0} map={1} missingRecording={2} missingTrackingState={3} loadedWithoutLoadedState={4} packedWithoutOnRailsState={5} packedButLoadedState={6} loadedButOnRailsState={7}",
                string.IsNullOrEmpty(reason) ? "unspecified" : reason,
                summary.BackgroundMapCount,
                summary.MissingRecording,
                summary.MissingTrackingState,
                summary.LoadedWithoutLoadedState,
                summary.PackedWithoutOnRailsState,
                summary.PackedButLoadedState,
                summary.LoadedButOnRailsState);
        }

        internal static string FormatBackgroundVesselStateDrift(
            uint vesselPid,
            string recordingId,
            string reason,
            bool vesselLoaded,
            bool vesselPacked,
            bool hasLoadedState,
            bool hasOnRailsState,
            bool hasRecording)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Background vessel state drift: pid={0} recId={1} reason={2} vesselLoaded={3} vesselPacked={4} hasLoadedState={5} hasOnRailsState={6} hasRecording={7}",
                vesselPid,
                string.IsNullOrEmpty(recordingId) ? "(null)" : recordingId,
                string.IsNullOrEmpty(reason) ? "unspecified" : reason,
                vesselLoaded,
                vesselPacked,
                hasLoadedState,
                hasOnRailsState,
                hasRecording);
        }

        private void WarnBackgroundVesselStateDrift(
            uint vesselPid,
            string recordingId,
            string reason,
            Vessel vessel,
            bool hasLoadedState,
            bool hasOnRailsState,
            bool hasRecording)
        {
            bool vesselLoaded = vessel != null && vessel.loaded;
            bool vesselPacked = vessel != null && vessel.packed;
            ParsekLog.WarnRateLimited("BgRecorder",
                $"bg-state-drift-{vesselPid}-{reason}",
                FormatBackgroundVesselStateDrift(
                    vesselPid,
                    recordingId,
                    reason,
                    vesselLoaded,
                    vesselPacked,
                    hasLoadedState,
                    hasOnRailsState,
                    hasRecording),
                10.0);
        }

        private void WarnIfBackgroundStateDrift(double currentUT, string reason)
        {
            if (tree == null || tree.BackgroundMap == null)
                return;

            if (!ShouldRunBackgroundStateDriftCheck(
                    lastBackgroundStateDriftCheckUT,
                    currentUT,
                    out lastBackgroundStateDriftCheckUT))
                return;

            var summary = BuildBackgroundStateDriftSummary();
            if (!summary.HasDrift)
                return;

            ParsekLog.WarnRateLimited("BgRecorder",
                "background-map-state-drift",
                FormatBackgroundStateDriftSummary(summary, reason),
                10.0);
        }

        internal void WarnIfBackgroundStateDriftForTesting(double currentUT, string reason)
        {
            WarnIfBackgroundStateDrift(currentUT, reason);
        }

        internal static bool ShouldRunBackgroundStateDriftCheck(
            double lastCheckUT,
            double currentUT,
            out double nextLastCheckUT)
        {
            nextLastCheckUT = lastCheckUT;
            if (lastCheckUT != double.MinValue)
            {
                if (currentUT < lastCheckUT)
                {
                    // Time went backwards (a rewind or a load): check now, whatever the interval.
                }
                else if (currentUT - lastCheckUT < BackgroundStateDriftCheckInterval)
                {
                    return false;
                }
            }

            nextLastCheckUT = currentUT;
            return true;
        }

        private BackgroundStateDriftSummary BuildBackgroundStateDriftSummary()
        {
            var summary = new BackgroundStateDriftSummary();
            if (tree == null || tree.BackgroundMap == null)
                return summary;

            summary.BackgroundMapCount = tree.BackgroundMap.Count;
            foreach (var kvp in tree.BackgroundMap)
            {
                uint pid = kvp.Key;
                string recordingId = kvp.Value;

                bool hasRecording = tree.Recordings != null
                    && !string.IsNullOrEmpty(recordingId)
                    && tree.Recordings.ContainsKey(recordingId);
                if (!hasRecording)
                    summary.MissingRecording++;

                bool hasLoadedState = loadedStates.ContainsKey(pid);
                bool hasOnRailsState = onRailsStates.ContainsKey(pid);
                if (!hasLoadedState && !hasOnRailsState)
                    summary.MissingTrackingState++;

                Vessel vessel = vesselFinderOverride != null
                    ? vesselFinderOverride(pid)
                    : FlightRecorder.FindVesselByPid(pid);
                if (vessel == null)
                    continue;

                bool loadedPhysics = vessel.loaded && !vessel.packed;
                if (loadedPhysics && !hasLoadedState)
                    summary.LoadedWithoutLoadedState++;
                if (!loadedPhysics && !hasOnRailsState)
                    summary.PackedWithoutOnRailsState++;
                if (!loadedPhysics && hasLoadedState)
                    summary.PackedButLoadedState++;
                if (loadedPhysics && hasOnRailsState)
                    summary.LoadedButOnRailsState++;
            }

            return summary;
        }
    }
}
