using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The goal identity of a stock contract whose goal stock offers at most once per career
    /// (design 3.15 step 8, owner ruling 2026-10-27, todo CONTRACT-TWIN-GUID-SEEDED-TYPES).
    /// <see cref="ContractTwinRule"/> matches a twin on this key OR on stock's
    /// <c>Contract.ContractID</c>; this key exists for the types whose ContractID is seeded by
    /// the guid and so never repeats on a regenerated copy.
    ///
    /// <para>Only three stock types carry one (decompiled KSP 1.12.5):</para>
    /// <list type="bullet">
    /// <item><c>FinePrint.Contracts.ExplorationContract</c>: one token per
    /// <c>ProgressTrackingParameter</c> milestone (type + body). Seeds come only from
    /// incomplete progress nodes and <c>MeetRequirements</c> ends an offer once any of its
    /// milestones is complete, so each milestone is paid by at most one contract per
    /// career.</item>
    /// <item><c>Contracts.Templates.PlantFlag</c> at Significant prestige: the target body.
    /// Generated only for a body with no manned landing yet, and at most one current per body
    /// (<c>AnyCurrentContracts(TargetBody)</c>); completing it lands a crew there, so it is
    /// never offered again. The Trivial plant flag is offered again on a flagged body once its
    /// crew left, so it gets no key.</item>
    /// <item><c>FinePrint.Contracts.CometSampleContract</c>: the comet's persistent id. Refused
    /// while a current OR a completed comet sample names that comet
    /// (<c>GetCompletedContracts</c>).</item>
    /// </list>
    /// <para>Every other type gets no key: either its ContractID already names the goal (part
    /// test ONCEPERPART / BODYANDSITUATION, science collection, the Breaking Ground science
    /// contracts), or stock offers the same goal again (grand tour, rescue, tourism, satellite,
    /// station, base, ISRU, ARM, survey, construction, repair, part test ALWAYS, the Trivial
    /// plant flag), or it is not stock (Contract Configurator). A key there would block a
    /// legitimate repeat.</para>
    ///
    /// <para>Form: <c>Type:token[,token...]</c>, tokens sorted and distinct, no <c>;</c> or
    /// <c>=</c> (it rides the accept event's detail). Two keys match when the type is the same
    /// and they share a token (<see cref="Overlaps"/>). Body tokens use the body's
    /// <c>flightGlobalsIndex</c>, the value stock itself saves.</para>
    /// </summary>
    internal static class ContractGoalKey
    {
        internal const string ExplorationType = "ExplorationContract";
        internal const string PlantFlagType = "PlantFlag";
        internal const string CometSampleType = "CometSampleContract";

        /// <summary>The <c>ContractPrestige.Significant</c> value stock saves as <c>prestige</c>.</summary>
        internal const int SignificantPrestige = 1;

        private const char TypeSeparator = ':';
        private const char TokenSeparator = ',';
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>True for a contract type name that can carry a goal key.</summary>
        internal static bool IsGoalType(string contractTypeName)
        {
            return string.Equals(contractTypeName, ExplorationType, StringComparison.Ordinal)
                || string.Equals(contractTypeName, PlantFlagType, StringComparison.Ordinal)
                || string.Equals(contractTypeName, CometSampleType, StringComparison.Ordinal);
        }

        /// <summary>Exploration: one token per (milestone type, body index); null when none.</summary>
        internal static string ForExploration(IEnumerable<KeyValuePair<string, int>> milestones)
        {
            if (milestones == null)
                return null;
            var tokens = new List<string>();
            foreach (var m in milestones)
            {
                if (string.IsNullOrEmpty(m.Key) || m.Value < 0)
                    continue;
                tokens.Add(Clean(m.Key) + "@" + m.Value.ToString(IC));
            }
            return Compose(ExplorationType, tokens);
        }

        /// <summary>Plant flag: the body index at Significant prestige, else null.</summary>
        internal static string ForPlantFlag(int prestige, int bodyIndex)
        {
            if (prestige != SignificantPrestige || bodyIndex < 0)
                return null;
            return Compose(PlantFlagType, new[] { "body@" + bodyIndex.ToString(IC) });
        }

        /// <summary>Comet sample: the comet's persistent id; null for 0 (unknown).</summary>
        internal static string ForCometSample(uint cometPersistentId)
        {
            if (cometPersistentId == 0)
                return null;
            return Compose(CometSampleType, new[] { "comet@" + cometPersistentId.ToString(IC) });
        }

        /// <summary>
        /// Pure: the goal key of a contract as stock saves it (<c>Contract.Save</c>: <c>type</c>,
        /// <c>prestige</c>, the template's own values and its <c>PARAM</c> nodes), or null.
        /// </summary>
        internal static string FromContractNode(ConfigNode node)
        {
            if (node == null)
                return null;
            string type = node.GetValue("type");
            if (!IsGoalType(type))
                return null;
            int index;
            switch (type)
            {
                case ExplorationType:
                {
                    var milestones = new List<KeyValuePair<string, int>>();
                    ConfigNode[] pars = node.GetNodes("PARAM");
                    for (int i = 0; i < pars.Length; i++)
                    {
                        if (!string.Equals(pars[i].GetValue("name"), "ProgressTrackingParameter", StringComparison.Ordinal))
                            continue;
                        if (!int.TryParse(pars[i].GetValue("targetBody"), NumberStyles.Integer, IC, out index))
                            continue;
                        milestones.Add(new KeyValuePair<string, int>(pars[i].GetValue("targetType"), index));
                    }
                    return ForExploration(milestones);
                }
                case PlantFlagType:
                {
                    int prestige;
                    if (!int.TryParse(node.GetValue("prestige"), NumberStyles.Integer, IC, out prestige))
                        return null;
                    if (!int.TryParse(node.GetValue("body"), NumberStyles.Integer, IC, out index))
                        return null;
                    return ForPlantFlag(prestige, index);
                }
                default:
                {
                    uint pid;
                    if (!uint.TryParse(node.GetValue("cometPersistentId"), NumberStyles.Integer, IC, out pid))
                        return null;
                    return ForCometSample(pid);
                }
            }
        }

        /// <summary>The type part of a key, or null for a malformed one.</summary>
        internal static string TypeOf(string goalKey)
        {
            if (string.IsNullOrEmpty(goalKey))
                return null;
            int sep = goalKey.IndexOf(TypeSeparator);
            return sep > 0 ? goalKey.Substring(0, sep) : null;
        }

        /// <summary>
        /// Pure: true when two keys name a common goal: the same contract type and at least one
        /// shared token. A null or malformed key never matches.
        /// </summary>
        internal static bool Overlaps(string a, string b)
        {
            string typeA = TypeOf(a);
            string typeB = TypeOf(b);
            if (typeA == null || typeB == null || !string.Equals(typeA, typeB, StringComparison.Ordinal))
                return false;
            string[] tokensA = a.Substring(typeA.Length + 1).Split(TokenSeparator);
            string[] tokensB = b.Substring(typeB.Length + 1).Split(TokenSeparator);
            for (int i = 0; i < tokensA.Length; i++)
            {
                if (tokensA[i].Length == 0)
                    continue;
                for (int j = 0; j < tokensB.Length; j++)
                    if (string.Equals(tokensA[i], tokensB[j], StringComparison.Ordinal))
                        return true;
            }
            return false;
        }

        private static string Compose(string type, IEnumerable<string> tokens)
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string t in tokens)
                if (!string.IsNullOrEmpty(t))
                    set.Add(t);
            if (set.Count == 0)
                return null;
            return type + TypeSeparator + string.Join(TokenSeparator.ToString(), set);
        }

        /// <summary>Strips the characters the key form reserves from a stock value.</summary>
        private static string Clean(string value)
        {
            return value.Replace(TypeSeparator, '_').Replace(TokenSeparator, '_')
                .Replace(';', '_').Replace('=', '_').Trim();
        }
    }
}
