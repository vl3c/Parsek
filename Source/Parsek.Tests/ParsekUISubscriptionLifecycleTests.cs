using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins how a scene's <see cref="ParsekUI"/> hooks its windows to the two cache
    /// invalidation signals: <see cref="LedgerOrchestrator.OnTimelineDataChanged"/> (every
    /// ledger-side change) and <see cref="DisplayNames.DisplayNamesChanged"/> (a vessel or
    /// mission rename). Both drop the Timeline cache and then the Kerbals cache, from the
    /// moment the ParsekUI is constructed until its <see cref="ParsekUI.Cleanup"/>. A scene
    /// handover constructs the next ParsekUI before the previous one is cleaned up, so for
    /// that one frame both instances react, and cleaning up the old one leaves the new one
    /// hooked. A second Cleanup is a no-op.
    ///
    /// <para>The observable is the two windows' own invalidation log lines, in order. Written
    /// against the code that subscribes from ParsekUI itself, so the subscription can move
    /// into the windows with these cells unchanged.</para>
    /// </summary>
    [Collection("Sequential")]
    public class ParsekUISubscriptionLifecycleTests : IDisposable
    {
        private const string TimelineLine = "[Timeline] Cache invalidated";
        private const string KerbalsLine = "KerbalsWindow: cache invalidated";

        private readonly List<string> logLines = new List<string>();
        private readonly Action savedTimelineHook;
        private readonly List<ParsekUI> built = new List<ParsekUI>();

        public ParsekUISubscriptionLifecycleTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekUI.ResetUiComplexityModeForTesting();
            savedTimelineHook = LedgerOrchestrator.OnTimelineDataChanged;
            LedgerOrchestrator.OnTimelineDataChanged = null;
            DisplayNames.ResetSubscribersForTesting();
        }

        public void Dispose()
        {
            foreach (ParsekUI ui in built)
                CleanupIgnoringUnityTeardown(ui);
            LedgerOrchestrator.OnTimelineDataChanged = savedTimelineHook;
            DisplayNames.ResetSubscribersForTesting();
            ParsekUI.ResetUiComplexityModeForTesting();
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // Cleanup drops both signal hooks first, then resets the cached window styles, which
        // reads a Unity native (GUIStyleState.background): the Windows .NET host throws
        // SecurityException there and mono throws MissingMethodException. The unhooks have
        // already run by then, so the cells still observe them.
        private static void CleanupIgnoringUnityTeardown(ParsekUI ui)
        {
            try { ui.Cleanup(); }
            catch (SecurityException) { }
            catch (MissingMethodException) { }
        }

        private ParsekUI Build()
        {
            var ui = new ParsekUI(UIMode.KSC);
            built.Add(ui);
            return ui;
        }

        // The invalidation lines a signal produced, in order, as "T" (Timeline) / "K" (Kerbals).
        private string Fire(Action raise)
        {
            logLines.Clear();
            raise();
            return string.Concat(logLines
                .Where(l => l.Contains(TimelineLine) || l.Contains(KerbalsLine))
                .Select(l => l.Contains(TimelineLine) ? "T" : "K"));
        }

        private static void RaiseTimeline() => LedgerOrchestrator.OnTimelineDataChanged?.Invoke();

        private static void RaiseRename() => DisplayNames.NoteChanged("test", "lifecycle");

        [Fact]
        public void NoParsekUI_NeitherSignalReachesAWindow()
        {
            Assert.Equal("", Fire(RaiseTimeline));
            Assert.Equal("", Fire(RaiseRename));
        }

        [Fact]
        public void Constructed_EachSignalDropsTimelineThenKerbals_Once()
        {
            Build();

            Assert.Equal("TK", Fire(RaiseTimeline));
            Assert.Equal("TK", Fire(RaiseRename));
        }

        [Fact]
        public void Cleanup_UnhooksBothSignals()
        {
            ParsekUI ui = Build();
            CleanupIgnoringUnityTeardown(ui);

            Assert.Equal("", Fire(RaiseTimeline));
            Assert.Equal("", Fire(RaiseRename));
        }

        [Fact]
        public void SecondCleanup_IsANoOp()
        {
            ParsekUI ui = Build();
            CleanupIgnoringUnityTeardown(ui);
            CleanupIgnoringUnityTeardown(ui);

            Assert.Equal("", Fire(RaiseTimeline));
            Assert.Equal("", Fire(RaiseRename));
        }

        [Fact]
        public void SceneHandover_BothInstancesReact_UntilTheOldOneIsCleanedUp()
        {
            ParsekUI previous = Build();
            ParsekUI next = Build();

            Assert.Equal("TKTK", Fire(RaiseTimeline));
            Assert.Equal("TKTK", Fire(RaiseRename));

            CleanupIgnoringUnityTeardown(previous);

            Assert.Equal("TK", Fire(RaiseTimeline));
            Assert.Equal("TK", Fire(RaiseRename));

            CleanupIgnoringUnityTeardown(next);

            Assert.Equal("", Fire(RaiseTimeline));
            Assert.Equal("", Fire(RaiseRename));
        }

        [Fact]
        public void Cleanup_LeavesAnotherSubscriberOnTheTimelineSignal()
        {
            int outside = 0;
            LedgerOrchestrator.OnTimelineDataChanged += () => outside++;
            ParsekUI ui = Build();
            CleanupIgnoringUnityTeardown(ui);

            Assert.Equal("", Fire(RaiseTimeline));
            Assert.Equal(1, outside);
        }
    }
}
