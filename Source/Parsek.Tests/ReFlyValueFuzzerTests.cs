using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Seeded property suite for the Re-Fly ledger-safety rules (rewind design section 2.12,
    /// owner rulings 2026-10-19 / 2026-10-26 / 2026-10-27; todo HARNESS-TIMELINE-FUZZERS item 2,
    /// test-gap audit 2026-10-31 C2). Every iteration builds a random career one timeline
    /// operation at a time, the way a player builds one:
    /// <list type="bullet">
    ///   <item>fly a flight forward, or after a Rewind-to-Launch to an earlier UT (a booster
    ///         slot: pre-separation rows, then a crash with losses only, or gains);</item>
    ///   <item>accept a contract, buy a tech node, upgrade a facility, hire a kerbal at the
    ///         Space Center (only when the purchase is affordable, as the greyed click is);</item>
    ///   <item>Re-Fly a slot from its separation: the value gate (Rule 1), a random attempt
    ///         (losses, gains, milestones, contract outcomes, some rows filed during the session,
    ///         the rest by the merge's build step), then Discard, or Merge through the dominance
    ///         check (Rule 5) and the real tombstone write (<c>SupersedeCommit.CommitTombstones</c>,
    ///         Variant A or B); a refused merge is discarded.</item>
    /// </list>
    /// The production pieces run unmodified: <c>SupersedeCommit.BuildRetireSet</c> /
    /// <c>SelectSealingRows</c> for the gate, <c>ReFlyMergeDominance.Evaluate</c> for the merge,
    /// <c>CommitTombstones</c> + <c>EffectiveState.ComputeELS</c> for what the merge leaves, and
    /// <c>Ledger.RemoveActionsForRecordings</c> for Discard. The oracles are written here, from
    /// the design text, not from the code they check:
    /// <list type="number">
    ///   <item>SEAL: an open slot's retire set holds no row the replay counts that is not on the
    ///         design's loss list (any counted gain or milestone seals), and retiring it alone
    ///         never lowers funds, science or reputation at any UT;</item>
    ///   <item>MERGE: a merge the check passes leaves funds, science and reputation at every UT
    ///         at least what they were, keeps every affordable purchase affordable, retires only
    ///         loss rows or rows the replay did not count, and keeps every credited milestone and
    ///         contract completion credited at the same or an earlier UT;</item>
    ///   <item>DISCARD: never refused, and leaves the Effective Ledger Set and every balance
    ///         exactly as they were before the invocation (so does the live session);</item>
    ///   <item>EARLIEST WINS: in every ledger the walk credits a milestone to its earliest row
    ///         in walk order, and a contract to its earliest outcome;</item>
    ///   <item>FORWARD PLAY: committing a flight (also after a rewind) never takes back a
    ///         credited milestone or contract completion, and changes no balance before the
    ///         flight's first row.</item>
    /// </list>
    /// A failure prints the seed, the iteration, the operation and the ledger for exact replay.
    /// <c>PARSEK_FUZZ_ITERATIONS</c> raises the iteration count for a deeper local sweep.
    /// </summary>
    [Collection("Sequential")]
    public class ReFlyValueFuzzerTests : IDisposable
    {
        private const int BaseSeed = 0x5EF1A7;
        private const int DefaultIterations = 120;
        private const int OpsPerIteration = 16;
        private const string IterationEnvVar = "PARSEK_FUZZ_ITERATIONS";

        /// <summary>Rounding allowance for float rewards and the reputation curve.</summary>
        private const double Eps = 0.05;

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private static readonly string[] BaseKerbals =
            { "Jebediah Kerman", "Bill Kerman", "Bob Kerman", "Valentina Kerman" };
        private static readonly string[] MilestoneIds =
            { "FirstLaunch", "Orbit", "Mun/Flyby", "Mun/Orbit", "Mun/Landing", "Minmus/Flyby" };
        private static readonly string[] SubjectIds =
            { "crewReport@KerbinSrfLaunchpad", "temperatureScan@KerbinFlyingLow",
              "barometerScan@KerbinInSpaceLow", "crewReport@MunInSpaceHigh", "evaReport@MunSrfMidlands" };
        private static readonly string[] Facilities =
            { "SpaceCenter/LaunchPad", "SpaceCenter/VehicleAssemblyBuilding", "SpaceCenter/TrackingStation" };

        private readonly Stats stats = new Stats();
        private readonly Xunit.Abstractions.ITestOutputHelper output;

        public ReFlyValueFuzzerTests(Xunit.Abstractions.ITestOutputHelper output)
        {
            this.output = output;
            ResetAll();
        }

        public void Dispose()
        {
            ResetAll();
            KerbalsModule.CrewRespawnPolicyProviderForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = false;
        }

        private static void ResetAll()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            CaptureSequence.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            SessionSuppressionState.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            InFlightRecoveryRequest.ResetForTesting();
            ReFlyMergeDominance.ResetForTesting();
            SupersedeCommit.ResetWorldActionSafetyCacheForTesting();
            KerbalsModule.CrewRespawnPolicyProviderForTesting = PermanentDeath;
        }

        private static bool PermanentDeath(out bool respawns, out double timerSeconds)
        {
            respawns = false;
            timerSeconds = 0.0;
            return true;
        }

        // =====================================================================
        // The property run
        // =====================================================================

        [Fact]
        public void RandomCareers_ReFlyNeverLosesValue()
        {
            int iterations = ResolveIterations();
            for (int it = 0; it < iterations; it++)
                RunIteration(it);

            // The run must actually reach every branch, or a green run proves nothing.
            Assert.True(stats.Sealed > 0, "no Re-Fly was ever sealed. " + stats);
            Assert.True(stats.Open > 0, "no Re-Fly slot was ever open. " + stats);
            Assert.True(stats.Merged > 0, "no Re-Fly merge ever passed. " + stats);
            Assert.True(stats.Refused > 0, "no Re-Fly merge was ever refused. " + stats);
            Assert.True(stats.Discarded > 0, "no Re-Fly was ever discarded. " + stats);
            Assert.True(stats.VariantB > 0, "the loss swap never took Variant B. " + stats);
            Assert.True(stats.ChainedReFlies > 0, "no slot was re-flown twice. " + stats);
            Assert.True(stats.EarlierCredits > 0,
                "no merge or rewound flight ever moved a credit earlier. " + stats);
            Assert.True(stats.RewoundFlights > 0, "no flight was flown after a rewind. " + stats);
            output.WriteLine("Re-Fly value fuzzer: " + iterations.ToString(IC) + " careers " + stats);
        }

        /// <summary>The generator only produces rows the design names; pin that the loss
        /// oracle here is the design's list and not the code's.</summary>
        [Fact]
        public void LossOracle_IsTheDesignList()
        {
            foreach (GameActionType type in Enum.GetValues(typeof(GameActionType)))
            {
                var a = new GameAction { Type = type };
                foreach (KerbalEndState es in Enum.GetValues(typeof(KerbalEndState)))
                {
                    a.KerbalEndStateField = es;
                    foreach (ReputationPenaltySource src in Enum.GetValues(typeof(ReputationPenaltySource)))
                    {
                        a.RepPenaltySource = src;
                        Assert.Equal(DesignLossRow(a), SupersedeCommit.IsLossRow(a));
                    }
                }
            }
        }

        private void RunIteration(int iteration)
        {
            ResetAll();
            LedgerOrchestrator.Initialize();
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);

            var c = new Career(new Random(BaseSeed + iteration), iteration, scenario);
            c.Seed();
            for (int op = 0; op < OpsPerIteration; op++)
            {
                c.Op = op;
                int roll = c.Rng.Next(100);
                if (c.Slots.Count == 0 || roll < 24)
                    FlyFlight(c, rewound: false);
                else if (roll < 34)
                    FlyFlight(c, rewound: true);
                else if (roll < 40)
                    RewindToEarlierUT(c);
                else if (roll < 49)
                    AcceptContract(c);
                else if (roll < 61)
                    SpaceCenterPurchase(c);
                else
                    ReFly(c);
                var opWalk = Walk(Els(), c.DeadlineClock());
                AssertEarliestWins(c, opWalk, "after op");
                AssertWalkMatchesRecalculation(c, opWalk, "after op");
            }
        }

        // =====================================================================
        // Career model
        // =====================================================================

        private sealed class Slot
        {
            internal string OriginId;
            internal string TipId;
            internal double RewindUT;
            internal double EndUT;
            internal List<string> Crew = new List<string>();
            internal int ReFlown;
        }

        private sealed class ContractSpec
        {
            internal string Id;
            internal double AcceptUT;
            internal double DeadlineUT = double.NaN;
            internal float FundsReward;
            internal float RepReward;
            internal float ScienceReward;
            internal float FundsPenalty;
            internal float RepPenalty;
            internal float[] ParamFunds;
        }

        private sealed class MilestoneSpec
        {
            internal float Funds;
            internal float Rep;
            internal float Science;
        }

        private sealed class Career
        {
            internal readonly Random Rng;
            internal readonly int Iteration;
            internal readonly ParsekScenario Scenario;
            internal int Op;
            internal double Clock;
            internal int NextId;
            internal readonly List<Slot> Slots = new List<Slot>();
            internal readonly List<ContractSpec> Contracts = new List<ContractSpec>();
            internal readonly List<string> Kerbals = new List<string>(BaseKerbals);
            internal readonly Dictionary<string, MilestoneSpec> Milestones = new Dictionary<string, MilestoneSpec>();
            internal readonly Dictionary<string, float> SubjectCap = new Dictionary<string, float>();
            internal readonly float[] RecordFunds = new float[8];
            internal readonly StringBuilder History = new StringBuilder();

            internal Career(Random rng, int iteration, ParsekScenario scenario)
            {
                Rng = rng;
                Iteration = iteration;
                Scenario = scenario;
                foreach (string m in MilestoneIds)
                {
                    bool zero = rng.Next(5) == 0;
                    Milestones[m] = new MilestoneSpec
                    {
                        Funds = zero ? 0f : Amount(rng, 500f, 6000f),
                        Rep = zero ? 0f : Amount(rng, 1f, 8f),
                        Science = zero ? 0f : Amount(rng, 0f, 3f),
                    };
                }
                foreach (string s in SubjectIds)
                    SubjectCap[s] = Amount(rng, 8f, 40f);
                for (int i = 0; i < RecordFunds.Length; i++)
                    RecordFunds[i] = Amount(rng, 100f, 1500f);
            }

            internal string Id(string prefix)
            {
                NextId++;
                return prefix + "_" + Iteration.ToString(IC) + "_" + NextId.ToString(IC);
            }

            internal double DeadlineClock()
            {
                double latest = Clock;
                var actions = Ledger.Actions;
                for (int i = 0; i < actions.Count; i++)
                    if (actions[i] != null && actions[i].UT > latest) latest = actions[i].UT;
                return latest;
            }

            internal void Log(string line)
            {
                History.Append("  op ").Append(Op.ToString(IC)).Append(": ").AppendLine(line);
            }

            internal void Seed()
            {
                Ledger.AddAction(new GameAction
                {
                    ActionId = Id("seed_funds"), Type = GameActionType.FundsInitial, UT = 0.0,
                    InitialFunds = Amount(Rng, 20000f, 60000f),
                });
                Ledger.AddAction(new GameAction
                {
                    ActionId = Id("seed_sci"), Type = GameActionType.ScienceInitial, UT = 0.0,
                    InitialScience = Amount(Rng, 0f, 40f),
                });
                Ledger.AddAction(new GameAction
                {
                    ActionId = Id("seed_rep"), Type = GameActionType.ReputationInitial, UT = 0.0,
                    InitialReputation = Rng.Next(3) == 0 ? Amount(Rng, 100f, 700f) : Amount(Rng, 0f, 30f),
                });
                Clock = 100.0;
                Log("seed");
            }
        }

        // =====================================================================
        // Operations
        // =====================================================================

        private void FlyFlight(Career c, bool rewound)
        {
            if (rewound)
            {
                if (!RewindToEarlierUT(c)) return;
                stats.RewoundFlights++;
            }
            double launch = c.Clock + Amount(c.Rng, 20f, 400f);
            double sep = launch + Amount(c.Rng, 20f, 200f);
            double end = sep + Amount(c.Rng, 20f, 400f);
            string rec = c.Id("rec");
            var crew = PickFreeCrew(c, launch, c.Rng.Next(0, 3));
            bool crash = c.Rng.Next(100) < 50;

            var rows = new List<GameAction>();
            int pre = c.Rng.Next(0, 3);
            for (int i = 0; i < pre; i++)
            {
                var g = RandomGain(c, rows, rec, Between(c.Rng, launch, sep), allowContractFail: !rewound, isAttempt: false);
                if (g != null) rows.Add(g);
            }
            if (crash)
            {
                double crashUT = Between(c.Rng, sep, end);
                AddCrashLosses(c, rows, rec, crew, launch, crashUT);
                // A crashed booster can also repeat a credit the timeline already holds
                // (a world-first it reached again): the replay leaves it out.
                if (c.Rng.Next(100) < 25)
                    rows.Add(Milestone(c, rec, Between(c.Rng, sep, crashUT), PickMilestone(c)));
                end = crashUT;
            }
            else
            {
                int post = c.Rng.Next(1, 4);
                for (int i = 0; i < post; i++)
                {
                    var g = RandomGain(c, rows, rec, Between(c.Rng, sep, end), allowContractFail: !rewound, isAttempt: false);
                    if (g != null) rows.Add(g);
                }
                foreach (string k in crew)
                    rows.Add(CrewRow(c, rec, k, launch, end, KerbalEndState.Recovered));
            }

            var before = SnapshotCredits(c);
            var realBefore = RealTotals(Els(), c.DeadlineClock());
            double firstRowUT = rows.Count > 0 ? rows.Min(r => r.UT) : double.PositiveInfinity;
            var walkBefore = Walk(Els(), Math.Max(c.DeadlineClock(), end));

            AddRecording(rec, launch, end, crash);
            // Captured as the flight happens: capture order is UT order.
            foreach (var r in rows.OrderBy(x => x.UT)) Ledger.AddAction(r);
            c.Clock = end;
            c.Slots.Add(new Slot { OriginId = rec, TipId = rec, RewindUT = sep, EndUT = end, Crew = crew });
            c.Log($"fly{(rewound ? " after rewind" : "")} rec={rec} ahead={R(c.DeadlineClock() - end)} launch={R(launch)} sep={R(sep)} end={R(end)} " +
                  $"crash={crash} crew=[{string.Join(",", crew)}] rows={rows.Count}");

            // FORWARD PLAY: a committed flight takes no credit back and moves nothing
            // before its first row.
            var walkAfter = Walk(Els(), c.DeadlineClock());
            AssertCreditsKept(c, before, SnapshotCredits(c), "forward flight " + rec);
            AssertRealCreditsKept(c, realBefore, RealTotals(Els(), c.DeadlineClock()), "forward flight " + rec);
            AssertBalancesEqualBefore(c, walkBefore, walkAfter, firstRowUT, "forward flight " + rec);
        }

        /// <summary>Rewind-to-Launch: only the player's clock moves (owner ruling 2026-10-24),
        /// to a UT with committed rows ahead of it.</summary>
        private bool RewindToEarlierUT(Career c)
        {
            if (c.Clock <= 250.0) return false;
            double to = Math.Round(100.0 + c.Rng.NextDouble() * (c.Clock - 200.0), 1);
            c.Log($"rewind clock {R(c.Clock)} -> {R(to)}");
            c.Clock = to;
            return true;
        }

        private static string DestroyedFacility(double ut)
        {
            var destroyed = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var a in Els().Where(x => x.UT <= ut).OrderBy(x => x.UT))
            {
                if (a.Type == GameActionType.FacilityDestruction) destroyed[a.FacilityId] = true;
                else if (a.Type == GameActionType.FacilityRepair) destroyed[a.FacilityId] = false;
            }
            foreach (var kv in destroyed)
                if (kv.Value) return kv.Key;
            return null;
        }

        private void AcceptContract(Career c)
        {
            var spec = new ContractSpec
            {
                Id = c.Id("contract"),
                AcceptUT = c.Clock,
                FundsReward = Amount(c.Rng, 2000f, 20000f),
                RepReward = Amount(c.Rng, 1f, 10f),
                ScienceReward = c.Rng.Next(3) == 0 ? Amount(c.Rng, 1f, 10f) : 0f,
                FundsPenalty = Amount(c.Rng, 0f, 4000f),
                RepPenalty = Amount(c.Rng, 0f, 6f),
                ParamFunds = new[] { Amount(c.Rng, 200f, 3000f), Amount(c.Rng, 200f, 3000f) },
            };
            if (c.Rng.Next(3) == 0)
                spec.DeadlineUT = Math.Round(c.Clock + Amount(c.Rng, 200f, 3000f), 1);
            c.Contracts.Add(spec);
            Ledger.AddAction(new GameAction
            {
                ActionId = c.Id("accept"), Type = GameActionType.ContractAccept, UT = c.Clock,
                ContractId = spec.Id, AdvanceFunds = Amount(c.Rng, 0f, 2000f),
                FundsPenalty = spec.FundsPenalty, RepPenalty = spec.RepPenalty,
                DeadlineUT = spec.DeadlineUT,
            });
            c.Clock += 1.0;
            c.Log($"accept contract={spec.Id} ut={R(spec.AcceptUT)} deadline={R(spec.DeadlineUT)}");
        }

        private void SpaceCenterPurchase(Career c)
        {
            double ut = c.Clock;
            var walk = Walk(Els(), c.DeadlineClock());
            double funds = MinFrom(walk, ut, s => s.Funds);
            double science = MinFrom(walk, ut, s => s.Science);
            GameAction row;
            int kind = c.Rng.Next(6);
            string destroyed = DestroyedFacility(ut);
            if (kind == 5 && destroyed != null)
            {
                float cost = Amount(c.Rng, 500f, 8000f);
                if (cost > funds) return;
                row = new GameAction
                {
                    ActionId = c.Id("repair"), Type = GameActionType.FacilityRepair, UT = ut,
                    FacilityId = destroyed, FacilityCost = cost,
                };
            }
            else if (kind == 4)
            {
                float cost = Amount(c.Rng, 1000f, 10000f);
                if (cost > funds) return;
                row = new GameAction
                {
                    ActionId = c.Id("strat"), Type = GameActionType.StrategyActivate, UT = ut,
                    StrategyId = "strategy-" + c.Rng.Next(3).ToString(IC),
                    SourceResource = StrategyResource.Funds, TargetResource = StrategyResource.Science,
                    Commitment = Amount(c.Rng, 0.05f, 0.5f), SetupCost = cost,
                };
            }
            else if (kind == 0 || kind == 5)
            {
                float cost = Amount(c.Rng, 5f, 60f);
                if (cost > science) return;
                row = new GameAction
                {
                    ActionId = c.Id("tech"), Type = GameActionType.ScienceSpending, UT = ut,
                    NodeId = c.Id("node"), Cost = cost,
                };
            }
            else if (kind == 1)
            {
                float cost = Amount(c.Rng, 2000f, 30000f);
                if (cost > funds) return;
                row = new GameAction
                {
                    ActionId = c.Id("upgrade"), Type = GameActionType.FacilityUpgrade, UT = ut,
                    FacilityId = Facilities[c.Rng.Next(Facilities.Length)], ToLevel = 1 + c.Rng.Next(2),
                    FacilityCost = cost,
                };
            }
            else if (kind == 2)
            {
                float cost = Amount(c.Rng, 1000f, 20000f);
                if (cost > funds) return;
                row = new GameAction
                {
                    ActionId = c.Id("spend"), Type = GameActionType.FundsSpending, UT = ut,
                    FundsSpent = cost, FundsSpendingSource = FundsSpendingSource.Other,
                };
            }
            else
            {
                float cost = Amount(c.Rng, 1000f, 15000f);
                if (cost > funds || c.Kerbals.Count > 8) return;
                string name = "Hire" + c.NextId.ToString(IC) + " Kerman";
                c.Kerbals.Add(name);
                row = new GameAction
                {
                    ActionId = c.Id("hire"), Type = GameActionType.KerbalHire, UT = ut,
                    KerbalName = name, KerbalRole = "Pilot", HireCost = cost,
                };
            }
            Ledger.AddAction(row);
            c.Clock += 1.0;
            c.Log($"purchase {row.Type} id={row.ActionId} ut={R(ut)}");
        }

        private void ReFly(Career c)
        {
            var slot = c.Slots[c.Rng.Next(c.Slots.Count)];
            double r = slot.RewindUT;
            var closure = new HashSet<string>(StringComparer.Ordinal) { slot.TipId };
            string where = $"Re-Fly slot={slot.OriginId} tip={slot.TipId} rewindUT={R(r)}";

            List<GameAction> l = Els().ToList();
            double clock = c.DeadlineClock();
            var walkL = Walk(l, clock);
            var gateWalk = RecalculationEngine.WalkBalancesOnClones(l);
            var retire = SupersedeCommit.BuildRetireSet(
                closure, r, Ledger.Actions, TombstonedIds(c), logDecisions: false).Rows;
            var sealing = SupersedeCommit.SelectSealingRows(retire, gateWalk.NotCountedByActionId, null);

            // The live gate (closure, cached replay walk) must say the same.
            var tipRec = EffectiveState.FindCommittedRecordingByIdRaw(slot.TipId);
            var liveSealing = SupersedeCommit.CollectRetireSetSealingRows(
                tipRec, slot.OriginId, null, r, null, out int liveRetireCount, out _);
            if (liveRetireCount != retire.Count
                || !liveSealing.Select(x => x.ActionId).SequenceEqual(sealing.Select(x => x.ActionId)))
                Fail(c, where, $"the live gate read retireSet={liveRetireCount} sealing=[" +
                    string.Join(",", liveSealing.Select(x => x.ActionId)) + $"], the pure parts retireSet={retire.Count} " +
                    "sealing=[" + string.Join(",", sealing.Select(x => x.ActionId)) + "]");

            // SEAL oracle: any counted row off the design's loss list seals the slot.
            foreach (var row in retire)
            {
                bool counted;
                gateWalk.EffectiveByActionId.TryGetValue(row.ActionId, out counted);
                if (!DesignLossRow(row) && counted && !sealing.Contains(row))
                    Fail(c, where, $"open slot retires a counted non-loss row {Describe(row)}");
                if (DesignLossRow(row) && sealing.Contains(row))
                    Fail(c, where, $"a loss row seals the slot {Describe(row)}");
            }
            if (sealing.Count > 0)
            {
                stats.Sealed++;
                stats.Bump("seal:" + sealing[0].Type);
                c.Log(where + " -> sealed by " + Describe(sealing[0]));
                return;
            }
            stats.Open++;

            // SEAL oracle: an open slot gives back only losses.
            var retireIds = new HashSet<string>(retire.Select(x => x.ActionId), StringComparer.Ordinal);
            var walkRetired = Walk(l.Where(x => !retireIds.Contains(x.ActionId)).ToList(), clock);
            AssertDominates(c, walkL, walkRetired, where + " retire-set alone");

            // The session: the attempt flies from the separation.
            string attempt = c.Id(slot.OriginId + "_rf");
            var attemptRows = BuildAttempt(c, attempt, slot, r, out double attemptEnd);
            // The session captures every row as it happens, filed now or by the build step.
            attemptRows = attemptRows.OrderBy(x => x.UT).ToList();
            foreach (var a in attemptRows) CaptureSequence.StampIfMissing(a);
            var filed = new List<GameAction>();
            var pending = new List<GameAction>();
            foreach (var a in attemptRows)
                (c.Rng.Next(2) == 0 ? filed : pending).Add(a);
            foreach (var a in filed) Ledger.AddAction(a);
            var marker = new ReFlySessionMarker
            {
                SessionId = c.Id("sess"), TreeId = "tree_" + slot.OriginId,
                ActiveReFlyRecordingId = attempt, OriginChildRecordingId = slot.TipId,
                SupersedeTargetId = slot.TipId, RewindPointUT = r, InvokedUT = r,
            };
            c.Scenario.ActiveReFlySessionMarker = marker;
            c.Log($"{where} -> open; attempt={attempt} rows=[{string.Join(" ", attemptRows.Select(Describe))}] " +
                  $"filed={filed.Count} pending={pending.Count}");

            // The live session leaves L exactly as it was.
            AssertSameLedger(c, l, Els().Where(x => x.RecordingId != attempt).ToList(), where + " live session");

            bool discard = c.Rng.Next(100) < 25;
            if (!discard)
            {
                var mergeRows = new List<GameAction>(Ledger.Actions);
                mergeRows.AddRange(pending);
                var mergeRetire = SupersedeCommit.BuildRetireSet(
                    closure, r, mergeRows, TombstonedIds(c), logDecisions: false).Rows;
                var liveRetire = SupersedeCommit.BuildMergeRetireSet(
                    marker, new HashSet<string> { attempt }, out _, pending);
                if (!liveRetire.Select(x => x.ActionId).SequenceEqual(mergeRetire.Select(x => x.ActionId)))
                    Fail(c, where, "the merge's retire set [" + string.Join(",", liveRetire.Select(x => x.ActionId)) +
                        "] is not the gate's [" + string.Join(",", mergeRetire.Select(x => x.ActionId)) + "]");
                double sessionClock = Math.Max(clock, attemptEnd);
                var verdict = ReFlyMergeDominance.Evaluate(new ReFlyMergeDominance.Inputs
                {
                    SessionId = marker.SessionId,
                    Els = Els(),
                    AttemptRecordingIds = new HashSet<string>(StringComparer.Ordinal) { attempt },
                    NewRows = pending,
                    RetireSet = mergeRetire,
                    RewindUT = r,
                    RespawnSecondsForRecording = _ => double.NaN,
                    DeadlineClockUT = sessionClock,
                });
                if (verdict.Pass)
                {
                    Merge(c, slot, closure, marker, attempt, attemptRows, pending, verdict, l, gateWalk,
                        sessionClock, attemptEnd, where);
                    return;
                }
                stats.Refused++;
                stats.Bump("refuse:" + verdict.Pool);
                c.Log($"merge refused pool={verdict.Pool} ut={R(verdict.UT)} -> discard");
            }

            // DISCARD: never refused; L comes back exactly.
            int removed = Ledger.RemoveActionsForRecordings(new HashSet<string> { attempt }, "fuzz-discard");
            if (removed != filed.Count)
                Fail(c, where, $"discard removed {removed} rows, the session filed {filed.Count}");
            c.Scenario.ActiveReFlySessionMarker = null;
            stats.Discarded++;
            AssertSameLedger(c, l, Els().ToList(), where + " discard");
            AssertSameBalances(c, walkL, Walk(Els(), clock), where + " discard");
            c.Log("discarded");
        }

        private void Merge(Career c, Slot slot, HashSet<string> closure, ReFlySessionMarker marker,
            string attempt, List<GameAction> attemptRows, List<GameAction> pending,
            ReFlyMergeDominanceVerdict verdict, List<GameAction> l, RecalculationEngine.IsolatedBalanceWalk gateWalk,
            double sessionClock, double attemptEnd, string where)
        {
            var before = SnapshotCredits(l, sessionClock);
            AddRecording(attempt, slot.RewindUT, attemptEnd, crashed: false);
            foreach (var a in pending) Ledger.AddAction(a);
            SupersedeCommit.CommitTombstones(marker, closure, attempt, attemptEnd,
                "2026-10-31T00:00:00Z", c.Scenario, verdict.Variant);
            c.Scenario.ActiveReFlySessionMarker = null;
            c.Clock = attemptEnd;
            stats.Merged++;
            if (verdict.Variant == LossSwapVariant.B) stats.VariantB++;
            if (slot.ReFlown > 0) stats.ChainedReFlies++;
            slot.ReFlown++;
            slot.TipId = attempt;
            c.Log($"merged variant={verdict.Variant} tombstones={c.Scenario.LedgerTombstones.Count}");

            List<GameAction> after = Els().ToList();
            var walkBefore = Walk(l, sessionClock);
            var walkAfter = Walk(after, sessionClock);

            // MERGE oracle: Invariant LS, read from the ledger the merge actually wrote.
            AssertDominates(c, walkBefore, walkAfter, where + " merge");

            // Every spend affordable in L stays affordable.
            var afterIds = new HashSet<string>(after.Select(x => x.ActionId), StringComparer.Ordinal);
            foreach (var s in l)
            {
                if (!RecalculationEngine.IsSpendingType(s.Type) || !afterIds.Contains(s.ActionId)) continue;
                bool okBefore, okAfter;
                walkBefore.AffordableByActionId.TryGetValue(s.ActionId, out okBefore);
                walkAfter.AffordableByActionId.TryGetValue(s.ActionId, out okAfter);
                if (okBefore && !okAfter)
                    Fail(c, where, $"merge made an affordable purchase unaffordable {Describe(s)}");
            }

            // Losses only: a row of L the merge retired is a loss row or a row the replay
            // did not count.
            foreach (var x in l)
            {
                if (afterIds.Contains(x.ActionId)) continue;
                bool counted;
                gateWalk.EffectiveByActionId.TryGetValue(x.ActionId, out counted);
                if (!DesignLossRow(x) && counted)
                    Fail(c, where, $"merge retired a counted non-loss row {Describe(x)}");
            }

            AssertCreditsKept(c, before, SnapshotCredits(c), where + " merge");

            AssertRealCreditsKept(c, RealTotals(l, sessionClock), RealTotals(after, sessionClock), where + " merge");
        }

        /// <summary>The live recalculation over the real modules: no science subject earns
        /// less, no credited milestone or record threshold is lost.</summary>
        private void AssertRealCreditsKept(Career c, Totals before, Totals after, string where)
        {
            foreach (var kv in before.Subjects)
            {
                double now;
                after.Subjects.TryGetValue(kv.Key, out now);
                if (now < kv.Value - Eps)
                    Fail(c, where, $"science credited to subject {kv.Key} dropped: {R(kv.Value)} -> {R(now)}");
            }
            foreach (string m in before.Milestones)
                if (!after.Milestones.Contains(m))
                    Fail(c, where, $"credited milestone {m} lost");
            if (after.RecordThresholds < before.RecordThresholds)
                Fail(c, where, $"credited record thresholds dropped {before.RecordThresholds} -> {after.RecordThresholds}");
        }

        // =====================================================================
        // Row generation
        // =====================================================================

        private List<GameAction> BuildAttempt(Career c, string rec, Slot slot, double r, out double end)
        {
            var rows = new List<GameAction>();
            end = r + Amount(c.Rng, 20f, 500f);
            int fate = c.Rng.Next(100);
            if (fate < 45)
            {
                // Crashes again: losses only (maybe fewer, maybe more than the replaced flight).
                double crashUT = Between(c.Rng, r, end);
                var dying = slot.Crew.Where(_ => c.Rng.Next(3) != 0).ToList();
                AddCrashLosses(c, rows, rec, dying, r, crashUT);
                foreach (string k in slot.Crew.Except(dying))
                    rows.Add(CrewRow(c, rec, k, r, crashUT, KerbalEndState.Recovered));
                end = crashUT;
            }
            else
            {
                int gains = c.Rng.Next(1, 4);
                for (int i = 0; i < gains; i++)
                {
                    var g = RandomGain(c, rows, rec, Between(c.Rng, r, end), allowContractFail: true, isAttempt: true);
                    if (g != null && g.Type == GameActionType.MilestoneAchievement && g.MilestoneId != RecordId
                        && c.Rng.Next(4) == 0)
                    {
                        // A strategy active at the re-fly's UT changes what stock pays.
                        float k = Amount(c.Rng, 0.5f, 1.5f);
                        g.MilestoneFundsAwarded *= k;
                        g.MilestoneRepAwarded *= k;
                    }
                    if (g != null) rows.Add(g);
                }
                if (fate < 70 && c.Rng.Next(2) == 0)
                {
                    // Landed but lost somebody, or a vessel-loss penalty.
                    rows.Add(RepLoss(c, rec, Between(c.Rng, r, end), ReputationPenaltySource.Other));
                }
                foreach (string k in slot.Crew)
                    rows.Add(CrewRow(c, rec, k, r, end, KerbalEndState.Recovered));
            }
            return rows;
        }

        private void AddCrashLosses(Career c, List<GameAction> rows, string rec, List<string> crew,
            double start, double crashUT)
        {
            foreach (string k in crew)
            {
                rows.Add(CrewRow(c, rec, k, start, crashUT, KerbalEndState.Dead));
                rows.Add(RepLoss(c, rec, crashUT, ReputationPenaltySource.KerbalDeath));
            }
            if (c.Rng.Next(100) < 50)
                rows.Add(RepLoss(c, rec, crashUT, ReputationPenaltySource.Other));
            if (c.Rng.Next(100) < 10)
            {
                rows.Add(new GameAction
                {
                    ActionId = c.Id("destroy"), Type = GameActionType.FacilityDestruction, UT = crashUT,
                    RecordingId = rec, FacilityId = Facilities[c.Rng.Next(Facilities.Length)],
                });
            }
        }

        private static GameAction RandomGain(Career c, List<GameAction> flight, string rec, double ut,
            bool allowContractFail, bool isAttempt)
        {
            int kind = c.Rng.Next(108);
            if (kind >= 100)
                return kind < 104 ? RecordRow(c, flight, rec, ut) : ParameterRow(c, flight, rec, ut);
            if (kind < 22)
                return Milestone(c, rec, ut, PickMilestone(c));
            if (kind < 44)
            {
                string subject = SubjectIds[c.Rng.Next(SubjectIds.Length)];
                float cap = c.SubjectCap[subject];
                return new GameAction
                {
                    ActionId = c.Id("sci"), Type = GameActionType.ScienceEarning, UT = ut, RecordingId = rec,
                    SubjectId = subject, SubjectMaxValue = cap, ScienceAwarded = Amount(c.Rng, 1f, cap),
                    Method = ScienceMethod.Recovered,
                };
            }
            if (kind < 60)
            {
                return new GameAction
                {
                    ActionId = c.Id("funds"), Type = GameActionType.FundsEarning, UT = ut, RecordingId = rec,
                    FundsAwarded = Amount(c.Rng, 500f, 8000f), FundsSource = FundsEarningSource.Recovery,
                };
            }
            if (kind < 70)
            {
                return new GameAction
                {
                    ActionId = c.Id("rep"), Type = GameActionType.ReputationEarning, UT = ut, RecordingId = rec,
                    NominalRep = Amount(c.Rng, 1f, 10f), RepSource = ReputationSource.Other,
                };
            }
            // A contract outcome for a contract accepted before this row.
            var open = c.Contracts.Where(x => x.AcceptUT < ut
                && (double.IsNaN(x.DeadlineUT) || ut < x.DeadlineUT)
                && !HasOutcome(flight, x.Id)
                && !HasOutcomeBefore(x.Id, ut)).ToList();
            if (open.Count == 0)
                return null;
            var spec = open[c.Rng.Next(open.Count)];
            bool fail = c.Rng.Next(100) < (isAttempt ? 30 : 15);
            if (fail && (allowContractFail && (isAttempt || !HasOutcome(Els(), spec.Id))))
            {
                return new GameAction
                {
                    ActionId = c.Id("fail"), Type = GameActionType.ContractFail, UT = ut, RecordingId = rec,
                    ContractId = spec.Id, FundsPenalty = spec.FundsPenalty, RepPenalty = spec.RepPenalty,
                };
            }
            return new GameAction
            {
                ActionId = c.Id("complete"), Type = GameActionType.ContractComplete, UT = ut, RecordingId = rec,
                ContractId = spec.Id, FundsReward = spec.FundsReward, RepReward = spec.RepReward,
                ScienceReward = spec.ScienceReward,
            };
        }

        private static bool HasOutcome(IEnumerable<GameAction> rows, string contractId)
        {
            foreach (var a in rows)
                if (a.ContractId == contractId
                    && (a.Type == GameActionType.ContractComplete || a.Type == GameActionType.ContractFail))
                    return true;
            return false;
        }

        /// <summary>A world-record row: stock's record node at <paramref name="ut"/> stands at
        /// the highest threshold the timeline crossed before it, so the flight crosses the
        /// next one to three (each paid once per career, earliest crossing wins).</summary>
        private static GameAction RecordRow(Career c, List<GameAction> flight, string rec, double ut)
        {
            int top = 0;
            foreach (var a in Els().Concat(flight))
                if (a.Type == GameActionType.MilestoneAchievement && a.MilestoneId == RecordId && a.UT < ut
                    && a.MilestoneRecordBand != null)
                    foreach (var b in a.MilestoneRecordBand) top = Math.Max(top, b.Index);
            int n = c.Rng.Next(1, 4);
            if (top + n > c.RecordFunds.Length) return null;
            var band = new List<RecordThresholdCredit>();
            float funds = 0f;
            for (int i = top + 1; i <= top + n; i++)
            {
                band.Add(new RecordThresholdCredit(i, c.RecordFunds[i - 1], 0.5f, 0f));
                funds += c.RecordFunds[i - 1];
            }
            return new GameAction
            {
                ActionId = c.Id("record"), Type = GameActionType.MilestoneAchievement, UT = ut, RecordingId = rec,
                MilestoneId = RecordId, MilestoneFundsAwarded = funds, MilestoneRepAwarded = 0.5f * n,
                MilestoneRecordThresholds = n, MilestoneRecordBand = band,
            };
        }

        private const string RecordId = "RecordsAltitude";

        /// <summary>A contract parameter of an open contract (each paid once).</summary>
        private static GameAction ParameterRow(Career c, List<GameAction> flight, string rec, double ut)
        {
            var open = c.Contracts.Where(x => x.AcceptUT < ut
                && (double.IsNaN(x.DeadlineUT) || ut < x.DeadlineUT)
                && !HasOutcome(flight, x.Id)
                && !HasOutcomeBefore(x.Id, ut)).ToList();
            if (open.Count == 0) return null;
            var spec = open[c.Rng.Next(open.Count)];
            int p = c.Rng.Next(spec.ParamFunds.Length);
            return new GameAction
            {
                ActionId = c.Id("param"), Type = GameActionType.ContractParameterComplete, UT = ut, RecordingId = rec,
                ContractId = spec.Id, ContractParameterId = "p" + p.ToString(IC), FundsReward = spec.ParamFunds[p],
                RepReward = 1f,
            };
        }

        /// <summary>Stock shows a contract resolved at <paramref name="ut"/> when an outcome
        /// row is on the timeline before it, so no flight there can resolve it again.</summary>
        private static bool HasOutcomeBefore(string contractId, double ut)
        {
            foreach (var a in Els())
                if (a.ContractId == contractId && a.UT <= ut
                    && (a.Type == GameActionType.ContractComplete || a.Type == GameActionType.ContractFail))
                    return true;
            return false;
        }

        private static string PickMilestone(Career c) => MilestoneIds[c.Rng.Next(MilestoneIds.Length)];

        private static GameAction Milestone(Career c, string rec, double ut, string id)
        {
            var spec = c.Milestones[id];
            return new GameAction
            {
                ActionId = c.Id("ms"), Type = GameActionType.MilestoneAchievement, UT = ut, RecordingId = rec,
                MilestoneId = id, MilestoneFundsAwarded = spec.Funds, MilestoneRepAwarded = spec.Rep,
                MilestoneScienceAwarded = spec.Science,
            };
        }

        private static GameAction RepLoss(Career c, string rec, double ut, ReputationPenaltySource source)
        {
            return new GameAction
            {
                ActionId = c.Id("repl"), Type = GameActionType.ReputationPenalty, UT = ut, RecordingId = rec,
                NominalPenalty = Amount(c.Rng, 1f, 15f), RepPenaltySource = source,
            };
        }

        private static GameAction CrewRow(Career c, string rec, string kerbal, double start, double end,
            KerbalEndState state)
        {
            return new GameAction
            {
                ActionId = c.Id("crew"), Type = GameActionType.KerbalAssignment, UT = start, RecordingId = rec,
                KerbalName = kerbal, KerbalRole = "Pilot", StartUT = (float)start, EndUT = (float)end,
                KerbalEndStateField = state,
            };
        }

        /// <summary>Kerbals a new flight at <paramref name="launch"/> can take: alive, and
        /// with no committed assignment reaching <paramref name="launch"/> or later (a kerbal a
        /// committed future flight uses is held, as the crew reservation holds him).</summary>
        private static List<string> PickFreeCrew(Career c, double launch, int count)
        {
            var busy = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in Els())
            {
                if (a.Type != GameActionType.KerbalAssignment || a.KerbalName == null) continue;
                if (a.KerbalEndStateField == KerbalEndState.Dead || a.EndUT >= launch - 1.0 || a.UT >= launch - 1.0)
                    busy.Add(a.KerbalName);
            }
            var free = c.Kerbals.Where(k => !busy.Contains(k)).ToList();
            var pick = new List<string>();
            while (pick.Count < count && free.Count > 0)
            {
                int i = c.Rng.Next(free.Count);
                pick.Add(free[i]);
                free.RemoveAt(i);
            }
            return pick;
        }

        private static void AddRecording(string id, double start, double end, bool crashed)
        {
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                RecordingId = id,
                VesselName = "Fuzz " + id,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
                TerminalStateValue = crashed ? TerminalState.Destroyed : TerminalState.Recovered,
            });
        }

        // =====================================================================
        // Oracles
        // =====================================================================

        /// <summary>Design section 2.12 Terms, restated: the complete loss list.</summary>
        private static bool DesignLossRow(GameAction a)
        {
            if (a == null) return false;
            if (a.Type == GameActionType.FacilityDestruction) return true;
            if (a.Type == GameActionType.KerbalAssignment)
                return a.KerbalEndStateField == KerbalEndState.Dead
                    || a.KerbalEndStateField == KerbalEndState.Aboard
                    || a.KerbalEndStateField == KerbalEndState.Unknown;
            if (a.Type == GameActionType.ReputationPenalty)
                return a.RepPenaltySource == ReputationPenaltySource.KerbalDeath
                    || a.RepPenaltySource == ReputationPenaltySource.Other;
            return false;
        }

        /// <summary>The once-ever credits a walk holds: milestone id and completed contract
        /// id to the UT of the counted row.</summary>
        private sealed class Credits
        {
            internal readonly Dictionary<string, double> Milestones = new Dictionary<string, double>();
            internal readonly Dictionary<string, double> Contracts = new Dictionary<string, double>();
        }

        private static Credits SnapshotCredits(Career c) => SnapshotCredits(Els().ToList(), c.DeadlineClock());

        private static Credits SnapshotCredits(List<GameAction> rows, double clock)
        {
            var walk = Walk(rows, clock);
            var credits = new Credits();
            foreach (var a in rows)
            {
                bool counted;
                if (!walk.EffectiveByActionId.TryGetValue(a.ActionId, out counted) || !counted) continue;
                if (a.Type == GameActionType.MilestoneAchievement && a.MilestoneId != RecordId)
                    Earliest(credits.Milestones, a.MilestoneId, a.UT);
                else if (a.Type == GameActionType.ContractComplete)
                    Earliest(credits.Contracts, a.ContractId, a.UT);
            }
            return credits;
        }

        private static void Earliest(Dictionary<string, double> map, string key, double ut)
        {
            double cur;
            if (!map.TryGetValue(key, out cur) || ut < cur) map[key] = ut;
        }

        private void AssertCreditsKept(Career c, Credits before, Credits after, string where)
        {
            foreach (var kv in before.Milestones)
            {
                double ut;
                if (!after.Milestones.TryGetValue(kv.Key, out ut))
                    Fail(c, where, $"milestone {kv.Key} credited at {R(kv.Value)} is no longer credited");
                if (ut > kv.Value + 1e-6)
                    Fail(c, where, $"milestone {kv.Key} moved later {R(kv.Value)} -> {R(ut)}");
                if (ut < kv.Value - 1e-6) stats.EarlierCredits++;
            }
            foreach (var kv in before.Contracts)
            {
                double ut;
                if (!after.Contracts.TryGetValue(kv.Key, out ut))
                    Fail(c, where, $"contract {kv.Key} completed at {R(kv.Value)} is no longer completed");
                if (ut > kv.Value + 1e-6)
                    Fail(c, where, $"contract {kv.Key} completion moved later {R(kv.Value)} -> {R(ut)}");
                if (ut < kv.Value - 1e-6) stats.EarlierCredits++;
            }
        }

        /// <summary>EARLIEST WINS: the walk credits a one-shot milestone to its first row in
        /// walk order, and resolves a contract with its first outcome row.</summary>
        private void AssertEarliestWins(Career c, RecalculationEngine.IsolatedBalanceWalk walk, string where)
        {
            var rows = Els().ToList();
            rows.Sort(RecalculationEngine.CompareWalkOrder);
            var seenMilestone = new HashSet<string>(StringComparer.Ordinal);
            var resolved = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in rows)
            {
                bool counted;
                walk.EffectiveByActionId.TryGetValue(a.ActionId, out counted);
                if (a.Type == GameActionType.MilestoneAchievement && a.MilestoneId != RecordId)
                {
                    bool first = seenMilestone.Add(a.MilestoneId);
                    if (first != counted)
                        Fail(c, where, $"milestone {a.MilestoneId}: row {Describe(a)} counted={counted} first={first}");
                }
                else if (a.Type == GameActionType.ContractComplete || a.Type == GameActionType.ContractFail)
                {
                    bool first = resolved.Add(a.ContractId);
                    if (first != counted)
                        Fail(c, where, $"contract {a.ContractId}: outcome {Describe(a)} counted={counted} first={first}");
                }
            }
        }

        private void AssertDominates(Career c, RecalculationEngine.IsolatedBalanceWalk before,
            RecalculationEngine.IsolatedBalanceWalk after, string where)
        {
            var uts = new SortedSet<double>(before.Samples.Select(s => s.UT));
            uts.UnionWith(after.Samples.Select(s => s.UT));
            foreach (double ut in uts)
            {
                var b = At(before, ut);
                var a = At(after, ut);
                if (a.Funds < b.Funds - Eps)
                    Fail(c, where, $"funds dropped at ut={R(ut)}: {R(b.Funds)} -> {R(a.Funds)}");
                if (a.Science < b.Science - Eps)
                    Fail(c, where, $"science dropped at ut={R(ut)}: {R(b.Science)} -> {R(a.Science)}");
                if (a.Reputation < b.Reputation - Eps)
                    Fail(c, where, $"reputation dropped at ut={R(ut)}: {R(b.Reputation)} -> {R(a.Reputation)}");
            }
        }

        private void AssertSameBalances(Career c, RecalculationEngine.IsolatedBalanceWalk before,
            RecalculationEngine.IsolatedBalanceWalk after, string where)
        {
            AssertDominates(c, before, after, where);
            AssertDominates(c, after, before, where + " (reverse)");
        }

        private void AssertBalancesEqualBefore(Career c, RecalculationEngine.IsolatedBalanceWalk before,
            RecalculationEngine.IsolatedBalanceWalk after, double firstRowUT, string where)
        {
            foreach (var s in before.Samples)
            {
                if (s.UT >= firstRowUT) break;
                var a = At(after, s.UT);
                var b = At(before, s.UT);
                if (Math.Abs(a.Funds - b.Funds) > Eps || Math.Abs(a.Science - b.Science) > Eps
                    || Math.Abs(a.Reputation - b.Reputation) > Eps)
                    Fail(c, where, $"balance before the flight's first row (ut={R(firstRowUT)}) changed at " +
                        $"ut={R(s.UT)}: funds {R(b.Funds)}->{R(a.Funds)} sci {R(b.Science)}->{R(a.Science)} " +
                        $"rep {R(b.Reputation)}->{R(a.Reputation)}");
            }
        }

        private void AssertSameLedger(Career c, List<GameAction> expected, List<GameAction> actual, string where)
        {
            var e = expected.Select(x => x.ActionId).ToList();
            var a = actual.Select(x => x.ActionId).ToList();
            if (!e.SequenceEqual(a))
                Fail(c, where, "the Effective Ledger Set changed: expected [" + string.Join(",", e) +
                    "] got [" + string.Join(",", a) + "]");
        }

        private sealed class Totals
        {
            internal double Funds, Science, Reputation;
            internal readonly Dictionary<string, double> Subjects = new Dictionary<string, double>(StringComparer.Ordinal);
            internal HashSet<string> Milestones = new HashSet<string>(StringComparer.Ordinal);
            internal int RecordThresholds;
        }

        /// <summary>The production recalculation (no cutoff) over the registered modules.</summary>
        private static Totals RealTotals(IReadOnlyList<GameAction> rows, double clock)
        {
            RecalculationEngine.Recalculate(new List<GameAction>(rows), null, clock);
            var t = new Totals
            {
                Funds = LedgerOrchestrator.Funds.GetRunningBalance(),
                Science = LedgerOrchestrator.Science.GetRunningScience(),
                Reputation = LedgerOrchestrator.Reputation.GetRunningRep(),
                Milestones = new HashSet<string>(LedgerOrchestrator.Milestones.GetCreditedMilestoneIds(), StringComparer.Ordinal),
            };
            foreach (string subject in SubjectIds)
                t.Subjects[subject] = LedgerOrchestrator.Science.GetSubjectCredited(subject);
            t.RecordThresholds = LedgerOrchestrator.Milestones.GetEffectiveMilestoneCount(RecordId);
            return t;
        }

        /// <summary>The isolated walk every Re-Fly check reads must end where the live
        /// recalculation ends, or the checks prove something about a different career.</summary>
        private void AssertWalkMatchesRecalculation(Career c, RecalculationEngine.IsolatedBalanceWalk walk, string where)
        {
            if (walk.Samples.Count == 0) return;
            var last = walk.Samples[walk.Samples.Count - 1];
            var real = RealTotals(Els(), c.DeadlineClock());
            if (Math.Abs(real.Funds - last.Funds) > Eps || Math.Abs(real.Science - last.Science) > Eps
                || Math.Abs(real.Reputation - last.Reputation) > Eps)
                Fail(c, where, $"isolated walk ends at funds={R(last.Funds)} sci={R(last.Science)} rep={R(last.Reputation)}, " +
                    $"the recalculation at funds={R(real.Funds)} sci={R(real.Science)} rep={R(real.Reputation)}");
        }

        private static RecalculationEngine.LedgerBalanceSample At(RecalculationEngine.IsolatedBalanceWalk walk, double ut)
        {
            var result = new RecalculationEngine.LedgerBalanceSample { UT = ut };
            foreach (var s in walk.Samples)
            {
                if (s.UT > ut) break;
                result.Funds = s.Funds;
                result.Science = s.Science;
                result.Reputation = s.Reputation;
            }
            return result;
        }

        private static double MinFrom(RecalculationEngine.IsolatedBalanceWalk walk, double ut,
            Func<RecalculationEngine.LedgerBalanceSample, double> pick)
        {
            double min = pick(At(walk, ut));
            foreach (var s in walk.Samples)
                if (s.UT >= ut) min = Math.Min(min, pick(s));
            return min;
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static IReadOnlyList<GameAction> Els() => EffectiveState.ComputeELS();

        private static RecalculationEngine.IsolatedBalanceWalk Walk(IReadOnlyList<GameAction> rows, double clock)
        {
            return RecalculationEngine.WalkBalancesOnClones(new List<GameAction>(rows), clock);
        }

        private static HashSet<string> TombstonedIds(Career c)
        {
            return new HashSet<string>(c.Scenario.LedgerTombstones.Select(t => t.ActionId), StringComparer.Ordinal);
        }

        private void Fail(Career c, string where, string what)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Re-Fly value fuzzer: {what}");
            sb.AppendLine($"  at: {where}");
            sb.AppendLine($"  seed={BaseSeed.ToString(IC)}+{c.Iteration.ToString(IC)} op={c.Op.ToString(IC)} {stats}");
            sb.AppendLine("history:");
            sb.Append(c.History);
            sb.AppendLine("ledger (tombstoned rows marked x):");
            var tomb = TombstonedIds(c);
            foreach (var a in Ledger.Actions)
                sb.AppendLine("  " + (tomb.Contains(a.ActionId) ? "x " : "  ") + Describe(a));
            throw new Xunit.Sdk.XunitException(sb.ToString());
        }

        private static string Describe(GameAction a)
        {
            if (a == null) return "<null>";
            string detail;
            switch (a.Type)
            {
                case GameActionType.MilestoneAchievement:
                    detail = $"{a.MilestoneId} f={R(a.MilestoneFundsAwarded)} r={R(a.MilestoneRepAwarded)} s={R(a.MilestoneScienceAwarded)}"; break;
                case GameActionType.ScienceEarning:
                    detail = $"{a.SubjectId} {R(a.ScienceAwarded)}/{R(a.SubjectMaxValue)}"; break;
                case GameActionType.ContractAccept:
                case GameActionType.ContractComplete:
                case GameActionType.ContractFail:
                    detail = $"{a.ContractId} adv={R(a.AdvanceFunds)} f={R(a.FundsReward)} r={R(a.RepReward)} pen={R(a.FundsPenalty)}/{R(a.RepPenalty)}"; break;
                case GameActionType.ReputationPenalty:
                    detail = $"{a.RepPenaltySource} {R(a.NominalPenalty)}"; break;
                case GameActionType.KerbalAssignment:
                    detail = $"{a.KerbalName} {R(a.StartUT)}-{R(a.EndUT)} {a.KerbalEndStateField}"; break;
                case GameActionType.FundsEarning: detail = R(a.FundsAwarded); break;
                case GameActionType.ReputationEarning: detail = R(a.NominalRep); break;
                case GameActionType.FundsSpending: detail = R(a.FundsSpent); break;
                case GameActionType.ScienceSpending: detail = $"{a.NodeId} {R(a.Cost)}"; break;
                case GameActionType.FacilityUpgrade: detail = $"{a.FacilityId} {R(a.FacilityCost)}"; break;
                case GameActionType.FacilityDestruction: detail = a.FacilityId; break;
                case GameActionType.KerbalHire: detail = $"{a.KerbalName} {R(a.HireCost)}"; break;
                default: detail = ""; break;
            }
            return $"{a.Type}:{a.ActionId}@{R(a.UT)} rec={a.RecordingId ?? "-"} cseq={a.CaptureSequence.ToString(IC)} {detail}";
        }

        private static float Amount(Random rng, float min, float max)
            => min + (float)(rng.NextDouble() * (max - min));

        /// <summary>A UT in [a, b]; one in ten lands exactly on <paramref name="a"/> (a row at
        /// the separation UT itself, the cutoff boundary).</summary>
        private static double Between(Random rng, double a, double b)
        {
            if (rng.Next(10) == 0) return a;
            return Math.Min(b, Math.Max(a, Math.Round(a + rng.NextDouble() * (b - a), 1)));
        }

        private static string R(double v) => v.ToString("0.###", IC);

        private static int ResolveIterations()
        {
            string raw = Environment.GetEnvironmentVariable(IterationEnvVar);
            int n;
            if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, NumberStyles.Integer, IC, out n) && n > 0)
                return n;
            return DefaultIterations;
        }

        private sealed class Stats
        {
            internal readonly SortedDictionary<string, int> Kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
            internal void Bump(string k) { int n; Kinds.TryGetValue(k, out n); Kinds[k] = n + 1; }
            internal int Sealed, Open, Merged, Refused, Discarded, VariantB, ChainedReFlies, EarlierCredits, RewoundFlights;

            public override string ToString()
            {
                return $"[sealed={Sealed} open={Open} merged={Merged} refused={Refused} discarded={Discarded} " +
                    $"variantB={VariantB} chained={ChainedReFlies} earlierCredits={EarlierCredits} rewound={RewoundFlights}] " +
                    string.Join(" ", Kinds.Select(kv => kv.Key + "=" + kv.Value.ToString(IC)));
            }
        }
    }
}
