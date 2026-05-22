using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class AutoLoopTests : System.IDisposable
    {
        public AutoLoopTests()
        {
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            // Per-recording clamp-warning dedupe set is static — flush between tests so
            // clamp assertions don't get swallowed by a prior test's warning on "TestVessel".
            GhostPlaybackLogic.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
            GhostPlaybackLogic.ResetForTesting();
        }

        static Recording MakeRec(
            double startUT = 100, double endUT = 200,
            double loopInterval = 10.0,
            LoopTimeUnit unit = LoopTimeUnit.Sec)
        {
            var rec = new Recording
            {
                VesselName = "TestVessel",
                PlaybackEnabled = true,
                LoopPlayback = true,
                LoopIntervalSeconds = loopInterval,
                LoopTimeUnit = unit,
            };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = startUT, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = endUT, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
            });
            return rec;
        }

        // --- Serialization round-trip ---

        [Fact]
        public void LoopTimeUnit_SaveLoad_RoundTrip_Auto()
        {
            var source = new Recording
            {
                RecordingId = "auto-test",
                LoopPlayback = true,
                LoopIntervalSeconds = 30.0,
                LoopTimeUnit = LoopTimeUnit.Auto,
            };
            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadataForTests(node, loaded);

            Assert.Equal(LoopTimeUnit.Auto, loaded.LoopTimeUnit);
        }

        [Fact]
        public void LoopTimeUnit_SaveLoad_RoundTrip_Hour()
        {
            var source = new Recording
            {
                RecordingId = "hr-test",
                LoopTimeUnit = LoopTimeUnit.Hour,
            };
            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadataForTests(node, loaded);

            Assert.Equal(LoopTimeUnit.Hour, loaded.LoopTimeUnit);
        }

        [Fact]
        public void LoopTimeUnit_Load_MissingKey_DefaultsSec()
        {
            var node = new ConfigNode("RECORDING");
            // No loopTimeUnit key at all
            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadataForTests(node, loaded);

            Assert.Equal(LoopTimeUnit.Sec, loaded.LoopTimeUnit);
        }

        [Fact]
        public void LoopTimeUnit_Save_DefaultSec_NotWritten()
        {
            var source = new Recording
            {
                RecordingId = "sec-default",
                LoopTimeUnit = LoopTimeUnit.Sec,
            };
            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            Assert.Null(node.GetValue("loopTimeUnit"));
        }

        // --- ResolveLoopInterval ---

        [Fact]
        public void ResolveLoopInterval_AutoMode_ReturnsGlobalValue()
        {
            var rec = MakeRec(unit: LoopTimeUnit.Auto, loopInterval: 999);
            double result = GhostPlaybackLogic.ResolveLoopInterval(rec, 42.0, 10.0, 1.0);
            Assert.Equal(42.0, result);
        }

        [Fact]
        public void ResolveLoopInterval_AutoMode_NegativeGlobal_ClampsToMin()
        {
            // #381: Auto-mode historically clamped negative globals to 0. Under the new
            // semantics a negative global is an illegal period, so we clamp all the way to
            // MinCycleDuration (1s) and emit a Warn.
            var rec = MakeRec(unit: LoopTimeUnit.Auto);
            double result = GhostPlaybackLogic.ResolveLoopInterval(rec, -5.0, 10.0, 1.0);
            Assert.Equal(1.0, result);
        }

        [Fact]
        public void ResolveLoopInterval_AutoMode_NaN_ReturnsDefault()
        {
            var rec = MakeRec(unit: LoopTimeUnit.Auto);
            double result = GhostPlaybackLogic.ResolveLoopInterval(rec, double.NaN, 10.0, 1.0);
            Assert.Equal(10.0, result);
        }

        [Fact]
        public void ResolveLoopInterval_ManualMode_ReturnsRecordingValue()
        {
            var rec = MakeRec(unit: LoopTimeUnit.Sec, loopInterval: 25.0);
            double result = GhostPlaybackLogic.ResolveLoopInterval(rec, 42.0, 10.0, 1.0);
            Assert.Equal(25.0, result);
        }

        [Fact]
        public void ResolveLoopInterval_ManualMode_NegativeClampsToMin()
        {
            // #381: negative intervals no longer preserve; they clamp to MinCycleDuration
            // and emit a Warn log on the "Loop" category.
            var captured = new System.Collections.Generic.List<string>();
            ParsekLog.TestSinkForTesting = captured.Add;
            try
            {
                var rec = MakeRec(unit: LoopTimeUnit.Min, loopInterval: -30.0);
                double result = GhostPlaybackLogic.ResolveLoopInterval(rec, 42.0, 10.0, 1.0);
                Assert.Equal(1.0, result);
                Assert.Contains(captured, line => line.Contains("ResolveLoopInterval") && line.Contains("MinCycleDuration"));
            }
            finally
            {
                ParsekLog.TestSinkForTesting = null;
            }
        }

        [Fact]
        public void ResolveLoopInterval_ManualMode_PeriodShorterThanDuration_Preserved()
        {
            // #381: period can be less than duration (overlap) — not clamped against duration.
            var rec = MakeRec(startUT: 100, endUT: 200, loopInterval: 30.0, unit: LoopTimeUnit.Sec);
            double result = GhostPlaybackLogic.ResolveLoopInterval(rec, 42.0, 10.0, 1.0);
            Assert.Equal(30.0, result);
        }

        [Fact]
        public void ResolveLoopInterval_ManualMode_BelowMin_Clamps()
        {
            var rec = MakeRec(unit: LoopTimeUnit.Sec, loopInterval: 0.1);
            double result = GhostPlaybackLogic.ResolveLoopInterval(rec, 42.0, 10.0, 1.0);
            Assert.Equal(1.0, result);
        }

        [Fact]
        public void ResolveLoopInterval_ManualMode_Zero_Clamps()
        {
            var rec = MakeRec(unit: LoopTimeUnit.Sec, loopInterval: 0.0);
            double result = GhostPlaybackLogic.ResolveLoopInterval(rec, 42.0, 10.0, 1.0);
            Assert.Equal(1.0, result);
        }

        [Fact]
        public void ResolveLoopInterval_NullRec_ReturnsDefault()
        {
            double result = GhostPlaybackLogic.ResolveLoopInterval(null, 42.0, 10.0, 1.0);
            Assert.Equal(10.0, result);
        }

        [Fact]
        public void ShouldUseGlobalAutoLaunchQueue_RequiresEnabledAutoLoopingTrajectory()
        {
            var eligible = MakeRec(startUT: 100, endUT: 150, unit: LoopTimeUnit.Auto);
            eligible.RecordingId = "eligible";

            var manual = MakeRec(startUT: 100, endUT: 150, unit: LoopTimeUnit.Sec);
            manual.RecordingId = "manual";

            var disabled = MakeRec(startUT: 100, endUT: 150, unit: LoopTimeUnit.Auto);
            disabled.RecordingId = "disabled";
            disabled.PlaybackEnabled = false;

            var tooShort = MakeRec(startUT: 100, endUT: 100.5, unit: LoopTimeUnit.Auto);
            tooShort.RecordingId = "too-short";

            Assert.True(GhostPlaybackLogic.ShouldUseGlobalAutoLaunchQueue(eligible));
            Assert.False(GhostPlaybackLogic.ShouldUseGlobalAutoLaunchQueue(manual));
            Assert.False(GhostPlaybackLogic.ShouldUseGlobalAutoLaunchQueue(disabled));
            Assert.False(GhostPlaybackLogic.ShouldUseGlobalAutoLaunchQueue(tooShort));
        }

        [Fact]
        public void TryResolveAutoLoopLaunchSchedule_OrdersQueueByEffectiveStartAndUsesGlobalCadence()
        {
            var third = MakeRec(startUT: 120, endUT: 170, loopInterval: 999, unit: LoopTimeUnit.Auto);
            third.RecordingId = "third";
            third.VesselName = "Third";

            var disabled = MakeRec(startUT: 90, endUT: 140, loopInterval: 999, unit: LoopTimeUnit.Auto);
            disabled.RecordingId = "disabled";
            disabled.VesselName = "Disabled";
            disabled.PlaybackEnabled = false;

            var first = MakeRec(startUT: 100, endUT: 150, loopInterval: 999, unit: LoopTimeUnit.Auto);
            first.RecordingId = "first";
            first.VesselName = "First";

            var manual = MakeRec(startUT: 95, endUT: 145, loopInterval: 45, unit: LoopTimeUnit.Sec);
            manual.RecordingId = "manual";
            manual.VesselName = "Manual";

            var second = MakeRec(startUT: 110, endUT: 160, loopInterval: 999, unit: LoopTimeUnit.Auto);
            second.RecordingId = "second";
            second.VesselName = "Second";

            var trajectories = new List<IPlaybackTrajectory> { third, disabled, first, manual, second };

            Assert.True(GhostPlaybackLogic.TryResolveAutoLoopLaunchSchedule(
                trajectories, 2, 30.0, out var firstSchedule));
            Assert.Equal(100.0, firstSchedule.LaunchStartUT, 6);
            Assert.Equal(90.0, firstSchedule.LaunchCadenceSeconds, 6);
            Assert.Equal(0, firstSchedule.SlotIndex);
            Assert.Equal(3, firstSchedule.QueueCount);

            Assert.True(GhostPlaybackLogic.TryResolveAutoLoopLaunchSchedule(
                trajectories, 4, 30.0, out var secondSchedule));
            Assert.Equal(130.0, secondSchedule.LaunchStartUT, 6);
            Assert.Equal(90.0, secondSchedule.LaunchCadenceSeconds, 6);
            Assert.Equal(1, secondSchedule.SlotIndex);
            Assert.Equal(3, secondSchedule.QueueCount);

            Assert.True(GhostPlaybackLogic.TryResolveAutoLoopLaunchSchedule(
                trajectories, 0, 30.0, out var thirdSchedule));
            Assert.Equal(160.0, thirdSchedule.LaunchStartUT, 6);
            Assert.Equal(90.0, thirdSchedule.LaunchCadenceSeconds, 6);
            Assert.Equal(2, thirdSchedule.SlotIndex);
            Assert.Equal(3, thirdSchedule.QueueCount);

            Assert.False(GhostPlaybackLogic.TryResolveAutoLoopLaunchSchedule(
                trajectories, 3, 30.0, out _));
        }

        // --- Unit helpers ---

        [Theory]
        [InlineData(LoopTimeUnit.Sec, "sec")]
        [InlineData(LoopTimeUnit.Min, "min")]
        [InlineData(LoopTimeUnit.Hour, "hr")]
        [InlineData(LoopTimeUnit.Auto, "auto")]
        public void UnitLabel_AllValues(LoopTimeUnit unit, string expected)
        {
            Assert.Equal(expected, ParsekUI.UnitLabel(unit));
        }

        [Theory]
        [InlineData(LoopTimeUnit.Sec, "s")]
        [InlineData(LoopTimeUnit.Min, "m")]
        [InlineData(LoopTimeUnit.Hour, "h")]
        [InlineData(LoopTimeUnit.Auto, "s")] // Auto falls through to "s" — callers resolve to concrete unit first
        public void UnitSuffix_AllValues(LoopTimeUnit unit, string expected)
        {
            Assert.Equal(expected, ParsekUI.UnitSuffix(unit));
        }

        [Fact]
        public void CycleRecordingUnit_FullCycle()
        {
            var u = LoopTimeUnit.Sec;
            u = ParsekUI.CycleRecordingUnit(u); Assert.Equal(LoopTimeUnit.Min, u);
            u = ParsekUI.CycleRecordingUnit(u); Assert.Equal(LoopTimeUnit.Hour, u);
            u = ParsekUI.CycleRecordingUnit(u); Assert.Equal(LoopTimeUnit.Auto, u);
            u = ParsekUI.CycleRecordingUnit(u); Assert.Equal(LoopTimeUnit.Sec, u);
        }

        [Fact]
        public void CycleDisplayUnit_FullCycle_NoAuto()
        {
            var u = LoopTimeUnit.Sec;
            u = ParsekUI.CycleDisplayUnit(u); Assert.Equal(LoopTimeUnit.Min, u);
            u = ParsekUI.CycleDisplayUnit(u); Assert.Equal(LoopTimeUnit.Hour, u);
            u = ParsekUI.CycleDisplayUnit(u); Assert.Equal(LoopTimeUnit.Sec, u);
        }

        [Theory]
        [InlineData(3600.0, LoopTimeUnit.Sec, 3600.0)]
        [InlineData(3600.0, LoopTimeUnit.Min, 60.0)]
        [InlineData(3600.0, LoopTimeUnit.Hour, 1.0)]
        [InlineData(120.0, LoopTimeUnit.Min, 2.0)]
        public void ConvertFromSeconds_AllUnits(double seconds, LoopTimeUnit unit, double expected)
        {
            Assert.Equal(expected, ParsekUI.ConvertFromSeconds(seconds, unit), 6);
        }

        [Theory]
        [InlineData(10.0, LoopTimeUnit.Sec, 10.0)]
        [InlineData(2.0, LoopTimeUnit.Min, 120.0)]
        [InlineData(1.0, LoopTimeUnit.Hour, 3600.0)]
        public void ConvertToSeconds_AllUnits(double value, LoopTimeUnit unit, double expected)
        {
            Assert.Equal(expected, ParsekUI.ConvertToSeconds(value, unit), 6);
        }

        // --- ApplyPersistenceArtifactsFrom ---

        [Fact]
        public void ApplyPersistenceArtifactsFrom_CopiesLoopTimeUnit()
        {
            var source = new Recording
            {
                LoopTimeUnit = LoopTimeUnit.Auto,
            };
            var target = new Recording();
            target.ApplyPersistenceArtifactsFrom(source);

            Assert.Equal(LoopTimeUnit.Auto, target.LoopTimeUnit);
        }

        // --- FormatLoopValue ---

        [Theory]
        [InlineData(0.0, LoopTimeUnit.Sec, "0")]
        [InlineData(5.5, LoopTimeUnit.Sec, "5")]
        [InlineData(10.0, LoopTimeUnit.Sec, "10")]
        [InlineData(1.5, LoopTimeUnit.Min, "1.5")]
        [InlineData(2.0, LoopTimeUnit.Min, "2.0")]
        [InlineData(0.5, LoopTimeUnit.Hour, "0.5")]
        [InlineData(1.0, LoopTimeUnit.Hour, "1.0")]
        public void FormatLoopValue_Formatting(double value, LoopTimeUnit unit, string expected)
        {
            Assert.Equal(expected, ParsekUI.FormatLoopValue(value, unit));
        }

        // --- TryParseLoopInput ---

        [Theory]
        [InlineData("10", LoopTimeUnit.Sec, true, 10.0)]
        [InlineData("1.5", LoopTimeUnit.Sec, false, 0.0)]  // float rejected for sec
        [InlineData("1.5", LoopTimeUnit.Min, true, 1.5)]
        [InlineData("0.5", LoopTimeUnit.Hour, true, 0.5)]
        [InlineData("abc", LoopTimeUnit.Sec, false, 0.0)]
        [InlineData("abc", LoopTimeUnit.Min, false, 0.0)]
        [InlineData("", LoopTimeUnit.Sec, false, 0.0)]    // empty string
        [InlineData("", LoopTimeUnit.Min, false, 0.0)]    // empty string
        [InlineData("-5", LoopTimeUnit.Sec, true, -5.0)]  // negative allowed in recordings
        [InlineData("-1.5", LoopTimeUnit.Min, true, -1.5)]
        public void TryParseLoopInput_UnitRules(string text, LoopTimeUnit unit, bool expectedOk, double expectedVal)
        {
            double val;
            bool ok = ParsekUI.TryParseLoopInput(text, unit, out val);
            Assert.Equal(expectedOk, ok);
            if (expectedOk) Assert.Equal(expectedVal, val, 6);
        }

        // --- ResolveLoopInterval log assertions ---

        [Fact]
        public void ResolveLoopInterval_AutoMode_Independent_Of_Duration()
        {
            // #381: auto-mode returns globalAutoInterval (clamped to MinCycleDuration)
            // regardless of recording duration. 5s, 50s and 5000s recordings all get 10s.
            var shortRec = MakeRec(startUT: 100, endUT: 105, unit: LoopTimeUnit.Auto);
            var midRec   = MakeRec(startUT: 100, endUT: 150, unit: LoopTimeUnit.Auto);
            var longRec  = MakeRec(startUT: 100, endUT: 5100, unit: LoopTimeUnit.Auto);
            Assert.Equal(10.0, GhostPlaybackLogic.ResolveLoopInterval(shortRec, 10.0, 10.0, 1.0));
            Assert.Equal(10.0, GhostPlaybackLogic.ResolveLoopInterval(midRec,   10.0, 10.0, 1.0));
            Assert.Equal(10.0, GhostPlaybackLogic.ResolveLoopInterval(longRec,  10.0, 10.0, 1.0));
        }

        [Fact]
        public void ResolveLoopInterval_AutoMode_Infinity_ReturnsDefault()
        {
            var rec = MakeRec(unit: LoopTimeUnit.Auto);
            double result = GhostPlaybackLogic.ResolveLoopInterval(rec, double.PositiveInfinity, 10.0, 1.0);
            Assert.Equal(10.0, result);
        }

        [Fact]
        public void ResolveLoopInterval_ManualMode_NaN_ReturnsDefault()
        {
            var rec = MakeRec(unit: LoopTimeUnit.Sec);
            rec.LoopIntervalSeconds = double.NaN;
            double result = GhostPlaybackLogic.ResolveLoopInterval(rec, 42.0, 10.0, 1.0);
            Assert.Equal(10.0, result);
        }

        [Fact]
        public void ResolveLoopInterval_ManualMode_Infinity_ReturnsDefault()
        {
            var rec = MakeRec(unit: LoopTimeUnit.Min);
            rec.LoopIntervalSeconds = double.PositiveInfinity;
            double result = GhostPlaybackLogic.ResolveLoopInterval(rec, 42.0, 10.0, 1.0);
            Assert.Equal(10.0, result);
        }

        // --- Chain-loop unit serialization no-leak (Phase 8, design 508-513) ---

        /// <summary>
        /// Builds a chain-member recording carrying the loop / period / chain fields a chain-loop
        /// unit is detected from. Window [startUT, endUT] comes from the first / last point.
        /// </summary>
        static Recording MakeChainMember(
            string recordingId, string chainId, int chainIndex,
            double startUT, double endUT,
            bool loop = true, LoopTimeUnit unit = LoopTimeUnit.Auto, int branch = 0)
        {
            var rec = new Recording
            {
                RecordingId = recordingId,
                VesselName = $"{chainId}-{chainIndex}",
                PlaybackEnabled = true,
                LoopPlayback = loop,
                LoopTimeUnit = unit,
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = branch,
            };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = startUT, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero,
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = endUT, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero,
            });
            return rec;
        }

        [Fact]
        public void ChainLoopUnit_RoundTrip_NoUnitStatePersisted()
        {
            // Phase 8 (design 508-513): chain-loop units are RUNTIME-only, derived each schedule
            // rebuild by RecordingStore.DetectChainLoopUnits, never written to the save. This test
            // round-trips a 3-member auto-loop chain through ParsekScenario metadata save/load and
            // asserts:
            //   (1) NO unit-membership / span / cadence key leaks into the serialized ConfigNode.
            //       WHAT MAKES IT FAIL: if a future change started persisting any unit descriptor
            //       field (spanStart/spanEnd/cadence/owner/member list/"chainLoopUnit"/"loopUnit"),
            //       the vocabulary scan below would catch the new key. A leaked unit key is a schema
            //       change the design explicitly forbids (no generation bump, no on-disk unit).
            //   (2) the unit RECONSTITUTES purely from the loaded loop/period/chain state on the
            //       next detection pass, proving the round-tripped save carries everything the
            //       runtime detector needs and nothing it doesn't.
            RecordingStore.ResetForTesting();

            var sources = new[]
            {
                MakeChainMember("clu-0", "cluChain", 0, 100, 150),
                MakeChainMember("clu-1", "cluChain", 1, 150, 200),
                MakeChainMember("clu-2", "cluChain", 2, 200, 250),
            };

            // The unit vocabulary that must NEVER appear as a serialized key. These are the field
            // names of the transient LoopUnit / LoopUnitSet descriptors plus the design's terms.
            string[] forbiddenKeyFragments =
            {
                "loopunit", "chainloopunit", "spanstart", "spanend", "spanut",
                "cadence", "unitowner", "unitmember", "ownerbyindex", "memberindices",
            };

            var loaded = new List<Recording>();
            foreach (var source in sources)
            {
                var node = new ConfigNode("RECORDING");
                ParsekScenario.SaveRecordingMetadata(node, source);

                // (1) No unit state in any serialized key (case-insensitive substring scan).
                for (int v = 0; v < node.values.Count; v++)
                {
                    string key = node.values[v].name.ToLowerInvariant();
                    foreach (var forbidden in forbiddenKeyFragments)
                    {
                        Assert.False(key.Contains(forbidden),
                            $"serialized recording metadata leaked chain-loop unit state via key '" +
                            $"{node.values[v].name}' (matched forbidden fragment '{forbidden}'); units are runtime-only");
                    }
                }

                // Load the metadata back into a fresh recording (loop / period / points round-trip).
                var rec = new Recording();
                ParsekScenario.LoadRecordingMetadataForTests(node, rec);
                // Metadata save/load round-trips loop+period but NOT the chain topology (that lives on
                // the RecordingTree). Re-apply the chain fields exactly as the tree load does, plus the
                // points the window derives from, so the reconstituted recording matches a real load.
                rec.ChainId = source.ChainId;
                rec.ChainIndex = source.ChainIndex;
                rec.ChainBranch = source.ChainBranch;
                rec.Points.Clear();
                rec.Points.AddRange(source.Points);
                loaded.Add(rec);
                RecordingStore.CommitRecordingDirect(rec);
            }

            // Sanity: the saved metadata DID carry the loop + period the detector keys off.
            Assert.True(loaded[0].LoopPlayback, "loaded member should round-trip LoopPlayback=true");
            Assert.Equal(LoopTimeUnit.Auto, loaded[0].LoopTimeUnit);

            // (2) The unit reconstitutes from the loaded state alone: same owner / members / span
            // a fresh recording set would produce. If any loop/period field failed to round-trip,
            // the run would break and no unit would form (this assertion would fail).
            var set = RecordingStore.DetectChainLoopUnits(RecordingStore.CommittedRecordings);
            Assert.Equal(1, set.Count);
            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(0, unit.OwnerIndex);
            Assert.Equal(new[] { 0, 1, 2 }, unit.MemberIndices);
            Assert.Equal(100.0, unit.SpanStartUT, 6);
            Assert.Equal(250.0, unit.SpanEndUT, 6);
        }
    }
}
