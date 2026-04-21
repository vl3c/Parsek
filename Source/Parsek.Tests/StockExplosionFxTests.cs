using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class StockExplosionFxTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<GameObject> createdObjects = new List<GameObject>();

        public StockExplosionFxTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            foreach (var obj in createdObjects)
            {
                if (obj != null)
                    UnityEngine.Object.DestroyImmediate(obj);
            }

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void TriggerExplosionIfDestroyed_LogsStockExplodeLine()
        {
            var engine = new GhostPlaybackEngine(null);
            var ghost = new GameObject("ghost-under-test");
            createdObjects.Add(ghost);
            ghost.transform.position = new Vector3(12f, 34f, 56f);

            var state = new GhostPlaybackState
            {
                ghost = ghost,
                reentryFxInfo = new ReentryFxInfo { vesselLength = 25f }
            };
            var traj = new MockTrajectory
            {
                VesselName = "Mock Boom",
                TerminalStateValue = TerminalState.Destroyed
            };

            engine.TriggerExplosionIfDestroyed(state, traj, 7, 1f);

            Assert.True(state.explosionFired);
            Assert.Contains(logLines, line =>
                line.Contains("[ExplosionFx]")
                && line.Contains("Stock FXMonger.Explode for ghost #7")
                && line.Contains("\"Mock Boom\"")
                && line.Contains("power=1.00"));
        }
    }
}
