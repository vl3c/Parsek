using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace Parsek.InGameTests.Spikes
{
    // R0 spike branch only. Every spike measurement is one log line plus one CSV row
    // under <KSP root>/parsek-spike/<Sx>/<name>.csv (plan r0-spikes.md, S0). Numbers
    // are written with the invariant culture so a ro-RO or de-DE PC writes "1.5",
    // never "1,5". The owner copies the folder to <umbrella>/logs/spikes/<Sx>/<date>/.

    /// <summary>Pure CSV formatting and path rules (xUnit-tested), plus the live writers.</summary>
    internal static class SpikeCsv
    {
        internal const string RootFolderName = "parsek-spike";

        /// <summary>
        /// One field. Null -> empty. double / float -> round-trip "R". Other
        /// IFormattable -> invariant. bool -> "true" / "false". Text with a comma,
        /// quote, CR or LF is quoted with inner quotes doubled (RFC 4180).
        /// </summary>
        internal static string FormatField(object value)
        {
            string text;
            if (value == null) text = "";
            else if (value is bool b) text = b ? "true" : "false";
            else if (value is double d) text = d.ToString("R", CultureInfo.InvariantCulture);
            else if (value is float f) text = f.ToString("R", CultureInfo.InvariantCulture);
            else if (value is IFormattable fmt) text = fmt.ToString(null, CultureInfo.InvariantCulture);
            else text = value.ToString() ?? "";

            if (text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>One row, fields joined by commas, no line ending.</summary>
        internal static string FormatRow(params object[] fields)
        {
            if (fields == null || fields.Length == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(FormatField(fields[i]));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Null when <paramref name="segment"/> is a safe single path segment, else the
        /// reason. Spike ids and file names never carry a separator or "..".
        /// </summary>
        internal static string ValidateSegment(string segment)
        {
            if (string.IsNullOrEmpty(segment)) return "empty";
            if (segment.Trim() != segment) return "leading or trailing space";
            if (segment == "." || segment == "..") return "dot segment";
            if (segment.IndexOf('/') >= 0 || segment.IndexOf('\\') >= 0) return "path separator";
            if (segment.IndexOf(':') >= 0) return "colon";
            foreach (char c in segment)
            {
                if (c < 32) return "control character";
                if ("<>\"|?*".IndexOf(c) >= 0) return "invalid character '" + c + "'";
            }
            return null;
        }

        /// <summary>
        /// &lt;kspRoot&gt;/parsek-spike/&lt;spikeId&gt;/&lt;name&gt;.csv (".csv" added when missing).
        /// Throws ArgumentException on an unsafe id or name.
        /// </summary>
        internal static string BuildPath(string kspRoot, string spikeId, string name)
        {
            if (string.IsNullOrEmpty(kspRoot)) throw new ArgumentException("KSP root is empty", nameof(kspRoot));
            string why = ValidateSegment(spikeId);
            if (why != null) throw new ArgumentException("bad spike id '" + spikeId + "': " + why, nameof(spikeId));
            why = ValidateSegment(name);
            if (why != null) throw new ArgumentException("bad csv name '" + name + "': " + why, nameof(name));
            string file = name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? name : name + ".csv";
            return Path.Combine(Path.Combine(Path.Combine(kspRoot, RootFolderName), spikeId), file);
        }

        /// <summary>The full path of &lt;KSP root&gt;/parsek-spike (live only).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static string ResolveSpikeRoot()
            => Path.GetFullPath(Path.Combine(ReadKspRootCore(), RootFolderName));

        /// <summary>Live path of one spike CSV under the running KSP's root.</summary>
        internal static string PathFor(string spikeId, string name)
            => Path.GetFullPath(BuildPath(ReadKspRootCore(), spikeId, name));

        // KSPUtil is a game type: keep the read out of every method a test calls.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ReadKspRootCore() => KSPUtil.ApplicationRootPath ?? "";

        /// <summary>
        /// Appends one row to a spike CSV, writing the header first when the file is
        /// new or empty. Opens and closes the file per call: for one-shot rows. A
        /// per-frame producer uses <see cref="Open"/>.
        /// </summary>
        internal static string AppendRow(string spikeId, string name, string[] header, params object[] fields)
        {
            string path = PathFor(spikeId, name);
            AppendRowAtPath(path, header, fields);
            return path;
        }

        /// <summary>The file work of <see cref="AppendRow"/> for a resolved path (xUnit-tested).</summary>
        internal static void AppendRowAtPath(string path, string[] header, params object[] fields)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var sb = new StringBuilder();
            if (header != null && header.Length > 0 && IsMissingOrEmpty(path))
                sb.Append(FormatRow(header)).Append('\n');
            sb.Append(FormatRow(fields)).Append('\n');
            File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>A writer kept open for many rows (each row is flushed).</summary>
        internal static SpikeCsvWriter Open(string spikeId, string name, params string[] header)
            => SpikeCsvWriter.OpenAtPath(PathFor(spikeId, name), header);

        internal static bool IsMissingOrEmpty(string path)
        {
            var info = new FileInfo(path);
            return !info.Exists || info.Length == 0;
        }
    }

    /// <summary>Append-mode CSV writer; header written only into a new or empty file.</summary>
    internal sealed class SpikeCsvWriter : IDisposable
    {
        private StreamWriter writer;

        internal string Path { get; }
        internal int RowsWritten { get; private set; }

        private SpikeCsvWriter(string path, StreamWriter writer)
        {
            Path = path;
            this.writer = writer;
        }

        internal static SpikeCsvWriter OpenAtPath(string path, params string[] header)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            bool writeHeader = header != null && header.Length > 0 && SpikeCsv.IsMissingOrEmpty(path);
            var sw = new StreamWriter(path, append: true, encoding: new UTF8Encoding(false));
            sw.NewLine = "\n";
            if (writeHeader)
            {
                sw.WriteLine(SpikeCsv.FormatRow(header));
                sw.Flush();
            }
            return new SpikeCsvWriter(path, sw);
        }

        internal void WriteRow(params object[] fields)
        {
            if (writer == null) throw new ObjectDisposedException(nameof(SpikeCsvWriter));
            writer.WriteLine(SpikeCsv.FormatRow(fields));
            writer.Flush();
            RowsWritten++;
        }

        public void Dispose()
        {
            if (writer == null) return;
            writer.Dispose();
            writer = null;
        }
    }
}
