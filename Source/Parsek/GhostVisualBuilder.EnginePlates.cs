using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // Engine plates (ModuleDynamicNodes mount pattern) and the shroud a ModuleJettison picks
    // among several variant meshes.
    internal static partial class GhostVisualBuilder
    {
        /// <summary>
        /// Pure: which ModuleDynamicNodes mount meshes (the engine plate's N1 / N2 / ... face
        /// patterns) the ghost shows. Stock ToggleNodeSet activates only the MeshTransform of
        /// the persistent NodeSetIdx and deactivates every other set's; the prefab carries all
        /// of them active because OnStart never ran there. NODE_SET order comes from the PART
        /// config, NodeSetIdx from the snapshot (stock default 0). An index outside the list
        /// returns no decisions, leaving the prefab state.
        /// </summary>
        internal static List<KeyValuePair<string, bool>> ResolveDynamicNodeMeshStates(
            ConfigNode partConfig, ConfigNode partNode)
        {
            var result = new List<KeyValuePair<string, bool>>();
            ConfigNode cfg = FindModuleNode(partConfig, "ModuleDynamicNodes");
            if (cfg == null) return result;
            ConfigNode[] sets = cfg.GetNodes("NODE_SET");
            if (sets == null || sets.Length == 0) return result;

            int idx = 0;
            ConfigNode snap = FindModuleNode(partNode, "ModuleDynamicNodes");
            string raw = snap != null ? snap.GetValue("NodeSetIdx") : null;
            if (raw != null)
                int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out idx);
            if (idx < 0 || idx >= sets.Length) return result;

            for (int i = 0; i < sets.Length; i++)
            {
                string mesh = sets[i].GetValue("MeshTransform");
                if (string.IsNullOrEmpty(mesh)) continue;
                result.Add(new KeyValuePair<string, bool>(mesh.Trim(), i == idx));
            }
            return result;
        }

        /// <summary>Applies <see cref="ResolveDynamicNodeMeshStates"/> to the ghost part.</summary>
        internal static void ApplyDynamicNodeMeshVisibility(
            ConfigNode partConfig, ConfigNode partNode, Transform ghostModelNode,
            uint persistentId, string partName)
        {
            if (ghostModelNode == null) return;
            List<KeyValuePair<string, bool>> states = ResolveDynamicNodeMeshStates(partConfig, partNode);
            if (states.Count == 0) return;
            int shown = 0, hidden = 0, missing = 0;
            string shownName = null;
            for (int i = 0; i < states.Count; i++)
            {
                Transform t = FindTransformByName(ghostModelNode, states[i].Key);
                if (t == null) { missing++; continue; }
                t.gameObject.SetActive(states[i].Value);
                if (states[i].Value) { shown++; shownName = states[i].Key; }
                else hidden++;
            }
            ParsekLog.VerboseRateLimited("GhostVisual", $"dynamic_nodes_{partName}",
                $"  Engine plate mounts: part='{partName}' pid={persistentId} shown='{shownName ?? "none"}' " +
                $"hidden={hidden} missing={missing}", 60.0);
        }

        /// <summary>
        /// Pure: the shroud names a ModuleJettison actually drives. Stock keeps the one it
        /// found active (the selected variant's) in the persistent activejettisonName and
        /// toggles only that; without it every listed name is a candidate.
        /// </summary>
        internal static List<string> SelectJettisonNames(string jettisonNameList, string activeJettisonName)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(jettisonNameList)) return names;
            string[] split = jettisonNameList.Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < split.Length; i++)
            {
                string n = split[i].Trim();
                if (n.Length > 0 && !names.Contains(n)) names.Add(n);
            }
            string active = activeJettisonName != null ? activeJettisonName.Trim() : null;
            if (!string.IsNullOrEmpty(active) && names.Contains(active))
                return new List<string> { active };
            return names;
        }

        /// <summary>
        /// The snapshot's ModuleJettison MODULE nodes in order, so the n-th prefab
        /// ModuleJettison reads the n-th node.
        /// </summary>
        internal static List<ConfigNode> SnapshotModulesNamed(ConfigNode partNode, string moduleName)
        {
            var list = new List<ConfigNode>();
            ConfigNode[] modules = partNode != null ? partNode.GetNodes("MODULE") : null;
            if (modules == null) return list;
            for (int i = 0; i < modules.Length; i++)
                if (modules[i].GetValue("name") == moduleName)
                    list.Add(modules[i]);
            return list;
        }
    }
}
