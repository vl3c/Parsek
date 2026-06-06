namespace Parsek
{
    /// <summary>
    /// Pure presentation helper for the Logistics window detail-panel route rename
    /// (M2). The detail panel renders a deferred-commit TextField; on commit (Enter
    /// or focus-loss) the window calls <see cref="ComputeRouteRename"/> to decide
    /// whether the typed text is a real change, trimming whitespace and rejecting
    /// empty / unchanged input. Keeping the trim + empty + unchanged decision pure
    /// (mirrors <see cref="LogisticsCreatePresentation"/> and
    /// <see cref="LogisticsDeliveryPresentation"/>) makes it unit-testable off the
    /// IMGUI path; the window owns the actual <see cref="Route.Name"/> write and the
    /// log line. Unity-free and side-effect-free.
    /// </summary>
    internal static class LogisticsRenamePresentation
    {
        /// <summary>
        /// Decides whether a typed route name is a committable change against the
        /// current name. Trims leading / trailing whitespace from
        /// <paramref name="typed"/> (a null typed value is treated as empty), then
        /// reports <c>changed = true</c> with the trimmed value ONLY when the trimmed
        /// text is non-empty AND differs from <paramref name="current"/>. Returns
        /// <c>changed = false</c> (and <paramref name="committed"/> = the trimmed
        /// text) for empty / whitespace-only input (the empty-name guard: a blank
        /// name must never overwrite the route's name) and for a same-as-current
        /// value after trimming (a no-op edit). The caller writes
        /// <see cref="Route.Name"/> = <paramref name="committed"/> and logs only when
        /// <c>changed</c> is true.
        /// </summary>
        /// <param name="current">The route's current name (may be null).</param>
        /// <param name="typed">The raw text typed into the rename field (may be null).</param>
        /// <param name="committed">The trimmed candidate name.</param>
        /// <returns><c>true</c> when the trimmed name is a non-empty change worth
        /// writing; <c>false</c> on an empty / whitespace / unchanged input.</returns>
        internal static bool ComputeRouteRename(string current, string typed, out string committed)
        {
            committed = (typed ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(committed))
                return false;
            return !string.Equals(committed, current, System.StringComparison.Ordinal);
        }
    }
}
