using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The part parity sweep parents the ghost under the real part (design 4.2 step 3). A
    /// ReStock depth-mask module's <c>onVariantApplied</c> / <c>onPartRepaired</c> handler
    /// re-runs <c>UpdateAllMaterials</c> over every renderer under the part, so if either event
    /// fired for a real case part during capture it would requeue the GHOST's renderers too.
    /// A module repaired for a dead transform never subscribed (its <c>OnStart</c> threw first), the repair
    /// subscribes nothing; this gate keeps the other half true: no file on the capture path
    /// (the sweep, its co-orbital rig, the ghost builder) names either event or applies a
    /// variant to a part. Line and block comments are stripped, so a comment naming them is fine.
    /// </summary>
    public class PartParityDepthMaskEventGateTests
    {
        private static readonly string[] Forbidden =
        {
            "onVariantApplied", "onPartRepaired", "SetVariant(", "ApplyVariant(",
        };

        private static string SourceDir()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                string candidate = Path.Combine(dir, "Source", "Parsek");
                if (Directory.Exists(Path.Combine(candidate, "TestCommands"))) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            throw new DirectoryNotFoundException("Source/Parsek not found above " +
                AppDomain.CurrentDomain.BaseDirectory);
        }

        internal static string StripComments(string text)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
                {
                    int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + 2;
                    continue;
                }
                if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/')
                {
                    int end = text.IndexOf('\n', i);
                    i = end < 0 ? text.Length : end;
                    continue;
                }
                sb.Append(text[i]);
                i++;
            }
            return sb.ToString();
        }

        private static List<string> CapturePathFiles()
        {
            string src = SourceDir();
            var files = new List<string>();
            string tc = Path.Combine(src, "TestCommands");
            files.AddRange(Directory.GetFiles(tc, "PartParity*.cs"));
            files.AddRange(Directory.GetFiles(tc, "TestCommandPartParitySweep*.cs"));
            files.AddRange(Directory.GetFiles(tc, "ParsekTestCommandAddon.PartParitySweep.cs"));
            files.Add(Path.Combine(src, "InGameTests", "GhostCoOrbitalRig.cs"));
            files.Add(Path.Combine(src, "InGameTests", "PartParityInGameTests.cs"));
            files.AddRange(Directory.GetFiles(Path.Combine(src, "Ghost"), "*.cs", SearchOption.AllDirectories));
            return files;
        }

        [Fact]
        public void NoCapturePathFileFiresOrAppliesAVariantOrRepairToAPart()
        {
            List<string> files = CapturePathFiles();
            Assert.True(files.Count >= 20, "expected the sweep and ghost builder files, found " + files.Count);
            Assert.Contains(files, f => Path.GetFileName(f) == "PartParityDepthMaskRepair.cs");
            Assert.Contains(files, f => Path.GetFileName(f) == "PartParityCaseCapture.cs");
            var hits = new List<string>();
            foreach (string path in files)
            {
                Assert.True(File.Exists(path), "capture-path file missing: " + path);
                string code = StripComments(File.ReadAllText(path));
                foreach (string token in Forbidden)
                    if (code.IndexOf(token, StringComparison.Ordinal) >= 0)
                        hits.Add(Path.GetFileName(path) + ": " + token);
            }
            Assert.True(hits.Count == 0, "capture path names a variant / repair event: " + string.Join(", ", hits));
        }

        [Fact]
        public void StripComments_DropsLineAndBlockComments_KeepsCode()
        {
            string code = "a(); // onVariantApplied\n/* onPartRepaired */ b();\n/// SetVariant(\nc();";
            string stripped = StripComments(code);
            Assert.DoesNotContain("onVariantApplied", stripped);
            Assert.DoesNotContain("onPartRepaired", stripped);
            Assert.DoesNotContain("SetVariant(", stripped);
            Assert.Contains("a();", stripped);
            Assert.Contains("b();", stripped);
            Assert.Contains("c();", stripped);
        }
    }
}
