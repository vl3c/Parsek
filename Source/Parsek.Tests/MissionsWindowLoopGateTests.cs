using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    // The Basic-mode gate over the Missions tab's manual-loop authoring controls (design
    // docs/dev/design-ui-basic-advanced.md section 4.5): the per-mission "Loop" toggle, the
    // loop-period cell beside it, and the include checkboxes that pick which intervals /
    // partner journeys the loop replays.
    //
    // The draw sites themselves are IMGUI callbacks with no headless seam, so what is
    // testable is the decision they all read: MissionsWindowUI.ShowsLoopAuthoringControls.
    // Each test names the regression it catches.
    public class MissionsWindowLoopGateTests
    {
        // The four GUIContent fields the include-checkbox family draws with. The scan below
        // is keyed on them, so a NEW include checkbox is covered the moment it takes one of
        // them, and a FIFTH field reds TheIncludeCheckboxContentFamilyIsStillFourFields.
        private static readonly string[] IncludeContentFields =
        {
            "VesselIncludeCheckboxContent",
            "IntervalIncludeCheckboxContent",
            "ChapterIncludeCheckboxContent",
            "PartnerJourneyCheckboxContent",
        };

        private const string GateCall = "ShowsLoopAuthoringControls(";

        // The feature itself. Basic drops the three controls; Advanced is unchanged
        // (philosophy 6 - Advanced stays behaviorally identical to today).
        [Fact]
        public void BasicHidesTheLoopAuthoringControlsAndAdvancedKeepsThem()
        {
            Assert.False(
                MissionsWindowUI.ShowsLoopAuthoringControls(UiComplexityMode.Basic),
                "Basic must not draw the Missions tab's manual-loop authoring controls");
            Assert.True(
                MissionsWindowUI.ShowsLoopAuthoringControls(UiComplexityMode.Advanced),
                "Advanced must keep every loop control it draws today");
        }

        // The gate is the shared decision point, not a private second opinion (philosophy 4).
        // Fails if someone re-implements the rule inline instead of re-keying this helper.
        [Fact]
        public void TheGateIsDerivedFromTheSharedSurfaceDecision()
        {
            foreach (UiComplexityMode mode in new[] { UiComplexityMode.Basic, UiComplexityMode.Advanced })
            {
                Assert.Equal(
                    UiSurfaceVisibility.IsVisible(UiSurface.MissionsLoopControls, mode),
                    MissionsWindowUI.ShowsLoopAuthoringControls(mode));
            }
        }

        // Scope guard. The loop controls live INSIDE the Missions tab, so a mis-keyed gate
        // (pointing at the tab instead of the control group) would take the whole tab dark in
        // Basic - the surface design section 4 pins as a core-loop keeper.
        [Fact]
        public void HidingTheLoopControlsDoesNotHideTheMissionsTab()
        {
            Assert.True(
                UiSurfaceVisibility.IsVisible(UiSurface.TabMissions, UiComplexityMode.Basic),
                "the Missions tab must stay visible in Basic; only its loop controls are gated");
            Assert.True(
                UiSurfaceVisibility.IsVisible(UiSurface.MainButtonRecordings, UiComplexityMode.Basic),
                "the Missions launcher must stay visible in Basic");
        }

        // catches: an include checkbox drawn OUTSIDE the gate, which is finding P21 exactly -
        // the chapter header row's tri-state Toggle wrote Mission.ExcludedIntervalKeys with no
        // ShowsLoopAuthoringControls check, so in Basic a player still had a click that
        // authored the loop set while every checkbox around it was hidden. Source inspection
        // because the draw sites are IMGUI callbacks with no headless seam.
        [Fact]
        public void EveryIncludeCheckboxIsDrawnInsideTheLoopAuthoringGate()
        {
            List<string> failures = FindUngatedIncludeCheckboxDrawSites(ReadMissionsWindowSource());

            Assert.True(failures.Count == 0,
                "every include-checkbox draw site must sit under a ShowsLoopAuthoringControls "
                + "branch. Ungated: " + string.Join("; ", failures.ToArray()));
        }

        // anti-vacuity for the scan above: the needle is a call the file's COMMENTS talk about
        // in four separate places, so a scan over raw source text answers "gated" for a draw
        // site whose only gate is the comment saying it should have one. Same shape as
        // test_hlib.py's decoy declarations.
        [Fact]
        public void TheGateScanRedsWhenTheGateExistsOnlyInAComment()
        {
            // A COMMENTED-OUT gate: the exact branch the scan looks for, in a comment. This
            // is the one decoy the if-condition rule alone cannot catch, so it is the cell
            // that proves the comment blanking is load-bearing (verified by mutation: with
            // SourceScanText.StripCSharpComments stubbed to return its input, this cell is
            // the one that goes green and the real scan goes vacuous).
            string decoy = DecoySource(
                "            // if (ShowsLoopAuthoringControls(mode))   <- dropped in a refactor\n"
                + "            bool toggled = GUILayout.Toggle(shown, VesselIncludeCheckboxContent,\n"
                + "                GUILayout.Width(20f));\n");

            List<string> failures = FindUngatedIncludeCheckboxDrawSites(decoy);

            Assert.Contains(failures, f => f.Contains("VesselIncludeCheckboxContent at line"));
        }

        // catches: the scan accepting a gate that is COMPUTED and then ignored - the shape a
        // half-finished refactor leaves behind (an adjacent control reads the local; the
        // checkbox itself draws unconditionally).
        [Fact]
        public void TheGateScanRedsWhenTheGateResultIsNeverBranchedOn()
        {
            string decoy = DecoySource(
                "            bool loopAuthoring = ShowsLoopAuthoringControls(mode);\n"
                + "            bool toggled = GUILayout.Toggle(shown, VesselIncludeCheckboxContent,\n"
                + "                GUILayout.Width(20f));\n");

            List<string> failures = FindUngatedIncludeCheckboxDrawSites(decoy);

            Assert.Contains(failures, f => f.Contains("VesselIncludeCheckboxContent"));
        }

        // positive control: the two production shapes (direct condition, and the local the
        // real draw sites assign) must both read as gated, or the two cells above are red for
        // everything and prove nothing.
        [Theory]
        [InlineData("            if (ShowsLoopAuthoringControls(mode))\n            {\n"
            + "                bool toggled = GUILayout.Toggle(shown, VesselIncludeCheckboxContent,\n"
            + "                    GUILayout.Width(20f));\n            }\n")]
        [InlineData("            bool loopAuthoring = ShowsLoopAuthoringControls(mode);\n"
            + "            if (!loopAuthoring)\n            {\n                Blank();\n            }\n"
            + "            else\n            {\n"
            + "                bool toggled = GUILayout.Toggle(shown, VesselIncludeCheckboxContent,\n"
            + "                    GUILayout.Width(20f));\n            }\n")]
        public void TheGateScanAcceptsTheProductionGateShapes(string body)
        {
            List<string> failures = FindUngatedIncludeCheckboxDrawSites(DecoySource(body));

            Assert.DoesNotContain(failures, f => f.Contains("VesselIncludeCheckboxContent at line"));
        }

        // catches: a fifth include-family GUIContent field appearing without a row in the scan
        // above, which would leave it ungated and unnoticed - and a RENAME, which the old
        // count-only assertion passed straight over while the gate scan silently stopped
        // covering the renamed field.
        [Fact]
        public void TheIncludeCheckboxContentFamilyIsStillFourFields()
        {
            List<string> found = FindIncludeCheckboxContentFieldNames(ReadMissionsWindowSource());

            Assert.Equal<IEnumerable<string>>(
                SortedCopy(IncludeContentFields), SortedCopy(found.ToArray()));
        }

        // anti-vacuity for the family scan, in its own direction: a documented or commented-out
        // field name must NOT inflate the family, because that reds this cell on source that is
        // in fact still four fields. A REAL fifth declaration still reds it.
        [Fact]
        public void TheFamilyScanIgnoresAFieldNameThatExistsOnlyInAComment()
        {
            string commentDecoy = FieldDeclarationSource()
                + "        // Retired 2026-09-11: private static readonly GUIContent\n"
                + "        // LegacyIncludeCheckboxContent = null;\n";

            Assert.Equal<IEnumerable<string>>(
                SortedCopy(IncludeContentFields),
                SortedCopy(FindIncludeCheckboxContentFieldNames(commentDecoy).ToArray()));

            string realDecoy = FieldDeclarationSource()
                + "        private static readonly GUIContent CrewIncludeCheckboxContent = null;\n";

            Assert.Equal(5, FindIncludeCheckboxContentFieldNames(realDecoy).Count);
        }

        // ==================================================================
        // The scans, as pure functions over source text so a decoy can drive them.
        // ==================================================================

        /// <summary>
        /// Every include-checkbox draw site in <paramref name="src"/> whose enclosing method
        /// does not branch on <c>ShowsLoopAuthoringControls</c> before it, plus any field the
        /// file never draws at all. Empty list = every site is gated.
        ///
        /// <para>Comments are blanked first (<see cref="SourceScanText"/>), EVERY draw site of
        /// each field is examined (not just the first), and the gate must reach an <c>if</c>
        /// CONDITION - directly, or through a local assigned from it that a condition then
        /// reads. A computed-and-ignored gate therefore reds.</para>
        /// </summary>
        internal static List<string> FindUngatedIncludeCheckboxDrawSites(string src)
        {
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(src);
            var failures = new List<string>();

            foreach (string field in IncludeContentFields)
            {
                List<int> sites = ToggleSitesUsing(prepared, field);
                if (sites.Count == 0)
                {
                    failures.Add(field + " is never passed to a GUILayout.Toggle");
                    continue;
                }

                foreach (int site in sites)
                {
                    int methodStart = SourceScanText.EnclosingMethodBodyStart(prepared, site);
                    if (methodStart < 0)
                    {
                        failures.Add(field + " at line " + LineOf(prepared, site)
                            + ": could not locate the enclosing method body");
                        continue;
                    }

                    string span = prepared.Substring(methodStart, site - methodStart);
                    if (!SpanBranchesOnTheGate(span))
                        failures.Add(field + " at line " + LineOf(prepared, site));
                }
            }

            return failures;
        }

        /// <summary>
        /// The names of the include-family <c>GUIContent</c> fields declared in
        /// <paramref name="src"/>, comments excluded.
        /// </summary>
        internal static List<string> FindIncludeCheckboxContentFieldNames(string src)
        {
            string prepared = SourceScanText.StripCSharpComments(src);
            var found = new List<string>();
            foreach (Match m in Regex.Matches(prepared, @"\b([A-Za-z_][A-Za-z0-9_]*CheckboxContent)\s*=[^=]"))
                found.Add(m.Groups[1].Value);
            return found;
        }

        // True when the gate is read as a branch condition somewhere in the span: either
        // inline in the condition, or via a local the span assigns from it and a condition
        // then reads.
        private static bool SpanBranchesOnTheGate(string span)
        {
            List<string> conditions = SourceScanText.IfConditions(span);
            foreach (string condition in conditions)
                if (condition.IndexOf(GateCall, StringComparison.Ordinal) >= 0)
                    return true;

            foreach (Match m in Regex.Matches(
                span, @"([A-Za-z_][A-Za-z0-9_]*)\s*=(?!=)[^;{}]*ShowsLoopAuthoringControls\s*\("))
            {
                string local = m.Groups[1].Value;
                foreach (string condition in conditions)
                    if (SourceScanText.ContainsIdentifier(condition, local))
                        return true;
            }

            return false;
        }

        // Every GUILayout.Toggle( whose BALANCED argument list mentions the field.
        private static List<int> ToggleSitesUsing(string prepared, string contentField)
        {
            const string Needle = "GUILayout.Toggle(";
            var sites = new List<int>();
            int i = 0;
            while (true)
            {
                int at = prepared.IndexOf(Needle, i, StringComparison.Ordinal);
                if (at < 0) return sites;

                int openParen = at + Needle.Length - 1;
                int depth = 0;
                int close = -1;
                for (int k = openParen; k < prepared.Length; k++)
                {
                    if (prepared[k] == '(') depth++;
                    else if (prepared[k] == ')')
                    {
                        depth--;
                        if (depth == 0) { close = k; break; }
                    }
                }
                if (close < 0) close = Math.Min(prepared.Length, at + 400);

                if (prepared.IndexOf(contentField, at, close - at, StringComparison.Ordinal) >= 0)
                    sites.Add(at);
                i = at + Needle.Length;
            }
        }

        private static int LineOf(string src, int index)
        {
            int line = 1;
            for (int i = 0; i < index && i < src.Length; i++)
                if (src[i] == '\n') line++;
            return line;
        }

        private static string[] SortedCopy(string[] values)
        {
            var copy = (string[])values.Clone();
            Array.Sort(copy, StringComparer.Ordinal);
            return copy;
        }

        // A minimal namespace -> class -> method file around a draw-site body, so the scan's
        // block walk sees the same structure the real file has.
        private static string DecoySource(string methodBody)
        {
            return "namespace Parsek\n{\n    internal class Decoy\n    {\n"
                + "        private int DrawRow(Mission mission)\n        {\n"
                + methodBody
                + "            return 1;\n        }\n    }\n}\n";
        }

        private static string FieldDeclarationSource()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("namespace Parsek\n{\n    internal class Decoy\n    {\n");
            foreach (string field in IncludeContentFields)
                sb.Append("        private static readonly GUIContent ").Append(field).Append(" = null;\n");
            return sb.ToString();
        }

        private static string ReadMissionsWindowSource()
        {
            string srcRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Parsek"));
            return File.ReadAllText(Path.Combine(srcRoot, "UI", "MissionsWindowUI.cs"));
        }
    }
}
