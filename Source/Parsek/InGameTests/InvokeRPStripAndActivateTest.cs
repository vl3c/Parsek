using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Phase 6 of Rewind-to-Staging (design §6.3 + §6.4): end-to-end runtime
    /// test for the rewind invocation flow. The test runs in FLIGHT so the
    /// live <c>FlightGlobals</c>, <c>PartLoader</c>, and
    /// <see cref="ParsekScenario"/> instance are all available.
    ///
    /// <para>
    /// Strategy: locate an Unfinished Flight recording in the current save,
    /// resolve its RewindPoint + slot, assert the Phase 6 preconditions
    /// (<c>CanInvoke</c> true; quicksave exists; PartLoader precondition
    /// passes), and exercise the atomic marker-write directly via
    /// <see cref="RewindInvoker.AtomicMarkerWrite"/>. We deliberately do not
    /// trigger the full pre-load / post-load invocation (see
    /// <see cref="RewindInvoker.StartInvoke"/>) because loading the quicksave
    /// is a destructive scene transition that cannot be undone inside a test.
    /// Instead we assert the testable guarantees:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>CanInvoke returns true for the RP.</description></item>
    ///   <item><description>Post-AtomicMarkerWrite, a provisional recording exists with MergeState=NotCommitted and SupersedeTargetId=origin.</description></item>
    ///   <item><description>ParsekScenario.ActiveReFlySessionMarker is set to the synthetic session id.</description></item>
    ///   <item><description>The marker's RewindPointId / OriginChildRecordingId round-trip correctly.</description></item>
    /// </list>
    /// <para>
    /// On teardown, the test removes the provisional + marker so the save
    /// does not grow stale state if the player continues playing.
    /// </para>
    /// </summary>
    public class InvokeRPStripAndActivateTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "Rewind invocation: preconditions + atomic provisional/marker write")]
        public void InvokeRPStripAndActivate()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            // Phase 7.5 guard would veto invocation with an active session already
            // present; tests must run from a clean slate.
            if (scenario.ActiveReFlySessionMarker != null)
            {
                InGameAssert.Skip("ActiveReFlySessionMarker is already set — " +
                    "finish / cancel the existing session and rerun.");
                return;
            }

            var members = UnfinishedFlightsGroup.ComputeMembers();
            if (members == null || members.Count == 0)
            {
                InGameAssert.Skip("No Unfinished Flight in current save — " +
                    "stage-crash a controllable child in a multi-controllable split, save, and rerun.");
                return;
            }

            // Find the first member whose parent RP has a live quicksave on disk.
            Recording target = null;
            RewindPoint rp = null;
            ChildSlot slot = null;
            for (int i = 0; i < members.Count; i++)
            {
                var rec = members[i];
                if (rec == null) continue;
                var candidate = FindRp(scenario, rec);
                if (candidate == null) continue;
                string abs = RewindInvoker.ResolveAbsoluteQuicksavePath(candidate);
                if (string.IsNullOrEmpty(abs) || !File.Exists(abs)) continue;
                int slotIdx = ResolveSlotIndex(candidate, rec);
                if (slotIdx < 0) continue;
                target = rec;
                rp = candidate;
                slot = candidate.ChildSlots[slotIdx];
                break;
            }

            if (target == null || rp == null || slot == null)
            {
                InGameAssert.Skip("No Unfinished Flight has an on-disk quicksave + resolvable slot " +
                    "in the current save; cannot exercise Phase 6.");
                return;
            }

            ParsekLog.Info("RewindTest",
                $"InvokeRPStripAndActivate: target rec={target.RecordingId} rp={rp.RewindPointId} " +
                $"slotIdx={slot.SlotIndex}");

            // Clear the precondition cache so the test sees a fresh CanInvoke result.
            RewindInvoker.PreconditionCache.InvalidateForTesting();
            string reason;
            bool canInvoke = RewindInvoker.CanInvoke(rp, out reason);
            InGameAssert.IsTrue(canInvoke,
                $"CanInvoke(rp={rp.RewindPointId}) expected true; got false reason='{reason}'");

            // Build a synthetic strip result (we do not actually strip vessels —
            // that would destroy the player's live flight). The atomic block
            // only needs SelectedPid to be non-zero.
            uint stubPid = 0xABCDEF01u;
            var stripResult = new PostLoadStripResult
            {
                SelectedVessel = null,
                SelectedPid = stubPid,
                StrippedPids = new List<uint>(),
                GhostsGuarded = 0,
                LeftAlone = 0,
                FallbackMatches = 0,
            };

            string sessionId = "sess_ingame_test_" + System.Guid.NewGuid().ToString("N").Substring(0, 8);

            // Snapshot pre-state so teardown is precise.
            int prevCommitted = RecordingStore.CommittedRecordings.Count;

            Recording addedProvisional = null;
            try
            {
                RewindInvoker.AtomicMarkerWrite(rp, slot, stripResult, sessionId);

                InGameAssert.IsNotNull(scenario.ActiveReFlySessionMarker,
                    "Marker expected non-null after AtomicMarkerWrite");
                InGameAssert.IsTrue(
                    scenario.ActiveReFlySessionMarker.SessionId == sessionId,
                    $"Marker SessionId mismatch: expected '{sessionId}', got " +
                    $"'{scenario.ActiveReFlySessionMarker.SessionId}'");
                InGameAssert.IsTrue(
                    scenario.ActiveReFlySessionMarker.RewindPointId == rp.RewindPointId,
                    $"Marker RewindPointId mismatch: expected '{rp.RewindPointId}', got " +
                    $"'{scenario.ActiveReFlySessionMarker.RewindPointId}'");
                InGameAssert.IsTrue(
                    scenario.ActiveReFlySessionMarker.OriginChildRecordingId == slot.OriginChildRecordingId,
                    $"Marker OriginChildRecordingId mismatch");

                // Provisional added: its RecordingId must match the marker's ActiveReFlyRecordingId.
                string provisionalId = scenario.ActiveReFlySessionMarker.ActiveReFlyRecordingId;
                InGameAssert.IsTrue(!string.IsNullOrEmpty(provisionalId),
                    "Marker.ActiveReFlyRecordingId is null/empty");
                InGameAssert.IsTrue(
                    RecordingStore.CommittedRecordings.Count == prevCommitted + 1,
                    $"CommittedRecordings count expected {prevCommitted + 1}; got " +
                    $"{RecordingStore.CommittedRecordings.Count}");

                // Locate the provisional.
                var committed = RecordingStore.CommittedRecordings;
                for (int i = 0; i < committed.Count; i++)
                {
                    if (committed[i] == null) continue;
                    if (committed[i].RecordingId == provisionalId)
                    {
                        addedProvisional = committed[i];
                        break;
                    }
                }
                InGameAssert.IsNotNull(addedProvisional,
                    $"Provisional rec id={provisionalId} not found in CommittedRecordings");
                InGameAssert.IsTrue(addedProvisional.MergeState == MergeState.NotCommitted,
                    $"Provisional MergeState expected NotCommitted; got {addedProvisional.MergeState}");
                InGameAssert.IsTrue(
                    addedProvisional.SupersedeTargetId == slot.OriginChildRecordingId,
                    $"Provisional SupersedeTargetId mismatch");
                InGameAssert.IsTrue(
                    addedProvisional.ProvisionalForRpId == rp.RewindPointId,
                    $"Provisional ProvisionalForRpId mismatch");
                InGameAssert.IsTrue(
                    addedProvisional.CreatingSessionId == sessionId,
                    $"Provisional CreatingSessionId mismatch");
                InGameAssert.IsTrue(
                    addedProvisional.VesselPersistentId == stubPid,
                    $"Provisional VesselPersistentId mismatch: expected {stubPid}; got " +
                    $"{addedProvisional.VesselPersistentId}");
            }
            finally
            {
                // Teardown: remove the provisional + clear the marker so the save
                // is not contaminated if the test fails halfway.
                if (addedProvisional != null)
                    RecordingStore.RemoveCommittedInternal(addedProvisional);
                scenario.ActiveReFlySessionMarker = null;
                Parsek.Rendering.RenderSessionState.Clear("ingame-test-teardown");
            }

            ParsekLog.Info("RewindTest",
                $"InvokeRPStripAndActivate: all assertions passed (sess={sessionId})");
        }

        private static RewindPoint FindRp(ParsekScenario scenario, Recording rec)
        {
            if (scenario?.RewindPoints == null || rec == null) return null;
            string bpId = rec.ParentBranchPointId;
            if (string.IsNullOrEmpty(bpId)) return null;
            for (int i = 0; i < scenario.RewindPoints.Count; i++)
            {
                var candidate = scenario.RewindPoints[i];
                if (candidate == null) continue;
                if (candidate.BranchPointId == bpId) return candidate;
            }
            return null;
        }

        private static int ResolveSlotIndex(RewindPoint rp, Recording rec)
        {
            if (rp?.ChildSlots == null || rec == null) return -1;
            var scenario = ParsekScenario.Instance;
            IReadOnlyList<RecordingSupersedeRelation> supersedes = scenario?.RecordingSupersedes
                ?? (IReadOnlyList<RecordingSupersedeRelation>)new List<RecordingSupersedeRelation>();
            for (int i = 0; i < rp.ChildSlots.Count; i++)
            {
                var slot = rp.ChildSlots[i];
                if (slot == null) continue;
                if (slot.EffectiveRecordingId(supersedes) == rec.RecordingId
                    || slot.OriginChildRecordingId == rec.RecordingId)
                {
                    return i;
                }
            }
            return -1;
        }
    }
}
