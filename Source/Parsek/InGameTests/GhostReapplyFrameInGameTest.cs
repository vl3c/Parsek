using System.Collections;
using System.Globalization;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live guard for the surface-ghost height flicker: stock crew portraits call
    /// <c>Camera.Render()</c> from a coroutine between Update and LateUpdate, which fires
    /// <c>Camera.onPreCull</c> before LateUpdate has reapplied and terrain-clamped the
    /// frame's ghost poses. If that early pre-cull consumes the reapply entries, the frame
    /// renders a buried surface ghost at its raw height while the frames around it render
    /// it clamped, and the ghost jumps up and down at the portrait refresh rate.
    ///
    /// <para>The cell parks a probe object below the PQS surface at the active vessel's
    /// ground track through the production surface-positioning path, renders a throwaway
    /// camera mid-frame on alternate frames (exactly what the portrait coroutine does),
    /// and reads the probe's altitude after each frame has rendered. Every frame must show
    /// the same clamped altitude.</para>
    /// </summary>
    public sealed class GhostReapplyFrameInGameTest
    {
        private const int FrameCount = 8;
        private const double BelowTerrainMeters = 0.4;
        private const double MaxVesselDistanceMeters = 2000.0;
        // ClampGhostsToTerrain skips ghosts above 10 km; stay well under it.
        private const double MaxTerrainAltitudeMeters = 9000.0;

        // Its own category: no committed harness spec pins it, so adding it moves no
        // pinned BATCH_COMPLETE tally.
        [InGameTest(Category = "GhostReapplyFrame", Scene = GameScenes.FLIGHT,
            Description = "A camera rendered between Update and LateUpdate (stock crew portraits) does not strip the LateUpdate terrain clamp from a surface ghost, so its rendered height is the same every frame")]
        public IEnumerator MidFrameCameraRender_SurfaceGhostHeightStableAcrossFrames()
        {
            ParsekFlight flight = ParsekFlight.Instance;
            if (flight == null)
            {
                InGameAssert.Skip("needs a ParsekFlight instance (FLIGHT scene)");
                yield break;
            }

            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null || active.mainBody == null)
            {
                InGameAssert.Skip("needs a FLIGHT scene with an active vessel");
                yield break;
            }

            CelestialBody body = active.mainBody;
            if (body.pqsController == null)
            {
                InGameAssert.Skip(
                    "needs an active vessel over a body with PQS terrain for the ghost terrain clamp, got '"
                    + body.name + "'");
                yield break;
            }

            double lat = active.latitude;
            double lon = active.longitude;
            double terrain = body.TerrainAltitude(lat, lon, true);
            if (terrain > MaxTerrainAltitudeMeters)
            {
                InGameAssert.Skip(
                    "needs terrain below the ghost clamp altitude limit, got "
                    + terrain.ToString("F0", CultureInfo.InvariantCulture) + " m");
                yield break;
            }
            double rawAltitude = terrain - BelowTerrainMeters;
            Vector3d rawWorld = body.GetWorldSurfacePosition(lat, lon, rawAltitude);
            double distance = Vector3d.Distance(rawWorld, active.GetWorldPos3D());
            if (distance > MaxVesselDistanceMeters)
            {
                InGameAssert.Skip(
                    "needs the active vessel within "
                    + MaxVesselDistanceMeters.ToString("F0", CultureInfo.InvariantCulture)
                    + " m of its ground track (landed or low), got "
                    + distance.ToString("F0", CultureInfo.InvariantCulture) + " m");
                yield break;
            }

            double clearance = ParsekFlight.ComputeTerrainClearance(distance);
            double clampedAltitude = terrain + clearance;

            var probe = new GameObject("ParsekGhostReapplyFrameProbe");
            var cameraObject = new GameObject("ParsekGhostReapplyFrameProbeCamera");
            var renderTexture = new RenderTexture(8, 8, 16);
            Camera probeCamera = cameraObject.AddComponent<Camera>();
            probeCamera.enabled = false;
            probeCamera.cullingMask = 0;
            probeCamera.targetTexture = renderTexture;

            var surfacePosition = new SurfacePosition
            {
                body = body.name,
                latitude = lat,
                longitude = lon,
                altitude = rawAltitude,
                rotation = Quaternion.identity,
            };

            var altitudes = new double[FrameCount];
            int midFrameRenders = 0;
            try
            {
                for (int frame = 0; frame < FrameCount; frame++)
                {
                    // Resumes after every Update: ParsekFlight has already cleared last
                    // frame's entries, as playback positioning would have.
                    yield return null;

                    flight.PositionGhostAtSurfaceForInGameTest(
                        probe, surfacePosition, "ingame-ghost-reapply-frame-probe");

                    if (frame % 2 == 1)
                    {
                        probeCamera.Render();
                        midFrameRenders++;
                        InGameAssert.IsTrue(flight.PendingGhostPosEntryCountForInGameTest > 0,
                            "a camera rendered before LateUpdate consumed the ghost reapply entries (frame "
                            + frame + ")");
                    }

                    yield return new WaitForEndOfFrame();
                    altitudes[frame] = body.GetAltitude(probe.transform.position);
                }
            }
            finally
            {
                probeCamera.targetTexture = null;
                Object.Destroy(renderTexture);
                Object.Destroy(cameraObject);
                Object.Destroy(probe);
            }

            double tolerance = System.Math.Max(
                0.05,
                InGameFixtureMath.SceneFloatGridToleranceMeters(
                    System.Math.Abs(((Vector3)rawWorld).magnitude)));
            for (int frame = 0; frame < FrameCount; frame++)
            {
                InGameAssert.ApproxEqual(clampedAltitude, altitudes[frame], tolerance,
                    "frame " + frame + (frame % 2 == 1 ? " (mid-frame camera render)" : "")
                    + " rendered the surface ghost at "
                    + altitudes[frame].ToString("F3", CultureInfo.InvariantCulture)
                    + " m, expected the clamped "
                    + clampedAltitude.ToString("F3", CultureInfo.InvariantCulture)
                    + " m (raw " + rawAltitude.ToString("F3", CultureInfo.InvariantCulture) + " m)");
            }

            ParsekLog.Info("TestRunner",
                "GhostReapplyFrame: " + FrameCount + " frames, " + midFrameRenders
                + " mid-frame camera renders, surface ghost held at clamped altitude "
                + clampedAltitude.ToString("F3", CultureInfo.InvariantCulture) + " m on every frame");
        }
    }
}
