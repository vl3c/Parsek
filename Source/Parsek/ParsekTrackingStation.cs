using System.Collections.Generic;
using System.Globalization;
using ClickThroughFix;
using Parsek.InGameTests;
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
    [KSPAddon(KSPAddon.Startup.TrackingStation, false)]
    public class ParsekTrackingStation : MonoBehaviour
    {
        private const string Tag = "TrackingStation";
        private const float LifecycleCheckIntervalSec = 2.0f;
        private float nextLifecycleCheckTime;

        /// <summary>Cached interpolation indices for atmospheric ghost icon rendering (per recording index).</summary>
        private readonly Dictionary<int, int> atmosCachedIndices = new Dictionary<int, int>();

    
        void Start()
        {
            int created = GhostMapPresence.CreateGhostVesselsFromCommittedRecordings();
            int renderersFixed = GhostMapPresence.EnsureGhostOrbitRenderers();

            nextLifecycleCheckTime = Time.time + LifecycleCheckIntervalSec;
            atmosCachedIndices.Clear();

            ParsekLog.Info(Tag,
                $"ParsekTrackingStation initialized: created {created} ghost vessel(s), " +
                $"fixed {renderersFixed} orbit renderer(s)");
        }

        void Update()
        {
            if (Time.time < nextLifecycleCheckTime) return;
            nextLifecycleCheckTime = Time.time + LifecycleCheckIntervalSec;

            GhostMapPresence.UpdateTrackingStationGhostLifecycle();
        }

        void OnGUI()
        {
            // Test runner window (available via Ctrl+Shift+T in tracking station)
            if (Event.current.type == EventType.KeyDown
                && Event.current.control && Event.current.shift
                && Event.current.keyCode == KeyCode.T)
            {
                showTestRunnerWindow = !showTestRunnerWindow;
                ParsekLog.Verbose(Tag, $"Test runner toggled: {(showTestRunnerWindow ? "open" : "closed")}");
                Event.current.Use();
            }
            DrawTestRunnerIfOpen();

            // Draw icons for recordings in atmospheric phases (no ProtoVessel).
            // Position comes directly from trajectory point interpolation —
            // same approach as ParsekUI.DrawMapMarkers in the flight scene.
            if (Event.current.type != EventType.Repaint) return;
            if (PlanetariumCamera.Camera == null) return;

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || committed.Count == 0) return;

            double currentUT = Planetarium.GetUniversalTime();

            for (int i = 0; i < committed.Count; i++)
            {
                // Skip if a ProtoVessel ghost already handles this recording
                if (GhostMapPresence.HasGhostVesselForRecording(i)) continue;

                var rec = committed[i];
                if (rec.IsDebris) continue;
                if (rec.Points == null || rec.Points.Count == 0) continue;
                if (currentUT < rec.Points[0].ut || currentUT > rec.Points[rec.Points.Count - 1].ut) continue;

                // Skip superseded recordings (intermediate chain segments).
                // Uses cached set from UpdateTrackingStationGhostLifecycle (computed once per tick).
                var superseded = GhostMapPresence.CachedSupersededIds;
                if (superseded != null && superseded.Contains(rec.RecordingId)) continue;

                // Skip non-orbital terminal states
                var terminal = rec.TerminalStateValue;
                if (terminal.HasValue
                    && terminal.Value != TerminalState.Orbiting
                    && terminal.Value != TerminalState.Docked)
                    continue;

                // Skip if currently in an orbit segment (ProtoVessel handles that)
                if (rec.OrbitSegments != null
                    && TrajectoryMath.FindOrbitSegment(rec.OrbitSegments, currentUT).HasValue)
                    continue;

                // Interpolate trajectory position at current UT
                if (!atmosCachedIndices.ContainsKey(i))
                    atmosCachedIndices[i] = -1;
                int cached = atmosCachedIndices[i];
                TrajectoryPoint? pt = TrajectoryMath.BracketPointAtUT(rec.Points, currentUT, ref cached);
                atmosCachedIndices[i] = cached;

                if (!pt.HasValue) continue;

                CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == pt.Value.bodyName);
                if (body == null) continue;

                Vector3d worldPos = body.GetWorldSurfacePosition(
                    pt.Value.latitude, pt.Value.longitude, pt.Value.altitude);

                VesselType vtype = ResolveVesselTypeWithFallback(committed, rec);
                Color markerColor = MapMarkerRenderer.GetColorForType(vtype);
                MapMarkerRenderer.DrawMarker(worldPos, rec.VesselName ?? "(unknown)", markerColor, vtype);
            }
        }

        /// <summary>
        /// Resolve VesselType for a recording. If the recording has no VesselSnapshot,
        /// searches other recordings of the same vessel (by VesselPersistentId) for a snapshot.
        /// Ensures consistent icon type across chain recordings of the same vessel.
        /// O(n) scan per call — acceptable for small committed recording counts (typically under 30).
        /// </summary>
        private static VesselType ResolveVesselTypeWithFallback(List<Recording> committed, Recording rec)
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

        void OnDestroy()
        {
            GhostMapPresence.RemoveAllGhostVessels("tracking-station-cleanup");
            if (testRunnerWindowHasInputLock)
                InputLockManager.RemoveControlLock(TestRunnerInputLockId);
            ParsekLog.Info(Tag, "ParsekTrackingStation destroyed");
        }

        #region Test Runner (standalone — no main Parsek UI in this scene)

        private bool showTestRunnerWindow;
        private Rect testRunnerWindowRect;
        private Vector2 testRunnerScrollPos;
        private InGameTestRunner testRunner;
        private bool testRunnerWindowHasInputLock;
        private const string TestRunnerInputLockId = "Parsek_TestRunnerWindow_TS";
        private readonly HashSet<string> expandedCategories = new HashSet<string>();
        private List<KeyValuePair<string, List<InGameTestInfo>>> cachedGroups;
        private bool wasRunning;
        private GUIStyle opaqueStyle;

        private void DrawTestRunnerIfOpen()
        {
            if (!showTestRunnerWindow) return;

            if (testRunner == null)
            {
                testRunner = new InGameTestRunner(this);
                foreach (var t in testRunner.Tests)
                    expandedCategories.Add(t.Category);
                RebuildGroups();
            }

            if (testRunnerWindowRect.width < 1f)
                testRunnerWindowRect = new Rect(20, 60, 380, 400);

            if (opaqueStyle == null)
            {
                opaqueStyle = new GUIStyle(GUI.skin.window);
                var bg = new Texture2D(1, 1);
                bg.SetPixel(0, 0, new Color(0.15f, 0.15f, 0.15f, 1f));
                bg.Apply();
                opaqueStyle.normal.background = bg;
                opaqueStyle.onNormal.background = bg;
            }

            testRunnerWindowRect = ClickThruBlocker.GUILayoutWindow(
                "ParsekTestRunnerTS".GetHashCode(),
                testRunnerWindowRect,
                DrawTestRunnerWindow,
                "Parsek \u2014 Test Runner",
                opaqueStyle,
                GUILayout.Width(380));

            if (testRunnerWindowRect.Contains(Event.current.mousePosition))
            {
                if (!testRunnerWindowHasInputLock)
                {
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, TestRunnerInputLockId);
                    testRunnerWindowHasInputLock = true;
                }
            }
            else if (testRunnerWindowHasInputLock)
            {
                InputLockManager.RemoveControlLock(TestRunnerInputLockId);
                testRunnerWindowHasInputLock = false;
            }
        }

        private void DrawTestRunnerWindow(int windowID)
        {
            if (testRunner == null) { GUI.DragWindow(); return; }

            bool running = testRunner.IsRunning;
            if (cachedGroups == null || (wasRunning && !running))
                RebuildGroups();
            wasRunning = running;

            GUILayout.BeginHorizontal();
            GUI.enabled = !running;
            if (GUILayout.Button("Run All")) { testRunner.ResetResults(); testRunner.RunAll(); }
            if (GUILayout.Button("Reset")) { testRunner.ResetResults(); }
            GUI.enabled = running;
            if (GUILayout.Button("Cancel")) { testRunner.Cancel(); }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            string status = running ? "RUNNING" : "idle";
            GUILayout.Label(
                $"{status} | {testRunner.Passed} passed  {testRunner.Failed} failed  {testRunner.Skipped} skipped  ({testRunner.Tests.Count} total)",
                GUI.skin.box);
            GUILayout.Label($"Scene: {HighLogic.LoadedScene}");

            testRunnerScrollPos = GUILayout.BeginScrollView(testRunnerScrollPos,
                GUILayout.MinHeight(200), GUILayout.MaxHeight(500));

            foreach (var group in cachedGroups)
            {
                var cat = group.Key;
                var tests = group.Value;
                bool expanded = expandedCategories.Contains(cat);
                int catPassed = 0, catFailed = 0;
                foreach (var t in tests)
                {
                    if (t.Status == TestStatus.Passed) catPassed++;
                    else if (t.Status == TestStatus.Failed) catFailed++;
                }

                GUILayout.BeginHorizontal();
                string arrow = expanded ? "\u25bc" : "\u25b6";
                string summary = catFailed > 0
                    ? $" ({catPassed}/{tests.Count}, {catFailed} failed)"
                    : $" ({catPassed}/{tests.Count})";
                if (GUILayout.Button($"{arrow} {cat}{summary}", GUI.skin.label))
                {
                    if (expanded) expandedCategories.Remove(cat);
                    else expandedCategories.Add(cat);
                }
                GUI.enabled = !running;
                if (GUILayout.Button("Run", GUILayout.Width(40)))
                {
                    testRunner.ResetCategory(cat);
                    testRunner.RunCategory(cat);
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();

                if (!expanded) continue;

                foreach (var test in tests)
                {
                    bool eligible = test.RequiredScene == InGameTestAttribute.AnyScene
                        || test.RequiredScene == HighLogic.LoadedScene;

                    GUILayout.BeginHorizontal();
                    GUILayout.Space(16);
                    var prevColor = GUI.contentColor;
                    GUI.contentColor = GetStatusColor(test.Status);
                    GUILayout.Label(GetStatusIcon(test.Status), GUILayout.Width(20));
                    GUI.contentColor = prevColor;

                    if (!eligible) GUI.enabled = false;
                    string label = test.Method.Name;
                    if (test.DurationMs > 0) label += $" ({test.DurationMs:F0}ms)";
                    GUILayout.Label(new GUIContent(label, test.Description ?? ""));
                    GUI.enabled = true;

                    GUI.enabled = !running && eligible;
                    if (GUILayout.Button("\u25b6", GUILayout.Width(24)))
                    {
                        test.Status = TestStatus.NotRun;
                        test.ErrorMessage = null;
                        testRunner.RunSingle(test);
                    }
                    GUI.enabled = true;
                    GUILayout.EndHorizontal();

                    if (test.Status == TestStatus.Failed && !string.IsNullOrEmpty(test.ErrorMessage))
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Space(40);
                        var prev = GUI.contentColor;
                        GUI.contentColor = Color.red;
                        GUILayout.Label(test.ErrorMessage, GUILayout.MaxWidth(320));
                        GUI.contentColor = prev;
                        GUILayout.EndHorizontal();
                    }
                }
            }

            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Export Results")) testRunner.ExportResultsFile();
            if (GUILayout.Button("Close")) showTestRunnerWindow = false;
            GUILayout.EndHorizontal();
            GUILayout.Label("Ctrl+Shift+T to toggle from any scene", GUI.skin.label);

            if (!string.IsNullOrEmpty(GUI.tooltip))
                GUILayout.Label(GUI.tooltip, GUI.skin.box);

            GUI.DragWindow();
        }

        private void RebuildGroups()
        {
            var groups = new Dictionary<string, List<InGameTestInfo>>();
            foreach (var t in testRunner.Tests)
            {
                if (!groups.TryGetValue(t.Category, out var list))
                {
                    list = new List<InGameTestInfo>();
                    groups[t.Category] = list;
                }
                list.Add(t);
            }
            var sorted = new List<KeyValuePair<string, List<InGameTestInfo>>>(groups);
            sorted.Sort((a, b) => string.Compare(a.Key, b.Key, System.StringComparison.Ordinal));
            cachedGroups = sorted;
        }

        private static string GetStatusIcon(TestStatus s)
        {
            switch (s)
            {
                case TestStatus.Passed:  return "\u2713";
                case TestStatus.Failed:  return "\u2717";
                case TestStatus.Running: return "\u25cb";
                case TestStatus.Skipped: return "\u2013";
                default:                 return "\u00b7";
            }
        }

        private static Color GetStatusColor(TestStatus s)
        {
            switch (s)
            {
                case TestStatus.Passed:  return Color.green;
                case TestStatus.Failed:  return Color.red;
                case TestStatus.Running: return Color.yellow;
                case TestStatus.Skipped: return Color.gray;
                default:                 return Color.white;
            }
        }

        #endregion
    }
}
