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
            GhostMapVessels.ResetForTesting();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            GhostMapVessels.ResetForTesting();
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

        /// <summary>
        /// The Vessel.Load postfix acts on ghost map vessels only: a real vessel that
        /// loads must keep its colliders and stock tolerances.
        /// </summary>
        [Fact]
        public void LoadedInertGate_OnlyGhostMapPids()
        {
            const uint ghostPid = 3236074800u;
            const uint realPid = 2614652043u;
            GhostMapPresence.ghostMapVesselPids.Add(ghostPid);
            try
            {
                Assert.True(GhostVesselLoadedInertPatch.ShouldMakeInert(ghostPid));
                Assert.False(GhostVesselLoadedInertPatch.ShouldMakeInert(realPid));
            }
            finally
            {
                GhostMapPresence.ghostMapVesselPids.Remove(ghostPid);
            }
            Assert.False(GhostVesselLoadedInertPatch.ShouldMakeInert(ghostPid));
        }

        [Fact]
        public void LogGhostVesselMadeInert_NamesVesselPidAndCounts()
        {
            GhostMapVessels.LogGhostVesselMadeInert(
                3236074800u, "Ghost: Single Point Holder", 1, 2, "Vessel.Load");

            Assert.Contains(logLines, l => l.Contains("[GhostMap]")
                && l.Contains("Ghost vessel made physics-inert on load: vessel='Ghost: Single Point Holder' pid=3236074800 parts=1 collidersDisabled=2 for Vessel.Load"));
        }

        // ---- P3-05: the foreign-lane branch of the map icon click and the switch block ----

        private int CountForeignLaneLines()
        {
            int n = 0;
            foreach (var l in logLines)
                if (l.Contains("foreign-lane ghost")) n++;
            return n;
        }

        /// <summary>
        /// A ghost a foreign lane owns is refused at the map icon click before the own
        /// recording lookup: the decider says so and writes exactly one Verbose GhostMap line
        /// naming the site, the pid, the lane and the recording.
        /// </summary>
        [Fact]
        public void IconClick_ForeignLaneGhost_IsRefusedWithOneLine()
        {
            GhostMapVessels.RegisterForeignForTesting(4242u, "lane-B", "rec-B7");

            Assert.True(GhostIconClickPatch.ShouldRefuseForeignLaneGhost(4242u));

            Assert.Equal(1, CountForeignLaneLines());
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][GhostMap]")
                && l.Contains("foreign-lane ghost icon-click pid=4242 lane=lane-B recording=rec-B7"));
        }

        /// <summary>An own-timeline ghost and a pid that is no ghost keep today's path, silently.</summary>
        [Fact]
        public void IconClick_OwnTimelineGhostAndNonGhost_AreNotRefused()
        {
            GhostMapVessels.Register(4243u, GhostMapVesselOwner.OwnTimeline);
            GhostMapVessels.RegisterForeignForTesting(4242u, "lane-B", "rec-B7");

            Assert.False(GhostIconClickPatch.ShouldRefuseForeignLaneGhost(4243u));
            Assert.False(GhostIconClickPatch.ShouldRefuseForeignLaneGhost(9999u));
            Assert.Equal(0, CountForeignLaneLines());
        }

        /// <summary>
        /// The switch block refuses a foreign-lane ghost with one line naming its own site; a
        /// foreign owner with no recording id prints the "(none)" placeholder.
        /// </summary>
        [Fact]
        public void VesselSwitch_ForeignLaneGhost_IsRefusedWithOneLine()
        {
            GhostMapVessels.RegisterForeignForTesting(5150u, "lane-C", null);

            Assert.True(GhostVesselSwitchPatch.ShouldRefuseForeignLaneGhost(5150u));

            Assert.Equal(1, CountForeignLaneLines());
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][GhostMap]")
                && l.Contains("foreign-lane ghost vessel-switch pid=5150 lane=lane-C recording=(none)"));
        }

        [Fact]
        public void VesselSwitch_OwnTimelineGhostAndNonGhost_AreNotRefused()
        {
            GhostMapVessels.Register(5151u, GhostMapVesselOwner.OwnTimeline);
            GhostMapVessels.RegisterForeignForTesting(5150u, "lane-C", "rec-C1");

            Assert.False(GhostVesselSwitchPatch.ShouldRefuseForeignLaneGhost(5151u));
            Assert.False(GhostVesselSwitchPatch.ShouldRefuseForeignLaneGhost(9999u));
            Assert.Equal(0, CountForeignLaneLines());
        }

        /// <summary>
        /// The answer follows the owner record: once the foreign pid is unregistered (or the
        /// own-timeline teardown ran and kept it), the decision tracks the live record.
        /// </summary>
        [Fact]
        public void ForeignLaneDecision_FollowsTheOwnerRecord()
        {
            GhostMapVessels.RegisterForeignForTesting(6000u, "lane-D", "rec-D2");
            GhostMapVessels.UnregisterAllOwnTimeline();
            Assert.True(GhostVesselSwitchPatch.ShouldRefuseForeignLaneGhost(6000u));

            GhostMapVessels.Unregister(6000u);
            Assert.False(GhostVesselSwitchPatch.ShouldRefuseForeignLaneGhost(6000u));
            Assert.False(GhostIconClickPatch.ShouldRefuseForeignLaneGhost(6000u));
        }

        /// <summary>The query itself: the owner of a foreign pid, nothing for an own or unknown pid.</summary>
        [Fact]
        public void TryGetForeignOwner_AnswersOnlyForAForeignPid()
        {
            GhostMapVessels.Register(7001u, GhostMapVesselOwner.OwnTimeline);
            GhostMapVessels.RegisterForeignForTesting(7002u, "lane-E", "rec-E3");

            Assert.False(GhostMapVessels.TryGetForeignOwner(7001u, out _));
            Assert.False(GhostMapVessels.TryGetForeignOwner(7003u, out _));
            Assert.True(GhostMapVessels.TryGetForeignOwner(7002u, out GhostMapVesselOwner owner));
            Assert.Equal("lane-E", owner.LaneId);
            Assert.Equal("rec-E3", owner.RecordingId);
            Assert.False(owner.IsOwnTimeline);
        }
    }
}
