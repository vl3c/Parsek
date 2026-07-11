using System.Linq;
using Parsek.InGameTests;
using Xunit;

// GameScenes lives in Assembly-CSharp, referenced by the Tests project.

namespace Parsek.Tests
{
    // Pure-decision tests for the M-A3 autorun hooks (design
    // docs/dev/design-autotest-autorun-hooks.md "Test Plan"). Each test names the
    // regression it catches. These prove the external env contract's parsing and
    // the three gates without a live KSP.
    public class AutorunHooksTests
    {
        // --- AutorunConfig.Parse (design edge cases 1, 2, 3, 9) ---

        // Guards edge 1: an unset/empty selector leaves H1 fully inert with no
        // spurious warning. Fails if a missing env var accidentally auto-runs.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Parse_UnsetOrEmpty_Inert_NoWarning(string testsVar)
        {
            var cfg = AutorunHooks.Parse(testsVar, null);

            Assert.False(cfg.Enabled);
            Assert.False(cfg.IsAll);
            Assert.Empty(cfg.Categories);
            Assert.Empty(cfg.Warnings);
        }

        // Guards: "all" arms RunAll (IsAll), not a category named "all".
        [Fact]
        public void Parse_All_EnablesRunAll()
        {
            var cfg = AutorunHooks.Parse("all", null);

            Assert.True(cfg.Enabled);
            Assert.True(cfg.IsAll);
            Assert.Empty(cfg.Categories);
            Assert.Empty(cfg.Warnings);
        }

        // Guards: a single category parses to exactly that token, enabled.
        [Fact]
        public void Parse_SingleCategory()
        {
            var cfg = AutorunHooks.Parse("RecordingInvariants", null);

            Assert.True(cfg.Enabled);
            Assert.False(cfg.IsAll);
            Assert.Equal(new[] { "RecordingInvariants" }, cfg.Categories.ToArray());
        }

        // Guards edge 3: an unknown category is kept verbatim (not dropped, not a
        // parse error); the "matched 0 discovered tests" signal is runtime, not here.
        [Fact]
        public void Parse_UnknownCategory_KeptVerbatim_Enabled()
        {
            var cfg = AutorunHooks.Parse("SomeCategoryThatDoesNotExist", null);

            Assert.True(cfg.Enabled);
            Assert.Equal(new[] { "SomeCategoryThatDoesNotExist" }, cfg.Categories.ToArray());
            Assert.Empty(cfg.Warnings);
        }

        // Guards edge 2: malformed selectors (stray/leading/trailing commas,
        // surrounding whitespace) trim + drop empties to the same {A,B}. Fails if a
        // malformed env var silently runs the wrong categories or crashes.
        [Theory]
        [InlineData("A,B")]
        [InlineData("A,,B")]
        [InlineData(" A , B ")]
        [InlineData(",A,B,")]
        [InlineData("A,,,B,")]
        public void Parse_Malformed_TrimsAndDropsEmpties(string testsVar)
        {
            var cfg = AutorunHooks.Parse(testsVar, null);

            Assert.True(cfg.Enabled);
            Assert.Equal(new[] { "A", "B" }, cfg.Categories.ToArray());
            Assert.Empty(cfg.Warnings);
        }

        // Guards edge 2: a non-empty selector that parses to zero tokens
        // (whitespace-only / commas-only) is inert AND warns, distinguishing it from
        // a truly unset var. Fails if such a value silently runs nothing with no
        // diagnostic, or crashes.
        [Theory]
        [InlineData("   ")]
        [InlineData(",")]
        [InlineData(", ,")]
        public void Parse_ZeroCategories_Inert_Warns(string testsVar)
        {
            var cfg = AutorunHooks.Parse(testsVar, null);

            Assert.False(cfg.Enabled);
            Assert.Empty(cfg.Categories);
            Assert.Contains(AutorunHooks.WarnZeroCategories, cfg.Warnings);
        }

        // Guards: exit var "1" arms H2; anything else does not.
        [Theory]
        [InlineData("1", true)]
        [InlineData("0", false)]
        [InlineData("true", false)]
        [InlineData(null, false)]
        [InlineData("", false)]
        public void Parse_ExitArmed_OnlyForExactlyOne(string exitVar, bool expected)
        {
            var cfg = AutorunHooks.Parse("all", exitVar);
            Assert.Equal(expected, cfg.ExitArmed);
        }

        // Guards edge 9: exit set but tests unset warns at startup (the process will
        // neither auto-run nor auto-quit). Fails if that misconfiguration is silent.
        [Fact]
        public void Parse_ExitWithoutTests_Warns()
        {
            var cfg = AutorunHooks.Parse(null, "1");

            Assert.False(cfg.Enabled);
            Assert.True(cfg.ExitArmed);
            Assert.Contains(AutorunHooks.WarnExitWithoutTests, cfg.Warnings);
        }

