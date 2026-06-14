namespace Parsek.Logistics
{
    /// <summary>
    /// Shared owner for the short-id truncation used in <see cref="Route"/> log
    /// lines across the Logistics subsystem. The per-file <c>ShortId</c> /
    /// <c>ShortIdForLog</c> / <c>ShortIdForRoute</c> wrappers all delegate here so
    /// the truncation algorithm has a single definition.
    /// </summary>
    internal static class RouteIds
    {
        internal static string Short(string id)
        {
            if (string.IsNullOrEmpty(id)) return "<no-id>";
            return id.Length > 8 ? id.Substring(0, 8) : id;
        }

        internal static string Short(Route route)
        {
            return Short(route != null ? route.Id : null);
        }
    }
}
