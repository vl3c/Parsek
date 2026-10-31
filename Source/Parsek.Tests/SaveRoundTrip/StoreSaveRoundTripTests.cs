using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.SaveRoundTrip
{
    /// <summary>
    /// Field-level save/load round-trip of the static stores the scenario node persists
    /// (missions, vessel names, UI groups, crew replacements, rewind vessel holds, the ledger
    /// held window, the stock-shadow take-back, the stock pool reference). Every static field of
    /// a store is filled with distinct values, the store is saved, reset, loaded under a
    /// comma-decimal culture, and each static field is diffed against what was written. The
    /// per-store specs name each field that is deliberately not saved (indexes rebuilt on load,
    /// runtime counters, session flags) with the reason.
    /// </summary>
    [Collection("Sequential")]
    [SaveRoundTripCovers(typeof(MissionStore))]
    [SaveRoundTripCovers(typeof(Mission))]
    [SaveRoundTripCovers(typeof(VesselNameStore))]
    [SaveRoundTripCovers(typeof(GroupHierarchyStore))]
    [SaveRoundTripCovers(typeof(CrewReservationManager))]
    [SaveRoundTripCovers(typeof(RewindVesselHold))]
    [SaveRoundTripCovers(typeof(ReFlyInvokeHold))]
    [SaveRoundTripCovers(typeof(LedgerHeldWindow))]
    [SaveRoundTripCovers(typeof(StockShadowTakeBack))]
    [SaveRoundTripCovers(typeof(StockPoolReference))]
    [SaveRoundTripCovers(typeof(GameStateStore))]
    [SaveRoundTripCovers(typeof(MilestoneStore))]
    [SaveRoundTripCovers(typeof(ParsekSettingsPersistence))]
    public class StoreSaveRoundTripTests : IDisposable
    {
        public StoreSaveRoundTripTests()
        {
            ParsekLog.SuppressLogging = true;
            ResetAll();
        }

        public void Dispose()
        {
            ResetAll();
            ParsekLog.ResetTestOverrides();
        }

        private static void ResetAll()
        {
            MissionStore.ResetForTesting();
            VesselNameStore.ResetForTesting();
            GroupHierarchyStore.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            RewindVesselHold.ResetForTesting();
            LedgerHeldWindow.ResetForTesting();
            StockShadowTakeBack.ResetForTesting();
            StockPoolReference.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekSettingsPersistence.ResetForTesting();
            try { System.IO.File.Delete(SettingsPath); } catch { }
        }

        /// <summary>The install-wide settings file lives outside the save; the test points it at a temp file.</summary>
        private static readonly string SettingsPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "parsek-settings-roundtrip-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".cfg");

        private static void SaveSettingsFile()
        {
            ParsekSettingsPersistence.SetFilePathOverrideForTesting(SettingsPath);
            typeof(ParsekSettingsPersistence)
                .GetMethod("Save", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                .Invoke(null, null);
        }

        /// <summary>One static store: its save, its reset, its load, and its field contract.</summary>
        public sealed class StoreCase
        {
            internal string Name;
            internal Type Store;
            internal Action<ConfigNode> Save;
            internal Action Reset;
            internal Action<ConfigNode> Load;
            internal Func<FieldSpec> Spec;
            internal Func<IEnumerable<FieldSpec>> NestedSpecs = () => Enumerable.Empty<FieldSpec>();
            public override string ToString() => Name;
        }

        private static readonly StoreCase[] Stores =
        {
            new StoreCase
            {
                Name = "MissionStore", Store = typeof(MissionStore),
                Save = MissionStore.Save, Reset = MissionStore.ResetForTesting, Load = MissionStore.Load,
                Spec = () => new FieldSpec(typeof(MissionStore))
                    .Derived("test-only logging switch", "SuppressLogging"),
            },
            new StoreCase
            {
                Name = "VesselNameStore", Store = typeof(VesselNameStore),
                Save = VesselNameStore.Save, Reset = VesselNameStore.ResetForTesting, Load = VesselNameStore.Load,
                Spec = () => new FieldSpec(typeof(VesselNameStore))
                    .Derived("guid index rebuilt from the decisions on load", "byGuid")
                    .Derived("moves on every store change, including the load itself", "Version")
                    .NotPersisted("self-heal bookkeeping of the last apply pass; restarts each session",
                        "lastAppliedStateVersion", "lastLiveTreeSignature", "nextLiveTreeCheckRealtime")
                    .NotPersisted("one-shot request for the next apply pass; a load re-applies anyway", "linkApplyDue")
                    .NotPersisted("revert-undo state is memory only by design (design-vessel-and-mission-names.md)",
                        "undoEntries", "lastPostInitState", "reFlyEpochSessionId", "FlightEpoch")
                    .Fill("links", () => KeyedLinks())
                    .Fill("dockAbsorbs", () => KeyedDockAbsorbs()),
                NestedSpecs = () => new[]
                {
                    new FieldSpec(typeof(VesselNameDecision))
                        .NotPersisted("revert-undo epoch of the session that decided it; memory only", "FlightEpoch"),
                },
            },
            new StoreCase
            {
                Name = "GroupHierarchyStore", Store = typeof(GroupHierarchyStore),
                // WriteInto: SaveInto first prunes groups no committed recording names, which with an
                // empty recording store is every filled group.
                Save = GroupHierarchyStore.WriteInto, Reset = GroupHierarchyStore.ResetForTesting,
                Load = n => { GroupHierarchyStore.LoadGroupHierarchy(n); GroupHierarchyStore.LoadHiddenGroups(n); },
                Spec = () => new FieldSpec(typeof(GroupHierarchyStore)),
            },
            new StoreCase
            {
                Name = "CrewReservationManager", Store = typeof(CrewReservationManager),
                Save = CrewReservationManager.SaveCrewReplacements,
                Reset = CrewReservationManager.ResetReplacementsForTesting,
                Load = CrewReservationManager.LoadCrewReplacements,
                Spec = () => new FieldSpec(typeof(CrewReservationManager)),
            },
            new StoreCase
            {
                Name = "RewindVesselHold", Store = typeof(RewindVesselHold),
                Save = n => RewindVesselHold.SaveInto(n), Reset = RewindVesselHold.ResetForTesting,
                Load = n => RewindVesselHold.Replace(RewindVesselHold.LoadFrom(n), "round-trip test"),
                Spec = () => new FieldSpec(typeof(RewindVesselHold)),
                NestedSpecs = () => new[]
                {
                    new FieldSpec(typeof(RewindVesselHold.Hold)).Fill("Snapshot", () => VesselNode()),
                },
            },
            new StoreCase
            {
                Name = "LedgerHeldWindow", Store = typeof(LedgerHeldWindow),
                Save = LedgerHeldWindow.SaveInto, Reset = LedgerHeldWindow.ResetForTesting,
                Load = n => LedgerHeldWindow.InstallAfterLoad(0, LedgerHeldWindow.ReadSaved(n), "round-trip test"),
                Spec = () => new FieldSpec(typeof(LedgerHeldWindow)),
            },
            new StoreCase
            {
                Name = "StockShadowTakeBack", Store = typeof(StockShadowTakeBack),
                Save = StockShadowTakeBack.SaveInto, Reset = StockShadowTakeBack.ResetForTesting,
                Load = n => StockShadowTakeBack.LoadFrom(n, "round-trip test"),
                Spec = () => new FieldSpec(typeof(StockShadowTakeBack)),
            },
            new StoreCase
            {
                Name = "StockPoolReference", Store = typeof(StockPoolReference),
                Save = StockPoolReference.SaveTo, Reset = StockPoolReference.ResetForTesting,
                Load = n => StockPoolReference.LoadFrom(n, "round-trip test"),
                Spec = () => new FieldSpec(typeof(StockPoolReference)),
            },
            new StoreCase
            {
                // OnSave's layout: the events node (events, contract snapshots, science subjects)
                // and a BASELINES node of one PARSEK_BASELINE per baseline, both read back by
                // the cold load from GAME_STATE.
                Name = "GameStateStore", Store = typeof(GameStateStore),
                Save = n =>
                {
                    n.AddNode(GameStateStore.BuildEventsNode(null).Root);
                    var baselinesNode = n.AddNode(ParsekScenario.BaselinesNodeName);
                    foreach (GameStateBaseline b in GameStateStore.Baselines)
                        baselinesNode.AddNode(GameStateStore.BuildBaselineNode(b));
                },
                Reset = GameStateStore.ResetForTesting,
                Load = n =>
                {
                    Assert.True(GameStateStore.LoadEventsFromSaveNode(n.GetNode(GameStateStore.EventsNodeName)));
                    Assert.True(GameStateStore.LoadBaselinesFromSaveNode(n.GetNode(ParsekScenario.BaselinesNodeName)));
                },
                Spec = () => new FieldSpec(typeof(GameStateStore))
                    .Derived("once-per-folder load guard; the load itself sets it", "initialLoadDone", "lastSaveFolder")
                    .Derived("test-only logging switch", "SuppressLogging")
                    .Compare("baselines", CompareBaselines),
                NestedSpecs = ScenarioRecordSaveRoundTripTests.Specs,
            },
            new StoreCase
            {
                // OnSave writes the milestones node plus one MILESTONE_STATE per milestone on the
                // scenario node; the load reads the node and then RestoreMutableState, so a lost
                // replay index in either half reds.
                Name = "MilestoneStore", Store = typeof(MilestoneStore),
                Save = n =>
                {
                    n.AddNode(MilestoneStore.BuildMilestonesNode());
                    MilestoneStore.SaveMutableState(n);
                },
                Reset = MilestoneStore.ResetForTesting,
                Load = n =>
                {
                    Assert.True(MilestoneStore.LoadMilestonesFromSaveNode(n.GetNode(MilestoneStore.MilestonesNodeName)));
                    MilestoneStore.RestoreMutableState(n);
                },
                Spec = () => new FieldSpec(typeof(MilestoneStore))
                    .Derived("once-per-folder load guard; the load itself sets it", "initialLoadDone", "lastSaveFolder"),
                NestedSpecs = ScenarioRecordSaveRoundTripTests.Specs,
            },
            new StoreCase
            {
                // The install-wide settings file: saved to a temp file, forgotten, re-read.
                Name = "ParsekSettingsPersistence", Store = typeof(ParsekSettingsPersistence),
                Save = n => SaveSettingsFile(),
                Reset = ParsekSettingsPersistence.ForgetLoadedStateForTesting,
                Load = n => ParsekSettingsPersistence.LoadIfNeeded(),
                Spec = () => new FieldSpec(typeof(ParsekSettingsPersistence))
                    .Derived("test-only redirect of the file location", "filePathOverrideForTesting")
                    .Derived("load latch; the load itself sets it", "loaded")
                    .Fill("storedSamplingDensity", () => (int?)2)
                    .Fill("storedGhostAudioVolume", () => (float?)0.75f),
            },
        };

        /// <summary>The load sorts baselines by UT; compare by saved form in that order.</summary>
        private static string CompareBaselines(object written, object read)
        {
            Func<object, List<string>> forms = list => ((IEnumerable<GameStateBaseline>)list)
                .OrderBy(b => b.ut)
                .Select(b => GameStateStore.BuildBaselineNode(b).ToString())
                .ToList();
            List<string> a = forms(written), b2 = forms(read);
            if (a.Count != b2.Count) return "wrote " + a.Count + " baselines, read " + b2.Count;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b2[i]) return "baseline " + i + " saved form differs after load";
            return null;
        }

        /// <summary>A held vessel's snapshot is saved as a VESSEL child and read back by that name.</summary>
        private static ConfigNode VesselNode()
        {
            var n = new ConfigNode(RewindVesselHold.SnapshotNodeName);
            n.AddValue("pid", Guid.NewGuid().ToString("N"));
            n.AddValue("name", "Held Ship");
            return n;
        }

        /// <summary>Link rows are keyed by their own normalized launch guid.</summary>
        private static Dictionary<string, VesselIdentityLink> KeyedLinks()
        {
            var d = new Dictionary<string, VesselIdentityLink>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < 2; i++)
            {
                string from = new Guid(0x100 + i, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10).ToString("N");
                string to = new Guid(0x200 + i, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10).ToString("N");
                d[from] = new VesselIdentityLink { Guid = from, ToGuid = to, Kind = (VesselIdentityLinkKind)(i + 1) };
            }
            return d;
        }

        /// <summary>Dock-absorb rows are keyed by the absorbed root part uid and carry a launch guid.</summary>
        private static Dictionary<uint, DockAbsorbRow> KeyedDockAbsorbs()
        {
            var d = new Dictionary<uint, DockAbsorbRow>();
            for (uint i = 0; i < 2; i++)
                d[7000 + i] = new DockAbsorbRow
                {
                    RootPartUId = 7000 + i,
                    Guid = new Guid(0x300 + (int)i, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10).ToString("N"),
                };
            return d;
        }

        /// <summary>Covered types this class reaches through a store's fields rather than as a store of their own.</summary>
        private static readonly Dictionary<Type, Type> ReachedThrough = new Dictionary<Type, Type>
        {
            [typeof(Mission)] = typeof(MissionStore),
            [typeof(ReFlyInvokeHold)] = typeof(RewindVesselHold), // Hold.Entry, via SaveEntry / LoadEntry
        };

        [Fact]
        public void EveryCoveredType_IsExercisedByAStoreCase()
        {
            // The coverage gate only sees the attribute; this keeps each attribute honest.
            var stores = new HashSet<Type>(Stores.Select(s => s.Store));
            var unexercised = typeof(StoreSaveRoundTripTests)
                .GetCustomAttributes(typeof(SaveRoundTripCoversAttribute), false)
                .Cast<SaveRoundTripCoversAttribute>()
                .Select(a => a.Type)
                .Where(t => !stores.Contains(t)
                    && !(ReachedThrough.TryGetValue(t, out Type via) && stores.Contains(via)))
                .Select(t => t.Name)
                .ToList();
            Assert.True(unexercised.Count == 0,
                "[SaveRoundTripCovers] names type(s) no StoreCase exercises: " + string.Join(", ", unexercised));
        }

        public static IEnumerable<object[]> AllStores() => Stores.Select(s => new object[] { s });

        [Theory]
        [MemberData(nameof(AllStores))]
        public void Store_RoundTripsEveryStaticField(StoreCase c)
        {
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                FieldSpec spec = c.Spec();
                var rt = new FieldRoundTrip(new[] { spec }.Concat(c.NestedSpecs()));
                StaticRoundTrip.Fill(c.Store, rt, spec);
                Dictionary<string, object> written = StaticRoundTrip.Capture(c.Store);

                var node = new ConfigNode("PARSEK");
                c.Save(node);
                c.Reset();
                c.Load(node);

                List<string> problems = StaticRoundTrip.Diff(c.Store, written, rt, spec);
                Assert.True(problems.Count == 0, FieldRoundTrip.Report(c.Name, problems));
            });
        }
    }
}
