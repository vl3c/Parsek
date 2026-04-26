using System.Collections.Generic;
using System.Globalization;
using ClickThroughFix;
using KSP.UI.Screens;
using Parsek.Patches;
using ToolbarControl_NS;
using UnityEngine;

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
        private const float LifecycleCheckIntervalSec = 2.0f;
        private const float GhostActionsWindowWidth = 330f;
        private float nextLifecycleCheckTime;
        private ToolbarControl toolbarControl;
        private ParsekUI ui;
        private bool showUI;
        private Rect windowRect = new Rect(20, 100, 250, 10);
        private Rect ghostActionsWindowRect = new Rect(20, 140, GhostActionsWindowWidth, 10);
        private bool showSelectedRecordingDetails;
        private uint recordingDetailsGhostPid;
        private int ghostActionCacheFrame = -1;
        private double ghostActionCurrentUT;
        private Dictionary<uint, GhostChain> ghostActionChains = new Dictionary<uint, GhostChain>();

        /// <summary>Cached interpolation indices for atmospheric ghost icon rendering (per recording index).</summary>
        private readonly Dictionary<int, int> atmosCachedIndices = new Dictionary<int, int>();

        /// <summary>Tracks the last known committed recording count for live-update detection.</summary>
        private int lastKnownCommittedCount;

        /// <summary>
        /// Tracks the last-known value of <c>ParsekSettings.Current.showGhostsInTrackingStation</c>.
        /// When the flag flips we force a lifecycle tick so ghosts appear/disappear
        /// immediately without waiting for the 2-second interval.
        /// </summary>
        private bool lastKnownShowGhosts = true;

        internal struct TrackingStationControlSurfaceState
        {
            internal int CommittedRecordings;
            internal int VisibleGhostVessels;
            internal int SuppressedRecordings;
            internal int MaterializedRecordings;
            internal bool ShowGhosts;
        }

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
            public int HiddenBySetting;
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

            internal bool HasSignal =>
                Candidates > 0
                || Drawn > 0
                || CameraUnavailable > 0
                || HiddenBySetting > 0
                || NoCommittedRecordings > 0
                || NativeIconActive > 0
                || NullRecording > 0
                || Debris > 0
                || NoTrajectoryPoints > 0
                || OutsideTimeRange > 0
                || SuppressedByChainFilter > 0
                || OrbitSegmentActive > 0
                || BracketMiss > 0
                || MissingBody > 0;
        }

        internal static string FormatAtmosphericMarkerSummary(AtmosphericMarkerSummary summary)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Atmospheric marker summary: event={0} candidates={1} drawn={2} cameraUnavailable={3} hiddenBySetting={4} noCommitted={5} nativeIcon={6} nullRecording={7} debris={8} noPoints={9} outsideTimeRange={10} chainSuppressed={11} orbitSegment={12} bracketMiss={13} missingBody={14}",
                string.IsNullOrEmpty(summary.EventTypeName) ? "(unknown)" : summary.EventTypeName,
                summary.Candidates,
                summary.Drawn,
                summary.CameraUnavailable,
                summary.HiddenBySetting,
                summary.NoCommittedRecordings,
                summary.NativeIconActive,
                summary.NullRecording,
                summary.Debris,
                summary.NoTrajectoryPoints,
                summary.OutsideTimeRange,
                summary.SuppressedByChainFilter,
                summary.OrbitSegmentActive,
                summary.BracketMiss,
                summary.MissingBody);
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
            ui = new ParsekUI(UIMode.TrackingStation);
            InstallToolbar();

            // Read through the persistence store so the startup tick uses the
            // recorded user preference even when ParsekSettings.Current isn't
            // resolved yet (early-scene-load case, see ParsekScenario.cs:546).
            lastKnownShowGhosts = ParsekSettingsPersistence.EffectiveShowGhostsInTrackingStation();
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
                $"showGhostsInTrackingStation={lastKnownShowGhosts}, " +
                $"trackingStationSuppressed={suppressedForGhosts}, " +
                "orbitSourceDiagnostics=aggregated");
        }

        private void InstallToolbar()
        {
            toolbarControl = gameObject.AddComponent<ToolbarControl>();
            toolbarControl.AddToAllToolbars(
                () => { showUI = true; ParsekLog.Verbose(Tag, "Toolbar button ON"); },
                () => { showUI = false; ParsekLog.Verbose(Tag, "Toolbar button OFF"); },
                ApplicationLauncher.AppScenes.TRACKSTATION,
                ParsekFlight.MODID, "parsekTrackingStationButton",
                "Parsek/Textures/parsek_64",
                "Parsek/Textures/parsek_32",
                ParsekFlight.MODNAME
            );

            ui.CloseMainWindow = () =>
            {
                showUI = false;
                if (toolbarControl != null) toolbarControl.SetFalse();
            };
        }

        void Update()
        {
            // Detect live recording commits (merge dialog, approval dialog) and force
            // an immediate lifecycle tick so proto-vessel ghosts appear without waiting
            // for the normal 2-second interval.
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

            // #388: detect the ghost visibility flag flipping and react immediately.
            // On off-flip, remove every ghost ProtoVessel so the vessel list empties
            // without waiting for a committed-count change. On on-flip, force a tick
            // so the Phase-2 loop in UpdateTrackingStationGhostLifecycle recreates
            // ghosts for every eligible recording.
            bool currentShowGhosts = ParsekSettingsPersistence.EffectiveShowGhostsInTrackingStation();
            if (currentShowGhosts != lastKnownShowGhosts)
            {
                ParsekLog.Info(Tag,
                    $"showGhostsInTrackingStation flipped {lastKnownShowGhosts} → {currentShowGhosts} " +
                    "— forcing immediate lifecycle tick");
                lastKnownShowGhosts = currentShowGhosts;
                if (!currentShowGhosts)
                    GhostMapPresence.RemoveAllGhostVessels("ghost-filter-disabled");
                nextLifecycleCheckTime = 0f;
            }

            if (GhostTrackingStationSelection.HasSelectedGhost)
                RefreshGhostActionCache();

            if (Time.time < nextLifecycleCheckTime) return;
            nextLifecycleCheckTime = Time.time + LifecycleCheckIntervalSec;

            GhostMapPresence.UpdateTrackingStationGhostLifecycle();
        }

        void OnGUI()
        {
            DrawSelectedGhostActionSurface();
            DrawAtmosphericMarkers();
            DrawControlSurface();
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
            if (!ShouldProcessAtmosphericMarkerEvent(
                    etype,
                    IsPointerOverParsekWindow(currentEvent.mousePosition)))
                return;
            if (PlanetariumCamera.Camera == null)
            {
                summary.CameraUnavailable++;
                LogAtmosphericMarkerSummary(summary);
                return;
            }

            // #388: skip the whole atmospheric-marker pass when the user has
            // hidden ghosts in the tracking station.
            if (!ParsekSettingsPersistence.EffectiveShowGhostsInTrackingStation())
            {
                summary.HiddenBySetting++;
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

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                summary.Candidates++;
                AtmosphericMarkerSkipReason skipReason =
                    ClassifyAtmosphericMarkerSkip(rec, i, currentUT, suppressed);
                if (skipReason != AtmosphericMarkerSkipReason.None)
                {
                    CountAtmosphericMarkerSkip(ref summary, skipReason);
                    continue;
                }

                // Interpolate trajectory position at current UT
                if (!atmosCachedIndices.ContainsKey(i))
                    atmosCachedIndices[i] = -1;
                int cached = atmosCachedIndices[i];
                TrajectoryPoint? pt = TrajectoryMath.BracketPointAtUT(rec.Points, currentUT, ref cached);
                atmosCachedIndices[i] = cached;

                if (!pt.HasValue)
                {
                    summary.BracketMiss++;
                    continue;
                }

                CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == pt.Value.bodyName);
                if (body == null)
                {
                    summary.MissingBody++;
                    continue;
                }

                Vector3d worldPos = body.GetWorldSurfacePosition(
                    pt.Value.latitude, pt.Value.longitude, pt.Value.altitude);

                VesselType vtype = ResolveVesselTypeWithFallback(committed, rec);
                Color markerColor = MapMarkerRenderer.GetColorForType(vtype);
                MapMarkerRenderer.DrawMarker(
                    worldPos, rec.RecordingId, rec.VesselName ?? "(unknown)", markerColor, vtype);
                summary.Drawn++;

                ParsekLog.VerboseRateLimited(Tag, $"atmosMarker-{i}",
                    $"Drawing atmospheric marker #{i} \"{rec.VesselName}\" " +
                    $"terminal={rec.TerminalStateValue?.ToString() ?? "null"} " +
                        $"lat={pt.Value.latitude:F2} lon={pt.Value.longitude:F2} alt={pt.Value.altitude:F0}");
            }

            LogAtmosphericMarkerSummary(summary);
        }

        internal bool IsPointerOverParsekWindow(Vector2 mousePosition)
        {
            return ParsekUI.IsPointerOverOpenWindow(showUI, windowRect, mousePosition)
                || (ui != null && ui.IsMouseOverOpenAuxiliaryWindows(mousePosition));
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

        private void DrawControlSurface()
        {
            if (!showUI || ui == null) return;

            windowRect.height = 0f;
            var opaqueWindowStyle = ui.GetOpaqueWindowStyle();
            if (opaqueWindowStyle == null)
                return;

            ParsekUI.ResetWindowGuiColors(
                out Color prevColor,
                out Color prevBackgroundColor,
                out Color prevContentColor);
            try
            {
                windowRect = ClickThruBlocker.GUILayoutWindow(
                    GetInstanceID(),
                    windowRect,
                    DrawControlSurfaceWindow,
                    "Parsek",
                    opaqueWindowStyle,
                    GUILayout.Width(250));
            }
            finally
            {
                ParsekUI.RestoreWindowGuiColors(prevColor, prevBackgroundColor, prevContentColor);
            }

            ui.LogMainWindowPosition(windowRect);
            ui.DrawRecordingsWindowIfOpen(windowRect);
            ui.DrawSettingsWindowIfOpen(windowRect);
            ui.DrawTestRunnerWindowIfOpen(windowRect, this);
        }

        private void DrawControlSurfaceWindow(int windowID)
        {
            TrackingStationControlSurfaceState state = BuildControlSurfaceState(
                RecordingStore.CommittedRecordings,
                GhostMapPresence.ghostMapVesselPids.Count,
                GhostMapPresence.CachedTrackingStationSuppressedIds?.Count ?? 0,
                ParsekSettingsPersistence.EffectiveShowGhostsInTrackingStation());

            GUILayout.BeginVertical();

            GUILayout.Label("Tracking Station", GUI.skin.box);
            GUILayout.Label(state.ShowGhosts ? "Ghosts: visible" : "Ghosts: hidden");
            GUILayout.Label(FormatControlSurfaceCountsLine(state));
            GUILayout.Label(FormatControlSurfaceLifecycleLine(state));

            GUILayout.Space(10f);

            bool showGhosts = GUILayout.Toggle(
                state.ShowGhosts,
                new GUIContent(
                    " Show ghosts in Tracking Station",
                    "Show or hide Parsek ghost vessels and markers in the Tracking Station"));
            if (showGhosts != state.ShowGhosts)
                SetShowGhostsFromControlSurface(showGhosts);

            GUILayout.Space(10f);

            if (GUILayout.Button(string.Format(
                    CultureInfo.InvariantCulture,
                    "Recordings ({0})",
                    state.CommittedRecordings)))
            {
                ui.ToggleRecordingsWindow();
            }

            if (GUILayout.Button("Settings"))
                ui.ToggleSettingsWindow();

            GUILayout.Space(10f);
            if (GUILayout.Button("Close"))
            {
                showUI = false;
                if (toolbarControl != null) toolbarControl.SetFalse();
                ParsekLog.Verbose(Tag, "Control surface closed via button");
            }

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        private void SetShowGhostsFromControlSurface(bool showGhosts)
        {
            bool liveSettingsUpdated = TryApplyGhostVisibilitySetting(
                ParsekSettings.Current,
                showGhosts);
            ParsekSettingsPersistence.RecordShowGhostsInTrackingStation(showGhosts);
            lastKnownShowGhosts = showGhosts;

            if (!showGhosts)
                GhostMapPresence.RemoveAllGhostVessels("tracking-station-ui-toggle");

            nextLifecycleCheckTime = 0f;
            ParsekLog.Info(Tag,
                $"Control surface set showGhostsInTrackingStation={showGhosts} " +
                $"liveSettingsUpdated={liveSettingsUpdated}");
        }

        internal static bool TryApplyGhostVisibilitySetting(ParsekSettings settings, bool showGhosts)
        {
            if (settings == null)
                return false;

            settings.showGhostsInTrackingStation = showGhosts;
            return true;
        }

        internal static TrackingStationControlSurfaceState BuildControlSurfaceState(
            IReadOnlyList<Recording> committed,
            int visibleGhostVessels,
            int suppressedRecordings,
            bool showGhosts)
        {
            int committedCount = committed?.Count ?? 0;
            int materialized = 0;
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    Recording rec = committed[i];
                    if (rec != null && (rec.VesselSpawned || rec.SpawnedVesselPersistentId != 0))
                        materialized++;
                }
            }

            return new TrackingStationControlSurfaceState
            {
                CommittedRecordings = ClampNonNegative(committedCount),
                VisibleGhostVessels = ClampNonNegative(visibleGhostVessels),
                SuppressedRecordings = ClampNonNegative(suppressedRecordings),
                MaterializedRecordings = ClampNonNegative(materialized),
                ShowGhosts = showGhosts
            };
        }

        internal static string FormatControlSurfaceCountsLine(
            TrackingStationControlSurfaceState state)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Recordings: {0} | Map ghosts: {1}",
                state.CommittedRecordings,
                state.VisibleGhostVessels);
        }

        internal static string FormatControlSurfaceLifecycleLine(
            TrackingStationControlSurfaceState state)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Suppressed: {0} | Materialized: {1}",
                state.SuppressedRecordings,
                state.MaterializedRecordings);
        }

        private static int ClampNonNegative(int value)
        {
            return value < 0 ? 0 : value;
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

        internal static AtmosphericMarkerSkipReason ClassifyAtmosphericMarkerSkip(
            Recording rec, int recordingIndex, double currentUT,
            HashSet<string> suppressedIds)
        {
            // A ProtoVessel exists but its icon may be suppressed (below atmosphere).
            // When suppressed, the atmospheric marker should still draw.
            if (GhostMapPresence.HasGhostVesselForRecording(recordingIndex))
            {
                uint ghostPid = GhostMapPresence.GetGhostVesselPidForRecording(recordingIndex);
                if (ghostPid == 0 || !GhostMapPresence.IsIconSuppressed(ghostPid))
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
            if (rec.HasOrbitSegments
                && TrajectoryMath.FindOrbitSegment(rec.OrbitSegments, currentUT).HasValue)
                return AtmosphericMarkerSkipReason.OrbitSegmentActive;
            return AtmosphericMarkerSkipReason.None;
        }

        void OnDestroy()
        {
            GhostTrackingStationSelection.ClearSelectedGhost("tracking-station-cleanup");
            GhostMapPresence.RemoveAllGhostVessels("tracking-station-cleanup");
            // Clear ghost-icon sticky state and force atlas re-init when leaving TS.
            // Flight scene's own OnSceneChangeRequested hook runs too, but this
            // addon is the only always-present TS listener — keep it defensive.
            MapMarkerRenderer.ResetForSceneChange();
            ui?.Cleanup();
            ui = null;
            if (toolbarControl != null)
            {
                toolbarControl.OnDestroy();
                Destroy(toolbarControl);
                toolbarControl = null;
            }
            ParsekLog.Info(Tag, "ParsekTrackingStation destroyed");
        }

        private void DrawSelectedGhostActionSurface()
        {
            if (!GhostTrackingStationSelection.HasSelectedGhost)
                return;

            RefreshGhostActionCache();

            TrackingStationGhostSelectionInfo selection = GhostTrackingStationSelection.SelectedGhost;
            Vessel vessel = FindGhostVessel(selection.GhostPid);
            if (vessel == null)
            {
                GhostTrackingStationSelection.ClearSelectedGhost("selected ghost vessel missing");
                return;
            }

            if (recordingDetailsGhostPid != selection.GhostPid)
            {
                recordingDetailsGhostPid = selection.GhostPid;
                showSelectedRecordingDetails = false;
            }

            if (ghostActionsWindowRect.x < 0f || ghostActionsWindowRect.x > Screen.width - 80f)
                ghostActionsWindowRect.x = 20f;
            if (ghostActionsWindowRect.y < 0f || ghostActionsWindowRect.y > Screen.height - 80f)
                ghostActionsWindowRect.y = 140f;

            ghostActionsWindowRect.height = 0f;
            GUIStyle windowStyle = GUI.skin != null ? GUI.skin.window : null;
            ParsekUI.ResetWindowGuiColors(
                out Color prevColor,
                out Color prevBackgroundColor,
                out Color prevContentColor);
            try
            {
                ghostActionsWindowRect = ClickThruBlocker.GUILayoutWindow(
                    GetInstanceID() + 553,
                    ghostActionsWindowRect,
                    id => DrawSelectedGhostActionsWindow(id, vessel, selection),
                    "Parsek Ghost",
                    windowStyle,
                    GUILayout.Width(GhostActionsWindowWidth));
            }
            finally
            {
                ParsekUI.RestoreWindowGuiColors(prevColor, prevBackgroundColor, prevContentColor);
            }
        }

        private void DrawSelectedGhostActionsWindow(
            int windowId,
            Vessel vessel,
            TrackingStationGhostSelectionInfo selection)
        {
            double currentUT = ghostActionCurrentUT;
            TrackingStationGhostActionContext context =
                GhostTrackingStationSelection.BuildActionContext(
                    selection,
                    hasGhostVessel: vessel != null,
                    canFocus: PlanetariumCamera.fetch != null && vessel?.mapObject != null,
                    canSetTarget: FlightGlobals.fetch != null,
                    currentUT: currentUT,
                    chains: ghostActionChains);
            TrackingStationGhostActionState[] actions =
                TrackingStationGhostActionPresentation.BuildActionStates(context);

            GUILayout.BeginVertical();
            GUILayout.Label(selection.VesselName ?? "(ghost)");
            GUILayout.Label(BuildGhostSummary(selection, currentUT));

            GUILayout.BeginHorizontal();
            DrawActionButton(
                FindAction(actions, TrackingStationGhostActionKind.Focus),
                () => FocusSelectedGhost(vessel),
                78f);
            DrawActionButton(
                FindAction(actions, TrackingStationGhostActionKind.SetTarget),
                () => TargetSelectedGhost(vessel),
                78f);
            DrawActionButton(
                FindAction(actions, TrackingStationGhostActionKind.ShowRecording),
                () => ToggleSelectedRecordingDetails(selection),
                92f);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            DrawActionButton(
                FindAction(actions, TrackingStationGhostActionKind.Materialize),
                () => MaterializeSelectedGhost(selection, currentUT),
                106f);
            DrawActionButton(
                FindAction(actions, TrackingStationGhostActionKind.Fly),
                null,
                58f);
            DrawActionButton(
                FindAction(actions, TrackingStationGhostActionKind.Delete),
                null,
                68f);
            DrawActionButton(
                FindAction(actions, TrackingStationGhostActionKind.Recover),
                null,
                76f);
            GUILayout.EndHorizontal();

            TrackingStationGhostActionState materialize =
                FindAction(actions, TrackingStationGhostActionKind.Materialize);
            if (!materialize.Enabled)
                GUILayout.Label(materialize.Reason);

            GUILayout.Label("Fly/Delete/Recover are blocked on ghost objects.");

            if (showSelectedRecordingDetails)
                DrawSelectedRecordingDetails(selection);

            if (GUILayout.Button("Close"))
                GhostTrackingStationSelection.ClearSelectedGhost("ghost action panel closed");

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        private static void DrawActionButton(
            TrackingStationGhostActionState action,
            System.Action onClick,
            float width)
        {
            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && action.Enabled;
            if (GUILayout.Button(new GUIContent(action.Label, action.Reason), GUILayout.Width(width))
                && action.Enabled)
            {
                onClick?.Invoke();
            }
            GUI.enabled = previousEnabled;
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

        private static string BuildGhostSummary(
            TrackingStationGhostSelectionInfo selection,
            double currentUT)
        {
            if (!selection.HasRecording)
                return "Chain ghost";

            string id = string.IsNullOrEmpty(selection.RecordingId)
                ? "(unknown)"
                : selection.RecordingId;
            double remaining = selection.EndUT - currentUT;
            string phase = remaining > 0.0
                ? string.Format(CultureInfo.InvariantCulture, "T-{0:F1}s", remaining)
                : "endpoint reached";

            return string.Format(
                CultureInfo.InvariantCulture,
                "Recording {0} - {1}",
                id,
                phase);
        }

        private static void DrawSelectedRecordingDetails(TrackingStationGhostSelectionInfo selection)
        {
            GUILayout.Space(4f);
            GUILayout.Label("Recording details");
            GUILayout.Label("ID: " + (selection.RecordingId ?? "(none)"));
            if (!double.IsNaN(selection.StartUT) && !double.IsNaN(selection.EndUT))
            {
                GUILayout.Label(string.Format(
                    CultureInfo.InvariantCulture,
                    "UT: {0:F1} - {1:F1}",
                    selection.StartUT,
                    selection.EndUT));
            }
            GUILayout.Label("Terminal: " + (selection.TerminalState?.ToString() ?? "(unknown)"));
            if (selection.SpawnedVesselPersistentId != 0)
                GUILayout.Label("Spawned PID: " + selection.SpawnedVesselPersistentId);
            else
                GUILayout.Label("Spawned: " + selection.VesselSpawned);
        }

        private static void FocusSelectedGhost(Vessel vessel)
        {
            if (vessel == null || PlanetariumCamera.fetch == null || vessel.mapObject == null)
            {
                ParsekLog.Warn(Tag,
                    $"Focus selected ghost failed: vessel={vessel != null} " +
                    $"camera={PlanetariumCamera.fetch != null} mapObj={vessel?.mapObject != null}");
                return;
            }

            PlanetariumCamera.fetch.SetTarget(vessel.mapObject);
            ParsekLog.Info(Tag,
                $"Focused Tracking Station ghost '{vessel.vesselName}' pid={vessel.persistentId}");
        }

        private static void TargetSelectedGhost(Vessel vessel)
        {
            if (vessel == null)
            {
                ParsekLog.Warn(Tag,
                    "Target selected ghost failed: vessel=False");
                return;
            }

            int recIndex = GhostMapPresence.FindRecordingIndexByVesselPid(vessel.persistentId);
            GhostMapPresence.SetGhostMapNavigationTarget(vessel, recIndex, "tracking station panel");
        }

        private void ToggleSelectedRecordingDetails(TrackingStationGhostSelectionInfo selection)
        {
            showSelectedRecordingDetails = !showSelectedRecordingDetails;
            recordingDetailsGhostPid = selection.GhostPid;
            ParsekLog.Verbose(Tag,
                $"Tracking Station ghost recording details toggled: " +
                $"{(showSelectedRecordingDetails ? "open" : "closed")} " +
                $"recId={selection.RecordingId ?? "(none)"}");
        }

        private static void MaterializeSelectedGhost(
            TrackingStationGhostSelectionInfo selection,
            double currentUT)
        {
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || string.IsNullOrEmpty(selection.RecordingId))
            {
                ParsekLog.Warn(Tag,
                    $"Materialize selected ghost failed: missing recording ID for ghost pid={selection.GhostPid}");
                return;
            }

            bool handled = GhostMapPresence.TryRunTrackingStationSpawnHandoffForRecordingId(
                committed,
                selection.RecordingId,
                currentUT,
                reselectSpawnedVessel: true);
            ParsekLog.Info(Tag,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Materialize requested for Tracking Station ghost pid={0} recId={1} ut={2:F1} handled={3}",
                    selection.GhostPid,
                    selection.RecordingId ?? "(none)",
                    currentUT,
                    handled));

            if (!GhostMapPresence.IsGhostMapVessel(selection.GhostPid))
                GhostTrackingStationSelection.ClearSelectedGhost("materialize handoff");
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
