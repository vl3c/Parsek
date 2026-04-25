using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
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

        // [RecState] sequence counter — incremented on every emission so log
        // readers can spot dropped lines and sort by emission order even when
        // multiple snapshots fire in the same tick.
        private static long s_recStateSeq;

        // Last-seen activeRecId, used to populate the "rec.prev" field only on
        // transitions. Reset for tests via ResetTestOverrides.
        // [ThreadStatic] is correct for KSP's main-thread-only reality and for
        // xUnit's per-thread test isolation. Any future caller from a background
        // thread (e.g., a Harmony prefix on an async path) would silently start
        // its own transition cache and miss cross-thread transitions — if that
        // ever happens, convert to a locked shared field.
        [ThreadStatic]
        private static string t_lastSeenActiveRecId;

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

        private const double DefaultRateLimitSeconds = 5.0;
        private static readonly DateTime UnixEpochUtc = new DateTime(1970, 1, 1);
        [ThreadStatic]
        internal static Func<double> ClockOverrideForTesting;
        [ThreadStatic]
        // Test-only override: receives the rendered line and suppresses Debug.Log.
        internal static Action<string> TestSinkForTesting;
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

        public static bool IsVerboseEnabled =>
            VerboseOverrideForTesting ?? (ParsekSettings.Current?.verboseLogging ?? true);

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
            ResetRecStateForTesting();
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

        /// <summary>
        /// Resets the [RecState] sequence counter and last-seen-recId cache.
        /// Tests call this before asserting on emitted lines so sequence numbers
        /// and the rec.prev transition cache start from a known baseline.
        /// </summary>
        internal static void ResetRecStateForTesting()
        {
            Interlocked.Exchange(ref s_recStateSeq, 0);
            t_lastSeenActiveRecId = null;
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
            TestObserverForTesting?.Invoke(line);
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
            var sink = ScreenMessageSinkForTesting;
            if (sink != null)
            {
                sink(message ?? string.Empty, duration);
                return;
            }
            ScreenMessages.PostScreenMessage(
                $"[Parsek] {message}",
                duration,
                ScreenMessageStyle.UPPER_CENTER);
        }

        // ----- [RecState] structured state-dump logging -----

        /// <summary>
        /// Emits a single deterministic <c>[RecState]</c> log line summarising
        /// every recorder-relevant field at the given lifecycle <paramref name="phase"/>.
        /// Format is field-ordered and stable so log readers can <c>grep "[RecState]"</c>
        /// and either eyeball or <c>cut -d ' '</c>-parse the output.
        /// </summary>
        /// <param name="phase">
        /// Short free-text tag identifying the call site (e.g. <c>"OnFlightReady"</c>,
        /// <c>"OnSave:pre"</c>). Always pass a string literal so the
        /// tag is grep-stable across releases.
        /// </param>
        /// <param name="snap">Captured state to render.</param>
        internal static void RecState(string phase, RecorderStateSnapshot snap)
        {
            long seq = Interlocked.Increment(ref s_recStateSeq);
            string line = FormatRecState(seq, phase, snap, ref t_lastSeenActiveRecId);
            Write("INFO", "RecState", line);
        }

        /// <summary>
        /// Rate-limited variant for hot-path recovery diagnostics that still need
        /// full <c>[RecState]</c> snapshots on the first occurrence and on summary
        /// cadence. Normal lifecycle boundaries should call <see cref="RecState"/>.
        /// </summary>
        /// <remarks>
        /// Like the other rate-limited loggers, summaries are emitted only when the
        /// same key fires again after the interval; trailing suppressed counts for
        /// abandoned fingerprints are intentionally dropped. Keys live in the shared
        /// per-session rate-limit dictionary until reset, so callers should use
        /// coarse, stable fingerprints rather than per-frame values.
        /// </remarks>
        internal static void RecStateRateLimited(
            string phase,
            RecorderStateSnapshot snap,
            string key,
            double minIntervalSeconds = DefaultRateLimitSeconds)
        {
            if (string.IsNullOrEmpty(key))
            {
                RecState(phase, snap);
                return;
            }

            string compositeKey = $"R|RecState|{phase}|{key}";
            double now = GetLogClockSeconds();
            if (!rateLimitStateByKey.TryGetValue(compositeKey, out var state))
            {
                rateLimitStateByKey[compositeKey] = new RateLimitState
                {
                    lastEmitSeconds = now,
                    suppressedCount = 0
                };
                RecState(phase, snap);
                return;
            }

            if ((now - state.lastEmitSeconds) >= minIntervalSeconds)
            {
                long seq = Interlocked.Increment(ref s_recStateSeq);
                string line = FormatRecState(seq, phase, snap, ref t_lastSeenActiveRecId);
                string suffix = state.suppressedCount > 0
                    ? $" | suppressed={state.suppressedCount}"
                    : string.Empty;
                Write("INFO", "RecState", $"{line}{suffix}");
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
        /// Pure formatting helper exposed for unit tests. The <paramref name="lastSeenRecId"/>
        /// ref parameter is updated to the current snapshot's activeRecId after rendering,
        /// implementing the "only show <c>rec.prev</c> on transitions" semantics.
        /// </summary>
        internal static string FormatRecState(
            long seq,
            string phase,
            RecorderStateSnapshot snap,
            ref string lastSeenRecId)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(256);

            sb.Append("[#").Append(seq.ToString(inv)).Append("][")
              .Append(phase ?? "-").Append("] ");

            sb.Append("mode=").Append(FormatMode(snap.mode));

            sb.Append(" tree=");
            if (snap.mode == RecorderMode.Tree)
                sb.Append(TruncateId(snap.treeId)).Append('|').Append(TruncateName(snap.treeName));
            else
                sb.Append('-');

            sb.Append(" rec=");
            if (snap.activeRecId != null || !string.IsNullOrEmpty(snap.activeVesselName) || snap.activeVesselPid != 0)
            {
                sb.Append(TruncateId(snap.activeRecId))
                  .Append('|')
                  .Append(TruncateName(snap.activeVesselName))
                  .Append("|pid=")
                  .Append(snap.activeVesselPid.ToString(inv));
            }
            else
            {
                sb.Append('-');
            }

            // rec.prev: only non-'-' on transition since the previous emitted snapshot.
            // Covers both "changed to different id" and "changed to null" transitions.
            sb.Append(" rec.prev=");
            if (lastSeenRecId != null && lastSeenRecId != snap.activeRecId)
                sb.Append(TruncateId(lastSeenRecId));
            else
                sb.Append('-');

            sb.Append(" rec.live=").Append(BoolStr(snap.isRecording))
              .Append('/').Append(BoolStr(snap.isBackgrounded));

            sb.Append(" rec.buf=")
              .Append(snap.bufferedPoints.ToString(inv))
              .Append('/')
              .Append(snap.bufferedPartEvents.ToString(inv))
              .Append('/')
              .Append(snap.bufferedOrbitSegments.ToString(inv));

            sb.Append(" lastUT=");
            if (double.IsNaN(snap.lastRecordedUT))
                sb.Append('-');
            else
                sb.Append(snap.lastRecordedUT.ToString("F1", inv));

            sb.Append(" tree.recs=")
              .Append(snap.treeRecordingCount.ToString(inv))
              .Append('/')
              .Append(snap.treeBackgroundMapCount.ToString(inv));

            sb.Append(" pend.tree=");
            if (snap.pendingTreePresent)
                sb.Append(TruncateId(snap.pendingTreeId))
                  .Append(':')
                  .Append(snap.pendingTreeState.ToString());
            else
                sb.Append('-');

            sb.Append(" pend.sa=");
            if (snap.pendingStandalonePresent)
                sb.Append(TruncateId(snap.pendingStandaloneRecId));
            else
                sb.Append('-');

            sb.Append(" pend.split=")
              .Append(BoolStr(snap.pendingSplitPresent))
              .Append('/')
              .Append(BoolStr(snap.pendingSplitInProgress));

            sb.Append(" chain=");
            if (snap.chainActiveChainId != null)
                sb.Append(TruncateId(snap.chainActiveChainId))
                  .Append("|idx=")
                  .Append(snap.chainNextIndex.ToString(inv));
            else
                sb.Append('-');

            // Auxiliary chain fields when continuations are active — only emitted
            // when non-zero so the line stays compact in the common case.
            if (snap.chainContinuationPid != 0)
                sb.Append(" chain.cont=").Append(snap.chainContinuationPid.ToString(inv));
            if (snap.chainUndockContinuationPid != 0)
                sb.Append(" chain.undock=").Append(snap.chainUndockContinuationPid.ToString(inv));
            if (snap.chainBoundaryAnchorPending)
                sb.Append(" chain.anchor=1");

            sb.Append(" ut=").Append(snap.currentUT.ToString("F1", inv));
            sb.Append(" scene=").Append(snap.loadedScene.ToString());

            // Update transition cache after rendering so the *next* call reflects
            // a transition only when activeRecId actually changes.
            lastSeenRecId = snap.activeRecId;

            return sb.ToString();
        }

        private static string FormatMode(RecorderMode mode)
        {
            switch (mode)
            {
                case RecorderMode.Tree: return "tree";
                case RecorderMode.Standalone: return "sa";
                case RecorderMode.None: return "none";
                default: return "?";
            }
        }

        private static string TruncateId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "-";
            return id.Length <= 8 ? id : id.Substring(0, 8);
        }

        // Caps free-text names (vessel / tree) to a bounded length so a 200-char
        // mod-generated name can't blow out the single-line [RecState] dump.
        // 32 chars leaves enough room to recognise a stock vessel name; longer
        // names get a trailing "..." marker.
        private const int MaxRecStateNameLen = 32;
        private static string TruncateName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "-";
            if (name.Length <= MaxRecStateNameLen) return name;
            return name.Substring(0, MaxRecStateNameLen) + "...";
        }

        private static string BoolStr(bool b) => b ? "T" : "F";
    }
}
