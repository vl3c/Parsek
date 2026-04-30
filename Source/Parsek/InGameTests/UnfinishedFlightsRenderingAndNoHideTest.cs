using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Phase 5 of Rewind-to-Staging (design §5.11 / §7.25 / §7.30): verifies
    /// the virtual Unfinished Flights group's runtime predicates end-to-end
    /// inside a live KSP instance. The test runs from the Space Center scene
    /// so it can see the scenario-backed RewindPoints / committed recordings
    /// from the last save without needing a specific flight to be active.
    ///
    /// <para>
    /// The test is observational only — it does not mutate the scenario,
    /// does not write to the save, and skips gracefully when the current
    /// save has no Unfinished Flight to surface. It exists so that after
    /// Phase 5 ships, a player running Ctrl+Shift+T after landing in a
    /// known-crash scenario gets explicit feedback that the group is
    /// wired correctly rather than having to eyeball the recordings
    /// table.
    /// </para>
    /// </summary>
    public class UnfinishedFlightsRenderingAndNoHideTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.SPACECENTER,
            Description = "Unfinished Flights virtual group: members present, cannot hide, cannot accept drops")]
        public void UnfinishedFlightsRenderingAndNoHide()
        {
            // Skip when the current save has no unfinished-flight recording
            // to observe. Players who have never crashed an Immutable
            // recording under a RewindPoint will always hit this path.
            var members = UnfinishedFlightsGroup.ComputeMembers();
            if (members == null || members.Count == 0)
            {
                InGameAssert.Skip("No Unfinished Flight member in current save — " +
                    "stage-crash a controllable child in a multi-controllable split, save, and rerun.");
                return;
            }

            ParsekLog.Info("UnfinishedFlightsTest",
                $"UnfinishedFlightsRenderingAndNoHide: members={members.Count} " +
                $"first={members[0].RecordingId ?? "<no-id>"} vessel={members[0].VesselName ?? "<no-name>"}");

            // Membership precondition: every returned recording must actually
            // classify as unfinished per the centralized EffectiveState rule —
            // guards against the virtual group accidentally populating from a
            // broader predicate than design §5.11 defines.
            for (int i = 0; i < members.Count; i++)
            {
                var rec = members[i];
                InGameAssert.IsNotNull(rec, $"members[{i}] is null");
                InGameAssert.IsTrue(EffectiveState.IsUnfinishedFlight(rec),
                    $"members[{i}] rec={rec.RecordingId} did not classify as unfinished");
            }

            // Design §7.30: the virtual group must not be hide-eligible.
            InGameAssert.IsFalse(
                GroupHierarchyStore.CanHide(UnfinishedFlightsGroup.GroupName),
                $"CanHide(\"{UnfinishedFlightsGroup.GroupName}\") expected false");

            // Design §7.25: the virtual group must reject drop targets.
            InGameAssert.IsFalse(
                GroupHierarchyStore.IsDropTargetAllowed(UnfinishedFlightsGroup.GroupName),
                $"IsDropTargetAllowed(\"{UnfinishedFlightsGroup.GroupName}\") expected false");

            // Design §7.25 end-to-end: the GroupPickerUI predicate must reject
            // an Unfinished Flight recording for a regular user-group target.
            // Use a synthetic target name that is guaranteed not to be a
            // system group — we are not mutating anything; this is a pure
            // predicate call.
            var firstMember = members[0];
            InGameAssert.IsFalse(
                GroupPickerUI.CanAddToUserGroup(firstMember, "__UserGroupSimulatedTarget__"),
                "CanAddToUserGroup should reject an Unfinished Flight recording " +
                "for a user-group target");

            // Positive control: the same predicate must accept a regular
            // (non-unfinished) recording for the same target. Find one from
            // the committed list; skip this leg if no such recording exists.
            var committed = RecordingStore.CommittedRecordings;
            Recording regular = null;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec == null) continue;
                if (!EffectiveState.IsUnfinishedFlight(rec))
                {
                    regular = rec;
                    break;
                }
            }
            if (regular != null)
            {
                InGameAssert.IsTrue(
                    GroupPickerUI.CanAddToUserGroup(regular, "__UserGroupSimulatedTarget__"),
                    $"CanAddToUserGroup expected true for regular recording " +
                    $"rec={regular.RecordingId}");
            }
            else
            {
                ParsekLog.Info("UnfinishedFlightsTest",
                    "No non-unfinished recording available for positive-control branch; skipping that leg.");
            }

            ParsekLog.Info("UnfinishedFlightsTest",
                "UnfinishedFlightsRenderingAndNoHide: all predicates matched expectations");
        }
    }
}
