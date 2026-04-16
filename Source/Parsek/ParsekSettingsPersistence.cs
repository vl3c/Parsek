using System;
using System.Globalization;
using System.IO;
using System.Security;

namespace Parsek
{
    /// <summary>
    /// Persists user-modified Parsek settings to an external config file at
    /// <c>GameData/Parsek/settings.cfg</c>, surviving rewind, save/load, and
    /// KSP session restarts.
    ///
    /// <para>
    /// Motivation: <see cref="ParsekSettings"/> inherits from
    /// <c>GameParameters.CustomParameterNode</c>, so values live inside
    /// <c>HighLogic.CurrentGame.Parameters</c> and are reset every time KSP
    /// loads a save (including the parsek_rw_* rewind quicksaves). The user
    /// expectation is that settings they explicitly changed should stick —
    /// they represent user intent, not per-save state.
    /// </para>
    ///
    /// <para>
    /// This store holds the authoritative value for a small set of
    /// user-intent settings. After KSP loads GameParameters, callers invoke
    /// <see cref="ApplyTo"/> to overwrite the loaded (stale) values with
    /// the persisted (fresh) ones. Callers that mutate settings via the UI
    /// invoke <see cref="MarkDirty"/> so the change is flushed to disk.
    /// </para>
    ///
    /// <para>
    /// Currently tracks <c>ghostCameraCutoffKm</c>, <c>writeReadableSidecarMirrors</c>,
    /// and <c>showGhostsInTrackingStation</c> (#388). Add more fields here if
    /// additional settings turn out to need the same survival semantics.
    /// </para>
    /// </summary>
    internal static class ParsekSettingsPersistence
    {
        private const string Tag = "SettingsStore";
        private const string FileName = "settings.cfg";
        private const string RootNodeName = "PARSEK_SETTINGS";
        private const string GhostCameraCutoffKey = "ghostCameraCutoffKm";
        private const string ReadableSidecarMirrorsKey = "writeReadableSidecarMirrors";
        private const string ShowGhostsInTrackingStationKey = "showGhostsInTrackingStation";

        // Null = no stored value (use defaults / whatever GameParameters loaded).
        // Non-null = user-set override, applied over GameParameters on load.
        private static float? storedGhostCameraCutoffKm;
        private static bool? storedReadableSidecarMirrors;
        private static bool? storedShowGhostsInTrackingStation;
        private static bool loaded;

        /// <summary>
        /// Resolves the settings file path under GameData/Parsek/PluginData/.
        /// PluginData is the KSP convention for runtime-written, non-asset state:
        /// it's excluded from ModuleManager's patch cache, survives mod updates
        /// (as long as the user doesn't wipe the folder), and is the standard
        /// place mods store per-installation settings.
        /// </summary>
        internal static string GetFilePath()
        {
            string root = KSPUtil.ApplicationRootPath ?? "";
            return Path.Combine(root, "GameData", "Parsek", "PluginData", FileName);
        }

