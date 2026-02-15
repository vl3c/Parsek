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
        EngineThrottle,       // 7
        ParachuteDestroyed,   // 8 — deployed chute destroyed (aero forces)
        DeployableExtended,   // 9 — solar panel / antenna / radiator fully deployed
        DeployableRetracted,  // 10 — solar panel / antenna / radiator fully retracted
        LightOn,              // 11 — light turned on
        LightOff              // 12 — light turned off
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
