using System.Collections.Generic;

namespace Parsek.Tests
{
    internal static class ScienceTestHelpers
    {
        /// <summary>
        /// Test helper that mirrors pending subjects into the committed-science cache
        /// through the live production merge path.
        /// </summary>
        internal static void CommitScienceSubjects(IReadOnlyList<PendingScienceSubject> pending)
        {
            if (pending == null)
            {
                GameStateStore.CommitScienceActions(null);
                return;
            }

            var actions = new List<GameAction>(pending.Count);
            for (int i = 0; i < pending.Count; i++)
            {
                var subject = pending[i];
                actions.Add(new GameAction
                {
                    Type = GameActionType.ScienceEarning,
                    SubjectId = subject.subjectId,
                    ScienceAwarded = subject.science,
                    SubjectMaxValue = subject.subjectMaxValue,
                    RecordingId = subject.recordingId
                });
            }

            GameStateStore.CommitScienceActions(actions);
        }
    }
}
