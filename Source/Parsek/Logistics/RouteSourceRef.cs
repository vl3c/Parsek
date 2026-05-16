using System;

namespace Parsek.Logistics
{
    /// <summary>
    /// Immutable proof-of-source reference captured when a route is created.
    /// One <see cref="RouteSourceRef"/> per source recording in the route's
    /// <c>RecordingIds</c> chain. Used at dispatch/delivery time to detect
    /// missing or mutated source recordings (see design §4.7
    /// "Source immutability contract" and §10.15/§10.16).
    /// </summary>
    /// <remarks>
    /// Equality is defined over every field so dedup, fingerprint comparison,
    /// and revalidation are mechanical: two refs are equal iff every captured
    /// proof field matches.
    /// </remarks>
    internal sealed class RouteSourceRef
    {
        /// <summary>Recording ID of the captured source recording.</summary>
        public string RecordingId;

        /// <summary>Tree ID containing the recording at route-creation time.</summary>
        public string TreeId;

        /// <summary>Position of the recording in its tree at capture time.</summary>
        public int TreeOrder;

        /// <summary>Recording format version captured for forward-compat tracking.</summary>
        public int RecordingFormatVersion;

        /// <summary>Recording schema generation captured for forward-compat tracking.</summary>
        public int RecordingSchemaGeneration;

        /// <summary>Sidecar epoch captured for cache-invalidation detection.</summary>
        public int SidecarEpoch;

        /// <summary>Recording start UT at capture time.</summary>
        public double StartUT;

        /// <summary>Recording end UT at capture time.</summary>
        public double EndUT;

        /// <summary>
        /// Fingerprint hash of route-relevant metadata (connection windows,
        /// delivery manifests, …). Captured at route creation. Helper to
        /// compute it lands in Phase 5; this phase just carries the value.
        /// </summary>
        public string RouteProofHash;

        public override bool Equals(object obj)
        {
            var other = obj as RouteSourceRef;
            if (other == null)
                return false;

            return string.Equals(RecordingId, other.RecordingId, StringComparison.Ordinal)
                && string.Equals(TreeId, other.TreeId, StringComparison.Ordinal)
                && TreeOrder == other.TreeOrder
                && RecordingFormatVersion == other.RecordingFormatVersion
                && RecordingSchemaGeneration == other.RecordingSchemaGeneration
                && SidecarEpoch == other.SidecarEpoch
                && StartUT.Equals(other.StartUT)
                && EndUT.Equals(other.EndUT)
                && string.Equals(RouteProofHash, other.RouteProofHash, StringComparison.Ordinal);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (RecordingId != null ? RecordingId.GetHashCode() : 0);
                hash = hash * 31 + (TreeId != null ? TreeId.GetHashCode() : 0);
                hash = hash * 31 + TreeOrder;
                hash = hash * 31 + RecordingFormatVersion;
                hash = hash * 31 + RecordingSchemaGeneration;
                hash = hash * 31 + SidecarEpoch;
                hash = hash * 31 + StartUT.GetHashCode();
                hash = hash * 31 + EndUT.GetHashCode();
                hash = hash * 31 + (RouteProofHash != null ? RouteProofHash.GetHashCode() : 0);
                return hash;
            }
        }
    }
}
