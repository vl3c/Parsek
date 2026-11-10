using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;
using static Parsek.Tests.BackgroundRecorderCharacterizationSupport;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests of the units extracted from
    /// <see cref="BackgroundRecorder.HandleBackgroundVesselSplit"/>: the parent continuation
    /// build, its identity log lines and the post-split reseed.
    /// </summary>
    [Collection("Sequential")]
    public class HandleBackgroundVesselSplitUnitsTests : IDisposable
    {
        private const uint ParentPid = 5401u;
        private const string ParentRecId = "split-units-parent";
        private const string TreeId = "split-units-tree";

        private readonly List<string> logLines = new List<string>();

        public HandleBackgroundVesselSplitUnitsTests()
        {
            // ParsekSettings' static constructor logs once per AppDomain; run it before the
            // sink is installed so an exact-line assertion does not depend on test order.
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ParsekSettings).TypeHandle);
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ClockOverrideForTesting = () => 100.0;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private sealed class Fixture
        {
            public RecordingTree Tree;
            public BackgroundRecorder Bg;
            public Recording Parent;
            public BranchPoint Bp;
        }

        private static Fixture Build(bool debris = false, string parentGuid = null)
        {
            var tree = new RecordingTree { Id = TreeId };
            var parent = new Recording
            {
                RecordingId = ParentRecId,
                TreeId = TreeId,
                VesselName = "Closed Leg",
                VesselPersistentId = ParentPid,
                IsDebris = debris,
                RecordedVesselGuid = parentGuid,
            };
            tree.Recordings[ParentRecId] = parent;
            tree.BackgroundMap[ParentPid] = ParentRecId;
            return new Fixture
            {
                Tree = tree,
                Bg = new BackgroundRecorder(tree),
                Parent = parent,
                Bp = new BranchPoint { Id = "split-units-bp", UT = 150.0 },
            };
        }

        private static Vessel LiveParent(Guid id, string name)
        {
            Vessel v = Vessel(ParentPid, alive: true, body: null);
            v.id = id;
            v.vesselName = name;
            return v;
        }

        // ---------------- BuildBackgroundSplitParentContinuation

        [Fact]
        public void Continuation_ParentVesselGone_NullAndTreeUntouched()
        {
            Fixture f = Build();
            Recording cont = f.Bg.BuildBackgroundSplitParentContinuationForTesting(
                f.Bp, f.Parent, ParentPid, 150.0, null, out string liveGuid);

            Assert.Null(cont);
            Assert.Null(liveGuid);
            Assert.Empty(f.Bp.ChildRecordingIds);
            Assert.Equal(ParentRecId, f.Tree.BackgroundMap[ParentPid]);
            Assert.Single(f.Tree.Recordings);
        }

        [Fact]
        public void Continuation_LiveParent_AttachedWithLiveGuidAndName()
        {
            Fixture f = Build(parentGuid: "0000000000000000000000000000abcd");
            var id = new Guid("11112222-3333-4444-5555-666677778888");
            Recording cont = f.Bg.BuildBackgroundSplitParentContinuationForTesting(
                f.Bp, f.Parent, ParentPid, 150.0, LiveParent(id, "Live Name"), out string liveGuid);

            Assert.NotNull(cont);
            Assert.Equal(id.ToString("N"), liveGuid);
            Assert.Equal(liveGuid, cont.RecordedVesselGuid);
            Assert.Equal("Live Name", cont.VesselName);
            Assert.Equal(ParentPid, cont.VesselPersistentId);
            Assert.Equal(TreeId, cont.TreeId);
            Assert.Equal("split-units-bp", cont.ParentBranchPointId);
            Assert.Equal(150.0, cont.ExplicitStartUT);
            Assert.NotNull(cont.Controllers);
            Assert.Empty(cont.Controllers);
            Assert.Equal(32, cont.RecordingId.Length);
            Assert.Equal(cont.RecordingId, f.Bp.ChildRecordingIds[0]);
            Assert.Equal(cont.RecordingId, f.Tree.BackgroundMap[ParentPid]);
            Assert.Same(cont, f.Tree.Recordings[cont.RecordingId]);
        }

        [Fact]
        public void Continuation_LiveParentWithoutGuid_KeepsTheStoredGuid()
        {
            Fixture f = Build(parentGuid: "0000000000000000000000000000abcd");
            Recording cont = f.Bg.BuildBackgroundSplitParentContinuationForTesting(
                f.Bp, f.Parent, ParentPid, 150.0, LiveParent(Guid.Empty, "Live Name"), out string liveGuid);

            Assert.Null(liveGuid);
            Assert.Equal("0000000000000000000000000000abcd", cont.RecordedVesselGuid);
        }

        // ---------------- LogParentContinuationIdentity

        private static Recording Cont(string guid, string name) => new Recording
        {
            RecordingId = "cont-id",
            RecordedVesselGuid = guid,
            VesselName = name,
        };

        [Fact]
        public void Identity_GuidsDiverge_InfoLine()
        {
            Fixture f = Build(parentGuid: "aaaa");
            BackgroundRecorder.LogParentContinuationIdentity(Cont("bbbb", "Closed Leg"), f.Parent, ParentRecId, "bbbb");

            Assert.Equal(
                "[Parsek][INFO][BgRecorder] Parent continuation launch guid diverges from the continued segment: " +
                "recId=cont-id parentRecId=split-units-parent live=bbbb parentRecGuid=aaaa stamped=bbbb " +
                "(live vessel wins)",
                Assert.Single(logLines));
        }

        [Fact]
        public void Identity_SameGuid_VerboseStampLine()
        {
            Fixture f = Build(parentGuid: "aaaa");
            BackgroundRecorder.LogParentContinuationIdentity(Cont("aaaa", "Closed Leg"), f.Parent, ParentRecId, "aaaa");

            Assert.Equal(
                "[Parsek][VERBOSE][BgRecorder] Parent continuation launch guid stamped: recId=cont-id " +
                "guid=aaaa live=aaaa parentRecGuid=aaaa",
                Assert.Single(logLines));
        }

        [Fact]
        public void Identity_NoGuids_VerboseLineNamesNone()
        {
            Fixture f = Build();
            BackgroundRecorder.LogParentContinuationIdentity(Cont(null, "Closed Leg"), f.Parent, ParentRecId, null);

            Assert.Equal(
                "[Parsek][VERBOSE][BgRecorder] Parent continuation launch guid stamped: recId=cont-id " +
                "guid=(none) live=(none) parentRecGuid=(none)",
                Assert.Single(logLines));
        }

        [Fact]
        public void Identity_NameChanged_AlsoLogsTheCapturedName()
        {
            Fixture f = Build(parentGuid: "aaaa");
            BackgroundRecorder.LogParentContinuationIdentity(Cont("aaaa", "Renamed"), f.Parent, ParentRecId, "aaaa");

            Assert.Equal(2, logLines.Count);
            Assert.Equal(
                "[Parsek][INFO][BgRecorder] Parent continuation captured the live vessel name: recId=cont-id " +
                "name='Renamed' (closed parent leg 'split-units-parent' keeps 'Closed Leg')",
                logLines[1]);
        }

        // ---------------- ReseedParentContinuationAfterSplit

        private static TrajectoryPoint Boundary(double ut) => new TrajectoryPoint
        {
            ut = ut, latitude = 1.0, longitude = 2.0, altitude = 3000.0, bodyName = "Kerbin",
        };

        [Fact]
        public void Reseed_BoundaryPoint_QueuedAndBackgroundedNoTtl()
        {
            Fixture f = Build();
            f.Bg.ReseedParentContinuationAfterSplitForTesting(f.Parent, ParentPid, 150.0, Boundary(150.0),
                null, null);

            // The parent vessel is not found headless, so the seed stays pending for the
            // next load and the recorder holds a minimal on-rails state.
            TrajectoryPoint? queued = f.Bg.PeekPendingInitialTrajectoryPointForTesting(ParentPid);
            Assert.True(queued.HasValue);
            Assert.Equal(150.0, queued.Value.ut);
            Assert.Equal(3000.0, queued.Value.altitude);
            Assert.Contains(logLines, l => l == "[Parsek][VERBOSE][BgRecorder] Queued initial trajectory point for pid=5401: ut=150.00");
            Assert.Contains(logLines, l => l == "[Parsek][INFO][BgRecorder] Vessel backgrounded (not found): pid=5401 recId=split-units-parent");
            Assert.Equal(0, f.Bg.DebrisTTLCount);
        }

        [Fact]
        public void Reseed_NoBoundaryNoVessel_NothingQueued()
        {
            Fixture f = Build();
            f.Bg.ReseedParentContinuationAfterSplitForTesting(f.Parent, ParentPid, 150.0, null, null, null);

            Assert.False(f.Bg.PeekPendingInitialTrajectoryPointForTesting(ParentPid).HasValue);
            Assert.DoesNotContain(logLines, l => l.Contains("Queued initial trajectory point"));
        }

        [Fact]
        public void Reseed_DebrisParent_ArmsTheTtl()
        {
            Fixture f = Build(debris: true);
            f.Bg.ReseedParentContinuationAfterSplitForTesting(f.Parent, ParentPid, 150.0, Boundary(150.0),
                null, null);

            Assert.Equal(150.0 + BackgroundRecorder.DebrisTTLSeconds, f.Bg.GetDebrisTTLExpiryForTesting(ParentPid));
            Assert.Equal(
                "[Parsek][INFO][BgRecorder] Parent continuation is debris, TTL set: parentPid=5401 expiry=" +
                (150.0 + BackgroundRecorder.DebrisTTLSeconds).ToString("F1"),
                logLines[logLines.Count - 1]);
        }
    }
}
