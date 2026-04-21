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

        [Fact]
        public void TryTriggerStockExplosionFx_ReturnsFalseWhenFxMongerIsUnavailable()
        {
            bool explodeCalled = false;

            bool queued = GhostVisualBuilder.TryTriggerStockExplosionFx(
                Vector3.zero,
                0.25,
                out string failureReason,
                isFxMongerAvailable: () => false,
                explode: (pos, power) => explodeCalled = true);

            Assert.False(queued);
            Assert.False(explodeCalled);
            Assert.Equal("no live FXMonger instance", failureReason);
        }

        [Fact]
        public void TryTriggerStockExplosionFx_ReturnsFalseWhenStockExplodeThrows()
        {
            bool explodeCalled = false;

            bool queued = GhostVisualBuilder.TryTriggerStockExplosionFx(
                Vector3.one,
                0.5,
                out string failureReason,
                isFxMongerAvailable: () => true,
                explode: (pos, power) =>
                {
                    explodeCalled = true;
                    throw new InvalidOperationException("boom");
                });

            Assert.False(queued);
            Assert.True(explodeCalled);
            Assert.Equal("boom", failureReason);
        }

        [Fact]
        public void TryTriggerStockExplosionFx_ReturnsTrueWhenStockExplodeRuns()
        {
            Vector3 capturedPosition = Vector3.zero;
            double capturedPower = -1;

            bool queued = GhostVisualBuilder.TryTriggerStockExplosionFx(
                new Vector3(1f, 2f, 3f),
                0.75,
                out string failureReason,
                isFxMongerAvailable: () => true,
                explode: (pos, power) =>
                {
                    capturedPosition = pos;
                    capturedPower = power;
                });

            Assert.True(queued);
            Assert.Null(failureReason);
            Assert.Equal(new Vector3(1f, 2f, 3f), capturedPosition);
            Assert.Equal(0.75, capturedPower, 6);
        }
    }
}
