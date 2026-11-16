using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live placement check for supply-route ghosts in FLIGHT (todo NEXT-TESTS-TO-BUILD-2026-11-11
    /// item 5, audit B3): every ghost-driving route's backing loop unit is resolved through the
    /// route's OWN delivery clock (<see cref="RouteOrchestrator.ResolveLoopUnit"/> +
    /// <see cref="RouteLoopClock.TryGetRouteLoopState"/>, the unit the orchestrator builds for
    /// cargo timing), and for each unit member whose window holds that loop UT the cell
    /// re-derives the recorded place (<see cref="RouteGhostPlacement"/>: section lookup,
    /// bracketing samples, latitude / longitude / altitude interpolation, stock
    /// <c>GetWorldSurfacePosition</c>) and compares it with the engine ghost's rendered root
    /// position, read after the frame has rendered.
    ///
    /// <para>Two things are checked per sample: the engine's own render clock for that member
    /// (<see cref="GhostPlaybackEngine.TryResolveUnitMemberPlaybackUT"/>, over the unit set the
    /// flight host built) equals the delivery clock, and the ghost sits within the horizontal
    /// tolerance of the recorded place and inside the vertical window. So a ghost drawn on the
    /// wrong cycle, at the wrong body rotation, at a Relative offset read as latitude /
    /// longitude, or left behind by a floating-origin shift reds by POSITION, where the route
    /// render lanes used to pin only counts and log lines.</para>
    ///
    /// <para>Samples are taken on consecutive frames for up to <see cref="MaxWaitSeconds"/> of
    /// real time, so a lane that reaches the step inside a unit's inter-cycle dead zone still
    /// sees the next cycle. A member outside its window, a ghost the zone or warp policy hid,
    /// a unit that self-overlaps or re-aims, and a recorded surface the cell cannot read
    /// (orbital checkpoint, Relative without a body-fixed shadow) are counted, never failed;
    /// zero compared samples skip, so a lane that pins <c>passed=1</c> reds when nothing was
    /// compared.</para>
    /// </summary>
    public class RouteGhostPlacementInGameTests
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private const string Tag = "RouteGhostPlacement";

        /// <summary>Real-time budget for the sampling loop (the rover route's dead zone is
        /// 4.96 s of every 50 s cycle).</summary>
        internal const float MaxWaitSeconds = 12f;

        /// <summary>Stop early once this many samples are compared.</summary>
        internal const int TargetComparisons = 30;

        /// <summary>Bound on per-sample log lines (first sample per member plus failures).</summary>
        private const int MaxSampleLines = 20;

        [InGameTest(Category = "RouteGhostPlacement", Scene = GameScenes.FLIGHT,
            Description = "Every ghost-driving supply route's rendered ghost sits on its source recording at the loop UT the route's delivery clock reads: render clock equals delivery clock, horizontal distance within 3 m plus float grid plus one frame of motion, altitude inside the recorded / terrain-plus-clearance window (route render lanes H59, V27M; audit B3)")]
        public IEnumerator RouteGhostsSitOnTheirRecordedTrajectory()
        {
            ParsekFlight flight = ParsekFlight.Instance;
            if (flight == null || flight.Engine == null)
            {
                InGameAssert.Skip("needs a ParsekFlight instance with a playback engine (FLIGHT scene)");
                yield break;
            }

            var counters = new Counters();
            var worstByMember = new Dictionary<string, string>();
            var worstMetersByMember = new Dictionary<string, double>();
            var firstLogged = new HashSet<string>();
            var failures = new List<string>();
            var bodies = new SortedSet<string>(System.StringComparer.Ordinal);
            int routesSeen = 0;
            int frames = 0;
            int sampleLines = 0;
            float started = Time.realtimeSinceStartup;

            while (Time.realtimeSinceStartup - started < MaxWaitSeconds
                   && counters.Compared < TargetComparisons)
            {
                yield return new WaitForEndOfFrame();
                if (ParsekFlight.Instance != flight || flight.Engine == null)
                    break;
                frames++;
                int routesThisFrame = SampleFrame(flight, counters, worstByMember, worstMetersByMember,
                    firstLogged, failures, bodies, ref sampleLines);
                if (routesThisFrame > routesSeen) routesSeen = routesThisFrame;
                if (routesSeen == 0 && frames >= 3)
                    break;
            }

            foreach (var kv in worstByMember)
                ParsekLog.Info(Tag, "worst " + kv.Value);

            double worst = 0.0;
            foreach (var m in worstMetersByMember.Values)
                if (m > worst) worst = m;

            ParsekLog.Info(Tag, RouteGhostPlacement.FormatSummary(
                HighLogic.LoadedScene.ToString(), routesSeen, frames, counters.Compared, counters.Ok,
                counters.Bad, counters.OutsideWindow, counters.Hidden, counters.Unsupported,
                counters.NotLive, worst, string.Join(",", new List<string>(bodies).ToArray())));

            if (failures.Count > 0)
            {
                InGameAssert.Fail(counters.Bad.ToString(IC) + " route ghost sample(s) off their recorded place: "
                    + string.Join(" | ", failures.ToArray()));
                yield break;
            }
            if (routesSeen == 0)
            {
                InGameAssert.Skip("no committed route is in a ghost-driving status");
                yield break;
            }
            if (counters.Compared == 0)
            {
                InGameAssert.Skip("no route ghost sample was compared in " + frames.ToString(IC)
                    + " frames (outsideWindow=" + counters.OutsideWindow.ToString(IC)
                    + " hidden=" + counters.Hidden.ToString(IC)
                    + " unsupported=" + counters.Unsupported.ToString(IC)
                    + " notLive=" + counters.NotLive.ToString(IC) + ")");
                yield break;
            }
        }

        private sealed class Counters
        {
            public int Compared, Ok, Bad, OutsideWindow, Hidden, Unsupported, NotLive;
        }

        /// <summary>One frame's pass over every ghost-driving route. Returns the number of
        /// ghost-driving routes seen.</summary>
        private static int SampleFrame(ParsekFlight flight, Counters c,
            Dictionary<string, string> worstByMember, Dictionary<string, double> worstMetersByMember,
            HashSet<string> firstLogged, List<string> failures, SortedSet<string> bodies,
            ref int sampleLines)
        {
            IReadOnlyList<Route> routes = RouteStore.CommittedRoutes;
            if (routes == null) return 0;
            // [ERS-exempt] (folder allowlisted): loop-unit member indices are raw committed-list
            // indices, the same index space the engine's ghost slots use.
            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            double ut = Planetarium.GetUniversalTime();
            double frameSeconds = Time.deltaTime;
            GhostPlaybackEngine engine = flight.Engine;
            int seen = 0;

            for (int r = 0; r < routes.Count; r++)
            {
                Route route = routes[r];
                if (route == null || !RouteStatusPolicy.GhostDriving(route.Status)) continue;
                seen++;
                string routeName = route.Name ?? route.Id ?? "?";

                GhostPlaybackLogic.LoopUnit? unitOpt = RouteOrchestrator.ResolveLoopUnit(route, ut);
                if (!unitOpt.HasValue)
                {
                    c.NotLive++;
                    continue;
                }
                GhostPlaybackLogic.LoopUnit unit = unitOpt.Value;
                if (unit.IsReaim || unit.HasDescentTrigger || GhostPlaybackLogic.UnitMemberOverlaps(unit))
                {
                    c.Unsupported++;
                    continue;
                }
                if (!RouteLoopClock.TryGetRouteLoopState(unit, ut, out double loopUT, out long cycle,
                        out bool inTail))
                {
                    c.NotLive++;
                    continue;
                }
                if (inTail)
                {
                    c.OutsideWindow++;
                    continue;
                }

                int[] members = unit.MemberIndices;
                for (int k = 0; members != null && k < members.Length; k++)
                {
                    int idx = members[k];
                    if (committed == null || idx < 0 || idx >= committed.Count || committed[idx] == null)
                    {
                        c.Unsupported++;
                        continue;
                    }
                    Recording rec = committed[idx];
                    double memberStart = unit.MemberStartUT(idx, rec.StartUT);
                    double memberEnd = unit.MemberEndUT(idx, rec.EndUT);
                    if (loopUT < memberStart || loopUT > memberEnd)
                    {
                        c.OutsideWindow++;
                        continue;
                    }

                    if (!engine.TryResolveUnitMemberPlaybackUT(idx, ut, rec.StartUT, rec.EndUT,
                            out double renderLoopUT))
                    {
                        // The engine has not published this unit (the first run plays as
                        // ordinary recordings before the unit's anchor).
                        c.NotLive++;
                        continue;
                    }

                    if (!engine.TryGetGhostState(idx, out GhostPlaybackState state) || state == null
                        || state.ghost == null || !state.ghost.activeInHierarchy
                        || !state.positionedThisFrame)
                    {
                        c.Hidden++;
                        continue;
                    }

                    if (!RouteGhostPlacement.TrySelectSurface(rec.TrackSections, rec.Points, loopUT,
                            out IList<TrajectoryPoint> frames, out string source)
                        || !RouteGhostPlacement.TryInterpolate(frames, loopUT,
                            out RouteGhostPlacement.BodyFixedSample sample, out _))
                    {
                        c.Unsupported++;
                        continue;
                    }
                    CelestialBody body = FindBody(sample.Body);
                    if (body == null)
                    {
                        c.Unsupported++;
                        continue;
                    }

                    double expectedLon = TrajectoryMath.FrameTransform.ShiftLongitudeDegrees(
                        sample.Longitude, state.bodyFixedShiftSeconds, body);
                    Vector3d ghostWorld = state.ghost.transform.position;
                    double ghostLat = body.GetLatitude(ghostWorld);
                    double ghostLon = body.GetLongitude(ghostWorld);
                    double ghostAlt = body.GetAltitude(ghostWorld);
                    double horizontal = RouteGhostPlacement.GreatCircleMeters(
                        sample.Latitude, expectedLon, ghostLat, ghostLon, body.Radius + sample.Altitude);
                    double maxAxis = System.Math.Max(System.Math.Abs(ghostWorld.x),
                        System.Math.Max(System.Math.Abs(ghostWorld.y), System.Math.Abs(ghostWorld.z)));
                    double tol = RouteGhostPlacement.HorizontalToleranceMeters(
                        InGameFixtureMath.SceneFloatGridToleranceMeters(maxAxis), sample.SpeedMps, frameSeconds);
                    double terrain = body.pqsController != null
                        ? body.TerrainAltitude(sample.Latitude, expectedLon, true) : double.NaN;
                    RouteGhostPlacement.VerticalWindow(sample.Altitude, terrain, sample.Clearance,
                        out double vMin, out double vMax);
                    RouteGhostPlacement.Verdict v = RouteGhostPlacement.Compare(horizontal, tol, ghostAlt,
                        vMin, vMax, sample.Altitude, renderLoopUT - loopUT);

                    c.Compared++;
                    bodies.Add(body.name);
                    string memberKey = routeName + "#" + idx.ToString(IC);
                    string line = RouteGhostPlacement.FormatSampleLine(routeName, idx, rec.RecordingId,
                        rec.VesselName, body.name, source, ut, loopUT, cycle, v);
                    if (!worstMetersByMember.TryGetValue(memberKey, out double prevWorst)
                        || horizontal > prevWorst)
                    {
                        worstMetersByMember[memberKey] = horizontal;
                        worstByMember[memberKey] = line;
                    }
                    if (v.Ok)
                    {
                        c.Ok++;
                        if (firstLogged.Add(memberKey) && sampleLines < MaxSampleLines)
                        {
                            sampleLines++;
                            ParsekLog.Info(Tag, line);
                        }
                    }
                    else
                    {
                        c.Bad++;
                        if (sampleLines < MaxSampleLines)
                        {
                            sampleLines++;
                            ParsekLog.Warn(Tag, line);
                        }
                        if (failures.Count < 5)
                            failures.Add(line);
                    }
                }
            }
            return seen;
        }

        private static CelestialBody FindBody(string name)
        {
            if (string.IsNullOrEmpty(name) || FlightGlobals.Bodies == null) return null;
            for (int i = 0; i < FlightGlobals.Bodies.Count; i++)
            {
                CelestialBody b = FlightGlobals.Bodies[i];
                if (b != null && b.name == name) return b;
            }
            return null;
        }
    }
}
