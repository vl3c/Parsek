using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live position check for a STANDALONE per-recording loop (the Recordings-tab Loop toggle,
    /// not a Mission loop unit) on the flight map and in the Tracking Station. Consumer: lane
    /// <c>SLM-1-standalone-loop-map-position</c>, over the <c>standalone-loop-map</c> preset
    /// (<c>Source/Parsek.Tests/Generators/StandaloneLoopMapFixture.cs</c>): a 1500 s ghost-only
    /// circular Kerbin orbit that loops every 4000 s.
    ///
    /// <para><b>The property.</b> Past the first run, the map ghost of a non-overlapping
    /// standalone loop must sit where the flight engine's loop clock puts the replay: on the
    /// RECORDED orbit at <c>StartUT + phase</c>, where <c>phase = (liveUT - StartUT) mod
    /// period</c>, and must not exist at all in the pause between runs
    /// (<c>phase >= duration</c>). The expectation is derived HERE from the recording's own
    /// fields, independently of <c>GhostPlaybackLogic.ResolveTrackingStationSampleUT</c>, and the
    /// ghost's position is compared against an orbit built from the recorded segment, not the
    /// ghost's own OrbitDriver (which cannot tell a wrong sample UT from a right one). Before the
    /// fix the seam returned the live UT for every non-unit recording, so the map ghost either
    /// vanished past the recording's end or sat on the terminal orbit at the live clock, hundreds
    /// of kilometres away (todo RECORDINGS-TAB-LOOP-NEVER-REPLAYS-ON-MAP).</para>
    ///
    /// <para><b>When it reads.</b> At end of frame, after the icon drive (OrbitDriver LateUpdate)
    /// has placed the ghost for this frame's clock, so the live UT read here is the one the drive
    /// used. One cell for both scenes; it skips outside FLIGHT and TRACKSTATION, when the preset
    /// is absent, and in the first run (nothing loop-specific to prove there).</para>
    /// </summary>
    public class StandaloneLoopMapInGameTests
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary><c>StandaloneLoopMapFixture.RecordingId</c> (the test project owns the fixture).</summary>
        internal const string PresetRecordingId = "standaloneloopmap00000000000slm1";

        /// <summary>
        /// Largest allowed angle between the ghost's body-relative position and the recorded
        /// orbit's position at the expected sample UT. At 700 km radius 0.5 deg is about 6 km:
        /// far above float and one-frame clock skew (2.2 km/s at 1x), far below the wrong-clock
        /// signature (the 4000 s shift alone is about 15 deg of this orbit).
        /// </summary>
        internal const double MaxPositionAngleDeg = 0.5;

        /// <summary>Largest allowed radius difference (metres) for the same comparison.</summary>
        internal const double MaxRadiusErrorMeters = 5000.0;

        [InGameTest(Category = "StandaloneLoopMap",
            Description = "A Recordings-tab loop (not a Mission unit) that never overlaps itself: past its first run, its flight-map / Tracking Station ghost sits on the recorded orbit at StartUT + (liveUT - StartUT) mod period, and is absent in the pause between runs (standalone-loop-map preset, lane SLM-1)")]
        public IEnumerator StandaloneLoopMapGhost_FollowsTheLoopClock()
        {
            GameScenes scene = HighLogic.LoadedScene;
            if (scene != GameScenes.FLIGHT && scene != GameScenes.TRACKSTATION)
            {
                InGameAssert.Skip("needs FLIGHT or TRACKSTATION (scene=" + scene + ")");
                yield break;
            }

            // Let the scene's map-presence passes run after the batch starts (the flight
            // reseed runs on a 0.5 s real-time timer), then read after this frame's drive.
            yield return new WaitForSeconds(1.0f);
            yield return new WaitForEndOfFrame();

            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            int idx = -1;
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    if (committed[i] != null && committed[i].RecordingId == PresetRecordingId)
                    {
                        idx = i;
                        break;
                    }
                }
            }
            if (idx < 0)
            {
                InGameAssert.Skip("needs the standalone-loop-map preset (recording "
                    + PresetRecordingId + " is not committed)");
                yield break;
            }
            Recording rec = committed[idx];

            if (!TryGetSceneLoopUnits(scene, out GhostPlaybackLogic.LoopUnitSet units))
            {
                InGameAssert.Fail("no scene controller exposes the loop-unit set in " + scene);
                yield break;
            }

            InGameAssert.IsTrue(rec.LoopPlayback,
                "the preset recording lost its loop flag at load (not loopable?)");
            InGameAssert.IsFalse(units.IsMember(idx),
                "the preset recording is a Mission loop-unit member, so it no longer tests the standalone clock");

            double liveUT = Planetarium.GetUniversalTime();
            double period = rec.LoopIntervalSeconds;
            double duration = rec.EndUT - rec.StartUT;
            InGameAssert.IsTrue(period > duration,
                "the preset must not overlap itself (period " + period.ToString("F1", IC)
                + " s, duration " + duration.ToString("F1", IC) + " s)");

            long cycle = (long)System.Math.Floor((liveUT - rec.StartUT) / period);
            if (cycle < 1)
            {
                InGameAssert.Skip("still in the first run (liveUT=" + liveUT.ToString("F1", IC)
                    + "); the lane warps past it");
                yield break;
            }
            double phase = (liveUT - rec.StartUT) - cycle * period;
            bool expectedHidden = phase >= duration;
            double expectedEffUT = rec.StartUT + phase;

            // The seam every map consumer reads, and the polyline's head through its own entry.
            double effUT = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                idx, rec.StartUT, rec.EndUT, liveUT, units, out bool hidden);
            double polylineHead = GhostPlaybackLogic.ResolveTrackingStationSampleFrame(
                idx, rec.StartUT, rec.EndUT, liveUT, units,
                out bool polylineHidden, out _, out _, out _);

            InGameAssert.IsTrue(hidden == expectedHidden,
                "map sample hidden=" + hidden + " but the loop clock says hidden=" + expectedHidden
                + " (cycle " + cycle.ToString(IC) + ", phase " + phase.ToString("F1", IC) + " s)");
            InGameAssert.IsTrue(polylineHidden == hidden,
                "the trajectory line's head hidden=" + polylineHidden + " disagrees with the map sample's " + hidden);

            uint ghostPid = GhostMapPresence.GetGhostVesselPidForRecording(idx);
            bool hasGhost = GhostMapPresence.TryGetGhostWorldPosForRecording(idx, out Vector3d worldPos);
            string sceneName = scene == GameScenes.FLIGHT ? "FLIGHT" : "TRACKSTATION";

            if (expectedHidden)
            {
                InGameAssert.IsFalse(hasGhost,
                    "a map ghost (pid " + ghostPid.ToString(IC) + ") is shown in the pause between runs "
                    + "(cycle " + cycle.ToString(IC) + ", phase " + phase.ToString("F1", IC) + " s >= duration "
                    + duration.ToString("F1", IC) + " s)");
                ParsekLog.Info("TestRunner", string.Format(IC,
                    "StandaloneLoopMap ({0}) PASS: phase=pause cycle={1} loopPhase={2:F1} liveUT={3:F1} mapGhost=no",
                    sceneName, cycle, phase, liveUT));
                yield break;
            }

            InGameAssert.IsTrue(System.Math.Abs(effUT - expectedEffUT) < 1e-6,
                "map sample UT " + effUT.ToString("R", IC) + " is not the loop clock's "
                + expectedEffUT.ToString("R", IC) + " (liveUT " + liveUT.ToString("R", IC) + ")");
            InGameAssert.IsTrue(System.Math.Abs(polylineHead - effUT) < 1e-6,
                "the trajectory line's head " + polylineHead.ToString("R", IC)
                + " is not the map sample UT " + effUT.ToString("R", IC));
            InGameAssert.IsTrue(hasGhost,
                "no map ghost while the loop replays (cycle " + cycle.ToString(IC) + ", phase "
                + phase.ToString("F1", IC) + " s)");

            OrbitSegment? seg = TrajectoryMath.FindOrbitSegment(rec.OrbitSegments, expectedEffUT);
            InGameAssert.IsTrue(seg.HasValue, "the recording has no orbit segment at UT "
                + expectedEffUT.ToString("F1", IC));
            CelestialBody body = FlightGlobals.GetBodyByName(seg.Value.bodyName);
            InGameAssert.IsNotNull(body, "body '" + seg.Value.bodyName + "' not found");
            Orbit recorded = MapRenderProbe.BuildOrbitFromSegment(seg.Value, body);
            InGameAssert.IsNotNull(recorded, "could not build the recorded orbit");

            Vector3d actualRel = worldPos - body.position;
            Vector3d expectedRel = MapRenderProbe.OrbitRelativePositionYup(recorded, expectedEffUT);
            Vector3d liveRel = MapRenderProbe.OrbitRelativePositionYup(recorded, liveUT);
            double angle = Vector3d.Angle(actualRel, expectedRel);
            double angleVsLive = Vector3d.Angle(actualRel, liveRel);
            double radiusError = System.Math.Abs(actualRel.magnitude - expectedRel.magnitude);

            string measured = string.Format(IC,
                "phase=replay cycle={0} loopPhase={1:F1} liveUT={2:F1} effUT={3:F1} shift={4:F1} mapGhost=yes "
                + "pid={5} angleDeg={6:F3} radiusErrM={7:F0} angleVsLiveClockDeg={8:F1}",
                cycle, phase, liveUT, effUT, liveUT - effUT, ghostPid, angle, radiusError, angleVsLive);
            ParsekLog.Info("TestRunner", "StandaloneLoopMap (" + sceneName + ") measured: " + measured);

            InGameAssert.IsTrue(angle <= MaxPositionAngleDeg,
                "the map ghost is " + angle.ToString("F3", IC) + " deg off the recorded orbit at the loop "
                + "clock's UT " + expectedEffUT.ToString("F1", IC) + " (limit " + MaxPositionAngleDeg.ToString("F1", IC)
                + " deg; " + angleVsLive.ToString("F1", IC) + " deg from the live-clock position)");
            InGameAssert.IsTrue(radiusError <= MaxRadiusErrorMeters,
                "the map ghost's orbital radius is off by " + radiusError.ToString("F0", IC) + " m");
            ParsekLog.Info("TestRunner", "StandaloneLoopMap (" + sceneName + ") PASS: " + measured);
        }

        private static bool TryGetSceneLoopUnits(GameScenes scene, out GhostPlaybackLogic.LoopUnitSet units)
        {
            units = null;
            if (scene == GameScenes.FLIGHT)
            {
                ParsekFlight flight = ParsekFlight.Instance;
                if (flight != null)
                    units = flight.CurrentCachedLoopUnits;
            }
            else if (scene == GameScenes.TRACKSTATION)
            {
                ParsekTrackingStation ts = UnityEngine.Object.FindObjectOfType<ParsekTrackingStation>();
                if (ts != null)
                    units = ts.CurrentCachedLoopUnits;
            }
            return units != null;
        }
    }
}
