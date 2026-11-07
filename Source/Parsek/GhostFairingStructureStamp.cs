using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Parsek
{
    /// <summary>
    /// Records, in a vessel snapshot, the live display state of a stock ModuleStructuralNode
    /// (the fairing interstage truss and cap plates) when it differs from stock's own rule.
    ///
    /// Stock evaluates the rule (ModuleStructuralNode.CheckDisplay) only at load and from
    /// DecoupleAction. Part.decouple fires the parent's DecoupleAction BEFORE it clears the
    /// parent's attach node, and Part.Die clears the node without firing it, so a live truss
    /// stays shown after its stage leaves, until the craft is loaded again. A snapshot taken
    /// then (every recording that starts at the separation) has an empty attN row, and the
    /// rule alone would hide the truss on the ghost. The stamp is written only in that
    /// disagreeing state, so an ordinary snapshot gains nothing. It is an additive MODULE key
    /// that stock's field loader ignores when a vessel is spawned from the snapshot.
    /// </summary>
    internal static class GhostFairingStructureStamp
    {
        internal const string ShownKey = "parsekStructureShown";
        internal const string ModuleName = "ModuleStructuralNode";

        /// <summary>
        /// Pure: writes the display state onto the moduleOrdinal-th ModuleStructuralNode
        /// MODULE of the PART whose persistentId is partPid. Returns false when no such
        /// part or module exists in the snapshot.
        /// </summary>
        internal static bool ApplyStamp(ConfigNode vesselNode, uint partPid, int moduleOrdinal, bool shown)
        {
            if (vesselNode == null || moduleOrdinal < 0) return false;
            ConfigNode[] parts = vesselNode.GetNodes("PART");
            for (int p = 0; p < parts.Length; p++)
            {
                uint pid;
                string raw = parts[p].GetValue("persistentId");
                if (raw == null
                    || !uint.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid)
                    || pid != partPid)
                    continue;
                ConfigNode[] modules = parts[p].GetNodes("MODULE");
                int ordinal = 0;
                for (int m = 0; m < modules.Length; m++)
                {
                    if (modules[m].GetValue("name") != ModuleName) continue;
                    if (ordinal == moduleOrdinal)
                    {
                        modules[m].SetValue(ShownKey, shown ? "True" : "False", true);
                        return true;
                    }
                    ordinal++;
                }
                return false;
            }
            return false;
        }

        /// <summary>
        /// Stamps every structure node of the live vessel whose shown state differs from
        /// stock's rule over its current attach nodes. Never throws; logs one summary.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void StampFromLiveVessel(Vessel vessel, ConfigNode vesselNode)
        {
            if (vessel == null || vesselNode == null || vessel.parts == null) return;
            int stamped = 0, missed = 0;
            try
            {
                for (int i = 0; i < vessel.parts.Count; i++)
                {
                    Part part = vessel.parts[i];
                    if (part == null) continue;
                    List<ModuleStructuralNode> nodes = part.FindModulesImplementing<ModuleStructuralNode>();
                    if (nodes == null || nodes.Count == 0) continue;
                    for (int n = 0; n < nodes.Count; n++)
                    {
                        ModuleStructuralNode node = nodes[n];
                        if (node == null || node.structTransform == null) continue;
                        bool live = node.structTransform.gameObject.activeSelf;
                        bool rule = GhostVisualBuilder.ComputeStructureNodeVisible(
                            node.visibilityState, node.spawnManually, node.spawnState,
                            node.reverseVisibility, AnyNodeAttached(part, node.attachNodeNames));
                        if (live == rule) continue;
                        if (ApplyStamp(vesselNode, part.persistentId, n, live)) stamped++;
                        else missed++;
                    }
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Snapshot",
                    $"Structure display stamp failed for vessel pid={vessel.persistentId}: {ex.GetType().Name}: {ex.Message}");
                return;
            }
            if (stamped > 0 || missed > 0)
                ParsekLog.Verbose("Snapshot",
                    $"Structure display stamp: vessel pid={vessel.persistentId} stamped={stamped} missed={missed} " +
                    "(live truss differs from the stock rule, e.g. kept after its stage separated)");
        }

        private static bool AnyNodeAttached(Part part, string attachNodeNames)
        {
            if (string.IsNullOrEmpty(attachNodeNames)) return false;
            string[] names = attachNodeNames.Split(',');
            for (int i = 0; i < names.Length; i++)
            {
                AttachNode an = part.FindAttachNode(names[i].Trim());
                if (an != null && an.attachedPart != null) return true;
            }
            return false;
        }
    }
}
