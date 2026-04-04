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
