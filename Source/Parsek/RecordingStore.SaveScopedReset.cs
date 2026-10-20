using System;

namespace Parsek
{
    public static partial class RecordingStore
    {
        /// <summary>The tree id of the live committed-tree restore attempt, or null.</summary>
        internal static string CommittedTreeRestoreAttemptTreeId => committedTreeRestoreAttemptTreeId;
    }
}
