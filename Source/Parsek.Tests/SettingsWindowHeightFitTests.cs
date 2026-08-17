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

        // ------------------------------------------------------------------
        // The state transition itself (not just the classification): capturing
        // the baseline a pass late would make every reported `was=` wrong, and
        // losing the awaiting guard would run the countdown forever.
        // ------------------------------------------------------------------

        [Fact]
        public void TheFitPassArmsTheReportAgainstThePreFitHeight()
        {
            var step = SettingsWindowPresentation.AdvanceHeightFitLog(
                default(SettingsWindowPresentation.HeightFitLogState),
                fitPass: true, currentHeight: 948f, passBudget: 12);

            Assert.True(step.State.AwaitingFit);
            // 948 is the height the window still has on the fit pass - the baseline the
            // landed fit is compared against.
            Assert.Equal(948f, step.State.HeightAtFitPass);
            Assert.Equal(12, step.State.PassesRemaining);
            // The fit pass itself can never report: the fitted height has not landed yet.
            Assert.Equal(SettingsWindowPresentation.HeightFitLogOutcome.None, step.Outcome);
        }

        [Fact]
        public void TheReportLandsOnThePassThatCarriesTheFittedHeightBack()
        {
            var armed = SettingsWindowPresentation.AdvanceHeightFitLog(
                default(SettingsWindowPresentation.HeightFitLogState),
                fitPass: true, currentHeight: 948f, passBudget: 12).State;

            // One quiet pass: still 948, nothing to say, one pass off the budget.
            var quiet = SettingsWindowPresentation.AdvanceHeightFitLog(
                armed, fitPass: false, currentHeight: 948f, passBudget: 12);
            Assert.Equal(SettingsWindowPresentation.HeightFitLogOutcome.None, quiet.Outcome);
            Assert.True(quiet.State.AwaitingFit);
            Assert.Equal(11, quiet.State.PassesRemaining);

            // The fitted height lands.
            var landed = SettingsWindowPresentation.AdvanceHeightFitLog(
                quiet.State, fitPass: false, currentHeight: 636f, passBudget: 12);
            Assert.Equal(SettingsWindowPresentation.HeightFitLogOutcome.Applied, landed.Outcome);
            Assert.False(landed.State.AwaitingFit);
            Assert.Equal(948f, landed.State.HeightAtFitPass);
        }

        [Fact]
        public void TheReportIsDisarmedOnceAndOnlyOnce()
        {
            var state = SettingsWindowPresentation.AdvanceHeightFitLog(
                default(SettingsWindowPresentation.HeightFitLogState),
                fitPass: true, currentHeight: 948f, passBudget: 1).State;

            // Budget of one: the next quiet pass exhausts it and reports the no-change.
            var expired = SettingsWindowPresentation.AdvanceHeightFitLog(
                state, fitPass: false, currentHeight: 948f, passBudget: 1);
            Assert.Equal(SettingsWindowPresentation.HeightFitLogOutcome.NoChange, expired.Outcome);
            Assert.False(expired.State.AwaitingFit);

            // Every later pass is silent, and the counter cannot run away once disarmed.
            var after = SettingsWindowPresentation.AdvanceHeightFitLog(
                expired.State, fitPass: false, currentHeight: 636f, passBudget: 1);
            Assert.Equal(SettingsWindowPresentation.HeightFitLogOutcome.None, after.Outcome);
            Assert.Equal(expired.State.PassesRemaining, after.State.PassesRemaining);
        }

        [Fact]
        public void ASecondRequestReArmsAgainstTheHeightItFindsNow()
        {
            var landed = SettingsWindowPresentation.AdvanceHeightFitLog(
                SettingsWindowPresentation.AdvanceHeightFitLog(
                    default(SettingsWindowPresentation.HeightFitLogState),
                    fitPass: true, currentHeight: 948f, passBudget: 12).State,
                fitPass: false, currentHeight: 636f, passBudget: 12).State;

            var reArmed = SettingsWindowPresentation.AdvanceHeightFitLog(
                landed, fitPass: true, currentHeight: 636f, passBudget: 12);

            Assert.True(reArmed.State.AwaitingFit);
            Assert.Equal(636f, reArmed.State.HeightAtFitPass);
            Assert.Equal(12, reArmed.State.PassesRemaining);
        }
    }
}
