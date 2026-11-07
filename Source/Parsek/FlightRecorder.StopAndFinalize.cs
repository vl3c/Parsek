using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // Recording stop and capture, terminal event cleanup, false-alarm resume, and the backup
    // snapshot / finalization cache refresh.
    public partial class FlightRecorder
    {
        /// <summary>
        /// Builds the CaptureAtStop recording object from current state.
        /// Extracted from StopRecording/StopRecordingForChainBoundary/OnPhysicsFrame to eliminate
        /// the triple duplication of the CaptureAtStop construction block.
        /// </summary>
        private Recording BuildCaptureRecording(
            string vesselName,
            bool isDestroyed,
            Vessel snapshotVessel = null,
            ConfigNode destroyedFallbackSnapshot = null,
            bool stopIsChainBoundary = false)
        {
            var capture = new Recording
            {
                RecordingId = System.Guid.NewGuid().ToString("N"),
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,

                VesselName = Parsek.Recording.ResolveLocalizedName(vesselName),
                Points = new List<TrajectoryPoint>(Recording),
                OrbitSegments = new List<OrbitSegment>(OrbitSegments),
                PartEvents = new List<PartEvent>(PartEvents),
                FlagEvents = new List<FlagEvent>(FlagEvents),
                SegmentEvents = new List<SegmentEvent>(SegmentEvents),
                PreLaunchFunds = PreLaunchFunds,
                PreLaunchScience = PreLaunchScience,
                PreLaunchReputation = PreLaunchReputation,
                RewindSaveFileName = RewindSaveFileName,
                RewindReservedFunds = RewindReservedFunds,
                RewindReservedScience = RewindReservedScience,
                RewindReservedRep = RewindReservedRep,
                TrackSections = Parsek.Recording.DeepCopyTrackSections(TrackSections),
                StartBodyName = StartBodyName,
                StartBiome = StartBiome,
                StartSituation = StartSituation,
                LaunchSiteName = LaunchSiteName
            };
            // Clear after first capture — prevents chain children from inheriting root's rewind save
            RewindSaveFileName = null;

            LiveVesselSnapshot.SnapshotVessel(
                capture,
                isDestroyed,
                snapshotVessel,
                destroyedFallbackSnapshot ?? lastGoodVesselSnapshot);
            GetFinalizationCacheForRecording(capture);
            capture.StartResources = pendingStartResources;
            capture.EndResources = VesselSnapshotOps.ExtractResourceManifest(capture.VesselSnapshot);
            ParsekLog.Verbose("Recorder", $"BuildCaptureRecording: captured {capture.EndResources?.Count ?? 0} end resource type(s)");
            capture.StartInventory = pendingStartInventory;
            capture.StartInventorySlots = pendingStartInventorySlots;
            int endInvSlots;
            capture.EndInventory = VesselSnapshotOps.ExtractInventoryManifest(capture.VesselSnapshot, out endInvSlots);
            capture.EndInventorySlots = endInvSlots;
            ParsekLog.Verbose("Recorder", $"BuildCaptureRecording: captured {capture.EndInventory?.Count ?? 0} end inventory item type(s), {endInvSlots} slot(s)");
            capture.StartCrew = pendingStartCrew;
            capture.EndCrew = VesselSpawner.ExtractCrewManifest(capture.VesselSnapshot);
            ParsekLog.Verbose("Recorder", $"BuildCaptureRecording: captured {capture.EndCrew?.Count ?? 0} end crew trait(s)");
            // Launch-unique identity: prefer the value captured at record-start; otherwise backfill
            // from this capture's own snapshot `pid` (a capture that did not go through a guid-aware
            // StartRecording still carries its launch guid in the snapshot).
            capture.RecordedVesselGuid = !string.IsNullOrEmpty(pendingRecordedVesselGuid)
                ? pendingRecordedVesselGuid
                : VesselLaunchIdentity.TryReadVesselGuid(capture.VesselSnapshot);
            // Forward the start-time controller identity. Never re-derive from the live
            // stop/remnant vessel here: a destructive breakup leaves a 1-part decoupler
            // with no command authority, and re-capturing would clobber the recorded
            // controllable identity used by the BG go-on-rails Destroyed override.
            capture.Controllers = pendingStartControllers != null
                ? new List<ControllerInfo>(pendingStartControllers)
                : null;
            ParsekLog.Verbose("Recorder", $"BuildCaptureRecording: forwarded {capture.Controllers?.Count ?? 0} start controller part(s)");

            // Forward the start-time RouteOriginProof onto the captured recording and
            // re-extract the end transport manifests scoped to the same part-pid set
            // captured at start. v0 limitation: if the transport decoupled parts between
            // start and end, those parts are absent from capture.VesselSnapshot and silently
            // drop out of the end manifest. Acceptable for the logistics v0 contract.
            RouteProofCapture.AttachEndManifestsAndForwardToCapture(
                capture,
                pendingRouteOriginProof,
                pendingRouteOriginProofStartPartPids,
                stopIsChainBoundary);
            // M2 run manifest (plan D3 rule 4): complete the END half from the
            // capture snapshot scoped to the START pid set - this is the only
            // path that completes a manifest (active stops: StopRecording,
            // chain boundary, pid change). ForceStop never builds a capture, so
            // its legs keep a start-only manifest and degrade to legacy.
            RouteProofCapture.CompleteRunCargoManifestAtStop(capture, pendingRouteRunManifest);
            // M2 harvest windows (plan D4): close any open window from the
            // capture snapshot (close-at-stop) and forward the leg's windows.
            CloseHarvestWindowsAtStopAndForward(capture);
            capture.GhostVisualSnapshot = initialGhostVisualSnapshot != null
                ? initialGhostVisualSnapshot.CreateCopy()
                : (capture.VesselSnapshot != null ? capture.VesselSnapshot.CreateCopy() : null);

            int densifiedCheckpointPoints = OrbitalCheckpointDensifier.DensifyRecording(capture);

            ParsekLog.Verbose("Recorder",
                $"Built capture recording: vessel=\"{vesselName}\", points={capture.Points.Count}, " +
                $"orbits={capture.OrbitSegments.Count}, partEvents={capture.PartEvents.Count}, " +
                $"checkpointDensifiedPoints={densifiedCheckpointPoints}, " +
                $"hasSnapshot={capture.VesselSnapshot != null}");

            return capture;
        }

        /// <summary>
        /// Shared finalization logic for StopRecording, StopRecordingForChainBoundary, and ForceStop.
        /// Finalizes orbit segments, closes TrackSection, disconnects Harmony, and optionally
        /// emits terminal events and clears RELATIVE mode state.
        /// </summary>
        /// <param name="emitTerminalEvents">True to emit terminal engine/RCS/robotic shutdown events.</param>
        /// <param name="clearRelativeMode">True to clear RELATIVE mode state (ForceStop passes false
        /// to preserve current behavior — it did not have RELATIVE cleanup before extraction).</param>
        /// <param name="sampleBoundaryOnRails">True to sample a boundary point if on rails at stop time.</param>
        /// <param name="logTag">Tag for the RELATIVE mode cleared log message.</param>
        private void FinalizeRecordingState(bool emitTerminalEvents, bool clearRelativeMode,
            bool sampleBoundaryOnRails, string logTag)
        {
            // Finalize in-progress orbit segment
            if (isOnRails)
            {
                currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
                OrbitSegments.Add(currentOrbitSegment);
                AddOrbitSegmentToCurrentTrackSection(currentOrbitSegment);
                isOnRails = false;

                if (sampleBoundaryOnRails)
                {
                    Vessel v = FlightGlobals.ActiveVessel;
                    if (v != null) SamplePosition(v);
                }
            }

            // Close final TrackSection
            CloseCurrentTrackSection(Planetarium.GetUniversalTime());

            // Clear RELATIVE mode state
            if (clearRelativeMode)
            {
                if (isRelativeMode)
                {
                    ParsekLog.Info("Anchor",
                        $"RELATIVE mode cleared on {logTag}: anchorRecordingId={currentAnchorRecordingId ?? "(none)"} " +
                        $"diagnosticPid={currentAnchorPid}");
                }
                isRelativeMode = false;
                ClearCurrentRecordingAnchor();
            }

            // Disconnect from Harmony patch
            Patches.PhysicsFramePatch.ActiveRecorder = null;
            UnsubscribePartEvents();
            IsRecording = false;

            // Finalize diagnostics growth rate tracking
            if (DiagnosticsState.hasActiveGrowthRate)
            {
                var finalGr = DiagnosticsState.activeGrowthRate;
                ParsekLog.Verbose("Diagnostics", FormatGrowthRateAtStop(finalGr));
                DiagnosticsState.hasActiveGrowthRate = false;
            }

            // Optionally emit terminal shutdown events for engines/RCS/robotics still active (bug #108)
            if (emitTerminalEvents)
            {
                double terminalUT = Planetarium.GetUniversalTime();
                var terminalEvts = EmitTerminalEngineAndRcsEvents(
                    activeEngineKeys, activeRcsKeys, activeRoboticKeys,
                    lastRoboticPosition, terminalUT, "Recorder", thrustingJetpackParts);
                PartEvents.AddRange(terminalEvts);
                // Save the exact terminal events so ResumeAfterFalseAlarm can remove them
                // by content-match (#287). Index-based RemoveRange is brittle across unstable
                // sort paths and any code path that appends events between stop and resume.
                lastEmittedTerminalEvents = terminalEvts;
            }
            else
            {
                lastEmittedTerminalEvents = null;
            }

            // Sort part/flag events chronologically with STABLE semantics so same-UT
            // events retain their insertion order. See #263/#287 for the root-cause
            // narrative — LINQ OrderBy is stable; List<T>.Sort(Comparison) is not.
            StableSortPartEventsByUT();

            // FlagEvents also uses stable sort for consistency — bug #287 established
            // that every flush/merge site should use stable semantics so future code
            // that adds a rollback path or high-frequency flag emission cannot silently
            // regress on same-UT ordering.
            var sortedFlags = StableSortByUT(FlagEvents, e => e.ut);
            FlagEvents.Clear();
            FlagEvents.AddRange(sortedFlags);
        }

        /// <summary>
        /// Sorts <see cref="PartEvents"/> by UT with stable semantics (equal-UT events
        /// keep their insertion order). Uses LINQ <c>OrderBy</c> which is documented as
        /// stable — unlike <see cref="List{T}.Sort(System.Comparison{T})"/> which uses
        /// introspective sort and may reorder equal elements. See bug #263.
        /// </summary>
        internal void StableSortPartEventsByUT()
        {
            // The static helper handles 0/1-element lists by returning a new empty/copy
            // list — Clear+AddRange stays safe for those cases, no need for a short-circuit.
            var sorted = StableSortPartEventsByUT(PartEvents);
            PartEvents.Clear();
            PartEvents.AddRange(sorted);
        }

        /// <summary>
        /// Static helper for stable UT-sorting of a <see cref="PartEvent"/> list. Returns a
        /// new list with equal-UT events in their original insertion order. Call sites that
        /// merge multiple event sources (tree flushes, session merger, recording optimizer)
        /// must use this instead of <c>List&lt;T&gt;.Sort(Comparison)</c> to preserve the
        /// terminal-vs-ignited ordering at chain boundaries (#263, #287).
        /// Always returns a NEW list (even for null/small inputs) so callers can safely
        /// Clear the source list before copying back, without aliasing issues.
        /// </summary>
        internal static List<PartEvent> StableSortPartEventsByUT(List<PartEvent> events)
        {
            return StableSortByUT(events, e => e.ut);
        }

        /// <summary>
        /// Generic stable UT-sort helper for any event type with a UT double. Returns a NEW
        /// list (never the same reference as input) so callers can safely Clear+AddRange.
        /// Uses LINQ <c>OrderBy</c> which is documented stable — same-UT events keep their
        /// insertion order. Used for <see cref="FlagEvent"/> and <see cref="SegmentEvent"/>
        /// sorts at flush/merge sites so the sort semantics stay consistent with the
        /// <see cref="PartEvent"/> path and future same-UT hazards cannot silently regress.
        /// </summary>
        internal static List<T> StableSortByUT<T>(List<T> list, System.Func<T, double> utSelector)
        {
            if (list == null) return new List<T>();
            if (list.Count < 2) return new List<T>(list);
            return list.OrderBy(utSelector).ToList();
        }

        /// <summary>
        /// Removes the exact terminal events saved by the most recent FinalizeRecordingState
        /// call from <see cref="PartEvents"/> by content-matching on (ut, pid, eventType,
        /// moduleIndex). Robust against sort reordering — unlike the index-based RemoveRange
        /// it replaces (#287). Returns the number of terminal events removed.
        /// </summary>
        internal int RemoveLastEmittedTerminals()
        {
            if (lastEmittedTerminalEvents == null || lastEmittedTerminalEvents.Count == 0)
                return 0;

            int removed = 0;
            int notFound = 0;
            for (int i = 0; i < lastEmittedTerminalEvents.Count; i++)
            {
                var target = lastEmittedTerminalEvents[i];
                int idx = FindTerminalEventIndex(PartEvents, target);
                if (idx >= 0)
                {
                    PartEvents.RemoveAt(idx);
                    removed++;
                }
                else
                {
                    notFound++;
                }
            }

            if (notFound > 0)
            {
                ParsekLog.Warn("Recorder",
                    $"RemoveLastEmittedTerminals: {notFound} terminal event(s) not found in PartEvents " +
                    $"(expected {lastEmittedTerminalEvents.Count}, removed {removed}) — possible race " +
                    $"condition with intervening flush/sort");
            }

            lastEmittedTerminalEvents = null;
            return removed;
        }

        /// <summary>
        /// Removes terminal events from an arbitrary PartEvent list using the same
        /// content-matching logic as RemoveLastEmittedTerminals. Used by callers that
        /// need to clean terminals from a COPY of the recorder's events (e.g.,
        /// CaptureAtStop.PartEvents) rather than the recorder's own live list.
        /// Bug #299 — CaptureAtStop may bake terminals before the caller can call
        /// RemoveLastEmittedTerminals on the recorder's internal list.
        /// </summary>
        internal static int RemoveTerminalsFromList(List<PartEvent> targetList, List<PartEvent> terminals)
        {
            if (targetList == null || terminals == null || terminals.Count == 0)
                return 0;

            int removed = 0;
            for (int i = 0; i < terminals.Count; i++)
            {
                int idx = FindTerminalEventIndex(targetList, terminals[i]);
                if (idx >= 0)
                {
                    targetList.RemoveAt(idx);
                    removed++;
                }
            }
            return removed;
        }

        /// <summary>
        /// Pure helper: finds the index of the first <see cref="PartEvent"/> in
        /// <paramref name="list"/> that matches <paramref name="target"/> by
        /// (ut, partPersistentId, eventType, moduleIndex). Returns -1 if not found.
        /// Ignores <c>partName</c> and <c>value</c> because terminal events stamp
        /// <c>partName="unknown"</c> while a natural event with the same (ut, pid, type,
        /// midx) tuple would not realistically coexist in the list (the active-key set
        /// gates both emission sites).
        /// </summary>
        internal static int FindTerminalEventIndex(List<PartEvent> list, PartEvent target)
        {
            if (list == null) return -1;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e.ut == target.ut
                    && e.partPersistentId == target.partPersistentId
                    && e.eventType == target.eventType
                    && e.moduleIndex == target.moduleIndex)
                {
                    return i;
                }
            }
            return -1;
        }

        public void StopRecording()
        {
            FinalizeRecordingState(
                emitTerminalEvents: true,
                clearRelativeMode: true,
                sampleBoundaryOnRails: true,
                logTag: "recording stop");

            // Capture persistence artifacts at stop-time so later scene changes
            // don't depend on whatever vessel is currently active.
            CaptureAtStop = BuildCaptureRecording(
                FlightGlobals.ActiveVessel != null
                    ? FlightGlobals.ActiveVessel.vesselName
                    : "Unknown Vessel",
                VesselDestroyedDuringRecording);
            pendingJointChildPartOriginSeeds.Clear();

            double duration = Recording.Count > 0
                ? Recording[Recording.Count - 1].ut - Recording[0].ut
                : 0;

            ParsekLog.Info("Recorder", $"Recording stopped. {Recording.Count} points, {OrbitSegments.Count} orbit segments over {duration:F1}s");
            ParsekLog.ScreenMessage($"Recording STOPPED: {Recording.Count} points", 3f);
        }

        /// <summary>
        /// Stops recording silently for a chain boundary (dock/undock/boarding).
        /// Same as StopRecording but without the "STOPPED" screen message.
        /// </summary>
        public void StopRecordingForChainBoundary()
        {
            // FinalizeRecordingState stashes the exact terminal events it emits in
            // lastEmittedTerminalEvents; ResumeAfterFalseAlarm uses that list to remove
            // them by content-match if this stop turns out to be a false alarm (#287).
            FinalizeRecordingState(
                emitTerminalEvents: true,
                clearRelativeMode: true,
                sampleBoundaryOnRails: true,
                logTag: "chain boundary stop");

            CaptureAtStop = BuildCaptureRecording(
                FlightGlobals.ActiveVessel != null
                    ? FlightGlobals.ActiveVessel.vesselName
                    : "Unknown Vessel",
                VesselDestroyedDuringRecording,
                stopIsChainBoundary: true);

            double duration = Recording.Count > 0
                ? Recording[Recording.Count - 1].ut - Recording[0].ut
                : 0;

            ParsekLog.Info("Recorder", $"Recording stopped (chain boundary). {Recording.Count} points, {OrbitSegments.Count} orbit segments over {duration:F1}s");
        }

        /// <summary>
        /// Re-enables recording after a false-alarm split detection (e.g., joint break that
        /// only produced debris, undock of a non-trackable vessel). Restores the recorder to
        /// the active state it was in before StopRecordingForChainBoundary() was called.
        /// The internal Recording/OrbitSegments/PartEvents lists are still intact since
        /// StopRecordingForChainBoundary only copies them into CaptureAtStop but does not clear them.
        /// </summary>
        public void ResumeAfterFalseAlarm()
        {
            ResumeAfterFalseAlarm(keepRecordedVessel: false);
        }

        /// <summary>
        /// <paramref name="keepRecordedVessel"/> resumes on the vessel this recorder was
        /// recording (<see cref="RecordingVesselId"/>) instead of the active vessel. A split
        /// can move focus to the vessel that came off (stock switches focus when the control
        /// part leaves on it) before the deferred split check runs; resuming on the active
        /// vessel would then record the new vessel into this vessel's recording.
        /// </summary>
        public void ResumeAfterFalseAlarm(bool keepRecordedVessel)
        {
            if (IsRecording)
            {
                ParsekLog.Warn("Recorder", "ResumeAfterFalseAlarm called while already recording — ignoring");
                return;
            }

            Vessel v = keepRecordedVessel
                ? FindVesselByPid(RecordingVesselId)
                : FlightGlobals.ActiveVessel;
            if (v == null)
            {
                ParsekLog.Warn("Recorder", keepRecordedVessel
                    ? $"ResumeAfterFalseAlarm: recorded vessel pid={RecordingVesselId} not found - cannot resume"
                    : "ResumeAfterFalseAlarm: no active vessel — cannot resume");
                return;
            }

            IsRecording = true;

            // Undo orphaned terminal events added by FinalizeRecordingState (#255).
            // StopRecordingForChainBoundary emits EngineShutdown/RCSStop events for all
            // active engines. If the split turns out to be a false alarm (debris only),
            // these events are wrong — the engines are still running.
            //
            // Content-based removal (#287): we match each saved terminal event against
            // PartEvents by (ut, pid, type, midx) and remove the matching entry. This is
            // robust across unstable sort paths and any code that appends events between
            // stop and resume — index-based RemoveRange was brittle and left spurious
            // terminal Shutdowns in committed recordings, causing engine FX to turn off
            // permanently after booster staging (bug #287 2026-04-09 Kerbal X playtest).
            int contentRemoved = RemoveLastEmittedTerminals();
            if (contentRemoved > 0)
            {
                ParsekLog.Info("Recorder",
                    $"ResumeAfterFalseAlarm: removed {contentRemoved} orphaned terminal event(s) " +
                    $"(PartEvents count: {PartEvents.Count})");
            }

            // M2 harvest windows (plan D4 stop/false-alarm rule, mirroring the
            // #287 terminal-event unwind above): the abandoned chain-boundary
            // stop closed the open window with ClosedAtRecordingStop; reopen it
            // (clear end UT / flag / end manifest) so post-resume drilling
            // stays accounted inside the same window. The discarded
            // CaptureAtStop's window clones die with it.
            if (lastStopClosedHarvestWindow != null)
            {
                RouteHarvestCapture.ReopenWindow(lastStopClosedHarvestWindow);
                openHarvestWindow = lastStopClosedHarvestWindow;
                lastStopClosedHarvestWindow = null;
                ParsekLog.Info("Recorder",
                    $"ResumeAfterFalseAlarm: reopened harvest window closed by abandoned stop " +
                    $"window={openHarvestWindow.WindowId}");
            }

            // Restore rewind save from CaptureAtStop — StopRecordingForChainBoundary
            // cleared RewindSaveFileName after copying it to CaptureAtStop, so if we
            // resume, we need to reclaim it before discarding CaptureAtStop.
            if (CaptureAtStop != null && !string.IsNullOrEmpty(CaptureAtStop.RewindSaveFileName)
                && string.IsNullOrEmpty(RewindSaveFileName))
            {
                RewindSaveFileName = CaptureAtStop.RewindSaveFileName;
                RewindReservedFunds = CaptureAtStop.RewindReservedFunds;
                RewindReservedScience = CaptureAtStop.RewindReservedScience;
                RewindReservedRep = CaptureAtStop.RewindReservedRep;
            }

            CaptureAtStop = null;
            HasPendingJointBreakCheck = false;
            pendingJointChildPartOriginSeeds.Clear();

            // Re-register the Harmony patch and part event hooks
            Patches.PhysicsFramePatch.ActiveRecorder = this;
            SubscribePartEvents();

            // Update RecordingVesselId in case the vessel PID changed (unlikely but defensive)
            RecordingVesselId = v.persistentId;

            // StopRecordingForChainBoundary closes the active TrackSection before copying
            // CaptureAtStop. If we resume the same recorder without reopening a continuation
            // section, subsequent samples only extend the flat Recording list and the
            // section-authoritative sidecar truncates playback at the false-alarm boundary.
            double resumeUT = Planetarium.GetUniversalTime();
            OrbitSegment? resumeOrbitSegment = null;
            if (TryGetFalseAlarmResumeTrackSection(out var resumeSection)
                && resumeSection.referenceFrame == ReferenceFrame.OrbitalCheckpoint
                && v.packed)
            {
                resumeOrbitSegment = CreateOrbitSegmentWithRotation(v, resumeUT);
            }

            RestoreTrackSectionAfterFalseAlarm(resumeUT, resumeOrbitSegment);

            ParsekLog.Info("Recorder", $"Recording resumed after false alarm ({Recording.Count} points preserved" +
                (keepRecordedVessel ? $", kept recorded vessel pid={RecordingVesselId})" : ")"));
        }

        /// <summary>
        /// Reopens a continuation TrackSection after <see cref="StopRecordingForChainBoundary"/>
        /// was later treated as a false alarm. Preserves the last section's environment /
        /// reference-frame metadata and seeds the new section with the previous boundary point
        /// so section-authoritative playback stays continuous across the stop/resume seam.
        /// </summary>
        internal void RestoreTrackSectionAfterFalseAlarm(double ut)
        {
            RestoreTrackSectionAfterFalseAlarm(ut, null);
        }

        internal void RestoreTrackSectionAfterFalseAlarm(double ut, OrbitSegment? resumeOrbitSegment)
        {
            if (trackSectionActive)
                return;

            TrackSection? resumeSection = null;
            if (TryGetFalseAlarmResumeTrackSection(out var restoredSection))
                resumeSection = restoredSection;

            SegmentEnvironment resumeEnv = resumeSection.HasValue
                ? resumeSection.Value.environment
                : (environmentHysteresis != null
                    ? environmentHysteresis.CurrentEnvironment
                    : SegmentEnvironment.Atmospheric);
            ReferenceFrame resumeRef = resumeSection.HasValue
                ? resumeSection.Value.referenceFrame
                : (isRelativeMode ? ReferenceFrame.Relative : ReferenceFrame.Absolute);
            TrackSectionSource resumeSource = resumeSection.HasValue
                ? resumeSection.Value.source
                : TrackSectionSource.Active;

            string resumeAnchorRecordingId = null;
            uint resumeAnchorDiagnosticPid = 0u;
            if (resumeRef == ReferenceFrame.Relative)
            {
                resumeAnchorRecordingId = resumeSection.HasValue
                    ? resumeSection.Value.anchorRecordingId
                    : currentAnchorRecordingId;
                resumeAnchorDiagnosticPid = resumeSection.HasValue
                    ? resumeSection.Value.anchorVesselId
                    : currentAnchorPid;

                if (string.IsNullOrWhiteSpace(resumeAnchorRecordingId))
                {
                    ParsekLog.Warn("Anchor",
                        $"RELATIVE resume rejected: missing anchorRecordingId " +
                        $"legacyDiagnosticPid={resumeAnchorDiagnosticPid} " +
                        $"vesselPid={RecordingVesselId} — starting ABSOLUTE section instead");
                    resumeRef = ReferenceFrame.Absolute;
                    isRelativeMode = false;
                    ClearCurrentRecordingAnchor();
                }
                else if (!TryResolveRecordedAnchorPose(
                             resumeAnchorRecordingId,
                             ut,
                             out _,
                             out RelativeAnchorResolveFailure resumeRejectFailure))
                {
                    string resumeRejectReason = RelativeAnchorResolveFailure.ReasonOrFallback(
                        resumeRejectFailure,
                        "recorded-anchor-unresolved");
                    ParsekLog.Warn("Anchor",
                        $"RELATIVE resume rejected: anchorRecordingId={resumeAnchorRecordingId} " +
                        $"reason={resumeRejectReason} diagnosticPid={resumeAnchorDiagnosticPid} " +
                        $"vesselPid={RecordingVesselId} — starting ABSOLUTE section instead");
                    resumeAnchorRecordingId = null;
                    resumeRef = ReferenceFrame.Absolute;
                    isRelativeMode = false;
                    ClearCurrentRecordingAnchor();
                }
                else
                {
                    isRelativeMode = true;
                    currentAnchorRecordingId = resumeAnchorRecordingId;
                    currentAnchorPid = resumeAnchorDiagnosticPid;
                    currentAnchorCandidate = default;
                    hasCurrentAnchorCandidate = false;
                    currentAnchorDistanceMeters = double.NaN;
                }
            }
            else
            {
                isRelativeMode = false;
                ClearCurrentRecordingAnchor();
            }

            TrajectoryPoint? boundaryPoint = resumeSection.HasValue
                ? GetLastFrameFromTrackSection(resumeSection.Value)
                : (TrajectoryPoint?)null;
            TrajectoryPoint? absoluteBoundaryPoint = resumeSection.HasValue
                ? GetLastBodyFixedFrameFromTrackSection(resumeSection.Value)
                : (TrajectoryPoint?)null;

            // Frame-mismatch repair when resume-anchor validation downgraded a
            // RELATIVE resume to ABSOLUTE: `boundaryPoint` is the prior
            // Relative section's last frame, whose lat/lon/alt fields hold
            // anchor-local Cartesian metres (NOT body-fixed surface coords).
            // Seeding it into a freshly-opened ABSOLUTE section would write
            // a meaningless metre-scale "lat/lon/alt" sample at the seam,
            // re-introducing the corrupted-trajectory class this guard closes.
            // The parallel body-fixed primary already carries the focused
            // vessel's true body-fixed position at the same UT — swap
            // boundaryPoint to that primary value so the new ABSOLUTE
            // section's first sample matches its declared contract. A
            // current-gen Relative section that lacks a body-fixed primary
            // falls through with no boundary seed; the next normal sample
            // seeds the ABSOLUTE section cleanly.
            bool downgradedRelativeToAbsolute = resumeSection.HasValue
                && resumeSection.Value.referenceFrame == ReferenceFrame.Relative
                && resumeRef == ReferenceFrame.Absolute;
            if (downgradedRelativeToAbsolute)
            {
                if (absoluteBoundaryPoint.HasValue)
                {
                    ParsekLog.Verbose("Anchor",
                        $"RELATIVE->ABSOLUTE resume: substituting body-fixed-primary boundary point " +
                        $"at ut={absoluteBoundaryPoint.Value.ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                        $"in place of relative-frame boundary point");
                    boundaryPoint = absoluteBoundaryPoint;
                }
                else
                {
                    ParsekLog.Verbose("Anchor",
                        $"RELATIVE->ABSOLUTE resume: prior Relative section has no body-fixed-primary; " +
                        $"skipping boundary seed to avoid mis-framed sample");
                    boundaryPoint = null;
                }
                // The new section is ABSOLUTE, so absoluteBoundaryPoint will
                // be ignored by SeedBoundaryPoint anyway — clear it for
                // clarity.
                absoluteBoundaryPoint = null;
            }

            double sectionStartUT = ut;
            if (resumeRef != ReferenceFrame.OrbitalCheckpoint
                && boundaryPoint.HasValue
                && IsFinite(boundaryPoint.Value.ut)
                && boundaryPoint.Value.ut <= ut + SectionBoundaryUtEpsilon)
            {
                sectionStartUT = boundaryPoint.Value.ut;
                if (sectionStartUT < ut - SectionBoundaryUtEpsilon)
                {
                    ParsekLog.Verbose("Recorder",
                        $"ResumeAfterFalseAlarm: aligning reopened TrackSection startUT " +
                        $"from {ut.ToString("F3", CultureInfo.InvariantCulture)} " +
                        $"to boundary seed UT={sectionStartUT.ToString("F3", CultureInfo.InvariantCulture)}");
                }
            }

            // Disjoint-cover repair at the stop/resume seam (INV2-NO-DOUBLE-COVER,
            // 2026-07-22 B5 triage): FinalizeRecordingState closed the section at the
            // stop-frame UT, which overhangs the section's last recorded frame by up
            // to one sample gap. Reopening at the back-aligned boundary-seed UT would
            // leave the closed section's endUT inside the reopened section's span —
            // a one-tick interior overlap that SessionMerger.MergeTree normally heals
            // at commit, but which reaches the .prec sidecar raw when the tree is
            // persisted pending (vessel destroyed before commit). Nothing was
            // recorded in (boundary seed, stop-frame UT], so clamping the closed
            // endUT down to the seed loses no data and makes the sections touch
            // exactly.
            if (TryClampClosedSectionEndToBoundarySeed(
                    TrackSections, sectionStartUT, out double overhangEndUT, out string clampRefusalReason))
            {
                ParsekLog.Info("Recorder",
                    $"ResumeAfterFalseAlarm: clamped closed TrackSection endUT " +
                    $"from {overhangEndUT.ToString("F3", CultureInfo.InvariantCulture)} " +
                    $"to boundary seed UT={sectionStartUT.ToString("F3", CultureInfo.InvariantCulture)} " +
                    $"(stop-frame overhang held no recorded data; disjoint-cover)");
            }
            else if (clampRefusalReason != null)
            {
                // An overhang exists but the clamp refused: the interior overlap
                // survives in the pending tree and only SessionMerger.MergeTree at
                // commit can heal it (a destroy-before-commit persist would carry
                // it to the sidecar). Rare, diagnostically important — log it.
                TrackSection lastPersisted = TrackSections[TrackSections.Count - 1];
                ParsekLog.Warn("Recorder",
                    $"ResumeAfterFalseAlarm: closed TrackSection endUT " +
                    $"{lastPersisted.endUT.ToString("F3", CultureInfo.InvariantCulture)} overhangs " +
                    $"boundary seed UT={sectionStartUT.ToString("F3", CultureInfo.InvariantCulture)} " +
                    $"but clamp refused (reason={clampRefusalReason}, " +
                    $"sectionStartUT={lastPersisted.startUT.ToString("F3", CultureInfo.InvariantCulture)}) — " +
                    $"overlap left for commit-time merge resolution");
            }

            StartNewTrackSection(resumeEnv, resumeRef, sectionStartUT, resumeSource);

            if (resumeRef == ReferenceFrame.Relative)
            {
                currentTrackSection.anchorRecordingId = resumeAnchorRecordingId;
                currentTrackSection.anchorVesselId = 0u;
            }

            isOnRails = false;
            if (resumeRef == ReferenceFrame.OrbitalCheckpoint)
            {
                if (resumeOrbitSegment.HasValue)
                {
                    currentOrbitSegment = resumeOrbitSegment.Value;
                    isOnRails = true;
                }
                else
                {
                    ParsekLog.Warn("Recorder",
                        $"ResumeAfterFalseAlarm: reopened OrbitalCheckpoint TrackSection without restoring on-rails state at UT={ut.ToString("F2", CultureInfo.InvariantCulture)}");
                }
            }

            if (resumeRef != ReferenceFrame.OrbitalCheckpoint)
                SeedBoundaryPoint(boundaryPoint, absoluteBoundaryPoint);
        }

        /// <summary>
        /// Clamps the most recently persisted TrackSection's endUT down to the
        /// boundary-seed UT the reopened continuation section will start at, so the
        /// two sections touch exactly instead of interior-overlapping by the
        /// stop-frame overhang (INV2-NO-DOUBLE-COVER). Refuses to clamp — returning
        /// false — when there is no persisted section, when the last section does not
        /// actually overhang the seed, when the seed would undercut the section's own
        /// startUT, or when the section carries any authored payload (frames,
        /// body-fixed frames, or checkpoints) past the seed UT: in those shapes the
        /// overhang window holds real data and truncating it would lose coverage.
        /// <paramref name="refusalReason"/> is non-null only when an overhang exists
        /// but the clamp refused (would-invert-section / payload-past-seed) — the
        /// diagnostically important refusals where the persisted overlap survives;
        /// the common no-section / no-overhang no-ops leave it null.
        /// </summary>
        internal static bool TryClampClosedSectionEndToBoundarySeed(
            List<TrackSection> sections, double boundarySeedUT,
            out double previousEndUT, out string refusalReason)
        {
            previousEndUT = double.NaN;
            refusalReason = null;
            if (sections == null || sections.Count == 0)
                return false;
            if (!IsFinite(boundarySeedUT))
                return false;

            int lastIdx = sections.Count - 1;
            TrackSection last = sections[lastIdx];

            // No overhang past the seed (fp-noise tolerant) — nothing to repair.
            if (last.endUT <= boundarySeedUT + SectionBoundaryUtEpsilon)
                return false;

            // Never invert the section.
            if (boundarySeedUT < last.startUT - SectionBoundaryUtEpsilon)
            {
                refusalReason = "would-invert-section";
                return false;
            }

            // Any authored payload past the seed means the overhang window holds
            // real data (not the empty stop-frame tail) — refuse to truncate it.
            // Predicted checkpoints are included deliberately: conservative guard.
            if (SectionHasPayloadPastUT(last, boundarySeedUT + SectionBoundaryUtEpsilon))
            {
                refusalReason = "payload-past-seed";
                return false;
            }

            previousEndUT = last.endUT;
            last.endUT = boundarySeedUT;

            // Keep the stored sample rate consistent with the shrunk duration
            // (CloseCurrentTrackSection computed it against the pre-clamp endUT).
            if (last.frames != null && last.frames.Count > 1)
            {
                double clampedDuration = last.endUT - last.startUT;
                if (clampedDuration > 0)
                    last.sampleRateHz = (float)(last.frames.Count / clampedDuration);
            }

            sections[lastIdx] = last;
            return true;
        }

        private static bool SectionHasPayloadPastUT(TrackSection section, double ut)
        {
            if (section.frames != null && section.frames.Count > 0
                && section.frames[section.frames.Count - 1].ut > ut)
                return true;

            if (section.bodyFixedFrames != null && section.bodyFixedFrames.Count > 0
                && section.bodyFixedFrames[section.bodyFixedFrames.Count - 1].ut > ut)
                return true;

            if (section.checkpoints != null)
            {
                for (int i = 0; i < section.checkpoints.Count; i++)
                {
                    if (section.checkpoints[i].endUT > ut)
                        return true;
                }
            }

            return false;
        }

        private bool TryGetFalseAlarmResumeTrackSection(out TrackSection section)
        {
            // Prefer the recorder's most recently closed section snapshot only when it
            // carries a payload we can seed or resume from. Relative v7+ sections may
            // carry only body-fixed-primary frames, which are still valid resume payload.
            // A discarded zero-frame section has metadata but no coverage, so using it
            // would reopen at the later resume UT and leave the prior persisted
            // section's boundary uncovered.
            if (TrackSectionHasResumePayload(currentTrackSection))
            {
                section = currentTrackSection;
                return true;
            }

            if (TrackSections.Count > 0)
            {
                section = TrackSections[TrackSections.Count - 1];
                return true;
            }

            section = default;
            return false;
        }

        private static bool TrackSectionHasResumePayload(TrackSection section)
        {
            // Single shared payload predicate; see OrbitSegmentCheckpointBridge.HasSectionPayload.
            return OrbitSegmentCheckpointBridge.HasSectionPayload(section);
        }

        private void RefreshBackupSnapshot(Vessel vessel, string reason, bool force = false)
        {
            if (object.ReferenceEquals(vessel, null) || vessel == null) return;

            double ut = Planetarium.GetUniversalTime();
            if (!ShouldRefreshSnapshot(lastSnapshotRefreshUT, ut, snapshotRefreshIntervalUT, force))
                return;

            float startMs = Time.realtimeSinceStartup * 1000f;
            var snapshot = LiveVesselSnapshot.TryBackupSnapshot(vessel);
            float elapsedMs = Time.realtimeSinceStartup * 1000f - startMs;

            if (snapshot != null)
            {
                lastGoodVesselSnapshot = snapshot;
                lastSnapshotRefreshUT = ut;
            }
            else
            {
                ParsekLog.Warn("Recorder", $"Snapshot backup failed ({reason}): pid={vessel.persistentId}, " +
                    $"loaded={vessel.loaded}, packed={vessel.packed} — using previous snapshot");
            }

            if (elapsedMs >= snapshotPerfLogThresholdMs)
            {
                DiagnosticsState.health.snapshotRefreshSpikes++;
                ParsekLog.Verbose("Recorder",
                    $"Snapshot backup cost ({reason}): {elapsedMs:F1}ms " +
                    $"pid={vessel.persistentId}, points={Recording.Count}");
            }
        }

        internal bool RefreshFinalizationCache(Vessel vessel, string reason, bool force = false)
        {
            if (vessel == null)
                return false;

            double ut = Planetarium.GetUniversalTime();
            bool hasMeaningfulThrust = RecordingFinalizationCacheProducer.HasMeaningfulThrust(
                activeEngineKeys,
                lastThrottle,
                activeRcsKeys,
                lastRcsThrottle);
            bool inAtmosphere = RecordingFinalizationCacheProducer.IsInAtmosphere(vessel);
            string currentDigest = RecordingFinalizationCacheProducer.BuildVesselDigest(
                vessel,
                hasMeaningfulThrust);
            bool requiresPeriodicRefresh = hasMeaningfulThrust
                || inAtmosphere
                || RecordingFinalizationCacheProducer.IsSurfaceTerminalSituation(vessel.situation)
                || FinalizationCache == null
                || FinalizationCache.Status == FinalizationCacheStatus.Failed;

            if (!RecordingFinalizationCacheProducer.ShouldRefresh(
                    lastFinalizationCacheRefreshUT,
                    ut,
                    finalizationCacheRefreshIntervalUT,
                    force,
                    FinalizationCache?.LastObservedOrbitDigest,
                    currentDigest,
                    requiresPeriodicRefresh))
            {
                RecordingFinalizationCacheProducer.TryTouchObservedTerminalCache(
                    FinalizationCache,
                    ut,
                    reason,
                    currentDigest);
                return false;
            }

            RecordingFinalizationCache previous = FinalizationCache;
            Recording context = BuildFinalizationCacheContext(ut);
            RecordingFinalizationCache refreshed;
            bool success = RecordingFinalizationCacheProducer.TryBuildFromLiveVessel(
                context,
                vessel,
                ut,
                FinalizationCacheOwner.ActiveRecorder,
                reason,
                hasMeaningfulThrust,
                ActiveTree,
                out refreshed);

            if (!success
                && RecordingFinalizationCacheProducer.IsAlreadyClassifiedDestroyedSkip(refreshed))
            {
                FinalizationCache = refreshed;
                lastFinalizationCacheRefreshUT = ut;
                return false;
            }

            if (!success
                && RecordingFinalizationCacheProducer.TryPreservePreviousCacheAfterFailedRefresh(
                    previous,
                    refreshed,
                    ut,
                    reason,
                    currentDigest))
            {
                FinalizationCache = previous;
                lastFinalizationCacheRefreshUT = ut;
                return false;
            }
            FinalizationCache = refreshed;
            lastFinalizationCacheRefreshUT = ut;
            return success;
        }

        private Recording BuildFinalizationCacheContext(double currentUT)
        {
            Recording context = null;
            if (ActiveTree != null
                && !string.IsNullOrEmpty(ActiveTree.ActiveRecordingId)
                && ActiveTree.Recordings != null)
            {
                ActiveTree.Recordings.TryGetValue(ActiveTree.ActiveRecordingId, out context);
            }

            if (context != null)
                return context;

            return new Recording
            {
                RecordingId = FinalizationCache?.RecordingId,
                VesselPersistentId = RecordingVesselId,
                ExplicitEndUT = !double.IsNaN(LastRecordedUT) ? LastRecordedUT : currentUT
            };
        }

        internal RecordingFinalizationCache GetFinalizationCacheForRecording(Recording recording)
        {
            if (FinalizationCache == null || recording == null)
                return FinalizationCache;

            if (!string.IsNullOrEmpty(FinalizationCache.RecordingId))
                return FinalizationCache;

            FinalizationCache.RecordingId = recording.RecordingId;
            if (FinalizationCache.VesselPersistentId == 0)
            {
                if (recording.VesselPersistentId != 0)
                {
                    FinalizationCache.VesselPersistentId = recording.VesselPersistentId;
                }
                else
                {
                    FinalizationCache.VesselPersistentId = RecordingVesselId;
                    if (RecordingVesselId != 0)
                    {
                        ParsekLog.Verbose("FinalizerCache",
                            $"Backfilled active cache vessel pid from recorder state: rec={recording.RecordingId ?? "(null)"} " +
                            $"pid={RecordingVesselId}");
                    }
                }
            }
            return FinalizationCache;
        }

        internal static bool ShouldRefreshSnapshot(
            double lastSnapshotRefreshUT, double currentUT, double intervalUT, bool force)
        {
            if (force) return true;
            if (lastSnapshotRefreshUT == double.MinValue) return true;
            return currentUT - lastSnapshotRefreshUT >= intervalUT;
        }

        /// <summary>
        /// Bootstraps DiagnosticsState.avgBytesPerPoint from committed recordings' .prec files.
        /// Called once at recording start. If no committed recordings exist, keeps the default (85).
        /// </summary>
        private static void BootstrapAvgBytesPerPoint()
        {
            try
            {
                // [ERS-exempt] reason: samples on-disk .prec file sizes for a
                // bytes-per-point heuristic; includes NotCommitted / superseded
                // recordings so the average reflects real storage, not visible
                // storage (admin/debug surface per design §3.4).
                var committed = RecordingStore.CommittedRecordings;
                if (committed == null || committed.Count == 0)
                {
                    ParsekLog.Verbose("Diagnostics", "avgBytesPerPoint bootstrap: no committed recordings, using default 85");
                    return;
                }

                long totalBytes = 0;
                int totalPoints = 0;
                int filesChecked = 0;

                for (int i = 0; i < committed.Count; i++)
                {
                    var rec = committed[i];
                    if (rec == null || rec.Points == null || rec.Points.Count == 0)
                        continue;

                    string relativePath = RecordingPaths.BuildTrajectoryRelativePath(rec.RecordingId);
                    string fullPath = RecordingPaths.ResolveSaveScopedPath(relativePath);
                    if (string.IsNullOrEmpty(fullPath))
                        continue;

                    try
                    {
                        var fi = new FileInfo(fullPath);
                        if (fi.Exists)
                        {
                            totalBytes += fi.Length;
                            totalPoints += rec.Points.Count;
                            filesChecked++;
                        }
                    }
                    catch
                    {
                        // Skip files that can't be stat'd
                    }
                }

                if (totalPoints > 0)
                {
                    DiagnosticsState.avgBytesPerPoint = (double)totalBytes / totalPoints;
                    ParsekLog.Verbose("Diagnostics",
                        string.Format(CultureInfo.InvariantCulture,
                            "avgBytesPerPoint bootstrapped: {0:F1} bytes/pt from {1} files ({2} bytes, {3} points)",
                            DiagnosticsState.avgBytesPerPoint, filesChecked, totalBytes, totalPoints));
                }
                else
                {
                    ParsekLog.Verbose("Diagnostics", "avgBytesPerPoint bootstrap: no .prec files found, using default 85");
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose("Diagnostics", $"avgBytesPerPoint bootstrap failed: {ex.Message}");
            }
        }
    }
}
