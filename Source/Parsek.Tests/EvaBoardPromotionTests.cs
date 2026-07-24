using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Cells for <see cref="ParsekFlight.DecideEvaBoardPromotion"/> - the board-time rebind
    /// that makes the Board branch point independent of EVA cadence.
    ///
    /// Background: an EVA branch parks the kerbal's recording in the tree BackgroundMap and
    /// leaves promotion to the post-switch first-modification watcher. A kerbal who exits and
    /// re-boards without moving never trips a trigger, so at the board there is no live
    /// recorder, the boarding detection in OnVesselSwitchComplete does not fire and
    /// HandleTreeBoardMerge never runs. This decision restores the promotion at the board.
    /// </summary>
    public class EvaBoardPromotionTests
    {
        private const uint EvaPid = 3726578224u;
        private const uint PodPid = 3620499050u;
        private const string EvaRecId = "eva-recording";

        private static ParsekFlight.EvaBoardPromotionDecision Decide(
            bool hasActiveTree = true,
            bool restoringActiveTree = false,
            bool sourceIsEva = true,
            bool sourceIsGhostVessel = false,
            uint sourceVesselPid = EvaPid,
            bool hasLiveRecorder = false,
            uint liveRecorderVesselPid = 0u,
            uint activeVesselPid = EvaPid,
            bool sourceTrackedInBackground = true,
            string trackedRecordingId = EvaRecId,
            // The live default: BOTH OnVesselSwitchComplete backgrounding branches clear
            // ActiveRecordingId when they park the pre-EVA recording, so at the board the
            // tree names no active recording.
            string treeActiveRecordingId = null)
        {
            return ParsekFlight.DecideEvaBoardPromotion(
                hasActiveTree,
                restoringActiveTree,
                sourceIsEva,
                sourceIsGhostVessel,
                sourceVesselPid,
                hasLiveRecorder,
                liveRecorderVesselPid,
                activeVesselPid,
                sourceTrackedInBackground,
                trackedRecordingId,
                treeActiveRecordingId);
        }

        [Fact]
        public void RapidBoardWithBackgroundOnlyEvaRecording_Promotes()
        {
            // The EVA-3 live case: exit -> board inside ~0.15 s, watcher still armed,
            // recorder null, EVA recording only in BackgroundMap.
            Assert.Equal(
                ParsekFlight.EvaBoardPromotionDecision.Promote,
                Decide());
        }

        [Fact]
        public void LiveRecorderAlreadyOnEvaKerbal_NotNeeded()
        {
            // The EVA-1 green path: the first-modification watcher already promoted the
            // EVA recording, so the board merge machinery is armed - do not disturb it.
            Assert.Equal(
                ParsekFlight.EvaBoardPromotionDecision.NotNeeded,
                Decide(hasLiveRecorder: true, liveRecorderVesselPid: EvaPid));
        }

        [Fact]
        public void LiveRecorderOnAnotherVessel_SkipsRecorderBusy()
        {
            // Stealing an unrelated live recorder would break that recording's continuity.
            Assert.Equal(
                ParsekFlight.EvaBoardPromotionDecision.SkipRecorderBusy,
                Decide(hasLiveRecorder: true, liveRecorderVesselPid: PodPid));
        }

        [Fact]
        public void NoActiveTree_SkipsNoTree()
        {
            Assert.Equal(
                ParsekFlight.EvaBoardPromotionDecision.SkipNoTree,
                Decide(hasActiveTree: false));
        }

        [Fact]
        public void RestoreCoroutineOwnsState_SkipsRestoring()
        {
            Assert.Equal(
                ParsekFlight.EvaBoardPromotionDecision.SkipRestoring,
                Decide(restoringActiveTree: true));
        }

        [Fact]
        public void SourceVesselNotEva_SkipsNotEva()
        {
            // Crew transfers and other onCrewBoardVessel sources are not EVA branches.
            Assert.Equal(
                ParsekFlight.EvaBoardPromotionDecision.SkipNotEva,
                Decide(sourceIsEva: false));
        }

        [Fact]
        public void SourceVesselPidZero_SkipsNotEva()
        {
            Assert.Equal(
                ParsekFlight.EvaBoardPromotionDecision.SkipNotEva,
                Decide(sourceVesselPid: 0u, activeVesselPid: 0u));
        }

        [Fact]
        public void GhostMapVessel_SkipsGhostVessel()
        {
            Assert.Equal(
                ParsekFlight.EvaBoardPromotionDecision.SkipGhostVessel,
                Decide(sourceIsGhostVessel: true));
        }

        [Fact]
        public void EvaKerbalNoLongerActiveVessel_SkipsEvaNotActiveVessel()
        {
            // FlightRecorder.StartRecording binds to FlightGlobals.ActiveVessel, so promoting
            // after KSP already switched focus would attach the EVA recording to the pod.
            Assert.Equal(
                ParsekFlight.EvaBoardPromotionDecision.SkipEvaNotActiveVessel,
                Decide(activeVesselPid: PodPid));
        }

        [Fact]
        public void EvaVesselNotTrackedInTree_SkipsNoTrackedRecording()
        {
            Assert.Equal(
                ParsekFlight.EvaBoardPromotionDecision.SkipNoTrackedRecording,
                Decide(sourceTrackedInBackground: false));
        }

        [Fact]
        public void GhostCheckPrecedesRecorderChecks()
        {
            // A ghost map vessel must never promote even when every other conjunct lines up.
            Assert.Equal(
                ParsekFlight.EvaBoardPromotionDecision.SkipGhostVessel,
                Decide(sourceIsGhostVessel: true, hasLiveRecorder: true, liveRecorderVesselPid: EvaPid));
        }

        [Fact]
        public void TreeActiveRecordingIsAnotherRecording_SkipsTreeActiveRecordingBusy()
        {
            // A merge child whose StartRecording failed (CreateMergeBranch step 10 nulls the
            // recorder but leaves ActiveRecordingId set) leaves the tree naming a recording
            // that no live recorder owns. PromoteRecordingFromBackground would overwrite that
            // id without parking it back into BackgroundMap, orphaning it - so skip.
            Assert.Equal(
                ParsekFlight.EvaBoardPromotionDecision.SkipTreeActiveRecordingBusy,
                Decide(treeActiveRecordingId: "orphaned-merge-child"));
        }

        [Fact]
        public void TreeActiveRecordingIsTheEvaRecording_StillPromotes()
        {
            // Already pointing at the recording we are about to promote: nothing to orphan.
            Assert.Equal(
                ParsekFlight.EvaBoardPromotionDecision.Promote,
                Decide(treeActiveRecordingId: EvaRecId));
        }

        [Fact]
        public void EmptyTreeActiveRecordingId_TreatedAsNoneAndPromotes()
        {
            // Empty string is "no active recording", not "some other recording".
            Assert.Equal(
                ParsekFlight.EvaBoardPromotionDecision.Promote,
                Decide(treeActiveRecordingId: ""));
        }

        [Fact]
        public void RecorderBusyCheckPrecedesTreeActiveRecordingCheck()
        {
            // A live recorder on another vessel is the stronger signal and keeps its own
            // decision, so the diagnostics still name the real reason.
            Assert.Equal(
                ParsekFlight.EvaBoardPromotionDecision.SkipRecorderBusy,
                Decide(hasLiveRecorder: true, liveRecorderVesselPid: PodPid,
                       treeActiveRecordingId: "orphaned-merge-child"));
        }
    }
}
