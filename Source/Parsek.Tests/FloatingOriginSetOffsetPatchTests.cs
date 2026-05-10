using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class FloatingOriginSetOffsetPatchTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public FloatingOriginSetOffsetPatchTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ReFlySettleStabilityTracker.Reset();
        }

        public void Dispose()
        {
            ReFlySettleStabilityTracker.Reset();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void RecordFloatingOriginShift_LogsPatchInputsAndFrame()
        {
            var refPos = new Vector3d(1.0, 2.0, 3.0);
            var nonFrame = new Vector3d(4.0, 5.0, 6.0);
            ReFlySettleStabilityTracker.RecordSettleCleared("rec-focus", frame: 40);

            ReFlySettleStabilityTracker.RecordFloatingOriginShift(
                refPos,
                nonFrame,
                frame: 42,
                realtimeSinceStartup: 12.345f);

            Assert.Contains(logLines, line =>
                line.Contains("[Parsek][INFO][ReFlySettle]")
                && line.Contains("FloatingOrigin.setOffset")
                && line.Contains("refPos=")
                && line.Contains("nonFrame=")
                && line.Contains("offsetNonKrakensbane=")
                && line.Contains("wallclock=12.345")
                && line.Contains("frame=42"));
            Assert.Single(logLines.Where(line => line.Contains("FloatingOrigin.setOffset")));
        }
    }
}
