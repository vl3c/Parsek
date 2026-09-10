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
    }
}
