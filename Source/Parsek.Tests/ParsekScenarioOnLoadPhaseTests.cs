using System.Collections.Generic;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests of the cold-load phases extracted from <c>ParsekScenario.OnLoad</c>
    /// (<c>OnLoadPhase*</c>). Their order inside OnLoad is pinned by
    /// <see cref="ParsekScenarioOnLoadCharacterizationTests"/>; these cells pin what each one
    /// does. Touches shared static state (ParsekLog, RouteStore, MissionStore, the committed
    /// trees, the crew replacement map, HighLogic.LoadedScene), hence Sequential.
    /// </summary>
    [Collection("Sequential")]
    public class ParsekScenarioOnLoadPhaseTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly GameScenes priorScene;

        public ParsekScenarioOnLoadPhaseTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RouteStore.ResetForTesting();
            MissionStore.ResetForTesting();
            RecordingStore.ClearCommittedTreesInternal();
            CrewReservationManager.ResetReplacementsForTesting();
            priorScene = HighLogic.LoadedScene;
        }

        public void Dispose()
        {
            HighLogic.LoadedScene = priorScene;
            CrewReservationManager.ResetReplacementsForTesting();
            RouteStore.ResetForTesting();
            MissionStore.ResetForTesting();
            RecordingStore.ClearCommittedTreesInternal();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Recording Rec(string id, double startUT, double endUT)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "V-" + id,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
            };
        }

        private static ConfigNode Snapshot()
        {
            var node = new ConfigNode("VESSEL");
            node.AddNode("PART").AddValue("crew", "Jebediah Kerman");
            return node;
        }

        // ---- route-loop-reconcile ----

        [Fact]
        public void RouteLoopReconcile_ClearsEveryRouteBoundTreeAndCountsThem()
        {
            RouteStore.AddRoute(new RouteFixtureBuilder()
                .WithId("route-A").WithName("route-A").WithStatus(RouteStatus.Active)
                .WithBackingMissionTreeId("tree-X").WithRecordingId("rec-X")
                .WithSourceRef(new RouteSourceRef { RecordingId = "rec-X", TreeId = "tree-X" })
                .WithSourceRef(new RouteSourceRef { RecordingId = "rec-Y", TreeId = "tree-Y" })
                .Build());

            ParsekScenario.OnLoadPhaseReconcileRouteBoundManualLoops(250.0);

            Assert.Contains(logLines, l => l.Contains("[RouteGuard]")
                && l.Contains("OnLoad route-loop reconcile: cleared manual loops on 2 route-bound tree(s)"));
            Assert.Contains(logLines, l => l.Contains("[RouteGuard]") && l.Contains("tree=tree-X"));
            Assert.Contains(logLines, l => l.Contains("[RouteGuard]") && l.Contains("tree=tree-Y"));
        }

        [Fact]
        public void RouteLoopReconcile_NoRoutes_LogsZero()
        {
            ParsekScenario.OnLoadPhaseReconcileRouteBoundManualLoops(250.0);

            Assert.Contains(logLines, l => l.Contains("[RouteGuard]")
                && l.Contains("cleared manual loops on 0 route-bound tree(s)"));
        }

        // ---- load-summary ----

        [Fact]
        public void LoadSummary_BucketsAgainstTheColdClockAndListsInProgressAndCrew()
        {
            CrewReservationManager.SeedReplacementForTesting("Jebediah Kerman", "Agasel Kerman");
            var recordings = new List<Recording>
            {
                Rec("future", 200, 300),
                Rec("running", 50, 150),
                Rec("past", 10, 20),
                Rec("edge", 100, 100),   // loadUT == EndUT counts as in progress
            };

            ParsekScenario.OnLoadPhaseLogColdLoadSummary(recordings, 100.0);

            Assert.Contains(logLines, l => l.Contains("[INFO]") && l.Contains("[Scenario]")
                && l.Contains("UT: 100, 4 recording(s)"));
            Assert.Contains(logLines, l => l.Contains("[VERBOSE]")
                && l.Contains("Load summary buckets: future=1 in-progress=2 past=1"));
            Assert.Contains(logLines, l => l.Contains("#1: \"V-running\"") && l.Contains("IN PROGRESS (50%)"));
            Assert.Contains(logLines, l => l.Contains("#3: \"V-edge\"") && l.Contains("IN PROGRESS")
                && !l.Contains("%"));
            Assert.Contains(logLines, l => l.Contains("Crew reservations active (1):"));
            Assert.Contains(logLines, l => l.Contains("Jebediah Kerman -> replacement: Agasel Kerman"));
        }

        [Fact]
        public void LoadSummary_NothingInProgressAndNoCrew_LogsOnlyTheSummaryAndBuckets()
        {
            ParsekScenario.OnLoadPhaseLogColdLoadSummary(
                new List<Recording> { Rec("past", 10, 20) }, 100.0);

            Assert.Contains(logLines, l => l.Contains("Load summary buckets: future=0 in-progress=0 past=1"));
            Assert.DoesNotContain(logLines, l => l.Contains("IN PROGRESS"));
            Assert.DoesNotContain(logLines, l => l.Contains("Crew reservations active"));
        }

        // ---- crew-auto-unreserve ----

        [Fact]
        public void AutoUnreserve_OutsideSpaceCenter_ClearsOnlyUnspawnedNonLoopRecordingsPastEnd()
        {
            HighLogic.LoadedScene = GameScenes.FLIGHT;
            Recording due = Rec("due", 10, 90);
            due.VesselSnapshot = Snapshot();
            Recording looping = Rec("loop", 10, 90);
            looping.VesselSnapshot = Snapshot();
            looping.LoopPlayback = true;
            Recording notYet = Rec("notyet", 10, 100);   // currentUT > EndUT is strict
            notYet.VesselSnapshot = Snapshot();
            Recording spawned = Rec("spawned", 10, 90);
            spawned.VesselSnapshot = Snapshot();
            spawned.VesselSpawned = true;
            Recording noSnapshot = Rec("nosnap", 10, 90);
            var recordings = new List<Recording> { due, looping, notYet, spawned, noSnapshot };

            ParsekScenario.OnLoadPhaseAutoUnreserveCrewPastEndUT(recordings, 100.0);

            Assert.Null(due.VesselSnapshot);
            Assert.True(due.VesselSpawned);
            Assert.NotNull(looping.VesselSnapshot);
            Assert.False(looping.VesselSpawned);
            Assert.NotNull(notYet.VesselSnapshot);
            Assert.False(notYet.VesselSpawned);
            Assert.NotNull(spawned.VesselSnapshot);
            Assert.False(noSnapshot.VesselSpawned);
            Assert.Single(logLines.FindAll(l => l.Contains("Auto-unreserved crew for recording")));
            Assert.Contains(logLines, l => l.Contains("[Scenario]")
                && l.Contains("Auto-unreserved crew for recording #0 (V-due)")
                && l.Contains("EndUT passed without spawn"));
        }

        [Fact]
        public void AutoUnreserve_AtSpaceCenter_TouchesNothing()
        {
            HighLogic.LoadedScene = GameScenes.SPACECENTER;
            Recording due = Rec("due", 10, 90);
            due.VesselSnapshot = Snapshot();

            ParsekScenario.OnLoadPhaseAutoUnreserveCrewPastEndUT(new List<Recording> { due }, 100.0);

            Assert.NotNull(due.VesselSnapshot);
            Assert.False(due.VesselSpawned);
            Assert.DoesNotContain(logLines, l => l.Contains("Auto-unreserved crew"));
        }
    }
}
