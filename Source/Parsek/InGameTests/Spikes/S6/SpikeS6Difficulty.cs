using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace Parsek.InGameTests.Spikes.S6
{
    // R0 spike branch only (spike/mp-r0, never merged). Plan: r0-spikes.md, task S6 step (d).
    //
    // The forty difficulty values R1-10 records for a race (the plan's RaceDifficulty.Keys):
    // the stock DifficultyParams fields, the CareerParams values (TechTreeUrl excluded, it is
    // not a difficulty), and the named fields of the two stock custom nodes AdvancedParams and
    // CommNet.CommNetParams. Members verified against the KSP 1.12.5 decompile (S6 report).
    // Read by reflection over plain objects so the xUnit suite can pin it with fakes.

    /// <summary>One difficulty key: the part it lives in and the member name there.</summary>
    internal struct SpikeS6DifficultyKey
    {
        internal readonly string Part;
        internal readonly string Member;

        internal SpikeS6DifficultyKey(string part, string member)
        {
            Part = part;
            Member = member;
        }

        internal string Name { get { return Part + "." + Member; } }
    }

    internal static class SpikeS6Difficulty
    {
        internal const string PartDifficulty = "Difficulty";
        internal const string PartCareer = "Career";
        internal const string PartAdvanced = "Advanced";
        internal const string PartCommNet = "CommNet";

        private static readonly string[] DifficultyMembers =
        {
            "AutoHireCrews", "MissingCrewsRespawn", "RespawnTimer", "BypassEntryPurchaseAfterResearch",
            "AllowStockVessels", "IndestructibleFacilities", "ResourceAbundance", "ReentryHeatScale",
            "EnableCommNet", "AllowOtherLaunchSites", "persistKerbalInventories",
        };

        private static readonly string[] CareerMembers =
        {
            "StartingFunds", "StartingScience", "StartingReputation", "FundsGainMultiplier",
            "RepGainMultiplier", "ScienceGainMultiplier", "FundsLossMultiplier", "RepLossMultiplier",
            "RepLossDeclined",
        };

        // EnableKerbalExperience and ImmediateLevelUp are properties with setters in 1.12.5.
        // PartUpgradesInMission and EnableFullSASInMissions are Making History mission-mode
        // switches, not career or sandbox difficulty, so they are not race keys.
        private static readonly string[] AdvancedMembers =
        {
            "AllowNegativeCurrency", "PressurePartLimits", "GPartLimits", "GKerbalLimits",
            "KerbalGToleranceMult", "ResourceTransferObeyCrossfeed", "ActionGroupsAlways",
            "BuildingImpactDamageMult", "PartUpgradesInSandbox", "PartUpgradesInCareer",
            "EnableFullSASInSandbox", "EnableKerbalExperience", "ImmediateLevelUp",
        };

        private static readonly string[] CommNetMembers =
        {
            "requireSignalForControl", "plasmaBlackout", "rangeModifier", "DSNModifier",
            "occlusionMultiplierVac", "occlusionMultiplierAtm", "enableGroundStations",
        };

        internal static readonly SpikeS6DifficultyKey[] Keys = BuildKeys();

        private static SpikeS6DifficultyKey[] BuildKeys()
        {
            var keys = new List<SpikeS6DifficultyKey>();
            foreach (string m in DifficultyMembers) keys.Add(new SpikeS6DifficultyKey(PartDifficulty, m));
            foreach (string m in CareerMembers) keys.Add(new SpikeS6DifficultyKey(PartCareer, m));
            foreach (string m in AdvancedMembers) keys.Add(new SpikeS6DifficultyKey(PartAdvanced, m));
            foreach (string m in CommNetMembers) keys.Add(new SpikeS6DifficultyKey(PartCommNet, m));
            return keys.ToArray();
        }

        /// <summary>
        /// The four edits of the procedure, one per part: "allow other launch sites" off, the
        /// funds reward at 80 percent, G limits for kerbals on, plasma blackout on.
        /// </summary>
        internal static readonly KeyValuePair<string, string>[] PlannedEdits =
        {
            new KeyValuePair<string, string>("Difficulty.AllowOtherLaunchSites", "False"),
            new KeyValuePair<string, string>("Career.FundsGainMultiplier", "0.8"),
            new KeyValuePair<string, string>("Advanced.GKerbalLimits", "True"),
            new KeyValuePair<string, string>("CommNet.plasmaBlackout", "True"),
        };

        private const BindingFlags MemberFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>Invariant text of one value: bools as True/False, floats round-trip.</summary>
        internal static string FormatValue(object value)
        {
            if (value == null) return "<null>";
            if (value is float f) return f.ToString("R", CultureInfo.InvariantCulture);
            if (value is double d) return d.ToString("R", CultureInfo.InvariantCulture);
            if (value is IFormattable fm) return fm.ToString(null, CultureInfo.InvariantCulture);
            return value.ToString();
        }

        private static object PartObject(string part, object difficulty, object career, object advanced, object commNet)
        {
            switch (part)
            {
                case PartDifficulty: return difficulty;
                case PartCareer: return career;
                case PartAdvanced: return advanced;
                case PartCommNet: return commNet;
                default: return null;
            }
        }

        /// <summary>
        /// Reads every key from the four part objects (field first, then property). A missing
        /// part object reads "<no-part>", a missing member "<missing>"; the read never throws.
        /// </summary>
        internal static List<KeyValuePair<string, string>> Read(object difficulty, object career, object advanced, object commNet)
        {
            var result = new List<KeyValuePair<string, string>>(Keys.Length);
            foreach (var key in Keys)
            {
                object target = PartObject(key.Part, difficulty, career, advanced, commNet);
                string text;
                if (target == null) text = "<no-part>";
                else
                {
                    try
                    {
                        Type t = target.GetType();
                        FieldInfo fi = t.GetField(key.Member, MemberFlags);
                        if (fi != null) text = FormatValue(fi.GetValue(target));
                        else
                        {
                            PropertyInfo pi = t.GetProperty(key.Member, MemberFlags);
                            text = pi != null && pi.CanRead ? FormatValue(pi.GetValue(target, null)) : "<missing>";
                        }
                    }
                    catch (Exception ex)
                    {
                        text = "<error:" + ex.GetType().Name + ">";
                    }
                }
                result.Add(new KeyValuePair<string, string>(key.Name, text));
            }
            return result;
        }

        /// <summary>Keys whose value differs between two reads, as "key: before -> after".</summary>
        internal static List<string> Diff(List<KeyValuePair<string, string>> before, List<KeyValuePair<string, string>> after)
        {
            var changes = new List<string>();
            var b = ToMap(before);
            var a = ToMap(after);
            foreach (var key in Keys)
            {
                string bv, av;
                b.TryGetValue(key.Name, out bv);
                a.TryGetValue(key.Name, out av);
                if (!string.Equals(bv, av, StringComparison.Ordinal))
                    changes.Add(key.Name + ": " + (bv ?? "<absent>") + " -> " + (av ?? "<absent>"));
            }
            return changes;
        }

        private static Dictionary<string, string> ToMap(List<KeyValuePair<string, string>> values)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (values == null) return map;
            foreach (var kv in values) map[kv.Key] = kv.Value;
            return map;
        }

        /// <summary>
        /// The pass rule of step (d) over a default read and a read after the edits: every
        /// planned key changed to its planned value and no other key moved. Returns the
        /// problems; empty means pass.
        /// </summary>
        internal static List<string> CheckEdits(List<KeyValuePair<string, string>> defaults,
            List<KeyValuePair<string, string>> edited, KeyValuePair<string, string>[] planned)
        {
            var problems = new List<string>();
            var d = ToMap(defaults);
            var e = ToMap(edited);
            var plannedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in planned ?? new KeyValuePair<string, string>[0])
            {
                plannedKeys.Add(p.Key);
                string ev;
                if (!e.TryGetValue(p.Key, out ev)) problems.Add(p.Key + " absent");
                else if (!ValuesEqual(ev, p.Value)) problems.Add(p.Key + " is " + ev + ", planned " + p.Value);
                string dv;
                if (d.TryGetValue(p.Key, out dv) && ValuesEqual(dv, p.Value))
                    problems.Add(p.Key + " default already " + dv + " (the edit proves nothing)");
            }
            foreach (var key in Keys)
            {
                if (plannedKeys.Contains(key.Name)) continue;
                string dv, ev;
                d.TryGetValue(key.Name, out dv);
                e.TryGetValue(key.Name, out ev);
                if (!string.Equals(dv, ev, StringComparison.Ordinal))
                    problems.Add(key.Name + " moved " + (dv ?? "<absent>") + " -> " + (ev ?? "<absent>"));
            }
            return problems;
        }

        /// <summary>Equal as text, or as numbers when both parse (0.8 equals 0.800000011920929).</summary>
        internal static bool ValuesEqual(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            double x, y;
            if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                return Math.Abs(x - y) <= 1e-6 * Math.Max(1.0, Math.Abs(y));
            return false;
        }

        /// <summary>
        /// Sets one key on the matching part object from invariant text (bool, float, double,
        /// int, enum). Returns false with a reason when the part, member or parse fails.
        /// </summary>
        internal static bool TrySet(object difficulty, object career, object advanced, object commNet,
            string keyName, string valueText, out string reason)
        {
            reason = null;
            int dot = keyName == null ? -1 : keyName.IndexOf('.');
            if (dot <= 0) { reason = "bad-key"; return false; }
            string part = keyName.Substring(0, dot);
            string member = keyName.Substring(dot + 1);
            object target = PartObject(part, difficulty, career, advanced, commNet);
            if (target == null) { reason = "no-part"; return false; }
            Type t = target.GetType();
            FieldInfo fi = t.GetField(member, MemberFlags);
            PropertyInfo pi = fi == null ? t.GetProperty(member, MemberFlags) : null;
            Type valueType = fi != null ? fi.FieldType : (pi != null && pi.CanWrite ? pi.PropertyType : null);
            if (valueType == null) { reason = "no-member"; return false; }
            object parsed;
            if (!TryParse(valueType, valueText, out parsed)) { reason = "parse"; return false; }
            try
            {
                if (fi != null) fi.SetValue(target, parsed);
                else pi.SetValue(target, parsed, null);
            }
            catch (Exception ex)
            {
                reason = "set:" + ex.GetType().Name;
                return false;
            }
            return true;
        }

        private static bool TryParse(Type type, string text, out object value)
        {
            value = null;
            if (text == null) return false;
            var inv = CultureInfo.InvariantCulture;
            if (type == typeof(bool))
            {
                bool b;
                if (!bool.TryParse(text, out b)) return false;
                value = b;
                return true;
            }
            if (type == typeof(float))
            {
                float f;
                if (!float.TryParse(text, NumberStyles.Float, inv, out f)) return false;
                value = f;
                return true;
            }
            if (type == typeof(double))
            {
                double d;
                if (!double.TryParse(text, NumberStyles.Float, inv, out d)) return false;
                value = d;
                return true;
            }
            if (type == typeof(int))
            {
                int i;
                if (!int.TryParse(text, NumberStyles.Integer, inv, out i)) return false;
                value = i;
                return true;
            }
            if (type.IsEnum)
            {
                try { value = Enum.Parse(type, text, true); return true; }
                catch (ArgumentException) { return false; }
            }
            if (type == typeof(string)) { value = text; return true; }
            return false;
        }

        /// <summary>The four part objects of a live GameParameters (custom nodes via CustomParams).</summary>
        internal static object[] PartsOf(GameParameters parameters)
        {
            if (parameters == null) return new object[4];
            object adv = null, comm = null;
            try { adv = parameters.CustomParams<GameParameters.AdvancedParams>(); } catch (Exception) { }
            try { comm = parameters.CustomParams<CommNet.CommNetParams>(); } catch (Exception) { }
            return new object[] { parameters.Difficulty, parameters.Career, adv, comm };
        }

        internal static List<KeyValuePair<string, string>> ReadLive(GameParameters parameters)
        {
            object[] p = PartsOf(parameters);
            return Read(p[0], p[1], p[2], p[3]);
        }
    }
}
