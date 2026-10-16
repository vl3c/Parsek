using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>What <see cref="ConstructionGhostShape.Apply"/> did to a member's ghost.</summary>
    internal enum ConstructionGhostShapeAction
    {
        /// <summary>The member has no ghost snapshot; nothing to shape.</summary>
        NoGhostSnapshot,
        /// <summary>A detach whose part the ghost already does not carry.</summary>
        NotInGhost,
        /// <summary>A detach: the part's ghost node is hidden from this UT.</summary>
        Removed,
        /// <summary>An attach where the ghost already carries the part at the same pose.</summary>
        Present,
        /// <summary>An attach of a part the ghost did not carry; a copy was appended.</summary>
        Added,
        /// <summary>An attach of a part the ghost carries at another pose; a stand-in node was appended.</summary>
        Moved,
        /// <summary>An attach whose part the fresh snapshot does not carry.</summary>
        MissingInFresh
    }

    internal struct ConstructionGhostShapeResult
    {
        public ConstructionGhostShapeAction Action;
        /// <summary>The ghost part the event names (0 when no event was written).</summary>
        public uint GhostPid;
        public bool EventWritten;
    }

    /// <summary>
    /// Keeps a cargo partner's ghost in step with EVA construction on it (design 13.10 /
    /// PART_ADDED, PART_REMOVED). A member recording renders from ONE ghost snapshot, so a
    /// part attached after that snapshot is appended to it and shown from the attach UT
    /// (<see cref="PartEventType.InventoryPartPlaced"/>, so it starts hidden), and a detached
    /// part is hidden from the detach UT (<see cref="PartEventType.InventoryPartRemoved"/>).
    /// Ghost part objects are named by persistentId, so a part re-attached at a different
    /// pose gets a stand-in node with its own deterministic pid and a
    /// <see cref="GhostOfKey"/> value naming the live part; later events on that live part
    /// resolve to the newest node. Stock only detaches a leaf part with no linked compound
    /// parts and attaches one held part (decompiled <c>EVAConstructionModeEditor</c>,
    /// KSP 1.12.5), so one node per change is the whole shape change. Pure over ConfigNode.
    /// </summary>
    internal static class ConstructionGhostShape
    {
        internal const string GhostOfKey = "parsekGhostOf";
        internal const double PoseTolerance = 1e-3;

        /// <summary>
        /// Applies one construction change to <paramref name="member"/>: mutates its
        /// GhostVisualSnapshot when a node is needed and appends one part event in stable
        /// UT order. <paramref name="fresh"/> is the vessel's snapshot taken after the change.
        /// </summary>
        internal static ConstructionGhostShapeResult Apply(
            Recording member, ConfigNode fresh, bool attached, uint livePid, string partName, double ut)
        {
            var result = new ConstructionGhostShapeResult();
            ConfigNode ghost = member != null ? member.GhostVisualSnapshot : null;
            if (ghost == null || livePid == 0)
            {
                result.Action = ConstructionGhostShapeAction.NoGhostSnapshot;
                return result;
            }

            PartEventType eventType;
            if (attached)
            {
                ConstructionGhostShapeAction action;
                result.GhostPid = MergeAttachedPart(ghost, fresh, livePid, out action);
                result.Action = action;
                eventType = PartEventType.InventoryPartPlaced;
            }
            else
            {
                result.GhostPid = ResolveGhostPid(ghost, livePid);
                result.Action = result.GhostPid != 0
                    ? ConstructionGhostShapeAction.Removed
                    : ConstructionGhostShapeAction.NotInGhost;
                eventType = PartEventType.InventoryPartRemoved;
            }

            if (result.GhostPid == 0)
                return result;

            GroundPartPlacement.AppendPartEventSorted(member,
                GroundPartPlacement.BuildInventoryEvent(eventType, ut, result.GhostPid, partName));
            result.EventWritten = true;
            return result;
        }

        /// <summary>
        /// Index of the ghost PART node that currently renders live part
        /// <paramref name="livePid"/>: the LAST node whose persistentId or
        /// <see cref="GhostOfKey"/> equals it. -1 when none.
        /// </summary>
        internal static int FindGhostPartIndex(ConfigNode[] ghostParts, uint livePid)
        {
            if (ghostParts == null || livePid == 0) return -1;
            for (int i = ghostParts.Length - 1; i >= 0; i--)
            {
                if (ReadPid(ghostParts[i], "persistentId") == livePid
                    || ReadPid(ghostParts[i], GhostOfKey) == livePid)
                    return i;
            }
            return -1;
        }

        internal static uint ResolveGhostPid(ConfigNode ghost, uint livePid)
        {
            ConfigNode[] parts = ghost != null ? ghost.GetNodes("PART") : null;
            int idx = FindGhostPartIndex(parts, livePid);
            return idx >= 0 ? ReadPid(parts[idx], "persistentId") : 0u;
        }

        /// <summary>
        /// Makes the ghost carry live part <paramref name="livePid"/> where the fresh snapshot
        /// has it, and returns the ghost pid that renders it (0 when the fresh snapshot lacks
        /// the part). Appends a node only when the ghost does not already carry the part at
        /// the same parent and pose.
        /// </summary>
        internal static uint MergeAttachedPart(
            ConfigNode ghost, ConfigNode fresh, uint livePid, out ConstructionGhostShapeAction action)
        {
            ConfigNode[] freshParts = fresh != null ? fresh.GetNodes("PART") : null;
            int freshIdx = -1;
            if (freshParts != null)
            {
                for (int i = 0; i < freshParts.Length; i++)
                {
                    if (ReadPid(freshParts[i], "persistentId") == livePid) { freshIdx = i; break; }
                }
            }
            if (freshIdx < 0)
            {
                action = ConstructionGhostShapeAction.MissingInFresh;
                return 0;
            }

            ConfigNode freshNode = freshParts[freshIdx];
            ConfigNode[] ghostParts = ghost.GetNodes("PART");
            int ghostIdx = FindGhostPartIndex(ghostParts, livePid);
            uint freshParentLive = ParentLivePid(freshParts, freshNode);

            if (ghostIdx >= 0)
            {
                ConfigNode ghostNode = ghostParts[ghostIdx];
                if (ParentLivePid(ghostParts, ghostNode) == freshParentLive
                    && PoseMatches(ghostNode, freshNode))
                {
                    action = ConstructionGhostShapeAction.Present;
                    return ReadPid(ghostNode, "persistentId");
                }
            }

            ConfigNode copy = freshNode.CreateCopy();
            uint ghostPid = livePid;
            if (ghostIdx >= 0)
            {
                ghostPid = SyntheticPid(ghostParts, livePid);
                copy.SetValue("persistentId", ghostPid.ToString(CultureInfo.InvariantCulture), true);
                copy.SetValue(GhostOfKey, livePid.ToString(CultureInfo.InvariantCulture), true);
                action = ConstructionGhostShapeAction.Moved;
            }
            else
            {
                copy.RemoveValues(GhostOfKey);
                action = ConstructionGhostShapeAction.Added;
            }

            RemapIndexValue(copy, "parent", freshParts, ghostParts);
            RemapLinkValues(copy, "attN", freshParts, ghostParts);
            RemapLinkValues(copy, "srfN", freshParts, ghostParts);
            ghost.AddNode(copy);
            return ghostPid;
        }

        /// <summary>
        /// A pid for a stand-in node, unique among <paramref name="ghostParts"/> and nonzero,
        /// derived only from the live pid and the node count so a replay is deterministic.
        /// </summary>
        internal static uint SyntheticPid(ConfigNode[] ghostParts, uint livePid)
        {
            var taken = new HashSet<uint>();
            int count = ghostParts != null ? ghostParts.Length : 0;
            for (int i = 0; i < count; i++)
                taken.Add(ReadPid(ghostParts[i], "persistentId"));

            uint h = 2166136261u;
            h = (h ^ livePid) * 16777619u;
            h = (h ^ (uint)count) * 16777619u;
            while (h == 0 || taken.Contains(h) || h == livePid)
                h = (h ^ 0x9E3779B9u) * 16777619u;
            return h;
        }

        internal static bool PoseMatches(ConfigNode a, ConfigNode b)
        {
            return VectorsMatch(GhostVisualBuilder.GetPartPositionRaw(a), GhostVisualBuilder.GetPartPositionRaw(b))
                && VectorsMatch(GhostVisualBuilder.GetPartRotationRaw(a), GhostVisualBuilder.GetPartRotationRaw(b));
        }

        private static bool VectorsMatch(string a, string b)
        {
            double[] va = ParseComponents(a);
            double[] vb = ParseComponents(b);
            if (va == null || vb == null || va.Length != vb.Length)
                return string.Equals(a, b, StringComparison.Ordinal);
            for (int i = 0; i < va.Length; i++)
            {
                if (Math.Abs(va[i] - vb[i]) > PoseTolerance)
                    return false;
            }
            return true;
        }

        private static double[] ParseComponents(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string[] parts = raw.Split(',');
            var values = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                    return null;
            }
            return values;
        }

        /// <summary>The live pid of a node's parent (a stand-in resolves to the part it stands for).</summary>
        private static uint ParentLivePid(ConfigNode[] parts, ConfigNode node)
        {
            int parentIdx;
            string raw = node.GetValue("parent");
            if (raw == null
                || !int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parentIdx)
                || parentIdx < 0 || parentIdx >= parts.Length)
                return 0;
            return LivePidOf(parts[parentIdx]);
        }

        private static uint LivePidOf(ConfigNode node)
        {
            uint ghostOf = ReadPid(node, GhostOfKey);
            return ghostOf != 0 ? ghostOf : ReadPid(node, "persistentId");
        }

        private static int MapIndex(int freshIdx, ConfigNode[] freshParts, ConfigNode[] ghostParts)
        {
            if (freshIdx < 0 || freshIdx >= freshParts.Length) return -1;
            return FindGhostPartIndex(ghostParts, ReadPid(freshParts[freshIdx], "persistentId"));
        }

        private static void RemapIndexValue(
            ConfigNode node, string key, ConfigNode[] freshParts, ConfigNode[] ghostParts)
        {
            string raw = node.GetValue(key);
            int idx;
            if (raw == null
                || !int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out idx))
                return;
            node.SetValue(key, MapIndex(idx, freshParts, ghostParts).ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Remaps "nodeId, partIndex[,extra]" link values (attN / srfN) by part pid.</summary>
        private static void RemapLinkValues(
            ConfigNode node, string key, ConfigNode[] freshParts, ConfigNode[] ghostParts)
        {
            string[] values = node.GetValues(key);
            if (values == null || values.Length == 0) return;
            node.RemoveValues(key);
            for (int v = 0; v < values.Length; v++)
            {
                string raw = values[v];
                string[] fields = raw.Split(',');
                int idx;
                if (fields.Length >= 2
                    && int.TryParse(fields[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out idx)
                    && idx >= 0)
                {
                    fields[1] = " " + MapIndex(idx, freshParts, ghostParts).ToString(CultureInfo.InvariantCulture);
                    raw = string.Join(",", fields);
                }
                node.AddValue(key, raw);
            }
        }

        private static uint ReadPid(ConfigNode node, string key)
        {
            string raw = node != null ? node.GetValue(key) : null;
            uint pid;
            return raw != null
                && uint.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid)
                ? pid : 0u;
        }

        internal static string FormatLog(
            string recordingId, uint vesselPid, bool attached, uint livePid, string partName,
            double ut, ConstructionGhostShapeResult result, int ghostPartCount)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Construction ghost shape: rec={0} vesselPid={1} change={2} part='{3}' livePid={4} " +
                "ghostPid={5} action={6} event={7} ut={8:F2} ghostParts={9}",
                recordingId ?? "(null)", vesselPid, attached ? "attach" : "detach",
                partName ?? "(null)", livePid, result.GhostPid, result.Action,
                result.EventWritten
                    ? (attached ? "InventoryPartPlaced" : "InventoryPartRemoved")
                    : "none",
                ut, ghostPartCount);
        }
    }
}
