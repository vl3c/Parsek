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
        internal static bool SuppressLogging;

        private struct RateLimitState
        {
            public double lastEmitSeconds;
            public int suppressedCount;
        }

        private static readonly Dictionary<string, RateLimitState> rateLimitStateByKey =
            new Dictionary<string, RateLimitState>();

        private const double DefaultRateLimitSeconds = 5.0;
        private static readonly DateTime UnixEpochUtc = new DateTime(1970, 1, 1);
        internal static Func<double> ClockOverrideForTesting;
        internal static Action<string> TestSinkForTesting;
        internal static bool? VerboseOverrideForTesting;

        public static bool IsVerboseEnabled =>
            VerboseOverrideForTesting ?? (ParsekSettings.Current?.verboseLogging ?? true);

        internal static void ResetRateLimitsForTesting()
        {
            rateLimitStateByKey.Clear();
        }

        internal static void ResetTestOverrides()
        {
            ClockOverrideForTesting = null;
            TestSinkForTesting = null;
            VerboseOverrideForTesting = null;
            ResetRateLimitsForTesting();
        }

        public static void Log(string message)
        {
            Info("General", message);
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

        private static double GetLogClockSeconds()
        {
            if (ClockOverrideForTesting != null)
                return ClockOverrideForTesting();

            return DateTime.UtcNow.Subtract(UnixEpochUtc).TotalSeconds;
        }

        private static void Write(string level, string subsystem, string message)
        {
            if (SuppressLogging)
                return;

            string safeSubsystem = string.IsNullOrEmpty(subsystem) ? "General" : subsystem;
            string safeMessage = string.IsNullOrEmpty(message) ? "(empty)" : message;
            string line = $"[Parsek][{level}][{safeSubsystem}] {safeMessage}";
            if (TestSinkForTesting != null)
            {
                TestSinkForTesting(line);
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
        }

        public static void ScreenMessage(string message, float duration)
        {
            ScreenMessages.PostScreenMessage(
                $"[Parsek] {message}",
                duration,
                ScreenMessageStyle.UPPER_CENTER);
        }
    }
}