        /// <summary>
        /// Loads the settings file from disk into the static store. Safe to call
        /// multiple times — subsequent calls are no-ops unless <see cref="ResetForTesting"/>
        /// was called. Missing file is not an error (first-run case).
        /// </summary>
        internal static void LoadIfNeeded()
        {
            if (loaded) return;
            loaded = true;

            string path = GetFilePath();
            if (!File.Exists(path))
            {
                ParsekLog.Verbose(Tag, $"No settings file at '{path}' — using defaults");
                return;
            }

            try
            {
                ConfigNode root = ConfigNode.Load(path);
                if (root == null)
                {
                    ParsekLog.Warn(Tag, $"ConfigNode.Load returned null for '{path}' — using defaults");
                    return;
                }

                // ConfigNode.Load returns the node containing the file contents directly,
                // which for our format is the PARSEK_SETTINGS body. Values live at the top level.
                string cutoffStr = root.GetValue(GhostCameraCutoffKey);
                if (!string.IsNullOrEmpty(cutoffStr)
                    && float.TryParse(cutoffStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float cutoff))
                {
                    storedGhostCameraCutoffKm = cutoff;
                }
                else
                {
                    ParsekLog.Verbose(Tag, $"Settings file '{path}' has no {GhostCameraCutoffKey} — using default");
                }

                string mirrorsStr = root.GetValue(ReadableSidecarMirrorsKey);
                if (!string.IsNullOrEmpty(mirrorsStr)
                    && bool.TryParse(mirrorsStr, out bool writeMirrors))
                {
                    storedReadableSidecarMirrors = writeMirrors;
                }
                else
                {
                    ParsekLog.Verbose(Tag, $"Settings file '{path}' has no {ReadableSidecarMirrorsKey} — using default");
                }

                string showGhostsStr = root.GetValue(ShowGhostsInTrackingStationKey);
                if (!string.IsNullOrEmpty(showGhostsStr)
                    && bool.TryParse(showGhostsStr, out bool showGhosts))
                {
                    storedShowGhostsInTrackingStation = showGhosts;
                }
                else
                {
                    ParsekLog.Verbose(Tag, $"Settings file '{path}' has no {ShowGhostsInTrackingStationKey} — using default");
                }

                ParsekLog.Info(Tag,
                    $"Loaded settings from '{path}': ghostCameraCutoffKm=" +
                    (storedGhostCameraCutoffKm?.ToString("F0", CultureInfo.InvariantCulture) ?? "<default>") +
                    $" writeReadableSidecarMirrors={(storedReadableSidecarMirrors.HasValue ? storedReadableSidecarMirrors.Value.ToString() : "<default>")}" +
                    $" showGhostsInTrackingStation={(storedShowGhostsInTrackingStation.HasValue ? storedShowGhostsInTrackingStation.Value.ToString() : "<default>")}");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"Failed to load settings file '{path}': {ex.Message}");
            }
        }

        /// <summary>
        /// Applies any stored user-override values on top of the current
        /// <see cref="ParsekSettings"/> instance. Called from
        /// <c>ParsekScenario.OnLoad</c> after KSP's GameParameters restore runs.
        /// </summary>
        internal static void ApplyTo(ParsekSettings settings)
        {
            if (settings == null) return;
            LoadIfNeeded();

            if (storedGhostCameraCutoffKm.HasValue
                && !FloatEquals(storedGhostCameraCutoffKm.Value, settings.ghostCameraCutoffKm))
            {
                float prev = settings.ghostCameraCutoffKm;
                settings.ghostCameraCutoffKm = storedGhostCameraCutoffKm.Value;
                ParsekLog.Info(Tag,
                    $"Restored ghostCameraCutoffKm {prev.ToString("F0", CultureInfo.InvariantCulture)}" +
                    $" → {storedGhostCameraCutoffKm.Value.ToString("F0", CultureInfo.InvariantCulture)}" +
                    " from persistent store");
            }

            if (storedReadableSidecarMirrors.HasValue
                && storedReadableSidecarMirrors.Value != settings.writeReadableSidecarMirrors)
            {
                bool prev = settings.writeReadableSidecarMirrors;
                settings.writeReadableSidecarMirrors = storedReadableSidecarMirrors.Value;
                ParsekLog.Info(Tag,
                    $"Restored writeReadableSidecarMirrors {prev} -> {storedReadableSidecarMirrors.Value} from persistent store");
            }

            if (storedShowGhostsInTrackingStation.HasValue
                && storedShowGhostsInTrackingStation.Value != settings.showGhostsInTrackingStation)
            {
                bool prev = settings.showGhostsInTrackingStation;
                settings.showGhostsInTrackingStation = storedShowGhostsInTrackingStation.Value;
                ParsekLog.Info(Tag,
                    $"Restored showGhostsInTrackingStation {prev} -> {storedShowGhostsInTrackingStation.Value} from persistent store");
            }
        }

