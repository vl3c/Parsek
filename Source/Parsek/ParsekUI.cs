using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClickThroughFix;
using KSP.UI.Screens.Mapview;
using UnityEngine;

namespace Parsek
{
    public enum UIMode { Flight, KSC }

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

        // Map view markers
        private GUIStyle mapMarkerStyle;
        private Texture2D mapMarkerDiamond; // fallback when atlas unavailable
        private static Texture2D vesselIconAtlas;
        private static Dictionary<VesselType, Rect> vesselIconUVs;

        // Recordings table window (extracted to RecordingsTableUI)
        private RecordingsTableUI recordingsTableUI;

        private const float ResizeHandleSize = 16f;

        // Settings window (extracted to SettingsWindowUI)
        private SettingsWindowUI settingsUI;

        // Test runner window (extracted to TestRunnerUI)
        private TestRunnerUI testRunnerUI;

        // Timeline window (extracted to TimelineWindowUI, replaces ActionsWindowUI)
        private TimelineWindowUI timelineUI;

        // Reusable per-frame buffers (used by DrawMapMarkers for chain dedup)
        private static readonly Dictionary<string, int> chainTipIndexBuffer = new Dictionary<string, int>();

        // Window drag tracking for position logging
        private Rect lastMainWindowRect;
        // Spawn Control window (extracted to SpawnControlUI)
        private SpawnControlUI spawnControlUI;

        private static readonly string VersionLabel = "v" +
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        private GUIStyle versionStyle;

        internal ParsekFlight Flight => flight;
        internal bool InFlightMode => InFlight;
        internal RecordingsTableUI GetRecordingsTableUI() => recordingsTableUI;
        internal TimelineWindowUI GetTimelineUI() => timelineUI;

        // Runtime-only empty groups — delegated to RecordingsTableUI
        internal List<string> KnownEmptyGroups => recordingsTableUI.KnownEmptyGroups;

        public ParsekUI(ParsekFlight flight)
        {
            this.flight = flight;
            this.mode = UIMode.Flight;
            this.recordingsTableUI = new RecordingsTableUI(this);
            this.spawnControlUI = new SpawnControlUI(this);
            this.timelineUI = new TimelineWindowUI(this);
            this.testRunnerUI = new TestRunnerUI(this);
            this.settingsUI = new SettingsWindowUI(this);
        }

        public ParsekUI(UIMode mode)
        {
            this.flight = null;
            this.mode = mode;
            this.recordingsTableUI = new RecordingsTableUI(this);
            this.spawnControlUI = new SpawnControlUI(this);
            this.timelineUI = new TimelineWindowUI(this);
            this.testRunnerUI = new TestRunnerUI(this);
            this.settingsUI = new SettingsWindowUI(this);
        }

        private const float SpacingSmall = 3f;
        private const float SpacingLarge = 10f;

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

            // --- Timeline buttons ---
            GUILayout.Space(SpacingLarge);

            int committedCount = RecordingStore.CommittedRecordings.Count;
            if (GUILayout.Button($"Recordings ({committedCount})"))
            {
                recordingsTableUI.IsOpen = !recordingsTableUI.IsOpen;
                ParsekLog.Verbose("UI", $"Recordings window toggled: {(recordingsTableUI.IsOpen ? "open" : "closed")}");
            }

            int actionCount = MilestoneStore.GetPendingEventCount() + GameStateStore.GetUncommittedEventCount();
            if (GUILayout.Button($"Timeline ({actionCount})"))
            {
                timelineUI.IsOpen = !timelineUI.IsOpen;
                ParsekLog.Verbose("UI", $"Timeline window toggled: {(timelineUI.IsOpen ? "open" : "closed")}");
            }

