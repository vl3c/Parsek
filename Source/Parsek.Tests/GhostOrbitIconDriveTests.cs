using System;
using Parsek;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for the frozen-icon-on-short-arc fix: the per-pid loop-epoch shift seam
    /// (<see cref="GhostMapPresence.GetGhostOrbitEpochShift"/> /
    /// <see cref="GhostMapPresence.MapLiveUTToEffUT"/>) and the pure icon-drive decision
    /// (<see cref="GhostOrbitIconDrivePatch.ResolveIconDriveDecision"/>). The decision picks the
    /// recorded-clock UT the ghost OrbitDriver is propagated at every frame so the marker glides at
    /// the loop-mapped effUT rate (no freeze), clamping to the recorded endpoints (and suppressing
    /// the native icon for the below-atmosphere handoff) only when the head is genuinely off the
    /// recorded window / off the visible arc.
    /// </summary>
    [Collection("Sequential")]
    public class GhostOrbitIconDriveTests : IDisposable
    {
        public GhostOrbitIconDriveTests()
        {
            GhostMapPresence.ResetForTesting();
        }

        public void Dispose()
        {
            GhostMapPresence.ResetForTesting();
        }

        // --- MapLiveUTToEffUT: live -> recorded sample clock ---

        [Fact]
        public void MapLiveUTToEffUT_SubtractsShift()
        {
            // effUT = liveUT - shift. shift = liveUT_now - effUT_now, so a positive shift means
            // the recorded clock is BEHIND the live clock (effUT < liveUT).
            Assert.Equal(40.0, GhostMapPresence.MapLiveUTToEffUT(100.0, 60.0));
        }

        [Fact]
        public void MapLiveUTToEffUT_ZeroShift_IsIdentity()
        {
            // Non-loop ghost: shift 0 => effUT == liveUT, so behavior is unchanged from stock.
            Assert.Equal(123.456, GhostMapPresence.MapLiveUTToEffUT(123.456, 0.0));
        }

        [Fact]
        public void MapLiveUTToEffUT_NegativeShift_AddsMagnitude()
        {
            // A loop where the recorded clock runs AHEAD of live (effUT > liveUT) yields a
            // negative shift; effUT = liveUT - shift adds the magnitude back.
            Assert.Equal(150.0, GhostMapPresence.MapLiveUTToEffUT(100.0, -50.0));
        }

        // --- GetGhostOrbitEpochShift: per-pid round-trip + default ---

        [Fact]
        public void GetGhostOrbitEpochShift_Unset_ReturnsZero()
        {
            Assert.Equal(0.0, GhostMapPresence.GetGhostOrbitEpochShift(9999u));
        }

        [Fact]
        public void GetGhostOrbitEpochShift_AfterReset_ReturnsZero()
        {
            GhostMapPresence.ghostOrbitEpochShift[7u] = 555.0;
            GhostMapPresence.ResetForTesting();
            Assert.Equal(0.0, GhostMapPresence.GetGhostOrbitEpochShift(7u));
        }

        [Fact]
        public void GetGhostOrbitEpochShift_PerPidIndependent()
        {
            GhostMapPresence.ghostOrbitEpochShift[1u] = 10.0;
            GhostMapPresence.ghostOrbitEpochShift[2u] = -20.0;
            Assert.Equal(10.0, GhostMapPresence.GetGhostOrbitEpochShift(1u));
            Assert.Equal(-20.0, GhostMapPresence.GetGhostOrbitEpochShift(2u));
            Assert.Equal(0.0, GhostMapPresence.GetGhostOrbitEpochShift(3u));
        }

        // --- ResolveIconDriveDecision: on-arc within window => GLIDE at effUT ---

        [Fact]
        public void ResolveIconDriveDecision_OnArcWithinWindow_DrivesAtEffUT_NotSuppressed()
        {
            // The freeze case: a short orbital arc with a large loop shift. The head is inside the
            // (live-frame) window and on the visible arc, so the icon must be driven at the
            // recorded-clock effUT = liveUT - shift (here 1000 - 600 = 400), NOT clamped.
            var d = GhostOrbitIconDrivePatch.ResolveIconDriveDecision(
                liveUT: 1000.0, startUTShifted: 950.0, endUTShifted: 1050.0, shift: 600.0,
                onArc: true);
            Assert.False(d.Suppressed);
            Assert.Equal("on-arc-drive", d.Reason);
            Assert.Equal(400.0, d.DriveUT);
        }

        [Fact]
        public void ResolveIconDriveDecision_OnArcWithinWindow_ZeroShift_DrivesAtLiveUT()
        {
            // Non-loop ghost: drive UT == live UT (byte-identical to stock live propagation).
            var d = GhostOrbitIconDrivePatch.ResolveIconDriveDecision(
                liveUT: 500.0, startUTShifted: 400.0, endUTShifted: 600.0, shift: 0.0,
                onArc: true);
            Assert.False(d.Suppressed);
            Assert.Equal("on-arc-drive", d.Reason);
            Assert.Equal(500.0, d.DriveUT);
        }

        // --- ResolveIconDriveDecision: past / before the recorded window => clamp + suppress ---

        [Fact]
        public void ResolveIconDriveDecision_PastWindow_ClampsToRawEnd_Suppressed()
        {
            // liveUT past the shifted window => clamp to the recorded end (endUTShifted - shift).
            var d = GhostOrbitIconDrivePatch.ResolveIconDriveDecision(
                liveUT: 1100.0, startUTShifted: 950.0, endUTShifted: 1050.0, shift: 600.0,
                onArc: true);
            Assert.True(d.Suppressed);
            Assert.Equal("past-window", d.Reason);
            Assert.Equal(450.0, d.DriveUT); // 1050 - 600
        }

        [Fact]
        public void ResolveIconDriveDecision_BeforeWindow_ClampsToRawStart_Suppressed()
        {
            var d = GhostOrbitIconDrivePatch.ResolveIconDriveDecision(
                liveUT: 900.0, startUTShifted: 950.0, endUTShifted: 1050.0, shift: 600.0,
                onArc: true);
            Assert.True(d.Suppressed);
            Assert.Equal("before-window", d.Reason);
            Assert.Equal(350.0, d.DriveUT); // 950 - 600
        }

        [Fact]
        public void ResolveIconDriveDecision_PastWindow_OverridesOnArcFalse()
        {
            // Past-window is checked before the on-arc branch, so onArc is irrelevant there.
            var d = GhostOrbitIconDrivePatch.ResolveIconDriveDecision(
                liveUT: 1100.0, startUTShifted: 950.0, endUTShifted: 1050.0, shift: 0.0,
                onArc: false);
            Assert.True(d.Suppressed);
            Assert.Equal("past-window", d.Reason);
            Assert.Equal(1050.0, d.DriveUT);
        }

        // --- ResolveIconDriveDecision: within window but off the visible arc => off-arc signal ---

        [Fact]
        public void ResolveIconDriveDecision_WithinWindowOffArc_SignalsOffArc_Suppressed()
        {
            // Underground portion (#212b): inside the window but off the above-ground arc. The
            // pure helper signals off-arc (DriveUT NaN) and the caller computes the nearest-endpoint
            // clamp from the Orbit; the icon is suppressed for the custom-icon handoff.
            var d = GhostOrbitIconDrivePatch.ResolveIconDriveDecision(
                liveUT: 1000.0, startUTShifted: 950.0, endUTShifted: 1050.0, shift: 600.0,
                onArc: false);
            Assert.True(d.Suppressed);
            Assert.Equal("off-arc", d.Reason);
            Assert.True(double.IsNaN(d.DriveUT));
        }

        // --- boundary: liveUT exactly at the window edges stays in-window (drive) ---

        [Fact]
        public void ResolveIconDriveDecision_AtEndUTExactly_StaysInWindow()
        {
            var d = GhostOrbitIconDrivePatch.ResolveIconDriveDecision(
                liveUT: 1050.0, startUTShifted: 950.0, endUTShifted: 1050.0, shift: 600.0,
                onArc: true);
            Assert.False(d.Suppressed);
            Assert.Equal("on-arc-drive", d.Reason);
            Assert.Equal(450.0, d.DriveUT);
        }

        [Fact]
        public void ResolveIconDriveDecision_AtStartUTExactly_StaysInWindow()
        {
            var d = GhostOrbitIconDrivePatch.ResolveIconDriveDecision(
                liveUT: 950.0, startUTShifted: 950.0, endUTShifted: 1050.0, shift: 600.0,
                onArc: true);
            Assert.False(d.Suppressed);
            Assert.Equal("on-arc-drive", d.Reason);
            Assert.Equal(350.0, d.DriveUT);
        }

        // --- lockstep: the icon drive UT and the arc-clip bounds use the SAME mapping ---

        [Fact]
        public void IconDriveAndArcClip_UseTheSameLiveToEffMapping()
        {
            // The arc-clip patch maps its stored (live-frame) bounds to the recorded clock with the
            // identical MapLiveUTToEffUT, so the line shape and the icon are evaluated in one frame.
            const double shift = 600.0;
            const double startUTShifted = 950.0;
            const double endUTShifted = 1050.0;
            const double liveUT = 1000.0;

            var icon = GhostOrbitIconDrivePatch.ResolveIconDriveDecision(
                liveUT, startUTShifted, endUTShifted, shift, onArc: true);

            double arcStartRaw = GhostMapPresence.MapLiveUTToEffUT(startUTShifted, shift);
            double arcEndRaw = GhostMapPresence.MapLiveUTToEffUT(endUTShifted, shift);

            // The icon drive UT lies within the arc-clip's recorded-frame bounds.
            Assert.InRange(icon.DriveUT, arcStartRaw, arcEndRaw);
            // And the at-edge live UTs map exactly onto the arc-clip endpoints.
            Assert.Equal(arcStartRaw, GhostMapPresence.MapLiveUTToEffUT(startUTShifted, shift));
            Assert.Equal(arcEndRaw, GhostMapPresence.MapLiveUTToEffUT(endUTShifted, shift));
        }

        // --- state-vector transition: the drive patch must DEFER (return true) for a
        //     state-vector-reseeded ghost, so stock propagates the shifted-epoch orbit at live UT ---

        [Fact]
        public void DrivePatchGate_StaleLoopShiftedSegmentBounds_WouldEngage()
        {
            // The pre-fix hazard: a loop ghost that just transitioned in place from a covering
            // OrbitSegment into a transfer-coast OrbitalCheckpoint gap kept its prior segment
            // phase's loop-shifted bounds + epoch shift. The now-authoritative drive patch keys on
            // TryGetVisibleOrbitBoundsForGhostVessel: with the stale loop-shifted entry present it
            // returns the stale (shifted) bounds, so the patch WOULD engage and re-subtract the
            // shift from the already-shifted state-vector orbit (the freeze/mis-position regression).
            const uint pid = 4242u;
            GhostMapPresence.ghostOrbitBounds[pid] = (950.0, 1050.0);
            GhostMapPresence.ghostOrbitLoopShiftedPids.Add(pid);
            GhostMapPresence.ghostOrbitEpochShift[pid] = 600.0;

            bool engaged = GhostMapPresence.TryGetVisibleOrbitBoundsForGhostVessel(
                pid, currentUT: 1000.0, out double startUT, out double endUT);

            Assert.True(engaged);
            Assert.Equal(950.0, startUT);
            Assert.Equal(1050.0, endUT);
        }

        [Fact]
        public void DrivePatchGate_AfterStateVectorReseedClearsSegmentDicts_Defers()
        {
            // The fix: UpdateGhostOrbitFromStateVectors clears the segment-drive dicts for the pid
            // before reseeding the shifted-epoch state-vector orbit. With the dicts cleared,
            // TryGetVisibleOrbitBoundsForGhostVessel returns false (no loop-shifted entry, no stored
            // bounds, no covering OrbitSegment for an unmapped pid), so GhostOrbitIconDrivePatch
            // returns true and defers to stock's live-UT propagation of the shifted-epoch orbit
            // (the correct state-vector behavior in BOTH the flight map and the Tracking Station).
            const uint pid = 4242u;
            GhostMapPresence.ghostOrbitBounds[pid] = (950.0, 1050.0);
            GhostMapPresence.ghostOrbitLoopShiftedPids.Add(pid);
            GhostMapPresence.ghostOrbitEpochShift[pid] = 600.0;

            // Simulate the state-vector reseed's stale-segment-drive clear.
            GhostMapPresence.ghostOrbitBounds.Remove(pid);
            GhostMapPresence.ghostBodyFrameOrbitBounds.Remove(pid);
            GhostMapPresence.ghostOrbitLoopShiftedPids.Remove(pid);
            GhostMapPresence.ghostOrbitEpochShift.Remove(pid);

            bool engaged = GhostMapPresence.TryGetVisibleOrbitBoundsForGhostVessel(
                pid, currentUT: 1000.0, out _, out _);

            Assert.False(engaged);
            // The shift is also gone, so any incidental read maps live -> eff as identity.
            Assert.Equal(0.0, GhostMapPresence.GetGhostOrbitEpochShift(pid));
        }

        // --- Bug 3 burn-seam: ShouldSuppressIconNoBounds (no-bounds suppress decision) ---

        [Fact]
        public void ShouldSuppressIconNoBounds_DirectorTracking_Suppresses()
        {
            // A director-tracked ghost whose segment bounds were just cleared at a loiter->burn
            // state-vector reseed: suppress the proto icon so the legacy terminal-visible branch
            // does not show it on the per-frame phantom eccentric orbit (the residual teleport).
            Assert.True(GhostOrbitIconDrivePatch.ShouldSuppressIconNoBounds(directorTracking: true));
        }

        [Fact]
        public void ShouldSuppressIconNoBounds_NotDirectorTracking_DoesNotSuppress()
        {
            // A genuine terminal-orbit ghost (not director-tracked) has shift 0 + a real full
            // ellipse; stock's live-UT propagation already glides the icon, so it must NOT be
            // suppressed (suppressing it would blank a correctly-positioned icon).
            Assert.False(GhostOrbitIconDrivePatch.ShouldSuppressIconNoBounds(directorTracking: false));
        }

        // --- Bug 3 burn-seam: ClassifyNoBoundsSuppressionTransition (enter / sustain / exit / none) ---

        [Fact]
        public void ClassifyNoBoundsSuppressionTransition_FirstSuppressedFrame_IsEnter()
        {
            // Suppressed this frame, never suppressed before (lastSuppressedFrame = MinValue):
            // the ENTER of a no-bounds suppression run (the stale window opens).
            var t = GhostMapPresence.ClassifyNoBoundsSuppressionTransition(
                suppressedThisFrame: true, currentFrame: 100, lastSuppressedFrame: int.MinValue);
            Assert.Equal(GhostMapPresence.NoBoundsSuppressTransition.Enter, t);
        }

        [Fact]
        public void ClassifyNoBoundsSuppressionTransition_SuppressedAgainImmediately_IsSustain()
        {
            // Suppressed this frame AND on the immediately-preceding frame (currentFrame - 1):
            // a continuing suppressed run, not logged per-frame.
            var t = GhostMapPresence.ClassifyNoBoundsSuppressionTransition(
                suppressedThisFrame: true, currentFrame: 101, lastSuppressedFrame: 100);
            Assert.Equal(GhostMapPresence.NoBoundsSuppressTransition.Sustain, t);
        }

        [Fact]
        public void ClassifyNoBoundsSuppressionTransition_DrivenAfterSuppressedRun_IsExit()
        {
            // NOT suppressed this frame, but suppressed on the immediately-preceding frame:
            // the un-suppress EXIT (the icon snap boundary the read needs).
            var t = GhostMapPresence.ClassifyNoBoundsSuppressionTransition(
                suppressedThisFrame: false, currentFrame: 102, lastSuppressedFrame: 101);
            Assert.Equal(GhostMapPresence.NoBoundsSuppressTransition.Exit, t);
        }

        [Fact]
        public void ClassifyNoBoundsSuppressionTransition_DrivenWithNoRecentSuppression_IsNone()
        {
            // Steady-state driven ghost: not suppressed this frame, no immediately-preceding
            // suppressed frame -> None (the per-frame fast path that logs nothing).
            var t = GhostMapPresence.ClassifyNoBoundsSuppressionTransition(
                suppressedThisFrame: false, currentFrame: 200, lastSuppressedFrame: int.MinValue);
            Assert.Equal(GhostMapPresence.NoBoundsSuppressTransition.None, t);
        }

        [Fact]
        public void ClassifyNoBoundsSuppressionTransition_GapFrameBetweenSuppressed_IsExitThenEnter()
        {
            // A non-suppressed frame BETWEEN two suppressed frames is a clean EXIT then ENTER (the
            // strict currentFrame-1 match), so burn-seam chatter is captured frame-accurately rather
            // than collapsed. Frame N suppressed, N+1 driven (EXIT), N+2 suppressed (ENTER).
            var exit = GhostMapPresence.ClassifyNoBoundsSuppressionTransition(
                suppressedThisFrame: false, currentFrame: 301, lastSuppressedFrame: 300);
            Assert.Equal(GhostMapPresence.NoBoundsSuppressTransition.Exit, exit);

            // After the EXIT the stamp is pruned, so frame 302's lastSuppressedFrame is MinValue
            // again (not 301, since 301 was not suppressed) -> ENTER.
            var enter = GhostMapPresence.ClassifyNoBoundsSuppressionTransition(
                suppressedThisFrame: true, currentFrame: 302, lastSuppressedFrame: int.MinValue);
            Assert.Equal(GhostMapPresence.NoBoundsSuppressTransition.Enter, enter);
        }

        [Fact]
        public void ClassifyNoBoundsSuppressionTransition_StaleNonAdjacentStamp_IsEnterNotSustain()
        {
            // A stamp from many frames ago (not currentFrame-1) does NOT count as suppressed last
            // frame, so a fresh suppression is an ENTER, not a Sustain. Guards against treating a
            // stale stamp as a continuing run.
            var t = GhostMapPresence.ClassifyNoBoundsSuppressionTransition(
                suppressedThisFrame: true, currentFrame: 500, lastSuppressedFrame: 400);
            Assert.Equal(GhostMapPresence.NoBoundsSuppressTransition.Enter, t);
        }

        // --- Bug 3 burn-seam: ghostNoBoundsSuppressLastFrame cleared on reset ---

        [Fact]
        public void GhostNoBoundsSuppressLastFrame_AfterReset_IsEmpty()
        {
            GhostMapPresence.ghostNoBoundsSuppressLastFrame[42u] = 1234;
            GhostMapPresence.ResetForTesting();
            Assert.Empty(GhostMapPresence.ghostNoBoundsSuppressLastFrame);
        }

        // --- Bug 3 burn-seam: the Director-traced suppress path (the path the headline event takes)
        //     classifies Enter then Exit through the SAME stamp the no-bounds branch uses. The
        //     EmitIconSuppressTransition emitter is Unity-coupled (private), so this replays the exact
        //     stamp-maintenance + classify sequence the emitter runs to prove the wiring across the two
        //     suppress branches produces a clean Enter/Exit pair (the deliverable: the grep fires). ---

        [Fact]
        public void IconSuppressTransition_DirectorTracedThenDriven_ClassifiesEnterThenExit()
        {
            const uint pid = 413625158u; // the headline burn-seam ghost from the captured log

            // Frame N: Director-traced early-return suppresses the icon (no prior stamp) -> ENTER.
            int lastN = GhostMapPresence.ghostNoBoundsSuppressLastFrame.TryGetValue(pid, out int fN)
                ? fN : int.MinValue;
            var tN = GhostMapPresence.ClassifyNoBoundsSuppressionTransition(
                suppressedThisFrame: true, currentFrame: 104448, lastSuppressedFrame: lastN);
            Assert.Equal(GhostMapPresence.NoBoundsSuppressTransition.Enter, tN);
            GhostMapPresence.ghostNoBoundsSuppressLastFrame[pid] = 104448; // emitter stamps suppressed frames

            // Frame N+1: still Director-traced -> SUSTAIN, stamp extends.
            int lastN1 = GhostMapPresence.ghostNoBoundsSuppressLastFrame[pid];
            var tN1 = GhostMapPresence.ClassifyNoBoundsSuppressionTransition(
                suppressedThisFrame: true, currentFrame: 104449, lastSuppressedFrame: lastN1);
            Assert.Equal(GhostMapPresence.NoBoundsSuppressTransition.Sustain, tN1);
            GhostMapPresence.ghostNoBoundsSuppressLastFrame[pid] = 104449;

            // Frame N+2: the StockConic (hyperbolic) drive re-establishes, the Prefix reaches the
            // bounds-found else branch with suppressedThisFrame=false -> EXIT (the snap boundary), and
            // the emitter prunes the stamp so a later run is a clean ENTER again.
            int lastN2 = GhostMapPresence.ghostNoBoundsSuppressLastFrame[pid];
            var tN2 = GhostMapPresence.ClassifyNoBoundsSuppressionTransition(
                suppressedThisFrame: false, currentFrame: 104450, lastSuppressedFrame: lastN2);
            Assert.Equal(GhostMapPresence.NoBoundsSuppressTransition.Exit, tN2);
            GhostMapPresence.ghostNoBoundsSuppressLastFrame.Remove(pid);

            Assert.False(GhostMapPresence.ghostNoBoundsSuppressLastFrame.ContainsKey(pid));
        }

        // --- icon-drive propagateUT record + freshness gate (probe icon-off-orbit reference clock) ---

        [Fact]
        public void IconDrivePropagateUT_FreshRecord_ReturnsDrivenUT()
        {
            const uint pid = 42u;
            GhostMapPresence.RecordIconDrivePropagateUT(pid, propagateUT: 13453.3, frame: 14626);

            bool has = GhostMapPresence.TryGetFreshIconDrivePropagateUT(
                pid, currentFrame: 14626, freshnessFrames: 2, out double ut);

            Assert.True(has);
            Assert.Equal(13453.3, ut, 3);
        }

        [Fact]
        public void IconDrivePropagateUT_WithinFreshnessWindow_StillFresh()
        {
            const uint pid = 42u;
            GhostMapPresence.RecordIconDrivePropagateUT(pid, propagateUT: 13453.3, frame: 14625);

            // Recorded at frame 14625, read at 14627: within a 2-frame window (the drive ran a frame
            // or two before this probe sample), so the record is still trusted.
            bool has = GhostMapPresence.TryGetFreshIconDrivePropagateUT(
                pid, currentFrame: 14627, freshnessFrames: 2, out double ut);

            Assert.True(has);
            Assert.Equal(13453.3, ut, 3);
        }

        [Fact]
        public void IconDrivePropagateUT_StaleRecord_FallsBack()
        {
            const uint pid = 42u;
            GhostMapPresence.RecordIconDrivePropagateUT(pid, propagateUT: 13453.3, frame: 14625);

            // Read 5 frames later (the icon-drive did not run since, e.g. stock re-took the drive at a
            // stale-segment transition): the record is no longer trusted, so the probe falls back to
            // its own derivation rather than comparing against a phase the icon may have left.
            bool has = GhostMapPresence.TryGetFreshIconDrivePropagateUT(
                pid, currentFrame: 14630, freshnessFrames: 2, out double ut);

            Assert.False(has);
            Assert.Equal(0.0, ut);
        }

        [Fact]
        public void IconDrivePropagateUT_AbsentRecord_FallsBack()
        {
            bool has = GhostMapPresence.TryGetFreshIconDrivePropagateUT(
                vesselPid: 999u, currentFrame: 14626, freshnessFrames: 2, out double ut);

            Assert.False(has);
            Assert.Equal(0.0, ut);
        }

        [Fact]
        public void IconDrivePropagateUT_ClearedByReset()
        {
            const uint pid = 42u;
            GhostMapPresence.RecordIconDrivePropagateUT(pid, propagateUT: 13453.3, frame: 14626);
            GhostMapPresence.ResetForTesting();

            bool has = GhostMapPresence.TryGetFreshIconDrivePropagateUT(
                pid, currentFrame: 14626, freshnessFrames: 2, out _);

            Assert.False(has);
        }
    }
}
