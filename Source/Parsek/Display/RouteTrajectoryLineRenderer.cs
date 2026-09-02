using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;
using Vectrosity;
using LegPolyline = Parsek.Display.GhostTrajectoryPolylineRenderer.LegPolyline;

namespace Parsek.Display
{
    /// <summary>
    /// Draws the static "overview" path of each committed same-body supply route on the flight
    /// map and the Tracking Station: the route's backing recorded legs (launch -&gt; dock at the
    /// destination) rendered as a persistent polyline, so the player can see WHERE a route runs,
    /// not just read its Logistics panel (design doc §17 "Map view integration" / §19.4 M6).
    ///
    /// <para>
    /// Rides the existing <see cref="GhostTrajectoryPolylineRenderer"/> machinery instead of
    /// inventing a new render style: the pure leg builder
    /// (<see cref="GhostTrajectoryPolylineRenderer.BuildLegsForRecording"/>) and the shared draw
    /// helper (<see cref="GhostTrajectoryPolylineRenderer.TryDrawLeg"/>, which paints the same
    /// stock-orbit grey / width / material as the ghost trajectory line). It keeps its OWN
    /// per-route cache and VectorLines so it never shares mutable state with the per-cycle ghost
    /// polyline, and it publishes NO ownership signal (<c>drewNonOrbitalLegRecordings</c>) — a
    /// route overview line is an independent overlay, not a ghost-phase owner, so it must never
    /// suppress a ghost's proto orbit line/icon.
    /// </para>
    ///
    /// <para>
    /// Draw ordering avoids double-drawing over a route ghost's OWN live trajectory: the shared
    /// <see cref="GhostTrajectoryPolylineRenderer.Driver"/> runs its ghost-leg draw first (in the
    /// same map-camera onPreCull frame) and publishes the recordings whose non-orbital leg it
    /// actually drew; this renderer then skips any backing recording the ghost is drawing this
    /// frame (<see cref="GhostTrajectoryPolylineRenderer.IsRenderingNonOrbitalLeg"/>), so the leg
    /// the animated ghost is on is drawn once (by the ghost) and the rest of the route path is
    /// drawn statically here.
    /// </para>
    ///
    /// <para>
    /// SCOPE: same-body routes (origin body == every stop body) draw their full
    /// recorded non-orbital legs, byte-identical to the shipped M6 v1. INTER-BODY routes
    /// (origin body != some stop body, milestone M5) draw the recorded non-orbital
    /// legs at the route's ENDPOINT bodies only — launch/ascent legs at the origin body,
    /// approach/descent legs at the destination body, still clipped to
    /// <see cref="Route.RecordedDockUT"/>. The transfer between them is deliberately NOT drawn
    /// here: the heliocentric coast is orbital (never emitted as a polyline leg), and non-orbital
    /// legs recorded in the TRANSFER frame (e.g. mid-course correction burns body-fixed to the
    /// Sun) are dropped at build time (<see cref="FilterLegsToEndpointBodies"/>) because the M5
    /// re-aim pipeline replaces the recorded transfer per launch window — a static draw of the
    /// recorded transfer-frame geometry would show a path that never flies again. The re-aimed
    /// transfer render belongs to the route's backing-mission ghost (conics / forward arcs), which
    /// inter-body routes already get. Behind the <c>showRouteLines</c> setting (default on).
    /// </para>
    /// </summary>
    internal static class RouteTrajectoryLineRenderer
    {
        private const string Tag = "RouteLine";

        /// <summary>
        /// Default used when no <see cref="ParsekSettings"/> instance is available (tests,
        /// pre-game-load). Route lines default ON: they are the M6 legibility feature and use the
        /// stock orbit-line style, matching the always-on spirit of the ghost polyline while still
        /// offering a hide toggle.
        /// </summary>
        internal const bool DefaultShowRouteLines = true;

        // Per-route cache: routeId -> the built member-leg groups + the signature that gates a
        // rebuild. Keyed by Route.Id (string). Separate from the ghost polylineCache so a route's
        // static overview never shares a VectorLine with the per-cycle ghost's head-gated draw.
        private static readonly Dictionary<string, RouteLineSet> routeCache =
            new Dictionary<string, RouteLineSet>(StringComparer.Ordinal);

        // Scratch reused each DrawAll frame for the "still committed" GC reconcile (no per-frame
        // allocation on the hot path).
        private static readonly HashSet<string> committedIdScratch =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<string> staleKeyScratch = new List<string>();

        internal static int BuildInvocationCountForTesting;

        // ------------------------------------------------------------------
        // Data model
        // ------------------------------------------------------------------

        /// <summary>One backing recording's clipped legs, tagged with its recording id so the draw
        /// pass can skip a member the ghost polyline is currently drawing.</summary>
        internal struct RouteMemberLegs
        {
            public string memberRecordingId;
            public Recording rec;
            public LegPolyline[] legs;
        }

        private struct RouteLineSet
        {
            public RouteMemberLegs[] groups;
            public long signature;
        }

        internal enum RouteLineSkipReason
        {
            None = 0,
            NullRoute = 1,
            Disabled = 2,
            MalformedMixedBodies = 3,
            NoBackingRecordings = 4,
        }

