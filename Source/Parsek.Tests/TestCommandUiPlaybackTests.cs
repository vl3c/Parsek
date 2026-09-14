using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for <c>UiAction op=playback</c>, the seam half of the GUI-P1
    /// per-recording playback tick box.
    ///
    /// <para>Four properties carry the weight. (1) <c>state=</c> is REQUIRED and its two
    /// refusals are DISTINCT, because the op is driven both ways by design and a defaulted
    /// direction would silently run the opposite half of a lane's check. (2) The selection
    /// is all-or-one and a NAMED id that matches nothing is a REJECTED rather than a
    /// cheerful "0 changed" - the one answer a lane cannot act on. (3) The read-back counts
    /// AGREEING against CONSIDERED and never against CHANGED, so an already-correct
    /// recording is a no-op and not a failure. (4) The write goes through
    /// <c>RecordingStore.SetRecordingPlaybackEnabled</c>, THE single writer, so the log line
    /// and the <c>RecordingPlaybackEnabledChanged</c> raise happen - the last of which is
    /// what brings the Tracking Station's quarter-second tick forward to the same frame.</para>
    ///
    /// <para><c>Sequential</c> because the writer cells touch the static
    /// <c>RecordingStore</c> event and <c>ParsekLog</c>'s sink.</para>
    /// </summary>
    [Collection("Sequential")]
    public class TestCommandUiPlaybackTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public TestCommandUiPlaybackTests()
        {
            RecordingStore.ResetForTesting();
            RecordingStore.ResetCommittedListNotificationsForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            RecordingStore.ResetCommittedListNotificationsForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ----- op vocabulary -----

        [Fact]
        public void ThePlaybackOpParsesAndRoundTrips_AndNamesNoWindow()
        {
            Assert.True(TestCommandUiAction.TryParseOp(
                "playback", out UiActionOp op, out string reject));
            Assert.Equal(UiActionOp.Playback, op);
            Assert.Null(reject);
            Assert.Equal("playback", TestCommandUiAction.OpToken(op));
            // No `window=` in the grammar: the flag lives on the Recording, and the
            // surfaces it gates are drawn by three hosts none of which owns it. Mirrored by
            // hlib.UIACTION_OPS_NEEDING_WINDOW, which a Python cell reads out of this body.
            Assert.False(TestCommandUiAction.OpNeedsWindow(UiActionOp.Playback));
            // TWO-PHASE (it changes drawn state) but NOT host-gated (the read-back is a
            // field on the Recording, which a hidden Parsek surface cannot fake).
            Assert.True(TestCommandUiAction.OpIsTwoPhase(UiActionOp.Playback));
            Assert.False(TestCommandUiAction.SettleChecksHostShowUi(UiActionOp.Playback));
        }

        // ----- state= -----

        [Fact]
        public void MissingState_IsItsOwnReject_NotADefaultDirection()
        {
            Assert.False(TestCommandUiAction.TryParsePlaybackState(
                null, out bool enabled, out string reason));
            Assert.False(enabled);
            Assert.Equal(TestCommandUiAction.StateArgMissingReason, reason);
            Assert.Equal("state-arg-missing", reason);
        }

        [Theory]
        [InlineData("true", true)]
        [InlineData("false", false)]
        public void BothStateTokensParse(string raw, bool expected)
        {
            Assert.True(TestCommandUiAction.TryParsePlaybackState(
                raw, out bool enabled, out string reason));
            Assert.Equal(expected, enabled);
            Assert.Null(reason);
        }

        [Theory]
        [InlineData("True")]     // case-sensitive, the LoadGame scene= rule
        [InlineData("FALSE")]
        [InlineData("on")]
        [InlineData("1")]
        [InlineData("")]
        public void AnyOtherState_IsInvalid_AndReusesTheOneStateReason(string raw)
        {
            Assert.False(TestCommandUiAction.TryParsePlaybackState(raw, out _, out string r));
            // REUSED rather than a second spelling: `state=` is ONE arg key with one closed
            // vocabulary across op=expand and op=playback, so a `playback-state-arg-invalid`
            // would be a second thing for a spec author to learn about the same refusal.
            Assert.Equal(TestCommandUiState.StateArgInvalidReason, r);
            Assert.Equal("state-arg-invalid", r);
        }

        // ----- recording= (the all-vs-single selection) -----

        [Fact]
        public void NoRecordingArg_SelectsEveryRecording_AndEchoesTheAllToken()
        {
            TestCommandUiAction.ResolvePlaybackSelection(
                null, out string id, out string echo);
            Assert.Null(id);
            Assert.Equal("all", echo);
            Assert.Equal(TestCommandUiAction.PlaybackAllRecordingsToken, echo);

            var set = Build("a", "b", "c");
            List<Recording> targets = TestCommandUiAction.SelectPlaybackTargets(set, id);
            Assert.Equal(new[] { "a", "b", "c" },
                         targets.Select(r => r.RecordingId).ToArray());
        }

        [Fact]
        public void ANamedRecording_SelectsExactlyThatOne()
        {
            TestCommandUiAction.ResolvePlaybackSelection("b", out string id, out string echo);
            Assert.Equal("b", id);
            Assert.Equal("b", echo);

            List<Recording> targets =
                TestCommandUiAction.SelectPlaybackTargets(Build("a", "b", "c"), id);
            Assert.Single(targets);
            Assert.Equal("b", targets[0].RecordingId);
        }

        [Fact]
        public void AnEmptyRecordingArg_ReadsAsAbsent()
        {
            // The wire carries `recording=` with nothing after it for both an omitted arg
            // and an empty one, so refusing one and not the other would be a distinction no
            // spec author can act on.
            foreach (string raw in new[] { "", "   " })
            {
                TestCommandUiAction.ResolvePlaybackSelection(
                    raw, out string id, out string echo);
                Assert.Null(id);
                Assert.Equal("all", echo);
            }
        }

        [Fact]
        public void AnUnknownRecordingId_YieldsNull_NotAnEmptySelection()
        {
            // NULL is the applier's `recording-unknown` signal. An empty LIST would be
            // indistinguishable from an empty save and would answer OK changed=0 total=0
            // over a typo, which is the one answer a lane cannot act on.
            Assert.Null(TestCommandUiAction.SelectPlaybackTargets(Build("a", "b"), "zz"));
            Assert.Null(TestCommandUiAction.SelectPlaybackTargets(Build(), "a"));
            Assert.Null(TestCommandUiAction.SelectPlaybackTargets(null, "a"));
            // The id match is ORDINAL: a RecordingId is a generated token, not display text.
            Assert.Null(TestCommandUiAction.SelectPlaybackTargets(Build("abc"), "ABC"));
        }

        [Fact]
        public void AnEmptySave_IsAnEmptyAllSelection_NeverNull()
        {
            Assert.Empty(TestCommandUiAction.SelectPlaybackTargets(Build(), null));
            Assert.Empty(TestCommandUiAction.SelectPlaybackTargets(null, null));
        }

        // ----- the read-back predicate -----

        [Fact]
        public void TheReadBackCountsAgreeingAgainstConsidered_NotAgainstChanged()
        {
            var set = Build("a", "b", "c");
            set[1].PlaybackEnabled = false;   // already in the requested state

            // Requesting `false` over the three: one is already right, so a `changed`-based
            // predicate would report 2 of 3 and ERROR on a save that ends up correct.
            for (int i = 0; i < set.Count; i++) set[i].PlaybackEnabled = false;
            Assert.Equal(3, TestCommandUiAction.CountPlaybackAgreeing(set, false));
            Assert.True(TestCommandUiAction.PlaybackAppliedToEveryConsidered(3, 3));
        }

        [Fact]
        public void OneDisagreeingRecording_FailsTheReadBack()
        {
            var set = Build("a", "b", "c");
            for (int i = 0; i < set.Count; i++) set[i].PlaybackEnabled = false;
            set[2].PlaybackEnabled = true;    // a window's draw put it back up
            int agreeing = TestCommandUiAction.CountPlaybackAgreeing(set, false);
            Assert.Equal(2, agreeing);
            Assert.False(
                TestCommandUiAction.PlaybackAppliedToEveryConsidered(agreeing, set.Count));
        }

        [Fact]
        public void AnEmptyConsideredSet_IsApplied()
        {
            // A save with no committed recordings on an all-recordings flip. Erroring would
            // report a Parsek defect for a fixture fact, and `total=0` already says what
            // happened.
            Assert.True(TestCommandUiAction.PlaybackAppliedToEveryConsidered(0, 0));
            Assert.Equal(0, TestCommandUiAction.CountPlaybackAgreeing(null, true));
        }

        [Fact]
        public void AVanishedNamedRecording_FailsTheReadBack()
        {
            // The settle re-resolves by ID, so a recording deleted between the write and
            // the drawn frame reaches the predicate as 0 agreeing over 1 considered.
            Assert.False(TestCommandUiAction.PlaybackAppliedToEveryConsidered(0, 1));
        }

        // ----- the payload -----

        [Fact]
        public void ThePayloadCarriesTheFiveKeys_InOrder()
        {
            List<KeyValuePair<string, string>> p =
                TestCommandUiAction.BuildPlaybackPayload("all", false, 4, 7);
            Assert.Equal(new[] { "op", "recording", "state", "changed", "total" },
                         p.Select(kv => kv.Key).ToArray());
            Assert.Equal("playback", Value(p, "op"));
            Assert.Equal("all", Value(p, "recording"));
            Assert.Equal("false", Value(p, "state"));
            Assert.Equal("4", Value(p, "changed"));
            Assert.Equal("7", Value(p, "total"));
        }

        [Fact]
        public void ThePayloadEchoesANamedRecording_AndFallsBackToTheAllSentinel()
        {
            Assert.Equal("rec_17", Value(
                TestCommandUiAction.BuildPlaybackPayload("rec_17", true, 1, 1), "recording"));
            // A null / empty token can only come from a hand-built call, and the sentinel is
            // the safe answer: a trailing `recording=` on the wire reads as a truncated line.
            Assert.Equal("all", Value(
                TestCommandUiAction.BuildPlaybackPayload(null, true, 0, 0), "recording"));
        }

        [Fact]
        public void ThePayloadCountsAreInvariantCulture()
        {
            // The harness regexes these, so a ro-RO / de-DE host must not print a grouped
            // or comma-decimal integer.
            var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    CultureInfo.GetCultureInfo("de-DE");
                List<KeyValuePair<string, string>> p =
                    TestCommandUiAction.BuildPlaybackPayload("all", true, 1234, 5678);
                Assert.Equal("1234", Value(p, "changed"));
                Assert.Equal("5678", Value(p, "total"));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        // ----- the write routes through the single writer -----

        [Fact]
        public void TheOpsWriteRoutesThroughTheSingleWriter_WhichNotifiesAndLogs()
        {
            // The applier loops RecordingStore.SetRecordingPlaybackEnabled rather than
            // assigning rec.PlaybackEnabled, so the event fires and the store's own log
            // line is written. This cell drives the same loop over the same writer: a
            // direct assignment would leave `events` empty and no log line behind.
            var set = Build("a", "b", "c");
            set[1].PlaybackEnabled = false;   // a mixed set before the flip
            var events = new List<(string id, bool enabled)>();
            RecordingStore.RecordingPlaybackEnabledChanged +=
                (r, enabled) => events.Add((r?.RecordingId, enabled));

            int changed = 0;
            try
            {
                List<Recording> considered =
                    TestCommandUiAction.SelectPlaybackTargets(set, null);
                for (int i = 0; i < considered.Count; i++)
                    if (RecordingStore.SetRecordingPlaybackEnabled(considered[i], false))
                        changed++;
            }
            finally
            {
                RecordingStore.ResetCommittedListNotificationsForTesting();
            }

            // The already-off member is a NO-OP, not a failure: `changed` counts real flips
            // while the read-back still sees every recording agreeing.
            Assert.Equal(2, changed);
            Assert.Equal(2, events.Count);
            Assert.Equal(new[] { "a", "c" }, events.Select(e => e.id).ToArray());
            Assert.All(events, e => Assert.False(e.enabled));
            Assert.Equal(3, TestCommandUiAction.CountPlaybackAgreeing(set, false));
            Assert.True(TestCommandUiAction.PlaybackAppliedToEveryConsidered(3, set.Count));
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]")
                && l.Contains("Recording playback disabled") && l.Contains("a"));
        }

        // ----- helpers -----

        private static List<Recording> Build(params string[] ids)
        {
            var list = new List<Recording>();
            foreach (string id in ids)
                list.Add(new Recording { RecordingId = id, VesselName = "Probe " + id });
            return list;
        }

        private static string Value(List<KeyValuePair<string, string>> payload, string key)
        {
            foreach (KeyValuePair<string, string> kv in payload)
                if (kv.Key == key) return kv.Value;
            return null;
        }
    }
}
