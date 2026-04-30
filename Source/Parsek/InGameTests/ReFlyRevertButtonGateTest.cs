using System;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live-scene verification that <see cref="ReFlyRevertButtonGate"/> drives
    /// the real <c>FlightDriver.CanRevertToPostInit</c> static field while a
    /// re-fly session marker is active — re-enabling the Esc-menu Revert
    /// button so <see cref="RevertInterceptor"/>'s 3-option dialog becomes
    /// reachable.
    ///
    /// <para>
    /// The xUnit unit-test counterpart in <c>ReFlyRevertButtonGateTests.cs</c>
    /// covers the decision logic with a test seam; this in-game test is the
    /// canary for the real flag mutation, which the seam suppresses. Runs as
    /// a synthetic in-memory fixture: installs a fake marker on the live
    /// scenario, invokes <c>Apply</c>, asserts the engine flag flipped, and
    /// restores prior state on teardown.
    /// </para>
    /// </summary>
    public class ReFlyRevertButtonGateTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "ReFlyRevertButtonGate forces FlightDriver.CanRevertToPostInit=true while marker active")]
        public void Gate_ForcesFlagTrue_WhenMarkerActive_AndRestoresOnClear()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            // Snapshot prior state — restored unconditionally on teardown so a
            // failure here does not corrupt the player's session.
            bool savedFlag = FlightDriver.CanRevertToPostInit;
            var savedMarker = scenario.ActiveReFlySessionMarker;
            ReFlyRevertButtonGate.ResetForTesting();

            string sessionId = "sess_btn_gate_igt_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string rpId = "rp_btn_gate_igt_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            var marker = new ReFlySessionMarker
            {
                SessionId = sessionId,
                TreeId = "tree_btn_gate_igt",
                ActiveReFlyRecordingId = "rec_provisional_btn_gate_igt",
                OriginChildRecordingId = "rec_origin_btn_gate_igt",
                RewindPointId = rpId,
                InvokedUT = 0.0,
                InvokedRealTime = DateTime.UtcNow.ToString("o"),
            };

            try
            {
                // Force the engine flag false to simulate the post-LoadGame
                // state RewindInvoker leaves us in: vessel mid-flight, KSP
                // computed CanRevertToPostInit=false, button grayed out.
                FlightDriver.CanRevertToPostInit = false;
                ReFlyRevertButtonGate.ResetForTesting();
                InGameAssert.IsFalse(ReFlyRevertButtonGate.ForcedFlagForTesting,
                    "ForcedFlag must start false — fixture sanity");

                // Install marker, invoke gate. This is what AtomicMarkerWrite
                // does in production, immediately after the marker is set.
                scenario.ActiveReFlySessionMarker = marker;
                ReFlyRevertButtonGate.Apply("igt:force-after-marker-set");

                InGameAssert.IsTrue(FlightDriver.CanRevertToPostInit,
                    "ReFlyRevertButtonGate should force CanRevertToPostInit=true while marker active");
                InGameAssert.IsTrue(ReFlyRevertButtonGate.ForcedFlagForTesting,
                    "ForcedFlag should record that the gate flipped the engine value");
                InGameAssert.IsTrue(FlightDriver.CanRevert,
                    "FlightDriver.CanRevert must be true so the Esc-menu Revert button is interactable");

                // Clear the marker — this is what RetryHandler / DiscardReFlyHandler
                // / SupersedeCommit do on teardown. Apply must drop the override.
                scenario.ActiveReFlySessionMarker = null;
                ReFlyRevertButtonGate.Apply("igt:reset-after-marker-cleared");

                InGameAssert.IsFalse(ReFlyRevertButtonGate.ForcedFlagForTesting,
                    "ForcedFlag must clear after the marker becomes null");
                // The natural state for a non-PRELAUNCH active vessel is
                // false; for an on-pad PRELAUNCH vessel it's true. The gate
                // recomputes this in ComputeNaturalCanRevertToPostInit so
                // either outcome is consistent with the engine.
                bool prelaunch = FlightGlobals.ActiveVessel != null
                    && FlightGlobals.ActiveVessel.situation == Vessel.Situations.PRELAUNCH;
                InGameAssert.AreEqual(prelaunch, FlightDriver.CanRevertToPostInit,
                    "After marker cleared, gate must restore CanRevertToPostInit to the engine-natural value (PRELAUNCH-true / otherwise-false)");
            }
            finally
            {
                scenario.ActiveReFlySessionMarker = savedMarker;
                FlightDriver.CanRevertToPostInit = savedFlag;
                ReFlyRevertButtonGate.ResetForTesting();
            }
        }

        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "ReFlyRevertButtonGate does not clobber an engine-set true flag when marker is null")]
        public void Gate_DoesNotClobber_LegitimateFlagTrue_WhenInactive()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            bool savedFlag = FlightDriver.CanRevertToPostInit;
            var savedMarker = scenario.ActiveReFlySessionMarker;
            ReFlyRevertButtonGate.ResetForTesting();

            try
            {
                // Engine-set true (mimics a fresh PRELAUNCH launch where
                // FlightDriver.Start legitimately set the flag). No marker
                // active. Apply must be a no-op — clobbering this would
                // gray out the Revert button on an ordinary launch.
                FlightDriver.CanRevertToPostInit = true;
                scenario.ActiveReFlySessionMarker = null;

                ReFlyRevertButtonGate.Apply("igt:no-op-on-engine-true");

                InGameAssert.IsTrue(FlightDriver.CanRevertToPostInit,
                    "Gate must not clobber an engine-set true flag when no marker is active");
                InGameAssert.IsFalse(ReFlyRevertButtonGate.ForcedFlagForTesting,
                    "ForcedFlag must stay false — gate did not claim ownership");
            }
            finally
            {
                scenario.ActiveReFlySessionMarker = savedMarker;
                FlightDriver.CanRevertToPostInit = savedFlag;
                ReFlyRevertButtonGate.ResetForTesting();
            }
        }
    }
}