        /// <summary>Drawable-scope classification of a committed route.</summary>
        internal enum RouteLineScope
        {
            /// <summary>Endpoints agree on one body (and the members agree with them): draw all
            /// recorded non-orbital legs.</summary>
            SameBody = 0,

            /// <summary>Origin body != some stop body (M5): draw the endpoint-body legs only.</summary>
            InterBody = 1,

            /// <summary>Declared same-body endpoints (or no readable endpoints) but members on
            /// mixed bodies: malformed, declined rather than drawing a cross-body chord.</summary>
            MalformedMixedBodies = 2,
        }

        /// <summary>
        /// Which authority settled a <see cref="RouteLineScope"/>. Logged with the classification so
        /// a reader can tell a route classified from its own declared endpoints from one that fell
        /// back to the member-body consistency read.
        /// </summary>
        internal enum RouteScopeBasis
        {
            /// <summary>The route's own endpoints answered it: <see cref="Route.Origin"/>'s body
            /// against the bodies of <see cref="Route.Stops"/>' endpoints.</summary>
            Endpoints = 0,

            /// <summary>No readable endpoint bodies (a default-constructed origin, or no stop
            /// carrying a body), so the shipped v1 member-body consistency read decided.</summary>
            MemberBodies = 1,
        }

        // ------------------------------------------------------------------
        // Pure decision helpers (Unity-free, unit-tested)
        // ------------------------------------------------------------------

        /// <summary>Whether route lines are enabled. Null settings (tests / pre-load) fall back to
        /// <see cref="DefaultShowRouteLines"/>.</summary>
        internal static bool RouteLinesEnabled(ParsekSettings settings)
            => settings == null ? DefaultShowRouteLines : settings.showRouteLines;

