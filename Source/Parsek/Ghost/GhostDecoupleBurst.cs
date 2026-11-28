using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// One decoupler module on a part, as the part prefab holds it (the fields
    /// <c>ModuleDecouplerBase</c> reads when it fires).
    /// </summary>
    internal struct GhostDecouplerModuleFacts
    {
        /// <summary>The module's index on the part (the snapshot's MODULE order).</summary>
        public int ModuleIndex;
        public string ModuleName;
        /// <summary><c>explosiveNodeID</c>: an attach node id, or "srf" for the surface attach node.</summary>
        public string ExplosiveNodeId;
        /// <summary>Whether the module separates every neighbour (<c>isOmniDecoupler</c> on a
        /// <c>ModuleDecouple</c>; an anchored decoupler never reads it).</summary>
        public bool IsOmni;
        /// <summary><c>fxGroupName</c>: the FX group the module bursts when it fires.</summary>
        public string FxGroupName;
    }

    /// <summary>Why a recorded joint break did or did not burst a decoupler's FX on the ghost.</summary>
    internal enum GhostDecoupleBurstReason
    {
        /// <summary>The ghost has no snapshot to read the part tree from.</summary>
        NoSnapshot = 0,
        /// <summary>The broken joint's child is not a part of the ghost's snapshot.</summary>
        PartNotInSnapshot = 1,
        /// <summary>The joint child is a decoupler whose explosive node held the parent.</summary>
        ChildDecouplerFired = 2,
        /// <summary>The joint parent is a decoupler whose explosive node held the child.</summary>
        ParentDecouplerFired = 3,
        /// <summary>Neither side of the joint is a decoupler: a part that just broke off.</summary>
        PlainJointBreak = 4,
        /// <summary>A side is a decoupler, but its explosive node holds another part.</summary>
        DecouplerFacesElsewhere = 5,
        /// <summary>A side is a decoupler that had already fired when the recording started.</summary>
        DecouplerAlreadyFired = 6,
    }

    /// <summary>The answer for one recorded joint break.</summary>
    internal struct GhostDecoupleBurstChoice
    {
        public GhostDecoupleBurstReason Reason;
        public uint ChildPid;
        public uint ParentPid;
        /// <summary>The decoupler that fired, 0 for none.</summary>
        public uint DecouplerPid;
        public string DecouplerPartName;
        public GhostDecouplerModuleFacts Module;

        public bool Fires => DecouplerPid != 0;
    }

    /// <summary>
    /// The part tree and attach nodes of a ghost's snapshot, read once on the ghost's first
    /// recorded joint break (design-part-ghost-parity.md G.7, Y4).
    /// </summary>
    internal sealed class GhostDecoupleTopology
    {
        private readonly ConfigNode[] partNodes;
        private readonly uint[] pidByIndex;
        private readonly Dictionary<uint, int> indexByPid;
        private readonly Func<string, string> partNameOf;

        private GhostDecoupleTopology(ConfigNode[] partNodes, uint[] pidByIndex, Dictionary<uint, int> indexByPid,
            Func<string, string> partNameOf)
        {
            this.partNodes = partNodes;
            this.pidByIndex = pidByIndex;
            this.indexByPid = indexByPid;
            this.partNameOf = partNameOf;
        }

        /// <summary>The explosive node id stock reads as the part's surface attach node.</summary>
        internal const string SurfaceNodeId = "srf";

        internal int PartCount => partNodes.Length;

        /// <summary>
        /// Reads the snapshot's PART nodes; null for a null snapshot. Pure. <paramref name="partNameOf"/>
        /// turns a snapshot part name into the runtime one (the ghost builder's own reader; null keeps
        /// the name as written).
        /// </summary>
        internal static GhostDecoupleTopology Parse(ConfigNode snapshot, Func<string, string> partNameOf)
        {
            if (snapshot == null) return null;
            ConfigNode[] nodes = snapshot.GetNodes("PART") ?? new ConfigNode[0];
            var pids = new uint[nodes.Length];
            var index = new Dictionary<uint, int>();
            for (int i = 0; i < nodes.Length; i++)
            {
                string pidStr = nodes[i].GetValue("persistentId");
                if (pidStr != null)
                    uint.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out pids[i]);
                if (pids[i] != 0 && !index.ContainsKey(pids[i]))
                    index[pids[i]] = i;
            }
            return new GhostDecoupleTopology(nodes, pids, index, partNameOf);
        }

        internal bool Contains(uint pid) => pid != 0 && indexByPid.ContainsKey(pid);

        /// <summary>The part's runtime name (a trailing numeric suffix dropped), or null.</summary>
        internal string PartName(uint pid)
        {
            if (!indexByPid.TryGetValue(pid, out int i)) return null;
            ConfigNode n = partNodes[i];
            string raw = n.GetValue("name") ?? n.GetValue("part");
            return partNameOf != null ? partNameOf(raw) : raw;
        }

        /// <summary>The part's parent in the snapshot tree; 0 for the root or an unknown part.</summary>
        internal uint ParentPid(uint pid)
        {
            if (!indexByPid.TryGetValue(pid, out int i)) return 0;
            return PidAt(ParseIndex(partNodes[i].GetValue("parent")), i);
        }

        /// <summary>
        /// The part attached at <paramref name="nodeId"/> of <paramref name="pid"/>, 0 for none:
        /// the snapshot's <c>attN = id, index</c> lines, or its <c>srfN = srfAttach, index</c> line
        /// for "srf" (stock's <c>ModuleDecouplerBase</c> takes the surface attach node for "srf").
        /// </summary>
        internal uint AttachedPid(uint pid, string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId) || !indexByPid.TryGetValue(pid, out int i)) return 0;
            ConfigNode n = partNodes[i];
            if (nodeId == SurfaceNodeId)
                return PidAt(ParseAttachIndex(n.GetValue("srfN"), null), i);
            string[] lines = n.GetValues("attN");
            if (lines == null) return 0;
            for (int k = 0; k < lines.Length; k++)
            {
                int idx = ParseAttachIndex(lines[k], nodeId);
                if (idx != int.MinValue) return PidAt(idx, i);
            }
            return 0;
        }

        /// <summary>
        /// The snapshot's <c>isDecoupled</c> for the module at <paramref name="moduleIndex"/> named
        /// <paramref name="moduleName"/> (the first MODULE of that name when the index does not
        /// match); false when absent.
        /// </summary>
        internal bool ModuleIsDecoupled(uint pid, int moduleIndex, string moduleName)
        {
            if (!indexByPid.TryGetValue(pid, out int i)) return false;
            ConfigNode[] modules = partNodes[i].GetNodes("MODULE");
            if (modules == null) return false;
            ConfigNode m = moduleIndex >= 0 && moduleIndex < modules.Length
                && modules[moduleIndex].GetValue("name") == moduleName
                ? modules[moduleIndex] : null;
            for (int k = 0; m == null && k < modules.Length; k++)
                if (modules[k].GetValue("name") == moduleName) m = modules[k];
            return m != null && string.Equals(m.GetValue("isDecoupled"), "True", StringComparison.OrdinalIgnoreCase);
        }

        private uint PidAt(int idx, int self)
        {
            if (idx < 0 || idx >= pidByIndex.Length || idx == self) return 0;
            return pidByIndex[idx];
        }

        private static int ParseIndex(string s)
        {
            if (s == null) return -1;
            return int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1;
        }

        /// <summary>
        /// The part index of one <c>attN</c> / <c>srfN</c> value ("id, index[, ...]");
        /// int.MinValue when <paramref name="wantId"/> is given and the line names another node.
        /// </summary>
        private static int ParseAttachIndex(string line, string wantId)
        {
            if (line == null) return wantId == null ? -1 : int.MinValue;
            string[] f = line.Split(',');
            if (wantId != null && (f.Length < 1 || f[0].Trim() != wantId)) return int.MinValue;
            return f.Length >= 2 ? ParseIndex(f[1]) : -1;
        }
    }

    /// <summary>
    /// Y4 (design-part-ghost-parity.md G.7, G.8 item 4): a recorded joint break draws a decoupler's
    /// own stock FX group on the ghost only when stock would have fired it. Decompiled
    /// <c>ModuleDecouple.OnDecouple</c> / <c>ModuleAnchoredDecoupler.OnDecouple</c> (KSP 1.12.5):
    /// the module bursts its FX group (<c>fx.Burst()</c>, a <c>Play()</c> of each emitter) and
    /// then separates the part on its explosive node: itself from its parent when the node holds
    /// the parent, else the attached part from itself; an omni <c>ModuleDecouple</c> separates
    /// every child and its parent. The joint the recorder wrote is that separation, so the
    /// decoupler that fired is the side of the broken joint whose explosive node held the other
    /// side. A joint that broke anywhere else (<c>Part.decouple()</c> on a plain part, a
    /// structural failure) has no decoupler there and stock draws nothing.
    /// </summary>
    internal static class GhostDecoupleBurst
    {
        /// <summary>Bounds on how long a burst's object is kept, seconds.</summary>
        internal const float MinLifetimeSeconds = 1f;
        internal const float MaxLifetimeSeconds = 10f;
        internal const float DefaultLifetimeSeconds = 5f;

        /// <summary>
        /// Which decoupler, if any, fired the recorded joint break of <paramref name="childPid"/>.
        /// The child side is read first (a decoupler below its stage, the usual build), then the
        /// parent. Pure: <paramref name="modulesOf"/> gives a part name's decoupler modules (none
        /// for a plain part).
        /// </summary>
        internal static GhostDecoupleBurstChoice ResolveFiredDecoupler(
            GhostDecoupleTopology topology, uint childPid,
            Func<string, List<GhostDecouplerModuleFacts>> modulesOf)
        {
            var choice = new GhostDecoupleBurstChoice { ChildPid = childPid };
            if (topology == null) { choice.Reason = GhostDecoupleBurstReason.NoSnapshot; return choice; }
            if (!topology.Contains(childPid)) { choice.Reason = GhostDecoupleBurstReason.PartNotInSnapshot; return choice; }
            uint parentPid = topology.ParentPid(childPid);
            choice.ParentPid = parentPid;

            GhostDecoupleBurstReason childSide = EvaluateSide(topology, childPid, parentPid, modulesOf,
                GhostDecoupleBurstReason.ChildDecouplerFired, ref choice);
            if (choice.Fires) return choice;
            GhostDecoupleBurstReason parentSide = parentPid == 0
                ? GhostDecoupleBurstReason.PlainJointBreak
                : EvaluateSide(topology, parentPid, childPid, modulesOf,
                    GhostDecoupleBurstReason.ParentDecouplerFired, ref choice);
            if (choice.Fires) return choice;
            choice.Reason = MostTelling(childSide, parentSide);
            return choice;
        }

        private static GhostDecoupleBurstReason EvaluateSide(
            GhostDecoupleTopology topology, uint pid, uint otherPid,
            Func<string, List<GhostDecouplerModuleFacts>> modulesOf,
            GhostDecoupleBurstReason firedReason, ref GhostDecoupleBurstChoice choice)
        {
            string name = topology.PartName(pid);
            List<GhostDecouplerModuleFacts> modules = name != null && modulesOf != null ? modulesOf(name) : null;
            if (modules == null || modules.Count == 0) return GhostDecoupleBurstReason.PlainJointBreak;
            GhostDecoupleBurstReason seen = GhostDecoupleBurstReason.DecouplerFacesElsewhere;
            for (int i = 0; i < modules.Count; i++)
            {
                GhostDecouplerModuleFacts m = modules[i];
                if (topology.ModuleIsDecoupled(pid, m.ModuleIndex, m.ModuleName))
                {
                    seen = GhostDecoupleBurstReason.DecouplerAlreadyFired;
                    continue;
                }
                if (otherPid == 0) continue;
                if (m.IsOmni || topology.AttachedPid(pid, m.ExplosiveNodeId) == otherPid)
                {
                    choice.DecouplerPid = pid;
                    choice.DecouplerPartName = name;
                    choice.Module = m;
                    choice.Reason = firedReason;
                    return firedReason;
                }
            }
            return seen;
        }

        /// <summary>The reason that says most about a joint no decoupler fired.</summary>
        private static GhostDecoupleBurstReason MostTelling(GhostDecoupleBurstReason a, GhostDecoupleBurstReason b)
        {
            if (a == GhostDecoupleBurstReason.DecouplerAlreadyFired || b == GhostDecoupleBurstReason.DecouplerAlreadyFired)
                return GhostDecoupleBurstReason.DecouplerAlreadyFired;
            if (a == GhostDecoupleBurstReason.DecouplerFacesElsewhere || b == GhostDecoupleBurstReason.DecouplerFacesElsewhere)
                return GhostDecoupleBurstReason.DecouplerFacesElsewhere;
            return GhostDecoupleBurstReason.PlainJointBreak;
        }

        /// <summary>
        /// Whether this burst repeats the one just drawn: an omni decoupler separates several
        /// joints in one call, and the recorder wrote one event for each, all at one UT and applied
        /// in one frame; stock bursts once. A loop cycle replays the same UT in a later frame.
        /// </summary>
        internal static bool IsRepeatBurst(uint lastPid, double lastUT, int lastFrame, uint pid, double ut, int frame)
            => lastPid != 0 && lastPid == pid && lastUT == ut && lastFrame == frame;

        /// <summary>
        /// How long a burst's object is kept: its longest system's duration plus its longest start
        /// lifetime, half a second of margin, within <see cref="MinLifetimeSeconds"/> and
        /// <see cref="MaxLifetimeSeconds"/>; the default for a non-finite input.
        /// </summary>
        internal static float LifetimeSeconds(float duration, float maxStartLifetime)
        {
            float sum = duration + maxStartLifetime + 0.5f;
            if (float.IsNaN(sum) || float.IsInfinity(sum)) return DefaultLifetimeSeconds;
            return sum < MinLifetimeSeconds ? MinLifetimeSeconds : (sum > MaxLifetimeSeconds ? MaxLifetimeSeconds : sum);
        }

        /// <summary>The log word of a reason.</summary>
        internal static string ReasonWord(GhostDecoupleBurstReason r)
        {
            switch (r)
            {
                case GhostDecoupleBurstReason.NoSnapshot: return "no-snapshot";
                case GhostDecoupleBurstReason.PartNotInSnapshot: return "part-not-in-snapshot";
                case GhostDecoupleBurstReason.ChildDecouplerFired: return "child-decoupler-fired";
                case GhostDecoupleBurstReason.ParentDecouplerFired: return "parent-decoupler-fired";
                case GhostDecoupleBurstReason.PlainJointBreak: return "plain-joint-break";
                case GhostDecoupleBurstReason.DecouplerFacesElsewhere: return "decoupler-faces-elsewhere";
                case GhostDecoupleBurstReason.DecouplerAlreadyFired: return "decoupler-already-fired";
                default: return "unknown";
            }
        }
    }
}
