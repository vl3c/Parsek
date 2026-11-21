using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The moving-side attach configuration of one Breaking Ground servo part, read from
    /// its prefab module (<c>BaseServo.servoAttachNodes</c> / <c>servoSrfMeshNames</c>,
    /// both comma-separated <c>[KSPField]</c> strings in the part config).
    /// </summary>
    internal sealed class ServoAttachConfig
    {
        public HashSet<string> movingNodeIds = new HashSet<string>(StringComparer.Ordinal);
        public HashSet<string> movingSrfMeshNames = new HashSet<string>(StringComparer.Ordinal);

        internal static ServoAttachConfig FromFieldStrings(string servoAttachNodes, string servoSrfMeshNames)
        {
            var config = new ServoAttachConfig();
            AddCsv(config.movingNodeIds, servoAttachNodes);
            AddCsv(config.movingSrfMeshNames, servoSrfMeshNames);
            return config;
        }

        internal bool IsEmpty => movingNodeIds.Count == 0 && movingSrfMeshNames.Count == 0;

        private static void AddCsv(HashSet<string> sink, string csv)
        {
            if (string.IsNullOrEmpty(csv)) return;
            string[] items = csv.Split(',');
            for (int i = 0; i < items.Length; i++)
            {
                string item = items[i].Trim();
                if (item.Length > 0)
                    sink.Add(item);
            }
        }
    }

    /// <summary>One ghost part whose pose must follow a servo's moving transform.</summary>
    internal struct ServoFollowerLink
    {
        public uint followerPid;
        public uint servoPid;
    }

    internal sealed class ServoFollowerPlan
    {
        public readonly List<ServoFollowerLink> links = new List<ServoFollowerLink>();
        /// <summary>
        /// Servos whose PARENT hangs off a moving-side node (stock's editor "rotateBase"
        /// case): the moving half is the fixed one and the base swings. Not modelled; such a
        /// servo carries no followers and its subtree keeps the snapshot pose.
        /// </summary>
        public readonly List<uint> invertedServoPids = new List<uint>();
    }

    /// <summary>
    /// Pure decision: which ghost parts ride on which Breaking Ground servo's moving half.
    ///
    /// A part follows the NEAREST servo ancestor on whose moving side its branch hangs.
    /// "Moving side" mirrors stock <c>BaseServo.SetChildParentTransform</c> (the editor
    /// visualisation of the same question): a child attached to one of the servo's
    /// <c>servoAttachNodes</c>, or surface-attached on one of its
    /// <c>servoSrfMeshNames</c> colliders. A branch on the servo's base side climbs on to
    /// the next servo up, so a part can follow at most one servo directly; nested servos
    /// compose through the follower chain at playback.
    /// </summary>
    internal static class ServoFollowerPlanner
    {
        internal static bool IsBreakingGroundServoModuleName(string moduleName)
        {
            return string.Equals(moduleName, "ModuleRoboticServoHinge", StringComparison.Ordinal) ||
                   string.Equals(moduleName, "ModuleRoboticServoPiston", StringComparison.Ordinal) ||
                   string.Equals(moduleName, "ModuleRoboticRotationServo", StringComparison.Ordinal) ||
                   string.Equals(moduleName, "ModuleRoboticServoRotor", StringComparison.Ordinal);
        }

        /// <param name="partNodes">The snapshot's PART nodes in file order (the order their
        /// <c>parent</c> / <c>attN</c> / <c>srfN</c> indices refer to).</param>
        /// <param name="servosByPartIndex">The servo parts, keyed by PART index.</param>
        internal static ServoFollowerPlan Resolve(
            ConfigNode[] partNodes, IDictionary<int, ServoAttachConfig> servosByPartIndex)
        {
            var plan = new ServoFollowerPlan();
            if (partNodes == null || partNodes.Length == 0
                || servosByPartIndex == null || servosByPartIndex.Count == 0)
                return plan;

            int n = partNodes.Length;
            var parentOf = new int[n];
            var pidOf = new uint[n];
            var attachments = new List<AttachEntry>[n];
            var srfAttach = new AttachEntry?[n];
            for (int i = 0; i < n; i++)
            {
                ConfigNode node = partNodes[i];
                parentOf[i] = -1;
                if (node == null) continue;
                if (int.TryParse(node.GetValue("parent"), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int parent)
                    && parent >= 0 && parent < n && parent != i)
                    parentOf[i] = parent;
                uint.TryParse(node.GetValue("persistentId"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out pidOf[i]);
                string[] attN = node.GetValues("attN");
                if (attN != null && attN.Length > 0)
                {
                    attachments[i] = new List<AttachEntry>(attN.Length);
                    for (int a = 0; a < attN.Length; a++)
                    {
                        if (TryParseAttach(attN[a], out AttachEntry entry))
                            attachments[i].Add(entry);
                    }
                }
                if (TryParseAttach(node.GetValue("srfN"), out AttachEntry srf))
                    srfAttach[i] = srf;
            }

            var inverted = new HashSet<int>();
            foreach (var kvp in servosByPartIndex)
            {
                int s = kvp.Key;
                if (s < 0 || s >= n || kvp.Value == null) continue;
                int parent = parentOf[s];
                if (parent >= 0 && HasMovingNodeTo(attachments[s], kvp.Value, parent))
                {
                    inverted.Add(s);
                    if (pidOf[s] != 0) plan.invertedServoPids.Add(pidOf[s]);
                }
            }

            for (int i = 0; i < n; i++)
            {
                if (pidOf[i] == 0) continue;
                int cur = i;
                // n steps bound a malformed parent cycle.
                for (int step = 0; step < n; step++)
                {
                    int p = parentOf[cur];
                    if (p < 0) break;
                    if (!inverted.Contains(p)
                        && servosByPartIndex.TryGetValue(p, out ServoAttachConfig config)
                        && config != null
                        && IsOnMovingSide(config, p, cur, attachments, srfAttach))
                    {
                        if (pidOf[p] != 0)
                            plan.links.Add(new ServoFollowerLink { followerPid = pidOf[i], servoPid = pidOf[p] });
                        break;
                    }
                    cur = p;
                }
            }

            return plan;
        }

        private struct AttachEntry
        {
            public string id;
            public int partIdx;
            public string meshName;
        }

        /// <summary>Parses an AttachNodeSnapshot line: <c>id, partIdx[, srfAttachMeshName]</c>.</summary>
        private static bool TryParseAttach(string raw, out AttachEntry entry)
        {
            entry = default(AttachEntry);
            if (string.IsNullOrEmpty(raw)) return false;
            string[] fields = raw.Split(',');
            if (fields.Length < 2) return false;
            if (!int.TryParse(fields[1].Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int idx))
                return false;
            entry.id = fields[0].Trim();
            entry.partIdx = idx;
            entry.meshName = fields.Length > 2 ? fields[2].Trim() : "";
            return true;
        }

        private static bool HasMovingNodeTo(List<AttachEntry> nodes, ServoAttachConfig config, int otherIdx)
        {
            if (nodes == null) return false;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].partIdx == otherIdx && config.movingNodeIds.Contains(nodes[i].id))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Stock <c>BaseServo.SetChildParentTransform</c>, branch for branch: the child sits on
        /// a moving attach node of the servo; or the servo's own srfAttach node holds the child
        /// on a moving mesh; or the child's srfAttach node holds the servo on a moving mesh.
        /// </summary>
        private static bool IsOnMovingSide(ServoAttachConfig config, int servo, int child,
            List<AttachEntry>[] attachments, AttachEntry?[] srfAttach)
        {
            if (HasMovingNodeTo(attachments[servo], config, child))
                return true;
            AttachEntry? servoSrf = srfAttach[servo];
            if (servoSrf.HasValue && servoSrf.Value.partIdx == child
                && config.movingSrfMeshNames.Contains(servoSrf.Value.meshName))
                return true;
            AttachEntry? childSrf = srfAttach[child];
            return childSrf.HasValue && childSrf.Value.partIdx == servo
                && config.movingSrfMeshNames.Contains(childSrf.Value.meshName);
        }
    }
}
