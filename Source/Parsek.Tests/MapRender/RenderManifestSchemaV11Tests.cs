using System;
using System.Globalization;
using Parsek.MapRender;
using Xunit;

namespace Parsek.Tests.MapRender
{
    /// <summary>
    /// M-A7 schema v1.1: the ADDITIVE optional keys and the ONE convention this pass pins by name
    /// (`.scout/schema-v1.1-decisions.md`). The schema VERSION deliberately stays 1 - every key here
    /// is optional and the Python verifier already tolerates absence - so the guard against a silent
    /// regression cannot be a version bump; it has to be these cells.
    ///
    /// <para>The boundary-overlap cell is the load-bearing one. Decision 6 pins
    /// <c>cycleIndex</c> = the PRIMARY cycle index and <c>detailA</c> = the SECONDARY cycle index,
    /// and it is driven here through a REAL <see cref="GhostPlaybackLogic.SpanLoopFrame"/> off the
    /// zero-slack loop fixture rather than a hand-built struct, so the cell measures what the clock
    /// actually produces rather than what the test author believed it produced.</para>
    /// </summary>
    [Collection("Sequential")]
    public class RenderManifestSchemaV11Tests : IDisposable
    {
        // The zero-slack loop from BoundaryOverlapClockTests: span [0,1000], cadence == span (slack 0),
        // phaseAnchor 300, T_sid 700, SOI exit 600 - so the boundary overlap ALWAYS engages and the
        // frame carries a secondary inside the borrow window.
        private const double ZAnchor = 300, ZS0 = 0, ZS1 = 1000, ZCad = 1000, ZTsid = 700, ZSoiExit = 600;

        public RenderManifestSchemaV11Tests()
        {
            ParsekLog.SuppressLogging = true;
            GhostPlaybackLogic.ResetForTesting();
            RenderCompositionRecorder.Reset();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GhostPlaybackLogic.ResetForTesting();
            RenderCompositionRecorder.Reset();
        }

        private static GhostPlaybackLogic.SpanLoopFrame ZFrame(double currentUT)
            => GhostPlaybackLogic.ComputeSpanLoopFrame(
                currentUT, ZAnchor, ZS0, ZS1, ZCad,
                schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalHoldAlignPeriod: double.NaN, launchBodyRotationPeriod: ZTsid,
                launchHoldEngaged: true, soiExitAtUT: ZSoiExit);

        private static ConfigNode Observed(RenderCompositionManifest m)
            => m.BuildFileNode(new RenderCompositionManifest.ManifestHeader(
                    1000.0, "verb", "FLIGHT", "test-save", true, false, true))
                .GetNode(RenderCompositionManifest.RootNodeName)
                .GetNode("OBSERVED");

        // =====================================================================================
        //  Decision 6: the boundary-overlap-secondary cycle-index convention
        // =====================================================================================

        [Fact]
        public void BoundaryOverlapSecondary_StampsThePrimaryCycleAndCarriesTheSecondaryInDetailA()
        {
            // phaseInCycle 850 sits inside cycle 1's borrow window for instance N+1 = 2.
            double rawDelta2 = GhostPlaybackLogic.ComputePerLoopLaunchAdvanceSeconds(
                ZAnchor, ZS0, 2, ZCad, ZTsid);
            double ut = ZAnchor + 1 * ZCad + (ZCad - rawDelta2) + 50.0;
            GhostPlaybackLogic.SpanLoopFrame frame = ZFrame(ut);
            Assert.True(frame.HasSecondary);
            Assert.Equal(1, frame.CycleIndex);
            Assert.Equal(2, frame.SecondaryCycleIndex);

            var m = new RenderCompositionManifest();
            RenderCompositionRecorder.AppendBoundaryOverlapSecondary(m, ownerIndex: 7, ut: ut, frame: in frame);

            ConfigNode ev = Assert.Single(Observed(m).GetNodes("CLOCK_EVENT"));
            Assert.Equal(RenderCompositionManifest.ClockBoundaryOverlapSecondary, ev.GetValue("kind"));
            Assert.Equal("7", ev.GetValue("ownerIndex"));
            // THE PIN: cycleIndex is the PRIMARY (the continuing instance N the camera follows) and
            // detailA is the SECONDARY (N+1). Reading cycleIndex as the secondary would recompute the
            // boundary-overlap gate one window early and silently pass a broken loop.
            Assert.Equal(
                frame.CycleIndex.ToString(CultureInfo.InvariantCulture), ev.GetValue("cycleIndex"));
            Assert.Equal(
                RenderCompositionManifest.D(frame.SecondaryCycleIndex), ev.GetValue("detailA"));
            Assert.Equal(
                RenderCompositionManifest.D(frame.SecondaryLoopUT), ev.GetValue("detailB"));
            Assert.Equal("secondary-live", ev.GetValue("detailS"));
            // And the two ARE one apart, which is what makes detailA recoverable when it is absent.
            Assert.Equal(frame.CycleIndex + 1, frame.SecondaryCycleIndex);
        }

