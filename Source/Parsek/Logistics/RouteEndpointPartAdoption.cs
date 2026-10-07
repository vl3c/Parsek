using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using static Parsek.Logistics.RouteEndpointPartScope;

namespace Parsek.Logistics
{
    /// <summary>
    /// One endpoint's adopted part set: the part flightIDs a route treats as that endpoint's own
    /// from the moment the player re-captured them, plus the endpoint binding they were captured
    /// against. Persisted in <see cref="Route.AdoptedEndpointParts"/>.
    /// </summary>
    internal sealed class RouteEndpointAdoptedParts
    {
        /// <summary>The endpoint's <see cref="RouteEndpoint.RootPartUId"/> at adoption (0 = none).</summary>
        public uint EndpointRootPartUId;

        /// <summary>The endpoint's <see cref="RouteEndpoint.VesselPersistentId"/> at adoption.</summary>
        public uint EndpointVesselPersistentId;

        /// <summary>The endpoint's normalized launch guid at adoption; null = unknown.</summary>
        public string EndpointLaunchGuid;

        /// <summary>UT of the adoption; -1 when unknown. Informational (log / audit) only.</summary>
        public double AdoptedUT = -1.0;

        /// <summary>
        /// The endpoint's parts at adoption, by <c>Part.flightID</c>. A flightID is assigned per
        /// launch and survives docking, undocking and save / load, where a part
        /// <c>persistentId</c> is baked into the .craft and reused by a later launch of the same
        /// craft once the original is gone.
        /// </summary>
        public HashSet<uint> PartFlightIds = new HashSet<uint>();
    }

    /// <summary>
    /// Re-captures a route's endpoint part sets from what is docked right now (owner ruling
    /// 2026-10-07, the Logistics window's Update parts). <see cref="RouteEndpointPartScope"/>
    /// restricts deliveries, capacity reads and debits to the parts a route RECORDED as the
    /// endpoint's, and stock records no dock time, so a module the player permanently docks to a
    /// station after recording its route reads exactly like a visitor and gets no cargo. Adopting
    /// the endpoint's current parts lets the player opt such a module in: the scope consults the
    /// adopted set BEFORE the recorded sets and admits every docked piece holding an adopted part,
    /// while a craft docked after the adoption still falls outside it.
    ///
    /// <para>WHAT A PRESS MAY TAKE (<see cref="DecideCapture"/>). Only an endpoint that IS the
    /// station adopts the composite it resolves to: its own piece (as the scope resolves it
    /// today: its root plus its adopted, else recorded, parts) must hold the composite's ROOT
    /// part. An endpoint docked INTO a larger craft (a lander parked at a station, a depot module
    /// docked into one) is refused and keeps its earlier set: adopting the composite would make
    /// the host's tanks its own. From the station's composite, the pieces that are another of
    /// the route's endpoints and the pieces holding the route's own transport root are left
    /// out; any other docked ship is taken, which is why the hover says to undock visitors
    /// first.</para>
    ///
    /// <para>BINDING. An adoption belongs to the endpoint binding it was captured against, matched
    /// the way the recorded sets are matched to an endpoint: by the endpoint's root part flightID
    /// when it has one, else by its vessel persistentId gated by the launch guid
    /// (<see cref="BindingMatches"/>). Only <see cref="RouteEndpointTransfer"/> rewrites those
    /// fields on a committed route, so a transfer to another vessel leaves the adoption behind
    /// (the scope simply does not find it; the next adoption prunes it), while a part that
    /// undocks is simply not aboard and is not found either.</para>
    ///
    /// <para>LIFECYCLE. The adoption lives on the <see cref="Route"/> instance like the player's
    /// other route settings (name, priority, cadence): the ROUTE codec persists it, a cold load
    /// reads the save's copy, and an in-session rewind or load reconcile keeps the live instance
    /// and with it the adoption. It names physical parts by launch-unique id, so in a world
    /// rewound to before the module docked the module's parts are just not aboard, and docking
    /// the same module again re-admits it.</para>
    /// </summary>
    internal static class RouteEndpointPartAdoption
    {
        private const string Tag = RouteOrchestrator.Tag;
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>The endpoint is docked into a larger craft whose root it does not hold.</summary>
        internal const string RefusedGuestInLargerComposite = "guest-in-larger-composite";
        /// <summary>A composite where neither the endpoint's root nor any part set is known.</summary>
        internal const string RefusedOwnPartsUnknown = "own-parts-unknown";
        /// <summary>A composite holding neither the endpoint's root nor any of its parts.</summary>
        internal const string RefusedEndpointNotAboard = "endpoint-not-aboard";
        /// <summary>No part with a readable flightID.</summary>
        internal const string RefusedNoReadableParts = "no-readable-parts";

