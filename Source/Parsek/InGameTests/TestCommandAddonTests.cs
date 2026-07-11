using System;
using Parsek.TestCommands;

namespace Parsek.InGameTests
{
    /// <summary>
    /// P6.2 in-game (live-KSP) tests for the ParsekTestCommands addon's Unity side.
    ///
    /// <para>Case (a) — env-gate inert-when-unarmed — is fully AUTOMATED: it runs in any
    /// scene and proves the security gate (the addon's armed state matches the pure
    /// <see cref="ParsekTestCommandAddon.IsArmed"/> predicate over the live env var) and,
    /// when unarmed, that the addon never touched the channel files
    /// (<c>StartupDoneForTesting == false</c>).</para>
    ///
    /// <para>Cases (b) and (c) are PENDING-OPERATOR: they cannot self-set-up inside the
    /// in-game batch and therefore <see cref="InGameAssert.Skip"/> with a message naming
    /// the required context (house rule: a FLIGHT/boot test that cannot size itself must
    /// Skip, never assert against an assumed context). The reason they cannot run in the
    /// batch is structural, on two counts. FIRST and foremost is the ENV GATE: the addon
    /// only arms when <c>PARSEK_TEST_COMMANDS=1</c>, and that value is read ONCE at process
    /// start (<c>Awake</c>) and never re-read, so an in-game test - which runs long after
    /// Awake, inside an already-launched process it cannot re-launch - can neither arm the
    /// addon nor un-set the gate. An in-game test therefore has no way to reach a live pump
    /// at all. SECOND (and only relevant once armed) is the BATCH GATE: while an in-game
    /// test batch runs the pump is gated OFF (<c>Update</c> returns at the
    /// <c>IsBatchRunning</c> gate, which post-F5 also covers the interactive Ctrl+Shift+T
    /// runner), so a command written from inside a batch would not be consumed even in an
    /// armed instance. The real end-to-end drive is performed by the EXTERNAL Python
    /// orchestrator (M-A5) against a dedicated automation instance launched with
    /// <c>PARSEK_TEST_COMMANDS=1</c>, not by an in-game test.</para>
    ///
    /// <para>PENDING-OPERATOR RUNBOOK (external, one dedicated automation KSP instance):
    /// <list type="number">
    /// <item><description>Launch KSP with the environment variable
    /// <c>PARSEK_TEST_COMMANDS=1</c> set (fail-closed: any other value stays inert).</description></item>
    /// <item><description>(b) FLIGHT round-trip: with a flight loaded, append to
    /// <c>&lt;KSP-root&gt;/parsek-test-commands.txt</c> the lines
    /// <c>id=1 cmd=StartRecording</c>, <c>id=2 cmd=RecordingState</c>,
    /// <c>id=3 cmd=CommitTree</c>, <c>id=4 cmd=RecordingState</c>; tail
    /// <c>parsek-test-responses.txt</c> and assert verdict=OK for each, that id=2 reports
    /// <c>recording=true</c> with a non-empty <c>tree</c>, that id=3 reports
    /// <c>committed=true</c>, and that id=4 reports <c>recording=false</c>.</description></item>
    /// <item><description>(c) Cold-boot: from the MAIN MENU (no save loaded) append
    /// <c>id=1 cmd=LoadGame save=&lt;SaveFolder&gt; name=persistent</c> then
    /// <c>id=2 cmd=RecordingState</c>; assert id=1 verdict=OK with <c>scene=FLIGHT</c> and
    /// the echoed <c>save</c>, and id=2 verdict=OK reporting the loaded scene.</description></item>
    /// </list>
    /// The addon's at-most-once journal (<c>parsek-test-commands.journal</c>) and lock
    /// (<c>parsek-test-commands.lock</c>) also live at the KSP root.</para>
    /// </summary>
    public static class TestCommandAddonTests
    {
        private const string EnvVarName = "PARSEK_TEST_COMMANDS";

        // (a) AUTOMATED: the shipped default (env unset / not "1") must be provably inert.
        [InGameTest(Category = "TestCommands",
            Description = "TC-A: env-gate honored; when unarmed the addon touches no channel files")]
        public static void EnvGateHonored_InertWhenUnarmed()
        {
            ParsekTestCommandAddon addon = ParsekTestCommandAddon.Instance;
            InGameAssert.IsNotNull(addon,
                "ParsekTestCommandAddon.Instance should exist (KSPAddon Startup.Instantly)");

            string env = Environment.GetEnvironmentVariable(EnvVarName);
            bool expectedArmed = ParsekTestCommandAddon.IsArmed(env);

            InGameAssert.AreEqual(expectedArmed, addon.IsArmedForTesting,
                $"TC-A: armed state must match the pure IsArmed gate for env='{env ?? "unset"}'");

            if (!expectedArmed)
            {
                // The security-critical inert proof: TryStartup (the only file-touching
                // startup) runs solely behind the armed gate, so an unarmed addon never
                // resolves channel paths or writes the lock/journal.
                InGameAssert.IsFalse(addon.StartupDoneForTesting,
                    "TC-A: unarmed addon must perform no channel file access (startup must not run)");
            }

            ParsekLog.Verbose("TestRunner",
                $"TC-A: env-gate honored armed={addon.IsArmedForTesting} startupDone={addon.StartupDoneForTesting} (env='{env ?? "unset"}')");
        }

        // (b) PENDING-OPERATOR: the FLIGHT StartRecording -> RecordingState -> CommitTree
        // -> RecordingState round-trip must be driven by the external orchestrator; the
        // addon pump is gated off during an in-game batch, so this cannot self-run here.
        [InGameTest(Category = "TestCommands", Scene = GameScenes.FLIGHT,
            Description = "TC-B: FLIGHT channel round-trip (external orchestrator; PENDING-OPERATOR)")]
        public static void FlightChannelRoundTrip_PendingOperator()
        {
            InGameAssert.Skip(
                "TC-B PENDING-OPERATOR: the StartRecording->RecordingState->CommitTree->RecordingState "
                + "round-trip via the file channel must be driven by the external orchestrator against an "
                + "instance launched with PARSEK_TEST_COMMANDS=1. It cannot self-run in an in-game batch: "
                + "the env gate is read once at process start so an in-game test can neither arm the addon "
                + "nor reach a live pump, and even in an armed instance the pump is gated off while a batch "
                + "runs (IsBatchRunning, which post-F5 also covers the Ctrl+Shift+T runner). See the "
                + "PENDING-OPERATOR RUNBOOK in TestCommandAddonTests.");
        }

        // (c) PENDING-OPERATOR: the cold-boot LoadGame (from MAINMENU) -> RecordingState
        // boot channel must be driven by the external orchestrator at process start.
        [InGameTest(Category = "TestCommands", Scene = GameScenes.MAINMENU,
            Description = "TC-C: cold-boot LoadGame->RecordingState (external orchestrator; PENDING-OPERATOR)")]
        public static void ColdBootLoadGame_PendingOperator()
        {
            InGameAssert.Skip(
                "TC-C PENDING-OPERATOR: the cold-boot LoadGame->RecordingState boot channel must be driven "
                + "by the external orchestrator against an instance launched with PARSEK_TEST_COMMANDS=1, "
                + "issuing LoadGame as the first command from the main menu. It cannot self-run in an in-game "
                + "test: the env gate is read once at process start, so an in-game test can neither arm the "
                + "addon nor re-launch the process, and the batch gate (post-F5) blocks the pump anyway. See "
                + "the PENDING-OPERATOR RUNBOOK in TestCommandAddonTests.");
        }
    }
}
