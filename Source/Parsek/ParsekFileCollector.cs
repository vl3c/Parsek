using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;

namespace Parsek
{
    /// <summary>
    /// The one place that deletes Parsek's own files under <c>Parsek/Recordings</c>,
    /// <c>Parsek/RewindPoints</c> and <c>Parsek/Saves</c> (design-load-restores-the-save.md 4.5).
    /// A file is kept while any save file in the save folder (its <c>*.sfs</c> and
    /// <c>Backup/*.sfs</c>, the referrers) or the in-memory state names it; everything else is
    /// collected. A referrer that cannot be read stops the whole run (fail closed).
    ///
    /// <para>Decision table: a retained version <c>_versions/&lt;id&gt;.&lt;hash&gt;&lt;suffix&gt;</c>
    /// goes when nothing names <c>(id, hash)</c>; a leftover <c>.incoming.</c> backup is filed
    /// first and then judged the same way; a head of an id nothing names is moved to
    /// <c>_quarantine/&lt;yyyy-MM-dd&gt;</c> and deleted from there only once
    /// <see cref="QuarantineGraceDays"/> have passed with nothing naming it again (a file a save
    /// names again is moved back); a <c>Parsek/RewindPoints/&lt;rp&gt;.sfs</c> goes when no
    /// <c>REWIND_POINTS</c> lists it; a <c>Parsek/Saves/parsek_rw_*.sfs</c> goes when nothing names
    /// it as <c>rewindSave</c> / <c>resumeRewindSave</c>; any other <c>Parsek/Saves</c> file
    /// (<c>parsek_career_start.sfs</c>, unknown names) is never collected.</para>
    /// </summary>
    internal static class ParsekFileCollector
    {
        private const string Tag = "FileCollector";
        internal const double InSessionMinIntervalSeconds = 600.0;
        internal const string RewindSavePrefix = "parsek_rw_";
        internal const string BackupDirName = "Backup";
        /// <summary>Days an unnamed recording file waits in <c>_quarantine</c> before it is deleted.</summary>
        internal const int QuarantineGraceDays = 14;
        internal const string QuarantineDateFormat = "yyyy-MM-dd";
        private const string QuarantineDupMarker = ".dup";

        /// <summary>Absolute <c>saves/&lt;folder&gt;</c> directory; null in the game.</summary>
        internal static string SaveDirectoryOverrideForTesting;
        /// <summary>Real-time clock in seconds; null in the game (a process stopwatch).</summary>
        internal static Func<double> RealtimeClockForTesting;
        /// <summary>Opens a referrer; null in the game. Lets a test model a locked file.</summary>
        internal static Func<string, TextReader> OpenReferrerForTesting;
        /// <summary>Extra memory references; null in the game.</summary>
        internal static Func<SaveReferences> ExtraMemoryReferencesForTesting;
        /// <summary>Wall clock (UTC) dating the quarantine folders; null in the game.</summary>
        internal static Func<DateTime> UtcNowForTesting;

        private sealed class CacheEntry
        {
            public long Length;
            public DateTime LastWriteUtc;
            public SaveReferences References;
        }

        private static readonly Dictionary<string, CacheEntry> referrerCache =
            new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        private static readonly HashSet<string> warnedUnreadable =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly Stopwatch processClock = Stopwatch.StartNew();
        private static double lastRunSeconds = double.NaN;

        internal sealed class Result
        {
            public bool Ran;
            public string SkipReason;
            public int Referrers;
            public int Scanned;
            public int Cached;
            public int IncomingFiled;
            public int VersionsDeleted;
            public int HeadsQuarantined;
            public int QuarantineRestored;
            public int QuarantinePurged;
            public int QuarantineDated;
            public int TransientDeleted;
            public int RewindPointFilesDeleted;
            public int RewindSavesDeleted;
            public int Failures;
            public long BytesFreed;
            public long Milliseconds;
        }

        internal static void ResetForTesting()
        {
            referrerCache.Clear();
            warnedUnreadable.Clear();
            lastRunSeconds = double.NaN;
            SaveDirectoryOverrideForTesting = null;
            RealtimeClockForTesting = null;
            OpenReferrerForTesting = null;
            ExtraMemoryReferencesForTesting = null;
            UtcNowForTesting = null;
        }

        internal static int CachedReferrerCountForTesting => referrerCache.Count;

        private static double NowSeconds()
        {
            var clock = RealtimeClockForTesting;
            return clock != null ? clock() : processClock.Elapsed.TotalSeconds;
        }

        private static DateTime UtcNow()
        {
            var clock = UtcNowForTesting;
            return clock != null ? clock() : DateTime.UtcNow;
        }

