using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins that the headless save output (<see cref="SaveWriterDriver"/>: the PARSEK node the
    /// production save writers build plus every file they write) is byte-identical across two
    /// writes, across thread cultures (the xUnit host runs under the OS culture, ro-RO on the
    /// owner's PC, and de-DE / ro-RO both print decimal commas) and across save folder paths.
    /// The golden generators and the save byte-identity tests compare these texts, so a
    /// non-deterministic writer (hash-set order, a culture-formatted number, a clock read, an
    /// absolute path) would read as a format change or a leak there; it reds here first.
    /// The anti-vacuity cells prove the comparison sees a one-value or one-byte change.
    /// </summary>
    [Collection("Sequential")]
    public class SaveWriterDriverDeterminismTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> tempDirs = new List<string>();
        private readonly GameScenes originalScene;
        private readonly CultureInfo originalCulture;
        private readonly CultureInfo originalUiCulture;

        public SaveWriterDriverDeterminismTests()
        {
            originalScene = HighLogic.LoadedScene;
            originalCulture = Thread.CurrentThread.CurrentCulture;
            originalUiCulture = Thread.CurrentThread.CurrentUICulture;
            HighLogic.LoadedScene = GameScenes.SPACECENTER;
            SaveWriterDriver.ResetAllStores();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            Thread.CurrentThread.CurrentCulture = originalCulture;
            Thread.CurrentThread.CurrentUICulture = originalUiCulture;
            RecordingPaths.SaveRootOverrideForTesting = null;
            SaveWriterDriver.ResetAllStores();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            HighLogic.LoadedScene = originalScene;
            foreach (string dir in tempDirs)
            {
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private string NewTempDir(string leaf)
        {
            string top = Path.Combine(Path.GetTempPath(), "parsek-save-driver-test-" + Guid.NewGuid().ToString("N"));
            tempDirs.Add(top);
            string dir = Path.Combine(top, leaf);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private (string First, string Second) RunUnder(CultureInfo culture, Action<ParsekScenario> between = null,
            string saveRoot = null)
        {
            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
            try
            {
                return SaveWriterDriver.Run(between, saveRoot);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = originalCulture;
                Thread.CurrentThread.CurrentUICulture = originalUiCulture;
            }
        }

        /// <summary>The first line where two texts differ, for a readable failure.</summary>
        private static string FirstDifference(string a, string b)
        {
            string[] la = a.Split('\n');
            string[] lb = b.Split('\n');
            for (int i = 0; i < Math.Max(la.Length, lb.Length); i++)
            {
                string x = i < la.Length ? la[i] : "<end>";
                string y = i < lb.Length ? lb[i] : "<end>";
                if (x != y)
                    return "line " + (i + 1).ToString(CultureInfo.InvariantCulture) + ": '" + x + "' vs '" + y + "'";
            }
            return "<no difference>";
        }

        private static void AssertSame(string expected, string actual, string what)
        {
            Assert.True(expected == actual, what + " differs at " + FirstDifference(expected, actual));
        }

        /// <summary>The text carries the populated state, so an equality between two texts is not vacuous.</summary>
        private static void AssertPopulated(string text)
        {
            Assert.Contains("name = ParsekScenario", text);
            Assert.Contains(SaveWriterDriver.LanderTreeId, text);
            Assert.Contains(SaveWriterDriver.RoverTreeId, text);
            Assert.Contains(SaveWriterDriver.RouteName0, text);
            Assert.Contains(SaveWriterDriver.TombstoneId, text);
            Assert.Contains(LoadReconcilePolicy.SaveStampKey + " = " + SaveWriterDriver.SaveStampMask, text);
            Assert.Contains("  Parsek/Recordings/" + SaveWriterDriver.RootId + ".prec\n", text);
            Assert.Contains("  Parsek/Recordings/" + SaveWriterDriver.RoverId + "_vessel.craft\n", text);
            Assert.Contains("  Parsek/GameState/ledger.pgld\n", text);
            Assert.Contains("  Parsek/GameState/events.pgse\n", text);
            int files = text.Split('\n').Count(l => l.Length > 66 && l[64] == ' ' && l[65] == ' ');
            Assert.True(files >= 20, "the save folder listing has only " + files + " files");
        }

        // ---------------------------------------------------------------- the pins

        [Fact]
        public void TwoWrites_EmptyHook_AreByteIdentical()
        {
            var run = RunUnder(CultureInfo.InvariantCulture);
            AssertPopulated(run.First);
            AssertSame(run.First, run.Second, "the second write of the same state");

            // A second build of the same state from empty stores writes the same bytes again.
            var again = RunUnder(CultureInfo.InvariantCulture);
            AssertSame(run.First, again.First, "a rebuilt state's write");
        }

        [Theory]
        [InlineData("de-DE")]
        [InlineData("ro-RO")]
        public void Write_UnderADecimalCommaCulture_MatchesTheInvariantWrite(string cultureName)
        {
            var culture = new CultureInfo(cultureName);
            // The culture is live: it prints a decimal comma, so a culture-formatted number in
            // any writer would show here.
            Assert.Equal("1,5", 1.5.ToString(culture));

            var invariant = RunUnder(CultureInfo.InvariantCulture);
            var local = RunUnder(culture);
            AssertPopulated(local.First);
            AssertSame(invariant.First, local.First, "the " + cultureName + " write");
            AssertSame(local.First, local.Second, "the second " + cultureName + " write");
        }

        [Fact]
        public void Write_InTwoSaveFolders_NamesNeitherPath()
        {
            string a = NewTempDir("a");
            string b = NewTempDir(Path.Combine("deeper folder", "with spaces", "b"));
            var inA = RunUnder(CultureInfo.InvariantCulture, saveRoot: a);
            var inB = RunUnder(CultureInfo.InvariantCulture, saveRoot: b);
            AssertSame(inA.First, inB.First, "the write in another save folder");
            Assert.DoesNotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), inA.First);
            Assert.DoesNotContain("\\", inA.First);
            // A given folder is the caller's: Run leaves it, with the writers' files in it.
            Assert.True(File.Exists(Path.Combine(a, "Parsek", "GameState", "ledger.pgld")));
        }

        [Fact]
        public void SaveStamp_IsFreshPerWrite_AndOnlyItIsMasked()
        {
            string root = NewTempDir("stamp");
            RecordingPaths.SaveRootOverrideForTesting = root;
            ParsekScenario scenario = SaveWriterDriver.BuildState();
            ConfigNode one = SaveWriterDriver.RunSaveWriters(scenario);
            ConfigNode two = SaveWriterDriver.RunSaveWriters(scenario);
            string s1 = one.GetValue(LoadReconcilePolicy.SaveStampKey);
            string s2 = two.GetValue(LoadReconcilePolicy.SaveStampKey);
            Assert.False(string.IsNullOrEmpty(s1));
            Assert.NotEqual(s1, s2);

            string t1 = SaveWriterDriver.WriteToText(one);
            Assert.Equal(t1, SaveWriterDriver.WriteToText(two));
            Assert.DoesNotContain(s1, t1);
            // The mask works on a copy, and the text is the node's own text apart from that value.
            Assert.Equal(s1, one.GetValue(LoadReconcilePolicy.SaveStampKey));
            Assert.Equal(one.ToString().Replace("\r\n", "\n").Replace(s1, SaveWriterDriver.SaveStampMask), t1);
        }

        // ---------------------------------------------------------------- anti-vacuity

        [Fact]
        public void Hook_ThatChangesOneSavedValue_RedsTheComparison()
        {
            var run = RunUnder(CultureInfo.InvariantCulture, between: _ => RouteStore.StateFloorUT = 451.5);
            Assert.NotEqual(run.First, run.Second);
            string diff = FirstDifference(run.First, run.Second);
            Assert.Contains("routeStateFloorUT = 450.25", diff);
            Assert.Contains("routeStateFloorUT = 451.5", diff);
        }

        [Fact]
        public void OneFlippedByte_InAWrittenFile_ChangesTheText()
        {
            string root = NewTempDir("flip");
            var run = RunUnder(CultureInfo.InvariantCulture, saveRoot: root);
            string before = SaveWriterDriver.SaveFolderText(root);
            Assert.Contains(before, run.Second);

            string ledger = Path.Combine(root, "Parsek", "GameState", "ledger.pgld");
            byte[] bytes = File.ReadAllBytes(ledger);
            bytes[bytes.Length / 2] ^= 0x01;
            File.WriteAllBytes(ledger, bytes);

            string after = SaveWriterDriver.SaveFolderText(root);
            Assert.NotEqual(before, after);
            Assert.Contains("  Parsek/GameState/ledger.pgld", FirstDifference(before, after));
        }

        [Fact]
        public void OneChangedValue_InTheNode_ChangesTheText()
        {
            var node = new ConfigNode("SCENARIO");
            node.AddValue("name", "ParsekScenario");
            node.AddValue("x", "1.5");
            string before = SaveWriterDriver.WriteToText(node);
            node.SetValue("x", "1,5");
            Assert.NotEqual(before, SaveWriterDriver.WriteToText(node));
        }
    }
}
