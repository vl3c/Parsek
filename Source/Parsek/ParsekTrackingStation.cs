using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using KSP.UI.Screens.Mapview;
using Parsek.Patches;
using UnityEngine;
using UnityEngine.UI;

namespace Parsek
{
    /// <summary>
    /// Tracking station scene host for ghost map presence.
    /// Creates ghost ProtoVessels from committed recordings so ghosts appear
    /// in the tracking station vessel list with orbit lines and targeting.
    /// Per-frame lifecycle: removes/creates ghosts when UT crosses segment bounds.
    /// OnGUI draws icons for atmospheric phases (no ProtoVessel — direct rendering
    /// from trajectory data, same approach as ParsekUI.DrawMapMarkers in flight).
    /// </summary>
    // [ERS-exempt — Phase 3] ParsekTrackingStation pairs with GhostMapPresence
    // which keys ghost vessels by committed recording index. The count-change
    // detection + atmospheric-marker pass in this file use the same raw index
    // space; converting to EffectiveState.ComputeERS() would decouple marker
    // positions from the underlying ghost lifecycle bookkeeping.
    // TODO(phase 6+): migrate atmosCachedIndices + GhostMapPresence to
    // recording-id keying and route this file through ComputeERS().
    [KSPAddon(KSPAddon.Startup.TrackingStation, false)]
    public class ParsekTrackingStation : MonoBehaviour
    {
        private const string Tag = "TrackingStation";
        // Cadence of the TS ghost create/remove + orbit-refresh pass. Kept short so the
        // map stays responsive: a looped mission hands off between members (each one's
        // proto-vessel orbit line) every cycle, and at the old 2 s cadence the OUTGOING
        // member's orbit line was gone for up to ~2 s before the INCOMING member's was
        // created -- a visible handoff blackout (during a member's orbital phase the
        // orbit line is the ONLY thing on screen). A short interval makes that handoff
        // prompt, and also makes ordinary (non-loop) recordings refresh promptly. This is
        // cheap to run often: a no-mutation tick only re-scans sources and reseeds a few
        // already-tracked ghost orbits; the expensive create/destroy + stock vessel-list
        // rebuild fire only at actual boundaries regardless of cadence.
        private const float LifecycleCheckIntervalSec = 0.25f;
        private const float GhostPopupWidth = 180f;
        private const float MaterializedFocusRetryDurationSec = 20.0f;
        private const float MaterializedFocusRetryIntervalSec = 0.1f;
        private float nextLifecycleCheckTime;
        private PopupDialog currentGhostPopup;
        private string currentGhostPopupKey;
        private int ghostPopupOpenFrame;
        private MapObject atmosphericFocusTarget;
        private string atmosphericFocusRecordingId;
        private int atmosphericFocusCachedIndex = -1;
        private int ghostActionCacheFrame = -1;
        private double ghostActionCurrentUT;
        private Dictionary<uint, GhostChain> ghostActionChains = new Dictionary<uint, GhostChain>();
        private uint pendingMaterializedFocusPid;
        private string pendingMaterializedFocusReason;
        private float pendingMaterializedFocusDeadlineTime;
        private float nextMaterializedFocusAttemptTime;
        private int pendingMaterializedFocusAttempts;
        private bool pendingMaterializedFocusBaselineHasSelectedGhost;
        private uint pendingMaterializedFocusBaselineGhostPid;
        private string pendingMaterializedFocusBaselineRecordingId;
        private bool pendingMaterializedFocusBaselineSelectedPidAvailable;
        private uint pendingMaterializedFocusBaselineSelectedPid;

        /// <summary>Cached interpolation indices for atmospheric ghost icon rendering (per recording index).</summary>
        private readonly Dictionary<int, int> atmosCachedIndices = new Dictionary<int, int>();

        // Slice (iii) TS port: reusable per-recording head-UT buffer for the per-instance overlap marker
        // branch (mirror of ParsekUI.cs:67). Cleared (not reallocated) per overlap recording inside
        // GhostMapPresence.TryGetLiveOverlapHeadUTs, so drawing N markers on the one shared polyline in the
        // tracking station allocates nothing per frame.
        private static readonly List<(long cycle, double headUT)> tsOverlapHeadUtBuffer =
            new List<(long, double)>();

        /// <summary>Tracks the last known committed recording count for live-update detection.</summary>
        private int lastKnownCommittedCount;

        // Mission loop-unit descriptors for THIS frame (Phase F: TS span-clock parity). Built from
        // the SAME MissionLoopUnitBuilder.Build the flight engine and KSC consume, so a looped
        // Mission renders in the tracking station identically. Empty (LoopUnitSet.Empty) means no
        // Mission loops, which keeps the feature dormant and TS behavior byte-identical to before.
        // Rebuilt only when the cheap signature changes (cachedLoopUnits + lastLoopUnitSignature),
        // then read by BOTH the ProtoVessel lifecycle pass (passed into
        // GhostMapPresence.UpdateTrackingStationGhostLifecycle once per Update tick) and the OnGUI
        // atmospheric-marker pass (DrawAtmosphericMarkers reads cachedLoopUnits directly), so the
        // (allocating, Verbose-logging) Build never runs per OnGUI frame.
        private GhostPlaybackLogic.LoopUnitSet cachedLoopUnits = GhostPlaybackLogic.LoopUnitSet.Empty;
        private string lastLoopUnitSignature;

        /// <summary>
        /// Read-only accessor on the current frame's cached loop unit set.
        /// Exposed for <see cref="Parsek.Display.GhostTrajectoryPolylineRenderer"/>'s
        /// DDOL Driver: it has no direct handle to the per-scene controller's
        /// private <c>cachedLoopUnits</c>, so it looks the controller up
        /// via FindObjectOfType and reads through this accessor. The field
        /// itself stays private.
        /// </summary>
        internal GhostPlaybackLogic.LoopUnitSet CurrentCachedLoopUnits => cachedLoopUnits;

        // Phase 7a decision-only shadow (design §6.7): the TS scene adapter the ShadowRenderDriver runs
        // the new pipeline against. The shadow is always enabled (ShadowRenderDriver.Enabled is always
        // true now that 8e S4 dropped the director-drive gate; the Director pipeline is unconditional).
        private readonly MapRender.TrackingStationScene shadowScene = new MapRender.TrackingStationScene();

        internal enum AtmosphericMarkerSkipReason
        {
            None,
            NativeIconActive,
            NullRecording,
            Debris,
            NoTrajectoryPoints,
            OutsideTimeRange,
            SuppressedByChainFilter,
            OrbitSegmentActive
        }

        internal struct AtmosphericMarkerSummary
        {
            public string EventTypeName;
            public int Candidates;
            public int Drawn;
            public int CameraUnavailable;
            public int NoCommittedRecordings;
            public int NativeIconActive;
            public int NullRecording;
            public int Debris;
            public int NoTrajectoryPoints;
            public int OutsideTimeRange;
            public int SuppressedByChainFilter;
            public int OrbitSegmentActive;
            public int BracketMiss;
            public int MissingBody;
            public int LoopMemberHidden; // Phase F: member outside its loop window this cycle

            internal bool HasSignal =>
                Candidates > 0
                || Drawn > 0
                || CameraUnavailable > 0
                || NoCommittedRecordings > 0
                || NativeIconActive > 0
                || NullRecording > 0
                || Debris > 0
                || NoTrajectoryPoints > 0
                || OutsideTimeRange > 0
                || SuppressedByChainFilter > 0
                || OrbitSegmentActive > 0
                || BracketMiss > 0
                || MissingBody > 0
                || LoopMemberHidden > 0;
        }

