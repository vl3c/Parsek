using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 6 of Rewind-to-Staging (design §6.3 step 4 phase 1+2): guards the
    /// atomic provisional-recording add + ReFlySessionMarker write in
    /// <see cref="RewindInvoker.AtomicMarkerWrite"/>. Verifies the two
    /// checkpoints (A: before/after provisional, B: before/after marker) run
    /// synchronously with no yield between them, and that a throw between the
    /// provisional add and the marker write rolls back the provisional.
    /// </summary>
    [Collection("Sequential")]
    public class AtomicMarkerWriteTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public AtomicMarkerWriteTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RewindInvoker.CheckpointHookForTesting = null;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;

            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RewindInvoker.CheckpointHookForTesting = null;
        }

        private static ParsekScenario MakeScenario()
        {
            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint>(),
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            return scenario;
        }

        private static (RewindPoint rp, ChildSlot slot) MakeRpAndSlot()
        {
            var slot = new ChildSlot
            {
                SlotIndex = 0,
                OriginChildRecordingId = "recOrigin",
                Controllable = true,
            };
            var rp = new RewindPoint
            {
                RewindPointId = "rp_atomic",
                BranchPointId = "bp_atomic",
                ChildSlots = new List<ChildSlot> { slot },
                UT = 42.0,
            };
            return (rp, slot);
        }

        private static PostLoadStripResult MakeStripResult(uint selectedPid = 12345u)
        {
            return new PostLoadStripResult
            {
                SelectedVessel = null, // tests do not construct live Unity objects
                SelectedPid = selectedPid,
                StrippedPids = new List<uint>(),
                GhostsGuarded = 0,
                LeftAlone = 0,
                FallbackMatches = 0,
            };
        }

        [Fact]
        public void Phase1And2_SameSyncBlock_NoInterleaving()
        {
            var scenario = MakeScenario();
            var (rp, slot) = MakeRpAndSlot();

            var checkpoints = new List<string>();
            int markerWriteCounter = 0;
            int provisionalCountAtMarker = -1;

            RewindInvoker.CheckpointHookForTesting = tag =>
            {
                checkpoints.Add(tag);
                if (tag == "CheckpointB:BeforeMarker")
                {
                    // At this instant, the provisional must already be in the list.
                    provisionalCountAtMarker = RecordingStore.CommittedRecordings.Count;
                }
                if (tag == "CheckpointB:AfterMarker") markerWriteCounter++;
            };

            RewindInvoker.AtomicMarkerWrite(rp, slot, MakeStripResult(), "sess_test");

            // Checkpoint sequence: A:Before -> A:After -> B:Before -> B:After
            // No other call may be interleaved.
            Assert.Equal(new List<string>
            {
                "CheckpointA:BeforeProvisional",
                "CheckpointA:AfterProvisional",
                "CheckpointB:BeforeMarker",
                "CheckpointB:AfterMarker",
            }, checkpoints);

            // Provisional was already in the committed list when marker-write started.
            Assert.Equal(1, provisionalCountAtMarker);

            // Post-block: provisional + marker both present.
            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            Assert.Equal("sess_test", scenario.ActiveReFlySessionMarker.SessionId);
            Assert.Equal(rp.RewindPointId, scenario.ActiveReFlySessionMarker.RewindPointId);
            Assert.Equal(slot.OriginChildRecordingId,
                scenario.ActiveReFlySessionMarker.OriginChildRecordingId);
            Assert.Equal(1, markerWriteCounter);

            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]") && l.Contains("Started sess=sess_test"));
        }

        [Fact]
        public void ExceptionBetween_RemovesProvisional_NoLeak()
        {
            MakeScenario();
            var (rp, slot) = MakeRpAndSlot();

            RewindInvoker.CheckpointHookForTesting = tag =>
            {
                if (tag == "CheckpointB:BeforeMarker")
                    throw new InvalidOperationException("simulated marker failure");
            };

            Assert.Throws<InvalidOperationException>(() =>
            {
                RewindInvoker.AtomicMarkerWrite(rp, slot, MakeStripResult(), "sess_fail");
            });

            // Rollback: provisional removed, marker never written.
            Assert.Empty(RecordingStore.CommittedRecordings);
            Assert.Null(ParsekScenario.Instance.ActiveReFlySessionMarker);
        }
    }
}
