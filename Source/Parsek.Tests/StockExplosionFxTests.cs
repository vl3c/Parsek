using System;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    // Engine-side "Stock FXMonger.Explode" log line is covered by manual in-game verification;
    // it cannot run under xUnit because TriggerExplosionIfDestroyed needs a real GameObject
    // transform and the helper's default branch calls Object.FindObjectOfType<FXMonger>() —
    // both hit Unity native code the managed-only xUnit runtime cannot link.
    public class StockExplosionFxTests
    {
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