        /// <summary>
        /// Pure: why an OnLoad collection is skipped, or null to run. A rewind, a Re-Fly start
        /// and a Discard Re-fly load hand memory's lists across a load the collector must not
        /// judge mid-flight; an open merge journal means memory is between two durable states.
        /// In-session loads run at most once per <see cref="InSessionMinIntervalSeconds"/>.
        /// </summary>
        internal static string DecideSkipAtLoad(
            EarlyLoadKind kind, bool coldBranch, bool rewinding, bool journalOpen,
            double nowSeconds, double lastRun)
        {
            if (rewinding || kind == EarlyLoadKind.PlainRewind)
                return "rewind";
            if (kind == EarlyLoadKind.ReFlyStart)
                return "refly-start";
            if (kind == EarlyLoadKind.DiscardReFly)
                return "discard-refly";
            if (journalOpen)
                return "journal-open";
            if (!coldBranch && !double.IsNaN(lastRun)
                && nowSeconds - lastRun < InSessionMinIntervalSeconds)
                return "rate-limited";
            return null;
        }

        /// <summary>
        /// OnLoad entry point, called after <c>LoadTimeSweep.Run</c> and
        /// <c>RewindPointReaper.ReapOrphanedRPs</c> so memory's lists are final.
        /// </summary>
        internal static Result RunAtLoad(EarlyLoadKind kind, bool coldBranch, ParsekScenario scenario)
        {
            string skip = DecideSkipAtLoad(
                kind, coldBranch, RewindContext.IsRewinding,
                !object.ReferenceEquals(scenario, null) && scenario.ActiveMergeJournal != null,
                NowSeconds(), lastRunSeconds);
            if (skip != null)
            {
                ParsekLog.Verbose(Tag, $"Collection skipped reason={skip} load={kind} cold={coldBranch}");
                return new Result { SkipReason = skip };
            }
            try
            {
                return Run(coldBranch ? "cold-load" : "in-session-load", scenario);
            }
            catch (Exception ex)
            {
                // Collection is housekeeping: a failure must never abort the load tail.
                ParsekLog.Warn(Tag,
                    $"Collection aborted: {ex.GetType().Name}:{ex.Message} - files left for the next run");
                return new Result { SkipReason = "exception" };
            }
        }

        internal static Result Run(string reason)
        {
            return Run(reason, ParsekScenario.Instance);
        }

        internal static Result Run(string reason, ParsekScenario scenario)
        {
            var sw = Stopwatch.StartNew();
            var result = new Result();
            string saveDir = ResolveSaveDirectory();
            if (string.IsNullOrEmpty(saveDir) || !Directory.Exists(saveDir))
            {
                result.SkipReason = "no-save-context";
                ParsekLog.Verbose(Tag, $"Collection skipped reason=no-save-context run={reason}");
                return result;
            }

            // 1. Referrers. Any unreadable one stops the run before anything is touched.
            var references = new SaveReferences();
            if (!ScanReferrers(saveDir, references, result, out string unreadablePath, out string unreadableDetail))
            {
                result.SkipReason = "unreadable-referrer";
                string message = $"Collection skipped reason=unreadable-referrer path={unreadablePath} " +
                    $"detail={unreadableDetail} run={reason} - nothing deleted, retried on the next run";
                if (warnedUnreadable.Add(unreadablePath))
                    ParsekLog.Warn(Tag, message);
                else
                    ParsekLog.Verbose(Tag, message);
                return result;
            }

            // 2. Memory.
            SaveReferences memory = BuildMemoryReferences(scenario);
            references.UnionWith(memory);
            var extra = ExtraMemoryReferencesForTesting;
            if (extra != null)
                references.UnionWith(extra());

            string recordingsDir = Path.Combine(saveDir, "Parsek", "Recordings");
            string versionsDir = Path.Combine(recordingsDir, SidecarVersions.VersionsDirName);

            // Second guard (the old CleanOrphanFiles refusal): neither memory nor any save file
            // names a recording but heads exist. That is the "load lost its tree state"
            // fingerprint; touch nothing. Memory alone is no test: a load of a save made
            // before any flight was recorded holds 0 recordings while the save names its heads.
            if (references.RecordingIds.Count == 0)
            {
                int diskIds = CountHeadIds(recordingsDir, out int fileCount)
                    + CountQuarantinedIds(QuarantineDirectory(recordingsDir), ref fileCount);
                if (diskIds > 0)
                {
                    result.SkipReason = "zero-known-ids";
                    ParsekLog.Warn(Tag,
                        $"Collection skipped reason=zero-known-ids: REFUSING to delete - memory and every save " +
                        $"file report 0 known recording IDs but disk has {diskIds} sidecar-shaped recording ID(s) " +
                        $"({fileCount} file(s) total). Either every save predates every recording on disk (a save " +
                        $"made before the first committed flight was loaded) or the scenario load lost its tree " +
                        $"state. Files preserved; a later collection that sees a named recording reaps them.");
                    return result;
                }
            }

            // 3. Recordings: incoming first, then versions, then heads and artifacts.
            FileIncomingBackups(versionsDir, result);
            CollectVersions(versionsDir, references, result);
            CollectRecordingHeads(recordingsDir, references, result);
            SweepQuarantine(recordingsDir, references, result);

            // 4. Rewind point files and rewind saves.
            CollectRewindPointFiles(Path.Combine(saveDir, "Parsek", "RewindPoints"), references, result);
            CollectRewindSaves(Path.Combine(saveDir, "Parsek", "Saves"), references, result);

            result.Ran = true;
            result.Milliseconds = sw.ElapsedMilliseconds;
            lastRunSeconds = NowSeconds();
            ParsekLog.Info(Tag,
                $"Collection: referrers={result.Referrers} scanned={result.Scanned} cached={result.Cached} " +
                $"incomingFiled={result.IncomingFiled} versionsDeleted={result.VersionsDeleted} " +
                $"headsQuarantined={result.HeadsQuarantined} quarantineRestored={result.QuarantineRestored} " +
                $"quarantinePurged={result.QuarantinePurged} quarantineDated={result.QuarantineDated} " +
                $"transientDeleted={result.TransientDeleted} " +
                $"rewindPointFilesDeleted={result.RewindPointFilesDeleted} " +
                $"rewindSavesDeleted={result.RewindSavesDeleted} failures={result.Failures} " +
                $"bytesFreed={result.BytesFreed} ms={result.Milliseconds} reason={reason}");
            return result;
        }

