using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// One-shot note that stock just built a game from a save file: F9, the Space Center pause
    /// menu Load, the main menu Load or Resume (decompiled KSP 1.12.5: every one fires
    /// <c>GameEvents.onGameStatePostLoad</c> after <c>GamePersistence.LoadGameCfg</c> returned a
    /// compatible game and before the scene request). Stock also fires it inside
    /// <c>FlightDriver.RevertToLaunch</c> / <c>RevertToPrelaunch</c>; a revert wins at
    /// classification because <see cref="RevertDetector"/> is armed by the same call.
    /// <c>GamePersistence.LoadGame</c> never fires it, so Parsek's own loads (rewind, Re-Fly
    /// start, Discard Re-fly, the test-batch restores) never arm it.
    ///
    /// <para>Consumed by the next <see cref="ParsekScenario.OnLoad"/>'s early classification
    /// whatever that load classifies as, cleared by the main-menu transition, and ignored with a
    /// Warn when older than <see cref="StaleAfterRealtimeSeconds"/> at consumption (a load stock
    /// announced but never delivered). Design: docs/dev/design-load-restores-the-save.md 4.1.</para>
    /// </summary>
    internal static class SaveFileLoadIntent
    {
        /// <summary>Real seconds after which an armed intent no longer names the next load.</summary>
        internal const double StaleAfterRealtimeSeconds = 120.0;

        private static bool armed;
        private static double armedRealtime = double.NaN;
        private static int armedFrame = -1;
        private static double armedNodeUT = double.NaN;
        private static string armedScene;

        internal static bool IsArmed => armed;

        internal static double ArmedNodeUT => armedNodeUT;

        internal static void Arm(double nodeUT, string scene, double realtime, int frame)
        {
            if (armed)
            {
                ParsekLog.Warn(LoadReconcilePolicy.LogTag,
                    "Save-file load intent re-armed before the previous one was consumed: previous "
                    + $"nodeUT={FormatUT(armedNodeUT)} scene={armedScene ?? "<none>"}");
            }
            armed = true;
            armedRealtime = realtime;
            armedFrame = frame;
            armedNodeUT = nodeUT;
            armedScene = scene;
            ParsekLog.Info(LoadReconcilePolicy.LogTag,
                $"Save-file load intent armed: nodeUT={FormatUT(nodeUT)} scene={scene ?? "<none>"}");
        }

        /// <summary>
        /// Reads and clears the intent. True only when one was armed and it is not stale; a stale
        /// intent is dropped with a Warn. <paramref name="ageFrames"/> is the frame distance from
        /// the arm (-1 when nothing was armed).
        /// </summary>
        internal static bool TryConsume(double nowRealtime, int nowFrame, string site,
            out double nodeUT, out int ageFrames)
        {
            nodeUT = armedNodeUT;
            ageFrames = -1;
            if (!armed)
                return false;

            double ageSeconds = nowRealtime - armedRealtime;
            ageFrames = armedFrame >= 0 && nowFrame >= armedFrame ? nowFrame - armedFrame : -1;
            string scene = armedScene;
            armed = false;
            armedRealtime = double.NaN;
            armedFrame = -1;
            armedNodeUT = double.NaN;
            armedScene = null;

            if (IsStale(ageSeconds))
            {
                ParsekLog.Warn(LoadReconcilePolicy.LogTag,
                    $"Save-file load intent dropped reason=stale at {site ?? "<no-site>"}: "
                    + $"ageSeconds={ageSeconds.ToString("F1", CultureInfo.InvariantCulture)} "
                    + $"nodeUT={FormatUT(nodeUT)} scene={scene ?? "<none>"}");
                return false;
            }

            ParsekLog.Verbose(LoadReconcilePolicy.LogTag,
                $"Save-file load intent consumed at {site ?? "<no-site>"}: nodeUT={FormatUT(nodeUT)} "
                + $"ageFrames={ageFrames.ToString(CultureInfo.InvariantCulture)}");
            return true;
        }

        /// <summary>Pure: an age that is not a finite value inside the window is stale.</summary>
        internal static bool IsStale(double ageSeconds)
        {
            if (double.IsNaN(ageSeconds) || double.IsInfinity(ageSeconds))
                return true;
            return ageSeconds < 0.0 || ageSeconds > StaleAfterRealtimeSeconds;
        }

        internal static void Clear(string reason)
        {
            if (!armed)
                return;
            ParsekLog.Verbose(LoadReconcilePolicy.LogTag,
                $"Save-file load intent dropped reason={reason ?? "<none>"} nodeUT={FormatUT(armedNodeUT)}");
            armed = false;
            armedRealtime = double.NaN;
            armedFrame = -1;
            armedNodeUT = double.NaN;
            armedScene = null;
        }

        /// <summary>
        /// Pure: the UT of the loaded game's flight state, from the node stock passes to
        /// <c>onGameStatePostLoad</c> (the GAME node, or a root holding it). NaN when absent.
        /// </summary>
        internal static double ReadNodeUT(ConfigNode gameNode)
        {
            if (gameNode == null)
                return double.NaN;
            ConfigNode flightState = gameNode.GetNode("FLIGHTSTATE")
                ?? gameNode.GetNode("GAME")?.GetNode("FLIGHTSTATE");
            string raw = flightState?.GetValue("UT");
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double ut)
                ? ut
                : double.NaN;
        }

        private static string FormatUT(double ut)
        {
            return double.IsNaN(ut) ? "NaN" : ut.ToString("F2", CultureInfo.InvariantCulture);
        }

        internal static void ResetForTesting()
        {
            armed = false;
            armedRealtime = double.NaN;
            armedFrame = -1;
            armedNodeUT = double.NaN;
            armedScene = null;
        }
    }

    /// <summary>
    /// Subscribes <see cref="SaveFileLoadIntent"/> to <c>GameEvents.onGameStatePostLoad</c> for the
    /// whole process. It lives on its own addon because the main-menu Load fires the event while
    /// no Parsek scenario exists, and the handler is an instance method because KSP's
    /// <c>EvtDelegate</c> reads <c>Target.GetType()</c> (a static handler NREs inside
    /// <c>GameEvents.Add</c>).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class SaveFileLoadIntentAddon : MonoBehaviour
    {
        private static bool subscribed;

        public void Awake()
        {
            DontDestroyOnLoad(gameObject);
            if (subscribed)
                return;
            subscribed = true;
            GameEvents.onGameStatePostLoad.Add(OnGameStatePostLoad);
            ParsekLog.Info(LoadReconcilePolicy.LogTag, "Subscribed to GameEvents.onGameStatePostLoad");
        }

        private void OnGameStatePostLoad(ConfigNode gameNode)
        {
            SaveFileLoadIntent.Arm(
                SaveFileLoadIntent.ReadNodeUT(gameNode),
                HighLogic.LoadedScene.ToString(),
                Time.realtimeSinceStartup,
                Time.frameCount);
        }
    }
}
