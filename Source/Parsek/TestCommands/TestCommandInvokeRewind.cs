using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The two-phase <c>InvokeRewind</c> completion outcome (M-C1,
    /// design-autotest-seam-verbs-c1.md). The re-fly reload straddles a KSP scene
    /// reload driven by <c>OnLoad</c>, so the terminal OK payload (the marker session
    /// id, the active pid) is not known until <c>ConsumePostLoad</c> writes the marker.
    /// Modeled on <see cref="LoadCompletionDecision"/>.
    /// </summary>
    internal enum RewindCompletionDecision
    {
        /// <summary>Pre-load or mid-load straddle: keep holding the FIFO head.</summary>
        StillWaiting,

        /// <summary>A fresh re-fly marker exists (ConsumePostLoad ran): terminal OK.</summary>
        CompleteOk,

        /// <summary>The invoke context cleared but no marker landed (StartInvoke's LoadGame
        /// returned null, or ConsumePostLoad aborted): terminal ERROR (rewind-failed).</summary>
        RewindFailed,

        /// <summary>The reload never settled within the budget: terminal ERROR
        /// (rewind-timeout).</summary>
        RewindTimeout,
    }

    /// <summary>
    /// Pure decision + payload helpers for the two-phase <c>InvokeRewind</c> seam verb
    /// (M-C1). The Unity applier on the addon resolves the RewindPoint / slot, routes
    /// through the real <c>RewindInvoker.CanInvoke</c> gate, calls
    /// <c>RewindInvoker.StartInvoke</c>, and samples the live invoke-context / re-fly
    /// marker; every decision it makes is factored here so it is xUnit-covered without a
    /// live KSP scene reload.
    /// </summary>
    internal static class TestCommandInvokeRewind
    {
        /// <summary>
        /// Decide the two-phase InvokeRewind completion. The addon polls this only at
        /// SETTLED scenes (the pump gates off during LOADING / transition / settle).
        ///
        /// <para>Order (mirroring <see cref="TestCommandLoadGame.DecideLoadCompletion"/>):
        /// a fresh marker is the success (RewindInvoker.CanInvoke refuses when a marker
        /// already exists, so a marker appearing AFTER StartInvoke is unambiguously this
        /// command's) and wins even past the budget; the budget expiry is then checked
        /// UNCONDITIONALLY (before the still-pending straddle) so a reload that aborts
        /// without ConsumePostLoad - leaving the invoke context Pending forever - still
        /// terminates as RewindTimeout instead of holding the FIFO head indefinitely; a
        /// still-pending context within budget keeps waiting; a cleared context with no
        /// marker is the fast failure. The earlier ordering short-circuited on
        /// contextPending FIRST, which made RewindTimeout unreachable in its own documented
        /// case (the reload never settled within the budget).</para>
        /// </summary>
        internal static RewindCompletionDecision DecideRewindCompletion(
            double elapsedSeconds, bool contextPending, bool markerPresent, double budgetSeconds)
        {
            if (markerPresent) return RewindCompletionDecision.CompleteOk;
            if (elapsedSeconds >= budgetSeconds) return RewindCompletionDecision.RewindTimeout;
            if (contextPending) return RewindCompletionDecision.StillWaiting;
            return RewindCompletionDecision.RewindFailed;
        }

        /// <summary>The gate-decline <c>msg</c>: the real <c>RewindInvoker.CanInvoke</c>
        /// reason surfaced VERBATIM behind a stable <c>refly-gate</c> prefix so the
        /// orchestrator sees WHY, not a bare failure.</summary>
        internal static string GateRefusalMsg(string reason)
            => "refly-gate " + (reason ?? string.Empty);

        /// <summary>Terminal OK payload once the new scene settles with a fresh re-fly
        /// marker. <paramref name="activePid"/> is diagnostic (the selected slot's live
        /// root-part pid).</summary>
        internal static List<KeyValuePair<string, string>> BuildCompletePayload(
            string session, string rp, string slot, uint activePid)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("rewound", "true"),
                new KeyValuePair<string, string>("session", session ?? string.Empty),
                new KeyValuePair<string, string>("rp", rp ?? string.Empty),
                new KeyValuePair<string, string>("slot", slot ?? string.Empty),
                new KeyValuePair<string, string>("activePid", activePid.ToString(CultureInfo.InvariantCulture)),
            };
    }
}
