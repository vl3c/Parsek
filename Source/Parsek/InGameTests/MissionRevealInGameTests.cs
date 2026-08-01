using System;
using System.Collections;
using System.Globalization;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Runtime gate for the Timeline GoTo cross-link's scroll
    /// (design `docs/dev/design-ui-basic-advanced.md` section 4.1a).
    ///
    /// <para>What headless xUnit structurally cannot reach. `RevealMissionForRecording` is
    /// unit-tested up to the point it arms a target, but everything after that is IMGUI: the
    /// capture reads real layout rects from a real `GUILayoutUtility` pass, and the apply
    /// writes `scrollPos` before a real `BeginScrollView`. No headless test can produce a
    /// solved layout tree, so that half shipped on manual playtest alone.</para>
    ///
    /// <para>The specific defect this exists for. A Repaint event is NOT on its own proof
    /// that rects are solved - Unity solves them at the end of a Layout pass and later passes
    /// reuse that window's cache. The most common GoTo path (Missions window CLOSED, the click
    /// opens it mid-frame) could therefore paint a Repaint against a cache that was never
    /// solved, read zeros, consume the target, land the list at offset 0, and log it as a
    /// success. It is silent by construction: no exception, no warning, and a log line that
    /// reads like it worked. The guard is that the capture requires the same frame to have run
    /// the list's own Layout pass; this test is what proves that guard is real in a live game
    /// rather than reasoned about on paper.</para>
    ///
    /// <para>SELF-SEEDING, by necessity. The H22 host runs with `injectedRecordings = "none"`,
    /// so there are no trees and no missions to reveal - a test that merely skipped in that
    /// case would verify nothing on the very run that is supposed to verify it. So it installs
    /// two synthetic committed trees through the non-flushing
    /// <see cref="RecordingStore.AddCommittedTreeInternal"/> (no sidecar writes, no merge, no
    /// grouping), lets the Missions tab seed their default missions the way it does for real
    /// trees, and removes both in a finally. Nothing is written to disk; the batch's own
    /// campaign isolation is a backstop, not the mechanism.</para>
    ///
    /// <para>The assertion is sort-independent: it reveals BOTH seeded missions and requires
    /// the two captured offsets to DIFFER. Two different rows cannot occupy the same offset,
    /// and the defect above collapses every offset to zero - so "they differ" fails under the
    /// bug and holds under any row ordering or window size. Asserting a specific pixel value,
    /// or even "greater than zero" for one mission, would bet on where the list happens to
    /// sort and on the window being tall enough.</para>
    /// </summary>
    public class MissionRevealInGameTests
    {
        // Frames to let the two-frame handshake run: the capture needs a pass whose own Layout
        // ran, the apply lands on a later one. Generous because a window opened this frame
        // deliberately defers its capture by one frame.
        private const int MaxFramesToWaitForCapture = 12;

        private const string SeedTreeIdPrefix = "parsek-ingame-missionreveal-";

        [InGameTest(Category = "UiComplexityMode", Scene = GameScenes.FLIGHT,
            Description = "The Timeline GoTo cross-link's mission reveal measures a real "
                + "layout pass: two different missions capture two different scroll offsets "
                + "(design 4.1a; guards the unsolved-layout-cache zero)")]
        public IEnumerator MissionRevealScrollsToTheTargetMission()
        {
            ParsekUI ui = ParsekUI.ActiveInstance;
            if (ui == null)
            {
                InGameAssert.Skip("No live ParsekUI in this scene");
                yield break;
            }

            RecordingsTableUI table = ui.GetRecordingsTableUI();
            MissionsWindowUI missions = ui.GetMissionsUI();
            if (table == null || missions == null)
            {
                InGameAssert.Skip("Recordings / Missions window not constructed in this scene");
                yield break;
            }

            bool originalOpen = table.IsOpen;
            int originalTab = table.SelectedTabForTesting;
            bool originalHideArchived = MissionStore.HideArchived;
            string treeA = SeedTreeIdPrefix + "a";
            string treeB = SeedTreeIdPrefix + "b";

            try
            {
                // The Archive filter would drop a seeded block out of the list entirely.
                MissionStore.HideArchived = false;

                SeedTree(treeA, "Parsek Reveal Probe A");
                SeedTree(treeB, "Parsek Reveal Probe B");

                // Open on the Missions tab and let it draw. This first draw is what seeds the
                // two default missions (EnsureDefaultsForTrees runs inside it) and establishes
                // the first-header reference the offsets are measured against.
                table.IsOpen = true;
                yield return null;
                yield return null;

                if (MissionStore.FindOriginalMission(treeA) == null
                    || MissionStore.FindOriginalMission(treeB) == null)
                {
                    InGameAssert.Skip(
                        "The Missions tab did not seed default missions for the probe trees "
                        + "(is the window drawing in this scene?)");
                    yield break;
                }

                float offsetA = float.NaN;
                yield return CaptureRevealOffset(table, missions, RootRecordingIdFor(treeA),
                    result => offsetA = result);

                float offsetB = float.NaN;
                yield return CaptureRevealOffset(table, missions, RootRecordingIdFor(treeB),
                    result => offsetB = result);

                InGameAssert.IsFalse(float.IsNaN(offsetA),
                    "revealing the first probe mission captured no offset at all - the draw "
                    + "never consumed the armed target");
                InGameAssert.IsFalse(float.IsNaN(offsetB),
                    "revealing the second probe mission captured no offset at all - the draw "
                    + "never consumed the armed target");

                // The load-bearing assertion. Under the unsolved-layout-cache defect both
                // captures read zero-height rects and collapse to the same 0.
                InGameAssert.IsTrue(Math.Abs(offsetA - offsetB) > 0.5f,
                    "two different missions captured the same scroll offset "
                    + $"(A={Px(offsetA)} B={Px(offsetB)}) - the capture measured an unsolved "
                    + "layout pass rather than real rects");

                ParsekLog.Info("TestRunner",
                    $"MissionReveal: captured distinct offsets A={Px(offsetA)} B={Px(offsetB)}");
            }
            finally
            {
                RecordingStore.RemoveCommittedTreeById(treeA, "MissionRevealInGameTests");
                RecordingStore.RemoveCommittedTreeById(treeB, "MissionRevealInGameTests");
                PruneSeededMissions(treeA, treeB);
                MissionStore.HideArchived = originalHideArchived;
                table.SelectedTabForTesting = originalTab;
                table.IsOpen = originalOpen;
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        // Arms one reveal and waits for a draw pass to consume it, handing back the offset the
        // capture measured. Returns NaN if no pass ever consumed the target.
        private static IEnumerator CaptureRevealOffset(
            RecordingsTableUI table, MissionsWindowUI missions, string recordingId,
            Action<float> onCaptured)
        {
            missions.LastRevealScrollOffsetForTesting = float.NaN;
            table.ShowMissionForRecording(recordingId);

            for (int frame = 0; frame < MaxFramesToWaitForCapture; frame++)
            {
                yield return null;
                if (!float.IsNaN(missions.LastRevealScrollOffsetForTesting)
                    && missions.PendingRevealMissionIdForTesting == null)
                {
                    onCaptured(missions.LastRevealScrollOffsetForTesting);
                    yield break;
                }
            }

            onCaptured(float.NaN);
        }

        // InvariantCulture: a comma-locale machine would otherwise write "12,5px" into the
        // log and the assertion message.
        private static string Px(float value)
        {
            return value.ToString("F1", CultureInfo.InvariantCulture) + "px";
        }

        private static string RootRecordingIdFor(string treeId)
        {
            return treeId + "-root";
        }

        // A minimal committed tree: one recording with two trajectory points, installed through
        // the non-flushing internal so nothing reaches disk.
        private static void SeedTree(string treeId, string vesselName)
        {
            var rec = new Recording
            {
                RecordingId = RootRecordingIdFor(treeId),
                TreeId = treeId,
                VesselName = vesselName,
                SegmentPhase = "atmo"
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0 });

            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = vesselName,
                RootRecordingId = rec.RecordingId
            };
            tree.Recordings[rec.RecordingId] = rec;

            RecordingStore.AddCommittedTreeInternal(tree);
            RecordingStore.AddCommittedInternal(rec);
        }

        // The trees are gone by now, so PruneOrphans drops exactly the missions that were
        // seeded for them and leaves every real mission alone.
        private static void PruneSeededMissions(string treeA, string treeB)
        {
            int removed = MissionStore.PruneOrphans(RecordingStore.CommittedTrees);
            ParsekLog.Info("TestRunner",
                $"MissionReveal: teardown pruned {removed} probe mission(s) "
                + $"(trees {treeA}, {treeB})");
        }
    }
}
