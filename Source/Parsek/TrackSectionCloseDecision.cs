namespace Parsek
{
    /// <summary>
    /// What <see cref="FlightRecorder.CloseCurrentTrackSection"/> /
    /// <c>BackgroundRecorder.CloseBackgroundTrackSection</c> do with the section
    /// they are closing.
    /// </summary>
    internal enum TrackSectionCloseDisposition
    {
        /// <summary>Append the section to the recording's section list.</summary>
        Persist,

        /// <summary>
        /// Drop it: the section carries no authored surface at all (no frames, no
        /// body-fixed frames, no checkpoints), so nothing downstream can render or
        /// read it, and its mere presence disables the codec's flat-trajectory
        /// defences.
        /// </summary>
        DiscardPayloadFree,

        /// <summary>
        /// Drop it: a Relative section closed within one physics frame of opening
        /// while holding only the environment-boundary seed point.
        /// </summary>
        DiscardSeedOnlyRelativeTransient,
    }

    /// <summary>
    /// The pure close-a-section decision shared by the two recorders. Extracted so the
    /// discard rules are unit-testable without a live recorder.
    /// </summary>
    internal static class TrackSectionCloseClassifier
    {
        /// <summary>
        /// Upper bound on a seed-only Relative transient's duration: the tightest
        /// production min sample interval (Full-tier ProximitySamplingCadence), so a
        /// section that closes faster than this can only contain the seed.
        /// </summary>
        internal const double SeedOnlyRelativeTransientMaxDurationSeconds = 0.05;

        /// <summary>
        /// Upper bound on a payload-free section's span for the discard to apply, carried
        /// over unchanged from the zero-frame discard <c>FlightRecorder</c> has always
        /// had. The shells the producers actually emit are transition artifacts and are
        /// sub-second by construction: of the 18 payload-free sections in the whole
        /// committed fixture corpus (1914 sections, 8 saves) 16 span 0.02 s or 0.04 s,
        /// including the undock shell this bound exists to catch; the two outliers are
        /// 5.0 s and 5.5 s, both in <c>rover-relay-c-recorded</c>.
        ///
        /// <para>The bound is a hygiene threshold, not the load-bearing fix: the codec
        /// predicates now SKIP a payload-free section whatever its span
        /// (<c>TrajectoryTextSidecarCodec.IsPayloadFreeTrackSection</c>), so a longer
        /// shell that still reaches disk can no longer disable the flat-trajectory
        /// defences, and the analyzer's INV11-EMPTY-SECTION reports it.</para>
        /// </summary>
        internal const double PayloadFreeMaxDurationSeconds = 1.0;

        /// <summary>
        /// Classifies a section that is being closed.
        ///
        /// <para>Rule order, and why:</para>
        /// <list type="number">
        /// <item>An <c>isBoundarySeam</c> section always persists. It is a producer-emitted
        /// recorder bookkeeping artifact the optimizer's split-suppression contract reads
        /// (see <see cref="TrackSection.isBoundarySeam"/> and
        /// docs/dev/done/plans/optimizer-persistence-split.md).</item>
        /// <item>A PAYLOAD-FREE section that spanned less than
        /// <see cref="PayloadFreeMaxDurationSeconds"/> is discarded, regardless of
        /// reference frame. "Payload-free" means all three authored surfaces are empty:
        /// <c>frames</c>, <c>bodyFixedFrames</c> and <c>checkpoints</c>.
        /// Such a section is never a legitimate finalization (the single-frame Absolute
        /// finalizations <c>FinalizeAllForCommit</c> emits carry exactly one frame and are
        /// preserved here), and persisting one is actively harmful: it makes
        /// <c>TrajectoryTextSidecarCodec.HasCompleteTrackSectionPayloadForFlatSync</c> and
        /// <c>TryBuildBodyFixedPrimaryFlatPointsForRelativeSections</c> answer "incomplete"
        /// for the WHOLE recording, which is how an undock background child persisted a
        /// Relative section's anchor-local metre offsets as flat lat/lon points and read
        /// maxDist ~735 km for a rover that never left the pad area (todo
        /// UNDOCK-BG-CHILD-WRITES-RELATIVE-METRES-AS-FLAT-LAT-LON).</item>
        /// <item>A seed-only Relative transient (at most one frame, no checkpoints, closed
        /// within <see cref="SeedOnlyRelativeTransientMaxDurationSeconds"/> of opening) is
        /// discarded: persisting it produces a synthetic boundaryDiscontinuityMeters against
        /// the next Absolute section and a matching anchor-correction offset at playback.
        /// Restricted to Relative to preserve the legitimate single-frame Absolute
        /// finalizations.</item>
        /// </list>
        /// </summary>
        internal static TrackSectionCloseDisposition Classify(
            int frameCount,
            int bodyFixedFrameCount,
            int checkpointCount,
            double sectionDurationSeconds,
            ReferenceFrame referenceFrame,
            bool isBoundarySeam)
        {
            if (isBoundarySeam)
                return TrackSectionCloseDisposition.Persist;

            if (frameCount <= 0
                && bodyFixedFrameCount <= 0
                && checkpointCount <= 0
                && sectionDurationSeconds < PayloadFreeMaxDurationSeconds)
            {
                return TrackSectionCloseDisposition.DiscardPayloadFree;
            }

            if (frameCount <= 1
                && checkpointCount == 0
                && sectionDurationSeconds < SeedOnlyRelativeTransientMaxDurationSeconds
                && referenceFrame == ReferenceFrame.Relative)
            {
                return TrackSectionCloseDisposition.DiscardSeedOnlyRelativeTransient;
            }

            return TrackSectionCloseDisposition.Persist;
        }
    }
}
