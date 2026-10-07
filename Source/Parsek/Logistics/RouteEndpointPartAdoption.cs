using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

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
        /// Every part of the resolved endpoint vessel at adoption, by <c>Part.flightID</c>. A
        /// flightID is assigned per launch and survives docking, undocking and save / load,
        /// where a part <c>persistentId</c> is baked into the .craft and reused by a later
        /// launch of the same craft once the original is gone.
        /// </summary>
        public HashSet<uint> PartFlightIds = new HashSet<uint>();
    }

    /// <summary>
    /// Re-captures a route's endpoint part sets from what is docked right now (owner ruling
    /// 2026-10-07). <see cref="RouteEndpointPartScope"/> restricts deliveries, capacity reads and
    /// debits to the parts a route RECORDED as the endpoint's, and stock records no dock time, so a
    /// module the player permanently docks to a station after recording its route reads exactly
    /// like a visitor and gets no cargo. Adopting the endpoint's current parts lets the player opt
    /// such a module in: the scope consults the adopted set BEFORE the recorded sets and admits
    /// every docked piece holding an adopted part, while a craft docked after the adoption still
    /// falls outside it. The player is responsible for having no visitor docked at that moment;
    /// the action captures the resolved vessel whole.
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

        /// <summary>
        /// Resolves one endpoint and reads its current parts. Returns false with a reason when
        /// the endpoint does not resolve to a live vessel. May rebind the route's endpoint as a
        /// side effect (the live resolver runs <see cref="RouteEndpointTransfer"/>).
        /// </summary>
        internal delegate bool EndpointPartCapture(
            RouteEndpoint endpoint, out string vesselName, out List<uint> partFlightIds, out string reason);

        /// <summary>What the action did for one endpoint.</summary>
        internal struct EndpointAdoptionResult
        {
            public bool IsOrigin;
            public int StopIndex;
            public bool Adopted;
            public string VesselName;
            /// <summary>Parts adopted (0 when not adopted).</summary>
            public int PartCount;
            /// <summary>Parts in the endpoint's adoption before this call; -1 when it had none.</summary>
            public int PreviousPartCount;
            /// <summary>Why the endpoint was not adopted; null when it was.</summary>
            public string Reason;
        }

        /// <summary>What one <see cref="AdoptCurrentParts(Route, double, EndpointPartCapture)"/> call did.</summary>
        internal sealed class AdoptionResult
        {
            public readonly List<EndpointAdoptionResult> Endpoints = new List<EndpointAdoptionResult>();
            public int AdoptedCount;
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
            List<RouteEndpointAdoptedParts> entries = route?.AdoptedEndpointParts;
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
        /// Live entry point: adopts every resolvable endpoint's current parts at the current UT.
        /// Resolution is <see cref="RouteEndpointResolver.TryResolveEndpoint"/>, the same lookup
        /// the deliveries use; the parts are read from the store the writers use
        /// (<see cref="RouteOrchestrator.EndpointStoreIsLiveParts(Vessel)"/>).
        /// </summary>
        internal static AdoptionResult AdoptCurrentParts(Route route)
        {
            return AdoptCurrentParts(route, ReadUniversalTime(), TryCaptureLive);
        }

        /// <summary>
        /// The action. For the origin (unless the route has no physical origin: KSC or harvest)
        /// and every stop, in that order: capture the endpoint through
        /// <paramref name="capture"/>, then key the captured set to the endpoint binding the route
        /// holds AFTER the capture (a resolution can rebind it) and replace any adoption for that
        /// binding. An endpoint that does not resolve keeps whatever adoption it had. Entries whose
        /// binding no current endpoint matches are pruned. Logs one Info line with the per-endpoint
        /// counts. Never throws on a null route or capture.
        /// </summary>
        internal static AdoptionResult AdoptCurrentParts(Route route, double ut, EndpointPartCapture capture)
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
                AdoptOne(route, isOrigin: true, stopIndex: -1, ut, capture, entries, result);
            if (route.Stops != null)
            {
                for (int s = 0; s < route.Stops.Count; s++)
                {
                    if (route.Stops[s] == null) continue;
                    AdoptOne(route, isOrigin: false, stopIndex: s, ut, capture, entries, result);
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

        private static void AdoptOne(
            Route route, bool isOrigin, int stopIndex, double ut, EndpointPartCapture capture,
            List<RouteEndpointAdoptedParts> entries, AdoptionResult result)
        {
            RouteEndpoint before = isOrigin ? route.Origin : route.Stops[stopIndex].Endpoint;
            var item = new EndpointAdoptionResult
            {
                IsOrigin = isOrigin,
                StopIndex = stopIndex,
                PreviousPartCount = -1,
            };

            bool resolved = capture(before, out string vesselName, out List<uint> partFlightIds, out string reason);
            item.VesselName = vesselName;

            // The resolution may have rebound the endpoint; the adoption belongs to the binding
            // the route holds now.
            RouteEndpoint after = isOrigin ? route.Origin : route.Stops[stopIndex].Endpoint;
            int existing = FindEntryIndex(entries, after);
            if (existing >= 0)
                item.PreviousPartCount = entries[existing].PartFlightIds?.Count ?? 0;

            var ids = new HashSet<uint>();
            if (resolved && partFlightIds != null)
            {
                for (int i = 0; i < partFlightIds.Count; i++)
                    if (partFlightIds[i] != 0u) ids.Add(partFlightIds[i]);
            }

            if (!resolved)
                item.Reason = string.IsNullOrEmpty(reason) ? "unresolved" : reason;
            else if (ids.Count == 0)
                item.Reason = "no-readable-parts";
            else if (after.RootPartUId == 0u && after.VesselPersistentId == 0u)
                item.Reason = "no-endpoint-identity";

            if (item.Reason != null)
            {
                result.UnresolvedCount++;
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
            result.AdoptedCount++;
            result.Endpoints.Add(item);
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
        /// <c>Endpoint part adoption: route=X ut=U endpoints=N adopted=A unresolved=M pruned=P
        /// [origin:'Depot' parts=12 prev=none; stop1:'Station' parts=31 prev=28;
        /// stop2:unresolved('reason') kept=4]</c>. Stop numbers are 1-based; <c>kept=K</c>
        /// names an unreached endpoint's surviving adoption.
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
                }
                else
                {
                    sb.Append("unresolved('").Append(e.Reason ?? string.Empty).Append("')");
                    if (e.PreviousPartCount >= 0)
                        sb.Append(" kept=").Append(e.PreviousPartCount.ToString(IC));
                }
            }
            return "Endpoint part adoption: route=" + RouteIds.Short(route)
                + " ut=" + ut.ToString("R", IC)
                + " endpoints=" + result.Endpoints.Count.ToString(IC)
                + " adopted=" + result.AdoptedCount.ToString(IC)
                + " unresolved=" + result.UnresolvedCount.ToString(IC)
                + " pruned=" + result.PrunedCount.ToString(IC)
                + " [" + sb + "]"
                + " - deliveries, capacity and debits now treat every adopted part as the endpoint's own";
        }

        // ------------------------------------------------------------------
        // Live capture
        // ------------------------------------------------------------------

        /// <summary>
        /// Resolves <paramref name="endpoint"/> the way the deliveries do and reads every part's
        /// flightID from the branch the writers use (live parts when loaded, the proto snapshot
        /// otherwise). NoInlining keeps the stock reads out of the pure action's JIT.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool TryCaptureLive(
            RouteEndpoint endpoint, out string vesselName, out List<uint> partFlightIds, out string reason)
        {
            vesselName = null;
            partFlightIds = null;
            try
            {
                if (!RouteEndpointResolver.TryResolveEndpoint(endpoint, out Vessel vessel, out reason)
                    || vessel == null)
                {
                    if (string.IsNullOrEmpty(reason)) reason = "unresolved";
                    return false;
                }
                vesselName = vessel.vesselName;
                partFlightIds = ReadPartFlightIds(vessel, RouteOrchestrator.EndpointStoreIsLiveParts(vessel));
                reason = null;
                return true;
            }
            catch (Exception ex)
            {
                reason = "threw-" + ex.GetType().Name;
                return false;
            }
        }

        private static List<uint> ReadPartFlightIds(Vessel vessel, bool isLoaded)
        {
            var ids = new List<uint>();
            if (isLoaded)
            {
                if (vessel.parts == null) return ids;
                for (int i = 0; i < vessel.parts.Count; i++)
                {
                    Part p = vessel.parts[i];
                    if (p != null && p.flightID != 0u) ids.Add(p.flightID);
                }
                return ids;
            }
            List<ProtoPartSnapshot> snapshots = vessel.protoVessel?.protoPartSnapshots;
            if (snapshots == null) return ids;
            for (int i = 0; i < snapshots.Count; i++)
            {
                ProtoPartSnapshot pps = snapshots[i];
                if (pps != null && pps.flightID != 0u) ids.Add(pps.flightID);
            }
            return ids;
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
