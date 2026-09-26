using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// S9 (owner ruling 2026-09-26): Parsek is inert in MISSION, MISSION_BUILDER, SCENARIO
    /// and SCENARIO_NON_RESUMABLE games. Covers the pure predicate over every
    /// <see cref="Game.Modes"/> member, the once-per-scene log line, the inert
    /// ParsekScenario load/save pass-through, two behavioral entry points, and an IL check
    /// that every gated entry point really calls the gate.
    /// </summary>
    [Collection("Sequential")]
    public class ParsekGameModeGateTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ParsekGameModeGateTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekGameModeGate.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RevertInterceptor.ResetTestOverrides();
        }

        public void Dispose()
        {
            ParsekGameModeGate.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RevertInterceptor.ResetTestOverrides();
            ParsekLog.ResetTestOverrides();
        }

        private static readonly Game.Modes[] ActiveModes =
        {
            Game.Modes.SANDBOX, Game.Modes.CAREER, Game.Modes.SCIENCE_SANDBOX,
        };

        private static readonly Game.Modes[] InertModes =
        {
            Game.Modes.MISSION, Game.Modes.MISSION_BUILDER,
            Game.Modes.SCENARIO, Game.Modes.SCENARIO_NON_RESUMABLE,
        };

        [Fact]
        public void EveryEnumMember_IsClassified_ActiveExactlyForSandboxCareerScience()
        {
            var all = (Game.Modes[])Enum.GetValues(typeof(Game.Modes));
            // A new KSP mode must be classified on purpose; this pins the member set.
            Assert.Equal(7, all.Length);
            foreach (var mode in all)
            {
                bool expectedActive = ActiveModes.Contains(mode);
                Assert.True(expectedActive || InertModes.Contains(mode), "unclassified mode " + mode);
                Assert.Equal(expectedActive, ParsekGameModeGate.IsActiveMode(mode));
                string reason = ParsekGameModeGate.InertReason(mode);
                if (expectedActive)
                    Assert.Null(reason);
                else
                    Assert.False(string.IsNullOrEmpty(reason), "inert mode needs a reason: " + mode);
            }
        }

        [Fact]
        public void UnknownModeValue_IsInert_FailClosed()
        {
            var unknown = (Game.Modes)99;
            Assert.False(ParsekGameModeGate.IsActiveMode(unknown));
            Assert.Equal("unknown game mode", ParsekGameModeGate.InertReason(unknown));
        }

        [Fact]
        public void NoCurrentGame_ReadsAsNotInert()
        {
            // xUnit has no HighLogic.CurrentGame: behavior outside a loaded game is unchanged.
            Assert.False(ParsekGameModeGate.CheckInert("test"));
            Assert.False(ParsekGameModeGate.IsInertForCurrentGame);
            Assert.DoesNotContain(logLines, l => l.Contains("[GameModeGate]"));
        }

        [Theory]
        [InlineData(Game.Modes.SANDBOX)]
        [InlineData(Game.Modes.CAREER)]
        [InlineData(Game.Modes.SCIENCE_SANDBOX)]
        public void ActiveMode_CheckInertFalse_NoLog(Game.Modes mode)
        {
            ParsekGameModeGate.ModeOverrideForTesting = mode;
            Assert.False(ParsekGameModeGate.CheckInert("test"));
            Assert.False(ParsekGameModeGate.IsInertForCurrentGame);
            Assert.DoesNotContain(logLines, l => l.Contains("[GameModeGate]"));
        }

        [Theory]
        [InlineData(Game.Modes.MISSION, "Making History mission")]
        [InlineData(Game.Modes.MISSION_BUILDER, "Making History mission builder")]
        [InlineData(Game.Modes.SCENARIO, "stock scenario save")]
        [InlineData(Game.Modes.SCENARIO_NON_RESUMABLE, "stock training / non-resumable scenario")]
        public void InertMode_LogsOncePerScene_NamingModeReasonAndSite(Game.Modes mode, string reason)
        {
            ParsekGameModeGate.ModeOverrideForTesting = mode;
            ParsekGameModeGate.SceneOverrideForTesting = GameScenes.FLIGHT;

            Assert.True(ParsekGameModeGate.CheckInert("ParsekFlight.Start"));
            Assert.True(ParsekGameModeGate.CheckInert("PhysicsFramePatch.Postfix"));
            Assert.True(ParsekGameModeGate.CheckInert("PhysicsFramePatch.Postfix"));
            Assert.True(ParsekGameModeGate.IsInertForCurrentGame);

            var gateLines = logLines.Where(l => l.Contains("[GameModeGate]")).ToList();
            Assert.Single(gateLines);
            Assert.Contains("[INFO]", gateLines[0]);
            Assert.Contains("game mode " + mode + " (" + reason + ")", gateLines[0]);
            Assert.Contains("scene=FLIGHT", gateLines[0]);
            Assert.Contains("site=ParsekFlight.Start", gateLines[0]);

            // A new scene logs again, once.
            ParsekGameModeGate.SceneOverrideForTesting = GameScenes.SPACECENTER;
            Assert.True(ParsekGameModeGate.CheckInert("ParsekKSC.Start"));
            Assert.True(ParsekGameModeGate.CheckInert("StockUiOverlayController.Awake"));
            gateLines = logLines.Where(l => l.Contains("[GameModeGate]")).ToList();
            Assert.Equal(2, gateLines.Count);
            Assert.Contains("scene=SPACECENTER site=ParsekKSC.Start", gateLines[1]);
        }

        [Fact]
        public void InertScenario_LoadThenSave_RoundTripsTheParsekNodeVerbatim()
        {
            ParsekGameModeGate.ModeOverrideForTesting = Game.Modes.MISSION;
            var loaded = new ConfigNode("SCENARIO");
            loaded.AddValue("name", "ParsekScenario");
            loaded.AddValue("scene", "7, 5, 8, 6");
            loaded.AddValue("someParsekKey", "42");
            var tree = loaded.AddNode("RECORDING_TREE");
            tree.AddValue("id", "tree_a");
            tree.AddNode("RECORDING").AddValue("recordingId", "rec_1");

            // The exact bodies OnLoad / OnSave run once CheckInert is true (the IL cells
            // below pin that both call the gate; OnLoad itself cannot JIT headless).
            var scenario = new ParsekScenario();
            Assert.True(ParsekGameModeGate.CheckInert("ParsekScenario.OnLoad"));
            scenario.StashInertGameModeNode(loaded);
            // A later mutation of the node KSP handed in must not leak into the save.
            loaded.SetValue("someParsekKey", "mutated");

            // KSP builds a fresh node per save: name + scene, then OnSave.
            var saved = new ConfigNode("SCENARIO");
            saved.AddValue("name", "ParsekScenario");
            saved.AddValue("scene", "7, 5, 8, 6");
            Assert.True(ParsekGameModeGate.CheckInert("ParsekScenario.OnSave"));
            scenario.WriteInertGameModeNode(saved);

            Assert.Equal(new[] { "ParsekScenario" }, saved.GetValues("name"));
            Assert.Equal(new[] { "7, 5, 8, 6" }, saved.GetValues("scene"));
            Assert.Equal("42", saved.GetValue("someParsekKey"));
            var savedTree = saved.GetNode("RECORDING_TREE");
            Assert.NotNull(savedTree);
            Assert.Equal("tree_a", savedTree.GetValue("id"));
            Assert.Equal("rec_1", savedTree.GetNode("RECORDING").GetValue("recordingId"));
            Assert.Contains(logLines, l => l.Contains("[Scenario]") && l.Contains("OnLoad: inert game mode"));
            Assert.Contains(logLines, l => l.Contains("[Scenario]") && l.Contains("OnSave: inert game mode")
                && l.Contains("(2 entries)"));
            // Nothing else ran: no committed-recordings save line.
            Assert.DoesNotContain(logLines, l => l.Contains("OnSave: saving"));
        }

        [Fact]
        public void CopyInertPassthroughNode_NullLoaded_WritesNothing()
        {
            var target = new ConfigNode("SCENARIO");
            target.AddValue("name", "ParsekScenario");
            Assert.Equal(0, ParsekScenario.CopyInertPassthroughNode(null, target));
            Assert.Single(target.values.Cast<ConfigNode.Value>());
            Assert.Empty(target.nodes.Cast<ConfigNode>());
        }

        [Fact]
        public void RevertInterceptor_InertMode_LetsStockRevertRunEvenWithAReFlyMarker()
        {
            var scenario = new ParsekScenario
            {
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = "sess_s9", TreeId = "t", ActiveReFlyRecordingId = "r",
                    OriginChildRecordingId = "o", RewindPointId = "rp",
                },
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            int dialogs = 0;
            RevertInterceptor.DialogShowForTesting = _ => dialogs++;

            // Control: an active game blocks stock and shows the re-fly dialog.
            ParsekGameModeGate.ModeOverrideForTesting = Game.Modes.CAREER;
            Assert.False(RevertInterceptor.Prefix(RevertTarget.Launch));
            Assert.Equal(1, dialogs);

            ParsekGameModeGate.ModeOverrideForTesting = Game.Modes.MISSION;
            Assert.True(RevertInterceptor.Prefix(RevertTarget.Launch));
            Assert.Equal(1, dialogs);
        }

        [Fact]
        public void SceneExitPrefix_InertMode_ReturnsTrueWithoutConsumingTheToken()
        {
            ParsekGameModeGate.ModeOverrideForTesting = Game.Modes.SCENARIO;
            SceneExitInterceptor.ResetTestOverrides();
            try
            {
                SceneExitInterceptor.s_AllowNextLoadScene = true;
                SceneExitInterceptor.s_AllowNextLoadSceneDestination = GameScenes.SPACECENTER;
                Assert.True(HighLogic_LoadScene_Patch.Prefix(GameScenes.SPACECENTER));
                Assert.True(SceneExitInterceptor.s_AllowNextLoadScene);
            }
            finally
            {
                SceneExitInterceptor.ResetTestOverrides();
            }
        }

        // ---------- IL check: every gated entry point calls the gate ----------

        // Every entry point S9 gates, as declaring type + method. The patch rows are the
        // classes whose Prefix / Postfix blocks, decorates or captures against a stock
        // control or the ledger; pure ghost-pid filters are deliberately absent (they act
        // only on ghost vessels, and an inert game creates none).
        private static readonly string[] GatedEntryPointNames =
        {
            "Parsek.ParsekScenario.OnLoad", "Parsek.ParsekScenario.OnSave",
            "Parsek.ParsekFlight.Start", "Parsek.ParsekKSC.Start", "Parsek.ParsekTrackingStation.Start",
            "Parsek.StockUiOverlayController.Awake", "Parsek.CurrencyReservationOverlay.Start",
            "Parsek.WarpToTimeConsumer.HandleLevelWasLoaded",
            "Parsek.RevertInterceptor.Prefix", "Parsek.HighLogic_LoadScene_Patch.Prefix",
            "Parsek.Patches.ActiveCrewCountPatch.Postfix",
            "Parsek.Patches.AdministrationButtonBackstopPatch.Prefix",
            "Parsek.Patches.AdministrationSetSelectedStrategyPatch.Postfix",
            "Parsek.Patches.AdministrationUpdateStrategyStatsPatch.Postfix",
            "Parsek.Patches.AstronautComplexAddItemPatch.Postfix",
            "Parsek.Patches.AstronautComplexCrewCountsPatch.Postfix",
            "Parsek.Patches.AstronautComplexDismissPatch.Prefix",
            "Parsek.Patches.AstronautComplexHireRecruitPatch.Prefix",
            "Parsek.Patches.ContractAcceptPatch.Prefix",
            "Parsek.Patches.ContractCancelPatch.Prefix",
            "Parsek.Patches.ContractConfiguratorCanAcceptPatch.Postfix",
            "Parsek.Patches.ContractConfiguratorContractTitlePatch.Postfix",
            "Parsek.Patches.ContractConfiguratorListRebuildPatch.Postfix",
            "Parsek.Patches.ContractDeclinePatch.Prefix",
            "Parsek.Patches.CrewAutoAssignPatch.Prefix",
            "Parsek.Patches.CrewDialogAvailItemPatch.Postfix",
            "Parsek.Patches.CrewDialogAvailListPatch.Postfix",
            "Parsek.Patches.CrewDialogDropOnCrewListPatch.Prefix",
            "Parsek.Patches.CrewDialogFillPatch.Prefix",
            "Parsek.Patches.CrewDialogMoveToSeatPatch.Prefix",
            "Parsek.Patches.CrewTooltipReservationPatch.Postfix",
            "Parsek.Patches.FacilityMenuUpgradeBlockPatch.Postfix",
            "Parsek.Patches.FacilityRepairScopePatch.Prefix",
            "Parsek.Patches.FacilityResetStructuresPatch.Prefix",
            "Parsek.Patches.FacilityUpgradePatch.Prefix",
            "Parsek.Patches.FacilityUpgradeSpendPatch.Prefix",
            "Parsek.Patches.GhostTrackingStationInitPatch.Prefix",
            "Parsek.Patches.KerbalDismissalPatch.Prefix",
            "Parsek.Patches.KerbalHirePatch.Prefix",
            "Parsek.Patches.KerbalSackPatch.Prefix",
            "Parsek.Patches.KscVesselMarkerFlyPatch.Prefix",
            "Parsek.Patches.MapFocusObjectOnSelectPatch.Prefix",
            "Parsek.Patches.MissionControlAcceptPatch.Prefix",
            "Parsek.Patches.MissionControlAddItemLabelPatch.Prefix",
            "Parsek.Patches.MissionControlInfoPanelPatch.Postfix",
            "Parsek.Patches.MissionControlRebuildPassPatch.Postfix",
            "Parsek.Patches.MissionControlRebuildPassPatch.Prefix",
            "Parsek.Patches.MissionControlRefreshUIControlsPatch.Postfix",
            "Parsek.Patches.PartListTooltipPurchasePatch.Prefix",
            "Parsek.Patches.PartListTooltipReasonPatch.Postfix",
            "Parsek.Patches.PartListTooltipSetupPartPatch.Postfix",
            "Parsek.Patches.PartListTooltipSetupUpgradePatch.Postfix",
            "Parsek.Patches.PartMassivePartCheckSeederPatch.PrefixMassivePartCheck",
            "Parsek.Patches.PhysicsFramePatch.Postfix",
            "Parsek.Patches.ProgressRewardPatch.Postfix",
            "Parsek.Patches.RDTechPurchasePartPatch.Prefix",
            "Parsek.Patches.RnDNodeMarkPatch.Postfix",
            "Parsek.Patches.RnDNodeTooltipPatch.Postfix",
            "Parsek.Patches.RnDPanelBlockPatch.Postfix",
            "Parsek.Patches.RnDPanelDescriptionPatch.Postfix",
            "Parsek.Patches.RnDPurchaseAllPatch.Prefix",
            "Parsek.Patches.RnDRefreshPassLogPatch.Postfix",
            "Parsek.Patches.ScienceSubjectPatch.ApplyCommittedScience",
            "Parsek.Patches.StrategyActivatePatch.Postfix",
            "Parsek.Patches.StrategyCanBeActivatedPatch.Postfix",
            "Parsek.Patches.StrategyDeactivatePatch.Postfix",
            "Parsek.Patches.StrategySystemLoadStrategiesPatch.Postfix",
            "Parsek.Patches.StrategyUpdateExpiryPatch.Prefix",
            "Parsek.Patches.SunLateUpdateGuardPatch.Prefix",
            "Parsek.Patches.SwitchIntentTrackingStationFlyPatch.Prefix",
            "Parsek.Patches.TechResearchPatch.Prefix",
            "Parsek.Patches.TechResearchSpendPatch.Prefix",
        };

        public static IEnumerable<object[]> GatedEntryPoints()
        {
            foreach (var name in GatedEntryPointNames)
                yield return new object[] { name };
        }

        [Theory]
        [MemberData(nameof(GatedEntryPoints))]
        public void GatedEntryPoint_CallsTheGameModeGate(string qualifiedName)
        {
            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            int dot = qualifiedName.LastIndexOf('.');
            var type = typeof(ParsekGameModeGate).Assembly.GetType(qualifiedName.Substring(0, dot), throwOnError: true);
            var methods = type.GetMethods(Any).Where(m => m.Name == qualifiedName.Substring(dot + 1)).ToList();
            Assert.True(methods.Count > 0, qualifiedName + " not found");
            // Multi-overload names (RevertInterceptor.Prefix) carry the gate on the overload
            // that does the work; the others delegate to it.
            MethodInfo body = methods.OrderByDescending(m => m.GetParameters().Length).First();
            var gateMethods = new HashSet<MethodBase>(
                typeof(ParsekGameModeGate).GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                    .Where(m => m.Name == nameof(ParsekGameModeGate.CheckInert)));
            Assert.True(CallsAny(body, gateMethods), qualifiedName + " does not call ParsekGameModeGate.CheckInert");
        }

        [Fact]
        public void IlCheck_IsDiscriminating_AGhostPidFilterDoesNotCallTheGate()
        {
            var type = typeof(ParsekGameModeGate).Assembly.GetType("Parsek.Patches.GhostTrackingFlyPatch", throwOnError: true);
            var prefix = type.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var gateMethods = new HashSet<MethodBase>(
                typeof(ParsekGameModeGate).GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                    .Where(m => m.Name == nameof(ParsekGameModeGate.CheckInert)));
            Assert.False(CallsAny(prefix, gateMethods));
        }

        /// <summary>
        /// True when the method's IL has a call / callvirt whose token resolves to one of
        /// <paramref name="targets"/>. Walks raw bytes: a stray 0x28/0x6F inside an operand
        /// can only false-positive if its following 4 bytes happen to resolve to the gate.
        /// </summary>
        private static bool CallsAny(MethodInfo method, HashSet<MethodBase> targets)
        {
            var body = method.GetMethodBody();
            if (body == null) return false;
            byte[] il = body.GetILAsByteArray();
            Module module = method.Module;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F) continue;
                int token = BitConverter.ToInt32(il, i + 1);
                MethodBase resolved;
                try
                {
                    resolved = module.ResolveMethod(token,
                        method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null,
                        method.IsGenericMethod ? method.GetGenericArguments() : null);
                }
                catch (ArgumentException) { continue; }
                catch (BadImageFormatException) { continue; }
                if (resolved != null && targets.Contains(resolved))
                    return true;
            }
            return false;
        }
    }
}