        // Guards: a valid selector + exit=1 is a clean config with no warnings.
        [Fact]
        public void Parse_TestsAndExit_CleanNoWarnings()
        {
            var cfg = AutorunHooks.Parse("all", "1");

            Assert.True(cfg.Enabled);
            Assert.True(cfg.ExitArmed);
            Assert.Empty(cfg.Warnings);
        }

        // Guards edge 14 (read-once mechanism): Parse is a pure function of its
        // inputs - identical inputs yield an identical parse - which is what makes
        // caching the result at Awake safe against a mid-process env mutation.
        [Fact]
        public void Parse_IsDeterministic()
        {
            var a = AutorunHooks.Parse("A,B", "1");
            var b = AutorunHooks.Parse("A,B", "1");

            Assert.Equal(a.Enabled, b.Enabled);
            Assert.Equal(a.ExitArmed, b.ExitArmed);
            Assert.Equal(a.Categories.ToArray(), b.Categories.ToArray());
        }

        // --- SceneSettleDecision (design edge cases 6, 7) ---

        // A fully-settled FLIGHT scene fires. This is the "everything holds" baseline
        // the negative cases below each perturb by one condition.
        [Fact]
        public void SceneSettle_FlightFullyReady_Fires()
        {
            Assert.True(AutorunHooks.SceneSettleDecision(
                GameScenes.FLIGHT, gameNonNull: true, saveLoaded: true,
                flightReady: true, vesselNonNull: true, vesselPacked: false,
                settleFrames: 30, settleTarget: 30, reconcilePending: false));
        }

        // Guards edge 7: MainMenu / LOADING never fire (no save, not a game scene),
        // even if the other flags are coincidentally true. Fails if H1 fires before a
        // save is loaded and corrupts nothing but runs a meaningless batch.
        [Theory]
        [InlineData(GameScenes.MAINMENU)]
        [InlineData(GameScenes.LOADING)]
        [InlineData(GameScenes.PSYSTEM)]
        [InlineData(GameScenes.CREDITS)]
        public void SceneSettle_NonGameScene_NeverFires(GameScenes scene)
        {
            Assert.False(AutorunHooks.SceneSettleDecision(
                scene, gameNonNull: true, saveLoaded: true,
                flightReady: true, vesselNonNull: true, vesselPacked: false,
                settleFrames: 30, settleTarget: 30, reconcilePending: false));
        }

        // Guards edge 7: a game scene with no loaded save (CurrentGame null or empty
        // SaveFolder) does not fire.
        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(false, false)]
        public void SceneSettle_NoSaveLoaded_DoesNotFire(bool gameNonNull, bool saveLoaded)
        {
            Assert.False(AutorunHooks.SceneSettleDecision(
                GameScenes.SPACECENTER, gameNonNull, saveLoaded,
                flightReady: true, vesselNonNull: true, vesselPacked: false,
                settleFrames: 30, settleTarget: 30, reconcilePending: false));
        }

        // Guards the FLIGHT physics gate: FLIGHT requires ready + a non-null,
        // UNPACKED vessel. Any of not-ready / no-vessel / still-packed blocks the
        // fire so tests never race the recorder or PartLoader.
        [Theory]
        [InlineData(false, true, false)]  // not ready
        [InlineData(true, false, false)]  // no vessel
        [InlineData(true, true, true)]    // still packed
        public void SceneSettle_FlightHalfInitialized_DoesNotFire(
            bool flightReady, bool vesselNonNull, bool vesselPacked)
        {
            Assert.False(AutorunHooks.SceneSettleDecision(
                GameScenes.FLIGHT, gameNonNull: true, saveLoaded: true,
                flightReady, vesselNonNull, vesselPacked,
                settleFrames: 30, settleTarget: 30, reconcilePending: false));
        }

        // Guards: non-FLIGHT game scenes have NO vessel gate - SPACECENTER settles
        // with no active vessel. Fails if the FLIGHT-only vessel gate leaks into
        // other scenes and stalls a SPACECENTER autorun forever.
        [Fact]
        public void SceneSettle_SpaceCenter_NoVesselGate_Fires()
        {
            Assert.True(AutorunHooks.SceneSettleDecision(
                GameScenes.SPACECENTER, gameNonNull: true, saveLoaded: true,
                flightReady: false, vesselNonNull: false, vesselPacked: true,
                settleFrames: 30, settleTarget: 30, reconcilePending: false));
        }

        // Guards the settle counter: the fire is held until settleFrames reaches the
        // target, then fires. Fails if H1 fires on the first qualifying frame and
        // races stock one-frame-late init.
        [Theory]
        [InlineData(0, false)]
        [InlineData(29, false)]
        [InlineData(30, true)]
        [InlineData(31, true)]
        public void SceneSettle_SettleCounterMustReachTarget(int settleFrames, bool expected)
        {
            Assert.Equal(expected, AutorunHooks.SceneSettleDecision(
                GameScenes.FLIGHT, gameNonNull: true, saveLoaded: true,
                flightReady: true, vesselNonNull: true, vesselPacked: false,
                settleFrames, settleTarget: 30, reconcilePending: false));
        }

