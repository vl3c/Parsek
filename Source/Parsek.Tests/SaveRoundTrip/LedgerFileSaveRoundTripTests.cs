using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Parsek.Tests.SaveRoundTrip
{
    /// <summary>
    /// Field-level save/load round-trip of the ledger's own save node (its rows, the pre-ledger
    /// progress seed and the stock-shadow take-back carried beside them) and of the kerbal
    /// slots the scenario node carries. Row contents are covered field by field by
    /// <see cref="GameActionSaveRoundTripTests"/>; here a row compares by its saved form, and
    /// every OTHER static field of <see cref="Ledger"/> must survive or be classified.
    /// </summary>
    [Collection("Sequential")]
    [SaveRoundTripCovers(typeof(Ledger))]
    [SaveRoundTripCovers(typeof(PreLedgerProgressSeed))]
    [SaveRoundTripCovers(typeof(KerbalsModule))]
    public class LedgerFileSaveRoundTripTests : IDisposable
    {
        public LedgerFileSaveRoundTripTests()
        {
            ParsekLog.SuppressLogging = true;
            Ledger.ResetForTesting();
            StockShadowTakeBack.ResetForTesting();
        }

        public void Dispose()
        {
            Ledger.ResetForTesting();
            StockShadowTakeBack.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static string SavedForm(object row)
        {
            var parent = new ConfigNode("P");
            ((GameAction)row).SerializeInto(parent);
            return parent.ToString();
        }

        private static string CompareRows(object written, object read)
        {
            var a = ((System.Collections.IEnumerable)written).Cast<object>().Select(SavedForm).ToList();
            var b = ((System.Collections.IEnumerable)read).Cast<object>().Select(SavedForm).ToList();
            if (a.Count != b.Count) return "wrote " + a.Count + " rows, read " + b.Count;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i]) return "row " + i + " saved form differs after load";
            return null;
        }

        /// <summary>Two well-formed rows: the row codec writes a per-type subset, so a fully filled row would not compare.</summary>
        private static List<GameAction> Rows() => new List<GameAction>
        {
            new GameAction { UT = 10.5, Type = GameActionType.ScienceSpending, NodeId = "basicRocketry", Cost = 5.25f, Sequence = 1 },
            new GameAction { UT = 20.25, Type = GameActionType.FundsEarning, FundsAwarded = 1234.5f,
                FundsSource = FundsEarningSource.ContractComplete, RecordingId = "rec-a", Sequence = 2 },
        };

        internal static FieldSpec LedgerSpec() => new FieldSpec(typeof(Ledger))
            .Derived("moves on every ledger change, including the load itself", "StateVersion")
            .Derived("per-load migration tallies and their one-shot log latch; each load recounts",
                "LegacyActionIdMigrationCount", "legacyActionIdMigrationLogEmitted",
                "ContractDeadlineAbsoluteCount", "ContractDeadlineMigratedCount",
                "ContractDeadlineAbsentCount", "ContractDeadlineUnparseableCount")
            .Fill("actions", () => Rows())
            .Compare("actions", CompareRows);

        [Fact]
        public void LedgerNode_RoundTripsEveryStaticField()
        {
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                FieldSpec spec = LedgerSpec();
                var rt = new FieldRoundTrip(new[] { spec });
                StaticRoundTrip.Fill(typeof(Ledger), rt, spec);
                Dictionary<string, object> written = StaticRoundTrip.Capture(typeof(Ledger));

                ConfigNode saved = Ledger.BuildSaveNode();
                Ledger.ResetForTesting();
                Assert.True(Ledger.LoadFromNode(saved), "ledger did not load its own save node");

                List<string> problems = StaticRoundTrip.Diff(typeof(Ledger), written, rt, spec);
                Assert.True(problems.Count == 0, FieldRoundTrip.Report("Ledger", problems));
            });
        }

        private static readonly FieldInfo SlotsField =
            typeof(KerbalsModule).GetField("slots", BindingFlags.Instance | BindingFlags.NonPublic);

        [Fact]
        public void KerbalSlots_RoundTripEveryField()
        {
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                var rt = new FieldRoundTrip(Enumerable.Empty<FieldSpec>());
                var slots = new Dictionary<string, KerbalsModule.KerbalSlot>();
                for (int i = 0; i < 2; i++)
                {
                    var slot = rt.Build<KerbalsModule.KerbalSlot>();
                    slots[slot.OwnerName] = slot; // the save keys a slot by its owner
                }

                var module = new KerbalsModule();
                SlotsField.SetValue(module, slots);
                var node = new ConfigNode("PARSEK");
                module.SaveSlots(node);

                var reloaded = new KerbalsModule();
                reloaded.LoadSlots(node);

                List<string> problems = rt.Diff(slots, SlotsField.GetValue(reloaded), "KerbalsModule.slots");
                Assert.True(problems.Count == 0, FieldRoundTrip.Report("KerbalsModule slots", problems));
            });
        }

        [Fact]
        public void KerbalSlot_FieldSetIsTheOneTheSaveWrites()
        {
            // SaveSlots writes owner, trait, permanentlyGone and the chain. A new KerbalSlot field
            // reds here so it is either saved or classified, not silently dropped on load.
            var fields = FieldRoundTrip.GetFields(typeof(KerbalsModule.KerbalSlot)).Select(FieldRoundTrip.LogicalName);
            Assert.Equal(new[] { "Chain", "OwnerName", "OwnerPermanentlyGone", "OwnerTrait" },
                fields.OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }
    }
}
