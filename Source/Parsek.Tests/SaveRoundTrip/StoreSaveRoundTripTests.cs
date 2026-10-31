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
                Spec = () => new FieldSpec(typeof(CrewReservationManager))
                    .NotPersisted("#615 rescue-placed marker is session scoped; a load clears it (todo RESCUE-PLACED-MARKER-SESSION-ONLY)",
                        "rescuePlacedKerbals"),
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
        };

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
