using System;
using System.Collections.Generic;

namespace Parsek.Logistics
{
    /// <summary>
    /// M4b Phase B1 (plan D10 / OQ5): the pure, all-or-nothing N-SOURCE
    /// dispatch gate derived from a route's pickup <see cref="RouteStop"/>s.
    ///
    /// <para>Each pickup window is a <see cref="RouteStop"/> carrying an
    /// <see cref="RouteStop.Endpoint"/> + <see cref="RouteStop.PickupManifest"/>
    /// (+ <see cref="RouteStop.InventoryPickupManifest"/>). A single source
    /// vessel may back several pickup windows (OQ6: same craft-baked pid, deliver
    /// / undock / redock / deliver again), so the gate GROUPS pickup stops by
    /// RESOLVED live vessel pid and SUMS the per-pid manifests before checking -
    /// each window checked independently against the full tank would under-gate
    /// (every window sees the whole tank). The loaded-gate
    /// (<c>loaded &amp;&amp; !packed</c>) is captured ONCE per resolved vessel and
    /// threaded into the stored-amount readers; it is NEVER hoisted across
    /// vessels.</para>
    ///
    /// <para>ALL sources must cover their summed outflow (all-or-nothing); the
    /// FIRST short source - ordered deterministically by the source's smallest
    /// pickup-stop <see cref="RouteStop.RecordedDockUT"/> (ascending, matching the
    /// flown sequence) - names the source in the failure token.</para>
    ///
    /// <para>This gate is INDEPENDENT of the single docked-origin
    /// (<see cref="Route.Origin"/>) provenance: the origin path gates
    /// <see cref="Route.CostManifest"/> against the origin vessel, the pickup-source
    /// path gates each pickup stop's <see cref="RouteStop.PickupManifest"/> against
    /// that stop's endpoint vessel. The two are distinct provenances (launched /
    /// loaded-en-route) and must not double-count a resource. See
    /// <see cref="LiveRouteRuntimeEnvironment.OriginHasCargo"/> for the live wiring.</para>
    ///
    /// <para><b>B2 escrow net (WIRED):</b> the per-pid stored-amount reader
    /// (<see cref="PickupSourceGroup.StoredResourceReader"/>) reads LIVE stored
    /// amounts NET of the cargo escrow. The caller (
    /// <see cref="LiveRouteRuntimeEnvironment.OriginHasCargo"/>) wraps the live
    /// reader to subtract the sum of reservations held by EVERY OTHER route on that
    /// <c>(pid, resource)</c> - via <see cref="RouteStore.OtherRoutesReservedFor"/>,
    /// pure RAM, never via ELS. A route does NOT subtract its OWN reservation (it
    /// owns what it reserved); subtracting only OTHER routes' reservations is the
    /// competing-route protection - route A reserves 100 from depot X at dispatch,
    /// and route B gating X before A's physical debit sees X reduced by A's 100 and
    /// cannot double-claim it. The grouping / summing / first-short logic here is
    /// unchanged by that net; only the reader's value shifts (and the net is a
    /// no-op until something reserves, which lands in B3).</para>
    ///
    /// No KSP statics, no logging, no mutation - the caller owns side effects and
    /// resolves endpoints / captures the loaded-gate / builds the readers.
    /// </summary>
    internal static class RoutePickupSourceGate
    {
        /// <summary>
        /// Caller-supplied resolution of one pickup stop's endpoint to a live
        /// source: the resolved pid + name + once-captured loaded-gate-baked
        /// stored-amount readers. The live env builds this from
        /// <see cref="RouteEndpointResolver"/> + <see cref="LiveOriginCargoProbe"/>
        /// + <see cref="LiveInventoryPickupWriter"/>; tests build it directly.
        /// </summary>
        internal readonly struct PickupSourceResolution
        {
            internal PickupSourceResolution(bool resolved, uint pid, string vesselName,
                Func<string, double> storedResourceReader, Func<string, int> storedInventoryReader,
                string reason)
            {
                Resolved = resolved;
                Pid = pid;
                VesselName = vesselName;
                StoredResourceReader = storedResourceReader;
                StoredInventoryReader = storedInventoryReader;
                Reason = reason;
            }

            internal bool Resolved { get; }
            internal uint Pid { get; }
            internal string VesselName { get; }
            internal Func<string, double> StoredResourceReader { get; }
            internal Func<string, int> StoredInventoryReader { get; }
            internal string Reason { get; }

            internal static PickupSourceResolution Miss(string reason) =>
                new PickupSourceResolution(false, 0u, null, null, null, reason);

            internal static PickupSourceResolution Ok(uint pid, string vesselName,
                Func<string, double> storedResourceReader, Func<string, int> storedInventoryReader) =>
                new PickupSourceResolution(true, pid, vesselName, storedResourceReader, storedInventoryReader, null);
        }

