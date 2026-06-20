using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.Reaim
{
    /// <summary>
    /// Auto-drops time-warp to realtime (1x) right as a re-aim looped descent leaves the loiter and begins, so
    /// the brief descent clip (the re-entry/landing, or the rendezvous-and-dock) is actually watchable instead of
    /// being warped straight past. A looped mission's descent renders for only its recorded duration (~hours)
    /// once per multi-hundred-day loop, so by hand the player almost always steps over its window.
    ///
    /// <para>Split for testability: the per-frame DECISION (<see cref="ShouldDropWarp"/>) is pure and Unity-free;
    /// the Unity calls (read the warp rate / frame dt, set the rate, read the enable setting) are injected via the
    /// seam delegates, wired only inside FLIGHT / TRACKING-STATION by <see cref="DescentWarpControlAddonBase"/> and
    /// left null in xUnit — so the resolver's per-frame <see cref="NotifyDescentState"/> call is a no-op in tests.</para>
    ///
    /// <para>The decision is PREDICTIVE during the loiter: it drops one frame BEFORE the loop clock would cross the
    /// trigger UT (computed from the current warp rate * frame dt), so even a high warp that would otherwise step a
    /// whole frame over the descent window lands a frame inside it. It fires at most ONCE per descent cycle.</para>
    /// </summary>
    internal static class DescentWarpControl
    {
        // --- Unity seam (wired by the scene addon; null in xUnit so NotifyDescentState no-ops) ---
        internal static Func<float> WarpRateProvider;    // TimeWarp.CurrentRate
        internal static Func<float> DeltaTimeProvider;   // Time.unscaledDeltaTime
        internal static Action WarpToRealtime;           // TimeWarp.SetRate(0, true)
        internal static Func<bool> EnabledProvider;      // the user setting; null => treated as enabled

        // Per mission (PhaseAnchorUT.SpanStartUT), the descent cycle we have already dropped for, so the warp is
        // yanked once as the descent starts and not re-fought if the player chooses to re-warp afterwards.
        private static readonly Dictionary<string, long> droppedCycleByKey = new Dictionary<string, long>();

        internal static void ResetForSceneChange() => droppedCycleByKey.Clear();

        /// <summary>
        /// Pure: should we drop to realtime THIS frame? True when the descent is imminent or live and we have not
        /// dropped yet this cycle and the warp is above 1x and we are not already past the descent:
        /// <list type="bullet">
        /// <item><c>Descent</c> phase: the icon has left the loiter and is on the clip — drop now.</item>
        /// <item><c>Loiter</c> phase: drop when the NEXT warp step (<c>currentUT + warpRate*deltaTime</c>) would
        ///   reach <paramref name="triggerUT"/> — i.e. the very last loiter frame before the icon leaves, so the
        ///   crossing itself happens at 1x and a high warp cannot step over the whole window.</item>
        /// </list>
        /// Returns false for Inert/Done, NaN timing, rate &lt;= 1x, or once already dropped this cycle.
        /// </summary>
        internal static bool ShouldDropWarp(
            DescentTrigger.DescentHeadPhase phase, double currentUT, double triggerUT, double descentEndUT,
            float warpRate, float deltaTime, bool alreadyDroppedThisCycle)
        {
            if (alreadyDroppedThisCycle) return false;
            if (warpRate <= 1.0f) return false;
            if (double.IsNaN(triggerUT) || double.IsNaN(descentEndUT)) return false;
            if (currentUT >= descentEndUT) return false; // already past the descent — do not yank warp after the fact

            if (phase == DescentTrigger.DescentHeadPhase.Descent)
                return true; // icon already on the clip
            if (phase == DescentTrigger.DescentHeadPhase.Loiter)
            {
                double nextUT = currentUT + (double)warpRate * Math.Max(0.0, deltaTime);
                return nextUT >= triggerUT; // next step would cross into the descent — drop now, before it
            }
            return false; // Inert / Done
        }

        /// <summary>
        /// Called from the descent resolver each frame for a descent member (the resolver is reached in BOTH the
        /// tracking station and FLIGHT — via the polyline Driver's per-frame walk — so this one call site covers
        /// both scenes). Drops warp to realtime once as the descent leaves the loiter. No-op when the Unity seam
        /// is unwired (xUnit / other scenes) or the user setting is off.
        /// </summary>
        internal static void NotifyDescentState(
            string missionKey, long cycle, DescentTrigger.DescentHeadPhase phase,
            double currentUT, double triggerUT, double descentEndUT)
        {
            if (WarpRateProvider == null || WarpToRealtime == null) return; // unwired (tests / non-flight scenes)
            if (EnabledProvider != null && !EnabledProvider()) return;      // user disabled auto-slow

            bool already = droppedCycleByKey.TryGetValue(missionKey, out long dc) && dc == cycle;
            float rate = WarpRateProvider();
            float dt = DeltaTimeProvider != null ? DeltaTimeProvider() : 0.02f;
            if (!ShouldDropWarp(phase, currentUT, triggerUT, descentEndUT, rate, dt, already)) return;

            droppedCycleByKey[missionKey] = cycle;
            ParsekLog.Info("ReaimDescent", string.Format(CultureInfo.InvariantCulture,
                "descent warp auto-drop: mission={0} cycle={1} phase={2} rate={3:F0}x -> 1x at UT {4:F1} " +
                "(trigger {5:F1}); icon leaving loiter for descent",
                missionKey, cycle, phase, rate, currentUT, triggerUT));
            WarpToRealtime();
        }
    }

    /// <summary>Wires the <see cref="DescentWarpControl"/> Unity seam in FLIGHT and the TRACKING STATION (the two
    /// scenes the descent renders in), and clears the per-cycle dedup on scene entry. Unwires on scene exit so the
    /// DDOL polyline Driver's resolver walk in any other scene stays a no-op.</summary>
    internal abstract class DescentWarpControlAddonBase : MonoBehaviour
    {
        protected void Awake()
        {
            DescentWarpControl.WarpRateProvider = () => TimeWarp.CurrentRate;
            DescentWarpControl.DeltaTimeProvider = () => Time.unscaledDeltaTime;
            DescentWarpControl.WarpToRealtime = () =>
            {
                if (TimeWarp.fetch != null)
                    TimeWarp.SetRate(0, true); // index 0 = 1x realtime; instant so a high warp stops immediately
            };
            DescentWarpControl.EnabledProvider = () =>
                ParsekSettings.Current == null || ParsekSettings.Current.autoDropWarpForDescent;
            DescentWarpControl.ResetForSceneChange();
        }

        protected void OnDestroy()
        {
            DescentWarpControl.WarpRateProvider = null;
            DescentWarpControl.DeltaTimeProvider = null;
            DescentWarpControl.WarpToRealtime = null;
            DescentWarpControl.EnabledProvider = null;
        }
    }

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal class DescentWarpControlFlightAddon : DescentWarpControlAddonBase { }

    [KSPAddon(KSPAddon.Startup.TrackingStation, false)]
    internal class DescentWarpControlTrackingAddon : DescentWarpControlAddonBase { }
}
