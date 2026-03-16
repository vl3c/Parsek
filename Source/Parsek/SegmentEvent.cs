namespace Parsek
{
    /// <summary>
    /// Types of within-segment state changes (no DAG branching).
    /// These are events that affect ghost visuals or recording metadata
    /// but do not create new branches in the recording tree.
    /// </summary>
    public enum SegmentEventType
    {
        TrackBoundary   = 0,  // Environment/reference-frame change within segment
        CrewTransfer    = 1,  // Crew moved between parts (affects controller identity)
        NameChange      = 2,  // Vessel renamed
    }

    /// <summary>
    /// A within-segment state change event.
    /// Unlike PartEvents (which are per-part visual changes), SegmentEvents
    /// represent vessel-level state transitions that don't branch the DAG.
    /// </summary>
    public struct SegmentEvent
    {
        public double ut;
        public SegmentEventType eventType;
        public string data;  // event-type-specific payload (e.g., new vessel name, track index)

        public override string ToString()
        {
            return $"UT={ut:F2} type={eventType} data='{data ?? ""}'";
        }
    }
}