        internal static string ResolveSaveDirectory()
        {
            if (!string.IsNullOrEmpty(SaveDirectoryOverrideForTesting))
                return Path.GetFullPath(SaveDirectoryOverrideForTesting);
            if (!string.IsNullOrEmpty(RecordingPaths.SaveRootOverrideForTesting))
                return Path.GetFullPath(RecordingPaths.SaveRootOverrideForTesting);
            string recordingsOverride = RecordingStore.CleanOrphanFilesDirectoryOverrideForTesting;
            if (!string.IsNullOrEmpty(recordingsOverride))
            {
                string parsekDir = Path.GetDirectoryName(Path.GetFullPath(recordingsOverride));
                return string.IsNullOrEmpty(parsekDir) ? null : Path.GetDirectoryName(parsekDir);
            }
            try
            {
                string root = KSPUtil.ApplicationRootPath ?? "";
                string folder = HighLogic.SaveFolder ?? "";
                if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(folder))
                    return null;
                return Path.GetFullPath(Path.Combine(root, "saves", folder));
            }
            catch
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- referrers

        internal static List<string> ListReferrers(string saveDir)
        {
            var paths = new List<string>();
            AddSfsFiles(saveDir, paths);
            AddSfsFiles(Path.Combine(saveDir, BackupDirName), paths);
            paths.Sort(StringComparer.Ordinal);
            return paths;
        }

        private static void AddSfsFiles(string dir, List<string> paths)
        {
            if (!Directory.Exists(dir))
                return;
            string[] files = Directory.GetFiles(dir, "*.sfs");
            for (int i = 0; i < files.Length; i++)
            {
                if (files[i].EndsWith(".sfs", StringComparison.OrdinalIgnoreCase))
                    paths.Add(Path.GetFullPath(files[i]));
            }
        }

        private static bool ScanReferrers(
            string saveDir, SaveReferences references, Result result,
            out string unreadablePath, out string unreadableDetail)
        {
            unreadablePath = null;
            unreadableDetail = null;
            List<string> referrers;
            try
            {
                referrers = ListReferrers(saveDir);
            }
            catch (Exception ex)
            {
                unreadablePath = saveDir;
                unreadableDetail = "list-failed:" + ex.GetType().Name + ":" + ex.Message;
                return false;
            }

            result.Referrers = referrers.Count;
            for (int i = 0; i < referrers.Count; i++)
            {
                string path = referrers[i];
                if (!TryGetReferrerReferences(path, result, out SaveReferences refs, out string failure))
                {
                    unreadablePath = path;
                    unreadableDetail = failure;
                    return false;
                }
                references.UnionWith(refs);
            }
            return true;
        }

        private static bool TryGetReferrerReferences(
            string path, Result result, out SaveReferences refs, out string failure)
        {
            refs = null;
            failure = null;
            long length;
            DateTime lastWrite;
            try
            {
                var info = new FileInfo(path);
                length = info.Length;
                lastWrite = info.LastWriteTimeUtc;
            }
            catch (Exception ex)
            {
                failure = "stat-failed:" + ex.GetType().Name + ":" + ex.Message;
                return false;
            }

            if (referrerCache.TryGetValue(path, out CacheEntry cached)
                && cached.Length == length && cached.LastWriteUtc == lastWrite)
            {
                refs = cached.References;
                result.Cached++;
                return true;
            }

            try
            {
                var open = OpenReferrerForTesting;
                using (TextReader reader = open != null
                    ? open(path)
                    : new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)))
                {
                    if (!SaveReferenceScanner.TryScan(reader, out refs, out string scanFailure))
                    {
                        referrerCache.Remove(path);
                        failure = "scan-failed:" + scanFailure;
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                referrerCache.Remove(path);
                failure = "open-failed:" + ex.GetType().Name + ":" + ex.Message;
                return false;
            }

            referrerCache[path] = new CacheEntry { Length = length, LastWriteUtc = lastWrite, References = refs };
            result.Scanned++;
            return true;
        }

