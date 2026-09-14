using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Shared logging utilities for the Parsek mod.
    /// </summary>
    public static class ParsekLog
    {
        // When true, suppresses Debug.Log calls (for unit testing outside Unity)
        [ThreadStatic]
        internal static bool SuppressLogging;

        private struct RateLimitState
        {
            public double lastEmitSeconds;
            public int suppressedCount;
        }

        private struct OnChangeState
        {
            public string lastKey;
            public int suppressedCount;
        }

        // ThreadStatic so each xUnit thread gets its own rate-limit state.
        // Backing field is null on non-initial threads; property lazy-creates.
        [ThreadStatic]
        private static Dictionary<string, RateLimitState> t_rateLimitStateByKey;
        private static Dictionary<string, RateLimitState> rateLimitStateByKey =>
            t_rateLimitStateByKey ?? (t_rateLimitStateByKey = new Dictionary<string, RateLimitState>());

        // Tracks last-emitted key per stable identity for VerboseOnChange. The
        // dictionary is keyed by a caller-chosen "identity" (e.g., "GhostMap|<recId>")
        // and stores the most recent decision key plus a suppressed counter. A new
        // emission fires only when the decision key flips; otherwise the suppressed
        // counter is bumped and surfaced via "| suppressed=N" on the next change.
        [ThreadStatic]
        private static Dictionary<string, OnChangeState> t_onChangeStateByIdentity;
        private static Dictionary<string, OnChangeState> onChangeStateByIdentity =>
            t_onChangeStateByIdentity ?? (t_onChangeStateByIdentity = new Dictionary<string, OnChangeState>());

        // internal so satellite log helpers that share the one rate-limit
        // dictionary (see TryClaimRateLimitSlot) can use the same default.
        internal const double DefaultRateLimitSeconds = 5.0;
        private static readonly DateTime UnixEpochUtc = new DateTime(1970, 1, 1);
        [ThreadStatic]
        internal static Func<double> ClockOverrideForTesting;
        [ThreadStatic]
        private static Action<string> t_testSinkForTesting;

        // Test-only override: receives the rendered line and suppresses Debug.Log.
        // Installing a sink means "capture this class's log", so it also clears
        // SuppressLogging: hundreds of Sequential test classes end Dispose() by
        // re-suppressing the global flag, and without this the NEXT class's capture
        // came back empty whenever xUnit happened to order it right after one of them.
        // The class's own later SuppressLogging = true (or a SuppressScope) still wins.
        // Restoring a saved non-null sink clears the flag too, so a save/restore pair
        // must restore the sink BEFORE the flag.
        internal static Action<string> TestSinkForTesting
        {
            get => t_testSinkForTesting;
            set
            {
                t_testSinkForTesting = value;
                if (value != null)
                    SuppressLogging = false;
            }
        }
        [ThreadStatic]
        // Test-only observer: receives the rendered line but still lets Debug.Log run.
        internal static Action<string> TestObserverForTesting;
        [ThreadStatic]
        internal static bool? VerboseOverrideForTesting;

        // Test seam: when set, ScreenMessage routes through this callback
        // instead of Unity's ScreenMessages.PostScreenMessage so unit tests
        // can assert on user-facing toasts without a live Unity canvas.
        [ThreadStatic]
        internal static Action<string, float> ScreenMessageSinkForTesting;

        // Production wiring for the verbose gate, installed by the settings type
        // rather than read from it: this class must reference nothing else in the
        // assembly so the most-referenced type in the tree stays a dependency leaf.
        // Null until the settings type is first touched, and a null provider means
        // verbose ON - the same answer the old "no settings object yet" branch gave.
        // NOT a test override: ResetTestOverrides must not clear it, or every test
        // running after the first reset would lose the installed production gate.
        internal static Func<bool> VerboseProvider;

        public static bool IsVerboseEnabled =>
            VerboseOverrideForTesting ?? (VerboseProvider?.Invoke() ?? true);

        // Test-reset callbacks published by satellite log helpers (RecorderStateLog)
        // whose own sequence/transition state must clear with this class's overrides.
        // Registered from the helper's static constructor, so a helper no test ever
        // touched contributes nothing here. Keeping the callback indirect is what lets
        // this class stay free of references to the types those helpers format.
        private static Action satelliteTestResets;

        internal static void RegisterTestResetHook(Action reset)
        {
            if (reset == null)
                return;

            satelliteTestResets += reset;
        }

        internal static void ResetRateLimitsForTesting()
        {
            rateLimitStateByKey.Clear();
            onChangeStateByIdentity.Clear();
        }

        internal static void ResetTestOverrides()
        {
            SuppressLogging = false;
            ClockOverrideForTesting = null;
            TestSinkForTesting = null;
            TestObserverForTesting = null;
            VerboseOverrideForTesting = null;
            ScreenMessageSinkForTesting = null;
            ResetRateLimitsForTesting();
            satelliteTestResets?.Invoke();
        }

        internal static IDisposable SuppressScope()
        {
            return new SuppressLoggingScope(SuppressLogging);
        }

        private sealed class SuppressLoggingScope : IDisposable
        {
            private readonly bool previous;
            private bool disposed;

            internal SuppressLoggingScope(bool previous)
            {
                this.previous = previous;
                SuppressLogging = true;
            }

            public void Dispose()
            {
                if (disposed)
                    return;

                SuppressLogging = previous;
                disposed = true;
            }
        }

        public static void Info(string subsystem, string message)
        {
            Write("INFO", subsystem, message);
        }

        public static void Verbose(string subsystem, string message)
        {
            if (!IsVerboseEnabled) return;
            Write("VERBOSE", subsystem, message);
        }

        public static void Warn(string subsystem, string message)
        {
            Write("WARN", subsystem, message);
        }

        public static void Error(string subsystem, string message)
        {
            Write("ERROR", subsystem, message);
        }

        public static void VerboseRateLimited(
            string subsystem,
            string key,
            string message,
            double minIntervalSeconds = DefaultRateLimitSeconds)
        {
            if (!IsVerboseEnabled)
                return;

            if (string.IsNullOrEmpty(key))
            {
                Verbose(subsystem, message);
                return;
            }

            string compositeKey = $"{subsystem}|{key}";
            double now = GetLogClockSeconds();
            if (!rateLimitStateByKey.TryGetValue(compositeKey, out var state))
            {
                rateLimitStateByKey[compositeKey] = new RateLimitState
                {
                    lastEmitSeconds = now,
                    suppressedCount = 0
                };
                Verbose(subsystem, message);
                return;
            }

            if ((now - state.lastEmitSeconds) >= minIntervalSeconds)
            {
                string suffix = state.suppressedCount > 0
                    ? $" | suppressed={state.suppressedCount}"
                    : string.Empty;
                Verbose(subsystem, $"{message}{suffix}");
                state.lastEmitSeconds = now;
                state.suppressedCount = 0;
            }
            else
            {
                state.suppressedCount++;
            }

            rateLimitStateByKey[compositeKey] = state;
        }

        public static void VerboseRateLimited(
            string subsystem,
            string key,
            Func<string> messageFactory,
            double minIntervalSeconds = DefaultRateLimitSeconds)
        {
            if (!IsVerboseEnabled)
                return;

            if (messageFactory == null)
                return;

            if (string.IsNullOrEmpty(key))
            {
                Verbose(subsystem, messageFactory());
                return;
            }

            string compositeKey = $"{subsystem}|{key}";
            double now = GetLogClockSeconds();
            if (!rateLimitStateByKey.TryGetValue(compositeKey, out var state))
            {
                rateLimitStateByKey[compositeKey] = new RateLimitState
                {
                    lastEmitSeconds = now,
                    suppressedCount = 0
                };
                Verbose(subsystem, messageFactory());
                return;
            }

            if ((now - state.lastEmitSeconds) >= minIntervalSeconds)
            {
                string message = messageFactory();
                string suffix = state.suppressedCount > 0
                    ? $" | suppressed={state.suppressedCount}"
                    : string.Empty;
                Verbose(subsystem, $"{message}{suffix}");
                state.lastEmitSeconds = now;
                state.suppressedCount = 0;
            }
            else
            {
                state.suppressedCount++;
            }

            rateLimitStateByKey[compositeKey] = state;
        }

        /// <summary>
        /// State-change-driven verbose log. Emits <paramref name="message"/> only when
        /// <paramref name="stateKey"/> differs from the last emitted state for the
        /// given <paramref name="identity"/>. Stable per-frame repeats with the same
        /// <paramref name="stateKey"/> are coalesced into a suppressed counter that
        /// is surfaced as <c>| suppressed=N</c> on the next change emission.
        /// </summary>
        /// <remarks>
        /// Use when the line should fire on every decision flip (None to Segment, etc.)
        /// but stay completely silent across stable per-frame repeats — the time-based
        /// re-emission of <see cref="VerboseRateLimited"/> would still spam the log
        /// for long-stable states (e.g., a recording stuck in pending for the entire
        /// session). The <paramref name="identity"/> is the stable scope to track
        /// (typically "<![CDATA[<subsystem>|<recId>]]>") and <paramref name="stateKey"/>
        /// encodes the decision tuple whose changes you care about.
        /// </remarks>
        public static void VerboseOnChange(
            string subsystem,
            string identity,
            string stateKey,
            string message)
        {
            if (!IsVerboseEnabled)
                return;

            if (string.IsNullOrEmpty(identity))
            {
                Verbose(subsystem, message);
                return;
            }

            string compositeIdentity = $"{subsystem}|{identity}";
            string normalizedKey = stateKey ?? string.Empty;
            if (!onChangeStateByIdentity.TryGetValue(compositeIdentity, out var state))
            {
                onChangeStateByIdentity[compositeIdentity] = new OnChangeState
                {
                    lastKey = normalizedKey,
                    suppressedCount = 0
                };
                Verbose(subsystem, message);
                return;
            }

            if (state.lastKey != normalizedKey)
            {
                string suffix = state.suppressedCount > 0
                    ? $" | suppressed={state.suppressedCount}"
                    : string.Empty;
                Verbose(subsystem, $"{message}{suffix}");
                state.lastKey = normalizedKey;
                state.suppressedCount = 0;
            }
            else
            {
                state.suppressedCount++;
            }

            onChangeStateByIdentity[compositeIdentity] = state;
        }

        /// <summary>
        /// Lazy <see cref="VerboseOnChange(string,string,string,string)"/>: the message is built only when
        /// the state actually changes (or first-seen), so a per-frame caller pays only the cheap
        /// <paramref name="stateKey"/> on stable frames - no per-frame string/collection allocation for a
        /// line that almost never emits. Use when the message is expensive to format but the change key is
        /// cheap.
        /// </summary>
        public static void VerboseOnChange(
            string subsystem,
            string identity,
            string stateKey,
            Func<string> messageFactory)
        {
            if (!IsVerboseEnabled)
                return;

            if (messageFactory == null)
                return;

            if (string.IsNullOrEmpty(identity))
            {
                Verbose(subsystem, messageFactory());
                return;
            }

            string compositeIdentity = $"{subsystem}|{identity}";
            string normalizedKey = stateKey ?? string.Empty;
            if (!onChangeStateByIdentity.TryGetValue(compositeIdentity, out var state))
            {
                onChangeStateByIdentity[compositeIdentity] = new OnChangeState
                {
                    lastKey = normalizedKey,
                    suppressedCount = 0
                };
                Verbose(subsystem, messageFactory());
                return;
            }

            if (state.lastKey != normalizedKey)
            {
                string suffix = state.suppressedCount > 0
                    ? $" | suppressed={state.suppressedCount}"
                    : string.Empty;
                Verbose(subsystem, $"{messageFactory()}{suffix}");
                state.lastKey = normalizedKey;
                state.suppressedCount = 0;
            }
            else
            {
                state.suppressedCount++;
            }

            onChangeStateByIdentity[compositeIdentity] = state;
        }

        internal static int ClearVerboseOnChangeIdentitiesWithPrefix(
            string subsystem,
            string identityPrefix)
        {
            if (string.IsNullOrEmpty(subsystem) || string.IsNullOrEmpty(identityPrefix))
                return 0;

            string compositePrefix = $"{subsystem}|{identityPrefix}";
            List<string> keysToRemove = null;
            foreach (string key in onChangeStateByIdentity.Keys)
            {
                if (key.StartsWith(compositePrefix, StringComparison.Ordinal))
                {
                    if (keysToRemove == null)
                        keysToRemove = new List<string>();
                    keysToRemove.Add(key);
                }
            }

            if (keysToRemove == null)
                return 0;

            for (int i = 0; i < keysToRemove.Count; i++)
                onChangeStateByIdentity.Remove(keysToRemove[i]);

            return keysToRemove.Count;
        }

        /// <summary>
        /// Rate-limited warning. Same throttling as VerboseRateLimited but emits at WARN level
        /// unconditionally (not gated on IsVerboseEnabled). Used for budget threshold warnings
        /// that should be visible even when verbose logging is disabled.
        /// </summary>
        public static void WarnRateLimited(
            string subsystem,
            string key,
            string message,
            double minIntervalSeconds = DefaultRateLimitSeconds)
        {
            if (string.IsNullOrEmpty(key))
            {
                Warn(subsystem, message);
                return;
            }

            string compositeKey = $"W|{subsystem}|{key}";
            double now = GetLogClockSeconds();
            if (!rateLimitStateByKey.TryGetValue(compositeKey, out var state))
            {
                rateLimitStateByKey[compositeKey] = new RateLimitState
                {
                    lastEmitSeconds = now,
                    suppressedCount = 0
                };
                Warn(subsystem, message);
                return;
            }

            if ((now - state.lastEmitSeconds) >= minIntervalSeconds)
            {
                string suffix = state.suppressedCount > 0
                    ? $" | suppressed={state.suppressedCount}"
                    : string.Empty;
                Warn(subsystem, $"{message}{suffix}");
                state.lastEmitSeconds = now;
                state.suppressedCount = 0;
            }
            else
            {
                state.suppressedCount++;
            }

            rateLimitStateByKey[compositeKey] = state;
        }

        /// <summary>
        /// Rate-limited info. Same throttling as <see cref="WarnRateLimited"/> but
        /// emits at INFO level, and like it is NOT gated on IsVerboseEnabled. For a
        /// STANDING condition a player needs to see in the log but whose producer
        /// runs on a poll - the route-analysis refusal reasons are the case it was
        /// added for: the Logistics window re-analyses at about 1 Hz while it is
        /// open, so an unthrottled Info would bury the log, and a Warn would put an
        /// ordinary designed refusal on the WRN surface the log validator reads.
        /// </summary>
        public static void InfoRateLimited(
            string subsystem,
            string key,
            string message,
            double minIntervalSeconds = DefaultRateLimitSeconds)
        {
            if (string.IsNullOrEmpty(key))
            {
                Info(subsystem, message);
                return;
            }

            string compositeKey = $"I|{subsystem}|{key}";
            double now = GetLogClockSeconds();
            if (!rateLimitStateByKey.TryGetValue(compositeKey, out var state))
            {
                rateLimitStateByKey[compositeKey] = new RateLimitState
                {
                    lastEmitSeconds = now,
                    suppressedCount = 0
                };
                Info(subsystem, message);
                return;
            }

            if ((now - state.lastEmitSeconds) >= minIntervalSeconds)
            {
                string suffix = state.suppressedCount > 0
                    ? $" | suppressed={state.suppressedCount}"
                    : string.Empty;
                Info(subsystem, $"{message}{suffix}");
                state.lastEmitSeconds = now;
                state.suppressedCount = 0;
            }
            else
            {
                state.suppressedCount++;
            }

            rateLimitStateByKey[compositeKey] = state;
        }

        /// <summary>
        /// Shared rate-limit gate for satellite log helpers (RecorderStateLog) that
        /// must throttle against the SAME per-session dictionary as the loggers here -
        /// a second dictionary would let two helpers using one key both emit.
        /// Returns true when the caller may emit now, with
        /// <paramref name="suppressedCount"/> carrying the emissions swallowed since
        /// the last one (0 on the first claim for a key) so the caller can render the
        /// usual "| suppressed=N" suffix. Returns false when still inside the interval,
        /// having bumped that count.
        /// </summary>
        /// <param name="compositeKey">
        /// Already-composed key including the caller's own prefix, so satellite keys
        /// cannot collide with the "W|" / "I|" spaces used above.
        /// </param>
        internal static bool TryClaimRateLimitSlot(
            string compositeKey,
            double minIntervalSeconds,
            out int suppressedCount)
        {
            suppressedCount = 0;
            double now = GetLogClockSeconds();
            if (!rateLimitStateByKey.TryGetValue(compositeKey, out var state))
            {
                rateLimitStateByKey[compositeKey] = new RateLimitState
                {
                    lastEmitSeconds = now,
                    suppressedCount = 0
                };
                return true;
            }

            bool emit = (now - state.lastEmitSeconds) >= minIntervalSeconds;
            if (emit)
            {
                suppressedCount = state.suppressedCount;
                state.lastEmitSeconds = now;
                state.suppressedCount = 0;
            }
            else
            {
                state.suppressedCount++;
            }

            rateLimitStateByKey[compositeKey] = state;
            return emit;
        }

        private static double GetLogClockSeconds()
        {
            if (ClockOverrideForTesting != null)
                return ClockOverrideForTesting();

            return DateTime.UtcNow.Subtract(UnixEpochUtc).TotalSeconds;
        }

        // internal (not private) so satellite log helpers render through the one
        // "[Parsek][LEVEL][Subsystem]" formatter and honour the same sink / suppress
        // seams instead of writing their own Debug.Log line.
        internal static void Write(string level, string subsystem, string message)
        {
            if (SuppressLogging)
                return;

            string safeSubsystem = string.IsNullOrEmpty(subsystem) ? "General" : subsystem;
            string safeMessage = string.IsNullOrEmpty(message) ? "(empty)" : message;
            string line = $"[Parsek][{level}][{safeSubsystem}] {safeMessage}";
            TestObserverForTesting?.Invoke(line);
            var sink = TestSinkForTesting;
            if (sink != null)
            {
                sink(line);
                return;
            }

            try
            {
                Debug.Log(line);
            }
            catch (System.Security.SecurityException)
            {
                // Unit-test runtime can throw when Unity internals are unavailable.
            }
            catch (MethodAccessException)
            {
                // Same fallback for some non-Unity execution environments.
            }
            catch (MissingMethodException)
            {
                // Mono (Linux test runs) surfaces Unity's unresolvable Internal_Log
                // native call as MissingMethodException instead of the above two.
            }
        }

        public static void ScreenMessage(string message, float duration)
        {
            var sink = ScreenMessageSinkForTesting;
            if (sink != null)
            {
                sink(message ?? string.Empty, duration);
                return;
            }
            // Same ECall guards as Write's Debug.Log: callers now include the
            // orchestrator's background route tick, which must never be unwound
            // by an unresolvable Unity ScreenMessages call (headless / mono / a
            // test that reached a toast path without installing the sink).
            try
            {
                ScreenMessages.PostScreenMessage(
                    $"[Parsek] {message}",
                    duration,
                    ScreenMessageStyle.UPPER_CENTER);
            }
            catch (System.Security.SecurityException)
            {
                // Unit-test runtime can throw when Unity internals are unavailable.
            }
            catch (MethodAccessException)
            {
                // Same fallback for some non-Unity execution environments.
            }
            catch (MissingMethodException)
            {
                // Mono (Linux test runs) surfaces unresolvable Unity native
                // calls as MissingMethodException instead of the above two.
            }
        }
    }
}
