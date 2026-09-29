using System.Collections.Generic;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// M-C1 coverage for the pure KscAction accept / typed-refusal core: kind parse,
    /// manifest-kind mapping, and each refusal boundary. Fails if an unaffordable action is
    /// admitted (a false OK), a refusal maps to the wrong reason, or the manifest kind
    /// drifts. The Unity applier (real stock APIs + effect confirmation) is exercised
    /// in-game / PENDING-OPERATOR.
    /// </summary>
    public class TestCommandKscActionTests
    {
        // ----- ParseKind + ManifestKindFor -----

        [Fact]
        public void ParseKind_And_ManifestKind()
        {
            Assert.Equal(KscActionKind.ResearchNode, TestCommandKscAction.ParseKind("research-node"));
            Assert.Equal("tech-unlock", TestCommandKscAction.ManifestKindFor(KscActionKind.ResearchNode));

            Assert.Equal(KscActionKind.UpgradeFacility, TestCommandKscAction.ParseKind("upgrade-facility"));
            Assert.Equal("facility-upgrade", TestCommandKscAction.ManifestKindFor(KscActionKind.UpgradeFacility));

            Assert.Equal(KscActionKind.HireKerbal, TestCommandKscAction.ParseKind("hire-kerbal"));
            Assert.Equal("kerbal-hire", TestCommandKscAction.ManifestKindFor(KscActionKind.HireKerbal));

            Assert.Equal(KscActionKind.DismissKerbal, TestCommandKscAction.ParseKind("dismiss-kerbal"));
            Assert.Equal("kerbal-dismiss", TestCommandKscAction.ManifestKindFor(KscActionKind.DismissKerbal));
        }

        [Fact]
        public void ParseKind_And_ManifestKind_BuildingSubActions()
        {
            Assert.Equal(KscActionKind.DemolishBuilding, TestCommandKscAction.ParseKind("demolish-building"));
            Assert.Equal("facility-destruction", TestCommandKscAction.ManifestKindFor(KscActionKind.DemolishBuilding));
            Assert.Equal(KscActionKind.RepairFacility, TestCommandKscAction.ParseKind("repair-facility"));
            Assert.Equal("facility-repair", TestCommandKscAction.ManifestKindFor(KscActionKind.RepairFacility));
        }

        [Fact]
        public void Decide_Demolish_RefusalsAndAccept()
        {
            Assert.Equal("unknown-building", TestCommandKscAction.Decide("demolish-building", "x",
                new KscActionInputs { ArgPresent = true, TargetResolves = false }).RejectReason);
            Assert.Equal("building-already-down", TestCommandKscAction.Decide("demolish-building", "x",
                new KscActionInputs { ArgPresent = true, TargetResolves = true, AlreadyApplied = true }).RejectReason);
            var ok = TestCommandKscAction.Decide("demolish-building", "x",
                new KscActionInputs { ArgPresent = true, TargetResolves = true });
            Assert.True(ok.Accepted);
            Assert.Equal("facility-destruction", ok.ManifestKind);
        }

        [Fact]
        public void Decide_Repair_RefusalsAndAccept()
        {
            Assert.Equal("unknown-facility", TestCommandKscAction.Decide("repair-facility", "f",
                new KscActionInputs { ArgPresent = true, TargetResolves = false }).RejectReason);
            Assert.Equal("facility-intact", TestCommandKscAction.Decide("repair-facility", "f",
                new KscActionInputs { ArgPresent = true, TargetResolves = true, AlreadyApplied = true }).RejectReason);
            Assert.Equal("insufficient-funds", TestCommandKscAction.Decide("repair-facility", "f",
                new KscActionInputs { ArgPresent = true, TargetResolves = true, CostAmount = 100, AvailableAmount = 50 }).RejectReason);
            Assert.True(TestCommandKscAction.Decide("repair-facility", "f",
                new KscActionInputs { ArgPresent = true, TargetResolves = true, CostAmount = 50, AvailableAmount = 50 }).Accepted);
        }

        [Fact]
        public void ResolveBuildingId_ExactThenUniqueSuffix()
        {
            var ids = new[]
            {
                "SpaceCenter/LaunchPad/Facility/LaunchPadMedium/ksp_pad_waterTower",
                "SpaceCenter/LaunchPad/Facility/LaunchPadMedium/ksp_pad_cylTank",
                "SpaceCenter/Runway/Facility/a/Tank",
                "SpaceCenter/VehicleAssemblyBuilding/Facility/Tank",
            };
            Assert.Equal(ids[0], TestCommandKscAction.ResolveBuildingId(ids, "ksp_pad_waterTower"));
            Assert.Equal(ids[1], TestCommandKscAction.ResolveBuildingId(ids, ids[1]));
            // Ambiguous suffix and a partial segment both resolve to nothing.
            Assert.Null(TestCommandKscAction.ResolveBuildingId(ids, "Tank"));
            Assert.Null(TestCommandKscAction.ResolveBuildingId(ids, "waterTower"));
            Assert.Null(TestCommandKscAction.ResolveBuildingId(ids, ""));
            Assert.Null(TestCommandKscAction.ResolveBuildingId(null, "x"));
        }

        [Fact]
        public void AnyStructureSettling_OnlyTheBetweenStatesPair()
        {
            // (IsIntact, IsDestroyed): intact, destroyed, and mid-animation.
            var intact = new KeyValuePair<bool, bool>(true, false);
            var down = new KeyValuePair<bool, bool>(false, true);
            var moving = new KeyValuePair<bool, bool>(false, false);
            Assert.False(TestCommandKscAction.AnyStructureSettling(new[] { intact, down }));
            Assert.True(TestCommandKscAction.AnyStructureSettling(new[] { intact, moving }));
            Assert.False(TestCommandKscAction.AnyStructureSettling(null));
        }

        [Theory]
        [InlineData("ResearchNode")]      // wrong case
        [InlineData("research_node")]     // wrong separator
        [InlineData("")]
        [InlineData(null)]
        [InlineData("complete-contract")] // deferred batch-2 sub-action
        public void ParseKind_Unknown(string action)
        {
            Assert.Equal(KscActionKind.Unknown, TestCommandKscAction.ParseKind(action));
        }

        // ----- Decide: unknown-action / missing-arg -----

        [Fact]
        public void Decide_UnknownAction_Rejects()
        {
            var d = TestCommandKscAction.Decide("no-such-action", "x", new KscActionInputs { ArgPresent = true, TargetResolves = true });
            Assert.False(d.Accepted);
            Assert.Equal("unknown-action", d.RejectReason);
        }

        [Fact]
        public void Decide_MissingArg_Rejects()
        {
            var d = TestCommandKscAction.Decide("research-node", "", new KscActionInputs { ArgPresent = false });
            Assert.False(d.Accepted);
            Assert.Equal("missing-arg", d.RejectReason);
        }

        // ----- research-node -----

        private static KscActionInputs Research(bool resolves, bool already, double cost, double science)
            => new KscActionInputs
            {
                ArgPresent = true, TargetResolves = resolves, AlreadyApplied = already,
                CostAmount = cost, AvailableAmount = science, CostIsFunds = false,
            };

        [Fact]
        public void Decide_Research_Accept()
        {
            var d = TestCommandKscAction.Decide("research-node", "basicRocketry", Research(true, false, 45, 100));
            Assert.True(d.Accepted);
            Assert.Null(d.RejectReason);
            Assert.Equal("tech-unlock", d.ManifestKind);
            Assert.Equal(KscActionKind.ResearchNode, d.Kind);
        }

        [Fact]
        public void Decide_Research_UnknownNode()
        {
            var d = TestCommandKscAction.Decide("research-node", "nope", Research(false, false, 0, 100));
            Assert.Equal("unknown-tech-node", d.RejectReason);
        }

        [Fact]
        public void Decide_Research_AlreadyUnlocked()
        {
            var d = TestCommandKscAction.Decide("research-node", "basicRocketry", Research(true, true, 45, 100));
            Assert.Equal("node-already-unlocked", d.RejectReason);
        }

        [Fact]
        public void Decide_Research_InsufficientScience()
        {
            var d = TestCommandKscAction.Decide("research-node", "basicRocketry", Research(true, false, 45, 44.9));
            Assert.Equal("insufficient-science", d.RejectReason);
        }

        [Fact]
        public void Decide_Research_ExactCost_Affordable()
        {
            // cost == available is affordable (cost > available is the refusal boundary).
            var d = TestCommandKscAction.Decide("research-node", "basicRocketry", Research(true, false, 45, 45));
            Assert.True(d.Accepted);
        }

        // ----- upgrade-facility -----

        private static KscActionInputs Facility(bool resolves, bool atMax, double cost, double funds)
            => new KscActionInputs
            {
                ArgPresent = true, TargetResolves = resolves, AlreadyApplied = atMax,
                CostAmount = cost, AvailableAmount = funds, CostIsFunds = true,
            };

        [Fact]
        public void Decide_Facility_Accept()
        {
            var d = TestCommandKscAction.Decide("upgrade-facility", "LaunchPad", Facility(true, false, 20000, 100000));
            Assert.True(d.Accepted);
            Assert.Equal("facility-upgrade", d.ManifestKind);
        }

        [Fact]
        public void Decide_Facility_UnknownFacility()
        {
            var d = TestCommandKscAction.Decide("upgrade-facility", "Nope", Facility(false, false, 0, 100000));
            Assert.Equal("unknown-facility", d.RejectReason);
        }

        [Fact]
        public void Decide_Facility_AtMax()
        {
            var d = TestCommandKscAction.Decide("upgrade-facility", "LaunchPad", Facility(true, true, 20000, 100000));
            Assert.Equal("facility-at-max", d.RejectReason);
        }

        [Fact]
        public void Decide_Facility_InsufficientFunds()
        {
            var d = TestCommandKscAction.Decide("upgrade-facility", "LaunchPad", Facility(true, false, 20000, 19999));
            Assert.Equal("insufficient-funds", d.RejectReason);
        }

        // ----- hire-kerbal -----

        private static KscActionInputs Hire(bool resolves, bool applicant, double cost, double funds)
            => new KscActionInputs
            {
                ArgPresent = true, TargetResolves = resolves, IsApplicant = applicant,
                CostAmount = cost, AvailableAmount = funds, CostIsFunds = true,
            };

        [Fact]
        public void Decide_Hire_Accept()
        {
            var d = TestCommandKscAction.Decide("hire-kerbal", "Jeb Kerman", Hire(true, true, 40000, 100000));
            Assert.True(d.Accepted);
            Assert.Equal("kerbal-hire", d.ManifestKind);
        }

        [Fact]
        public void Decide_Hire_UnknownKerbal()
        {
            var d = TestCommandKscAction.Decide("hire-kerbal", "Nobody", Hire(false, false, 0, 100000));
            Assert.Equal("unknown-kerbal", d.RejectReason);
        }

        [Fact]
        public void Decide_Hire_NotApplicant()
        {
            // Resolves in the roster but not in the applicant pool (already crew).
            var d = TestCommandKscAction.Decide("hire-kerbal", "Jeb Kerman", Hire(true, false, 40000, 100000));
            Assert.Equal("kerbal-not-applicant", d.RejectReason);
        }

        [Fact]
        public void Decide_Hire_InsufficientFunds()
        {
            var d = TestCommandKscAction.Decide("hire-kerbal", "Jeb Kerman", Hire(true, true, 40000, 39999));
            Assert.Equal("insufficient-funds", d.RejectReason);
        }

        // ----- dismiss-kerbal -----

        private static KscActionInputs Dismiss(bool resolves, bool managed, bool dismissable)
            => new KscActionInputs
            {
                ArgPresent = true, TargetResolves = resolves, IsParsekManaged = managed, IsDismissable = dismissable,
            };

        [Fact]
        public void Decide_Dismiss_Accept()
        {
            var d = TestCommandKscAction.Decide("dismiss-kerbal", "Bob Kerman", Dismiss(true, false, true));
            Assert.True(d.Accepted);
            Assert.Equal("kerbal-dismiss", d.ManifestKind);
        }

        [Fact]
        public void Decide_Dismiss_UnknownKerbal()
        {
            var d = TestCommandKscAction.Decide("dismiss-kerbal", "Nobody", Dismiss(false, false, false));
            Assert.Equal("unknown-kerbal", d.RejectReason);
        }

        [Fact]
        public void Decide_Dismiss_ParsekManaged_TakesPrecedence()
        {
            // A Parsek-managed kerbal is pre-declined before the dismissability check,
            // mirroring the KerbalDismissalPatch IsManaged block.
            var d = TestCommandKscAction.Decide("dismiss-kerbal", "Jeb Kerman", Dismiss(true, managed: true, dismissable: false));
            Assert.Equal("kerbal-parsek-managed", d.RejectReason);
        }

        [Fact]
        public void Decide_Dismiss_NotDismissable()
        {
            // Not managed, but assigned / tourist / protected.
            var d = TestCommandKscAction.Decide("dismiss-kerbal", "Val Kerman", Dismiss(true, managed: false, dismissable: false));
            Assert.Equal("kerbal-not-dismissable", d.RejectReason);
        }

        // ----- activate-strategy / deactivate-strategy -----

        private static KscActionInputs Strategy(bool resolves = true, bool applied = false,
            bool factorInvalid = false, int used = 0, int limit = 1)
            => new KscActionInputs
            {
                ArgPresent = true, TargetResolves = resolves, AlreadyApplied = applied,
                FactorArgInvalid = factorInvalid, SlotsUsed = used, SlotLimit = limit,
            };

        [Fact]
        public void ParseKind_And_ManifestKind_StrategySubActions()
        {
            Assert.Equal(KscActionKind.ActivateStrategy, TestCommandKscAction.ParseKind("activate-strategy"));
            Assert.Equal("strategy-activate", TestCommandKscAction.ManifestKindFor(KscActionKind.ActivateStrategy));
            Assert.Equal(KscActionKind.DeactivateStrategy, TestCommandKscAction.ParseKind("deactivate-strategy"));
            Assert.Equal("strategy-deactivate", TestCommandKscAction.ManifestKindFor(KscActionKind.DeactivateStrategy));
            // Exact kebab-case only, like every other sub-action.
            Assert.Equal(KscActionKind.Unknown, TestCommandKscAction.ParseKind("Activate-Strategy"));
        }

        [Fact]
        public void Decide_ActivateStrategy_RefusalsInOrder()
        {
            Assert.Equal("missing-arg", TestCommandKscAction.Decide("activate-strategy", null,
                new KscActionInputs { ArgPresent = false }).RejectReason);
            Assert.Equal("unknown-strategy", TestCommandKscAction.Decide("activate-strategy", "Nope",
                Strategy(resolves: false)).RejectReason);
            // A bad factor is named before the strategy's own state, so a typo never reads
            // as "already active" or "no slot".
            Assert.Equal("factor-arg-invalid", TestCommandKscAction.Decide("activate-strategy", "S",
                Strategy(factorInvalid: true, applied: true, used: 1, limit: 1)).RejectReason);
            Assert.Equal("strategy-already-active", TestCommandKscAction.Decide("activate-strategy", "S",
                Strategy(applied: true, used: 1, limit: 1)).RejectReason);
            Assert.Equal("no-strategy-slot", TestCommandKscAction.Decide("activate-strategy", "S",
                Strategy(used: 1, limit: 1)).RejectReason);
            // A zero limit (the read failed) is a refusal, never a free slot.
            Assert.Equal("no-strategy-slot", TestCommandKscAction.Decide("activate-strategy", "S",
                Strategy(used: 0, limit: 0)).RejectReason);
        }

        [Fact]
        public void Decide_ActivateStrategy_Accepts_WithAFreeSlot()
        {
            var d = TestCommandKscAction.Decide("activate-strategy", "OutsourcedResearchCfg", Strategy(used: 1, limit: 2));
            Assert.True(d.Accepted);
            Assert.Null(d.RejectReason);
            Assert.Equal("strategy-activate", d.ManifestKind);
            Assert.Equal("OutsourcedResearchCfg", d.Target);
        }

        [Fact]
        public void Decide_DeactivateStrategy_RefusalsAndAccept()
        {
            Assert.Equal("unknown-strategy", TestCommandKscAction.Decide("deactivate-strategy", "Nope",
                Strategy(resolves: false)).RejectReason);
            Assert.Equal("strategy-not-active", TestCommandKscAction.Decide("deactivate-strategy", "S",
                Strategy(applied: true)).RejectReason);
            // Slots do not gate a cancel.
            var d = TestCommandKscAction.Decide("deactivate-strategy", "S", Strategy(used: 1, limit: 1));
            Assert.True(d.Accepted);
            Assert.Equal("strategy-deactivate", d.ManifestKind);
        }

        [Theory]
        [InlineData(null, true, float.NaN)]
        [InlineData("", true, float.NaN)]
        [InlineData("0.05", true, 0.05f)]
        [InlineData("1", true, 1f)]
        [InlineData("0.25", true, 0.25f)]
        [InlineData("0", false, float.NaN)]
        [InlineData("-0.1", false, float.NaN)]
        [InlineData("1.5", false, float.NaN)]
        [InlineData("NaN", false, float.NaN)]
        [InlineData("Infinity", false, float.NaN)]
        [InlineData("abc", false, float.NaN)]
        public void TryParseStrategyFactor_AcceptsOnlyAHalfOpenUnitRange(string raw, bool ok, float expected)
        {
            Assert.Equal(ok, TestCommandKscAction.TryParseStrategyFactor(raw, out float f));
            if (float.IsNaN(expected)) Assert.True(float.IsNaN(f));
            else Assert.Equal(expected, f);
        }

        [Fact]
        public void TryParseStrategyFactor_IsCultureInvariant()
        {
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Assert.True(TestCommandKscAction.TryParseStrategyFactor("0.5", out float f));
                Assert.Equal(0.5f, f);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Theory]
        [InlineData(null, "(none)")]
        [InlineData("", "(none)")]
        [InlineData("   ", "(none)")]
        [InlineData("Not enough funds", "Not enough funds")]
        [InlineData("<color=orange>Requires 20%\ncommitment</color>", "Requires 20% commitment")]
        [InlineData("  a \t\r\n  b  ", "a b")]
        public void SanitizeStockReason_OneLineNoTags(string raw, string expected)
        {
            Assert.Equal(expected, TestCommandKscAction.SanitizeStockReason(raw));
        }

        /// <summary>KB-2 reads funds unchanged across a refused repair from this line
        /// (a backreference over fundsBefore / fundsAfter), so its shape is pinned, and it
        /// must print invariantly whatever the host culture.</summary>
        [Fact]
        public void FormatRepairNotAppliedLine_PinnedShape_CultureInvariant()
        {
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Assert.Equal(
                    "kscaction repair-facility not applied: target=SpaceCenter/TrackingStation "
                    + "destroyedBefore=1 destroyedAfter=1 fundsBefore=465808.5 fundsAfter=465808.5 fundsDelta=0",
                    TestCommandKscAction.FormatRepairNotAppliedLine("SpaceCenter/TrackingStation", 1, 1,
                        465808.5, 465808.5));
                Assert.Contains("target= ", TestCommandKscAction.FormatRepairNotAppliedLine(null, 0, 0, 0, 0));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        // ----- accept-contract -----

        private static KscActionInputs ContractInputs(bool resolves = true, bool notOffered = false)
            => new KscActionInputs { ArgPresent = true, TargetResolves = resolves, AlreadyApplied = notOffered };

        [Fact]
        public void ParseKind_And_ManifestKind_AcceptContract()
        {
            Assert.Equal(KscActionKind.AcceptContract, TestCommandKscAction.ParseKind("accept-contract"));
            Assert.Equal("contract-accept", TestCommandKscAction.ManifestKindFor(KscActionKind.AcceptContract));
            Assert.Equal(KscActionKind.Unknown, TestCommandKscAction.ParseKind("Accept-Contract"));
            Assert.Equal(KscActionKind.Unknown, TestCommandKscAction.ParseKind("accept"));
        }

        [Fact]
        public void Decide_AcceptContract_RefusalsInOrder_ThenAccepts()
        {
            Assert.Equal("missing-arg", TestCommandKscAction.Decide("accept-contract", null,
                new KscActionInputs { ArgPresent = false }).RejectReason);
            // An unknown guid is named before the contract's state, so a typo never reads
            // as "not offered".
            Assert.Equal("unknown-contract", TestCommandKscAction.Decide("accept-contract", "g",
                ContractInputs(resolves: false, notOffered: true)).RejectReason);
            Assert.Equal("contract-not-offered", TestCommandKscAction.Decide("accept-contract", "g",
                ContractInputs(notOffered: true)).RejectReason);

            var d = TestCommandKscAction.Decide("accept-contract", "90e4faaf-2029-4c6b-8bc1-40226bb0fc27", ContractInputs());
            Assert.True(d.Accepted);
            Assert.Null(d.RejectReason);
            Assert.Equal("contract-accept", d.ManifestKind);
            Assert.Equal("90e4faaf-2029-4c6b-8bc1-40226bb0fc27", d.Target);
        }

        [Theory]
        [InlineData("90e4faaf-2029-4c6b-8bc1-40226bb0fc27", "90e4faaf-2029-4c6b-8bc1-40226bb0fc27", true)]
        [InlineData("90e4faaf-2029-4c6b-8bc1-40226bb0fc27", "90E4FAAF-2029-4C6B-8BC1-40226BB0FC27", true)]
        [InlineData("90e4faaf-2029-4c6b-8bc1-40226bb0fc27", "{90e4faaf-2029-4c6b-8bc1-40226bb0fc27}", true)]
        [InlineData("90e4faaf-2029-4c6b-8bc1-40226bb0fc27", "90e4faaf", false)]
        [InlineData("90e4faaf-2029-4c6b-8bc1-40226bb0fc27", "", false)]
        [InlineData(null, "90e4faaf-2029-4c6b-8bc1-40226bb0fc27", false)]
        public void ContractGuidMatches_WholeGuidOnly(string guid, string arg, bool expected)
        {
            Assert.Equal(expected, TestCommandKscAction.ContractGuidMatches(guid, arg));
        }

        /// <summary>KB-3 reads each refused click's state, funds and science unchanged from
        /// this line (backreferences over the Before / After pairs), so its shape is pinned,
        /// and it must print invariantly whatever the host culture.</summary>
        [Fact]
        public void FormatNotAppliedLine_PinnedShape_CultureInvariant()
        {
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Assert.Equal(
                    "kscaction hire-kerbal not applied: target=Verhat_Kerman typeBefore=Applicant typeAfter=Applicant "
                    + "fundsBefore=232416.75 fundsAfter=232416.75 fundsDelta=0 "
                    + "scienceBefore=111.6 scienceAfter=111.6 scienceDelta=0",
                    TestCommandKscAction.FormatNotAppliedLine("hire-kerbal", "Verhat Kerman", "type",
                        "Applicant", "Applicant", 232416.75, 232416.75, 111.6, 111.6));
                // A changed pool prints its delta, so a lane backreference cannot pass on it.
                Assert.Contains("fundsDelta=-150000 ",
                    TestCommandKscAction.FormatNotAppliedLine("upgrade-facility", "SpaceCenter/TrackingStation",
                        "level", "0", "0", 232416.75, 82416.75, 0, 0));
                Assert.Contains("target=- stateBefore=- stateAfter=- ",
                    TestCommandKscAction.FormatNotAppliedLine("accept-contract", null, "state", null, "", 0, 0, 0, 0));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        // ----- The stock-UI click-block sub-actions (KB-4) -----

        [Theory]
        [InlineData("decline-contract", "DeclineContract", "contract-decline")]
        [InlineData("cancel-contract", "CancelContract", "contract-cancel")]
        [InlineData("sack-kerbal", "SackKerbal", "kerbal-dismiss")]
        [InlineData("press-strategy", "PressStrategyButton", "strategy-button")]
        [InlineData("purchase-part", "PurchasePart", "part-purchase")]
        [InlineData("purchase-all", "PurchaseAllParts", "part-purchase")]
        [InlineData("assign-crew", "AssignCrew", "crew-assign")]
        public void ParseKind_And_ManifestKind_ClickBlockSubActions(string action, string kind, string manifest)
        {
            KscActionKind parsed = TestCommandKscAction.ParseKind(action);
            Assert.Equal(kind, parsed.ToString());
            Assert.Equal(manifest, TestCommandKscAction.ManifestKindFor(parsed));
        }

        [Theory]
        [InlineData("decline-contract", "unknown-contract")]
        [InlineData("cancel-contract", "unknown-contract")]
        [InlineData("sack-kerbal", "unknown-kerbal")]
        [InlineData("assign-crew", "unknown-kerbal")]
        [InlineData("press-strategy", "unknown-strategy")]
        [InlineData("purchase-part", "unknown-part")]
        [InlineData("purchase-all", "unknown-tech-node")]
        public void Decide_ClickBlock_UnknownTargetNamedFirst(string action, string reason)
        {
            // Every other input would refuse too; the unknown target is named first so a
            // typo never reads as a state refusal.
            var d = TestCommandKscAction.Decide(action, "x", new KscActionInputs
            {
                ArgPresent = true, TargetResolves = false, AlreadyApplied = true,
            });
            Assert.False(d.Accepted);
            Assert.Equal(reason, d.RejectReason);
        }

        [Fact]
        public void Decide_DeclineAndCancel_StateRefusalsThenAccept()
        {
            Assert.Equal("contract-not-offered", TestCommandKscAction.Decide("decline-contract", "g",
                ContractInputs(notOffered: true)).RejectReason);
            Assert.Equal("contract-not-active", TestCommandKscAction.Decide("cancel-contract", "g",
                ContractInputs(notOffered: true)).RejectReason);
            Assert.True(TestCommandKscAction.Decide("decline-contract", "g", ContractInputs()).Accepted);
            Assert.True(TestCommandKscAction.Decide("cancel-contract", "g", ContractInputs()).Accepted);
        }

        [Fact]
        public void Decide_Sack_AsksNoParsekPredicate()
        {
            // A Parsek-managed kerbal is still admitted: the backstop on stock's call is
            // what the lane proves, so the seam must reach it.
            var admitted = TestCommandKscAction.Decide("sack-kerbal", "Bill Kerman", new KscActionInputs
            {
                ArgPresent = true, TargetResolves = true, IsParsekManaged = true, IsDismissable = true,
            });
            Assert.True(admitted.Accepted);
            Assert.Equal("kerbal-not-dismissable", TestCommandKscAction.Decide("sack-kerbal", "Bill Kerman",
                new KscActionInputs { ArgPresent = true, TargetResolves = true }).RejectReason);
        }

        [Fact]
        public void Decide_PressStrategy_NeedsTheOpenScreenWithTheStrategySelected()
        {
            var inputs = new KscActionInputs { ArgPresent = true, TargetResolves = true };
            Assert.Equal("administration-not-open",
                TestCommandKscAction.Decide("press-strategy", "s", inputs).RejectReason);
            inputs.ScreenOpen = true;
            Assert.Equal("strategy-not-selected",
                TestCommandKscAction.Decide("press-strategy", "s", inputs).RejectReason);
            inputs.TargetSelected = true;
            Assert.True(TestCommandKscAction.Decide("press-strategy", "s", inputs).Accepted);
        }

        [Theory]
        [InlineData("purchase-part", "part-tech-not-researched", "part-already-purchased")]
        [InlineData("purchase-all", "node-not-researched", "nothing-to-purchase")]
        public void Decide_Purchase_RefusalsInOrder(string action, string notResearched, string nothingLeft)
        {
            var inputs = new KscActionInputs { ArgPresent = true, TargetResolves = true, AlreadyApplied = true };
            Assert.Equal("rnd-not-open", TestCommandKscAction.Decide(action, "x", inputs).RejectReason);
            inputs.ScreenOpen = true;
            Assert.Equal(notResearched, TestCommandKscAction.Decide(action, "x", inputs).RejectReason);
            inputs.PrerequisiteMet = true;
            Assert.Equal(nothingLeft, TestCommandKscAction.Decide(action, "x", inputs).RejectReason);
            inputs.AlreadyApplied = false;
            Assert.True(TestCommandKscAction.Decide(action, "x", inputs).Accepted);
        }

        [Fact]
        public void Decide_AssignCrew_RefusalsThenAccept()
        {
            var inputs = new KscActionInputs { ArgPresent = true, TargetResolves = true, AlreadyApplied = true };
            Assert.Equal("crew-dialog-not-open", TestCommandKscAction.Decide("assign-crew", "k", inputs).RejectReason);
            inputs.ScreenOpen = true;
            Assert.Equal("kerbal-already-assigned", TestCommandKscAction.Decide("assign-crew", "k", inputs).RejectReason);
            inputs.AlreadyApplied = false;
            Assert.True(TestCommandKscAction.Decide("assign-crew", "k", inputs).Accepted);
        }

        [Fact]
        public void AppendRepFields_PinnedShape_CultureInvariant()
        {
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Assert.Equal("L repBefore=12.5 repAfter=12.5 repDelta=0",
                    TestCommandKscAction.AppendRepFields("L", 12.5, 12.5));
                Assert.EndsWith(" repDelta=-2.5", TestCommandKscAction.AppendRepFields("L", 12.5, 10.0));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }
    }
}
