using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The ghost map vessel guard state (<see cref="GhostMapVessels"/>): the pid set and the
    /// teardown depth, and the <see cref="GhostMapPresence"/> entry points that forward to them.
    /// </summary>
    [Collection("Sequential")]
    public class GhostMapVesselsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostMapVesselsTests()
        {
            GhostMapPresence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            GhostMapPresence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void PresenceField_IsTheSameSetInstance()
        {
            Assert.Same(GhostMapVessels.Pids, GhostMapPresence.ghostMapVesselPids);

            GhostMapPresence.ghostMapVesselPids.Add(4242);
            Assert.Contains(4242u, GhostMapVessels.Pids);
            Assert.True(GhostMapVessels.IsGhostMapVessel(4242));
        }

        [Fact]
        public void Register_ThenUnregister_TracksMembershipAndCount()
        {
            Assert.False(GhostMapVessels.IsGhostMapVessel(111));
            Assert.Equal(0, GhostMapVessels.RegisteredCount);

            GhostMapVessels.Pids.Add(111);
            GhostMapVessels.Pids.Add(222);
            Assert.True(GhostMapVessels.IsGhostMapVessel(111));
            Assert.True(GhostMapVessels.IsGhostMapVessel(222));
            Assert.False(GhostMapVessels.IsGhostMapVessel(333));
            Assert.Equal(2, GhostMapVessels.RegisteredCount);
            Assert.True(GhostMapPresence.IsGhostMapVessel(111));
            Assert.Equal(2, GhostMapPresence.RegisteredGhostMapVesselCount);

            GhostMapVessels.Pids.Remove(111);
            Assert.False(GhostMapVessels.IsGhostMapVessel(111));
            Assert.False(GhostMapPresence.IsGhostMapVessel(111));
            Assert.True(GhostMapVessels.IsGhostMapVessel(222));
            Assert.Equal(1, GhostMapVessels.RegisteredCount);
            Assert.Equal(1, GhostMapPresence.RegisteredGhostMapVesselCount);
        }

        [Fact]
        public void TestSeam_RegistersIntoTheSharedSet()
        {
            GhostMapPresence.RegisterGhostMapVesselPidForTesting(777);
            Assert.True(GhostMapVessels.IsGhostMapVessel(777));
            Assert.Equal(1, GhostMapVessels.RegisteredCount);
        }

        [Fact]
        public void Teardown_Nests_AndFloorsAtZero()
        {
            Assert.False(GhostMapVessels.IsTeardownInProgress);

            GhostMapVessels.BeginTeardown();
            GhostMapVessels.BeginTeardown();
            Assert.True(GhostMapVessels.IsTeardownInProgress);
            Assert.True(GhostMapPresence.IsGhostTeardownInProgress);

            GhostMapVessels.EndTeardown();
            Assert.True(GhostMapVessels.IsTeardownInProgress);

            GhostMapVessels.EndTeardown();
            Assert.False(GhostMapVessels.IsTeardownInProgress);

            // An unbalanced end floors at zero, so the next begin raises the flag again.
            GhostMapVessels.EndTeardown();
            Assert.False(GhostMapVessels.IsTeardownInProgress);
            GhostMapVessels.BeginTeardown();
            Assert.True(GhostMapVessels.IsTeardownInProgress);
            GhostMapVessels.EndTeardown();
            Assert.False(GhostMapVessels.IsTeardownInProgress);
        }

        [Fact]
        public void PresenceTeardownEntryPoints_DriveTheSameDepth()
        {
            GhostMapPresence.BeginGhostTeardown();
            Assert.True(GhostMapVessels.IsTeardownInProgress);
            GhostMapVessels.BeginTeardown();
            GhostMapPresence.EndGhostTeardown();
            Assert.True(GhostMapPresence.IsGhostTeardownInProgress);
            GhostMapVessels.EndTeardown();
            Assert.False(GhostMapPresence.IsGhostTeardownInProgress);
        }

        [Fact]
        public void ResetForTesting_ClearsSetAndDepth_KeepsInstance()
        {
            HashSet<uint> set = GhostMapVessels.Pids;
            set.Add(1);
            set.Add(2);
            GhostMapVessels.BeginTeardown();
            GhostMapVessels.BeginTeardown();

            GhostMapVessels.ResetForTesting();

            Assert.Same(set, GhostMapVessels.Pids);
            Assert.Empty(GhostMapVessels.Pids);
            Assert.Equal(0, GhostMapVessels.RegisteredCount);
            Assert.False(GhostMapVessels.IsTeardownInProgress);
            GhostMapVessels.BeginTeardown();
            GhostMapVessels.EndTeardown();
            Assert.False(GhostMapVessels.IsTeardownInProgress);
        }

        [Fact]
        public void PresenceResetForTesting_ResetsTheGuardState()
        {
            GhostMapVessels.Pids.Add(9);
            GhostMapVessels.BeginTeardown();

            GhostMapPresence.ResetForTesting();

            Assert.Empty(GhostMapVessels.Pids);
            Assert.False(GhostMapVessels.IsTeardownInProgress);
        }

        [Fact]
        public void GuardCalls_WriteNoLogLine()
        {
            GhostMapVessels.Pids.Add(5);
            GhostMapVessels.IsGhostMapVessel(5);
            GhostMapPresence.IsGhostMapVessel(5);
            GhostMapVessels.BeginTeardown();
            GhostMapPresence.BeginGhostTeardown();
            bool inProgress = GhostMapPresence.IsGhostTeardownInProgress;
            int count = GhostMapPresence.RegisteredGhostMapVesselCount;
            GhostMapPresence.EndGhostTeardown();
            GhostMapVessels.EndTeardown();

            Assert.True(inProgress);
            Assert.Equal(1, count);
            Assert.Empty(logLines);
        }
    }
}
