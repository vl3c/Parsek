namespace Parsek
{
    // The vocabulary of a mission profile (docs/dev/design-mission-profile.md sections 3.1,
    // 3.2, 4.4): what the phase analyzer (MissionPhaseAnalyzer) names. No player words live
    // here; ProfileLabels (Missions) turns a kind into the text a row shows. Every enum is
    // additive-only: nothing persists these numbers, but a later optimizer reader (P3) may.

    /// <summary>A span of one vessel line (section 3.1).</summary>
    internal enum ProfilePhaseKind
    {
        Prelaunch = 0,
        Ascent,
        Circularization,
        Injection,
        Orbit,
        Transfer,
        Flyby,
        Rendezvous,
        Docked,
        Deorbit,
        Descent,
        Reentry,
        Aerocapture,
        Aerobraking,
        AtmosphericFlyby,
        AtmospherePass,
        SurfaceStay,
        Drive,
        Flight,
        SuborbitalHop,
        Eva,
        EvaSurface,
        JetpackFlight,
        Unclassified,
    }

    /// <summary>An instant or a burn inside a phase (section 3.2).</summary>
    internal enum ProfileEventKind
    {
        Launch = 0,
        Liftoff,
        StageSeparation,
        Separation,
        Breakup,
        Burn,
        AtmosphereExit,
        AtmosphereEntry,
        SoiEntry,
        SoiExit,
        Docking,
        Undocking,
        SwitchedTo,
        Eva,
        Board,
        PartPlaced,
        InventoryTransfer,
        ParachutesDeployed,
        Touchdown,
        Splashdown,
        Skip,
        Pass,
        Reflown,
        Recovered,
        End,
    }

    /// <summary>What a burn did (table 4.4).</summary>
    internal enum ProfileBurnKind
    {
        Unknown = 0,
        Circularization,
        Injection,
        Transfer,
        Return,
        Escape,
        Correction,
        Capture,
        CaptureAerobraking,
        PeriapsisRaise,
        OrbitChange,
        PlaneChange,
        Phasing,
        Approach,
        Deorbit,
        Descent,
        Landing,
    }

    /// <summary>How strong the evidence behind a fact is, strongest last (section 4.3).</summary>
    internal enum ProfileEvidenceTier
    {
        None = 0,
        Structural,
        Inferred,
        Stored,
    }

    /// <summary>Orbit energy axis: closed inside the SOI, or open / reaching past it.</summary>
    internal enum ProfileOrbitEnergy
    {
        Unknown = 0,
        Bound,
        Escaping,
    }

    /// <summary>Orbit periapsis axis against the body's floor (section 4.3).</summary>
    internal enum ProfilePeriapsis
    {
        Unknown = 0,
        Clear,
        InAir,
        SubSurface,
    }

    /// <summary>Stock's three orbit kinds, projected from the two axes (section 4.3).</summary>
    internal enum ProfileOrbitKind
    {
        Unknown = 0,
        Falling,
        InOrbit,
        Leaving,
    }

    /// <summary>The coarse class of a phase (F4's seed): where it is flown.</summary>
    internal enum ProfilePhaseClass
    {
        Surface = 0,
        Atmosphere,
        Space,
        Kerbal,
    }

    internal static class ProfileElementKinds
    {
        /// <summary>The coarse class of <paramref name="kind"/>.</summary>
        internal static ProfilePhaseClass ClassOf(ProfilePhaseKind kind)
        {
            switch (kind)
            {
                case ProfilePhaseKind.Prelaunch:
                case ProfilePhaseKind.SurfaceStay:
                case ProfilePhaseKind.Drive:
                    return ProfilePhaseClass.Surface;
                case ProfilePhaseKind.Ascent:
                case ProfilePhaseKind.Reentry:
                case ProfilePhaseKind.Aerocapture:
                case ProfilePhaseKind.Aerobraking:
                case ProfilePhaseKind.AtmosphericFlyby:
                case ProfilePhaseKind.AtmospherePass:
                case ProfilePhaseKind.Flight:
                    return ProfilePhaseClass.Atmosphere;
                case ProfilePhaseKind.Eva:
                case ProfilePhaseKind.EvaSurface:
                case ProfilePhaseKind.JetpackFlight:
                    return ProfilePhaseClass.Kerbal;
                default:
                    return ProfilePhaseClass.Space;
            }
        }
    }
}