        // ---------------------------------------------------------------- memory

        /// <summary>
        /// Everything the running game holds: every recording the store knows (committed list,
        /// committed trees, pending tree, the pending tree saved during an active restore), the
        /// recordings of committed trees kept inert (<see cref="InertRecordingTrees"/>), the
        /// flight scene's active tree and live recorder, the scenario's rewind point list and
        /// <see cref="ParsekScenario.pendingActiveTreeResumeRewindSave"/>.
        /// </summary>
        internal static SaveReferences BuildMemoryReferences(ParsekScenario scenario)
        {
            var refs = new SaveReferences();
            RecordingStore.ForEachRecordingKnownToMemory(rec => AddRecording(refs, rec));
            // GC-3: committed trees kept inert name their files exactly as their save node does.
            InertRecordingTrees.AddReferencesTo(refs);
            AddFlightSceneReferences(refs);

            // Reference test: a scenario built outside Unity compares equal to null.
            if (!object.ReferenceEquals(scenario, null) && scenario.RewindPoints != null)
            {
                for (int i = 0; i < scenario.RewindPoints.Count; i++)
                {
                    var rp = scenario.RewindPoints[i];
                    if (rp != null)
                        refs.AddRewindPoint(rp.RewindPointId, rp.QuicksaveFilename);
                }
            }

            refs.AddRewindSave(ParsekScenario.pendingActiveTreeResumeRewindSave);
            return refs;
        }

        private static void AddRecording(SaveReferences refs, Recording rec)
        {
            if (rec == null)
                return;
            refs.AddRecording(rec.RecordingId, rec.PrecHash, rec.VesselSnapshotHash, rec.GhostSnapshotHash);
            refs.AddRewindSave(rec.RewindSaveFileName);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AddFlightSceneReferences(SaveReferences refs)
        {
            try
            {
                var flight = ParsekFlight.Instance;
                if (flight == null)
                    return;
                var tree = flight.ActiveTreeForSerialization;
                if (tree != null && tree.Recordings != null)
                {
                    foreach (var kvp in tree.Recordings)
                        AddRecording(refs, kvp.Value);
                }
                var recorder = flight.ActiveRecorderForSerialization;
                if (recorder != null)
                {
                    refs.AddRewindSave(recorder.RewindSaveFileName);
                    AddRecording(refs, recorder.CaptureAtStop);
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag, $"Flight-scene references unavailable: {ex.GetType().Name}");
            }
        }

        // ---------------------------------------------------------------- recordings

        private static int CountHeadIds(string recordingsDir, out int fileCount)
        {
            fileCount = 0;
            if (!Directory.Exists(recordingsDir))
                return 0;
            string[] files;
            try { files = Directory.GetFiles(recordingsDir); }
            catch { return 0; }
            fileCount = files.Length;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < files.Length; i++)
            {
                string fileName = Path.GetFileName(files[i]);
                if (RecordingStore.IsTransientSidecarArtifactFile(fileName)
                    || RecordingStore.IsLegacySidecarFile(fileName))
                    continue;
                string id = RecordingStore.ExtractRecordingIdFromFileName(fileName);
                if (!string.IsNullOrEmpty(id))
                    ids.Add(id);
            }
            return ids.Count;
        }

