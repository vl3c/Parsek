using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Bounded diagnostic for where the stock camera actually lands after a watch exit hands it
    /// back to a vessel. A watch exit can retarget the camera onto a vessel that is packed under
    /// rails warp and far from the ghost, and the player has reported the view ending up inside
    /// the ground there; the restore log line alone says only which vessel was targeted.
    /// Samples a fixed set of frames after the restore and raises one Warn the first frame the
    /// camera sits below the terrain under it.
    /// </summary>
    internal partial class WatchModeController
    {
        internal const int PostExitCameraProbeFrames = 240;
        internal const double PostExitCameraBelowTerrainMarginMeters = 0.5;

        private int postExitCameraProbeFrame = -1;
        private string postExitCameraProbeContext;
        private bool postExitCameraProbeWarned;

        internal bool IsPostExitCameraProbeArmedForTesting => postExitCameraProbeFrame >= 0;

        /// <summary>
        /// Clearance of the camera over the terrain directly below it. "unknown" when either
        /// altitude is not a finite number (no body, no PQS).
        /// </summary>
        internal static string ClassifyPostExitCameraClearance(double cameraAltitude, double terrainAltitude)
        {
            if (double.IsNaN(cameraAltitude) || double.IsInfinity(cameraAltitude)
                || double.IsNaN(terrainAltitude) || double.IsInfinity(terrainAltitude))
                return "unknown";
            return cameraAltitude < terrainAltitude - PostExitCameraBelowTerrainMarginMeters
                ? "below-terrain"
                : "clear";
        }

        /// <summary>Frames (0 = the restore frame) whose sample is always logged.</summary>
        internal static bool IsPostExitCameraProbeLoggedFrame(int frame)
        {
            return frame == 0 || frame == 1 || frame == 10 || frame == 60
                || frame == PostExitCameraProbeFrames;
        }

        private void ArmPostExitCameraProbe(string context)
        {
            postExitCameraProbeFrame = 0;
            postExitCameraProbeContext = context;
            postExitCameraProbeWarned = false;
            TickPostExitCameraProbe();
        }

        private void DisarmPostExitCameraProbe()
        {
            postExitCameraProbeFrame = -1;
            postExitCameraProbeContext = null;
            postExitCameraProbeWarned = false;
        }

        /// <summary>Called once per LateUpdate while no ghost is watched.</summary>
        internal void TickPostExitCameraProbe()
        {
            if (postExitCameraProbeFrame < 0)
                return;

            int frame = postExitCameraProbeFrame;
            string sample = null;
            string clearance = "unknown";
            try
            {
                sample = SamplePostExitCameraCore(out clearance);
            }
            catch (Exception ex)
            {
                // Unit-test host or scene teardown: no camera to sample.
                sample = "unavailable (" + ex.GetType().Name + ")";
            }

            if (clearance == "below-terrain" && !postExitCameraProbeWarned)
            {
                postExitCameraProbeWarned = true;
                ParsekLog.Warn("CameraFollow",
                    $"Post-watch-exit camera BELOW TERRAIN at frame +{frame.ToString(CultureInfo.InvariantCulture)} " +
                    $"({postExitCameraProbeContext ?? "?"}): {sample}");
            }
            else if (IsPostExitCameraProbeLoggedFrame(frame))
            {
                ParsekLog.Verbose("CameraFollow",
                    $"Post-watch-exit camera sample frame=+{frame.ToString(CultureInfo.InvariantCulture)} " +
                    $"unityFrame={GetFrameCountSafe().ToString(CultureInfo.InvariantCulture)} " +
                    $"({postExitCameraProbeContext ?? "?"}): clearance={clearance} {sample}");
            }

            if (frame >= PostExitCameraProbeFrames)
                DisarmPostExitCameraProbe();
            else
                postExitCameraProbeFrame = frame + 1;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string SamplePostExitCameraCore(out string clearance)
        {
            clearance = "unknown";
            FlightCamera flightCamera = FlightCamera.fetch;
            if (flightCamera == null)
                return "camera=null";

            Transform target = flightCamera.Target;
            Vessel targetVessel = target != null ? target.GetComponent<Vessel>() : null;
            Vessel active = FlightGlobals.ActiveVessel;
            CelestialBody body = targetVessel != null ? targetVessel.mainBody
                : (active != null ? active.mainBody : null);

            Vector3d cameraPos = flightCamera.transform.position;
            Vector3d pivotPos = flightCamera.transform.parent != null
                ? (Vector3d)flightCamera.transform.parent.position : cameraPos;
            double cameraAlt = double.NaN;
            double terrainAlt = double.NaN;
            if (body != null)
            {
                cameraAlt = body.GetAltitude(cameraPos);
                double lat = body.GetLatitude(cameraPos);
                double lon = body.GetLongitude(cameraPos);
                terrainAlt = body.TerrainAltitude(lat, lon, true);
            }
            clearance = ClassifyPostExitCameraClearance(cameraAlt, terrainAlt);

            return string.Format(CultureInfo.InvariantCulture,
                "target='{0}' targetVessel='{1}' packed={2} active='{3}' warp={4:F0}x " +
                "cameraAlt={5:F1} terrainAlt={6:F1} pivotAlt={7:F1} pivotToCamera={8:F1}m distance={9:F1} " +
                "pitchDeg={10:F1} sharpness={11:F2}",
                target != null ? target.name : "null",
                targetVessel != null ? targetVessel.vesselName : "-",
                targetVessel != null ? (targetVessel.packed ? "T" : "F") : "-",
                active != null ? active.vesselName : "null",
                TimeWarp.CurrentRate,
                cameraAlt,
                terrainAlt,
                body != null ? body.GetAltitude(pivotPos) : double.NaN,
                (cameraPos - pivotPos).magnitude,
                flightCamera.Distance,
                flightCamera.camPitch * Mathf.Rad2Deg,
                flightCamera.pivotTranslateSharpness);
        }
    }
}
