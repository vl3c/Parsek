using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>How a stored format number compares with what this build reads and writes.</summary>
    internal enum SaveFormatClass
    {
        /// <summary>The key or header field is absent (or unparseable).</summary>
        Missing,

        /// <summary>Older than the 1.0 baseline: a pre-1.0 save, loaded best effort or rejected.</summary>
        PreBaseline,

        /// <summary>At or after the baseline, older than current: the upgrade steps bring it to current.</summary>
        Upgradable,

        /// <summary>Exactly what this build writes.</summary>
        Current,

        /// <summary>Written by a newer build: never read as if current, never destroyed.</summary>
        Newer,
    }

    /// <summary>
    /// One versioned on-disk format. <see cref="Baseline"/> is the value the 1.0 release wrote and
    /// never changes; <see cref="Current"/> is the live constant this build writes (referenced
    /// from its owner, never copied), which only grows.
    /// </summary>
    internal sealed class SaveFormatInfo
    {
        internal readonly string Name;
        internal readonly string Key;
        internal readonly int Baseline;
        private readonly int current;

        internal SaveFormatInfo(string name, string key, int baseline, int current)
        {
            Name = name;
            Key = key;
            Baseline = baseline;
            this.current = current;
        }

        /// <summary>The constant this build writes, unless a test raised it through
        /// <see cref="SaveFormat.SetCurrentForTesting"/> (writers never read this override).</summary>
        internal int Current => SaveFormat.ResolveCurrent(Name, current);

        internal SaveFormatClass Classify(int? stored) => SaveFormat.Classify(stored, Baseline, Current);
    }

    /// <summary>
    /// A single format step <c>From -> From + 1</c>. <see cref="UpgradeNode"/> rewrites a ConfigNode
    /// in place into the next shape and returns false when it cannot; a step whose reader keeps an
    /// explicit decode branch for the older layout (a binary sidecar) registers a null delegate.
    /// </summary>
    internal sealed class SaveFormatUpgradeStep
    {
        internal int From;
        internal string Description;
        internal Func<ConfigNode, bool> UpgradeNode;
    }

    /// <summary>
    /// The registry of every versioned format Parsek persists (1.0 save-format freeze, owner
    /// ruling 2026-11-04), the pure <see cref="Classify(int?, int, int)"/> every reader routes a
    /// stored number through, and the per-format upgrade steps a reader runs on Upgradable data.
    /// Rule from 1.0 on: a build loads every 1.0+ save, upgrades older-but-baseline data through
    /// these steps, and keeps data it does not understand (a Newer number) untouched and written
    /// back, never read as current and never dropped. Pre-baseline data loads best effort.
    /// Excluded: the <c>.pann</c> annotation cache, which is recomputed when it does not match.
    /// </summary>
    internal static class SaveFormat
    {
        internal const string Tag = "SaveFormat";

        /// <summary>
        /// The save-wide format of the PARSEK scenario node, written as <see cref="SaveFormatKey"/>.
        /// It versions every store inside that node that carries no version of its own: the rewind
        /// points, supersede relations, ledger tombstones, merge journal and Re-Fly marker, the
        /// supply routes and their event log, vessel names, missions' container, crew replacements,
        /// group hierarchy, held windows, the stock pool reference and the other markers. A shape
        /// change to any of those (a renamed or removed key, a changed layout) bumps this number
        /// and registers an upgrade step for <see cref="Save"/>; an additive key does not. The
        /// self-versioned stores (recordings, trees, sidecars, ledger, game-state store,
        /// milestones, mission selection) keep their own rows below.
        /// </summary>
        internal const int CurrentSaveFormat = 1;
        internal const string SaveFormatKey = "parsekSaveFormat";
        internal const string WrittenByKey = "parsekWrittenBy";

        internal static readonly SaveFormatInfo Save =
            new SaveFormatInfo("save", "PARSEK/" + SaveFormatKey, 1, CurrentSaveFormat);
        internal static readonly SaveFormatInfo RecordingFormat =
            new SaveFormatInfo("recording-format", "RECORDING/recordingFormatVersion", 1,
                RecordingSchema.CurrentRecordingFormatVersion);
        internal static readonly SaveFormatInfo RecordingSchemaGeneration =
            new SaveFormatInfo("recording-schema-generation", "recordingSchemaGeneration", 4,
                RecordingSchema.CurrentRecordingSchemaGeneration);
        internal static readonly SaveFormatInfo TreeFormat =
            new SaveFormatInfo("tree-format", "RECORDING_TREE/treeFormatVersion", 0,
                RecordingTree.CurrentTreeFormatVersion);
        internal static readonly SaveFormatInfo TrajectoryBinary =
            new SaveFormatInfo("trajectory-binary", "PSK0 header formatVersion", 1,
                TrajectorySidecarBinary.CurrentBinaryVersion);
        internal static readonly SaveFormatInfo Snapshot =
            new SaveFormatInfo("snapshot-sidecar", "PSN0 header formatVersion", 0,
                SnapshotSidecarCodec.CurrentFormatVersion);
        internal static readonly SaveFormatInfo Ledger =
            new SaveFormatInfo("ledger", "LEDGER/version", 0, Parsek.Ledger.CurrentLedgerVersion);
        internal static readonly SaveFormatInfo GameStateStoreFormat =
            new SaveFormatInfo("game-state-store", "PARSEK_GAME_STATE/version", 1,
                GameStateStore.CurrentEventsVersion);
        internal static readonly SaveFormatInfo Milestones =
            new SaveFormatInfo("milestones", "PARSEK_MILESTONES/version", 1,
                MilestoneStore.CurrentMilestonesVersion);
        internal static readonly SaveFormatInfo MissionSelection =
            new SaveFormatInfo("mission-selection", "MISSION/selectionSchemaGeneration", 1,
                Mission.CurrentSelectionSchemaGeneration);

        internal static readonly IReadOnlyList<SaveFormatInfo> All = new[]
        {
            Save, RecordingFormat, RecordingSchemaGeneration, TreeFormat, TrajectoryBinary, Snapshot,
            Ledger, GameStateStoreFormat, Milestones, MissionSelection,
        };

        // Upgrade steps per format name, ordered by From. Empty while every Current equals its
        // Baseline; a format bump adds its step here in the same commit.
        private static readonly Dictionary<string, List<SaveFormatUpgradeStep>> steps =
            new Dictionary<string, List<SaveFormatUpgradeStep>>(StringComparer.Ordinal);

        private static readonly Dictionary<string, int> currentOverridesForTesting =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<SaveFormatUpgradeStep>> testSteps =
            new Dictionary<string, List<SaveFormatUpgradeStep>>(StringComparer.Ordinal);

        /// <summary>Pure: where <paramref name="stored"/> sits against the baseline and the current value.</summary>
        internal static SaveFormatClass Classify(int? stored, int baseline, int current)
        {
            if (!stored.HasValue)
                return SaveFormatClass.Missing;
            int v = stored.Value;
            if (v > current)
                return SaveFormatClass.Newer;
            if (v == current)
                return SaveFormatClass.Current;
            if (v < baseline)
                return SaveFormatClass.PreBaseline;
            return SaveFormatClass.Upgradable;
        }

        /// <summary>Parses a stored number invariantly; null when absent or unparseable.</summary>
        internal static int? ParseStored(string value)
        {
            if (string.IsNullOrEmpty(value))
                return null;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                ? v
                : (int?)null;
        }

        internal static int ResolveCurrent(string name, int constant)
        {
            return name != null && currentOverridesForTesting.TryGetValue(name, out int v) ? v : constant;
        }

        /// <summary>
        /// The ordered steps that bring <paramref name="from"/> to <paramref name="format"/>'s
        /// current value. False (and null) when <paramref name="from"/> is not Upgradable or a step
        /// in the chain is missing. An empty list is never returned.
        /// </summary>
        internal static bool TryGetUpgradePath(
            SaveFormatInfo format, int from, out List<SaveFormatUpgradeStep> path)
        {
            path = null;
            if (format == null || format.Classify(from) != SaveFormatClass.Upgradable)
                return false;
            var result = new List<SaveFormatUpgradeStep>();
            for (int v = from; v < format.Current; v++)
            {
                SaveFormatUpgradeStep step = FindStep(format.Name, v);
                if (step == null)
                    return false;
                result.Add(step);
            }
            path = result;
            return true;
        }

        internal static bool HasUpgradePath(SaveFormatInfo format, int from)
        {
            return TryGetUpgradePath(format, from, out _);
        }

        /// <summary>
        /// Runs the node steps from <paramref name="from"/> to current on <paramref name="node"/>
        /// in place. Returns false (with <paramref name="failure"/>) when there is no path or a
        /// step refused; the node may then be partly rewritten, so callers pass a copy.
        /// Logs one Info line per successful upgrade.
        /// </summary>
        internal static bool TryUpgradeNode(
            SaveFormatInfo format, int from, ConfigNode node, string context, out string failure)
        {
            failure = null;
            if (!TryGetUpgradePath(format, from, out List<SaveFormatUpgradeStep> path))
            {
                failure = "no-upgrade-path";
                return false;
            }
            for (int i = 0; i < path.Count; i++)
            {
                if (path[i].UpgradeNode == null)
                    continue;
                bool ok;
                try
                {
                    ok = node != null && path[i].UpgradeNode(node);
                }
                catch (Exception ex)
                {
                    ok = false;
                    failure = "step-threw " + ex.GetType().Name;
                }
                if (!ok)
                {
                    failure = failure ?? "step-refused";
                    ParsekLog.Warn(Tag,
                        $"Upgrade of {format.Name} {context ?? ""} failed at step " +
                        $"{path[i].From.ToString(CultureInfo.InvariantCulture)}->" +
                        $"{(path[i].From + 1).ToString(CultureInfo.InvariantCulture)}: {failure}");
                    return false;
                }
            }
            ParsekLog.Info(Tag,
                $"Upgraded {format.Name} {context ?? ""} from {from.ToString(CultureInfo.InvariantCulture)} " +
                $"to {format.Current.ToString(CultureInfo.InvariantCulture)} through " +
                $"{path.Count.ToString(CultureInfo.InvariantCulture)} step(s)");
            return true;
        }

        /// <summary>
        /// The recording gate's per-number decision: null when <paramref name="stored"/> is current
        /// or Upgradable with a registered path (then <paramref name="needsUpgrade"/> is true),
        /// else the reject reason (<paramref name="missingReason"/> / <paramref name="olderReason"/>
        /// for pre-baseline data and for an Upgradable number without a path /
        /// <paramref name="newerReason"/>). A stored 0 counts as missing only when
        /// <paramref name="zeroIsMissing"/> (a generation is never 0).
        /// </summary>
        internal static string GateReason(
            SaveFormatInfo format, int stored, bool zeroIsMissing,
            string missingReason, string olderReason, string newerReason, out bool needsUpgrade)
        {
            needsUpgrade = false;
            SaveFormatClass c = format.Classify(zeroIsMissing && stored == 0 ? (int?)null : stored);
            switch (c)
            {
                case SaveFormatClass.Current:
                    return null;
                case SaveFormatClass.Upgradable:
                    if (HasUpgradePath(format, stored))
                    {
                        needsUpgrade = true;
                        return null;
                    }
                    return olderReason;
                case SaveFormatClass.Newer:
                    return newerReason;
                case SaveFormatClass.Missing:
                    return missingReason;
                default:
                    return olderReason;
            }
        }

        // ---------------------------------------------------------------- save-wide stamp

        /// <summary>Writes the save-wide format and the informational writer version into the PARSEK node.</summary>
        internal static void StampScenarioNode(ConfigNode node)
        {
            if (node == null)
                return;
            node.SetValue(SaveFormatKey, CurrentSaveFormat.ToString(CultureInfo.InvariantCulture), true);
            node.SetValue(WrittenByKey, WriterVersion(), true);
        }

        internal static string WriterVersion()
        {
            try
            {
                Version v = typeof(SaveFormat).Assembly.GetName().Version;
                return v != null ? v.ToString() : "unknown";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        /// <summary>
        /// Reads and logs the save-wide stamp of a loaded PARSEK node. <paramref name="hasParsekData"/>
        /// says whether the node carries any Parsek data (an empty first-contact node has no stamp
        /// and says nothing). Returns the classification.
        /// </summary>
        internal static SaveFormatClass ClassifyLoadedScenarioNode(ConfigNode node, bool hasParsekData)
        {
            string raw = node?.GetValue(SaveFormatKey);
            SaveFormatClass c = Save.Classify(ParseStored(raw));
            string writtenBy = node?.GetValue(WrittenByKey) ?? "(unknown)";
            var ic = CultureInfo.InvariantCulture;
            switch (c)
            {
                case SaveFormatClass.Missing:
                    if (hasParsekData)
                        ParsekLog.Info(Tag, "pre-1.0 save (no parsekSaveFormat); loading best effort");
                    else
                        ParsekLog.Verbose(Tag, "no parsekSaveFormat on an empty Parsek node (new save)");
                    break;
                case SaveFormatClass.Current:
                    ParsekLog.Verbose(Tag,
                        $"parsekSaveFormat={raw} (current) writtenBy={writtenBy}");
                    break;
                case SaveFormatClass.Newer:
                    ParsekLog.Warn(Tag,
                        $"save written by a newer Parsek: parsekSaveFormat={raw} > " +
                        $"current={CurrentSaveFormat.ToString(ic)} (writtenBy={writtenBy}); stores this " +
                        "build does not understand are kept where their readers can hold them");
                    break;
                case SaveFormatClass.Upgradable:
                    ParsekLog.Info(Tag,
                        $"parsekSaveFormat={raw} older than current={CurrentSaveFormat.ToString(ic)} " +
                        $"(writtenBy={writtenBy}); upgrade path " +
                        (HasUpgradePath(Save, ParseStored(raw).Value) ? "registered" : "MISSING"));
                    break;
                default:
                    ParsekLog.Info(Tag,
                        $"parsekSaveFormat={raw} older than the 1.0 baseline={Save.Baseline.ToString(ic)}; " +
                        "loading best effort");
                    break;
            }
            return c;
        }

        // ---------------------------------------------------------------- registry internals

        private static SaveFormatUpgradeStep FindStep(string formatName, int from)
        {
            SaveFormatUpgradeStep found = FindIn(testSteps, formatName, from);
            return found ?? FindIn(steps, formatName, from);
        }

        private static SaveFormatUpgradeStep FindIn(
            Dictionary<string, List<SaveFormatUpgradeStep>> table, string formatName, int from)
        {
            if (!table.TryGetValue(formatName, out List<SaveFormatUpgradeStep> list))
                return null;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].From == from)
                    return list[i];
            }
            return null;
        }

        /// <summary>Number of production steps registered for <paramref name="format"/>.</summary>
        internal static int RegisteredStepCount(SaveFormatInfo format)
        {
            return format != null && steps.TryGetValue(format.Name, out List<SaveFormatUpgradeStep> list)
                ? list.Count
                : 0;
        }

        // ---------------------------------------------------------------- test seams

        /// <summary>Test seam: classify <paramref name="format"/> against another current value (null restores).</summary>
        internal static void SetCurrentForTesting(SaveFormatInfo format, int? current)
        {
            if (format == null)
                return;
            if (current.HasValue)
                currentOverridesForTesting[format.Name] = current.Value;
            else
                currentOverridesForTesting.Remove(format.Name);
        }

        /// <summary>Test seam: registers a step for <paramref name="format"/> until <see cref="ResetForTesting"/>.</summary>
        internal static void RegisterStepForTesting(SaveFormatInfo format, SaveFormatUpgradeStep step)
        {
            if (format == null || step == null)
                return;
            if (!testSteps.TryGetValue(format.Name, out List<SaveFormatUpgradeStep> list))
            {
                list = new List<SaveFormatUpgradeStep>();
                testSteps[format.Name] = list;
            }
            list.Add(step);
        }

        internal static void ResetForTesting()
        {
            currentOverridesForTesting.Clear();
            testSteps.Clear();
        }
    }
}
