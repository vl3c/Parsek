using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The two gates that keep the frozen format goldens honest (1.0 save-format freeze, owner
    /// ruling 2026-11-04). Coverage: every version from each <see cref="SaveFormat.All"/> row's
    /// baseline through its current value is carried by some <c>FormatGolden/vN/formats.txt</c>,
    /// so a bumped constant reds until a new golden folder with the new value is committed.
    /// Frozen bytes: every file under <c>FormatGolden/</c> matches <c>checksums.txt</c>, so an edit
    /// to a released golden reds instead of quietly rewriting history.
    /// </summary>
    [Collection("Sequential")]
    public class SaveFormatGoldenGateTests : IDisposable
    {
        private readonly List<string> tempDirs = new List<string>();

        public SaveFormatGoldenGateTests()
        {
            SaveFormat.ResetForTesting();
        }

        public void Dispose()
        {
            SaveFormat.ResetForTesting();
            foreach (string dir in tempDirs)
            {
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        // ---------------------------------------------------------------- pure checks

        /// <summary>
        /// Every format version from baseline to current that no manifest carries, every manifest
        /// row naming no registered format, and every manifest value above that format's current.
        /// </summary>
        internal static List<string> FindCoverageProblems(
            IReadOnlyList<SaveFormatInfo> formats, IReadOnlyList<KeyValuePair<string, Dictionary<string, int>>> manifests)
        {
            var problems = new List<string>();
            var ic = CultureInfo.InvariantCulture;
            foreach (SaveFormatInfo f in formats)
            {
                for (int v = f.Baseline; v <= f.Current; v++)
                {
                    bool covered = manifests.Any(m => m.Value.TryGetValue(f.Name, out int got) && got == v);
                    if (!covered)
                        problems.Add($"format '{f.Name}' version {v.ToString(ic)} (baseline {f.Baseline.ToString(ic)}, "
                            + $"current {f.Current.ToString(ic)}) is carried by no golden folder: write a new "
                            + $"Fixtures/FormatGolden/vN whose {FormatGolden.ManifestFileName} says {f.Name}={v.ToString(ic)}");
                }
            }
            foreach (var m in manifests)
            {
                foreach (var row in m.Value)
                {
                    SaveFormatInfo f = formats.FirstOrDefault(x => x.Name == row.Key);
                    if (f == null)
                        problems.Add($"{m.Key}/{FormatGolden.ManifestFileName} names '{row.Key}', which is no "
                            + "SaveFormat.All row (a renamed or removed format)");
                    else if (row.Value > f.Current)
                        problems.Add($"{m.Key}/{FormatGolden.ManifestFileName} says {row.Key}={row.Value.ToString(ic)}, "
                            + $"above the current {f.Current.ToString(ic)}");
                }
            }
            return problems;
        }

        /// <summary>Files under <paramref name="root"/> that differ from, are missing from, or are absent in its checksum list.</summary>
        internal static List<string> FindFrozenFileProblems(string root)
        {
            var problems = new List<string>();
            string listPath = Path.Combine(root, FormatGolden.ChecksumsFileName);
            if (!File.Exists(listPath))
            {
                problems.Add("no " + FormatGolden.ChecksumsFileName + " under " + root);
                return problems;
            }
            Dictionary<string, string> expected = FormatGolden.ReadChecksums(listPath);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in FormatGolden.FrozenFiles(root))
            {
                string key = FormatGolden.RelativeKey(root, path);
                seen.Add(key);
                if (!expected.TryGetValue(key, out string hash))
                    problems.Add("added: " + key);
                else if (!string.Equals(hash, FormatGolden.Sha256(path), StringComparison.Ordinal))
                    problems.Add("modified: " + key);
            }
            foreach (string key in expected.Keys)
            {
                if (!seen.Contains(key))
                    problems.Add("removed: " + key);
            }
            problems.Sort(StringComparer.Ordinal);
            return problems;
        }

        private static List<KeyValuePair<string, Dictionary<string, int>>> CommittedManifests()
        {
            var list = new List<KeyValuePair<string, Dictionary<string, int>>>();
            foreach (var dir in FormatGolden.VersionDirs())
            {
                string manifest = Path.Combine(dir.Value, FormatGolden.ManifestFileName);
                if (File.Exists(manifest))
                    list.Add(new KeyValuePair<string, Dictionary<string, int>>(
                        "v" + dir.Key.ToString(CultureInfo.InvariantCulture), FormatGolden.ReadManifest(manifest)));
            }
            return list;
        }

        // ---------------------------------------------------------------- the gates

        // catches: a SaveFormat Current bumped (or a new SaveFormat row) without a golden folder
        // carrying the new version, so no committed save in the new format exists to load forever.
        [Fact]
        public void EveryFormatVersion_BaselineThroughCurrent_IsCarriedByAGoldenFolder()
        {
            List<KeyValuePair<string, Dictionary<string, int>>> manifests = CommittedManifests();
            Assert.NotEmpty(manifests);
            List<string> problems = FindCoverageProblems(SaveFormat.All, manifests);
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        // catches: a golden folder without its manifest or its save (a half-written vN).
        [Fact]
        public void EveryGoldenFolder_HasAManifestAndASave_AndV1Exists()
        {
            var dirs = FormatGolden.VersionDirs();
            Assert.Contains(dirs, d => d.Key == 1);
            foreach (var dir in dirs)
            {
                Assert.True(File.Exists(Path.Combine(dir.Value, FormatGolden.ManifestFileName)),
                    "v" + dir.Key + " has no " + FormatGolden.ManifestFileName);
                Assert.True(File.Exists(Path.Combine(dir.Value, "persistent.sfs")),
                    "v" + dir.Key + " has no persistent.sfs");
            }
        }

        // catches: a manual edit, re-harvest, regeneration, deletion or addition inside a released
        // golden folder. A released save must keep loading as it was written.
        [Fact]
        public void GoldenFiles_MatchTheirCommittedChecksums()
        {
            List<string> problems = FindFrozenFileProblems(FormatGolden.Root());
            Assert.True(problems.Count == 0,
                "golden saves are frozen; add a new vN folder instead (Fixtures/FormatGolden/README.md):\n"
                + string.Join("\n", problems));
        }

        // ---------------------------------------------------------------- the gates catch what they claim

        [Fact]
        public void CoverageGate_ACurrentBumpWithoutANewGolden_IsNamed()
        {
            SaveFormat.SetCurrentForTesting(SaveFormat.Ledger, SaveFormat.Ledger.Current + 1);
            List<string> problems = FindCoverageProblems(SaveFormat.All, CommittedManifests());
            string bumped = (SaveFormat.Ledger.Current).ToString(CultureInfo.InvariantCulture);
            string problem = Assert.Single(problems);
            Assert.Contains("format 'ledger' version " + bumped, problem);
            Assert.Contains("ledger=" + bumped, problem);

            // A new golden folder whose manifest carries the bumped value closes it.
            var withV2 = CommittedManifests();
            withV2.Add(new KeyValuePair<string, Dictionary<string, int>>("v2",
                new Dictionary<string, int>(StringComparer.Ordinal) { ["ledger"] = SaveFormat.Ledger.Current }));
            Assert.Empty(FindCoverageProblems(SaveFormat.All, withV2));
        }

        [Fact]
        public void CoverageGate_AnUnknownOrFutureManifestRow_IsNamed()
        {
            var manifests = new List<KeyValuePair<string, Dictionary<string, int>>>(CommittedManifests())
            {
                new KeyValuePair<string, Dictionary<string, int>>("v9", new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["no-such-format"] = 1,
                    ["milestones"] = SaveFormat.Milestones.Current + 5,
                }),
            };
            List<string> problems = FindCoverageProblems(SaveFormat.All, manifests);
            Assert.Contains(problems, p => p.Contains("'no-such-format', which is no SaveFormat.All row"));
            Assert.Contains(problems, p => p.Contains("v9/formats.txt says milestones="));
        }

        [Fact]
        public void FrozenGate_AnEditARemovalAndAnAddition_AreEachNamed()
        {
            string copy = Path.Combine(Path.GetTempPath(), "parsek-golden-gate-" + Guid.NewGuid().ToString("N"));
            tempDirs.Add(copy);
            FormatGolden.CopyTree(FormatGolden.Root(), copy);
            Assert.Empty(FindFrozenFileProblems(copy));

            string sfs = Path.Combine(copy, "v1", "persistent.sfs");
            File.AppendAllText(sfs, "\n");
            File.Delete(Path.Combine(copy, "v1", FormatGolden.ManifestFileName));
            File.WriteAllText(Path.Combine(copy, "v1", "extra.txt"), "x");

            List<string> problems = FindFrozenFileProblems(copy);
            Assert.Equal(new[] { "added: v1/extra.txt", "modified: v1/persistent.sfs", "removed: v1/formats.txt" },
                problems);
        }
    }
}
