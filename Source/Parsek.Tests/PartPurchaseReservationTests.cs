using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using KSP.UI.Screens;
using KSP.UI.Screens.Editor;
using Parsek.Patches;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// P1, part entry purchases (docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md,
    /// section 10 step 9): the block predicate, its text, the state patch that marks a
    /// committed purchase purchased in stock, the pairing of the greyed tooltip with the
    /// editor and R&amp;D refusals, and the resolution of every patched stock member.
    /// </summary>
    [Collection("Sequential")]
    public class PartPurchaseReservationTests : IDisposable
    {
        private const string Pod = "mk1pod.v2";
        private const string Booster = "solidBooster.sm";
        private const string Free = "basicFin";

        private readonly List<string> logLines = new List<string>();
        private readonly List<string> dialogActions = new List<string>();
        private readonly List<string> dialogReasons = new List<string>();

        private static readonly Func<double, string> Fmt =
            ut => "D" + ((long)(ut / 100.0)).ToString(CultureInfo.InvariantCulture);

        public PartPurchaseReservationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateStore.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            KspStatePatcher.ResetForTesting();
            StockUiPartPurchase.ResetForTesting();
            GameStateRecorder.BypassEntryPurchaseAfterResearchProviderForTesting = () => false;
            StockUiPartPurchase.PurchasedInStockProviderForTesting = ap => false;
            CommittedActionDialog.TestHookForTesting = (action, reason, detail) =>
            {
                dialogActions.Add(action);
                dialogReasons.Add(reason);
            };
        }

        public void Dispose()
        {
            CommittedActionDialog.TestHookForTesting = null;
            StockUiPartPurchase.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static GameAction Purchase(double ut, string part, float cost, string recordingId = null) =>
            new GameAction
            {
                UT = ut,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = cost,
                DedupKey = part,
                RecordingId = recordingId
            };

        private static CommittedFutureIndex Index(params GameAction[] rows)
        {
            return CommittedFutureIndex.Build(rows,
                id => id == "rec-committed",
                id => id == "rec-committed" ? "Pod Test" : null,
                null);
        }

        private static AvailablePart Part(string name, string tech = "start") =>
            new AvailablePart { name = name, title = name + " title", TechRequired = tech };

        // ---------------- predicate ----------------

        [Fact]
        public void Predicate_BlocksBeforeTheCommittedUT_LiftsAtIt()
        {
            var index = Index(Purchase(500, Pod, 1600f));

            Assert.True(StockUiReservationPredicates.IsPartPurchaseBlocked(index, Pod, 499.9, false, false));
            // The walk applies a row AT the current UT, and the state patch marks it then.
            Assert.False(StockUiReservationPredicates.IsPartPurchaseBlocked(index, Pod, 500, false, false));
            Assert.False(StockUiReservationPredicates.IsPartPurchaseBlocked(index, Pod, 501, false, false));
        }

        [Fact]
        public void Predicate_BypassEntryPurchaseOn_NeverBlocks()
        {
            var index = Index(Purchase(500, Pod, 1600f));
            Assert.False(StockUiReservationPredicates.IsPartPurchaseBlocked(index, Pod, 100, true, false));
        }

        [Fact]
        public void Predicate_AlreadyPurchasedInStock_DoesNotBlock()
        {
            var index = Index(Purchase(500, Pod, 1600f));
            Assert.False(StockUiReservationPredicates.IsPartPurchaseBlocked(index, Pod, 100, false, true));
        }

        [Fact]
        public void Predicate_UncommittedRecordingsRow_IsIgnored_CommittedFlightAndKscRowsBlock()
        {
            var index = Index(
                Purchase(500, Pod, 1600f, "rec-live"),
                Purchase(500, Booster, 800f, "rec-committed"),
                Purchase(500, Free, 100f));

            Assert.False(StockUiReservationPredicates.IsPartPurchaseBlocked(index, Pod, 100, false, false));
            Assert.True(StockUiReservationPredicates.IsPartPurchaseBlocked(index, Booster, 100, false, false));
            Assert.True(StockUiReservationPredicates.IsPartPurchaseBlocked(index, Free, 100, false, false));
            Assert.Equal(1, index.SkippedUncommittedRows);
        }

        [Fact]
        public void Predicate_ZeroCostRowCapturedUnderBypass_ReservesNothing()
        {
            var index = Index(Purchase(500, Pod, 0f));
            Assert.False(StockUiReservationPredicates.IsPartPurchaseBlocked(index, Pod, 100, false, false));
        }

        [Fact]
        public void Predicate_OtherFundsSpendingSources_AreNotPartPurchases()
        {
            var build = Purchase(500, Pod, 1600f);
            build.FundsSpendingSource = FundsSpendingSource.VesselBuild;
            var index = Index(build);
            Assert.False(StockUiReservationPredicates.IsPartPurchaseBlocked(index, Pod, 100, false, false));
        }

        [Fact]
        public void Predicate_KeysOnTheRuntimeDotFormPartName()
        {
            // The capture stores AvailablePart.name (dot form); the tooltip and backstops
            // read the same field, so no conversion happens anywhere.
            var action = GameStateEventConverter.ConvertEvent(
                GameStateRecorder.CreatePartPurchasedEvent(Pod, 1600f, false, 500, 50000), null);
            var index = Index(action);
            Assert.True(StockUiReservationPredicates.IsPartPurchaseBlocked(index, "mk1pod.v2", 100, false, false));
            Assert.False(StockUiReservationPredicates.IsPartPurchaseBlocked(index, "mk1pod_v2", 100, false, false));
        }

        // ---------------- text ----------------

        [Fact]
        public void Explanation_NamesTheCommittedFlight_AndTheDate()
        {
            var index = Index(Purchase(500, Booster, 800f, "rec-committed"));
            var text = StockUiReservationPredicates.ExplainPartPurchase(index, Booster, 100, Fmt);

            Assert.Equal("Purchased on D5", text.Title);
            Assert.Equal(
                "Purchased on D5 by the committed flight 'Pod Test'. " +
                "Parsek's timeline is fixed once committed, so this cannot happen earlier or twice. " +
                "It becomes available on that date.",
                text.Body);
        }

        [Fact]
        public void Explanation_KscRow_SaysOnYourCommittedTimeline()
        {
            var index = Index(Purchase(500, Pod, 1600f));
            var text = StockUiReservationPredicates.ExplainPartPurchase(index, Pod, 100, Fmt);
            Assert.StartsWith("Purchased on D5 on your committed timeline.", text.Body);
        }

        [Fact]
        public void PurchaseAllSkipReason_OnePart_FullBody_SeveralParts_OneLineEach()
        {
            var a = ReservationExplanation.PartPurchase(
                new CommittedFutureEntry(CommittedFutureKind.PartPurchase, Pod, 500, null, null), Fmt);
            var b = ReservationExplanation.PartPurchase(
                new CommittedFutureEntry(CommittedFutureKind.PartPurchase, Booster, 700, "r", "Flight B"), Fmt);

            Assert.Equal("Pod: " + a.Body, StockUiPartPurchase.BuildPurchaseAllSkipReason(
                new[] { new KeyValuePair<string, ReservationText>("Pod", a) }));

            Assert.Equal(
                "Pod: Purchased on D5 on your committed timeline.\n" +
                "Booster: Purchased on D7 by the committed flight 'Flight B'.\n" +
                ReservationExplanation.TimelineRule + " They become available on those dates.",
                StockUiPartPurchase.BuildPurchaseAllSkipReason(new[]
                {
                    new KeyValuePair<string, ReservationText>("Pod", a),
                    new KeyValuePair<string, ReservationText>("Booster", b)
                }));
        }

        [Theory]
        [InlineData(0, 0, false)]
        [InlineData(3, 0, false)]
        [InlineData(3, 2, false)]
        [InlineData(3, 3, true)]
        [InlineData(1, 1, true)]
        public void PurchaseAllButton_DisabledOnlyWhenEveryRemainingPartIsBlocked(int unpurchased, int blocked, bool disable)
        {
            Assert.Equal(disable, StockUiPartPurchase.ShouldDisablePurchaseAll(unpurchased, blocked));
        }

        [Fact]
        public void TooltipButtons_BlockedDisables_UnblockedGivesBackOnlyWhatParsekTook()
        {
            Assert.Equal(false, StockUiPartPurchase.PurchaseButtonsInteractable(blocked: true, disabledByParsek: false));
            Assert.Equal(false, StockUiPartPurchase.PurchaseButtonsInteractable(blocked: true, disabledByParsek: true));
            Assert.Equal(true, StockUiPartPurchase.PurchaseButtonsInteractable(blocked: false, disabledByParsek: true));
            Assert.Null(StockUiPartPurchase.PurchaseButtonsInteractable(blocked: false, disabledByParsek: false));
        }

        // ---------------- pairing: greyed set == editor refusal set == R&D refusal set ----------------

        private static bool InvokePrefix(Type patch, AvailablePart ap)
        {
            var prefix = patch.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(prefix);
            return (bool)prefix.Invoke(null, new object[] { ap });
        }

        [Fact]
        public void Pairing_GreyedTooltips_AreExactlyTheEditorAndRnDRefusals_WithTheSameText()
        {
            Ledger.AddAction(Purchase(500, Pod, 1600f));
            Ledger.AddAction(Purchase(50, Booster, 800f));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 100;
            var parts = new[] { Part(Pod), Part(Booster), Part(Free) };

            var greyed = parts.Where(p => StockUiPartPurchase.DecideLive(p).Blocked).Select(p => p.name).ToArray();
            var editorRefused = parts.Where(p => !InvokePrefix(typeof(PartListTooltipPurchasePatch), p))
                .Select(p => p.name).ToArray();
            var rndRefused = parts.Where(p => !InvokePrefix(typeof(RDTechPurchasePartPatch), p))
                .Select(p => p.name).ToArray();

            Assert.Equal(new[] { Pod }, greyed);
            Assert.Equal(greyed, editorRefused);
            Assert.Equal(greyed, rndRefused);
            string why = StockUiPartPurchase.DecideLive(parts[0]).Why;
            Assert.Equal(new[] { why, why }, dialogReasons.ToArray());
            Assert.All(dialogActions, a => Assert.Equal("Cannot purchase \"mk1pod.v2 title\"", a));
            Assert.Contains(logLines, l => l.Contains("[PartPurchasePatch]")
                && l.Contains("Blocking part purchase: 'mk1pod.v2'") && l.Contains("source=tooltip purchase"));
        }

        [Fact]
        public void Pairing_BypassOn_NothingGreyedAndNothingRefused()
        {
            GameStateRecorder.BypassEntryPurchaseAfterResearchProviderForTesting = () => true;
            Ledger.AddAction(Purchase(500, Pod, 1600f));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 100;
            var pod = Part(Pod);

            Assert.False(StockUiPartPurchase.DecideLive(pod).Blocked);
            Assert.True(InvokePrefix(typeof(PartListTooltipPurchasePatch), pod));
            Assert.True(InvokePrefix(typeof(RDTechPurchasePartPatch), pod));
            Assert.Empty(dialogReasons);
        }

        [Fact]
        public void RnDPurchaseAll_SkipsTheBlockedPart_WithoutAPerPartDialog()
        {
            Ledger.AddAction(Purchase(500, Pod, 1600f));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 100;

            StockUiPartPurchase.EnterPurchaseAll();
            bool podAllowed = InvokePrefix(typeof(RDTechPurchasePartPatch), Part(Pod));
            bool freeAllowed = InvokePrefix(typeof(RDTechPurchasePartPatch), Part(Free));
            StockUiPartPurchase.ExitPurchaseAll();

            Assert.False(podAllowed);
            Assert.True(freeAllowed);
            Assert.Empty(dialogReasons);
            Assert.Contains(logLines, l => l.Contains("[PartPurchasePatch]")
                && l.Contains("Blocking part purchase: 'mk1pod.v2'")
                && l.Contains("source=R&D purchase all") && l.Contains("dialog=no"));
            Assert.False(StockUiPartPurchase.InPurchaseAll);
        }

        [Fact]
        public void Backstop_BypassedDuringActionReplay()
        {
            Ledger.AddAction(Purchase(500, Pod, 1600f));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 100;
            using (SuppressionGuard.ResourcesAndReplay())
            {
                Assert.True(InvokePrefix(typeof(PartListTooltipPurchasePatch), Part(Pod)));
            }
            Assert.Empty(dialogReasons);
            Assert.Contains(logLines, l => l.Contains("[PartPurchasePatch]")
                && l.Contains("action replay in progress"));
        }

        // ---------------- state patch ----------------

        [Fact]
        public void StatePatch_Names_RowsAtOrBeforeTheCutoff_PartRowsOnly_SortedAndDeduped()
        {
            var build = Purchase(10, "notAPart", 500f);
            build.FundsSpendingSource = FundsSpendingSource.VesselBuild;
            var actions = new List<GameAction>
            {
                Purchase(300, Pod, 1600f),
                Purchase(100, Booster, 800f),
                Purchase(200, Booster, 800f),
                Purchase(301, Free, 100f),
                Purchase(50, "", 10f),
                build,
                // A zero-cost row (captured under bypass) still means the purchase happened.
                Purchase(20, "zeroCost", 0f)
            };

            Assert.Equal(new[] { Pod, Booster, "zeroCost" },
                KspStatePatcher.BuildPurchasedPartNamesForPatch(actions, 300).ToArray());
            Assert.Equal(new[] { Booster, "zeroCost" },
                KspStatePatcher.BuildPurchasedPartNamesForPatch(actions, 299.9).ToArray());
        }

        private sealed class FakeRnD
        {
            internal readonly Dictionary<string, string> TechOf = new Dictionary<string, string>();
            internal readonly HashSet<string> Researched = new HashSet<string>();
            internal readonly HashSet<string> Purchased = new HashSet<string>();
            internal int Writes;

            internal KspStatePatcher.PartPurchasePatchResult Apply(IEnumerable<string> names)
            {
                return KspStatePatcher.ApplyPartPurchasePatch(
                    names,
                    n => TechOf.TryGetValue(n, out var t) ? t : null,
                    t => Researched.Contains(t),
                    n => Purchased.Contains(n),
                    n => { Purchased.Add(n); Writes++; });
            }
        }

        [Fact]
        public void StatePatch_AddsOnly_IsIdempotent_AndSkipsUnresearchedTechAndUnknownParts()
        {
            var rnd = new FakeRnD();
            rnd.TechOf[Pod] = "start";
            rnd.TechOf[Booster] = "basicRocketry";
            rnd.TechOf[Free] = "start";
            rnd.Researched.Add("start");
            rnd.Purchased.Add(Free);
            rnd.Purchased.Add("someOtherPart");
            var names = new[] { Booster, Free, Pod, "notLoaded" };

            var first = rnd.Apply(names);
            Assert.Equal(1, first.Added);
            Assert.Equal(new[] { Pod }, first.AddedNames.ToArray());
            Assert.Equal(1, first.AlreadyPurchased);
            Assert.Equal(1, first.TechNotResearched);
            Assert.Equal(new[] { Booster }, first.TechNotResearchedNames.ToArray());
            Assert.Equal(1, first.UnknownPart);
            // Add-only: a purchased part the ledger does not name is left in place.
            Assert.Contains("someOtherPart", rnd.Purchased);
            Assert.DoesNotContain(Booster, rnd.Purchased);

            var second = rnd.Apply(names);
            Assert.Equal(0, second.Added);
            Assert.Equal(2, second.AlreadyPurchased);
            Assert.Equal(1, rnd.Writes);
        }

        [Fact]
        public void StatePatch_Cutoff_TechCutoffFirst_ThenWalkCutoff_ThenTheReadyLiveClock()
        {
            Assert.Equal(100.0, KspStatePatcher.ResolvePartPurchasePatchCutoff(200.0, 100.0, 300.0));
            Assert.Equal(200.0, KspStatePatcher.ResolvePartPurchasePatchCutoff(200.0, null, 300.0));
            Assert.Equal(300.0, KspStatePatcher.ResolvePartPurchasePatchCutoff(null, null, 300.0));
            // A not-ready clock (UT 0 on a cold load) never marks anything without an
            // explicit finite cutoff, and keeps an explicit one as given.
            Assert.Null(KspStatePatcher.ResolvePartPurchasePatchCutoff(null, null, 0.0));
            Assert.Equal(200.0, KspStatePatcher.ResolvePartPurchasePatchCutoff(null, 200.0, 0.0));
        }

        [Fact]
        public void StatePatch_Cutoff_IsClampedToTheReadyLiveClock()
        {
            // An explicit cutoff past now never marks a purchase the block still guards.
            Assert.Equal(300.0, KspStatePatcher.ResolvePartPurchasePatchCutoff(500.0, 500.0, 300.0));
            Assert.Equal(300.0, KspStatePatcher.ResolvePartPurchasePatchCutoff(500.0, null, 300.0));
        }

        [Theory]
        [InlineData(double.MaxValue)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NaN)]
        public void StatePatch_Cutoff_SentinelTechCutoff_CountsAsNoCutoff(double sentinel)
        {
            Assert.Equal(300.0, KspStatePatcher.ResolvePartPurchasePatchCutoff(null, sentinel, 300.0));
            Assert.Null(KspStatePatcher.ResolvePartPurchasePatchCutoff(null, sentinel, 0.0));
        }

        [Fact]
        public void StatePatch_TombstoneRefreshShape_MarksThePastPurchase_NotTheFutureOne()
        {
            // LedgerOrchestrator.RecalculateAndPatchAfterTombstones after retiring a
            // ScienceSpending row: utCutoff null, techPatchCutoff double.MaxValue. The walk
            // spans the whole timeline, but the add-only patch must stop at now.
            var actions = new List<GameAction>
            {
                Purchase(100, Booster, 800f),
                Purchase(300, Pod, 1600f)
            };
            double? cutoff;
            var names = KspStatePatcher.PlanPurchasedPartNamesForPatch(
                actions, walkCutoff: null, techPatchCutoff: double.MaxValue, liveUT: 200.0, out cutoff);

            Assert.Equal(200.0, cutoff);
            Assert.Equal(new[] { Booster }, names.ToArray());

            var rnd = new FakeRnD();
            rnd.TechOf[Pod] = "start";
            rnd.TechOf[Booster] = "start";
            rnd.Researched.Add("start");
            rnd.Apply(names);
            Assert.Contains(Booster, rnd.Purchased);
            Assert.DoesNotContain(Pod, rnd.Purchased);

            // So the future purchase stays blocked (stock still shows it unpurchased).
            var index = Index(actions.ToArray());
            Assert.True(StockUiReservationPredicates.IsPartPurchaseBlocked(
                index, Pod, 200.0, false, rnd.Purchased.Contains(Pod)));
        }

        [Fact]
        public void StatePatch_NoResearchAndDevelopment_SkipsAndLogs()
        {
            KspStatePatcher.PatchPurchasedParts(new List<GameAction> { Purchase(100, Pod, 1600f) }, 200.0, 200.0);
            Assert.Contains(logLines, l => l.Contains("[KspStatePatcher]")
                && l.Contains("PatchPurchasedParts: ResearchAndDevelopment.Instance is null"));
        }

        // ---------------- target resolution: every patched stock member ----------------

        private static MethodBase Resolve(Type patch)
        {
            Assert.NotEmpty(patch.GetCustomAttributes(typeof(HarmonyPatch), inherit: false));
            MethodInfo helper = patch.GetMethod("ResolveTargetMethodForTesting",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(helper);
            var method = helper.Invoke(null, null) as MethodBase;
            Assert.NotNull(method);
            return method;
        }

        private static string[] ParamNames(MethodBase m) => m.GetParameters().Select(p => p.Name).ToArray();

        [Fact]
        public void Target_PartListTooltipSetup_PartOverload_Resolves_WithTheInjectedParameterName()
        {
            var m = Resolve(typeof(PartListTooltipSetupPartPatch));
            Assert.Equal(typeof(PartListTooltip), m.DeclaringType);
            Assert.Equal(3, m.GetParameters().Length);
            Assert.Equal("availablePart", ParamNames(m)[0]);
            Assert.Equal("UnityEngine.UI.Button", typeof(PartListTooltip).GetField("buttonPurchase").FieldType.FullName);
            Assert.Equal("UnityEngine.UI.Button", typeof(PartListTooltip).GetField("buttonPurchaseRed").FieldType.FullName);
        }

        [Fact]
        public void Target_PartListTooltipSetup_UpgradeOverload_Resolves()
        {
            var m = Resolve(typeof(PartListTooltipSetupUpgradePatch));
            Assert.Equal(typeof(PartListTooltip), m.DeclaringType);
            Assert.Equal(typeof(PartUpgradeHandler.Upgrade), m.GetParameters()[1].ParameterType);
        }

        [Fact]
        public void Target_PartListTooltipControllerCreateTooltip_Resolves_AndTheGreyoutLabelExists()
        {
            var m = (MethodInfo)Resolve(typeof(PartListTooltipReasonPatch));
            Assert.Equal(typeof(PartListTooltipController), m.DeclaringType);
            Assert.True(m.IsPrivate);
            Assert.Equal(new[] { "tooltip", "partIcon" }, ParamNames(m));
            Assert.NotNull(typeof(PartListTooltip).GetField("textGreyoutMessage"));
        }

        [Fact]
        public void Target_PartListTooltipControllerOnPurchase_Resolves_AndThePartInfoFieldExists()
        {
            var m = Resolve(typeof(PartListTooltipPurchasePatch));
            Assert.Equal(typeof(PartListTooltipController), m.DeclaringType);
            Assert.Equal("onPurchase", m.Name);
            var field = typeof(PartListTooltipController).GetField("partInfo",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            Assert.Equal(typeof(AvailablePart), field.FieldType);
        }

        [Fact]
        public void Target_RDControllerActionButtonClick_Resolves_WithTheStateParameter()
        {
            var m = Resolve(typeof(RnDPurchaseAllPatch));
            Assert.Equal(typeof(RDController), m.DeclaringType);
            Assert.Equal(new[] { "state" }, ParamNames(m));
        }

        [Fact]
        public void Target_RDTechPurchasePart_Resolves_WithTheApParameter()
        {
            var m = Resolve(typeof(RDTechPurchasePartPatch));
            Assert.Equal(typeof(RDTech), m.DeclaringType);
            Assert.Equal(new[] { "ap" }, ParamNames(m));
        }
    }
}
