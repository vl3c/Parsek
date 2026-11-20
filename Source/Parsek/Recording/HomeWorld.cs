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
    /// Hot path: KSC structural dispatch reads it per committed recording per Update and the KSC
    /// pose resolvers per rendered ghost per frame, so a call allocates nothing once resolved.
    /// The name is memoized on the identity of the live home <c>CelestialBody</c>: every call
    /// still asks <c>FlightGlobals.GetHomeBody()</c> (a cached field read, no allocation) and
    /// re-reads the allocating Unity <c>name</c> only when that reference changes. Bodies live
    /// for the whole process (a planet pack builds them once at startup), so loading another
    /// game keeps the entry valid, and a different home body object re-resolves by itself: no
    /// scene or load hook is needed.
    ///
    /// Headless-safe: the live reads sit in NoInlining cores and any failure falls back to the
    /// stock name without caching, so a unit test (or a scene with no body registry) sees
    /// "Kerbin" unless it sets <see cref="HomeBodyNameOverrideForTesting"/>.
    /// </summary>
    internal static class HomeWorld
    {
        internal const string Tag = "HomeWorld";
        internal const string StockHomeBodyName = "Kerbin";

        // Rate-limit keys for the fallback warning, composed once (TryClaimRateLimitSlot keys
        // carry their own prefix and must not collide with ParsekLog's W| / I| spaces).
        private const string FallbackNoHomeBodyKey = "HomeWorld|fallback|no-home-body";
        private const string FallbackReadFailedKey = "HomeWorld|fallback|read-failed";

        internal enum LiveReadFailure
        {
            None,
            Headless,
            NoHomeBody,
            ReadFailed,
        }

        /// <summary>One resolved home body: the live body reference and the name read from it.</summary>
        internal sealed class ResolvedHomeBody
        {
            internal readonly object Body;
            internal readonly string Name;

            internal ResolvedHomeBody(object body, string name)
            {
                Body = body;
                Name = name;
            }
        }

        // The memo. One immutable entry swapped by reference, so a reader never sees a body
        // paired with another body's name.
        private static ResolvedHomeBody resolved;

        // Test seams. [ThreadStatic] so a test that sets one cannot leak a planet-pack home
        // into a test running in parallel on another xUnit thread (the KSC dispatch, pose and
        // route-origin tests read the home world with Kerbin data in every collection). A
        // synchronous xUnit test runs its constructor, body and Dispose on one thread (the
        // ThreadStatic ParsekLog test sink relies on the same), and Parsek starts no threads.
        [ThreadStatic]
        private static string homeBodyNameOverrideForTesting;

        /// <summary>Stands in for the live <c>FlightGlobals.GetHomeBody()</c> read.</summary>
        [ThreadStatic]
        internal static Func<object> LiveHomeBodyReaderForTesting;

        /// <summary>Stands in for the live (allocating) Unity <c>CelestialBody.name</c> read.</summary>
        [ThreadStatic]
        internal static Func<object, string> BodyNameReaderForTesting;

        /// <summary>Test seam: when non-empty, the home world's name without a live read (this thread only).</summary>
        internal static string HomeBodyNameOverrideForTesting
        {
            get => homeBodyNameOverrideForTesting;
            set => homeBodyNameOverrideForTesting = value;
        }

        /// <summary>
        /// The home world's body name: the override when set, else the memoized name of the
        /// live <c>FlightGlobals.GetHomeBody()</c>, else the stock "Kerbin".
        /// </summary>
        internal static string HomeBodyName
        {
            get
            {
                string overrideName = homeBodyNameOverrideForTesting;
                if (!string.IsNullOrEmpty(overrideName))
                    return overrideName;

                object body = TryReadLiveHomeBody(out LiveReadFailure failure, out Exception error);
                if (body != null)
                {
                    ResolvedHomeBody entry = resolved;
                    if (IsResolvedFor(entry, body))
                        return entry.Name;

                    string name = TryReadBodyName(body, out failure, out error);
                    if (!string.IsNullOrEmpty(name))
                    {
                        resolved = new ResolvedHomeBody(body, name);
                        LogResolved(name);
                        return name;
                    }
                }

                LogFallback(failure, error);
                return StockHomeBodyName;
            }
        }

        /// <summary>True when <paramref name="bodyName"/> names the home world (ordinal).</summary>
        internal static bool IsHomeBodyName(string bodyName)
        {
            return !string.IsNullOrEmpty(bodyName)
                && string.Equals(bodyName, HomeBodyName, StringComparison.Ordinal);
        }

        /// <summary>
        /// The memo decision: the cached entry answers only for the very body object it was
        /// read from (reference identity; never a name or Unity's overloaded equality).
        /// </summary>
        internal static bool IsResolvedFor(ResolvedHomeBody entry, object liveBody)
        {
            return entry != null
                && liveBody != null
                && object.ReferenceEquals(entry.Body, liveBody);
        }

        /// <summary>Drops the memo (tests only; production re-resolves on a body change by itself).</summary>
        internal static void ResetResolvedForTesting()
        {
            resolved = null;
        }

        private static void LogResolved(string name)
        {
            if (string.Equals(name, StockHomeBodyName, StringComparison.Ordinal))
            {
                ParsekLog.Verbose(Tag, $"home world resolved: '{name}' (stock)");
                return;
            }

            ParsekLog.Info(Tag,
                $"home world resolved: '{name}', not stock '{StockHomeBodyName}': " +
                "KSC ghosts and Space Center routes key on it");
        }

        private static void LogFallback(LiveReadFailure failure, Exception error)
        {
            // Headless (unit tests, no body registry) is the expected fallback and stays
            // silent: pure callers such as ParsekKSC.ShouldShowInKSC must not log.
            if (failure == LiveReadFailure.Headless)
                return;

            string key = failure == LiveReadFailure.ReadFailed
                ? FallbackReadFailedKey
                : FallbackNoHomeBodyKey;
            // Claim the slot before building the message, so a per-frame caller that keeps
            // failing allocates nothing between the emitted lines.
            if (!ParsekLog.TryClaimRateLimitSlot(key, ParsekLog.DefaultRateLimitSeconds, out int suppressed))
                return;

            string reason = failure == LiveReadFailure.ReadFailed
                ? "read-failed:" + (error != null ? error.GetType().Name : "unknown")
                : "no-home-body";
            ParsekLog.Warn(Tag,
                $"home world unavailable ({reason}); using stock '{StockHomeBodyName}'" +
                (suppressed > 0 ? $" | suppressed={suppressed}" : string.Empty));
        }

        private static object TryReadLiveHomeBody(out LiveReadFailure failure, out Exception error)
        {
            failure = LiveReadFailure.None;
            error = null;
            try
            {
                Func<object> reader = LiveHomeBodyReaderForTesting;
                object body = reader != null ? reader() : ReadLiveHomeBodyCore();
                if (body == null)
                    failure = LiveReadFailure.NoHomeBody;
                return body;
            }
            catch (Exception ex)
            {
                failure = IsHeadlessFailure(ex) ? LiveReadFailure.Headless : LiveReadFailure.ReadFailed;
                error = ex;
                return null;
            }
        }

        private static string TryReadBodyName(object body, out LiveReadFailure failure, out Exception error)
        {
            failure = LiveReadFailure.None;
            error = null;
            try
            {
                Func<object, string> reader = BodyNameReaderForTesting;
                string name = reader != null ? reader(body) : ReadBodyNameCore(body);
                if (string.IsNullOrEmpty(name))
                    failure = LiveReadFailure.NoHomeBody;
                return name;
            }
            catch (Exception ex)
            {
                failure = IsHeadlessFailure(ex) ? LiveReadFailure.Headless : LiveReadFailure.ReadFailed;
                error = ex;
                return null;
            }
        }

        // Keeps the FlightGlobals read (and its static initializer, which fails headless) out of
        // every method a test calls. Returns the body as object so no caller names the type.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object ReadLiveHomeBodyCore()
        {
            CelestialBody home = FlightGlobals.GetHomeBody();
            return object.ReferenceEquals(home, null) ? null : home;
        }

        // Unity's name getter is a native call that allocates a new string: read once per body.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ReadBodyNameCore(object body)
        {
            return ((CelestialBody)body).name;
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
