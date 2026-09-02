using System.Collections.Generic;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure decision cells for the DeleteRecording seam verb (AUTOMATION-GAP-KSC-TABLE-DELETE):
    /// the REQUIRED index arg, the route the table's scene branch takes, the by-reference
    /// read-back that stands behind the verdict, the terminal payload, and the dispatch
    /// guards the logistics pair shares with it.
    ///
    /// The read-back cell is the load-bearing one: every production delete the verb routes
    /// to is void and refuses with a Warn, so a verdict that trusted the call returning
    /// would report OK on a row that is still there.
    /// </summary>
    public class TestCommandDeleteRecordingTests
    {
        private static ParsedCommand Cmd(string line)
            => TestCommandParser.ParseLine(line, 1);

        private static DispatchState Loaded(TestCommandScene scene) => new DispatchState
        {
            Scene = scene,
            GameLoaded = true,
            SettingsPresent = true,
        };

        [Theory]
        [InlineData("0", true, 0)]
        [InlineData("1", true, 1)]
        [InlineData("12", true, 12)]
        [InlineData(null, false, -1)]    // REQUIRED: no auto-select sentinel for a delete
        [InlineData("", false, -1)]
        [InlineData("-1", false, -1)]
        [InlineData("1.0", false, -1)]   // an index is an integer token, never coerced
        [InlineData("1,0", false, -1)]   // no locale comma
        [InlineData(" 1", false, -1)]    // NumberStyles.None: no whitespace tolerance
        [InlineData("last", false, -1)]
        public void Index_arg_is_required_and_a_nonnegative_integer(string raw, bool ok, int want)
        {
            bool parsed = TestCommandDeleteRecording.TryParseIndexArg(raw, out int index);
            Assert.Equal(ok, parsed);
            if (ok)
                Assert.Equal(want, index);
        }

        // The route is asserted through its grep-stable token because the internal enum
        // cannot appear in a public xUnit theory signature (the dispatch-state cells make
        // the same choice for the requirement enum).
        [Theory]
        [InlineData(false, false, "store")]
        [InlineData(false, true, "store")]              // KSC: ghost-only or not, the store branch
        [InlineData(true, true, "flight-ghost-only")]   // the table's "X" button
        [InlineData(true, false, "flight-full")]        // ParsekFlight.DeleteRecording behind its guard
        public void Route_mirrors_the_tables_scene_branch(bool flightHost, bool ghostOnly, string token)
        {
            Assert.Equal(token, TestCommandDeleteRecording.RouteToken(
                TestCommandDeleteRecording.DecideRoute(flightHost, ghostOnly)));
        }

        [Fact]
        public void Route_tokens_are_distinct()
        {
            var tokens = new HashSet<string>
            {
                TestCommandDeleteRecording.RouteToken(DeleteRecordingRoute.Store),
                TestCommandDeleteRecording.RouteToken(DeleteRecordingRoute.FlightGhostOnly),
                TestCommandDeleteRecording.RouteToken(DeleteRecordingRoute.FlightFull),
            };
            Assert.Equal(3, tokens.Count);
        }

        [Fact]
        public void Read_back_is_by_reference_not_by_index_or_id()
        {
            var a = new Recording { RecordingId = "dup", VesselName = "A" };
            var b = new Recording { RecordingId = "dup", VesselName = "B" };
            var c = new Recording { RecordingId = "c", VesselName = "C" };
            var after = new List<Recording> { a, c };

            // b shares a's id and a's old index now names c: only the reference tells the truth.
            Assert.False(TestCommandDeleteRecording.IsStillPresent(after, b));
            Assert.True(TestCommandDeleteRecording.IsStillPresent(after, a));
            Assert.True(TestCommandDeleteRecording.IsStillPresent(after, c));
            Assert.False(TestCommandDeleteRecording.IsStillPresent(null, a));
            Assert.False(TestCommandDeleteRecording.IsStillPresent(after, null));
        }

        [Fact]
        public void Complete_payload_carries_the_handle_the_route_and_both_counts()
        {
            var payload = TestCommandDeleteRecording.BuildCompletePayload(
                1, "b3cd21d7", "Kerbal X Debris", false, "store", 9, 8);
            var map = new Dictionary<string, string>();
            foreach (var kv in payload) map[kv.Key] = kv.Value;

            Assert.Equal("1", map["index"]);
            Assert.Equal("b3cd21d7", map["recId"]);
            Assert.Equal("Kerbal X Debris", map["vessel"]);
            Assert.Equal("false", map["ghostOnly"]);
            Assert.Equal("store", map["route"]);
            Assert.Equal("9", map["committedBefore"]);
            Assert.Equal("8", map["committedAfter"]);
            Assert.Equal(7, payload.Count);
        }

        // ----- Dispatch: RequiresGameLoaded plus the logistics pair's guard pair -----

        [Fact]
        public void Dispatch_AtSpaceCenterWithGameLoaded_Executes()
        {
            var r = TestCommandDispatcher.DecideDispatch(
                Cmd("id=1 cmd=DeleteRecording index=1"), Loaded(TestCommandScene.SpaceCenter));
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Fact]
        public void Dispatch_InFlightWithRecorderLive_StillExecutes()
        {
            // No recording-active guard: the table offers the delete with a recorder live.
            var st = Loaded(TestCommandScene.Flight);
            st.Recording = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=DeleteRecording index=1"), st);
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Fact]
        public void Dispatch_GameNotLoaded_Defers()
        {
            var r = TestCommandDispatcher.DecideDispatch(
                Cmd("id=1 cmd=DeleteRecording index=1"),
                new DispatchState { Scene = TestCommandScene.MainMenu });
            Assert.Equal(DispatchDecision.Defer, r.Decision);
            Assert.Equal("game-not-loaded", r.Reason);
        }

        [Fact]
        public void Dispatch_LoadInFlight_Rejects()
        {
            var st = Loaded(TestCommandScene.SpaceCenter);
            st.LoadInFlight = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=DeleteRecording index=1"), st);
            Assert.Equal(DispatchDecision.Reject, r.Decision);
            Assert.Equal("load-in-flight", r.Reason);
        }

        [Fact]
        public void Dispatch_MergeJournalInFlight_Rejects()
        {
            var st = Loaded(TestCommandScene.SpaceCenter);
            st.MergeJournalInFlight = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=DeleteRecording index=1"), st);
            Assert.Equal(DispatchDecision.Reject, r.Decision);
            Assert.Equal("merge-journal-in-flight", r.Reason);
        }

        [Fact]
        public void Dispatch_LoadFirst_WhenBothGuardsFire()
        {
            // Load-first, matching the SealSlot / RouteCommand rows verbatim.
            var st = Loaded(TestCommandScene.SpaceCenter);
            st.LoadInFlight = true;
            st.MergeJournalInFlight = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=DeleteRecording index=1"), st);
            Assert.Equal(DispatchDecision.Reject, r.Decision);
            Assert.Equal("load-in-flight", r.Reason);
        }
    }
}
