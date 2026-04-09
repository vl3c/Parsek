using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for the [RecState] structured observability dump:
    /// <see cref="RecorderStateSnapshot.CaptureFromParts"/>, the
    /// <see cref="ParsekLog.RecState"/> emit/format helpers, and
    /// <see cref="Recording.DebugName"/>.
    ///
    /// All tests construct snapshots from explicit inputs (no Unity needed)
    /// and capture emitted lines via <c>ParsekLog.TestSinkForTesting</c>.
    /// </summary>
    [Collection("Sequential")]
    public class RecorderStateObservabilityTests : IDisposable
    {
        private readonly List<string> allLogLines = new List<string>();

        // RecState-only filtered view: ChainSegmentManager and FlightRecorder constructors
        // emit unrelated INFO lines into the sink, so the assertions need a focused subset.
        private List<string> logLines =>
            allLogLines.FindAll(l => l.Contains("[RecState]"));

        public RecorderStateObservabilityTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => allLogLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ----- CaptureFromParts: mode determination -----

        [Fact]
        public void CaptureFromParts_NullEverything_ReturnsModeNone()
        {
            var snap = RecorderStateSnapshot.CaptureFromParts(
                activeTree: null,
                recorder: null,
                pendingTree: null,
                pendingTreeState: PendingTreeState.Finalized,
                pendingStandalone: null,
                pendingSplitRecorder: null,
                pendingSplitInProgress: false,
                chain: null,
                currentUT: 0,
                loadedScene: GameScenes.MAINMENU);

            Assert.Equal(RecorderMode.None, snap.mode);
            Assert.Null(snap.treeId);
            Assert.Null(snap.activeRecId);
            Assert.False(snap.recorderExists);
            Assert.False(snap.isRecording);
            Assert.False(snap.pendingTreePresent);
            Assert.False(snap.pendingStandalonePresent);
            Assert.False(snap.pendingSplitPresent);
            Assert.True(double.IsNaN(snap.lastRecordedUT));
        }

        [Fact]
        public void CaptureFromParts_RecorderOnly_ReturnsModeStandalone()
        {
            var rec = new FlightRecorder();

            var snap = RecorderStateSnapshot.CaptureFromParts(
                activeTree: null,
                recorder: rec,
                pendingTree: null,
                pendingTreeState: PendingTreeState.Finalized,
                pendingStandalone: null,
                pendingSplitRecorder: null,
                pendingSplitInProgress: false,
                chain: null,
                currentUT: 100.0,
                loadedScene: GameScenes.FLIGHT);

            Assert.Equal(RecorderMode.Standalone, snap.mode);
            Assert.True(snap.recorderExists);
            Assert.False(snap.isRecording); // not started
            Assert.Equal(0, snap.bufferedPoints);
            Assert.Equal(0, snap.bufferedPartEvents);
            Assert.True(double.IsNaN(snap.lastRecordedUT));
        }

        [Fact]
        public void CaptureFromParts_TreeAndRecorder_ReturnsModeTree()
        {
            var tree = new RecordingTree
            {
                Id = "treeABC123XYZ",
                TreeName = "Kerbal X",
                ActiveRecordingId = "rec0001"
            };
            tree.Recordings["rec0001"] = new Recording
            {
                RecordingId = "rec0001",
                VesselName = "Kerbal X",
                TreeId = tree.Id,
                VesselPersistentId = 4242,
            };
            var rec = new FlightRecorder { ActiveTree = tree };

            var snap = RecorderStateSnapshot.CaptureFromParts(
                activeTree: tree,
                recorder: rec,
                pendingTree: null,
                pendingTreeState: PendingTreeState.Finalized,
                pendingStandalone: null,
                pendingSplitRecorder: null,
                pendingSplitInProgress: false,
                chain: null,
                currentUT: 200.0,
                loadedScene: GameScenes.FLIGHT);

            Assert.Equal(RecorderMode.Tree, snap.mode);
            Assert.Equal("treeABC123XYZ", snap.treeId);
            Assert.Equal("Kerbal X", snap.treeName);
            Assert.Equal("rec0001", snap.activeRecId);
            Assert.Equal("Kerbal X", snap.activeVesselName);
            Assert.Equal((uint)4242, snap.activeVesselPid);
            Assert.Equal(1, snap.treeRecordingCount);
        }

        [Fact]
        public void CaptureFromParts_PendingTreeAndStandalone_BothPresent()
        {
            var pendingTree = new RecordingTree
            {
                Id = "ptreeXYZ",
                TreeName = "Stashed",
            };
            var pendingStandalone = new Recording
            {
                RecordingId = "psaABCD1234",
                VesselName = "Detached",
            };

            var snap = RecorderStateSnapshot.CaptureFromParts(
                activeTree: null,
                recorder: null,
                pendingTree: pendingTree,
                pendingTreeState: PendingTreeState.Limbo,
                pendingStandalone: pendingStandalone,
                pendingSplitRecorder: null,
                pendingSplitInProgress: false,
                chain: null,
                currentUT: 300.0,
                loadedScene: GameScenes.FLIGHT);

            Assert.True(snap.pendingTreePresent);
            Assert.Equal(PendingTreeState.Limbo, snap.pendingTreeState);
            Assert.Equal("ptreeXYZ", snap.pendingTreeId);
            Assert.True(snap.pendingStandalonePresent);
            Assert.Equal("psaABCD1234", snap.pendingStandaloneRecId);
        }

        [Fact]
        public void CaptureFromParts_ChainManagerState_Captured()
        {
            var chain = new ChainSegmentManager();
            chain.ActiveChainId = "chainABCDEFG";
            chain.ActiveChainNextIndex = 3;
            chain.ContinuationVesselPid = 1111;
            chain.UndockContinuationPid = 2222;

            var snap = RecorderStateSnapshot.CaptureFromParts(
                activeTree: null,
                recorder: null,
                pendingTree: null,
                pendingTreeState: PendingTreeState.Finalized,
                pendingStandalone: null,
                pendingSplitRecorder: null,
                pendingSplitInProgress: false,
                chain: chain,
                currentUT: 400.0,
                loadedScene: GameScenes.FLIGHT);

            Assert.Equal("chainABCDEFG", snap.chainActiveChainId);
            Assert.Equal(3, snap.chainNextIndex);
            Assert.Equal((uint)1111, snap.chainContinuationPid);
            Assert.Equal((uint)2222, snap.chainUndockContinuationPid);
        }

        // ----- Recording.DebugName format -----

        [Fact]
        public void DebugName_StandaloneRecording_FormatsCorrectly()
        {
            var rec = new Recording
            {
                RecordingId = "abcdef12345678",
                VesselName = "KerbalX",
            };

            // Standalone, no chain
            Assert.Equal("rec[abcdef12|KerbalX|sa|-]", rec.DebugName);
        }

        [Fact]
        public void DebugName_TreeRecordingWithChain_FormatsCorrectly()
        {
            var rec = new Recording
            {
                RecordingId = "deadbeef0000",
                VesselName = "Stage 2",
                TreeId = "treeXYZ",
                ChainIndex = 2,
            };

            Assert.Equal("rec[deadbeef|Stage 2|tree|2]", rec.DebugName);
        }

        [Fact]
        public void DebugName_TreeRecordingWithoutChain_FormatsCorrectly()
        {
            var rec = new Recording
            {
                RecordingId = "tree9999abc",
                VesselName = "Probe",
                TreeId = "treeXYZ",
                // ChainIndex defaults to -1
            };

            Assert.Equal("rec[tree9999|Probe|tree|-]", rec.DebugName);
        }

        [Fact]
        public void DebugName_NullRecordingId_RendersAsDash()
        {
            var rec = new Recording
            {
                RecordingId = null,
                VesselName = "Anon",
            };

            Assert.Equal("rec[-|Anon|sa|-]", rec.DebugName);
        }

        [Fact]
        public void DebugName_ShortRecordingId_NotTruncated()
        {
            var rec = new Recording
            {
                RecordingId = "abc",
                VesselName = "Tiny",
            };

            Assert.Equal("rec[abc|Tiny|sa|-]", rec.DebugName);
        }

        [Fact]
        public void DebugName_EmptyVesselName_RendersAsDash()
        {
            var rec = new Recording
            {
                RecordingId = "12345678abcd",
                VesselName = "",
            };

            Assert.Equal("rec[12345678|-|sa|-]", rec.DebugName);
        }

        // ----- ParsekLog.RecState format -----

        [Fact]
        public void RecState_NoneSnapshot_EmitsCanonicalLine()
        {
            var snap = RecorderStateSnapshot.CaptureFromParts(
                null, null, null, PendingTreeState.Finalized, null,
                null, false, null,
                17000.5, GameScenes.SPACECENTER);

            ParsekLog.RecState("OnFlightReady", snap);

            Assert.Single(logLines);
            string line = logLines[0];
            Assert.Contains("[RecState]", line);
            Assert.Contains("[#1]", line);
            Assert.Contains("[OnFlightReady]", line);
            Assert.Contains("mode=none", line);
            Assert.Contains("tree=-", line);
            Assert.Contains("rec=-", line);
            Assert.Contains("rec.prev=-", line);
            Assert.Contains("rec.live=F/F", line);
            Assert.Contains("rec.buf=0/0/0", line);
            Assert.Contains("lastUT=-", line);
            Assert.Contains("tree.recs=0/0", line);
            Assert.Contains("pend.tree=-", line);
            Assert.Contains("pend.sa=-", line);
            Assert.Contains("pend.split=F/F", line);
            Assert.Contains("chain=-", line);
            Assert.Contains("ut=17000.5", line);
            Assert.Contains("scene=SPACECENTER", line);
        }

        [Fact]
        public void RecState_TreeMode_EmitsTreeFields()
        {
            var tree = new RecordingTree
            {
                Id = "treeABCDEFGH",
                TreeName = "Kerbal X",
                ActiveRecordingId = "rec0001234567",
            };
            tree.Recordings["rec0001234567"] = new Recording
            {
                RecordingId = "rec0001234567",
                VesselName = "Kerbal X",
                TreeId = tree.Id,
                VesselPersistentId = 9999,
            };
            tree.BackgroundMap[1234] = "rec0001234567";

            var snap = RecorderStateSnapshot.CaptureFromParts(
                tree, null, null, PendingTreeState.Finalized, null,
                null, false, null,
                17050.0, GameScenes.FLIGHT);

            ParsekLog.RecState("OnSave:pre", snap);

            string line = logLines[0];
            Assert.Contains("mode=tree", line);
            Assert.Contains("tree=treeABCD|Kerbal X", line);
            Assert.Contains("rec=rec00012|Kerbal X|pid=9999", line);
            Assert.Contains("tree.recs=1/1", line);
        }

        [Fact]
        public void RecState_PendingSlotsBoth_EmitsBoth()
        {
            var pendingTree = new RecordingTree
            {
                Id = "ptreeABCDEFG",
                TreeName = "Stashed",
            };
            var pendingStandalone = new Recording
            {
                RecordingId = "saABCDEFGH1234",
                VesselName = "Sa",
            };

            var snap = RecorderStateSnapshot.CaptureFromParts(
                null, null, pendingTree, PendingTreeState.Limbo, pendingStandalone,
                null, false, null,
                17100.0, GameScenes.FLIGHT);

            ParsekLog.RecState("OnLoad:limbo-dispatched", snap);

            string line = logLines[0];
            Assert.Contains("pend.tree=ptreeABC:Limbo", line);
            Assert.Contains("pend.sa=saABCDEF", line);
        }

        [Fact]
        public void RecState_PendingSplit_EmitsBothFlags()
        {
            var split = new FlightRecorder();
            var snap = RecorderStateSnapshot.CaptureFromParts(
                null, null, null, PendingTreeState.Finalized, null,
                split, true, null,
                17200.0, GameScenes.FLIGHT);

            ParsekLog.RecState("CreateSplitBranch:entry", snap);

            string line = logLines[0];
            Assert.Contains("pend.split=T/T", line);
        }

        [Fact]
        public void RecState_ChainAuxFields_EmittedWhenNonzero()
        {
            var chain = new ChainSegmentManager
            {
                ActiveChainId = "chainXYZ",
                ActiveChainNextIndex = 5,
                ContinuationVesselPid = 1234,
                UndockContinuationPid = 5678,
            };

            var snap = RecorderStateSnapshot.CaptureFromParts(
                null, null, null, PendingTreeState.Finalized, null,
                null, false, chain,
                17300.0, GameScenes.FLIGHT);

            ParsekLog.RecState("OnVesselSwitchComplete:entry", snap);

            string line = logLines[0];
            Assert.Contains("chain=chainXYZ|idx=5", line);
            Assert.Contains("chain.cont=1234", line);
            Assert.Contains("chain.undock=5678", line);
        }

        [Fact]
        public void RecState_LongVesselName_TruncatedWithEllipsis()
        {
            // 40-char vessel name exceeds the 32-char cap (stock max ~30, but mods
            // can produce longer names — line length must stay bounded).
            string longName = new string('X', 40);
            var tree = new RecordingTree
            {
                Id = "treeLongName",
                TreeName = new string('T', 50), // also long
                ActiveRecordingId = "recID12345678",
            };
            tree.Recordings["recID12345678"] = new Recording
            {
                RecordingId = "recID12345678",
                VesselName = longName,
                TreeId = tree.Id,
            };

            var snap = RecorderStateSnapshot.CaptureFromParts(
                tree, null, null, PendingTreeState.Finalized, null,
                null, false, null,
                17400.0, GameScenes.FLIGHT);

            ParsekLog.RecState("LongName", snap);

            string line = logLines[0];
            // Vessel name truncated to 32 X's + "..."
            Assert.Contains("|" + new string('X', 32) + "...", line);
            Assert.DoesNotContain(new string('X', 33), line);
            // Tree name also truncated
            Assert.Contains("|" + new string('T', 32) + "...", line);
        }

        [Fact]
        public void RecState_LocaleSafe_UsesInvariantCulture()
        {
            // Save current locale and switch to one that uses comma as decimal separator
            var originalCulture = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");

                var snap = RecorderStateSnapshot.CaptureFromParts(
                    null, null, null, PendingTreeState.Finalized, null,
                    null, false, null,
                    17000.5, GameScenes.FLIGHT);

                ParsekLog.RecState("LocaleTest", snap);

                string line = logLines[0];
                // Must use period decimal separator regardless of system locale
                Assert.Contains("ut=17000.5", line);
                Assert.DoesNotContain("ut=17000,5", line);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = originalCulture;
            }
        }

        // ----- Sequence number monotonicity -----

        [Fact]
        public void RecState_SequenceNumbers_AreStrictlyIncreasing()
        {
            var snap = RecorderStateSnapshot.CaptureFromParts(
                null, null, null, PendingTreeState.Finalized, null,
                null, false, null,
                0, GameScenes.FLIGHT);

            const int N = 100;
            for (int i = 0; i < N; i++)
                ParsekLog.RecState("seq", snap);

            Assert.Equal(N, logLines.Count);

            int prevSeq = 0;
            for (int i = 0; i < N; i++)
            {
                string line = logLines[i];
                int hashIdx = line.IndexOf("[#", StringComparison.Ordinal);
                int closeIdx = line.IndexOf(']', hashIdx + 1);
                int seq = int.Parse(line.Substring(hashIdx + 2, closeIdx - hashIdx - 2));
                Assert.True(seq > prevSeq, $"Sequence not increasing at line {i}: prev={prevSeq}, curr={seq}");
                prevSeq = seq;
            }
        }

        // ----- prevRecId transition semantics -----

        [Fact]
        public void RecState_PrevRecId_OnlyShownOnTransition()
        {
            var rec1 = MakeSnapshotWithRecId("recAAAA1111");
            var rec1Again = MakeSnapshotWithRecId("recAAAA1111");
            var rec2 = MakeSnapshotWithRecId("recBBBB2222");
            var rec1Back = MakeSnapshotWithRecId("recAAAA1111");

            ParsekLog.RecState("p1", rec1);          // first emission, no prev
            ParsekLog.RecState("p2", rec1Again);     // same rec, no prev
            ParsekLog.RecState("p3", rec2);          // transition, prev = recAAAA1111
            ParsekLog.RecState("p4", rec1Back);      // transition back, prev = recBBBB2222

            Assert.Contains("rec.prev=-", logLines[0]);
            Assert.Contains("rec.prev=-", logLines[1]);
            Assert.Contains("rec.prev=recAAAA1", logLines[2]);
            Assert.Contains("rec.prev=recBBBB2", logLines[3]);
        }

        private static RecorderStateSnapshot MakeSnapshotWithRecId(string recId)
        {
            var tree = new RecordingTree
            {
                Id = "treeXYZ",
                TreeName = "T",
                ActiveRecordingId = recId,
            };
            tree.Recordings[recId] = new Recording
            {
                RecordingId = recId,
                VesselName = "V",
                TreeId = tree.Id,
            };
            return RecorderStateSnapshot.CaptureFromParts(
                tree, null, null, PendingTreeState.Finalized, null,
                null, false, null,
                17000.0, GameScenes.FLIGHT);
        }

        // ----- Integration phase-sequence test (the EVA + F5 + F9 repro walk) -----

        [Fact]
        public void RecState_PhaseSequence_StandaloneToTreePromotionToQuickloadResume()
        {
            // Phase 1: standalone recording in flight
            var snap1 = RecorderStateSnapshot.CaptureFromParts(
                activeTree: null,
                recorder: new FlightRecorder(),
                pendingTree: null,
                pendingTreeState: PendingTreeState.Finalized,
                pendingStandalone: null,
                pendingSplitRecorder: null,
                pendingSplitInProgress: false,
                chain: new ChainSegmentManager(),
                currentUT: 17000.0,
                loadedScene: GameScenes.FLIGHT);
            ParsekLog.RecState("start-standalone", snap1);

            // Phase 2: F5 quicksave taken
            ParsekLog.RecState("OnSave:pre", snap1);

            // Phase 3: tree promotion (e.g. EVA → tree branch)
            var tree = new RecordingTree
            {
                Id = "treeXYZABCDEF",
                TreeName = "Bob",
                ActiveRecordingId = "evaRec0001234",
            };
            tree.Recordings["evaRec0001234"] = new Recording
            {
                RecordingId = "evaRec0001234",
                VesselName = "Bob",
                TreeId = tree.Id,
            };
            var snap2 = RecorderStateSnapshot.CaptureFromParts(
                tree, new FlightRecorder { ActiveTree = tree },
                null, PendingTreeState.Finalized, null, null, false,
                new ChainSegmentManager(),
                17050.0, GameScenes.FLIGHT);
            ParsekLog.RecState("PromoteToTreeForBreakup:exit", snap2);

            // Phase 4: another F5 quicksave (now in tree mode)
            ParsekLog.RecState("OnSave:pre", snap2);

            // Phase 5: scene change → StashTreeLimbo
            var snap3 = RecorderStateSnapshot.CaptureFromParts(
                tree, null, null, PendingTreeState.Finalized, null, null, false,
                new ChainSegmentManager(),
                17051.0, GameScenes.FLIGHT);
            ParsekLog.RecState("StashTreeLimbo:pre", snap3);

            // Phase 6: F9 quickload → OnLoad sees tree restored to pending-Limbo
            var snap4 = RecorderStateSnapshot.CaptureFromParts(
                null, null, tree, PendingTreeState.Limbo, null, null, false, null,
                17050.0, GameScenes.FLIGHT);
            ParsekLog.RecState("TryRestoreActiveTreeNode:stashed", snap4);
            ParsekLog.RecState("OnLoad:limbo-dispatched", snap4);

            // Phase 7: restore coroutine
            ParsekLog.RecState("Restore:start", snap4);
            ParsekLog.RecState("Restore:matched", snap4);

            // Phase 8: post-restore — back to live tree mode
            var snap5 = RecorderStateSnapshot.CaptureFromParts(
                tree, new FlightRecorder { ActiveTree = tree }, null, PendingTreeState.Finalized,
                null, null, false, new ChainSegmentManager(),
                17050.5, GameScenes.FLIGHT);
            ParsekLog.RecState("Restore:after-start", snap5);

            // Verify: all 10 phases logged in order with strictly increasing sequence numbers
            Assert.Equal(10, logLines.Count);
            string[] expectedPhases = {
                "start-standalone", "OnSave:pre", "PromoteToTreeForBreakup:exit",
                "OnSave:pre", "StashTreeLimbo:pre",
                "TryRestoreActiveTreeNode:stashed", "OnLoad:limbo-dispatched",
                "Restore:start", "Restore:matched", "Restore:after-start"
            };
            for (int i = 0; i < expectedPhases.Length; i++)
            {
                Assert.Contains($"[{expectedPhases[i]}]", logLines[i]);
            }

            // Mode transitions visible at expected points
            Assert.Contains("mode=sa", logLines[0]);   // start standalone
            Assert.Contains("mode=sa", logLines[1]);   // OnSave:pre still standalone
            Assert.Contains("mode=tree", logLines[2]); // promoted
            Assert.Contains("mode=tree", logLines[3]); // OnSave in tree mode
            Assert.Contains("mode=tree", logLines[4]); // stash tree pre (recorder gone but activeTree still set)
            Assert.Contains("mode=none", logLines[5]); // OnLoad: only pending tree
            Assert.Contains("mode=none", logLines[6]); // OnLoad limbo dispatch
            Assert.Contains("mode=none", logLines[7]); // Restore start
            Assert.Contains("mode=none", logLines[8]); // Restore matched (still in pending)
            Assert.Contains("mode=tree", logLines[9]); // Restore after-start (live again)

            // pend.tree=Limbo visible during the limbo phases
            Assert.Contains("pend.tree=treeXYZA:Limbo", logLines[5]);
            Assert.Contains("pend.tree=treeXYZA:Limbo", logLines[8]);
        }
    }
}
