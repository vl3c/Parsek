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
            // Breathing room below the title bar — matches Timeline's visual spacing.
            GUILayout.Space(5);

            bool isRecording = flight.IsGloopsRecording;
            bool hasLastRecording = flight.LastGloopsRecording != null;
            bool isPreviewing = flight.IsPlaying;

            // Buttons are drawn in fixed order (primary action / preview / discard)
            // with text and enablement driven by state. Status labels render below
            // so button positions never shift between states.

            string primaryLabel = isRecording
                ? "Stop Recording"
                : (hasLastRecording ? "Start New Recording" : "Start Recording");

            if (GUILayout.Button(primaryLabel))
            {
                ParsekLog.Verbose("UI", "Gloops " + primaryLabel + " clicked");
                if (isRecording)
                {
                    flight.StopGloopsRecording();
                }
                else
                {
                    if (isPreviewing)
                        flight.StopPlayback();
                    flight.StartGloopsRecording();
                }
            }

            bool previewEnabled = hasLastRecording && !isRecording;
            string previewLabel = isPreviewing ? "Stop Preview" : "Preview";
            GUI.enabled = previewEnabled;
            if (GUILayout.Button(previewLabel))
            {
                ParsekLog.Verbose("UI", "Gloops " + previewLabel + " clicked");
                if (isPreviewing)
                    flight.StopPlayback();
                else
                    flight.PreviewGloopsRecording();
            }
            GUI.enabled = true;

            GUILayout.Space(6f);

            bool discardEnabled = isRecording || hasLastRecording;
            GUI.enabled = discardEnabled;
            if (GUILayout.Button("Discard Recording"))
            {
                ParsekLog.Verbose("UI", "Gloops Discard Recording clicked");
                if (isRecording)
                {
                    flight.DiscardGloopsInProgress();
                }
                else
                {
                    if (isPreviewing)
                        flight.StopPlayback();
                    flight.DiscardLastGloopsRecording();
                }
            }
            GUI.enabled = true;

            GUILayout.Space(6f);

            if (isRecording)
            {
                DrawRecordingStatus(flight);
            }
            else if (hasLastRecording)
            {
                var rec = flight.LastGloopsRecording;
                GUILayout.Label($"Saved: \"{rec.VesselName}\"");
                GUILayout.Label($"Points: {rec.Points.Count}");
                double duration = rec.Points.Count > 1
                    ? rec.Points[rec.Points.Count - 1].ut - rec.Points[0].ut
                    : 0;
                GUILayout.Label(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Duration: {0:F1}s", duration));
            }
            else
            {
                string vesselName = FlightGlobals.ActiveVessel != null
                    ? FlightGlobals.ActiveVessel.vesselName
                    : "No vessel";
                GUILayout.Label($"Vessel: {vesselName}");
                GUILayout.Label("Ghost-only - loops by default");
            }

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Close"))
            {
                showWindow = false;
                ReleaseInputLock();
                ParsekLog.Verbose("UI", "Gloops Recorder window closed via button");
            }

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        private void DrawRecordingStatus(ParsekFlight flight)
        {
            GUILayout.Label("Recording...", parentUI.GetSectionHeaderStyle());

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
