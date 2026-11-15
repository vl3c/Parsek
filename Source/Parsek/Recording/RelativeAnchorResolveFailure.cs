namespace Parsek
{
    internal enum RelativeAnchorResolveOutcome
    {
        None = 0,
        OutOfSectionRange,
        NoSectionAtUT,
        AnchorCycleDetected,
        AnchorOutOfScope,
        TrackSectionsMissing,
        AnchorRecordingNotFound,
        PoseNonFinite,
        PreconditionFailed,
        Other
    }

    internal readonly struct RelativeAnchorResolveFailure
    {
        public readonly RelativeAnchorResolveOutcome Outcome;
        public readonly string Reason;
        public readonly string FailureRecordingId;
        public readonly string AnchorRecordingId;
        public readonly double RequestedUT;
        public readonly int SectionIndex;
        public readonly double RangeStartUT;
        public readonly double RangeEndUT;

        public RelativeAnchorResolveFailure(
            RelativeAnchorResolveOutcome outcome,
            string reason,
            string failureRecordingId,
            string anchorRecordingId,
            double requestedUT,
            int sectionIndex,
            double rangeStartUT,
            double rangeEndUT)
        {
            Outcome = outcome;
            Reason = reason;
            FailureRecordingId = failureRecordingId;
            AnchorRecordingId = anchorRecordingId;
            RequestedUT = requestedUT;
            SectionIndex = sectionIndex;
            RangeStartUT = rangeStartUT;
            RangeEndUT = rangeEndUT;
        }

        public bool HasFailure => Outcome != RelativeAnchorResolveOutcome.None;

        public static RelativeAnchorResolveFailure Create(
            RelativeAnchorResolveOutcome outcome,
            string reason,
            string failureRecordingId,
            string anchorRecordingId,
            double requestedUT,
            int sectionIndex = -1,
            double rangeStartUT = double.NaN,
            double rangeEndUT = double.NaN)
        {
            return new RelativeAnchorResolveFailure(
                outcome,
                reason,
                failureRecordingId,
                anchorRecordingId,
                requestedUT,
                sectionIndex,
                rangeStartUT,
                rangeEndUT);
        }

        public static string ReasonOrFallback(
            RelativeAnchorResolveFailure failure,
            string fallback)
        {
            return !string.IsNullOrEmpty(failure.Reason)
                ? failure.Reason
                : fallback;
        }
    }
}
