using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClickThroughFix;
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

        // Cached waypoint indices and body lookup for trajectory-derived map marker positions (Bug A fix)
        private readonly Dictionary<int, int> mapMarkerCachedIndices = new Dictionary<int, int>();
        private readonly Dictionary<string, CelestialBody> bodyCache = new Dictionary<string, CelestialBody>();

        // Window drag tracking for position logging
        private Rect lastMainWindowRect;
        // Spawn Control window (extracted to SpawnControlUI)
        private SpawnControlUI spawnControlUI;

        // Gloops Flight Recorder window (extracted to GloopsRecorderUI)
        private GloopsRecorderUI gloopsUI;

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
            this.spawnControlUI = new SpawnControlUI(this);
            this.gloopsUI = new GloopsRecorderUI(this);
            this.timelineUI = new TimelineWindowUI(this);
            this.kerbalsUI = new KerbalsWindowUI(this);
            this.careerStateUI = new CareerStateWindowUI(this);
            this.testRunnerUI = new TestRunnerUI(this);
            this.settingsUI = new SettingsWindowUI(this);
            LedgerOrchestrator.OnTimelineDataChanged += OnTimelineDataChanged;
        }

        public ParsekUI(UIMode mode)
        {
            this.flight = null;
            this.mode = mode;
            this.recordingsTableUI = new RecordingsTableUI(this);
            this.spawnControlUI = new SpawnControlUI(this);
            this.gloopsUI = new GloopsRecorderUI(this);
            this.timelineUI = new TimelineWindowUI(this);
            this.kerbalsUI = new KerbalsWindowUI(this);
            this.careerStateUI = new CareerStateWindowUI(this);
            this.testRunnerUI = new TestRunnerUI(this);
            this.settingsUI = new SettingsWindowUI(this);
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

            // [Phase 3] ERS-routed: top-level Recordings button shows the
            // user-facing effective count (hides NotCommitted / superseded /
            // session-suppressed).
            int committedCount = EffectiveState.ComputeERS().Count;
            if (GUILayout.Button($"Recordings ({committedCount})"))
                ToggleRecordingsWindow();

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

        // ════════════════════════════════════════════════════════════════
        //  Recordings table (extracted to RecordingsTableUI)
        // ════════════════════════════════════════════════════════════════

        public void DrawRecordingsWindowIfOpen(Rect mainWindowRect)
        {
            recordingsTableUI.DrawIfOpen(mainWindowRect);
        }

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

            // Bigger title font + more breathing room above/below the title text so the
            // title bar is not cramped. Applied here because every Parsek window routes
            // through GetOpaqueWindowStyle() — one edit retitles them all.
            int baseFontSize = skin.window.fontSize;
            if (baseFontSize <= 0) baseFontSize = skin.label.fontSize;       // GUI.skin.window often reports 0
            if (baseFontSize <= 0) baseFontSize = 12;                         // last-resort default
            opaqueWindowStyle.fontSize = baseFontSize + 2;
            var p = opaqueWindowStyle.padding;
            opaqueWindowStyle.padding = new RectOffset(p.left, p.right, p.top + 10, p.bottom + 4);
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
                    "Confirm Wipe Recordings",
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
                    "Confirm Wipe Game Actions",
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
                || MissingBody > 0;
        }

        internal static string FormatMapMarkerSummary(MapMarkerSummary summary)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Map marker summary: view={0} candidates={1} previewDrawn={2} drawn={3} cameraUnavailable={4} hiddenInFlight={5} nativeIcon={6} debris={7} chainNonTip={8} positionFailure={9} missingBody={10}",
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
                summary.MissingBody);
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

            foreach (var kvp in flight.Engine.ghostStates)
            {
                var state = kvp.Value;
                if (state == null) continue;
                summary.Candidates++;
                bool meshActive = state.ghost != null && state.ghost.activeSelf;

                // In flight view, skip hidden ghosts (stale positions cause wrong markers #245/#247)
                if (!meshActive && !isMapView)
                {
                    summary.HiddenInFlight++;
                    continue;
                }

                // Skip if native KSP icon is active (ProtoVessel exists and icon not suppressed).
                // When the Harmony patch suppresses the icon (below atmosphere), we draw
                // our custom marker at the ghost mesh position instead.
                if (GhostMapPresence.HasGhostVesselForRecording(kvp.Key))
                {
                    uint ghostPid = GhostMapPresence.GetGhostVesselPidForRecording(kvp.Key);
                    if (ghostPid == 0 || !GhostMapPresence.IsIconSuppressed(ghostPid))
                    {
                        summary.NativeIcon++;
                        continue; // native icon is active — skip our marker
                    }
                }
                if (kvp.Key < committed.Count)
                {
                    if (committed[kvp.Key].IsDebris)
                    {
                        summary.Debris++;
                        continue;
                    }
                    string chainId = committed[kvp.Key].ChainId;
                    if (!string.IsNullOrEmpty(chainId) && chainTipIndexBuffer.Count > 0
                        && chainTipIndexBuffer.TryGetValue(chainId, out int tip) && kvp.Key != tip)
                    {
                        summary.ChainNonTip++;
                        continue; // not the tip — skip duplicate
                    }
                }

                // Resolve marker world position: use ghost mesh when active, otherwise
                // compute from trajectory data (map view only — hidden ghosts need icons too).
                Vector3 markerPos;
                if (meshActive)
                {
                    markerPos = state.ghost.transform.position;
                }
                else
                {
                    if (!TryComputeGhostWorldPosition(
                            kvp.Key,
                            committed,
                            currentUT,
                            out markerPos,
                            out MapMarkerPositionFailureReason failureReason))
                    {
                        summary.PositionFailure++;
                        if (failureReason == MapMarkerPositionFailureReason.MissingBody)
                            summary.MissingBody++;
                        continue;
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
            }

            LogMapMarkerSummary(summary);
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
            out MapMarkerPositionFailureReason failureReason)
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
