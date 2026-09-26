using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The automation-only crash-after-temp hook in <see cref="FileIOUtils"/> (D16
    /// <c>safe-write</c>) and the pure half of the <c>SafeWriteCrash</c> seam verb. The hook
    /// must be inert unless the seam armed it, fire once on a matching path only, leave the
    /// previous destination byte-identical and the temp file on disk, and survive the one
    /// in-process cleanup (<see cref="SidecarFileCommitBatch.StageWrite"/>) a real crash
    /// would never run.
    /// </summary>
    [Collection("Sequential")]
    public class SafeWriteCrashHookTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public SafeWriteCrashHookTests()
        {
            FileIOUtils.ResetCrashAfterTempForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            tempDir = Path.Combine(Path.GetTempPath(), "parsek_swcrash_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            FileIOUtils.ResetCrashAfterTempForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }

        private string Seed(string name, string content)
        {
            string path = Path.Combine(tempDir, name);
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
            return path;
        }

        private static string Read(string path) => Encoding.UTF8.GetString(File.ReadAllBytes(path));

        [Fact]
        public void Disarmed_WritesNormally_AndNeverFires()
        {
            string path = Seed("abc.prec", "old");

            FileIOUtils.SafeWriteBytes(Encoding.UTF8.GetBytes("new"), path, "T");

            Assert.Equal("new", Read(path));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal(0, FileIOUtils.CrashAfterTempFiredCount);
            Assert.DoesNotContain(logLines, l => l.Contains("crash-after-temp fired"));
        }

        [Fact]
        public void ArmWithoutSeam_IsRefusedAndInert()
        {
            string path = Seed("abc.prec", "old");

            Assert.False(FileIOUtils.ArmCrashAfterTemp("abc.prec", seamArmed: false));
            Assert.False(FileIOUtils.IsCrashAfterTempArmed);
            FileIOUtils.SafeWriteBytes(Encoding.UTF8.GetBytes("new"), path, "T");

            Assert.Equal("new", Read(path));
            Assert.Contains(logLines, l => l.Contains("[SafeWrite]")
                && l.Contains("crash-after-temp arm refused seamArmed=False"));
        }

        [Fact]
        public void Armed_MatchingBytesWrite_ThrowsAfterTemp_LeavesPreviousAndTemp()
        {
            string path = Seed("abc.prec", "old");
            Assert.True(FileIOUtils.ArmCrashAfterTemp("abc.prec", seamArmed: true));

            Assert.Throws<FileIOUtils.SafeWriteInjectedCrashException>(
                () => FileIOUtils.SafeWriteBytes(Encoding.UTF8.GetBytes("new!"), path, "T"));

            Assert.Equal("old", Read(path));
            Assert.Equal("new!", Read(path + ".tmp"));
            Assert.Equal(1, FileIOUtils.CrashAfterTempFiredCount);
            Assert.Contains(logLines, l => l.Contains("[SafeWrite]")
                && l.Contains("crash-after-temp fired phase=after-temp")
                && l.Contains("path='" + path + "'")
                && l.Contains("tmpBytes=4 destExists=True destUntouched=True pattern=abc.prec"));
        }

        [Fact]
        public void Armed_FiresOnce_ThenNextWriteSwapsNormally()
        {
            string path = Seed("abc.prec", "old");
            FileIOUtils.ArmCrashAfterTemp("abc.prec", seamArmed: true);
            Assert.ThrowsAny<IOException>(
                () => FileIOUtils.SafeWriteBytes(Encoding.UTF8.GetBytes("one"), path, "T"));

            Assert.False(FileIOUtils.IsCrashAfterTempArmed);
            FileIOUtils.SafeWriteBytes(Encoding.UTF8.GetBytes("two"), path, "T");

            Assert.Equal("two", Read(path));
            Assert.Equal(1, FileIOUtils.CrashAfterTempFiredCount);
        }

        [Fact]
        public void Armed_NonMatchingPath_IsUntouchedAndStaysArmed()
        {
            string other = Seed("xyz.prec", "old");
            string target = Seed("abc.prec", "old");
            FileIOUtils.ArmCrashAfterTemp("abc.prec", seamArmed: true);

            FileIOUtils.SafeWriteBytes(Encoding.UTF8.GetBytes("new"), other, "T");
            Assert.Equal("new", Read(other));
            Assert.True(FileIOUtils.IsCrashAfterTempArmed);

            Assert.ThrowsAny<IOException>(
                () => FileIOUtils.SafeWriteBytes(Encoding.UTF8.GetBytes("new"), target, "T"));
            Assert.Equal("old", Read(target));
        }

        [Fact]
        public void Armed_ConfigNodeWrite_AlsoFires_DestinationUntouched()
        {
            string path = Path.Combine(tempDir, "ledger.pgld");
            var first = new ConfigNode("ROOT");
            first.AddValue("v", "1");
            FileIOUtils.SafeWriteConfigNode(first, path, "T");
            string before = Read(path);
            FileIOUtils.ArmCrashAfterTemp("ledger.pgld", seamArmed: true);

            var second = new ConfigNode("ROOT");
            second.AddValue("v", "2");
            Assert.Throws<FileIOUtils.SafeWriteInjectedCrashException>(
                () => FileIOUtils.SafeWriteConfigNode(second, path, "T"));

            Assert.Equal(before, Read(path));
            Assert.True(File.Exists(path + ".tmp"));
        }

        [Fact]
        public void StageWrite_LetsTheInjectedCrashResidueSurvive()
        {
            string final = Seed("abc.prec", "old");
            FileIOUtils.ArmCrashAfterTemp("abc.prec", seamArmed: true);

            Assert.Throws<FileIOUtils.SafeWriteInjectedCrashException>(() =>
                SidecarFileCommitBatch.StageWrite(
                    p => FileIOUtils.SafeWriteBytes(Encoding.UTF8.GetBytes("new"), p, "T"), final));

            string[] names = Directory.GetFiles(tempDir).Select(Path.GetFileName).ToArray();
            Assert.Equal("old", Read(final));
            Assert.Single(names, n => n.StartsWith("abc.prec.stage.") && n.EndsWith(".tmp"));
            Assert.Equal(1, TestCommandSafeWriteCrash.CountResidue(names, "abc"));
        }

        [Fact]
        public void StageWrite_OrdinaryFailure_StillCleansItsStagedFiles()
        {
            string final = Seed("abc.prec", "old");

            Assert.Throws<InvalidOperationException>(() =>
                SidecarFileCommitBatch.StageWrite(p =>
                {
                    File.WriteAllText(p + ".tmp", "partial");
                    throw new InvalidOperationException("boom");
                }, final));

            string[] names = Directory.GetFiles(tempDir).Select(Path.GetFileName).ToArray();
            Assert.Equal(new[] { "abc.prec" }, names);
        }

        [Theory]
        [InlineData(null, "a/abc.prec", false)]
        [InlineData("", "a/abc.prec", false)]
        [InlineData("abc.prec", null, false)]
        [InlineData("abc.prec", "a/xyz.prec", false)]
        [InlineData("abc.prec", "a/abc.prec", true)]
        [InlineData("abc.prec", "a/ABC.prec.stage.123", true)]
        public void ShouldCrashAfterTemp_Table(string pattern, string path, bool expected)
        {
            Assert.Equal(expected, FileIOUtils.ShouldCrashAfterTemp(pattern, path));
        }

        [Fact]
        public void IsDestUntouched_ComparesExistenceLengthAndWriteTime()
        {
            var a = new FileIOUtils.DestState { Exists = true, Length = 5, WriteTicks = 10 };
            Assert.True(FileIOUtils.IsDestUntouched(a, a));
            Assert.False(FileIOUtils.IsDestUntouched(a, new FileIOUtils.DestState { Exists = true, Length = 6, WriteTicks = 10 }));
            Assert.False(FileIOUtils.IsDestUntouched(a, new FileIOUtils.DestState { Exists = true, Length = 5, WriteTicks = 11 }));
            Assert.False(FileIOUtils.IsDestUntouched(a, default(FileIOUtils.DestState)));
            Assert.True(FileIOUtils.IsDestUntouched(default(FileIOUtils.DestState), default(FileIOUtils.DestState)));
        }

        // ------------------------------------------------ TestCommandSafeWriteCrash (pure)

        [Theory]
        [InlineData(null, "safewritecrash-phase-arg-missing")]
        [InlineData("", "safewritecrash-phase-arg-missing")]
        [InlineData("Arm", "safewritecrash-phase-arg-invalid")]
        [InlineData("fire", "safewritecrash-phase-arg-invalid")]
        [InlineData("arm", null)]
        [InlineData("probe", null)]
        [InlineData("coldreload", null)]
        public void ValidateOp_Table(string op, string expected)
        {
            Assert.Equal(expected, TestCommandSafeWriteCrash.ValidatePhase(op));
        }

        [Fact]
        public void CountResidue_CountsOnlyThatRecordingsTransientTrajectoryArtifacts()
        {
            var names = new[]
            {
                "abc.prec", "abc.prec.txt", "abc_vessel.craft",
                "abc.prec.tmp", "abc.prec.stage.0f.tmp", "abc.prec.bak.1a",
                "abc_vessel.craft.tmp", "xyz.prec.stage.2b.tmp", null,
            };
            Assert.Equal(3, TestCommandSafeWriteCrash.CountResidue(names, "abc"));
            Assert.Equal(0, TestCommandSafeWriteCrash.CountResidue(null, "abc"));
            Assert.Equal(0, TestCommandSafeWriteCrash.CountResidue(names, ""));
        }

        [Fact]
        public void Pattern_ShortHex_AndLines_AreStable()
        {
            Assert.Equal("abc.prec", TestCommandSafeWriteCrash.BuildPattern("abc"));
            Assert.Equal("none", TestCommandSafeWriteCrash.ShortHex(null));
            Assert.Equal("00ff10ab01020304",
                TestCommandSafeWriteCrash.ShortHex(new byte[] { 0, 255, 16, 171, 1, 2, 3, 4, 9, 9 }));
            Assert.Equal(
                "safewritecrash armed recording=abc pattern=abc.prec baselineDigest=00ff baselinePoints=1234",
                TestCommandSafeWriteCrash.FormatArmLine("abc", "abc.prec", "00ff", 1234));
            Assert.Equal(
                "safewritecrash probe recording=abc fired=1 armed=false digest=00ff unchanged=true " +
                "residue=0 loaded=true loadFailed=false points=1234 baselinePoints=1234",
                TestCommandSafeWriteCrash.FormatProbeLine("abc", 1, false, "00ff", true, 0, 1234, 1234, true, false));
        }
    }
}
