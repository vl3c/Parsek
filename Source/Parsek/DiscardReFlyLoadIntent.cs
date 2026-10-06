namespace Parsek
{
    /// <summary>
    /// One-shot note that the next <see cref="ParsekScenario.OnLoad"/> is the load Discard
    /// Re-fly issued. Armed by <see cref="RevertInterceptor.DiscardReFlyHandler"/> right after its
    /// <c>GamePersistence.LoadGame</c> succeeded, cleared there when the scene dispatch fails, and
    /// consumed by the next OnLoad's early classification
    /// (<see cref="LoadReconcilePolicy.ClassifyEarly"/>), which is its only reader.
    ///
    /// <para>The consume checks the scene: the discard dispatches to the Space Center (Launch) or
    /// the editor (Prelaunch), and an intent whose dispatch never produced its own OnLoad (a
    /// LoadScene prefix that swallowed it) must not name a later, unrelated load.</para>
    /// </summary>
    internal static class DiscardReFlyLoadIntent
    {
        private static bool armed;
        private static RevertTarget armedTarget;
        private static string armedSessionId;

        internal static bool IsArmed => armed;

        internal static RevertTarget ArmedTarget => armedTarget;

        /// <summary>The scene the Discard Re-fly dispatch loads for <paramref name="target"/>.</summary>
        internal static GameScenes ExpectedSceneFor(RevertTarget target)
        {
            return target == RevertTarget.Prelaunch ? GameScenes.EDITOR : GameScenes.SPACECENTER;
        }

        internal static void Arm(RevertTarget target, string sessionId)
        {
            if (armed)
            {
                ParsekLog.Warn(LoadReconcilePolicy.LogTag,
                    $"DiscardReFly load intent re-armed before the previous one was consumed: "
                    + $"previous target={armedTarget} sess={armedSessionId ?? "<no-id>"}");
            }
            armed = true;
            armedTarget = target;
            armedSessionId = sessionId;
            ParsekLog.Info(LoadReconcilePolicy.LogTag,
                $"DiscardReFly load intent armed target={target} sess={sessionId ?? "<no-id>"} "
                + $"expectedScene={ExpectedSceneFor(target)}");
        }

        /// <summary>
        /// Reads and clears the intent. True only when one was armed AND the load landed in the
        /// scene its dispatch targeted; a mismatch drops the intent with a Warn.
        /// </summary>
        internal static bool TryConsume(GameScenes loadedScene, string site, out RevertTarget target)
        {
            target = armedTarget;
            if (!armed)
                return false;

            string sessionId = armedSessionId;
            armed = false;
            armedSessionId = null;
            GameScenes expected = ExpectedSceneFor(target);
            if (loadedScene != expected)
            {
                ParsekLog.Warn(LoadReconcilePolicy.LogTag,
                    $"DiscardReFly load intent dropped as stale at {site ?? "<no-site>"}: target={target} "
                    + $"sess={sessionId ?? "<no-id>"} expectedScene={expected} loadedScene={loadedScene}");
                return false;
            }

            ParsekLog.Info(LoadReconcilePolicy.LogTag,
                $"DiscardReFly load intent consumed at {site ?? "<no-site>"}: target={target} "
                + $"sess={sessionId ?? "<no-id>"} scene={loadedScene}");
            return true;
        }

        internal static void Clear(string reason)
        {
            if (!armed)
            {
                ParsekLog.Verbose(LoadReconcilePolicy.LogTag,
                    $"DiscardReFly load intent clear requested reason={reason ?? "<none>"}: nothing armed");
                return;
            }

            ParsekLog.Info(LoadReconcilePolicy.LogTag,
                $"DiscardReFly load intent cleared reason={reason ?? "<none>"} target={armedTarget} "
                + $"sess={armedSessionId ?? "<no-id>"}");
            armed = false;
            armedSessionId = null;
        }

        internal static void ResetForTesting()
        {
            armed = false;
            armedTarget = RevertTarget.Launch;
            armedSessionId = null;
        }
    }
}
