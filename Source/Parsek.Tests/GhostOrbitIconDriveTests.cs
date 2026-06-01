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
    }
}
