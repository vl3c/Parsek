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
    /// Tracks user-intent settings that must survive rewind, quickload,
    /// KSP's save/scene GameParameters reloads, and session restart. Add
    /// more fields here if additional settings need the same survival
    /// semantics.
    /// </para>
    /// </summary>
    internal static class ParsekSettingsPersistence
    {
        private const string Tag = "SettingsStore";
        private const string FileName = "settings.cfg";
        private const string RootNodeName = "PARSEK_SETTINGS";
        private const string GhostCameraCutoffKey = "ghostCameraCutoffKm";
        private const string ReadableSidecarMirrorsKey = "writeReadableSidecarMirrors";
        private const string ShowCommittedFutureOverlaysKey = "showCommittedFutureOverlays";
        private const string BlockCommittedActionsKey = "blockCommittedActions";
        private const string GhostRenderTracingKey = "ghostRenderTracing";
        private const string MapRenderTracingKey = "mapRenderTracing";
        private const string LedgerTracingKey = "ledgerTracing";
        private const string UseSmoothingSplinesKey = "useSmoothingSplines";
        private const string UseAnchorCorrectionKey = "useAnchorCorrection";
        private const string UseAnchorTaxonomyKey = "useAnchorTaxonomy";
        private const string UseOutlierRejectionKey = "useOutlierRejection";
        private const string WarpYearKey = "warpYear";
        private const string WarpDayKey = "warpDay";
        private const string WarpHourKey = "warpHour";
        private const string WarpMinuteKey = "warpMinute";

        // Null = no stored value (use defaults / whatever GameParameters loaded).
        // Non-null = user-set override, applied over GameParameters on load.
        private static bool? storedReadableSidecarMirrors;
        private static bool? storedShowCommittedFutureOverlays;
        private static bool? storedBlockCommittedActions;
        private static bool? storedGhostRenderTracing;
        private static bool? storedMapRenderTracing;
        private static bool? storedLedgerTracing;
        private static bool? storedUseSmoothingSplines;
        private static bool? storedUseAnchorCorrection;
        private static bool? storedUseAnchorTaxonomy;
        private static bool? storedUseOutlierRejection;
        // Warp-to-time draft inputs (Timeline window). Pure UI state persisted across
        // sessions so the user need not re-type a frequently-used target date.
        private static int? storedWarpYear;
        private static int? storedWarpDay;
        private static int? storedWarpHour;
        private static int? storedWarpMinute;
        private static bool loaded;

        /// <summary>
        /// True once <see cref="ApplyTo"/> has reconciled the store into a live
        /// <see cref="ParsekSettings"/> instance. Until then, <c>ParsekSettings.Current</c>
        /// may still hold whatever KSP restored from the .sfs (or the compiled
        /// default for a fresh game) - trusting it would let a stale save value
        /// overwrite a correct stored preference. Reset only via
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

                string showOverlaysStr = root.GetValue(ShowCommittedFutureOverlaysKey);
                if (!string.IsNullOrEmpty(showOverlaysStr)
                    && bool.TryParse(showOverlaysStr, out bool showOverlays))
                {
                    storedShowCommittedFutureOverlays = showOverlays;
                }
                else
                {
                    ParsekLog.Verbose(Tag, $"Settings file '{path}' has no {ShowCommittedFutureOverlaysKey} — using default");
                }

                string blockActionsStr = root.GetValue(BlockCommittedActionsKey);
                if (!string.IsNullOrEmpty(blockActionsStr)
                    && bool.TryParse(blockActionsStr, out bool blockActions))
                {
                    storedBlockCommittedActions = blockActions;
                }
                else
                {
                    ParsekLog.Verbose(Tag, $"Settings file '{path}' has no {BlockCommittedActionsKey} — using default");
                }

                string ghostRenderTracingStr = root.GetValue(GhostRenderTracingKey);
                if (!string.IsNullOrEmpty(ghostRenderTracingStr)
                    && bool.TryParse(ghostRenderTracingStr, out bool ghostRenderTracing))
                {
                    storedGhostRenderTracing = ghostRenderTracing;
                }
                else
                {
                    ParsekLog.Verbose(Tag, $"Settings file '{path}' has no {GhostRenderTracingKey} — using default");
                }

                string mapRenderTracingStr = root.GetValue(MapRenderTracingKey);
                if (!string.IsNullOrEmpty(mapRenderTracingStr)
                    && bool.TryParse(mapRenderTracingStr, out bool mapRenderTracing))
                {
                    storedMapRenderTracing = mapRenderTracing;
                }
                else
                {
                    ParsekLog.Verbose(Tag, $"Settings file '{path}' has no {MapRenderTracingKey} — using default");
                }

                string ledgerTracingStr = root.GetValue(LedgerTracingKey);
                if (!string.IsNullOrEmpty(ledgerTracingStr)
                    && bool.TryParse(ledgerTracingStr, out bool ledgerTracing))
                {
                    storedLedgerTracing = ledgerTracing;
                }
                else
                {
                    ParsekLog.Verbose(Tag, $"Settings file '{path}' has no {LedgerTracingKey} — using default");
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

                string useTaxonomyStr = root.GetValue(UseAnchorTaxonomyKey);
                if (!string.IsNullOrEmpty(useTaxonomyStr)
                    && bool.TryParse(useTaxonomyStr, out bool useTaxonomy))
                {
                    storedUseAnchorTaxonomy = useTaxonomy;
                }
                else
                {
                    ParsekLog.Verbose(Tag, $"Settings file '{path}' has no {UseAnchorTaxonomyKey} — using default");
                }

                string useOutlierStr = root.GetValue(UseOutlierRejectionKey);
                if (!string.IsNullOrEmpty(useOutlierStr)
                    && bool.TryParse(useOutlierStr, out bool useOutlier))
                {
                    storedUseOutlierRejection = useOutlier;
                }
                else
                {
                    ParsekLog.Verbose(Tag, $"Settings file '{path}' has no {UseOutlierRejectionKey} — using default");
                }

                storedWarpYear = ParseStoredInt(root, WarpYearKey);
                storedWarpDay = ParseStoredInt(root, WarpDayKey);
                storedWarpHour = ParseStoredInt(root, WarpHourKey);
                storedWarpMinute = ParseStoredInt(root, WarpMinuteKey);

                ParsekLog.Info(Tag,
                    $"Loaded settings from '{path}': writeReadableSidecarMirrors=" +
                    (storedReadableSidecarMirrors.HasValue ? storedReadableSidecarMirrors.Value.ToString() : "<default>") +
                    $" showCommittedFutureOverlays={(storedShowCommittedFutureOverlays.HasValue ? storedShowCommittedFutureOverlays.Value.ToString() : "<default>")}" +
                    $" blockCommittedActions={(storedBlockCommittedActions.HasValue ? storedBlockCommittedActions.Value.ToString() : "<default>")}" +
                    $" ghostRenderTracing={(storedGhostRenderTracing.HasValue ? storedGhostRenderTracing.Value.ToString() : "<default>")}" +
                    $" mapRenderTracing={(storedMapRenderTracing.HasValue ? storedMapRenderTracing.Value.ToString() : "<default>")}" +
                    $" ledgerTracing={(storedLedgerTracing.HasValue ? storedLedgerTracing.Value.ToString() : "<default>")}" +
                    $" useSmoothingSplines={(storedUseSmoothingSplines.HasValue ? storedUseSmoothingSplines.Value.ToString() : "<default>")}" +
                    $" useAnchorCorrection={(storedUseAnchorCorrection.HasValue ? storedUseAnchorCorrection.Value.ToString() : "<default>")}" +
                    $" useAnchorTaxonomy={(storedUseAnchorTaxonomy.HasValue ? storedUseAnchorTaxonomy.Value.ToString() : "<default>")}" +
                    $" useOutlierRejection={(storedUseOutlierRejection.HasValue ? storedUseOutlierRejection.Value.ToString() : "<default>")}");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"Failed to load settings file '{path}': {ex.Message}");
            }
        }

        private static int? ParseStoredInt(ConfigNode root, string key)
        {
            string raw = root.GetValue(key);
            if (!string.IsNullOrEmpty(raw)
                && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                return v;
            return null;
        }

        /// <summary>
        /// Records the Timeline warp-to-time draft inputs and writes them to disk. Called
        /// when the Timeline window closes. Guards against the xUnit / non-Unity context
        /// where KSPUtil.ApplicationRootPath throws (the in-memory store is still updated).
        /// </summary>
        internal static void RecordWarpDate(int year, int day, int hour, int minute)
        {
            try { LoadIfNeeded(); }
            catch (SecurityException ex)
            {
                ParsekLog.Verbose(Tag,
                    $"RecordWarpDate: LoadIfNeeded threw SecurityException " +
                    $"(likely xUnit / non-Unity context: {ex.Message}) — using in-memory fallback");
            }
            storedWarpYear = year;
            storedWarpDay = day;
            storedWarpHour = hour;
            storedWarpMinute = minute;
            try { Save(); }
            catch (SecurityException ex)
            {
                ParsekLog.Verbose(Tag,
                    $"RecordWarpDate: Save threw SecurityException " +
                    $"(likely xUnit / non-Unity context: {ex.Message}) — store is in-memory only");
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

            if (storedShowCommittedFutureOverlays.HasValue
                && storedShowCommittedFutureOverlays.Value != settings.showCommittedFutureOverlays)
            {
                bool prev = settings.showCommittedFutureOverlays;
                settings.showCommittedFutureOverlays = storedShowCommittedFutureOverlays.Value;
                ParsekLog.Info(Tag,
                    $"Restored showCommittedFutureOverlays {prev} -> {storedShowCommittedFutureOverlays.Value} from persistent store");
            }

            if (storedBlockCommittedActions.HasValue
                && storedBlockCommittedActions.Value != settings.blockCommittedActions)
            {
                bool prev = settings.blockCommittedActions;
                settings.blockCommittedActions = storedBlockCommittedActions.Value;
                ParsekLog.Info(Tag,
                    $"Restored blockCommittedActions {prev} -> {storedBlockCommittedActions.Value} from persistent store");
            }

            if (storedGhostRenderTracing.HasValue
                && storedGhostRenderTracing.Value != settings.ghostRenderTracing)
            {
                bool prev = settings.ghostRenderTracing;
                settings.ghostRenderTracing = storedGhostRenderTracing.Value;
                ParsekLog.Info(Tag,
                    $"Restored ghostRenderTracing {prev} -> {storedGhostRenderTracing.Value} from persistent store");
            }

            if (storedMapRenderTracing.HasValue
                && storedMapRenderTracing.Value != settings.mapRenderTracing)
            {
                bool prev = settings.mapRenderTracing;
                settings.mapRenderTracing = storedMapRenderTracing.Value;
                ParsekLog.Info(Tag,
                    $"Restored mapRenderTracing {prev} -> {storedMapRenderTracing.Value} from persistent store");
            }

            if (storedLedgerTracing.HasValue
                && storedLedgerTracing.Value != settings.ledgerTracing)
            {
                bool prev = settings.ledgerTracing;
                settings.ledgerTracing = storedLedgerTracing.Value;
                ParsekLog.Info(Tag,
                    $"Restored ledgerTracing {prev} -> {storedLedgerTracing.Value} from persistent store");
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

            if (storedUseAnchorTaxonomy.HasValue
                && storedUseAnchorTaxonomy.Value != settings.useAnchorTaxonomy)
            {
                bool prev = settings.useAnchorTaxonomy;
                // Property setter emits Notify on change.
                settings.useAnchorTaxonomy = storedUseAnchorTaxonomy.Value;
                ParsekLog.Info(Tag,
                    $"Restored useAnchorTaxonomy {prev} -> {storedUseAnchorTaxonomy.Value} from persistent store");
            }

            if (storedUseOutlierRejection.HasValue
                && storedUseOutlierRejection.Value != settings.useOutlierRejection)
            {
                bool prev = settings.useOutlierRejection;
                // Property setter emits Notify on change.
                settings.useOutlierRejection = storedUseOutlierRejection.Value;
                ParsekLog.Info(Tag,
                    $"Restored useOutlierRejection {prev} -> {storedUseOutlierRejection.Value} from persistent store");
            }

            // PR #328 P2-A: mark reconciled AFTER writes complete. Only now is
            // ParsekSettings.Current authoritative enough for the persisting
            // property setters to trust it. Before this flag flips, any early
            // call must treat the store as the source of truth.
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

        internal static void RecordShowCommittedFutureOverlays(bool value)
        {
            LoadIfNeeded();
            storedShowCommittedFutureOverlays = value;
            Save();
        }

        internal static void RecordBlockCommittedActions(bool value)
        {
            LoadIfNeeded();
            storedBlockCommittedActions = value;
            Save();
        }

        internal static void RecordGhostRenderTracing(bool value)
        {
            try { LoadIfNeeded(); }
            catch (SecurityException ex)
            {
                ParsekLog.Verbose(Tag,
                    $"RecordGhostRenderTracing: LoadIfNeeded threw SecurityException " +
                    $"(likely xUnit / non-Unity context: {ex.Message}) — using in-memory fallback");
            }
            if (storedGhostRenderTracing.HasValue && storedGhostRenderTracing.Value == value) return;
            storedGhostRenderTracing = value;
            try { Save(); }
            catch (SecurityException ex)
            {
                ParsekLog.Verbose(Tag,
                    $"RecordGhostRenderTracing: Save threw SecurityException " +
                    $"(likely xUnit / non-Unity context: {ex.Message}) — store is in-memory only");
            }
        }

        internal static void RecordMapRenderTracing(bool value)
        {
            try { LoadIfNeeded(); }
            catch (SecurityException ex)
            {
                ParsekLog.Verbose(Tag,
                    $"RecordMapRenderTracing: LoadIfNeeded threw SecurityException " +
                    $"(likely xUnit / non-Unity context: {ex.Message}) — using in-memory fallback");
            }
            if (storedMapRenderTracing.HasValue && storedMapRenderTracing.Value == value) return;
            storedMapRenderTracing = value;
            try { Save(); }
            catch (SecurityException ex)
            {
                ParsekLog.Verbose(Tag,
                    $"RecordMapRenderTracing: Save threw SecurityException " +
                    $"(likely xUnit / non-Unity context: {ex.Message}) — store is in-memory only");
            }
        }

        internal static void RecordLedgerTracing(bool value)
        {
            try { LoadIfNeeded(); }
            catch (SecurityException ex)
            {
                ParsekLog.Verbose(Tag,
                    $"RecordLedgerTracing: LoadIfNeeded threw SecurityException " +
                    $"(likely xUnit / non-Unity context: {ex.Message}) — using in-memory fallback");
            }
            if (storedLedgerTracing.HasValue && storedLedgerTracing.Value == value) return;
            storedLedgerTracing = value;
            try { Save(); }
            catch (SecurityException ex)
            {
                ParsekLog.Verbose(Tag,
                    $"RecordLedgerTracing: Save threw SecurityException " +
                    $"(likely xUnit / non-Unity context: {ex.Message}) — store is in-memory only");
            }
        }

        internal static void RecordUseSmoothingSplines(bool value)
        {
            // SecurityException guard: under xUnit, KSPUtil.ApplicationRootPath
            // throws SecurityException.
            // The in-memory store is still updated below — that's what the tests
            // (and the in-process value precedence) actually depend on.
            try { LoadIfNeeded(); }
            catch (SecurityException ex)
            {
                ParsekLog.Verbose(Tag,
                    $"RecordUseSmoothingSplines: LoadIfNeeded threw SecurityException " +
                    $"(likely xUnit / non-Unity context: {ex.Message}) — using in-memory fallback");
            }
            // Idempotent: if the persistent store already has this value,
            // skip Save() to avoid disk I/O on the restore-then-apply
            // round-trip (the property setter calls Record on every real
            // change, including the one that ApplyTo triggers when it
            // restores the stored value into the live ParsekSettings).
            if (storedUseSmoothingSplines.HasValue && storedUseSmoothingSplines.Value == value) return;
            storedUseSmoothingSplines = value;
            try { Save(); }
            catch (SecurityException ex)
            {
                ParsekLog.Verbose(Tag,
                    $"RecordUseSmoothingSplines: Save threw SecurityException " +
                    $"(likely xUnit / non-Unity context: {ex.Message}) — store is in-memory only");
            }
        }

        internal static void RecordUseAnchorCorrection(bool value)
        {
            try { LoadIfNeeded(); }
            catch (SecurityException ex)
            {
                ParsekLog.Verbose(Tag,
                    $"RecordUseAnchorCorrection: LoadIfNeeded threw SecurityException " +
                    $"(likely xUnit / non-Unity context: {ex.Message}) — using in-memory fallback");
            }
            if (storedUseAnchorCorrection.HasValue && storedUseAnchorCorrection.Value == value) return;
            storedUseAnchorCorrection = value;
            try { Save(); }
            catch (SecurityException ex)
            {
                ParsekLog.Verbose(Tag,
                    $"RecordUseAnchorCorrection: Save threw SecurityException " +
                    $"(likely xUnit / non-Unity context: {ex.Message}) — store is in-memory only");
            }
        }

        internal static void RecordUseAnchorTaxonomy(bool value)
        {
            try { LoadIfNeeded(); }
            catch (SecurityException ex)
            {
                ParsekLog.Verbose(Tag,
                    $"RecordUseAnchorTaxonomy: LoadIfNeeded threw SecurityException " +
                    $"(likely xUnit / non-Unity context: {ex.Message}) — using in-memory fallback");
            }
            if (storedUseAnchorTaxonomy.HasValue && storedUseAnchorTaxonomy.Value == value) return;
            storedUseAnchorTaxonomy = value;
            try { Save(); }
            catch (SecurityException ex)
            {
                ParsekLog.Verbose(Tag,
                    $"RecordUseAnchorTaxonomy: Save threw SecurityException " +
                    $"(likely xUnit / non-Unity context: {ex.Message}) — store is in-memory only");
            }
        }

        internal static void RecordUseOutlierRejection(bool value)
        {
            try { LoadIfNeeded(); }
            catch (SecurityException ex)
            {
                ParsekLog.Verbose(Tag,
                    $"RecordUseOutlierRejection: LoadIfNeeded threw SecurityException " +
                    $"(likely xUnit / non-Unity context: {ex.Message}) — using in-memory fallback");
            }
            if (storedUseOutlierRejection.HasValue && storedUseOutlierRejection.Value == value) return;
            storedUseOutlierRejection = value;
            try { Save(); }
            catch (SecurityException ex)
            {
                ParsekLog.Verbose(Tag,
                    $"RecordUseOutlierRejection: Save threw SecurityException " +
                    $"(likely xUnit / non-Unity context: {ex.Message}) — store is in-memory only");
            }
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
                if (storedShowCommittedFutureOverlays.HasValue)
                    root.AddValue(ShowCommittedFutureOverlaysKey, storedShowCommittedFutureOverlays.Value.ToString());
                if (storedBlockCommittedActions.HasValue)
                    root.AddValue(BlockCommittedActionsKey, storedBlockCommittedActions.Value.ToString());
                if (storedGhostRenderTracing.HasValue)
                    root.AddValue(GhostRenderTracingKey, storedGhostRenderTracing.Value.ToString());
                if (storedMapRenderTracing.HasValue)
                    root.AddValue(MapRenderTracingKey, storedMapRenderTracing.Value.ToString());
                if (storedLedgerTracing.HasValue)
                    root.AddValue(LedgerTracingKey, storedLedgerTracing.Value.ToString());
                if (storedUseSmoothingSplines.HasValue)
                    root.AddValue(UseSmoothingSplinesKey, storedUseSmoothingSplines.Value.ToString());
                if (storedUseAnchorCorrection.HasValue)
                    root.AddValue(UseAnchorCorrectionKey, storedUseAnchorCorrection.Value.ToString());
                if (storedUseAnchorTaxonomy.HasValue)
                    root.AddValue(UseAnchorTaxonomyKey, storedUseAnchorTaxonomy.Value.ToString());
                if (storedUseOutlierRejection.HasValue)
                    root.AddValue(UseOutlierRejectionKey, storedUseOutlierRejection.Value.ToString());
                if (storedWarpYear.HasValue)
                    root.AddValue(WarpYearKey, storedWarpYear.Value.ToString(CultureInfo.InvariantCulture));
                if (storedWarpDay.HasValue)
                    root.AddValue(WarpDayKey, storedWarpDay.Value.ToString(CultureInfo.InvariantCulture));
                if (storedWarpHour.HasValue)
                    root.AddValue(WarpHourKey, storedWarpHour.Value.ToString(CultureInfo.InvariantCulture));
                if (storedWarpMinute.HasValue)
                    root.AddValue(WarpMinuteKey, storedWarpMinute.Value.ToString(CultureInfo.InvariantCulture));
                FileIOUtils.SafeWriteConfigNode(root, path, Tag);
                ParsekLog.Verbose(Tag,
                    $"Saved settings to '{path}': writeReadableSidecarMirrors=" +
                    (storedReadableSidecarMirrors.HasValue ? storedReadableSidecarMirrors.Value.ToString() : "<null>") +
                    $" showCommittedFutureOverlays={(storedShowCommittedFutureOverlays.HasValue ? storedShowCommittedFutureOverlays.Value.ToString() : "<null>")}" +
                    $" blockCommittedActions={(storedBlockCommittedActions.HasValue ? storedBlockCommittedActions.Value.ToString() : "<null>")}" +
                    $" ghostRenderTracing={(storedGhostRenderTracing.HasValue ? storedGhostRenderTracing.Value.ToString() : "<null>")}" +
                    $" mapRenderTracing={(storedMapRenderTracing.HasValue ? storedMapRenderTracing.Value.ToString() : "<null>")}" +
                    $" ledgerTracing={(storedLedgerTracing.HasValue ? storedLedgerTracing.Value.ToString() : "<null>")}" +
                    $" useSmoothingSplines={(storedUseSmoothingSplines.HasValue ? storedUseSmoothingSplines.Value.ToString() : "<null>")}" +
                    $" useAnchorCorrection={(storedUseAnchorCorrection.HasValue ? storedUseAnchorCorrection.Value.ToString() : "<null>")}" +
                    $" useAnchorTaxonomy={(storedUseAnchorTaxonomy.HasValue ? storedUseAnchorTaxonomy.Value.ToString() : "<null>")}" +
                    $" useOutlierRejection={(storedUseOutlierRejection.HasValue ? storedUseOutlierRejection.Value.ToString() : "<null>")}");
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
            storedShowCommittedFutureOverlays = null;
            storedBlockCommittedActions = null;
            storedGhostRenderTracing = null;
            storedMapRenderTracing = null;
            storedLedgerTracing = null;
            storedUseSmoothingSplines = null;
            storedUseAnchorCorrection = null;
            storedUseAnchorTaxonomy = null;
            storedUseOutlierRejection = null;
            storedWarpYear = null;
            storedWarpDay = null;
            storedWarpHour = null;
            storedWarpMinute = null;
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
        /// True after <see cref="ApplyTo"/> has reconciled the persistent
        /// store with live <see cref="ParsekSettings"/>. Property setters
        /// that persist (useSmoothingSplines, useAnchorCorrection) check
        /// this before calling <c>Record*</c>, so an early KSP-load assign
        /// of a stale per-save value cannot clobber the user's persisted
        /// intent before <c>ApplyTo</c> has had a chance to restore it
        /// (PR #328 P2-A).
        ///
        /// <see cref="ParsekSettings.OnLoad"/> resets this flag to false
        /// BEFORE calling <c>base.OnLoad</c> so the per-load cycle starts
        /// fresh — the latch is not a one-way process-wide flip.
        /// </summary>
        internal static bool IsReconciled => reconciledWithLiveSettings;

        /// <summary>
        /// Reset the reconciliation latch to false. Called by
        /// <see cref="ParsekSettings.OnLoad"/> at the start of each KSP
        /// settings-load cycle so the property setters' persistence gate
        /// closes again before <c>base.OnLoad</c> deserializes the .sfs
        /// node. Otherwise a long-running KSP process would keep the
        /// latch true after the first <see cref="ApplyTo"/> and the
        /// second + subsequent loads would let stale .sfs values clobber
        /// the persistent store.
        /// </summary>
        internal static void InvalidateReconciliation()
        {
            reconciledWithLiveSettings = false;
        }

        /// <summary>
        /// Test-only: returns the current stored readable-mirror value (null if unset).
        /// </summary>
        internal static bool? GetStoredReadableSidecarMirrors() => storedReadableSidecarMirrors;

        internal static bool? GetStoredShowCommittedFutureOverlays() => storedShowCommittedFutureOverlays;

        internal static bool? GetStoredBlockCommittedActions() => storedBlockCommittedActions;

        internal static bool? GetStoredGhostRenderTracing() => storedGhostRenderTracing;

        internal static bool? GetStoredMapRenderTracing() => storedMapRenderTracing;

        internal static bool? GetStoredLedgerTracing() => storedLedgerTracing;

        internal static bool? GetStoredUseSmoothingSplines() => storedUseSmoothingSplines;

        internal static bool? GetStoredUseAnchorCorrection() => storedUseAnchorCorrection;

        internal static bool? GetStoredUseAnchorTaxonomy() => storedUseAnchorTaxonomy;

        internal static bool? GetStoredUseOutlierRejection() => storedUseOutlierRejection;

        internal static int? GetStoredWarpYear() => storedWarpYear;
        internal static int? GetStoredWarpDay() => storedWarpDay;
        internal static int? GetStoredWarpHour() => storedWarpHour;
        internal static int? GetStoredWarpMinute() => storedWarpMinute;

        /// <summary>Test-only: directly sets the stored warp inputs without disk I/O.</summary>
        internal static void SetStoredWarpDateForTesting(int? year, int? day, int? hour, int? minute)
        {
            storedWarpYear = year;
            storedWarpDay = day;
            storedWarpHour = hour;
            storedWarpMinute = minute;
            loaded = true;
        }

        /// <summary>
        /// Test-only: directly sets the stored readable-mirror value without disk I/O.
        /// Marks the store as loaded so LoadIfNeeded doesn't clobber it.
        /// </summary>
        internal static void SetStoredReadableSidecarMirrorsForTesting(bool? value)
        {
            storedReadableSidecarMirrors = value;
            loaded = true;
        }

        internal static void SetStoredShowCommittedFutureOverlaysForTesting(bool? value)
        {
            storedShowCommittedFutureOverlays = value;
            loaded = true;
        }

        internal static void SetStoredBlockCommittedActionsForTesting(bool? value)
        {
            storedBlockCommittedActions = value;
            loaded = true;
        }

        internal static void SetStoredGhostRenderTracingForTesting(bool? value)
        {
            storedGhostRenderTracing = value;
            loaded = true;
        }

        internal static void SetStoredMapRenderTracingForTesting(bool? value)
        {
            storedMapRenderTracing = value;
            loaded = true;
        }

        internal static void SetStoredLedgerTracingForTesting(bool? value)
        {
            storedLedgerTracing = value;
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

        internal static void SetStoredUseAnchorTaxonomyForTesting(bool? value)
        {
            storedUseAnchorTaxonomy = value;
            loaded = true;
        }

        internal static void SetStoredUseOutlierRejectionForTesting(bool? value)
        {
            storedUseOutlierRejection = value;
            loaded = true;
        }
    }
}
