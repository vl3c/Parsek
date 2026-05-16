using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Parsek.Logistics
{
    /// <summary>
    /// Static in-memory store for committed supply routes (design §4.7).
    /// Survives scene changes within a KSP session. Save/load is handled by
    /// <see cref="ParsekScenario"/> driving <see cref="SaveRoutesTo"/> /
    /// <see cref="LoadRoutesFrom"/>.
    ///
    /// Phase 3 owns CRUD + codec drivers; Phase 5 adds
    /// <see cref="RevalidateSources(string)"/> which routes through
    /// <see cref="EffectiveState.ComputeERS"/>. This file must not read the
    /// raw committed-recording list or raw ledger actions directly; route
    /// everything through EffectiveState (CI gated by
    /// <c>scripts/grep-audit-ers-els.ps1</c>).
    /// </summary>
    internal static class RouteStore
    {
        private const string Tag = "RouteStore";
        private const string RoutesParentNodeName = "ROUTES";
        private const string RouteChildNodeName = "ROUTE";

        /// <summary>
        /// Stable sentinel returned by <see cref="ComputeRouteProofHashFromRecording"/>
        /// when the recording has no <see cref="Recording.RouteConnectionWindows"/>
        /// AND no <see cref="Recording.RouteOriginProof"/>. Routes built from such a
        /// recording will fingerprint-compare equal as long as both sides agree the
        /// source has no proof.
        /// </summary>
        internal const string NoRouteProofSentinel = "no-route-proof";

        private static readonly List<Route> committedRoutes = new List<Route>();

        /// <summary>Read-only view of currently committed routes.</summary>
        internal static IReadOnlyList<Route> CommittedRoutes => committedRoutes;

        /// <summary>
        /// Add a route. Idempotent on <see cref="Route.Id"/>: a second call
        /// with the same Id logs a Warn and does NOT replace the existing
        /// entry. Callers wanting replace semantics must remove-then-add.
        /// </summary>
        internal static void AddRoute(Route route)
        {
            if (route == null)
            {
                ParsekLog.Warn(Tag, "AddRoute: null route — ignored");
                return;
            }
            if (string.IsNullOrEmpty(route.Id))
            {
                ParsekLog.Warn(Tag, "AddRoute: route with empty Id — ignored");
                return;
            }

            for (int i = 0; i < committedRoutes.Count; i++)
            {
                if (string.Equals(committedRoutes[i].Id, route.Id, System.StringComparison.Ordinal))
                {
                    ParsekLog.Warn(Tag,
                        $"AddRoute: duplicate id={ShortId(route.Id)} (full={route.Id}); " +
                        "keeping the original entry. Callers wanting replace semantics " +
                        "must RemoveRoute first, then AddRoute.");
                    return;
                }
            }

            committedRoutes.Add(route);
            int stopCount = route.Stops != null ? route.Stops.Count : 0;
            ParsekLog.Info(Tag,
                $"Route {ShortId(route.Id)} added: status={route.Status} stops={stopCount}");
        }

        /// <summary>
        /// Remove a route by Id. Returns true on removal, false on miss or on
        /// an empty/null id (both logged at Warn).
        /// </summary>
        internal static bool RemoveRoute(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                ParsekLog.Warn(Tag, "RemoveRoute: null or empty id — ignored");
                return false;
            }

            for (int i = 0; i < committedRoutes.Count; i++)
            {
                if (string.Equals(committedRoutes[i].Id, id, System.StringComparison.Ordinal))
                {
                    committedRoutes.RemoveAt(i);
                    ParsekLog.Info(Tag, $"Route {ShortId(id)} removed");
                    return true;
                }
            }

            ParsekLog.Warn(Tag, $"RemoveRoute: route {ShortId(id)} not found (full={id})");
            return false;
        }

        /// <summary>
        /// Look up a route by Id. Silent on both hit and miss — callers
        /// decide whether to log absence as a warning.
        /// </summary>
        internal static bool TryGetRoute(string id, out Route route)
        {
            if (string.IsNullOrEmpty(id))
            {
                route = null;
                return false;
            }

            for (int i = 0; i < committedRoutes.Count; i++)
            {
                if (string.Equals(committedRoutes[i].Id, id, System.StringComparison.Ordinal))
                {
                    route = committedRoutes[i];
                    return true;
                }
            }

            route = null;
            return false;
        }

        /// <summary>
        /// Clear in-memory state. Test seam — production paths should
        /// remove individual routes through <see cref="RemoveRoute"/>.
        /// </summary>
        internal static void ResetForTesting()
        {
            int prevCount = committedRoutes.Count;
            committedRoutes.Clear();
            ParsekLog.Verbose(Tag, $"ResetForTesting prevCount={prevCount}");
        }

        /// <summary>
        /// Write the current store into <paramref name="parent"/>. Strips any
        /// pre-existing <c>ROUTES</c> children first so stale entries from a
        /// prior save do not leak. When the store is empty, no <c>ROUTES</c>
        /// node is written at all — saves stay lean and
        /// <see cref="LoadRoutesFrom"/> treats a missing node as zero routes.
        /// </summary>
        internal static void SaveRoutesTo(ConfigNode parent)
        {
            if (parent == null)
            {
                ParsekLog.Warn(Tag, "SaveRoutesTo: null parent — skipped");
                return;
            }

            // Always strip pre-existing wrappers before deciding what to
            // write. A previously-saved ROUTES node with stale entries would
            // otherwise survive an empty-store save.
            parent.RemoveNodes(RoutesParentNodeName);

            if (committedRoutes.Count == 0)
            {
                ParsekLog.Verbose(Tag, "SaveRoutesTo: no routes to save");
                return;
            }

            ConfigNode routesNode = parent.AddNode(RoutesParentNodeName);
            for (int i = 0; i < committedRoutes.Count; i++)
            {
                Route route = committedRoutes[i];
                if (route == null) continue;
                ConfigNode routeNode = routesNode.AddNode(RouteChildNodeName);
                route.SerializeInto(routeNode);
            }

            ParsekLog.Info(Tag, $"SaveRoutesTo: wrote {committedRoutes.Count} route(s)");
        }

        /// <summary>
        /// Replace in-memory state with the contents of the <c>ROUTES</c>
        /// child node under <paramref name="parent"/>. Missing
        /// <c>ROUTES</c> node is the common "save with no routes" path —
        /// returns zero without warning. Routes that the Phase-2 codec
        /// rejects (null) are dropped silently here; the codec already
        /// emitted its own Warn explaining the reject reason.
        /// </summary>
        /// <returns>Number of routes successfully loaded.</returns>
        internal static int LoadRoutesFrom(ConfigNode parent)
        {
            // Wholesale replace: clear first, then fill from the save node.
            // Mirrors MilestoneStore.LoadMilestoneFile / RecordingStore load
            // semantics so callers do not have to manage the reset themselves.
            committedRoutes.Clear();

            if (parent == null)
            {
                ParsekLog.Verbose(Tag, "LoadRoutesFrom: null parent — 0 loaded");
                return 0;
            }

            ConfigNode routesNode = parent.GetNode(RoutesParentNodeName);
            if (routesNode == null)
            {
                ParsekLog.Verbose(Tag, "LoadRoutesFrom: no ROUTES node, 0 loaded");
                return 0;
            }

            ConfigNode[] routeNodes = routesNode.GetNodes(RouteChildNodeName);
            int loaded = 0;
            int dropped = 0;
            for (int i = 0; i < routeNodes.Length; i++)
            {
                Route route = Route.DeserializeFrom(routeNodes[i]);
                if (route == null)
                {
                    // Codec already logged the Warn explaining why.
                    dropped++;
                    continue;
                }
                committedRoutes.Add(route);
                loaded++;
            }

            if (dropped > 0)
            {
                ParsekLog.Info(Tag,
                    $"LoadRoutesFrom: loaded {loaded} route(s), {dropped} dropped (see prior Warn lines)");
            }
            else
            {
                ParsekLog.Info(Tag, $"LoadRoutesFrom: loaded {loaded} route(s)");
            }

            return loaded;
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "<no-id>";
            return id.Length > 8 ? id.Substring(0, 8) : id;
        }

        // -----------------------------------------------------------------
        // Phase 5: route-proof fingerprint hash
        // -----------------------------------------------------------------

        /// <summary>
        /// Pure / static. Computes a deterministic fingerprint of the
        /// route-relevant metadata on a <see cref="Recording"/>. Used by
        /// Phase 5 source revalidation to detect source recording rewrites
        /// (optimizer / re-fly). When the recording has neither
        /// <see cref="Recording.RouteConnectionWindows"/> nor
        /// <see cref="Recording.RouteOriginProof"/>, returns
        /// <see cref="NoRouteProofSentinel"/> so the hash remains stable across
        /// the empty-proof shape.
        /// </summary>
        /// <remarks>
        /// Canonical input is built as <c>key=value\n</c> lines with sorted
        /// dictionary keys and InvariantCulture numeric formatting (design §4.2),
        /// then SHA-256 hashed and truncated to 16 hex chars. The exact byte
        /// ordering matters for determinism across runs — never change the field
        /// order or formatting without bumping a fingerprint version.
        /// </remarks>
        internal static string ComputeRouteProofHashFromRecording(Recording rec)
        {
            if (rec == null) return NoRouteProofSentinel;

            bool hasWindows = rec.RouteConnectionWindows != null
                && rec.RouteConnectionWindows.Count > 0;
            bool hasOrigin = rec.RouteOriginProof != null;
            if (!hasWindows && !hasOrigin)
                return NoRouteProofSentinel;

            var sb = new StringBuilder();
            if (hasWindows)
            {
                sb.Append("windows.count=").Append(rec.RouteConnectionWindows.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
                for (int wi = 0; wi < rec.RouteConnectionWindows.Count; wi++)
                {
                    var w = rec.RouteConnectionWindows[wi];
                    string prefix = "windows[" + wi.ToString(CultureInfo.InvariantCulture) + "].";
                    if (w == null)
                    {
                        sb.Append(prefix).Append("null=1\n");
                        continue;
                    }
                    sb.Append(prefix).Append("windowId=").Append(w.WindowId ?? "").Append('\n');
                    sb.Append(prefix).Append("dockUT=").Append(w.DockUT.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
                    sb.Append(prefix).Append("undockUT=").Append(w.UndockUT.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
                    sb.Append(prefix).Append("transferTargetVesselPid=").Append(w.TransferTargetVesselPid.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    sb.Append(prefix).Append("transferKind=").Append(((int)w.TransferKind).ToString(CultureInfo.InvariantCulture)).Append('\n');
                    AppendUintList(sb, prefix + "transportPartPids", w.TransportPartPersistentIds);
                    AppendUintList(sb, prefix + "endpointPartPids", w.EndpointPartPersistentIds);
                    AppendResourceManifest(sb, prefix + "dockTransportRes", w.DockTransportResources);
                    AppendResourceManifest(sb, prefix + "undockTransportRes", w.UndockTransportResources);
                    AppendResourceManifest(sb, prefix + "dockEndpointRes", w.DockEndpointResources);
                    AppendResourceManifest(sb, prefix + "undockEndpointRes", w.UndockEndpointResources);
                    AppendInventoryItems(sb, prefix + "dockTransportInv", w.DockTransportInventory);
                    AppendInventoryItems(sb, prefix + "undockTransportInv", w.UndockTransportInventory);
                    AppendInventoryItems(sb, prefix + "dockEndpointInv", w.DockEndpointInventory);
                    AppendInventoryItems(sb, prefix + "undockEndpointInv", w.UndockEndpointInventory);
                    sb.Append(prefix).Append("transferEndpointSituation=").Append(w.TransferEndpointSituation.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    if (w.EndpointAtDock.HasValue)
                    {
                        var ep = w.EndpointAtDock.Value;
                        sb.Append(prefix).Append("endpointAtDock.vesselPid=").Append(ep.VesselPersistentId.ToString(CultureInfo.InvariantCulture)).Append('\n');
                        sb.Append(prefix).Append("endpointAtDock.body=").Append(ep.BodyName ?? "").Append('\n');
                        sb.Append(prefix).Append("endpointAtDock.lat=").Append(ep.Latitude.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
                        sb.Append(prefix).Append("endpointAtDock.lon=").Append(ep.Longitude.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
                        sb.Append(prefix).Append("endpointAtDock.alt=").Append(ep.Altitude.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
                        sb.Append(prefix).Append("endpointAtDock.isSurface=").Append(ep.IsSurface ? "1" : "0").Append('\n');
                    }
                    else
                    {
                        sb.Append(prefix).Append("endpointAtDock=null\n");
                    }
                }
            }
            else
            {
                sb.Append("windows.count=0\n");
            }

            if (hasOrigin)
            {
                var op = rec.RouteOriginProof;
                sb.Append("origin.startDockedOriginVesselPid=").Append(op.StartDockedOriginVesselPid.ToString(CultureInfo.InvariantCulture)).Append('\n');
                AppendResourceManifest(sb, "origin.startTransportRes", op.StartTransportResources);
                AppendResourceManifest(sb, "origin.endTransportRes", op.EndTransportResources);
                AppendInventoryItems(sb, "origin.startTransportInv", op.StartTransportInventory);
                AppendInventoryItems(sb, "origin.endTransportInv", op.EndTransportInventory);
            }
            else
            {
                sb.Append("origin=null\n");
            }

            // SHA-256, hex-truncated to 16 chars. SHA-256 is overkill for
            // collision resistance here; we use it because it is locale-stable
            // and available on .NET Framework 4.x without extra deps.
            byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var hex = new StringBuilder(16);
                int hexLen = 8; // 8 bytes -> 16 hex chars
                for (int i = 0; i < hexLen; i++)
                    hex.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        // ---- Canonical-input helpers for ComputeRouteProofHashFromRecording ----

        private static void AppendUintList(StringBuilder sb, string prefix, List<uint> list)
        {
            if (list == null)
            {
                sb.Append(prefix).Append("=null\n");
                return;
            }
            // Order-stable: callers pass authored part-pid order; we keep it
            // because rearranging pid order changes the dock/undock physical
            // contract. (Inventories are order-insensitive — see AppendInventoryItems.)
            sb.Append(prefix).Append(".count=").Append(list.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            for (int i = 0; i < list.Count; i++)
            {
                sb.Append(prefix).Append('[').Append(i.ToString(CultureInfo.InvariantCulture)).Append("]=")
                  .Append(list[i].ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
        }

        private static void AppendResourceManifest(
            StringBuilder sb, string prefix, Dictionary<string, ResourceAmount> manifest)
        {
            if (manifest == null)
            {
                sb.Append(prefix).Append("=null\n");
                return;
            }
            sb.Append(prefix).Append(".count=").Append(manifest.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            // Sort by ordinal key so authored order does not perturb the hash.
            var keys = new List<string>(manifest.Keys);
            keys.Sort(StringComparer.Ordinal);
            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i];
                var ra = manifest[key];
                sb.Append(prefix).Append('[').Append(key).Append("].amt=")
                  .Append(ra.amount.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
                sb.Append(prefix).Append('[').Append(key).Append("].max=")
                  .Append(ra.maxAmount.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            }
        }

        private static void AppendInventoryItems(
            StringBuilder sb, string prefix, List<InventoryPayloadItem> items)
        {
            if (items == null)
            {
                sb.Append(prefix).Append("=null\n");
                return;
            }
            sb.Append(prefix).Append(".count=").Append(items.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            // Inventory order is not authored by the player — items get
            // re-listed in storage order on every dock. Sort by identity
            // hash + part name to keep the fingerprint stable across
            // harmless reorders. A real payload change (different identity,
            // quantity, or stored resource) still produces a different hash.
            var sorted = new List<InventoryPayloadItem>(items.Count);
            for (int i = 0; i < items.Count; i++) sorted.Add(items[i]);
            sorted.Sort((x, y) =>
            {
                int cmp = string.CompareOrdinal(x?.IdentityHash ?? "", y?.IdentityHash ?? "");
                if (cmp != 0) return cmp;
                cmp = string.CompareOrdinal(x?.PartName ?? "", y?.PartName ?? "");
                if (cmp != 0) return cmp;
                return string.CompareOrdinal(x?.VariantName ?? "", y?.VariantName ?? "");
            });
            for (int i = 0; i < sorted.Count; i++)
            {
                var it = sorted[i];
                string slot = prefix + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                if (it == null)
                {
                    sb.Append(slot).Append("=null\n");
                    continue;
                }
                sb.Append(slot).Append(".identity=").Append(it.IdentityHash ?? "").Append('\n');
                sb.Append(slot).Append(".part=").Append(it.PartName ?? "").Append('\n');
                sb.Append(slot).Append(".variant=").Append(it.VariantName ?? "").Append('\n');
                sb.Append(slot).Append(".qty=").Append(it.Quantity.ToString(CultureInfo.InvariantCulture)).Append('\n');
                sb.Append(slot).Append(".slots=").Append(it.SlotsTaken.ToString(CultureInfo.InvariantCulture)).Append('\n');
                AppendResourceManifest(sb, slot + ".stored", it.StoredResources);
            }
        }
    }
}
