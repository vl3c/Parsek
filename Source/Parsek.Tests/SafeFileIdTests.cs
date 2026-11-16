using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The id check under every sidecar path: a fixed refused set (the 41 chars .NET reports on
    /// Windows) plus the path-sequence rule, the same on every operating system and in every
    /// culture, and every id shape Parsek produces or a committed save carries still accepted.
    /// </summary>
    [Collection("Sequential")]
    public class SafeFileIdTests : IDisposable
    {
        // Written out by hand, independent of the production table: NUL, 1 to 31, and the nine
        // printable members.
        private static readonly char[] ExpectedRefusedPrintable =
            { '"', '<', '>', '|', ':', '*', '?', '\\', '/' };

        public SafeFileIdTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public static IEnumerable<object[]> FixedRefusedChars()
        {
            for (int c = 0; c < 32; c++)
                yield return new object[] { c };
            for (int i = 0; i < ExpectedRefusedPrintable.Length; i++)
                yield return new object[] { (int)ExpectedRefusedPrintable[i] };
        }

        [Theory]
        [InlineData(null, "NullOrEmpty")]
        [InlineData("", "NullOrEmpty")]
        [InlineData("..", "PathSequence")]
        [InlineData("../escape", "PathSequence")]
        [InlineData("..\\escape", "PathSequence")]
        [InlineData("a/b", "PathSequence")]
        [InlineData("a\\b", "PathSequence")]
        [InlineData("ok..ok", "PathSequence")]
        [InlineData("C:", "InvalidChar")]
        [InlineData("rec:id", "InvalidChar")]
        [InlineData("rec\u0000id", "InvalidChar")]
        [InlineData("rec\nid", "InvalidChar")]
        [InlineData("abc123def", "Ok")]
        public void Check_ReturnsEachVerdict(string id, string expected)
        {
            Assert.Equal(expected, SafeFileId.Check(id).ToString());
        }

        [Theory]
        [MemberData(nameof(FixedRefusedChars))]
        public void Check_RefusesEachCharOfTheFixedSet(int code)
        {
            char c = (char)code;
            string id = "rec" + c + "id";
            SafeFileIdVerdict expected = c == '/' || c == '\\'
                ? SafeFileIdVerdict.PathSequence
                : SafeFileIdVerdict.InvalidChar;

            Assert.True(SafeFileId.IsInvalidChar(c));
            Assert.Equal(expected, SafeFileId.Check(id));
            Assert.False(RecordingPaths.ValidateRecordingId(id, RecordingIdValidationLogContext.Test));
        }

        [Fact]
        public void TheFixedSetIsExactlyThe41WindowsChars()
        {
            var refused = new List<int>();
            for (int code = char.MinValue; code <= char.MaxValue; code++)
            {
                if (SafeFileId.IsInvalidChar((char)code))
                    refused.Add(code);
            }

            var expected = new List<int>();
            foreach (object[] row in FixedRefusedChars())
                expected.Add((int)row[0]);
            expected.Sort();

            Assert.Equal(41, refused.Count);
            Assert.Equal(expected, refused);
        }

        [Fact]
        public void ValidateRecordingId_RefusesColonOnEveryOperatingSystem()
        {
            // Mono on Linux and macOS reports only NUL and '/' as invalid file-name chars, so
            // before the fixed set this id passed there and failed on Windows.
            Assert.False(RecordingPaths.ValidateRecordingId("rec:id", RecordingIdValidationLogContext.Test));
            Assert.False(RecordingPaths.ValidateRecordingId("rec|id", RecordingIdValidationLogContext.Test));
            Assert.False(RecordingPaths.ValidateRecordingId("rec?id", RecordingIdValidationLogContext.Test));
        }

        [Fact]
        public void TheRunningPlatformsInvalidCharsAreASubsetOfTheFixedSet()
        {
            char[] platform = Path.GetInvalidFileNameChars();
            for (int i = 0; i < platform.Length; i++)
                Assert.True(SafeFileId.IsInvalidChar(platform[i]),
                    "platform invalid char U+" + ((int)platform[i]).ToString("X4", CultureInfo.InvariantCulture)
                    + " is not in the fixed set");
        }

        [Fact]
        public void EveryProducedIdShapeIsAccepted()
        {
            Guid g = Guid.NewGuid();
            var produced = new[]
            {
                g.ToString("N"),                                          // Recording.RecordingId
                g.ToString("D", CultureInfo.InvariantCulture),            // switch continuation
                "rec_" + g.ToString("N"),                                 // Re-Fly provisional
                "rp_" + g.ToString("N"),                                  // rewind point
                "mj_" + g.ToString("N"),                                  // merge journal
            };
            for (int i = 0; i < produced.Length; i++)
            {
                Assert.Equal(SafeFileIdVerdict.Ok, SafeFileId.Check(produced[i]));
                Assert.True(RecordingPaths.ValidateRecordingId(produced[i], RecordingIdValidationLogContext.Test),
                    produced[i]);
            }
        }

        [Theory]
        [InlineData("id with spaces")]
        [InlineData("a.b")]
        [InlineData(".hidden")]
        [InlineData("trailing.")]
        [InlineData("Jebediah-ÄŁ日本")]
        [InlineData("abc-123")]
        public void IdsWindowsAcceptsTodayStayAccepted(string id)
        {
            Assert.Equal(SafeFileIdVerdict.Ok, SafeFileId.Check(id));
            Assert.True(RecordingPaths.ValidateRecordingId(id, RecordingIdValidationLogContext.Test));
        }

        [Theory]
        [InlineData("tr-TR")]
        [InlineData("de-DE")]
        [InlineData("ja-JP")]
        public void TheVerdictDoesNotDependOnTheCulture(string cultureName)
        {
            string[] ids = { "INVALID:ID", "Iiİı", "a..b", "a․․b", "rec_" + Guid.Empty.ToString("N") };
            var invariant = new SafeFileIdVerdict[ids.Length];
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                for (int i = 0; i < ids.Length; i++)
                    invariant[i] = SafeFileId.Check(ids[i]);

                Thread.CurrentThread.CurrentCulture = new CultureInfo(cultureName);
                for (int i = 0; i < ids.Length; i++)
                    Assert.Equal(invariant[i], SafeFileId.Check(ids[i]));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }

            Assert.Equal(SafeFileIdVerdict.InvalidChar, invariant[0]);
            Assert.Equal(SafeFileIdVerdict.Ok, invariant[1]);
            Assert.Equal(SafeFileIdVerdict.PathSequence, invariant[2]);
            // U+2024 ONE DOT LEADER is not '.', so two of them are not a path sequence.
            Assert.Equal(SafeFileIdVerdict.Ok, invariant[3]);
            Assert.Equal(SafeFileIdVerdict.Ok, invariant[4]);
        }

        // Sidecar file names written next to the id, per RecordingPaths.Build*RelativePath.
        private static readonly string[] SidecarSuffixes =
        {
            ".prec.txt", "_vessel.craft.txt", "_ghost.craft.txt",
            ".prec", ".pann", "_vessel.craft", "_ghost.craft", ".pcrf", ".sfs",
        };

        private static readonly Regex IdKeyLine = new Regex(
            @"^\s*\w*(?:[Rr]ecordingId|[Rr]ewindPointId)\s*=\s*(\S.*?)\s*$",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// Every recording and rewind-point id a committed save carries (each sidecar file name
        /// under a <c>Parsek/Recordings</c> or <c>Parsek/RewindPoints</c> folder, and every
        /// <c>*RecordingId</c> / <c>*RewindPointId</c> value in a committed <c>.sfs</c>) is
        /// accepted, so no committed save loses a sidecar to the fixed set.
        /// </summary>
        [Fact]
        public void EveryIdInTheCommittedFixturesIsAccepted()
        {
            string repo = SyntheticRecordingTests.ProjectRoot;
            string[] roots =
            {
                Path.Combine(repo, "Source", "Parsek.Tests", "Fixtures"),
                Path.Combine(repo, "harness", "fixtures"),
            };
            // Operator-local fixtures are gitignored and machine-specific.
            string localSaves = Path.GetFullPath(Path.Combine(repo, "harness", "fixtures", "local-saves"));

            var ids = new HashSet<string>(StringComparer.Ordinal);
            int saveFiles = 0, sidecarIds = 0;
            for (int r = 0; r < roots.Length; r++)
            {
                Assert.True(Directory.Exists(roots[r]), "fixture root missing: " + roots[r]);
                foreach (string file in Directory.GetFiles(roots[r], "*", SearchOption.AllDirectories))
                {
                    string full = Path.GetFullPath(file);
                    if (full.StartsWith(localSaves, StringComparison.Ordinal))
                        continue;

                    string dir = Path.GetDirectoryName(full);
                    string dirName = Path.GetFileName(dir);
                    if ((dirName == "Recordings" || dirName == "RewindPoints")
                        && Path.GetFileName(Path.GetDirectoryName(dir)) == "Parsek")
                    {
                        string name = Path.GetFileName(full);
                        for (int s = 0; s < SidecarSuffixes.Length; s++)
                        {
                            if (name.EndsWith(SidecarSuffixes[s], StringComparison.Ordinal))
                            {
                                ids.Add(name.Substring(0, name.Length - SidecarSuffixes[s].Length));
                                sidecarIds++;
                                break;
                            }
                        }
                    }

                    if (full.EndsWith(".sfs", StringComparison.Ordinal))
                    {
                        saveFiles++;
                        foreach (string line in File.ReadLines(full))
                        {
                            Match m = IdKeyLine.Match(line);
                            if (m.Success)
                                ids.Add(m.Groups[1].Value);
                        }
                    }
                }
            }

            var refused = new List<string>();
            foreach (string id in ids)
            {
                if (SafeFileId.Check(id) != SafeFileIdVerdict.Ok)
                    refused.Add(id);
            }

            Assert.True(saveFiles >= 100, "too few committed saves scanned: " + saveFiles);
            Assert.True(sidecarIds >= 300, "too few committed sidecars scanned: " + sidecarIds);
            Assert.True(ids.Count >= 400, "too few committed ids scanned: " + ids.Count);
            Assert.True(refused.Count == 0, "committed ids refused: " + string.Join(", ", refused));
        }
    }
}