        /// <summary>One resolved endpoint vessel reduced to what the decision reads.</summary>
        internal sealed class CapturedVessel
        {
            public string Name;
            public List<PartRecord> Parts;
            public List<DockNodeRecord> Nodes;
            /// <summary>The vessel's root part flightID (the composite's root when docked).</summary>
            public uint RootFlightId;
        }

        /// <summary>
        /// Resolves one endpoint and reads its current parts and dock nodes. Returns false with a
        /// reason when the endpoint does not resolve to a live vessel. May rebind the route's
        /// endpoint as a side effect (the live resolver runs <see cref="RouteEndpointTransfer"/>).
        /// </summary>
        internal delegate bool EndpointPartCapture(
            RouteEndpoint endpoint, out CapturedVessel vessel, out string reason);

        /// <summary>What the action did for one endpoint.</summary>
        internal struct EndpointAdoptionResult
        {
            public bool IsOrigin;
            public int StopIndex;
            public bool Adopted;
            /// <summary>Resolved, but the decision refused it (<see cref="Reason"/> says why).</summary>
            public bool Refused;
            public string VesselName;
            /// <summary>Parts adopted (0 when not adopted).</summary>
            public int PartCount;
            /// <summary>Docked pieces left out of the adoption (another endpoint, the transport).</summary>
            public int ExcludedPieces;
            /// <summary>Parts in the endpoint's adoption before this call; -1 when it had none.</summary>
            public int PreviousPartCount;
            /// <summary>Why the endpoint was not adopted; null when it was.</summary>
            public string Reason;
        }

        /// <summary>What one <see cref="AdoptCurrentParts(Route, double, EndpointPartCapture, Func{RouteEndpoint, HashSet{uint}}, ICollection{uint})"/> call did.</summary>
        internal sealed class AdoptionResult
        {
            public readonly List<EndpointAdoptionResult> Endpoints = new List<EndpointAdoptionResult>();
            public int AdoptedCount;
            public int RefusedCount;
            public int UnresolvedCount;
            public int PrunedCount;
        }

        /// <summary>
        /// True when <paramref name="entry"/> was captured against <paramref name="endpoint"/>'s
        /// current binding. With a root part flightID on the endpoint, the roots must be equal
        /// (launch-unique). Without one, the entry must carry no root either, the vessel
        /// persistentIds must be equal and nonzero, and the launch guids must not conclusively
        /// differ: a persistentId is craft-baked, so a bare pid match can name a different launch.
        /// </summary>
        internal static bool BindingMatches(RouteEndpointAdoptedParts entry, RouteEndpoint endpoint)
        {
            if (entry == null) return false;
            if (endpoint.RootPartUId != 0u)
                return entry.EndpointRootPartUId == endpoint.RootPartUId;
            if (entry.EndpointRootPartUId != 0u) return false;
            if (endpoint.VesselPersistentId == 0u
                || entry.EndpointVesselPersistentId != endpoint.VesselPersistentId)
            {
                return false;
            }
            return !VesselLaunchIdentity.GuidsConclusivelyDiffer(entry.EndpointLaunchGuid, endpoint.LaunchGuid);
        }

        /// <summary>
        /// The adopted part flightIDs for <paramref name="endpoint"/> of <paramref name="route"/>,
        /// or null when the route holds no adoption for that binding (or an empty one). Returns the
        /// stored set itself; callers only read it.
        /// </summary>
        internal static HashSet<uint> FindAdoptedPartFlightIds(Route route, RouteEndpoint endpoint)
        {
            return FindAdopted(route?.AdoptedEndpointParts, endpoint);
        }

