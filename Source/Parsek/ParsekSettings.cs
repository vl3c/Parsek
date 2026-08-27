using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Recorder sample density presets. Each level maps to a fixed set of
    /// adaptive-sampling thresholds (min/max interval, direction, speed).
    /// </summary>
    public enum SamplingDensity
    {
        Low = 0,
        Medium = 1,
        High = 2,
    }

    public class ParsekSettings : GameParameters.CustomParameterNode
    {
        private const string SamplingDensityKey = "samplingDensity";
        private const string LegacyMinSampleIntervalKey = "minSampleInterval";
        private const string LegacyMaxSampleIntervalKey = "maxSampleInterval";
        private const string LegacyVelocityDirThresholdKey = "velocityDirThreshold";
        private const string LegacySpeedChangeThresholdKey = "speedChangeThreshold";

        public override string Title => "Parsek";
        public override GameParameters.GameMode GameMode => GameParameters.GameMode.ANY;
        public override string Section => "Parsek";
        public override string DisplaySection => "Parsek";
        public override int SectionOrder => 1;
        public override bool HasPresets => false;

        // The three auto-record toggles are HIDDEN by design (2026-08-27 settings
        // simplification): recording everything is the mod's premise, so players
        // always run with all three ON. The fields survive (no CustomParameterUI,
        // not drawn in the Settings window) because harness scenarios pin them
        // per-run through the M-A2 command seam (SettingWhitelist), including
        // deliberate autoRecordOnLaunch=false fixture flights.
        public bool autoRecordOnLaunch = true;

        public bool autoRecordOnEva = true;

        public bool autoRecordOnFirstModificationAfterSwitch = true;

        /// <summary>
        /// Commit finished recordings to the timeline without the per-mission
        /// "Merge to Timeline / Discard" confirmation dialog.
        ///
        /// <para>Defaults <b>ON</b> since 0.10.4. The ON path used to be lossy
        /// (it committed ghost-only and dropped the terminal vessel snapshot, so a
        /// surviving vessel never re-materialised at its recording end), which is
        /// why the default was OFF; the silent full-fidelity auto-commit work
        /// (`docs/dev/plans/silent-full-fidelity-autocommit.md`) closed that gap for
        /// the path that matters, routing a <c>Finalized</c> pending tree through the
        /// dialog's own commit + per-leaf vessel decisions. Note the qualifier: a
        /// non-<c>Finalized</c> (Limbo) tree or a live re-fly marker still falls to
        /// <c>AutoCommitTreeGhostOnly</c> by design (plan §4.4 / §10), so "ON is no
        /// longer lossy" is a statement about the Finalized path, not a universal one.
        /// Re-Fly exits and MAINMENU still show their dialog regardless.</para>
        ///
        /// <para>HIDDEN by design (2026-08-27 settings simplification): players always
        /// run with auto-merge ON; the per-recording confirmation dialog is no longer
        /// player-selectable. The field survives because harness scenarios pin
        /// <c>autoMerge=true</c> through the M-A2 command seam as the exit verb's
        /// supported shape (and other lanes rely on setting it explicitly).</para>
        /// </summary>
        public bool autoMerge = true;

        [GameParameters.CustomParameterUI("Verbose logging",
            toolTip = "When enabled, write detailed diagnostics to KSP.log (default for development)")]
        public bool verboseLogging = true;

        [GameParameters.CustomParameterUI("Ghost render tracing (Warning: huge logs)",
            toolTip = "When enabled, write detailed per-ghost render placement diagnostics to KSP.log. Leave off for normal playtests.")]
        public bool ghostRenderTracing = false;

        [GameParameters.CustomParameterUI("Map/TS render tracing (Warning: huge logs)",
            toolTip = "When enabled, write detailed map and tracking-station ghost render diagnostics to KSP.log. Leave off for normal playtests. Per-frame detail also requires Verbose logging on.")]
        public bool mapRenderTracing = false;

        [GameParameters.CustomParameterUI("Ledger apply tracing (Warning: huge logs)",
            toolTip = "When enabled, write detailed ledger reconstruction diagnostics to KSP.log: one structural snapshot per recalc, per-identity change lines (facility / tech-node / contract / per-subject science), and computed-vs-live read-back mismatch warnings. Leave off for normal playtests. Per-identity detail also requires Verbose logging on.")]
        public bool ledgerTracing = false;

        [GameParameters.CustomParameterUI("Readable sidecar mirrors (Warning: extra disk usage)",
            toolTip = "When enabled, also write human-readable .txt mirrors of recording sidecars for debugging and binary/text comparison")]
        public bool writeReadableSidecarMirrors = true;

        // autoBackupExistingSaves, showCommittedFutureOverlays and blockCommittedActions
        // were DELETED in the 2026-08-27 settings simplification: the pre-Parsek backup
        // always runs (PreParsekBackup), committed-future overlays always draw
        // (StockUiOverlayController), and committed-action click blocking is always
        // active (TechResearch/FacilityUpgrade/ContractAccept/KerbalHire patches).
        // Stale keys in existing saves / settings.cfg are silently ignored on load.

        [GameParameters.CustomParameterUI("Show supply route paths on map",
            toolTip = "When on, each committed same-body supply route draws its recorded launch-to-dock path as a line on the flight map and Tracking Station, so you can see where a route runs")]
        public bool showRouteLines = true;

        /// <summary>
        /// UI complexity mode (0 = Basic, 1 = Advanced; design
        /// `docs/dev/design-ui-basic-advanced.md` sections 6.2 / 7.3). Stored as an int
        /// to match the existing `samplingDensity` / `autoLoopTimeUnit` persisted-int
        /// convention rather than introducing an enum-typed persisted field.
        ///
        /// <para>Deliberately carries NO <c>CustomParameterUI</c> attribute: every mode
        /// write must route through the single setter seam
        /// <see cref="ParsekUI.SetUiComplexityMode"/> (design 6.2), and a stock
        /// difficulty-screen control would be a second writer that bypasses it.</para>
        ///
        /// <para>Raw default is <b>Advanced</b>, not Basic. The default is nearly
        /// irrelevant in practice because `ParsekSettingsPersistence.ApplyTo` always
        /// resolves the effective value (stored key, else the first-run install-footprint
        /// resolution of design 7.3, which persists immediately). It matters only if some
        /// path reads the field before any restore has run, and there Advanced is the
        /// fail-open answer that matches <see cref="UiSurfaceVisibility.FromStoredInt"/>:
        /// showing everything is the safe wrong answer, hiding windows is not.</para>
        /// </summary>
        public int uiComplexityMode = (int)UiComplexityMode.Advanced;

        /// <summary>
        /// Typed accessor for <see cref="uiComplexityMode"/> following the
        /// <see cref="SamplingDensityLevel"/> precedent. Conversion goes through
        /// <see cref="UiSurfaceVisibility.FromStoredInt"/> so the out-of-range clamp
        /// (fail-open to Advanced, design 6.2) lives in exactly one place.
        /// </summary>
        internal UiComplexityMode UiComplexityModeLevel
        {
            get => UiSurfaceVisibility.FromStoredInt(uiComplexityMode);
            set => uiComplexityMode = (int)value;
        }

        // The map-view non-orbital ghost trajectory polyline is always on (no
        // setting). It renders unconditionally in the DDOL Driver; there is no
        // useGhostTrajectoryPolyline field, CustomParameterUI, or persistence.
        //
        // The ghost trajectory rendering pipeline (Catmull-Rom smoothing
        // splines, anchor correction, the anchor taxonomy + DAG propagation,
        // and kraken outlier rejection) is always on too. These were once
        // gated behind useSmoothingSplines / useAnchorCorrection /
        // useAnchorTaxonomy / useOutlierRejection developer rollout flags;
        // the flags were removed once the pipeline stabilized on their
        // defaults, so there are no legacy off-paths to toggle.

        /// <summary>
        /// Recorder sample density preset (0=Low, 1=Medium, 2=High).
        /// Replaces the four individual sampling sliders (minSampleInterval,
        /// maxSampleInterval, velocityDirThreshold, speedChangeThreshold).
        /// Serialized as int for ConfigNode round-trip.
        /// </summary>
        [GameParameters.CustomIntParameterUI("Recorder sample density", minValue = 0, maxValue = 2,
            stepSize = 1, displayFormat = "N0",
            toolTip = "Trajectory sampling preset. 0 = Low, 1 = Medium, 2 = High. " +
                      "The Parsek settings window shows labeled buttons and an exact threshold summary.")]
        public int samplingDensity = 1; // Medium

        public SamplingDensity SamplingDensityLevel
        {
            get => samplingDensity >= 0 && samplingDensity <= 2
                ? (SamplingDensity)samplingDensity
                : SamplingDensity.Medium;
            set => samplingDensity = (int)value;
        }

        // --- Derived sampling thresholds from preset ---

        public float minSampleInterval => GetMinSampleInterval(SamplingDensityLevel);
        public float maxSampleInterval => GetMaxSampleInterval(SamplingDensityLevel);
        public float velocityDirThreshold => GetVelocityDirThreshold(SamplingDensityLevel);
        public float speedChangeThreshold => GetSpeedChangeThreshold(SamplingDensityLevel);

        internal static float GetMinSampleInterval(SamplingDensity level) =>
            level == SamplingDensity.Low ? 0.5f
            : level == SamplingDensity.High ? 0.05f
            : 0.2f;

        internal static float GetMaxSampleInterval(SamplingDensity level) =>
            level == SamplingDensity.Low ? 8.0f
            : level == SamplingDensity.High ? 1.0f
            : 3.0f;

        internal static float GetVelocityDirThreshold(SamplingDensity level) =>
            level == SamplingDensity.Low ? 6.0f
            : level == SamplingDensity.High ? 0.5f
            : 2.0f;

        internal static float GetSpeedChangeThreshold(SamplingDensity level) =>
            level == SamplingDensity.Low ? 12.0f
            : level == SamplingDensity.High ? 1.0f
            : 5.0f;

        internal static string DensityLabel(SamplingDensity level) =>
            level == SamplingDensity.Low ? "Low"
            : level == SamplingDensity.High ? "High"
            : "Medium";

        internal static string DensityTooltip(SamplingDensity level) =>
            level == SamplingDensity.Low
                ? "Fewer samples: smaller files, less CPU. Sharp turns look angular."
            : level == SamplingDensity.High
                ? "Dense sampling: smooth cinematic curves. Larger files."
            : "Balanced sampling for most flights.";

        internal static string DensitySummary(SamplingDensity level)
        {
            var ic = CultureInfo.InvariantCulture;
            float min = GetMinSampleInterval(level);
            float max = GetMaxSampleInterval(level);
            float dir = GetVelocityDirThreshold(level);
            float spd = GetSpeedChangeThreshold(level);
            return $"Sampling: every {min.ToString("F2", ic)}\u2013{max.ToString("F1", ic)}s, " +
                   $"{dir.ToString("F1", ic)}\u00b0 / {spd.ToString("F0", ic)}% thresholds";
        }

        /// <summary>
        /// Default launch-to-launch period in seconds (#381) for recordings with
        /// LoopTimeUnit.Auto. Must be &gt;= LoopTiming.MinCycleDuration. Overlap
        /// emerges when the period is shorter than the recording's duration.
        /// </summary>
        public float autoLoopIntervalSeconds = (float)LoopTiming.DefaultLoopIntervalSeconds;
        public int autoLoopTimeUnit = 0; // 0=Sec, 1=Min, 2=Hour

        /// <summary>
        /// How a looped mission that LANDS on a TRANSITED body (e.g. the Mun) treats that
        /// body's landing rotation when scheduling faithful relaunch windows. The launch pad
        /// always stays tight; this only governs the landed-on body. Permanently
        /// <see cref="Parsek.TransitedBodyRotationMode.Loose"/> (~1-2 Kerbin month cadence,
        /// few-km handoff seam) since the 2026-08-27 settings simplification retired the
        /// player-facing A/B knob (was <c>transitedBodyRotationModeIndex</c>). The Drop /
        /// Tight enum values remain reachable from unit tests, which pass the mode directly.
        /// See docs/dev/plans/zero-drift-reschedule.md.
        /// </summary>
        internal const TransitedBodyRotationMode LandingBodyAlignmentMode =
            Parsek.TransitedBodyRotationMode.Loose;

        /// <summary>
        /// Forces a re-aim-SUPPORTED looped mission to FAITHFUL playback (the verbatim recorded
        /// trajectory replayed on the loop clock) instead of auto-engaging re-aim. Default OFF -
        /// the auto-by-target behaviour is unchanged unless a player deliberately turns this on
        /// (A/B comparison of re-aim against the recording, or a preference for the verbatim
        /// trajectory). Persisted through GameParameters ONLY - deliberately NOT recorded in
        /// ParsekSettingsPersistence, so the knob cannot leak instance-wide across harness
        /// runs. HIDDEN from the Settings window since the 2026-08-27 settings
        /// simplification; the harness V8/V9 A/B lanes still pin it per-run through the
        /// M-A2 command seam.
        /// </summary>
        public bool forceFaithfulLoopPlayback = false;

        [GameParameters.CustomFloatParameterUI("Ghost audio volume", minValue = 0f, maxValue = 1f,
            stepCount = 20, displayFormat = "P0",
            toolTip = "Volume multiplier for ghost vessel audio (engines, decouplers, explosions). 0 = muted.")]
        public float ghostAudioVolume = 0.7f;

        public LoopTimeUnit AutoLoopDisplayUnit
        {
            get => autoLoopTimeUnit == 1 ? LoopTimeUnit.Min
                 : autoLoopTimeUnit == 2 ? LoopTimeUnit.Hour
                 : LoopTimeUnit.Sec;
            set => autoLoopTimeUnit = value == LoopTimeUnit.Min ? 1
                 : value == LoopTimeUnit.Hour ? 2 : 0;
        }

        public static ParsekSettings Current =>
            CurrentOverrideForTesting ?? HighLogic.CurrentGame?.Parameters?.CustomParams<ParsekSettings>();

        /// <summary>
        /// Clamps the hidden-but-kept settings (the auto-record trio, autoMerge,
        /// forceFaithfulLoopPlayback) back to their shipping values. KSP round-trips every
        /// GameParameters field through the save, so a career played before the 2026-08-27
        /// settings simplification can carry e.g. <c>autoMerge = False</c> - a value the
        /// player can no longer see or change anywhere. For players those stored values are
        /// stale, not intent, so <c>ParsekScenario.OnLoad</c> clamps them on every load -
        /// UNLESS an automation env hook is armed (<see cref="AutomationEnvPresent"/>),
        /// because the harness pins these per-run through fixture saves and the M-A2
        /// command seam and must keep control. Pure and silent; the caller logs.
        /// Returns true when any value changed.
        ///
        /// <para>Invariant for MANUAL in-game test batches (Ctrl+Shift+T on the dev
        /// instance, no automation env): the clamp is armed there, so a test that flips
        /// one of these fields must finish its assertions without crossing a scene load -
        /// any intervening <c>ParsekScenario.OnLoad</c> resets the field mid-test.</para>
        /// </summary>
        internal static bool ClampHiddenSettingsToShippingValues(ParsekSettings s)
        {
            if (s == null) return false;
            bool changed = !s.autoRecordOnLaunch || !s.autoRecordOnEva
                || !s.autoRecordOnFirstModificationAfterSwitch || !s.autoMerge
                || s.forceFaithfulLoopPlayback;
            s.autoRecordOnLaunch = true;
            s.autoRecordOnEva = true;
            s.autoRecordOnFirstModificationAfterSwitch = true;
            s.autoMerge = true;
            s.forceFaithfulLoopPlayback = false;
            return changed;
        }

        /// <summary>
        /// Whether any Parsek automation env hook is armed for this process
        /// (PARSEK_TEST_COMMANDS=1, the M-A2 command seam; or PARSEK_AUTORUN_TESTS set,
        /// the M-A3 autorun hook). Read ONCE per process like the hooks themselves -
        /// changing the env after start has no effect. Gates the hidden-settings clamp
        /// above: an armed harness keeps authority over the hidden fields.
        /// </summary>
        internal static bool AutomationEnvPresent
        {
            get
            {
                if (!automationEnvPresent.HasValue)
                {
                    // The env-var names and the seam's arm predicate belong to the hooks
                    // themselves - referenced here, never re-spelled, so a rename cannot
                    // silently disarm this gate and let the clamp fire under the harness.
                    // The autorun half is deliberately looser than AutorunHooks.Parse's
                    // arming (any non-empty value counts, whitespace included): erring
                    // toward NOT clamping is the safe direction.
                    automationEnvPresent =
                        TestCommands.ParsekTestCommandAddon.IsArmed(
                            Environment.GetEnvironmentVariable(
                                TestCommands.ParsekTestCommandAddon.EnvVarName))
                        || !string.IsNullOrEmpty(
                            Environment.GetEnvironmentVariable(
                                InGameTests.TestRunnerShortcut.EnvTestsVar));
                }
                return automationEnvPresent.Value;
            }
        }

        private static bool? automationEnvPresent;

        /// <summary>Test-only: clears the cached automation-env read.</summary>
        internal static void ResetAutomationEnvCacheForTesting() => automationEnvPresent = null;

        /// <summary>
        /// Test-only override for <see cref="Current"/>. Lets unit tests exercise
        /// code paths that branch on a non-null <c>ParsekSettings.Current</c> without
        /// standing up a full <c>HighLogic.CurrentGame</c>. Production code must not
        /// set this.
        /// </summary>
        internal static ParsekSettings CurrentOverrideForTesting;

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);

            SamplingDensity level = ResolveSamplingDensityFromConfig(
                node, out bool migratedFromLegacy, out string invalidSamplingDensityValue);
            SamplingDensityLevel = level;

            if (!string.IsNullOrEmpty(invalidSamplingDensityValue))
            {
                ParsekLog.Warn("Settings",
                    $"Invalid samplingDensity='{invalidSamplingDensityValue}' in config; " +
                    $"using {(migratedFromLegacy ? "legacy-derived" : "default")} " +
                    $"{DensityLabel(level)} preset");
            }

            if (migratedFromLegacy &&
                TryReadLegacySamplingThresholds(node,
                    out float legacyMin, out float legacyMax, out float legacyDir, out float legacySpeed))
            {
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.Info("Settings",
                    $"Migrated legacy sampling thresholds ({legacyMin.ToString("F2", ic)}s/" +
                    $"{legacyMax.ToString("F1", ic)}s/{legacyDir.ToString("F1", ic)}\u00b0/" +
                    $"{legacySpeed.ToString("F0", ic)}%) to samplingDensity={DensityLabel(level)}");
            }
        }

        internal static SamplingDensity ResolveSamplingDensityFromConfig(
            ConfigNode node, out bool migratedFromLegacy, out string invalidSamplingDensityValue)
        {
            migratedFromLegacy = false;
            invalidSamplingDensityValue = null;

            if (TryReadSamplingDensityFromConfig(node, out SamplingDensity storedLevel))
                return storedLevel;

            invalidSamplingDensityValue = GetConfigValueOrNull(node, SamplingDensityKey);

            if (TryReadLegacySamplingThresholds(node,
                out float legacyMin, out float legacyMax, out float legacyDir, out float legacySpeed))
            {
                migratedFromLegacy = true;
                return DeriveSamplingDensityFromLegacyThresholds(
                    legacyMin, legacyMax, legacyDir, legacySpeed);
            }

            return SamplingDensity.Medium;
        }

        internal static SamplingDensity DeriveSamplingDensityFromLegacyThresholds(
            float minSampleInterval, float maxSampleInterval,
            float velocityDirThreshold, float speedChangeThreshold)
        {
            SamplingDensity bestLevel = SamplingDensity.Medium;
            double bestScore = double.MaxValue;

            foreach (SamplingDensity level in new[]
            {
                SamplingDensity.Low,
                SamplingDensity.Medium,
                SamplingDensity.High
            })
            {
                double score =
                    Square(NormalizeLegacyDistance(minSampleInterval, GetMinSampleInterval(level), 0.95)) +
                    Square(NormalizeLegacyDistance(maxSampleInterval, GetMaxSampleInterval(level), 9.0)) +
                    Square(NormalizeLegacyDistance(velocityDirThreshold, GetVelocityDirThreshold(level), 9.5)) +
                    Square(NormalizeLegacyDistance(speedChangeThreshold, GetSpeedChangeThreshold(level), 19.0));

                if (score < bestScore)
                {
                    bestScore = score;
                    bestLevel = level;
                }
            }

            return bestLevel;
        }

        internal static bool TryReadLegacySamplingThresholds(
            ConfigNode node,
            out float minSampleInterval,
            out float maxSampleInterval,
            out float velocityDirThreshold,
            out float speedChangeThreshold)
        {
            minSampleInterval = GetMinSampleInterval(SamplingDensity.Medium);
            maxSampleInterval = GetMaxSampleInterval(SamplingDensity.Medium);
            velocityDirThreshold = GetVelocityDirThreshold(SamplingDensity.Medium);
            speedChangeThreshold = GetSpeedChangeThreshold(SamplingDensity.Medium);

            if (node == null) return false;

            bool sawLegacyField = false;
            sawLegacyField |= TryReadFloat(node, LegacyMinSampleIntervalKey, ref minSampleInterval);
            sawLegacyField |= TryReadFloat(node, LegacyMaxSampleIntervalKey, ref maxSampleInterval);
            sawLegacyField |= TryReadFloat(node, LegacyVelocityDirThresholdKey, ref velocityDirThreshold);
            sawLegacyField |= TryReadFloat(node, LegacySpeedChangeThresholdKey, ref speedChangeThreshold);
            return sawLegacyField;
        }

        private static bool TryReadSamplingDensityFromConfig(ConfigNode node, out SamplingDensity level)
        {
            level = SamplingDensity.Medium;
            string rawValue = GetConfigValueOrNull(node, SamplingDensityKey);
            if (string.IsNullOrEmpty(rawValue))
                return false;

            if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                return false;
            if (parsed < 0 || parsed > 2)
                return false;

            level = (SamplingDensity)parsed;
            return true;
        }

        private static string GetConfigValueOrNull(ConfigNode node, string key)
        {
            if (node == null) return null;
            string value = node.GetValue(key);
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private static bool TryReadFloat(ConfigNode node, string key, ref float value)
        {
            string rawValue = GetConfigValueOrNull(node, key);
            if (rawValue == null)
                return false;

            if (float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                value = parsed;
            return true;
        }

        private static double NormalizeLegacyDistance(float actual, float preset, double range)
            => (actual - preset) / range;

        private static double Square(double value) => value * value;

        // The Pipeline-Smoothing / Pipeline-Anchor / Pipeline-Outlier
        // "flag flipped" Notify* helpers were removed with the
        // useSmoothingSplines / useAnchorCorrection / useAnchorTaxonomy /
        // useOutlierRejection rollout flags; the pipeline is now
        // unconditionally on, so there is no flag flip to log.
    }
}