        /// <summary>
        /// One resolved pickup SOURCE: the live vessel a set of same-pid pickup
        /// windows resolved to, the summed resource + inventory manifests across
        /// those windows, the per-vessel stored-amount readers (loaded-gate
        /// already baked in by the caller), and the earliest dock UT for the
        /// deterministic first-short ordering.
        /// </summary>
        internal sealed class PickupSourceGroup
        {
            /// <summary>Resolved live vessel persistent id (the group key).</summary>
            public uint ResolvedPid;

            /// <summary>Player-facing vessel name for the hold reason (best-effort).</summary>
            public string VesselName;

            /// <summary>
            /// Earliest <see cref="RouteStop.RecordedDockUT"/> across the windows
            /// backing this source; the ascending first-short ordering key.
            /// </summary>
            public double EarliestDockUT;

            /// <summary>Summed per-resource pickup amount across this source's windows.</summary>
            public Dictionary<string, double> SummedResourceManifest;

            /// <summary>Summed inventory pickup payloads across this source's windows (by IdentityHash).</summary>
            public List<InventoryPayloadItem> SummedInventoryManifest;

            /// <summary>
            /// Currently-stored amount of the named resource on this source's
            /// resolved vessel, summed across deliverable tanks. The caller bakes
            /// the once-per-vessel loaded-gate into this delegate (B2 nets escrow).
            /// </summary>
            public Func<string, double> StoredResourceReader;

            /// <summary>
            /// Currently-stored count of the identity-hashed stored part on this
            /// source's resolved vessel. Caller bakes the loaded-gate in.
            /// </summary>
            public Func<string, int> StoredInventoryReader;
        }

        /// <summary>
        /// Outcome of a pure multi-source gate evaluation: <see cref="Covered"/>
        /// is all-or-nothing; on a short, <see cref="ShortSourcePid"/> /
        /// <see cref="ShortSourceName"/> / <see cref="ShortResource"/> name the
        /// first short source + its first short resource (or inventory identity),
        /// and <see cref="ShortHoldToken"/> is the ready-to-store
        /// <c>OriginLacksCargo</c> detail token (consumed by
        /// <see cref="Parsek.LogisticsHoldPresentation"/>).
        /// </summary>
        internal readonly struct GateResult
        {
            internal GateResult(bool covered, uint shortPid, string shortName,
                string shortResource, double shortfall, bool inventoryShort, string holdToken)
            {
                Covered = covered;
                ShortSourcePid = shortPid;
                ShortSourceName = shortName;
                ShortResource = shortResource;
                Shortfall = shortfall;
                InventoryShort = inventoryShort;
                ShortHoldToken = holdToken;
            }

            internal bool Covered { get; }
            internal uint ShortSourcePid { get; }
            internal string ShortSourceName { get; }
            internal string ShortResource { get; }
            internal double Shortfall { get; }
            internal bool InventoryShort { get; }
            internal string ShortHoldToken { get; }

            internal static GateResult Ok() =>
                new GateResult(true, 0u, null, null, 0.0, false, null);
        }

