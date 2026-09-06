using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// P14 - the Disassembled terminal state: a vessel whose LAST remaining part is
    /// pocketed into an inventory during EVA construction.
    ///
    /// <para>Headless-proven only. There is no live driver: kRPC exposes no EVA
    /// construction API, so no harness mission can pick a part up. Every cell here
    /// drives the pure decision, the guard, the log formatter or a consumer directly;
    /// the one thing they cannot witness is KSP actually raising
    /// <c>onVesselWillDestroy</c> from <c>EVAConstructionModeEditor.PickupPart</c>,
    /// which was established by decompiling KSP 1.12.5 (see
    /// <see cref="VesselDisassemblyClassifier"/>'s contract comment).</para>
    /// </summary>
    [Collection("Sequential")]
    public class DisassembledTerminalStateTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public DisassembledTerminalStateTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ---------------------------------------------------------------
        // The pure classifier: every branch.
        // ---------------------------------------------------------------

        [Fact]
        public void ClassifyVesselDeath_AllThreeConjunctsTrue_IsDisassembled()
        {
            Assert.Equal(
                VesselDeathKind.Disassembled,
                VesselDisassemblyClassifier.ClassifyVesselDeath(
                    evaConstructionModeOpen: true,
                    partCount: 1,
                    dyingPartIsCurrentCargoPart: true));
        }

        [Theory]
        // Mode closed: a crash that happens to coincide with a held cargo part.
        [InlineData(false, 1, true)]
        // Not the last part: a multi-part vessel losing a part is not a vessel ending
        // (and KSP's own grab handler cannot even reach PickupPart for it).
        [InlineData(true, 2, true)]
        [InlineData(true, 0, true)]
        // No object identity with the held cargo part: an ordinary destruction that
        // happens while the construction panel is open.
        [InlineData(true, 1, false)]
        // Every remaining combination, so the whole 2x3x2 cube is covered.
        [InlineData(false, 1, false)]
        [InlineData(false, 2, true)]
        [InlineData(false, 2, false)]
        [InlineData(false, 0, true)]
        [InlineData(false, 0, false)]
        [InlineData(true, 2, false)]
        [InlineData(true, 0, false)]
        public void ClassifyVesselDeath_AnyConjunctFalse_IsDestroyed(
            bool modeOpen, int partCount, bool identityMatch)
        {
            Assert.Equal(
                VesselDeathKind.Destroyed,
                VesselDisassemblyClassifier.ClassifyVesselDeath(
                    modeOpen, partCount, identityMatch));
        }

        [Fact]
        public void ClassifyVesselDeath_EvidenceOverload_AgreesWithPrimitiveOverload()
        {
            var evidence = new VesselDeathEvidence
            {
                EvaConstructionModeOpen = true,
                PartCount = 1,
                DyingPartIsCurrentCargoPart = true,
            };
            Assert.Equal(
                VesselDeathKind.Disassembled,
                VesselDisassemblyClassifier.ClassifyVesselDeath(evidence));

            evidence.PartCount = 3;
            Assert.Equal(
                VesselDeathKind.Destroyed,
                VesselDisassemblyClassifier.ClassifyVesselDeath(evidence));
        }

        [Fact]
        public void ClassifyLiveVesselDeath_NullVessel_FailsClosedToDestroyed()
        {
            VesselDeathEvidence evidence;
            Assert.Equal(
                VesselDeathKind.Destroyed,
                VesselDisassemblyClassifier.ClassifyLiveVesselDeath(null, out evidence));
            Assert.False(evidence.EvaConstructionModeOpen);
            Assert.Equal(0, evidence.PartCount);
            Assert.False(evidence.DyingPartIsCurrentCargoPart);
        }

        // ---------------------------------------------------------------
        // The stamp guard.
        // ---------------------------------------------------------------

        [Fact]
        public void ShouldStampDisassembledTerminal_DisassembledAndUnstamped_True()
        {
            var rec = new Recording { RecordingId = "rec-pocket" };
            Assert.True(ParsekFlight.ShouldStampDisassembledTerminal(
                VesselDeathKind.Disassembled, rec));
        }

        [Fact]
        public void ShouldStampDisassembledTerminal_OrdinaryDestruction_False()
        {
            var rec = new Recording { RecordingId = "rec-crash" };
            Assert.False(ParsekFlight.ShouldStampDisassembledTerminal(
                VesselDeathKind.Destroyed, rec));
        }

        [Fact]
        public void ShouldStampDisassembledTerminal_NullRecording_False()
        {
            Assert.False(ParsekFlight.ShouldStampDisassembledTerminal(
                VesselDeathKind.Disassembled, null));
        }

        [Theory]
        [InlineData(TerminalState.Destroyed)]
        [InlineData(TerminalState.Landed)]
        [InlineData(TerminalState.Disassembled)]
        public void ShouldStampDisassembledTerminal_AlreadyStamped_False(TerminalState existing)
        {
            // The pocket seam is a first-writer, never an override: a recording sealed
            // out of band (identity loss, an earlier destruction) keeps its verdict,
            // and re-running the seam on an already-Disassembled recording is a no-op.
            var rec = new Recording
            {
                RecordingId = "rec-sealed",
                TerminalStateValue = existing,
            };
            Assert.False(ParsekFlight.ShouldStampDisassembledTerminal(
                VesselDeathKind.Disassembled, rec));
        }

        [Fact]
        public void ApplyDisassembledTerminal_StampsTerminalAndEndUT_ButNotVesselDestroyed()
        {
            // The write-set is load-bearing. VesselDestroyed MUST stay false: the seam
            // runs BEFORE BackgroundRecorder.OnBackgroundVesselWillDestroy, whose
            // already-destroyed short circuit keys on that bool and takes a branch
            // (RetireDestroyedBackgroundEntry) that drops loadedStates WITHOUT
            // flushing the accumulated TrackSections, skips the persist, and drains
            // the BackgroundMap so DeferredDestructionCheck never runs. Setting the
            // flag here costs the pocketed debris its frames.
            var rec = new Recording { RecordingId = "rec-pocket" };
            Assert.False(rec.VesselDestroyed);

            ParsekFlight.ApplyDisassembledTerminal(rec, 1234.5);

            Assert.Equal(TerminalState.Disassembled, rec.TerminalStateValue);
            Assert.Equal(1234.5, rec.ExplicitEndUT);
            Assert.False(rec.VesselDestroyed);
        }

        [Fact]
        public void ApplyDisassembledTerminal_NullRecording_DoesNotThrow()
        {
            ParsekFlight.ApplyDisassembledTerminal(null, 0.0);
        }

        // ---------------------------------------------------------------
        // The log line: grep-stable tokens, invariant formatting.
        // ---------------------------------------------------------------

        [Fact]
        public void FormatDisassembledTerminalLog_CarriesTheGrepStableTokens()
        {
            string line = ParsekFlight.FormatDisassembledTerminalLog(
                "Booster Debris", 4211u, 1, "rec-pocket", 1500.25);

            Assert.Contains("Recording terminal: kind=Disassembled", line);
            Assert.Contains("reason=last-part-stored", line);
            Assert.Contains("vessel='Booster Debris'", line);
            Assert.Contains("pid=4211", line);
            Assert.Contains("parts=1", line);
            Assert.Contains("rec=rec-pocket", line);
            Assert.Contains("ut=1500.250", line);
        }

        [Fact]
        public void FormatDisassembledTerminalLog_NullNames_RenderPlaceholders()
        {
            string line = ParsekFlight.FormatDisassembledTerminalLog(null, 7u, 1, null, 0.0);
            Assert.Contains("vessel='(null)'", line);
            Assert.Contains("rec=(null)", line);
        }

        [Fact]
        public void FormatDisassembledTerminalLog_IsInvariantUnderACommaCulture()
        {
            // Proves the site is invariant (this test reads the string, so the
            // production formatter must not depend on the OS culture).
            var saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string line = ParsekFlight.FormatDisassembledTerminalLog(
                    "Probe", 1u, 1, "rec", 12.5);
                Assert.Contains("ut=12.500", line);
                Assert.DoesNotContain("ut=12,500", line);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        // ---------------------------------------------------------------
        // Serialization: the codec round trip and the (unmoved) schema gate.
        // ---------------------------------------------------------------

        [Fact]
        public void Codec_RoundTripsDisassembledTerminal()
        {
            var rec = new Recording
            {
                RecordingId = "rec-pocket",
                VesselName = "Dropped Girder",
                TerminalStateValue = TerminalState.Disassembled,
                VesselDestroyed = true,
                ExplicitEndUT = 4242.0,
            };

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);

            // The wire form is the NUMERIC enum value, which is what saveparse.py's
            // TERMINAL_STATE_NAMES indexes.
            Assert.Equal("8", node.GetValue("terminalState"));

            var restored = new Recording();
            RecordingTree.LoadRecordingFrom(node, restored);
            Assert.Equal(TerminalState.Disassembled, restored.TerminalStateValue);
        }

        [Fact]
        public void SchemaGeneration_StaysAtFour_AdditiveEnumMemberIsNotASchemaChange()
        {
            // The operator's 2026-09-06 ruling: appending an enum member renames no
            // key, adds no field and changes no binary layout, so it is NOT a schema
            // change. Generation 5 stays reserved for the co-op shape change.
            Assert.Equal(4, RecordingStore.CurrentRecordingSchemaGeneration);
            Assert.Equal(1, RecordingStore.CurrentRecordingFormatVersion);

            string reason;
            Assert.True(RecordingStore.IsRecordingSchemaCompatible(1, 4, out reason));
            Assert.Null(reason);

            Assert.False(RecordingStore.IsRecordingSchemaCompatible(1, 3, out reason));
            Assert.Equal("generation-older", reason);

            Assert.False(RecordingStore.IsRecordingSchemaCompatible(1, 5, out reason));
            Assert.Equal("generation-newer", reason);
        }

        // ---------------------------------------------------------------
        // Consumers.
        // ---------------------------------------------------------------

        [Fact]
        public void TerminalKindClassifier_Disassembled_IsLandedNotCrashed()
        {
            // Landed => the supersede commit seals Immutable. Crashed would keep the
            // slot rewindable, which is wrong: nothing continues after a pocket.
            var rec = new Recording
            {
                RecordingId = "rec-pocket",
                TerminalStateValue = TerminalState.Disassembled,
            };
            Assert.Equal(TerminalKind.Landed, TerminalKindClassifier.Classify(rec));
        }

        [Fact]
        public void RecordingTree_DisassembledLeaf_IsNotSpawnableAndIsTerminal()
        {
            var rec = new Recording
            {
                RecordingId = "rec-pocket",
                TerminalStateValue = TerminalState.Disassembled,
                VesselSnapshot = new ConfigNode("VESSEL"),
            };
            Assert.False(RecordingTree.IsSpawnableLeaf(rec));

            var recordings = new Dictionary<string, Recording> { { "rec-pocket", rec } };
            Assert.True(RecordingTree.AreAllLeavesTerminal(
                recordings, activeRecordingId: null, activeVesselDestroyed: false));
        }

        [Fact]
        public void GhostPlaybackLogic_DisassembledIsNotSpawnable()
        {
            Assert.False(GhostPlaybackLogic.IsSpawnableTerminal(TerminalState.Disassembled));
        }

        [Fact]
        public void GhostChainWalker_DisassembledLeaf_TreeIsFullyTerminated()
        {
            var tree = new RecordingTree
            {
                Id = "tree-pocket",
                Recordings = new Dictionary<string, Recording>
                {
                    {
                        "rec-pocket",
                        new Recording
                        {
                            RecordingId = "rec-pocket",
                            TerminalStateValue = TerminalState.Disassembled,
                        }
                    },
                },
            };
            Assert.True(GhostChainWalker.IsTreeFullyTerminated(tree));
        }

        [Fact]
        public void MissionRowWording_Disassembled_ReadsAsAnEndingNotALoss()
        {
            // MissionCompositionBuilder.TerminalName is the single source for the
            // Missions tab header word, the flattened per-vessel rows (their EndEvent),
            // the event digest verb and the route status cell.
            Assert.Equal(
                "Disassembled",
                MissionCompositionBuilder.TerminalName(TerminalState.Disassembled));
            Assert.Equal(
                "Disassembled",
                TimelineEntryDisplay.FormatTerminalState(TerminalState.Disassembled));
        }

        [Fact]
        public void RecordingsTable_EndPosition_RendersDisassembledWithBody()
        {
            var rec = new Recording
            {
                RecordingId = "rec-pocket",
                TerminalStateValue = TerminalState.Disassembled,
                StartBodyName = "Minmus",
            };
            Assert.Equal("Disassembled, Minmus", RecordingsTableFormatters.FormatEndPosition(rec));

            var noBody = new Recording
            {
                RecordingId = "rec-pocket-2",
                TerminalStateValue = TerminalState.Disassembled,
            };
            Assert.Equal("Disassembled", RecordingsTableFormatters.FormatEndPosition(noBody));
        }

        [Fact]
        public void Ledger_Disassembled_ProducesNoRecoveryEarning()
        {
            // A pocket is NOT a recovery: no funds, no science, no reputation. The
            // recovery correlator keys strictly on TerminalState.Recovered, so the
            // funds delta sitting in the points must stay unclaimed.
            var rec = new Recording
            {
                RecordingId = "rec-disassembled-ledger",
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Disassembled,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 40000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, funds = 48500.0 });
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = LedgerOrchestrator.CreateVesselCostActions(
                "rec-disassembled-ledger", 100.0, 200.0);

            Assert.DoesNotContain(actions, a =>
                a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.Recovery);
            // The build cost is orthogonal and still recorded, so the assertion above
            // is not passing merely because the whole action list is empty.
            Assert.Contains(actions, a => a.Type == GameActionType.FundsSpending);
        }

        [Fact]
        public void ResurrectionEligibility_Disassembled_IsNotARecoveryAnchor()
        {
            // The resurrection / retirement walk is the second place a terminal state
            // can be mistaken for a recovery. It also keys strictly on Recovered.
            var rec = new Recording
            {
                RecordingId = "rec-disassembled-resurrect",
                TerminalStateValue = TerminalState.Disassembled,
                VesselPersistentId = 4211u,
            };

            var result = ResurrectionRetirementEligibility.Classify(
                new List<(uint pid, string guid)> { (4211u, null) },
                new List<Recording> { rec },
                new List<GameAction>
                {
                    new GameAction
                    {
                        Type = GameActionType.FundsEarning,
                        FundsSource = FundsEarningSource.Recovery,
                        RecordingId = "rec-disassembled-resurrect",
                        UT = 300.0,
                    },
                },
                retireCutoffUT: 200.0);

            Assert.Empty(result);
        }
    }
}
