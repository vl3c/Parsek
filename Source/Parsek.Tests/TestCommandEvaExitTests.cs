using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// M-C2 coverage for the EvaExit pure surfaces (<see cref="TestCommandEvaExit"/>).
    /// ResolveKerbalArg: default-first-crew / exact-match / unknown / no-crew (fails if a
    /// typo silently EVAs the wrong kerbal). DecideEvaExitCompletion: every conjunct gates,
    /// and the F7 settleSeconds dwell holds the head (fails if the head advances before the
    /// auto-switch or before the dwell elapsed).
    /// </summary>
    public class TestCommandEvaExitTests
    {
        private const double Budget = 120.0;

        private static string Val(List<KeyValuePair<string, string>> p, string key)
            => p.First(kv => kv.Key == key).Value;

        // ----- ResolveKerbalArg -----

        [Fact]
        public void Resolve_NullArg_DefaultsToFirstCrew()
        {
            var crew = new List<string> { "Jebediah Kerman", "Bob Kerman" };
            string r = TestCommandEvaExit.ResolveKerbalArg(null, crew, out string err);
            Assert.Null(err);
            Assert.Equal("Jebediah Kerman", r);
        }

        [Fact]
        public void Resolve_EmptyArg_DefaultsToFirstCrew()
        {
            var crew = new List<string> { "Valentina Kerman" };
            string r = TestCommandEvaExit.ResolveKerbalArg("", crew, out string err);
            Assert.Null(err);
            Assert.Equal("Valentina Kerman", r);
        }

        [Fact]
        public void Resolve_ExactMatch_ReturnsNamed()
        {
            var crew = new List<string> { "Jebediah Kerman", "Bob Kerman" };
            string r = TestCommandEvaExit.ResolveKerbalArg("Bob Kerman", crew, out string err);
            Assert.Null(err);
            Assert.Equal("Bob Kerman", r);
        }

        [Fact]
        public void Resolve_Unknown_ErrorsKerbalNotAboard()
        {
            var crew = new List<string> { "Jebediah Kerman" };
            string r = TestCommandEvaExit.ResolveKerbalArg("Bill Kerman", crew, out string err);
            Assert.Null(r);
            Assert.Equal("kerbal-not-aboard", err);
        }

        [Fact]
        public void Resolve_CaseSensitive_ErrorsKerbalNotAboard()
        {
            // Ordinal exact match: a case-off name is not a hit.
            var crew = new List<string> { "Jebediah Kerman" };
            string r = TestCommandEvaExit.ResolveKerbalArg("jebediah kerman", crew, out string err);
            Assert.Null(r);
            Assert.Equal("kerbal-not-aboard", err);
        }

        [Fact]
        public void Resolve_EmptyCrew_ErrorsNoCrew()
        {
            string r = TestCommandEvaExit.ResolveKerbalArg(null, new List<string>(), out string err);
            Assert.Null(r);
            Assert.Equal("no-crew", err);
        }

        [Fact]
        public void Resolve_NullCrew_ErrorsNoCrew()
        {
            string r = TestCommandEvaExit.ResolveKerbalArg("Jeb", null, out string err);
            Assert.Null(r);
            Assert.Equal("no-crew", err);
        }

        // ----- DecideEvaExitCompletion -----

        [Fact]
        public void Complete_AllConjuncts_NoRelease_NoDwell_Ok()
        {
            Assert.Equal(EvaExitCompletionDecision.CompleteOk,
                TestCommandEvaExit.DecideEvaExitCompletion(
                    5.0, evaVesselExists: true, evaVesselIsActive: true, sceneSettled: true,
                    releaseRequested: false, releaseApplied: false, settleElapsed: true, Budget));
        }

        [Fact]
        public void Complete_NotExists_StillWaiting()
        {
            Assert.Equal(EvaExitCompletionDecision.StillWaiting,
                TestCommandEvaExit.DecideEvaExitCompletion(
                    5.0, false, true, true, false, false, true, Budget));
        }

        [Fact]
        public void Complete_NotActive_StillWaiting()
        {
            // Exists but the auto-switch has not made it the active vessel yet.
            Assert.Equal(EvaExitCompletionDecision.StillWaiting,
                TestCommandEvaExit.DecideEvaExitCompletion(
                    5.0, true, false, true, false, false, true, Budget));
        }

        [Fact]
        public void Complete_NotSettled_StillWaiting()
        {
            Assert.Equal(EvaExitCompletionDecision.StillWaiting,
                TestCommandEvaExit.DecideEvaExitCompletion(
                    5.0, true, true, false, false, false, true, Budget));
        }

        [Fact]
        public void Complete_ReleaseRequestedNotApplied_StillWaiting()
        {
            Assert.Equal(EvaExitCompletionDecision.StillWaiting,
                TestCommandEvaExit.DecideEvaExitCompletion(
                    5.0, true, true, true, releaseRequested: true, releaseApplied: false,
                    settleElapsed: true, Budget));
        }

        [Fact]
        public void Complete_ReleaseRequestedAndApplied_Ok()
        {
            Assert.Equal(EvaExitCompletionDecision.CompleteOk,
                TestCommandEvaExit.DecideEvaExitCompletion(
                    5.0, true, true, true, releaseRequested: true, releaseApplied: true,
                    settleElapsed: true, Budget));
        }

        [Fact]
        public void Complete_DwellNotElapsed_StillWaiting()
        {
            // F7: the base conjuncts hold but the settleSeconds dwell has not elapsed -> hold
            // the head so Parsek's deferred EVA auto-record arms before the next FIFO command.
            Assert.Equal(EvaExitCompletionDecision.StillWaiting,
                TestCommandEvaExit.DecideEvaExitCompletion(
                    5.0, true, true, true, false, false, settleElapsed: false, Budget));
        }

        [Fact]
        public void Complete_BudgetExpired_ExitTimeout()
        {
            Assert.Equal(EvaExitCompletionDecision.ExitTimeout,
                TestCommandEvaExit.DecideEvaExitCompletion(
                    Budget, false, false, true, false, false, true, Budget));
        }

        [Fact]
        public void Complete_PositiveBeatsBudget()
        {
            // Complete even past budget: a settled active EVA vessel is OK, not a timeout.
            Assert.Equal(EvaExitCompletionDecision.CompleteOk,
                TestCommandEvaExit.DecideEvaExitCompletion(
                    Budget + 10.0, true, true, true, false, false, true, Budget));
        }

        // ----- BuildCompletePayload -----

        [Fact]
        public void Payload_CarriesKerbalPidReleased()
        {
            var p = TestCommandEvaExit.BuildCompletePayload("Bob Kerman", 12345u, released: true);
            Assert.Equal("Bob Kerman", Val(p, "kerbal"));
            Assert.Equal("12345", Val(p, "evaPid"));
            Assert.Equal("true", Val(p, "released"));
            Assert.Equal(new[] { "kerbal", "evaPid", "released" }, p.Select(kv => kv.Key).ToArray());
        }

        [Fact]
        public void Payload_NullKerbal_EmptyString_ReleasedFalse()
        {
            var p = TestCommandEvaExit.BuildCompletePayload(null, 0u, released: false);
            Assert.Equal(string.Empty, Val(p, "kerbal"));
            Assert.Equal("0", Val(p, "evaPid"));
            Assert.Equal("false", Val(p, "released"));
        }
    }
}
