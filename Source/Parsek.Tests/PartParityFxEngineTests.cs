using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>The pure decisions the live FX engine (design-part-ghost-parity.md Appendix E,
    /// piece F3) reads besides PartParityFxRules: the structure diff, the control evidence and
    /// its judge across steps, the FX wave kinds and admission, the compact accumulators.</summary>
    public class PartParityFxEngineTests
    {
        // ----- structure -----

        private static readonly bool[] Both = { true, true };

        private static (List<PartParityFxStructureEntry> missing, List<PartParityFxStructureEntry> extra, List<string> differs) Diff(
            string[] real, bool[] realRendered, string[] ghost, bool[] ghostRendered)
        {
            var m = new List<PartParityFxStructureEntry>();
            var e = new List<PartParityFxStructureEntry>();
            var d = new List<string>();
            PartParityFxStructure.Diff(real, realRendered, ghost, ghostRendered, m, e, d);
            return (m, e, d);
        }

        [Theory]
        [InlineData(0, 9, 9, 9, 0)]
        [InlineData(8, 9, 9, 9, 8)]   // RCSBlock.v2: RCSjet systems, RCSthruster nozzles
        [InlineData(9, 9, 10, 10, -1)] // no such nozzle
        [InlineData(-1, 9, 9, 9, -1)]
        [InlineData(2, 9, 10, 9, -1)]  // a match outside the model shifts F2's index
        public void AnRcsFxSystem_MapsToStocksNozzleByIndex(int index, int nozzles, int inPrefab, int underModel, int expected)
            => Assert.Equal(expected, PartParityFxDriver.NozzleOf(index, nozzles, inPrefab, underModel));

        [Fact]
        public void Structure_PairsByNameAndParent_AndNamesWhatDiffers()
        {
            const string a = "fx_flame<thrust pos=(0.00,0.00,0.00) dir=(0.0,1.0,0.0) scale=(1.00,1.00,1.00) size=1.00 speed=10.00";
            const string b = "fx_flame<thrust pos=(0.00,0.00,0.00) dir=(0.0,1.0,0.0) scale=(1.00,1.00,1.00) size=2.00 speed=10.00";
            var same = Diff(new[] { a, a }, Both, new[] { a, a }, Both);
            Assert.Empty(same.missing);
            Assert.Empty(same.extra);
            Assert.Empty(same.differs);
            var differs = Diff(new[] { a, a }, Both, new[] { b, a }, Both);
            Assert.Empty(differs.missing);
            Assert.Empty(differs.extra);
            Assert.Single(differs.differs);
            Assert.Contains("size=1.00", differs.differs[0]);
            Assert.Contains("size=2.00", differs.differs[0]);
            Assert.Equal("fx_flame<thrust", PartParityFxStructure.KeyOf(a));
        }

        [Fact]
        public void Structure_CountsCopies_AndCarriesWhetherTheAssetDraws()
        {
            const string flame = "fx_flame<thrust pos=(0.00,0.00,0.00) dir=(0.0,1.0,0.0) scale=(1.00,1.00,1.00) size=1.00 speed=10.00";
            const string smoke = "fx_smoke<thrust pos=(0.00,0.00,0.00) dir=(0.0,1.0,0.0) scale=(1.00,1.00,1.00) size=1.00 speed=10.00";
            var r = Diff(new[] { flame, flame, smoke }, new[] { true, true, false }, new[] { flame }, new[] { true });
            Assert.Equal(2, r.missing.Count);
            Assert.Equal("fx_flame<thrust", r.missing[0].Key);
            Assert.True(r.missing[0].Rendered);
            Assert.False(r.missing[1].Rendered); // the stock vacuum smoke, hidden on the real side
            Assert.Empty(r.extra);
            r = Diff(new[] { flame }, new[] { true }, new[] { flame, smoke }, new[] { true, true });
            Assert.Single(r.extra);
            Assert.Equal("fx_smoke<thrust", r.extra[0].Key);
            Assert.Equal("[{\"key\":\"fx_flame<thrust\",\"rendered\":false}]",
                PartParityFxStructure.Json(new[] { new PartParityFxStructureEntry { Key = "fx_flame<thrust" } }));
            Assert.Equal("fx_flame<thrust(hidden)", PartParityFxStructure.Join(new[] { new PartParityFxStructureEntry { Key = "fx_flame<thrust" } }));
            Assert.Equal("[]", PartParityFxStructure.Json(new List<PartParityFxStructureEntry>()));
        }

        private const string Jet = "restock-fx-rcs-1<RCSjet pos=(0.00,0.00,0.00) dir=(0.0,1.0,0.0) scale=(0.64,0.64,0.64) size=1.00 speed=5.00";
        private const string JetWeak = "restock-fx-rcs-1<RCSjet pos=(0.00,0.00,0.00) dir=(0.0,1.0,0.0) scale=(0.64,0.64,0.64) size=0.20 speed=1.00";

        /// <summary>RCSBlock.v2 on PP-9 <c>2026-11-24_0735</c>: 36 real systems, ten hidden ones of
        /// other variants, the eight drawn ones, eighteen hidden; eight on the ghost.</summary>
        private static (string[] real, bool[] drawn) VariantBlock(int hiddenBefore, int drawnCount, int hiddenAfter)
        {
            int n = hiddenBefore + drawnCount + hiddenAfter;
            var real = new string[n];
            var drawn = new bool[n];
            for (int i = 0; i < n; i++)
            {
                real[i] = Jet;
                drawn[i] = i >= hiddenBefore && i < hiddenBefore + drawnCount;
            }
            return (real, drawn);
        }

        [Fact]
        public void Structure_AHiddenVariantsSystem_NeverUsesUpADrawnOnesGhost()
        {
            (string[] real, bool[] drawn) = VariantBlock(18, 8, 10);
            var ghost = Enumerable.Repeat(JetWeak, 8).ToArray();
            var r = Diff(real, drawn, ghost, Enumerable.Repeat(true, 8).ToArray());
            Assert.Equal(28, r.missing.Count);
            Assert.DoesNotContain(r.missing, m => m.Rendered);
            Assert.Empty(r.extra);
            Assert.Equal(8, r.differs.Count);
            // The ghost's systems not playing yet (their prime): the drawn ones still pair first.
            r = Diff(real, drawn, ghost, new bool[8]);
            Assert.DoesNotContain(r.missing, m => m.Rendered);
        }

        [Theory]
        [InlineData(7, 1)]
        [InlineData(4, 4)]
        [InlineData(0, 8)]
        public void Structure_ADrawnSystemTheGhostLacks_StillReadsMissing(int ghostSystems, int expectedMissing)
        {
            (string[] real, bool[] drawn) = VariantBlock(18, 8, 10);
            var r = Diff(real, drawn, Enumerable.Repeat(JetWeak, ghostSystems).ToArray(), Enumerable.Repeat(true, ghostSystems).ToArray());
            Assert.Equal(expectedMissing, r.missing.Count(m => m.Rendered));
            Assert.Equal(36 - ghostSystems, r.missing.Count);
        }

        [Fact]
        public void Structure_AGhostWithTwoVariantsWorth_ReadsExtraOnTheDrawnOnes()
        {
            (string[] real, bool[] drawn) = VariantBlock(0, 8, 0);
            var r = Diff(real, drawn, Enumerable.Repeat(Jet, 10).ToArray(),
                Enumerable.Range(0, 10).Select(i => i != 3).ToArray());
            Assert.Equal(2, r.extra.Count);
            Assert.Single(r.extra, e => e.Rendered);   // nine drawn for eight: one too many
            Assert.Single(r.extra, e => !e.Rendered);  // the hidden one stays behind the drawn ones
        }

        // ----- control evidence and its judge -----

        private static PartParityCaseSidecar SidecarWith(PartParityFxControlEvidence e, string status = "ok")
        {
            var s = new PartParityCaseSidecar { Setup = new PartParitySetup { Status = status } };
            s.Flags.Add(PartParityFxControls.FormatEvidence(e));
            return s;
        }

        private static PartParityFxControlEvidence Full()
        {
            var e = new PartParityFxControlEvidence
            {
                RealCorePresent = true, EventsRecorded = 2, AllCoverageZero = false, GhostCoverageZero = true,
                RealCoverageNonZero = true, RealPlateLit = true, PlateViewsFail = true,
            };
            e.FailingMeasures.Add(PartParityFxRules.MeasureLength);
            e.FailingMeasures.Add(PartParityFxRules.MeasureOrigin);
            return e;
        }

        [Fact]
        public void Evidence_RoundTripsThroughTheSidecarFlag()
        {
            PartParityFxControlEvidence e = Full();
            string flag = PartParityFxControls.FormatEvidence(e);
            Assert.StartsWith(PartParityFxControls.EvidenceFlagPrefix, flag);
            PartParityFxControlEvidence r = PartParityFxControls.ParseEvidence(new[] { "seed-events:0", flag }, "state-timeout");
            Assert.Equal("state-timeout", r.SetupStatus);
            Assert.True(r.RealCorePresent);
            Assert.Equal(2, r.EventsRecorded);
            Assert.False(r.AllCoverageZero);
            Assert.True(r.GhostCoverageZero);
            Assert.True(r.RealCoverageNonZero);
            Assert.True(r.RealPlateLit);
            Assert.True(r.PlateViewsFail);
            Assert.Equal(new[] { PartParityFxRules.MeasureLength, PartParityFxRules.MeasureOrigin }, r.FailingMeasures.OrderBy(x => x));
            Assert.Null(PartParityFxControls.ParseEvidence(new[] { "seed-events:0" }, "ok"));
            Assert.Null(PartParityFxControls.ParseEvidence(null, "ok"));
            Assert.Null(PartParityFxControls.FormatEvidence(null));
        }

        [Fact]
        public void TheSameJudge_ReadsThisStepsAndEarlierStepsEvidence()
        {
            // The short plume: length fails, presence and colour pass.
            var e = new PartParityFxControlEvidence { RealCorePresent = true };
            e.FailingMeasures.Add(PartParityFxRules.MeasureLength);
            Assert.Equal("ok", PartParityFxControls.Judge(TestCommandPartParitySweep.ControlFxPlumeShort, SidecarWith(e), out _));
            e.FailingMeasures.Add(PartParityFxRules.MeasureColour);
            Assert.Equal("bad", PartParityFxControls.Judge(TestCommandPartParitySweep.ControlFxPlumeShort, SidecarWith(e), out string why));
            Assert.Contains("colour", why);
            // No evidence, no setup fault: bad, never ok.
            Assert.Equal("bad", PartParityFxControls.Judge(TestCommandPartParitySweep.ControlFxPlumeShort,
                new PartParityCaseSidecar { Setup = new PartParitySetup { Status = "ok" } }, out why));
            Assert.Equal("bad", PartParityFxControls.Judge(TestCommandPartParitySweep.ControlFxPlumeShort, null, out _));
            // A setup fault keeps its own reason even with no evidence.
            Assert.Equal("bad", PartParityFxControls.Judge(TestCommandPartParitySweep.ControlFxPlumeShort,
                new PartParityCaseSidecar { Setup = new PartParitySetup { Status = TestCommandPartParitySweep.SetupStateTimeout } }, out why));
            Assert.Contains("state-timeout", why);
            // An optional control with no subject is n/a.
            var na = new PartParityCaseSidecar { Setup = new PartParitySetup { Status = "ok" } };
            na.Flags.Add(PartParityFxControls.NotApplicableFlag);
            Assert.Equal("n/a", PartParityFxControls.Judge(TestCommandPartParitySweep.ControlPlateLightOffPoint, na, out _));
        }

        [Fact]
        public void TheHeatControl_IsJudgedOnItsViews_NotOnEvidence()
        {
            var s = new PartParityCaseSidecar { Setup = new PartParitySetup { Status = "ok" } };
            Assert.Equal("bad", PartParityFxControls.Judge(TestCommandPartParitySweep.ControlHeatEventsDropped, s, out string why));
            Assert.Contains("heat-hot", why);
            s.StateProof = new PartParityStateProof { Reached = true, Events = new List<PartParityStateEvent> { new PartParityStateEvent { Type = "ThermalAnimationHot" } } };
            s.Views.Add(new PartParityViewRecord { View = "side-a", Measures = new PartParityViewMeasures { ColorBlobPx = 5000 } });
            Assert.Equal("ok", PartParityFxControls.Judge(TestCommandPartParitySweep.ControlHeatEventsDropped, s, out _));
        }

        [Fact]
        public void TheDroppedEventControls_AreTheThreeThatDropThem()
        {
            Assert.True(PartParityFxDriver.DropsEvents(TestCommandPartParitySweep.ControlFxPlumeOff));
            Assert.True(PartParityFxDriver.DropsEvents(TestCommandPartParitySweep.ControlHeatEventsDropped));
            Assert.False(PartParityFxDriver.DropsEvents(TestCommandPartParitySweep.ControlFxPlumeShort));
            Assert.False(PartParityFxDriver.DropsEvents(null));
        }

        // ----- wave kinds and admission -----

        private static PartParityCaseKey FxKey(string family, bool responseTime = false)
            => new PartParityCaseKey
            {
                Part = "p", State = "s", FxOption = new PartParityFxOption { Family = family, UseEngineResponseTime = responseTime },
            };

        [Fact]
        public void FxWaves_AreGroupedByBucket_AndNeverMixWithOthers()
        {
            Assert.Equal("fx:Burn", PartParityStateWave.WaveKind(FxKey("plume")));
            Assert.Equal("fx:Spool", PartParityStateWave.WaveKind(FxKey("plume", responseTime: true)));
            Assert.Equal("fx:Rcs", PartParityStateWave.WaveKind(FxKey("rcs")));
            PartParityCaseKey main = FxKey("plume", responseTime: true);
            main.FxOption.MainThrottle = true;
            Assert.Equal("fx:Spool:main", PartParityStateWave.WaveKind(main));
            Assert.True(PartParityStateWave.IsFxKind("fx:Spool:main"));
            Assert.Equal("fx:Heat", PartParityStateWave.WaveKind(FxKey("heat")));
            Assert.Equal("fx:Plate", PartParityStateWave.WaveKind(FxKey("light")));
            Assert.True(PartParityStateWave.IsFxKind("fx:Burn"));
            Assert.False(PartParityStateWave.IsFxKind(PartParityStateWave.KindStowed));
            Assert.False(PartParityStateWave.IsFxKind("state:Long"));
            Assert.False(PartParityStateWave.IsFxKind(null));
        }

        [Fact]
        public void AnFxWave_IsAdmittedAgainstItsOwnWorstCase()
        {
            var burn = new List<PartParityFxOption>
            {
                new PartParityFxOption { Family = "plume" }, new PartParityFxOption { Family = "plume" },
            };
            double worst = PartParityFxRules.WaveWorstCaseSeconds(burn);
            Assert.True(PartParityStateWave.AdmitWave("fx:Burn", 10.0, 480, 0.0, 2, 0.0, null, false, burn));
            // Admitted only when the elapsed time plus the wave's own worst case fits the box.
            Assert.False(PartParityStateWave.AdmitWave("fx:Burn", 480.0 - worst + 1.0, 480, 0.0, 2, 0.0, null, false, burn));
            Assert.True(PartParityStateWave.AdmitWave("fx:Burn", 480.0 - worst - 1.0, 480, 0.0, 2, 0.0, null, false, burn));
            // The first wave of a step always starts, and an unknown list reads as the burn bound.
            Assert.True(PartParityStateWave.AdmitWave("fx:Burn", 470.0, 480, 0.0, 2, 0.0, null, true, burn));
        }

        [Fact]
        public void ACrashedFxCase_StillNamesItsFamilyAndState()
        {
            PartParityStateProof p = PartParityStateWave.CrashedFxStateProof(
                new PartParityFxOption { Family = "plume", Module = "ModuleEnginesFX", ModuleIndex = 3, State = "burn-full" }, "burn-full");
            Assert.Equal("plume", p.Family);
            Assert.Equal(3, p.ModuleIndex);
            Assert.Equal("burn-full", p.State);
            Assert.False(p.Reached);
            Assert.Null(PartParityStateWave.CrashedFxStateProof(null));
        }

        // ----- the plateau window -----

        [Fact]
        public void ThePlateauWindow_IsTheNewestThreeTicks()
        {
            var signal = new List<KeyValuePair<float, double>>
            {
                new KeyValuePair<float, double>(0f, 900.0), new KeyValuePair<float, double>(0.27f, 100.0),
            };
            Assert.False(PartParityFxDriver.PlateauWindow(signal, out _, out _));
            signal.Add(new KeyValuePair<float, double>(0.55f, 102.0));
            Assert.True(PartParityFxDriver.PlateauWindow(signal, out double min, out double max));
            Assert.Equal(100.0, min);
            Assert.Equal(900.0, max);
            // The early spike leaves the window as ticks pass; the settled check is the rule's.
            signal.Add(new KeyValuePair<float, double>(0.82f, 101.0));
            Assert.True(PartParityFxDriver.PlateauWindow(signal, out min, out max));
            Assert.Equal(100.0, min);
            Assert.Equal(102.0, max);
            Assert.True(PartParityFxRules.PlateauSettled(8.0, 7.5, min, max));
        }

        // ----- accumulators -----

        [Fact]
        public void Accumulators_MergeHalvesIntoTheWholeSet()
        {
            var even = new PartParityFxAccumulator(PartParityFxRules.LayerFlame, 1, 1);
            var odd = new PartParityFxAccumulator(PartParityFxRules.LayerFlame, 1, 1);
            even.Add(new byte[] { 200, 0, 0, 0 });
            even.Add(new byte[] { 100, 0, 0, 0 });
            odd.Add(new byte[] { 60, 40, 20, 0 });
            even.Merge(odd);
            Assert.Equal(3, even.Samples);
            Assert.Equal(3, even.CoveredAt(0));
            byte[] tile = even.ToTile();
            Assert.Equal(120, tile[0]); // (200 + 100 + 60) / 3
            Assert.Equal(13, tile[1]);
            Assert.Equal(255, tile[3]);
            Assert.Throws<System.ArgumentException>(() => even.Merge(new PartParityFxAccumulator(PartParityFxRules.LayerSmoke, 1, 1)));
        }

        [Fact]
        public void TheWholeSet_EqualsItsHalves_AfterEverySample()
        {
            var set = new PartParityFxSampleSet(PartParityFxRules.LayerFlame, 2, 1);
            for (int i = 0; i < 5; i++)
            {
                set.Add(new byte[] { (byte)(50 + i), 0, 0, 0, 0, 0, 0, 0 });
                Assert.Equal(i + 1, set.All.Samples);
                Assert.Equal((i + 2) / 2, set.Even.Samples);
                Assert.Equal((i + 1) / 2, set.Odd.Samples);
                Assert.Equal(i + 1, set.All.CoveredAt(0));
                Assert.Equal(0, set.All.CoveredAt(1));
            }
            // 254 channel sums of a 255 sample fit the compact accumulator (255 * 255 = 65025).
            var acc = new PartParityFxAccumulator(PartParityFxRules.LayerSmoke, 1, 1);
            for (int i = 0; i < PartParityFxRules.MaxSamples; i++) acc.Add(new byte[] { 255, 255, 255, 255 });
            Assert.Equal(new byte[] { 255, 255, 255, 255 }, acc.ToTile());
        }
    }
}
