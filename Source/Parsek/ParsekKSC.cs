using System;
using System.Collections.Generic;
using ClickThroughFix;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// KSC scene host for the Parsek UI and passive ghost playback.
    /// Shows committed recording ghosts (rockets launching, planes flying)
    /// as visual-only objects in the KSC view with full part-event fidelity
    /// (engine plumes, staging, parachutes, lights, etc.).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public class ParsekKSC : MonoBehaviour
    {
        private ToolbarControl toolbarControl;
        private ParsekUI ui;
        private bool showUI;
        private Rect windowRect = new Rect(20, 100, 200, 10);

        // KSC ghost playback state — keyed by recording index in CommittedRecordings
        private Dictionary<int, ParsekFlight.GhostPlaybackState> kscGhosts =
            new Dictionary<int, ParsekFlight.GhostPlaybackState>();

        // Overlap ghosts for negative loop intervals (multiple simultaneous ghosts per recording)
        private Dictionary<int, List<ParsekFlight.GhostPlaybackState>> kscOverlapGhosts =
            new Dictionary<int, List<ParsekFlight.GhostPlaybackState>>();

        // Cached body lookup to avoid per-frame lambda allocations
        private Dictionary<string, CelestialBody> bodyCache;

        // One-time log tracking (avoids repeating the same log every frame)
        private HashSet<int> loggedGhostSpawn = new HashSet<int>();

        private const double DefaultLoopIntervalSeconds = 10.0;
        private const double MinLoopDurationSeconds = 1.0;
        private const double MinCycleDuration = 1.0;
        // Safety cap for overlap ghosts. Natural phase expiration keeps count bounded for
        // well-behaved recordings, but pathological cases (very short duration, very negative
        // interval) could spawn many before expiration catches up.
        private const int MaxOverlapGhostsPerRecording = 20;

        // Distance culling: skip part events and deactivate ghosts beyond this range from camera.
        // 25km matches Kerbal Konstructs' default activation range for statics.
        private const float GhostCullDistanceSq = 25000f * 25000f;

        void Start()
        {
            ParsekLog.Info("KSC", "ParsekKSC starting in Space Center scene");

            ui = new ParsekUI(ParsekUI.UIMode.KSC);

            toolbarControl = gameObject.AddComponent<ToolbarControl>();
            toolbarControl.AddToAllToolbars(
                () => { showUI = true; ParsekLog.Verbose("KSC", "Toolbar button ON"); },
                () => { showUI = false; ParsekLog.Verbose("KSC", "Toolbar button OFF"); },
                ApplicationLauncher.AppScenes.SPACECENTER,
                ParsekFlight.MODID, "parsekKSCButton",
                "Parsek/Textures/parsek_38",
                "Parsek/Textures/parsek_24",
                ParsekFlight.MODNAME
            );

            // Build body lookup cache
            bodyCache = new Dictionary<string, CelestialBody>();
            if (FlightGlobals.Bodies != null)
            {
                for (int i = 0; i < FlightGlobals.Bodies.Count; i++)
                {
                    var b = FlightGlobals.Bodies[i];
                    if (b != null && !string.IsNullOrEmpty(b.name))
                        bodyCache[b.name] = b;
                }
                ParsekLog.Verbose("KSC", $"Body cache built: {bodyCache.Count} bodies");
            }

            int ghostCount = 0;
            var committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (ShouldShowInKSC(rec))
                {
                    ghostCount++;
                    ParsekLog.Verbose("KSCGhost",
                        $"Recording #{i} \"{rec.VesselName}\" eligible: " +
                        $"UT=[{rec.StartUT:F1},{rec.EndUT:F1}] loop={rec.LoopPlayback} " +
                        $"terminal={rec.TerminalStateValue} points={rec.Points.Count}");
                }
            }

            ParsekLog.Info("KSC",
                $"ParsekKSC initialized, {committed.Count} committed recordings, " +
                $"{ghostCount} eligible for KSC ghost playback");
        }

        void OnGUI()
        {
            if (!showUI) return;

            windowRect = ClickThruBlocker.GUILayoutWindow(
                GetInstanceID(), windowRect, ui.DrawWindow,
                "Parsek", ui.GetOpaqueWindowStyle(), GUILayout.Width(250));

            ui.DrawRecordingsWindowIfOpen(windowRect);
            ui.DrawActionsWindowIfOpen(windowRect);
            ui.DrawSettingsWindowIfOpen(windowRect);
        }

        #region Ghost Playback

        void Update()
        {
            var committed = RecordingStore.CommittedRecordings;
            if (committed.Count == 0) return;

            double currentUT = Planetarium.GetUniversalTime();

            float warpRate = TimeWarp.CurrentRate;
            bool suppressGhosts = ParsekFlight.ShouldSuppressGhosts(warpRate);
            bool suppressExplosionFx = ParsekFlight.ShouldSuppressExplosionFx(warpRate);

            if (suppressGhosts)
            {
                // High time warp: hide primary ghosts, destroy overlap ghosts
                foreach (var kvp in kscGhosts)
                    if (kvp.Value.ghost != null && kvp.Value.ghost.activeSelf)
                    {
                        kvp.Value.ghost.SetActive(false);
                        ParsekLog.Info("KSCGhost",
                            $"Ghost #{kvp.Key} hidden: warp {warpRate:F0}x > 50x");
                    }
                foreach (int key in new List<int>(kscOverlapGhosts.Keys))
                    DestroyAllKscOverlapGhosts(key);
                return;
            }

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (!ShouldShowInKSC(rec))
                {
                    // Recording no longer eligible (disabled, wrong body, etc.)
                    // — clean up any active ghosts so they don't linger in the scene.
                    if (kscGhosts.ContainsKey(i))
                    {
                        ParsekLog.Info("KSCGhost",
                            $"Ghost #{i} \"{rec.VesselName}\" no longer eligible — destroying");
                        DestroyKscGhost(kscGhosts[i], i);
                        kscGhosts.Remove(i);
                        loggedGhostSpawn.Remove(i);
                    }
                    DestroyAllKscOverlapGhosts(i);
                    continue;
                }

                // Branch: looping recordings check interval sign for overlap support
                if (rec.LoopPlayback)
                {
                    double duration = rec.EndUT - rec.StartUT;
                    if (duration <= MinLoopDurationSeconds) continue;

                    double intervalSeconds = GetLoopIntervalSeconds(rec);
                    if (intervalSeconds < 0)
                    {
                        // Negative interval: multi-ghost overlap path
                        UpdateOverlapKsc(i, rec, currentUT, intervalSeconds, duration, suppressExplosionFx);
                        continue;
                    }

                    // Positive/zero interval: single-ghost path — clean up any leftover overlaps
                    DestroyAllKscOverlapGhosts(i);

                    double targetUT;
                    int cycleIndex;
                    bool inPauseWindow;
                    bool inRange = TryComputeLoopUT(rec, currentUT,
                        out targetUT, out cycleIndex, out inPauseWindow);

                    UpdateSingleGhostKsc(i, rec, currentUT, targetUT, cycleIndex,
                        inRange, inPauseWindow, suppressExplosionFx);
                }
                else
                {
                    // Non-looping: raw UT range check
                    DestroyAllKscOverlapGhosts(i);
                    bool inRange = currentUT >= rec.StartUT && currentUT <= rec.EndUT;
                    UpdateSingleGhostKsc(i, rec, currentUT, currentUT, 0, inRange, false, suppressExplosionFx);
                }
            }
        }

        /// <summary>
        /// Single-ghost playback path (positive/zero loop interval, or non-looping).
        /// </summary>
        void UpdateSingleGhostKsc(int recIdx, RecordingStore.Recording rec,
            double currentUT, double targetUT, int cycleIndex,
            bool inRange, bool inPauseWindow, bool suppressExplosionFx)
        {
            ParsekFlight.GhostPlaybackState state;
            kscGhosts.TryGetValue(recIdx, out state);
            bool ghostActive = state != null && state.ghost != null;

            if (inRange && !inPauseWindow)
            {
                // Loop cycle change: destroy + respawn to guarantee clean visual state
                if (ghostActive && rec.LoopPlayback && state.loopCycleIndex != cycleIndex)
                {
                    int oldCycle = state.loopCycleIndex;
                    TriggerExplosionIfDestroyed(state, rec, recIdx);
                    DestroyKscGhost(state, recIdx);
                    kscGhosts.Remove(recIdx);
                    ghostActive = false;
                    state = null;
                    ParsekLog.Verbose("KSCGhost",
                        $"Ghost #{recIdx} \"{rec.VesselName}\" cycle change {oldCycle}→{cycleIndex}");
                }

                if (!ghostActive)
                {
                    state = SpawnKscGhost(rec, recIdx);
                    if (state == null) return;
                    state.loopCycleIndex = cycleIndex;
                    kscGhosts[recIdx] = state;
                    if (loggedGhostSpawn.Add(recIdx))
                        ParsekLog.Info("KSCGhost",
                            $"Ghost #{recIdx} \"{rec.VesselName}\" entered range: " +
                            $"targetUT={targetUT:F1} recUT=[{rec.StartUT:F1},{rec.EndUT:F1}] " +
                            $"cycle={cycleIndex} loop={rec.LoopPlayback} " +
                            $"terminal={rec.TerminalStateValue}");
                }
                else if (!state.ghost.activeSelf)
                {
                    state.ghost.SetActive(true);
                    ParsekLog.Info("KSCGhost",
                        $"Ghost #{recIdx} \"{rec.VesselName}\" re-shown after warp-down");
                }

                InterpolateAndPositionKsc(
                    state.ghost, rec.Points,
                    ref state.playbackIndex, targetUT,
                    rec.RecordingFormatVersion >= 5);

                // Distance culling: skip expensive part events for ghosts too far from camera
                if (IsGhostInCullRange(state.ghost))
                    ParsekFlight.ApplyPartEvents(recIdx, rec, targetUT, state);
                if (suppressExplosionFx)
                    ParsekFlight.StopAllRcsEmissions(state);

                if (!state.explosionFired && targetUT >= rec.EndUT)
                    TriggerExplosionIfDestroyed(state, rec, recIdx);
            }
            else if (ghostActive)
            {
                if (inPauseWindow)
                {
                    if (!state.explosionFired)
                        TriggerExplosionIfDestroyed(state, rec, recIdx);
                    if (!state.pauseHidden)
                    {
                        state.pauseHidden = true;
                        ParsekFlight.HideAllGhostParts(state);
                        ParsekLog.Verbose("KSCGhost",
                            $"Ghost #{recIdx} \"{rec.VesselName}\" hidden during loop pause window");
                    }
                }
                else
                {
                    TriggerExplosionIfDestroyed(state, rec, recIdx);
                    ParsekLog.Verbose("KSCGhost",
                        $"Ghost #{recIdx} \"{rec.VesselName}\" exited range at UT {currentUT:F1}");
                    DestroyKscGhost(state, recIdx);
                    kscGhosts.Remove(recIdx);
                    loggedGhostSpawn.Remove(recIdx);
                }
            }
        }

        /// <summary>
        /// Multi-ghost overlap path for negative loop intervals.
        /// Multiple ghosts from different cycles visible simultaneously.
        /// Simplified version of ParsekFlight.UpdateOverlapLoopPlayback
        /// (no camera logic, no reentry FX).
        /// </summary>
        void UpdateOverlapKsc(int recIdx, RecordingStore.Recording rec,
            double currentUT, double intervalSeconds, double duration, bool suppressExplosionFx)
        {
            ParsekFlight.GhostPlaybackState primaryState;
            kscGhosts.TryGetValue(recIdx, out primaryState);
            bool primaryActive = primaryState != null && primaryState.ghost != null;

            if (currentUT < rec.StartUT)
            {
                if (primaryActive) { DestroyKscGhost(primaryState, recIdx); kscGhosts.Remove(recIdx); }
                DestroyAllKscOverlapGhosts(recIdx);
                return;
            }

            double cycleDuration = duration + intervalSeconds;
            if (cycleDuration < MinCycleDuration) cycleDuration = MinCycleDuration;

            int firstCycle, lastCycle;
            ParsekFlight.GetActiveCycles(currentUT, rec.StartUT, rec.EndUT,
                intervalSeconds, MaxOverlapGhostsPerRecording, out firstCycle, out lastCycle);

            // Ensure overlap list exists
            List<ParsekFlight.GhostPlaybackState> overlaps;
            if (!kscOverlapGhosts.TryGetValue(recIdx, out overlaps))
            {
                overlaps = new List<ParsekFlight.GhostPlaybackState>();
                kscOverlapGhosts[recIdx] = overlaps;
            }

            bool srfRel = rec.RecordingFormatVersion >= 5;

            // --- Primary ghost = newest cycle (lastCycle) ---
            // primaryActive already guarantees primaryState != null && ghost != null
            bool primaryCycleChanged = !primaryActive
                || primaryState.loopCycleIndex != lastCycle;

            if (primaryCycleChanged)
            {
                // Move old primary to overlap list (don't destroy — it keeps playing)
                if (primaryActive)
                {
                    kscGhosts.Remove(recIdx);
                    overlaps.Add(primaryState);
                    ParsekLog.Verbose("KSCGhost",
                        $"Ghost #{recIdx} cycle={primaryState.loopCycleIndex} moved to overlap list");
                }

                // Spawn new primary for lastCycle
                primaryState = SpawnKscGhost(rec, recIdx);
                if (primaryState == null) return;
                primaryState.loopCycleIndex = lastCycle;
                kscGhosts[recIdx] = primaryState;
                ParsekLog.Info("KSCGhost",
                    $"Ghost #{recIdx} \"{rec.VesselName}\" overlap spawn cycle={lastCycle}");
            }

            // Position and animate primary (SpawnKscGhost above guarantees non-null)
            {
                double cycleStartUT = rec.StartUT + lastCycle * cycleDuration;
                double phase = currentUT - cycleStartUT;
                if (phase < 0) phase = 0;
                if (phase > duration) phase = duration;
                double loopUT = rec.StartUT + phase;

                InterpolateAndPositionKsc(primaryState.ghost, rec.Points,
                    ref primaryState.playbackIndex, loopUT, srfRel);

                if (IsGhostInCullRange(primaryState.ghost))
                    ParsekFlight.ApplyPartEvents(recIdx, rec, loopUT, primaryState);
                if (suppressExplosionFx)
                    ParsekFlight.StopAllRcsEmissions(primaryState);

                if (!primaryState.explosionFired && phase >= duration)
                    TriggerExplosionIfDestroyed(primaryState, rec, recIdx);
            }

            // --- Overlap ghosts (older cycles, reverse iterate) ---
            for (int j = overlaps.Count - 1; j >= 0; j--)
            {
                var ovState = overlaps[j];
                if (ovState == null || ovState.ghost == null)
                {
                    overlaps.RemoveAt(j);
                    continue;
                }

                int cycle = ovState.loopCycleIndex;
                double cycleStart = rec.StartUT + cycle * cycleDuration;
                double phase = currentUT - cycleStart;

                if (phase > duration)
                {
                    // Cycle expired — position at end, explode, destroy
                    if (rec.Points.Count > 0)
                        PositionGhostAtPoint(ovState.ghost,
                            rec.Points[rec.Points.Count - 1], srfRel);
                    TriggerExplosionIfDestroyed(ovState, rec, recIdx);
                    ParsekLog.Verbose("KSCGhost",
                        $"Ghost #{recIdx} overlap cycle={cycle} expired, destroying");
                    DestroyKscGhost(ovState, recIdx);
                    overlaps.RemoveAt(j);
                    continue;
                }

                if (phase < 0) phase = 0;
                double loopUT = rec.StartUT + phase;

                InterpolateAndPositionKsc(ovState.ghost, rec.Points,
                    ref ovState.playbackIndex, loopUT, srfRel);

                if (IsGhostInCullRange(ovState.ghost))
                    ParsekFlight.ApplyPartEvents(recIdx, rec, loopUT, ovState);
                if (suppressExplosionFx)
                    ParsekFlight.StopAllRcsEmissions(ovState);
            }
        }

        /// <summary>
        /// Destroy all overlap ghosts for a recording.
        /// Called when transitioning from negative to positive interval, or on cleanup.
        /// </summary>
        void DestroyAllKscOverlapGhosts(int recIdx)
        {
            List<ParsekFlight.GhostPlaybackState> list;
            if (!kscOverlapGhosts.TryGetValue(recIdx, out list)) return;
            if (list.Count > 0)
                ParsekLog.Verbose("KSCGhost",
                    $"Destroying {list.Count} overlap ghost(s) for recording #{recIdx}");
            for (int i = 0; i < list.Count; i++)
                DestroyKscGhost(list[i], recIdx);
            list.Clear();
        }

        /// <summary>
        /// Filter recordings for KSC ghost display.
        /// </summary>
        internal static bool ShouldShowInKSC(RecordingStore.Recording rec)
        {
            if (!rec.PlaybackEnabled) return false;
            if (rec.Points == null || rec.Points.Count < 2) return false;
            // Only Kerbin recordings (KSC is on Kerbin)
            if (rec.Points[0].bodyName != "Kerbin") return false;
            return true;
        }

        /// <summary>
        /// Spawn a ghost for KSC playback. Returns null if no snapshot available.
        /// Simplified version of ParsekFlight.SpawnTimelineGhost — no camera pivot,
        /// no reentry FX, no sphere fallback.
        /// </summary>
        ParsekFlight.GhostPlaybackState SpawnKscGhost(RecordingStore.Recording rec, int index)
        {
            // Skip if no snapshot — no sphere fallback in KSC
            var snapshot = GhostVisualBuilder.GetGhostSnapshot(rec);
            if (snapshot == null)
            {
                ParsekLog.VerboseRateLimited("KSCGhost", $"spawn-no-snapshot-{index}",
                    $"Ghost #{index} \"{rec.VesselName}\": no snapshot, skipping");
                return null;
            }

            List<ParachuteGhostInfo> parachuteInfoList;
            List<JettisonGhostInfo> jettisonInfoList;
            List<EngineGhostInfo> engineInfoList;
            List<DeployableGhostInfo> deployableInfoList;
            List<HeatGhostInfo> heatInfoList;
            List<LightGhostInfo> lightInfoList;
            List<FairingGhostInfo> fairingInfoList;
            List<RcsGhostInfo> rcsInfoList;
            List<RoboticGhostInfo> roboticInfoList;

            GameObject ghost = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(
                rec, $"Parsek_KSC_{index}",
                out parachuteInfoList, out jettisonInfoList,
                out engineInfoList, out deployableInfoList,
                out heatInfoList, out lightInfoList, out fairingInfoList,
                out rcsInfoList, out roboticInfoList);

            if (ghost == null)
            {
                ParsekLog.Verbose("KSCGhost",
                    $"Ghost #{index} \"{rec.VesselName}\": BuildTimelineGhostFromSnapshot returned null");
                return null;
            }

            var state = new ParsekFlight.GhostPlaybackState
            {
                ghost = ghost,
                // cameraPivot intentionally null — RecalculateCameraPivot no-ops
                // reentryFxInfo intentionally null — RebuildReentryMeshes no-ops
                playbackIndex = 0,
                partEventIndex = 0,
                partTree = GhostVisualBuilder.BuildPartSubtreeMap(snapshot)
            };

            if (parachuteInfoList != null)
            {
                state.parachuteInfos = new Dictionary<uint, ParachuteGhostInfo>();
                for (int i = 0; i < parachuteInfoList.Count; i++)
                    state.parachuteInfos[parachuteInfoList[i].partPersistentId] = parachuteInfoList[i];
            }

            if (jettisonInfoList != null)
            {
                state.jettisonInfos = new Dictionary<uint, JettisonGhostInfo>();
                for (int i = 0; i < jettisonInfoList.Count; i++)
                    state.jettisonInfos[jettisonInfoList[i].partPersistentId] = jettisonInfoList[i];
            }

            if (engineInfoList != null)
            {
                state.engineInfos = new Dictionary<ulong, EngineGhostInfo>();
                for (int i = 0; i < engineInfoList.Count; i++)
                {
                    ulong key = FlightRecorder.EncodeEngineKey(
                        engineInfoList[i].partPersistentId, engineInfoList[i].moduleIndex);
                    state.engineInfos[key] = engineInfoList[i];
                }
            }

            if (deployableInfoList != null)
            {
                state.deployableInfos = new Dictionary<uint, DeployableGhostInfo>();
                for (int i = 0; i < deployableInfoList.Count; i++)
                    state.deployableInfos[deployableInfoList[i].partPersistentId] = deployableInfoList[i];
            }

            if (heatInfoList != null)
            {
                state.heatInfos = new Dictionary<uint, HeatGhostInfo>();
                for (int i = 0; i < heatInfoList.Count; i++)
                    state.heatInfos[heatInfoList[i].partPersistentId] = heatInfoList[i];
            }

            if (lightInfoList != null)
            {
                state.lightInfos = new Dictionary<uint, LightGhostInfo>();
                state.lightPlaybackStates =
                    new Dictionary<uint, ParsekFlight.LightPlaybackState>();
                for (int i = 0; i < lightInfoList.Count; i++)
                {
                    state.lightInfos[lightInfoList[i].partPersistentId] = lightInfoList[i];
                    state.lightPlaybackStates[lightInfoList[i].partPersistentId] =
                        new ParsekFlight.LightPlaybackState();
                }
            }

            if (fairingInfoList != null)
            {
                state.fairingInfos = new Dictionary<uint, FairingGhostInfo>();
                for (int i = 0; i < fairingInfoList.Count; i++)
                    state.fairingInfos[fairingInfoList[i].partPersistentId] = fairingInfoList[i];
            }

            if (rcsInfoList != null)
            {
                state.rcsInfos = new Dictionary<ulong, RcsGhostInfo>();
                for (int i = 0; i < rcsInfoList.Count; i++)
                {
                    ulong key = FlightRecorder.EncodeEngineKey(
                        rcsInfoList[i].partPersistentId, rcsInfoList[i].moduleIndex);
                    state.rcsInfos[key] = rcsInfoList[i];
                }
            }

            if (roboticInfoList != null)
            {
                state.roboticInfos = new Dictionary<ulong, RoboticGhostInfo>();
                for (int i = 0; i < roboticInfoList.Count; i++)
                {
                    ulong key = FlightRecorder.EncodeEngineKey(
                        roboticInfoList[i].partPersistentId, roboticInfoList[i].moduleIndex);
                    state.roboticInfos[key] = roboticInfoList[i];
                }
            }

            ParsekFlight.InitializeInventoryPlacementVisibility(rec, state);

            ParsekLog.Info("KSCGhost",
                $"Ghost #{index} \"{rec.VesselName}\" spawned" +
                $" (engines={engineInfoList?.Count ?? 0}" +
                $" rcs={rcsInfoList?.Count ?? 0}" +
                $" lights={lightInfoList?.Count ?? 0}" +
                $" deployables={deployableInfoList?.Count ?? 0}" +
                $" fairings={fairingInfoList?.Count ?? 0}" +
                $" parachutes={parachuteInfoList?.Count ?? 0})");

            return state;
        }

        /// <summary>
        /// Position a ghost by interpolating between trajectory points.
        /// Simplified version — no FloatingOrigin GhostPosEntry registration
        /// (positions are recomputed from lat/lon/alt each frame, so origin
        /// shifts are handled automatically).
        /// Stops positioning when the trajectory leaves Kerbin.
        /// </summary>
        internal void InterpolateAndPositionKsc(
            GameObject ghost, List<TrajectoryPoint> points,
            ref int cachedIndex, double targetUT,
            bool surfaceRelativeRotation)
        {
            if (points == null || points.Count == 0)
            {
                ghost.SetActive(false);
                return;
            }

            int indexBefore = TrajectoryMath.FindWaypointIndex(points, ref cachedIndex, targetUT);

            if (indexBefore < 0)
            {
                // Before recording start — position at first point
                PositionGhostAtPoint(ghost, points[0], surfaceRelativeRotation);
                return;
            }

            TrajectoryPoint before = points[indexBefore];
            TrajectoryPoint after = points[indexBefore + 1];

            // Stop positioning when trajectory leaves Kerbin
            if (before.bodyName != "Kerbin" || after.bodyName != "Kerbin")
            {
                ghost.SetActive(false);
                return;
            }

            double segmentDuration = after.ut - before.ut;
            if (segmentDuration <= 0.0001)
            {
                PositionGhostAtPoint(ghost, before, surfaceRelativeRotation);
                return;
            }

            float t = (float)((targetUT - before.ut) / segmentDuration);
            t = Mathf.Clamp01(t);

            CelestialBody bodyBefore = LookupBody(before.bodyName);
            CelestialBody bodyAfter = LookupBody(after.bodyName);
            if (bodyBefore == null || bodyAfter == null)
            {
                ParsekLog.VerboseRateLimited("KSCGhost", "interp-no-body",
                    $"Body not found: {(bodyBefore == null ? before.bodyName : after.bodyName)}");
                ghost.SetActive(false);
                return;
            }
            if (!ghost.activeSelf) ghost.SetActive(true);

            Vector3d posBefore = bodyBefore.GetWorldSurfacePosition(
                before.latitude, before.longitude, before.altitude);
            Vector3d posAfter = bodyAfter.GetWorldSurfacePosition(
                after.latitude, after.longitude, after.altitude);

            Vector3d interpolatedPos = Vector3d.Lerp(posBefore, posAfter, t);
            Quaternion interpolatedRot = Quaternion.Slerp(before.rotation, after.rotation, t);
            interpolatedRot = TrajectoryMath.SanitizeQuaternion(interpolatedRot);

            if (double.IsNaN(interpolatedPos.x) || double.IsNaN(interpolatedPos.y) ||
                double.IsNaN(interpolatedPos.z))
            {
                interpolatedPos = posBefore;
            }

            ghost.transform.position = interpolatedPos;
            // Uses bodyBefore's transform for rotation (not interpolated between bodies).
            // Negligible for KSC: both points are Kerbin, body rotation is constant.
            ghost.transform.rotation = surfaceRelativeRotation
                ? bodyBefore.bodyTransform.rotation * interpolatedRot
                : interpolatedRot;
        }

        /// <summary>
        /// Position ghost at a single trajectory point (no interpolation).
        /// </summary>
        void PositionGhostAtPoint(GameObject ghost, TrajectoryPoint point,
            bool surfaceRelativeRotation)
        {
            if (point.bodyName != "Kerbin")
            {
                ghost.SetActive(false);
                return;
            }

            CelestialBody body = LookupBody(point.bodyName);
            if (body == null) return;

            Vector3d worldPos = body.GetWorldSurfacePosition(
                point.latitude, point.longitude, point.altitude);

            Quaternion sanitized = TrajectoryMath.SanitizeQuaternion(point.rotation);
            ghost.transform.position = worldPos;
            ghost.transform.rotation = surfaceRelativeRotation
                ? body.bodyTransform.rotation * sanitized
                : sanitized;

            if (!ghost.activeSelf) ghost.SetActive(true);
        }

        /// <summary>
        /// Check if a ghost is within rendering distance of the KSC camera.
        /// Ghosts beyond 25km are deactivated to skip expensive part event processing.
        /// </summary>
        static bool IsGhostInCullRange(GameObject ghost)
        {
            if (ghost == null) return false;
            Camera cam = PlanetariumCamera.Camera;
            // Null camera during scene transitions — process all ghosts rather than skip
            if (cam == null) return true;
            float sqrDist = (ghost.transform.position - cam.transform.position).sqrMagnitude;
            if (sqrDist > GhostCullDistanceSq)
            {
                if (ghost.activeSelf) ghost.SetActive(false);
                return false;
            }
            if (!ghost.activeSelf) ghost.SetActive(true);
            return true;
        }

        /// <summary>
        /// Cached body lookup — avoids per-frame lambda allocation from FlightGlobals.Bodies.Find.
        /// </summary>
        CelestialBody LookupBody(string bodyName)
        {
            if (bodyCache != null)
            {
                CelestialBody cached;
                if (bodyCache.TryGetValue(bodyName, out cached))
                    return cached;
            }
            // Fallback if cache miss (shouldn't happen for Kerbin)
            return FlightGlobals.Bodies?.Find(b => b.name == bodyName);
        }

        /// <summary>
        /// Compute loop playback UT for a recording (positive/zero interval path only).
        /// Negative intervals use UpdateOverlapKsc with GetActiveCycles instead.
        /// Reimplemented from ParsekFlight.TryComputeLoopPlaybackUT (instance version)
        /// because the static 6-param overload doesn't return pause-window state.
        /// </summary>
        internal static bool TryComputeLoopUT(
            RecordingStore.Recording rec,
            double currentUT,
            out double loopUT,
            out int cycleIndex,
            out bool inPauseWindow)
        {
            loopUT = rec != null ? rec.StartUT : 0;
            cycleIndex = 0;
            inPauseWindow = false;
            if (rec == null || rec.Points == null || rec.Points.Count < 2) return false;
            if (currentUT < rec.StartUT) return false;

            double duration = rec.EndUT - rec.StartUT;
            if (duration <= MinLoopDurationSeconds) return false;

            double intervalSeconds = GetLoopIntervalSeconds(rec);
            double cycleDuration = duration + intervalSeconds;
            if (cycleDuration <= MinLoopDurationSeconds)
                cycleDuration = duration;

            double elapsed = currentUT - rec.StartUT;
            cycleIndex = (int)Math.Floor(elapsed / cycleDuration);
            if (cycleIndex < 0) cycleIndex = 0;

            double cycleTime = elapsed - (cycleIndex * cycleDuration);
            if (intervalSeconds > 0 && cycleTime > duration)
            {
                inPauseWindow = true;
                loopUT = rec.EndUT;
                return true;
            }

            loopUT = rec.StartUT + Math.Min(cycleTime, duration);
            return true;
        }

        /// <summary>
        /// Get the loop interval for a recording. Matches ParsekFlight logic:
        /// clamped so cycleDuration is always >= MinCycleDuration.
        /// Negative intervals mean shorter cycles (overlapping launches from KSC).
        /// </summary>
        internal static double GetLoopIntervalSeconds(RecordingStore.Recording rec)
        {
            double globalInterval = ParsekSettings.Current?.autoLoopIntervalSeconds
                                    ?? DefaultLoopIntervalSeconds;
            return ParsekFlight.ResolveLoopInterval(
                rec, globalInterval, DefaultLoopIntervalSeconds, MinCycleDuration);
        }

        /// <summary>
        /// Trigger explosion FX if the recording ended with vessel destruction.
        /// Guards against repeat firing via state.explosionFired.
        /// </summary>
        void TriggerExplosionIfDestroyed(ParsekFlight.GhostPlaybackState state,
            RecordingStore.Recording rec, int recIdx)
        {
            if (state == null || state.ghost == null) return;
            if (state.explosionFired) return;
            if (rec.TerminalStateValue != TerminalState.Destroyed) return;

            if (ParsekFlight.ShouldSuppressExplosionFx(TimeWarp.CurrentRate))
            {
                state.explosionFired = true;
                ParsekFlight.HideAllGhostParts(state);
                ParsekLog.Verbose("KSCGhost",
                    $"Explosion suppressed for ghost #{recIdx} \"{rec.VesselName}\": warp > 10x");
                return;
            }

            state.explosionFired = true;

            Vector3 worldPos = state.ghost.transform.position;
            // ComputeGhostLength excludes ParticleSystemRenderer bounds, so this
            // returns the actual vessel mesh size (not inflated by engine plume particles).
            float vesselLength = GhostVisualBuilder.ComputeGhostLength(state.ghost);

            ParsekLog.Info("KSCGhost",
                $"Explosion for ghost #{recIdx} \"{rec.VesselName}\" " +
                $"vesselLength={vesselLength:F1}m");

            var explosion = GhostVisualBuilder.SpawnExplosionFx(worldPos, vesselLength);
            if (explosion != null)
                Destroy(explosion, 6f);

            ParsekFlight.HideAllGhostParts(state);
        }

        /// <summary>
        /// Clean up a KSC ghost — stop FX, destroy canopies and GameObject.
        /// </summary>
        void DestroyKscGhost(ParsekFlight.GhostPlaybackState state, int index)
        {
            if (state == null) return;

            // Stop engine particle systems
            if (state.engineInfos != null)
            {
                foreach (var kv in state.engineInfos)
                {
                    if (kv.Value.particleSystems == null) continue;
                    for (int i = 0; i < kv.Value.particleSystems.Count; i++)
                    {
                        var ps = kv.Value.particleSystems[i];
                        if (ps != null)
                        {
                            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                            ps.Clear(true);
                        }
                    }
                }
            }

            // Stop RCS particle systems
            if (state.rcsInfos != null)
            {
                foreach (var kv in state.rcsInfos)
                {
                    if (kv.Value.particleSystems == null) continue;
                    for (int i = 0; i < kv.Value.particleSystems.Count; i++)
                    {
                        var ps = kv.Value.particleSystems[i];
                        if (ps != null)
                        {
                            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                            ps.Clear(true);
                        }
                    }
                }
            }

            ParsekFlight.DestroyAllFakeCanopies(state);

            if (state.ghost != null)
                Destroy(state.ghost);

            ParsekLog.Verbose("KSCGhost", $"Ghost #{index} destroyed");
        }

        #endregion

        void OnDestroy()
        {
            // Clean up all KSC ghosts (primary + overlap)
            foreach (var kv in kscGhosts)
                DestroyKscGhost(kv.Value, kv.Key);
            kscGhosts.Clear();

            foreach (var kv in kscOverlapGhosts)
                for (int i = 0; i < kv.Value.Count; i++)
                    DestroyKscGhost(kv.Value[i], kv.Key);
            kscOverlapGhosts.Clear();
            loggedGhostSpawn.Clear();

            ParsekLog.Info("KSC", "ParsekKSC destroyed");
            ui?.Cleanup();
            if (toolbarControl != null)
            {
                toolbarControl.OnDestroy();
                Destroy(toolbarControl);
            }
        }
    }
}
