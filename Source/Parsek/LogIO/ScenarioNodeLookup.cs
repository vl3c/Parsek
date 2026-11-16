using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Finds a scenario module's stored node in a parsed game: over the game's
    /// <see cref="ProtoScenarioModule"/> list, or over a save file's GAME node and its
    /// <c>SCENARIO</c> children. The FIRST node whose name matches wins, ordinal and
    /// case-sensitive; a node or proto with no name never matches. Every lookup is pure: it
    /// logs nothing, so each caller keeps its own log line for "not found".
    ///
    /// <para>The ledger ground-truth parser (<c>CareerSaveParser</c>) keeps its own reader on
    /// purpose: it must read a save independently of the production code it checks.</para>
    /// </summary>
    internal static class ScenarioNodeLookup
    {
        /// <summary>
        /// The stored name of Parsek's scenario module (the class name stock writes as the
        /// <c>SCENARIO</c> node's <c>name</c>). A literal, not <c>nameof</c>, because this floor
        /// module may not name the Controllers type; a unit test pins the two equal.
        /// </summary>
        internal const string ParsekScenarioName = "ParsekScenario";

        private const string ScenarioNodeName = "SCENARIO";
        private const string NameValue = "name";

        /// <summary>
        /// The data node of the first proto in <paramref name="scenarios"/> whose
        /// <c>moduleName</c> is <paramref name="moduleName"/>; null for a null list or no match.
        /// Null entries are skipped.
        /// </summary>
        internal static ConfigNode FindInProtoList(IList<ProtoScenarioModule> scenarios, string moduleName)
        {
            if (scenarios == null)
                return null;
            for (int i = 0; i < scenarios.Count; i++)
            {
                ProtoScenarioModule proto = scenarios[i];
                if (proto != null && string.Equals(proto.moduleName, moduleName, StringComparison.Ordinal))
                    return proto.GetData();
            }
            return null;
        }

        /// <summary>
        /// The first <c>SCENARIO</c> child of <paramref name="game"/> whose <c>name</c> value is
        /// <paramref name="moduleName"/>; null for a null node or no match. The caller passes
        /// the GAME node itself (a save file's root unwraps through <c>GetNode("GAME")</c>).
        /// </summary>
        internal static ConfigNode FindInGameNode(ConfigNode game, string moduleName)
        {
            ConfigNode[] scenarios = EnumerateScenarioNodes(game);
            for (int i = 0; i < scenarios.Length; i++)
            {
                if (string.Equals(scenarios[i].GetValue(NameValue), moduleName, StringComparison.Ordinal))
                    return scenarios[i];
            }
            return null;
        }

        /// <summary>
        /// Every <c>SCENARIO</c> child of <paramref name="game"/> in file order; empty (never
        /// null) for a null node.
        /// </summary>
        internal static ConfigNode[] EnumerateScenarioNodes(ConfigNode game)
        {
            if (game == null)
                return new ConfigNode[0];
            return game.GetNodes(ScenarioNodeName) ?? new ConfigNode[0];
        }
    }
}
