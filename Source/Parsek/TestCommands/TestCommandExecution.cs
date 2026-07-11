namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure execution-outcome decisions for the ParsekTestCommands pump (M-A2). The addon
    /// side (a MonoBehaviour) invokes a verb handler and then acts on these decisions; kept
    /// <c>internal static</c> so the exception-containment and queue-advance rules are
    /// xUnit-covered without a live Unity runtime.
    /// </summary>
    internal static class TestCommandExecution
    {
        /// <summary>The two-phase sentinel verdict: the side effect was INITIATED and the
        /// terminal response is deferred until the operation settles. It is the ONLY verdict
        /// that holds the FIFO head instead of advancing it.</summary>
        internal const string PendingVerdict = "__PENDING__";

        /// <summary>
        /// Maps a caught executor exception to the terminal response it becomes so the id
        /// completes and the pump advances rather than the whole pump wedging on an unhandled
        /// throw. The verdict is always <c>ERROR</c>; <paramref name="msg"/> carries the
        /// exception type name (the response formatter percent-encodes it on the wire).
        /// </summary>
        internal static void ExceptionTerminal(string exceptionTypeName, out string verdict, out string msg)
        {
            verdict = "ERROR";
            msg = "exception=" + (string.IsNullOrEmpty(exceptionTypeName) ? "Exception" : exceptionTypeName);
        }

        /// <summary>
        /// True when a produced verdict is terminal and therefore advances the strict-FIFO
        /// head. Only the two-phase <see cref="PendingVerdict"/> sentinel holds the head; every
        /// real verdict (OK / ERROR / REJECTED / TIMEOUT / INTERRUPTED) advances.
        /// </summary>
        internal static bool AdvancesQueue(string verdict) => verdict != PendingVerdict;
    }
}