        private static int CountQuarantinedIds(string quarantineDir, ref int fileCount)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var files = ListQuarantinedFiles(quarantineDir);
                fileCount += files.Count;
                for (int i = 0; i < files.Count; i++)
                {
                    string id = RecordingStore.ExtractRecordingIdFromFileName(
                        QuarantinedHeadFileName(Path.GetFileName(files[i].Key)));
                    if (!string.IsNullOrEmpty(id))
                        ids.Add(id);
                }
            }
            catch { }
            return ids.Count;
        }

        private static void FileIncomingBackups(string versionsDir, Result result)
        {
            if (!Directory.Exists(versionsDir))
                return;
            string[] files = Directory.GetFiles(versionsDir);
            for (int i = 0; i < files.Length; i++)
            {
                string fileName = Path.GetFileName(files[i]);
                if (!SidecarVersions.TryParseIncomingFileName(fileName, out string id, out SidecarKind kind))
                    continue;
                string hash = null;
                try
                {
                    hash = SidecarContentHash.ComputeFile(files[i]);
                    string target = Path.Combine(versionsDir, SidecarVersions.VersionFileName(id, hash, kind));
                    string outcome;
                    if (File.Exists(target))
                    {
                        File.Delete(files[i]);
                        outcome = "identical-exists";
                    }
                    else
                    {
                        File.Move(files[i], target);
                        outcome = "filed";
                    }
                    result.IncomingFiled++;
                    ParsekLog.Verbose(Tag,
                        $"Incoming backup filed: rec={id} kind={SidecarVersions.Label(kind)} hash={hash} outcome={outcome}");
                }
                catch (Exception ex)
                {
                    result.Failures++;
                    ParsekLog.Warn(Tag,
                        $"Incoming backup filing failed: file={fileName} hash={hash ?? "<unknown>"} " +
                        $"ex={ex.GetType().Name}:{ex.Message} - left for the next run");
                }
            }
        }

        private static void CollectVersions(string versionsDir, SaveReferences references, Result result)
        {
            if (!Directory.Exists(versionsDir))
                return;
            string[] files = Directory.GetFiles(versionsDir);
            int kept = 0;
            for (int i = 0; i < files.Length; i++)
            {
                string fileName = Path.GetFileName(files[i]);
                if (!SidecarVersions.TryParseVersionFileName(fileName, out string id, out string hash, out SidecarKind kind))
                    continue;
                if (references.NamesVersion(id, hash))
                {
                    kept++;
                    continue;
                }
                if (TryDelete(files[i], result))
                {
                    result.VersionsDeleted++;
                    ParsekLog.Verbose(Tag,
                        $"Version collected: rec={id} kind={SidecarVersions.Label(kind)} hash={hash}");
                }
            }
            ParsekLog.Verbose(Tag, $"Versions judged: kept={kept} deleted={result.VersionsDeleted}");
        }

        private static void CollectRecordingHeads(string recordingsDir, SaveReferences references, Result result)
        {
            if (!Directory.Exists(recordingsDir))
                return;
            string[] files = Directory.GetFiles(recordingsDir);
            int skippedUnrecognized = 0;
            for (int i = 0; i < files.Length; i++)
            {
                string fileName = Path.GetFileName(files[i]);
                string id = RecordingStore.ExtractRecordingIdFromFileName(fileName);
                if (id == null)
                {
                    // Legacy (.pcrf) and transient (.tmp / .stage / .bak) artifacts carry no
                    // recorded data: deleted outright, as before.
                    if (RecordingStore.IsLegacySidecarFile(fileName)
                        || RecordingStore.IsTransientSidecarArtifactFile(fileName))
                    {
                        if (TryDelete(files[i], result))
                            result.TransientDeleted++;
                    }
                    else
                    {
                        skippedUnrecognized++;
                    }
                    continue;
                }

                if (references.RecordingIds.Contains(id))
                    continue;
                if (QuarantineOrphanRecordingFile(files[i], recordingsDir, fileName, id, result))
                    result.HeadsQuarantined++;
            }
            if (skippedUnrecognized > 0)
                ParsekLog.Verbose(Tag, $"Recordings: skipped {skippedUnrecognized} unrecognized file(s)");
        }

        /// <summary>
        /// Moves an unnamed head into <c>_quarantine</c>, never overwriting an earlier copy.
        /// The bulk data stays recoverable even when "nothing names it" was a state bug.
        /// </summary>
        private static bool QuarantineOrphanRecordingFile(
            string filePath, string recordingsDir, string fileName, string id, Result result)
        {
            try
            {
                string datedDir = TodayQuarantineDirectory(recordingsDir);
                MoveIntoQuarantineFolder(filePath, datedDir, fileName);
                ParsekLog.Verbose(Tag,
                    $"Quarantined orphan recording file: {fileName} (id={id}) -> " +
                    $"{RecordingStore.OrphanQuarantineDirName}/{Path.GetFileName(datedDir)}/");
                return true;
            }
            catch (Exception ex)
            {
                result.Failures++;
                ParsekLog.Warn(Tag,
                    $"Failed to quarantine orphan recording file '{fileName}': {ex.Message} (left in place, NOT deleted)");
                return false;
            }
        }

        // ---------------------------------------------------------------- quarantine

        private static string QuarantineDirectory(string recordingsDir)
        {
            return Path.Combine(recordingsDir, RecordingStore.OrphanQuarantineDirName);
        }

        internal static string TodayQuarantineDirectory(string recordingsDir)
        {
            return Path.Combine(QuarantineDirectory(recordingsDir),
                UtcNow().ToString(QuarantineDateFormat, CultureInfo.InvariantCulture));
        }

        /// <summary>Moves a file into a quarantine folder, never overwriting an earlier copy.</summary>
        private static void MoveIntoQuarantineFolder(string filePath, string folder, string fileName)
        {
            Directory.CreateDirectory(folder);
            string dest = Path.Combine(folder, fileName);
            if (File.Exists(dest))
            {
                dest = Path.Combine(
                    folder,
                    fileName + QuarantineDupMarker + Guid.NewGuid().ToString("N").Substring(0, 8));
            }
            File.Move(filePath, dest);
        }

        /// <summary>
        /// Pure: the head file name a quarantined file was moved from (the
        /// <c>.dup&lt;8 hex&gt;</c> collision suffix stripped).
        /// </summary>
        internal static string QuarantinedHeadFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return fileName;
            int marker = fileName.LastIndexOf(QuarantineDupMarker, StringComparison.Ordinal);
            if (marker > 0 && fileName.Length - marker == QuarantineDupMarker.Length + 8)
            {
                bool hex = true;
                for (int i = marker + QuarantineDupMarker.Length; i < fileName.Length && hex; i++)
                    hex = Uri.IsHexDigit(fileName[i]);
                if (hex)
                    return fileName.Substring(0, marker);
            }
            return fileName;
        }

        /// <summary>Pure: parses a quarantine folder name (<c>yyyy-MM-dd</c>, UTC).</summary>
        internal static bool TryParseQuarantineFolderDate(string folderName, out DateTime date)
        {
            return DateTime.TryParseExact(
                folderName, QuarantineDateFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date);
        }

        /// <summary>
        /// Pure: whether a quarantine folder dated <paramref name="folderDate"/> has waited out
        /// the grace period on <paramref name="nowUtc"/> (whole UTC days).
        /// </summary>
        internal static bool IsQuarantineExpired(DateTime folderDate, DateTime nowUtc)
        {
            return nowUtc.Date >= folderDate.Date.AddDays(QuarantineGraceDays);
        }

        /// <summary>
        /// Moves a quarantined file back to its head position. A head that already exists is
        /// never replaced; the quarantined copy then stays where it is.
        /// </summary>
        private static bool TryRestoreQuarantinedFile(
            string quarantinedPath, string recordingsDir, string headName, string id, string reason,
            out bool headExists)
        {
            headExists = false;
            string headPath = Path.Combine(recordingsDir, headName);
            if (File.Exists(headPath))
            {
                headExists = true;
                ParsekLog.Verbose(Tag,
                    $"Quarantined file named again but its head exists: {headName} (id={id}) - copy left in quarantine");
                return false;
            }
            try
            {
                File.Move(quarantinedPath, headPath);
                ParsekLog.Info(Tag,
                    $"Restored quarantined recording file: {headName} (id={id}) reason={reason}");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"Failed to restore quarantined recording file '{headName}': {ex.GetType().Name}:{ex.Message} " +
                    $"(left in quarantine)");
                return false;
            }
        }

        /// <summary>
        /// Moves back every quarantined head whose recording <paramref name="named"/> names. When
        /// several copies of one head wait (quarantined on different days), the copy whose bytes
        /// carry a hash <paramref name="named"/> names for that recording wins, else the newest
        /// (latest dated folder, an undated copy last). The other copies stay in quarantine.
        /// </summary>
        private static void RestoreNamedQuarantinedFiles(
            string recordingsDir, List<KeyValuePair<string, string>> files, SaveReferences named,
            string reason, out int restored, out int headExists)
        {
            restored = 0;
            headExists = 0;
            var candidates = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.Ordinal);
            var headOrder = new List<string>();
            for (int i = 0; i < files.Count; i++)
            {
                string headName = QuarantinedHeadFileName(Path.GetFileName(files[i].Key));
                string id = RecordingStore.ExtractRecordingIdFromFileName(headName);
                if (id == null || !named.RecordingIds.Contains(id))
                    continue;
                if (!candidates.TryGetValue(headName, out var list))
                {
                    list = new List<KeyValuePair<string, string>>();
                    candidates[headName] = list;
                    headOrder.Add(headName);
                }
                list.Add(files[i]);
            }

            for (int h = 0; h < headOrder.Count; h++)
            {
                string headName = headOrder[h];
                string id = RecordingStore.ExtractRecordingIdFromFileName(headName);
                var list = candidates[headName];
                // Newest first: dated folders descending, an undated copy last.
                list.Sort((a, b) =>
                {
                    if (a.Value == null) return b.Value == null ? 0 : 1;
                    if (b.Value == null) return -1;
                    return string.CompareOrdinal(b.Value, a.Value);
                });
                string pick = list[0].Key;
                if (list.Count > 1)
                {
                    for (int c = 0; c < list.Count; c++)
                    {
                        string hash = null;
                        try { hash = SidecarContentHash.ComputeFile(list[c].Key); }
                        catch (Exception) { }
                        if (hash != null && named.NamesVersion(id, hash))
                        {
                            pick = list[c].Key;
                            break;
                        }
                    }
                }
                if (TryRestoreQuarantinedFile(pick, recordingsDir, headName, id, reason, out bool exists))
                    restored++;
                else if (exists)
                    headExists++;
            }
        }

        /// <summary>
        /// Every file in <c>_quarantine</c>: flat files (quarantined before the folders were
        /// dated) and files in <c>yyyy-MM-dd</c> folders. Other subfolders are not Parsek's.
        /// </summary>
        private static List<KeyValuePair<string, string>> ListQuarantinedFiles(string quarantineDir)
        {
            var files = new List<KeyValuePair<string, string>>();
            if (!Directory.Exists(quarantineDir))
                return files;
            foreach (string flat in Directory.GetFiles(quarantineDir))
                files.Add(new KeyValuePair<string, string>(flat, null));
            string[] dirs = Directory.GetDirectories(quarantineDir);
            Array.Sort(dirs, StringComparer.Ordinal);
            for (int d = 0; d < dirs.Length; d++)
            {
                string folderName = Path.GetFileName(dirs[d]);
                if (!TryParseQuarantineFolderDate(folderName, out _))
                    continue;
                foreach (string dated in Directory.GetFiles(dirs[d]))
                    files.Add(new KeyValuePair<string, string>(dated, folderName));
            }
            return files;
        }

        /// <summary>
        /// Collection step after the heads: a quarantined file some save or memory names again
        /// goes back to its head position; a flat (undated) one is filed into today's folder so
        /// its grace period starts now; a file in a folder older than
        /// <see cref="QuarantineGraceDays"/> that nothing names is deleted; empty dated folders go.
        /// </summary>
        private static void SweepQuarantine(string recordingsDir, SaveReferences references, Result result)
        {
            string quarantineDir = QuarantineDirectory(recordingsDir);
            if (!Directory.Exists(quarantineDir))
                return;
            DateTime now = UtcNow();
            string todayDir = TodayQuarantineDirectory(recordingsDir);
            int keptWaiting = 0;
            int keptNamed = 0;
            int unrecognized = 0;
            RestoreNamedQuarantinedFiles(recordingsDir, ListQuarantinedFiles(quarantineDir), references,
                "named-by-save-or-memory", out int restored, out _);
            result.QuarantineRestored += restored;
            var files = ListQuarantinedFiles(quarantineDir);
            for (int i = 0; i < files.Count; i++)
            {
                string path = files[i].Key;
                string folderName = files[i].Value;
                string fileName = Path.GetFileName(path);
                string headName = QuarantinedHeadFileName(fileName);
                string id = RecordingStore.ExtractRecordingIdFromFileName(headName);
                if (id == null)
                {
                    unrecognized++;
                    continue;
                }

                if (references.RecordingIds.Contains(id))
                {
                    // Named but not moved back (its head exists, or another copy won): never purged.
                    keptNamed++;
                    continue;
                }

                if (folderName == null)
                {
                    try
                    {
                        MoveIntoQuarantineFolder(path, todayDir, fileName);
                        result.QuarantineDated++;
                    }
                    catch (Exception ex)
                    {
                        result.Failures++;
                        ParsekLog.Warn(Tag,
                            $"Failed to date quarantined file '{fileName}': {ex.GetType().Name}:{ex.Message} - left for the next run");
                    }
                    continue;
                }

                TryParseQuarantineFolderDate(folderName, out DateTime folderDate);
                if (!IsQuarantineExpired(folderDate, now))
                {
                    keptWaiting++;
                    continue;
                }
                if (TryDelete(path, result))
                {
                    result.QuarantinePurged++;
                    ParsekLog.Verbose(Tag,
                        $"Quarantined recording file purged: {headName} (id={id}) quarantined={folderName} " +
                        $"graceDays={QuarantineGraceDays}");
                }
            }

            RemoveEmptyQuarantineFolders(quarantineDir);
            ParsekLog.Verbose(Tag,
                $"Quarantine judged: restored={result.QuarantineRestored} purged={result.QuarantinePurged} " +
                $"dated={result.QuarantineDated} waiting={keptWaiting} namedKept={keptNamed} " +
                $"unrecognized={unrecognized}");
        }

        private static void RemoveEmptyQuarantineFolders(string quarantineDir)
        {
            foreach (string dir in Directory.GetDirectories(quarantineDir))
            {
                if (!TryParseQuarantineFolderDate(Path.GetFileName(dir), out _))
                    continue;
                try
                {
                    if (Directory.GetFileSystemEntries(dir).Length == 0)
                        Directory.Delete(dir);
                }
                catch (Exception ex)
                {
                    ParsekLog.Verbose(Tag,
                        $"Empty quarantine folder not removed: {Path.GetFileName(dir)} {ex.GetType().Name}");
                }
            }
        }

        /// <summary>
        /// Cold-load step before the recordings hydrate: every quarantined file whose recording
        /// the loaded save names goes back to its head position, so a save brought back into the
        /// folder (copied in, or restored from <c>Backup/</c>) loads whole. Housekeeping: a
        /// failure never aborts the load.
        /// </summary>
        internal static int RestoreQuarantinedNamedBy(ConfigNode scenarioNode)
        {
            try
            {
                string saveDir = ResolveSaveDirectory();
                if (string.IsNullOrEmpty(saveDir) || scenarioNode == null)
                    return 0;
                string recordingsDir = Path.Combine(saveDir, "Parsek", "Recordings");
                string quarantineDir = QuarantineDirectory(recordingsDir);
                if (!Directory.Exists(quarantineDir))
                    return 0;

                var named = new SaveReferences();
                CollectRecordingReferences(scenarioNode, named);
                if (named.RecordingIds.Count == 0)
                    return 0;

                RestoreNamedQuarantinedFiles(recordingsDir, ListQuarantinedFiles(quarantineDir), named,
                    "named-by-loaded-save", out int restored, out int headExists);
                if (restored > 0 || headExists > 0)
                {
                    RemoveEmptyQuarantineFolders(quarantineDir);
                    ParsekLog.Info(Tag,
                        $"Quarantine restore at load: restored={restored} namedHeadExists={headExists} " +
                        $"namedRecordings={named.RecordingIds.Count}");
                }
                return restored;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"Quarantine restore at load aborted: {ex.GetType().Name}:{ex.Message} - files left in quarantine");
                return 0;
            }
        }

        /// <summary>
        /// Pure: every <c>recordingId</c> (with its three hashes) of a <c>RECORDING</c> node at
        /// any depth (the scanner's rule).
        /// </summary>
        internal static void CollectRecordingReferences(ConfigNode node, SaveReferences refs)
        {
            if (node == null)
                return;
            ConfigNode[] children = node.GetNodes();
            for (int i = 0; i < children.Length; i++)
            {
                ConfigNode child = children[i];
                if (string.Equals(child.name, "RECORDING", StringComparison.Ordinal))
                {
                    refs.AddRecording(
                        child.GetValue("recordingId"),
                        child.GetValue("precHash"),
                        child.GetValue("vesselHash"),
                        child.GetValue("ghostHash"));
                }
                CollectRecordingReferences(child, refs);
            }
        }

        // ---------------------------------------------------------------- rewind files

        private static void CollectRewindPointFiles(string rewindPointsDir, SaveReferences references, Result result)
        {
            if (!Directory.Exists(rewindPointsDir))
                return;
            string[] files = Directory.GetFiles(rewindPointsDir, "*.sfs");
            for (int i = 0; i < files.Length; i++)
            {
                string fileName = Path.GetFileName(files[i]);
                if (!fileName.EndsWith(".sfs", StringComparison.OrdinalIgnoreCase))
                    continue;
                string rpId = fileName.Substring(0, fileName.Length - 4);
                if (references.RewindPointIds.Contains(rpId))
                    continue;
                if (TryDelete(files[i], result))
                {
                    result.RewindPointFilesDeleted++;
                    ParsekLog.Verbose(Tag, $"Rewind point file collected: rp={rpId}");
                }
            }
        }

        /// <summary>Pure: whether a <c>Parsek/Saves</c> file name is ever collectable.</summary>
        internal static bool IsCollectableRewindSaveName(string fileName)
        {
            return !string.IsNullOrEmpty(fileName)
                && fileName.StartsWith(RewindSavePrefix, StringComparison.Ordinal)
                && fileName.EndsWith(".sfs", StringComparison.OrdinalIgnoreCase)
                && fileName.Length > RewindSavePrefix.Length + 4;
        }

        private static void CollectRewindSaves(string savesDir, SaveReferences references, Result result)
        {
            if (!Directory.Exists(savesDir))
                return;
            string[] files = Directory.GetFiles(savesDir);
            for (int i = 0; i < files.Length; i++)
            {
                string fileName = Path.GetFileName(files[i]);
                if (!IsCollectableRewindSaveName(fileName))
                    continue;
                string stem = fileName.Substring(0, fileName.Length - 4);
                if (references.RewindSaveNames.Contains(stem) || references.RewindSaveNames.Contains(fileName))
                    continue;
                if (TryDelete(files[i], result))
                {
                    result.RewindSavesDeleted++;
                    ParsekLog.Verbose(Tag, $"Rewind save collected: {fileName}");
                }
            }
        }

        private static bool TryDelete(string path, Result result)
        {
            try
            {
                long length = new FileInfo(path).Length;
                File.Delete(path);
                result.BytesFreed += length;
                return true;
            }
            catch (Exception ex)
            {
                result.Failures++;
                ParsekLog.Warn(Tag,
                    $"Failed to collect '{Path.GetFileName(path)}': {ex.GetType().Name}:{ex.Message} - left for the next run");
                return false;
            }
        }
    }
}
