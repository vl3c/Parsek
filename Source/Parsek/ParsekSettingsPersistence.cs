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
        private const string ShowRouteLinesKey = "showRouteLines";
        private const string AutoBackupExistingSavesKey = "autoBackupExistingSaves";
        private const string GhostRenderTracingKey = "ghostRenderTracing";
        private const string MapRenderTracingKey = "mapRenderTracing";
        private const string LedgerTracingKey = "ledgerTracing";
        private const string UiComplexityModeKey = "uiComplexityMode";
        private const string WarpYearKey = "warpYear";
        private const string WarpDayKey = "warpDay";
        private const string WarpHourKey = "warpHour";
        private const string WarpMinuteKey = "warpMinute";

        // Null = no stored value (use defaults / whatever GameParameters loaded).
        // Non-null = user-set override, applied over GameParameters on load.
        private static bool? storedReadableSidecarMirrors;
        private static bool? storedShowCommittedFutureOverlays;
        private static bool? storedBlockCommittedActions;
        private static bool? storedShowRouteLines;
        private static bool? storedAutoBackupExistingSaves;
        private static bool? storedGhostRenderTracing;
        private static bool? storedMapRenderTracing;
        private static bool? storedLedgerTracing;
        // Basic / Advanced UI complexity mode (design 6.2). Persisted as an int
        // (0=Basic, 1=Advanced); null = never resolved on this install, which is the
        // ONLY state that lets the first-run footprint resolution of design 7.3 run.
        private static int? storedUiComplexityMode;
        // Warp-to-time draft inputs (Timeline window). Pure UI state persisted across
        // sessions so the user need not re-type a frequently-used target date.
        private static int? storedWarpYear;
        private static int? storedWarpDay;
        private static int? storedWarpHour;
        private static int? storedWarpMinute;
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
                if (!string.IsNullOrEmpty(cutoffStr))
                {
                    ParsekLog.Verbose(Tag,
                        $"Settings file '{path}' contains deprecated {GhostCameraCutoffKey}=" +
                        $"'{cutoffStr}' — ignoring; watch cutoff is fixed at " +
                        $"{DistanceThresholds.GhostFlight.DefaultWatchCameraCutoffKm.ToString("F0", CultureInfo.InvariantCulture)}km");
                }

                TryLoadBool(root, path, ReadableSidecarMirrorsKey, ref storedReadableSidecarMirrors);
                TryLoadBool(root, path, ShowCommittedFutureOverlaysKey, ref storedShowCommittedFutureOverlays);
                TryLoadBool(root, path, BlockCommittedActionsKey, ref storedBlockCommittedActions);
                TryLoadBool(root, path, ShowRouteLinesKey, ref storedShowRouteLines);
                TryLoadBool(root, path, AutoBackupExistingSavesKey, ref storedAutoBackupExistingSaves);
                TryLoadBool(root, path, GhostRenderTracingKey, ref storedGhostRenderTracing);
                TryLoadBool(root, path, MapRenderTracingKey, ref storedMapRenderTracing);
                TryLoadBool(root, path, LedgerTracingKey, ref storedLedgerTracing);

                storedUiComplexityMode = ParseStoredInt(root, UiComplexityModeKey);

                storedWarpYear = ParseStoredInt(root, WarpYearKey);
                storedWarpDay = ParseStoredInt(root, WarpDayKey);
                storedWarpHour = ParseStoredInt(root, WarpHourKey);
                storedWarpMinute = ParseStoredInt(root, WarpMinuteKey);

                ParsekLog.Info(Tag,
                    $"Loaded settings from '{path}': writeReadableSidecarMirrors=" +
                    (storedReadableSidecarMirrors.HasValue ? storedReadableSidecarMirrors.Value.ToString() : "<default>") +
                    $" showCommittedFutureOverlays={(storedShowCommittedFutureOverlays.HasValue ? storedShowCommittedFutureOverlays.Value.ToString() : "<default>")}" +
                    $" blockCommittedActions={(storedBlockCommittedActions.HasValue ? storedBlockCommittedActions.Value.ToString() : "<default>")}" +
                    $" showRouteLines={(storedShowRouteLines.HasValue ? storedShowRouteLines.Value.ToString() : "<default>")}" +
                    $" autoBackupExistingSaves={(storedAutoBackupExistingSaves.HasValue ? storedAutoBackupExistingSaves.Value.ToString() : "<default>")}" +
                    $" ghostRenderTracing={(storedGhostRenderTracing.HasValue ? storedGhostRenderTracing.Value.ToString() : "<default>")}" +
                    $" mapRenderTracing={(storedMapRenderTracing.HasValue ? storedMapRenderTracing.Value.ToString() : "<default>")}" +
                    $" ledgerTracing={(storedLedgerTracing.HasValue ? storedLedgerTracing.Value.ToString() : "<default>")}" +
                    $" uiComplexityMode={(storedUiComplexityMode.HasValue ? storedUiComplexityMode.Value.ToString(CultureInfo.InvariantCulture) : "<default>")}");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"Failed to load settings file '{path}': {ex.Message}");
            }
        }

        /// <summary>
        /// Reads <paramref name="key"/> from <paramref name="root"/> as a bool override:
        /// stores it when present and parseable, otherwise verbose-logs the using-default
        /// case. Folds the six byte-identical per-key read blocks in <see cref="LoadIfNeeded"/>.
        /// </summary>
        private static void TryLoadBool(ConfigNode root, string path, string key, ref bool? stored)
        {
            string raw = root.GetValue(key);
            if (!string.IsNullOrEmpty(raw)
                && bool.TryParse(raw, out bool parsed))
            {
                stored = parsed;
            }
            else
            {
                ParsekLog.Verbose(Tag, $"Settings file '{path}' has no {key} — using default");
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
        /// <param name="scenarioNodePopulated">
        /// Footprint signal 2 of design 7.3: true when the save's
        /// <c>SCENARIO{name=ParsekScenario}</c> node carries real Parsek data (not the
        /// empty name+scene node KSP injects on first AddToAllGames contact). Passed as
        /// a BARE BOOL by <c>ParsekScenario.OnLoad</c>, which is the only caller with
        /// the node in hand: the whole mode vocabulary stays inside the UI / settings
        /// layer so the phase-8 grep gate can keep `ParsekScenario.cs` un-allowlisted.
        /// Defaults to false so unit tests and any future caller without a node opt out
        /// of this signal rather than fabricating one.
        /// </param>
        internal static void ApplyTo(ParsekSettings settings, bool scenarioNodePopulated = false)
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

            if (storedShowRouteLines.HasValue
                && storedShowRouteLines.Value != settings.showRouteLines)
            {
                bool prev = settings.showRouteLines;
                settings.showRouteLines = storedShowRouteLines.Value;
                ParsekLog.Info(Tag,
                    $"Restored showRouteLines {prev} -> {storedShowRouteLines.Value} from persistent store");
            }

            if (storedAutoBackupExistingSaves.HasValue
                && storedAutoBackupExistingSaves.Value != settings.autoBackupExistingSaves)
            {
                bool prev = settings.autoBackupExistingSaves;
                settings.autoBackupExistingSaves = storedAutoBackupExistingSaves.Value;
                ParsekLog.Info(Tag,
                    $"Restored autoBackupExistingSaves {prev} -> {storedAutoBackupExistingSaves.Value} from persistent store");
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

            ApplyUiComplexityMode(settings, scenarioNodePopulated);
        }

        /// <summary>
        /// Restores the persisted UI complexity mode, or resolves it once per install
        /// when no key is stored yet (design 7.3).
        ///
        /// <para>Ordering is the whole point: the STORED branch is checked first and
        /// returns, so an absent key is the only path that can ever reach footprint
        /// resolution. Both branches converge on
        /// <see cref="UiSurfaceVisibility.ResolveMode"/>, which takes the stored value as
        /// an input, so the stored-wins precedence cannot be inverted anywhere else.</para>
        ///
        /// <para>Resolution PERSISTS immediately. Without that, resolution would re-run
        /// each session, and by session 2 even a brand-new install has a footprint (its
        /// own `Parsek/` sidecar dir and populated scenario node), silently flipping a
        /// new player from Basic to Advanced. Persist-on-resolve makes this run at most
        /// once per install, ever, and makes "stored value present" the steady state -
        /// which is also why no cold-load gate is needed here.</para>
        /// </summary>
        private static void ApplyUiComplexityMode(ParsekSettings settings, bool scenarioNodePopulated)
        {
            if (storedUiComplexityMode.HasValue)
            {
                if (storedUiComplexityMode.Value != settings.uiComplexityMode)
                {
                    int prev = settings.uiComplexityMode;
                    settings.uiComplexityMode = storedUiComplexityMode.Value;
                    ParsekLog.Info(Tag,
                        $"Restored uiComplexityMode {prev} -> {storedUiComplexityMode.Value} from persistent store");
                }
                return;
            }

            // Signal 3 must be read BEFORE the persist below, or the key this very call
            // writes would make the store look pre-populated on its own resolution.
            bool storedSettingsKeys = HasAnyStoredValue();
            bool savesParsekDir = SavesRootHasParsekDirectory(SafeSavesRootPath());
            bool footprint = savesParsekDir || scenarioNodePopulated || storedSettingsKeys;

            UiComplexityMode resolved = UiSurfaceVisibility.ResolveMode(null, footprint);
            settings.UiComplexityModeLevel = resolved;
            RecordUiComplexityMode((int)resolved);

            // Tagged [UI] rather than [SettingsStore]: design 12.1 gives the [UI] tag
            // ownership of mode events, and this is the mode's birth line.
            ParsekLog.Info("UI",
                $"First-run default resolved: uiComplexityMode={resolved} " +
                $"installHasParsekFootprint={footprint} " +
                $"(savesParsekDir={savesParsekDir} scenarioNodePopulated={scenarioNodePopulated} " +
                $"storedSettingsKeys={storedSettingsKeys}) persisted=true");
        }

        /// <summary>
        /// Footprint signal 1 of design 7.3: true when ANY save folder directly under
        /// <paramref name="savesRootPath"/> carries a <c>Parsek/</c> subdirectory. Cheap
        /// <see cref="Directory.Exists"/> walk, no <c>.sfs</c> parsing.
        ///
        /// <para>Takes the saves root as a PARAMETER (rather than reading
        /// <c>KSPUtil.ApplicationRootPath</c> itself) so xUnit can drive it with a temp
        /// directory; <see cref="SafeSavesRootPath"/> supplies the live value.</para>
        ///
        /// <para>An unreadable root returns false (no footprint). That biases toward
        /// Basic, but only when signals 2 and 3 are ALSO absent - and any install that
        /// has really run Parsek before will have a populated scenario node or a stored
        /// settings key, both of which are read without touching the filesystem.</para>
        /// </summary>
        internal static bool SavesRootHasParsekDirectory(string savesRootPath)
        {
            if (string.IsNullOrEmpty(savesRootPath)) return false;
            try
            {
                if (!Directory.Exists(savesRootPath)) return false;
                string[] saveDirs = Directory.GetDirectories(savesRootPath);
                for (int i = 0; i < saveDirs.Length; i++)
                {
                    if (Directory.Exists(Path.Combine(saveDirs[i], "Parsek")))
                        return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"SavesRootHasParsekDirectory('{savesRootPath}') failed " +
                    $"({ex.GetType().Name}: {ex.Message}) — treating as no saves footprint");
                return false;
            }
        }

        /// <summary>
        /// The live saves root, or null when it cannot be resolved (xUnit / headless,
        /// where <c>KSPUtil.ApplicationRootPath</c> throws). Null makes signal 1 sit out.
        /// </summary>
        private static string SafeSavesRootPath()
        {
            try
            {
                string root = KSPUtil.ApplicationRootPath;
                return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "saves");
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"Saves root unavailable ({ex.GetType().Name}: {ex.Message}) — " +
                    "install saves-footprint signal skipped");
                return null;
            }
        }

        /// <summary>
        /// Footprint signal 3 of design 7.3: true when the store holds at least one
        /// stored key, i.e. Parsek ran on this install before (the strongest cheap
        /// signal, and the only one that needs no filesystem or save access).
        /// </summary>
        internal static bool HasAnyStoredValue()
        {
            return storedReadableSidecarMirrors.HasValue
                || storedShowCommittedFutureOverlays.HasValue
                || storedBlockCommittedActions.HasValue
                || storedShowRouteLines.HasValue
                || storedAutoBackupExistingSaves.HasValue
                || storedGhostRenderTracing.HasValue
                || storedMapRenderTracing.HasValue
                || storedLedgerTracing.HasValue
                || storedUiComplexityMode.HasValue
                || storedWarpYear.HasValue
                || storedWarpDay.HasValue
                || storedWarpHour.HasValue
                || storedWarpMinute.HasValue;
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

        internal static void RecordShowRouteLines(bool value)
        {
            LoadIfNeeded();
            storedShowRouteLines = value;
            Save();
        }

        /// <summary>
        /// Records the UI complexity mode (0=Basic, 1=Advanced) and writes it to disk
        /// immediately, mirroring <see cref="RecordShowRouteLines"/>. Two callers only:
        /// the single setter seam <c>ParsekUI.SetUiComplexityMode</c> (user toggle), and
        /// the persist-on-resolve step of <see cref="ApplyUiComplexityMode"/>.
        ///
        /// <para>Unlike <see cref="RecordShowRouteLines"/> this guards the xUnit /
        /// non-Unity context where <see cref="KSPUtil.ApplicationRootPath"/> throws
        /// (the <see cref="RecordWarpDate"/> shape), because the first-run resolution
        /// calls it from inside <see cref="ApplyTo"/>, which unit tests drive directly.
        /// The in-memory store is still updated in that case.</para>
        /// </summary>
        internal static void RecordUiComplexityMode(int value)
        {
            try { LoadIfNeeded(); }
            catch (SecurityException ex)
            {
                ParsekLog.Verbose(Tag,
                    $"RecordUiComplexityMode: LoadIfNeeded threw SecurityException " +
                    $"(likely xUnit / non-Unity context: {ex.Message}) — using in-memory fallback");
            }
            storedUiComplexityMode = value;
            try { Save(); }
            catch (SecurityException ex)
            {
                ParsekLog.Verbose(Tag,
                    $"RecordUiComplexityMode: Save threw SecurityException " +
                    $"(likely xUnit / non-Unity context: {ex.Message}) — store is in-memory only");
            }
        }

        internal static void RecordAutoBackupExistingSaves(bool value)
        {
            LoadIfNeeded();
            storedAutoBackupExistingSaves = value;
            Save();
        }

        internal static void RecordGhostRenderTracing(bool value)
            => RecordTracingFlag(ref storedGhostRenderTracing, value, "RecordGhostRenderTracing");

        internal static void RecordMapRenderTracing(bool value)
            => RecordTracingFlag(ref storedMapRenderTracing, value, "RecordMapRenderTracing");

        internal static void RecordLedgerTracing(bool value)
            => RecordTracingFlag(ref storedLedgerTracing, value, "RecordLedgerTracing");

        /// <summary>
        /// Records a tracing-flag override and writes it to disk, guarding the xUnit /
        /// non-Unity context where <see cref="KSPUtil.ApplicationRootPath"/> throws.
        /// Folds the three byte-identical RecordGhost/Map/LedgerTracing methods, which
        /// differed only by the backing field (<paramref name="stored"/>) and the
        /// method-name prefix in the SecurityException verbose logs (<paramref name="name"/>).
        /// </summary>
        private static void RecordTracingFlag(ref bool? stored, bool value, string name)
        {
            try { LoadIfNeeded(); }
            catch (SecurityException ex)
            {
                ParsekLog.Verbose(Tag,
                    $"{name}: LoadIfNeeded threw SecurityException " +
                    $"(likely xUnit / non-Unity context: {ex.Message}) — using in-memory fallback");
            }
            if (stored.HasValue && stored.Value == value) return;
            stored = value;
            try { Save(); }
            catch (SecurityException ex)
            {
                ParsekLog.Verbose(Tag,
                    $"{name}: Save threw SecurityException " +
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
                if (storedShowRouteLines.HasValue)
                    root.AddValue(ShowRouteLinesKey, storedShowRouteLines.Value.ToString());
                if (storedAutoBackupExistingSaves.HasValue)
                    root.AddValue(AutoBackupExistingSavesKey, storedAutoBackupExistingSaves.Value.ToString());
                if (storedGhostRenderTracing.HasValue)
                    root.AddValue(GhostRenderTracingKey, storedGhostRenderTracing.Value.ToString());
                if (storedMapRenderTracing.HasValue)
                    root.AddValue(MapRenderTracingKey, storedMapRenderTracing.Value.ToString());
                if (storedLedgerTracing.HasValue)
                    root.AddValue(LedgerTracingKey, storedLedgerTracing.Value.ToString());
                if (storedUiComplexityMode.HasValue)
                    root.AddValue(UiComplexityModeKey, storedUiComplexityMode.Value.ToString(CultureInfo.InvariantCulture));
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
                    $" showRouteLines={(storedShowRouteLines.HasValue ? storedShowRouteLines.Value.ToString() : "<null>")}" +
                    $" autoBackupExistingSaves={(storedAutoBackupExistingSaves.HasValue ? storedAutoBackupExistingSaves.Value.ToString() : "<null>")}" +
                    $" ghostRenderTracing={(storedGhostRenderTracing.HasValue ? storedGhostRenderTracing.Value.ToString() : "<null>")}" +
                    $" mapRenderTracing={(storedMapRenderTracing.HasValue ? storedMapRenderTracing.Value.ToString() : "<null>")}" +
                    $" ledgerTracing={(storedLedgerTracing.HasValue ? storedLedgerTracing.Value.ToString() : "<null>")}" +
                    $" uiComplexityMode={(storedUiComplexityMode.HasValue ? storedUiComplexityMode.Value.ToString(CultureInfo.InvariantCulture) : "<null>")}");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"Failed to save settings file '{path}': {ex.Message}");
            }
        }

        /// <summary>
        /// Test-only: clears the static store so LoadIfNeeded re-reads the file.
        /// </summary>
        internal static void ResetForTesting()
        {
            storedReadableSidecarMirrors = null;
            storedShowCommittedFutureOverlays = null;
            storedBlockCommittedActions = null;
            storedShowRouteLines = null;
            storedAutoBackupExistingSaves = null;
            storedGhostRenderTracing = null;
            storedMapRenderTracing = null;
            storedLedgerTracing = null;
            storedUiComplexityMode = null;
            storedWarpYear = null;
            storedWarpDay = null;
            storedWarpHour = null;
            storedWarpMinute = null;
            loaded = false;
        }

        /// <summary>
        /// Test-only: returns the current stored readable-mirror value (null if unset).
        /// </summary>
        internal static bool? GetStoredReadableSidecarMirrors() => storedReadableSidecarMirrors;

        internal static bool? GetStoredShowCommittedFutureOverlays() => storedShowCommittedFutureOverlays;

        internal static bool? GetStoredBlockCommittedActions() => storedBlockCommittedActions;

        internal static bool? GetStoredShowRouteLines() => storedShowRouteLines;

        internal static bool? GetStoredAutoBackupExistingSaves() => storedAutoBackupExistingSaves;

        internal static bool? GetStoredGhostRenderTracing() => storedGhostRenderTracing;

        internal static bool? GetStoredMapRenderTracing() => storedMapRenderTracing;

        internal static bool? GetStoredLedgerTracing() => storedLedgerTracing;

        /// <summary>
        /// Test-only: the stored UI complexity mode as a raw int, null when the install
        /// has never resolved one. Null is the state that lets first-run resolution run.
        /// </summary>
        internal static int? GetStoredUiComplexityMode() => storedUiComplexityMode;

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

        internal static void SetStoredShowRouteLinesForTesting(bool? value)
        {
            storedShowRouteLines = value;
            loaded = true;
        }

        internal static void SetStoredAutoBackupExistingSavesForTesting(bool? value)
        {
            storedAutoBackupExistingSaves = value;
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

        /// <summary>
        /// Test-only: sets the stored UI complexity mode without disk I/O. Passing null
        /// marks the store loaded with NO mode key, which is exactly the state that lets
        /// the first-run footprint resolution run.
        /// </summary>
        internal static void SetStoredUiComplexityModeForTesting(int? value)
        {
            storedUiComplexityMode = value;
            loaded = true;
        }
    }
}
