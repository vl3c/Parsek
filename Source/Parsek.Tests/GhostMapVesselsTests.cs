using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The ghost map vessel guard state (<see cref="GhostMapVessels"/>): the pid set, its owner
    /// record, the teardown depth, and the <see cref="GhostMapPresence"/> entry points that
    /// forward to them.
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
        public void OwnTimeline_IsOwnTimeline_AForeignOwnerIsNot()
        {
            Assert.True(GhostMapVesselOwner.OwnTimeline.IsOwnTimeline);
            Assert.Null(GhostMapVesselOwner.OwnTimeline.LaneId);
            Assert.Null(GhostMapVesselOwner.OwnTimeline.RecordingId);

            var foreign = new GhostMapVesselOwner("lane-b", "rec-b");
            Assert.False(foreign.IsOwnTimeline);
            Assert.Equal("lane-b", foreign.LaneId);
            Assert.Equal("rec-b", foreign.RecordingId);
        }

        [Fact]
        public void Register_OwnTimeline_AddsThePidAndRecordsNoOwner()
        {
            GhostMapVessels.Register(10, GhostMapVesselOwner.OwnTimeline);

            Assert.True(GhostMapVessels.IsGhostMapVessel(10));
            Assert.Equal(0, GhostMapVessels.ForeignOwnerCountForTesting);
            Assert.False(GhostMapVessels.TryGetForeignOwnerForTesting(10, out _));
        }

        [Fact]
        public void RegisterForeign_AddsThePidAndItsOwner()
        {
            GhostMapVessels.RegisterForeignForTesting(30, "lane-b", "rec-b");

            Assert.True(GhostMapVessels.IsGhostMapVessel(30));
            Assert.True(GhostMapPresence.IsGhostMapVessel(30));
            Assert.Equal(1, GhostMapVessels.ForeignOwnerCountForTesting);
            Assert.True(GhostMapVessels.TryGetForeignOwnerForTesting(30, out GhostMapVesselOwner owner));
            Assert.Equal("lane-b", owner.LaneId);
            Assert.Equal("rec-b", owner.RecordingId);
        }

        [Fact]
        public void OwnTeardown_KeepsTheForeignPidAndItsOwner()
        {
            GhostMapVessels.Register(10, GhostMapVesselOwner.OwnTimeline);
            GhostMapVessels.Register(20, GhostMapVesselOwner.OwnTimeline);
            GhostMapVessels.RegisterForeignForTesting(30, "lane-b", "rec-b");

            GhostMapVessels.UnregisterAllOwnTimeline();

            Assert.False(GhostMapVessels.IsGhostMapVessel(10));
            Assert.False(GhostMapVessels.IsGhostMapVessel(20));
            Assert.True(GhostMapVessels.IsGhostMapVessel(30));
            Assert.Equal(1, GhostMapVessels.RegisteredCount);
            Assert.True(GhostMapVessels.TryGetForeignOwnerForTesting(30, out GhostMapVesselOwner owner));
            Assert.Equal("lane-b", owner.LaneId);
            Assert.Equal("rec-b", owner.RecordingId);
        }

        [Fact]
        public void OwnTeardown_WithNoForeignOwner_EmptiesTheSet_KeepsInstance()
        {
            HashSet<uint> set = GhostMapVessels.Pids;
            GhostMapVessels.Register(10, GhostMapVesselOwner.OwnTimeline);
            GhostMapVessels.Register(20, GhostMapVesselOwner.OwnTimeline);

            GhostMapVessels.UnregisterAllOwnTimeline();

            Assert.Same(set, GhostMapVessels.Pids);
            Assert.Empty(GhostMapVessels.Pids);
            Assert.Equal(0, GhostMapVessels.ForeignOwnerCountForTesting);
        }

        [Fact]
        public void Unregister_ForeignPid_DropsThePidAndItsOwner()
        {
            GhostMapVessels.Register(10, GhostMapVesselOwner.OwnTimeline);
            GhostMapVessels.RegisterForeignForTesting(30, "lane-b", "rec-b");

            GhostMapVessels.Unregister(30);

            Assert.False(GhostMapVessels.IsGhostMapVessel(30));
            Assert.False(GhostMapVessels.TryGetForeignOwnerForTesting(30, out _));
            Assert.Equal(0, GhostMapVessels.ForeignOwnerCountForTesting);
            Assert.True(GhostMapVessels.IsGhostMapVessel(10));

            // With the owner gone the own teardown takes everything left.
            GhostMapVessels.UnregisterAllOwnTimeline();
            Assert.Empty(GhostMapVessels.Pids);
        }

        [Fact]
        public void ResetForTesting_ClearsTheForeignOwners()
        {
            GhostMapVessels.RegisterForeignForTesting(30, "lane-b", "rec-b");
            GhostMapVessels.ResetForTesting();
            Assert.Empty(GhostMapVessels.Pids);
            Assert.Equal(0, GhostMapVessels.ForeignOwnerCountForTesting);

            GhostMapVessels.RegisterForeignForTesting(31, "lane-c", "rec-c");
            GhostMapPresence.ResetForTesting();
            Assert.Empty(GhostMapVessels.Pids);
            Assert.Equal(0, GhostMapVessels.ForeignOwnerCountForTesting);
        }

        [Fact]
        public void OwnerCalls_WriteNoLogLine()
        {
            GhostMapVessels.Register(10, GhostMapVesselOwner.OwnTimeline);
            GhostMapVessels.RegisterForeignForTesting(30, "lane-b", "rec-b");
            GhostMapVessels.Unregister(10);
            GhostMapVessels.UnregisterAllOwnTimeline();
            GhostMapVessels.Unregister(30);

            Assert.Empty(logLines);
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
