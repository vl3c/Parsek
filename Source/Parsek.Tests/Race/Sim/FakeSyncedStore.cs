using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Tests.Race.Sim
{
    /// <summary>
    /// How one machine's sync client behaves. Delay and reorder apply to that machine's
    /// uploads and downloads; partial and placeholder apply to its downloads. Chances are in
    /// thousandths: 0 never draws, 1000 always fires without a draw.
    /// </summary>
    internal sealed class FakeSyncPolicy
    {
        public int MinDelayTicks { get; set; }
        public int MaxDelayTicks { get; set; }
        public int ReorderPerMille { get; set; }
        public int PartialPerMille { get; set; }
        public int PlaceholderPerMille { get; set; }

        internal void Validate()
        {
            if (MinDelayTicks < 0 || MaxDelayTicks < MinDelayTicks)
                throw new InvalidOperationException("FakeSyncPolicy: need 0 <= MinDelayTicks <= MaxDelayTicks");
        }
    }

    /// <summary>What a machine's directory listing shows for one file.</summary>
    internal struct SyncedFileInfo
    {
        public string Path;
        /// <summary>Bytes a read returns now; a placeholder reports its full size, as clients do.</summary>
        public int Length;
        public bool IsPlaceholder;
    }

    /// <summary>
    /// An in-memory model of a cloud-synced shared folder: one authoritative cloud plus one
    /// view per machine. A machine's write lands in its own view at once and reaches the cloud
    /// and the other views only through queued deliveries, which <see cref="Advance"/> /
    /// <see cref="DeliverDue"/> apply. Controls: delay, reorder, drop until reconnect (an
    /// offline machine), partial content, placeholder files, conflict copies, foreign edits,
    /// renames and deletes. Every random decision comes from the <see cref="SimSchedule"/>,
    /// which also logs every event, so a seed replays exactly.
    ///
    /// Model rules (what a sync client does, as far as the race needs it):
    /// - Paths are relative, '/'-separated and case-sensitive (ordinal); a '\' is read as '/'.
    /// - The cloud versions every path with a store-wide counter. An upload is accepted when
    ///   it was based on the cloud's current version of that path (or the path is new or
    ///   deleted and the upload never saw it); otherwise a write becomes a conflict copy
    ///   <c>name (conflict &lt;machine&gt; &lt;n&gt;).ext</c> in the cloud and the writer's own
    ///   file goes back to the cloud's, and a delete is lost (the file comes back).
    /// - A machine's later write to a path whose upload is still queued replaces that upload.
    /// - Per path a view never goes back: a download older than what the view holds is
    ///   skipped, and so is a download onto a file with an unsent local change.
    /// - Offline: downloads to the machine are dropped (queued ones too) and its uploads wait;
    ///   on reconnect the machine is brought up to the cloud's current state.
    /// - Placeholder: the listing shows the name and full size, the content arrives at the
    ///   first <see cref="TryOpen"/>, which fails while the machine is offline.
    /// - Partial: a prefix of the file lands first, the rest in a later delivery due at
    ///   least one tick later, so the truncated file is readable between two ticks.
    /// Single-threaded, no file system, no wall clock: nothing here touches the disk.
    /// </summary>
    internal sealed class FakeSyncedStore
    {
        /// <summary>The machine name events carry for an edit made outside every machine (web UI, a person).</summary>
        public const string ForeignActor = "foreign";

        private sealed class CloudEntry
        {
            public byte[] Content;
            public long Version;
            public bool Deleted;
        }

        private sealed class ViewEntry
        {
            public byte[] Content;
            public byte[] Full;
            public long Version;
            public bool Dirty;
            public bool Deleted;
            public bool Placeholder;
            public bool Partial;
            public long LocalWriteSeq;
        }

        private sealed class Machine
        {
            public string Id;
            public FakeSyncPolicy Policy;
            public bool Online = true;
            public readonly SortedDictionary<string, ViewEntry> View =
                new SortedDictionary<string, ViewEntry>(StringComparer.Ordinal);
        }

        private sealed class Delivery
        {
            public long Seq;
            public long OrderKey;
            public long DueTick;
            public bool IsUpload;
            public Machine Machine;
            public string Path;
            public bool IsDelete;
            public byte[] Content;
            public long BaseVersion;
            public long LocalWriteSeq;
            public long Version;
            public bool Placeholder;
            public int PartialLength;
            public bool IsRest;
        }

        private const int DeliverLoopGuard = 1000000;

        private readonly List<Machine> machines = new List<Machine>();
        private readonly SortedDictionary<string, CloudEntry> cloud =
            new SortedDictionary<string, CloudEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> conflictCounters = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<Delivery> pending = new List<Delivery>();
        private long versionCounter;
        private long deliverySeq;
        private long orderCounter;
        private long localWriteSeq;

        public FakeSyncedStore(SimSchedule schedule)
        {
            Schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        }

        public SimSchedule Schedule { get; }

        public long Tick { get; private set; }

        public int PendingCount => pending.Count;

        public IReadOnlyList<string> Machines
        {
            get
            {
                var ids = new List<string>(machines.Count);
                for (int i = 0; i < machines.Count; i++)
                    ids.Add(machines[i].Id);
                return ids;
            }
        }

        // ---- machines -------------------------------------------------------------------

        public void AddMachine(string machine, FakeSyncPolicy policy = null)
        {
            ValidateMachineId(machine);
            if (FindMachine(machine) != null)
                throw new ArgumentException("machine already added: " + machine, nameof(machine));
            var m = new Machine { Id = machine, Policy = policy ?? new FakeSyncPolicy() };
            m.Policy.Validate();
            machines.Add(m);
        }

        /// <summary>The live policy object of a machine; a test may change it between steps.</summary>
        public FakeSyncPolicy Policy(string machine)
        {
            return Get(machine).Policy;
        }

        public bool IsOnline(string machine)
        {
            return Get(machine).Online;
        }

        public void SetOnline(string machine, bool online)
        {
            Machine m = Get(machine);
            if (m.Online == online)
                return;
            m.Online = online;
            if (!online)
            {
                Schedule.Record(Tick, SimEventKind.MachineOffline, m.Id, null, 0, 0);
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    Delivery d = pending[i];
                    if (!d.IsUpload && d.Machine == m)
                    {
                        pending.RemoveAt(i);
                        Schedule.Record(Tick, SimEventKind.DroppedOffline, m.Id, d.Path, d.Version, d.Seq);
                    }
                }
                return;
            }
            Schedule.Record(Tick, SimEventKind.MachineOnline, m.Id, null, 0, 0);
            Resync(m);
        }

        // ---- a machine's own file operations ---------------------------------------------

        public void Write(string machine, string path, byte[] content)
        {
            if (content == null)
                throw new ArgumentNullException(nameof(content));
            Machine m = Get(machine);
            path = NormalizePath(path);
            byte[] copy = Copy(content);
            ViewEntry e = GetOrCreateView(m, path);
            e.Content = copy;
            e.Full = copy;
            e.Deleted = false;
            e.Placeholder = false;
            e.Partial = false;
            e.Dirty = true;
            e.LocalWriteSeq = ++localWriteSeq;
            Schedule.Record(Tick, SimEventKind.LocalWrite, m.Id, path, e.Version, copy.Length);
            QueueUpload(m, path, false, copy, e);
        }

        public bool Delete(string machine, string path)
        {
            Machine m = Get(machine);
            path = NormalizePath(path);
            ViewEntry e;
            if (!m.View.TryGetValue(path, out e) || e.Deleted)
                return false;
            e.Content = null;
            e.Full = null;
            e.Deleted = true;
            e.Placeholder = false;
            e.Partial = false;
            e.Dirty = true;
            e.LocalWriteSeq = ++localWriteSeq;
            Schedule.Record(Tick, SimEventKind.LocalDelete, m.Id, path, e.Version, 0);
            QueueUpload(m, path, true, null, e);
            return true;
        }

        /// <summary>A rename on one machine, which its client uploads as a delete plus a write.</summary>
        public bool Rename(string machine, string from, string to)
        {
            Machine m = Get(machine);
            from = NormalizePath(from);
            to = NormalizePath(to);
            ViewEntry e;
            if (from == to || !m.View.TryGetValue(from, out e) || e.Deleted)
                return false;
            byte[] content = e.Placeholder ? e.Full : e.Content;
            Delete(machine, from);
            Write(machine, to, content);
            return true;
        }

        /// <summary>The files under a directory of one machine's view ("" for all), in ordinal path order.</summary>
        public IReadOnlyList<SyncedFileInfo> List(string machine, string directory)
        {
            Machine m = Get(machine);
            string prefix = DirectoryPrefix(directory);
            var result = new List<SyncedFileInfo>();
            foreach (KeyValuePair<string, ViewEntry> kv in m.View)
            {
                if (kv.Value.Deleted || !kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                result.Add(new SyncedFileInfo
                {
                    Path = kv.Key,
                    Length = kv.Value.Placeholder ? kv.Value.Full.Length : kv.Value.Content.Length,
                    IsPlaceholder = kv.Value.Placeholder,
                });
            }
            return result;
        }

        public bool Exists(string machine, string path)
        {
            ViewEntry e;
            return Get(machine).View.TryGetValue(NormalizePath(path), out e) && !e.Deleted;
        }

        /// <summary>
        /// Opens and reads a file of one machine's view. Opening a placeholder downloads it
        /// (the content is the file's from then on) and fails while the machine is offline.
        /// Returns a copy; reasons: <c>missing</c>, <c>placeholder-offline</c>.
        /// </summary>
        public bool TryOpen(string machine, string path, out byte[] content, out string reason)
        {
            Machine m = Get(machine);
            path = NormalizePath(path);
            content = null;
            ViewEntry e;
            if (!m.View.TryGetValue(path, out e) || e.Deleted)
            {
                reason = "missing";
                return false;
            }
            if (e.Placeholder)
            {
                if (!m.Online)
                {
                    reason = "placeholder-offline";
                    return false;
                }
                e.Placeholder = false;
                e.Content = e.Full;
                Schedule.Record(Tick, SimEventKind.PlaceholderOpened, m.Id, path, e.Version, e.Full.Length);
            }
            content = Copy(e.Content);
            reason = null;
            return true;
        }

        // ---- edits made outside every machine (straight to the cloud) --------------------

        public void ForeignEdit(string path, byte[] content)
        {
            if (content == null)
                throw new ArgumentNullException(nameof(content));
            path = NormalizePath(path);
            long v = SetCloud(path, Copy(content));
            Schedule.Record(Tick, SimEventKind.ForeignEdit, ForeignActor, path, v, content.Length);
            QueueDownloadsToAll(path, null);
        }

        public bool ForeignRename(string from, string to)
        {
            from = NormalizePath(from);
            to = NormalizePath(to);
            CloudEntry e;
            if (from == to || !cloud.TryGetValue(from, out e) || e.Deleted)
                return false;
            byte[] content = e.Content;
            long removed = DeleteCloud(from);
            long v = SetCloud(to, content);
            Schedule.Record(Tick, SimEventKind.ForeignRename, ForeignActor, from, removed, 0);
            Schedule.Record(Tick, SimEventKind.ForeignRename, ForeignActor, to, v, content.Length);
            QueueDownloadsToAll(from, null);
            QueueDownloadsToAll(to, null);
            return true;
        }

        public bool ForeignDelete(string path)
        {
            path = NormalizePath(path);
            CloudEntry e;
            if (!cloud.TryGetValue(path, out e) || e.Deleted)
                return false;
            long v = DeleteCloud(path);
            Schedule.Record(Tick, SimEventKind.ForeignDelete, ForeignActor, path, v, 0);
            QueueDownloadsToAll(path, null);
            return true;
        }

        // ---- the cloud, for assertions ---------------------------------------------------

        public IReadOnlyList<string> CloudList(string directory)
        {
            string prefix = DirectoryPrefix(directory);
            var result = new List<string>();
            foreach (KeyValuePair<string, CloudEntry> kv in cloud)
            {
                if (!kv.Value.Deleted && kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                    result.Add(kv.Key);
            }
            return result;
        }

        public bool TryReadCloud(string path, out byte[] content)
        {
            CloudEntry e;
            if (cloud.TryGetValue(NormalizePath(path), out e) && !e.Deleted)
            {
                content = Copy(e.Content);
                return true;
            }
            content = null;
            return false;
        }

        // ---- time ------------------------------------------------------------------------

        /// <summary>Moves the store's tick forward and applies every delivery then due. Returns how many applied.</summary>
        public int Advance(int ticks = 1)
        {
            if (ticks < 0)
                throw new ArgumentOutOfRangeException(nameof(ticks));
            Tick += ticks;
            return DeliverDue();
        }

        /// <summary>
        /// Applies due deliveries until none is left: lowest order key first, an upload of an
        /// offline machine waits. Deliveries a delivery queues apply in the same call when due.
        /// </summary>
        public int DeliverDue()
        {
            int applied = 0;
            for (int guard = 0; guard < DeliverLoopGuard; guard++)
            {
                int pick = -1;
                for (int i = 0; i < pending.Count; i++)
                {
                    Delivery d = pending[i];
                    if (d.DueTick > Tick || (d.IsUpload && !d.Machine.Online))
                        continue;
                    if (pick < 0 || Before(d, pending[pick]))
                        pick = i;
                }
                if (pick < 0)
                    return applied;
                Delivery next = pending[pick];
                pending.RemoveAt(pick);
                if (next.IsUpload)
                    ApplyUpload(next);
                else
                    ApplyDownload(next);
                applied++;
            }
            throw new InvalidOperationException("FakeSyncedStore.DeliverDue did not settle; " + Schedule.Describe());
        }

        /// <summary>
        /// Advances one tick at a time until nothing deliverable is queued (an offline
        /// machine's waiting uploads do not count). Returns the ticks spent; throws past maxTicks.
        /// </summary>
        public int RunUntilQuiet(int maxTicks = 10000)
        {
            int spent = 0;
            DeliverDue();
            while (HasDeliverable())
            {
                if (spent >= maxTicks)
                    throw new InvalidOperationException("FakeSyncedStore did not go quiet within "
                        + maxTicks.ToString(CultureInfo.InvariantCulture) + " ticks; " + Schedule.Describe());
                Advance(1);
                spent++;
            }
            return spent;
        }

        // ---- names -----------------------------------------------------------------------

        /// <summary>
        /// The one path form of the store: relative, '/'-separated, no empty, "." or ".."
        /// segment, no drive. A '\' is read as '/', so a Windows-style path names the same file.
        /// </summary>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("path is empty", nameof(path));
            string p = path.Replace('\\', '/');
            if (p[0] == '/' || p.IndexOf(':') >= 0)
                throw new ArgumentException("path must be relative: " + path, nameof(path));
            string[] segments = p.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                string s = segments[i];
                if (s.Length == 0 || s == "." || s == "..")
                    throw new ArgumentException("bad path segment in: " + path, nameof(path));
            }
            return p;
        }

        /// <summary>
        /// <c>dir/stem (conflict &lt;machine&gt; &lt;n&gt;).ext</c>: the extension is the last
        /// dot of the file name onward, and a name with no dot (or only a leading one) has none.
        /// </summary>
        public static string ConflictCopyName(string path, string machine, int n)
        {
            path = NormalizePath(path);
            int slash = path.LastIndexOf('/');
            string dir = slash < 0 ? "" : path.Substring(0, slash + 1);
            string name = path.Substring(slash + 1);
            int dot = name.LastIndexOf('.');
            string stem = dot > 0 ? name.Substring(0, dot) : name;
            string ext = dot > 0 ? name.Substring(dot) : "";
            return string.Format(CultureInfo.InvariantCulture, "{0}{1} (conflict {2} {3}){4}", dir, stem, machine, n, ext);
        }

        // ---- internals -------------------------------------------------------------------

        private static bool Before(Delivery a, Delivery b)
        {
            if (a.OrderKey != b.OrderKey)
                return a.OrderKey < b.OrderKey;
            return a.Seq < b.Seq;
        }

        private bool HasDeliverable()
        {
            for (int i = 0; i < pending.Count; i++)
            {
                if (!pending[i].IsUpload || pending[i].Machine.Online)
                    return true;
            }
            return false;
        }

        private void QueueUpload(Machine m, string path, bool isDelete, byte[] content, ViewEntry e)
        {
            for (int i = 0; i < pending.Count; i++)
            {
                Delivery q = pending[i];
                if (q.IsUpload && q.Machine == m && q.Path == path)
                {
                    q.IsDelete = isDelete;
                    q.Content = content;
                    q.LocalWriteSeq = e.LocalWriteSeq;
                    Schedule.Record(Tick, SimEventKind.Coalesced, m.Id, path, q.BaseVersion, q.Seq);
                    return;
                }
            }
            var d = new Delivery
            {
                IsUpload = true,
                Machine = m,
                Path = path,
                IsDelete = isDelete,
                Content = content,
                BaseVersion = e.Version,
                LocalWriteSeq = e.LocalWriteSeq,
            };
            Enqueue(d, m.Policy, SimEventKind.UploadQueued);
        }

        private void QueueDownload(Machine target, string path, bool isRest, byte[] restContent, long restVersion)
        {
            if (!target.Online)
            {
                Schedule.Record(Tick, SimEventKind.DroppedOffline, target.Id, path,
                    isRest ? restVersion : cloud[path].Version, 0);
                return;
            }
            var d = new Delivery { IsUpload = false, Machine = target, Path = path, IsRest = isRest };
            if (isRest)
            {
                d.Content = restContent;
                d.Version = restVersion;
            }
            else
            {
                CloudEntry c = cloud[path];
                d.Version = c.Version;
                d.IsDelete = c.Deleted;
                d.Content = c.Content;
                FakeSyncPolicy p = target.Policy;
                if (!d.IsDelete)
                {
                    if (Schedule.Chance(SimDrawPurpose.Placeholder, p.PlaceholderPerMille))
                        d.Placeholder = true;
                    else if (d.Content.Length >= 2 && Schedule.Chance(SimDrawPurpose.Partial, p.PartialPerMille))
                        d.PartialLength = 1 + Schedule.Next(SimDrawPurpose.PartialLength, d.Content.Length - 1);
                }
            }
            Enqueue(d, target.Policy, SimEventKind.DownloadQueued);
        }

        private void QueueDownloadsToAll(string path, Machine except)
        {
            for (int i = 0; i < machines.Count; i++)
            {
                if (machines[i] != except)
                    QueueDownload(machines[i], path, false, null, 0);
            }
        }

        private void Enqueue(Delivery d, FakeSyncPolicy policy, SimEventKind queuedKind)
        {
            d.Seq = ++deliverySeq;
            int delay = Schedule.Between(SimDrawPurpose.Delay, policy.MinDelayTicks, policy.MaxDelayTicks);
            // The rest of a partial file is never due in the tick its head landed, so a reader
            // between ticks always gets to see the truncated file the control claims to show.
            d.DueTick = Tick + (d.IsRest ? Math.Max(1, delay) : delay);
            d.OrderKey = ++orderCounter;
            bool jumped = false;
            if (pending.Count > 0 && Schedule.Chance(SimDrawPurpose.Reorder, policy.ReorderPerMille))
            {
                long min = long.MaxValue;
                for (int i = 0; i < pending.Count; i++)
                    min = Math.Min(min, pending[i].OrderKey);
                d.OrderKey = min - 1;
                jumped = true;
            }
            pending.Add(d);
            Schedule.Record(Tick, queuedKind, d.Machine.Id, d.Path, d.IsUpload ? d.BaseVersion : d.Version, d.Seq);
            if (delay > 0)
                Schedule.Record(Tick, SimEventKind.Delayed, d.Machine.Id, d.Path, d.Seq, delay);
            if (jumped)
                Schedule.Record(Tick, SimEventKind.Reordered, d.Machine.Id, d.Path, d.Seq, pending.Count - 1);
        }

        private void ApplyUpload(Delivery d)
        {
            Machine m = d.Machine;
            CloudEntry c;
            bool exists = cloud.TryGetValue(d.Path, out c);
            long current = exists ? c.Version : 0;
            bool live = exists && !c.Deleted;
            bool accept = d.BaseVersion == current || (!live && d.BaseVersion == 0);
            ViewEntry own;
            m.View.TryGetValue(d.Path, out own);
            bool ownIsThisChange = own != null && own.LocalWriteSeq == d.LocalWriteSeq;

            if (d.IsDelete)
            {
                if (accept || !live)
                {
                    long v = live ? DeleteCloud(d.Path) : current;
                    if (ownIsThisChange)
                    {
                        own.Version = v;
                        own.Dirty = false;
                    }
                    Schedule.Record(Tick, SimEventKind.DeleteApplied, m.Id, d.Path, v, d.Seq);
                    if (live)
                        QueueDownloadsToAll(d.Path, m);
                    return;
                }
                Schedule.Record(Tick, SimEventKind.DeleteLost, m.Id, d.Path, current, d.Seq);
                if (ownIsThisChange)
                    own.Dirty = false;
                QueueDownload(m, d.Path, false, null, 0);
                return;
            }

            if (accept)
            {
                long v = SetCloud(d.Path, d.Content);
                if (ownIsThisChange)
                {
                    own.Version = v;
                    own.Dirty = false;
                }
                Schedule.Record(Tick, SimEventKind.UploadApplied, m.Id, d.Path, v, d.Seq);
                QueueDownloadsToAll(d.Path, m);
                return;
            }

            string copyPath = NextConflictName(d.Path, m.Id);
            long cv = SetCloud(copyPath, d.Content);
            Schedule.Record(Tick, SimEventKind.ConflictCopy, m.Id, copyPath, cv, d.Seq);
            QueueDownloadsToAll(copyPath, null);
            if (ownIsThisChange)
                own.Dirty = false;
            QueueDownload(m, d.Path, false, null, 0);
        }

        private void ApplyDownload(Delivery d)
        {
            Machine m = d.Machine;
            ViewEntry e;
            m.View.TryGetValue(d.Path, out e);
            if (e != null && e.Dirty)
            {
                Schedule.Record(Tick, SimEventKind.SkippedDirty, m.Id, d.Path, d.Version, d.Seq);
                return;
            }
            if (d.IsRest)
            {
                if (e == null || !e.Partial || e.Version != d.Version)
                {
                    Schedule.Record(Tick, SimEventKind.SkippedStale, m.Id, d.Path, d.Version, d.Seq);
                    return;
                }
                e.Content = e.Full;
                e.Partial = false;
                Schedule.Record(Tick, SimEventKind.RestLanded, m.Id, d.Path, d.Version, e.Full.Length);
                return;
            }
            if (e != null && e.Version >= d.Version)
            {
                Schedule.Record(Tick, SimEventKind.SkippedStale, m.Id, d.Path, d.Version, d.Seq);
                return;
            }
            if (e == null)
            {
                e = new ViewEntry();
                m.View.Add(d.Path, e);
            }
            e.Version = d.Version;
            e.Dirty = false;
            e.Placeholder = false;
            e.Partial = false;
            if (d.IsDelete)
            {
                e.Deleted = true;
                e.Content = null;
                e.Full = null;
                Schedule.Record(Tick, SimEventKind.DownloadDeleted, m.Id, d.Path, d.Version, d.Seq);
                return;
            }
            e.Deleted = false;
            e.Full = d.Content;
            if (d.Placeholder)
            {
                e.Placeholder = true;
                e.Content = null;
                Schedule.Record(Tick, SimEventKind.PlaceholderLanded, m.Id, d.Path, d.Version, d.Content.Length);
                return;
            }
            if (d.PartialLength > 0)
            {
                e.Partial = true;
                e.Content = Prefix(d.Content, d.PartialLength);
                Schedule.Record(Tick, SimEventKind.PartialLanded, m.Id, d.Path, d.Version, e.Content.Length);
                QueueDownload(m, d.Path, true, d.Content, d.Version);
                return;
            }
            e.Content = d.Content;
            Schedule.Record(Tick, SimEventKind.Downloaded, m.Id, d.Path, d.Version, d.Content.Length);
        }

        private void Resync(Machine m)
        {
            foreach (KeyValuePair<string, CloudEntry> kv in cloud)
            {
                ViewEntry e;
                bool has = m.View.TryGetValue(kv.Key, out e);
                if (has && e.Dirty)
                    continue;
                if (has && e.Partial && e.Version == kv.Value.Version)
                {
                    QueueDownload(m, kv.Key, true, e.Full, e.Version);
                    continue;
                }
                long held = has ? e.Version : 0;
                if (held >= kv.Value.Version)
                    continue;
                if (kv.Value.Deleted && (!has || e.Deleted))
                    continue;
                QueueDownload(m, kv.Key, false, null, 0);
            }
        }

        private long SetCloud(string path, byte[] content)
        {
            CloudEntry c;
            if (!cloud.TryGetValue(path, out c))
            {
                c = new CloudEntry();
                cloud.Add(path, c);
            }
            c.Content = content;
            c.Deleted = false;
            c.Version = ++versionCounter;
            return c.Version;
        }

        private long DeleteCloud(string path)
        {
            CloudEntry c = cloud[path];
            c.Content = null;
            c.Deleted = true;
            c.Version = ++versionCounter;
            return c.Version;
        }

        private string NextConflictName(string path, string machine)
        {
            int n;
            conflictCounters.TryGetValue(path, out n);
            while (true)
            {
                n++;
                string candidate = ConflictCopyName(path, machine, n);
                CloudEntry c;
                if (!cloud.TryGetValue(candidate, out c) || c.Deleted)
                {
                    conflictCounters[path] = n;
                    return candidate;
                }
            }
        }

        private Machine FindMachine(string id)
        {
            for (int i = 0; i < machines.Count; i++)
            {
                if (string.Equals(machines[i].Id, id, StringComparison.Ordinal))
                    return machines[i];
            }
            return null;
        }

        private Machine Get(string id)
        {
            Machine m = FindMachine(id);
            if (m == null)
                throw new ArgumentException("unknown machine: " + id, nameof(id));
            return m;
        }

        private static ViewEntry GetOrCreateView(Machine m, string path)
        {
            ViewEntry e;
            if (!m.View.TryGetValue(path, out e))
            {
                e = new ViewEntry { Deleted = true };
                m.View.Add(path, e);
            }
            return e;
        }

        private static string DirectoryPrefix(string directory)
        {
            if (string.IsNullOrEmpty(directory))
                return "";
            string d = directory.Replace('\\', '/').TrimEnd('/');
            return d.Length == 0 ? "" : NormalizePath(d) + "/";
        }

        private static void ValidateMachineId(string id)
        {
            if (string.IsNullOrEmpty(id) || id == ForeignActor)
                throw new ArgumentException("bad machine id: " + (id ?? "null"), nameof(id));
            for (int i = 0; i < id.Length; i++)
            {
                char ch = id[i];
                bool ok = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_' || ch == '-';
                if (!ok)
                    throw new ArgumentException("bad machine id: " + id, nameof(id));
            }
        }

        private static byte[] Copy(byte[] source)
        {
            var copy = new byte[source.Length];
            Buffer.BlockCopy(source, 0, copy, 0, source.Length);
            return copy;
        }

        private static byte[] Prefix(byte[] source, int length)
        {
            var copy = new byte[length];
            Buffer.BlockCopy(source, 0, copy, 0, length);
            return copy;
        }
    }
}
