using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The cross-window style contract: one countdown, one "Go to" spelling, the
    /// mode-aware route label, one empty-list voice, the Mission Log's empty layout, and
    /// the shared palette. Pure cells call the helpers; the source cells pin the draw
    /// sites that IMGUI cannot expose headlessly.
    /// </summary>
    [Collection("Sequential")]
    public class SharedStyleConsistencyTests : IDisposable
    {
        public SharedStyleConsistencyTests()
        {
            ParsekTimeFormat.KerbinTimeOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekTimeFormat.ResetForTesting();
        }

        // ---------------- route label (Basic reads no loop word) ----------------

        [Fact]
        public void RouteBoundLabel_Basic_SaysRunByRoute()
        {
            Assert.Equal("Run by route", MissionsWindowUI.RouteBoundLabel(UiComplexityMode.Basic));
            Assert.Equal("Run on the schedule of route 'Minmus Ore Run'.",
                MissionsWindowUI.RouteBoundTooltip(UiComplexityMode.Basic, "Minmus Ore Run"));
        }

        [Fact]
        public void RouteBoundLabel_Advanced_KeepsTheLoopWording()
        {
            Assert.Equal("Looped by route", MissionsWindowUI.RouteBoundLabel(UiComplexityMode.Advanced));
            Assert.Equal("Looped by route: Minmus Ore Run",
                MissionsWindowUI.RouteBoundTooltip(UiComplexityMode.Advanced, "Minmus Ore Run"));
        }

        [Fact]
        public void RouteBoundLabel_Basic_NeverNamesALoop()
        {
            // Fails if Basic's label or hover picks up the loop vocabulary, for a named route
            // and for one with no name.
            foreach (string name in new[] { "Duna Supply", null, "" })
            {
                string text = MissionsWindowUI.RouteBoundLabel(UiComplexityMode.Basic) + " "
                    + MissionsWindowUI.RouteBoundTooltip(UiComplexityMode.Basic, name);
                Assert.DoesNotContain("loop", text, StringComparison.OrdinalIgnoreCase);
            }
            Assert.Equal("Run on the schedule of route 'route'.",
                MissionsWindowUI.RouteBoundTooltip(UiComplexityMode.Basic, null));
        }

        [Fact]
        public void RouteBoundLabel_BothDrawSitesReadTheFrameLatchedMode()
        {
            // The label is a control: its text must come from the frame-latched mode so the
            // Layout and Repaint passes of one frame agree, at the Basic site and the
            // Advanced loop row alike. And the label method itself must not hard-code a word.
            string code = Dense(ReadSource("UI", "MissionsWindowUI.cs"));
            Assert.Contains("DrawLoopedByRouteLabel(bindingRoute,ParsekUI.AppliedUiComplexityMode);", code);
            Assert.Contains("DrawLoopedByRouteLabel(bindingRoute,ParsekUI.AppliedUiComplexityMode,LoopCellWidth);", code);
            string body = MethodBody(code, "privatevoidDrawLoopedByRouteLabel(");
            Assert.Contains("RouteBoundLabel(mode)", body);
            Assert.Contains("RouteBoundTooltip(mode,", body);
            Assert.DoesNotContain("\"Loopedbyroute", body);
        }

        // ---------------- one countdown ----------------

        [Fact]
        public void EveryWindowCountdownGoesThroughTheHouseHelper()
        {
            // Fails if a window builds a countdown of its own ("T-" glued to a number, or the
            // removed SelectiveSpawnUI / MissionsWindowUI copies) instead of
            // ParsekTimeFormat.FormatCountdown.
            string[] files =
            {
                "MissionsWindowUI.cs", "LogisticsRoutePresentation.cs", "RecordingsTableUI.cs",
                "SpawnControlUI.cs", "SpawnControlPresentation.cs", "TimelineWindowUI.cs",
            };
            foreach (string f in files)
            {
                string code = StripComments(ReadSource("UI", f));
                Assert.DoesNotContain("SelectiveSpawnUI.FormatCountdown", code);
                Assert.DoesNotContain("FormatCountdownCompact", code);
                Assert.DoesNotContain("\"T- \"", code);
                Assert.DoesNotContain("\"T-\"", code);
            }
            Assert.Contains("ParsekTimeFormat.FormatCountdown(delta)",
                StripComments(ReadSource("UI", "SpawnControlUI.cs")));
            Assert.Contains("ParsekTimeFormat.FormatCountdown(entryUT - currentUT)",
                StripComments(ReadSource("UI", "TimelineWindowUI.cs")));
        }

        [Fact]
        public void TheMissionsCountdownCellIsTheHouseCountdown()
        {
            // The Missions T- cell (re-aim and phase-locked branches) reads exactly the house
            // countdown, so Logistics, Real Spawn Control and the Timeline cannot drift from it.
            ParsekTimeFormat.KerbinTimeOverrideForTesting = false;
            string cell = MissionsWindowUI.BuildTMinusCellText(
                looping: true, solved: true, shouldPhaseLock: true, unitBuilt: true,
                p: 3600.0, nextRelaunchUT: 1000.0 + 2 * 86400.0 + 4 * 3600.0, nowUT: 1000.0);
            Assert.Equal("T- 2d 4h", cell);
            Assert.Equal(ParsekTimeFormat.FormatCountdown(2 * 86400.0 + 4 * 3600.0), cell);
        }

        [Fact]
        public void TheCountdownCellsAreDrawnInTheHouseAmber()
        {
            Assert.Equal(ParsekUI.CountdownTextColor, MissionsWindowUI.LoopPeriodClampColor);
            string spawn = Dense(StripComments(ReadSource("UI", "SpawnControlUI.cs")));
            Assert.Contains("GUI.contentColor=ParsekUI.CountdownTextColor;GUILayout.Label(ParsekTimeFormat.FormatCountdown(delta)", spawn);
            // The Status word "Leaves" names a departure still ahead: amber, not the palette yellow.
            Assert.Contains("caseSpawnCandidateStatus.Leaves:returnParsekUI.CountdownTextColor;", spawn);
            Assert.DoesNotContain("StatusColorKind.Yellow", spawn);
            string timeline = Dense(StripComments(ReadSource("UI", "TimelineWindowUI.cs")));
            Assert.Contains("timelineCountdownStyle.normal.textColor=ParsekUI.CountdownTextColor;", timeline);
            Assert.Contains("GUILayout.Label(time,showCountdownTime?timelineCountdownStyle:style,", timeline);
            string logistics = Dense(StripComments(ReadSource("UI", "LogisticsWindowUI.cs")));
            Assert.Contains("nextAmberStyle.normal.textColor=ParsekUI.CountdownTextColor;", logistics);
            string recordings = Dense(StripComments(ReadSource("UI", "RecordingsTableUI.cs")));
            Assert.Contains("statusStyleFuture.normal.textColor=ParsekUI.CountdownTextColor;", recordings);
        }

        // ---------------- the shared palette ----------------

        [Fact]
        public void TheMutedGreyIsOneValue()
        {
            Assert.Equal(new UnityEngine.Color(0.78f, 0.78f, 0.78f), ParsekUI.MutedTextColor);
            Assert.Equal(ParsekUI.MutedTextColor, MissionsWindowUI.MissionSummaryTextColor);
            Assert.Contains("detailStyle.normal.textColor=ParsekUI.MutedTextColor;",
                Dense(StripComments(ReadSource("UI", "LogisticsWindowUI.cs"))));
            Assert.Contains("textColor=ParsekUI.MutedTextColor",
                Dense(StripComments(ReadSource("UI", "KerbalsWindowUI.cs"))));
        }

        [Fact]
        public void TheConsolidatedWindowsCarryNoPrivateStatusColourLiterals()
        {
            // Fails if a near-duplicate green / red / blue / grey literal comes back in a
            // window that was moved onto ParsekUI.StatusColor / MutedTextColor / DimTextColor.
            // (Recordings' phase legend, its ended grey and static orange, and the Spawn
            // Control departing orange carry meanings of their own and are not in this set.)
            foreach (string f in new[] { "KerbalsWindowUI.cs", "TimelineWindowUI.cs", "TestRunnerUI.cs" })
            {
                string code = StripComments(ReadSource("UI", f));
                foreach (Match m in Regex.Matches(code, @"new Color\(([^)]*)\)"))
                {
                    string args = Regex.Replace(m.Groups[1].Value, @"\s+", "");
                    Assert.True(args == "0.9f,0.9f,0.9f",
                        f + " still builds a private colour: new Color(" + args + ")");
                }
                Assert.DoesNotContain("Color.green", code);
                Assert.DoesNotContain("Color.red", code);
                Assert.DoesNotContain("Color.yellow", code);
            }
        }

        // ---------------- one empty-list voice ----------------

        [Fact]
        public void EmptyListSentencesUseTheSharedGreyStyle()
        {
            Assert.Contains("GUILayout.Label(\"Nomissionsrecordedyet.\",parentUI.GetEmptyStateStyle());",
                Dense(ReadSource("UI", "MissionsWindowUI.cs")));
            Assert.Contains("GUILayout.Label(\"Norecordingsyet.\",parentUI.GetEmptyStateStyle());",
                Dense(ReadSource("UI", "RecordingsTableUI.cs")));
            Assert.Contains("GUILayout.Label(\"Nonearbycrafttospawn.\",parentUI.GetEmptyStateStyle());",
                Dense(ReadSource("UI", "SpawnControlUI.cs")));
            Assert.Contains("GUILayout.Label(\"Notimelineentries.\",parentUI.GetEmptyStateStyle());",
                Dense(ReadSource("UI", "TimelineWindowUI.cs")));
            Assert.Contains("GUILayout.Label(\"\"+emptyText,parentUI.GetEmptyStateStyle());",
                Dense(ReadSource("UI", "LogisticsWindowUI.cs")));
        }

        [Fact]
        public void NoWindowDrawsANoneParenthesis()
        {
            // "(none)" is never an empty state: an empty list is one grey sentence and an
            // empty value drops its clause.
            foreach (string f in Directory.GetFiles(SourceDir("UI"), "*.cs"))
            {
                string code = StripComments(File.ReadAllText(f));
                Assert.False(code.Contains("\"(none)\""), Path.GetFileName(f) + " draws \"(none)\"");
            }
        }

        // ---------------- the Mission Log's empty layout ----------------

        [Fact]
        public void MissionLogEmpty_KeepsHeadersAndSentenceRowAndCloseAtTheBottom()
        {
            // Fails if the Mission Log grows its own empty shape again: both instances (the
            // Mission Log and the Route History) draw the table - headers, then the one grey
            // sentence as the body row - then the hover strip and Close at the bottom, with
            // no early return before the table.
            string code = Dense(StripComments(ReadSource("UI", "StructureListWindowUI.cs")));
            Assert.DoesNotContain("DrawsTableWhenEmpty", code);
            string window = MethodBody(code, "privatevoidDrawWindow(intwindowID)");
            Assert.DoesNotContain("return;", window);
            int table = window.IndexOf("DrawStepTable();", StringComparison.Ordinal);
            int echo = window.IndexOf("tooltipEcho.Draw();", StringComparison.Ordinal);
            int close = window.IndexOf("GUILayout.Button(\"Close\")", StringComparison.Ordinal);
            Assert.True(table >= 0 && echo > table && close > echo,
                "DrawWindow must draw the table, then the hover strip, then Close");
            Assert.Equal(1, Regex.Matches(window, Regex.Escape("GUILayout.Button(\"Close\")")).Count);

            string stepTable = MethodBody(code, "privatevoidDrawStepTable()");
            Assert.Contains("DrawColumnHeader();", stepTable);
            Assert.Contains("if(steps.Count==0)DrawEmptyBodyRow();", stepTable);
            string emptyRow = MethodBody(code, "privatevoidDrawEmptyBodyRow()");
            Assert.Contains("GUILayout.Label(EmptyText(mode),emptyBodyCellLabel);", emptyRow);
            Assert.Contains("normal={textColor=ParsekUI.MutedTextColor}", window);
        }

        [Fact]
        public void MissionLogEmptyText_IsOneSentencePerMode()
        {
            Assert.Equal(StructureListWindowUI.EmptyMissionText,
                StructureListWindowUI.EmptyText(StructureListWindowUI.TargetMode.Mission));
            Assert.Equal(RouteHistoryBuilder.EmptyText,
                StructureListWindowUI.EmptyText(StructureListWindowUI.TargetMode.Route));
            foreach (StructureListWindowUI.TargetMode m in
                Enum.GetValues(typeof(StructureListWindowUI.TargetMode)))
            {
                string text = StructureListWindowUI.EmptyText(m);
                Assert.EndsWith(".", text);
                Assert.DoesNotContain("(none)", text);
            }
        }

        // ---------------- "Go to" ----------------

        [Fact]
        public void GoToIsSpelledTheSameInEveryWindow()
        {
            Assert.Equal("Go to", TimelineWindowUI.GoToButtonLabel);
            Assert.Equal(TimelineWindowUI.GoToButtonLabel, LogisticsRoutePresentation.GoToButtonLabel);
            foreach (string f in Directory.GetFiles(SourceDir("UI"), "*.cs"))
            {
                string code = StripComments(File.ReadAllText(f));
                Assert.False(code.Contains("\"GoTo\""), Path.GetFileName(f) + " still labels a button \"GoTo\"");
            }
        }

        // ---------------- helpers ----------------

        private static string SourceDir(string sub)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string dir = Path.Combine(root, "Source", "Parsek", sub);
            Assert.True(Directory.Exists(dir), "source dir not found: " + dir);
            return dir;
        }

        private static string ReadSource(string sub, string file)
        {
            string path = Path.Combine(SourceDir(sub), file);
            Assert.True(File.Exists(path), "source not found: " + path);
            return File.ReadAllText(path).Replace("\r\n", "\n");
        }

        private static string Dense(string s) => Regex.Replace(s, @"\s+", string.Empty);

        // Line comments only (the gates read code, never a comment quoting it).
        private static string StripComments(string s) =>
            Regex.Replace(s, @"(?m)^\s*//.*$", string.Empty);

        private static string MethodBody(string code, string signature)
        {
            int at = code.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "method signature not found: " + signature);
            int open = code.IndexOf('{', at);
            int depth = 0;
            for (int i = open; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}')
                {
                    depth--;
                    if (depth == 0) return code.Substring(open + 1, i - open - 1);
                }
            }
            throw new InvalidOperationException("unbalanced braces after " + signature);
        }
    }
}