            // --- Real Spawn Control toggle (in the window group, after Game Actions) ---
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
            }

            if (InFlight)
                DrawFlightRecordingControls();

            // --- Settings button ---
            GUILayout.Space(SpacingLarge);
            if (GUILayout.Button("Settings"))
            {
                settingsUI.IsOpen = !settingsUI.IsOpen;
                ParsekLog.Verbose("UI", $"Settings window toggled: {(settingsUI.IsOpen ? "open" : "closed")}");
            }

            // --- Version footer ---
            GUILayout.Space(SpacingSmall);
            if (versionStyle == null)
            {
                versionStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 10,
                    alignment = TextAnchor.MiddleRight,
                    normal = { textColor = new Color(1f, 1f, 1f, 0.4f) }
                };
            }
            GUILayout.Label(VersionLabel, versionStyle);

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

        private void DrawFlightRecordingControls()
        {
            GUILayout.Space(SpacingLarge);

            if (!flight.IsRecording)
            {
                if (GUILayout.Button("Start Recording"))
                {
                    ParsekLog.Verbose("UI", "Start Recording button clicked");
                    flight.StartRecording();
                }
            }
            else
            {
                if (GUILayout.Button("Stop Recording"))
                {
                    ParsekLog.Verbose("UI", "Stop Recording button clicked");
                    flight.StopRecording();
                }
            }

            if (!flight.IsPlaying)
            {
                GUI.enabled = !flight.IsRecording && flight.recording.Count > 0;
                if (GUILayout.Button("Preview Playback"))
                {
                    ParsekLog.Verbose("UI", "Preview Playback button clicked");
                    flight.StartPlayback();
                }
                GUI.enabled = true;
            }
            else
            {
                if (GUILayout.Button("Stop Preview"))
                {
                    ParsekLog.Verbose("UI", "Stop Preview button clicked");
                    flight.StopPlayback();
                }
            }

            GUI.enabled = !flight.IsRecording && !flight.IsPlaying && flight.recording.Count > 0;
            if (GUILayout.Button("Clear Current Recording"))
            {
                ParsekLog.Verbose("UI", "Clear Current Recording button clicked");
                recordingsTableUI.ShowClearRecordingConfirmation();
            }

            bool canCommitStandalone = !flight.IsRecording && !flight.IsPlaying
                && flight.recording.Count >= 2 && !flight.HasActiveChain && !flight.HasActiveTree;
            bool canCommitTree = flight.HasActiveTree;
            bool stableSituation = FlightGlobals.ActiveVessel != null
                && RecordingStore.IsStableState((int)FlightGlobals.ActiveVessel.situation);
            GUI.enabled = (canCommitStandalone || canCommitTree) && stableSituation;
            if (GUILayout.Button(stableSituation
                ? new GUIContent("Commit Recording to Timeline")
                : new GUIContent("Commit Recording to Timeline", "Land or stop before committing.")))
            {
                ParsekLog.Verbose("UI", "Commit Flight button clicked");
                if (flight.HasActiveTree)
                    flight.CommitTreeFlight();
                else
                    flight.CommitFlight();
            }
            GUI.enabled = true;
        }

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

        // ════════════════════════════════════════════════════════════════
        //  Recordings table (extracted to RecordingsTableUI)
        // ════════════════════════════════════════════════════════════════

        public void DrawRecordingsWindowIfOpen(Rect mainWindowRect)
        {
            recordingsTableUI.DrawIfOpen(mainWindowRect);
        }

        // ════════════════════════════════════════════════════════════════
        //  Opaque window style (shared by all sub-windows)
        // ════════════════════════════════════════════════════════════════

        private void EnsureOpaqueWindowStyle()
        {
            if (opaqueWindowStyle != null) return;

            // Copy KSP's default window style, then make background textures opaque
            // while preserving all border/highlight details
            opaqueWindowStyle = new GUIStyle(GUI.skin.window);
            opaqueWindowStyle.normal.background = MakeOpaqueCopy(opaqueWindowStyle.normal.background);
            opaqueWindowStyle.onNormal.background = MakeOpaqueCopy(opaqueWindowStyle.onNormal.background);
            opaqueWindowStyle.focused.background = MakeOpaqueCopy(opaqueWindowStyle.focused.background);
            opaqueWindowStyle.onFocused.background = MakeOpaqueCopy(opaqueWindowStyle.onFocused.background);
            opaqueWindowStyle.active.background = MakeOpaqueCopy(opaqueWindowStyle.active.background);
            opaqueWindowStyle.onActive.background = MakeOpaqueCopy(opaqueWindowStyle.onActive.background);
            opaqueWindowStyle.hover.background = MakeOpaqueCopy(opaqueWindowStyle.hover.background);
            opaqueWindowStyle.onHover.background = MakeOpaqueCopy(opaqueWindowStyle.onHover.background);
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
            EnsureOpaqueWindowStyle();
            return opaqueWindowStyle;
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
            float width, bool expand, Action onChanged)
            where TCol : struct
        {
            string arrow = EqualityComparer<TCol>.Default.Equals(currentCol, col)
                ? (ascending ? " \u25b2" : " \u25bc") : "";
            bool clicked = expand
                ? GUILayout.Button(label + arrow, GUI.skin.label, GUILayout.ExpandWidth(true))
                : GUILayout.Button(label + arrow, GUI.skin.label, GUILayout.Width(width));

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
            List<Recording> committed, int[] sortedIndices,
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

        internal enum SortColumn { Index, Phase, Name, LaunchTime, Duration, Status }

        internal static int GetStatusOrder(Recording rec, double now)
            => RecordingsTableUI.GetStatusOrder(rec, now);

        internal static double GetRecordingSortKey(Recording rec, SortColumn column, double now, int rowFallback)
            => RecordingsTableUI.GetRecordingSortKey(rec, (RecordingsTableUI.SortColumn)(int)column, now, rowFallback);

        internal static double GetChainSortKey(List<int> members, List<Recording> committed,
            SortColumn column, double now)
            => RecordingsTableUI.GetChainSortKey(members, committed, (RecordingsTableUI.SortColumn)(int)column, now);

        internal static int CompareRecordings(
            Recording ra, Recording rb,
            SortColumn column, bool ascending, double now)
            => RecordingsTableUI.CompareRecordings(ra, rb, (RecordingsTableUI.SortColumn)(int)column, ascending, now);

        internal static int[] BuildSortedIndices(
            List<Recording> committed, SortColumn column, bool ascending, double now)
            => RecordingsTableUI.BuildSortedIndices(committed, (RecordingsTableUI.SortColumn)(int)column, ascending, now);

        internal static string FormatDuration(double seconds)
            => RecordingsTableUI.FormatDuration(seconds);

        internal static string FormatAltitude(double meters)
            => RecordingsTableUI.FormatAltitude(meters);

        internal static string FormatSpeed(double mps)
            => RecordingsTableUI.FormatSpeed(mps);

        internal static string FormatDistance(double meters)
            => RecordingsTableUI.FormatDistance(meters);

        internal static double GetGroupEarliestStartUT(HashSet<int> descendants, List<Recording> committed)
            => RecordingsTableUI.GetGroupEarliestStartUT(descendants, committed);

        internal static double GetGroupTotalDuration(HashSet<int> descendants, List<Recording> committed)
            => RecordingsTableUI.GetGroupTotalDuration(descendants, committed);

        internal static int FindGroupMainRecordingIndex(
            HashSet<int> descendants, List<Recording> committed)
            => RecordingsTableUI.FindGroupMainRecordingIndex(descendants, committed);

        internal static void GetGroupStatus(HashSet<int> descendants, List<Recording> committed,
            double now, out string statusText, out int statusOrder)
            => RecordingsTableUI.GetGroupStatus(descendants, committed, now, out statusText, out statusOrder);

        internal static double GetGroupSortKey(HashSet<int> descendants, List<Recording> committed,
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

        public void DrawTestRunnerWindowIfOpen(Rect mainWindowRect, MonoBehaviour host)
        {
            testRunnerUI.DrawIfOpen(mainWindowRect, host);
        }

        internal void ToggleTestRunner()
        {
            testRunnerUI.IsOpen = !testRunnerUI.IsOpen;
            ParsekLog.Verbose("UI", $"Test runner window toggled: {(testRunnerUI.IsOpen ? "open" : "closed")}");
        }

        // ════════════════════════════════════════════════════════════════
        //  Map markers
        // ════════════════════════════════════════════════════════════════

        public void DrawMapMarkers()
        {
            // Resolve camera for current view; bail if unavailable
            if (MapView.MapIsEnabled)
            {
                if (PlanetariumCamera.Camera == null) return;
            }
            else
            {
                if (FlightCamera.fetch == null || FlightCamera.fetch.mainCamera == null) return;
            }

            EnsureMapMarkerResources();

            // Manual preview ghost
            if (flight.IsPlaying && flight.PreviewGhost != null)
            {
                DrawMapMarkerAt(flight.PreviewGhost.transform.position, "Preview",
                    new Color(0.2f, 1f, 0.4f, 0.9f));
            }

            // Timeline ghosts — skip if a ghost map ProtoVessel exists for this index
            // (the native KSP vessel icon replaces this marker and tracks the correct orbital position).
            // Deduplicate per chain: during warp, multiple chain segments can be active
            // simultaneously. Only draw the marker for the highest-index (latest) ghost per chain.
            var committed = RecordingStore.CommittedRecordings;

            // First pass: find the highest active index per chain
            chainTipIndexBuffer.Clear();
            foreach (var kvp in flight.TimelineGhosts)
            {
                if (kvp.Value == null) continue;
                if (kvp.Key >= committed.Count) continue;
                string chainId = committed[kvp.Key].ChainId;
                if (string.IsNullOrEmpty(chainId)) continue;
                int existing;
                if (!chainTipIndexBuffer.TryGetValue(chainId, out existing) || kvp.Key > existing)
                    chainTipIndexBuffer[chainId] = kvp.Key;
            }

            // Second pass: draw markers, skipping non-tip chain members and debris
            foreach (var kvp in flight.TimelineGhosts)
            {
                if (kvp.Value == null) continue;
                // Skip if native KSP icon is active (ProtoVessel exists and icon not suppressed).
                // When the Harmony patch suppresses the icon (below atmosphere), we draw
                // our custom marker at the ghost mesh position instead.
                if (GhostMapPresence.HasGhostVesselForRecording(kvp.Key))
                {
                    uint ghostPid = GhostMapPresence.GetGhostVesselPidForRecording(kvp.Key);
                    if (ghostPid == 0 || !GhostMapPresence.IsIconSuppressed(ghostPid))
                        continue; // native icon is active — skip our marker
                }
                if (kvp.Key < committed.Count)
                {
                    if (committed[kvp.Key].IsDebris)
                        continue;
                    string chainId = committed[kvp.Key].ChainId;
                    if (!string.IsNullOrEmpty(chainId) && chainTipIndexBuffer.Count > 0
                        && chainTipIndexBuffer.TryGetValue(chainId, out int tip) && kvp.Key != tip)
                        continue; // not the tip — skip duplicate
                }

                string ghostName = kvp.Key < committed.Count ? committed[kvp.Key].VesselName : "Ghost";
                VesselType vtype = kvp.Key < committed.Count
                    ? GhostMapPresence.ResolveVesselType(committed[kvp.Key].VesselSnapshot)
                    : VesselType.Ship;
                Color markerColor = GetGhostMarkerColorForType(vtype);
                DrawMapMarkerAt(kvp.Value.transform.position, ghostName, markerColor, vtype);
            }
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
            recordingsTableUI.ReleaseInputLock();
            timelineUI.ReleaseInputLock();
            settingsUI.ReleaseInputLock();
            spawnControlUI.ReleaseInputLock();
            if (mapMarkerDiamond != null)
                UnityEngine.Object.Destroy(mapMarkerDiamond);
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

        private void DrawMapMarkerAt(Vector3 worldPos, string label, Color color,
            VesselType vtype = VesselType.Ship)
        {
            Vector3 screenPos;
            if (MapView.MapIsEnabled)
            {
                Vector3d scaledPos = ScaledSpace.LocalToScaledSpace(worldPos);
                screenPos = PlanetariumCamera.Camera.WorldToScreenPoint(scaledPos);
            }
            else
            {
                screenPos = FlightCamera.fetch.mainCamera.WorldToScreenPoint(worldPos);
            }

            // Behind camera
            if (screenPos.z < 0) return;

            // GUI coordinates (Y inverted)
            float x = screenPos.x;
            float y = Screen.height - screenPos.y;

            // Draw vessel type icon from KSP atlas (untinted — original icon colors)
            Color prevColor = GUI.color;
            int iconSize = 20;
            Rect uvRect;
            if (vesselIconAtlas != null && vesselIconUVs != null
                && vesselIconUVs.TryGetValue(vtype, out uvRect))
            {
                GUI.color = Color.white;
                GUI.DrawTextureWithTexCoords(
                    new Rect(x - iconSize / 2, y - iconSize / 2, iconSize, iconSize),
                    vesselIconAtlas, uvRect);
            }
            else
            {
                GUI.color = color;
                GUI.DrawTexture(
                    new Rect(x - iconSize / 2, y - iconSize / 2, iconSize, iconSize),
                    mapMarkerDiamond);
            }
            GUI.color = prevColor;

            // Draw vessel name label
            mapMarkerStyle.normal.textColor = color;
            GUI.Label(new Rect(x - 75, y + iconSize / 2 + 2, 150, 20), "Ghost: " + label, mapMarkerStyle);
        }

        private void EnsureMapMarkerResources()
        {
            // Try to load KSP's stock vessel icon atlas from the MapView prefab
            if (vesselIconAtlas == null && MapView.fetch != null)
                InitVesselTypeIcons();

            // Fallback diamond texture (used when atlas unavailable)
            if (mapMarkerDiamond == null)
            {
                int size = 16;
                mapMarkerDiamond = new Texture2D(size, size, TextureFormat.ARGB32, false);
                float center = size / 2f;
                float halfDiag = size / 2f - 1f;
                for (int py = 0; py < size; py++)
                {
                    for (int px = 0; px < size; px++)
                    {
                        float manhattan = Mathf.Abs(px - center + 0.5f) + Mathf.Abs(py - center + 0.5f);
                        mapMarkerDiamond.SetPixel(px, py, manhattan <= halfDiag ? Color.white : Color.clear);
                    }
                }
                mapMarkerDiamond.Apply();
            }

            if (mapMarkerStyle == null)
            {
                mapMarkerStyle = new GUIStyle(GUI.skin.label);
                mapMarkerStyle.fontSize = 11;
                mapMarkerStyle.fontStyle = FontStyle.Bold;
                mapMarkerStyle.alignment = TextAnchor.UpperCenter;
            }
        }

        private static void InitVesselTypeIcons()
        {
            vesselIconAtlas = MapView.OrbitIconsMap;
            if (vesselIconAtlas == null) return;

            var prefab = MapView.UINodePrefab;
            if (prefab == null) return;

            var fi = typeof(MapNode).GetField("iconSprites",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (fi == null)
            {
                ParsekLog.Warn("UI", "InitVesselTypeIcons: iconSprites field not found on MapNode");
                vesselIconAtlas = null;
                return;
            }

            var sprites = fi.GetValue(prefab) as Sprite[];
            if (sprites == null || sprites.Length == 0)
            {
                ParsekLog.Warn("UI", "InitVesselTypeIcons: iconSprites is null or empty");
                vesselIconAtlas = null;
                return;
            }

            // VesselType -> sprite index mapping from decompiled MapNode.GetIconIndex.
            // KSP-version-dependent: indices may change if the atlas is reordered.
            // Failure is graceful — falls back to diamond texture.
            var mapping = new Dictionary<VesselType, int>
            {
                { VesselType.Ship,        20 }, { VesselType.Probe,    18 },
                { VesselType.Rover,       19 }, { VesselType.Station,   0 },
                { VesselType.Plane,       23 }, { VesselType.Lander,   14 },
                { VesselType.Base,         5 }, { VesselType.EVA,      13 },
                { VesselType.Relay,       24 }, { VesselType.Debris,    7 },
                { VesselType.SpaceObject, 21 },
            };

            float atlasW = vesselIconAtlas.width;
            float atlasH = vesselIconAtlas.height;
            vesselIconUVs = new Dictionary<VesselType, Rect>();

            foreach (var kvp in mapping)
            {
                if (kvp.Value < sprites.Length && sprites[kvp.Value] != null)
                {
                    Rect texRect = sprites[kvp.Value].textureRect;
                    vesselIconUVs[kvp.Key] = new Rect(
                        texRect.x / atlasW, texRect.y / atlasH,
                        texRect.width / atlasW, texRect.height / atlasH);
                }
            }

            ParsekLog.Info("UI",
                $"InitVesselTypeIcons: loaded {vesselIconUVs.Count} vessel type icons from atlas " +
                $"({atlasW}x{atlasH}, {sprites.Length} sprites)");
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
