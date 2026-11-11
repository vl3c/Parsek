using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    // Pad evidence: the idle-on-pad and pad-failure heuristics and the tree evidence they read.
    public partial class ParsekFlight
    {
        /// <summary>
        /// Returns true if the recording should be discarded as a pad failure:
        /// duration &lt; 10s AND max distance from launch &lt; 30m.
        /// </summary>
        internal static bool IsPadFailure(double duration, double maxDistanceFromLaunch)
        {
            return duration < PadFailureDurationThresholdSeconds
                && maxDistanceFromLaunch < PadLocalizedDistanceThresholdMeters;
        }

        /// <summary>
        /// Recording-aware pad failure check. Keeps the legacy 3D distance rule, but
        /// also treats topple/drop launches as pad failures when their surface range
        /// and vertical displacement from launch both stayed within pad-local thresholds.
        /// </summary>
        internal static bool IsPadFailure(Recording rec)
        {
            if (rec == null)
                return false;

            double duration = rec.EndUT - rec.StartUT;
            return IsPadFailure(duration, rec.MaxDistanceFromLaunch)
                || (duration < PadFailureDurationThresholdSeconds && HasPadLocalizedMotionOverride(rec));
        }

        /// <summary>
        /// Returns true if the vessel never moved meaningfully from its launch position,
        /// regardless of duration. Covers vessels that sat idle on the pad while the
        /// player did other activities (EVA, vehicle switch, etc.).
        /// </summary>
        internal static bool IsIdleOnPad(double maxDistanceFromLaunch)
        {
            return maxDistanceFromLaunch < PadLocalizedDistanceThresholdMeters;
        }

        /// <summary>
        /// Recording-aware idle-on-pad check. Extends the legacy 3D max-distance rule
        /// with a pad-drop override so local collapse or topple noise does not
        /// promote a pad-local failure into merge UI. Pad evidence is a tree-level
        /// requirement (<see cref="TreePadEvidence"/>), not a per-recording one: EVA
        /// kerbals and background-only children carry no launch situation of their own.
        /// </summary>
        internal static bool IsIdleOnPad(Recording rec)
        {
            if (rec == null)
                return false;

            return IsIdleOnPad(rec.MaxDistanceFromLaunch)
                || HasPadLocalizedMotionOverride(rec);
        }
        /// <summary>
        /// Pure decision: the pad evidence a recording shows, or null when it shows none.
        /// The launch site is checked first and wins over any start situation: a normal
        /// launch recording starts airborne ("Flying") with a site, because the auto-record
        /// trigger fires when the vessel leaves PRELAUNCH, so a fizzled launch that never
        /// left the pad is still an idle-on-pad recording. Otherwise the evidence is a
        /// Prelaunch / Landed / Splashed start, then a Landed / Splashed terminal. An
        /// orbiting or flying start with no site and no surface terminal shows no evidence
        /// (the one-sample orbiting case). Returns the first matching reason: "launch-site",
        /// "start-prelaunch", "start-landed", "start-splashed", "terminal-landed" or
        /// "terminal-splashed".
        /// </summary>
        internal static string PadEvidenceReason(string startSituation, string launchSiteName, TerminalState? terminal)
        {
            if (!string.IsNullOrEmpty(launchSiteName))
                return "launch-site";
            if (string.Equals(startSituation, "Prelaunch", StringComparison.OrdinalIgnoreCase))
                return "start-prelaunch";
            if (string.Equals(startSituation, "Landed", StringComparison.OrdinalIgnoreCase))
                return "start-landed";
            if (string.Equals(startSituation, "Splashed", StringComparison.OrdinalIgnoreCase))
                return "start-splashed";
            if (terminal == TerminalState.Landed)
                return "terminal-landed";
            if (terminal == TerminalState.Splashed)
                return "terminal-splashed";
            return null;
        }

        /// <summary>
        /// Pure decision: true when <see cref="PadEvidenceReason(string, string, TerminalState?)"/> finds evidence.
        /// </summary>
        internal static bool HasPadEvidence(string startSituation, string launchSiteName, TerminalState? terminal)
        {
            return PadEvidenceReason(startSituation, launchSiteName, terminal) != null;
        }

        /// <summary>
        /// Pure decision: true when the recording's own start / site / terminal fields show pad evidence.
        /// </summary>
        internal static bool HasPadEvidence(Recording rec)
        {
            return rec != null
                && HasPadEvidence(rec.StartSituation, rec.LaunchSiteName, rec.TerminalStateValue);
        }

        /// <summary>
        /// Pure decision: the pad evidence of a flight's recordings, or null when none shows
        /// any. The first recording (in enumeration order) with evidence supplies the reason.
        /// Null entries are skipped.
        /// </summary>
        internal static string TreePadEvidence(IEnumerable<Recording> recordings)
        {
            if (recordings == null)
                return null;

            foreach (Recording rec in recordings)
            {
                if (rec == null)
                    continue;
                string reason = PadEvidenceReason(rec.StartSituation, rec.LaunchSiteName, rec.TerminalStateValue);
                if (reason != null)
                    return reason;
            }
            return null;
        }

        /// <summary>
        /// Tree-level pad-evidence gate shared by <see cref="IsTreeIdleOnPad(RecordingTree)"/>
        /// and <see cref="IsActiveTreeIdleOnPad"/>. Logs the verdict (tree, reason or "none")
        /// and returns true when at least one recording shows pad evidence.
        /// </summary>
        internal static bool TreeHasPadEvidenceForIdle(RecordingTree tree, string caller)
        {
            string reason = TreePadEvidence(tree?.Recordings?.Values);
            ParsekLog.Verbose("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "{0}: tree='{1}' id={2} pad evidence={3}",
                    caller, tree?.TreeName ?? "<unnamed>", tree?.Id ?? "(none)", reason ?? "none"));
            return reason != null;
        }

        /// <summary>
        /// Pure decision for the destroyed-during-split single-recording path
        /// (<c>FallbackCommitSplitRecorder</c>): "pad failure" or "idle on pad" when the
        /// recording is within the pad thresholds AND shows pad evidence, else null. The pad
        /// rules stay tree-level wherever a tree is available (EVA and debris children carry
        /// no launch situation); this branch handles one recording, so the recording's own
        /// evidence is the gate. A one-sample recording has duration 0 and maxDist 0, so
        /// without the evidence check an orbiting recording destroyed within one sample
        /// interval is discarded.
        /// </summary>
        internal static string DecideDestroyedSplitRecordingDiscard(Recording rec)
        {
            if (rec == null || !HasPadEvidence(rec))
                return null;
            if (IsPadFailure(rec))
                return "pad failure";
            if (IsIdleOnPad(rec))
                return "idle on pad";
            return null;
        }

        private static bool HasPadLocalizedMotionOverride(Recording rec)
        {
            if (rec == null
                || rec.Points == null
                || rec.Points.Count < 2
                || rec.MaxDistanceFromLaunch < PadLocalizedDistanceThresholdMeters)
            {
                return false;
            }

            if (!TryGetPadLocalizedMotionMetrics(
                rec,
                out double maxSurfaceRangeFromLaunch,
                out double maxAbsoluteAltitudeDeltaFromLaunch))
            {
                return false;
            }

            bool localized =
                maxSurfaceRangeFromLaunch < PadLocalizedDistanceThresholdMeters &&
                maxAbsoluteAltitudeDeltaFromLaunch < PadLocalizedAltitudeDisplacementThresholdMeters;
            if (localized)
            {
                ParsekLog.Verbose("Flight",
                    $"Pad-localized motion override: rec='{rec.RecordingId ?? "(unknown)"}' " +
                    $"surfaceRange={maxSurfaceRangeFromLaunch:F1}m " +
                    $"maxAbsAltDelta={maxAbsoluteAltitudeDeltaFromLaunch:F1}m " +
                    $"maxDist3D={rec.MaxDistanceFromLaunch:F1}m");
            }
            return localized;
        }

        private static bool TryGetPadLocalizedMotionMetrics(
            Recording rec,
            out double maxSurfaceRangeFromLaunch,
            out double maxAbsoluteAltitudeDeltaFromLaunch)
        {
            maxSurfaceRangeFromLaunch = 0.0;
            maxAbsoluteAltitudeDeltaFromLaunch = 0.0;

            if (rec == null || rec.Points == null || rec.Points.Count < 2)
                return false;

            if (rec.TrackSections != null)
            {
                for (int i = 0; i < rec.TrackSections.Count; i++)
                {
                    if (rec.TrackSections[i].referenceFrame != ReferenceFrame.Absolute)
                        return false;
                }
            }

            var launchPoint = rec.Points[0];
            string launchBody = launchPoint.bodyName ?? "Kerbin";
            double bodyRadius = GetPadHeuristicBodyRadius(launchBody);
            double launchAltitude = launchPoint.altitude;

            for (int i = 1; i < rec.Points.Count; i++)
            {
                var pt = rec.Points[i];
                string pointBody = pt.bodyName ?? launchBody;
                if (!string.Equals(pointBody, launchBody, StringComparison.Ordinal))
                {
                    maxSurfaceRangeFromLaunch = double.PositiveInfinity;
                    maxAbsoluteAltitudeDeltaFromLaunch = double.PositiveInfinity;
                    return true;
                }

                double effectiveRadius = bodyRadius + Math.Max(0.0, (launchAltitude + pt.altitude) * 0.5);
                double surfaceRange = SpawnCollisionDetector.SurfaceDistance(
                    launchPoint.latitude,
                    launchPoint.longitude,
                    pt.latitude,
                    pt.longitude,
                    effectiveRadius);
                if (surfaceRange > maxSurfaceRangeFromLaunch)
                    maxSurfaceRangeFromLaunch = surfaceRange;

                double absoluteAltitudeDeltaFromLaunch = Math.Abs(pt.altitude - launchAltitude);
                if (absoluteAltitudeDeltaFromLaunch > maxAbsoluteAltitudeDeltaFromLaunch)
                    maxAbsoluteAltitudeDeltaFromLaunch = absoluteAltitudeDeltaFromLaunch;
            }

            return true;
        }

        private static double GetPadHeuristicBodyRadius(string bodyName)
        {
            try
            {
                CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
                if (body != null)
                    return body.Radius;
            }
            catch
            {
                // FlightGlobals is unavailable in unit tests; fall back to stock radii.
            }

            if (!string.IsNullOrEmpty(bodyName)
                && PadHeuristicBodyRadii.TryGetValue(bodyName, out double radius))
            {
                return radius;
            }

            return 600000.0;
        }

        /// <summary>
        /// Computes terrain clearance based on distance from the active vessel.
        /// Inside the physics bubble (2.3km): 0.5m (full-LOD terrain matches PQS).
        /// Outside: lerps from 2m at the bubble edge to 5m at 120km to compensate
        /// for low-LOD terrain mesh overshooting PQS height at ridges.
        /// </summary>
        internal static double ComputeTerrainClearance(double distanceToVessel)
        {
            return DistanceThresholds.GhostFlight.ComputeTerrainClearance(distanceToVessel);
        }

        /// <summary>
        /// Returns true if every recording in the tree qualifies as a pad failure AND the
        /// tree shows pad evidence. A tree with any non-pad-failure recording is not
        /// discarded. The evidence gate matters because a one-sample recording has duration 0
        /// and MaxDistanceFromLaunch 0 by construction, so the thresholds alone would read an
        /// orbiting tree destroyed within one sample interval as a pad failure.
        /// </summary>
        internal static bool IsTreePadFailure(RecordingTree tree)
        {
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
                return false;

            if (TreeHasPlacedGroundPartsForIdle(tree, "IsTreePadFailure")
                || TreeHasPlantedFlagsForIdle(tree, "IsTreePadFailure")
                || TreeMovesHeldKerbalForIdle(tree, "IsTreePadFailure")
                || TreeInteractsWithForeignVesselForIdle(tree, "IsTreePadFailure"))
                return false;

            foreach (var rec in tree.Recordings.Values)
            {
                if (!IsPadFailure(rec))
                    return false;
            }
            return TreeHasPadEvidenceForIdle(tree, "IsTreePadFailure");
        }

        /// <summary>
        /// Counts the <see cref="BranchPointType.GroundPartPlaced"/> branch points of
        /// <paramref name="tree"/>: each one is a vessel an EVA kerbal created by placing
        /// a ground part (Breaking Ground science, deployables). Pure, null-safe.
        /// </summary>
        internal static int CountPlacedGroundParts(RecordingTree tree)
        {
            if (tree?.BranchPoints == null) return 0;
            int count = 0;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                BranchPoint bp = tree.BranchPoints[i];
                if (bp != null && bp.Type == BranchPointType.GroundPartPlaced)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Counts the flags planted across every recording of <paramref name="tree"/>
        /// (<see cref="Recording.FlagEvents"/>). Pure, null-safe.
        /// </summary>
        internal static int CountPlantedFlags(RecordingTree tree)
        {
            if (tree?.Recordings == null) return 0;
            int count = 0;
            foreach (Recording rec in tree.Recordings.Values)
            {
                if (rec?.FlagEvents != null)
                    count += rec.FlagEvents.Count;
            }
            return count;
        }

        /// <summary>
        /// Counts the converter part events (drills, ISRU, any BaseConverter) the player caused
        /// across every recording of <paramref name="tree"/>: ConverterActivated /
        /// ConverterDeactivated after the recording's start seed. Pure, null-safe.
        /// </summary>
        internal static int CountPlayerConverterToggles(RecordingTree tree)
        {
            if (tree?.Recordings == null) return 0;
            int count = 0;
            foreach (Recording rec in tree.Recordings.Values)
            {
                if (rec?.PartEvents == null || rec.PartEvents.Count == 0) continue;
                double seedUT = rec.StartUT + ConverterSeedEpsilonSeconds;
                for (int i = 0; i < rec.PartEvents.Count; i++)
                {
                    PartEvent ev = rec.PartEvents[i];
                    if ((ev.eventType == PartEventType.ConverterActivated
                            || ev.eventType == PartEventType.ConverterDeactivated)
                        && ev.ut > seedUT)
                        count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Idle-on-pad veto for converter work: a stationary miner or ISRU run is player work
        /// even when nothing moved 30 m. Logs the veto; returns true when the tree must not be
        /// classified idle. The live predicate calls this after flushing the recorder.
        /// </summary>
        internal static bool TreeHasConverterWorkForIdle(RecordingTree tree, string caller)
        {
            int toggles = CountPlayerConverterToggles(tree);
            if (toggles <= 0) return false;
            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "{0}: not idle - tree has {1} converter toggle(s) tree='{2}'",
                    caller, toggles, tree?.TreeName ?? "<unnamed>"));
            return true;
        }

        /// <summary>
        /// Counts the science-lab payouts (<see cref="GameStateEventType.ScienceLabCredited"/>:
        /// a lab transmission or a lab recovery) in <paramref name="events"/> tagged to any
        /// recording of <paramref name="tree"/>. Pure, null-safe.
        /// </summary>
        internal static int CountTaggedLabPayouts(
            RecordingTree tree, IReadOnlyList<GameStateEvent> events)
        {
            if (tree?.Recordings == null || tree.Recordings.Count == 0
                || events == null || events.Count == 0)
                return 0;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Recording> kv in tree.Recordings)
            {
                if (!string.IsNullOrEmpty(kv.Key)) ids.Add(kv.Key);
                if (!string.IsNullOrEmpty(kv.Value?.RecordingId)) ids.Add(kv.Value.RecordingId);
            }
            int count = 0;
            for (int i = 0; i < events.Count; i++)
            {
                GameStateEvent e = events[i];
                if (e.eventType == GameStateEventType.ScienceLabCredited
                    && !string.IsNullOrEmpty(e.recordingId)
                    && ids.Contains(e.recordingId))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Idle-on-pad veto for a science-lab payout (owner ruling 2026-10-17): the lab's
        /// science is banked in the R&amp;D pool the moment it pays out, so the flight is
        /// player work even when nothing moved 30 m. Reads the live event store, the same
        /// source a discard re-homes lab credits from. Logs the veto; returns true when the
        /// tree must not be classified idle.
        /// </summary>
        internal static bool TreeHasLabPayoutsForIdle(RecordingTree tree, string caller)
        {
            return TreeHasLabPayoutsForIdle(tree, caller, GameStateStore.Events);
        }

        internal static bool TreeHasLabPayoutsForIdle(
            RecordingTree tree, string caller, IReadOnlyList<GameStateEvent> events)
        {
            int payouts = CountTaggedLabPayouts(tree, events);
            if (payouts <= 0) return false;
            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "{0}: not idle - tree has {1} lab payout(s) tree='{2}'",
                    caller, payouts, tree?.TreeName ?? "<unnamed>"));
            return true;
        }

        /// <summary>
        /// Idle-on-pad veto for the recovery half of the lab ruling (owner ruling 2026-10-17):
        /// a flight Recover pays the lab's stored science at the Space Center, AFTER every
        /// idle-on-pad check has run (the LoadScene-prefix fast path and the Space Center
        /// OnLoad), and that payout is stored untagged, so
        /// <see cref="TreeHasLabPayoutsForIdle(RecordingTree, string)"/> can never see it.
        /// Reads the armed <see cref="InFlightRecoveryRequest"/> (lab science captured from
        /// the live vessel) or, after the finalize stamped the tip Recovered, the request's
        /// per-recording lab mark. Logs the veto; returns true when the tree must not be
        /// classified idle.
        /// </summary>
        internal static bool TreeHasPendingLabRecoveryForIdle(RecordingTree tree, string caller)
        {
            return TreeHasPendingLabRecoveryForIdle(
                tree, caller, InFlightRecoveryRequest.Armed,
                InFlightRecoveryRequest.LabScienceAwaitingRecovery);
        }

        internal static bool TreeHasPendingLabRecoveryForIdle(
            RecordingTree tree, string caller,
            InFlightRecoveryRequest.Request armedRequest,
            IReadOnlyDictionary<string, float> stampedLabScience)
        {
            float science = InFlightRecoveryRequest.PendingRecoveryLabScience(
                tree, armedRequest, stampedLabScience);
            if (!(science > 0f)) return false;
            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "{0}: not idle - recovery pending with lab science {1:F2} tree='{2}'",
                    caller, science, tree?.TreeName ?? "<unnamed>"));
            return true;
        }

        /// <summary>
        /// Idle-on-pad veto for a planted flag: the flag is a vessel the player made,
        /// so the flight is player work even when nothing moved 30 m. Logs the veto;
        /// returns true when the tree must not be classified idle. The live predicate
        /// must call this after flushing the recorder, which buffers flag events.
        /// </summary>
        internal static bool TreeHasPlantedFlagsForIdle(RecordingTree tree, string caller)
        {
            int flags = CountPlantedFlags(tree);
            if (flags <= 0) return false;
            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "{0}: not idle - tree has {1} planted flag(s) tree='{2}'",
                    caller, flags, tree?.TreeName ?? "<unnamed>"));
            return true;
        }

        /// <summary>
        /// The first EVA recording of <paramref name="tree"/> whose kerbal
        /// <paramref name="isHeld"/> reports held, or null. Pure, null-safe.
        /// </summary>
        internal static Recording FindEvaOfHeldKerbal(RecordingTree tree, Func<string, bool> isHeld)
        {
            if (tree?.Recordings == null || isHeld == null) return null;
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec == null || string.IsNullOrEmpty(rec.EvaCrewName)) continue;
                if (isHeld(rec.EvaCrewName)) return rec;
            }
            return null;
        }

        /// <summary>
        /// Idle-on-pad veto for a held kerbal leaving his vessel: a kerbal the committed
        /// timeline holds (an earlier mission left him aboard, so his hold is open-ended)
        /// stepped out on EVA in this flight. Only this flight's recordings can end that
        /// hold (the joined-vessel claim, then the EVA's own recovery or end), and stock has
        /// already moved him, so discarding it would leave him held forever (owner ruling
        /// 2026-10-25, todo CX4-IDLE-DISCARDED-FLIGHT-LEAVES-THE-HOLD-OPEN). Logs the veto;
        /// returns true when the tree must not be classified idle.
        /// </summary>
        internal static bool TreeMovesHeldKerbalForIdle(RecordingTree tree, string caller)
        {
            Func<string, bool> isHeld = HeldKerbalProbeForTesting
                ?? (name => LedgerOrchestrator.Kerbals?.IsReservedNow(name) ?? false);
            Recording eva = FindEvaOfHeldKerbal(tree, isHeld);
            if (eva == null) return false;
            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "{0}: not idle - held kerbal '{1}' left his vessel on EVA (recording {2}) tree='{3}'",
                    caller, eva.EvaCrewName, eva.RecordingId ?? "(none)", tree?.TreeName ?? "<unnamed>"));
            return true;
        }

        /// <summary>
        /// The first branch point of <paramref name="tree"/> at which the flight interacted
        /// with a vessel from outside the tree, or null: an InventoryTransfer (always a foreign
        /// partner), or a Dock / Board whose stamped target is no vessel the tree carried
        /// before the merge (no recording outside the branch point's own children has its
        /// pid). A board or dock back onto the tree's own vessel stamps no target or names a
        /// tree vessel, so it is not an interaction. Pure, null-safe.
        /// </summary>
        internal static BranchPoint FindForeignVesselInteraction(RecordingTree tree)
        {
            if (tree?.BranchPoints == null) return null;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                BranchPoint bp = tree.BranchPoints[i];
                if (bp == null) continue;
                if (bp.Type == BranchPointType.InventoryTransfer)
                    return bp;
                if (bp.Type != BranchPointType.Dock && bp.Type != BranchPointType.Board)
                    continue;
                if (bp.TargetVesselPersistentId == 0)
                    continue;
                if (!TreeCarriesVesselOutsideChildren(tree, bp, bp.TargetVesselPersistentId))
                    return bp;
            }
            return null;
        }

        private static bool TreeCarriesVesselOutsideChildren(RecordingTree tree, BranchPoint bp, uint pid)
        {
            if (tree.Recordings == null) return false;
            foreach (var kvp in tree.Recordings)
            {
                Recording rec = kvp.Value;
                if (rec == null || rec.VesselPersistentId != pid) continue;
                if (bp.ChildRecordingIds != null && bp.ChildRecordingIds.Contains(kvp.Key)) continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Idle-on-pad veto for an interaction with another vessel: a kerbal of this flight
        /// boarded or took a seat on, docked with, or moved cargo to or from a vessel outside
        /// the tree (owner ruling 2026-10-25: the interaction is player work even when nothing
        /// moved 30 m; the vessel the kerbal came from stays recorded as his origin). Logs the
        /// veto; returns true when the tree must not be classified idle.
        /// </summary>
        internal static bool TreeInteractsWithForeignVesselForIdle(RecordingTree tree, string caller)
        {
            BranchPoint bp = FindForeignVesselInteraction(tree);
            if (bp == null) return false;
            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "{0}: not idle - {1} with a vessel outside the flight (bp={2} targetPid={3}) tree='{4}'",
                    caller, bp.Type, bp.Id ?? "(none)", bp.TargetVesselPersistentId,
                    tree?.TreeName ?? "<unnamed>"));
            return true;
        }

        /// <summary>
        /// Idle-on-pad veto shared by <see cref="IsTreeIdleOnPad(RecordingTree)"/> and
        /// <see cref="IsActiveTreeIdleOnPad"/>: a tree whose kerbal placed ground parts
        /// created vessels, so it is player work even when nothing moved 30 m (a
        /// science cluster set up beside a lander). Logs the veto; returns true when
        /// the tree must not be classified idle.
        /// </summary>
        internal static bool TreeHasPlacedGroundPartsForIdle(RecordingTree tree, string caller)
        {
            int placed = CountPlacedGroundParts(tree);
            if (placed <= 0) return false;
            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "{0}: not idle - tree has {1} placed ground part(s) tree='{2}'",
                    caller, placed, tree?.TreeName ?? "<unnamed>"));
            return true;
        }

        /// <summary>
        /// Returns true if every recording in the tree is idle-on-pad (never moved
        /// more than 30m from launch, and shows pad evidence per <see cref="IsIdleOnPad(Recording)"/>).
        /// A tree with any non-idle recording is not discarded.
        /// </summary>
        internal static bool IsTreeIdleOnPad(RecordingTree tree)
        {
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
                return false;

            if (TreeHasPlacedGroundPartsForIdle(tree, "IsTreeIdleOnPad")
                || TreeHasPlantedFlagsForIdle(tree, "IsTreeIdleOnPad")
                || TreeMovesHeldKerbalForIdle(tree, "IsTreeIdleOnPad")
                || TreeInteractsWithForeignVesselForIdle(tree, "IsTreeIdleOnPad")
                || TreeHasConverterWorkForIdle(tree, "IsTreeIdleOnPad")
                || TreeHasLabPayoutsForIdle(tree, "IsTreeIdleOnPad")
                || TreeHasPendingLabRecoveryForIdle(tree, "IsTreeIdleOnPad"))
                return false;

            bool anyHasPoints = false;
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec.Points != null && rec.Points.Count > 0)
                    anyHasPoints = true;
                if (!IsIdleOnPad(rec))
                {
                    ParsekLog.Verbose("Flight",
                        string.Format(CultureInfo.InvariantCulture,
                            "IsTreeIdleOnPad: '{0}' maxDist={1:F1}m — not idle",
                            rec.VesselName, rec.MaxDistanceFromLaunch));
                    return false;
                }
            }
            // Bug #290d: a tree where no recording has trajectory points is data-loss
            // (e.g., sidecar epoch mismatch), not idle-on-pad. Don't auto-discard.
            if (!anyHasPoints)
            {
                ParsekLog.Verbose("Flight",
                    $"IsTreeIdleOnPad: all {tree.Recordings.Count} recordings have 0 points — " +
                    "cannot determine idle, returning false");
                return false;
            }
            if (!TreeHasPadEvidenceForIdle(tree, "IsTreeIdleOnPad"))
                return false;
            ParsekLog.Verbose("Flight",
                $"IsTreeIdleOnPad: all {tree.Recordings.Count} recordings within 30m — idle on pad");
            return true;
        }
    }
}
