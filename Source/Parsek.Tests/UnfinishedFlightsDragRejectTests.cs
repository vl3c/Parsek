using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 5 of Rewind-to-Staging (design §7.25). Guards
    /// <see cref="GroupPickerUI.CanAddToUserGroup"/>, the pure-static gate
    /// that every add-to-group path in <c>ApplyGroupPopupChanges</c> now
    /// consults. The predicate is exercised here without a live KSP popup;
    /// the wiring of the gate into the apply path is covered by the
    /// in-game rendering test.
    /// </summary>
    [Collection("Sequential")]
    public class UnfinishedFlightsDragRejectTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;
        private readonly bool priorVerbose;

        public UnfinishedFlightsDragRejectTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            priorVerbose = ParsekLog.IsVerboseEnabled;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // --- Helpers ----------------------------------------------------------

        private static Recording MakeUnfinishedFlight(string id, string bpId, string treeId)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Destroyed,
                ParentBranchPointId = bpId,
                TreeId = treeId
            };
        }

        private static Recording MakeRegularRecording(string id)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Landed
            };
        }

        private static void InstallUnfinishedFlightScenario(Recording rec, string bpId)
        {
            // IsUnfinishedFlight consults ParsekScenario.RewindPoints (not
            // tree.BranchPoints) so the minimal setup is to add the recording
            // via the test helper and supply the matching RewindPoint on the
            // scenario.
            RecordingStore.AddRecordingWithTreeForTesting(rec, rec.TreeId);

            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>
                {
                    new RewindPoint
                    {
                        RewindPointId = "rp_1",
                        BranchPointId = bpId,
                        UT = 0.0,
                        SessionProvisional = false,
                        ChildSlots = new List<ChildSlot>
                        {
                            new ChildSlot
                            {
                                SlotIndex = 0,
                                OriginChildRecordingId = rec.RecordingId,
                                Controllable = true
                            }
                        }
                    }
                }
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();
        }

        // =====================================================================
        // Predicate cases
        // =====================================================================

        [Fact]
        public void CanAddToUserGroup_UnfinishedFlight_UserGroup_False()
        {
            // Regression: the core §7.25 rule. An Unfinished Flight recording
            // dropped onto a regular user-defined group must reject.
            var rec = MakeUnfinishedFlight("rec_A", "bp_1", "tree_1");
            InstallUnfinishedFlightScenario(rec, "bp_1");

            // Precondition: the scenario setup must make rec actually
            // classify as an Unfinished Flight, otherwise this test would
            // pass by accident.
            Assert.True(EffectiveState.IsUnfinishedFlight(rec),
                "scenario setup broken: rec did not classify as unfinished");

            bool allowed = GroupPickerUI.CanAddToUserGroup(rec, "MyUserGroup");
            Assert.False(allowed);
        }

        [Fact]
        public void CanAddToUserGroup_RegularRecording_UserGroup_True()
        {
            // Regression: regular recordings must continue to accept
            // user-group assignment. The gate is narrow — only Unfinished
            // Flights trip it.
            var rec = MakeRegularRecording("rec_B");
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            bool allowed = GroupPickerUI.CanAddToUserGroup(rec, "MyUserGroup");
            Assert.True(allowed);
        }

        [Fact]
        public void CanAddToUserGroup_AnyRecording_SystemGroupTarget_False()
        {
            // Regression: system groups (currently only "Unfinished Flights")
            // are never valid drop targets regardless of the recording's
            // classification — the virtual group has no stored membership to
            // add to.
            var rec = MakeRegularRecording("rec_C");
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            bool allowed = GroupPickerUI.CanAddToUserGroup(
                rec, UnfinishedFlightsGroup.GroupName);
            Assert.False(allowed);
        }

        [Fact]
        public void CanAddToUserGroup_NullRecording_True()
        {
            // Regression: defensive. A null Recording must not NRE in the
            // gate path; the predicate returns true so the caller's own
            // null-guard stays in charge of downstream behaviour.
            bool allowed = GroupPickerUI.CanAddToUserGroup(null, "MyUserGroup");
            Assert.True(allowed);
        }

        [Fact]
        public void CanAddToUserGroup_EmptyGroupName_True()
        {
            // Regression: defensive. An empty / null target group should
            // short-circuit to true (nothing to gate) rather than throwing.
            var rec = MakeRegularRecording("rec_D");
            Assert.True(GroupPickerUI.CanAddToUserGroup(rec, null));
            Assert.True(GroupPickerUI.CanAddToUserGroup(rec, ""));
        }

        [Fact]
        public void LogAndToastRejectAdd_EmitsVerboseLine()
        {
            // Regression: the production reject path MUST emit an
            // [UnfinishedFlights] Verbose line per design §10.5 so
            // the gate's behaviour is auditable post-hoc.
            logLines.Clear();
            GroupPickerUI.LogAndToastRejectAdd("rec_A", "MyUserGroup");

            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("Drag reject")
                && l.Contains("rec_A")
                && l.Contains("MyUserGroup"));
        }

        [Fact]
        public void LogAndToastRejectAdd_NullRecId_UsesPlaceholder()
        {
            // Regression: a null recordingId must not produce a "rec=" with
            // empty content; the placeholder keeps the log line parseable.
            logLines.Clear();
            GroupPickerUI.LogAndToastRejectAdd(null, "MyUserGroup");

            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("Drag reject")
                && l.Contains("<no-id>"));
        }
    }
}