        // Guards edge 6: a pending crash-reconcile blocks the fire even when the scene
        // is otherwise fully settled, so the baseline is captured against the reverted
        // save, not the half-reverted one.
        [Fact]
        public void SceneSettle_ReconcilePending_Blocks()
        {
            Assert.False(AutorunHooks.SceneSettleDecision(
                GameScenes.FLIGHT, gameNonNull: true, saveLoaded: true,
                flightReady: true, vesselNonNull: true, vesselPacked: false,
                settleFrames: 30, settleTarget: 30, reconcilePending: true));
        }

        // --- AutorunFireGate (design edge cases 5, 8; correction G1) ---

        // The only all-clear combination fires; every blocker below flips it false.
        [Fact]
        public void FireGate_AllClear_Fires()
        {
            Assert.True(AutorunHooks.AutorunFireGate(
                isRunning: false, commandRunnerRunning: false,
                consumedForProcess: false, firedThisScene: false));
        }

        // Guards edge 5: a runner batch already in flight (human clicked Run All)
        // blocks the fire; H1 stays armed and re-checks next frame.
        [Fact]
        public void FireGate_RunnerRunning_Blocks()
        {
            Assert.False(AutorunHooks.AutorunFireGate(
                isRunning: true, commandRunnerRunning: false,
                consumedForProcess: false, firedThisScene: false));
        }

        // Guards correction G1: an M-A2 command-seam batch running blocks the fire,
        // so a settle-fire cannot launch a second concurrent batch and corrupt the
        // campaign. Fails if H1 ignores the seam runner.
        [Fact]
        public void FireGate_CommandRunnerRunning_Blocks()
        {
            Assert.False(AutorunHooks.AutorunFireGate(
                isRunning: false, commandRunnerRunning: true,
                consumedForProcess: false, firedThisScene: false));
        }

        // Guards edge 8: once the process latch has consumed the selector, a per-scene
        // re-arm (FLIGHT->FLIGHT isolation reload) does not restart it.
        [Fact]
        public void FireGate_ConsumedForProcess_Blocks()
        {
            Assert.False(AutorunHooks.AutorunFireGate(
                isRunning: false, commandRunnerRunning: false,
                consumedForProcess: true, firedThisScene: false));
        }

        // Guards single-fire-per-scene: having fired this scene entry blocks a second
        // fire until the next scene re-arm.
        [Fact]
        public void FireGate_FiredThisScene_Blocks()
        {
            Assert.False(AutorunHooks.AutorunFireGate(
                isRunning: false, commandRunnerRunning: false,
                consumedForProcess: false, firedThisScene: true));
        }

        // --- H2ExitDecision (design edge cases 11, 13) ---

        // Guards the core exit contract: quit only when exit is armed AND the batch
        // was an autorun batch. Fails if exit fires on the wrong combination.
        [Theory]
        [InlineData(true, true, true)]     // armed + autorun -> quit
        [InlineData(true, false, false)]   // armed but not autorun -> no quit (edge 13)
        [InlineData(false, true, false)]   // autorun but not armed -> no quit
        [InlineData(false, false, false)]  // neither -> no quit
        public void H2Exit_QuitsOnlyWhenArmedAndAutorun(
            bool exitArmed, bool wasAutorunBatch, bool expectedQuit)
        {
            var d = AutorunHooks.H2ExitDecision(exitArmed, wasAutorunBatch, bounceArmed: false);
            Assert.Equal(expectedQuit, d.ShouldQuit);
        }

        // Guards edge 13: a human-initiated (button) batch never quits KSP under the
        // developer even when both env vars are set, because its wasAutorunBatch latch
        // is false.
        [Fact]
        public void H2Exit_ButtonBatch_NeverQuits()
        {
            var d = AutorunHooks.H2ExitDecision(
                exitArmed: true, wasAutorunBatch: false, bounceArmed: true);
            Assert.False(d.ShouldQuit);
            Assert.False(d.SkipBounce);
        }

        // Guards edge 11: when H2 quits, it supersedes the Space Center bounce (no
        // operator to leave in a usable scene; the process is dying). The disk is
        // already reverted, so skipping the bounce is safe.
        [Fact]
        public void H2Exit_QuitSupersedesBounce()
        {
            var d = AutorunHooks.H2ExitDecision(
                exitArmed: true, wasAutorunBatch: true, bounceArmed: true);
            Assert.True(d.ShouldQuit);
            Assert.True(d.SkipBounce);
        }

        // Guards: a non-quitting decision never suppresses the bounce, so a
        // corruption-recovery bounce still runs for a human batch.
        [Fact]
        public void H2Exit_NoQuit_DoesNotSkipBounce()
        {
            var d = AutorunHooks.H2ExitDecision(
                exitArmed: false, wasAutorunBatch: true, bounceArmed: true);
            Assert.False(d.ShouldQuit);
            Assert.False(d.SkipBounce);
        }
    }
}
