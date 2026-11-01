using System;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Recognises stock's "quit without saving" exit out of flight and carries that fact to the
    /// scene-exit finalize of the same load (owner ruling 2026-11-01: the recording follows the
    /// save; a quit without saving drops the flight since the last save, as stock drops it from
    /// the world).
    ///
    /// <para>Decompiled KSP 1.12.5: <c>GameEvents.onSceneConfirmExit</c> is fired only by
    /// <c>PauseMenu</c>, and every one of its fire sites but one calls <c>saveAndExit</c> (or
    /// <c>GamePersistence.SaveGame</c>) right after it, which writes persistent.sfs through
    /// <c>Game.Save</c> and so fires <c>GameEvents.onGameStateSaved</c> before
    /// <c>HighLogic.LoadScene</c>. The exception is the quit-without-saving button
    /// (<c>drawExitWithoutSaveOptions</c>, offered when <c>FlightGlobals.ClearToSave</c> refused
    /// and the game allows a restart): it fires the event and calls <c>LoadScene</c> directly.
    /// So a flight exit whose <c>LoadScene</c> runs in the frame of a confirm-exit with no save
    /// after it wrote nothing, and the Space Center / Tracking Station start reloads the older
    /// persistent.sfs (<c>SpaceCenterMain.Start</c> / <c>SpaceTracking</c>
    /// <c>GamePersistence.LoadGame</c>). Parsek's own <c>SafeWritePersistent</c> and the seam's
    /// exits fire both events, so they read as saved.</para>
    ///
    /// <para>Both handlers and the <c>LoadScene</c> prefix run synchronously in one frame
    /// (<c>HighLogic.LoadScene</c> fires <c>onGameSceneLoadRequested</c> inside the call), so
    /// the note and the arm are frame-exact; nothing older than the current frame counts. That
    /// is also why nothing clears the arm at the main-menu transition: a clear there would race
    /// the flight's scene-exit finalize, both being <c>onGameSceneLoadRequested</c> handlers.</para>
    /// </summary>
    internal static class NoSaveSceneExit
    {
        internal const string LogTag = "SceneExit";

        /// <summary>Test seam for the current frame (Unity's <c>Time.frameCount</c> otherwise).</summary>
        internal static Func<int> FrameForTesting;

        private static int confirmExitFrame = -1;
        private static bool savedSinceConfirmExit;

        private static bool armed;
        private static int armedFrame = -1;
        private static GameScenes armedDestination = GameScenes.LOADING;

        /// <summary>
        /// Pure: true when a confirm-exit was noted in <paramref name="nowFrame"/> and no game
        /// save followed it in that frame.
        /// </summary>
        internal static bool IsNoSaveExit(int confirmFrame, bool savedSinceConfirm, int nowFrame)
        {
            return confirmFrame >= 0 && confirmFrame == nowFrame && !savedSinceConfirm;
        }

        /// <summary>
        /// Pure: a detected quit without saving follows the save for the three stock no-save
        /// destinations, unless a rewind owns the load, a Re-Fly is live (its exit stays a manual
        /// decision, owner ruling 2026-11-01) or a merge journal is still finishing.
        /// </summary>
        internal static bool ShouldFollowTheSave(
            GameScenes destination, bool noSaveExit, bool rewinding, bool reFlyActive, bool mergeJournalActive)
        {
            if (!noSaveExit || rewinding || reFlyActive || mergeJournalActive)
                return false;
            return destination == GameScenes.SPACECENTER
                || destination == GameScenes.TRACKSTATION
                || destination == GameScenes.MAINMENU;
        }

        internal static void NoteConfirmExit(int frame)
        {
            confirmExitFrame = frame;
            savedSinceConfirmExit = false;
        }

        internal static void NoteGameSaved(int frame)
        {
            if (confirmExitFrame >= 0 && confirmExitFrame == frame)
                savedSinceConfirmExit = true;
        }

        /// <summary>
        /// Reads and clears the confirm-exit note at a <c>LoadScene</c> prefix: true when this
        /// load is a stock quit without saving.
        /// </summary>
        internal static bool ConsumeAtLoadScene(GameScenes destination)
        {
            if (confirmExitFrame < 0)
                return false;
            int now = CurrentFrame();
            bool noSave = IsNoSaveExit(confirmExitFrame, savedSinceConfirmExit, now);
            ParsekLog.Verbose(LogTag,
                $"Quit-without-saving check dest={destination}: confirmExitFrame={confirmExitFrame} " +
                $"frame={now} savedSinceConfirmExit={savedSinceConfirmExit} noSave={noSave}");
            confirmExitFrame = -1;
            savedSinceConfirmExit = false;
            return noSave;
        }

        /// <summary>Marks this frame's exit to <paramref name="destination"/> as following the save.</summary>
        internal static void Arm(GameScenes destination)
        {
            armed = true;
            armedFrame = CurrentFrame();
            armedDestination = destination;
            ParsekLog.Info(LogTag,
                $"Quit-without-saving exit armed dest={destination} frame={armedFrame}");
        }

        /// <summary>True when this frame's exit to <paramref name="destination"/> follows the save.</summary>
        internal static bool IsArmedFor(GameScenes destination)
        {
            if (!armed)
                return false;
            return armedDestination == destination && armedFrame == CurrentFrame();
        }

        internal static void Clear(string reason)
        {
            if (!armed && confirmExitFrame < 0)
                return;
            ParsekLog.Verbose(LogTag,
                $"Quit-without-saving state cleared reason={reason ?? "<none>"} armed={armed} " +
                $"dest={armedDestination}");
            armed = false;
            armedFrame = -1;
            armedDestination = GameScenes.LOADING;
            confirmExitFrame = -1;
            savedSinceConfirmExit = false;
        }

        internal static void ResetForTesting()
        {
            FrameForTesting = null;
            armed = false;
            armedFrame = -1;
            armedDestination = GameScenes.LOADING;
            confirmExitFrame = -1;
            savedSinceConfirmExit = false;
        }

        private static int CurrentFrame()
        {
            return FrameForTesting != null ? FrameForTesting() : Time.frameCount;
        }
    }

    /// <summary>
    /// Subscribes <see cref="NoSaveSceneExit"/> to <c>GameEvents.onSceneConfirmExit</c> and
    /// <c>GameEvents.onGameStateSaved</c> for the whole process. Instance handlers, because KSP's
    /// <c>EvtDelegate</c> reads <c>Target.GetType()</c> (a static handler NREs inside
    /// <c>GameEvents.Add</c>).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class NoSaveSceneExitAddon : MonoBehaviour
    {
        private static bool subscribed;

        public void Awake()
        {
            DontDestroyOnLoad(gameObject);
            if (subscribed)
                return;
            subscribed = true;
            GameEvents.onSceneConfirmExit.Add(OnSceneConfirmExit);
            GameEvents.onGameStateSaved.Add(OnGameStateSaved);
            ParsekLog.Info(NoSaveSceneExit.LogTag,
                "Subscribed to GameEvents.onSceneConfirmExit and onGameStateSaved (quit-without-saving detection)");
        }

        private void OnSceneConfirmExit(GameScenes scene)
        {
            NoSaveSceneExit.NoteConfirmExit(Time.frameCount);
        }

        private void OnGameStateSaved(Game game)
        {
            NoSaveSceneExit.NoteGameSaved(Time.frameCount);
        }
    }
}
