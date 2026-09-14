using System;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Parsek
{
    /// <summary>
    /// [RecState] structured state-dump logging: renders a
    /// <see cref="RecorderStateSnapshot"/> into one deterministic log line and
    /// emits it through <see cref="ParsekLog"/>.
    ///
    /// <para>Lives here rather than on <see cref="ParsekLog"/> because the logger
    /// is referenced by nearly every file in the assembly and must therefore
    /// reference nothing itself: formatting a recorder snapshot would drag the
    /// recording types (and the whole cycle they sit in) behind every log call.
    /// This class owns the sequence counter and the rec.prev transition cache and
    /// borrows only <see cref="ParsekLog"/>'s emit surface and its single
    /// rate-limit dictionary.</para>
    /// </summary>
    internal static class RecorderStateLog
    {
        // [RecState] sequence counter - incremented on every emission so log
        // readers can spot dropped lines and sort by emission order even when
        // multiple snapshots fire in the same tick.
        private static long s_recStateSeq;

        // Last-seen activeRecId, used to populate the "rec.prev" field only on
        // transitions. Reset for tests via ParsekLog.ResetTestOverrides, which
        // reaches this class through the hook registered below.
        // [ThreadStatic] is correct for KSP's main-thread-only reality and for
        // xUnit's per-thread test isolation. Any future caller from a background
        // thread (e.g., a Harmony prefix on an async path) would silently start
        // its own transition cache and miss cross-thread transitions - if that
        // ever happens, convert to a locked shared field.
        [ThreadStatic]
        private static string t_lastSeenActiveRecId;

        static RecorderStateLog()
        {
            // Publishes this class's test reset so ParsekLog.ResetTestOverrides
            // stays the one call a test makes, without ParsekLog naming this type.
            ParsekLog.RegisterTestResetHook(ResetRecStateForTesting);
        }

        /// <summary>
        /// Resets the [RecState] sequence counter and last-seen-recId cache.
        /// Tests reach this through <c>ParsekLog.ResetTestOverrides</c> so sequence
        /// numbers and the rec.prev transition cache start from a known baseline.
        /// </summary>
        internal static void ResetRecStateForTesting()
        {
            Interlocked.Exchange(ref s_recStateSeq, 0);
            t_lastSeenActiveRecId = null;
        }

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
            ParsekLog.Write("INFO", "RecState", line);
        }

        /// <summary>
        /// Rate-limited variant for hot-path recovery diagnostics that still need
        /// full <c>[RecState]</c> snapshots on the first occurrence and on summary
        /// cadence. Normal lifecycle boundaries should call <see cref="RecState"/>.
        /// </summary>
        /// <remarks>
        /// Like the other rate-limited loggers, summaries are emitted only when the
        /// same key fires again after the interval; trailing suppressed counts for
        /// abandoned fingerprints are intentionally dropped. Keys live in ParsekLog's
        /// shared per-session rate-limit dictionary until reset, so callers should use
        /// coarse, stable fingerprints rather than per-frame values.
        /// </remarks>
        internal static void RecStateRateLimited(
            string phase,
            RecorderStateSnapshot snap,
            string key,
            double minIntervalSeconds = ParsekLog.DefaultRateLimitSeconds)
        {
            if (string.IsNullOrEmpty(key))
            {
                RecState(phase, snap);
                return;
            }

            string compositeKey = $"R|RecState|{phase}|{key}";
            if (!ParsekLog.TryClaimRateLimitSlot(compositeKey, minIntervalSeconds, out int suppressedCount))
                return;

            long seq = Interlocked.Increment(ref s_recStateSeq);
            string line = FormatRecState(seq, phase, snap, ref t_lastSeenActiveRecId);
            string suffix = suppressedCount > 0
                ? $" | suppressed={suppressedCount}"
                : string.Empty;
            ParsekLog.Write("INFO", "RecState", $"{line}{suffix}");
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

            // Auxiliary chain fields when continuations are active - only emitted
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