        [Fact]
        public void BoundaryOverlapSecondary_EmitsNothingWithoutASecondary()
        {
            GhostPlaybackLogic.SpanLoopFrame frame = ZFrame(ZAnchor + 1 * ZCad + 400.0);
            Assert.True(frame.Resolved);
            Assert.False(frame.HasSecondary);

            var m = new RenderCompositionManifest();
            RenderCompositionRecorder.AppendBoundaryOverlapSecondary(m, 7, ZAnchor + 1400.0, in frame);
            Assert.Empty(Observed(m).GetNodes("CLOCK_EVENT"));
            Assert.Equal(0, m.ClockEventCount);
        }

        // =====================================================================================
        //  Decision 3: the optional fourth numeric slot
        // =====================================================================================

        [Fact]
        public void DetailD_IsWrittenOnlyWhenMeasured()
        {
            var m = new RenderCompositionManifest();
            m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockDescentPhase, 6, 0, 7300.0,
                7250.0, 6800.0, 0.0, "Descent", hasDetailD: true, detailD: 7250.0);
            m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockCycleRollover, 4, 0, 1000.0,
                0.0, 1000.0, 0.0, null);

            ConfigNode[] events = Observed(m).GetNodes("CLOCK_EVENT");
            Assert.Equal(2, events.Length);
            Assert.Equal("7250", events[0].GetValue("detailD"));
            Assert.False(events[1].HasValue("detailD"),
                "a kind that measures no fourth value must OMIT detailD, so a consumer can tell "
                + "'not measured' from 'measured zero'.");
        }

        // =====================================================================================
        //  Decisions 4 + 5: recorded-clock endpoints + ownerIndex on a DWELL
        // =====================================================================================

        private static RenderCompositionManifest.DwellSample LoopSample(
            double ut, double loopUT, bool hasLoopUT = true, bool hasOwner = true)
        {
            var s = default(RenderCompositionManifest.DwellSample);
            s.Pid = 4242u;
            s.RecId = "rec-a";
            s.CommittedIndex = 3;
            s.ChainSignature = "sig-A";
            s.SegmentIndex = 0;
            s.PhaseKind = "ascent";
            s.Treatment = "TracedPath";
            s.Visible = true;
            s.Coverage = "InSegment";
            s.FrameBody = "Kerbin";
            s.CurrentUT = ut;
            s.HeadUT = ut;
            s.WarpRate = 1.0;
            s.HasLoopUT = hasLoopUT;
            s.LoopUT = loopUT;
            s.HasOwnerIndex = hasOwner;
            s.OwnerIndex = 9;
            return s;
        }

        [Fact]
        public void Dwell_StampsTheRecordedClockAtItsOwnEndpoints()
        {
            var m = new RenderCompositionManifest();
            m.ObserveDwellFrame(LoopSample(1000.0, 500.0));
            m.ObserveDwellFrame(LoopSample(1010.0, 510.0));
            m.ObserveDwellFrame(LoopSample(1020.0, 520.0));
            m.CloseAllOpenDwells(1030.0);

            ConfigNode d = Assert.Single(Observed(m).GetNodes("DWELL"));
            Assert.Equal("9", d.GetValue("ownerIndex"));
            // OPEN keeps the FIRST frame's recorded instant; CLOSE tracks the LAST one - the pair is
            // the dwell's own interval on the recorded clock, which is the only clock a loiter cut
            // lives on.
            Assert.Equal("500", d.GetValue("openLoopUT"));
            Assert.Equal("520", d.GetValue("closeLoopUT"));
            Assert.Equal("1000", d.GetValue("openUT"));
            Assert.Equal("1030", d.GetValue("closeUT"));
        }

        [Fact]
        public void Dwell_OmitsTheOptionalKeysWhenTheMemberMapsToNoUnit()
        {
            var m = new RenderCompositionManifest();
            m.ObserveDwellFrame(LoopSample(1000.0, double.NaN, hasLoopUT: false, hasOwner: false));
            m.CloseAllOpenDwells(1010.0);

            ConfigNode d = Assert.Single(Observed(m).GetNodes("DWELL"));
            Assert.False(d.HasValue("ownerIndex"));
            Assert.False(d.HasValue("openLoopUT"));
            Assert.False(d.HasValue("closeLoopUT"));
        }

        // =====================================================================================
        //  Decision 2: the hold pair's debounce identity
        // =====================================================================================

        [Fact]
        public void HoldPair_EngageAndReleaseDoNotCollideInTheDebounceKey()
        {
            var m = new RenderCompositionManifest();
            // Same owner, same cycle, same run ordinal: the two kinds must both land (a shared key
            // would drop one).
            Assert.True(m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockHoldEngage, 4, 1, 8000.0, 0.0, 5000.0, 0.0, null));
            Assert.True(m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockHoldRelease, 4, 1, 11600.0, 0.0, 5000.0, 3600.0, null));
            // One engage per RUN: a repeat carrying the same (kind, owner, cycle, ordinal) is
            // debounced away.
            Assert.False(m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockHoldEngage, 4, 1, 8100.0, 0.0, 5000.0, 0.0, null));
            // ... but a SECOND run in the same cycle carries ordinal 1 and is admitted, which is the
            // whole reason detailA is the run ordinal rather than a repeat of the cycle index.
            Assert.True(m.AppendClockEventIfChanged(
                RenderCompositionManifest.ClockHoldEngage, 4, 1, 20000.0, 1.0, 9000.0, 0.0, null));
            Assert.Equal(3, m.ClockEventCount);
        }

        [Fact]
        public void HoldThresholds_AreNamedConstantsNotBuriedLiterals()
        {
            // The two numbers the detector is defined by. Pinned so a silent re-tune is a red rather
            // than a quietly different definition of "the render clock stood still".
            Assert.Equal(0.25, RenderCompositionRecorder.HoldStationaryLoopUtEpsilonSeconds);
            Assert.Equal(5.0, RenderCompositionRecorder.HoldMinStallSeconds);
        }

        // =====================================================================================
        //  The hold-run DETECTOR (stall accumulation)
        // =====================================================================================

        private static ConfigNode[] RecorderClockEvents()
            => Observed(RenderCompositionRecorder.ManifestForTesting).GetNodes("CLOCK_EVENT");

        private static ConfigNode SingleOfKind(ConfigNode[] events, string kind)
        {
            ConfigNode hit = null;
            for (int i = 0; i < events.Length; i++)
            {
                if (!string.Equals(events[i].GetValue("kind"), kind, StringComparison.Ordinal))
                    continue;
                Assert.Null(hit);
                hit = events[i];
            }
            Assert.NotNull(hit);
            return hit;
        }

        [Fact]
        public void HoldDetector_EngagesOnAccumulatedStall_AtOneXWhereNoSingleStepWouldQualify()
        {
            // 1x-shaped frames: 40 frames of 0.2 s live each, render clock frozen at 5000. NO single
            // step is anywhere near a second, so a per-frame stationarity floor could never fire -
            // accumulation is what makes a 1x hold observable at all.
            RenderCompositionRecorder.ObserveHoldFrameForTesting(4, 100.0, 5000.0, 1);
            for (int i = 1; i <= 40; i++)
                RenderCompositionRecorder.ObserveHoldFrameForTesting(4, 100.0 + i * 0.2, 5000.0, 1);

            ConfigNode engage = SingleOfKind(
                RecorderClockEvents(), RenderCompositionManifest.ClockHoldEngage);
            Assert.Equal("4", engage.GetValue("ownerIndex"));
            Assert.Equal("1", engage.GetValue("cycleIndex"));
            Assert.Equal("0", engage.GetValue("detailA"));    // first run of this cycle
            Assert.Equal("5000", engage.GetValue("detailB")); // the frozen loopUT
            // RETROACTIVE: the event is stamped at the stall's START (the last moving frame, UT 100),
            // not at the frame that happened to cross the threshold.
            Assert.Equal("100", engage.GetValue("ut"));

            // Still engaged - no release until the render clock moves again.
            Assert.Empty(System.Array.FindAll(RecorderClockEvents(),
                n => n.GetValue("kind") == RenderCompositionManifest.ClockHoldRelease));
        }

        [Fact]
        public void HoldDetector_SurvivesAWarpDropMidHold_AsOneRun()
        {
            // The run starts under coarse steps (10 s of live time each) and the player drops warp
            // halfway, so the steps shrink to 0.2 s. The render clock never moved, so this is ONE
            // hold: a detector keyed on the size of the live step would have released at the drop and
            // re-engaged, reporting two runs where the product held once.
            RenderCompositionRecorder.ObserveHoldFrameForTesting(4, 0.0, 5000.0, 2);
            double ut = 0.0;
            for (int i = 0; i < 3; i++)
            {
                ut += 10.0;
                RenderCompositionRecorder.ObserveHoldFrameForTesting(4, ut, 5000.0, 2);
            }
            for (int i = 0; i < 50; i++)
            {
                ut += 0.2;
                RenderCompositionRecorder.ObserveHoldFrameForTesting(4, ut, 5000.0, 2);
            }
            // The clock resumes.
            RenderCompositionRecorder.ObserveHoldFrameForTesting(4, ut + 0.2, 5010.0, 2);

            ConfigNode[] events = RecorderClockEvents();
            ConfigNode engage = SingleOfKind(events, RenderCompositionManifest.ClockHoldEngage);
            ConfigNode release = SingleOfKind(events, RenderCompositionManifest.ClockHoldRelease);

            Assert.Equal("0", engage.GetValue("detailA"));
            Assert.Equal("0", release.GetValue("detailA"));   // SAME ordinal: one run, one pair
            Assert.Equal("2", release.GetValue("cycleIndex"));
            Assert.Equal("5000", release.GetValue("detailB"));
            // detailC is the ACCUMULATED live seconds of the stall: 3 x 10 + 50 x 0.2 = 40.
            Assert.Equal(40.0,
                double.Parse(release.GetValue("detailC"), NumberStyles.Float, CultureInfo.InvariantCulture),
                6);
        }

        [Fact]
        public void HoldDetector_GivesEachStallItsOwnOrdinalWithinOneCycle()
        {
            // Two separate stalls inside cycle 3, with the render clock advancing between them. Under
            // the wave-1 convention both pairs keyed on (owner, cycle) alone and the second collapsed
            // into the debounce; the ordinal is what keeps them distinguishable.
            double ut = 0.0;
            RenderCompositionRecorder.ObserveHoldFrameForTesting(4, ut, 100.0, 3);
            for (int i = 0; i < 4; i++)
            {
                ut += 3.0;
                RenderCompositionRecorder.ObserveHoldFrameForTesting(4, ut, 100.0, 3);
            }
            ut += 3.0;
            RenderCompositionRecorder.ObserveHoldFrameForTesting(4, ut, 130.0, 3);   // clock resumes
            ut += 3.0;
            RenderCompositionRecorder.ObserveHoldFrameForTesting(4, ut, 160.0, 3);
            for (int i = 0; i < 4; i++)
            {
                ut += 3.0;
                RenderCompositionRecorder.ObserveHoldFrameForTesting(4, ut, 160.0, 3);
            }
            ut += 3.0;
            RenderCompositionRecorder.ObserveHoldFrameForTesting(4, ut, 190.0, 3);   // second release

            ConfigNode[] events = RecorderClockEvents();
            ConfigNode[] engages = System.Array.FindAll(
                events, n => n.GetValue("kind") == RenderCompositionManifest.ClockHoldEngage);
            ConfigNode[] releases = System.Array.FindAll(
                events, n => n.GetValue("kind") == RenderCompositionManifest.ClockHoldRelease);

            Assert.Equal(2, engages.Length);
            Assert.Equal(2, releases.Length);
            Assert.Equal("3", engages[0].GetValue("cycleIndex"));
            Assert.Equal("3", engages[1].GetValue("cycleIndex"));
            Assert.Equal("0", engages[0].GetValue("detailA"));
            Assert.Equal("1", engages[1].GetValue("detailA"));
            Assert.Equal("0", releases[0].GetValue("detailA"));
            Assert.Equal("1", releases[1].GetValue("detailA"));
            // Each pair names its OWN frozen instant.
            Assert.Equal("100", engages[0].GetValue("detailB"));
            Assert.Equal("160", engages[1].GetValue("detailB"));
        }

        [Fact]
        public void HoldDetector_EmitsNothingForAStallBelowTheFloor()
        {
            // 4 s of stalled live time, under the 5 s floor: below-resolution, so NO pair at all. The
            // verifier reads a missing pair as unevaluable, never as a mismatch.
            RenderCompositionRecorder.ObserveHoldFrameForTesting(4, 0.0, 700.0, 0);
            RenderCompositionRecorder.ObserveHoldFrameForTesting(4, 2.0, 700.0, 0);
            RenderCompositionRecorder.ObserveHoldFrameForTesting(4, 4.0, 700.0, 0);
            RenderCompositionRecorder.ObserveHoldFrameForTesting(4, 6.0, 730.0, 0);

            Assert.Empty(System.Array.FindAll(RecorderClockEvents(),
                n => n.GetValue("kind") == RenderCompositionManifest.ClockHoldEngage
                    || n.GetValue("kind") == RenderCompositionManifest.ClockHoldRelease));
        }

        [Fact]
        public void HoldDetector_KeepsOneIdentityAcrossACycleRollover()
        {
            // The stall starts in cycle 6 and the clock's cycle index ticks over mid-run. BOTH events
            // must keep the cycle at ENGAGE, so a hold straddling a rollover reads as one hold.
            RenderCompositionRecorder.ObserveHoldFrameForTesting(4, 0.0, 900.0, 6);
            RenderCompositionRecorder.ObserveHoldFrameForTesting(4, 4.0, 900.0, 6);
            RenderCompositionRecorder.ObserveHoldFrameForTesting(4, 8.0, 900.0, 7);
            RenderCompositionRecorder.ObserveHoldFrameForTesting(4, 12.0, 900.0, 7);
            RenderCompositionRecorder.ObserveHoldFrameForTesting(4, 16.0, 940.0, 7);

            ConfigNode[] events = RecorderClockEvents();
            Assert.Equal("6",
                SingleOfKind(events, RenderCompositionManifest.ClockHoldEngage).GetValue("cycleIndex"));
            Assert.Equal("6",
                SingleOfKind(events, RenderCompositionManifest.ClockHoldRelease).GetValue("cycleIndex"));
        }
    }
}
