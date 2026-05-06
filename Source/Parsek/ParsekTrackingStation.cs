using System.Collections.Generic;
using System.Globalization;
using ClickThroughFix;
using KSP.UI.Screens;
using KSP.UI.Screens.Mapview;
using Parsek.Patches;
using ToolbarControl_NS;
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
        private const float LifecycleCheckIntervalSec = 2.0f;
        private const float GhostPopupWidth = 180f;
        private float nextLifecycleCheckTime;
        private ToolbarControl toolbarControl;
        private ParsekUI ui;
        private bool showUI;
        private Rect windowRect = new Rect(20, 100, 250, 10);
        private PopupDialog currentGhostPopup;
        private string currentGhostPopupKey;
        private int ghostPopupOpenFrame;
        private MapObject atmosphericFocusTarget;
        private string atmosphericFocusRecordingId;
        private int atmosphericFocusCachedIndex = -1;
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
            UpdateSelectedGhostPopup();
            UpdateAtmosphericFocusTarget();

            if (Time.time < nextLifecycleCheckTime) return;
            nextLifecycleCheckTime = Time.time + LifecycleCheckIntervalSec;

            GhostMapPresence.UpdateTrackingStationGhostLifecycle();
        }

        void OnGUI()
        {
            // The Esc / pause overlay lives on KSP's Canvas and sorts above
            // our IMGUI layer, so without this gate the ghost icons, ghost
            // action panel, and control surface visually punch through the
            // pause menu. Both the Layout pass AND the Repaint draw are
            // skipped so width-clamped layouts can't flicker between events.
            if (PauseMenuGate.IsPauseMenuOpen())
                return;

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

                if (!atmosCachedIndices.ContainsKey(i))
                    atmosCachedIndices[i] = -1;
                int cached = atmosCachedIndices[i];
                if (!TryResolveRecordingWorldPosition(
                        rec,
                        currentUT,
                        ref cached,
                        out Vector3d worldPos,
                        out _,
                        out TrajectoryPoint sampledPoint,
                        out string resolveReason))
                {
                    if (resolveReason == "body-missing")
                        summary.MissingBody++;
                    else
                        summary.BracketMiss++;
                    continue;
                }
                atmosCachedIndices[i] = cached;

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

                ParsekLog.VerboseRateLimited(Tag, $"atmosMarker-{i}",
                    $"Drawing atmospheric marker #{i} \"{rec.VesselName}\" " +
                    $"terminal={rec.TerminalStateValue?.ToString() ?? "null"} " +
                    $"lat={sampledPoint.latitude:F2} lon={sampledPoint.longitude:F2} alt={sampledPoint.altitude:F0}");
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
            DismissCurrentGhostPopup("tracking-station-cleanup");
            DestroyAtmosphericFocusTarget("tracking-station-cleanup");
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
                DismissCurrentGhostPopup("selected-recording-materialized");
                GhostTrackingStationSelection.ClearSelectedGhost("selected-recording-materialized");
                return;
            }

            double currentUT = ghostActionCurrentUT;
            string key = BuildGhostPopupKey(selection, currentUT);
            if (currentGhostPopup == null || currentGhostPopupKey != key)
            {
                DismissCurrentGhostPopup("opening-new-ghost-popup");
                OpenSelectedGhostPopup(selection, key);
            }

            CheckGhostPopupOutsideClick();
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
                return "materialized";
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
            bool canFocus = CanFocusSelection(selection, vessel, currentUT);
            TrackingStationGhostActionContext context =
                GhostTrackingStationSelection.BuildActionContext(
                    selection,
                    hasGhostVessel: vessel != null,
                    canFocus: canFocus,
                    canSetTarget: false,
                    currentUT: currentUT,
                    chains: ghostActionChains);
            TrackingStationGhostActionState[] actions =
                TrackingStationGhostActionPresentation.BuildActionStates(context);
            TrackingStationGhostActionState focus =
                FindAction(actions, TrackingStationGhostActionKind.Focus);
            TrackingStationGhostActionState materialize =
                FindAction(actions, TrackingStationGhostActionKind.Materialize);

            // KSP dismisses DialogGUIButton after the handler returns. Clear our
            // popup reference first so lifecycle code does not touch a stale dialog.
            var options = new DialogGUIBase[]
            {
                new DialogGUIButton(
                    () => focus.Label,
                    () =>
                    {
                        currentGhostPopup = null;
                        currentGhostPopupKey = null;
                        FocusSelectedGhost(selection);
                        GhostTrackingStationSelection.ClearSelectedGhost("tracking-station focus action");
                    },
                    () => focus.Enabled,
                    160f,
                    30f,
                    true,
                    (DialogGUIBase[])null),
                new DialogGUIButton(
                    () => materialize.Label,
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
                    BuildGhostPopupText(selection, currentUT, context),
                    "Parsek Ghost",
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
                    "Tracking Station ghost popup opened: key={0} ghostPid={1} recId={2} focus={3} materialize={4}",
                    key ?? "(none)",
                    selection.GhostPid,
                    selection.RecordingId ?? "(none)",
                    focus.Enabled,
                    materialize.Enabled));
        }

        private bool CanFocusSelection(
            TrackingStationGhostSelectionInfo selection,
            Vessel vessel,
            double currentUT)
        {
            if (PlanetariumCamera.fetch == null)
                return false;
            if (vessel != null && vessel.mapObject != null)
                return true;
            if (!selection.HasRecording)
                return false;

            int cached = atmosphericFocusCachedIndex;
            return TryResolveSelectionWorldPosition(
                selection,
                currentUT,
                ref cached,
                out _,
                out _,
                out _,
                out _);
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

        private void CheckGhostPopupOutsideClick()
        {
            if (currentGhostPopup == null)
                return;
            if (Time.frameCount - ghostPopupOpenFrame < 5)
                return;
            if (!Input.GetMouseButtonUp(0) && !Input.GetMouseButtonUp(1))
                return;

            RectTransform rt = currentGhostPopup.GetComponent<RectTransform>();
            Vector3 mouse = Input.mousePosition;
            if (IsScreenPointInsideRect(rt, new Vector2(mouse.x, mouse.y)))
            {
                ParsekLog.Verbose(Tag,
                    "Tracking Station ghost popup click ignored: inside-popup");
                return;
            }

            DismissCurrentGhostPopup("outside-click", clearSelection: true);
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
            return BuildGhostPopupText(
                selection,
                currentUT,
                default(TrackingStationGhostActionContext),
                hasActionContext: false);
        }

        internal static string BuildGhostPopupText(
            TrackingStationGhostSelectionInfo selection,
            double currentUT,
            TrackingStationGhostActionContext actionContext)
        {
            return BuildGhostPopupText(
                selection,
                currentUT,
                actionContext,
                hasActionContext: true);
        }

        private static string BuildGhostPopupText(
            TrackingStationGhostSelectionInfo selection,
            double currentUT,
            TrackingStationGhostActionContext actionContext,
            bool hasActionContext)
        {
            string vesselName = string.IsNullOrEmpty(selection.VesselName)
                ? "(ghost)"
                : selection.VesselName;
            string recordingStatus = BuildGhostPopupRecordingStatus(selection, currentUT);
            string endState = selection.TerminalState.HasValue
                ? selection.TerminalState.Value.ToString()
                : "(unknown)";
            string materializeLine = BuildGhostPopupMaterializeStatusLine(
                selection,
                currentUT,
                actionContext,
                hasActionContext);

            return string.Format(
                CultureInfo.InvariantCulture,
                "Ghost: {0}\nRecording: {1}\nEnd state: {2}{3}",
                vesselName,
                recordingStatus,
                endState,
                materializeLine);
        }

        private static string BuildGhostPopupMaterializeStatusLine(
            TrackingStationGhostSelectionInfo selection,
            double currentUT,
            TrackingStationGhostActionContext actionContext,
            bool hasActionContext)
        {
            if (!hasActionContext || !actionContext.MaterializeFastForwardEligible)
                return string.Empty;

            double remaining = selection.EndUT - currentUT;
            if (!(remaining > 0.0))
                return string.Empty;

            return "\nMaterialize: fast-forward "
                + ParsekTimeFormat.FormatDuration(remaining);
        }

        private static string BuildGhostPopupRecordingStatus(
            TrackingStationGhostSelectionInfo selection,
            double currentUT)
        {
            if (!selection.HasRecording)
                return "unavailable";
            if (selection.VesselSpawned || selection.SpawnedVesselPersistentId != 0)
                return "materialized";
            if (double.IsNaN(selection.EndUT))
                return "unknown";

            double remaining = selection.EndUT - currentUT;
            return remaining > 0.0
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "T-{0:F1}s to endpoint",
                    remaining)
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
                FocusSpawnedTrackingStationVessel(
                    recording.SpawnedVesselPersistentId,
                    "atmospheric-focus-materialized");
                DestroyAtmosphericFocusTarget("atmospheric-focus-materialized");
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
                        recording.TrackSections[sectionIdx],
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
                        nearestSection,
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
            TrackSection section,
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
                if (section.absoluteFrames == null || section.absoluteFrames.Count == 0)
                {
                    reason = "relative-without-absolute-shadow";
                    blocked = true;
                    return false;
                }

                frames = section.absoluteFrames;
                return true;
            }

            if (section.frames != null && section.frames.Count > 0)
            {
                frames = section.frames;
                return true;
            }

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
                        "Materialize selected ghost failed: missing recording for ghostPid={0} recId={1}",
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
                            "Materialize fast-forward refused: recId={0} fromUT={1:F1} endpointUT={2:F1} reason={3}",
                            recording.RecordingId ?? "(none)",
                            currentUT,
                            recording.EndUT,
                            endpointMaterialize.reason ?? "(none)"));
                    ParsekLog.ScreenMessage(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Cannot materialize \"{0}\": {1}",
                            recording.VesselName ?? "ghost",
                            endpointMaterialize.reason ?? "endpoint blocked"),
                        4f);
                    GhostTrackingStationSelection.ClearSelectedGhost("materialize endpoint ineligible");
                    return;
                }

                double jumpDelta = recording.EndUT - currentUT;
                ParsekLog.Info(Tag,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Materialize fast-forward requested: recId={0} fromUT={1:F1} endpointUT={2:F1} delta={3:F1}s",
                        recording.RecordingId ?? "(none)",
                        currentUT,
                        recording.EndUT,
                        jumpDelta));
                TimeJumpManager.ExecuteForwardJump(recording.EndUT);
                ParsekLog.ScreenMessage(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Fast-forwarded to materialize \"{0}\" ({1:F0}s)",
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
                    "Materialize requested for Tracking Station ghost pid={0} recId={1} ut={2:F1} handled={3}",
                    selection.GhostPid,
                    selection.RecordingId ?? "(none)",
                    currentUT,
                    handled));

            if (handled)
            {
                if (recording.SpawnedVesselPersistentId != 0)
                    FocusSpawnedTrackingStationVessel(
                        recording.SpawnedVesselPersistentId,
                        "tracking-station-materialize");
                DestroyAtmosphericFocusTarget("materialize handoff");
            }

            GhostTrackingStationSelection.ClearSelectedGhost("materialize handoff");
        }

        private static bool FocusSpawnedTrackingStationVessel(uint spawnedPid, string reason)
        {
            Vessel spawned = FlightRecorder.FindVesselByPid(spawnedPid);
            if (spawned != null
                && spawned.mapObject != null
                && PlanetariumCamera.fetch != null)
            {
                PlanetariumCamera.fetch.SetTarget(spawned.mapObject);
                ParsekLog.Info(Tag,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Focused materialized Tracking Station vessel '{0}' pid={1} reason={2}",
                        spawned.vesselName ?? "(unknown)",
                        spawned.persistentId,
                        reason ?? "(none)"));
                return true;
            }

            ParsekLog.Warn(Tag,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Focus materialized Tracking Station vessel failed: pid={0} reason={1} vessel={2} camera={3} mapObj={4}",
                    spawnedPid,
                    reason ?? "(none)",
                    spawned != null,
                    PlanetariumCamera.fetch != null,
                    spawned != null && spawned.mapObject != null));
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
