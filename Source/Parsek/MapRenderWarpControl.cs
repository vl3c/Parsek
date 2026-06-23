using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // ============================================================================================================
    // TEMPORARY DEBUG AID — MUST BE REMOVED once the map-render moments it watches are fully debugged. It BREAKS
    // GAMEPLAY when enabled: it forcibly decelerates the player's time-warp as a REGISTERED render moment nears, so
    // the moment (a descent, a loiter->descent handoff, a specific ghost's render at a specific UT, an SOI crossing)
    // is observable/capturable instead of being warped clean over. It is OFF by every normal install: the master
    // code flag DebugWarpEnabled defaults false AND it is also gated on the map-render tracer (MapRenderTrace), so a
    // shipped game never slows. An AI agent debugging a moment REGISTERS it in code (RegisterWatchWindow) and flips
    // DebugWarpEnabled to true; the warp then decelerates into the window via a distance-proportional cap that a
    // single laggy frame cannot overshoot. Caps DOWN only — the player may always warp slower and re-warp freely
    // once past the window. NOT a shipping feature — do NOT keep a CHANGELOG entry for it.
    //
    // REMOVAL RECIPE (after the rendering is debugged):
    //   1. Delete this file (Source/Parsek/MapRenderWarpControl.cs) + its tests
    //      (Source/Parsek.Tests/MapRenderWarpControlTests.cs).
    //   2. Delete the RegisterWatchWindow call in GhostPlaybackLogic.ResolveTrackingStationSampleUT (the descent
    //      caller) and, if no other caller remains, the DescentTrigger.DescentWindowEndLiveUT helper + its
    //      regression test in DescentTriggerTests.cs.
    //   3. Grep for any other RegisterWatchWindow / DebugWarpEnabled references and remove them.
    // ============================================================================================================

    /// <summary>
    /// GENERAL, reusable DEBUG control that decelerates KSP time-warp BEFORE a registered map-render moment so the
    /// moment is reachable/observable instead of warped over. Generalizes the old descent-only warp cap: any debug
    /// target is expressed as a <see cref="WatchWindow"/> (a UT interval) and registered by label; the per-frame
    /// <see cref="Tick"/> caps warp into whichever window is active.
    ///
    /// <para>WHY A WINDOW (UT interval), NOT A PREDICATE: the no-overshoot cap is DISTANCE-based — it tightens as
    /// <c>currentUT</c> approaches <c>triggerUT</c> so even a worst-case (laggy) frame cannot leap past the moment.
    /// A bare "fire when situation X" predicate gives no distance to a future instant, so it cannot drive that cap.
    /// Every debug target here is UT-expressible (a descent window, an SOI-crossing instant, a ghost's render UT), so
    /// the first cut takes only windows. A future <c>RegisterWatchPredicate</c> sugar would resolve a predicate to a
    /// concrete UT window and feed the same machinery; it is intentionally left out of this cut.</para>
    ///
    /// <para>WHY A PROPORTIONAL CAP, NOT A STEP-DOWN RAMP: at 1,000,000x one frame jumps ~20,000 s and a single
    /// lag-spike frame (dt 0.4 s seen in-game = ~400,000 s) can leap over an entire sub-frame window between two
    /// ticks, so a ~1-frame-wide deceleration zone is simply straddled. Instead the cap is PROPORTIONAL to the
    /// distance to the moment (<c>cap ≈ distance / WorstFrameSeconds</c>): far away the cap is huge (no effect), and
    /// as the moment nears the cap tightens so that even a worst-case frame advances less than the remaining gap and
    /// therefore cannot overshoot. Inside the window the cap is <c>span / WatchSeconds</c> so the moment plays over a
    /// watchable span and a frame still cannot skip it. It is a MAX only.</para>
    ///
    /// <para>Split for testability: the per-frame cap (<see cref="ComputeMaxWarpRate"/>), the rate-level pick
    /// (<see cref="SelectRateIndexForCap"/>), and the active-window selection (<see cref="SelectActiveWindow"/>) are
    /// pure and Unity-free; the Unity calls (read the rate / index / rate-level table, set the rate) are injected via
    /// the seam delegates, wired only inside FLIGHT / TRACKING-STATION by the concrete addons below and left null in
    /// xUnit — so <see cref="Tick"/> is a no-op in tests unless a fake seam is wired.</para>
    /// </summary>
    internal static class MapRenderWarpControl
    {
        // A registered render moment the player's warp should be decelerated into so it is observable.
        // windowEndUT == triggerUT pins 1x at the INSTANT (e.g. an SOI crossing): the approaching cap drives warp to
        // 1x right up to triggerUT (and the no-overshoot invariant keeps a frame from leaping past it), and from the
        // instant on there is no cap. windowEndUT = triggerUT + N gives N observable seconds inside the window.
        internal struct WatchWindow
        {
            public double triggerUT;    // LIVE UT when the moment begins (deceleration target)
            public double windowEndUT;  // LIVE UT when the moment ends; == triggerUT for an instant pin
            public string label;        // stable, caller-owned key (the upsert key + the log tag)
        }

        // The moment should play over at most ~this many real seconds inside its window (a MAX cap; the player may
        // still go slower). cap_in_window = span / WatchSeconds.
        internal const double WatchSeconds = 20.0;
        // Assume a frame can take up to ~this many real seconds (a lag-spike margin: the worst seen was 0.4 s, so
        // 2.0 keeps ~5x headroom). The approaching cap = distance / WorstFrameSeconds, so a frame at the cap advances
        // at most cap * WorstFrameSeconds = distance — it can never leap PAST the moment, even on a hitch. This value
        // ALSO sets how early/aggressive the run-in is: the cap starts biting at distance < currentRate *
        // WorstFrameSeconds and decays as distance / WorstFrameSeconds, so HALVING it starts the slow-down half as
        // far out and runs in ~2x faster.
        internal const double WorstFrameSeconds = 2.0;

        // ---- Enable gate (CODE flags, never a settings-UI param) ----

        /// <summary>
        /// Master code flag. Default FALSE so normal gameplay is never slowed. An AI agent debugging a render moment
        /// flips this to true (and registers a window) to make the moment reachable. Tied to the map-render tracer in
        /// <see cref="IsActive"/> so deceleration needs BOTH this and the tracer — enabling the tracer alone never
        /// slows warp.
        /// </summary>
        internal static bool DebugWarpEnabled = false;

        /// <summary>xUnit-only override of the tracer half of the gate so a fake-seam Tick test does not need a live
        /// <c>ParsekSettings</c>. Reset to false in test teardown.</summary>
        internal static bool ForceEnabledForTesting;

        /// <summary>
        /// The control acts this frame only when BOTH the master flag is on AND the map-render tracer is enabled
        /// (or forced for tests). Both default off, so a shipped game is never decelerated.
        /// </summary>
        internal static bool IsActive =>
            (MapRenderTrace.IsEnabled || ForceEnabledForTesting) && DebugWarpEnabled;

        // ---- Registry (label-keyed; the descent caller re-registers every frame with a stable label) ----

        private static readonly List<WatchWindow> windows = new List<WatchWindow>();

        /// <summary>
        /// Register (or update) the watch window the warp should decelerate into, idempotently keyed by
        /// <paramref name="label"/>: re-registering the same label replaces its window (so a per-frame caller can call
        /// this every frame and a cycle that advances its trigger just overwrites the prior window). See
        /// <see cref="WatchWindow"/> for the zero-width (instant-pin) semantics. Maintaining the registry is cheap and
        /// unconditional; the warp is only ever changed inside <see cref="Tick"/> behind <see cref="IsActive"/>.
        /// </summary>
        internal static void RegisterWatchWindow(double triggerUT, double windowEndUT, string label)
        {
            if (string.IsNullOrEmpty(label)) return;
            for (int i = 0; i < windows.Count; i++)
            {
                if (windows[i].label == label)
                {
                    windows[i] = new WatchWindow { triggerUT = triggerUT, windowEndUT = windowEndUT, label = label };
                    return;
                }
            }
            windows.Add(new WatchWindow { triggerUT = triggerUT, windowEndUT = windowEndUT, label = label });
        }

        /// <summary>Remove the window registered under <paramref name="label"/> (no-op if absent).</summary>
        internal static void Unregister(string label)
        {
            if (string.IsNullOrEmpty(label)) return;
            for (int i = 0; i < windows.Count; i++)
            {
                if (windows[i].label == label)
                {
                    windows.RemoveAt(i);
                    return;
                }
            }
        }

        /// <summary>Drop every registered window. Called from <see cref="Wire"/> on scene ENTRY (each addon's
        /// Awake); a per-frame caller re-registers its window next frame. (Unwire/OnDestroy does not clear, so the
        /// list persists in memory through a scene-transition gap until the next scene wires — harmless, since
        /// past / NaN windows contribute +Infinity to the cap and are skipped by <see cref="SelectActiveWindow"/>.)</summary>
        internal static void Clear()
        {
            windows.Clear();
        }

        // ---- Unity seam (wired by the scene addons; null in xUnit so Tick no-ops without a fake seam) ----
        internal static Func<float> WarpRateProvider;     // TimeWarp.CurrentRate
        internal static Func<int> RateIndexProvider;      // TimeWarp.CurrentRateIndex
        internal static Action<int> SetRateAction;        // TimeWarp.SetRate(index, true)
        internal static Func<float[]> RateLevelsProvider; // TimeWarp.fetch.warpRates (ascending)

        // ---- Pure helpers (Unity-free; unit-tested) ----

        /// <summary>
        /// PURE: the maximum warp rate allowed THIS frame so the window <c>[triggerUT, windowEndUT]</c> cannot be
        /// warped over. All UTs are in the SAME (live) frame.
        /// <list type="bullet">
        /// <item>Past the window (<c>currentUT &gt;= windowEndUT</c>) or NaN timing: <c>+Infinity</c> — no cap.</item>
        /// <item>Inside the window (<c>triggerUT &lt;= currentUT &lt; windowEndUT</c>): <c>span / WatchSeconds</c> — the
        ///   moment plays over ~that many real seconds and a frame cannot skip it.</item>
        /// <item>Approaching (<c>currentUT &lt; triggerUT</c>): <c>distance / WorstFrameSeconds</c> — the cap tightens
        ///   as the moment nears so even a laggy frame lands inside, never past. Far away this is huge (no effect).</item>
        /// </list>
        /// Always at least 1.0 (never below realtime). A MAX only; the player may warp slower.
        /// </summary>
        internal static double ComputeMaxWarpRate(double currentUT, double triggerUT, double windowEndUT)
        {
            if (double.IsNaN(currentUT) || double.IsNaN(triggerUT) || double.IsNaN(windowEndUT))
                return double.PositiveInfinity;
            if (currentUT >= windowEndUT) return double.PositiveInfinity; // past the window: free warp
            if (currentUT >= triggerUT)
            {
                double span = windowEndUT - triggerUT;
                return Math.Max(1.0, span / WatchSeconds); // in the window: watchable cap
            }
            double distance = triggerUT - currentUT;       // approaching: tighten as it nears
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
        /// PURE: pick the one window the warp cap should be LABELLED against. Prefers a window the player is INSIDE
        /// (<c>triggerUT &lt;= currentUT &lt; windowEndUT</c>); else the nearest UPCOMING window
        /// (<c>windowEndUT &gt; currentUT</c>, smallest <c>triggerUT</c>); skips past windows
        /// (<c>windowEndUT &lt;= currentUT</c>) and NaN-timed ones. Returns false (no active window → no cap) when
        /// none qualify. This selects the window for the LOG label; the binding CAP is the most-restrictive
        /// (minimum) <see cref="ComputeMaxWarpRate"/> over ALL windows (computed in <see cref="Tick"/>) — the two
        /// coincide in the common single-window case, and past windows contribute <c>+Infinity</c> to that min so
        /// the cap and this selection stay consistent.
        /// </summary>
        internal static bool SelectActiveWindow(
            IReadOnlyList<WatchWindow> candidates, double currentUT, out WatchWindow chosen)
        {
            chosen = default;
            if (candidates == null || candidates.Count == 0) return false;

            bool haveInside = false, haveUpcoming = false;
            WatchWindow inside = default, upcoming = default;
            for (int i = 0; i < candidates.Count; i++)
            {
                WatchWindow w = candidates[i];
                if (double.IsNaN(w.triggerUT) || double.IsNaN(w.windowEndUT)) continue;
                if (w.windowEndUT <= currentUT) continue; // past: skip
                if (w.triggerUT <= currentUT)
                {
                    // inside (triggerUT <= currentUT < windowEndUT): prefer the earliest-started inside window.
                    if (!haveInside || w.triggerUT < inside.triggerUT) { inside = w; haveInside = true; }
                }
                else
                {
                    // upcoming (currentUT < triggerUT): prefer the nearest (smallest triggerUT).
                    if (!haveUpcoming || w.triggerUT < upcoming.triggerUT) { upcoming = w; haveUpcoming = true; }
                }
            }
            if (haveInside) { chosen = inside; return true; }
            if (haveUpcoming) { chosen = upcoming; return true; }
            return false;
        }

        // ---- Per-frame drive (single owner: the scene addons' Update) ----

        /// <summary>
        /// Per-frame drive: caps the warp DOWN so the active registered window cannot be warped over; never raises the
        /// rate (a MAX) and never acts past the window. Early-returns when <see cref="IsActive"/> is false, the seam
        /// is unwired, or no window is active/upcoming. Driven ONCE per frame by the scene addons (single owner, so no
        /// per-caller dedup is needed). The binding cap is the most-restrictive (minimum) per-window cap.
        /// </summary>
        internal static void Tick(double currentUT)
        {
            if (!IsActive) return;
            if (WarpRateProvider == null || SetRateAction == null) return; // seam unwired (xUnit / non-flight scenes)
            if (windows.Count == 0) return;
            if (!SelectActiveWindow(windows, currentUT, out WatchWindow chosen)) return; // nothing active/upcoming

            int index = RateIndexProvider != null ? RateIndexProvider() : 0;
            float[] levels = RateLevelsProvider != null ? RateLevelsProvider() : null;
            float rate = WarpRateProvider();

            // Multi-window arbitration: the binding cap is the MIN over all windows (most-restrictive). Past windows
            // return +Infinity, so they never tighten the cap.
            double cap = double.PositiveInfinity;
            for (int i = 0; i < windows.Count; i++)
            {
                double c = ComputeMaxWarpRate(currentUT, windows[i].triggerUT, windows[i].windowEndUT);
                if (c < cap) cap = c;
            }

            int targetIdx = double.IsPositiveInfinity(cap) ? index : SelectRateIndexForCap(cap, levels);

            // Always-on (rate-limited) decision trace so a no-op is debuggable from a single log.
            ParsekLog.VerboseRateLimited("MapRenderWarp", "warpcap." + chosen.label,
                string.Format(CultureInfo.InvariantCulture,
                    "warp-cap window={0} cap={1}x targetIdx={2} rate={3:F0}x idx={4} curUT={5:F1} trig={6:F1} ttt={7:F1}",
                    chosen.label,
                    double.IsPositiveInfinity(cap) ? "none" : ((long)cap).ToString(CultureInfo.InvariantCulture),
                    targetIdx, rate, index, currentUT, chosen.triggerUT, chosen.triggerUT - currentUT));

            // Cap DOWN only — never speed the player up, and do nothing past the window / when already slow enough.
            if (double.IsPositiveInfinity(cap) || targetIdx >= index) return;
            SetRateAction(targetIdx);
            ParsekLog.Info("MapRenderWarp", string.Format(CultureInfo.InvariantCulture,
                "debug warp cap: window={0} idx {1} -> {2} (was {3:F0}x; cap {4}x; {5:F0}s to trigger); window {6}",
                chosen.label, index, targetIdx, rate, (long)cap, chosen.triggerUT - currentUT,
                currentUT >= chosen.triggerUT ? "playing" : "approaching"));
        }

        // ---- Unity wiring (shared by the two concrete scene addons) ----
        internal static void Wire()
        {
            WarpRateProvider = () => TimeWarp.CurrentRate;
            RateIndexProvider = () => TimeWarp.CurrentRateIndex;
            SetRateAction = idx => { if (TimeWarp.fetch != null) TimeWarp.SetRate(idx, true); };
            RateLevelsProvider = () => TimeWarp.fetch != null ? TimeWarp.fetch.warpRates : null;
            Clear();
        }

        internal static void Unwire()
        {
            WarpRateProvider = null;
            RateIndexProvider = null;
            SetRateAction = null;
            RateLevelsProvider = null;
        }

        /// <summary>
        /// The single-owner per-frame Unity driver, called from the scene addons' <c>Update</c>. Drives
        /// <see cref="Tick"/> only when the control is armed AND KSP is in rails (HIGH) time-warp:
        /// <list type="bullet">
        /// <item>The <see cref="IsActive"/> gate first, so the per-frame <c>Planetarium</c>/<c>TimeWarp</c> ECalls
        ///   below do NOT run in normal gameplay (the debug flag is off) — they fire only while an agent is actively
        ///   debugging.</item>
        /// <item><c>Planetarium.fetch</c> / <c>TimeWarp.fetch</c> null-guard for the early-scene-load window (the
        ///   codebase guards <c>Planetarium.GetUniversalTime()</c> elsewhere for the same reason).</item>
        /// <item>Rails-warp only: the seam reads the rails <c>warpRates</c> table and <c>SetRate</c>/<c>CurrentRateIndex</c>
        ///   are index-relative to the active warp mode, so capping is only well-defined in HIGH mode. The straddle
        ///   the cap defeats is inherently a high-warp problem (physics/LOW warp tops out ~4x and cannot leap a
        ///   window), so skipping LOW warp loses nothing.</item>
        /// </list>
        /// </summary>
        internal static void TickFromScene()
        {
            if (!IsActive) return;
            if (TimeWarp.fetch == null || Planetarium.fetch == null) return;
            if (TimeWarp.WarpMode != TimeWarp.Modes.HIGH) return;
            Tick(Planetarium.GetUniversalTime());
        }
    }

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal sealed class MapRenderWarpControlFlightAddon : MonoBehaviour
    {
        private void Awake()
        {
            MapRenderWarpControl.Wire();
            ParsekLog.Info("MapRenderWarp", "MapRenderWarpControl wired (Flight)");
        }
        private void Update() => MapRenderWarpControl.TickFromScene();
        private void OnDestroy() => MapRenderWarpControl.Unwire();
    }

    [KSPAddon(KSPAddon.Startup.TrackingStation, false)]
    internal sealed class MapRenderWarpControlTrackingAddon : MonoBehaviour
    {
        private void Awake()
        {
            MapRenderWarpControl.Wire();
            ParsekLog.Info("MapRenderWarp", "MapRenderWarpControl wired (TrackingStation)");
        }
        private void Update() => MapRenderWarpControl.TickFromScene();
        private void OnDestroy() => MapRenderWarpControl.Unwire();
    }
}
