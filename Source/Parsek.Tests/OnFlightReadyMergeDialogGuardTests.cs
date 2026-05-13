using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for <see cref="ParsekFlight.ShouldShowOnFlightReadyMergeDialog"/>.
    /// The OnFlightReady fallback opens the tree merge dialog only when there is
    /// a pending tree, no restore coroutine owns it, AND no active Re-Fly session
    /// owns it. The Re-Fly guard prevents the fallback from firing immediately
    /// after an initial Re-Fly invocation or a Retry-from-Rewind-Point.
    /// </summary>
    [Collection("Sequential")]
    public class OnFlightReadyMergeDialogGuardTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public OnFlightReadyMergeDialogGuardTests()
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
            Assert.False(ParsekFlight.ShouldShowOnFlightReadyMergeDialog(
                hasPendingTree: false,
                restoringActiveTree: false,
                reFlySessionActive: false));
        }

        [Fact]
        public void PendingTree_NoRestoring_NoReFly_ReturnsTrue()
        {
            // Canonical fallback case: a pending tree leaked through to OnFlightReady
            // outside a Re-Fly. This is the only configuration that opens the dialog.
            Assert.True(ParsekFlight.ShouldShowOnFlightReadyMergeDialog(
                hasPendingTree: true,
                restoringActiveTree: false,
                reFlySessionActive: false));
        }

        [Fact]
        public void PendingTree_RestoringActiveTree_ReturnsFalse()
        {
            // #293: restore coroutine owns the pending tree mid-yield. Skipping the
            // fallback prevents auto-merging it and leaving the vessel with no
            // active recorder.
            Assert.False(ParsekFlight.ShouldShowOnFlightReadyMergeDialog(
                hasPendingTree: true,
                restoringActiveTree: true,
                reFlySessionActive: false));
        }

        [Fact]
        public void PendingTree_ReFlySessionActive_ReturnsFalse()
        {
            // Initial Re-Fly invocation or Retry-from-Rewind-Point: AtomicMarkerWrite
            // just attached a fresh fork to the pending tree, and the session marker
            // is set by the time OnFlightReady runs. The merge decision belongs to
            // the scene-exit path once the attempt actually finishes, not now.
            Assert.False(ParsekFlight.ShouldShowOnFlightReadyMergeDialog(
                hasPendingTree: true,
                restoringActiveTree: false,
                reFlySessionActive: true));
        }

        [Fact]
        public void PendingTree_RestoringAndReFlyActive_ReturnsFalse()
        {
            // Defense in depth: either skip reason is sufficient. Combined, the
            // dialog still must not fire.
            Assert.False(ParsekFlight.ShouldShowOnFlightReadyMergeDialog(
                hasPendingTree: true,
                restoringActiveTree: true,
                reFlySessionActive: true));
        }

        [Fact]
        public void NoPendingTree_RestoringActive_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldShowOnFlightReadyMergeDialog(
                hasPendingTree: false,
                restoringActiveTree: true,
                reFlySessionActive: false));
        }

        [Fact]
        public void NoPendingTree_ReFlySessionActive_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldShowOnFlightReadyMergeDialog(
                hasPendingTree: false,
                restoringActiveTree: false,
                reFlySessionActive: true));
        }
    }
}
