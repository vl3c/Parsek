namespace Parsek
{
    public enum SegmentEventType
    {
        Split        = 0,
        Merge        = 1,
        Destroyed    = 2,
        Recovered    = 3,
        Landed       = 4,
        Launched     = 5,
        SOIChange    = 6,
        Docked       = 7
    }

    public struct SegmentEvent
    {
        public double ut;
        public SegmentEventType type;
        public string details;

        public override string ToString()
        {
            return $"UT={ut:F2} type={type}" +
                   (string.IsNullOrEmpty(details) ? "" : $" details='{details}'");
        }
    }
}
