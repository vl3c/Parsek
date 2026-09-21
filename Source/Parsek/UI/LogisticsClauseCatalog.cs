using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Which half of the Logistics reason vocabulary a clause belongs to.
    /// </summary>
    internal enum LogisticsClauseKind
    {
        /// <summary>
        /// A LIVE route's refusal to dispatch: the detail panel's yellow
        /// "Last cycle blocked:" line, the Status cell's "Held: ..." override
        /// and its tooltip. Produced by <see cref="LogisticsHoldPresentation"/>.
        /// </summary>
        Hold = 0,

        /// <summary>
        /// Why a committed tree is NOT a Supply Run candidate: the near-miss
        /// subsection's reason column and the route-creation dialog's defensive
        /// body. Produced by <see cref="LogisticsRejectPresentation"/> and
        /// <see cref="Parsek.Logistics.RouteCreationFormatters"/>.
        /// </summary>
        Reject = 1
    }

    /// <summary>
    /// One entry of the Logistics reason vocabulary: the constant's own name,
    /// the format string it holds, and which half it belongs to. Enumerable so
    /// a test (and, later, the GUI state gallery's completeness guard,
    /// <c>docs/dev/design-gui-state-gallery.md</c> section 11) can walk the
    /// whole vocabulary mechanically instead of scraping literals out of the
    /// presentation source - an interpolated clause is stored split at its
    /// holes, so a source scrape can only ever be best-effort.
    /// </summary>
    internal struct LogisticsClause
    {
        /// <summary>The declaring constant's field name (e.g. "OriginOutOfResource").</summary>
        internal readonly string Name;

        /// <summary>
        /// The composite format string as the player reads it, holes and all
        /// (e.g. "origin is out of {0} - delivers when the origin has the full amount").
        /// </summary>
        internal readonly string Format;

        /// <summary>Hold or Reject.</summary>
        internal readonly LogisticsClauseKind Kind;

        internal LogisticsClause(string name, string format, LogisticsClauseKind kind)
        {
            Name = name;
            Format = format;
            Kind = kind;
        }
    }

    /// <summary>
    /// The whole Logistics reason vocabulary in one list: every hold clause
    /// (<see cref="LogisticsHoldClauses"/>) followed by every reject clause
    /// (<see cref="LogisticsRejectClauses"/>). Pure data, no Unity, no KSP.
    /// </summary>
    internal static class LogisticsClauseCatalog
    {
        private static readonly IReadOnlyList<LogisticsClause> all = BuildAll();

        /// <summary>Hold clauses then reject clauses, in declaration order.</summary>
        internal static IReadOnlyList<LogisticsClause> All
        {
            get { return all; }
        }

        private static IReadOnlyList<LogisticsClause> BuildAll()
        {
            var list = new List<LogisticsClause>();
            list.AddRange(LogisticsHoldClauses.All);
            list.AddRange(LogisticsRejectClauses.All);
            return list.AsReadOnly();
        }
    }
}
