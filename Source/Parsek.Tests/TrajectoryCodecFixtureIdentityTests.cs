using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Parsek.Tests
{
    /// <summary>
    /// The identity manifest over every committed trajectory sidecar (<c>.prec</c>) under
    /// <c>Source/Parsek.Tests/Fixtures/</c> and <c>harness/fixtures/</c>: one row per file
    /// pins what today's binary decoder produces from it, so a later change to the decode
    /// path that alters any decoded value, the bytes the writer re-emits, or the lines the
    /// read logs reds here and names the fixture.
    ///
    /// <para>
    /// A row is computed the way the production cold load reads a sidecar
    /// (<c>RecordingSidecarStore</c>: <c>TryProbeTrajectorySidecar</c>, then
    /// <c>DeserializeTrajectorySidecar</c>) into a <see cref="Recording"/> whose id is the
    /// file name and whose sidecar epoch is the probed one, as the save's metadata carries
    /// them. Columns: the file's SHA-256; the outcome; the probed header; the decoded list
    /// counts and the logged line count; the SHA-256 of a canonical field-level dump of the
    /// decoded state (every field of every element of the six lists, plus the recording
    /// facts the read stamps or side-effects); the SHA-256 of the bytes
    /// <c>TrajectorySidecarBinary.Write</c> produces from that recording; and the SHA-256 of
    /// every Parsek line the probe and the read logged.
    /// </para>
    ///
    /// <para>
    /// Determinism across mono on Linux and .NET Framework on Windows: doubles and floats
    /// are dumped as their IEEE bit patterns (never through <c>ToString("R")</c>, whose
    /// output differs between runtimes), the dump omits a field at its type's default so an
    /// additive field leaves existing rows alone, strings are escaped to ASCII, paths are
    /// repo-relative with '/', rows are sorted ordinally, the file is LF (the fixture tree is
    /// <c>-text</c> in .gitattributes and the reader tolerates CRLF anyway), and the decode
    /// runs under the invariant culture, as KSP pins its main thread to "en".
    /// </para>
    ///
    /// <para>
    /// The comparison is strict in both directions: a <c>.prec</c> on disk with no row reds
    /// (a new fixture is never silently uncovered), a row whose file is gone reds, and a
    /// fixture whose bytes changed reds even when it still decodes. A legitimate fixture
    /// change (a harness re-harvest) is recorded by regenerating the manifest:
    /// <c>PARSEK_WRITE_CODEC_MANIFEST=1 scripts/cloud-test.sh -method
    /// "Parsek.Tests.TrajectoryCodecFixtureIdentityTests.RegenerateManifest"</c> (or the
    /// <c>dotnet test --filter</c> equivalent). The writer is a separate fact that is skipped
    /// unless that variable is exactly "1" and refuses to run under CI, so no ordinary test
    /// run rewrites the manifest; the checking fact never writes.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class TrajectoryCodecFixtureIdentityTests : IDisposable
    {
        internal const string WriteEnvVar = "PARSEK_WRITE_CODEC_MANIFEST";

        internal const string ManifestRelativePath =
            "Source/Parsek.Tests/Fixtures/TrajectoryCodecIdentity/manifest.tsv";

        // The roots walked for committed .prec fixtures, repo-relative.
        internal static readonly string[] FixtureRoots =
        {
            "Source/Parsek.Tests/Fixtures",
            "harness/fixtures",
        };

        // Gitignored operator-local fixtures (harness/fixtures/local-saves/.gitignore): never
        // committed, so never in the manifest.
        internal static readonly string[] ExcludedPrefixes =
        {
            "harness/fixtures/local-saves/",
        };

        internal const string FrozenGoldenPrefix = "Source/Parsek.Tests/Fixtures/FormatGolden/";

        internal static readonly string[] Columns =
        {
            "path", "fileSha256", "status", "header", "counts", "decodeSha256", "writeSha256", "logSha256",
        };

        private const string RegenerateHint =
            "If the change is intended (a re-harvested or added fixture, a deliberate decode change), " +
            "regenerate: " + WriteEnvVar + "=1 scripts/cloud-test.sh -method " +
            "\"Parsek.Tests.TrajectoryCodecFixtureIdentityTests.RegenerateManifest\" and review the row diff.";

        private readonly ITestOutputHelper output;
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public TrajectoryCodecFixtureIdentityTests(ITestOutputHelper output)
        {
            this.output = output;
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            tempDir = Path.Combine(Path.GetTempPath(), "parsek-codec-identity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // ------------------------------------------------------------------ the gate

        [Fact]
        public void EveryCommittedTrajectoryFixture_DecodesAsTheManifestPins()
        {
            string root = RepoRoot();
            List<string> fixtures = EnumerateFixtures(root);
            Dictionary<string, ManifestRow> expected = ReadManifest(Path.Combine(root, ManifestRelativePath));

            // Non-vacuity: the walk finds the frozen goldens and the manifest is not empty.
            Assert.Contains(fixtures, f => f.StartsWith(FrozenGoldenPrefix, StringComparison.Ordinal));
            Assert.NotEmpty(expected);

            var actual = new List<ManifestRow>(fixtures.Count);
            foreach (string rel in fixtures)
                actual.Add(ComputeRow(root, rel));

            List<string> problems = Compare(expected, actual);
            int compared = actual.Count(r => expected.ContainsKey(r.Path));
            output.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "trajectory codec identity: fixtures={0} manifestRows={1} compared={2} problems={3}",
                actual.Count, expected.Count, compared, problems.Count));

            if (problems.Count > 0)
            {
                const int shown = 40;
                var sb = new StringBuilder();
                sb.Append(problems.Count.ToString(CultureInfo.InvariantCulture))
                  .Append(" trajectory fixture identity problem(s); fixtures differing per column: ")
                  .Append(ColumnTally(expected, actual)).Append('\n');
                foreach (string p in problems.Take(shown))
                    sb.Append("  ").Append(p).Append('\n');
                if (problems.Count > shown)
                    sb.Append("  ... and ").Append((problems.Count - shown).ToString(CultureInfo.InvariantCulture))
                      .Append(" more\n");
                sb.Append(RegenerateHint);
                Assert.True(false, sb.ToString());
            }
        }

        // ------------------------------------------------------------------ the writer

        /// <summary>Skipped unless PARSEK_WRITE_CODEC_MANIFEST is exactly "1".</summary>
        internal sealed class WriteCodecManifestFactAttribute : FactAttribute
        {
            public WriteCodecManifestFactAttribute()
            {
                if (Environment.GetEnvironmentVariable(WriteEnvVar) != "1")
                    Skip = "set " + WriteEnvVar + "=1 to regenerate " + ManifestRelativePath;
            }
        }

        [Trait("Category", "Manual")]
        [WriteCodecManifestFact]
        public void RegenerateManifest()
        {
            Assert.True(
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")),
                "refusing to regenerate the trajectory codec manifest under CI");

            string root = RepoRoot();
            var rows = new List<ManifestRow>();
            foreach (string rel in EnumerateFixtures(root))
                rows.Add(ComputeRow(root, rel));

            string path = Path.Combine(root, ManifestRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, FormatManifest(rows), new UTF8Encoding(false));
            output.WriteLine("wrote " + rows.Count.ToString(CultureInfo.InvariantCulture) + " rows to " + path);
        }

        // ------------------------------------------------------------------ mutation proofs

        // One flipped byte inside a fixture reds, naming the fixture and the file digest.
        [Fact]
        public void AFlippedFixtureByte_RedsNamingTheFixture()
        {
            string root = RepoRoot();
            string rel = FirstFrozenGolden(root);
            Dictionary<string, ManifestRow> expected = ReadManifest(Path.Combine(root, ManifestRelativePath));
            Assert.True(expected.ContainsKey(rel), "manifest has no row for " + rel);

            ManifestRow clean = ComputeRow(root, rel);
            Assert.Empty(Compare(Only(expected, rel), new List<ManifestRow> { clean }));

            byte[] bytes = File.ReadAllBytes(Path.Combine(root, rel));
            bytes[bytes.Length - 1] ^= 0x01;
            string mutatedRoot = Path.Combine(tempDir, "mutated-root");
            string mutatedAbs = Path.Combine(mutatedRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(mutatedAbs));
            File.WriteAllBytes(mutatedAbs, bytes);

            ManifestRow mutated = ComputeRow(mutatedRoot, rel);
            List<string> problems = Compare(Only(expected, rel), new List<ManifestRow> { mutated });

            Assert.Single(problems);
            Assert.StartsWith(rel + ": differs in fileSha256", problems[0]);
            Assert.Contains("frozen FormatGolden file changed", problems[0]);
        }

        // One decoded value moved by one unit in the last place reds, naming the fixture and
        // the decode digest, with the file bytes unchanged.
        [Fact]
        public void OneDecodedValueMovedByOneUlp_RedsNamingTheFixture()
        {
            string root = RepoRoot();
            string rel = FirstFrozenGolden(root);
            Dictionary<string, ManifestRow> expected = ReadManifest(Path.Combine(root, ManifestRelativePath));

            ManifestRow mutated = ComputeRow(root, rel, rec =>
            {
                Assert.NotEmpty(rec.TrackSections);
                TrackSection s = rec.TrackSections[0];
                s.endUT = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(s.endUT) + 1);
                rec.TrackSections[0] = s;
            });
            List<string> problems = Compare(Only(expected, rel), new List<ManifestRow> { mutated });

            Assert.Single(problems);
            Assert.StartsWith(rel + ": differs in decodeSha256", problems[0]);
            Assert.DoesNotContain("fileSha256", problems[0]);
        }

        // A fixture on disk with no row, and a row with no file, each red by name.
        [Fact]
        public void AnUnlistedFixtureAndAMissingFixture_EachRedByName()
        {
            var expected = new Dictionary<string, ManifestRow>(StringComparer.Ordinal)
            {
                ["harness/fixtures/saves/gone/Parsek/Recordings/a.prec"] = Row("harness/fixtures/saves/gone/Parsek/Recordings/a.prec"),
            };
            var actual = new List<ManifestRow> { Row("harness/fixtures/saves/new/Parsek/Recordings/b.prec") };

            List<string> problems = Compare(expected, actual);

            Assert.Contains(problems, p => p.StartsWith("harness/fixtures/saves/new/Parsek/Recordings/b.prec:", StringComparison.Ordinal)
                                           && p.Contains("no manifest row"));
            Assert.Contains(problems, p => p.StartsWith("harness/fixtures/saves/gone/Parsek/Recordings/a.prec:", StringComparison.Ordinal)
                                           && p.Contains("missing"));
        }

        [Fact]
        public void TheCanonicalDump_SeesEveryLeafKindAndOmitsDefaults()
        {
            var rec = new Recording { RecordingId = "dumptest" };
            rec.Points.Add(new TrajectoryPoint { ut = 1.0, bodyName = "Kerbin" });
            rec.Points.Add(default(TrajectoryPoint));
            rec.SegmentEvents.Add(new SegmentEvent { ut = 2.0, details = "" });
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                frames = new List<TrajectoryPoint>(),
                minAltitude = float.NaN,
            });
            string dump = CanonicalDump(rec, default(TrajectorySidecarProbe), null);

            Assert.Contains("Points.count=2\n", dump);
            Assert.Contains("Points[0].ut=d:3FF0000000000000\n", dump);
            Assert.Contains("Points[0].bodyName=s:Kerbin\n", dump);
            Assert.DoesNotContain("Points[1].", dump);
            Assert.DoesNotContain("Points[0].latitude", dump);
            Assert.Contains("SegmentEvents[0].details=s:\n", dump); // empty string is not null
            Assert.Contains("TrackSections[0].frames.count=0\n", dump); // empty list is not null
            Assert.DoesNotContain("TrackSections[0].bodyFixedFrames", dump);
            Assert.Contains("TrackSections[0].minAltitude=f:FFC00000\n", dump);
            Assert.Contains("TrackSections[0].referenceFrame=e:", dump);

            var negZero = new Recording { RecordingId = "dumptest" };
            negZero.Points.Add(new TrajectoryPoint { ut = -0.0 });
            negZero.Points.Add(default(TrajectoryPoint));
            Assert.NotEqual(dump, CanonicalDump(negZero, default(TrajectorySidecarProbe), null));
            Assert.Contains("Points[0].ut=d:8000000000000000\n",
                CanonicalDump(negZero, default(TrajectorySidecarProbe), null));
        }

        [Fact]
        public void TheManifestText_RoundTripsAndToleratesCrlf()
        {
            var rows = new List<ManifestRow> { Row("b/x.prec"), Row("a/y.prec") };
            string text = FormatManifest(rows);
            Assert.DoesNotContain("\r", text);
            Assert.True(text.IndexOf("a/y.prec", StringComparison.Ordinal)
                        < text.IndexOf("b/x.prec", StringComparison.Ordinal));

            string path = Path.Combine(tempDir, "m.tsv");
            File.WriteAllText(path, text.Replace("\n", "\r\n"), new UTF8Encoding(false));
            Dictionary<string, ManifestRow> parsed = ReadManifest(path);
            Assert.Equal(2, parsed.Count);
            Assert.Empty(Compare(parsed, rows));
        }

        // ------------------------------------------------------------------ row computation

        internal sealed class ManifestRow
        {
            public string Path;
            public string FileSha256;
            public string Status;
            public string Header;
            public string Counts;
            public string DecodeSha256;
            public string WriteSha256;
            public string LogSha256;

            public string[] Cells()
            {
                return new[] { Path, FileSha256, Status, Header, Counts, DecodeSha256, WriteSha256, LogSha256 };
            }
        }

        private ManifestRow ComputeRow(string root, string rel, Action<Recording> afterRead = null)
        {
            string abs = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            var row = new ManifestRow
            {
                Path = rel,
                FileSha256 = Sha256Hex(File.ReadAllBytes(abs)),
                Status = "ok",
                Header = "-",
                Counts = "-",
                DecodeSha256 = "-",
                WriteSha256 = "-",
                LogSha256 = "-",
            };

            CultureInfo priorCulture = CultureInfo.CurrentCulture;
            CultureInfo priorUiCulture = CultureInfo.CurrentUICulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                System.Threading.Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
                ParsekLog.ResetRateLimitsForTesting();
                logLines.Clear();

                TrajectorySidecarProbe probe;
                bool probed = RecordingSidecarStore.TryProbeTrajectorySidecar(abs, out probe);
                if (probed)
                {
                    row.Header = string.Format(CultureInfo.InvariantCulture,
                        "enc={0};v={1};g={2};e={3}",
                        (int)probe.Encoding, probe.FormatVersion, probe.SchemaGeneration, probe.SidecarEpoch);
                }

                Recording rec = null;
                ListInstances before = null;
                if (!probed)
                {
                    row.Status = "probe-failed";
                }
                else if (!probe.Supported)
                {
                    row.Status = "unsupported";
                }
                else
                {
                    // As the cold load: the recording's id and epoch come from the save's
                    // metadata, which names the file after the id.
                    rec = new Recording
                    {
                        RecordingId = System.IO.Path.GetFileNameWithoutExtension(abs),
                        SidecarEpoch = probe.SidecarEpoch,
                    };
                    before = new ListInstances(rec);
                    try
                    {
                        RecordingSidecarStore.DeserializeTrajectorySidecar(abs, probe, rec);
                    }
                    catch (Exception ex)
                    {
                        row.Status = "read-threw:" + ex.GetType().FullName;
                    }
                }

                var readLines = new List<string>(logLines.Count);
                foreach (string line in logLines)
                    readLines.Add(NormalizeLogLine(line, abs, rel));
                row.LogSha256 = Sha256Hex(Encoding.UTF8.GetBytes(string.Join("\n", readLines)));

                if (rec != null)
                {
                    afterRead?.Invoke(rec);
                    row.Counts = string.Format(CultureInfo.InvariantCulture,
                        "p={0};o={1};pe={2};fe={3};se={4};ts={5};lines={6}",
                        rec.Points.Count, rec.OrbitSegments.Count, rec.PartEvents.Count,
                        rec.FlagEvents.Count, rec.SegmentEvents.Count, rec.TrackSections.Count,
                        readLines.Count);
                    row.DecodeSha256 = Sha256Hex(Encoding.UTF8.GetBytes(CanonicalDump(rec, probe, before)));

                    if (row.Status == "ok")
                    {
                        // Write mutates the recording (checkpoint sections), so it runs after the dump.
                        string writePath = System.IO.Path.Combine(tempDir, "write.prec");
                        TrajectorySidecarBinary.Write(writePath, rec, probe.SidecarEpoch);
                        row.WriteSha256 = Sha256Hex(File.ReadAllBytes(writePath));
                        File.Delete(writePath);
                    }
                }
                else
                {
                    row.Counts = string.Format(CultureInfo.InvariantCulture, "lines={0}", readLines.Count);
                }
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = priorCulture;
                System.Threading.Thread.CurrentThread.CurrentUICulture = priorUiCulture;
                logLines.Clear();
            }
            return row;
        }

        private static string NormalizeLogLine(string line, string abs, string rel)
        {
            string forward = abs.Replace('\\', '/');
            string backward = abs.Replace('/', '\\');
            return line.Replace(abs, rel).Replace(forward, rel).Replace(backward, rel);
        }

        // The six list instances before the read, so the dump records whether the read kept
        // them (a heal replaces Points / OrbitSegments with new lists).
        private sealed class ListInstances
        {
            private readonly object[] lists;

            public ListInstances(Recording rec)
            {
                lists = Lists(rec);
            }

            public static object[] Lists(Recording rec)
            {
                return new object[]
                {
                    rec.Points, rec.OrbitSegments, rec.PartEvents, rec.FlagEvents, rec.SegmentEvents, rec.TrackSections,
                };
            }

            public string Describe(Recording rec)
            {
                object[] now = Lists(rec);
                var sb = new StringBuilder();
                for (int i = 0; i < ListNames.Length; i++)
                {
                    sb.Append("rec.").Append(ListNames[i]).Append(".sameInstance=")
                      .Append(ReferenceEquals(lists[i], now[i]) ? "true" : "false").Append('\n');
                }
                return sb.ToString();
            }
        }

        private static readonly string[] ListNames =
        {
            "Points", "OrbitSegments", "PartEvents", "FlagEvents", "SegmentEvents", "TrackSections",
        };

        // ------------------------------------------------------------------ canonical dump

        internal static string CanonicalDump(Recording rec, TrajectorySidecarProbe probe, object listInstances)
        {
            var ic = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("probe.Encoding=").Append(((int)probe.Encoding).ToString(ic)).Append('\n');
            sb.Append("probe.FormatVersion=").Append(probe.FormatVersion.ToString(ic)).Append('\n');
            sb.Append("probe.SchemaGeneration=").Append(probe.SchemaGeneration.ToString(ic)).Append('\n');
            sb.Append("probe.SidecarEpoch=").Append(probe.SidecarEpoch.ToString(ic)).Append('\n');
            sb.Append("probe.RecordingId=").Append(Escape(probe.RecordingId)).Append('\n');
            sb.Append("rec.RecordingId=").Append(Escape(rec.RecordingId)).Append('\n');
            sb.Append("rec.RecordingFormatVersion=").Append(rec.RecordingFormatVersion.ToString(ic)).Append('\n');
            sb.Append("rec.RecordingSchemaGeneration=").Append(rec.RecordingSchemaGeneration.ToString(ic)).Append('\n');
            sb.Append("rec.SidecarEpoch=").Append(rec.SidecarEpoch.ToString(ic)).Append('\n');
            sb.Append("rec.FilesDirty=").Append(rec.FilesDirty ? "true" : "false").Append('\n');
            sb.Append("rec.CachedStats.HasValue=").Append(rec.CachedStats.HasValue ? "true" : "false").Append('\n');
            sb.Append("rec.CachedStatsPointCount=").Append(rec.CachedStatsPointCount.ToString(ic)).Append('\n');
            if (listInstances is ListInstances li)
                sb.Append(li.Describe(rec));

            var dumper = new FieldDumper(sb);
            dumper.Dump("Points", rec.Points);
            dumper.Dump("OrbitSegments", rec.OrbitSegments);
            dumper.Dump("PartEvents", rec.PartEvents);
            dumper.Dump("FlagEvents", rec.FlagEvents);
            dumper.Dump("SegmentEvents", rec.SegmentEvents);
            dumper.Dump("TrackSections", rec.TrackSections);
            return sb.ToString();
        }

        /// <summary>
        /// Walks a value's instance fields (public and private, ordinal by name) and writes one
        /// line per non-default leaf. A type outside the Parsek assembly that is not a Unity
        /// value type it knows is refused loudly, so a new field of an unmodelled type reds
        /// instead of hashing a reference's identity.
        /// </summary>
        private sealed class FieldDumper
        {
            private readonly StringBuilder sb;
            private readonly Dictionary<Type, FieldInfo[]> fieldCache = new Dictionary<Type, FieldInfo[]>();

            public FieldDumper(StringBuilder sb)
            {
                this.sb = sb;
            }

            public void Dump(string path, object value)
            {
                Dump(path, value, 0);
            }

            private void Dump(string path, object value, int depth)
            {
                if (value == null)
                    return;
                if (depth > 16)
                    throw new NotSupportedException("canonical dump too deep at " + path);

                var ic = CultureInfo.InvariantCulture;
                Type t = value.GetType();
                if (t == typeof(double))
                {
                    long bits = BitConverter.DoubleToInt64Bits((double)value);
                    if (bits != 0)
                        Line(path, "d:" + bits.ToString("X16", ic));
                    return;
                }
                if (t == typeof(float))
                {
                    int bits = BitConverter.ToInt32(BitConverter.GetBytes((float)value), 0);
                    if (bits != 0)
                        Line(path, "f:" + bits.ToString("X8", ic));
                    return;
                }
                if (t == typeof(string))
                {
                    Line(path, "s:" + Escape((string)value));
                    return;
                }
                if (t == typeof(bool))
                {
                    if ((bool)value)
                        Line(path, "true");
                    return;
                }
                if (t.IsEnum)
                {
                    long n = Convert.ToInt64(value, ic);
                    if (n != 0)
                        Line(path, "e:" + n.ToString(ic));
                    return;
                }
                if (t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort)
                    || t == typeof(int) || t == typeof(uint) || t == typeof(long))
                {
                    long n = Convert.ToInt64(value, ic);
                    if (n != 0)
                        Line(path, n.ToString(ic));
                    return;
                }
                if (t == typeof(ulong))
                {
                    ulong n = (ulong)value;
                    if (n != 0)
                        Line(path, n.ToString(ic));
                    return;
                }
                if (value is IList list)
                {
                    Line(path + ".count", list.Count.ToString(ic));
                    for (int i = 0; i < list.Count; i++)
                        Dump(path + "[" + i.ToString(ic) + "]", list[i], depth + 1);
                    return;
                }

                bool modelled = t.Assembly == typeof(Recording).Assembly
                                || (t.IsValueType && t.Namespace == "UnityEngine");
                if (!modelled)
                    throw new NotSupportedException(
                        "canonical dump has no rule for " + t.FullName + " at " + path);

                foreach (FieldInfo f in Fields(t))
                    Dump(path + "." + f.Name, f.GetValue(value), depth + 1);
            }

            private FieldInfo[] Fields(Type t)
            {
                FieldInfo[] fields;
                if (!fieldCache.TryGetValue(t, out fields))
                {
                    fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .OrderBy(f => f.Name, StringComparer.Ordinal)
                        .ToArray();
                    fieldCache[t] = fields;
                }
                return fields;
            }

            private void Line(string path, string value)
            {
                sb.Append(path).Append('=').Append(value).Append('\n');
            }
        }

        private static string Escape(string s)
        {
            if (s == null)
                return "<null>";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c < 0x20 || c > 0x7E || c == '\\')
                    sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ manifest I/O + compare

        // One problem per fixture, ordered so the strongest signal comes first: a fixture on
        // disk with no row, a row with no file, changed fixture bytes, then decode drift.
        // A digest column is named; a short column (status, header, counts) shows both values.
        internal static List<string> Compare(Dictionary<string, ManifestRow> expected, List<ManifestRow> actual)
        {
            var coverage = new List<string>();
            var bytesChanged = new List<string>();
            var drift = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ManifestRow a in actual.OrderBy(r => r.Path, StringComparer.Ordinal))
            {
                seen.Add(a.Path);
                ManifestRow e;
                if (!expected.TryGetValue(a.Path, out e))
                {
                    coverage.Add(a.Path + ": committed trajectory fixture has no manifest row");
                    continue;
                }
                bool frozen = a.Path.StartsWith(FrozenGoldenPrefix, StringComparison.Ordinal);
                string[] ec = e.Cells();
                string[] ac = a.Cells();
                var parts = new List<string>();
                for (int i = 1; i < Columns.Length; i++)
                {
                    if (string.Equals(ec[i], ac[i], StringComparison.Ordinal))
                        continue;
                    parts.Add(Columns[i].EndsWith("Sha256", StringComparison.Ordinal)
                        ? Columns[i]
                        : Columns[i] + " " + ec[i] + " -> " + ac[i]);
                }
                if (parts.Count == 0)
                    continue;
                bool fileChanged = !string.Equals(ec[1], ac[1], StringComparison.Ordinal);
                string note = fileChanged
                    ? (frozen ? " (a frozen FormatGolden file changed: never edit it)" : " (fixture bytes changed)")
                    : (frozen ? " (frozen FormatGolden fixture: a decode change here breaks a 1.0 save)" : string.Empty);
                string line = a.Path + ": differs in " + string.Join(", ", parts) + note;
                (fileChanged ? bytesChanged : drift).Add(line);
            }
            foreach (string path in expected.Keys.OrderBy(p => p, StringComparer.Ordinal))
            {
                if (!seen.Contains(path))
                    coverage.Add(path + ": manifest row names a fixture that is missing on disk");
            }
            var problems = new List<string>(coverage.Count + bytesChanged.Count + drift.Count);
            problems.AddRange(coverage);
            problems.AddRange(bytesChanged);
            problems.AddRange(drift);
            return problems;
        }

        // Per column, how many fixtures differ: tells a decode drift (decode / write) from a
        // log-only drift from a fixture edit at a glance.
        internal static string ColumnTally(Dictionary<string, ManifestRow> expected, List<ManifestRow> actual)
        {
            var counts = new int[Columns.Length];
            foreach (ManifestRow a in actual)
            {
                ManifestRow e;
                if (!expected.TryGetValue(a.Path, out e))
                    continue;
                string[] ec = e.Cells();
                string[] ac = a.Cells();
                for (int i = 1; i < Columns.Length; i++)
                {
                    if (!string.Equals(ec[i], ac[i], StringComparison.Ordinal))
                        counts[i]++;
                }
            }
            var parts = new List<string>();
            for (int i = 1; i < Columns.Length; i++)
                parts.Add(Columns[i] + "=" + counts[i].ToString(CultureInfo.InvariantCulture));
            return string.Join(" ", parts);
        }

        internal static string FormatManifest(List<ManifestRow> rows)
        {
            var sb = new StringBuilder();
            sb.Append("# Trajectory codec identity manifest. GENERATED by TrajectoryCodecFixtureIdentityTests;\n");
            sb.Append("# do not edit by hand. Regenerate with " + WriteEnvVar + "=1 (see the test class).\n");
            sb.Append(string.Join("\t", Columns)).Append('\n');
            foreach (ManifestRow r in rows.OrderBy(x => x.Path, StringComparer.Ordinal))
                sb.Append(string.Join("\t", r.Cells())).Append('\n');
            return sb.ToString();
        }

        internal static Dictionary<string, ManifestRow> ReadManifest(string path)
        {
            Assert.True(File.Exists(path), "trajectory codec manifest missing: " + path);
            var rows = new Dictionary<string, ManifestRow>(StringComparer.Ordinal);
            string header = string.Join("\t", Columns);
            int lineNo = 0;
            foreach (string raw in File.ReadAllText(path).Split('\n'))
            {
                lineNo++;
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#' || line == header)
                    continue;
                string[] cells = line.Split('\t');
                Assert.True(cells.Length == Columns.Length, string.Format(CultureInfo.InvariantCulture,
                    "{0}:{1}: expected {2} tab-separated cells, found {3}", path, lineNo, Columns.Length, cells.Length));
                var row = new ManifestRow
                {
                    Path = cells[0],
                    FileSha256 = cells[1],
                    Status = cells[2],
                    Header = cells[3],
                    Counts = cells[4],
                    DecodeSha256 = cells[5],
                    WriteSha256 = cells[6],
                    LogSha256 = cells[7],
                };
                Assert.False(rows.ContainsKey(row.Path), path + ": duplicate row " + row.Path);
                rows[row.Path] = row;
            }
            return rows;
        }

        // ------------------------------------------------------------------ helpers

        internal static List<string> EnumerateFixtures(string root)
        {
            var result = new List<string>();
            foreach (string fixtureRoot in FixtureRoots)
            {
                string dir = Path.Combine(root, fixtureRoot.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(Directory.Exists(dir), "fixture root missing: " + dir);
                foreach (string abs in Directory.GetFiles(dir, "*.prec", SearchOption.AllDirectories))
                {
                    string rel = abs.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
                    if (ExcludedPrefixes.Any(p => rel.StartsWith(p, StringComparison.Ordinal)))
                        continue;
                    result.Add(rel);
                }
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private static string FirstFrozenGolden(string root)
        {
            string rel = EnumerateFixtures(root)
                .FirstOrDefault(f => f.StartsWith(FrozenGoldenPrefix, StringComparison.Ordinal));
            Assert.NotNull(rel);
            return rel;
        }

        private static Dictionary<string, ManifestRow> Only(Dictionary<string, ManifestRow> rows, string path)
        {
            return new Dictionary<string, ManifestRow>(StringComparer.Ordinal) { [path] = rows[path] };
        }

        private static ManifestRow Row(string path)
        {
            return new ManifestRow
            {
                Path = path, FileSha256 = "f", Status = "ok", Header = "h", Counts = "c",
                DecodeSha256 = "d", WriteSha256 = "w", LogSha256 = "l",
            };
        }

        private static string RepoRoot()
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            Assert.True(Directory.Exists(Path.Combine(root, "Source", "Parsek.Tests")),
                "repo root not found from " + AppDomain.CurrentDomain.BaseDirectory);
            return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }
}
