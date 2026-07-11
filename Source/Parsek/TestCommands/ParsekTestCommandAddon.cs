using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
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
    public class ParsekTestCommandAddon : MonoBehaviour
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

        internal bool IsArmedForTesting => armed;
        internal bool SceneTransitioningForTesting => sceneTransitioning;
        internal int SettleCounterForTesting => settleCounter;
        internal bool DisabledForTesting => disabled;

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
        }

        // ----- Startup: channel paths + lock (P4.3) -----

        // Runs ONCE on the first armed frame. Resolves the four KSP-root channel paths and
        // decides lock ownership; the journal replay + reconcile and offset/processed-set
        // seed are wired on top of this in P4.4.
        private void TryStartup()
        {
            startupDone = true;
            string root = KspRootPath();
            commandFilePath = Path.Combine(root, TestCommandChannelIo.CommandFileName);
            responseFilePath = Path.Combine(root, TestCommandChannelIo.ResponseFileName);
            journalFilePath = Path.Combine(root, TestCommandChannelIo.JournalFileName);
            lockFilePath = Path.Combine(root, TestCommandChannelIo.LockFileName);

            AcquireLock(root);
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
