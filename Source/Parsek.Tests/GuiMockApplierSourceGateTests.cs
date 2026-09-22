using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Parsek.UI.Gallery;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// WITNESSES for the APPLIER half of <c>UiAction op=mock</c>, in the shape
    /// <c>GuiCensusApplierSourceGateTests</c> established.
    ///
    /// <para>The pure half is exercised directly by <c>TestCommandUiMockTests</c>. The
    /// applier lives on a <c>MonoBehaviour</c> partial that reaches into <c>ParsekUI</c>,
    /// so xUnit cannot call it - which would leave the two per-window tables (the install
    /// arms and the suppression sites) with no cell on them at all. A window wired to the
    /// wrong class's field, or a suppression site declared and never wired, would survive
    /// the whole suite and cost a flight.</para>
    ///
    /// <para>These cells close that by deriving the applier's tables FROM ITS SOURCE and
    /// comparing them against the declared ones. Comments are stripped first, with line
    /// indices preserved, for the reason every source gate here strips them: these files'
    /// headers discuss the windows they do NOT support and the stores they do NOT touch,
    /// so a scan that read comments would report rows that are not wired and pass against
    /// a source saying the opposite.</para>
    /// </summary>
    public class GuiMockApplierSourceGateTests
    {
        private const string ApplierPath = "TestCommands/ParsekTestCommandAddon.UiMock.cs";

        // ----- the suppression-site set -----

        [Fact]
        public void EveryDeclaredSuppressionSiteIsWiredExactlyOnce()
        {
            // The hole this closes: GuiMockSession.SuppressionSites is what the session
            // logs and what the unit suite walks, and the PRODUCTION sites are separate
            // call sites in three window classes. A site declared and never wired is a
            // mock that gets clobbered mid-capture; a site wired and never declared is a
            // suppression nobody can see in the log.
            Dictionary<string, int> wired = WiredSuppressionSites();
            var declared = GuiMockSession.SuppressionSites
                .Select(s => SiteFieldName(s))
                .ToList();

            Assert.Equal(
                declared.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                wired.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray());

            foreach (var pair in wired)
            {
                Assert.True(pair.Value == 1,
                    "suppression site '" + pair.Key + "' is wired " + pair.Value
                    + " times. One production site per declared token, or the one-shot "
                    + "Verbose line names a site whose second caller stays invisible.");
            }
        }

        [Fact]
        public void TheSuppressionSiteScanIsNotVacuous()
        {
            // Anti-vacuity over a SYNTHETIC source: the real sites sit next to comments
            // that name them ("GUI state gallery: same suppression as InvalidateCache"),
            // so a scan reading comments would find sites that are not wired.
            string synthetic = string.Join("\n", new[]
            {
                "// if (GuiMockSession.Suppressed(GuiMockSession.CareerVmRebuild)) return false;",
                "/// <para>Mirrors GuiMockSession.Suppressed(GuiMockSession.KerbalsLiveCrew).</para>",
                "public void InvalidateCache()",
                "{",
                "    if (Parsek.UI.Gallery.GuiMockSession.Suppressed(",
                "            Parsek.UI.Gallery.GuiMockSession.KerbalsInvalidate))",
                "        return;",
                "}",
            });
            Dictionary<string, int> found = ScanSuppressionSites(
                SourceScanText.StripCommentsAndMaskLiterals(synthetic));
            Assert.Equal(new[] { "KerbalsInvalidate" }, found.Keys.ToArray());
            Assert.Equal(1, found["KerbalsInvalidate"]);
        }

        // ----- the install-arm set -----

        [Fact]
        public void TheInstallArmsCoverExactlyTheSupportedWindows()
        {
            // The mapping-error class the sibling gate's header describes: an arm reaching
            // the wrong window's field lives in ONE of three branches while the others
            // stay right, and a headless suite cannot witness it. What it CAN witness is
            // the set: a supported window with no arm throws at the resolver's default
            // (so every state on it fails after a KSP boot), and an arm for an
            // unsupported window is dead code that reads as coverage.
            string code = PreparedSource(ApplierPath);

            var armRe = new Regex(
                @"case\s+GuiMockSession\.(\w+Window)\s*:", RegexOptions.CultureInvariant);
            var armed = new List<string>();
            foreach (Match m in armRe.Matches(code))
                armed.Add(WindowConstantValue(m.Groups[1].Value));

            Assert.True(armed.Count > 0,
                "no install arm parsed out of " + ApplierPath + "; the switch moved and "
                + "this gate is vacuous");
            Assert.Equal(
                GuiMockCatalogue.SupportedWindows.OrderBy(w => w, StringComparer.Ordinal)
                    .ToArray(),
                armed.Distinct().OrderBy(w => w, StringComparer.Ordinal).ToArray());
        }

        [Fact]
        public void EachInstallArmNamesItsOwnWindowsUiAccessorAndItsOwnInjectionMember()
        {
            // One row per window, each naming the accessor and the member it swaps. This
            // is the cell a mis-wired arm (kerbals reaching the career field) fails.
            string code = PreparedSource(ApplierPath);
            var expected = new[]
            {
                new { Method = "InstallKerbalsMock",
                      Accessor = "ui.GetKerbalsUI()",
                      Member = "CachedViewModelForTesting" },
                new { Method = "InstallCareerMock",
                      Accessor = "ui.GetCareerStateUI()",
                      Member = "CachedVMForTesting" },
                new { Method = "InstallStructureMock",
                      Accessor = "ui.GetStructureListUI()",
                      Member = "OpenWithGallerySteps" },
            };

            foreach (var row in expected)
            {
                int at = code.IndexOf("void " + row.Method + "(", StringComparison.Ordinal);
                Assert.True(at >= 0,
                    "install arm " + row.Method + " not found in " + ApplierPath
                    + "; this gate is vacuous");
                int open = code.IndexOf('{', at);
                Assert.True(open > at);
                string body = SourceScanText.BraceMatchedBlock(code, open);
                Assert.True(body.IndexOf(row.Accessor, StringComparison.Ordinal) >= 0,
                    row.Method + " does not call " + row.Accessor);
                Assert.True(body.IndexOf(row.Member, StringComparison.Ordinal) >= 0,
                    row.Method + " does not touch " + row.Member);
                // And it does not reach ANOTHER window's accessor: that is the mapping
                // error this whole gate exists for.
                foreach (var other in expected)
                {
                    if (other.Method == row.Method) continue;
                    Assert.True(body.IndexOf(other.Accessor, StringComparison.Ordinal) < 0,
                        row.Method + " also reaches " + other.Accessor);
                }
            }
        }

        [Fact]
        public void EveryInstallArmPushesItsOwnUndoAsItWrites()
        {
            // A restore closure is what makes the paired op a pair, and the UNDO STACK is
            // what makes the install transactional: each arm pushes an undo as it writes,
            // so a throw part-way through unwinds exactly what happened instead of leaving
            // half an install standing with no way back. An arm that wrote without pushing
            // would leave the mock standing after the clear, and the next REAL capture
            // would photograph it.
            string code = PreparedSource(ApplierPath);
            foreach (string method in new[]
                     { "InstallKerbalsMock", "InstallCareerMock", "InstallStructureMock" })
            {
                int at = code.IndexOf("void " + method + "(", StringComparison.Ordinal);
                Assert.True(at >= 0,
                    "install arm " + method + " not found; this gate is vacuous");
                string body = SourceScanText.BraceMatchedBlock(code, code.IndexOf('{', at));
                Assert.True(body.IndexOf("undo.Add(", StringComparison.Ordinal) >= 0,
                    method + " pushes no undo, so a throw mid-install has no way back");
            }

            // And the caller really unwinds on a throw rather than swallowing it.
            int dataAt = code.IndexOf("Action InstallMockData(", StringComparison.Ordinal);
            Assert.True(dataAt >= 0, "InstallMockData moved; this gate is vacuous");
            string dataBody = SourceScanText.BraceMatchedBlock(
                code, code.IndexOf('{', dataAt));
            Assert.True(dataBody.IndexOf("Unwind(undo)", StringComparison.Ordinal) >= 0,
                "InstallMockData does not unwind a partial install");
            Assert.True(dataBody.IndexOf("throw;", StringComparison.Ordinal) >= 0,
                "InstallMockData swallows an install exception instead of re-throwing it");
        }

        // ----- the every-exit-clears discipline -----

        [Fact]
        public void EveryExitPathClearsTheSession()
        {
            // The ReleaseRaisedDialogInputLock discipline: release on EVERY exit rather
            // than on the happy one. Derived from the addon's own comment-stripped source
            // so a removed call reds here instead of leaving a scope pointing at a
            // destroyed window.
            string addon = PreparedSource("TestCommands/ParsekTestCommandAddon.cs");
            foreach (string site in new[]
                     {
                         "OnSceneChangeRequested", "OnLevelWasLoaded", "FlushAndQuitImpl",
                     })
            {
                int at = addon.IndexOf("void " + site + "(", StringComparison.Ordinal);
                Assert.True(at >= 0,
                    "exit path " + site + " not found; this gate is vacuous");
                string body = SourceScanText.BraceMatchedBlock(addon, addon.IndexOf('{', at));
                Assert.True(
                    body.IndexOf("ClearGuiMockSessionOnExit", StringComparison.Ordinal) >= 0,
                    site + " does not clear a live GUI-state-gallery scope. Every exit "
                    + "clears, or a scope survives into a scene whose window instances "
                    + "are gone.");
            }

            // And the save refusal, which is lane hygiene rather than a data guard.
            int saveAt = addon.IndexOf("void SaveGameImpl(", StringComparison.Ordinal);
            Assert.True(saveAt >= 0);
            string saveBody = SourceScanText.BraceMatchedBlock(
                addon, addon.IndexOf('{', saveAt));
            Assert.True(
                saveBody.IndexOf("GuiMockSession.IsLive", StringComparison.Ordinal) >= 0,
                "SaveGame does not refuse while a mock scope is live");
        }

        // ----- scanning -----

        private static Dictionary<string, int> WiredSuppressionSites()
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            string root = Path.Combine(ResolveRepoRoot(), "Source", "Parsek");
            foreach (string path in Directory.EnumerateFiles(root, "*.cs",
                                                             SearchOption.AllDirectories))
            {
                // The declaration file itself names every site; it is the table, not a
                // wiring site, so it is excluded by name rather than by a heuristic.
                if (Path.GetFileName(path) == "GuiMockSession.cs") continue;
                string prepared =
                    SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(path));
                foreach (var pair in ScanSuppressionSites(prepared))
                {
                    int prior;
                    counts.TryGetValue(pair.Key, out prior);
                    counts[pair.Key] = prior + pair.Value;
                }
            }
            return counts;
        }

        /// <summary>Counts <c>GuiMockSession.Suppressed(...GuiMockSession.&lt;Field&gt;)</c>
        /// call sites in prepared source, by field name. Multiline-tolerant, because the
        /// real call sites wrap.</summary>
        private static Dictionary<string, int> ScanSuppressionSites(string prepared)
        {
            var re = new Regex(
                @"GuiMockSession\.Suppressed\(\s*(?:[A-Za-z_][A-Za-z0-9_.]*\.)?"
                + @"GuiMockSession\.(\w+)\s*\)",
                RegexOptions.CultureInvariant | RegexOptions.Singleline);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Match m in re.Matches(prepared))
            {
                string field = m.Groups[1].Value;
                int prior;
                counts.TryGetValue(field, out prior);
                counts[field] = prior + 1;
            }
            return counts;
        }

        /// <summary>The declared field name for a site, read off the table by VALUE so a
        /// renamed field is a compile error here rather than a silent miss.</summary>
        private static string SiteFieldName(GuiMockSuppressionSite site)
        {
            if (site.Site == GuiMockSession.CareerVmRebuild.Site) return "CareerVmRebuild";
            if (site.Site == GuiMockSession.CareerInvalidate.Site) return "CareerInvalidate";
            if (site.Site == GuiMockSession.KerbalsInvalidate.Site) return "KerbalsInvalidate";
            if (site.Site == GuiMockSession.KerbalsLiveCrew.Site) return "KerbalsLiveCrew";
            throw new InvalidOperationException(
                "GuiMockSession declares a suppression site this gate does not name: "
                + site.Site + ". Add it here AND wire it in a window, or the set check "
                + "above is incomplete.");
        }

        private static string WindowConstantValue(string constantName)
        {
            switch (constantName)
            {
                case "KerbalsWindow": return GuiMockSession.KerbalsWindow;
                case "CareerWindow": return GuiMockSession.CareerWindow;
                case "StructureWindow": return GuiMockSession.StructureWindow;
                default:
                    throw new InvalidOperationException(
                        "install arm names an unknown window constant: " + constantName);
            }
        }

        private static string PreparedSource(string relativePath)
        {
            string path = Path.Combine(ResolveRepoRoot(), "Source", "Parsek",
                                       relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path),
                "source moved, this gate is vacuous: " + path);
            return SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(path));
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
