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
            ClosedAtLastKnownUT = 1
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
                "(already ended by a recovery/termination event) outcomes=[{8}]",
                missing != null ? missing.Count : 0,
                backgroundMapCount,
                debris,
                string.IsNullOrEmpty(reason) ? "unspecified" : reason,
                string.Join(",", pids.ToArray()),
                string.Join(",", ids.ToArray()),
                closed,
                leftToEvent,
                string.Join(",", outcomes.ToArray()));
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
        /// This deliberately does not route through <see cref="EndDebrisRecording"/>: a
        /// restored on-rails member has no finalization cache, so that path stamps
        /// Destroyed, which would replay a pruned debris as an explosion.
        /// No sidecar write: nothing is appended to the trajectory here, and the terminal
        /// fields travel in the tree metadata the next save writes.
        /// Returns the missing count.
        /// </summary>
        internal int CloseMissingBackgroundMembersAtLoad(Func<uint, bool> vesselExists, string reason)
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
            for (int i = 0; i < missing.Count; i++)
            {
                Recording rec;
                tree.Recordings.TryGetValue(missing[i].RecordingId, out rec);
                MissingMemberClosePlan plan = DecideMissingMemberClose(rec);
                plans.Add(plan);

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

                // The normal "stops being recorded" shape: drop every per-vessel tracking
                // structure, then the map entry. The restored member's on-rails state has no
                // open orbit segment, so nothing is appended at closeUT.
                OnVesselRemovedFromBackground(missing[i].VesselPid, plan.EndUT);
                tree.BackgroundMap.Remove(missing[i].VesselPid);
            }

            ParsekLog.Info("BgRecorder",
                FormatMissingBackgroundMembersSummary(missing, plans, mapCountBefore, reason));
            return missing.Count;
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