        /// <summary>
        /// Build the ordered, summed <see cref="PickupSourceGroup"/> list from a
        /// route's pickup stops. Each <paramref name="resolver"/> call resolves a
        /// stop's <see cref="RouteEndpoint"/> to a live pid + name + once-captured
        /// loaded-gate-baked readers; stops sharing a resolved pid are grouped and
        /// their manifests SUMMED (OQ5/OQ6). A stop whose endpoint does not
        /// resolve is reported via <paramref name="unresolvedReason"/> (the gate
        /// holds the route; the evaluator's step-5 endpoint check normally catches
        /// this first, so this is the defensive re-resolve at the cargo gate).
        ///
        /// <para>Only stops carrying a non-empty <see cref="RouteStop.PickupManifest"/>
        /// or <see cref="RouteStop.InventoryPickupManifest"/> are pickup SOURCES; a
        /// pure-delivery stop contributes nothing. Pure / KSP-free: the caller's
        /// <paramref name="resolver"/> owns every live read.</para>
        /// </summary>
        internal static bool TryBuildSourceGroups(
            Route route,
            Func<RouteEndpoint, PickupSourceResolution> resolver,
            out List<PickupSourceGroup> groups,
            out string unresolvedReason)
        {
            groups = new List<PickupSourceGroup>();
            unresolvedReason = null;

            if (route == null || route.Stops == null || resolver == null)
                return true; // nothing to gate

            // pid -> group index for O(1) accumulation; preserves first-seen order
            // (the final ordering is by EarliestDockUT below).
            var byPid = new Dictionary<uint, int>();

            for (int i = 0; i < route.Stops.Count; i++)
            {
                RouteStop stop = route.Stops[i];
                if (stop == null) continue;

                bool hasResourcePickup = stop.PickupManifest != null && stop.PickupManifest.Count > 0;
                bool hasInventoryPickup = stop.InventoryPickupManifest != null
                    && stop.InventoryPickupManifest.Count > 0;
                if (!hasResourcePickup && !hasInventoryPickup)
                    continue; // pure-delivery stop, not a source

                PickupSourceResolution res = resolver(stop.Endpoint);
                if (!res.Resolved)
                {
                    unresolvedReason = string.IsNullOrEmpty(res.Reason) ? "unknown" : res.Reason;
                    return false;
                }

                PickupSourceGroup group;
                if (byPid.TryGetValue(res.Pid, out int idx))
                {
                    group = groups[idx];
                    // Same-pid windows: keep the EARLIEST dock UT for first-short
                    // ordering; accumulate manifests (the under-gate guard - sum
                    // against the one tank, not each window vs the full tank).
                    if (stop.RecordedDockUT >= 0.0 && stop.RecordedDockUT < group.EarliestDockUT)
                        group.EarliestDockUT = stop.RecordedDockUT;
                }
                else
                {
                    group = new PickupSourceGroup
                    {
                        ResolvedPid = res.Pid,
                        VesselName = res.VesselName,
                        EarliestDockUT = stop.RecordedDockUT >= 0.0
                            ? stop.RecordedDockUT
                            : double.MaxValue,
                        SummedResourceManifest = new Dictionary<string, double>(StringComparer.Ordinal),
                        SummedInventoryManifest = new List<InventoryPayloadItem>(),
                        StoredResourceReader = res.StoredResourceReader,
                        StoredInventoryReader = res.StoredInventoryReader,
                    };
                    byPid[res.Pid] = groups.Count;
                    groups.Add(group);
                }

                if (hasResourcePickup)
                    SumResourceManifestInto(group.SummedResourceManifest, stop.PickupManifest);
                if (hasInventoryPickup)
                    SumInventoryManifestInto(group.SummedInventoryManifest, stop.InventoryPickupManifest);
            }

            return true;
        }

