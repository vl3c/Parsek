using System.Collections;
using System.Collections.Generic;
using Parsek.Display;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek.InGameTests
{
    // In-game gate for the supply-route overview line (M6 map-view route lines). Drives the REAL
    // RouteTrajectoryLineRenderer.DrawAll over a self-created committed same-body route + backing
    // recording and asserts the three runtime contracts the headless unit tests cannot reach (they
    // stop at the pure builder / decision, before the Vectrosity draw):
    //   1. toggle ON  + not owned  -> the route's leg VectorLine draws (active),
    //   2. ghost owns the recording -> the route line SKIPS it (no double-draw over the ghost's own
    //      trajectory) -> the leg deactivates,
    //   3. toggle OFF -> the route line hides.
    //
    // Deterministic by construction: the recording is authored in the FAR PAST (well before liveUT) so
    // the live ghost polyline Driver's head-UT gate can never draw it and never marks it owned; this
    // test owns the ownership signal explicitly via SetOwnershipPublishForTesting. The static route
    // line ignores the head-UT gate, so it draws the far-past path regardless. Each assertion is taken
    // immediately after this test's own DrawAll call (before any concurrent map-view onPreCull), so it
    // is robust whether the runner fires the test from the flight map or the flight scene.
    //
    // NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics); FLIGHT only; career-independent.
    // Cannot run headless (TryDrawLeg bakes a Vectrosity mesh against a live CelestialBody.scaledBody).
    public class RouteLineDrawInGameTest
    {
        private const string KerbinBodyName = "Kerbin";
        private const int MapLineLayer = 31;

        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "M6 route line: a committed same-body route draws its backing path (active "
                + "VectorLine), skips the recording the ghost is drawing (no double-draw), and hides "
                + "when the setting is off")]
        public IEnumerator RouteLine_DrawsSkipsOwnedAndHidesOnToggle()
        {
            CelestialBody kerbin = FlightGlobals.Bodies?.Find(b => b.bodyName == KerbinBodyName);
            if (kerbin == null)
            {
                InGameAssert.Skip("Kerbin not found in FlightGlobals.Bodies (non-stock pack)");
                yield break;
            }

            double liveUT = Planetarium.GetUniversalTime();
            // FAR PAST so the live ghost head-UT gate can never draw this recording (it would otherwise
            // populate drewNonOrbitalLegRecordings and race our explicit ownership control).
            double startUT = liveUT - 10000.0;
            double endUT = liveUT - 9000.0;

            var route = BuildSameBodyRoute(startUT, endUT);
            string recId = route.RecordingIds[0];
            Recording rec = BuildFarPastRecording(recId, startUT, endUT);

            List<Recording> prevRecordings = RecordingStore.CommittedRecordings != null
                ? new List<Recording>(RecordingStore.CommittedRecordings)
                : new List<Recording>();
            List<RecordingTree> prevTrees = RecordingStore.CommittedTrees != null
                ? new List<RecordingTree>(RecordingStore.CommittedTrees)
                : new List<RecordingTree>();
            ParsekSettings prevOverride = ParsekSettings.CurrentOverrideForTesting;

            var settings = new ParsekSettings { showRouteLines = true };
            System.Func<string, CelestialBody> resolveBody =
                name => FlightGlobals.Bodies?.Find(b => b.bodyName == name);

            try
            {
                RecordingStore.ClearCommittedInternal();
                RecordingStore.ClearCommittedTreesInternal();
                RecordingStore.AddCommittedInternal(rec);
                RouteStore.AddRoute(route);
                ParsekSettings.CurrentOverrideForTesting = settings;
                RouteTrajectoryLineRenderer.Clear();
                GhostTrajectoryPolylineRenderer.Clear(); // clears the ghost ownership drew-set

                // (1) ON + not owned -> draws.
                settings.showRouteLines = true;
                GhostTrajectoryPolylineRenderer.SetOwnershipPublishForTesting(recId, inDrewSet: false);
                RouteTrajectoryLineRenderer.DrawAll(Time.frameCount, MapLineLayer, resolveBody);
                int activeWhenShown = RouteTrajectoryLineRenderer.ActiveLegCountForTesting();
                ParsekLog.Info("TestRunner",
                    "RouteLine: activeWhenShown=" + activeWhenShown);
                InGameAssert.IsTrue(activeWhenShown > 0,
                    "a committed same-body route with the setting on must draw at least one active "
                    + "leg line (the backing recorded path)");

                yield return null;

                // (2) ON + ghost owns the recording -> the route line skips it (no double-draw) -> hidden.
                settings.showRouteLines = true;
                GhostTrajectoryPolylineRenderer.SetOwnershipPublishForTesting(recId, inDrewSet: true);
                RouteTrajectoryLineRenderer.DrawAll(Time.frameCount, MapLineLayer, resolveBody);
                int activeWhenOwned = RouteTrajectoryLineRenderer.ActiveLegCountForTesting();
                ParsekLog.Info("TestRunner",
                    "RouteLine: activeWhenOwned=" + activeWhenOwned);
                InGameAssert.AreEqual(0, activeWhenOwned,
                    "the route line must SKIP a recording the ghost polyline is drawing this frame "
                    + "(no double-draw over the ghost's own trajectory) -> the leg deactivates");

                yield return null;

                // (3) Re-show, then toggle OFF -> hidden.
                settings.showRouteLines = true;
                GhostTrajectoryPolylineRenderer.SetOwnershipPublishForTesting(recId, inDrewSet: false);
                RouteTrajectoryLineRenderer.DrawAll(Time.frameCount, MapLineLayer, resolveBody);
                InGameAssert.IsTrue(RouteTrajectoryLineRenderer.ActiveLegCountForTesting() > 0,
                    "route line re-draws after the ghost stops owning the recording");

                yield return null;

                settings.showRouteLines = false;
                RouteTrajectoryLineRenderer.DrawAll(Time.frameCount, MapLineLayer, resolveBody);
                int activeWhenOff = RouteTrajectoryLineRenderer.ActiveLegCountForTesting();
                ParsekLog.Info("TestRunner",
                    "RouteLine: activeWhenOff=" + activeWhenOff);
                InGameAssert.AreEqual(0, activeWhenOff,
                    "with the setting off the route line must hide every leg");
            }
            finally
            {
                RouteStore.RemoveRoute(route.Id);
                RouteTrajectoryLineRenderer.Clear();
                GhostTrajectoryPolylineRenderer.Clear();
                ParsekSettings.CurrentOverrideForTesting = prevOverride;
                RecordingStore.ClearCommittedInternal();
                RecordingStore.ClearCommittedTreesInternal();
                for (int i = 0; i < prevRecordings.Count; i++)
                    RecordingStore.AddCommittedInternal(prevRecordings[i]);
                for (int i = 0; i < prevTrees.Count; i++)
                    RecordingStore.AddCommittedTreeInternal(prevTrees[i]);
            }
        }

        private static Route BuildSameBodyRoute(double startUT, double endUT)
        {
            return new Route
            {
                Id = "routeline-test-" + System.Guid.NewGuid().ToString("N"),
                Name = "Parsek Route Line Test",
                RecordingIds = new List<string>
                {
                    "routeline-rec-" + System.Guid.NewGuid().ToString("N"),
                },
                DispatchWindowPeriod = 0.0, // same-body (v1 scope)
                RecordedDockUT = -1.0,       // no dock clip -> whole path
                Status = RouteStatus.Active,
            };
        }

        // A short far-past non-orbital recording: flat Points only, NO OrbitSegment, so
        // BuildLegsForRecording yields one non-orbital leg the route line can draw body-fixed.
        private static Recording BuildFarPastRecording(string recId, double startUT, double endUT)
        {
            var rec = new Recording
            {
                RecordingId = recId,
                VesselName = "Parsek Route Line Test",
                StartBodyName = KerbinBodyName,
                SegmentBodyName = KerbinBodyName,
                PlaybackEnabled = true,
            };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = startUT, latitude = 0.0, longitude = 0.0, altitude = 5000.0,
                rotation = Quaternion.identity, velocity = new Vector3(0f, 300f, 0f),
                bodyName = KerbinBodyName,
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = (startUT + endUT) * 0.5, latitude = 0.25, longitude = 0.25, altitude = 15000.0,
                rotation = Quaternion.identity, velocity = new Vector3(0f, 500f, 0f),
                bodyName = KerbinBodyName,
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = endUT, latitude = 0.5, longitude = 0.5, altitude = 25000.0,
                rotation = Quaternion.identity, velocity = new Vector3(0f, 800f, 0f),
                bodyName = KerbinBodyName,
            });
            rec.MarkFilesDirty();
            return rec;
        }
    }
}
