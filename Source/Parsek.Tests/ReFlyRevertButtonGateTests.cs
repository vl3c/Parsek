using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Guards <see cref="ReFlyRevertButtonGate"/>'s decision logic:
    /// <list type="bullet">
    ///   <item><description>Active marker → forces <c>FlightDriver.CanRevertToPostInit = true</c> so the Esc-menu Revert button is clickable and routes to <see cref="RevertInterceptor.Prefix"/>.</description></item>
    ///   <item><description>No marker / no scenario → no override.</description></item>
    ///   <item><description>Marker cleared after a force → reset path drops the override and logs the transition.</description></item>
    ///   <item><description>Engine-set true (legitimate launch) → not clobbered; <c>forcedFlag</c> stays false.</description></item>
    /// </list>
    ///
    /// <para>
    /// Touching <see cref="FlightDriver.CanRevertToPostInit"/> directly from
    /// xUnit is cheap (it's a public static field), but exercising the natural
    /// state recompute requires <c>FlightGlobals.ActiveVessel</c> which is
    /// Unity-only. Tests use <see cref="ReFlyRevertButtonGate.ApplyForTesting"/>
    /// to observe the decision, plus log-capture assertions to verify the
    /// force/reset transitions emit their tagged log lines.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class ReFlyRevertButtonGateTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;

        public ReFlyRevertButtonGateTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            ParsekScenario.ResetInstanceForTesting();
            ReFlyRevertButtonGate.ResetForTesting();
        }

        public void Dispose()
        {
            ReFlyRevertButtonGate.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            ParsekScenario.ResetInstanceForTesting();
        }

        // ---------- Helpers ---------------------------------------------

        private static ReFlySessionMarker MakeMarker(string sessionId = "sess_btn_gate_test")
        {
            return new ReFlySessionMarker
            {
                SessionId = sessionId,
                TreeId = "tree_btn_gate",
                ActiveReFlyRecordingId = "rec_provisional_btn_gate",
                OriginChildRecordingId = "rec_origin_btn_gate",
                RewindPointId = "rp_btn_gate",
                InvokedUT = 100.0,
                InvokedRealTime = "2026-04-30T00:00:00.000Z",
            };
        }

        private static ParsekScenario InstallScenario(ReFlySessionMarker marker = null)
        {
            var scenario = new ParsekScenario
            {
                ActiveReFlySessionMarker = marker,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            return scenario;
        }

        // ---------- Apply: decision routing ------------------------------

        [Fact]
        public void Apply_NoScenario_TreatedAsInactive()
        {
            ParsekScenario.ResetInstanceForTesting();

            bool? observed = null;
            ReFlyRevertButtonGate.ApplyForTesting = active => observed = active;

            ReFlyRevertButtonGate.Apply("test:no-scenario");

            Assert.False(observed ?? true);
        }

        [Fact]
        public void Apply_ScenarioWithoutMarker_TreatedAsInactive()
        {
            InstallScenario(marker: null);

            bool? observed = null;
            ReFlyRevertButtonGate.ApplyForTesting = active => observed = active;

            ReFlyRevertButtonGate.Apply("test:no-marker");

            Assert.False(observed ?? true);
        }

        [Fact]
        public void Apply_ScenarioWithMarker_TreatedAsActive()
        {
            InstallScenario(marker: MakeMarker());

            bool? observed = null;
            ReFlyRevertButtonGate.ApplyForTesting = active => observed = active;

            ReFlyRevertButtonGate.Apply("test:active-marker");

            Assert.True(observed ?? false);
        }

        // ---------- Force / reset cycle on the real flag -----------------

        [Fact]
        public void Apply_ActiveMarker_FlagWasFalse_ForcesTrueAndLogs()
        {
            InstallScenario(marker: MakeMarker(sessionId: "sess_force_test"));

            FlightDriver.CanRevertToPostInit = false;
            ReFlyRevertButtonGate.ResetForTesting();
            Assert.False(ReFlyRevertButtonGate.ForcedFlagForTesting);

            ReFlyRevertButtonGate.Apply("test:force");

            Assert.True(FlightDriver.CanRevertToPostInit);
            Assert.True(ReFlyRevertButtonGate.ForcedFlagForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("ReFlyRevertButtonGate")
                && l.Contains("forced FlightDriver.CanRevertToPostInit=true")
                && l.Contains("sess=sess_force_test")
                && l.Contains("test:force"));
        }

        [Fact]
        public void Apply_ActiveMarker_FlagAlreadyTrue_LeavesForcedFlagFalse()
        {
            // Engine-set true (e.g. fresh PRELAUNCH launch). We must not claim
            // ownership of a flag we did not flip — otherwise the eventual
            // reset would clobber the engine's legitimate value.
            InstallScenario(marker: MakeMarker());
            FlightDriver.CanRevertToPostInit = true;
            ReFlyRevertButtonGate.ResetForTesting();

            ReFlyRevertButtonGate.Apply("test:already-true");

            Assert.True(FlightDriver.CanRevertToPostInit);
            Assert.False(ReFlyRevertButtonGate.ForcedFlagForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("CanRevertToPostInit already true")
                && l.Contains("no override needed"));
        }

        [Fact]
        public void Apply_MarkerCleared_AfterForce_ResetsFlagAndLogs()
        {
            // Step 1: force.
            var scenario = InstallScenario(marker: MakeMarker(sessionId: "sess_reset_test"));
            FlightDriver.CanRevertToPostInit = false;
            ReFlyRevertButtonGate.ResetForTesting();
            ReFlyRevertButtonGate.Apply("test:force-then-reset:force");
            Assert.True(ReFlyRevertButtonGate.ForcedFlagForTesting);
            logLines.Clear();

            // Step 2: marker cleared, Apply called from a clear-site.
            scenario.ActiveReFlySessionMarker = null;
            ReFlyRevertButtonGate.Apply("test:force-then-reset:clear");

            Assert.False(FlightDriver.CanRevertToPostInit);
            Assert.False(ReFlyRevertButtonGate.ForcedFlagForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("reset FlightDriver.CanRevertToPostInit=False")
                && l.Contains("re-fly marker cleared")
                && l.Contains("test:force-then-reset:clear"));
        }

        [Fact]
        public void Apply_MarkerCleared_NoPriorForce_DoesNotTouchFlag()
        {
            // Engine-set true that we never claimed → reset path must be a
            // no-op so a non-Parsek launch keeps its legitimate flag value.
            InstallScenario(marker: null);
            FlightDriver.CanRevertToPostInit = true;
            ReFlyRevertButtonGate.ResetForTesting();
            Assert.False(ReFlyRevertButtonGate.ForcedFlagForTesting);

            ReFlyRevertButtonGate.Apply("test:engine-set-true");

            Assert.True(FlightDriver.CanRevertToPostInit);
            Assert.False(ReFlyRevertButtonGate.ForcedFlagForTesting);
            // No reset log line — the gate did not fire.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("ReFlyRevertButtonGate")
                && l.Contains("reset FlightDriver.CanRevertToPostInit"));
        }

        [Fact]
        public void Apply_Idempotent_RepeatedActiveCalls_LogVerboseAfterFirstForce()
        {
            InstallScenario(marker: MakeMarker());
            FlightDriver.CanRevertToPostInit = false;
            ReFlyRevertButtonGate.ResetForTesting();

            ReFlyRevertButtonGate.Apply("test:first");
            int forceLogs = 0;
            foreach (var l in logLines)
            {
                if (l.Contains("forced FlightDriver.CanRevertToPostInit=true")
                    && l.Contains("test:first"))
                    forceLogs++;
            }
            Assert.Equal(1, forceLogs);

            logLines.Clear();
            ReFlyRevertButtonGate.Apply("test:second");

            Assert.True(FlightDriver.CanRevertToPostInit);
            Assert.True(ReFlyRevertButtonGate.ForcedFlagForTesting);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("forced FlightDriver.CanRevertToPostInit=true"));
            Assert.Contains(logLines, l =>
                l.Contains("CanRevertToPostInit already true")
                && l.Contains("test:second"));
        }

        // ---------- Subscribe / Unsubscribe ------------------------------

        [Fact]
        public void Subscribe_Then_Unsubscribe_AreIdempotent()
        {
            // Cannot probe GameEvents from xUnit (Unity-only static state),
            // but the no-op-on-second-call invariant is observable through
            // the log lines emitted around the subscribed-bool.
            ReFlyRevertButtonGate.Subscribe();
            ReFlyRevertButtonGate.Subscribe();
            ReFlyRevertButtonGate.Unsubscribe();
            ReFlyRevertButtonGate.Unsubscribe();

            int subscribeLogs = 0;
            int unsubscribeLogs = 0;
            foreach (var l in logLines)
            {
                if (l.Contains("subscribed to GameEvents.onFlightReady")
                    && !l.Contains("unsubscribed")) subscribeLogs++;
                if (l.Contains("unsubscribed from GameEvents.onFlightReady")) unsubscribeLogs++;
            }
            Assert.Equal(1, subscribeLogs);
            Assert.Equal(1, unsubscribeLogs);
        }
    }
}
