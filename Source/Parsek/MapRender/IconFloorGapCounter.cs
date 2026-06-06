using System.Collections.Generic;
using System.Globalization;

namespace Parsek.MapRender
{
    /// <summary>
    /// Phase 8e S0 Instrument 2 (PURELY ADDITIVE): per-frame gap-counter proving the SEPARATE deletion
    /// invariant the polyline coverage assertion does NOT cover - that the legacy effUT ICON floor is
    /// dead for in-scope ghosts. A proto-BEARING StockConic ghost still falls to the legacy effUT icon
    /// drive on any frame with no fresh Director seed: the <c>!fresh || !directorDriveActive</c> branch
    /// in <c>GhostOrbitIconDrivePatch.Prefix</c> (the <c>propagateUT = driveUT</c> legacy path) and the
    /// no-bounds loiter-&gt;burn early return. This counter MEASURES those legacy-floor frames, tagged by
    /// pid + reason; the count must reach 0 for in-scope ghosts before a later slice (S2) deletes the
    /// icon floor. S0 only counts - it does NOT change the fallback behavior.
    ///
    /// <para>The classifier is pure (Unity-free, unit-tested). The accumulator + the rate-limited summary
    /// are gated by the call sites on <see cref="MapRenderTrace.IsEnabled"/> so default play pays nothing.
    /// The accumulator is per (pid, reason); the summary is emitted once per frame with a WARP-STABLE
    /// rate-limit key (the stable tag <c>"icon-floor-fallback"</c>, never a cycle/UT/frame value - the
    /// #1063 / #1064 lesson), carrying the aggregate count + the distinct pids + reasons in the BODY.</para>
    /// </summary>
    internal static class IconFloorGapCounter
    {
        /// <summary>Why a proto-bearing StockConic ghost took the legacy icon floor this frame.</summary>
        internal enum FloorReason : byte
        {
            /// <summary>Not a legacy-floor frame (the Director drove the icon, or the gate was off). The
            /// classifier returns this when nothing should be counted.</summary>
            None = 0,

            /// <summary>The director-drive gate is OFF, so the legacy floor is the ONLY path. Counting it
            /// is meaningless ("under gate-ON" is the in-scope condition), so this is NOT counted - the
            /// classifier returns it only so the gate-off case is explicit, not folded into a real reason.</summary>
            GateOff = 1,

            /// <summary>No recorded arc bounds (the no-bounds loiter-&gt;burn early return): stock
            /// propagates at the live clock with no Director ownership of the icon. Also covers the benign
            /// terminal-orbit shift-0 case, which the reader separates by pid.</summary>
            NoBounds = 2,

            /// <summary>Gate ON, recorded bounds present, but the Director recorded NO fresh StockConic
            /// seed for this pid this frame, so the icon-drive ran the legacy effUT path. (A re-aim TRIM
            /// GAP, where the seed expires for a frame, manifests as this same observable - the icon-drive
            /// site has no cheap re-aim context to distinguish it, so it folds into no-fresh-seed.)</summary>
            NoFreshSeed = 3,

            /// <summary>Gate ON, a FRESH StockConic seed existed, but its body name did not resolve
            /// (degenerate / unknown body, never for a real recording), so the icon-drive fell back to the
            /// legacy effUT path rather than baking the epoch.</summary>
            UnresolvableSeedBody = 4,
        }

        internal static string FloorReasonToken(FloorReason reason)
        {
            switch (reason)
            {
                case FloorReason.GateOff: return "gate-off";
                case FloorReason.NoBounds: return "no-bounds";
                case FloorReason.NoFreshSeed: return "no-fresh-seed";
                case FloorReason.UnresolvableSeedBody: return "unresolvable-seed-body";
                default: return "none";
            }
        }

        /// <summary>
        /// PURE classifier (Unity-free): given the icon-drive Prefix's observable branch inputs, which
        /// legacy-floor reason (if any) applies for a proto-bearing StockConic ghost this frame?
        /// <list type="bullet">
        /// <item><paramref name="gateOn"/> false -&gt; <see cref="FloorReason.GateOff"/> (NOT counted -
        /// the legacy path is the only path with the gate off).</item>
        /// <item><paramref name="hasBounds"/> false -&gt; <see cref="FloorReason.NoBounds"/> (the
        /// loiter-&gt;burn / terminal-orbit no-bounds early return).</item>
        /// <item>bounds present, <paramref name="freshSeed"/> true, <paramref name="seedBodyResolved"/>
        /// false -&gt; <see cref="FloorReason.UnresolvableSeedBody"/>.</item>
        /// <item>bounds present, <paramref name="freshSeed"/> false -&gt;
        /// <see cref="FloorReason.NoFreshSeed"/>.</item>
        /// <item>bounds present, fresh seed, body resolves -&gt; <see cref="FloorReason.None"/> (the
        /// Director DROVE the icon; NOT a legacy-floor frame).</item>
        /// </list>
        /// Mirrors EXACTLY the live branch order in <c>GhostOrbitIconDrivePatch.Prefix</c> /
        /// <c>ShadowRenderDriver.IsDirectorDriveActive</c> (gate AND fresh seed AND body resolves), so a
        /// "None" result == a director-driven frame and any non-None/non-GateOff result == a real legacy
        /// floor under the gate.
        /// </summary>
        internal static FloorReason Classify(
            bool gateOn, bool hasBounds, bool freshSeed, bool seedBodyResolved)
        {
            if (!gateOn)
                return FloorReason.GateOff;
            if (!hasBounds)
                return FloorReason.NoBounds;
            if (freshSeed && !seedBodyResolved)
                return FloorReason.UnresolvableSeedBody;
            if (!freshSeed)
                return FloorReason.NoFreshSeed;
            return FloorReason.None; // director drove the icon - not a floor frame
        }

