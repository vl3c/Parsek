using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Parsek
{
    /// <summary>
    /// Startup problems the player should hear about once per game session, not only in
    /// KSP.log: Harmony patches that failed to apply (a reservation block or recorder hook
    /// silently missing) and a Parsek folder that is not directly under GameData (the
    /// toolbar textures are looked up as <c>Parsek/Textures/...</c>).
    ///
    /// Both are EVENTS of this process start, so each produces one
    /// <see cref="ParsekLog.ScreenMessage"/> (the only player channel CLAUDE.md allows for
    /// this). The evaluation is pure; <see cref="ParsekHarmony"/> collects the inputs at
    /// <c>Startup.Instantly</c> and posts the queued notices once a playable scene settles,
    /// because KSP clears screen messages on every level load.
    /// </summary>
    internal static class StartupNotices
    {
        internal const float NoticeDurationSeconds = 15f;

        /// <summary>Realtime seconds a scene must stay loaded before the notices post.</summary>
        internal const float SceneSettleSeconds = 3f;

        /// <summary>Feature names listed in the patch notice before "and N more".</summary>
        internal const int MaxListedFeatures = 3;

        private static readonly List<string> pending = new List<string>();

        // Ordered: the first rule whose prefix starts the patch class name wins, so the
        // switch-intent Fly patch is "vessel switch recording", not "ghost vessels".
        private static readonly (string[] prefixes, string feature)[] FeatureRules =
        {
            (new[] { "SwitchIntent", "MapFocusObject", "KscVesselMarkerFly" }, "vessel switch recording"),
            (new[] { "Ghost", "FlightStateGhostBudget", "SunLateUpdateGuard" }, "ghost vessels"),
            (new[] { "Tech", "RnD", "RDTech", "PartListTooltip" }, "tech tree reservations"),
            (new[] { "Facility" }, "facility upgrade reservations"),
            (new[] { "Contract", "MissionControl" }, "contract reservations"),
            (new[] { "Strategy", "Administration" }, "strategy reservations"),
            (new[] { "Kerbal", "Crew", "AstronautComplex", "ActiveCrewCount", "FlightEvaSpawn" }, "crew reservations"),
            (new[] { "ScienceSubject", "ProgressReward" }, "science and milestone rewards"),
            (new[] { "PhysicsFrame", "PartMassivePartCheck", "CheatTeleport", "HackGravity" }, "flight recording"),
            (new[] { "Revert", "HighLogic_LoadScene", "FloatingOrigin" }, "revert, rewind and scene changes"),
        };

        /// <summary>
        /// The player-facing feature a Harmony patch class serves, or null when no rule
        /// names it (a unit test keeps every patch class in the assembly covered).
        /// </summary>
        internal static string PatchFeatureFor(string patchClassName)
        {
            if (string.IsNullOrEmpty(patchClassName))
                return null;
            foreach (var rule in FeatureRules)
            {
                foreach (var prefix in rule.prefixes)
                {
                    if (patchClassName.StartsWith(prefix, StringComparison.Ordinal))
                        return rule.feature;
                }
            }
            return null;
        }

        /// <summary>
        /// The notice for failed Harmony patches, or null when none failed. Lists the
        /// distinct affected features in first-failure order.
        /// </summary>
        internal static string BuildPatchFailureNotice(int total, IList<string> failedPatchClassNames)
        {
            int failed = failedPatchClassNames?.Count ?? 0;
            if (failed == 0)
                return null;

            var features = new List<string>();
            bool anyUnnamed = false;
            foreach (var name in failedPatchClassNames)
            {
                string feature = PatchFeatureFor(name);
                if (feature == null)
                    anyUnnamed = true;
                else if (!features.Contains(feature))
                    features.Add(feature);
            }
            if (anyUnnamed)
                features.Add("other features");

            string list;
            if (features.Count <= MaxListedFeatures)
                list = string.Join(", ", features);
            else
                list = string.Join(", ", features.Take(MaxListedFeatures))
                    + string.Format(CultureInfo.InvariantCulture, " and {0} more",
                        features.Count - MaxListedFeatures);

            return string.Format(CultureInfo.InvariantCulture,
                "{0} of {1} game patches failed to load, so these may not work: {2}. "
                + "Another mod or a different KSP version is the usual cause. Details are in KSP.log.",
                failed, total, list);
        }

        /// <summary>
        /// The notice for a misplaced Parsek install, or null when the install looks right or
        /// cannot be judged. <paramref name="parsekDllPaths"/> are the file paths of the
        /// loaded assemblies named Parsek; <paramref name="gameDataDir"/> is the KSP GameData
        /// directory. Folder names compare case-insensitively so only a clear misinstall is
        /// reported. KSP's AssemblyLoader keeps ONE entry per assembly name (the highest
        /// version among same-named copies), so a duplicate install is not visible here; the
        /// wording covers it for the case where the stray copy is the one that loaded.
        /// </summary>
        internal static string EvaluateInstallLocation(IList<string> parsekDllPaths, string gameDataDir)
        {
            if (parsekDllPaths == null || parsekDllPaths.Count == 0 || string.IsNullOrEmpty(gameDataDir))
                return null;

            var relatives = new List<string>();
            foreach (var path in parsekDllPaths)
            {
                string rel = RelativeToGameData(path, gameDataDir);
                if (rel != null)
                    relatives.Add(rel);
            }
            if (relatives.Count == 0)
                return null;

            var misplaced = relatives.Where(rel =>
            {
                string[] segments = rel.Split('/');
                return !(segments.Length >= 2
                    && string.Equals(segments[0], "Parsek", StringComparison.OrdinalIgnoreCase));
            }).ToList();
            if (misplaced.Count == 0)
                return null;

            return "Parsek is loading from the wrong folder ("
                + string.Join(", ", misplaced.Select(r => "GameData/" + r))
                + "). It must be in GameData/Parsek; if you have more than one copy, keep only that one.";
        }

        /// <summary>
        /// <paramref name="path"/> relative to <paramref name="gameDataDir"/> with forward
        /// slashes, or null when it is not inside that directory. Both must already be full
        /// paths (the caller resolves <c>KSPUtil.ApplicationRootPath</c>'s <c>..</c> segment).
        /// </summary>
        internal static string RelativeToGameData(string path, string gameDataDir)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(gameDataDir))
                return null;
            string p = NormalizeSlashes(path);
            string root = NormalizeSlashes(gameDataDir).TrimEnd('/') + "/";
            if (!p.StartsWith(root, StringComparison.OrdinalIgnoreCase) || p.Length == root.Length)
                return null;
            return p.Substring(root.Length);
        }

        private static string NormalizeSlashes(string path)
        {
            string p = path.Replace('\\', '/');
            while (p.Contains("//"))
                p = p.Replace("//", "/");
            return p;
        }

        internal static void Enqueue(string notice)
        {
            if (!string.IsNullOrEmpty(notice))
                pending.Add(notice);
        }

        internal static int PendingCount => pending.Count;

        /// <summary>Returns and clears the queued notices.</summary>
        internal static List<string> TakePending()
        {
            var taken = new List<string>(pending);
            pending.Clear();
            return taken;
        }

        /// <summary>
        /// True for the scenes where a posted message stays readable: the loading screen
        /// covers the UI, and every level load clears active screen messages.
        /// </summary>
        internal static bool IsPostableScene(GameScenes scene)
        {
            switch (scene)
            {
                case GameScenes.MAINMENU:
                case GameScenes.SPACECENTER:
                case GameScenes.EDITOR:
                case GameScenes.FLIGHT:
                case GameScenes.TRACKSTATION:
                    return true;
                default:
                    return false;
            }
        }

        internal static void ResetForTesting()
        {
            pending.Clear();
        }
    }

    /// <summary>
    /// One-frame step of the startup-notice poster, driven by a coroutine on
    /// <see cref="ParsekHarmony"/>. Waits until one postable scene has stayed current for
    /// <see cref="StartupNotices.SceneSettleSeconds"/> with ScreenMessages present, then
    /// posts every queued notice; a post stock drops (a scene load in progress) goes back
    /// in the queue and is retried on the next tick.
    /// </summary>
    internal sealed class StartupNoticePoster
    {
        private GameScenes candidateScene = GameScenes.LOADING;
        private float candidateSince = -1f;
        private bool settled;

        internal bool Settled => settled;
        internal int DroppedAttempts { get; private set; }

        /// <summary>Returns true once the queue is empty (the coroutine can stop).</summary>
        internal bool Tick(GameScenes scene, bool screenMessagesReady, float realtimeNow,
            Func<string, bool> tryPost)
        {
            if (StartupNotices.PendingCount == 0)
                return true;

            if (!settled)
            {
                if (!StartupNotices.IsPostableScene(scene) || !screenMessagesReady)
                {
                    candidateSince = -1f;
                    return false;
                }
                if (candidateSince < 0f || scene != candidateScene)
                {
                    candidateScene = scene;
                    candidateSince = realtimeNow;
                    return false;
                }
                if (realtimeNow - candidateSince < StartupNotices.SceneSettleSeconds)
                    return false;
                settled = true;
            }

            foreach (var notice in StartupNotices.TakePending())
            {
                if (tryPost(notice))
                {
                    ParsekLog.Info("Init", string.Format(CultureInfo.InvariantCulture,
                        "Startup notice posted in {0} after {1} dropped attempt(s): {2}",
                        scene, DroppedAttempts, notice));
                }
                else
                {
                    DroppedAttempts++;
                    StartupNotices.Enqueue(notice);
                }
            }
            return StartupNotices.PendingCount == 0;
        }
    }
}
