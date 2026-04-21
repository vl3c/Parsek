using System;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Marks a method as an in-game test discoverable by the runtime test runner.
    /// Methods can return void (single-frame) or IEnumerator (multi-frame coroutine).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class InGameTestAttribute : Attribute
    {
        /// <summary>Grouping label shown in the test runner UI.</summary>
        public string Category { get; set; } = "General";

        /// <summary>
        /// Scene required for this test. Use <see cref="AnyScene"/> for scene-independent tests.
        /// </summary>
        public GameScenes Scene { get; set; } = AnyScene;

        /// <summary>Sentinel value meaning "runs in any scene". Uses -1 to avoid collision with real scenes.</summary>
        public const GameScenes AnyScene = (GameScenes)(-1);

        /// <summary>Short description shown in the UI tooltip.</summary>
        public string Description { get; set; }

        /// <summary>
        /// Run this test after normal tests in batch execution.
        /// Use for disruptive scenarios that mutate or reload live game state.
        /// </summary>
        public bool RunLast { get; set; }

        /// <summary>
        /// Whether this test is safe to run in the ordinary shared-session
        /// batch entry points such as Run All / Run category. Leave false for
        /// destructive tests that either require the isolated restore path or
        /// must remain manual-only.
        /// </summary>
        public bool AllowBatchExecution { get; set; } = true;

        /// <summary>
        /// When true, the batch runner may include this destructive FLIGHT test
        /// by capturing a temporary baseline save before the batch and
        /// quickloading that baseline after the test finishes. Use for tests
        /// that mutate the live FLIGHT session but can be returned to a known
        /// baseline automatically. Leave false for tests whose failure mode can
        /// still poison or hang the session irrecoverably.
        /// </summary>
        public bool RestoreBatchFlightBaselineAfterExecution { get; set; }

        /// <summary>
        /// Optional reason shown when a test is excluded from the ordinary
        /// shared-session batch path.
        /// </summary>
        public string BatchSkipReason { get; set; }
    }

    /// <summary>
    /// Optional setup method called before each test in the fixture class.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class InGameSetupAttribute : Attribute { }

    /// <summary>
    /// Optional teardown method called after each test (even on failure).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class InGameTeardownAttribute : Attribute { }
}
