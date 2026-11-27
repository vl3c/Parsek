using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The start-route reload (design-part-ghost-parity.md E.4, "Start-route reload"): the pure ramp
    /// rules, stock's own module read by reflection (what a load keeps), the pass's place and its
    /// restore, and the atlas rows.
    /// </summary>
    public class PartParityStartReloadTests
    {
        // ----- the pure rules -----

        [Theory]
        [InlineData(true, true, 0.316f, true)]    // toroidalAerospike 12.6 s into its burn at 0.025 /s
        [InlineData(true, true, 1f, false)]       // arrived: a load shows the same
        [InlineData(true, true, 0.9995f, false)]  // arrived within the tolerance
        [InlineData(true, true, 0.998f, true)]
        [InlineData(true, false, 0f, false)]      // a cabin light that is off
        [InlineData(true, false, 0.4f, true)]     // cooling: a load jumps it to cold
        [InlineData(false, true, 0.3f, false)]    // no ramp at all
        public void OnlyARampALoadWouldMove_IsHeld(bool useRate, bool animState, float ramp, bool moved)
            => Assert.Equal(moved, PartParityRampHold.MovedByLoad(useRate, animState, ramp));

        [Fact]
        public void AnUnreadableRamp_IsNotHeld() => Assert.False(PartParityRampHold.MovedByLoad(true, true, float.NaN));

        [Theory]
        [InlineData(0.3f, true, 0.025f, 4.0, 0.4f)]
        [InlineData(0.3f, true, 0.025f, 0.0, 0.3f)]
        [InlineData(0.99f, true, 0.025f, 4.0, 1f)]
        [InlineData(0.3f, false, 0.05f, 2.0, 0.2f)]
        [InlineData(0.05f, false, 0.05f, 2.0, 0f)]
        [InlineData(0.3f, true, 0.025f, -5.0, 0.3f)] // a clock that ran back moves nothing
        public void ARestoredRamp_StandsWhereItWouldHave(float ramp, bool animState, float rate, double seconds, float expected)
            => Assert.Equal((double)expected, PartParityRampHold.RampAfter(ramp, animState, rate, seconds), 5);

        [Fact]
        public void TheLoadedRamp_IsStocksStart()
        {
            Assert.Equal(1f, PartParityRampHold.LoadedRamp(true));
            Assert.Equal(0f, PartParityRampHold.LoadedRamp(false));
            Assert.Equal("start-route-reloaded:2", PartParityRampHold.Flag(2));
        }

        // ----- stock's module -----

        [Fact]
        public void Stock_PersistsTheSwitch_AndNotTheRamp()
        {
            FieldInfo ramp = PartParityRampHold.RampField();
            Assert.NotNull(ramp);
            Assert.Empty(ramp.GetCustomAttributes(typeof(KSPField), true));
            FieldInfo sw = typeof(ModuleColorChanger).GetField("animState");
            Assert.True(((KSPField)sw.GetCustomAttributes(typeof(KSPField), true).Single()).isPersistant);
            FieldInfo level = typeof(FXModuleThrottleEffects).GetField("state");
            Assert.True(((KSPField)level.GetCustomAttributes(typeof(KSPField), true).Single()).isPersistant);
            // Every other persisted field a load would bring back differently would be a second
            // difference the pass does not make.
            Assert.Equal(new[] { "animState" }, PersistedFields(typeof(ModuleColorChanger)));
            Assert.Equal(new[] { "state" }, PersistedFields(typeof(FXModuleThrottleEffects)));
        }

        private static string[] PersistedFields(Type t)
            => t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(f => f.GetCustomAttributes(typeof(KSPField), true).Cast<KSPField>().Any(a => a.isPersistant))
                .Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        // ----- the pass -----

        private static string Read(string file)
            => PartParityStateRulesTests.StripComments(File.ReadAllText(Path.Combine(
                SyntheticRecordingTests.ResolveProjectRoot(), "Source", "Parsek", "TestCommands", file)));

        [Fact]
        public void ThePass_RunsAfterTheBodyViews_AndBeforeAnyLightOrFxRead()
        {
            string text = Read("PartParityFxDriver.Capture.cs");
            int capture = text.IndexOf("IEnumerator CaptureCase", StringComparison.Ordinal);
            string body = text.Substring(capture);
            int views = body.IndexOf("PartParityCaseCapture.CaptureView(ctx, wc, view)", StringComparison.Ordinal);
            int reload = body.IndexOf("CaptureStartReload(ctx, wc)", StringComparison.Ordinal);
            int lights = body.IndexOf("CompareLights(wc)", StringComparison.Ordinal);
            int framing = body.IndexOf("FinalFraming(wc)", StringComparison.Ordinal);
            int sample = body.IndexOf("SampleFrame(ctx, wc, i)", StringComparison.Ordinal);
            Assert.True(capture >= 0 && views >= 0 && views < reload && reload < lights && lights < framing && framing < sample,
                $"views={views} reload={reload} lights={lights} framing={framing} sample={sample}");
        }

        [Fact]
        public void ThePass_RestoresTheRampOnEveryPath_AndRunsAFrameAfter()
        {
            string text = Read("PartParityFxDriver.StartReload.cs");
            string pass = text.Substring(text.IndexOf("IEnumerator CaptureStartReload", StringComparison.Ordinal));
            pass = pass.Substring(0, pass.IndexOf("internal static bool StartReloadApplies", StringComparison.Ordinal));
            int load = pass.IndexOf("r.Load()", StringComparison.Ordinal);
            int tryAt = pass.LastIndexOf("try", load, StringComparison.Ordinal);
            int fin = pass.IndexOf("finally", load, StringComparison.Ordinal);
            int restore = pass.IndexOf("RestoreRamps(wc, ramps)", StringComparison.Ordinal);
            Assert.True(tryAt >= 0 && tryAt < load && load < fin && fin < restore, "the ramps must be loaded inside the try whose finally restores them");
            // The picture waits for the game's frame after the write, and the next reader for two
            // ends of frame after the restore.
            string shown = pass.Substring(load, pass.IndexOf("CaptureStartPair", StringComparison.Ordinal) - load);
            Assert.Contains("yield return new WaitForEndOfFrame()", shown);
            string after = pass.Substring(restore);
            Assert.Equal(2, CountOf(after, "yield return new WaitForEndOfFrame()"));
        }

        private static int CountOf(string text, string what)
        {
            int n = 0;
            for (int i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + 1, StringComparison.Ordinal)) n++;
            return n;
        }

        // ----- the files -----

        private static PartParitySweepRunner.WaveCase Case(int views)
        {
            var wc = new PartParitySweepRunner.WaveCase { CaseId = "x" };
            for (int i = 0; i < views; i++)
            {
                wc.CapturedViews.Add("v" + i);
                wc.RealTiles.Add(new byte[] { 1 });
                wc.GhostStartTiles.Add(new byte[] { 2 });
                wc.ViewRecords.Add(new PartParityViewRecord { View = "v" + i, Measures = new PartParityViewMeasures(), Start = new PartParityViewRecord() });
            }
            return wc;
        }

        [Fact]
        public void ACommittedPair_ReplacesTheStartBlockAndTile_AndNothingElse()
        {
            PartParitySweepRunner.WaveCase wc = Case(2);
            var pairs = new List<PartParityStartPair>();
            for (int i = 0; i < 2; i++)
                pairs.Add(new PartParityStartPair
                {
                    View = "v" + i, RealTile = new byte[] { 7 }, GhostTile = new byte[] { 8 },
                    Record = new PartParityViewRecord { Measures = new PartParityViewMeasures { ColorBlobPx = 10 + i } },
                });
            PartParityViewMeasures eventBefore = wc.ViewRecords[0].Measures;
            PartParityStateDriver.CommitStartPairs(wc, pairs);
            Assert.Equal(2, wc.RealStartTiles.Count);
            Assert.Equal(7, wc.RealStartTiles[1][0]);
            Assert.Equal(8, wc.GhostStartTiles[0][0]);
            Assert.Equal(11, wc.ViewRecords[1].Start.Measures.ColorBlobPx);
            Assert.Same(eventBefore, wc.ViewRecords[0].Measures);
            Assert.Equal(1, wc.RealTiles[0][0]);
        }

        [Fact]
        public void TheAtlasRows_CarryTheLoadedRealPartLast()
        {
            Assert.Equal(new[] { "real", "ghost", "ghost-start", "ghost-stowed", "real-start" }, PartParityJson.AtlasRowsFor(true, true));
            Assert.Equal(PartParityJson.StateAtlasRows, PartParityJson.AtlasRowsFor(true, false));
            Assert.Equal(PartParityJson.StowedAtlasRows, PartParityJson.AtlasRowsFor(false, true));
            PartParityJson.AtlasRowsFor(true, true)[4] = "x";
            Assert.Equal("real-start", PartParityJson.ReloadedStateAtlasRows[4]);
            Assert.Equal(PartParityJson.StateAtlasRows, PartParityJson.ReloadedStateAtlasRows.Take(4));
        }
    }
}