        /// <summary>
        /// Run the all-or-nothing gate over pre-built, pre-summed source groups.
        /// Sources are checked in ASCENDING <see cref="PickupSourceGroup.EarliestDockUT"/>
        /// order (then ordinal pid as a tie-break) so the first short source is
        /// deterministic and matches the flown sequence. Each source's summed
        /// resource manifest is gated via <see cref="RouteOriginCargoCheck.HasRequired"/>
        /// and its summed inventory manifest via
        /// <see cref="RouteOriginCargoCheck.HasRequiredInventory"/>, both using the
        /// group's loaded-gate-baked readers. Returns <see cref="GateResult.Ok"/>
        /// when EVERY source covers, else the first short source.
        /// </summary>
        internal static GateResult Evaluate(List<PickupSourceGroup> groups)
        {
            if (groups == null || groups.Count == 0)
                return GateResult.Ok();

            var ordered = new List<PickupSourceGroup>(groups);
            ordered.Sort((a, b) =>
            {
                int byUt = a.EarliestDockUT.CompareTo(b.EarliestDockUT);
                if (byUt != 0) return byUt;
                return a.ResolvedPid.CompareTo(b.ResolvedPid);
            });

            for (int i = 0; i < ordered.Count; i++)
            {
                PickupSourceGroup g = ordered[i];

                bool covered = RouteOriginCargoCheck.HasRequired(
                    g.SummedResourceManifest, g.StoredResourceReader,
                    out string shortResource, out double shortfall);
                if (!covered)
                {
                    return new GateResult(
                        false, g.ResolvedPid, g.VesselName, shortResource, shortfall,
                        inventoryShort: false,
                        holdToken: BuildHoldToken(g.ResolvedPid, g.VesselName, shortResource));
                }

                bool inventoryCovered = RouteOriginCargoCheck.HasRequiredInventory(
                    g.SummedInventoryManifest, g.StoredInventoryReader,
                    out string shortIdentity, out int shortQuantity);
                if (!inventoryCovered)
                {
                    return new GateResult(
                        false, g.ResolvedPid, g.VesselName, "inventory:" + shortIdentity,
                        shortQuantity, inventoryShort: true,
                        holdToken: BuildHoldToken(g.ResolvedPid, g.VesselName, "inventory:" + shortIdentity));
                }
            }

            return GateResult.Ok();
        }

        /// <summary>
        /// M4b Phase B3 (plan D11 / OQ7): ONE source's escrow reservation - the
        /// per-pid SUMMED resource amounts this route reserves against a single
        /// resolved source vessel at dispatch. Keyed on the SAME resolved
        /// <see cref="ResolvedPid"/> the B1 gate groups + nets on (so the reserve
        /// and the gate read the same pid), with the SUMMED amounts the gate
        /// checked (so reserve == sum-of-per-window-releases per pid).
        /// </summary>
        internal sealed class PickupSourceReservation
        {
            /// <summary>Resolved live vessel pid (the escrow key, same pid the gate netted on).</summary>
            public uint ResolvedPid;
            /// <summary>Player-facing vessel name for the reserve/release log lines (best-effort).</summary>
            public string VesselName;
            /// <summary>Summed per-resource reservation across this source's windows (positive only).</summary>
            public Dictionary<string, double> SummedResourceManifest;
        }

