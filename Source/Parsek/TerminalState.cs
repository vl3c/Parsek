namespace Parsek
{
    public enum TerminalState
    {
        Orbiting   = 0,
        Landed     = 1,
        Splashed   = 2,
        SubOrbital = 3,
        Destroyed  = 4,
        Recovered  = 5,
        Docked     = 6,
        Boarded    = 7,

        /// <summary>
        /// The vessel ended because its LAST remaining part was pocketed into an
        /// inventory during EVA construction. KSP destroys the vessel on that path
        /// exactly as it does for a crash, but the outcome is the opposite: a
        /// deliberate disassembly, not a loss. Per the 2026-09-02 inventory ruling a
        /// vessel whose core is stored in an inventory HAS ENDED ITS MISSION, and
        /// from then on the part is generic cargo.
        ///
        /// <para>Not a recovery: no funds, science or reputation are involved. The
        /// ledger's recovery correlator (<c>LedgerOrchestrator.AddVesselRecoveryCostActions</c>)
        /// and the resurrection/retirement eligibility walk key strictly on
        /// <see cref="Recovered"/>, so this value cannot reach either.</para>
        ///
        /// <para>Purely additive: appending an enum member renames no key, adds no
        /// field and changes no binary layout, so it is NOT a schema change and
        /// <c>RecordingStore.CurrentRecordingSchemaGeneration</c> stays 4 (operator
        /// ruling, 2026-09-06).</para>
        /// </summary>
        Disassembled = 8
    }
}
