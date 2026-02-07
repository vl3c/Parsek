using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Parsek Spike Prototype — Phase 2
    ///
    /// Purpose: Validate recording persistence across reverts and
    /// automatic timeline playback at original UT timestamps.
    ///
    /// Controls:
    ///   F9  - Start/Stop recording
    ///   F10 - Start manual playback of current recording (preview)
    ///   F11 - Stop manual playback
    ///
    /// Timeline: Committed recordings auto-play when UT enters their time range.
    /// On revert/scene change, pending recordings trigger a merge dialog.
    ///
    /// This is a throwaway prototype. Delete after validating concept.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ParsekSpike : MonoBehaviour
    {
        #region Data Structures

        /// <summary>
        /// A single point in the recorded trajectory.
        /// Uses geographic coordinates (lat/lon/alt) instead of Unity world coords
        /// because world coords drift over time as celestial bodies move.
        /// </summary>
        public struct TrajectoryPoint
        {
            public double ut;           // Universal Time when recorded
            public double latitude;
            public double longitude;
            public double altitude;
            public Quaternion rotation;
            public Vector3 velocity;    // Surface-relative velocity
            public string bodyName;     // Reference celestial body

            // Career mode resources (absolute values at this tick)
            public double funds;
            public float science;
            public float reputation;

            public override string ToString()
            {
                return $"UT={ut:F1} lat={latitude:F4} lon={longitude:F4} alt={altitude:F1}";
            }
        }

        #endregion

        #region State

        // Recording state
        internal List<TrajectoryPoint> recording = new List<TrajectoryPoint>();
        private bool isRecording = false;
        private float sampleInterval = 0.5f; // seconds between samples

        // Manual playback state (F10/F11 preview of current recording)
        private bool isPlaying = false;
        private double playbackStartUT;
        private double recordingStartUT;
        private GameObject ghostObject;
        private Material ghostMaterial;
        internal int lastPlaybackIndex = 0;

        // Timeline ghost state (auto-playback of committed recordings)
        private Dictionary<int, GameObject> timelineGhosts = new Dictionary<int, GameObject>();
        private Dictionary<int, Material> timelineGhostMaterials = new Dictionary<int, Material>();
        private Dictionary<int, int> timelinePlaybackIndices = new Dictionary<int, int>();

        // UI
        private Rect windowRect = new Rect(20, 100, 250, 250);
        private bool showUI = true;

        #endregion

        #region Unity Lifecycle

        void Start()
        {
            Log("Parsek Spike loaded (Phase 2 — timeline persistence).");
            Log("Press F9 to record, F10 to preview playback.");

            GameEvents.onGameSceneLoadRequested.Add(OnSceneChangeRequested);
            GameEvents.onFlightReady.Add(OnFlightReady);
        }

        void Update()
        {
            HandleInput();

            if (isPlaying)
            {
                UpdatePlayback();
            }

            UpdateTimelinePlayback();
        }

        void OnGUI()
        {
            if (showUI)
            {
                windowRect = GUILayout.Window(
                    GetInstanceID(),
                    windowRect,
                    DrawWindow,
                    "Parsek Spike",
                    GUILayout.Width(250)
                );
            }
        }

        void OnDestroy()
        {
            // Unregister events to prevent handler leaks
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneChangeRequested);
            GameEvents.onFlightReady.Remove(OnFlightReady);

            // Clean up recording if active
            if (isRecording)
            {
                CancelInvoke(nameof(SamplePosition));
                isRecording = false;
            }
            StopPlayback();
            DestroyAllTimelineGhosts();
        }

        #endregion

        #region Scene Change Handling

        void OnSceneChangeRequested(GameScenes scene)
        {
            Log($"Scene change requested: {scene}");

            // If we have recording data and are leaving flight, stash it
            if (recording.Count > 0)
            {
                // Stop recording if active
                if (isRecording)
                {
                    CancelInvoke(nameof(SamplePosition));
                    isRecording = false;
                    Log("Auto-stopped recording due to scene change");
                }

                string vesselName = FlightGlobals.ActiveVessel != null
                    ? FlightGlobals.ActiveVessel.vesselName
                    : "Unknown Vessel";

                RecordingStore.StashPending(recording, vesselName);
                recording.Clear();
                lastPlaybackIndex = 0;
            }

            // Stop manual playback
            StopPlayback();
            DestroyAllTimelineGhosts();
        }

        void OnFlightReady()
        {
            Log("Flight ready. Checking for pending recordings...");

            if (RecordingStore.HasPending)
            {
                var pending = RecordingStore.Pending;
                Log($"Found pending recording from {pending.VesselName} ({pending.Points.Count} points)");
                ShowMergeDialog(pending);
            }

            Log($"Timeline has {RecordingStore.CommittedRecordings.Count} committed recording(s)");
        }

        void ShowMergeDialog(RecordingStore.Recording pending)
        {
            double duration = pending.EndUT - pending.StartUT;
            string message = $"You have a recording from your previous flight.\n\n" +
                $"Vessel: {pending.VesselName}\n" +
                $"Points: {pending.Points.Count}\n" +
                $"Duration: {duration:F1}s\n\n" +
                $"Merge to timeline for auto-playback?";

            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekMerge",
                    message,
                    "Parsek — Merge Recording",
                    HighLogic.UISkin,
                    new DialogGUIButton("Merge to Timeline", () =>
                    {
                        RecordingStore.CommitPending();
                        ScreenMessage("Recording merged to timeline!", 3f);
                        Log("User chose: Merge to Timeline");
                    }),
                    new DialogGUIButton("Discard", () =>
                    {
                        RecordingStore.DiscardPending();
                        ScreenMessage("Recording discarded", 2f);
                        Log("User chose: Discard");
                    })
                ),
                false,
                HighLogic.UISkin
            );
        }

        #endregion

        #region Input Handling

        void HandleInput()
        {
            // F9 - Toggle recording
            if (Input.GetKeyDown(KeyCode.F9))
            {
                if (!isRecording)
                    StartRecording();
                else
                    StopRecording();
            }

            // F10 - Start manual playback (preview current recording)
            if (Input.GetKeyDown(KeyCode.F10))
            {
                if (!isRecording && recording.Count > 0 && !isPlaying)
                {
                    StartPlayback();
                }
            }

            // F11 - Stop manual playback
            if (Input.GetKeyDown(KeyCode.F11))
            {
                if (isPlaying)
                {
                    StopPlayback();
                }
            }

            // Alt+P - Toggle UI
            if (Input.GetKeyDown(KeyCode.P) &&
                (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)))
            {
                showUI = !showUI;
            }
        }

        #endregion

        #region Recording

        void StartRecording()
        {
            if (FlightGlobals.ActiveVessel == null)
            {
                Log("No active vessel to record!");
                return;
            }

            recording.Clear();
            isRecording = true;
            recordingStartUT = Planetarium.GetUniversalTime();

            // Start sampling
            InvokeRepeating(nameof(SamplePosition), 0f, sampleInterval);

            Log($"Recording started. Sampling every {sampleInterval}s");
            ScreenMessage("Recording STARTED", 2f);
        }

        void StopRecording()
        {
            CancelInvoke(nameof(SamplePosition));
            isRecording = false;

            double duration = recording.Count > 0
                ? recording[recording.Count - 1].ut - recording[0].ut
                : 0;

            Log($"Recording stopped. {recording.Count} points over {duration:F1}s");
            ScreenMessage($"Recording STOPPED: {recording.Count} points", 3f);
        }

        void SamplePosition()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;

            TrajectoryPoint point = new TrajectoryPoint
            {
                ut = Planetarium.GetUniversalTime(),
                latitude = v.latitude,
                longitude = v.longitude,
                altitude = v.altitude,
                rotation = v.transform.rotation,
                velocity = v.GetSrfVelocity(),
                bodyName = v.mainBody.name,
                funds = Funding.Instance != null ? Funding.Instance.Funds : 0,
                science = ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science : 0,
                reputation = Reputation.Instance != null ? Reputation.CurrentRep : 0
            };

            recording.Add(point);

            // Debug: Log every 10th point
            if (recording.Count % 10 == 0)
            {
                Log($"Recorded point #{recording.Count}: {point}");
            }
        }

        #endregion

        #region Manual Playback (F10/F11 preview)

        void StartPlayback()
        {
            if (recording.Count < 2)
            {
                Log("Not enough recording points for playback!");
                return;
            }

            ghostObject = CreateGhostSphere("Parsek_Ghost_Preview", Color.green);
            ghostMaterial = ghostObject.GetComponent<Renderer>()?.material;

            // Replay from current time (ghost starts at recording start position)
            playbackStartUT = Planetarium.GetUniversalTime();
            recordingStartUT = recording[0].ut;
            lastPlaybackIndex = 0;

            isPlaying = true;

            Log($"Manual playback started. Duration: {recording[recording.Count - 1].ut - recording[0].ut:F1}s");
            ScreenMessage("Preview Playback STARTED", 2f);
        }

        void StopPlayback()
        {
            isPlaying = false;

            if (ghostMaterial != null)
            {
                Destroy(ghostMaterial);
                ghostMaterial = null;
            }

            if (ghostObject != null)
            {
                Destroy(ghostObject);
                ghostObject = null;
            }

            Log("Manual playback stopped");
        }

        void UpdatePlayback()
        {
            if (ghostObject == null || recording.Count < 2)
            {
                StopPlayback();
                return;
            }

            double currentUT = Planetarium.GetUniversalTime();
            double elapsedSinceStart = currentUT - playbackStartUT;
            double recordingTime = recordingStartUT + elapsedSinceStart;

            if (recordingTime > recording[recording.Count - 1].ut)
            {
                Log("Manual playback complete — reached end of recording");
                StopPlayback();
                ScreenMessage("Preview playback complete", 2f);
                return;
            }

            InterpolateAndPosition(ghostObject, recording, ref lastPlaybackIndex, recordingTime);
        }

        #endregion

        #region Timeline Auto-Playback

        void UpdateTimelinePlayback()
        {
            var committed = RecordingStore.CommittedRecordings;
            if (committed.Count == 0) return;

            double currentUT = Planetarium.GetUniversalTime();

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec.Points.Count < 2) continue;

                bool inRange = currentUT >= rec.StartUT && currentUT <= rec.EndUT;

                if (inRange)
                {
                    // Spawn ghost if not yet active
                    if (!timelineGhosts.ContainsKey(i) || timelineGhosts[i] == null)
                    {
                        SpawnTimelineGhost(i, rec);
                    }

                    // Position the ghost
                    int playbackIdx = 0;
                    if (timelinePlaybackIndices.ContainsKey(i))
                        playbackIdx = timelinePlaybackIndices[i];

                    InterpolateAndPosition(timelineGhosts[i], rec.Points, ref playbackIdx, currentUT);
                    timelinePlaybackIndices[i] = playbackIdx;

                    // Apply resource deltas for any points we've passed since last frame
                    ApplyResourceDeltas(rec, currentUT);
                }
                else
                {
                    // Outside time range — despawn if active
                    if (timelineGhosts.ContainsKey(i) && timelineGhosts[i] != null)
                    {
                        DestroyTimelineGhost(i);
                    }
                }
            }
        }

        /// <summary>
        /// Apply resource deltas (funds, science, reputation) for recorded points
        /// that we've passed since the last frame. Computes delta between consecutive
        /// points and adds it to the current career pools.
        /// </summary>
        void ApplyResourceDeltas(RecordingStore.Recording rec, double currentUT)
        {
            var points = rec.Points;

            // Find the highest point index whose UT we've passed
            int targetIndex = rec.LastAppliedResourceIndex;
            for (int j = rec.LastAppliedResourceIndex + 1; j < points.Count; j++)
            {
                if (points[j].ut <= currentUT)
                    targetIndex = j;
                else
                    break;
            }

            // Apply deltas for each point we've newly passed
            if (targetIndex > rec.LastAppliedResourceIndex)
            {
                int startIdx = Math.Max(rec.LastAppliedResourceIndex, 0);
                TrajectoryPoint fromPoint = points[startIdx];
                TrajectoryPoint toPoint = points[targetIndex];

                double fundsDelta = toPoint.funds - fromPoint.funds;
                float scienceDelta = toPoint.science - fromPoint.science;
                float repDelta = toPoint.reputation - fromPoint.reputation;

                if (fundsDelta != 0 && Funding.Instance != null)
                {
                    Funding.Instance.AddFunds(fundsDelta, TransactionReasons.None);
                    Log($"Timeline resource: funds {fundsDelta:+0.0;-0.0}");
                }

                if (scienceDelta != 0 && ResearchAndDevelopment.Instance != null)
                {
                    ResearchAndDevelopment.Instance.AddScience(scienceDelta, TransactionReasons.None);
                    Log($"Timeline resource: science {scienceDelta:+0.0;-0.0}");
                }

                if (repDelta != 0 && Reputation.Instance != null)
                {
                    Reputation.Instance.AddReputation(repDelta, TransactionReasons.None);
                    Log($"Timeline resource: reputation {repDelta:+0.0;-0.0}");
                }

                rec.LastAppliedResourceIndex = targetIndex;
            }
        }

        void SpawnTimelineGhost(int index, RecordingStore.Recording rec)
        {
            Log($"Spawning timeline ghost #{index} for {rec.VesselName}");

            // Use a slightly different color to distinguish from manual preview
            Color ghostColor = new Color(0.2f, 1f, 0.4f, 0.8f); // bright green-cyan
            GameObject ghost = CreateGhostSphere($"Parsek_Timeline_{index}", ghostColor);

            timelineGhosts[index] = ghost;
            timelineGhostMaterials[index] = ghost.GetComponent<Renderer>()?.material;
            timelinePlaybackIndices[index] = 0;
        }

        void DestroyTimelineGhost(int index)
        {
            Log($"Despawning timeline ghost #{index}");

            if (timelineGhostMaterials.ContainsKey(index) && timelineGhostMaterials[index] != null)
            {
                Destroy(timelineGhostMaterials[index]);
                timelineGhostMaterials.Remove(index);
            }

            if (timelineGhosts.ContainsKey(index) && timelineGhosts[index] != null)
            {
                Destroy(timelineGhosts[index]);
                timelineGhosts.Remove(index);
            }

            timelinePlaybackIndices.Remove(index);
        }

        void DestroyAllTimelineGhosts()
        {
            // Copy keys to avoid modifying during iteration
            var keys = new List<int>(timelineGhosts.Keys);
            foreach (int key in keys)
            {
                DestroyTimelineGhost(key);
            }
        }

        #endregion

        #region Ghost Positioning (shared by manual + timeline playback)

        GameObject CreateGhostSphere(string name, Color color)
        {
            GameObject ghost = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ghost.name = name;
            ghost.transform.localScale = Vector3.one * 12f; // 12m diameter

            Collider collider = ghost.GetComponent<Collider>();
            if (collider != null)
                collider.enabled = false;

            Renderer renderer = ghost.GetComponent<Renderer>();
            if (renderer != null)
            {
                Shader shader = Shader.Find("KSP/Emissive/Diffuse");
                if (shader != null)
                {
                    Material mat = new Material(shader);
                    mat.color = color;
                    mat.SetColor("_EmissiveColor", color);
                    renderer.material = mat;
                }
                else
                {
                    Log("Warning: Could not find KSP/Emissive/Diffuse shader, using default");
                }
            }

            return ghost;
        }

        void InterpolateAndPosition(GameObject ghost, List<TrajectoryPoint> points, ref int cachedIndex, double targetUT)
        {
            int indexBefore = FindWaypointIndex(points, ref cachedIndex, targetUT);

            if (indexBefore < 0)
            {
                PositionGhostAt(ghost, points[0]);
                return;
            }

            TrajectoryPoint before = points[indexBefore];
            TrajectoryPoint after = points[indexBefore + 1];

            double segmentDuration = after.ut - before.ut;
            if (segmentDuration <= 0.0001)
            {
                PositionGhostAt(ghost, before);
                return;
            }

            float t = (float)((targetUT - before.ut) / segmentDuration);
            t = Mathf.Clamp01(t);

            CelestialBody body = FlightGlobals.Bodies.Find(b => b.name == before.bodyName);
            if (body == null)
            {
                Log($"Could not find body: {before.bodyName}");
                return;
            }

            Vector3 posBefore = body.GetWorldSurfacePosition(
                before.latitude, before.longitude, before.altitude);
            Vector3 posAfter = body.GetWorldSurfacePosition(
                after.latitude, after.longitude, after.altitude);

            Vector3 interpolatedPos = Vector3.Lerp(posBefore, posAfter, t);
            Quaternion interpolatedRot = Quaternion.Slerp(before.rotation, after.rotation, t);

            interpolatedRot = SanitizeQuaternion(interpolatedRot);

            if (float.IsNaN(interpolatedPos.x) || float.IsNaN(interpolatedPos.y) || float.IsNaN(interpolatedPos.z))
            {
                Log("Warning: NaN in interpolated position, using 'before' position");
                interpolatedPos = posBefore;
            }

            ghost.transform.position = interpolatedPos;
            ghost.transform.rotation = interpolatedRot;
        }

        /// <summary>
        /// Find the waypoint index for interpolation using cached lookup.
        /// Parameterized to work with any point list + cached index.
        /// </summary>
        internal int FindWaypointIndex(List<TrajectoryPoint> points, ref int cachedIndex, double targetUT)
        {
            if (points.Count < 2)
                return -1;

            if (targetUT < points[0].ut)
                return -1;

            if (targetUT >= points[points.Count - 1].ut)
                return points.Count - 2;

            // Try cached index first (common case: sequential playback)
            if (cachedIndex >= 0 && cachedIndex < points.Count - 1)
            {
                if (points[cachedIndex].ut <= targetUT &&
                    points[cachedIndex + 1].ut > targetUT)
                {
                    return cachedIndex;
                }

                int nextIndex = cachedIndex + 1;
                if (nextIndex < points.Count - 1 &&
                    points[nextIndex].ut <= targetUT &&
                    points[nextIndex + 1].ut > targetUT)
                {
                    cachedIndex = nextIndex;
                    return nextIndex;
                }
            }

            // Binary search fallback
            int low = 0;
            int high = points.Count - 2;

            while (low <= high)
            {
                int mid = (low + high) / 2;

                if (points[mid].ut <= targetUT && points[mid + 1].ut > targetUT)
                {
                    cachedIndex = mid;
                    return mid;
                }
                else if (points[mid].ut > targetUT)
                {
                    high = mid - 1;
                }
                else
                {
                    low = mid + 1;
                }
            }

            // Linear fallback (shouldn't reach here)
            for (int i = 0; i < points.Count - 1; i++)
            {
                if (points[i].ut <= targetUT && points[i + 1].ut > targetUT)
                {
                    cachedIndex = i;
                    return i;
                }
            }

            return -1;
        }

        // Keep the old signature for backward compat with tests
        internal int FindWaypointIndex(double targetUT)
        {
            return FindWaypointIndex(recording, ref lastPlaybackIndex, targetUT);
        }

        void PositionGhostAt(GameObject ghost, TrajectoryPoint point)
        {
            CelestialBody body = FlightGlobals.Bodies.Find(b => b.name == point.bodyName);
            if (body == null) return;

            Vector3 worldPos = body.GetWorldSurfacePosition(
                point.latitude, point.longitude, point.altitude);

            ghost.transform.position = worldPos;
            ghost.transform.rotation = SanitizeQuaternion(point.rotation);
        }

        internal Quaternion SanitizeQuaternion(Quaternion q)
        {
            if (float.IsNaN(q.x) || float.IsInfinity(q.x)) q.x = 0;
            if (float.IsNaN(q.y) || float.IsInfinity(q.y)) q.y = 0;
            if (float.IsNaN(q.z) || float.IsInfinity(q.z)) q.z = 0;
            if (float.IsNaN(q.w) || float.IsInfinity(q.w)) q.w = 1;

            float magnitude = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (float.IsNaN(magnitude) || float.IsInfinity(magnitude) || magnitude < 0.001f)
            {
                return Quaternion.identity;
            }

            return new Quaternion(q.x / magnitude, q.y / magnitude, q.z / magnitude, q.w / magnitude);
        }

        #endregion

        #region UI

        void DrawWindow(int windowID)
        {
            GUILayout.BeginVertical();

            // Status
            GUILayout.Label($"Status: {GetStatusText()}");
            GUILayout.Space(5);

            // Recording info
            GUILayout.Label($"Recorded Points: {recording.Count}");
            if (recording.Count > 0)
            {
                double duration = recording[recording.Count - 1].ut - recording[0].ut;
                GUILayout.Label($"Duration: {duration:F1}s");
            }

            // Timeline info
            int committedCount = RecordingStore.CommittedRecordings.Count;
            int activeGhosts = timelineGhosts.Count;
            GUILayout.Label($"Timeline: {committedCount} recording(s), {activeGhosts} active ghost(s)");

            GUILayout.Space(10);

            // Controls
            GUILayout.Label("Controls:");
            GUILayout.Label("  F9  - Start/Stop Recording");
            GUILayout.Label("  F10 - Preview Playback");
            GUILayout.Label("  F11 - Stop Preview");
            GUILayout.Label("  Alt+P - Toggle this window");

            GUILayout.Space(10);

            // Buttons
            GUILayout.BeginHorizontal();

            if (!isRecording)
            {
                if (GUILayout.Button("Start Recording"))
                    StartRecording();
            }
            else
            {
                if (GUILayout.Button("Stop Recording"))
                    StopRecording();
            }

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();

            GUI.enabled = !isRecording && recording.Count > 0 && !isPlaying;
            if (GUILayout.Button("Preview Playback"))
                StartPlayback();

            GUI.enabled = isPlaying;
            if (GUILayout.Button("Stop Preview"))
                StopPlayback();

            GUI.enabled = true;

            GUILayout.EndHorizontal();

            // Clear buttons
            GUILayout.Space(5);
            GUI.enabled = !isRecording && !isPlaying;
            if (GUILayout.Button("Clear Current Recording"))
            {
                recording.Clear();
                lastPlaybackIndex = 0;
                Log("Recording cleared");
            }

            GUI.enabled = committedCount > 0;
            if (GUILayout.Button($"Clear Timeline ({committedCount})"))
            {
                DestroyAllTimelineGhosts();
                RecordingStore.CommittedRecordings.Clear();
                Log("Timeline cleared");
                ScreenMessage("Timeline cleared", 2f);
            }
            GUI.enabled = true;

            GUILayout.EndVertical();

            // Make window draggable
            GUI.DragWindow();
        }

        string GetStatusText()
        {
            if (isRecording) return "RECORDING";
            if (isPlaying) return "PREVIEWING";
            if (recording.Count > 0) return "Ready (has recording)";
            return "Idle";
        }

        #endregion

        #region Utilities

        void Log(string message)
        {
            Debug.Log($"[Parsek Spike] {message}");
        }

        void ScreenMessage(string message, float duration)
        {
            ScreenMessages.PostScreenMessage(
                $"[Parsek] {message}",
                duration,
                ScreenMessageStyle.UPPER_CENTER
            );
        }

        #endregion
    }
}
