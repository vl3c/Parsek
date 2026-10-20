using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace Parsek
{
    /// <summary>
    /// The three recording sidecar kinds whose bytes a save names by content hash
    /// (design-load-restores-the-save.md section 4.4). The readable <c>.txt</c> mirrors and
    /// the <c>.pann</c> cache are not hashed and have no kind here.
    /// </summary>
    internal enum SidecarKind
    {
        None = 0,
        Prec = 1,
        Vessel = 2,
        Ghost = 3,
    }

    /// <summary>
    /// Content identity of a sidecar file: the first 64 bits of SHA-1 over the raw bytes,
    /// as 16 lower-case hex characters. Identity, not security.
    /// </summary>
    internal static class SidecarContentHash
    {
        internal const int HexLength = 16;

        internal static string Compute(byte[] bytes)
        {
            if (bytes == null)
                throw new ArgumentNullException(nameof(bytes));

            byte[] digest;
            using (SHA1 sha = SHA1.Create())
                digest = sha.ComputeHash(bytes);

            var chars = new char[HexLength];
            const string hex = "0123456789abcdef";
            for (int i = 0; i < HexLength / 2; i++)
            {
                chars[i * 2] = hex[digest[i] >> 4];
                chars[i * 2 + 1] = hex[digest[i] & 0xF];
            }
            return new string(chars);
        }

        internal static string ComputeFile(string path)
        {
            return Compute(File.ReadAllBytes(path));
        }

        /// <summary>16 lower-case hex characters. Anything else is not a hash this code wrote.</summary>
        internal static bool IsWellFormed(string hash)
        {
            if (hash == null || hash.Length != HexLength)
                return false;
            for (int i = 0; i < hash.Length; i++)
            {
                char c = hash[i];
                bool digit = c >= '0' && c <= '9';
                bool lowerHex = c >= 'a' && c <= 'f';
                if (!digit && !lowerHex)
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// File naming for retained sidecar versions. A head is
    /// <c>Parsek/Recordings/&lt;id&gt;&lt;suffix&gt;</c>; a retained version of it is
    /// <c>Parsek/Recordings/_versions/&lt;id&gt;.&lt;hash&gt;&lt;suffix&gt;</c>, and a replaced head
    /// waiting to be filed is <c>_versions/&lt;id&gt;&lt;suffix&gt;.incoming.&lt;guid&gt;</c>.
    /// The suffix is the head's own (<c>.prec</c>, <c>_vessel.craft</c>, <c>_ghost.craft</c>).
    /// </summary>
    internal static class SidecarVersions
    {
        internal const string VersionsDirName = "_versions";
        internal const string IncomingMarker = ".incoming.";

        private static readonly SidecarKind[] ParseOrder =
        {
            SidecarKind.Vessel,
            SidecarKind.Ghost,
            SidecarKind.Prec,
        };

        internal static string Suffix(SidecarKind kind)
        {
            switch (kind)
            {
                case SidecarKind.Prec: return ".prec";
                case SidecarKind.Vessel: return "_vessel.craft";
                case SidecarKind.Ghost: return "_ghost.craft";
                default: return null;
            }
        }

        internal static string Label(SidecarKind kind)
        {
            switch (kind)
            {
                case SidecarKind.Prec: return "prec";
                case SidecarKind.Vessel: return "vessel";
                case SidecarKind.Ghost: return "ghost";
                default: return "none";
            }
        }

        internal static string VersionFileName(string recordingId, string hash, SidecarKind kind)
        {
            return recordingId + "." + hash + Suffix(kind);
        }

        internal static string IncomingFileName(string recordingId, SidecarKind kind)
        {
            return recordingId + Suffix(kind) + IncomingMarker + Guid.NewGuid().ToString("N");
        }

        internal static string VersionsDirectoryForHead(string headPath)
        {
            string dir = string.IsNullOrEmpty(headPath) ? null : Path.GetDirectoryName(headPath);
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, VersionsDirName);
        }

        internal static string VersionPathForHead(string headPath, string recordingId, string hash, SidecarKind kind)
        {
            string dir = VersionsDirectoryForHead(headPath);
            return dir == null ? null : Path.Combine(dir, VersionFileName(recordingId, hash, kind));
        }

        /// <summary>Parses <c>&lt;id&gt;.&lt;hash&gt;&lt;suffix&gt;</c>.</summary>
        internal static bool TryParseVersionFileName(
            string fileName, out string recordingId, out string hash, out SidecarKind kind)
        {
            recordingId = null;
            hash = null;
            kind = SidecarKind.None;
            if (string.IsNullOrEmpty(fileName) || fileName.IndexOf(IncomingMarker, StringComparison.Ordinal) >= 0)
                return false;

            for (int i = 0; i < ParseOrder.Length; i++)
            {
                string suffix = Suffix(ParseOrder[i]);
                if (!fileName.EndsWith(suffix, StringComparison.Ordinal))
                    continue;
                string stem = fileName.Substring(0, fileName.Length - suffix.Length);
                int dot = stem.LastIndexOf('.');
                if (dot <= 0)
                    return false;
                string candidateHash = stem.Substring(dot + 1);
                if (!SidecarContentHash.IsWellFormed(candidateHash))
                    return false;
                recordingId = stem.Substring(0, dot);
                hash = candidateHash;
                kind = ParseOrder[i];
                return true;
            }
            return false;
        }

        /// <summary>Parses <c>&lt;id&gt;&lt;suffix&gt;.incoming.&lt;guid&gt;</c>.</summary>
        internal static bool TryParseIncomingFileName(
            string fileName, out string recordingId, out SidecarKind kind)
        {
            recordingId = null;
            kind = SidecarKind.None;
            if (string.IsNullOrEmpty(fileName))
                return false;
            int marker = fileName.IndexOf(IncomingMarker, StringComparison.Ordinal);
            if (marker <= 0)
                return false;
            string head = fileName.Substring(0, marker);
            for (int i = 0; i < ParseOrder.Length; i++)
            {
                string suffix = Suffix(ParseOrder[i]);
                if (!head.EndsWith(suffix, StringComparison.Ordinal))
                    continue;
                string id = head.Substring(0, head.Length - suffix.Length);
                if (id.Length == 0)
                    return false;
                recordingId = id;
                kind = ParseOrder[i];
                return true;
            }
            return false;
        }

        internal static string GetHash(Recording rec, SidecarKind kind)
        {
            if (rec == null) return null;
            switch (kind)
            {
                case SidecarKind.Prec: return rec.PrecHash;
                case SidecarKind.Vessel: return rec.VesselSnapshotHash;
                case SidecarKind.Ghost: return rec.GhostSnapshotHash;
                default: return null;
            }
        }

        internal static void SetHash(Recording rec, SidecarKind kind, string hash)
        {
            if (rec == null) return;
            switch (kind)
            {
                case SidecarKind.Prec: rec.PrecHash = hash; break;
                case SidecarKind.Vessel: rec.VesselSnapshotHash = hash; break;
                case SidecarKind.Ghost: rec.GhostSnapshotHash = hash; break;
            }
        }

        /// <summary>
        /// Copies the three content hashes. Every site that copies <c>SidecarEpoch</c>
        /// between recordings copies these with it.
        /// </summary>
        internal static void CopyHashes(Recording target, Recording source)
        {
            if (target == null || source == null) return;
            target.PrecHash = source.PrecHash;
            target.VesselSnapshotHash = source.VesselSnapshotHash;
            target.GhostSnapshotHash = source.GhostSnapshotHash;
        }
    }

    internal enum SidecarResolveSource
    {
        /// <summary>The save names no hash: read the head with the epoch rule.</summary>
        NoHash = 0,
        /// <summary>The head's bytes hash to the named hash.</summary>
        Head = 1,
        /// <summary>The retained version the save names.</summary>
        Version = 2,
        /// <summary>A hash is named and neither the head nor a version holds it.</summary>
        Unresolved = 3,
    }

    internal struct SidecarResolution
    {
        public SidecarKind Kind;
        public SidecarResolveSource Source;
        public string NamedHash;
        public string HeadPath;
        public string VersionPath;
        public bool HeadExists;

        /// <summary>A version-sourced sidecar that was moved into the head position.</summary>
        public bool Promoted;

        /// <summary>The path to read: the head for NoHash / Head / a promoted version, the version path for an unpromoted one, null when unresolved.</summary>
        public string ReadPath
        {
            get
            {
                switch (Source)
                {
                    case SidecarResolveSource.NoHash:
                    case SidecarResolveSource.Head:
                        return HeadPath;
                    case SidecarResolveSource.Version:
                        return Promoted ? HeadPath : VersionPath;
                    default:
                        return null;
                }
            }
        }
    }

    /// <summary>
    /// Version resolution on Cold loads (design 4.4 "Loads resolve the version the save
    /// names"). A Cold <c>OnLoad</c> opens the scope with the save node so the hydration
    /// inside it can count how many nodes of the loaded save name each recording id (a
    /// version is promoted to head only for a single owner). Every other load kind runs
    /// with the scope closed and reads heads with the epoch rule, unchanged.
    /// </summary>
    internal static class SidecarVersionResolution
    {
        private const string Tag = "RecordingStore";

        private static Dictionary<string, int> ownerCounts;
        private static int headCount;
        private static int versionCount;
        private static int promotedCount;
        private static int notPromotedCount;
        private static int unresolvedCount;
        private static int noHashCount;

        internal static bool IsActive => ownerCounts != null;

        internal static void BeginColdLoad(ConfigNode scenarioNode)
        {
            Begin(CountRecordingOwners(scenarioNode));
        }

        internal static void BeginForTesting(Dictionary<string, int> owners)
        {
            Begin(owners ?? new Dictionary<string, int>(StringComparer.Ordinal));
        }

        private static void Begin(Dictionary<string, int> owners)
        {
            ownerCounts = owners;
            headCount = 0;
            versionCount = 0;
            promotedCount = 0;
            notPromotedCount = 0;
            unresolvedCount = 0;
            noHashCount = 0;
        }

        /// <summary>Closes the scope and logs one summary when it resolved anything. No-op when closed.</summary>
        internal static void End(string reason)
        {
            if (ownerCounts == null)
                return;

            int total = headCount + versionCount + unresolvedCount + noHashCount;
            if (total > 0)
            {
                ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                    "Sidecar version resolution summary: reason={0} files={1} head={2} version={3} " +
                    "promoted={4} readWithoutPromote={5} unresolved={6} noHash={7} owners={8}",
                    reason ?? "<none>", total, headCount, versionCount, promotedCount,
                    notPromotedCount, unresolvedCount, noHashCount, ownerCounts.Count));
            }
            ownerCounts = null;
        }

        internal static void ResetForTesting()
        {
            ownerCounts = null;
        }

        internal static int OwnerCount(string recordingId)
        {
            if (ownerCounts == null || string.IsNullOrEmpty(recordingId))
                return 0;
            int n;
            return ownerCounts.TryGetValue(recordingId, out n) ? n : 0;
        }

        /// <summary>
        /// Counts the RECORDING nodes that name each recording id in a Parsek scenario node:
        /// every RECORDING under every RECORDING_TREE (committed, active and pending trees
        /// alike) plus top-level RECORDING nodes.
        /// </summary>
        internal static Dictionary<string, int> CountRecordingOwners(ConfigNode scenarioNode)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (scenarioNode == null)
                return counts;

            CountRecordingNodes(scenarioNode.GetNodes("RECORDING"), counts);
            ConfigNode[] trees = scenarioNode.GetNodes("RECORDING_TREE");
            for (int t = 0; t < trees.Length; t++)
                CountRecordingNodes(trees[t]?.GetNodes("RECORDING"), counts);
            return counts;
        }

        private static void CountRecordingNodes(ConfigNode[] nodes, Dictionary<string, int> counts)
        {
            if (nodes == null)
                return;
            for (int i = 0; i < nodes.Length; i++)
            {
                string id = nodes[i]?.GetValue("recordingId");
                if (string.IsNullOrEmpty(id))
                    continue;
                int n;
                counts.TryGetValue(id, out n);
                counts[id] = n + 1;
            }
        }

        /// <summary>
        /// Resolves one hashed sidecar (design 4.4 steps 1-4): no named hash reads the head;
        /// a head whose bytes hash to the named hash is read; otherwise the retained version
        /// <c>_versions/&lt;id&gt;.&lt;hash&gt;&lt;suffix&gt;</c> is read when its bytes really
        /// carry that hash; otherwise the sidecar is unresolved.
        /// </summary>
        internal static SidecarResolution Resolve(
            string headPath, string recordingId, SidecarKind kind, string namedHash)
        {
            var result = new SidecarResolution
            {
                Kind = kind,
                NamedHash = namedHash,
                HeadPath = headPath,
                HeadExists = !string.IsNullOrEmpty(headPath) && File.Exists(headPath),
            };

            if (string.IsNullOrEmpty(namedHash))
            {
                result.Source = SidecarResolveSource.NoHash;
                return result;
            }

            if (result.HeadExists && HashEquals(headPath, namedHash))
            {
                result.Source = SidecarResolveSource.Head;
                return result;
            }

            string versionPath = SidecarVersions.VersionPathForHead(headPath, recordingId, namedHash, kind);
            if (!string.IsNullOrEmpty(versionPath) && File.Exists(versionPath))
            {
                if (HashEquals(versionPath, namedHash))
                {
                    result.Source = SidecarResolveSource.Version;
                    result.VersionPath = versionPath;
                    return result;
                }

                ParsekLog.Warn(Tag,
                    $"Sidecar version content does not match its name: rec={recordingId} " +
                    $"kind={SidecarVersions.Label(kind)} hash={namedHash} path='{versionPath}' - not read");
            }

            result.Source = SidecarResolveSource.Unresolved;
            return result;
        }

        private static bool HashEquals(string path, string expected)
        {
            try
            {
                return string.Equals(SidecarContentHash.ComputeFile(path), expected, StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"Sidecar hash read failed path='{path}' ex={ex.GetType().Name}:{ex.Message}");
                return false;
            }
        }

        internal static void NoteResolved(Recording rec, SidecarResolution resolution)
        {
            bool promoted = resolution.Promoted;
            switch (resolution.Source)
            {
                case SidecarResolveSource.Head: headCount++; break;
                case SidecarResolveSource.Version:
                    versionCount++;
                    if (promoted) promotedCount++;
                    else notPromotedCount++;
                    break;
                case SidecarResolveSource.Unresolved: unresolvedCount++; break;
                default: noHashCount++; return;
            }

            if (resolution.Source == SidecarResolveSource.Unresolved)
                return;

            ParsekLog.Verbose(Tag,
                $"Sidecar version resolved: rec={rec?.RecordingId ?? "<null>"} " +
                $"kind={SidecarVersions.Label(resolution.Kind)} hash={resolution.NamedHash} " +
                $"source={(resolution.Source == SidecarResolveSource.Version ? "version" : "head")} " +
                $"promoted={(promoted ? "True" : "False")}");
        }

        /// <summary>
        /// Promotes every version-sourced sidecar of one recording to head in ONE commit
        /// batch: each current head is retained under its own hash (dropped when that
        /// version already exists) and the version moves into the head position. On any
        /// failure the batch rolls back and the caller keeps reading from the version paths.
        /// </summary>
        internal static bool TryPromote(string recordingId, List<SidecarResolution> versions, out string failure)
        {
            failure = null;
            if (versions == null || versions.Count == 0)
                return true;

            var changes = new List<SidecarFileCommitBatch.StagedChange>(versions.Count);
            for (int i = 0; i < versions.Count; i++)
            {
                SidecarResolution r = versions[i];
                changes.Add(new SidecarFileCommitBatch.StagedChange
                {
                    FinalPath = r.HeadPath,
                    StagedPath = r.VersionPath,
                    StagedHash = r.NamedHash,
                    PreserveStagedOnFailure = true,
                    RetainKind = r.Kind,
                    RetainRecordingId = recordingId,
                });
            }

            try
            {
                SidecarFileCommitBatch.Apply(changes, () => RecordingStore.SuppressLogging);
                return true;
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name + ":" + ex.Message;
                return false;
            }
        }
    }
}
