namespace Parsek
{
    public enum PartEventType
    {
        Decoupled,
        Destroyed,
        ParachuteDeployed,
        ParachuteCut
    }

    public struct PartEvent
    {
        public double ut;
        public uint partPersistentId;
        public PartEventType eventType;
        public string partName;
    }
}
