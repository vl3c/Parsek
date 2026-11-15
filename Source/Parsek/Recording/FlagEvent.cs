namespace Parsek
{
    public struct FlagEvent
    {
        public double ut;
        public string flagSiteName;   // vessel name the player gave the flag
        public string placedBy;       // kerbal who planted it
        public string plaqueText;     // plaque message
        public string flagURL;        // GameDatabase texture path (e.g. "Squad/Flags/default")
        public double latitude;
        public double longitude;
        public double altitude;
        public float rotX, rotY, rotZ, rotW; // surface-relative rotation
        public string bodyName;       // body the flag is on

        public override string ToString()
        {
            return $"UT={ut:F2} flag='{flagSiteName ?? "?"}' by='{placedBy ?? "?"}' " +
                   $"at ({latitude:F4},{longitude:F4},{altitude:F1}) on {bodyName ?? "?"}";
        }
    }
}
