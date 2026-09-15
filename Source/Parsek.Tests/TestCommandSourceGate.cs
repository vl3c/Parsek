using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Shared source reader for the command-seam payload gates.
    ///
    /// <para>Two payload cells assert culture invariance over fields that are all integers,
    /// and integer default formatting is identical in every .NET culture - so the culture
    /// swap they run exercises nothing and they would pass against a CurrentCulture site.
    /// The only place the invariance can be pinned is the format provider the builder
    /// passes, which has no headless seam of its own. This reads the production file with
    /// comments blanked out (via <see cref="SourceScanText.StripCSharpComments"/>) and
    /// returns the brace-matched body of <c>BuildPayload</c>, so what the cell matches is
    /// CODE and the window is the method, not a fixed character count.</para>
    /// </summary>
    internal static class TestCommandSourceGate
    {
        internal static string BuildPayloadBody(string fileName)
        {
            string source = SourceScanText.StripCSharpComments(ReadTestCommandSource(fileName));

            int signature = source.IndexOf("internal static List<KeyValuePair<string, string>> BuildPayload",
                StringComparison.Ordinal);
            Assert.True(signature >= 0, "BuildPayload signature not found in " + fileName);

            int open = source.IndexOf('{', signature);
            Assert.True(open >= 0, "BuildPayload body not found in " + fileName);

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return source.Substring(open, i - open + 1);
                }
            }

            Assert.True(false, "BuildPayload body is unbalanced in " + fileName);
            return string.Empty;
        }

        private static string ReadTestCommandSource(string fileName)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(root, "Source", "Parsek", "TestCommands", fileName);
            if (!File.Exists(path))
                path = Path.Combine(root, "Parsek", "TestCommands", fileName);
            Assert.True(File.Exists(path), "Source file not found at " + path);
            return File.ReadAllText(path);
        }
    }
}
