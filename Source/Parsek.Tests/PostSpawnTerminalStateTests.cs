using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="ParsekScenario.ClearPostSpawnTerminalState"/> —
    /// clears stale terminal state after a spawn is undone by revert.
    /// </summary>
    [Collection("Sequential")]
    public class PostSpawnTerminalStateTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public PostSpawnTerminalStateTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ────────────────────────────────────────────────────────────
        //  Spawned + Recovered → cleared
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void SpawnedAndRecovered_ClearsTerminalState()
        {
            // Bug caught: after revert, a previously-spawned vessel that was recovered
            // still shows as Recovered — preventing it from being re-spawned.
            var rec = new Recording
            {
                VesselName = "Test Vessel",
                VesselSpawned = true,
                TerminalStateValue = TerminalState.Recovered
            };

            ParsekScenario.ClearPostSpawnTerminalState(rec, "test");

            Assert.Null(rec.TerminalStateValue);
        }

        // ────────────────────────────────────────────────────────────
        //  Spawned + Destroyed → cleared
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void SpawnedAndDestroyed_ClearsTerminalState()
        {
            // Bug caught: after revert, a vessel destroyed during the reverted flight
            // should not remain marked Destroyed — that state is stale.
            var rec = new Recording
            {
                VesselName = "Crash Vessel",
                VesselSpawned = true,
                TerminalStateValue = TerminalState.Destroyed
            };

            ParsekScenario.ClearPostSpawnTerminalState(rec, "revert-test");

            Assert.Null(rec.TerminalStateValue);
        }

        // ────────────────────────────────────────────────────────────
        //  Spawned + Orbiting → NOT cleared
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void SpawnedAndOrbiting_NotCleared()
        {
            // Bug caught: only Recovered and Destroyed are post-spawn terminal states
            // that should be cleared. Orbiting is a valid terminal state (vessel still
            // exists in orbit) and must NOT be cleared on revert.
            var rec = new Recording
            {
                VesselName = "Orbit Vessel",
                VesselSpawned = true,
                TerminalStateValue = TerminalState.Orbiting
            };

            ParsekScenario.ClearPostSpawnTerminalState(rec, "orbit-test");

            Assert.Equal(TerminalState.Orbiting, rec.TerminalStateValue);
        }

        [Fact]
        public void SpawnedAndLanded_NotCleared()
        {
            // Bug caught: Landed is not a terminal-reset state — vessel persists on surface
            var rec = new Recording
            {
                VesselName = "Landed Vessel",
                VesselSpawned = true,
                TerminalStateValue = TerminalState.Landed
            };

            ParsekScenario.ClearPostSpawnTerminalState(rec, "land-test");

            Assert.Equal(TerminalState.Landed, rec.TerminalStateValue);
        }

        [Fact]
        public void SpawnedAndSplashed_NotCleared()
        {
            // Bug caught: Splashed is not a terminal-reset state
            var rec = new Recording
            {
                VesselName = "Splash Vessel",
                VesselSpawned = true,
                TerminalStateValue = TerminalState.Splashed
            };

            ParsekScenario.ClearPostSpawnTerminalState(rec, "splash-test");

            Assert.Equal(TerminalState.Splashed, rec.TerminalStateValue);
        }

        [Fact]
        public void SpawnedAndSubOrbital_NotCleared()
        {
            // Bug caught: SubOrbital is not a terminal-reset state
            var rec = new Recording
            {
                VesselName = "SubOrbit Vessel",
                VesselSpawned = true,
                TerminalStateValue = TerminalState.SubOrbital
            };

            ParsekScenario.ClearPostSpawnTerminalState(rec, "sub-test");

            Assert.Equal(TerminalState.SubOrbital, rec.TerminalStateValue);
        }

        // ────────────────────────────────────────────────────────────
        //  NOT spawned → NOT cleared
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void NotSpawned_Recovered_NotCleared()
        {
            // Bug caught: if VesselSpawned is false, the terminal state was set
            // by the original recording, not by a spawn — must not be cleared.
            var rec = new Recording
            {
                VesselName = "Original Vessel",
                VesselSpawned = false,
                TerminalStateValue = TerminalState.Recovered
            };

            ParsekScenario.ClearPostSpawnTerminalState(rec, "no-spawn-test");

            Assert.Equal(TerminalState.Recovered, rec.TerminalStateValue);
        }

        [Fact]
        public void NotSpawned_Destroyed_NotCleared()
        {
            // Bug caught: Destroyed on a non-spawned vessel is the original recording
            // terminal state (e.g. crashed during the recorded flight) — must be preserved.
            var rec = new Recording
            {
                VesselName = "Crashed Vessel",
                VesselSpawned = false,
                TerminalStateValue = TerminalState.Destroyed
            };

            ParsekScenario.ClearPostSpawnTerminalState(rec, "crash-test");

            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
        }

        // ────────────────────────────────────────────────────────────
        //  No terminal state → no change
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void SpawnedButNoTerminalState_NoChange()
        {
            // Bug caught: null TerminalStateValue must not cause a crash or side effect
            var rec = new Recording
            {
                VesselName = "Active Vessel",
                VesselSpawned = true,
                TerminalStateValue = null
            };

            ParsekScenario.ClearPostSpawnTerminalState(rec, "active-test");

            Assert.Null(rec.TerminalStateValue);
        }

        // ────────────────────────────────────────────────────────────
        //  Log message includes context string
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void LogMessage_IncludesContextAndVesselName()
        {
            // Bug caught: if the context parameter is not included in the log,
            // debugging post-spawn issues becomes harder (which call site triggered it?)
            logLines.Clear();

            var rec = new Recording
            {
                VesselName = "MyRocket",
                VesselSpawned = true,
                TerminalStateValue = TerminalState.Recovered
            };

            ParsekScenario.ClearPostSpawnTerminalState(rec, "revert-handler");

            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") &&
                l.Contains("revert-handler") &&
                l.Contains("MyRocket") &&
                l.Contains("Recovered"));
        }

        [Fact]
        public void NoLog_WhenNotCleared()
        {
            // Bug caught: logging a "cleared" message when nothing was actually
            // cleared would be misleading in the log
            logLines.Clear();

            var rec = new Recording
            {
                VesselName = "NotSpawnedVessel",
                VesselSpawned = false,
                TerminalStateValue = TerminalState.Destroyed
            };

            ParsekScenario.ClearPostSpawnTerminalState(rec, "should-not-log");

            Assert.DoesNotContain(logLines, l =>
                l.Contains("Clearing post-spawn"));
        }

        [Fact]
        public void DefaultContext_UsedWhenOmitted()
        {
            // Bug caught: the default parameter "recording" must be used when
            // context is not specified explicitly
            logLines.Clear();

            var rec = new Recording
            {
                VesselName = "DefaultCtx",
                VesselSpawned = true,
                TerminalStateValue = TerminalState.Recovered
            };

            ParsekScenario.ClearPostSpawnTerminalState(rec);

            Assert.Contains(logLines, l =>
                l.Contains("for recording 'DefaultCtx'"));
        }
    }
}
