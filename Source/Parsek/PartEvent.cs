namespace Parsek
{
    public enum PartEventType
    {
        Decoupled,            // 0
        Destroyed,            // 1
        ParachuteDeployed,    // 2
        ParachuteCut,         // 3
        ShroudJettisoned,     // 4
        EngineIgnited,        // 5
        EngineShutdown,       // 6
        EngineThrottle        // 7
    }

    public struct PartEvent
    {
        public double ut;
        public uint partPersistentId;
        public PartEventType eventType;
        public string partName;
        public float value;       // throttle 0-1 for engine events; 0 for others
        public int moduleIndex;   // index of ModuleEngines on part (0 for single-engine parts)
    }
}
