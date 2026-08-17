using Parsek;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure half of the Settings window's height fit (the Basic / Advanced switch).
    ///
    /// <para>What these CANNOT cover: the GUILayout clamp itself
    /// (<c>Mathf.Clamp(passedHeight, contentMin, contentMax)</c>) needs a real IMGUI pass,
    /// so the behavioural proof that Advanced -&gt; Basic actually shrinks the window lives
    /// in the in-game <c>SettingsWindowShrinksWhenEnteringBasic</c> gate. These pin the
    /// decisions that feed it: what rect the fit pass hands GUILayout, what the caller
    /// stores while the fit is in flight, and when the fit gets reported.</para>
    /// </summary>
    public class SettingsWindowHeightFitTests
    {
        [Fact]
        public void FitPassReleasesTheHeightToZero()
        {
            var stored = new Rect(280f, 100f, 383f, 948f);

            Rect requested = SettingsWindowPresentation.BuildHeightFitLayoutRect(stored, releaseHeight: true);

            // Zero is the whole fix: Clamp(0, contentMin, contentMax) resolves to contentMin
            // (the true fit), where Clamp(948, ...) clamps straight back to 948 because this
            // window's contentMax sits above it.
            Assert.Equal(0f, requested.height);
            Assert.Equal(stored.x, requested.x);
            Assert.Equal(stored.y, requested.y);
            Assert.Equal(stored.width, requested.width);
        }

        [Fact]
        public void NonFitPassHandsTheStoredRectThroughUntouched()
        {
            var stored = new Rect(280f, 100f, 383f, 948f);

            Rect requested = SettingsWindowPresentation.BuildHeightFitLayoutRect(stored, releaseHeight: false);

            Assert.Equal(stored, requested);
        }

        [Fact]
        public void FitPassKeepsTheStoredHeightOutOfTheCollapsedReturn()
        {
            // What GUILayout hands back on the fit pass: the zero-height rect it was given.
            var drawn = new Rect(280f, 100f, 383f, 0f);

            Rect kept = SettingsWindowPresentation.KeepStoredHeightAcrossFitPass(
                drawn, storedHeight: 948f, releaseHeight: true);

            // Storing the zero would collapse the window AND feed GUILayout.Height(0) back
            // on the next non-fit pass, pinning it there for good.
            Assert.Equal(948f, kept.height);
        }

        [Fact]
        public void FitPassStillHonoursAPositionChangeFromTheSamePass()
        {
            // A drag landing on the fit pass must not be discarded: x / y / width come from
            // the returned rect, only the height is restored.
            var drawn = new Rect(640f, 220f, 400f, 0f);

            Rect kept = SettingsWindowPresentation.KeepStoredHeightAcrossFitPass(
                drawn, storedHeight: 948f, releaseHeight: true);

            Assert.Equal(640f, kept.x);
            Assert.Equal(220f, kept.y);
            Assert.Equal(400f, kept.width);
            Assert.Equal(948f, kept.height);
        }

        [Fact]
        public void NonFitPassStoresWhateverGuiLayoutReturned()
        {
            var drawn = new Rect(280f, 100f, 383f, 636f);

            Rect kept = SettingsWindowPresentation.KeepStoredHeightAcrossFitPass(
                drawn, storedHeight: 948f, releaseHeight: false);

            Assert.Equal(drawn, kept);
        }

        [Fact]
        public void NothingIsLoggedWhenNoFitIsInFlight()
        {
            Assert.Equal(
                SettingsWindowPresentation.HeightFitLogOutcome.None,
                SettingsWindowPresentation.ClassifyHeightFitLog(
                    awaitingFit: false, heightAtFitPass: 948f, currentHeight: 636f, passesRemaining: 12));
        }

        [Fact]
        public void TheFitIsReportedOnlyOnceTheFittedHeightLands()
        {
            // Same pass as the request: the fitted height has not come back yet, so the old
            // code's log here was the misleading one (it printed 948 on a switch to Basic
            // that had resized nothing).
            Assert.Equal(
                SettingsWindowPresentation.HeightFitLogOutcome.None,
                SettingsWindowPresentation.ClassifyHeightFitLog(
                    awaitingFit: true, heightAtFitPass: 948f, currentHeight: 948f, passesRemaining: 12));

            Assert.Equal(
                SettingsWindowPresentation.HeightFitLogOutcome.Applied,
                SettingsWindowPresentation.ClassifyHeightFitLog(
                    awaitingFit: true, heightAtFitPass: 948f, currentHeight: 636f, passesRemaining: 9));
        }

        [Fact]
        public void AnUnchangedHeightIsReportedOnceTheWaitRunsOut()
        {
            Assert.Equal(
                SettingsWindowPresentation.HeightFitLogOutcome.NoChange,
                SettingsWindowPresentation.ClassifyHeightFitLog(
                    awaitingFit: true, heightAtFitPass: 636f, currentHeight: 636f, passesRemaining: 0));
        }

        [Fact]
        public void AnAppliedFitWinsOverAnExpiredWait()
        {
            // Both conditions true on the same pass: the height DID change, so say so.
            Assert.Equal(
                SettingsWindowPresentation.HeightFitLogOutcome.Applied,
                SettingsWindowPresentation.ClassifyHeightFitLog(
                    awaitingFit: true, heightAtFitPass: 948f, currentHeight: 636f, passesRemaining: 0));
        }
    }
}
