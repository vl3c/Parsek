using System;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin Unity host for the ParsekTestCommands seam (M-A2). A DDOL
    /// <see cref="MonoBehaviour"/> that, ONLY when armed by the
    /// <c>PARSEK_TEST_COMMANDS=1</c> environment variable, polls the KSP-root command
    /// file, executes commands on the Unity main thread, and appends responses. The
    /// parse / validate / dispatch / journal / lock logic all lives in the pure
    /// <c>Parsek.TestCommands</c> core (xUnit-tested without Unity); this addon only
    /// samples live state, performs file I/O, and calls those decisions. It mirrors
    /// <see cref="Parsek.InGameTests.TestRunnerShortcut"/>'s lifecycle pattern
    /// (Instantly + DontDestroyOnLoad + singleton guard + scene-scoped safe-point
    /// gating).
    ///
    /// <para>Fail-closed and provably inert: the env var is read ONCE in
    /// <see cref="Awake"/> (changing it requires a process restart), and when it is not
    /// the literal <c>1</c> the addon takes NO file handles and does NO polling work -
    /// <see cref="Update"/> returns immediately on the cached <see cref="armed"/> bool.
    /// It is never shipped enabled and adds no Settings-UI toggle.</para>
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class ParsekTestCommandAddon : MonoBehaviour
    {
        private const string Tag = "TestCommands";

        /// <summary>The environment variable that arms the addon.</summary>
        internal const string EnvVarName = "PARSEK_TEST_COMMANDS";

        /// <summary>The ONLY value that arms it (exact match, fail-closed).</summary>
        internal const string ArmValue = "1";

        /// <summary>Frames to wait after a new scene loads before it is "settled" and the
        /// pump may execute. Matches the scene-scoped-state pattern TestRunnerShortcut
        /// uses for its input lock.</summary>
        private const int SettleFrames = 2;

        private static ParsekTestCommandAddon instance;

        /// <summary>Singleton accessor - non-null after Awake while DDOL keeps the
        /// instance alive. Used by in-game tests to assert inert-when-unarmed.</summary>
        internal static ParsekTestCommandAddon Instance => instance;

        // Cached ONCE in Awake from the env var; changing PARSEK_TEST_COMMANDS after
        // process start has NO effect (documented: env change requires a KSP restart).
        private bool armed;

        // Scene-scoped safe-point gating. onGameSceneLoadRequested sets transitioning;
        // onLevelWasLoaded seeds the settle counter for the new scene; Update drains it
        // and clears transitioning at the settle boundary.
        private bool sceneTransitioning;
        private int settleCounter;

        internal bool IsArmedForTesting => armed;
        internal bool SceneTransitioningForTesting => sceneTransitioning;
        internal int SettleCounterForTesting => settleCounter;

        /// <summary>
        /// Pure fail-closed env-gate predicate: ONLY the literal <c>"1"</c> arms the
        /// addon. <c>null</c> (unset), <c>"0"</c>, <c>"true"</c>, and <c>""</c> all stay
        /// inert. This is the security boundary that prevents the seam from ever shipping
        /// enabled by accident.
        /// </summary>
        internal static bool IsArmed(string envValue) => envValue == ArmValue;

        void Awake()
        {
            if (instance != null)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);

            // Read ONCE. The env var is the entire arm gate; it is never re-read.
            string envValue = Environment.GetEnvironmentVariable(EnvVarName);
            armed = IsArmed(envValue);

            if (armed)
            {
                ParsekLog.Info(Tag, "armed (PARSEK_TEST_COMMANDS=1)");
                // Scene-scoped state only (no gameplay subscriptions), matching
                // TestRunnerShortcut: the addon tracks scene transitions purely for its
                // own safe-point gating.
                GameEvents.onGameSceneLoadRequested.Add(OnSceneChangeRequested);
                GameEvents.onLevelWasLoaded.Add(OnLevelWasLoaded);
            }
            else
            {
                ParsekLog.Verbose(Tag, $"inert: PARSEK_TEST_COMMANDS={FormatEnvForLog(envValue)}");
            }
        }

        void Update()
        {
            // Provably inert when unarmed: no polling, no file access - return on the
            // cached bool before any work.
            if (!armed) return;

            if (settleCounter > 0)
            {
                settleCounter--;
                if (settleCounter == 0)
                    sceneTransitioning = false;
            }
        }

        void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
                if (armed)
                {
                    GameEvents.onGameSceneLoadRequested.Remove(OnSceneChangeRequested);
                    GameEvents.onLevelWasLoaded.Remove(OnLevelWasLoaded);
                }
            }
        }

        // A scene load has been requested: we are leaving the current scene. Gate the
        // pump off until the new scene loads and settles.
        private void OnSceneChangeRequested(GameScenes scene)
        {
            sceneTransitioning = true;
            settleCounter = 0;
        }

        // The new scene is active: start the settle countdown. Transitioning clears when
        // the counter drains (in Update), a couple of frames into the new scene.
        private void OnLevelWasLoaded(GameScenes scene)
        {
            settleCounter = SettleFrames;
        }

        /// <summary>Human-readable rendering of the env value for the inert log line:
        /// <c>unset</c> for null, <c>empty</c> for the empty string, else the raw value.</summary>
        internal static string FormatEnvForLog(string envValue)
            => envValue == null ? "unset" : (envValue.Length == 0 ? "empty" : envValue);
    }
}
