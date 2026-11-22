using System;
using System.IO;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The host-integrity guard of the part parity sweep (<see cref="PartParityHostIntegrity"/>,
    /// design-part-ghost-parity.md 4.1 and 5.3): the damage decision, its detail and Warn
    /// line, the two files that carry it across steps, the case-vessel-lost rule, and the
    /// runner's wiring of each check.
    /// </summary>
    public class PartParityHostIntegrityTests
    {
        private static PartParityHostBaseline Baseline()
            => new PartParityHostBaseline { Pid = 4242, PartCount = 13, Situation = "ORBITING" };

        private static PartParityHostObservation Intact()
            => new PartParityHostObservation { Present = true, Active = true, PartCount = 13, Orbiting = true, Situation = "ORBITING" };

        // ----- the decision -----

        [Fact]
        public void Decide_AnIntactHostIsNotDamaged()
        {
            Assert.Null(PartParityHostIntegrity.Decide(Baseline(), Intact()));
            // A gain is not damage (nothing docks onto the host, but a count up proves no loss).
            var more = Intact();
            more.PartCount = 14;
            Assert.Null(PartParityHostIntegrity.Decide(Baseline(), more));
        }

        [Fact]
        public void Decide_NothingToCompareDecidesNothing()
        {
            Assert.Null(PartParityHostIntegrity.Decide(null, Intact()));
            Assert.Null(PartParityHostIntegrity.Decide(Baseline(), null));
        }

        [Fact]
        public void Decide_GoneThenPartsThenSituation()
        {
            var gone = Intact();
            gone.Present = false;
            gone.PartCount = 0;
            gone.Orbiting = false;
            Assert.Equal(PartParityHostIntegrity.DamageGone, PartParityHostIntegrity.Decide(Baseline(), gone));

            var notActive = Intact();
            notActive.Active = false;
            Assert.Equal(PartParityHostIntegrity.DamageGone, PartParityHostIntegrity.Decide(Baseline(), notActive));

            var broke = Intact();
            broke.PartCount = 9;
            broke.Orbiting = false; // parts outranks situation
            Assert.Equal(PartParityHostIntegrity.DamageParts, PartParityHostIntegrity.Decide(Baseline(), broke));

            var deorbited = Intact();
            deorbited.Orbiting = false;
            deorbited.Situation = "SUB_ORBITAL";
            Assert.Equal(PartParityHostIntegrity.DamageSituation, PartParityHostIntegrity.Decide(Baseline(), deorbited));

            Assert.Equal(new[] { "gone", "parts", "situation" }, PartParityHostIntegrity.DamageReasons);
        }

        [Fact]
        public void Describe_NamesWhatChangedAndWhere()
        {
            var broke = Intact();
            broke.PartCount = 9;
            Assert.Equal("pid=4242 parts 13->9 at before wave 3",
                PartParityHostIntegrity.Describe(PartParityHostIntegrity.DamageParts, Baseline(), broke, "before wave 3"));
            var deorbited = Intact();
            deorbited.Situation = "SUB_ORBITAL";
            Assert.Equal("pid=4242 situation ORBITING->SUB_ORBITAL",
                PartParityHostIntegrity.Describe(PartParityHostIntegrity.DamageSituation, Baseline(), deorbited, null));
            var gone = Intact();
            gone.Present = false;
            Assert.Equal("pid=4242 no live vessel at spawn wait",
                PartParityHostIntegrity.Describe(PartParityHostIntegrity.DamageGone, Baseline(), gone, "spawn wait"));
            var notActive = Intact();
            notActive.Active = false;
            Assert.Equal("pid=4242 no longer the active vessel",
                PartParityHostIntegrity.Describe(PartParityHostIntegrity.DamageGone, Baseline(), notActive, ""));
        }

        [Fact]
        public void DamageLine_IsOneLineNamingTheReasonAndTheUnrunCases()
        {
            var d = new PartParityHostDamage { Reason = "parts", Detail = "pid=1 parts 13->9" };
            Assert.Equal("PartParity host damaged label=pp1 reason=parts detail=pid=1 parts 13->9: the sweep stops; "
                + "125 case(s) left unrun and every later step of this label stops at once",
                PartParityHostIntegrity.DamageLine("pp1", d, 125));
            Assert.StartsWith(PartParityHostIntegrity.DamageLineLiteral, PartParityHostIntegrity.DamageLine(null, null, 0));
        }

        [Fact]
        public void PayloadToken_IsTheReasonOrNone()
        {
            Assert.Equal("none", PartParityHostIntegrity.PayloadToken(null));
            Assert.Equal("none", PartParityHostIntegrity.PayloadToken(new PartParityHostDamage()));
            Assert.Equal("gone", PartParityHostIntegrity.PayloadToken(new PartParityHostDamage { Reason = "gone" }));
        }

        // ----- files -----

        [Fact]
        public void Baseline_RoundTripsAndRefusesATornFile()
        {
            string text = PartParityHostIntegrity.FormatBaseline(Baseline());
            Assert.Equal("pid=4242\nparts=13\nsituation=ORBITING\n", text);
            PartParityHostBaseline back = PartParityHostIntegrity.ParseBaseline(text.Replace("\n", "\r\n"));
            Assert.Equal(4242u, back.Pid);
            Assert.Equal(13, back.PartCount);
            Assert.Equal("ORBITING", back.Situation);
            Assert.Null(PartParityHostIntegrity.ParseBaseline(null));
            Assert.Null(PartParityHostIntegrity.ParseBaseline("pid=4242\n"));
            Assert.Null(PartParityHostIntegrity.ParseBaseline("pid=-1\nparts=3\n"));
            Assert.Null(PartParityHostIntegrity.ParseBaseline("pid=x\nparts=3\n"));
            Assert.Equal(string.Empty, PartParityHostIntegrity.FormatBaseline(null));
        }

        [Fact]
        public void Damage_RoundTripsAndATornRecordStillStops()
        {
            var d = new PartParityHostDamage { Reason = "situation", Detail = "pid=1 situation ORBITING->SUB_ORBITAL\nx" };
            string text = PartParityHostIntegrity.FormatDamage(d);
            Assert.Equal("reason=situation\ndetail=pid=1 situation ORBITING->SUB_ORBITAL x\n", text);
            PartParityHostDamage back = PartParityHostIntegrity.ParseDamage(text);
            Assert.Equal("situation", back.Reason);
            Assert.Equal("pid=1 situation ORBITING->SUB_ORBITAL x", back.Detail);

            Assert.Null(PartParityHostIntegrity.ParseDamage(null));
            Assert.Null(PartParityHostIntegrity.ParseDamage("  \n"));
            PartParityHostDamage torn = PartParityHostIntegrity.ParseDamage("reas");
            Assert.Equal(PartParityHostIntegrity.DamageGone, torn.Reason);
            Assert.Contains("unreadable", torn.Detail);
            Assert.Equal(PartParityHostIntegrity.DamageGone,
                PartParityHostIntegrity.ParseDamage("reason=exploded\n").Reason);
        }

        // ----- the case vessel -----

        [Theory]
        [InlineData(true, true, false, false)]
        [InlineData(false, false, false, true)]  // destroyed vessel (Unity null)
        [InlineData(true, false, false, true)]   // root part gone
        [InlineData(true, true, true, true)]     // DEAD
        public void IsCaseVesselLost(bool present, bool root, bool dead, bool lost)
        {
            Assert.Equal(lost, PartParityHostIntegrity.IsCaseVesselLost(present, root, dead));
        }

        // ----- the runner's wiring -----

        private static string RunnerSource()
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            return File.ReadAllText(Path.Combine(root, "Source", "Parsek", "TestCommands", "PartParitySweepRunner.cs"));
        }

        private static string Body(string src, string signature)
        {
            int start = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, "missing " + signature);
            int next = src.IndexOf("        private static ", start + signature.Length, StringComparison.Ordinal);
            int next2 = src.IndexOf("        internal static ", start + signature.Length, StringComparison.Ordinal);
            int end = next < 0 ? next2 : next2 < 0 ? next : Math.Min(next, next2);
            return end < 0 ? src.Substring(start) : src.Substring(start, end - start);
        }

        [Fact]
        public void Runner_ChecksTheHostBeforeEachWaveAfterEachCaseAndWhileWaiting()
        {
            string src = RunnerSource();
            Assert.Contains("CheckHost(ctx, \"before wave ", Body(src, "private static IEnumerator RunWaves("));
            string wave = Body(src, "private static IEnumerator RunWave(");
            Assert.Contains("CheckHost(ctx, \"after case \"", wave);
            Assert.True(wave.IndexOf("FinishCase(ctx, wc)", StringComparison.Ordinal)
                        < wave.IndexOf("CheckHost(ctx, \"after case \"", StringComparison.Ordinal));
            // A stopped wave deletes its inflight (no process died) and still tears down.
            Assert.True(wave.IndexOf("if (ctx.HostDamage != null)", StringComparison.Ordinal)
                        < wave.IndexOf("TearDownWave(ctx, wave)", StringComparison.Ordinal));
            Assert.Contains("CheckHost(ctx, \"spawn wait\")", Body(src, "internal static IEnumerator WaitReady("));
            Assert.Contains("CheckHost(ctx, \"settle wait\")", Body(src, "internal static IEnumerator WaitSettled("));
        }

        [Fact]
        public void Runner_ARecordedDamageStopsTheStepBeforeAdmission()
        {
            string core = Body(RunnerSource(), "private static IEnumerator StepCore(");
            int prior = core.IndexOf("ReadHostDamage(ctx.LabelDir)", StringComparison.Ordinal);
            int admit = core.IndexOf("AdmitHost(ctx)", StringComparison.Ordinal);
            Assert.True(prior >= 0 && admit > prior, "the damage record must be read before the host admission");
            Assert.Contains("ReadHostBaseline(ctx.LabelDir)", core);
            Assert.Contains("CheckHost(ctx, \"step start\")", core);
        }

        [Fact]
        public void Runner_RechecksTheCaseVesselBeforePrepare()
        {
            string capture = Body(RunnerSource(), "private static IEnumerator RunCaseCapture(");
            int lost = capture.IndexOf("CaseVesselLost(wc)", StringComparison.Ordinal);
            int prepare = capture.IndexOf("PartParityCaseCapture.Prepare(ctx, wc)", StringComparison.Ordinal);
            Assert.True(lost >= 0 && prepare > lost, "the case vessel must be rechecked before Prepare");
            Assert.Contains("SetupDiedSettling", capture.Substring(lost, prepare - lost));
        }
    }
}