        /// <summary>
        /// Records a user-intent setting change and writes it to disk immediately.
        /// Called from the settings UI commit path after the user confirms a new value.
        /// </summary>
        internal static void RecordGhostCameraCutoff(float value)
        {
            LoadIfNeeded();
            storedGhostCameraCutoffKm = value;
            Save();
        }

        internal static void RecordReadableSidecarMirrors(bool value)
        {
            LoadIfNeeded();
            storedReadableSidecarMirrors = value;
            Save();
        }

        internal static void RecordShowGhostsInTrackingStation(bool value)
        {
            LoadIfNeeded();
            storedShowGhostsInTrackingStation = value;
            Save();
        }

        /// <summary>
        /// Resolve the effective <c>showGhostsInTrackingStation</c> value. Precedence:
        /// <list type="number">
        ///   <item>Live <c>ParsekSettings.Current</c> when resolvable — authoritative
        ///     after <c>ParsekScenario.OnLoad</c> reconciles the store into it, and
        ///     the ONLY surface that catches a flip from KSP's stock Game Parameters
        ///     menu (which mutates the field directly, bypassing
        ///     <see cref="RecordShowGhostsInTrackingStation"/>). When live differs
        ///     from stored we resync the store so the next cold-start window (before
        ///     <c>ParsekSettings.Current</c> resolves) reads the user's real intent.</item>
        ///   <item>Persisted store (settings.cfg) — fallback for the early-scene-load
        ///     window. Does NOT depend on <c>HighLogic.CurrentGame</c>, so it's
        ///     readable during <c>SpaceTracking.Awake</c> where
        ///     <c>ParsekSettings.Current</c> can be null (see <c>ParsekScenario.cs:546</c>
        ///     comment).</item>
        ///   <item>Default <c>true</c> (pre-#388 behavior).</item>
        /// </list>
        /// </summary>
        internal static bool EffectiveShowGhostsInTrackingStation()
        {
            // Swallow ONLY the "called outside Unity runtime" case —
            // KSPUtil.ApplicationRootPath throws SecurityException/ECall under
            // xUnit. Real disk-read failures are handled inside LoadIfNeeded
            // itself and logged at Warn level, so we deliberately don't mask
            // those here: a genuine settings.cfg corruption bug has to remain
            // loud or the user's stored preference would silently revert to
            // the pre-#388 default — the exact symptom this helper is
            // supposed to prevent.
            try { LoadIfNeeded(); }
            catch (SecurityException ex)
            {
                ParsekLog.Verbose(Tag,
                    $"EffectiveShowGhostsInTrackingStation: LoadIfNeeded threw SecurityException " +
                    $"(likely xUnit / non-Unity context: {ex.Message}) — using in-memory fallback");
            }

            // Live Current wins when available. The stock Game Parameters UI writes
            // directly to this field (bypassing RecordShowGhostsInTrackingStation), so
            // reading from the store first would mask that flip for the rest of the
            // session. When live and stored disagree, persist the live value so the
            // next cold-start reads the user's current intent — the store still acts
            // as the early-scene-load fallback below.
            var current = ParsekSettings.Current;
            if (current != null)
            {
                if (!storedShowGhostsInTrackingStation.HasValue
                    || storedShowGhostsInTrackingStation.Value != current.showGhostsInTrackingStation)
                {
                    ParsekLog.Info(Tag,
                        $"Live showGhostsInTrackingStation={current.showGhostsInTrackingStation}" +
                        $" differs from stored={(storedShowGhostsInTrackingStation.HasValue ? storedShowGhostsInTrackingStation.Value.ToString() : "<null>")}" +
                        " — resyncing store (likely a flip from KSP Game Parameters UI)");
                    storedShowGhostsInTrackingStation = current.showGhostsInTrackingStation;
                    // Same xUnit guard as LoadIfNeeded above — KSPUtil.ApplicationRootPath
                    // inside Save → GetFilePath throws SecurityException under xUnit.
                    // In-memory store is still updated, which is what the tests assert.
                    try { Save(); }
                    catch (SecurityException ex)
                    {
                        ParsekLog.Verbose(Tag,
                            $"EffectiveShowGhostsInTrackingStation: Save threw SecurityException " +
                            $"(likely xUnit / non-Unity context: {ex.Message}) — store is in-memory only");
                    }
                }
                return current.showGhostsInTrackingStation;
            }

            if (storedShowGhostsInTrackingStation.HasValue)
                return storedShowGhostsInTrackingStation.Value;
            return true;
        }

