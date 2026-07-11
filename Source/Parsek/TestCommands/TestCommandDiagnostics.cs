using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure diagnostic emitters for the per-command lifecycle of the ParsekTestCommands
    /// seam (P6.1). Every dispatch / execution branch routes its log line through here so
    /// none is silent and the grep-able shapes (<c>recv</c>, <c>dispatch -&gt; ...</c>,
    /// <c>reject</c>, <c>exec ... verdict=</c>, <c>timeout</c>, <c>duplicate</c>) are pinned
    /// by log-assertion tests via <c>ParsekLog.TestSinkForTesting</c> without a live Unity
    /// runtime (the addon that calls these is a MonoBehaviour and cannot be exercised
    /// headless). Goal: a KSP.log reader can reconstruct, for every command id, that it was
    /// received, which branch it took and why, whether it executed, and the terminal verdict.
    /// </summary>
    internal static class TestCommandDiagnostics
    {
        internal const string Tag = "TestCommands";

        /// <summary>Command received (one per parsed, non-duplicate command line).</summary>
        internal static void Receipt(string id, string verb, int argCount)
            => ParsekLog.Info(Tag, $"recv id={id} cmd={verb ?? "?"} args={argCount.ToString(CultureInfo.InvariantCulture)}");

        /// <summary>Head dispatched to Execute.</summary>
        internal static void DispatchExecute(string id)
            => ParsekLog.Info(Tag, $"dispatch id={id} -> EXECUTE");

        /// <summary>Head deferred (first time at the head).</summary>
        internal static void DispatchDefer(string id, string reason)
            => ParsekLog.Info(Tag, $"dispatch id={id} -> DEFER reason={reason}");

        /// <summary>Head still deferring (rate-limited per id while it keeps deferring).</summary>
        internal static void DispatchDeferRepeat(string id, string reason)
            => ParsekLog.VerboseRateLimited(Tag, "defer-" + id, $"dispatch id={id} -> DEFER reason={reason}", 5.0);

        /// <summary>Terminal REJECTED (malformed / unknown / reserved / whitelist / LoadGame guard).</summary>
        internal static void DispatchReject(string id, string verb, string reason)
            => ParsekLog.Warn(Tag, $"reject id={id} cmd={verb ?? "?"} reason={reason}");

        /// <summary>Crash-recovery INTERRUPTED at the live head (journal at CLAIMED).</summary>
        internal static void DispatchInterrupted(string id, JournalPhase phase)
            => ParsekLog.Info(Tag, $"dispatch id={id} -> INTERRUPTED (journal={phase})");

        /// <summary>Execution about to start.</summary>
        internal static void ExecStart(string id, string verb)
            => ParsekLog.Info(Tag, $"exec id={id} cmd={verb} start");

        /// <summary>Execution produced a terminal verdict.</summary>
        internal static void ExecVerdict(string id, string verdict)
            => ParsekLog.Info(Tag, $"exec id={id} verdict={verdict}");

        /// <summary>Two-phase side effect initiated; terminal deferred to completion.</summary>
        internal static void ExecPending(string id)
            => ParsekLog.Info(Tag, $"exec id={id} verdict=PENDING (two-phase awaiting completion)");

        /// <summary>Response line landed (Verbose; the IO-failure path logs its own Warn/Error).</summary>
        internal static void ResponseAppended(string id, string verdict)
            => ParsekLog.Verbose(Tag, $"response appended id={id} verdict={verdict}");

        /// <summary>Head exceeded its deferral budget and converted to TIMEOUT.</summary>
        internal static void Timeout(string id, string verb, double deferredSeconds, string reason)
            => ParsekLog.Warn(Tag,
                $"timeout id={id} cmd={verb} deferred={deferredSeconds.ToString("F1", CultureInfo.InvariantCulture)}s reason={reason}");

        /// <summary>A command id already terminal / in-flight: skipped, no second response.</summary>
        internal static void Duplicate(string id)
            => ParsekLog.Warn(Tag, $"duplicate id={id} ignored");
    }
}
