using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Parsek Spike Prototype
    ///
    /// Purpose: Validate the core concept of recording vessel trajectory
    /// and playing it back as a ghost vessel.
    ///
    /// Controls:
    ///   F9  - Start/Stop recording
    ///   F10 - Start playback (after recording)
    ///   F11 - Stop playback
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

        // Playback state
        private bool isPlaying = false;
        private double playbackStartUT;
        private double recordingStartUT;
        private GameObject ghostObject;
        private Material ghostMaterial; // Track material for cleanup
        internal int lastPlaybackIndex = 0; // Cached index for O(1) lookups

        // UI
        private Rect windowRect = new Rect(20, 100, 250, 200);
        private bool showUI = true;

        #endregion

        #region Unity Lifecycle

        void Start()
        {
            Log("Parsek Spike loaded. Press F9 to record, F10 to playback.");
        }

        void Update()
        {
            HandleInput();

            if (isPlaying)
            {
                UpdatePlayback();
            }
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
            // Clean up recording if active
            if (isRecording)
            {
                CancelInvoke(nameof(SamplePosition));
                isRecording = false;
            }
            StopPlayback();
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

            // F10 - Start playback
            if (Input.GetKeyDown(KeyCode.F10))
            {
                if (!isRecording && recording.Count > 0 && !isPlaying)
                {
                    StartPlayback();
                }
            }

            // F11 - Stop playback
            if (Input.GetKeyDown(KeyCode.F11))
            {
                if (isPlaying)
                {
                    StopPlayback();
                }
            }

            // P - Toggle UI
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
                bodyName = v.mainBody.name
            };

            recording.Add(point);

            // Debug: Log every 10th point
            if (recording.Count % 10 == 0)
            {
                Log($"Recorded point #{recording.Count}: {point}");
            }
        }

        #endregion

        #region Playback

        void StartPlayback()
        {
            if (recording.Count < 2)
            {
                Log("Not enough recording points for playback!");
                return;
            }

            // Create ghost object - a simple green sphere
            ghostObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ghostObject.name = "Parsek_Ghost";
            ghostObject.transform.localScale = Vector3.one * 12f; // 12m diameter for visibility

            // Disable collider so it doesn't interfere with physics
            Collider collider = ghostObject.GetComponent<Collider>();
            if (collider != null)
                collider.enabled = false;

            // Set up material - bright green, emissive so visible in shadow
            Renderer renderer = ghostObject.GetComponent<Renderer>();
            if (renderer != null)
            {
                Shader shader = Shader.Find("KSP/Emissive/Diffuse");
                if (shader != null)
                {
                    ghostMaterial = new Material(shader);
                    ghostMaterial.color = Color.green;
                    ghostMaterial.SetColor("_EmissiveColor", Color.green);
                    renderer.material = ghostMaterial;
                }
                else
                {
                    Log("Warning: Could not find KSP/Emissive/Diffuse shader, using default");
                }
            }

            // Set playback timing
            // Replay from current time (ghost starts at recording start position)
            playbackStartUT = Planetarium.GetUniversalTime();
            recordingStartUT = recording[0].ut;
            lastPlaybackIndex = 0; // Reset cached index

            isPlaying = true;

            Log($"Playback started. Duration: {recording[recording.Count - 1].ut - recording[0].ut:F1}s");
            ScreenMessage("Playback STARTED", 2f);
        }

        void StopPlayback()
        {
            isPlaying = false;

            // Clean up material to prevent leak
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

            Log("Playback stopped");
            ScreenMessage("Playback STOPPED", 2f);
        }

        void UpdatePlayback()
        {
            if (ghostObject == null || recording.Count < 2)
            {
                StopPlayback();
                return;
            }

            // Calculate what time we should be at in the recording
            double currentUT = Planetarium.GetUniversalTime();
            double elapsedSinceStart = currentUT - playbackStartUT;
            double recordingTime = recordingStartUT + elapsedSinceStart;

            // Check if playback is complete
            if (recordingTime > recording[recording.Count - 1].ut)
            {
                Log("Playback complete - reached end of recording");
                StopPlayback();
                return;
            }

            // Interpolate position at current recording time
            // No reference frame correction needed - we recompute from lat/lon/alt each frame
            // which automatically gives us correct world coordinates
            InterpolateAndPosition(recordingTime);
        }

        void InterpolateAndPosition(double targetUT)
        {
            // Find the two waypoints surrounding targetUT
            // Use cached index for O(1) lookup in common case (sequential playback)
            int indexBefore = FindWaypointIndex(targetUT);

            // Handle edge cases
            if (indexBefore < 0)
            {
                // Before first point - use first point
                PositionGhostAt(recording[0]);
                return;
            }

            TrajectoryPoint before = recording[indexBefore];
            TrajectoryPoint after = recording[indexBefore + 1];

            // Calculate interpolation factor (0 to 1)
            double segmentDuration = after.ut - before.ut;

            // Guard against division by zero (duplicate timestamps)
            if (segmentDuration <= 0.0001)
            {
                PositionGhostAt(before);
                return;
            }

            float t = (float)((targetUT - before.ut) / segmentDuration);
            t = Mathf.Clamp01(t);

            // Get celestial body for coordinate conversion
            CelestialBody body = FlightGlobals.Bodies.Find(b => b.name == before.bodyName);
            if (body == null)
            {
                Log($"Could not find body: {before.bodyName}");
                return;
            }

            // Convert geographic coords to world positions
            // This automatically handles floating origin - no manual correction needed
            Vector3 posBefore = body.GetWorldSurfacePosition(
                before.latitude, before.longitude, before.altitude);
            Vector3 posAfter = body.GetWorldSurfacePosition(
                after.latitude, after.longitude, after.altitude);

            // Interpolate position and rotation
            Vector3 interpolatedPos = Vector3.Lerp(posBefore, posAfter, t);
            Quaternion interpolatedRot = Quaternion.Slerp(before.rotation, after.rotation, t);

            // Sanitize quaternion (protect against NaN)
            interpolatedRot = SanitizeQuaternion(interpolatedRot);

            // Sanitize position (protect against NaN)
            if (float.IsNaN(interpolatedPos.x) || float.IsNaN(interpolatedPos.y) || float.IsNaN(interpolatedPos.z))
            {
                Log("Warning: NaN in interpolated position, using 'before' position");
                interpolatedPos = posBefore;
            }

            // Apply to ghost
            ghostObject.transform.position = interpolatedPos;
            ghostObject.transform.rotation = interpolatedRot;
        }

        /// <summary>
        /// Find the waypoint index for interpolation using cached lookup.
        /// Returns index of the point BEFORE targetUT, or -1 if before first point or recording too small.
        /// </summary>
        internal int FindWaypointIndex(double targetUT)
        {
            // Guard: need at least 2 points for interpolation
            if (recording.Count < 2)
                return -1;

            // Early out: before first point
            if (targetUT < recording[0].ut)
                return -1;

            // Early out: at or after last point
            if (targetUT >= recording[recording.Count - 1].ut)
                return recording.Count - 2;

            // Try cached index first (common case: sequential playback)
            if (lastPlaybackIndex >= 0 && lastPlaybackIndex < recording.Count - 1)
            {
                if (recording[lastPlaybackIndex].ut <= targetUT &&
                    recording[lastPlaybackIndex + 1].ut > targetUT)
                {
                    return lastPlaybackIndex;
                }

                // Try next index (time moved forward)
                int nextIndex = lastPlaybackIndex + 1;
                if (nextIndex < recording.Count - 1 &&
                    recording[nextIndex].ut <= targetUT &&
                    recording[nextIndex + 1].ut > targetUT)
                {
                    lastPlaybackIndex = nextIndex;
                    return nextIndex;
                }
            }

            // Cache miss - do binary search for O(log n) instead of O(n)
            int low = 0;
            int high = recording.Count - 2;

            while (low <= high)
            {
                int mid = (low + high) / 2;

                if (recording[mid].ut <= targetUT && recording[mid + 1].ut > targetUT)
                {
                    lastPlaybackIndex = mid;
                    return mid;
                }
                else if (recording[mid].ut > targetUT)
                {
                    high = mid - 1;
                }
                else
                {
                    low = mid + 1;
                }
            }

            // Shouldn't reach here, but fallback to linear search
            for (int i = 0; i < recording.Count - 1; i++)
            {
                if (recording[i].ut <= targetUT && recording[i + 1].ut > targetUT)
                {
                    lastPlaybackIndex = i;
                    return i;
                }
            }

            return -1;
        }

        void PositionGhostAt(TrajectoryPoint point)
        {
            CelestialBody body = FlightGlobals.Bodies.Find(b => b.name == point.bodyName);
            if (body == null) return;

            Vector3 worldPos = body.GetWorldSurfacePosition(
                point.latitude, point.longitude, point.altitude);

            ghostObject.transform.position = worldPos;
            ghostObject.transform.rotation = SanitizeQuaternion(point.rotation);
        }

        internal Quaternion SanitizeQuaternion(Quaternion q)
        {
            // Protect against NaN and Infinity values that can occur during interpolation
            if (float.IsNaN(q.x) || float.IsInfinity(q.x)) q.x = 0;
            if (float.IsNaN(q.y) || float.IsInfinity(q.y)) q.y = 0;
            if (float.IsNaN(q.z) || float.IsInfinity(q.z)) q.z = 0;
            if (float.IsNaN(q.w) || float.IsInfinity(q.w)) q.w = 1;

            // Normalize to ensure valid quaternion
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
            GUILayout.Space(10);

            // Controls
            GUILayout.Label("Controls:");
            GUILayout.Label("  F9  - Start/Stop Recording");
            GUILayout.Label("  F10 - Start Playback");
            GUILayout.Label("  F11 - Stop Playback");
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
            if (GUILayout.Button("Start Playback"))
                StartPlayback();

            GUI.enabled = isPlaying;
            if (GUILayout.Button("Stop Playback"))
                StopPlayback();

            GUI.enabled = true;

            GUILayout.EndHorizontal();

            // Clear button
            GUILayout.Space(5);
            GUI.enabled = !isRecording && !isPlaying;
            if (GUILayout.Button("Clear Recording"))
            {
                recording.Clear();
                lastPlaybackIndex = 0;
                Log("Recording cleared");
            }
            GUI.enabled = true;

            GUILayout.EndVertical();

            // Make window draggable
            GUI.DragWindow();
        }

        string GetStatusText()
        {
            if (isRecording) return "RECORDING";
            if (isPlaying) return "PLAYING";
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
