using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Implements the relative-state time jump: a discrete UT skip that preserves
    /// the spatial configuration of all vessels in the physics bubble.
    /// Nothing moves. Orbital epochs are adjusted so Keplerian propagation at the
    /// new UT produces the same position.
    /// </summary>
    internal static class TimeJumpManager
    {
        private const string Tag = "TimeJump";
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        /// <summary>
        /// Pure: compute the epoch-shifted mean anomaly for an orbit.
        /// The orbit shape stays the same; only the phase reference changes
        /// so Keplerian propagation at the new UT produces the same position.
        /// newMeanAnomaly = oldMeanAnomaly + n * (newEpoch - oldEpoch)
        /// where n = sqrt(GM / a^3)
        /// Result is normalized to [0, 2*pi].
        /// </summary>
        internal static double ComputeEpochShiftedMeanAnomaly(
            double oldMeanAnomalyAtEpoch, double oldEpoch,
            double sma, double gravParam,
            double newEpoch)
        {
            double sma3 = sma * sma * sma;
            double meanMotion = Math.Sqrt(gravParam / sma3);
            double jumpDelta = newEpoch - oldEpoch;
            double newM = oldMeanAnomalyAtEpoch + meanMotion * jumpDelta;

            // Normalize to [0, 2*pi]
            const double twoPi = 2.0 * Math.PI;
            newM %= twoPi;
            if (newM < 0) newM += twoPi;

            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "ComputeEpochShiftedMeanAnomaly: oldM={0:F6} oldEpoch={1:F1} " +
                    "sma={2:F0} newEpoch={3:F1} delta={4:F1} n={5:F8} newM={6:F6}",
                    oldMeanAnomalyAtEpoch, oldEpoch, sma, newEpoch, jumpDelta,
                    meanMotion, newM));

            return newM;
        }

        /// <summary>
        /// Pure: determine which chain tips are crossed during a jump from t0 to targetUT.
        /// A chain tip is crossed when t0 &lt; chain.SpawnUT &lt;= targetUT and the chain
        /// is not terminated.
        /// Returns list of chains sorted by SpawnUT (chronological order).
        /// </summary>
        internal static List<GhostChain> FindCrossedChainTips(
            Dictionary<uint, GhostChain> chains,
            double t0, double targetUT)
        {
            var result = new List<GhostChain>();

            if (chains == null || chains.Count == 0)
            {
                ParsekLog.Verbose(Tag, "FindCrossedChainTips: empty/null chains, returning empty");
                return result;
            }

            foreach (var kvp in chains)
            {
                GhostChain chain = kvp.Value;

                if (chain.IsTerminated)
                {
                    ParsekLog.Verbose(Tag,
                        string.Format(ic,
                            "FindCrossedChainTips: skipping terminated chain vessel={0}",
                            chain.OriginalVesselPid));
                    continue;
                }

                if (chain.SpawnUT > t0 && chain.SpawnUT <= targetUT)
                {
                    result.Add(chain);
                }
            }

            // Sort chronologically by SpawnUT
            result.Sort((a, b) => a.SpawnUT.CompareTo(b.SpawnUT));

            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "FindCrossedChainTips: t0={0:F1} target={1:F1} crossed={2}",
                    t0, targetUT, result.Count));

            return result;
        }

        /// <summary>
        /// Pure: compute the minimum jump target UT for a selected chain tip.
        /// Returns the SpawnUT of the chain owning the given vessel PID.
        /// Must also include all independent chain tips chronologically before the selected one,
        /// so all earlier tips are also spawned (a ghost cannot remain past its tip).
        /// If the PID is not found, returns 0 (invalid).
        /// </summary>
        internal static double ComputeJumpTargetUT(
            Dictionary<uint, GhostChain> chains,
            uint selectedVesselPid)
        {
            if (chains == null || chains.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    "ComputeJumpTargetUT: empty/null chains, returning 0");
                return 0;
            }

            GhostChain selected;
            if (!chains.TryGetValue(selectedVesselPid, out selected))
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "ComputeJumpTargetUT: pid={0} not found in chains, returning 0",
                        selectedVesselPid));
                return 0;
            }

            double targetUT = selected.SpawnUT;

            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "ComputeJumpTargetUT: selected pid={0} spawnUT={1:F1}",
                    selectedVesselPid, targetUT));

            return targetUT;
        }

        /// <summary>
        /// Pure: validate that a time jump is valid (targetUT strictly greater than currentUT).
        /// </summary>
        internal static bool IsValidJump(double currentUT, double targetUT)
        {
            bool valid = targetUT > currentUT;

            ParsekLog.Verbose(Tag,
                string.Format(ic,
                    "IsValidJump: current={0:F1} target={1:F1} valid={2}",
                    currentUT, targetUT, valid));

            return valid;
        }

        /// <summary>
        /// KSP runtime: execute the full time jump sequence.
        /// 1. Set Planetarium UT
        /// 2. Epoch-shift all vessel orbits (keep position/velocity, update epoch)
        /// 3. Process spawn queue (chain tips crossed during jump)
        /// 4. Trigger game actions recalculation (if available, else skip with warning)
        /// </summary>
        internal static void ExecuteJump(
            double targetUT,
            Dictionary<uint, GhostChain> chains,
            VesselGhoster ghoster)
        {
            double t0 = Planetarium.GetUniversalTime();
            double jumpDelta = targetUT - t0;

            if (!IsValidJump(t0, targetUT))
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "ExecuteJump: invalid jump t0={0:F1} target={1:F1} — aborted",
                        t0, targetUT));
                return;
            }

            int objectCount = FlightGlobals.VesselsLoaded != null
                ? FlightGlobals.VesselsLoaded.Count
                : 0;

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Time jump initiated: T0={0:F1} target={1:F1} delta={2:F1}s objects={3}",
                    t0, targetUT, jumpDelta, objectCount));

            // Step 1: Set game clock to target UT
            Planetarium.SetUniversalTime(targetUT);

            ParsekLog.Verbose(Tag,
                string.Format(ic, "UT set to {0:F1}", targetUT));

            // Step 2: Epoch-shift all loaded vessel orbits.
            // Keep position/velocity unchanged; recompute orbital elements at the new epoch.
            // KSP's Orbit.UpdateFromStateVectors recomputes elements from state vectors
            // at the given UT, which is numerically more stable than delta-shifting mean anomaly.
            if (FlightGlobals.VesselsLoaded != null)
            {
                foreach (Vessel v in FlightGlobals.VesselsLoaded)
                {
                    if (v == null || v.orbit == null) continue;

                    // Surface vessels: no epoch shift needed (surface-fixed coords are UT-independent)
                    if (v.situation == Vessel.Situations.LANDED ||
                        v.situation == Vessel.Situations.SPLASHED ||
                        v.situation == Vessel.Situations.PRELAUNCH)
                    {
                        ParsekLog.Verbose(Tag,
                            string.Format(ic,
                                "Epoch shift skipped (surface): pid={0} name={1} situation={2}",
                                v.persistentId, v.vesselName, v.situation));
                        continue;
                    }

                    try
                    {
                        // Capture current state vectors before epoch shift
                        Vector3d pos = v.orbit.pos;
                        Vector3d vel = v.orbit.vel;
                        CelestialBody body = v.orbit.referenceBody;
                        double oldMeanAnomaly = v.orbit.meanAnomalyAtEpoch;

                        // Recompute orbital elements at new UT from same state vectors
                        v.orbit.UpdateFromStateVectors(pos, vel, body, targetUT);

                        double shift = v.orbit.meanAnomalyAtEpoch - oldMeanAnomaly;

                        ParsekLog.Verbose(Tag,
                            string.Format(ic,
                                "Epoch-shifted vessel: pid={0} name={1} body={2} dMeanAnomaly={3:F6}",
                                v.persistentId, v.vesselName,
                                body != null ? body.bodyName : "null",
                                shift));
                    }
                    catch (Exception ex)
                    {
                        ParsekLog.Error(Tag,
                            string.Format(ic,
                                "Epoch shift failed: pid={0} name={1} error={2}",
                                v.persistentId, v.vesselName, ex.Message));
                    }
                }
            }

            // Step 3: Process crossed chain tips — spawn real vessels
            var crossed = FindCrossedChainTips(chains, t0, targetUT);
            int spawnCount = 0;

            foreach (GhostChain chain in crossed)
            {
                if (ghoster == null)
                {
                    ParsekLog.Warn(Tag,
                        string.Format(ic,
                            "Spawn skipped (no ghoster): vessel={0}",
                            chain.OriginalVesselPid));
                    continue;
                }

                try
                {
                    uint spawnedPid = ghoster.SpawnAtChainTip(chain);
                    if (spawnedPid != 0)
                    {
                        spawnCount++;
                        chains.Remove(chain.OriginalVesselPid);
                        ParsekLog.Info(Tag,
                            string.Format(ic,
                                "Chain tip spawned during jump: vessel={0} spawnedPid={1}",
                                chain.OriginalVesselPid, spawnedPid));
                    }
                    else if (chain.SpawnBlocked)
                    {
                        ParsekLog.Warn(Tag,
                            string.Format(ic,
                                "Chain tip spawn blocked during jump: vessel={0} — ghost continues",
                                chain.OriginalVesselPid));
                    }
                }
                catch (Exception ex)
                {
                    ParsekLog.Error(Tag,
                        string.Format(ic,
                            "Chain tip spawn failed during jump: vessel={0} error={1}",
                            chain.OriginalVesselPid, ex.Message));
                }
            }

            // Step 4: Game actions recalculation
            // The game actions system is a future dependency. If not available, skip with warning.
            ParsekLog.Warn(Tag,
                "Time jump: game actions system not available, skipping recalculation");

            int remainingGhosts = chains != null ? chains.Count : 0;

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Time jump complete: {0} vessels spawned, {1} ghosts remaining",
                    spawnCount, remainingGhosts));
        }

        /// <summary>
        /// Creates a TIME_JUMP SegmentEvent with pre/post state details.
        /// Called by FlightRecorder when a time jump occurs during active recording.
        /// </summary>
        internal static SegmentEvent CreateTimeJumpEvent(
            double preJumpUT, double postJumpUT,
            double lat, double lon, double alt,
            float velX, float velY, float velZ)
        {
            string details = string.Format(ic,
                "preUT={0:R};postUT={1:R};lat={2:R};lon={3:R};alt={4:R};vx={5:R};vy={6:R};vz={7:R}",
                preJumpUT, postJumpUT, lat, lon, alt, velX, velY, velZ);

            var evt = new SegmentEvent
            {
                ut = postJumpUT,
                type = SegmentEventType.TimeJump,
                details = details
            };

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "TIME_JUMP event created: preUT={0:F1} postUT={1:F1} delta={2:F1}s",
                    preJumpUT, postJumpUT, postJumpUT - preJumpUT));

            return evt;
        }

        /// <summary>
        /// Notifies FlightRecorder (if actively recording) that a time jump occurred,
        /// so it can emit a TIME_JUMP SegmentEvent.
        /// </summary>
        internal static void NotifyRecorder(FlightRecorder recorder, double preJumpUT, double postJumpUT)
        {
            if (recorder == null || !recorder.IsRecording)
            {
                ParsekLog.Verbose(Tag,
                    "NotifyRecorder: no active recording, skipping TIME_JUMP event");
                return;
            }

            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null)
            {
                ParsekLog.Warn(Tag,
                    "NotifyRecorder: no active vessel, skipping TIME_JUMP event");
                return;
            }

            double lat = v.latitude;
            double lon = v.longitude;
            double alt = v.altitude;
            Vector3 vel = v.GetSrfVelocity();

            var evt = CreateTimeJumpEvent(preJumpUT, postJumpUT, lat, lon, alt,
                vel.x, vel.y, vel.z);
            recorder.SegmentEvents.Add(evt);

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "TIME_JUMP SegmentEvent emitted for recording vessel pid={0} name={1}",
                    v.persistentId, v.vesselName));
        }
    }
}
