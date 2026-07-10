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

        // ------------------------------------------------------------------
        // KSP EventData static-handler trap pins (2026-07-10 batch-prime NRE).
        // KSP's EventData<T>.Add constructs an EvtDelegate whose ctor reads
        // evt.Target.GetType().Name (decompiled, 1.12.5), so a delegate to a
        // STATIC method (null Target) throws NullReferenceException inside Add.
        // FlightCameraReloadPin.Arm hit this on its very first in-game call:
        // the pin window never armed and every batch aborted at the first
        // isolated-restore prime. Second project occurrence of this trap (the
        // first wiped a persistent.sfs index from a static OnLoad handler).
        // EventData<T> itself is plain C# (no Unity natives), so the trap and
        // the cached-lambda idiom are pinned headless here.
        // ------------------------------------------------------------------

        private static void StaticHandlerForTrapPin(int value) { }

        [Fact]
        public void KspEventDataAdd_StaticMethodDelegate_ThrowsNre_TheTrapThisFileMustAvoid()
        {
            var evt = new EventData<int>("parsekTestStaticTrap");
            Assert.Throws<System.NullReferenceException>(
                () => evt.Add(StaticHandlerForTrapPin));
        }

        [Fact]
        public void KspEventDataAdd_NonCapturingLambda_SubscribesAndRemoves()
        {
            // The cached non-capturing-lambda idiom FlightCameraReloadPin uses:
            // the lambda's Target is the compiler's closure singleton (non-null),
            // so Add succeeds; reusing the SAME instance keeps Remove's
            // delegate-equality match.
            EventData<int>.OnEvent handler = v => StaticHandlerForTrapPin(v);
            var evt = new EventData<int>("parsekTestLambdaIdiom");
            evt.Add(handler);
            evt.Remove(handler);
        }

        [Fact]
        public void ArmSubscribesViaCachedLambdaFields_SourceGate()
        {
            // Source-text wiring gate: Arm/Disarm must subscribe through the
            // cached delegate fields, never a naked static method group (which
            // compiles to a null-Target delegate and NREs inside EventData.Add).
            string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                System.AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..",
                "Source", "Parsek", "InGameTests", "Helpers", "FlightCameraReloadPin.cs"));
            Assert.True(System.IO.File.Exists(path), $"source file not found: {path}");
            string source = System.IO.File.ReadAllText(path);
            Assert.Contains(".Add(VesselChangeHandler)", source);
            Assert.Contains(".Add(LevelLoadedHandler)", source);
            Assert.DoesNotContain(".Add(OnVesselChangeWhileArmed)", source);
            Assert.DoesNotContain(".Add(OnLevelLoadedWhileArmed)", source);
        }
    }
}