        private static HashSet<uint> FindAdopted(List<RouteEndpointAdoptedParts> entries, RouteEndpoint endpoint)
        {
            if (entries == null) return null;
            for (int i = 0; i < entries.Count; i++)
            {
                RouteEndpointAdoptedParts entry = entries[i];
                if (entry?.PartFlightIds == null || entry.PartFlightIds.Count == 0) continue;
                if (BindingMatches(entry, endpoint)) return entry.PartFlightIds;
            }
            return null;
        }

        /// <summary>The latest <see cref="RouteEndpointAdoptedParts.AdoptedUT"/> on the route,
        /// or -1 when it has no dated adoption (the Logistics hover's "Last updated" clause).</summary>
        internal static double LastAdoptedUT(Route route)
        {
            double latest = -1.0;
            List<RouteEndpointAdoptedParts> entries = route?.AdoptedEndpointParts;
            if (entries == null) return latest;
            for (int i = 0; i < entries.Count; i++)
            {
                RouteEndpointAdoptedParts entry = entries[i];
                if (entry != null && entry.AdoptedUT > latest) latest = entry.AdoptedUT;
            }
            return latest;
        }

        /// <summary>
        /// THE DECISION, pure: which parts of the vessel resolved for an endpoint a press may
        /// adopt as that endpoint's own. Returns false with <paramref name="refusal"/> when it may
        /// adopt nothing.
        /// <list type="bullet">
        /// <item>No settled dock seam: the vessel is one craft, every readable part.</item>
        /// <item>Docked: the endpoint's own pieces (<paramref name="self"/>, as the scope resolves
        /// them) must be found (<see cref="RefusedOwnPartsUnknown"/> /
        /// <see cref="RefusedEndpointNotAboard"/>) and must hold the composite's root part
        /// <paramref name="compositeRootFlightId"/>, i.e. the endpoint is the station, not a
        /// craft docked into one (<see cref="RefusedGuestInLargerComposite"/>).</item>
        /// <item>Then every piece is taken except one that is another endpoint's own
        /// (<paramref name="otherEndpoints"/>) or holds a part named in
        /// <paramref name="transportRootFlightIds"/>; the endpoint's own pieces are never left
        /// out, whatever those lists name.</item>
        /// </list>
        /// </summary>
        internal static bool DecideCapture(
            IReadOnlyList<PartRecord> parts,
            IReadOnlyList<DockNodeRecord> nodes,
            uint compositeRootFlightId,
            OwnPartSets self,
            IReadOnlyList<OwnPartSets> otherEndpoints,
            ICollection<uint> transportRootFlightIds,
            out HashSet<uint> adoptFlightIds,
            out int excludedPieces,
            out string refusal)
        {
            adoptFlightIds = null;
            excludedPieces = 0;
            refusal = null;

            var readable = new HashSet<uint>();
            if (parts != null)
                for (int i = 0; i < parts.Count; i++)
                    if (parts[i].FlightId != 0u) readable.Add(parts[i].FlightId);
            if (readable.Count == 0)
            {
                refusal = RefusedNoReadableParts;
                return false;
            }

            List<SeamEdge> seams = CollectSettledSeamEdges(parts, nodes);
            if (seams.Count == 0)
            {
                adoptFlightIds = readable;
                return true;
            }

            int[] component = LabelComponents(parts, seams);
            HashSet<int> own = OwnComponents(parts, component, self);
            if (own.Count == 0)
            {
                bool anythingKnown = self.RootPartUId != 0u
                    || (self.AdoptedPartFlightIds != null && self.AdoptedPartFlightIds.Count > 0)
                    || (self.RecordedPartPids != null && self.RecordedPartPids.Count > 0);
                refusal = anythingKnown ? RefusedEndpointNotAboard : RefusedOwnPartsUnknown;
                return false;
            }

            int rootComponent = -1;
            if (compositeRootFlightId != 0u)
            {
                for (int i = 0; i < parts.Count; i++)
                {
                    if (parts[i].FlightId != compositeRootFlightId) continue;
                    rootComponent = component[i];
                    break;
                }
            }
            if (rootComponent < 0 || !own.Contains(rootComponent))
            {
                refusal = RefusedGuestInLargerComposite;
                return false;
            }

            // Pieces another endpoint of the route owns, and pieces holding the transport's
            // root, stay out - unless they are this endpoint's own.
            var excluded = new HashSet<int>();
            if (otherEndpoints != null)
            {
                for (int o = 0; o < otherEndpoints.Count; o++)
                {
                    foreach (int c in OwnComponents(parts, component, otherEndpoints[o]))
                        if (!own.Contains(c)) excluded.Add(c);
                }
            }
            if (transportRootFlightIds != null && transportRootFlightIds.Count > 0)
            {
                for (int i = 0; i < parts.Count; i++)
                {
                    uint flightId = parts[i].FlightId;
                    if (flightId == 0u || !transportRootFlightIds.Contains(flightId)) continue;
                    if (!own.Contains(component[i])) excluded.Add(component[i]);
                }
            }

            adoptFlightIds = new HashSet<uint>();
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i].FlightId == 0u || excluded.Contains(component[i])) continue;
                adoptFlightIds.Add(parts[i].FlightId);
            }
            excludedPieces = excluded.Count;
            return true;
        }

        /// <summary>
        /// The flightIDs that name the route's own transport in a docked composite: the root part
        /// of every source recording's vessel snapshots (the end snapshot's root is the
        /// transport's pre-dock root, which an undock restores) and a start-docked origin proof's
        /// transport root. Roots that are one of the route's <paramref name="endpoints"/> are
        /// dropped (a snapshot taken while docked can be rooted at the station).
        /// <see cref="DecideCapture"/> never leaves an endpoint's own piece out anyway.
        /// </summary>
        internal static HashSet<uint> CollectTransportRootFlightIds(
            IEnumerable<Recording> sourceRecordings, IEnumerable<RouteEndpoint> endpoints)
        {
            var roots = new HashSet<uint>();
            if (sourceRecordings == null) return roots;
            foreach (Recording rec in sourceRecordings)
            {
                if (rec == null) continue;
                AddSnapshotRoot(rec.VesselSnapshot, roots);
                AddSnapshotRoot(rec.GhostVisualSnapshot, roots);
                uint proofRoot = rec.RouteOriginProof != null
                    ? rec.RouteOriginProof.StartDockedTransportRootPartUId : 0u;
                if (proofRoot != 0u) roots.Add(proofRoot);
            }
            if (endpoints != null)
            {
                foreach (RouteEndpoint ep in endpoints)
                    if (ep.RootPartUId != 0u) roots.Remove(ep.RootPartUId);
            }
            return roots;
        }

        /// <summary>The root part flightID (<c>uid</c> of the <c>root</c>-indexed PART) of a
        /// VESSEL snapshot, added when readable.</summary>
        private static void AddSnapshotRoot(ConfigNode vesselNode, HashSet<uint> roots)
        {
            if (vesselNode == null) return;
            ConfigNode[] partNodes = vesselNode.GetNodes("PART");
            if (partNodes == null || partNodes.Length == 0) return;
            int rootIndex = 0;
            string rootStr = vesselNode.GetValue("root");
            if (!string.IsNullOrEmpty(rootStr)
                && !int.TryParse(rootStr, NumberStyles.Integer, IC, out rootIndex))
            {
                return;
            }
            if (rootIndex < 0 || rootIndex >= partNodes.Length) return;
            if (uint.TryParse(partNodes[rootIndex].GetValue("uid"), NumberStyles.Integer, IC, out uint uid)
                && uid != 0u)
            {
                roots.Add(uid);
            }
        }

        /// <summary>
        /// Live entry point (the Logistics window's Update parts): adopts every resolvable
        /// endpoint's current parts at the current UT. Resolution is
        /// <see cref="RouteEndpointResolver.TryResolveEndpoint"/>, the same lookup the deliveries
        /// use; the parts are read from the store the writers use
        /// (<see cref="RouteOrchestrator.EndpointStoreIsLiveParts(Vessel)"/>); the recorded sets
        /// and the transport roots come from the route's source recordings.
        /// </summary>
        internal static AdoptionResult AdoptCurrentParts(Route route)
        {
            List<Recording> sources = route != null ? ResolveSourceRecordings(route) : new List<Recording>();
            return AdoptCurrentParts(route, ReadUniversalTime(), TryCaptureLive,
                ep => CollectRecordedEndpointPartPids(sources, ep, out _),
                route != null ? CollectTransportRootFlightIds(sources, CurrentBindings(route)) : null);
        }

        /// <summary>
        /// The action. For the origin (unless the route has no physical origin: KSC or harvest)
        /// and every stop, in that order: capture the endpoint through
        /// <paramref name="capture"/>, decide what it may adopt (<see cref="DecideCapture"/>,
        /// against the binding the route holds AFTER the capture, since a resolution can rebind
        /// it; <paramref name="recordedPartPids"/> supplies each endpoint's recorded set), and
        /// replace any adoption for that binding. An endpoint that does not resolve or is refused
        /// keeps whatever adoption it had. Entries whose binding no current endpoint matches are
        /// pruned. Logs one Info line with the per-endpoint counts. Never throws on a null route
        /// or capture.
        /// </summary>
        internal static AdoptionResult AdoptCurrentParts(
            Route route,
            double ut,
            EndpointPartCapture capture,
            Func<RouteEndpoint, HashSet<uint>> recordedPartPids = null,
            ICollection<uint> transportRootFlightIds = null)
        {
            var result = new AdoptionResult();
            if (route == null || capture == null)
            {
                ParsekLog.Verbose(Tag, "Endpoint part adoption skipped: "
                    + (route == null ? "no route" : "no capture for route=" + RouteIds.Short(route)));
                return result;
            }

            var entries = route.AdoptedEndpointParts != null
                ? new List<RouteEndpointAdoptedParts>(route.AdoptedEndpointParts)
                : new List<RouteEndpointAdoptedParts>();

            if (HasPhysicalOrigin(route))
                AdoptOne(route, -1, ut, capture, recordedPartPids, transportRootFlightIds, entries, result);
            if (route.Stops != null)
            {
                for (int s = 0; s < route.Stops.Count; s++)
                {
                    if (route.Stops[s] == null) continue;
                    AdoptOne(route, s, ut, capture, recordedPartPids, transportRootFlightIds, entries, result);
                }
            }

            // Prune entries no current endpoint binding names (left behind by a transfer).
            List<RouteEndpoint> bindings = CurrentBindings(route);
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                RouteEndpointAdoptedParts entry = entries[i];
                bool named = false;
                if (entry?.PartFlightIds != null && entry.PartFlightIds.Count > 0)
                {
                    for (int b = 0; b < bindings.Count && !named; b++)
                        named = BindingMatches(entry, bindings[b]);
                }
                if (named) continue;
                entries.RemoveAt(i);
                result.PrunedCount++;
            }

            route.AdoptedEndpointParts = entries.Count > 0 ? entries : null;
            ParsekLog.Info(Tag, FormatAdoptionLine(route, ut, result));
            return result;
        }

        /// <param name="stopIndex">-1 for the origin.</param>
        private static void AdoptOne(
            Route route, int stopIndex, double ut, EndpointPartCapture capture,
            Func<RouteEndpoint, HashSet<uint>> recordedPartPids, ICollection<uint> transportRootFlightIds,
            List<RouteEndpointAdoptedParts> entries, AdoptionResult result)
        {
            bool isOrigin = stopIndex < 0;
            RouteEndpoint before = BindingAt(route, stopIndex);
            var item = new EndpointAdoptionResult
            {
                IsOrigin = isOrigin,
                StopIndex = stopIndex,
                PreviousPartCount = -1,
            };

            bool resolved = capture(before, out CapturedVessel vessel, out string reason);
            item.VesselName = vessel?.Name;

            // The resolution may have rebound the endpoint; the adoption belongs to the binding
            // the route holds now.
            RouteEndpoint after = BindingAt(route, stopIndex);
            int existing = FindEntryIndex(entries, after);
            if (existing >= 0)
                item.PreviousPartCount = entries[existing].PartFlightIds?.Count ?? 0;

            if (!resolved || vessel == null)
            {
                item.Reason = string.IsNullOrEmpty(reason) ? "unresolved" : reason;
                result.UnresolvedCount++;
                result.Endpoints.Add(item);
                return;
            }
            if (after.RootPartUId == 0u && after.VesselPersistentId == 0u)
            {
                item.Reason = "no-endpoint-identity";
                result.UnresolvedCount++;
                result.Endpoints.Add(item);
                return;
            }

            // The other endpoints of this route, as the scope resolves their own pieces.
            var others = new List<OwnPartSets>();
            int lastStop = route.Stops != null ? route.Stops.Count - 1 : -1;
            for (int idx = HasPhysicalOrigin(route) ? -1 : 0; idx <= lastStop; idx++)
            {
                if (idx == stopIndex) continue;
                if (idx >= 0 && route.Stops[idx] == null) continue;
                others.Add(SetsFor(BindingAt(route, idx), entries, recordedPartPids));
            }

            bool decided = DecideCapture(vessel.Parts, vessel.Nodes, vessel.RootFlightId,
                SetsFor(after, entries, recordedPartPids), others, transportRootFlightIds,
                out HashSet<uint> ids, out int excludedPieces, out string refusal);
            if (!decided || ids == null || ids.Count == 0)
            {
                item.Reason = refusal ?? RefusedNoReadableParts;
                if (item.Reason == RefusedNoReadableParts)
                {
                    result.UnresolvedCount++;
                }
                else
                {
                    item.Refused = true;
                    result.RefusedCount++;
                }
                result.Endpoints.Add(item);
                return;
            }

            var entry = new RouteEndpointAdoptedParts
            {
                EndpointRootPartUId = after.RootPartUId,
                EndpointVesselPersistentId = after.VesselPersistentId,
                EndpointLaunchGuid = VesselLaunchIdentity.NormalizeGuid(after.LaunchGuid),
                AdoptedUT = ut,
                PartFlightIds = ids,
            };
            // Replace every entry for this binding (there is at most one unless a save was
            // hand-edited).
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (BindingMatches(entries[i], after))
                    entries.RemoveAt(i);
            }
            entries.Add(entry);

            item.Adopted = true;
            item.PartCount = ids.Count;
            item.ExcludedPieces = excludedPieces;
            result.AdoptedCount++;
            result.Endpoints.Add(item);
        }

        private static RouteEndpoint BindingAt(Route route, int stopIndex)
        {
            return stopIndex < 0 ? route.Origin : route.Stops[stopIndex].Endpoint;
        }

        /// <summary>An endpoint's own-part sets as the scope reads them: its root, its adoption
        /// in <paramref name="entries"/> (this call's working copy), else its recorded set.</summary>
        private static OwnPartSets SetsFor(
            RouteEndpoint endpoint, List<RouteEndpointAdoptedParts> entries,
            Func<RouteEndpoint, HashSet<uint>> recordedPartPids)
        {
            HashSet<uint> adopted = FindAdopted(entries, endpoint);
            return new OwnPartSets
            {
                RootPartUId = endpoint.RootPartUId,
                AdoptedPartFlightIds = adopted,
                RecordedPartPids = adopted == null && recordedPartPids != null
                    ? recordedPartPids(endpoint) : null,
            };
        }

        private static int FindEntryIndex(List<RouteEndpointAdoptedParts> entries, RouteEndpoint endpoint)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i]?.PartFlightIds != null && BindingMatches(entries[i], endpoint))
                    return i;
            }
            return -1;
        }

        /// <summary>KSC and harvest routes carry a display-only origin with no vessel.</summary>
        private static bool HasPhysicalOrigin(Route route)
        {
            return !route.IsKscOrigin && !route.IsHarvestOrigin;
        }

        private static List<RouteEndpoint> CurrentBindings(Route route)
        {
            var bindings = new List<RouteEndpoint>();
            if (HasPhysicalOrigin(route)) bindings.Add(route.Origin);
            if (route.Stops != null)
            {
                for (int s = 0; s < route.Stops.Count; s++)
                    if (route.Stops[s] != null) bindings.Add(route.Stops[s].Endpoint);
            }
            return bindings;
        }

        /// <summary>
        /// The one Info line per adoption:
        /// <c>Endpoint part adoption: route=X ut=U endpoints=N adopted=A refused=R unresolved=M
        /// pruned=P [origin:'Depot' parts=12 prev=none; stop1:'Station' parts=31 prev=28
        /// excluded=1; stop2:'Station' refused=guest-in-larger-composite kept=3;
        /// stop3:unresolved('reason') kept=4]</c>. Stop numbers are 1-based; <c>kept=K</c> names
        /// a refused or unreached endpoint's surviving adoption; <c>excluded=E</c> the docked
        /// pieces left out (another endpoint's, the route's transport).
        /// </summary>
        internal static string FormatAdoptionLine(Route route, double ut, AdoptionResult result)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < result.Endpoints.Count; i++)
            {
                EndpointAdoptionResult e = result.Endpoints[i];
                if (i > 0) sb.Append("; ");
                sb.Append(e.IsOrigin ? "origin" : "stop" + (e.StopIndex + 1).ToString(IC)).Append(':');
                if (e.Adopted)
                {
                    sb.Append('\'').Append(e.VesselName ?? "<unnamed>").Append('\'')
                        .Append(" parts=").Append(e.PartCount.ToString(IC))
                        .Append(" prev=").Append(e.PreviousPartCount < 0
                            ? "none" : e.PreviousPartCount.ToString(IC));
                    if (e.ExcludedPieces > 0)
                        sb.Append(" excluded=").Append(e.ExcludedPieces.ToString(IC));
                    continue;
                }
                if (e.Refused)
                {
                    sb.Append('\'').Append(e.VesselName ?? "<unnamed>").Append('\'')
                        .Append(" refused=").Append(e.Reason ?? string.Empty);
                }
                else
                {
                    sb.Append("unresolved('").Append(e.Reason ?? string.Empty).Append("')");
                }
                if (e.PreviousPartCount >= 0)
                    sb.Append(" kept=").Append(e.PreviousPartCount.ToString(IC));
            }
            return "Endpoint part adoption: route=" + RouteIds.Short(route)
                + " ut=" + ut.ToString("R", IC)
                + " endpoints=" + result.Endpoints.Count.ToString(IC)
                + " adopted=" + result.AdoptedCount.ToString(IC)
                + " refused=" + result.RefusedCount.ToString(IC)
                + " unresolved=" + result.UnresolvedCount.ToString(IC)
                + " pruned=" + result.PrunedCount.ToString(IC)
                + " [" + sb + "]"
                + " - deliveries, capacity and debits now treat every adopted part as the endpoint's own";
        }

        // ------------------------------------------------------------------
        // Live capture
        // ------------------------------------------------------------------

        /// <summary>
        /// Resolves <paramref name="endpoint"/> the way the deliveries do and reads the vessel's
        /// part and dock-node records from the branch the writers use (live parts when loaded,
        /// the proto snapshot otherwise). NoInlining keeps the stock reads out of the pure
        /// action's JIT.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool TryCaptureLive(RouteEndpoint endpoint, out CapturedVessel captured, out string reason)
        {
            captured = null;
            try
            {
                if (!RouteEndpointResolver.TryResolveEndpoint(endpoint, out Vessel vessel, out reason)
                    || vessel == null)
                {
                    if (string.IsNullOrEmpty(reason)) reason = "unresolved";
                    return false;
                }
                bool isLoaded = RouteOrchestrator.EndpointStoreIsLiveParts(vessel);
                if (!TryBuildRecords(vessel, isLoaded, out List<PartRecord> parts, out List<DockNodeRecord> nodes))
                {
                    parts = new List<PartRecord>();
                    nodes = new List<DockNodeRecord>();
                }
                captured = new CapturedVessel
                {
                    Name = vessel.vesselName,
                    Parts = parts,
                    Nodes = nodes,
                    RootFlightId = RouteEndpointResolver.ResolveRootPartFlightId(vessel),
                };
                reason = null;
                return true;
            }
            catch (Exception ex)
            {
                reason = "threw-" + ex.GetType().Name;
                return false;
            }
        }

        private static double ReadUniversalTime()
        {
            try
            {
                return Planetarium.GetUniversalTime();
            }
            catch
            {
                // Off-scene or mid-teardown: the UT is informational, the adoption still lands.
                return -1.0;
            }
        }
    }
}
