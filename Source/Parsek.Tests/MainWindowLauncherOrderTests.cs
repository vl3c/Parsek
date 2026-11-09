using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins the main Parsek window's content top to bottom: the Real Spawn Control launcher,
    /// Timeline, Missions, the Supply Route candidate banner (Open Logistics / Dismiss), the
    /// Logistics launcher, Kerbals, Settings, then the Close row. IMGUI draws in source order,
    /// so the order of these draw calls IS the window's layout; a reordered launcher or a
    /// banner drawn after its launcher reads as a different window.
    ///
    /// <para>Scoped to the draw method's own brace-matched body (comments stripped), so a
    /// later method naming the same text cannot satisfy it.</para>
    /// </summary>
    public class MainWindowLauncherOrderTests
    {
        // In draw order. Literals are matched with their quotes, so a tooltip or log line
        // that merely mentions a launcher name cannot stand in for the button.
        private static readonly string[] DrawOrder =
        {
            "\"Real Spawn Control ({0})\"",
            "\"Timeline\"",
            "\"Missions\"",
            "RouteRunPrompt.HasPendingPrompt",
            "\"Supply Route candidate:",
            "\"Open Logistics\"",
            "\"Dismiss\"",
            "\"Logistics\"",
            "GetKerbalsMainButtonLabel()",
            "\"Settings\"",
            "tooltipEcho.Draw()",
            "\"Close\"",
        };

        [Fact]
        public void MainWindowDrawsItsLaunchersAndBannerInOrder()
        {
            string text = MainWindowDrawText();
            int at = -1;
            string previous = "<start>";
            foreach (string token in DrawOrder)
            {
                int next = text.IndexOf(token, at + 1, StringComparison.Ordinal);
                Assert.True(next > at,
                    "main window: " + token + " must be drawn after " + previous
                    + (next < 0 ? " (not found)" : " (found earlier)"));
                at = next;
                previous = token;
            }
        }

        [Fact]
        public void EachLauncherIsDrawnOnce()
        {
            string text = MainWindowDrawText();
            foreach (string token in new[]
                     {
                         "\"Timeline\"", "\"Missions\"", "\"Open Logistics\"", "\"Logistics\"",
                         "\"Settings\"", "\"Close\"",
                     })
            {
                int first = text.IndexOf(token, StringComparison.Ordinal);
                Assert.True(first >= 0, "main window: no " + token);
                Assert.True(text.IndexOf(token, first + 1, StringComparison.Ordinal) < 0,
                    "main window: " + token + " drawn twice");
            }
        }

        // The comment-stripped text of the main window's draw body, with the Logistics
        // window's section (the candidate banner and the Logistics launcher, which the
        // Logistics window draws into the main window) read in at its call.
        internal static string MainWindowDrawText()
        {
            const string sectionCall = "logisticsUI.DrawMainWindowSection(complexity);";
            string main = MethodBodyText("ParsekUI.cs", "public void DrawWindow(int windowID)");
            int call = main.IndexOf(sectionCall, StringComparison.Ordinal);
            Assert.True(call >= 0, "ParsekUI.DrawWindow no longer draws the Logistics section");
            Assert.True(main.IndexOf(sectionCall, call + 1, StringComparison.Ordinal) < 0,
                "ParsekUI.DrawWindow draws the Logistics section twice");
            string section = MethodBodyText("UI/LogisticsWindowUI.MainWindowSection.cs",
                "public void DrawMainWindowSection(UiComplexityMode complexity)");
            return main.Substring(0, call) + section + main.Substring(call + sectionCall.Length);
        }

        // Finds the body in the comment-blanked, literal-masked text (so braces inside
        // strings and comments cannot unbalance the walk) and returns the same span of the
        // comment-blanked text, literals intact. Both forms are length-preserving.
        internal static string MethodBodyText(string file, string signature)
        {
            string raw = File.ReadAllText(SourcePath(file));
            string stripped = SourceScanText.StripCSharpComments(raw);
            string prepared = SourceScanText.MaskStringLiteralContents(stripped);
            int sig = prepared.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(sig >= 0, file + ": " + signature + " not found, this gate is vacuous");
            Assert.True(prepared.IndexOf(signature, sig + 1, StringComparison.Ordinal) < 0,
                file + ": " + signature + " declared twice");
            int open = prepared.IndexOf('{', sig);
            string block = SourceScanText.BraceMatchedBlock(prepared, open);
            return stripped.Substring(open, block.Length);
        }

        private static string SourcePath(string file)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                string candidate = Path.Combine(dir, "Source", "Parsek", file);
                if (File.Exists(candidate))
                    return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            throw new FileNotFoundException("Source/Parsek/" + file + " not found above "
                + AppDomain.CurrentDomain.BaseDirectory);
        }
    }
}
