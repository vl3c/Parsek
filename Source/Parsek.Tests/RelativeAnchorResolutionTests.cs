using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="RelativeAnchorResolution"/>, the pure decision
    /// helper used by relative-frame ghost playback to decide whether the
    /// recorded anchor pid resolves to a live vessel or whether the ghost
    /// must be retired (hidden) for the duration of the relative section.
    ///
    /// <para>
    /// Bug B (2026-04-26): a Re-Fly rewind erased the originally recorded
    /// anchor vessel (Kerbal X Probe, pid=3151978247). Post-rewind, the
    /// playback path resolved <c>FindVesselByPid</c> to null and "froze" a
    /// freshly spawned ghost in place -- but the ghost had no last position,
    /// so it rendered at world origin (0,0,0) with a bogus reported distance.
    /// The fix retires the ghost (hides it via <c>SetActive(false)</c>) and
    /// emits a one-shot per-(recording, anchor) WARN tagged
    /// <c>relative-anchor-retired</c>. The frozen-at-origin failure mode is
    /// unreachable.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class RelativeAnchorResolutionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RelativeAnchorResolutionTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        #region Decide

        [Fact]
        public void Decide_ResolverReturnsTrue_OutcomeResolved()
        {
            var outcome = RelativeAnchorResolution.Decide(
                anchorPid: 42u,
                resolver: pid => pid == 42u);

            Assert.Equal(RelativeAnchorResolution.Outcome.Resolved, outcome);
        }

        [Fact]
        public void Decide_ResolverReturnsFalse_OutcomeRetired()
        {
            // Bug B repro: recorded anchorPid=3151978247 was destroyed by a
            // Re-Fly rewind, so post-rewind the resolver returns false.
            var outcome = RelativeAnchorResolution.Decide(
                anchorPid: 3151978247u,
                resolver: _ => false);

            Assert.Equal(RelativeAnchorResolution.Outcome.Retired, outcome);
        }

        [Fact]
        public void Decide_AnchorPidZero_OutcomeRetired()
        {
            // anchorPid==0 sentinel from a corrupted track section: no
            // vessel can ever match, so the resolver is never invoked.
            bool resolverCalled = false;
            var outcome = RelativeAnchorResolution.Decide(
                anchorPid: 0u,
                resolver: _ => { resolverCalled = true; return true; });

            Assert.Equal(RelativeAnchorResolution.Outcome.Retired, outcome);
            Assert.False(resolverCalled, "resolver must short-circuit on pid=0");
        }

        [Fact]
        public void Decide_ResolverNull_OutcomeRetired()
        {
            // Defensive null handling: callers in test fixtures may not have
            // a resolver wired up.
            var outcome = RelativeAnchorResolution.Decide(
                anchorPid: 42u,
                resolver: null);

            Assert.Equal(RelativeAnchorResolution.Outcome.Retired, outcome);
        }

        #endregion

        #region DedupeKey

        [Fact]
        public void DedupeKey_SameRecordingSameAnchor_ProducesSameKey()
        {
            long a = RelativeAnchorResolution.DedupeKey(9, 3151978247u);
            long b = RelativeAnchorResolution.DedupeKey(9, 3151978247u);
            Assert.Equal(a, b);
        }

        [Fact]
        public void DedupeKey_DifferentRecording_ProducesDifferentKey()
        {
            // Two recordings sharing the same anchor pid must dedupe
            // independently -- both should log on first encounter.
            long a = RelativeAnchorResolution.DedupeKey(9, 3151978247u);
            long b = RelativeAnchorResolution.DedupeKey(10, 3151978247u);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void DedupeKey_DifferentAnchor_ProducesDifferentKey()
        {
            long a = RelativeAnchorResolution.DedupeKey(9, 3151978247u);
            long b = RelativeAnchorResolution.DedupeKey(9, 2708531065u);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void DedupeKey_HandlesMaxRecordingIndexAndPid()
        {
            // Verifies no overflow / sign-extension when pid has the high
            // bit set (uint pids commonly use the 31st bit) and the index
            // approaches int.MaxValue.
            long a = RelativeAnchorResolution.DedupeKey(int.MaxValue, uint.MaxValue);
            long b = RelativeAnchorResolution.DedupeKey(0, uint.MaxValue);
            long c = RelativeAnchorResolution.DedupeKey(int.MaxValue, 0u);
            Assert.NotEqual(a, b);
            Assert.NotEqual(a, c);
            Assert.NotEqual(b, c);
        }

        #endregion

        #region FormatRetiredMessage

        [Fact]
        public void FormatRetiredMessage_IncludesGreppableKeyword()
        {
            string msg = RelativeAnchorResolution.FormatRetiredMessage(
                recordingIndex: 9,
                vesselName: "Kerbal X",
                anchorPid: 3151978247u,
                callsite: "InterpolateAndPositionRelative");

            Assert.Contains("relative-anchor-retired", msg);
        }

        [Fact]
        public void FormatRetiredMessage_IncludesIdentifyingFields()
        {
            string msg = RelativeAnchorResolution.FormatRetiredMessage(
                recordingIndex: 9,
                vesselName: "Kerbal X",
                anchorPid: 3151978247u,
                callsite: "InterpolateAndPositionRelative");

            Assert.Contains("recording=#9", msg);
            Assert.Contains("vessel=\"Kerbal X\"", msg);
            Assert.Contains("anchorPid=3151978247", msg);
            Assert.Contains("callsite=InterpolateAndPositionRelative", msg);
        }

        [Fact]
        public void FormatRetiredMessage_NullVesselName_RendersUnknownPlaceholder()
        {
            string msg = RelativeAnchorResolution.FormatRetiredMessage(
                recordingIndex: 0,
                vesselName: null,
                anchorPid: 1u,
                callsite: "PositionGhostRelativeAt");

            Assert.Contains("vessel=\"(unknown)\"", msg);
        }

        [Fact]
        public void FormatRetiredMessage_MentionsRewindRootCause()
        {
            // Players reading KSP.log should be told (gently) that a Re-Fly
            // rewind is the typical root cause -- this anchors the message
            // to the bug B narrative and helps support triage.
            string msg = RelativeAnchorResolution.FormatRetiredMessage(
                recordingIndex: 9,
                vesselName: "Kerbal X",
                anchorPid: 3151978247u,
                callsite: "InterpolateAndPositionRelative");

            Assert.Contains("Re-Fly rewind", msg);
        }

        #endregion

        #region Log-assertion: scenario coverage

        [Fact]
        public void RetiredMessage_LoggedToParsekLog_AppearsInWarnSink()
        {
            // Mirrors the production call site: emit FormatRetiredMessage
            // through ParsekLog.Warn under the [Anchor] tag, verify the
            // resulting line carries the greppable keyword and the [WARN]
            // level. RewindLoggingTests follows the same harness pattern.
            string formatted = RelativeAnchorResolution.FormatRetiredMessage(
                recordingIndex: 9,
                vesselName: "Kerbal X",
                anchorPid: 3151978247u,
                callsite: "InterpolateAndPositionRelative");
            ParsekLog.Warn("Anchor", formatted);

            Assert.Contains(logLines,
                l => l.Contains("[WARN]") && l.Contains("[Anchor]") && l.Contains("relative-anchor-retired"));
        }

        [Fact]
        public void Decide_RewindScenario_AnchorAliveStillResolves()
        {
            // Negative test: after the rewind, the recording's anchor vessel
            // is alive in the post-rewind FlightGlobals (e.g. a station that
            // pre-existed the rewind point). Decide must return Resolved so
            // playback proceeds normally and no spurious retirement WARN is
            // emitted.
            var liveVessels = new HashSet<uint> { 100u, 200u, 42u };
            var outcome = RelativeAnchorResolution.Decide(
                anchorPid: 42u,
                resolver: pid => liveVessels.Contains(pid));

            Assert.Equal(RelativeAnchorResolution.Outcome.Resolved, outcome);
        }

        [Fact]
        public void Decide_RewindScenario_AnchorErasedReturnsRetired_NoFreezePath()
        {
            // Repro for bug B: the anchor pid was destroyed in a Re-Fly
            // rewind so it's not in the live FlightGlobals. The decision is
            // Retired, which the production code translates into
            // SetActive(false). Frozen-at-origin is unreachable: the only
            // two outcomes are Resolved (positioned by anchor) or Retired
            // (hidden).
            var liveVesselsPostRewind = new HashSet<uint> { 100u, 200u };
            var outcome = RelativeAnchorResolution.Decide(
                anchorPid: 3151978247u,
                resolver: pid => liveVesselsPostRewind.Contains(pid));

            Assert.Equal(RelativeAnchorResolution.Outcome.Retired, outcome);
        }

        [Fact]
        public void OutcomeEnum_HasOnlyTwoStates()
        {
            // Defensive: any third outcome would represent a partially
            // positioned ghost (the bug B failure mode). Lock the contract
            // to exactly two values.
            var values = Enum.GetValues(typeof(RelativeAnchorResolution.Outcome));
            Assert.Equal(2, values.Length);
            Assert.Contains(RelativeAnchorResolution.Outcome.Resolved,
                (RelativeAnchorResolution.Outcome[])values);
            Assert.Contains(RelativeAnchorResolution.Outcome.Retired,
                (RelativeAnchorResolution.Outcome[])values);
        }

        #endregion
    }
}
