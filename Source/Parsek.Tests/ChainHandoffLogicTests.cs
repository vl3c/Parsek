using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure decision tests for <see cref="ChainHandoffLogic"/> — no log capture.
    /// Runs in parallel; no shared state.
    /// </summary>
    public class ChainHandoffLogicTests
    {
        // -------------------- DecideShadow --------------------

        [Fact]
        public void DecideShadow_NoChainNext_ReturnsFalse()
        {
            // chainNextIndex = -1: this slot has no chain continuation, so the
            // shadow decision is always false (render normally).
            Assert.False(ChainHandoffLogic.DecideShadow(
                chainNextIndex: -1,
                continuationHasActiveGhost: false));
            Assert.False(ChainHandoffLogic.DecideShadow(
                chainNextIndex: -1,
                continuationHasActiveGhost: true));
        }

        [Fact]
        public void DecideShadow_ChainNextNotActive_ReturnsFalse()
        {
            // Continuation exists but its ghost hasn't loaded yet (still
            // pending spawn / building visuals). Don't shadow yet — that
            // would hide both, leaving nothing for the player to see.
            Assert.False(ChainHandoffLogic.DecideShadow(
                chainNextIndex: 5,
                continuationHasActiveGhost: false));
        }

        [Fact]
        public void DecideShadow_ChainNextActive_ReturnsTrue()
        {
            // Canonical overlap case: continuation is rendering. Shadow the
            // head so the player only sees the continuation.
            Assert.True(ChainHandoffLogic.DecideShadow(
                chainNextIndex: 5,
                continuationHasActiveGhost: true));
        }

        // -------------------- DecideBridgeHold --------------------

        [Fact]
        public void DecideBridgeHold_NoChainNext_ReturnsNone()
        {
            // No continuation exists, so no bridge needed. The normal
            // stale-past-end cleanup will destroy the head.
            Assert.Equal(ChainBridgeAction.None, ChainHandoffLogic.DecideBridgeHold(
                chainNextIndex: -1,
                continuationHasActiveGhost: false,
                currentUT: 100.0,
                bridgeOpenedUT: double.NaN,
                bridgeMaxSeconds: ChainHandoffLogic.DefaultBridgeMaxSeconds));
        }

        [Fact]
        public void DecideBridgeHold_ContinuationAlreadyActive_ReturnsNone()
        {
            // Continuation is rendering, so the shadow path is handling
            // visibility already. No bridge needed — destroy the head normally.
            Assert.Equal(ChainBridgeAction.None, ChainHandoffLogic.DecideBridgeHold(
                chainNextIndex: 5,
                continuationHasActiveGhost: true,
                currentUT: 100.0,
                bridgeOpenedUT: double.NaN,
                bridgeMaxSeconds: ChainHandoffLogic.DefaultBridgeMaxSeconds));
        }

        [Fact]
        public void DecideBridgeHold_ContinuationPendingAndBridgeNotOpenedYet_ReturnsHold()
        {
            // Canonical gap case, first frame: continuation exists but its
            // ghost hasn't loaded. Bridge has not opened yet
            // (bridgeOpenedUT = NaN). Decision: Hold (open the bridge).
            Assert.Equal(ChainBridgeAction.Hold, ChainHandoffLogic.DecideBridgeHold(
                chainNextIndex: 5,
                continuationHasActiveGhost: false,
                currentUT: 336.65,
                bridgeOpenedUT: double.NaN,
                bridgeMaxSeconds: ChainHandoffLogic.DefaultBridgeMaxSeconds));
        }

        [Fact]
        public void DecideBridgeHold_ContinuationPendingWithinBridgeWindow_ReturnsHold()
        {
            // Bridge opened earlier, elapsed < window. Decision: Hold.
            Assert.Equal(ChainBridgeAction.Hold, ChainHandoffLogic.DecideBridgeHold(
                chainNextIndex: 5,
                continuationHasActiveGhost: false,
                currentUT: 336.85, // 0.20s after bridge open
                bridgeOpenedUT: 336.65,
                bridgeMaxSeconds: 1.0));
        }

        [Fact]
        public void DecideBridgeHold_ContinuationPendingExactlyAtWindowEdge_ReturnsExpired()
        {
            // Boundary: elapsed == window. The implementation uses
            // strict '<' so the edge counts as expired.
            Assert.Equal(ChainBridgeAction.Expired, ChainHandoffLogic.DecideBridgeHold(
                chainNextIndex: 5,
                continuationHasActiveGhost: false,
                currentUT: 337.65, // exactly 1.0s after bridge open
                bridgeOpenedUT: 336.65,
                bridgeMaxSeconds: 1.0));
        }

        [Fact]
        public void DecideBridgeHold_ContinuationPendingBeyondBridgeWindow_ReturnsExpired()
        {
            // Bridge has been held longer than the window. Decision: Expired
            // (let the head destroy normally to bound the stuck-bridge case).
            Assert.Equal(ChainBridgeAction.Expired, ChainHandoffLogic.DecideBridgeHold(
                chainNextIndex: 5,
                continuationHasActiveGhost: false,
                currentUT: 338.65, // 2.0s after bridge open, window=1.0
                bridgeOpenedUT: 336.65,
                bridgeMaxSeconds: 1.0));
        }

        [Fact]
        public void DecideBridgeHold_ZeroBridgeWindow_CollapsesToExpiredAfterFirstOpenFrame()
        {
            // Edge case: a zero-second bridge window means the bridge opens
            // on the first frame, then immediately expires on the second.
            Assert.Equal(ChainBridgeAction.Hold, ChainHandoffLogic.DecideBridgeHold(
                chainNextIndex: 5,
                continuationHasActiveGhost: false,
                currentUT: 336.65,
                bridgeOpenedUT: double.NaN, // first frame
                bridgeMaxSeconds: 0.0));
            Assert.Equal(ChainBridgeAction.Expired, ChainHandoffLogic.DecideBridgeHold(
                chainNextIndex: 5,
                continuationHasActiveGhost: false,
                currentUT: 336.67,
                bridgeOpenedUT: 336.65, // second frame, bridge open
                bridgeMaxSeconds: 0.0));
        }

        [Fact]
        public void DecideBridgeHold_NegativeBridgeWindow_TreatsAsExpired()
        {
            // Defensive: negative window means "no bridge ever". After the
            // first open frame, immediately expire.
            Assert.Equal(ChainBridgeAction.Expired, ChainHandoffLogic.DecideBridgeHold(
                chainNextIndex: 5,
                continuationHasActiveGhost: false,
                currentUT: 336.67,
                bridgeOpenedUT: 336.65,
                bridgeMaxSeconds: -0.5));
        }

        [Fact]
        public void DefaultBridgeMaxSeconds_IsOneSecond()
        {
            // Pin the production default. Changing this value is a behaviour
            // change that should be deliberate.
            Assert.Equal(1.0, ChainHandoffLogic.DefaultBridgeMaxSeconds);
        }
    }
}
