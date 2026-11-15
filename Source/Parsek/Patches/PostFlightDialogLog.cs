using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Observation-only logging of the two stock post-flight screens that otherwise leave no
    /// reliable trace in KSP.log: the flight results dialog (<c>KSP.UI.Dialogs.FlightResultsDialog</c>,
    /// the crash screen "Outcome: Catastrophic Failure!" and the F3 flight status screen) and
    /// the KSC recovery summary (<c>KSP.UI.Screens.MissionRecoveryDialog</c>, "Mission Summary
    /// for vessel"). Each display and each dismissal writes one Info line under the
    /// <see cref="Tag"/> subsystem; the dismissal carries the time on screen in wall seconds
    /// so a log scan can measure how long a harness run sat behind either screen.
    ///
    /// <para>Pure state + formatting: no Unity or KSP calls, so every caller passes the wall
    /// clock and the scene in. The live feeds are
    /// <see cref="Patches.FlightResultsDialogLogPatches"/> (no stock event exists for that
    /// dialog) and <see cref="PostFlightDialogLogHost"/> (stock
    /// <c>GameEvents.onGUIRecoveryDialogSpawn</c> / <c>Despawn</c>). Touches no game state,
    /// so it is deliberately not behind the game-mode gate.</para>
    /// </summary>
    internal static class PostFlightDialogLog
    {
        internal const string Tag = "PostFlightDialog";
        internal const string FlightResultsName = "FlightResultsDialog";
        internal const string RecoveryName = "MissionRecoveryDialog";

        // Stock MissionRecoveryDialog.CreateFullDialog / CreateScienceDialog name the
        // dialog's GameObject pv.GetDisplayName() + this suffix (decompiled, KSP 1.12.5).
        internal const string RecoveryHandlerNameSuffix = " Recovery Dialog Handler";

        private sealed class RecoveryEntry
        {
            internal double ShownWall;
            internal string Scene;
            internal string Vessel;
            internal bool ShownLogged;
        }

        private static bool flightResultsShown;
        private static double flightResultsShownWall;
        private static string flightResultsScene;
        private static string flightResultsVessel;

        private static readonly Dictionary<int, RecoveryEntry> recoveryEntries =
            new Dictionary<int, RecoveryEntry>();
        private static int pendingRecoveryShownCount;

        internal static bool IsFlightResultsShown => flightResultsShown;
        internal static int PendingRecoveryShownCount => pendingRecoveryShownCount;

        internal static void ResetForTesting()
        {
            flightResultsShown = false;
            flightResultsShownWall = 0.0;
            flightResultsScene = null;
            flightResultsVessel = null;
            recoveryEntries.Clear();
            pendingRecoveryShownCount = 0;
        }

        // ---------- formatting ----------

        /// <summary>Wall seconds, two decimals, culture-invariant; negative clamps to 0.</summary>
        internal static string FormatWallSeconds(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0.0)
                seconds = 0.0;
            return seconds.ToString("F2", CultureInfo.InvariantCulture);
        }

        /// <summary>A free-text value safe inside one double-quoted log field.</summary>
        internal static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "\"?\"";
            string clean = value.Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'').Trim();
            return "\"" + (clean.Length == 0 ? "?" : clean) + "\"";
        }

        internal static string FormatBool(bool value) => value ? "true" : "false";

        internal static string FormatFlightResultsShown(
            string scene, string vessel, bool paused, bool exitControls, string outcome)
        {
            return FlightResultsName + " shown: scene=" + (scene ?? "?")
                + " vessel=" + Quote(vessel)
                + " paused=" + FormatBool(paused)
                + " exitControls=" + FormatBool(exitControls)
                + " outcome=" + Quote(outcome);
        }

        internal static string FormatFlightResultsDismissed(
            string scene, string vessel, string via, double onScreenWallSeconds, bool paused)
        {
            return FlightResultsName + " dismissed: scene=" + (scene ?? "?")
                + " vessel=" + Quote(vessel)
                + " via=" + (via ?? "?")
                + " onScreenWallSeconds=" + FormatWallSeconds(onScreenWallSeconds)
                + " paused=" + FormatBool(paused);
        }

        internal static string FormatRecoveryShown(string scene, string vessel)
        {
            return RecoveryName + " shown: scene=" + (scene ?? "?") + " vessel=" + Quote(vessel);
        }

        /// <summary><paramref name="onScreenWallSeconds"/> null means the display was never
        /// observed (the dialog predates the subscription), printed as <c>unknown</c>.</summary>
        internal static string FormatRecoveryDismissed(
            string scene, string vessel, double? onScreenWallSeconds)
        {
            return RecoveryName + " dismissed: scene=" + (scene ?? "?")
                + " vessel=" + Quote(vessel)
                + " onScreenWallSeconds="
                + (onScreenWallSeconds.HasValue ? FormatWallSeconds(onScreenWallSeconds.Value) : "unknown");
        }

        /// <summary>The vessel name from a recovery dialog's GameObject name, or null when the
        /// name is not the stock "&lt;vessel&gt; Recovery Dialog Handler" shape (it is still
        /// "MissionRecoveryDialog(Clone)" while the spawn event fires inside Awake).</summary>
        internal static string ParseVesselFromRecoveryHandlerName(string gameObjectName)
        {
            if (string.IsNullOrEmpty(gameObjectName)
                || !gameObjectName.EndsWith(RecoveryHandlerNameSuffix, System.StringComparison.Ordinal))
                return null;
            string vessel = gameObjectName.Substring(
                0, gameObjectName.Length - RecoveryHandlerNameSuffix.Length);
            return vessel.Length == 0 ? null : vessel;
        }

        // ---------- flight results dialog ----------

        internal static void NoteFlightResultsShown(
            double nowWall, string scene, string vessel, bool paused, bool exitControls, string outcome)
        {
            if (flightResultsShown)
            {
                // Display() on an already-shown dialog only refreshes its text; keep the
                // original start so the dismissal measures the whole time on screen.
                ParsekLog.Verbose(Tag, FlightResultsName + " re-displayed while shown: outcome="
                    + Quote(outcome) + " sinceShownWallSeconds="
                    + FormatWallSeconds(nowWall - flightResultsShownWall));
                return;
            }

            flightResultsShown = true;
            flightResultsShownWall = nowWall;
            flightResultsScene = scene;
            flightResultsVessel = vessel;
            ParsekLog.Info(Tag, FormatFlightResultsShown(scene, vessel, paused, exitControls, outcome));
        }

        /// <summary>Closes the tracked display. <paramref name="via"/> is <c>Close</c> for stock
        /// <c>FlightResultsDialog.Close()</c> and <c>Destroyed</c> when the dialog object went
        /// away without it (scene change). A call with nothing shown is a no-op, which is what
        /// makes the OnDestroy that follows every Close() silent.</summary>
        internal static void NoteFlightResultsDismissed(double nowWall, string via, bool paused)
        {
            if (!flightResultsShown)
                return;

            flightResultsShown = false;
            ParsekLog.Info(Tag, FormatFlightResultsDismissed(
                flightResultsScene, flightResultsVessel, via,
                nowWall - flightResultsShownWall, paused));
            flightResultsScene = null;
            flightResultsVessel = null;
        }

        // ---------- mission recovery dialog ----------

        /// <summary>Stock fires the spawn event inside the dialog's Awake, before the vessel
        /// name is assigned, so the shown line waits for <see cref="NoteRecoveryVessel"/>.</summary>
        internal static void NoteRecoverySpawned(int dialogId, double nowWall, string scene)
        {
            if (recoveryEntries.TryGetValue(dialogId, out RecoveryEntry existing))
            {
                if (!existing.ShownLogged)
                    pendingRecoveryShownCount--;
            }

            recoveryEntries[dialogId] = new RecoveryEntry
            {
                ShownWall = nowWall,
                Scene = scene,
            };
            pendingRecoveryShownCount++;
        }

        /// <summary>Writes the pending shown line once the vessel is known. Unknown ids and
        /// already-logged entries are no-ops; a null vessel still logs (as <c>"?"</c>).</summary>
        internal static void NoteRecoveryVessel(int dialogId, string vessel)
        {
            if (!recoveryEntries.TryGetValue(dialogId, out RecoveryEntry entry) || entry.ShownLogged)
                return;

            entry.Vessel = vessel;
            entry.ShownLogged = true;
            pendingRecoveryShownCount--;
            ParsekLog.Info(Tag, FormatRecoveryShown(entry.Scene, vessel));
        }

        internal static void CollectPendingRecoveryIds(List<int> into)
        {
            into.Clear();
            foreach (var kv in recoveryEntries)
            {
                if (!kv.Value.ShownLogged)
                    into.Add(kv.Key);
            }
        }

        internal static void NoteRecoveryDespawned(
            int dialogId, double nowWall, string scene, string fallbackVessel)
        {
            if (!recoveryEntries.TryGetValue(dialogId, out RecoveryEntry entry))
            {
                ParsekLog.Info(Tag, FormatRecoveryDismissed(scene, fallbackVessel, null));
                return;
            }

            if (!entry.ShownLogged)
                NoteRecoveryVessel(dialogId, fallbackVessel);

            recoveryEntries.Remove(dialogId);
            ParsekLog.Info(Tag, FormatRecoveryDismissed(
                entry.Scene, entry.Vessel ?? fallbackVessel, nowWall - entry.ShownWall));
        }
    }

    /// <summary>
    /// Process-lifetime host for the recovery-dialog feed: subscribes once to stock
    /// <c>GameEvents.onGUIRecoveryDialogSpawn</c> / <c>onGUIRecoveryDialogDespawn</c>
    /// (<c>EventData&lt;MissionRecoveryDialog&gt;</c>, fired from the dialog's Awake / OnDestroy)
    /// and <c>onVesselRecoveryProcessing</c> (<c>EventData&lt;ProtoVessel, MissionRecoveryDialog,
    /// float&gt;</c>, fired by stock VesselRecovery in the same call stack as the spawn, with
    /// the vessel). DontDestroyOnLoad so a dialog torn down by a scene change still reports
    /// its despawn (mirrors <see cref="WarpToTimeConsumer"/>).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class PostFlightDialogLogHost : MonoBehaviour
    {
        private static PostFlightDialogLogHost instance;

        private readonly Dictionary<int, MissionRecoveryDialog> liveDialogs =
            new Dictionary<int, MissionRecoveryDialog>();
        private readonly List<int> pendingScratch = new List<int>();

        void Awake()
        {
            if (instance != null)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);
            GameEvents.onGUIRecoveryDialogSpawn.Add(OnRecoveryDialogSpawn);
            GameEvents.onGUIRecoveryDialogDespawn.Add(OnRecoveryDialogDespawn);
            GameEvents.onVesselRecoveryProcessing.Add(OnVesselRecoveryProcessing);
            ParsekLog.Verbose(PostFlightDialogLog.Tag, "PostFlightDialogLogHost initialized");
        }

        void OnDestroy()
        {
            if (instance != this)
                return;
            instance = null;
            GameEvents.onGUIRecoveryDialogSpawn.Remove(OnRecoveryDialogSpawn);
            GameEvents.onGUIRecoveryDialogDespawn.Remove(OnRecoveryDialogDespawn);
            GameEvents.onVesselRecoveryProcessing.Remove(OnVesselRecoveryProcessing);
        }

        // A dialog that no recovery-processing event named (the science-only variant) gets
        // its shown line here, by which time stock has renamed its GameObject.
        void LateUpdate()
        {
            if (PostFlightDialogLog.PendingRecoveryShownCount <= 0)
                return;

            PostFlightDialogLog.CollectPendingRecoveryIds(pendingScratch);
            for (int i = 0; i < pendingScratch.Count; i++)
            {
                int id = pendingScratch[i];
                liveDialogs.TryGetValue(id, out MissionRecoveryDialog dialog);
                PostFlightDialogLog.NoteRecoveryVessel(id,
                    dialog != null
                        ? PostFlightDialogLog.ParseVesselFromRecoveryHandlerName(dialog.name)
                        : null);
            }
        }

        private void OnRecoveryDialogSpawn(MissionRecoveryDialog dialog)
        {
            if (dialog == null)
                return;
            int id = dialog.GetInstanceID();
            liveDialogs[id] = dialog;
            PostFlightDialogLog.NoteRecoverySpawned(
                id, Time.realtimeSinceStartup, HighLogic.LoadedScene.ToString());
        }

        private void OnVesselRecoveryProcessing(ProtoVessel pv, MissionRecoveryDialog dialog, float factor)
        {
            if (dialog == null)
                return;
            PostFlightDialogLog.NoteRecoveryVessel(
                dialog.GetInstanceID(), pv != null ? pv.GetDisplayName() : null);
        }

        // Fired from the dialog's own OnDestroy, so test reference identity rather than
        // Unity's destroyed-object equality, and read the name defensively.
        private void OnRecoveryDialogDespawn(MissionRecoveryDialog dialog)
        {
            if (ReferenceEquals(dialog, null))
                return;
            int id = dialog.GetInstanceID();
            liveDialogs.Remove(id);
            string name = null;
            try { name = dialog.name; }
            catch (System.Exception) { name = null; }
            PostFlightDialogLog.NoteRecoveryDespawned(
                id, Time.realtimeSinceStartup, HighLogic.LoadedScene.ToString(),
                PostFlightDialogLog.ParseVesselFromRecoveryHandlerName(name));
        }
    }
}
