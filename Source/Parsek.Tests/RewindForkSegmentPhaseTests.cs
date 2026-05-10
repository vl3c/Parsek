using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the in-place Re-Fly fork's SegmentPhase / SegmentBodyName
    /// inheritance contract. See
    /// docs/dev/plans/fix-refly-fork-segment-phase-inheritance.md for the
    /// rationale.
    ///
    /// Background: <see cref="RewindInvoker.CopyInheritedIdentityForFork"/>
    /// previously copied <c>SegmentPhase</c> and <c>SegmentBodyName</c> from
    /// the parent recording onto the fork. Those fields describe the
    /// recording's own most-recent segment classification, not the launch
    /// identity, so inheriting them across a Re-Fly fork left the fork with
    /// the parent's terminating phase even when the new flight ended in a
    /// completely different environment (atmo→orbit was the user-reported
    /// repro).
    ///
    /// The fix drops the two inheritance lines and adds an explicit
    /// <see cref="RewindInvoker.TagForkInitialSegmentPhase"/> call at fork
    /// creation that classifies from the live post-Strip vessel via
    /// <see cref="ParsekFlight.TagSegmentPhaseIfMissing"/>. The live-vessel
    /// classification itself is exercised by the in-game test framework
    /// since <c>Vessel</c> is a Unity type — these xUnit tests cover the
    /// pure data-model contract (drop on inherit, no-op on null).
    /// </summary>
    [Collection("Sequential")]
    public class RewindForkSegmentPhaseTests : System.IDisposable
    {
        private readonly List<string> _logLines = new List<string>();

        public RewindForkSegmentPhaseTests()
        {
            ParsekLog.TestSinkForTesting = line => _logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        [Fact]
        public void CopyInheritedIdentityForFork_DoesNotInheritSegmentPhase()
        {
            // Direct contract: parent has a non-empty SegmentPhase reflecting
            // its own terminating segment; the fork must not pick it up.
            var inheritFrom = new Recording
            {
                RecordingId = "parent-rec",
                VesselPersistentId = 1234u,
                VesselName = "Probe",
                SegmentPhase = "atmo",
                SegmentBodyName = "Kerbin",
            };
            var provisional = new Recording { RecordingId = "fork-rec" };

            RewindInvoker.CopyInheritedIdentityForFork(provisional, inheritFrom);

            Assert.Null(provisional.SegmentPhase);
            Assert.Null(provisional.SegmentBodyName);
            // Confirm the helper still copies the launch-identity fields so
            // this test wouldn't pass for a structurally broken inheritance.
            Assert.Equal(1234u, provisional.VesselPersistentId);
            Assert.Equal("Probe", provisional.VesselName);
        }

        [Fact]
        public void TagForkInitialSegmentPhase_NullProvisional_IsNoOp()
        {
            // Defensive: never throw on null inputs.
            RewindInvoker.TagForkInitialSegmentPhase(
                provisional: null, liveVessel: null, sessionId: "sess_test");
            // No assertion — surviving the call without throwing is the contract.
        }

        [Fact]
        public void TagForkInitialSegmentPhase_NullLiveVessel_LogsWarn_LeavesFieldsEmpty()
        {
            var provisional = new Recording { RecordingId = "fork-rec" };

            RewindInvoker.TagForkInitialSegmentPhase(
                provisional, liveVessel: null, sessionId: "sess_test");

            Assert.Null(provisional.SegmentPhase);
            Assert.Null(provisional.SegmentBodyName);
            Assert.Contains(_logLines, l =>
                l.Contains("[Rewind]")
                && l.Contains("[WARN]")
                && l.Contains("TagForkInitialSegmentPhase: live vessel null")
                && l.Contains("rec=fork-rec")
                && l.Contains("sess=sess_test"));
        }

        [Fact]
        public void TagForkInitialSegmentPhase_NullProvisional_NoLog()
        {
            // The helper short-circuits on null provisional before reaching the
            // null-vessel branch, so no log lines are emitted. This is
            // belt-and-suspenders to confirm the order of guards.
            RewindInvoker.TagForkInitialSegmentPhase(
                provisional: null, liveVessel: null, sessionId: "sess_test");

            Assert.DoesNotContain(_logLines, l => l.Contains("TagForkInitialSegmentPhase"));
        }

        [Fact]
        public void CopyInheritedIdentity_ThenTagForkInitialSegmentPhase_OrderIsLoadBearing()
        {
            // Sequencing canary: CopyInheritedIdentityForFork must run BEFORE
            // TagForkInitialSegmentPhase so the runtime tagger's IsNullOrEmpty
            // guard sees an empty SegmentPhase. If a future refactor swaps the
            // order (e.g. tag first, then copy-inherited last), the
            // copy-inherited step would have to re-introduce the inheritance to
            // not regress non-inherited fields — which is exactly the bug this
            // fix addresses.
            //
            // We can't drive TagForkInitialSegmentPhase to a positive outcome
            // without a Unity Vessel, but we can prove that calling
            // CopyInheritedIdentityForFork after a hypothetical pre-set tag
            // does NOT clobber the tag (because we removed the inheritance
            // lines). Combined with TagForkInitialSegmentPhase's guard
            // (delegated to TagSegmentPhaseIfMissing's IsNullOrEmpty check),
            // this confirms the post-fix order is safe in both directions.
            var inheritFrom = new Recording
            {
                RecordingId = "parent-rec",
                SegmentPhase = "atmo",
                SegmentBodyName = "Kerbin",
            };
            var provisional = new Recording
            {
                RecordingId = "fork-rec",
                // Pretend a hypothetical reordered flow tagged us first.
                SegmentPhase = "exo",
                SegmentBodyName = "Kerbin",
            };

            RewindInvoker.CopyInheritedIdentityForFork(provisional, inheritFrom);

            // Prior values survive — no inheritance overwrite. If the fix
            // regresses (inheritance lines reintroduced), this assertion fails
            // because "atmo" would replace "exo".
            Assert.Equal("exo", provisional.SegmentPhase);
            Assert.Equal("Kerbin", provisional.SegmentBodyName);
        }
    }
}
