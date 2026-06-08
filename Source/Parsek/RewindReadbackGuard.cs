using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// A null-safe snapshot of the live KSP career economy (funds / science /
    /// reputation) read straight off the stock singletons. A <c>null</c> field means
    /// "no finite witness was available" — either the singleton was absent (sandbox /
    /// early load) or the read was NaN / Infinity (already corrupt and useless as a
    /// floor). The rewind read-back guard captures two of these (pre-rewind and
    /// post-load) to build a downward-only divergence floor.
    /// </summary>
    internal struct EconomySnapshot
    {
        internal double? Funds;
        internal double? Science;
        internal float? Reputation;

        /// <summary>
        /// Reads the three stock economy singletons null-safe. Mirrors the in-game
        /// <c>RuntimeTests.SnapshotFinancials</c> read, but treats NaN / Infinity as a
        /// missing witness (<c>null</c>) so a corrupt singleton never seeds the floor.
        /// </summary>
        internal static EconomySnapshot Capture()
        {
            var snap = new EconomySnapshot();

            if (Funding.Instance != null)
            {
                double f = Funding.Instance.Funds;
                if (IsFinite(f))
                    snap.Funds = f;
            }

            if (ResearchAndDevelopment.Instance != null)
            {
                double s = ResearchAndDevelopment.Instance.Science;
                if (IsFinite(s))
                    snap.Science = s;
            }

            if (global::Reputation.Instance != null)
            {
                float r = global::Reputation.Instance.reputation;
                if (IsFinite(r))
                    snap.Reputation = r;
            }

            return snap;
        }

        private static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }
    }

    /// <summary>
    /// Process-lifetime holder for the rewind read-back divergence guard
    /// (audit recommendation #1, <c>docs/dev/ledger-state-reconstruction-audit.md</c> §8).
    ///
    /// <para>
    /// On a Rewind-to-Separation, <see cref="RewindInvoker.StartInvoke"/> loads a real
    /// KSP RewindPoint quicksave and then runs
    /// <c>LedgerOrchestrator.RecalculateAndPatch(double.MaxValue)</c>, which overwrites
    /// the live career economy with the recalc target via
    /// <see cref="KspStatePatcher.PatchAll"/>. The recalc target is the LIVE-TIP economy
    /// ("career state sticks"), NOT the loaded quicksave (the OLD at-RP) economy, so the
    /// two legitimately differ by post-RP earning / spending.
    /// </para>
    ///
    /// <para>
    /// To catch a silent career-corruption clobber without false-flagging healthy
    /// rewinds, the guard captures TWO independent witnesses of the real career economy:
    /// <see cref="Before"/> (live state captured BEFORE the LoadGame — the pre-rewind
    /// career) and <see cref="Loaded"/> (live state captured right after the load — the
    /// quicksave / OLD at-RP economy). The per-resource floor is <c>min(Before, Loaded)</c>
    /// (finite witnesses only). A recalc target that drops below that floor by more than
    /// a per-resource tolerance is flagged; the upward / restore direction is never
    /// flagged.
    /// </para>
    ///
    /// <para>
    /// <b>Default behavior is warn-only and MUST NOT alter any successful rewind.</b>
    /// Aborting the patch is opt-in via <see cref="AbortRewindPatchOnDivergence"/>
    /// (default OFF).
    /// </para>
    ///
    /// <para>
    /// Runtime-only: the witnesses survive the in-process LoadGame because they are
    /// statics; nothing here is serialized. The guard is armed for the duration of the
    /// post-load recalc and cleared in a <c>finally</c> so it never leaks onto an
    /// ordinary (non-rewind) recalc patch.
    /// </para>
    /// </summary>
    internal static class RewindReadbackGuard
    {
        private const string Tag = "RewindReadback";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>True while a rewind apply boundary has armed the guard.</summary>
        internal static bool Armed;

        /// <summary>Live economy captured BEFORE the rewind LoadGame (pre-rewind career).</summary>
        internal static EconomySnapshot Before;

        /// <summary>Live economy captured AFTER the load (the loaded quicksave / OLD at-RP economy).</summary>
        internal static EconomySnapshot Loaded;

        /// <summary>
        /// Opt-in: when true AND a divergence is flagged, the runner asks
        /// <see cref="KspStatePatcher.PatchAll"/> to abort before writing the economy.
        /// Default OFF — the guard is warn-only out of the box and never alters a
        /// successful rewind.
        /// </summary>
        internal static bool AbortRewindPatchOnDivergence = false;

        /// <summary>
        /// Test seam: forces the abort decision on a flagged divergence without flipping
        /// the production opt-in. Mirrors <c>MapRenderTrace.ForceEnabledForTesting</c>.
        /// </summary>
        internal static bool ForceAbortForTesting;

        /// <summary>
        /// Arms the guard with the two witnesses and logs an Info line with all values.
        /// Call exactly once at the rewind apply boundary, paired with a
        /// <c>finally { Clear(); }</c>.
        /// </summary>
        internal static void Arm(EconomySnapshot before, EconomySnapshot loaded)
        {
            Before = before;
            Loaded = loaded;
            Armed = true;

            ParsekLog.Info(Tag,
                "armed rewind read-back guard: " +
                $"eBeforeFunds={Fmt(before.Funds)} eRpFunds={Fmt(loaded.Funds)} " +
                $"eBeforeScience={Fmt(before.Science)} eRpScience={Fmt(loaded.Science)} " +
                $"eBeforeRep={Fmt(before.Reputation)} eRpRep={Fmt(loaded.Reputation)} " +
                $"abortOnDivergence={AbortRewindPatchOnDivergence}");
        }

        /// <summary>Disarms the guard and drops both witnesses. Idempotent.</summary>
        internal static void Clear()
        {
            Armed = false;
            Before = default(EconomySnapshot);
            Loaded = default(EconomySnapshot);
        }

        /// <summary>Resets all state including the abort opt-in / test seam. Tests only.</summary>
        internal static void ResetForTesting()
        {
            Clear();
            AbortRewindPatchOnDivergence = false;
            ForceAbortForTesting = false;
        }

        private static string Fmt(double? v)
        {
            return v.HasValue ? v.Value.ToString("R", IC) : "none";
        }

        private static string Fmt(float? v)
        {
            return v.HasValue ? ((double)v.Value).ToString("R", IC) : "none";
        }
    }
}
