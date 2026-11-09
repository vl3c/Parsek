using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct cells for the RewindSpawnScope units: the owner strip's identity / name
    /// members, the owner strip builder's EVA-child and alias-name halves, the transfer-member
    /// predicate, the reproduction walk's per-candidate routes and skips, the kept-holder
    /// pass of the strip scope, and the held-back pid collection.
    /// </summary>
    [Collection("Sequential")]
    public class RewindSpawnScopeUnitTests : IDisposable
    {
        private const double AdjustedUT = 1000.0;
        private readonly List<string> logLines = new List<string>();

        public RewindSpawnScopeUnitTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            RewindContext.ResetForTesting();
            PlaybackScopeTracker.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            RewindContext.ResetForTesting();
            PlaybackScopeTracker.ResetForTesting();
        }

        private static Recording Rec(
            string id, string name, string treeId, double startUT, double endUT,
            uint vesselPid, string guid = null, string chainId = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = name,
                TreeId = treeId,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                VesselPersistentId = vesselPid,
                RecordedVesselGuid = guid,
                ChainId = chainId,
            };
        }

        // ---------- RewindOwnerStrip members ----------

        [Fact]
        public void IsOwnerIdentityGuid_OwnerGuidIgnoringCase_OrAnIdentityGuid()
        {
            var strip = new RewindSpawnScope.RewindOwnerStrip { OwnerGuid = "aaaa" };
            strip.OwnerIdentityGuids.Add("bbbb");

            Assert.True(strip.IsOwnerIdentityGuid("AAAA"));
            Assert.True(strip.IsOwnerIdentityGuid("BBBB"));
            Assert.False(strip.IsOwnerIdentityGuid("cccc"));
            Assert.False(strip.IsOwnerIdentityGuid(null));
        }

        [Fact]
        public void IsOwnerIdentityGuid_NoOwnerGuid_ReadsTheSetOnly()
        {
            var strip = new RewindSpawnScope.RewindOwnerStrip();
            strip.OwnerIdentityGuids.Add("bbbb");

            Assert.True(strip.IsOwnerIdentityGuid("bbbb"));
            Assert.False(strip.IsOwnerIdentityGuid("aaaa"));
        }

        [Fact]
        public void HasOwnerIdentityGuid_EitherTheOwnerGuidOrASetMember()
        {
            var none = new RewindSpawnScope.RewindOwnerStrip();
            var ownerOnly = new RewindSpawnScope.RewindOwnerStrip { OwnerGuid = "aaaa" };
            var setOnly = new RewindSpawnScope.RewindOwnerStrip();
            setOnly.OwnerIdentityGuids.Add("bbbb");

            Assert.False(none.HasOwnerIdentityGuid);
            Assert.True(ownerOnly.HasOwnerIdentityGuid);
            Assert.True(setOnly.HasOwnerIdentityGuid);
        }

        [Fact]
        public void AllNames_OwnerFirstThenTheNameOnlySet_NoDuplicateOwner()
        {
            var strip = RewindSpawnScope.RewindOwnerStrip.FromNames(new[] { "Jeb" });
            strip.OwnerName = "Ship";
            Assert.Equal(new[] { "Ship", "Jeb" }, strip.AllNames().ToArray());

            strip.NameOnly.Add("Ship");
            Assert.Equal(new[] { "Jeb", "Ship" }, strip.AllNames().ToArray());

            var unnamed = RewindSpawnScope.RewindOwnerStrip.FromNames(new[] { "Bill" });
            unnamed.OwnerName = "";
            Assert.Equal(new[] { "Bill" }, unnamed.AllNames().ToArray());
        }

        [Fact]
        public void FromNames_NullSetAndNullEntries_AreSkipped()
        {
            Assert.Empty(RewindSpawnScope.RewindOwnerStrip.FromNames(null).NameOnly);
            var strip = RewindSpawnScope.RewindOwnerStrip.FromNames(new[] { "A", null, "B" });
            Assert.Equal(new HashSet<string> { "A", "B" }, strip.NameOnly);
            Assert.Null(strip.OwnerName);
            Assert.Null(strip.OwnerGuid);
        }

        // ---------- BuildRewindOwnerStrip ----------

        [Fact]
        public void BuildOwnerStrip_NullOwner_IsEmptyAndQuiet()
        {
            var strip = RewindSpawnScope.BuildRewindOwnerStrip(null);

            Assert.Null(strip.OwnerName);
            Assert.Empty(strip.NameOnly);
            Assert.Empty(strip.OwnerIdentityGuids);
            Assert.Empty(logLines);
        }

        [Fact]
        public void BuildOwnerStrip_TakesTheChainsEvaKerbalNames_Only()
        {
            var owner = Rec("own", "Ship", "t", 10, 100, 1u, chainId: "ch");
            var jeb = Rec("eva1", "Jebediah Kerman", "t", 20, 30, 2u, chainId: "ch");
            jeb.EvaCrewName = "Jebediah Kerman";
            var sameName = Rec("eva2", "Ship", "t", 30, 40, 3u, chainId: "ch");
            sameName.EvaCrewName = "Bill Kerman";
            var noName = Rec("eva3", null, "t", 40, 50, 4u, chainId: "ch");
            noName.EvaCrewName = "Bob Kerman";
            var notEva = Rec("seg", "Ship Lander", "t", 50, 60, 5u, chainId: "ch");
            var otherChain = Rec("eva4", "Valentina Kerman", "t", 60, 70, 6u, chainId: "other");
            otherChain.EvaCrewName = "Valentina Kerman";
            foreach (var r in new[] { owner, jeb, sameName, noName, notEva, otherChain })
                RecordingStore.AddRecordingWithTreeForTesting(r);

            var strip = RewindSpawnScope.BuildRewindOwnerStrip(owner);

            Assert.Equal("Ship", strip.OwnerName);
            Assert.Equal(new HashSet<string> { "Jebediah Kerman" }, strip.NameOnly);
            Assert.Contains(logLines, l => l.Contains("[INFO][Rewind]")
                && l.Contains("Rewind strip includes 1 EVA child vessel name(s) from chain 'ch'"));
        }

        [Fact]
        public void BuildOwnerStrip_NoChain_TakesNoEvaNamesAndLogsTheSummary()
        {
            var owner = Rec("own", "Ship", "t", 10, 100, 1u);
            var jeb = Rec("eva1", "Jebediah Kerman", "t", 20, 30, 2u);
            jeb.EvaCrewName = "Jebediah Kerman";
            RecordingStore.AddRecordingWithTreeForTesting(jeb);

            var strip = RewindSpawnScope.BuildRewindOwnerStrip(owner);

            Assert.Empty(strip.NameOnly);
            Assert.DoesNotContain(logLines, l => l.Contains("EVA child vessel name(s)"));
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][Rewind]")
                && l.Contains("Rewind owner strip: owner='Ship' guid=(none, name-only) evaChildNames=0"));
        }

        [Fact]
        public void BuildOwnerStrip_OwnerGuidIsNormalizedIntoTheIdentitySet()
        {
            var guid = Guid.NewGuid();
            var owner = Rec("own", "Ship", "t", 10, 100, 1u, guid: guid.ToString());

            var strip = RewindSpawnScope.BuildRewindOwnerStrip(owner);

            Assert.Equal(guid.ToString("N"), strip.OwnerGuid);
            Assert.Contains(guid.ToString("N"), strip.OwnerIdentityGuids);
            Assert.Empty(strip.OwnerAliasNames);
        }

        // ---------- IsTransferMemberId ----------

        [Fact]
        public void IsTransferMemberId_NeedsARecordingAnIdAndTheSet()
        {
            var members = new List<string> { "m1", "" };

            Assert.True(RewindSpawnScope.IsTransferMemberId(Rec("m1", "V", "t", 0, 1, 1u), members));
            Assert.False(RewindSpawnScope.IsTransferMemberId(Rec("m2", "V", "t", 0, 1, 1u), members));
            Assert.False(RewindSpawnScope.IsTransferMemberId(Rec("", "V", "t", 0, 1, 1u), members));
            Assert.False(RewindSpawnScope.IsTransferMemberId(null, members));
            Assert.False(RewindSpawnScope.IsTransferMemberId(Rec("m1", "V", "t", 0, 1, 1u), null));
        }

        // ---------- IsSpawnedVesselReproducedByRewindReplay ----------

        [Fact]
        public void Reproduced_NoHolderOrNoSpawnedPid_IsFalseWithNoRoute()
        {
            Assert.False(RewindSpawnScope.IsSpawnedVesselReproducedByRewindReplay(
                null, new List<Recording>(), AdjustedUT, out string via));
            Assert.Null(via);

            var holder = Rec("h", "Probe", "t", 10, 100, 5u);
            Assert.False(RewindSpawnScope.IsSpawnedVesselReproducedByRewindReplay(
                holder, new List<Recording>(), double.NaN, out via));
            Assert.Null(via);
        }

        [Fact]
        public void Reproduced_HolderReplays_RouteIsTheHolder()
        {
            var holder = Rec("h", "Probe", "t", AdjustedUT + 50, AdjustedUT + 100, 5u);
            holder.SpawnedVesselPersistentId = 5u;

            Assert.True(RewindSpawnScope.IsSpawnedVesselReproducedByRewindReplay(
                holder, null, AdjustedUT, out string via));
            Assert.Equal("holder-replays", via);
        }

        [Fact]
        public void Reproduced_HistoricalHolderWithoutRecordings_IsFalse()
        {
            var holder = Rec("h", "Probe", "t", 10, 100, 5u);
            holder.SpawnedVesselPersistentId = 5u;

            Assert.False(RewindSpawnScope.IsSpawnedVesselReproducedByRewindReplay(
                holder, null, AdjustedUT, out string via));
            Assert.Null(via);
        }

        [Fact]
        public void Reproduced_ACopyCarryingTheHoldersId_IsNotACandidate()
        {
            var holder = Rec("h", "Probe", "t", 10, 100, 5u);
            holder.SpawnedVesselPersistentId = 5u;
            var copy = Rec("h", "Probe", "t", AdjustedUT + 10, AdjustedUT + 50, 5u);

            Assert.False(RewindSpawnScope.IsSpawnedVesselReproducedByRewindReplay(
                holder, new List<Recording> { null, holder, copy }, AdjustedUT, out _));
        }

        [Fact]
        public void Reproduced_ChainMemberOfAnotherTree_IsNotARoute()
        {
            var holder = Rec("h", "Probe", "t1", 10, 100, 5u, chainId: "ch");
            holder.SpawnedVesselPersistentId = 9u;
            var elsewhere = Rec("x", "Probe", "t2", AdjustedUT + 10, AdjustedUT + 50, 6u, chainId: "ch");

            Assert.False(RewindSpawnScope.IsSpawnedVesselReproducedByRewindReplay(
                holder, new List<Recording> { holder, elsewhere }, AdjustedUT, out _));

            elsewhere.TreeId = "t1";
            Assert.True(RewindSpawnScope.IsSpawnedVesselReproducedByRewindReplay(
                holder, new List<Recording> { holder, elsewhere }, AdjustedUT, out string via));
            Assert.Equal("chain-member-replays:x", via);
        }

        [Fact]
        public void Reproduced_ARouteWhoseRecordingDoesNotReplay_IsSkipped()
        {
            var holder = Rec("h", "Probe", "t", 10, 100, 5u);
            holder.SpawnedVesselPersistentId = 7u;
            var historicalFlier = Rec("f1", "Probe", "t", 200, 300, 7u);
            var replayingFlier = Rec("f2", "Probe", "t", AdjustedUT + 10, AdjustedUT + 50, 7u);

            Assert.True(RewindSpawnScope.IsSpawnedVesselReproducedByRewindReplay(
                holder, new List<Recording> { holder, historicalFlier, replayingFlier }, AdjustedUT, out string via));
            Assert.Equal("same-vessel-recording-replays:f2", via);
        }

        [Fact]
        public void Reproduced_SupersedingContinuationFlyingAnAdoptedVessel_IsSkippedAndLogged()
        {
            var holder = Rec("h", "Rover", "t", 10, 100, 5u);
            holder.SpawnedVesselPersistentId = 7u;
            holder.TerminalSpawnSupersededByRecordingId = "c1";
            var continuation = Rec("c1", "Rover", "t", AdjustedUT + 10, AdjustedUT + 50, 7u);

            Assert.False(RewindSpawnScope.IsSpawnedVesselReproducedByRewindReplay(
                holder, new List<Recording> { holder, continuation }, AdjustedUT, out string via,
                new List<string> { "c1" }));
            Assert.Null(via);
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][Rewind]")
                && l.Contains("spawned pid=7 of 'Rover' (id=h) is not re-produced by its superseding continuation 'c1'"));
        }

        [Fact]
        public void Reproduced_TransferAdoptedFlier_IsNotTheSameVesselRoute()
        {
            var holder = Rec("h", "Probe", "t", 10, 100, 5u);
            holder.SpawnedVesselPersistentId = 7u;
            var adoptedFlier = Rec("m", "Probe", "t", AdjustedUT + 10, AdjustedUT + 50, 7u);

            Assert.False(RewindSpawnScope.IsSpawnedVesselReproducedByRewindReplay(
                holder, new List<Recording> { holder, adoptedFlier }, AdjustedUT, out _,
                new List<string> { "m" }));
        }

        // ---------- ResolveRewindSpawnStripScope ----------

        [Fact]
        public void StripScope_NullRecordings_IsEmptyAndQuiet()
        {
            var scope = RewindSpawnScope.ResolveRewindSpawnStripScope(null, AdjustedUT);

            Assert.Empty(scope.StripPids);
            Assert.Empty(scope.KeptPids);
            Assert.Empty(logLines);
        }

        [Fact]
        public void StripScope_HistoricalHolder_IsKeptAndLogged()
        {
            var holder = Rec("h", "Probe", "t", 10, 100, 5u);
            holder.SpawnedVesselPersistentId = 5u;
            var notSpawned = Rec("n", "Other", "t", 10, 100, 6u);

            var scope = RewindSpawnScope.ResolveRewindSpawnStripScope(
                new List<Recording> { null, notSpawned, holder }, AdjustedUT);

            Assert.Equal(new HashSet<uint> { 5u }, scope.KeptPids);
            Assert.Equal(new HashSet<string> { "h" }, scope.KeptHolderRecordingIds);
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][Rewind]")
                && l.Contains("keep spawned pid=5 of 'Probe' (id=h) as committed history")
                && l.Contains("adjustedUT=1000.0"));
            Assert.Contains(logLines, l => l.Contains("[INFO][Rewind]")
                && l.Contains("Rewind strip scope: adjustedUT=1000.0 stripPids=0 keptHistoryPids=1 keptHolders=1 foreignAdoptedPids=0"));
        }

        [Fact]
        public void StripScope_IdlessHolderAlwaysReplays_SoItsPidIsStripped()
        {
            // The replay-scope gate never calls an id-less recording historical. An empty id
            // logs as empty (only a null id reads <no-id>).
            var holder = Rec("", "Probe", "t", 10, 100, 5u);
            holder.SpawnedVesselPersistentId = 5u;

            var scope = RewindSpawnScope.ResolveRewindSpawnStripScope(
                new List<Recording> { holder }, AdjustedUT);

            Assert.Equal(new HashSet<uint> { 5u }, scope.StripPids);
            Assert.Empty(scope.KeptPids);
            Assert.Contains(logLines, l => l.Contains("strip spawned pid=5 of 'Probe' (id=) via holder-replays"));
        }

        [Fact]
        public void StripScope_TwoKeptHoldersOfOnePid_NameBothHolders()
        {
            var a = Rec("a", "Probe", "t", 10, 100, 5u);
            a.SpawnedVesselPersistentId = 5u;
            var b = Rec("b", "Probe", "t2", 10, 100, 6u);
            b.SpawnedVesselPersistentId = 5u;

            var scope = RewindSpawnScope.ResolveRewindSpawnStripScope(
                new List<Recording> { a, b }, AdjustedUT);

            Assert.Equal(new HashSet<uint> { 5u }, scope.KeptPids);
            Assert.Equal(new HashSet<string> { "a", "b" }, scope.KeptHolderRecordingIds);
        }

        [Fact]
        public void StripScope_StripLogNamesTheRoute()
        {
            var holder = Rec("h", "Probe", "t", AdjustedUT + 10, AdjustedUT + 50, 5u);
            holder.SpawnedVesselPersistentId = 5u;

            var scope = RewindSpawnScope.ResolveRewindSpawnStripScope(
                new List<Recording> { holder }, AdjustedUT);

            Assert.Equal(new HashSet<uint> { 5u }, scope.StripPids);
            Assert.Empty(scope.KeptPids);
            Assert.Contains(logLines, l => l.Contains("strip spawned pid=5 of 'Probe' (id=h) via holder-replays"));
        }

        // ---------- Held-back pids ----------

        [Fact]
        public void HeldBackPids_NullChainsAndNullChain_AreEmpty()
        {
            Assert.Empty(RewindSpawnScope.CollectHeldBackVesselPids(null));
            var chains = new Dictionary<uint, GhostChain> { { 1u, null } };
            Assert.Empty(RewindSpawnScope.CollectHeldBackVesselPids(chains));
        }

        [Fact]
        public void HeldBackPids_KeyAndClaimsOfEveryChain()
        {
            var a = new GhostChain { OriginalVesselPid = 10u };
            a.AddClaimedVessel(11u, null, 5.0);
            var b = new GhostChain { OriginalVesselPid = 0u };
            b.AddClaimedVessel(20u, null, 5.0);

            var pids = RewindSpawnScope.CollectHeldBackVesselPids(
                new Dictionary<uint, GhostChain> { { 10u, a }, { 0u, b } });

            Assert.Equal(new HashSet<uint> { 10u, 11u, 20u }, pids);
        }

        [Fact]
        public void ClaimedPidsResolver_UnknownUTOrNoTrees_IsNull()
        {
            Assert.Null(RewindSpawnScope.ResolveRewindClaimedVesselPids(double.NaN));
            Assert.Null(RewindSpawnScope.ResolveRewindClaimedVesselPids(AdjustedUT));
        }

        // ---------- IsStableState ----------

        [Theory]
        [InlineData(1, true)]
        [InlineData(2, true)]
        [InlineData(4, true)]
        [InlineData(32, true)]
        [InlineData(8, false)]
        [InlineData(16, false)]
        [InlineData(64, false)]
        [InlineData(0, false)]
        public void IsStableState_LandedSplashedPrelaunchOrbiting(int situation, bool expected)
        {
            Assert.Equal(expected, RewindSpawnScope.IsStableState(situation));
        }

        // ---------- Extracted units ----------

        [Fact]
        public void AliasNames_DecisionNameAndCapturedName_MinusTheOwnerName()
        {
            var owner = Rec("own", "New Name", "t", 10, 100, 1u);
            var decision = new VesselNameDecision { Name = "New Name" };
            decision.CapturedNames["own"] = "Old Name";
            var strip = new RewindSpawnScope.RewindOwnerStrip { OwnerName = "New Name" };

            RewindSpawnScope.AddOwnerAliasNames(strip, owner, decision);

            Assert.Equal(new HashSet<string> { "Old Name" }, strip.OwnerAliasNames);
        }

        [Fact]
        public void AliasNames_NoCapturedName_FallsBackToTheOwnersCurrentName()
        {
            var owner = Rec("own", "Current", "t", 10, 100, 1u);
            var decision = new VesselNameDecision { Name = "Decided" };
            var strip = new RewindSpawnScope.RewindOwnerStrip { OwnerName = "Other" };

            RewindSpawnScope.AddOwnerAliasNames(strip, owner, decision);

            Assert.Equal(new HashSet<string> { "Decided", "Current" }, strip.OwnerAliasNames);
        }

        [Fact]
        public void AliasNames_NullOwnerIdAndEmptyNames_AddNothingEmpty()
        {
            var owner = Rec(null, "", "t", 10, 100, 1u);
            var decision = new VesselNameDecision { Name = "" };
            decision.CapturedNames["x"] = "Never";
            var strip = new RewindSpawnScope.RewindOwnerStrip();

            RewindSpawnScope.AddOwnerAliasNames(strip, owner, decision);

            Assert.Empty(strip.OwnerAliasNames);
        }

        [Fact]
        public void ChainEvaChildNames_ReadOnlyTheGivenList()
        {
            var owner = Rec("own", "Ship", "t", 10, 100, 1u, chainId: "ch");
            var jeb = Rec("eva1", "Jebediah Kerman", "t", 20, 30, 2u, chainId: "ch");
            jeb.EvaCrewName = "Jebediah Kerman";
            var bill = Rec("eva2", "Bill Kerman", "t", 30, 40, 3u, chainId: "ch");
            bill.EvaCrewName = "Bill Kerman";
            RecordingStore.AddRecordingWithTreeForTesting(bill);
            var strip = new RewindSpawnScope.RewindOwnerStrip();

            RewindSpawnScope.AddChainEvaChildNames(strip, owner, new List<Recording> { owner, jeb });

            Assert.Equal(new HashSet<string> { "Jebediah Kerman" }, strip.NameOnly);
            Assert.Contains(logLines, l => l.Contains("Rewind strip includes 1 EVA child vessel name(s) from chain 'ch'"));
        }

        [Fact]
        public void ChainEvaChildNames_NoneFound_LogsNothing()
        {
            var owner = Rec("own", "Ship", "t", 10, 100, 1u, chainId: "ch");
            var strip = new RewindSpawnScope.RewindOwnerStrip();

            RewindSpawnScope.AddChainEvaChildNames(strip, owner, new List<Recording> { owner });

            Assert.Empty(strip.NameOnly);
            Assert.Empty(logLines);
        }

        [Fact]
        public void HolderOrNoCandidate_NullSelfAndSameId_ButNotAnEmptyIdMatch()
        {
            var holder = Rec("h", "P", "t", 0, 1, 1u);
            Assert.True(RewindSpawnScope.IsHolderOrNoCandidate(holder, null));
            Assert.True(RewindSpawnScope.IsHolderOrNoCandidate(holder, holder));
            Assert.True(RewindSpawnScope.IsHolderOrNoCandidate(holder, Rec("h", "P", "t", 0, 1, 1u)));
            Assert.False(RewindSpawnScope.IsHolderOrNoCandidate(holder, Rec("o", "P", "t", 0, 1, 1u)));

            var idless = Rec("", "P", "t", 0, 1, 1u);
            Assert.False(RewindSpawnScope.IsHolderOrNoCandidate(idless, Rec("", "P", "t", 0, 1, 1u)));
        }

        private static string Route(Recording holder, Recording other, uint pid, bool unique,
            string supersededBy, bool adopted, out bool adoptedContinuation)
        {
            return RewindSpawnScope.ClassifyReproductionRoute(
                holder, other, pid, unique, supersededBy, adopted, out adoptedContinuation);
        }

        [Fact]
        public void Route_SupersedingContinuation_WinsOverEveryOtherRoute()
        {
            var holder = Rec("h", "P", "t", 0, 1, 7u, chainId: "ch");
            var other = Rec("c", "P", "t", 0, 1, 7u, chainId: "ch");

            Assert.Equal("superseding-continuation-replays",
                Route(holder, other, 7u, true, "c", false, out bool adoptedContinuation));
            Assert.False(adoptedContinuation);
        }

        [Fact]
        public void Route_AdoptedSupersedingContinuation_IsNoRouteAndFlagged()
        {
            var holder = Rec("h", "P", "t", 0, 1, 7u, chainId: "ch");
            var other = Rec("c", "P", "t", 0, 1, 7u, chainId: "ch");

            Assert.Null(Route(holder, other, 7u, true, "c", true, out bool adoptedContinuation));
            Assert.True(adoptedContinuation);
        }

        [Fact]
        public void Route_ChainMember_NeedsTheChainAndTheTree()
        {
            var holder = Rec("h", "P", "t", 0, 1, 1u, chainId: "ch");

            Assert.Equal("chain-member-replays",
                Route(holder, Rec("m", "P", "t", 0, 1, 2u, chainId: "ch"), 9u, true, null, false, out _));
            Assert.Null(Route(holder, Rec("m", "P", "t2", 0, 1, 2u, chainId: "ch"), 9u, true, null, false, out _));
            Assert.Null(Route(Rec("h", "P", "t", 0, 1, 1u), Rec("m", "P", "t", 0, 1, 2u), 9u, true, null, false, out _));
        }

        [Fact]
        public void Route_SameVessel_UniquePidMatchesByPidAlone()
        {
            var holder = Rec("h", "P", "t", 0, 1, 1u, guid: "aaaa0000aaaa0000aaaa0000aaaa0000");
            var other = Rec("o", "P", "t2", 0, 1, 9u, guid: "bbbb0000bbbb0000bbbb0000bbbb0000");

            Assert.Equal("same-vessel-recording-replays",
                Route(holder, other, 9u, true, null, false, out _));
            Assert.Null(Route(holder, other, 8u, true, null, false, out _));
            Assert.Null(Route(holder, other, 9u, true, null, true, out _));
        }

        [Fact]
        public void Route_SameVessel_BakedPidNeedsGuidsThatDoNotDiffer()
        {
            var holder = Rec("h", "P", "t", 0, 1, 9u, guid: "aaaa0000aaaa0000aaaa0000aaaa0000");
            var relaunch = Rec("o", "P", "t2", 0, 1, 9u, guid: "bbbb0000bbbb0000bbbb0000bbbb0000");
            var sameLaunch = Rec("s", "P", "t2", 0, 1, 9u, guid: "aaaa0000aaaa0000aaaa0000aaaa0000");
            var unknown = Rec("u", "P", "t2", 0, 1, 9u);

            Assert.Null(Route(holder, relaunch, 9u, false, null, false, out _));
            Assert.Equal("same-vessel-recording-replays", Route(holder, sameLaunch, 9u, false, null, false, out _));
            Assert.Equal("same-vessel-recording-replays", Route(holder, unknown, 9u, false, null, false, out _));
        }

        [Fact]
        public void KeptHolders_SkipAStrippedPid_KeepTheRestWithTheirIds()
        {
            var scope = new RewindSpawnScope.RewindSpawnStripScope();
            scope.StripPids.Add(5u);
            var stripped = Rec("a", "P", "t", 0, 1, 1u);
            stripped.SpawnedVesselPersistentId = 5u;
            var kept = Rec("b", "Q", "t", 0, 1, 2u);
            kept.SpawnedVesselPersistentId = 6u;
            var keptNoId = Rec(null, "R", "t", 0, 1, 3u);
            keptNoId.SpawnedVesselPersistentId = 7u;

            RewindSpawnScope.RecordKeptHolders(scope, new List<Recording> { stripped, kept, keptNoId }, 12.5);

            Assert.Equal(new HashSet<uint> { 6u, 7u }, scope.KeptPids);
            Assert.Equal(new HashSet<string> { "b" }, scope.KeptHolderRecordingIds);
            Assert.Equal(2, logLines.Count(l => l.Contains("as committed history")));
            Assert.Contains(logLines, l => l.Contains("keep spawned pid=7 of 'R' (id=<no-id>)")
                && l.Contains("adjustedUT=12.5"));
        }
    }
}
