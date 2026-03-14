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

        private const double DefaultLoopIntervalSeconds = 10.0;
        private const double MinLoopDurationSeconds = 0.001;
        private const double MinCycleDuration = 0.001;

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

            int ghostCount = 0;
            var committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
            {
                if (ShouldShowInKSC(committed[i]))
                    ghostCount++;
            }

            ParsekLog.Info("KSC",
                $"ParsekKSC initialized, {committed.Count} committed recordings, {ghostCount} eligible for KSC ghost playback");
        }

        void OnGUI()
        {
            if (!showUI) return;

            windowRect = ClickThruBlocker.GUILayoutWindow(
                GetInstanceID(), windowRect, ui.DrawWindow,
                "Parsek", GUILayout.Width(250));

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

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (!ShouldShowInKSC(rec)) continue;

                double targetUT;
                int cycleIndex;
                bool inRange;
                bool inPauseWindow = false;

                if (rec.LoopPlayback)
                {
                    inRange = TryComputeLoopUT(rec, currentUT,
                        out targetUT, out cycleIndex, out inPauseWindow);
                }
                else
                {
                    inRange = currentUT >= rec.StartUT && currentUT <= rec.EndUT;
                    targetUT = currentUT;
                    cycleIndex = 0;
                }

                ParsekFlight.GhostPlaybackState state;
                kscGhosts.TryGetValue(i, out state);
                bool ghostActive = state != null && state.ghost != null;

                if (inRange && !inPauseWindow)
                {
                    // Loop cycle change: destroy + respawn for clean visual state + reset event indices
                    if (ghostActive && rec.LoopPlayback && state.loopCycleIndex != cycleIndex)
                    {
                        DestroyKscGhost(state, i);
                        kscGhosts.Remove(i);
                        ghostActive = false;
                        state = null;
                    }

                    if (!ghostActive)
                    {
                        state = SpawnKscGhost(rec, i);
                        if (state == null) continue;
                        state.loopCycleIndex = cycleIndex;
                        kscGhosts[i] = state;
                    }

                    InterpolateAndPositionKsc(
                        state.ghost, rec.Points,
                        ref state.playbackIndex, targetUT,
                        rec.RecordingFormatVersion >= 5);

                    ParsekFlight.ApplyPartEvents(i, rec, targetUT, state);
                }
                else if (ghostActive)
                {
                    if (inPauseWindow)
                    {
                        // In the pause between loop cycles — hide ghost parts
                        if (!state.pauseHidden)
                        {
                            state.pauseHidden = true;
                            ParsekFlight.HideAllGhostParts(state);
                            ParsekLog.Verbose("KSCGhost",
                                $"Ghost #{i} \"{rec.VesselName}\" hidden during loop pause window");
                        }
                    }
                    else
                    {
                        DestroyKscGhost(state, i);
                        kscGhosts.Remove(i);
                    }
                }
            }
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
            // Skip chain mid-segments (they'd create overlapping ghosts)
            if (!string.IsNullOrEmpty(rec.ChainId) && rec.ChainIndex > 0) return false;
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
            if (GhostVisualBuilder.GetGhostSnapshot(rec) == null)
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
                partTree = GhostVisualBuilder.BuildPartSubtreeMap(
                    GhostVisualBuilder.GetGhostSnapshot(rec))
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
        /// </summary>
        internal static void InterpolateAndPositionKsc(
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

            double segmentDuration = after.ut - before.ut;
            if (segmentDuration <= 0.0001)
            {
                PositionGhostAtPoint(ghost, before, surfaceRelativeRotation);
                return;
            }

            float t = (float)((targetUT - before.ut) / segmentDuration);
            t = Mathf.Clamp01(t);

            CelestialBody bodyBefore = FlightGlobals.Bodies.Find(b => b.name == before.bodyName);
            CelestialBody bodyAfter = FlightGlobals.Bodies.Find(b => b.name == after.bodyName);
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
            ghost.transform.rotation = surfaceRelativeRotation
                ? bodyBefore.bodyTransform.rotation * interpolatedRot
                : interpolatedRot;
        }

        /// <summary>
        /// Position ghost at a single trajectory point (no interpolation).
        /// </summary>
        static void PositionGhostAtPoint(GameObject ghost, TrajectoryPoint point,
            bool surfaceRelativeRotation)
        {
            CelestialBody body = FlightGlobals.Bodies.Find(b => b.name == point.bodyName);
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
        /// Compute loop playback UT for a recording.
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

        static double GetLoopIntervalSeconds(RecordingStore.Recording rec)
        {
            if (rec == null) return DefaultLoopIntervalSeconds;
            if (double.IsNaN(rec.LoopIntervalSeconds) || double.IsInfinity(rec.LoopIntervalSeconds))
                return DefaultLoopIntervalSeconds;
            double duration = rec.EndUT - rec.StartUT;
            return Math.Max(-duration + MinCycleDuration, rec.LoopIntervalSeconds);
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
            // Clean up all KSC ghosts
            foreach (var kv in kscGhosts)
                DestroyKscGhost(kv.Value, kv.Key);
            kscGhosts.Clear();

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
