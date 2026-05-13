using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for <see cref="ParsekScenario.ShouldShowDeferredMergeDialog"/>.
    /// The deferred-merge-dialog fallback opens the tree merge dialog only when
    /// there is a pending tree AND no active Re-Fly session owns it. Mirrors
    /// <see cref="ParsekFlight.ShouldShowOnFlightReadyMergeDialog"/> (PR #839)
    /// for the FLIGHT→non-FLIGHT exit fallback path that runs when
    /// <c>SceneExitInterceptor</c>'s pre-transition prefix missed the scene
    /// change (mod compat, KSP version drift, foreign LoadScene patch).
    /// </summary>
    [Collection("Sequential")]
    public class DeferredMergeDialogGuardTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public DeferredMergeDialogGuardTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void NoPendingTree_ReturnsFalse()
        {
            Assert.False(ParsekScenario.ShouldShowDeferredMergeDialog(
                hasPendingTree: false,
                reFlySessionActive: false));
        }

        [Fact]
        public void PendingTree_NoReFly_ReturnsTrue()
        {
            // Canonical fallback case: SceneExitInterceptor missed a
            // FLIGHT→KSC/TS/MAINMENU transition outside any Re-Fly session,
            // and the OnLoad fallback queued ShowDeferredMergeDialog. This
            // is the only configuration that opens the dialog.
            Assert.True(ParsekScenario.ShouldShowDeferredMergeDialog(
                hasPendingTree: true,
                reFlySessionActive: false));
        }

        [Fact]
        public void PendingTree_ReFlySessionActive_ReturnsFalse()
        {
            // SceneExitInterceptor missed a FLIGHT→non-FLIGHT transition
            // while a Re-Fly session was active. AtomicMarkerWrite attached
            // a fresh fork to the pending tree, the session marker survived
            // the transition, and the deferred coroutine resumed in the
            // new scene. The merge decision belongs to the active Re-Fly
            // session, not to this fallback.
            Assert.False(ParsekScenario.ShouldShowDeferredMergeDialog(
                hasPendingTree: true,
                reFlySessionActive: true));
        }

        [Fact]
        public void NoPendingTree_ReFlySessionActive_ReturnsFalse()
        {
            // Defense in depth: either skip reason is sufficient.
            Assert.False(ParsekScenario.ShouldShowDeferredMergeDialog(
                hasPendingTree: false,
                reFlySessionActive: true));
        }
    }
}
