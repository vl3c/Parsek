namespace Parsek.Reaim
{
    /// <summary>
    /// Feature flag for the re-aim whole-chain synthesis fix (reaim-fix-plan.md, option 3). When ON the
    /// re-aim playback path synthesizes a continuous patched-conic chain (escape hyperbola + heliocentric
    /// transfer + capture hyperbola) so the transfer LINE forms a real encounter into the target SOI; when
    /// OFF it keeps today's single-leg heliocentric replacement (a single honest kink at each SOI seam).
    ///
    /// <para>DEFAULT OFF. With the flag off, runtime behavior is byte-identical to today: the resolver
    /// routes through <see cref="ReaimSegmentAssembler.AssembleWindowChain"/> with <c>useChain=false</c>,
    /// which returns exactly <see cref="ReaimSegmentAssembler.ReplaceHeliocentricLeg"/>'s output by value.
    /// Revert is one line (flip the default).</para>
    ///
    /// <para>Mirrors the <see cref="MapRenderTrace"/> / <c>LedgerTrace</c> flag pattern: a
    /// settings-backed bool (<c>ParsekSettings.reaimChainSynthesis</c>, default false) read through
    /// <see cref="IsEnabled"/>, plus a nullable <see cref="ForceEnabledForTesting"/> override for xUnit.
    /// The override is a process-wide mutable static, so every test that sets it MUST run in the
    /// <c>Sequential</c> collection and reset it in a try/finally or Dispose.</para>
    /// </summary>
    internal static class ReaimChainSynthesis
    {
        /// <summary>
        /// Test-only override for <see cref="IsEnabled"/>. <c>null</c> = follow the
        /// <c>reaimChainSynthesis</c> setting (production); non-null = force on/off for a test. Reset to
        /// <c>null</c> in test teardown (try/finally or Dispose). Production code must not set this.
        /// </summary>
        internal static bool? ForceEnabledForTesting;

        /// <summary>
        /// True when the re-aim chain-synthesis path should be used. Follows
        /// <see cref="ForceEnabledForTesting"/> when set, otherwise the <c>reaimChainSynthesis</c>
        /// setting (default false). When false the re-aim playback path is byte-identical to today.
        /// </summary>
        internal static bool IsEnabled =>
            ForceEnabledForTesting
            ?? (ParsekSettings.Current != null && ParsekSettings.Current.reaimChainSynthesis);

        /// <summary>Test-only: clears the override so subsequent reads follow the setting.</summary>
        internal static void Reset()
        {
            ForceEnabledForTesting = null;
        }
    }
}
