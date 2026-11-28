// [ERS-exempt] The near-watch resume after a hand-back finds the watched recording by id in
// the raw committed list, the index space WatchModeController keys on (as FarWatchController.cs).
using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// The far watch's warp half (design-far-watch.md 8.4): the watched ghost's state as stock's
    /// warp limits read it, Parsek's ceiling over the return point and the drone's terrain floor,
    /// and the HIGH / LOW mode decision taken at the ghost. The Harmony side is
    /// <see cref="FarWatchWarpLimitPatch"/> / <see cref="FarWatchWarpModePatch"/>.
    /// </summary>
    internal sealed partial class FarWatchController
    {
        private const string WarpTag = "FarWatchWarp";
        // The ghost's proper acceleration is a finite difference of its playback velocity; it is
        // read over at least this much game time so a sample seam does not read as thrust.
        internal const double GeeWindowSeconds = 1.0;
        private float stockLimitRealTime = -100f;
        private bool pendingWatchEntryReady;
        private bool postTeardownPending;
        private string postTeardownResumeId;
        private Vessel postTeardownSwitchTarget;
        private Action<Vessel> postTeardownSwitchReplay;
        private FarWatchReturnCause postTeardownMessageCause;
        private string postTeardownShipName;

        /// <summary>True while the drone holds focus for a running watch (the warp patches' gate).</summary>
        internal bool OwnsWarp => state == FarWatchState.Following || state == FarWatchState.HandoverOut;

        /// <summary>
        /// Refreshes the watched ghost's warp state from one follow tick's resolved ghost position
        /// and velocity (the values the drone is written from, before the terrain floor).
        /// </summary>
        private void UpdateGhostWarpState(CelestialBody body, Vector3d world, Vector3d r, Vector3d vObt, Vector3d vSrf, double ut)
        {
            double alt = body.GetAltitude(world);
            double terrain = SafeTerrain(body, body.GetLatitude(world), body.GetLongitude(world));
            if (body.ocean && terrain < 0.0)
                terrain = 0.0;
            Vector3d up = r.normalized;
            double g = body.gravParameter / Math.Max(1.0, r.sqrMagnitude);
            ghostWarpBody = body;
            ghostAltitude = alt;
            ghostHeight = alt - terrain;
            ghostVUp = Vector3d.Dot(vSrf, up);
            ghostSrfSpeedSq = vSrf.sqrMagnitude;
            ghostGravity = g;
            ghostLanded = ghostHeight < GhostLandedHeightMeters && Math.Sqrt(ghostSrfSpeedSq) < GhostLandedSpeed;
            if (double.IsNaN(prevGhostVUT) || ut < prevGhostVUT)
            {
                prevGhostVObt = vObt;
                prevGhostR = r;
                prevGhostVUT = ut;
                ghostGee = 0.0;
            }
            else if (ut - prevGhostVUT >= GeeWindowSeconds)
            {
                ghostGee = FarWatchWarpLimits.NonGravitationalGee(prevGhostR, prevGhostVObt, r, vObt,
                    body.gravParameter, ut - prevGhostVUT);
                prevGhostVObt = vObt;
                prevGhostR = r;
                prevGhostVUT = ut;
            }
            FarWatchConic conic;
            ghostHasConic = FarWatchConicMath.TryFromStateVector(r, vObt, body.gravParameter, ut, body.bodyName, out conic);
            ghostConic = conic;
            ghostWarpValid = true;
        }

        /// <summary>
        /// Stock's on-rails rate limit read at the watched ghost (or at the ship in a hold watch)
        /// instead of at the drone (8.4). False when the far watch does not own the warp, so stock
        /// runs its own method.
        /// </summary>
        internal bool TryEvaluateStockWarpLimit(TimeWarp tw, int tgtRateIdx, bool lookAhead, out FarWatchWarpResult result)
        {
            result = default(FarWatchWarpResult);
            if (!OwnsWarp || tw == null)
                return false;
            FarWatchWarpInputs i;
            string source;
            if (!TryBuildWarpInputs(tw, tgtRateIdx, lookAhead, out i, out source))
            {
                // Fallback (8.4): with no reading of the ghost, no warp near the ground.
                result.RateIndex = Math.Min(tgtRateIdx, tw.maxPhysicsRate_index);
                result.Reason = ClearToSaveStatus.NOT_WHILE_ABOUT_TO_CRASH;
                ParsekLog.InfoRateLimited(WarpTag, "warp-limit-fallback",
                    FarWatchWarpLimits.FormatLimitLine("none", "fallback", RateAt(tw, tgtRateIdx), RateAt(tw, result.RateIndex)), 5.0);
                return true;
            }
            result = FarWatchWarpLimits.EvaluateStock(i);
            if (result.RateIndex < tgtRateIdx)
            {
                stockLimitRealTime = Time.realtimeSinceStartup;
                ParsekLog.InfoRateLimited(WarpTag, "warp-limit-stock-" + result.Reason,
                    FarWatchWarpLimits.FormatLimitLine(source, "stock:" + result.Reason,
                        RateAt(tw, tgtRateIdx), RateAt(tw, result.RateIndex)), 5.0);
            }
            return true;
        }

        private bool TryBuildWarpInputs(TimeWarp tw, int tgt, bool lookAhead, out FarWatchWarpInputs i, out string source)
        {
            i = new FarWatchWarpInputs
            {
                TargetRateIndex = tgt,
                LookAhead = lookAhead,
                Mode = GameSettings.ORBIT_WARP_MAXRATE_MODE,
                PeModeSurfaceMargin = GameSettings.ORBIT_WARP_PEMODE_SURFACE_MARGIN,
                WarpRates = tw.warpRates,
                MaxPhysicsRateIndex = tw.maxPhysicsRate_index,
                FixedDeltaTime = Time.fixedDeltaTime,
                GThreshold = TimeWarp.GThreshold,
                AnyLoadedEvaOnLadder = false,
                NowUT = Planetarium.GetUniversalTime(),
            };
            CelestialBody body;
            if (followGhost && ghostWarpValid && ghostWarpBody != null)
            {
                source = "ghost";
                body = ghostWarpBody;
                i.LandedOrSplashed = ghostLanded;
                i.SrfSpeedSq = ghostSrfSpeedSq;
                i.Altitude = ghostAltitude;
                i.GeeForce = ghostGee;
                i.HasConic = ghostHasConic;
                i.Conic = ghostConic;
            }
            else if (ship != null && ship.mainBody != null)
            {
                source = "ship";
                body = ship.mainBody;
                i.LandedOrSplashed = ship.LandedOrSplashed;
                i.SrfSpeedSq = ship.srfSpeed * ship.srfSpeed;
                i.Altitude = ship.altitude;
                i.GeeForce = 0.0;
                FarWatchConic conic;
                i.HasConic = !ship.LandedOrSplashed && ship.orbit != null
                    && FarWatchConicMath.TryFromStateVector(ship.orbit.getRelativePositionAtUT(i.NowUT),
                        ship.orbit.getOrbitalVelocityAtUT(i.NowUT), body.gravParameter, i.NowUT, body.bodyName, out conic)
                    && Assign(out i.Conic, conic);
            }
            else
            {
                source = "none";
                return false;
            }
            i.AltitudeLimits = body.timeWarpAltitudeLimits;
            i.HasAtmosphere = body.atmosphere;
            i.AtmosphereDepth = body.atmosphereDepth;
            i.BodyRadius = body.Radius;
            i.HasSolidSurface = body.pqsController != null;
            if (i.HasSolidSurface)
            {
                i.PqsMaxHeight = body.pqsController.radiusMax - body.pqsController.radius;
                i.PqsMeshVertMax = body.pqsController.meshVertMax;
            }
            return true;
        }

        private static bool Assign(out FarWatchConic into, FarWatchConic value)
        {
            into = value;
            return true;
        }

        private static float RateAt(TimeWarp tw, int idx)
        {
            if (tw == null || tw.warpRates == null || tw.warpRates.Length == 0) return 1f;
            if (idx < 0) idx = 0;
            if (idx >= tw.warpRates.Length) idx = tw.warpRates.Length - 1;
            return tw.warpRates[idx];
        }

        /// <summary>
        /// Parsek's ceiling (8.4), every physics tick of a running watch: on-rails warp drops to
        /// the highest rate that leaves the return point at least 10 s of real time away and keeps
        /// the drone's terrain floor under 1 km. Also refreshes the overlay's warp-limit cause.
        /// </summary>
        private void ApplyWarpCeiling()
        {
            TimeWarp tw = TimeWarp.fetch;
            if (tw == null || !OwnsWarp)
            {
                warpLimitCause = "none";
                return;
            }
            double now = Planetarium.GetUniversalTime();
            double vUp = followGhost && ghostWarpValid ? ghostVUp : 0.0;
            double g = followGhost && ghostWarpValid ? ghostGravity : 0.0;
            double height = followGhost && ghostWarpValid ? ghostHeight : double.NaN;
            int ceiling = FarWatchWarpLimits.CeilingRateIndex(tw.warpRates, Time.fixedDeltaTime, now, returnUT, vUp, g, height);
            string cause = "none";
            if (tw.Mode == TimeWarp.Modes.HIGH)
            {
                if (tw.current_rate_index > ceiling)
                {
                    string rule = FarWatchWarpLimits.DescribeCeiling(tw.warpRates, Time.fixedDeltaTime, now, returnUT,
                        vUp, g, height, ceiling);
                    ParsekLog.InfoRateLimited(WarpTag, "warp-limit-ceiling-" + rule,
                        FarWatchWarpLimits.FormatLimitLine(followGhost ? "ghost" : "ship", "ceiling:" + rule,
                            RateAt(tw, tw.current_rate_index), RateAt(tw, ceiling)), 5.0);
                    TimeWarp.SetRate(ceiling, true, false);
                }
                if (tw.current_rate_index >= ceiling && tw.warpRates != null && ceiling < tw.warpRates.Length - 1)
                    cause = FarWatchWarpLimits.DescribeCeiling(tw.warpRates, Time.fixedDeltaTime, now, returnUT,
                        vUp, g, height, ceiling);
            }
            if (cause == "none" && Time.realtimeSinceStartup - stockLimitRealTime < 1f)
                cause = "stock";
            if (cause != warpLimitCause)
                ParsekLog.Verbose(WarpTag, "warp limit cause " + warpLimitCause + " -> " + cause);
            warpLimitCause = cause;
        }

        /// <summary>
        /// Stock's HIGH / LOW switch (<c>TimeWarp.Update</c>) reads the drone; while a ghost is
        /// followed the ghost decides (8.4). Called every frame; the switch goes through stock's
        /// own <c>setMode</c> under the token, at a rate stock allows a switch at.
        /// </summary>
        private void ApplyGhostWarpMode()
        {
            TimeWarp tw = TimeWarp.fetch;
            if (tw == null || state != FarWatchState.Following || !followGhost || !ghostWarpValid || ghostWarpBody == null)
                return;
            TimeWarp.Modes want = DesiredGhostWarpMode();
            if (tw.Mode == want || tw.current_rate_index > tw.maxModeSwitchRate_index)
                return;
            ModeSwitchTokenHeld = true;
            bool ok = false;
            try
            {
                ok = InvokeSetModeCore(tw, want);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(WarpTag, "setMode threw " + ex.GetType().Name);
            }
            finally
            {
                ModeSwitchTokenHeld = false;
            }
            ParsekLog.Info(WarpTag, string.Format(CultureInfo.InvariantCulture,
                "warp mode set at the ghost mode={0} ok={1} ghostAlt={2} landed={3}",
                want, ok, ghostAltitude.ToString("F0", CultureInfo.InvariantCulture), ghostLanded));
        }

        /// <summary>The mode the watched ghost wants (stock's rule read at the ghost).</summary>
        internal TimeWarp.Modes DesiredGhostWarpMode()
        {
            bool pressure = ghostWarpBody != null && ghostWarpBody.atmosphere && ghostAltitude < ghostWarpBody.atmosphereDepth;
            return FarWatchWarpLimits.WantsLowMode(ghostLanded, pressure, ghostSrfSpeedSq)
                ? TimeWarp.Modes.LOW : TimeWarp.Modes.HIGH;
        }

        /// <summary>True when a stock mode switch to <paramref name="mode"/> disagrees with the ghost.</summary>
        internal bool ShouldRefuseStockModeSwitch(TimeWarp.Modes mode)
        {
            if (ModeSwitchTokenHeld || state != FarWatchState.Following || !followGhost || !ghostWarpValid)
                return false;
            return mode != DesiredGhostWarpMode();
        }

        private static System.Reflection.MethodInfo setModeMethod;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool InvokeSetModeCore(TimeWarp tw, TimeWarp.Modes mode)
        {
            if (setModeMethod == null)
                setModeMethod = HarmonyLib.AccessTools.Method(typeof(TimeWarp), "setMode", new[] { typeof(TimeWarp.Modes) });
            if (setModeMethod == null)
                return false;
            object r = setModeMethod.Invoke(tw, new object[] { mode });
            return r is bool && (bool)r;
        }

        // ------------------------------------------------------------------ after the hand-back

        /// <summary>
        /// The hand-back's follow-ups (8.4 / 8.5), run in the first Update after the teardown: one
        /// screen message for a forced return, today's near watch on the ghost that came near, or
        /// today's switch to the vessel the player picked.
        /// </summary>
        private void RunPostTeardown()
        {
            if (!postTeardownPending || IsRunning)
                return;
            postTeardownPending = false;
            if (postTeardownMessageCause != FarWatchReturnCause.None)
            {
                string msg = FarWatchDecisions.ForcedReturnMessage(postTeardownMessageCause, postTeardownShipName);
                ParsekLog.Info(Tag, "forced-return screen message: " + msg);
                ParsekLog.ScreenMessage(msg, 5f);
            }
            if (postTeardownResumeId != null)
            {
                var committed = RecordingStore.CommittedRecordings;
                int idx = -1;
                for (int k = 0; k < committed.Count; k++)
                {
                    if (committed[k] != null && committed[k].RecordingId == postTeardownResumeId)
                    {
                        idx = k;
                        break;
                    }
                }
                ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                    "near watch resumes after the hand-back id={0} rec=#{1}", postTeardownResumeId, idx));
                if (idx >= 0 && !host.IsWatchingGhost)
                    host.EnterNearWatch(idx);
            }
            if (postTeardownSwitchTarget != null)
            {
                Vessel target = postTeardownSwitchTarget;
                bool alive = IsVesselAliveCore(target);
                bool active = alive && IsActiveVesselCore(target);
                FarWatchPostSwitch d = FarWatchDecisions.DecidePostTeardownSwitch(true, alive, active,
                    postTeardownSwitchReplay != null);
                ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                    "switch after the hand-back target={0} loaded={1} decision={2}",
                    alive ? target.persistentId : 0u, alive && target.loaded, d));
                if (d == FarWatchPostSwitch.ReplayThroughGates)
                {
                    try
                    {
                        postTeardownSwitchReplay(target);
                    }
                    catch (Exception ex)
                    {
                        ParsekLog.Warn(Tag, "the switch after the hand-back threw " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
            postTeardownResumeId = null;
            postTeardownSwitchTarget = null;
            postTeardownSwitchReplay = null;
            postTeardownMessageCause = FarWatchReturnCause.None;
        }

        /// <summary>Captures the follow-ups at the teardown, before the per-watch refs are cleared.</summary>
        private void CapturePostTeardown()
        {
            postTeardownPending = forcedReturnCause != FarWatchReturnCause.None
                || resumeNearWatchRecordingId != null || pendingSwitchTarget != null;
            postTeardownMessageCause = forcedReturnCause;
            postTeardownShipName = shipName;
            postTeardownResumeId = resumeNearWatchRecordingId;
            postTeardownSwitchTarget = pendingSwitchTarget;
            postTeardownSwitchReplay = pendingSwitchReplay;
            forcedReturnCause = FarWatchReturnCause.None;
            resumeNearWatchRecordingId = null;
            pendingSwitchTarget = null;
            pendingSwitchReplay = null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsVesselAliveCore(Vessel v)
        {
            try
            {
                return v != null && v.state != Vessel.State.DEAD;
            }
            catch (Exception)
            {
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsActiveVesselCore(Vessel v)
        {
            try
            {
                return FlightGlobals.ActiveVessel == v;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
