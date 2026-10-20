using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Design section 2.12 Rule 1 (Re-Fly ledger safety): the retire set and the value
    /// gate. One builder (<see cref="SupersedeCommit.BuildRetireSet"/>) decides the rows
    /// a merge tombstones; the gate (<see cref="SupersedeCommit.RetireSetHoldsSealingRow"/>)
    /// refuses a Re-Fly whose retire set holds any row that is not a loss row. Covers the
    /// builder / merge parity, the exhaustive loss-row table, the gate's scope edges,
    /// <see cref="RewindInvoker.CanInvoke(RewindPoint, ChildSlot, out string)"/> after a
    /// ledger change, the load-time value-seal sweep, and the auto-seal preview.
    /// </summary>
    [Collection("Sequential")]
    public class ReFlyValueGateTests : IDisposable
    {
        private const double RpUT = 100.0;

        private readonly List<string> logLines = new List<string>();
        private readonly List<string> deletedRpIds = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;
        private readonly GameScenes previousScene;
        private readonly string tempDir;

        public ReFlyValueGateTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            SupersedeCommit.ResetWorldActionSafetyCacheForTesting();
            MarkerValidator.ResetTestOverrides();
            MarkerValidator.NowUtProvider = () => 1_000_000.0;
            RewindInvokeContext.Clear();
            RewindInvoker.PreconditionCache.InvalidateForTesting();
            RewindInvoker.ResolveAbsoluteQuicksavePathOverrideForTesting = null;
            RewindInvoker.PartLoaderPrecondition.PartExistsOverrideForTesting = null;
            RewindInvoker.NowUtProviderForTesting = null;
            RewindPointReaper.ResetTestOverrides();
            RewindPointReaper.DeleteQuicksaveForTesting = rpId =>
            {
                deletedRpIds.Add(rpId);
                return true;
            };

            previousScene = HighLogic.LoadedScene;
            HighLogic.LoadedScene = GameScenes.FLIGHT;
            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek-value-gate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            HighLogic.LoadedScene = previousScene;
            RewindInvokeContext.Clear();
            RewindInvoker.PreconditionCache.InvalidateForTesting();
            RewindInvoker.ResolveAbsoluteQuicksavePathOverrideForTesting = null;
            RewindInvoker.PartLoaderPrecondition.PartExistsOverrideForTesting = null;
            RewindInvoker.NowUtProviderForTesting = null;
            RewindPointReaper.ResetTestOverrides();
            MarkerValidator.ResetTestOverrides();
            SupersedeCommit.ResetWorldActionSafetyCacheForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            KspStatePatcher.ResetForTesting();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); }
                catch { }
            }
        }

        // ---------- helpers ----------------------------------------------

        private static GameAction Funds(string id, string rec, double ut)
        {
            return new GameAction
            {
                ActionId = id, Type = GameActionType.FundsEarning, RecordingId = rec, UT = ut,
                FundsAwarded = 100f, FundsSource = FundsEarningSource.Other,
            };
        }

        private static GameAction Science(string id, string rec, double ut,
            ScienceMethod method = ScienceMethod.Transmitted)
        {
            return new GameAction
            {
                ActionId = id, Type = GameActionType.ScienceEarning, RecordingId = rec, UT = ut,
                SubjectId = "crewReport@MunInSpaceLow", ScienceAwarded = 1.5f, Method = method,
            };
        }

        private static GameAction Death(string id, string rec, double ut, string kerbal = "Bill Kerman")
        {
            return new GameAction
            {
                ActionId = id, Type = GameActionType.KerbalAssignment, RecordingId = rec,
                UT = (float)ut, StartUT = (float)ut, EndUT = (float)ut, KerbalName = kerbal,
                KerbalEndStateField = KerbalEndState.Dead,
            };
        }

        private static GameAction DeathPenalty(string id, string rec, double ut)
        {
            return new GameAction
            {
                ActionId = id, Type = GameActionType.ReputationPenalty, RecordingId = rec, UT = ut,
                NominalPenalty = 10f, RepPenaltySource = ReputationPenaltySource.KerbalDeath,
            };
        }

        private static Recording Rec(string id, MergeState state, string treeId = "tree_1")
        {
            return new Recording
            {
                RecordingId = id, VesselName = id, TreeId = treeId, MergeState = state,
                TerminalStateValue = TerminalState.Destroyed,
            };
        }

        private static void InstallTree(string treeId, params Recording[] recordings)
        {
            var tree = new RecordingTree { Id = treeId, TreeName = "Test_" + treeId };
            foreach (var rec in recordings)
            {
                rec.TreeId = treeId;
                tree.AddOrReplaceRecording(rec);
                RecordingStore.AddRecordingWithTreeForTesting(rec, treeId);
            }
            RecordingStore.CommittedTrees.Add(tree);
        }

        private static ChildSlot Slot(int index, string origin)
        {
            return new ChildSlot { SlotIndex = index, OriginChildRecordingId = origin, Controllable = true };
        }

        private static RewindPoint Rp(string id, double ut, params ChildSlot[] slots)
        {
            return new RewindPoint
            {
                RewindPointId = id,
                BranchPointId = "bp_" + id,
                UT = ut,
                QuicksaveFilename = "Parsek/RewindPoints/" + id + ".sfs",
                SessionProvisional = false,
                ChildSlots = new List<ChildSlot>(slots ?? Array.Empty<ChildSlot>()),
            };
        }

        private static ParsekScenario InstallScenario(ReFlySessionMarker marker, params RewindPoint[] rps)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                RecordingRewindRetirements = new List<RecordingRewindRetirement>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(rps),
                ActiveReFlySessionMarker = marker,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            EffectiveState.ResetCachesForTesting();
            return scenario;
        }

        /// <summary>One open slot (origin rec_a, CommittedProvisional) on rp_1 at UT 100.</summary>
        private ParsekScenario InstallOpenSlot(out RewindPoint rp, out ChildSlot slot, out Recording origin)
        {
            origin = Rec("rec_a", MergeState.CommittedProvisional);
            InstallTree("tree_1", origin, Rec("rec_other", MergeState.Immutable));
            slot = Slot(0, "rec_a");
            rp = Rp("rp_1", RpUT, slot);
            return InstallScenario(null, rp);
        }

        // ---------- builder / merge parity --------------------------------

        // catches: the gate and the merge drifting apart - a row the merge would
        // tombstone that the gate does not read, or the reverse.
        [Fact]
        public void BuildRetireSet_IsExactlyWhatCommitTombstonesWrites_InOrder()
        {
            var actions = new List<GameAction>
            {
                Funds("act_b_funds", "rec_b", 140.0),             // retired, first-seen rec_b
                Funds("act_a_funds", "rec_a", 120.0),             // retired
                Science("act_a_prerewind", "rec_a", 50.0),        // kept: earned before the RP
                Death("act_a_death", "rec_a", 130.0),             // retired (loss)
                DeathPenalty("act_a_deathrep", "rec_a", 130.0),   // retired (loss)
                Funds("act_a_tombstoned", "rec_a", 125.0),        // already tombstoned
                new GameAction
                {
                    ActionId = "act_a_build", Type = GameActionType.FundsSpending,
                    RecordingId = "rec_a", UT = 110.0, FundsSpent = 50f,
                    FundsSpendingSource = FundsSpendingSource.VesselBuild,
                },                                                // rollout: never retired
                Funds("act_out", "rec_outside", 150.0),           // outside the closure
                new GameAction
                {
                    ActionId = "act_deployed", Type = GameActionType.ScienceEarning,
                    RecordingId = "rec_b", UT = 160.0, ScienceAwarded = 2f,
                    SubjectId = DeployedSubjectId(),
                },                                                // ground-station credit
                Science("act_b_sci", "rec_b", 170.0),             // retired
            };
            Ledger.AddActions(actions);
            var preexisting = new LedgerTombstone
            {
                TombstoneId = "tomb_pre", ActionId = "act_a_tombstoned",
                RetiringRecordingId = "rec_old", UT = 90.0, CreatedRealTime = "x",
            };
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_1", TreeId = "tree_1", ActiveReFlyRecordingId = "rec_fork",
                OriginChildRecordingId = "rec_a", SupersedeTargetId = "rec_a",
                RewindPointUT = RpUT, InvokedUT = RpUT,
            };
            var scenario = InstallScenario(marker);
            scenario.LedgerTombstones.Add(preexisting);
            var closure = new List<string> { "rec_a", "rec_b" };

            SupersedeCommit.RetireSetScan scan = SupersedeCommit.BuildRetireSet(
                closure, SupersedeCommit.ComputeTombstoneRewindCutoffUT(marker), Ledger.Actions,
                new[] { "act_a_tombstoned" }, logDecisions: false);

            SupersedeCommit.CommitTombstones(marker, closure, "rec_fork", 200.0,
                "2026-10-20T00:00:00Z", scenario);

            var written = scenario.LedgerTombstones
                .Where(t => !ReferenceEquals(t, preexisting))
                .Select(t => t.ActionId)
                .ToList();
            Assert.Equal(scan.Rows.Select(a => a.ActionId).ToList(), written);
            Assert.Equal(
                new[] { "act_b_funds", "act_b_sci", "act_a_funds", "act_a_death", "act_a_deathrep" },
                written);
            Assert.Equal(1, scan.PreRewindKept);
            Assert.Equal(1, scan.AlreadyTombstoned);
            Assert.Equal(1, scan.RolloutExcluded);
            Assert.Equal(1, scan.DeployedScienceExcluded);
        }

        private static string DeployedSubjectId()
        {
            // A subject id the capture classifies as Breaking Ground deployed science.
            foreach (string candidate in new[]
                {
                    "deployedSeismicSensor@KerbinSrfLandedLaunchPad",
                    "deployedGooObservation@KerbinSrfLandedLaunchPad",
                    "deployedWeatherReport@KerbinSrfLandedLaunchPad",
                })
            {
                if (GameStateRecorder.IsDeployedScienceSubjectId(candidate))
                    return candidate;
            }
            throw new InvalidOperationException("no deployed-science subject id recognized");
        }

        // ---------- the loss-row table ------------------------------------

        // The reviewed classification of every (type, sub-source) pair. A new enum
        // member reds RetireSet_EveryActionShapeIsClassifiedAsLossOrSealing until it is
        // added here, which forces the 2.12 review (default: sealing).
        private static readonly Dictionary<string, bool> LossTable = BuildLossTable();

        private static Dictionary<string, bool> BuildLossTable()
        {
            var t = new Dictionary<string, bool>(StringComparer.Ordinal);
            t["FacilityDestruction"] = true;
            string[] sealingTypes =
            {
                "ScienceEarning", "ScienceSpending", "FundsEarning", "MilestoneAchievement",
                "ContractAccept", "ContractComplete", "ContractFail", "ContractCancel",
                "ReputationEarning", "KerbalHire", "KerbalRescue", "KerbalStandIn",
                "FacilityUpgrade", "FacilityRepair", "StrategyActivate", "StrategyDeactivate",
                "FundsInitial", "ScienceInitial", "ReputationInitial",
                "RouteDispatched", "RouteCargoDebited", "RouteCargoDelivered", "RoutePaused",
                "RouteEndpointLost", "RouteRecoveryCredited", "RouteCargoPickedUp", "RouteResumed",
                "KerbalExperience", "StrategyScienceDebit", "StrategyScienceCredit",
                "KerbalRecovered", "VesselRecovered", "RouteHeld", "ResourceScanUnlocked",
                "ScienceLabEarning",
            };
            foreach (string s in sealingTypes) t[s] = false;
            t["KerbalAssignment/Aboard"] = true;
            t["KerbalAssignment/Dead"] = true;
            t["KerbalAssignment/Recovered"] = false;
            t["KerbalAssignment/Unknown"] = true;
            t["ReputationPenalty/ContractFail"] = false;
            t["ReputationPenalty/ContractDecline"] = false;
            t["ReputationPenalty/KerbalDeath"] = true;
            t["ReputationPenalty/Strategy"] = false;
            t["ReputationPenalty/Other"] = true;
            t["ReputationPenalty/StrategyConverter"] = false;
            foreach (FundsSpendingSource src in Enum.GetValues(typeof(FundsSpendingSource)))
                t["FundsSpending/" + src] = false;
            return t;
        }

        private static IEnumerable<KeyValuePair<string, GameAction>> EveryActionShape()
        {
            foreach (GameActionType type in Enum.GetValues(typeof(GameActionType)))
            {
                if (type == GameActionType.KerbalAssignment)
                {
                    foreach (KerbalEndState s in Enum.GetValues(typeof(KerbalEndState)))
                        yield return new KeyValuePair<string, GameAction>(type + "/" + s,
                            new GameAction { Type = type, RecordingId = "r", KerbalEndStateField = s });
                }
                else if (type == GameActionType.ReputationPenalty)
                {
                    foreach (ReputationPenaltySource s in Enum.GetValues(typeof(ReputationPenaltySource)))
                        yield return new KeyValuePair<string, GameAction>(type + "/" + s,
                            new GameAction { Type = type, RecordingId = "r", RepPenaltySource = s });
                }
                else if (type == GameActionType.FundsSpending)
                {
                    foreach (FundsSpendingSource s in Enum.GetValues(typeof(FundsSpendingSource)))
                        yield return new KeyValuePair<string, GameAction>(type + "/" + s,
                            new GameAction { Type = type, RecordingId = "r", FundsSpendingSource = s });
                }
                else
                {
                    yield return new KeyValuePair<string, GameAction>(type.ToString(),
                        new GameAction { Type = type, RecordingId = "r" });
                }
            }
        }

        // catches: a new GameActionType / KerbalEndState / ReputationPenaltySource /
        // FundsSpendingSource member silently classified without review, or the loss
        // list drifting from design section 2.12 Terms.
        [Fact]
        public void RetireSet_EveryActionShapeIsClassifiedAsLossOrSealing()
        {
            var unclassified = new List<string>();
            var wrong = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in EveryActionShape())
            {
                seen.Add(kv.Key);
                bool expected;
                if (!LossTable.TryGetValue(kv.Key, out expected))
                {
                    unclassified.Add(kv.Key);
                    continue;
                }
                if (SupersedeCommit.IsLossRow(kv.Value) != expected)
                    wrong.Add(kv.Key + " expected loss=" + expected);
            }
            var stale = LossTable.Keys.Where(k => !seen.Contains(k)).ToList();
            Assert.True(unclassified.Count == 0 && wrong.Count == 0 && stale.Count == 0,
                "Loss-row table (design 2.12 Terms) out of step. Unclassified=["
                + string.Join(",", unclassified) + "] Wrong=[" + string.Join(",", wrong)
                + "] Stale=[" + string.Join(",", stale) + "]");
        }

        // catches: a loss row that can never enter a retire set (so the merge would never
        // retire the loss the replacement flight re-records).
        [Fact]
        public void EveryLossShape_IsSupersedeTombstoneEligible()
        {
            foreach (var kv in EveryActionShape())
            {
                if (!SupersedeCommit.IsLossRow(kv.Value)) continue;
                Assert.True(TombstoneEligibility.IsSupersedeTombstoneEligible(kv.Value), kv.Key);
            }
        }

        // ---------- the gate's scope ---------------------------------------

        [Fact]
        public void Gate_NoRows_IsOpen_AndLogsNothingToGiveBack()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);

            string summary;
            Assert.False(SupersedeCommit.RetireSetHoldsSealingRow(origin, rp, slot, out summary));
            Assert.Null(summary);
            Assert.Contains(logLines, l => l.Contains("[Supersede]")
                && l.Contains("RetireSet gate (section 2.12 Rule 1): rp=rp_1 slot=0")
                && l.Contains("tip=rec_a") && l.Contains("-> nothing to give back"));
        }

        [Fact]
        public void Gate_RowEarnedBeforeTheRewindPoint_DoesNotSeal()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            Ledger.AddAction(Funds("act_pre", "rec_a", 50.0));

            string summary;
            Assert.False(SupersedeCommit.RetireSetHoldsSealingRow(origin, rp, slot, out summary));
        }

        [Fact]
        public void Gate_AlreadyTombstonedRow_DoesNotSeal()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            var scenario = InstallOpenSlot(out rp, out slot, out origin);
            Ledger.AddAction(Funds("act_gone", "rec_a", 120.0));
            scenario.LedgerTombstones.Add(new LedgerTombstone
            {
                TombstoneId = "t1", ActionId = "act_gone", RetiringRecordingId = "rec_x", UT = 1.0,
            });

            string summary;
            Assert.False(SupersedeCommit.RetireSetHoldsSealingRow(origin, rp, slot, out summary));
        }

        [Fact]
        public void Gate_LossRowsOnly_DoesNotSeal_AndCountsThem()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            Ledger.AddAction(Death("act_death", "rec_a", 130.0));
            Ledger.AddAction(DeathPenalty("act_deathrep", "rec_a", 130.0));
            Ledger.AddAction(new GameAction
            {
                ActionId = "act_facility", Type = GameActionType.FacilityDestruction,
                RecordingId = "rec_a", UT = 131.0, FacilityId = "SpaceCenter/LaunchPad",
            });

            string summary;
            Assert.False(SupersedeCommit.RetireSetHoldsSealingRow(origin, rp, slot, out summary));
            Assert.Contains(logLines, l => l.Contains("RetireSet gate")
                && l.Contains("retireSet=3") && l.Contains("lossRows=3") && l.Contains("sealingRows=0"));
        }

        [Fact]
        public void Gate_PostRewindGain_SealsAndNamesTheRow()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            Ledger.AddAction(Death("act_death", "rec_a", 130.0));
            Ledger.AddAction(Funds("act_gain", "rec_a", 120.0));

            string summary;
            Assert.True(SupersedeCommit.RetireSetHoldsSealingRow(origin, rp, slot, out summary));
            Assert.Equal("FundsEarning:act_gain", summary);
            Assert.Contains(logLines, l => l.Contains("RetireSet gate")
                && l.Contains("-> sealed by FundsEarning:act_gain"));
        }

        [Fact]
        public void Gate_RowOnAChainSiblingOfTheTip_Seals()
        {
            var origin = Rec("rec_a", MergeState.CommittedProvisional);
            origin.ChainId = "chain_1"; origin.ChainIndex = 0;
            var sibling = Rec("rec_a2", MergeState.CommittedProvisional);
            sibling.ChainId = "chain_1"; sibling.ChainIndex = 1;
            InstallTree("tree_1", origin, sibling);
            var slot = Slot(0, "rec_a");
            var rp = Rp("rp_1", RpUT, slot);
            InstallScenario(null, rp);
            Ledger.AddAction(Science("act_sib", "rec_a2", 150.0));

            string summary;
            Assert.True(SupersedeCommit.RetireSetHoldsSealingRow(origin, rp, slot, out summary));
            Assert.Equal("ScienceEarning:act_sib", summary);
        }

        [Fact]
        public void Gate_RowOnAnUnrelatedRecording_DoesNotSeal()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            Ledger.AddAction(Funds("act_other", "rec_other", 150.0));

            string summary;
            Assert.False(SupersedeCommit.RetireSetHoldsSealingRow(origin, rp, slot, out summary));
        }

        // catches: a stale cached verdict after a row is filed - the gate must see
        // a ledger change the moment it happens.
        [Fact]
        public void Gate_LedgerChangeAfterTheFirstRead_IsSeenAtOnce()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            var scenario = InstallOpenSlot(out rp, out slot, out origin);
            string summary;
            Assert.False(SupersedeCommit.RetireSetHoldsSealingRow(origin, rp, slot, out summary));

            Ledger.AddAction(Funds("act_late", "rec_a", 120.0));
            Assert.True(SupersedeCommit.RetireSetHoldsSealingRow(origin, rp, slot, out summary));
            Assert.Equal("FundsEarning:act_late", summary);

            scenario.LedgerTombstones.Add(new LedgerTombstone
            {
                TombstoneId = "t_late", ActionId = "act_late", RetiringRecordingId = "rec_x", UT = 1.0,
            });
            scenario.BumpTombstoneStateVersion();
            Assert.False(SupersedeCommit.RetireSetHoldsSealingRow(origin, rp, slot, out summary));
        }

        // ---------- RewindInvoker.CanInvoke precondition ------------------

        private void InstallInvokableQuicksave()
        {
            string path = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".sfs");
            File.WriteAllText(path, "GAME\n{\n  FLIGHTSTATE\n  {\n  }\n}\n");
            RewindInvoker.ResolveAbsoluteQuicksavePathOverrideForTesting = _ => path;
            RewindInvoker.PartLoaderPrecondition.PartExistsOverrideForTesting = _ => true;
            RewindInvoker.NowUtProviderForTesting = () => 500.0;
        }

        // catches: a Re-Fly started from a row drawn before a ledger change (design
        // 2.12 Rule 1: the gate is read again at invoke, not only at row draw).
        [Fact]
        public void CanInvoke_RefusesTheSlotOnceASealingRowIsFiled()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            InstallInvokableQuicksave();

            string reason;
            Assert.True(RewindInvoker.CanInvoke(rp, slot, out reason), reason);

            Ledger.AddAction(Funds("act_after_draw", "rec_a", 120.0));

            Assert.False(RewindInvoker.CanInvoke(rp, slot, out reason));
            Assert.Equal("recordingAction:FundsEarning:act_after_draw", reason);
            // The rewind-point-only overload names no slot and skips the check.
            Assert.True(RewindInvoker.CanInvoke(rp, out reason), reason);
        }

        [Fact]
        public void CanInvoke_LossRowsOnly_StaysInvokable()
        {
            RewindPoint rp; ChildSlot slot; Recording origin;
            InstallOpenSlot(out rp, out slot, out origin);
            InstallInvokableQuicksave();
            Ledger.AddAction(Death("act_death", "rec_a", 130.0));
            Ledger.AddAction(DeathPenalty("act_deathrep", "rec_a", 130.0));

            string reason;
            Assert.True(RewindInvoker.CanInvoke(rp, slot, out reason), reason);
        }

        // ---------- load-time value-seal sweep -----------------------------

        [Fact]
        public void Sweep_ConcludesAValueHoldingSlot_LeavesALossOnlySlot_AndIsIdempotent()
        {
            var sealedOrigin = Rec("rec_a", MergeState.CommittedProvisional);
            var lossOrigin = Rec("rec_b", MergeState.CommittedProvisional);
            InstallTree("tree_1", sealedOrigin, lossOrigin);
            var rp = Rp("rp_1", RpUT, Slot(0, "rec_a"), Slot(1, "rec_b"));
            var scenario = InstallScenario(null, rp);
            Ledger.AddAction(Funds("act_gain", "rec_a", 120.0));
            Ledger.AddAction(Death("act_death", "rec_b", 130.0));

            var first = LoadTimeSweep.SweepValueSealedSlots(scenario);

            Assert.Equal(1, first.Found);
            Assert.Equal(1, first.Sealed);
            Assert.Equal(0, first.KeptLiveSession);
            Assert.Equal(MergeState.Immutable, sealedOrigin.MergeState);
            Assert.True(sealedOrigin.FilesDirty);
            Assert.Equal(MergeState.CommittedProvisional, lossOrigin.MergeState);
            Assert.Contains(logLines, l => l.Contains("[LoadSweep]")
                && l.Contains("Value-sealed slot concluded rp=rp_1 slot=0 tip=rec_a")
                && l.Contains("row=recordingAction:FundsEarning:act_gain"));
            Assert.Contains(logLines, l => l.Contains("[INFO][LoadSweep]")
                && l.Contains("[LoadSweep] Value-sealed slots (section 2.12): found=1 sealed=1"));

            logLines.Clear();
            var second = LoadTimeSweep.SweepValueSealedSlots(scenario);
            Assert.Equal(0, second.Found);
            Assert.Equal(0, second.Sealed);
            Assert.Contains(logLines, l =>
                l.Contains("[LoadSweep] Value-sealed slots (section 2.12): found=0 sealed=0"));
        }

        [Fact]
        public void Sweep_LiveSessionRewindPoint_IsCountedButLeftOpen()
        {
            var origin = Rec("rec_a", MergeState.CommittedProvisional);
            var fork = Rec("rec_fork", MergeState.NotCommitted);
            InstallTree("tree_1", origin, fork);
            var rp = Rp("rp_1", RpUT, Slot(0, "rec_a"));
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_1", TreeId = "tree_1", ActiveReFlyRecordingId = "rec_fork",
                OriginChildRecordingId = "rec_a", SupersedeTargetId = "rec_a",
                RewindPointId = "rp_1", RewindPointUT = RpUT, InvokedUT = RpUT,
            };
            var scenario = InstallScenario(marker, rp);
            Ledger.AddAction(Funds("act_gain", "rec_a", 120.0));

            var result = LoadTimeSweep.SweepValueSealedSlots(scenario);

            Assert.Equal(1, result.Found);
            Assert.Equal(0, result.Sealed);
            Assert.Equal(1, result.KeptLiveSession);
            Assert.Equal(MergeState.CommittedProvisional, origin.MergeState);
            Assert.Empty(deletedRpIds);
            Assert.Contains(logLines, l => l.Contains("[LoadSweep]")
                && l.Contains("Value-sealed slot kept open for the live Re-Fly session rp=rp_1"));
        }

        [Fact]
        public void Sweep_NoRewindPoints_StillLogsTheSummary()
        {
            var scenario = InstallScenario(null);
            var result = LoadTimeSweep.SweepValueSealedSlots(scenario);
            Assert.Equal(0, result.Found);
            Assert.Contains(logLines, l =>
                l.Contains("[LoadSweep] Value-sealed slots (section 2.12): found=0 sealed=0 keptLiveSession=0 rpsReaped=0"));
        }

        // ---------- the auto-seal preview reads the same predicate ----------

        [Fact]
        public void Preview_AttemptRowsNotYetFiled_AreReadByTheSamePredicate()
        {
            var origin = Rec("rec_a", MergeState.Immutable);
            var provisional = Rec("rec_fork", MergeState.NotCommitted);
            provisional.CreatingSessionId = "sess_1";
            InstallTree("tree_1", origin, provisional);
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_1", TreeId = "tree_1", ActiveReFlyRecordingId = "rec_fork",
                OriginChildRecordingId = "rec_a", SupersedeTargetId = "rec_a",
                RewindPointId = "rp_1", RewindPointUT = RpUT, InvokedUT = RpUT,
            };
            InstallScenario(marker, Rp("rp_1", RpUT, Slot(0, "rec_a")));

            var lossOnly = ReFlyAutoSealPreviewer.Preview(provisional, marker, null,
                new List<GameAction> { Death("act_d", "rec_fork", 130.0) });
            Assert.DoesNotContain(ReFlyAutoSealReason.ChangedCareer, lossOnly.Reasons);
            Assert.DoesNotContain(ReFlyAutoSealReason.TransmittedScience, lossOnly.Reasons);

            var gain = ReFlyAutoSealPreviewer.Preview(provisional, marker, null,
                new List<GameAction>
                {
                    Funds("act_f", "rec_fork", 120.0),
                    Science("act_s", "rec_fork", 121.0, ScienceMethod.Recovered),
                });
            Assert.True(gain.WillAutoSeal);
            Assert.Contains(ReFlyAutoSealReason.ChangedCareer, gain.Reasons);
            Assert.Contains(ReFlyAutoSealReason.RecoveredScience, gain.Reasons);

            var sealing = SupersedeCommit.CollectReFlyAttemptSealingRows(marker, provisional,
                new List<GameAction> { Funds("act_f", "rec_fork", 120.0) });
            Assert.Equal(new[] { "act_f" }, sealing.Select(a => a.ActionId));
        }
    }
}
