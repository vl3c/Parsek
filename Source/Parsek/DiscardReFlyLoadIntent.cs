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
        private static double armedRewindPointUT = double.NaN;

        /// <summary>How far (seconds) the loaded clock may sit from the clock of the rewind-point
        /// quicksave the discard loaded and still read as landed on it. Both read the same saved
        /// FLIGHTSTATE, so this only absorbs float formatting.</summary>
        internal const double LandedAtRewindPointToleranceSeconds = 1.0;

        internal static bool IsArmed => armed;

        internal static RevertTarget ArmedTarget => armedTarget;

        /// <summary>The scene the Discard Re-fly dispatch loads for <paramref name="target"/>.</summary>
        internal static GameScenes ExpectedSceneFor(RevertTarget target)
        {
            return target == RevertTarget.Prelaunch ? GameScenes.EDITOR : GameScenes.SPACECENTER;
        }

        internal static void Arm(RevertTarget target, string sessionId, double rewindPointUT = double.NaN)
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
            armedRewindPointUT = rewindPointUT;
            ParsekLog.Info(LoadReconcilePolicy.LogTag,
                $"DiscardReFly load intent armed target={target} sess={sessionId ?? "<no-id>"} "
                + $"expectedScene={ExpectedSceneFor(target)}");
        }

        /// <summary>
        /// Reads and clears the intent. True only when one was armed AND the load landed in the
        /// scene its dispatch targeted; a mismatch drops the intent with a Warn.
        /// </summary>
        internal static bool TryConsume(GameScenes loadedScene, string site, out RevertTarget target,
            double loadedUT = double.NaN)
        {
            target = armedTarget;
            if (!armed)
                return false;

            string sessionId = armedSessionId;
            double rewindPointUT = armedRewindPointUT;
            armed = false;
            armedSessionId = null;
            armedRewindPointUT = double.NaN;
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
                + $"sess={sessionId ?? "<no-id>"} scene={loadedScene} "
                + $"rpSaveUT={FormatUT(rewindPointUT)} loadedUT={FormatUT(loadedUT)} "
                + $"landedAtRewindPoint={LandedAtRewindPointToken(rewindPointUT, loadedUT)}");
            return true;
        }

        /// <summary>
        /// Pure: <c>true</c> when the loaded clock is the clock of the rewind-point quicksave the
        /// discard loaded (within
        /// <see cref="LandedAtRewindPointToleranceSeconds"/>), <c>false</c> when it is not,
        /// <c>unknown</c> when either side is missing. The Discard Re-fly writes the rewind
        /// point's game to persistent.sfs so the scene start lands there
        /// (DISCARD-REFLY-LANDS-ON-PERSISTENT-NOT-THE-REWIND-POINT).
        /// </summary>
        internal static string LandedAtRewindPointToken(double rewindPointUT, double loadedUT)
        {
            if (double.IsNaN(rewindPointUT) || double.IsNaN(loadedUT)
                || double.IsInfinity(rewindPointUT) || double.IsInfinity(loadedUT))
                return "unknown";
            return System.Math.Abs(loadedUT - rewindPointUT) <= LandedAtRewindPointToleranceSeconds
                ? "true" : "false";
        }

        private static string FormatUT(double ut)
        {
            return double.IsNaN(ut)
                ? "NaN"
                : ut.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
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
            armedRewindPointUT = double.NaN;
        }

        internal static void ResetForTesting()
        {
            armed = false;
            armedTarget = RevertTarget.Launch;
            armedSessionId = null;
            armedRewindPointUT = double.NaN;
        }
    }
}
