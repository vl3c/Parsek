using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The UI-scale layer (todo S10, owner ruling 2026-11-04): every Parsek IMGUI window
    /// follows KSP's UI Scale slider. The arithmetic and decisions are pure and pinned
    /// here; the GUI.matrix application itself needs a live IMGUI pass and is covered by
    /// the in-game <c>UiScale</c> category. The two source gates keep every window on
    /// the one scaled entry point, so a new window cannot silently ignore the setting.
    /// </summary>
    [Collection("Sequential")]
    public class ParsekUiScaleTests : IDisposable
    {
        public ParsekUiScaleTests()
        {
            ParsekUiScale.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekUiScale.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        [Theory]
        [InlineData(1f, 1f)]
        [InlineData(1.0004f, 1f)]
        [InlineData(0.9995f, 1f)]
        [InlineData(1.5f, 1.5f)]
        [InlineData(0.8f, 0.8f)]
        [InlineData(2f, 2f)]
        [InlineData(0.1f, ParsekUiScale.MinScale)]
        [InlineData(9f, ParsekUiScale.MaxScale)]
        [InlineData(0f, 1f)]
        [InlineData(-2f, 1f)]
        [InlineData(float.NaN, 1f)]
        [InlineData(float.PositiveInfinity, 1f)]
        public void ResolveScale_ClampsSnapsAndRejectsGarbage(float raw, float expected)
        {
            Assert.Equal(expected, ParsekUiScale.ResolveScale(raw));
        }

        [Fact]
        public void ToScaled_DividesByTheScale_AndIgnoresANonPositiveScale()
        {
            Assert.Equal(853.3333f, ParsekUiScale.ToScaled(1280f, 1.5f), 0.001f);
            Assert.Equal(1280f, ParsekUiScale.ToScaled(1280f, 1f));
            Assert.Equal(1280f, ParsekUiScale.ToScaled(1280f, 0f));
        }

        [Fact]
        public void InputMouseToScaledRoot_FlipsYAndDividesByTheScale()
        {
            // Input.mousePosition is y-up physical pixels: (730, 259) on a 720-high screen
            // is GUI-space (730, 461), which is (486.7, 307.3) at 1.5x.
            Vector2 root = ParsekUiScale.InputMouseToScaledRoot(new Vector2(730f, 259f), 720f, 1.5f);
            Assert.Equal(486.667f, root.x, 0.01f);
            Assert.Equal(307.333f, root.y, 0.01f);
            Vector2 same = ParsekUiScale.InputMouseToScaledRoot(new Vector2(730f, 259f), 720f, 1f);
            Assert.Equal(730f, same.x, 0.001f);
            Assert.Equal(461f, same.y, 0.001f);
        }

        [Fact]
        public void UnscalePoint_MapsAPhysicalPointBackToScaledRoot()
        {
            // A window drawn at scaled-root (100, 60) under a 1.5 matrix sits at physical
            // (150, 90); a physical reading (the OS pointer) maps back to (100, 60).
            Vector2 root = ParsekUiScale.UnscalePoint(new Vector2(150f, 90f), 1.5f);
            Assert.Equal(100f, root.x, 0.001f);
            Assert.Equal(60f, root.y, 0.001f);
            Vector2 same = ParsekUiScale.UnscalePoint(new Vector2(150f, 90f), 1f);
            Assert.Equal(150f, same.x);
            Assert.Equal(90f, same.y);
        }

        [Fact]
        public void PullOntoScreen_LeavesAnOnScreenWindowAlone()
        {
            var r = new Rect(100f, 50f, 300f, 200f);
            Assert.Equal(r, ParsekUiScale.PullOntoScreen(r, 853f, 480f));
        }

        [Fact]
        public void PullOntoScreen_MovesAWindowParkedNearTheRightEdgeBackIn()
        {
            // Parked at x=1000 on a 1280 screen at 100%, then the scale goes to 150%: the
            // screen is now 853 scaled units wide and the window would be out of reach.
            Rect pulled = ParsekUiScale.PullOntoScreen(new Rect(1000f, 600f, 250f, 120f),
                853.333f, 480f);
            Assert.Equal(603.333f, pulled.x, 0.001f);
            Assert.Equal(360f, pulled.y, 0.001f);
            Assert.Equal(250f, pulled.width);
            Assert.Equal(120f, pulled.height);
        }

        [Fact]
        public void PullOntoScreen_AWindowLargerThanTheScreenGoesToTheTopLeftEdge()
        {
            Rect pulled = ParsekUiScale.PullOntoScreen(new Rect(300f, 200f, 1355f, 900f),
                853f, 480f);
            Assert.Equal(0f, pulled.x);
            Assert.Equal(0f, pulled.y);
            Assert.Equal(1355f, pulled.width);
        }

        [Fact]
        public void PullOntoScreen_NegativePositionsComeBackToZero()
        {
            Rect pulled = ParsekUiScale.PullOntoScreen(new Rect(-40f, -10f, 200f, 100f),
                853f, 480f);
            Assert.Equal(0f, pulled.x);
            Assert.Equal(0f, pulled.y);
        }

        [Fact]
        public void PullOntoScreen_AnUnknownScreenSizeChangesNothing()
        {
            var r = new Rect(5000f, 5000f, 10f, 10f);
            Assert.Equal(r, ParsekUiScale.PullOntoScreen(r, 0f, 480f));
            Assert.Equal(r, ParsekUiScale.PullOntoScreen(r, 853f, 0f));
        }

        [Theory]
        // held, over, rightHeld -> action
        [InlineData(false, true, false, "Set")]
        [InlineData(true, true, false, "Keep")]
        [InlineData(false, true, true, "Keep")]
        [InlineData(true, true, true, "Keep")]
        [InlineData(true, false, false, "Release")]
        [InlineData(true, false, true, "Release")]
        [InlineData(false, false, false, "Keep")]
        [InlineData(false, false, true, "Keep")]
        public void DecideLock_MirrorsClickThroughBlockersFocusFollowsMouseRule(
            bool held, bool over, bool rightHeld, string expected)
        {
            Assert.Equal(expected, ParsekUiScale.DecideLock(held, over, rightHeld).ToString());
        }

        [Theory]
        [InlineData(true, true, true)]
        [InlineData(true, false, false)]
        [InlineData(false, false, false)]
        public void TheLockHitTestRunsOnRepaintOnly(bool hasEvent, bool repaint, bool expected)
        {
            // A Layout pass returns the handed rect, zero-height for a content-sized
            // window, so testing the lock there would flap it every frame.
            Assert.Equal(expected, ParsekUiScale.ShouldEvaluateLock(hasEvent, repaint));
        }

        [Theory]
        [InlineData(100, 100, false)]
        [InlineData(99, 100, false)]
        [InlineData(98, 100, true)]
        [InlineData(0, 100, true)]
        public void IsLockStale_AfterMoreThanOneUndrawnFrame(int lastDraw, int now, bool stale)
        {
            Assert.Equal(stale, ParsekUiScale.IsLockStale(lastDraw, now));
        }

        [Fact]
        public void ScaledLockId_IsPrefixedAndInvariant()
        {
            Assert.Equal("ParsekScaledWindow_-42", ParsekUiScale.ScaledLockId(-42));
            Assert.Equal("ParsekScaledWindow_12345", ParsekUiScale.ScaledLockId(12345));
        }

        [Fact]
        public void FormatScaleChangeLog_IsInvariantAndNamesTheHost()
        {
            var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");
                string line = ParsekUiScale.FormatScaleChangeLog(1f, 1.5f, 1.5f, "Flight");
                Assert.Contains("UI scale changed 1 -> 1.5", line);
                Assert.Contains("GameSettings.UI_SCALE=1.5", line);
                Assert.Contains("host=Flight", line);
                Assert.DoesNotContain("test override", line);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = prev;
            }
        }

        [Fact]
        public void TheTestOverrideWinsOverTheStockSetting_AndIsNamedInTheLog()
        {
            ParsekUiScale.OverrideForTesting = 1.25f;
            Assert.Equal(1.25f, ParsekUiScale.CurrentSettingScale());
            Assert.Equal(1.25f, ParsekUiScale.ReadRawSetting());
            Assert.Contains("test override",
                ParsekUiScale.FormatScaleChangeLog(1f, 1.25f, 1.25f, "Probe"));
            ParsekUiScale.OverrideForTesting = 40f;
            Assert.Equal(ParsekUiScale.MaxScale, ParsekUiScale.CurrentSettingScale());
        }

        [Fact]
        public void ReadingTheStockSettingHeadlessNeverThrows()
        {
            float raw = ParsekUiScale.ReadRawSetting();
            Assert.False(float.IsNaN(raw));
            float s = ParsekUiScale.CurrentSettingScale();
            Assert.InRange(s, ParsekUiScale.MinScale, ParsekUiScale.MaxScale);
        }

        [Fact]
        public void BeginHostAtScaleOne_LeavesTheActiveScaleAtOneAndLogsNothing()
        {
            var lines = new List<string>();
            ParsekLog.TestSinkForTesting = l => lines.Add(l);
            using (ParsekUiScale.BeginHost("Probe", 1f))
                Assert.Equal(1f, ParsekUiScale.ActiveScale);
            Assert.Equal(1f, ParsekUiScale.ActiveScale);
            Assert.DoesNotContain(lines, l => l.Contains("[UIScale]"));
        }

        [Fact]
        public void TheCurrencyTooltipFollowsTheScale()
        {
            // Pointer at physical (300, 600 up) on a 1280x720 screen = GUI (300, 120).
            Rect at1 = CurrencyReservationOverlay.PlaceTooltip(new Vector2(300f, 600f),
                1280f, 720f, 280f, 60f, 1f);
            Assert.Equal(316f, at1.x, 0.001f);
            Assert.Equal(136f, at1.y, 0.001f);

            // At 150% the same pointer is scaled-root (200, 80); the box sits 16 units
            // below-right of it, and 280 x 60 units is 420 x 90 physical pixels.
            Rect at15 = CurrencyReservationOverlay.PlaceTooltip(new Vector2(300f, 600f),
                1280f, 720f, 280f, 60f, 1.5f);
            Assert.Equal(216f, at15.x, 0.001f);
            Assert.Equal(96f, at15.y, 0.001f);
            Assert.Equal(280f, at15.width);
        }

        [Fact]
        public void TheCurrencyTooltipStaysInsideTheScaledScreen()
        {
            // Pointer near the bottom-right corner at 150%: screen is 853.33 x 480 units.
            Rect r = CurrencyReservationOverlay.PlaceTooltip(new Vector2(1270f, 5f),
                1280f, 720f, 280f, 60f, 1.5f);
            Assert.Equal(853.333f - 280f - 8f, r.x, 0.01f);
            Assert.Equal(480f - 60f - 8f, r.y, 0.01f);
        }

        // ----- source gates -----

        [Fact]
        public void EveryParsekWindowDrawsThroughTheScaledEntryPoint()
        {
            // ClickThroughBlocker's hit test reads the raw mouse against the returned
            // rect, which is wrong under a UI scale; ParsekUiScale.Window owns the only
            // direct call so the scaled path can take over.
            var offenders = ScanProduction(@"\bClickThruBlocker\s*\.\s*GUILayoutWindow\s*\(")
                .Where(f => f != "UI/ParsekUiScale.cs").ToList();
            Assert.True(offenders.Count == 0,
                "draw Parsek windows with ParsekUiScale.Window, not ClickThruBlocker directly: "
                + string.Join(", ", offenders));
        }

        [Fact]
        public void PlainGuiLayoutWindowsAreOnlyTheKnownScopedOnes()
        {
            // ParsekUiScale.Window (its scaled path) and the global test runner, which
            // opens its own host scope. In-game test probes are not production windows.
            var allowed = new HashSet<string> { "UI/ParsekUiScale.cs", "InGameTests/TestRunnerShortcut.cs" };
            var offenders = ScanProduction(@"\bGUILayout\s*\.\s*Window\s*\(")
                .Where(f => !allowed.Contains(f) && !IsInGameTestProbe(f)).ToList();
            Assert.True(offenders.Count == 0,
                "a GUILayout.Window outside a UI-scale host scope ignores KSP's UI Scale: "
                + string.Join(", ", offenders));
        }

        [Fact]
        public void PopupAnchorsUseTheScaledRootConversion()
        {
            // One home for the anchor contract: GUIToScreenPoint ignores GUI.matrix (the
            // in-game UiScale geometry cell measures it), so the seam is the plain call, and a
            // future Unity that changes that is fixed in one place.
            var offenders = ScanProduction(
                    @"GUIUtility\s*\.\s*GUIToScreenPoint\s*\(\s*Event\s*\.\s*current\s*\.\s*mousePosition\s*\)")
                .Where(f => f != "UI/ParsekUiScale.cs").ToList();
            Assert.True(offenders.Count == 0,
                "anchor popups with ParsekUiScale.GuiPointToScaledRoot: " + string.Join(", ", offenders));
        }

        [Theory]
        [InlineData("ParsekFlight.cs", "BeginHost(\"Flight\")")]
        [InlineData("ParsekKSC.cs", "BeginHost(\"KSC\")")]
        [InlineData("InGameTests/TestRunnerShortcut.cs", "BeginHost(\"TestRunnerShortcut\")")]
        [InlineData("CurrencyReservationOverlay.cs", "BeginHost(\"CurrencyOverlay\")")]
        [InlineData("ParsekFlight.cs", "BeginHost(\"WatchOverlay\")")]
        public void EachDrawHostOpensAScaleScope(string file, string needle)
        {
            string raw = ParsekFlightSourceFiles.ReadText(Path.Combine(SourceRoot(), file));
            Assert.Contains("ParsekUiScale." + needle, raw);
        }

        [Theory]
        [InlineData("ParsekFlight.cs")]
        [InlineData("ParsekKSC.cs")]
        public void WindowHostsSweepStaleScaledLocksBeforeTheirEarlyReturns(string file)
        {
            string src = SourceScanText.StripCommentsAndMaskLiterals(
                ParsekFlightSourceFiles.ReadText(Path.Combine(SourceRoot(), file)));
            int onGui = src.IndexOf("void OnGUI()", StringComparison.Ordinal);
            Assert.True(onGui >= 0, file + ": no OnGUI");
            int sweep = src.IndexOf("ParsekUiScale.SweepStaleLocks()", onGui, StringComparison.Ordinal);
            int pause = src.IndexOf("PauseMenuGate.IsPauseMenuOpen()", onGui, StringComparison.Ordinal);
            Assert.True(sweep > onGui && pause > sweep,
                file + ": the sweep must run before the pause-menu early return");
            Assert.Contains("ParsekUiScale.ReleaseAllScaledLocks(", src);
        }

        private static bool IsInGameTestProbe(string relative)
        {
            return relative.StartsWith("InGameTests/", StringComparison.Ordinal)
                && relative.EndsWith("Test.cs", StringComparison.Ordinal);
        }

        private static List<string> ScanProduction(string pattern)
        {
            string root = SourceRoot();
            var rx = new Regex(pattern);
            var hits = new List<string>();
            foreach (string path in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string rel = path.Substring(root.Length + 1).Replace('\\', '/');
                if (rel.StartsWith("obj/", StringComparison.Ordinal)
                    || rel.StartsWith("bin/", StringComparison.Ordinal))
                    continue;
                string src = SourceScanText.StripCommentsAndMaskLiterals(ParsekFlightSourceFiles.ReadText(path));
                if (rx.IsMatch(src)) hits.Add(rel);
            }
            Assert.True(Directory.Exists(root), "source root moved, this gate is vacuous: " + root);
            return hits;
        }

        private static string SourceRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                string candidate = Path.Combine(dir, "Source", "Parsek");
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "Parsek.csproj")))
                    return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
        }
    }
}
