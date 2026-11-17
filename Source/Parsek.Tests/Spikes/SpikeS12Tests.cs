using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Parsek.InGameTests.Spikes;
using Parsek.InGameTests.Spikes.S12;
using Xunit;
using Xunit.Abstractions;

namespace Parsek.Tests.Spikes
{
    /// <summary>
    /// R0 spike branch only (spike/mp-r0, never merged). Plan: r0-spikes.md, task S12.
    /// The pure half of the crypto probe, plus one smoke run of the probe list under the
    /// test host's runtime. THE SMOKE IS NOT THE ANSWER FOR KSP: it runs on the cloud's
    /// (or the PC's) own Mono / .NET, not on the Unity Mono inside KSP 1.12.5, and it
    /// skips the key-container probes. Only the in-game CryptoProbe answers S12.
    /// </summary>
    [Collection("Sequential")]
    public class SpikeS12Tests : IDisposable
    {
        private readonly ITestOutputHelper output;
        private readonly string tempRoot;

        public SpikeS12Tests(ITestOutputHelper output)
        {
            this.output = output;
            tempRoot = Path.Combine(Path.GetTempPath(), "parsek-s12-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
        }

        public void Dispose()
        {
            try { Directory.Delete(tempRoot, recursive: true); }
            catch (Exception) { }
        }

        private static SpikeS12Step Step(string name, SpikeS12Status status, double ms = 1.0)
            => new SpikeS12Step { Name = name, Status = status, Ms = ms, Detail = "d" };

        // ----- fixed inputs -----

        [Fact]
        public void FillBuffer_FirstBytes_AreThePinnedSequence()
        {
            byte[] b = SpikeS12Plan.FillBuffer(8);
            Assert.Equal("009e3cda7817b553", SpikeS12Plan.ToHex(b));
        }

        [Fact]
        public void FillBuffer_Lengths_AndNegativeRefused()
        {
            Assert.Empty(SpikeS12Plan.FillBuffer(0));
            Assert.Equal(SpikeS12Plan.TenMb, SpikeS12Plan.FillBuffer(SpikeS12Plan.TenMb).Length);
            Assert.Throws<ArgumentOutOfRangeException>(() => SpikeS12Plan.FillBuffer(-1));
        }

        [Fact]
        public void Sizes_AreThePlanSizes()
        {
            Assert.Equal(1024, SpikeS12Plan.BlobBytes);
            Assert.Equal(1048576, SpikeS12Plan.OneMb);
            Assert.Equal(10485760, SpikeS12Plan.TenMb);
            Assert.Equal(2048, SpikeS12Plan.RsaKeyBits);
            Assert.Equal(256, SpikeS12Plan.EcKeyBits);
            Assert.Equal(100.0, SpikeS12Plan.ShaPassMs);
        }

        [Theory]
        [InlineData(SpikeS12Plan.BlobBytes, SpikeS12Plan.BlobSha256)]
        [InlineData(SpikeS12Plan.OneMb, SpikeS12Plan.OneMbSha256)]
        [InlineData(SpikeS12Plan.TenMb, SpikeS12Plan.TenMbSha256)]
        public void PinnedDigests_MatchTheFilledBuffers(int length, string expected)
        {
            using (var h = SHA256.Create())
                Assert.Equal(expected, SpikeS12Plan.ToHex(h.ComputeHash(SpikeS12Plan.FillBuffer(length))));
        }

        [Fact]
        public void ToHex_LowerCase_AndNullIsEmpty()
        {
            Assert.Equal("00ff0a", SpikeS12Plan.ToHex(new byte[] { 0, 255, 10 }));
            Assert.Equal("", SpikeS12Plan.ToHex(null));
        }

        [Fact]
        public void RunLabel_IsUtcStamp_AndNamesFollowIt()
        {
            string run = SpikeS12Plan.RunLabel(new DateTime(2026, 11, 17, 9, 30, 5, DateTimeKind.Utc));
            Assert.Equal("s12-20261117T093005Z", run);
            Assert.Equal("parsek-spike-s12-s12-20261117T093005Z", SpikeS12Plan.ContainerName(run));
            Assert.Equal("rsa-public-s12-20261117T093005Z.xml", SpikeS12Plan.PublicKeyFileName(run));
            Assert.Null(SpikeCsv.ValidateSegment(SpikeS12Plan.PublicKeyFileName(run)));
        }

        [Fact]
        public void Median_OddEvenEmpty()
        {
            Assert.Equal(2.0, SpikeS12Plan.Median(new List<double> { 3, 1, 2 }));
            Assert.Equal(2.5, SpikeS12Plan.Median(new List<double> { 4, 1, 2, 3 }));
            Assert.True(double.IsNaN(SpikeS12Plan.Median(new List<double>())));
            Assert.True(double.IsNaN(SpikeS12Plan.Median(null)));
        }

        [Fact]
        public void OneLine_FlattensBreaksAndQuotes()
        {
            Assert.Equal("a  b 'c'", SpikeS12Plan.OneLine("a\r\nb \"c\"\t"));
            Assert.Equal("", SpikeS12Plan.OneLine(null));
        }

        [Fact]
        public void Describe_UnwrapsInvocationAndTypeInit()
        {
            var inner = new PlatformNotSupportedException("no ECDsa\nhere");
            var wrapped = new System.Reflection.TargetInvocationException(new TypeInitializationException("T", inner));
            SpikeS12Plan.Describe(wrapped, out string type, out string msg);
            Assert.Equal("System.PlatformNotSupportedException", type);
            Assert.StartsWith("no ECDsa here", msg);
            Assert.Contains("wrapped in TargetInvocationException", msg);

            SpikeS12Plan.Describe(new NotImplementedException("x"), out type, out msg);
            Assert.Equal("System.NotImplementedException", type);
            Assert.Equal("x", msg);
        }

        // ----- line and row -----

        [Fact]
        public void StepLine_OkStep_IsInvariantUnderGermanCulture()
        {
            var saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var s = new SpikeS12Step { Name = "sha256.10mb", Status = SpikeS12Status.Ok, Ms = 41.5, Bytes = 10485760, Detail = "digest=ab \"x\"" };
                Assert.Equal("step run=s12-r name=sha256.10mb status=ok ms=41.500 bytes=10485760 detail=\"digest=ab 'x'\"",
                    SpikeS12Plan.StepLine("s12-r", s));
                object[] row = SpikeS12Plan.StepRow("s12-r", s);
                Assert.Equal("s12-r,sha256.10mb,ok,41.5,10485760,digest=ab 'x',,", SpikeCsv.FormatRow(row));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void StepLine_FailStep_CarriesErrorTypeAndMessage()
        {
            var s = new SpikeS12Step
            {
                Name = "ecdsa.create", Status = SpikeS12Status.Fail, Ms = 0.25, Detail = "",
                ErrorType = "System.PlatformNotSupportedException", ErrorMessage = "Operation is not supported\non this platform.",
            };
            Assert.Equal("step run=r name=ecdsa.create status=fail ms=0.250 bytes=0 detail=\"\" "
                + "error=System.PlatformNotSupportedException message=\"Operation is not supported on this platform.\"",
                SpikeS12Plan.StepLine("r", s));
            Assert.Equal(SpikeS12Plan.ProbeCsvHeader.Length, SpikeS12Plan.StepRow("r", s).Length);
        }

        [Fact]
        public void StepLine_SkipStep_SaysSkip_AndNaNMsPrintsNaN()
        {
            var s = new SpikeS12Step { Name = "x", Status = SpikeS12Status.Skip, Ms = double.NaN, Detail = "skipped: why" };
            Assert.Contains(" status=skip ms=NaN ", SpikeS12Plan.StepLine("r", s));
        }

        // ----- verdict -----

        private static List<SpikeS12Step> GoodRun()
        {
            var steps = new List<SpikeS12Step>
            {
                Step(SpikeS12Plan.StepShaKat, SpikeS12Status.Ok),
                Step(SpikeS12Plan.StepSha10, SpikeS12Status.Ok, 120.0),
                Step(SpikeS12Plan.StepSha10Warm, SpikeS12Status.Ok, 42.0),
                Step(SpikeS12Plan.StepRsaCreate, SpikeS12Status.Ok),
                Step(SpikeS12Plan.StepRsaSign, SpikeS12Status.Ok),
                Step(SpikeS12Plan.StepRsaVerify, SpikeS12Status.Ok),
                Step(SpikeS12Plan.StepRsaSignLegacy, SpikeS12Status.Ok),
                Step(SpikeS12Plan.StepRsaVerifyLegacy, SpikeS12Status.Ok),
                Step(SpikeS12Plan.StepRsaTampered, SpikeS12Status.Ok),
                Step(SpikeS12Plan.StepRsaXmlImport, SpikeS12Status.Ok),
                Step(SpikeS12Plan.StepRsaParams, SpikeS12Status.Ok),
                Step(SpikeS12Plan.StepEcCreate, SpikeS12Status.Fail),
                Step(SpikeS12Plan.StepEcCreateDefault, SpikeS12Status.Fail),
                Step(SpikeS12Plan.StepEcSign, SpikeS12Status.Skip),
            };
            return steps;
        }

        [Fact]
        public void Judge_RsaOnly_Passes_AndCountsFailsAndSkips()
        {
            var v = SpikeS12Plan.Judge(GoodRun());
            Assert.True(v.ShaOk);
            Assert.True(v.RsaChain);
            Assert.False(v.EcdsaChain);
            Assert.True(v.Pass);
            Assert.Equal(2, v.Fails);
            Assert.Equal(1, v.Skips);
            Assert.Equal(120.0, v.Sha10FirstMs);
            Assert.Equal(42.0, v.Sha10WarmMs);
            Assert.Equal("not-run", v.Container);
            Assert.Equal("verdict run=r sha10mbFirstMs=120.000 sha10mbWarmMs=42.000 shaOk=true rsaChain=true "
                + "ecdsaChain=false container=not-run fails=2 skips=1 result=PASS", SpikeS12Plan.VerdictLine("r", v));
            Assert.Equal(SpikeS12Plan.VerdictCsvHeader.Length, SpikeS12Plan.VerdictRow("r", v).Length);
        }

        [Fact]
        public void Judge_WarmShaAtOrOverBudget_Fails()
        {
            var steps = GoodRun();
            SpikeS12Plan.Find(steps, SpikeS12Plan.StepSha10Warm).Ms = 100.0;
            var v = SpikeS12Plan.Judge(steps);
            Assert.False(v.ShaOk);
            Assert.False(v.Pass);
            Assert.EndsWith("result=FAIL", SpikeS12Plan.VerdictLine("r", v));
        }

        [Theory]
        [InlineData(SpikeS12Plan.StepShaKat)]
        [InlineData(SpikeS12Plan.StepSha10)]
        [InlineData(SpikeS12Plan.StepSha10Warm)]
        public void Judge_AnyShaStepFailed_ShaNotOk(string name)
        {
            var steps = GoodRun();
            SpikeS12Plan.Find(steps, name).Status = SpikeS12Status.Fail;
            Assert.False(SpikeS12Plan.Judge(steps).ShaOk);
        }

        [Fact]
        public void Judge_RsaModernMissing_LegacyStillCarriesTheChain()
        {
            var steps = GoodRun();
            SpikeS12Plan.Find(steps, SpikeS12Plan.StepRsaSign).Status = SpikeS12Status.Fail;
            SpikeS12Plan.Find(steps, SpikeS12Plan.StepRsaVerify).Status = SpikeS12Status.Skip;
            Assert.True(SpikeS12Plan.Judge(steps).RsaChain);

            SpikeS12Plan.Find(steps, SpikeS12Plan.StepRsaVerifyLegacy).Status = SpikeS12Status.Fail;
            Assert.False(SpikeS12Plan.Judge(steps).RsaChain);
        }

        [Fact]
        public void Judge_RsaNeedsOneRoundTrip_AndTheTamperRefusal()
        {
            var steps = GoodRun();
            SpikeS12Plan.Find(steps, SpikeS12Plan.StepRsaXmlImport).Status = SpikeS12Status.Fail;
            Assert.True(SpikeS12Plan.Judge(steps).RsaChain);
            SpikeS12Plan.Find(steps, SpikeS12Plan.StepRsaParams).Status = SpikeS12Status.Fail;
            Assert.False(SpikeS12Plan.Judge(steps).RsaChain);

            steps = GoodRun();
            SpikeS12Plan.Find(steps, SpikeS12Plan.StepRsaTampered).Status = SpikeS12Status.Fail;
            Assert.False(SpikeS12Plan.Judge(steps).RsaChain);
        }

        [Fact]
        public void Judge_EcdsaOnly_Passes_ViaTheDefaultCreate()
        {
            var steps = new List<SpikeS12Step>
            {
                Step(SpikeS12Plan.StepShaKat, SpikeS12Status.Ok),
                Step(SpikeS12Plan.StepSha10, SpikeS12Status.Ok),
                Step(SpikeS12Plan.StepSha10Warm, SpikeS12Status.Ok, 10),
                Step(SpikeS12Plan.StepRsaCreate, SpikeS12Status.Fail),
                Step(SpikeS12Plan.StepEcCreate, SpikeS12Status.Fail),
                Step(SpikeS12Plan.StepEcCreateDefault, SpikeS12Status.Ok),
                Step(SpikeS12Plan.StepEcSign, SpikeS12Status.Ok),
                Step(SpikeS12Plan.StepEcVerify, SpikeS12Status.Ok),
                Step(SpikeS12Plan.StepEcTampered, SpikeS12Status.Ok),
                Step(SpikeS12Plan.StepEcParams, SpikeS12Status.Ok),
            };
            var v = SpikeS12Plan.Judge(steps);
            Assert.False(v.RsaChain);
            Assert.True(v.EcdsaChain);
            Assert.True(v.Pass);

            SpikeS12Plan.Find(steps, SpikeS12Plan.StepEcParams).Status = SpikeS12Status.Fail;
            Assert.False(SpikeS12Plan.Judge(steps).Pass);
        }

        [Fact]
        public void Judge_NullOrEmpty_IsFail()
        {
            Assert.False(SpikeS12Plan.Judge(null).Pass);
            Assert.False(SpikeS12Plan.Judge(new List<SpikeS12Step>()).Pass);
        }

        [Theory]
        [InlineData(null, null, null, null, "not-run")]
        [InlineData("skip", null, null, null, "not-run")]
        [InlineData("fail", "skip", "ok", "ok", "not-writable")]
        [InlineData("ok", "ok", "ok", "ok", "writable")]
        [InlineData("ok", "ok", "ok", "fail", "left-behind")]
        [InlineData("ok", "fail", "ok", "ok", "partial")]
        [InlineData("ok", "ok", "fail", "ok", "partial")]
        public void ContainerVerdict_Table(string persist, string reopen, string delete, string leftover, string expected)
        {
            var steps = new List<SpikeS12Step>();
            void Add(string name, string st)
            {
                if (st == null) return;
                steps.Add(Step(name, st == "ok" ? SpikeS12Status.Ok : st == "skip" ? SpikeS12Status.Skip : SpikeS12Status.Fail));
            }
            Add(SpikeS12Plan.StepContainerPersist, persist);
            Add(SpikeS12Plan.StepContainerReopen, reopen);
            Add(SpikeS12Plan.StepContainerDelete, delete);
            Add(SpikeS12Plan.StepContainerLeftover, leftover);
            Assert.Equal(expected, SpikeS12Plan.ContainerVerdict(steps));
        }

        // ----- key stores -----

        [Fact]
        public void KeyStoreDirs_MonoStoresFirst_ThenCryptoApi()
        {
            string app = Path.Combine("R", "AppData");
            string common = Path.Combine("R", "ProgramData");
            var dirs = SpikeS12Plan.KeyStoreDirs(app, common);
            Assert.Equal(new[]
            {
                Path.Combine(app, ".mono", "keypairs"),
                Path.Combine(common, ".mono", "keypairs"),
                Path.Combine(app, "Microsoft", "Crypto", "RSA"),
                Path.Combine(app, "Microsoft", "Crypto", "Keys"),
            }, dirs.ToArray());
            Assert.Empty(SpikeS12Plan.KeyStoreDirs(null, ""));
            Assert.Single(SpikeS12Plan.KeyStoreDirs(null, common));
        }

        [Fact]
        public void SnapshotAndDiff_SeeAFileAppearChangeAndGo()
        {
            string store = Path.Combine(tempRoot, "store");
            string missing = Path.Combine(tempRoot, "missing");
            Directory.CreateDirectory(store);
            File.WriteAllText(Path.Combine(store, "old.xml"), "a");
            var dirs = new List<string> { store, missing };

            var before = SpikeS12Plan.Snapshot(dirs);
            Assert.Contains("dir|" + missing + "|missing", before);
            Assert.Contains("dir|" + store + "|present", before);
            Assert.True(SpikeS12Plan.Diff(before, SpikeS12Plan.Snapshot(dirs)).Empty);

            string added = Path.Combine(store, "parsek-spike-s12.xml");
            File.WriteAllText(added, "key");
            Directory.CreateDirectory(missing);
            var mid = SpikeS12Plan.Snapshot(dirs);
            var d = SpikeS12Plan.Diff(before, mid);
            Assert.Equal(new[] { added }, d.Added.ToArray());
            Assert.Equal(new[] { missing }, d.DirsCreated.ToArray());
            Assert.Empty(d.Removed);
            Assert.Contains("added=" + added, d.Describe());

            File.Delete(added);
            File.WriteAllText(Path.Combine(store, "old.xml"), "changed-length");
            var end = SpikeS12Plan.Snapshot(dirs);
            var d2 = SpikeS12Plan.Diff(before, end);
            Assert.Empty(d2.Added);
            Assert.Equal(new[] { Path.Combine(store, "old.xml") }, d2.Changed.ToArray());
            Assert.Equal(new[] { missing }, d2.DirsCreated.ToArray());

            var d3 = SpikeS12Plan.Diff(mid, end);
            Assert.Equal(new[] { added }, d3.Removed.ToArray());
            Assert.Equal("added=none removed=none changed=none dirsCreated=none",
                SpikeS12Plan.Diff(end, end).Describe());
        }

        [Fact]
        public void Snapshot_NullAndEmptyEntries_AreIgnored()
        {
            Assert.Empty(SpikeS12Plan.Snapshot(null));
            Assert.Empty(SpikeS12Plan.Snapshot(new[] { null, "" }));
        }

        // ----- the wrapper -----

        [Fact]
        public void Run_ExceptionBecomesFail_WithTypeMessageAndTime()
        {
            var s = SpikeS12Probes.Run("boom", st => throw new PlatformNotSupportedException("nope"));
            Assert.Equal(SpikeS12Status.Fail, s.Status);
            Assert.Equal("System.PlatformNotSupportedException", s.ErrorType);
            Assert.Equal("nope", s.ErrorMessage);
            Assert.False(double.IsNaN(s.Ms));
            Assert.True(s.Ms >= 0);
        }

        [Fact]
        public void Run_BodyMayReportFailOrItsOwnMs()
        {
            var ok = SpikeS12Probes.Run("ok", st => "fine");
            Assert.Equal(SpikeS12Status.Ok, ok.Status);
            Assert.Equal("fine", ok.Detail);

            var wrong = SpikeS12Probes.Run("wrong", st => { st.Status = SpikeS12Status.Fail; return "mismatch"; });
            Assert.Equal(SpikeS12Status.Fail, wrong.Status);
            Assert.Null(wrong.ErrorType);

            var own = SpikeS12Probes.Run("own", st => { st.Ms = 7.5; return ""; });
            Assert.Equal(7.5, own.Ms);
        }

        // ----- smoke: NOT the KSP answer -----

        [Fact]
        [Trait("Category", "MonoSmoke")]
        public void Smoke_HostRuntimeNotKspMono_ProbeListRunsToTheEnd()
        {
            using (var ctx = new SpikeS12Context
            {
                Run = "s12-xunit-smoke",
                OutputDir = Path.Combine(tempRoot, "out"),
                IncludeContainer = false,
                WarmRepeats = 1,
            })
            {
                var steps = SpikeS12Probes.RunAll(ctx).ToList();
                var v = SpikeS12Plan.Judge(steps);
                // Echoed to stdout too, so the console capture of a passing run keeps them.
                var lines = new List<string> { "[S12 smoke] THIS IS THE TEST HOST'S RUNTIME, NOT KSP's MONO. Not the S12 answer." };
                foreach (var s in steps) lines.Add("[S12 smoke] " + SpikeS12Plan.StepLine(ctx.Run, s));
                lines.Add("[S12 smoke] " + SpikeS12Plan.VerdictLine(ctx.Run, v));
                foreach (string l in lines)
                {
                    output.WriteLine(l);
                    Console.WriteLine(l);
                }

                // The names are unique and every step has a status and a time.
                Assert.Equal(steps.Count, steps.Select(s => s.Name).Distinct().Count());
                Assert.All(steps, s => Assert.False(double.IsNaN(s.Ms), s.Name));

                // SHA-256 is in every runtime this suite runs on: the digests must match.
                foreach (string n in new[] { "sha256.create", SpikeS12Plan.StepShaKat, "sha256.blob", "sha256.1mb", SpikeS12Plan.StepSha10, SpikeS12Plan.StepSha10Warm })
                    Assert.True(SpikeS12Plan.Find(steps, n).IsOk, SpikeS12Plan.StepLine(ctx.Run, SpikeS12Plan.Find(steps, n)));

                // The container probes are off here; nothing is written to a key store.
                Assert.Equal("not-run", v.Container);
                Assert.Equal(SpikeS12Status.Skip, SpikeS12Plan.Find(steps, SpikeS12Plan.StepContainerPersist).Status);

                // The public-key file, when RSA got that far, lands only in the output folder.
                var file = SpikeS12Plan.Find(steps, "rsa.xml.file");
                Assert.NotNull(file);
                if (file.IsOk)
                    Assert.True(File.Exists(Path.Combine(ctx.OutputDir, SpikeS12Plan.PublicKeyFileName(ctx.Run))));
            }
        }
    }
}
