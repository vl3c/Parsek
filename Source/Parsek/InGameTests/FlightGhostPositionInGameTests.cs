using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live placement check for every flight-scene ghost the playback engine draws (test-gap
    /// doc 2026-11-18, Batch 3: "flight-scene ghosts never checked by position"). For each live
    /// primary ghost the cell resolves the UT the engine plays it at (the loop unit's span clock
    /// for a mission-loop member, the recording's own loop clock for a looped recording, the
    /// current UT otherwise), re-derives the recorded place from the recording's body-fixed
    /// samples (<see cref="FlightGhostPosition.TryClassify"/> +
    /// <see cref="RouteGhostPlacement.TryInterpolate"/>) and compares it with the rendered ghost
    /// root after the frame has rendered, through <see cref="RouteGhostPlacement.Compare"/>.
    ///
    /// <para>One cell per recorded surface (<see cref="FlightGhostPosition.SurfaceKind"/>), so a
    /// lane's <c>BATCH_COMPLETE</c> split says which surfaces its flight put on screen: a cell
    /// whose surface no live ghost sat on during the sampling window skips, naming the counts.
    /// Samples are taken on consecutive frames for up to <see cref="MaxWaitSeconds"/> of real
    /// time; a hidden ghost, a sample outside its window, an unsupported surface or clock, and a
    /// sample whose float grid swallows the signal are counted, never failed.</para>
    /// </summary>
    public class FlightGhostPositionInGameTests
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private const string Tag = "FlightGhostPosition";

        /// <summary>Real-time budget for one cell's sampling loop.</summary>
        internal const float MaxWaitSeconds = 8f;

        /// <summary>Stop early once this many samples of the cell's surface are compared.</summary>
        internal const int TargetComparisons = 30;

        /// <summary>Give up early when no live ghost at all was seen for this many frames.</summary>
        internal const int NoGhostGiveUpFrames = 60;

        /// <summary>Bound on per-sample log lines per cell (first sample per ghost plus failures).</summary>
        private const int MaxSampleLines = 20;

        [InGameTest(Category = "FlightGhostPosition", Scene = GameScenes.FLIGHT,
            Description = "Every live flight ghost on an Absolute section sits on its recorded body-fixed trajectory at the UT the engine plays it at: horizontal distance within 3 m plus float grid plus one frame of motion, altitude inside the recorded / terrain-plus-clearance window widened by one frame of motion (test-gap Batch 3)")]
        public IEnumerator AbsoluteSectionGhostsSitOnTheirRecordedTrajectory()
        {
            return RunCell(FlightGhostPosition.SurfaceKind.Absolute,
                "no live flight ghost sat on an Absolute section in the sampling window");
        }

        [InGameTest(Category = "FlightGhostPosition", Scene = GameScenes.FLIGHT,
            Description = "Every live parent-anchored ghost (debris, controlled-decoupled children) on a Relative section sits on its recorded body-fixed shadow, its primary playback surface (test-gap Batch 3)")]
        public IEnumerator ParentAnchoredGhostsSitOnTheirBodyFixedShadow()
        {
            return RunCell(FlightGhostPosition.SurfaceKind.ParentAnchored,
                "no live parent-anchored flight ghost sat on a Relative section with a covering body-fixed shadow in the sampling window");
        }

        [InGameTest(Category = "FlightGhostPosition", Scene = GameScenes.FLIGHT,
            Description = "Every live non-loop ghost on an anchor-local Relative section (placed from the anchor's resolved pose and the recorded offset) lands on the body-fixed shadow the recorder wrote beside the offsets, so a wrong anchor pose or a mis-applied offset reds (test-gap Batch 3)")]
        public IEnumerator RelativeSectionGhostsLandOnTheirRecordedShadow()
        {
            return RunCell(FlightGhostPosition.SurfaceKind.RelativeAnchor,
                "no live non-loop flight ghost sat on an anchor-local Relative section with a covering body-fixed shadow in the sampling window");
        }

        private sealed class Counters
        {
            public int Ghosts, Compared, Ok, Bad, OtherKind, OutsideWindow, Hidden, Unsupported,
                Unresolved, NotLive;
        }

        private static IEnumerator RunCell(FlightGhostPosition.SurfaceKind kind, string skipReason)
        {
            ParsekFlight flight = ParsekFlight.Instance;
            if (flight == null || flight.Engine == null)
            {
                InGameAssert.Skip("needs a ParsekFlight instance with a playback engine (FLIGHT scene)");
                yield break;
            }

            var c = new Counters();
            var worstByGhost = new Dictionary<int, string>();
            var worstMetersByGhost = new Dictionary<int, double>();
            var firstLogged = new HashSet<int>();
            var failures = new List<string>();
            var bodies = new SortedSet<string>(System.StringComparer.Ordinal);
            int frames = 0;
            int framesWithoutGhost = 0;
            int sampleLines = 0;
            float started = Time.realtimeSinceStartup;

            while (Time.realtimeSinceStartup - started < MaxWaitSeconds
                   && c.Compared < TargetComparisons)
            {
                yield return new WaitForEndOfFrame();
                if (ParsekFlight.Instance != flight || flight.Engine == null)
                    break;
                frames++;
                int ghostsThisFrame = SampleFrame(flight, kind, c, worstByGhost, worstMetersByGhost,
                    firstLogged, failures, bodies, ref sampleLines);
                if (ghostsThisFrame > c.Ghosts) c.Ghosts = ghostsThisFrame;
                framesWithoutGhost = ghostsThisFrame == 0 ? framesWithoutGhost + 1 : 0;
                if (c.Ghosts == 0 && framesWithoutGhost >= NoGhostGiveUpFrames)
                    break;
            }

            foreach (var kv in worstByGhost)
                ParsekLog.Info(Tag, "worst " + kv.Value);

            double worst = 0.0;
            foreach (var m in worstMetersByGhost.Values)
                if (m > worst) worst = m;

            ParsekLog.Info(Tag, FlightGhostPosition.FormatSummary(kind,
                HighLogic.LoadedScene.ToString(), frames, c.Ghosts, c.Compared, c.Ok, c.Bad,
                c.OtherKind, c.OutsideWindow, c.Hidden, c.Unsupported, c.Unresolved, c.NotLive, worst,
                string.Join(",", new List<string>(bodies).ToArray())));

            switch (FlightGhostPosition.DecideOutcome(c.Compared, c.Bad))
            {
                case FlightGhostPosition.CellOutcome.Fail:
                    InGameAssert.Fail(c.Bad.ToString(IC) + " "
                        + FlightGhostPosition.KindLabel(kind) + " ghost sample(s) off their recorded place: "
                        + string.Join(" | ", failures.ToArray()));
                    yield break;
                case FlightGhostPosition.CellOutcome.Skip:
                    InGameAssert.Skip(skipReason + " (frames=" + frames.ToString(IC)
                        + " ghosts=" + c.Ghosts.ToString(IC)
                        + " otherKind=" + c.OtherKind.ToString(IC)
                        + " outsideWindow=" + c.OutsideWindow.ToString(IC)
                        + " hidden=" + c.Hidden.ToString(IC)
                        + " unsupported=" + c.Unsupported.ToString(IC)
                        + " unresolved=" + c.Unresolved.ToString(IC)
                        + " notLive=" + c.NotLive.ToString(IC) + ")");
                    yield break;
            }
        }

        /// <summary>One frame's pass over every live primary ghost. Returns the number of
        /// ghost slots the engine holds this frame.</summary>
        private static int SampleFrame(ParsekFlight flight, FlightGhostPosition.SurfaceKind kind,
            Counters c, Dictionary<int, string> worstByGhost, Dictionary<int, double> worstMetersByGhost,
            HashSet<int> firstLogged, List<string> failures, SortedSet<string> bodies,
            ref int sampleLines)
        {
            GhostPlaybackEngine engine = flight.Engine;
            // [ERS-exempt] (folder allowlisted): engine ghost slots are raw committed-list indices.
            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            Dictionary<int, GameObject> ghosts = engine.GetGhostGameObjects();
            if (ghosts == null || ghosts.Count == 0 || committed == null) return 0;

            double ut = Planetarium.GetUniversalTime();
            double frameSeconds = Time.deltaTime;

            foreach (int idx in ghosts.Keys)
            {
                if (idx < 0 || idx >= committed.Count || committed[idx] == null)
                {
                    c.Unsupported++;
                    continue;
                }
                Recording rec = committed[idx];
                if (!engine.TryGetGhostState(idx, out GhostPlaybackState state) || state == null
                    || state.ghost == null || !state.ghost.activeInHierarchy
                    || !state.positionedThisFrame)
                {
                    c.Hidden++;
                    continue;
                }

                if (!TryResolvePlaybackUT(flight, engine, idx, rec, ut, c, out double playbackUT,
                        out bool loop, out string clock))
                    continue;

                if (!FlightGhostPosition.InsideRecording(playbackUT, rec.StartUT, rec.EndUT))
                {
                    c.OutsideWindow++;
                    continue;
                }

                bool parentAnchored = !string.IsNullOrEmpty(rec.ParentAnchorRecordingId);
                bool classified = FlightGhostPosition.TryClassify(rec.TrackSections, rec.Points,
                    parentAnchored, loop, playbackUT, out FlightGhostPosition.SurfaceKind sampleKind,
                    out IList<TrajectoryPoint> frames, out string source);
                if (sampleKind != kind)
                {
                    c.OtherKind++;
                    continue;
                }
                if (!classified)
                {
                    c.Unsupported++;
                    continue;
                }
                if (!RouteGhostPlacement.TryInterpolate(frames, playbackUT,
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
                if (!FlightGhostPosition.ToleranceResolves(tol, sample.SpeedMps))
                {
                    c.Unresolved++;
                    continue;
                }
                double terrain = body.pqsController != null
                    ? body.TerrainAltitude(sample.Latitude, expectedLon, true) : double.NaN;
                RouteGhostPlacement.VerticalWindow(sample.Altitude, terrain, sample.Clearance,
                    out double vMin, out double vMax);
                double motion = FlightGhostPosition.FrameMotionMeters(sample.SpeedMps, frameSeconds);
                vMin -= motion;
                vMax += motion;
                RouteGhostPlacement.Verdict v = RouteGhostPlacement.Compare(horizontal, tol, ghostAlt,
                    vMin, vMax, sample.Altitude, 0.0);

                c.Compared++;
                bodies.Add(body.name);
                string line = FlightGhostPosition.FormatSampleLine(kind, idx, rec.RecordingId,
                    rec.VesselName, body.name, source, clock, ut, playbackUT, v);
                if (!worstMetersByGhost.TryGetValue(idx, out double prevWorst) || horizontal > prevWorst)
                {
                    worstMetersByGhost[idx] = horizontal;
                    worstByGhost[idx] = line;
                }
                if (v.Ok)
                {
                    c.Ok++;
                    if (firstLogged.Add(idx) && sampleLines < MaxSampleLines)
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
            return ghosts.Count;
        }

        /// <summary>
        /// The UT the engine plays ghost <paramref name="idx"/> at, by the same resolution order
        /// watch mode uses (<c>WatchModeController.ResolveWatchPlaybackUT</c>): a mission loop-unit
        /// member through the unit's span clock, a looped recording through its own loop clock,
        /// anything else at the current UT. A re-aim / descent-trigger / self-overlapping unit and
        /// an overlap loop are counted unsupported (their placement deliberately leaves the verbatim
        /// recording, or the primary ghost is not the only copy).
        /// </summary>
        private static bool TryResolvePlaybackUT(ParsekFlight flight, GhostPlaybackEngine engine,
            int idx, Recording rec, double ut, Counters c, out double playbackUT, out bool loop,
            out string clock)
        {
            playbackUT = ut;
            loop = false;
            clock = "live";

            GhostPlaybackLogic.LoopUnitSet units = flight.cachedLoopUnits;
            if (units != null && units.TryGetUnitForMember(idx, out GhostPlaybackLogic.LoopUnit unit))
            {
                loop = true;
                clock = "loop-unit";
                if (unit.IsReaim || unit.HasDescentTrigger || GhostPlaybackLogic.UnitMemberOverlaps(unit))
                {
                    c.Unsupported++;
                    return false;
                }
                if (!engine.TryResolveUnitMemberPlaybackUT(idx, ut, rec.StartUT, rec.EndUT,
                        out playbackUT))
                {
                    c.NotLive++;
                    return false;
                }
                return true;
            }

            if (GhostPlaybackEngine.ShouldLoopPlayback(rec))
            {
                loop = true;
                clock = "loop";
                if (!flight.TryGetLoopScheduleForWatch(rec, idx, out _, out _,
                        out double duration, out double intervalSeconds))
                {
                    c.NotLive++;
                    return false;
                }
                if (GhostPlaybackLogic.IsOverlapLoop(intervalSeconds, duration))
                {
                    c.Unsupported++;
                    return false;
                }
                if (!flight.TryComputeLoopPlaybackUTForWatch(rec, ut, out playbackUT, out _,
                        out bool inPause, idx))
                {
                    c.NotLive++;
                    return false;
                }
                if (inPause)
                {
                    c.OutsideWindow++;
                    return false;
                }
                return true;
            }

            return true;
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
