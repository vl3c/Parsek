namespace Parsek
{
    /// <summary>
    /// Describes a controller part on a vessel segment.
    /// Stored at segment start to identify what gives the vessel command authority.
    /// </summary>
    public struct ControllerInfo
    {
        public string type;              // "CrewedPod", "ExternalSeat", "ProbeCore", "KerbalEVA"
        public string partName;          // Part cfg name (e.g., "mk1pod.v2")
        public uint partPersistentId;    // Part's persistentId

        public override string ToString()
        {
            return $"Controller type={type} part={partName} pid={partPersistentId}";
        }
    }
}
