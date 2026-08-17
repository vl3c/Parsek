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
        // Explicit value for serialization stability. Values 0-33 are contiguous;
        // new values must be explicitly numbered.
        ThermalAnimationMedium = 34, // ModuleAnimateHeat entered medium visual state
        // A CUT chute was repacked back to STOWED by an EVA kerbal (stock
        // ModuleParachute.Repack: cap re-shown, canopy hidden). Distinct from
        // ParachuteCut, which hides the cap permanently — before this member
        // existed the recorder collapsed STOWED/ACTIVE/CUT into one state and a
        // repack emitted ParachuteCut, so a repacked chute rendered as an empty
        // can for the rest of playback.
        ParachuteRepacked = 35,

        // ------------------------------------------------------------------
        // P8 (part-event fidelity). Members 36-44, explicit and append-only.
        //
        // FORWARD-COMPAT NOTE, verified rather than assumed: neither reader gates
        // these out destructively. TrajectorySidecarBinary.ReadPartEventList raw-casts
        // `(PartEventType)reader.ReadInt32()`, so an OLDER build reading a newer .prec
        // materialises an undefined member and carries it; the legacy text codec gates on
        // Enum.IsDefined and skips it. Every reachable consumer of an unrecognised member
        // degrades conservatively (ApplyPartEvents: unhandled switch arm;
        // IsPermanentVisualStateEvent: false; IsGhostingTrigger: true + a Verbose line),
        // which is why members 34/35 shipped without a schema-generation bump and why
        // these do too. See RecordedSignalFixTests
        // .BinarySidecar_RawCastsAnUndefinedPartEventType_AndEveryConsumerDegradesGracefully.
        // ------------------------------------------------------------------

        /// A ModuleDeployablePart entered BROKEN. Deliberately its OWN member rather
        /// than a value-flagged DeployableRetracted: BROKEN renders differently (the
        /// break subtree is HIDDEN, not posed stowed), and a value flag would poison
        /// every boolean consumer that reads the deployable family — the split-seed
        /// reducer (family 3), the snapshot baseline resolver, the S2 interpolator and
        /// the sun-tracking gate all ask "extended or not?" and none of them can answer
        /// "gone". Seeded VERBATIM through the reducer, exactly like the parachute trio.
        /// REVERSIBLE: stock eventRepairExternal returns the panel to RETRACTED, so a
        /// later DeployableRetracted un-hides the subtree.
        DeployableBroken = 36,

        /// First BaseConverter on the part became active (ModuleResourceConverter or
        /// ModuleResourceHarvester — cachedConverters is List&lt;BaseConverter&gt;). Drives
        /// the ModuleAnimationGroup running-loop animation on playback (drill spinning,
        /// ISRU churning).
        ConverterActivated = 37,
        /// Last BaseConverter on the part went inactive: the running loop stops.
        ConverterDeactivated = 38,

        /// KerbalEVA jetpack extended (KerbalEVA.JetpackDeployed true).
        EvaJetpackDeployed = 39,
        /// KerbalEVA jetpack stowed.
        EvaJetpackStowed = 40,
        /// KerbalEVA jetpack began sustained thrust (value = 1.0). DEBOUNCED on the same
        /// frame threshold as RCS — KerbalEVA.JetpackIsThrusting is rewritten every
        /// FixedUpdate from a fuel-flow comparison and flickers on a single tap.
        EvaJetpackThrustStarted = 41,
        /// KerbalEVA jetpack thrust ended.
        EvaJetpackThrustStopped = 42,

        /// KerbalEVA went ragdoll. EVENTS ONLY — the ragdoll POSE is deliberately not
        /// recorded or replayed (it is a physics outcome with no clip to sample, so a
        /// replayed pose would be invention). The events gate the thrust plume and mark
        /// the timeline.
        EvaRagdollStarted = 43,
        /// KerbalEVA recovered from ragdoll.
        EvaRagdollEnded = 44
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