        /// <summary>True when <paramref name="reason"/> is a real legacy-floor-under-gate-ON reason worth
        /// counting (everything except <see cref="FloorReason.None"/> and <see cref="FloorReason.GateOff"/>).
        /// Pure.</summary>
        internal static bool IsCountableFloor(FloorReason reason)
            => reason != FloorReason.None && reason != FloorReason.GateOff;

        // Per-frame accumulator: (pid, reason) -> count. Reset every frame by FlushFrameSummary. Tracked
        // distinct pids + reasons feed the summary body. Bounded by the live-ghost count (a handful) +
        // the small reason enum, so no warp-unbounded growth.
        private static readonly Dictionary<(uint pid, FloorReason reason), int> floorCountsThisFrame =
            new Dictionary<(uint, FloorReason), int>();

        /// <summary>
        /// Record one legacy-floor frame for <paramref name="pid"/> with <paramref name="reason"/>. No-op
        /// for a non-countable reason (None / GateOff) so the call site can pass the raw classifier result.
        /// Caller gates on <see cref="MapRenderTrace.IsEnabled"/> so default play never accumulates. NOT a
        /// behavior change - pure measurement.
        /// </summary>
        internal static void NoteLegacyFloor(uint pid, FloorReason reason)
        {
            if (!IsCountableFloor(reason))
                return;
            var key = (pid, reason);
            floorCountsThisFrame.TryGetValue(key, out int n);
            floorCountsThisFrame[key] = n + 1;
        }

        /// <summary>
        /// Emit the once-per-frame rate-limited summary of the accumulated legacy-floor frames, then reset
        /// the accumulator. Called once per frame from <c>ShadowRenderDriver.RunFrame</c> (the single
        /// once-per-frame Director entry), gated by the caller on <see cref="MapRenderTrace.IsEnabled"/>.
        /// The rate-limit KEY is the WARP-STABLE constant <c>"icon-floor-fallback"</c> (never a cycle / UT
        /// / frame, per the #1063 / #1064 lesson); the aggregate count + the distinct pids + reasons live
        /// in the message BODY. No-op when nothing hit the floor this frame.
        /// </summary>
        internal static void FlushFrameSummary()
        {
            if (floorCountsThisFrame.Count == 0)
                return;

            int total = 0;
            var pids = new HashSet<uint>();
            var reasons = new HashSet<FloorReason>();
            foreach (var kv in floorCountsThisFrame)
            {
                total += kv.Value;
                pids.Add(kv.Key.pid);
                reasons.Add(kv.Key.reason);
            }

            var pidList = new List<string>(pids.Count);
            foreach (uint p in pids)
                pidList.Add(p.ToString(CultureInfo.InvariantCulture));
            pidList.Sort(System.StringComparer.Ordinal);

            var reasonList = new List<string>(reasons.Count);
            foreach (FloorReason r in reasons)
                reasonList.Add(FloorReasonToken(r));
            reasonList.Sort(System.StringComparer.Ordinal);

            ParsekLog.VerboseRateLimited("MapRender", "icon-floor-fallback",
                string.Format(CultureInfo.InvariantCulture,
                    "icon-floor: {0} proto-bearing StockConic frame(s) hit the legacy icon floor this window "
                    + "(distinctPids={1} pids=[{2}] reasons=[{3}]) - must reach 0 before S2 deletes the floor",
                    total, pids.Count, string.Join(",", pidList.ToArray()),
                    string.Join(",", reasonList.ToArray())),
                5.0);

            floorCountsThisFrame.Clear();
        }

        /// <summary>Test-only: current accumulated count for a (pid, reason) so the accumulator can be
        /// asserted before a flush.</summary>
        internal static int CountForTesting(uint pid, FloorReason reason)
            => floorCountsThisFrame.TryGetValue((pid, reason), out int n) ? n : 0;

        /// <summary>Test-only: number of distinct (pid, reason) buckets accumulated this frame.</summary>
        internal static int BucketCountForTesting => floorCountsThisFrame.Count;

        /// <summary>Drop the accumulator (defensive scene-switch reset; the per-frame flush also clears
        /// it). Diagnostic-only.</summary>
        internal static void Reset() => floorCountsThisFrame.Clear();

        /// <summary>Test-only alias of <see cref="Reset"/> so a test starts from empty.</summary>
        internal static void ResetForTesting() => Reset();
    }
}
