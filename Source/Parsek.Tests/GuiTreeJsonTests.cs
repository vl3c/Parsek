using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit coverage for the hand-rolled GUI-tree JSON writer: escaping, optional-key
    /// omission, nesting, and - the one that would otherwise ship broken - culture
    /// invariance. The xUnit host runs under the OS culture (ro-RO on this machine), so a
    /// site that formatted a rect with the ambient culture would emit <c>[10,5, 0]</c> and
    /// produce a file no parser accepts. The de-DE cells PROVE the writer is invariant;
    /// they are not there to make a culture-dependent writer pass.
    /// </summary>
    public class GuiTreeJsonTests
    {
        private static GuiTreeCaptureHeader Header()
        {
            return new GuiTreeCaptureHeader
            {
                Label = "probe",
                CapturedUtc = "2026-09-10T10:11:12Z",
                Frame = 4242,
                ScreenWidth = 1920,
                ScreenHeight = 1080,
                ScreenshotHint = "probe.png",
            };
        }

        private static GuiTreeResult TreeWithOneWindow()
        {
            var events = new List<GuiTreeEvent>
            {
                new GuiTreeEvent
                {
                    Op = GuiTreeOp.Begin,
                    Kind = GuiNodeKind.Window,
                    Rect = new GuiRect(10.5f, 20.25f, 300f, 400f),
                    LocalRect = new GuiRect(0f, 0f, 300f, 400f),
                    ClipDepth = 1,
                    Text = "Parsek",
                    StyleName = "window",
                    WindowId = 90210,
                    ContentOriginX = 10.5f,
                    ContentOriginY = 40.5f,
                    ArgWidth = 300f,
                    ArgHeight = 400f,
                },
                new GuiTreeEvent
                {
                    Op = GuiTreeOp.Leaf,
                    Kind = GuiNodeKind.Label,
                    Rect = new GuiRect(14f, 44.75f, 120f, 18f),
                    LocalRect = new GuiRect(4f, 4.25f, 120f, 18f),
                    ClipDepth = 1,
                    Text = "Recordings",
                    Tooltip = "How many flights are stored",
                    StyleName = "label",
                },
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.Window },
            };
            return GuiTreeAssembler.Assemble(events);
        }

        [Fact]
        public void HeaderAndCountsAreWritten()
        {
            string json = GuiTreeJson.Write(Header(), TreeWithOneWindow());

            Assert.Contains("\"schema\": \"parsek-gui-tree/1\"", json);
            Assert.Contains("\"label\": \"probe\"", json);
            Assert.Contains("\"frame\": 4242", json);
            Assert.Contains("\"screen\": {\"width\": 1920, \"height\": 1080}", json);
            Assert.Contains("\"screenshotHint\": \"probe.png\"", json);
            Assert.Contains("\"windows\": 1", json);
            Assert.Contains("\"nodes\": 2", json);
            Assert.Contains("\"strayEnds\": 0", json);
            Assert.Contains("\"unclosedAtEnd\": 0", json);
        }

        [Fact]
        public void NodesCarryRectsStyleTextAndNesting()
        {
            string json = GuiTreeJson.Write(Header(), TreeWithOneWindow());

            Assert.Contains("\"kind\": \"window\"", json);
            Assert.Contains("\"rect\": [10.5, 20.25, 300, 400]", json);
            Assert.Contains("\"localRect\": [0, 0, 300, 400]", json);
            Assert.Contains("\"windowId\": 90210", json);
            Assert.Contains("\"contentOrigin\": [10.5, 40.5]", json);
            Assert.Contains("\"argSize\": [300, 400]", json);
            Assert.Contains("\"kind\": \"label\"", json);
            Assert.Contains("\"tooltip\": \"How many flights are stored\"", json);
            Assert.Contains("\"style\": \"label\"", json);
            // The label is nested inside the window, so its object appears after the
            // window's "children" key rather than in "roots".
            int children = json.IndexOf("\"children\": [", StringComparison.Ordinal);
            int label = json.IndexOf("\"kind\": \"label\"", StringComparison.Ordinal);
            Assert.True(children > 0 && label > children);
        }

        [Fact]
        public void OptionalKeysAreOmittedWhenAbsent()
        {
            var events = new List<GuiTreeEvent>
            {
                new GuiTreeEvent
                {
                    Op = GuiTreeOp.Leaf,
                    Kind = GuiNodeKind.Label,
                    Rect = new GuiRect(0f, 0f, 10f, 10f),
                    StyleName = "label",
                },
            };
            string json = GuiTreeJson.Write(Header(), GuiTreeAssembler.Assemble(events));

            Assert.DoesNotContain("\"tooltip\"", json);
            Assert.DoesNotContain("\"windowId\"", json);
            Assert.DoesNotContain("\"value\"", json);
            Assert.DoesNotContain("\"horizontal\"", json);
            Assert.DoesNotContain("\"contentOrigin\"", json);
            // Never omitted: an absent text is meaningful (an icon-only control).
            Assert.Contains("\"text\": null", json);
        }

        [Fact]
        public void TogglesAndTextFieldsCarryTheirValues()
        {
            var events = new List<GuiTreeEvent>
            {
                new GuiTreeEvent
                {
                    Op = GuiTreeOp.Leaf, Kind = GuiNodeKind.Toggle,
                    Rect = new GuiRect(0f, 0f, 10f, 10f),
                    Text = "Show ghosts", ToggleValue = true, Enabled = false,
                },
                new GuiTreeEvent
                {
                    Op = GuiTreeOp.Leaf, Kind = GuiNodeKind.TextField,
                    Rect = new GuiRect(0f, 20f, 100f, 20f),
                    TextValue = "Munar Lander", ControlId = 17,
                },
            };
            string json = GuiTreeJson.Write(Header(), GuiTreeAssembler.Assemble(events));

            Assert.Contains("\"value\": true", json);
            Assert.Contains("\"enabled\": false", json);
            Assert.Contains("\"textValue\": \"Munar Lander\"", json);
            Assert.Contains("\"controlId\": 17", json);
        }

        [Fact]
        public void FunnelReportIsWrittenWithPatchedAndHitCounts()
        {
            GuiTreeCaptureHeader header = Header();
            header.Funnels.Add(new GuiTreeFunnelReport("GUI.DoLabel", true, 12));
            header.Funnels.Add(new GuiTreeFunnelReport("GUI.EndGroup", false, 0));

            string json = GuiTreeJson.Write(header, TreeWithOneWindow());

            Assert.Contains("{\"name\": \"GUI.DoLabel\", \"patched\": true, \"hits\": 12}", json);
            Assert.Contains("{\"name\": \"GUI.EndGroup\", \"patched\": false, \"hits\": 0}", json);
        }

        [Fact]
        public void EmptyFunnelListWritesAnEmptyArray()
        {
            string json = GuiTreeJson.Write(Header(), TreeWithOneWindow());
            Assert.Contains("\"funnels\": [],", json);
        }

        [Fact]
        public void NullHeaderAndNullTreeStillProduceAWellFormedDocument()
        {
            string json = GuiTreeJson.Write(null, null);
            Assert.Contains("\"schema\": \"parsek-gui-tree/1\"", json);
            Assert.Contains("\"label\": null", json);
            Assert.Contains("\"roots\": []", json);
        }

        // ------------------------------------------------------------------ escaping

        [Theory]
        [InlineData("plain", "\"plain\"")]
        [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
        [InlineData("C:\\KSP", "\"C:\\\\KSP\"")]
        [InlineData("a\nb", "\"a\\nb\"")]
        [InlineData("a\rb", "\"a\\rb\"")]
        [InlineData("a\tb", "\"a\\tb\"")]
        [InlineData("a\bb", "\"a\\bb\"")]
        [InlineData("a\fb", "\"a\\fb\"")]
        [InlineData("a\u0001b", "\"a\\u0001b\"")]
        [InlineData("a\u007fb", "\"a\\u007fb\"")]
        [InlineData("Kerbin \u00b0C", "\"Kerbin \u00b0C\"")]
        [InlineData(null, "null")]
        public void StringsAreEscapedForJson(string input, string expected)
        {
            Assert.Equal(expected, GuiTreeJson.Str(input));
        }

        [Fact]
        public void ControlCharactersInControlTextCannotBreakTheDocument()
        {
            var events = new List<GuiTreeEvent>
            {
                new GuiTreeEvent
                {
                    Op = GuiTreeOp.Leaf, Kind = GuiNodeKind.Button,
                    Rect = new GuiRect(0f, 0f, 10f, 10f),
                    Text = "Fly \"Munar\" \\ probe\u0007\nrow2",
                    Tooltip = "tab\there",
                },
            };
            string json = GuiTreeJson.Write(Header(), GuiTreeAssembler.Assemble(events));

            Assert.Contains("\\\"Munar\\\"", json);
            Assert.Contains("\\\\ probe", json);
            Assert.Contains("\\u0007", json);
            Assert.Contains("\\nrow2", json);
            Assert.Contains("tab\\there", json);
            // No raw newline may survive inside a JSON string literal.
            Assert.DoesNotContain("probe\u0007", json);
        }

        // ----------------------------------------------------------------- numbers

        [Fact]
        public void NonFiniteFloatsBecomeJsonNull()
        {
            Assert.Equal("null", GuiTreeJson.Num(float.NaN));
            Assert.Equal("null", GuiTreeJson.Num(float.PositiveInfinity));
            Assert.Equal("null", GuiTreeJson.Num(float.NegativeInfinity));
        }

        [Fact]
        public void FloatsAreWrittenWithADotUnderTheAmbientCulture()
        {
            Assert.Equal("10.5", GuiTreeJson.Num(10.5f));
            Assert.Equal("300", GuiTreeJson.Num(300f));
            Assert.Equal("-0.25", GuiTreeJson.Num(-0.25f));
        }

        [Fact]
        public void FloatsStayDotSeparatedUnderDeDE()
        {
            CultureInfo previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal("10.5", GuiTreeJson.Num(10.5f));
                Assert.Equal("-0.25", GuiTreeJson.Num(-0.25f));
                Assert.Equal("42", GuiTreeJson.Num(42));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [Fact]
        public void WholeDocumentStaysDotSeparatedUnderDeDE()
        {
            CultureInfo previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string json = GuiTreeJson.Write(Header(), TreeWithOneWindow());
                Assert.Contains("\"rect\": [10.5, 20.25, 300, 400]", json);
                Assert.Contains("\"contentOrigin\": [10.5, 40.5]", json);
                Assert.DoesNotContain("10,5", json);
                Assert.DoesNotContain("20,25", json);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        // ------------------------------------------------------------------------
        // Cells below cover the two header surfaces added after the first review, and
        // the escaping failure that would lose a whole dump for one character.
        // ------------------------------------------------------------------------

        [Fact]
        public void ALoneSurrogateIsEscapedRatherThanWrittenRaw()
        {
            // A UTF-16 string with an unpaired surrogate - trivially produced by
            // truncating a label that contains an emoji - cannot be encoded as UTF-8 at
            // all, so File.WriteAllText mangles or refuses it and ONE bad character costs
            // the ENTIRE dump. Escaping the whole surrogate range side-steps it.
            string lone = "row " + (char)0xD83D + " end";
            string json = GuiTreeJson.Str(lone);

            Assert.Equal("\"row \\ud83d end\"", json);
            Assert.DoesNotContain(((char)0xD83D).ToString(), json);
            // And the escaped form survives a strict UTF-8 round trip, which the raw
            // string does not (the encoder throws on it).
            var strict = new System.Text.UTF8Encoding(false, true);
            Assert.Equal(json, strict.GetString(strict.GetBytes(json)));
            Assert.Throws<System.Text.EncoderFallbackException>(() => strict.GetBytes(lone));
        }

        [Fact]
        public void AWellFormedSurrogatePairSurvivesAsTwoEscapes()
        {
            // The pair is escaped too, and that is not a loss: two \uXXXX escapes are
            // exactly how JSON encodes an astral character, and a parser rebuilds it.
            string rocket = char.ConvertFromUtf32(0x1F680);   // U+1F680 ROCKET
            Assert.Equal("\"\\ud83d\\ude80\"", GuiTreeJson.Str(rocket));
            Assert.Equal(rocket, MiniJson.ParseValue("\"\\ud83d\\ude80\""));
        }

        [Fact]
        public void TheGuiMatrixIsReportedSoAScaledCaptureCanBeUndone()
        {
            // GUIUtility.GUIToScreenRect converts a rect's ORIGIN only, so under a
            // scaling GUI.matrix the recorder multiplies w/h through by hand. A reader
            // has to be able to tell that happened, and by how much.
            GuiTreeCaptureHeader header = Header();
            string identity = GuiTreeJson.Write(header, TreeWithOneWindow());
            Assert.Contains("\"guiMatrix\": {\"identity\": true, \"m00\": 1, \"m11\": 1, "
                + "\"m03\": 0, \"m13\": 0}", identity);

            header.MatrixIsIdentity = false;
            header.MatrixM00 = 1.5f;
            header.MatrixM11 = 1.5f;
            header.MatrixM03 = 12.25f;
            header.MatrixM13 = -4f;
            string scaled = GuiTreeJson.Write(header, TreeWithOneWindow());
            Assert.Contains("\"guiMatrix\": {\"identity\": false, \"m00\": 1.5, \"m11\": 1.5, "
                + "\"m03\": 12.25, \"m13\": -4}", scaled);
        }

        [Fact]
        public void TheInertRectRuleCounterReachesTheCounts()
        {
            var tree = new GuiTreeResult();
            tree.RectRuleInert = 3;
            Assert.Contains("\"rectRuleInert\": 3", GuiTreeJson.Write(Header(), tree));
        }

        [Fact]
        public void ARichDocumentSurvivesARealJsonParse()
        {
            // Every other cell here does substring matching, which cannot see a missing
            // comma, an unclosed brace or a duplicated key - and the writer is
            // hand-rolled, so those are exactly its available failure modes. This one
            // parses the output with a strict recursive-descent parser and walks the
            // STRUCTURE that comes back.
            GuiTreeCaptureHeader header = Header();
            header.MatrixIsIdentity = false;
            header.MatrixM00 = 2f;
            header.Funnels.Add(new GuiTreeFunnelReport("GUI.DoLabel", true, 5));
            header.Funnels.Add(new GuiTreeFunnelReport("GUI.EndGroup", true, 0));
            header.RecordFaults = 1;
            header.DroppedOverCap = 2;

            var events = new List<GuiTreeEvent>
            {
                new GuiTreeEvent
                {
                    Op = GuiTreeOp.Begin,
                    Kind = GuiNodeKind.Window,
                    Rect = new GuiRect(10.5f, 20.25f, 300f, 400f),
                    LocalRect = new GuiRect(0f, 0f, 300f, 400f),
                    ClipDepth = 1,
                    Text = "Parsek \"quoted\" \\ back\\slash\ttab",
                    StyleName = "window",
                    WindowId = 90210,
                    ContentOriginX = 10.5f,
                    ContentOriginY = 40.5f,
                    ArgWidth = 300f,
                    ArgHeight = 400f,
                },
                new GuiTreeEvent
                {
                    Op = GuiTreeOp.Begin,
                    Kind = GuiNodeKind.LayoutGroup,
                    Rect = new GuiRect(14f, 44f, 280f, 300f),
                    ClipDepth = 1,
                    Horizontal = true,
                },
                new GuiTreeEvent
                {
                    Op = GuiTreeOp.Leaf,
                    Kind = GuiNodeKind.Toggle,
                    Rect = new GuiRect(16f, 46.75f, 100f, 20f),
                    ClipDepth = 1,
                    Text = null,
                    ToggleValue = true,
                    ControlId = 77,
                    Enabled = false,
                },
                new GuiTreeEvent
                {
                    Op = GuiTreeOp.Leaf,
                    Kind = GuiNodeKind.TextField,
                    Rect = new GuiRect(16f, 70f, 100f, 20f),
                    ClipDepth = 1,
                    TextValue = "line1\nline2",
                },
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.LayoutGroup },
                new GuiTreeEvent { Op = GuiTreeOp.End, Kind = GuiNodeKind.Window },
            };

            string json = GuiTreeJson.Write(header, GuiTreeAssembler.Assemble(events));
            var doc = (Dictionary<string, object>)MiniJson.ParseValue(json);

            Assert.Equal("parsek-gui-tree/1", doc["schema"]);
            Assert.Equal(4242.0, doc["frame"]);
            var screen = (Dictionary<string, object>)doc["screen"];
            Assert.Equal(1920.0, screen["width"]);
            var matrix = (Dictionary<string, object>)doc["guiMatrix"];
            Assert.Equal(false, matrix["identity"]);
            Assert.Equal(2.0, matrix["m00"]);
            var counts = (Dictionary<string, object>)doc["counts"];
            Assert.Equal(1.0, counts["windows"]);
            Assert.Equal(4.0, counts["nodes"]);
            Assert.Equal(1.0, counts["recordFaults"]);
            Assert.Equal(2.0, counts["droppedOverCap"]);
            Assert.Equal(0.0, counts["rectRuleInert"]);

            var funnels = (List<object>)doc["funnels"];
            Assert.Equal(2, funnels.Count);
            Assert.Equal("GUI.DoLabel", ((Dictionary<string, object>)funnels[0])["name"]);
            Assert.Equal(0.0, ((Dictionary<string, object>)funnels[1])["hits"]);

            var roots = (List<object>)doc["roots"];
            var window = (Dictionary<string, object>)Assert.Single(roots);
            Assert.Equal("window", window["kind"]);
            // The escapes come back as the original characters, which substring matching
            // over the raw text can never show.
            Assert.Equal("Parsek \"quoted\" \\ back\\slash\ttab", window["text"]);
            var rect = (List<object>)window["rect"];
            Assert.Equal(10.5, rect[0]);
            Assert.Equal(20.25, rect[1]);

            var group = (Dictionary<string, object>)((List<object>)window["children"])[0];
            Assert.Equal("layoutgroup", group["kind"]);
            Assert.Equal(true, group["horizontal"]);

            var kids = (List<object>)group["children"];
            Assert.Equal(2, kids.Count);
            var toggle = (Dictionary<string, object>)kids[0];
            Assert.Equal("toggle", toggle["kind"]);
            Assert.Equal(true, toggle["value"]);
            Assert.Equal(false, toggle["enabled"]);
            Assert.Null(toggle["text"]);
            Assert.False(toggle.ContainsKey("tooltip"));
            var field = (Dictionary<string, object>)kids[1];
            Assert.Equal("line1\nline2", field["textValue"]);
        }
    }

    /// <summary>
    /// A strict, minimal JSON parser, here only so
    /// <c>GuiTreeJsonTests.ARichDocumentSurvivesARealJsonParse</c> can check the
    /// hand-rolled writer's output as a DOCUMENT rather than as a bag of substrings.
    /// Deliberately unforgiving: it throws on trailing commas, unterminated strings,
    /// duplicate keys, raw control characters and trailing junk, because every one of
    /// those is a way a StringBuilder-based writer breaks.
    /// </summary>
    internal static class MiniJson
    {
        internal static object ParseValue(string text)
        {
            int i = 0;
            object value = ParseValue(text, ref i);
            SkipWhitespace(text, ref i);
            if (i != text.Length)
                throw new FormatException("trailing content at " + i + ": " + Rest(text, i));
            return value;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length)
                throw new FormatException("unexpected end of document");
            char c = s[i];
            if (c == '{')
                return ParseObject(s, ref i);
            if (c == '[')
                return ParseArray(s, ref i);
            if (c == '"')
                return ParseString(s, ref i);
            if (Literal(s, ref i, "true"))
                return true;
            if (Literal(s, ref i, "false"))
                return false;
            if (Literal(s, ref i, "null"))
                return null;
            return ParseNumber(s, ref i);
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var result = new Dictionary<string, object>();
            i++;   // '{'
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}')
            {
                i++;
                return result;
            }
            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != '"')
                    throw new FormatException("expected a key at " + i + ": " + Rest(s, i));
                string key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':')
                    throw new FormatException("expected ':' after key '" + key + "'");
                i++;
                object value = ParseValue(s, ref i);
                if (result.ContainsKey(key))
                    throw new FormatException("duplicate key '" + key + "'");
                result[key] = value;
                SkipWhitespace(s, ref i);
                if (i >= s.Length)
                    throw new FormatException("unterminated object");
                if (s[i] == ',')
                {
                    i++;
                    continue;
                }
                if (s[i] == '}')
                {
                    i++;
                    return result;
                }
                throw new FormatException("expected ',' or '}' at " + i + ": " + Rest(s, i));
            }
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var result = new List<object>();
            i++;   // '['
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']')
            {
                i++;
                return result;
            }
            while (true)
            {
                result.Add(ParseValue(s, ref i));
                SkipWhitespace(s, ref i);
                if (i >= s.Length)
                    throw new FormatException("unterminated array");
                if (s[i] == ',')
                {
                    i++;
                    continue;
                }
                if (s[i] == ']')
                {
                    i++;
                    return result;
                }
                throw new FormatException("expected ',' or ']' at " + i + ": " + Rest(s, i));
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++;   // opening quote
            var sb = new System.Text.StringBuilder();
            while (true)
            {
                if (i >= s.Length)
                    throw new FormatException("unterminated string");
                char c = s[i++];
                if (c == '"')
                    return sb.ToString();
                if (c != '\\')
                {
                    if (c < ' ')
                        throw new FormatException("raw control character in a string");
                    sb.Append(c);
                    continue;
                }
                if (i >= s.Length)
                    throw new FormatException("unterminated escape");
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length)
                            throw new FormatException("truncated unicode escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4),
                            NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default:
                        throw new FormatException("unknown escape: " + e);
                }
            }
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && ("+-.eE".IndexOf(s[i]) >= 0 || (s[i] >= '0' && s[i] <= '9')))
                i++;
            if (i == start)
                throw new FormatException("expected a value at " + start + ": " + Rest(s, start));
            string token = s.Substring(start, i - start);
            double value;
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                throw new FormatException("not a JSON number: '" + token + "'");
            return value;
        }

        private static bool Literal(string s, ref int i, string word)
        {
            if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                return false;
            i += word.Length;
            return true;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\n' || s[i] == '\r' || s[i] == '\t'))
                i++;
        }

        private static string Rest(string s, int i)
        {
            return s.Substring(i, Math.Min(40, s.Length - i));
        }
    }
}
