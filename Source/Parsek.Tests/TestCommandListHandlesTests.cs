using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure decision cells for the R10 <c>ListHandles kind=&lt;family&gt;</c> seam verb: the
    /// REQUIRED closed <c>kind=</c> arg, the exact key SEQUENCE each family emits, the
    /// caps and their truncation signal, the orderings a spec's <c>${handles.rp0}</c>
    /// index depends on, and the slot open/closed mapping.
    ///
    /// The key-sequence cells are the load-bearing ones. A spec names a member by key, so
    /// a reordering or a renamed suffix is not a cosmetic change - it silently stops a
    /// substitution from resolving, and the step that consumes the handle then flies with
    /// an unsubstituted literal.
    /// </summary>
    public class TestCommandListHandlesTests
    {
        private static List<string> Keys(IEnumerable<KeyValuePair<string, string>> payload)
            => payload.Select(kv => kv.Key).ToList();

        private static Dictionary<string, string> Map(
            IEnumerable<KeyValuePair<string, string>> payload)
        {
            var d = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> kv in payload)
                d[kv.Key] = kv.Value;
            return d;
        }

        private static RewindPointRow Rp(string id, double ut, params SlotRow[] slots)
            => new RewindPointRow
            {
                Id = id,
                Ut = ut,
                Provisional = false,
                Corrupted = false,
                Slots = new List<SlotRow>(slots),
            };

        private static SlotRow Slot(string origin, bool open)
            => new SlotRow { OriginChildRecordingId = origin, Open = open };

        private static CommittedRow Rec(string id, string tree, uint pid = 0)
            => new CommittedRow
            {
                RecordingId = id,
                TreeId = tree,
                Pid = pid,
                Name = "V",
                Spawned = false,
                State = MergeState.Immutable,
            };

        // ----- kind= parse -----

        [Fact]
        public void ParseKind_absent_is_its_own_reason()
        {
            ListHandlesKind kind;
            string reason;
            Assert.False(TestCommandListHandles.ParseKind(null, out kind, out reason));
            Assert.Equal(ListHandlesKind.None, kind);
            // Distinct from kind-arg-invalid on purpose: "you did not ask for a family"
            // and "that is not a family" are different spec mistakes.
            Assert.Equal(TestCommandListHandles.KindArgMissingReason, reason);
            Assert.Equal("kind-arg-missing", reason);
        }

        // The parsed kind is checked through its wire token rather than the enum member:
        // ListHandlesKind is internal (the whole seam decision surface is), and an
        // InlineData parameter of an internal type cannot appear on a public test method.
        // The token is the thing the payload echoes anyway, so this pins the reachable
        // half.
        [Theory]
        [InlineData("rewindpoints")]
        [InlineData("committed")]
        [InlineData("active")]
        public void ParseKind_accepts_the_three_lowercase_literals(string raw)
        {
            ListHandlesKind kind;
            string reason;
            Assert.True(TestCommandListHandles.ParseKind(raw, out kind, out reason));
            Assert.NotEqual(ListHandlesKind.None, kind);
            Assert.Null(reason);
            Assert.Equal(raw, TestCommandListHandles.KindToken(kind));
        }

        [Fact]
        public void ParseKind_maps_each_literal_to_its_own_member()
        {
            ListHandlesKind rp, committed, active;
            string reason;
            TestCommandListHandles.ParseKind("rewindpoints", out rp, out reason);
            TestCommandListHandles.ParseKind("committed", out committed, out reason);
            TestCommandListHandles.ParseKind("active", out active, out reason);
            Assert.Equal(ListHandlesKind.RewindPoints, rp);
            Assert.Equal(ListHandlesKind.Committed, committed);
            Assert.Equal(ListHandlesKind.Active, active);
        }

        [Theory]
        [InlineData("")]
        [InlineData("Rewindpoints")]   // case variant: fail-closed, the LoadGame scene= rule
        [InlineData("REWINDPOINTS")]
        [InlineData("Committed")]
        [InlineData("Active")]
        [InlineData("rp")]
        [InlineData("rewindpoint")]
        [InlineData(" rewindpoints")]  // no whitespace tolerance on a closed string arg
        [InlineData("garbage")]
        public void ParseKind_rejects_everything_outside_the_closed_set(string raw)
        {
            ListHandlesKind kind;
            string reason;
            Assert.False(TestCommandListHandles.ParseKind(raw, out kind, out reason));
            Assert.Equal(ListHandlesKind.None, kind);
            Assert.Equal(TestCommandListHandles.KindArgInvalidReason, reason);
            Assert.Equal("kind-arg-invalid", reason);
        }

        // ----- rewindpoints family -----

        [Fact]
        public void RewindPoints_payload_emits_the_documented_key_sequence()
        {
            var rows = new List<RewindPointRow>
            {
                Rp("rp_a", 12.5, Slot("child-0", true), Slot("child-1", false)),
                Rp("rp_b", 30.0),
            };

            Assert.Equal(new[]
            {
                "kind", "count", "truncated",
                "rp0", "rp0ut", "rp0provisional", "rp0corrupted", "rp0slots",
                "rp0slot0", "rp0slot0open", "rp0slot1", "rp0slot1open",
                "rp1", "rp1ut", "rp1provisional", "rp1corrupted", "rp1slots",
            }, Keys(TestCommandListHandles.BuildRewindPointsPayload(rows)));

            Dictionary<string, string> m =
                Map(TestCommandListHandles.BuildRewindPointsPayload(rows));
            Assert.Equal("rewindpoints", m["kind"]);
            Assert.Equal("2", m["count"]);
            Assert.Equal("false", m["truncated"]);
            Assert.Equal("rp_a", m["rp0"]);
            Assert.Equal("2", m["rp0slots"]);
            Assert.Equal("child-0", m["rp0slot0"]);
            Assert.Equal("true", m["rp0slot0open"]);
            Assert.Equal("false", m["rp0slot1open"]);
            // The last enumerated rewind point is the newest (append order preserved).
            Assert.Equal("rp_b", m["rp1"]);
            Assert.Equal("0", m["rp1slots"]);
        }

        [Fact]
        public void RewindPoints_preserve_list_order_rather_than_sorting_by_id()
        {
            // Append order IS the contract (rp<count-1> is the newest), so an id-sorted
            // enumeration would silently change what ${handles.rp0} names.
            var rows = new List<RewindPointRow> { Rp("rp_z", 1), Rp("rp_a", 2) };
            Dictionary<string, string> m =
                Map(TestCommandListHandles.BuildRewindPointsPayload(rows));
            Assert.Equal("rp_z", m["rp0"]);
            Assert.Equal("rp_a", m["rp1"]);
        }

        [Fact]
        public void RewindPoints_cap_enumerates_sixteen_and_reports_the_true_total()
        {
            var rows = new List<RewindPointRow>();
            for (int i = 0; i < 17; i++)
                rows.Add(Rp("rp_" + i.ToString(CultureInfo.InvariantCulture), i));

            List<KeyValuePair<string, string>> payload =
                TestCommandListHandles.BuildRewindPointsPayload(rows);
            Dictionary<string, string> m = Map(payload);
            Assert.Equal("17", m["count"]);
            Assert.Equal("true", m["truncated"]);
            Assert.Equal(TestCommandListHandles.MaxRewindPoints, 16);
            Assert.True(m.ContainsKey("rp15"));
            Assert.False(m.ContainsKey("rp16"));
        }

        [Fact]
        public void Slot_cap_enumerates_eight_and_still_raises_truncated()
        {
            // The slot cap is the one a count check cannot catch: count counts rewind
            // points, so without this bit an 9-slot cut would be silent.
            var slots = new List<SlotRow>();
            for (int j = 0; j < 9; j++)
                slots.Add(Slot("c" + j.ToString(CultureInfo.InvariantCulture), true));
            var rows = new List<RewindPointRow>
            {
                new RewindPointRow { Id = "rp_a", Ut = 1, Slots = slots },
            };

            Dictionary<string, string> m =
                Map(TestCommandListHandles.BuildRewindPointsPayload(rows));
            Assert.Equal("1", m["count"]);
            Assert.Equal("true", m["truncated"]);
            Assert.Equal(TestCommandListHandles.MaxSlotsPerRewindPoint, 8);
            // slots= is the UNTRUNCATED count, so the reader sees what was cut.
            Assert.Equal("9", m["rp0slots"]);
            Assert.True(m.ContainsKey("rp0slot7"));
            Assert.False(m.ContainsKey("rp0slot8"));
        }

        [Fact]
        public void RewindPoints_empty_is_a_legitimate_answer()
        {
            Dictionary<string, string> m = Map(
                TestCommandListHandles.BuildRewindPointsPayload(new List<RewindPointRow>()));
            Assert.Equal(new[] { "kind", "count", "truncated" },
                Keys(TestCommandListHandles.BuildRewindPointsPayload(new List<RewindPointRow>())));
            Assert.Equal("0", m["count"]);
            Assert.Equal("false", m["truncated"]);
        }

        [Fact]
        public void RewindPoint_ut_is_invariant_culture()
        {
            CultureInfo prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                // A comma-decimal locale must not reach the wire: the harness parses
                // `rp0ut=` as a number and a `12,5` would split the token list.
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Dictionary<string, string> m = Map(
                    TestCommandListHandles.BuildRewindPointsPayload(
                        new List<RewindPointRow> { Rp("rp_a", 1234.5) }));
                Assert.Equal("1234.5", m["rp0ut"]);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prev;
            }
        }

        // ----- the open mapping -----

        [Theory]
        [InlineData(true, MergeState.CommittedProvisional, true)]
        [InlineData(true, MergeState.NotCommitted, true)]
        [InlineData(true, MergeState.Immutable, false)]
        // An unresolvable tip reads CLOSED: nothing could be invoked or sealed through it,
        // so reporting it open would hand a spec a target that cannot be acted on.
        [InlineData(false, MergeState.CommittedProvisional, false)]
        [InlineData(false, MergeState.Immutable, false)]
        public void SlotOpen_is_resolved_and_not_immutable(
            bool tipResolved, MergeState tipState, bool expected)
        {
            Assert.Equal(expected, TestCommandListHandles.SlotOpen(tipResolved, tipState));
        }

        // ----- committed family -----

        [Fact]
        public void Committed_payload_emits_the_documented_key_sequence()
        {
            var groups = new List<IReadOnlyList<CommittedRow>>
            {
                new List<CommittedRow>
                {
                    new CommittedRow
                    {
                        RecordingId = "r1", TreeId = "t1", Pid = 4242, SpawnedPid = 777001,
                        Name = "Rocket A", Spawned = true, State = MergeState.CommittedProvisional,
                    },
                },
            };

            Assert.Equal(new[]
            {
                "kind", "count", "truncated",
                "rec0", "rec0tree", "rec0pid", "rec0spawnedPid", "rec0name", "rec0spawned",
                "rec0state",
            }, Keys(TestCommandListHandles.BuildCommittedPayload(groups)));

            Dictionary<string, string> m =
                Map(TestCommandListHandles.BuildCommittedPayload(groups));
            Assert.Equal("committed", m["kind"]);
            Assert.Equal("1", m["count"]);
            Assert.Equal("4242", m["rec0pid"]);
            // The spawn pid is the LIVE handle; 0 would mean the ghost was never really spawned.
            Assert.Equal("777001", m["rec0spawnedPid"]);
            // The raw name: the response writer owns the percent-encoding, so nothing here
            // pre-encodes (a double encode would reach the spec as "Rocket%2520A").
            Assert.Equal("Rocket A", m["rec0name"]);
            Assert.Equal("true", m["rec0spawned"]);
            Assert.Equal("CommittedProvisional", m["rec0state"]);
        }

        [Fact]
        public void Committed_walks_trees_in_list_order_and_sorts_ids_within_each()
        {
            // Tree order is the store's; within a tree the source is a dictionary, which
            // has no order contract, so the ordinal sort is what makes rec<i> stable.
            var groups = new List<IReadOnlyList<CommittedRow>>
            {
                new List<CommittedRow> { Rec("b", "t1"), Rec("a", "t1"), Rec("C", "t1") },
                new List<CommittedRow> { Rec("z", "t2"), Rec("y", "t2") },
            };

            Dictionary<string, string> m =
                Map(TestCommandListHandles.BuildCommittedPayload(groups));
            // Ordinal, not culture-aware: uppercase 'C' sorts before lowercase 'a'.
            Assert.Equal("C", m["rec0"]);
            Assert.Equal("a", m["rec1"]);
            Assert.Equal("b", m["rec2"]);
            Assert.Equal("t1", m["rec2tree"]);
            // Second tree's members follow, never interleaved with the first tree's.
            Assert.Equal("y", m["rec3"]);
            Assert.Equal("z", m["rec4"]);
            Assert.Equal("t2", m["rec4tree"]);
        }

        [Fact]
        public void Committed_cap_enumerates_thirtytwo_and_reports_the_true_total()
        {
            var group = new List<CommittedRow>();
            for (int i = 0; i < 33; i++)
                group.Add(Rec("r" + i.ToString("D2", CultureInfo.InvariantCulture), "t1"));

            Dictionary<string, string> m = Map(TestCommandListHandles.BuildCommittedPayload(
                new List<IReadOnlyList<CommittedRow>> { group }));
            Assert.Equal("33", m["count"]);
            Assert.Equal("true", m["truncated"]);
            Assert.Equal(TestCommandListHandles.MaxCommittedRecordings, 32);
            Assert.True(m.ContainsKey("rec31"));
            Assert.False(m.ContainsKey("rec32"));
        }

        [Fact]
        public void Committed_empty_and_null_groups_are_legitimate_answers()
        {
            Assert.Equal(new[] { "kind", "count", "truncated" },
                Keys(TestCommandListHandles.BuildCommittedPayload(null)));
            Dictionary<string, string> m = Map(TestCommandListHandles.BuildCommittedPayload(
                new List<IReadOnlyList<CommittedRow>> { null, new List<CommittedRow>() }));
            Assert.Equal("0", m["count"]);
            Assert.Equal("false", m["truncated"]);
        }

        [Fact]
        public void Committed_pid_is_invariant_culture()
        {
            CultureInfo prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                // A digit-grouping locale must not reach the wire: the consumer is
                // `SimulateStockSwitchClick pid=`, which parses a bare integer.
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Dictionary<string, string> m = Map(TestCommandListHandles.BuildCommittedPayload(
                    new List<IReadOnlyList<CommittedRow>>
                    {
                        new List<CommittedRow> { Rec("r1", "t1", 1234567) },
                    }));
                Assert.Equal("1234567", m["rec0pid"]);
                Assert.Equal("0", m["rec0spawnedPid"]);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prev;
            }
        }

        // ----- active family -----

        [Fact]
        public void Active_payload_emits_the_documented_key_sequence()
        {
            var row = new ActiveRow
            {
                TreeId = "t1",
                ActiveRecordingId = "r1",
                ActivePid = 77,
                Background = new List<BackgroundRow>
                {
                    new BackgroundRow { Pid = 300, RecordingId = "r-c" },
                    new BackgroundRow { Pid = 100, RecordingId = "r-a" },
                    new BackgroundRow { Pid = 200, RecordingId = "r-b" },
                },
            };

            Assert.Equal(new[]
            {
                "kind", "tree", "activeRec", "activePid", "bg", "truncated",
                "bg0pid", "bg0rec", "bg1pid", "bg1rec", "bg2pid", "bg2rec",
            }, Keys(TestCommandListHandles.BuildActivePayload(row)));

            Dictionary<string, string> m = Map(TestCommandListHandles.BuildActivePayload(row));
            Assert.Equal("active", m["kind"]);
            Assert.Equal("t1", m["tree"]);
            Assert.Equal("r1", m["activeRec"]);
            Assert.Equal("77", m["activePid"]);
            Assert.Equal("3", m["bg"]);
            Assert.Equal("false", m["truncated"]);
            // Sorted by pid ASCENDING, not by the map's enumeration order.
            Assert.Equal("100", m["bg0pid"]);
            Assert.Equal("r-a", m["bg0rec"]);
            Assert.Equal("200", m["bg1pid"]);
            Assert.Equal("300", m["bg2pid"]);
        }

        [Fact]
        public void Active_with_no_live_tree_is_an_empty_answer_not_a_failure()
        {
            // What the applier hands over outside a live FLIGHT: the default row. This is
            // an OK terminal by contract - "no live tree" is a true observation, so the
            // verb must never defer or reject on it.
            Dictionary<string, string> m =
                Map(TestCommandListHandles.BuildActivePayload(default(ActiveRow)));
            Assert.Equal(new[] { "kind", "tree", "activeRec", "activePid", "bg", "truncated" },
                Keys(TestCommandListHandles.BuildActivePayload(default(ActiveRow))));
            Assert.Equal(string.Empty, m["tree"]);
            Assert.Equal(string.Empty, m["activeRec"]);
            Assert.Equal("0", m["activePid"]);
            Assert.Equal("0", m["bg"]);
            Assert.Equal("false", m["truncated"]);
        }

        [Fact]
        public void Active_background_cap_enumerates_sixteen_and_reports_the_true_total()
        {
            var members = new List<BackgroundRow>();
            for (uint p = 0; p < 17; p++)
                members.Add(new BackgroundRow { Pid = p, RecordingId = "r" + p.ToString(CultureInfo.InvariantCulture) });

            Dictionary<string, string> m = Map(TestCommandListHandles.BuildActivePayload(
                new ActiveRow { TreeId = "t1", Background = members }));
            Assert.Equal("17", m["bg"]);
            Assert.Equal("true", m["truncated"]);
            Assert.Equal(TestCommandListHandles.MaxBackgroundMembers, 16);
            Assert.True(m.ContainsKey("bg15pid"));
            Assert.False(m.ContainsKey("bg16pid"));
        }

        // ----- the log line reads the payload, never a second count -----

        [Fact]
        public void Count_and_truncated_for_the_log_line_come_out_of_the_built_payload()
        {
            // The Info line and the wire must never disagree, so the applier reads both
            // values back out of the payload it is about to send instead of recomputing.
            List<KeyValuePair<string, string>> rp =
                TestCommandListHandles.BuildRewindPointsPayload(
                    new List<RewindPointRow> { Rp("rp_a", 1), Rp("rp_b", 2) });
            Assert.Equal("2", TestCommandListHandles.CountFromPayload(rp, ListHandlesKind.RewindPoints));
            Assert.Equal("false", TestCommandListHandles.TruncatedFromPayload(rp));

            // The active family's count key is `bg`, not `count`.
            List<KeyValuePair<string, string>> active =
                TestCommandListHandles.BuildActivePayload(new ActiveRow
                {
                    TreeId = "t1",
                    Background = new List<BackgroundRow> { new BackgroundRow { Pid = 1, RecordingId = "r" } },
                });
            Assert.Equal("1", TestCommandListHandles.CountFromPayload(active, ListHandlesKind.Active));
            Assert.Equal(string.Empty,
                TestCommandListHandles.CountFromPayload(null, ListHandlesKind.Committed));
        }

        // ----- chains family -----

        private static ChainRow Chain(uint pid, int links, string tip, double spawnUt,
            bool terminated = false)
            => new ChainRow
            {
                Pid = pid,
                Links = links,
                TipRecordingId = tip,
                SpawnUt = spawnUt,
                Terminated = terminated,
            };

        [Fact]
        public void ParseKind_accepts_chains_and_maps_it_to_its_own_member()
        {
            ListHandlesKind kind;
            string reason;
            Assert.True(TestCommandListHandles.ParseKind("chains", out kind, out reason));
            Assert.Equal(ListHandlesKind.Chains, kind);
            Assert.Null(reason);
            Assert.Equal("chains", TestCommandListHandles.KindToken(kind));
        }

        [Theory]
        [InlineData("Chains")]
        [InlineData("chain")]
        [InlineData("CHAINS")]
        public void ParseKind_chains_is_case_sensitive_and_exact(string raw)
        {
            ListHandlesKind kind;
            string reason;
            Assert.False(TestCommandListHandles.ParseKind(raw, out kind, out reason));
            Assert.Equal(TestCommandListHandles.KindArgInvalidReason, reason);
        }

        [Fact]
        public void Chains_payload_emits_the_documented_key_sequence_in_pid_order()
        {
            var rows = new List<ChainRow>
            {
                Chain(3620499050u, 1, "37d0dc074351408ba0374230793abb1c", 8951.5),
                Chain(7u, 2, "tipB", 100.25, terminated: true),
            };
            List<KeyValuePair<string, string>> payload =
                TestCommandListHandles.BuildChainsPayload(rows, evaluated: true, expectedDigest: null);

            Assert.Equal(new[]
            {
                "kind", "count", "truncated", "evaluated", "digest",
                "chain0pid", "chain0links", "chain0tip", "chain0spawnUT", "chain0terminated",
                "chain0trees", "chain0treeIds",
                "chain1pid", "chain1links", "chain1tip", "chain1spawnUT", "chain1terminated",
                "chain1trees", "chain1treeIds",
            }, Keys(payload));
            Dictionary<string, string> m = Map(payload);
            Assert.Equal("chains", m["kind"]);
            Assert.Equal("2", m["count"]);
            Assert.Equal("false", m["truncated"]);
            Assert.Equal("true", m["evaluated"]);
            // Pid ascending, whatever order the live dictionary yielded.
            Assert.Equal("7", m["chain0pid"]);
            Assert.Equal("2", m["chain0links"]);
            Assert.Equal("tipB", m["chain0tip"]);
            Assert.Equal("100.25", m["chain0spawnUT"]);
            Assert.Equal("true", m["chain0terminated"]);
            Assert.Equal("3620499050", m["chain1pid"]);
            Assert.Equal("8951.5", m["chain1spawnUT"]);
            Assert.Equal("false", m["chain1terminated"]);
        }

        [Fact]
        public void Chains_payload_reports_a_pid_pooled_chain_as_two_distinct_trees()
        {
            ChainRow pooled = Chain(3620499050u, 3, "30b49a24", 11794.7);
            pooled.LinkTreeIds = new List<string> { "ac9641d6", "8c677bba", "ac9641d6" };
            ChainRow single = Chain(7u, 1, "tipB", 100.25);
            single.LinkTreeIds = new List<string> { "8c677bba" };
            ChainRow unknown = Chain(9u, 1, "tipC", 200.0);
            List<KeyValuePair<string, string>> payload = TestCommandListHandles.BuildChainsPayload(
                new List<ChainRow> { pooled, single, unknown }, evaluated: true, expectedDigest: null);

            Dictionary<string, string> m = Map(payload);
            Assert.Equal("1", m["chain0trees"]);
            Assert.Equal("8c677bba", m["chain0treeIds"]);
            Assert.Equal("0", m["chain1trees"]);
            Assert.Equal(string.Empty, m["chain1treeIds"]);
            // Distinct, ordinal-sorted, whatever order the links were kept in.
            Assert.Equal("2", m["chain2trees"]);
            Assert.Equal("8c677bba,ac9641d6", m["chain2treeIds"]);
        }

        [Fact]
        public void Chains_tree_keys_do_not_move_the_digest()
        {
            ChainRow bare = Chain(10u, 2, "tip", 50.0);
            ChainRow withTrees = Chain(10u, 2, "tip", 50.0);
            withTrees.LinkTreeIds = new List<string> { "a", "b" };
            Assert.Equal(
                TestCommandListHandles.ChainsDigest(new List<ChainRow> { bare }),
                TestCommandListHandles.ChainsDigest(new List<ChainRow> { withTrees }));
        }

        [Fact]
        public void DistinctTreeIds_drops_null_empty_and_duplicates_and_sorts_ordinal()
        {
            Assert.Empty(TestCommandListHandles.DistinctTreeIds(null));
            Assert.Equal(new[] { "B", "a" }, TestCommandListHandles.DistinctTreeIds(
                new List<string> { "a", null, "", "B", "a" }).ToArray());
        }

        [Fact]
        public void Chain_log_lines_mirror_the_payload_one_line_per_enumerated_chain()
        {
            ChainRow pooled = Chain(3620499050u, 2, "30b49a24", 11794.7);
            pooled.LinkTreeIds = new List<string> { "8c677bba", "ac9641d6" };
            List<KeyValuePair<string, string>> payload = TestCommandListHandles.BuildChainsPayload(
                new List<ChainRow> { pooled }, evaluated: true, expectedDigest: null);

            List<string> lines = TestCommandListHandles.ChainLogLines(payload, ListHandlesKind.Chains);
            Assert.Equal(new[]
            {
                "listhandles chain index=0 pid=3620499050 links=2 trees=2 treeIds=8c677bba,ac9641d6 tip=30b49a24",
            }, lines.ToArray());
            Assert.Empty(TestCommandListHandles.ChainLogLines(payload, ListHandlesKind.Committed));
            Assert.Empty(TestCommandListHandles.ChainLogLines(
                TestCommandListHandles.BuildChainsPayload(new List<ChainRow>(), true, null),
                ListHandlesKind.Chains));
        }

        [Fact]
        public void Chain_log_lines_are_capped_at_the_enumerated_chains()
        {
            var rows = new List<ChainRow>();
            for (uint pid = 1; pid <= TestCommandListHandles.MaxChains + 3; pid++)
                rows.Add(Chain(pid, 1, "t", 1.0));
            List<KeyValuePair<string, string>> payload =
                TestCommandListHandles.BuildChainsPayload(rows, true, null);
            Assert.Equal(TestCommandListHandles.MaxChains,
                TestCommandListHandles.ChainLogLines(payload, ListHandlesKind.Chains).Count);
        }

        [Fact]
        public void Chains_digest_is_pinned_fnv1a_and_empty_set_is_the_offset_basis()
        {
            // Pinned values, computed independently (FNV-1a 32 over the canonical lines).
            // A change here moves every archived capture's digest, so it is a deliberate
            // wire change, not a refactor.
            Assert.Equal("811c9dc5", TestCommandListHandles.ChainsDigest(new List<ChainRow>()));
            Assert.Equal("811c9dc5", TestCommandListHandles.ChainsDigest(null));
            Assert.Equal("bbd83d3b", TestCommandListHandles.ChainsDigest(new List<ChainRow>
            {
                Chain(3620499050u, 1, "37d0dc074351408ba0374230793abb1c", 8951.5),
            }));
            // Order-independent: the digest sorts by pid itself.
            var a = new List<ChainRow>
            {
                Chain(3620499050u, 1, "37d0dc074351408ba0374230793abb1c", 8951.5),
                Chain(7u, 2, "tipB", 100.25, terminated: true),
            };
            var b = new List<ChainRow> { a[1], a[0] };
            Assert.Equal("8952919c", TestCommandListHandles.ChainsDigest(a));
            Assert.Equal("8952919c", TestCommandListHandles.ChainsDigest(b));
        }

        [Fact]
        public void Chains_digest_moves_on_every_identity_field()
        {
            ChainRow baseRow = Chain(10u, 1, "tip", 50.0);
            string d0 = TestCommandListHandles.ChainsDigest(new List<ChainRow> { baseRow });
            var variants = new[]
            {
                Chain(11u, 1, "tip", 50.0),
                Chain(10u, 2, "tip", 50.0),
                Chain(10u, 1, "tiq", 50.0),
                Chain(10u, 1, "tip", 50.000000000000007),
                Chain(10u, 1, "tip", 50.0, terminated: true),
            };
            foreach (ChainRow v in variants)
                Assert.NotEqual(d0, TestCommandListHandles.ChainsDigest(new List<ChainRow> { v }));
        }

        [Fact]
        public void Chains_payload_and_digest_are_culture_invariant()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                var rows = new List<ChainRow> { Chain(7u, 2, "tipB", 100.25, terminated: true) };
                Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                string invariant = TestCommandListHandles.ChainsDigest(rows);
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                List<KeyValuePair<string, string>> payload =
                    TestCommandListHandles.BuildChainsPayload(rows, true, null);
                Assert.Equal("100.25", Map(payload)["chain0spawnUT"]);
                Assert.Equal(invariant, TestCommandListHandles.ChainsDigest(rows));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void Chains_cap_truncates_the_listing_but_not_the_count_or_digest()
        {
            var rows = new List<ChainRow>();
            for (uint i = 0; i < TestCommandListHandles.MaxChains + 2; i++)
                rows.Add(Chain(i + 1, 1, "t" + i.ToString(CultureInfo.InvariantCulture), i));
            List<KeyValuePair<string, string>> payload =
                TestCommandListHandles.BuildChainsPayload(rows, true, null);
            Dictionary<string, string> m = Map(payload);
            Assert.Equal((TestCommandListHandles.MaxChains + 2).ToString(CultureInfo.InvariantCulture), m["count"]);
            Assert.Equal("true", m["truncated"]);
            Assert.Equal(TestCommandListHandles.MaxChains * 7 + 5, payload.Count);
            // A difference in a chain the listing cut must still move the digest.
            var changed = new List<ChainRow>(rows);
            changed[changed.Count - 1] = Chain(changed[changed.Count - 1].Pid, 9, "x", 1);
            Assert.NotEqual(m["digest"], TestCommandListHandles.ChainsDigest(changed));
        }

        [Fact]
        public void Chains_payload_outside_flight_is_the_empty_unevaluated_answer()
        {
            List<KeyValuePair<string, string>> payload =
                TestCommandListHandles.BuildChainsPayload(null, evaluated: false, expectedDigest: null);
            Assert.Equal(new[] { "kind", "count", "truncated", "evaluated", "digest" }, Keys(payload));
            Dictionary<string, string> m = Map(payload);
            Assert.Equal("0", m["count"]);
            Assert.Equal("false", m["evaluated"]);
            Assert.Equal("811c9dc5", m["digest"]);
        }

        [Fact]
        public void Chains_payload_with_expected_digest_answers_match()
        {
            var rows = new List<ChainRow>
            {
                Chain(3620499050u, 1, "37d0dc074351408ba0374230793abb1c", 8951.5),
            };
            List<KeyValuePair<string, string>> same =
                TestCommandListHandles.BuildChainsPayload(rows, true, "bbd83d3b");
            Assert.Equal(new[]
            {
                "kind", "count", "truncated", "evaluated", "digest", "expected", "match",
                "chain0pid", "chain0links", "chain0tip", "chain0spawnUT", "chain0terminated",
                "chain0trees", "chain0treeIds",
            }, Keys(same));
            Assert.Equal("true", Map(same)["match"]);
            Assert.Equal("bbd83d3b", Map(same)["expected"]);

            List<KeyValuePair<string, string>> other =
                TestCommandListHandles.BuildChainsPayload(rows, true, "811c9dc5");
            Assert.Equal("false", Map(other)["match"]);
        }

        [Fact]
        public void ParseExpectDigest_absent_is_a_plain_read_on_every_family()
        {
            string expected, reason;
            Assert.True(TestCommandListHandles.ParseExpectDigest(
                null, ListHandlesKind.Committed, out expected, out reason));
            Assert.Null(expected);
            Assert.Null(reason);
            Assert.True(TestCommandListHandles.ParseExpectDigest(
                null, ListHandlesKind.Chains, out expected, out reason));
            Assert.Null(expected);
        }

        [Fact]
        public void ParseExpectDigest_on_another_family_is_rejected()
        {
            string expected, reason;
            Assert.False(TestCommandListHandles.ParseExpectDigest(
                "bbd83d3b", ListHandlesKind.Committed, out expected, out reason));
            Assert.Equal("expect-digest-kind-mismatch", reason);
            Assert.Null(expected);
        }

        [Theory]
        [InlineData("")]
        [InlineData("BBD83D3B")]            // the digest is lowercase on the wire
        [InlineData("bbd83d3")]
        [InlineData("bbd83d3b0")]
        [InlineData("bbd83d3g")]
        [InlineData("${before.digest}")]    // an unsubstituted handle reaching the wire
        public void ParseExpectDigest_rejects_everything_but_eight_lowercase_hex(string raw)
        {
            string expected, reason;
            Assert.False(TestCommandListHandles.ParseExpectDigest(
                raw, ListHandlesKind.Chains, out expected, out reason));
            Assert.Equal("expect-digest-invalid", reason);
        }

        [Fact]
        public void ParseExpectDigest_accepts_a_digest_on_chains()
        {
            string expected, reason;
            Assert.True(TestCommandListHandles.ParseExpectDigest(
                "0a1b2c3d", ListHandlesKind.Chains, out expected, out reason));
            Assert.Equal("0a1b2c3d", expected);
            Assert.Null(reason);
        }

        [Fact]
        public void ChainsLogTail_reads_the_payload_and_is_empty_for_other_families()
        {
            var rows = new List<ChainRow> { Chain(1u, 1, "t", 2.0) };
            List<KeyValuePair<string, string>> plain =
                TestCommandListHandles.BuildChainsPayload(rows, true, null);
            string digest = Map(plain)["digest"];
            Assert.Equal(" evaluated=true digest=" + digest,
                TestCommandListHandles.ChainsLogTail(plain, ListHandlesKind.Chains));

            List<KeyValuePair<string, string>> compared =
                TestCommandListHandles.BuildChainsPayload(rows, true, "811c9dc5");
            Assert.Equal(" evaluated=true digest=" + digest + " expected=811c9dc5 match=false",
                TestCommandListHandles.ChainsLogTail(compared, ListHandlesKind.Chains));

            List<KeyValuePair<string, string>> committed =
                TestCommandListHandles.BuildCommittedPayload(null);
            Assert.Equal(string.Empty,
                TestCommandListHandles.ChainsLogTail(committed, ListHandlesKind.Committed));
            Assert.Equal("1", TestCommandListHandles.CountFromPayload(plain, ListHandlesKind.Chains));
        }

        // ----- registration -----

        [Fact]
        public void ListHandles_is_an_implemented_non_mutating_verb()
        {
            Assert.Equal(TestCommandVerbClass.Implemented, TestCommandVerbs.Classify("ListHandles"));
            Assert.DoesNotContain("ListHandles", TestCommandVerbs.ReservedVerbNames);
            // Read-only w.r.t. the game world, so it must not clear the FlushAndQuit
            // batch-baseline latch (hlib calls the same fact TAIL_ROLE_INERT).
            Assert.False(TestCommandVerbs.IsStateMutatingVerb("ListHandles"));
        }
    }
}
