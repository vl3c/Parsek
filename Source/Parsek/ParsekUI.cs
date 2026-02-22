using System.Collections.Generic;
using ClickThroughFix;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// UI rendering for the Parsek window and map view markers.
    /// Receives a ParsekFlight reference for accessing flight state.
    /// </summary>
    public class ParsekUI
    {
        private readonly ParsekFlight flight;

        // Map view markers
        private GUIStyle mapMarkerStyle;
        private Texture2D mapMarkerTexture;

        // Recordings window
        private bool showRecordingsWindow;
        private Rect recordingsWindowRect;
        private Vector2 recordingsScrollPos;
        private int deleteConfirmIndex = -1;
        private bool isResizingRecordingsWindow;
        private bool recordingsWindowHasInputLock;
        private const string RecordingsInputLockId = "Parsek_RecordingsWindow";
        private const float ResizeHandleSize = 16f;
        private const float MinWindowWidth = 350f;
        private const float MinWindowHeight = 150f;

        // Settings window
        private bool showSettingsWindow;
        private Rect settingsWindowRect;
        private bool settingsWindowHasInputLock;
        private const string SettingsInputLockId = "Parsek_SettingsWindow";

        // Column widths — shared between header and body for alignment
        private const float ColW_Index = 25f;
        private const float ColW_Launch = 110f;
        private const float ColW_Dur = 55f;
        private const float ColW_Status = 45f;
        private const float ColW_LoopLabel = 30f;
        private const float ColW_LoopToggle = 15f;
        private const float ColW_Delete = 25f;
        private const float ScrollbarWidth = 16f;

        // Sort state
        internal enum SortColumn { Index, Name, LaunchTime, Duration, Status }
        private SortColumn sortColumn = SortColumn.Index;
        private bool sortAscending = true;
        private int[] sortedIndices; // maps display row → CommittedRecordings index
        private int lastSortedCount = -1;

        // Cached styles for status labels
        private GUIStyle statusStyleFuture;
        private GUIStyle statusStyleActive;
        private GUIStyle statusStylePast;

        public ParsekUI(ParsekFlight flight)
        {
            this.flight = flight;
        }

        public void DrawWindow(int windowID)
        {
            GUILayout.BeginVertical();

            // Status
            GUILayout.Label($"Status: {GetStatusText()}");
            GUILayout.Space(5);

            // Recording info
            GUILayout.Label($"Recorded Points: {flight.recording.Count}");
            if (flight.recording.Count > 0)
            {
                double duration = flight.recording[flight.recording.Count - 1].ut - flight.recording[0].ut;
                GUILayout.Label($"Duration: {duration:F1}s");
            }

            // Timeline info
            int committedCount = RecordingStore.CommittedRecordings.Count;
            int activeGhosts = flight.TimelineGhostCount;
            GUILayout.Label($"Timeline: {committedCount} recording(s), {activeGhosts} active ghost(s)");
            if (GameStateStore.EventCount > 0)
                GUILayout.Label($"Events: {GameStateStore.EventCount}");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button($"Recordings ({committedCount})"))
                showRecordingsWindow = !showRecordingsWindow;
            if (GUILayout.Button("Settings"))
                showSettingsWindow = !showSettingsWindow;
            GUILayout.EndHorizontal();

            // Active ghost controls — Take Control buttons
            var committed = RecordingStore.CommittedRecordings;
            var ghosts = flight.TimelineGhosts;
            for (int i = 0; i < committed.Count; i++)
            {
                if (!ghosts.ContainsKey(i) || ghosts[i] == null) continue;
                var rec = committed[i];
                if (rec.VesselSnapshot == null || rec.VesselDestroyed || rec.VesselSpawned || rec.TakenControl) continue;

                GUILayout.BeginHorizontal();
                GUILayout.Label(rec.VesselName, GUILayout.ExpandWidth(true));
                if (GUILayout.Button("Take Control", GUILayout.Width(90)))
                    flight.TakeControlOfGhost(i);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(10);

            // Controls
            GUILayout.Label("Controls:");
            GUILayout.Label("  F9  - Start/Stop Recording");
            GUILayout.Label("  F10 - Preview Playback");
            GUILayout.Label("  F11 - Stop Preview");

            GUILayout.Space(10);

            // Buttons
            GUILayout.BeginHorizontal();

            if (!flight.IsRecording)
            {
                if (GUILayout.Button("Start Recording"))
                    flight.StartRecording();
            }
            else
            {
                if (GUILayout.Button("Stop Recording"))
                    flight.StopRecording();
            }

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();

            GUI.enabled = !flight.IsRecording && flight.recording.Count > 0 && !flight.IsPlaying;
            if (GUILayout.Button("Preview Playback"))
                flight.StartPlayback();

            GUI.enabled = flight.IsPlaying;
            if (GUILayout.Button("Stop Preview"))
                flight.StopPlayback();

            GUI.enabled = true;

            GUILayout.EndHorizontal();

            // Clear buttons
            GUILayout.Space(5);
            GUI.enabled = !flight.IsRecording && !flight.IsPlaying && flight.recording.Count > 0;
            if (GUILayout.Button("Clear Current Recording"))
            {
                flight.ClearRecording();
            }

            GUI.enabled = !flight.IsRecording && !flight.IsPlaying
                && flight.recording.Count >= 2 && !flight.HasActiveChain;
            if (GUILayout.Button("Commit Flight"))
            {
                flight.CommitFlight();
            }

            GUI.enabled = activeGhosts > 0;
            if (GUILayout.Button($"Despawn Ghosts ({activeGhosts})"))
            {
                flight.DestroyAllTimelineGhosts();
                ParsekLog.Log("Ghosts despawned");
            }

            GUI.enabled = committedCount > 0;
            if (GUILayout.Button($"Wipe Recordings ({committedCount})"))
            {
                // Unreserve crew from all recordings before wiping
                foreach (var rec in RecordingStore.CommittedRecordings)
                    ParsekScenario.UnreserveCrewInSnapshot(rec.VesselSnapshot);
                ParsekScenario.ClearReplacements();
                flight.DestroyAllTimelineGhosts();
                RecordingStore.ClearCommitted();
                ParsekLog.Log("All recordings wiped");
                ParsekLog.ScreenMessage("All recordings wiped", 2f);
            }
            GUI.enabled = true;

            GUILayout.EndVertical();

            // Make window draggable
            GUI.DragWindow();
        }

        public void DrawRecordingsWindowIfOpen(Rect mainWindowRect)
        {
            if (!showRecordingsWindow)
            {
                ReleaseRecordingsInputLock();
                return;
            }

            // Position to the right of main window on first open
            if (recordingsWindowRect.width < 1f)
            {
                recordingsWindowRect = new Rect(
                    mainWindowRect.x + mainWindowRect.width + 10,
                    mainWindowRect.y,
                    520, 350);
            }

            // Handle resize drag (must be outside the window function to track across frames)
            if (isResizingRecordingsWindow)
            {
                if (Event.current.type == EventType.MouseDrag || Event.current.type == EventType.MouseUp)
                {
                    float newW = Mathf.Max(MinWindowWidth, Event.current.mousePosition.x - recordingsWindowRect.x);
                    float newH = Mathf.Max(MinWindowHeight, Event.current.mousePosition.y - recordingsWindowRect.y);
                    recordingsWindowRect.width = newW;
                    recordingsWindowRect.height = newH;
                }
                if (Event.current.type == EventType.MouseUp)
                    isResizingRecordingsWindow = false;
                if (Event.current.type == EventType.MouseDrag)
                    Event.current.Use();
            }

            recordingsWindowRect = ClickThruBlocker.GUILayoutWindow(
                "ParsekRecordings".GetHashCode(),
                recordingsWindowRect,
                DrawRecordingsWindow,
                "Parsek \u2014 Recordings",
                GUILayout.Width(recordingsWindowRect.width),
                GUILayout.Height(recordingsWindowRect.height)
            );

            // Lock camera controls (including scroll zoom) when mouse is over window.
            // ClickThroughBlocker uses ALLBUTCAMERAS which intentionally leaves camera
            // controls unlocked. We add our own lock for CAMERACONTROLS to block scroll zoom.
            if (recordingsWindowRect.Contains(Event.current.mousePosition))
            {
                if (!recordingsWindowHasInputLock)
                {
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, RecordingsInputLockId);
                    recordingsWindowHasInputLock = true;
                }
            }
            else
            {
                ReleaseRecordingsInputLock();
            }
        }

        private void ReleaseRecordingsInputLock()
        {
            if (!recordingsWindowHasInputLock) return;
            InputLockManager.RemoveControlLock(RecordingsInputLockId);
            recordingsWindowHasInputLock = false;
        }

        private void DrawRecordingsWindow(int windowID)
        {
            var committed = RecordingStore.CommittedRecordings;
            double now = Planetarium.GetUniversalTime();

            EnsureStatusStyles();
            RebuildSortedIndices(committed, now);

            if (committed.Count == 0)
            {
                GUILayout.Label("No recordings.");
            }
            else
            {
                // Header row with sortable columns
                // Use same widths as body row cells to ensure alignment.
                // Add scrollbar-width spacer at end so header spans the same
                // content width as the scroll view body.
                GUILayout.BeginHorizontal();
                DrawSortableHeader("#", SortColumn.Index, ColW_Index);
                DrawSortableHeader("Name", SortColumn.Name, 0, true);
                DrawSortableHeader("Launch", SortColumn.LaunchTime, ColW_Launch);
                DrawSortableHeader("Dur", SortColumn.Duration, ColW_Dur);
                DrawSortableHeader("Status", SortColumn.Status, ColW_Status);

                // Select-all loop header + checkbox
                int loopCount = 0;
                for (int i = 0; i < committed.Count; i++)
                    if (committed[i].LoopPlayback) loopCount++;

                bool allLoop = loopCount == committed.Count;
                GUILayout.Label("Loop", GUILayout.Width(ColW_LoopLabel));
                bool newAllLoop = GUILayout.Toggle(allLoop, "", GUILayout.Width(ColW_LoopToggle));
                if (newAllLoop != allLoop)
                {
                    for (int i = 0; i < committed.Count; i++)
                        committed[i].LoopPlayback = newAllLoop;
                }

                GUILayout.Button("", GUI.skin.label, GUILayout.Width(ColW_Delete)); // placeholder
                GUILayout.Space(ScrollbarWidth); // account for scrollbar in body
                GUILayout.EndHorizontal();

                // Scrollable table body (expands to fill available window space)
                recordingsScrollPos = GUILayout.BeginScrollView(
                    recordingsScrollPos, GUILayout.ExpandHeight(true));

                for (int row = 0; row < sortedIndices.Length; row++)
                {
                    int ri = sortedIndices[row];
                    var rec = committed[ri];
                    GUILayout.BeginHorizontal();

                    // #
                    GUILayout.Label((ri + 1).ToString(), GUILayout.Width(ColW_Index));

                    // Name
                    string name = string.IsNullOrEmpty(rec.VesselName) ? "Untitled" : rec.VesselName;
                    GUILayout.Label(name, GUILayout.ExpandWidth(true));

                    // Launch Time
                    string launchTime = rec.Points.Count > 0
                        ? KSPUtil.PrintDateCompact(rec.StartUT, true)
                        : "-";
                    GUILayout.Label(launchTime, GUILayout.Width(ColW_Launch));

                    // Duration
                    double dur = rec.EndUT - rec.StartUT;
                    GUILayout.Label(FormatDuration(dur), GUILayout.Width(ColW_Dur));

                    // Status
                    GUIStyle statusStyle;
                    string statusText;
                    if (now < rec.StartUT)
                    {
                        statusStyle = statusStyleFuture;
                        statusText = "future";
                    }
                    else if (now <= rec.EndUT)
                    {
                        statusStyle = statusStyleActive;
                        statusText = "active";
                    }
                    else
                    {
                        statusStyle = statusStylePast;
                        statusText = "past";
                    }
                    GUILayout.Label(statusText, statusStyle, GUILayout.Width(ColW_Status));

                    // Loop checkbox
                    GUILayout.Label("", GUILayout.Width(ColW_LoopLabel));
                    bool loop = GUILayout.Toggle(rec.LoopPlayback, "", GUILayout.Width(ColW_LoopToggle));
                    if (loop != rec.LoopPlayback)
                        rec.LoopPlayback = loop;

                    // Delete button (disabled during recording/continuation)
                    GUI.enabled = flight.CanDeleteRecording;
                    if (deleteConfirmIndex == ri)
                    {
                        if (GUILayout.Button("?", GUILayout.Width(ColW_Delete)))
                        {
                            deleteConfirmIndex = -1;
                            flight.DeleteRecording(ri);
                            InvalidateSort();
                            GUI.enabled = true;
                            GUILayout.EndHorizontal();
                            break; // list changed, stop iterating
                        }
                    }
                    else
                    {
                        if (GUILayout.Button("X", GUILayout.Width(ColW_Delete)))
                            deleteConfirmIndex = ri;
                    }
                    GUI.enabled = true;

                    GUILayout.EndHorizontal();
                }

                GUILayout.EndScrollView();
            }

            // Reset confirm state if index is now out of range
            if (deleteConfirmIndex >= committed.Count)
                deleteConfirmIndex = -1;

            GUILayout.Space(5);
            if (GUILayout.Button("Close"))
                showRecordingsWindow = false;

            // Resize handle (bottom-right corner)
            Rect handleRect = new Rect(
                recordingsWindowRect.width - ResizeHandleSize,
                recordingsWindowRect.height - ResizeHandleSize,
                ResizeHandleSize, ResizeHandleSize);
            GUI.Label(handleRect, "\u25e2"); // triangle
            if (Event.current.type == EventType.MouseDown && handleRect.Contains(Event.current.mousePosition))
            {
                isResizingRecordingsWindow = true;
                Event.current.Use();
            }

            GUI.DragWindow();
        }

        private void DrawSortableHeader(string label, SortColumn col, float width, bool expand = false)
        {
            string arrow = sortColumn == col ? (sortAscending ? " \u25b2" : " \u25bc") : "";
            bool clicked;
            if (expand)
                clicked = GUILayout.Button(label + arrow, GUI.skin.label, GUILayout.ExpandWidth(true));
            else
                clicked = GUILayout.Button(label + arrow, GUI.skin.label, GUILayout.Width(width));

            if (clicked)
            {
                if (sortColumn == col)
                    sortAscending = !sortAscending;
                else
                {
                    sortColumn = col;
                    sortAscending = true;
                }
                InvalidateSort();
            }
        }

        private void InvalidateSort()
        {
            lastSortedCount = -1;
            sortedIndices = null;
        }

        private void RebuildSortedIndices(List<RecordingStore.Recording> committed, double now)
        {
            if (sortedIndices != null && lastSortedCount == committed.Count)
                return;

            lastSortedCount = committed.Count;
            sortedIndices = new int[committed.Count];
            for (int i = 0; i < committed.Count; i++)
                sortedIndices[i] = i;

            if (sortColumn == SortColumn.Index)
            {
                if (!sortAscending)
                    System.Array.Reverse(sortedIndices);
                return;
            }

            var col = sortColumn;
            var asc = sortAscending;
            System.Array.Sort(sortedIndices, (a, b) =>
                CompareRecordings(committed[a], committed[b], col, asc, now));
        }

        internal static int GetStatusOrder(RecordingStore.Recording rec, double now)
        {
            if (now < rec.StartUT) return 0;  // future
            if (now <= rec.EndUT) return 1;    // active
            return 2;                          // past
        }

        internal static int CompareRecordings(
            RecordingStore.Recording ra, RecordingStore.Recording rb,
            SortColumn column, bool ascending, double now)
        {
            int cmp = 0;
            switch (column)
            {
                case SortColumn.Index:
                    cmp = 0; // stable — Array.Sort preserves original order for equal elements
                    break;
                case SortColumn.Name:
                    string na = string.IsNullOrEmpty(ra.VesselName) ? "Untitled" : ra.VesselName;
                    string nb = string.IsNullOrEmpty(rb.VesselName) ? "Untitled" : rb.VesselName;
                    cmp = string.Compare(na, nb, System.StringComparison.OrdinalIgnoreCase);
                    break;
                case SortColumn.LaunchTime:
                    cmp = ra.StartUT.CompareTo(rb.StartUT);
                    break;
                case SortColumn.Duration:
                    cmp = (ra.EndUT - ra.StartUT).CompareTo(rb.EndUT - rb.StartUT);
                    break;
                case SortColumn.Status:
                    cmp = GetStatusOrder(ra, now).CompareTo(GetStatusOrder(rb, now));
                    break;
            }
            return ascending ? cmp : -cmp;
        }

        internal static int[] BuildSortedIndices(
            List<RecordingStore.Recording> committed, SortColumn column, bool ascending, double now)
        {
            var indices = new int[committed.Count];
            for (int i = 0; i < committed.Count; i++)
                indices[i] = i;

            if (column == SortColumn.Index)
            {
                if (!ascending)
                    System.Array.Reverse(indices);
                return indices;
            }

            System.Array.Sort(indices, (a, b) =>
                CompareRecordings(committed[a], committed[b], column, ascending, now));
            return indices;
        }

        internal static string FormatDuration(double seconds)
        {
            int total = (int)seconds;
            if (total < 60) return $"{total}s";
            if (total < 3600) return $"{total / 60}m {total % 60}s";
            return $"{total / 3600}h {(total % 3600) / 60}m";
        }

        private void EnsureStatusStyles()
        {
            if (statusStyleFuture != null) return;

            statusStyleFuture = new GUIStyle(GUI.skin.label);
            statusStyleFuture.normal.textColor = Color.gray;

            statusStyleActive = new GUIStyle(GUI.skin.label);
            statusStyleActive.normal.textColor = Color.green;

            statusStylePast = new GUIStyle(GUI.skin.label);
            statusStylePast.normal.textColor = new Color(0.5f, 0.5f, 0.5f);
        }

        public void DrawSettingsWindowIfOpen(Rect mainWindowRect)
        {
            if (!showSettingsWindow)
            {
                ReleaseSettingsInputLock();
                return;
            }

            if (settingsWindowRect.width < 1f)
            {
                settingsWindowRect = new Rect(
                    mainWindowRect.x + mainWindowRect.width + 10,
                    mainWindowRect.y + 40,
                    280, 10);
            }

            settingsWindowRect = ClickThruBlocker.GUILayoutWindow(
                "ParsekSettings".GetHashCode(),
                settingsWindowRect,
                DrawSettingsWindow,
                "Parsek \u2014 Settings",
                GUILayout.Width(280)
            );

            if (settingsWindowRect.Contains(Event.current.mousePosition))
            {
                if (!settingsWindowHasInputLock)
                {
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, SettingsInputLockId);
                    settingsWindowHasInputLock = true;
                }
            }
            else
            {
                ReleaseSettingsInputLock();
            }
        }

        private void ReleaseSettingsInputLock()
        {
            if (!settingsWindowHasInputLock) return;
            InputLockManager.RemoveControlLock(SettingsInputLockId);
            settingsWindowHasInputLock = false;
        }

        private void DrawSettingsWindow(int windowID)
        {
            var s = ParsekSettings.Current;
            if (s == null)
            {
                GUILayout.Label("Settings unavailable (no active game).");
                if (GUILayout.Button("Close"))
                    showSettingsWindow = false;
                GUI.DragWindow();
                return;
            }

            GUILayout.Label("Recording", GUI.skin.box);
            s.autoRecordOnLaunch = GUILayout.Toggle(s.autoRecordOnLaunch, "Auto-record on launch");
            s.autoRecordOnEva = GUILayout.Toggle(s.autoRecordOnEva, "Auto-record on EVA");
            s.autoWarpStop = GUILayout.Toggle(s.autoWarpStop, "Auto-stop time warp");

            GUILayout.Space(5);
            GUILayout.Label("Sampling", GUI.skin.box);

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Max interval: {s.maxSampleInterval:F1}s", GUILayout.Width(140));
            s.maxSampleInterval = GUILayout.HorizontalSlider(s.maxSampleInterval, 1f, 10f);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Direction: {s.velocityDirThreshold:F1}\u00b0", GUILayout.Width(140));
            s.velocityDirThreshold = GUILayout.HorizontalSlider(s.velocityDirThreshold, 0.5f, 10f);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Speed: {s.speedChangeThreshold:F0}%", GUILayout.Width(140));
            s.speedChangeThreshold = GUILayout.HorizontalSlider(s.speedChangeThreshold, 1f, 20f);
            GUILayout.EndHorizontal();

            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Defaults"))
            {
                s.autoRecordOnLaunch = true;
                s.autoRecordOnEva = true;
                s.autoWarpStop = true;
                s.maxSampleInterval = 3.0f;
                s.velocityDirThreshold = 2.0f;
                s.speedChangeThreshold = 5.0f;
            }
            if (GUILayout.Button("Close"))
                showSettingsWindow = false;
            GUILayout.EndHorizontal();

            GUI.DragWindow();
        }

        public void DrawMapMarkers()
        {
            Camera cam = PlanetariumCamera.Camera;
            if (cam == null) return;

            EnsureMapMarkerResources();

            // Manual preview ghost
            if (flight.IsPlaying && flight.PreviewGhost != null)
            {
                DrawMapMarkerAt(cam, flight.PreviewGhost.transform.position, "Preview", Color.green);
            }

            // Timeline ghosts
            var committed = RecordingStore.CommittedRecordings;
            Color ghostColor = new Color(0.2f, 1f, 0.4f, 0.9f);
            foreach (var kvp in flight.TimelineGhosts)
            {
                if (kvp.Value == null) continue;
                string ghostName = kvp.Key < committed.Count ? committed[kvp.Key].VesselName : "Ghost";
                DrawMapMarkerAt(cam, kvp.Value.transform.position, ghostName, ghostColor);
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
            ReleaseRecordingsInputLock();
            ReleaseSettingsInputLock();
            if (mapMarkerTexture != null)
                Object.Destroy(mapMarkerTexture);
        }

        private void DrawMapMarkerAt(Camera cam, Vector3 worldPos, string label, Color color)
        {
            Vector3d scaledPos = ScaledSpace.LocalToScaledSpace(worldPos);
            Vector3 screenPos = cam.WorldToScreenPoint(scaledPos);

            // Behind camera
            if (screenPos.z < 0) return;

            // GUI coordinates (Y inverted)
            float x = screenPos.x;
            float y = Screen.height - screenPos.y;

            // Draw marker dot
            Color prevColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(x - 5, y - 5, 10, 10), mapMarkerTexture);
            GUI.color = prevColor;

            // Draw vessel name label
            mapMarkerStyle.normal.textColor = color;
            GUI.Label(new Rect(x - 75, y + 7, 150, 20), label, mapMarkerStyle);
        }

        private void EnsureMapMarkerResources()
        {
            if (mapMarkerTexture == null)
            {
                int size = 10;
                mapMarkerTexture = new Texture2D(size, size, TextureFormat.ARGB32, false);
                float center = size / 2f;
                float radius = size / 2f - 1f;
                for (int py = 0; py < size; py++)
                {
                    for (int px = 0; px < size; px++)
                    {
                        float dist = Mathf.Sqrt((px - center) * (px - center) + (py - center) * (py - center));
                        mapMarkerTexture.SetPixel(px, py, dist <= radius ? Color.white : Color.clear);
                    }
                }
                mapMarkerTexture.Apply();
            }

            if (mapMarkerStyle == null)
            {
                mapMarkerStyle = new GUIStyle(GUI.skin.label);
                mapMarkerStyle.fontSize = 11;
                mapMarkerStyle.fontStyle = FontStyle.Bold;
                mapMarkerStyle.alignment = TextAnchor.UpperCenter;
            }
        }
    }
}