        internal static string FormatAtmosphericMarkerSummary(AtmosphericMarkerSummary summary)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Atmospheric marker summary: event={0} candidates={1} drawn={2} cameraUnavailable={3} noCommitted={4} nativeIcon={5} nullRecording={6} debris={7} noPoints={8} outsideTimeRange={9} chainSuppressed={10} orbitSegment={11} bracketMiss={12} missingBody={13} loopMemberHidden={14}",
                string.IsNullOrEmpty(summary.EventTypeName) ? "(unknown)" : summary.EventTypeName,
                summary.Candidates,
                summary.Drawn,
                summary.CameraUnavailable,
                summary.NoCommittedRecordings,
                summary.NativeIconActive,
                summary.NullRecording,
                summary.Debris,
                summary.NoTrajectoryPoints,
                summary.OutsideTimeRange,
                summary.SuppressedByChainFilter,
                summary.OrbitSegmentActive,
                summary.BracketMiss,
                summary.MissingBody,
                summary.LoopMemberHidden);
        }

        private static void LogAtmosphericMarkerSummary(AtmosphericMarkerSummary summary)
        {
            if (!summary.HasSignal)
                return;

            ParsekLog.VerboseRateLimited(Tag,
                "atmos-marker-summary",
                FormatAtmosphericMarkerSummary(summary),
                2.0);
        }

        private static void CountAtmosphericMarkerSkip(
            ref AtmosphericMarkerSummary summary,
            AtmosphericMarkerSkipReason reason)
        {
            switch (reason)
            {
                case AtmosphericMarkerSkipReason.NativeIconActive:
                    summary.NativeIconActive++;
                    break;
                case AtmosphericMarkerSkipReason.NullRecording:
                    summary.NullRecording++;
                    break;
                case AtmosphericMarkerSkipReason.Debris:
                    summary.Debris++;
                    break;
                case AtmosphericMarkerSkipReason.NoTrajectoryPoints:
                    summary.NoTrajectoryPoints++;
                    break;
                case AtmosphericMarkerSkipReason.OutsideTimeRange:
                    summary.OutsideTimeRange++;
                    break;
                case AtmosphericMarkerSkipReason.SuppressedByChainFilter:
                    summary.SuppressedByChainFilter++;
                    break;
                case AtmosphericMarkerSkipReason.OrbitSegmentActive:
                    summary.OrbitSegmentActive++;
                    break;
            }
        }

        void Start()
        {
            int created = GhostMapPresence.CreateGhostVesselsFromCommittedRecordings();
            int renderersFixed = GhostMapPresence.EnsureGhostOrbitRenderers();
            int suppressedForGhosts = GhostMapPresence.CachedTrackingStationSuppressedIds?.Count ?? 0;

            // Run the first lifecycle tick on the first Update after all scene Start
            // methods have completed, so real-vessel handoff sees the loaded vessel list
            // instead of the earlier SpaceTracking.Awake precreate state.
            nextLifecycleCheckTime = 0f;
            atmosCachedIndices.Clear();
            lastKnownCommittedCount = RecordingStore.CommittedRecordings?.Count ?? 0;

            ParsekLog.Info(Tag,
                $"ParsekTrackingStation initialized: created {created} ghost vessel(s), " +
                $"fixed {renderersFixed} orbit renderer(s), " +
                $"trackingStationSuppressed={suppressedForGhosts}, " +
                "orbitSourceDiagnostics=aggregated");
        }

        void Update()
        {
            // Detect live recording commits (merge dialog, approval dialog) and force
            // an immediate lifecycle tick so proto-vessel ghosts appear without waiting
            // for the next LifecycleCheckIntervalSec tick.
            // NOTE: count-based detection has a blind spot if a recording is removed and
            // another added in the same frame (net zero change). This can't happen in TS
            // today — removals only occur via clear-all which resets the entire session.
            int currentCount = RecordingStore.CommittedRecordings?.Count ?? 0;
            if (currentCount != lastKnownCommittedCount)
            {
                ParsekLog.Info(Tag,
                    $"Committed recording count changed ({lastKnownCommittedCount} → {currentCount}) " +
                    "— forcing immediate lifecycle tick");
                lastKnownCommittedCount = currentCount;
                nextLifecycleCheckTime = 0f; // force tick this frame
            }

            if (GhostTrackingStationSelection.HasSelectedGhost)
                RefreshGhostActionCache();
            UpdatePendingMaterializedFocus();
            UpdateSelectedGhostPopup();
            UpdateAtmosphericFocusTarget();

            // Phase F: refresh the Mission loop-unit set once per Update tick (change-detected),
            // before both the lifecycle pass below and the OnGUI atmospheric-marker pass that reads
            // the cached field. The OnGUI pass never rebuilds.
            DriveMissionLoopUnits(RecordingStore.CommittedRecordings);

            // Phase 7a decision-only shadow: run the new map-render pipeline over the TS map ghosts and
            // reconcile each intent against the OLD path's truth via MapRenderProbe. Writes NOTHING to
            // the stock surfaces. Gated on the off-by-default mapRenderTracing setting; try/catch so a
            // diagnostic bug can never break the TS update. Runs every Update tick (cachedLoopUnits is
            // fresh) before the rate-limited lifecycle pass.
            if (MapRender.ShadowRenderDriver.Enabled)
            {
                try
                {
                    shadowScene.SetFrameInputs(cachedLoopUnits, Planetarium.GetUniversalTime());
                    MapRender.ShadowRenderDriver.RunFrame(shadowScene);
                }
                catch (System.Exception ex)
                {
                    ParsekLog.VerboseRateLimited("MapRender", "ts-shadow-run-exception",
                        "TS shadow RunFrame threw (suppressed): " + ex.GetType().Name + ": " + ex.Message, 10.0);
                }
            }

            if (Time.time < nextLifecycleCheckTime) return;
            nextLifecycleCheckTime = Time.time + LifecycleCheckIntervalSec;

            GhostMapPresence.UpdateTrackingStationGhostLifecycle(cachedLoopUnits);
        }

        /// <summary>
        /// Recompute the Mission LoopUnitSet only when its inputs change (cheap signature compare),
        /// caching it on the addon. Mirrors <c>ParsekKSC.DriveMissionLoopUnits</c> /
        /// <c>ParsekFlight.DriveMissionLoopUnits</c>: the SAME committed list passed to
        /// <see cref="MissionLoopUnitBuilder.BuildSignature"/> and
        /// <see cref="MissionLoopUnitBuilder.Build"/> is what the per-recording lifecycle and
        /// atmospheric passes iterate, so member indices align. Build (and its Verbose log) only
        /// fires on an actual input change; the cached set is read every frame by both passes.
        /// </summary>
        private void DriveMissionLoopUnits(IReadOnlyList<Recording> committed)
        {
            double autoLoopIntervalSeconds = ParsekSettings.Current?.autoLoopIntervalSeconds
                                             ?? LoopTiming.DefaultLoopIntervalSeconds;
            // Phase-lock (mission periodicity): the same live-body seam the flight engine + KSC use,
            // so all three scenes phase-lock identically.
            IBodyInfo bodyInfo = FlightGlobalsBodyInfo.Instance;
            TransitedBodyRotationMode tbrMode = ParsekSettings.Current?.TransitedBodyRotationMode
                                                ?? TransitedBodyRotationMode.Loose;

            // === Supply-route render union (Phase 3) ===
            // Same append shape as ParsekFlight / ParsekKSC. The union lands HERE inside
            // DriveMissionLoopUnits writing to cachedLoopUnits, which is invoked (in Update) BEFORE
            // GhostMapPresence.UpdateTrackingStationGhostLifecycle(cachedLoopUnits) reads it, so that
            // read sees the unioned set automatically — no separate field, no reorder.
            double routeSelectUT = Planetarium.GetUniversalTime();
            IReadOnlyList<Mission> routeMissions =
                Parsek.Logistics.RouteGhostDriverSelector.SelectGhostDrivingBackingMissions(
                    Parsek.Logistics.RouteStore.CommittedRoutes, routeSelectUT);
            List<Mission> unioned = new List<Mission>(MissionStore.Missions);
            unioned.AddRange(routeMissions);

            string signature = MissionLoopUnitBuilder.BuildSignature(
                unioned, RecordingStore.CommittedTrees, committed, autoLoopIntervalSeconds, bodyInfo, tbrMode);
            if (!string.Equals(signature, lastLoopUnitSignature, System.StringComparison.Ordinal))
            {
                cachedLoopUnits = MissionLoopUnitBuilder.Build(
                    unioned, RecordingStore.CommittedTrees, committed, autoLoopIntervalSeconds, bodyInfo, tbrMode);
                lastLoopUnitSignature = signature;
                // Drop cached per-window re-aim adapters so a stale window transfer can't survive a
                // recording / mission edit made from the Tracking Station (mirrors ParsekFlight).
                Parsek.Reaim.ReaimPlaybackResolver.Shared.Clear();
                ParsekLog.Verbose("Mission",
                    $"TS Mission loop units rebuilt (signature changed): committed={committed?.Count ?? 0} " +
                    $"routeMissions={routeMissions.Count}");
            }
        }

        void OnGUI()
        {
            // The Esc / pause overlay lives on KSP's Canvas and sorts above
            // our IMGUI layer, so without this gate the ghost icons and ghost
            // action panel visually punch through the pause menu. Both the
            // Layout pass AND the Repaint draw are skipped so width-clamped
            // layouts can't flicker between events.
            if (PauseMenuGate.IsPauseMenuOpen())
                return;

            DrawAtmosphericMarkers();
        }

        private void DrawAtmosphericMarkers()
        {
            // Draw icons for recordings in atmospheric phases (no ProtoVessel).
            // Position comes directly from trajectory point interpolation —
            // same approach as ParsekUI.DrawMapMarkers in the flight scene.
            // NOTE: we must let MouseDown events through so the hover/click
            // sticky-label handling in MapMarkerRenderer (#386) can consume
            // clicks. Previously this gate short-circuited on anything but
            // Repaint, which made the click-to-pin interaction dead in TS.
            Event currentEvent = Event.current;
            EventType etype = currentEvent.type;
            var summary = new AtmosphericMarkerSummary
            {
                EventTypeName = etype.ToString()
            };
            bool pointerOverGhostPopup =
                IsAtmosphericMarkerClickEvent(etype)
                && IsMouseOverCurrentGhostPopup();
            if (ShouldBlockAtmosphericMarkerClickForGhostPopup(
                    etype,
                    pointerOverGhostPopup))
            {
                ParsekLog.VerboseRateLimited(Tag,
                    "atmos-marker-popup-click-blocked",
                    "Atmospheric marker click ignored: pointer over ghost popup event=" + etype,
                    1.0);
                return;
            }

            // No Parsek window is hosted in the Tracking Station scene, so a
            // marker click can never land on a Parsek window here.
            if (!ShouldProcessAtmosphericMarkerEvent(etype, pointerOverParsekWindow: false))
                return;
            if (PlanetariumCamera.Camera == null)
            {
                summary.CameraUnavailable++;
                LogAtmosphericMarkerSummary(summary);
                return;
            }

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || committed.Count == 0)
            {
                summary.NoCommittedRecordings++;
                LogAtmosphericMarkerSummary(summary);
                return;
            }

            double currentUT = Planetarium.GetUniversalTime();

            var suppressed = GhostMapPresence.CachedTrackingStationSuppressedIds;
            // Phase F: the per-frame cached Mission loop-unit set (built by DriveMissionLoopUnits in
            // Update, never rebuilt here). Empty => the substitution below is inert.
            var loopUnits = cachedLoopUnits;

            bool traceOn = MapRenderTrace.IsEnabled;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                summary.Candidates++;

                // ---- Marker-decision tracer state (per-pid, change-based; gated, off in normal play) ----
                // Mirrors the flight-map DrawMapMarkers tracer: one change-based line per ghost saying
                // WHY its atmospheric marker drew or was skipped, carrying the four
                // ResolveMarkerDrawDecision disjuncts (resolved only when a proto ghost vessel exists,
                // false otherwise). This NON-OVERLAP single-marker path does NOT ride the polyline today
                // (it draws at the body-fixed head), so the ride reason stays NotAttempted and the
                // position source is "traj" - honest. The OVERLAP instance path
                // (DrawOneTsOverlapInstanceMarker) DOES ride the shared polyline and emits its OWN
                // per-instance decision line carrying the real ride reason + posSource=polyline (GAP-1),
                // so this closure is correct ONLY for the single-marker tail below.
                //
                // C-1: this path also threads the raw AtmosphericMarkerSkipReason token as a tsSkip=
                // field so the finer TS taxonomy (which MapSkipReasonToMarkerOutcome folds into the
                // shared MarkerOutcome) survives on the per-ghost decision line, not just the aggregate
                // summary. Pass tsSkipReason=None on the draw path -> the optional field is omitted.
                bool decDirectorTraced = false, decPolylineOwning = false,
                    decIconSuppressed = false, decShouldDraw = false;
                void EmitMarkerDecision(MapRenderTrace.MarkerOutcome outcome,
                    AtmosphericMarkerSkipReason tsSkipReason = AtmosphericMarkerSkipReason.None)
                {
                    if (!traceOn || rec == null || string.IsNullOrEmpty(rec.RecordingId))
                        return;
                    MapRenderTrace.EmitMarkerDecisionOnChange(
                        MapRenderTrace.RenderSurface.AtmosphericMarker, rec.RecordingId, currentUT,
                        MapRenderTrace.BuildMarkerDecisionSignature(
                            i, rec.VesselName, decDirectorTraced, decPolylineOwning,
                            decIconSuppressed, decShouldDraw, outcome,
                            MapRenderTrace.MarkerRideReason.NotAttempted, -1, "traj",
                            tsSkipReason: tsSkipReason == AtmosphericMarkerSkipReason.None
                                ? null
                                : AtmosphericMarkerSkipReasonToken(tsSkipReason)));
                }

                // ---- Slice (iii) TS port: per-instance overlap markers riding the ONE shared polyline ----
                // Port of the flight-map DrawMapMarkers per-instance branch (ParsekUI.cs:1191-1252) to the
                // tracking-station marker path. For an OVERLAPPING looped mission rendered via the trajectory
                // polyline, draw N markers - one per live overlap instance - each at its own
                // ComputeOverlapCyclePlaybackUT head along the SINGLE shared polyline (the polyline Driver
                // draws once keyed by RecordingId in TS too), so the tracking station shows the SAME N
                // markers the flight scene does. Gated behind ShouldDriveOverlapPerInstance (inside
                // TryGetLiveOverlapHeadUTs = overlap recordings only, 8e S4): non-overlap recordings
                // return false and fall straight through to the UNCHANGED span-clock effUT block + 8c proto
                // gate (ClassifyAtmosphericMarkerSkip) + single-marker tail below, byte-identically.
                //
                // HOISTED above the span-clock effUT/renderHidden block AND the
                // ClassifyAtmosphericMarkerSkip 8c proto gate (which consults ONLY the newest instance's
                // pid). This matches the flight slice (iii) hoist: a MIXED overlap recording - newest cycle
                // mid-orbit (visible proto icon) while OLDER cycles are simultaneously in a non-orbital
                // reentry/descent phase - must not get pre-empted by the newest-only skip and silently drop
                // the older instances' polyline markers. Safe to hoist because the PER-CYCLE no-double rule
                // inside DrawOneTsOverlapInstanceMarker (TryGetOverlapInstancePidForCycle +
                // ShouldDrawNonProtoMarkerForGhost) makes the correct per-cycle orbital-vs-polyline decision.
                // Uses cachedLoopUnits (the SAME TS span-clock set the lifecycle + legacy marker path read)
                // and the TS-valid currentUT = Planetarium.GetUniversalTime(). Debris is guarded here to
                // preserve the IsDebris filter the 8c block below applies.
                if (i < committed.Count && rec != null && !rec.IsDebris
                    && GhostMapPresence.TryGetLiveOverlapHeadUTs(
                        rec, i, committed, cachedLoopUnits, currentUT, tsOverlapHeadUtBuffer))
                {
                    int instancesDrawn = 0;
                    int liveCycles = tsOverlapHeadUtBuffer.Count;
                    for (int hi = 0; hi < tsOverlapHeadUtBuffer.Count; hi++)
                    {
                        var (cycle, headUT) = tsOverlapHeadUtBuffer[hi];
                        if (DrawOneTsOverlapInstanceMarker(i, committed, rec, headUT, cycle))
                            instancesDrawn++;
                    }
                    summary.Candidates += instancesDrawn > 1 ? instancesDrawn - 1 : 0;
                    summary.Drawn += instancesDrawn;

                    EmitMarkerDecision(MapRenderTrace.MarkerOutcome.DrawnNonProto);

                    // One rate-limited per-recording summary (batch-counting convention): how many of the N
                    // live cycles actually drew a polyline marker this frame (an instance skips when its head
                    // is out-of-window / between legs / off-line, or its cycle is drawn by a live
                    // non-suppressed proto icon). Mirrors the flight branch's liveCycles/drawn summary.
                    int recIdxForLog = i;
                    string overlapNameForLog = rec.VesselName;
                    ParsekLog.VerboseRateLimited(Tag,
                        "ts-overlap-instance-markers-"
                            + recIdxForLog.ToString(CultureInfo.InvariantCulture),
                        () => string.Format(CultureInfo.InvariantCulture,
                            "TS overlap per-instance markers: rec={0} vessel={1} liveCycles={2} drawn={3}",
                            recIdxForLog, overlapNameForLog ?? "Ghost", liveCycles, instancesDrawn),
                        2.0);
                    continue; // per-instance markers drew (or all skipped) — bypass effUT + 8c gate + tail
                }

                // Phase F: substitute the shared Mission span-clock loopUT for the live UT when this
                // committed index is a loop-unit member. A member outside its loop window this cycle
                // is skipped (no marker). Inert (effUT == currentUT, renderHidden false) for
                // non-members and when loopUnits is Empty.
                double effUT = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                    i, rec != null ? rec.StartUT : currentUT, rec != null ? rec.EndUT : currentUT,
                    currentUT, loopUnits, out bool renderHidden);
                if (renderHidden)
                {
                    summary.LoopMemberHidden++;
                    EmitMarkerDecision(MapRenderTrace.MarkerOutcome.SkippedLoopHidden);
                    continue;
                }

                // Tracer: surface the decision disjuncts when a proto ghost vessel exists (the only
                // case ShouldDrawNonProtoMarkerForGhost is consulted). Behavior-identical to the
                // bare predicate ClassifyAtmosphericMarkerSkip already calls; this only reads them out.
                if (traceOn && GhostMapPresence.HasGhostVesselForRecording(i))
                {
                    uint ghostPidTrace = GhostMapPresence.GetGhostVesselPidForRecording(i);
                    if (ghostPidTrace != 0)
                        decShouldDraw = GhostMapPresence.ShouldDrawNonProtoMarkerForGhost(
                            ghostPidTrace, out decDirectorTraced,
                            out decPolylineOwning, out decIconSuppressed);
                }

                AtmosphericMarkerSkipReason skipReason =
                    ClassifyAtmosphericMarkerSkip(rec, i, effUT, suppressed);
                if (skipReason != AtmosphericMarkerSkipReason.None)
                {
                    CountAtmosphericMarkerSkip(ref summary, skipReason);
                    // C-1: pass the RAW skip reason so the finer TS taxonomy (folded away by
                    // MapSkipReasonToMarkerOutcome) survives as the tsSkip= field on this ghost's line.
                    EmitMarkerDecision(MapSkipReasonToMarkerOutcome(skipReason), skipReason);
                    continue;
                }

                if (!atmosCachedIndices.ContainsKey(i))
                    atmosCachedIndices[i] = -1;
                int cached = atmosCachedIndices[i];
                bool rodePolyline = false;
                bool resolved = TryResolveRecordingWorldPosition(
                    rec,
                    effUT,
                    ref cached,
                    out Vector3d worldPos,
                    out _,
                    out TrajectoryPoint sampledPoint,
                    out string resolveReason);
                if (resolved)
                {
                    atmosCachedIndices[i] = cached;
                }
                else if (Parsek.Display.GhostTrajectoryPolylineRenderer.TryAnchorMarkerToPolyline(
                        rec.RecordingId, effUT, out Vector3 riddenWorldPos))
                {
                    // Playtest-12 follow-up (icon vanished on the gap-filled landing chord): the
                    // recording-side resolver has nothing to bracket inside a FRAMELESS recorded span
                    // (the OrbitalCheckpoint section under the below-surface descent), but the POLYLINE
                    // is drawing that span - including the conic gap-fill points, which live only in
                    // the renderer's leg cache. RIDE the drawn line (the same contract the flight-map
                    // marker and the TS overlap-instance markers use) so the icon stays on the curve
                    // instead of vanishing. The ride only succeeds when a leg containing the head
                    // actually drew this frame, so this can never paint a marker for an undrawn phase.
                    worldPos = riddenWorldPos;
                    rodePolyline = true;
                    resolved = true;
                }
                if (!resolved)
                {
                    if (resolveReason == "body-missing")
                        summary.MissingBody++;
                    else
                        summary.BracketMiss++;
                    EmitMarkerDecision(MapRenderTrace.MarkerOutcome.SkippedPositionFail);
                    continue;
                }

                VesselType vtype = ResolveVesselTypeWithFallback(committed, rec);
                Color markerColor = MapMarkerRenderer.GetColorForType(vtype);
                int recordingIndex = i;
                Recording markerRecording = rec;
                MapMarkerRenderer.DrawMarker(
                    worldPos,
                    rec.RecordingId,
                    rec.VesselName ?? "(unknown)",
                    markerColor,
                    vtype,
                    context => OnAtmosphericMarkerClicked(recordingIndex, markerRecording, context));
                summary.Drawn++;

                // Tracer: the atmospheric (non-proto) marker drew.
                EmitMarkerDecision(MapRenderTrace.MarkerOutcome.DrawnNonProto);

                ParsekLog.VerboseRateLimited(Tag, $"atmosMarker-{i}",
                    $"Drawing atmospheric marker #{i} \"{rec.VesselName}\" " +
                    $"terminal={rec.TerminalStateValue?.ToString() ?? "null"} " +
                    $"lat={sampledPoint.latitude:F2} lon={sampledPoint.longitude:F2} alt={sampledPoint.altitude:F0} " +
                    $"rodePolyline={rodePolyline}");

                // MapRenderTrace IMGUI surface coverage (AtmosphericMarker). Decision-only: this
                // marker draws here in OnGUI, so the position IS the truth (no end-of-frame
                // reconciliation). Gated + rate-limited inside EmitMarker.
                if (MapRenderTrace.IsEnabled)
                    MapRenderTrace.EmitMarker(
                        MapRenderTrace.RenderSurface.AtmosphericMarker, rec.RecordingId,
                        Planetarium.GetUniversalTime(),
                        string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "vessel={0} worldPos={1} lat={2:F2} lon={3:F2} alt={4:F0} terminal={5}",
                            rec.VesselName, worldPos, sampledPoint.latitude, sampledPoint.longitude,
                            sampledPoint.altitude, rec.TerminalStateValue?.ToString() ?? "null"));
            }

            LogAtmosphericMarkerSummary(summary);
        }

        internal static bool ShouldProcessAtmosphericMarkerEvent(
            EventType eventType,
            bool pointerOverParsekWindow)
        {
            if (eventType != EventType.Repaint
                && eventType != EventType.MouseDown
                && eventType != EventType.MouseUp)
                return false;

            if (pointerOverParsekWindow
                && (eventType == EventType.MouseDown || eventType == EventType.MouseUp))
                return false;

            return true;
        }

        internal static bool IsAtmosphericMarkerClickEvent(EventType eventType)
        {
            return eventType == EventType.MouseDown
                || eventType == EventType.MouseUp;
        }

        internal static bool ShouldBlockAtmosphericMarkerClickForGhostPopup(
            EventType eventType,
            bool pointerOverGhostPopup)
        {
            return pointerOverGhostPopup
                && IsAtmosphericMarkerClickEvent(eventType);
        }

        /// <summary>
        /// Resolve VesselType for a recording. If the recording has no VesselSnapshot,
        /// searches other recordings of the same vessel (by VesselPersistentId) for a snapshot.
        /// Ensures consistent icon type across chain recordings of the same vessel.
        /// O(n) scan per call — acceptable for small committed recording counts (typically under 30).
        /// </summary>
        private static VesselType ResolveVesselTypeWithFallback(IReadOnlyList<Recording> committed, Recording rec)
        {
            if (rec.VesselSnapshot != null)
                return GhostMapPresence.ResolveVesselType(rec.VesselSnapshot);

            // No snapshot — search for a sibling recording of the same vessel
            uint vpid = rec.VesselPersistentId;
            if (vpid != 0)
            {
                for (int j = 0; j < committed.Count; j++)
                {
                    if (committed[j].VesselPersistentId == vpid && committed[j].VesselSnapshot != null)
                        return GhostMapPresence.ResolveVesselType(committed[j].VesselSnapshot);
                }
            }

            return VesselType.Ship;
        }

        /// <summary>
        /// Pure: should an atmospheric trajectory marker be drawn for this recording?
        /// Returns true if the recording is eligible for trajectory-interpolated icon rendering
        /// (no ProtoVessel ghost, has trajectory data at currentUT, not suppressed by the
        /// current tracking-station chain filter, not in orbit segment).
        /// Deliberately does NOT filter by terminal state — atmospheric markers show the ghost's
        /// flight path during its time window regardless of how the recording ended.
        /// </summary>
        internal static bool ShouldDrawAtmosphericMarker(
            Recording rec, int recordingIndex, double currentUT,
            HashSet<string> suppressedIds)
        {
            return ClassifyAtmosphericMarkerSkip(rec, recordingIndex, currentUT, suppressedIds)
                == AtmosphericMarkerSkipReason.None;
        }

        /// <summary>
        /// Pure: map an <see cref="AtmosphericMarkerSkipReason"/> to the shared
        /// <see cref="MapRenderTrace.MarkerOutcome"/> the marker tracer logs, so the TS marker path
        /// and the flight-map path emit one common outcome vocabulary. <c>None</c> means the marker
        /// will draw (the caller upgrades it to <see cref="MapRenderTrace.MarkerOutcome.DrawnNonProto"/>
        /// after a successful position resolve, or to
        /// <see cref="MapRenderTrace.MarkerOutcome.SkippedPositionFail"/> if the resolve fails).
        /// </summary>
        internal static MapRenderTrace.MarkerOutcome MapSkipReasonToMarkerOutcome(
            AtmosphericMarkerSkipReason skipReason)
        {
            switch (skipReason)
            {
                case AtmosphericMarkerSkipReason.None:
                    // Eligible to draw; the caller finalizes after the position resolve.
                    return MapRenderTrace.MarkerOutcome.DrawnNonProto;
                case AtmosphericMarkerSkipReason.NativeIconActive:
                    return MapRenderTrace.MarkerOutcome.DrawnProtoIcon;
                case AtmosphericMarkerSkipReason.Debris:
                    return MapRenderTrace.MarkerOutcome.SkippedDebris;
                // NullRecording / NoTrajectoryPoints / OutsideTimeRange / SuppressedByChainFilter /
                // OrbitSegmentActive all collapse to "the decision said do not draw this marker".
                default:
                    return MapRenderTrace.MarkerOutcome.SkippedDecisionFalse;
            }
        }

        /// <summary>
        /// C-1: stable lowercase-token name for the FINER <see cref="AtmosphericMarkerSkipReason"/>,
        /// appended verbatim as the optional <c>tsSkip=</c> field on the TS marker-decision line so the
        /// distinct reasons that <see cref="MapSkipReasonToMarkerOutcome"/> folds into a single shared
        /// <see cref="MapRenderTrace.MarkerOutcome"/> survive per-ghost (not just in the aggregate
        /// summary). Pure; unit-testable. <c>None</c> maps to <c>none</c> but the call site omits the
        /// field entirely for <c>None</c>, so this token is only emitted for an actual skip reason.
        /// </summary>
        internal static string AtmosphericMarkerSkipReasonToken(AtmosphericMarkerSkipReason reason)
        {
            switch (reason)
            {
                case AtmosphericMarkerSkipReason.None: return "none";
                case AtmosphericMarkerSkipReason.NativeIconActive: return "native-icon-active";
                case AtmosphericMarkerSkipReason.NullRecording: return "null-recording";
                case AtmosphericMarkerSkipReason.Debris: return "debris";
                case AtmosphericMarkerSkipReason.NoTrajectoryPoints: return "no-trajectory-points";
                case AtmosphericMarkerSkipReason.OutsideTimeRange: return "outside-time-range";
                case AtmosphericMarkerSkipReason.SuppressedByChainFilter: return "suppressed-by-chain-filter";
                case AtmosphericMarkerSkipReason.OrbitSegmentActive: return "orbit-segment-active";
                default: return "unknown";
            }
        }

        internal static AtmosphericMarkerSkipReason ClassifyAtmosphericMarkerSkip(
            Recording rec, int recordingIndex, double currentUT,
            HashSet<string> suppressedIds)
        {
            // A ProtoVessel exists but its icon may be suppressed (below atmosphere).
            // When suppressed, the atmospheric marker should still draw.
            if (GhostMapPresence.HasGhostVesselForRecording(recordingIndex))
            {
                uint ghostPid = GhostMapPresence.GetGhostVesselPidForRecording(recordingIndex);
                // The native proto icon is NOT visible when the marker-draw decision
                // (GhostMapPresence.ShouldDrawNonProtoMarkerForGhost, Phase 8c - the SAME
                // source the flight-map DrawMapMarkers uses) says draw our marker: gate ON
                // the Director's TracedPath decision + polyline-owns (8b.2) are authoritative
                // with the legacy icon-suppressed flag kept as the fallback, gate OFF the
                // legacy IsIconSuppressed || IsPolylineOwningGhostPhase predicate (byte-identical).
                // Otherwise the atmospheric marker must still draw as the sole position indicator
                // (an airless descent would show the polyline with no ghost icon).
                if (ghostPid == 0
                    || !GhostMapPresence.ShouldDrawNonProtoMarkerForGhost(ghostPid))
                    return AtmosphericMarkerSkipReason.NativeIconActive;
            }
            if (rec == null) return AtmosphericMarkerSkipReason.NullRecording;
            if (rec.IsDebris) return AtmosphericMarkerSkipReason.Debris;
            if (rec.Points == null || rec.Points.Count == 0)
                return AtmosphericMarkerSkipReason.NoTrajectoryPoints;
            if (currentUT < rec.Points[0].ut || currentUT > rec.Points[rec.Points.Count - 1].ut)
                return AtmosphericMarkerSkipReason.OutsideTimeRange;
            if (suppressedIds != null && suppressedIds.Contains(rec.RecordingId))
                return AtmosphericMarkerSkipReason.SuppressedByChainFilter;
            // OrbitSegmentActive veto - BYPASSED when the polyline OWNS this recording's current
            // phase (playtest-12 icon fix): ownership means the proto orbit line/icon is hidden (or
            // the proto is destroyed entirely, e.g. the below-surface Duna descent), so the marker is
            // the SOLE position indicator. Without the bypass, a recorded conic covering the current
            // UT - including a BELOW-SURFACE one that draws no arc, or short mid-burn fragments under
            // an owned leg - silently vetoed the marker and the icon vanished on the landing chord
            // and on the escape-burn leg. When the polyline does NOT own the phase the veto keeps its
            // original job: no duplicate marker next to a live proto orbit icon.
            if (rec.HasOrbitSegments
                && !Parsek.Display.GhostTrajectoryPolylineRenderer.IsRenderingNonOrbitalLeg(rec.RecordingId)
                && TrajectoryMath.FindOrbitSegment(rec.OrbitSegments, currentUT).HasValue)
                return AtmosphericMarkerSkipReason.OrbitSegmentActive;
            return AtmosphericMarkerSkipReason.None;
        }

        void OnDestroy()
        {
            DismissCurrentGhostPopup("tracking-station-cleanup");
            DestroyAtmosphericFocusTarget("tracking-station-cleanup");
            GhostTrackingStationSelection.ClearSelectedGhost("tracking-station-cleanup");
            GhostMapPresence.RemoveAllGhostVessels("tracking-station-cleanup");
            // Clear ghost-icon sticky state and force atlas re-init when leaving TS.
            // Flight scene's own OnSceneChangeRequested hook runs too, but this
            // addon is the only always-present TS listener — keep it defensive.
            MapMarkerRenderer.ResetForSceneChange();
            ParsekLog.Info(Tag, "ParsekTrackingStation destroyed");
        }

        private bool OnAtmosphericMarkerClicked(
            int recordingIndex,
            Recording recording,
            MapMarkerRenderer.MarkerClickContext context)
        {
            if (recording == null)
                return false;

            GhostTrackingStationSelection.SelectRecordingMarker(
                recordingIndex,
                recording,
                "atmospheric marker");
            LockStockActionsForAtmosphericGhostSelection("atmospheric marker");
            if (GhostTrackingStationSelection.HasSelectedGhost)
                FocusSelectedGhost(GhostTrackingStationSelection.SelectedGhost);
            ParsekLog.Info(Tag,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Atmospheric marker selected: recIndex={0} recId={1} label={2} button={3} screen=({4:F0},{5:F0})",
                    recordingIndex,
                    recording.RecordingId ?? "(none)",
                    recording.VesselName ?? "(unknown)",
                    context.Button,
                    context.ScreenPosition.x,
                    context.ScreenPosition.y));
            return true;
        }

        /// <summary>
        /// Slice (iii) TS port: draw ONE per-instance overlap marker for a single live overlap cycle at its
        /// own playback head UT (<paramref name="headUT"/> = <c>ComputeOverlapCyclePlaybackUT(cycle)</c>),
        /// riding the SINGLE shared polyline keyed by RecordingId in the tracking station. Returns true when
        /// a marker drew, false when the instance was skipped (head out-of-window / between legs / off-line,
        /// or the cycle is currently represented by a live non-suppressed proto icon). TS-local analogue of
        /// the flight <c>ParsekUI.DrawOneOverlapInstanceMarker</c> using TS primitives (the TS resolver +
        /// the world-pos <see cref="MapMarkerRenderer.DrawMarker"/> overload + the TS click handler).
        ///
        /// Orbital no-double-marker rule: for an ORBITAL overlap recording slice (i) creates N ProtoVessels
        /// with orbit icons. If this cycle has a live instance proto AND its icon is NOT suppressed, the
        /// proto icon already draws the cycle - skip the polyline marker. Otherwise (pid 0: pure-suborbital
        /// or pre-materialize, OR the icon is suppressed for a non-orbital phase) the polyline marker is the
        /// sole indicator - draw it.
        ///
        /// Position contract mirrors the TS single-marker tail: resolve the body-fixed head via
        /// <see cref="TryResolveRecordingWorldPosition"/> at the per-cycle headUT (NOT the span-clock
        /// effUT), skip on failure, then RIDE the shared polyline via
        /// <see cref="Parsek.Display.GhostTrajectoryPolylineRenderer.TryAnchorMarkerToPolyline(string,double,out Vector3)"/>
        /// - use the on-line position when it rides, else skip this instance (never draw off-line). The
        /// per-instance marker key is <c>recId + "#" + cycle</c> so hover/sticky state is independent across
        /// instances; the visible label is the shared mission name for all N (they ARE the same mission).
        /// The click handler is the SAME <see cref="OnAtmosphericMarkerClicked"/> the single TS marker wires,
        /// so per-instance markers stay clickable (a TS nuance: flight markers are not clickable).
        /// </summary>
        private bool DrawOneTsOverlapInstanceMarker(
            int recordingIndex, IReadOnlyList<Recording> committed, Recording rec,
            double headUT, long cycle)
        {
            if (rec == null || committed == null
                || recordingIndex < 0 || recordingIndex >= committed.Count)
                return false;

            // Orbital no-double-marker join: if a live, non-suppressed proto icon already draws this cycle,
            // skip the polyline marker. pid 0 means no proto for the cycle (pure-suborbital or not yet
            // materialized) -> draw the polyline marker.
            uint instancePid = GhostMapPresence.TryGetOverlapInstancePidForCycle(recordingIndex, cycle);
            if (instancePid != 0
                && !GhostMapPresence.ShouldDrawNonProtoMarkerForGhost(instancePid))
            {
                // The proto icon owns this cycle this frame — no polyline marker (no double).
                return false;
            }

            // Body-fixed head for the instance at the PER-CYCLE headUT; skip on failure (out-of-window /
            // between legs). A fresh local cache index per call is fine (N <= 20 cycles).
            int cached = -1;
            if (!TryResolveRecordingWorldPosition(
                    rec, headUT, ref cached, out Vector3d worldPos, out _, out _, out _))
                return false;

            // Ride the SINGLE shared polyline at this instance's head; never draw off-line. GAP-1: use
            // the DIAGNOSTIC overload so we capture the REAL ride reason + leg index for THIS instance,
            // then thread them into this instance's marker-decision line below. The ride LOGIC is
            // byte-identical to the 3-arg wrapper (the wrapper just discards the out-params), so this is
            // pure instrumentation - no second anchor call, no control-flow change beyond capturing the
            // out-params the diagnostic overload already computes.
            if (!Parsek.Display.GhostTrajectoryPolylineRenderer.TryAnchorMarkerToPolyline(
                    rec.RecordingId, headUT, out Vector3 onLinePos,
                    out MapRenderTrace.MarkerRideReason rideReason, out int rideLegIndex))
                return false;

            // Per-instance key (hover-collision fix): distinct keys, identical labels are fine
            // (MapMarkerRenderer keys hover/sticky on markerKey, not the label).
            string markerKey = string.IsNullOrEmpty(rec.RecordingId)
                ? null
                : rec.RecordingId + "#" + cycle.ToString(CultureInfo.InvariantCulture);
            string label = rec.VesselName ?? "(unknown)";
            VesselType vtype = ResolveVesselTypeWithFallback(committed, rec);
            Color markerColor = MapMarkerRenderer.GetColorForType(vtype);
            int recordingIndexForClick = recordingIndex;
            Recording markerRecording = rec;
            MapMarkerRenderer.DrawMarker(
                onLinePos,
                markerKey,
                label,
                markerColor,
                vtype,
                context => OnAtmosphericMarkerClicked(recordingIndexForClick, markerRecording, context));

            // GAP-1: per-INSTANCE marker-decision line, keyed by the SAME per-instance markerKey
            // (recId#cycle) the EmitMarker call below uses - NOT the bare recordingId. The overlap path
            // runs N instances under ONE recordingId; keying the change-detection signature on the bare
            // recordingId would let the N instances thrash a single signature (false "changed" churn /
            // masking). This is the TS analogue the flight overlap path lacks: the flight branch emits
            // one per-RECORDING decision line with ride=not-attempted, so its ride field cannot
            // represent a specific instance. Here each instance's line carries ITS OWN real ride reason
            // + posSource=polyline (it just rode a leg above), finally answering the canonical TS debug
            // question "did this icon ride its line or fall off?" per instance.
            if (MapRenderTrace.IsEnabled && !string.IsNullOrEmpty(markerKey))
                MapRenderTrace.EmitMarkerDecisionOnChange(
                    MapRenderTrace.RenderSurface.AtmosphericMarker, markerKey, headUT,
                    MapRenderTrace.BuildMarkerDecisionSignature(
                        recordingIndex, label,
                        // polylineOwning + shouldDraw are true because this instance just rode the shared
                        // polyline above (8e S4 dropped the director-drive gate; the per-instance overlap
                        // path is now unconditional).
                        directorTracedPathActive: false,
                        polylineOwning: true,
                        iconSuppressed: false,
                        shouldDrawNonProto: true,
                        outcome: MapRenderTrace.MarkerOutcome.DrawnNonProto,
                        rideReason: rideReason,
                        legIndex: rideLegIndex,
                        posSource: "polyline"));

            // Throttle key is per-RECORDING (rec.RecordingId), NOT the per-cycle markerKey: at high
            // time warp the overlap cycle index advances every frame, so a per-cycle key yields a fresh
            // key each frame and defeats VerboseRateLimited (a per-marker flood). The cycle stays in the
            // detail; the per-recording `ts-overlap-instance-markers` summary carries the drawn count.
            if (MapRenderTrace.IsEnabled)
                MapRenderTrace.EmitMarker(
                    MapRenderTrace.RenderSurface.AtmosphericMarker, rec.RecordingId, headUT,
                    string.Format(CultureInfo.InvariantCulture,
                        "vessel={0} cycle={1} markerPos={2} overlapInstance=True",
                        label, cycle, MapRenderTrace.FormatVector3(onLinePos)));

            return true;
        }

        private static void LockStockActionsForAtmosphericGhostSelection(string source)
        {
            SpaceTracking tracking = FindObjectOfType<SpaceTracking>();
            if (tracking == null)
            {
                ParsekLog.Warn(Tag,
                    "Atmospheric marker ghost selection could not lock stock actions: SpaceTracking instance not found");
                return;
            }

            bool cleared = GhostTrackingStationSelection.TryClearSelectedVessel(
                tracking,
                out object previousSelection,
                out string clearError);
            if (!string.IsNullOrEmpty(clearError))
                ParsekLog.Warn(Tag,
                    "Atmospheric marker ghost selection failed to clear stock selection: " + clearError);

            GhostTrackingStationSelection.DisableStockActionButtons(
                tracking,
                out bool flyDisabled,
                out bool deleteDisabled,
                out bool recoverDisabled);
            ParsekLog.Info(Tag,
                FormatAtmosphericMarkerStockActionLockLine(
                    cleared,
                    previousSelection != null,
                    flyDisabled,
                    deleteDisabled,
                    recoverDisabled,
                    clearError,
                    source));
        }

        internal static string FormatAtmosphericMarkerStockActionLockLine(
            bool clearedSelection,
            bool hadPreviousSelection,
            bool flyDisabled,
            bool deleteDisabled,
            bool recoverDisabled,
            string clearError,
            string source)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Atmospheric marker ghost selection locked stock actions: clearedSelection={0} hadPreviousSelection={1} flyDisabled={2} deleteDisabled={3} recoverDisabled={4} clearError={5} source={6}",
                clearedSelection,
                hadPreviousSelection,
                flyDisabled,
                deleteDisabled,
                recoverDisabled,
                string.IsNullOrEmpty(clearError) ? "(none)" : clearError,
                source ?? "(unknown)");
        }

        private void UpdateSelectedGhostPopup()
        {
            if (PauseMenuGate.IsPauseMenuOpen())
            {
                DismissCurrentGhostPopup("pause-menu-open");
                return;
            }

            if (!GhostTrackingStationSelection.HasSelectedGhost)
            {
                DismissCurrentGhostPopup("ghost-selection-cleared");
                return;
            }

            TrackingStationGhostSelectionInfo selection = GhostTrackingStationSelection.SelectedGhost;
            if (GhostTrackingStationSelection.TryResolveRecording(
                    selection,
                    out Recording selectedRecording,
                    out _)
                && GhostMapPresence.IsTrackingStationRecordingAlreadyMaterialized(selectedRecording))
            {
                DismissCurrentGhostPopup("selected-recording-spawned");
                GhostTrackingStationSelection.ClearSelectedGhost("selected-recording-spawned");
                return;
            }

            double currentUT = ghostActionCurrentUT;
            if (!ShouldOpenSelectedGhostPopup(selection))
            {
                DismissCurrentGhostPopup("ghost-selection-focus-only");
                return;
            }

            string key = BuildGhostPopupKey(selection, currentUT);
            if (currentGhostPopup == null || currentGhostPopupKey != key)
            {
                DismissCurrentGhostPopup("opening-new-ghost-popup");
                OpenSelectedGhostPopup(selection, key);
            }

            CheckGhostPopupOutsideClick();
        }

        internal static bool ShouldOpenSelectedGhostPopup(TrackingStationGhostSelectionInfo selection)
        {
            return selection.ShowPopup;
        }

        private static string BuildGhostPopupKey(
            TrackingStationGhostSelectionInfo selection,
            double currentUT)
        {
            string phase = BuildGhostPopupStatusPhase(selection, currentUT);
            if (selection.GhostPid != 0)
            {
                return "ghost:"
                    + selection.GhostPid.ToString(CultureInfo.InvariantCulture)
                    + ":"
                    + phase;
            }

            return "recording:" + (selection.RecordingId ?? "(none)") + ":" + phase;
        }

        internal static string BuildGhostPopupStatusPhase(
            TrackingStationGhostSelectionInfo selection,
            double currentUT)
        {
            if (!selection.HasRecording)
                return "no-recording";
            if (selection.VesselSpawned || selection.SpawnedVesselPersistentId != 0)
                return "spawned";
            if (double.IsNaN(selection.EndUT) || double.IsInfinity(selection.EndUT))
                return "unknown-end";

            return currentUT < selection.EndUT
                ? "before-end"
                : "endpoint";
        }

        private void OpenSelectedGhostPopup(
            TrackingStationGhostSelectionInfo selection,
            string key)
        {
            double currentUT = ghostActionCurrentUT;
            Vessel vessel = FindGhostVessel(selection.GhostPid);
            TrackingStationGhostActionContext context =
                GhostTrackingStationSelection.BuildActionContext(
                    selection,
                    hasGhostVessel: vessel != null,
                    canFocus: false,
                    canSetTarget: false,
                    currentUT: currentUT,
                    chains: ghostActionChains);
            TrackingStationGhostActionState[] actions =
                TrackingStationGhostActionPresentation.BuildActionStates(context);
            TrackingStationGhostActionState materialize =
                FindAction(actions, TrackingStationGhostActionKind.Materialize);

            // KSP dismisses DialogGUIButton after the handler returns. Clear our
            // popup reference first so lifecycle code does not touch a stale dialog.
            var options = new DialogGUIBase[]
            {
                new DialogGUIButton(
                    () => BuildMaterializeButtonLabel(
                        materialize.Label,
                        selection,
                        context,
                        Planetarium.GetUniversalTime()),
                    () =>
                    {
                        currentGhostPopup = null;
                        currentGhostPopupKey = null;
                        MaterializeSelectedGhost(selection, Planetarium.GetUniversalTime());
                    },
                    () => materialize.Enabled,
                    160f,
                    30f,
                    true,
                    (DialogGUIBase[])null)
            };

            currentGhostPopup = PopupDialog.SpawnPopupDialog(
                Vector2.zero,
                Vector2.zero,
                new MultiOptionDialog(
                    "ParsekTrackingStationGhostMenu",
                    BuildGhostPopupText(selection, currentUT),
                    "Ghost",
                    HighLogic.UISkin,
                    GhostPopupWidth,
                    options),
                persistAcrossScenes: false,
                skin: HighLogic.UISkin);
            currentGhostPopupKey = key;
            ghostPopupOpenFrame = Time.frameCount;
            PositionPopupAtCursor(currentGhostPopup);
            ParsekLog.Verbose(Tag,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Tracking Station ghost popup opened: key={0} ghostPid={1} recId={2} warpToSpawn={3}",
                    key ?? "(none)",
                    selection.GhostPid,
                    selection.RecordingId ?? "(none)",
                    materialize.Enabled));
        }

        private void DismissCurrentGhostPopup(string reason, bool clearSelection = false)
        {
            if (currentGhostPopup != null)
            {
                currentGhostPopup.Dismiss();
                currentGhostPopup = null;
                ParsekLog.Verbose(Tag,
                    "Tracking Station ghost popup dismissed: reason=" + (reason ?? "(none)"));
            }
            currentGhostPopupKey = null;

            if (clearSelection)
                GhostTrackingStationSelection.ClearSelectedGhost(reason);
        }

        // Frames to ignore after the popup opens before an outside press can
        // dismiss it. The opening click's press fires the frame before the
        // popup exists, so this is belt-and-suspenders against event-order
        // edge cases.
        private const int GhostPopupOutsideClickArmFrames = 5;

        private void CheckGhostPopupOutsideClick()
        {
            if (currentGhostPopup == null)
                return;

            // Dismiss on a fresh outside PRESS, not a release. Releasing the
            // very click that opened the popup used to close it: the popup
            // anchors just below the cursor, so the cursor sits at/above its
            // top edge (outside the rect), and the opening click's release was
            // treated as an outside click. That is why the menu only stayed
            // visible while the button was held down. A press starts a new
            // interaction, so the opening click no longer closes the menu it
            // just opened.
            int framesSinceOpen = Time.frameCount - ghostPopupOpenFrame;
            bool freshClick = Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1);
            bool mouseOverPopup = IsMouseOverCurrentGhostPopup();

            if (framesSinceOpen >= GhostPopupOutsideClickArmFrames
                && freshClick
                && mouseOverPopup)
            {
                ParsekLog.Verbose(Tag,
                    "Tracking Station ghost popup click ignored: inside-popup");
                return;
            }

            if (!ShouldDismissGhostPopupOnOutsideClick(
                    framesSinceOpen, freshClick, mouseOverPopup))
                return;

            DismissCurrentGhostPopup("outside-click", clearSelection: true);
        }

        /// <summary>
        /// Pure: should an open ghost popup be dismissed this frame? Dismiss
        /// only on a fresh outside press once the arm window has elapsed, so
        /// neither the press nor the release of the click that opened the popup
        /// can close it. Extracted so the decision is unit-testable without a
        /// Unity Input context — callers pass the frame delta, whether a mouse
        /// button went down this frame, and whether the cursor is over the popup.
        /// </summary>
        internal static bool ShouldDismissGhostPopupOnOutsideClick(
            int framesSinceOpen, bool freshClickThisFrame, bool mouseOverPopup)
        {
            if (framesSinceOpen < GhostPopupOutsideClickArmFrames)
                return false;
            if (!freshClickThisFrame)
                return false;
            if (mouseOverPopup)
                return false;
            return true;
        }

        private bool IsMouseOverCurrentGhostPopup()
        {
            if (currentGhostPopup == null)
                return false;

            RectTransform rt = currentGhostPopup.GetComponent<RectTransform>();
            Vector3 mouse = Input.mousePosition;
            return IsScreenPointInsideRect(rt, new Vector2(mouse.x, mouse.y));
        }

        private static bool IsScreenPointInsideRect(
            RectTransform rectTransform,
            Vector2 screenPoint)
        {
            if ((object)rectTransform == null)
                return false;

            Canvas canvas = rectTransform.GetComponentInParent<Canvas>();
            Camera camera = null;
            if ((object)canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                camera = canvas.worldCamera;

            return RectTransformUtility.RectangleContainsScreenPoint(
                rectTransform,
                screenPoint,
                camera);
        }

        private static void PositionPopupAtCursor(PopupDialog popup)
        {
            if (popup == null)
                return;

            popup.SetDraggable(false);
            RectTransform rt = popup.GetComponent<RectTransform>();
            if (rt == null)
                return;

            LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
            RectTransform canvasRect = MapViewCanvasUtil.MapViewCanvasRect;
            if (canvasRect == null)
                return;

            Vector3 uiPos = CanvasUtil.ScreenToUISpacePos(
                Input.mousePosition,
                canvasRect,
                out bool _);
            uiPos = CanvasUtil.AnchorOffset(uiPos, rt, Vector2.down);
            rt.localPosition = uiPos;
            ParsekLog.Verbose(Tag,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Tracking Station ghost popup positioned: screen=({0:F0},{1:F0}) canvas=({2:F0},{3:F0})",
                    Input.mousePosition.x,
                    Input.mousePosition.y,
                    uiPos.x,
                    uiPos.y));
        }

        private static TrackingStationGhostActionState FindAction(
            TrackingStationGhostActionState[] actions,
            TrackingStationGhostActionKind kind)
        {
            for (int i = 0; i < actions.Length; i++)
                if (actions[i].Kind == kind)
                    return actions[i];

            return new TrackingStationGhostActionState(
                kind,
                kind.ToString(),
                TrackingStationGhostActionSafety.BlockedOnGhost,
                false,
                "Action unavailable.");
        }

        internal static string BuildGhostPopupText(
            TrackingStationGhostSelectionInfo selection,
            double currentUT)
        {
            string vesselName = FormatGhostPopupVesselName(selection.VesselName);
            string recordingStatus = BuildGhostPopupRecordingStatus(selection, currentUT);
            string endState = selection.TerminalState.HasValue
                ? selection.TerminalState.Value.ToString()
                : "(unknown)";

            return string.Format(
                CultureInfo.InvariantCulture,
                "Name: {0}\nRecording: {1}\nEnd state: {2}",
                vesselName,
                recordingStatus,
                endState);
        }

        internal static string FormatGhostPopupVesselName(string vesselName)
        {
            string name = string.IsNullOrEmpty(vesselName)
                ? "(ghost)"
                : vesselName.Trim();
            const string stockGhostPrefix = "Ghost:";

            while (name.StartsWith(stockGhostPrefix, System.StringComparison.OrdinalIgnoreCase))
                name = name.Substring(stockGhostPrefix.Length).TrimStart();

            return string.IsNullOrEmpty(name) ? "(ghost)" : name;
        }

        internal static string BuildMaterializeButtonLabel(
            string baseLabel,
            TrackingStationGhostSelectionInfo selection,
            TrackingStationGhostActionContext actionContext,
            double currentUT)
        {
            if (!actionContext.MaterializeFastForwardEligible)
                return string.IsNullOrEmpty(baseLabel) ? "Warp to Spawn" : baseLabel;

            double remaining = selection.EndUT - currentUT;
            if (!(remaining > 0.0))
                return string.IsNullOrEmpty(baseLabel) ? "Warp to Spawn" : baseLabel;

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} ({1})",
                string.IsNullOrEmpty(baseLabel) ? "Warp to Spawn" : baseLabel,
                ParsekTimeFormat.FormatDuration(remaining));
        }

        private static string BuildGhostPopupRecordingStatus(
            TrackingStationGhostSelectionInfo selection,
            double currentUT)
        {
            if (!selection.HasRecording)
                return "unavailable";
            if (selection.VesselSpawned || selection.SpawnedVesselPersistentId != 0)
                return "spawned";
            if (double.IsNaN(selection.EndUT))
                return "unknown";

            double remaining = selection.EndUT - currentUT;
            return remaining > 0.0
                ? "before endpoint"
                : "endpoint reached";
        }

        private void FocusSelectedGhost(TrackingStationGhostSelectionInfo selection)
        {
            Vessel vessel = FindGhostVessel(selection.GhostPid);
            if (vessel != null && PlanetariumCamera.fetch != null && vessel.mapObject != null)
            {
                DestroyAtmosphericFocusTarget("proto-ghost-focused");
                PlanetariumCamera.fetch.SetTarget(vessel.mapObject);
                ParsekLog.Info(Tag,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Focused Tracking Station ghost '{0}' pid={1}",
                        vessel.vesselName,
                        vessel.persistentId));
                return;
            }

            if (TryFocusAtmosphericGhost(selection, Planetarium.GetUniversalTime()))
                return;

            ParsekLog.Warn(Tag,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Focus selected ghost failed: ghostPid={0} recId={1} vessel={2} camera={3} mapObj={4}",
                    selection.GhostPid,
                    selection.RecordingId ?? "(none)",
                    vessel != null,
                    PlanetariumCamera.fetch != null,
                    vessel != null && vessel.mapObject != null));
        }

        private bool TryFocusAtmosphericGhost(
            TrackingStationGhostSelectionInfo selection,
            double currentUT)
        {
            if (PlanetariumCamera.fetch == null || !selection.HasRecording)
                return false;

            int cached = atmosphericFocusRecordingId == selection.RecordingId
                ? atmosphericFocusCachedIndex
                : -1;
            if (!TryResolveSelectionWorldPosition(
                    selection,
                    currentUT,
                    ref cached,
                    out Vector3d worldPos,
                    out CelestialBody body,
                    out _,
                    out string reason))
            {
                ParsekLog.Warn(Tag,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Focus atmospheric Tracking Station ghost failed: recId={0} reason={1}",
                        selection.RecordingId ?? "(none)",
                        reason ?? "(none)"));
                return false;
            }

            atmosphericFocusCachedIndex = cached;
            if (atmosphericFocusTarget == null
                || atmosphericFocusRecordingId != selection.RecordingId)
            {
                DestroyAtmosphericFocusTarget("new-atmospheric-focus");
                string targetName = "Ghost: " + (selection.VesselName ?? "Ghost");
                atmosphericFocusTarget = MapObject.Create(
                    targetName,
                    targetName,
                    body != null ? body.orbit : null,
                    MapObject.ObjectType.Generic);
                atmosphericFocusRecordingId = selection.RecordingId;
            }

            UpdateAtmosphericFocusTransform(atmosphericFocusTarget, worldPos);
            PlanetariumCamera.fetch.SetTarget(atmosphericFocusTarget);
            ParsekLog.Info(Tag,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Focused atmospheric Tracking Station ghost '{0}' recId={1} ut={2:F1}",
                    selection.VesselName ?? "(ghost)",
                    selection.RecordingId ?? "(none)",
                    currentUT));
            return true;
        }

        private void UpdateAtmosphericFocusTarget()
        {
            if (atmosphericFocusTarget == null
                || string.IsNullOrEmpty(atmosphericFocusRecordingId))
                return;

            if (!GhostMapPresence.TryGetCommittedRecordingById(
                    atmosphericFocusRecordingId,
                    out _,
                    out Recording recording))
            {
                DestroyAtmosphericFocusTarget("focused-recording-missing");
                return;
            }

            if (recording.SpawnedVesselPersistentId != 0)
            {
                if (!FocusSpawnedTrackingStationVessel(
                        recording.SpawnedVesselPersistentId,
                        "atmospheric-focus-spawned",
                        restoreStockSelection: true,
                        warnOnFailure: true,
                        out _))
                {
                    ScheduleMaterializedFocusRetry(
                        recording.SpawnedVesselPersistentId,
                        "atmospheric-focus-spawned");
                }
                DestroyAtmosphericFocusTarget("atmospheric-focus-spawned");
                return;
            }

            int cached = atmosphericFocusCachedIndex;
            if (TryResolveRecordingWorldPosition(
                    recording,
                    Planetarium.GetUniversalTime(),
                    ref cached,
                    out Vector3d worldPos,
                    out _,
                    out _,
                    out string reason))
            {
                atmosphericFocusCachedIndex = cached;
                UpdateAtmosphericFocusTransform(atmosphericFocusTarget, worldPos);
                return;
            }

            ParsekLog.VerboseRateLimited(Tag,
                "atmospheric-focus-update-failed|" + atmosphericFocusRecordingId,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Atmospheric focus target update failed: recId={0} reason={1}",
                    atmosphericFocusRecordingId,
                    reason ?? "(none)"),
                5.0);
        }

        private bool TryResolveSelectionWorldPosition(
            TrackingStationGhostSelectionInfo selection,
            double currentUT,
            ref int cachedIndex,
            out Vector3d worldPos,
            out CelestialBody body,
            out TrajectoryPoint sampledPoint,
            out string reason)
        {
            worldPos = default(Vector3d);
            body = null;
            sampledPoint = default(TrajectoryPoint);
            reason = null;

            if (!GhostTrackingStationSelection.TryResolveRecording(
                    selection,
                    out Recording recording,
                    out _))
            {
                reason = "recording-missing";
                return false;
            }

            return TryResolveRecordingWorldPosition(
                recording,
                currentUT,
                ref cachedIndex,
                out worldPos,
                out body,
                out sampledPoint,
                out reason);
        }

        internal static bool TryResolveRecordingWorldPosition(
            Recording recording,
            double currentUT,
            ref int cachedIndex,
            out Vector3d worldPos,
            out CelestialBody body,
            out TrajectoryPoint sampledPoint,
            out string reason)
        {
            worldPos = default(Vector3d);
            body = null;
            sampledPoint = default(TrajectoryPoint);
            reason = null;

            if (recording == null)
            {
                reason = "recording-null";
                return false;
            }
            if (!TrySelectTrackingStationFocusFrames(
                    recording,
                    currentUT,
                    out List<TrajectoryPoint> frames,
                    out reason))
            {
                return false;
            }

            double sampleUT = currentUT;
            double firstUT = frames[0].ut;
            double lastUT = frames[frames.Count - 1].ut;
            if (sampleUT < firstUT)
                sampleUT = firstUT;
            else if (sampleUT > lastUT)
                sampleUT = lastUT;

            TrajectoryPoint? pt = TrajectoryMath.BracketPointAtUT(
                frames,
                sampleUT,
                ref cachedIndex);
            if (!pt.HasValue)
            {
                reason = "bracket-miss";
                return false;
            }

            sampledPoint = pt.Value;
            body = FlightGlobals.Bodies?.Find(b => b.name == pt.Value.bodyName);
            if (body == null)
            {
                reason = "body-missing";
                return false;
            }

            worldPos = body.GetWorldSurfacePosition(
                pt.Value.latitude,
                pt.Value.longitude,
                pt.Value.altitude);
            return true;
        }

        internal static bool TrySelectTrackingStationFocusFrames(
            Recording recording,
            double sampleUT,
            out List<TrajectoryPoint> frames,
            out string reason)
        {
            frames = null;
            reason = null;
            if (recording == null)
            {
                reason = "recording-null";
                return false;
            }

            int sectionIdx = TrajectoryMath.FindTrackSectionForUT(
                recording.TrackSections,
                sampleUT);
            if (sectionIdx >= 0
                && recording.TrackSections != null
                && sectionIdx < recording.TrackSections.Count)
            {
                if (TrySelectTrackingStationFocusFramesFromSection(
                        recording,
                        recording.TrackSections[sectionIdx],
                        sampleUT,
                        out frames,
                        out reason,
                        out bool blockedBySection))
                {
                    return true;
                }

                if (blockedBySection)
                    return false;
            }
            else if (TryFindNearestTrackSectionForUT(
                recording.TrackSections,
                sampleUT,
                out TrackSection nearestSection))
            {
                if (TrySelectTrackingStationFocusFramesFromSection(
                        recording,
                        nearestSection,
                        sampleUT,
                        out frames,
                        out reason,
                        out bool blockedByNearestSection))
                {
                    return true;
                }

                if (blockedByNearestSection)
                    return false;
            }

            if (recording.Points == null || recording.Points.Count == 0)
            {
                reason = "no-points";
                return false;
            }

            frames = recording.Points;
            return true;
        }

        private static bool TrySelectTrackingStationFocusFramesFromSection(
            Recording recording,
            TrackSection section,
            double sampleUT,
            out List<TrajectoryPoint> frames,
            out string reason,
            out bool blocked)
        {
            frames = null;
            reason = null;
            blocked = false;

            if (section.referenceFrame == ReferenceFrame.OrbitalCheckpoint)
            {
                reason = "checkpoint-section";
                blocked = true;
                return false;
            }

            if (section.referenceFrame == ReferenceFrame.Relative)
            {
                // TryResolveRecordingWorldPosition consumes the selected list as
                // body-fixed lat/lon/alt. Relative frames are anchor-local
                // metre offsets, so Tracking Station focus/icon positioning must
                // use the body-fixed primary surface unless this path grows a
                // real relative world-pose resolver.
                if (!ParsekFlight.BodyFixedPrimaryCoversPlaybackUT(
                        section, sampleUT, out _, out _))
                {
                    reason = "relative-body-fixed-primary-out-of-range";
                    blocked = true;
                    return false;
                }

                frames = section.bodyFixedFrames;
                return true;
            }

            if (section.frames != null && section.frames.Count > 0)
            {
                frames = section.frames;
                return true;
            }

            // Empty absolute sections are not a safety block; let callers try
            // the nearest section or the legacy flat Points list.
            return false;
        }

        private static bool TryFindNearestTrackSectionForUT(
            List<TrackSection> sections,
            double sampleUT,
            out TrackSection nearestSection)
        {
            nearestSection = default(TrackSection);
            if (sections == null || sections.Count == 0)
                return false;
            if (double.IsNaN(sampleUT) || double.IsInfinity(sampleUT))
                return false;

            double bestDistance = double.PositiveInfinity;
            bool hasNearest = false;
            for (int i = 0; i < sections.Count; i++)
            {
                TrackSection section = sections[i];

                double distance;
                if (sampleUT < section.startUT)
                    distance = section.startUT - sampleUT;
                else if (sampleUT > section.endUT)
                    distance = sampleUT - section.endUT;
                else
                    distance = 0.0;

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    nearestSection = section;
                    hasNearest = true;
                }
            }

            return hasNearest;
        }

        private static void UpdateAtmosphericFocusTransform(
            MapObject target,
            Vector3d worldPos)
        {
            if (target == null)
                return;

            Vector3 scaledPos = ScaledSpace.LocalToScaledSpace(worldPos);
            target.transform.position = scaledPos;
            if (target.trf != null)
                target.trf.position = scaledPos;
        }

        private void DestroyAtmosphericFocusTarget(string reason)
        {
            if (atmosphericFocusTarget == null)
                return;

            MapObject target = atmosphericFocusTarget;
            atmosphericFocusTarget = null;
            atmosphericFocusRecordingId = null;
            atmosphericFocusCachedIndex = -1;

            try
            {
                PlanetariumCamera camera = PlanetariumCamera.fetch;
                if (camera != null && ReferenceEquals(camera.target, target))
                {
                    MapObject fallback = camera.FindNearestTarget();
                    if (fallback != null && !ReferenceEquals(fallback, target))
                        camera.SetTarget(fallback);
                }
                target.Terminate();
                ParsekLog.Verbose(Tag,
                    "Destroyed atmospheric ghost focus target: reason=" + (reason ?? "(none)"));
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn(Tag,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Destroy atmospheric ghost focus target failed: reason={0} {1}: {2}",
                        reason ?? "(none)",
                        ex.GetType().Name,
                        ex.Message));
            }
        }

        private void MaterializeSelectedGhost(
            TrackingStationGhostSelectionInfo selection,
            double currentUT)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null
                || string.IsNullOrEmpty(selection.RecordingId)
                || !GhostTrackingStationSelection.TryResolveRecording(
                    selection,
                    out Recording recording,
                    out _))
            {
                ParsekLog.Warn(Tag,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Warp to Spawn selected ghost failed: missing recording for ghostPid={0} recId={1}",
                        selection.GhostPid,
                        selection.RecordingId ?? "(none)"));
                return;
            }

            if (currentUT < recording.EndUT)
            {
                var endpointMaterialize =
                    GhostTrackingStationSelection.EvaluateMaterializeAtEndpoint(recording);
                if (!endpointMaterialize.needsSpawn)
                {
                    ParsekLog.Warn(Tag,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Warp to Spawn fast-forward refused: recId={0} fromUT={1:F1} endpointUT={2:F1} reason={3}",
                            recording.RecordingId ?? "(none)",
                            currentUT,
                            recording.EndUT,
                            endpointMaterialize.reason ?? "(none)"));
                    ParsekLog.ScreenMessage(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Cannot warp to spawn \"{0}\": {1}",
                            recording.VesselName ?? "ghost",
                            endpointMaterialize.reason ?? "endpoint blocked"),
                        4f);
                    GhostTrackingStationSelection.ClearSelectedGhost("spawn endpoint ineligible");
                    return;
                }

                double jumpDelta = recording.EndUT - currentUT;
                ParsekLog.Info(Tag,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Warp to Spawn fast-forward requested: recId={0} fromUT={1:F1} endpointUT={2:F1} delta={3:F1}s",
                        recording.RecordingId ?? "(none)",
                        currentUT,
                        recording.EndUT,
                        jumpDelta));
                TimeJumpManager.ExecuteForwardJump(recording.EndUT);
                ParsekLog.ScreenMessage(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Warped to spawn \"{0}\" ({1:F0}s)",
                        recording.VesselName ?? "ghost",
                        jumpDelta),
                    3f);
                currentUT = Planetarium.GetUniversalTime();
            }

            bool handled = GhostMapPresence.TryRunTrackingStationSpawnHandoffForRecordingId(
                committed,
                selection.RecordingId,
                currentUT,
                reselectSpawnedVessel: true);
            ParsekLog.Info(Tag,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Warp to Spawn requested for Tracking Station ghost pid={0} recId={1} ut={2:F1} handled={3}",
                    selection.GhostPid,
                    selection.RecordingId ?? "(none)",
                    currentUT,
                    handled));

            if (handled)
            {
                ParsekLog.Info(Tag,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Warp to Spawn handoff forcing immediate Tracking Station lifecycle tick: recId={0}",
                        selection.RecordingId ?? "(none)"));
                GhostMapPresence.UpdateTrackingStationGhostLifecycle(
                    cachedLoopUnits, refreshStockList: false);
                nextLifecycleCheckTime = Time.time + LifecycleCheckIntervalSec;
                GhostMapPresence.TryRefreshLiveTrackingStationVesselList("tracking-station-spawn-handoff");

                if (recording.SpawnedVesselPersistentId != 0)
                {
                    if (!FocusSpawnedTrackingStationVessel(
                            recording.SpawnedVesselPersistentId,
                            "tracking-station-spawn",
                            restoreStockSelection: true,
                            warnOnFailure: true,
                            out _))
                    {
                        ScheduleMaterializedFocusRetry(
                            recording.SpawnedVesselPersistentId,
                            "tracking-station-spawn");
                    }
                }
                DestroyAtmosphericFocusTarget("spawn handoff");
            }

            GhostTrackingStationSelection.ClearSelectedGhost("spawn handoff");
        }

        private void ScheduleMaterializedFocusRetry(uint spawnedPid, string reason)
        {
            if (spawnedPid == 0)
                return;

            pendingMaterializedFocusPid = spawnedPid;
            pendingMaterializedFocusReason = string.IsNullOrEmpty(reason)
                ? "tracking-station-spawn"
                : reason;
            pendingMaterializedFocusDeadlineTime =
                Time.time + MaterializedFocusRetryDurationSec;
            nextMaterializedFocusAttemptTime = Time.time;
            pendingMaterializedFocusAttempts = 0;
            CaptureMaterializedFocusSelectionBaseline(spawnedPid);

            ParsekLog.Info(Tag,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Scheduled spawned Tracking Station focus retry: pid={0} reason={1} duration={2:F1}s interval={3:F2}s",
                    spawnedPid,
                    pendingMaterializedFocusReason,
                    MaterializedFocusRetryDurationSec,
                    MaterializedFocusRetryIntervalSec));
        }

        private void CaptureMaterializedFocusSelectionBaseline(uint spawnedPid)
        {
            pendingMaterializedFocusBaselineHasSelectedGhost =
                GhostTrackingStationSelection.HasSelectedGhost;
            pendingMaterializedFocusBaselineGhostPid =
                pendingMaterializedFocusBaselineHasSelectedGhost
                    ? GhostTrackingStationSelection.SelectedGhost.GhostPid
                    : 0u;
            pendingMaterializedFocusBaselineRecordingId =
                pendingMaterializedFocusBaselineHasSelectedGhost
                    ? GhostTrackingStationSelection.SelectedGhost.RecordingId
                    : null;
            pendingMaterializedFocusBaselineSelectedPidAvailable = false;
            pendingMaterializedFocusBaselineSelectedPid = 0;

            SpaceTracking tracking = UnityEngine.Object.FindObjectOfType<SpaceTracking>();
            string selectedError = null;
            if (tracking != null
                && GhostTrackingStationSelection.TryGetSelectedVesselPid(
                    tracking,
                    out uint selectedPid,
                    out selectedError))
            {
                pendingMaterializedFocusBaselineSelectedPidAvailable = true;
                pendingMaterializedFocusBaselineSelectedPid = selectedPid;
            }
            else if (tracking != null)
            {
                ParsekLog.Verbose(Tag,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Spawned Tracking Station focus retry baseline stock-selection probe failed: pid={0} error={1}",
                        spawnedPid,
                        selectedError ?? "(none)"));
            }

            ParsekLog.Verbose(Tag,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Captured spawned Tracking Station focus retry selection baseline: pid={0} ghostSelected={1} ghostPid={2} recId={3} stockAvailable={4} stockPid={5}",
                    spawnedPid,
                    pendingMaterializedFocusBaselineHasSelectedGhost,
                    pendingMaterializedFocusBaselineGhostPid,
                    pendingMaterializedFocusBaselineRecordingId ?? "(none)",
                    pendingMaterializedFocusBaselineSelectedPidAvailable,
                    pendingMaterializedFocusBaselineSelectedPid));
        }

        private void UpdatePendingMaterializedFocus()
        {
            bool expired;
            if (!ShouldAttemptMaterializedFocusRetry(
                    pendingMaterializedFocusPid,
                    Time.time,
                    nextMaterializedFocusAttemptTime,
                    pendingMaterializedFocusDeadlineTime,
                    out expired))
            {
                if (expired)
                {
                    ParsekLog.Warn(Tag,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Spawned Tracking Station focus retry expired: pid={0} reason={1} attempts={2}",
                            pendingMaterializedFocusPid,
                            pendingMaterializedFocusReason ?? "(none)",
                            pendingMaterializedFocusAttempts));
                    ClearPendingMaterializedFocus();
                }
                return;
            }

            uint spawnedPid = pendingMaterializedFocusPid;
            string reason = pendingMaterializedFocusReason ?? "tracking-station-spawn";
            if (ShouldAbortMaterializedFocusRetryForCurrentSelection(
                    spawnedPid,
                    out string abortReason))
            {
                ParsekLog.Info(Tag,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Spawned Tracking Station focus retry cancelled: pid={0} reason={1} attempts={2} abort={3}",
                        spawnedPid,
                        reason,
                        pendingMaterializedFocusAttempts,
                        abortReason ?? "(none)"));
                ClearPendingMaterializedFocus();
                return;
            }

            pendingMaterializedFocusAttempts++;
            nextMaterializedFocusAttemptTime = Time.time + MaterializedFocusRetryIntervalSec;

            if (FocusSpawnedTrackingStationVessel(
                    spawnedPid,
                    reason + "-retry",
                    restoreStockSelection: true,
                    warnOnFailure: false,
                    out string focusError))
            {
                ParsekLog.Info(Tag,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Spawned Tracking Station focus retry succeeded: pid={0} reason={1} attempts={2}",
                        spawnedPid,
                        reason,
                        pendingMaterializedFocusAttempts));
                ClearPendingMaterializedFocus();
                return;
            }

            ParsekLog.VerboseRateLimited(Tag,
                "spawned-focus-retry-pending|" + spawnedPid,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Spawned Tracking Station focus retry pending: pid={0} reason={1} attempts={2} error={3}",
                    spawnedPid,
                    reason,
                    pendingMaterializedFocusAttempts,
                    focusError ?? "(none)"),
                1.0);
        }

        private bool ShouldAbortMaterializedFocusRetryForCurrentSelection(
            uint spawnedPid,
            out string abortReason)
        {
            uint selectedGhostPid = GhostTrackingStationSelection.HasSelectedGhost
                ? GhostTrackingStationSelection.SelectedGhost.GhostPid
                : 0u;
            string selectedRecordingId = GhostTrackingStationSelection.HasSelectedGhost
                ? GhostTrackingStationSelection.SelectedGhost.RecordingId
                : null;
            if (ShouldAbortMaterializedFocusRetryForUserSelection(
                    spawnedPid,
                    pendingMaterializedFocusBaselineHasSelectedGhost,
                    pendingMaterializedFocusBaselineGhostPid,
                    pendingMaterializedFocusBaselineRecordingId,
                    pendingMaterializedFocusBaselineSelectedPidAvailable,
                    pendingMaterializedFocusBaselineSelectedPid,
                    GhostTrackingStationSelection.HasSelectedGhost,
                    selectedGhostPid,
                    selectedRecordingId,
                    selectedGhostPid != 0,
                    false,
                    0u,
                    out abortReason))
            {
                return true;
            }

            SpaceTracking tracking = UnityEngine.Object.FindObjectOfType<SpaceTracking>();
            if (tracking == null)
                return false;

            if (!GhostTrackingStationSelection.TryGetSelectedVesselPid(
                    tracking,
                    out uint selectedPid,
                    out string selectedError))
            {
                ParsekLog.VerboseRateLimited(Tag,
                    "spawned-focus-selected-vessel-probe-failed",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Spawned Tracking Station focus retry selection probe failed: pid={0} error={1}",
                        spawnedPid,
                        selectedError ?? "(none)"),
                    1.0);
                return false;
            }

            if (!pendingMaterializedFocusBaselineSelectedPidAvailable)
            {
                pendingMaterializedFocusBaselineSelectedPidAvailable = true;
                pendingMaterializedFocusBaselineSelectedPid = selectedPid;
                ParsekLog.Verbose(Tag,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Captured deferred spawned Tracking Station focus retry stock-selection baseline: pid={0} stockPid={1}",
                        spawnedPid,
                        selectedPid));
                return false;
            }

            return ShouldAbortMaterializedFocusRetryForUserSelection(
                spawnedPid,
                pendingMaterializedFocusBaselineHasSelectedGhost,
                pendingMaterializedFocusBaselineGhostPid,
                pendingMaterializedFocusBaselineRecordingId,
                pendingMaterializedFocusBaselineSelectedPidAvailable,
                pendingMaterializedFocusBaselineSelectedPid,
                false,
                0u,
                null,
                false,
                true,
                selectedPid,
                out abortReason);
        }

        internal static bool ShouldAbortMaterializedFocusRetryForUserSelection(
            uint pendingPid,
            bool baselineHasSelectedGhost,
            uint baselineGhostPid,
            string baselineRecordingId,
            bool baselineSelectedPidAvailable,
            uint baselineSelectedPid,
            bool currentHasSelectedGhost,
            uint currentGhostPid,
            string currentRecordingId,
            bool currentGhostPidAvailable,
            bool currentSelectedPidAvailable,
            uint currentSelectedPid,
            out string reason)
        {
            reason = null;
            if (pendingPid == 0)
                return false;

            if (currentHasSelectedGhost)
            {
                if (IsSameMaterializedFocusGhostSelection(
                        baselineHasSelectedGhost,
                        baselineGhostPid,
                        baselineRecordingId,
                        currentGhostPid,
                        currentRecordingId,
                        currentGhostPidAvailable))
                    return false;

                reason = string.Format(
                    CultureInfo.InvariantCulture,
                    "ghost-selection-changed ghostPid={0} baselineGhostPid={1} recId={2} baselineRecId={3}",
                    currentGhostPid,
                    baselineGhostPid,
                    currentRecordingId ?? "(none)",
                    baselineRecordingId ?? "(none)");
                return true;
            }

            if (currentSelectedPidAvailable
                && baselineSelectedPidAvailable
                && currentSelectedPid != 0
                && currentSelectedPid != pendingPid
                && currentSelectedPid != baselineSelectedPid)
            {
                reason = string.Format(
                    CultureInfo.InvariantCulture,
                    "stock-selection-changed selectedPid={0} baselinePid={1}",
                    currentSelectedPid,
                    baselineSelectedPid);
                return true;
            }

            return false;
        }

        internal static bool IsSameMaterializedFocusGhostSelection(
            bool baselineHasSelectedGhost,
            uint baselineGhostPid,
            string baselineRecordingId,
            uint currentGhostPid,
            string currentRecordingId,
            bool currentGhostPidAvailable)
        {
            if (!baselineHasSelectedGhost)
                return false;

            if (currentGhostPidAvailable)
                return currentGhostPid == baselineGhostPid;

            return baselineGhostPid == 0
                && !string.IsNullOrEmpty(baselineRecordingId)
                && string.Equals(
                    baselineRecordingId,
                    currentRecordingId,
                    System.StringComparison.Ordinal);
        }

        private void ClearPendingMaterializedFocus()
        {
            pendingMaterializedFocusPid = 0;
            pendingMaterializedFocusReason = null;
            pendingMaterializedFocusDeadlineTime = 0f;
            nextMaterializedFocusAttemptTime = 0f;
            pendingMaterializedFocusAttempts = 0;
            pendingMaterializedFocusBaselineHasSelectedGhost = false;
            pendingMaterializedFocusBaselineGhostPid = 0;
            pendingMaterializedFocusBaselineRecordingId = null;
            pendingMaterializedFocusBaselineSelectedPidAvailable = false;
            pendingMaterializedFocusBaselineSelectedPid = 0;
        }

        internal static bool ShouldAttemptMaterializedFocusRetry(
            uint pendingPid,
            float now,
            float nextAttemptTime,
            float deadlineTime,
            out bool expired)
        {
            expired = false;
            if (pendingPid == 0)
                return false;

            if (now > deadlineTime)
            {
                expired = true;
                return false;
            }

            return now >= nextAttemptTime;
        }

        private static bool FocusSpawnedTrackingStationVessel(
            uint spawnedPid,
            string reason,
            bool restoreStockSelection,
            bool warnOnFailure,
            out string error)
        {
            error = null;
            Vessel spawned = FlightRecorder.FindVesselByPid(spawnedPid);
            if (spawned != null
                && spawned.mapObject != null
                && PlanetariumCamera.fetch != null)
            {
                if (GhostMapPresence.TrySetReadyMapObjectTarget(
                        () => spawned.mapObject.GetName(),
                        () => PlanetariumCamera.fetch.SetTarget(spawned.mapObject),
                        out string mapObjectName,
                        out string mapObjectError))
                {
                    if (restoreStockSelection)
                    {
                        SpaceTracking tracking = UnityEngine.Object.FindObjectOfType<SpaceTracking>();
                        if (tracking == null)
                        {
                            error = "SpaceTracking instance not found";
                            if (warnOnFailure)
                            {
                                ParsekLog.Warn(Tag,
                                    string.Format(
                                        CultureInfo.InvariantCulture,
                                        "Focus spawned Tracking Station vessel failed: pid={0} reason={1} error={2}",
                                        spawnedPid,
                                        reason ?? "(none)",
                                        error));
                            }
                            return false;
                        }

                        if (!GhostMapPresence.TrySelectTrackingStationVessel(
                                tracking,
                                spawned,
                                out string selectError))
                        {
                            error = string.IsNullOrEmpty(selectError)
                                ? "stock selection failed"
                                : selectError;
                            if (warnOnFailure)
                            {
                                ParsekLog.Warn(Tag,
                                    string.Format(
                                        CultureInfo.InvariantCulture,
                                        "Focus spawned Tracking Station vessel failed: pid={0} reason={1} mapObject='{2}' error={3}",
                                        spawnedPid,
                                        reason ?? "(none)",
                                        mapObjectName ?? "(null)",
                                        error));
                            }
                            return false;
                        }
                    }

                    ParsekLog.Info(Tag,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Focused spawned Tracking Station vessel '{0}' pid={1} mapObject='{2}' reason={3} stockSelected={4}",
                            spawned.vesselName ?? "(unknown)",
                            spawned.persistentId,
                            mapObjectName ?? "(null)",
                            reason ?? "(none)",
                            restoreStockSelection));
                    return true;
                }

                error = mapObjectError ?? "map object focus failed";
                if (warnOnFailure)
                {
                    ParsekLog.Warn(Tag,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Focus spawned Tracking Station vessel failed: pid={0} reason={1} vessel={2} camera={3} mapObj={4} error={5}",
                            spawnedPid,
                            reason ?? "(none)",
                            true,
                            true,
                            true,
                            error));
                }
                return false;
            }

            error = string.Format(
                CultureInfo.InvariantCulture,
                "vessel={0} camera={1} mapObj={2}",
                spawned != null,
                PlanetariumCamera.fetch != null,
                spawned != null && spawned.mapObject != null);
            if (warnOnFailure)
            {
                ParsekLog.Warn(Tag,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Focus spawned Tracking Station vessel failed: pid={0} reason={1} {2}",
                        spawnedPid,
                        reason ?? "(none)",
                        error));
            }
            return false;
        }

        private static Vessel FindGhostVessel(uint persistentId)
        {
            if (persistentId == 0 || !GhostMapPresence.IsGhostMapVessel(persistentId))
                return null;

            var vessels = FlightGlobals.Vessels;
            if (vessels == null)
                return null;

            for (int i = 0; i < vessels.Count; i++)
            {
                Vessel vessel = vessels[i];
                if (vessel != null && vessel.persistentId == persistentId)
                    return vessel;
            }

            return null;
        }

        private void RefreshGhostActionCache()
        {
            if (ghostActionCacheFrame == Time.frameCount)
                return;

            ghostActionCurrentUT = Planetarium.GetUniversalTime();
            ghostActionChains = GhostChainWalker.ComputeAllGhostChains(
                RecordingStore.CommittedTrees,
                ghostActionCurrentUT);
            ghostActionCacheFrame = Time.frameCount;
        }
    }
}
