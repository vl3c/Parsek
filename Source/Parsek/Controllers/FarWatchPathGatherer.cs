using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The live half of the ship-safety check (design-far-watch.md 8.1): reads the ship, the
    /// vessels it exposes, its maneuver nodes and alarms, and walks each path with Parsek's own
    /// propagation (<see cref="BallisticExtrapolator.Extrapolate"/>), never stock's patched-conic
    /// chain (absent on an inactive ship and in an early career). One synchronous pass, one Zup.
    /// </summary>
    internal static class FarWatchPathGatherer
    {
        private const string Tag = "FarWatchSafety";

        /// <summary>
        /// Builds the safety inputs for <paramref name="ship"/>. <paramref name="clearToSave"/> is
        /// stock's reading taken while the ship was still active (CLEAR during a watch: the packed
        /// ship cannot change state). <paramref name="bodies"/> is null when no body table could
        /// be built (every walk then fails, so the check refuses PathUnknown);
        /// <paramref name="exposed"/> are the loaded vessels the caller found within their pack
        /// range of the ship (8.2 rules 4, 6, 7), or null.
        /// </summary>
        internal static FarWatchSafetyInputs Gather(Vessel ship, double nowUT, ClearToSaveStatus clearToSave,
            string pendingWindow, bool reFlyActive, Dictionary<string, ExtrapolationBody> bodies, List<Vessel> exposed)
        {
            var inputs = new FarWatchSafetyInputs
            {
                NowUT = nowUT,
                ClearToSave = clearToSave,
                PendingWindow = pendingWindow,
                ReFlyActive = reFlyActive,
            };
            if (bodies != null)
            {
                foreach (var kv in bodies)
                {
                    ExtrapolationBody b = kv.Value;
                    inputs.Bodies[kv.Key] = new FarWatchSafetyBody
                    {
                        Name = b.Name,
                        Radius = b.Radius,
                        AtmosphereDepth = b.AtmosphereDepth,
                        MaxTerrainHeight = b.MaxTerrainAltitude,
                    };
                }
            }
            inputs.Ship = Describe(ship, nowUT, bodies);
            if (ship != null)
            {
                ReadNodeUTs(ship, inputs.ShipNodeUTs);
                ReadAlarmUTs(ship.persistentId, inputs.ShipAlarmUTs);
            }
            if (exposed != null)
            {
                for (int k = 0; k < exposed.Count; k++)
                    inputs.Exposed.Add(Describe(exposed[k], nowUT, bodies));
            }
            return inputs;
        }

        internal static FarWatchSafetyVessel Describe(Vessel v, double nowUT, IReadOnlyDictionary<string, ExtrapolationBody> bodies)
        {
            if (v == null)
                return null;
            var sv = new FarWatchSafetyVessel
            {
                Pid = v.persistentId,
                Guid = v.id,
                Name = v.vesselName,
                LandedOrSplashed = v.LandedOrSplashed,
                SrfSpeed = v.srfSpeed,
                Crewed = SafeCrewCount(v) > 0,
                Controllable = SafeControllable(v),
                DockingCaptureActive = HasDockingCapture(v),
            };
            sv.Path = sv.LandedOrSplashed ? new FarWatchSafetyPath { Ending = FarWatchPathEnding.Final } : WalkPath(v, nowUT, bodies);
            return sv;
        }

        /// <summary>Parsek's own walk of <paramref name="v"/>'s on-rails path (8.1).</summary>
        internal static FarWatchSafetyPath WalkPath(Vessel v, double nowUT, IReadOnlyDictionary<string, ExtrapolationBody> bodies)
        {
            var path = new FarWatchSafetyPath();
            if (bodies == null)
            {
                path.FailureReason = "no-bodies";
                return path;
            }
            if (v == null || v.orbit == null || v.orbit.referenceBody == null)
            {
                path.FailureReason = "no-orbit";
                return path;
            }
            Vector3d position = v.orbit.getRelativePositionAtUT(nowUT);
            Vector3d velocity = v.orbit.getOrbitalVelocityAtUT(nowUT);
            if (!IsFinite(position) || !IsFinite(velocity))
            {
                path.FailureReason = "state-not-finite";
                return path;
            }
            var start = new BallisticStateVector
            {
                ut = nowUT,
                bodyName = v.orbit.referenceBody.name,
                position = position,
                velocity = velocity,
            };
            ExtrapolationResult result;
            try
            {
                result = BallisticExtrapolator.Extrapolate(start, bodies, null, warnOnSubSurfaceStart: false,
                    stopOnFinalConic: true);
            }
            catch (Exception ex)
            {
                path.FailureReason = "extrapolate-threw:" + ex.GetType().Name;
                return path;
            }
            ApplyResult(result, bodies, path);
            return path;
        }

        /// <summary>Maps an extrapolation result onto a path (pure; the ending rules of 8.1).</summary>
        internal static void ApplyResult(ExtrapolationResult result, IReadOnlyDictionary<string, ExtrapolationBody> bodies,
            FarWatchSafetyPath path)
        {
            if (result.failureReason != ExtrapolationFailureReason.None)
            {
                path.Ending = FarWatchPathEnding.Failed;
                path.FailureReason = result.failureReason.ToString();
                return;
            }
            if (result.segments != null)
            {
                for (int k = 0; k < result.segments.Count; k++)
                {
                    OrbitSegment s = result.segments[k];
                    ExtrapolationBody b;
                    double mu = s.bodyName != null && bodies != null && bodies.TryGetValue(s.bodyName, out b)
                        ? b.GravitationalParameter : double.NaN;
                    path.Segments.Add(FarWatchConicMath.FromSegment(s, mu));
                }
            }
            if (result.terminalState == TerminalState.Destroyed)
            {
                path.Ending = FarWatchPathEnding.Impact;
                path.EndUT = result.terminalUT;
            }
            else if (result.endedOnFinalConic)
            {
                path.Ending = FarWatchPathEnding.Final;
            }
            else
            {
                path.Ending = FarWatchPathEnding.Truncated;
                path.EndUT = result.terminalUT;
            }
        }

        /// <summary>
        /// The ship's maneuver node UTs: the live solver while the ship is active, else the flight
        /// plan stock keeps in <c>Vessel.flightPlanNode</c> (the solver is destroyed by
        /// <c>MakeInactive</c>, 8.5).
        /// </summary>
        internal static void ReadNodeUTs(Vessel ship, List<double> into)
        {
            try
            {
                if (ship.patchedConicSolver != null && ship.patchedConicSolver.maneuverNodes != null)
                {
                    for (int k = 0; k < ship.patchedConicSolver.maneuverNodes.Count; k++)
                        into.Add(ship.patchedConicSolver.maneuverNodes[k].UT);
                    return;
                }
                ConfigNode plan = ship.flightPlanNode;
                if (plan == null)
                    return;
                ConfigNode[] nodes = plan.GetNodes("MANEUVER");
                for (int k = 0; k < nodes.Length; k++)
                {
                    double ut;
                    string s = nodes[k].GetValue("UT");
                    if (s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out ut))
                        into.Add(ut);
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "maneuver node read failed: " + ex.GetType().Name);
            }
        }

        /// <summary>Stock Alarm Clock alarms bound to the ship (8.5).</summary>
        internal static void ReadAlarmUTs(uint shipPid, List<double> into)
        {
            try
            {
                AlarmClockScenario scenario = AlarmClockScenario.Instance;
                if (scenario == null || scenario.alarms == null)
                    return;
                foreach (AlarmTypeBase alarm in scenario.alarms.Values)
                {
                    if (alarm != null && alarm.vesselId == shipPid)
                        into.Add(alarm.ut);
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "alarm read failed: " + ex.GetType().Name);
            }
        }

        /// <summary>A cheap signature of a UT list (change detection for the re-check).</summary>
        internal static string Signature(List<double> uts)
        {
            if (uts == null || uts.Count == 0)
                return string.Empty;
            var copy = new List<double>(uts);
            copy.Sort();
            var parts = new string[copy.Count];
            for (int k = 0; k < copy.Count; k++)
                parts[k] = copy[k].ToString("F1", CultureInfo.InvariantCulture);
            return string.Join(",", parts);
        }

        private static bool HasDockingCapture(Vessel v)
        {
            try
            {
                if (!v.loaded || v.parts == null)
                    return false;
                List<ModuleDockingNode> nodes = v.FindPartModulesImplementing<ModuleDockingNode>();
                for (int k = 0; k < nodes.Count; k++)
                {
                    string s = nodes[k].state;
                    if (s != null && (s.Contains("Acquire") || s.Contains("PreAttached")))
                        return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        private static int SafeCrewCount(Vessel v)
        {
            try { return v.GetCrewCount(); }
            catch (Exception) { return 0; }
        }

        private static bool SafeControllable(Vessel v)
        {
            try { return v.IsControllable; }
            catch (Exception) { return false; }
        }

        private static bool IsFinite(Vector3d v)
        {
            return !double.IsNaN(v.x) && !double.IsNaN(v.y) && !double.IsNaN(v.z)
                && !double.IsInfinity(v.x) && !double.IsInfinity(v.y) && !double.IsInfinity(v.z);
        }
    }
}
