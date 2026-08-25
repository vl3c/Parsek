using System.Collections.Generic;
using Parsek;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure decision cells for the MissionConfig seam verb (the arrival-
    /// validation lane's promotion): strict loop-arg parsing (the SetSetting
    /// convention) and the optional positive interval whose absence is the
    /// leave-alone sentinel. The Unity applier is one thin call into the
    /// production MissionStore.SetLoopEnabled and is exercised live by the
    /// dwell lane's flights.
    /// </summary>
    public class TestCommandMissionConfigTests
    {
        [Theory]
        [InlineData("true", true, true)]
        [InlineData("false", true, false)]
        [InlineData("True", false, false)]   // strict: case-sensitive
        [InlineData("1", false, false)]
        [InlineData("", false, false)]
        [InlineData(null, false, false)]
        public void Loop_arg_is_strict_true_false(string raw, bool ok, bool on)
        {
            bool parsed = TestCommandMissionConfig.TryParseLoopArg(raw, out bool loopOn);
            Assert.Equal(ok, parsed);
            if (ok)
                Assert.Equal(on, loopOn);
        }

        [Theory]
        [InlineData(null, true, 0.0)]
        [InlineData("", true, 0.0)]
        [InlineData("19645697.5", true, 19645697.5)]
        [InlineData("0", false, 0.0)]        // zero is not a usable interval
        [InlineData("-5", false, 0.0)]
        [InlineData("NaN", false, 0.0)]
        [InlineData("Infinity", false, 0.0)]
        [InlineData("bogus", false, 0.0)]
        public void Interval_arg_is_optional_and_positive(string raw, bool ok, double want)
        {
            bool parsed = TestCommandMissionConfig.TryParseIntervalArg(raw, out double s);
            Assert.Equal(ok, parsed);
            if (ok)
                Assert.Equal(want, s, 9);
        }

        [Theory]
        [InlineData(true, 300.0, true)]
        [InlineData(true, 0.0, false)]    // 0 = the leave-alone sentinel
        [InlineData(false, 300.0, false)] // disable must not rewrite config
        [InlineData(false, 0.0, false)]
        public void Interval_applies_only_on_an_enable(bool loopOn, double s, bool want)
        {
            Assert.Equal(want,
                TestCommandMissionConfig.ShouldApplyInterval(loopOn, s));
        }

        // ------------------------------------------------------------------
        // The span-clock compression payload (added 2026-08-25). The V24W
        // reading flight (run 2026-08-25_1415) dwelled three windows built as
        // phaseAnchorUt + RECORDED offset against a unit whose clock COMPRESSES
        // an 11,393,869 s loiter out of an 18,394,999 s span, so every window
        // landed past the 7,001,129 s compressed span in the inter-cycle tail
        // and the flight observed zero ghosts. These keys are what let the
        // consumer run the same compression the clock runs.
        // ------------------------------------------------------------------

        private static List<GhostPlaybackLogic.LoopCut> Cuts(
            params double[] startThenLength)
        {
            var list = new List<GhostPlaybackLogic.LoopCut>();
            for (int i = 0; i + 1 < startThenLength.Length; i += 2)
                list.Add(new GhostPlaybackLogic.LoopCut
                {
                    StartUT = startThenLength[i],
                    LengthSeconds = startThenLength[i + 1],
                });
            return list;
        }

        [Theory]
        // The clock's own rule verbatim (SpanClock.cs:955, :1385).
        [InlineData(1000.0, 400.0, 600.0)]
        [InlineData(1000.0, 0.0, 1000.0)]     // no cuts -> raw span
        [InlineData(1000.0, -5.0, 1000.0)]    // degenerate negative -> raw span
        [InlineData(1000.0, 1000.0, 1000.0)]  // total == span -> raw span
        [InlineData(1000.0, 2000.0, 1000.0)]  // total > span -> raw span
        public void Compressed_span_mirrors_the_clock_rule(
            double span, double totalCut, double want)
        {
            Assert.Equal(want,
                TestCommandMissionConfig.CompressedSpanSeconds(span, totalCut), 9);
        }

        [Fact]
        public void Compressed_span_of_the_v24w_unit_is_the_number_the_clock_logged()
        {
            // The measured unit: span [52569234.178200819, 70964232.983117744]
            // with one 11,393,869 s cut. The game's diagnostic line reads
            // `compressedSpan=7001129/18394999`; those are that line's own
            // integer formatting, so the cell pins the exact doubles instead
            // of re-deriving how the log rounds them.
            double span = 70964232.983117744 - 52569234.178200819;
            double compressed =
                TestCommandMissionConfig.CompressedSpanSeconds(span, 11393869.0);
            Assert.Equal(18394998.804916926, span, 6);
            Assert.Equal(7001129.804916926, compressed, 6);
            // And the failure the whole change exists for: every V24W dwell
            // offset was LARGER than this compressed span.
            Assert.True(11474541.813229546 > compressed);
            Assert.True(18329155.146610025 > compressed);
            Assert.True(18390509.0 > compressed);
        }

        [Fact]
        public void Compressed_span_of_a_non_finite_span_is_non_finite()
        {
            Assert.True(double.IsNaN(
                TestCommandMissionConfig.CompressedSpanSeconds(double.NaN, 1.0)));
            // A non-finite TOTAL cannot be subtracted honestly, so the raw span
            // stands (never a NaN compressed span the clock would not agree with).
            Assert.Equal(1000.0,
                TestCommandMissionConfig.CompressedSpanSeconds(1000.0, double.NaN), 9);
        }

        [Fact]
        public void Cuts_serialize_as_span_start_relative_start_colon_length()
        {
            Assert.Equal("100:50,400:25",
                TestCommandMissionConfig.FormatLoiterCuts(
                    Cuts(1100.0, 50.0, 1400.0, 25.0), spanStartUT: 1000.0));
        }

        [Fact]
        public void One_cut_round_trips_the_v24w_shape()
        {
            // Offsets from spanStart, the frame the harness dwell offsets are
            // already in - the consumer needs no absolute UT to compress.
            string wire = TestCommandMissionConfig.FormatLoiterCuts(
                Cuts(52569234.178200819 + 3000.0, 11393869.0),
                spanStartUT: 52569234.178200819);
            Assert.StartsWith("3000", wire);
            Assert.Contains(":11393869", wire);
            Assert.DoesNotContain(",", wire);
        }

        [Fact]
        public void An_empty_or_null_cut_list_serializes_empty()
        {
            Assert.Equal("", TestCommandMissionConfig.FormatLoiterCuts(null, 0.0));
            Assert.Equal("", TestCommandMissionConfig.FormatLoiterCuts(Cuts(), 0.0));
        }

        [Fact]
        public void One_unrepresentable_cut_poisons_the_whole_list()
        {
            // FAIL-EMPTY, never FAIL-PARTIAL: a partial list would let the
            // consumer compress against fewer cuts than the clock uses and land
            // somewhere plausible but wrong.
            Assert.Equal("", TestCommandMissionConfig.FormatLoiterCuts(
                Cuts(100.0, 50.0, 400.0, double.NaN), spanStartUT: 0.0));
            Assert.Equal("", TestCommandMissionConfig.FormatLoiterCuts(
                Cuts(double.PositiveInfinity, 50.0), spanStartUT: 0.0));
            Assert.Equal("", TestCommandMissionConfig.FormatLoiterCuts(
                Cuts(100.0, 50.0), spanStartUT: double.NaN));
        }

        [Fact]
        public void An_over_cap_cut_list_serializes_empty_rather_than_truncated()
        {
            var many = new List<GhostPlaybackLogic.LoopCut>();
            for (int i = 0; i <= TestCommandMissionConfig.MaxSerializedLoiterCuts; i++)
                many.Add(new GhostPlaybackLogic.LoopCut
                {
                    StartUT = i * 100.0,
                    LengthSeconds = 10.0,
                });
            Assert.Equal("", TestCommandMissionConfig.FormatLoiterCuts(many, 0.0));

            var atCap = many.GetRange(
                0, TestCommandMissionConfig.MaxSerializedLoiterCuts);
            Assert.NotEqual("", TestCommandMissionConfig.FormatLoiterCuts(atCap, 0.0));
        }

        [Fact]
        public void The_wire_separators_are_not_protocol_reserved()
        {
            // ':' and ',' must ride the response literally, or the consumer
            // parses percent escapes it was never told about.
            string wire = TestCommandMissionConfig.FormatLoiterCuts(
                Cuts(100.0, 50.0, 400.0, 25.0), 0.0);
            Assert.Equal(wire, TestCommandProtocol.Encode(wire));
        }

        [Fact]
        public void Serialized_cuts_reproduce_the_clocks_own_compression()
        {
            // The consumer's whole job: parse this string, run CompressSpanUT
            // in offset space, and get what GhostPlaybackLogic gets. Proven
            // here against the production function itself.
            var cuts = Cuts(1100.0, 50.0, 1400.0, 25.0);
            const double spanStart = 1000.0;
            Assert.Equal("100:50,400:25",
                TestCommandMissionConfig.FormatLoiterCuts(cuts, spanStart));
            foreach (double offset in new[] { 0.0, 99.0, 100.0, 150.0, 300.0,
                                              400.0, 425.0, 900.0 })
            {
                double viaClock = GhostPlaybackLogic.CompressSpanUT(
                    spanStart + offset, cuts) - spanStart;
                // The offset-space transcription the harness runs.
                double removed = 0.0;
                foreach (var cut in cuts)
                {
                    double start = cut.StartUT - spanStart;
                    double end = start + cut.LengthSeconds;
                    if (offset <= start)
                        continue;
                    removed += (offset < end ? offset : end) - start;
                }
                Assert.Equal(viaClock, offset - removed, 9);
            }
        }
    }
}
