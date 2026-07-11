using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Parsek.InGameTests;
using Parsek.InGameTests.Helpers;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin Unity host for the ParsekTestCommands seam (M-A2). A DDOL
    /// <see cref="MonoBehaviour"/> that, ONLY when armed by the
    /// <c>PARSEK_TEST_COMMANDS=1</c> environment variable, polls the KSP-root command
    /// file, executes commands on the Unity main thread, and appends responses. The
    /// parse / validate / dispatch / journal / lock logic all lives in the pure
    /// <c>Parsek.TestCommands</c> core (xUnit-tested without Unity); this addon only
    /// samples live state, performs file I/O, and calls those decisions. It mirrors
    /// <see cref="Parsek.InGameTests.TestRunnerShortcut"/>'s lifecycle pattern
    /// (Instantly + DontDestroyOnLoad + singleton guard + scene-scoped safe-point
    /// gating).
    ///
    /// <para>Fail-closed and provably inert: the env var is read ONCE in
    /// <see cref="Awake"/> (changing it requires a process restart), and when it is not
    /// the literal <c>1</c> the addon takes NO file handles and does NO polling work -
    /// <see cref="Update"/> returns immediately on the cached <see cref="armed"/> bool.
    /// It is never shipped enabled and adds no Settings-UI toggle.</para>
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class ParsekTestCommandAddon : MonoBehaviour, ITestCommandExecutor
    {
        private const string Tag = "TestCommands";

        /// <summary>The environment variable that arms the addon.</summary>
        internal const string EnvVarName = "PARSEK_TEST_COMMANDS";

        /// <summary>The ONLY value that arms it (exact match, fail-closed).</summary>
        internal const string ArmValue = "1";

        /// <summary>Frames to wait after a new scene loads before it is "settled" and the
        /// pump may execute. Matches the scene-scoped-state pattern TestRunnerShortcut
        /// uses for its input lock.</summary>
        private const int SettleFrames = 2;

        private static ParsekTestCommandAddon instance;

        /// <summary>Singleton accessor - non-null after Awake while DDOL keeps the
        /// instance alive. Used by in-game tests to assert inert-when-unarmed.</summary>
        internal static ParsekTestCommandAddon Instance => instance;

        // Cached ONCE in Awake from the env var; changing PARSEK_TEST_COMMANDS after
        // process start has NO effect (documented: env change requires a KSP restart).
        private bool armed;

        // Scene-scoped safe-point gating. onGameSceneLoadRequested sets transitioning;
        // onLevelWasLoaded seeds the settle counter for the new scene; Update drains it
        // and clears transitioning at the settle boundary.
        private bool sceneTransitioning;
        private int settleCounter;

        // Channel state (P4.3). Paths resolve on the first armed frame; the lock decides
        // whether this instance owns the channel (disabled == stood down behind a live
        // foreign lock). commandByteOffset is the persistent read cursor over whole lines.
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private readonly IPidLivenessProbe livenessProbe = new ProcessLivenessProbe();

        private bool startupDone;
        private bool disabled;
        private string commandFilePath;
        private string responseFilePath;
        private string journalFilePath;
        private string lockFilePath;
        private long commandByteOffset;

        // Pump state (P4.4). pending is the strict-FIFO command queue; queuedIds /
        // processedIds dedup ids (in-flight vs already-terminal). journalPhases is the
        // startup-replayed phase map (feeds DispatchState.JournalPhase). lineCounter is
        // the absolute file line position for the line#<n> fallback id. responseSeq is
        // the per-process monotonic response/journal counter.
        private readonly Queue<ParsedCommand> pending = new Queue<ParsedCommand>();
        private readonly HashSet<string> queuedIds = new HashSet<string>();
        private readonly HashSet<string> processedIds = new HashSet<string>();
        private Dictionary<string, JournalPhase> journalPhases = new Dictionary<string, JournalPhase>();
        private int lineCounter;
        private long responseSeq;
        // Set true while a LoadGame is in flight (P5.7); read now by the LoadGame guard.
        private bool loadInFlight = false;

        // Two-phase verbs (RunTests P5.6 / LoadGame P5.7). The executor INITIATES the
        // long-running side effect and returns PendingVerdict; the pump journals CLAIMED
        // at initiation but defers EXECUTED + the terminal response until TryCompleteTwoPhase
        // sees the operation settle (RunTests batch done / new game loaded). A crash between
        // CLAIMED and completion leaves the id at CLAIMED -> INTERRUPTED on restart.
        private const string PendingVerdict = TestCommandExecution.PendingVerdict;
        private bool awaitingCompletion;
        private string completionId;
        private string completionVerb;
        private long completionSeq;
        private double completionStartedAt;
        private string loadGameSave;

        // FlushAndQuit (P5.8): the response + journal DONE are written and flushed BEFORE
        // Application.Quit, which is deferred one frame via a coroutine scheduled only
        // after the DONE for the quit id lands.
        private bool pendingQuit;
        private string quitId;

        // The in-game test runner the addon owns for RunTests (P5.6). Null until then;
        // the batch-running safe-point gate reads it now so the pump never runs a command
        // mid-batch once RunTests is wired.
        private InGameTestRunner ownedRunner = null;

        // Head deferral tracking: the id currently deferring at the FIFO head and when it
        // began, so a never-satisfiable head converts to TIMEOUT once its budget expires.
        private string deferHeadId;
        private double deferStartedAtSeconds;
        private string lastDeferReason;

        // A terminal response whose append exhausted its retries on a prior frame. The
        // side effect already ran and is journaled EXECUTED, so this is re-appended
        // WITHOUT re-executing until it lands, then journaled DONE.
        private bool headPendingResponse;
        private string headPendingResponseLine;
        private string headPendingResponseId;
        private long headPendingResponseSeq;
        private string headPendingResponseVerdict;

        // Executor result carrier: the ITestCommandExecutor methods are void, so a verb
        // handler stashes its verdict/payload/msg here and the pump reads them back.
        private string execVerdict;
        private List<KeyValuePair<string, string>> execPayload;
        private string execMsg;

        internal bool IsArmedForTesting => armed;
        internal bool SceneTransitioningForTesting => sceneTransitioning;
        internal int SettleCounterForTesting => settleCounter;
        internal bool DisabledForTesting => disabled;

        /// <summary>True once the first armed frame resolved channel paths + touched the
        /// lock/journal files. Stays false forever when unarmed (TryStartup is the only
        /// file-touching startup and runs only behind the armed gate), so it is the
        /// in-game inert-when-unarmed proof: no file access ever happened.</summary>
        internal bool StartupDoneForTesting => startupDone;

        /// <summary>
        /// Pure fail-closed env-gate predicate: ONLY the literal <c>"1"</c> arms the
        /// addon. <c>null</c> (unset), <c>"0"</c>, <c>"true"</c>, and <c>""</c> all stay
        /// inert. This is the security boundary that prevents the seam from ever shipping
        /// enabled by accident.
        /// </summary>
        internal static bool IsArmed(string envValue) => envValue == ArmValue;

        void Awake()
        {
            if (instance != null)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);

            // Read ONCE. The env var is the entire arm gate; it is never re-read.
            string envValue = Environment.GetEnvironmentVariable(EnvVarName);
            armed = IsArmed(envValue);

            if (armed)
            {
                ParsekLog.Info(Tag, "armed (PARSEK_TEST_COMMANDS=1)");
                // Scene-scoped state only (no gameplay subscriptions), matching
                // TestRunnerShortcut: the addon tracks scene transitions purely for its
                // own safe-point gating.
                GameEvents.onGameSceneLoadRequested.Add(OnSceneChangeRequested);
                GameEvents.onLevelWasLoaded.Add(OnLevelWasLoaded);
            }
            else
            {
                ParsekLog.Verbose(Tag, $"inert: PARSEK_TEST_COMMANDS={FormatEnvForLog(envValue)}");
            }
        }

        void Update()
        {
            // Provably inert when unarmed: no polling, no file access - return on the
            // cached bool before any work.
            if (!armed) return;

            // First armed frame: resolve channel paths and acquire the lock. If we stood
            // down behind a live foreign lock, disabled is set and we do no channel work.
            if (!startupDone)
                TryStartup();
            if (disabled) return;

            if (settleCounter > 0)
            {
                settleCounter--;
                if (settleCounter == 0)
                    sceneTransitioning = false;
            }

            // Safe-point gate: never pump during LOADING, a scene transition / settle, or
            // an in-game test batch. (DecideDispatch re-checks these from DispatchState;
            // gating here avoids even reading the channel at an unsafe moment.)
            if (HighLogic.LoadedScene == GameScenes.LOADING) return;
            if (sceneTransitioning || settleCounter > 0) return;

            // Two-phase completion (RunTests / LoadGame) is checked BEFORE the batch gate:
            // RunTests must complete precisely when its owned batch stops running, which
            // the IsBatchRunning early return below would otherwise mask. While a two-phase
            // command awaits completion it holds the FIFO head, so no other command runs.
            if (awaitingCompletion)
            {
                TryCompleteTwoPhase();
                return;
            }

            if (IsBatchRunning()) return;

            Pump();
        }

        // ----- Startup: channel paths + lock + journal reconcile (P4.3 / P4.4) -----

        // Runs ONCE on the first armed frame. Resolves the four KSP-root channel paths,
        // decides lock ownership, then (if we own the channel) replays the journal and
        // reconciles crash-recovery leftovers and seeds the processed-id set.
        private void TryStartup()
        {
            startupDone = true;
            string root = KspRootPath();
            commandFilePath = Path.Combine(root, TestCommandChannelIo.CommandFileName);
            responseFilePath = Path.Combine(root, TestCommandChannelIo.ResponseFileName);
            journalFilePath = Path.Combine(root, TestCommandChannelIo.JournalFileName);
            lockFilePath = Path.Combine(root, TestCommandChannelIo.LockFileName);

            AcquireLock(root);
            if (disabled) return;

            ReplayAndReconcile();
        }

        // Replays the journal into a per-id phase map and applies each crash-recovery
        // action (DecideRecovery): DONE ids skip, EXECUTED ids re-ack, CLAIMED ids get an
        // INTERRUPTED terminal. Every journalled id is seeded into the processed set so a
        // re-read of its command line is a no-op. The command-file byte offset stays 0:
        // ids are deduped by the processed set, so a full rescan on the first poll is
        // correct and only happens once (steady state uses the offset + processed set).
        private void ReplayAndReconcile()
        {
            string content = null;
            try
            {
                if (File.Exists(journalFilePath))
                    content = File.ReadAllText(journalFilePath, Utf8NoBom);
            }
            catch (IOException ex)
            {
                ParsekLog.Warn(Tag, $"journal read failed: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                ParsekLog.Warn(Tag, $"journal read failed (access denied): {ex.Message}");
            }

            List<string> lines = TestCommandJournal.SplitCompleteLines(content ?? string.Empty);
            journalPhases = TestCommandJournal.ReplayIntoPhaseMap(lines);

            // Capture the verb per id from CLAIMED lines for nicer recovery responses/logs.
            var verbs = new Dictionary<string, string>();
            foreach (string l in lines)
                if (TestCommandJournal.TryParseLine(l, out JournalLine jl) && !string.IsNullOrEmpty(jl.Verb))
                    verbs[jl.Id] = jl.Verb;

            int done = 0, executed = 0, claimed = 0;
            foreach (KeyValuePair<string, JournalPhase> kv in journalPhases)
            {
                string id = kv.Key;
                verbs.TryGetValue(id, out string verb);
                switch (TestCommandJournal.DecideRecovery(id, kv.Value))
                {
                    case RecoveryAction.Skip:
                        done++;
                        break;
                    case RecoveryAction.RewriteResponse:
                        executed++;
                        // Side effect ran; re-emit the ORIGINAL terminal response stored on the
                        // EXECUTED line (true RewriteResponse) so the orchestrator sees the real
                        // verdict/payload/msg, not a synthetic INTERRUPTED. A pre-enrichment /
                        // torn EXECUTED line with no stored fields falls back to the old ack.
                        if (TestCommandJournal.TryGetExecutedResponse(
                                lines, id, out string exVerdict, out var exPayload, out string exMsg))
                            WriteRecoveryTerminal(id, verb, exVerdict, exPayload, exMsg);
                        else
                            WriteRecoveryTerminal(id, verb, "INTERRUPTED", null, "recovered-executed");
                        break;
                    case RecoveryAction.Interrupted:
                        claimed++;
                        WriteRecoveryTerminal(id, verb, "INTERRUPTED", null, "interrupted-claimed");
                        break;
                }
                processedIds.Add(id);
            }

            ParsekLog.Info(Tag,
                $"journal replay: {done} done, {executed} executed-not-done (rewriting response), {claimed} claimed-not-executed (INTERRUPTED)");
        }

        // ----- Lock acquire / inspect (P4.3) -----

        // Reads any existing lock, decides ownership via the pure DecideLockOwnership with
        // a real Process.GetProcessById liveness probe, and either stands down (live
        // foreign owner) or writes our own lock line (acquire / reclaim / reclaim-crashed).
        private void AcquireLock(string root)
        {
            LockInfo existing = default;
            bool present = false;
            try
            {
                if (File.Exists(lockFilePath))
                {
                    string content = File.ReadAllText(lockFilePath, Utf8NoBom);
                    List<string> lines = TestCommandJournal.SplitCompleteLines(content);
                    // The lock is a single line; tolerate a missing trailing newline by
                    // falling back to the raw trimmed content.
                    string lockLine = lines.Count > 0 ? lines[0] : content.Trim();
                    present = LockFile.TryParseLockLine(lockLine, out existing);
                }
            }
            catch (IOException ex)
            {
                ParsekLog.Warn(Tag, $"lock read failed: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                ParsekLog.Warn(Tag, $"lock read failed (access denied): {ex.Message}");
            }

            string ownSession = ParsekProcess.ProcessSessionId.ToString("N");
            double ownT = WallClockSeconds();
            LockOwnership decision = LockFile.DecideLockOwnership(
                existing, present, ownSession, ownT, livenessProbe);

            if (decision == LockOwnership.StandDown)
            {
                disabled = true;
                ParsekLog.Warn(Tag,
                    $"standing down: foreign live lock session={existing.Session} pid={existing.Pid} (alive) t={existing.T.ToString("R", CultureInfo.InvariantCulture)}");
                return;
            }
            if (decision == LockOwnership.ReclaimWithWarn)
            {
                ParsekLog.Warn(Tag,
                    $"reclaimed crashed lock session={existing.Session} pid={existing.Pid} (dead) t={existing.T.ToString("R", CultureInfo.InvariantCulture)}");
            }

            int pid = CurrentProcessId();
            string line = LockFile.FormatLockLine(ownSession, pid, ownT, root);
            try
            {
                File.WriteAllText(lockFilePath, line + "\n", Utf8NoBom);
                ParsekLog.Info(Tag, $"lock acquired session={ownSession} pid={pid}");
            }
            catch (IOException ex)
            {
                ParsekLog.Warn(Tag, $"lock write failed: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                ParsekLog.Warn(Tag, $"lock write failed (access denied): {ex.Message}");
            }
        }

        // ----- Command-file reads (P4.3) -----

        // Reads whole new command lines appended since the persistent byte offset. Opens
        // the file share-all (FileShare.ReadWrite) so the orchestrator can keep appending;
        // advances the offset only over newline-terminated content (the pure
        // WholeLineByteCount rule), leaving a torn trailing fragment for the next poll.
        private List<string> ReadNewCommandLines()
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(commandFilePath) || !File.Exists(commandFilePath))
                return lines;
            try
            {
                using (var fs = new FileStream(
                    commandFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long length = fs.Length;
                    if (length <= commandByteOffset)
                        return lines; // nothing new since the last poll
                    fs.Seek(commandByteOffset, SeekOrigin.Begin);
                    int toRead = checked((int)(length - commandByteOffset));
                    byte[] buffer = new byte[toRead];
                    int read = ReadFully(fs, buffer, toRead);
                    int whole = TestCommandChannelIo.WholeLineByteCount(buffer, read);
                    if (whole <= 0)
                        return lines; // only a torn trailing fragment so far
                    string chunk = Utf8NoBom.GetString(buffer, 0, whole);
                    lines = TestCommandJournal.SplitCompleteLines(chunk);
                    commandByteOffset += whole;
                }
            }
            catch (IOException ex)
            {
                ParsekLog.Warn(Tag, $"command read failed offset={commandByteOffset}: {ex.Message}");
            }
            return lines;
        }

        // ----- Response / journal appends (P4.3) -----

        private bool AppendResponse(string line, string id)
            => TryAppendLine(responseFilePath, line, "response", id);

        private bool AppendJournal(string line, string id)
            => TryAppendLine(journalFilePath, line, "journal", id);

        // Append one newline-terminated line with append+flush and bounded backoff retry.
        // On exhaustion returns false WITHOUT throwing; the caller leaves the id at
        // EXECUTED so the response is (re)written next frame / next restart, never
        // re-executed.
        private bool TryAppendLine(string path, string line, string kind, string id)
        {
            if (string.IsNullOrEmpty(path)) return false;
            for (int attempt = 1; attempt <= TestCommandChannelIo.MaxAppendAttempts; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(
                        path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (var writer = new StreamWriter(fs, Utf8NoBom))
                    {
                        writer.Write(line);
                        writer.Write('\n');
                        writer.Flush();
                    }
                    return true;
                }
                catch (IOException ex)
                {
                    ParsekLog.Warn(Tag, $"{kind} append failed id={id} attempt={attempt}: {ex.Message}");
                    if (!TestCommandChannelIo.ShouldRetryAppend(attempt, TestCommandChannelIo.MaxAppendAttempts))
                    {
                        ParsekLog.Error(Tag,
                            $"{kind} append giving up this frame id={id}; will retry (journal=EXECUTED)");
                        return false;
                    }
                    Thread.Sleep(TestCommandChannelIo.BackoffMillis(attempt));
                }
            }
            return false;
        }

        // ----- The pump (P4.4) -----

        // One poll: retry a stuck response, then read+enqueue new command lines, then make
        // and act on a single strict-FIFO head decision. Runs only at a safe point.
        private void Pump()
        {
            // Retry a terminal response append that exhausted its retries on a prior frame.
            // The side effect already ran (journalled EXECUTED), so this never re-executes.
            if (headPendingResponse)
            {
                if (!AppendResponse(headPendingResponseLine, headPendingResponseId))
                    return; // still failing; try again next frame
                headPendingResponse = false;
                WriteDoneAndAdvance(headPendingResponseId, headPendingResponseSeq, headPendingResponseVerdict);
                return;
            }

            ReadAndEnqueue();

            if (pending.Count == 0) return;

            ParsedCommand head = pending.Peek();
            DispatchState state = BuildDispatchState(head);
            DispatchResult result = TestCommandDispatcher.DecideDispatch(head, state);

            switch (result.Decision)
            {
                case DispatchDecision.Execute:
                    ResetDeferTracking();
                    TestCommandDiagnostics.DispatchExecute(head.Id);
                    ExecuteHead(head); // owns its dequeue (terminal / pending-response)
                    break;
                case DispatchDecision.Reject:
                    ResetDeferTracking();
                    TestCommandDiagnostics.DispatchReject(head.Id, head.Verb, result.Reason);
                    WriteTerminalNoSideEffect(head, "REJECTED", result.Reason);
                    pending.Dequeue();
                    break;
                case DispatchDecision.Interrupted:
                    ResetDeferTracking();
                    InterruptHead(head);
                    pending.Dequeue();
                    break;
                case DispatchDecision.Defer:
                    HandleDefer(head, result.Reason); // dequeues only on TIMEOUT
                    break;
            }
        }

        // Read new whole lines and enqueue parseable, not-yet-terminal commands (skipping
        // blank/comment lines and duplicate ids). Batch-counting per the house convention:
        // one rate-limited poll summary rather than a line per read.
        private void ReadAndEnqueue()
        {
            List<string> newLines = ReadNewCommandLines();
            int readCount = newLines.Count;
            int parsedCount = 0;

            foreach (string raw in newLines)
            {
                lineCounter++;
                ParsedCommand parsed = TestCommandParser.ParseLine(raw, lineCounter);
                if (parsed.Ignored) continue; // blank / comment: no response

                // Normalize the correlation id (real id, or line#<n> for a missing/garbage id).
                string id = parsed.Id ?? TestCommandParser.FallbackId(lineCounter);
                parsed.Id = id;

                if (processedIds.Contains(id) || queuedIds.Contains(id))
                {
                    TestCommandDiagnostics.Duplicate(id);
                    continue;
                }

                int argCount = parsed.Args != null ? parsed.Args.Count : 0;
                TestCommandDiagnostics.Receipt(id, parsed.Verb, argCount);
                queuedIds.Add(id);
                pending.Enqueue(parsed);
                parsedCount++;
            }

            if (readCount > 0 || pending.Count > 0)
            {
                string headVerb = pending.Count > 0 ? (pending.Peek().Verb ?? "?") : "none";
                ParsekLog.VerboseRateLimited(Tag, "poll",
                    $"poll: read={readCount} lines, parsed={parsedCount}, deferred-head={headVerb}", 5.0);
            }
        }

        // Execute the head verb: journal CLAIMED -> run the (stub) handler -> journal
        // EXECUTED -> append the terminal response -> journal DONE -> advance. At-most-once:
        // the side effect runs ONLY after CLAIMED durably lands, and a failed response
        // append is cached for retry WITHOUT re-executing.
        private void ExecuteHead(ParsedCommand head)
        {
            string id = head.Id;
            long seq = NextSeq();

            if (!WriteJournal(
                TestCommandJournal.FormatClaimed(id, seq, head.Verb ?? string.Empty, SessionId(), WallClockSeconds()),
                id, "CLAIMED"))
            {
                // Could not durably claim; do NOT run the side effect. Leave the head and
                // retry next frame (no side effect has run, so at-most-once holds).
                return;
            }

            TestCommandDiagnostics.ExecStart(id, head.Verb);
            ClearExecResult();
            try
            {
                InvokeExecutor(head);
            }
            catch (Exception ex)
            {
                // An executor threw: contain it as a terminal ERROR so the id completes and
                // the pump advances instead of the whole seam wedging on an unhandled throw.
                // The side effect already ran to the point of the throw and is journalled
                // CLAIMED; converting to a terminal (EXECUTED + response + DONE) preserves
                // at-most-once (a restart replay sees EXECUTED and never re-runs it).
                TestCommandExecution.ExceptionTerminal(ex.GetType().Name, out string exVerdict, out string exMsg);
                ParsekLog.Error(Tag, $"exec threw id={id} cmd={head.Verb}: {ex.GetType().Name}: {ex.Message}");
                EmitExecutedTerminal(id, seq, head.Verb, exVerdict, null, exMsg);
                return;
            }
            string verdict = execVerdict ?? "ERROR";

            if (verdict == PendingVerdict)
            {
                // Two-phase (RunTests / LoadGame): the side effect was initiated; the
                // terminal response + EXECUTED journal are deferred until the operation
                // settles (TryCompleteTwoPhase). Journal stays at CLAIMED meanwhile, so a
                // crash between here and completion reports INTERRUPTED on restart. Head
                // stays in the queue (strict FIFO) so nothing else runs during the wait.
                awaitingCompletion = true;
                completionId = id;
                completionSeq = seq;
                completionVerb = head.Verb;
                completionStartedAt = WallClockSeconds();
                TestCommandDiagnostics.ExecPending(id);
                return;
            }

            EmitExecutedTerminal(id, seq, head.Verb, verdict, execPayload, execMsg);
        }

        // Shared single-phase terminal tail: journal EXECUTED, append the response, then
        // (on a successful append) journal DONE + advance, else cache for a retry WITHOUT
        // re-executing (id stays journalled EXECUTED). Used by ExecuteHead and by the
        // two-phase completion.
        private void EmitExecutedTerminal(
            string id, long seq, string verb, string verdict,
            List<KeyValuePair<string, string>> payload, string msg)
        {
            WriteJournal(
                TestCommandJournal.FormatExecuted(id, seq, WallClockSeconds(), verdict, payload, msg),
                id, "EXECUTED");

            string line = TestCommandResponse.FormatResponseLine(
                id, verb, verdict, seq, CurrentUt(), payload, msg);
            TestCommandDiagnostics.ExecVerdict(id, verdict);

            if (AppendResponse(line, id))
            {
                TestCommandDiagnostics.ResponseAppended(id, verdict);
                WriteDoneAndAdvance(id, seq, verdict);
            }
            else
            {
                headPendingResponse = true;
                headPendingResponseLine = line;
                headPendingResponseId = id;
                headPendingResponseSeq = seq;
                headPendingResponseVerdict = verdict;
            }
        }

        // Journal DONE, mark the id processed, advance the FIFO head, and (for
        // FlushAndQuit) schedule the deferred quit now that the response + DONE are durable.
        private void WriteDoneAndAdvance(string id, long seq, string verdict)
        {
            WriteJournal(TestCommandJournal.FormatDone(id, seq, verdict, WallClockSeconds()), id, "DONE");
            MarkProcessed(id);
            if (pending.Count > 0) pending.Dequeue();
            MaybeScheduleQuit(id);
        }

        // ----- Two-phase completion (P5.6 RunTests / P5.7 LoadGame) -----

        // Called each safe-point frame while a two-phase command awaits completion. When
        // the operation has settled it builds the terminal payload, journals EXECUTED +
        // response + DONE (advancing the head); otherwise it converts to TIMEOUT once the
        // verb's budget expires so a never-settling operation cannot wedge the run.
        private void TryCompleteTwoPhase()
        {
            double now = WallClockSeconds();
            bool done = false;
            string verdict = null;
            List<KeyValuePair<string, string>> payload = null;
            string msg = null;

            if (completionVerb == "RunTests")
            {
                if (ownedRunner == null || !ownedRunner.IsRunning)
                {
                    int passed = ownedRunner != null ? ownedRunner.Passed : 0;
                    int failed = ownedRunner != null ? ownedRunner.Failed : 0;
                    int skipped = ownedRunner != null ? ownedRunner.Skipped : 0;
                    payload = TestCommandRunTests.BuildResultPayload(passed, failed, skipped);
                    verdict = "OK";
                    done = true;
                    ParsekLog.Info(Tag,
                        $"runtests complete passed={passed} failed={failed} skipped={skipped} results={TestCommandRunTests.ResultsFileName}");
                }
            }
            else if (completionVerb == "LoadGame")
            {
                if (HighLogic.CurrentGame != null)
                {
                    string sceneName = HighLogic.LoadedScene.ToString();
                    payload = TestCommandLoadGame.BuildCompletePayload(sceneName, loadGameSave);
                    verdict = "OK";
                    done = true;
                    loadInFlight = false;
                    ParsekLog.Info(Tag,
                        $"loadgame complete scene={sceneName} save={loadGameSave ?? string.Empty} game-loaded=true");
                }
            }

            if (!done)
            {
                double budget = DeferralBudget.BudgetSeconds(completionVerb);
                if (DeferralBudget.ShouldTimeout(completionStartedAt, now, budget))
                {
                    TestCommandDiagnostics.Timeout(completionId, completionVerb, now - completionStartedAt, "awaiting-completion");
                    if (completionVerb == "LoadGame") loadInFlight = false;
                    string tid = completionId; long tseq = completionSeq; string tverb = completionVerb;
                    ClearTwoPhase();
                    EmitExecutedTerminal(tid, tseq, tverb, "TIMEOUT", null, "awaiting-completion");
                }
                return;
            }

            string cid = completionId; long cseq = completionSeq; string cverb = completionVerb;
            ClearTwoPhase();
            EmitExecutedTerminal(cid, cseq, cverb, verdict, payload, msg);
        }

        private void ClearTwoPhase()
        {
            awaitingCompletion = false;
            completionId = null;
            completionVerb = null;
        }

        // Deferred one-frame quit for FlushAndQuit: scheduled only after the response +
        // journal DONE are durable, so a killed process never loses the quit's ack.
        private void MaybeScheduleQuit(string id)
        {
            if (!pendingQuit || id != quitId) return;
            pendingQuit = false;
            ParsekLog.Info(Tag, $"flushandquit: scheduling Application.Quit (deferred one frame) id={id}");
            StartCoroutine(DeferredQuit());
        }

        private System.Collections.IEnumerator DeferredQuit()
        {
            yield return null;
            ParsekLog.Info(Tag, "flushandquit: Application.Quit");
            Application.Quit();
        }

        // A terminal outcome with NO side effect (REJECTED / TIMEOUT): journal
        // CLAIMED+EXECUTED+DONE around the response so a restart replay skips the id.
        private void WriteTerminalNoSideEffect(ParsedCommand head, string verdict, string msg)
        {
            string id = head.Id;
            string verb = head.Verb ?? string.Empty;
            long seq = NextSeq();
            WriteJournal(TestCommandJournal.FormatClaimed(id, seq, verb, SessionId(), WallClockSeconds()), id, "CLAIMED");
            WriteJournal(TestCommandJournal.FormatExecuted(id, seq, WallClockSeconds(), verdict, null, msg), id, "EXECUTED");
            string line = TestCommandResponse.FormatResponseLine(id, verb, verdict, seq, CurrentUt(), null, msg);
            AppendResponse(line, id);
            WriteJournal(TestCommandJournal.FormatDone(id, seq, verdict, WallClockSeconds()), id, "DONE");
            MarkProcessed(id);
        }

        // Crash-recovery INTERRUPTED at the live head (journal CLAIMED for this id). Mostly
        // defensive: such ids are usually seeded into the processed set at startup and
        // filtered before reaching dispatch.
        private void InterruptHead(ParsedCommand head)
        {
            string id = head.Id;
            TestCommandDiagnostics.DispatchInterrupted(id, JournalPhase.Claimed);
            long seq = NextSeq();
            string line = TestCommandResponse.FormatResponseLine(
                id, head.Verb ?? string.Empty, "INTERRUPTED", seq, CurrentUt(), null, "interrupted-claimed");
            AppendResponse(line, id);
            WriteJournal(TestCommandJournal.FormatDone(id, seq, "INTERRUPTED", WallClockSeconds()), id, "DONE");
            MarkProcessed(id);
        }

        // Startup crash-recovery terminal (from ReplayAndReconcile). No pending head to
        // dequeue; just re-ack + DONE + processed-set seed happens in the caller. For the
        // RewriteResponse (EXECUTED) case the ORIGINAL verdict/payload/msg are supplied so the
        // rewritten line is byte-equivalent to the pre-crash response; the CLAIMED case keeps
        // the synthetic interrupted-claimed ack.
        private void WriteRecoveryTerminal(
            string id, string verb, string verdict,
            List<KeyValuePair<string, string>> payload, string msg)
        {
            long seq = NextSeq();
            string line = TestCommandResponse.FormatResponseLine(
                id, verb ?? string.Empty, verdict, seq, CurrentUt(), payload, msg);
            AppendResponse(line, id);
            WriteJournal(TestCommandJournal.FormatDone(id, seq, verdict, WallClockSeconds()), id, "DONE");
        }

        // Head deferral + per-command TIMEOUT conversion. The head tracks when it first
        // began deferring; once (now - start) exceeds the verb's budget it converts to a
        // TIMEOUT terminal (carrying the last defer reason) and the pump advances so a
        // never-satisfiable command never wedges the run.
        private void HandleDefer(ParsedCommand head, string reason)
        {
            double now = WallClockSeconds();
            if (deferHeadId != head.Id)
            {
                deferHeadId = head.Id;
                deferStartedAtSeconds = now;
                TestCommandDiagnostics.DispatchDefer(head.Id, reason);
            }
            else
            {
                TestCommandDiagnostics.DispatchDeferRepeat(head.Id, reason);
            }
            lastDeferReason = reason;

            double budget = DeferralBudget.BudgetSeconds(head.Verb);
            if (DeferralBudget.ShouldTimeout(deferStartedAtSeconds, now, budget))
            {
                TestCommandDiagnostics.Timeout(head.Id, head.Verb, now - deferStartedAtSeconds, lastDeferReason);
                WriteTerminalNoSideEffect(head, "TIMEOUT", lastDeferReason);
                pending.Dequeue();
                ResetDeferTracking();
            }
        }

        private void ResetDeferTracking() => deferHeadId = null;

        private DispatchState BuildDispatchState(ParsedCommand head)
        {
            JournalPhase phase = JournalPhase.None;
            if (journalPhases != null && head.Id != null)
                journalPhases.TryGetValue(head.Id, out phase);

            ParsekFlight flight = ParsekFlight.Instance;
            return new DispatchState
            {
                Scene = MapScene(HighLogic.LoadedScene),
                GameLoaded = HighLogic.CurrentGame != null,
                SettingsPresent = ParsekSettings.Current != null,
                Recording = ParsekFlight.HasLiveRecorderForTagging(),
                HasTree = flight != null && flight.HasActiveTree,
                Transitioning = sceneTransitioning,
                SettleCounter = settleCounter,
                BatchRunning = IsBatchRunning(),
                LoadInFlight = loadInFlight,
                JournalPhase = phase,
            };
        }

        private bool IsBatchRunning() => ownedRunner != null && ownedRunner.IsRunning;

        private static TestCommandScene MapScene(GameScenes scene)
        {
            switch (scene)
            {
                case GameScenes.LOADING: return TestCommandScene.Loading;
                case GameScenes.MAINMENU: return TestCommandScene.MainMenu;
                case GameScenes.SPACECENTER: return TestCommandScene.SpaceCenter;
                case GameScenes.EDITOR: return TestCommandScene.Editor;
                case GameScenes.FLIGHT: return TestCommandScene.Flight;
                case GameScenes.TRACKSTATION: return TestCommandScene.TrackingStation;
                default: return TestCommandScene.Other;
            }
        }

        // ----- Executor: all ten v1 verbs (P5.1 - P5.8) -----
        // The addon implements ITestCommandExecutor; each method delegates to its verb
        // body (below). The bodies stash their verdict/payload/msg via SetExecResult (or
        // the PENDING sentinel for the two-phase RunTests / LoadGame) and the pump reads
        // them back to journal + append the terminal response.

        // Explicit interface implementation: ParsedCommand is internal, so these cannot be
        // public members of the public MonoBehaviour. The pump dispatches via the interface
        // (InvokeExecutor casts this to ITestCommandExecutor).
        void ITestCommandExecutor.SetSetting(ParsedCommand cmd) => SetSettingImpl(cmd);
        void ITestCommandExecutor.StartRecording(ParsedCommand cmd) => StartRecordingImpl(cmd);
        void ITestCommandExecutor.StopRecording(ParsedCommand cmd) => StopRecordingImpl(cmd);
        void ITestCommandExecutor.CommitTree(ParsedCommand cmd) => CommitTreeImpl(cmd);
        void ITestCommandExecutor.DiscardTree(ParsedCommand cmd) => DiscardTreeImpl(cmd);
        void ITestCommandExecutor.RecordingState(ParsedCommand cmd) => RecordingStateImpl(cmd);
        void ITestCommandExecutor.RunTests(ParsedCommand cmd) => RunTestsImpl(cmd);
        void ITestCommandExecutor.LoadGame(ParsedCommand cmd) => LoadGameImpl(cmd);
        void ITestCommandExecutor.MissionMark(ParsedCommand cmd) => MissionMarkImpl(cmd);
        void ITestCommandExecutor.FlushAndQuit(ParsedCommand cmd) => FlushAndQuitImpl(cmd);

        private void InvokeExecutor(ParsedCommand cmd)
        {
            ITestCommandExecutor exec = this;
            switch (cmd.Verb)
            {
                case "SetSetting": exec.SetSetting(cmd); break;
                case "StartRecording": exec.StartRecording(cmd); break;
                case "StopRecording": exec.StopRecording(cmd); break;
                case "CommitTree": exec.CommitTree(cmd); break;
                case "DiscardTree": exec.DiscardTree(cmd); break;
                case "RecordingState": exec.RecordingState(cmd); break;
                case "RunTests": exec.RunTests(cmd); break;
                case "LoadGame": exec.LoadGame(cmd); break;
                case "MissionMark": exec.MissionMark(cmd); break;
                case "FlushAndQuit": exec.FlushAndQuit(cmd); break;
                default:
                    // Unreachable: DecideDispatch rejects unknown/reserved verbs before Execute.
                    SetExecResult("ERROR", null, "unknown-command");
                    break;
            }
        }

        // ----- Shared verb-body helpers (P5.x) -----

        private static string ArgOrNull(ParsedCommand cmd, string key)
            => cmd.Args != null && cmd.Args.TryGetValue(key, out string v) ? v : null;

        private static KeyValuePair<string, string> Kv(string key, string value)
            => new KeyValuePair<string, string>(key, value);

        private static List<KeyValuePair<string, string>> Payload(params KeyValuePair<string, string>[] items)
            => new List<KeyValuePair<string, string>>(items);

        private static string Bool(bool b) => b ? "true" : "false";

        private static string Int(int n) => n.ToString(CultureInfo.InvariantCulture);

        // ----- SetSetting (P5.1) -----
        // Applies a whitelisted setting through the pure whitelist decision + the pure
        // route dispatcher. An unknown name or out-of-range value is REJECTED here (the
        // side effect is journalled so a restart replay skips the id); the field is never
        // touched on a reject. The security boundary is SettingWhitelist: there is no
        // reflective set, only a switch over the exact known names.
        private void SetSettingImpl(ParsedCommand cmd)
        {
            string name = ArgOrNull(cmd, "name");
            string value = ArgOrNull(cmd, "value");

            SettingApplyResult result = SettingWhitelist.TryApply(name, value);
            if (!result.Accepted)
            {
                ParsekLog.Warn(Tag,
                    $"setting rejected name={name ?? "(none)"} reason={(result.RejectReason == "setting-not-whitelisted" ? "not-whitelisted" : "value-invalid")} raw={value ?? "(none)"}");
                SetExecResult("REJECTED", null, $"{result.RejectReason} name={name ?? string.Empty}");
                return;
            }

            string oldValue = ReadLiveSettingForLog(result.Name);
            TestCommandSettingApplier.ApplySetting(result, SetLiveSettingField, InvokeRecordMethod);
            string newValue = ReadLiveSettingForLog(result.Name);

            ParsekLog.Info(Tag, $"setting name={result.Name} old={oldValue} new={newValue}");
            SetExecResult("OK", Payload(Kv("name", result.Name), Kv("value", value ?? string.Empty)), null);
        }

        // Sets the live ParsekSettings.Current field for the accepted whitelist name.
        // Guarded by the RequiresGameLoaded precondition (SettingsPresent), but null-safe.
        private void SetLiveSettingField(SettingApplyResult r)
        {
            ParsekSettings s = ParsekSettings.Current;
            if (s == null) return;
            switch (r.Name)
            {
                case "autoRecordOnLaunch": s.autoRecordOnLaunch = r.BoolValue; break;
                case "autoRecordOnEva": s.autoRecordOnEva = r.BoolValue; break;
                case "autoRecordOnFirstModificationAfterSwitch": s.autoRecordOnFirstModificationAfterSwitch = r.BoolValue; break;
                case "autoMerge": s.autoMerge = r.BoolValue; break;
                case "verboseLogging": s.verboseLogging = r.BoolValue; break;
                case "samplingDensity": s.samplingDensity = r.IntValue; break;
                case "ghostAudioVolume": s.ghostAudioVolume = r.FloatValue; break;
                case "transitedBodyRotationModeIndex": s.transitedBodyRotationModeIndex = r.IntValue; break;
                case "ghostRenderTracing": s.ghostRenderTracing = r.BoolValue; break;
                case "mapRenderTracing": s.mapRenderTracing = r.BoolValue; break;
                case "ledgerTracing": s.ledgerTracing = r.BoolValue; break;
                case "writeReadableSidecarMirrors": s.writeReadableSidecarMirrors = r.BoolValue; break;
                case "autoBackupExistingSaves": s.autoBackupExistingSaves = r.BoolValue; break;
                case "showCommittedFutureOverlays": s.showCommittedFutureOverlays = r.BoolValue; break;
                case "blockCommittedActions": s.blockCommittedActions = r.BoolValue; break;
                case "showRouteLines": s.showRouteLines = r.BoolValue; break;
            }
        }

        // Invokes the exact ParsekSettingsPersistence.Record* member for a
        // sidecar-tracked setting (mirrors UI/SettingsWindowUI). All 8 tracked settings
        // are bools, so every Record* takes r.BoolValue.
        private void InvokeRecordMethod(SettingApplyResult r)
        {
            switch (r.RecordMethod)
            {
                case "RecordGhostRenderTracing": ParsekSettingsPersistence.RecordGhostRenderTracing(r.BoolValue); break;
                case "RecordMapRenderTracing": ParsekSettingsPersistence.RecordMapRenderTracing(r.BoolValue); break;
                case "RecordLedgerTracing": ParsekSettingsPersistence.RecordLedgerTracing(r.BoolValue); break;
                case "RecordReadableSidecarMirrors": ParsekSettingsPersistence.RecordReadableSidecarMirrors(r.BoolValue); break;
                case "RecordAutoBackupExistingSaves": ParsekSettingsPersistence.RecordAutoBackupExistingSaves(r.BoolValue); break;
                case "RecordShowCommittedFutureOverlays": ParsekSettingsPersistence.RecordShowCommittedFutureOverlays(r.BoolValue); break;
                case "RecordBlockCommittedActions": ParsekSettingsPersistence.RecordBlockCommittedActions(r.BoolValue); break;
                case "RecordShowRouteLines": ParsekSettingsPersistence.RecordShowRouteLines(r.BoolValue); break;
            }
        }

        // ----- RecordingState (P5.2) -----
        // Read-only recorder/tree snapshot, valid in ANY scene. With no live
        // ParsekFlight (any non-flight scene, or flight not yet ready) the recorder is
        // definitionally not recording, so recording=false with empty tree / zero points.
        private void RecordingStateImpl(ParsedCommand cmd)
        {
            string sceneName = HighLogic.LoadedScene.ToString();
            ParsekFlight flight = ParsekFlight.Instance;
            bool hasFlight = flight != null;
            bool isRecording = false;
            string treeId = null;
            int points = 0;
            if (hasFlight)
            {
                RecorderStateSnapshot snap = flight.CaptureRecorderState();
                isRecording = snap.isRecording;
                treeId = snap.treeId;
                points = snap.bufferedPoints;
            }

            List<KeyValuePair<string, string>> payload =
                TestCommandRecordingState.BuildPayload(hasFlight, isRecording, treeId, points, sceneName);
            ParsekLog.Info(Tag,
                $"recordingstate recording={Bool(hasFlight && isRecording)} tree={(hasFlight ? (treeId ?? string.Empty) : string.Empty)} points={Int(hasFlight ? points : 0)} scene={sceneName}{(hasFlight ? string.Empty : " (no-flight-instance)")}");
            SetExecResult("OK", payload, null);
        }

        // ----- StartRecording / StopRecording (P5.4, C1) -----
        // Both underlying ParsekFlight methods return void, so the payload is derived
        // from recorder-live state sampled around the call (C1). Dispatch guarantees
        // FLIGHT, but the handlers are defensive against a null Instance.
        private void StartRecordingImpl(ParsedCommand cmd)
        {
            bool alreadyLive = ParsekFlight.HasLiveRecorderForTagging();
            ParsekFlight flight = ParsekFlight.Instance;
            if (flight == null)
            {
                ParsekLog.Warn(Tag, "startrecording no-flight-instance");
                SetExecResult("ERROR", null, "no-flight-instance");
                return;
            }

            if (!alreadyLive)
                flight.StartRecording();

            // F4: ParsekFlight.StartRecording is void and can REFUSE (vessel not ready /
            // packed / a guard blocked it) without going live. Re-sample the recorder truth so
            // a refusal reports ERROR msg=start-refused instead of a false OK. (A vessel-ready
            // Defer is the cleaner long-term fix; tracked in the design doc's deferred list.)
            if (!ParsekFlight.HasLiveRecorderForTagging())
            {
                ParsekLog.Warn(Tag, $"startrecording start-refused (recorder not live after StartRecording) already={Bool(alreadyLive)}");
                SetExecResult("ERROR", null, "start-refused");
                return;
            }

            string recordingId = ParsekFlight.GetActiveRecordingIdForTagging();
            ParsekLog.Info(Tag, $"startrecording recordingId={recordingId} already={Bool(alreadyLive)}");
            SetExecResult("OK", TestCommandRecordingVerbs.BuildStartPayload(alreadyLive, recordingId), null);
        }

        private void StopRecordingImpl(ParsedCommand cmd)
        {
            bool wasLive = ParsekFlight.HasLiveRecorderForTagging();
            ParsekFlight flight = ParsekFlight.Instance;
            if (wasLive && flight != null)
                flight.StopRecording();

            ParsekLog.Info(Tag, $"stoprecording stopped={Bool(wasLive)} idle={Bool(!wasLive)}");
            SetExecResult("OK", TestCommandRecordingVerbs.BuildStopPayload(wasLive), null);
        }

        // ----- FlushAndQuit (P5.8) -----
        // If a game is loaded, force a "persistent"-slot save so committed data is durable,
        // THEN schedule Application.Quit deferred one frame. The quit is scheduled by
        // MaybeScheduleQuit only AFTER the response + journal DONE are written and flushed
        // (WriteDoneAndAdvance), so a killed process never loses this command's ack.
        // Deliberately NEVER auto-commits an in-flight recorder (a bare quit never did).
        private void FlushAndQuitImpl(ParsedCommand cmd)
        {
            bool gameLoaded = HighLogic.CurrentGame != null;
            bool saveFolderPresent = !string.IsNullOrEmpty(HighLogic.SaveFolder);
            bool saved = false;

            if (TestCommandFlushAndQuit.ShouldSave(gameLoaded, saveFolderPresent))
            {
                try
                {
                    string result = GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
                    saved = !string.IsNullOrEmpty(result);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(Tag, $"flushandquit save failed: {ex.Message}");
                }
            }

            ParsekLog.Info(Tag, $"flushandquit: saved={Bool(saved)} game-loaded={Bool(gameLoaded)}; quitting");
            pendingQuit = true;
            quitId = cmd.Id;
            SetExecResult("OK", TestCommandFlushAndQuit.BuildPayload(saved), null);
        }

        // ----- LoadGame (P5.7, two-phase boot channel, C2) -----
        // The boot channel: realises the .sfs into a KSP Game and focuses its active
        // vessel via the same Assembly-CSharp-only sequence QuickloadResumeHelpers uses,
        // INCLUDING FlightCameraReloadPin.Arm BEFORE StartAndFocusVessel (stock bug #4803
        // guard). Dispatch already rejected recording-active / load-in-flight. A null /
        // incompatible game or an out-of-range active-vessel index is a single-phase
        // ERROR load-failed; a focusable game enters two-phase (PENDING), completing when
        // the new scene settles with HighLogic.CurrentGame != null. save=<folder> maps to
        // HighLogic.SaveFolder + the saveName param; name=<slot/file> maps to slotName.
        private void LoadGameImpl(ParsedCommand cmd)
        {
            string save = ArgOrNull(cmd, "save");
            string name = ArgOrNull(cmd, "name");
            ParsekLog.Info(Tag,
                $"loadgame start save={save ?? string.Empty} name={name ?? string.Empty} scene={HighLogic.LoadedScene}");

            Game game;
            try
            {
                if (!string.IsNullOrEmpty(save))
                    HighLogic.SaveFolder = save;
                game = GamePersistence.LoadGame(name, save, true, false);
            }
            catch (Exception ex)
            {
                ParsekLog.Error(Tag, $"loadgame failed save={save ?? string.Empty}: {ex.Message}");
                SetExecResult("ERROR", null, "load-failed");
                return;
            }

            bool focusable = TestCommandLoadGame.IsLoadedGameFocusable(
                game != null,
                game != null && game.compatible,
                game != null && game.flightState != null,
                game != null && game.flightState != null && game.flightState.protoVessels != null,
                game != null && game.flightState != null ? game.flightState.activeVesselIdx : -1,
                game != null && game.flightState != null && game.flightState.protoVessels != null
                    ? game.flightState.protoVessels.Count : 0);

            if (!focusable)
            {
                ParsekLog.Error(Tag, $"loadgame failed save={save ?? string.Empty}: game null/incompatible");
                SetExecResult("ERROR", null, "load-failed");
                return;
            }

            int idx = game.flightState.activeVesselIdx;
            FlightCameraReloadPin.Arm($"TestCommandLoadGame:{save}/{name}");
            FlightDriver.StartAndFocusVessel(game, idx);
            loadInFlight = true;
            loadGameSave = save;
            SetExecResult(PendingVerdict, null, null);
        }

        // ----- RunTests (P5.6, two-phase) -----
        // Owns an InGameTestRunner (the pump's IsBatchRunning gate reads it). Initiates
        // RunAll() / RunCategory(category) and returns PendingVerdict; completion is
        // deferred (TryCompleteTwoPhase) until the batch stops running, then reports
        // passed/failed/skipped + the exported results filename.
        private void RunTestsImpl(ParsedCommand cmd)
        {
            string category = ArgOrNull(cmd, "category");
            if (ownedRunner == null)
                ownedRunner = new InGameTestRunner(this);

            if (string.IsNullOrEmpty(category))
                ownedRunner.RunAll();
            else
                ownedRunner.RunCategory(category);

            ParsekLog.Info(Tag, $"runtests start category={category ?? "all"}");
            SetExecResult(PendingVerdict, null, null);
        }

        // ----- CommitTree / DiscardTree (P5.5, C1) -----
        // CommitTree pre-checks HasActiveTree: with no tree it is ERROR msg=no-active-tree
        // (the production CommitTreeFlight guard is only a screen toast, so we mirror it
        // here as a real terminal verdict). DiscardTree with no tree is an idempotent
        // OK nothing=true; otherwise stop a live recorder then discard via the
        // wrong-context entry point with test-command-specific strings.
        private void CommitTreeImpl(ParsedCommand cmd)
        {
            ParsekFlight flight = ParsekFlight.Instance;
            if (flight == null || !flight.HasActiveTree)
            {
                ParsekLog.Warn(Tag, "committree no-active-tree");
                SetExecResult("ERROR", null, "no-active-tree");
                return;
            }

            flight.CommitTreeFlight();
            ParsekLog.Info(Tag, "committree committed=true");
            SetExecResult("OK", TestCommandRecordingVerbs.BuildCommitPayload(), null);
        }

        private void DiscardTreeImpl(ParsedCommand cmd)
        {
            ParsekFlight flight = ParsekFlight.Instance;
            if (flight == null || !flight.HasActiveTree)
            {
                ParsekLog.Info(Tag, "discardtree nothing=true");
                SetExecResult("OK", TestCommandRecordingVerbs.BuildDiscardPayload(hadTree: false), null);
                return;
            }

            if (ParsekFlight.HasLiveRecorderForTagging())
                flight.StopRecording();
            flight.AutoDiscardActiveTreeWithMessage(
                reason: "test-command-discard",
                screenMessage: "Recording discarded (test command)",
                ledgerRecalcReason: "test-command-discard");

            ParsekLog.Info(Tag, "discardtree discarded=true");
            SetExecResult("OK", TestCommandRecordingVerbs.BuildDiscardPayload(hadTree: true), null);
        }

        // ----- MissionMark (P5.3) -----
        // Emits the stable MISSIONMARK correlation line (any scene) and echoes the label.
        private void MissionMarkImpl(ParsedCommand cmd)
        {
            string label = ArgOrNull(cmd, "label") ?? string.Empty;
            TestCommandMissionMark.EmitMark(label, CurrentUt());
            SetExecResult("OK", Payload(Kv("label", label)), null);
        }

        private static string ReadLiveSettingForLog(string name)
        {
            ParsekSettings s = ParsekSettings.Current;
            if (s == null) return "?";
            switch (name)
            {
                case "autoRecordOnLaunch": return Bool(s.autoRecordOnLaunch);
                case "autoRecordOnEva": return Bool(s.autoRecordOnEva);
                case "autoRecordOnFirstModificationAfterSwitch": return Bool(s.autoRecordOnFirstModificationAfterSwitch);
                case "autoMerge": return Bool(s.autoMerge);
                case "verboseLogging": return Bool(s.verboseLogging);
                case "samplingDensity": return Int(s.samplingDensity);
                case "ghostAudioVolume": return s.ghostAudioVolume.ToString("R", CultureInfo.InvariantCulture);
                case "transitedBodyRotationModeIndex": return Int(s.transitedBodyRotationModeIndex);
                case "ghostRenderTracing": return Bool(s.ghostRenderTracing);
                case "mapRenderTracing": return Bool(s.mapRenderTracing);
                case "ledgerTracing": return Bool(s.ledgerTracing);
                case "writeReadableSidecarMirrors": return Bool(s.writeReadableSidecarMirrors);
                case "autoBackupExistingSaves": return Bool(s.autoBackupExistingSaves);
                case "showCommittedFutureOverlays": return Bool(s.showCommittedFutureOverlays);
                case "blockCommittedActions": return Bool(s.blockCommittedActions);
                case "showRouteLines": return Bool(s.showRouteLines);
                default: return "?";
            }
        }

        private void SetExecResult(string verdict, List<KeyValuePair<string, string>> payload, string msg)
        {
            execVerdict = verdict;
            execPayload = payload;
            execMsg = msg;
        }

        private void ClearExecResult()
        {
            execVerdict = "ERROR";
            execPayload = null;
            execMsg = null;
        }

        private void MarkProcessed(string id)
        {
            processedIds.Add(id);
            queuedIds.Remove(id);
        }

        private long NextSeq() => ++responseSeq;

        private static string SessionId() => ParsekProcess.ProcessSessionId.ToString("N");

        private static double? CurrentUt()
        {
            if (HighLogic.CurrentGame == null) return null;
            try { return Planetarium.GetUniversalTime(); }
            catch (Exception) { return null; }
        }

        private bool WriteJournal(string line, string id, string phase)
        {
            bool ok = AppendJournal(line, id);
            if (ok)
            {
                ParsekLog.Verbose(Tag, $"journal id={id} phase={phase}");
                // Defense-in-depth: mirror the durable write into the in-memory phase map so a
                // same-process re-dispatch of this id never reads JournalPhase.None for an
                // already-claimed command (the durable at-most-once record and the live map
                // stay in lock-step without waiting for a restart replay).
                TestCommandJournal.MirrorPhaseIntoMap(journalPhases, id, phase);
            }
            return ok;
        }

        // ----- Small impl helpers -----

        private static string KspRootPath() => KSPUtil.ApplicationRootPath;

        private static int CurrentProcessId()
        {
            try { return Process.GetCurrentProcess().Id; }
            catch (Exception) { return 0; }
        }

        // Wall-clock seconds (Unix epoch, InvariantCulture on the wire) for lock/journal
        // t. NOT a game clock: the lock outlives any single scene and must not depend on
        // Planetarium being present.
        private static double WallClockSeconds() => (DateTime.UtcNow - UnixEpoch).TotalSeconds;

        private static int ReadFully(Stream s, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buffer, total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        // Real pid-liveness probe over System.Diagnostics.Process (the seam the pure
        // DecideLockOwnership consumes; tests inject a fake instead).
        private sealed class ProcessLivenessProbe : IPidLivenessProbe
        {
            public PidLiveness Probe(int pid)
            {
                if (pid <= 0) return PidLiveness.Dead;
                try
                {
                    using (Process proc = Process.GetProcessById(pid))
                    {
                        return proc.HasExited ? PidLiveness.Dead : PidLiveness.Alive;
                    }
                }
                catch (ArgumentException)
                {
                    return PidLiveness.Dead; // no live process carries that id
                }
                catch (InvalidOperationException)
                {
                    return PidLiveness.Dead; // the process has already exited
                }
                catch (Exception)
                {
                    return PidLiveness.Unknown; // could not probe (e.g. access denied)
                }
            }
        }

        void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
                if (armed)
                {
                    GameEvents.onGameSceneLoadRequested.Remove(OnSceneChangeRequested);
                    GameEvents.onLevelWasLoaded.Remove(OnLevelWasLoaded);
                }
            }
        }

        // A scene load has been requested: we are leaving the current scene. Gate the
        // pump off until the new scene loads and settles.
        private void OnSceneChangeRequested(GameScenes scene)
        {
            sceneTransitioning = true;
            settleCounter = 0;
        }

        // The new scene is active: start the settle countdown. Transitioning clears when
        // the counter drains (in Update), a couple of frames into the new scene.
        private void OnLevelWasLoaded(GameScenes scene)
        {
            settleCounter = SettleFrames;
        }

        /// <summary>Human-readable rendering of the env value for the inert log line:
        /// <c>unset</c> for null, <c>empty</c> for the empty string, else the raw value.</summary>
        internal static string FormatEnvForLog(string envValue)
            => envValue == null ? "unset" : (envValue.Length == 0 ? "empty" : envValue);
    }
}
