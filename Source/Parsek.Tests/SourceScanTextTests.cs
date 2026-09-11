using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The source-scan text preparation is itself load-bearing: every cell that asserts a
    /// product call site exists now trusts it to tell code from commentary. These cells pin
    /// the two properties the callers rely on - comments go, literals stay, and indices do
    /// not move.
    /// </summary>
    public class SourceScanTextTests
    {
        // catches: the strip eating a string literal, which would make the scans that match
        // on a literal (a log line, a tooltip constant) red on correct source.
        [Fact]
        public void StripCSharpComments_RemovesComments_AndKeepsStringLiterals()
        {
            string src =
                "var a = \"http://example/*x*/\"; // gone\n"
                + "/* also\n   gone */ var b = @\"C:\\p // not a comment\";\n"
                + "/// <summary>gone</summary>\n"
                + "var c = '\\'';\n";

            string stripped = SourceScanText.StripCSharpComments(src);

            Assert.DoesNotContain("gone", stripped);
            Assert.Contains("http://example/*x*/", stripped);
            Assert.Contains("C:\\p // not a comment", stripped);
            Assert.Contains("var c = '\\'';", stripped);
        }

        // catches: the strip changing offsets, which every caller depends on - they slice the
        // PREPARED text with indices and then report them against the real file.
        [Fact]
        public void StripCSharpComments_PreservesLengthAndLineCount()
        {
            string src = "a(); // one\n/* two\nthree */ b();\nc(); /// four\n";

            string stripped = SourceScanText.StripCSharpComments(src);

            Assert.Equal(src.Length, stripped.Length);
            Assert.Equal(src.Split('\n').Length, stripped.Split('\n').Length);
            Assert.Equal(src.IndexOf("b();"), stripped.IndexOf("b();"));
        }

        // catches: brace counting tripping over a literal brace or an interpolation hole,
        // which would pick the wrong enclosing method for a draw site.
        [Fact]
        public void MaskStringLiteralContents_HidesBracesInsideLiterals()
        {
            string src = "Log($\"hidden={x} }}\"); { real }";

            string masked = SourceScanText.MaskStringLiteralContents(src);

            Assert.Equal(src.Length, masked.Length);
            Assert.Equal(1, CountOf(masked, '{'));
            Assert.Equal(1, CountOf(masked, '}'));
        }

        [Fact]
        public void EnclosingMethodBodyStart_FindsTheMethodBlockNotTheInnerBlock()
        {
            string src =
                "namespace N\n{\n    class C\n    {\n"
                + "        [Attr]\n        public static void M()\n        {\n"
                + "            if (x)\n            {\n                NEEDLE;\n            }\n"
                + "        }\n    }\n}\n";
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(src);

            int start = SourceScanText.EnclosingMethodBodyStart(prepared, prepared.IndexOf("NEEDLE"));

            Assert.True(start > 0);
            string span = prepared.Substring(start, prepared.IndexOf("NEEDLE") - start);
            Assert.Contains("if (x)", span);
            // The method's own brace, not the if-block's: the signature line is just above it.
            Assert.Contains("public static void M()", src.Substring(0, start));
        }

        // catches: the if-condition extractor cutting a condition short at a nested call's
        // closing paren, which would hide a gate read inside an argument list.
        [Fact]
        public void IfConditions_ReturnsBalancedConditionsOnly()
        {
            string src = "bool g = Gate(mode); if (a && Gate(mode)) { } while (Gate(mode)) { }";

            var conditions = SourceScanText.IfConditions(src);

            Assert.Single(conditions);
            Assert.Equal("a && Gate(mode)", conditions[0]);
        }

        private static int CountOf(string s, char c)
        {
            int n = 0;
            foreach (char ch in s) if (ch == c) n++;
            return n;
        }
    }
}
