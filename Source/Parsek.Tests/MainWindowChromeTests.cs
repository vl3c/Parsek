using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The MAIN "Parsek" window's chrome: its title is drawn bold and a little larger
    /// than every other Parsek window's, and the flight form carries no recorder status
    /// block (Parsek records everything, so a State / Recorded Points / Active Ghosts
    /// readout has nothing left to tell the player). The size rule is pure; the two
    /// wiring facts are read off the source because they only exist inside an OnGUI
    /// callback a headless host cannot run.
    /// </summary>
    public class MainWindowChromeTests
    {
        [Theory]
        [InlineData(14, 16)]
        [InlineData(12, 14)]
        [InlineData(0, 16)]
        [InlineData(-1, 16)]
        public void TheMainWindowTitleIsTwoPixelsLargerThanTheSharedWindowTitle(
            int opaqueTitleSize, int expected)
        {
            Assert.Equal(expected, ParsekUI.ResolveMainWindowTitleFontSize(opaqueTitleSize));
        }

        [Theory]
        [InlineData("ParsekFlight.cs")]
        [InlineData("ParsekKSC.cs")]
        public void BothMainWindowHostsDrawWithTheMainWindowStyle(string file)
        {
            string src = ReadPreparedSource(file);
            Assert.Contains("ui.GetMainWindowStyle()", src);
            // The shared opaque style is for the SUB-windows; a host drawing the main
            // window with it would silently lose the bold title.
            Assert.DoesNotContain("ui.GetOpaqueWindowStyle()", src);
        }

        [Fact]
        public void TheFlightStatusBlockIsGone()
        {
            string src = ReadPreparedSource("ParsekUI.cs");
            Assert.DoesNotContain("DrawFlightStatus", src);
            Assert.DoesNotContain("GetStatusText", src);
        }

        private static string ReadPreparedSource(string relativePath)
        {
            string path = Path.Combine(ResolveRepoRoot(), "Source", "Parsek", relativePath);
            Assert.True(File.Exists(path),
                "source file moved, this gate is vacuous: " + path);
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
                "Could not locate repo root from " + AppContext.BaseDirectory);
        }
    }
}
