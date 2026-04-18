using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Phase 6 of Rewind-to-Staging (design §6.4 step 4): post-load strip of
    /// non-selected sibling vessels at re-fly invocation. Pure static; all
    /// state comes from <c>FlightGlobals.Vessels</c> and the supplied
    /// <see cref="RewindPoint"/>.
    ///
    /// <para>
    /// Algorithm:
    /// <list type="number">
    ///   <item><description>Enumerate live vessels.</description></item>
    ///   <item><description>Skip ghost ProtoVessels (<see cref="GhostMapPresence.IsGhostMapVessel"/>) - §7.38.</description></item>
    ///   <item><description>Primary match via <see cref="RewindPoint.PidSlotMap"/> by <c>Vessel.persistentId</c>.</description></item>
    ///   <item><description>Fallback match via <see cref="RewindPoint.RootPartPidMap"/> by <c>rootPart.persistentId</c>.</description></item>
    ///   <item><description>Non-matches are left alone (§7.39).</description></item>
    /// </list>
    /// A vessel that matches <paramref name="selectedSlotIndex"/> is returned as
    /// <see cref="PostLoadStripResult.SelectedVessel"/>; all other matches are
    /// stripped via <c>Vessel.Die()</c>. Phase 6 performs no pre-despawn snapshot
    /// capture — that is deferred to Phase 7's ghost-fy path.
    /// </para>
    ///
    /// <para>
    /// [ERS-exempt — Phase 6] The stripper correlates live vessels to
    /// slot indices via raw <c>Vessel.persistentId</c>, not through the
    /// supersede-aware ERS view. The file is allowlisted in
    /// <c>scripts/ers-els-audit-allowlist.txt</c>.
    /// </para>
    /// </summary>
    internal static class PostLoadStripper
    {
        private const string Tag = "Rewind";

        /// <summary>
        /// Runs the post-load strip. Returns a <see cref="PostLoadStripResult"/>
        /// with the selected vessel (may be null on total failure) and
        /// diagnostics counters.
        /// </summary>
        internal static PostLoadStripResult Strip(RewindPoint rp, int selectedSlotIndex)
        {
            var result = new PostLoadStripResult
            {
                SelectedVessel = null,
                StrippedPids = new List<uint>(),
                GhostsGuarded = 0,
                LeftAlone = 0,
                FallbackMatches = 0,
            };

            if (rp == null)
            {
                ParsekLog.Warn(Tag, "Strip called with null rp");
                return result;
            }

            var allVessels = SafeGetAllVessels();
            if (allVessels == null || allVessels.Count == 0)
            {
                ParsekLog.Info(Tag,
                    $"Strip stripped=[] selected=none ghostsGuarded=0 leftAlone=0 fallbackMatches=0 " +
                    $"(FlightGlobals empty rp={rp.RewindPointId})");
                return result;
            }

            // Two passes so stripping does not mutate the iterator.
            var matches = new List<(Vessel v, int slotIdx, bool viaFallback)>();

            for (int i = 0; i < allVessels.Count; i++)
            {
                var v = allVessels[i];
                if (v == null) continue;

                uint pid = v.persistentId;

                // Ghost ProtoVessel guard (§7.38).
                if (GhostMapPresence.IsGhostMapVessel(pid))
                {
                    result.GhostsGuarded++;
                    ParsekLog.Verbose(Tag,
                        $"Strip guard: ghost-ProtoVessel v={pid} name='{v.vesselName}'");
                    continue;
                }

                // Primary match.
                int slotIdx;
                if (rp.PidSlotMap != null && rp.PidSlotMap.TryGetValue(pid, out slotIdx))
                {
                    matches.Add((v, slotIdx, false));
                    continue;
                }

                // Fallback match via root-part persistentId.
                uint rootPid = 0u;
                try
                {
                    if (v.rootPart != null)
                        rootPid = v.rootPart.persistentId;
                }
                catch
                {
                    rootPid = 0u;
                }
                if (rootPid != 0u && rp.RootPartPidMap != null
                    && rp.RootPartPidMap.TryGetValue(rootPid, out slotIdx))
                {
                    result.FallbackMatches++;
                    ParsekLog.Warn(Tag,
                        $"Fallback match via root-part v={pid} rootPart={rootPid} slotIdx={slotIdx}");
                    matches.Add((v, slotIdx, true));
                    continue;
                }

                // Unrelated vessel.
                result.LeftAlone++;
                ParsekLog.Verbose(Tag,
                    $"Strip leaveAlone: unrelated v={pid} name='{v.vesselName}'");
            }

            // Apply decisions.
            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                if (m.slotIdx == selectedSlotIndex)
                {
                    if (result.SelectedVessel == null)
                    {
                        result.SelectedVessel = m.v;
                    }
                    else
                    {
                        // Two vessels claim the same selected slot. Keep the
                        // first and strip subsequent duplicates so the active
                        // vessel is deterministic.
                        ParsekLog.Warn(Tag,
                            $"Multiple vessels match selectedSlot={selectedSlotIndex}; " +
                            $"keeping pid={result.SelectedVessel.persistentId}, stripping pid={m.v.persistentId}");
                        StripVessel(m.v, result);
                    }
                }
                else
                {
                    StripVessel(m.v, result);
                }
            }

            string strippedIds = string.Join(",", result.StrippedPids.ConvertAll(p => p.ToString()).ToArray());
            string selectedStr = result.SelectedVessel != null
                ? result.SelectedVessel.persistentId.ToString()
                : "none";
            ParsekLog.Info(Tag,
                $"Strip stripped=[{strippedIds}] selected={selectedStr} " +
                $"ghostsGuarded={result.GhostsGuarded} leftAlone={result.LeftAlone} " +
                $"fallbackMatches={result.FallbackMatches}");

            return result;
        }

        private static void StripVessel(Vessel v, PostLoadStripResult result)
        {
            if (v == null) return;
            uint pid = v.persistentId;
            try
            {
                v.Die();
                result.StrippedPids.Add(pid);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"Strip Die() threw for v={pid} name='{v.vesselName}': {ex.Message}");
            }
        }

        // Test seam: production reads FlightGlobals.Vessels; tests assign this to
        // drive the enumeration without a live KSP scene.
        internal static Func<IList<Vessel>> AllVesselsOverrideForTesting;

        private static IList<Vessel> SafeGetAllVessels()
        {
            if (AllVesselsOverrideForTesting != null)
                return AllVesselsOverrideForTesting();
            try
            {
                return FlightGlobals.Vessels;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Diagnostics output from <see cref="PostLoadStripper.Strip"/>.
    /// </summary>
    internal struct PostLoadStripResult
    {
        /// <summary>Vessel that matched the selected slot (may be null on failure).</summary>
        public Vessel SelectedVessel;

        /// <summary>PIDs of vessels that were despawned via <c>Vessel.Die()</c>.</summary>
        public List<uint> StrippedPids;

        /// <summary>Count of vessels skipped because they are Parsek ghost ProtoVessels.</summary>
        public int GhostsGuarded;

        /// <summary>Count of vessels left alone because they did not match any slot map.</summary>
        public int LeftAlone;

        /// <summary>Count of matches that used the root-part fallback path.</summary>
        public int FallbackMatches;
    }
}
