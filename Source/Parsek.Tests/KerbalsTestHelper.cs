using System.Collections.Generic;

namespace Parsek.Tests
{
    /// <summary>
    /// Shared test helper for KerbalsModule lifecycle.
    /// Builds KerbalAssignment actions from RecordingStore and runs the full
    /// IResourceModule lifecycle (Reset → PrePass → ProcessAction → PostWalk).
    /// </summary>
    internal static class KerbalsTestHelper
    {
        /// <summary>
        /// Creates a fresh KerbalsModule and runs the full lifecycle from RecordingStore data.
        /// </summary>
        internal static KerbalsModule RecalculateFromStore()
        {
            return RecalculateModule(new KerbalsModule());
        }

        /// <summary>
        /// Runs the full lifecycle on an existing KerbalsModule instance.
        /// Use when the module has pre-loaded slots via LoadSlots before recalculation.
        /// </summary>
        internal static KerbalsModule RecalculateModule(KerbalsModule module)
        {
            module.Reset();

            var actions = new List<GameAction>();
            var recordings = RecordingStore.CommittedRecordings;
            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (rec.CrewEndStates == null && rec.VesselSnapshot != null)
                    KerbalsModule.PopulateCrewEndStates(rec);

                var snapshot = rec.GhostVisualSnapshot ?? rec.VesselSnapshot;
                if (snapshot == null) continue;
                var names = CrewReservationManager.ExtractCrewFromSnapshot(snapshot);
                for (int j = 0; j < names.Count; j++)
                {
                    KerbalEndState endState = KerbalEndState.Unknown;
                    if (rec.CrewEndStates != null)
                        rec.CrewEndStates.TryGetValue(names[j], out endState);

                    actions.Add(new GameAction
                    {
                        UT = rec.StartUT,
                        Type = GameActionType.KerbalAssignment,
                        RecordingId = rec.RecordingId,
                        KerbalName = names[j],
                        KerbalRole = KerbalsModule.FindTraitForKerbal(names[j]),
                        StartUT = (float)rec.StartUT,
                        EndUT = (float)rec.EndUT,
                        KerbalEndStateField = endState,
                        Sequence = j + 1
                    });
                }
            }

            module.PrePass(actions);
            for (int i = 0; i < actions.Count; i++)
                module.ProcessAction(actions[i]);
            module.PostWalk();
            return module;
        }
    }
}
