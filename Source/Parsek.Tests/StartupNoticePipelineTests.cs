using System;
using System.Collections.Generic;
using HarmonyLib;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The live wiring around <see cref="StartupNotices"/>: the per-frame poster that must
    /// survive stock's scene-load drop window, and the patch sweep that feeds the failure
    /// notice. The patch cells use Harmony for real but never patch a method: one target
    /// does not exist (Harmony throws before touching anything) and one opts out through
    /// <c>Prepare()</c>.
    /// </summary>
    [Collection("Sequential")]
    public class StartupNoticePipelineTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> posted = new List<string>();

        public StartupNoticePipelineTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            StartupNotices.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            StartupNotices.ResetForTesting();
        }

        private bool Accept(string notice)
        {
            posted.Add(notice);
            return true;
        }

        // ---- poster ----

        [Fact]
        public void Poster_EmptyQueueIsDoneImmediately()
        {
            var poster = new StartupNoticePoster();
            Assert.True(poster.Tick(GameScenes.LOADING, false, 0f, Accept));
            Assert.Empty(posted);
        }

        [Fact]
        public void Poster_WaitsForTheSceneToSettleThenPostsOnce()
        {
            StartupNotices.Enqueue("broken");
            var poster = new StartupNoticePoster();

            Assert.False(poster.Tick(GameScenes.LOADING, true, 0f, Accept));
            Assert.False(poster.Tick(GameScenes.MAINMENU, true, 10f, Accept));   // starts the clock
            Assert.False(poster.Tick(GameScenes.MAINMENU, true, 12.9f, Accept)); // 2.9 s
            Assert.Empty(posted);

            Assert.True(poster.Tick(GameScenes.MAINMENU, true, 13f, Accept));    // 3.0 s
            Assert.Equal(new[] { "broken" }, posted);
            Assert.True(poster.Tick(GameScenes.MAINMENU, true, 14f, Accept));
            Assert.Single(posted);
            Assert.Contains(logLines, l => l.Contains("[Init]")
                && l.Contains("Startup notice posted in MAINMENU after 0 dropped attempt(s): broken"));
        }

        [Fact]
        public void Poster_SceneChangeRestartsTheClock()
        {
            StartupNotices.Enqueue("broken");
            var poster = new StartupNoticePoster();

            poster.Tick(GameScenes.MAINMENU, true, 0f, Accept);
            poster.Tick(GameScenes.MAINMENU, true, 2f, Accept);
            // The player clicks Resume: the load request already reports SPACECENTER.
            Assert.False(poster.Tick(GameScenes.SPACECENTER, true, 2.5f, Accept));
            Assert.False(poster.Tick(GameScenes.SPACECENTER, true, 5f, Accept));
            Assert.Empty(posted);

            Assert.True(poster.Tick(GameScenes.SPACECENTER, true, 5.5f, Accept));
            Assert.Single(posted);
        }

        [Fact]
        public void Poster_NonPostableSceneOrMissingScreenMessagesRestartsTheClock()
        {
            StartupNotices.Enqueue("broken");
            var poster = new StartupNoticePoster();

            poster.Tick(GameScenes.MAINMENU, true, 0f, Accept);
            Assert.False(poster.Tick(GameScenes.MAINMENU, false, 2f, Accept));   // no instance
            Assert.False(poster.Tick(GameScenes.MAINMENU, true, 3f, Accept));    // clock restarts
            Assert.False(poster.Tick(GameScenes.SETTINGS, true, 4f, Accept));    // not postable
            Assert.False(poster.Tick(GameScenes.MAINMENU, true, 5f, Accept));
            Assert.False(poster.Tick(GameScenes.MAINMENU, true, 7.9f, Accept));
            Assert.Empty(posted);
            Assert.True(poster.Tick(GameScenes.MAINMENU, true, 8f, Accept));
        }

        [Fact]
        public void Poster_DroppedPostStaysQueuedAndRetriesEveryTick()
        {
            // Stock returns null from the load REQUEST until onLevelWasLoaded; the notice
            // must survive that and the log must not claim a post that never happened.
            StartupNotices.Enqueue("broken");
            var poster = new StartupNoticePoster();
            bool loading = true;
            Func<string, bool> stock = n => { if (loading) return false; posted.Add(n); return true; };

            poster.Tick(GameScenes.SPACECENTER, true, 0f, stock);
            Assert.False(poster.Tick(GameScenes.SPACECENTER, true, 3f, stock));
            Assert.False(poster.Tick(GameScenes.SPACECENTER, true, 3.1f, stock));
            Assert.Equal(2, poster.DroppedAttempts);
            Assert.Equal(1, StartupNotices.PendingCount);
            Assert.DoesNotContain(logLines, l => l.Contains("Startup notice posted"));

            loading = false;
            Assert.True(poster.Tick(GameScenes.SPACECENTER, true, 3.2f, stock));
            Assert.Equal(new[] { "broken" }, posted);
            Assert.Contains(logLines, l => l.Contains("after 2 dropped attempt(s): broken"));
        }

        [Fact]
        public void Poster_PostsEveryQueuedNoticeInOrder()
        {
            StartupNotices.Enqueue("first");
            StartupNotices.Enqueue("second");
            var poster = new StartupNoticePoster();

            poster.Tick(GameScenes.MAINMENU, true, 0f, Accept);
            Assert.True(poster.Tick(GameScenes.MAINMENU, true, 3f, Accept));
            Assert.Equal(new[] { "first", "second" }, posted);
        }

        // ---- patch sweep ----

        [Fact]
        public void ApplyPatches_MissingTargetIsRecordedAsFailedAndLogged()
        {
            var failed = new List<string>();
            int applied = ParsekHarmony.ApplyPatches(new Harmony("parsek.tests.startupnotices"),
                new[] { typeof(StartupNoticeTestPatches.MissingTargetPatch) }, failed);

            Assert.Equal(0, applied);
            Assert.Equal(new[] { "StartupNoticeTestPatches.MissingTargetPatch" }, failed);
            Assert.Contains(logLines, l => l.Contains("[Harmony]")
                && l.Contains("Failed to apply patch MissingTargetPatch"));
        }

        [Fact]
        public void PatchClassKey_QualifiesNestedClassesWithTheirOuterClass()
        {
            Assert.Equal("StartupNoticeTestPatches.MissingTargetPatch",
                StartupNotices.PatchClassKey(typeof(StartupNoticeTestPatches.MissingTargetPatch)));
            Assert.Equal("StartupNoticePipelineTests",
                StartupNotices.PatchClassKey(typeof(StartupNoticePipelineTests)));
            Assert.Null(StartupNotices.PatchClassKey(null));
        }

        [Fact]
        public void ApplyPatches_PrepareFalseCountsAsAppliedNotFailed()
        {
            var failed = new List<string>();
            int applied = ParsekHarmony.ApplyPatches(new Harmony("parsek.tests.startupnotices"),
                new[] { typeof(StartupNoticeTestPatches.OptionalModAbsentPatch) }, failed);

            Assert.Equal(1, applied);
            Assert.Empty(failed);
            Assert.Null(StartupNotices.BuildPatchFailureNotice(applied, failed));
        }

        [Fact]
        public void EndToEnd_FailedPatchReachesTheScreenThroughTheRealPostPath()
        {
            var screen = new List<string>();
            ParsekLog.ScreenMessageSinkForTesting = (msg, duration) => screen.Add(msg);

            var failed = new List<string>();
            int applied = ParsekHarmony.ApplyPatches(new Harmony("parsek.tests.startupnotices"),
                new[]
                {
                    typeof(StartupNoticeTestPatches.OptionalModAbsentPatch),
                    typeof(StartupNoticeTestPatches.MissingTargetPatch),
                }, failed);
            StartupNotices.Enqueue(StartupNotices.BuildPatchFailureNotice(applied + failed.Count, failed));

            var poster = new StartupNoticePoster();
            poster.Tick(GameScenes.MAINMENU, true, 0f,
                n => ParsekLog.TryScreenMessage(n, StartupNotices.NoticeDurationSeconds));
            Assert.True(poster.Tick(GameScenes.MAINMENU, true, 3f,
                n => ParsekLog.TryScreenMessage(n, StartupNotices.NoticeDurationSeconds)));

            Assert.Equal(new[]
            {
                "1 of 2 game patches failed to load, so these may not work: other features. "
                + "Another mod or a different KSP version is the usual cause. Details are in KSP.log.",
            }, screen);
        }
    }

    /// <summary>Patch classes that exercise ParsekHarmony.ApplyPatches without patching anything.</summary>
    internal static class StartupNoticeTestPatches
    {
        internal static class Target
        {
            internal static int Existing() => 1;
        }

        [HarmonyPatch(typeof(Target), "NoSuchMethod")]
        internal static class MissingTargetPatch
        {
            static void Postfix() { }
        }

        [HarmonyPatch(typeof(Target), nameof(Target.Existing))]
        internal static class OptionalModAbsentPatch
        {
            static bool Prepare() => false;
            static void Postfix() { }
        }
    }
}
