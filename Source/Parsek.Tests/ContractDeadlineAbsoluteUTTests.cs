using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// CONTRACT-DEADLINE-CAPTURED-AS-DURATION.
    ///
    /// <para><see cref="GameAction.DeadlineUT"/> is an ABSOLUTE UT that every consumer
    /// compares against the clock. Stock's <c>Contract.TimeDeadline</c> is a DURATION;
    /// parking it in <c>DeadlineUT</c> makes every accepted contract read as elapsed
    /// once the career clock passes one deadline's worth of seconds, and
    /// <c>ContractsModule.CheckDeadlines</c> retires it on the next dispatched action.</para>
    ///
    /// <para>This file covers the on-disk key (<c>deadlineAbsUT</c>, a double), the
    /// point-of-use SANITY GUARD, the projection horizon, and the eviction mechanism a
    /// duration value would trip.</para>
    /// </summary>
    [Collection("Sequential")]
    public class ContractDeadlineAbsoluteUTTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>The PartTest contract guid from the C2Career fixture, used as a realistic id.</summary>
        private const string C2ContractGuid = "d45c8b23-b4c6-4626-8d63-eded40f90214";

        private readonly List<string> logLines = new List<string>();

        public ContractDeadlineAbsoluteUTTests()
        {
            LedgerOrchestrator.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            LedgerOrchestrator.Initialize();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // 1. The on-disk key
        // ================================================================

        [Fact]
        public void Deserialize_AbsoluteKey_UsedVerbatim()
        {
            var n = new ConfigNode("GAME_ACTION");
            n.AddValue("ut", "1000");
            n.AddValue("type", ((int)GameActionType.ContractAccept).ToString(IC));
            n.AddValue(GameAction.ContractDeadlineAbsoluteKey, "9202600");

            Assert.Equal(9202600.0, GameAction.DeserializeFrom(n).DeadlineUT, 6);
        }

        [Fact]
        public void Deserialize_NoDeadlineKey_StaysNaN()
        {
            var n = new ConfigNode("GAME_ACTION");
            n.AddValue("ut", "1000");
            n.AddValue("type", ((int)GameActionType.ContractAccept).ToString(IC));
            n.AddValue("contractId", "no-deadline");

            Assert.True(double.IsNaN(GameAction.DeserializeFrom(n).DeadlineUT));
        }

        [Fact]
        public void Deserialize_UnparseableValue_WarnsAndReadsAsNoDeadline()
        {
            var n = new ConfigNode("GAME_ACTION");
            n.AddValue("ut", "1000");
            n.AddValue("type", ((int)GameActionType.ContractAccept).ToString(IC));
            n.AddValue("contractId", "garbled");
            n.AddValue(GameAction.ContractDeadlineAbsoluteKey, "not-a-number");

            Assert.True(double.IsNaN(GameAction.DeserializeFrom(n).DeadlineUT));
            Assert.Contains(logLines, l =>
                l.Contains("[GameAction]") && l.Contains("unparseable")
                && l.Contains("garbled"));
        }

        /// <summary>
        /// A no-deadline row survives a full save/load cycle untouched, never as a
        /// deadline of "accept UT + 0".
        /// </summary>
        [Fact]
        public void RoundTrip_NoDeadlineRow_StaysNaN()
        {
            var original = new GameAction
            {
                UT = 8381.0195501716571,
                Type = GameActionType.ContractAccept,
                ContractId = "no-deadline",
                DeadlineUT = double.NaN
            };

            var root = new ConfigNode("LEDGER");
            original.SerializeInto(root);
            ConfigNode row = root.GetNodes("GAME_ACTION")[0];

            Assert.Null(row.GetValue(GameAction.ContractDeadlineAbsoluteKey));
            Assert.True(double.IsNaN(GameAction.DeserializeFrom(row).DeadlineUT));
        }

        /// <summary>
        /// The field is a <c>double</c>: a mid-career absolute UT round-trips without
        /// losing whole seconds, which a <c>float</c> (~7 significant digits) could not.
        /// </summary>
        [Fact]
        public void RoundTrip_MidCareerAbsoluteUT_WithoutTruncation()
        {
            const double midCareerDeadline = 82369527.4730284;

            var original = new GameAction
            {
                UT = 74140955.7,
                Type = GameActionType.ContractAccept,
                ContractId = "aged",
                DeadlineUT = midCareerDeadline
            };

            var root = new ConfigNode("LEDGER");
            original.SerializeInto(root);
            GameAction restored = GameAction.DeserializeFrom(root.GetNodes("GAME_ACTION")[0]);

            Assert.Equal(midCareerDeadline, restored.DeadlineUT);
            Assert.NotEqual(midCareerDeadline, (double)(float)midCareerDeadline);
        }

        // ================================================================
        // 2. The point-of-use sanity guard
        // ================================================================

        [Theory]
        [InlineData(500.0, 100.0, false)]   // ordinary: deadline after accept
        [InlineData(100.0, 100.0, true)]    // exactly at the accept — never a real deadline
        [InlineData(99.0, 100.0, true)]     // before the accept — the duration-shaped defect
        public void ImplausibleDeadline_Classification(double deadline, double acceptUT, bool expected)
        {
            Assert.Equal(expected,
                ContractsModule.IsImplausibleContractDeadline(deadline, acceptUT));
        }

        [Fact]
        public void ImplausibleDeadline_IsNotAnElapsedDeadline()
        {
            // A duration stored as a deadline for a contract accepted LATE in a career:
            // 8228571.5 seconds looks elapsed against a clock of 9e6 but sits BEFORE the
            // accept at 9.05e6, so it was never a UT.
            const double acceptUT = 9050000.0;
            const double storedDuration = 8228571.5;

            Assert.False(ContractsModule.HasContractDeadlineElapsed(
                9060000.0, storedDuration, acceptUT));

            // ...and the guard refuses in the other direction too: a completion still
            // earns its reward rather than being called late.
            Assert.True(ContractsModule.IsBeforeContractDeadline(
                9060000.0, storedDuration, acceptUT));
        }

        [Fact]
        public void NaNDeadline_IsStillOpenEnded()
        {
            Assert.False(ContractsModule.HasContractDeadlineElapsed(500.0, double.NaN, 100.0));
            Assert.False(ContractsModule.IsBeforeContractDeadline(500.0, double.NaN, 100.0));
            Assert.False(ContractsModule.IsImplausibleContractDeadline(double.NaN, 100.0));
        }

        /// <summary>
        /// The guard WARNs, and warns ONCE per contract rather than once per dispatched
        /// action — <c>CheckDeadlines</c> runs on every single action in the walk.
        /// </summary>
        [Fact]
        public void ImplausibleDeadline_WarnsOncePerContract()
        {
            var module = new ContractsModule();
            module.Reset();

            var accept = new GameAction
            {
                UT = 9050000.0,
                Type = GameActionType.ContractAccept,
                ContractId = "impossible",
                DeadlineUT = 8228571.5
            };
            module.ProcessAction(accept);

            for (int i = 1; i <= 5; i++)
            {
                module.ProcessAction(new GameAction
                {
                    UT = 9050000.0 + i * 1000.0,
                    Type = GameActionType.FundsEarning,
                    FundsAwarded = 1f
                });
            }

            Assert.Contains("impossible", module.GetActiveContractIds());

            int warns = 0;
            for (int i = 0; i < logLines.Count; i++)
            {
                if (logLines[i].Contains("Implausible contract deadline IGNORED")
                    && logLines[i].Contains("impossible"))
                    warns++;
            }
            Assert.Equal(1, warns);
        }

        // ================================================================
        // 3. ComputeProjectionHorizon
        // ================================================================

        /// <summary>
        /// The horizon takes the max of every action UT and every ContractAccept's
        /// deadline. With an ABSOLUTE deadline a mid-career pending deadline extends the
        /// horizon past the last action, which is the whole point: the horizon feeds
        /// <c>minProjected</c> -&gt; <c>GetAvailableFunds()</c> -&gt; the reservation-net
        /// number written to the stock funds bar.
        ///
        /// <para>Pre-fix the same career gave the WRONG answer in both directions — a
        /// duration below the action UTs never extended the horizon at all (mid-career),
        /// and a duration far above them blew it out to ~8.2e6 for a career at UT ~8000
        /// (early-career). Both cells are pinned here.</para>
        /// </summary>
        [Fact]
        public void ProjectionHorizon_MidCareerAbsoluteDeadline_ExtendsPastLastAction()
        {
            var actions = new List<GameAction>
            {
                new GameAction { UT = 8_000_000.0, Type = GameActionType.FundsEarning },
                new GameAction
                {
                    UT = 8_100_000.0,
                    Type = GameActionType.ContractAccept,
                    ContractId = "aged",
                    DeadlineUT = 8_300_000.0
                },
                new GameAction { UT = 8_200_000.0, Type = GameActionType.FundsEarning },
            };

            Assert.Equal(8_300_000.0,
                RecalculationEngine.ComputeProjectionHorizonForTesting(actions), 6);
        }

        [Fact]
        public void ProjectionHorizon_ImplausibleDeadline_CannotBlowTheHorizonOut()
        {
            // The early-career degradation, restated as the guard's job: a duration-shaped
            // value that predates its own accept must not extend the horizon.
            var actions = new List<GameAction>
            {
                new GameAction { UT = 9_000_000.0, Type = GameActionType.FundsEarning },
                new GameAction
                {
                    UT = 9_050_000.0,
                    Type = GameActionType.ContractAccept,
                    ContractId = "impossible",
                    DeadlineUT = 8_228_571.5
                },
            };

            Assert.Equal(9_050_000.0,
                RecalculationEngine.ComputeProjectionHorizonForTesting(actions), 6);
        }

        // ================================================================
        // 4. The eviction mechanism
        // ================================================================

        /// <summary>
        /// The MECHANISM the absolute key exists to avoid: feed the module the pre-fix value directly (a duration parked in
        /// <c>DeadlineUT</c>, plausible because it sits after this early accept) and the
        /// contract IS evicted by <c>CheckDeadlines</c> on the next action, with the
        /// <c>DeadlineExpired</c> log line the defect entry names as the cheapest live
        /// discriminator. The sanity guard deliberately does NOT save this case: the
        /// value is indistinguishable from a real deadline here, so the capture is the
        /// fix and the guard is only a net.
        /// </summary>
        [Fact]
        public void AgedCareer_PreFixDurationValue_IsEvictedByCheckDeadlines()
        {
            const double acceptUT = 8381.0195501716571;
            const double storedDuration = 8228571.5;

            var module = new ContractsModule();
            module.Reset();
            module.ProcessAction(new GameAction
            {
                UT = acceptUT,
                Type = GameActionType.ContractAccept,
                ContractId = C2ContractGuid,
                DeadlineUT = storedDuration        // the defect, verbatim
            });
            Assert.Contains(C2ContractGuid, module.GetActiveContractIds());

            module.ProcessAction(new GameAction
            {
                UT = 8_230_000.0,
                Type = GameActionType.FundsEarning,
                FundsAwarded = 1f
            });

            Assert.DoesNotContain(C2ContractGuid, module.GetActiveContractIds());
            Assert.Contains(logLines, l =>
                l.Contains("DeadlineExpired") && l.Contains(C2ContractGuid));
        }
    }
}
