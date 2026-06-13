using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClickThroughFix;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    public enum UIMode { Flight, KSC, TrackingStation }

    /// <summary>
    /// UI rendering for the Parsek window and map view markers.
    /// Receives a ParsekFlight reference for accessing flight state.
    /// </summary>
    public class ParsekUI
    {
        private readonly UIMode mode;
        private bool InFlight => mode == UIMode.Flight;

        private readonly ParsekFlight flight;

        // Opaque window style (replaces KSP's semi-transparent default)
        private GUIStyle opaqueWindowStyle;
        private GameScenes opaqueWindowStyleScene;
        private bool hasOpaqueWindowStyleScene;

        // Shared header styles (promoted from CareerStateWindowUI so every window
        // renders section bars and column headers the same way). Lazy-initialized
        // via EnsureSharedHeaderStyles because GUIStyle construction requires a
        // valid GUI.skin — only available during draw.
        private GUIStyle sharedSectionHeaderStyle;
        private GUIStyle sharedColumnHeaderStyle;

        // Map view markers: icon atlas, fallback texture, label style, hover/sticky
        // state all live in MapMarkerRenderer (shared with ParsekTrackingStation).

        // Recordings table window (extracted to RecordingsTableUI)
        private RecordingsTableUI recordingsTableUI;

        // Missions window (extracted to MissionsWindowUI)
        private MissionsWindowUI missionsUI;

        private const float ResizeHandleSize = 16f;

        // Settings window (extracted to SettingsWindowUI)
        private SettingsWindowUI settingsUI;

        // Test runner window (extracted to TestRunnerUI)
        private TestRunnerUI testRunnerUI;

        // Timeline window (extracted to TimelineWindowUI, replaces ActionsWindowUI)
        private TimelineWindowUI timelineUI;

        // Kerbals window (reserved crew, active stand-ins, retired stand-ins; #385)
        private KerbalsWindowUI kerbalsUI;

        // Career State window (contracts, strategies, facilities, milestones; #416)
        private CareerStateWindowUI careerStateUI;

        // Reusable per-frame buffers (used by DrawMapMarkers for chain dedup)
        private static readonly Dictionary<string, int> chainTipIndexBuffer = new Dictionary<string, int>();

        // Review MINOR-1 (PR #1105): chains that drew a LABELED marker via the pid-keyed walk this
        // frame, so the ghost-less polyline fallback never adds a second marker for the same chain
        // during a warp handoff. A chain whose tip showed only its stock vessel icon (nativeIcon skip)
        // is NOT in this set - the fallback may still mark the playing head there (two genuine
        // objects, two icons). Cleared with chainTipIndexBuffer each walk.
        private static readonly HashSet<string> drawnChainMarkerBuffer =
            new HashSet<string>(StringComparer.Ordinal);

        // Slice (iii): reusable per-recording head-UT buffer for the per-instance overlap marker
        // branch. Cleared (not reallocated) per overlap recording inside TryGetLiveOverlapHeadUTs so
        // drawing N markers on the one shared polyline allocates nothing per frame.
        private static readonly List<(long cycle, double headUT)> overlapHeadUtBuffer =
            new List<(long, double)>();

        // The labeled marker uses GhostMapPresence.IsPolylineRecentlyOwningGhostPhase to decide
        // when to prefer trajPos over the stale OrbitDriver mesh transform during the brief
        // post-polyline-release window (the seg-drive dispatcher runs on a ~0.5s cadence so the
        // mesh transform is stale for ~12 frames at 60Hz right after polyline release). The
        // GhostOrbitLinePatch reads from the same source to keep the stock icon suppressed
        // through the same window, so both the labeled marker and the stock icon agree.

        // Cached waypoint indices and body lookup for trajectory-derived map marker positions (Bug A fix)
        private readonly Dictionary<int, int> mapMarkerCachedIndices = new Dictionary<int, int>();
        private readonly Dictionary<string, CelestialBody> bodyCache = new Dictionary<string, CelestialBody>();

        // Window drag tracking for position logging
        private Rect lastMainWindowRect;
        // Spawn Control window (extracted to SpawnControlUI)
        private SpawnControlUI spawnControlUI;

        // Structure-list window (one reusable instance, retargeted per mission / route)
        private StructureListWindowUI structureListUI;

        // Gloops Flight Recorder window (extracted to GloopsRecorderUI)
        private GloopsRecorderUI gloopsUI;

        // Logistics window (v0 — Supply Routes tab + Send Once button)
        private LogisticsWindowUI logisticsUI;

        private static readonly string VersionLabel = "v" +
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        private GUIStyle versionStyle;

        // Shared time-range filter state — read by TimelineWindowUI and RecordingsTableUI
        internal readonly TimeRangeFilterState TimeRangeFilter = new TimeRangeFilterState();

        internal ParsekFlight Flight => flight;
        internal UIMode Mode => mode;
        internal bool InFlightMode => InFlight;
        internal RecordingsTableUI GetRecordingsTableUI() => recordingsTableUI;
        internal SettingsWindowUI GetSettingsWindowUI() => settingsUI;
        internal TimelineWindowUI GetTimelineUI() => timelineUI;
        internal CareerStateWindowUI GetCareerStateUI() { return careerStateUI; }

        /// <summary>
        /// Shared cross-link between Timeline and Recordings Manager.
        /// Setting this from either window causes the other to scroll to and highlight
        /// the matching recording. Null means no selection.
        /// </summary>
        internal string SelectedRecordingId { get; set; }

        // Runtime-only empty groups — delegated to RecordingsTableUI
        internal List<string> KnownEmptyGroups => recordingsTableUI.KnownEmptyGroups;

        // Host-supplied callback invoked when the user clicks the main window Close
        // button. Host (ParsekFlight / ParsekKSC) wires this to hide the window and
        // un-press the toolbar button.
        internal Action CloseMainWindow { get; set; }

        public ParsekUI(ParsekFlight flight)
        {
            this.flight = flight;
            this.mode = UIMode.Flight;
            this.recordingsTableUI = new RecordingsTableUI(this);
            this.missionsUI = new MissionsWindowUI(this);
            this.spawnControlUI = new SpawnControlUI(this);
            this.gloopsUI = new GloopsRecorderUI(this);
            this.timelineUI = new TimelineWindowUI(this);
            this.kerbalsUI = new KerbalsWindowUI(this);
            this.careerStateUI = new CareerStateWindowUI(this);
            this.testRunnerUI = new TestRunnerUI(this);
            this.settingsUI = new SettingsWindowUI(this);
            this.logisticsUI = new LogisticsWindowUI(this);
            this.structureListUI = new StructureListWindowUI(this);
            LedgerOrchestrator.OnTimelineDataChanged += OnTimelineDataChanged;
        }

        public ParsekUI(UIMode mode)
        {
            this.flight = null;
            this.mode = mode;
            this.recordingsTableUI = new RecordingsTableUI(this);
            this.missionsUI = new MissionsWindowUI(this);
            this.spawnControlUI = new SpawnControlUI(this);
            this.gloopsUI = new GloopsRecorderUI(this);
            this.timelineUI = new TimelineWindowUI(this);
            this.kerbalsUI = new KerbalsWindowUI(this);
            this.careerStateUI = new CareerStateWindowUI(this);
            this.testRunnerUI = new TestRunnerUI(this);
            this.settingsUI = new SettingsWindowUI(this);
            this.logisticsUI = new LogisticsWindowUI(this);
            this.structureListUI = new StructureListWindowUI(this);
            LedgerOrchestrator.OnTimelineDataChanged += OnTimelineDataChanged;
        }

        private void OnTimelineDataChanged()
        {
            timelineUI.InvalidateCache();
            kerbalsUI.InvalidateCache();
            careerStateUI.InvalidateCache();
        }

        private const float SpacingSmall = 3f;
        private const float SpacingLarge = 10f;

        internal static string GetKerbalsMainButtonLabel() => "Kerbals";

        internal static string GetCareerMainButtonLabel() => "Career";

        /// <summary>
        /// Returns the resource budget.
        /// </summary>
        internal BudgetSummary GetCachedBudget()
        {
            return recordingsTableUI.GetCachedBudget();
        }

        public void DrawWindow(int windowID)
        {
            GUILayout.BeginVertical();

            if (InFlight)
                DrawFlightStatus();

            DrawCompactBudgetLine();

            GUILayout.Space(SpacingLarge);

            // Button order (separator groups requested by the player):
            //   1. Real Spawn Control  (InFlight-only; its trailing separator is inside the block)
            //   2. Timeline / Recordings
            //   3. Kerbals / Career
            //   4. Gloops Flight Recorder  (InFlight-only; trailing separator inside the block)
            //   5. Settings

            // --- Real Spawn Control (InFlight-only, top of the button column) ---
            if (InFlight && flight != null)
            {
                int spawnCount = flight.NearbySpawnCandidates.Count;
                GUI.enabled = spawnCount > 0;
                if (GUILayout.Button(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Real Spawn Control ({0})", spawnCount)))
                {
                    spawnControlUI.IsOpen = !spawnControlUI.IsOpen;
                    ParsekLog.Verbose("UI",
                        string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "Real Spawn Control window toggled: {0}",
                            spawnControlUI.IsOpen ? "open" : "closed"));
                }
                GUI.enabled = true;
                GUILayout.Space(SpacingLarge);
            }

            if (GUILayout.Button("Timeline"))
            {
                timelineUI.IsOpen = !timelineUI.IsOpen;
                ParsekLog.Verbose("UI", $"Timeline window toggled: {(timelineUI.IsOpen ? "open" : "closed")}");
            }

            // Top-level Recordings button. The per-state count lives inside the
            // window; the launch-surface label stays short. The Missions view now
            // lives as a second tab inside this same window (no separate button).
            if (GUILayout.Button("Recordings"))
                ToggleRecordingsWindow();

            // --- Logistics (v0, available in both Flight and KSC) ---
            // Grouped with Timeline + Recordings as the primary navigation set.
            // The whole button tints red when any route is hard-broken (EndpointLost /
            // MissingSourceRecording / SourceChanged) so a problem is visible without
            // opening the window. The label is just "Logistics" (no live count); the
            // route count is still computed for the broken-tint diagnostic log only.
            IReadOnlyList<Route> logisticsRoutes = RouteStore.CommittedRoutes;
            int logisticsRouteCount = logisticsRoutes != null ? logisticsRoutes.Count : 0;
            bool anyLogisticsBroken = LogisticsButtonState.AnyRouteHardBroken(
                logisticsRoutes != null
                    ? logisticsRoutes.Select(r => r.Status)
                    : Enumerable.Empty<RouteStatus>());
            if (anyLogisticsBroken)
            {
                ParsekLog.VerboseRateLimited("UI", "logistics-button-broken-tint",
                    string.Format(CultureInfo.InvariantCulture,
                        "Logistics button broken-state tint applied (routeCount={0})",
                        logisticsRouteCount));
            }
            Color prevLogisticsColor = GUI.color;
            if (anyLogisticsBroken)
                GUI.color = new Color(0.95f, 0.45f, 0.45f);
            try
            {
                if (GUILayout.Button("Logistics"))
                {
                    logisticsUI.IsOpen = !logisticsUI.IsOpen;
                    ParsekLog.Verbose("UI",
                        $"Logistics window toggled: {(logisticsUI.IsOpen ? "open" : "closed")}");
                }
            }
            finally
            {
                GUI.color = prevLogisticsColor;
            }

            GUILayout.Space(SpacingLarge);

            // Keep top-level launch-surface labels short; detailed counts stay inside the window.
            if (GUILayout.Button(GetKerbalsMainButtonLabel()))
            {
                kerbalsUI.IsOpen = !kerbalsUI.IsOpen;
                ParsekLog.Verbose("UI", $"Kerbals window toggled: {(kerbalsUI.IsOpen ? "open" : "closed")}");
            }

            if (GUILayout.Button(GetCareerMainButtonLabel()))
            {
                careerStateUI.IsOpen = !careerStateUI.IsOpen;
                ParsekLog.Verbose("UI", $"Career window toggled: {(careerStateUI.IsOpen ? "open" : "closed")}");
            }

            GUILayout.Space(SpacingLarge);

            // --- Gloops Flight Recorder (InFlight-only; trailing separator before Settings) ---
            if (InFlight)
            {
                if (GUILayout.Button("Gloops Flight Recorder"))
                {
                    gloopsUI.IsOpen = !gloopsUI.IsOpen;
                    ParsekLog.Verbose("UI",
                        $"Gloops Flight Recorder window toggled: {(gloopsUI.IsOpen ? "open" : "closed")}");
                }
                GUILayout.Space(SpacingLarge);
            }

            // --- Settings ---
            if (GUILayout.Button("Settings"))
                ToggleSettingsWindow();

            // --- Version footer (version on the left, Close button fills the rest) ---
            GUILayout.Space(SpacingLarge);
            if (versionStyle == null)
            {
                versionStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 10,
                    alignment = TextAnchor.MiddleLeft,
                    normal = { textColor = new Color(1f, 1f, 1f, 0.4f) },
                    contentOffset = new Vector2(0f, 3f)
                };
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label(VersionLabel, versionStyle, GUILayout.ExpandWidth(false));
            GUILayout.Space(10f);
            if (GUILayout.Button("Close", GUILayout.ExpandWidth(true)))
            {
                ParsekLog.Verbose("UI", "Main window closed via button");
                CloseMainWindow?.Invoke();
            }
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();

            // Make window draggable
            GUI.DragWindow();
        }

        private void DrawFlightStatus()
        {
            GUILayout.Label("Status", GUI.skin.box);
            GUILayout.Label($"State: {GetStatusText()}");
            GUILayout.Label($"Recorded Points: {flight.recording.Count}");
            if (flight.recording.Count > 0)
            {
                double duration = flight.recording[flight.recording.Count - 1].ut - flight.recording[0].ut;
                GUILayout.Label($"Duration: {duration:F1}s");
            }
            GUILayout.Label($"Active Ghosts: {flight.TimelineGhostCount}");
        }

        // Recording controls moved to GloopsRecorderUI (Gloops Flight Recorder window)

        /// <summary>
        /// Call after each window's GUILayoutWindow to log position/size changes (rate-limited).
        /// </summary>
        internal void LogWindowPosition(string windowName, ref Rect lastRect, Rect currentRect)
        {
            if (lastRect.x != currentRect.x || lastRect.y != currentRect.y ||
                lastRect.width != currentRect.width || lastRect.height != currentRect.height)
            {
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                ParsekLog.VerboseRateLimited("UI", $"window.{windowName}",
                    $"{windowName} window position: x={currentRect.x.ToString("F0", ic)} y={currentRect.y.ToString("F0", ic)} " +
                    $"w={currentRect.width.ToString("F0", ic)} h={currentRect.height.ToString("F0", ic)}", 2.0);
                lastRect = currentRect;
            }
        }

        public void LogMainWindowPosition(Rect currentRect)
        {
            LogWindowPosition("Main", ref lastMainWindowRect, currentRect);
        }

        private void DrawCompactBudgetLine()
        {
            var budget = GetCachedBudget();

            if (budget.reservedFunds <= 0 && budget.reservedScience <= 0 && budget.reservedReputation <= 0)
                return;

            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var parts = new List<string>();
            if (budget.reservedFunds > 0)
                parts.Add(budget.reservedFunds.ToString("N0", ic) + " funds");
            if (budget.reservedScience > 0)
                parts.Add(budget.reservedScience.ToString("F1", ic) + " science");
            if (budget.reservedReputation > 0)
                parts.Add(budget.reservedReputation.ToString("F0", ic) + " reputation");

            if (parts.Count > 0)
            {
                GUILayout.Label("Reserved:");
                for (int i = 0; i < parts.Count; i++)
                    GUILayout.Label("  \u2022 " + parts[i]);
            }
        }

        public void DrawTimelineWindowIfOpen(Rect mainWindowRect)
        {
            timelineUI.DrawIfOpen(mainWindowRect);
        }

        public void DrawKerbalsWindowIfOpen(Rect mainWindowRect)
        {
            kerbalsUI.DrawIfOpen(mainWindowRect);
        }

        public void DrawCareerStateWindowIfOpen(Rect mainWindowRect)
        {
            careerStateUI.DrawIfOpen(mainWindowRect);
        }

        public void DrawLogisticsWindowIfOpen(Rect mainWindowRect)
        {
            logisticsUI.DrawIfOpen(mainWindowRect);
        }

        public void DrawStructureWindowIfOpen(Rect mainWindowRect)
        {
            structureListUI.DrawIfOpen(mainWindowRect);
        }

        /// <summary>Opens the structure-list window for a mission tree (Missions tab button).</summary>
        internal void OpenStructureWindowForMission(string treeId, string title)
        {
            structureListUI.OpenForMission(treeId, title);
        }

        /// <summary>Opens the structure-list window for a supply route (Logistics window button).</summary>
        internal void OpenStructureWindowForRoute(string routeId, string title)
        {
            structureListUI.OpenForRoute(routeId, title);
        }

        // ════════════════════════════════════════════════════════════════
        //  Recordings table (extracted to RecordingsTableUI)
        // ════════════════════════════════════════════════════════════════

        public void DrawRecordingsWindowIfOpen(Rect mainWindowRect)
        {
            recordingsTableUI.DrawIfOpen(mainWindowRect);
        }

        // The Missions view is drawn as a tab inside the Recordings window
        // (RecordingsTableUI dispatches to it); it is no longer a standalone window.
        internal MissionsWindowUI GetMissionsUI() => missionsUI;

        internal void ToggleRecordingsWindow()
        {
            recordingsTableUI.IsOpen = !recordingsTableUI.IsOpen;
            ParsekLog.Verbose("UI", $"Recordings window toggled: {(recordingsTableUI.IsOpen ? "open" : "closed")}");
        }

        internal void ToggleSettingsWindow()
        {
            settingsUI.IsOpen = !settingsUI.IsOpen;
            ParsekLog.Verbose("UI", $"Settings window toggled: {(settingsUI.IsOpen ? "open" : "closed")}");
        }

        // ════════════════════════════════════════════════════════════════
        //  Opaque window style (shared by all sub-windows)
        // ════════════════════════════════════════════════════════════════

        private bool EnsureOpaqueWindowStyle(GUISkin skin)
        {
            if (opaqueWindowStyle != null)
            {
                if (hasOpaqueWindowStyleScene
                    && opaqueWindowStyleScene == HighLogic.LoadedScene
                    && AreAllOpaqueStyleBackgroundsPresent(opaqueWindowStyle))
                    return true;

                ParsekLog.VerboseRateLimited("UI", "opaque-style-cache-stale",
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "ParsekUI: dropping cached opaque style before rebuild (built={0}, current={1})",
                        opaqueWindowStyleScene, HighLogic.LoadedScene), 1f);
                ResetCachedWindowStylesForSceneChange();
            }

            if (!IsOpaqueStyleSkinReady(skin))
            {
                ParsekLog.VerboseRateLimited("UI", "opaque-style-skin-not-ready",
                    "ParsekUI: skin not ready, deferring opaque style rebuild", 1f);
                return false;
            }

            opaqueWindowStyle = BuildOpaqueWindowStyleFromSource(skin.window);
            opaqueWindowStyleScene = HighLogic.LoadedScene;
            hasOpaqueWindowStyleScene = true;
            return true;
        }

        private static bool IsOpaqueStyleSkinReady(GUISkin skin)
        {
            return skin != null
                && skin.window != null
                && skin.window.normal.background != null;
        }

        private static bool AreAllOpaqueStyleBackgroundsPresent(GUIStyle style)
        {
            return style != null
                && style.normal.background != null
                && style.onNormal.background != null
                && style.focused.background != null
                && style.onFocused.background != null
                && style.active.background != null
                && style.onActive.background != null
                && style.hover.background != null
                && style.onHover.background != null;
        }

        internal static GUIStyle BuildOpaqueWindowStyleFromSource(GUIStyle sourceStyle)
        {
            Texture2D normalSource = sourceStyle.normal.background;
            var style = new GUIStyle(sourceStyle);
            style.normal.background = MakeOpaqueCopy(normalSource);
            style.onNormal.background = MakeOpaqueCopy(sourceStyle.onNormal.background ?? normalSource);
            style.focused.background = MakeOpaqueCopy(sourceStyle.focused.background ?? normalSource);
            style.onFocused.background = MakeOpaqueCopy(sourceStyle.onFocused.background ?? normalSource);
            style.active.background = MakeOpaqueCopy(sourceStyle.active.background ?? normalSource);
            style.onActive.background = MakeOpaqueCopy(sourceStyle.onActive.background ?? normalSource);
            style.hover.background = MakeOpaqueCopy(sourceStyle.hover.background ?? normalSource);
            style.onHover.background = MakeOpaqueCopy(sourceStyle.onHover.background ?? normalSource);
            NormalizeOpaqueWindowTitleTextColors(style, sourceStyle);

            // Bigger title font + more breathing room above/below the title text
            // so the title bar is not cramped. Applied here so every caller
            // (main UI, Test Runner, any future window using this builder) gets
            // the same title bar without having to remember to re-tweak.
            int baseFontSize = sourceStyle.fontSize;
            if (baseFontSize <= 0) baseFontSize = 12;       // GUI.skin.window often reports 0
            style.fontSize = baseFontSize + 2;
            var p = style.padding;
            style.padding = new RectOffset(p.left, p.right, p.top + 10, p.bottom + 4);

            return style;
        }

        internal static void NormalizeOpaqueWindowTitleTextColors(
            GUIStyle style,
            GUIStyle sourceStyle)
        {
            if (style == null || sourceStyle == null)
                return;

            Color baseTextColor = ResolveReadableWindowTitleTextColor(
                sourceStyle.normal.textColor,
                sourceStyle.onNormal.textColor);
            Color toggledTextColor = ResolveReadableWindowTitleTextColor(
                sourceStyle.onNormal.textColor,
                sourceStyle.normal.textColor);

            style.normal.textColor = baseTextColor;
            style.hover.textColor = baseTextColor;
            style.focused.textColor = baseTextColor;
            style.active.textColor = baseTextColor;

            style.onNormal.textColor = toggledTextColor;
            style.onHover.textColor = toggledTextColor;
            style.onFocused.textColor = toggledTextColor;
            style.onActive.textColor = toggledTextColor;
        }

        internal static void NormalizeOpaqueWindowTitleTextColors(
            Color sourceNormalTextColor,
            Color sourceOnNormalTextColor,
            ref Color normalTextColor,
            ref Color hoverTextColor,
            ref Color focusedTextColor,
            ref Color activeTextColor,
            ref Color onNormalTextColor,
            ref Color onHoverTextColor,
            ref Color onFocusedTextColor,
            ref Color onActiveTextColor)
        {
            Color baseTextColor = ResolveReadableWindowTitleTextColor(
                sourceNormalTextColor,
                sourceOnNormalTextColor);
            Color toggledTextColor = ResolveReadableWindowTitleTextColor(
                sourceOnNormalTextColor,
                sourceNormalTextColor);

            normalTextColor = baseTextColor;
            hoverTextColor = baseTextColor;
            focusedTextColor = baseTextColor;
            activeTextColor = baseTextColor;

            onNormalTextColor = toggledTextColor;
            onHoverTextColor = toggledTextColor;
            onFocusedTextColor = toggledTextColor;
            onActiveTextColor = toggledTextColor;
        }

        internal static Color ResolveReadableWindowTitleTextColor(
            Color preferred,
            Color fallback)
        {
            if (IsReadableWindowTitleTextColor(preferred))
                return preferred;
            if (IsReadableWindowTitleTextColor(fallback))
                return fallback;
            return Color.white;
        }

        internal static bool IsReadableWindowTitleTextColor(Color color)
        {
            if (color.a < 0.5f)
                return false;

            float luminance = 0.2126f * color.r
                + 0.7152f * color.g
                + 0.0722f * color.b;
            return luminance >= 0.55f;
        }

        private void ClearOpaqueWindowStyle()
        {
            if (opaqueWindowStyle == null)
                return;

            try
            {
                var destroyedBackgrounds = new HashSet<int>();
                DestroyOpaqueBackground(opaqueWindowStyle.normal.background, destroyedBackgrounds);
                DestroyOpaqueBackground(opaqueWindowStyle.onNormal.background, destroyedBackgrounds);
                DestroyOpaqueBackground(opaqueWindowStyle.focused.background, destroyedBackgrounds);
                DestroyOpaqueBackground(opaqueWindowStyle.onFocused.background, destroyedBackgrounds);
                DestroyOpaqueBackground(opaqueWindowStyle.active.background, destroyedBackgrounds);
                DestroyOpaqueBackground(opaqueWindowStyle.onActive.background, destroyedBackgrounds);
                DestroyOpaqueBackground(opaqueWindowStyle.hover.background, destroyedBackgrounds);
                DestroyOpaqueBackground(opaqueWindowStyle.onHover.background, destroyedBackgrounds);
            }
            catch (Exception ex)
            {
                if (!IsHeadlessUiObjectFailure(ex))
                    throw;
            }
            opaqueWindowStyle = null;
            hasOpaqueWindowStyleScene = false;
        }

        private void ResetCachedWindowStylesForSceneChange()
        {
            ClearOpaqueWindowStyle();
            sharedSectionHeaderStyle = null;
            sharedColumnHeaderStyle = null;
            versionStyle = null;
        }

        private static void DestroyOpaqueBackground(
            Texture2D background,
            HashSet<int> destroyedBackgrounds)
        {
            if (background == null)
                return;

            try
            {
                int id = background.GetInstanceID();
                if (!destroyedBackgrounds.Add(id))
                    return;

                UnityEngine.Object.Destroy(background);
            }
            catch (Exception ex)
            {
                if (!IsHeadlessUiObjectFailure(ex))
                    throw;
            }
        }

        private static bool IsHeadlessUiObjectFailure(Exception ex)
        {
            for (Exception current = ex; current != null; current = current.InnerException)
            {
                if (current is System.Security.SecurityException)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Creates a readable copy of a texture with all pixel alpha set to 1.
        /// Uses RenderTexture blit to handle non-readable source textures (KSP skin).
        /// </summary>
        private static Texture2D MakeOpaqueCopy(Texture2D source)
        {
            if (source == null) return null;

            // Blit to RenderTexture (works even for non-readable textures)
            RenderTexture rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
            RenderTexture prev = RenderTexture.active;
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;

            Texture2D copy = new Texture2D(source.width, source.height, TextureFormat.ARGB32, false);
            copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            // Max out alpha on all pixels
            Color[] pixels = copy.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
                pixels[i].a = 1f;
            copy.SetPixels(pixels);
            copy.Apply();
            copy.filterMode = source.filterMode;
            return copy;
        }

        /// <summary>
        /// Returns the opaque window style for use by external callers (ParsekFlight, ParsekKSC).
        /// </summary>
        public GUIStyle GetOpaqueWindowStyle()
        {
            if (!EnsureOpaqueWindowStyle(GUI.skin))
                return null;
            return opaqueWindowStyle;
        }

        internal static void ResetWindowGuiColors(
            out Color previousColor,
            out Color previousBackgroundColor,
            out Color previousContentColor)
        {
            previousColor = GUI.color;
            previousBackgroundColor = GUI.backgroundColor;
            previousContentColor = GUI.contentColor;
            GUI.color = Color.white;
            GUI.backgroundColor = Color.white;
            GUI.contentColor = Color.white;
        }

        internal static void RestoreWindowGuiColors(
            Color previousColor,
            Color previousBackgroundColor,
            Color previousContentColor)
        {
            GUI.color = previousColor;
            GUI.backgroundColor = previousBackgroundColor;
            GUI.contentColor = previousContentColor;
        }

        internal static T RunWithNormalizedWindowGuiColors<T>(Func<T> callback)
        {
            ResetWindowGuiColors(out Color prevColor, out Color prevBackgroundColor, out Color prevContentColor);
            try
            {
                return callback();
            }
            finally
            {
                RestoreWindowGuiColors(prevColor, prevBackgroundColor, prevContentColor);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Shared header styles (section bars + column headers)
        // ════════════════════════════════════════════════════════════════

        private void EnsureSharedHeaderStyles()
        {
            if (sharedSectionHeaderStyle != null && sharedColumnHeaderStyle != null) return;

            // Section header - bold label in a box, left-aligned, stretches full width.
            sharedSectionHeaderStyle = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleLeft,
                fontStyle = FontStyle.Bold,
                stretchWidth = true
            };

            // Column header - bold label in a box with slightly-lighter textColor.
            sharedColumnHeaderStyle = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleLeft,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f) }
            };
        }

        /// <summary>
        /// Shared section-header style: bold label in a box, stretches full width.
        /// Must be called during draw (requires a valid GUI.skin).
        /// </summary>
        public GUIStyle GetSectionHeaderStyle()
        {
            EnsureSharedHeaderStyles();
            return sharedSectionHeaderStyle;
        }

        /// <summary>
        /// Shared column-header style: bold label in a box with slightly-lighter textColor.
        /// Must be called during draw (requires a valid GUI.skin).
        /// </summary>
        public GUIStyle GetColumnHeaderStyle()
        {
            EnsureSharedHeaderStyles();
            return sharedColumnHeaderStyle;
        }

        // ============================================================
        //  Shared status-text color palette (L4)
        // ============================================================

        /// <summary>
        /// The canonical status-text color slots shared across windows that color
        /// status labels (the house palette). Windows keep their own GUIStyle objects
        /// (padding / wordWrap differ) but pull the text color from one source so the
        /// literals live in a single place.
        /// </summary>
        public enum StatusColorKind
        {
            /// <summary>Good / delivering / active (0.55, 1, 0.55).</summary>
            Green = 0,

            /// <summary>Caution / flying-but-not-delivering (1, 1, 0.4).</summary>
            Yellow = 1,

            /// <summary>Broken / hard error (1, 0.4, 0.4).</summary>
            Red = 2,

            /// <summary>Inert / paused (0.7, 0.7, 0.7).</summary>
            Grey = 3,

            /// <summary>New / informational / eligible (0.65, 0.85, 1).</summary>
            Cyan = 4,
        }

        /// <summary>
        /// The single source of truth for the house status-text palette. Pure (no GUI
        /// side effects), so it is unit-testable directly and both windows resolve the
        /// same RGBA. Values are taken verbatim from the prior Logistics palette so no
        /// rendered color changes when the windows route through here.
        /// </summary>
        internal static Color StatusColor(StatusColorKind kind)
        {
            switch (kind)
            {
                case StatusColorKind.Green:
                    return new Color(0.55f, 1f, 0.55f);
                case StatusColorKind.Yellow:
                    return new Color(1f, 1f, 0.4f);
                case StatusColorKind.Red:
                    return new Color(1f, 0.4f, 0.4f);
                case StatusColorKind.Cyan:
                    return new Color(0.65f, 0.85f, 1f);
                case StatusColorKind.Grey:
                default:
                    return new Color(0.7f, 0.7f, 0.7f);
            }
        }

        /// <summary>
        /// Public accessor for the shared status-text palette. Windows assign the
        /// returned color to their own GUIStyle's <c>normal.textColor</c> during draw.
        /// </summary>
        public Color GetStatusColor(StatusColorKind kind)
        {
            return StatusColor(kind);
        }

        // ════════════════════════════════════════════════════════════════
        //  Wipe confirmations (kept on ParsekUI — called by SettingsWindowUI)
        // ════════════════════════════════════════════════════════════════

        internal void ShowWipeRecordingsConfirmation(int count)
        {
            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekWipeRecordingsConfirm",
                    $"Delete all {count} recording(s) and their files?\n\nThis cannot be undone.",
                    "Confirm: Wipe Recordings",
                    HighLogic.UISkin,
                    new DialogGUIButton("Wipe All", () =>
                    {
                        // [ERS-exempt] reason: wholesale wipe of every stored
                        // recording (including NotCommitted / superseded) must
                        // unreserve every snapshot's crew — ERS would skip hidden
                        // entries and leak crew reservations.
                        foreach (var rec in RecordingStore.CommittedRecordings)
                            CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);
                        CrewReservationManager.ClearReplacements();
                        if (InFlight) flight.DestroyAllTimelineGhosts();
                        RecordingStore.ClearCommitted();
                        GameStateStore.ClearScienceSubjects();
                        ParsekLog.Info("UI", "All recordings wiped");
                        ParsekLog.ScreenMessage("All recordings wiped", 2f);
                    }),
                    new DialogGUIButton("Cancel", () =>
                    {
                        ParsekLog.Verbose("UI", "Wipe recordings cancelled");
                    })
                ),
                false, HighLogic.UISkin);
        }

        internal void ShowWipeActionsConfirmation(int count)
        {
            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekWipeActionsConfirm",
                    $"Delete all {count} game action milestone(s)?\n\nThis cannot be undone.",
                    "Confirm: Wipe Game Actions",
                    HighLogic.UISkin,
                    new DialogGUIButton("Wipe All", () =>
                    {
                        MilestoneStore.ClearAll();
                        ParsekLog.Info("UI", "All game actions wiped");
                        ParsekLog.ScreenMessage("All game actions wiped", 2f);
                    }),
                    new DialogGUIButton("Cancel", () =>
                    {
                        ParsekLog.Verbose("UI", "Wipe game actions cancelled");
                    })
                ),
                false, HighLogic.UISkin);
        }

        // ════════════════════════════════════════════════════════════════
        //  Sortable header helper (shared by RecordingsTableUI + SpawnControlUI)
        // ════════════════════════════════════════════════════════════════

        internal void DrawSortableHeaderCore<TCol>(
            string label, TCol col, ref TCol currentCol, ref bool ascending,
            float width, bool expand, Action onChanged, float height = 0f)
            where TCol : struct
        {
            string arrow = EqualityComparer<TCol>.Default.Equals(currentCol, col)
                ? (ascending ? " \u25b2" : " \u25bc") : "";
            // Reuse the shared column-header style so clickable sort headers match the
            // static column headers in the same row — otherwise the header row looks
            // half bold-boxed and half plain.
            GUIStyle headerStyle = GetColumnHeaderStyle();
            bool clicked;
            if (expand)
                clicked = height > 0f
                    ? GUILayout.Button(label + arrow, headerStyle, GUILayout.ExpandWidth(true), GUILayout.Height(height))
                    : GUILayout.Button(label + arrow, headerStyle, GUILayout.ExpandWidth(true));
            else
                clicked = height > 0f
                    ? GUILayout.Button(label + arrow, headerStyle, GUILayout.Width(width), GUILayout.Height(height))
                    : GUILayout.Button(label + arrow, headerStyle, GUILayout.Width(width));

            if (clicked)
            {
                if (EqualityComparer<TCol>.Default.Equals(currentCol, col))
                    ascending = !ascending;
                else
                {
                    currentCol = col;
                    ascending = true;
                }
                onChanged?.Invoke();
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Loop time unit helpers (shared — used by SettingsWindowUI + RecordingsTableUI)
        // ════════════════════════════════════════════════════════════════

        internal static string UnitLabel(LoopTimeUnit unit)
            => unit == LoopTimeUnit.Min ? "min"
             : unit == LoopTimeUnit.Hour ? "hr"
             : unit == LoopTimeUnit.Auto ? "auto"
             : "sec";

        internal static LoopTimeUnit CycleDisplayUnit(LoopTimeUnit u)
            => u == LoopTimeUnit.Sec ? LoopTimeUnit.Min
             : u == LoopTimeUnit.Min ? LoopTimeUnit.Hour
             : LoopTimeUnit.Sec;

        // Auto is resolved separately by callers; falls through to seconds if called directly.
        internal static double ConvertFromSeconds(double seconds, LoopTimeUnit unit)
            => unit == LoopTimeUnit.Min ? seconds / 60.0
             : unit == LoopTimeUnit.Hour ? seconds / 3600.0
             : seconds;

        // Auto is resolved separately by callers; falls through to seconds if called directly.
        internal static double ConvertToSeconds(double value, LoopTimeUnit unit)
            => unit == LoopTimeUnit.Min ? value * 60.0
             : unit == LoopTimeUnit.Hour ? value * 3600.0
             : value;

        internal static string FormatLoopValue(double value, LoopTimeUnit unit)
        {
            if (unit == LoopTimeUnit.Min || unit == LoopTimeUnit.Hour)
                return value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            return ((long)System.Math.Truncate(value)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static bool TryParseLoopInput(string text, LoopTimeUnit unit, out double value)
        {
            if (unit == LoopTimeUnit.Min || unit == LoopTimeUnit.Hour)
                return double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out value);
            int intVal;
            bool ok = int.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out intVal);
            value = intVal;
            return ok;
        }

        // ════════════════════════════════════════════════════════════════
        //  Forwarding methods for backward compatibility with tests
        //  (canonical implementations are on RecordingsTableUI)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Forwards to RecordingsTableUI.BuildGroupTreeData for backward compatibility with tests.
        /// </summary>
        internal static void BuildGroupTreeData(
            IReadOnlyList<Recording> committed, int[] sortedIndices,
            List<string> KnownEmptyGroups,
            out Dictionary<string, List<int>> grpToRecs,
            out Dictionary<string, List<int>> chainToRecs,
            out Dictionary<string, List<string>> grpChildren,
            out List<string> rootGrps,
            out HashSet<string> rootChainIds)
        {
            RecordingsTableUI.BuildGroupTreeData(committed, sortedIndices, KnownEmptyGroups,
                out grpToRecs, out chainToRecs, out grpChildren, out rootGrps, out rootChainIds);
        }

        internal enum SortColumn { Index, Phase, Name, LaunchTime, Duration, Status, LaunchSite }

        internal static int GetStatusOrder(Recording rec, double now)
            => RecordingsTableUI.GetStatusOrder(rec, now);

        internal static double GetRecordingSortKey(Recording rec, SortColumn column, double now, int rowFallback)
            => RecordingsTableUI.GetRecordingSortKey(rec, (RecordingsTableUI.SortColumn)(int)column, now, rowFallback);

        internal static double GetChainSortKey(List<int> members, IReadOnlyList<Recording> committed,
            SortColumn column, double now)
            => RecordingsTableUI.GetChainSortKey(members, committed, (RecordingsTableUI.SortColumn)(int)column, now);

        internal static int CompareRecordings(
            Recording ra, Recording rb,
            SortColumn column, bool ascending, double now)
            => RecordingsTableUI.CompareRecordings(ra, rb, (RecordingsTableUI.SortColumn)(int)column, ascending, now);

        internal static int[] BuildSortedIndices(
            IReadOnlyList<Recording> committed, SortColumn column, bool ascending, double now)
            => RecordingsTableUI.BuildSortedIndices(committed, (RecordingsTableUI.SortColumn)(int)column, ascending, now);

        internal static string FormatDuration(double seconds)
            => RecordingsTableUI.FormatDuration(seconds);

        internal static string FormatAltitude(double meters)
            => RecordingsTableUI.FormatAltitude(meters);

        internal static string FormatSpeed(double mps)
            => RecordingsTableUI.FormatSpeed(mps);

        internal static string FormatDistance(double meters)
            => RecordingsTableUI.FormatDistance(meters);

        internal static double GetGroupEarliestStartUT(HashSet<int> descendants, IReadOnlyList<Recording> committed)
            => RecordingsTableUI.GetGroupEarliestStartUT(descendants, committed);

        internal static double GetGroupTotalDuration(HashSet<int> descendants, IReadOnlyList<Recording> committed)
            => RecordingsTableUI.GetGroupTotalDuration(descendants, committed);

        internal static int FindGroupMainRecordingIndex(
            HashSet<int> descendants, IReadOnlyList<Recording> committed)
            => RecordingsTableUI.FindGroupMainRecordingIndex(descendants, committed);

        internal static void GetGroupStatus(HashSet<int> descendants, IReadOnlyList<Recording> committed,
            double now, out string statusText, out int statusOrder)
            => RecordingsTableUI.GetGroupStatus(descendants, committed, now, out statusText, out statusOrder);

        internal static double GetGroupSortKey(HashSet<int> descendants, IReadOnlyList<Recording> committed,
            SortColumn column, double now)
            => RecordingsTableUI.GetGroupSortKey(descendants, committed, (RecordingsTableUI.SortColumn)(int)column, now);

        internal static string UnitSuffix(LoopTimeUnit unit)
            => RecordingsTableUI.UnitSuffix(unit);

        internal static LoopTimeUnit CycleRecordingUnit(LoopTimeUnit u)
            => RecordingsTableUI.CycleRecordingUnit(u);

        internal static void ApplyAutoLoopRange(Recording rec, bool loopEnabled)
            => RecordingsTableUI.ApplyAutoLoopRange(rec, loopEnabled);

        // ════════════════════════════════════════════════════════════════
        //  Settings / Spawn Control / Test Runner (extracted)
        // ════════════════════════════════════════════════════════════════

        public void DrawSettingsWindowIfOpen(Rect mainWindowRect)
        {
            settingsUI.DrawIfOpen(mainWindowRect);
        }

        public void DrawSpawnControlWindowIfOpen(Rect mainWindowRect)
        {
            spawnControlUI.DrawIfOpen(mainWindowRect, flight, InFlight);
        }

        public void DrawGloopsRecorderWindowIfOpen(Rect mainWindowRect)
        {
            if (InFlight && flight != null)
                gloopsUI.DrawIfOpen(mainWindowRect, flight);
        }

        public void DrawTestRunnerWindowIfOpen(Rect mainWindowRect, MonoBehaviour host)
        {
            testRunnerUI.DrawIfOpen(mainWindowRect, host);
        }

        internal bool IsMouseOverOpenAuxiliaryWindows(Vector2 mousePosition)
        {
            return recordingsTableUI.IsMouseOverOpenWindow(mousePosition)
                || settingsUI.IsMouseOverOpenWindow(mousePosition)
                || testRunnerUI.IsMouseOverOpenWindow(mousePosition);
        }

        internal static bool IsPointerOverOpenWindow(bool isOpen, Rect windowRect, Vector2 mousePosition)
        {
            return isOpen
                && windowRect.width > 0f
                && windowRect.height > 0f
                && windowRect.Contains(mousePosition);
        }

        internal void ToggleTestRunner()
        {
            testRunnerUI.IsOpen = !testRunnerUI.IsOpen;
            ParsekLog.Verbose("UI", $"Test runner window toggled: {(testRunnerUI.IsOpen ? "open" : "closed")}");
        }

        // ════════════════════════════════════════════════════════════════
        //  Map markers
        // ════════════════════════════════════════════════════════════════

        internal enum MapMarkerPositionFailureReason
        {
            None,
            BadRecordingIndex,
            NoTrajectoryPoints,
            OutsideTimeRange,
            MissingBody
        }

        internal struct MapMarkerSummary
        {
            public bool IsMapView;
            public int Candidates;
            public int PreviewDrawn;
            public int Drawn;
            public int CameraUnavailable;
            public int HiddenInFlight;
            public int NativeIcon;
            public int Debris;
            public int ChainNonTip;
            public int PositionFailure;
            public int MissingBody;
            // A looped-mission member whose shared span-clock has it OUTSIDE its render window this
            // frame (inter-cycle tail / not yet / already past): the engine hides the mesh too, so
            // the custom marker is intentionally skipped (distinct from a position failure).
            public int LoopHidden;

            internal bool HasSignal =>
                Candidates > 0
                || PreviewDrawn > 0
                || Drawn > 0
                || CameraUnavailable > 0
                || HiddenInFlight > 0
                || NativeIcon > 0
                || Debris > 0
                || ChainNonTip > 0
                || PositionFailure > 0
                || MissingBody > 0
                || LoopHidden > 0;
        }

        internal static string FormatMapMarkerSummary(MapMarkerSummary summary)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Map marker summary: view={0} candidates={1} previewDrawn={2} drawn={3} cameraUnavailable={4} hiddenInFlight={5} nativeIcon={6} debris={7} chainNonTip={8} positionFailure={9} missingBody={10} loopHidden={11}",
                summary.IsMapView ? "map" : "flight",
                summary.Candidates,
                summary.PreviewDrawn,
                summary.Drawn,
                summary.CameraUnavailable,
                summary.HiddenInFlight,
                summary.NativeIcon,
                summary.Debris,
                summary.ChainNonTip,
                summary.PositionFailure,
                summary.MissingBody,
                summary.LoopHidden);
        }

        private static void LogMapMarkerSummary(MapMarkerSummary summary)
        {
            if (!summary.HasSignal)
                return;

            ParsekLog.VerboseRateLimited("UI",
                "map-marker-summary",
                FormatMapMarkerSummary(summary),
                2.0);
        }

        public void DrawMapMarkers()
        {
            bool isMapView = MapView.MapIsEnabled;
            var summary = new MapMarkerSummary { IsMapView = isMapView };

            // Resolve camera for current view; bail if unavailable
            if (isMapView)
            {
                if (PlanetariumCamera.Camera == null)
                {
                    summary.CameraUnavailable++;
                    LogMapMarkerSummary(summary);
                    return;
                }
            }
            else
            {
                if (FlightCamera.fetch == null || FlightCamera.fetch.mainCamera == null)
                {
                    summary.CameraUnavailable++;
                    LogMapMarkerSummary(summary);
                    return;
                }
            }

            // Manual preview ghost. Uses a fixed marker key so hover/sticky state is
            // consistent across frames, and a "Preview" display label.
            if (flight.IsPlaying && flight.PreviewGhost != null)
            {
                DrawMapMarkerAt(flight.PreviewGhost.transform.position, "preview", "Preview",
                    new Color(0.2f, 1f, 0.4f, 0.9f));
                summary.PreviewDrawn++;
            }

            // Timeline ghosts — skip if a ghost map ProtoVessel exists for this index
            // (the native KSP vessel icon replaces this marker and tracks the correct orbital position).
            // Deduplicate per chain: during warp, multiple chain segments can be active
            // simultaneously. Only draw the marker for the highest-index (latest) ghost per chain.
            // [ERS-exempt] reason: ghostStates dict is keyed by CommittedRecordings
            // index; markers must be looked up by the same index space. Converting
            // would mis-align the index -> recording mapping.
            // TODO(phase 6+): migrate ghostStates to recording-id keyed storage.
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null)
            {
                LogMapMarkerSummary(summary);
                return;
            }

            // First pass: find the highest active index per chain
            chainTipIndexBuffer.Clear();
            drawnChainMarkerBuffer.Clear();
            foreach (var kvp in flight.Engine.ghostStates)
            {
                if (kvp.Value == null) continue;
                if (kvp.Key >= committed.Count) continue;
                string chainId = committed[kvp.Key].ChainId;
                if (string.IsNullOrEmpty(chainId)) continue;
                int existing;
                if (!chainTipIndexBuffer.TryGetValue(chainId, out existing) || kvp.Key > existing)
                    chainTipIndexBuffer[chainId] = kvp.Key;
            }

            // Second pass: draw markers, skipping non-tip chain members and debris.
            // In flight view, skip hidden ghosts (#245/#247): their transform.position is stale
            // after FloatingOrigin shifts and projects to wrong map locations.
            // In map view, draw ALL active ghosts — use trajectory-derived positions when the
            // mesh is hidden by zone distance so every ghost is visible on the map.
            double currentUT = isMapView ? Planetarium.GetUniversalTime() : 0;

            // Marker-decision tracer timestamp: currentUT is 0 in flight view (only map view needs
            // it for positioning), so use the live UT for the trace line so it is meaningful in
            // both views. Only read when tracing is enabled.
            double traceUT = MapRenderTrace.IsEnabled ? Planetarium.GetUniversalTime() : 0.0;

            foreach (var kvp in flight.Engine.ghostStates)
            {
                var state = kvp.Value;
                if (state == null) continue;
                summary.Candidates++;
                bool meshActive = state.ghost != null && state.ghost.activeSelf;

                // ---- Marker-decision tracer state (per-pid, change-based; gated, off in normal play) ----
                // Carries the four ResolveMarkerDrawDecision disjuncts (resolved only at the proto-gate
                // branch below; false elsewhere because no proto vessel exists / the gate was not
                // consulted), the resolved shouldDrawNonProto bool, the terminal outcome, and (for a
                // drawn non-proto marker) the polyline ride reason + the fallback position source. A
                // single change-based line per ghost is emitted via EmitMarkerDecision() at each exit.
                bool decDirectorTraced = false, decPolylineOwning = false,
                    decIconSuppressed = false, decShouldDraw = false;
                var decOutcome = MapRenderTrace.MarkerOutcome.Unknown;
                var decRideReason = MapRenderTrace.MarkerRideReason.NotAttempted;
                int decRideLeg = -1;
                string decPosSource = "?";
                string traceRecId = kvp.Key < committed.Count ? committed[kvp.Key].RecordingId : null;
                string traceVessel = kvp.Key < committed.Count ? committed[kvp.Key].VesselName : "Ghost";
                void EmitMarkerDecision()
                {
                    if (!MapRenderTrace.IsEnabled || string.IsNullOrEmpty(traceRecId))
                        return;
                    MapRenderTrace.EmitMarkerDecisionOnChange(
                        MapRenderTrace.RenderSurface.ImguiLabeledMarker, traceRecId, traceUT,
                        MapRenderTrace.BuildMarkerDecisionSignature(
                            kvp.Key, traceVessel, decDirectorTraced, decPolylineOwning,
                            decIconSuppressed, decShouldDraw, decOutcome, decRideReason, decRideLeg,
                            decPosSource));
                }

                // In flight view, skip hidden ghosts (stale positions cause wrong markers #245/#247)
                if (!meshActive && !isMapView)
                {
                    summary.HiddenInFlight++;
                    decOutcome = MapRenderTrace.MarkerOutcome.SkippedNotOnMap;
                    EmitMarkerDecision();
                    continue;
                }

                // ---- Slice (iii): per-instance overlap markers riding the ONE shared polyline ----
                // For an OVERLAPPING looped mission rendered via the trajectory polyline (the
                // maintainer's sub-2km hop with ZERO orbit), draw N markers - one per live overlap
                // instance - each at its own ComputeOverlapCyclePlaybackUT head along the single shared
                // polyline, so the map matches the N flight ghosts. The polyline GEOMETRY is untouched
                // (it draws ONCE keyed by RecordingId); this only adds N markers riding it. Gated behind
                // ShouldDriveOverlapPerInstance (inside TryGetLiveOverlapHeadUTs): non-overlap / gate-off
                // returns false and falls through to the UNCHANGED 8c proto gate + single-marker tail
                // below, byte-identically.
                //
                // HOISTED above the 8c proto gate (which consults ONLY the newest instance's pid) and
                // the chain-non-tip gate so a MIXED overlap recording - newest cycle mid-orbit (visible
                // proto icon) while OLDER cycles are simultaneously in a non-orbital reentry/descent
                // phase - does NOT get pre-empted by the newest-only `continue` and silently drop the
                // older instances' polyline markers. Safe to hoist because the PER-CYCLE no-double rule
                // inside DrawOneOverlapInstanceMarker (TryGetOverlapInstancePidForCycle +
                // ShouldDrawNonProtoMarkerForGhost(instancePid)) makes the correct per-cycle
                // orbital-vs-polyline decision: a cycle whose proto icon is visible is skipped (the proto
                // draws it), a cycle that is suborbital/suppressed draws its polyline marker. So moving
                // the whole branch up cannot double-draw and closes the dropped-marker gap. Debris is
                // still skipped here (overlap recordings are top-level mission members, but guard anyway
                // to preserve the IsDebris filter the 8c block below applied).
                //
                // Map-view only: the per-instance heads ride the polyline scratchScaledSpace (filled at
                // onPreCull) and resolve world positions via the map's scaled-space anchoring; in flight
                // view currentUT is 0 and there is no shared map polyline to ride, so overlap markers
                // stay on the single-instance (newest) path below.
                if (isMapView && kvp.Key < committed.Count
                    && !committed[kvp.Key].IsDebris
                    && GhostMapPresence.TryGetLiveOverlapHeadUTs(
                        committed[kvp.Key], kvp.Key, committed,
                        flight.Engine.CurrentLoopUnits, currentUT, overlapHeadUtBuffer))
                {
                    int instancesDrawn = 0;
                    for (int hi = 0; hi < overlapHeadUtBuffer.Count; hi++)
                    {
                        var (cycle, headUT) = overlapHeadUtBuffer[hi];
                        if (DrawOneOverlapInstanceMarker(kvp.Key, committed, headUT, cycle))
                            instancesDrawn++;
                    }
                    summary.Drawn += instancesDrawn;

                    decOutcome = MapRenderTrace.MarkerOutcome.DrawnNonProto;
                    EmitMarkerDecision();

                    // A single rate-limited summary per overlap recording (per the batch-counting
                    // convention): how many of the N live cycles actually drew a polyline marker this
                    // frame (an instance skips when its head is out-of-window / between legs / off-line,
                    // or when its cycle is currently drawn by a live non-suppressed proto icon).
                    int recIdxForOverlapLog = kvp.Key;
                    int liveCyclesForLog = overlapHeadUtBuffer.Count;
                    int drawnForLog = instancesDrawn;
                    string overlapNameForLog = committed[kvp.Key].VesselName;
                    ParsekLog.VerboseRateLimited("GhostMap",
                        "overlap-instance-markers-"
                            + recIdxForOverlapLog.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        () => string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "Overlap per-instance markers: rec={0} vessel={1} liveCycles={2} drawn={3}",
                            recIdxForOverlapLog, overlapNameForLog ?? "Ghost", liveCyclesForLog, drawnForLog),
                        2.0);
                    continue; // per-instance markers drew (or all skipped) — skip the 8c gate + tail
                }

                // Skip if native KSP icon is active (ProtoVessel exists and icon not suppressed).
                // When the Harmony patch suppresses the icon (below atmosphere), we draw
                // our custom marker at the ghost mesh position instead.
                if (GhostMapPresence.HasGhostVesselForRecording(kvp.Key))
                {
                    uint ghostPid = GhostMapPresence.GetGhostVesselPidForRecording(kvp.Key);
                    // Skip our marker only when the native proto icon is actually
                    // visible. The marker-draw decision (proto hidden -> draw our
                    // marker) is owned by GhostMapPresence.ShouldDrawNonProtoMarkerForGhost
                    // (Phase 8c): gate ON the Director's TracedPath decision +
                    // polyline-owns (8b.2 actual-draw) are authoritative, with the legacy
                    // icon-suppressed flag (below-atmosphere clamp / off-arc / Director
                    // no-bounds transient) kept as the fallback; gate OFF it is the
                    // legacy IsIconSuppressed || IsPolylineOwningGhostPhase predicate,
                    // byte-identical. In the polyline / suppressed case our marker is the
                    // sole position indicator, so it must draw - otherwise an airless
                    // descent (e.g. the Mun) shows the polyline with no ghost icon.
                    //
                    // Tracer: resolve the decision through the diagnostics overload to surface the
                    // four disjuncts (behavior-identical to the parameterless overload). ghostPid==0
                    // means no resolvable proto pid -> proto icon assumed active, decShouldDraw stays
                    // false.
                    bool decision;
                    if (ghostPid == 0)
                    {
                        decision = false;
                    }
                    else
                    {
                        decision = GhostMapPresence.ShouldDrawNonProtoMarkerForGhost(
                            ghostPid, out decDirectorTraced,
                            out decPolylineOwning, out decIconSuppressed);
                    }
                    decShouldDraw = decision;
                    if (ghostPid == 0 || !decision)
                    {
                        summary.NativeIcon++;
                        decOutcome = MapRenderTrace.MarkerOutcome.DrawnProtoIcon;
                        EmitMarkerDecision();
                        continue; // native icon is active — skip our marker
                    }
                }
                if (kvp.Key < committed.Count)
                {
                    if (committed[kvp.Key].IsDebris)
                    {
                        summary.Debris++;
                        decOutcome = MapRenderTrace.MarkerOutcome.SkippedDebris;
                        EmitMarkerDecision();
                        continue;
                    }
                    string chainId = committed[kvp.Key].ChainId;
                    if (!string.IsNullOrEmpty(chainId) && chainTipIndexBuffer.Count > 0
                        && chainTipIndexBuffer.TryGetValue(chainId, out int tip) && kvp.Key != tip)
                    {
                        summary.ChainNonTip++;
                        decOutcome = MapRenderTrace.MarkerOutcome.SkippedChainNonTip;
                        EmitMarkerDecision();
                        continue; // not the tip — skip duplicate
                    }
                }

                // Resolve marker world position: use ghost mesh when it is actually POSITIONED, otherwise
                // compute from trajectory data (map view only — hidden ghosts need icons too).
                // A meshActive ghost whose transform sits at the world origin is stale / unpositioned: in map
                // view the engine drives a far ghost by its orbit/trajectory and leaves the hidden mesh
                // transform at (0,0,0) (the FloatingOrigin #245/#247 artifact), so reading it would pin the
                // labelled marker to the map centre (the "static yellow-label icon in the wrong location"
                // seam the playtest showed via Marker pos markerPos=(0,0,0)). Treat a near-origin transform
                // as unpositioned and fall through to the trajectory-derived position below; a genuinely
                // positioned mesh is never at the floating-origin centre (it is a different vessel).
                // During a non-orbital phase the trajectory polyline owns the rendering and the stock
                // proto orbit icon is hidden (GhostOrbitLinePatch drawIcons=NONE). The ghost mesh
                // transform stops being driven per-frame in that phase because the orbit-segment drive
                // path is not active and any state-vector reseed only refreshes the orbit at the orbit-
                // resolve cadence (~5s), so the mesh transform freezes at the last seeded position
                // between reseeds even as the recording's effUT advances. The labelled non-proto marker
                // would then ride that stale mesh and read as "frozen at the polyline's start" - the
                // exact playtest seam. During polyline ownership the recorded trajectory points are the
                // source of truth (the polyline draws them), so fall through to TryComputeGhostWorldPosition
                // at the loop-mapped effUT below. The diagnostic "Marker pos: ... meshVsTraj" makes this
                // explicit by reporting both sources.
                //
                // PolylineReleaseGraceSec extends the "polyline phase" through a short grace window
                // after the polyline releases ownership: the seg-drive dispatcher runs on a ~0.5s
                // cadence, so the next orbital segment is not applied on the same frame as release.
                // For ~12 frames in between, the mesh transform is still stale at the pre-polyline
                // position. Without the grace, the marker reads that stale mesh and snaps backward by
                // hundreds of km (the parking-orbit endpoint vs the loiter), which is what the playtest
                // reported as "icon teleported far away to the wrong position on the loiter". The grace
                // suppresses that stale read until the seg-drive catches up; trajPos / TryComputeGhost
                // WorldPosition is preferred when available, and the marker simply does not draw for
                // those frames when the recording has no points covering the post-cut effUT (gracefully
                // invisible for ~200ms rather than teleporting). End-of-grace lands on the freshly seg-
                // driven mesh transform and the marker resumes normally.
                uint ghostPidForPhase = GhostMapPresence.HasGhostVesselForRecording(kvp.Key)
                    ? GhostMapPresence.GetGhostVesselPidForRecording(kvp.Key)
                    : 0u;
                // Polyline ownership including the post-release grace - shared with GhostOrbitLinePatch.
                // The stamping happens in the orbit-line patch every frame the polyline owns; here we
                // just read. The grace duration matches Patches.GhostOrbitLinePatch.PolylineReleaseGraceSeconds.
                bool polylinePhase;
                if (ghostPidForPhase != 0)
                {
                    polylinePhase = GhostMapPresence.IsPolylineRecentlyOwningGhostPhase(
                        ghostPidForPhase,
                        Parsek.Patches.GhostOrbitLinePatch.PolylineReleaseGraceSeconds);
                }
                else
                {
                    // PID-less recording (a chain descent / atmospheric-only recording with NO proto ghost
                    // vessel and no OrbitSegments, e.g. the Duna re-aim descent member): there is no ghost PID
                    // to stamp polyline ownership against, so resolve ownership directly by RecordingId from
                    // the polyline Driver's per-frame publish. Without this the labeled marker for such a
                    // recording never rode its own drawn body-fixed line (polylinePhase stayed false) and the
                    // icon fell back to the body-fixed head, off the line. No post-release grace applies (there
                    // is no PID stamp), and TryAnchorMarkerToPolyline below still self-gates on the leg being
                    // drawn THIS frame, so the marker rides only while the line is actually drawn.
                    polylinePhase = kvp.Key < committed.Count
                        && committed[kvp.Key] != null
                        && Parsek.Display.GhostTrajectoryPolylineRenderer.IsRenderingNonOrbitalLeg(
                            committed[kvp.Key].RecordingId);
                }
                Vector3 markerPos;
                bool markerRidesPolyline = false;
                bool meshPositioned = meshActive && state.ghost.transform.position.sqrMagnitude > 1f
                    && !polylinePhase;
                if (meshPositioned)
                {
                    markerPos = state.ghost.transform.position;
                    decPosSource = "mesh";
                }
                else
                {
                    decPosSource = "traj";
                    // A looped-mission member's recorded trajectory points sit at the ORIGINAL
                    // (past) recorded UTs, so sampling at the LIVE UT is always OutsideTimeRange and
                    // the custom icon never draws (the engine positions the mesh at the shared
                    // span-clock effUT instead, but the mesh is hidden here by zone distance). Map
                    // the live UT to that same span-clock effUT - identity for a non-loop ghost or an
                    // Empty unit set, so non-loop behavior is unchanged - and skip the frame when the
                    // shared clock has this member outside its render window (renderHidden), matching
                    // the engine / proto-vessel paths.
                    double sampleUT = currentUT;
                    if (kvp.Key < committed.Count)
                    {
                        Recording recForUT = committed[kvp.Key];
                        double effUT = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                            kvp.Key, recForUT.StartUT, recForUT.EndUT, currentUT,
                            flight.Engine.CurrentLoopUnits, out bool loopHidden);
                        if (loopHidden)
                        {
                            summary.LoopHidden++;
                            decOutcome = MapRenderTrace.MarkerOutcome.SkippedLoopHidden;
                            EmitMarkerDecision();
                            continue;
                        }
                        sampleUT = effUT;
                    }

                    // M4b: derotate body-fixed marker sampling by the member's per-launch shift
                    // (0 for non-members / knob-less schedules).
                    double markerBodyFixedShift =
                        GhostPlaybackLogic.ComputeUnitMemberBodyFixedShiftSeconds(
                            kvp.Key, currentUT, sampleUT, flight.Engine.CurrentLoopUnits);

                    if (!TryComputeGhostWorldPosition(
                            kvp.Key,
                            committed,
                            sampleUT,
                            out markerPos,
                            out MapMarkerPositionFailureReason failureReason,
                            markerBodyFixedShift))
                    {
                        summary.PositionFailure++;
                        if (failureReason == MapMarkerPositionFailureReason.MissingBody)
                            summary.MissingBody++;
                        decOutcome = MapRenderTrace.MarkerOutcome.SkippedPositionFail;
                        EmitMarkerDecision();
                        continue;
                    }

                    // Ride the conic-anchored polyline: while the trajectory polyline owns this phase, the
                    // labeled marker (icon + label) sits ON the drawn burn line instead of the body-fixed
                    // head, which is ~96 deg off the loiter/hyperbola conics under the loop shift. Samples
                    // the same per-frame drawn points the line uses, so it matches exactly; a no-op outside
                    // an anchored leg or when the leg was not drawn this frame.
                    bool rideAttempted = polylinePhase && kvp.Key < committed.Count;
                    if (rideAttempted
                        && Parsek.Display.GhostTrajectoryPolylineRenderer.TryAnchorMarkerToPolyline(
                            committed[kvp.Key].RecordingId, sampleUT, out Vector3 onLineMarkerPos,
                            out decRideReason, out decRideLeg))
                    {
                        markerPos = onLineMarkerPos;
                        markerRidesPolyline = true;
                        decPosSource = "polyline";
                        if (ghostPidForPhase == 0)
                            ParsekLog.VerboseRateLimited(
                                "GhostMap",
                                "pidless-polyline-ride." + committed[kvp.Key].RecordingId,
                                "PID-less marker rides its own polyline: rec=" + kvp.Key
                                    + " recId=" + committed[kvp.Key].RecordingId);
                    }
                }

                string ghostName = kvp.Key < committed.Count ? committed[kvp.Key].VesselName : "Ghost";
                // Use RecordingId as marker key so hover/sticky state follows the recording,
                // not the array index (which may shuffle when recordings are added/removed).
                string markerKey = kvp.Key < committed.Count ? committed[kvp.Key].RecordingId : null;
                VesselType vtype = kvp.Key < committed.Count
                    ? GhostMapPresence.ResolveVesselType(committed[kvp.Key].VesselSnapshot)
                    : VesselType.Ship;
                Color markerColor = GetGhostMarkerColorForType(vtype);
                DrawMapMarkerAt(markerPos, markerKey, ghostName, markerColor, vtype);
                summary.Drawn++;
                // Review MINOR-1 dedup input: remember which CHAINS drew a labeled marker this frame
                // so the ghost-less fallback below never adds a second one for the same chain.
                if (kvp.Key < committed.Count
                    && !string.IsNullOrEmpty(committed[kvp.Key].ChainId))
                    drawnChainMarkerBuffer.Add(committed[kvp.Key].ChainId);

                // Tracer: the non-proto labeled marker drew. Emit the per-pid change-based decision
                // line carrying WHY (the disjuncts), the ride reason + leg, and the position source.
                decOutcome = MapRenderTrace.MarkerOutcome.DrawnNonProto;
                EmitMarkerDecision();

                // Comprehensive per-marker DRAW log (always available, rate-limited, NOT tracing-gated):
                // EVERY non-proto labelled marker logs WHERE it drew + its position SOURCE - mesh-ridden
                // or trajectory-derived (polyline phase). A "yellow label in the wrong place" - e.g. riding
                // the body-fixed escape-burn polyline during a burn - reads in one line: source=traj
                // polylinePhase=True with the drawn world position. Built via the lazy Func overload so the
                // string.Format + markerPos.ToString are paid only when the 2s rate-limit actually emits,
                // not every frame for every ghost. (The detailed mesh-vs-recorded-path gap is tracing-gated below.)
                int recIdxForLog = kvp.Key;
                bool meshPositionedForLog = meshPositioned;
                bool polylinePhaseForLog = polylinePhase;
                bool meshActiveForLog = meshActive;
                bool ridesPolylineForLog = markerRidesPolyline;
                VesselType vtypeForLog = vtype;
                Vector3 markerPosForLog = markerPos;
                string ghostNameForLog = ghostName;
                ParsekLog.VerboseRateLimited("GhostMap",
                    "marker-draw-" + recIdxForLog.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    () => string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Marker DRAWN: rec={0} vessel={1} source={2} polylinePhase={3} meshActive={4} " +
                        "meshPositioned={5} ridesPolyline={6} vtype={7} drawnPos={8}",
                        recIdxForLog, ghostNameForLog, meshPositionedForLog ? "mesh" : "traj", polylinePhaseForLog,
                        meshActiveForLog, meshPositionedForLog, ridesPolylineForLog, vtypeForLog,
                        markerPosForLog.ToString("F0")),
                    2.0);

                // MapRenderTrace IMGUI surface coverage (ImguiLabeledMarker). Decision-only: the
                // labeled marker draws here in OnGUI, so the projected world position IS the truth
                // (no end-of-frame reconciliation). Gated + rate-limited inside EmitMarker.
                if (MapRenderTrace.IsEnabled)
                    MapRenderTrace.EmitMarker(
                        MapRenderTrace.RenderSurface.ImguiLabeledMarker, markerKey, currentUT,
                        string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "vessel={0} markerPos={1} mapView={2} meshActive={3}",
                            ghostName, MapRenderTrace.FormatVector3(markerPos), isMapView, meshActive));

                // Diagnostic (loiter-seam icon debugging): when the marker rides the LIVE ghost mesh
                // (meshActive), compare the drawn mesh position against the trajectory-interpolated position
                // at the same span-clock head UT. Both are in the current world frame, so their gap
                // (meshVsTraj) is frame-independent: a large gap means the engine's mesh sits off the
                // recorded path (the "non-proto icon in the wrong location / not moving correctly on the
                // polyline" seam), while a ~0 gap means the icon is on the path and any apparent freeze is
                // just the sub-pixel parking circle at map scale. Rate-limited per recording.
                //
                // Gated behind the map-render tracer (off in normal play, so the per-frame
                // TryComputeGhostWorldPosition probe is skipped too) and emitted only when a
                // trajectory position exists to compare against: a NaN meshVsTraj (no trajPos)
                // carries no signal, so those frames are skipped rather than logged (they were
                // ~93% of this line's volume).
                if (MapRenderTrace.IsEnabled && meshActive && isMapView && kvp.Key < committed.Count)
                {
                    var recDiag = committed[kvp.Key];
                    double headUT = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                        kvp.Key, recDiag.StartUT, recDiag.EndUT, currentUT,
                        flight.Engine.CurrentLoopUnits, out bool _);
                    if (TryComputeGhostWorldPosition(
                            kvp.Key, committed, headUT, out Vector3 trajPos, out _))
                    {
                        Vector3 meshXform = state.ghost.transform.position;
                        double meshVsTraj = (meshXform - trajPos).magnitude;
                        ParsekLog.VerboseRateLimited("GhostMap",
                            "marker-pos-" + kvp.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "Marker pos: rec={0} meshPositioned={1} headUT={2:F1} drawnPos={3} meshXform={4} trajPos={5} meshVsTraj={6:F0}",
                                kvp.Key, meshPositioned, headUT, markerPos.ToString("F0"),
                                meshXform.ToString("F0"), trajPos.ToString("F0"), meshVsTraj),
                            2.0);
                    }
                }
            }

            // Playtest-13 (map view): a chain ghost destroyed MID-PHASE - the engine retires it at the
            // below-surface Duna descent boundary ('chain-loop unit member outside its window') - leaves
            // the recording with NO ghostStates entry, so the pid-keyed walk above never considers it and
            // the icon vanished on the landing chord while the polyline kept drawing it. Recording-keyed
            // fallback (the TS atmospheric-marker equivalent): any committed recording that the walk
            // above did NOT cover and whose polyline OWNS the current phase (exactly one per chain - the
            // head-gated active member) draws its labeled marker riding the drawn line. The ride
            // self-gates on the leg having drawn this frame (or the short HeldLastGood pan-hold of a
            // position that was on the drawn line moments earlier), so an undrawn phase can never
            // paint a marker.
            if (isMapView)
            {
                for (int ri = 0; ri < committed.Count; ri++)
                {
                    var rec = committed[ri];
                    if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) continue;
                    if (flight.Engine.ghostStates.TryGetValue(ri, out var coveredState)
                        && coveredState != null)
                        continue; // covered (drawn or intentionally skipped) by the pid-keyed walk
                    if (!Parsek.Display.GhostTrajectoryPolylineRenderer.IsRenderingNonOrbitalLeg(
                            rec.RecordingId))
                        continue;
                    // Review MINOR-1: if this chain already drew a LABELED marker via the pid-keyed
                    // walk (warp handoff: a sibling member still holds a live ghost state), skip -
                    // never two labeled markers for one chain. A chain whose live sibling showed only
                    // its stock vessel icon still gets the playing head marked here.
                    if (!string.IsNullOrEmpty(rec.ChainId)
                        && drawnChainMarkerBuffer.Contains(rec.ChainId))
                        continue;
                    double effUT = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                        ri, rec.StartUT, rec.EndUT, currentUT,
                        flight.Engine.CurrentLoopUnits, out bool ghostlessHidden);
                    if (ghostlessHidden) continue;
                    if (!Parsek.Display.GhostTrajectoryPolylineRenderer.TryAnchorMarkerToPolyline(
                            rec.RecordingId, effUT, out Vector3 onLinePos))
                        continue;
                    VesselType ghostlessType = GhostMapPresence.ResolveVesselType(rec.VesselSnapshot);
                    DrawMapMarkerAt(
                        onLinePos, rec.RecordingId, rec.VesselName ?? "Ghost",
                        GetGhostMarkerColorForType(ghostlessType), ghostlessType);
                    summary.Drawn++;
                    ParsekLog.VerboseRateLimited("GhostMap",
                        "ghostless-polyline-marker." + rec.RecordingId,
                        string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "Ghost-less polyline marker: rec={0} recId={1} effUT={2:F1} " +
                            "(engine ghost absent; marker rides the drawn leg)",
                            ri, rec.RecordingId, effUT),
                        5.0);
                }
            }

            LogMapMarkerSummary(summary);
        }

        /// <summary>
        /// Slice (iii): draw ONE per-instance overlap marker for a single live overlap cycle at its
        /// own playback head UT (<paramref name="headUT"/> = <c>ComputeOverlapCyclePlaybackUT(cycle)</c>),
        /// riding the SINGLE shared polyline keyed by RecordingId. Returns true when a marker drew, false
        /// when the instance was skipped (head out-of-window / between legs / off-line, or the cycle is
        /// currently represented by a live non-suppressed proto icon). Map-view only (the caller gates
        /// on isMapView).
        ///
        /// Orbital no-double-marker rule: for an ORBITAL overlap recording slice (i) creates N
        /// ProtoVessels with orbit icons. If this cycle has a live instance proto AND its icon is NOT
        /// suppressed, the proto icon already draws the cycle - skip the polyline marker. Otherwise (no
        /// proto for the cycle: pure-suborbital or pre-materialize, OR the icon is suppressed for a
        /// non-orbital phase) the polyline marker is the sole indicator - draw it. For the maintainer's
        /// pure-suborbital case overlapInstanceVessels is empty so every cycle takes the draw branch.
        ///
        /// Position contract mirrors the single-instance polyline tail in DrawMapMarkers verbatim:
        /// resolve the body-fixed head via TryComputeGhostWorldPosition (skip on failure), then RIDE the
        /// shared polyline via TryAnchorMarkerToPolyline - use the on-line position when it rides, else
        /// skip this instance's marker (never draw off-line). The per-instance marker key is
        /// <c>recId + "#" + cycle</c> so hover/sticky state is independent across instances; the visible
        /// label is the shared mission name for all N (they ARE the same mission).
        /// </summary>
        private bool DrawOneOverlapInstanceMarker(
            int recordingIndex, IReadOnlyList<Recording> committed, double headUT, long cycle)
        {
            if (committed == null || recordingIndex < 0 || recordingIndex >= committed.Count)
                return false;
            Recording rec = committed[recordingIndex];
            if (rec == null)
                return false;

            // Orbital no-double-marker join: if a live, non-suppressed proto icon already draws this
            // cycle, skip the polyline marker. A pid of 0 means no proto for the cycle (pure-suborbital
            // or not yet materialized) -> draw the polyline marker.
            uint instancePid = GhostMapPresence.TryGetOverlapInstancePidForCycle(recordingIndex, cycle);
            if (instancePid != 0
                && !GhostMapPresence.ShouldDrawNonProtoMarkerForGhost(instancePid))
            {
                // The proto icon owns this cycle this frame — no polyline marker (no double).
                return false;
            }

            // Body-fixed head for the instance; skip on failure (out-of-window / between legs).
            if (!TryComputeGhostWorldPosition(
                    recordingIndex, committed, headUT, out Vector3 markerPos, out _))
                return false;

            // Ride the SINGLE shared polyline at this instance's head; never draw off-line.
            if (!Parsek.Display.GhostTrajectoryPolylineRenderer.TryAnchorMarkerToPolyline(
                    rec.RecordingId, headUT, out Vector3 onLinePos))
                return false;
            markerPos = onLinePos;

            // Per-instance key (hover-collision fix): distinct keys, identical labels are fine
            // (MapMarkerRenderer keys hover/sticky on markerKey, not the label).
            string markerKey = string.IsNullOrEmpty(rec.RecordingId)
                ? null
                : rec.RecordingId + "#" + cycle.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string label = rec.VesselName ?? "Ghost";
            VesselType vtype = GhostMapPresence.ResolveVesselType(rec.VesselSnapshot);
            Color markerColor = GetGhostMarkerColorForType(vtype);
            DrawMapMarkerAt(markerPos, markerKey, label, markerColor, vtype);

            // Throttle key is per-RECORDING (rec.RecordingId), NOT the per-cycle markerKey: at high
            // time warp the overlap cycle index advances every frame, so a per-cycle key yields a fresh
            // key each frame and defeats VerboseRateLimited (a per-marker flood). The cycle stays in the
            // detail; the per-recording `overlap-instance-markers` summary carries the drawn count.
            if (MapRenderTrace.IsEnabled)
                MapRenderTrace.EmitMarker(
                    MapRenderTrace.RenderSurface.ImguiLabeledMarker, rec.RecordingId, headUT,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "vessel={0} cycle={1} markerPos={2} overlapInstance=True",
                        label, cycle, MapRenderTrace.FormatVector3(markerPos)));

            return true;
        }

        /// <summary>
        /// Computes a ghost's world position from trajectory data when the ghost mesh is hidden.
        /// Uses InterpolatePoints for smooth movement between recorded trajectory points.
        /// Returns false if the recording is out of UT range or has no trajectory data.
        /// </summary>
        private bool TryComputeGhostWorldPosition(
            int recordingIndex,
            IReadOnlyList<Recording> committed,
            double ut,
            out Vector3 worldPos,
            out MapMarkerPositionFailureReason failureReason,
            double bodyFixedShiftSeconds = 0.0)
        {
            worldPos = Vector3.zero;
            failureReason = MapMarkerPositionFailureReason.None;
            if (committed == null || recordingIndex < 0 || recordingIndex >= committed.Count)
            {
                failureReason = MapMarkerPositionFailureReason.BadRecordingIndex;
                return false;
            }

            var rec = committed[recordingIndex];
            if (rec.Points == null || rec.Points.Count == 0)
            {
                failureReason = MapMarkerPositionFailureReason.NoTrajectoryPoints;
                return false;
            }
            if (ut < rec.StartUT || ut > rec.EndUT)
            {
                failureReason = MapMarkerPositionFailureReason.OutsideTimeRange;
                return false;
            }

            int cachedIdx;
            if (!mapMarkerCachedIndices.TryGetValue(recordingIndex, out cachedIdx))
                cachedIdx = -1;

            TrajectoryPoint before, after;
            float t;
            bool found = TrajectoryMath.InterpolatePoints(rec.Points, ref cachedIdx, ut,
                out before, out after, out t);
            mapMarkerCachedIndices[recordingIndex] = cachedIdx;

            double lat, lon, alt;
            if (found)
            {
                lat = before.latitude + (after.latitude - before.latitude) * t;
                lon = before.longitude + (after.longitude - before.longitude) * t;
                alt = TrajectoryMath.InterpolateAltitude(before.altitude, after.altitude, t);
            }
            else
            {
                lat = before.latitude;
                lon = before.longitude;
                alt = before.altitude;
            }

            CelestialBody body;
            if (!bodyCache.TryGetValue(before.bodyName, out body))
            {
                body = FlightGlobals.Bodies?.Find(b => b.name == before.bodyName);
                if (body != null)
                    bodyCache[before.bodyName] = body;
            }
            if (body == null)
            {
                failureReason = MapMarkerPositionFailureReason.MissingBody;
                return false;
            }

            // M4b phasing-knob body-fixed derotation (identity at shift 0): a knob-shifted
            // launch's vacuum point must land at its recorded INERTIAL spot, not ride the
            // planet's extra rotation (the map-icon teleports near the station).
            lon = TrajectoryMath.FrameTransform.ShiftLongitudeDegrees(
                lon, bodyFixedShiftSeconds, body);
            worldPos = (Vector3)body.GetWorldSurfacePosition(lat, lon, alt);
            return true;
        }

        /// <summary>
        /// Clears cached waypoint indices used for trajectory-derived map markers.
        /// Must be called when recordings are reindexed (deletion, optimization pass).
        /// </summary>
        internal void ClearMapMarkerCache()
        {
            mapMarkerCachedIndices.Clear();
            bodyCache.Clear();
        }

        public string GetStatusText()
        {
            if (flight.IsRecording) return "RECORDING";
            if (flight.IsPlaying) return "PREVIEWING";
            if (flight.recording.Count > 0) return "Ready (has recording)";
            return "Idle";
        }

        public void Cleanup()
        {
            LedgerOrchestrator.OnTimelineDataChanged -= OnTimelineDataChanged;
            recordingsTableUI.ReleaseInputLock();
            timelineUI.ReleaseInputLock();
            kerbalsUI.ReleaseInputLock();
            careerStateUI.ReleaseInputLock();
            settingsUI.ReleaseInputLock();
            spawnControlUI.ReleaseInputLock();
            structureListUI.ReleaseInputLock();
            ResetCachedWindowStylesForSceneChange();
            // Map marker resources (icon atlas, fallback diamond, label style) are
            // owned by MapMarkerRenderer and reset per scene via ResetForSceneChange.
        }

        /// <summary>
        /// Returns the orbit color for a ghost marker by vessel type.
        /// Delegates to MapMarkerRenderer.GetColorForType for consistency
        /// between flight map view and tracking station.
        /// </summary>
        private static Color GetGhostMarkerColorForType(VesselType vtype)
        {
            return MapMarkerRenderer.GetColorForType(vtype);
        }

        /// <summary>
        /// Flight-scene marker draw. Picks FlightCamera or PlanetariumCamera based on
        /// current view, then delegates to <see cref="MapMarkerRenderer.DrawMarkerAtScreen"/>
        /// so icon lookup, label hover/sticky, and click handling all stay in one place.
        /// </summary>
        private void DrawMapMarkerAt(Vector3 worldPos, string markerKey, string label, Color color,
            VesselType vtype = VesselType.Ship)
        {
            Vector3 screenPos;
            if (MapView.MapIsEnabled)
            {
                Vector3d scaledPos = ScaledSpace.LocalToScaledSpace(worldPos);
                if (PlanetariumCamera.Camera == null) return;
                screenPos = PlanetariumCamera.Camera.WorldToScreenPoint(scaledPos);
            }
            else
            {
                if (FlightCamera.fetch == null || FlightCamera.fetch.mainCamera == null) return;
                screenPos = FlightCamera.fetch.mainCamera.WorldToScreenPoint(worldPos);
            }

            // Behind camera
            if (screenPos.z < 0) return;

            MapMarkerRenderer.DrawMarkerAtScreen(
                new Vector2(screenPos.x, screenPos.y), markerKey, label, color, vtype);
        }

        /// <summary>
        /// Handles resize drag for a window. Call before the window function.
        /// Group Popup passes null for windowName to suppress logging.
        /// </summary>
        internal static void HandleResizeDrag(ref Rect windowRect, ref bool isResizing,
            float minWidth, float minHeight, string windowName)
        {
            if (!isResizing) return;

            if (Event.current.type == EventType.MouseDrag || Event.current.type == EventType.MouseUp)
            {
                float newW = Mathf.Max(minWidth, Event.current.mousePosition.x - windowRect.x);
                float newH = Mathf.Max(minHeight, Event.current.mousePosition.y - windowRect.y);
                windowRect.width = newW;
                windowRect.height = newH;
            }
            if (Event.current.type == EventType.MouseUp)
            {
                isResizing = false;
                if (windowName != null)
                {
                    var ic = System.Globalization.CultureInfo.InvariantCulture;
                    ParsekLog.Verbose("UI",
                        $"{windowName} resize ended: w={windowRect.width.ToString("F0", ic)} h={windowRect.height.ToString("F0", ic)}");
                }
            }
            if (Event.current.type == EventType.MouseDrag)
                Event.current.Use();
        }

        /// <summary>
        /// Draws a resize handle triangle and starts resize on mouse down.
        /// Call at the end of the window draw function.
        /// </summary>
        internal static void DrawResizeHandle(Rect windowRect, ref bool isResizing, string windowName)
        {
            Rect handleRect = new Rect(
                windowRect.width - ResizeHandleSize,
                windowRect.height - ResizeHandleSize,
                ResizeHandleSize, ResizeHandleSize);
            GUI.Label(handleRect, "\u25e2");
            if (Event.current.type == EventType.MouseDown && handleRect.Contains(Event.current.mousePosition))
            {
                isResizing = true;
                if (windowName != null)
                    ParsekLog.Verbose("UI", $"{windowName} resize started");
                Event.current.Use();
            }
        }
    }
}