        /// <summary>
        /// Writes the current store to disk via the shared safe-write helper.
        /// </summary>
        private static void Save()
        {
            string path = GetFilePath();
            try
            {
                var root = new ConfigNode(RootNodeName);
                if (storedGhostCameraCutoffKm.HasValue)
                {
                    root.AddValue(GhostCameraCutoffKey,
                        storedGhostCameraCutoffKm.Value.ToString("R", CultureInfo.InvariantCulture));
                }
                if (storedReadableSidecarMirrors.HasValue)
                    root.AddValue(ReadableSidecarMirrorsKey, storedReadableSidecarMirrors.Value.ToString());
                if (storedShowGhostsInTrackingStation.HasValue)
                    root.AddValue(ShowGhostsInTrackingStationKey, storedShowGhostsInTrackingStation.Value.ToString());
                FileIOUtils.SafeWriteConfigNode(root, path, Tag);
                ParsekLog.Verbose(Tag,
                    $"Saved settings to '{path}': ghostCameraCutoffKm=" +
                    (storedGhostCameraCutoffKm?.ToString("F0", CultureInfo.InvariantCulture) ?? "<null>") +
                    $" writeReadableSidecarMirrors={(storedReadableSidecarMirrors.HasValue ? storedReadableSidecarMirrors.Value.ToString() : "<null>")}" +
                    $" showGhostsInTrackingStation={(storedShowGhostsInTrackingStation.HasValue ? storedShowGhostsInTrackingStation.Value.ToString() : "<null>")}");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"Failed to save settings file '{path}': {ex.Message}");
            }
        }

        private static bool FloatEquals(float a, float b) => Math.Abs(a - b) < 1e-4f;

        /// <summary>
        /// Test-only: clears the static store so LoadIfNeeded re-reads the file.
        /// </summary>
        internal static void ResetForTesting()
        {
            storedGhostCameraCutoffKm = null;
            storedReadableSidecarMirrors = null;
            storedShowGhostsInTrackingStation = null;
            loaded = false;
        }

        /// <summary>
        /// Test-only: returns the current stored cutoff value (null if unset).
        /// </summary>
        internal static float? GetStoredGhostCameraCutoffKm() => storedGhostCameraCutoffKm;

        internal static bool? GetStoredReadableSidecarMirrors() => storedReadableSidecarMirrors;

        internal static bool? GetStoredShowGhostsInTrackingStation() => storedShowGhostsInTrackingStation;

        /// <summary>
        /// Test-only: directly sets the stored cutoff without disk I/O.
        /// Marks the store as loaded so LoadIfNeeded doesn't clobber it.
        /// </summary>
        internal static void SetStoredGhostCameraCutoffKmForTesting(float? value)
        {
            storedGhostCameraCutoffKm = value;
            loaded = true;
        }

        internal static void SetStoredReadableSidecarMirrorsForTesting(bool? value)
        {
            storedReadableSidecarMirrors = value;
            loaded = true;
        }

        internal static void SetStoredShowGhostsInTrackingStationForTesting(bool? value)
        {
            storedShowGhostsInTrackingStation = value;
            loaded = true;
        }
    }
}