        /// <summary>
        /// M4b Phase B3: build the per-source escrow reservation list from a route's
        /// pickup stops. Reuses <see cref="TryBuildSourceGroups"/> (the SAME grouping
        /// the B1 gate uses), then projects each group to a
        /// <see cref="PickupSourceReservation"/> carrying the resolved pid + summed
        /// resource manifest. The orchestrator calls
        /// <see cref="RouteStore.ReserveCargo"/> once per (pid, resource) entry at
        /// dispatch. A source with no positive resource manifest (inventory-only or
        /// zero-summed) contributes no reservation - inventory escrow is the B3 seam
        /// left for a multi-window inventory consolidation, not wired here (resource
        /// escrow is the primary deliverable). Returns the same
        /// <paramref name="unresolvedReason"/> contract as
        /// <see cref="TryBuildSourceGroups"/> (false on an unresolved endpoint - the
        /// caller should not reserve a partial set).
        /// </summary>
        internal static bool TryBuildReservations(
            Route route,
            Func<RouteEndpoint, PickupSourceResolution> resolver,
            out List<PickupSourceReservation> reservations,
            out string unresolvedReason)
        {
            reservations = new List<PickupSourceReservation>();

            if (!TryBuildSourceGroups(route, resolver, out List<PickupSourceGroup> groups, out unresolvedReason))
                return false;

            for (int i = 0; i < groups.Count; i++)
            {
                PickupSourceGroup g = groups[i];
                if (g.SummedResourceManifest == null || g.SummedResourceManifest.Count == 0)
                    continue; // inventory-only / no resource pickup - no resource reservation

                Dictionary<string, double> positive = null;
                foreach (var kv in g.SummedResourceManifest)
                {
                    if (string.IsNullOrEmpty(kv.Key) || !(kv.Value > 0.0))
                        continue;
                    if (positive == null)
                        positive = new Dictionary<string, double>(g.SummedResourceManifest.Count, StringComparer.Ordinal);
                    positive[kv.Key] = kv.Value;
                }
                if (positive == null)
                    continue; // every summed entry was non-positive

                reservations.Add(new PickupSourceReservation
                {
                    ResolvedPid = g.ResolvedPid,
                    VesselName = g.VesselName,
                    SummedResourceManifest = positive,
                });
            }

            return true;
        }

        /// <summary>
        /// The <c>OriginLacksCargo</c> detail token for a short pickup source,
        /// consumed by <see cref="Parsek.LogisticsHoldPresentation"/>. Shape:
        /// <c>source:&lt;pid&gt;:&lt;name&gt;:&lt;resource-or-inventory-token&gt;</c>.
        /// The name is sanitized of <c>:</c> so the presentation parse stays
        /// unambiguous; an empty name renders as <c>&lt;unnamed&gt;</c>.
        /// </summary>
        internal static string BuildHoldToken(uint pid, string vesselName, string shortToken)
        {
            string name = string.IsNullOrEmpty(vesselName) ? "<unnamed>" : vesselName.Replace(':', '_');
            return "source:" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ":" + name + ":" + (shortToken ?? string.Empty);
        }

        private static void SumResourceManifestInto(
            Dictionary<string, double> accumulator, Dictionary<string, double> add)
        {
            foreach (var kv in add)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                accumulator.TryGetValue(kv.Key, out double cur);
                accumulator[kv.Key] = cur + kv.Value;
            }
        }

        private static void SumInventoryManifestInto(
            List<InventoryPayloadItem> accumulator, List<InventoryPayloadItem> add)
        {
            for (int i = 0; i < add.Count; i++)
            {
                InventoryPayloadItem item = add[i];
                if (item == null || string.IsNullOrEmpty(item.IdentityHash)) continue;

                InventoryPayloadItem existing = null;
                for (int j = 0; j < accumulator.Count; j++)
                {
                    if (accumulator[j] != null
                        && string.Equals(accumulator[j].IdentityHash, item.IdentityHash, StringComparison.Ordinal))
                    {
                        existing = accumulator[j];
                        break;
                    }
                }

                if (existing != null)
                {
                    existing.Quantity += item.Quantity;
                }
                else
                {
                    // Sum into a fresh item so we never mutate the caller's manifest
                    // (the route's stop payloads are immutable from the gate's view).
                    accumulator.Add(new InventoryPayloadItem
                    {
                        IdentityHash = item.IdentityHash,
                        Quantity = item.Quantity,
                    });
                }
            }
        }
    }
}
