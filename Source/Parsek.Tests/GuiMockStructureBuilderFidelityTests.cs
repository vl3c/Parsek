using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Parsek.UI.Gallery;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// THE cell that keeps the Structure List catalogue honest: every row it produces must
    /// be REAL-BUILDER output, because <c>StructureListWindowUI</c> draws
    /// <c>step.Label</c> / <c>step.Status</c> / <c>step.Location</c> / <c>step.VesselName</c>
    /// VERBATIM - so a typed row is a picture of nothing, and the owner's one
    /// non-negotiable is that the mirror shows only what the game can draw.
    ///
    /// <para>The first version of this catalogue hand-built its rows and got them wrong in
    /// four ways at once, none of which any test could see: terminal rows typed the
    /// terminal word into the EVENT column where the builder always writes the generic
    /// <c>"End"</c>, route rows typed <c>"Docked"</c> / <c>"Undocked"</c> where the builder
    /// writes <c>"Dock"</c> / <c>"Undock"</c> and filled a Vessel cell the builder always
    /// leaves empty, a staging row used a branch label instead of
    /// <c>"Staged &lt;part&gt;"</c>, and one whole state claimed a <c>"Switch"</c> row the
    /// branch pass skips by name. These cells are the mechanical answer.</para>
    /// </summary>
    public class GuiMockStructureBuilderFidelityTests
    {
        /// <summary>
        /// Every state id paired with the builder call that must reproduce it. EXPLICIT
        /// rather than derived: a new state with no entry here fails the count assert
        /// instead of slipping through unchecked.
        /// </summary>
        private static Dictionary<string, Func<List<StructureStep>>> ExpectedRows()
            => new Dictionary<string, Func<List<StructureStep>>>(StringComparer.Ordinal)
            {
                { "structure.mission.terminal-splashed",
                  () => GuiMockStructureStates.TerminalRun(
                      TerminalState.Splashed, "Water", "Booster Recovery Test") },
                { "structure.mission.terminal-destroyed",
                  () => GuiMockStructureStates.TerminalRun(
                      TerminalState.Destroyed, "Midlands", "Mun Lander 3") },
                { "structure.mission.terminal-recovered",
                  () => GuiMockStructureStates.TerminalRun(
                      TerminalState.Recovered, "Shores", "Kerbin Return") },
                { "structure.mission.terminal-suborbital",
                  () => GuiMockStructureStates.TerminalRun(
                      TerminalState.SubOrbital, "Highlands", "Sounding Rocket") },
                { "structure.mission.terminal-disassembled",
                  () => GuiMockStructureStates.TerminalRun(
                      TerminalState.Disassembled, "Launch Pad", "Pad Teardown") },
                { "structure.mission.terminal-docked",
                  GuiMockStructureStates.BuildDockedAndBoardedTerminals },
                { "structure.mission.terminal-orbiting-landed",
                  GuiMockStructureStates.BuildOrbitingAndLandedTerminals },
                { "structure.mission.eva-and-board", GuiMockStructureStates.BuildEvaRun },
                { "structure.mission.eva-leg-launch",
                  GuiMockStructureStates.BuildEvaLegLaunchRun },
                { "structure.mission.breakup", GuiMockStructureStates.BuildBreakupRun },
                { "structure.mission.staging-collapse",
                  GuiMockStructureStates.BuildStagingRun },
                { "structure.route.pickup",
                  () => GuiMockStructureStates.RouteRun(
                      GuiMockStructureStates.RouteShape.Pickup) },
                { "structure.route.mixed",
                  () => GuiMockStructureStates.RouteRun(
                      GuiMockStructureStates.RouteShape.Mixed) },
                { "structure.route.origin-depot",
                  () => GuiMockStructureStates.RouteRun(
                      GuiMockStructureStates.RouteShape.DepotOrigin) },
            };

        [Fact]
        public void EveryStructureStateIsExactlyWhatTheRealBuildersReturn()
        {
            Dictionary<string, Func<List<StructureStep>>> expected = ExpectedRows();
            List<GuiMockState> states =
                GuiMockCatalogue.ForWindow(GuiMockSession.StructureWindow);

            Assert.Equal(
                expected.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
                states.Select(s => s.Id).OrderBy(k => k, StringComparer.Ordinal).ToArray());

            foreach (GuiMockState state in states)
            {
                List<StructureStep> got = state.Build().Structure.Steps;
                List<StructureStep> want = expected[state.Id]();
                Assert.True(got.Count > 0, state.Id + " produced no rows");
                Assert.Equal(Describe(want), Describe(got));
            }
        }

        [Fact]
        public void TheTerminalRowsEventCellIsTheGenericEndWordAndTheStatusCarriesTheKind()
        {
            // The specific misreading the first version shipped, pinned so it cannot come
            // back: the terminal pass writes Label = "End" ALWAYS, and puts the terminal
            // word in STATUS so the two columns are not redundant.
            foreach (TerminalState terminal in new[]
                     {
                         TerminalState.Splashed, TerminalState.Destroyed,
                         TerminalState.Recovered, TerminalState.SubOrbital,
                         TerminalState.Disassembled,
                     })
            {
                List<StructureStep> rows =
                    GuiMockStructureStates.TerminalRun(terminal, "Water", "Probe");
                StructureStep row = rows.Single(r => r.Kind == StructureStepKind.Terminal);
                Assert.Equal("End", row.Label);
                Assert.Equal(MissionCompositionBuilder.TerminalName(terminal), row.Status);
            }
        }

        [Fact]
        public void TheBreakupStateReachesAllFourFailureCauseWords()
        {
            var labels = new HashSet<string>(
                GuiMockStructureStates.BuildBreakupRun().Select(r => r.Label),
                StringComparer.Ordinal);
            foreach (string want in new[] { "Overheated", "Broke up", "Broke off", "Crashed" })
                Assert.Contains(want, labels);
        }

        [Fact]
        public void TheStagingStateReachesTheCollapseAndBothNamedJettisonForms()
        {
            List<StructureStep> rows = GuiMockStructureStates.BuildStagingRun();
            var labels = rows.Select(r => r.Label).ToList();
            // The xN collapse is the display walk's, so the row must come back collapsed
            // rather than as eight rows.
            Assert.Contains(
                MissionStructureListBuilder.FormatCollapsedLabel("Shroud jettisoned", 8),
                labels);
            Assert.Contains("Fairing jettisoned", labels);
            // "Staged <part>" is what a Decoupled part event renders as - NOT the branch
            // label "Decoupled", which the first version used.
            Assert.Contains(labels, l => l.StartsWith("Staged ", StringComparison.Ordinal));
            Assert.DoesNotContain("Decoupled", labels);
        }

        [Fact]
        public void TheTwoEvaFormsAreDifferentRowsAndOnlyTheRootLegCarriesTheCrewName()
        {
            // A mid-mission EVA branch row is the bare event word; the crew name in the
            // EVENT cell exists only on a ROOT leg, which is a different tree shape.
            var midLabels = GuiMockStructureStates.BuildEvaRun()
                .Select(r => r.Label).ToList();
            Assert.Contains("EVA", midLabels);
            Assert.Contains("Boarded", midLabels);
            Assert.DoesNotContain(midLabels,
                l => l.StartsWith("EVA ", StringComparison.Ordinal));

            var rootLabels = GuiMockStructureStates.BuildEvaLegLaunchRun()
                .Select(r => r.Label).ToList();
            Assert.Contains(rootLabels,
                l => l.StartsWith("EVA ", StringComparison.Ordinal));
        }

        [Fact]
        public void TheRouteRowsUseTheBuildersOwnDockWordsAndLeaveEveryVesselCellEmpty()
        {
            foreach (GuiMockStructureStates.RouteShape shape in new[]
                     {
                         GuiMockStructureStates.RouteShape.Pickup,
                         GuiMockStructureStates.RouteShape.Mixed,
                         GuiMockStructureStates.RouteShape.DepotOrigin,
                     })
            {
                List<StructureStep> rows = GuiMockStructureStates.RouteRun(shape);
                var labels = rows.Select(r => r.Label).ToList();
                Assert.Contains("Dock", labels);
                Assert.Contains("Undock", labels);
                // The branch-event spellings the first version used are NOT route rows.
                Assert.DoesNotContain("Docked", labels);
                Assert.DoesNotContain("Undocked", labels);
                foreach (StructureStep row in rows)
                    Assert.Equal("", row.VesselName ?? "");
            }
        }

        [Fact]
        public void NoSwitchContinuationRowCanBeDrawnSoNoStateClaimsOne()
        {
            // The branch pass skips BranchPointType.VesselSwitchContinuation BY NAME ("an
            // observation boundary, not a physical event"), so the "Switch" word
            // MissionCompositionBuilder can produce is unreachable in THIS window. Proven
            // by running a tree that carries exactly that branch point.
            List<StructureStep> rows = new GuiMockStructureStates.MissionInputs()
                .Leg("carrier", "Twin Probe Carrier", 500_000.0, 506_000.0,
                     TerminalState.Orbiting, "Kerbin", "", "Orbiting",
                     launchSite: "Launch Pad")
                .Leg("probe", "Probe B", 503_000.0, 505_000.0, null,
                     "Kerbin", "", "Orbiting", parentAnchorId: "carrier")
                .Branch(BranchPointType.VesselSwitchContinuation, 504_000.0,
                        "carrier", "probe")
                .Rows();
            string switchWord = MissionCompositionBuilder.BranchEventName(
                BranchPointType.VesselSwitchContinuation, null);
            Assert.Equal("Switch", switchWord);
            Assert.DoesNotContain(switchWord, rows.Select(r => r.Label));
            // And no catalogue state claims it.
            foreach (GuiMockState state in GuiMockCatalogue.All)
                Assert.DoesNotContain("switch", state.Id, StringComparison.Ordinal);
        }

        [Fact]
        public void NoGalleryFileAssignsAStringLiteralToADrawnStructureStepField()
        {
            // The gate that makes the rule mechanical rather than a habit, and it is
            // SCOPED TO A `new StructureStep { ... }` INITIALIZER rather than run over
            // whole lines. That scoping is not tidiness: a gallery INPUT legitimately
            // carries typed data - a Recording's VesselName is the recorded craft name,
            // and the builder decides whether any row shows it - so a line-wide scan
            // fires on exactly the assignments the design wants. It caught
            // `VesselName = "Supply Tug"` on a Recording on its first run, which is how
            // this scoping got written.
            var violations = new List<string>();
            var assign = new Regex(
                @"\b(Label|Status|Location|VesselName)\s*=\s*[$@]*""",
                RegexOptions.CultureInvariant);

            foreach (string path in GalleryFiles())
            {
                foreach (string hit in ScopedHits(File.ReadAllText(path), assign))
                    violations.Add(Path.GetFileName(path) + ": " + hit);
            }

            Assert.True(violations.Count == 0,
                "a gallery file assigns a STRING LITERAL to a StructureStep field the "
                + "window draws verbatim. Build detached inputs and run the real builders "
                + "instead - a typed row is a picture of nothing:\n  "
                + string.Join("\n  ", violations));
        }

        [Fact]
        public void TheLiteralAssignmentScanIsNotVacuous()
        {
            // Anti-vacuity over SYNTHETIC source, both directions: the real files' comments
            // quote the very spellings this scan looks for ("a route's dock rows read
            // \"Dock\" / \"Undock\""), so a scan that read comments would fire on the
            // prose that documents the rule.
            var assign = new Regex(
                @"\b(Label|Status|Location|VesselName)\s*=\s*[$@]*""",
                RegexOptions.CultureInvariant);

            string commentOnly = SourceScanText.StripCSharpComments(string.Join("\n", new[]
            {
                "// the builder writes Label = \"End\" always",
                "/// <para>A route row's Label = \"Dock\".</para>",
            }));
            Assert.False(assign.IsMatch(commentOnly),
                "a spelling named in a COMMENT must not trip the gate");

            string realAssignment = SourceScanText.StripCSharpComments(
                "                    Label = \"Splashed\",");
            Assert.True(assign.IsMatch(realAssignment),
                "a real literal assignment must trip the gate");

            // And the SCOPING works in both directions on a synthetic source: the same
            // field name is a violation inside a StructureStep initializer and legitimate
            // input data on a Recording.
            string synthetic = string.Join("\n", new[]
            {
                "var rec = new Recording",
                "{",
                "    VesselName = \"Supply Tug\",",
                "};",
                "steps.Add(new StructureStep",
                "{",
                "    Label = \"Splashed\",",
                "});",
            });
            List<string> scoped = ScopedHits(synthetic, assign);
            Assert.Single(scoped);
            Assert.EndsWith("Label = \"", scoped[0], StringComparison.Ordinal);

            // And the scan reads real files rather than nothing.
            Assert.True(GalleryFiles().Count >= 8);
        }

        [Fact]
        public void EveryStructureStateBuildsAFreshGraphPerCall()
        {
            // The builders take MUTABLE inputs, so a shared graph would let one capture's
            // rows leak into the next apply.
            foreach (GuiMockState state in
                     GuiMockCatalogue.ForWindow(GuiMockSession.StructureWindow))
            {
                GuiMockStructure a = state.Build().Structure;
                GuiMockStructure b = state.Build().Structure;
                Assert.NotSame(a.Steps, b.Steps);
                Assert.Equal(Describe(a.Steps), Describe(b.Steps));
            }
        }

        // ----- helpers -----

        /// <summary>
        /// Every <paramref name="pattern"/> match that falls INSIDE a
        /// <c>new StructureStep { ... }</c> object initializer, as
        /// <c>&lt;line&gt;: &lt;match&gt;</c>.
        ///
        /// <para>TWO preparations of the same text, both length-preserving, so an index
        /// means the same thing in each: braces are walked over the literals-masked form
        /// (an interpolated hole or a literal brace would otherwise unbalance the count)
        /// and the pattern is matched in the form that still HAS its literals. An
        /// unbalanced brace is a FAILURE, not a pass -
        /// <see cref="SourceScanText.BraceMatchedBlock"/> throws.</para>
        /// </summary>
        private static List<string> ScopedHits(string raw, Regex pattern)
        {
            var hits = new List<string>();
            string commentsOnly = SourceScanText.StripCSharpComments(raw);
            string forBraces = SourceScanText.StripCommentsAndMaskLiterals(raw);

            int at = 0;
            while (true)
            {
                at = forBraces.IndexOf("new StructureStep", at, StringComparison.Ordinal);
                if (at < 0) break;
                int open = forBraces.IndexOf('{', at);
                if (open < 0) break;
                string block = SourceScanText.BraceMatchedBlock(forBraces, open);
                string realBlock = commentsOnly.Substring(open, block.Length);
                foreach (Match m in pattern.Matches(realBlock))
                {
                    int line = 1;
                    for (int i = 0; i < open + m.Index; i++)
                        if (commentsOnly[i] == '\n') line++;
                    hits.Add(line + ": " + m.Value.Trim());
                }
                at = open + block.Length;
            }
            return hits;
        }

        /// <summary>A stable, readable rendering of a row list, so a mismatch names the
        /// cell rather than printing two object graphs.</summary>
        private static string Describe(List<StructureStep> steps)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < steps.Count; i++)
            {
                StructureStep s = steps[i];
                sb.Append(i).Append(" ut=")
                  .Append(double.IsNaN(s.UT)
                      ? "NaN"
                      : s.UT.ToString("F0", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(" kind=").Append(s.Kind)
                  .Append(" label='").Append(s.Label ?? "")
                  .Append("' status='").Append(s.Status ?? "")
                  .Append("' loc='").Append(s.Location ?? "")
                  .Append("' vessel='").Append(s.VesselName ?? "")
                  .Append("'\n");
            }
            return sb.ToString();
        }

        private static List<string> GalleryFiles()
        {
            string dir = Path.Combine(ResolveRepoRoot(), "Source", "Parsek", "UI", "Gallery");
            Assert.True(Directory.Exists(dir),
                "gallery directory moved, this gate is vacuous: " + dir);
            return new List<string>(
                Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories));
        }

        private static string ResolveRepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "scripts"))
                    && Directory.Exists(Path.Combine(dir, "Source")))
                {
                    return dir;
                }
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException(
                "repo root not found from " + AppContext.BaseDirectory);
        }
    }
}
