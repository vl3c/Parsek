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
        ParachuteDestroyed,   // 8 - deployed chute destroyed (aero forces)
        DeployableExtended,   // 9 - solar panel / antenna / radiator fully deployed
        DeployableRetracted,  // 10 - solar panel / antenna / radiator fully retracted
        LightOn,              // 11 - light turned on
        LightOff,             // 12 - light turned off
        GearDeployed,         // 13 - landing gear / leg fully deployed
        GearRetracted,        // 14 - landing gear / leg fully retracted
        CargoBayOpened,       // 15 - cargo bay / service bay doors opened
        CargoBayClosed,       // 16 - cargo bay / service bay doors closed
        FairingJettisoned,    // 17 - procedural fairing deployed/jettisoned
        RCSActivated,         // 18 - RCS module started firing (value = normalized power 0-1)
        RCSStopped,           // 19 - RCS module stopped firing
        RCSThrottle,          // 20 - RCS aggregate power changed while firing
        Docked,               // 21 - docking port coupled (chain segment boundary)
        Undocked,             // 22 - docking port undocked (chain segment boundary)
        LightBlinkEnabled,    // 23 - light blink mode enabled (value = blinkRate)
        LightBlinkDisabled,   // 24 - light blink mode disabled
        LightBlinkRate,       // 25 - light blink rate changed (value = blinkRate)
        InventoryPartPlaced,  // 26 - inventory deployable placed into the world
        InventoryPartRemoved, // 27 - inventory deployable removed from the world
        RoboticMotionStarted, // 28 - robotic module started moving (value = sampled position)
        RoboticPositionSample, // 29 - robotic module motion sample (value = sampled position)
        RoboticMotionStopped, // 30 - robotic module stopped moving (value = sampled position)
        ThermalAnimationHot,  // 31 - ModuleAnimateHeat entered hot visual state
        ThermalAnimationCold, // 32 - ModuleAnimateHeat returned to cold visual state
        ParachuteSemiDeployed, // 33 - parachute entered semi-deployed (streamer) state
        ThermalAnimationMedium = 34 // ModuleAnimateHeat entered medium visual state
    }

    internal enum HeatLevel { Cold, Medium, Hot }

    public struct PartEvent
    {
        public double ut;
        public uint partPersistentId;
        public PartEventType eventType;
        public string partName;
        public float value;       // throttle 0-1 for engine events; 0 for others
        public int moduleIndex;   // index of ModuleEngines on part (0 for single-engine parts)

        public override string ToString()
        {
            return $"UT={ut:F2} event={eventType} part='{partName ?? "?"}' pid={partPersistentId} " +
                   $"midx={moduleIndex} value={value:F3}";
        }
    }
}
