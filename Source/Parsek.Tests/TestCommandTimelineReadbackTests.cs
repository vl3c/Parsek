using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Cells for <c>ListHandles kind=timeline</c> (TL-1): the closed kind literal, the
    /// count definitions over the REAL <see cref="TimelineBuilder.Build"/> output, the wire
    /// key sequence and the Info lines a spec pins. The load-bearing pair is the phantom
    /// one: the same milestone store and retired row read <c>retiredTwinRows=0</c> through
    /// the fixed build and <c>retiredTwinRows=1</c> through a build that drops the retired
    /// rows (what removing <c>AddRetiredLegacyDuplicateKeys</c> does), so the lane's
    /// assertion separates the two.
    /// </summary>
    [Collection("Sequential")]
    public class TestCommandTimelineReadbackTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public TestCommandTimelineReadbackTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Dictionary<string, string> Map(IEnumerable<KeyValuePair<string, string>> payload)
        {
            var d = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> kv in payload)
                d[kv.Key] = kv.Value;
            return d;
        }

        private static GameStateEvent MilestoneEvent(double ut, string key, string rec = "rec-head")
            => new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.MilestoneAchieved,
                key = key,
                recordingId = rec,
            };

        private static GameAction MilestoneRow(double ut, string id, bool effective = true)
            => new GameAction
            {
                UT = ut,
                Type = GameActionType.MilestoneAchievement,
                Effective = effective,
                MilestoneId = id,
                RecordingId = "rec-tip",
            };

        private static Milestone Store(params GameStateEvent[] events)
            => new Milestone { Committed = true, Epoch = 0, Events = new List<GameStateEvent>(events) };

        // The todo's shape: the copy of Kerbin/Science stays tagged to a visible recording
        // while its ledger row is tombstoned; FirstLaunch's row is kept, so its copy is
        // hidden by the ordinary ELS dedup.
        private static void PhantomState(
            out List<GameAction> els, out List<GameAction> retired, out List<Milestone> store)
        {
            store = new List<Milestone>
            {
                Store(MilestoneEvent(15.12, "FirstLaunch"), MilestoneEvent(346.08, "Kerbin/Science")),
            };
            els = new List<GameAction> { MilestoneRow(15.12, "FirstLaunch") };
            retired = new List<GameAction> { MilestoneRow(346.08, "Kerbin/Science", effective: false) };
        }

        private static TimelineReadback Read(
            List<GameAction> els, List<GameAction> retired, List<Milestone> store,
            bool passRetiredToBuild, Func<GameStateEvent, bool> visible = null)
        {
            Func<GameStateEvent, bool> v = visible ?? (_ => true);
            List<TimelineEntry> entries = TimelineBuilder.Build(
                new List<Recording>(), els, store, v, null,
                passRetiredToBuild ? retired : null);
            return TestCommandTimelineReadback.Count(entries, els, retired, store, v);
        }

        [Fact]
        public void ParseKind_accepts_timeline_and_maps_it_to_its_own_member()
        {
            ListHandlesKind kind;
            string reason;
            Assert.True(TestCommandListHandles.ParseKind("timeline", out kind, out reason));
            Assert.Equal(ListHandlesKind.Timeline, kind);
            Assert.Null(reason);
            Assert.Equal("timeline", TestCommandListHandles.KindToken(kind));
            Assert.False(TestCommandListHandles.ParseKind("Timeline", out kind, out reason));
            Assert.Equal(TestCommandListHandles.KindArgInvalidReason, reason);
        }

        // catches: TIMELINE-PHANTOM-MILESTONE-AFTER-REFLY-OF-LATER-SEGMENT read back
        // through the seam. The fixed build hides the copy; a build that ignores the
        // retired rows draws it, and the read-back says so.
        [Fact]
        public void Phantom_copy_is_counted_and_the_fixed_build_draws_no_row_for_it()
        {
            List<GameAction> els, retired;
            List<Milestone> store;
            PhantomState(out els, out retired, out store);

            TimelineReadback fixedBuild = Read(els, retired, store, passRetiredToBuild: true);
            Assert.Equal(1, fixedBuild.Retired);
            Assert.Equal(1, fixedBuild.RetiredWithTwin);
            Assert.Equal(1, fixedBuild.RetiredTwinCopies);
            Assert.Equal(0, fixedBuild.RetiredTwinRows);
            Assert.Equal(0, fixedBuild.LegacyMilestoneRows);
            Assert.Equal(1, fixedBuild.MilestoneRows);
            Assert.Equal(0, fixedBuild.LegacyRows);

            TimelineReadback withoutFix = Read(els, retired, store, passRetiredToBuild: false);
            Assert.Equal(1, withoutFix.RetiredTwinCopies);
            Assert.Equal(1, withoutFix.RetiredTwinRows);
            Assert.Equal(1, withoutFix.LegacyMilestoneRows);
            Assert.Equal(1, withoutFix.LegacyRows);

            RetiredTimelineRow row = fixedBuild.RetiredRows.Single();
            Assert.Equal(GameActionType.MilestoneAchievement, row.Type);
            Assert.Equal("Kerbin/Science", row.Subject);
            Assert.Equal(1, row.TwinCopies);
            Assert.Equal(0, row.TwinRows);
        }

        // The only retirable row type with a legacy twin since the 2026-10-19 rulings: a
        // building a flight destroyed (a loss row). Same mechanism, same answer.
        [Fact]
        public void Facility_destruction_twin_reads_the_same_way()
        {
            var store = new List<Milestone>
            {
                Store(new GameStateEvent
                {
                    ut = 120.5, eventType = GameStateEventType.BuildingDestroyed,
                    key = "SpaceCenter/LaunchPad/Facility/building", recordingId = "rec-head",
                }),
            };
            var retired = new List<GameAction>
            {
                new GameAction
                {
                    UT = 120.5, Type = GameActionType.FacilityDestruction, Effective = false,
                    FacilityId = "SpaceCenter/LaunchPad/Facility/building", RecordingId = "rec-tip",
                },
            };
            var els = new List<GameAction>();

            TimelineReadback fixedBuild = Read(els, retired, store, passRetiredToBuild: true);
            Assert.Equal(1, fixedBuild.RetiredTwinCopies);
            Assert.Equal(0, fixedBuild.RetiredTwinRows);
            Assert.Equal(0, fixedBuild.LegacyRows);

            TimelineReadback withoutFix = Read(els, retired, store, passRetiredToBuild: false);
            Assert.Equal(1, withoutFix.RetiredTwinRows);
            Assert.Equal(0, withoutFix.LegacyMilestoneRows);
            Assert.Equal("SpaceCenter/LaunchPad/Facility/building", fixedBuild.RetiredRows[0].Subject);
        }

        [Fact]
        public void A_copy_an_effective_row_already_hides_is_not_a_phantom_candidate()
        {
            List<GameAction> els, retired;
            List<Milestone> store;
            PhantomState(out els, out retired, out store);
            // A re-earned row at the same identity keeps the copy hidden whatever the fix.
            els.Add(MilestoneRow(346.08, "Kerbin/Science"));

            TimelineReadback r = Read(els, retired, store, passRetiredToBuild: false);
            Assert.Equal(0, r.RetiredTwinCopies);
            Assert.Equal(0, r.RetiredTwinRows);
        }

        [Fact]
        public void A_copy_tagged_to_a_hidden_recording_is_not_a_phantom_candidate()
        {
            List<GameAction> els, retired;
            List<Milestone> store;
            PhantomState(out els, out retired, out store);

            TimelineReadback r = Read(els, retired, store, passRetiredToBuild: false,
                visible: e => e.recordingId != "rec-head");
            Assert.Equal(0, r.RetiredTwinCopies);
            Assert.Equal(0, r.RetiredTwinRows);
        }

        [Fact]
        public void Retired_rows_without_a_twin_are_counted_but_carry_no_subject()
        {
            var retired = new List<GameAction>
            {
                new GameAction
                {
                    UT = 50, Type = GameActionType.KerbalAssignment, Effective = false,
                    RecordingId = "rec-tip", KerbalName = "Jebediah Kerman",
                },
            };
            TimelineReadback r = TestCommandTimelineReadback.Count(
                new List<TimelineEntry>(), new List<GameAction>(), retired,
                new List<Milestone>(), _ => true);
            Assert.Equal(1, r.Retired);
            Assert.Equal(0, r.RetiredWithTwin);
            Assert.Equal(string.Empty, r.RetiredRows[0].Subject);
        }

        [Fact]
        public void Payload_emits_the_documented_key_sequence_and_the_log_lines_read_it()
        {
            List<GameAction> els, retired;
            List<Milestone> store;
            PhantomState(out els, out retired, out store);
            List<KeyValuePair<string, string>> payload =
                TestCommandTimelineReadback.BuildPayload(Read(els, retired, store, true));

            Assert.Equal(new[]
            {
                "kind", "count", "truncated", "recordingRows", "actionRows", "legacyRows",
                "milestoneRows", "legacyMilestoneRows", "retired", "retiredWithTwin",
                "retiredTwinCopies", "retiredTwinRows",
                "retired0type", "retired0subject", "retired0ut", "retired0twinCopies",
                "retired0twinRows",
            }, payload.Select(kv => kv.Key).ToArray());
            Dictionary<string, string> m = Map(payload);
            Assert.Equal("timeline", m["kind"]);
            Assert.Equal("1", m["count"]);
            Assert.Equal("false", m["truncated"]);

            List<string> lines = TestCommandTimelineReadback.LogLines(payload, ListHandlesKind.Timeline);
            Assert.Equal(2, lines.Count);
            Assert.Equal(
                "listhandles timeline count=1 recordingRows=0 actionRows=1 legacyRows=0 " +
                "milestoneRows=1 legacyMilestoneRows=0 retired=1 retiredWithTwin=1 " +
                "retiredTwinCopies=1 retiredTwinRows=0", lines[0]);
            Assert.Equal(
                "listhandles timeline retired index=0 type=MilestoneAchievement " +
                "subject=Kerbin/Science ut=346.08 twinCopies=1 twinRows=0", lines[1]);
            Assert.Empty(TestCommandTimelineReadback.LogLines(payload, ListHandlesKind.Routes));
            Assert.Equal("1", TestCommandListHandles.CountFromPayload(payload, ListHandlesKind.Timeline));
        }

        [Fact]
        public void Retired_rows_are_capped_and_the_cut_raises_truncated()
        {
            var retired = new List<GameAction>();
            for (int i = 0; i < TestCommandTimelineReadback.MaxRetiredRows + 1; i++)
                retired.Add(MilestoneRow(100 + i, "M" + i, effective: false));
            TimelineReadback r = TestCommandTimelineReadback.Count(
                new List<TimelineEntry>(), new List<GameAction>(), retired,
                new List<Milestone>(), _ => true);
            Dictionary<string, string> m = Map(TestCommandTimelineReadback.BuildPayload(r));
            Assert.Equal("true", m["truncated"]);
            Assert.Equal("17", m["retired"]);
            Assert.True(m.ContainsKey("retired15type"));
            Assert.False(m.ContainsKey("retired16type"));
        }

        [Fact]
        public void Empty_state_is_a_legitimate_answer()
        {
            TimelineReadback r = TestCommandTimelineReadback.Count(null, null, null, null, null);
            Dictionary<string, string> m = Map(TestCommandTimelineReadback.BuildPayload(r));
            Assert.Equal("0", m["count"]);
            Assert.Equal("0", m["retired"]);
            Assert.Equal("0", m["retiredTwinRows"]);
        }

        [Fact]
        public void Uts_are_invariant_culture()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var retired = new List<GameAction> { MilestoneRow(346.08, "Kerbin/Science", false) };
                TimelineReadback r = TestCommandTimelineReadback.Count(
                    new List<TimelineEntry>(), new List<GameAction>(), retired,
                    new List<Milestone>(), _ => true);
                Dictionary<string, string> m = Map(TestCommandTimelineReadback.BuildPayload(r));
                Assert.Equal("346.08", m["retired0ut"]);
                Assert.Equal("Kerbin/Science", m["retired0subject"]);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }
    }
}
