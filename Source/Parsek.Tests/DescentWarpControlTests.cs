using Parsek.Reaim;
using Xunit;
using Phase = Parsek.Reaim.DescentTrigger.DescentHeadPhase;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for <see cref="DescentWarpControl.ShouldDropWarp"/> — the pure, Unity-free decision behind the
    /// auto-slow-warp-for-descent feature. (The Unity seam / addon / per-cycle dedup are exercised in-game.)
    /// </summary>
    public class DescentWarpControlTests
    {
        // Representative descent geometry: trigger at 1000, descent ends at 21513 (~5.7h clip), 1x dt = 0.02s.
        private const double Trigger = 1000.0;
        private const double DescentEnd = 21513.0;
        private const float Dt = 0.02f;

        [Fact]
        public void Descent_HighWarp_NotYetDropped_DropsNow()
        {
            // Icon is already on the clip (Descent phase) at high warp -> drop immediately.
            Assert.True(DescentWarpControl.ShouldDropWarp(
                Phase.Descent, currentUT: 1100.0, Trigger, DescentEnd, warpRate: 1000f, Dt,
                alreadyDroppedThisCycle: false));
        }

        [Fact]
        public void Descent_AtRealtime_DoesNotDrop()
        {
            // Already at 1x -> nothing to do.
            Assert.False(DescentWarpControl.ShouldDropWarp(
                Phase.Descent, currentUT: 1100.0, Trigger, DescentEnd, warpRate: 1f, Dt,
                alreadyDroppedThisCycle: false));
        }

        [Fact]
        public void Descent_AlreadyDroppedThisCycle_DoesNotDropAgain()
        {
            // Once-per-cycle: do not fight the player if they re-warp after we dropped.
            Assert.False(DescentWarpControl.ShouldDropWarp(
                Phase.Descent, currentUT: 1100.0, Trigger, DescentEnd, warpRate: 1000f, Dt,
                alreadyDroppedThisCycle: true));
        }

        [Fact]
        public void Loiter_NextStepDoesNotReachTrigger_DoesNotDrop()
        {
            // currentUT 500, rate 1000x, dt 0.02 -> next step +20s -> 520 < trigger 1000. Keep warping.
            Assert.False(DescentWarpControl.ShouldDropWarp(
                Phase.Loiter, currentUT: 500.0, Trigger, DescentEnd, warpRate: 1000f, Dt,
                alreadyDroppedThisCycle: false));
        }

        [Fact]
        public void Loiter_NextStepWouldCrossTrigger_DropsNow_Predictive()
        {
            // currentUT 990, rate 1000x, dt 0.02 -> next step +20s -> 1010 >= trigger 1000. Drop on THIS loiter
            // frame so the crossing itself happens at 1x.
            Assert.True(DescentWarpControl.ShouldDropWarp(
                Phase.Loiter, currentUT: 990.0, Trigger, DescentEnd, warpRate: 1000f, Dt,
                alreadyDroppedThisCycle: false));
        }

        [Fact]
        public void Loiter_ExtremeWarpThatWouldStepOverWholeWindow_StillDropsBeforeCrossing()
        {
            // currentUT 800, rate 5,000,000x, dt 0.02 -> next step +100000s -> far past descentEnd. We must STILL
            // drop on this loiter frame (before the crossing), not let the single frame step the whole clip.
            Assert.True(DescentWarpControl.ShouldDropWarp(
                Phase.Loiter, currentUT: 800.0, Trigger, DescentEnd, warpRate: 5_000_000f, Dt,
                alreadyDroppedThisCycle: false));
        }

        [Fact]
        public void Inert_DoesNotDrop()
        {
            Assert.False(DescentWarpControl.ShouldDropWarp(
                Phase.Inert, currentUT: 0.0, Trigger, DescentEnd, warpRate: 1000f, Dt,
                alreadyDroppedThisCycle: false));
        }

        [Fact]
        public void Done_DoesNotDrop()
        {
            Assert.False(DescentWarpControl.ShouldDropWarp(
                Phase.Done, currentUT: 22000.0, Trigger, DescentEnd, warpRate: 1000f, Dt,
                alreadyDroppedThisCycle: false));
        }

        [Fact]
        public void PastDescentEnd_DoesNotDrop_EvenIfDescentPhase()
        {
            // Defensive: never yank warp after the descent has already finished in game-time.
            Assert.False(DescentWarpControl.ShouldDropWarp(
                Phase.Descent, currentUT: DescentEnd + 100.0, Trigger, DescentEnd, warpRate: 1000f, Dt,
                alreadyDroppedThisCycle: false));
        }

        [Fact]
        public void NaNTiming_DoesNotDrop()
        {
            Assert.False(DescentWarpControl.ShouldDropWarp(
                Phase.Descent, currentUT: 1100.0, double.NaN, DescentEnd, warpRate: 1000f, Dt,
                alreadyDroppedThisCycle: false));
            Assert.False(DescentWarpControl.ShouldDropWarp(
                Phase.Loiter, currentUT: 990.0, Trigger, double.NaN, warpRate: 1000f, Dt,
                alreadyDroppedThisCycle: false));
        }
    }
}
