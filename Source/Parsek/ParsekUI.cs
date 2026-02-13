using System.Collections.Generic;
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
                string name = kvp.Key < committed.Count ? committed[kvp.Key].VesselName : "Ghost";
                DrawMapMarkerAt(cam, kvp.Value.transform.position, name, ghostColor);
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
