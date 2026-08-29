using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// CONTRACT-DEADLINE-CAPTURED-AS-DURATION.
    ///
    /// <para><c>GameStateRecorder.OnContractAccepted</c> used to capture stock's
    /// <c>Contract.TimeDeadline</c> — a DURATION in seconds — into
    /// <see cref="GameAction.DeadlineUT"/>, which every consumer reads as an ABSOLUTE
    /// UT. Once a career's clock passed one deadline's worth of seconds, every accepted
    /// contract read as elapsed, <c>ContractsModule.CheckDeadlines</c> retired it on the
    /// very next dispatched action, and <c>KspStatePatcher.PatchContracts</c> deleted it
    /// out of Mission Control through <c>Contract.Unregister()</c> — which fires neither
    /// the RED MessageSystem entry nor <c>GameEvents.Contract.onFailed</c>. Player-facing
    /// state loss with no in-game signal at all.</para>
    ///
    /// <para>This file covers the four halves of the fix: the CAPTURE (pinned against
    /// the same-guid stock CONTRACT node in the committed C2Career fixture), the on-disk
    /// MIGRATION (legacy duration rows re-based onto their accept UT, idempotently), the
    /// point-of-use SANITY GUARD, and the AGED-CAREER end-to-end shape that neither
    /// committed fixture is old enough to trip.</para>
    /// </summary>
    [Collection("Sequential")]
    public class ContractDeadlineAbsoluteUTTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>The PartTest contract carried by both halves of the C2Career fixture.</summary>
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
        // 1. The capture, pinned against stock's own save bytes
        // ================================================================

        /// <summary>
        /// The regression pin the defect entry asked for. Reads the SAME contract guid
        /// out of the C2Career fixture's <c>persistent.sfs</c> (KSP-authored ground
        /// truth) and out of that career's Parsek ledger, and asserts the three facts
        /// the entry proved by hand:
        ///
        /// <list type="number">
        /// <item>the pre-fix ledger row's raw on-disk <c>deadlineUT</c> IS stock's
        /// <c>TimeDeadline</c> (a duration), not its <c>dateDeadline</c>;</item>
        /// <item>the ledger row's own <c>ut</c> IS stock's <c>dateAccepted</c>, which is
        /// what makes the migration exact;</item>
        /// <item>after loading through the migration, <see cref="GameAction.DeadlineUT"/>
        /// equals stock's <c>dateDeadline</c>.</item>
        /// </list>
        ///
        /// <para>The third assertion carries a 0.5 s tolerance and that number is not
        /// slack — it is the float truncation ALREADY BAKED INTO THE FIXTURE. The row
        /// stores 8228571.5 for a stock TimeDeadline of 8228571.72775267, because the
        /// field was a <c>float</c>. The migration cannot recover digits the pre-fix
        /// write threw away; it recovers the value to within them. Rows written by the
        /// current <c>double</c> field lose nothing.</para>
        /// </summary>
        [Fact]
        public void C2CareerFixture_LegacyDurationRowMigratesToStockDateDeadline()
        {
            double[] values = ReadC2StockContractValues();
            double stockTimeDeadline = values[1];
            double stockDateAccepted = values[9];
            double stockDateDeadline = values[10];

            // Stock's own invariant, restated so a re-harvested fixture that broke it
            // would red HERE rather than silently weakening the cells below.
            Assert.Equal(stockDateAccepted + stockTimeDeadline, stockDateDeadline, 6);

            // (1) The raw on-disk value is the DURATION.
            ConfigNode rawRow = ReadC2LedgerContractAcceptNode();
            string rawLegacy = rawRow.GetValue(GameAction.ContractDeadlineLegacyDurationKey);
            Assert.NotNull(rawLegacy);
            Assert.Null(rawRow.GetValue(GameAction.ContractDeadlineAbsoluteKey));
            float rawLegacyValue = float.Parse(rawLegacy, NumberStyles.Float, IC);
            Assert.Equal((float)stockTimeDeadline, rawLegacyValue);

            // (2) The row's UT is stock's dateAccepted.
            double rawUT = double.Parse(rawRow.GetValue("ut"), NumberStyles.Float, IC);
            Assert.Equal(stockDateAccepted, rawUT, 6);

            // (3) Loaded through the migration, the row carries the ABSOLUTE deadline.
            GameAction migrated = LoadC2LedgerContractAccept();
            Assert.False(double.IsNaN(migrated.DeadlineUT));
            Assert.True(Math.Abs(migrated.DeadlineUT - stockDateDeadline) <= 0.5,
                $"migrated deadline {migrated.DeadlineUT.ToString("R", IC)} is not within the " +
                $"fixture's pre-existing float truncation of stock dateDeadline " +
                $"{stockDateDeadline.ToString("R", IC)}");

            // And it is emphatically NOT the duration any more — the whole defect.
            Assert.True(migrated.DeadlineUT > migrated.UT,
                "an absolute deadline must sit after the accept UT");
        }

        /// <summary>
        /// The per-load batch summary names what the migration did, per the logging
        /// convention (one line after the loop, never one per row).
        /// </summary>
        [Fact]
        public void C2CareerFixture_LoadEmitsMigrationBatchSummary()
        {
            LoadC2LedgerContractAccept();

            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") &&
                l.Contains("ContractAccept deadline resolution") &&
                l.Contains("migratedFromDuration=1") &&
                l.Contains("absolute=0"));
        }

        // ================================================================
        // 2. The on-disk migration, as a pure decision
        // ================================================================

        [Fact]
        public void Resolve_LegacyDurationKey_RebasesOntoAcceptUT()
        {
            var n = new ConfigNode("GAME_ACTION");
            n.AddValue(GameAction.ContractDeadlineLegacyDurationKey, "9201600");

            double deadline;
            var outcome = GameAction.ResolveContractDeadlineUT(n, 1000.0, out deadline);

            Assert.Equal(GameAction.ContractDeadlineResolution.MigratedFromDuration, outcome);
            Assert.Equal(9202600.0, deadline, 6);
        }

        [Fact]
        public void Resolve_AbsoluteKey_UsedVerbatim()
        {
            var n = new ConfigNode("GAME_ACTION");
            n.AddValue(GameAction.ContractDeadlineAbsoluteKey, "9202600");

            double deadline;
            var outcome = GameAction.ResolveContractDeadlineUT(n, 1000.0, out deadline);

            Assert.Equal(GameAction.ContractDeadlineResolution.Absolute, outcome);
            Assert.Equal(9202600.0, deadline, 6);
        }

        /// <summary>
        /// Absolute wins outright when both keys are present, so a hand-edited or
        /// half-written file can never be re-based twice.
        /// </summary>
        [Fact]
        public void Resolve_AbsoluteKeyWinsOverLegacyKey()
        {
            var n = new ConfigNode("GAME_ACTION");
            n.AddValue(GameAction.ContractDeadlineLegacyDurationKey, "9201600");
            n.AddValue(GameAction.ContractDeadlineAbsoluteKey, "9202600");

            double deadline;
            var outcome = GameAction.ResolveContractDeadlineUT(n, 1000.0, out deadline);

            Assert.Equal(GameAction.ContractDeadlineResolution.Absolute, outcome);
            Assert.Equal(9202600.0, deadline, 6);
        }

        [Fact]
        public void Resolve_NoDeadlineRow_StaysNaN()
        {
            var n = new ConfigNode("GAME_ACTION");
            n.AddValue("contractId", "no-deadline");

            double deadline;
            var outcome = GameAction.ResolveContractDeadlineUT(n, 1000.0, out deadline);

            Assert.Equal(GameAction.ContractDeadlineResolution.Absent, outcome);
            Assert.True(double.IsNaN(deadline));
        }

        /// <summary>
        /// A legacy row whose duration is zero or negative meant "no deadline" under the
        /// old capture's own convention (<c>deadline &gt; 0 ? value : "NaN"</c>).
        /// Re-basing it would MANUFACTURE a deadline that expires at the accept.
        /// </summary>
        [Theory]
        [InlineData("0")]
        [InlineData("-1")]
        [InlineData("NaN")]
        public void Resolve_LegacyNonPositiveOrNaNDuration_StaysNoDeadline(string raw)
        {
            var n = new ConfigNode("GAME_ACTION");
            n.AddValue(GameAction.ContractDeadlineLegacyDurationKey, raw);

            double deadline;
            var outcome = GameAction.ResolveContractDeadlineUT(n, 1000.0, out deadline);

            Assert.Equal(GameAction.ContractDeadlineResolution.Absent, outcome);
            Assert.True(double.IsNaN(deadline));
        }

        [Fact]
        public void Resolve_UnparseableValue_ReportedAndTreatedAsNoDeadline()
        {
            var n = new ConfigNode("GAME_ACTION");
            n.AddValue(GameAction.ContractDeadlineAbsoluteKey, "not-a-number");

            double deadline;
            var outcome = GameAction.ResolveContractDeadlineUT(n, 1000.0, out deadline);

            Assert.Equal(GameAction.ContractDeadlineResolution.Unparseable, outcome);
            Assert.True(double.IsNaN(deadline));
        }

        /// <summary>
        /// IDEMPOTENCE, end to end and by construction: a legacy row deserializes to the
        /// absolute value, re-serializes under the ABSOLUTE key only, and a second
        /// deserialize reports <see cref="GameAction.ContractDeadlineResolution.Absolute"/>
        /// with the value unchanged. The key NAME is the migration stamp, so no separate
        /// version flag can be forgotten or lost.
        /// </summary>
        [Fact]
        public void Migration_IsIdempotentAcrossASaveLoadCycle()
        {
            var legacy = new ConfigNode("GAME_ACTION");
            legacy.AddValue("ut", "8381.0195501716571");
            legacy.AddValue("type", ((int)GameActionType.ContractAccept).ToString(IC));
            legacy.AddValue("contractId", C2ContractGuid);
            legacy.AddValue("advanceFunds", "1871.8269");
            legacy.AddValue(GameAction.ContractDeadlineLegacyDurationKey, "8228571.5");

            GameAction first = GameAction.DeserializeFrom(legacy);
            double afterFirst = first.DeadlineUT;
            Assert.Equal(8381.0195501716571 + 8228571.5, afterFirst, 6);

            var reSerialized = new ConfigNode("LEDGER");
            first.SerializeInto(reSerialized);
            ConfigNode row = reSerialized.GetNodes("GAME_ACTION")[0];

            // Written ONLY under the absolute key.
            Assert.NotNull(row.GetValue(GameAction.ContractDeadlineAbsoluteKey));
            Assert.Null(row.GetValue(GameAction.ContractDeadlineLegacyDurationKey));

            double reread;
            var outcome = GameAction.ResolveContractDeadlineUT(row, first.UT, out reread);
            Assert.Equal(GameAction.ContractDeadlineResolution.Absolute, outcome);
            Assert.Equal(afterFirst, reread, 6);

            GameAction second = GameAction.DeserializeFrom(row);
            Assert.Equal(afterFirst, second.DeadlineUT, 6);
        }

        /// <summary>
        /// A no-deadline row survives a full save/load cycle untouched — the migration
        /// must not mangle it into a deadline of "accept UT + 0".
        /// </summary>
        [Fact]
        public void Migration_NoDeadlineRow_RoundTripsAsNaN()
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
            Assert.Null(row.GetValue(GameAction.ContractDeadlineLegacyDurationKey));
            Assert.True(double.IsNaN(GameAction.DeserializeFrom(row).DeadlineUT));
        }

        /// <summary>
        /// The field is a <c>double</c> now: a mid-career absolute UT round-trips without
        /// losing whole seconds. The pre-fix <c>float</c> could not — a float carries ~7
        /// significant digits, so this value would land 0.5 s off.
        /// </summary>
        [Fact]
        public void Migration_MidCareerAbsoluteUT_RoundTripsWithoutTruncation()
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
        // 3. The point-of-use sanity guard
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
        // 4. ComputeProjectionHorizon
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
        // 5. The aged career — the shape neither committed fixture can reach
        // ================================================================

        /// <summary>
        /// THE HEADLINE CELL, and it FAILS ON MAIN.
        ///
        /// <para>A synthetic ledger written in the LEGACY on-disk form, shaped straight
        /// off the C2Career numbers: the PartTest contract accepted at UT 8381.02 with
        /// <c>deadlineUT = 8228571.5</c> (stock's TimeDeadline), plus one later ordinary
        /// action at UT 8,230,000. That UT is the entire point — it sits ABOVE the
        /// deadline DURATION (8,228,571.5) and BELOW the absolute deadline
        /// (8,236,952.5). Neither committed fixture can be this shape: C2Career's clock
        /// stops ~957x short of its own deadline and C1Career ~4.2x short.</para>
        ///
        /// <para>Pre-fix, <c>DeadlineUT</c> loads as the duration, <c>CheckDeadlines</c>
        /// fires on the very next dispatched action, and the contract leaves
        /// <c>activeContracts</c> — which is exactly what makes
        /// <c>PatchContracts</c> Unregister it out of Mission Control. Post-fix the row
        /// migrates to the absolute deadline and the contract is still active.</para>
        /// </summary>
        [Fact]
        public void AgedCareer_LegacyLedger_ContractSurvivesPastItsDeadlineDuration()
        {
            const double acceptUT = 8381.0195501716571;
            const double storedDuration = 8228571.5;
            const double laterActionUT = 8_230_000.0;

            Assert.True(laterActionUT > storedDuration,
                "the fixture must be OLDER than the deadline duration or it proves nothing");
            Assert.True(laterActionUT < acceptUT + storedDuration,
                "the fixture must be YOUNGER than the real absolute deadline");

            string path = WriteLegacyLedgerFile(acceptUT, storedDuration, laterActionUT);
            try
            {
                Assert.True(Ledger.LoadFromFile(path));

                var module = new ContractsModule();
                module.Reset();
                foreach (var action in Ledger.Actions)
                    module.ProcessAction(action);

                Assert.Contains(C2ContractGuid, module.GetActiveContractIds());
                Assert.DoesNotContain(C2ContractGuid, module.GetTerminalContractIds());
                Assert.DoesNotContain(logLines, l => l.Contains("DeadlineExpired"));
            }
            finally
            {
                TryDeleteTempDir(path);
            }
        }

        /// <summary>
        /// The other half of the same statement, pinning the MECHANISM rather than the
        /// fix: feed the module the pre-fix value directly (a duration parked in
        /// <c>DeadlineUT</c>, plausible because it sits after this early accept) and the
        /// contract IS evicted by <c>CheckDeadlines</c> on the next action, with the
        /// <c>DeadlineExpired</c> log line the defect entry names as the cheapest live
        /// discriminator. The sanity guard deliberately does NOT save this case — the
        /// value is indistinguishable from a real deadline here, which is precisely why
        /// the capture and the migration are the fix and the guard is only a net.
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

        // ================================================================
        // Helpers
        // ================================================================

        private static string ResolveC2FixtureDir()
        {
            string root = SyntheticRecordingTests.ResolveProjectRoot();
            string dir = Path.Combine(root, "Source", "Parsek.Tests", "Fixtures", "C2Career");
            Assert.True(Directory.Exists(dir), $"C2Career fixture dir not found at '{dir}'");
            return dir;
        }

        /// <summary>
        /// Pulls the comma-joined <c>values</c> line off the C2Career save's CONTRACT
        /// node for <see cref="C2ContractGuid"/>. Field order (decompiled
        /// <c>Contract.Save</c>): TimeExpiry, TimeDeadline, FundsAdvance,
        /// FundsCompletion, FundsFailure, ScienceCompletion, ReputationCompletion,
        /// ReputationFailure, dateExpire, dateAccepted, dateDeadline, dateFinished.
        /// </summary>
        private static double[] ReadC2StockContractValues()
        {
            string path = Path.Combine(ResolveC2FixtureDir(), "persistent.sfs");
            ConfigNode root = ConfigNode.Load(path);
            Assert.NotNull(root);

            ConfigNode found = FindNodeWithValue(root, "guid", C2ContractGuid);
            Assert.True(found != null,
                $"no node carrying guid={C2ContractGuid} in the C2Career save");

            string raw = found.GetValue("values");
            Assert.False(string.IsNullOrEmpty(raw), "stock CONTRACT node has no values line");

            string[] parts = raw.Split(',');
            Assert.True(parts.Length >= 12, $"unexpected values arity {parts.Length}");

            var parsed = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                parsed[i] = double.Parse(parts[i], NumberStyles.Float, IC);
            return parsed;
        }

        private static ConfigNode FindNodeWithValue(ConfigNode node, string key, string value)
        {
            if (string.Equals(node.GetValue(key), value, StringComparison.Ordinal))
                return node;

            for (int i = 0; i < node.nodes.Count; i++)
            {
                ConfigNode hit = FindNodeWithValue(node.nodes[i], key, value);
                if (hit != null)
                    return hit;
            }
            return null;
        }

        private static ConfigNode ReadC2LedgerContractAcceptNode()
        {
            string path = Path.Combine(ResolveC2FixtureDir(), "Parsek", "GameState", "ledger.pgld");
            ConfigNode root = ConfigNode.Load(path);
            Assert.NotNull(root);

            ConfigNode[] rows = root.GetNodes("GAME_ACTION");
            for (int i = 0; i < rows.Length; i++)
            {
                if (string.Equals(rows[i].GetValue("contractId"), C2ContractGuid, StringComparison.Ordinal)
                    && rows[i].GetValue("type") == ((int)GameActionType.ContractAccept).ToString(IC))
                {
                    return rows[i];
                }
            }

            Assert.True(false, $"no ContractAccept row for {C2ContractGuid} in the C2Career ledger");
            return null;
        }

        private static GameAction LoadC2LedgerContractAccept()
        {
            string path = Path.Combine(ResolveC2FixtureDir(), "Parsek", "GameState", "ledger.pgld");
            Assert.True(Ledger.LoadFromFile(path), "Ledger.LoadFromFile failed on the C2Career fixture");

            foreach (var a in Ledger.Actions)
            {
                if (a.Type == GameActionType.ContractAccept
                    && string.Equals(a.ContractId, C2ContractGuid, StringComparison.Ordinal))
                {
                    return a;
                }
            }

            Assert.True(false, "loaded C2Career ledger has no ContractAccept for the pinned guid");
            return null;
        }

        /// <summary>
        /// Writes a ledger file in the LEGACY on-disk shape (duration under
        /// <c>deadlineUT</c>), which is what every pre-fix save carries.
        /// </summary>
        private static string WriteLegacyLedgerFile(
            double acceptUT, double durationSeconds, double laterActionUT)
        {
            string dir = Path.Combine(
                Path.GetTempPath(), "parsek-deadline-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "ledger.pgld");

            var text = new System.Text.StringBuilder();
            text.AppendLine("version = 0");
            text.AppendLine("recordingSchemaGeneration = "
                + RecordingStore.CurrentRecordingSchemaGeneration.ToString(IC));
            text.AppendLine("GAME_ACTION");
            text.AppendLine("{");
            text.AppendLine("\tut = " + acceptUT.ToString("R", IC));
            text.AppendLine("\ttype = " + ((int)GameActionType.ContractAccept).ToString(IC));
            text.AppendLine("\tactionId = act_legacy_deadline_fixture");
            text.AppendLine("\tseq = 1");
            text.AppendLine("\tcontractId = " + C2ContractGuid);
            text.AppendLine("\tcontractType = PartTest");
            text.AppendLine("\tcontractTitle = Aged career deadline fixture");
            text.AppendLine("\tadvanceFunds = 1871.8269");
            text.AppendLine("\t" + GameAction.ContractDeadlineLegacyDurationKey
                + " = " + durationSeconds.ToString("R", IC));
            text.AppendLine("\tfundsPenalty = 1946.69983");
            text.AppendLine("\trepPenalty = 1");
            text.AppendLine("}");
            text.AppendLine("GAME_ACTION");
            text.AppendLine("{");
            text.AppendLine("\tut = " + laterActionUT.ToString("R", IC));
            text.AppendLine("\ttype = " + ((int)GameActionType.FundsEarning).ToString(IC));
            text.AppendLine("\tactionId = act_legacy_deadline_later");
            text.AppendLine("\tseq = 2");
            text.AppendLine("\tfundsAwarded = 10");
            text.AppendLine("}");

            File.WriteAllText(path, text.ToString());
            return path;
        }

        private static void TryDeleteTempDir(string filePath)
        {
            try { Directory.Delete(Path.GetDirectoryName(filePath), true); }
            catch (Exception) { /* best effort */ }
        }
    }
}
