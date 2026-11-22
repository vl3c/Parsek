using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The ghost builder's animation samplers instantiate a part prefab's model, colliders
    /// included, to sample its animation. A deferred <c>Object.Destroy</c> leaves that clone
    /// alive into the next physics step, where its colliders push any real vessel near the
    /// clone (the part-parity sweep's host lost two joints and its orbit to a Clydesdale
    /// heat-animation clone). Every temporary clone must be released with
    /// <c>Object.DestroyImmediate</c> in the same call that made it.
    /// </summary>
    public class GhostTempCloneDestroyGateTests
    {
        private static string GhostDir()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                string candidate = Path.Combine(dir, "Source", "Parsek", "Ghost");
                if (Directory.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            throw new DirectoryNotFoundException("Source/Parsek/Ghost not found above " +
                AppDomain.CurrentDomain.BaseDirectory);
        }

        private static string StripLineComments(string text)
            => string.Join("\n", text.Split('\n').Select(l =>
            {
                int i = l.IndexOf("//", StringComparison.Ordinal);
                return i >= 0 ? l.Substring(0, i) : l;
            }));

        [Fact]
        public void EveryTempCloneIsDestroyedImmediately()
        {
            int made = 0, freed = 0;
            foreach (string path in Directory.GetFiles(GhostDir(), "*.cs", SearchOption.AllDirectories))
            {
                string code = StripLineComments(File.ReadAllText(path));
                string name = Path.GetFileName(path);
                Assert.False(Regex.IsMatch(code, @"Object\.Destroy\s*\(\s*tempClone\s*\)"),
                    name + " frees a temporary prefab clone with a deferred Object.Destroy");
                made += Regex.Matches(code, @"tempClone\s*=\s*Object\.Instantiate\s*\(").Count;
                freed += Regex.Matches(code, @"Object\.DestroyImmediate\s*\(\s*tempClone\s*\)").Count;
            }
            Assert.True(made >= 4, "expected the four animation samplers, found " + made);
            Assert.Equal(made, freed);
        }
    }
}
