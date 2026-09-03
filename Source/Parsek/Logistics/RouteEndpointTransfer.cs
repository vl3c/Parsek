using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// ENDPOINT TRANSFER (operator ruling 2026-09-04): "if the destination vessel is no
    /// longer there, but there is another vessel within 500 m, transfer the route to that
    /// endpoint."
    ///
    /// <para>Before this, <see cref="RouteEndpointResolver"/>'s SurfaceProximity step already
    /// DELIVERED into the nearby craft, but the substitution lived only in the log: the
    /// persisted <see cref="RouteStop.Endpoint"/> still named the vanished vessel, so every
    /// cycle re-resolved by position and the player was never told (todo entry
    /// ROUTE-DELIVERY-PROXIMITY-RETARGETS-ANY-NEARBY-VESSEL, measured by harness lane
    /// RVR-18). This class turns that substitution into a TRANSFER: the stop (or the origin)
    /// is REBOUND to the resolved vessel, so the next cycle resolves it by identity, the
    /// produced save names the new vessel, and one screen message announces the change.</para>
    ///
    /// <para>THE GUARD. The route's own transport - the vessel of the route's source
    /// recordings (<see cref="Route.SourceRefs"/>) - is excluded from the proximity candidate
    /// set. RVR-18 measured it as the runner-up at 16.42 m, and paying a route's own carrier
    /// is exactly the "a route paying itself" case the root-part step was introduced to
    /// prevent for origins. Comparison is <see cref="VesselLaunchIdentity"/>-shaped: pid match
    /// unless the launch guids conclusively differ.</para>
    ///
    /// <para>WHAT IS REBOUND, AND WHAT IS NOT. The identity fields move
    /// (<see cref="RouteEndpoint.VesselPersistentId"/> and
    /// <see cref="RouteEndpoint.RootPartUId"/>, the latter taken from the resolved vessel so
    /// the NEXT resolution wins at the launch-unique root-part step rather than on a
    /// craft-baked pid). The POSITION fields (body / lat / lon / altitude / isSurface) stay as
    /// the historical dock anchor: they are what the 500 m radius is measured from, and moving
    /// the anchor onto each successive substitute would let a route walk across the landscape
    /// one transfer at a time. Nothing is added to the on-disk shape - both identity fields
    /// already round-trip through <see cref="RouteNodeCodec.SerializeEndpoint"/>, so no schema
    /// generation moves.</para>
    ///
    /// <para>THE ONE CONSTRAINT WORTH KNOWING. Both halves - the guard and the rebind - need
    /// to know which route owns the endpoint, and the resolver is handed a COPY of the struct,
    /// so the way back is a value match over <c>RouteStore.CommittedRoutes</c>. A caller that
    /// re-resolved a STALE copy after a transfer would find no owner and therefore get no
    /// transport exclusion; every production caller reads <c>stop.Endpoint</c> /
    /// <c>route.Origin</c> at call time, which is also why a transfer announces itself exactly
    /// once per tick no matter how many sites resolve the same stop.</para>
    /// </summary>
    internal static class RouteEndpointTransfer
    {
        private const string Tag = "Route";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>Which endpoint of a route a resolution was for; only the label differs.</summary>
        internal enum EndpointRole
        {
            Destination = 0,
            Pickup = 1,
            Origin = 2,
        }

        /// <summary>
        /// The launch-unique identity pair used for every vessel comparison here: a
        /// craft-baked <c>persistentId</c> plus KSP's per-launch <c>Vessel.id</c> guid when it
        /// is known. An unknown (null/empty) guid means "no evidence", never "differs" - the
        /// <see cref="VesselLaunchIdentity"/> contract.
        /// </summary>
        internal struct VesselIdentity
        {
            public uint Pid;
            public string LaunchGuid;
        }

        /// <summary>Outcome of <see cref="Evaluate"/>.</summary>
        internal enum TransferDecision
        {
            Keep = 0,
            Transfer = 1,
        }

        /// <summary>
        /// THE DECISION, and the only place it exists. Pure: given the identity the endpoint
        /// RECORDED and the identity of the vessel the resolver actually landed on, plus which
        /// step landed it, decide whether the persisted endpoint must be rebound.
        ///
        /// <para>Arms, in evaluation order:</para>
        /// <list type="bullet">
        /// <item>An unknown resolved pid is never a transfer - there is nothing to rebind to.</item>
        /// <item>The ROOT-PART step resolves the recorded identity BY identity, so whatever pid
        /// it lands on is the same physical vessel: never a transfer.</item>
        /// <item>The PID step returns the recorded pid by construction, so it is a transfer only
        /// when the launch guids CONCLUSIVELY differ - i.e. a different launch of the same craft
        /// file wearing the same baked pid. A <see cref="RouteEndpoint"/> carries no launch guid
        /// today (filed as RESOLVER-PID-STEP-NOT-GUID-GATED), so production always passes an
        /// unknown recorded guid here and this arm stays unreachable until an endpoint persists
        /// one; the arm exists so that the day it does, the mirror direction is already decided
        /// rather than discovered.</item>
        /// <item>The SURFACE-PROXIMITY step is positional, so any vessel that is not the recorded
        /// one is a transfer. Same pid with guids that do not conclusively differ is the ordinary
        /// "the depot drifted a few metres" case and stays a Keep.</item>
        /// </list>
        /// </summary>
        internal static TransferDecision Evaluate(
            uint recordedPid,
            string recordedLaunchGuid,
            uint resolvedPid,
            string resolvedLaunchGuid,
            RouteEndpointResolver.EndpointResolutionStep step,
            out string reason)
        {
            if (resolvedPid == 0u)
            {
                reason = "resolved-pid-unknown";
                return TransferDecision.Keep;
            }

            if (step == RouteEndpointResolver.EndpointResolutionStep.RootPart)
            {
                reason = "root-identity-match";
                return TransferDecision.Keep;
            }

            bool guidsDiffer = VesselLaunchIdentity.GuidsConclusivelyDiffer(
                recordedLaunchGuid, resolvedLaunchGuid);

            if (step == RouteEndpointResolver.EndpointResolutionStep.Pid)
            {
                if (recordedPid != resolvedPid)
                {
                    // Defensive: the pid step matches on the recorded pid, so this cannot
                    // happen today. If a future step ever returns a different pid under this
                    // label, treat it the same as a positional substitute rather than
                    // silently keeping a stale binding.
                    reason = "pid-substitute";
                    return TransferDecision.Transfer;
                }
                if (guidsDiffer)
                {
                    reason = "pid-different-launch";
                    return TransferDecision.Transfer;
                }
                reason = "pid-same-launch";
                return TransferDecision.Keep;
            }

            if (step == RouteEndpointResolver.EndpointResolutionStep.SurfaceProximity)
            {
                if (recordedPid != resolvedPid)
                {
                    reason = "proximity-substitute";
                    return TransferDecision.Transfer;
                }
                if (guidsDiffer)
                {
                    reason = "proximity-different-launch";
                    return TransferDecision.Transfer;
                }
                reason = "proximity-same-vessel";
                return TransferDecision.Keep;
            }

            reason = "no-step";
            return TransferDecision.Keep;
        }

        /// <summary>
        /// Pure transport test: is this candidate one of the route's own transports? Guid
        /// first, pid fallback - a pid match counts unless a known guid on BOTH sides
        /// conclusively disagrees, which is the same rule every other identity site in Parsek
        /// uses. <paramref name="reason"/> is <c>route-own-transport</c> on a hit and empty
        /// otherwise, so callers log one stable token.
        /// </summary>
        internal static bool IsRouteTransport(
            uint candidatePid,
            string candidateLaunchGuid,
            IReadOnlyList<VesselIdentity> transports,
            out string reason)
        {
            reason = string.Empty;
            if (candidatePid == 0u || transports == null || transports.Count == 0)
                return false;

            for (int i = 0; i < transports.Count; i++)
            {
                VesselIdentity t = transports[i];
                if (t.Pid == 0u || t.Pid != candidatePid) continue;
                if (VesselLaunchIdentity.GuidsConclusivelyDiffer(t.LaunchGuid, candidateLaunchGuid))
                    continue;
                reason = "route-own-transport";
                return true;
            }

            return false;
        }

        /// <summary>
        /// One route endpoint that a given <see cref="RouteEndpoint"/> value belongs to.
        /// <see cref="StopIndex"/> is -1 for <see cref="EndpointRole.Origin"/>.
        /// </summary>
        internal struct EndpointOwner
        {
            public Route Route;
            public EndpointRole Role;
            public int StopIndex;
        }

        /// <summary>
        /// Pure owner search: which persisted endpoint(s) carry exactly this value. The
        /// resolver is handed a COPY of the struct, so the way back to the field that must be
        /// rebound is a value match over the route list. Ambiguity is real and benign - two
        /// routes may deliver to the same depot - and every match names the same recorded
        /// vessel, so all of them are rebound to the same resolution.
        /// </summary>
        internal static List<EndpointOwner> FindOwners(
            IReadOnlyList<Route> routes, RouteEndpoint endpoint)
        {
            var owners = new List<EndpointOwner>();
            if (routes == null) return owners;

            for (int i = 0; i < routes.Count; i++)
            {
                Route route = routes[i];
                if (route == null) continue;

                if (SameEndpoint(route.Origin, endpoint))
                {
                    owners.Add(new EndpointOwner
                    {
                        Route = route,
                        Role = EndpointRole.Origin,
                        StopIndex = -1,
                    });
                }

                if (route.Stops == null) continue;
                for (int s = 0; s < route.Stops.Count; s++)
                {
                    RouteStop stop = route.Stops[s];
                    if (stop == null) continue;
                    if (!SameEndpoint(stop.Endpoint, endpoint)) continue;
                    owners.Add(new EndpointOwner
                    {
                        Route = route,
                        Role = ClassifyStopRole(stop),
                        StopIndex = s,
                    });
                }
            }

            return owners;
        }

        /// <summary>
        /// A stop endpoint serves BOTH directions: the delivery manifests write cargo INTO it
        /// and the pickup manifests take cargo OUT of it (the pickup-source gate resolves the
        /// very same <see cref="RouteStop.Endpoint"/>). The role here is a LABEL for the log
        /// and the screen message only - the rebind is identical either way - so a stop that
        /// does both is labelled by its delivery direction.
        /// </summary>
        internal static EndpointRole ClassifyStopRole(RouteStop stop)
        {
            if (stop == null) return EndpointRole.Destination;
            bool delivers = (stop.DeliveryManifest != null && stop.DeliveryManifest.Count > 0)
                || (stop.InventoryDeliveryManifest != null && stop.InventoryDeliveryManifest.Count > 0);
            if (delivers) return EndpointRole.Destination;
            bool picks = (stop.PickupManifest != null && stop.PickupManifest.Count > 0)
                || (stop.InventoryPickupManifest != null && stop.InventoryPickupManifest.Count > 0);
            return picks ? EndpointRole.Pickup : EndpointRole.Destination;
        }

        /// <summary>
        /// Value equality over every persisted <see cref="RouteEndpoint"/> field. The
        /// coordinates are compared EXACTLY on purpose: the value being matched is a copy of
        /// the same struct the route holds, never a recomputed one, so a tolerance would only
        /// widen the match onto a neighbouring stop.
        /// </summary>
        internal static bool SameEndpoint(RouteEndpoint a, RouteEndpoint b)
        {
            return a.VesselPersistentId == b.VesselPersistentId
                && a.RootPartUId == b.RootPartUId
                && string.Equals(a.BodyName, b.BodyName, StringComparison.Ordinal)
                && a.Latitude.Equals(b.Latitude)
                && a.Longitude.Equals(b.Longitude)
                && a.Altitude.Equals(b.Altitude)
                && a.IsSurface == b.IsSurface;
        }

        /// <summary>
        /// Pure transport collection: the vessel identity of each of the route's source
        /// recordings. <paramref name="recordingById"/> is the ERS lookup on the live side and
        /// a dictionary in tests. Deduped by pid; a recording with no pid contributes nothing
        /// (there is no identity to exclude on).
        /// </summary>
        internal static List<VesselIdentity> CollectTransportIdentities(
            Route route, Func<string, Recording> recordingById)
        {
            var transports = new List<VesselIdentity>();
            if (route == null || route.SourceRefs == null || recordingById == null)
                return transports;

            for (int i = 0; i < route.SourceRefs.Count; i++)
            {
                RouteSourceRef sref = route.SourceRefs[i];
                if (sref == null || string.IsNullOrEmpty(sref.RecordingId)) continue;
                Recording rec = recordingById(sref.RecordingId);
                if (rec == null || rec.VesselPersistentId == 0u) continue;

                bool known = false;
                for (int t = 0; t < transports.Count; t++)
                {
                    if (transports[t].Pid != rec.VesselPersistentId) continue;
                    known = true;
                    break;
                }
                if (known) continue;

                transports.Add(new VesselIdentity
                {
                    Pid = rec.VesselPersistentId,
                    LaunchGuid = rec.RecordedVesselGuid,
                });
            }

            return transports;
        }

        /// <summary>
        /// The transfer log line, built purely so a test can pin the exact grep-stable text
        /// the harness lanes read. Every number is InvariantCulture; the distance is F2 metres
        /// and reads <c>-</c> when the step that resolved carries no distance (the pid arm).
        /// </summary>
        internal static string FormatTransferLine(
            string routeId,
            EndpointRole role,
            int stopIndex,
            uint oldPid,
            string oldName,
            uint newPid,
            string newName,
            RouteEndpointResolver.EndpointResolutionStep step,
            double distanceMeters,
            bool distanceKnown,
            string bodyName,
            double ut)
        {
            return "Route endpoint transferred: route=" + ShortId(routeId)
                + " stop=" + stopIndex.ToString(IC)
                + " role=" + RoleToken(role)
                + " from=" + oldPid.ToString(IC) + "/'" + NameOrUnknown(oldName) + "'"
                + " to=" + newPid.ToString(IC) + "/'" + NameOrUnknown(newName) + "'"
                + " step=" + StepToken(step)
                + " distance=" + (distanceKnown ? distanceMeters.ToString("F2", IC) : "-")
                + " body=" + (string.IsNullOrEmpty(bodyName) ? "<none>" : bodyName)
                + " at ut=" + ut.ToString("R", IC);
        }

        /// <summary>
        /// The one-shot player message for a transfer. It qualifies under the no-new-surfaces
        /// rule because it announces an EVENT that changed persisted state, and it cannot
        /// repeat: the stop is rebound, so the next cycle resolves at the identity steps and
        /// never reaches this branch again.
        /// </summary>
        internal static string FormatTransferScreenMessage(
            string routeName, EndpointRole role, string oldName, string newName)
        {
            string route = string.IsNullOrEmpty(routeName) ? "Supply route" : "Supply route " + routeName;
            string verb = role == EndpointRole.Destination ? "now delivering to " : "now loading from ";
            string lost = string.IsNullOrEmpty(oldName)
                ? (role == EndpointRole.Destination ? "destination not found, " : "source not found, ")
                : oldName + " not found, ";
            return route + ": " + lost + verb + NameOrUnknown(newName);
        }

        internal static string RoleToken(EndpointRole role)
        {
            if (role == EndpointRole.Origin) return "origin";
            if (role == EndpointRole.Pickup) return "pickup";
            return "destination";
        }

        internal static string StepToken(RouteEndpointResolver.EndpointResolutionStep step)
        {
            if (step == RouteEndpointResolver.EndpointResolutionStep.SurfaceProximity) return "proximity";
            if (step == RouteEndpointResolver.EndpointResolutionStep.Pid) return "pid";
            if (step == RouteEndpointResolver.EndpointResolutionStep.RootPart) return "root-part";
            return "none";
        }

        private static string NameOrUnknown(string name)
            => string.IsNullOrEmpty(name) ? "<unknown>" : name;

        internal static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "<none>";
            return id.Length <= 8 ? id : id.Substring(0, 8);
        }

        // -----------------------------------------------------------------
        // Live side. Everything below touches KSP / the stores; the decisions
        // above stay reachable headlessly.
        // -----------------------------------------------------------------

        /// <summary>
        /// The committed routes that carry this endpoint value. Dormant routes are
        /// deliberately NOT scanned: they are the rewind subsystem's held copies, are never
        /// resolved at runtime, and rebinding one would edit state a re-fly is expected to
        /// restore verbatim.
        /// </summary>
        internal static List<EndpointOwner> FindOwnersLive(RouteEndpoint endpoint)
        {
            try
            {
                return FindOwners(RouteStore.CommittedRoutes, endpoint);
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    "Endpoint owner lookup threw " + ex.GetType().Name + ": " + ex.Message
                    + "; treating endpoint as unowned");
                return new List<EndpointOwner>();
            }
        }

        /// <summary>
        /// The union of every owning route's transports, resolved through the ERS (never a raw
        /// committed-recordings read, so the ERS/ELS grep gate stays green). Only reached on
        /// the proximity step, which is itself the rare path.
        /// </summary>
        internal static List<VesselIdentity> CollectTransportIdentitiesLive(
            IReadOnlyList<EndpointOwner> owners)
        {
            var transports = new List<VesselIdentity>();
            if (owners == null || owners.Count == 0) return transports;

            Dictionary<string, Recording> byId = ErsById();
            if (byId == null || byId.Count == 0) return transports;

            Func<string, Recording> lookup = id =>
            {
                Recording found;
                return byId.TryGetValue(id, out found) ? found : null;
            };

            for (int i = 0; i < owners.Count; i++)
            {
                List<VesselIdentity> routeTransports =
                    CollectTransportIdentities(owners[i].Route, lookup);
                for (int t = 0; t < routeTransports.Count; t++)
                {
                    bool known = false;
                    for (int k = 0; k < transports.Count; k++)
                    {
                        if (transports[k].Pid != routeTransports[t].Pid) continue;
                        known = true;
                        break;
                    }
                    if (!known) transports.Add(routeTransports[t]);
                }
            }

            return transports;
        }

        // The ERS by id, memoized on the ERS list INSTANCE. EffectiveState.ComputeERS is
        // itself cached and hands back the same list until a state version moves, so
        // reference equality is an exact invalidation: a route whose endpoint is permanently
        // lost re-reaches the proximity step every frame the Logistics window draws it, and
        // rebuilding this dictionary each time would be a per-frame allocation for nothing.
        // Single-threaded by construction (the background-thread grep gate keeps it so).
        private static IReadOnlyList<Recording> cachedErs;
        private static Dictionary<string, Recording> cachedErsById;

        private static Dictionary<string, Recording> ErsById()
        {
            IReadOnlyList<Recording> ers = RouteOrchestrator.SafeComputeErs();
            if (ers == null || ers.Count == 0)
            {
                cachedErs = null;
                cachedErsById = null;
                return null;
            }
            if (ReferenceEquals(ers, cachedErs) && cachedErsById != null)
                return cachedErsById;

            var byId = new Dictionary<string, Recording>(StringComparer.Ordinal);
            for (int i = 0; i < ers.Count; i++)
            {
                Recording rec = ers[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) continue;
                byId[rec.RecordingId] = rec;
            }
            cachedErs = ers;
            cachedErsById = byId;
            return byId;
        }

        /// <summary>
        /// Apply the ruling: rebind every owning endpoint that <see cref="Evaluate"/> says
        /// must transfer, log one grep-stable Info line per rebind, and post ONE screen
        /// message per rebind. Returns the number of endpoints rebound.
        /// </summary>
        internal static int ApplyTransfers(
            IReadOnlyList<EndpointOwner> owners,
            RouteEndpoint recorded,
            uint resolvedPid,
            string resolvedLaunchGuid,
            string resolvedName,
            uint resolvedRootPartFlightId,
            RouteEndpointResolver.EndpointResolutionStep step,
            double distanceMeters,
            bool distanceKnown,
            double ut)
        {
            if (owners == null || owners.Count == 0) return 0;

            string decisionReason;
            if (Evaluate(recorded.VesselPersistentId, null, resolvedPid, resolvedLaunchGuid,
                    step, out decisionReason) != TransferDecision.Transfer)
            {
                ParsekLog.Verbose(Tag,
                    "Endpoint transfer skipped: recordedPid="
                    + recorded.VesselPersistentId.ToString(IC)
                    + " resolvedPid=" + resolvedPid.ToString(IC)
                    + " step=" + StepToken(step)
                    + " reason=" + decisionReason);
                return 0;
            }

            // Headless guard: the name probe is the only FlightGlobals read on this path and
            // mono runs the failing FlightGlobals initializer at JIT of the CALLING method, so
            // the probe is a NoInlining core AND its call site carries its own catch. Without
            // both, an xUnit cell that drives a transfer dies on the static initializer
            // instead of asserting the rebind.
            string oldName = null;
            try
            {
                oldName = TryResolveLiveVesselName(recorded.VesselPersistentId);
            }
            catch
            {
                // No live name available (headless, or a scene mid-teardown): the line reads
                // '<unknown>', which is the normal case anyway - a transferred-from vessel is
                // usually gone.
            }
            int rebound = 0;

            for (int i = 0; i < owners.Count; i++)
            {
                EndpointOwner owner = owners[i];
                if (owner.Route == null) continue;

                RouteEndpoint rebind = recorded;
                rebind.VesselPersistentId = resolvedPid;
                // The resolved vessel's root part flightID is launch-unique where the pid is
                // craft-baked, so stamping it makes the NEXT resolution win at the root-part
                // step instead of walking to proximity again. Zero (unknown) clears the stale
                // recorded root rather than leaving one that names the vanished vessel.
                rebind.RootPartUId = resolvedRootPartFlightId;

                if (owner.Role == EndpointRole.Origin)
                {
                    owner.Route.Origin = rebind;
                }
                else
                {
                    if (owner.Route.Stops == null
                        || owner.StopIndex < 0
                        || owner.StopIndex >= owner.Route.Stops.Count
                        || owner.Route.Stops[owner.StopIndex] == null)
                    {
                        continue;
                    }
                    owner.Route.Stops[owner.StopIndex].Endpoint = rebind;
                }

                rebound++;
                ParsekLog.Info(Tag, FormatTransferLine(
                    owner.Route.Id, owner.Role, owner.StopIndex,
                    recorded.VesselPersistentId, oldName,
                    resolvedPid, resolvedName,
                    step, distanceMeters, distanceKnown,
                    recorded.BodyName, ut));
                ParsekLog.ScreenMessage(
                    FormatTransferScreenMessage(owner.Route.Name, owner.Role, oldName, resolvedName),
                    6f);
            }

            return rebound;
        }

        /// <summary>
        /// Best-effort live name for the pid the endpoint RECORDED. Normally null - the whole
        /// point of a transfer is that this vessel is gone - but a different-launch pid match
        /// still has one.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static string TryResolveLiveVesselName(uint pid)
        {
            if (pid == 0u) return null;
            try
            {
                Vessel found;
                if (FlightGlobals.fetch != null && FlightGlobals.FindVessel(pid, out found) && found != null)
                    return found.vesselName;
            }
            catch
            {
                // Defensive, same rationale as the resolver's own probes: a stock-side null
                // during scene teardown surfaces as an unknown name, never a crash.
            }
            return null;
        }

        /// <summary>
        /// The live vessel's launch guid in the shape <see cref="VesselLaunchIdentity"/>
        /// normalizes, or null when it cannot be read.
        /// </summary>
        internal static string TryReadLaunchGuid(Vessel v)
        {
            if (v == null) return null;
            try
            {
                return v.id == Guid.Empty ? null : v.id.ToString("N", IC);
            }
            catch
            {
                return null;
            }
        }
    }
}
