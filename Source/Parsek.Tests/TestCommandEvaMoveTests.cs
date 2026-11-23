using System.Globalization;
using System.Threading;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the <c>EvaMove</c> seam verb's decision core: the fail-closed arg
    /// parse, the arm refusals, the per-poll drive / release / stow / conclude ladder, the
    /// dispatch row and the payload and log line shapes. No KSP types are touched.
    /// </summary>
    public class TestCommandEvaMoveTests
    {
        private static ParsedCommand Cmd(string line) => TestCommandParser.ParseLine(line, 1);

        // ----- TryParseArgs -----

        [Fact]
        public void Parse_JetpackUpWithStow()
        {
            Assert.Null(TestCommandEvaMove.TryParseArgs("jetpack", "up", "1.5", "true", out var a));
            Assert.Equal(EvaMoveMode.Jetpack, a.Mode);
            Assert.Equal(EvaMoveDir.Up, a.Dir);
            Assert.Equal(1.5, a.Seconds);
            Assert.True(a.Stow);
        }

        [Fact]
        public void Parse_WalkForward_StowDefaultsFalse()
        {
            Assert.Null(TestCommandEvaMove.TryParseArgs("walk", "forward", "3", null, out var a));
            Assert.Equal(EvaMoveMode.Walk, a.Mode);
            Assert.Equal(EvaMoveDir.Forward, a.Dir);
            Assert.False(a.Stow);
        }

        [Theory]
        [InlineData(null, "up", "1", null, "evamove-mode-invalid")]
        [InlineData("Jetpack", "up", "1", null, "evamove-mode-invalid")]
        [InlineData("fly", "up", "1", null, "evamove-mode-invalid")]
        [InlineData("jetpack", null, "1", null, "evamove-dir-invalid")]
        [InlineData("jetpack", "Up", "1", null, "evamove-dir-invalid")]
        [InlineData("walk", "up", "1", null, "evamove-dir-invalid")]
        [InlineData("walk", "down", "1", null, "evamove-dir-invalid")]
        [InlineData("jetpack", "up", null, null, "evamove-seconds-invalid")]
        [InlineData("jetpack", "up", "0.05", null, "evamove-seconds-invalid")]
        [InlineData("jetpack", "up", "30.5", null, "evamove-seconds-invalid")]
        [InlineData("jetpack", "up", "NaN", null, "evamove-seconds-invalid")]
        [InlineData("jetpack", "up", "1,5", null, "evamove-seconds-invalid")]
        [InlineData("jetpack", "up", "1", "yes", "evamove-stow-invalid")]
        [InlineData("jetpack", "up", "1", "True", "evamove-stow-invalid")]
        [InlineData("walk", "left", "1", "true", "evamove-stow-invalid")]
        public void Parse_RefusesFailClosed(string mode, string dir, string seconds, string stow, string reason)
        {
            Assert.Equal(reason, TestCommandEvaMove.TryParseArgs(mode, dir, seconds, stow, out _));
        }

        [Fact]
        public void Parse_SecondsBoundsAreInclusive_AndInvariantUnderACommaCulture()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Null(TestCommandEvaMove.TryParseArgs("jetpack", "up", "0.1", null, out var lo));
                Assert.Equal(0.1, lo.Seconds);
                Assert.Null(TestCommandEvaMove.TryParseArgs("jetpack", "up", "30", "false", out var hi));
                Assert.Equal(30.0, hi.Seconds);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void Dir_TokensRoundTrip_AndAxisSignsAreOneAxisEach()
        {
            foreach (string raw in TestCommandEvaMove.DirValues)
            {
                Assert.True(TestCommandEvaMove.TryParseDir(raw, out EvaMoveDir d));
                Assert.Equal(raw, TestCommandEvaMove.DirToken(d));
                TestCommandEvaMove.AxisSigns(d, out int f, out int r, out int u);
                Assert.Equal(1, System.Math.Abs(f) + System.Math.Abs(r) + System.Math.Abs(u));
            }
            TestCommandEvaMove.AxisSigns(EvaMoveDir.Back, out int bf, out _, out _);
            Assert.Equal(-1, bf);
            TestCommandEvaMove.AxisSigns(EvaMoveDir.Left, out _, out int lr, out _);
            Assert.Equal(-1, lr);
            TestCommandEvaMove.AxisSigns(EvaMoveDir.Down, out _, out _, out int du);
            Assert.Equal(-1, du);
        }

        // ----- DecideArmRefusal -----

        [Fact]
        public void ArmRefusal_InOrder()
        {
            Assert.Equal("evamove-not-eva",
                TestCommandEvaMove.DecideArmRefusal(EvaMoveMode.Jetpack, false, true, false, 0, false));
            Assert.Equal("evamove-ragdoll",
                TestCommandEvaMove.DecideArmRefusal(EvaMoveMode.Jetpack, true, true, false, 0, false));
            Assert.Equal("evamove-no-jetpack",
                TestCommandEvaMove.DecideArmRefusal(EvaMoveMode.Jetpack, true, false, false, 5, false));
            Assert.Equal("evamove-no-fuel",
                TestCommandEvaMove.DecideArmRefusal(EvaMoveMode.Jetpack, true, false, true, 0, false));
            Assert.Equal("evamove-no-fuel",
                TestCommandEvaMove.DecideArmRefusal(EvaMoveMode.Jetpack, true, false, true, double.NaN, false));
            Assert.Null(TestCommandEvaMove.DecideArmRefusal(EvaMoveMode.Jetpack, true, false, true, 5, false));
            // The walk needs the ground, not a jetpack.
            Assert.Equal("evamove-not-on-ground",
                TestCommandEvaMove.DecideArmRefusal(EvaMoveMode.Walk, true, false, true, 5, false));
            Assert.Null(TestCommandEvaMove.DecideArmRefusal(EvaMoveMode.Walk, true, false, false, 0, true));
        }

        // ----- Decide -----

        private static EvaMoveDecision D(EvaMovePhase phase, bool alive = true, bool ragdoll = false,
            double ut = 100, double endUT = 101, bool effect = true, bool stow = false, bool pack = false,
            int fires = 0, int polls = 0, bool settled = true, double elapsed = 1, double budget = 90)
            => TestCommandEvaMove.Decide(phase, alive, ragdoll, ut, endUT, effect, stow, pack, fires, polls,
                settled, elapsed, budget);

        [Fact]
        public void Decide_Driving_HoldsUntilTheEndUT()
        {
            Assert.Equal(EvaMoveDecision.KeepWaiting, D(EvaMovePhase.Driving, ut: 100.5));
            Assert.Equal(EvaMoveDecision.ReleaseDrive, D(EvaMovePhase.Driving, ut: 101));
            Assert.Equal(EvaMoveDecision.Ragdolled, D(EvaMovePhase.Driving, ragdoll: true, ut: 101));
            Assert.Equal(EvaMoveDecision.Timeout, D(EvaMovePhase.Driving, ut: 100.5, elapsed: 90));
        }

        [Fact]
        public void Decide_KerbalLostBeatsEverything()
        {
            Assert.Equal(EvaMoveDecision.KerbalLost, D(EvaMovePhase.Driving, alive: false, ragdoll: true));
            Assert.Equal(EvaMoveDecision.KerbalLost, D(EvaMovePhase.Released, alive: false, effect: false));
        }

        [Fact]
        public void Decide_Released_NoEffectIsAnError()
        {
            Assert.Equal(EvaMoveDecision.NoEffect, D(EvaMovePhase.Released, effect: false));
            Assert.Equal("evamove-no-effect", TestCommandEvaMove.ErrorTokenFor(EvaMoveDecision.NoEffect));
        }

        [Fact]
        public void Decide_Released_SettlesThenCompletes()
        {
            Assert.Equal(EvaMoveDecision.KeepWaiting, D(EvaMovePhase.Released, settled: false));
            Assert.Equal(EvaMoveDecision.Timeout, D(EvaMovePhase.Released, settled: false, elapsed: 95));
            Assert.Equal(EvaMoveDecision.CompleteOk, D(EvaMovePhase.Released));
            // No stow asked: a deployed pack is left out.
            Assert.Equal(EvaMoveDecision.CompleteOk, D(EvaMovePhase.Released, pack: true));
        }

        [Fact]
        public void Decide_Released_StowLadder()
        {
            Assert.Equal(EvaMoveDecision.FireStow, D(EvaMovePhase.Released, stow: true, pack: true));
            Assert.Equal(EvaMoveDecision.KeepWaiting,
                D(EvaMovePhase.Released, stow: true, pack: true, fires: 1, polls: 5));
            Assert.Equal(EvaMoveDecision.FireStow, D(EvaMovePhase.Released, stow: true, pack: true, fires: 1,
                polls: TestCommandEvaMove.StowRefirePolls));
            Assert.Equal(EvaMoveDecision.StowRefused, D(EvaMovePhase.Released, stow: true, pack: true,
                fires: TestCommandEvaMove.MaxStowFires, polls: TestCommandEvaMove.StowRefirePolls));
            // Stowed: settle and complete.
            Assert.Equal(EvaMoveDecision.CompleteOk, D(EvaMovePhase.Released, stow: true, pack: false, fires: 1));
        }

        [Fact]
        public void ErrorTokens_CoverTheTerminals()
        {
            Assert.Equal("evamove-kerbal-lost", TestCommandEvaMove.ErrorTokenFor(EvaMoveDecision.KerbalLost));
            Assert.Equal("evamove-ragdolled", TestCommandEvaMove.ErrorTokenFor(EvaMoveDecision.Ragdolled));
            Assert.Equal("evamove-stow-refused", TestCommandEvaMove.ErrorTokenFor(EvaMoveDecision.StowRefused));
            Assert.Equal("evamove-timeout", TestCommandEvaMove.ErrorTokenFor(EvaMoveDecision.Timeout));
            Assert.Null(TestCommandEvaMove.ErrorTokenFor(EvaMoveDecision.CompleteOk));
            Assert.Null(TestCommandEvaMove.ErrorTokenFor(EvaMoveDecision.KeepWaiting));
        }

        [Fact]
        public void WalkMoved_Threshold()
        {
            Assert.False(TestCommandEvaMove.WalkMoved(0.19));
            Assert.True(TestCommandEvaMove.WalkMoved(TestCommandEvaMove.MinWalkMeters));
            Assert.False(TestCommandEvaMove.WalkMoved(double.NaN));
        }

        // ----- Dispatch row -----

        [Fact]
        public void EvaMove_IsImplementedFlightOnlyOnItsOwnBudget()
        {
            Assert.Equal(TestCommandVerbClass.Implemented, TestCommandVerbs.Classify("EvaMove"));
            Assert.Equal(VerbSceneRequirement.RequiresFlight, TestCommandDispatcher.RequirementFor("EvaMove"));
            Assert.Equal(TestCommandEvaMove.BudgetSeconds, DeferralBudget.BudgetSeconds("EvaMove"));
            Assert.True(TestCommandEvaMove.BudgetSeconds <= 540.0);
            Assert.False(TestCommandActiveVesselLoss.WatchesActiveVessel("EvaMove"));
        }

        [Fact]
        public void Dispatch_NotOnEva_DefersNotEva()
        {
            var state = new DispatchState
            {
                Scene = TestCommandScene.Flight,
                GameLoaded = true,
                SettingsPresent = true,
                FlightEvaPresent = true,
                ActiveVesselIsEva = false,
            };
            var line = "id=1 cmd=EvaMove mode=jetpack dir=up seconds=1";
            var r = TestCommandDispatcher.DecideDispatch(Cmd(line), state);
            Assert.Equal(DispatchDecision.Defer, r.Decision);
            Assert.Equal("not-eva", r.Reason);

            state.ActiveVesselIsEva = true;
            Assert.Equal(DispatchDecision.Execute, TestCommandDispatcher.DecideDispatch(Cmd(line), state).Decision);
        }

        // ----- Payload and lines -----

        [Fact]
        public void Payload_AndStartLine_AreInvariant()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                TestCommandEvaMove.TryParseArgs("jetpack", "down", "1.25", "true", out var a);
                var p = TestCommandEvaMove.BuildOkPayload("Valentina Kerman", 77u, a, 424.5, 425.75, true, false,
                    2614652043u, 3.456, double.NaN, 0.104);
                string[] keys = { "kerbal", "evaPid", "mode", "dir", "seconds", "startUT", "endUT", "effect",
                    "deployed", "refPid", "refDistStart", "refDistEnd", "refSpeed" };
                Assert.Equal(keys.Length, p.Count);
                for (int i = 0; i < keys.Length; i++) Assert.Equal(keys[i], p[i].Key);
                Assert.Equal("Valentina Kerman", p[0].Value);
                Assert.Equal("jetpack", p[2].Value);
                Assert.Equal("down", p[3].Value);
                Assert.Equal("1.25", p[4].Value);
                Assert.Equal("424.50", p[5].Value);
                Assert.Equal("425.75", p[6].Value);
                Assert.Equal("true", p[7].Value);
                Assert.Equal("false", p[8].Value);
                Assert.Equal("2614652043", p[9].Value);
                Assert.Equal("3.46", p[10].Value);
                Assert.Equal("-1", p[11].Value);
                Assert.Equal("0.10", p[12].Value);

                Assert.Equal("evamove start kerbal='Jeb' evaPid=7 mode=jetpack dir=down seconds=1.25 stow=true "
                    + "startUT=10.00 packBefore=false refPid=0 refDist=-1",
                    TestCommandEvaMove.FormatStartLine("Jeb", 7u, a, 10.0, false, 0u, -1.0));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void ReasonTables_MatchTheConstants()
        {
            Assert.Equal(new[] { TestCommandEvaMove.ModeInvalidReason, TestCommandEvaMove.DirInvalidReason,
                TestCommandEvaMove.SecondsInvalidReason, TestCommandEvaMove.StowInvalidReason },
                TestCommandEvaMove.ArgReasons);
            Assert.Equal(new[] { TestCommandEvaMove.NotEvaReason, TestCommandEvaMove.NoJetpackReason,
                TestCommandEvaMove.NoFuelReason, TestCommandEvaMove.RagdollReason,
                TestCommandEvaMove.NotOnGroundReason, TestCommandEvaMove.PackRefusedReason },
                TestCommandEvaMove.Reasons);
            Assert.Equal(new[] { TestCommandEvaMove.ThrewReason, TestCommandEvaMove.KerbalLostReason,
                TestCommandEvaMove.RagdolledReason, TestCommandEvaMove.NoEffectReason,
                TestCommandEvaMove.StowRefusedReason, TestCommandEvaMove.TimeoutReason },
                TestCommandEvaMove.ErrorReasons);
        }
    }
}
