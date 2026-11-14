using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live placement check for committed recordings' spawned vessels: every effective (ERS)
    /// committed recording whose <c>SpawnedVesselPersistentId</c> names a live, non-ghost vessel and
    /// whose terminal state is Orbiting / Landed / Splashed must have that vessel at the
    /// recording's terminal place (orbit elements and phase on the terminal body, or within
    /// <see cref="LeafVesselPlacement.MaxSurfaceDistanceMeters"/> of the terminal surface
    /// position). Consumers: the far-body timeline lanes (FAR-*), which read it after a
    /// Rewind-to-Launch, a revert, physics warp or a respawn, so a far-body leaf that was
    /// stripped, duplicated, moved or re-placed on the wrong body reads by POSITION, not by a
    /// vessel count.
    ///
    /// <para>Runs in any scene that holds <c>FlightGlobals.Vessels</c> (FLIGHT, the Space
    /// Center, the Tracking Station); unloaded vessels keep their on-rails orbit and their
    /// saved latitude / longitude there. A recording whose vessel is absent is counted, not
    /// failed (it may be recovered, still a ghost, or not spawned yet); two live vessels
    /// carrying one recording's spawn pid fail. Zero compared pairs skip, so a lane that pins
    /// <c>passed=1</c> reds when nothing was there to compare.</para>
    ///
    /// <para>A Parsek <c>TimeJump</c> (<see cref="TimeJumpManager"/>) epoch-shifts every orbit
    /// so nothing moves while the clock jumps, which by design leaves an orbiting leaf one
    /// jump span behind its recorded phase; a lane that runs this cell must move the clock
    /// by warp, a load or a rewind, never by a TimeJump.</para>
    /// </summary>
    public class LeafVesselPlacementInGameTests
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private const string Tag = "LeafPlacement";

        [InGameTest(Category = "LeafVesselPlacement",
            Description = "Every committed recording's live spawned vessel (Orbiting / Landed / Splashed terminal) sits at the recording's terminal place: same body, same orbit elements and phase, or within 100 m of the terminal surface position; no spawn pid is held by two live vessels (far-body timeline lanes FAR-*)")]
        public IEnumerator SpawnedLeafVesselsSitAtTheirTerminalPlace()
        {
            GameScenes scene = HighLogic.LoadedScene;
            if (scene != GameScenes.FLIGHT && scene != GameScenes.SPACECENTER
                && scene != GameScenes.TRACKSTATION)
            {
                InGameAssert.Skip("needs FLIGHT, SPACECENTER or TRACKSTATION (scene=" + scene + ")");
                yield break;
            }
            // Let a spawn queued on the scene's first frames land before reading.
            yield return new WaitForSeconds(1.0f);
            yield return new WaitForEndOfFrame();

            // The effective set: a superseded recording's spawn is not the timeline's claim.
            IReadOnlyList<Recording> committed = EffectiveState.ComputeERS();
            List<Vessel> vessels = FlightGlobals.Vessels;
            double ut = Planetarium.GetUniversalTime();

            int compared = 0, ok = 0, bad = 0, absent = 0, duplicate = 0, unsupported = 0;
            var bodies = new SortedSet<string>(System.StringComparer.Ordinal);
            var failures = new List<string>();

            for (int i = 0; committed != null && i < committed.Count; i++)
            {
                Recording rec = committed[i];
                if (rec == null || rec.SpawnedVesselPersistentId == 0 || !rec.TerminalStateValue.HasValue)
                    continue;
                TerminalState ts = rec.TerminalStateValue.Value;
                if (ts != TerminalState.Orbiting && ts != TerminalState.Landed && ts != TerminalState.Splashed)
                    continue;

                uint pid = rec.SpawnedVesselPersistentId;
                Vessel live = null;
                int matches = 0;
                for (int v = 0; vessels != null && v < vessels.Count; v++)
                {
                    Vessel candidate = vessels[v];
                    if (candidate == null || candidate.persistentId != pid) continue;
                    if (GhostMapPresence.IsGhostMapVessel(candidate.persistentId)) continue;
                    matches++;
                    if (live == null) live = candidate;
                }
                if (matches == 0)
                {
                    absent++;
                    continue;
                }
                if (matches > 1)
                {
                    duplicate++;
                    failures.Add("pid " + pid.ToString(IC) + " of " + rec.RecordingId + " is held by "
                        + matches.ToString(IC) + " live vessels");
                    continue;
                }

                string line;
                bool pairOk;
                string recordedBody;
                if (ts == TerminalState.Orbiting)
                {
                    recordedBody = rec.TerminalOrbitBody;
                    Orbit orbit = live.orbit;
                    CelestialBody body = orbit?.referenceBody;
                    if (orbit == null || body == null || string.IsNullOrEmpty(recordedBody))
                    {
                        unsupported++;
                        continue;
                    }
                    var recorded = new LeafVesselPlacement.OrbitElements
                    {
                        Body = recordedBody,
                        SemiMajorAxis = rec.TerminalOrbitSemiMajorAxis,
                        Eccentricity = rec.TerminalOrbitEccentricity,
                        InclinationDeg = rec.TerminalOrbitInclination,
                        LanDeg = rec.TerminalOrbitLAN,
                        ArgPeDeg = rec.TerminalOrbitArgumentOfPeriapsis,
                        MeanAnomalyAtEpochRad = rec.TerminalOrbitMeanAnomalyAtEpoch,
                        Epoch = rec.TerminalOrbitEpoch,
                    };
                    var current = new LeafVesselPlacement.OrbitElements
                    {
                        Body = body.name,
                        SemiMajorAxis = orbit.semiMajorAxis,
                        Eccentricity = orbit.eccentricity,
                        InclinationDeg = orbit.inclination,
                        LanDeg = orbit.LAN,
                        ArgPeDeg = orbit.argumentOfPeriapsis,
                        MeanAnomalyAtEpochRad = orbit.meanAnomalyAtEpoch,
                        Epoch = orbit.epoch,
                    };
                    LeafVesselPlacement.OrbitVerdict verdict =
                        LeafVesselPlacement.CompareOrbits(recorded, current, body.gravParameter, ut);
                    if (verdict.Reason == "unsupported-open-orbit")
                    {
                        unsupported++;
                        continue;
                    }
                    pairOk = verdict.Ok;
                    line = LeafVesselPlacement.FormatOrbitLine(rec.RecordingId, rec.VesselName, pid,
                        recordedBody, body.name, verdict);
                }
                else
                {
                    if (!rec.TerminalPosition.HasValue)
                    {
                        unsupported++;
                        continue;
                    }
                    SurfacePosition sp = rec.TerminalPosition.Value;
                    recordedBody = sp.body;
                    CelestialBody body = live.mainBody;
                    if (body == null || string.IsNullOrEmpty(recordedBody))
                    {
                        unsupported++;
                        continue;
                    }
                    LeafVesselPlacement.SurfaceVerdict verdict = LeafVesselPlacement.CompareSurface(
                        recordedBody, sp.latitude, sp.longitude,
                        body.name, live.latitude, live.longitude, body.Radius);
                    pairOk = verdict.Ok;
                    line = LeafVesselPlacement.FormatSurfaceLine(rec.RecordingId, rec.VesselName, pid,
                        recordedBody, body.name, verdict);
                }

                compared++;
                bodies.Add(recordedBody);
                ParsekLog.Info(Tag, line);
                if (pairOk)
                {
                    ok++;
                }
                else
                {
                    bad++;
                    failures.Add(line);
                }
            }

            ParsekLog.Info(Tag, LeafVesselPlacement.FormatSummary(scene.ToString(), compared, ok, bad,
                absent, duplicate, unsupported, string.Join(",", new List<string>(bodies).ToArray())));

            if (failures.Count > 0)
            {
                InGameAssert.Fail(failures.Count.ToString(IC) + " spawned vessel(s) off their terminal place: "
                    + string.Join(" | ", failures.ToArray()));
                yield break;
            }
            if (compared == 0)
            {
                InGameAssert.Skip("no committed recording has a live spawned vessel to compare (absent="
                    + absent.ToString(IC) + ", unsupported=" + unsupported.ToString(IC) + ")");
                yield break;
            }
        }
    }
}
