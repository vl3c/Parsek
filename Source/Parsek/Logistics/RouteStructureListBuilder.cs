using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// Pure route-endpoint location text helpers. They live beside the routes they read
    /// because <see cref="RouteEndpoint"/> is Logistics vocabulary and Missions code must
    /// not depend on Logistics; the body/biome wording itself still comes from the shared
    /// <see cref="StructureLocationFormatter"/> so route and mission rows read alike.
    /// All numeric output uses InvariantCulture.
    /// </summary>
    internal static class RouteEndpointLocationFormatter
    {
        // A route endpoint (origin / dock / delivery / undock). RouteEndpoint is a struct with
        // no backing Recording and NO recorded biome, so for a surface endpoint the biome is
        // resolved at DISPLAY time from body + lat/lon via the injected resolver (the window
        // passes VesselSpawner.TryResolveBiome; headless tests pass null). When no biome
        // resolves, fall back to the surface coordinates. KSC keeps "KSC" in the biome slot.
        internal static string EndpointLocation(
            RouteEndpoint ep, bool isKsc, Func<string, double, double, string> biomeResolver = null)
        {
            if (isKsc)
                return StructureLocationFormatter.BodyBiome(string.IsNullOrEmpty(ep.BodyName) ? "Kerbin" : ep.BodyName, "KSC");
            if (string.IsNullOrEmpty(ep.BodyName))
                return "-";
            if (ep.IsSurface)
            {
                string biome = biomeResolver != null
                    ? biomeResolver(ep.BodyName, ep.Latitude, ep.Longitude)
                    : null;
                if (!string.IsNullOrEmpty(biome))
                    return StructureLocationFormatter.BodyBiome(ep.BodyName, biome);
                return string.Format(CultureInfo.InvariantCulture,
                    "{0} ({1:F2}, {2:F2})", ep.BodyName, ep.Latitude, ep.Longitude);
            }
            return ep.BodyName;
        }

        // The endpoint's situation for the Status column.
        internal static string EndpointStatus(RouteEndpoint ep, bool isKsc)
        {
            if (isKsc) return "Prelaunch";
            return ep.IsSurface ? "Landed" : "Orbiting";
        }
    }

    internal static class RouteStructureListBuilder
    {
        internal static bool SuppressLogging;

        /// <summary>
        /// Builds a route's step list in logical/chronological order: origin, dock,
        /// delivery (per stop), undock. Pure. <paramref name="sourceLookup"/> resolves a
        /// recording id to its committed <see cref="Recording"/>, and
        /// <paramref name="biomeResolver"/> resolves (bodyName, lat, lon) to a biome name
        /// for surface endpoints (the window passes <c>VesselSpawner.TryResolveBiome</c>;
        /// null in headless tests falls back to coordinates). Both injected so the builder
        /// stays free of singletons / live KSP and is headless-testable.
        /// </summary>
        internal static List<StructureStep> Build(
            Route route,
            Func<string, Recording> sourceLookup,
            Func<string, double, double, string> biomeResolver = null)
        {
            var steps = new List<StructureStep>();
            if (route == null)
            {
                if (!SuppressLogging)
                    ParsekLog.Verbose("Route", "BuildStructureList: null route");
                return steps;
            }

            // Origin pseudo-step (no single UT; rendered first via NaN).
            steps.Add(new StructureStep
            {
                UT = double.NaN,
                Kind = StructureStepKind.Origin,
                Label = route.IsKscOrigin ? "Origin: KSC" : "Origin: depot",
                Status = RouteEndpointLocationFormatter.EndpointStatus(route.Origin, route.IsKscOrigin),
                Location = RouteEndpointLocationFormatter.EndpointLocation(route.Origin, route.IsKscOrigin, biomeResolver),
                VesselName = ""
            });

            // Connection window lives on the dock-member recording.
            RouteConnectionWindow win = null;
            Recording dockRec = sourceLookup != null && !string.IsNullOrEmpty(route.DockMemberRecordingId)
                ? sourceLookup(route.DockMemberRecordingId)
                : null;
            if (dockRec?.RouteConnectionWindows != null && dockRec.RouteConnectionWindows.Count > 0)
            {
                // Prefer the last COMPLETE window (the delivery binding; v0 dock members
                // carry exactly one), falling back to the last non-null window so a Dock
                // row still renders for a degenerate incomplete capture.
                for (int i = dockRec.RouteConnectionWindows.Count - 1; i >= 0; i--)
                {
                    RouteConnectionWindow w = dockRec.RouteConnectionWindows[i];
                    if (w != null && w.IsComplete)
                    {
                        win = w;
                        break;
                    }
                }
                if (win == null)
                {
                    for (int i = dockRec.RouteConnectionWindows.Count - 1; i >= 0; i--)
                    {
                        if (dockRec.RouteConnectionWindows[i] != null)
                        {
                            win = dockRec.RouteConnectionWindows[i];
                            break;
                        }
                    }
                }
            }

            bool hasEndpoint = win != null && win.EndpointAtDock.HasValue;
            string endpointLoc = hasEndpoint
                ? RouteEndpointLocationFormatter.EndpointLocation(win.EndpointAtDock.Value, false, biomeResolver)
                : "";
            string endpointStatus = hasEndpoint
                ? RouteEndpointLocationFormatter.EndpointStatus(win.EndpointAtDock.Value, false)
                : "";

            // Dock.
            if (win != null && !double.IsNaN(win.DockUT))
            {
                steps.Add(new StructureStep
                {
                    UT = win.DockUT,
                    Kind = StructureStepKind.Dock,
                    Label = "Dock",
                    Status = endpointStatus,
                    Location = endpointLoc,
                    VesselName = ""
                });
            }

            // Delivery: fires at the recorded dock phase each cycle (RecordedDockUT), one
            // per stop. Falls back to the connection window dock UT when RecordedDockUT is
            // unset. NOTE: the route steps are emitted in logical order (origin, dock,
            // delivery, undock) and NOT sorted; this stays chronological because v0 lifts
            // RecordedDockUT FROM the leaf's RouteConnectionWindow.DockUT (Route.cs), so
            // delivery never precedes dock. If a future capture path makes them diverge, add
            // a UT sort here (Origin pinned first via its NaN UT).
            double deliveryUT = route.RecordedDockUT >= 0
                ? route.RecordedDockUT
                : (win != null ? win.DockUT : double.NaN);
            for (int i = 0; i < route.Stops.Count; i++)
            {
                RouteStop stop = route.Stops[i];
                if (stop == null) continue;
                string num = route.Stops.Count > 1 ? " #" + (i + 1).ToString(CultureInfo.InvariantCulture) : "";
                steps.Add(new StructureStep
                {
                    UT = deliveryUT,
                    Kind = StructureStepKind.Delivery,
                    // M3 Phase 4: the stop label is now direction-aware. A delivery
                    // stop reads "Deliver (...)"; a PURE-pickup stop reads
                    // "Pick up (...)" (a pure-pickup route was not dispatchable
                    // before Phase 4, so this label was unreachable); a MIXED stop
                    // reads "Deliver (...) / Pick up (...)". A degenerate empty stop
                    // falls back to the bare "Deliver" label (unchanged).
                    Label = FormatStopLabel(stop, num),
                    Status = RouteEndpointLocationFormatter.EndpointStatus(stop.Endpoint, false),
                    Location = RouteEndpointLocationFormatter.EndpointLocation(stop.Endpoint, false, biomeResolver),
                    VesselName = ""
                });
            }

            // Undock.
            if (win != null && !double.IsNaN(win.UndockUT))
            {
                steps.Add(new StructureStep
                {
                    UT = win.UndockUT,
                    Kind = StructureStepKind.Undock,
                    Label = "Undock",
                    Status = endpointStatus,
                    Location = endpointLoc,
                    VesselName = ""
                });
            }

            if (!SuppressLogging)
                ParsekLog.Verbose("Route",
                    $"BuildStructureList: route={ShortId(route.Id)} steps={steps.Count} " +
                    $"ksc={route.IsKscOrigin} stops={route.Stops.Count} " +
                    $"window={(win != null ? "yes" : "no")} dockRec={(dockRec != null ? "yes" : "no")}");
            return steps;
        }

        /// <summary>
        /// Builds the direction-aware stop label (M3 Phase 4): "Deliver{num} (...)"
        /// for a delivery stop, "Pick up{num} (...)" for a PURE-pickup stop, and
        /// "Deliver{num} (...) / Pick up (...)" for a MIXED stop. A degenerate
        /// stop with neither manifest renders the bare "Deliver{num}" (the pre-M3
        /// fallback). Pure / internal for direct testing. The pickup direction is
        /// the resource <see cref="RouteStop.PickupManifest"/> +
        /// <see cref="RouteStop.InventoryPickupManifest"/> (Phase 5
        /// inventory still null in Phase 4).
        /// </summary>
        internal static string FormatStopLabel(RouteStop stop, string num)
        {
            if (stop == null) return "Deliver" + (num ?? "");
            num = num ?? "";

            bool hasDelivery =
                (stop.DeliveryManifest != null && stop.DeliveryManifest.Count > 0)
                || (stop.InventoryDeliveryManifest != null && stop.InventoryDeliveryManifest.Count > 0);
            bool hasPickup =
                (stop.PickupManifest != null && stop.PickupManifest.Count > 0)
                || (stop.InventoryPickupManifest != null && stop.InventoryPickupManifest.Count > 0);

            // Pure-pickup: "Pick up (...)" instead of a "Deliver" describing nothing.
            if (hasPickup && !hasDelivery)
                return "Pick up" + num
                    + FormatManifestSummary(stop.PickupManifest, stop.InventoryPickupManifest);

            // Mixed deliver-and-pickup at one dock: both directions named.
            if (hasPickup && hasDelivery)
                return "Deliver" + num
                    + FormatManifestSummary(stop.DeliveryManifest, stop.InventoryDeliveryManifest)
                    + " / Pick up"
                    + FormatManifestSummary(stop.PickupManifest, stop.InventoryPickupManifest);

            // Delivery (or degenerate empty): the pre-M3 "Deliver (...)" label.
            return "Deliver" + num
                + FormatManifestSummary(stop.DeliveryManifest, stop.InventoryDeliveryManifest);
        }

        // Compact "(50 LiquidFuel, 20 Oxidizer, 2 parts)" suffix; empty when nothing.
        private static string FormatManifestSummary(
            Dictionary<string, double> resources, List<InventoryPayloadItem> inventory)
        {
            var parts = new List<string>();
            if (resources != null)
            {
                var keys = new List<string>(resources.Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (string k in keys)
                    parts.Add(string.Format(CultureInfo.InvariantCulture, "{0:F0} {1}", resources[k], k));
            }
            if (inventory != null && inventory.Count > 0)
                parts.Add(inventory.Count.ToString(CultureInfo.InvariantCulture)
                    + (inventory.Count == 1 ? " part" : " parts"));
            return parts.Count == 0 ? "" : " (" + string.Join(", ", parts.ToArray()) + ")";
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "<no-id>";
            return id.Length > 8 ? id.Substring(0, 8) : id;
        }
    }
}
