using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Parsek.Tests
{
    /// <summary>
    /// Paths and file helpers shared by the frozen format goldens
    /// (<c>Source/Parsek.Tests/Fixtures/FormatGolden/</c>): the generator that wrote a folder
    /// once, the load tests that read it forever, and the gate that keeps it unchanged.
    /// </summary>
    internal static class FormatGolden
    {
        internal const string ManifestFileName = "formats.txt";
        internal const string ChecksumsFileName = "checksums.txt";
        internal const string ReadmeFileName = "README.md";
        internal const string CareerFolderName = "career";
        internal const string WriteEnvVar = "PARSEK_WRITE_FORMAT_GOLDEN";

        internal static string Root()
        {
            return Path.Combine(SyntheticRecordingTests.ResolveProjectRoot(),
                "Source", "Parsek.Tests", "Fixtures", "FormatGolden");
        }

        internal static string VersionDir(int version)
        {
            return Path.Combine(Root(), "v" + version.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Every <c>vN</c> folder under the root, by N ascending.</summary>
        internal static List<KeyValuePair<int, string>> VersionDirs()
        {
            var result = new List<KeyValuePair<int, string>>();
            string root = Root();
            if (!Directory.Exists(root))
                return result;
            foreach (string dir in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(dir);
                if (name.Length > 1 && name[0] == 'v'
                    && int.TryParse(name.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                    result.Add(new KeyValuePair<int, string>(n, dir));
            }
            result.Sort((a, b) => a.Key.CompareTo(b.Key));
            return result;
        }

        /// <summary>Parses a <c>formats.txt</c>: one <c>name=version</c> per line; blank and <c>#</c> lines skipped.</summary>
        internal static Dictionary<string, int> ReadManifest(string path)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    throw new FormatException("bad manifest line '" + line + "' in " + path);
                string name = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                map[name] = int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
            }
            return map;
        }

        internal static string BuildManifestText(IEnumerable<SaveFormatInfo> formats)
        {
            var sb = new StringBuilder();
            sb.Append("# Format versions this golden folder carries (SaveFormat.All name=version).\n");
            foreach (SaveFormatInfo f in formats)
                sb.Append(f.Name).Append('=').Append(f.Current.ToString(CultureInfo.InvariantCulture)).Append('\n');
            return sb.ToString();
        }

        /// <summary>Lowercase hex SHA-256 of a file's bytes.</summary>
        internal static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>Root-relative path with forward slashes (the checksums file's key).</summary>
        internal static string RelativeKey(string root, string path)
        {
            string rel = path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return rel.Replace(Path.DirectorySeparatorChar, '/');
        }

        /// <summary>
        /// The files the checksum list covers: everything under the root except the root's own
        /// README and the checksum list itself.
        /// </summary>
        internal static List<string> FrozenFiles(string root)
        {
            var files = new List<string>();
            foreach (string path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                string key = RelativeKey(root, path);
                if (key == ChecksumsFileName || key == ReadmeFileName)
                    continue;
                files.Add(path);
            }
            files.Sort(StringComparer.Ordinal);
            return files;
        }

        /// <summary>Parses <c>checksums.txt</c>: <c>&lt;sha256&gt;  &lt;relative path&gt;</c> per line.</summary>
        internal static Dictionary<string, string> ReadChecksums(string path)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                int sep = line.IndexOf("  ", StringComparison.Ordinal);
                if (sep != 64)
                    throw new FormatException("bad checksum line '" + line + "' in " + path);
                map[line.Substring(sep + 2)] = line.Substring(0, 64);
            }
            return map;
        }

        internal static string BuildChecksumsText(Dictionary<string, string> map)
        {
            var sb = new StringBuilder();
            sb.Append("# SHA-256 of every frozen golden file (sha256, two spaces, path under FormatGolden/).\n");
            sb.Append("# Golden saves are frozen: a format bump adds a new vN folder and appends its lines here.\n");
            foreach (string key in map.Keys.OrderBy(k => k, StringComparer.Ordinal))
                sb.Append(map[key]).Append("  ").Append(key).Append('\n');
            return sb.ToString();
        }

        /// <summary>Copies every file under <paramref name="source"/> to the same relative path under <paramref name="dest"/>.</summary>
        internal static void CopyTree(string source, string dest)
        {
            foreach (string path in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string rel = path.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string to = Path.Combine(dest, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(path, to, overwrite: false);
            }
        }

        /// <summary>The PARSEK scenario node of a KSP-shaped persistent.sfs.</summary>
        internal static ConfigNode LoadParsekScenario(string sfsPath)
        {
            ConfigNode root = ConfigNode.Load(sfsPath);
            if (root == null)
                return null;
            ConfigNode game = root.GetNode("GAME") ?? root;
            return game.GetNodes("SCENARIO").FirstOrDefault(s => s.GetValue("name") == "ParsekScenario");
        }
    }
}
