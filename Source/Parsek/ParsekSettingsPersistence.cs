using System;
using System.Globalization;
using System.IO;

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
    /// Currently only <c>ghostCameraCutoffKm</c> is tracked. Add more fields
    /// here if additional settings turn out to need the same survival
    /// semantics.
    /// </para>
    /// </summary>
    internal static class ParsekSettingsPersistence
    {
        private const string Tag = "SettingsStore";
        private const string FileName = "settings.cfg";
        private const string RootNodeName = "PARSEK_SETTINGS";
        private const string GhostCameraCutoffKey = "ghostCameraCutoffKm";

        // Null = no stored value (use defaults / whatever GameParameters loaded).
        // Non-null = user-set override, applied over GameParameters on load.
        private static float? storedGhostCameraCutoffKm;
        private static bool loaded;

        /// <summary>
        /// Resolves the settings file path under GameData/Parsek/.
        /// </summary>
        internal static string GetFilePath()
        {
            string root = KSPUtil.ApplicationRootPath ?? "";
            return Path.Combine(root, "GameData", "Parsek", FileName);
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
                    ParsekLog.Info(Tag,
                        $"Loaded settings from '{path}': ghostCameraCutoffKm={cutoff.ToString("F0", CultureInfo.InvariantCulture)}");
                }
                else
                {
                    ParsekLog.Verbose(Tag, $"Settings file '{path}' has no {GhostCameraCutoffKey} — using default");
                }
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
                FileIOUtils.SafeWriteConfigNode(root, path, Tag);
                ParsekLog.Verbose(Tag,
                    $"Saved settings to '{path}': ghostCameraCutoffKm=" +
                    (storedGhostCameraCutoffKm?.ToString("F0", CultureInfo.InvariantCulture) ?? "<null>"));
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
            loaded = false;
        }

        /// <summary>
        /// Test-only: returns the current stored cutoff value (null if unset).
        /// </summary>
        internal static float? GetStoredGhostCameraCutoffKm() => storedGhostCameraCutoffKm;

        /// <summary>
        /// Test-only: directly sets the stored cutoff without disk I/O.
        /// Marks the store as loaded so LoadIfNeeded doesn't clobber it.
        /// </summary>
        internal static void SetStoredGhostCameraCutoffKmForTesting(float? value)
        {
            storedGhostCameraCutoffKm = value;
            loaded = true;
        }
    }
}
