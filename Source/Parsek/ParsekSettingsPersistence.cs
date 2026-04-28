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
    /// Currently tracks <c>writeReadableSidecarMirrors</c> and
    /// <c>showGhostsInTrackingStation</c> (#388). Add more fields here if
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
        private const string UseSmoothingSplinesKey = "useSmoothingSplines";
        private const string UseAnchorCorrectionKey = "useAnchorCorrection";

        // Null = no stored value (use defaults / whatever GameParameters loaded).
        // Non-null = user-set override, applied over GameParameters on load.
        private static bool? storedReadableSidecarMirrors;
        private static bool? storedShowGhostsInTrackingStation;
        private static bool? storedUseSmoothingSplines;
        private static bool? storedUseAnchorCorrection;
        private static bool loaded;

        /// <summary>
        /// True once <see cref="ApplyTo"/> has reconciled the store into a live
        /// <see cref="ParsekSettings"/> instance. Until then, <c>ParsekSettings.Current</c>
        /// may still hold whatever KSP restored from the .sfs (or the compiled
        /// default for a fresh game) — trusting it would let
        /// <see cref="EffectiveShowGhostsInTrackingStation"/> overwrite a correct
        /// stored preference with a stale save value. Reset only via
        /// <see cref="ResetForTesting"/>.
        /// </summary>
        private static bool reconciledWithLiveSettings;

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
                if (!string.IsNullOrEmpty(cutoffStr))
                {
                    ParsekLog.Verbose(Tag,
                        $"Settings file '{path}' contains deprecated {GhostCameraCutoffKey}=" +
                        $"'{cutoffStr}' — ignoring; watch cutoff is fixed at " +
                        $"{DistanceThresholds.GhostFlight.DefaultWatchCameraCutoffKm.ToString("F0", CultureInfo.InvariantCulture)}km");
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

                string useSplinesStr = root.GetValue(UseSmoothingSplinesKey);
                if (!string.IsNullOrEmpty(useSplinesStr)
                    && bool.TryParse(useSplinesStr, out bool useSplines))
                {
                    storedUseSmoothingSplines = useSplines;
                }
                else
                {
                    ParsekLog.Verbose(Tag, $"Settings file '{path}' has no {UseSmoothingSplinesKey} — using default");
                }

                string useAnchorStr = root.GetValue(UseAnchorCorrectionKey);
                if (!string.IsNullOrEmpty(useAnchorStr)
                    && bool.TryParse(useAnchorStr, out bool useAnchor))
                {
                    storedUseAnchorCorrection = useAnchor;
                }
                else
                {
                    ParsekLog.Verbose(Tag, $"Settings file '{path}' has no {UseAnchorCorrectionKey} — using default");
                }

                ParsekLog.Info(Tag,
                    $"Loaded settings from '{path}': writeReadableSidecarMirrors=" +
                    (storedReadableSidecarMirrors.HasValue ? storedReadableSidecarMirrors.Value.ToString() : "<default>") +
                    $" showGhostsInTrackingStation={(storedShowGhostsInTrackingStation.HasValue ? storedShowGhostsInTrackingStation.Value.ToString() : "<default>")}" +
                    $" useSmoothingSplines={(storedUseSmoothingSplines.HasValue ? storedUseSmoothingSplines.Value.ToString() : "<default>")}" +
                    $" useAnchorCorrection={(storedUseAnchorCorrection.HasValue ? storedUseAnchorCorrection.Value.ToString() : "<default>")}");
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

            if (storedUseSmoothingSplines.HasValue
                && storedUseSmoothingSplines.Value != settings.useSmoothingSplines)
            {
                bool prev = settings.useSmoothingSplines;
                // Property setter emits Notify on change; explicit Notify call
                // removed (would double-fire the Pipeline-Smoothing flip Info).
                settings.useSmoothingSplines = storedUseSmoothingSplines.Value;
                ParsekLog.Info(Tag,
                    $"Restored useSmoothingSplines {prev} -> {storedUseSmoothingSplines.Value} from persistent store");
            }

            if (storedUseAnchorCorrection.HasValue
                && storedUseAnchorCorrection.Value != settings.useAnchorCorrection)
            {
                bool prev = settings.useAnchorCorrection;
                // Property setter emits Notify on change.
                settings.useAnchorCorrection = storedUseAnchorCorrection.Value;
                ParsekLog.Info(Tag,
                    $"Restored useAnchorCorrection {prev} -> {storedUseAnchorCorrection.Value} from persistent store");
            }

            // #388 + PR #328 P2-A: mark reconciled AFTER writes complete. Only
            // now is ParsekSettings.Current authoritative enough for
            // EffectiveShowGhostsInTrackingStation to trust it and resync the
            // store from it. Before this flag flips, any early call must treat
            // the store as the source of truth.
            reconciledWithLiveSettings = true;
        }

        /// <summary>
        /// Records a user-intent setting change and writes it to disk immediately.
        /// Called from the settings UI commit path after the user confirms a new value.
        /// </summary>
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

        internal static void RecordUseSmoothingSplines(bool value)
        {
            LoadIfNeeded();
            storedUseSmoothingSplines = value;
            Save();
        }

        internal static void RecordUseAnchorCorrection(bool value)
        {
            LoadIfNeeded();
            storedUseAnchorCorrection = value;
            Save();
        }

        /// <summary>
        /// Resolve the effective <c>showGhostsInTrackingStation</c> value. Precedence:
        /// <list type="number">
        ///   <item>Live <c>ParsekSettings.Current</c> — but only AFTER
        ///     <see cref="ApplyTo"/> has reconciled the store into it
        ///     (<c>reconciledWithLiveSettings</c> flag). Until then, <c>Current</c>
        ///     may still hold the value KSP restored from the .sfs — trusting it
        ///     would overwrite the user's persisted preference with a stale save
        ///     value (PR #328 P2-A).</item>
        ///   <item>Persisted store (settings.cfg) — authoritative before
        ///     reconciliation AND the fallback for the early-scene-load window
        ///     where <c>ParsekSettings.Current</c> is null (see
        ///     <c>ParsekScenario.cs:546</c> comment).</item>
        ///   <item>Default <c>true</c> (pre-#388 behavior).</item>
        /// </list>
        /// Post-reconciliation, a live value that disagrees with the store is
        /// resynced back to disk so a flip from KSP's stock Game Parameters UI
        /// (which mutates the field directly, bypassing
        /// <see cref="RecordShowGhostsInTrackingStation"/>) survives cold-start.
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

            // Post-reconciliation: live Current wins. The stock Game Parameters UI
            // writes directly to this field (bypassing
            // RecordShowGhostsInTrackingStation), so reading from the store first
            // would mask that flip for the rest of the session. When live and
            // stored disagree, persist the live value so the next cold-start reads
            // the user's current intent.
            var current = ParsekSettings.Current;
            if (reconciledWithLiveSettings && current != null)
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

            // Pre-reconciliation (or Current unavailable): store is the source of
            // truth. This is the SpaceTracking.Awake pre-OnLoad window #388
            // originally targeted, plus the new P2-A fix — a non-null-but-stale
            // Current must not clobber the persisted preference before
            // ParsekScenario.OnLoad had a chance to reconcile.
            if (storedShowGhostsInTrackingStation.HasValue)
                return storedShowGhostsInTrackingStation.Value;

            // Neither reconciled Current nor stored value available — fall back
            // to pre-reconciliation Current if present (first-run, no settings.cfg
            // yet), else the compiled default.
            return current?.showGhostsInTrackingStation ?? true;
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
                if (storedReadableSidecarMirrors.HasValue)
                    root.AddValue(ReadableSidecarMirrorsKey, storedReadableSidecarMirrors.Value.ToString());
                if (storedShowGhostsInTrackingStation.HasValue)
                    root.AddValue(ShowGhostsInTrackingStationKey, storedShowGhostsInTrackingStation.Value.ToString());
                if (storedUseSmoothingSplines.HasValue)
                    root.AddValue(UseSmoothingSplinesKey, storedUseSmoothingSplines.Value.ToString());
                if (storedUseAnchorCorrection.HasValue)
                    root.AddValue(UseAnchorCorrectionKey, storedUseAnchorCorrection.Value.ToString());
                FileIOUtils.SafeWriteConfigNode(root, path, Tag);
                ParsekLog.Verbose(Tag,
                    $"Saved settings to '{path}': writeReadableSidecarMirrors=" +
                    (storedReadableSidecarMirrors.HasValue ? storedReadableSidecarMirrors.Value.ToString() : "<null>") +
                    $" showGhostsInTrackingStation={(storedShowGhostsInTrackingStation.HasValue ? storedShowGhostsInTrackingStation.Value.ToString() : "<null>")}" +
                    $" useSmoothingSplines={(storedUseSmoothingSplines.HasValue ? storedUseSmoothingSplines.Value.ToString() : "<null>")}" +
                    $" useAnchorCorrection={(storedUseAnchorCorrection.HasValue ? storedUseAnchorCorrection.Value.ToString() : "<null>")}");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"Failed to save settings file '{path}': {ex.Message}");
            }
        }

        /// <summary>
        /// Test-only: clears the static store so LoadIfNeeded re-reads the file.
        /// Also resets the reconciliation flag — tests that exercise the
        /// live-wins precedence must either call <see cref="ApplyTo"/> with a
        /// settings instance or <see cref="MarkReconciledForTesting"/> first.
        /// </summary>
        internal static void ResetForTesting()
        {
            storedReadableSidecarMirrors = null;
            storedShowGhostsInTrackingStation = null;
            storedUseSmoothingSplines = null;
            storedUseAnchorCorrection = null;
            loaded = false;
            reconciledWithLiveSettings = false;
        }

        /// <summary>
        /// Test-only: flip the reconciled-with-live-settings flag without
        /// needing to stand up a full <see cref="ParsekSettings"/> + call
        /// <see cref="ApplyTo"/>. Mirrors the effect
        /// <c>ParsekScenario.OnLoad → ApplyTo</c> has in production.
        /// </summary>
        internal static void MarkReconciledForTesting()
        {
            reconciledWithLiveSettings = true;
        }

        /// <summary>Test-only: current reconciliation-flag state.</summary>
        internal static bool IsReconciledForTesting => reconciledWithLiveSettings;

        /// <summary>
        /// Test-only: returns the current stored readable-mirror value (null if unset).
        /// </summary>
        internal static bool? GetStoredReadableSidecarMirrors() => storedReadableSidecarMirrors;

        internal static bool? GetStoredShowGhostsInTrackingStation() => storedShowGhostsInTrackingStation;

        internal static bool? GetStoredUseSmoothingSplines() => storedUseSmoothingSplines;

        internal static bool? GetStoredUseAnchorCorrection() => storedUseAnchorCorrection;

        /// <summary>
        /// Test-only: directly sets the stored readable-mirror value without disk I/O.
        /// Marks the store as loaded so LoadIfNeeded doesn't clobber it.
        /// </summary>
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

        internal static void SetStoredUseSmoothingSplinesForTesting(bool? value)
        {
            storedUseSmoothingSplines = value;
            loaded = true;
        }

        internal static void SetStoredUseAnchorCorrectionForTesting(bool? value)
        {
            storedUseAnchorCorrection = value;
            loaded = true;
        }
    }
}
