using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// What the recorder does when a stock Alt+F12 teleport cheat (Set Orbit, Rendezvous,
    /// Set Position, and the middle-click pick that only fills Set Position's fields) moved
    /// the active vessel. Every one of those runs <c>FlightGlobals.PrepForOrbitSet</c>, sets
    /// the orbit, then <c>FlightGlobals.PostOrbitSet(oldBody)</c> (decompiled KSP 1.12.5);
    /// <see cref="Patches.CheatTeleportPatch"/> postfixes the latter.
    /// </summary>
    internal enum CheatTeleportAction
    {
        /// <summary>No live recorder: log only.</summary>
        NoLiveRecording = 0,
        /// <summary>The live recorder records another vessel: log only.</summary>
        OtherVessel,
        /// <summary>
        /// Cross-body while on rails with a coastable new orbit: stock fired
        /// <c>onVesselSOIChanged</c> inside <c>PostOrbitSet</c> BEFORE the postfix, and
        /// <see cref="FlightRecorder.OnVesselSOIChanged"/> already closed the old segment,
        /// re-captured the new orbit, split the section and set the SOI flag. Log only.
        /// </summary>
        CrossBodyHandledBySoi,
        /// <summary>On rails, same body, coastable new orbit: re-capture the orbit segment.</summary>
        OnRailsRecapture,
        /// <summary>
        /// On rails but the new orbit is not a coast (inside an atmosphere, or an airless
        /// sub-surface arc): leave rails bookkeeping, open an Absolute section, and let the
        /// go-off-rails sample finish the seam.
        /// </summary>
        OnRailsToAbsolute,
        /// <summary>Not on rails (surface / atmosphere start), same body: seam at go-off-rails.</summary>
        OffRailsSameBody,
        /// <summary>Not on rails, body changed: split the section by body now.</summary>
        OffRailsCrossBody,
    }

    internal static class CheatTeleportDecision
    {
        internal const string Tag = "Recorder";

        internal static CheatTeleportAction Decide(
            bool recorderLive,
            bool vesselIsRecorded,
            bool recorderOnRails,
            bool bodyChanged,
            bool newOrbitIsCoast)
        {
            if (!recorderLive)
                return CheatTeleportAction.NoLiveRecording;
            if (!vesselIsRecorded)
                return CheatTeleportAction.OtherVessel;
            if (recorderOnRails)
            {
                if (!newOrbitIsCoast)
                    return CheatTeleportAction.OnRailsToAbsolute;
                return bodyChanged
                    ? CheatTeleportAction.CrossBodyHandledBySoi
                    : CheatTeleportAction.OnRailsRecapture;
            }
            return bodyChanged
                ? CheatTeleportAction.OffRailsCrossBody
                : CheatTeleportAction.OffRailsSameBody;
        }

        /// <summary>
        /// True when the post-teleport orbit may be recorded as an orbit segment: the same
        /// layer-2 rule <see cref="FlightRecorder.OnVesselGoOnRails"/> applies (no segment
        /// inside an atmosphere), plus no segment for an airless sub-surface arc (periapsis at
        /// or below the surface), which is what Set Position onto an airless body produces.
        /// </summary>
        internal static bool IsPostTeleportOrbitACoast(
            bool bodyHasAtmosphere, double altitude, double atmosphereDepth, double periapsisAltitude)
        {
            if (double.IsNaN(altitude) || double.IsInfinity(altitude))
                return false;
            if (FlightRecorder.ShouldSkipOrbitSegmentForAtmosphere(bodyHasAtmosphere, altitude, atmosphereDepth))
                return false;
            if (!bodyHasAtmosphere && !(periapsisAltitude > 0.0))
                return false;
            return true;
        }

        /// <summary>
        /// A same-body teleport is a discontinuity, not a phase change: its boundary is a
        /// recorder bookkeeping artifact, so it is marked with a seam section and the
        /// optimizer never splits there. A cross-body teleport keeps an unflagged boundary so
        /// the optimizer's body rule (#251) splits the recording by body.
        /// </summary>
        internal static bool UsesSeamSection(bool bodyChanged)
        {
            return !bodyChanged;
        }

        internal static string FormatLogLine(
            CheatTeleportAction action,
            string vesselName,
            uint vesselPid,
            string fromBody,
            string toBody,
            double ut)
        {
            return "Cheat teleport detected (Alt+F12 Set Orbit / Rendezvous / Set Position): "
                + "vessel='" + (vesselName ?? "(null)") + "' pid="
                + vesselPid.ToString(CultureInfo.InvariantCulture)
                + " from=" + (fromBody ?? "(null)")
                + " to=" + (toBody ?? "(null)")
                + " ut=" + ut.ToString("F2", CultureInfo.InvariantCulture)
                + " action=" + action;
        }
    }

    public partial class FlightRecorder
    {
        // A same-body teleport armed at PostOrbitSet and finished at the next go-off-rails,
        // when the vessel's live position is valid again (inside the postfix
        // v.latitude / v.altitude are still the pre-teleport values).
        private bool cheatTeleportSeamPending;
        private double cheatTeleportPendingUT = double.NaN;
        private bool cheatTeleportReseedPending;

        internal bool CheatTeleportSeamPendingForTesting => cheatTeleportSeamPending;

        internal void SetOnRailsStateForTesting(bool onRails, OrbitSegment segment)
        {
            isOnRails = onRails;
            currentOrbitSegment = segment;
        }

        internal bool IsOnRailsForTesting => isOnRails;

        /// <summary>
        /// Live entry point from <see cref="Patches.CheatTeleportPatch"/> (via
        /// <see cref="ParsekFlight.HandleCheatTeleport"/>). <paramref name="v"/> is the
        /// active vessel stock just moved; <paramref name="oldBody"/> is PostOrbitSet's
        /// argument.
        /// </summary>
        internal void OnCheatTeleport(Vessel v, CelestialBody oldBody)
        {
            if (v == null)
                return;
            double ut = Planetarium.GetUniversalTime();
            CelestialBody newBody = v.mainBody;
            bool bodyChanged = oldBody != null && newBody != null && !ReferenceEquals(oldBody, newBody);

            bool coast = false;
            if (isOnRails && newBody != null && v.orbit != null)
            {
                coast = CheatTeleportDecision.IsPostTeleportOrbitACoast(
                    newBody.atmosphere, v.orbit.altitude, newBody.atmosphereDepth, v.orbit.PeA);
            }

            CheatTeleportAction action = CheatTeleportDecision.Decide(
                recorderLive: IsRecording,
                vesselIsRecorded: v.persistentId == RecordingVesselId,
                recorderOnRails: isOnRails,
                bodyChanged: bodyChanged,
                newOrbitIsCoast: coast);

            ParsekLog.Info(CheatTeleportDecision.Tag, CheatTeleportDecision.FormatLogLine(
                action, v.vesselName, v.persistentId, oldBody?.name, newBody?.name, ut));

            if (action == CheatTeleportAction.NoLiveRecording
                || action == CheatTeleportAction.OtherVessel
                || action == CheatTeleportAction.CrossBodyHandledBySoi)
                return;

            if (isRelativeMode)
                ClearRelativeModeForRailsTransition();

            OrbitSegment? recaptured = null;
            if (action == CheatTeleportAction.OnRailsRecapture)
                recaptured = CreateOrbitSegmentWithRotation(v, ut);

            SegmentEnvironment env = environmentHysteresis != null
                ? environmentHysteresis.CurrentEnvironment
                : SegmentEnvironment.ExoBallistic;
            ApplyCheatTeleportBookkeeping(action, ut, env, oldBody?.name, newBody?.name, recaptured);
            RefreshFinalizationCache(v, "cheat_teleport", force: true);
        }

        /// <summary>
        /// Vessel-free half of <see cref="OnCheatTeleport"/>: orbit-segment and track-section
        /// bookkeeping for one teleport, so xUnit can drive it.
        /// </summary>
        internal void ApplyCheatTeleportBookkeeping(
            CheatTeleportAction action,
            double ut,
            SegmentEnvironment env,
            string fromBody,
            string toBody,
            OrbitSegment? recapturedSegment)
        {
            string utText = ut.ToString("F2", CultureInfo.InvariantCulture);
            switch (action)
            {
                case CheatTeleportAction.OnRailsRecapture:
                {
                    CloseCurrentOrbitSegmentForCheatTeleport(ut);
                    if (recapturedSegment.HasValue)
                        currentOrbitSegment = recapturedSegment.Value;
                    CloseCurrentTrackSection(ut);
                    StartNewTrackSection(env, ReferenceFrame.OrbitalCheckpoint, ut,
                        TrackSectionSource.Checkpoint);
                    ParsekLog.Info("Recorder",
                        $"Cheat teleport: orbit segment re-captured on rails (body={toBody ?? "(null)"}) at UT={utText}");
                    return;
                }
                case CheatTeleportAction.OnRailsToAbsolute:
                {
                    CloseCurrentOrbitSegmentForCheatTeleport(ut);
                    isOnRails = false;
                    CloseCurrentTrackSection(ut);
                    StartNewTrackSection(env, ReferenceFrame.Absolute, ut);
                    bool bodyChanged = !string.Equals(fromBody, toBody, StringComparison.Ordinal);
                    ArmCheatTeleportPending(ut, seam: CheatTeleportDecision.UsesSeamSection(bodyChanged));
                    ParsekLog.Info("Recorder",
                        $"Cheat teleport: new orbit is not a coast (body={toBody ?? "(null)"}) - left rails " +
                        $"bookkeeping, Absolute section at UT={utText}, seam={(bodyChanged ? 0 : 1)} pending go-off-rails");
                    return;
                }
                case CheatTeleportAction.OffRailsSameBody:
                {
                    ArmCheatTeleportPending(ut, seam: true);
                    ParsekLog.Info("Recorder",
                        $"Cheat teleport: seam section pending go-off-rails (body={toBody ?? "(null)"}) at UT={utText}");
                    return;
                }
                case CheatTeleportAction.OffRailsCrossBody:
                {
                    CloseCurrentTrackSection(ut);
                    StartNewTrackSection(env, ReferenceFrame.Absolute, ut);
                    SoiChangePending = true;
                    SoiChangeFromBody = fromBody;
                    ArmCheatTeleportPending(ut, seam: false);
                    ParsekLog.Info("Recorder",
                        $"Cheat teleport: section split by body {fromBody ?? "(null)"} -> {toBody ?? "(null)"} " +
                        $"at UT={utText} (SOI change flagged)");
                    return;
                }
                default:
                    return;
            }
        }

        private void ArmCheatTeleportPending(double ut, bool seam)
        {
            cheatTeleportSeamPending = seam;
            cheatTeleportReseedPending = true;
            cheatTeleportPendingUT = ut;
        }

        private void CloseCurrentOrbitSegmentForCheatTeleport(double ut)
        {
            if (!isOnRails)
                return;
            currentOrbitSegment.endUT = ut;
            // A segment opened in this same frame (PrepForOrbitSet's GoOnRails) carries the
            // pre-teleport elements over zero time; nothing can play it, so drop it.
            if (currentOrbitSegment.endUT > currentOrbitSegment.startUT)
            {
                OrbitSegments.Add(currentOrbitSegment);
                AddOrbitSegmentToCurrentTrackSection(currentOrbitSegment);
            }
            else
            {
                ParsekLog.Verbose("Recorder",
                    "Cheat teleport: dropped zero-length pre-teleport orbit segment at UT=" +
                    ut.ToString("F2", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// Finishes an armed teleport at the go-off-rails that follows it (stock holds the
        /// unpack for 10 frames). For a same-body teleport it writes a zero-duration seam
        /// section holding the first post-teleport frame (the producer-C shape of
        /// <c>BackgroundRecorder.FlushLoadedStateForOnRailsTransition</c>) and opens the next
        /// section, so neither boundary is a split candidate. Returns true when a pending
        /// teleport was consumed. <paramref name="commitBoundaryFrame"/> commits the live
        /// sample into the current section (the live caller passes SamplePosition).
        /// </summary>
        internal bool FinishPendingCheatTeleportAtOffRails(
            double ut, SegmentEnvironment env, Action commitBoundaryFrame)
        {
            if (!cheatTeleportReseedPending && !cheatTeleportSeamPending)
                return false;

            bool seam = cheatTeleportSeamPending;
            double armedUT = cheatTeleportPendingUT;
            cheatTeleportSeamPending = false;
            cheatTeleportReseedPending = false;
            cheatTeleportPendingUT = double.NaN;

            if (!seam)
            {
                commitBoundaryFrame?.Invoke();
                ParsekLog.Verbose("Recorder",
                    "Cheat teleport: go-off-rails after cross-body teleport armed at UT=" +
                    armedUT.ToString("F2", CultureInfo.InvariantCulture));
                return true;
            }

            CloseCurrentTrackSection(ut);
            StartNewTrackSection(env, ReferenceFrame.Absolute, ut);
            currentTrackSection.isBoundarySeam = true;
            commitBoundaryFrame?.Invoke();
            int seamFrames = currentTrackSection.frames?.Count ?? 0;
            if (seamFrames == 0)
            {
                // A payload-free seam would persist (seams always do) and make the codec read
                // the whole recording's section payload as incomplete; without a frame the
                // boundary is not worth marking.
                currentTrackSection.isBoundarySeam = false;
            }
            bool persistedSeam = currentTrackSection.isBoundarySeam;
            CloseCurrentTrackSection(ut);
            StartNewTrackSection(env, ReferenceFrame.Absolute, ut);
            ParsekLog.Info("Recorder",
                $"Cheat teleport: seam section written at go-off-rails UT={ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"(armed UT={armedUT.ToString("F2", CultureInfo.InvariantCulture)}, frames={seamFrames}, " +
                $"seam={(persistedSeam ? 1 : 0)})");
            return true;
        }
    }
}
