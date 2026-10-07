using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// The parts of a resolved endpoint vessel that a route may write cargo into, read
    /// capacity from, or debit cargo from: the endpoint's OWN parts, never a craft that is
    /// merely docked to it (ROUTE-DELIVERY-INTO-DOCKED-VISITOR).
    ///
    /// <para>One instance covers one probe / writer bundle on one branch: the mask is indexed
    /// by <c>Vessel.parts</c> on the loaded branch and by
    /// <c>ProtoVessel.protoPartSnapshots</c> on the unloaded one, so it is built right before
    /// the probe and writers of the same branch and never carried across a tick. A null
    /// scope means "every part", which is the behaviour for an undocked endpoint and the
    /// logged fallback when the endpoint's own parts cannot be determined.</para>
    /// </summary>
    internal sealed class EndpointPartScope
    {
        private readonly bool[] ownMask;

        internal bool IsLoadedBranch { get; }
        internal int OwnPartCount { get; }
        internal int TotalPartCount { get; }
        internal int ExcludedComponentCount { get; }

        internal EndpointPartScope(bool[] ownMask, bool isLoadedBranch, int excludedComponentCount)
        {
            this.ownMask = ownMask ?? new bool[0];
            IsLoadedBranch = isLoadedBranch;
            ExcludedComponentCount = excludedComponentCount;
            TotalPartCount = this.ownMask.Length;
            int own = 0;
            var sb = new System.Text.StringBuilder(isLoadedBranch ? "loaded:" : "unloaded:");
            for (int i = 0; i < this.ownMask.Length; i++)
            {
                if (!this.ownMask[i]) continue;
                if (own > 0) sb.Append(',');
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
                own++;
            }
            OwnPartCount = own;
            key = sb.ToString();
        }

        /// <summary>True when the part at <paramref name="partIndex"/> on this scope's
        /// branch belongs to the endpoint. An index outside the mask is NOT included: the
        /// part list changed after the scope was built, and a part the scope never saw cannot
        /// be proven to be the endpoint's.</summary>
        internal bool Includes(int partIndex)
        {
            return partIndex >= 0 && partIndex < ownMask.Length && ownMask[partIndex];
        }

        /// <summary>Null-tolerant include test: a null scope includes every part.</summary>
        internal static bool Includes(EndpointPartScope scope, int partIndex)
        {
            return scope == null || scope.Includes(partIndex);
        }

        /// <summary>
        /// Identity of the part set a scope admits: <c>whole</c> for a null scope, else the
        /// branch and the admitted part indices. Two scopes with the same key read and write
        /// the same parts. A per-vessel cache that shares capacity accounting across stops
        /// must key on the vessel pid PLUS this, because two stops resolving to one docked
        /// composite can own disjoint halves of it.
        /// </summary>
        internal static string KeyOf(EndpointPartScope scope)
        {
            return scope == null ? "whole" : scope.key;
        }

        private readonly string key;

        /// <summary>Log token: <c>whole</c> for a null scope, else <c>own=N/M</c>.</summary>
        internal static string Describe(EndpointPartScope scope)
        {
            if (scope == null) return "whole";
            return "own=" + scope.OwnPartCount.ToString(CultureInfo.InvariantCulture)
                + "/" + scope.TotalPartCount.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Returns <paramref name="scope"/> when it was built for the
        /// <paramref name="isLoaded"/> branch, else null with a Warn. A loaded-branch mask
        /// indexes <c>Vessel.parts</c> and an unloaded one <c>protoPartSnapshots</c>; the two
        /// orders are not the same list, so a mask applied to the other branch would pick
        /// arbitrary parts. Dropping it restores the whole-vessel behaviour, which is the
        /// documented fallback.
        /// </summary>
        internal static EndpointPartScope ForBranch(EndpointPartScope scope, bool isLoaded, string owner)
        {
            if (scope == null || scope.IsLoadedBranch == isLoaded) return scope;
            ParsekLog.Warn(RouteOrchestrator.Tag,
                "Endpoint part scope dropped: owner=" + (owner ?? "<none>")
                + " scopeBranch=" + (scope.IsLoadedBranch ? "loaded" : "unloaded")
                + " readerBranch=" + (isLoaded ? "loaded" : "unloaded")
                + " - a part mask is only valid on the branch it was built for; using the whole vessel");
            return null;
        }
    }

    /// <summary>
    /// One probe per (resolved vessel, part scope) for a gate that walks several stops.
    /// Stops that resolve to the same vessel AND the same own-part scope share one probe
    /// instance, so the capacity gate accounts their combined manifest against one set of
    /// tanks (<see cref="RouteDestinationCapacityCheck.HasCapacityForAllStops"/>). Stops on
    /// one docked composite with DIFFERENT scopes (a station and a lander that was docked to
    /// it after both were recorded) get separate probes, each over the parts its own writer
    /// will fill; sharing the first stop's probe would plan the second stop's cargo against
    /// tanks its writer never touches, and the cargo that did not fit would be dropped.
    /// </summary>
    internal sealed class EndpointScopedCache<TProbe> where TProbe : class
    {
        private readonly Dictionary<string, TProbe> byKey =
            new Dictionary<string, TProbe>(StringComparer.Ordinal);

        internal static string KeyFor(uint vesselPid, EndpointPartScope scope)
        {
            return vesselPid.ToString(CultureInfo.InvariantCulture)
                + "|" + EndpointPartScope.KeyOf(scope);
        }

        internal TProbe GetOrAdd(uint vesselPid, EndpointPartScope scope, Func<TProbe> create)
        {
            string key = KeyFor(vesselPid, scope);
            if (byKey.TryGetValue(key, out TProbe cached)) return cached;
            TProbe made = create != null ? create() : null;
            byKey[key] = made;
            return made;
        }
    }

    /// <summary>
    /// Decides which parts of a docked composite are the route endpoint's own
    /// (ROUTE-DELIVERY-INTO-DOCKED-VISITOR). The resolver deliberately returns the docked
    /// composite (<see cref="RouteEndpointResolver.TryRootPartMatchPure"/>), so without this
    /// a visiting tanker's tanks filled first and left with the cargo on undock, the capacity
    /// gate counted them, and an origin debit could drain the visitor instead of the depot.
    ///
    /// <para>HOW THE COMPOSITE IS CUT: the way an undock would. Stock records every
    /// cross-vessel dock on the port itself - <c>ModuleDockingNode.DockToVessel</c> sets each
    /// half's <c>vesselInfo</c> (that half's pre-dock root part flightID) and
    /// <c>dockedPartUId</c>, and <c>Part.Couple</c> makes the two port parts parent and child
    /// - and <c>ModuleDockingNode.Undock</c> detaches exactly the child side of that edge
    /// (decompiled KSP 1.12.5; <c>ModuleGrappleNode</c> keeps both halves' info on the one
    /// claw node and releases the same way). Cutting every such settled seam splits the
    /// composite into the craft that were docked together.</para>
    ///
    /// <para>WHICH PIECES ARE THE ENDPOINT'S: the piece holding the endpoint's recorded root
    /// part, plus every piece holding a part the route RECORDED as the endpoint's. Stock's
    /// bookkeeping alone cannot answer this: a station module docked before the route was
    /// recorded and a tanker docked after it leave identical records (the station side names
    /// the station root, the other side names its own root, and nothing stores when). So the
    /// recorded part set is the discriminator: the connection window's
    /// <see cref="RouteConnectionWindow.EndpointPartPersistentIds"/> for an endpoint captured
    /// at a dock, and the start-docked seam split of the recording's start snapshot for a
    /// start-docked origin. A module docked after the route was recorded is therefore left
    /// out too, which is the conservative reading: cargo stays in the parts the route was
    /// proven against.</para>
    ///
    /// <para>FALLBACK: when no recorded part set exists, or none of the endpoint's recorded
    /// parts is aboard, the whole composite is used, exactly as before, and a rate-limited
    /// Info line says why.</para>
    /// </summary>
    internal static class RouteEndpointPartScope
    {
        private const string Tag = RouteOrchestrator.Tag;
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        internal const string DockingNodeModuleName = "ModuleDockingNode";
        internal const string GrappleNodeModuleName = "ModuleGrappleNode";

        /// <summary>One part of the vessel reduced to what the cut reads.
        /// <see cref="ParentIndex"/> is -1 for the root.</summary>
        internal readonly struct PartRecord
        {
            public readonly uint FlightId;
            public readonly uint PersistentId;
            public readonly int ParentIndex;

            internal PartRecord(uint flightId, uint persistentId, int parentIndex)
            {
                FlightId = flightId;
                PersistentId = persistentId;
                ParentIndex = parentIndex;
            }
        }

        /// <summary>One docking or grapple node: the part it sits on, the partner part it
        /// names, and the pre-dock root flightIDs stock recorded for its own side (and, on a
        /// claw, for the grabbed side).</summary>
        internal readonly struct DockNodeRecord
        {
            public readonly int PartIndex;
            public readonly uint DockedPartUId;
            public readonly bool HasVesselInfo;
            public readonly uint OwnSideRootUId;
            public readonly uint OtherSideRootUId;

            internal DockNodeRecord(int partIndex, uint dockedPartUId, bool hasVesselInfo,
                uint ownSideRootUId, uint otherSideRootUId)
            {
                PartIndex = partIndex;
                DockedPartUId = dockedPartUId;
                HasVesselInfo = hasVesselInfo;
                OwnSideRootUId = ownSideRootUId;
                OtherSideRootUId = otherSideRootUId;
            }
        }

        /// <summary>One settled cross-vessel dock: the parent/child part edge an undock
        /// would cut, with the pre-dock root flightID stock recorded for each side (0 when
        /// no node on that side recorded one).</summary>
        internal struct SeamEdge
        {
            public int ChildIndex;
            public int ParentIndex;
            public uint ChildSideRootUId;
            public uint ParentSideRootUId;
        }

        internal enum Outcome
        {
            /// <summary>No settled dock seam: the vessel is one craft, every part is its own.</summary>
            NotDocked = 0,
            /// <summary>At least one docked piece was excluded.</summary>
            Scoped = 1,
            /// <summary>Docked pieces exist but every one of them is the endpoint's.</summary>
            AllOwn = 2,
            /// <summary>Fallback: no recorded part set for this endpoint.</summary>
            NoRecordedParts = 3,
            /// <summary>Fallback: neither the root nor any recorded part is aboard.</summary>
            EndpointNotAboard = 4,
        }

        internal static string OutcomeToken(Outcome outcome)
        {
            switch (outcome)
            {
                case Outcome.NotDocked: return "not-docked";
                case Outcome.Scoped: return "scoped";
                case Outcome.AllOwn: return "all-own";
                case Outcome.NoRecordedParts: return "no-recorded-parts";
                case Outcome.EndpointNotAboard: return "endpoint-not-aboard";
                default: return "unknown";
            }
        }

        /// <summary>
        /// Every settled cross-vessel seam on the vessel. A node counts only when it carries
        /// stock's <c>vesselInfo</c> (set by <c>DockToVessel</c> / a claw grab, never by an
        /// editor pre-attach or a same-vessel dock), names a partner part on this vessel, and
        /// that partner is its parent or child - so a node left with stale info after an
        /// undock, or a same-vessel loop, never cuts anything. Both ports of one dock collapse
        /// to one edge.
        /// </summary>
        internal static List<SeamEdge> CollectSettledSeamEdges(
            IReadOnlyList<PartRecord> parts,
            IReadOnlyList<DockNodeRecord> nodes)
        {
            var edges = new List<SeamEdge>();
            if (parts == null || parts.Count == 0 || nodes == null || nodes.Count == 0)
                return edges;

            // First occurrence wins; a zero flightID is "unreadable" and indexes nothing.
            var indexByFlightId = new Dictionary<uint, int>(parts.Count);
            for (int i = 0; i < parts.Count; i++)
            {
                uint id = parts[i].FlightId;
                if (id != 0u && !indexByFlightId.ContainsKey(id))
                    indexByFlightId[id] = i;
            }

            for (int n = 0; n < nodes.Count; n++)
            {
                DockNodeRecord node = nodes[n];
                if (!node.HasVesselInfo || node.DockedPartUId == 0u) continue;
                int i = node.PartIndex;
                if (i < 0 || i >= parts.Count) continue;
                if (!indexByFlightId.TryGetValue(node.DockedPartUId, out int j) || j == i) continue;

                int child;
                int parent;
                if (parts[i].ParentIndex == j) { child = i; parent = j; }
                else if (parts[j].ParentIndex == i) { child = j; parent = i; }
                else continue;

                int existing = -1;
                for (int e = 0; e < edges.Count; e++)
                {
                    if (edges[e].ChildIndex == child && edges[e].ParentIndex == parent)
                    {
                        existing = e;
                        break;
                    }
                }
                SeamEdge edge = existing >= 0
                    ? edges[existing]
                    : new SeamEdge { ChildIndex = child, ParentIndex = parent };

                // The node's own vesselInfo describes the side it sits on; a claw's
                // otherVesselInfo describes the grabbed side.
                bool nodeOnChild = i == child;
                if (node.OwnSideRootUId != 0u)
                {
                    if (nodeOnChild && edge.ChildSideRootUId == 0u) edge.ChildSideRootUId = node.OwnSideRootUId;
                    if (!nodeOnChild && edge.ParentSideRootUId == 0u) edge.ParentSideRootUId = node.OwnSideRootUId;
                }
                if (node.OtherSideRootUId != 0u)
                {
                    if (nodeOnChild && edge.ParentSideRootUId == 0u) edge.ParentSideRootUId = node.OtherSideRootUId;
                    if (!nodeOnChild && edge.ChildSideRootUId == 0u) edge.ChildSideRootUId = node.OtherSideRootUId;
                }

                if (existing >= 0) edges[existing] = edge;
                else edges.Add(edge);
            }
            return edges;
        }

        /// <summary>
        /// Connected-component label per part over the parent/child tree with
        /// <paramref name="cutEdges"/> removed. A parent index outside the list is treated as
        /// no parent.
        /// </summary>
        internal static int[] LabelComponents(
            IReadOnlyList<PartRecord> parts,
            IReadOnlyList<SeamEdge> cutEdges)
        {
            int count = parts != null ? parts.Count : 0;
            var link = new int[count];
            for (int i = 0; i < count; i++) link[i] = i;

            for (int i = 0; i < count; i++)
            {
                int parent = parts[i].ParentIndex;
                if (parent < 0 || parent >= count || parent == i) continue;
                if (IsCut(cutEdges, i, parent)) continue;
                int a = Find(link, i);
                int b = Find(link, parent);
                if (a != b) link[a] = b;
            }

            var label = new int[count];
            var labelByRoot = new Dictionary<int, int>();
            for (int i = 0; i < count; i++)
            {
                int root = Find(link, i);
                if (!labelByRoot.TryGetValue(root, out int l))
                {
                    l = labelByRoot.Count;
                    labelByRoot[root] = l;
                }
                label[i] = l;
            }
            return label;
        }

        private static bool IsCut(IReadOnlyList<SeamEdge> cutEdges, int child, int parent)
        {
            if (cutEdges == null) return false;
            for (int e = 0; e < cutEdges.Count; e++)
            {
                if (cutEdges[e].ChildIndex == child && cutEdges[e].ParentIndex == parent)
                    return true;
            }
            return false;
        }

        private static int Find(int[] link, int i)
        {
            while (link[i] != i)
            {
                link[i] = link[link[i]];
                i = link[i];
            }
            return i;
        }

        /// <summary>
        /// Pure selection. Returns a per-part mask (true = the endpoint's own part) or null
        /// for "use every part"; <paramref name="outcome"/> says which. See the class summary
        /// for the rule.
        /// </summary>
        internal static bool[] SelectOwnParts(
            IReadOnlyList<PartRecord> parts,
            IReadOnlyList<DockNodeRecord> nodes,
            uint endpointRootPartUId,
            ICollection<uint> recordedOwnPartPids,
            out Outcome outcome,
            out int ownPartCount,
            out int excludedComponentCount)
        {
            outcome = Outcome.NotDocked;
            ownPartCount = parts != null ? parts.Count : 0;
            excludedComponentCount = 0;
            if (parts == null || parts.Count == 0)
                return null;

            List<SeamEdge> seams = CollectSettledSeamEdges(parts, nodes);
            if (seams.Count == 0)
                return null;

            // Stock cannot tell a module docked before the route was recorded from a visitor
            // docked after it, so without the recorded part set nothing is excluded.
            if (recordedOwnPartPids == null || recordedOwnPartPids.Count == 0)
            {
                outcome = Outcome.NoRecordedParts;
                return null;
            }

            int[] component = LabelComponents(parts, seams);
            int componentCount = 0;
            for (int i = 0; i < component.Length; i++)
                if (component[i] + 1 > componentCount) componentCount = component[i] + 1;

            var ownComponents = new HashSet<int>();
            if (endpointRootPartUId != 0u)
            {
                for (int i = 0; i < parts.Count; i++)
                {
                    if (parts[i].FlightId != endpointRootPartUId) continue;
                    ownComponents.Add(component[i]);
                    break;
                }
            }
            for (int i = 0; i < parts.Count; i++)
            {
                uint pid = parts[i].PersistentId;
                if (pid != 0u && recordedOwnPartPids.Contains(pid))
                    ownComponents.Add(component[i]);
            }

            if (ownComponents.Count == 0)
            {
                outcome = Outcome.EndpointNotAboard;
                return null;
            }
            if (ownComponents.Count >= componentCount)
            {
                outcome = Outcome.AllOwn;
                return null;
            }

            var mask = new bool[parts.Count];
            int own = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                if (!ownComponents.Contains(component[i])) continue;
                mask[i] = true;
                own++;
            }
            outcome = Outcome.Scoped;
            ownPartCount = own;
            excludedComponentCount = componentCount - ownComponents.Count;
            return mask;
        }

        /// <summary>
        /// Splits a vessel at the one dock seam whose two sides stock recorded as
        /// <paramref name="ownRootUId"/> and <paramref name="partnerRootUId"/>, and returns the
        /// part persistentIds on the side holding the <paramref name="ownRootUId"/> part. Used
        /// on a start-docked recording's start snapshot, where that seam is the depot /
        /// transport pair the origin proof names, to recover the depot's own parts (its
        /// earlier-docked modules included, since only the pair seam is cut).
        /// </summary>
        internal static bool TrySplitOwnSideAtPairSeam(
            IReadOnlyList<PartRecord> parts,
            IReadOnlyList<DockNodeRecord> nodes,
            uint ownRootUId,
            uint partnerRootUId,
            out HashSet<uint> ownSidePartPids)
        {
            ownSidePartPids = null;
            if (parts == null || parts.Count == 0) return false;
            if (ownRootUId == 0u || partnerRootUId == 0u || ownRootUId == partnerRootUId) return false;

            List<SeamEdge> seams = CollectSettledSeamEdges(parts, nodes);
            var pairSeams = new List<SeamEdge>();
            for (int e = 0; e < seams.Count; e++)
            {
                SeamEdge s = seams[e];
                bool forward = s.ChildSideRootUId == ownRootUId && s.ParentSideRootUId == partnerRootUId;
                bool backward = s.ChildSideRootUId == partnerRootUId && s.ParentSideRootUId == ownRootUId;
                if (forward || backward) pairSeams.Add(s);
            }
            if (pairSeams.Count == 0) return false;

            int ownIndex = -1;
            int partnerIndex = -1;
            for (int i = 0; i < parts.Count; i++)
            {
                if (ownIndex < 0 && parts[i].FlightId == ownRootUId) ownIndex = i;
                if (partnerIndex < 0 && parts[i].FlightId == partnerRootUId) partnerIndex = i;
            }
            if (ownIndex < 0 || partnerIndex < 0) return false;

            int[] component = LabelComponents(parts, pairSeams);
            // The cut must actually separate the two named craft, or the "sides" are not the
            // halves and the set would mis-scope every later write.
            if (component[ownIndex] == component[partnerIndex]) return false;

            var pids = new HashSet<uint>();
            for (int i = 0; i < parts.Count; i++)
            {
                if (component[i] != component[ownIndex]) continue;
                if (parts[i].PersistentId != 0u) pids.Add(parts[i].PersistentId);
            }
            if (pids.Count == 0) return false;
            ownSidePartPids = pids;
            return true;
        }

        /// <summary>
        /// The endpoint's recorded part persistentIds, gathered from the route's source
        /// recordings, or null when none were recorded. Two sources: every connection window
        /// that docked to this endpoint (matched by root part flightID; by the target pid only
        /// when the endpoint carries no root id), and a start-docked origin proof naming this
        /// endpoint's root, split out of the recording's start snapshot.
        /// </summary>
        internal static HashSet<uint> CollectRecordedEndpointPartPids(
            IEnumerable<Recording> sourceRecordings,
            RouteEndpoint endpoint,
            out string source)
        {
            source = "none";
            if (sourceRecordings == null) return null;
            uint root = endpoint.RootPartUId;
            uint pid = endpoint.VesselPersistentId;
            if (root == 0u && pid == 0u) return null;

            var pids = new HashSet<uint>();
            bool fromWindow = false;
            bool fromSnapshot = false;
            foreach (Recording rec in sourceRecordings)
            {
                if (rec == null) continue;

                List<RouteConnectionWindow> windows = rec.RouteConnectionWindows;
                if (windows != null)
                {
                    for (int w = 0; w < windows.Count; w++)
                    {
                        RouteConnectionWindow window = windows[w];
                        if (window?.EndpointPartPersistentIds == null
                            || window.EndpointPartPersistentIds.Count == 0)
                        {
                            continue;
                        }
                        // The root part flightID is launch-unique; the craft-baked pid is used
                        // only when the endpoint has no root to compare.
                        bool match = root != 0u
                            ? window.EndpointRootPartUId == root
                              || (window.EndpointAtDock.HasValue
                                  && window.EndpointAtDock.Value.RootPartUId == root)
                            : window.TransferTargetVesselPid == pid;
                        if (!match) continue;
                        for (int p = 0; p < window.EndpointPartPersistentIds.Count; p++)
                        {
                            if (window.EndpointPartPersistentIds[p] != 0u)
                                pids.Add(window.EndpointPartPersistentIds[p]);
                        }
                        fromWindow = true;
                    }
                }

                RouteOriginProof proof = rec.RouteOriginProof;
                if (root != 0u
                    && proof != null
                    && proof.StartDockedOriginRootPartUId == root
                    && proof.StartDockedTransportRootPartUId != 0u
                    && TryBuildRecordsFromVesselNode(rec.GhostVisualSnapshot,
                        out List<PartRecord> snapshotParts, out List<DockNodeRecord> snapshotNodes)
                    && TrySplitOwnSideAtPairSeam(snapshotParts, snapshotNodes,
                        root, proof.StartDockedTransportRootPartUId, out HashSet<uint> depotPids))
                {
                    pids.UnionWith(depotPids);
                    fromSnapshot = true;
                }
            }

            if (pids.Count == 0) return null;
            source = fromWindow && fromSnapshot
                ? "window+start-docked-snapshot"
                : fromWindow ? "window" : "start-docked-snapshot";
            return pids;
        }

        /// <summary>
        /// Reads a VESSEL snapshot node into part / dock-node records. The root part (the
        /// <c>root</c> index, or a part naming itself as parent) gets parent -1.
        /// </summary>
        internal static bool TryBuildRecordsFromVesselNode(
            ConfigNode vesselNode,
            out List<PartRecord> parts,
            out List<DockNodeRecord> nodes)
        {
            parts = null;
            nodes = null;
            if (vesselNode == null) return false;
            ConfigNode[] partNodes = vesselNode.GetNodes("PART");
            if (partNodes == null || partNodes.Length == 0) return false;

            int rootIndex = -1;
            string rootStr = vesselNode.GetValue("root");
            if (!string.IsNullOrEmpty(rootStr))
                int.TryParse(rootStr, NumberStyles.Integer, IC, out rootIndex);

            parts = new List<PartRecord>(partNodes.Length);
            nodes = new List<DockNodeRecord>();
            for (int i = 0; i < partNodes.Length; i++)
            {
                ConfigNode partNode = partNodes[i];
                uint flightId = ParseUInt(partNode.GetValue("uid"));
                uint pid = ParseUInt(partNode.GetValue("persistentId"));
                int parent = -1;
                string parentStr = partNode.GetValue("parent");
                if (!string.IsNullOrEmpty(parentStr)
                    && int.TryParse(parentStr, NumberStyles.Integer, IC, out int parsed))
                {
                    parent = parsed;
                }
                parts.Add(new PartRecord(flightId, pid,
                    NormalizeParentIndex(i, parent, rootIndex, partNodes.Length)));

                ConfigNode[] modules = partNode.GetNodes("MODULE");
                for (int m = 0; m < modules.Length; m++)
                {
                    if (TryReadDockNode(modules[m].GetValue("name"), modules[m], i, out DockNodeRecord node)
                        && node.HasVesselInfo)
                    {
                        nodes.Add(node);
                    }
                }
            }
            return true;
        }

        /// <summary>The root (the vessel's root index, or a part naming itself or an
        /// out-of-range index as parent) gets -1.</summary>
        internal static int NormalizeParentIndex(int index, int parent, int rootIndex, int count)
        {
            if (index == rootIndex) return -1;
            if (parent < 0 || parent >= count || parent == index) return -1;
            return parent;
        }

        private static uint ParseUInt(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0u;
            return uint.TryParse(value, NumberStyles.Integer, IC, out uint parsed) ? parsed : 0u;
        }

        /// <summary>
        /// Reads one MODULE node (a snapshot MODULE or a <c>ProtoPartModuleSnapshot</c>'s
        /// <c>moduleValues</c>) as a dock node when it is a docking port or a claw.
        /// </summary>
        internal static bool TryReadDockNode(
            string moduleName,
            ConfigNode moduleValues,
            int partIndex,
            out DockNodeRecord node)
        {
            node = default(DockNodeRecord);
            if (moduleValues == null) return false;
            bool isGrapple = string.Equals(moduleName, GrappleNodeModuleName, StringComparison.Ordinal);
            bool isDockingPort = string.Equals(moduleName, DockingNodeModuleName, StringComparison.Ordinal);
            if (!isGrapple && !isDockingPort) return false;

            uint dockedPartUId = ParseUInt(moduleValues.GetValue("dockUId"));
            ConfigNode ownInfo = moduleValues.GetNode("DOCKEDVESSEL");
            uint ownRoot = ownInfo != null ? ParseUInt(ownInfo.GetValue("rootUId")) : 0u;
            uint otherRoot = 0u;
            if (isGrapple)
            {
                ConfigNode otherInfo = moduleValues.GetNode("DOCKEDVESSEL_Other");
                otherRoot = otherInfo != null ? ParseUInt(otherInfo.GetValue("rootUId")) : 0u;
            }
            node = new DockNodeRecord(partIndex, dockedPartUId, ownInfo != null, ownRoot, otherRoot);
            return true;
        }

        // ------------------------------------------------------------------
        // Live: build a scope for one resolved endpoint vessel on one branch.
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds the part scope for <paramref name="vessel"/> resolved for
        /// <paramref name="endpoint"/> of <paramref name="route"/>, on the branch
        /// <paramref name="isLoaded"/> names (the same capture the probe and writers use).
        /// Returns null - every part - when the vessel is not docked to anything, and on every
        /// fallback (logged, rate-limited). Never throws.
        /// </summary>
        internal static EndpointPartScope ForEndpoint(
            Route route,
            RouteEndpoint endpoint,
            Vessel vessel,
            bool isLoaded,
            string purpose)
        {
            if (vessel == null) return null;
            try
            {
                List<PartRecord> parts;
                List<DockNodeRecord> nodes;
                bool read = isLoaded
                    ? TryBuildLoadedRecords(vessel, out parts, out nodes)
                    : TryBuildUnloadedRecords(vessel, out parts, out nodes);
                if (!read) return null;
                // Cheap exit before the recording lookup: a node can carry stale info
                // without being a seam.
                if (CollectSettledSeamEdges(parts, nodes).Count == 0) return null;

                HashSet<uint> recorded = CollectRecordedEndpointPartPids(
                    ResolveSourceRecordings(route), endpoint, out string recordedFrom);
                bool[] mask = SelectOwnParts(parts, nodes, endpoint.RootPartUId, recorded,
                    out Outcome outcome, out int own, out int excluded);
                LogOutcome(route, endpoint, vessel, isLoaded, purpose, outcome, own, parts.Count,
                    excluded, recordedFrom);
                return mask != null ? new EndpointPartScope(mask, isLoaded, excluded) : null;
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag,
                    "endpoint-part-scope-threw-" + endpoint.RootPartUId.ToString(IC),
                    "Endpoint part scope threw " + ex.GetType().Name + ": " + ex.Message
                    + " route=" + (route?.Id ?? "<none>")
                    + " endpointRoot=" + endpoint.RootPartUId.ToString(IC)
                    + " - using the whole vessel");
                return null;
            }
        }

        /// <summary>
        /// <see cref="ForEndpoint"/> for a caller that holds only the route id (the per-window
        /// pickup appliers). An unknown id yields no recorded part set, so the scope falls
        /// back to the whole vessel with the usual log line.
        /// </summary>
        internal static EndpointPartScope ForEndpointOfRoute(
            string routeId,
            RouteEndpoint endpoint,
            Vessel vessel,
            bool isLoaded,
            string purpose)
        {
            Route route = null;
            if (!string.IsNullOrEmpty(routeId))
                RouteStore.TryGetRoute(routeId, out route);
            return ForEndpoint(route, endpoint, vessel, isLoaded, purpose);
        }

        private static void LogOutcome(
            Route route, RouteEndpoint endpoint, Vessel vessel, bool isLoaded, string purpose,
            Outcome outcome, int own, int total, int excluded, string recordedFrom)
        {
            string body = "purpose=" + (purpose ?? "<none>")
                + " route=" + (route?.Id ?? "<none>")
                + " endpointRoot=" + endpoint.RootPartUId.ToString(IC)
                + " vessel='" + (vessel.vesselName ?? "<none>") + "'"
                + " pid=" + vessel.persistentId.ToString(IC)
                + " outcome=" + OutcomeToken(outcome)
                + " ownParts=" + own.ToString(IC) + "/" + total.ToString(IC)
                + " excludedComponents=" + excluded.ToString(IC)
                + " recordedFrom=" + (recordedFrom ?? "none")
                + " path=" + (isLoaded ? "loaded" : "unloaded");
            // The changing part of the condition is in the key, so a new visitor or a
            // different fallback reason prints at once; the gates re-derive this every tick
            // and the Logistics window every frame.
            string key = "endpoint-part-scope-" + endpoint.RootPartUId.ToString(IC)
                + "-" + vessel.persistentId.ToString(IC)
                + "-" + OutcomeToken(outcome)
                + "-" + own.ToString(IC) + "-" + total.ToString(IC);
            switch (outcome)
            {
                case Outcome.Scoped:
                    ParsekLog.InfoRateLimited(Tag, key,
                        "Endpoint part scope: " + body
                        + " - deliveries, capacity and debits use only the endpoint's own parts;"
                        + " craft docked to it are left untouched", 30.0);
                    break;
                case Outcome.NoRecordedParts:
                case Outcome.EndpointNotAboard:
                    ParsekLog.InfoRateLimited(Tag, key,
                        "Endpoint part scope undetermined: " + body
                        + " - using the whole docked vessel", 30.0);
                    break;
                default:
                    ParsekLog.VerboseRateLimited(Tag, key, "Endpoint part scope: " + body, 30.0);
                    break;
            }
        }

        /// <summary>The route's source recordings from the effective recording set. Empty
        /// when the route names none or the set cannot be computed.</summary>
        private static List<Recording> ResolveSourceRecordings(Route route)
        {
            var result = new List<Recording>();
            if (route?.RecordingIds == null || route.RecordingIds.Count == 0) return result;
            var ids = new HashSet<string>(route.RecordingIds, StringComparer.Ordinal);
            IReadOnlyList<Recording> ers;
            try
            {
                ers = EffectiveState.ComputeERS();
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited(Tag, "endpoint-part-scope-ers-" + route.Id,
                    "Endpoint part scope: ComputeERS threw " + ex.GetType().Name
                    + "; no recorded part set for route=" + route.Id, 30.0);
                return result;
            }
            if (ers == null) return result;
            for (int i = 0; i < ers.Count; i++)
            {
                Recording rec = ers[i];
                if (rec != null && rec.RecordingId != null && ids.Contains(rec.RecordingId))
                    result.Add(rec);
            }
            return result;
        }

        /// <summary>Live parts: the dock / claw nodes carrying stock's vesselInfo first (no
        /// allocation beyond the node list when there are none), then the part records.</summary>
        private static bool TryBuildLoadedRecords(
            Vessel v, out List<PartRecord> parts, out List<DockNodeRecord> nodes)
        {
            parts = null;
            nodes = null;
            if (v.parts == null || v.parts.Count == 0) return false;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null || p.Modules == null) continue;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule module = p.Modules[m];
                    if (module is ModuleDockingNode port)
                    {
                        if (port.vesselInfo == null) continue;
                        (nodes ?? (nodes = new List<DockNodeRecord>())).Add(new DockNodeRecord(
                            i, port.dockedPartUId, true, port.vesselInfo.rootPartUId, 0u));
                    }
                    else if (module is ModuleGrappleNode claw)
                    {
                        if (claw.vesselInfo == null) continue;
                        (nodes ?? (nodes = new List<DockNodeRecord>())).Add(new DockNodeRecord(
                            i, claw.dockedPartUId, true, claw.vesselInfo.rootPartUId,
                            claw.otherVesselInfo != null ? claw.otherVesselInfo.rootPartUId : 0u));
                    }
                }
            }
            if (nodes == null) return false;

            var indexByPart = new Dictionary<Part, int>(v.parts.Count);
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p != null && !indexByPart.ContainsKey(p)) indexByPart[p] = i;
            }
            parts = new List<PartRecord>(v.parts.Count);
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null)
                {
                    parts.Add(new PartRecord(0u, 0u, -1));
                    continue;
                }
                int parentIndex = -1;
                if (p.parent != null && indexByPart.TryGetValue(p.parent, out int found) && found != i)
                    parentIndex = found;
                parts.Add(new PartRecord(p.flightID, p.persistentId, parentIndex));
            }
            return true;
        }

        /// <summary>Proto parts, read the same way a snapshot is: the modules' persisted
        /// <c>dockUId</c> / <c>DOCKEDVESSEL</c> values.</summary>
        private static bool TryBuildUnloadedRecords(
            Vessel v, out List<PartRecord> parts, out List<DockNodeRecord> nodes)
        {
            parts = null;
            nodes = null;
            ProtoVessel pv = v.protoVessel;
            if (pv?.protoPartSnapshots == null || pv.protoPartSnapshots.Count == 0) return false;

            for (int i = 0; i < pv.protoPartSnapshots.Count; i++)
            {
                ProtoPartSnapshot pps = pv.protoPartSnapshots[i];
                if (pps?.modules == null) continue;
                for (int m = 0; m < pps.modules.Count; m++)
                {
                    ProtoPartModuleSnapshot module = pps.modules[m];
                    if (module == null) continue;
                    if (TryReadDockNode(module.moduleName, module.moduleValues, i, out DockNodeRecord node)
                        && node.HasVesselInfo)
                    {
                        (nodes ?? (nodes = new List<DockNodeRecord>())).Add(node);
                    }
                }
            }
            if (nodes == null) return false;

            int count = pv.protoPartSnapshots.Count;
            parts = new List<PartRecord>(count);
            for (int i = 0; i < count; i++)
            {
                ProtoPartSnapshot pps = pv.protoPartSnapshots[i];
                if (pps == null)
                {
                    parts.Add(new PartRecord(0u, 0u, -1));
                    continue;
                }
                parts.Add(new PartRecord(pps.flightID, pps.persistentId,
                    NormalizeParentIndex(i, pps.parentIdx, pv.rootIndex, count)));
            }
            return true;
        }
    }
}
