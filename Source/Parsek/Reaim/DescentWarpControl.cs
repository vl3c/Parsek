using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.Reaim
{
    // ============================================================================================================
    // Auto-slow time-warp for re-aim looped descents (a real, user-gated feature — NOT a scaffold). A re-aim
    // looped landing's recorded deorbit->reentry->landing clip renders for only its recorded duration once per
    // multi-hundred-day loop (~0.05% of the loop), so at high warp a single frame leaps clean over it. This
    // decelerates warp into the descent window so it is watchable. Gated by ParsekSettings.autoDropWarpForDescent
    // (default on); fully no-op when the setting is off, in xUnit / non-flight scenes (the Unity seam is unwired),
    // and for any vessel that is not a re-aim looped descent (the resolver only calls NotifyDescentState for a
    // descent-trigger member). Caps DOWN only — the player always keeps slower control.
    // ============================================================================================================

    /// <summary>
    /// Caps time-warp so a re-aim looped descent (re-entry/landing or station dock) is watchable instead of being
    /// warped straight past — the descent renders for only its recorded clip (~hours) once per multi-hundred-day
    /// loop (~0.05% of the loop), so at high warp a single frame leaps clean over its whole window.
    ///
    /// <para>WHY A PROPORTIONAL CAP, NOT A STEP-DOWN RAMP: the earlier "step the warp down one level when the next
    /// step would consume &gt; half the time-to-trigger" ramp could NOT catch the window. At 1,000,000x one frame
    /// jumps ~20,000 s (≈ the whole 20,513 s window) and a single lag-spike frame (dt 0.4 s seen in-game = ~400,000 s)
    /// leaps over the entire loiter+window between two consecutive resolver calls, so the ramp's ~1-frame-wide
    /// deceleration zone is simply straddled (2026-06-20 logs: descent SKIPPED every cycle, the trace first sees the
    /// cycle already at Done). Instead this CAPS the max rate PROPORTIONAL to the distance to the descent
    /// (<c>cap ≈ distance / WorstFrameSeconds</c>): far away the cap is huge (no effect), and as the descent nears the
    /// cap tightens so that even a worst-case (laggy) frame advances less than the remaining gap and therefore cannot
    /// overshoot. Inside the window the cap is <c>clip / DescentWatchSeconds</c> so the clip plays over a watchable
    /// span and a frame still cannot skip it. It is a MAX only — the player may always warp SLOWER (e.g. 1x to study
    /// the landing) and may re-warp freely once past the descent.</para>
    ///
    /// <para>Split for testability: the per-frame cap (<see cref="ComputeMaxWarpRate"/>) and the rate-level pick
    /// (<see cref="SelectRateIndexForCap"/>) are pure and Unity-free; the Unity calls (read the rate / index / frame
    /// count / rate-level table, set the rate, read the setting) are injected via the seam delegates, wired only
    /// inside FLIGHT / TRACKING-STATION by the concrete addons below and left null in xUnit — so the resolver's
    /// per-frame <see cref="NotifyDescentState"/> call is a no-op in tests.</para>
    /// </summary>
    internal static class DescentWarpControl
    {
        // The descent clip should play over at most ~this many real seconds inside the window (a MAX cap; the player
        // may still go slower). cap_in_window = clip / DescentWatchSeconds.
        internal const double DescentWatchSeconds = 20.0;
        // Assume a frame can take up to ~this many real seconds (a lag-spike margin: the worst seen was 0.4 s, so
        // 2.0 keeps ~5x headroom). The approaching cap = distance / WorstFrameSeconds, so a frame at the cap advances
        // at most cap * WorstFrameSeconds = distance — it can never leap PAST the descent, even on a hitch. This
        // value ALSO sets how early/aggressive the run-in is: the cap starts biting at distance < currentRate *
        // WorstFrameSeconds and decays as distance / WorstFrameSeconds, so HALVING it (4 -> 2) starts the slow-down
        // half as far out and runs in ~2x faster (the playtested "it slows too soon / takes too long" trim).
        internal const double WorstFrameSeconds = 2.0;

        // --- Unity seam (wired by the scene addons; null in xUnit so NotifyDescentState no-ops) ---
        internal static Func<float> WarpRateProvider;    // TimeWarp.CurrentRate
        internal static Func<int> RateIndexProvider;     // TimeWarp.CurrentRateIndex
        internal static Func<int> FrameProvider;         // Time.frameCount
        internal static Action<int> SetRateAction;       // TimeWarp.SetRate(index, true)
        internal static Func<bool> EnabledProvider;      // the user setting; null => treated as enabled
        internal static Func<float[]> RateLevelsProvider; // TimeWarp.fetch.warpRates (ascending)

        // The resolver calls NotifyDescentState once per descent MEMBER per frame (4+ calls/frame for one mission),
        // all with the same shared state; act at most once per rendered frame.
        private static int lastActionFrame = -1;

        internal static void ResetForSceneChange()
        {
            lastActionFrame = -1;
        }

        /// <summary>
        /// The descent render window's END in the LIVE time frame: the (live) <paramref name="triggerUT"/> plus the
        /// recorded clip duration (<paramref name="descentEndUT"/> - <paramref name="recordedDeorbitUT"/>, both
        /// RECORDED-frame). The window is <c>[triggerUT, this]</c>. NaN if any input is NaN.
        ///
        /// <para>CRITICAL FRAME NOTE: <c>RecordedDeorbitUT</c> / <c>DescentEndUT</c> are RECORDED UT (~2.5e9 in a
        /// mid-game save) while the live loop clock / <c>triggerUT</c> are LIVE UT (~3.9e9). The cap MUST compare the
        /// live <c>currentUT</c> against THIS live window end, never against the raw recorded <c>descentEndUT</c>: a
        /// recorded UT is far smaller than any live UT, so <c>currentUT &gt;= descentEndUT</c> would be true on EVERY
        /// frame and silently disable the entire control (the 2026-06-20 dead-warp-control bug — descent warped over
        /// all session).</para>
        /// </summary>
        internal static double DescentWindowEndLiveUT(
            double triggerUT, double recordedDeorbitUT, double descentEndUT)
        {
            if (double.IsNaN(triggerUT) || double.IsNaN(recordedDeorbitUT) || double.IsNaN(descentEndUT))
                return double.NaN;
            return triggerUT + (descentEndUT - recordedDeorbitUT);
        }

        /// <summary>
        /// PURE: the maximum warp rate allowed THIS frame so the descent window cannot be warped over.
        /// <paramref name="descentWindowEndLiveUT"/> is the LIVE-frame window end (<see cref="DescentWindowEndLiveUT"/>).
        /// <list type="bullet">
        /// <item>Past the descent (<c>currentUT &gt;= descentWindowEndLiveUT</c>) or NaN timing: <c>+Infinity</c> — no
        ///   cap, the player warps freely.</item>
        /// <item>Inside the window (<c>triggerUT &lt;= currentUT &lt; end</c>): <c>clip / DescentWatchSeconds</c> — the
        ///   clip plays over ~that many real seconds and a frame cannot skip it.</item>
        /// <item>Approaching (<c>currentUT &lt; triggerUT</c>): <c>distance / WorstFrameSeconds</c> — the cap tightens
        ///   as the descent nears so even a laggy frame lands inside, never past. Far away this is huge (no effect).</item>
        /// </list>
        /// Always at least 1.0 (never below realtime). A MAX only; the player may warp slower.
        /// </summary>
        internal static double ComputeMaxWarpRate(
            double currentUT, double triggerUT, double descentWindowEndLiveUT)
        {
            if (double.IsNaN(currentUT) || double.IsNaN(triggerUT) || double.IsNaN(descentWindowEndLiveUT))
                return double.PositiveInfinity;
            if (currentUT >= descentWindowEndLiveUT) return double.PositiveInfinity; // past the descent: free warp
            if (currentUT >= triggerUT)
            {
                double clip = descentWindowEndLiveUT - triggerUT;
                return Math.Max(1.0, clip / DescentWatchSeconds); // in the window: watchable cap
            }
            double distance = triggerUT - currentUT;              // approaching: tighten as it nears
            return Math.Max(1.0, distance / WorstFrameSeconds);
        }

        /// <summary>
        /// PURE: the highest warp-rate INDEX whose level is &lt;= <paramref name="maxRate"/> (at least 0).
        /// <paramref name="rateLevels"/> is the ascending warp-rate table (<c>TimeWarp.warpRates</c>). Returns the top
        /// index for <c>+Infinity</c> / NaN / empty (i.e. "no cap → leave the rate alone"); always returns &gt;= 0 so
        /// index 0 (1x) is the floor.
        /// </summary>
        internal static int SelectRateIndexForCap(double maxRate, IReadOnlyList<float> rateLevels)
        {
            if (rateLevels == null || rateLevels.Count == 0) return 0;
            if (double.IsNaN(maxRate) || double.IsPositiveInfinity(maxRate)) return rateLevels.Count - 1;
            int idx = 0;
            for (int i = 0; i < rateLevels.Count; i++)
            {
                if (rateLevels[i] <= maxRate) idx = i;
                else break;
            }
            return idx;
        }

        /// <summary>
        /// Called from the descent resolver each frame for a descent member (the resolver is reached in BOTH the
        /// tracking station and FLIGHT — via the polyline Driver's per-frame walk — so one call site covers both
        /// scenes). Caps the warp DOWN to the proportional limit; never raises it (a MAX), so the player keeps full
        /// slower control and can re-warp freely once past the descent. No-op when the Unity seam is unwired (xUnit /
        /// non-flight scenes) or the setting is off; acts at most once per frame.
        /// </summary>
        internal static void NotifyDescentState(
            string missionKey, long cycle, DescentTrigger.DescentHeadPhase phase,
            double currentUT, double triggerUT, double recordedDeorbitUT, double descentEndUT)
        {
            if (WarpRateProvider == null || SetRateAction == null) return; // unwired (tests / non-flight scenes)
            if (EnabledProvider != null && !EnabledProvider()) return;     // user disabled

            int frame = FrameProvider != null ? FrameProvider() : 0;
            if (frame != 0 && frame == lastActionFrame) return; // already acted this frame (per-member dedup)

            float rate = WarpRateProvider();
            int index = RateIndexProvider != null ? RateIndexProvider() : 0;
            float[] levels = RateLevelsProvider != null ? RateLevelsProvider() : null;

            // CRITICAL: descentEndUT/recordedDeorbitUT are RECORDED-frame; triggerUT/currentUT are LIVE. Convert the
            // window end into the LIVE frame, or the recorded end (far below any live UT) disables the cap every frame
            // (the 2026-06-20 dead-warp-control bug).
            double descentWindowEndLiveUT = DescentWindowEndLiveUT(triggerUT, recordedDeorbitUT, descentEndUT);
            double maxRate = ComputeMaxWarpRate(currentUT, triggerUT, descentWindowEndLiveUT);
            int targetIdx = double.IsPositiveInfinity(maxRate) ? index : SelectRateIndexForCap(maxRate, levels);

            // Always-on (rate-limited) decision trace so a no-op is debuggable from a single log.
            ParsekLog.VerboseRateLimited("ReaimDescent", "warpcap." + missionKey + "." + phase,
                string.Format(CultureInfo.InvariantCulture,
                    "warp-cap mission={0} cycle={1} phase={2} rate={3:F0}x idx={4} curUT={5:F1} trig={6:F1} " +
                    "ttt={7:F1} cap={8} targetIdx={9}",
                    missionKey, cycle, phase, rate, index, currentUT, triggerUT, triggerUT - currentUT,
                    double.IsPositiveInfinity(maxRate) ? "none" : ((long)maxRate).ToString(CultureInfo.InvariantCulture) + "x",
                    targetIdx));

            // Cap DOWN only — never speed the player up, and do nothing past the descent / when already slow enough.
            if (double.IsPositiveInfinity(maxRate) || targetIdx >= index) return;
            lastActionFrame = frame;
            SetRateAction(targetIdx);
            ParsekLog.Info("ReaimDescent", string.Format(CultureInfo.InvariantCulture,
                "descent warp cap: mission={0} cycle={1} phase={2} idx {3} -> {4} (was {5:F0}x; cap {6}x; {7:F0}s to trigger); descent {8}",
                missionKey, cycle, phase, index, targetIdx, rate, (long)maxRate, triggerUT - currentUT,
                currentUT >= triggerUT ? "playing" : "approaching"));
        }

        // --- Unity wiring (shared by the two concrete scene addons) ---
        internal static void Wire()
        {
            WarpRateProvider = () => TimeWarp.CurrentRate;
            RateIndexProvider = () => TimeWarp.CurrentRateIndex;
            FrameProvider = () => Time.frameCount;
            SetRateAction = idx => { if (TimeWarp.fetch != null) TimeWarp.SetRate(idx, true); };
            EnabledProvider = () =>
                ParsekSettings.Current == null || ParsekSettings.Current.autoDropWarpForDescent;
            RateLevelsProvider = () => TimeWarp.fetch != null ? TimeWarp.fetch.warpRates : null;
            ResetForSceneChange();
        }

        internal static void Unwire()
        {
            WarpRateProvider = null;
            RateIndexProvider = null;
            FrameProvider = null;
            SetRateAction = null;
            EnabledProvider = null;
            RateLevelsProvider = null;
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
