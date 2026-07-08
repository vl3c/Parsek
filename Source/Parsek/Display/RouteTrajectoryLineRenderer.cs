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
    /// v1 SCOPE: SAME-BODY routes only (<see cref="Route.DispatchWindowPeriod"/> == 0, the shipped
    /// route shape). Inter-body route paths depend on the re-aimed transfer render (milestone M5,
    /// PR #1238) and are a documented follow-up. Behind the <c>showRouteLines</c> setting
    /// (default on).
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
            NotSameBody = 3,
            NoBackingRecordings = 4,
        }

        // ------------------------------------------------------------------
        // Pure decision helpers (Unity-free, unit-tested)
        // ------------------------------------------------------------------

        /// <summary>Whether route lines are enabled. Null settings (tests / pre-load) fall back to
        /// <see cref="DefaultShowRouteLines"/>.</summary>
        internal static bool RouteLinesEnabled(ParsekSettings settings)
            => settings == null ? DefaultShowRouteLines : settings.showRouteLines;

        /// <summary>
        /// True when a route is same-body (v1 scope). <see cref="Route.DispatchWindowPeriod"/> is
        /// the authoritative flag (0 = same-body, the synodic period for inter-body). When the
        /// resolved member bodies are supplied they must all agree — a period of 0 with mixed
        /// bodies is a malformed route we decline rather than draw a cross-body chord.
        /// </summary>
        internal static bool IsSameBodyRoute(double dispatchWindowPeriod, IReadOnlyList<string> memberBodies)
        {
            if (dispatchWindowPeriod != 0.0) return false;
            if (memberBodies == null || memberBodies.Count == 0) return true;
            string first = null;
            for (int i = 0; i < memberBodies.Count; i++)
            {
                string b = memberBodies[i];
                if (string.IsNullOrEmpty(b)) continue;
                if (first == null) first = b;
                else if (!string.Equals(first, b, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        /// <summary>Pure skip classification for a candidate route line.</summary>
        internal static RouteLineSkipReason ClassifyRouteLineSkip(
            Route route, bool enabled, bool sameBody, int drawableMemberCount)
        {
            if (route == null) return RouteLineSkipReason.NullRoute;
            if (!enabled) return RouteLineSkipReason.Disabled;
            if (!sameBody) return RouteLineSkipReason.NotSameBody;
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
        /// contribute no drawable leg, are dropped. READ-ONLY over the route + recording data.
        /// </summary>
        internal static List<RouteMemberLegs> BuildRouteMemberLegs(
            Route route, Func<string, Recording> resolve,
            out int resolvableMembers, out int totalLegs)
        {
            resolvableMembers = 0;
            totalLegs = 0;
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
            return groups;
        }

        /// <summary>
        /// Content signature that gates a route-line rebuild. Folds the ordered recording ids, each
        /// resolvable member's polyline content hash (so an optimizer re-cut or supersede rebuild
        /// invalidates the cached line), the dock-clip UT, and the same-body flag (so a route that
        /// flips inter-body invalidates). Pure and stable across a save round-trip.
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
                h ^= route.DispatchWindowPeriod != 0.0 ? 0x5bd1e9955bd1e995L : 0L;
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

            int routesDrawn = 0, legsDrawn = 0, skippedOwned = 0, skippedInterBody = 0, skippedOther = 0;
            if (enabled)
            {
                var routes = RouteStore.CommittedRoutes;
                for (int ri = 0; ri < routes.Count; ri++)
                {
                    Route route = routes[ri];
                    if (route == null || string.IsNullOrEmpty(route.Id)) continue;

                    // Authoritative same-body quick gate: decline inter-body routes without
                    // resolving any recording (v1 scope).
                    if (route.DispatchWindowPeriod != 0.0) { skippedInterBody++; continue; }

                    RouteLineSet set = RefreshForRoute(route, ResolveRecording);

                    // Defensive same-body cross-check on the resolved member bodies.
                    if (!IsSameBodyRoute(route.DispatchWindowPeriod, CollectMemberBodies(set)))
                    { skippedOther++; continue; }

                    var skip = ClassifyRouteLineSkip(
                        route, enabled: true, sameBody: true,
                        drawableMemberCount: set.groups != null ? set.groups.Length : 0);
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
                        { skippedOwned++; continue; }

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
                    "interBody={4} other={5} deact={6} cache={7} frame={8}",
                    enabled, routesDrawn, legsDrawn, skippedOwned, skippedInterBody, skippedOther,
                    deactivated, routeCache.Count, frame),
                2.0);
        }

        private static Recording ResolveRecording(string recordingId)
            => RecordingStore.TryFindCommittedRecordingById(recordingId);

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

            var groups = BuildRouteMemberLegs(route, resolve, out int resolvable, out int totalLegs);
            var set = new RouteLineSet { groups = groups.ToArray(), signature = sig };
            routeCache[route.Id] = set;
            BuildInvocationCountForTesting++;
            ParsekLog.VerboseRateLimited(Tag, "route-build." + route.Id,
                string.Format(CultureInfo.InvariantCulture,
                    "Route line build: route={0} members={1} groups={2} legs={3}",
                    RouteIds.Short(route.Id), resolvable, set.groups.Length, totalLegs),
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
