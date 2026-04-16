using ClickThroughFix;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Gloops Flight Recorder window — manual ghost-only recording controls.
    /// Runs a parallel FlightRecorder that produces IsGhostOnly recordings
    /// auto-committed to the Gloops group with looping enabled by default.
    /// </summary>
    internal class GloopsRecorderUI
    {
        private readonly ParsekUI parentUI;

        private bool showWindow;
        private Rect windowRect;
        private bool hasInputLock;
        private const string InputLockId = "Parsek_GloopsRecorderWindow";

        private Rect lastWindowRect;

        public bool IsOpen
        {
            get { return showWindow; }
            set { showWindow = value; }
        }

        internal GloopsRecorderUI(ParsekUI parentUI)
        {
            this.parentUI = parentUI;
        }

        public void DrawIfOpen(Rect mainWindowRect, ParsekFlight flight)
        {
            if (!showWindow)
            {
                ReleaseInputLock();
                return;
            }

            if (flight == null)
            {
                showWindow = false;
                ReleaseInputLock();
                return;
            }

            if (windowRect.width < 1f)
            {
                float x = mainWindowRect.x + mainWindowRect.width + 10;
                windowRect = new Rect(x, mainWindowRect.y, 280, 180);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                ParsekLog.Verbose("UI",
                    $"Gloops Recorder window initial position: " +
                    $"x={windowRect.x.ToString("F0", ic)} y={windowRect.y.ToString("F0", ic)}");
            }

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            windowRect = ClickThruBlocker.GUILayoutWindow(
                "ParsekGloopsRecorder".GetHashCode(),
                windowRect,
                (id) => DrawWindow(id, flight),
                "Gloops Flight Recorder",
                opaqueWindowStyle,
                GUILayout.Width(windowRect.width),
                GUILayout.Height(windowRect.height)
            );
            parentUI.LogWindowPosition("GloopsRecorder", ref lastWindowRect, windowRect);

            if (windowRect.Contains(Event.current.mousePosition))
            {
                if (!hasInputLock)
                {
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, InputLockId);
                    hasInputLock = true;
                }
            }
            else
            {
                ReleaseInputLock();
            }
        }

        public void ReleaseInputLock()
        {
            if (!hasInputLock) return;
            InputLockManager.RemoveControlLock(InputLockId);
            hasInputLock = false;
        }

        private void DrawWindow(int windowID, ParsekFlight flight)
        {
            GUILayout.BeginVertical();

            bool isRecording = flight.IsGloopsRecording;
            bool hasLastRecording = flight.LastGloopsRecording != null;
            bool isPreviewing = flight.IsPlaying;

            if (isRecording)
            {
                // --- State B: Recording in progress ---
                DrawRecordingStatus(flight);

                GUILayout.Space(6f);

                if (GUILayout.Button("Stop Recording"))
                {
                    ParsekLog.Verbose("UI", "Gloops Stop Recording clicked");
                    flight.StopGloopsRecording();
                }

                if (GUILayout.Button("Discard"))
                {
                    ParsekLog.Verbose("UI", "Gloops Discard (in-progress) clicked");
                    flight.DiscardGloopsInProgress();
                }
            }
            else if (hasLastRecording)
            {
                // --- State C: Recording just saved ---
                var rec = flight.LastGloopsRecording;
                GUILayout.Label($"Saved: \"{rec.VesselName}\"");
                GUILayout.Label($"Points: {rec.Points.Count}");
                double duration = rec.Points.Count > 1
                    ? rec.Points[rec.Points.Count - 1].ut - rec.Points[0].ut
                    : 0;
                GUILayout.Label(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Duration: {0:F1}s", duration));

                GUILayout.Space(6f);

                if (!isPreviewing)
                {
                    if (GUILayout.Button("Preview"))
                    {
                        ParsekLog.Verbose("UI", "Gloops Preview clicked");
                        flight.PreviewGloopsRecording();
                    }
                }
                else
                {
                    if (GUILayout.Button("Stop Preview"))
                    {
                        ParsekLog.Verbose("UI", "Gloops Stop Preview clicked");
                        flight.StopPlayback();
                    }
                }

                if (GUILayout.Button("Discard Recording"))
                {
                    ParsekLog.Verbose("UI", "Gloops Discard (saved) clicked");
                    if (isPreviewing)
                        flight.StopPlayback();
                    flight.DiscardLastGloopsRecording();
                }

                GUILayout.Space(6f);

                if (GUILayout.Button("Start New Recording"))
                {
                    ParsekLog.Verbose("UI", "Gloops Start New Recording clicked");
                    if (isPreviewing)
                        flight.StopPlayback();
                    flight.StartGloopsRecording();
                }
            }
            else
            {
                // --- State A: Idle ---
                string vesselName = FlightGlobals.ActiveVessel != null
                    ? FlightGlobals.ActiveVessel.vesselName
                    : "No vessel";
                GUILayout.Label($"Vessel: {vesselName}");
                GUILayout.Label("Ghost-only \u2014 loops by default");

                GUILayout.Space(6f);

                if (GUILayout.Button("Start Recording"))
                {
                    ParsekLog.Verbose("UI", "Gloops Start Recording clicked");
                    flight.StartGloopsRecording();
                }
            }

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        private void DrawRecordingStatus(ParsekFlight flight)
        {
            GUILayout.Label("Recording...", GUI.skin.box);

            var gloopsRecorder = flight.GloopsRecorderForUI;
            if (gloopsRecorder != null)
            {
                int pointCount = gloopsRecorder.Recording.Count;
                GUILayout.Label($"Points: {pointCount}");

                if (pointCount > 1)
                {
                    double duration = gloopsRecorder.Recording[pointCount - 1].ut
                        - gloopsRecorder.Recording[0].ut;
                    GUILayout.Label(string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "Duration: {0:F1}s", duration));
                }
            }
        }
    }
}
