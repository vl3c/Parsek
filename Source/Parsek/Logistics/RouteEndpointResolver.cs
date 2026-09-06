using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek.Logistics
{
    /// <summary>
    /// Resolves a saved <see cref="RouteEndpoint"/> to a live <c>Vessel</c>.
    /// First tries the O(1) persistent-id lookup; if that misses and the
    /// endpoint is surface-typed, falls back to a great-circle proximity
    /// search bounded by <see cref="RouteOrchestrator.SurfaceProximityRadiusMeters"/>.
    /// Ghost map vessels are always excluded.
    ///
    /// The proximity search is split off into a pure helper
    /// (<see cref="TrySurfaceFallbackPure"/>) that takes a flat list of
    /// <see cref="SurfaceVesselSnapshot"/> records so xUnit can exercise the
    /// branch without constructing live <c>Vessel</c> instances.
    ///
    /// <para>THE PROXIMITY STEP IS A TRANSFER, NOT A SUBSTITUTION (operator ruling
    /// 2026-09-04). When it lands on a vessel other than the recorded one, the persisted
    /// endpoint is REBOUND to it by <see cref="RouteEndpointTransfer"/> - so the next cycle
    /// resolves it by identity, the save names the new vessel, and the player is told once -
    /// and the owning route's own TRANSPORT is excluded from the candidate set, because a
    /// route's carrier parked back at the dock is routinely the nearest surface vessel to the
    /// recorded coordinates. Both need to know which route owns the endpoint, which is why
    /// that lookup happens on this step and nowhere earlier.</para>
    /// </summary>
    internal static class RouteEndpointResolver
    {
        /// <summary>
        /// Minimal POCO surface used by <see cref="TrySurfaceFallbackPure"/>.
        /// Production callers convert the live <c>Vessel</c> list to this shape
        /// before invoking the pure helper; tests construct records directly.
        /// </summary>
        /// <summary>
        /// One step of endpoint resolution, in the order they are attempted.
        /// <see cref="EndpointResolutionStep.None"/> means "no step left to try".
        /// </summary>
        internal enum EndpointResolutionStep
        {
            None = 0,
            RootPart = 1,
            Pid = 2,
            SurfaceProximity = 3,
        }

        /// <summary>
        /// THE STEP ORDER, and the ONLY place it exists. Pure / static: given the step just
        /// attempted and which inputs the endpoint carries, returns the next step to try.
        /// <see cref="TryResolveEndpoint"/> drives itself from this and holds no ordering of
        /// its own, so a reordering here changes production behaviour AND reds
        /// <c>RouteEndpointStepOrderTests</c> - which is the point: an order expressed as the
        /// sequence of if-blocks in the caller could be swapped with the whole suite green.
        ///
        /// <para>WHY ROOT-PART IS FIRST. A start-docked origin carries NO pid (the depot's
        /// own <c>Vessel</c> is destroyed by <c>Part.Couple</c> at the dock), so if proximity
        /// ran first the origin would resolve to whatever surface vessel is nearest the
        /// recorded coordinates - routinely the TRANSPORT parked back at the depot, i.e. a
        /// route paying itself. Identity must beat position, and a known identity must beat a
        /// pid too, because a <c>persistentId</c> is craft-baked and can name a different
        /// launch of the same craft file where a part <c>flightID</c> cannot.</para>
        /// </summary>
        internal static EndpointResolutionStep NextEndpointStep(
            EndpointResolutionStep previous,
            bool rootIdKnown,
            bool pidKnown,
            bool proximityEligible)
        {
            if (previous == EndpointResolutionStep.None && rootIdKnown)
                return EndpointResolutionStep.RootPart;
            if (previous <= EndpointResolutionStep.RootPart && pidKnown)
                return EndpointResolutionStep.Pid;
            if (previous <= EndpointResolutionStep.Pid && proximityEligible)
                return EndpointResolutionStep.SurfaceProximity;
            return EndpointResolutionStep.None;
        }

        /// <summary>
        /// Pure whole-resolution walk over <see cref="NextEndpointStep"/>: given which inputs
        /// exist and which steps WOULD match, returns the step that actually wins (or
        /// <see cref="EndpointResolutionStep.None"/>). Exists so the ORDER can be pinned
        /// headlessly against the same function production drives - no live
        /// <c>FlightGlobals</c>, no <c>Vessel</c>.
        /// </summary>
        internal static EndpointResolutionStep ResolveEndpointStepPure(
            bool rootIdKnown, bool rootMatches,
            bool pidKnown, bool pidMatches,
            bool proximityEligible, bool proximityMatches)
        {
            EndpointResolutionStep step = EndpointResolutionStep.None;
            while (true)
            {
                step = NextEndpointStep(step, rootIdKnown, pidKnown, proximityEligible);
                switch (step)
                {
                    case EndpointResolutionStep.RootPart:
                        if (rootMatches) return step;
                        break;
                    case EndpointResolutionStep.Pid:
                        if (pidMatches) return step;
                        break;
                    case EndpointResolutionStep.SurfaceProximity:
                        if (proximityMatches) return step;
                        break;
                    default:
                        return EndpointResolutionStep.None;
                }
            }
        }

        /// <summary>
        /// Minimal POCO for the ROOT-PART identity step. Deliberately unfiltered by body
        /// or situation: a root-part id names one physical vessel wherever it is, so an
        /// orbital depot resolves through this step even though it can never reach the
        /// surface fallback.
        /// </summary>
        internal struct RootIdVesselSnapshot
        {
            public uint PersistentId;
            /// <summary>Root part flightID; 0 when neither the live parts nor the proto
            /// snapshot could supply one (the vessel is then never a root match).</summary>
            public uint RootPartFlightId;
            /// <summary>
            /// EVERY part flightID this vessel carries, for the DOCKED-COMPOSITE arm.
            /// <c>Part.Couple</c> merges two craft into the DOMINANT half's <c>Vessel</c>
            /// (<c>Vessel.GetDominantVessel</c>: higher <c>VesselType</c>, then heavier, then
            /// larger guid), and the absorbed half's root part stays aboard as an ORDINARY
            /// part - re-parented, never renumbered. So a base with a dominant visitor docked
            /// to it is no vessel's ROOT any more, and matching only on
            /// <see cref="RootPartFlightId"/> would lose it for exactly as long as the pair
            /// stays docked. Null / empty in pure-test contexts that only exercise the root
            /// arm.
            /// </summary>
            public IReadOnlyList<uint> PartFlightIds;
            /// <summary>The live <c>Vessel</c> for the resolver to return; null in pure-test contexts.</summary>
            public Vessel Vessel;
        }

        internal struct SurfaceVesselSnapshot
        {
            public uint PersistentId;
            /// <summary>
            /// KSP's per-launch <c>Vessel.id</c> guid, normalized, when it can be read;
            /// null/empty means unknown. Only the transport exclusion reads it, and an unknown
            /// guid there means "no evidence", never "a different launch" (the
            /// <see cref="VesselLaunchIdentity"/> contract).
            /// </summary>
            public string LaunchGuid;
            public string BodyName;
            public Vessel.Situations Situation;
            /// <summary>World-space position used for distance comparison.</summary>
            public Vector3d WorldPosition;
            /// <summary>The live <c>Vessel</c> reference for the resolver to return; null in pure-test contexts.</summary>
            public Vessel Vessel;
        }

        /// <summary>
        /// Production entry point. Three steps, in this order: ROOT PART ID, then PID,
        /// then surface proximity. Returns <c>false</c> with a stable reason token on
        /// failure. Logs which step resolved.
        ///
        /// <para>THE ROOT-PART STEP IS FIRST AND THAT ORDER IS THE CONTRACT. A start-docked
        /// origin carries no pid at all (the depot's <c>Vessel</c> is destroyed by
        /// <c>Part.Couple</c> at the dock), so without it the origin would fall straight
        /// through to proximity and resolve to WHATEVER surface vessel is nearest the
        /// recorded coordinates - which, for a shuttle route, is routinely the TRANSPORT
        /// parked back at the depot. That is exactly the "deducts from the origin depot,
        /// NOT the transport" case the design doc names, so the identity has to win before
        /// any positional guess is made.</para>
        ///
        /// <para>Guid-free BY CONSTRUCTION, not by omission: a part <c>flightID</c> is
        /// assigned per launch and is never written into the <c>.craft</c>, so it cannot
        /// collide across launches the way a <c>persistentId</c> does and there is nothing
        /// for a launch-guid gate to disambiguate. It also survives the split that creates
        /// the depot's own vessel: <c>Part.Undock(newVesselInfo)</c> looks the part up as
        /// <c>this.vessel[newVesselInfo.rootPartUId]</c>, calls
        /// <c>part.SetHierarchyRoot(part)</c> and builds the new <c>Vessel</c> on that
        /// part's GameObject (decompiled KSP 1.12.5), so the undocked half's
        /// <c>rootPart.flightID</c> IS the <c>rootPartUId</c> the proof recorded - while
        /// the same method assigns <c>vessel.id = Guid.NewGuid()</c>, which is why matching
        /// on a launch guid here would be actively wrong.</para>
        /// </summary>
        internal static bool TryResolveEndpoint(
            RouteEndpoint endpoint,
            out Vessel vessel,
            out string reason)
        {
            vessel = null;
            reason = string.Empty;

            // THE ORDER IS NOT WRITTEN HERE. Every "which step next" decision comes from the
            // pure NextEndpointStep, so this method cannot be silently reordered: the blocks
            // below are dispatched BY step rather than arranged in an order of their own.
            bool rootIdKnown = endpoint.RootPartUId != 0u;
            bool pidKnown = endpoint.VesselPersistentId != 0u;
            bool proximityEligible = endpoint.IsSurface
                && !string.IsNullOrEmpty(endpoint.BodyName)
                && FlightGlobals.Vessels != null;

            EndpointResolutionStep step = EndpointResolutionStep.None;
            while ((step = NextEndpointStep(step, rootIdKnown, pidKnown, proximityEligible))
                   != EndpointResolutionStep.None)
            {
                if (step == EndpointResolutionStep.RootPart)
                {
                    // THE PART SETS ARE BUILT LAZILY, and that is a cost decision, not a
                    // style one. This step runs EVERY FRAME the Logistics window draws a
                    // route, so collecting a part-id list per live vessel on the common path
                    // would be a per-frame allocation for a pass that almost never runs. Pass
                    // 1 (own root) uses the cheap snapshots; only a total miss - which is the
                    // docked-composite case, and the permanently-lost case that already pays
                    // for the proximity build below - rebuilds them with part sets.
                    List<RootIdVesselSnapshot> rootSnapshots =
                        BuildRootIdSnapshots(FlightGlobals.Vessels, includePartSets: false);
                    bool rootMatched = TryRootPartMatchPure(
                        endpoint.RootPartUId,
                        rootSnapshots,
                        GhostMapPresence.ghostMapVesselPids,
                        out Vessel byRoot,
                        out uint rootPickedPid,
                        out uint rootCollidingPid,
                        out string rootReason);
                    if (!rootMatched && rootReason == "no-root-match")
                    {
                        rootSnapshots = BuildRootIdSnapshots(
                            FlightGlobals.Vessels, includePartSets: true);
                        rootMatched = TryRootPartMatchPure(
                            endpoint.RootPartUId,
                            rootSnapshots,
                            GhostMapPresence.ghostMapVesselPids,
                            out byRoot,
                            out rootPickedPid,
                            out rootCollidingPid,
                            out rootReason);
                    }
                    if (rootMatched)
                    {
                        vessel = byRoot;
                        // AMBIGUITY IS ANNOUNCED ON THE SUCCESS PATH. Two live vessels sharing
                        // one root flightID is impossible in a healthy save, and taking the
                        // first SILENTLY would let a route debit an arbitrary one of them
                        // forever with no trace. Both ids are named, so the log identifies the
                        // pair rather than only complaining that a pair exists.
                        if (rootReason == "root-match-ambiguous"
                            || rootReason == "docked-composite-match-ambiguous")
                        {
                            ParsekLog.Warn("Logistics",
                                "Endpoint root-part match AMBIGUOUS: rootPartUId="
                                + endpoint.RootPartUId.ToString(CultureInfo.InvariantCulture)
                                + " pickedPid=" + rootPickedPid.ToString(CultureInfo.InvariantCulture)
                                + " collidingPid=" + rootCollidingPid.ToString(CultureInfo.InvariantCulture)
                                + " reason=" + rootReason
                                + " - two vessels report the same part flightID; taking the first");
                        }
                        // THE DOCKED-COMPOSITE ARM IS ANNOUNCED TOO, and at Info rather than
                        // Verbose: it is a standing condition an operator will want to see
                        // when a delivery lands somewhere unexpected - the recorded endpoint
                        // is currently INSIDE a merged craft whose pid is not the recorded
                        // one, so the delivery goes into the composite. Rate-limited on a key
                        // that carries the resolved pid, so a re-dock to a different visitor
                        // prints at once while a stable pair prints once.
                        if (rootReason == "docked-composite-match"
                            || rootReason == "docked-composite-match-ambiguous")
                        {
                            ParsekLog.InfoRateLimited("Logistics",
                                "endpoint-docked-composite-"
                                + endpoint.RootPartUId.ToString(CultureInfo.InvariantCulture)
                                + "-" + rootPickedPid.ToString(CultureInfo.InvariantCulture),
                                "Endpoint resolved through a DOCKED COMPOSITE: rootPartUId="
                                + endpoint.RootPartUId.ToString(CultureInfo.InvariantCulture)
                                + " recordedPid="
                                + endpoint.VesselPersistentId.ToString(CultureInfo.InvariantCulture)
                                + " compositePid=" + rootPickedPid.ToString(CultureInfo.InvariantCulture)
                                + " - the recorded endpoint is docked to another craft and the"
                                + " merged vessel carries the other half's persistentId; the"
                                + " route resolves it by part identity and is NOT rebound",
                                30.0);
                        }
                        ParsekLog.Verbose("Logistics",
                            "Endpoint resolved: step=root-part rootPartUId="
                            + endpoint.RootPartUId.ToString(CultureInfo.InvariantCulture)
                            + " pid=" + rootPickedPid.ToString(CultureInfo.InvariantCulture)
                            + " reason=" + (string.IsNullOrEmpty(rootReason) ? "-" : rootReason));
                        return true;
                    }
                    ParsekLog.Verbose("Logistics",
                        "Endpoint root-part step missed: rootPartUId="
                        + endpoint.RootPartUId.ToString(CultureInfo.InvariantCulture)
                        + " reason=" + rootReason
                        + " candidates=" + rootSnapshots.Count.ToString(CultureInfo.InvariantCulture));
                    continue;
                }

                if (step == EndpointResolutionStep.Pid)
                {
                    // A CORROBORATING FALLBACK BEHIND THE ROOT-PART STEP, GUID-GATED SINCE
                    // 2026-09-06. It runs only when the endpoint carries no root id or that
                    // root no longer resolves. A persistentId is craft-baked and is reused
                    // verbatim on every launch of the same .craft, so a bare match can name a
                    // DIFFERENT launch - the exact trap VesselLaunchIdentity exists for, and
                    // in this step's own reachable case (the depot's root no longer resolves)
                    // the vessel it would match is precisely a same-craft sibling standing
                    // where the depot was.
                    //
                    // THE GATE IS THE STANDARD ONE, NOT A STRICTER ONE: a match is refused
                    // ONLY when both guids are known and conclusively differ. An endpoint with
                    // no persisted guid (every route built before the key existed, every KSC
                    // origin, every evidence-free pid stamp) or a live vessel whose guid
                    // cannot be read degrades to the ungated match it always had, which is
                    // CLAUDE.md's unknown-guid rule. A refusal is not a dead end either: the
                    // walk falls through to the surface-proximity step, which resolves
                    // positionally and then REBINDS through RouteEndpointTransfer, so the
                    // route follows the depot actually standing there instead of paying a
                    // stranger by name.
                    Vessel byPid = ResolveByPid(endpoint.VesselPersistentId);
                    HashSet<uint> ghostPids = GhostMapPresence.ghostMapVesselPids;
                    if (byPid != null
                        && (ghostPids == null || !ghostPids.Contains(byPid.persistentId)))
                    {
                        string liveGuid = RouteEndpointTransfer.TryReadLaunchGuid(byPid);
                        if (VesselLaunchIdentity.GuidsConclusivelyDiffer(
                                endpoint.LaunchGuid, liveGuid))
                        {
                            ParsekLog.Verbose("Logistics",
                                "Endpoint pid step refused: pid="
                                + endpoint.VesselPersistentId.ToString(CultureInfo.InvariantCulture)
                                + " reason=different-launch"
                                + " recordedGuid=" + GuidToken(endpoint.LaunchGuid)
                                + " liveGuid=" + GuidToken(liveGuid)
                                + " - a craft-baked persistentId matched a DIFFERENT launch of"
                                + " the same craft; falling through to the next step");
                            continue;
                        }

                        vessel = byPid;
                        ParsekLog.Verbose("Logistics",
                            "Endpoint resolved: step=pid pid="
                            + endpoint.VesselPersistentId.ToString(CultureInfo.InvariantCulture)
                            + " guidGate=" + ClassifyPidGuidGate(endpoint.LaunchGuid, liveGuid));
                        return true;
                    }
                    continue;
                }

                // EndpointResolutionStep.SurfaceProximity - the last step, so it returns
                // either way.
                CelestialBody body = ResolveBodyByName(endpoint.BodyName);
                if (body == null)
                {
                    reason = "body-unresolved";
                    return false;
                }

                Vector3d endpointWorldPos = body.GetWorldSurfacePosition(
                    endpoint.Latitude, endpoint.Longitude, endpoint.Altitude);

                List<SurfaceVesselSnapshot> snapshots = BuildSurfaceSnapshots(
                    FlightGlobals.Vessels, endpoint.BodyName);

                // THE ROUTE CONTEXT, resolved only on this step. Two things need it and
                // nothing earlier does: the TRANSPORT EXCLUSION (a route must never pay or
                // load from its own carrier, measured as the runner-up at 16.42 m by RVR-18),
                // and the REBIND that turns a positional substitution into a persisted
                // transfer. The resolver is handed a copy of the endpoint struct, so the way
                // back to the persisted field is a value match over the committed routes.
                List<RouteEndpointTransfer.EndpointOwner> owners =
                    RouteEndpointTransfer.FindOwnersLive(endpoint);
                List<RouteEndpointTransfer.VesselIdentity> transports =
                    RouteEndpointTransfer.CollectTransportIdentitiesLive(owners);

                bool resolved = TrySurfaceFallbackPure(
                    endpointWorldPos,
                    endpoint.BodyName,
                    snapshots,
                    GhostMapPresence.ghostMapVesselPids,
                    transports,
                    RouteOrchestrator.SurfaceProximityRadiusMeters,
                    out vessel,
                    out uint proximityPid,
                    out double proximityDistance,
                    out int transportsSkipped,
                    out reason);
                if (transportsSkipped > 0)
                {
                    ParsekLog.Verbose("Logistics",
                        "Endpoint proximity transport excluded: skipped="
                        + transportsSkipped.ToString(CultureInfo.InvariantCulture)
                        + " reason=route-own-transport"
                        + " body=" + endpoint.BodyName);
                }
                ParsekLog.Verbose("Logistics",
                    "Endpoint proximity step: resolved=" + (resolved ? "1" : "0")
                    + " pid=" + proximityPid.ToString(CultureInfo.InvariantCulture)
                    + " reason=" + (string.IsNullOrEmpty(reason) ? "-" : reason)
                    + " body=" + endpoint.BodyName);
                if (resolved)
                {
                    RouteEndpointTransfer.ApplyTransfers(
                        owners,
                        endpoint,
                        proximityPid,
                        RouteEndpointTransfer.TryReadLaunchGuid(vessel),
                        vessel != null ? vessel.vesselName : null,
                        ResolveRootPartFlightId(vessel),
                        EndpointResolutionStep.SurfaceProximity,
                        proximityDistance,
                        true,
                        ReadUniversalTime());
                }
                return resolved;
            }

            reason = "pid-miss-no-surface-fallback";
            return false;
        }

        /// <summary>
        /// The PID step's guid-gate outcome as one stable token, pure so the four readings
        /// can be pinned headlessly against the same function production logs.
        /// <c>different-launch</c> is the ONLY refusing one; both unknown-side readings and
        /// the same-launch reading accept, which is the VesselLaunchIdentity contract
        /// (unknown means "no evidence", never "differs").
        /// </summary>
        internal static string ClassifyPidGuidGate(string recordedGuid, string liveGuid)
        {
            if (VesselLaunchIdentity.GuidsConclusivelyDiffer(recordedGuid, liveGuid))
                return "different-launch";
            if (string.IsNullOrEmpty(VesselLaunchIdentity.NormalizeGuid(recordedGuid)))
                return "unknown-recorded";
            if (string.IsNullOrEmpty(VesselLaunchIdentity.NormalizeGuid(liveGuid)))
                return "unknown-live";
            return "same-launch";
        }

        /// <summary>A guid for a log line: the normalized value, or <c>&lt;unknown&gt;</c>.</summary>
        private static string GuidToken(string guid)
        {
            string normalized = VesselLaunchIdentity.NormalizeGuid(guid);
            return string.IsNullOrEmpty(normalized) ? "<unknown>" : normalized;
        }

        /// <summary>
        /// Pure surface-fallback search. Takes the endpoint's world position +
        /// body name and a flat list of candidate vessel snapshots; picks the
        /// closest surface-classified candidate within
        /// <paramref name="radiusMeters"/> whose PID is not in
        /// <paramref name="excludePids"/>. <c>out vessel</c> may be null when
        /// the snapshot has no live <c>Vessel</c> reference (pure-test mode);
        /// production callers always populate it. <c>out pickedPid</c> exposes
        /// the chosen snapshot's <see cref="SurfaceVesselSnapshot.PersistentId"/>
        /// for diagnostic clarity and pure-test assertions; it is <c>0</c> on
        /// every miss path.
        /// </summary>
        internal static bool TrySurfaceFallbackPure(
            Vector3d endpointWorldPos,
            string bodyName,
            IReadOnlyList<SurfaceVesselSnapshot> liveSnapshots,
            HashSet<uint> excludePids,
            double radiusMeters,
            out Vessel vessel,
            out uint pickedPid,
            out string reason)
        {
            return TrySurfaceFallbackPure(
                endpointWorldPos, bodyName, liveSnapshots, excludePids,
                null, radiusMeters,
                out vessel, out pickedPid, out _, out _, out reason);
        }

        /// <summary>
        /// The full surface-fallback search: the overload above plus the ROUTE-OWN-TRANSPORT
        /// exclusion and the two diagnostics the transfer path needs.
        ///
        /// <para><paramref name="excludeTransports"/> carries the identities of the owning
        /// route's transports (guid-first, pid-fallback via
        /// <see cref="RouteEndpointTransfer.IsRouteTransport"/>). A route's own carrier parked
        /// back at the dock is routinely the NEAREST surface vessel to the recorded
        /// coordinates, so without this the "the depot was rebuilt here" branch would hand the
        /// route's cargo to the route itself. Excluded candidates are counted in
        /// <paramref name="transportCandidatesSkipped"/> so the caller can log them, and when
        /// an exclusion is the reason nothing was found the miss carries its own token
        /// (<c>no-candidate-after-transport-exclusion</c>) rather than the generic one.</para>
        ///
        /// <para><paramref name="pickedDistanceMeters"/> is the winning candidate's distance
        /// (0 on every miss path), reported so the transfer log names how far the substitute
        /// stood from the recorded dock point.</para>
        /// </summary>
        internal static bool TrySurfaceFallbackPure(
            Vector3d endpointWorldPos,
            string bodyName,
            IReadOnlyList<SurfaceVesselSnapshot> liveSnapshots,
            HashSet<uint> excludePids,
            IReadOnlyList<RouteEndpointTransfer.VesselIdentity> excludeTransports,
            double radiusMeters,
            out Vessel vessel,
            out uint pickedPid,
            out double pickedDistanceMeters,
            out int transportCandidatesSkipped,
            out string reason)
        {
            vessel = null;
            pickedPid = 0u;
            pickedDistanceMeters = 0.0;
            transportCandidatesSkipped = 0;
            reason = string.Empty;

            if (liveSnapshots == null || liveSnapshots.Count == 0)
            {
                reason = "no-live-vessels";
                return false;
            }

            int bestIdx = -1;
            double bestDist = double.MaxValue;
            for (int i = 0; i < liveSnapshots.Count; i++)
            {
                SurfaceVesselSnapshot snap = liveSnapshots[i];

                // Body match (case-sensitive — KSP body names are stable).
                if (snap.BodyName != bodyName)
                    continue;

                // Surface-class situations only.
                if (!IsSurfaceSituation(snap.Situation))
                    continue;

                // Exclude ghosts.
                if (excludePids != null && excludePids.Contains(snap.PersistentId))
                    continue;

                // Exclude the route's own transport (operator ruling 2026-09-04).
                if (RouteEndpointTransfer.IsRouteTransport(
                        snap.PersistentId, snap.LaunchGuid, excludeTransports, out _))
                {
                    transportCandidatesSkipped++;
                    continue;
                }

                double dist = (snap.WorldPosition - endpointWorldPos).magnitude;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0)
            {
                reason = transportCandidatesSkipped > 0
                    ? "no-candidate-after-transport-exclusion"
                    : "no-surface-candidate";
                return false;
            }
            if (bestDist > radiusMeters)
            {
                reason = "no-vessel-within-radius";
                return false;
            }

            vessel = liveSnapshots[bestIdx].Vessel;
            pickedPid = liveSnapshots[bestIdx].PersistentId;
            pickedDistanceMeters = bestDist;
            return true;
        }

        /// <summary>
        /// Pure root-part identity search, in TWO passes over the same launch-unique key.
        ///
        /// <para>PASS 1, the ordinary one: the vessel whose OWN ROOT part flightID equals
        /// <paramref name="rootPartUId"/>.</para>
        ///
        /// <para>PASS 2, THE DOCKED COMPOSITE (2026-09-06,
        /// ROUTE-ENDPOINT-TRANSFER-DOCKED-DOMINANT-PARTNER): the vessel whose PART SET
        /// CONTAINS that flightID. <c>Part.Couple</c> merges two craft into the DOMINANT
        /// half's <c>Vessel</c> - higher <c>VesselType</c>, then heavier, then larger guid
        /// (<c>Vessel.GetDominantVessel</c>) - and the absorbed half's parts are RE-PARENTED,
        /// never renumbered. So while a dominant visitor is docked to a base the base is no
        /// vessel's root, its own pid is gone from <c>FlightGlobals</c>, and pass 1 alone
        /// would lose it for exactly as long as the pair stays docked; the walk would fall to
        /// proximity, land on the composite, and REBIND the route to the visitor, which then
        /// undocks and flies away with it. A part flightID is launch-unique, so a vessel
        /// carrying it IS the physical craft that holds the recorded endpoint - cargo
        /// delivered into that composite reaches the base, which is the pre-#1627 outcome
        /// restored by identity rather than by positional accident.</para>
        ///
        /// <para>PASS ORDER IS THE CONTRACT and it is not symmetric: an own-root match beats a
        /// contains match everywhere, so the MIRROR case (the destination dominates the merge)
        /// resolves through pass 1 exactly as before and the two directions land on the same
        /// composite. Pass 2 runs only when pass 1 found nothing at all.</para>
        ///
        /// <para>A zero <paramref name="rootPartUId"/> never matches (it is the "unknown"
        /// sentinel, and a snapshot that could not supply a root id also carries 0 - so
        /// admitting it would pair every unknown with every other unknown). Two vessels
        /// carrying one root flightID is impossible in a healthy save; if it happens the
        /// FIRST is taken and the reason token records the collision so a log names it.
        /// <paramref name="reason"/> also names WHICH pass resolved
        /// (<c>docked-composite-match</c>), so an operator can tell a base standing on its own
        /// from one currently inside a merged craft.</para>
        /// </summary>
        internal static bool TryRootPartMatchPure(
            uint rootPartUId,
            IReadOnlyList<RootIdVesselSnapshot> snapshots,
            HashSet<uint> excludePids,
            out Vessel vessel,
            out uint pickedPid,
            out uint collidingPid,
            out string reason)
        {
            vessel = null;
            pickedPid = 0u;
            collidingPid = 0u;
            reason = string.Empty;

            if (rootPartUId == 0u)
            {
                reason = "root-id-unknown";
                return false;
            }
            if (snapshots == null || snapshots.Count == 0)
            {
                reason = "no-root-candidate";
                return false;
            }

            int found = 0;
            int firstIdx = -1;
            int secondIdx = -1;
            for (int i = 0; i < snapshots.Count; i++)
            {
                if (snapshots[i].RootPartFlightId != rootPartUId) continue;
                if (excludePids != null && excludePids.Contains(snapshots[i].PersistentId)) continue;
                found++;
                if (firstIdx < 0) firstIdx = i;
                else if (secondIdx < 0) secondIdx = i;
            }

            if (firstIdx >= 0)
            {
                vessel = snapshots[firstIdx].Vessel;
                pickedPid = snapshots[firstIdx].PersistentId;
                if (found > 1)
                {
                    collidingPid = snapshots[secondIdx].PersistentId;
                    reason = "root-match-ambiguous";
                }
                return true;
            }

            // PASS 2: the recorded root part is aboard some vessel without being its root.
            int compositeFound = 0;
            int compositeFirst = -1;
            int compositeSecond = -1;
            for (int i = 0; i < snapshots.Count; i++)
            {
                IReadOnlyList<uint> parts = snapshots[i].PartFlightIds;
                if (parts == null || parts.Count == 0) continue;
                if (excludePids != null && excludePids.Contains(snapshots[i].PersistentId)) continue;

                bool carries = false;
                for (int p = 0; p < parts.Count; p++)
                {
                    if (parts[p] != rootPartUId) continue;
                    carries = true;
                    break;
                }
                if (!carries) continue;

                compositeFound++;
                if (compositeFirst < 0) compositeFirst = i;
                else if (compositeSecond < 0) compositeSecond = i;
            }

            if (compositeFirst < 0)
            {
                reason = "no-root-match";
                return false;
            }

            vessel = snapshots[compositeFirst].Vessel;
            pickedPid = snapshots[compositeFirst].PersistentId;
            reason = compositeFound > 1 ? "docked-composite-match-ambiguous" : "docked-composite-match";
            if (compositeFound > 1)
                collidingPid = snapshots[compositeSecond].PersistentId;
            return true;
        }

        /// <summary>
        /// Convert the live <see cref="FlightGlobals.Vessels"/> list to root-part
        /// snapshots. Unloaded vessels have no instantiated <c>rootPart</c>, so the root
        /// flightID comes from the <c>ProtoVessel</c>'s own root snapshot - without that
        /// half an unloaded depot (the normal state of a depot at dispatch time) would
        /// never match. NOT filtered by body or situation: identity does not depend on
        /// where the vessel is.
        ///
        /// <para>The PART SET is collected only when <paramref name="includePartSets"/> - the
        /// docked-composite pass needs it and nothing else does, and this method runs every
        /// frame the Logistics window draws a route, so a per-vessel list allocation on the
        /// common path would be a per-frame cost for a pass that almost never runs. Read
        /// live-parts-first / proto-second for the same reason the root is: at dispatch time
        /// the depot is normally unloaded.</para>
        /// </summary>
        private static List<RootIdVesselSnapshot> BuildRootIdSnapshots(
            IReadOnlyList<Vessel> liveVessels,
            bool includePartSets)
        {
            var snapshots = new List<RootIdVesselSnapshot>();
            if (liveVessels == null) return snapshots;

            for (int i = 0; i < liveVessels.Count; i++)
            {
                Vessel v = liveVessels[i];
                if (v == null) continue;
                snapshots.Add(new RootIdVesselSnapshot
                {
                    PersistentId = v.persistentId,
                    RootPartFlightId = ResolveRootPartFlightId(v),
                    PartFlightIds = includePartSets ? ResolvePartFlightIds(v) : null,
                    Vessel = v,
                });
            }

            return snapshots;
        }

        /// <summary>
        /// Every part flightID on the vessel: live parts first, the <c>ProtoVessel</c>'s part
        /// snapshots second, null when neither can supply any. Defensive for the same reason
        /// <see cref="ResolveRootPartFlightId"/> is - a stock-side null during scene teardown
        /// must surface as an endpoint miss, never a crash.
        /// </summary>
        private static List<uint> ResolvePartFlightIds(Vessel v)
        {
            if (v == null) return null;
            try
            {
                if (v.parts != null && v.parts.Count > 0)
                {
                    var ids = new List<uint>(v.parts.Count);
                    for (int i = 0; i < v.parts.Count; i++)
                    {
                        Part p = v.parts[i];
                        if (p != null) ids.Add(p.flightID);
                    }
                    return ids;
                }

                ProtoVessel pv = v.protoVessel;
                if (pv?.protoPartSnapshots == null || pv.protoPartSnapshots.Count == 0) return null;
                var protoIds = new List<uint>(pv.protoPartSnapshots.Count);
                for (int i = 0; i < pv.protoPartSnapshots.Count; i++)
                {
                    ProtoPartSnapshot snap = pv.protoPartSnapshots[i];
                    if (snap != null) protoIds.Add(snap.flightID);
                }
                return protoIds;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The vessel's ROOT part flightID: live part first, proto snapshot second, 0 when
        /// neither can supply one. Internal because the transfer path stamps it onto a rebound
        /// endpoint (a flightID is launch-unique where a persistentId is craft-baked, so the
        /// next cycle resolves at the root-part step instead of walking to proximity again).
        /// </summary>
        internal static uint ResolveRootPartFlightId(Vessel v)
        {
            if (v == null) return 0u;
            try
            {
                if (v.rootPart != null)
                    return v.rootPart.flightID;
                ProtoVessel pv = v.protoVessel;
                if (pv?.protoPartSnapshots == null) return 0u;
                int rootIndex = pv.rootIndex;
                if (rootIndex < 0 || rootIndex >= pv.protoPartSnapshots.Count) return 0u;
                return pv.protoPartSnapshots[rootIndex]?.flightID ?? 0u;
            }
            catch
            {
                // Defensive, same rationale as ResolveByPid: a stock-side null during
                // scene teardown surfaces as an endpoint miss, not a crash.
                return 0u;
            }
        }

        /// <summary>
        /// Convert the live <see cref="FlightGlobals.Vessels"/> list to a flat
        /// snapshot list filtered to the matching body. Encapsulates every
        /// <c>Vessel.*</c> field read so the pure path above stays KSP-free.
        /// </summary>
        private static List<SurfaceVesselSnapshot> BuildSurfaceSnapshots(
            IReadOnlyList<Vessel> liveVessels,
            string bodyName)
        {
            var snapshots = new List<SurfaceVesselSnapshot>();
            if (liveVessels == null) return snapshots;

            for (int i = 0; i < liveVessels.Count; i++)
            {
                Vessel v = liveVessels[i];
                if (v == null) continue;
                if (v.mainBody == null) continue;
                if (v.mainBody.bodyName != bodyName) continue;
                if (!IsSurfaceSituation(v.situation)) continue;

                snapshots.Add(new SurfaceVesselSnapshot
                {
                    PersistentId = v.persistentId,
                    LaunchGuid = RouteEndpointTransfer.TryReadLaunchGuid(v),
                    BodyName = v.mainBody.bodyName,
                    Situation = v.situation,
                    WorldPosition = v.GetWorldPos3D(),
                    Vessel = v,
                });
            }

            return snapshots;
        }

        private static bool IsSurfaceSituation(Vessel.Situations situation)
        {
            return situation == Vessel.Situations.LANDED
                || situation == Vessel.Situations.SPLASHED
                || situation == Vessel.Situations.PRELAUNCH;
        }

        private static Vessel ResolveByPid(uint pid)
        {
            try
            {
                // FlightGlobals.FindVessel is an O(1) wrapper around
                // FlightGlobals.PersistentVesselIds (matches the canonical
                // GhostMapPresence pattern).
                if (FlightGlobals.fetch != null
                    && FlightGlobals.FindVessel(pid, out Vessel found))
                {
                    return found;
                }
            }
            catch
            {
                // Defensive: a stock-side null-deref during scene teardown should
                // surface as a benign endpoint-miss rather than a hard crash.
            }
            return null;
        }

        /// <summary>
        /// Current UT for the transfer log line. Defensive for the same reason the probes
        /// above are: a resolution can run while <c>Planetarium</c> is mid-teardown, and a
        /// missing timestamp must not cost the rebind.
        /// </summary>
        private static double ReadUniversalTime()
        {
            try
            {
                return Planetarium.GetUniversalTime();
            }
            catch
            {
                return 0.0;
            }
        }

        private static CelestialBody ResolveBodyByName(string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName)) return null;
            if (FlightGlobals.Bodies == null) return null;
            for (int i = 0; i < FlightGlobals.Bodies.Count; i++)
            {
                CelestialBody body = FlightGlobals.Bodies[i];
                if (body != null && body.bodyName == bodyName)
                    return body;
            }
            return null;
        }
    }
}
