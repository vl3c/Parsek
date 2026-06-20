using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.Reaim
{
    // ============================================================================================================
    // TEMPORARY / PROVISIONAL — DIAGNOSTIC SCAFFOLD, NOT A SHIPPING FEATURE. Exists only to capture ONE clean
    // in-window descent log (the descent window is ~0.05% of the loop and manual warp keeps stepping over it).
    // Once the descent is confirmed to render cleanly, REVERT this whole feature: delete DescentWarpControl.cs +
    // DescentWarpControlTests.cs, the ParsekSettings.autoDropWarpForDescent field, and the NotifyDescentState call
    // in GhostPlaybackLogic.ResolveTrackingStationSampleUT. Do NOT add it to the CHANGELOG as a feature.
    // ============================================================================================================

    internal enum DescentWarpAction { None, StepDown, DropToRealtime }

    /// <summary>
    /// Decelerates time-warp so a re-aim looped descent (re-entry/landing or station dock) is watchable instead of
    /// being warped straight past — the descent renders for only its recorded duration (~hours) once per
    /// multi-hundred-day loop, so at high warp a single frame can leap clean over its whole window.
    ///
    /// <para>A single-frame lookahead is NOT enough: at extreme warp (1,000,000x) one frame can jump tens of
    /// thousands of seconds — wider than the loiter itself — so the "next step crosses the trigger" check undershoots
    /// and the following frame steps over the window. Instead this RAMPS the warp down: every frame, while the loop
    /// clock is still approaching the trigger, if the next warp step would consume more than half the remaining
    /// time-to-trigger it steps the rate down ONE level (1M -> 100k -> ... -> 1x). That physically cannot overshoot,
    /// and lands at 1x right as the descent leaves the loiter.</para>
    ///
    /// <para>Split for testability: the per-frame DECISION (<see cref="DecideWarpAction"/>) is pure and Unity-free;
    /// the Unity calls (read the warp rate / rate index / frame dt / frame count, set the rate, read the setting) are
    /// injected via the seam delegates, wired only inside FLIGHT / TRACKING-STATION by the concrete addons below and
    /// left null in xUnit — so the resolver's per-frame <see cref="NotifyDescentState"/> call is a no-op in tests.</para>
    /// </summary>
    internal static class DescentWarpControl
    {
        // --- Unity seam (wired by the scene addons; null in xUnit so NotifyDescentState no-ops) ---
        internal static Func<float> WarpRateProvider;    // TimeWarp.CurrentRate
        internal static Func<int> RateIndexProvider;     // TimeWarp.CurrentRateIndex
        internal static Func<float> DeltaTimeProvider;   // Time.unscaledDeltaTime
        internal static Func<int> FrameProvider;         // Time.frameCount
        internal static Action<int> SetRateAction;       // TimeWarp.SetRate(index, true)
        internal static Func<bool> EnabledProvider;      // the user setting; null => treated as enabled

        // Per mission (PhaseAnchorUT.SpanStartUT), the descent cycle we have already dropped to 1x for, so once the
        // descent starts we stop acting and the player can re-warp freely afterwards.
        private static readonly Dictionary<string, long> droppedCycleByKey = new Dictionary<string, long>();
        // The resolver calls NotifyDescentState once per descent MEMBER per frame (4+ calls/frame for one mission),
        // all with the same shared state; act at most once per rendered frame.
        private static int lastActionFrame = -1;

        internal static void ResetForSceneChange()
        {
            droppedCycleByKey.Clear();
            lastActionFrame = -1;
        }

        /// <summary>
        /// Pure: what to do with the warp THIS frame.
        /// <list type="bullet">
        /// <item>In the window (<c>Descent</c> phase, or <c>currentUT &gt;= triggerUT</c>): <c>DropToRealtime</c> if
        ///   still warping, else <c>None</c>.</item>
        /// <item>Approaching (Inert/Loiter, <c>currentUT &lt; triggerUT</c>) and warping above 1x: <c>StepDown</c>
        ///   when the next warp step (<c>warpRate*deltaTime</c>) would consume &gt; half the remaining
        ///   time-to-trigger — i.e. decelerate one level so the clock cannot leap over the window.</item>
        /// </list>
        /// <c>None</c> for NaN timing, already past the descent, already at 1x / lowest index, or once dropped.
        /// </summary>
        internal static DescentWarpAction DecideWarpAction(
            DescentTrigger.DescentHeadPhase phase, double currentUT, double triggerUT, double descentEndUT,
            float warpRate, int currentRateIndex, float deltaTime, bool alreadyDroppedThisCycle)
        {
            if (alreadyDroppedThisCycle) return DescentWarpAction.None;
            if (double.IsNaN(triggerUT) || double.IsNaN(descentEndUT)) return DescentWarpAction.None;
            if (currentUT >= descentEndUT) return DescentWarpAction.None; // past the descent — do not yank warp

            if (phase == DescentTrigger.DescentHeadPhase.Descent || currentUT >= triggerUT)
                return warpRate > 1.0f ? DescentWarpAction.DropToRealtime : DescentWarpAction.None;

            // Approaching the trigger.
            if (warpRate <= 1.0f) return DescentWarpAction.None;        // already realtime
            if (currentRateIndex <= 0) return DescentWarpAction.None;   // already at the lowest rate
            double timeToTrigger = triggerUT - currentUT;
            if (timeToTrigger <= 0.0) return DescentWarpAction.DropToRealtime; // safety (covered above, defensive)
            double nextStep = (double)warpRate * Math.Max(0.0, deltaTime);
            if (nextStep > timeToTrigger * 0.5)
                return DescentWarpAction.StepDown;
            return DescentWarpAction.None;
        }

        /// <summary>
        /// Called from the descent resolver each frame for a descent member (the resolver is reached in BOTH the
        /// tracking station and FLIGHT — via the polyline Driver's per-frame walk — so one call site covers both
        /// scenes). Applies the deceleration ramp / realtime drop. No-op when the Unity seam is unwired (xUnit /
        /// non-flight scenes) or the setting is off; acts at most once per frame and drops once per descent cycle.
        /// </summary>
        internal static void NotifyDescentState(
            string missionKey, long cycle, DescentTrigger.DescentHeadPhase phase,
            double currentUT, double triggerUT, double descentEndUT)
        {
            if (WarpRateProvider == null || SetRateAction == null) return; // unwired (tests / non-flight scenes)
            if (EnabledProvider != null && !EnabledProvider()) return;     // user disabled

            int frame = FrameProvider != null ? FrameProvider() : 0;
            if (frame != 0 && frame == lastActionFrame) return; // already acted this frame (per-member dedup)

            bool already = droppedCycleByKey.TryGetValue(missionKey, out long dc) && dc == cycle;
            float rate = WarpRateProvider();
            int index = RateIndexProvider != null ? RateIndexProvider() : 0;
            float dt = DeltaTimeProvider != null ? DeltaTimeProvider() : 0.02f;

            DescentWarpAction action = DecideWarpAction(
                phase, currentUT, triggerUT, descentEndUT, rate, index, dt, already);

            // Always-on (rate-limited) decision trace so a no-op is debuggable from a single log.
            ParsekLog.VerboseRateLimited("ReaimDescent", "warpctl." + missionKey + "." + phase,
                string.Format(CultureInfo.InvariantCulture,
                    "warp-ctl mission={0} cycle={1} phase={2} rate={3:F0}x idx={4} dt={5:F3} curUT={6:F1} " +
                    "trig={7:F1} ttt={8:F1} dropped={9} action={10}",
                    missionKey, cycle, phase, rate, index, dt, currentUT, triggerUT,
                    triggerUT - currentUT, already, action));

            if (action == DescentWarpAction.None) return;
            lastActionFrame = frame;

            if (action == DescentWarpAction.StepDown)
            {
                SetRateAction(Math.Max(0, index - 1));
                ParsekLog.VerboseRateLimited("ReaimDescent", "warpstep." + missionKey,
                    string.Format(CultureInfo.InvariantCulture,
                        "descent warp ramp: mission={0} step idx {1} -> {2} (rate {3:F0}x, {4:F0}s to trigger)",
                        missionKey, index, index - 1, rate, triggerUT - currentUT));
            }
            else // DropToRealtime
            {
                droppedCycleByKey[missionKey] = cycle;
                SetRateAction(0);
                ParsekLog.Info("ReaimDescent", string.Format(CultureInfo.InvariantCulture,
                    "descent warp auto-drop: mission={0} cycle={1} phase={2} rate={3:F0}x -> 1x at UT {4:F1} " +
                    "(trigger {5:F1}); descent starting",
                    missionKey, cycle, phase, rate, currentUT, triggerUT));
            }
        }

        // --- Unity wiring (shared by the two concrete scene addons) ---
        internal static void Wire()
        {
            WarpRateProvider = () => TimeWarp.CurrentRate;
            RateIndexProvider = () => TimeWarp.CurrentRateIndex;
            DeltaTimeProvider = () => Time.unscaledDeltaTime;
            FrameProvider = () => Time.frameCount;
            SetRateAction = idx => { if (TimeWarp.fetch != null) TimeWarp.SetRate(idx, true); };
            EnabledProvider = () =>
                ParsekSettings.Current == null || ParsekSettings.Current.autoDropWarpForDescent;
            ResetForSceneChange();
        }

        internal static void Unwire()
        {
            WarpRateProvider = null;
            RateIndexProvider = null;
            DeltaTimeProvider = null;
            FrameProvider = null;
            SetRateAction = null;
            EnabledProvider = null;
        }
    }

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal sealed class DescentWarpControlFlightAddon : MonoBehaviour
    {
        private void Awake()
        {
            DescentWarpControl.Wire();
            ParsekLog.Info("ReaimDescent", "DescentWarpControl wired (Flight)");
        }
        private void OnDestroy() => DescentWarpControl.Unwire();
    }

    [KSPAddon(KSPAddon.Startup.TrackingStation, false)]
    internal sealed class DescentWarpControlTrackingAddon : MonoBehaviour
    {
        private void Awake()
        {
            DescentWarpControl.Wire();
            ParsekLog.Info("ReaimDescent", "DescentWarpControl wired (TrackingStation)");
        }
        private void OnDestroy() => DescentWarpControl.Unwire();
    }
}
