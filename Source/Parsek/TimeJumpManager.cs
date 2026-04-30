using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
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
        // Keep the transient under a fraction of a second, but long enough to cover the
        // SetUniversalTime callback burst and a few trailing scene/render frames.
        private const int TimeJumpLaunchAutoRecordSuppressFrames = 8;
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;
        private static bool isTimeJumpLaunchAutoRecordInProgress;
        // Frame-bounded rather than UT-bounded so rewinds/quickloads cannot revive stale suppression.
        private static int timeJumpLaunchAutoRecordSuppressUntilFrame = -1;

        internal static bool IsTimeJumpLaunchAutoRecordInProgress
            => isTimeJumpLaunchAutoRecordInProgress;

        internal static int TimeJumpLaunchAutoRecordSuppressUntilFrame
            => timeJumpLaunchAutoRecordSuppressUntilFrame;

        internal static bool IsTimeJumpLaunchAutoRecordSuppressed(
            bool timeJumpLaunchAutoRecordInProgress,
            int currentFrame,
            int suppressUntilFrame)
        {
            return timeJumpLaunchAutoRecordInProgress
                || (suppressUntilFrame >= 0 && currentFrame <= suppressUntilFrame);
        }

        private static void ArmTimeJumpLaunchAutoRecordSuppression(string jumpKind)
        {
            timeJumpLaunchAutoRecordSuppressUntilFrame =
                Time.frameCount + TimeJumpLaunchAutoRecordSuppressFrames;

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Time-jump launch auto-record suppression armed: jump={0} currentFrame={1} untilFrame={2} durationFrames={3}",
                    jumpKind,
                    Time.frameCount,
                    timeJumpLaunchAutoRecordSuppressUntilFrame,
                    TimeJumpLaunchAutoRecordSuppressFrames));
        }

        /// <summary>
        /// Pure: compute the epoch-shifted mean anomaly for an orbit.
        /// The orbit shape stays the same; only the phase reference changes
        /// so Keplerian propagation at the new UT produces the same position.
        /// newMeanAnomaly = oldMeanAnomaly + n * (newEpoch - oldEpoch)
        /// where n = sqrt(GM / a^3)
        /// Result is normalized to [0, 2*pi].
        /// </summary>
        // Used for diagnostic logging of epoch shift magnitude
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
            // Stop time warp before jumping — SetUniversalTime during warp can cause desync
            if (TimeWarp.CurrentRateIndex > 0)
            {
                TimeWarp.SetRate(0, true);
                ParsekLog.Info(Tag, "ExecuteJump: stopped time warp before jump");
            }

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

            isTimeJumpLaunchAutoRecordInProgress = true;
            try
            {
                // Step 1: Capture state vectors for all loaded vessels BEFORE changing UT.
                // If UT is set first, KSP's orbit propagation runs at the new UT with old epochs,
                // moving vessels before we can freeze them.
                var capturedStates = CaptureOrbitalStates();

                // Step 2: Set game clock to target UT
                Planetarium.SetUniversalTime(targetUT);
                ArmTimeJumpLaunchAutoRecordSuppression("epoch-shift");

                ParsekLog.Verbose(Tag,
                    string.Format(ic, "UT set to {0:F1}", targetUT));

                // Step 3: Epoch-shift all captured vessel orbits.
                // Recompute orbital elements at the new epoch from the pre-jump state vectors.
                ApplyEpochShifts(capturedStates, targetUT);

                // Step 4: Process crossed chain tips — spawn real vessels
                var spawnedChainKeys = SpawnCrossedChainTips(chains, ghoster, t0, targetUT);

                // Remove spawned chains from caller's dict (#79 — SpawnCrossedChainTips
                // returns chain keys and no longer mutates the dict directly)
                if (chains != null)
                {
                    for (int i = 0; i < spawnedChainKeys.Count; i++)
                        chains.Remove(spawnedChainKeys[i]);
                }

                // Step 5: Game actions recalculation
                // The game actions system is a future dependency. If not available, skip with warning.
                ParsekLog.Warn(Tag,
                    "Time jump: game actions system not available, skipping recalculation");

                int remainingGhosts = chains != null ? chains.Count : 0;

                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Time jump complete: {0} vessels spawned, {1} ghosts remaining",
                        spawnedChainKeys.Count, remainingGhosts));
            }
            finally
            {
                isTimeJumpLaunchAutoRecordInProgress = false;
            }
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
                    v.persistentId, Recording.ResolveLocalizedName(v.vesselName)));
        }

        /// <summary>
        /// Executes a forward UT jump WITHOUT epoch-shifting orbits.
        /// Unlike ExecuteJump (which freezes relative positions for Real Spawn Control),
        /// this lets orbits propagate naturally — vessels advance along their Keplerian paths.
        /// Used for the FF button to skip ahead to a future recording's start.
        ///
        /// After advancing UT, fixes ModuleResourceConverter.lastUpdateTime on all loaded
        /// vessels to prevent burst resource production/consumption from the large time delta.
        /// </summary>
        internal static void ExecuteForwardJump(double targetUT)
        {
            double t0 = Planetarium.GetUniversalTime();
            double jumpDelta = targetUT - t0;

            if (!IsValidJump(t0, targetUT))
            {
                ParsekLog.Warn(Tag,
                    string.Format(ic,
                        "ExecuteForwardJump: invalid jump t0={0:F1} target={1:F1} — aborted",
                        t0, targetUT));
                return;
            }

            int objectCount = FlightGlobals.VesselsLoaded != null
                ? FlightGlobals.VesselsLoaded.Count
                : 0;

            ParsekLog.Info(Tag,
                string.Format(ic,
                    "Forward jump initiated: T0={0:F1} target={1:F1} delta={2:F1}s objects={3}",
                    t0, targetUT, jumpDelta, objectCount));

            isTimeJumpLaunchAutoRecordInProgress = true;
            try
            {
                // Put in-physics vessels on rails temporarily so SetUniversalTime doesn't
                // cause physics interactions during the jump.
                var onRailsVessels = PutLoadedVesselsOnRails();

                // Advance UT — orbits propagate naturally (no epoch shift)
                Planetarium.SetUniversalTime(targetUT);
                ArmTimeJumpLaunchAutoRecordSuppression("forward");

                ParsekLog.Verbose(Tag,
                    string.Format(ic, "Forward jump: UT set to {0:F1}", targetUT));

                // Fix resource converter timestamps to prevent burst production/consumption.
                // BaseConverter.lastUpdateTime tracks when the converter last ran; after a large
                // UT jump, converters see a massive deltaTime and drain/produce in one burst.
                FixResourceConverterTimestamps(targetUT);

                // Take vessels off rails
                TakeVesselsOffRails(onRailsVessels);

                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Forward jump complete: delta={0:F1}s, {1} vessels temporarily on-railed",
                        jumpDelta, onRailsVessels.Count));
            }
            finally
            {
                isTimeJumpLaunchAutoRecordInProgress = false;
            }
        }

        /// <summary>
        /// Captures orbital state vectors (position, velocity, body, meanAnomaly) for all
        /// loaded vessels that are in orbital flight. Surface vessels are skipped (surface-fixed
        /// coords are UT-independent). Atmospheric vessels emit a warning.
        /// </summary>
        private static List<(Vessel v, Vector3d pos, Vector3d vel, CelestialBody body, double oldMeanAnomaly)> CaptureOrbitalStates()
        {
            var capturedStates = new List<(Vessel v, Vector3d pos, Vector3d vel, CelestialBody body, double oldMeanAnomaly)>();
            if (FlightGlobals.VesselsLoaded == null) return capturedStates;

            foreach (Vessel v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.orbit == null) continue;
                if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;

                // Surface vessels: no epoch shift needed (surface-fixed coords are UT-independent)
                if (v.situation == Vessel.Situations.LANDED ||
                    v.situation == Vessel.Situations.SPLASHED ||
                    v.situation == Vessel.Situations.PRELAUNCH)
                {
                    ParsekLog.Verbose(Tag,
                        string.Format(ic,
                            "Epoch shift skipped (surface): pid={0} name={1} situation={2}",
                            v.persistentId, Recording.ResolveLocalizedName(v.vesselName), v.situation));
                    continue;
                }

                // Atmospheric warning
                if (v.mainBody != null && v.mainBody.atmosphere &&
                    v.altitude < v.mainBody.atmosphereDepth)
                {
                    ParsekLog.Warn(Tag,
                        string.Format(ic,
                            "vessel '{0}' is in atmosphere — epoch shift is approximate",
                            Recording.ResolveLocalizedName(v.vesselName)));
                }

                capturedStates.Add((v, v.orbit.pos, v.orbit.vel, v.orbit.referenceBody, v.orbit.meanAnomalyAtEpoch));
            }

            ParsekLog.Verbose(Tag,
                string.Format(ic, "CaptureOrbitalStates: captured {0} orbital vessel state(s)",
                    capturedStates.Count));
            return capturedStates;
        }

        /// <summary>
        /// Applies epoch shifts to all captured orbital states by recomputing orbital
        /// elements at the new UT from the pre-jump state vectors.
        /// </summary>
        private static void ApplyEpochShifts(
            List<(Vessel v, Vector3d pos, Vector3d vel, CelestialBody body, double oldMeanAnomaly)> capturedStates,
            double targetUT)
        {
            for (int i = 0; i < capturedStates.Count; i++)
            {
                var (v, pos, vel, body, oldMeanAnomaly) = capturedStates[i];
                try
                {
                    // Recompute orbital elements at new UT from same state vectors
                    v.orbit.UpdateFromStateVectors(pos, vel, body, targetUT);

                    double shift = v.orbit.meanAnomalyAtEpoch - oldMeanAnomaly;

                    ParsekLog.Verbose(Tag,
                        string.Format(ic,
                            "Epoch-shifted vessel: pid={0} name={1} body={2} dMeanAnomaly={3:F6}",
                            v.persistentId, Recording.ResolveLocalizedName(v.vesselName),
                            body != null ? body.bodyName : "null",
                            shift));
                }
                catch (Exception ex)
                {
                    ParsekLog.Error(Tag,
                        string.Format(ic,
                            "Epoch shift failed: pid={0} name={1} error={2}",
                            v.persistentId, Recording.ResolveLocalizedName(v.vesselName), ex.Message));
                }
            }
        }

        /// <summary>
        /// Spawns real vessels for all chain tips crossed during a time jump.
        /// Returns the original-vessel PID keys for successfully spawned chains
        /// (caller is responsible for removing them from the chains dictionary).
        /// Does NOT mutate the input chains dict (#79).
        /// </summary>
        internal static List<uint> SpawnCrossedChainTips(
            Dictionary<uint, GhostChain> chains,
            VesselGhoster ghoster,
            double t0, double targetUT)
        {
            var crossed = FindCrossedChainTips(chains, t0, targetUT);
            var spawnedChainKeys = new List<uint>();

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
                        spawnedChainKeys.Add(chain.OriginalVesselPid);
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

            return spawnedChainKeys;
        }

        /// <summary>
        /// Puts all loaded in-physics vessels on rails to prevent physics interactions
        /// during a UT jump. Returns the list of vessels that were put on rails
        /// (for later off-rails restoration).
        /// </summary>
        private static List<Vessel> PutLoadedVesselsOnRails()
        {
            var onRailsVessels = new List<Vessel>();
            if (FlightGlobals.VesselsLoaded == null) return onRailsVessels;

            // Copy to list first — GoOnRails callbacks could mutate VesselsLoaded.
            var loadedSnapshot = new List<Vessel>(FlightGlobals.VesselsLoaded);
            foreach (Vessel v in loadedSnapshot)
            {
                if (v == null) continue;
                if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;

                // Atmospheric warning: Keplerian propagation ignores drag,
                // vessel may end up underground after forward jump
                if (v.mainBody != null && v.mainBody.atmosphere &&
                    v.altitude < v.mainBody.atmosphereDepth)
                {
                    ParsekLog.Warn(Tag,
                        string.Format(ic,
                            "vessel '{0}' is in atmosphere — orbit propagation is approximate",
                            Recording.ResolveLocalizedName(v.vesselName)));
                }

                if (!v.packed && v.loaded)
                {
                    try
                    {
                        v.GoOnRails();
                        onRailsVessels.Add(v);
                        ParsekLog.Verbose(Tag,
                            string.Format(ic,
                                "Put vessel on rails: pid={0} name={1}",
                                v.persistentId, Recording.ResolveLocalizedName(v.vesselName)));
                    }
                    catch (Exception ex)
                    {
                        ParsekLog.Warn(Tag,
                            string.Format(ic,
                                "Failed to put vessel on rails: pid={0} name={1} error={2}",
                                v.persistentId, Recording.ResolveLocalizedName(v.vesselName), ex.Message));
                    }
                }
            }

            ParsekLog.Verbose(Tag,
                string.Format(ic, "PutLoadedVesselsOnRails: {0} vessel(s) put on rails",
                    onRailsVessels.Count));
            return onRailsVessels;
        }

        /// <summary>
        /// Takes a list of vessels off rails after a UT jump.
        /// </summary>
        private static void TakeVesselsOffRails(List<Vessel> onRailsVessels)
        {
            foreach (Vessel v in onRailsVessels)
            {
                if (v == null) continue;
                try
                {
                    v.GoOffRails();
                    ParsekLog.Verbose(Tag,
                        string.Format(ic,
                            "Took vessel off rails: pid={0} name={1}",
                            v.persistentId, Recording.ResolveLocalizedName(v.vesselName)));
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(Tag,
                        string.Format(ic,
                            "Failed to take vessel off rails: pid={0} name={1} error={2}",
                            v.persistentId, Recording.ResolveLocalizedName(v.vesselName), ex.Message));
                }
            }
        }

        /// <summary>
        /// Resets lastUpdateTime on all BaseConverter (ModuleResourceConverter) modules
        /// across all loaded vessels to the given UT. This prevents converters from seeing
        /// the full time jump delta and producing/consuming resources in a single burst.
        /// lastUpdateTime is protected, so we use reflection.
        /// </summary>
        private static readonly FieldInfo lastUpdateTimeField =
            typeof(BaseConverter).GetField("lastUpdateTime",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static void FixResourceConverterTimestamps(double newUT)
        {
            if (FlightGlobals.VesselsLoaded == null) return;

            if (lastUpdateTimeField == null)
            {
                ParsekLog.Warn(Tag,
                    "FixResourceConverterTimestamps: lastUpdateTime field not found via reflection — skipping");
                return;
            }

            int fixedCount = 0;
            var vessels = new List<Vessel>(FlightGlobals.VesselsLoaded);
            foreach (Vessel v in vessels)
            {
                if (v == null || v.parts == null) continue;
                if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;

                foreach (Part p in v.parts)
                {
                    if (p == null || p.Modules == null) continue;

                    for (int m = 0; m < p.Modules.Count; m++)
                    {
                        var converter = p.Modules[m] as BaseConverter;
                        if (converter != null)
                        {
                            double oldTime = (double)lastUpdateTimeField.GetValue(converter);
                            lastUpdateTimeField.SetValue(converter, newUT);
                            fixedCount++;

                            ParsekLog.Verbose(Tag,
                                string.Format(ic,
                                    "Fixed converter timestamp: vessel={0} part={1} module={2} old={3:F1} new={4:F1}",
                                    Recording.ResolveLocalizedName(v.vesselName), p.partInfo?.name ?? "?", converter.GetType().Name,
                                    oldTime, newUT));
                        }
                    }
                }
            }

            if (fixedCount > 0)
            {
                ParsekLog.Info(Tag,
                    string.Format(ic,
                        "Fixed {0} resource converter timestamp(s) to UT={1:F1}",
                        fixedCount, newUT));
            }
        }
    }
}
