using System;
using System.Collections.Generic;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class GhostVesselLoadPatchTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostVesselLoadPatchTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        /// <summary>
        /// KSP retries Vessel.GoOffRails on every FixedUpdate while a ghost is in
        /// physics range, and the Harmony prefix returns false to keep it on rails.
        /// The 2026-04-26_2357_newest playtest captured 2941 raw Verbose lines for
        /// a single ghost PID in 25 seconds (~117 Hz). LogBlockedOffRails must
        /// coalesce the per-frame stream so the first block emits and identical
        /// repeats stay silent.
        /// </summary>
        [Fact]
        public void LogBlockedOffRails_RepeatedCallsSamePid_EmitOnceForStableName()
        {
            GhostVesselLoadPatch.LogBlockedOffRails(940887686u, "Ghost: Kerbal X");

            int initialCount = 0;
            for (int i = 0; i < logLines.Count; i++)
            {
                if (logLines[i].Contains("[GhostMap]")
                    && logLines[i].Contains("Blocked GoOffRails")
                    && logLines[i].Contains("pid=940887686"))
                    initialCount++;
            }
            Assert.Equal(1, initialCount);

            logLines.Clear();
            for (int i = 0; i < 100; i++)
                GhostVesselLoadPatch.LogBlockedOffRails(940887686u, "Ghost: Kerbal X");

            int respam = 0;
            foreach (var l in logLines)
            {
                if (l.Contains("[GhostMap]") && l.Contains("Blocked GoOffRails"))
                    respam++;
            }
            Assert.Equal(0, respam);
        }

        /// <summary>
        /// Each ghost PID gets its own VerboseOnChange identity slot. Two
        /// distinct ghosts must each emit on first block; cross-PID alternation
        /// must not ping-pong like a shared identity would.
        /// </summary>
        [Fact]
        public void LogBlockedOffRails_DistinctPids_EachEmitsOnceAndStaysSilent()
        {
            GhostVesselLoadPatch.LogBlockedOffRails(100u, "Ghost: A");
            GhostVesselLoadPatch.LogBlockedOffRails(200u, "Ghost: B");

            int firstA = 0, firstB = 0;
            foreach (var l in logLines)
            {
                if (!l.Contains("Blocked GoOffRails")) continue;
                if (l.Contains("pid=100")) firstA++;
                else if (l.Contains("pid=200")) firstB++;
            }
            Assert.Equal(1, firstA);
            Assert.Equal(1, firstB);

            logLines.Clear();
            for (int i = 0; i < 50; i++)
            {
                GhostVesselLoadPatch.LogBlockedOffRails(100u, "Ghost: A");
                GhostVesselLoadPatch.LogBlockedOffRails(200u, "Ghost: B");
            }

            int respam = 0;
            foreach (var l in logLines)
            {
                if (l.Contains("[GhostMap]") && l.Contains("Blocked GoOffRails"))
                    respam++;
            }
            Assert.Equal(0, respam);
        }
    }
}
