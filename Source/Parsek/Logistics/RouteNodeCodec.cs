using System;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// Shared ConfigNode sub-codecs that are byte-identical across
    /// <see cref="RouteCodec"/> (the ROUTE node) and
    /// <see cref="Parsek.RouteProofCodec"/> (the per-Recording route-proof node).
    ///
    /// Only the proven field-for-field identical sub-codecs live here; both call
    /// sites keep their original method names as thin wrappers so every existing
    /// caller is unchanged. The serialized on-disk shape is frozen (gen-4 schema,
    /// no migrations) — the round-trip serialization suites are the safety net.
    ///
    /// NOT shared (intentionally): the resource-MANIFEST codecs (RouteCodec writes
    /// a flat <c>name=amount</c> manifest, RouteProofCodec writes nested
    /// <c>RESOURCE{name,amount,maxAmount}</c> nodes — different on-disk shapes), the
    /// inventory-item codec (RouteCodec writes <c>STOREDPART</c> directly under
    /// <c>ITEM</c>, RouteProofCodec wraps it in <c>STOREDPART_SNAPSHOT</c>), and the
    /// endpoint DESERIALIZER (the two read the keys in a different order).
    /// </summary>
    internal static class RouteNodeCodec
    {
        // Endpoint shape: vesselPersistentId (sparse on pid == 0), bodyName (sparse
        // on empty), latitude, longitude, altitude, isSurface. The same struct
        // serializes the same way whether it lands inside a ROUTE node or inside a
        // RouteConnectionWindow. Sparse writes on pid == 0 and empty body name keep
        // KSC origins (pid == 0) byte-identical on both sides.
        internal static void SerializeEndpoint(ConfigNode node, RouteEndpoint ep, CultureInfo ic)
        {
            if (ep.VesselPersistentId != 0)
                node.AddValue("vesselPersistentId", ep.VesselPersistentId.ToString(ic));
            if (!string.IsNullOrEmpty(ep.BodyName))
                node.AddValue("bodyName", ep.BodyName);
            node.AddValue("latitude", ep.Latitude.ToString("R", ic));
            node.AddValue("longitude", ep.Longitude.ToString("R", ic));
            node.AddValue("altitude", ep.Altitude.ToString("R", ic));
            node.AddValue("isSurface", ep.IsSurface.ToString());
        }

        internal static RouteConnectionKind ParseConnectionKind(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return RouteConnectionKind.None;

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue)
                && Enum.IsDefined(typeof(RouteConnectionKind), intValue))
            {
                return (RouteConnectionKind)intValue;
            }

            if (Enum.TryParse(raw, out RouteConnectionKind kind)
                && Enum.IsDefined(typeof(RouteConnectionKind), kind))
            {
                return kind;
            }

            return RouteConnectionKind.Unknown;
        }
    }
}
