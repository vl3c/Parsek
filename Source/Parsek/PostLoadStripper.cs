using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Phase 6 of Rewind-to-Staging (design §6.4 step 4): post-load strip of
    /// non-selected sibling vessels at re-fly invocation. Pure static; all
    /// state comes from an injectable vessel enumerator so unit tests can
    /// drive the algorithm without a live KSP scene.
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
    /// A vessel that matches the <c>selectedSlotIndex</c> parameter is left
    /// in place and returned via <see cref="PostLoadStripResult.SelectedVessel"/>;
    /// all other matches are stripped via <c>Vessel.Die()</c>. Phase 6 performs
    /// no pre-despawn snapshot capture — that is deferred to Phase 7's ghost-fy path.
    /// </para>
    ///
    /// <para>
    /// [ERS-exempt — Phase 6] The stripper correlates live vessels to slot
    /// indices via raw <c>Vessel.persistentId</c>, not through the
    /// supersede-aware ERS view. The file is allowlisted in
    /// <c>scripts/ers-els-audit-allowlist.txt</c>.
    /// </para>
    /// </summary>
    internal static class PostLoadStripper
    {
        private const string Tag = "Rewind";

        /// <summary>
        /// Runs the post-load strip using the default <see cref="DefaultVesselEnumeration"/>
        /// that reads live <c>FlightGlobals.Vessels</c>.
        /// </summary>
        internal static PostLoadStripResult Strip(RewindPoint rp, int selectedSlotIndex)
        {
            return Strip(rp, selectedSlotIndex, DefaultVesselEnumeration.Instance);
        }

        /// <summary>
        /// Testable overload: callers inject an <see cref="IVesselEnumeration"/>
        /// so the algorithm operates on mock candidates.
        /// </summary>
        internal static PostLoadStripResult Strip(
            RewindPoint rp, int selectedSlotIndex, IVesselEnumeration source)
        {
            var result = new PostLoadStripResult
            {
                SelectedVessel = null,
                StrippedPids = new List<uint>(),
                GhostsGuarded = 0,
                LeftAlone = 0,
                LeftAlonePidNames = new List<(uint, string)>(),
                FallbackMatches = 0,
            };

            if (rp == null)
            {
                ParsekLog.Warn(Tag, "Strip called with null rp");
                return result;
            }
            if (source == null)
            {
                ParsekLog.Warn(Tag, "Strip called with null source");
                return result;
            }

            var candidates = source.EnumerateVessels();
            if (candidates == null)
            {
                ParsekLog.Warn(Tag,
                    $"Strip: enumerator returned null rp={rp.RewindPointId} " +
                    $"(stripped=[] selected=none)");
                return result;
            }

            var matches = new List<(IStrippableVessel v, int slotIdx)>();

            foreach (var v in candidates)
            {
                if (v == null) continue;

                uint pid = v.PersistentId;

                // Ghost ProtoVessel guard (§7.38).
                if (GhostMapPresence.IsGhostMapVessel(pid))
                {
                    result.GhostsGuarded++;
                    ParsekLog.Verbose(Tag,
                        $"Strip guard: ghost-ProtoVessel v={pid} name='{v.VesselName}'");
                    continue;
                }

                // Primary match.
                int slotIdx;
                if (rp.PidSlotMap != null && rp.PidSlotMap.TryGetValue(pid, out slotIdx))
                {
                    matches.Add((v, slotIdx));
                    continue;
                }

                // Fallback match via root-part persistentId.
                uint rootPid = v.RootPartPersistentId;
                if (rootPid != 0u && rp.RootPartPidMap != null
                    && rp.RootPartPidMap.TryGetValue(rootPid, out slotIdx))
                {
                    result.FallbackMatches++;
                    ParsekLog.Warn(Tag,
                        $"Fallback match via root-part v={pid} rootPart={rootPid} slotIdx={slotIdx}");
                    matches.Add((v, slotIdx));
                    continue;
                }

                // Unrelated vessel.
                result.LeftAlone++;
                if (!string.IsNullOrEmpty(v.VesselName))
                    result.LeftAlonePidNames.Add((pid, v.VesselName));
                ParsekLog.Verbose(Tag,
                    $"Strip leaveAlone: unrelated v={pid} name='{v.VesselName}'");
            }

            IStrippableVessel selected = null;
            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                if (m.slotIdx == selectedSlotIndex)
                {
                    if (selected == null)
                    {
                        selected = m.v;
                    }
                    else
                    {
                        ParsekLog.Warn(Tag,
                            $"Multiple vessels match selectedSlot={selectedSlotIndex}; " +
                            $"keeping pid={selected.PersistentId}, stripping pid={m.v.PersistentId}");
                        StripVessel(m.v, result);
                    }
                }
                else
                {
                    StripVessel(m.v, result);
                }
            }

            result.SelectedVessel = selected?.LiveVessel;
            result.SelectedPid = selected != null ? selected.PersistentId : 0u;

            string strippedIds = string.Join(",", result.StrippedPids.ConvertAll(p => p.ToString()).ToArray());
            string selectedStr = selected != null ? selected.PersistentId.ToString() : "none";
            ParsekLog.Info(Tag,
                $"Strip stripped=[{strippedIds}] selected={selectedStr} " +
                $"ghostsGuarded={result.GhostsGuarded} leftAlone={result.LeftAlone} " +
                $"fallbackMatches={result.FallbackMatches}");

            return result;
        }

        private static void StripVessel(IStrippableVessel v, PostLoadStripResult result)
        {
            if (v == null) return;
            uint pid = v.PersistentId;
            try
            {
                // §6.4 step 4 contract: Strip is a SILENT vessel removal — the
                // despawned siblings must not look like player-driven deaths
                // to GameStateRecorder (no CrewKilled / CrewRemoved / etc.
                // events should fan out to the ledger). The active call site
                // already unsubscribes GameStateRecorder before Strip runs,
                // so today this guard is belt-and-suspenders. Wrapping each
                // Die() locally keeps the silent-removal contract owned by
                // the stripper so a future refactor that reorders the
                // unsubscribe/strip dance cannot accidentally let strip-time
                // crew/resource deltas leak into the ledger.
                using (SuppressionGuard.Crew())
                {
                    v.Die();
                }
                result.StrippedPids.Add(pid);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"Strip Die() threw for v={pid} name='{v.VesselName}': {ex.Message}");
            }
        }

        /// <summary>
        /// Cross-references vessel names the strip left alone against the
        /// re-fly tree's committed recording vessel names and returns the
        /// intersection. A match means a pre-existing quicksave vessel
        /// (e.g. an orbital "Kerbal X" from an earlier career run) shares
        /// its name with a recording in the active tree — the player will
        /// see both in-scene and usually mistakes the real vessel for a
        /// second ghost of the current flight. Caller toasts a warning so
        /// the situation is diagnosable.
        /// </summary>
        /// <param name="leftAloneNames">Names from <see cref="PostLoadStripResult.LeftAlonePidNames"/> (project the name field).</param>
        /// <param name="treeVesselNames">Distinct vessel names from the re-fly tree's committed recordings.</param>
        /// <returns>Distinct collision names (ordinal compare). Empty list when nothing matches or any input is null.</returns>
        internal static List<string> FindTreeNameCollisions(
            IEnumerable<string> leftAloneNames, IEnumerable<string> treeVesselNames)
        {
            var matches = new List<string>();
            if (leftAloneNames == null || treeVesselNames == null) return matches;

            var treeSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (string n in treeVesselNames)
            {
                if (!string.IsNullOrEmpty(n)) treeSet.Add(n);
            }
            if (treeSet.Count == 0) return matches;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string n in leftAloneNames)
            {
                if (string.IsNullOrEmpty(n)) continue;
                if (!treeSet.Contains(n)) continue;
                if (seen.Add(n)) matches.Add(n);
            }
            return matches;
        }
    }

    /// <summary>
    /// Abstraction over a single live vessel for the post-load strip. Production
    /// wraps a <see cref="Vessel"/>; tests wrap a POCO with pre-set identifiers.
    /// </summary>
    internal interface IStrippableVessel
    {
        uint PersistentId { get; }
        uint RootPartPersistentId { get; }
        string VesselName { get; }

        /// <summary>
        /// The underlying <see cref="Vessel"/>, or null in test harnesses.
        /// The activate step (SetActiveVessel) needs a live Vessel; test
        /// harnesses observe that the selected stub was returned and skip
        /// the activate call.
        /// </summary>
        Vessel LiveVessel { get; }

        /// <summary>Despawns the vessel (production: <c>Vessel.Die()</c>).</summary>
        void Die();
    }

    /// <summary>
    /// Injectable source of <see cref="IStrippableVessel"/> candidates. The
    /// default implementation reads <c>FlightGlobals.Vessels</c>.
    /// </summary>
    internal interface IVesselEnumeration
    {
        IEnumerable<IStrippableVessel> EnumerateVessels();
    }

    internal sealed class DefaultVesselEnumeration : IVesselEnumeration
    {
        internal static readonly IVesselEnumeration Instance = new DefaultVesselEnumeration();
        private DefaultVesselEnumeration() { }

        public IEnumerable<IStrippableVessel> EnumerateVessels()
        {
            IList<Vessel> vessels;
            try { vessels = FlightGlobals.Vessels; }
            catch { vessels = null; }
            if (vessels == null) yield break;
            for (int i = 0; i < vessels.Count; i++)
            {
                var v = vessels[i];
                if (v == null) continue;
                yield return new LiveVesselAdapter(v);
            }
        }

        private sealed class LiveVesselAdapter : IStrippableVessel
        {
            private readonly Vessel vessel;
            public LiveVesselAdapter(Vessel v) { vessel = v; }
            public uint PersistentId => vessel != null ? vessel.persistentId : 0u;
            public uint RootPartPersistentId
            {
                get
                {
                    try
                    {
                        return vessel != null && vessel.rootPart != null
                            ? vessel.rootPart.persistentId
                            : 0u;
                    }
                    catch { return 0u; }
                }
            }
            public string VesselName => vessel != null ? vessel.vesselName : null;
            public Vessel LiveVessel => vessel;
            public void Die()
            {
                if (vessel != null) vessel.Die();
            }
        }
    }

    /// <summary>
    /// Diagnostics output from <see cref="PostLoadStripper.Strip(RewindPoint, int)"/>.
    /// </summary>
    internal struct PostLoadStripResult
    {
        /// <summary>Live vessel that matched the selected slot (may be null on failure or in tests).</summary>
        public Vessel SelectedVessel;

        /// <summary>
        /// PID of the vessel that matched the selected slot, even when
        /// <see cref="SelectedVessel"/> is null (test harnesses use stubs).
        /// </summary>
        public uint SelectedPid;

        /// <summary>PIDs of vessels that were despawned via <c>Vessel.Die()</c>.</summary>
        public List<uint> StrippedPids;

        /// <summary>Count of vessels skipped because they are Parsek ghost ProtoVessels.</summary>
        public int GhostsGuarded;

        /// <summary>Count of vessels left alone because they did not match any slot map.</summary>
        public int LeftAlone;

        /// <summary>
        /// (pid, name) pairs of vessels the strip left alone (collected alongside
        /// <see cref="LeftAlone"/>). Used by the caller to surface a diagnostic
        /// warning when a left-alone vessel shares its name with a committed
        /// recording in the re-fly tree — a classic case is a prior-career
        /// "Kerbal X" still orbiting that the player confuses with the
        /// current flight's ghost.
        /// <para>
        /// PR #577 P2 review: pids are retained alongside names so the
        /// post-supplement re-survey can scope the live-vessel count to the
        /// pre-existing left-alone set — the live-name-only resurvey would
        /// otherwise count the actively re-flown vessel, ghost-ProtoVessels,
        /// or any other legitimate same-name vessel as a "leftover", producing
        /// a false-positive WARN/toast.
        /// </para>
        /// </summary>
        public List<(uint pid, string name)> LeftAlonePidNames;

        /// <summary>Count of matches that used the root-part fallback path.</summary>
        public int FallbackMatches;
    }
}
