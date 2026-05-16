using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Parsek.Logistics
{
    /// <summary>
    /// Pure / static fingerprint hasher for the route-relevant metadata on a
    /// <see cref="Recording"/>. Used by <see cref="RouteStore.RevalidateSources"/>
    /// to detect source-recording rewrites (optimizer / re-fly).
    ///
    /// Extracted from <see cref="RouteStore"/> so the ~200-line canonical-input
    /// encoder lives next to its tests in <c>RouteProofHashTests.cs</c> rather
    /// than diluting the CRUD-and-validate surface in RouteStore.
    /// </summary>
    /// <remarks>
    /// Canonical input is built as <c>key=value\n</c> lines with sorted
    /// dictionary keys and InvariantCulture numeric formatting (design §4.2),
    /// then SHA-256 hashed and truncated to 16 hex chars. The exact byte
    /// ordering matters for determinism across runs — never change the field
    /// order or formatting without bumping a fingerprint version.
    /// </remarks>
    internal static class RouteProofHasher
    {
        /// <summary>
        /// Stable sentinel returned by <see cref="ComputeRouteProofHashFromRecording"/>
        /// when the recording has no <see cref="Recording.RouteConnectionWindows"/>
        /// AND no <see cref="Recording.RouteOriginProof"/>. Routes built from such a
        /// recording will fingerprint-compare equal as long as both sides agree the
        /// source has no proof.
        /// </summary>
        internal const string NoRouteProofSentinel = "no-route-proof";

        /// <summary>
        /// Pure / static. Computes a deterministic fingerprint of the
        /// route-relevant metadata on a <see cref="Recording"/>. When the
        /// recording has neither <see cref="Recording.RouteConnectionWindows"/>
        /// nor <see cref="Recording.RouteOriginProof"/>, returns
        /// <see cref="NoRouteProofSentinel"/> so the hash remains stable across
        /// the empty-proof shape.
        /// </summary>
        internal static string ComputeRouteProofHashFromRecording(Recording rec)
        {
            if (rec == null) return NoRouteProofSentinel;

            bool hasWindows = rec.RouteConnectionWindows != null
                && rec.RouteConnectionWindows.Count > 0;
            // IMPORTANT: rec.RouteOriginProof == null and `new RouteOriginProof()` with
            // all-null member lists hash DIFFERENTLY here — the null branch emits
            // `origin=null\n` while a non-null empty proof walks AppendResourceManifest /
            // AppendInventoryItems on the member lists. This relies on the Phase-2 codec
            // preserving null across save/load (see Recording.cs:~713/810 where the deep
            // clone uses `source.RouteOriginProof != null ? ... : null`). A future codec
            // that lazily allocates an empty RouteOriginProof on load would silently
            // flip every existing route to SourceChanged on the first revalidate pass —
            // keep the null-preservation contract.
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
