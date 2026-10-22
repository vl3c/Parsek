using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The plain rewind's vessel hold (todos REWIND-LEAVES-VESSELS-WITH-A-COMMITTED-FUTURE-LIVE,
    /// CLAIMED-VESSEL-DESPAWN-AT-REWIND-OUTSIDE-FLIGHT), its landing world
    /// (REWIND-TO-LAUNCH-LANDS-IN-THE-LATEST-PERSISTENT-WORLD) and the foreign Board stamp
    /// (FOREIGN-BOARD-LEAVES-NO-GHOST-CLAIM).
    /// </summary>
    [Collection("Sequential")]
    public class RewindVesselHoldTests : IDisposable
    {
        private const string GuidA = "11111111111111111111111111111111";
        private const string GuidB = "22222222222222222222222222222222";
        private const double RewindUT = 1000.0;

        private readonly List<string> logLines = new List<string>();

        public RewindVesselHoldTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RewindVesselHold.ResetForTesting();
            RewindContext.ResetForTesting();
            SceneExitInterceptor.ResetTestOverrides();
            RecordingStore.RewindWritePersistentForTesting = null;
            RecordingStore.PostRewindPersistentWriteForTesting = null;
        }

        public void Dispose()
        {
            RewindVesselHold.ResetForTesting();
            RewindContext.ResetForTesting();
            SceneExitInterceptor.ResetTestOverrides();
            RecordingStore.RewindWritePersistentForTesting = null;
            RecordingStore.PostRewindPersistentWriteForTesting = null;
            ParsekLog.ResetTestOverrides();
        }

        private static Recording Rec(string id, uint pid, string guid, double startUt, double endUt,
            TerminalState? terminal = null)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselPersistentId = pid,
                RecordedVesselGuid = guid,
                ExplicitStartUT = startUt,
                ExplicitEndUT = endUt,
            };
            rec.TerminalStateValue = terminal;
            return rec;
        }

        private static GhostChain Chain(uint key, string keyGuid, double firstClaimUT, double spawnUT,
            bool terminated = false, params ChainClaimedVessel[] folded)
        {
            var chain = new GhostChain
            {
                OriginalVesselPid = key,
                LaunchGuid = keyGuid,
                GhostStartUT = firstClaimUT,
                SpawnUT = spawnUT,
                TipRecordingId = "tip-" + key,
                TipTreeId = "tree-" + key,
                IsTerminated = terminated,
            };
            if (chain.ClaimedVessels == null)
                chain.ClaimedVessels = new List<ChainClaimedVessel>();
            chain.ClaimedVessels.AddRange(folded);
            return chain;
        }

        // ---------- Foreign Board stamp ----------------------------------

        [Fact]
        public void BoardOntoForeignVessel_StampsItsPidAndLaunchGuid()
        {
            string guid;
            uint pid = ParsekFlight.ResolveBoardPartnerStamp(false, 4242u, GuidA, out guid);
            Assert.Equal(4242u, pid);
            Assert.Equal(GuidA, guid);
        }

        [Fact]
        public void BoardOntoSameTreeVessel_StaysUnstamped()
        {
            string guid;
            uint pid = ParsekFlight.ResolveBoardPartnerStamp(true, 4242u, GuidA, out guid);
            Assert.Equal(0u, pid);
            Assert.Null(guid);
        }

        [Fact]
        public void BoardStamp_UnknownGuid_KeepsThePidAlone_AndZeroPidStampsNothing()
        {
            string guid;
            Assert.Equal(4242u, ParsekFlight.ResolveBoardPartnerStamp(false, 4242u, "", out guid));
            Assert.Null(guid);
            Assert.Equal(0u, ParsekFlight.ResolveBoardPartnerStamp(false, 0u, GuidA, out guid));
            Assert.Null(guid);
        }

        [Fact]
        public void ForeignBoardStamp_IsAChangeClaimTheHoldReads()
        {
            // The boarded vessel's committed future: a kerbal of another tree boards it at UT 1500.
            var tree = new RecordingTree { Id = "tree-eva" };
            var eva = Rec("eva", 77u, GuidB, 1400.0, 1500.0, TerminalState.Boarded);
            eva.TreeId = tree.Id;
            tree.Recordings[eva.RecordingId] = eva;
            string guid;
            uint stamp = ParsekFlight.ResolveBoardPartnerStamp(false, 42u, GuidA, out guid);
            var (bp, child) = ParsekFlight.BuildMergeBranchData(
                new List<string> { "eva" }, tree.Id, 1500.0, BranchPointType.Board, 42u, "Station",
                branchPartnerPid: stamp, branchPartnerGuid: guid);
            tree.BranchPoints.Add(bp);
            child.TreeId = tree.Id;
            tree.Recordings[child.RecordingId] = child;

            int skipped;
            var claims = ReFlyInvokeHold.CollectChangeClaims(
                new List<RecordingTree> { tree }, RewindUT, null, out skipped);
            var claim = Assert.Single(claims);
            Assert.Equal(42u, claim.Pid);
            Assert.Equal(GuidA, claim.LaunchGuid);
            Assert.Equal("board", claim.Kind);

            int matchedWithoutFuture;
            var held = ReFlyInvokeHold.Classify(
                new List<(uint, string)> { (42u, GuidA) }, null, new List<Recording>(),
                new List<GameAction>(), claims, RewindUT, out matchedWithoutFuture);
            var entry = Assert.Single(held);
            Assert.Equal(1500.0, entry.FirstClaimUT);
            Assert.Equal(ReFlyInvokeHold.EvidenceChanges, entry.Evidence);
        }

        // ---------- Chain holds -------------------------------------------

        [Fact]
        public void ActiveChain_HoldsItsKeyAndFoldedClaims_UntilEachFirstClaim()
        {
            var folded = new ChainClaimedVessel { Pid = 55u, LaunchGuid = GuidB, FirstClaimUT = 1800.0 };
            var chains = new Dictionary<uint, GhostChain>
            {
                { 42u, Chain(42u, GuidA, 1600.0, 2000.0, false, folded) },
            };

            var holds = RewindVesselHold.CollectChainHolds(chains, RewindUT,
                pid => pid == 42u ? GuidA : pid == 55u ? GuidB : null);

            Assert.Equal(2, holds.Count);
            Assert.Equal(42u, holds[0].Pid);
            Assert.Equal(1600.0, holds[0].FirstClaimUT);
            Assert.Equal(RewindVesselHold.EvidenceChain, holds[0].Evidence);
            Assert.Equal("tip-42", holds[0].RecordingId);
            Assert.Equal(55u, holds[1].Pid);
            Assert.Equal(1800.0, holds[1].FirstClaimUT);
            Assert.Equal(GuidB, holds[1].LaunchGuid);
        }

        [Fact]
        public void TerminatedOrSpawnedChains_HoldNothing()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 42u, Chain(42u, GuidA, 1600.0, 2000.0, terminated: true) },
                { 43u, Chain(43u, GuidA, 500.0, 900.0) },
            };
            Assert.Empty(RewindVesselHold.CollectChainHolds(chains, RewindUT, pid => null));
        }

        [Fact]
        public void FoldedClaimOfAnotherLaunch_IsNotHeld()
        {
            var folded = new ChainClaimedVessel { Pid = 55u, LaunchGuid = GuidB, FirstClaimUT = 1800.0 };
            var chains = new Dictionary<uint, GhostChain>
            {
                { 42u, Chain(42u, GuidA, 1600.0, 2000.0, false, folded) },
            };
            // The live 55 is a relaunch of the same craft (another launch guid).
            var holds = RewindVesselHold.CollectChainHolds(chains, RewindUT,
                pid => pid == 55u ? GuidA : null);
            var only = Assert.Single(holds);
            Assert.Equal(42u, only.Pid);
        }

        // ---------- Merge ---------------------------------------------------

        [Fact]
        public void Merge_OneEntryPerVessel_EarliestClaimWins_FlyingRecordingsUnioned()
        {
            var chainEntry = new ReFlyInvokeHold.Entry
            {
                Pid = 42u, LaunchGuid = GuidA, FirstClaimUT = 1600.0, RecordingId = "tip",
                Evidence = RewindVesselHold.EvidenceChain,
            };
            var flies = new ReFlyInvokeHold.Entry
            {
                Pid = 42u, LaunchGuid = GuidA, FirstClaimUT = 1200.0, RecordingId = "switch",
                Evidence = ReFlyInvokeHold.EvidenceFlies,
            };
            flies.FlyingRecordingIds.Add("switch");
            var other = new ReFlyInvokeHold.Entry
            {
                Pid = 9u, LaunchGuid = GuidB, FirstClaimUT = 3000.0, RecordingId = "rec9",
                Evidence = ReFlyInvokeHold.EvidenceRecovery,
            };

            var merged = RewindVesselHold.Merge(
                new List<ReFlyInvokeHold.Entry> { chainEntry },
                new List<ReFlyInvokeHold.Entry> { flies, other });

            Assert.Equal(2, merged.Count);
            Assert.Equal(42u, merged[0].Pid);
            Assert.Equal(1200.0, merged[0].FirstClaimUT);
            Assert.Equal("switch", merged[0].RecordingId);
            Assert.Equal(ReFlyInvokeHold.EvidenceFlies, merged[0].Evidence);
            Assert.Equal(new[] { "switch" }, merged[0].FlyingRecordingIds);
            Assert.Equal(9u, merged[1].Pid);
            // The inputs are not mutated.
            Assert.Equal(1600.0, chainEntry.FirstClaimUT);
        }

        [Fact]
        public void SwitchContinuationFlightAfterTheRewind_HoldsTheVessel()
        {
            // The vessel of an earlier mission, flown again later by a switch continuation of
            // another tree: no branch point claims it, so before the fix it stayed live.
            var earlier = Rec("launch", 42u, GuidA, 100.0, 400.0, TerminalState.Orbiting);
            var later = Rec("switch", 42u, GuidA, 1500.0, 2500.0, TerminalState.Orbiting);
            int matchedWithoutFuture;
            var future = ReFlyInvokeHold.Classify(
                new List<(uint, string)> { (42u, GuidA), (7u, GuidB) }, null,
                new List<Recording> { earlier, later }, new List<GameAction>(),
                new List<ReFlyInvokeHold.ChangeClaim>(), RewindUT, out matchedWithoutFuture);

            var merged = RewindVesselHold.Merge(new List<ReFlyInvokeHold.Entry>(), future);
            var entry = Assert.Single(merged);
            Assert.Equal(42u, entry.Pid);
            Assert.Equal(1500.0, entry.FirstClaimUT);
            Assert.Equal(new[] { "switch" }, entry.FlyingRecordingIds);
        }

        [Fact]
        public void FlightTheRewindWillNotReplay_HoldsNothing()
        {
            // A recording that spans the rewind UT but is historical after it (no ghost, no
            // spawn): holding its vessel would remove it for good.
            var spanning = Rec("fork", 42u, GuidA, 600.0, 2000.0, TerminalState.Landed);
            int matchedWithoutFuture, fliesNotReplayed;
            var future = ReFlyInvokeHold.Classify(
                new List<(uint, string)> { (42u, GuidA) }, null,
                new List<Recording> { spanning }, new List<GameAction>(),
                new List<ReFlyInvokeHold.ChangeClaim>(), RewindUT, rec => rec.RecordingId != "fork",
                out matchedWithoutFuture, out fliesNotReplayed);

            Assert.Empty(future);
            Assert.Equal(1, fliesNotReplayed);

            // The same recording, replayed, holds.
            future = ReFlyInvokeHold.Classify(
                new List<(uint, string)> { (42u, GuidA) }, null,
                new List<Recording> { spanning }, new List<GameAction>(),
                new List<ReFlyInvokeHold.ChangeClaim>(), RewindUT, rec => true,
                out matchedWithoutFuture, out fliesNotReplayed);
            Assert.Single(future);
            Assert.Equal(0, fliesNotReplayed);
        }

        [Fact]
        public void ChainWhoseTipWillNotReplay_HoldsNothing()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 42u, Chain(42u, GuidA, 1600.0, 2000.0) },
                { 43u, Chain(43u, GuidB, 1700.0, 2100.0) },
            };
            int tipNotReplayed;
            var holds = RewindVesselHold.CollectChainHolds(chains, RewindUT,
                pid => pid == 42u ? GuidA : pid == 43u ? GuidB : null,
                id => id != "tip-42", out tipNotReplayed);

            var only = Assert.Single(holds);
            Assert.Equal(43u, only.Pid);
            Assert.Equal(1, tipNotReplayed);
        }

        // ---------- Save / load ---------------------------------------------

        [Fact]
        public void SaveInto_LoadFrom_RoundTripsEveryHoldWithItsSnapshot()
        {
            var entry = new ReFlyInvokeHold.Entry
            {
                Pid = 42u, LaunchGuid = GuidA, FirstClaimUT = 1600.5, RecordingId = "tip",
                Evidence = RewindVesselHold.EvidenceChain,
            };
            entry.FlyingRecordingIds.Add("switch");
            var snap = new ConfigNode("VESSEL");
            snap.AddValue("name", "Station");
            snap.AddValue("persistentId", "42");
            snap.AddNode("PART").AddValue("name", "probeCoreOcto");
            var claimPassed = new ReFlyInvokeHold.Entry { Pid = 9u, FirstClaimUT = 10.0, Evidence = "recovery" };
            RewindVesselHold.Replace(new List<RewindVesselHold.Hold>
            {
                new RewindVesselHold.Hold { Entry = entry, VesselName = "Station", Snapshot = snap },
                new RewindVesselHold.Hold { Entry = claimPassed, VesselName = "Old" },
            }, "test");

            var parsek = new ConfigNode("SCENARIO");
            Assert.Equal(2, RewindVesselHold.SaveInto(parsek));
            // A second save replaces, never appends.
            Assert.Equal(2, RewindVesselHold.SaveInto(parsek));
            Assert.Single(parsek.GetNodes(RewindVesselHold.NodeName));

            var loaded = RewindVesselHold.LoadFrom(parsek);
            Assert.Equal(2, loaded.Count);
            Assert.Equal(42u, loaded[0].Entry.Pid);
            Assert.Equal(GuidA, loaded[0].Entry.LaunchGuid);
            Assert.Equal(1600.5, loaded[0].Entry.FirstClaimUT);
            Assert.Equal("chain", loaded[0].Entry.Evidence);
            Assert.Equal(new[] { "switch" }, loaded[0].Entry.FlyingRecordingIds);
            Assert.Equal("Station", loaded[0].VesselName);
            Assert.Equal("Station", loaded[0].Snapshot.GetValue("name"));
            Assert.Equal("probeCoreOcto", loaded[0].Snapshot.GetNode("PART").GetValue("name"));
            Assert.Null(loaded[1].Snapshot);
        }

        [Fact]
        public void NoHolds_WritesNoNode_AndANodeWithoutHoldsLoadsEmpty()
        {
            var parsek = new ConfigNode("SCENARIO");
            parsek.AddNode(RewindVesselHold.NodeName);
            Assert.Equal(0, RewindVesselHold.SaveInto(parsek));
            Assert.False(parsek.HasNode(RewindVesselHold.NodeName));
            Assert.Empty(RewindVesselHold.LoadFrom(parsek));
            Assert.Empty(RewindVesselHold.LoadFrom(null));
        }

        [Fact]
        public void ReplaceAndClear_AreLogged()
        {
            RewindVesselHold.Replace(new List<RewindVesselHold.Hold>
            {
                new RewindVesselHold.Hold { Entry = new ReFlyInvokeHold.Entry { Pid = 42u, FirstClaimUT = 1600.0 } },
                new RewindVesselHold.Hold { Entry = new ReFlyInvokeHold.Entry { Pid = 0u } },
            }, "plain-rewind");
            Assert.Single(RewindVesselHold.Holds);
            RewindVesselHold.Clear("load ReFlyStart");
            Assert.Empty(RewindVesselHold.Holds);
            Assert.Contains(logLines, l => l.Contains("[RewindHold]")
                && l.Contains("Rewind vessel holds replaced: before=0 after=1 reason=plain-rewind"));
            Assert.Contains(logLines, l => l.Contains("[RewindHold]")
                && l.Contains("Rewind vessel holds cleared: dropped=1 reason=load ReFlyStart"));
        }

        [Fact]
        public void HeldLine_NamesTheVesselAndItsClaim()
        {
            var entry = new ReFlyInvokeHold.Entry
            {
                Pid = 42u, LaunchGuid = GuidA, FirstClaimUT = 1600.0, RecordingId = "tip",
                Evidence = RewindVesselHold.EvidenceChain,
            };
            string line = RewindVesselHold.FormatHeldLine(
                new RewindVesselHold.Hold { Entry = entry, VesselName = "Station", Snapshot = new ConfigNode("VESSEL") });
            Assert.Equal(
                "Rewind held vessel for its committed future: pid=42 name='Station' guid=" + GuidA +
                " rec=tip firstClaimUT=1600.0 evidence=chain flying=0 snapshot=yes", line);
        }

        // ---------- Load policy ---------------------------------------------

        [Fact]
        public void LoadPolicy_ColdTakesTheSave_RewindsClear_InSessionKeepsMemory()
        {
            Assert.Equal(LoadReconcileAction.Save,
                LoadReconcilePolicy.Decide(LoadKind.Cold, LoadStateCategory.RewindVesselHolds).Action);
            Assert.Equal(LoadReconcileAction.Clear,
                LoadReconcilePolicy.Decide(LoadKind.PlainRewind, LoadStateCategory.RewindVesselHolds).Action);
            Assert.Equal(LoadReconcileAction.Clear,
                LoadReconcilePolicy.Decide(LoadKind.ReFlyStart, LoadStateCategory.RewindVesselHolds).Action);
            Assert.Equal(LoadReconcileAction.Clear,
                LoadReconcilePolicy.Decide(LoadKind.DiscardReFly, LoadStateCategory.RewindVesselHolds).Action);
            Assert.Equal(LoadReconcileAction.Memory,
                LoadReconcilePolicy.DecideEarly(EarlyLoadKind.InSession, LoadStateCategory.RewindVesselHolds).Action);
            Assert.True(LoadReconcilePolicy.IsConsumedEarly(LoadStateCategory.RewindVesselHolds));
        }

        // ---------- The rewind's landing world --------------------------------

        [Fact]
        public void RewindGameWritten_MarksTheRewind_AndASceneExitSaveThenSkips()
        {
            RewindContext.BeginRewind(500.0, default(BudgetSummary), 0, 0, 0f);
            RecordingStore.RewindWritePersistentForTesting = g => true;
            int saves = 0;
            SceneExitInterceptor.PersistentSaveStepForTesting = _ => saves++;

            Assert.True(RecordingStore.WriteRewindGameAsPersistent(null, "Rewind"));
            Assert.True(RewindContext.RewindGameWrittenToPersistent);

            Assert.True(SceneExitInterceptor.SafeWritePersistent(GameScenes.SPACECENTER));
            Assert.Equal(0, saves);
            Assert.Contains(logLines, l => l.Contains("[SceneExit]")
                && l.Contains("SafeWritePersistent: skipped dest=SPACECENTER - a rewind wrote its game to persistent.sfs"));

            // The rewind's end lifts it: the next scene exit saves as before.
            RewindContext.EndRewind();
            Assert.False(RewindContext.RewindGameWrittenToPersistent);
            Assert.True(SceneExitInterceptor.SafeWritePersistent(GameScenes.SPACECENTER));
            Assert.Equal(1, saves);
        }

        [Fact]
        public void RewindGameNotWritten_SceneExitSavesAsBefore()
        {
            RewindContext.BeginRewind(500.0, default(BudgetSummary), 0, 0, 0f);
            RecordingStore.RewindWritePersistentForTesting = g => false;
            int saves = 0;
            SceneExitInterceptor.PersistentSaveStepForTesting = _ => saves++;

            Assert.False(RecordingStore.WriteRewindGameAsPersistent(null, "Rewind"));
            Assert.False(RewindContext.RewindGameWrittenToPersistent);
            Assert.True(SceneExitInterceptor.SafeWritePersistent(GameScenes.SPACECENTER));
            Assert.Equal(1, saves);
        }

        [Fact]
        public void WriteRewindGame_NoGame_Refuses()
        {
            RewindContext.BeginRewind(500.0, default(BudgetSummary), 0, 0, 0f);
            Assert.False(RecordingStore.WriteRewindGameAsPersistent(null, "Rewind"));
            Assert.False(RewindContext.RewindGameWrittenToPersistent);
            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("Rewind: no rewind game to write to persistent.sfs"));
        }

        [Fact]
        public void PostRewindPersistentWrite_IsLogged()
        {
            int writes = 0;
            RecordingStore.PostRewindPersistentWriteForTesting = () => { writes++; return true; };
            Assert.True(RecordingStore.WritePersistentAfterRewindAdjustment());
            Assert.Equal(1, writes);
            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("Post-rewind persistent.sfs write ok=true"));
        }

        [Fact]
        public void BeginRewind_ClearsAStaleWrittenFlag()
        {
            RewindContext.BeginRewind(500.0, default(BudgetSummary), 0, 0, 0f);
            RewindContext.MarkRewindGameWrittenToPersistent();
            RewindContext.BeginRewind(600.0, default(BudgetSummary), 0, 0, 0f);
            Assert.False(RewindContext.RewindGameWrittenToPersistent);
        }
    }
}
