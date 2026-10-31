using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.Tests.SaveRoundTrip;
using Xunit;

namespace Parsek.Tests.SaveRoundTrip
{
    /// <summary>
    /// Field-level save/load round-trip of the small records the scenario node persists: the
    /// rewind and Re-Fly bookkeeping (rewind points and their slots, supersede rows, rewind
    /// retirements, the Re-Fly marker, the merge journal, ledger tombstones, the switch-segment
    /// session, the stock-action intent, the test batch marker) and the game-state capture
    /// records (events, contract snapshots, baselines, milestones). Every field of each type is
    /// filled with a distinct value, saved, loaded under a comma-decimal culture and diffed; the
    /// only fields allowed to differ are the ones the specs below classify, each with its reason.
    /// </summary>
    [Collection("Sequential")]
    [SaveRoundTripCovers(typeof(ChildSlot))]
    [SaveRoundTripCovers(typeof(RewindPoint))]
    [SaveRoundTripCovers(typeof(RecordingSupersedeRelation))]
    [SaveRoundTripCovers(typeof(RecordingRewindRetirement))]
    [SaveRoundTripCovers(typeof(ReFlySessionMarker))]
    [SaveRoundTripCovers(typeof(MergeJournal))]
    [SaveRoundTripCovers(typeof(LedgerTombstone))]
    [SaveRoundTripCovers(typeof(SwitchSegmentSession))]
    [SaveRoundTripCovers(typeof(StockActionIntentMarker))]
    [SaveRoundTripCovers(typeof(TestBatchMarker))]
    [SaveRoundTripCovers(typeof(GameStateEvent))]
    [SaveRoundTripCovers(typeof(ContractSnapshot))]
    [SaveRoundTripCovers(typeof(GameStateBaseline))]
    [SaveRoundTripCovers(typeof(GameStateBaseline.CrewEntry))]
    [SaveRoundTripCovers(typeof(Milestone))]
    public class ScenarioRecordSaveRoundTripTests : IDisposable
    {
        public ScenarioRecordSaveRoundTripTests()
        {
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        /// <summary>One persisted record type: how to save it into a parent node and read it back.</summary>
        public sealed class Case
        {
            internal string Name;
            internal Type Type;
            internal Func<object, ConfigNode> Save;
            internal Func<ConfigNode, object> Load;
            public override string ToString() => Name;
        }

        private static Case Child<T>(Action<T, ConfigNode> saveIntoParent, Func<ConfigNode, T> loadChild) =>
            new Case
            {
                Name = typeof(T).Name,
                Type = typeof(T),
                Save = o =>
                {
                    var parent = new ConfigNode("PARENT");
                    saveIntoParent((T)o, parent);
                    Assert.True(parent.CountNodes == 1, typeof(T).Name + " save wrote " + parent.CountNodes + " child nodes");
                    return parent.nodes[0];
                },
                Load = n => loadChild(n),
            };

        private static Case Flat<T>(Action<T, ConfigNode> saveInto, Func<ConfigNode, T> load) =>
            new Case
            {
                Name = typeof(T).Name,
                Type = typeof(T),
                Save = o =>
                {
                    var node = new ConfigNode(typeof(T).Name.ToUpperInvariant());
                    saveInto((T)o, node);
                    return node;
                },
                Load = n => load(n),
            };

        private static readonly Case[] Cases =
        {
            Child<ChildSlot>((o, p) => o.SaveInto(p), ChildSlot.LoadFrom),
            Child<RewindPoint>((o, p) => o.SaveInto(p), RewindPoint.LoadFrom),
            Child<RecordingSupersedeRelation>((o, p) => o.SaveInto(p), RecordingSupersedeRelation.LoadFrom),
            Child<RecordingRewindRetirement>((o, p) => o.SaveInto(p), RecordingRewindRetirement.LoadFrom),
            Child<ReFlySessionMarker>((o, p) => o.SaveInto(p), ReFlySessionMarker.LoadFrom),
            Child<MergeJournal>((o, p) => o.SaveInto(p), MergeJournal.LoadFrom),
            Child<LedgerTombstone>((o, p) => o.SaveInto(p), LedgerTombstone.LoadFrom),
            Child<SwitchSegmentSession>((o, p) => o.SaveInto(p),
                n => SwitchSegmentSession.TryLoadFrom(n, out SwitchSegmentSession s) ? s : null),
            Child<StockActionIntentMarker>((o, p) => o.SaveInto(p),
                n => StockActionIntentMarker.TryLoadFrom(n, out StockActionIntentMarker m) ? m : null),
            Child<TestBatchMarker>((o, p) => o.SaveInto(p), TestBatchMarker.LoadFrom),
            Flat<GameStateEvent>((o, n) => o.SerializeInto(n), GameStateEvent.DeserializeFrom),
            Child<ContractSnapshot>((o, p) => o.SerializeInto(p), ContractSnapshot.DeserializeFrom),
            Flat<GameStateBaseline>((o, n) => o.SerializeInto(n), GameStateBaseline.DeserializeFrom),
            Flat<GameStateBaseline.CrewEntry>((o, n) => o.SerializeInto(n), GameStateBaseline.CrewEntry.DeserializeFrom),
            Flat<Milestone>((o, n) => o.SerializeInto(n), Milestone.DeserializeFrom),
        };

        /// <summary>The per-type contracts: fields deliberately not written, and constrained fills.</summary>
        internal static IEnumerable<FieldSpec> Specs()
        {
            yield return new FieldSpec(typeof(ReFlySessionMarker))
                .Derived("not written; LoadFrom sets it true on every marker it reads back", "LoadedFromSave");
            yield return new FieldSpec(typeof(GameStateEvent))
                .NotPersisted("legacy load-only stamp: read from old saves, never assigned or written by this build",
                    "epoch");
            yield return new FieldSpec(typeof(Milestone))
                .NotPersisted("legacy load-only stamp: read from old saves, never assigned or written by this build",
                    "Epoch");
            yield return new FieldSpec(typeof(ContractSnapshot))
                .Fill("contractNode", () => ContractNode("snap"));
            yield return new FieldSpec(typeof(GameStateBaseline))
                .Fill("activeContracts", () => new List<ConfigNode> { ContractNode("a"), ContractNode("b") });
            yield return new FieldSpec(typeof(MergeJournal))
                .NotPersisted("transient thread inside CompleteFromPostDurable; documented as never persisted",
                    "RecoveredSubtreeIds")
                .Fill("Phase", () => MergeJournal.Phases.Supersede);
        }

        /// <summary>Stock CONTRACT nodes are stored verbatim and read back by that node name.</summary>
        private static ConfigNode ContractNode(string tag)
        {
            var n = new ConfigNode("CONTRACT");
            n.AddValue("guid", "guid-" + tag);
            n.AddValue("state", "Active");
            return n;
        }

        public static IEnumerable<object[]> AllCases() => Cases.Select(c => new object[] { c });

        [Theory]
        [MemberData(nameof(AllCases))]
        public void Record_RoundTripsEveryField(Case c)
        {
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                var rt = new FieldRoundTrip(Specs());
                object written = rt.Build(c.Type);
                ConfigNode node = c.Save(written);
                object read = c.Load(node);
                Assert.True(read != null, c.Name + " did not load back from its own save");
                List<string> problems = rt.Diff(written, read, c.Name);
                Assert.True(problems.Count == 0, FieldRoundTrip.Report(c.Name, problems));
            });
        }

        [Fact]
        public void Harness_DetectsAFieldTheCodecDrops()
        {
            // A spec-free diff of a marker against a copy missing one field must report it.
            var rt = new FieldRoundTrip(Specs());
            var written = rt.Build<RecordingSupersedeRelation>();
            var parent = new ConfigNode("PARENT");
            written.SaveInto(parent);
            parent.nodes[0].RemoveValue("newRecordingId");
            var read = RecordingSupersedeRelation.LoadFrom(parent.nodes[0]);
            Assert.Contains(rt.Diff(written, read, "rel"), p => p.Contains("NewRecordingId"));
        }
    }
}
