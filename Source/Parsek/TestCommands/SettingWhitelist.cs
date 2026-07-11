using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Where a whitelisted setting is authoritatively persisted. 8 of the 16 are
    /// NOT authoritative through <c>GameParameters.CustomParameterNode</c>: for
    /// those the <c>ParsekSettingsPersistence</c> sidecar
    /// (<c>GameData/Parsek/PluginData/settings.cfg</c>) is authoritative and
    /// <c>ParsekScenario.OnLoad</c> OVERWRITES the GameParameters value on every
    /// save load. A dispatcher that only mutated the live field would see its
    /// change silently reverted at the next save load, so those route through the
    /// matching <c>ParsekSettingsPersistence.Record*</c> path too.
    /// </summary>
    internal enum PersistenceRoute
    {
        /// <summary>Persisted via GameParameters only (round-trips on the next game save).</summary>
        GameParameters,

        /// <summary>Persisted via GameParameters AND the ParsekSettingsPersistence sidecar.</summary>
        GameParametersPlusSidecar,
    }

    internal enum SettingValueType
    {
        Bool,
        Int,
        Float,
    }

    /// <summary>
    /// Pure decision result of <see cref="SettingWhitelist.TryApply"/>. Carries the
    /// typed value, the persistence route, and the exact
    /// <c>ParsekSettingsPersistence.Record*</c> selector (a method-name string, not
    /// reflection over user input) so the thin Unity applier can dispatch without
    /// interpreting arbitrary field names. Commands are data, never code.
    /// </summary>
    internal struct SettingApplyResult
    {
        public bool Accepted;

        /// <summary>Reject reason when <see cref="Accepted"/> is false
        /// (<c>setting-not-whitelisted</c> / <c>setting-value-invalid</c>).</summary>
        public string RejectReason;

        public string Name;
        public SettingValueType Type;
        public PersistenceRoute Route;

        /// <summary>The exact <c>ParsekSettingsPersistence.Record*</c> method name for a
        /// sidecar-tracked setting; null for a GameParameters-only setting.</summary>
        public string RecordMethod;

        public bool BoolValue;
        public int IntValue;
        public float FloatValue;
    }

    /// <summary>
    /// Explicit allowlist of settings <c>SetSetting</c> may mutate, each mapped to a
    /// typed parse-and-range check and a persistence route. There is NO reflective
    /// "set any field": the whitelist switches on the exact name and returns a pure
    /// decision (typed value + route + Record* selector) for the applier to act on.
    /// This is the security boundary that keeps commands data, not code.
    /// </summary>
    internal static class SettingWhitelist
    {
        private struct Entry
        {
            public SettingValueType Type;
            public double Min;
            public double Max;
            public PersistenceRoute Route;
            public string RecordMethod; // null for GameParameters-only
        }

        // The 16 whitelisted settings. Fields and Record* method names verified
        // against ParsekSettings.cs and ParsekSettingsPersistence.cs. Note the name
        // asymmetry: setting `writeReadableSidecarMirrors` -> method
        // `RecordReadableSidecarMirrors` (the method drops the "write" prefix).
        private static readonly Dictionary<string, Entry> Table = new Dictionary<string, Entry>
        {
            // --- GameParameters-only (8) ---
            ["autoRecordOnLaunch"] = Bool(PersistenceRoute.GameParameters, null),
            ["autoRecordOnEva"] = Bool(PersistenceRoute.GameParameters, null),
            ["autoRecordOnFirstModificationAfterSwitch"] = Bool(PersistenceRoute.GameParameters, null),
            ["autoMerge"] = Bool(PersistenceRoute.GameParameters, null),
            ["verboseLogging"] = Bool(PersistenceRoute.GameParameters, null),
            ["samplingDensity"] = Int(0, 2, PersistenceRoute.GameParameters, null),
            ["ghostAudioVolume"] = Float(0.0, 1.0, PersistenceRoute.GameParameters, null),
            ["transitedBodyRotationModeIndex"] = Int(0, 2, PersistenceRoute.GameParameters, null),

            // --- GameParameters + ParsekSettingsPersistence sidecar (8 tracked) ---
            ["ghostRenderTracing"] = Bool(PersistenceRoute.GameParametersPlusSidecar, "RecordGhostRenderTracing"),
            ["mapRenderTracing"] = Bool(PersistenceRoute.GameParametersPlusSidecar, "RecordMapRenderTracing"),
            ["ledgerTracing"] = Bool(PersistenceRoute.GameParametersPlusSidecar, "RecordLedgerTracing"),
            ["writeReadableSidecarMirrors"] = Bool(PersistenceRoute.GameParametersPlusSidecar, "RecordReadableSidecarMirrors"),
            ["autoBackupExistingSaves"] = Bool(PersistenceRoute.GameParametersPlusSidecar, "RecordAutoBackupExistingSaves"),
            ["showCommittedFutureOverlays"] = Bool(PersistenceRoute.GameParametersPlusSidecar, "RecordShowCommittedFutureOverlays"),
            ["blockCommittedActions"] = Bool(PersistenceRoute.GameParametersPlusSidecar, "RecordBlockCommittedActions"),
            ["showRouteLines"] = Bool(PersistenceRoute.GameParametersPlusSidecar, "RecordShowRouteLines"),
        };

        /// <summary>
        /// Pure whitelist decision. An unknown name -> reject
        /// <c>setting-not-whitelisted</c>; a value that fails the typed parse or
        /// range -> reject <c>setting-value-invalid</c>. On accept, returns the
        /// typed value plus the persistence route and Record* selector. The field is
        /// never touched here (that is the applier's job).
        /// </summary>
        internal static SettingApplyResult TryApply(string name, string rawValue)
        {
            var result = new SettingApplyResult { Name = name, Accepted = false };

            if (name == null || !Table.TryGetValue(name, out Entry entry))
            {
                result.RejectReason = "setting-not-whitelisted";
                return result;
            }

            result.Type = entry.Type;
            result.Route = entry.Route;
            result.RecordMethod = entry.RecordMethod;

            switch (entry.Type)
            {
                case SettingValueType.Bool:
                    if (!bool.TryParse(rawValue, out bool bv))
                    {
                        result.RejectReason = "setting-value-invalid";
                        return result;
                    }
                    result.BoolValue = bv;
                    break;

                case SettingValueType.Int:
                    if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv)
                        || iv < entry.Min || iv > entry.Max)
                    {
                        result.RejectReason = "setting-value-invalid";
                        return result;
                    }
                    result.IntValue = iv;
                    break;

                case SettingValueType.Float:
                    if (!float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float fv)
                        || fv < entry.Min || fv > entry.Max)
                    {
                        result.RejectReason = "setting-value-invalid";
                        return result;
                    }
                    result.FloatValue = fv;
                    break;
            }

            result.Accepted = true;
            return result;
        }

        /// <summary>Whether <paramref name="name"/> is a whitelisted setting.</summary>
        internal static bool IsWhitelisted(string name) => name != null && Table.ContainsKey(name);

        /// <summary>Read-only view of the whitelisted names (for coverage tests).</summary>
        internal static IReadOnlyCollection<string> WhitelistedNames => Table.Keys;

        private static Entry Bool(PersistenceRoute route, string recordMethod)
            => new Entry { Type = SettingValueType.Bool, Route = route, RecordMethod = recordMethod };

        private static Entry Int(int min, int max, PersistenceRoute route, string recordMethod)
            => new Entry { Type = SettingValueType.Int, Min = min, Max = max, Route = route, RecordMethod = recordMethod };

        private static Entry Float(double min, double max, PersistenceRoute route, string recordMethod)
            => new Entry { Type = SettingValueType.Float, Min = min, Max = max, Route = route, RecordMethod = recordMethod };
    }
}
