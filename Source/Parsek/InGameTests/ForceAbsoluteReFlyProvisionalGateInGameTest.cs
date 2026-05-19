using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Observational in-game test for the experimental
    /// <c>forceAbsoluteForReFlyProvisional</c> setting gate.
    /// Runs in FLIGHT during a live re-fly session and asserts that the
    /// <c>ReFlyAnchorSelection.IsActiveRecordingReFlyProvisional</c>
    /// predicate matches the recorder's authored reference frame on the
    /// current active recording's tail track section.
    ///
    /// <para>Preconditions: an active <see cref="ReFlySessionMarker"/> +
    /// at least one authored <c>TrackSection</c> in the provisional. The
    /// test auto-skips otherwise (running outside a re-fly is
    /// meaningless).</para>
    ///
    /// <para>This pins the production-side wiring: when the setting is
    /// on and the predicate fires, the recorder must have stayed in
    /// Absolute; when the setting is off, the recorder may have opened
    /// Relative as before. The predicate now fires uniformly for top-
    /// level and parent-anchored re-fly provisionals (the earlier
    /// DebrisParentRecordingId carve-out was removed).</para>
    /// </summary>
    public class ForceAbsoluteReFlyProvisionalGateInGameTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "force-absolute-refly setting gates Relative authoring on the active provisional")]
        public void ForceAbsoluteReFlyProvisionalGate()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            var marker = scenario.ActiveReFlySessionMarker;
            if (marker == null)
            {
                InGameAssert.Skip("No active re-fly session; start a rewind before running this test.");
                return;
            }

            var settings = ParsekSettings.Current;
            InGameAssert.IsNotNull(settings, "ParsekSettings.Current is null");

            var host = GameObject.FindObjectOfType<ParsekFlight>();
            if (host == null)
            {
                InGameAssert.Skip("No ParsekFlight in current scene; test is FLIGHT-only.");
                return;
            }
            var tree = host.ActiveTreeForDisplay;
            InGameAssert.IsNotNull(tree, "ParsekFlight active tree is null");

            string activeRecordingId = tree.ActiveRecordingId;
            InGameAssert.IsTrue(!string.IsNullOrEmpty(activeRecordingId),
                "Active recording id is empty");

            Recording activeRec = null;
            if (tree.Recordings != null)
                tree.Recordings.TryGetValue(activeRecordingId, out activeRec);
            InGameAssert.IsNotNull(activeRec, $"Active recording {activeRecordingId} not found in tree");

            string debrisParentRecordingId = activeRec.DebrisParentRecordingId;
            bool predicateFires = ReFlyAnchorSelection.IsActiveRecordingReFlyProvisional(
                marker, activeRecordingId);

            ParsekLog.Info("RewindTest",
                $"ForceAbsoluteReFlyProvisionalGate: setting={settings.forceAbsoluteForReFlyProvisional} " +
                $"activeRecId={activeRecordingId} debrisParentRecId={debrisParentRecordingId ?? "(none)"} " +
                $"predicateFires={predicateFires} markerActiveRecId={marker.ActiveReFlyRecordingId}");

            // Walk the recording's TrackSections to find the tail section's
            // reference frame. Skip if the provisional has no authored
            // sections yet (recorder may have just started).
            if (activeRec.TrackSections == null || activeRec.TrackSections.Count == 0)
            {
                InGameAssert.Skip("Active provisional has no authored TrackSections yet.");
                return;
            }

            var tail = activeRec.TrackSections[activeRec.TrackSections.Count - 1];
            ParsekLog.Info("RewindTest",
                $"ForceAbsoluteReFlyProvisionalGate: tail section frame={tail.referenceFrame} " +
                $"anchorRecordingId={tail.anchorRecordingId ?? "(none)"}");

            if (settings.forceAbsoluteForReFlyProvisional && predicateFires)
            {
                // Gate must have fired: tail section authored by the gated
                // recorder branch should be Absolute. The recorder may have
                // emitted Absolute sections preceded by older Relative ones
                // recorded before the setting was flipped on; we only
                // assert the tail because that's the section the current
                // recorder branch is authoring.
                //
                // The gate applies uniformly to parent-anchored and top-
                // level re-fly provisionals (the earlier
                // DebrisParentRecordingId carve-out was removed after the
                // 2026-05-19 Kerbal X Probe re-fly showed parent-anchored
                // provisionals fall into the same supersede-target anchor
                // anti-pattern as top-level ones).
                InGameAssert.AreEqual(ReferenceFrame.Absolute, tail.referenceFrame,
                    $"force-absolute-refly setting is ON and predicate fires, but tail " +
                    $"TrackSection is {tail.referenceFrame} (expected Absolute). " +
                    $"recordingId={activeRecordingId} debrisParentRecId={debrisParentRecordingId ?? "(none)"}");
            }

            ParsekLog.Info("RewindTest",
                "ForceAbsoluteReFlyProvisionalGate: assertions complete");
        }
    }
}