        /// <summary>
        /// THE inter-body predicate, and the ONLY expression of it: a route is inter-body when its
        /// ORIGIN endpoint's body is known and at least one STOP endpoint's body is known and
        /// DIFFERENT. Endpoint-only by construction (no member walk), so the build-time
        /// endpoint-body filter and <see cref="ClassifyRouteScope"/> gate on the same function
        /// rather than restating the test — they cannot disagree about one route.
        ///
        /// <para>WHY THE ENDPOINTS AND NOT <see cref="Route.DispatchWindowPeriod"/>. The period was
        /// the shipped scope flag, and NOTHING in production ever set it non-zero
        /// (<c>RouteBuilder</c> hard-coded 0.0), so <see cref="RouteLineScope.InterBody"/> was
        /// unreachable from creation and every real Kerbin -&gt; Duna route classified
        /// <see cref="RouteLineScope.MalformedMixedBodies"/> and drew no line at all
        /// (ROUTE-INTERBODY-SCOPE-NEVER-REACHABLE). Scope is now derived from what actually defines
        /// it. A supply route rides the looped-mission infrastructure: the journey and its cadence
        /// are the LOOP's business (the per-tick <c>RouteWindowBasis</c> in
        /// <c>RouteLoopClock.DeriveWindowBasis</c>, which never read the period either and is
        /// deliberately never persisted), and the route only adds the resource transfer. What makes
        /// a route inter-body for the RENDER is therefore the pair of places it connects, which the
        /// route carries first-hand and every production path fills in.</para>
        /// </summary>
        internal static bool IsInterBodyByEndpoints(
            string originBodyName, IReadOnlyList<string> stopBodyNames)
        {
            if (string.IsNullOrEmpty(originBodyName) || stopBodyNames == null) return false;
            for (int i = 0; i < stopBodyNames.Count; i++)
            {
                string stop = stopBodyNames[i];
                if (string.IsNullOrEmpty(stop)) continue;
                if (!string.Equals(originBodyName, stop, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Live overload of <see cref="IsInterBodyByEndpoints(string, IReadOnlyList{string})"/>
        /// reading the route's own <see cref="Route.Origin"/> / <see cref="Route.Stops"/>.</summary>
        internal static bool IsInterBodyByEndpoints(Route route)
            => route != null
               && IsInterBodyByEndpoints(route.Origin.BodyName, CollectStopBodies(route));

        /// <summary>
        /// Classifies a route's render scope from its ENDPOINT bodies, with the member bodies as a
        /// cross-check. The rule, top-down:
        ///
        /// <list type="number">
        /// <item>ORIGIN body known and some STOP body known and different -&gt;
        /// <see cref="RouteLineScope.InterBody"/> (<see cref="RouteScopeBasis.Endpoints"/>). The
        /// members are EXPECTED to span bodies here — the transfer-frame member is the whole point —
        /// so no consistency check applies, and a THIRD body among the members is the ratified
        /// transfer gap, not a malformation: <see cref="FilterLegsToEndpointBodies"/> drops it.</item>
        /// <item>ORIGIN body known and every known STOP body EQUAL to it -&gt; a declared same-body
        /// route (<see cref="RouteScopeBasis.Endpoints"/>). Its members must agree with that body;
        /// a member on another body means the recorded path leaves the pair the route declares, and
        /// drawing it whole would paint a cross-body chord — that is
        /// <see cref="RouteLineScope.MalformedMixedBodies"/> (design doc §17 "Map view
        /// integration"). Otherwise <see cref="RouteLineScope.SameBody"/>.</item>
        /// <item>No readable endpoint bodies (a default-constructed origin, or no stop carrying a
        /// body) -&gt; the shipped v1 member-body consistency read
        /// (<see cref="RouteScopeBasis.MemberBodies"/>): all known member bodies agree (or none are
        /// known) -&gt; <see cref="RouteLineScope.SameBody"/>, they disagree -&gt;
        /// <see cref="RouteLineScope.MalformedMixedBodies"/>. With no endpoints there is no
        /// authority saying WHICH two bodies are the endpoints, so the safe reading is the shipped
        /// decline rather than a guessed inter-body draw.</item>
        /// </list>
        /// </summary>
        internal static RouteLineScope ClassifyRouteScope(
            string originBodyName, IReadOnlyList<string> stopBodyNames,
            IReadOnlyList<string> memberBodies, out RouteScopeBasis basis)
        {
            if (IsInterBodyByEndpoints(originBodyName, stopBodyNames))
            {
                basis = RouteScopeBasis.Endpoints;
                return RouteLineScope.InterBody;
            }

            bool endpointsReadable =
                !string.IsNullOrEmpty(originBodyName) && HasKnownBody(stopBodyNames);
            basis = endpointsReadable ? RouteScopeBasis.Endpoints : RouteScopeBasis.MemberBodies;

            // Declared same-body: the endpoint body is the reference every member must match.
            // No endpoints: the first known member body becomes the reference (shipped v1).
            string reference = endpointsReadable ? originBodyName : null;
            if (memberBodies == null || memberBodies.Count == 0) return RouteLineScope.SameBody;
            for (int i = 0; i < memberBodies.Count; i++)
            {
                string b = memberBodies[i];
                if (string.IsNullOrEmpty(b)) continue;
                if (reference == null) reference = b;
                else if (!string.Equals(reference, b, StringComparison.Ordinal))
                    return RouteLineScope.MalformedMixedBodies;
            }
            return RouteLineScope.SameBody;
        }

        /// <summary>Basis-free overload of
        /// <see cref="ClassifyRouteScope(string, IReadOnlyList{string}, IReadOnlyList{string}, out RouteScopeBasis)"/>.</summary>
        internal static RouteLineScope ClassifyRouteScope(
            string originBodyName, IReadOnlyList<string> stopBodyNames,
            IReadOnlyList<string> memberBodies)
            => ClassifyRouteScope(originBodyName, stopBodyNames, memberBodies, out _);

        /// <summary>Live overload reading the route's own endpoints.</summary>
        internal static RouteLineScope ClassifyRouteScope(
            Route route, IReadOnlyList<string> memberBodies, out RouteScopeBasis basis)
        {
            if (route == null)
            {
                basis = RouteScopeBasis.MemberBodies;
                return RouteLineScope.SameBody;
            }
            return ClassifyRouteScope(
                route.Origin.BodyName, CollectStopBodies(route), memberBodies, out basis);
        }

        /// <summary>Live basis-free overload.</summary>
        internal static RouteLineScope ClassifyRouteScope(
            Route route, IReadOnlyList<string> memberBodies)
            => ClassifyRouteScope(route, memberBodies, out _);

        /// <summary>Ordered stop-endpoint body names; null when the route declares no stops.</summary>
        internal static List<string> CollectStopBodies(Route route)
        {
            if (route?.Stops == null || route.Stops.Count == 0) return null;
            var bodies = new List<string>(route.Stops.Count);
            for (int i = 0; i < route.Stops.Count; i++)
            {
                RouteStop stop = route.Stops[i];
                if (stop == null) continue;
                bodies.Add(stop.Endpoint.BodyName);
            }
            return bodies;
        }

        private static bool HasKnownBody(IReadOnlyList<string> bodies)
        {
            if (bodies == null) return false;
            for (int i = 0; i < bodies.Count; i++)
                if (!string.IsNullOrEmpty(bodies[i])) return true;
            return false;
        }

        /// <summary>The route's destination label for logs: the LAST known stop body, or
        /// <c>&lt;none&gt;</c>.</summary>
        internal static string DestinationBodyLabel(IReadOnlyList<string> stopBodyNames)
        {
            if (stopBodyNames == null) return "<none>";
            for (int i = stopBodyNames.Count - 1; i >= 0; i--)
                if (!string.IsNullOrEmpty(stopBodyNames[i])) return stopBodyNames[i];
            return "<none>";
        }

        /// <summary>True when a route classifies <see cref="RouteLineScope.SameBody"/>.</summary>
        internal static bool IsSameBodyRoute(
            string originBodyName, IReadOnlyList<string> stopBodyNames,
            IReadOnlyList<string> memberBodies)
            => ClassifyRouteScope(originBodyName, stopBodyNames, memberBodies)
               == RouteLineScope.SameBody;

        /// <summary>Pure skip classification for a candidate route line.</summary>
        internal static RouteLineSkipReason ClassifyRouteLineSkip(
            Route route, bool enabled, RouteLineScope scope, int drawableMemberCount)
        {
            if (route == null) return RouteLineSkipReason.NullRoute;
            if (!enabled) return RouteLineSkipReason.Disabled;
            if (scope == RouteLineScope.MalformedMixedBodies)
                return RouteLineSkipReason.MalformedMixedBodies;
            if (drawableMemberCount <= 0) return RouteLineSkipReason.NoBackingRecordings;
            return RouteLineSkipReason.None;
        }

        /// <summary>
        /// Whether a leg falls within the route's rendered [launch .. dock] extent. The route
        /// render stops at the docking moment (the docked combined-vessel stretch is excluded), so
        /// a leg that begins at/after the dock UT is dropped. <paramref name="dockClipUT"/> &lt;= 0
        /// means unset (<see cref="Route.RecordedDockUT"/> default -1) — no clip.
        /// </summary>
        internal static bool LegWithinDockClip(double legStartUT, double legEndUT, double dockClipUT)
        {
            if (dockClipUT <= 0.0) return true;
            return legStartUT < dockClipUT;
        }

        // ------------------------------------------------------------------
        // Pure builder
        // ------------------------------------------------------------------

        /// <summary>
        /// Resolves a route's backing recordings and builds their non-orbital polyline legs,
        /// clipped to the route's dock UT. Reuses the ghost leg builder verbatim (same body-fixed
        /// lat/lon/alt extraction, same downsample cap, same RELATIVE-frame handling) so route
        /// lines render identically to ghost trajectory lines. Members that do not resolve, or that
        /// contribute no drawable leg, are dropped. For an INTER-BODY route
        /// (<see cref="IsInterBodyByEndpoints(Route)"/>) the endpoint-body filter then drops
        /// every leg not on the route's origin or destination body
        /// (<see cref="FilterLegsToEndpointBodies"/>; <paramref name="transferLegsDropped"/>
        /// reports the count) — same-body routes are never filtered. READ-ONLY over the route +
        /// recording data.
        /// </summary>
        internal static List<RouteMemberLegs> BuildRouteMemberLegs(
            Route route, Func<string, Recording> resolve,
            out int resolvableMembers, out int totalLegs, out int transferLegsDropped)
        {
            resolvableMembers = 0;
            totalLegs = 0;
            transferLegsDropped = 0;
            var groups = new List<RouteMemberLegs>();
            if (route == null || route.RecordingIds == null || resolve == null)
                return groups;

            double dockClipUT = route.RecordedDockUT;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int r = 0; r < route.RecordingIds.Count; r++)
            {
                string recId = route.RecordingIds[r];
                if (string.IsNullOrEmpty(recId) || !seen.Add(recId)) continue;
                Recording rec = resolve(recId);
                if (rec == null) continue;
                resolvableMembers++;

                var built = GhostTrajectoryPolylineRenderer.BuildLegsForRecording(rec);
                if (built == null || built.Count == 0) continue;

                List<LegPolyline> kept = null;
                for (int i = 0; i < built.Count; i++)
                {
                    var leg = built[i];
                    if (leg.PointCount < 2) continue;
                    if (!LegWithinDockClip(leg.startUT, leg.endUT, dockClipUT)) continue;
                    (kept ?? (kept = new List<LegPolyline>())).Add(leg);
                }
                if (kept == null || kept.Count == 0) continue;

                totalLegs += kept.Count;
                groups.Add(new RouteMemberLegs
                {
                    memberRecordingId = recId,
                    rec = rec,
                    legs = kept.ToArray(),
                });
            }

            // Inter-body scope: keep only the endpoint-body legs (origin + destination); the
            // recorded transfer-frame legs are stale geometry under the M5 re-aim pipeline. Gated
            // on the SAME endpoint predicate ClassifyRouteScope's InterBody branch uses, so "the
            // filter ran" and "the scope classified InterBody" are one decision, not two.
            if (IsInterBodyByEndpoints(route))
            {
                transferLegsDropped = FilterLegsToEndpointBodies(groups);
                totalLegs -= transferLegsDropped;
            }
            return groups;
        }

        /// <summary>
        /// Resolves an inter-body route's endpoint bodies from its built legs across ALL members:
        /// the body of the earliest leg (by start UT) is the origin (launch/ascent always records
        /// non-orbital samples), the body of the latest leg (by end UT, inside the dock clip the
        /// caller already applied) is the destination (the dock approach). False when no legs.
        /// </summary>
        internal static bool ResolveEndpointBodies(
            List<RouteMemberLegs> groups, out string originBody, out string destinationBody)
        {
            originBody = null;
            destinationBody = null;
            if (groups == null) return false;
            double earliest = double.MaxValue, latest = double.MinValue;
            for (int g = 0; g < groups.Count; g++)
            {
                LegPolyline[] legs = groups[g].legs;
                if (legs == null) continue;
                for (int i = 0; i < legs.Length; i++)
                {
                    if (string.IsNullOrEmpty(legs[i].bodyName)) continue;
                    if (legs[i].startUT < earliest)
                    {
                        earliest = legs[i].startUT;
                        originBody = legs[i].bodyName;
                    }
                    if (legs[i].endUT > latest)
                    {
                        latest = legs[i].endUT;
                        destinationBody = legs[i].bodyName;
                    }
                }
            }
            return originBody != null && destinationBody != null;
        }

        /// <summary>
        /// Inter-body endpoint filter: keeps only legs on the route's origin or destination body
        /// (resolved via <see cref="ResolveEndpointBodies"/> across all members) and drops
        /// everything between — non-orbital burn legs recorded in the transfer frame (mid-course
        /// corrections body-fixed to the transfer parent) and flyby legs. Those replay on re-aimed
        /// per-window geometry owned by the backing mission's ghost render; a static draw of the
        /// recorded ones would show a transfer that never flies again. Known heuristic limit
        /// (documented, benign): a route with NO non-orbital destination legs — none recorded, or
        /// all removed by an early dock clip — resolves its latest leg (possibly a transfer-frame
        /// burn) as the destination and keeps it; in practice a docking approach always records
        /// non-orbital ExoPropulsive samples at the destination before the dock.
        /// Groups left empty are removed. Returns the dropped leg count. Build-time only (legs
        /// carry no VectorLines yet).
        ///
        /// <para>Deliberate split of duties: the route's DECLARED endpoints gate whether this runs
        /// (<see cref="IsInterBodyByEndpoints(Route)"/>), the LEGS resolve which bodies to keep. The
        /// legs are what actually gets drawn, and a member whose stop endpoint body is unset still
        /// contributes drawable geometry; when the two disagree (a round trip whose legs resolve
        /// origin == destination) the stand-down above keeps everything, which is the documented
        /// lesser error.</para>
        /// </summary>
        internal static int FilterLegsToEndpointBodies(List<RouteMemberLegs> groups)
        {
            if (!ResolveEndpointBodies(groups, out string originBody, out string destinationBody))
                return 0;

            // Round-trip stand-down: a recording that returns to its origin body (and carries no
            // dock clip to cut the return) resolves origin == destination, and filtering on that
            // pair would drop the ENTIRE far-body arc — the geometry the route exists to show.
            // Keeping everything (including any transfer-frame burn legs) is the lesser error.
            // The build log's transferDropped=0 records the stand-down.
            if (string.Equals(originBody, destinationBody, StringComparison.Ordinal))
                return 0;

            int dropped = 0;
            for (int g = groups.Count - 1; g >= 0; g--)
            {
                LegPolyline[] legs = groups[g].legs;
                if (legs == null) continue;
                List<LegPolyline> kept = null;
                for (int i = 0; i < legs.Length; i++)
                    if (IsEndpointBodyLeg(legs[i].bodyName, originBody, destinationBody))
                        (kept ?? (kept = new List<LegPolyline>(legs.Length))).Add(legs[i]);
                int keepCount = kept != null ? kept.Count : 0;
                if (keepCount == legs.Length) continue;
                dropped += legs.Length - keepCount;
                if (keepCount == 0)
                {
                    groups.RemoveAt(g);
                    continue;
                }
                var group = groups[g];
                group.legs = kept.ToArray();
                groups[g] = group;
            }
            return dropped;
        }

        private static bool IsEndpointBodyLeg(string legBody, string originBody, string destinationBody)
            => string.Equals(legBody, originBody, StringComparison.Ordinal)
               || string.Equals(legBody, destinationBody, StringComparison.Ordinal);

        /// <summary>
        /// Content signature that gates a route-line rebuild. Folds the ordered recording ids, each
        /// resolvable member's polyline content hash (so an optimizer re-cut or supersede rebuild
        /// invalidates the cached line), the dock-clip UT, and the ENDPOINT bodies (origin + every
        /// stop) — the scope authority, so a scope flip invalidates the cached line the way the
        /// period fold used to. An INTER-BODY route additionally folds its window schedule (the now
        /// informational period, window epoch, cadence multiplier) so a schedule change rebuilds
        /// the line; a SAME-BODY route folds no schedule field, so its computation is unchanged
        /// from the shipped v1 apart from the endpoint-body fold every route now carries. Pure and
        /// stable across a save round-trip.
        /// </summary>
        internal static long ComputeRouteSignature(Route route, Func<string, Recording> resolve)
        {
            if (route == null) return 0L;
            unchecked
            {
                long h = 1469598103934665603L; // FNV-1a offset basis
                if (route.RecordingIds != null)
                {
                    for (int r = 0; r < route.RecordingIds.Count; r++)
                    {
                        h = MixString(h, route.RecordingIds[r]);
                        Recording rec = resolve?.Invoke(route.RecordingIds[r]);
                        if (rec != null)
                            h ^= GhostTrajectoryPolylineRenderer.ComputeContentHash(rec);
                    }
                }
                h ^= BitConverter.DoubleToInt64Bits(route.RecordedDockUT);
                h = MixString(h, route.Origin.BodyName);
                List<string> stopBodies = CollectStopBodies(route);
                if (stopBodies != null)
                    for (int i = 0; i < stopBodies.Count; i++)
                        h = MixString(h, stopBodies[i]);
                if (IsInterBodyByEndpoints(route.Origin.BodyName, stopBodies))
                {
                    h = (h ^ BitConverter.DoubleToInt64Bits(route.DispatchWindowPeriod)) * 1099511628211L;
                    h = (h ^ BitConverter.DoubleToInt64Bits(route.DispatchWindowEpochUT)) * 1099511628211L;
                    h = (h ^ route.CadenceMultiplier) * 1099511628211L;
                }
                return h;
            }
        }

        private static long MixString(long h, string s)
        {
            unchecked
            {
                if (s == null) return h * 1099511628211L;
                for (int i = 0; i < s.Length; i++)
                    h = (h ^ s[i]) * 1099511628211L;
                return h;
            }
        }

        // ------------------------------------------------------------------
        // Draw orchestration (invoked from the polyline Driver's route onPreCull slot)
        // ------------------------------------------------------------------

        /// <summary>
        /// Draws every committed same-body route's overview line this frame. Called from the shared
        /// polyline Driver's map-camera onPreCull slot AFTER the ghost-leg draw, so
        /// <see cref="GhostTrajectoryPolylineRenderer.IsRenderingNonOrbitalLeg"/> reflects the
        /// recordings the ghost drew this frame. Reads the <c>showRouteLines</c> setting; when off,
        /// no route draws and the end-of-frame sweep hides any previously drawn line.
        /// </summary>
        internal static void DrawAll(int frame, int targetLayer, Func<string, CelestialBody> resolveBody)
        {
            if (resolveBody == null) return;

            bool enabled = RouteLinesEnabled(ParsekSettings.Current);

            // GC cache entries for routes no longer committed (runs whether enabled or not so a
            // removed route's VectorLines are freed even while the toggle is off).
            ReconcileCommittedRoutes();

            int routesDrawn = 0, legsDrawn = 0, skippedOwned = 0, skippedMalformed = 0, skippedOther = 0;
            if (enabled)
            {
                var routes = RouteStore.CommittedRoutes;
                for (int ri = 0; ri < routes.Count; ri++)
                {
                    Route route = routes[ri];
                    if (route == null || string.IsNullOrEmpty(route.Id)) continue;

                    RouteLineSet set = RefreshForRoute(route, ResolveRecording);

                    RouteLineScope scope = ResolveScope(route, set);

                    var skip = ClassifyRouteLineSkip(
                        route, enabled: true, scope,
                        drawableMemberCount: set.groups != null ? set.groups.Length : 0);
                    if (skip == RouteLineSkipReason.MalformedMixedBodies)
                    { skippedMalformed++; continue; }
                    if (skip != RouteLineSkipReason.None) { skippedOther++; continue; }

                    bool anyDrawn = false;
                    for (int g = 0; g < set.groups.Length; g++)
                    {
                        RouteMemberLegs group = set.groups[g];
                        if (group.legs == null || group.legs.Length == 0) continue;

                        // No-double-draw: the per-cycle ghost polyline already draws this member's
                        // leg when its playback head is on it; skip it here so the static overview
                        // never paints a second identical line over the live ghost trajectory.
                        if (GhostTrajectoryPolylineRenderer.IsRenderingNonOrbitalLeg(group.memberRecordingId))
                        {
                            skippedOwned++;
                            // M-A7: the deferral is a RATIFIED skip (the ghost owns this member's leg
                            // this frame); aggregated per (route, member) by the recorder.
                            Parsek.MapRender.RenderCompositionRecorder.NoteRouteLegDeferred(
                                route.Id, group.memberRecordingId);
                            continue;
                        }

                        LegPolyline[] legs = group.legs; // array ref shared with the cached set
                        string keyBase = "route:" + route.Id + ":" + group.memberRecordingId;
                        for (int i = 0; i < legs.Length; i++)
                        {
                            CelestialBody body = resolveBody(legs[i].bodyName);
                            if (body == null) continue;
                            // requireConicAnchor:false -> draw body-fixed (or anchored when a
                            // bracketing conic exists), never publish ownership.
                            if (GhostTrajectoryPolylineRenderer.TryDrawLeg(
                                    ref legs[i], group.rec, body, targetLayer, frame, keyBase, i,
                                    requireConicAnchor: false))
                            {
                                legsDrawn++;
                                anyDrawn = true;
                                // M-A7 CO-DRAW VIOLATION: the ownership set said nobody owned this
                                // member (we got past the skip above) yet a ghost leg mesh for the same
                                // recording is still live - the one frame shape aggregate skip counts
                                // cannot see (the -50 walk early-returned after clearing the drew set
                                // while last frame's mesh is still active). Recorded on the EVENT only,
                                // so the per-frame cost lands on the defect. Instant no-op when the
                                // manifest env gate is unarmed.
                                if (Parsek.MapRender.RenderCompositionRecorder.IsEnabled
                                    && GhostTrajectoryPolylineRenderer.IsAnyLegActiveForRecording(
                                        group.memberRecordingId))
                                {
                                    Parsek.MapRender.RenderCompositionRecorder.NoteRouteCoDrawViolation(
                                        route.Id, group.memberRecordingId, frame);
                                }
                            }
                        }
                    }
                    if (anyDrawn) routesDrawn++;
                }
            }

            int deactivated = RunDeactivationSweep(frame);

            ParsekLog.VerboseRateLimited(Tag, "route-draw",
                string.Format(CultureInfo.InvariantCulture,
                    "Route line draw: enabled={0} routesDrawn={1} legsDrawn={2} skippedOwned={3} " +
                    "malformed={4} other={5} deact={6} cache={7} frame={8}",
                    enabled, routesDrawn, legsDrawn, skippedOwned, skippedMalformed, skippedOther,
                    deactivated, routeCache.Count, frame),
                2.0);
        }

        private static Recording ResolveRecording(string recordingId)
            => RecordingStore.TryFindCommittedRecordingById(recordingId);

        /// <summary>
        /// THE single owner of the route-scope expression. Non-inter-body routes get the member-body
        /// consistency cross-check (a declared same-body route with a member on another body is
        /// malformed); inter-body routes are expected to span bodies, so the per-frame member-body
        /// collection is skipped entirely (<see cref="ClassifyRouteScope"/> ignores it once the
        /// endpoints answer InterBody). Both the draw pass and the render-composition build capture
        /// call this rather than restating it, so the two can never classify one route two different
        /// ways.
        /// </summary>
        private static RouteLineScope ResolveScope(Route route, RouteLineSet set)
            => ResolveScope(route, set, out _);

        private static RouteLineScope ResolveScope(
            Route route, RouteLineSet set, out RouteScopeBasis basis)
            => ClassifyRouteScope(
                route,
                IsInterBodyByEndpoints(route) ? null : CollectMemberBodies(set),
                out basis);

        private static List<string> CollectMemberBodies(RouteLineSet set)
        {
            if (set.groups == null || set.groups.Length == 0) return null;
            var bodies = new List<string>(set.groups.Length);
            for (int g = 0; g < set.groups.Length; g++)
            {
                Recording rec = set.groups[g].rec;
                if (rec == null) continue;
                string body = !string.IsNullOrEmpty(rec.StartBodyName)
                    ? rec.StartBodyName
                    : rec.SegmentBodyName;
                if (!string.IsNullOrEmpty(body)) bodies.Add(body);
            }
            return bodies;
        }

        private static RouteLineSet RefreshForRoute(Route route, Func<string, Recording> resolve)
        {
            long sig = ComputeRouteSignature(route, resolve);
            if (routeCache.TryGetValue(route.Id, out RouteLineSet existing) && existing.signature == sig)
                return existing;

            if (routeCache.TryGetValue(route.Id, out RouteLineSet stale))
                DestroyRouteLines(stale.groups);

            var groups = BuildRouteMemberLegs(
                route, resolve, out int resolvable, out int totalLegs, out int transferDropped);
            var set = new RouteLineSet { groups = groups.ToArray(), signature = sig };
            routeCache[route.Id] = set;
            BuildInvocationCountForTesting++;

            // The scope decision, logged once per BUILD (signature-gated above, so a steady route
            // logs it once). The line names its own authority so a reader never has to guess which
            // branch of ClassifyRouteScope answered: basis=Endpoints means the route's own
            // origin/stop bodies decided, basis=MemberBodies means no endpoint body was readable
            // and the member-consistency fallback did.
            RouteLineScope loggedScope = ResolveScope(route, set, out RouteScopeBasis loggedBasis);
            ParsekLog.VerboseRateLimited(Tag, "route-scope." + route.Id,
                string.Format(CultureInfo.InvariantCulture,
                    "Route scope: route={0} origin={1} destination={2} scope={3} basis={4}",
                    RouteIds.Short(route.Id),
                    string.IsNullOrEmpty(route.Origin.BodyName) ? "<none>" : route.Origin.BodyName,
                    DestinationBodyLabel(CollectStopBodies(route)),
                    loggedScope, loggedBasis),
                5.0);

            // M-A7 render-composition ROUTE-LINE capture (capture point 5): signature-gated, so this
            // fires only on an actual rebuild, and it reuses the scope the log line above already
            // resolved (one resolution per build, not two). DispatchWindowPeriod is still reported
            // into the manifest record verbatim - it is informational now, but the M-A7 schema keeps
            // the field so a reading run can still see what a save carries.
            if (Parsek.MapRender.RenderCompositionRecorder.IsEnabled)
            {
                Parsek.MapRender.RenderCompositionRecorder.NoteRouteLineBuild(
                    route.Id, sig, route.RecordedDockUT, route.DispatchWindowPeriod,
                    (int)loggedScope,
                    resolvable, set.groups.Length, totalLegs, transferDropped);
            }
            ParsekLog.VerboseRateLimited(Tag, "route-build." + route.Id,
                string.Format(CultureInfo.InvariantCulture,
                    "Route line build: route={0} members={1} groups={2} legs={3} transferDropped={4}",
                    RouteIds.Short(route.Id), resolvable, set.groups.Length, totalLegs, transferDropped),
                5.0);
            return set;
        }

        /// <summary>
        /// Per-frame sweep: hide any cached route leg line not drawn this frame (toggle off, route
        /// skipped because the ghost owns it, member removed, or the line's window went away).
        /// Vectrosity's <c>Draw3D()</c> is one-shot, so a line stays visible until explicitly
        /// deactivated (mirrors the ghost path's deactivation sweep).
        /// </summary>
        private static int RunDeactivationSweep(int frame)
        {
            int deactivated = 0;
            foreach (var kvp in routeCache)
            {
                RouteMemberLegs[] groups = kvp.Value.groups;
                if (groups == null) continue;
                for (int g = 0; g < groups.Length; g++)
                {
                    LegPolyline[] legs = groups[g].legs;
                    if (legs == null) continue;
                    for (int i = 0; i < legs.Length; i++)
                    {
                        VectorLine line = legs[i].vectorLine;
                        if (line == null) continue;
                        if (GhostTrajectoryPolylineRenderer.ShouldDeactivateLeg(
                                line.active, legs[i].lastDrawnFrame, frame))
                        {
                            line.active = false;
                            deactivated++;
                        }
                    }
                }
            }
            return deactivated;
        }

        private static void ReconcileCommittedRoutes()
        {
            if (routeCache.Count == 0) return;
            committedIdScratch.Clear();
            var routes = RouteStore.CommittedRoutes;
            for (int i = 0; i < routes.Count; i++)
            {
                Route route = routes[i];
                if (route != null && !string.IsNullOrEmpty(route.Id))
                    committedIdScratch.Add(route.Id);
            }
            staleKeyScratch.Clear();
            foreach (var kvp in routeCache)
                if (!committedIdScratch.Contains(kvp.Key))
                    staleKeyScratch.Add(kvp.Key);
            for (int i = 0; i < staleKeyScratch.Count; i++)
                ReleaseForRoute(staleKeyScratch[i]);
        }

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        /// <summary>Destroys a route's cached VectorLines and drops its cache entry.</summary>
        internal static void ReleaseForRoute(string routeId)
        {
            if (string.IsNullOrEmpty(routeId)) return;
            if (!routeCache.TryGetValue(routeId, out RouteLineSet set)) return;
            DestroyRouteLines(set.groups);
            routeCache.Remove(routeId);
            ParsekLog.Verbose(Tag, "Route line release: route=" + RouteIds.Short(routeId));
        }

        /// <summary>Destroys every cached route's VectorLines (cross-save flush / scene teardown).</summary>
        internal static void Clear()
        {
            if (routeCache.Count == 0)
            {
                BuildInvocationCountForTesting = 0;
                return;
            }
            int dropped = routeCache.Count;
            foreach (var kvp in routeCache)
                DestroyRouteLines(kvp.Value.groups);
            routeCache.Clear();
            BuildInvocationCountForTesting = 0;
            ParsekLog.Verbose(Tag, "Route line cache clear: dropped=" + dropped);
        }

        private static void DestroyRouteLines(RouteMemberLegs[] groups)
        {
            if (groups == null) return;
            for (int g = 0; g < groups.Length; g++)
            {
                LegPolyline[] legs = groups[g].legs;
                if (legs == null) continue;
                for (int i = 0; i < legs.Length; i++)
                {
                    VectorLine line = legs[i].vectorLine;
                    if (line != null)
                        VectorLine.Destroy(ref line);
                }
            }
        }

        // ------------------------------------------------------------------
        // Testing seams
        // ------------------------------------------------------------------

        internal static int CacheCountForTesting => routeCache.Count;

        /// <summary>
        /// Test-only (in-game): count the currently-active route leg VectorLines across the cache.
        /// Lets an in-game test assert the draw / hide / owned-skip transitions without exposing the
        /// cache internals. Returns 0 headlessly (legs carry null VectorLines).
        /// </summary>
        internal static int ActiveLegCountForTesting()
        {
            int active = 0;
            foreach (var kvp in routeCache)
            {
                RouteMemberLegs[] groups = kvp.Value.groups;
                if (groups == null) continue;
                for (int g = 0; g < groups.Length; g++)
                {
                    LegPolyline[] legs = groups[g].legs;
                    if (legs == null) continue;
                    for (int i = 0; i < legs.Length; i++)
                        if (legs[i].vectorLine != null && legs[i].vectorLine.active)
                            active++;
                }
            }
            return active;
        }

        /// <summary>
        /// Test-only: drive the cache refresh (build + signature gate) headlessly. Legs carry null
        /// VectorLines in tests, so this touches no Unity API. Assert via
        /// <see cref="CacheCountForTesting"/> / <see cref="BuildInvocationCountForTesting"/>.
        /// </summary>
        internal static void RefreshForRouteForTesting(Route route, Func<string, Recording> resolve)
            => RefreshForRoute(route, resolve);

        internal static void ResetForTesting()
        {
            // Tests never draw, so cached legs carry null VectorLines; drop the cache without a
            // Unity Destroy call.
            routeCache.Clear();
            BuildInvocationCountForTesting = 0;
        }
    }
}
