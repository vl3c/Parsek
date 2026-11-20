using System;
using System.Runtime.CompilerServices;

namespace Parsek
{
    /// <summary>
    /// The name of the game's home world: the body the Space Center sits on. Stock calls it
    /// "Kerbin", but a planet pack (Kopernicus: GPP, JNSQ's own home, RSS's Earth) marks another
    /// body <c>isHomeWorld</c>. A check that means "is this the Space Center's planet" reads
    /// <see cref="IsHomeBodyName"/>, never the literal "Kerbin".
    ///
    /// Headless-safe: the live read sits in a NoInlining core and any failure falls back to the
    /// stock name, so a unit test (or a scene with no body registry) sees "Kerbin" unless it sets
    /// <see cref="HomeBodyNameOverrideForTesting"/>.
    /// </summary>
    internal static class HomeWorld
    {
        internal const string Tag = "HomeWorld";
        internal const string StockHomeBodyName = "Kerbin";
        internal const string HeadlessFailure = "headless";

        /// <summary>Test seam: when non-empty, the home world's name without a live read.</summary>
        internal static string HomeBodyNameOverrideForTesting;

        /// <summary>
        /// The home world's body name: the override when set, else the live
        /// <c>FlightGlobals.GetHomeBody()</c>, else the stock "Kerbin".
        /// </summary>
        internal static string HomeBodyName
        {
            get
            {
                string overrideName = HomeBodyNameOverrideForTesting;
                if (!string.IsNullOrEmpty(overrideName))
                    return overrideName;

                string live = TryReadLiveHomeBodyName(out string failure);
                if (!string.IsNullOrEmpty(live))
                {
                    if (!string.Equals(live, StockHomeBodyName, StringComparison.Ordinal))
                        ParsekLog.VerboseRateLimited(Tag, "home-world-" + live,
                            $"home world is '{live}', not stock '{StockHomeBodyName}': " +
                            "KSC ghosts and Space Center routes key on it");
                    return live;
                }

                // Headless (unit tests, no body registry) is the expected fallback and stays
                // silent: pure callers such as ParsekKSC.ShouldShowInKSC must not log.
                if (failure != HeadlessFailure)
                    ParsekLog.WarnRateLimited(Tag, "home-world-fallback-" + failure,
                        $"home world unavailable ({failure}); using stock '{StockHomeBodyName}'");
                return StockHomeBodyName;
            }
        }

        /// <summary>True when <paramref name="bodyName"/> names the home world (ordinal).</summary>
        internal static bool IsHomeBodyName(string bodyName)
        {
            return !string.IsNullOrEmpty(bodyName)
                && string.Equals(bodyName, HomeBodyName, StringComparison.Ordinal);
        }

        private static string TryReadLiveHomeBodyName(out string failure)
        {
            failure = null;
            try
            {
                string name = ReadLiveHomeBodyNameCore();
                if (string.IsNullOrEmpty(name))
                    failure = "no-home-body";
                return name;
            }
            catch (Exception ex)
            {
                failure = IsHeadlessFailure(ex) ? HeadlessFailure : "read-failed:" + ex.GetType().Name;
                return null;
            }
        }

        // Keeps the FlightGlobals read (and its static initializer, which fails headless) out of
        // every method a test calls.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ReadLiveHomeBodyNameCore()
        {
            CelestialBody home = FlightGlobals.GetHomeBody();
            return object.ReferenceEquals(home, null) ? null : home.name;
        }

        private static bool IsHeadlessFailure(Exception ex)
        {
            for (Exception current = ex; current != null; current = current.InnerException)
            {
                if (current is System.Security.SecurityException
                    || current is MissingMethodException
                    || current is MethodAccessException
                    || current is TypeInitializationException)
                    return true;
            }
            return false;
        }
    }
}
