using Parsek.InGameTests.Helpers;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure decision-core tests for the stock Bug #4803 late-switch camera re-pin
    /// window (<see cref="FlightCameraReloadPin"/>). Only the arm/disarm decision
    /// predicate is headless-testable; the GameEvents subscription + FlightCamera
    /// re-pin side effects are exercised in-game by the FLIGHT Run All + Isolated
    /// batch (see docs/dev/todo-and-known-bugs.md, 2026-07-10 soft-freeze entry).
    /// </summary>
    public class FlightCameraReloadPinTests
    {
        [Fact]
        public void DecideOnEventWhileArmed_NotArmed_Ignores()
        {
            // An event with the window closed must never re-pin (a re-pin outside the
            // commit-to-unload window would hijack legitimate camera targeting).
            Assert.Equal(FlightCameraReloadPin.PinWindowAction.Ignore,
                FlightCameraReloadPin.DecideOnEventWhileArmed(
                    isArmed: false, armedAtRealtime: 0f, nowRealtime: 1f,
                    ttlSeconds: FlightCameraReloadPin.ArmTtlSeconds));
            // Not-armed wins even when the timestamps would look expired.
            Assert.Equal(FlightCameraReloadPin.PinWindowAction.Ignore,
                FlightCameraReloadPin.DecideOnEventWhileArmed(
                    isArmed: false, armedAtRealtime: 0f, nowRealtime: 10_000f,
                    ttlSeconds: FlightCameraReloadPin.ArmTtlSeconds));
        }

        [Fact]
        public void DecideOnEventWhileArmed_ArmedWithinTtl_RePins()
        {
            // The 2026-07-10 hole: a late vessel switch lands in the same frame as the
            // commit (elapsed ~0s) -> re-pin the pivot onto the DDOL root.
            Assert.Equal(FlightCameraReloadPin.PinWindowAction.RePin,
                FlightCameraReloadPin.DecideOnEventWhileArmed(
                    isArmed: true, armedAtRealtime: 100f, nowRealtime: 100f,
                    ttlSeconds: FlightCameraReloadPin.ArmTtlSeconds));
            // Still inside the window one tick before the TTL boundary.
            Assert.Equal(FlightCameraReloadPin.PinWindowAction.RePin,
                FlightCameraReloadPin.DecideOnEventWhileArmed(
                    isArmed: true, armedAtRealtime: 100f,
                    nowRealtime: 100f + FlightCameraReloadPin.ArmTtlSeconds - 0.01f,
                    ttlSeconds: FlightCameraReloadPin.ArmTtlSeconds));
        }

        [Fact]
        public void DecideOnEventWhileArmed_ArmedAtOrPastTtl_AutoDisarms()
        {
            // Fail-safe: a load that never completed must not leave the window live
            // forever; the first event at/past the TTL disarms WITHOUT re-pinning.
            Assert.Equal(FlightCameraReloadPin.PinWindowAction.AutoDisarm,
                FlightCameraReloadPin.DecideOnEventWhileArmed(
                    isArmed: true, armedAtRealtime: 100f,
                    nowRealtime: 100f + FlightCameraReloadPin.ArmTtlSeconds,
                    ttlSeconds: FlightCameraReloadPin.ArmTtlSeconds));
            Assert.Equal(FlightCameraReloadPin.PinWindowAction.AutoDisarm,
                FlightCameraReloadPin.DecideOnEventWhileArmed(
                    isArmed: true, armedAtRealtime: 100f, nowRealtime: 100_000f,
                    ttlSeconds: FlightCameraReloadPin.ArmTtlSeconds));
        }

        [Fact]
        public void DecideOnEventWhileArmed_NonPositiveTtl_DisablesFailSafe()
        {
            // Mirrors the runner's threshold conventions: a non-positive TTL disables
            // the fail-safe (the window then closes only via onLevelWasLoaded or an
            // explicit Disarm), so a misconfiguration degrades to the explicit
            // teardown paths instead of silently never re-pinning.
            Assert.Equal(FlightCameraReloadPin.PinWindowAction.RePin,
                FlightCameraReloadPin.DecideOnEventWhileArmed(
                    isArmed: true, armedAtRealtime: 0f, nowRealtime: 1_000_000f,
                    ttlSeconds: 0f));
            Assert.Equal(FlightCameraReloadPin.PinWindowAction.RePin,
                FlightCameraReloadPin.DecideOnEventWhileArmed(
                    isArmed: true, armedAtRealtime: 0f, nowRealtime: 1_000_000f,
                    ttlSeconds: -1f));
        }
    }
}
